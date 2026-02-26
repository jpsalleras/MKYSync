using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;
using System.Security.Claims;

namespace DbSync.Web.Pages.Versions;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly VersionHistoryReader _versionReader;
    private readonly CredentialEncryptor _encryptor;
    private readonly UserClientService _userClientService;

    public IndexModel(AppDbContext db, VersionHistoryReader versionReader, CredentialEncryptor encryptor, UserClientService userClientService)
    {
        _db = db;
        _versionReader = versionReader;
        _encryptor = encryptor;
        _userClientService = userClientService;
    }

    public List<SelectListItem> ClientesList { get; set; } = new();
    public Cliente? SelectedCliente { get; set; }
    public List<ObjectVersionSummary>? Stats { get; set; }
    public List<ObjectVersionEntry>? SelectedObjectHistory { get; set; }
    public string? ErrorMessage { get; set; }
    public string? DbCheckMessage { get; set; }

    [BindProperty(SupportsGet = true)] public int? ClienteId { get; set; }
    [BindProperty(SupportsGet = true)] public string Ambiente { get; set; } = "PR";
    [BindProperty(SupportsGet = true)] public string? Filtro { get; set; }
    [BindProperty(SupportsGet = true)] public string? SelectedObject { get; set; }

    public async Task OnGetAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var isAdmin = User.IsInRole("Admin");

        ClientesList = await _userClientService.GetClientesSelectListAsync(userId, isAdmin, ClienteId);

        if (!ClienteId.HasValue || ClienteId == 0) return;

        if (!await _userClientService.UserHasAccessToClienteAsync(userId, isAdmin, ClienteId.Value))
        {
            ErrorMessage = "No tiene acceso a este cliente";
            return;
        }

        try
        {
            SelectedCliente = await _db.Clientes
                .Include(c => c.Ambientes)
                .FirstOrDefaultAsync(c => c.Id == ClienteId.Value);

            if (SelectedCliente == null)
            {
                ErrorMessage = "Cliente no encontrado";
                return;
            }

            var amb = Enum.Parse<Ambiente>(Ambiente, true);
            var ambConfig = SelectedCliente.Ambientes.FirstOrDefault(a => a.Ambiente == amb);
            if (ambConfig == null)
            {
                ErrorMessage = $"El cliente no tiene configurado el ambiente {Ambiente}";
                return;
            }

            var connStr = ambConfig.GetConnectionString(_encryptor.Decrypt);

            // Timeout general de 20 segundos para todas las operaciones contra el servidor del cliente
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var (exists, msg) = await _versionReader.CheckVersionControlDbAsync(connStr, cts.Token);
            if (!exists)
            {
                DbCheckMessage = "El historial de versiones no esta activo en este ambiente. Active el versionado desde la configuracion del cliente.";
                return;
            }

            Stats = await _versionReader.GetStatsAsync(connStr, ct: cts.Token);

            if (!string.IsNullOrWhiteSpace(Filtro))
            {
                Stats = Stats.Where(s =>
                    s.ObjectName.Contains(Filtro, StringComparison.OrdinalIgnoreCase) ||
                    s.FullName.Contains(Filtro, StringComparison.OrdinalIgnoreCase)
                ).ToList();
            }

            if (!string.IsNullOrEmpty(SelectedObject))
            {
                var parts = SelectedObject.Split('.', 2);
                var schema = parts.Length > 1 ? parts[0] : "dbo";
                var name = parts.Length > 1 ? parts[1] : parts[0];

                var spStat = Stats.FirstOrDefault(s => s.FullName.Equals(SelectedObject, StringComparison.OrdinalIgnoreCase));
                var dbName = spStat?.DatabaseName;

                SelectedObjectHistory = await _versionReader.GetHistoryAsync(
                    connStr,
                    databaseName: dbName,
                    schemaName: schema,
                    objectName: name,
                    ct: cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Tiempo de espera agotado al consultar el servidor del cliente. Verifique la conexión.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al consultar: {ex.Message}";
        }
    }
}
