namespace Argus.Configuration;

/// <summary>
/// Configuration for ArgusCoordinator
/// </summary>
public class CoordinatorConfiguration
{
    /// <summary>
    /// Interval in seconds for taking NOC snapshots.
    /// Default: 30 seconds
    /// </summary>
    public int SnapshotIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Grace period multiplier for startup.
    /// Grace period = WindowSize × PollingIntervalSeconds × GracePeriodMultiplier
    /// During this period:
    /// - Watchdog monitoring is disabled
    /// - Restart storm detection is disabled
    /// - NOC snapshots are deferred
    ///
    /// Minimum: 1.0 (exactly fills restart tracking window, no buffer)
    /// Recommended: 2.0-3.0 (provides buffer for pod stabilization)
    /// Default: 2.5 (ensures window fills + extra buffer for pod stabilization)
    ///
    /// Example: WindowSize=5, PollingInterval=15s → GracePeriod = 5 × 15 × 2.5 = 187.5s
    /// </summary>
    public double StartupGracePeriodMultiplier { get; set; } = 2.5;

    /// <summary>
    /// Calculate the startup grace period in seconds based on restart tracking configuration.
    /// Formula: WindowSize × PollingIntervalSeconds × GracePeriodMultiplier
    /// Minimum multiplier enforced: 1.0
    /// </summary>
    public int CalculateStartupGracePeriodSeconds(int windowSize, int pollingIntervalSeconds)
    {
        // Enforce minimum multiplier of 1.0 to ensure window can fill
        var effectiveMultiplier = Math.Max(1.0, StartupGracePeriodMultiplier);

        var calculatedSeconds = windowSize * pollingIntervalSeconds * effectiveMultiplier;
        return (int)Math.Ceiling(calculatedSeconds);
    }
}

