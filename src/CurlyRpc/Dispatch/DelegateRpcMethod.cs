using System.Text.Json;

namespace CurlyRpc;

/// <summary>
/// An AOT- and trim-safe <see cref="RpcMethodEntry"/> that binds inbound parameters using the
/// configured <see cref="JsonSerializerOptions"/> (its <c>TypeInfoResolver</c>) and invokes a
/// strongly-typed delegate directly, without reflection or runtime code generation. Instances are
/// produced by the typed <c>AddLocalRpcMethod&lt;...&gt;</c> overloads, which capture each parameter
/// and result type as a generic type argument so that all (de)serialization flows through a
/// statically-resolvable <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/>.
/// </summary>
internal sealed class DelegateRpcMethod : RpcMethodEntry
{
    // A null entry marks a CancellationToken slot that is supplied by the dispatcher rather than
    // bound from the request's params.
    private readonly Type?[] _parameterTypes;
    private readonly int _valueParameterCount;
    private readonly Func<object?[], ValueTask<object?>> _invoke;

    public DelegateRpcMethod(string methodName, Type?[] parameterTypes, Func<object?[], ValueTask<object?>> invoke)
    {
        MethodName = methodName;
        _parameterTypes = parameterTypes;
        _invoke = invoke;

        int count = 0;
        foreach (Type? type in parameterTypes)
        {
            if (type is not null)
            {
                count++;
            }
        }

        _valueParameterCount = count;
    }

    public override string MethodName { get; }

    public override ValueTask<object?> InvokeAsync(
        JsonElement? parameters,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        var args = new object?[_parameterTypes.Length];

        int valueIndex = 0;
        for (int i = 0; i < _parameterTypes.Length; i++)
        {
            Type? type = _parameterTypes[i];
            if (type is null)
            {
                args[i] = cancellationToken;
                continue;
            }

            args[i] = BindValue(type, parameters, valueIndex, serializerOptions);
            valueIndex++;
        }

        return _invoke(args);
    }

    private object? BindValue(Type type, JsonElement? parameters, int valueIndex, JsonSerializerOptions options)
    {
        if (parameters is not { } element)
        {
            throw new RpcInvalidParametersException(
                $"Missing required parameter at position {valueIndex} for method '{MethodName}'.");
        }

        try
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    if (valueIndex >= element.GetArrayLength())
                    {
                        throw new RpcInvalidParametersException(
                            $"Missing required parameter at position {valueIndex} for method '{MethodName}'.");
                    }

                    return element[valueIndex].Deserialize(options.GetTypeInfo(type));

                case JsonValueKind.Object:
                    // By-name params (JSON-RPC 2.0 §4.2). Without compile-time parameter names a typed
                    // delegate can only bind a single value parameter: a one-member object such as
                    // {"input": value} is bound to that member's value, while any other object is
                    // treated as a single request DTO. Multi-parameter by-name calls must use a
                    // positional array.
                    if (_valueParameterCount == 1)
                    {
                        JsonElement? single = TryGetSingleMember(element);
                        return (single ?? element).Deserialize(options.GetTypeInfo(type));
                    }

                    throw new RpcInvalidParametersException(
                        $"By-name parameters are not supported for method '{MethodName}' with multiple parameters; send a positional params array.");

                default:
                    // A bare JSON scalar is only meaningful when the handler takes a single value.
                    if (_valueParameterCount == 1)
                    {
                        return element.Deserialize(options.GetTypeInfo(type));
                    }

                    throw new RpcInvalidParametersException(
                        $"Method '{MethodName}' expects positional parameters.");
            }
        }
        catch (JsonException ex)
        {
            throw new RpcInvalidParametersException(
                $"Could not deserialize parameter at position {valueIndex} for method '{MethodName}'.", ex);
        }
    }

    private static JsonElement? TryGetSingleMember(JsonElement obj)
    {
        JsonElement? value = null;
        int count = 0;
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            count++;
            if (count == 1)
            {
                value = property.Value;
            }
            else
            {
                return null;
            }
        }

        return count == 1 ? value : null;
    }
}
