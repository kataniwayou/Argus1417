using Argus.Models;

namespace Argus.Services.Noc;

/// <summary>
/// Result of sending an alert to NOC
/// </summary>
public class NocHttpResult
{
    /// <summary>HTTP status code from NOC API</summary>
    public int StatusCode { get; set; }

    /// <summary>Whether the request was successful (200 or 204 only)</summary>
    public bool IsSuccess => StatusCode == 200 || StatusCode == 204;

    /// <summary>Error message if request failed</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The payload that was sent to NOC</summary>
    public NocHttpPayload? SentPayload { get; set; }

    /// <summary>Response body from NOC (if any)</summary>
    public string? ResponseBody { get; set; }
}

/// <summary>
/// Result of verifying an alert was committed by NOC
/// </summary>
public class NocVerifyResult
{
    /// <summary>HTTP status code from NOC verify API</summary>
    public int StatusCode { get; set; }

    /// <summary>Whether the request was successful (200 or 204 only)</summary>
    public bool IsSuccess => StatusCode == 200 || StatusCode == 204;

    /// <summary>Whether the payload comparison succeeded</summary>
    public bool ComparisonSuccess { get; set; }

    /// <summary>Error message if request failed</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The payload received from NOC (for comparison)</summary>
    public NocHttpPayload? ReceivedPayload { get; set; }
}

/// <summary>
/// HTTP client for communicating with NOC API.
/// Handles sending alerts and verifying they were committed.
/// </summary>
public interface INocHttpClient
{
    /// <summary>
    /// Send alert to NOC (CREATE or CANCEL).
    /// The payload's level, message, source, and suppressionKey are always
    /// overridden from the AlertDto before sending.
    /// </summary>
    /// <param name="alert">Alert to send</param>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Result with status code and sent payload</returns>
    Task<NocHttpResult> SendAlertAsync(AlertDto alert, string correlationId, CancellationToken ct = default);

    /// <summary>
    /// Verify alert was committed by NOC.
    /// Retrieves the committed alert from NOC and compares with sent payload.
    /// </summary>
    /// <param name="alert">Alert to verify</param>
    /// <param name="sentPayload">The payload that was originally sent</param>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Result with status code and comparison result</returns>
    Task<NocVerifyResult> VerifyAlertAsync(AlertDto alert, NocHttpPayload sentPayload, string correlationId, CancellationToken ct = default);
}

