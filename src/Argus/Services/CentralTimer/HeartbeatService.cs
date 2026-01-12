using System.Text.Json;
using Argus.Configuration;
using Argus.Models;
using Argus.Services.LeaderElection;
using Argus.Services.Metrics;
using Argus.Services.Noc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.CentralTimer;

/// <summary>
/// Unified Heartbeat Service - combines file and HTTP heartbeat functionality.
/// Checks LivenessVector health to decide behavior:
/// - HEALTHY: Send HTTP heartbeat (Phase 1 + Phase 2) + Write file heartbeat (normal)
/// - UNHEALTHY: Skip HTTP heartbeat + Write file heartbeat (enriched with unhealthy callback details)
///
/// Two-phase NOC handling:
/// - Phase 1 (Send): Only leader sends actual HTTP to NOC
/// - Phase 2 (Verify): Both leader and follower verify with NOC
///
/// Circuit breaker for file writes (leader only):
/// - If Phase 2 verify fails → stop writing heartbeat file
/// - When Phase 2 succeeds again → resume writing heartbeat file
/// </summary>
public interface IHeartbeatService
{
    /// <summary>Count of heartbeat files written.</summary>
    long FileWriteCount { get; }

    /// <summary>Count of HTTP heartbeats sent.</summary>
    long HttpSendCount { get; }

    /// <summary>Whether NOC verification is healthy (circuit breaker state).</summary>
    bool IsNocVerifyHealthy { get; }

    /// <summary>Start the service by registering the callback with CentralTimer.</summary>
    void Start();
}

/// <summary>
/// Unified Heartbeat Service implementation.
/// </summary>
public class HeartbeatService : IHeartbeatService
{
    private const string CallbackName = "Heartbeat";

    private readonly ILogger<HeartbeatService> _logger;
    private readonly ICentralTimerService _centralTimer;
    private readonly ILivenessVectorService _livenessVector;
    private readonly ILeaderElectionService _leaderElection;
    private readonly IArgusMetrics _metrics;
    private readonly INocHttpClient _nocHttpClient;
    private readonly INocHealthService _nocHealthService;
    private readonly NocConfiguration _nocConfig;
    private readonly HeartbeatConfiguration _heartbeatConfig;
    private readonly FileHeartbeatConfiguration _fileConfig;
    private readonly HttpHeartbeatConfiguration _httpConfig;
    private int _intervalTicks;

    private long _fileWriteCount;
    private long _httpSendCount;
    private volatile bool _nocVerifyHealthy = true; // Per-heartbeat NOC verify state
    private volatile bool _livenessHealthy = true; // Liveness state
    private volatile bool _nocCircuitBreakerTripped = false; // NOC circuit breaker state (across all NOC sources)

    public long FileWriteCount => Interlocked.Read(ref _fileWriteCount);
    public long HttpSendCount => Interlocked.Read(ref _httpSendCount);
    public bool IsNocVerifyHealthy => _nocVerifyHealthy;
    public bool IsLivenessHealthy => _livenessHealthy;
    public bool IsNocCircuitBreakerHealthy => !_nocCircuitBreakerTripped;

    public HeartbeatService(
        ILogger<HeartbeatService> logger,
        ICentralTimerService centralTimer,
        ILivenessVectorService livenessVector,
        ILeaderElectionService leaderElection,
        IArgusMetrics metrics,
        INocHttpClient nocHttpClient,
        INocHealthService nocHealthService,
        IOptions<NocConfiguration> nocConfig,
        IOptions<ArgusConfiguration> config)
    {
        _logger = logger;
        _centralTimer = centralTimer;
        _livenessVector = livenessVector;
        _leaderElection = leaderElection;
        _metrics = metrics;
        _nocHttpClient = nocHttpClient;
        _nocHealthService = nocHealthService;
        _nocConfig = nocConfig.Value;
        _heartbeatConfig = config.Value.Heartbeat;
        _fileConfig = _heartbeatConfig.File;
        _httpConfig = _heartbeatConfig.Http;

        _logger.LogInformation(
            "HeartbeatService initialized. NocEnabled={NocEnabled}, FileEnabled={FileEnabled}, HttpEnabled={HttpEnabled}, NocCircuitBreakerThreshold={Threshold}",
            _nocConfig.Enabled, _fileConfig.Enabled, _httpConfig.Enabled, _nocHealthService.FailureThreshold);
    }

