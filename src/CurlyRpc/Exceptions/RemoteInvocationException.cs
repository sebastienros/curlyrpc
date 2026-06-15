using System.Text.Json;

namespace CurlyRpc;

/// <summary>
/// Thrown on the calling side when the remote peer returns a JSON-RPC error response.
/// </summary>
public class RemoteInvocationException : JsonRpcException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteInvocationException"/> class.
    /// </summary>
    /// <param name="message">The error message reported by the remote peer.</param>
    /// <param name="errorCode">The JSON-RPC error code.</param>
    /// <param name="errorData">The optional structured <c>data</c> member of the error, if present.</param>
    public RemoteInvocationException(string? message, int errorCode, JsonElement? errorData = null)
        : base(message)
    {
        ErrorCode = errorCode;
        ErrorData = errorData;
    }

    /// <summary>The JSON-RPC error code returned by the remote peer.</summary>
    public int ErrorCode { get; }

    /// <summary>
    /// The structured <c>data</c> member of the error response, if one was supplied. Deserialize it
    /// with <see cref="GetErrorData{T}(JsonSerializerOptions)"/> or inspect it directly.
    /// </summary>
    public JsonElement? ErrorData { get; }

    /// <summary>
    /// Deserializes the error <c>data</c> payload into <typeparamref name="T"/> using the supplied options.
    /// </summary>
    /// <returns>The deserialized value, or <see langword="default"/> if no data was present.</returns>
    public T? GetErrorData<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (ErrorData is not { } data)
        {
            return default;
        }

        return (T?)data.Deserialize(options.GetTypeInfo(typeof(T)));
    }
}
