using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;

namespace DbSync.Web.Pages.Usuarios;

[Authorize(Policy = "AdminOnly")]
public class EditModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly AppDbContext _db;

    public EditModel(UserManager<ApplicationUser> userManager,
                     RoleManager<IdentityRole> roleManager,
                     AppDbContext db)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _db = db;
    }

    [BindProperty] public string? UserId { get; set; }
    [BindProperty] public string UserName { get; set; } = string.Empty;
    [BindProperty] public string Email { get; set; } = string.Empty;
    [BindProperty] public string NombreCompleto { get; set; } = string.Empty;
    [BindProperty] public bool Activo { get; set; } = true;
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string SelectedRole { get; set; } = "Reader";
    [BindProperty] public List<int> SelectedClienteIds { get; set; } = new();

    public bool IsNew => string.IsNullOrEmpty(UserId);
    public List<IdentityRole> AllRoles { get; set; } = new();
    public List<Cliente> AllClientes { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync(string? id)
    {
        await LoadLists();

        if (!string.IsNullOrEmpty(id))
        {
            var user = await _userManager.Users
                .Include(u => u.ClientesAsignados)
                .FirstOrDefaultAsync(u => u.Id == id);

            if (user != null)
            {
                UserId = user.Id;
                UserName = user.UserName!;
                Email = user.Email!;
                NombreCompleto = user.NombreCompleto;
                Activo = user.Activo;
                SelectedClienteIds = user.ClientesAsignados.Select(uc => uc.ClienteId).ToList();

                var roles = await _userManager.GetRolesAsync(user);
                SelectedRole = roles.FirstOrDefault() ?? "Reader";
            }
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadLists();

        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(NombreCompleto))
        {
            ErrorMessage = "Todos los campos son requeridos";
            return Page();
        }

        if (IsNew)
        {
            if (string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "La contrasena es requerida para usuarios nuevos";
                return Page();
            }

            var user = new ApplicationUser
            {
                UserName = UserName,
                Email = Email,
                NombreCompleto = NombreCompleto,
                Activo = Activo,
                EmailConfirmed = true
            };

            var result = await _userManager.CreateAsync(user, Password);
            if (!result.Succeeded)
            {
                ErrorMessage = string.Join(". ", result.Errors.Select(e => e.Description));
                return Page();
            }

            await _userManager.AddToRoleAsync(user, SelectedRole);
            await UpdateClientAssignments(user.Id);

            return RedirectToPage("Index");
        }
        else
        {
            var user = await _userManager.FindByIdAsync(UserId!);
            if (user == null) { ErrorMessage = "Usuario no encontrado"; return Page(); }

            user.UserName = UserName;
            user.Email = Email;
            user.NombreCompleto = NombreCompleto;
            user.Activo = Activo;

            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                ErrorMessage = string.Join(". ", result.Errors.Select(e => e.Description));
                return Page();
            }

            if (!string.IsNullOrWhiteSpace(Password))
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var passResult = await _userManager.ResetPasswordAsync(user, token, Password);
                if (!passResult.Succeeded)
                {
                    ErrorMessage = string.Join(". ", passResult.Errors.Select(e => e.Description));
                    return Page();
                }
            }

            var currentRoles = await _userManager.GetRolesAsync(user);
            await _userManager.RemoveFromRolesAsync(user, currentRoles);
            await _userManager.AddToRoleAsync(user, SelectedRole);

            await UpdateClientAssignments(user.Id);

            return RedirectToPage("Index");
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (string.IsNullOrEmpty(UserId)) return RedirectToPage("Index");

        var user = await _userManager.FindByIdAsync(UserId);
        if (user != null)
        {
            var assignments = await _db.UsuarioClientes
                .Where(uc => uc.UserId == UserId)
                .ToListAsync();
            _db.UsuarioClientes.RemoveRange(assignments);
            await _db.SaveChangesAsync();

            await _userManager.DeleteAsync(user);
        }

        return RedirectToPage("Index");
    }

    private async Task UpdateClientAssignments(string userId)
    {
        var existing = await _db.UsuarioClientes
            .Where(uc => uc.UserId == userId)
            .ToListAsync();
        _db.UsuarioClientes.RemoveRange(existing);

        if (SelectedRole != "Admin")
        {
            foreach (var clienteId in SelectedClienteIds)
            {
                _db.UsuarioClientes.Add(new UsuarioCliente
                {
                    UserId = userId,
                    ClienteId = clienteId
                });
            }
        }

        await _db.SaveChangesAsync();
    }

    private async Task LoadLists()
    {
        AllRoles = await _roleManager.Roles.OrderBy(r => r.Name).ToListAsync();
        AllClientes = await _db.Clientes
            .Where(c => c.Activo)
            .OrderBy(c => c.Nombre)
            .ToListAsync();
    }
}
