using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;
using System.Security.Claims;

namespace DbSync.Web.Pages.Clientes;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserClientService _userClientService;

    public IndexModel(AppDbContext db, UserClientService userClientService)
    {
        _db = db;
        _userClientService = userClientService;
    }

    public List<Cliente> Clientes { get; set; } = new();

    public async Task OnGetAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var isAdmin = User.IsInRole("Admin");

        Clientes = await _userClientService.GetClientesForUser(userId, isAdmin)
            .Include(c => c.Ambientes)
            .Include(c => c.ObjetosCustom)
            .ToListAsync();
    }
}
