namespace Argus.Models;

/// <summary>
/// Type of NOC decision to process
/// </summary>
public enum NocDecisionType
{
    /// <summary>
    /// Handle a single CREATE alert (highest priority active alert)
    /// </summary>
    HandleCreate,

    /// <summary>
    /// Handle a batch of CANCEL alerts (all resolved alerts)
    /// </summary>
    HandleCancels
}

/// <summary>
/// Represents a NOC decision to be processed asynchronously.
/// Contains either a single CREATE alert or a batch of CANCEL alerts.
/// </summary>
public class NocDecision
{
    /// <summary>
    /// Type of decision (HandleCreate or HandleCancels)
    /// </summary>
    public NocDecisionType Type { get; set; }
    
    /// <summary>
    /// Single alert for HandleCreate type
    /// </summary>
    public AlertDto? Alert { get; set; }
    
    /// <summary>
    /// Multiple alerts for HandleCancels type
    /// </summary>
    public List<AlertDto>? Alerts { get; set; }
    
    /// <summary>
    /// When this snapshot was taken
    /// </summary>
    public DateTime SnapshotTime { get; set; }
    
    /// <summary>
    /// Correlation ID for tracing
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;
}

