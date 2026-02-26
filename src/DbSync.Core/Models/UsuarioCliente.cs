namespace DbSync.Core.Models;

public class UsuarioCliente
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int ClienteId { get; set; }

    public ApplicationUser? Usuario { get; set; }
    public Cliente? Cliente { get; set; }
}
