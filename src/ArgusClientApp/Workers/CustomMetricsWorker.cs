using System.Diagnostics;
using System.Diagnostics.Metrics;
using ArgusApi;
using Microsoft.Extensions.Logging;

namespace ArgusClientApp.Workers;

/// <summary>
/// Demonstrates custom metrics that are NOT part of Argus but exported via OTel.
/// These metrics can be used for Grafana dashboards but cannot create Argus NOC alerts.
/// </summary>
public class CustomMetricsWorker : ArgusBackgroundService
{
    private readonly ILogger<CustomMetricsWorker> _logger;
    private readonly Meter _customMeter;
    private readonly Counter<long> _ordersProcessedCounter;
    private readonly Histogram<double> _orderProcessingTime;
    private readonly Random _random = new();
    private int _cycleCount;

    protected override TimeSpan Interval => TimeSpan.FromSeconds(10);

    public CustomMetricsWorker(IArgusMonitor monitor, ILogger<CustomMetricsWorker> logger)
        : base(monitor, logger)
    {
        _logger = logger;

        // Create a custom meter - this is NOT coupled to Argus
        // It's just standard .NET metrics that will be exported via OTel
        _customMeter = new Meter(
            name: "ArgusClientApp.Business",
            version: "1.0.0",
            tags: new TagList { { "platform", "custom_e" } });

        // Custom counter for business metrics
        _ordersProcessedCounter = _customMeter.CreateCounter<long>(
            "orders_processed",
            unit: "count",
            description: "Total number of orders processed");

        // Custom histogram for timing
        _orderProcessingTime = _customMeter.CreateHistogram<double>(
            "order_processing_duration",
            unit: "ms",
            description: "Order processing duration in milliseconds");
    }

    protected override Task DoWorkAsync(CancellationToken stoppingToken)
    {
        _cycleCount++;

        // Simulate processing some orders
        var ordersThisCycle = _random.Next(1, 10);
        var processingTimeMs = _random.NextDouble() * 500 + 50; // 50-550ms

        // Record custom metrics with platform="custom2" at datapoint level
        // This overrides the meter-level platform="custom" tag
        _ordersProcessedCounter.Add(ordersThisCycle, new TagList
        {
            { "region", "us-east" },
            { "order_type", "standard" },
            { "platform", "custom2" }
        });

        _orderProcessingTime.Record(processingTimeMs, new TagList
        {
            { "region", "us-east" }
        });

        _logger.LogInformation(
            "CustomMetricsWorker cycle {Cycle}: Processed {Orders} orders in {Duration:F1}ms",
            _cycleCount, ordersThisCycle, processingTimeMs);

        return Task.CompletedTask;
    }
}

