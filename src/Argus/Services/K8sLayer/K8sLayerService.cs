using Argus.Configuration;
using Argus.Models;
using Argus.Services.CentralTimer;
using Argus.Services.Metrics;
using Argus.Utilities;
using Microsoft.Extensions.Options;

namespace Argus.Services.K8sLayer;

/// <summary>
/// K8s Layer service that orchestrates health checks for Prometheus and KSM pods.
/// Uses CentralTimer.HeartbeatTimestamp for consistent timestamps across all operations within a tick.
/// </summary>
public class K8sLayerService : IK8sLayerService
{
    private readonly ILogger<K8sLayerService> _logger;
    private readonly ICentralTimerService _centralTimer;
    private readonly IKubernetesClientWrapper _k8sClient;
    private readonly IPodHealthChecker _podHealthChecker;
    private readonly IRestartTracker _restartTracker;
    private readonly IArgusMetrics _metrics;
    private readonly K8sLayerConfiguration _config;

    private K8sLayerState? _previousState;
    private readonly object _lock = new();

    public K8sLayerService(
        ILogger<K8sLayerService> logger,
        ICentralTimerService centralTimer,
        IKubernetesClientWrapper k8sClient,
        IPodHealthChecker podHealthChecker,
        IRestartTracker restartTracker,
        IArgusMetrics metrics,
        IOptions<K8sLayerConfiguration> config)
    {
        _logger = logger;
        _centralTimer = centralTimer;
        _k8sClient = k8sClient;
        _podHealthChecker = podHealthChecker;
        _restartTracker = restartTracker;
        _metrics = metrics;
        _config = config.Value;
    }

    /// <summary>
    /// Get current K8s Layer state by checking K8s API, Prometheus, and KSM pods
    /// </summary>
    /// <param name="correlationId">Correlation ID for tracing this request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<K8sLayerState> GetStateAsync(string correlationId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting K8s Layer health check. CorrelationId={CorrelationId}", correlationId);

        // Check all three components in parallel (independent checks)
        var apiTask = _k8sClient.CheckApiAvailabilityAsync(correlationId, cancellationToken);
        var prometheusTask = _podHealthChecker.CheckPrometheusHealthAsync(correlationId, cancellationToken);
        var ksmTask = _podHealthChecker.CheckKsmHealthAsync(correlationId, cancellationToken);

        await Task.WhenAll(apiTask, prometheusTask, ksmTask);

        var apiAvailable = await apiTask;
        var state = new K8sLayerState
        {
            CorrelationId = correlationId,
            ApiAvailable = apiAvailable,
            ApiUnavailableReason = apiAvailable ? null : "Kubernetes API server is not responding",
            Prometheus = await prometheusTask,
            Ksm = await ksmTask,
            RestartTrackingGracePeriodActive = _restartTracker.IsGracePeriodActive,
            Timestamp = _centralTimer.HeartbeatTimestamp // Consistent with tick cycle
        };

        // Calculate combined status
        state.CalculateCombinedStatus();

        // Update metrics for K8s layer component status
        _metrics.SetK8sApiAvailable(state.ApiAvailable);
        _metrics.SetPrometheusPodHealthy(state.Prometheus.Status == PodStatus.Healthy);
        _metrics.SetKsmPodHealthy(state.Ksm.Status == PodStatus.Healthy);

        // Check if state changed
        bool stateChanged = HasStateChanged(state);
        if (stateChanged)
        {
            _logger.LogInformation(
                "K8s Layer state changed: Status={Status}, Reason={Reason}. CorrelationId={CorrelationId}",
                state.Status, state.StatusReason, correlationId);
        }

        // Update previous state
        lock (_lock)
        {
            _previousState = state;
        }

        _logger.LogDebug(
            "K8s Layer health check complete: API={ApiStatus}, Prometheus={PrometheusStatus}, KSM={KsmStatus}, Combined={Status}. CorrelationId={CorrelationId}",
            state.ApiAvailable ? "Available" : "Unavailable",
            state.Prometheus.Status, state.Ksm.Status, state.Status, correlationId);

