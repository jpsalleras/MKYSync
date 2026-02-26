namespace DbSync.Core.Models;

/// <summary>
/// Representa una versión base global del sistema.
/// Congela el estado de todos los objetos base de un cliente/ambiente en un momento dado.
/// Es global: aplica como referencia para todos los clientes.
/// </summary>
public class BaseVersion
{
    public int Id { get; set; }
    public string VersionName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SourceClienteId { get; set; }
    public string SourceClienteNombre { get; set; } = string.Empty;
    public string SourceClienteCodigo { get; set; } = string.Empty;
    public string SourceAmbiente { get; set; } = string.Empty;
    public int TotalObjects { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>
/// Objeto que pertenece a una versión base (metadatos sin definición).
/// La definición se almacena en BaseVersionObjectDefinitions (tabla separada).
/// </summary>
public class BaseVersionObject
{
    public long Id { get; set; }
    public int BaseVersionId { get; set; }
    public string ObjectFullName { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public string DefinitionHash { get; set; } = string.Empty;
    public long SourceSnapshotId { get; set; }
}
