using DbSync.Core.Models;
using DbSync.Core.Services;
using Microsoft.Extensions.Options;

namespace DbSync.Worker;

/// <summary>
/// BackgroundService que ejecuta el scanner periódicamente.
/// Se puede instalar como Windows Service o ejecutar como consola.
/// </summary>
public class ScanWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkerSettings _settings;
    private readonly ILogger<ScanWorker> _logger;

    public ScanWorker(
        IServiceProvider serviceProvider,
        IOptions<WorkerSettings> settings,
        ILogger<ScanWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DbSync Worker iniciado. Intervalo: {Minutes} minutos, Parallelism: {Parallel}",
            _settings.IntervalMinutes, _settings.MaxParallelClients);

        if (_settings.RunOnStartup)
        {
            await RunScanAsync(stoppingToken);
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(_settings.IntervalMinutes));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunScanAsync(stoppingToken);
        }
    }

    private async Task RunScanAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Iniciando scan programado...");

            using var scope = _serviceProvider.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<SnapshotScanner>();

            var result = await scanner.RunFullScanAsync(
                ScanTrigger.Scheduled,
                maxParallelClients: _settings.MaxParallelClients,
                ct: ct);

            _logger.LogInformation(
                "Scan completado: {Objects} objetos, {Changes} cambios, {Errors} errores. Duración: {Duration:F1}s",
                result.TotalObjectsScanned, result.TotalChangesDetected,
                result.TotalErrors,
                result.CompletedAt.HasValue
                    ? (result.CompletedAt.Value - result.StartedAt).TotalSeconds
                    : 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Scan cancelado por shutdown del servicio");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error no manejado durante el scan");
        }
    }
}
