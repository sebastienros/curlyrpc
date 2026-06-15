namespace CurlyRpc;

/// <summary>
/// Base class for stream-backed <see cref="IJsonRpcMessageHandler"/> implementations. Reads use a
/// single growable buffer with carry-over between frames (no per-message allocation); writes are
/// serialized so a framed message is never interleaved with another and always completes once started.
/// </summary>
/// <remarks>
/// This type deliberately avoids <c>System.IO.Pipelines</c> so the core library has zero package
/// dependencies on every target framework.
/// </remarks>
public abstract class StreamMessageHandler : IJsonRpcMessageHandler
{
    private readonly Stream _sendStream;
    private readonly Stream _receiveStream;
    private readonly bool _ownsStreams;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private byte[] _buffer = new byte[1024];
    private int _dataStart;
    private int _dataLength;
    private byte[] _frameBuffer = new byte[256];
    private bool _disposed;

    /// <summary>
    /// The maximum size, in bytes, of a single inbound frame body. When greater than zero, a frame
    /// that exceeds this limit faults the read loop with a <see cref="JsonRpcMessageTooLargeException"/>
    /// before its body is fully buffered. <c>0</c> (the default) means no limit.
    /// </summary>
    public int MaximumMessageSize { get; set; }

    /// <summary>
    /// Initializes a new <see cref="StreamMessageHandler"/>.
    /// </summary>
    /// <param name="sendStream">The stream to write framed messages to.</param>
    /// <param name="receiveStream">The stream to read framed messages from.</param>
    /// <param name="ownsStreams">
    /// When <see langword="true"/>, the streams are disposed when this handler is disposed.
    /// </param>
    protected StreamMessageHandler(Stream sendStream, Stream receiveStream, bool ownsStreams = false)
    {
        ArgumentNullException.ThrowIfNull(sendStream);
        ArgumentNullException.ThrowIfNull(receiveStream);

        _sendStream = sendStream;
        _receiveStream = receiveStream;
        _ownsStreams = ownsStreams;
    }

    /// <summary>The stream framed messages are written to.</summary>
    protected Stream SendStream => _sendStream;

    /// <inheritdoc />
    public async ValueTask WriteMessageAsync(ReadOnlyMemory<byte> messageJsonUtf8, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Cancellation only applies while waiting for the lock. Once we hold it and start emitting a
        // frame we must finish it; cancelling mid-frame would leave the peer waiting for bytes that
        // never arrive and desynchronize every subsequent message on this connection.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(messageJsonUtf8).ConfigureAwait(false);
            await _sendStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            if (_dataLength > 0)
            {
                bool found = TryReadFrame(
                    new ReadOnlySpan<byte>(_buffer, _dataStart, _dataLength),
                    out int consumed,
                    out int bodyStart,
                    out int bodyLength);

                if (found)
                {
                    Span<byte> destination = RentFrameSpan(bodyLength);
                    new ReadOnlySpan<byte>(_buffer, _dataStart + bodyStart, bodyLength).CopyTo(destination);
                    Advance(consumed);
                    return _frameBuffer.AsMemory(0, bodyLength);
                }

                // Discard any bytes the parser deemed safely skippable (e.g. blank lines).
                if (consumed > 0)
                {
                    Advance(consumed);
                }
            }

            // Guard against an unbounded in-progress frame (a peer that streams a body without a
            // terminator, or a framing without a declared length). Header framing additionally rejects
            // an oversized declared Content-Length up front in TryReadFrame.
            if (MaximumMessageSize > 0 && _dataLength > MaximumMessageSize)
            {
                throw new JsonRpcMessageTooLargeException(MaximumMessageSize);
            }

            int read = await FillAsync(cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (_dataLength == 0)
                {
                    return null; // clean end of stream
                }

                throw new EndOfStreamException("The stream ended in the middle of a JSON-RPC frame.");
            }
        }
    }

    /// <summary>
    /// Attempts to parse a single frame from the front of <paramref name="available"/>.
    /// </summary>
    /// <param name="available">The currently buffered bytes.</param>
    /// <param name="consumed">
    /// The number of leading bytes to discard. On success this is the full frame length; on failure
    /// it may be non-zero to discard skippable bytes (such as blank lines) that will never form a frame.
    /// </param>
    /// <param name="bodyStart">On success, the offset of the message body within <paramref name="available"/>.</param>
    /// <param name="bodyLength">On success, the length of the message body.</param>
    /// <returns><see langword="true"/> if a complete frame was parsed; otherwise <see langword="false"/>.</returns>
    protected abstract bool TryReadFrame(ReadOnlySpan<byte> available, out int consumed, out int bodyStart, out int bodyLength);

    /// <summary>
    /// Writes the framing and body of a single message to <see cref="SendStream"/>. Flushing is
    /// handled by the base class.
    /// </summary>
    protected abstract ValueTask WriteFrameAsync(ReadOnlyMemory<byte> body);

    /// <summary>
    /// Returns a span over the handler's reusable frame buffer with at least <paramref name="length"/>
    /// bytes, growing the buffer if necessary.
    /// </summary>
    private Span<byte> RentFrameSpan(int length)
    {
        if (_frameBuffer.Length < length)
        {
            _frameBuffer = new byte[Math.Max(length, _frameBuffer.Length * 2)];
        }

        return _frameBuffer.AsSpan(0, length);
    }

    private void Advance(int count)
    {
        _dataStart += count;
        _dataLength -= count;
        if (_dataLength == 0)
        {
            _dataStart = 0;
        }
    }

    private async ValueTask<int> FillAsync(CancellationToken cancellationToken)
    {
        if (_dataStart + _dataLength == _buffer.Length)
        {
            if (_dataStart > 0)
            {
                // Compact: move the live bytes to the front to reclaim trailing space.
                Array.Copy(_buffer, _dataStart, _buffer, 0, _dataLength);
                _dataStart = 0;
            }
            else
            {
                // The buffer is full of one in-progress frame; grow it.
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }
        }

        int offset = _dataStart + _dataLength;
        int read = await _receiveStream.ReadAsync(_buffer.AsMemory(offset, _buffer.Length - offset), cancellationToken).ConfigureAwait(false);
        _dataLength += read;
        return read;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases resources held by the handler.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            _writeLock.Dispose();
            if (_ownsStreams)
            {
                _sendStream.Dispose();
                _receiveStream.Dispose();
            }
        }
    }

    /// <summary>Asynchronously releases resources held by the handler.</summary>
    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();

        if (_ownsStreams)
        {
            await _sendStream.DisposeAsync().ConfigureAwait(false);
            await _receiveStream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
