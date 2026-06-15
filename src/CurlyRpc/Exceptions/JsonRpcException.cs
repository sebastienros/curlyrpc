namespace CurlyRpc;

/// <summary>
/// The base class for all JSON-RPC errors raised by CurlyRpc.
/// </summary>
public class JsonRpcException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="JsonRpcException"/> class.</summary>
    public JsonRpcException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JsonRpcException"/> class.</summary>
    public JsonRpcException(string? message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JsonRpcException"/> class.</summary>
    public JsonRpcException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
