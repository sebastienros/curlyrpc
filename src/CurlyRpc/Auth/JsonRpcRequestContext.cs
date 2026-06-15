using System.Text.Json;

namespace CurlyRpc;

/// <summary>
/// Context describing a single inbound JSON-RPC request or notification as seen by a
/// <see cref="JsonRpcInboundMiddleware"/>.
/// </summary>
public readonly struct JsonRpcRequestContext
{
    internal JsonRpcRequestContext(JsonRpc connection, string method, JsonElement? parameters, bool isNotification)
    {
        Connection = connection;
        Method = method;
        Parameters = parameters;
        IsNotification = isNotification;
    }

    /// <summary>The connection on which the request arrived.</summary>
    public JsonRpc Connection { get; }

    /// <summary>The requested method name.</summary>
    public string Method { get; }

    /// <summary>The raw <c>params</c> payload, if any.</summary>
    public JsonElement? Parameters { get; }

    /// <summary><see langword="true"/> when the message is a notification (no response expected).</summary>
    public bool IsNotification { get; }
}
