namespace Argus.Services.CentralTimer;

/// <summary>
/// Represents a callback's liveness state in the LivenessVector.
/// Uses tick-based timing (no absolute clock).
/// </summary>
/// <param name="Name">Callback name</param>
/// <param name="LastExecutionTick">Tick number when callback last completed</param>
/// <param name="ExpectedIntervalTicks">Expected interval in ticks</param>
public record CallbackLiveness(
    string Name,
    long LastExecutionTick,
    int ExpectedIntervalTicks);

/// <summary>
/// Represents an unhealthy callback with age details.
/// </summary>
/// <param name="Name">Callback name</param>
/// <param name="ExpectedIntervalTicks">Expected interval in ticks</param>
/// <param name="LastExecutionTick">Tick number when callback last completed</param>
/// <param name="AgeTicks">Current age in ticks (currentTick - lastExecutionTick)</param>
/// <param name="ThresholdTicks">Threshold for unhealthy (expectedInterval * 2)</param>
public record UnhealthyCallback(
    string Name,
    int ExpectedIntervalTicks,
    long LastExecutionTick,
    long AgeTicks,
    int ThresholdTicks);

/// <summary>
/// LivenessVector Service - tracks callback execution health using tick-based timing.
/// Each callback records its execution at the END of its work (only on success/failure, not exception).
/// Exception = unhealthy functionality, so no stamp = detected as unhealthy.
/// The Heartbeat callback checks vector health to decide HTTP/File behavior.
/// </summary>
public interface ILivenessVectorService
{
    /// <summary>
    /// Record a callback's execution completion.
    /// Called by callbacks at the END of their execution (only on success/failure, not exception).
    /// </summary>
    /// <param name="callbackName">Name of the callback</param>
    /// <param name="expectedIntervalTicks">Expected interval in ticks for this callback</param>
    /// <param name="currentTick">Current tick number from CentralTimer</param>
    void RecordExecution(string callbackName, int expectedIntervalTicks, long currentTick);

    /// <summary>
    /// Check if all callbacks in the vector are healthy.
    /// A callback is healthy if its age is less than expectedIntervalTicks * 2.
    /// </summary>
    /// <param name="currentTick">Current tick number</param>
    /// <returns>True if all callbacks are healthy</returns>
    bool IsHealthy(long currentTick);

    /// <summary>
    /// Get list of unhealthy callbacks.
    /// A callback is unhealthy if its age >= expectedIntervalTicks * 2.
    /// </summary>
    /// <param name="currentTick">Current tick number</param>
    /// <returns>List of unhealthy callbacks with details</returns>
    IReadOnlyList<UnhealthyCallback> GetUnhealthyCallbacks(long currentTick);

    /// <summary>
    /// Get a snapshot of all callbacks in the vector.
    /// For diagnostics and API exposure.
    /// </summary>
    /// <returns>List of all callback liveness entries</returns>
    IReadOnlyList<CallbackLiveness> GetSnapshot();

    /// <summary>
    /// Get the count of callbacks in the vector.
    /// </summary>
    int Count { get; }
}

