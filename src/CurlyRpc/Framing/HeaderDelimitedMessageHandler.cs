using System.Buffers;
using System.Buffers.Text;

namespace CurlyRpc;

/// <summary>
/// Frames messages using the LSP-style header convention: <c>Content-Length: N\r\n\r\n</c> followed
/// by <c>N</c> bytes of UTF-8 JSON. This is the default framing and is wire-compatible with the
/// Language Server Protocol.
/// </summary>
public sealed class HeaderDelimitedMessageHandler : StreamMessageHandler
{
    private static readonly byte[] ContentLengthName = "content-length"u8.ToArray();

    /// <summary>
    /// Initializes a new <see cref="HeaderDelimitedMessageHandler"/> over a single duplex stream.
    /// </summary>
    /// <param name="duplexStream">A readable and writable duplex stream.</param>
    /// <param name="ownsStream">When <see langword="true"/>, the stream is disposed with this handler.</param>
    /// <param name="maximumMessageSize">
    /// The maximum inbound frame body size in bytes (<c>0</c> for no limit). See
    /// <see cref="StreamMessageHandler.MaximumMessageSize"/>.
    /// </param>
    public HeaderDelimitedMessageHandler(Stream duplexStream, bool ownsStream = false, int maximumMessageSize = 0)
        : base(duplexStream, duplexStream, ownsStream)
    {
        MaximumMessageSize = maximumMessageSize;
    }

    /// <summary>
    /// Initializes a new <see cref="HeaderDelimitedMessageHandler"/> over separate send and receive streams.
    /// </summary>
    /// <param name="sendStream">The stream framed messages are written to.</param>
    /// <param name="receiveStream">The stream framed messages are read from.</param>
    /// <param name="ownsStreams">When <see langword="true"/>, the streams are disposed with this handler.</param>
    /// <param name="maximumMessageSize">
    /// The maximum inbound frame body size in bytes (<c>0</c> for no limit). See
    /// <see cref="StreamMessageHandler.MaximumMessageSize"/>.
    /// </param>
    public HeaderDelimitedMessageHandler(Stream sendStream, Stream receiveStream, bool ownsStreams = false, int maximumMessageSize = 0)
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

        int terminator = available.IndexOf("\r\n\r\n"u8);
        if (terminator < 0)
        {
            return false; // headers not fully received yet
        }

        int headerEnd = terminator + 4;
        if (!TryParseContentLength(available[..terminator], out int contentLength))
        {
            throw new InvalidDataException("JSON-RPC frame has a missing, duplicate, or invalid Content-Length header.");
        }

        // Reject an oversized declared length immediately, before buffering the body, so a peer cannot
        // force a large allocation with a tiny header.
        if (MaximumMessageSize > 0 && contentLength > MaximumMessageSize)
        {
            throw new JsonRpcMessageTooLargeException(MaximumMessageSize);
        }

        if (available.Length - headerEnd < contentLength)
        {
            return false; // body not fully received yet
        }

        bodyStart = headerEnd;
        bodyLength = contentLength;
        consumed = headerEnd + contentLength;
        return true;
    }

    /// <inheritdoc />
    protected override async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> body)
    {
        // "Content-Length: " (16) + up to 10 digits + "\r\n\r\n" (4) = 30 bytes max.
        byte[] header = ArrayPool<byte>.Shared.Rent(32);
        try
        {
            int headerLength = WriteHeader(header, body.Length);
            await SendStream.WriteAsync(header.AsMemory(0, headerLength), CancellationToken.None).ConfigureAwait(false);
            await SendStream.WriteAsync(body, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    private static int WriteHeader(Span<byte> destination, int contentLength)
    {
        ReadOnlySpan<byte> prefix = "Content-Length: "u8;
        prefix.CopyTo(destination);
        int position = prefix.Length;

        bool formatted = Utf8Formatter.TryFormat(contentLength, destination[position..], out int written);
        System.Diagnostics.Debug.Assert(formatted, "Content-Length must always format.");
        position += written;

        "\r\n\r\n"u8.CopyTo(destination[position..]);
        position += 4;

        return position;
    }

    private static bool TryParseContentLength(ReadOnlySpan<byte> headerBlock, out int contentLength)
    {
        contentLength = -1;

        while (!headerBlock.IsEmpty)
        {
            int lineEnd = headerBlock.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> line = lineEnd >= 0 ? headerBlock[..lineEnd] : headerBlock;

            if (TryParseHeaderLine(line, out int value))
            {
                if (contentLength >= 0)
                {
                    contentLength = -1; // duplicate Content-Length header
                    return false;
                }

                contentLength = value;
            }

            headerBlock = lineEnd >= 0 ? headerBlock[(lineEnd + 2)..] : default;
        }

        return contentLength >= 0;
    }

    private static bool TryParseHeaderLine(ReadOnlySpan<byte> line, out int contentLength)
    {
        contentLength = -1;

        int colon = line.IndexOf((byte)':');
        if (colon < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> name = line[..colon].Trim((byte)' ');
        if (!AsciiEqualsIgnoreCase(name, ContentLengthName))
        {
            return false;
        }

        ReadOnlySpan<byte> value = line[(colon + 1)..].Trim((byte)' ');
        return Utf8Parser.TryParse(value, out int parsed, out int bytesConsumed)
            && bytesConsumed == value.Length
            && parsed >= 0
            && (contentLength = parsed) >= 0;
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> lowerExpected)
    {
        if (value.Length != lowerExpected.Length)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            byte c = value[i];
            if (c is >= (byte)'A' and <= (byte)'Z')
            {
                c += 32; // to lower-case ASCII
            }

            if (c != lowerExpected[i])
            {
                return false;
            }
        }

        return true;
    }
}
