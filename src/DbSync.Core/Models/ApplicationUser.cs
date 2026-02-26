using Microsoft.AspNetCore.Identity;

namespace DbSync.Core.Models;

public class ApplicationUser : IdentityUser
{
    public string NombreCompleto { get; set; } = string.Empty;
    public bool Activo { get; set; } = true;
    public DateTime FechaCreacion { get; set; } = DateTime.Now;

    public List<UsuarioCliente> ClientesAsignados { get; set; } = new();
}
