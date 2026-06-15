using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;

namespace CurlyRpc;

/// <summary>
/// A <see cref="RpcMethodEntry"/> that binds inbound parameters and invokes a target method using
/// reflection. This is the default dispatcher; it is not compatible with Native AOT or trimming and
/// is therefore annotated accordingly. The source generator emits strongly-typed entries instead.
/// </summary>
[RequiresUnreferencedCode("Reflection-based JSON-RPC dispatch may require types that cannot be statically analyzed.")]
[RequiresDynamicCode("Reflection-based JSON-RPC dispatch may require runtime code generation.")]
internal sealed class ReflectionRpcMethod : RpcMethodEntry
{
    private readonly object? _target;
    private readonly MethodInfo _method;
    private readonly ParameterInfo[] _parameters;
    private readonly int _cancellationTokenIndex;
    private readonly Type? _streamElementType;

    public ReflectionRpcMethod(string methodName, object? target, MethodInfo method)
    {
        MethodName = methodName;
        _target = target;
        _method = method;
        _parameters = method.GetParameters();

        _cancellationTokenIndex = -1;
        for (int i = 0; i < _parameters.Length; i++)
        {
            if (_parameters[i].ParameterType == typeof(CancellationToken))
            {
                _cancellationTokenIndex = i;
                break;
            }
        }

        _streamElementType = GetStreamElementType(method.ReturnType);
    }

    public override string MethodName { get; }

    public override async ValueTask<object?> InvokeAsync(
        JsonElement? parameters,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        object?[] args = BindArguments(parameters, serializerOptions, cancellationToken);

        object? result;
        try
        {
            result = _method.Invoke(_target, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }

        object? value = await UnwrapResultAsync(result, _method.ReturnType).ConfigureAwait(false);

        if (value is not null && _streamElementType is not null)
        {
            return RpcEnumerableResult.Create(_streamElementType, value, cancellationToken);
        }

        return value;
    }

    private object?[] BindArguments(JsonElement? parameters, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
    {
        var args = new object?[_parameters.Length];

        // By-name params (JSON-RPC 2.0 §4.2 "by-name"): the request's params is a JSON object whose
        // member names match the handler's parameter names.
        if (parameters is { ValueKind: JsonValueKind.Object } namedParams)
        {
            BindNamedArguments(namedParams, args, serializerOptions, cancellationToken);
            return args;
        }

        // Positional binding: the JSON-RPC params array maps to the non-CancellationToken
        // parameters, in order. A missing params value is allowed only when every remaining
        // parameter is optional.
        int arrayLength = parameters is { ValueKind: JsonValueKind.Array } arr ? arr.GetArrayLength() : 0;
        int jsonIndex = 0;

        for (int i = 0; i < _parameters.Length; i++)
        {
            ParameterInfo p = _parameters[i];
            if (i == _cancellationTokenIndex)
            {
                args[i] = cancellationToken;
                continue;
            }

            if (parameters is { ValueKind: JsonValueKind.Array } array && jsonIndex < arrayLength)
            {
                JsonElement element = array[jsonIndex];
                args[i] = DeserializeElement(element, p, serializerOptions);
            }
            else if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
            }
            else
            {
                throw new RpcInvalidParametersException(
                    $"Missing required parameter '{p.Name}' for method '{MethodName}'.");
            }

            jsonIndex++;
        }

        return args;
    }

    private void BindNamedArguments(JsonElement namedParams, object?[] args, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
    {
        int valueParameterCount = _parameters.Length - (_cancellationTokenIndex >= 0 ? 1 : 0);

        for (int i = 0; i < _parameters.Length; i++)
        {
            ParameterInfo p = _parameters[i];
            if (i == _cancellationTokenIndex)
            {
                args[i] = cancellationToken;
                continue;
            }

            if (p.Name is { } name && namedParams.TryGetProperty(name, out JsonElement member))
            {
                args[i] = DeserializeElement(member, p, serializerOptions);
            }
            else if (valueParameterCount == 1)
            {
                // Single-object convenience: when the handler takes exactly one value parameter and no
                // member matches its name, deserialize the whole params object into it (the common
                // "request DTO" shape).
                args[i] = Deserialize(namedParams, p, serializerOptions);
            }
            else if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
            }
            else
            {
                throw new RpcInvalidParametersException(
                    $"Missing required parameter '{p.Name}' for method '{MethodName}'.");
            }
        }
    }

    private object? Deserialize(JsonElement? value, ParameterInfo parameter, JsonSerializerOptions serializerOptions)
    {
        if (value is not { } element || element.ValueKind == JsonValueKind.Null)
        {
            return parameter.HasDefaultValue ? parameter.DefaultValue : GetDefault(parameter.ParameterType);
        }

        return DeserializeElement(element, parameter, serializerOptions);
    }

    private object? DeserializeElement(JsonElement element, ParameterInfo parameter, JsonSerializerOptions serializerOptions)
    {
        try
        {
            return element.Deserialize(serializerOptions.GetTypeInfo(parameter.ParameterType));
        }
        catch (JsonException ex)
        {
            throw new RpcInvalidParametersException(
                $"Could not deserialize parameter '{parameter.Name}' for method '{MethodName}'.", ex);
        }
    }

    private static object? GetDefault(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static async ValueTask<object?> UnwrapResultAsync(object? result, Type returnType)
    {
        switch (result)
        {
            case null:
                return null;

            case Task task:
                await task.ConfigureAwait(false);
                if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    return GetTaskResult(task);
                }

                return null;

            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return null;
        }

        Type resultType = result.GetType();
        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = (Task)resultType.GetMethod("AsTask")!.Invoke(result, null)!;
            await asTask.ConfigureAwait(false);
            return GetTaskResult(asTask);
        }

        return result;
    }

    private static object? GetTaskResult(Task task)
        => task.GetType().GetProperty("Result")!.GetValue(task);

    private static Type? GetStreamElementType(Type returnType)
    {
        Type core = returnType;
        if (core.IsGenericType)
        {
            Type definition = core.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                core = core.GetGenericArguments()[0];
            }
        }

        if (core.IsGenericType && core.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            return core.GetGenericArguments()[0];
        }

        return null;
    }
}
