using System.Text.Json;
using System.Text.Json.Serialization;

namespace Argus.Models;

/// <summary>
/// DTO for alerts sent directly by Prometheus to Alertmanager receivers.
/// This is the format Prometheus uses when sending to alerting.alertmanagers endpoints.
///
/// Developers can use this format to send alerts via POST /api/v2/alerts
///
/// Example:
/// <code>
/// {
///   "status": "firing",
///   "sendToNoc": true,
///   "suppressWindow": "15m",
///   "labels": {
///     "alertname": "ServiceDown",
///     "platform": "argus",
///     "priority": "2"
///   },
///   "annotations": {
///     "summary": "Service is down",
///     "description": "The service is not responding",
///     "payload": "component=api,type=availability,severity=high"
///   },
///   "startsAt": "2024-01-15T10:30:00Z"
/// }
/// </code>
/// </summary>
public class PrometheusAlertDto
{
    /// <summary>Alert status: "firing" or "resolved"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Whether this alert should be sent to NOC.
    /// When sending alerts via API, set this to true to send alerts to NOC.
    /// This is converted to/from the "send_to_noc" annotation for Prometheus compatibility.
    /// Default is false.
    /// </summary>
    [JsonPropertyName("sendToNoc")]
    public bool SendToNoc { get; set; }

    /// <summary>
    /// How long to suppress duplicate notifications for this alert.
    /// When sending alerts via API, use standard TimeSpan format or duration strings.
    /// Examples: "00:15:00", "15m", "2h", "1d"
    /// This is converted to/from the "suppress_window" annotation for Prometheus compatibility.
    /// If not specified, no suppression is applied.
    /// </summary>
    [JsonPropertyName("suppressWindow")]
    [JsonConverter(typeof(TimeSpanConverter))]
    public TimeSpan? SuppressWindow { get; set; }

    /// <summary>
    /// Labels attached to the alert (includes alertname).
    /// Required labels:
    /// - alertname: Name of the alert
    /// - priority: Numeric priority (0 = highest for Prometheus alerts)
    /// Optional labels:
    /// - payload: Additional context (can also be in annotations)
    /// - argus: Can be set as label "true"/"false" (deprecated - use Argus property instead)
    /// </summary>
    [JsonPropertyName("labels")]
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>
    /// Annotations attached to the alert.
    /// Supported annotations:
    /// - summary: Short summary of the alert
    /// - description: Detailed description
    /// - payload: Additional context (e.g., "component=api,type=availability,severity=high")
    /// - send_to_noc: Whether this alert should be sent to NOC ("true"/"false")
    ///   Can be set via annotation when inheriting from metric labels using templates
    /// - suppress_window: How long to suppress duplicate notifications
    ///   Supported formats (unit suffix REQUIRED): "120s", "5m", "2h", "1d"
    ///   Plain numbers without units (e.g., "120") are NOT supported
    ///   If not specified, no suppression is applied
    /// </summary>
    [JsonPropertyName("annotations")]
    public Dictionary<string, string> Annotations { get; set; } = new();

    /// <summary>When the alert started firing (ISO 8601)</summary>
    [JsonPropertyName("startsAt")]
    public DateTime StartsAt { get; set; }

    /// <summary>When the alert will end (for resolved alerts)</summary>
    [JsonPropertyName("endsAt")]
    public DateTime? EndsAt { get; set; }

    /// <summary>URL to the generator (Prometheus)</summary>
    [JsonPropertyName("generatorURL")]
    public string GeneratorUrl { get; set; } = string.Empty;

