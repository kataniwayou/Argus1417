using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Argus.Models;

namespace Argus.Services.Metrics;

/// <summary>
/// OpenTelemetry-based implementation of Argus system metrics.
/// Uses System.Diagnostics.Metrics for proper OTel integration.
/// All metrics are exported via OTel Collector with telemetry_source="argus_service".
/// </summary>
public class ArgusMetrics : IArgusMetrics
{
    /// <summary>
    /// The meter name used for Argus service metrics.
    /// Must match the name registered with AddMeter() in Program.cs.
    /// </summary>
    public const string MeterName = "Argus.Service";

    private readonly Meter _meter;

    // Ingestion metrics
    private readonly Counter<long> _alertsReceivedCounter;
    private readonly Counter<long> _alertsFilteredCounter;
    private readonly ConcurrentDictionary<string, long> _alertsReceivedBySource = new();

    // Lifecycle metrics
    private readonly Counter<long> _alertsCreatedCounter;
    private readonly Counter<long> _alertsResolvedCounter;

    // Vector state gauges (ObservableGauge callbacks)
    private int _alertsVectorSize;
    private readonly ConcurrentDictionary<AlertStatus, int> _alertsVectorByStatus = new();

    // NOC metrics
    private readonly Counter<long> _nocDecisionsCounter;
    private readonly ConcurrentDictionary<NocDecisionType, long> _nocDecisionsByType = new();
    private readonly Counter<long> _nocSentCounter;
    private readonly Counter<long> _nocSuppressedCounter;
    private int _nocQueueDepth;

    // Health metrics
    private int _gracePeriodActive = 1; // Start as active
    private readonly Histogram<double> _k8sPollDurationHistogram;
    private readonly Histogram<double> _snapshotDurationHistogram;

    // K8s Layer component status
    private int _k8sApiAvailable = 1; // Start as available
    private int _prometheusPodHealthy = 1; // Start as healthy
    private int _ksmPodHealthy = 1; // Start as healthy

    // Leader election status
    private int _isLeader; // Start as follower (0)

    // Central Timer metrics
    private long _centralTimerTickCount;
    private readonly Histogram<double> _callbackDurationHistogram;
    private readonly Counter<long> _callbackErrorCounter;
    private readonly Counter<long> _callbackSkippedCounter;
    private int _externalMonitorOk = 1; // Start as OK
    private double _externalMonitorHeartbeatAge;

    // Liveness Vector metrics
    private int _livenessVectorHealthy = 1; // Start as healthy
    private int _livenessVectorSize;
    private int _livenessUnhealthyCallbackCount;

    // For snapshot reporting (backward compatibility)
    private long _totalAlertsReceived;
    private long _totalAlertsFiltered;
    private long _totalAlertsCreated;
    private long _totalAlertsResolved;
    private long _totalNocDecisions;
    private long _totalNocSent;
    private long _totalNocSuppressed;
    private TimeSpan _lastK8sPollDuration;
    private TimeSpan _lastSnapshotDuration;
    private readonly object _durationLock = new();

