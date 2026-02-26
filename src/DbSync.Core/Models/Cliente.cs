namespace DbSync.Core.Models;

public class Cliente
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Codigo { get; set; } = string.Empty;
    public bool Activo { get; set; } = true;

    public List<ClienteAmbiente> Ambientes { get; set; } = new();
    public List<ObjetoCustom> ObjetosCustom { get; set; } = new();
    public List<ObjetoBase> ObjetosBase { get; set; } = new();
}

public class ClienteAmbiente
{
    public int Id { get; set; }
    public int ClienteId { get; set; }
    public Ambiente Ambiente { get; set; }
    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? PasswordEncrypted { get; set; }
    public bool NotificarCambios { get; set; }

    public Cliente? Cliente { get; set; }

    /// <summary>
    /// Genera el connection string. Si UserId es null, usa Windows Auth.
    /// </summary>
    public string GetConnectionString(Func<string, string>? decryptPassword = null)
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            TrustServerCertificate = true,
            ConnectTimeout = 15
        };

        if (string.IsNullOrEmpty(UserId))
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = UserId;
            builder.Password = decryptPassword != null && !string.IsNullOrEmpty(PasswordEncrypted)
                ? decryptPassword(PasswordEncrypted)
                : PasswordEncrypted ?? string.Empty;
        }

        return builder.ConnectionString;
    }
}

public class ObjetoCustom
{
    public int Id { get; set; }
    public int ClienteId { get; set; }
    public string NombreObjeto { get; set; } = string.Empty;  // schema.nombre
    public string TipoObjeto { get; set; } = "SP";             // SP, VIEW, FN
    public string? Notas { get; set; }

    public Cliente? Cliente { get; set; }
}

/// <summary>
/// Objeto base del sistema.
/// ClienteId = null → global (aplica a todos los clientes).
/// ClienteId = valor → específico de ese cliente.
/// Solo los objetos base se escanean automáticamente.
/// </summary>
public class ObjetoBase
{
    public int Id { get; set; }
    public int? ClienteId { get; set; }
    public string NombreObjeto { get; set; } = string.Empty;  // schema.nombre (ej: dbo.usp_GetPacientes)
    public string TipoObjeto { get; set; } = "SP";             // SP, VIEW, FN
    public string? Notas { get; set; }

    public Cliente? Cliente { get; set; }
}

