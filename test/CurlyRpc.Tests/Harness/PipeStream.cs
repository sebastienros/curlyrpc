namespace CurlyRpc.Tests.Harness;

/// <summary>
/// A minimal in-memory, one-way byte stream: bytes written on one end become readable on the same
/// instance. Reads block asynchronously until data is available or the writer is closed.
/// </summary>
internal sealed class PipeStream : Stream
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private readonly SemaphoreSlim _available = new(0);
    private ReadOnlyMemory<byte> _current;
    private bool _writerClosed;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void CloseWriter()
    {
        lock (_gate)
        {
            _writerClosed = true;
        }

        _available.Release();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_current.Length > 0)
                {
                    int n = Math.Min(buffer.Length, _current.Length);
                    _current.Span.Slice(0, n).CopyTo(buffer.Span);
                    _current = _current.Slice(n);
                    return n;
                }

                if (_chunks.Count > 0)
                {
                    _current = _chunks.Dequeue();
                    continue;
                }

                if (_writerClosed)
                {
                    return 0;
                }
            }

            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length > 0)
        {
            lock (_gate)
            {
                _chunks.Enqueue(buffer.ToArray());
            }

            _available.Release();
        }

        return ValueTask.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

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
            CloseWriter();
        }

        base.Dispose(disposing);
    }
}
