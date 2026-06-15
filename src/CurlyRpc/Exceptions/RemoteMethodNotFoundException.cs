namespace CurlyRpc;

/// <summary>
/// Thrown on the calling side when the remote peer reports that the requested method does not exist
/// (JSON-RPC error code <c>-32601</c>).
/// </summary>
public sealed class RemoteMethodNotFoundException : RemoteInvocationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteMethodNotFoundException"/> class.
    /// </summary>
    public RemoteMethodNotFoundException(string? message, string methodName)
        : base(message, JsonRpcErrorCodes.MethodNotFound)
    {
        MethodName = methodName;
    }

    /// <summary>The name of the method that was not found.</summary>
    public string MethodName { get; }
}
