using Argus.Models;
using Argus.Services.AlertsVector;
using Argus.Services.CentralTimer;
using Argus.Services.Metrics;
using Microsoft.Extensions.Logging;

namespace Argus.Services.Noc;

/// <summary>
/// Service that takes snapshots of the alerts vector every 30 seconds
/// and enqueues decisions for asynchronous processing.
/// Uses CentralTimer.HeartbeatTimestamp for consistent timestamps across all operations within a tick.
///
/// Enqueue Strategy:
/// - First CREATE alert (highest priority active alert)
/// - All CANCEL alerts (regardless of priority)
/// </summary>
public interface INocSnapshotService
{
    /// <summary>
    /// Take a snapshot of the alerts vector and enqueue decisions
    /// </summary>
    void TakeSnapshot(string correlationId);
}

public class NocSnapshotService : INocSnapshotService
{
    private readonly ILogger<NocSnapshotService> _logger;
    private readonly ICentralTimerService _centralTimer;
    private readonly IAlertsVectorService _alertsVector;
    private readonly INocQueueService _nocQueue;
    private readonly ISuppressionCache _suppressionCache;
    private readonly IArgusMetrics _metrics;

    public NocSnapshotService(
        ILogger<NocSnapshotService> logger,
        ICentralTimerService centralTimer,
        IAlertsVectorService alertsVector,
        INocQueueService nocQueue,
        ISuppressionCache suppressionCache,
        IArgusMetrics metrics)
    {
        _logger = logger;
        _centralTimer = centralTimer;
        _alertsVector = alertsVector;
        _nocQueue = nocQueue;
        _suppressionCache = suppressionCache;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public void TakeSnapshot(string correlationId)
    {
        var startTime = DateTime.UtcNow;

        // Clean up stale CREATE alerts before taking snapshot
        _alertsVector.CleanupExpiredAlerts();

        var snapshot = _alertsVector.GetSnapshot();

        // Count alerts by status
        var createCount = snapshot.Count(a => a.Status == AlertStatus.CREATE);
        var cancelCount = snapshot.Count(a => a.Status == AlertStatus.CANCEL);

        // Update vector state metrics
        _metrics.SetAlertsVectorSize(snapshot.Count);
        _metrics.SetAlertsVectorByStatus(AlertStatus.CREATE, createCount);
        _metrics.SetAlertsVectorByStatus(AlertStatus.CANCEL, cancelCount);
        _metrics.SetNocQueueDepth(_nocQueue.GetQueueDepth());

        // INFO: Log snapshot summary with active alerts count
        if (createCount > 0)
        {
            // Build active alerts summary for INFO log
            var activeAlerts = snapshot.Where(a => a.Status == AlertStatus.CREATE).ToList();
            var alertNames = string.Join(", ", activeAlerts.Select(a => $"{a.Name}(P{a.Priority})"));

            _logger.LogInformation(
                "NOC Snapshot: {Create} active alert(s) [{AlertNames}], Queue depth: {QueueDepth}. CorrelationId={CorrelationId}",
                createCount, alertNames, _nocQueue.GetQueueDepth(), correlationId);
        }
        else
        {
            // DEBUG: Log when no active alerts (system healthy)
            _logger.LogDebug(
                "NOC Snapshot: No active alerts, Queue depth: {QueueDepth}. CorrelationId={CorrelationId}",
                _nocQueue.GetQueueDepth(), correlationId);
        }

        // DEBUG: Log each alert in priority order (detailed view)
        for (int i = 0; i < snapshot.Count; i++)
        {
            var alert = snapshot[i];
            _logger.LogDebug(
                "  [{Index}] Priority={Priority} Status={Status} Name={Name} Summary={Summary} Fingerprint={Fingerprint} ExecutionId={ExecutionId}",
                i, alert.Priority, alert.Status, alert.Name, alert.Summary, alert.Fingerprint, alert.ExecutionId);
        }

        // Enqueue decisions
        EnqueueDecisions(snapshot, correlationId);

        // Record snapshot duration
        _metrics.RecordSnapshotDuration(DateTime.UtcNow - startTime);
    }

    private void EnqueueDecisions(List<AlertDto> alerts, string correlationId)
    {
        // Find first CREATE alert (highest priority active alert)
        var firstCreate = alerts.FirstOrDefault(a => a.Status == AlertStatus.CREATE);

        // Find all CANCEL alerts
        var allCancels = alerts.Where(a => a.Status == AlertStatus.CANCEL).ToList();

        // Use HeartbeatTimestamp for consistent snapshot time across all decisions
        var snapshotTime = _centralTimer.HeartbeatTimestamp;

        // Enqueue first CREATE (if not suppressed)
        if (firstCreate != null)
        {
            if (!_suppressionCache.WasRecentlyProcessed(firstCreate))
            {
                _nocQueue.Enqueue(new NocDecision
                {
                    Type = NocDecisionType.HandleCreate,
                    Alert = firstCreate,
                    SnapshotTime = snapshotTime,
                    CorrelationId = correlationId
                });

                _suppressionCache.MarkAsProcessed(firstCreate);

                _logger.LogDebug(
                    "Enqueued CREATE alert: {Name} (Priority={Priority}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    firstCreate.Name, firstCreate.Priority, correlationId, firstCreate.ExecutionId);
            }
            else
            {
                // INFO: Alert suppression is important operational info
                _logger.LogInformation(
                    "CREATE alert suppressed: {Name} (Priority={Priority}, within suppression window). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    firstCreate.Name, firstCreate.Priority, correlationId, firstCreate.ExecutionId);
            }
        }
        else
        {
            _logger.LogDebug(
                "No active alerts. System is healthy. CorrelationId={CorrelationId}",
                correlationId);
        }

        // Enqueue all CANCEL alerts (filter out suppressed)
        if (allCancels.Any())
        {
            // Filter out suppressed CANCELs
            var nonSuppressedCancels = allCancels
                .Where(a => !_suppressionCache.WasRecentlyProcessed(a))
                .ToList();

            if (nonSuppressedCancels.Any())
            {
                _nocQueue.Enqueue(new NocDecision
                {
                    Type = NocDecisionType.HandleCancels,
                    Alerts = nonSuppressedCancels,
                    SnapshotTime = snapshotTime,
                    CorrelationId = correlationId
                });

                foreach (var cancel in nonSuppressedCancels)
                {
                    _suppressionCache.MarkAsProcessed(cancel);
                }

                _logger.LogDebug(
                    "Enqueued {Count} CANCEL alerts. CorrelationId={CorrelationId}",
                    nonSuppressedCancels.Count, correlationId);
            }
            else
            {
                _logger.LogDebug(
                    "Skipped {Count} CANCEL alerts - all suppressed. CorrelationId={CorrelationId}",
                    allCancels.Count, correlationId);
            }
        }
    }
}

