namespace Argus.Services.CentralTimer;

/// <summary>
/// Callback registration for the central timer.
/// </summary>
/// <param name="Name">Unique name for the callback (for logging/metrics)</param>
/// <param name="IntervalTicks">Execute every N ticks (1 = every tick, 60 = every 60 ticks)</param>
/// <param name="Callback">Async callback to execute (receives tick count, correlationId, and cancellation token)</param>
/// <param name="IsGracePeriodAware">If true, callback is skipped during grace period</param>
public record CentralTimerCallback(
    string Name,
    int IntervalTicks,
    Func<long, string, CancellationToken, Task> Callback,
    bool IsGracePeriodAware = false);

/// <summary>
/// Central Timer Service - the system heartbeat.
/// Provides a single source of truth for system liveness via tick-based timing.
/// All services register callbacks instead of managing their own timers.
/// </summary>
public interface ICentralTimerService
{
    /// <summary>
    /// Current tick count (incremented every tick interval).
    /// Thread-safe.
    /// </summary>
    long TickCount { get; }

    /// <summary>
    /// Timestamp of the last heartbeat (updated every tick).
    /// Thread-safe.
    /// </summary>
    DateTime HeartbeatTimestamp { get; }

    /// <summary>
    /// Whether the startup grace period is still active.
    /// During grace period, GracePeriodAware callbacks are skipped.
    /// Thread-safe.
    /// </summary>
    bool IsGracePeriodActive { get; }

    /// <summary>
    /// Tick interval in seconds.
    /// </summary>
    int TickIntervalSeconds { get; }

    /// <summary>
    /// Grace period duration in seconds.
    /// </summary>
    int GracePeriodSeconds { get; }

    /// <summary>
    /// Register a callback to be executed at the specified interval.
    /// </summary>
    /// <param name="callback">The callback registration</param>
    void RegisterCallback(CentralTimerCallback callback);

    /// <summary>
    /// Unregister a callback by name.
    /// </summary>
    /// <param name="name">The callback name</param>
    /// <returns>True if callback was found and removed</returns>
    bool UnregisterCallback(string name);

    /// <summary>
    /// Get the list of registered callback names.
    /// </summary>
    IReadOnlyList<string> GetRegisteredCallbacks();
}

