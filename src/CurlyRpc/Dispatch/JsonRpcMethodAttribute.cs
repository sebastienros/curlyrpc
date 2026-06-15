namespace CurlyRpc;

/// <summary>
/// Customizes how a method is exposed as a JSON-RPC handler when a target object is registered with
/// <see cref="JsonRpc.AddLocalRpcTarget(object, JsonRpcTargetOptions?)"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class JsonRpcMethodAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="JsonRpcMethodAttribute"/> class.</summary>
    public JsonRpcMethodAttribute(string? name = null)
    {
        Name = name;
    }

    /// <summary>
    /// The JSON-RPC method name to expose. When <see langword="null"/>, the .NET method name is used
    /// (subject to <see cref="JsonRpcTargetOptions.MethodNameTransform"/>).
    /// </summary>
    public string? Name { get; }
}
