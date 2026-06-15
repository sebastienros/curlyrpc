namespace CurlyRpc;

/// <summary>
/// Abstracts the framing of JSON-RPC messages over a transport. A message handler is responsible
/// for delimiting individual JSON payloads on the wire (for example, with <c>Content-Length</c>
/// headers or newline separators); it is not responsible for JSON serialization.
/// </summary>
/// <remarks>
/// <see cref="WriteMessageAsync"/> is safe to call concurrently; implementations serialize writes so
/// that a framed message is never interleaved with another. <see cref="ReadMessageAsync"/> is not
/// safe to call concurrently and is driven by a single read loop.
/// </remarks>
public interface IJsonRpcMessageHandler : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Writes a single framed message whose body is the supplied UTF-8 encoded JSON.
    /// </summary>
    /// <param name="messageJsonUtf8">The UTF-8 JSON body of one JSON-RPC message.</param>
    /// <param name="cancellationToken">
    /// Cancels waiting to begin the write. Once a frame starts being written it always runs to
    /// completion to avoid desynchronizing the stream.
    /// </param>
    ValueTask WriteMessageAsync(ReadOnlyMemory<byte> messageJsonUtf8, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the next framed message.
    /// </summary>
    /// <returns>
    /// The UTF-8 JSON body of the next message, or <see langword="null"/> when the transport has
    /// reached the end of the stream. The returned memory is owned by the handler and is only valid
    /// until the next call to <see cref="ReadMessageAsync"/>.
    /// </returns>
    ValueTask<ReadOnlyMemory<byte>?> ReadMessageAsync(CancellationToken cancellationToken);
}
