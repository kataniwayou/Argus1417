using System.Text.Json.Serialization;

namespace Argus.Models;

/// <summary>
/// Payload for NOC HTTP POST requests.
/// This is the JSON structure sent to the NOC API.
/// 
/// Runtime Override Rules:
/// - level: Always overridden (3=CREATE, 0=CANCEL)
/// - message: Always overridden from AlertDto.Description
/// - source: Always overridden from AlertDto.Source
/// - suppressionKey: Always overridden from AlertDto.Fingerprint
/// </summary>
public class NocHttpPayload
{
    /// <summary>
    /// Custom field 1 - Team name
    /// </summary>
    [JsonPropertyName("custom1")]
    public string Custom1 { get; set; } = string.Empty;

    /// <summary>
    /// Custom field 2 - System name
    /// </summary>
    [JsonPropertyName("custom2")]
    public string Custom2 { get; set; } = string.Empty;

    /// <summary>
    /// Host name - Server name
    /// </summary>
    [JsonPropertyName("hostName")]
    public string HostName { get; set; } = string.Empty;

    /// <summary>
    /// Alert level: 3=CREATE (firing), 0=CANCEL (resolved)
    /// Always overridden at runtime based on AlertDto.Status
    /// </summary>
    [JsonPropertyName("level")]
    public int Level { get; set; }

    /// <summary>
    /// Alert message - description of the alert.
    /// Always overridden at runtime from AlertDto.Description
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Severity - always empty string as per NOC API spec
    /// </summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    /// <summary>
    /// Source of the alert (e.g., "K8sLayer", "prometheus", "StatusFileSystem")
    /// Always overridden at runtime from AlertDto.Source
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Suppression key - used for deduplication.
    /// Always overridden at runtime from AlertDto.Fingerprint
    /// </summary>
    [JsonPropertyName("suppressionKey")]
    public string SuppressionKey { get; set; } = string.Empty;

    /// <summary>
    /// Visibility flag - always true as per NOC API spec
    /// </summary>
    [JsonPropertyName("visible")]
    public bool Visible { get; set; } = true;

    /// <summary>
    /// Creates a deep copy of this payload
    /// </summary>
    public NocHttpPayload Clone()
    {
        return new NocHttpPayload
        {
            Custom1 = Custom1,
            Custom2 = Custom2,
            HostName = HostName,
            Level = Level,
            Message = Message,
            Severity = Severity,
            Source = Source,
            SuppressionKey = SuppressionKey,
            Visible = Visible
        };
    }

    /// <summary>
    /// Apply runtime overrides from an AlertDto.
    /// This ensures level, message, source, and suppressionKey are always derived from the alert.
    /// </summary>
    public void ApplyAlertOverrides(AlertDto alert)
    {
        // Level: 3=CREATE, 0=CANCEL
        Level = alert.Status == AlertStatus.CREATE ? 3 : 0;

        // Message: Use Description, or Summary if Description is empty
        Message = !string.IsNullOrEmpty(alert.Description)
            ? alert.Description
            : alert.Summary;

        // Source: From alert source
        Source = alert.Source;

        // SuppressionKey: From alert fingerprint
        SuppressionKey = alert.Fingerprint;
    }

    /// <summary>
    /// Creates a payload from an AlertDto with all overrides applied
    /// </summary>
    public static NocHttpPayload FromAlert(AlertDto alert, NocHttpPayload? template = null)
    {
        var payload = template?.Clone() ?? new NocHttpPayload();
        payload.ApplyAlertOverrides(alert);
        return payload;
    }
}

