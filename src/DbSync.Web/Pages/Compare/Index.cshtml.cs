using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;
using System.Security.Claims;

namespace DbSync.Web.Pages.Compare;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DbComparer _comparer;
    private readonly CredentialEncryptor _encryptor;
    private readonly UserClientService _userClientService;
    private readonly ScriptGenerator _scriptGenerator;
    private readonly SyncExecutor _syncExecutor;
    private readonly NotificationService _notificationService;
    private readonly CentralRepository _centralRepo;
    private readonly MergeSuggestionService _mergeService;

    public IndexModel(
        AppDbContext db,
        DbComparer comparer,
        CredentialEncryptor encryptor,
        UserClientService userClientService,
        ScriptGenerator scriptGenerator,
        SyncExecutor syncExecutor,
        NotificationService notificationService,
        CentralRepository centralRepo,
        MergeSuggestionService mergeService)
    {
        _db = db;
        _comparer = comparer;
        _encryptor = encryptor;
        _userClientService = userClientService;
        _scriptGenerator = scriptGenerator;
        _syncExecutor = syncExecutor;
        _notificationService = notificationService;
        _centralRepo = centralRepo;
        _mergeService = mergeService;
    }

    public List<SelectListItem> ClientesList { get; set; } = new();
    public ComparisonSummary? Summary { get; set; }
    public string? ErrorMessage { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? ClienteId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Origen { get; set; } = "DEV";

    [BindProperty(SupportsGet = true)]
    public string Destino { get; set; } = "QA";

    [BindProperty(SupportsGet = true)]
    public string? Tipo { get; set; }

    // --- Version mode ---
    [BindProperty(SupportsGet = true)]
    public string Modo { get; set; } = "ambiente";

    [BindProperty(SupportsGet = true)]
    public int? VersionId { get; set; }

    public List<SelectListItem> VersionesList { get; set; } = new();
    public BaseVersion? SelectedVersion { get; set; }
    public bool IsVersionMode => Modo == "version";
    public string OrigenLabel => IsVersionMode && SelectedVersion != null
        ? $"v{SelectedVersion.VersionName}" : Origen;

    // --- Merge IA ---
    public bool MergeEnabled => _mergeService.IsEnabled;

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
        await RunComparisonAsync();
        CanSync = User.IsInRole("Admin") || User.IsInRole("Ejecutar") || User.IsInRole("DBA");
        CanExecute = User.IsInRole("Admin") || User.IsInRole("Ejecutar");
    }

    public async Task<IActionResult> OnPostPreviewSyncAsync()
    {
        if (!User.IsInRole("Admin") && !User.IsInRole("Ejecutar") && !User.IsInRole("DBA"))
            return Forbid();

        CanSync = true;
        CanExecute = User.IsInRole("Admin") || User.IsInRole("Ejecutar");
        await RunComparisonAsync();

        if (Summary == null || SelectedObjects.Count == 0)
        {
            SyncErrorMessage = "No se seleccionaron objetos para sincronizar.";
            return Page();
        }

        var selectedSet = SelectedObjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedResults = Summary.Results
            .Where(r => selectedSet.Contains(r.ObjectFullName) && r.Status != CompareStatus.Equal)
            .ToList();

        if (selectedResults.Count == 0)
        {
            SyncErrorMessage = "Ninguno de los objetos seleccionados requiere sincronización.";
            return Page();
        }

        IncludesDrops = selectedResults.Any(r => r.Status == CompareStatus.OnlyInTarget);
        IncludesCustom = selectedResults.Any(r => r.IsCustom);

        ScriptPreview = _scriptGenerator.GenerateBatchSyncScript(selectedResults, wrapInTransaction: true);

        PreviewSummary = new SyncPreviewInfo
        {
            TotalCreate = selectedResults.Count(r => r.Status == CompareStatus.OnlyInSource),
            TotalAlter = selectedResults.Count(r => r.Status == CompareStatus.Modified),
            TotalDrop = selectedResults.Count(r => r.Status == CompareStatus.OnlyInTarget),
            TotalCustom = selectedResults.Count(r => r.IsCustom),
            Items = selectedResults.Select(r => new SyncPreviewItem
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
        return Page();
    }

    public async Task<IActionResult> OnPostExecuteSyncAsync()
    {
        if (!User.IsInRole("Admin") && !User.IsInRole("Ejecutar"))
            return Forbid();

        CanSync = true;
        CanExecute = true;
        await RunComparisonAsync();

        if (Summary == null || SelectedObjects.Count == 0)
        {
            SyncErrorMessage = "Error: no se pudieron obtener los objetos para sincronizar.";
            return Page();
        }

        var selectedSet = SelectedObjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedResults = Summary.Results
            .Where(r => selectedSet.Contains(r.ObjectFullName) && r.Status != CompareStatus.Equal)
            .ToList();

        if (selectedResults.Count == 0)
        {
            SyncErrorMessage = "Los objetos seleccionados ya están sincronizados.";
            return Page();
        }

        try
        {
            var cliente = await _db.Clientes
                .Include(c => c.Ambientes)
                .FirstOrDefaultAsync(c => c.Id == ClienteId!.Value);

            var ambDestino = Enum.Parse<Ambiente>(Destino);

            // En modo version, usar el ambiente origen de la version para el log
            Ambiente ambOrigen;
            if (IsVersionMode && SelectedVersion != null)
            {
                ambOrigen = Enum.Parse<Ambiente>(SelectedVersion.SourceAmbiente);
            }
            else if (IsVersionMode && VersionId.HasValue)
            {
                var version = await _centralRepo.GetBaseVersionAsync(VersionId.Value);
                ambOrigen = version != null
                    ? Enum.Parse<Ambiente>(version.SourceAmbiente)
                    : Ambiente.PR;
            }
            else
            {
                ambOrigen = Enum.Parse<Ambiente>(Origen);
            }

            var csDestino = cliente!.Ambientes
                .First(a => a.Ambiente == ambDestino)
                .GetConnectionString(_encryptor.Decrypt);

            var usuario = User.Identity?.Name;

            SyncResults = await _syncExecutor.ExecuteBatchTransactionalAsync(
                selectedResults, csDestino,
                ClienteId!.Value, ambOrigen, ambDestino,
                usuario);

            MostrarResultadosSync = true;

            // Notificar a usuarios si el ambiente destino tiene notificaciones habilitadas
            var ambDestinoConfig = cliente!.Ambientes.First(a => a.Ambiente == ambDestino);
            if (ambDestinoConfig.NotificarCambios && SyncResults.Any(r => r.Success))
            {
                await _notificationService.SendSyncNotificationToClientUsersAsync(
                    ClienteId!.Value, cliente.Codigo, ambOrigen, ambDestino,
                    SyncResults, usuario);
            }
        }
        catch (Exception ex)
        {
            SyncErrorMessage = $"Error al ejecutar sincronización: {ex.Message}";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSuggestMergeAsync(
        [FromForm] int clienteId,
        [FromForm] string origen,
        [FromForm] string destino,
        [FromForm] string objectName,
        [FromForm] string? modo,
        [FromForm] int? versionId)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            var isAdmin = User.IsInRole("Admin");

            if (!await _userClientService.UserHasAccessToClienteAsync(userId, isAdmin, clienteId))
                return new JsonResult(new { error = "No tiene acceso a este cliente" });

            var cliente = await _db.Clientes
                .Include(c => c.Ambientes)
                .FirstOrDefaultAsync(c => c.Id == clienteId);

            if (cliente == null)
                return new JsonResult(new { error = "Cliente no encontrado" });

            var extractor = HttpContext.RequestServices.GetRequiredService<DbObjectExtractor>();
            var parts = objectName.Split('.', 2);
            var schema = parts.Length > 1 ? parts[0] : "dbo";
            var name = parts.Length > 1 ? parts[1] : parts[0];

            string? sourceDef, targetDef;
            string origenLabel, destinoLabel;

            if (modo == "version" && versionId.HasValue)
            {
                // Source desde version base
                var versionObjects = await _centralRepo.GetBaseVersionObjectsWithDefinitionsAsync(versionId.Value);
                var vObj = versionObjects.FirstOrDefault(v =>
                    v.Object.ObjectFullName.Equals(objectName, StringComparison.OrdinalIgnoreCase));
                sourceDef = vObj.Definition;

                var version = await _centralRepo.GetBaseVersionAsync(versionId.Value);
                origenLabel = version != null ? $"v{version.VersionName}" : origen;

                // Target desde ambiente live
                var ambDestino = Enum.Parse<Ambiente>(destino);
                var csDestino = cliente.Ambientes.First(a => a.Ambiente == ambDestino)
                    .GetConnectionString(_encryptor.Decrypt);
                var target = await extractor.ExtractSingleAsync(csDestino, schema, name);
                targetDef = target?.Definition;
                destinoLabel = destino;
            }
            else
            {
                // Modo ambiente: ambos desde SQL live
                var ambOrigen = Enum.Parse<Ambiente>(origen);
                var ambDestino = Enum.Parse<Ambiente>(destino);

                var csOrigen = cliente.Ambientes.First(a => a.Ambiente == ambOrigen)
                    .GetConnectionString(_encryptor.Decrypt);
                var csDestino = cliente.Ambientes.First(a => a.Ambiente == ambDestino)
                    .GetConnectionString(_encryptor.Decrypt);

                var sourceTask = extractor.ExtractSingleAsync(csOrigen, schema, name);
                var targetTask = extractor.ExtractSingleAsync(csDestino, schema, name);
                await Task.WhenAll(sourceTask, targetTask);

                sourceDef = sourceTask.Result?.Definition;
                targetDef = targetTask.Result?.Definition;
                origenLabel = origen;
                destinoLabel = destino;
            }

            if (sourceDef == null || targetDef == null)
                return new JsonResult(new { error = "No se pudieron obtener ambas definiciones del objeto." });

            var result = await _mergeService.SuggestMergeAsync(
                objectName, sourceDef, targetDef,
                origenLabel, destinoLabel);

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

    private async Task RunComparisonAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var isAdmin = User.IsInRole("Admin");

        ClientesList = await _userClientService.GetClientesSelectListAsync(userId, isAdmin, ClienteId);

        // Cargar versiones disponibles (para el selector)
        try
        {
            var versions = await _centralRepo.GetAllBaseVersionsAsync();
            VersionesList = versions.Select(v => new SelectListItem(
                v.VersionName, v.Id.ToString(),
                v.Id == VersionId)).ToList();
        }
        catch { /* Si falla la carga de versiones, continuar sin ellas */ }

        if (IsVersionMode)
        {
            await RunVersionComparisonAsync(userId, isAdmin);
        }
        else
        {
            await RunAmbienteComparisonAsync(userId, isAdmin);
        }
    }

    private async Task RunAmbienteComparisonAsync(string userId, bool isAdmin)
    {
        if (!ClienteId.HasValue || ClienteId <= 0) return;

        if (!await _userClientService.UserHasAccessToClienteAsync(userId, isAdmin, ClienteId.Value))
        {
            ErrorMessage = "No tiene acceso a este cliente";
            return;
        }

        try
        {
            var cliente = await _db.Clientes
                .Include(c => c.Ambientes)
                .Include(c => c.ObjetosCustom)
                .FirstOrDefaultAsync(c => c.Id == ClienteId.Value);

            if (cliente == null)
            {
                ErrorMessage = "Cliente no encontrado";
                return;
            }

            var ambOrigen = Enum.Parse<Ambiente>(Origen);
            var ambDestino = Enum.Parse<Ambiente>(Destino);

            if (!cliente.Ambientes.Any(a => a.Ambiente == ambOrigen))
            {
                ErrorMessage = $"El cliente no tiene configurado el ambiente {Origen}";
                return;
            }
            if (!cliente.Ambientes.Any(a => a.Ambiente == ambDestino))
            {
                ErrorMessage = $"El cliente no tiene configurado el ambiente {Destino}";
                return;
            }

            Summary = await _comparer.CompareAsync(cliente, ambOrigen, ambDestino, _encryptor.Decrypt);

            // Refrescar snapshots con los datos obtenidos (best effort)
            await RefreshSnapshotsFromComparisonAsync(cliente, ambOrigen, ambDestino);

            if (!string.IsNullOrEmpty(Tipo))
            {
                Summary.Results = Summary.Results
                    .Where(r => r.ObjectType.ToShortCode() == Tipo)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al comparar: {ex.Message}";
        }
    }

    private async Task RunVersionComparisonAsync(string userId, bool isAdmin)
    {
        if (!VersionId.HasValue || !ClienteId.HasValue || ClienteId <= 0) return;

        if (!await _userClientService.UserHasAccessToClienteAsync(userId, isAdmin, ClienteId.Value))
        {
            ErrorMessage = "No tiene acceso a este cliente";
            return;
        }

        try
        {
            var version = await _centralRepo.GetBaseVersionAsync(VersionId.Value);
            if (version == null)
            {
                ErrorMessage = "Version no encontrada";
                return;
            }
            SelectedVersion = version;

            var cliente = await _db.Clientes
                .Include(c => c.Ambientes)
                .Include(c => c.ObjetosCustom)
                .FirstOrDefaultAsync(c => c.Id == ClienteId.Value);

            if (cliente == null)
            {
                ErrorMessage = "Cliente no encontrado";
                return;
            }

            var ambDestino = Enum.Parse<Ambiente>(Destino);
            if (!cliente.Ambientes.Any(a => a.Ambiente == ambDestino))
            {
                ErrorMessage = $"El cliente no tiene configurado el ambiente {Destino}";
                return;
            }

            // 1. Cargar objetos de la version con definiciones
            var versionObjects = await _centralRepo.GetBaseVersionObjectsWithDefinitionsAsync(VersionId.Value);

            // 2. Extraer objetos live del destino
            var csDestino = cliente.Ambientes.First(a => a.Ambiente == ambDestino)
                .GetConnectionString(_encryptor.Decrypt);
            var extractor = HttpContext.RequestServices.GetRequiredService<DbObjectExtractor>();
            var targetList = await extractor.ExtractAllAsync(csDestino);
            var targetObjects = targetList.ToDictionary(o => o.FullName, StringComparer.OrdinalIgnoreCase);

            // 3. Construir diccionario de source desde version
            var sourceObjects = new Dictionary<string, DbObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var (vObj, def) in versionObjects)
            {
                DbObjectType objectType;
                try { objectType = DbObjectTypeExtensions.FromSqlType(vObj.ObjectType); }
                catch { objectType = DbObjectType.StoredProcedure; }

                sourceObjects[vObj.ObjectFullName] = new DbObject
                {
                    SchemaName = vObj.SchemaName,
                    ObjectName = vObj.ObjectName,
                    ObjectType = objectType,
                    Definition = def
                };
            }

            // 4. Comparar usando el metodo reutilizable
            var customObjects = cliente.ObjetosCustom
                .Select(c => c.NombreObjeto)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Summary = _comparer.CompareFromDictionaries(
                sourceObjects, targetObjects, customObjects, cliente.Codigo);

            Summary.ClienteId = cliente.Id;
            Summary.ClienteNombre = cliente.Nombre;
            Summary.AmbienteDestino = ambDestino;

            if (!string.IsNullOrEmpty(Tipo))
            {
                Summary.Results = Summary.Results
                    .Where(r => r.ObjectType.ToShortCode() == Tipo)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al comparar: {ex.Message}";
        }
    }

    /// <summary>
    /// Refresca los snapshots en el repositorio central con los datos obtenidos en la comparación.
    /// Esto mantiene los snapshots actualizados sin esperar al próximo scan del worker.
    /// Solo incluye objetos que el scanner incluiría (ObjetosBase + custom).
    /// </summary>
    private async Task RefreshSnapshotsFromComparisonAsync(Cliente cliente, Ambiente ambOrigen, Ambiente ambDestino)
    {
        if (Summary == null || Summary.Results.Count == 0) return;

        try
        {
            // Cargar filtro de objetos base (misma lógica que el scanner)
            var baseList = await _db.ObjetosBase
                .Where(o => o.ClienteId == null || o.ClienteId == cliente.Id)
                .Select(o => o.NombreObjeto)
                .ToListAsync();

            // Si no hay ObjetosBase configurados, incluir todo (igual que scanner en modo scanAll)
            HashSet<string>? baseFilter = baseList.Count > 0
                ? baseList.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;

            var now = DateTime.UtcNow;
            var scanLog = new ScanLog
            {
                StartedAt = now,
                Status = ScanStatus.Completed,
                Trigger = ScanTrigger.Compare,
                TriggeredBy = User.Identity?.Name,
                TotalClientes = 1,
                TotalAmbientes = 2
            };
            await _centralRepo.CreateScanLogAsync(scanLog);

            bool ShouldInclude(CompareResult r) =>
                baseFilter == null || baseFilter.Contains(r.ObjectFullName) || r.IsCustom;

            int totalObjects = 0;

            // Snapshots del ambiente origen
            var (srcSnaps, srcDefs) = BuildSnapshots(scanLog.Id, cliente, ambOrigen,
                Summary.Results.Where(r => r.Source != null && ShouldInclude(r)),
                r => r.Source!, now);
            if (srcSnaps.Count > 0)
                await _centralRepo.BulkInsertSnapshotsAsync(scanLog.Id, srcSnaps, srcDefs);
            totalObjects += srcSnaps.Count;

            // Snapshots del ambiente destino
            var (tgtSnaps, tgtDefs) = BuildSnapshots(scanLog.Id, cliente, ambDestino,
                Summary.Results.Where(r => r.Target != null && ShouldInclude(r)),
                r => r.Target!, now);
            if (tgtSnaps.Count > 0)
                await _centralRepo.BulkInsertSnapshotsAsync(scanLog.Id, tgtSnaps, tgtDefs);
            totalObjects += tgtSnaps.Count;

            scanLog.CompletedAt = DateTime.UtcNow;
            scanLog.TotalObjectsScanned = totalObjects;
            await _centralRepo.UpdateScanLogAsync(scanLog);
        }
        catch
        {
            // Best effort — no interrumpir la comparación si falla el refresh de snapshots
        }
    }

    private static (List<ObjectSnapshot> Snapshots, List<string> Definitions) BuildSnapshots(
        int scanLogId, Cliente cliente, Ambiente ambiente,
        IEnumerable<CompareResult> results, Func<CompareResult, DbObject> getObj, DateTime now)
    {
        var snapshots = new List<ObjectSnapshot>();
        var definitions = new List<string>();

        foreach (var r in results)
        {
            var obj = getObj(r);
            snapshots.Add(new ObjectSnapshot
            {
                ScanLogId = scanLogId,
                ClienteId = cliente.Id,
                ClienteNombre = cliente.Nombre,
                ClienteCodigo = cliente.Codigo,
                Ambiente = ambiente,
                ObjectFullName = obj.FullName,
                SchemaName = obj.SchemaName,
                ObjectName = obj.ObjectName,
                ObjectType = obj.ObjectType.ToSqlType(),
                DefinitionHash = obj.DefinitionHash,
                ObjectLastModified = obj.LastModified,
                SnapshotDate = now,
                IsCustom = r.IsCustom
            });
            definitions.Add(obj.Definition);
        }

        return (snapshots, definitions);
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
