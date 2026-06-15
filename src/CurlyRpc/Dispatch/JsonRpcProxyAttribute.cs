namespace CurlyRpc;

/// <summary>
/// Marks an interface for which the optional <c>CurlyRpc.SourceGenerator</c> emits a typed,
/// AOT-safe client proxy. The generator produces a <c>Create&lt;InterfaceName&gt;Proxy</c> extension
/// method on <see cref="JsonRpc"/> that returns an implementation forwarding each member to the
/// remote peer.
/// </summary>
/// <remarks>
/// Interface members must return <see cref="System.Threading.Tasks.Task"/>,
/// <see cref="System.Threading.Tasks.Task{TResult}"/>, <see cref="System.Threading.Tasks.ValueTask"/>,
/// <see cref="System.Threading.Tasks.ValueTask{TResult}"/>, or
/// <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/>. A trailing
/// <see cref="System.Threading.CancellationToken"/> parameter is forwarded to the call. Apply
/// <see cref="JsonRpcMethodAttribute"/> to a member to override the wire method name.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class JsonRpcProxyAttribute : Attribute
{
}
