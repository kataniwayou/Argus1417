using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Argus.Services.CentralTimer;

/// <summary>
/// LivenessVector Service implementation.
/// Tracks callback execution health using tick-based timing.
/// Callbacks stamp on success/failure only (not exception) - exception = unhealthy.
/// Thread-safe using ConcurrentDictionary.
/// </summary>
public class LivenessVectorService : ILivenessVectorService
{
    private readonly ILogger<LivenessVectorService> _logger;
    private readonly ConcurrentDictionary<string, CallbackLiveness> _liveness = new();

    /// <summary>
    /// Hard-coded tolerance multiplier.
    /// A callback is unhealthy if age >= expectedIntervalTicks * ToleranceMultiplier.
    /// </summary>
    private const int ToleranceMultiplier = 2;

    public int Count => _liveness.Count;

    public LivenessVectorService(ILogger<LivenessVectorService> logger)
    {
        _logger = logger;
        _logger.LogInformation(
            "LivenessVectorService initialized. ToleranceMultiplier={ToleranceMultiplier}",
            ToleranceMultiplier);
    }

    public void RecordExecution(string callbackName, int expectedIntervalTicks, long currentTick)
    {
        var entry = new CallbackLiveness(callbackName, currentTick, expectedIntervalTicks);

        _liveness.AddOrUpdate(
            callbackName,
            entry,
            (_, _) => entry);

        _logger.LogTrace(
            "Callback execution recorded: {CallbackName}, Tick={Tick}, ExpectedInterval={ExpectedInterval}",
            callbackName, currentTick, expectedIntervalTicks);
    }

    public bool IsHealthy(long currentTick)
    {
        foreach (var kvp in _liveness)
        {
            var entry = kvp.Value;
            var age = currentTick - entry.LastExecutionTick;
            var threshold = entry.ExpectedIntervalTicks * ToleranceMultiplier;

            if (age >= threshold)
            {
                return false;
            }
        }

        return true;
    }

    public IReadOnlyList<UnhealthyCallback> GetUnhealthyCallbacks(long currentTick)
    {
        var unhealthy = new List<UnhealthyCallback>();

        foreach (var kvp in _liveness)
        {
            var entry = kvp.Value;
            var age = currentTick - entry.LastExecutionTick;
            var threshold = entry.ExpectedIntervalTicks * ToleranceMultiplier;

            if (age >= threshold)
            {
                unhealthy.Add(new UnhealthyCallback(
                    Name: entry.Name,
                    ExpectedIntervalTicks: entry.ExpectedIntervalTicks,
                    LastExecutionTick: entry.LastExecutionTick,
                    AgeTicks: age,
                    ThresholdTicks: threshold));

                _logger.LogWarning(
                    "Unhealthy callback detected: {Name}, Age={Age} ticks, Threshold={Threshold} ticks",
                    entry.Name, age, threshold);
            }
        }

        return unhealthy;
    }

    public IReadOnlyList<CallbackLiveness> GetSnapshot()
    {
        return _liveness.Values.ToList();
    }
}

