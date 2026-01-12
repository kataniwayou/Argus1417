using ArgusApi;
using ArgusClientApp.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArgusClientApp.Workers;

/// <summary>
/// Example worker that does NOT inherit from ArgusBackgroundService.
/// Demonstrates manual heartbeat and exception recording using IArgusMonitor directly.
/// </summary>
public class NonArgusWorker : BackgroundService
{
    private readonly IArgusMonitor _monitor;
    private readonly OrderProcessingService _orderService;
    private readonly ILogger<NonArgusWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(15);

    public NonArgusWorker(
        IArgusMonitor monitor,
        OrderProcessingService orderService,
        ILogger<NonArgusWorker> logger)
    {
        _monitor = monitor;
        _orderService = orderService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NonArgusWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Manual heartbeat using component name (string)
                _monitor.Heartbeat("NonArgusWorker");

                // Process a batch of orders using the service
                await _orderService.ProcessOrderBatchAsync(5, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Manual exception recording using component name
                _monitor.RecordException(ex, "NonArgusWorker", "ExecuteAsync");
                _logger.LogError(ex, "NonArgusWorker encountered an error");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("NonArgusWorker stopped");
    }
}

