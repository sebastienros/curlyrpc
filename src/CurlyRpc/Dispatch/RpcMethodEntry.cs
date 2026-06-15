using System.Text.Json;

namespace CurlyRpc;

/// <summary>
/// Represents a registered local method that can be invoked in response to an inbound JSON-RPC
/// request or notification. Generated dispatchers and the reflection-based dispatcher both produce
/// implementations of this type.
/// </summary>
internal abstract class RpcMethodEntry
{
    /// <summary>The JSON-RPC method name this entry handles.</summary>
    public abstract string MethodName { get; }

    /// <summary>
    /// Invokes the method, binding <paramref name="parameters"/> (the JSON-RPC <c>params</c> value,
    /// or <see langword="null"/> when omitted) to the handler's arguments.
    /// </summary>
    /// <returns>The handler's result, or <see langword="null"/> for void/Task handlers.</returns>
    public abstract ValueTask<object?> InvokeAsync(
        JsonElement? parameters,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken);
}
