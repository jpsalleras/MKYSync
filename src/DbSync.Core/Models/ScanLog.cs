namespace DbSync.Core.Models;

/// <summary>
/// Representa una ejecución del scanner (un ciclo completo del worker).
/// </summary>
public class ScanLog
{
    public int Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public ScanStatus Status { get; set; } = ScanStatus.Running;
    public ScanTrigger Trigger { get; set; } = ScanTrigger.Scheduled;
    public string? TriggeredBy { get; set; }
    public int TotalClientes { get; set; }
    public int TotalAmbientes { get; set; }
    public int TotalObjectsScanned { get; set; }
    public int TotalChangesDetected { get; set; }
    public int TotalErrors { get; set; }
    public string? ErrorSummary { get; set; }

    public List<ScanLogEntry> Entries { get; set; } = new();
}

public enum ScanStatus
{
    Running,
    Completed,
    CompletedWithErrors,
    Failed
}

public enum ScanTrigger
{
    Scheduled,
    Manual,
    OnDemand,
    Compare
}

/// <summary>
/// Detalle por cliente/ambiente dentro de un ScanLog.
/// </summary>
public class ScanLogEntry
{
    public long Id { get; set; }
    public int ScanLogId { get; set; }
    public int ClienteId { get; set; }
    public string ClienteCodigo { get; set; } = string.Empty;
    public Ambiente Ambiente { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool Success { get; set; }
    public int ObjectsFound { get; set; }
    public int ObjectsNew { get; set; }
    public int ObjectsModified { get; set; }
    public int ObjectsDeleted { get; set; }
    public string? ErrorMessage { get; set; }
    public double DurationSeconds { get; set; }

    public ScanLog? ScanLog { get; set; }
}

/// <summary>
/// Cambio detectado entre dos snapshots consecutivos.
/// Usado para notificaciones y log de cambios.
/// </summary>
public class DetectedChange
{
    public long Id { get; set; }
    public int ScanLogId { get; set; }
    public int ClienteId { get; set; }
    public string ClienteCodigo { get; set; } = string.Empty;
    public Ambiente Ambiente { get; set; }
    public string ObjectFullName { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public ObjectChangeType ChangeType { get; set; }
    public string? PreviousHash { get; set; }
    public string? CurrentHash { get; set; }
    public DateTime DetectedAt { get; set; }
    public bool NotificationSent { get; set; }
}

public enum ObjectChangeType
{
    Created,
    Modified,
    Deleted
}
