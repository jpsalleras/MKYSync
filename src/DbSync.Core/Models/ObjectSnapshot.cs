namespace DbSync.Core.Models;

/// <summary>
/// Snapshot capturado de un objeto de base de datos en un momento dado.
/// Se almacena en el repositorio central (SQL Server).
/// </summary>
public class ObjectSnapshot
{
    public long Id { get; set; }
    public int ScanLogId { get; set; }
    public int ClienteId { get; set; }
    public string ClienteNombre { get; set; } = string.Empty;
    public string ClienteCodigo { get; set; } = string.Empty;
    public Ambiente Ambiente { get; set; }
    public string ObjectFullName { get; set; } = string.Empty;   // schema.nombre
    public string SchemaName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;       // P, V, FN, TF, IF
    public string DefinitionHash { get; set; } = string.Empty;   // SHA256
    public DateTime ObjectLastModified { get; set; }
    public DateTime SnapshotDate { get; set; }
    public bool IsCustom { get; set; }
}

/// <summary>
/// Almacena la definición NVARCHAR(MAX) de un snapshot.
/// Separado de ObjectSnapshot para mantener la tabla principal liviana.
/// </summary>
public class ObjectSnapshotDefinition
{
    public long Id { get; set; }
    public long ObjectSnapshotId { get; set; }
    public string Definition { get; set; } = string.Empty;
}
