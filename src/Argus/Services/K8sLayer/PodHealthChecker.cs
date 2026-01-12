using Argus.Configuration;
using Argus.Models;
using Argus.Services.CentralTimer;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.K8sLayer;

/// <summary>
/// Performs 6-step health check on pods.
/// Uses CentralTimer.HeartbeatTimestamp for consistent timestamps across all operations within a tick.
/// </summary>
public class PodHealthChecker : IPodHealthChecker
{
    private readonly ILogger<PodHealthChecker> _logger;
    private readonly ICentralTimerService _centralTimer;
    private readonly IKubernetesClientWrapper _k8sClient;
    private readonly IRestartTracker _restartTracker;
    private readonly K8sLayerConfiguration _options;

    public PodHealthChecker(
        ILogger<PodHealthChecker> logger,
        ICentralTimerService centralTimer,
        IKubernetesClientWrapper k8sClient,
        IRestartTracker restartTracker,
        IOptions<ArgusConfiguration> options)
    {
        _logger = logger;
        _centralTimer = centralTimer;
        _k8sClient = k8sClient;
        _restartTracker = restartTracker;
        _options = options.Value.K8sLayer;
    }

    /// <summary>
    /// Check health of Prometheus pod
    /// </summary>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<PodHealthState> CheckPrometheusHealthAsync(
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        return await CheckPodHealthAsync(
            "prometheus",
            _options.PrometheusPod.LabelSelector,
            _options.PrometheusPod.ContainerName,
            correlationId,
            cancellationToken);
    }

    /// <summary>
    /// Check health of KSM pod
    /// </summary>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<PodHealthState> CheckKsmHealthAsync(
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        return await CheckPodHealthAsync(
            "ksm",
            _options.KsmPod.LabelSelector,
            _options.KsmPod.ContainerName,
            correlationId,
            cancellationToken);
    }

    private async Task<PodHealthState> CheckPodHealthAsync(
        string podId,
        string labelSelector,
        string containerName,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var state = new PodHealthState { Timestamp = _centralTimer.HeartbeatTimestamp };

        // Get pods from K8s API
        var pods = await _k8sClient.GetPodsAsync(labelSelector, correlationId, cancellationToken);

        if (pods == null)
        {
            // K8s API unavailable - treat as healthy/resolved (will generate CANCEL alert)
            state.Status = PodStatus.Healthy;
            state.StatusReason = "Kubernetes API unavailable - treating as resolved";
            _logger.LogWarning(
                "Cannot check {PodId} health: K8s API unavailable, treating as resolved. CorrelationId={CorrelationId}",
                podId, correlationId);
            return state;
        }

        // Step 1: Pod exists
        state.HealthChecks.PodExists = pods.Items.Count > 0;
        if (!state.HealthChecks.PodExists)
        {
            state.Status = PodStatus.Down;
            state.StatusReason = $"No pods found matching selector '{labelSelector}'";
            _logger.LogWarning(
                "{PodId} health check failed: {Reason}. CorrelationId={CorrelationId}",
                podId, state.StatusReason, correlationId);
            return state;
        }

        // Use first pod (typically there's only one)
        var pod = pods.Items[0];
        state.PodName = pod.Metadata.Name;

        // Step 2: Pod phase is Running
        state.PodPhase = pod.Status.Phase;
        state.HealthChecks.PodPhaseRunning = pod.Status.Phase == "Running";

        // Step 3: Not terminating
        state.DeletionTimestamp = pod.Metadata.DeletionTimestamp;
        state.HealthChecks.NotTerminating = pod.Metadata.DeletionTimestamp == null;

        // Find the target container
        var container = pod.Status.ContainerStatuses?
            .FirstOrDefault(c => c.Name == containerName);

        if (container == null)
        {
            state.ContainerState = ContainerState.Unknown;
            state.Status = PodStatus.Down;
            state.StatusReason = $"Container '{containerName}' not found in pod";
            _logger.LogWarning(
                "{PodId} health check failed: {Reason}. CorrelationId={CorrelationId}",
                podId, state.StatusReason, correlationId);
            return state;
        }

        // Step 4: Container ready
        state.ContainerReady = container.Ready;
        state.HealthChecks.ContainerReady = container.Ready;

        // Step 5: Container state is running
        state.ContainerState = GetContainerState(container.State);
        state.HealthChecks.ContainerRunning = state.ContainerState == ContainerState.Running;

        // Step 6: Restart stability
        state.RestartCount = container.RestartCount;
        var (restartsInWindow, isStable) = _restartTracker.RecordRestartCount(podId, container.RestartCount);
        state.RestartsInWindow = restartsInWindow;
        state.RestartWindow = _restartTracker.GetRestartWindow(podId);
        state.HealthChecks.RestartStable = isStable;

        // Determine overall status
        state.Status = DetermineStatus(state);
        state.StatusReason = BuildStatusReason(state);

        _logger.LogDebug(
            "{PodId} health check complete: Status={Status}, Reason={Reason}. CorrelationId={CorrelationId}",
            podId, state.Status, state.StatusReason, correlationId);

        return state;
    }

    private static ContainerState GetContainerState(V1ContainerState? containerState)
    {
        if (containerState?.Running != null) return ContainerState.Running;
        if (containerState?.Waiting != null) return ContainerState.Waiting;
        if (containerState?.Terminated != null) return ContainerState.Terminated;
        return ContainerState.Unknown;
    }

    private static PodStatus DetermineStatus(PodHealthState state)
    {
        var checks = state.HealthChecks;
        
        if (!checks.PodExists || !checks.PodPhaseRunning || !checks.NotTerminating ||
            !checks.ContainerReady || !checks.ContainerRunning)
        {
            return PodStatus.Down;
        }

        if (!checks.RestartStable)
        {
            return PodStatus.Unstable;
        }

        return PodStatus.Healthy;
    }

    private static string BuildStatusReason(PodHealthState state) =>
        state.Status switch
        {
            PodStatus.Healthy => "All health checks passed",
            PodStatus.Down when !state.HealthChecks.PodExists => "Pod does not exist",
            PodStatus.Down when !state.HealthChecks.PodPhaseRunning => $"Pod phase is {state.PodPhase}",
            PodStatus.Down when !state.HealthChecks.NotTerminating => "Pod is terminating",
            PodStatus.Down when !state.HealthChecks.ContainerReady => "Container not ready",
            PodStatus.Down when !state.HealthChecks.ContainerRunning => $"Container state: {state.ContainerState}",
            PodStatus.Unstable => $"Restart storm: {state.RestartsInWindow} restarts in window",
            _ => "Unknown"
        };
}

/// <summary>
/// Interface for pod health checker
/// </summary>
public interface IPodHealthChecker
{
    /// <summary>
    /// Check health of Prometheus pod
    /// </summary>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<PodHealthState> CheckPrometheusHealthAsync(string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check health of KSM pod
    /// </summary>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<PodHealthState> CheckKsmHealthAsync(string correlationId, CancellationToken cancellationToken = default);
}

