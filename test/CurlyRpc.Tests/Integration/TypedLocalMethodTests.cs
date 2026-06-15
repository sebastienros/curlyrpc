using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

/// <summary>
/// Covers the AOT- and trim-safe strongly-typed <see cref="JsonRpc.AddLocalRpcMethod{TResult}(string, Func{Task{TResult}})"/>
/// family. These mirror the registration shape Aspire's Native AOT CLI uses for its callback target.
/// </summary>
[TestClass]
public sealed class TypedLocalMethodTests
{
    public sealed record ValidationResult(string Message, bool Successful);

    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    private static (JsonRpc Client, JsonRpc Server) CreatePair()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        return (new JsonRpc(h1, Options()), new JsonRpc(h2, Options()));
    }

    [TestMethod]
    public async Task Parameterless_ReturnsResult()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("getCliVersion", () => Task.FromResult("9.0.0"));
        server.StartListening();
        client.StartListening();

        string version = (await client.InvokeAsync<string>("getCliVersion"))!;
        Assert.AreEqual("9.0.0", version);
    }

    [TestMethod]
    public async Task SingleParameter_ByNameObject_BindsNamedMemberValue()
    {
        // Mirrors the VS Code extension calling sendRequest('validatePromptInputString', { input })
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod<string, ValidationResult?>(
            "validatePromptInputString",
            input => Task.FromResult<ValidationResult?>(new ValidationResult($"got:{input}", input.Length > 0)));
        server.StartListening();
        client.StartListening();

        var result = await client.InvokeWithParameterObjectAsync<ValidationResult>(
            "validatePromptInputString", new { input = "hello" });

        Assert.IsNotNull(result);
        Assert.AreEqual("got:hello", result!.Message);
        Assert.IsTrue(result.Successful);
    }

    [TestMethod]
    public async Task SingleParameter_PositionalArray_Binds()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod<int, int>("square", x => Task.FromResult(x * x));
        server.StartListening();
        client.StartListening();

        int result = await client.InvokeAsync<int>("square", 6);
        Assert.AreEqual(36, result);
    }

    [TestMethod]
    public async Task SingleParameter_WholeDtoObject_Binds()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod<ValidationResult, string>(
            "echoDto", dto => Task.FromResult(dto.Message));
        server.StartListening();
        client.StartListening();

        // Multi-member object => bound as the whole request DTO.
        string message = (await client.InvokeWithParameterObjectAsync<string>(
            "echoDto", new ValidationResult("from-dto", true)))!;
        Assert.AreEqual("from-dto", message);
    }

    [TestMethod]
    public async Task VoidTaskHandler_Completes()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        var tcs = new TaskCompletionSource();
        server.AddLocalRpcMethod("stopCli", () => { tcs.TrySetResult(); return Task.CompletedTask; });
        server.StartListening();
        client.StartListening();

        await client.InvokeAsync("stopCli", null, CancellationToken.None);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task TrailingCancellationToken_IsSupplied()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod<string, string>(
            "withCt",
            (string value, CancellationToken ct) => Task.FromResult($"{value}:{ct.CanBeCanceled}"));
        server.StartListening();
        client.StartListening();

        string result = (await client.InvokeAsync<string>("withCt", new object?[] { "v" }, CancellationToken.None))!;
        Assert.AreEqual("v:True", result);
    }

    [TestMethod]
    public async Task MissingRequiredParameter_ReportsInvalidParams()
    {
        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod<int, int>("needsArg", x => Task.FromResult(x));
        server.StartListening();
        client.StartListening();

        try
        {
            await client.InvokeAsync<int>("needsArg");
            Assert.Fail("Expected an invalid-params error.");
        }
        catch (RemoteInvocationException ex)
        {
            Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, ex.ErrorCode);
        }
    }
}
