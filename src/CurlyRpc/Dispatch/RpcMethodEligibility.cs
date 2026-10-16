using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CurlyRpc;

/// <summary>
/// Decides which reflected methods may be exposed as JSON-RPC handlers. Everything a peer can invoke
/// passes through these checks, so they err on the side of exclusion.
/// </summary>
internal static class RpcMethodEligibility
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="method"/>, a public instance method of
    /// <paramref name="targetType"/>, is part of the target's own RPC surface rather than plumbing
    /// inherited from <see cref="object"/>, synthesized by the compiler, or a lifetime method.
    /// </summary>
    [RequiresUnreferencedCode("Inspects the interface map of the target type.")]
    public static bool IsExposableMember(Type targetType, MethodInfo method, bool includeInherited)
    {
        if (method.IsSpecialName || method.IsGenericMethodDefinition)
        {
            return false;
        }

        // GetBaseDefinition walks overrides back to the slot that introduced the method, so this
        // catches ToString/Equals/GetHashCode overrides as well as the System.Object methods
        // themselves (a record's ToString prints every property, secrets included).
        MethodInfo baseDefinition = method.GetBaseDefinition();
        if (baseDefinition.DeclaringType == typeof(object))
        {
            return false;
        }

        // Without inherited methods, an override of a base-declared virtual is the base type's
        // surface, not the target's.
        if (!includeInherited && baseDefinition.DeclaringType != method.DeclaringType)
        {
            return false;
        }

        // Record-synthesized members (<Clone>$, Deconstruct, Equals(T), PrintMembers) and any other
        // compiler-generated method.
        if (method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            return false;
        }

        // Lifetime and equality plumbing: letting a peer dispose the target, or probe it for
        // equality, is never part of an intended RPC surface.
        if (ImplementsInterfaceMethod(targetType, method, typeof(IDisposable))
            || ImplementsInterfaceMethod(targetType, method, typeof(IAsyncDisposable)))
        {
            return false;
        }

        foreach (Type iface in targetType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEquatable<>)
                && ImplementsInterfaceMethod(targetType, method, iface))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns a description of why <paramref name="method"/>'s signature cannot be dispatched, or
    /// <see langword="null"/> when it can.
    /// </summary>
    public static string? GetUnsupportedSignatureReason(MethodInfo method)
    {
        // An async void method reports failures to the synchronization context; on the thread pool
        // that is an unhandled exception that terminates the process, and the caller would already
        // have been sent a null result.
        if (method.ReturnType == typeof(void) && method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false))
        {
            return "it is 'async void'; return Task or ValueTask instead";
        }

        if (method.ReturnType.IsByRef || method.ReturnType.IsPointer || method.ReturnType.IsByRefLike)
        {
            return "its return type cannot be serialized";
        }

        foreach (ParameterInfo parameter in method.GetParameters())
        {
            Type type = parameter.ParameterType;
            if (type.IsByRef)
            {
                return $"parameter '{parameter.Name}' is passed by reference (ref, out or in)";
            }

            if (type.IsPointer || type.IsFunctionPointer || type.IsByRefLike)
            {
                return $"parameter '{parameter.Name}' has a type that cannot be bound from JSON";
            }
        }

        return null;
    }

    [RequiresUnreferencedCode("Inspects the interface map of the target type.")]
    private static bool ImplementsInterfaceMethod(Type targetType, MethodInfo method, Type interfaceType)
    {
        if (!interfaceType.IsAssignableFrom(targetType) || targetType.IsInterface)
        {
            return false;
        }

        InterfaceMapping map = targetType.GetInterfaceMap(interfaceType);
        foreach (MethodInfo implementation in map.TargetMethods)
        {
            if (implementation.MethodHandle == method.MethodHandle)
            {
                return true;
            }
        }

        return false;
    }
}
