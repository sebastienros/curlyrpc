using System.Text;
using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class RequestEnvelopeTests
{
    public static IEnumerable<object[]> MalformedEnvelopes()
    {
        string[] envelopes =
        [
            "null", "true", "42", "\"text\"",
            "{}", """{"jsonrpc":"2.0"}""",
            """{"method":"run"}""",
            """{"jsonrpc":"1.0","method":"run"}""",
            """{"jsonrpc":2.0,"method":"run"}""",
            """{"jsonrpc":null,"method":"run"}""",
            """{"jsonrpc":false,"method":"run"}""",
            """{"jsonrpc":{},"method":"run"}""",
            """{"jsonrpc":[],"method":"run"}""",
            """{"jsonrpc":"2.0","method":1}""",
            """{"jsonrpc":"2.0","method":null}""",
            """{"jsonrpc":"2.0","method":true}""",
            """{"jsonrpc":"2.0","method":{}}""",
            """{"jsonrpc":"2.0","method":[]}""",
            """{"jsonrpc":"2.0","method":"run","id":true}""",
            """{"jsonrpc":"2.0","method":"run","id":false}""",
            """{"jsonrpc":"2.0","method":"run","id":{}}""",
            """{"jsonrpc":"2.0","method":"run","id":[]}""",
            """{"jsonrpc":"2.0","method":"run","params":null}""",
            """{"jsonrpc":"2.0","method":"run","params":1}""",
            """{"jsonrpc":"2.0","method":"run","params":true}""",
            """{"jsonrpc":"2.0","method":"run","params":"text"}""",
        ];

        foreach (bool batch in new[] { false, true })
        {
            foreach (string envelope in envelopes)
            {
                yield return [envelope, batch, "null"];
                // Exercise both malformed notifications and requests carrying an identifiable id.
                if (envelope.StartsWith('{') && !envelope.Contains("\"id\"", StringComparison.Ordinal))
                {
                    string withId = envelope.Length == 2 ? "{\"id\":7}" : envelope[..^1] + ",\"id\":7}";
                    yield return [withId, batch, "7"];
                }
            }

            yield return ["""{"jsonrpc":"2.0","method":1,"id":"abc","result":0}""", batch, "\"abc\""];
            yield return ["""{"jsonrpc":"2.0","method":1,"id":null}""", batch, "null"];
        }
    }

    [TestMethod]
    [DynamicData(nameof(MalformedEnvelopes))]
    public async Task MalformedEnvelope_ReturnsInvalidRequestWithoutInvocation(string envelope, bool batch, string expectedId)
    {
        var (rpc, peer) = CreateConnection();
        await using var _ = rpc;
        int calls = 0;
        rpc.AddLocalRpcMethod("run", () => ++calls);
        rpc.StartListening();

        await WriteAsync(peer, batch ? $"[{envelope}]" : envelope);
        using JsonDocument response = await ReadAsync(peer);
        JsonElement root = response.RootElement;
        if (batch)
        {
            Assert.AreEqual(1, root.GetArrayLength());
            root = root[0];
        }

        Assert.AreEqual("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.AreEqual(expectedId, root.GetProperty("id").GetRawText());
        Assert.AreEqual(JsonRpcErrorCodes.InvalidRequest, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.AreEqual(0, calls);

        await WriteAsync(peer, """{"jsonrpc":"2.0","method":"run","id":99}""");
        using JsonDocument probe = await ReadAsync(peer);
        Assert.AreEqual(1, probe.RootElement.GetProperty("result").GetInt32());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ValidEnvelopes_PreserveIdsParameterShapesAndSilentNotifications(bool batch)
    {
        var (rpc, peer) = CreateConnection();
        await using var _ = rpc;
        int calls = 0;
        rpc.AddLocalRpcMethod("run", () => ++calls);
        rpc.AddLocalRpcMethod("echo", (int x) => x);
        rpc.StartListening();

        string[] messages =
        [
            """{"jsonrpc":"2.0","method":"run"}""",
            """{"jsonrpc":"2.0","method":"missing"}""",
            """{"jsonrpc":"2.0","method":"echo","params":["bad"]}""",
            """{"jsonrpc":"2.0","method":"run","params":[],"id":null}""",
            """{"jsonrpc":"2.0","method":"echo","params":[5],"id":"abc"}""",
            """{"jsonrpc":"2.0","method":"echo","params":{"x":6},"id":7}""",
        ];
        if (batch)
        {
            await WriteAsync(peer, "[" + string.Join(',', messages) + "]");
        }
        else
        {
            foreach (string message in messages)
            {
                await WriteAsync(peer, message);
            }
        }

        string[] ids = ["null", "\"abc\"", "7"];
        int[] results = [2, 5, 6];
        if (batch)
        {
            using JsonDocument response = await ReadAsync(peer);
            Assert.AreEqual(3, response.RootElement.GetArrayLength());
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(ids[i], response.RootElement[i].GetProperty("id").GetRawText());
                Assert.AreEqual(results[i], response.RootElement[i].GetProperty("result").GetInt32());
            }
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                using JsonDocument response = await ReadAsync(peer);
                Assert.AreEqual(ids[i], response.RootElement.GetProperty("id").GetRawText());
                Assert.AreEqual(results[i], response.RootElement.GetProperty("result").GetInt32());
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ResponseCandidates_DoNotProduceInvalidRequestReplies(bool batch)
    {
        var (rpc, peer) = CreateConnection();
        await using var _ = rpc;
        rpc.AddLocalRpcMethod("run", () => 7);
        rpc.StartListening();
        Task<int> call = rpc.InvokeAsync<int>("remote");
        using JsonDocument request = await ReadAsync(peer);
        string id = request.RootElement.GetProperty("id").GetRawText();
        string[] messages =
        [
            """{"jsonrpc":"2.0","result":0}""",
            """{"jsonrpc":"2.0","error":{"code":-32600,"message":"Invalid Request"}}""",
            """{"jsonrpc":"2.0","result":0,"id":999}""",
            $$"""{"jsonrpc":"2.0","result":42,"id":{{id}}}""",
        ];
        if (batch)
        {
            await WriteAsync(peer, "[" + string.Join(',', messages) + "]");
        }
        else
        {
            foreach (string message in messages)
            {
                await WriteAsync(peer, message);
            }
        }

        Assert.AreEqual(42, await call.WaitAsync(TimeSpan.FromSeconds(5)));
        await WriteAsync(peer, """{"jsonrpc":"2.0","method":"run","id":99}""");
        using JsonDocument probe = await ReadAsync(peer);
        Assert.AreEqual(99, probe.RootElement.GetProperty("id").GetInt32());
        Assert.AreEqual(7, probe.RootElement.GetProperty("result").GetInt32());
    }

    private static (JsonRpc Rpc, HeaderDelimitedMessageHandler Peer) CreateConnection()
    {
        var input = new PipeStream();
        var output = new PipeStream();
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(output, input),
            new JsonRpcOptions { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) });
        return (rpc, new HeaderDelimitedMessageHandler(input, output));
    }

    private static ValueTask WriteAsync(HeaderDelimitedMessageHandler peer, string message)
        => peer.WriteMessageAsync(Encoding.UTF8.GetBytes(message), CancellationToken.None);

    private static async Task<JsonDocument> ReadAsync(HeaderDelimitedMessageHandler peer)
    {
        ReadOnlyMemory<byte>? response = await peer.ReadMessageAsync(CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(response);
        return JsonDocument.Parse(response.Value);
    }
}
