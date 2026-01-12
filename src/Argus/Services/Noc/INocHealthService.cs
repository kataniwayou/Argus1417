namespace Argus.Services.Noc;

/// <summary>
/// Service to track NOC health across all NOC call sources.
/// Uses a general counter (Option B): any failure increments, any success resets to 0.
/// 
/// NOC sources:
/// - Heartbeat NOC (HeartbeatService) - every 30s when liveness healthy
/// - Alert NOC (NocQueueService) - on CREATE/CANCEL alert processing
/// 
/// Both sources contribute to the same counter.
/// </summary>
public interface INocHealthService
{
    /// <summary>
    /// Whether NOC is considered healthy (consecutive failures &lt; threshold).
    /// File heartbeat should only write when this is true.
    /// </summary>
    bool IsHealthy { get; }

    /// <summary>
    /// Current count of consecutive NOC failures.
    /// Resets to 0 on any successful NOC call.
    /// </summary>
    int ConsecutiveFailures { get; }

    /// <summary>
    /// Configured failure threshold.
    /// When ConsecutiveFailures >= FailureThreshold, IsHealthy becomes false.
    /// </summary>
    int FailureThreshold { get; }

    /// <summary>
    /// Record a successful NOC call (Phase 2 success).
    /// Resets failure counter to 0.
    /// Called by HeartbeatService and NocQueueService on successful NOC operations.
    /// </summary>
    void RecordSuccess();

    /// <summary>
    /// Record a failed NOC call (Phase 2 HTTP or comparison failure).
    /// Increments failure counter.
    /// Called by HeartbeatService and NocQueueService on failed NOC operations.
    /// </summary>
    void RecordFailure();
}

