namespace CurlyRpc;

/// <summary>
/// Thrown by a local JSON-RPC handler to return a specific JSON-RPC error (code and optional data)
/// to the remote caller, instead of the default <see cref="JsonRpcErrorCodes.InternalError"/>.
/// </summary>
public class LocalRpcException : JsonRpcException
{
    /// <summary>Initializes a new instance of the <see cref="LocalRpcException"/> class.</summary>
    public LocalRpcException(string? message, int errorCode = JsonRpcErrorCodes.InternalError, object? errorData = null)
        : base(message)
    {
        ErrorCode = errorCode;
        ErrorData = errorData;
    }

    /// <summary>The JSON-RPC error code to report to the caller.</summary>
    public int ErrorCode { get; }

    /// <summary>Optional structured data serialized into the error's <c>data</c> member.</summary>
    public object? ErrorData { get; }
}
