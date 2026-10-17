using System.Globalization;
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
        string key = NormalizeNumber(json, out string digits, out long exponent);
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

    // Exponents beyond this magnitude are kept opaque. The bound leaves room to fold in the
    // mantissa's fraction and trailing-zero counts (each at most int.MaxValue) without overflow.
    private const long MaximumExponentMagnitude = 1_000_000_000_000_000_000;

    // Peer-controlled tokens never go through BigInteger, whose parsing and formatting are
    // quadratic in the token length. Every step here is linear.
    private static string NormalizeNumber(string json, out string digits, out long exponent)
    {
        int exponentIndex = json.IndexOfAny(['e', 'E']);
        string mantissa = exponentIndex < 0 ? json : json[..exponentIndex];
        exponent = 0;
        if (exponentIndex >= 0 &&
            (!long.TryParse(json.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent) ||
             exponent > MaximumExponentMagnitude || exponent < -MaximumExponentMagnitude))
        {
            // Zero is zero whatever its exponent. Any other value keeps its raw token as an opaque
            // key; the 'x' prefix cannot collide with a canonical key, which starts with a digit or '-'.
            if (mantissa.AsSpan().TrimStart('-').IndexOfAnyExcept('0', '.') < 0)
            {
                digits = "0";
                exponent = 0;
                return "0e0";
            }

            digits = mantissa;
            exponent = long.MaxValue;
            return "x" + json;
        }

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
            exponent = 0;
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