    public void Start()
    {
        _intervalTicks = _heartbeatConfig.IntervalSeconds / _centralTimer.TickIntervalSeconds;
        if (_intervalTicks < 1) _intervalTicks = 1;

        var callback = new CentralTimerCallback(
            Name: CallbackName,
            IntervalTicks: _intervalTicks,
            Callback: OnHeartbeatTickAsync,
            IsGracePeriodAware: false);

        _centralTimer.RegisterCallback(callback);

        _logger.LogInformation(
            "HeartbeatService registered with CentralTimer. IntervalTicks={IntervalTicks} ({IntervalSeconds}s)",
            _intervalTicks, _heartbeatConfig.IntervalSeconds);
    }

    private async Task OnHeartbeatTickAsync(long tick, string correlationId, CancellationToken stoppingToken)
    {
        try
        {
            var isLeader = _leaderElection.IsLeader;
            var rolePrefix = isLeader ? "LEADER" : "FOLLOWER";

            // Check LivenessVector health - both leader and follower
            var unhealthyCallbacks = _livenessVector.GetUnhealthyCallbacks(tick);
            var allCallbacks = _livenessVector.GetSnapshot();
            var isHealthy = unhealthyCallbacks.Count == 0;

            // Detect state transitions
            var wasHealthy = _livenessHealthy;
            _livenessHealthy = isHealthy;

            // Log LivenessVector status report
            if (isHealthy)
            {
                _logger.LogInformation(
                    "{Role}: LivenessVector OK. Tracked={Count}, Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, _livenessVector.Count, tick, correlationId);
            }
            else
            {
                var unhealthyDetails = string.Join(", ", unhealthyCallbacks.Select(c => $"{c.Name}(age={c.AgeTicks},expected={c.ExpectedIntervalTicks})"));
                _logger.LogInformation(
                    "{Role}: LivenessVector UNHEALTHY. Tracked={Count}, Unhealthy=[{UnhealthyDetails}], Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, _livenessVector.Count, unhealthyDetails, tick, correlationId);
            }

            // Update metrics - both leader and follower
            _metrics.SetLivenessVectorHealthy(isHealthy);
            _metrics.SetLivenessVectorSize(_livenessVector.Count);
            _metrics.SetLivenessUnhealthyCallbackCount(unhealthyCallbacks.Count);

            // Handle liveness state transitions
            if (wasHealthy && !isHealthy)
            {
                // Transition: HEALTHY -> UNHEALTHY
                // Write final diagnostic file, then stop both NOC heartbeat and file
                _logger.LogWarning(
                    "{Role}: LivenessVector became UNHEALTHY - stopping NOC heartbeat and writing final diagnostic. Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, tick, correlationId);

                if (_fileConfig.Enabled && isLeader)
                {
                    await WriteFileHeartbeatAsync(tick, correlationId, allCallbacks, unhealthyCallbacks, rolePrefix,
                        unhealthyReason: "LIVENESS_FAILURE", stoppingToken);
                }
                return;
            }

            if (!wasHealthy && isHealthy)
            {
                // Transition: UNHEALTHY -> HEALTHY (recovered)
                _logger.LogInformation(
                    "{Role}: LivenessVector recovered - resuming NOC heartbeat. Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, tick, correlationId);
            }

            // If liveness currently unhealthy (and not first detection), skip NOC heartbeat and file
            if (!isHealthy)
            {
                _logger.LogDebug(
                    "{Role}: Skipping heartbeat (liveness unhealthy, final diagnostic already written). Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, tick, correlationId);
                return;
            }

            // LIVENESS HEALTHY path: Send NOC heartbeat

            // Send HTTP heartbeat - both leader and follower participate
            // Phase 1: Only leader sends actual HTTP
            // Phase 2: Both leader and follower verify
            bool nocVerifySuccess = true;
            if (_httpConfig.Enabled && _nocConfig.Enabled)
            {
                nocVerifySuccess = await SendHttpHeartbeatAsync(tick, correlationId, isLeader, rolePrefix, stoppingToken);

                // Record success/failure to NOC health service
                if (nocVerifySuccess)
                {
                    _nocHealthService.RecordSuccess();
                }
                else
                {
                    _nocHealthService.RecordFailure();
                }
            }
            else if (_httpConfig.Enabled && !_nocConfig.Enabled)
            {
                _logger.LogDebug(
                    "{Role}: Skipping NOC HTTP heartbeat (NOC disabled). Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, tick, correlationId);
            }

            // Update per-heartbeat NOC verify state (for logging)
            var previousNocVerifyState = _nocVerifyHealthy;
            _nocVerifyHealthy = nocVerifySuccess;

            if (previousNocVerifyState != nocVerifySuccess)
            {
                if (nocVerifySuccess)
                {
                    _logger.LogInformation(
                        "{Role}: NOC heartbeat verify recovered. Tick={Tick}, CorrelationId={CorrelationId}",
                        rolePrefix, tick, correlationId);
                }
                else
                {
                    _logger.LogWarning(
                        "{Role}: NOC heartbeat verify failed. Tick={Tick}, CorrelationId={CorrelationId}",
                        rolePrefix, tick, correlationId);
                }
            }

            // Check NOC circuit breaker state (across all NOC sources: heartbeat + alerts)
            var wasNocCbHealthy = !_nocCircuitBreakerTripped;
            var isNocCbHealthy = _nocHealthService.IsHealthy;
            _nocCircuitBreakerTripped = !isNocCbHealthy;

            // Handle NOC circuit breaker state transitions
            if (wasNocCbHealthy && !isNocCbHealthy)
            {
                // Transition: NOC CB HEALTHY -> TRIPPED
                _logger.LogWarning(
                    "{Role}: NOC circuit breaker TRIPPED (ConsecutiveFailures={Failures}, Threshold={Threshold}) - stopping file heartbeat and writing final diagnostic. Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, _nocHealthService.ConsecutiveFailures, _nocHealthService.FailureThreshold, tick, correlationId);

                if (_fileConfig.Enabled && isLeader)
                {
                    await WriteFileHeartbeatAsync(tick, correlationId, allCallbacks, unhealthyCallbacks, rolePrefix,
                        unhealthyReason: "NOC_FAILURE", stoppingToken);
                }
                return;
            }

            if (!wasNocCbHealthy && isNocCbHealthy)
            {
                // Transition: NOC CB TRIPPED -> HEALTHY (recovered)
                _logger.LogInformation(
                    "{Role}: NOC circuit breaker recovered - resuming file heartbeat. Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, tick, correlationId);
            }

            // If NOC circuit breaker is tripped (and not first detection), skip file heartbeat
            if (!isNocCbHealthy)
            {
                _logger.LogDebug(
                    "{Role}: Skipping file heartbeat (NOC circuit breaker tripped, final diagnostic already written). Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, tick, correlationId);
                return;
            }

            // LIVENESS HEALTHY + NOC CB HEALTHY: Write file heartbeat - only leader writes
            if (_fileConfig.Enabled && isLeader)
            {
                await WriteFileHeartbeatAsync(tick, correlationId, allCallbacks, unhealthyCallbacks, rolePrefix,
                    unhealthyReason: null, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Heartbeat callback failed. Tick={Tick}, CorrelationId={CorrelationId}",
                tick, correlationId);
        }
        finally
        {
            // Always stamp - callback executed (didn't silently die)
            _livenessVector.RecordExecution(CallbackName, _intervalTicks, tick);
        }
    }

    /// <summary>
    /// Send HTTP heartbeat with two-phase process.
    /// Phase 1: Only leader sends actual HTTP to NOC.
    /// Phase 2: Both leader and follower verify with NOC.
    /// </summary>
    /// <returns>True if Phase 2 verify succeeded, false otherwise.</returns>
    private async Task<bool> SendHttpHeartbeatAsync(long tick, string correlationId, bool isLeader, string rolePrefix, CancellationToken stoppingToken)
    {
        try
        {
            var sendCount = Interlocked.Increment(ref _httpSendCount);

            // Clone the configured payload and update dynamic fields
            var payload = _httpConfig.Payload.Clone();
            payload.Message = $"Argus heartbeat - Tick {tick} CorrelationId={correlationId}";

            // Create a minimal AlertDto for the NOC HTTP client
            var heartbeatAlert = new AlertDto
            {
                Priority = 0,
                Name = "ArgusHeartbeat",
                Fingerprint = payload.SuppressionKey,
                Source = payload.Source,
                Status = AlertStatus.CANCEL, // Heartbeat is always healthy (level=1)
                Summary = "Argus heartbeat",
                Description = payload.Message,
                Payload = payload,
                SendToNoc = true
            };

            NocHttpPayload? sentPayload = null;

            // Phase 1: Send HTTP to NOC - only leader sends
            if (isLeader)
            {
                _logger.LogDebug(
                    "{Role}: Sending NOC HTTP POST for heartbeat. Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, tick, correlationId);

                var sendResult = await _nocHttpClient.SendAlertAsync(heartbeatAlert, correlationId, stoppingToken);

                if (!sendResult.IsSuccess)
                {
                    _logger.LogWarning(
                        "{Role}: NOC HTTP POST failed for heartbeat (StatusCode={StatusCode}, Error={Error}). Continuing to verification. Tick={Tick}, CorrelationId={CorrelationId}",
                        rolePrefix, sendResult.StatusCode, sendResult.ErrorMessage, tick, correlationId);
                    // Continue to Phase 2 even if Phase 1 fails - NOC might have received the data
                }
                else
                {
                    sentPayload = sendResult.SentPayload;

                    _logger.LogDebug(
                        "{Role}: NOC HTTP POST succeeded for heartbeat #{SendCount}. StatusCode={StatusCode}, Tick={Tick}, CorrelationId={CorrelationId}",
                        rolePrefix, sendCount, sendResult.StatusCode, tick, correlationId);
                }
            }
            else
            {
                _logger.LogDebug(
                    "{Role}: Sending NOC HTTP POST for heartbeat (skipped). Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, tick, correlationId);
            }

            // Phase 2: Verify with NOC - both leader and follower
            if (sentPayload == null)
            {
                // Follower or leader without sent payload - create from alert
                sentPayload = NocHttpPayload.FromAlert(heartbeatAlert, heartbeatAlert.Payload);
            }

            _logger.LogDebug(
                "{Role}: Sending verification HTTP POST for heartbeat. Tick={Tick}, CorrelationId={CorrelationId}",
                rolePrefix, tick, correlationId);

            var verifyResult = await _nocHttpClient.VerifyAlertAsync(heartbeatAlert, sentPayload, correlationId, stoppingToken);

            if (!verifyResult.IsSuccess)
            {
                _logger.LogWarning(
                    "{Role}: Verification HTTP POST failed for heartbeat (StatusCode={StatusCode}, Error={Error}). Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, verifyResult.StatusCode, verifyResult.ErrorMessage, tick, correlationId);
                return false;
            }

            if (!verifyResult.ComparisonSuccess)
            {
                _logger.LogWarning(
                    "{Role}: Verification comparison failed for heartbeat. Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, tick, correlationId);
                return false;
            }

            _metrics.IncrementNocSent();

            _logger.LogDebug(
                "{Role}: NOC HTTP heartbeat completed (both phases successful). Tick={Tick}, CorrelationId={CorrelationId}",
                rolePrefix, tick, correlationId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Role}: Failed to send HTTP heartbeat. Tick={Tick}, CorrelationId={CorrelationId}",
                rolePrefix, tick, correlationId);
            return false;
        }
    }

    /// <summary>
    /// Write file heartbeat with optional unhealthy reason.
    /// </summary>
    /// <param name="unhealthyReason">
    /// null = normal healthy heartbeat
    /// "LIVENESS_FAILURE" = final diagnostic before stopping due to liveness failure
    /// "NOC_FAILURE" = final diagnostic before stopping due to NOC circuit breaker trip
    /// </param>
    private async Task WriteFileHeartbeatAsync(
        long tick,
        string correlationId,
        IReadOnlyList<CallbackLiveness> allCallbacks,
        IReadOnlyList<UnhealthyCallback> unhealthyCallbacks,
        string rolePrefix,
        string? unhealthyReason,
        CancellationToken stoppingToken)
    {
        var writeCount = Interlocked.Increment(ref _fileWriteCount);
        var isLivenessHealthy = unhealthyCallbacks.Count == 0;
        var isNocCbHealthy = _nocHealthService.IsHealthy;

        // Determine overall status - must be both liveness healthy AND NOC CB healthy
        var overallStatus = (isLivenessHealthy && isNocCbHealthy) ? "HEALTHY" : "UNHEALTHY";

        // Build callback diagnostic with healthy/unhealthy status
        var unhealthyNames = unhealthyCallbacks.Select(u => u.Name).ToHashSet();

        var content = new
        {
            tick,
            correlationId,
            status = overallStatus,
            unhealthyReason = unhealthyReason,
            isLeader = true,
            nocVerifyHealthy = _nocVerifyHealthy,
            nocCircuitBreaker = new
            {
                isHealthy = isNocCbHealthy,
                consecutiveFailures = _nocHealthService.ConsecutiveFailures,
                failureThreshold = _nocHealthService.FailureThreshold
            },
            livenessVector = new
            {
                isHealthy = isLivenessHealthy,
                totalCount = allCallbacks.Count,
                healthyCount = allCallbacks.Count - unhealthyCallbacks.Count,
                unhealthyCount = unhealthyCallbacks.Count,
                callbacks = allCallbacks.Select(c => new
                {
                    name = c.Name,
                    lastExecutionTick = c.LastExecutionTick,
                    expectedIntervalTicks = c.ExpectedIntervalTicks,
                    isHealthy = !unhealthyNames.Contains(c.Name)
                }).ToList(),
                unhealthyDetails = unhealthyCallbacks.Select(u => new
                {
                    name = u.Name,
                    expectedIntervalTicks = u.ExpectedIntervalTicks,
                    lastExecutionTick = u.LastExecutionTick,
                    ageTicks = u.AgeTicks,
                    thresholdTicks = u.ThresholdTicks
                }).ToList()
            }
        };

        var json = JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true });

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(_fileConfig.DestinationPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Atomic write: temp file + rename
            var tempPath = _fileConfig.DestinationPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, stoppingToken);
            File.Move(tempPath, _fileConfig.DestinationPath, overwrite: true);

            // Update metrics - overall status (both liveness and NOC CB must be healthy)
            _metrics.SetExternalMonitorStatus(overallStatus == "HEALTHY");
            _metrics.SetExternalMonitorHeartbeatAge(0);

            if (unhealthyReason != null)
            {
                _logger.LogWarning(
                    "{Role}: Heartbeat file written #{WriteCount} (FINAL DIAGNOSTIC). Status={Status}, Reason={Reason}, Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, writeCount, overallStatus, unhealthyReason, tick, correlationId);
            }
            else
            {
                _logger.LogDebug(
                    "{Role}: Heartbeat file written #{WriteCount}. Status={Status}, Tick={Tick}, CorrelationId={CorrelationId}",
                    rolePrefix, writeCount, overallStatus, tick, correlationId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Role}: Failed to write heartbeat file to {Path}. CorrelationId={CorrelationId}",
                rolePrefix, _fileConfig.DestinationPath, correlationId);
        }
    }
}
