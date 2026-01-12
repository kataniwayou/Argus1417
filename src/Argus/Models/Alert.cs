namespace Argus.Models;

/// <summary>
/// Represents an alert received from Prometheus/Alertmanager
/// </summary>
public class Alert
{
    /// <summary>Alert name (e.g., "KSMScrapeFailing", "ElasticsearchDown")</summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>Alert status from Alertmanager</summary>
    public string Status { get; set; } = string.Empty;
    
    /// <summary>Labels attached to the alert</summary>
    public Dictionary<string, string> Labels { get; set; } = new();
    
    /// <summary>Annotations attached to the alert</summary>
    public Dictionary<string, string> Annotations { get; set; } = new();
    
    /// <summary>When the alert started firing</summary>
    public DateTime StartsAt { get; set; }
    
    /// <summary>When the alert ended (if resolved)</summary>
    public DateTime? EndsAt { get; set; }
    
    /// <summary>Fingerprint for deduplication</summary>
    public string Fingerprint { get; set; } = string.Empty;
    
    /// <summary>Generator URL from Prometheus</summary>
    public string GeneratorUrl { get; set; } = string.Empty;

    /// <summary>
    /// Unique execution ID for tracking this specific alert instance through its lifecycle.
    /// Generated when the alert passes the platform filter.
    /// </summary>
    public string ExecutionId { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether alert is currently firing.
    /// Supports both API v1 (explicit status field) and API v2 (inferred from endsAt).
    /// For API v2, an alert is firing if endsAt is null, zero, or in the future.
    /// </summary>
    public bool IsFiring
    {
        get
        {
            // If status is explicitly set (API v1 or webhook format), use it
            if (!string.IsNullOrEmpty(Status))
            {
                return Status.Equals("firing", StringComparison.OrdinalIgnoreCase);
            }

            // API v2: Infer status from endsAt
            // Alert is firing if endsAt is not set or is in the future
            return !EndsAt.HasValue || EndsAt.Value == DateTime.MinValue || EndsAt.Value > DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Check if this alert should be processed by Argus.
    /// Only alerts with platform="argus" label are processed.
    /// </summary>
    public bool ShouldBeProcessed()
    {
        var platform = Labels.GetValueOrDefault("platform", "");
        return platform.Equals("argus", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether alert is resolved.
    /// Supports both API v1 (explicit status field) and API v2 (inferred from endsAt).
    /// </summary>
    public bool IsResolved
    {
        get
        {
            // If status is explicitly set (API v1 or webhook format), use it
            if (!string.IsNullOrEmpty(Status))
            {
                return Status.Equals("resolved", StringComparison.OrdinalIgnoreCase);
            }

            // API v2: Infer status from endsAt
            // Alert is resolved if endsAt is set and in the past
            return EndsAt.HasValue && EndsAt.Value != DateTime.MinValue && EndsAt.Value <= DateTime.UtcNow;
        }
    }
    
    /// <summary>
    /// Get label value or default
    /// </summary>
    public string GetLabel(string key, string defaultValue = "")
    {
        return Labels.TryGetValue(key, out var value) ? value : defaultValue;
    }
    
    /// <summary>
    /// Check if alert has specific label with specific value
    /// </summary>
    public bool HasLabel(string key, string value)
    {
        return Labels.TryGetValue(key, out var v) && v.Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the numeric priority value from the priority label.
    /// Returns int.MaxValue if no priority label is present or if parsing fails.
    /// Lower values indicate higher priority (0 = highest for Prometheus alerts).
    /// </summary>
    public int GetPriorityValue()
    {
        var priorityLabel = GetLabel("priority", "");
        if (string.IsNullOrEmpty(priorityLabel))
        {
            return int.MaxValue;
        }

        return int.TryParse(priorityLabel, out var priority) ? priority : int.MaxValue;
    }

    /// <summary>
    /// Convert Prometheus Alert to unified AlertDto
    /// </summary>
    public AlertDto ToAlertDto()
    {
        var summary = Annotations.GetValueOrDefault("summary", "");
        var description = Annotations.GetValueOrDefault("description", "");

        // Parse send_to_noc annotation to boolean (default: false)
        var sendToNocAnnotation = Annotations.GetValueOrDefault("send_to_noc", "false");
        var sendToNoc = sendToNocAnnotation.Equals("true", StringComparison.OrdinalIgnoreCase);

        // Parse suppress_window annotation to TimeSpan (optional)
        TimeSpan? suppressWindow = null;
        if (Annotations.TryGetValue("suppress_window", out var suppressWindowStr))
        {
            suppressWindow = ParseDuration(suppressWindowStr);
        }

        // Determine status and level
        var status = IsFiring ? AlertStatus.CREATE : AlertStatus.CANCEL;
        var level = status == AlertStatus.CREATE ? 3 : 1;

        // Create NocHttpPayload - runtime override fields are set here
        // At sending time, these will be re-applied from AlertDto to ensure consistency
        var nocPayload = new NocHttpPayload
        {
            Custom1 = "ArgusTeam",
            Custom2 = "PrometheusAlert",
            HostName = "ArgusServer",
            Level = level,
            Message = !string.IsNullOrEmpty(description) ? description : summary,
            Severity = "",
            Source = "prometheus",
            SuppressionKey = Fingerprint,
            Visible = true
        };

        return new AlertDto
        {
            Priority = GetPriorityValue(),
            Name = Name,
            Summary = summary,
            Description = description,
            Payload = nocPayload,
            SendToNoc = sendToNoc,
            SuppressWindow = suppressWindow,
            Fingerprint = Fingerprint,
            Status = status,
            Timestamp = StartsAt,
            Source = "prometheus",
            Annotations = new Dictionary<string, string>(Annotations),
            OriginalPrometheusAlert = this,
            ExecutionId = ExecutionId
        };
    }

    private static TimeSpan? ParseDuration(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();

        // Extract number and unit
        var unitIndex = value.Length - 1;
        while (unitIndex > 0 && char.IsLetter(value[unitIndex]))
            unitIndex--;

        if (unitIndex == value.Length - 1)
            return null; // No unit found

        var numberPart = value.Substring(0, unitIndex + 1);
        var unitPart = value.Substring(unitIndex + 1);

        if (!double.TryParse(numberPart, out var number))
            return null;

        return unitPart.ToLowerInvariant() switch
        {
            "s" => TimeSpan.FromSeconds(number),
            "m" => TimeSpan.FromMinutes(number),
            "h" => TimeSpan.FromHours(number),
            "d" => TimeSpan.FromDays(number),
            _ => null
        };
    }
}

/// <summary>
/// Alertmanager webhook payload
/// </summary>
public class AlertManagerPayload
{
    /// <summary>Version of the payload format</summary>
    public string Version { get; set; } = string.Empty;
    
    /// <summary>Receiver name that triggered this webhook</summary>
    public string Receiver { get; set; } = string.Empty;
    
    /// <summary>Status of the group (firing/resolved)</summary>
    public string Status { get; set; } = string.Empty;
    
    /// <summary>List of alerts in this notification</summary>
    public List<Alert> Alerts { get; set; } = new();
    
    /// <summary>Common labels shared by all alerts</summary>
    public Dictionary<string, string> CommonLabels { get; set; } = new();
    
    /// <summary>Common annotations shared by all alerts</summary>
    public Dictionary<string, string> CommonAnnotations { get; set; } = new();
    
    /// <summary>External URL of the Alertmanager</summary>
    public string ExternalUrl { get; set; } = string.Empty;
    
    /// <summary>Group key for this alert group</summary>
    public string GroupKey { get; set; } = string.Empty;
}

