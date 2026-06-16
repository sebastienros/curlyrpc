namespace CurlyRpc.Tests.Harness;

/// <summary>
/// A single duplex <see cref="Stream"/> built from two one-way <see cref="PipeStream"/> halves, so a
/// connected pair can be handed to <see cref="JsonRpc(Stream, JsonRpcOptions?)"/> exactly as a real
/// socket would be. Disposing one end closes both underlying halves, so the peer reading from the
/// shared pipe observes end-of-stream — the property a connection's stream ownership must provide.
/// </summary>
internal sealed class DuplexStream : Stream
{
    private readonly PipeStream _read;
    private readonly PipeStream _write;

    private DuplexStream(PipeStream read, PipeStream write)
    {
        _read = read;
        _write = write;
    }

    /// <summary>Creates two duplex streams wired so each end's writes are the other end's reads.</summary>
    public static (DuplexStream A, DuplexStream B) CreatePair()
    {
        var aToB = new PipeStream();
        var bToA = new PipeStream();
        return (new DuplexStream(bToA, aToB), new DuplexStream(aToB, bToA));
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _read.ReadAsync(buffer, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
        => _read.Read(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _write.WriteAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
        => _write.Write(buffer, offset, count);

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
            _read.Dispose();
            _write.Dispose();
        }

        base.Dispose(disposing);
    }
}
