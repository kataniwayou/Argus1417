namespace Argus.Models;

/// <summary>
/// Alert status for the alerts vector
/// </summary>
public enum AlertStatus
{
    /// <summary>Alert is active/firing - should be sent to NOC</summary>
    CREATE,

    /// <summary>Alert is resolved/cancelled - should be sent to NOC then removed from vector</summary>
    CANCEL
}

/// <summary>
/// Unified alert DTO for all alert sources (Prometheus alerts, K8s layer, Watchdog).
/// This is the standard format used in the alerts vector.
/// </summary>
public class AlertDto
{
    /// <summary>
    /// Priority of the alert. Lower values = higher priority.
    /// -3 = Prometheus pod down (highest)
    /// -2 = KSM pod down
    /// -1 = Watchdog expired
    /// 0+ = Prometheus alerts (from priority label)
    /// </summary>
    public int Priority { get; set; }
    
    /// <summary>
    /// Alert name (e.g., "PrometheusDown", "KSMDown", "WatchdogExpired", "ElasticsearchDown")
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Short summary of the alert
    /// </summary>
    public string Summary { get; set; } = string.Empty;
    
    /// <summary>
    /// Detailed description of the alert
    /// </summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// NOC payload for HTTP POST requests to NOC API.
    /// At runtime, level/message/source/suppressionKey are always overridden from this AlertDto.
    /// </summary>
    public NocHttpPayload Payload { get; set; } = new();

    /// <summary>
    /// Whether this alert should be sent to NOC.
    /// Derived from the "send_to_noc" annotation in Prometheus alerts.
    /// Default is false.
    /// </summary>
    public bool SendToNoc { get; set; }

    /// <summary>
    /// How long to suppress duplicate notifications for this alert.
    /// Derived from the "suppress_window" annotation in Prometheus alerts.
    /// If not specified, uses default from configuration.
    /// </summary>
    public TimeSpan? SuppressWindow { get; set; }

    /// <summary>
    /// Unique fingerprint for this alert (used for deduplication and tracking)
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Alert status (CREATE or CANCEL)
    /// - CREATE: Alert is active/firing - should be sent to NOC
    /// - CANCEL: Alert is resolved/cancelled - should be sent to NOC then removed from vector
    /// </summary>
    public AlertStatus Status { get; set; }

    /// <summary>
    /// When this alert was created/updated (for logging only)
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this alert was last seen/refreshed (for logging only).
    /// Updated each time the alert is received from any source.
    /// </summary>
    public DateTime LastSeenTimestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tick count when this alert was last seen/refreshed.
    /// Used for TTL-based expiry of stale CREATE alerts.
    /// Updated each time the alert is received from any source.
    /// </summary>
    public long LastSeenTick { get; set; }

    /// <summary>
    /// Source of the alert (for debugging)
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Annotations from the alert.
    /// Common annotations:
    /// - summary: Short summary of the alert
    /// - description: Detailed description
    /// - suppress_window: Suppression window duration
    ///   Controls how long to wait before sending duplicate notifications
    ///   Supported formats (unit suffix REQUIRED): "120s", "5m", "2h", "1d"
    ///   Plain numbers without units are NOT supported
    /// </summary>
    public Dictionary<string, string> Annotations { get; set; } = new();

    /// <summary>
    /// Original Prometheus alert (if applicable)
    /// </summary>
    public Alert? OriginalPrometheusAlert { get; set; }

    /// <summary>
    /// Unique execution ID for tracking this specific alert instance through its lifecycle.
    /// Generated when:
    /// - Prometheus alert passes platform filter (exec-{guid})
    /// - K8s polling cycle starts (exec-{guid} for all alerts in that cycle)
    /// - Watchdog expires (exec-{guid})
    /// This ID stays with the alert through: creation → vector → snapshot → enqueue → NOC → suppression/cancellation
    /// </summary>
    public string ExecutionId { get; set; } = string.Empty;
}

