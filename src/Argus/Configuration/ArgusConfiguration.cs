namespace Argus.Configuration;

/// <summary>
/// Root configuration for Argus monitoring
/// </summary>
public class ArgusConfiguration
{
    public const string SectionName = "Argus";

    /// <summary>Coordinator configuration for snapshot intervals</summary>
    public CoordinatorConfiguration Coordinator { get; set; } = new();

    /// <summary>K8s Layer configuration for Kubernetes infrastructure monitoring</summary>
    public K8sLayerConfiguration K8sLayer { get; set; } = new();

    /// <summary>Watchdog heartbeat configuration</summary>
    public WatchdogConfiguration Watchdog { get; set; } = new();

    /// <summary>StatusFileSystem monitoring configuration</summary>
    public StatusFileSystemConfiguration StatusFileSystem { get; set; } = new();

    /// <summary>Default NOC behavior for Prometheus alerts</summary>
    public DefaultNocConfiguration DefaultNoc { get; set; } = new();

    /// <summary>Alerts vector configuration</summary>
    public AlertsVectorConfiguration AlertsVector { get; set; } = new();

    /// <summary>Leader election configuration for HA deployments</summary>
    public LeaderElectionConfiguration LeaderElection { get; set; } = new();

    /// <summary>Central timer configuration - the system tick engine</summary>
    public CentralTimerConfiguration CentralTimer { get; set; } = new();

    /// <summary>Heartbeat configuration for file and HTTP heartbeats</summary>
    public HeartbeatConfiguration Heartbeat { get; set; } = new();

    /// <summary>Unified NOC configuration (HttpClient + CircuitBreaker)</summary>
    public NocConfiguration Noc { get; set; } = new();
}

/// <summary>
/// Configuration for the alerts vector service
/// </summary>
public class AlertsVectorConfiguration
{
    /// <summary>
    /// TTL (Time-To-Live) for CREATE alerts in the vector.
    /// Alerts with CREATE status that haven't been refreshed within this duration
    /// will be automatically removed to prevent stale alerts.
    /// Format: "<number><unit>" where unit is s, m, h, or d (e.g., "1h", "30m", "2d")
    /// Default: 1 hour
    /// </summary>
    public string AlertTtl { get; set; } = "1h";
}

