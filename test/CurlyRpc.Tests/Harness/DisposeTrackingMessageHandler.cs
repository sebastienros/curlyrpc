namespace CurlyRpc.Tests.Harness;

/// <summary>
/// Wraps an <see cref="IJsonRpcMessageHandler"/> and signals a <see cref="TaskCompletionSource"/> the
/// first time it is disposed, so tests can observe that the transport was actually torn down (rather
/// than merely left idle).
/// </summary>
internal sealed class DisposeTrackingMessageHandler : IJsonRpcMessageHandler
{
    private readonly IJsonRpcMessageHandler _inner;
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposedFlag;

    public DisposeTrackingMessageHandler(IJsonRpcMessageHandler inner) => _inner = inner;

    /// <summary>Completes when the handler is disposed for the first time.</summary>
    public Task Disposed => _disposed.Task;

    public ValueTask WriteMessageAsync(ReadOnlyMemory<byte> messageJsonUtf8, CancellationToken cancellationToken)
        => _inner.WriteMessageAsync(messageJsonUtf8, cancellationToken);

    public ValueTask<ReadOnlyMemory<byte>?> ReadMessageAsync(CancellationToken cancellationToken)
        => _inner.ReadMessageAsync(cancellationToken);

    public void Dispose()
    {
        MarkDisposed();
        _inner.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        MarkDisposed();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    private void MarkDisposed()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) == 0)
        {
            _disposed.TrySetResult();
        }
    }
}
