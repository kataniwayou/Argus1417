using Argus.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.Noc;

/// <summary>
/// NOC Health Service implementation.
/// Tracks consecutive NOC failures using Option B logic:
/// - Any failure (heartbeat or alert NOC) increments counter
/// - Any success (heartbeat or alert NOC) resets counter to 0
/// - IsHealthy = false when counter >= threshold
/// 
/// Thread-safe using lock.
/// </summary>
public class NocHealthService : INocHealthService
{
    private readonly ILogger<NocHealthService> _logger;
    private readonly int _failureThreshold;
    private int _consecutiveFailures;
    private readonly object _lock = new();

    public bool IsHealthy
    {
        get
        {
            lock (_lock)
            {
                return _consecutiveFailures < _failureThreshold;
            }
        }
    }

    public int ConsecutiveFailures
    {
        get
        {
            lock (_lock)
            {
                return _consecutiveFailures;
            }
        }
    }

    public int FailureThreshold => _failureThreshold;

    public NocHealthService(
        ILogger<NocHealthService> logger,
        IOptions<ArgusConfiguration> config)
    {
        _logger = logger;
        _failureThreshold = config.Value.Noc.CircuitBreaker.FailureThreshold;

        _logger.LogInformation(
            "NocHealthService initialized. FailureThreshold={FailureThreshold}",
            _failureThreshold);
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            var wasUnhealthy = _consecutiveFailures >= _failureThreshold;
            var previousCount = _consecutiveFailures;
            _consecutiveFailures = 0;

            if (wasUnhealthy)
            {
                _logger.LogInformation(
                    "NOC circuit breaker recovered. ConsecutiveFailures reset from {Previous} to 0",
                    previousCount);
            }
            else if (previousCount > 0)
            {
                _logger.LogDebug(
                    "NOC success recorded. ConsecutiveFailures reset from {Previous} to 0",
                    previousCount);
            }
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            var wasHealthy = _consecutiveFailures < _failureThreshold;
            _consecutiveFailures++;

            if (wasHealthy && _consecutiveFailures >= _failureThreshold)
            {
                _logger.LogWarning(
                    "NOC circuit breaker TRIPPED. ConsecutiveFailures={ConsecutiveFailures}, Threshold={Threshold}",
                    _consecutiveFailures, _failureThreshold);
            }
            else
            {
                _logger.LogDebug(
                    "NOC failure recorded. ConsecutiveFailures={ConsecutiveFailures}, Threshold={Threshold}",
                    _consecutiveFailures, _failureThreshold);
            }
        }
    }
}

