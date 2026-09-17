using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class EnumerationLimitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task AbandonedStarts_AreBounded_AndRejectedEnumeratorsAreDisposedWithoutReading()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        var options = JsonRpcOptions.CreateHardened();
        Assert.AreEqual(128, options.MaximumActiveEnumerations);
        options.MaximumActiveEnumerations = 2;
        options.MaximumConcurrentRequests = 1;
        await using var client = new JsonRpc(h1);
        await using var server = new JsonRpc(h2, options);
        var allocated = new List<TrackedEnumerable>();
        server.AddLocalRpcMethod("stream", () =>
        {
            var stream = new TrackedEnumerable();
            allocated.Add(stream);
            return (IAsyncEnumerable<int>)stream;
        });
        server.StartListening();
        client.StartListening();

        JsonElement first = await client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout);
        await client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout);
        for (int i = 0; i < 20; i++)
        {
            var error = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
                () => client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout));
            Assert.AreEqual(JsonRpcErrorCodes.EnumerationLimitExceeded, error.ErrorCode);
            Assert.AreEqual(0, allocated[^1].Reads);
            Assert.AreEqual(1, allocated[^1].Disposals);
        }

        // An abort releases capacity even when all stream slots are occupied.
        await client.NotifyAsync("$/enumerator/abort", new object?[] { first.GetProperty("token").GetInt64() });
        await allocated[0].Disposed.Task.WaitAsync(Timeout);
        await client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout);
        await server.DisposeAsync();
        foreach (var stream in allocated)
        {
            await stream.Disposed.Task.WaitAsync(Timeout);
            Assert.AreEqual(1, stream.Disposals);
        }
    }

    [TestMethod]
    public async Task ConcurrentStarts_ReserveCapacityBeforeFirstRead_AndShutdownCannotStrandStart()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1);
        await using var server = new JsonRpc(h2, new JsonRpcOptions { MaximumActiveEnumerations = 1 });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allocated = new TaskCompletionSource<TrackedEnumerable>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("stream", () =>
        {
            var stream = new TrackedEnumerable { ReadRelease = release.Task };
            allocated.TrySetResult(stream);
            return (IAsyncEnumerable<int>)stream;
        });
        server.StartListening();
        client.StartListening();
        Task<JsonElement> start = client.InvokeAsync<JsonElement>("stream");
        var first = await allocated.Task.WaitAsync(Timeout);
        await first.Reading.Task.WaitAsync(Timeout);
        var error = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            () => client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout));
        Assert.AreEqual(JsonRpcErrorCodes.EnumerationLimitExceeded, error.ErrorCode);

        await server.DisposeAsync();
        release.SetResult();
        await first.Disposed.Task.WaitAsync(Timeout);
        Assert.AreEqual(1, first.Disposals);
        // The harness handlers do not own their pipes; close the client to settle its pending call.
        await client.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ConnectionLostException>(() => start.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task ShutdownDuringNext_WaitsForReadBeforeDisposing()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1);
        await using var server = new JsonRpc(h2, new JsonRpcOptions { MaximumActiveEnumerations = 1 });
        var stream = new TrackedEnumerable();
        server.AddLocalRpcMethod("stream", () => (IAsyncEnumerable<int>)stream);
        server.StartListening();
        client.StartListening();
        JsonElement start = await client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.ReadRelease = release.Task;
        stream.Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonElement> next = client.InvokeAsync<JsonElement>("$/enumerator/next",
            new object?[] { start.GetProperty("token").GetInt64() });
        await stream.Reading.Task.WaitAsync(Timeout);
        await server.DisposeAsync();
        Assert.AreEqual(0, stream.Disposals);
        release.SetResult();
        await stream.Disposed.Task.WaitAsync(Timeout);
        Assert.AreEqual(1, stream.Disposals);
        Assert.IsFalse(stream.DisposedDuringRead);
        await client.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ConnectionLostException>(() => next.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task FirstReadFailure_ReleasesCapacity()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1);
        await using var server = new JsonRpc(h2, new JsonRpcOptions { MaximumActiveEnumerations = 1 });
        var stream = new TrackedEnumerable { FailRead = true };
        server.AddLocalRpcMethod("stream", () => (IAsyncEnumerable<int>)stream);
        server.StartListening();
        client.StartListening();
        await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            () => client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout));
        Assert.AreEqual(1, stream.Disposals);
        stream = new TrackedEnumerable();
        await client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout);
        Assert.AreEqual(1, stream.Reads);
    }

    [TestMethod]
    public async Task CompletedOrFaultedNext_ReleasesCapacity_EvenWhenDisposalThrows()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1);
        await using var server = new JsonRpc(h2, new JsonRpcOptions { MaximumActiveEnumerations = 1 });
        var stream = new TrackedEnumerable();
        server.AddLocalRpcMethod("stream", () => (IAsyncEnumerable<int>)stream);
        server.StartListening();
        client.StartListening();
        foreach (bool fail in new[] { false, true })
        {
            JsonElement start = await client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout);
            stream.Finished = true;
            stream.FailRead = fail;
            stream.FailDispose = true;
            Task<JsonElement> next = client.InvokeAsync<JsonElement>("$/enumerator/next",
                new object?[] { start.GetProperty("token").GetInt64() });
            if (fail)
            {
                await Assert.ThrowsExactlyAsync<RemoteInvocationException>(() => next.WaitAsync(Timeout));
            }
            else
            {
                Assert.IsTrue((await next.WaitAsync(Timeout)).GetProperty("finished").GetBoolean());
            }
            Assert.AreEqual(1, stream.Disposals);
            stream = new TrackedEnumerable();
        }
        await client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task FailedStartResponse_DisposesRegisteredEnumeratorAndReleasesCapacity()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1);
        await using var server = new JsonRpc(new FailFirstWriteHandler(h2),
            new JsonRpcOptions { MaximumActiveEnumerations = 1 });
        var stream = new TrackedEnumerable();
        server.AddLocalRpcMethod("stream", () => (IAsyncEnumerable<int>)stream);
        server.StartListening();
        client.StartListening();
        await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            () => client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout));
        Assert.AreEqual(1, stream.Disposals);
        stream = new TrackedEnumerable();
        await client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout);
        Assert.AreEqual(1, stream.Reads);
    }

    private sealed class FailFirstWriteHandler(IJsonRpcMessageHandler inner) : IJsonRpcMessageHandler
    {
        private int _writes;
        public ValueTask WriteMessageAsync(ReadOnlyMemory<byte> messageJsonUtf8, CancellationToken cancellationToken)
            => Interlocked.Increment(ref _writes) == 1
                ? ValueTask.FromException(new IOException("Write failed."))
                : inner.WriteMessageAsync(messageJsonUtf8, cancellationToken);
        public ValueTask<ReadOnlyMemory<byte>?> ReadMessageAsync(CancellationToken cancellationToken)
            => inner.ReadMessageAsync(cancellationToken);
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class TrackedEnumerable : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public int Reads;
        public int Disposals;
        public bool FailRead;
        public bool FailDispose;
        public bool Finished;
        public bool DisposedDuringRead;
        private bool _reading;
        public Task? ReadRelease;
        public TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 42;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public async ValueTask<bool> MoveNextAsync()
        {
            _reading = true;
            Reads++;
            Reading.TrySetResult();
            try
            {
                if (ReadRelease is not null)
                {
                    await ReadRelease;
                }
                if (FailRead)
                {
                    throw new InvalidOperationException("Read failed.");
                }
                return !Finished;
            }
            finally
            {
                _reading = false;
            }
        }
        public ValueTask DisposeAsync()
        {
            DisposedDuringRead |= _reading;
            Disposals++;
            Disposed.TrySetResult();
            return FailDispose ? ValueTask.FromException(new InvalidOperationException("Dispose failed.")) : ValueTask.CompletedTask;
        }
    }
}
