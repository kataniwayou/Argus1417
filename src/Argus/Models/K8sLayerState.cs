namespace Argus.Models;

/// <summary>
/// Complete K8s Layer state representing Kubernetes infrastructure health
/// </summary>
public class K8sLayerState
{
    /// <summary>
    /// Correlation ID for tracing this request through the system.
    /// Format: alert-{8 hex}, poll-{8 hex}, or watchdog-{8 hex}
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>Whether the K8s API server is available</summary>
    public bool ApiAvailable { get; set; } = true;

    /// <summary>Reason if API is unavailable</summary>
    public string? ApiUnavailableReason { get; set; }

    /// <summary>Health state of the Prometheus pod</summary>
    public PodHealthState Prometheus { get; set; } = new();

    /// <summary>Health state of the KSM pod</summary>
    public PodHealthState Ksm { get; set; } = new();

    /// <summary>Whether restart tracking grace period is active</summary>
    public bool RestartTrackingGracePeriodActive { get; set; }

    /// <summary>Combined K8s Layer status based on both pods</summary>
    public K8sLayerStatus Status { get; set; } = K8sLayerStatus.Unknown;

    /// <summary>Priority level based on current status</summary>
    public Priority Priority { get; set; } = Priority.None;

    /// <summary>Data source for this state</summary>
    public string Source { get; set; } = "kubernetes_api";

    /// <summary>Timestamp when this state was captured</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Reason for current status</summary>
    public string StatusReason { get; set; } = string.Empty;

    /// <summary>
    /// Calculate combined status based on Prometheus and KSM pod states
    /// </summary>
    public void CalculateCombinedStatus()
    {
        // Prometheus DOWN → CRITICAL (P0)
        if (Prometheus.Status == PodStatus.Down)
        {
            Status = K8sLayerStatus.Critical;
            Priority = Priority.Critical;
            StatusReason = $"Prometheus pod is down: {Prometheus.StatusReason}";
            return;
        }

        // Prometheus UNKNOWN → UNKNOWN (P0)
        if (Prometheus.Status == PodStatus.Unknown)
        {
            Status = K8sLayerStatus.Unknown;
            Priority = Priority.Critical;
            StatusReason = $"Cannot determine Prometheus status: {Prometheus.StatusReason}";
            return;
        }

        // Prometheus UNSTABLE → CRITICAL (P0)
        if (Prometheus.Status == PodStatus.Unstable)
        {
            Status = K8sLayerStatus.Critical;
            Priority = Priority.Critical;
            StatusReason = $"Prometheus pod is unstable: {Prometheus.StatusReason}";
            return;
        }

        // Prometheus HEALTHY, check KSM
        if (Ksm.Status == PodStatus.Down)
        {
            Status = K8sLayerStatus.Degraded;
            Priority = Priority.High;
            StatusReason = $"KSM pod is down: {Ksm.StatusReason}";
            return;
        }

        if (Ksm.Status == PodStatus.Unknown)
        {
            Status = K8sLayerStatus.Partial;
            Priority = Priority.High;
            StatusReason = $"Cannot determine KSM status: {Ksm.StatusReason}";
            return;
        }

        if (Ksm.Status == PodStatus.Unstable)
        {
            Status = K8sLayerStatus.Degraded;
            Priority = Priority.Normal;
            StatusReason = $"KSM pod is unstable: {Ksm.StatusReason}";
            return;
        }

        // Both HEALTHY
        Status = K8sLayerStatus.Healthy;
        Priority = Priority.None;
        StatusReason = "Both Prometheus and KSM pods are healthy";
    }
}

