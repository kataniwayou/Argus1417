using ArgusApi;
using Microsoft.Extensions.Logging;

namespace ArgusClientApp.Services;

/// <summary>
/// Example service demonstrating heartbeat and exception recording
/// from a non-worker context (i.e., not inheriting from ArgusBackgroundService).
/// </summary>
public class OrderProcessingService
{
    private readonly IArgusMonitor _monitor;
    private readonly ILogger<OrderProcessingService> _logger;
    private readonly Random _random = new();

    public OrderProcessingService(IArgusMonitor monitor, ILogger<OrderProcessingService> logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    /// <summary>
    /// Processes an order batch. Demonstrates heartbeat and exception recording.
    /// </summary>
    public async Task ProcessOrderBatchAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        // Send heartbeat at start of processing - indicates service is alive
        _monitor.Heartbeat("OrderProcessingService");

        using var activity = _monitor.StartTrace("OrderProcessingService.ProcessBatch");
        
        _logger.LogInformation("Processing batch of {BatchSize} orders", batchSize);

        for (int i = 0; i < batchSize; i++)
        {
            try
            {
                await ProcessSingleOrderAsync(i + 1, cancellationToken);
            }
            catch (Exception ex)
            {
                // Record exception from non-worker context
                _monitor.RecordException(ex, "OrderProcessingService", "ProcessSingleOrder");
                _logger.LogWarning(ex, "Failed to process order {OrderNumber}", i + 1);
                // Continue processing other orders
            }
        }

        _logger.LogInformation("Batch processing complete");
    }

    private async Task ProcessSingleOrderAsync(int orderNumber, CancellationToken cancellationToken)
    {
        // Simulate processing time
        await Task.Delay(_random.Next(10, 50), cancellationToken);

        // Simulate occasional failures (10% chance)
        if (_random.NextDouble() < 0.1)
        {
            throw new InvalidOperationException($"Failed to process order {orderNumber}: Payment declined");
        }

        _logger.LogDebug("Processed order {OrderNumber}", orderNumber);
    }
}

