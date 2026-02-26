namespace DbSync.Core.Models;

/// <summary>
/// Configuración del worker service.
/// Se mapea desde appsettings.json sección "Worker".
/// </summary>
public class WorkerSettings
{
    public const string SectionName = "Worker";

    /// <summary>Intervalo entre scans en minutos. Default 360 (6 horas).</summary>
    public int IntervalMinutes { get; set; } = 360;

    /// <summary>Máximo de clientes escaneados en paralelo.</summary>
    public int MaxParallelClients { get; set; } = 5;

    /// <summary>Timeout de conexión por cliente en segundos.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>Si true, ejecuta un scan al iniciar antes de esperar el primer intervalo.</summary>
    public bool RunOnStartup { get; set; } = true;
}

/// <summary>
/// Configuración SMTP para notificaciones por email.
/// Se mapea desde appsettings.json sección "Smtp".
/// </summary>
public class SmtpSettings
{
    public const string SectionName = "Smtp";

    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "DbSync";
    public List<string> Recipients { get; set; } = new();
}
