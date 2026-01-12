namespace Argus.Configuration;

/// <summary>
/// Unified NOC configuration containing all NOC-related settings.
/// Groups HttpClient and CircuitBreaker configurations under a single section.
/// </summary>
public class NocConfiguration
{
    /// <summary>
    /// NOC HTTP client configuration for endpoints and connection settings.
    /// </summary>
    public NocHttpClientConfiguration HttpClient { get; set; } = new();

    /// <summary>
    /// NOC circuit breaker configuration for failure tracking.
    /// </summary>
    public NocCircuitBreakerConfiguration CircuitBreaker { get; set; } = new();
}

