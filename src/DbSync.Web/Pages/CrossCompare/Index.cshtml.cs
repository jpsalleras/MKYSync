using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;
using System.Security.Claims;

namespace DbSync.Web.Pages.CrossCompare;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CentralRepository _centralRepo;
    private readonly CrossClientComparer _comparer;
    private readonly UserClientService _userClientService;
    private readonly CredentialEncryptor _encryptor;
    private readonly ScriptGenerator _scriptGenerator;
    private readonly SyncExecutor _syncExecutor;
    private readonly NotificationService _notificationService;
    private readonly MergeSuggestionService _mergeService;

    public IndexModel(
        AppDbContext db,
        CentralRepository centralRepo,
        CrossClientComparer comparer,
        UserClientService userClientService,
        CredentialEncryptor encryptor,
        ScriptGenerator scriptGenerator,
        SyncExecutor syncExecutor,
        NotificationService notificationService,
        MergeSuggestionService mergeService)
    {
        _db = db;
        _centralRepo = centralRepo;
        _comparer = comparer;
        _userClientService = userClientService;
        _encryptor = encryptor;
        _scriptGenerator = scriptGenerator;
        _syncExecutor = syncExecutor;
        _notificationService = notificationService;
        _mergeService = mergeService;
    }

    public List<SelectListItem> ClientesList { get; set; } = new();
    public CrossComparisonResult? Result { get; set; }
    public string? ErrorMessage { get; set; }

    // --- Merge IA ---
    public bool MergeEnabled => _mergeService.IsEnabled;

    [BindProperty(SupportsGet = true)]
    public int? ClienteIdA { get; set; }

    [BindProperty(SupportsGet = true)]
    public string AmbienteA { get; set; } = "PR";

    [BindProperty(SupportsGet = true)]
    public int? ClienteIdB { get; set; }

    [BindProperty(SupportsGet = true)]
    public string AmbienteB { get; set; } = "PR";

    [BindProperty(SupportsGet = true)]
    public string? Tipo { get; set; }

    // --- Sync ---
    public bool CanSync { get; set; }
    public bool CanExecute { get; set; }
    public bool MostrarPreviewSync { get; set; }
    public bool MostrarResultadosSync { get; set; }
    public string? ScriptPreview { get; set; }
    public string? SyncErrorMessage { get; set; }
    public SyncPreviewInfo? PreviewSummary { get; set; }
    public List<SyncResult> SyncResults { get; set; } = new();
    public bool IncludesDrops { get; set; }
    public bool IncludesCustom { get; set; }

    [BindProperty]
    public List<string> SelectedObjects { get; set; } = new();

    public async Task OnGetAsync()
    {
        await RunCrossComparisonAsync();
        CanSync = User.IsInRole("Admin") || User.IsInRole("Ejecutar") || User.IsInRole("DBA");
        CanExecute = User.IsInRole("Admin") || User.IsInRole("Ejecutar");
    }

    public async Task<IActionResult> OnGetDiffAsync(long snapshotIdA, long snapshotIdB)
    {
        try
        {
            var diff = await _comparer.GetDiffAsync(snapshotIdA, snapshotIdB);
            if (diff == null)
                return new JsonResult(new { error = "No se encontraron definiciones" });

            return new JsonResult(new
            {
                html = diff.DiffHtml,
                linesAdded = diff.LinesAdded,
                linesRemoved = diff.LinesRemoved
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message });
        }
    }

    public async Task<IActionResult> OnPostSuggestMergeAsync(
        [FromForm] long snapshotIdA,
        [FromForm] long snapshotIdB,
        [FromForm] string objectName,
        [FromForm] string labelA,
        [FromForm] string labelB)
    {
        try
        {
            var defATask = _centralRepo.GetSnapshotDefinitionAsync(snapshotIdA);
            var defBTask = _centralRepo.GetSnapshotDefinitionAsync(snapshotIdB);
            await Task.WhenAll(defATask, defBTask);

            var defA = defATask.Result;
            var defB = defBTask.Result;

            if (defA == null || defB == null)
                return new JsonResult(new { error = "No se pudieron obtener ambas definiciones del objeto." });

            var result = await _mergeService.SuggestMergeAsync(
                objectName, defA, defB, labelA, labelB);

            if (!result.Success)
                return new JsonResult(new { error = result.ErrorMessage });

            return new JsonResult(new
            {
                mergedSql = result.MergedDefinition,
                explanation = result.Explanation,
                model = result.Model,
                inputTokens = result.InputTokens,
                outputTokens = result.OutputTokens
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = $"Error: {ex.Message}" });
        }
    }

    public async Task<IActionResult> OnPostPreviewSyncAsync()
    {
        if (!User.IsInRole("Admin") && !User.IsInRole("Ejecutar") && !User.IsInRole("DBA"))
            return Forbid();

        CanSync = true;
        CanExecute = User.IsInRole("Admin") || User.IsInRole("Ejecutar");
        await RunCrossComparisonAsync();

        if (Result == null || SelectedObjects.Count == 0)
        {
            SyncErrorMessage = "No se seleccionaron objetos para sincronizar.";
            return Page();
        }

        var selectedSet = SelectedObjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedItems = Result.Items
            .Where(i => selectedSet.Contains(i.ObjectFullName) && i.Status != CompareStatus.Equal)
            .ToList();

        if (selectedItems.Count == 0)
        {
            SyncErrorMessage = "Ninguno de los objetos seleccionados requiere sincronización.";
            return Page();
        }

        try
        {
            var compareResults = await BuildCompareResultsFromSnapshotsAsync(selectedItems);

            IncludesDrops = compareResults.Any(r => r.Status == CompareStatus.OnlyInTarget);
            IncludesCustom = compareResults.Any(r => r.IsCustom);

            ScriptPreview = _scriptGenerator.GenerateBatchSyncScript(compareResults, wrapInTransaction: true);

            PreviewSummary = new SyncPreviewInfo
            {
                TotalCreate = compareResults.Count(r => r.Status == CompareStatus.OnlyInSource),
                TotalAlter = compareResults.Count(r => r.Status == CompareStatus.Modified),
                TotalDrop = compareResults.Count(r => r.Status == CompareStatus.OnlyInTarget),
                TotalCustom = compareResults.Count(r => r.IsCustom),
                Items = compareResults.Select(r => new SyncPreviewItem
                {
                    Name = r.ObjectFullName,
                    Action = r.Status switch
                    {
                        CompareStatus.OnlyInSource => "CREATE",
                        CompareStatus.Modified => "ALTER",
                        CompareStatus.OnlyInTarget => "DROP",
                        _ => "NONE"
                    },
                    IsCustom = r.IsCustom
                }).ToList()
            };

            MostrarPreviewSync = true;
        }
        catch (Exception ex)
        {
            SyncErrorMessage = $"Error generando preview: {ex.Message}";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostExecuteSyncAsync()
    {
        if (!User.IsInRole("Admin") && !User.IsInRole("Ejecutar"))
            return Forbid();

        CanSync = true;
        CanExecute = true;
        await RunCrossComparisonAsync();

        if (Result == null || SelectedObjects.Count == 0)
        {
            SyncErrorMessage = "Error: no se pudieron obtener los objetos para sincronizar.";
            return Page();
        }

        var selectedSet = SelectedObjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedItems = Result.Items
            .Where(i => selectedSet.Contains(i.ObjectFullName) && i.Status != CompareStatus.Equal)
            .ToList();

        if (selectedItems.Count == 0)
        {
            SyncErrorMessage = "Los objetos seleccionados ya están sincronizados.";
            return Page();
        }

        try
        {
            var compareResults = await BuildCompareResultsFromSnapshotsAsync(selectedItems);

            var clienteB = await _db.Clientes
                .Include(c => c.Ambientes)
                .FirstOrDefaultAsync(c => c.Id == ClienteIdB!.Value);

            if (clienteB == null)
            {
                SyncErrorMessage = "Cliente destino no encontrado.";
                return Page();
            }

            var ambDestino = Enum.Parse<Ambiente>(AmbienteB);
            var ambOrigen = Enum.Parse<Ambiente>(AmbienteA);

            var ambDestinoConfig = clienteB.Ambientes.FirstOrDefault(a => a.Ambiente == ambDestino);
            if (ambDestinoConfig == null)
            {
                SyncErrorMessage = $"El cliente destino no tiene configurado el ambiente {AmbienteB}";
                return Page();
            }

            var csDestino = ambDestinoConfig.GetConnectionString(_encryptor.Decrypt);
            var usuario = User.Identity?.Name;

            SyncResults = await _syncExecutor.ExecuteBatchTransactionalAsync(
                compareResults, csDestino,
                ClienteIdB!.Value, ambOrigen, ambDestino,
                usuario);

            MostrarResultadosSync = true;

            if (ambDestinoConfig.NotificarCambios && SyncResults.Any(r => r.Success))
            {
                await _notificationService.SendSyncNotificationToClientUsersAsync(
                    ClienteIdB!.Value, clienteB.Codigo, ambOrigen, ambDestino,
                    SyncResults, usuario);
            }
        }
        catch (Exception ex)
        {
            SyncErrorMessage = $"Error al ejecutar sincronización: {ex.Message}";
        }

        return Page();
    }

    private async Task RunCrossComparisonAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var isAdmin = User.IsInRole("Admin");

        ClientesList = await _userClientService.GetClientesSelectListAsync(userId, isAdmin);

        if (!ClienteIdA.HasValue || ClienteIdA <= 0 || !ClienteIdB.HasValue || ClienteIdB <= 0)
            return;

        if (!await _userClientService.UserHasAccessToClienteAsync(userId, isAdmin, ClienteIdA.Value) ||
            !await _userClientService.UserHasAccessToClienteAsync(userId, isAdmin, ClienteIdB.Value))
        {
            ErrorMessage = "No tiene acceso a uno de los clientes seleccionados";
            return;
        }

        try
        {
            var ambA = Enum.Parse<Ambiente>(AmbienteA);
            var ambB = Enum.Parse<Ambiente>(AmbienteB);

            string? filterType = Tipo switch
            {
                "SP" => "P",
                "VIEW" => "V",
                "FN" => "FN",
                _ => null
            };

            Result = await _comparer.CompareAsync(
                ClienteIdA.Value, ambA,
                ClienteIdB.Value, ambB,
                filterType);

            if (Result.TotalSnapshotsA == 0)
                ErrorMessage = $"No hay snapshots para el cliente A en ambiente {AmbienteA}. Ejecute un scan primero.";
            else if (Result.TotalSnapshotsB == 0)
                ErrorMessage = $"No hay snapshots para el cliente B en ambiente {AmbienteB}. Ejecute un scan primero.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al comparar: {ex.Message}";
        }
    }

    private async Task<List<CompareResult>> BuildCompareResultsFromSnapshotsAsync(
        List<CrossCompareItem> selectedItems)
    {
        var results = new List<CompareResult>();

        foreach (var item in selectedItems)
        {
            var sourceDefTask = item.SnapshotIdA.HasValue
                ? _centralRepo.GetSnapshotDefinitionAsync(item.SnapshotIdA.Value)
                : Task.FromResult<string?>(null);

            var targetDefTask = item.SnapshotIdB.HasValue
                ? _centralRepo.GetSnapshotDefinitionAsync(item.SnapshotIdB.Value)
                : Task.FromResult<string?>(null);

            await Task.WhenAll(sourceDefTask, targetDefTask);

            var sourceDef = sourceDefTask.Result;
            var targetDef = targetDefTask.Result;

            DbObjectType objectType;
            try { objectType = DbObjectTypeExtensions.FromSqlType(item.ObjectType); }
            catch { objectType = DbObjectType.StoredProcedure; }

            var parts = item.ObjectFullName.Split('.', 2);
            var schema = parts.Length > 1 ? parts[0] : "dbo";
            var name = parts.Length > 1 ? parts[1] : parts[0];

            results.Add(new CompareResult
            {
                ObjectFullName = item.ObjectFullName,
                ObjectType = objectType,
                Status = item.Status,
                IsCustom = item.IsCustomA || item.IsCustomB,
                Source = sourceDef != null ? new DbObject
                {
                    SchemaName = schema,
                    ObjectName = name,
                    ObjectType = objectType,
                    Definition = sourceDef
                } : null,
                Target = targetDef != null ? new DbObject
                {
                    SchemaName = schema,
                    ObjectName = name,
                    ObjectType = objectType,
                    Definition = targetDef
                } : null
            });
        }

        return results;
    }

    public class SyncPreviewInfo
    {
        public int TotalCreate { get; set; }
        public int TotalAlter { get; set; }
        public int TotalDrop { get; set; }
        public int TotalCustom { get; set; }
        public int Total => TotalCreate + TotalAlter + TotalDrop;
        public List<SyncPreviewItem> Items { get; set; } = new();
    }

    public class SyncPreviewItem
    {
        public string Name { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public bool IsCustom { get; set; }
    }
}
