namespace CurlyRpc;

/// <summary>
/// Options controlling how a target object's methods are registered as JSON-RPC handlers.
/// </summary>
public sealed class JsonRpcTargetOptions
{
    /// <summary>
    /// An optional transform applied to .NET method names to produce JSON-RPC method names. Applied
    /// only when a method does not specify an explicit name via <see cref="JsonRpcMethodAttribute"/>.
    /// </summary>
    public Func<string, string>? MethodNameTransform { get; set; }

    /// <summary>
    /// When <see langword="true"/>, methods declared on base types (excluding <see cref="object"/>)
    /// are also registered. Defaults to <see langword="true"/>.
    /// </summary>
    public bool IncludeInheritedMethods { get; set; } = true;
}