    public ArgusMetrics()
    {
        // Create meter for Argus service metrics
        // The meter name must match what's registered with AddMeter() in Program.cs
        _meter = new Meter(MeterName, "1.0.0");

        // Ingestion metrics
        _alertsReceivedCounter = _meter.CreateCounter<long>(
            "argus_alerts_received",
            unit: "count",
            description: "Total alerts received from all sources");

        _alertsFilteredCounter = _meter.CreateCounter<long>(
            "argus_alerts_filtered",
            unit: "count",
            description: "Alerts filtered (no platform=argus label)");

        // Lifecycle metrics
        _alertsCreatedCounter = _meter.CreateCounter<long>(
            "argus_alerts_created",
            unit: "count",
            description: "Alerts that entered CREATE status");

        _alertsResolvedCounter = _meter.CreateCounter<long>(
            "argus_alerts_resolved",
            unit: "count",
            description: "Alerts resolved (CANCEL processed)");

        // Vector state gauges (ObservableGauge)
        _meter.CreateObservableGauge(
            "argus_alerts_vector_size",
            () => _alertsVectorSize,
            unit: "count",
            description: "Current alerts vector size");

        _meter.CreateObservableGauge(
            "argus_alerts_vector_by_status",
            () => _alertsVectorByStatus.Select(kvp => new Measurement<int>(
                kvp.Value,
                new KeyValuePair<string, object?>("status", kvp.Key.ToString()))),
            unit: "count",
            description: "Alerts by status");

        // NOC metrics
        _nocDecisionsCounter = _meter.CreateCounter<long>(
            "argus_noc_decisions",
            unit: "count",
            description: "NOC decisions enqueued");

        _nocSentCounter = _meter.CreateCounter<long>(
            "argus_noc_sent",
            unit: "count",
            description: "Alerts sent to NOC");

        _nocSuppressedCounter = _meter.CreateCounter<long>(
            "argus_noc_suppressed",
            unit: "count",
            description: "Alerts suppressed by cache");

        _meter.CreateObservableGauge(
            "argus_noc_queue_depth",
            () => _nocQueueDepth,
            unit: "count",
            description: "Current NOC queue depth");

        // Health metrics
        _k8sPollDurationHistogram = _meter.CreateHistogram<double>(
            "argus_k8s_poll_duration",
            unit: "s",
            description: "K8s polling duration");

        _snapshotDurationHistogram = _meter.CreateHistogram<double>(
            "argus_snapshot_duration",
            unit: "s",
            description: "Snapshot processing duration");

        _meter.CreateObservableGauge(
            "argus_grace_period_active",
            () => _gracePeriodActive,
            unit: "state",
            description: "Grace period active (1=active, 0=expired)");

        // K8s Layer component status metrics
        _meter.CreateObservableGauge(
            "argus_k8s_api_available",
            () => _k8sApiAvailable,
            unit: "state",
            description: "K8s API server availability (1=available, 0=unavailable)");

        _meter.CreateObservableGauge(
            "argus_prometheus_pod_healthy",
            () => _prometheusPodHealthy,
            unit: "state",
            description: "Prometheus pod health status (1=healthy, 0=down/unstable)");

        _meter.CreateObservableGauge(
            "argus_ksm_pod_healthy",
            () => _ksmPodHealthy,
            unit: "state",
            description: "KSM pod health status (1=healthy, 0=down/unstable)");

        _meter.CreateObservableGauge(
            "argus_leader_election",
            () => _isLeader,
            unit: "state",
            description: "Leader election status (1=leader, 0=follower)");

        // Central Timer metrics
        _meter.CreateObservableGauge(
            "argus_central_timer_tick_count",
            () => Interlocked.Read(ref _centralTimerTickCount),
            unit: "count",
            description: "Central timer tick count (system heartbeat)");

        _callbackDurationHistogram = _meter.CreateHistogram<double>(
            "argus_callback_duration",
            unit: "s",
            description: "Callback execution duration");

        _callbackErrorCounter = _meter.CreateCounter<long>(
            "argus_callback_errors",
            unit: "count",
            description: "Callback execution errors");

        _callbackSkippedCounter = _meter.CreateCounter<long>(
            "argus_callback_skipped",
            unit: "count",
            description: "Callback executions skipped (previous still running)");

        _meter.CreateObservableGauge(
            "argus_external_monitor_status",
            () => _externalMonitorOk,
            unit: "state",
            description: "External monitor status (1=OK, 0=expired)");

        _meter.CreateObservableGauge(
            "argus_external_monitor_heartbeat_age",
            () => _externalMonitorHeartbeatAge,
            unit: "s",
            description: "External monitor heartbeat age in seconds");

        // Liveness Vector metrics
        _meter.CreateObservableGauge(
            "argus_liveness_vector_healthy",
            () => _livenessVectorHealthy,
            unit: "state",
            description: "Liveness vector healthy status (1=all healthy, 0=unhealthy)");

        _meter.CreateObservableGauge(
            "argus_liveness_vector_size",
            () => _livenessVectorSize,
            unit: "count",
            description: "Number of callbacks tracked in liveness vector");

        _meter.CreateObservableGauge(
            "argus_liveness_unhealthy_callbacks",
            () => _livenessUnhealthyCallbackCount,
            unit: "count",
            description: "Current count of unhealthy callbacks");
    }

