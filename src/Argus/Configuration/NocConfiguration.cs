namespace Argus.Configuration;

/// <summary>
/// Unified NOC configuration containing all NOC-related settings.
/// Groups HttpClient and CircuitBreaker configurations under a single section.
/// </summary>
public class NocConfiguration
{
    /// <summary>
    /// Master switch to enable/disable all NOC HTTP communication.
    /// When false, Phase 1 (Send) and Phase 2 (Verify) are skipped for all alerts and heartbeats.
    /// Applies to both leader and follower instances.
    /// Alert processing continues normally but without NOC HTTP calls.
    /// Default: false
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// NOC HTTP client configuration for endpoints and connection settings.
    /// </summary>
    public NocHttpClientConfiguration HttpClient { get; set; } = new();

    /// <summary>
    /// NOC circuit breaker configuration for failure tracking.
    /// </summary>
    public NocCircuitBreakerConfiguration CircuitBreaker { get; set; } = new();
}

