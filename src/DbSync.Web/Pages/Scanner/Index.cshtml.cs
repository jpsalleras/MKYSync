using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;
using System.Security.Claims;

namespace DbSync.Web.Pages.Scanner;

[Authorize(Policy = "DBAOrAdmin")]
public class IndexModel : PageModel
{
    private readonly CentralRepository _centralRepo;
    private readonly ScanQueue _scanQueue;
    private readonly AppDbContext _db;
    private readonly NotificationService _notificationService;
    private readonly SmtpSettings _smtp;
    private readonly UserClientService _userClientService;

    public IndexModel(
        CentralRepository centralRepo,
        ScanQueue scanQueue,
        AppDbContext db,
        NotificationService notificationService,
        IOptions<SmtpSettings> smtpSettings,
        UserClientService userClientService)
    {
        _centralRepo = centralRepo;
        _scanQueue = scanQueue;
        _db = db;
        _notificationService = notificationService;
        _smtp = smtpSettings.Value;
        _userClientService = userClientService;
    }

    public ScanLog? LatestScan { get; set; }
    public List<DetectedChange> RecentChanges { get; set; } = new();
    public List<SelectListItem> ClientesList { get; set; } = new();
    public string? StatusMessage { get; set; }
    public bool IsScanQueued { get; set; }
    public bool SmtpEnabled => _smtp.Enabled;
    public int PendingNotifications { get; set; }

    public async Task OnGetAsync()
    {
        await LoadDataAsync();
    }

    public async Task<IActionResult> OnPostTriggerFullScanAsync()
    {
        if (!User.IsInRole("Admin") && !User.IsInRole("DBA"))
            return Forbid();

        try
        {
            await _scanQueue.QueueScanAsync(new ScanRequest(null, null, User.Identity?.Name ?? "Web User"));
            StatusMessage = "Scan completo encolado. Se procesara en segundo plano.";
        }
        catch
        {
            StatusMessage = "Error: no se pudo encolar el scan. La cola puede estar llena.";
        }

        await LoadDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostTriggerClientScanAsync(int clienteId)
    {
        if (!User.IsInRole("Admin") && !User.IsInRole("DBA"))
            return Forbid();

        try
        {
            await _scanQueue.QueueScanAsync(new ScanRequest(clienteId, null, User.Identity?.Name ?? "Web User"));
            StatusMessage = "Scan individual encolado. Se procesara en segundo plano.";
        }
        catch
        {
            StatusMessage = "Error: no se pudo encolar el scan.";
        }

        await LoadDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostTestEmailAsync()
    {
        if (!User.IsInRole("Admin") && !User.IsInRole("DBA"))
            return Forbid();

        var (success, message) = await _notificationService.SendTestEmailAsync();
        StatusMessage = success
            ? $"Email de prueba enviado correctamente: {message}"
            : $"Error enviando email: {message}";

        await LoadDataAsync();
        return Page();
    }

    private async Task LoadDataAsync()
    {
        var scans = await _centralRepo.GetRecentScanLogsAsync(1);
        LatestScan = scans.FirstOrDefault();
        RecentChanges = await _centralRepo.GetRecentChangesAsync(top: 30);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var isAdmin = User.IsInRole("Admin");

        ClientesList = await _userClientService.GetClientesForUser(userId, isAdmin)
            .Where(c => c.Activo)
            .OrderBy(c => c.Nombre)
            .Select(c => new SelectListItem(c.Nombre, c.Id.ToString()))
            .ToListAsync();

        _scanQueue.TryPeek(out _);

        // Contar notificaciones pendientes
        var pending = await _centralRepo.GetPendingNotificationsAsync();
        PendingNotifications = pending.Count;
    }
}
