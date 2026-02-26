using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;
using System.Security.Claims;

namespace DbSync.Web.Pages.Sync;

[Authorize(Policy = "DBAOrAdmin")]
public class HistoryModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserClientService _userClientService;

    public HistoryModel(AppDbContext db, UserClientService userClientService)
    {
        _db = db;
        _userClientService = userClientService;
    }

    public List<SelectListItem> ClientesList { get; set; } = new();
    public List<SyncHistory> History { get; set; } = new();

    [BindProperty(SupportsGet = true)] public int? ClienteId { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? Desde { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? Hasta { get; set; }
    [BindProperty(SupportsGet = true)] public bool SoloErrores { get; set; }

    public async Task OnGetAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var isAdmin = User.IsInRole("Admin");

        ClientesList = await _userClientService.GetClientesSelectListAsync(userId, isAdmin, ClienteId);

        var clienteIds = await _userClientService.GetClientesForUser(userId, isAdmin)
            .Select(c => c.Id).ToListAsync();

        // Default: fecha actual
        Desde ??= DateTime.Today;
        Hasta ??= DateTime.Today;

        var query = _db.SyncHistory
            .Include(h => h.Cliente)
            .Where(h => clienteIds.Contains(h.ClienteId));

        if (ClienteId.HasValue)
            query = query.Where(h => h.ClienteId == ClienteId.Value);
        query = query.Where(h => h.FechaEjecucion >= Desde.Value);
        query = query.Where(h => h.FechaEjecucion <= Hasta.Value.AddDays(1));
        if (SoloErrores)
            query = query.Where(h => !h.Exitoso);

        History = await query
            .OrderByDescending(h => h.FechaEjecucion)
            .Take(200)
            .ToListAsync();
    }
}
