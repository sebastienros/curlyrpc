using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CurlyRpc.Extensions.Logging;

/// <summary>
/// Bridges <see cref="JsonRpc"/> diagnostics to <see cref="Microsoft.Extensions.Logging.ILogger"/> by
/// subscribing to the library's <see cref="System.Diagnostics.ActivitySource"/>. Activity start, stop,
/// and error events are written as structured log entries. Dispose the bridge to detach the listener.
/// </summary>
/// <remarks>
/// This is an optional convenience for applications that prefer <c>ILogger</c> output over wiring the
/// activity source into an OpenTelemetry pipeline. The core library has no dependency on it.
/// </remarks>
public sealed class JsonRpcLoggingBridge : IDisposable
{
    private readonly ActivityListener _listener;

    /// <summary>Creates a bridge that writes to <paramref name="logger"/>.</summary>
    /// <param name="logger">The logger that receives JSON-RPC activity events.</param>
    public JsonRpcLoggingBridge(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == JsonRpcDiagnostics.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => OnStarted(logger, activity),
            ActivityStopped = activity => OnStopped(logger, activity),
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Creates a bridge using a logger from <paramref name="loggerFactory"/>.</summary>
    public static JsonRpcLoggingBridge Create(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return new JsonRpcLoggingBridge(loggerFactory.CreateLogger(JsonRpcDiagnostics.SourceName));
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private static void OnStarted(ILogger logger, Activity activity)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("JSON-RPC {Kind} call '{Method}' started.", activity.Kind, activity.DisplayName);
        }
    }

    private static void OnStopped(ILogger logger, Activity activity)
    {
        if (activity.Status == ActivityStatusCode.Error)
        {
            logger.LogError(
                "JSON-RPC {Kind} call '{Method}' failed after {DurationMs:F2} ms: {Reason}",
                activity.Kind,
                activity.DisplayName,
                activity.Duration.TotalMilliseconds,
                activity.StatusDescription ?? "error");
            return;
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "JSON-RPC {Kind} call '{Method}' completed in {DurationMs:F2} ms.",
                activity.Kind,
                activity.DisplayName,
                activity.Duration.TotalMilliseconds);
        }
    }
}
