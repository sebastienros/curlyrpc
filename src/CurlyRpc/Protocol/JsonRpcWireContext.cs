using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurlyRpc;

/// <summary>
/// AOT-safe, source-generated serialization metadata for the fixed JSON-RPC envelope types.
/// Dynamic payloads (<c>params</c>, <c>result</c>, error <c>data</c>) are handled separately with
/// the caller-supplied <see cref="JsonSerializerOptions"/>; this context only covers the envelope.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(JsonRpcRequestWire))]
[JsonSerializable(typeof(JsonRpcNotificationWire))]
[JsonSerializable(typeof(JsonRpcResultWire))]
[JsonSerializable(typeof(JsonRpcErrorWire))]
[JsonSerializable(typeof(JsonRpcErrorDetail))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class JsonRpcWireContext : JsonSerializerContext
{
}
