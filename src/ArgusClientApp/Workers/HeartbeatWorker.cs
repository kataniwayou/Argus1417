using ArgusApi;
using Microsoft.Extensions.Logging;

namespace ArgusClientApp.Workers;

/// <summary>
/// Background worker using ArgusBackgroundService base class.
/// Demonstrates the simplified pattern - just implement DoWorkAsync().
/// Heartbeat and exception handling are automatic.
/// </summary>
public class HeartbeatWorker : ArgusBackgroundService
{
    private readonly ILogger<HeartbeatWorker> _logger;
    private int _cycleCount;

    // Override interval if needed (default is 30 seconds)
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    public HeartbeatWorker(IArgusMonitor monitor, ILogger<HeartbeatWorker> logger)
        : base(monitor, logger)
    {
        _logger = logger;
    }

    protected override Task OnStartAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HeartbeatWorker initialized, ready to process");
        return Task.CompletedTask;
    }

    protected override async Task DoWorkAsync(CancellationToken stoppingToken)
    {
        _cycleCount++;

        _logger.LogInformation(
            "HeartbeatWorker cycle {Cycle} at {Time}",
            _cycleCount, DateTimeOffset.Now);

        await Task.Delay(100, stoppingToken);
    }

    protected override Task OnStopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HeartbeatWorker cleanup complete, processed {Count} cycles", _cycleCount);
        return Task.CompletedTask;
    }
}

