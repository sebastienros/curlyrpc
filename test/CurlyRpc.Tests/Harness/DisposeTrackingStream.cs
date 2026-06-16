namespace CurlyRpc.Tests.Harness;

/// <summary>
/// A minimal duplex stream that records whether it was disposed, so tests can assert that a
/// connection closed (or did not close) the transport it was given. Writes are discarded; reads block
/// until the stream is disposed and then report end-of-stream, mimicking a live transport that only
/// reaches EOF when the local end is torn down.
/// </summary>
internal sealed class DisposeTrackingStream : Stream
{
    private readonly SemaphoreSlim _disposedSignal = new(0, 1);
    private int _disposed;

    /// <summary>Whether <see cref="Stream.Dispose()"/> or <see cref="Stream.DisposeAsync"/> has run.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _disposedSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        _disposedSignal.Release();
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count)
    {
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            MarkDisposed();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        MarkDisposed();
        return base.DisposeAsync();
    }

    private void MarkDisposed()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposedSignal.Release();
        }
    }
}
