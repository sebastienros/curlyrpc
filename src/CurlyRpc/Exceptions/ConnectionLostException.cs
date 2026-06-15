namespace CurlyRpc;

/// <summary>
/// Thrown for pending requests when the underlying connection is closed or lost before a response
/// is received.
/// </summary>
public sealed class ConnectionLostException : JsonRpcException
{
    /// <summary>Initializes a new instance of the <see cref="ConnectionLostException"/> class.</summary>
    public ConnectionLostException()
        : base("The JSON-RPC connection was lost before the request completed.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ConnectionLostException"/> class.</summary>
    public ConnectionLostException(string? message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ConnectionLostException"/> class.</summary>
    public ConnectionLostException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
