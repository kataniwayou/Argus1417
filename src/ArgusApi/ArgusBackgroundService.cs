using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArgusApi;

/// <summary>
/// Base class for background workers with automatic heartbeat and exception monitoring.
/// Inherit from this class and implement DoWorkAsync() - heartbeat and exceptions are handled automatically.
/// </summary>
public abstract class ArgusBackgroundService : BackgroundService
{
    private readonly IArgusMonitor _monitor;
    private readonly ILogger _logger;
    private readonly string _workerName;

    /// <summary>
    /// The interval between work cycles. Override to customize.
    /// Default: 30 seconds.
    /// </summary>
    protected virtual TimeSpan Interval => TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to continue running after an exception. Override to customize.
    /// Default: true (worker continues after logging exception).
    /// </summary>
    protected virtual bool ContinueOnException => true;

    /// <summary>
    /// Gets the worker name (derived from type name).
    /// </summary>
    protected string WorkerName => _workerName;

    /// <summary>
    /// Gets the Argus monitor for advanced scenarios.
    /// </summary>
    protected IArgusMonitor Monitor => _monitor;

    protected ArgusBackgroundService(IArgusMonitor monitor, ILogger logger)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerName = GetType().Name;
    }

    /// <summary>
    /// Implement this method with your work logic.
    /// Called repeatedly at the configured Interval.
    /// Heartbeat is sent automatically after successful completion.
    /// Exceptions are recorded automatically.
    /// </summary>
    protected abstract Task DoWorkAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Called once when the worker starts. Override for initialization logic.
    /// </summary>
    protected virtual Task OnStartAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    /// <summary>
    /// Called once when the worker stops. Override for cleanup logic.
    /// </summary>
    protected virtual Task OnStopAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    /// <summary>
    /// Called when an exception occurs. Override for custom exception handling.
    /// Base implementation logs and records the exception.
    /// </summary>
    protected virtual Task OnExceptionAsync(Exception exception, CancellationToken stoppingToken)
    {
        _logger.LogError(exception, "{WorkerName} encountered an error", _workerName);
        _monitor.RecordException(this, exception, "DoWorkAsync");
        return Task.CompletedTask;
    }

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{WorkerName} starting at {Time}", _workerName, DateTimeOffset.Now);

        try
        {
            await OnStartAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{WorkerName} failed during OnStartAsync", _workerName);
            _monitor.RecordException(this, ex, "OnStartAsync");
            if (!ContinueOnException) return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Send heartbeat at start of cycle (indicates worker is alive and starting work)
                _monitor.Heartbeat(this);

                // Start trace for this work cycle
                using var activity = _monitor.StartTrace($"{_workerName}.DoWork");

                // Execute the work
                await DoWorkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown - don't record as exception
                break;
            }
            catch (Exception ex)
            {
                await OnExceptionAsync(ex, stoppingToken);
                if (!ContinueOnException) break;
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        try
        {
            await OnStopAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{WorkerName} failed during OnStopAsync", _workerName);
            _monitor.RecordException(this, ex, "OnStopAsync");
        }

        _logger.LogInformation("{WorkerName} stopped at {Time}", _workerName, DateTimeOffset.Now);
    }
}

