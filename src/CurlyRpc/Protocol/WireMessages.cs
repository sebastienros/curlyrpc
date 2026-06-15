using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurlyRpc;

/// <summary>
/// The JSON-RPC protocol version string emitted on every message.
/// </summary>
internal static class JsonRpcConstants
{
    public const string Version = "2.0";
}

/// <summary>
/// Wire representation of an outbound JSON-RPC request (a call that expects a response).
/// The dynamic <c>params</c> payload is pre-serialized into a <see cref="JsonElement"/> using
/// the caller-supplied <see cref="JsonSerializerOptions"/> so this envelope stays AOT-safe.
/// </summary>
internal sealed class JsonRpcRequestWire
{
    [JsonPropertyName("jsonrpc")]
    public string Version { get; set; } = JsonRpcConstants.Version;

    [JsonPropertyName("id")]
    public RequestId Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }
}

/// <summary>
/// Wire representation of an outbound JSON-RPC notification (a call with no response). It has no
/// <c>id</c> member, which is what distinguishes it from a request on the wire.
/// </summary>
internal sealed class JsonRpcNotificationWire
{
    [JsonPropertyName("jsonrpc")]
    public string Version { get; set; } = JsonRpcConstants.Version;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }
}

/// <summary>
/// Wire representation of a successful JSON-RPC response. Per the spec, <c>result</c> is always
/// present (it may be JSON <c>null</c>).
/// </summary>
internal sealed class JsonRpcResultWire
{
    [JsonPropertyName("jsonrpc")]
    public string Version { get; set; } = JsonRpcConstants.Version;

    [JsonPropertyName("id")]
    public RequestId Id { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }
}

/// <summary>
/// Wire representation of a failed JSON-RPC response.
/// </summary>
internal sealed class JsonRpcErrorWire
{
    [JsonPropertyName("jsonrpc")]
    public string Version { get; set; } = JsonRpcConstants.Version;

    [JsonPropertyName("id")]
    public RequestId Id { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcErrorDetail Error { get; set; } = new();
}

/// <summary>
/// The <c>error</c> object of a JSON-RPC error response.
/// </summary>
internal sealed class JsonRpcErrorDetail
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; set; }
}
