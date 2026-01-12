namespace Argus.Models;

/// <summary>
/// Unified state from the ArgusCoordinator.
/// </summary>
public class ArgusState
{
    /// <summary>
    /// Correlation ID for tracing
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// K8s Layer state (Kubernetes infrastructure health)
    /// </summary>
    public K8sLayerState K8sLayer { get; set; } = new();

    /// <summary>
    /// Watchdog state (Prometheus heartbeat)
    /// </summary>
    public WatchdogState Watchdog { get; set; } = new();

    /// <summary>
    /// List of currently active alerts
    /// </summary>
    public List<Alert> ActiveAlerts { get; set; } = new();

    /// <summary>
    /// Overall system status
    /// </summary>
    public ArgusStatus Status { get; set; } = ArgusStatus.Unknown;

    /// <summary>
    /// Human-readable status reason
    /// </summary>
    public string StatusReason { get; set; } = string.Empty;

    /// <summary>
    /// Total count of alerts received since startup
    /// </summary>
    public long TotalAlertsReceived { get; set; }

    /// <summary>
    /// Total count of alerts filtered (no argus=true label)
    /// </summary>
    public long TotalAlertsFiltered { get; set; }

    /// <summary>
    /// Last time alerts were received from Prometheus
    /// </summary>
    public DateTime? LastAlertReceivedAt { get; set; }

    /// <summary>
    /// Timestamp of this state snapshot
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Calculate overall status based on K8s layer and watchdog states
    /// </summary>
    public void CalculateOverallStatus()
    {
        // If K8s Layer is critical, the whole system is compromised
        if (K8sLayer.Status == K8sLayerStatus.Critical)
        {
            Status = ArgusStatus.Critical;
            StatusReason = "K8s Layer critical: Prometheus infrastructure is down";
            return;
        }

        // If K8s Layer is unknown, we can't verify infrastructure
        if (K8sLayer.Status == K8sLayerStatus.Unknown)
        {
            Status = ArgusStatus.Unknown;
            StatusReason = "K8s Layer unknown: Cannot verify Prometheus infrastructure";
            return;
        }

        // If watchdog is missing, Prometheus may not be functioning
        if (Watchdog.Status == WatchdogStatus.Missing)
        {
            Status = ArgusStatus.Critical;
            StatusReason = "Watchdog missing: Prometheus may be down or not sending alerts";
            return;
        }

        // If K8s Layer is degraded
        if (K8sLayer.Status == K8sLayerStatus.Degraded)
        {
            Status = ArgusStatus.Degraded;
            StatusReason = K8sLayer.StatusReason;
            return;
        }

        // If there are active alerts
        if (ActiveAlerts.Count > 0)
        {
            Status = ArgusStatus.Alerting;
            StatusReason = $"{ActiveAlerts.Count} active alert(s)";
            return;
        }

        // All healthy
        Status = ArgusStatus.Healthy;
        StatusReason = "All systems healthy";
    }
}

/// <summary>
/// Overall Argus status
/// </summary>
public enum ArgusStatus
{
    /// <summary>
    /// Status is unknown (startup, no data)
    /// </summary>
    Unknown,

    /// <summary>
    /// All systems are healthy
    /// </summary>
    Healthy,

    /// <summary>
    /// Active alerts but infrastructure is healthy
    /// </summary>
    Alerting,

    /// <summary>
    /// K8s Layer is degraded
    /// </summary>
    Degraded,

    /// <summary>
    /// Critical issue - K8s Layer down or watchdog missing
    /// </summary>
    Critical
}

