using Argus.Configuration;
using Argus.Services.CentralTimer;
using Argus.Services.Metrics;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace Argus.Services.LeaderElection;

/// <summary>
/// Kubernetes Lease-based leader election service.
/// All pods execute the same business logic; only the leader sends NOC HTTP calls.
/// Uses CentralTimer for periodic lease management.
/// Each callback stamps LivenessVector on success/failure (not exception).
/// </summary>
public class LeaderElectionService : ILeaderElectionService
{
    private const string CallbackName = "LeaderElection";

    private readonly ILogger<LeaderElectionService> _logger;
    private readonly ICentralTimerService _centralTimer;
    private readonly LeaderElectionConfiguration _config;
    private readonly IArgusMetrics _metrics;
    private readonly ILivenessVectorService _livenessVector;
    private readonly string _namespace;
    private readonly IKubernetes _client;
    private readonly int _intervalTicks;

    private volatile bool _isLeader;
    private volatile string? _currentLeaderIdentity;
    private readonly object _leadershipLock = new();

    public bool IsLeader => _isLeader;
    public string PodIdentity { get; }
    public string? CurrentLeaderIdentity => _currentLeaderIdentity;

    public event EventHandler<bool>? OnLeadershipChanged;

    public LeaderElectionService(
        ILogger<LeaderElectionService> logger,
        ICentralTimerService centralTimer,
        IOptions<ArgusConfiguration> options,
        IArgusMetrics metrics,
        ILivenessVectorService livenessVector)
    {
        _logger = logger;
        _centralTimer = centralTimer;
        _config = options.Value.LeaderElection;
        _metrics = metrics;
        _livenessVector = livenessVector;
        _namespace = options.Value.K8sLayer.Kubernetes.Namespace;

        // Get pod identity from environment variable (set via downward API)
        PodIdentity = Environment.GetEnvironmentVariable("POD_NAME")
            ?? $"argus-{Guid.NewGuid():N}";

        // Initialize K8s client
        var k8sConfig = options.Value.K8sLayer.Kubernetes.UseInClusterConfig
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _client = new Kubernetes(k8sConfig);

        // Register callback with Central Timer
        // Use RenewIntervalSeconds for the interval (e.g., 10s = 2 ticks at 5s per tick)
        _intervalTicks = _config.RenewIntervalSeconds / _centralTimer.TickIntervalSeconds;
        _centralTimer.RegisterCallback(new CentralTimerCallback(
            Name: CallbackName,
            IntervalTicks: _intervalTicks,
            Callback: OnLeaderElectionTickAsync,
            IsGracePeriodAware: false)); // Leader election must always run

        _logger.LogInformation(
            "LeaderElectionService initialized. PodIdentity={PodIdentity}, LeaseName={LeaseName}, " +
            "Namespace={Namespace}, LeaseDuration={LeaseDuration}s, Interval={Interval} ticks",
            PodIdentity, _config.LeaseName, _namespace,
            _config.LeaseDurationSeconds, _intervalTicks);
    }