    #region Ingestion Metrics

    public void IncrementAlertsReceived(string source)
    {
        _alertsReceivedCounter.Add(1, new KeyValuePair<string, object?>("source", source));
        _alertsReceivedBySource.AddOrUpdate(source, 1, (_, count) => count + 1);
        Interlocked.Increment(ref _totalAlertsReceived);
    }

    public void IncrementAlertsFiltered()
    {
        _alertsFilteredCounter.Add(1);
        Interlocked.Increment(ref _totalAlertsFiltered);
    }

    #endregion

    #region Alert Lifecycle Metrics

    public void IncrementAlertsCreated()
    {
        _alertsCreatedCounter.Add(1);
        Interlocked.Increment(ref _totalAlertsCreated);
    }

    public void IncrementAlertsResolved()
    {
        _alertsResolvedCounter.Add(1);
        Interlocked.Increment(ref _totalAlertsResolved);
    }

    #endregion

    #region Alert Vector State

    public void SetAlertsVectorSize(int size)
    {
        Interlocked.Exchange(ref _alertsVectorSize, size);
    }

    public void SetAlertsVectorByStatus(AlertStatus status, int count)
    {
        _alertsVectorByStatus[status] = count;
    }

    #endregion

    #region NOC Decision Metrics

    public void IncrementNocDecisions(NocDecisionType type)
    {
        _nocDecisionsCounter.Add(1, new KeyValuePair<string, object?>("type", type.ToString()));
        _nocDecisionsByType.AddOrUpdate(type, 1, (_, count) => count + 1);
        Interlocked.Increment(ref _totalNocDecisions);
    }

    public void IncrementNocSent()
    {
        _nocSentCounter.Add(1);
        Interlocked.Increment(ref _totalNocSent);
    }

    public void IncrementNocSuppressed()
    {
        _nocSuppressedCounter.Add(1);
        Interlocked.Increment(ref _totalNocSuppressed);
    }

    public void SetNocQueueDepth(int depth)
    {
        Interlocked.Exchange(ref _nocQueueDepth, depth);
    }

    #endregion

    #region Infrastructure Health Metrics

    public void RecordK8sPollDuration(TimeSpan duration)
    {
        _k8sPollDurationHistogram.Record(duration.TotalSeconds);
        lock (_durationLock)
        {
            _lastK8sPollDuration = duration;
        }
    }

    public void RecordSnapshotDuration(TimeSpan duration)
    {
        _snapshotDurationHistogram.Record(duration.TotalSeconds);
        lock (_durationLock)
        {
            _lastSnapshotDuration = duration;
        }
    }

    public void SetGracePeriodActive(bool active)
    {
        Interlocked.Exchange(ref _gracePeriodActive, active ? 1 : 0);
    }

    public void SetK8sApiAvailable(bool available)
    {
        Interlocked.Exchange(ref _k8sApiAvailable, available ? 1 : 0);
    }

    public void SetPrometheusPodHealthy(bool healthy)
    {
        Interlocked.Exchange(ref _prometheusPodHealthy, healthy ? 1 : 0);
    }

    public void SetKsmPodHealthy(bool healthy)
    {
        Interlocked.Exchange(ref _ksmPodHealthy, healthy ? 1 : 0);
    }

    public void SetLeaderElectionState(bool isLeader)
    {
        Interlocked.Exchange(ref _isLeader, isLeader ? 1 : 0);
    }

    #endregion

    #region Central Timer Metrics

    public void SetCentralTimerTickCount(long tickCount)
    {
        Interlocked.Exchange(ref _centralTimerTickCount, tickCount);
    }

