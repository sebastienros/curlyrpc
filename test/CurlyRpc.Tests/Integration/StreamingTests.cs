using System.Runtime.CompilerServices;
using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class StreamingTests
{
    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    private static (JsonRpc Client, JsonRpc Server) CreatePair()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        return (new JsonRpc(h1, Options()), new JsonRpc(h2, Options()));
    }

    [TestMethod]
    public async Task InvokeAsyncEnumerable_StreamsAllElements()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("count", (int count, CancellationToken ct) => Range(count, ct));
        server.StartListening();
        client.StartListening();

        var received = new List<int>();
        await foreach (int value in client.InvokeAsyncEnumerable<int>("count", new object?[] { 5 }))
        {
            received.Add(value);
        }

        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, received);
    }

    [TestMethod]
    public async Task InvokeAsyncEnumerable_EmptySequence_YieldsNothing()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("count", (CancellationToken ct) => Range(0, ct));
        server.StartListening();
        client.StartListening();

        var received = new List<int>();
        await foreach (int value in client.InvokeAsyncEnumerable<int>("count"))
        {
            received.Add(value);
        }

        Assert.AreEqual(0, received.Count);
    }

    [TestMethod]
    public async Task InvokeAsyncEnumerable_EarlyBreak_AbortsEnumeration()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("count", (CancellationToken ct) => RangeUntilAbort(aborted, ct));
        server.StartListening();
        client.StartListening();

        var received = new List<int>();
        await foreach (int value in client.InvokeAsyncEnumerable<int>("count"))
        {
            received.Add(value);
            if (received.Count == 3)
            {
                break;
            }
        }

        Assert.AreEqual(3, received.Count);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ServerDispose_DisposesActiveEnumerators()
    {
        var (client, server) = CreatePair();
        await using var _c = client;

        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("count", (CancellationToken ct) => RangeUntilAbort(disposed, ct));
        server.StartListening();
        client.StartListening();

        await using IAsyncEnumerator<int> enumerator =
            client.InvokeAsyncEnumerable<int>("count").GetAsyncEnumerator();

        // After the first element the server holds an active, unfinished enumerator.
        Assert.IsTrue(await enumerator.MoveNextAsync());

        // Tearing down the connection must dispose the server-side enumerator; the client's
        // abort notification can never reach the now-dead peer, so the only path that runs the
        // generator's finally is the shutdown drain.
        await server.DisposeAsync();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async IAsyncEnumerable<int> Range(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return i;
        }
    }

    private static async IAsyncEnumerable<int> RangeUntilAbort(
        TaskCompletionSource aborted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            int i = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return i++;
            }
        }
        finally
        {
            aborted.TrySetResult();
        }
    }
}
