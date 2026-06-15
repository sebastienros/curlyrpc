namespace CurlyRpc;

/// <summary>
/// A hook invoked for every inbound JSON-RPC request and notification before it is dispatched to a
/// registered handler. Use it to implement cross-cutting concerns such as authentication, throttling,
/// or auditing. Set <see cref="JsonRpcOptions.InboundMiddleware"/> to enable it.
/// </summary>
public abstract class JsonRpcInboundMiddleware
{
    /// <summary>
    /// Inspects an inbound message and decides whether it should proceed to dispatch, be answered
    /// directly, or be rejected.
    /// </summary>
    /// <param name="context">The inbound request context.</param>
    /// <param name="cancellationToken">A token tied to the request lifetime.</param>
    /// <returns>The dispatch decision.</returns>
    public abstract ValueTask<JsonRpcDispatchDecision> OnRequestAsync(
        JsonRpcRequestContext context,
        CancellationToken cancellationToken);
}
