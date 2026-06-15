using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CurlyRpc;

/// <summary>
/// A built-in <see cref="JsonRpcInboundMiddleware"/> implementing the key-based handshake used by
/// hosts such as <c>microsoft/aspire</c>: a peer must call <c>authenticate</c> with a shared secret
/// before any other method is permitted. The token is compared in constant time with
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><c>ping</c> is always permitted (liveness probe before authentication).</description></item>
/// <item><description><c>authenticate</c> validates the supplied token.</description></item>
/// <item><description>A missing token returns a retryable error and keeps the connection open.</description></item>
/// <item><description>An incorrect token returns an error and closes the connection.</description></item>
/// <item><description>All other methods are rejected until authentication succeeds.</description></item>
/// </list>
/// </remarks>
public sealed class HandshakeAuthenticationMiddleware : JsonRpcInboundMiddleware
{
    /// <summary>Error code returned when a method is invoked before authentication.</summary>
    public const int AuthenticationRequiredErrorCode = -32001;

    /// <summary>Error code returned when authentication fails or a token is missing.</summary>
    public const int AuthenticationFailedErrorCode = -32002;

    private readonly byte[] _expectedToken;
    private readonly string _authenticateMethodName;
    private readonly string _pingMethodName;
    private volatile bool _authenticated;

    /// <summary>Creates the middleware with a UTF-8 string secret.</summary>
    /// <param name="token">The shared secret a peer must present to authenticate.</param>
    /// <param name="authenticateMethodName">The handshake method name. Defaults to <c>authenticate</c>.</param>
    /// <param name="pingMethodName">The pre-auth liveness method name. Defaults to <c>ping</c>.</param>
    public HandshakeAuthenticationMiddleware(string token, string authenticateMethodName = "authenticate", string pingMethodName = "ping")
        : this(Encoding.UTF8.GetBytes(token ?? throw new ArgumentNullException(nameof(token))), authenticateMethodName, pingMethodName)
    {
    }

    /// <summary>Creates the middleware with a raw byte secret. The array is copied.</summary>
    public HandshakeAuthenticationMiddleware(byte[] token, string authenticateMethodName = "authenticate", string pingMethodName = "ping")
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrEmpty(authenticateMethodName);
        ArgumentException.ThrowIfNullOrEmpty(pingMethodName);

        _expectedToken = (byte[])token.Clone();
        _authenticateMethodName = authenticateMethodName;
        _pingMethodName = pingMethodName;
    }

    /// <summary>Whether the peer has successfully authenticated on this connection.</summary>
    public bool IsAuthenticated => _authenticated;

    /// <inheritdoc />
    public override ValueTask<JsonRpcDispatchDecision> OnRequestAsync(
        JsonRpcRequestContext context,
        CancellationToken cancellationToken)
    {
        if (string.Equals(context.Method, _pingMethodName, StringComparison.Ordinal))
        {
            return new ValueTask<JsonRpcDispatchDecision>(JsonRpcDispatchDecision.Respond(true));
        }

        if (string.Equals(context.Method, _authenticateMethodName, StringComparison.Ordinal))
        {
            return new ValueTask<JsonRpcDispatchDecision>(Authenticate(context.Parameters));
        }

        return _authenticated
            ? new ValueTask<JsonRpcDispatchDecision>(JsonRpcDispatchDecision.Proceed)
            : new ValueTask<JsonRpcDispatchDecision>(JsonRpcDispatchDecision.Reject(
                AuthenticationRequiredErrorCode,
                "Authentication is required before invoking this method."));
    }

    private JsonRpcDispatchDecision Authenticate(JsonElement? parameters)
    {
        byte[]? candidate = ExtractToken(parameters);
        if (candidate is null)
        {
            return JsonRpcDispatchDecision.Reject(
                AuthenticationFailedErrorCode,
                "A token is required to authenticate.");
        }

        bool valid;
        try
        {
            valid = CryptographicOperations.FixedTimeEquals(candidate, _expectedToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }

        if (valid)
        {
            _authenticated = true;
            return JsonRpcDispatchDecision.Respond(true);
        }

        return JsonRpcDispatchDecision.Reject(
            AuthenticationFailedErrorCode,
            "Authentication failed.",
            closeConnection: true);
    }

    private static byte[]? ExtractToken(JsonElement? parameters)
    {
        if (parameters is not JsonElement element)
        {
            return null;
        }

        string? token = element.ValueKind switch
        {
            JsonValueKind.Array when element.GetArrayLength() > 0 => ReadString(element[0]),
            JsonValueKind.Object when element.TryGetProperty("token", out JsonElement t) => ReadString(t),
            JsonValueKind.String => element.GetString(),
            _ => null,
        };

        return token is null ? null : Encoding.UTF8.GetBytes(token);
    }

    private static string? ReadString(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
