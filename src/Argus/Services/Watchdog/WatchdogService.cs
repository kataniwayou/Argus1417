using Argus.Configuration;
using Argus.Models;
using Argus.Services.AlertsVector;
using Argus.Services.CentralTimer;
using Argus.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.Watchdog;

/// <summary>
/// Service to manage watchdog monitoring and generate watchdog alerts.
/// Uses CentralTimer for tick-based expiration detection (no independent timers).
///
/// Two-tier design:
/// - Tier 1: _lastHeartbeatTick (internal state, updated when Prometheus heartbeat arrives)
/// - Tier 2: AlertsVector (updated by callback based on Tier 1 state)
///
/// Pure tick-driven: Only the callback updates AlertsVector.
/// </summary>
public interface IWatchdogService
{
    /// <summary>
    /// Start the watchdog service (register with CentralTimer)
    /// </summary>
    void Start();

    /// <summary>
    /// Record a watchdog heartbeat (Tier 1 only - saves tick, no AlertsVector update)
    /// </summary>
    void RecordHeartbeat();

    /// <summary>
    /// Generate watchdog alert based on current state.
    /// Always generates an alert (even if healthy) to update the alerts vector.
    /// </summary>
    AlertDto GenerateAlert();

    /// <summary>
    /// Check if grace period is active
    /// </summary>
    bool IsGracePeriodActive { get; }

    /// <summary>
    /// Get watchdog state
    /// </summary>
    WatchdogState GetState();
}

public class WatchdogService : IWatchdogService
{
    private const string CallbackName = "WatchdogCheck";

    private readonly ILogger<WatchdogService> _logger;
    private readonly WatchdogConfiguration _config;
    private readonly ICentralTimerService _centralTimer;
    private readonly IAlertsVectorService _alertsVector;
    private readonly ILivenessVectorService _livenessVector;
    private readonly int _timeoutTicks;
    private readonly object _lock = new();

    private long? _lastHeartbeatTick;  // Tier 1: Prometheus heartbeat tick
    private bool _wasExpired; // Track previous state for transition logging

    public WatchdogService(
        ILogger<WatchdogService> logger,
        ICentralTimerService centralTimer,
        IOptions<ArgusConfiguration> config,
        IAlertsVectorService alertsVector,
        ILivenessVectorService livenessVector)
    {
        _logger = logger;
        _centralTimer = centralTimer;
        _config = config.Value.Watchdog;
        _alertsVector = alertsVector;
        _livenessVector = livenessVector;

        // Calculate timeout in ticks
        _timeoutTicks = _config.TimeoutSeconds / _centralTimer.TickIntervalSeconds;
        if (_timeoutTicks < 1) _timeoutTicks = 1;

        _logger.LogInformation(
            "WatchdogService initialized. TimeoutSeconds={TimeoutSeconds}, TimeoutTicks={TimeoutTicks}",
            _config.TimeoutSeconds, _timeoutTicks);
    }

    /// <summary>
    /// Start the watchdog service by registering with CentralTimer
    /// </summary>
    public void Start()
    {
        // Register callback with CentralTimer - check every timeout interval
        var callback = new CentralTimerCallback(
            Name: CallbackName,
            IntervalTicks: _timeoutTicks,
            Callback: OnWatchdogTickAsync,
            IsGracePeriodAware: true); // Skip during grace period

        _centralTimer.RegisterCallback(callback);

        _logger.LogInformation(
            "WatchdogService registered with CentralTimer. IntervalTicks={IntervalTicks} (TimeoutSeconds={TimeoutSeconds})",
            _timeoutTicks, _config.TimeoutSeconds);
    }

