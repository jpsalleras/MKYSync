namespace DbSync.Core.Models;

/// <summary>
/// Representa una entrada de la tabla ObjectChangeHistory
/// en la base de datos del cliente (registrada por DDL trigger).
/// </summary>
public class ObjectVersionEntry
{
    public int HistoryID { get; set; }
    public string DatabaseName { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;  // PROCEDURE, VIEW, FUNCTION
    public string EventType { get; set; } = string.Empty;   // CREATE, ALTER, DROP
    public string? ObjectDefinition { get; set; }
    public string ModifiedBy { get; set; } = string.Empty;
    public DateTime ModifiedDate { get; set; }
    public string? HostName { get; set; }
    public string? ApplicationName { get; set; }
    public int VersionNumber { get; set; }
    public int? DefinitionLength { get; set; }

    /// <summary>Nombre completo: schema.nombre</summary>
    public string FullName => $"{SchemaName}.{ObjectName}";

    /// <summary>Tipo corto para badges</summary>
    public string ShortType => ObjectType switch
    {
        "PROCEDURE" => "SP",
        "VIEW" => "VIEW",
        "FUNCTION" => "FN",
        _ => ObjectType
    };

    /// <summary>Descripción corta para listados</summary>
    public string ShortDescription => $"v{VersionNumber} - {EventType} por {ModifiedBy} ({ModifiedDate:dd/MM/yyyy HH:mm})";
}

/// <summary>
/// Resumen de un objeto con su historial de versiones.
/// </summary>
public class ObjectVersionSummary
{
    public string DatabaseName { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public int TotalVersions { get; set; }
    public int CurrentVersion { get; set; }
    public DateTime FirstModified { get; set; }
    public DateTime LastModified { get; set; }
    public int TotalContributors { get; set; }

    public string FullName => $"{SchemaName}.{ObjectName}";

    public string ShortType => ObjectType switch
    {
        "PROCEDURE" => "SP",
        "VIEW" => "VIEW",
        "FUNCTION" => "FN",
        _ => ObjectType
    };
}

/// <summary>
/// Resultado de comparar dos versiones históricas.
/// </summary>
public class VersionCompareResult
{
    public ObjectVersionEntry Version1 { get; set; } = null!;
    public ObjectVersionEntry Version2 { get; set; } = null!;
    public string DiffHtml { get; set; } = string.Empty;
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public bool AreEqual { get; set; }
}
