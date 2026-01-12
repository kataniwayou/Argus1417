using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArgusApi;

/// <summary>
/// NOC HTTP payload structure for alert notifications.
/// This is the JSON structure sent to the NOC API.
/// </summary>
public class NocPayload
{
    /// <summary>Custom field 1 - Team name</summary>
    [JsonPropertyName("custom1")]
    public string Custom1 { get; set; } = string.Empty;

    /// <summary>Custom field 2 - System name</summary>
    [JsonPropertyName("custom2")]
    public string Custom2 { get; set; } = string.Empty;

    /// <summary>Host name - Server name</summary>
    [JsonPropertyName("hostName")]
    public string HostName { get; set; } = string.Empty;

    /// <summary>Alert level: 3=CREATE (firing), 1=CANCEL (resolved)</summary>
    [JsonPropertyName("level")]
    public int Level { get; set; }

    /// <summary>Alert message - description of the alert</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Severity - always empty string as per NOC API spec</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    /// <summary>Source of the alert</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>Suppression key - used for deduplication</summary>
    [JsonPropertyName("suppressionKey")]
    public string SuppressionKey { get; set; } = string.Empty;

    /// <summary>Visibility flag - always true as per NOC API spec</summary>
    [JsonPropertyName("visible")]
    public bool Visible { get; set; } = true;

    /// <summary>
    /// Serializes the payload to JSON string for meter tags.
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
}

/// <summary>
/// Meter-level tags for ArgusMeter (argus_api).
/// These tags are attached to argus_heartbeat and argus_exception metrics only.
/// Used for alert configuration in Argus NOC.
/// </summary>
public class ArgusMeterTags
{
    /// <summary>
    /// When true, alerts triggered by this service are sent to NOC.
    /// </summary>
    public bool SendToNoc { get; set; } = false;

    /// <summary>
    /// NOC payload structure for alert notifications.
    /// Contains custom1, custom2, hostName, level, message, severity, source, suppressionKey, visible.
    /// </summary>
    public NocPayload Payload { get; set; } = new();

    /// <summary>
    /// Duration to suppress duplicate alerts.
    /// Examples: "5m", "15m", "1h".
    /// </summary>
    public string SuppressWindow { get; set; } = "1m";

    /// <summary>
    /// Converts the tags to a dictionary for meter creation.
    /// </summary>
    public Dictionary<string, object> ToDictionary()
    {
        return new Dictionary<string, object>
        {
            ["send_to_noc"] = SendToNoc.ToString().ToLower(),
            ["payload"] = Payload.ToJson(),
            ["suppress_window"] = SuppressWindow
        };
    }
}