    /// <summary>
    /// CentralTimer callback for leader election.
    /// Runs every RenewIntervalSeconds (default: 10 ticks).
    /// correlationId received from CentralTimer (single source of truth for tick correlation).
    /// Stamps LivenessVector on success/failure (not exception).
    /// </summary>
    private async Task OnLeaderElectionTickAsync(long tick, string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            if (_isLeader)
            {
                await RenewLeaseAsync(correlationId, cancellationToken);
            }
            else
            {
                await TryAcquireLeaseAsync(correlationId, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown requested - release leadership gracefully
            if (_isLeader)
            {
                _logger.LogInformation(
                    "Pod shutting down, releasing leadership. PodIdentity={PodIdentity} CorrelationId={CorrelationId}",
                    PodIdentity, correlationId);
                SetLeadershipStatus(false, null, correlationId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Leader election cycle failed. Tick={Tick}, PodIdentity={PodIdentity}, IsLeader={IsLeader} CorrelationId={CorrelationId}",
                tick, PodIdentity, _isLeader, correlationId);
        }
        finally
        {
            // Always stamp - callback executed (didn't silently die)
            _livenessVector.RecordExecution(CallbackName, _intervalTicks, tick);
        }
    }

    private async Task TryAcquireLeaseAsync(string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            var lease = await GetOrCreateLeaseAsync(correlationId, cancellationToken);
            if (lease == null) return;

            var holder = lease.Spec?.HolderIdentity;
            var renewTime = lease.Spec?.RenewTime;
            var leaseDuration = lease.Spec?.LeaseDurationSeconds ?? _config.LeaseDurationSeconds;

            // Check if lease is expired or held by us
            var isExpired = renewTime == null ||
                (DateTime.UtcNow - renewTime.Value.ToUniversalTime()).TotalSeconds > leaseDuration;
            var isHeldByUs = holder == PodIdentity;

            if (isExpired || isHeldByUs)
            {
                // Try to acquire the lease
                lease.Spec ??= new V1LeaseSpec();
                lease.Spec.HolderIdentity = PodIdentity;
                lease.Spec.LeaseDurationSeconds = _config.LeaseDurationSeconds;
                lease.Spec.AcquireTime = isHeldByUs ? lease.Spec.AcquireTime : DateTime.UtcNow;
                lease.Spec.RenewTime = DateTime.UtcNow;

                await _client.CoordinationV1.ReplaceNamespacedLeaseAsync(
                    lease, _config.LeaseName, _namespace, cancellationToken: cancellationToken);

                SetLeadershipStatus(true, PodIdentity, correlationId);
            }
            else
            {
                // Lease is held by another pod
                _currentLeaderIdentity = holder;

                if (_isLeader)
                {
                    // We lost leadership
                    SetLeadershipStatus(false, holder, correlationId);
                }
                else
                {
                    _logger.LogDebug(
                        "FOLLOWER: Lease held by {Leader}. PodIdentity={PodIdentity} CorrelationId={CorrelationId}",
                        holder, PodIdentity, correlationId);
                }
            }
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger.LogDebug(
                "FOLLOWER: Lost race for lease. PodIdentity={PodIdentity} CorrelationId={CorrelationId}",
                PodIdentity, correlationId);
        }
    }

    private async Task RenewLeaseAsync(string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            var lease = await _client.CoordinationV1.ReadNamespacedLeaseAsync(
                _config.LeaseName, _namespace, cancellationToken: cancellationToken);

            // Verify we still hold the lease
            if (lease.Spec?.HolderIdentity != PodIdentity)
            {
                _logger.LogWarning(
                    "LEADER: Lost leadership to {NewLeader}. PodIdentity={PodIdentity} CorrelationId={CorrelationId}",
                    lease.Spec?.HolderIdentity, PodIdentity, correlationId);
                SetLeadershipStatus(false, lease.Spec?.HolderIdentity, correlationId);
                return;
            }

            // Renew the lease
            lease.Spec.RenewTime = DateTime.UtcNow;

            await _client.CoordinationV1.ReplaceNamespacedLeaseAsync(
                lease, _config.LeaseName, _namespace, cancellationToken: cancellationToken);

            _logger.LogDebug(
                "LEADER: Lease renewed. PodIdentity={PodIdentity} CorrelationId={CorrelationId}",
                PodIdentity, correlationId);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger.LogWarning(
                "LEADER: Failed to renew lease (conflict). PodIdentity={PodIdentity} CorrelationId={CorrelationId}",
                PodIdentity, correlationId);
            SetLeadershipStatus(false, null, correlationId);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "LEADER: Lease not found. PodIdentity={PodIdentity} CorrelationId={CorrelationId}",
                PodIdentity, correlationId);
            SetLeadershipStatus(false, null, correlationId);
        }
    }

    private async Task<V1Lease?> GetOrCreateLeaseAsync(string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.CoordinationV1.ReadNamespacedLeaseAsync(
                _config.LeaseName, _namespace, cancellationToken: cancellationToken);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Lease doesn't exist, try to create it
            try
            {
                var newLease = new V1Lease
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = _config.LeaseName,
                        NamespaceProperty = _namespace
                    },
                    Spec = new V1LeaseSpec
                    {
                        HolderIdentity = PodIdentity,
                        LeaseDurationSeconds = _config.LeaseDurationSeconds,
                        AcquireTime = DateTime.UtcNow,
                        RenewTime = DateTime.UtcNow
                    }
                };

                var created = await _client.CoordinationV1.CreateNamespacedLeaseAsync(
                    newLease, _namespace, cancellationToken: cancellationToken);

                _logger.LogInformation(
                    "LEADER: Created lease and acquired leadership. PodIdentity={PodIdentity} CorrelationId={CorrelationId}",
                    PodIdentity, correlationId);

                SetLeadershipStatus(true, PodIdentity, correlationId);
                return created;
            }
            catch (k8s.Autorest.HttpOperationException createEx) when (createEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Another pod created it first, read it again
                _logger.LogDebug(
                    "FOLLOWER: Lost race to create lease. PodIdentity={PodIdentity} CorrelationId={CorrelationId}",
                    PodIdentity, correlationId);

                return await _client.CoordinationV1.ReadNamespacedLeaseAsync(
                    _config.LeaseName, _namespace, cancellationToken: cancellationToken);
            }
        }
    }

    private void SetLeadershipStatus(bool isLeader, string? leaderIdentity, string correlationId)
    {
        lock (_leadershipLock)
        {
            var wasLeader = _isLeader;
            _isLeader = isLeader;
            _currentLeaderIdentity = leaderIdentity;

            // Update metrics
            _metrics.SetLeaderElectionState(isLeader);

            if (wasLeader != isLeader)
            {
                if (isLeader)
                {
                    _logger.LogInformation(
                        "*** Pod elected as LEADER ***. PodIdentity={PodIdentity} CorrelationId={CorrelationId}",
                        PodIdentity, correlationId);
                }
                else
                {
                    _logger.LogInformation(
                        "*** Pod elected as FOLLOWER ***. PodIdentity={PodIdentity}, CurrentLeader={CurrentLeader} CorrelationId={CorrelationId}",
                        PodIdentity, leaderIdentity ?? "unknown", correlationId);
                }

                OnLeadershipChanged?.Invoke(this, isLeader);
            }
        }
    }
}
