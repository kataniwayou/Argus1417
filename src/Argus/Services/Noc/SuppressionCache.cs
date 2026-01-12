using System.Collections.Concurrent;
using Argus.Configuration;
using Argus.Models;
using Argus.Services.CentralTimer;
using Argus.Utilities;
using Microsoft.Extensions.Options;

namespace Argus.Services.Noc;

/// <summary>
/// Cache for suppressing duplicate alert processing based on fingerprint, status, and time window.
/// Prevents processing the same alert multiple times within a configured suppression window.
/// Suppression is checked BEFORE enqueueing to NOC queue.
/// Uses tick-based timing for consistency with CentralTimer.
/// </summary>
public interface ISuppressionCache
{
    /// <summary>
    /// Check if an alert was recently processed (within suppression window)
    /// </summary>
    /// <param name="alert">Alert to check</param>
    /// <returns>True if alert was recently processed (should be suppressed)</returns>
    bool WasRecentlyProcessed(AlertDto alert);

    /// <summary>
    /// Mark an alert as processed with its suppression window
    /// </summary>
    /// <param name="alert">Alert that was processed</param>
    void MarkAsProcessed(AlertDto alert);

    /// <summary>
    /// Remove suppression entry for a specific alert (fingerprint + status).
    /// Called when NOC processing fails to allow immediate retry on next snapshot.
    /// </summary>
    /// <param name="alert">Alert to unmark</param>
    void UnmarkAsProcessed(AlertDto alert);

    /// <summary>
    /// Remove all entries for a fingerprint (both CREATE and CANCEL).
    /// Called when alert is removed from vector (incident closed).
    /// </summary>
    /// <param name="fingerprint">Alert fingerprint to clear</param>
    void ClearFingerprint(string fingerprint);
}

public class SuppressionCache : ISuppressionCache
{
    private readonly ILogger<SuppressionCache> _logger;
    private readonly ICentralTimerService _centralTimer;
    private readonly DefaultNocConfiguration _config;
    private readonly ConcurrentDictionary<string, SuppressionEntry> _entries = new();

    public SuppressionCache(
        ILogger<SuppressionCache> logger,
        ICentralTimerService centralTimer,
        IOptions<DefaultNocConfiguration> config)
    {
        _logger = logger;
        _centralTimer = centralTimer;
        _config = config.Value;
    }

    /// <inheritdoc />
    public bool WasRecentlyProcessed(AlertDto alert)
    {
        // Build cache key: fingerprint:status (separate CREATE and CANCEL)
        var cacheKey = GetCacheKey(alert);

        // Check if suppression window is 0 (empty string means no suppression)
        var windowSeconds = GetSuppressionWindowSeconds(alert);
        if (windowSeconds == 0)
        {
            return false; // No suppression requested
        }

        if (!_entries.TryGetValue(cacheKey, out var entry))
        {
            return false; // Never processed, don't suppress
        }

        // Tick-based comparison
        var currentTick = _centralTimer.TickCount;
        var ageTicks = currentTick - entry.ProcessedAtTick;
        var wasRecentlyProcessed = ageTicks < entry.WindowTicks;

        if (wasRecentlyProcessed)
        {
            var ageSeconds = ageTicks * _centralTimer.TickIntervalSeconds;
            var windowSecondsActual = entry.WindowTicks * _centralTimer.TickIntervalSeconds;
            _logger.LogDebug(
                "Alert {Name} ({Status}) was recently processed. Last processed {AgeTicks} ticks ({AgeSeconds}s) ago, window={WindowTicks} ticks ({WindowSeconds}s). Fingerprint={Fingerprint}",
                alert.Name, alert.Status, ageTicks, ageSeconds, entry.WindowTicks, windowSecondsActual, alert.Fingerprint);
        }

        return wasRecentlyProcessed;
    }

    /// <inheritdoc />
    public void MarkAsProcessed(AlertDto alert)
    {
        // Build cache key: fingerprint:status (separate CREATE and CANCEL)
        var cacheKey = GetCacheKey(alert);
        var windowSeconds = GetSuppressionWindowSeconds(alert);

        // Only track if suppression is enabled (windowSeconds > 0)
        if (windowSeconds > 0)
        {
            var windowTicks = windowSeconds / _centralTimer.TickIntervalSeconds;
            if (windowTicks < 1) windowTicks = 1; // Minimum 1 tick

            _entries[cacheKey] = new SuppressionEntry
            {
                ProcessedAtTick = _centralTimer.TickCount,
                WindowTicks = windowTicks,
                ProcessedAtTimestamp = _centralTimer.HeartbeatTimestamp // For logging only
            };

            _logger.LogDebug(
                "Marked alert {Name} ({Status}) as processed with {WindowTicks} ticks ({WindowSeconds}s) suppression window. Fingerprint={Fingerprint}",
                alert.Name, alert.Status, windowTicks, windowSeconds, alert.Fingerprint);
        }
        else
        {
            _logger.LogDebug(
                "Alert {Name} ({Status}) processed with no suppression (suppress_window is empty). Fingerprint={Fingerprint}",
                alert.Name, alert.Status, alert.Fingerprint);
        }
    }