    public void RecordCallbackDuration(string callbackName, TimeSpan duration)
    {
        _callbackDurationHistogram.Record(
            duration.TotalSeconds,
            new KeyValuePair<string, object?>("callback", callbackName));
    }

    public void IncrementCallbackError(string callbackName)
    {
        _callbackErrorCounter.Add(1, new KeyValuePair<string, object?>("callback", callbackName));
    }

    public void IncrementCallbackSkipped(string callbackName)
    {
        _callbackSkippedCounter.Add(1, new KeyValuePair<string, object?>("callback", callbackName));
    }

    public void SetExternalMonitorStatus(bool isOk)
    {
        Interlocked.Exchange(ref _externalMonitorOk, isOk ? 1 : 0);
    }

    public void SetExternalMonitorHeartbeatAge(double ageSeconds)
    {
        Interlocked.Exchange(ref _externalMonitorHeartbeatAge, ageSeconds);
    }

    #endregion

    #region Liveness Vector Metrics

    public void SetLivenessVectorHealthy(bool isHealthy)
    {
        Interlocked.Exchange(ref _livenessVectorHealthy, isHealthy ? 1 : 0);
    }

    public void SetLivenessVectorSize(int size)
    {
        Interlocked.Exchange(ref _livenessVectorSize, size);
    }

    public void SetLivenessUnhealthyCallbackCount(int count)
    {
        Interlocked.Exchange(ref _livenessUnhealthyCallbackCount, count);
    }

    #endregion

    #region Snapshot Accessors

    public ArgusMetricsSnapshot GetSnapshot()
    {
        lock (_durationLock)
        {
            return new ArgusMetricsSnapshot
            {
                // Ingestion
                TotalAlertsReceived = Interlocked.Read(ref _totalAlertsReceived),
                TotalAlertsFiltered = Interlocked.Read(ref _totalAlertsFiltered),
                AlertsReceivedBySource = new Dictionary<string, long>(_alertsReceivedBySource),

                // Lifecycle
                TotalAlertsCreated = Interlocked.Read(ref _totalAlertsCreated),
                TotalAlertsResolved = Interlocked.Read(ref _totalAlertsResolved),

                // Vector State
                AlertsVectorSize = _alertsVectorSize,
                AlertsVectorByStatus = new Dictionary<AlertStatus, int>(_alertsVectorByStatus),

                // NOC
                TotalNocDecisions = Interlocked.Read(ref _totalNocDecisions),
                NocDecisionsByType = new Dictionary<NocDecisionType, long>(_nocDecisionsByType),
                TotalNocSent = Interlocked.Read(ref _totalNocSent),
                TotalNocSuppressed = Interlocked.Read(ref _totalNocSuppressed),
                NocQueueDepth = _nocQueueDepth,

                // Health
                GracePeriodActive = _gracePeriodActive == 1,
                LastK8sPollDuration = _lastK8sPollDuration,
                LastSnapshotDuration = _lastSnapshotDuration,

                // K8s Layer Component Status
                K8sApiAvailable = _k8sApiAvailable == 1,
                PrometheusPodHealthy = _prometheusPodHealthy == 1,
                KsmPodHealthy = _ksmPodHealthy == 1,

                // Leader Election
                IsLeader = _isLeader == 1,

                // Central Timer
                CentralTimerTickCount = Interlocked.Read(ref _centralTimerTickCount),
                ExternalMonitorOk = _externalMonitorOk == 1,
                ExternalMonitorHeartbeatAge = _externalMonitorHeartbeatAge,

                // Liveness Vector
                LivenessVectorHealthy = _livenessVectorHealthy == 1,
                LivenessVectorSize = _livenessVectorSize,
                LivenessUnhealthyCallbackCount = _livenessUnhealthyCallbackCount
            };
        }
    }

    public string GetPrometheusMetrics()
    {
        // This method is deprecated - metrics are now exported via OpenTelemetry
        // Kept for backward compatibility with /metrics endpoint
        return "# Metrics are now exported via OpenTelemetry to the OTel Collector\n" +
               "# Please scrape metrics from Prometheus instead of this endpoint\n";
    }

    #endregion
}

