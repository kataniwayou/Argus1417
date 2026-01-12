using System.Diagnostics;

namespace ArgusApi;

/// <summary>
/// Interface for Argus monitoring - heartbeat, exceptions, and traces.
/// </summary>
public interface IArgusMonitor
{
    /// <summary>
    /// Records a heartbeat for the specified worker.
    /// Worker name is derived from the object's type name.
    /// Auto-registers on first call.
    /// </summary>
    /// <param name="worker">The worker instance (typically 'this')</param>
    void Heartbeat(object worker);

    /// <summary>
    /// Records a heartbeat for the specified worker type.
    /// Auto-registers on first call.
    /// </summary>
    /// <typeparam name="T">The worker type</typeparam>
    void Heartbeat<T>();

    /// <summary>
    /// Records a heartbeat for a named component.
    /// Use this for non-worker contexts like services, controllers, or scheduled tasks.
    /// </summary>
    /// <param name="componentName">Name of the component sending the heartbeat</param>
    void Heartbeat(string componentName);

    /// <summary>
    /// Records an exception from a worker context.
    /// </summary>
    /// <param name="worker">The worker instance (typically 'this')</param>
    /// <param name="exception">The exception that occurred</param>
    /// <param name="operation">Optional operation name where exception occurred</param>
    void RecordException(object worker, Exception exception, string? operation = null);

    /// <summary>
    /// Records an exception from a non-worker context.
    /// </summary>
    /// <param name="exception">The exception that occurred</param>
    /// <param name="component">Component/source where exception occurred</param>
    /// <param name="operation">Optional operation name where exception occurred</param>
    void RecordException(Exception exception, string? component = null, string? operation = null);

    /// <summary>
    /// Starts a new trace/span for the given operation.
    /// Use with 'using' statement for automatic completion.
    /// </summary>
    /// <param name="operationName">Name of the operation being traced</param>
    /// <returns>Activity (span) or null if tracing is disabled</returns>
    Activity? StartTrace(string operationName);

    /// <summary>
    /// Starts a new trace/span for the given operation with a parent context.
    /// </summary>
    /// <param name="operationName">Name of the operation being traced</param>
    /// <param name="kind">The kind of activity (client, server, etc.)</param>
    /// <returns>Activity (span) or null if tracing is disabled</returns>
    Activity? StartTrace(string operationName, ActivityKind kind);

    /// <summary>
    /// Gets the underlying ActivitySource for advanced tracing scenarios.
    /// </summary>
    ActivitySource ActivitySource { get; }

    /// <summary>
    /// Gets the telemetry prefix used for naming (argus_{serviceName}_{version}).
    /// </summary>
    string TelemetryPrefix { get; }
}

