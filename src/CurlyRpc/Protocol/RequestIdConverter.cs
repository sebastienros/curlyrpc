using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurlyRpc;

/// <summary>
/// Serializes <see cref="RequestId"/> values as a JSON number, string, or <c>null</c>.
/// </summary>
internal sealed class RequestIdConverter : JsonConverter<RequestId>
{
    public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return new RequestId(reader.GetInt64());
            case JsonTokenType.String:
                return new RequestId(reader.GetString()!);
            case JsonTokenType.Null:
                return RequestId.Null;
            default:
                throw new JsonException($"Unexpected token '{reader.TokenType}' for a JSON-RPC id.");
        }
    }

    public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
    {
        if (value.Number is { } number)
        {
            writer.WriteNumberValue(number);
        }
        else if (value.String is { } text)
        {
            writer.WriteStringValue(text);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
