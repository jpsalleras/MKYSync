namespace DbSync.Core.Models;

/// <summary>
/// Representa un objeto de base de datos extraído de SQL Server.
/// </summary>
public class DbObject
{
    public string SchemaName { get; set; } = "dbo";
    public string ObjectName { get; set; } = string.Empty;
    public DbObjectType ObjectType { get; set; }
    public string Definition { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Nombre completo: schema.nombre
    /// </summary>
    public string FullName => $"{SchemaName}.{ObjectName}";

    /// <summary>
    /// Definición normalizada para comparación (sin diferencias de whitespace irrelevantes).
    /// </summary>
    public string NormalizedDefinition => NormalizeDefinition(Definition);

    /// <summary>
    /// Hash de la definición normalizada para comparación rápida.
    /// </summary>
    public string DefinitionHash
    {
        get
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(NormalizedDefinition);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }
    }

    /// <summary>
    /// Normaliza la definición removiendo diferencias cosméticas:
    /// - Trim de líneas
    /// - Normalizar fin de línea a \n
    /// - Remover todas las líneas vacías (no son significativas en SQL)
    /// </summary>
    public static string NormalizeDefinition(string definition)
    {
        if (string.IsNullOrWhiteSpace(definition))
            return string.Empty;

        var lines = definition
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l));

        return string.Join("\n", lines);
    }
}
