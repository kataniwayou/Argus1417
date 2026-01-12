using ArgusApi;
using Microsoft.Extensions.Logging;

namespace ArgusClientApp.Workers;

/// <summary>
/// Simple worker using ArgusBackgroundService.
/// Logs messages every 30 seconds - heartbeat is automatic.
/// </summary>
public class SimpleLoopWorker : ArgusBackgroundService
{
    private readonly ILogger<SimpleLoopWorker> _logger;
    private int _messageCount;

    public SimpleLoopWorker(IArgusMonitor monitor, ILogger<SimpleLoopWorker> logger)
        : base(monitor, logger)
    {
        _logger = logger;
    }

    protected override Task DoWorkAsync(CancellationToken stoppingToken)
    {
        _messageCount++;

        _logger.LogInformation(
            "SimpleLoopWorker message #{Count} at {Time}",
            _messageCount, DateTimeOffset.Now);

        return Task.CompletedTask;
    }
}

