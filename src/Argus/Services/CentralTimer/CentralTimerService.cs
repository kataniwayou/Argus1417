using System.Collections.Concurrent;
using System.Diagnostics;
using Argus.Configuration;
using Argus.Services.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.CentralTimer;

/// <summary>
/// Central Timer Service - the system heartbeat.
/// Ticks every 1 second (hardcoded), updating heartbeat timestamp
/// and executing registered callbacks using fire-and-forget pattern.
/// Each callback is responsible for stamping its own execution in LivenessVector.
/// </summary>
public class CentralTimerService : BackgroundService, ICentralTimerService
{
    /// <summary>
    /// Tick interval is hardcoded to 1 second for consistent timing across all callbacks.
    /// </summary>
    private const int TickIntervalSecondsConst = 1;

    private readonly ILogger<CentralTimerService> _logger;
    private readonly CoordinatorConfiguration _coordinatorConfig;
    private readonly IArgusMetrics _metrics;
    private readonly ILivenessVectorService _livenessVector;
    private readonly ConcurrentDictionary<string, CentralTimerCallback> _callbacks = new();
    private readonly ConcurrentDictionary<string, bool> _runningCallbacks = new();

    private long _tickCount;
    private DateTime _heartbeatTimestamp;
    private volatile bool _isGracePeriodActive = true;
    private volatile bool _gracePeriodFired;

    public long TickCount => Interlocked.Read(ref _tickCount);
    public DateTime HeartbeatTimestamp => _heartbeatTimestamp;
    public bool IsGracePeriodActive => _isGracePeriodActive;
    public int TickIntervalSeconds => TickIntervalSecondsConst;
    public int GracePeriodSeconds { get; }

    public CentralTimerService(
        ILogger<CentralTimerService> logger,
        IArgusMetrics metrics,
        ILivenessVectorService livenessVector,
        IOptions<ArgusConfiguration> config)
    {
        _logger = logger;
        _metrics = metrics;
        _livenessVector = livenessVector;
        _coordinatorConfig = config.Value.Coordinator;

        // Calculate grace period: SnapshotInterval * Multiplier
        GracePeriodSeconds = (int)(_coordinatorConfig.SnapshotIntervalSeconds *
            _coordinatorConfig.StartupGracePeriodMultiplier);

        _heartbeatTimestamp = DateTime.UtcNow;

        _logger.LogInformation(
            "CentralTimerService initialized. TickInterval={TickInterval}s (hardcoded), GracePeriod={GracePeriod}s",
            TickIntervalSecondsConst, GracePeriodSeconds);
    }

    public void RegisterCallback(CentralTimerCallback callback)
    {
        if (_callbacks.TryAdd(callback.Name, callback))
        {
            _logger.LogInformation(
                "Callback registered: {Name}, Interval={Interval} ticks, GracePeriodAware={GracePeriodAware}",
                callback.Name, callback.IntervalTicks, callback.IsGracePeriodAware);
        }
        else
        {
            _logger.LogWarning("Callback already registered: {Name}", callback.Name);
        }
    }

    public bool UnregisterCallback(string name)
    {
        if (_callbacks.TryRemove(name, out _))
        {
            _logger.LogInformation("Callback unregistered: {Name}", name);
            return true;
        }
        return false;
    }

    public IReadOnlyList<string> GetRegisteredCallbacks()
    {
        return _callbacks.Keys.ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "CentralTimerService starting. TickInterval={TickInterval}s (hardcoded), GracePeriod={GracePeriod}s",
            TickIntervalSecondsConst, GracePeriodSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TickIntervalSecondsConst));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                OnTick(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in central timer tick");
            }
        }

        _logger.LogInformation("CentralTimerService stopped");
    }

    private void OnTick(CancellationToken stoppingToken)
    {
        var tick = Interlocked.Increment(ref _tickCount);
        var correlationId = GenerateTickCorrelationId(tick);
        _heartbeatTimestamp = DateTime.UtcNow;

        // Update tick count metric
        _metrics.SetCentralTimerTickCount(tick);

        // Check grace period expiration (fire once)
        CheckGracePeriodExpiration(tick);

        // Execute callbacks - fire and forget (always on schedule)
        // Each callback is protected by _runningCallbacks to prevent concurrent execution
        // All callbacks executed on this tick receive the same correlationId
        foreach (var kvp in _callbacks)
        {
            var callback = kvp.Value;

            // Check if this tick should execute the callback
            if (tick % callback.IntervalTicks != 0)
                continue;

            // Skip grace period aware callbacks during grace period
            if (callback.IsGracePeriodAware && _isGracePeriodActive)
            {
                _logger.LogTrace(
                    "Skipping callback {Name} during grace period (tick {Tick})",
                    callback.Name, tick);
                continue;
            }

            // Skip if callback is already running (prevent concurrent execution of same callback)
            if (!_runningCallbacks.TryAdd(callback.Name, true))
            {
                _logger.LogWarning(
                    "Skipping callback {Name} at tick {Tick} - previous execution still running",
                    callback.Name, tick);
                _metrics.IncrementCallbackSkipped(callback.Name);
                continue;
            }

            // Fire and forget - don't await, next tick fires on schedule
            // Errors are handled inside ExecuteCallbackAsync
            // Pass correlationId to executed callbacks only
            _ = ExecuteCallbackAsync(callback, tick, correlationId, stoppingToken);
        }
    }

    /// <summary>
    /// Generate a unique correlation ID for this tick.
    /// Format: tick-{5-digit-tick}-{8-char-guid}
    /// All callbacks executed on this tick share this correlationId.
    /// </summary>
    private static string GenerateTickCorrelationId(long tick) =>
        $"tick-{tick:D5}-{Guid.NewGuid().ToString("N")[..8]}";

    private async Task ExecuteCallbackAsync(CentralTimerCallback callback, long tick, string correlationId, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await callback.Callback(tick, correlationId, stoppingToken);
            stopwatch.Stop();
            _metrics.RecordCallbackDuration(callback.Name, stopwatch.Elapsed);
            // Each callback is responsible for stamping its own execution in LivenessVector
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown - callback should have stamped LivenessVector
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordCallbackDuration(callback.Name, stopwatch.Elapsed);
            _metrics.IncrementCallbackError(callback.Name);
            _logger.LogError(ex, "Callback {Name} failed at tick {Tick}. CorrelationId={CorrelationId}", callback.Name, tick, correlationId);
            // Callback does NOT stamp LivenessVector on exception - this is intentional
            // so that the HeartbeatService can detect unhealthy callbacks
        }
        finally
        {
            // Always release the running lock
            _runningCallbacks.TryRemove(callback.Name, out _);
        }
    }

    private void CheckGracePeriodExpiration(long tick)
    {
        if (_gracePeriodFired) return;

        var gracePeriodTicks = GracePeriodSeconds / TickIntervalSecondsConst;
        if (tick >= gracePeriodTicks)
        {
            _isGracePeriodActive = false;
            _gracePeriodFired = true;

            _logger.LogInformation(
                "Grace period expired at tick {Tick}. GracePeriodAware callbacks now active.",
                tick);
        }
    }
}

