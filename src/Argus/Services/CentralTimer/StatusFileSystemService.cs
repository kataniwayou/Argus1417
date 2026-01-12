using Argus.Configuration;
using Argus.Models;
using Argus.Services.AlertsVector;
using Argus.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.CentralTimer;

/// <summary>
/// StatusFileSystem Service - monitors write accessibility to the heartbeat destination path.
/// Checks folder existence and write permissions.
/// Generates CREATE alert if inaccessible, CANCEL if accessible.
/// Priority: -6
/// Each callback stamps LivenessVector on success/failure (not exception).
/// </summary>
public class StatusFileSystemService : IStatusFileSystemService
{
    private const int AlertPriority = -6;
    private const string AlertName = "StatusFileSystemDown";
    private const string AlertFingerprint = "status-filesystem";
    private const string AlertSource = "StatusFileSystem";
    private const string CallbackName = "StatusFileSystemCheck";

    private readonly ILogger<StatusFileSystemService> _logger;
    private readonly ICentralTimerService _centralTimer;
    private readonly IAlertsVectorService _alertsVector;
    private readonly ILivenessVectorService _livenessVector;
    private readonly FileHeartbeatConfiguration _fileHeartbeatConfig;
    private readonly StatusFileSystemConfiguration _config;

    private bool _lastCheckSuccessful = true;
    private string? _lastErrorReason;
    private long _checkCount;
    private int _intervalTicks;

    public bool IsAccessible => _lastCheckSuccessful;
    public string? LastErrorReason => _lastErrorReason;
    public long CheckCount => Interlocked.Read(ref _checkCount);

    public StatusFileSystemService(
        ILogger<StatusFileSystemService> logger,
        ICentralTimerService centralTimer,
        IAlertsVectorService alertsVector,
        ILivenessVectorService livenessVector,
        IOptions<ArgusConfiguration> config)
    {
        _logger = logger;
        _centralTimer = centralTimer;
        _alertsVector = alertsVector;
        _livenessVector = livenessVector;
        _fileHeartbeatConfig = config.Value.Heartbeat.File;
        _config = config.Value.StatusFileSystem;

        _logger.LogInformation(
            "StatusFileSystemService initialized. PollingInterval={PollingInterval}s, DestinationPath={DestinationPath}",
            _config.PollingIntervalSeconds, _fileHeartbeatConfig.DestinationPath);
    }

    public void Start()
    {
        // Register callback with central timer
        _intervalTicks = _config.PollingIntervalSeconds / _centralTimer.TickIntervalSeconds;
        if (_intervalTicks < 1) _intervalTicks = 1;

        var callback = new CentralTimerCallback(
            Name: CallbackName,
            IntervalTicks: _intervalTicks,
            Callback: OnCheckAsync,
            IsGracePeriodAware: false); // Check immediately, don't wait for grace period

        _centralTimer.RegisterCallback(callback);

        _logger.LogInformation(
            "StatusFileSystemService registered with CentralTimer. IntervalTicks={IntervalTicks}",
            _intervalTicks);

        // Perform initial check (tick 0, with init-prefixed correlationId)
        var initCorrelationId = $"init-{Guid.NewGuid().ToString("N")[..8]}";
        _ = Task.Run(async () => await OnCheckAsync(0, initCorrelationId, CancellationToken.None));
    }

