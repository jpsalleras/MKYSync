namespace DbSync.Core.Models;

/// <summary>
/// Resultado de la comparación de un objeto entre dos ambientes.
/// </summary>
public class CompareResult
{
    public string ObjectFullName { get; set; } = string.Empty;
    public DbObjectType ObjectType { get; set; }
    public CompareStatus Status { get; set; }
    public bool IsCustom { get; set; }

    /// <summary>Objeto en el ambiente origen (null si solo existe en destino)</summary>
    public DbObject? Source { get; set; }

    /// <summary>Objeto en el ambiente destino (null si solo existe en origen)</summary>
    public DbObject? Target { get; set; }

    /// <summary>Diff en formato unificado (solo cuando Status = Modified)</summary>
    public string? DiffHtml { get; set; }

    /// <summary>Cantidad de líneas agregadas</summary>
    public int LinesAdded { get; set; }

    /// <summary>Cantidad de líneas eliminadas</summary>
    public int LinesRemoved { get; set; }
}

public enum CompareStatus
{
    /// <summary>Mismo contenido en ambos ambientes</summary>
    Equal,

    /// <summary>Contenido diferente entre ambientes</summary>
    Modified,

    /// <summary>Existe solo en el ambiente origen</summary>
    OnlyInSource,

    /// <summary>Existe solo en el ambiente destino</summary>
    OnlyInTarget
}

/// <summary>
/// Resumen general de la comparación entre dos ambientes de un cliente.
/// </summary>
public class ComparisonSummary
{
    public int ClienteId { get; set; }
    public string ClienteNombre { get; set; } = string.Empty;
    public Ambiente AmbienteOrigen { get; set; }
    public Ambiente AmbienteDestino { get; set; }
    public DateTime FechaComparacion { get; set; } = DateTime.Now;
    public TimeSpan Duracion { get; set; }

    public List<CompareResult> Results { get; set; } = new();

    public int TotalEqual => Results.Count(r => r.Status == CompareStatus.Equal);
    public int TotalModified => Results.Count(r => r.Status == CompareStatus.Modified);
    public int TotalOnlyInSource => Results.Count(r => r.Status == CompareStatus.OnlyInSource);
    public int TotalOnlyInTarget => Results.Count(r => r.Status == CompareStatus.OnlyInTarget);
    public int TotalCustom => Results.Count(r => r.IsCustom);
    public int TotalObjects => Results.Count;
    public bool HasDifferences => TotalModified > 0 || TotalOnlyInSource > 0 || TotalOnlyInTarget > 0;
}
