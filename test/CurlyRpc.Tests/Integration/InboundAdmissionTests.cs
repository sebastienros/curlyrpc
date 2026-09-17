using System.Text;
using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class InboundAdmissionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task BlockedHandler_CountLimitClosesBeforeDrainingFlood()
    {
        await using var handler = new ControlledHandler();
        var options = JsonRpcOptions.CreateHardened();
        options.MaximumConcurrentRequests = 1;
        options.MaximumPendingInboundRequests = 3;
        await using var rpc = new JsonRpc(handler, options);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        rpc.AddLocalRpcMethod("work", async (CancellationToken token) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
        });
        rpc.StartListening();
        handler.Send(Request(1));
        await started.Task.WaitAsync(Timeout);
        for (int i = 2; i <= 32; i++)
        {
            handler.Send(Request(i));
        }

        await Assert.ThrowsExactlyAsync<ConnectionLostException>(() => rpc.Completion.WaitAsync(Timeout));
        Assert.AreEqual(4, handler.ReadCount);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ByteLimitCountsWholeBatchBeforeDispatch()
    {
        await using var handler = new ControlledHandler();
        string batch = "[" + Request(1) + "," + Request(2) + "]";
        await using var rpc = new JsonRpc(handler, new JsonRpcOptions
        {
            MaximumPendingInboundBytes = Encoding.UTF8.GetByteCount(batch) - 1,
        });
        int calls = 0;
        rpc.AddLocalRpcMethod("work", () => Interlocked.Increment(ref calls));
        rpc.StartListening();
        handler.Send(batch);
        await Assert.ThrowsExactlyAsync<ConnectionLostException>(() => rpc.Completion.WaitAsync(Timeout));
        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, handler.WriteCount);
    }

    [TestMethod]
    public async Task BatchElementsCountIndividuallyBeforeDispatch()
    {
        await using var handler = new ControlledHandler();
        await using var rpc = new JsonRpc(handler, new JsonRpcOptions { MaximumPendingInboundRequests = 2 });
        int calls = 0;
        rpc.AddLocalRpcMethod("work", () => Interlocked.Increment(ref calls));
        rpc.StartListening();
        handler.Send("[" + Request(1) + "," + Request(2) + "," + Request(3) + "]");
        await Assert.ThrowsExactlyAsync<ConnectionLostException>(() => rpc.Completion.WaitAsync(Timeout));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NonReadingPeer_HoldsAdmissionThroughResponseWrite(bool batch)
    {
        await using var handler = new ControlledHandler { BlockWrites = true };
        await using var rpc = new JsonRpc(handler, new JsonRpcOptions { MaximumPendingInboundRequests = 2 });
        rpc.AddLocalRpcMethod("work", () => true);
        rpc.StartListening();
        for (int i = 1; i <= 16; i++)
        {
            handler.Send(batch ? "[" + Request(i) + "]" : Request(i));
        }

        await Assert.ThrowsExactlyAsync<ConnectionLostException>(() => rpc.Completion.WaitAsync(Timeout));
        Assert.AreEqual(3, handler.ReadCount);
        Assert.AreEqual(2, handler.WriteCount);
    }

    [TestMethod]
    [DataRow("{broken")]
    [DataRow("[]")]
    [DataRow("{\"method\":\"$/ping\",\"id\":1}")]
    public async Task NonReadingPeer_ProtocolRepliesAlsoUseAdmission(string message)
    {
        await using var handler = new ControlledHandler { BlockWrites = true };
        await using var rpc = new JsonRpc(handler, new JsonRpcOptions { MaximumPendingInboundRequests = 2 });
        rpc.StartListening();
        for (int i = 0; i < 16; i++)
        {
            handler.Send(message);
        }

        await Assert.ThrowsExactlyAsync<ConnectionLostException>(() => rpc.Completion.WaitAsync(Timeout));
        Assert.AreEqual(3, handler.ReadCount);
        Assert.AreEqual(2, handler.WriteCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SaturatedAdmission_StillProcessesResponsesAndCancellation(bool batch)
    {
        await using var handler = new ControlledHandler();
        await using var rpc = new JsonRpc(handler, new JsonRpcOptions
        {
            MaximumConcurrentRequests = 1,
            MaximumPendingInboundRequests = 1,
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.AddLocalRpcMethod("work", async (CancellationToken token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
            finally { canceled.TrySetResult(); }
        });
        rpc.StartListening();
        handler.Send(Request(10));
        await started.Task.WaitAsync(Timeout);
        Task<int> outbound = rpc.InvokeAsync<int>("remote");
        const string response = "{\"id\":1,\"result\":42}";
        const string cancellation = "{\"method\":\"$/cancelRequest\",\"params\":{\"id\":10}}";
        handler.Send(batch ? "[" + response + "," + cancellation + "]" : response);
        if (!batch) { handler.Send(cancellation); }
        Assert.AreEqual(42, await outbound.WaitAsync(Timeout));
        await canceled.Task.WaitAsync(Timeout);
        Assert.IsFalse(rpc.Completion.IsCompleted);
    }

    [TestMethod]
    public async Task Batch_ResponseAfterHandlerCanCompleteDuplexCall()
    {
        await using var handler = new ControlledHandler();
        await using var rpc = new JsonRpc(handler, new JsonRpcOptions { MaximumPendingInboundRequests = 1 });
        Task<int>? outbound = null;
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.AddLocalRpcMethod("work", async () =>
        {
            int result = await outbound!;
            finished.TrySetResult(result);
            return result;
        });
        rpc.StartListening();
        outbound = rpc.InvokeAsync<int>("remote");
        handler.Send("[" + Request(10) + ",{\"id\":1,\"result\":42}]");
        Assert.AreEqual(42, await finished.Task.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task ByteBudgetAccumulatesAcrossFrames()
    {
        await using var handler = new ControlledHandler { BlockWrites = true };
        int frameBytes = Encoding.UTF8.GetByteCount(Request(1));
        await using var rpc = new JsonRpc(handler, new JsonRpcOptions
        {
            MaximumPendingInboundBytes = 2 * frameBytes,
        });
        rpc.StartListening();
        handler.Send(Request(1));
        handler.Send(Request(2));
        handler.Send(Request(3));
        await Assert.ThrowsExactlyAsync<ConnectionLostException>(() => rpc.Completion.WaitAsync(Timeout));
        Assert.AreEqual(2, handler.WriteCount);
    }

    private static string Request(int id) => $"{{\"method\":\"work\",\"id\":{id},\"params\":{{\"payload\":\"{new string('x', 1024)}\"}}}}";

    private sealed class ControlledHandler : IJsonRpcMessageHandler
    {
        private readonly Channel<ReadOnlyMemory<byte>> _input = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockWrites { get; init; }
        public int ReadCount;
        public int WriteCount;
        public void Send(string json) => _input.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        public async ValueTask<ReadOnlyMemory<byte>?> ReadMessageAsync(CancellationToken cancellationToken)
        {
            var frame = await _input.Reader.ReadAsync(cancellationToken);
            Interlocked.Increment(ref ReadCount);
            return frame;
        }
        public async ValueTask WriteMessageAsync(ReadOnlyMemory<byte> messageJsonUtf8, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref WriteCount);
            if (BlockWrites) { await _closed.Task.WaitAsync(cancellationToken); }
        }
        public void Dispose() => _closed.TrySetResult();
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
