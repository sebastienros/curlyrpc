using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurlyRpc;

/// <summary>
/// Represents a JSON-RPC request identifier, which may be a number, a string, or JSON <c>null</c>.
/// A default <see cref="RequestId"/> serializes as <c>null</c>.
/// </summary>
[JsonConverter(typeof(RequestIdConverter))]
public readonly record struct RequestId
{
    // Keep the original JSON token for lossless serialization and a canonical decimal key
    // for correlation. Neither floating point nor decimal can represent every JSON number.
    private readonly string? _numberJson;
    private readonly string? _numberKey;

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

    private RequestId(string numberJson, string numberKey, long? number)
    {
        _numberJson = numberJson;
        _numberKey = numberKey;
        Number = number;
    }

    /// <summary>The numeric value when exactly representable as an <see cref="long"/>; otherwise <see langword="null"/>.</summary>
    public long? Number { get; }

    /// <summary>The string value of this identifier, or <see langword="null"/> if it is not a string.</summary>
    public string? String { get; }

    /// <summary>Gets a value indicating whether this identifier is JSON <c>null</c>.</summary>
    public bool IsNull => !IsNumber && String is null;

    /// <summary>Gets whether this identifier is a JSON number, including values outside the range of <see cref="long"/>.</summary>
    public bool IsNumber => Number.HasValue || _numberJson is not null;

    internal string? NumberJson => _numberJson;

    internal static RequestId FromNumber(JsonElement element)
    {
        if (element.TryGetInt64(out long number))
        {
            return new RequestId(number);
        }

        string json = element.GetRawText();
        string key = NormalizeNumber(json, out string digits, out BigInteger exponent);
        long? integer = null;
        // Expand only small integral values; even a huge exponent never causes a huge allocation.
        if (exponent >= 0 && exponent <= 19 && digits.Length + (int)exponent <= 20 &&
            long.TryParse(digits + new string('0', (int)exponent), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out number))
        {
            integer = number;
        }

        return new RequestId(json, key, integer);
    }

    /// <summary>Compares identifiers by type and exact value, regardless of numeric notation.</summary>
    public bool Equals(RequestId other)
    {
        if (Number is { } number && other.Number is { } otherNumber)
        {
            return number == otherNumber;
        }

        if (IsNumber || other.IsNumber)
        {
            return IsNumber && other.IsNumber && GetNumberKey() == other.GetNumberKey();
        }

        return String == other.String;
    }

    /// <inheritdoc />
    public override int GetHashCode() => Number is { } number
        ? HashCode.Combine(true, number)
        : IsNumber ? HashCode.Combine(true, GetNumberKey()) : HashCode.Combine(false, String);

    private string GetNumberKey() => _numberKey ?? NormalizeNumber(
        Number!.Value.ToString(CultureInfo.InvariantCulture), out _, out _);

    private static string NormalizeNumber(string json, out string digits, out BigInteger exponent)
    {
        int exponentIndex = json.IndexOfAny(['e', 'E']);
        string mantissa = exponentIndex < 0 ? json : json[..exponentIndex];
        exponent = exponentIndex < 0 ? BigInteger.Zero
            : BigInteger.Parse(json.AsSpan(exponentIndex + 1), CultureInfo.InvariantCulture);
        int point = mantissa.IndexOf('.');
        if (point >= 0)
        {
            exponent -= mantissa.Length - point - 1;
            mantissa = mantissa.Remove(point, 1);
        }

        bool negative = mantissa[0] == '-';
        digits = (negative ? mantissa[1..] : mantissa).TrimStart('0');
        if (digits.Length == 0)
        {
            digits = "0";
            exponent = BigInteger.Zero;
        }
        else
        {
            string significant = digits.TrimEnd('0');
            exponent += digits.Length - significant.Length;
            digits = negative ? "-" + significant : significant;
        }

        return digits + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (_numberJson is { } json)
        {
            return json;
        }

        if (Number is { } n)
        {
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return String ?? "(null)";
    }
}
