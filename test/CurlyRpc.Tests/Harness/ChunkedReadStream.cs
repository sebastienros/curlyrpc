namespace CurlyRpc.Tests.Harness;

/// <summary>
/// A read-only stream over a fixed byte array that returns at most <see cref="_chunkSize"/> bytes
/// per read. Used to simulate transports that deliver framed messages in arbitrary fragments.
/// </summary>
internal sealed class ChunkedReadStream : Stream
{
    private readonly byte[] _data;
    private readonly int _chunkSize;
    private int _position;

    public ChunkedReadStream(byte[] data, int chunkSize)
    {
        _data = data;
        _chunkSize = Math.Max(1, chunkSize);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int remaining = _data.Length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        int toCopy = Math.Min(Math.Min(count, _chunkSize), remaining);
        Array.Copy(_data, _position, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int remaining = _data.Length - _position;
        if (remaining <= 0)
        {
            return ValueTask.FromResult(0);
        }

        int toCopy = Math.Min(Math.Min(buffer.Length, _chunkSize), remaining);
        _data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
        _position += toCopy;
        return ValueTask.FromResult(toCopy);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
