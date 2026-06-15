namespace CurlyRpc;

public sealed partial class JsonRpc
{
    // ---------------------------------------------------------------------------------------------
    // Strongly-typed local method registration.
    //
    // These overloads are the AOT- and trim-safe counterpart to AddLocalRpcMethod(string, Delegate).
    // Each parameter and result is captured as a generic type argument, so binding and result
    // serialization flow through JsonSerializerOptions.GetTypeInfo(typeof(T)) (resolvable from a
    // source-generated JsonSerializerContext) and the supplied delegate is invoked directly — no
    // reflection or runtime code generation. Prefer these in Native AOT projects.
    //
    // Parameter binding mirrors the reflection dispatcher: a positional params array binds by index;
    // a single-value handler also accepts a by-name object ({"name": value} or a request DTO). An
    // optional trailing CancellationToken parameter receives a token canceled if the caller cancels.
    // ---------------------------------------------------------------------------------------------

    private static T Arg<T>(object? value) => value is T typed ? typed : default!;

    private void RegisterDelegate(string rpcMethodName, Type?[] parameterTypes, Func<object?[], ValueTask<object?>> invoke)
    {
        ArgumentException.ThrowIfNullOrEmpty(rpcMethodName);
        EnsureNotListening();
        _methods[rpcMethodName] = new DelegateRpcMethod(rpcMethodName, parameterTypes, invoke);
    }

    private static readonly Type?[] NoParameters = Array.Empty<Type?>();
    private static readonly Type?[] CancellationOnly = { null };

    // ---- Handlers returning Task<TResult> -------------------------------------------------------

    /// <summary>Registers a parameterless handler that returns a result.</summary>
    public void AddLocalRpcMethod<TResult>(string rpcMethodName, Func<Task<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, NoParameters, async _ => await handler().ConfigureAwait(false));
    }

    /// <summary>Registers a parameterless handler that returns a result and observes cancellation.</summary>
    public void AddLocalRpcMethod<TResult>(string rpcMethodName, Func<CancellationToken, Task<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, CancellationOnly, async a => await handler(Arg<CancellationToken>(a[0])).ConfigureAwait(false));
    }

    /// <summary>Registers a single-parameter handler that returns a result.</summary>
    public void AddLocalRpcMethod<T1, TResult>(string rpcMethodName, Func<T1, Task<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, new Type?[] { typeof(T1) }, async a => await handler(Arg<T1>(a[0])).ConfigureAwait(false));
    }

    /// <summary>Registers a single-parameter handler that returns a result and observes cancellation.</summary>
    public void AddLocalRpcMethod<T1, TResult>(string rpcMethodName, Func<T1, CancellationToken, Task<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, new Type?[] { typeof(T1), null }, async a => await handler(Arg<T1>(a[0]), Arg<CancellationToken>(a[1])).ConfigureAwait(false));
    }

    /// <summary>Registers a two-parameter handler that returns a result.</summary>
    public void AddLocalRpcMethod<T1, T2, TResult>(string rpcMethodName, Func<T1, T2, Task<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, new Type?[] { typeof(T1), typeof(T2) }, async a => await handler(Arg<T1>(a[0]), Arg<T2>(a[1])).ConfigureAwait(false));
    }

    /// <summary>Registers a two-parameter handler that returns a result and observes cancellation.</summary>
    public void AddLocalRpcMethod<T1, T2, TResult>(string rpcMethodName, Func<T1, T2, CancellationToken, Task<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, new Type?[] { typeof(T1), typeof(T2), null }, async a => await handler(Arg<T1>(a[0]), Arg<T2>(a[1]), Arg<CancellationToken>(a[2])).ConfigureAwait(false));
    }

    // ---- Handlers returning Task (no result) ----------------------------------------------------

    /// <summary>Registers a parameterless handler with no result.</summary>
    public void AddLocalRpcMethod(string rpcMethodName, Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, NoParameters, async _ => { await handler().ConfigureAwait(false); return null; });
    }

    /// <summary>Registers a parameterless handler with no result that observes cancellation.</summary>
    public void AddLocalRpcMethod(string rpcMethodName, Func<CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, CancellationOnly, async a => { await handler(Arg<CancellationToken>(a[0])).ConfigureAwait(false); return null; });
    }

    /// <summary>Registers a single-parameter handler with no result.</summary>
    public void AddLocalRpcMethod<T1>(string rpcMethodName, Func<T1, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, new Type?[] { typeof(T1) }, async a => { await handler(Arg<T1>(a[0])).ConfigureAwait(false); return null; });
    }

    /// <summary>Registers a single-parameter handler with no result that observes cancellation.</summary>
    public void AddLocalRpcMethod<T1>(string rpcMethodName, Func<T1, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, new Type?[] { typeof(T1), null }, async a => { await handler(Arg<T1>(a[0]), Arg<CancellationToken>(a[1])).ConfigureAwait(false); return null; });
    }

    /// <summary>Registers a two-parameter handler with no result.</summary>
    public void AddLocalRpcMethod<T1, T2>(string rpcMethodName, Func<T1, T2, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, new Type?[] { typeof(T1), typeof(T2) }, async a => { await handler(Arg<T1>(a[0]), Arg<T2>(a[1])).ConfigureAwait(false); return null; });
    }

    /// <summary>Registers a two-parameter handler with no result that observes cancellation.</summary>
    public void AddLocalRpcMethod<T1, T2>(string rpcMethodName, Func<T1, T2, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterDelegate(rpcMethodName, new Type?[] { typeof(T1), typeof(T2), null }, async a => { await handler(Arg<T1>(a[0]), Arg<T2>(a[1]), Arg<CancellationToken>(a[2])).ConfigureAwait(false); return null; });
    }
}