    /// <summary>
    /// CentralTimer callback for StatusFileSystem check.
    /// correlationId received from CentralTimer (single source of truth for tick correlation).
    /// Stamps LivenessVector on success/failure (not exception).
    /// </summary>
    private async Task OnCheckAsync(long tick, string correlationId, CancellationToken stoppingToken)
    {
        Interlocked.Increment(ref _checkCount);
        var executionId = GenerateExecutionId();

        try
        {
            var (isAccessible, errorReason) = await CheckDestinationPathAsync(stoppingToken);
            _lastCheckSuccessful = isAccessible;
            _lastErrorReason = errorReason;

            // Generate and update alert
            var alert = GenerateAlert(isAccessible, errorReason, executionId);
            _alertsVector.UpdateAlert(alert);

            if (!isAccessible)
            {
                _logger.LogWarning(
                    "StatusFileSystem check failed: {ErrorReason}. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    errorReason, correlationId, executionId);
            }
            else
            {
                _logger.LogDebug(
                    "StatusFileSystem check passed. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    correlationId, executionId);
            }
        }
        catch (Exception ex)
        {
            _lastCheckSuccessful = false;
            _lastErrorReason = ex.Message;

            var alert = GenerateAlert(false, ex.Message, executionId);
            _alertsVector.UpdateAlert(alert);

            _logger.LogError(ex, "StatusFileSystem check error. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                correlationId, executionId);
        }
        finally
        {
            // Always stamp - callback executed (didn't silently die)
            _livenessVector.RecordExecution(CallbackName, _intervalTicks, tick);
        }
    }

    private async Task<(bool IsAccessible, string? ErrorReason)> CheckDestinationPathAsync(CancellationToken stoppingToken)
    {
        var destinationPath = _fileHeartbeatConfig.DestinationPath;

        // Get directory from file path
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory))
        {
            return (false, $"Invalid destination path: {destinationPath}");
        }

        // Check if directory exists
        if (!Directory.Exists(directory))
        {
            return (false, $"Directory does not exist: {directory}");
        }

        // Check write permission by attempting to create a test file
        var testFilePath = Path.Combine(directory, $".argus_write_test_{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(testFilePath, "test", stoppingToken);
            File.Delete(testFilePath);
            return (true, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, $"No write permission to directory: {directory}");
        }
        catch (IOException ex)
        {
            return (false, $"IO error accessing directory: {ex.Message}");
        }
        finally
        {
            // Cleanup test file if it exists
            try { if (File.Exists(testFilePath)) File.Delete(testFilePath); } catch { /* ignore */ }
        }
    }

    private AlertDto GenerateAlert(bool isAccessible, string? errorReason, string executionId)
    {
        var nocBehavior = isAccessible ? _config.CancelNocBehavior : _config.CreateNocBehavior;
        var status = isAccessible ? AlertStatus.CANCEL : AlertStatus.CREATE;
        var summary = isAccessible
            ? "Status file destination is accessible"
            : "Status file destination is inaccessible";

        var alert = new AlertDto
        {
            Priority = AlertPriority,
            Name = AlertName,
            Fingerprint = AlertFingerprint,
            Source = AlertSource,
            Status = status,
            Summary = summary,
            Description = errorReason ?? $"Destination path: {_fileHeartbeatConfig.DestinationPath}",
            Payload = nocBehavior.Payload.Clone(),
            SendToNoc = nocBehavior.SendToNoc,
            SuppressWindow = ParseSuppressWindow(nocBehavior.SuppressWindow),
            Timestamp = DateTime.UtcNow,
            ExecutionId = executionId
        };

        // Apply runtime overrides (level, message, source, suppressionKey)
        alert.Payload.ApplyAlertOverrides(alert);

        return alert;
    }

    private static string GenerateExecutionId() => $"exec-{Guid.NewGuid().ToString("N")[..8]}";

    private static TimeSpan? ParseSuppressWindow(string? suppressWindow)
    {
        if (string.IsNullOrWhiteSpace(suppressWindow))
            return null;

        if (TimeSpanParser.TryParseToTimeSpan(suppressWindow, out var timeSpan))
            return timeSpan;

        return null;
    }
}

/// <summary>
/// Interface for StatusFileSystem monitoring service
/// </summary>
public interface IStatusFileSystemService
{
    /// <summary>Whether the destination path is currently accessible</summary>
    bool IsAccessible { get; }

    /// <summary>Last error reason if not accessible</summary>
    string? LastErrorReason { get; }

    /// <summary>Number of checks performed</summary>
    long CheckCount { get; }

    /// <summary>Start the service and register with CentralTimer</summary>
    void Start();
}

