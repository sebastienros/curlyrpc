namespace CurlyRpc;

/// <summary>
/// The decision returned by a <see cref="JsonRpcInboundMiddleware"/> for an inbound message: allow it
/// to dispatch, answer it directly, or reject it (optionally closing the connection).
/// </summary>
public readonly struct JsonRpcDispatchDecision
{
    internal enum DecisionKind
    {
        Proceed,
        Respond,
        Reject,
    }

    private JsonRpcDispatchDecision(DecisionKind kind, object? result, int errorCode, string? message, object? errorData, bool closeConnection)
    {
        Kind = kind;
        Result = result;
        ErrorCode = errorCode;
        Message = message;
        ErrorData = errorData;
        CloseConnection = closeConnection;
    }

    internal DecisionKind Kind { get; }

    internal object? Result { get; }

    internal int ErrorCode { get; }

    internal string? Message { get; }

    internal object? ErrorData { get; }

    internal bool CloseConnection { get; }

    /// <summary>Allows the message to be dispatched to the registered handler.</summary>
    public static JsonRpcDispatchDecision Proceed { get; } =
        new(DecisionKind.Proceed, null, 0, null, null, false);

    /// <summary>Answers the request directly with <paramref name="result"/> without dispatching.</summary>
    public static JsonRpcDispatchDecision Respond(object? result)
        => new(DecisionKind.Respond, result, 0, null, null, false);

    /// <summary>Rejects the request with a JSON-RPC error and (optionally) closes the connection.</summary>
    public static JsonRpcDispatchDecision Reject(int errorCode, string message, object? errorData = null, bool closeConnection = false)
        => new(DecisionKind.Reject, null, errorCode, message, errorData, closeConnection);
}
