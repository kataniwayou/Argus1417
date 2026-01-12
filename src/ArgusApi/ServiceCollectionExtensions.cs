using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ArgusApi;

/// <summary>
/// Extension methods for registering Argus client wrapper services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The meter name and telemetry source identifier for ArgusApi client metrics.
    /// Used for meter registration and resource attribute tagging.
    /// </summary>
    public const string ArgusApiMeterName = "argus_api";
    /// <summary>
    /// Adds the Argus client wrapper with OpenTelemetry integration.
    /// Configures metrics, traces, and logs to export via gRPC to OTel Collector.
    /// This wrapper enables applications to integrate with Argus monitoring.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configuration action for options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddArgusClientWrapper(
        this IServiceCollection services,
        Action<ArgusMonitoringOptions> configure)
    {
        // Configure options
        services.Configure(configure);

        // Build options for immediate use in OTel setup
        var options = new ArgusMonitoringOptions();
        
        configure(options);
        options.Validate();

        // Build resource with service info and platform attribute
        // Resource attributes are attached to ALL metrics/traces/logs from this service
        var resourceAttributes = options.DefaultMeterTags.ToDictionary();
        resourceAttributes["platform"] = options.Platform;

        // Merge developer-provided resource attributes (overwrites defaults if same key)
        foreach (var attr in options.ResourceAttributes)
        {
            resourceAttributes[attr.Key] = attr.Value;
        }

        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName: options.TelemetryPrefix)
            .AddAttributes(resourceAttributes);

        // Configure OpenTelemetry Metrics
        services.AddOpenTelemetry()
            .WithMetrics(builder =>
            {
                builder
                    .SetResourceBuilder(resourceBuilder)
                    .AddMeter(ArgusApiMeterName) // Meter name for ArgusApi client metrics
                    .AddRuntimeInstrumentation(); // Add .NET runtime metrics (GC, ThreadPool, etc.)

                // Add any additional meters specified by the developer
                foreach (var meter in options.AdditionalMeters)
                {
                    builder.AddMeter(meter);
                }

                builder.AddOtlpExporter((exporterOptions, readerOptions) =>
                {
                    exporterOptions.Endpoint = new Uri(options.CollectorEndpoint);
                    exporterOptions.Protocol = OtlpExportProtocol.Grpc;
                    readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                        (int)options.MetricExportInterval.TotalMilliseconds;
                });
            })
            .WithTracing(builder =>
            {
                builder
                    .SetResourceBuilder(resourceBuilder)
                    .AddSource(options.TelemetryPrefix)
                    .AddOtlpExporter(exporterOptions =>
                    {
                        exporterOptions.Endpoint = new Uri(options.CollectorEndpoint);
                        exporterOptions.Protocol = OtlpExportProtocol.Grpc;
                    });
            });

        // Configure logging to use OTel and optionally Console
        services.AddLogging(logging =>
        {
            logging.ClearProviders();

            if (options.UseConsoleExporter)
            {
                logging.AddConsole(); // Log to console for visibility
            }

            logging.AddOpenTelemetry(otelLogging =>
            {
                otelLogging.SetResourceBuilder(resourceBuilder);
                otelLogging.IncludeFormattedMessage = true;
                otelLogging.IncludeScopes = true;
                otelLogging.AddOtlpExporter(exporterOptions =>
                {
                    exporterOptions.Endpoint = new Uri(options.CollectorEndpoint);
                    exporterOptions.Protocol = OtlpExportProtocol.Grpc;
                });
            });
        });

        // Register ArgusMonitor as singleton
        services.AddSingleton<IArgusMonitor, ArgusMonitor>();

        return services;
    }
}

