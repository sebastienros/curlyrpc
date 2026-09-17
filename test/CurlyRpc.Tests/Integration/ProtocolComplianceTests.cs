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
    [DataRow("9223372036854775808")]
    [DataRow("-9223372036854775809")]
    [DataRow("1e1000")]
    [DataRow("1.25")]
    [DataRow("1.0000000000000000000000000000000001")]
    public async Task NumericId_PreservesNumberInSuccessErrorAndBatchResponses(string id)
    {
        var (server, peer) = CreateServer();
        await using var lifetime = server;

        foreach (bool batch in new[] { false, true })
        {
            foreach (string method in new[] { "echo", "missing" })
            {
                string request = $$$"""{"jsonrpc":"2.0","method":"{{{method}}}","params":[5],"id":{{{id}}}}""";
                await peer.WriteMessageAsync(Encoding.UTF8.GetBytes(batch ? "[" + request + "]" : request), CancellationToken.None);
                using JsonDocument response = await ReadResponseAsync(peer);
                JsonElement root = batch ? response.RootElement[0] : response.RootElement;
                Assert.AreEqual(JsonValueKind.Number, root.GetProperty("id").ValueKind);
                Assert.AreEqual(id, root.GetProperty("id").GetRawText());
                Assert.IsTrue(root.TryGetProperty(method == "echo" ? "result" : "error", out _));
            }
        }
    }

    [TestMethod]
    [DataRow("9223372036854775808", "9223372036854775808")]
    [DataRow("1e1000", "1e1000")]
    [DataRow("1.25", "1.25")]
    [DataRow("9223372036854775808", "9223372036854775808.0")]
    [DataRow("1e1000", "10e999")]
    [DataRow("1.25", "125e-2")]
    public async Task Cancellation_DistinguishesNumericAndStringIds(string id, string equivalentId)
    {
        var numericStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stringStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, peer) = CreateServer(rpc =>
            rpc.AddLocalRpcMethod("block", async (int which, CancellationToken token) =>
            {
                (which == 0 ? numericStarted : stringStarted).TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return which;
            }));
        await using var lifetime = server;

        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes(
            $$$"""{"jsonrpc":"2.0","method":"block","params":[0],"id":{{{id}}}}"""), CancellationToken.None);
        await numericStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes(
            $$$"""{"jsonrpc":"2.0","method":"block","params":[1],"id":"{{{id}}}"}"""), CancellationToken.None);
        await stringStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes(
            $$$"""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":{{{equivalentId}}}}}"""), CancellationToken.None);
        using JsonDocument numericResponse = await ReadResponseAsync(peer);
        Assert.AreEqual(JsonValueKind.Number, numericResponse.RootElement.GetProperty("id").ValueKind);
        Assert.AreEqual(id, numericResponse.RootElement.GetProperty("id").GetRawText());
        Assert.AreEqual(-32800, numericResponse.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes(
            $$$"""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":"{{{id}}}"}}"""), CancellationToken.None);
        using JsonDocument stringResponse = await ReadResponseAsync(peer);
        Assert.AreEqual(JsonValueKind.String, stringResponse.RootElement.GetProperty("id").ValueKind);
        Assert.AreEqual(id, stringResponse.RootElement.GetProperty("id").GetString());
        Assert.AreEqual(-32800, stringResponse.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

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
    [DataRow("")]
    [DataRow(",\"result\":42,\"error\":{\"code\":-32603,\"message\":\"bad\"}")]
    [DataRow(",\"error\":null")]
    [DataRow(",\"error\":[]")]
    [DataRow(",\"error\":\"bad\"")]
    [DataRow(",\"error\":{}")]
    [DataRow(",\"error\":{\"code\":\"invalid\",\"message\":\"bad\"}")]
    [DataRow(",\"error\":{\"code\":null,\"message\":\"bad\"}")]
    [DataRow(",\"error\":{\"code\":true,\"message\":\"bad\"}")]
    [DataRow(",\"error\":{\"code\":{},\"message\":\"bad\"}")]
    [DataRow(",\"error\":{\"code\":[],\"message\":\"bad\"}")]
    [DataRow(",\"error\":{\"code\":1.5,\"message\":\"bad\"}")]
    [DataRow(",\"error\":{\"code\":2147483648,\"message\":\"bad\"}")]
    [DataRow(",\"error\":{\"message\":\"bad\"}")]
    [DataRow(",\"error\":{\"code\":-32603}")]
    [DataRow(",\"error\":{\"code\":-32603,\"message\":null}")]
    [DataRow(",\"error\":{\"code\":-32603,\"message\":42}")]
    public async Task Response_Malformed_FaultsCallAndReadLoopSurvives(string members)
    {
        foreach (bool batch in new[] { false, true })
        {
            var (client, peer) = CreateClient();
            await using var _ = client;

            Task<int> call = client.InvokeAsync<int>("compute", 1);
            using JsonDocument request = await ReadResponseAsync(peer);
            int id = request.RootElement.GetProperty("id").GetInt32();

            Task<int> probe = client.InvokeAsync<int>("compute", 2);
            using JsonDocument probeRequest = await ReadResponseAsync(peer);
            int probeId = probeRequest.RootElement.GetProperty("id").GetInt32();

            string malformed = $"{{\"jsonrpc\":\"2.0\",\"id\":{id}{members}}}";
            string valid = $"{{\"jsonrpc\":\"2.0\",\"id\":{probeId},\"result\":42}}";
            await peer.WriteMessageAsync(
                Encoding.UTF8.GetBytes(batch ? $"[{malformed},{valid}]" : malformed),
                CancellationToken.None);
            if (!batch)
            {
                await peer.WriteMessageAsync(Encoding.UTF8.GetBytes(valid), CancellationToken.None);
            }

            await Assert.ThrowsExactlyAsync<JsonRpcException>(
                () => call.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(call.IsFaulted);
            Assert.AreEqual(42, await probe.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(client.Completion.IsCompleted);

            await client.DisposeAsync();
            Assert.IsTrue(call.IsFaulted);
        }
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

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task Batch_WithStreamStart_ReturnsOneArrayAndOmitsNotifications(int count)
    {
        var (server, peer) = CreateServer(rpc => rpc.AddLocalRpcMethod("count", (int n) => Count(n)));
        await using var _ = server;

        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes($$$"""
            [
              {"jsonrpc":"2.0","method":"count","params":[{{{count}}}],"id":1},
              {"jsonrpc":"2.0","method":"count","params":[2]},
              {"jsonrpc":"2.0","method":"$/enumerator/next","params":{"token":-1}},
              {"jsonrpc":"2.0","method":"echo","params":[7]},
              {"jsonrpc":"2.0","method":"echo","params":[42],"id":2}
            ]
            """), CancellationToken.None);

        using JsonDocument doc = await ReadResponseAsync(peer);
        Dictionary<int, JsonElement> replies = ReadBatchReplies(doc, 2);
        JsonElement result = replies[1].GetProperty("result");
        Assert.AreEqual(count == 0, result.GetProperty("finished").GetBoolean());
        Assert.AreEqual(count == 0 ? 0 : 1, result.GetProperty("values").GetArrayLength());
        Assert.AreEqual(count != 0, result.TryGetProperty("token", out JsonElement tokenElement));
        Assert.AreEqual(42, replies[2].GetProperty("result").GetInt32());
        await AssertNoExtraResponseAsync(peer);
    }

    [TestMethod]
    public async Task Batch_WithStreamNextAndCompletion_ReturnsOneArray()
    {
        var (server, peer) = CreateServer(rpc => rpc.AddLocalRpcMethod("count", (int n) => Count(n)));
        await using var _ = server;
        long token = await StartStreamAsync(peer, "count", "[2]");

        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes($$$"""
            [
              {"jsonrpc":"2.0","method":"$/enumerator/next","params":{"token":{{{token}}}}},
              {"jsonrpc":"2.0","method":"$/enumerator/next","params":{"token":{{{token}}}},"id":1},
              {"jsonrpc":"2.0","method":"echo","params":[42],"id":2},
              {"jsonrpc":"2.0","method":"$/enumerator/next","params":[{{{token}}}],"id":3},
              {"jsonrpc":"2.0","method":"$/enumerator/next","params":{"token":{{{token}}}},"id":4}
            ]
            """), CancellationToken.None);

        using JsonDocument doc = await ReadResponseAsync(peer);
        Dictionary<int, JsonElement> replies = ReadBatchReplies(doc, 4);
        JsonElement next = replies[1].GetProperty("result");
        Assert.AreEqual(1, next.GetProperty("values")[0].GetInt32());
        Assert.IsFalse(next.GetProperty("finished").GetBoolean());
        Assert.AreEqual(42, replies[2].GetProperty("result").GetInt32());
        JsonElement completed = replies[3].GetProperty("result");
        Assert.IsTrue(completed.GetProperty("finished").GetBoolean());
        Assert.AreEqual(0, completed.GetProperty("values").GetArrayLength());
        Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, replies[4].GetProperty("error").GetProperty("code").GetInt32());
        await AssertNoExtraResponseAsync(peer);
    }

    [TestMethod]
    public async Task Batch_WithStreamFailures_ReturnsErrorsInOneArray()
    {
        var (server, peer) = CreateServer(rpc => rpc.AddLocalRpcMethod("fail", (bool immediately) => FailingStream(immediately)));
        await using var _ = server;
        long token = await StartStreamAsync(peer, "fail", "[false]");

        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes($$$"""
            [
              {"jsonrpc":"2.0","method":"fail","params":[true],"id":1},
              {"jsonrpc":"2.0","method":"$/enumerator/next","params":{"token":{{{token}}}},"id":2},
              {"jsonrpc":"2.0","method":"$/enumerator/next","params":{"token":{{{token}}}},"id":3},
              {"jsonrpc":"2.0","method":"$/enumerator/next","params":{},"id":4},
              {"jsonrpc":"2.0","method":"fail","params":[true]},
              {"jsonrpc":"2.0","method":"$/enumerator/next","params":{}},
              {"jsonrpc":"2.0","method":"echo","params":[42],"id":5}
            ]
            """), CancellationToken.None);

        using JsonDocument doc = await ReadResponseAsync(peer);
        Dictionary<int, JsonElement> replies = ReadBatchReplies(doc, 5);
        foreach (int id in new[] { 1, 2 })
        {
            Assert.AreEqual(JsonRpcErrorCodes.InternalError, replies[id].GetProperty("error").GetProperty("code").GetInt32());
        }

        foreach (int id in new[] { 3, 4 })
        {
            Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, replies[id].GetProperty("error").GetProperty("code").GetInt32());
        }

        Assert.AreEqual(42, replies[5].GetProperty("result").GetInt32());
        await AssertNoExtraResponseAsync(peer);
    }

    private static Dictionary<int, JsonElement> ReadBatchReplies(JsonDocument document, int count)
    {
        Assert.AreEqual(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.AreEqual(count, document.RootElement.GetArrayLength());
        return document.RootElement.EnumerateArray().ToDictionary(reply => reply.GetProperty("id").GetInt32());
    }

    private static async Task<long> StartStreamAsync(HeaderDelimitedMessageHandler peer, string method, string parameters)
    {
        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes($$$"""
            {"jsonrpc":"2.0","method":"{{{method}}}","params":{{{parameters}}},"id":10}
            """), CancellationToken.None);
        using JsonDocument doc = await ReadResponseAsync(peer);
        return doc.RootElement.GetProperty("result").GetProperty("token").GetInt64();
    }

    private static async Task AssertNoExtraResponseAsync(HeaderDelimitedMessageHandler peer)
    {
        // The completed batch must leave no standalone streaming replies queued before this probe.
        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes("""
            {"jsonrpc":"2.0","method":"echo","params":[99],"id":99}
            """), CancellationToken.None);
        using JsonDocument doc = await ReadResponseAsync(peer);
        Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.AreEqual(99, doc.RootElement.GetProperty("id").GetInt32());
        Assert.AreEqual(99, doc.RootElement.GetProperty("result").GetInt32());
    }

    private static async IAsyncEnumerable<int> FailingStream(bool immediately)
    {
        await Task.Yield();
        if (!immediately)
        {
            yield return 0;
        }

        throw new InvalidOperationException("Stream failed.");
    }

    private static async IAsyncEnumerable<int> Count(int n)
    {
        for (int i = 0; i < n; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }

    private static (JsonRpc Server, HeaderDelimitedMessageHandler ClientHandler) CreateServer(Action<JsonRpc>? configure = null)
    {
        var pipe1 = new PipeStream();
        var pipe2 = new PipeStream();
        var serverHandler = new HeaderDelimitedMessageHandler(sendStream: pipe2, receiveStream: pipe1);
        var clientHandler = new HeaderDelimitedMessageHandler(sendStream: pipe1, receiveStream: pipe2);

        var server = new JsonRpc(serverHandler, Options());
        server.AddLocalRpcMethod("echo", (int x) => x);
        configure?.Invoke(server);
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
