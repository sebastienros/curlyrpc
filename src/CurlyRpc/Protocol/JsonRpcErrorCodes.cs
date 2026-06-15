namespace CurlyRpc;

/// <summary>
/// Standard JSON-RPC 2.0 error codes, plus the reserved server-error range.
/// See https://www.jsonrpc.org/specification#error_object.
/// </summary>
public static class JsonRpcErrorCodes
{
    /// <summary>Invalid JSON was received by the server (parse error).</summary>
    public const int ParseError = -32700;

    /// <summary>The JSON sent is not a valid Request object.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>The method does not exist or is not available.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>Invalid method parameter(s).</summary>
    public const int InvalidParams = -32602;

    /// <summary>Internal JSON-RPC error.</summary>
    public const int InternalError = -32603;

    /// <summary>The inclusive lower bound of the implementation-defined server error range.</summary>
    public const int ServerErrorRangeStart = -32099;

    /// <summary>The inclusive upper bound of the implementation-defined server error range.</summary>
    public const int ServerErrorRangeEnd = -32000;

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="code"/> falls in the reserved
    /// pre-defined error range (<c>-32768</c> to <c>-32000</c>).
    /// </summary>
    public static bool IsReserved(int code) => code is >= -32768 and <= -32000;
}
