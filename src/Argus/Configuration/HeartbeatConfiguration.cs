using Argus.Models;

namespace Argus.Configuration;

/// <summary>
/// Configuration for Argus heartbeat functionality.
/// Supports file-based and HTTP-based heartbeats.
/// </summary>
public class HeartbeatConfiguration
{
    /// <summary>
    /// How often to send heartbeats (in seconds).
    /// Applies to both file and HTTP heartbeats.
    /// Default: 30 seconds
    /// </summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>
    /// File heartbeat configuration.
    /// Writes a JSON status file to prove Argus is alive.
    /// </summary>
    public FileHeartbeatConfiguration File { get; set; } = new();

    /// <summary>
    /// HTTP heartbeat configuration.
    /// Sends HTTP POST to NOC endpoint.
    /// </summary>
    public HttpHeartbeatConfiguration Http { get; set; } = new();
}

/// <summary>
/// Configuration for file-based heartbeat.
/// </summary>
public class FileHeartbeatConfiguration
{
    /// <summary>
    /// Whether file heartbeat is enabled.
    /// Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Destination path for the heartbeat status file.
    /// Only the leader writes to this path.
    /// </summary>
    public string DestinationPath { get; set; } = "/var/argus/heartbeat_status.json";
}

/// <summary>
/// Configuration for HTTP-based heartbeat.
/// Uses same endpoint as NOC alerts (SendEndpoint).
/// </summary>
public class HttpHeartbeatConfiguration
{
    /// <summary>
    /// Whether HTTP heartbeat is enabled.
    /// Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Payload to send with the HTTP heartbeat.
    /// Uses NocHttpPayload structure for consistency with NOC alerts.
    /// </summary>
    public NocHttpPayload Payload { get; set; } = new()
    {
        Custom1 = "ArgusTeam",
        Custom2 = "Heartbeat",
        HostName = "ArgusServer",
        Level = 1,
        Message = "Argus heartbeat",
        Severity = "",
        Source = "argus-heartbeat",
        SuppressionKey = "argus-heartbeat",
        Visible = true
    };
}

/// <summary>
/// Configuration for StatusFileSystem monitoring.
/// Checks folder existence and write permissions to the DestinationPath.
/// </summary>
public class StatusFileSystemConfiguration
{
    /// <summary>
    /// How often to check the destination path accessibility.
    /// Default: 30 seconds
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>NOC behavior when destination path is inaccessible (AlertStatus.CREATE)</summary>
    public NocBehaviorConfiguration CreateNocBehavior { get; set; } = new()
    {
        SendToNoc = true,
        Payload = new NocHttpPayload
        {
            Custom1 = "ArgusTeam",
            Custom2 = "StatusFileSystem",
            HostName = "ArgusServer",
            Level = 3,
            Message = "Destination path inaccessible",
            Severity = "critical",
            Source = "status-filesystem",
            SuppressionKey = "status-filesystem",
            Visible = true
        },
        SuppressWindow = "1m"
    };

    /// <summary>NOC behavior when destination path is accessible (AlertStatus.CANCEL)</summary>
    public NocBehaviorConfiguration CancelNocBehavior { get; set; } = new()
    {
        SendToNoc = true,
        Payload = new NocHttpPayload
        {
            Custom1 = "ArgusTeam",
            Custom2 = "StatusFileSystem",
            HostName = "ArgusServer",
            Level = 1,
            Message = "Destination path accessible",
            Severity = "info",
            Source = "status-filesystem",
            SuppressionKey = "status-filesystem",
            Visible = true
        },
        SuppressWindow = "5m"
    };
}

