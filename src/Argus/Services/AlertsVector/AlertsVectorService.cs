using Argus.Configuration;
using Argus.Models;
using Argus.Services.CentralTimer;
using Argus.Services.Metrics;
using Argus.Services.Noc;
using Argus.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.AlertsVector;

/// <summary>
/// Service to manage the real-time alerts vector.
/// The vector is ordered by priority (lowest value = highest priority).
/// Each priority level can contain multiple alerts.
/// All alert sources (K8s layer, Prometheus, Watchdog) update this vector asynchronously.
/// Uses tick-based timing for TTL expiry, synchronized with CentralTimer.
///
/// In-memory storage only - no persistence across restarts.
/// </summary>
public interface IAlertsVectorService
{
    /// <summary>
    /// Update or add an alert to the vector
    /// </summary>
    void UpdateAlert(AlertDto alert);

    /// <summary>
    /// Remove an alert from the vector by fingerprint.
    /// Also clears associated suppression cache entries.
    /// </summary>
    /// <returns>True if alert was removed, false if not found</returns>
    bool RemoveAlert(string fingerprint);

    /// <summary>
    /// Get a snapshot of the current alerts vector ordered by priority
    /// </summary>
    List<AlertDto> GetSnapshot();

    /// <summary>
    /// Get count of active (CREATE status) alerts
    /// </summary>
    int GetActiveAlertCount();

    /// <summary>
    /// Remove stale CREATE alerts that have exceeded the TTL.
    /// Also clears associated suppression cache entries.
    /// Returns the number of alerts removed.
    /// </summary>
    int CleanupExpiredAlerts();

    /// <summary>
    /// Clear all alerts (for testing)
    /// </summary>
    void Clear();
}

public class AlertsVectorService : IAlertsVectorService
{
    private readonly ILogger<AlertsVectorService> _logger;
    private readonly ICentralTimerService _centralTimer;
    private readonly ISuppressionCache _suppressionCache;
    private readonly IArgusMetrics _metrics;
    private readonly AlertsVectorConfiguration _config;
    private readonly int _alertTtlTicks;

    // In-memory storage
    private readonly Dictionary<string, AlertDto> _alerts = new();
    private readonly object _lock = new();

    public AlertsVectorService(
        ILogger<AlertsVectorService> logger,
        ICentralTimerService centralTimer,
        ISuppressionCache suppressionCache,
        IArgusMetrics metrics,
        IOptions<AlertsVectorConfiguration> config)
    {
        _logger = logger;
        _centralTimer = centralTimer;
        _suppressionCache = suppressionCache;
        _metrics = metrics;
        _config = config.Value;

        // Convert TTL from seconds to ticks
        var alertTtlSeconds = (int)TimeSpanParser.ParseToTimeSpan(_config.AlertTtl).TotalSeconds;
        _alertTtlTicks = alertTtlSeconds / _centralTimer.TickIntervalSeconds;
        if (_alertTtlTicks < 1) _alertTtlTicks = 1;

        _logger.LogInformation(
            "AlertsVectorService initialized with TTL={Ttl} ({TtlTicks} ticks)",
            _config.AlertTtl, _alertTtlTicks);
    }

