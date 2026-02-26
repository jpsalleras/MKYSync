using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using DbSync.Core.Models;

namespace DbSync.Web.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;

    public LoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
    }

    [BindProperty] public string UserName { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty] public bool RememberMe { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Ingrese usuario y contrasena";
            return Page();
        }

        var user = await _userManager.FindByNameAsync(UserName);
        if (user != null && !user.Activo)
        {
            ErrorMessage = "Su cuenta esta deshabilitada. Contacte al administrador.";
            return Page();
        }

        var result = await _signInManager.PasswordSignInAsync(UserName, Password, RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            if (!string.IsNullOrEmpty(ReturnUrl) && ReturnUrl != "/")
                return LocalRedirect(ReturnUrl);

            // Reader no tiene acceso al Dashboard, redirigir a Clientes
            var roles = await _userManager.GetRolesAsync(user!);
            if (roles.Contains("Admin") || roles.Contains("DBA"))
                return LocalRedirect("/");
            return LocalRedirect("/Clientes");
        }

        if (result.IsLockedOut)
            ErrorMessage = "Cuenta bloqueada por intentos fallidos. Intente en 15 minutos.";
        else
            ErrorMessage = "Usuario o contrasena incorrectos";

        return Page();
    }
}
