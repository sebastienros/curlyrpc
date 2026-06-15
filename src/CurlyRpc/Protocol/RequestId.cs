using System.Text.Json.Serialization;

namespace CurlyRpc;

/// <summary>
/// Represents a JSON-RPC request identifier, which may be a number, a string, or JSON <c>null</c>.
/// A default <see cref="RequestId"/> serializes as <c>null</c>.
/// </summary>
[JsonConverter(typeof(RequestIdConverter))]
public readonly record struct RequestId
{
    /// <summary>An explicit JSON <c>null</c> identifier (used for error responses to unparseable requests).</summary>
    public static readonly RequestId Null = default;

    /// <summary>Initializes a numeric identifier.</summary>
    public RequestId(long number)
    {
        Number = number;
        String = null;
    }

    /// <summary>Initializes a string identifier.</summary>
    public RequestId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Number = null;
        String = value;
    }

    /// <summary>The numeric value of this identifier, or <see langword="null"/> if it is not a number.</summary>
    public long? Number { get; }

    /// <summary>The string value of this identifier, or <see langword="null"/> if it is not a string.</summary>
    public string? String { get; }

    /// <summary>Gets a value indicating whether this identifier is JSON <c>null</c>.</summary>
    public bool IsNull => Number is null && String is null;

    /// <inheritdoc />
    public override string ToString()
    {
        if (Number is { } n)
        {
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return String ?? "(null)";
    }
}
