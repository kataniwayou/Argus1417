using System.Text.Json.Serialization;

namespace Argus.Models;

/// <summary>
/// Filter document for NOC verification HTTP POST requests (second phase).
/// This is the JSON structure sent to query/filter committed alerts in NOC.
/// Built from the sent NocHttpPayload properties.
/// </summary>
public class NocFilterDoc
{
    [JsonPropertyName("custom1")]
    public string Custom1 { get; set; } = string.Empty;

    [JsonPropertyName("custom2")]
    public string Custom2 { get; set; } = string.Empty;

    [JsonPropertyName("hostName")]
    public string HostName { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("suppressionKey")]
    public string SuppressionKey { get; set; } = string.Empty;

    [JsonPropertyName("visible")]
    public bool Visible { get; set; } = true;

    [JsonPropertyName("userTga1")]
    public string UserTga1 { get; set; } = string.Empty;

    [JsonPropertyName("userTga2")]
    public string UserTga2 { get; set; } = string.Empty;

    [JsonPropertyName("userTga3")]
    public string UserTga3 { get; set; } = string.Empty;

    /// <summary>
    /// Creates a NocFilterDoc from a sent NocHttpPayload.
    /// Copies all properties from the payload and sets userTga fields to empty.
    /// </summary>
    public static NocFilterDoc FromPayload(NocHttpPayload payload)
    {
        return new NocFilterDoc
        {
            Custom1 = payload.Custom1,
            Custom2 = payload.Custom2,
            HostName = payload.HostName,
            Level = payload.Level,
            Message = payload.Message,
            Severity = payload.Severity,
            Source = payload.Source,
            SuppressionKey = payload.SuppressionKey,
            Visible = payload.Visible,
            UserTga1 = string.Empty,
            UserTga2 = string.Empty,
            UserTga3 = string.Empty
        };
    }
}

