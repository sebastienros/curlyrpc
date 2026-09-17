using System.Text;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Framing;

[TestClass]
public sealed class NewlineDelimitedMessageHandlerTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [TestMethod]
    public async Task WritesNewlineFraming()
    {
        using var output = new MemoryStream();
        var handler = new NewlineDelimitedMessageHandler(output, Stream.Null);

        await handler.WriteMessageAsync(Utf8("{\"a\":1}"), CancellationToken.None);

        Assert.AreEqual("{\"a\":1}\n", Encoding.UTF8.GetString(output.ToArray()));
    }

    [TestMethod]
    public async Task RoundTripsMultipleMessages()
    {
        byte[] wire = Utf8("{\"a\":1}\n{\"b\":2}\n");
        var handler = new NewlineDelimitedMessageHandler(Stream.Null, new MemoryStream(wire));

        Assert.AreEqual("{\"a\":1}", await ReadStringAsync(handler));
        Assert.AreEqual("{\"b\":2}", await ReadStringAsync(handler));
        Assert.IsNull(await handler.ReadMessageAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task IgnoresBlankLinesAndToleratesCrlf()
    {
        byte[] wire = Utf8("\n{\"a\":1}\r\n\r\n{\"b\":2}\r\n");
        var handler = new NewlineDelimitedMessageHandler(Stream.Null, new MemoryStream(wire));

        Assert.AreEqual("{\"a\":1}", await ReadStringAsync(handler));
        Assert.AreEqual("{\"b\":2}", await ReadStringAsync(handler));
        Assert.IsNull(await handler.ReadMessageAsync(CancellationToken.None));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(5)]
    public async Task ParsesAcrossChunkBoundaries(int chunkSize)
    {
        byte[] wire = Utf8("{\"a\":1}\n{\"b\":2}\n");
        var handler = new NewlineDelimitedMessageHandler(Stream.Null, new ChunkedReadStream(wire, chunkSize));

        Assert.AreEqual("{\"a\":1}", await ReadStringAsync(handler));
        Assert.AreEqual("{\"b\":2}", await ReadStringAsync(handler));
    }

    [TestMethod]
    public async Task UnterminatedBodyOverLimit_ThrowsMessageTooLarge()
    {
        // A long line with no terminating newline must not buffer without bound.
        byte[] wire = Utf8(new string('x', 200));
        var handler = new NewlineDelimitedMessageHandler(Stream.Null, new ChunkedReadStream(wire, 8), maximumMessageSize: 32);

        var ex = await Assert.ThrowsExactlyAsync<JsonRpcMessageTooLargeException>(
            async () => await handler.ReadMessageAsync(CancellationToken.None));
        Assert.AreEqual(32, ex.MaximumMessageSize);
    }

    [TestMethod]
    [DataRow(100, "\n")]
    [DataRow(101, "\n")]
    [DataRow(200, "\n")]
    [DataRow(100, "\r\n")]
    [DataRow(101, "\r\n")]
    public async Task SizeLimit_IsIndependentOfReadBoundaries(int bodyLength, string separator)
    {
        string payload = "\"" + new string('x', bodyLength - 2) + "\"";
        byte[] wire = Utf8("\n\r\n" + payload + separator);
        for (int chunkSize = wire.Length; chunkSize >= 1; chunkSize--)
        {
            using var input = new ChunkedReadStream(wire, chunkSize);
            using var handler = new NewlineDelimitedMessageHandler(Stream.Null, input, maximumMessageSize: 100);
            if (bodyLength <= 100)
            {
                Assert.AreEqual(payload, await ReadStringAsync(handler), $"Chunk size: {chunkSize}");
                Assert.IsNull(await handler.ReadMessageAsync(CancellationToken.None));
            }
            else
            {
                var ex = await Assert.ThrowsExactlyAsync<JsonRpcMessageTooLargeException>(
                    async () => await handler.ReadMessageAsync(CancellationToken.None), $"Chunk size: {chunkSize}");
                Assert.AreEqual(100, ex.MaximumMessageSize);
            }
        }
    }

    [TestMethod]
    public async Task SizeLimit_AppliesToEachBufferedMessage()
    {
        using var input = new MemoryStream(Utf8("[1,2]\n[3,4]\n[5,6,7]\n"));
        using var handler = new NewlineDelimitedMessageHandler(Stream.Null, input, maximumMessageSize: 5);

        Assert.AreEqual("[1,2]", await ReadStringAsync(handler));
        Assert.AreEqual("[3,4]", await ReadStringAsync(handler));
        await Assert.ThrowsExactlyAsync<JsonRpcMessageTooLargeException>(
            async () => await handler.ReadMessageAsync(CancellationToken.None));
    }

    private static async Task<string> ReadStringAsync(IJsonRpcMessageHandler handler)
    {
        ReadOnlyMemory<byte>? message = await handler.ReadMessageAsync(CancellationToken.None);
        Assert.IsNotNull(message);
        return Encoding.UTF8.GetString(message.Value.Span);
    }
}
