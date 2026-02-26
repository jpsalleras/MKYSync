using System.Threading.Channels;
using DbSync.Core.Models;

namespace DbSync.Core.Services;

/// <summary>
/// Cola en memoria para pedidos de scan.
/// La web encola pedidos que son procesados por un BackgroundService.
/// </summary>
public class ScanQueue
{
    private readonly Channel<ScanRequest> _channel =
        Channel.CreateBounded<ScanRequest>(10);

    public async ValueTask QueueScanAsync(ScanRequest request, CancellationToken ct = default)
        => await _channel.Writer.WriteAsync(request, ct);

    public async ValueTask<ScanRequest> DequeueAsync(CancellationToken ct = default)
        => await _channel.Reader.ReadAsync(ct);

    public bool TryPeek(out ScanRequest? request)
        => _channel.Reader.TryPeek(out request);
}

/// <summary>
/// Pedido de scan encolado desde la web.
/// </summary>
public record ScanRequest(
    int? ClienteId,
    Ambiente? Ambiente,
    string? TriggeredBy,
    bool ScanAll = false
);
