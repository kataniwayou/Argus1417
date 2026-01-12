using System.Collections.Concurrent;
using Argus.Configuration;
using Argus.Models;
using Argus.Services.AlertsVector;
using Argus.Services.LeaderElection;
using Argus.Services.Metrics;
using Argus.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.Noc;

/// <summary>
/// Service to manage NOC decision queue and process decisions asynchronously.
/// Prevents blocking snapshots when sending HTTP requests to NOC service.
/// Only the leader sends NOC HTTP calls; followers skip them.
/// </summary>
public interface INocQueueService
{
    /// <summary>
    /// Enqueue a NOC decision for processing
    /// </summary>
    void Enqueue(NocDecision decision);

    /// <summary>
    /// Get current queue depth
    /// </summary>
    int GetQueueDepth();
}

public class NocQueueService : BackgroundService, INocQueueService
{
    private readonly ILogger<NocQueueService> _logger;
    private readonly IAlertsVectorService _alertsVector;
    private readonly ILeaderElectionService _leaderElection;
    private readonly IArgusMetrics _metrics;
    private readonly INocHttpClient _nocHttpClient;
    private readonly ISuppressionCache _suppressionCache;
    private readonly INocHealthService _nocHealthService;
    private readonly NocConfiguration _nocConfig;
    private readonly ConcurrentQueue<NocDecision> _queue = new();

    // Track sent payloads for verification (keyed by fingerprint)
    private readonly ConcurrentDictionary<string, NocHttpPayload> _sentPayloads = new();

    public NocQueueService(
        ILogger<NocQueueService> logger,
        IAlertsVectorService alertsVector,
        ILeaderElectionService leaderElection,
        IArgusMetrics metrics,
        INocHttpClient nocHttpClient,
        ISuppressionCache suppressionCache,
        INocHealthService nocHealthService,
        IOptions<NocConfiguration> nocConfig)
    {
        _logger = logger;
        _alertsVector = alertsVector;
        _leaderElection = leaderElection;
        _metrics = metrics;
        _nocHttpClient = nocHttpClient;
        _suppressionCache = suppressionCache;
        _nocHealthService = nocHealthService;
        _nocConfig = nocConfig.Value;
    }

    /// <inheritdoc />
    public void Enqueue(NocDecision decision)
    {
        _queue.Enqueue(decision);

        // Track NOC decision metric
        _metrics.IncrementNocDecisions(decision.Type);

        _logger.LogDebug(
            "Enqueued NOC decision: Type={Type}, QueueDepth={Depth}. CorrelationId={CorrelationId}",
            decision.Type, _queue.Count, decision.CorrelationId);
    }

    /// <inheritdoc />
    public int GetQueueDepth() => _queue.Count;
    
    /// <summary>
    /// Background worker that processes the queue
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NOC Queue Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Process queue
                if (_queue.TryDequeue(out var decision))
                {
                    await ProcessDecisionAsync(decision, stoppingToken);
                }
                else
                {
                    // No work, wait a bit
                    await Task.Delay(100, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in NOC queue worker");
                await Task.Delay(1000, stoppingToken); // Wait before retrying
            }
        }

