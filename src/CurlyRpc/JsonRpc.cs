using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CurlyRpc;

/// <summary>
/// A full-duplex JSON-RPC 2.0 connection (peer). Each instance can both invoke methods on the remote
/// peer (<see cref="InvokeAsync{TResult}(string, object?[])"/>, <see cref="NotifyAsync(string, object?[])"/>)
/// and serve methods registered locally (<see cref="AddLocalRpcMethod(string, Delegate)"/>,
/// <see cref="AddLocalRpcTarget(object, JsonRpcTargetOptions?)"/>).
/// </summary>
/// <remarks>
/// Register all local methods before calling <see cref="StartListening"/>. After listening has
/// started the read loop runs until the transport reaches end-of-stream, the connection is disposed,
/// or a fatal error occurs; observe <see cref="Completion"/> to learn when it has stopped.
/// </remarks>
public sealed partial class JsonRpc : IDisposable, IAsyncDisposable
{
    private readonly IJsonRpcMessageHandler _handler;
    private readonly JsonRpcOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly string _cancellationMethodName;
    private readonly JsonRpcInboundMiddleware? _inboundMiddleware;
    private readonly SemaphoreSlim? _inboundThrottle;
    private readonly int _maximumPendingInboundRequests;
    private readonly long _maximumPendingInboundBytes;
    private int _pendingInboundRequests;
    private long _pendingInboundBytes;
    private readonly bool _exposeExceptionDetails;
    private readonly bool _propagateTraceContext;
    private readonly TimeSpan _keepAliveInterval;
    private readonly TimeSpan _keepAliveTimeout;

    private readonly Dictionary<string, RpcMethodEntry> _methods = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, PendingCall> _pending = new();
    private readonly ConcurrentDictionary<RequestId, CancellationTokenSource> _inboundCancellations = new();
    private readonly ConcurrentDictionary<long, RpcEnumerableResult> _enumerators = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _disposeCts = new();
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    private long _nextId;
    private long _nextEnumeratorToken;
    private readonly int _maximumActiveEnumerations;
    private int _activeEnumerations;
    private bool _listeningStarted;
    private bool _disposed;
    private int _shutdownFlag;
    private int _closeRequested;
    private Task? _readLoopTask;
    private Task? _keepAliveTask;

    /// <summary>The built-in keep-alive liveness method. Any response (including an error) proves liveness.</summary>
    private const string KeepAlivePingMethodName = "$/ping";