    /// <inheritdoc />
    public void UnmarkAsProcessed(AlertDto alert)
    {
        var cacheKey = GetCacheKey(alert);
        var removed = _entries.TryRemove(cacheKey, out _);

        if (removed)
        {
            _logger.LogDebug(
                "Unmarked alert {Name} ({Status}) from suppression cache (NOC processing failed). Fingerprint={Fingerprint}",
                alert.Name, alert.Status, alert.Fingerprint);
        }
    }

    /// <inheritdoc />
    public void ClearFingerprint(string fingerprint)
    {
        var createKey = $"{fingerprint}:{AlertStatus.CREATE}";
        var cancelKey = $"{fingerprint}:{AlertStatus.CANCEL}";

        var removedCreate = _entries.TryRemove(createKey, out _);
        var removedCancel = _entries.TryRemove(cancelKey, out _);

        if (removedCreate || removedCancel)
        {
            _logger.LogDebug(
                "Cleared suppression cache for fingerprint {Fingerprint}: CREATE={RemovedCreate}, CANCEL={RemovedCancel}",
                fingerprint, removedCreate, removedCancel);
        }
    }

    /// <summary>
    /// Build cache key from alert fingerprint and status.
    /// Format: "fingerprint:status" (e.g., "prometheus:CREATE", "prometheus:CANCEL")
    /// This allows CREATE and CANCEL alerts to be suppressed independently.
    /// </summary>
    private static string GetCacheKey(AlertDto alert)
    {
        return $"{alert.Fingerprint}:{alert.Status}";
    }

    /// <summary>
    /// Get suppression window in seconds for an alert based on SuppressWindow property, annotations, or default.
    /// Returns 0 for empty string (no suppression), or DefaultNoc based on status for missing/invalid.
    /// </summary>
    private int GetSuppressionWindowSeconds(AlertDto alert)
    {
        // 1. Try to get from SuppressWindow property (preferred - set by K8sLayer, Watchdog, or API)
        if (alert.SuppressWindow.HasValue)
        {
            var seconds = (int)alert.SuppressWindow.Value.TotalSeconds;
            _logger.LogDebug(
                "Using SuppressWindow property for {Name} ({Status}): {Seconds}s",
                alert.Name, alert.Status, seconds);
            return seconds;
        }

        // 2. Try to get from annotations (supports formats like "120s", "4m", "8h", "3d")
        if (alert.Annotations.TryGetValue("suppress_window", out var windowStr))
        {
            // Empty string means no suppression (explicit opt-out)
            if (string.IsNullOrWhiteSpace(windowStr))
            {
                _logger.LogDebug(
                    "No suppression for {Name} ({Status}): suppress_window is empty",
                    alert.Name, alert.Status);
                return 0;
            }

            // Try to parse the value
            if (TimeSpanParser.TryParseToSeconds(windowStr, out var seconds))
            {
                _logger.LogDebug(
                    "Using suppress_window from annotation for {Name} ({Status}): {Window} ({Seconds}s)",
                    alert.Name, alert.Status, windowStr, seconds);
                return seconds;
            }
            else
            {
                // Invalid format - fall through to default
                _logger.LogWarning(
                    "Invalid suppress_window annotation for {Name} ({Status}): '{Window}'. Using DefaultNoc",
                    alert.Name, alert.Status, windowStr);
            }
        }

        // 3. Use DefaultNoc for all other cases (invalid/missing suppress_window)
        // Use status-aware defaults: CREATE vs CANCEL
        var defaultConfig = alert.Status == AlertStatus.CREATE
            ? _config.CreateNocBehavior
            : _config.CancelNocBehavior;

        var defaultSeconds = TimeSpanParser.ParseToSeconds(defaultConfig.SuppressWindow);
        _logger.LogDebug(
            "Using DefaultNoc suppression window for {Name} ({Status}): {Window} ({Seconds}s)",
            alert.Name, alert.Status, defaultConfig.SuppressWindow, defaultSeconds);
        return defaultSeconds;
    }
}

/// <summary>
/// Entry in the suppression cache.
/// Uses tick-based timing for consistency with CentralTimer.
/// </summary>
internal class SuppressionEntry
{
    /// <summary>
    /// Tick count when this alert was processed
    /// </summary>
    public long ProcessedAtTick { get; set; }

    /// <summary>
    /// Suppression window in ticks
    /// </summary>
    public int WindowTicks { get; set; }

    /// <summary>
    /// Timestamp when processed (for logging only)
    /// </summary>
    public DateTime ProcessedAtTimestamp { get; set; }
}

