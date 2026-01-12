namespace Argus.Configuration;

/// <summary>
/// Configuration for NOC circuit breaker.
/// Tracks consecutive NOC failures across all sources (heartbeat NOC and alert NOC).
/// When failures reach the threshold, file heartbeat stops.
/// File heartbeat resumes only when a NOC success occurs (counter resets to 0).
/// </summary>
public class NocCircuitBreakerConfiguration
{
    /// <summary>
    /// Number of consecutive NOC failures before stopping file heartbeat.
    /// Any successful NOC call (heartbeat or alert) resets the counter to 0.
    /// Default: 3
    /// </summary>
    public int FailureThreshold { get; set; } = 3;
}

