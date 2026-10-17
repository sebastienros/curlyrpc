using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CurlyRpc.Extensions.Logging;
using CurlyRpc.Tests.Harness;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class ConnectionLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    // ---- framing failures close the transport ---------------------------------------------------

    [TestMethod]
    public async Task FramingError_ClosesTransport_SoPeerSeesEndOfStream()
    {
        var input = new PipeStream();
        var output = new PipeStream();
        await using var server = new JsonRpc(new HeaderDelimitedMessageHandler(output, input, ownsStreams: true), Options());
        var peer = new HeaderDelimitedMessageHandler(input, output);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("block", async (CancellationToken ct) =>
        {
            using var _ = ct.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        });
        server.StartListening();

        await WriteAsync(peer, """{"jsonrpc":"2.0","method":"block","id":1}""");
        await entered.Task.WaitAsync(Timeout);
        await input.WriteAsync(Encoding.ASCII.GetBytes("Content-Length: nope\r\n\r\n"));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => server.Completion.WaitAsync(Timeout));
        await canceled.Task.WaitAsync(Timeout);
        Assert.IsNull(await peer.ReadMessageAsync(CancellationToken.None).AsTask().WaitAsync(Timeout));
    }

    // ---- throwing cancellation callbacks --------------------------------------------------------

    [TestMethod]
    public async Task ThrowingCancellationCallback_DoesNotKillReadLoop()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, Options());
        await using var server = new JsonRpc(h2, Options());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("block", (CancellationToken ct) => BlockWithThrowingCallback(entered, ct));
        server.AddLocalRpcMethod("echo", (string s) => s);
        server.StartListening();
        client.StartListening();

        using var cts = new CancellationTokenSource();
        Task call = client.InvokeAsync("block", null, cts.Token);
        await entered.Task.WaitAsync(Timeout);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(Timeout));

        Assert.AreEqual("alive", await client.InvokeAsync<string>("echo", "alive").WaitAsync(Timeout));
        Assert.IsFalse(server.Completion.IsCompleted);
    }

    [TestMethod]
    public async Task ThrowingCancellationCallback_DisposeStillCompletesAndDisposesHandler()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        var serverHandler = new DisposeTrackingMessageHandler(h2);
        await using var client = new JsonRpc(h1, Options());
        var server = new JsonRpc(serverHandler, Options());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("block", (CancellationToken ct) => BlockWithThrowingCallback(entered, ct));
        server.StartListening();
        client.StartListening();

        _ = client.InvokeAsync("block", null, CancellationToken.None);
        await entered.Task.WaitAsync(Timeout);

        await server.DisposeAsync().AsTask().WaitAsync(Timeout);
        await server.Completion.WaitAsync(Timeout);
        await serverHandler.Disposed.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task ThrowingCancellationCallback_PeerDisconnect_StillCompletesCompletion()
    {
        var (h1, h2) = CreateOwnedPair();
        var client = new JsonRpc(h1, Options());
        await using var server = new JsonRpc(h2, Options());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("block", (CancellationToken ct) => BlockWithThrowingCallback(entered, ct));
        server.StartListening();
        client.StartListening();

        _ = client.InvokeAsync("block", null, CancellationToken.None);
        await entered.Task.WaitAsync(Timeout);
        await client.DisposeAsync();

        await server.Completion.WaitAsync(Timeout);
    }

    private static async Task BlockWithThrowingCallback(TaskCompletionSource entered, CancellationToken ct)
    {
        using var registration = ct.Register(static () => throw new InvalidOperationException("callback failure"));
        entered.TrySetResult();
        await Task.Delay(System.Threading.Timeout.Infinite, ct);
    }

    // ---- calls after or racing connection loss --------------------------------------------------

    [TestMethod]
    public async Task Invoke_AfterConnectionLoss_FailsWithConnectionLost()
    {
        var input = new PipeStream();
        var output = new PipeStream();
        await using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(output, input, ownsStreams: true), Options());
        rpc.StartListening();

        input.CloseWriter();
        await rpc.Completion.WaitAsync(Timeout);

        // Writes still succeed on this transport, so only the shutdown re-check can fail the call.
        await Assert.ThrowsExactlyAsync<ConnectionLostException>(
            () => rpc.InvokeAsync<int>("anything").WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task Invoke_AfterDispose_ThrowsObjectDisposed()
    {
        var input = new PipeStream();
        var output = new PipeStream();
        var options = Options();
        options.DisposeHandlerOnDispose = false;
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(output, input), options);
        rpc.StartListening();
        await rpc.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => rpc.InvokeAsync<int>("anything").WaitAsync(Timeout));
    }

    // ---- streaming enumerations own a live cancellation token -----------------------------------

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BlockedIterator_IsCanceled_OnAbortOrShutdown(bool disconnect)
    {
        var (h1, h2) = CreateOwnedPair();
        var client = new JsonRpc(h1, Options());
        await using var server = new JsonRpc(h2, Options());
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("stream", (CancellationToken ct) => YieldThenBlock(blocked, released, ct));
        server.StartListening();
        client.StartListening();

        try
        {
            JsonElement start = await client.InvokeAsync<JsonElement>("stream").WaitAsync(Timeout);
            long token = start.GetProperty("token").GetInt64();
            Assert.AreEqual(0, start.GetProperty("values")[0].GetInt32());

            Task<JsonElement> next = client.InvokeWithParameterObjectAsync<JsonElement>("$/enumerator/next", new { token });
            await blocked.Task.WaitAsync(Timeout);

            if (disconnect)
            {
                await client.DisposeAsync();
            }
            else
            {
                await client.NotifyWithParameterObjectAsync("$/enumerator/abort", new { token });
                await Assert.ThrowsAsync<RemoteInvocationException>(() => next.WaitAsync(Timeout));
            }

            await released.Task.WaitAsync(Timeout);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static async IAsyncEnumerable<int> YieldThenBlock(
        TaskCompletionSource blocked,
        TaskCompletionSource released,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            yield return 0;
            blocked.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            yield return 1;
        }
        finally
        {
            released.TrySetResult();
        }
    }

    // ---- duplicate inbound ids and canceling queued requests ------------------------------------

    [TestMethod]
    public async Task DuplicateInFlightId_IsRejected_AndOriginalStaysCancelable()
    {
        var (rpc, peer) = CreateRawConnection(Options());
        await using var _ = rpc;
        int calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.AddLocalRpcMethod("block", async (CancellationToken ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        });
        rpc.StartListening();

        await WriteAsync(peer, """{"jsonrpc":"2.0","method":"block","id":1}""");
        await entered.Task.WaitAsync(Timeout);
        await WriteAsync(peer, """{"jsonrpc":"2.0","method":"block","id":1}""");

        using (JsonDocument duplicate = await ReadAsync(peer))
        {
            Assert.AreEqual(1, duplicate.RootElement.GetProperty("id").GetInt32());
            Assert.AreEqual(JsonRpcErrorCodes.InvalidRequest, duplicate.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        Assert.AreEqual(1, Volatile.Read(ref calls));

        await WriteAsync(peer, """{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":1}}""");
        using JsonDocument canceled = await ReadAsync(peer);
        Assert.AreEqual(1, canceled.RootElement.GetProperty("id").GetInt32());
        Assert.AreEqual(-32800, canceled.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task CancelRequest_ForThrottledQueuedRequest_TakesEffectImmediately()
    {
        var options = Options();
        options.MaximumConcurrentRequests = 1;
        var (rpc, peer) = CreateRawConnection(options);
        await using var _ = rpc;
        int calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.AddLocalRpcMethod("block", async (CancellationToken ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        });
        rpc.StartListening();

        await WriteAsync(peer, """{"jsonrpc":"2.0","method":"block","id":1}""");
        await entered.Task.WaitAsync(Timeout);
        await WriteAsync(peer, """{"jsonrpc":"2.0","method":"block","id":2}""");
        await Task.Delay(50);
        await WriteAsync(peer, """{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":2}}""");

        using (JsonDocument queued = await ReadAsync(peer))
        {
            Assert.AreEqual(2, queued.RootElement.GetProperty("id").GetInt32());
            Assert.AreEqual(-32800, queued.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        Assert.AreEqual(1, Volatile.Read(ref calls));

        await WriteAsync(peer, """{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":1}}""");
        using JsonDocument first = await ReadAsync(peer);
        Assert.AreEqual(1, first.RootElement.GetProperty("id").GetInt32());
        Assert.AreEqual(1, Volatile.Read(ref calls));
    }

    // ---- keep-alive -----------------------------------------------------------------------------

    [TestMethod]
    public async Task KeepAlive_StuckWrite_IsDetectedAsConnectionLoss()
    {
        var input = new PipeStream();
        var output = new PipeStream();
        var handler = new PingFaultingHandler(new HeaderDelimitedMessageHandler(output, input), PingFault.Hang);
        var options = Options();
        options.KeepAliveInterval = TimeSpan.FromMilliseconds(50);
        options.KeepAliveTimeout = TimeSpan.FromMilliseconds(200);
        await using var rpc = new JsonRpc(handler, options);
        rpc.StartListening();

        await Assert.ThrowsExactlyAsync<ConnectionLostException>(() => rpc.Completion.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task KeepAlive_UnexpectedWriteFailure_FaultsConnection()
    {
        var input = new PipeStream();
        var output = new PipeStream();
        var handler = new PingFaultingHandler(new HeaderDelimitedMessageHandler(output, input), PingFault.Throw);
        var options = Options();
        options.KeepAliveInterval = TimeSpan.FromMilliseconds(50);
        options.KeepAliveTimeout = TimeSpan.FromSeconds(2);
        await using var rpc = new JsonRpc(handler, options);
        rpc.StartListening();

        var ex = await Assert.ThrowsExactlyAsync<ConnectionLostException>(() => rpc.Completion.WaitAsync(Timeout));
        Assert.IsInstanceOfType<IOException>(ex.InnerException);
    }

    [TestMethod]
    public async Task KeepAlive_MalformedErrorReply_CountsAsAlive()
    {
        var options = Options();
        options.KeepAliveInterval = TimeSpan.FromMilliseconds(50);
        options.KeepAliveTimeout = TimeSpan.FromSeconds(2);
        var (rpc, peer) = CreateRawConnection(options);
        await using var _ = rpc;
        rpc.StartListening();

        using (JsonDocument ping = await ReadAsync(peer))
        {
            Assert.AreEqual("$/ping", ping.RootElement.GetProperty("method").GetString());
            await WriteAsync(peer, $$"""{"jsonrpc":"2.0","id":{{ping.RootElement.GetProperty("id").GetRawText()}},"error":"bad"}""");
        }

        // The loop keeps pinging after a reply it cannot parse.
        using (JsonDocument ping = await ReadAsync(peer))
        {
            Assert.AreEqual("$/ping", ping.RootElement.GetProperty("method").GetString());
            await WriteAsync(peer, $$"""{"jsonrpc":"2.0","id":{{ping.RootElement.GetProperty("id").GetRawText()}},"result":null}""");
        }

        Assert.IsFalse(rpc.Completion.IsCompleted);
    }

    private enum PingFault
    {
        Hang,
        Throw,
    }

    private sealed class PingFaultingHandler(IJsonRpcMessageHandler inner, PingFault fault) : IJsonRpcMessageHandler
    {
        public ValueTask WriteMessageAsync(ReadOnlyMemory<byte> messageJsonUtf8, CancellationToken cancellationToken)
        {
            if (Encoding.UTF8.GetString(messageJsonUtf8.Span).Contains("\"$/ping\"", StringComparison.Ordinal))
            {
                // A stuck transport that ignores cancellation, or one that fails unexpectedly.
                return fault == PingFault.Hang
                    ? new ValueTask(new TaskCompletionSource().Task)
                    : ValueTask.FromException(new IOException("write failed"));
            }

            return inner.WriteMessageAsync(messageJsonUtf8, cancellationToken);
        }

        public ValueTask<ReadOnlyMemory<byte>?> ReadMessageAsync(CancellationToken cancellationToken)
            => inner.ReadMessageAsync(cancellationToken);

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // ---- bounded telemetry ----------------------------------------------------------------------

    [TestMethod]
    public async Task UnknownMethod_UsesOtherForSpanAndMetric_AndRequestIdTagIsCapped()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == JsonRpcDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var methodTags = new ConcurrentQueue<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == JsonRpcDiagnostics.SourceName && instrument.Name == "rpc.server.duration")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "rpc.method" && tag.Value is string value)
                {
                    methodTags.Enqueue(value);
                }
            }
        });
        meterListener.Start();

        var (rpc, peer) = CreateRawConnection(Options());
        await using var _ = rpc;
        string echoMethod = "echo-" + Guid.NewGuid().ToString("N");
        rpc.AddLocalRpcMethod(echoMethod, (int x) => x);
        rpc.StartListening();

        string unknown = "unknown-" + Guid.NewGuid().ToString("N");
        await WriteAsync(peer, $$"""{"jsonrpc":"2.0","method":"{{unknown}}","id":1}""");
        using (JsonDocument _1 = await ReadAsync(peer))
        {
        }

        // Longer than the 128-character tag cap, but within the 256-character id limit.
        string longId = new string('i', 200);
        await WriteAsync(peer, $$"""{"jsonrpc":"2.0","method":"{{echoMethod}}","params":[3],"id":"{{longId}}"}""");
        using (JsonDocument _2 = await ReadAsync(peer))
        {
        }

        await WaitUntilAsync(() => activities.Any(a => a.Kind == ActivityKind.Server && a.DisplayName == echoMethod)
            && methodTags.Contains(echoMethod));

        Assert.IsFalse(activities.Any(a => a.DisplayName.Contains(unknown, StringComparison.Ordinal)
            || a.Tags.Any(t => t.Value?.Contains(unknown, StringComparison.Ordinal) == true)));
        Assert.IsTrue(activities.Any(a => a.Kind == ActivityKind.Server && a.DisplayName == JsonRpcDiagnostics.OtherMethodName
            && (string?)a.GetTagItem("rpc.method") == JsonRpcDiagnostics.OtherMethodName));
        Assert.IsTrue(methodTags.Contains(JsonRpcDiagnostics.OtherMethodName));
        Assert.IsFalse(methodTags.Contains(unknown));

        Activity echo = activities.First(a => a.DisplayName == echoMethod);
        Assert.AreEqual(128, ((string)echo.GetTagItem("rpc.jsonrpc.request_id")!).Length);
    }

    [TestMethod]
    public async Task LoggingBridge_LogsMethodNotFoundBelowError()
    {
        var logger = new LevelCapturingLogger();
        using var bridge = new JsonRpcLoggingBridge(logger);
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, Options());
        await using var server = new JsonRpc(h2, Options());
        server.AddLocalRpcMethod("fail", () => throw new InvalidOperationException("boom"));
        server.StartListening();
        client.StartListening();

        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(() => client.InvokeAsync<int>("missing-method"));
        await Assert.ThrowsAsync<RemoteInvocationException>(() => client.InvokeAsync<int>("fail"));

        await WaitUntilAsync(() => logger.Snapshot().Any(e => e.Level == LogLevel.Error && e.Message.Contains("'fail'", StringComparison.Ordinal))
            && logger.Snapshot().Any(e => e.Message.Contains(JsonRpcDiagnostics.OtherMethodName, StringComparison.Ordinal) && e.Message.Contains("failed", StringComparison.Ordinal)));

        Assert.IsFalse(logger.Snapshot().Any(e => e.Level >= LogLevel.Error && e.Message.Contains(JsonRpcDiagnostics.OtherMethodName, StringComparison.Ordinal)));
        Assert.IsFalse(logger.Snapshot().Any(e => e.Message.Contains("missing-method", StringComparison.Ordinal)
            && e.Message.Contains("Server", StringComparison.Ordinal)));
    }

    private sealed class LevelCapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public (LogLevel Level, string Message)[] Snapshot()
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    // ---- bounded method-not-found echo and batch size -------------------------------------------

    [TestMethod]
    public async Task MethodNotFound_DoesNotEchoFullMethodName()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, Options());
        await using var server = new JsonRpc(h2, Options());
        server.StartListening();
        client.StartListening();

        string method = new string('m', 10_000);
        var ex = await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(() => client.InvokeAsync<int>(method));
        Assert.IsTrue(ex.Message.Length < 200, ex.Message);
    }

    [TestMethod]
    public async Task MaximumBatchSize_RejectsOversizedBatchWithoutDispatching()
    {
        var options = Options();
        options.MaximumBatchSize = 2;
        var (rpc, peer) = CreateRawConnection(options);
        await using var _ = rpc;
        int calls = 0;
        rpc.AddLocalRpcMethod("run", () => Interlocked.Increment(ref calls));
        rpc.StartListening();

        await WriteAsync(peer, """[{"jsonrpc":"2.0","method":"run","id":1},{"jsonrpc":"2.0","method":"run","id":2},{"jsonrpc":"2.0","method":"run","id":3}]""");
        using (JsonDocument rejected = await ReadAsync(peer))
        {
            Assert.AreEqual(JsonValueKind.Object, rejected.RootElement.ValueKind);
            Assert.AreEqual(JsonValueKind.Null, rejected.RootElement.GetProperty("id").ValueKind);
            Assert.AreEqual(JsonRpcErrorCodes.InvalidRequest, rejected.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        Assert.AreEqual(0, Volatile.Read(ref calls));

        await WriteAsync(peer, """[{"jsonrpc":"2.0","method":"run","id":1},{"jsonrpc":"2.0","method":"run","id":2}]""");
        using JsonDocument accepted = await ReadAsync(peer);
        Assert.AreEqual(2, accepted.RootElement.GetArrayLength());
        Assert.AreEqual(2, Volatile.Read(ref calls));
    }

    [TestMethod]
    public void MaximumBatchSize_DefaultsAndValidation()
    {
        Assert.AreEqual(0, new JsonRpcOptions().MaximumBatchSize);
        Assert.AreEqual(JsonRpcOptions.DefaultHardenedMaximumBatchSize, JsonRpcOptions.CreateHardened().MaximumBatchSize);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new JsonRpcOptions { MaximumBatchSize = -1 });
    }

    [TestMethod]
    public void NegativeLimits_AreRejected()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new JsonRpc(h1, new JsonRpcOptions { MaximumInboundMessageSize = -1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new JsonRpc(h1, new JsonRpcOptions { MaximumConcurrentRequests = -1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new JsonRpc(h1, new JsonRpcOptions { MaximumActiveEnumerations = -1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new JsonRpc(h1, new JsonRpcOptions { MaximumPendingInboundRequests = -1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new JsonRpc(h1, new JsonRpcOptions { MaximumPendingInboundBytes = -1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => h2.MaximumMessageSize = -1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NewlineDelimitedMessageHandler(new MemoryStream(), maximumMessageSize: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new JsonRpc(new MemoryStream(), new JsonRpcOptions { MaximumInboundMessageSize = -1 }));
    }

    // ---- Helpers --------------------------------------------------------------------------------

    // Handlers that own their pipes, so disposing one end is observed as end-of-stream by the other.
    private static (HeaderDelimitedMessageHandler First, HeaderDelimitedMessageHandler Second) CreateOwnedPair()
    {
        var pipe1 = new PipeStream();
        var pipe2 = new PipeStream();
        return (new HeaderDelimitedMessageHandler(pipe1, pipe2, ownsStreams: true),
            new HeaderDelimitedMessageHandler(pipe2, pipe1, ownsStreams: true));
    }

    private static (JsonRpc Rpc, HeaderDelimitedMessageHandler Peer) CreateRawConnection(JsonRpcOptions options)
    {
        var input = new PipeStream();
        var output = new PipeStream();
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(output, input, ownsStreams: true), options);
        return (rpc, new HeaderDelimitedMessageHandler(input, output));
    }

    private static ValueTask WriteAsync(HeaderDelimitedMessageHandler peer, string message)
        => peer.WriteMessageAsync(Encoding.UTF8.GetBytes(message), CancellationToken.None);

    private static async Task<JsonDocument> ReadAsync(HeaderDelimitedMessageHandler peer)
    {
        ReadOnlyMemory<byte>? response = await peer.ReadMessageAsync(CancellationToken.None).AsTask().WaitAsync(Timeout);
        Assert.IsNotNull(response);
        return JsonDocument.Parse(response.Value);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > Timeout)
            {
                Assert.Fail("Condition was not observed in time.");
            }

            await Task.Delay(15);
        }
    }
}