        return state;
    }

    /// <summary>
    /// Check if current state differs from previous state
    /// </summary>
    public bool HasStateChanged(K8sLayerState currentState)
    {
        lock (_lock)
        {
            if (_previousState == null) return true;

            return _previousState.ApiAvailable != currentState.ApiAvailable ||
                   _previousState.Status != currentState.Status ||
                   _previousState.Prometheus.Status != currentState.Prometheus.Status ||
                   _previousState.Ksm.Status != currentState.Ksm.Status;
        }
    }

    /// <summary>
    /// Get the previous K8s Layer state (for comparison)
    /// </summary>
    public K8sLayerState? GetPreviousState()
    {
        lock (_lock)
        {
            return _previousState;
        }
    }

    /// <summary>
    /// Generate AlertDto objects for current K8s layer state.
    /// Always generates alerts (even if healthy) to update the alerts vector.
    /// </summary>
    /// <param name="state">Current K8s layer state</param>
    /// <param name="executionId">Optional execution ID to assign to all alerts in this polling cycle</param>
    public List<AlertDto> GenerateAlerts(K8sLayerState state, string? executionId = null)
    {
        var alerts = new List<AlertDto>();

        // K8s API availability alert (Priority -10)
        alerts.Add(GenerateApiAlert(
            state.ApiAvailable,
            state.ApiUnavailableReason,
            priority: -10,
            name: "K8sApiDown",
            fingerprint: "k8s-layer-api",
            timestamp: state.Timestamp,
            executionId: executionId));

        // Prometheus pod alert (Priority -9)
        alerts.Add(GeneratePodAlert(
            state.Prometheus,
            _config.PrometheusPod,
            priority: -9,
            name: "PrometheusDown",
            fingerprint: "k8s-layer-prometheus",
            timestamp: state.Timestamp,
            executionId: executionId));

        // KSM pod alert (Priority -8)
        alerts.Add(GeneratePodAlert(
            state.Ksm,
            _config.KsmPod,
            priority: -8,
            name: "KSMDown",
            fingerprint: "k8s-layer-ksm",
            timestamp: state.Timestamp,
            executionId: executionId));

        return alerts;
    }

    /// <summary>
    /// Generate an AlertDto for K8s API availability
    /// </summary>
    private AlertDto GenerateApiAlert(
        bool apiAvailable,
        string? unavailableReason,
        int priority,
        string name,
        string fingerprint,
        DateTime timestamp,
        string? executionId)
    {
        var alert = new AlertDto
        {
            Priority = priority,
            Name = name,
            Fingerprint = fingerprint,
            Source = "K8sLayer",
            Timestamp = timestamp,
            ExecutionId = executionId ?? string.Empty
        };

        if (apiAvailable)
        {
            // API is available - generate CANCEL alert
            var cancelConfig = _config.K8sApi.CancelNocBehavior;
            alert.Status = AlertStatus.CANCEL;
            alert.Summary = "Kubernetes API server is available";
            alert.Description = "K8s API server is responding normally";
            alert.Payload = cancelConfig.Payload.Clone();
            alert.SendToNoc = cancelConfig.SendToNoc;
            alert.SuppressWindow = ParseSuppressWindow(cancelConfig.SuppressWindow);
        }
        else
        {
            // API is down - generate CREATE alert
            var createConfig = _config.K8sApi.CreateNocBehavior;
            alert.Status = AlertStatus.CREATE;
            alert.Summary = "Kubernetes API server is unavailable";
            alert.Description = unavailableReason ?? "K8s API server is not responding";
            alert.Payload = createConfig.Payload.Clone();
            alert.SendToNoc = createConfig.SendToNoc;
            alert.SuppressWindow = ParseSuppressWindow(createConfig.SuppressWindow);
        }

        // Apply runtime overrides (level, message, source, suppressionKey)
        alert.Payload.ApplyAlertOverrides(alert);

        return alert;
    }

    /// <summary>
    /// Generate an AlertDto for a pod based on its health state
    /// </summary>
    private AlertDto GeneratePodAlert(
        PodHealthState podState,
        PodMonitorConfiguration config,
        int priority,
        string name,
        string fingerprint,
        DateTime timestamp,
        string? executionId)
    {
        var alert = new AlertDto
        {
            Priority = priority,
            Name = name,
            Fingerprint = fingerprint,
            Source = "K8sLayer",
            Timestamp = timestamp,
            ExecutionId = executionId ?? string.Empty
        };

        if (podState.Status == PodStatus.Healthy)
        {
            // Healthy state generates CANCEL alert
            var cancelConfig = config.CancelNocBehavior;
            alert.Status = AlertStatus.CANCEL;
            alert.Summary = $"{name.Replace("Down", "")} pod is healthy";
            alert.Description = $"Pod '{podState.PodName}' is running normally";
            alert.Payload = cancelConfig.Payload.Clone();
            alert.SendToNoc = cancelConfig.SendToNoc;
            alert.SuppressWindow = ParseSuppressWindow(cancelConfig.SuppressWindow);
        }
        else // Down, Unstable
        {
            var createConfig = config.CreateNocBehavior;
            alert.Status = AlertStatus.CREATE;
            alert.Summary = $"{name.Replace("Down", "")} pod is {podState.Status}";
            alert.Description = podState.StatusReason;
            alert.Payload = createConfig.Payload.Clone();
            alert.SendToNoc = createConfig.SendToNoc;
            alert.SuppressWindow = ParseSuppressWindow(createConfig.SuppressWindow);
        }

        // Apply runtime overrides (level, message, source, suppressionKey)
        alert.Payload.ApplyAlertOverrides(alert);

        return alert;
    }

    /// <summary>
    /// Parse suppress window string to TimeSpan. Returns null if invalid.
    /// </summary>
    private static TimeSpan? ParseSuppressWindow(string? suppressWindow)
    {
        if (string.IsNullOrWhiteSpace(suppressWindow))
            return null;

        if (TimeSpanParser.TryParseToTimeSpan(suppressWindow, out var timeSpan))
            return timeSpan;

        return null;
    }
}

/// <summary>
/// Interface for K8s Layer service
/// </summary>
public interface IK8sLayerService
{
    /// <summary>
    /// Get current K8s Layer state by checking both Prometheus and KSM pods
    /// </summary>
    /// <param name="correlationId">Correlation ID for tracing this request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<K8sLayerState> GetStateAsync(string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if current state differs from previous state
    /// </summary>
    bool HasStateChanged(K8sLayerState currentState);

    /// <summary>
    /// Get the previous K8s Layer state (for comparison)
    /// </summary>
    K8sLayerState? GetPreviousState();

    /// <summary>
    /// Generate AlertDto objects for current K8s layer state.
    /// Always generates alerts (even if healthy) to update the alerts vector.
    /// </summary>
    /// <param name="state">Current K8s layer state</param>
    /// <param name="executionId">Optional execution ID to assign to all alerts in this polling cycle</param>
    List<AlertDto> GenerateAlerts(K8sLayerState state, string? executionId = null);
}

