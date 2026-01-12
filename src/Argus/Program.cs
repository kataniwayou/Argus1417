using System.Net;
using System.Net.Sockets;
using Argus.Configuration;
using Argus.Models;
using Argus.Services.AlertsVector;
using Argus.Services.CentralTimer;
using Argus.Services.Coordinator;
using Argus.Services.K8sLayer;
using Argus.Services.LeaderElection;
using Argus.Services.Metrics;
using Argus.Services.Noc;
using Argus.Services.Watchdog;
using ArgusApi;

var builder = WebApplication.CreateBuilder(args);

// Configure options
builder.Services.Configure<ArgusConfiguration>(
    builder.Configuration.GetSection(ArgusConfiguration.SectionName));
builder.Services.Configure<K8sLayerConfiguration>(
    builder.Configuration.GetSection($"{ArgusConfiguration.SectionName}:K8sLayer"));
builder.Services.Configure<WatchdogConfiguration>(
    builder.Configuration.GetSection($"{ArgusConfiguration.SectionName}:Watchdog"));
builder.Services.Configure<DefaultNocConfiguration>(
    builder.Configuration.GetSection($"{ArgusConfiguration.SectionName}:DefaultNoc"));
builder.Services.Configure<AlertsVectorConfiguration>(
    builder.Configuration.GetSection($"{ArgusConfiguration.SectionName}:AlertsVector"));
builder.Services.Configure<NocHttpClientConfiguration>(
    builder.Configuration.GetSection($"{ArgusConfiguration.SectionName}:Noc:HttpClient"));

// ============================================================
// NOC HTTP Client Configuration with SocketsHttpHandler
// ============================================================

// Get NOC HTTP client configuration
var nocHttpConfig = builder.Configuration
    .GetSection($"{ArgusConfiguration.SectionName}:Noc:HttpClient")
    .Get<NocHttpClientConfiguration>() ?? new NocHttpClientConfiguration();

