using System.Text.Json;

namespace CurlyRpc;

/// <summary>
/// Configures a <see cref="JsonRpc"/> connection.
/// </summary>
public sealed class JsonRpcOptions
{
    /// <summary>
    /// The <see cref="JsonSerializerOptions"/> used to serialize method arguments and deserialize
    /// results. For Native AOT or trimming, supply options backed by a
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/>, a reflection-based default using
    /// <see cref="JsonSerializerDefaults.Web"/> (camelCase, case-insensitive) is used. That default
    /// is not compatible with Native AOT.
    /// </remarks>
    public JsonSerializerOptions? SerializerOptions { get; set; }

    /// <summary>
    /// The method name used to request cancellation of an in-flight request. Defaults to
    /// <c>$/cancelRequest</c>, matching the Language Server Protocol.
    /// </summary>
    public string CancellationMethodName { get; set; } = "$/cancelRequest";

    /// <summary>
    /// When <see langword="true"/> (the default), the connection disposes its message handler when
    /// the connection itself is disposed.
    /// </summary>
    public bool DisposeHandlerOnDispose { get; set; } = true;

    /// <summary>
    /// An optional hook invoked for every inbound request and notification before dispatch. Use it to
    /// implement authentication (see <see cref="HandshakeAuthenticationMiddleware"/>) or other
    /// cross-cutting policies.
    /// </summary>
    public JsonRpcInboundMiddleware? InboundMiddleware { get; set; }

    /// <summary>
    /// The maximum size, in bytes, of a single inbound message. A peer that declares (or streams) a
    /// larger frame faults the connection with a <see cref="JsonRpcMessageTooLargeException"/> before
    /// the body is buffered, preventing a memory-exhaustion denial of service. <c>0</c> (the default)
    /// means no limit.
    /// </summary>
    /// <remarks>
    /// This is only applied automatically when the connection creates its own message handler (the
    /// <see cref="JsonRpc(Stream, JsonRpcOptions?)"/> constructor). When supplying a custom
    /// <see cref="IJsonRpcMessageHandler"/>, set <see cref="StreamMessageHandler.MaximumMessageSize"/>
    /// (or the handler's constructor parameter) directly. Set a finite limit for any connection that
    /// can receive data from an untrusted or unauthenticated peer.
    /// </remarks>
    public int MaximumInboundMessageSize { get; set; }

    /// <summary>
    /// The maximum number of inbound requests and notifications dispatched concurrently. Additional
    /// inbound calls queue until a slot frees, bounding handler concurrency (CPU and thread-pool
    /// pressure) under a request flood. <c>0</c> (the default) means no limit.
    /// Responses to this peer's own outbound calls are never throttled.
    /// </summary>
    public int MaximumConcurrentRequests { get; set; }

    /// <summary>
    /// When <see langword="true"/> (the default), an unhandled exception thrown by a local handler is
    /// reported to the caller with the exception's <see cref="System.Exception.Message"/>. Set to
    /// <see langword="false"/> for connections exposed to untrusted peers so unexpected failures return
    /// a generic message instead, avoiding disclosure of internal details (paths, SQL, secrets) through
    /// exception text.
    /// </summary>
    /// <remarks>
    /// This only affects the generic "internal error" path. Messages from a deliberately thrown
    /// <see cref="LocalRpcException"/> (and the invalid-parameter error) are author-chosen and are
    /// always sent verbatim.
    /// </remarks>
    public bool ExposeExceptionDetails { get; set; } = true;

    /// <summary>
    /// When greater than <see cref="TimeSpan.Zero"/>, the connection periodically sends a lightweight
    /// <c>$/ping</c> request at this interval and faults the connection with a
    /// <see cref="ConnectionLostException"/> if the peer does not respond within
    /// <see cref="KeepAliveTimeout"/>. This detects a silently dropped or hung transport (for example a
    /// half-open TCP connection) that would otherwise leave outbound calls waiting indefinitely. The
    /// default (<see cref="TimeSpan.Zero"/>) disables keep-alive.
    /// </summary>
    /// <remarks>
    /// Any response — including a "method not found" error — proves the peer is alive, so the remote
    /// peer is not required to implement <c>$/ping</c>; a <see cref="JsonRpc"/> peer answers it
    /// automatically.
    /// </remarks>
    public TimeSpan KeepAliveInterval { get; set; }

    /// <summary>
    /// How long to wait for a response to a keep-alive <c>$/ping</c> before treating the peer as
    /// unresponsive. Ignored unless <see cref="KeepAliveInterval"/> is enabled. When unset or not
    /// positive, <see cref="KeepAliveInterval"/> is used as the timeout.
    /// </summary>
    public TimeSpan KeepAliveTimeout { get; set; }

    /// <summary>
    /// The <see cref="MaximumInboundMessageSize"/> applied by <see cref="CreateHardened"/>: 4 MiB.
    /// </summary>
    public const int DefaultHardenedMaximumInboundMessageSize = 4 * 1024 * 1024;

    /// <summary>
    /// Creates options pre-configured with conservative limits for a connection that can receive data
    /// from an untrusted or unauthenticated peer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor defaults are permissive — no message-size cap, no concurrency cap, exception
    /// detail exposed — so trusted transports drop in unchanged. This factory
    /// instead opts in to conservative values in one call:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><see cref="MaximumInboundMessageSize"/> = 4 MiB
    ///   (<see cref="DefaultHardenedMaximumInboundMessageSize"/>).</description></item>
    ///   <item><description><see cref="MaximumConcurrentRequests"/> = <c>Environment.ProcessorCount * 16</c>.</description></item>
    ///   <item><description><see cref="ExposeExceptionDetails"/> = <see langword="false"/>.</description></item>
    /// </list>
    /// <para>
    /// Tune the returned instance for your workload and set the remaining members you need — in
    /// particular <see cref="SerializerOptions"/> (required for Native AOT), an
    /// <see cref="InboundMiddleware"/> for authentication, and optionally
    /// <see cref="KeepAliveInterval"/>, which is left disabled because the right interval is
    /// transport-specific.
    /// </para>
    /// </remarks>
    /// <param name="serializerOptions">
    /// The serializer options for the connection, assigned to <see cref="SerializerOptions"/>. Supply
    /// options backed by a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> for
    /// Native AOT.
    /// </param>
    public static JsonRpcOptions CreateHardened(JsonSerializerOptions? serializerOptions = null) => new()
    {
        SerializerOptions = serializerOptions,
        MaximumInboundMessageSize = DefaultHardenedMaximumInboundMessageSize,
        MaximumConcurrentRequests = Environment.ProcessorCount * 16,
        ExposeExceptionDetails = false,
    };
}
