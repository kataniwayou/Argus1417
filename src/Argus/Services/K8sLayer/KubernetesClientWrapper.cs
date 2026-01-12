using Argus.Configuration;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.K8sLayer;

/// <summary>
/// Wrapper for Kubernetes API client - simplified without retry logic or circuit breaker
/// </summary>
public class KubernetesClientWrapper : IKubernetesClientWrapper
{
    private readonly ILogger<KubernetesClientWrapper> _logger;
    private readonly K8sLayerConfiguration _options;
    private readonly IKubernetes _client;

    public KubernetesClientWrapper(
        ILogger<KubernetesClientWrapper> logger,
        IOptions<ArgusConfiguration> options)
    {
        _logger = logger;
        _options = options.Value.K8sLayer;

        // Initialize K8s client
        var config = _options.Kubernetes.UseInClusterConfig
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();

        _client = new Kubernetes(config);
    }

    /// <summary>
    /// Check if Kubernetes API server is available.
    /// Returns true if API is reachable, false otherwise.
    /// </summary>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<bool> CheckApiAvailabilityAsync(
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.Version.GetCodeAsync(cancellationToken);

            _logger.LogDebug(
                "K8s API server is available. CorrelationId={CorrelationId}",
                correlationId);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "K8s API server is unavailable. CorrelationId={CorrelationId}",
                correlationId);

            return false;
        }
    }

    /// <summary>
    /// Get pods matching the label selector in the configured namespace.
    /// Returns null on any failure (no retries, no circuit breaker).
    /// </summary>
    /// <param name="labelSelector">Kubernetes label selector</param>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<V1PodList?> GetPodsAsync(
        string labelSelector,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pods = await _client.CoreV1.ListNamespacedPodAsync(
                namespaceParameter: _options.Kubernetes.Namespace,
                labelSelector: labelSelector,
                timeoutSeconds: _options.Kubernetes.ApiTimeoutSeconds,
                cancellationToken: cancellationToken);

            _logger.LogDebug(
                "Successfully retrieved {Count} pods. CorrelationId={CorrelationId}, LabelSelector={LabelSelector}",
                pods.Items.Count, correlationId, labelSelector);

            return pods;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to get pods. CorrelationId={CorrelationId}, LabelSelector={LabelSelector}",
                correlationId, labelSelector);
            return null;
        }
    }
}

/// <summary>
/// Interface for Kubernetes client wrapper
/// </summary>
public interface IKubernetesClientWrapper
{
    /// <summary>
    /// Check if Kubernetes API server is available
    /// </summary>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<bool> CheckApiAvailabilityAsync(string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get pods matching the label selector in the configured namespace
    /// </summary>
    /// <param name="labelSelector">Kubernetes label selector</param>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<V1PodList?> GetPodsAsync(string labelSelector, string correlationId, CancellationToken cancellationToken = default);
}

