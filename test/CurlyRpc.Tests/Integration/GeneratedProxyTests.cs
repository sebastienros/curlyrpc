using System.Runtime.CompilerServices;
using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[JsonRpcProxy]
public interface ICalculator
{
    Task<int> AddAsync(int a, int b, CancellationToken cancellationToken = default);

    [JsonRpcMethod("multiply")]
    Task<int> MultiplyAsync(int a, int b);

    Task PingAsync();

    IAsyncEnumerable<int> CountAsync(int count, CancellationToken cancellationToken = default);
}

[JsonRpcProxy]
public interface IKeywordApi
{
    // Parameter and method names that are reserved C# keywords must be emitted escaped.
    [JsonRpcMethod("sum")]
    Task<int> SumAsync(int @int, int @params);

    [JsonRpcMethod("noop")]
    Task @void();
}

[TestClass]
public sealed class GeneratedProxyTests
{
    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    private static (ICalculator Proxy, JsonRpc Client, JsonRpc Server) CreatePair()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        var client = new JsonRpc(h1, Options());
        var server = new JsonRpc(h2, Options());

        server.AddLocalRpcTarget(new CalculatorService());
        server.StartListening();
        client.StartListening();

        return (client.CreateICalculatorProxy(), client, server);
    }

    [TestMethod]
    public async Task Proxy_InvokesTaskOfTMethod()
    {
        var (proxy, client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        Assert.AreEqual(7, await proxy.AddAsync(3, 4));
    }

    [TestMethod]
    public async Task Proxy_HonorsExplicitMethodName()
    {
        var (proxy, client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        Assert.AreEqual(12, await proxy.MultiplyAsync(3, 4));
    }

    [TestMethod]
    public async Task Proxy_InvokesVoidTaskMethod()
    {
        var (proxy, client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        await proxy.PingAsync();
    }

    [TestMethod]
    public async Task Proxy_StreamsAsyncEnumerable()
    {
        var (proxy, client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        var values = new List<int>();
        await foreach (int value in proxy.CountAsync(4))
        {
            values.Add(value);
        }

        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, values);
    }

    [TestMethod]
    public async Task Proxy_EscapesKeywordIdentifiers()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, Options());
        await using var server = new JsonRpc(h2, Options());

        server.AddLocalRpcTarget(new KeywordService());
        server.StartListening();
        client.StartListening();

        IKeywordApi proxy = client.CreateIKeywordApiProxy();

        Assert.AreEqual(5, await proxy.SumAsync(2, 3));
        await proxy.@void();
    }

    private sealed class KeywordService
    {
        [JsonRpcMethod("sum")]
        public int Sum(int a, int b) => a + b;

        [JsonRpcMethod("noop")]
        public Task Noop() => Task.CompletedTask;
    }

    private sealed class CalculatorService
    {
        public int AddAsync(int a, int b) => a + b;

        [JsonRpcMethod("multiply")]
        public int Multiply(int a, int b) => a * b;

        public Task PingAsync() => Task.CompletedTask;

        public async IAsyncEnumerable<int> CountAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return i;
            }
        }
    }
}
