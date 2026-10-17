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
    /// When <see langword="true"/>, public methods declared on base types (excluding <see cref="object"/>
    /// and the other members <see cref="JsonRpc.AddLocalRpcTarget(object, JsonRpcTargetOptions?)"/> always
    /// excludes) are also registered. Defaults to <see langword="false"/>: only methods introduced by the
    /// target's own type are exposed, so deriving from a framework or library base class does not
    /// silently publish that base class's public API to the peer.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/>, an override of a virtual method declared on a base type is treated as
    /// part of the base type's surface and is not registered either. Enable this only when every public
    /// method in the target's inheritance chain is meant to be remotely callable.
    /// </remarks>
    public bool IncludeInheritedMethods { get; set; }
}
