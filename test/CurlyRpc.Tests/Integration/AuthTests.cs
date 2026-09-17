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
}
