using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

/// <summary>
/// Verifies who disposes the underlying transport stream. The Stream-taking entry points
/// (<see cref="JsonRpc(System.IO.Stream, JsonRpcOptions?)"/> and <see cref="JsonRpc.Attach(System.IO.Stream, JsonRpcOptions?)"/>)
/// own the stream by default so disposing the connection closes the transport — matching StreamJsonRpc
/// and letting a peer observe end-of-stream. Explicitly-built handlers keep caller ownership.
/// </summary>
[TestClass]
public sealed class StreamOwnershipTests
{
    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    [TestMethod]
    public async Task Dispose_OwnsStreamDefault_DisposesStream()
    {
        var stream = new DisposeTrackingStream();
        var rpc = new JsonRpc(stream, Options());
        rpc.StartListening();

        await rpc.DisposeAsync();

        Assert.IsTrue(stream.IsDisposed, "Disposing the connection should dispose the owned stream.");
    }

    [TestMethod]
    public async Task Dispose_AttachOwnsStreamDefault_DisposesStream()
    {
        var stream = new DisposeTrackingStream();
        var rpc = JsonRpc.Attach(stream, Options());

        await rpc.DisposeAsync();

        Assert.IsTrue(stream.IsDisposed, "Attach should take ownership and dispose the stream on dispose.");
    }

    [TestMethod]
    public async Task Dispose_OwnsStreamFalse_LeavesStreamOpen()
    {
        var stream = new DisposeTrackingStream();
        var options = Options();
        options.OwnsStream = false;
        var rpc = new JsonRpc(stream, options);
        rpc.StartListening();

        await rpc.DisposeAsync();

        Assert.IsFalse(stream.IsDisposed, "With OwnsStream=false the caller retains ownership of the stream.");
    }

    [TestMethod]
    public async Task Dispose_ExplicitHandlerCtor_LeavesStreamsOpen()
    {
        var stream = new DisposeTrackingStream();

        // Building the handler explicitly means the caller owns the stream (ownsStream defaults to false),
        // regardless of JsonRpcOptions.OwnsStream, which only governs the JsonRpc(Stream) convenience path.
        var handler = new HeaderDelimitedMessageHandler(stream);
        var rpc = new JsonRpc(handler, Options());
        rpc.StartListening();

        await rpc.DisposeAsync();

        Assert.IsFalse(stream.IsDisposed, "An explicitly-built handler must not dispose a caller-owned stream.");
    }

    [TestMethod]
    public async Task Dispose_OwnedStream_PeerObservesDisconnect()
    {
        var (clientStream, serverStream) = DuplexStream.CreatePair();
        var client = new JsonRpc(clientStream, Options());
        await using var server = new JsonRpc(serverStream, Options());
        client.StartListening();
        server.StartListening();

        // Disposing the client owns and closes its stream, which is the server's read source, so the
        // server's read loop reaches end-of-stream and Completion finishes. This is the exact property
        // a single-slot listener relies on to free its slot.
        await client.DisposeAsync();

        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(server.Completion.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task RepeatedConnectInvokeDispose_AgainstSingleSlotServer_AllSucceed()
    {
        // Models Aspire's single-slot control listener: only one connection is served at a time, and the
        // next is accepted only after the previous peer's Completion fires (i.e. it closed its end). If
        // disposing the client did not close the transport, the second iteration would deadlock.
        var slot = new SemaphoreSlim(1, 1);

        for (int i = 0; i < 3; i++)
        {
            await slot.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var (clientStream, serverStream) = DuplexStream.CreatePair();
            var server = new JsonRpc(serverStream, Options());
            server.AddLocalRpcMethod("ping", () => i);
            server.StartListening();

            _ = server.Completion.ContinueWith(_ => slot.Release(), TaskScheduler.Default);

            await using (var client = new JsonRpc(clientStream, Options()))
            {
                client.StartListening();
                int result = await client.InvokeAsync<int>("ping").WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(i, result);
            }

            await server.DisposeAsync();
        }
    }
}
