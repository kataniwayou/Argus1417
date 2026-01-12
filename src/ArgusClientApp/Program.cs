using ArgusApi;
using ArgusClientApp.Services;
using ArgusClientApp.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Build the host with Argus monitoring
var builder = Host.CreateApplicationBuilder(args);

// Configure logging level to Information (filter out Debug noise)
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Add Argus client wrapper with OTel integration (includes Console + OTel logging)
// CompositeKey should match K8s pod label: argus.io/composite-key
// Read CollectorEndpoint from configuration (env var: ArgusApi__CollectorEndpoint)
var collectorEndpoint = builder.Configuration["ArgusApi:CollectorEndpoint"] ?? "http://localhost:4317";
builder.Services.AddArgusClientWrapper(options =>
{
    options.CompositeKey = "argusclientapp_v2";
    options.CollectorEndpoint = collectorEndpoint;
    options.MetricExportInterval = TimeSpan.FromSeconds(15);
    options.Platform = "argus";

    // Configure DefaultMeterTags (applied to .NET runtime metrics via OTel Collector transform)
    options.DefaultMeterTags = new ArgusMeterTags
    {
        SendToNoc = true,
        Payload = new NocPayload
        {
            Custom1 = "ArgusTeam",
            Custom2 = "RuntimeMetrics",
            HostName = "ArgusClientApp",
            Level = 3,
            Message = "Runtime metrics alert",
            Severity = "",
            Source = "runtime-metrics",
            SuppressionKey = "runtime-metrics",
            Visible = true
        },
        SuppressWindow = "1m"
    };

    // Configure ArgusMonitorMeterTags (applied to argus_heartbeat and argus_exception metrics)
    options.ArgusMonitorMeterTags = new ArgusMeterTags
    {
        SendToNoc = true,
        Payload = new NocPayload
        {
            Custom1 = "ArgusTeam",
            Custom2 = "ArgusMonitor",
            HostName = "ArgusClientApp",
            Level = 3,
            Message = "Heartbeat/exception alert",
            Severity = "",
            Source = "team-alpha-oncall",
            SuppressionKey = "argus-monitor",
            Visible = true
        },
        SuppressWindow = "5m"
    };

    // Add custom meters for business metrics (not coupled to Argus NOC alerts)
    options.AdditionalMeters.Add("ArgusClientApp.Business");
});

// Register services
builder.Services.AddSingleton<OrderProcessingService>();

// Register background workers
builder.Services.AddHostedService<HeartbeatWorker>();
builder.Services.AddHostedService<SimpleLoopWorker>();
builder.Services.AddHostedService<CustomMetricsWorker>();
builder.Services.AddHostedService<NonArgusWorker>(); // Example: worker not inheriting from ArgusBackgroundService

var host = builder.Build();

// Get logger and monitor for startup message
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var monitor = host.Services.GetRequiredService<IArgusMonitor>();

logger.LogInformation(
    "ArgusClientApp starting. TelemetryPrefix={Prefix}, CollectorEndpoint={Endpoint}",
    monitor.TelemetryPrefix,
    collectorEndpoint);

// Run the host
await host.RunAsync();
