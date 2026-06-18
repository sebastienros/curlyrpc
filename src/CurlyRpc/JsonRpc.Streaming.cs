using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CurlyRpc;

public sealed partial class JsonRpc
{
    /// <summary>Built-in method requesting the next batch of an in-progress enumeration.</summary>
    private const string EnumeratorNextMethodName = "$/enumerator/next";

    /// <summary>Built-in method aborting an in-progress enumeration.</summary>
    private const string EnumeratorAbortMethodName = "$/enumerator/abort";

    /// <summary>The number of elements read per batch. One keeps streaming latency minimal.</summary>
    private const int EnumeratorBatchSize = 1;

    /// <summary>
    /// Invokes a remote method whose result is an <see cref="IAsyncEnumerable{T}"/> and streams its
    /// elements back as they are produced. The wire protocol (token / values / finished envelope plus
    /// <c>$/enumerator/next</c> and <c>$/enumerator/abort</c>) follows the common JSON-RPC streaming convention.
    /// </summary>
    /// <remarks>
    /// Errors are surfaced as exceptions while enumerating: a JSON-RPC error from the initial call or any
    /// subsequent <c>$/enumerator/next</c> batch throws a <see cref="RemoteInvocationException"/>, and a
    /// dropped connection throws a <see cref="ConnectionLostException"/>. Abandoning the enumeration (for
    /// example via <c>break</c>) or canceling <paramref name="cancellationToken"/> sends <c>$/enumerator/abort</c>
    /// to release the remote enumerator. A successfully produced element is never <see langword="null"/> to
    /// signal failure; <see langword="null"/> only appears when the remote sequence legitimately yields a null element.
    /// </remarks>
    /// <exception cref="RemoteInvocationException">The remote peer returned a JSON-RPC error response while starting or advancing the enumeration.</exception>
    /// <exception cref="RemoteMethodNotFoundException">The remote peer reported that <paramref name="method"/> does not exist (error code -32601).</exception>
    /// <exception cref="ConnectionLostException">The connection was closed before the enumeration completed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled before the enumeration completed.</exception>
    public async IAsyncEnumerable<T> InvokeAsyncEnumerable<T>(
        string method,
        object?[]? arguments = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);

        RawJsonValue? @params = SerializePositionalParameters(method, arguments);
        JsonElement start = await InvokeCoreAsync(method, @params, cancellationToken).ConfigureAwait(false);

        long? token = start.TryGetProperty("token", out JsonElement tokenElement)
            && tokenElement.ValueKind == JsonValueKind.Number
            && tokenElement.TryGetInt64(out long tokenValue)
                ? tokenValue
                : null;

        bool finished = ReadFinished(start);

        foreach (T value in EnumerateValues<T>(start))
        {
            yield return value;
        }

        if (finished || token is null)
        {
            yield break;
        }

