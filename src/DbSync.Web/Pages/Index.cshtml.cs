using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;
using System.Security.Claims;

namespace DbSync.Web.Pages;

[Authorize(Policy = "DBAOrAdmin")]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserClientService _userClientService;
    private readonly CentralRepository _centralRepo;

    public IndexModel(AppDbContext db, UserClientService userClientService, CentralRepository centralRepo)
    {
        _db = db;
        _userClientService = userClientService;
        _centralRepo = centralRepo;
    }

    public int TotalClientes { get; set; }
    public int TotalAmbientes { get; set; }
    public int SyncHoy { get; set; }
    public int ErroresRecientes { get; set; }
    public List<SyncHistory> UltimasSync { get; set; } = new();

    // Scanner stats
    public ScanLog? UltimoScan { get; set; }
    public int TotalScans { get; set; }
    public int CambiosDetectados7Dias { get; set; }
    public List<DetectedChange> UltimosCambios { get; set; } = new();

    public async Task OnGetAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var isAdmin = User.IsInRole("Admin");

        var clienteIds = await _userClientService.GetClientesForUser(userId, isAdmin)
            .Select(c => c.Id)
            .ToListAsync();

        TotalClientes = clienteIds.Count;
        TotalAmbientes = await _db.ClienteAmbientes.CountAsync(a => clienteIds.Contains(a.ClienteId));

        var hoy = DateTime.Today;
        SyncHoy = await _db.SyncHistory.CountAsync(s => s.FechaEjecucion >= hoy && clienteIds.Contains(s.ClienteId));

        var hace7Dias = DateTime.Today.AddDays(-7);
        ErroresRecientes = await _db.SyncHistory.CountAsync(s => !s.Exitoso && s.FechaEjecucion >= hace7Dias && clienteIds.Contains(s.ClienteId));

        UltimasSync = await _db.SyncHistory
            .Include(s => s.Cliente)
            .Where(s => clienteIds.Contains(s.ClienteId))
            .OrderByDescending(s => s.FechaEjecucion)
            .Take(10)
            .ToListAsync();

        // Scanner stats desde CentralRepository
        var recentScans = await _centralRepo.GetRecentScanLogsAsync(1);
        UltimoScan = recentScans.FirstOrDefault();

        var allRecentScans = await _centralRepo.GetRecentScanLogsAsync(100);
        TotalScans = allRecentScans.Count;

        // Cambios detectados filtrados por clientes del usuario
        UltimosCambios = await _centralRepo.GetRecentChangesForClientsAsync(clienteIds, top: 10);
        CambiosDetectados7Dias = await _centralRepo.GetChangesCountForClientsSinceAsync(clienteIds, hace7Dias);

        // Para no-admin, recalcular stats del último scan solo con sus clientes
        if (UltimoScan != null && !isAdmin)
        {
            var (totalClientes, totalObjects, totalChanges, totalErrors) =
                await _centralRepo.GetScanStatsForClientsAsync(UltimoScan.Id, clienteIds);
            UltimoScan.TotalClientes = totalClientes;
            UltimoScan.TotalObjectsScanned = totalObjects;
            UltimoScan.TotalChangesDetected = totalChanges;
            UltimoScan.TotalErrors = totalErrors;
        }
    }
}