    /// <summary>
    /// Initializes a new <see cref="JsonRpc"/> over the supplied message handler.
    /// </summary>
    /// <param name="messageHandler">The framing handler used to read and write messages.</param>
    /// <param name="options">Connection options, or <see langword="null"/> for defaults.</param>
    public JsonRpc(IJsonRpcMessageHandler messageHandler, JsonRpcOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(messageHandler);
        _handler = messageHandler;
        _options = options ?? new JsonRpcOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaximumInboundMessageSize);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaximumConcurrentRequests);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaximumActiveEnumerations);
        _maximumActiveEnumerations = _options.MaximumActiveEnumerations;
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaximumPendingInboundRequests);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaximumPendingInboundBytes);
        _maximumPendingInboundRequests = _options.MaximumPendingInboundRequests;
        _maximumPendingInboundBytes = _options.MaximumPendingInboundBytes;
        _cancellationMethodName = _options.CancellationMethodName;
        _inboundMiddleware = _options.InboundMiddleware;
        _serializerOptions = ResolveSerializerOptions(_options.SerializerOptions);
        _inboundThrottle = _options.MaximumConcurrentRequests > 0
            ? new SemaphoreSlim(_options.MaximumConcurrentRequests, _options.MaximumConcurrentRequests)
            : null;
        _exposeExceptionDetails = _options.ExposeExceptionDetails;
        _propagateTraceContext = _options.PropagateTraceContext;
        _keepAliveInterval = _options.KeepAliveInterval > TimeSpan.Zero ? _options.KeepAliveInterval : TimeSpan.Zero;
        _keepAliveTimeout = _options.KeepAliveTimeout > TimeSpan.Zero ? _options.KeepAliveTimeout : _keepAliveInterval;
    }

    /// <summary>
    /// Initializes a new <see cref="JsonRpc"/> over a single duplex stream using the default
    /// header-delimited (<c>Content-Length</c>) framing.
    /// </summary>
    /// <param name="stream">A readable and writable duplex stream.</param>
    /// <param name="options">Connection options, or <see langword="null"/> for defaults.</param>
    /// <remarks>
    /// The connection takes ownership of <paramref name="stream"/> by default and disposes it when the
    /// connection is disposed, closing the transport so the peer observes end-of-stream. Set
    /// <see cref="JsonRpcOptions.OwnsStream"/> to <see langword="false"/> to retain ownership. This matches
    /// StreamJsonRpc's <c>JsonRpc(Stream)</c> behavior.
    /// </remarks>
    public JsonRpc(Stream stream, JsonRpcOptions? options = null)
        : this(CreateDefaultHandler(stream, options), options)
    {
    }

    private static HeaderDelimitedMessageHandler CreateDefaultHandler(Stream stream, JsonRpcOptions? options)
        => new(stream, ownsStream: options?.OwnsStream ?? true, maximumMessageSize: options?.MaximumInboundMessageSize ?? 0);

    /// <summary>
    /// A task that completes when the connection stops listening: successfully on end-of-stream or
    /// disposal, or faulted if the read loop terminates because of an error.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>The effective serializer options used by this connection.</summary>
    public JsonSerializerOptions SerializerOptions => _serializerOptions;

    /// <summary>
    /// Creates a connection over a duplex stream and immediately starts listening.
    /// </summary>
    /// <remarks>
    /// The connection takes ownership of <paramref name="stream"/> by default and disposes it when the
    /// connection is disposed; set <see cref="JsonRpcOptions.OwnsStream"/> to <see langword="false"/> to
    /// retain ownership. See <see cref="JsonRpc(Stream, JsonRpcOptions?)"/>.
    /// </remarks>
    public static JsonRpc Attach(Stream stream, JsonRpcOptions? options = null)
    {
        var rpc = new JsonRpc(stream, options);
        rpc.StartListening();
        return rpc;
    }

    /// <summary>
    /// Creates a connection over a message handler and immediately starts listening.
    /// </summary>
    public static JsonRpc Attach(IJsonRpcMessageHandler messageHandler, JsonRpcOptions? options = null)
    {
        var rpc = new JsonRpc(messageHandler, options);
        rpc.StartListening();
        return rpc;
    }

    /// <summary>
    /// Starts the background read loop. Call this once, after all local methods are registered.
    /// </summary>
    public void StartListening()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listeningStarted)
            {
                throw new InvalidOperationException("Listening has already been started.");
            }

            _listeningStarted = true;
            _readLoopTask = Task.Run(() => ReadLoopAsync(_disposeCts.Token));
            if (_keepAliveInterval > TimeSpan.Zero)
            {
                _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(_disposeCts.Token));
            }
        }
    }

    /// <summary>
    /// Registers a single local method handler bound to <paramref name="rpcMethodName"/>.
    /// </summary>
    /// <param name="rpcMethodName">The JSON-RPC method name to expose.</param>
    /// <param name="handler">
    /// The delegate to invoke. Its parameters are bound positionally from the request's <c>params</c>
    /// array; an optional trailing <see cref="CancellationToken"/> parameter receives a token that is
    /// canceled if the caller cancels the request.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A method named <paramref name="rpcMethodName"/> is already registered, or <paramref name="handler"/>
    /// cannot be dispatched (it is <c>async void</c>, or takes <c>ref</c>/<c>out</c>/<c>in</c>, pointer or
    /// ref struct parameters).
    /// </exception>
    [RequiresUnreferencedCode(ReflectionDispatchMessage)]
    [RequiresDynamicCode(ReflectionDispatchMessage)]
    public void AddLocalRpcMethod(string rpcMethodName, Delegate handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(rpcMethodName);
        ArgumentNullException.ThrowIfNull(handler);
        EnsureNotListening();

        if (RpcMethodEligibility.GetUnsupportedSignatureReason(handler.Method) is { } reason)
        {
            throw new ArgumentException($"The handler for '{rpcMethodName}' cannot be registered because {reason}.", nameof(handler));
        }

        EnsureMethodNameAvailable(rpcMethodName);
        _methods.Add(rpcMethodName, new ReflectionRpcMethod(rpcMethodName, handler.Target, handler.Method));
    }

    private void EnsureMethodNameAvailable(string rpcMethodName)
    {
        if (_methods.ContainsKey(rpcMethodName))
        {
            throw new ArgumentException(
                $"A local RPC method named '{rpcMethodName}' is already registered.", nameof(rpcMethodName));
        }
    }

    /// <summary>
    /// Registers all eligible public methods of <paramref name="target"/> as local JSON-RPC handlers.
    /// </summary>
    /// <param name="target">The object whose methods are exposed.</param>
    /// <param name="options">Registration options, or <see langword="null"/> for defaults.</param>
    /// <remarks>
    /// <para>
    /// Every eligible public method becomes remotely callable. Expose only an object whose entire public
    /// surface is safe to invoke from the peer; apply <see cref="JsonRpcIgnoreAttribute"/> to methods that
    /// must not be reachable, and prefer a dedicated facade over registering a broad domain object. For
    /// untrusted peers, combine this with <see cref="JsonRpcOptions.InboundMiddleware"/> for authentication.
    /// </para>
    /// <para>
    /// A public instance method is eligible unless it is: declared on, or an override of a method declared
    /// on, <see cref="object"/> (<c>ToString</c>, <c>Equals</c>, <c>GetHashCode</c>, ...); compiler
    /// generated (record members such as <c>&lt;Clone&gt;$</c> and <c>Deconstruct</c>); an implementation of
    /// <see cref="IDisposable.Dispose"/>, <see cref="IAsyncDisposable.DisposeAsync"/> or
    /// <see cref="IEquatable{T}.Equals(T)"/>; generic; a property or event accessor; <c>async void</c>;
    /// or takes <c>ref</c>/<c>out</c>/<c>in</c>, pointer or ref struct parameters. Methods inherited from
    /// base types are only included when <see cref="JsonRpcTargetOptions.IncludeInheritedMethods"/> is set.
    /// Applying <see cref="JsonRpcMethodAttribute"/> to an unsupported signature throws.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Two eligible methods map to the same JSON-RPC name (for example overloads, or a name already
    /// registered), or a method marked with <see cref="JsonRpcMethodAttribute"/> has an unsupported
    /// signature. Nothing from <paramref name="target"/> is registered in that case.
    /// </exception>
    [RequiresUnreferencedCode(ReflectionDispatchMessage)]
    [RequiresDynamicCode(ReflectionDispatchMessage)]
    public void AddLocalRpcTarget(object target, JsonRpcTargetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureNotListening();
        options ??= new JsonRpcTargetOptions();

        Type targetType = target.GetType();
        var bindingFlags = BindingFlags.Public | BindingFlags.Instance;
        if (!options.IncludeInheritedMethods)
        {
            bindingFlags |= BindingFlags.DeclaredOnly;
        }

        // Validate the whole target before registering anything, so a rejected target leaves no
        // partial registration behind.
        var discovered = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (var method in targetType.GetMethods(bindingFlags))
        {
            if (!RpcMethodEligibility.IsExposableMember(targetType, method, options.IncludeInheritedMethods))
            {
                continue;
            }

            if (method.GetCustomAttribute<JsonRpcIgnoreAttribute>() is not null)
            {
                continue;
            }

            var attribute = method.GetCustomAttribute<JsonRpcMethodAttribute>();
            if (RpcMethodEligibility.GetUnsupportedSignatureReason(method) is { } reason)
            {
                if (attribute is not null)
                {
                    throw new ArgumentException(
                        $"Method '{targetType.Name}.{method.Name}' is marked with [JsonRpcMethod] but cannot be exposed because {reason}.",
                        nameof(target));
                }

                continue;
            }

            string name = attribute?.Name ?? options.MethodNameTransform?.Invoke(method.Name) ?? method.Name;

            if (discovered.TryGetValue(name, out MethodInfo? existing))
            {
                string hint = existing.Name == method.Name
                    ? " Overloads cannot share a JSON-RPC name; give each a distinct name with [JsonRpcMethod(\"...\")] or exclude one with [JsonRpcIgnore]."
                    : " Give one of them a distinct name with [JsonRpcMethod(\"...\")] or exclude it with [JsonRpcIgnore].";
                throw new ArgumentException(
                    $"Methods '{targetType.Name}.{existing.Name}' and '{targetType.Name}.{method.Name}' both map to the JSON-RPC method name '{name}'.{hint}",
                    nameof(target));
            }

            if (_methods.ContainsKey(name))
            {
                throw new ArgumentException(
                    $"Method '{targetType.Name}.{method.Name}' maps to the JSON-RPC method name '{name}', which is already registered.",
                    nameof(target));
            }

            discovered.Add(name, method);
        }

        foreach (var (name, method) in discovered)
        {
            _methods.Add(name, new ReflectionRpcMethod(name, target, method));
        }
    }

    /// <summary>
    /// Invokes a remote method with positional arguments and returns its result.
    /// </summary>
    /// <returns>
    /// The deserialized result. A <see langword="null"/> value means the call <em>succeeded</em> and the
    /// remote method returned a JSON <c>null</c> result; it never indicates a failure. Remote and transport
    /// failures are surfaced as exceptions, not as <see langword="null"/>.
    /// </returns>
    /// <exception cref="RemoteInvocationException">The remote peer returned a JSON-RPC error response (carries the error <c>code</c> and optional <c>data</c>).</exception>
    /// <exception cref="RemoteMethodNotFoundException">The remote peer reported that <paramref name="method"/> does not exist (error code -32601).</exception>
    /// <exception cref="ConnectionLostException">The connection was closed before a response was received.</exception>
    public Task<TResult?> InvokeAsync<TResult>(string method, params object?[]? arguments)
        => InvokeAsync<TResult>(method, arguments, CancellationToken.None);

    /// <summary>
    /// Invokes a remote method with positional arguments and returns its result, with cancellation.
    /// </summary>
    /// <returns>
    /// The deserialized result. A <see langword="null"/> value means the call <em>succeeded</em> and the
    /// remote method returned a JSON <c>null</c> result; it never indicates a failure. Remote and transport
    /// failures are surfaced as exceptions, not as <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// Each positional argument is serialized by its <em>runtime</em> type (this untyped API has no declared
    /// parameter type to use). Under a source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>
    /// — required for Native AOT — every argument's concrete runtime type must be registered. A lazy LINQ
    /// projection (for example <c>items.Select(...)</c>), an iterator (<c>yield</c>) result, or an anonymous
    /// type compiles to a generated type that cannot be registered; materialize it to a registered type first
    /// (for example by calling <c>.ToArray()</c>). Otherwise the call throws <see cref="NotSupportedException"/>.
    /// </remarks>
    /// <exception cref="RemoteInvocationException">The remote peer returned a JSON-RPC error response (carries the error <c>code</c> and optional <c>data</c>).</exception>
    /// <exception cref="RemoteMethodNotFoundException">The remote peer reported that <paramref name="method"/> does not exist (error code -32601).</exception>
    /// <exception cref="ConnectionLostException">The connection was closed before a response was received.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled before the response arrived.</exception>
    /// <exception cref="NotSupportedException">An argument's runtime type has no JSON metadata under the configured serializer (common with lazy LINQ projections or anonymous types under a source-generated context).</exception>
    public async Task<TResult?> InvokeAsync<TResult>(string method, object?[]? arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        RawJsonValue? @params = SerializePositionalParameters(method, arguments);
        JsonElement result = await InvokeCoreAsync(method, @params, cancellationToken).ConfigureAwait(false);
        return DeserializeResult<TResult>(result);
    }

    /// <summary>
    /// Invokes a remote method with a single by-name parameter object and returns its result.
    /// </summary>
    /// <returns>
    /// The deserialized result. A <see langword="null"/> value means the call <em>succeeded</em> and the
    /// remote method returned a JSON <c>null</c> result; it never indicates a failure. Remote and transport
    /// failures are surfaced as exceptions, not as <see langword="null"/>.
    /// </returns>
    /// <exception cref="RemoteInvocationException">The remote peer returned a JSON-RPC error response (carries the error <c>code</c> and optional <c>data</c>).</exception>
    /// <exception cref="RemoteMethodNotFoundException">The remote peer reported that <paramref name="method"/> does not exist (error code -32601).</exception>
    /// <exception cref="ConnectionLostException">The connection was closed before a response was received.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled before the response arrived.</exception>
    public async Task<TResult?> InvokeWithParameterObjectAsync<TResult>(string method, object? argument = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        RawJsonValue? @params = SerializeParameterObject(method, argument);
        JsonElement result = await InvokeCoreAsync(method, @params, cancellationToken).ConfigureAwait(false);
        return DeserializeResult<TResult>(result);
    }

    /// <summary>
    /// Invokes a remote method with positional arguments, ignoring any returned result.
    /// </summary>
    /// <remarks>
    /// The remote method is still called and awaited; only its result value is discarded. A successful
    /// return means the peer completed the call without error.
    /// </remarks>
    /// <exception cref="RemoteInvocationException">The remote peer returned a JSON-RPC error response (carries the error <c>code</c> and optional <c>data</c>).</exception>
    /// <exception cref="RemoteMethodNotFoundException">The remote peer reported that <paramref name="method"/> does not exist (error code -32601).</exception>
    /// <exception cref="ConnectionLostException">The connection was closed before a response was received.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled before the response arrived.</exception>
    public async Task InvokeAsync(string method, object?[]? arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        RawJsonValue? @params = SerializePositionalParameters(method, arguments);
        await InvokeCoreAsync(method, @params, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a notification (a request with no response) with positional arguments.
    /// </summary>
    public Task NotifyAsync(string method, params object?[]? arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        RawJsonValue? @params = SerializePositionalParameters(method, arguments);
        return SendNotificationAsync(method, @params, _propagateTraceContext).AsTask();
    }

    /// <summary>
    /// Sends a notification (a request with no response) with a single by-name parameter object.
    /// </summary>
    public Task NotifyWithParameterObjectAsync(string method, object? argument = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        RawJsonValue? @params = SerializeParameterObject(method, argument);
        return SendNotificationAsync(method, @params, _propagateTraceContext).AsTask();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            // A throwing application cancellation callback must not abort disposal.
            CancelQuietly(_disposeCts);

            if (_readLoopTask is not null)
            {
                try
                {
                    await _readLoopTask.ConfigureAwait(false);
                }
                catch
                {
                    // Read loop failures are surfaced through Completion, not Dispose.
                }
            }

            if (_keepAliveTask is not null)
            {
                try
                {
                    await _keepAliveTask.ConfigureAwait(false);
                }
                catch
                {
                    // Keep-alive failures are surfaced through Completion, not Dispose.
                }
            }

            Shutdown(null);

            // Dispatches may still be unwinding and releasing the semaphore.
        }
        finally
        {
            try
            {
                if (_options.DisposeHandlerOnDispose)
                {
                    await _handler.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _disposeCts.Dispose();
            }
        }
    }

    private void EnsureNotListening()
    {
        if (_listeningStarted)
        {
            throw new InvalidOperationException("Local methods must be registered before StartListening is called.");
        }
    }

    private const string ReflectionDispatchMessage =
        "Reflection-based JSON-RPC registration is not compatible with trimming or Native AOT. Use the source generator for AOT scenarios.";

    [RequiresUnreferencedCode(ReflectionDispatchMessage)]
    [RequiresDynamicCode(ReflectionDispatchMessage)]
    private static JsonSerializerOptions CreateReflectionSerializerOptions(JsonSerializerOptions? template)
    {
        var options = template is null
            ? new JsonSerializerOptions(JsonSerializerDefaults.Web)
            : new JsonSerializerOptions(template);
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        return options;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The reflection resolver is only used when the caller supplies no TypeInfoResolver and dynamic code is supported; AOT/trimmed apps must supply a JsonSerializerContext.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Guarded by RuntimeFeature.IsDynamicCodeSupported; AOT/trimmed apps must supply a JsonSerializerContext.")]
    private static JsonSerializerOptions ResolveSerializerOptions(JsonSerializerOptions? provided)
    {
        // A caller supplying options with a TypeInfoResolver (for example a source-generated
        // JsonSerializerContext) is the AOT-safe path: use the options as-is. Otherwise we attach the
        // reflection-based resolver, which is the documented non-AOT convenience behavior.
        if (provided?.TypeInfoResolver is not null)
        {
            return provided;
        }

        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                "No JsonSerializerOptions.TypeInfoResolver was supplied. In Native AOT or trimmed " +
                "applications, provide JsonSerializerOptions backed by a JsonSerializerContext.");
        }

        JsonSerializerOptions options = CreateReflectionSerializerOptions(provided);
        options.MakeReadOnly();
        return options;
    }
}
