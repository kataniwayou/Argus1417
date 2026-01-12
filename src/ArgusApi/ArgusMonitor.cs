using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace ArgusApi;

/// <summary>
/// Implementation of Argus monitoring with OTel integration.
/// </summary>
public sealed class ArgusMonitor : IArgusMonitor, IDisposable
{
    private readonly ArgusMonitoringOptions _options;
    private readonly Meter _meter;
    private readonly Counter<long> _heartbeatCounter;
    private readonly Counter<long> _exceptionCounter;
    private readonly ConcurrentDictionary<string, bool> _registeredWorkers = new();
    private readonly TagList _meterTags; // Cached meter tags to add to each datapoint
    private bool _disposed;

    /// <inheritdoc />
    public ActivitySource ActivitySource { get; }

    /// <inheritdoc />
    public string TelemetryPrefix => _options.TelemetryPrefix;

    public ArgusMonitor(IOptions<ArgusMonitoringOptions> options)
    {
        _options = options.Value;
        _options.Validate();

        // Create meter with name: argus_api (for ArgusApi client metrics)
        // Cache meter-level tags from ArgusMonitorMeterTags to add to each datapoint
        // (Meter-level tags are scope attributes, not automatically added to datapoints)
        // Only add tags if ArgusMonitorMeterTags is explicitly configured
        _meterTags = new TagList();
        if (_options.ArgusMonitorMeterTags != null)
        {
            foreach (var tag in _options.ArgusMonitorMeterTags.ToDictionary())
            {
                _meterTags.Add(tag.Key, tag.Value);
            }
        }
        _meter = new Meter(
            name: ServiceCollectionExtensions.ArgusApiMeterName,
            version: null,
            tags: _meterTags);

        // Create heartbeat counter: argus_heartbeat_total
        // Use argus_ prefix for namespacing - all Argus metrics are easily filterable
        // Labels: worker
        _heartbeatCounter = _meter.CreateCounter<long>(
            "argus_heartbeat",
            unit: "count",
            description: "Heartbeat counter for background workers");

        // Create exception counter: argus_exceptions_total
        _exceptionCounter = _meter.CreateCounter<long>(
            "argus_exceptions",
            unit: "count",
            description: "Exception counter with type and context dimensions");

        // Create ActivitySource for tracing
        ActivitySource = new ActivitySource(_options.TelemetryPrefix);
    }

    /// <inheritdoc />
    public void Heartbeat(object worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var workerName = GetWorkerName(worker.GetType());
        RecordHeartbeat(workerName);
    }

    /// <inheritdoc />
    public void Heartbeat<T>()
    {
        var workerName = GetWorkerName(typeof(T));
        RecordHeartbeat(workerName);
    }

    /// <inheritdoc />
    public void Heartbeat(string componentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        RecordHeartbeat(componentName);
    }

    private void RecordHeartbeat(string workerName)
    {
        // Auto-register on first heartbeat
        _registeredWorkers.TryAdd(workerName, true);

        // Add worker name + meter tags (send_to_noc, payload, suppress_window)
        var tags = new TagList
        {
            { "worker", workerName }
        };
        // Add ArgusMonitorMeterTags to datapoint
        foreach (var tag in _meterTags)
        {
            tags.Add(tag);
        }

        _heartbeatCounter.Add(1, tags);
    }

    /// <inheritdoc />
    public void RecordException(object worker, Exception exception, string? operation = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(exception);

        var workerName = GetWorkerName(worker.GetType());
        RecordExceptionInternal(exception, workerName, operation);
    }

    /// <inheritdoc />
    public void RecordException(Exception exception, string? component = null, string? operation = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        RecordExceptionInternal(exception, component, operation);
    }

    private void RecordExceptionInternal(Exception exception, string? workerOrComponent, string? operation)
    {
        var tags = new TagList
        {
            { "type", exception.GetType().Name }
        };

        if (!string.IsNullOrEmpty(workerOrComponent))
            tags.Add("worker", workerOrComponent);

        if (!string.IsNullOrEmpty(operation))
            tags.Add("operation", operation);

        // Add ArgusMonitorMeterTags to datapoint
        foreach (var tag in _meterTags)
        {
            tags.Add(tag);
        }

        _exceptionCounter.Add(1, tags);
    }

    /// <inheritdoc />
    public Activity? StartTrace(string operationName)
    {
        return ActivitySource.StartActivity(operationName);
    }

    /// <inheritdoc />
    public Activity? StartTrace(string operationName, ActivityKind kind)
    {
        return ActivitySource.StartActivity(operationName, kind);
    }

    private static string GetWorkerName(Type type)
    {
        return type.Name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _meter.Dispose();
        ActivitySource.Dispose();
    }
}

