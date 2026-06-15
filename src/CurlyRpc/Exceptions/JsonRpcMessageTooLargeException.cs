namespace CurlyRpc;

/// <summary>
/// Thrown by a message handler when an inbound frame exceeds the configured
/// <see cref="JsonRpcOptions.MaximumInboundMessageSize"/> (or the handler's
/// <see cref="StreamMessageHandler.MaximumMessageSize"/>). The connection is faulted and closed so a
/// peer cannot exhaust memory by declaring (or streaming) an arbitrarily large message.
/// </summary>
public sealed class JsonRpcMessageTooLargeException : JsonRpcException
{
    /// <summary>Initializes a new instance of the <see cref="JsonRpcMessageTooLargeException"/> class.</summary>
    public JsonRpcMessageTooLargeException(int maximumMessageSize)
        : base($"An inbound JSON-RPC message exceeded the maximum allowed size of {maximumMessageSize} bytes.")
    {
        MaximumMessageSize = maximumMessageSize;
    }

    /// <summary>The configured maximum inbound message size, in bytes, that was exceeded.</summary>
    public int MaximumMessageSize { get; }
}
