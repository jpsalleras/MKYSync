using DbSync.Core.Models;
using DbSync.Core.Services;

namespace DbSync.Web.Services;

/// <summary>
/// BackgroundService que corre dentro del proceso web.
/// Escucha la ScanQueue y ejecuta scans cuando se solicitan desde la UI.
/// </summary>
public class WebScanBackgroundService : BackgroundService
{
    private readonly ScanQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WebScanBackgroundService> _logger;

    public WebScanBackgroundService(
        ScanQueue queue,
        IServiceProvider serviceProvider,
        ILogger<WebScanBackgroundService> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebScanBackgroundService iniciado, esperando pedidos...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var request = await _queue.DequeueAsync(stoppingToken);

                _logger.LogInformation(
                    "Procesando pedido de scan: ClienteId={ClienteId}, Ambiente={Ambiente}, TriggeredBy={TriggeredBy}",
                    request.ClienteId, request.Ambiente, request.TriggeredBy);

                using var scope = _serviceProvider.CreateScope();
                var scanner = scope.ServiceProvider.GetRequiredService<SnapshotScanner>();

                if (request.ClienteId.HasValue)
                {
                    // Scan de un cliente específico
                    var result = await scanner.ScanSingleAsync(
                        request.ClienteId.Value,
                        request.Ambiente,
                        ScanTrigger.Manual,
                        request.TriggeredBy,
                        scanAll: request.ScanAll,
                        ct: stoppingToken);

                    _logger.LogInformation(
                        "Scan individual completado: {Objects} objetos, {Changes} cambios, {Errors} errores",
                        result.TotalObjectsScanned, result.TotalChangesDetected, result.TotalErrors);
                }
                else
                {
                    // Scan completo
                    var result = await scanner.RunFullScanAsync(
                        ScanTrigger.Manual,
                        request.TriggeredBy,
                        scanAll: request.ScanAll,
                        ct: stoppingToken);

                    _logger.LogInformation(
                        "Scan completo finalizado: {Objects} objetos, {Changes} cambios, {Errors} errores",
                        result.TotalObjectsScanned, result.TotalChangesDetected, result.TotalErrors);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando pedido de scan");
            }
        }
    }
}
