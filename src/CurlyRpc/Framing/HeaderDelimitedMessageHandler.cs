using System.Buffers.Text;
using System.Text;

namespace CurlyRpc;

/// <summary>
/// Frames messages using the LSP-style header convention: <c>Content-Length: N\r\n\r\n</c> followed
/// by <c>N</c> bytes of UTF-8 JSON. This is the default framing and is wire-compatible with the
/// Language Server Protocol.
/// </summary>
public sealed class HeaderDelimitedMessageHandler : StreamMessageHandler
{
    // The u8 literal is backed by the assembly's data section, so exposing it as a span avoids the
    // static byte[] allocation a field initializer would incur. Only ever compared via Ascii.EqualsIgnoreCase.
    private static ReadOnlySpan<byte> ContentLengthName => "content-length"u8;

    /// <summary>The default value of <see cref="MaximumHeaderSize"/>: 8 KiB.</summary>
    public const int DefaultMaximumHeaderSize = 8 * 1024;

    private int _maximumHeaderSize = DefaultMaximumHeaderSize;

    // Bytes of the pending header section already searched for the terminator, so each read only
    // scans new bytes. Reset whenever a frame is consumed.
    private int _headerScanned;

    // Once the header section of the pending frame is parsed, its end offset and declared length,
    // so a body arriving over many reads does not re-parse the headers; -1 while headers are incomplete.
    private int _pendingHeaderEnd = -1;
    private int _pendingContentLength;

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

    /// <summary>
    /// The maximum size, in bytes, of a frame's header section, including the terminating blank line.
    /// A peer that sends more header bytes faults the read loop with an <see cref="InvalidDataException"/>.
    /// Unlike <see cref="StreamMessageHandler.MaximumMessageSize"/>, this limit always applies, so an
    /// unterminated header block can never grow the read buffer without bound.
    /// Defaults to <see cref="DefaultMaximumHeaderSize"/>.
    /// </summary>
    public int MaximumHeaderSize
    {
        get => _maximumHeaderSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 32); // room for our own Content-Length header
            _maximumHeaderSize = value;
        }
    }

    /// <inheritdoc />
    protected override bool TryReadFrame(ReadOnlySpan<byte> available, out int consumed, out int bodyStart, out int bodyLength)
    {
        consumed = 0;
        bodyStart = 0;
        bodyLength = 0;

        int headerEnd = _pendingHeaderEnd;
        int contentLength = _pendingContentLength;
        if (headerEnd < 0)
        {
            // Resume where the last search stopped, backing up 3 bytes in case the terminator
            // straddles the previous read boundary.
            int searchStart = Math.Max(0, _headerScanned - 3);
            int found = available[searchStart..].IndexOf("\r\n\r\n"u8);
            if (found < 0)
            {
                if (available.Length > MaximumHeaderSize)
                {
                    throw HeaderTooLarge();
                }

                _headerScanned = available.Length;
                return false; // headers not fully received yet
            }

            int terminator = searchStart + found;
            headerEnd = terminator + 4;
            if (headerEnd > MaximumHeaderSize)
            {
                throw HeaderTooLarge();
            }

            if (!TryParseContentLength(available[..terminator], out contentLength))
            {
                throw new InvalidDataException("JSON-RPC frame has a missing, duplicate, or invalid Content-Length header.");
            }

            // Reject an oversized declared length immediately, before buffering the body, so a peer cannot
            // force a large allocation with a tiny header.
            if (MaximumMessageSize > 0 && contentLength > MaximumMessageSize)
            {
                throw new JsonRpcMessageTooLargeException(MaximumMessageSize);
            }

            _pendingHeaderEnd = headerEnd;
            _pendingContentLength = contentLength;
        }

        if (available.Length - headerEnd < contentLength)
        {
            return false; // body not fully received yet
        }

        bodyStart = headerEnd;
        bodyLength = contentLength;
        consumed = headerEnd + contentLength;
        _headerScanned = 0;
        _pendingHeaderEnd = -1;
        return true;
    }

    private InvalidDataException HeaderTooLarge()
        => new($"JSON-RPC frame header section exceeded the maximum allowed size of {MaximumHeaderSize} bytes.");

    private protected override void ValidateIncompleteFrameSize(ReadOnlySpan<byte> available)
    {
        // TryReadFrame validates the declared body length as soon as the headers are complete.
        // The buffered byte count includes headers and must not be compared with a body-size cap.
    }

    /// <inheritdoc />
    protected override async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> body)
    {
        // The header is tiny and bounded, so format it on the stack and write it synchronously
        // (no await holds the stack buffer). Writes are serialized under the base class write lock,
        // so the synchronous header and the async body stay ordered, and the base class flushes after.
        WriteHeaderToStream(body.Length);
        await SendStream.WriteAsync(body, CancellationToken.None).ConfigureAwait(false);
    }

    private void WriteHeaderToStream(int contentLength)
    {
        // "Content-Length: " (16) + up to 10 digits + "\r\n\r\n" (4) = 30 bytes max.
        Span<byte> header = stackalloc byte[32];
        int headerLength = WriteHeader(header, contentLength);
        SendStream.Write(header[..headerLength]);
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

            if (TryGetContentLengthValue(line, out ReadOnlySpan<byte> value))
            {
                if (contentLength >= 0
                    || !Utf8Parser.TryParse(value, out int parsed, out int bytesConsumed)
                    || bytesConsumed != value.Length
                    || parsed < 0)
                {
                    contentLength = -1; // duplicate or invalid Content-Length header
                    return false;
                }

                contentLength = parsed;
            }

            headerBlock = lineEnd >= 0 ? headerBlock[(lineEnd + 2)..] : default;
        }

        return contentLength >= 0;
    }

    private static bool TryGetContentLengthValue(ReadOnlySpan<byte> line, out ReadOnlySpan<byte> value)
    {
        value = default;

        int colon = line.IndexOf((byte)':');
        if (colon < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> name = line[..colon].Trim((byte)' ');
        if (!Ascii.EqualsIgnoreCase(name, ContentLengthName))
        {
            return false;
        }

        value = line[(colon + 1)..].Trim((byte)' ');
        return true;
    }
}
