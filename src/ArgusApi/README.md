# ArgusApi

A .NET library for integrating applications with the Argus monitoring platform. Provides OpenTelemetry-based metrics, traces, and logs export with automatic heartbeat monitoring for background workers.

## Installation

```bash
dotnet add package ArgusApi
```

## Quick Start

```csharp
using ArgusApi;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddArgusClientWrapper(options =>
{
    options.CompositeKey = "myapp_v1";
    options.Payload = "component=myapp,severity=high";
    options.SendToNoc = true;
    options.SuppressWindow = "5m";
    options.CollectorEndpoint = "http://otel-collector:4317";
});

builder.Services.AddHostedService<MyWorker>();
```

## Configuration Options

### ArgusMonitoringOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `CompositeKey` | `string` | `""` | **Required.** Unique identifier for the service. Used for K8s pod label correlation (`argus.io/composite-key`). Example: `"orderservice_v1"` |
| `Payload` | `string` | `"default_payload"` | Metadata included in all metrics. Passed to alert annotations. Example: `"component=orders,type=heartbeat,severity=high"` |
| `SendToNoc` | `bool` | `false` | When `true`, alerts are sent to NOC. When `false`, alerts are generated but not forwarded. |
| `SuppressWindow` | `string` | `"1m"` | Duration to suppress duplicate alerts. Examples: `"5m"`, `"15m"`, `"1h"`. |
| `CollectorEndpoint` | `string` | `"http://localhost:4317"` | OpenTelemetry Collector gRPC endpoint. |
| `MetricExportInterval` | `TimeSpan` | `15 seconds` | How often metrics are exported to the collector. |
| `UseConsoleExporter` | `bool` | `true` | Also log to console (useful for development). |
| `AdditionalMeters` | `List<string>` | `[]` | Custom meter names to export. The `"argus"` meter is always included. |
| `Platform` | `string` | `"argus"` | Platform identifier. The OTel Collector uses this to apply platform-specific transforms. |
| `ResourceAttributes` | `Dictionary<string, object>` | `{}` | Additional resource attributes for all telemetry. Merged with defaults (can override `platform`). |

## Services

### IArgusMonitor

Injected as a singleton. Use for heartbeats, exception recording, and tracing.

#### Heartbeat Methods

```csharp
// From a worker (uses type name)
_monitor.Heartbeat(this);

// Generic version
_monitor.Heartbeat<MyWorker>();

// From non-worker context (custom component name)
_monitor.Heartbeat("OrderProcessingService");
```

#### Exception Recording

```csharp
// From a worker context
_monitor.RecordException(this, exception, "ProcessOrder");

// From non-worker context
_monitor.RecordException(exception, "PaymentService", "ChargeCard");
```

#### Tracing

```csharp
// Start a trace span (auto-completes with 'using')
using var activity = _monitor.StartTrace("ProcessOrder");

// With activity kind
using var activity = _monitor.StartTrace("CallExternalApi", ActivityKind.Client);

// Access underlying ActivitySource for advanced scenarios
var source = _monitor.ActivitySource;
```

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `ActivitySource` | `ActivitySource` | Underlying activity source for advanced tracing |
| `TelemetryPrefix` | `string` | The telemetry prefix: `argus_{compositeKey}` |

## ArgusBackgroundService

Base class for background workers with **automatic** heartbeat and exception monitoring.

### Basic Usage

```csharp
public class MyWorker : ArgusBackgroundService
{
    public MyWorker(IArgusMonitor monitor, ILogger<MyWorker> logger)
        : base(monitor, logger) { }

    protected override async Task DoWorkAsync(CancellationToken stoppingToken)
    {
        // Your work logic here
        // Heartbeat is sent automatically at the start of each cycle
        // Exceptions are recorded automatically
    }
}
```

### Overridable Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Interval` | `TimeSpan` | `30 seconds` | Time between work cycles |
| `ContinueOnException` | `bool` | `true` | Continue running after exceptions |

### Overridable Methods

