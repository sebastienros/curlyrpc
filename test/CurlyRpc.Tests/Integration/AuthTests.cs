using System.Text;
using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class AuthTests
{
    private const string Token = "s3cr3t-key";

    private static JsonRpcOptions ClientOptions()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    private static JsonRpcOptions ServerOptions()
        => new()
        {
            SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
            InboundMiddleware = new HandshakeAuthenticationMiddleware(Token),
        };

    private static (JsonRpc Client, JsonRpc Server) CreatePair(JsonRpcOptions? serverOptions = null)
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        var client = new JsonRpc(h1, ClientOptions());
        var server = new JsonRpc(h2, serverOptions ?? ServerOptions());
        server.AddLocalRpcMethod("echo", (string s) => s);
        server.StartListening();
        client.StartListening();
        return (client, server);
    }

    [TestMethod]
    [DataRow("echo")]
    [DataRow("ping")]
    [DataRow("authenticate")]
    public async Task SharedMiddleware_RejectsSecondConnection(string method)
    {
        var options = ServerOptions();
        var (clientA, serverA) = CreatePair(options);
        await using var _ca = clientA;
        await using var _sa = serverA;
        var (clientB, serverB) = CreatePair(options);
        await using var _cb = clientB;
        await using var _sb = serverB;

        Assert.IsTrue(await clientA.InvokeAsync<bool>("authenticate", Token));
        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await clientB.InvokeAsync<JsonElement>(method, Token));

        Assert.AreEqual(HandshakeAuthenticationMiddleware.AuthenticationFailedErrorCode, ex.ErrorCode);
        await serverB.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("still authenticated", await clientA.InvokeAsync<string>("echo", "still authenticated"));
    }

    [TestMethod]
    public async Task SharedMiddleware_ConcurrentHandshakes_OnlyOneConnectionSucceeds()
    {
        var options = ServerOptions();
        var (clientA, serverA) = CreatePair(options);
        await using var _ca = clientA;
        await using var _sa = serverA;
        var (clientB, serverB) = CreatePair(options);
        await using var _cb = clientB;
        await using var _sb = serverB;

        var results = await Task.WhenAll(Authenticate(clientA), Authenticate(clientB))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreNotEqual(results[0], results[1]);
        var winner = results[0] ? clientA : clientB;
        var rejectedServer = results[0] ? serverB : serverA;
        Assert.AreEqual("authorized", await winner.InvokeAsync<string>("echo", "authorized"));
        await rejectedServer.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        static async Task<bool> Authenticate(JsonRpc client)
        {
            try
            {
                return await client.InvokeAsync<bool>("authenticate", Token);
            }
            catch (RemoteInvocationException ex)
            {
                Assert.AreEqual(HandshakeAuthenticationMiddleware.AuthenticationFailedErrorCode, ex.ErrorCode);
                return false;
            }
        }
    }

    [TestMethod]
    public async Task SharedMiddleware_PreAuthenticationPing_BindsConnection()
    {
        var options = ServerOptions();
        var middleware = (HandshakeAuthenticationMiddleware)options.InboundMiddleware!;
        var (clientA, serverA) = CreatePair(options);
        await using var _ca = clientA;
        await using var _sa = serverA;
        var (clientB, serverB) = CreatePair(options);
        await using var _cb = clientB;
        await using var _sb = serverB;

        Assert.IsTrue(await clientA.InvokeAsync<bool>("ping"));
        Assert.IsFalse(middleware.IsAuthenticated);
        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await clientB.InvokeAsync<bool>("authenticate", Token));
        Assert.AreEqual(HandshakeAuthenticationMiddleware.AuthenticationFailedErrorCode, ex.ErrorCode);
        Assert.IsFalse(middleware.IsAuthenticated);
        await serverB.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(await clientA.InvokeAsync<bool>("authenticate", Token));
        Assert.IsTrue(middleware.IsAuthenticated);
    }

    [TestMethod]
    public async Task SharedMiddleware_CannotBeReusedAfterOwnerIsDisposed()
    {
        var options = ServerOptions();
        var (clientA, serverA) = CreatePair(options);
        await using (clientA)
        await using (serverA)
        {
            Assert.IsTrue(await clientA.InvokeAsync<bool>("authenticate", Token));
        }

        var (clientB, serverB) = CreatePair(options);
        await using var _cb = clientB;
        await using var _sb = serverB;
        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await clientB.InvokeAsync<string>("echo", "unauthenticated"));
        Assert.AreEqual(HandshakeAuthenticationMiddleware.AuthenticationFailedErrorCode, ex.ErrorCode);
        await serverB.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Method_BeforeAuthentication_IsRejected()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await client.InvokeAsync<string>("echo", "hi"));

        Assert.AreEqual(HandshakeAuthenticationMiddleware.AuthenticationRequiredErrorCode, ex.ErrorCode);
    }

    [TestMethod]
    public async Task Ping_IsAllowedBeforeAuthentication()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        bool pong = await client.InvokeAsync<bool>("ping");
        Assert.IsTrue(pong);
    }

    [TestMethod]
    public async Task Authenticate_WithValidToken_UnlocksMethods()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        bool authed = await client.InvokeAsync<bool>("authenticate", Token);
        Assert.IsTrue(authed);

        string result = (await client.InvokeAsync<string>("echo", "hello"))!;
        Assert.AreEqual("hello", result);
    }

    [TestMethod]
    public async Task Authenticate_WithInvalidToken_FailsAndClosesConnection()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await client.InvokeAsync<bool>("authenticate", "wrong-token"));

        Assert.AreEqual(HandshakeAuthenticationMiddleware.AuthenticationFailedErrorCode, ex.ErrorCode);

        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("s3cr3t")]
    [DataRow("s3cr3t-key-and-more")]
    [DataRow("S3CR3T-KEY")]
    public async Task Authenticate_WithTokenOfAnyOtherLength_Fails(string candidate)
    {
        // Digests are compared, so a prefix, an extension or an empty token is just another mismatch.
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await client.InvokeAsync<bool>("authenticate", candidate));

        Assert.AreEqual(HandshakeAuthenticationMiddleware.AuthenticationFailedErrorCode, ex.ErrorCode);
    }

    [TestMethod]
    public async Task Authenticate_WithByteToken_AcceptsMatchingUtf8Token()
    {
        var options = ServerOptions();
        options.InboundMiddleware = new HandshakeAuthenticationMiddleware(Encoding.UTF8.GetBytes(Token));
        var (client, server) = CreatePair(options);
        await using var _c = client;
        await using var _s = server;

        Assert.IsTrue(await client.InvokeAsync<bool>("authenticate", Token));
        Assert.AreEqual("hello", await client.InvokeAsync<string>("echo", "hello"));
    }

    [TestMethod]
    public async Task Authenticate_WithInvalidToken_DisposesServerTransport()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        var serverHandler = new DisposeTrackingMessageHandler(h2);

        var client = new JsonRpc(h1, ClientOptions());
        var server = new JsonRpc(serverHandler, ServerOptions());
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("echo", (string s) => s);
        server.StartListening();
        client.StartListening();

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await client.InvokeAsync<bool>("authenticate", "wrong-token"));

        Assert.AreEqual(HandshakeAuthenticationMiddleware.AuthenticationFailedErrorCode, ex.ErrorCode);

        // Closing the connection on a failed handshake must tear down the transport, not just stop
        // the read loop. With DisposeHandlerOnDispose (the default) the handler is disposed.
        await serverHandler.Disposed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Authenticate_WithInvalidToken_KeepsTransportWhenDisposeHandlerDisabled()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        var serverHandler = new DisposeTrackingMessageHandler(h2);

        var client = new JsonRpc(h1, ClientOptions());
        var serverOptions = ServerOptions();
        serverOptions.DisposeHandlerOnDispose = false;
        var server = new JsonRpc(serverHandler, serverOptions);
        await using var _c = client;

        server.AddLocalRpcMethod("echo", (string s) => s);
        server.StartListening();
        client.StartListening();

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await client.InvokeAsync<bool>("authenticate", "wrong-token"));

        Assert.AreEqual(HandshakeAuthenticationMiddleware.AuthenticationFailedErrorCode, ex.ErrorCode);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // The caller opted out of handler disposal, so the transport must be left intact.
        Assert.IsFalse(serverHandler.Disposed.IsCompleted);

        await server.DisposeAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Batch_WrongToken_StopsLaterGuessesAndNeverRunsProtectedMethods(bool correctGuessFirstInSameBatch)
    {
        var input = new PipeStream();
        var output = new PipeStream();
        var options = ServerOptions();
        var middleware = (HandshakeAuthenticationMiddleware)options.InboundMiddleware!;
        await using var server = new JsonRpc(new HeaderDelimitedMessageHandler(output, input, ownsStreams: true), options);
        var peer = new HeaderDelimitedMessageHandler(input, output);
        int calls = 0;
        server.AddLocalRpcMethod("op", () => Interlocked.Increment(ref calls));
        server.StartListening();

        var elements = new List<string>
        {
            """{"jsonrpc":"2.0","method":"authenticate","params":["wrong"],"id":1}""",
            """{"jsonrpc":"2.0","method":"op","id":2}""",
            """{"jsonrpc":"2.0","method":"authenticate","params":["guess-2"],"id":3}""",
            """{"jsonrpc":"2.0","method":"op","id":4}""",
            $$"""{"jsonrpc":"2.0","method":"authenticate","params":["{{Token}}"],"id":5}""",
            """{"jsonrpc":"2.0","method":"op","id":6}""",
            """{"jsonrpc":"2.0","method":"op"}""",
        };
        if (correctGuessFirstInSameBatch)
        {
            // Even a guess that comes before any protected call cannot unlock it after a failure.
            elements.Insert(1, $$"""{"jsonrpc":"2.0","method":"authenticate","params":["{{Token}}"],"id":7}""");
        }

        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes("[" + string.Join(',', elements) + "]"), CancellationToken.None);

        // The failed handshake closes the connection: the peer reaches end-of-stream.
        while (await peer.ReadMessageAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) is not null)
        {
        }

        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, Volatile.Read(ref calls));
        Assert.IsFalse(middleware.IsAuthenticated);
    }

    [TestMethod]
    public async Task FailedHandshake_IsPermanent_ForRequestsAlreadyQueued()
    {
        var input = new PipeStream();
        var output = new PipeStream();
        var options = ServerOptions();
        options.DisposeHandlerOnDispose = false;
        var middleware = (HandshakeAuthenticationMiddleware)options.InboundMiddleware!;
        await using var server = new JsonRpc(new HeaderDelimitedMessageHandler(output, input, ownsStreams: true), options);
        var peer = new HeaderDelimitedMessageHandler(input, output);
        int calls = 0;
        server.AddLocalRpcMethod("op", () => Interlocked.Increment(ref calls));
        server.StartListening();

        // Pipelined separately: the correct token arrives right behind the wrong one.
        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"authenticate","params":["wrong"],"id":1}"""), CancellationToken.None);
        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes($$"""{"jsonrpc":"2.0","method":"authenticate","params":["{{Token}}"],"id":2}"""), CancellationToken.None);
        await peer.WriteMessageAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"op","id":3}"""), CancellationToken.None);

        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.AreEqual(0, Volatile.Read(ref calls));
        Assert.IsFalse(middleware.IsAuthenticated);
    }
}
