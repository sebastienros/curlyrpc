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
                if (reader.TryGetInt64(out long number))
                {
                    return new RequestId(number);
                }

                using (JsonDocument document = JsonDocument.ParseValue(ref reader))
                {
                    return RequestId.FromNumber(document.RootElement);
                }
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
        if (value.NumberJson is { } json)
        {
            writer.WriteRawValue(json);
        }
        else if (value.Number is { } number)
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
