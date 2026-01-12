using Argus.Models;

namespace Argus.Configuration;

/// <summary>
/// K8s Layer configuration - Kubernetes Infrastructure State monitoring
/// </summary>
public class K8sLayerConfiguration
{
    public KubernetesConfiguration Kubernetes { get; set; } = new();
    public PodMonitorConfiguration K8sApi { get; set; } = new();
    public PodMonitorConfiguration PrometheusPod { get; set; } = new();
    public PodMonitorConfiguration KsmPod { get; set; } = new();
    public RestartTrackingConfiguration RestartTracking { get; set; } = new();
    public int PollingIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Kubernetes connection configuration
/// </summary>
public class KubernetesConfiguration
{
    public string Namespace { get; set; } = "argus";
    public int ApiTimeoutSeconds { get; set; } = 30;
    public bool UseInClusterConfig { get; set; } = true;
}

/// <summary>
/// NOC behavior configuration for a specific alert status.
/// Payload is a NocHttpPayload object that will be sent to the NOC API.
/// At runtime, level/message/source/suppressionKey are always overridden from AlertDto.
/// </summary>
public class NocBehaviorConfiguration
{
    /// <summary>Whether alerts should be sent to NOC</summary>
    public bool SendToNoc { get; set; } = true;

    /// <summary>
    /// Payload template for NOC HTTP POST requests.
    /// At runtime, the following fields are always overridden from AlertDto:
    /// - level: 3=CREATE, 1=CANCEL
    /// - message: from AlertDto.Description
    /// - source: from AlertDto.Source
    /// - suppressionKey: from AlertDto.Fingerprint
    /// </summary>
    public NocHttpPayload Payload { get; set; } = new();

    /// <summary>Suppression window for alerts (e.g., "2m", "5m")</summary>
    public string SuppressWindow { get; set; } = "5m";
}

/// <summary>
/// Pod monitoring configuration with alert settings
/// </summary>
public class PodMonitorConfiguration
{
    public string LabelSelector { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>NOC behavior when pod is DOWN/UNSTABLE (AlertStatus.CREATE)</summary>
    public NocBehaviorConfiguration CreateNocBehavior { get; set; } = new();

    /// <summary>NOC behavior when pod is healthy or API unavailable (AlertStatus.CANCEL)</summary>
    public NocBehaviorConfiguration CancelNocBehavior { get; set; } = new();
}

/// <summary>
/// Restart tracking sliding window configuration
/// </summary>
public class RestartTrackingConfiguration
{
    public int WindowSize { get; set; } = 5;
    public int RestartThreshold { get; set; } = 3;
}

/// <summary>
/// Watchdog heartbeat configuration
/// </summary>
public class WatchdogConfiguration
{
    /// <summary>Name of the watchdog alert from Prometheus</summary>
    public string AlertName { get; set; } = "Watchdog";

    /// <summary>Timeout in seconds before watchdog is considered missing</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>NOC behavior when watchdog expires (AlertStatus.CREATE)</summary>
    public NocBehaviorConfiguration CreateNocBehavior { get; set; } = new()
    {
        SendToNoc = true,
        Payload = new NocHttpPayload
        {
            Custom1 = "ArgusTeam",
            Custom2 = "Watchdog",
            HostName = "ArgusServer",
            Level = 3,
            Message = "Watchdog heartbeat expired - Prometheus may be down",
            Severity = "",
            Source = "watchdog",
            SuppressionKey = "watchdog",
            Visible = true
        },
        SuppressWindow = "2m"
    };

    /// <summary>NOC behavior when watchdog is healthy (AlertStatus.CANCEL)</summary>
    public NocBehaviorConfiguration CancelNocBehavior { get; set; } = new()
    {
        SendToNoc = true,
        Payload = new NocHttpPayload
        {
            Custom1 = "ArgusTeam",
            Custom2 = "Watchdog",
            HostName = "ArgusServer",
            Level = 0,
            Message = "Watchdog heartbeat restored - Prometheus is healthy",
            Severity = "",
            Source = "watchdog",
            SuppressionKey = "watchdog",
            Visible = true
        },
        SuppressWindow = "5m"
    };
}
