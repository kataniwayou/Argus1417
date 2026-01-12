using Argus.Models;

namespace Argus.Services.Metrics;

/// <summary>
/// Interface for Argus system metrics.
/// Tracks ingestion, lifecycle, NOC decisions, and infrastructure health.
/// </summary>
public interface IArgusMetrics
{
    #region Ingestion Metrics

    /// <summary>
    /// Increment total alerts received counter
    /// </summary>
    /// <param name="source">Alert source: k8s_layer, prometheus_push, watchdog</param>
    void IncrementAlertsReceived(string source);

    /// <summary>
    /// Increment filtered alerts counter (no platform=argus label)
    /// </summary>
    void IncrementAlertsFiltered();

    #endregion

    #region Alert Lifecycle Metrics

    /// <summary>
    /// Increment alerts that entered CREATE status
    /// </summary>
    void IncrementAlertsCreated();

    /// <summary>
    /// Increment alerts that were resolved (CANCEL processed)
    /// </summary>
    void IncrementAlertsResolved();

    #endregion

    #region Alert Vector State

    /// <summary>
    /// Update current alerts vector size gauge
    /// </summary>
    void SetAlertsVectorSize(int size);

    /// <summary>
    /// Update current alerts count by status gauge
    /// </summary>
    void SetAlertsVectorByStatus(AlertStatus status, int count);

    #endregion

    #region NOC Decision Metrics

    /// <summary>
    /// Increment NOC decisions enqueued by type
    /// </summary>
    void IncrementNocDecisions(NocDecisionType type);

    /// <summary>
    /// Increment successfully sent to NOC
    /// </summary>
    void IncrementNocSent();

    /// <summary>
    /// Increment suppressed by suppression cache
    /// </summary>
    void IncrementNocSuppressed();

    /// <summary>
    /// Update current NOC queue depth gauge
    /// </summary>
    void SetNocQueueDepth(int depth);

    #endregion

    #region Infrastructure Health Metrics

    /// <summary>
    /// Record K8s polling duration
    /// </summary>
    void RecordK8sPollDuration(TimeSpan duration);

    /// <summary>
    /// Record snapshot processing duration
    /// </summary>
    void RecordSnapshotDuration(TimeSpan duration);

    /// <summary>
    /// Update grace period active gauge (1=active, 0=expired)
    /// </summary>
    void SetGracePeriodActive(bool active);

    /// <summary>
    /// Set K8s API availability status (1=available, 0=unavailable)
    /// </summary>
    void SetK8sApiAvailable(bool available);

    /// <summary>
    /// Set Prometheus pod status (1=healthy, 0=down/unstable)
    /// </summary>
    void SetPrometheusPodHealthy(bool healthy);

    /// <summary>
    /// Set KSM pod status (1=healthy, 0=down/unstable)
    /// </summary>
    void SetKsmPodHealthy(bool healthy);

    /// <summary>
    /// Set leader election status (1=leader, 0=follower)
    /// </summary>
    void SetLeaderElectionState(bool isLeader);

    #endregion

    #region Central Timer Metrics

    /// <summary>
    /// Update central timer tick count gauge
    /// </summary>
    void SetCentralTimerTickCount(long tickCount);

    /// <summary>
    /// Record callback execution duration
    /// </summary>
    /// <param name="callbackName">Name of the callback</param>
    /// <param name="duration">Execution duration</param>
    void RecordCallbackDuration(string callbackName, TimeSpan duration);

    /// <summary>
    /// Increment callback error counter
    /// </summary>
    /// <param name="callbackName">Name of the callback that failed</param>
    void IncrementCallbackError(string callbackName);

    /// <summary>
    /// Increment callback skipped counter (when previous execution still running)
    /// </summary>
    /// <param name="callbackName">Name of the callback that was skipped</param>
    void IncrementCallbackSkipped(string callbackName);

    /// <summary>
    /// Set external monitor status (1=OK, 0=expired)
    /// </summary>
    void SetExternalMonitorStatus(bool isOk);

    /// <summary>
    /// Set external monitor heartbeat age in seconds
    /// </summary>
    void SetExternalMonitorHeartbeatAge(double ageSeconds);

    #endregion

    #region Liveness Vector Metrics

    /// <summary>
    /// Set liveness vector healthy status (1=all healthy, 0=degraded)
    /// </summary>
    void SetLivenessVectorHealthy(bool isHealthy);

    /// <summary>
    /// Set liveness vector size (number of callbacks tracked)
    /// </summary>
    void SetLivenessVectorSize(int size);

    /// <summary>
    /// Set count of unhealthy callbacks
    /// </summary>
    void SetLivenessUnhealthyCallbackCount(int count);

    #endregion

    #region Snapshot Accessors

    /// <summary>
    /// Get current metrics snapshot for reporting
    /// </summary>
    ArgusMetricsSnapshot GetSnapshot();

    /// <summary>
    /// Get metrics in Prometheus text exposition format
    /// </summary>
    string GetPrometheusMetrics();

    #endregion
}

/// <summary>
/// Snapshot of current metrics values for reporting
/// </summary>
public class ArgusMetricsSnapshot
{
    // Ingestion
    public long TotalAlertsReceived { get; set; }
    public long TotalAlertsFiltered { get; set; }
    public Dictionary<string, long> AlertsReceivedBySource { get; set; } = new();

    // Lifecycle
    public long TotalAlertsCreated { get; set; }
    public long TotalAlertsResolved { get; set; }

    // Vector State
    public int AlertsVectorSize { get; set; }
    public Dictionary<AlertStatus, int> AlertsVectorByStatus { get; set; } = new();

    // NOC
    public long TotalNocDecisions { get; set; }
    public Dictionary<NocDecisionType, long> NocDecisionsByType { get; set; } = new();
    public long TotalNocSent { get; set; }
    public long TotalNocSuppressed { get; set; }
    public int NocQueueDepth { get; set; }

    // Health
    public bool GracePeriodActive { get; set; }
    public TimeSpan LastK8sPollDuration { get; set; }
    public TimeSpan LastSnapshotDuration { get; set; }

    // K8s Layer Component Status
    public bool K8sApiAvailable { get; set; }
    public bool PrometheusPodHealthy { get; set; }
    public bool KsmPodHealthy { get; set; }

    // Leader Election
    public bool IsLeader { get; set; }

    // Central Timer
    public long CentralTimerTickCount { get; set; }
    public bool ExternalMonitorOk { get; set; }
    public double ExternalMonitorHeartbeatAge { get; set; }

    // Liveness Vector
    public bool LivenessVectorHealthy { get; set; }
    public int LivenessVectorSize { get; set; }
    public int LivenessUnhealthyCallbackCount { get; set; }
}