// Register NOC HTTP client with SocketsHttpHandler
builder.Services.AddHttpClient<INocHttpClient, NocHttpClient>()
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var handler = new SocketsHttpHandler();

        // SSL certificate bypass (for development/internal networks)
        if (nocHttpConfig.BypassSslValidation)
        {
            handler.SslOptions.RemoteCertificateValidationCallback =
                (sender, certificate, chain, errors) => true;
        }

        // IP address override (bypasses DNS resolution)
        // Useful for port-forwarding scenarios with IPv4/IPv6 localhost issues
        if (!string.IsNullOrEmpty(nocHttpConfig.ConnectIpAddress))
        {
            var connectIp = IPAddress.Parse(nocHttpConfig.ConnectIpAddress);
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(connectIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(
                        new IPEndPoint(connectIp, nocHttpConfig.ConnectPort),
                        cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        return handler;
    });

// ============================================================
// OpenTelemetry Configuration
// ============================================================

// Read OTel Collector endpoint from configuration (env var: OpenTelemetry__CollectorEndpoint)
var collectorEndpoint = builder.Configuration["OpenTelemetry:CollectorEndpoint"] ?? "http://localhost:4317";

// Add Argus client wrapper with OTel integration
// Uses telemetry_source="argus_service" to prevent circular monitoring
builder.Services.AddArgusClientWrapper(options =>
{
    options.CompositeKey = "argus_service";
    options.CollectorEndpoint = collectorEndpoint;
    options.MetricExportInterval = TimeSpan.FromSeconds(15);
    options.Platform = "custom"; // Override platform to exclude from Argus platform monitoring
    options.AdditionalMeters.Add(ArgusMetrics.MeterName); // Our custom Argus metrics
    options.UseConsoleExporter = true; // Enable console logging for debugging
});

// Register Metrics service (must be before other services that depend on it)
builder.Services.AddSingleton<IArgusMetrics, ArgusMetrics>();

// Register LivenessVector service (must be before CentralTimer that depends on it)
builder.Services.AddSingleton<ILivenessVectorService, LivenessVectorService>();

// Register Central Timer service (system heartbeat - THE fundamental component)
builder.Services.AddSingleton<CentralTimerService>();
builder.Services.AddSingleton<ICentralTimerService>(sp => sp.GetRequiredService<CentralTimerService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CentralTimerService>());

// Register Leader Election service (uses CentralTimer callback)
builder.Services.AddSingleton<LeaderElectionService>();
builder.Services.AddSingleton<ILeaderElectionService>(sp => sp.GetRequiredService<LeaderElectionService>());

// Register unified Heartbeat service (combines file + HTTP, checks LivenessVector health)
builder.Services.AddSingleton<IHeartbeatService, HeartbeatService>();

// Register StatusFileSystem service (monitors destination path accessibility)
builder.Services.AddSingleton<IStatusFileSystemService, StatusFileSystemService>();

// Register K8s Layer services
builder.Services.AddSingleton<IRestartTracker, RestartTracker>();
builder.Services.AddSingleton<IKubernetesClientWrapper, KubernetesClientWrapper>();
builder.Services.AddSingleton<IPodHealthChecker, PodHealthChecker>();
builder.Services.AddSingleton<IK8sLayerService, K8sLayerService>();

// Register Alerts Vector service (in-memory)
builder.Services.AddSingleton<IAlertsVectorService, AlertsVectorService>();

// Register Watchdog service
builder.Services.AddSingleton<IWatchdogService, WatchdogService>();

// Register NOC services
builder.Services.AddSingleton<ISuppressionCache, SuppressionCache>();
builder.Services.AddSingleton<INocHealthService, NocHealthService>();
builder.Services.AddSingleton<INocQueueService, NocQueueService>();
builder.Services.AddHostedService<NocQueueService>(sp => (NocQueueService)sp.GetRequiredService<INocQueueService>());
builder.Services.AddSingleton<INocSnapshotService, NocSnapshotService>();

// Register ArgusCoordinator (central coordinator with real-time alerts vector)
builder.Services.AddSingleton<ArgusCoordinator>();
builder.Services.AddSingleton<IArgusCoordinator>(sp => sp.GetRequiredService<ArgusCoordinator>());

// Add OpenAPI
builder.Services.AddOpenApi();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Argus starting");

// Start HeartbeatService (unified file + HTTP, registers with CentralTimer)
var heartbeatService = app.Services.GetRequiredService<IHeartbeatService>();
heartbeatService.Start();

// Start StatusFileSystemService (registers with CentralTimer)
var statusFileSystemService = app.Services.GetRequiredService<IStatusFileSystemService>();
statusFileSystemService.Start();

// Start WatchdogService (registers with CentralTimer for tick-based expiration)
var watchdogService = app.Services.GetRequiredService<IWatchdogService>();
watchdogService.Start();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Only use HTTPS redirection in production when HTTPS is configured
// In Kubernetes, TLS termination is typically handled by ingress
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Production"))
{
    app.UseHttpsRedirection();
}

// ============================================================
// K8s Layer Endpoints
// ============================================================

// K8s Layer health endpoint - returns Kubernetes infrastructure health
app.MapGet("/api/k8s/health", async (
    IK8sLayerService k8sLayerService,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    var correlationId = httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault()
        ?? $"poll-{Guid.NewGuid().ToString("N")[..8]}";

    var state = await k8sLayerService.GetStateAsync(correlationId, ct);

    httpContext.Response.Headers["X-Correlation-ID"] = correlationId;

    return Results.Ok(state);
})
.WithName("GetK8sLayerHealth")
.WithOpenApi();

// ============================================================
// ArgusCoordinator Endpoints (PUSH model)
// ============================================================

// Alertmanager-compatible receiver endpoint (API v2)
// Prometheus pushes alerts here (configured via alerting.alertmanagers with api_version: v2)
//
// Developers can also use this endpoint to send custom alerts programmatically.
//
// Supported annotations:
//   - suppress_window: Controls duplicate notification suppression
//     Formats (unit suffix REQUIRED): "120s" (seconds), "5m" (minutes), "2h" (hours), "1d" (days)
//     Plain numbers without units (e.g., "120") are NOT supported
//     If not specified, uses default from configuration (10m)
//   - summary: Short summary of the alert
//   - description: Detailed description
//   - payload: Additional context (e.g., "component=api,type=availability,severity=high")
//
// Required labels:
//   - alertname: Name of the alert
//   - platform: Must be "argus" for alert to be processed
//   - priority: Numeric priority (0 = highest for Prometheus alerts)
// HTTP push is event-driven (not tick-driven), so no correlationId.
// ExecutionId is generated per alert inside ReceiveAlerts.
app.MapPost("/api/v2/alerts", (
    IArgusCoordinator coordinator,
    List<PrometheusAlertDto> alertDtos) =>
{
    var alerts = alertDtos.Select(dto => dto.ToAlert()).ToList();

    coordinator.ReceiveAlerts(alerts);

    // Alertmanager expects empty 200 response
    return Results.Ok();
})
.WithName("ReceiveAlerts")
.WithOpenApi();

// ArgusCoordinator health endpoint - returns unified state
app.MapGet("/api/health", (IArgusCoordinator coordinator) =>
{
    var state = coordinator.GetState();
    return Results.Ok(state);
})
.WithName("GetArgusHealth")
.WithOpenApi();

// Watchdog status endpoint
app.MapGet("/api/watchdog", (IArgusCoordinator coordinator) =>
{
    return Results.Ok(coordinator.GetWatchdogState());
})
.WithName("GetWatchdogStatus")
.WithOpenApi();

// Alerts vector snapshot endpoint
app.MapGet("/api/alerts", (IAlertsVectorService alertsVector) =>
{
    var snapshot = alertsVector.GetSnapshot();
    return Results.Ok(snapshot);
})
.WithName("GetAlertsVector")
.WithOpenApi();

// ============================================================
// Kubernetes Probe Endpoints
// ============================================================

// Liveness probe - simple check that the process is alive
// If this fails, Kubernetes will restart the pod
app.MapGet("/livez", () => Results.Ok(new { status = "alive" }))
.WithName("Liveness")
.WithOpenApi();

// Readiness probe - check that the app is ready to receive traffic
// If this fails, Kubernetes removes the pod from service endpoints
app.MapGet("/readyz", (IArgusCoordinator coordinator) =>
{
    try
    {
        var watchdogState = coordinator.GetWatchdogState();
        return Results.Ok(new { status = "ready", watchdog = watchdogState.Status.ToString() });
    }
    catch
    {
        return Results.StatusCode(503);
    }
})
.WithName("Readiness")
.WithOpenApi();

// ============================================================
// Prometheus Metrics Endpoint (DEPRECATED)
// ============================================================

// DEPRECATED: Metrics are now exported via OpenTelemetry to the OTel Collector
// Prometheus should scrape metrics from the OTel Collector, not this endpoint
// This endpoint is kept for backward compatibility only
app.MapGet("/metrics", (IArgusMetrics metrics) =>
{
    var metricsText = metrics.GetPrometheusMetrics();
    return Results.Text(metricsText, "text/plain; version=0.0.4; charset=utf-8");
})
.WithName("Metrics")
.WithOpenApi();

app.Run();