    /// <inheritdoc />
    public void UpdateAlert(AlertDto alert)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(alert.Fingerprint))
            {
                _logger.LogWarning("Alert {Name} has no fingerprint, skipping", alert.Name);
                return;
            }

            // Check if this is a new alert or status change for lifecycle metrics
            var isNew = !_alerts.TryGetValue(alert.Fingerprint, out var existingAlert);
            var statusChanged = !isNew && existingAlert!.Status != alert.Status;

            // If CANCEL arrives for an alert not in the vector, ignore it silently
            // This happens when:
            // - Prometheus sends resolved notifications for alerts that fired before this Argus instance started
            // - Alerts were already processed and removed from the vector
            // - Watchdog/K8s sends CANCEL when no CREATE alert exists
            if (isNew && alert.Status == AlertStatus.CANCEL)
            {
                _logger.LogDebug(
                    "Ignoring CANCEL for unknown alert: {Name} (Fingerprint={Fingerprint}, Source={Source}, Priority={Priority}) - alert was not in vector",
                    alert.Name, alert.Fingerprint, alert.Source, alert.Priority);
                return;
            }

            // If CANCEL arrives but existing alert is also CANCEL, skip update
            // This prevents unnecessary updates when K8s/Watchdog repeatedly send CANCEL
            if (!isNew && alert.Status == AlertStatus.CANCEL && existingAlert!.Status == AlertStatus.CANCEL)
            {
                // Just update LastSeen for existing CANCEL
                existingAlert.LastSeenTick = _centralTimer.TickCount;
                existingAlert.LastSeenTimestamp = _centralTimer.HeartbeatTimestamp;
                _logger.LogDebug(
                    "Skipping CANCEL update for existing CANCEL alert: {Name} (Fingerprint={Fingerprint}, Source={Source}, Priority={Priority})",
                    alert.Name, alert.Fingerprint, alert.Source, alert.Priority);
                return;
            }

            // Update LastSeen (tick for business logic, timestamp for logging - both from CentralTimer)
            alert.LastSeenTick = _centralTimer.TickCount;
            alert.LastSeenTimestamp = _centralTimer.HeartbeatTimestamp;

            // Track lifecycle metrics for new alerts or status changes
            if (isNew || statusChanged)
            {
                switch (alert.Status)
                {
                    case AlertStatus.CREATE:
                        _metrics.IncrementAlertsCreated();
                        break;
                }
            }

            // Update or add the alert
            _alerts[alert.Fingerprint] = alert;

            // INFO: Log new CREATE alerts (first seen) - important operational visibility
            if (isNew && alert.Status == AlertStatus.CREATE)
            {
                _logger.LogInformation(
                    "ALERT CREATED: {Name} - {Summary}. Priority={Priority}, Source={Source}, Fingerprint={Fingerprint}, Description={Description}, ExecutionId={ExecutionId}",
                    alert.Name, alert.Summary, alert.Priority, alert.Source, alert.Fingerprint, alert.Description, alert.ExecutionId);
            }
            // INFO: Log status transitions (CREATE -> CANCEL or CANCEL -> CREATE)
            else if (statusChanged && alert.Status == AlertStatus.CREATE)
            {
                // Alert transitioned from CANCEL back to CREATE - log with full details
                _logger.LogInformation(
                    "ALERT CREATED: {Name} - {Summary}. Priority={Priority}, Source={Source}, Fingerprint={Fingerprint}, Description={Description}, PreviousStatus={OldStatus}, ExecutionId={ExecutionId}",
                    alert.Name, alert.Summary, alert.Priority, alert.Source, alert.Fingerprint, alert.Description, existingAlert!.Status, alert.ExecutionId);
            }
            else if (statusChanged)
            {
                // Alert transitioned to CANCEL
                _logger.LogInformation(
                    "Alert resolved: {Name} {OldStatus} -> {NewStatus} (Priority={Priority}, Source={Source}, Fingerprint={Fingerprint}). ExecutionId={ExecutionId}",
                    alert.Name, existingAlert!.Status, alert.Status, alert.Priority, alert.Source, alert.Fingerprint, alert.ExecutionId);
            }
            else
            {
                // DEBUG: Regular updates without status change
                _logger.LogDebug(
                    "Alert vector updated: {Status} {Name} (Priority={Priority}, Fingerprint={Fingerprint}, Source={Source}, IsNew={IsNew}, ExecutionId={ExecutionId})",
                    alert.Status, alert.Name, alert.Priority, alert.Fingerprint, alert.Source, isNew, alert.ExecutionId);
            }
        }
    }

    /// <inheritdoc />
    public bool RemoveAlert(string fingerprint)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                _logger.LogWarning("Cannot remove alert: fingerprint is empty");
                return false;
            }

            if (_alerts.TryGetValue(fingerprint, out var alert))
            {
                _alerts.Remove(fingerprint);

                // Clear suppression cache entries for this fingerprint (incident closed)
                _suppressionCache.ClearFingerprint(fingerprint);

                // Track resolved metrics (alert removed = resolved)
                _metrics.IncrementAlertsResolved();

                _logger.LogDebug(
                    "Alert removed from vector: {Name} (Priority={Priority}, Fingerprint={Fingerprint}, Source={Source}, ExecutionId={ExecutionId})",
                    alert.Name, alert.Priority, alert.Fingerprint, alert.Source, alert.ExecutionId);
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc />
    public List<AlertDto> GetSnapshot()
    {
        lock (_lock)
        {
            // Return alerts ordered by priority (lowest value first = highest priority)
            // Then by timestamp (oldest first)
            return _alerts.Values
                .OrderBy(a => a.Priority)
                .ThenBy(a => a.Timestamp)
                .ToList();
        }
    }

    /// <inheritdoc />
    public int GetActiveAlertCount()
    {
        lock (_lock)
        {
            return _alerts.Values.Count(a => a.Status == AlertStatus.CREATE);
        }
    }

    /// <inheritdoc />
    public int CleanupExpiredAlerts()
    {
        lock (_lock)
        {
            var currentTick = _centralTimer.TickCount;

            // Find all expired alerts (both CREATE and CANCEL)
            var expiredAlerts = _alerts.Values
                .Where(a => (currentTick - a.LastSeenTick) > _alertTtlTicks)
                .ToList();

            var expiredCreateCount = 0;
            var expiredCancelCount = 0;

            foreach (var alert in expiredAlerts)
            {
                _alerts.Remove(alert.Fingerprint);

                // Clear suppression cache entries for this fingerprint (incident closed)
                _suppressionCache.ClearFingerprint(alert.Fingerprint);

                var ageTicks = currentTick - alert.LastSeenTick;
                var ageSeconds = ageTicks * _centralTimer.TickIntervalSeconds;
                _logger.LogWarning(
                    "Alert expired (TTL): {Status} {Name} (Priority={Priority}, Fingerprint={Fingerprint}, Source={Source}, LastSeenTick={LastSeenTick}, AgeTicks={AgeTicks} ({AgeSeconds}s), ExecutionId={ExecutionId})",
                    alert.Status, alert.Name, alert.Priority, alert.Fingerprint, alert.Source,
                    alert.LastSeenTick, ageTicks, ageSeconds, alert.ExecutionId);

                if (alert.Status == AlertStatus.CREATE)
                    expiredCreateCount++;
                else
                    expiredCancelCount++;
            }

            if (expiredAlerts.Count > 0)
            {
                _logger.LogInformation(
                    "TTL cleanup completed: {CreateCount} stale CREATE alert(s) and {CancelCount} stale CANCEL alert(s) removed",
                    expiredCreateCount, expiredCancelCount);
            }

            return expiredAlerts.Count;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _alerts.Clear();
            _logger.LogDebug("Alerts vector cleared");
        }
    }
}