    /// <summary>
    /// Convert to domain Alert model
    /// </summary>
    public Alert ToAlert()
    {
        var labels = new Dictionary<string, string>(Labels);
        var annotations = new Dictionary<string, string>(Annotations);

        // If SendToNoc property is explicitly true, override the annotation
        // Otherwise, preserve any existing send_to_noc annotation from Prometheus
        if (SendToNoc)
        {
            annotations["send_to_noc"] = "true";
        }
        // If annotation doesn't exist, don't add it (let Alert.ToAlertDto use default false)

        // If SuppressWindow property is set, add/override the suppress_window annotation
        // Priority: SuppressWindow property > suppress_window annotation
        // Only add if value is specified (optional)
        if (SuppressWindow.HasValue)
        {
            annotations["suppress_window"] = FormatTimeSpan(SuppressWindow.Value);
        }

        return new Alert
        {
            Name = Labels.GetValueOrDefault("alertname", "Unknown"),
            Status = Status,
            Labels = labels,
            Annotations = annotations,
            StartsAt = StartsAt,
            EndsAt = EndsAt,
            GeneratorUrl = GeneratorUrl,
            // Generate fingerprint from labels if not provided
            Fingerprint = GenerateFingerprint()
        };
    }

    private static string FormatTimeSpan(TimeSpan timeSpan)
    {
        // Convert TimeSpan to Prometheus duration format (e.g., "15m", "2h", "1d")
        if (timeSpan.TotalDays >= 1 && timeSpan.TotalDays % 1 == 0)
            return $"{(int)timeSpan.TotalDays}d";
        if (timeSpan.TotalHours >= 1 && timeSpan.TotalHours % 1 == 0)
            return $"{(int)timeSpan.TotalHours}h";
        if (timeSpan.TotalMinutes >= 1 && timeSpan.TotalMinutes % 1 == 0)
            return $"{(int)timeSpan.TotalMinutes}m";
        return $"{(int)timeSpan.TotalSeconds}s";
    }

    private string GenerateFingerprint()
    {
        // Simple fingerprint: hash of sorted labels
        var sortedLabels = Labels.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={kv.Value}");
        return string.Join(",", sortedLabels).GetHashCode().ToString("X8");
    }
}

/// <summary>
/// JSON converter for TimeSpan that supports both standard TimeSpan format and Prometheus duration strings.
/// Supports: "15m", "2h", "1d", "120s", or standard TimeSpan format "00:15:00"
/// </summary>
public class TimeSpanConverter : JsonConverter<TimeSpan?>
{
    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
                return null;

            // Try parsing as Prometheus duration format first (e.g., "15m", "2h", "1d")
            if (TryParseDuration(value, out var duration))
                return duration;

            // Fall back to standard TimeSpan parsing
            if (TimeSpan.TryParse(value, out var timeSpan))
                return timeSpan;

            throw new JsonException($"Invalid TimeSpan format: {value}. Use formats like '15m', '2h', '1d', or '00:15:00'");
        }

        throw new JsonException($"Unexpected token type for TimeSpan: {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (!value.HasValue)
        {
            writer.WriteNullValue();
            return;
        }

        // Write in Prometheus duration format
        var timeSpan = value.Value;
        string formatted;

        if (timeSpan.TotalDays >= 1 && timeSpan.TotalDays % 1 == 0)
            formatted = $"{(int)timeSpan.TotalDays}d";
        else if (timeSpan.TotalHours >= 1 && timeSpan.TotalHours % 1 == 0)
            formatted = $"{(int)timeSpan.TotalHours}h";
        else if (timeSpan.TotalMinutes >= 1 && timeSpan.TotalMinutes % 1 == 0)
            formatted = $"{(int)timeSpan.TotalMinutes}m";
        else
            formatted = $"{(int)timeSpan.TotalSeconds}s";

        writer.WriteStringValue(formatted);
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();

        // Extract number and unit
        var unitIndex = value.Length - 1;
        while (unitIndex > 0 && char.IsLetter(value[unitIndex]))
            unitIndex--;

        if (unitIndex == value.Length - 1)
            return false; // No unit found

        var numberPart = value.Substring(0, unitIndex + 1);
        var unitPart = value.Substring(unitIndex + 1);

        if (!double.TryParse(numberPart, out var number))
            return false;

        duration = unitPart.ToLowerInvariant() switch
        {
            "s" => TimeSpan.FromSeconds(number),
            "m" => TimeSpan.FromMinutes(number),
            "h" => TimeSpan.FromHours(number),
            "d" => TimeSpan.FromDays(number),
            _ => TimeSpan.Zero
        };

        return duration != TimeSpan.Zero;
    }
}
