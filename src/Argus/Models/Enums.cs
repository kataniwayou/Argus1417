namespace Argus.Models;

/// <summary>
/// Individual pod health status
/// </summary>
public enum PodStatus
{
    /// <summary>All 6 health checks pass</summary>
    Healthy,

    /// <summary>Pod is not running or container not ready</summary>
    Down,

    /// <summary>Pod is running but restart rate exceeds threshold</summary>
    Unstable,

    /// <summary>Cannot determine status (K8s API unavailable)</summary>
    Unknown
}

/// <summary>
/// Combined K8s Layer status based on Prometheus and KSM pod states
/// </summary>
public enum K8sLayerStatus
{
    /// <summary>Both Prometheus and KSM are healthy</summary>
    Healthy,

    /// <summary>Prometheus healthy, KSM down</summary>
    Degraded,

    /// <summary>Prometheus healthy, KSM unknown</summary>
    Partial,

    /// <summary>Prometheus down</summary>
    Critical,

    /// <summary>Cannot determine status</summary>
    Unknown
}

/// <summary>
/// Priority levels for alerts
/// </summary>
public enum Priority
{
    /// <summary>P0 - Immediate action required</summary>
    Critical,

    /// <summary>P1 - High priority</summary>
    High,

    /// <summary>P2 - Normal priority</summary>
    Normal,

    /// <summary>No priority (healthy state)</summary>
    None
}

/// <summary>
/// Container running state
/// </summary>
public enum ContainerState
{
    Running,
    Waiting,
    Terminated,
    Unknown
}

/// <summary>
/// Watchdog heartbeat status
/// </summary>
public enum WatchdogStatus
{
    /// <summary>Watchdog received within timeout window</summary>
    Healthy,

    /// <summary>Watchdog not received within timeout window</summary>
    Missing,

    /// <summary>Startup grace period active, waiting for first watchdog</summary>
    Initializing
}

