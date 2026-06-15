namespace CurlyRpc;

/// <summary>
/// Internal exception signalling that inbound <c>params</c> could not be bound to a handler's
/// parameters. Mapped to JSON-RPC error code <c>-32602</c> (Invalid params).
/// </summary>
internal sealed class RpcInvalidParametersException : JsonRpcException
{
    public RpcInvalidParametersException(string message)
        : base(message)
    {
    }

    public RpcInvalidParametersException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