        _logger.LogInformation("NOC Queue Service stopped");
    }
    
    private async Task ProcessDecisionAsync(NocDecision decision, CancellationToken cancellationToken)
    {
        try
        {
            switch (decision.Type)
            {
                case NocDecisionType.HandleCreate:
                    await HandleCreateAlertAsync(decision, cancellationToken);
                    break;
                case NocDecisionType.HandleCancels:
                    await HandleCancelAlertsAsync(decision, cancellationToken);
                    break;
                default:
                    _logger.LogWarning("Unknown NOC decision type: {Type}. CorrelationId={CorrelationId}",
                        decision.Type, decision.CorrelationId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing NOC decision. Type={Type}, CorrelationId={CorrelationId}",
                decision.Type, decision.CorrelationId);
        }
    }

    private async Task HandleCreateAlertAsync(NocDecision decision, CancellationToken cancellationToken)
    {
        if (decision.Alert == null)
        {
            _logger.LogWarning("HandleCreate decision has null Alert. CorrelationId={CorrelationId}",
                decision.CorrelationId);
            return;
        }

        // Re-check alert is still CREATE before handling
        var currentAlert = GetCurrentAlert(decision.Alert.Fingerprint);
        if (currentAlert?.Status != AlertStatus.CREATE)
        {
            _logger.LogDebug(
                "Skipping CREATE alert {Name} - status changed to {Status}. CorrelationId={CorrelationId}",
                decision.Alert.Name, currentAlert?.Status, decision.CorrelationId);
            return;
        }

        // Check if alert should be sent to NOC
        if (!currentAlert.SendToNoc)
        {
            _logger.LogDebug(
                "NOC Decision: Skipping HTTP POST for CREATE alert {Name} (send_to_noc=false). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                currentAlert.Name, decision.CorrelationId, currentAlert.ExecutionId);
            return;
        }

        // Check if NOC HTTP is globally enabled
        if (!_nocConfig.Enabled)
        {
            _logger.LogDebug(
                "NOC Decision: Skipping HTTP POST for CREATE alert {Name} (NOC disabled). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                currentAlert.Name, decision.CorrelationId, currentAlert.ExecutionId);
            return;
        }

        var isLeader = _leaderElection.IsLeader;
        var rolePrefix = isLeader ? "LEADER" : "FOLLOWER";

        NocHttpPayload? sentPayload = null;

        // Phase 1: Create alert in NOC - only leader sends
        if (isLeader)
        {
            _logger.LogInformation(
                "{Role}: Sending NOC HTTP POST for CREATE alert {Name} (Priority={Priority}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                rolePrefix, currentAlert.Name, currentAlert.Priority, decision.CorrelationId, currentAlert.ExecutionId);

            var sendResult = await _nocHttpClient.SendAlertAsync(currentAlert, decision.CorrelationId, cancellationToken);

            if (!sendResult.IsSuccess)
            {
                _logger.LogWarning(
                    "{Role}: NOC HTTP POST failed for CREATE alert {Name} (StatusCode={StatusCode}, Error={Error}). Continuing to verification. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    rolePrefix, currentAlert.Name, sendResult.StatusCode, sendResult.ErrorMessage, decision.CorrelationId, currentAlert.ExecutionId);
                // Continue to Phase 2 even if Phase 1 fails - NOC might have received the data
            }
            else
            {
                sentPayload = sendResult.SentPayload;
                if (sentPayload != null)
                {
                    _sentPayloads[currentAlert.Fingerprint] = sentPayload;
                }

                _logger.LogDebug(
                    "{Role}: NOC HTTP POST succeeded for CREATE alert {Name}. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    rolePrefix, currentAlert.Name, decision.CorrelationId, currentAlert.ExecutionId);
            }
        }
        else
        {
            _logger.LogInformation(
                "{Role}: Sending NOC HTTP POST for CREATE alert {Name} (Priority={Priority}) (skipped). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                rolePrefix, currentAlert.Name, currentAlert.Priority, decision.CorrelationId, currentAlert.ExecutionId);

            // Follower retrieves sent payload from cache (leader should have stored it)
            _sentPayloads.TryGetValue(currentAlert.Fingerprint, out sentPayload);
        }

        // Second HTTP POST: Verify acceptance - both leader and follower send
        if (sentPayload == null)
        {
            // Create payload for verification if not available
            sentPayload = NocHttpPayload.FromAlert(currentAlert, currentAlert.Payload);
        }

        _logger.LogDebug(
            "{Role}: Sending verification HTTP POST for CREATE alert {Name}. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
            rolePrefix, currentAlert.Name, decision.CorrelationId, currentAlert.ExecutionId);

        var verifyResult = await _nocHttpClient.VerifyAlertAsync(currentAlert, sentPayload, decision.CorrelationId, cancellationToken);

        if (!verifyResult.IsSuccess)
        {
            _logger.LogWarning(
                "{Role}: Verification HTTP POST failed for CREATE alert {Name} (StatusCode={StatusCode}, Error={Error}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                rolePrefix, currentAlert.Name, verifyResult.StatusCode, verifyResult.ErrorMessage, decision.CorrelationId, currentAlert.ExecutionId);
            _suppressionCache.UnmarkAsProcessed(currentAlert);
            _nocHealthService.RecordFailure();
            return;
        }

        if (!verifyResult.ComparisonSuccess)
        {
            _logger.LogWarning(
                "{Role}: Verification comparison failed for CREATE alert {Name}. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                rolePrefix, currentAlert.Name, decision.CorrelationId, currentAlert.ExecutionId);
            _suppressionCache.UnmarkAsProcessed(currentAlert);
            _nocHealthService.RecordFailure();
            return;
        }

        _metrics.IncrementNocSent();
        _nocHealthService.RecordSuccess();

        _logger.LogInformation(
            "{Role}: NOC HTTP POST completed for CREATE alert {Name} (both phases successful). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
            rolePrefix, currentAlert.Name, decision.CorrelationId, currentAlert.ExecutionId);
    }

    private async Task HandleCancelAlertsAsync(NocDecision decision, CancellationToken cancellationToken)
    {
        if (decision.Alerts == null || !decision.Alerts.Any())
        {
            _logger.LogWarning("HandleCancels decision has no alerts. CorrelationId={CorrelationId}",
                decision.CorrelationId);
            return;
        }

        // Re-check which alerts are still CANCEL
        var stillCancels = decision.Alerts
            .Select(a => GetCurrentAlert(a.Fingerprint))
            .Where(a => a?.Status == AlertStatus.CANCEL)
            .ToList();

        if (!stillCancels.Any())
        {
            _logger.LogDebug(
                "Skipping CANCEL alerts - all status changed. CorrelationId={CorrelationId}",
                decision.CorrelationId);
            return;
        }

        // Separate by send_to_noc flag
        var alertsToSend = stillCancels.Where(a => a!.SendToNoc).ToList();
        var alertsToSkipSend = stillCancels.Where(a => !a!.SendToNoc).ToList();

        // Check if NOC HTTP is globally enabled
        if (!_nocConfig.Enabled)
        {
            // NOC disabled - skip HTTP but still remove alerts from vector
            foreach (var alert in alertsToSend)
            {
                _logger.LogDebug(
                    "NOC Decision: Skipping HTTP POST for CANCEL alert {Name} (NOC disabled). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    alert!.Name, decision.CorrelationId, alert.ExecutionId);

                var removed = _alertsVector.RemoveAlert(alert.Fingerprint);
                if (removed)
                {
                    _logger.LogDebug(
                        "Removed CANCEL alert from vector (NOC disabled): {Name} (Priority={Priority}, Fingerprint={Fingerprint}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                        alert.Name, alert.Priority, alert.Fingerprint, decision.CorrelationId, alert.ExecutionId);
                }
            }
        }
        else
        {
            // NOC enabled - process alerts that need NOC POST (send_to_noc=true)
            foreach (var alert in alertsToSend)
            {
                await ProcessCancelAlertWithNocAsync(alert!, decision.CorrelationId, cancellationToken);
            }
        }

        // Remove alerts with send_to_noc=false from vector (no NOC send needed)
        foreach (var alert in alertsToSkipSend)
        {
            _logger.LogDebug(
                "NOC Decision: Skipping HTTP POST for CANCEL alert {Name} (send_to_noc=false). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                alert!.Name, decision.CorrelationId, alert.ExecutionId);

            var removed = _alertsVector.RemoveAlert(alert.Fingerprint);
            if (removed)
            {
                _logger.LogDebug(
                    "Removed CANCEL alert from vector (send_to_noc=false): {Name} (Priority={Priority}, Fingerprint={Fingerprint}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    alert.Name, alert.Priority, alert.Fingerprint, decision.CorrelationId, alert.ExecutionId);
            }
        }
    }

    private async Task ProcessCancelAlertWithNocAsync(AlertDto alert, string correlationId, CancellationToken cancellationToken)
    {
        var isLeader = _leaderElection.IsLeader;
        var rolePrefix = isLeader ? "LEADER" : "FOLLOWER";

        NocHttpPayload? sentPayload = null;

        // Phase 1: Resolve alert in NOC - only leader sends
        if (isLeader)
        {
            _logger.LogInformation(
                "{Role}: Sending NOC HTTP POST for CANCEL alert {Name} (Priority={Priority}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                rolePrefix, alert.Name, alert.Priority, correlationId, alert.ExecutionId);

            var sendResult = await _nocHttpClient.SendAlertAsync(alert, correlationId, cancellationToken);

            if (!sendResult.IsSuccess)
            {
                _logger.LogWarning(
                    "{Role}: NOC HTTP POST failed for CANCEL alert {Name} (StatusCode={StatusCode}, Error={Error}). Continuing to verification. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    rolePrefix, alert.Name, sendResult.StatusCode, sendResult.ErrorMessage, correlationId, alert.ExecutionId);
                // Continue to Phase 2 even if Phase 1 fails - NOC might have received the data
            }
            else
            {
                sentPayload = sendResult.SentPayload;
                if (sentPayload != null)
                {
                    _sentPayloads[alert.Fingerprint] = sentPayload;
                }

                _logger.LogDebug(
                    "{Role}: NOC HTTP POST succeeded for CANCEL alert {Name}. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    rolePrefix, alert.Name, correlationId, alert.ExecutionId);
            }
        }
        else
        {
            _logger.LogDebug(
                "{Role}: Sending NOC HTTP POST for CANCEL alert {Name} (skipped). CorrelationId={CorrelationId}",
                rolePrefix, alert.Name, correlationId);

            // Follower retrieves sent payload from cache (leader should have stored it)
            _sentPayloads.TryGetValue(alert.Fingerprint, out sentPayload);
        }

        // Second HTTP POST: Verify acceptance - both leader and follower send
        if (sentPayload == null)
        {
            // Create payload for verification if not available
            sentPayload = NocHttpPayload.FromAlert(alert, alert.Payload);
        }

        _logger.LogDebug(
            "{Role}: Sending verification HTTP POST for CANCEL alert {Name}. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
            rolePrefix, alert.Name, correlationId, alert.ExecutionId);

        var verifyResult = await _nocHttpClient.VerifyAlertAsync(alert, sentPayload, correlationId, cancellationToken);

        if (!verifyResult.IsSuccess)
        {
            _logger.LogWarning(
                "{Role}: Verification HTTP POST failed for CANCEL alert {Name} (StatusCode={StatusCode}, Error={Error}). Alert NOT removed from vector. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                rolePrefix, alert.Name, verifyResult.StatusCode, verifyResult.ErrorMessage, correlationId, alert.ExecutionId);
            _suppressionCache.UnmarkAsProcessed(alert);
            _nocHealthService.RecordFailure();
            return;
        }

        if (!verifyResult.ComparisonSuccess)
        {
            _logger.LogWarning(
                "{Role}: Verification comparison failed for CANCEL alert {Name}. Alert NOT removed from vector. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                rolePrefix, alert.Name, correlationId, alert.ExecutionId);
            _suppressionCache.UnmarkAsProcessed(alert);
            _nocHealthService.RecordFailure();
            return;
        }

        // Both phases successful - remove alert from vector and clean up cache
        _sentPayloads.TryRemove(alert.Fingerprint, out _);
        _metrics.IncrementNocSent();
        _nocHealthService.RecordSuccess();
        var removed = _alertsVector.RemoveAlert(alert.Fingerprint);
        if (removed)
        {
            _logger.LogInformation(
                "{Role}: CANCEL alert removed from vector after successful verification: {Name} (Priority={Priority}, Fingerprint={Fingerprint}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                rolePrefix, alert.Name, alert.Priority, alert.Fingerprint, correlationId, alert.ExecutionId);
        }
    }

    private AlertDto? GetCurrentAlert(string fingerprint)
    {
        var snapshot = _alertsVector.GetSnapshot();
        return snapshot.FirstOrDefault(a => a.Fingerprint == fingerprint);
    }
}
