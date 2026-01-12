using Argus.Configuration;
using Argus.Models;
using Argus.Services.AlertsVector;
using Argus.Services.CentralTimer;
using Argus.Services.K8sLayer;
using Argus.Services.Metrics;
using Argus.Services.Noc;
using Argus.Services.Watchdog;
using Microsoft.Extensions.Options;

namespace Argus.Services.Coordinator;

/// <summary>
/// ArgusCoordinator - Central coordinator for Argus monitoring.
///
/// Real-time Design - Alerts vector updated asynchronously:
///
/// Alert Sources:
/// 1. K8s Layer (polling) - Updates Prometheus/KSM alerts on configured interval
/// 2. Prometheus Alerts (push) - Updates on alert arrival (platform="argus" label only)
/// 3. Watchdog (one-shot timer) - Updates when alert arrives (CANCEL) or timer expires (CREATE)
///
/// NOC Snapshots:
/// - Taken on configured interval (default: 30 seconds)
/// - Processes alerts vector in priority order
/// - All snapshots logged at DEBUG level
///
/// Each callback is responsible for stamping LivenessVector before exit (on success/failure, not exception).
/// </summary>
public class ArgusCoordinator : IArgusCoordinator, IDisposable
{
    private const string K8sPollingCallbackName = "K8sPolling";
    private const string NocSnapshotCallbackName = "NocSnapshot";

    private readonly ILogger<ArgusCoordinator> _logger;
    private readonly ICentralTimerService _centralTimer;
    private readonly IK8sLayerService _k8sLayerService;
    private readonly IWatchdogService _watchdogService;
    private readonly IAlertsVectorService _alertsVector;
    private readonly INocSnapshotService _nocSnapshot;
    private readonly IArgusMetrics _metrics;
    private readonly ILivenessVectorService _livenessVector;
    private readonly CoordinatorConfiguration _coordinatorConfig;
    private readonly K8sLayerConfiguration _k8sConfig;
    private readonly WatchdogConfiguration _watchdogConfig;

    private bool _disposed = false;

    // Statistics (kept for backward compatibility, also tracked in metrics)
    private DateTime? _lastAlertReceivedAt;
    private readonly object _statsLock = new();

    // Store interval ticks for LivenessVector stamping
    private int _pollingIntervalTicks;
    private int _snapshotIntervalTicks;

    public ArgusCoordinator(
        ILogger<ArgusCoordinator> logger,
        ICentralTimerService centralTimer,
        IK8sLayerService k8sLayerService,
        IWatchdogService watchdogService,
        IAlertsVectorService alertsVector,
        INocSnapshotService nocSnapshot,
        IArgusMetrics metrics,
        ILivenessVectorService livenessVector,
        IOptions<ArgusConfiguration> config)
    {
        _logger = logger;
        _centralTimer = centralTimer;
        _k8sLayerService = k8sLayerService;
        _watchdogService = watchdogService;
        _alertsVector = alertsVector;
        _nocSnapshot = nocSnapshot;
        _metrics = metrics;
        _livenessVector = livenessVector;
        _coordinatorConfig = config.Value.Coordinator;
        _k8sConfig = config.Value.K8sLayer;
        _watchdogConfig = config.Value.Watchdog;

        _logger.LogInformation(
            "ArgusCoordinator started. K8s polling: {Polling}s, Snapshot interval: {Snapshot}s, Watchdog timeout: {Timeout}s, Grace period: {GracePeriod}s",
            _k8sConfig.PollingIntervalSeconds, _coordinatorConfig.SnapshotIntervalSeconds, _watchdogConfig.TimeoutSeconds, _centralTimer.GracePeriodSeconds);

        // Register callbacks with Central Timer
        RegisterCallbacks();
    }

    /// <summary>
    /// Register callbacks with the Central Timer
    /// </summary>
    private void RegisterCallbacks()
    {
        // K8s polling callback - fires on configured interval (not grace period aware - always polls)
        _pollingIntervalTicks = _k8sConfig.PollingIntervalSeconds / _centralTimer.TickIntervalSeconds;
        _centralTimer.RegisterCallback(new CentralTimerCallback(
            Name: K8sPollingCallbackName,
            IntervalTicks: _pollingIntervalTicks,
            Callback: OnPollingTickAsync,
            IsGracePeriodAware: false));

        // NOC snapshot callback - fires on configured interval (grace period aware - skips during grace)
        _snapshotIntervalTicks = _coordinatorConfig.SnapshotIntervalSeconds / _centralTimer.TickIntervalSeconds;
        _centralTimer.RegisterCallback(new CentralTimerCallback(
            Name: NocSnapshotCallbackName,
            IntervalTicks: _snapshotIntervalTicks,
            Callback: OnSnapshotTickAsync,
            IsGracePeriodAware: true));

        _logger.LogInformation(
            "Registered callbacks: K8sPolling (every {PollingTicks} ticks), NocSnapshot (every {SnapshotTicks} ticks, grace period aware)",
            _pollingIntervalTicks, _snapshotIntervalTicks);
    }

