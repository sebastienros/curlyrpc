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
    private readonly bool _exposeExceptionDetails;
    private readonly TimeSpan _keepAliveInterval;
    private readonly TimeSpan _keepAliveTimeout;

    private readonly Dictionary<string, RpcMethodEntry> _methods = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, PendingCall> _pending = new();
    private readonly ConcurrentDictionary<RequestId, CancellationTokenSource> _inboundCancellations = new();
    private readonly ConcurrentDictionary<long, RpcEnumerableResult> _enumerators = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _gate = new();

    private long _nextId;
    private long _nextEnumeratorToken;
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
        _cancellationMethodName = _options.CancellationMethodName;
        _inboundMiddleware = _options.InboundMiddleware;
        _serializerOptions = ResolveSerializerOptions(_options.SerializerOptions);
        _inboundThrottle = _options.MaximumConcurrentRequests > 0
            ? new SemaphoreSlim(_options.MaximumConcurrentRequests, _options.MaximumConcurrentRequests)
            : null;
        _exposeExceptionDetails = _options.ExposeExceptionDetails;
        _keepAliveInterval = _options.KeepAliveInterval > TimeSpan.Zero ? _options.KeepAliveInterval : TimeSpan.Zero;
        _keepAliveTimeout = _options.KeepAliveTimeout > TimeSpan.Zero ? _options.KeepAliveTimeout : _keepAliveInterval;
    }

    /// <summary>
    /// Initializes a new <see cref="JsonRpc"/> over a single duplex stream using the default
    /// header-delimited (<c>Content-Length</c>) framing.
    /// </summary>
    /// <param name="stream">A readable and writable duplex stream.</param>
    /// <param name="options">Connection options, or <see langword="null"/> for defaults.</param>
    public JsonRpc(Stream stream, JsonRpcOptions? options = null)
        : this(CreateDefaultHandler(stream, options), options)
    {
    }

    private static HeaderDelimitedMessageHandler CreateDefaultHandler(Stream stream, JsonRpcOptions? options)
        => new(stream, ownsStream: false, maximumMessageSize: options?.MaximumInboundMessageSize ?? 0);

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
    [RequiresUnreferencedCode(ReflectionDispatchMessage)]
    [RequiresDynamicCode(ReflectionDispatchMessage)]
    public void AddLocalRpcMethod(string rpcMethodName, Delegate handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(rpcMethodName);
        ArgumentNullException.ThrowIfNull(handler);
        EnsureNotListening();

        _methods[rpcMethodName] = new ReflectionRpcMethod(rpcMethodName, handler.Target, handler.Method);
    }

    /// <summary>
    /// Registers all eligible public methods of <paramref name="target"/> as local JSON-RPC handlers.
    /// </summary>
    /// <param name="target">The object whose methods are exposed.</param>
    /// <param name="options">Registration options, or <see langword="null"/> for defaults.</param>
    /// <remarks>
    /// Every eligible public method becomes remotely callable. Expose only an object whose entire public
    /// surface is safe to invoke from the peer; apply <see cref="JsonRpcIgnoreAttribute"/> to methods that
    /// must not be reachable, and prefer a dedicated facade over registering a broad domain object. For
    /// untrusted peers, combine this with <see cref="JsonRpcOptions.InboundMiddleware"/> for authentication.
    /// </remarks>
    [RequiresUnreferencedCode(ReflectionDispatchMessage)]
    [RequiresDynamicCode(ReflectionDispatchMessage)]
    public void AddLocalRpcTarget(object target, JsonRpcTargetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureNotListening();
        options ??= new JsonRpcTargetOptions();

        var bindingFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        if (!options.IncludeInheritedMethods)
        {
            bindingFlags |= System.Reflection.BindingFlags.DeclaredOnly;
        }

        foreach (var method in target.GetType().GetMethods(bindingFlags))
        {
            if (method.DeclaringType == typeof(object) || method.IsSpecialName || method.IsGenericMethodDefinition)
            {
                continue;
            }

            if (method.GetCustomAttribute<JsonRpcIgnoreAttribute>() is not null)
            {
                continue;
            }

            var attribute = method.GetCustomAttribute<JsonRpcMethodAttribute>();
            string name = attribute?.Name ?? options.MethodNameTransform?.Invoke(method.Name) ?? method.Name;

            _methods[name] = new ReflectionRpcMethod(name, target, method);
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
    /// <exception cref="RemoteInvocationException">The remote peer returned a JSON-RPC error response (carries the error <c>code</c> and optional <c>data</c>).</exception>
    /// <exception cref="RemoteMethodNotFoundException">The remote peer reported that <paramref name="method"/> does not exist (error code -32601).</exception>
    /// <exception cref="ConnectionLostException">The connection was closed before a response was received.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled before the response arrived.</exception>
    public async Task<TResult?> InvokeAsync<TResult>(string method, object?[]? arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        JsonElement? @params = SerializePositionalParameters(arguments);
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
        JsonElement? @params = SerializeParameterObject(argument);
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
        JsonElement? @params = SerializePositionalParameters(arguments);
        await InvokeCoreAsync(method, @params, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a notification (a request with no response) with positional arguments.
    /// </summary>
    public Task NotifyAsync(string method, params object?[]? arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        JsonElement? @params = SerializePositionalParameters(arguments);
        return SendNotificationAsync(method, @params).AsTask();
    }

    /// <summary>
    /// Sends a notification (a request with no response) with a single by-name parameter object.
    /// </summary>
    public Task NotifyWithParameterObjectAsync(string method, object? argument = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        JsonElement? @params = SerializeParameterObject(argument);
        return SendNotificationAsync(method, @params).AsTask();
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

        _disposeCts.Cancel();

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

        _inboundThrottle?.Dispose();

        if (_options.DisposeHandlerOnDispose)
        {
            await _handler.DisposeAsync().ConfigureAwait(false);
        }

        _disposeCts.Dispose();
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
