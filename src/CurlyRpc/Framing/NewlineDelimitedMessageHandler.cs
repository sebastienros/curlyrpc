namespace CurlyRpc;

/// <summary>
/// Frames messages with a single <c>\n</c> separator between consecutive JSON payloads
/// (newline-delimited JSON). Each JSON message must not contain a raw newline, which is always true
/// for compact <see cref="System.Text.Json"/> output. Blank lines between messages are ignored.
/// </summary>
public sealed class NewlineDelimitedMessageHandler : StreamMessageHandler
{
    private static readonly byte[] Newline = [(byte)'\n'];

    /// <summary>
    /// Initializes a new <see cref="NewlineDelimitedMessageHandler"/> over a single duplex stream.
    /// </summary>
    /// <param name="duplexStream">A readable and writable duplex stream.</param>
    /// <param name="ownsStream">When <see langword="true"/>, the stream is disposed with this handler.</param>
    /// <param name="maximumMessageSize">
    /// The maximum inbound frame body size in bytes (<c>0</c> for no limit). See
    /// <see cref="StreamMessageHandler.MaximumMessageSize"/>.
    /// </param>
    public NewlineDelimitedMessageHandler(Stream duplexStream, bool ownsStream = false, int maximumMessageSize = 0)
        : base(duplexStream, duplexStream, ownsStream)
    {
        MaximumMessageSize = maximumMessageSize;
    }

    /// <summary>
    /// Initializes a new <see cref="NewlineDelimitedMessageHandler"/> over separate send and receive streams.
    /// </summary>
    /// <param name="sendStream">The stream framed messages are written to.</param>
    /// <param name="receiveStream">The stream framed messages are read from.</param>
    /// <param name="ownsStreams">When <see langword="true"/>, the streams are disposed with this handler.</param>
    /// <param name="maximumMessageSize">
    /// The maximum inbound frame body size in bytes (<c>0</c> for no limit). See
    /// <see cref="StreamMessageHandler.MaximumMessageSize"/>.
    /// </param>
    public NewlineDelimitedMessageHandler(Stream sendStream, Stream receiveStream, bool ownsStreams = false, int maximumMessageSize = 0)
        : base(sendStream, receiveStream, ownsStreams)
    {
        MaximumMessageSize = maximumMessageSize;
    }

    /// <inheritdoc />
    protected override bool TryReadFrame(ReadOnlySpan<byte> available, out int consumed, out int bodyStart, out int bodyLength)
    {
        consumed = 0;
        bodyStart = 0;
        bodyLength = 0;

        int offset = 0;
        while (true)
        {
            int newline = available[offset..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                // No complete line remains. Keep skipping the blank lines already consumed.
                consumed = offset;
                return false;
            }

            int lineStart = offset;
            int lineLength = newline;
            int next = offset + newline + 1;

            // Tolerate CRLF separators by trimming a trailing carriage return.
            if (lineLength > 0 && available[lineStart + lineLength - 1] == (byte)'\r')
            {
                lineLength--;
            }

            if (lineLength == 0)
            {
                // Ignore blank keep-alive lines and continue scanning.
                offset = next;
                continue;
            }

            bodyStart = lineStart;
            bodyLength = lineLength;
            consumed = next;
            return true;
        }
    }

    /// <inheritdoc />
    protected override async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> body)
    {
        await SendStream.WriteAsync(body, CancellationToken.None).ConfigureAwait(false);
        await SendStream.WriteAsync(Newline, CancellationToken.None).ConfigureAwait(false);
    }
}
