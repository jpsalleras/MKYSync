using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;

namespace DbSync.Web.Pages.Usuarios;

[Authorize(Policy = "AdminOnly")]
public class IndexModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AppDbContext _db;

    public IndexModel(UserManager<ApplicationUser> userManager, AppDbContext db)
    {
        _userManager = userManager;
        _db = db;
    }

    public List<UsuarioViewModel> Usuarios { get; set; } = new();

    public async Task OnGetAsync()
    {
        var users = await _userManager.Users
            .Include(u => u.ClientesAsignados)
                .ThenInclude(uc => uc.Cliente)
            .OrderBy(u => u.NombreCompleto)
            .ToListAsync();

        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            Usuarios.Add(new UsuarioViewModel
            {
                Id = user.Id,
                UserName = user.UserName!,
                NombreCompleto = user.NombreCompleto,
                Email = user.Email!,
                Activo = user.Activo,
                Roles = roles.ToList(),
                ClientesAsignados = user.ClientesAsignados
                    .Where(uc => uc.Cliente != null)
                    .Select(uc => $"{uc.Cliente!.Codigo}")
                    .ToList()
            });
        }
    }

    public class UsuarioViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string NombreCompleto { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool Activo { get; set; }
        public List<string> Roles { get; set; } = new();
        public List<string> ClientesAsignados { get; set; } = new();
    }
}
