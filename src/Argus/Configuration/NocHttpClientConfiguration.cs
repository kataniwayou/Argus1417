namespace Argus.Configuration;

/// <summary>
/// Configuration for NOC HTTP client endpoints and connection settings.
/// Supports HTTPS with optional SSL certificate bypass and IP address override.
/// </summary>
public class NocHttpClientConfiguration
{
    /// <summary>
    /// URL for sending CREATE/CANCEL alerts (HTTP POST)
    /// </summary>
    public string SendEndpoint { get; set; } = "https://noc.example.com/api/alerts";

    /// <summary>
    /// URL for verification - retrieves committed alert from NOC for comparison (HTTP POST)
    /// </summary>
    public string VerifyEndpoint { get; set; } = "https://noc.example.com/api/alerts/verify";

    /// <summary>
    /// Basic authentication username (optional)
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Basic authentication password (optional)
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// HTTP request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Override IP address for connection (bypasses DNS resolution).
    /// Useful for port-forwarding scenarios with IPv4/IPv6 localhost issues.
    /// Leave empty to use normal DNS resolution.
    /// </summary>
    public string? ConnectIpAddress { get; set; }

    /// <summary>
    /// Port to use when ConnectIpAddress is specified
    /// </summary>
    public int ConnectPort { get; set; } = 443;

    /// <summary>
    /// Team name for NOC payload custom1 field
    /// </summary>
    public string TeamName { get; set; } = "ArgusTeam";

    /// <summary>
    /// System name for NOC payload custom2 field
    /// </summary>
    public string SystemName { get; set; } = "ArgusSystem";

    /// <summary>
    /// Host/Server name for NOC payload hostName field
    /// </summary>
    public string HostName { get; set; } = "ArgusServer";

    /// <summary>
    /// Whether to bypass SSL certificate validation.
    /// Should only be true in development/testing environments.
    /// </summary>
    public bool BypassSslValidation { get; set; } = true;
}

