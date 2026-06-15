using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class EndToEndTests
{
    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    private static (JsonRpc Client, JsonRpc Server) CreatePair()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        return (new JsonRpc(h1, Options()), new JsonRpc(h2, Options()));
    }

    [TestMethod]
    public async Task LargePayload_RoundTripsAcrossFrames()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("echo", (string s) => s);
        server.StartListening();
        client.StartListening();

        string payload = new('x', 256 * 1024);
        string result = (await client.InvokeAsync<string>("echo", payload))!;

        Assert.AreEqual(payload.Length, result.Length);
        Assert.AreEqual(payload, result);
    }

    [TestMethod]
    public async Task ConcurrentInvocations_AllCompleteCorrectly()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("square", async (int x, CancellationToken ct) =>
        {
            await Task.Yield();
            return x * x;
        });
        server.StartListening();
        client.StartListening();

        Task<int>[] calls = Enumerable.Range(0, 100)
            .Select(i => client.InvokeAsync<int>("square", i))
            .ToArray();

        int[] results = await Task.WhenAll(calls);

        for (int i = 0; i < results.Length; i++)
        {
            Assert.AreEqual(i * i, results[i]);
        }
    }

    [TestMethod]
    public async Task ComplexTypes_RoundTrip()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("combine", (Person p) => new Person(p.Name + "!", p.Age + 1));
        server.StartListening();
        client.StartListening();

        Person result = (await client.InvokeAsync<Person>("combine", new Person("Ada", 35)))!;

        Assert.AreEqual("Ada!", result.Name);
        Assert.AreEqual(36, result.Age);
    }

    public sealed record Person(string Name, int Age);
}