| Method | Description |
|--------|-------------|
| `DoWorkAsync(CancellationToken)` | **Required.** Your work logic, called repeatedly. |
| `OnStartAsync(CancellationToken)` | Called once when worker starts. |
| `OnStopAsync(CancellationToken)` | Called once when worker stops. |
| `OnExceptionAsync(Exception, CancellationToken)` | Custom exception handling. |

### Protected Members

| Member | Type | Description |
|--------|------|-------------|
| `WorkerName` | `string` | Worker name (derived from type name) |
| `Monitor` | `IArgusMonitor` | Access to the Argus monitor |

## Custom Metrics

Export your own application metrics alongside Argus metrics:

```csharp
// 1. Register additional meters
builder.Services.AddArgusClientWrapper(options =>
{
    options.CompositeKey = "myapp_v1";
    options.AdditionalMeters.Add("MyApp.Business");
});

// 2. Create and use custom metrics
public class OrderService
{
    private readonly Meter _meter = new("MyApp.Business");
    private readonly Counter<long> _ordersCounter;

    public OrderService()
    {
        _ordersCounter = _meter.CreateCounter<long>("orders_processed");
    }

    public void ProcessOrder()
    {
        // Add platform="custom" to exclude from Argus monitoring
        _ordersCounter.Add(1, new TagList
        {
            { "region", "us-east" },
            { "platform", "custom" }  // Excludes from Argus alerts
        });
    }
}
```

### Monitored vs Unmonitored Metrics

By default, all metrics have `platform="argus"` (from resource attributes), which makes them eligible for Argus alert rules. To create metrics that are exported to Prometheus/Grafana but **not** monitored by Argus alerts, add `platform="custom"` to the TagList:

| Scenario | TagList | Result |
|----------|---------|--------|
| Monitored (default) | `new TagList { }` | `platform="argus"` - Triggers alerts |
| Unmonitored | `new TagList { { "platform", "custom" } }` | `platform="custom"` - No alerts |

> **Note:** TagList values take precedence over resource attributes. The OTel Collector only sets resource attributes if the TagList doesn't already provide them.

## Metrics Reference

### Argus Metrics (automatic)

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `argus_heartbeat_count_total` | Counter | `composite_key`, `worker`, `payload`, `send_to_noc`, `suppress_window` | Heartbeat counter |
| `argus_exceptions_count_total` | Counter | `composite_key`, `worker`, `type`, `operation`, `payload`, `send_to_noc`, `suppress_window` | Exception counter |

### Runtime Metrics (automatic)

.NET runtime metrics are automatically included (GC, ThreadPool, etc.).

## Resource Attributes

Resource attributes are metadata attached to **all** telemetry (metrics, traces, logs) from your service. They appear as labels in Prometheus and can be used for filtering and grouping.

### Default Resource Attributes

| Attribute | Default Value | Description |
|-----------|---------------|-------------|
| `platform` | `"argus"` | Enables Argus platform monitoring. OTel Collector uses this to apply transforms. |
| `service.name` | `argus_{compositeKey}` | Service identifier (set automatically from CompositeKey). |

### Overriding Resource Attributes

```csharp
builder.Services.AddArgusClientWrapper(options =>
{
    options.CompositeKey = "myapp_v1";

    // Override the platform (disables Argus-specific OTel Collector transforms)
    options.Platform = "custom";

    // Add custom resource attributes
    options.ResourceAttributes = new Dictionary<string, object>
    {
        { "environment", "production" },
        { "region", "us-east-1" },
        { "team", "payments" }
    };
});
```

### Precedence

1. `options.Platform` sets the default `platform` attribute
2. `options.ResourceAttributes` values are merged (can override `platform` if same key is provided)

### Platform Values

| Platform | OTel Collector Behavior |
|----------|------------------------|
| `"argus"` (default) | Adds `platform=argus` label to all datapoints. Enables Argus alert rules. |
| `"custom"` | Adds `platform=custom` label. Metrics exported to Prometheus but not monitored by Argus alerts. |

## Kubernetes Integration

Set the OTel Collector endpoint via environment variable:

```yaml
env:
- name: ArgusApi__CollectorEndpoint
  value: "http://otel-collector:4317"
```

Ensure your pod has the composite key label:

```yaml
labels:
  argus.io/composite-key: myapp_v1
```

