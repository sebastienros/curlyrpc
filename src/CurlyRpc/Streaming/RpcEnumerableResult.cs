using System.Buffers;
using System.Text.Json;

namespace CurlyRpc;

/// <summary>
/// Non-generic handle to a server-side <see cref="IAsyncEnumerable{T}"/> result that is being
/// streamed to the remote consumer in batches. The reflection dispatcher and the source generator
/// both produce a <see cref="RpcEnumerableResult{T}"/> wrapping the handler's enumerable.
/// </summary>
internal abstract class RpcEnumerableResult
{
    private readonly SemaphoreSlim _readGate = new(1, 1);

    /// <summary>
    /// Reads up to <paramref name="batchSize"/> elements, serialized with the connection options.
    /// Reads are serialized per enumerator because <see cref="IAsyncEnumerator{T}"/> is not
    /// thread-safe: two concurrent <c>$/enumerator/next</c> requests for the same token must not
    /// drive <c>MoveNextAsync</c> simultaneously.
    /// </summary>
    public async ValueTask<(List<JsonElement> Values, bool Finished)> ReadBatchAsync(
        JsonSerializerOptions serializerOptions,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadBatchCoreAsync(serializerOptions, batchSize, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>Reads the next batch from the underlying enumerator. Callers are already serialized.</summary>
    protected abstract ValueTask<(List<JsonElement> Values, bool Finished)> ReadBatchCoreAsync(
        JsonSerializerOptions serializerOptions,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>Disposes the underlying enumerator.</summary>
    public abstract ValueTask DisposeAsync();

    /// <summary>Creates a typed result for <paramref name="elementType"/> from a runtime enumerable instance.</summary>
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Constructing a generic enumerable result requires runtime code generation.")]
    public static RpcEnumerableResult Create(Type elementType, object enumerable, CancellationToken cancellationToken)
    {
        Type closed = typeof(RpcEnumerableResult<>).MakeGenericType(elementType);
        return (RpcEnumerableResult)Activator.CreateInstance(closed, enumerable, cancellationToken)!;
    }
}

/// <summary>
/// Strongly-typed <see cref="RpcEnumerableResult"/> over an <see cref="IAsyncEnumerable{T}"/>.
/// </summary>
internal sealed class RpcEnumerableResult<T> : RpcEnumerableResult
{
    private readonly IAsyncEnumerator<T> _enumerator;

    public RpcEnumerableResult(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        _enumerator = source.GetAsyncEnumerator(cancellationToken);
    }

    protected override async ValueTask<(List<JsonElement> Values, bool Finished)> ReadBatchCoreAsync(
        JsonSerializerOptions serializerOptions,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var values = new List<JsonElement>(batchSize);
        bool finished = false;

        for (int i = 0; i < batchSize; i++)
        {
            if (!await _enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                finished = true;
                break;
            }

            values.Add(SerializeValue(_enumerator.Current, serializerOptions));
        }

        return (values, finished);
    }

    public override ValueTask DisposeAsync() => _enumerator.DisposeAsync();

    private static JsonElement SerializeValue(T value, JsonSerializerOptions serializerOptions)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, value, serializerOptions.GetTypeInfo(typeof(T)));
            }
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }
}
