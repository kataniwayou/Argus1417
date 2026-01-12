namespace ArgusApi;

/// <summary>
/// Configuration options for Argus monitoring.
/// </summary>
public class ArgusMonitoringOptions
{
    /// <summary>
    /// Composite key for identifying the service instance.
    /// Used for K8s pod labeling correlation: argus.io/composite-key
    /// Example: "orderservice_v1" -> TelemetryPrefix becomes "argus_orderservice_v1"
    /// </summary>
    public string CompositeKey { get; set; } = string.Empty;

    /// <summary>
    /// OpenTelemetry Collector endpoint (gRPC).
    /// </summary>
    public string CollectorEndpoint { get; set; } = "http://localhost:4317";

    /// <summary>
    /// Interval for exporting metrics to the collector.
    /// </summary>
    public TimeSpan MetricExportInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether to add console exporter for logs (in addition to OTLP).
    /// Default is true for visibility during development.
    /// </summary>
    public bool UseConsoleExporter { get; set; } = false;

    /// <summary>
    /// Additional meter names to capture. The "argus" meter is always included.
    /// Use this to export custom application metrics via OTel.
    /// Example: "MyApp.Business", "MyApp.Performance"
    /// </summary>
    public List<string> AdditionalMeters { get; set; } = new();

    /// <summary>
    /// Additional resource attributes to include with all telemetry data.
    /// These are merged with the default resource attributes (platform, service.name).
    /// Use this to add custom metadata that should be attached to all metrics/traces/logs.
    /// Example: { "environment", "production" }, { "region", "us-east-1" }
    /// </summary>
    public Dictionary<string, object> ResourceAttributes { get; set; } = new();

    /// <summary>
    /// Platform identifier for all telemetry from this service.
    /// Default is "argus" which enables Argus platform monitoring and alert rules.
    /// The OTel Collector uses this to apply platform-specific transforms and defaults.
    /// </summary>
    public string Platform { get; set; } = "argus";

    /// <summary>
    /// Meter-level tags for default metrics (.NET runtime: GC, ThreadPool, etc.).
    /// These tags are applied via OTel Collector transform to runtime metrics.
    /// Configure SendToNoc, Payload, SuppressWindow for alert settings.
    /// </summary>
    public ArgusMeterTags DefaultMeterTags { get; set; } = new();

    /// <summary>
    /// Meter-level tags for ArgusMonitor metrics (argus_heartbeat, argus_exception).
    /// These tags are attached directly to the ArgusMeter (argus_api).
    /// Configure SendToNoc, Payload, SuppressWindow for alert settings.
    /// When null (default), no additional tags are added to heartbeat/exception metrics.
    /// </summary>
    public ArgusMeterTags? ArgusMonitorMeterTags { get; set; } = null;

    /// <summary>
    /// Gets the normalized composite key (lowercase, no spaces, no dots).
    /// </summary>
    internal string NormalizedCompositeKey => CompositeKey
        .ToLowerInvariant()
        .Replace(" ", "")
        .Replace("-", "_")
        .Replace(".", "_");

    /// <summary>
    /// Gets the telemetry prefix: argus_{compositeKey}
    /// </summary>
    internal string TelemetryPrefix => $"argus_{NormalizedCompositeKey}";

    /// <summary>
    /// Validates the options.
    /// </summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(CompositeKey))
            throw new ArgumentException("CompositeKey is required", nameof(CompositeKey));

        if (string.IsNullOrWhiteSpace(CollectorEndpoint))
            throw new ArgumentException("CollectorEndpoint is required", nameof(CollectorEndpoint));
    }
}

