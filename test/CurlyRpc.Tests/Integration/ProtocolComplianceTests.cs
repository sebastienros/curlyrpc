using System.Text;
using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class ProtocolComplianceTests
{
    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    [TestMethod]
    public async Task RequestWithExplicitNullId_ReceivesResponseWithNullId()
    {
        var pipe1 = new PipeStream();
        var pipe2 = new PipeStream();
        var serverHandler = new HeaderDelimitedMessageHandler(sendStream: pipe2, receiveStream: pipe1);
        var clientHandler = new HeaderDelimitedMessageHandler(sendStream: pipe1, receiveStream: pipe2);

        await using var server = new JsonRpc(serverHandler, Options());
        server.AddLocalRpcMethod("echo", (int x) => x);
        server.StartListening();

        // A request object with "id": null is a request (not a notification) and must be answered.
        byte[] raw = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":[5],\"id\":null}");
        await clientHandler.WriteMessageAsync(raw, CancellationToken.None);

        ReadOnlyMemory<byte>? response = await clientHandler
            .ReadMessageAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsNotNull(response);
        using var document = JsonDocument.Parse(response.Value);
        JsonElement root = document.RootElement;

        Assert.IsTrue(root.TryGetProperty("id", out JsonElement id));
        Assert.AreEqual(JsonValueKind.Null, id.ValueKind);
        Assert.AreEqual(5, root.GetProperty("result").GetInt32());
    }

    [TestMethod]
    public async Task Batch_OfRequests_ReturnsSingleArrayResponse()
    {
        var (server, clientHandler) = CreateServer();
        await using var _ = server;

        byte[] raw = Encoding.UTF8.GetBytes(
            "[{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":[1],\"id\":1}," +
            "{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":[2],\"id\":2}]");
        await clientHandler.WriteMessageAsync(raw, CancellationToken.None);

        using JsonDocument document = await ReadResponseAsync(clientHandler);
        JsonElement root = document.RootElement;

        Assert.AreEqual(JsonValueKind.Array, root.ValueKind);
        Assert.AreEqual(2, root.GetArrayLength());

        var results = new Dictionary<int, int>();
        foreach (JsonElement element in root.EnumerateArray())
        {
            results[element.GetProperty("id").GetInt32()] = element.GetProperty("result").GetInt32();
        }

        Assert.AreEqual(1, results[1]);
        Assert.AreEqual(2, results[2]);
    }

    [TestMethod]
    public async Task Batch_WithNotification_OmitsNotificationFromResponse()
    {
        var (server, clientHandler) = CreateServer();
        await using var _ = server;

        // The first element is a notification (no id) and must not appear in the batch reply.
        byte[] raw = Encoding.UTF8.GetBytes(
            "[{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":[7]}," +
            "{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":[8],\"id\":42}]");
        await clientHandler.WriteMessageAsync(raw, CancellationToken.None);

        using JsonDocument document = await ReadResponseAsync(clientHandler);
        JsonElement root = document.RootElement;

        Assert.AreEqual(JsonValueKind.Array, root.ValueKind);
        Assert.AreEqual(1, root.GetArrayLength());
        JsonElement only = root[0];
        Assert.AreEqual(42, only.GetProperty("id").GetInt32());
        Assert.AreEqual(8, only.GetProperty("result").GetInt32());
    }

    [TestMethod]
    public async Task Batch_OfOnlyNotifications_ProducesNoResponse()
    {
        var (server, clientHandler) = CreateServer();
        await using var _ = server;

        // A batch made up entirely of notifications yields nothing on the wire.
        byte[] batch = Encoding.UTF8.GetBytes(
            "[{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":[1]}," +
            "{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":[2]}]");
        await clientHandler.WriteMessageAsync(batch, CancellationToken.None);

        // Follow it with a normal request; the only frame we should read is that request's response,
        // proving the all-notification batch emitted nothing ahead of it.
        byte[] probe = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":[99],\"id\":99}");
        await clientHandler.WriteMessageAsync(probe, CancellationToken.None);

        using JsonDocument document = await ReadResponseAsync(clientHandler);
        JsonElement root = document.RootElement;

        Assert.AreEqual(JsonValueKind.Object, root.ValueKind);
        Assert.AreEqual(99, root.GetProperty("id").GetInt32());
        Assert.AreEqual(99, root.GetProperty("result").GetInt32());
    }

    [TestMethod]
    public async Task Batch_Empty_ReturnsSingleInvalidRequestError()
    {
        var (server, clientHandler) = CreateServer();
        await using var _ = server;

        await clientHandler.WriteMessageAsync(Encoding.UTF8.GetBytes("[]"), CancellationToken.None);

        using JsonDocument document = await ReadResponseAsync(clientHandler);
        JsonElement root = document.RootElement;

        // Per JSON-RPC 2.0 §6 an empty array is answered with a single (non-array) error object.
        Assert.AreEqual(JsonValueKind.Object, root.ValueKind);
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        Assert.AreEqual(JsonRpcErrorCodes.InvalidRequest, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task Batch_WithInvalidElement_ReturnsInvalidRequestForThatElement()
    {
        var (server, clientHandler) = CreateServer();
        await using var _ = server;

        // A primitive element is an Invalid Request and gets its own error object (id: null).
        byte[] raw = Encoding.UTF8.GetBytes(
            "[1,{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":[5],\"id\":5}]");
        await clientHandler.WriteMessageAsync(raw, CancellationToken.None);

        using JsonDocument document = await ReadResponseAsync(clientHandler);
        JsonElement root = document.RootElement;

        Assert.AreEqual(JsonValueKind.Array, root.ValueKind);
        Assert.AreEqual(2, root.GetArrayLength());

        bool sawError = false;
        bool sawResult = false;
        foreach (JsonElement element in root.EnumerateArray())
        {
            if (element.TryGetProperty("error", out JsonElement error))
            {
                Assert.AreEqual(JsonValueKind.Null, element.GetProperty("id").ValueKind);
                Assert.AreEqual(JsonRpcErrorCodes.InvalidRequest, error.GetProperty("code").GetInt32());
                sawError = true;
            }
            else if (element.TryGetProperty("result", out JsonElement result))
            {
                Assert.AreEqual(5, element.GetProperty("id").GetInt32());
                Assert.AreEqual(5, result.GetInt32());
                sawResult = true;
            }
        }

        Assert.IsTrue(sawError);
        Assert.IsTrue(sawResult);
    }

    [TestMethod]
    public async Task Response_MissingId_IsIgnoredAndReadLoopSurvives()
    {
        var (client, peer) = CreateClient();
        await using var _ = client;

        Task<int> call = client.InvokeAsync<int>("compute", 1);

        using JsonDocument request = await ReadResponseAsync(peer);
        int id = request.RootElement.GetProperty("id").GetInt32();

        // A result message with no "id" cannot be correlated and must be ignored gracefully — it must
        // not fault the read loop.
        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"result\":7}"), CancellationToken.None);

        // The correctly-correlated response that follows still completes the call.
        await peer.WriteMessageAsync(
            Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"result\":42,\"id\":{id}}}"),
            CancellationToken.None);

        int result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(42, result);
        Assert.IsFalse(client.Completion.IsFaulted);
    }

    [TestMethod]
    public async Task Response_WithNeitherResultNorError_CompletesWithDefault()
    {
        var (client, peer) = CreateClient();
        await using var _ = client;

        Task<int> call = client.InvokeAsync<int>("compute", 1);

        using JsonDocument request = await ReadResponseAsync(peer);
        int id = request.RootElement.GetProperty("id").GetInt32();

        // A response object carrying neither "result" nor "error" must resolve to the default value
        // rather than hang or throw.
        await peer.WriteMessageAsync(
            Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":{id}}}"),
            CancellationToken.None);

        int result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public async Task CancellingCall_SurfacesCancellation_NotConnectionLost()
    {
        var (client, peer) = CreateClient();

        using var cts = new CancellationTokenSource();
        Task<int> call = client.InvokeAsync<int>("block", new object?[] { 1 }, cts.Token);

        // Drain the outbound request so it is genuinely in-flight before we cancel.
        using JsonDocument request = await ReadResponseAsync(peer);
        Assert.AreEqual("block", request.RootElement.GetProperty("method").GetString());

        cts.Cancel();

        // The canceled call must observe cancellation.
        Exception? captured = null;
        try
        {
            await call;
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        Assert.IsInstanceOfType(captured, typeof(OperationCanceledException));
        Assert.IsNotInstanceOfType(captured, typeof(ConnectionLostException));

        // Disposing now closes the connection, but because the call was already settled as canceled it
        // must NOT be re-surfaced as ConnectionLostException.
        await client.DisposeAsync();
        Assert.IsTrue(call.IsCanceled);
    }

    [TestMethod]
    public async Task EnumeratorNext_AcceptsPositionalToken()
    {
        // An interoperating IAsyncEnumerable consumer may drive $/enumerator/next with a POSITIONAL
        // params array ([token, count]); CurlyRpc's own consumer uses a by-name { token } object.
        // The server must accept both so it can stream results to either client shape.
        var pipe1 = new PipeStream();
        var pipe2 = new PipeStream();
        var serverHandler = new HeaderDelimitedMessageHandler(sendStream: pipe2, receiveStream: pipe1);
        var clientHandler = new HeaderDelimitedMessageHandler(sendStream: pipe1, receiveStream: pipe2);

        await using var server = new JsonRpc(serverHandler, Options());
        server.AddLocalRpcMethod("count", (int n) => Count(n));
        server.StartListening();

        byte[] start = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"count\",\"params\":[3],\"id\":1}");
        await clientHandler.WriteMessageAsync(start, CancellationToken.None);

        using JsonDocument startDoc = await ReadResponseAsync(clientHandler);
        JsonElement startResult = startDoc.RootElement.GetProperty("result");
        long token = startResult.GetProperty("token").GetInt64();
        Assert.IsFalse(startResult.GetProperty("finished").GetBoolean());

        var values = new List<int>();
        foreach (JsonElement v in startResult.GetProperty("values").EnumerateArray())
        {
            values.Add(v.GetInt32());
        }

        bool finished = false;
        int id = 2;
        while (!finished)
        {
            byte[] next = Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"method\":\"$/enumerator/next\",\"params\":[" + token + ",10],\"id\":" + id + "}");
            await clientHandler.WriteMessageAsync(next, CancellationToken.None);

            using JsonDocument doc = await ReadResponseAsync(clientHandler);
            JsonElement result = doc.RootElement.GetProperty("result");
            foreach (JsonElement v in result.GetProperty("values").EnumerateArray())
            {
                values.Add(v.GetInt32());
            }

            finished = result.GetProperty("finished").GetBoolean();
            id++;
        }

        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, values.ToArray());
    }

    private static async IAsyncEnumerable<int> Count(int n)
    {
        for (int i = 0; i < n; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }

    private static (JsonRpc Server, HeaderDelimitedMessageHandler ClientHandler) CreateServer()
    {
        var pipe1 = new PipeStream();
        var pipe2 = new PipeStream();
        var serverHandler = new HeaderDelimitedMessageHandler(sendStream: pipe2, receiveStream: pipe1);
        var clientHandler = new HeaderDelimitedMessageHandler(sendStream: pipe1, receiveStream: pipe2);

        var server = new JsonRpc(serverHandler, Options());
        server.AddLocalRpcMethod("echo", (int x) => x);
        server.StartListening();
        return (server, clientHandler);
    }

    private static (JsonRpc Client, HeaderDelimitedMessageHandler Peer) CreateClient()
    {
        var pipe1 = new PipeStream();
        var pipe2 = new PipeStream();
        var clientHandler = new HeaderDelimitedMessageHandler(sendStream: pipe1, receiveStream: pipe2);
        var peer = new HeaderDelimitedMessageHandler(sendStream: pipe2, receiveStream: pipe1);

        var client = new JsonRpc(clientHandler, Options());
        client.StartListening();
        return (client, peer);
    }

    private static async Task<JsonDocument> ReadResponseAsync(HeaderDelimitedMessageHandler handler)
    {
        ReadOnlyMemory<byte>? response = await handler
            .ReadMessageAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsNotNull(response);
        return JsonDocument.Parse(response.Value);
    }
}
