using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class DispatchTests
{
    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    private static (JsonRpc Client, JsonRpc Server) CreatePair(out HeaderDelimitedMessageHandler clientHandler, out HeaderDelimitedMessageHandler serverHandler)
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        clientHandler = h1;
        serverHandler = h2;
        return (new JsonRpc(h1, Options()), new JsonRpc(h2, Options()));
    }

    [TestMethod]
    public async Task AddLocalRpcTarget_AppliesMethodNameTransform()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new Service(), new JsonRpcTargetOptions
        {
            MethodNameTransform = name => "svc/" + name,
        });
        server.StartListening();
        client.StartListening();

        int result = await client.InvokeAsync<int>("svc/Square", 5);
        Assert.AreEqual(25, result);
    }

    [TestMethod]
    public async Task AddLocalRpcTarget_HonorsExplicitNameAndIgnore()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new Service());
        server.StartListening();
        client.StartListening();

        string greeting = (await client.InvokeAsync<string>("greet", "world"))!;
        Assert.AreEqual("hello world", greeting);

        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("Secret"));
    }

    [TestMethod]
    public async Task VoidTaskHandler_CompletesInvocation()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("run", async () =>
        {
            await Task.Yield();
            ran.TrySetResult();
        });
        server.StartListening();
        client.StartListening();

        await client.InvokeAsync("run", Array.Empty<object?>(), CancellationToken.None);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task InvalidParameters_ReturnInvalidParamsError()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("needsInt", (int value) => value);
        server.StartListening();
        client.StartListening();

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await client.InvokeAsync<int>("needsInt", "not-a-number"));

        Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, ex.ErrorCode);
    }

    [TestMethod]
    public async Task NullResult_DeserializesToDefaultForValueTypes()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("getNull", () => (string?)null);
        server.StartListening();
        client.StartListening();

        // A JSON null result must round-trip gracefully into reference types, nullable value types,
        // and (defensively) non-nullable value types — never throw.
        Assert.IsNull(await client.InvokeAsync<string?>("getNull"));
        Assert.IsNull(await client.InvokeAsync<int?>("getNull"));
        Assert.AreEqual(0, await client.InvokeAsync<int>("getNull"));
    }

    [TestMethod]
    public async Task ObjectAndNonPublicMethods_AreNotInvokable()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new Restricted());
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(1, await client.InvokeAsync<int>("Visible"));

        // Methods declared on System.Object must not be exposed.
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<string>("ToString"));
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("GetHashCode"));
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<string>("GetType"));

        // Overrides of System.Object members must not be exposed either.
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<bool>("Equals", 1));

        // Lifetime methods must not be exposed.
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<object>("Dispose"));
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<object>("DisposeAsync"));

        // Non-public methods must not be exposed.
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("Hidden"));
    }

    [TestMethod]
    public async Task RecordTarget_SynthesizedMembersAreNotInvokable()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new Credentials("sk-live-123"));
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(1, await client.InvokeAsync<int>("Visible"));

        // A record's ToString prints every property; it must never be reachable.
        foreach (string name in new[] { "ToString", "GetHashCode", "Equals", "Deconstruct", "<Clone>$", "PrintMembers", "get_ApiKey", "get_EqualityContract" })
        {
            await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
                async () => await client.InvokeAsync<object>(name), name);
        }
    }

    [TestMethod]
    public async Task InheritedMethods_AreExcludedByDefault()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new DerivedService());
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(2, await client.InvokeAsync<int>("Derived"));
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("Inherited"));
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("Overridable"));

        // Public API of a framework base class is not published.
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<object>("Clear"));
    }

    [TestMethod]
    public async Task InheritedMethods_CanBeOptedIn()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new DerivedService(), new JsonRpcTargetOptions { IncludeInheritedMethods = true });
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(2, await client.InvokeAsync<int>("Derived"));
        Assert.AreEqual(1, await client.InvokeAsync<int>("Inherited"));
        Assert.AreEqual(20, await client.InvokeAsync<int>("Overridable"));

        // Even when opted in, System.Object members stay hidden.
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<string>("ToString"));
    }

    [TestMethod]
    public async Task AsyncVoidMethods_AreNotExposed()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new AsyncVoidService());
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(1, await client.InvokeAsync<int>("Visible"));
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<object>("Crash"));
    }

    [TestMethod]
    public async Task AsyncVoidDelegate_IsRejected()
    {
        await using var rpc = new JsonRpc(DuplexConnection.CreateHandlerPair().Item1, Options());

        Action handler = async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        };

        var ex = Assert.ThrowsExactly<ArgumentException>(() => rpc.AddLocalRpcMethod("crash", handler));
        StringAssert.Contains(ex.Message, "async void");
    }

    [TestMethod]
    public async Task JsonRpcMethodOnAsyncVoid_Throws()
    {
        await using var rpc = new JsonRpc(DuplexConnection.CreateHandlerPair().Item1, Options());

        Assert.ThrowsExactly<ArgumentException>(() => rpc.AddLocalRpcTarget(new ExplicitAsyncVoidService()));
    }

    [TestMethod]
    public async Task DuplicateMethodName_Throws()
    {
        await using var rpc = new JsonRpc(DuplexConnection.CreateHandlerPair().Item1, Options());

        rpc.AddLocalRpcMethod("run", () => 1);
        Assert.ThrowsExactly<ArgumentException>(() => rpc.AddLocalRpcMethod("run", () => 2));
        Assert.ThrowsExactly<ArgumentException>(() => rpc.AddLocalRpcMethod("run", () => Task.FromResult(3)));

        // A target whose method collides with an existing registration is rejected as a whole.
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => rpc.AddLocalRpcTarget(new Service(), new JsonRpcTargetOptions { MethodNameTransform = _ => "run" }));
        StringAssert.Contains(ex.Message, "run");
    }

    [TestMethod]
    public async Task RejectedTarget_RegistersNothing()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("Square", (int value) => -value);
        Assert.ThrowsExactly<ArgumentException>(() => server.AddLocalRpcTarget(new Service()));
        server.StartListening();
        client.StartListening();

        // The original registration is untouched and nothing else from the target was added.
        Assert.AreEqual(-5, await client.InvokeAsync<int>("Square", 5));
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<string>("greet", "world"));
    }

    [TestMethod]
    public async Task Overloads_ThrowSuggestingJsonRpcMethod()
    {
        await using var rpc = new JsonRpc(DuplexConnection.CreateHandlerPair().Item1, Options());

        var ex = Assert.ThrowsExactly<ArgumentException>(() => rpc.AddLocalRpcTarget(new OverloadedService()));
        StringAssert.Contains(ex.Message, "[JsonRpcMethod");
    }

    [TestMethod]
    public async Task Overloads_WithDistinctNames_AreRegistered()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new NamedOverloadService());
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(3, await client.InvokeAsync<int>("Add", 1, 2));
        Assert.AreEqual(6, await client.InvokeAsync<int>("add3", 1, 2, 3));
    }

    [TestMethod]
    public async Task ByRefParameters_AreNotExposed()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new ByRefService());
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(1, await client.InvokeAsync<int>("Visible"));
        foreach (string name in new[] { "WithRef", "WithOut", "WithIn", "WithSpan" })
        {
            await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
                async () => await client.InvokeAsync<int>(name, 1), name);
        }
    }

    [TestMethod]
    public async Task ByRefDelegate_IsRejected()
    {
        await using var rpc = new JsonRpc(DuplexConnection.CreateHandlerPair().Item1, Options());

        RefHandler handler = (ref int value) => value++;
        var ex = Assert.ThrowsExactly<ArgumentException>(() => rpc.AddLocalRpcMethod("inc", handler));
        StringAssert.Contains(ex.Message, "by reference");

        Assert.ThrowsExactly<ArgumentException>(() => rpc.AddLocalRpcTarget(new ExplicitByRefService()));
    }

    [TestMethod]
    public async Task Handler_WithOnlyCancellationToken_IsInvokable()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        // A method whose sole parameter is a CancellationToken must bind with no JSON params.
        server.AddLocalRpcMethod("tick", (CancellationToken ct) => 7);
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(7, await client.InvokeAsync<int>("tick"));
    }

    [TestMethod]
    public async Task Handler_WithJsonElementAndCancellationToken_BindsParameterObject()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        // A handler taking (JsonElement, CancellationToken) must receive the whole by-name params
        // object in its JsonElement parameter.
        server.AddLocalRpcMethod(
            "describe",
            (JsonElement data, CancellationToken ct) => data.GetProperty("name").GetString());
        server.StartListening();
        client.StartListening();

        string? name = await client.InvokeWithParameterObjectAsync<string>("describe", new { name = "neo" });
        Assert.AreEqual("neo", name);
    }

    [TestMethod]
    public async Task SingleDtoParameter_BindsWholeParamsObject()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new PointService());
        server.StartListening();
        client.StartListening();

        // A handler with a single DTO parameter receives the whole params object when no member
        // matches the parameter name.
        int sum = await client.InvokeWithParameterObjectAsync<int>("addPoint", new { x = 2, y = 3 });
        Assert.AreEqual(5, sum);
    }

    [TestMethod]
    public async Task ByNameParams_BindToMultipleParametersByName()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("subtract", (int minuend, int subtrahend) => minuend - subtrahend);
        server.StartListening();
        client.StartListening();

        // JSON-RPC 2.0 by-name params (spec example): members are matched to parameter names
        // regardless of order.
        int result = await client.InvokeWithParameterObjectAsync<int>(
            "subtract", new { subtrahend = 23, minuend = 42 });
        Assert.AreEqual(19, result);
    }

    [TestMethod]
    public async Task ByNameParams_MissingRequiredParameter_ReportsInvalidParams()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("subtract", (int minuend, int subtrahend) => minuend - subtrahend);
        server.StartListening();
        client.StartListening();

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            () => client.InvokeWithParameterObjectAsync<int>("subtract", new { minuend = 42 }));
        Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, ex.ErrorCode);
    }

    [TestMethod]
    public async Task ByNameParams_OmittedOptionalParameter_UsesDefault()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("scale", (int value, int factor) => value * factor);
        server.StartListening();
        client.StartListening();

        int doubled = await client.InvokeWithParameterObjectAsync<int>("scale", new { value = 21, factor = 2 });
        Assert.AreEqual(42, doubled);
    }

    [TestMethod]
    public async Task AddLocalRpcTarget_HonorsAttributesDeclaredOnInterfaces()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new AnnotatedContractService());
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(1, await client.InvokeAsync<int>("Public"));
        Assert.AreEqual(2, await client.InvokeAsync<int>("contract/renamed"));
        Assert.AreEqual(4, await client.InvokeAsync<int>("Extra"));

        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("Renamed"));
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("Internal"));
    }

    [TestMethod]
    public void AddLocalRpcTarget_MethodAndIgnoreAttributesTogether_Throw()
    {
        var (client, server) = CreatePair(out _, out _);
        using var _c = client;
        using var _s = server;

        var ex = Assert.ThrowsExactly<ArgumentException>(() => server.AddLocalRpcTarget(new ConflictingAttributesService()));
        StringAssert.Contains(ex.Message, "both [JsonRpcMethod] and [JsonRpcIgnore]");

        ex = Assert.ThrowsExactly<ArgumentException>(() => server.AddLocalRpcTarget(new SplitConflictService()));
        StringAssert.Contains(ex.Message, "both [JsonRpcMethod] and [JsonRpcIgnore]");
    }

    [TestMethod]
    public async Task AddLocalRpcTarget_InterfaceScoped_ExposesOnlyContractMembers()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(typeof(IScopedContract), new ScopedService());
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(1, await client.InvokeAsync<int>("Contract"));
        Assert.AreEqual(2, await client.InvokeAsync<int>("Explicit"));
        Assert.AreEqual(3, await client.InvokeAsync<int>("Base"));
        Assert.AreEqual(5, await client.InvokeAsync<int>("scoped/renamed"));

        // Public on the class but not part of the contract.
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("NotInContract"));

        // [JsonRpcIgnore] on the implementation still hides a contract member.
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("IgnoredByImplementation"));

        // Lifetime plumbing inherited by the contract is never exposed.
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<object>("Dispose"));
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<object>("get_Value"));
    }

    [TestMethod]
    public void AddLocalRpcTarget_InterfaceScoped_RejectsInvalidContracts()
    {
        var (client, server) = CreatePair(out _, out _);
        using var _c = client;
        using var _s = server;

        Assert.ThrowsExactly<ArgumentException>(() => server.AddLocalRpcTarget(typeof(ScopedService), new ScopedService()));
        Assert.ThrowsExactly<ArgumentException>(() => server.AddLocalRpcTarget(typeof(IScopedContract), new Service()));
        Assert.ThrowsExactly<ArgumentException>(() => server.AddLocalRpcTarget(typeof(IEnumerable<>), new List<int>()));
    }

    private sealed class Service
    {
        public int Square(int value) => value * value;

        [JsonRpcMethod("greet")]
        public string Greeting(string name) => "hello " + name;

        [JsonRpcIgnore]
        public int Secret() => 42;
    }

    public interface IAnnotatedContract
    {
        int Public();

        [JsonRpcMethod("contract/renamed")]
        int Renamed();

        [JsonRpcIgnore]
        int Internal();
    }

    private sealed class AnnotatedContractService : IAnnotatedContract
    {
        public int Public() => 1;

        public int Renamed() => 2;

        public int Internal() => 3;

        public int Extra() => 4;
    }

    private sealed class ConflictingAttributesService
    {
        [JsonRpcMethod("name")]
        [JsonRpcIgnore]
        public int Method() => 1;
    }

    public interface IIgnoredContract
    {
        [JsonRpcIgnore]
        int Method();
    }

    private sealed class SplitConflictService : IIgnoredContract
    {
        [JsonRpcMethod("name")]
        public int Method() => 1;
    }

    public interface IScopedBase : IDisposable
    {
        int Base();
    }

    public interface IScopedContract : IScopedBase
    {
        int Value { get; }

        int Contract();

        int Explicit();

        int IgnoredByImplementation();

        [JsonRpcMethod("scoped/renamed")]
        int Renamed();
    }

    private sealed class ScopedService : IScopedContract
    {
        public int Value => 0;

        public int Contract() => 1;

        int IScopedContract.Explicit() => 2;

        public int Base() => 3;

        [JsonRpcIgnore]
        public int IgnoredByImplementation() => 4;

        public int Renamed() => 5;

        public int NotInContract() => 6;

        public void Dispose()
        {
        }
    }

    private sealed class Restricted : IDisposable, IAsyncDisposable
    {
        public int Visible() => 1;

        private int Hidden() => 2;

        public override string ToString() => "secret";

        public override bool Equals(object? obj) => true;

        public override int GetHashCode() => 0;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Credentials(string ApiKey)
    {
        public int Visible() => 1;
    }

    private class BaseService : System.Collections.ObjectModel.Collection<string>
    {
        public int Inherited() => 1;

        public virtual int Overridable() => 10;
    }

    private sealed class DerivedService : BaseService
    {
        public int Derived() => 2;

        public override int Overridable() => 20;
    }

    private sealed class AsyncVoidService
    {
        public int Visible() => 1;

        public async void Crash()
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class ExplicitAsyncVoidService
    {
        [JsonRpcMethod("crash")]
        public async void Crash() => await Task.Yield();
    }

    private sealed class OverloadedService
    {
        public int Add(int a, int b) => a + b;

        public int Add(int a, int b, int c) => a + b + c;
    }

    private sealed class NamedOverloadService
    {
        public int Add(int a, int b) => a + b;

        [JsonRpcMethod("add3")]
        public int Add(int a, int b, int c) => a + b + c;
    }

    private delegate int RefHandler(ref int value);

    private sealed class ByRefService
    {
        public int Visible() => 1;

        public int WithRef(ref int value) => value++;

        public bool WithOut(int input, out int value)
        {
            value = input;
            return true;
        }

        public int WithIn(in int value) => value;

        public int WithSpan(ReadOnlySpan<int> values) => values.Length;
    }

    private sealed class ExplicitByRefService
    {
        [JsonRpcMethod("withRef")]
        public int WithRef(ref int value) => value++;
    }

    private sealed class PointService
    {
        [JsonRpcMethod("addPoint")]
        public int Add(Point point) => point.X + point.Y;
    }

    public sealed class Point
    {
        public int X { get; set; }

        public int Y { get; set; }
    }
}
