namespace CurlyRpc;

/// <summary>
/// Excludes a public method on a registered target from being exposed as a JSON-RPC handler.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class JsonRpcIgnoreAttribute : Attribute
{
}
