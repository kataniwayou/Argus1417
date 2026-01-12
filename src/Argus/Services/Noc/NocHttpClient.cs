using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Argus.Configuration;
using Argus.Models;
using Microsoft.Extensions.Options;

namespace Argus.Services.Noc;

/// <summary>
/// HTTP client for communicating with NOC API.
/// Uses SocketsHttpHandler for HTTPS with SSL bypass and optional IP override.
/// </summary>
public class NocHttpClient : INocHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NocHttpClient> _logger;
    private readonly NocHttpClientConfiguration _config;
    private readonly JsonSerializerOptions _jsonOptions;

    public NocHttpClient(
        HttpClient httpClient,
        ILogger<NocHttpClient> logger,
        IOptions<NocHttpClientConfiguration> config)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = config.Value;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        // Configure timeout
        _httpClient.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);

        // Configure basic auth if provided
        if (!string.IsNullOrEmpty(_config.Username) && !string.IsNullOrEmpty(_config.Password))
        {
            var credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_config.Username}:{_config.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    /// <inheritdoc />
    public async Task<NocHttpResult> SendAlertAsync(AlertDto alert, string correlationId, CancellationToken ct = default)
    {
        // Create payload from alert with runtime overrides applied
        var payload = NocHttpPayload.FromAlert(alert, alert.Payload);

        // Apply static fields from configuration
        payload.Custom1 = !string.IsNullOrEmpty(payload.Custom1) ? payload.Custom1 : _config.TeamName;
        payload.Custom2 = !string.IsNullOrEmpty(payload.Custom2) ? payload.Custom2 : _config.SystemName;
        payload.HostName = !string.IsNullOrEmpty(payload.HostName) ? payload.HostName : _config.HostName;

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogDebug(
            "[{CorrelationId}] Sending alert to NOC: {AlertName} (Level={Level}, SuppressionKey={SuppressionKey})",
            correlationId, alert.Name, payload.Level, payload.SuppressionKey);

        try
        {
            var response = await _httpClient.PostAsync(_config.SendEndpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            _logger.LogDebug(
                "[{CorrelationId}] NOC response: {StatusCode} - {ResponseBody}",
                correlationId, (int)response.StatusCode, responseBody);

            return new NocHttpResult
            {
                StatusCode = (int)response.StatusCode,
                SentPayload = payload,
                ResponseBody = responseBody,
                ErrorMessage = response.IsSuccessStatusCode ? null : responseBody
            };
        }
        catch (HttpRequestException ex)
        {
            // Network errors - log warning without stack trace (expected in some environments)
            _logger.LogWarning(
                "[{CorrelationId}] Failed to send alert to NOC: {AlertName} ({ErrorType}: {Error})",
                correlationId, alert.Name, ex.GetType().Name, ex.Message);

            return new NocHttpResult
            {
                StatusCode = 0,
                SentPayload = payload,
                ErrorMessage = ex.Message
            };
        }
        catch (Exception ex)
        {
            // Unexpected errors - log error with stack trace
            _logger.LogError(ex,
                "[{CorrelationId}] Failed to send alert to NOC: {AlertName}",
                correlationId, alert.Name);

            return new NocHttpResult
            {
                StatusCode = 0,
                SentPayload = payload,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<NocVerifyResult> VerifyAlertAsync(
        AlertDto alert,
        NocHttpPayload sentPayload,
        string correlationId,
        CancellationToken ct = default)
    {
        // Build filterDoc from sent payload (includes userTga1, userTga2, userTga3 as empty strings)
        var filterDoc = NocFilterDoc.FromPayload(sentPayload);
        var json = JsonSerializer.Serialize(filterDoc, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogDebug(
            "[{CorrelationId}] Verifying alert with NOC: {AlertName} (SuppressionKey={SuppressionKey})",
            correlationId, alert.Name, sentPayload.SuppressionKey);

        try
        {
            var response = await _httpClient.PostAsync(_config.VerifyEndpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[{CorrelationId}] NOC verify failed: {StatusCode} - {ResponseBody}",
                    correlationId, (int)response.StatusCode, responseBody);

                return new NocVerifyResult
                {
                    StatusCode = (int)response.StatusCode,
                    ComparisonSuccess = false,
                    ErrorMessage = responseBody
                };
            }

            // Parse received payload and compare
            var receivedPayload = JsonSerializer.Deserialize<NocHttpPayload>(responseBody, _jsonOptions);
            var comparisonSuccess = ComparePayloads(sentPayload, receivedPayload);

            _logger.LogDebug(
                "[{CorrelationId}] NOC verify result: Comparison={ComparisonSuccess}",
                correlationId, comparisonSuccess);

            return new NocVerifyResult
            {
                StatusCode = (int)response.StatusCode,
                ComparisonSuccess = comparisonSuccess,
                ReceivedPayload = receivedPayload
            };
        }
        catch (HttpRequestException ex)
        {
            // Network errors - log warning without stack trace (expected in some environments)
            _logger.LogWarning(
                "[{CorrelationId}] Failed to verify alert with NOC: {AlertName} ({ErrorType}: {Error})",
                correlationId, alert.Name, ex.GetType().Name, ex.Message);

            return new NocVerifyResult
            {
                StatusCode = 0,
                ComparisonSuccess = false,
                ErrorMessage = ex.Message
            };
        }
        catch (Exception ex)
        {
            // Unexpected errors - log error with stack trace
            _logger.LogError(ex,
                "[{CorrelationId}] Failed to verify alert with NOC: {AlertName}",
                correlationId, alert.Name);

            return new NocVerifyResult
            {
                StatusCode = 0,
                ComparisonSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Compare sent payload with received payload.
    /// Key comparison is on suppressionKey (fingerprint).
    /// </summary>
    private static bool ComparePayloads(NocHttpPayload sent, NocHttpPayload? received)
    {
        if (received == null)
            return false;

        // Primary comparison: suppressionKey must match
        if (sent.SuppressionKey != received.SuppressionKey)
            return false;

        // Secondary comparisons: level and source should match
        if (sent.Level != received.Level)
            return false;

        if (sent.Source != received.Source)
            return false;

        return true;
    }
}