    private async Task OnPollingTickAsync(long tick, string correlationId, CancellationToken stoppingToken)
    {
        // correlationId received from CentralTimer (single source of truth for tick correlation)
        // Generate execution ID at the start of polling cycle - all alerts from this cycle share it
        var executionId = GenerateExecutionId();
        var startTime = DateTime.UtcNow;
        try
        {
            var k8sState = await _k8sLayerService.GetStateAsync(correlationId, stoppingToken);
            var alerts = _k8sLayerService.GenerateAlerts(k8sState, executionId);

            foreach (var alert in alerts)
            {
                // Track K8s layer alerts
                _metrics.IncrementAlertsReceived("k8s_layer");
                _alertsVector.UpdateAlert(alert);
            }

            // Record polling duration
            _metrics.RecordK8sPollDuration(DateTime.UtcNow - startTime);

            _logger.LogDebug(
                "K8s polling complete: {AlertCount} alerts updated. Tick={Tick} CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                alerts.Count, tick, correlationId, executionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "K8s polling failed. Tick={Tick} CorrelationId={CorrelationId} ExecutionId={ExecutionId}", tick, correlationId, executionId);
        }
        finally
        {
            // Always stamp - callback executed (didn't silently die)
            _livenessVector.RecordExecution(K8sPollingCallbackName, _pollingIntervalTicks, tick);
        }
    }

    private Task OnSnapshotTickAsync(long tick, string correlationId, CancellationToken stoppingToken)
    {
        // correlationId received from CentralTimer (single source of truth for tick correlation)
        try
        {
            _nocSnapshot.TakeSnapshot(correlationId);
            _logger.LogDebug("NOC snapshot complete. Tick={Tick} CorrelationId={CorrelationId}", tick, correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NOC snapshot failed. Tick={Tick} CorrelationId={CorrelationId}", tick, correlationId);
        }
        finally
        {
            // Always stamp - callback executed (didn't silently die)
            _livenessVector.RecordExecution(NocSnapshotCallbackName, _snapshotIntervalTicks, tick);
        }
        return Task.CompletedTask;
    }

    #region Execution ID Generation

    /// <summary>
    /// Generate a unique execution ID for tracking an alert through its lifecycle.
    /// Format: exec-{8-char-guid}
    /// </summary>
    private static string GenerateExecutionId() =>
        $"exec-{Guid.NewGuid().ToString("N")[..8]}";

    #endregion

    #region IArgusCoordinator Implementation

    /// <inheritdoc />
    public void ReceiveAlerts(IEnumerable<Alert> alerts)
    {
        // HTTP push is event-driven (not tick-driven), so no correlationId.
        // ExecutionId is generated per alert inside ProcessAlert.
        var alertList = alerts.ToList();

        lock (_statsLock)
        {
            _lastAlertReceivedAt = _centralTimer.HeartbeatTimestamp;
        }

        foreach (var alert in alertList)
        {
            // Track received alert (source: prometheus_push)
            _metrics.IncrementAlertsReceived("prometheus_push");

            // Check platform label - only process alerts with platform="argus"
            // Alerts without this label or with different value are completely ignored (not even logged)
            if (!alert.ShouldBeProcessed())
            {
                _metrics.IncrementAlertsFiltered();
                continue;
            }

            // platform=argus - process the alert
            ProcessAlert(alert);
        }
    }

    private void ProcessAlert(Alert alert)
    {
        // Generate execution ID for this alert (tracks it through its entire lifecycle)
        var executionId = GenerateExecutionId();
        alert.ExecutionId = executionId;

        // Check if this is a watchdog alert (special handling)
        if (alert.Name.Equals(_watchdogConfig.AlertName, StringComparison.OrdinalIgnoreCase))
        {
            if (alert.IsFiring)
            {
                // Record heartbeat - this will reset the timer and update alerts vector with CANCEL
                // Note: Watchdog heartbeat generates its own execution ID when it updates the alert
                _watchdogService.RecordHeartbeat();
                _logger.LogDebug(
                    "Watchdog alert received: 1 alert updated (heartbeat recorded, timer reset). ExecutionId={ExecutionId}",
                    executionId);
            }
            return;
        }

        // Convert to AlertDto and update vector (ExecutionId is copied in ToAlertDto)
        var alertDto = alert.ToAlertDto();
        _alertsVector.UpdateAlert(alertDto);

        _logger.LogDebug(
            "Prometheus alert received: Name={Name} Priority={Priority} Status={Status} Fingerprint={Fingerprint} ExecutionId={ExecutionId}",
            alertDto.Name, alertDto.Priority, alertDto.Status, alertDto.Fingerprint, executionId);
    }

    /// <inheritdoc />
    public ArgusState GetState()
    {
        var metricsSnapshot = _metrics.GetSnapshot();
        lock (_statsLock)
        {
            return new ArgusState
            {
                TotalAlertsReceived = metricsSnapshot.TotalAlertsReceived,
                TotalAlertsFiltered = metricsSnapshot.TotalAlertsFiltered,
                LastAlertReceivedAt = _lastAlertReceivedAt,
                Watchdog = _watchdogService.GetState()
            };
        }
    }

    /// <inheritdoc />
    public WatchdogState GetWatchdogState() => _watchdogService.GetState();

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;

        // Unregister callbacks from Central Timer
        _centralTimer.UnregisterCallback("K8sPolling");
        _centralTimer.UnregisterCallback("NocSnapshot");
        _centralTimer.UnregisterCallback("WatchdogCheck");

        _disposed = true;
        _logger.LogInformation("ArgusCoordinator disposed");
    }

    #endregion
}