        try
        {
            while (!finished)
            {
                JsonElement batch = await InvokeCoreAsync(
                    EnumeratorNextMethodName,
                    BuildTokenParameters(token.Value),
                    cancellationToken).ConfigureAwait(false);

                finished = ReadFinished(batch);

                foreach (T value in EnumerateValues<T>(batch))
                {
                    yield return value;
                }
            }
        }
        finally
        {
            if (!finished)
            {
                try
                {
                    await SendNotificationAsync(EnumeratorAbortMethodName, BuildTokenParameters(token.Value)).ConfigureAwait(false);
                }
                catch
                {
                    // Best effort cleanup; the connection may already be gone.
                }
            }
        }
    }

    private async Task SendEnumerableStartAsync(RequestId id, RpcEnumerableResult enumerable)
    {
        long token = Interlocked.Increment(ref _nextEnumeratorToken);

        List<JsonElement> values;
        bool finished;
        try
        {
            (values, finished) = await enumerable
                .ReadBatchAsync(_serializerOptions, EnumeratorBatchSize, _disposeCts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // The enumerator faulted before it was registered; dispose it so it cannot leak, then
            // let the dispatch loop translate the failure into a JSON-RPC error response.
            await enumerable.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        RawJsonValue result = BuildEnumerableEnvelope(finished ? null : token, values, finished);

        if (finished)
        {
            await enumerable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _enumerators[token] = enumerable;
        }

        await SendResultElementAsync(id, result).ConfigureAwait(false);
    }

    private async Task HandleEnumeratorNextAsync(RequestId id, JsonElement? @params, bool isNotification)
    {
        if (isNotification)
        {
            return;
        }

        if (ReadToken(@params) is not long token || !_enumerators.TryGetValue(token, out RpcEnumerableResult? enumerable))
        {
            await SendErrorAsync(id, JsonRpcErrorCodes.InvalidParams, "Unknown or completed enumeration token.").ConfigureAwait(false);
            return;
        }

        List<JsonElement> values;
        bool finished;
        try
        {
            (values, finished) = await enumerable
                .ReadBatchAsync(_serializerOptions, EnumeratorBatchSize, _disposeCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A faulted iterator (or a serialization failure) must not strand the enumerator in the
            // registry; remove and dispose it, then report the error to the consumer.
            if (_enumerators.TryRemove(token, out RpcEnumerableResult? failed))
            {
                await failed.DisposeAsync().ConfigureAwait(false);
            }

            if (ex is not OperationCanceledException)
            {
                await SendErrorAsync(id, JsonRpcErrorCodes.InternalError, ex.Message).ConfigureAwait(false);
            }

            return;
        }

        if (finished && _enumerators.TryRemove(token, out RpcEnumerableResult? completed))
        {
            await completed.DisposeAsync().ConfigureAwait(false);
        }

        RawJsonValue result = BuildEnumerableEnvelope(null, values, finished);
        await SendResultElementAsync(id, result).ConfigureAwait(false);
    }

    private async Task HandleEnumeratorAbortAsync(JsonElement? @params)
    {
        if (ReadToken(@params) is long token && _enumerators.TryRemove(token, out RpcEnumerableResult? enumerable))
        {
            await enumerable.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Task SendResultElementAsync(RequestId id, RawJsonValue result)
    {
        var wire = new JsonRpcResultWire { Id = id, Result = result };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(wire, JsonRpcWireContext.Default.JsonRpcResultWire);
        return _handler.WriteMessageAsync(bytes, CancellationToken.None).AsTask();
    }

    private IEnumerable<T> EnumerateValues<T>(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("values", out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement element in values.EnumerateArray())
        {
            yield return DeserializeValue<T>(element);
        }
    }

    private T DeserializeValue<T>(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default!;
        }

        return (T)element.Deserialize(_serializerOptions.GetTypeInfo(typeof(T)))!;
    }

    private static bool ReadFinished(JsonElement envelope)
        => envelope.TryGetProperty("finished", out JsonElement finished) && finished.ValueKind == JsonValueKind.True;

    private static long? ReadToken(JsonElement? @params)
    {
        if (@params is not { } element)
        {
            return null;
        }

        // By-name form: { "token": <n>, ... } (CurlyRpc's own consumer).
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("token", out JsonElement tokenElement)
            && tokenElement.ValueKind == JsonValueKind.Number
            && tokenElement.TryGetInt64(out long named))
        {
            return named;
        }

        // Positional form: [ <token>, <count> ] (the consumer sends the token first).
        if (element.ValueKind == JsonValueKind.Array
            && element.GetArrayLength() >= 1)
        {
            JsonElement first = element[0];
            if (first.ValueKind == JsonValueKind.Number && first.TryGetInt64(out long positional))
            {
                return positional;
            }
        }

        return null;
    }

    private static RawJsonValue BuildEnumerableEnvelope(long? token, List<JsonElement> values, bool finished)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (token is long t)
            {
                writer.WriteNumber("token", t);
            }

            writer.WritePropertyName("values");
            writer.WriteStartArray();
            foreach (JsonElement value in values)
            {
                value.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("finished", finished);
            writer.WriteEndObject();
        }

        return RawJsonValue.FromWritten(buffer.WrittenSpan);
    }

    private static RawJsonValue BuildTokenParameters(long token)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("token", token);
            writer.WriteEndObject();
        }

        return RawJsonValue.FromWritten(buffer.WrittenSpan);
    }
}
