using Argus.Models;

namespace Argus.Configuration;

/// <summary>
/// Default NOC behavior configuration for Prometheus alerts when suppress_window annotation is missing or invalid.
/// Provides separate behavior for CREATE (firing) and CANCEL (resolved) alerts.
/// Supports time duration formats: s (seconds), m (minutes), h (hours), d (days)
/// Examples: "120s", "4m", "8h", "3d"
/// </summary>
public class DefaultNocConfiguration
{
    /// <summary>NOC behavior for CREATE alerts (firing)</summary>
    public NocBehaviorConfiguration CreateNocBehavior { get; set; } = new()
    {
        SendToNoc = true,
        Payload = new NocHttpPayload
        {
            Custom1 = "ArgusTeam",
            Custom2 = "PrometheusAlert",
            HostName = "ArgusServer",
            Level = 3,
            Message = "Alert firing",
            Severity = "",
            Source = "prometheus",
            SuppressionKey = "",
            Visible = true
        },
        SuppressWindow = "2m"
    };

    /// <summary>NOC behavior for CANCEL alerts (resolved)</summary>
    public NocBehaviorConfiguration CancelNocBehavior { get; set; } = new()
    {
        SendToNoc = true,
        Payload = new NocHttpPayload
        {
            Custom1 = "ArgusTeam",
            Custom2 = "PrometheusAlert",
            HostName = "ArgusServer",
            Level = 0,
            Message = "Alert resolved",
            Severity = "",
            Source = "prometheus",
            SuppressionKey = "",
            Visible = true
        },
        SuppressWindow = "5m"
    };
}

