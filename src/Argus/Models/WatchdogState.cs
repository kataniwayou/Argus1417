namespace Argus.Models;

/// <summary>
/// Watchdog heartbeat state (tick-based)
/// </summary>
public class WatchdogState
{
    /// <summary>Whether watchdog is currently active (received within timeout)</summary>
    public bool Active => Status == WatchdogStatus.Healthy;

    /// <summary>Tick when last watchdog was received</summary>
    public long? LastReceivedTick { get; set; }

    /// <summary>Current watchdog status</summary>
    public WatchdogStatus Status { get; set; } = WatchdogStatus.Initializing;

    /// <summary>Whether startup grace period is active</summary>
    public bool GracePeriodActive { get; set; } = true;

    /// <summary>Reason for current status</summary>
    public string StatusReason { get; set; } = string.Empty;

    /// <summary>Timestamp when this state was captured</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