    /// <summary>
    /// Generate a unique execution ID for tracking an alert through its lifecycle.
    /// Format: exec-{8-char-guid}
    /// </summary>
    private static string GenerateExecutionId() =>
        $"exec-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// CentralTimer callback for watchdog expiration check.
    /// Runs every timeout interval to check Tier 1 (_lastHeartbeatTick) and update Tier 2 (AlertsVector).
    /// Stamps LivenessVector on success/failure (not exception).
    /// </summary>
    private Task OnWatchdogTickAsync(long tick, string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            lock (_lock)
            {
                // During grace period, skip business logic
                if (IsGracePeriodActive)
                {
                    _logger.LogDebug(
                        "WatchdogCheck skipped (grace period). Tick={Tick}, CorrelationId={CorrelationId}",
                        tick, correlationId);
                    return Task.CompletedTask;
                }

                var executionId = GenerateExecutionId();
                bool isExpired;
                string reason;

                if (!_lastHeartbeatTick.HasValue)
                {
                    // No heartbeat ever received
                    isExpired = true;
                    reason = "No heartbeat ever received";
                }
                else
                {
                    var ageTicks = tick - _lastHeartbeatTick.Value;
                    isExpired = ageTicks >= _timeoutTicks;
                    reason = isExpired
                        ? $"No heartbeat for {ageTicks * _centralTimer.TickIntervalSeconds}s (timeout: {_config.TimeoutSeconds}s)"
                        : $"Heartbeat {ageTicks * _centralTimer.TickIntervalSeconds}s ago";
                }

                // State transition logging
                if (isExpired && !_wasExpired)
                {
                    _logger.LogWarning(
                        "Watchdog expired: {Reason}. Tick={Tick}, CorrelationId={CorrelationId}, ExecutionId={ExecutionId}",
                        reason, tick, correlationId, executionId);
                }
                else if (!isExpired && _wasExpired)
                {
                    _logger.LogInformation(
                        "Watchdog recovered: {Reason}. Tick={Tick}, CorrelationId={CorrelationId}, ExecutionId={ExecutionId}",
                        reason, tick, correlationId, executionId);
                }

                _wasExpired = isExpired;

                // SINGLE call site - update AlertsVector (Tier 2) based on current state
                var alert = GenerateAlert(executionId);
                _alertsVector.UpdateAlert(alert);

                _logger.LogDebug(
                    "WatchdogCheck completed: Status={Status}, Reason={Reason}. Tick={Tick}, ExecutionId={ExecutionId}",
                    alert.Status, reason, tick, executionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WatchdogCheck failed. Tick={Tick}, CorrelationId={CorrelationId}", tick, correlationId);
        }
        finally
        {
            // Always stamp - callback executed (didn't silently die)
            _livenessVector.RecordExecution(CallbackName, _timeoutTicks, tick);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <summary>
    /// Record a watchdog heartbeat (Tier 1 only - saves tick, no AlertsVector update).
    /// AlertsVector will be updated on the next tick by OnWatchdogTickAsync.
    /// </summary>
    public void RecordHeartbeat()
    {
        lock (_lock)
        {
            var currentTick = _centralTimer.TickCount;
            _lastHeartbeatTick = currentTick;

            // NOTE: _wasExpired is NOT reset here - only the tick callback handles state transitions
            // This ensures a single source of truth for AlertsVector updates

            _logger.LogDebug(
                "Watchdog heartbeat recorded (Tier 1). Tick={Tick}, TimeoutTicks={TimeoutTicks}, GracePeriod={GracePeriod}",
                currentTick, _timeoutTicks, IsGracePeriodActive);

            // No AlertsVector update here - that's the callback's responsibility (Tier 2)
        }
    }

    /// <inheritdoc />
    public AlertDto GenerateAlert() => GenerateAlert(null);

    /// <summary>
    /// Generate watchdog alert with optional execution ID
    /// </summary>
    private AlertDto GenerateAlert(string? executionId)
    {
        var state = GetState();

        // Determine status first
        AlertStatus status;
        string summary;
        if (state.Status == WatchdogStatus.Healthy)
        {
            status = AlertStatus.CANCEL;
            summary = "Watchdog is healthy";
        }
        else if (state.Status == WatchdogStatus.Initializing)
        {
            status = AlertStatus.CANCEL;
            summary = "Watchdog initializing";
        }
        else // Missing
        {
            status = AlertStatus.CREATE;
            summary = "Watchdog expired";
        }

        // Use status-specific NOC behavior
        var nocBehavior = status == AlertStatus.CREATE
            ? _config.CreateNocBehavior
            : _config.CancelNocBehavior;

        var alert = new AlertDto
        {
            Priority = -7,
            Name = "WatchdogExpired",
            Fingerprint = "watchdog",
            Source = "watchdog",
            Status = status,
            Summary = summary,
            Description = state.StatusReason,
            Payload = nocBehavior.Payload.Clone(),
            SendToNoc = nocBehavior.SendToNoc,
            SuppressWindow = ParseSuppressWindow(nocBehavior.SuppressWindow),
            Timestamp = DateTime.UtcNow,
            ExecutionId = executionId ?? string.Empty
        };

        // Apply runtime overrides (level, message, source, suppressionKey)
        alert.Payload.ApplyAlertOverrides(alert);

        return alert;
    }

    /// <summary>
    /// Parse suppress window string to TimeSpan. Returns null if invalid.
    /// </summary>
    private static TimeSpan? ParseSuppressWindow(string? suppressWindow)
    {
        if (string.IsNullOrWhiteSpace(suppressWindow))
            return null;

        if (TimeSpanParser.TryParseToTimeSpan(suppressWindow, out var timeSpan))
            return timeSpan;

        return null;
    }
    
    /// <inheritdoc />
    public bool IsGracePeriodActive => _centralTimer.IsGracePeriodActive;

    /// <inheritdoc />
    public WatchdogState GetState()
    {
        lock (_lock)
        {
            var currentTick = _centralTimer.TickCount;
            var status = WatchdogStatus.Initializing;
            var reason = "Startup grace period active";

            if (!IsGracePeriodActive)
            {
                if (!_lastHeartbeatTick.HasValue)
                {
                    // No heartbeat ever received
                    status = WatchdogStatus.Missing;
                    reason = "No watchdog ever received";
                }
                else
                {
                    var ageTicks = currentTick - _lastHeartbeatTick.Value;
                    var ageSeconds = ageTicks * _centralTimer.TickIntervalSeconds;

                    if (ageTicks >= _timeoutTicks)
                    {
                        status = WatchdogStatus.Missing;
                        reason = $"Watchdog not received for {ageSeconds}s (timeout: {_config.TimeoutSeconds}s)";
                    }
                    else
                    {
                        status = WatchdogStatus.Healthy;
                        reason = $"Watchdog received {ageSeconds}s ago";
                    }
                }
            }

            return new WatchdogState
            {
                Status = status,
                StatusReason = reason,
                LastReceivedTick = _lastHeartbeatTick,
                GracePeriodActive = IsGracePeriodActive
            };
        }
    }
}

