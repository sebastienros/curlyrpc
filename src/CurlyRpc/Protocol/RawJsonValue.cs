using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurlyRpc;

/// <summary>
/// A pre-serialized UTF-8 JSON value carried verbatim on the wire. The dynamic JSON-RPC payloads
/// (<c>params</c>, <c>result</c>, error <c>data</c>) are serialized once with the caller-supplied
/// <see cref="JsonSerializerOptions"/> and then written straight through with
/// <see cref="Utf8JsonWriter.WriteRawValue(System.ReadOnlySpan{byte}, bool)"/>, avoiding the
/// parse/clone/re-tokenize round-trip that storing them as a <see cref="JsonElement"/> would incur.
/// </summary>
[JsonConverter(typeof(RawJsonValueConverter))]
internal readonly struct RawJsonValue
{
    private readonly ReadOnlyMemory<byte> _utf8;

    private RawJsonValue(ReadOnlyMemory<byte> utf8) => _utf8 = utf8;

    /// <summary>The raw UTF-8 bytes of exactly one JSON value.</summary>
    public ReadOnlySpan<byte> Span => _utf8.Span;

    /// <summary>
    /// Captures the contents already written to a serialization buffer (exactly one JSON value).
    /// The span is copied so the value can outlive the pooled writer.
    /// </summary>
    public static RawJsonValue FromWritten(ReadOnlySpan<byte> utf8) => new(utf8.ToArray());

    /// <summary>
    /// Wraps the raw UTF-8 backing a <see cref="JsonElement"/>. On .NET 9+ this reads the element's
    /// underlying bytes directly via <c>JsonMarshal.GetRawUtf8Value</c> instead of re-encoding it.
    /// </summary>
    public static RawJsonValue FromElement(JsonElement element)
    {
#if NET9_0_OR_GREATER
        return new RawJsonValue(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(element).ToArray());
#else
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            element.WriteTo(writer);
        }

        return new RawJsonValue(buffer.WrittenSpan.ToArray());
#endif
    }
}

/// <summary>
/// Serializes a <see cref="RawJsonValue"/> by emitting its bytes verbatim. Reading is only present for
/// completeness — the wire envelopes are serialize-only and inbound messages are parsed with
/// <see cref="JsonDocument"/> navigation, never deserialized into the wire DTOs.
/// </summary>
internal sealed class RawJsonValueConverter : JsonConverter<RawJsonValue>
{
    public override RawJsonValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return RawJsonValue.FromElement(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, RawJsonValue value, JsonSerializerOptions options)
        => writer.WriteRawValue(value.Span, skipInputValidation: true);
}
