using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Framing;

[TestClass]
public sealed class StreamMessageHandlerTests
{
    [TestMethod]
    public async Task DisposeAsync_WhileWriteInProgress_FailsQueuedWritersAndReleasesHolder()
    {
        var output = new GatedWriteStream();
        var handler = new HeaderDelimitedMessageHandler(output, Stream.Null);

        // The first write holds the write lock, blocked inside the stream.
        Task holder = handler.WriteMessageAsync(Encoding.UTF8.GetBytes("[1]"), CancellationToken.None).AsTask();
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task queued = handler.WriteMessageAsync(Encoding.UTF8.GetBytes("[2]"), CancellationToken.None).AsTask();
        using var cts = new CancellationTokenSource();
        Task queuedWithToken = handler.WriteMessageAsync(Encoding.UTF8.GetBytes("[3]"), cts.Token).AsTask();
        Assert.IsFalse(queued.IsCompleted);
        Assert.IsFalse(queuedWithToken.IsCompleted);

        await handler.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => queuedWithToken.WaitAsync(TimeSpan.FromSeconds(5)));

        // The holder finishes its frame and releases the lock without throwing.
        output.Release.TrySetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await handler.WriteMessageAsync(Encoding.UTF8.GetBytes("[4]"), CancellationToken.None));
    }

    [TestMethod]
    public async Task Dispose_WhileWriteInProgress_FailsQueuedWriters()
    {
        var output = new GatedWriteStream();
        var handler = new NewlineDelimitedMessageHandler(output, Stream.Null);

        Task holder = handler.WriteMessageAsync(Encoding.UTF8.GetBytes("[1]"), CancellationToken.None).AsTask();
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task queued = handler.WriteMessageAsync(Encoding.UTF8.GetBytes("[2]"), CancellationToken.None).AsTask();

        handler.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        output.Release.TrySetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task CallerCancellation_WhileQueued_ThrowsOperationCanceled()
    {
        var output = new GatedWriteStream();
        await using var handler = new HeaderDelimitedMessageHandler(output, Stream.Null);

        Task holder = handler.WriteMessageAsync(Encoding.UTF8.GetBytes("[1]"), CancellationToken.None).AsTask();
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        Task queued = handler.WriteMessageAsync(Encoding.UTF8.GetBytes("[2]"), cts.Token).AsTask();

        cts.Cancel();
        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(cts.Token, ex.CancellationToken);

        output.Release.TrySetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class GatedWriteStream : Stream
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => WriteStarted.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
