using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CurlyRpc;

/// <summary>
/// Exposes the <see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/>
/// names emitted by <see cref="JsonRpc"/>. Consumers wire these into OpenTelemetry by subscribing to the
/// source and meter named <see cref="SourceName"/>; no package dependency on OpenTelemetry is required.
/// </summary>
public static class JsonRpcDiagnostics
{
    /// <summary>The name of the <see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/>.</summary>
    public const string SourceName = "CurlyRpc";

    /// <summary>The <c>rpc.system</c> attribute value used on emitted spans and metrics.</summary>
    public const string RpcSystem = "jsonrpc";

    internal static readonly string Version =
        typeof(JsonRpcDiagnostics).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    internal static readonly ActivitySource ActivitySource = new(SourceName, Version);

    internal static readonly Meter Meter = new(SourceName, Version);

    internal static readonly Histogram<double> ClientCallDuration = Meter.CreateHistogram<double>(
        "rpc.client.duration",
        unit: "ms",
        description: "Duration of outbound JSON-RPC calls.");

    internal static readonly Histogram<double> ServerCallDuration = Meter.CreateHistogram<double>(
        "rpc.server.duration",
        unit: "ms",
        description: "Duration of inbound JSON-RPC dispatch.");

    internal static readonly UpDownCounter<long> ClientCallsInFlight = Meter.CreateUpDownCounter<long>(
        "rpc.client.in_flight",
        unit: "{call}",
        description: "Number of outbound JSON-RPC calls awaiting a response.");

    internal static readonly UpDownCounter<long> ServerCallsInFlight = Meter.CreateUpDownCounter<long>(
        "rpc.server.in_flight",
        unit: "{call}",
        description: "Number of inbound JSON-RPC requests currently being handled.");

    internal static Activity? StartClientActivity(string method, long requestId)
    {
        if (!ActivitySource.HasListeners())
        {
            return null;
        }

        Activity? activity = ActivitySource.StartActivity(method, ActivityKind.Client);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("rpc.system", RpcSystem);
            activity.SetTag("rpc.method", method);
            activity.SetTag("rpc.jsonrpc.request_id", requestId);
        }

        return activity;
    }

    internal static Activity? StartServerActivity(string method, RequestId requestId)
    {
        if (!ActivitySource.HasListeners())
        {
            return null;
        }

        Activity? activity = ActivitySource.StartActivity(method, ActivityKind.Server);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("rpc.system", RpcSystem);
            activity.SetTag("rpc.method", method);
            if (!requestId.IsNull)
            {
                activity.SetTag("rpc.jsonrpc.request_id", requestId.ToString());
            }
        }

        return activity;
    }

    internal static void SetError(Activity? activity, int errorCode, string? message)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, message);
        activity.SetTag("rpc.jsonrpc.error_code", errorCode);
    }

    internal static void SetError(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag("error.type", exception.GetType().FullName);
    }

    internal static double ElapsedMilliseconds(long startTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
