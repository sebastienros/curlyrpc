using System.Diagnostics;
using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class TraceContextPropagationTests
{
    private const string TestSourceName = "CurlyRpc.Tests.TraceContext";
    private static readonly ActivitySource s_testSource = new(TestSourceName);

    private static JsonRpcOptions Options(bool propagate)
        => new()
        {
            SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
            PropagateTraceContext = propagate,
        };

    private (JsonRpc Client, JsonRpc Server) CreatePair(bool propagate)
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        return (new JsonRpc(h1, Options(propagate)), new JsonRpc(h2, Options(propagate)));
    }

    private static ActivityListener ListenAll(List<Activity> sink)
    {
        // Force W3C ids so traceparent has a valid representation regardless of ambient host configuration.
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == JsonRpcDiagnostics.SourceName || source.Name == TestSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = sink.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [TestMethod]
    public async Task Invoke_WithPropagationEnabled_LinksServerSpanToClientTrace()
    {
        var activities = new List<Activity>();
        using var listener = ListenAll(activities);

        var (client, server) = CreatePair(propagate: true);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("square", (int x) => x * x);
        server.StartListening();
        client.StartListening();

        int result = await client.InvokeAsync<int>("square", 6);
        Assert.AreEqual(36, result);

        Activity clientActivity = activities.Single(a => a.Kind == ActivityKind.Client && a.DisplayName == "square");
        Activity serverActivity = activities.Single(a => a.Kind == ActivityKind.Server && a.DisplayName == "square");

        Assert.AreEqual(clientActivity.TraceId, serverActivity.TraceId);
        Assert.AreEqual(clientActivity.SpanId, serverActivity.ParentSpanId);
        Assert.IsTrue(serverActivity.HasRemoteParent);
    }

    [TestMethod]
    public async Task Invoke_WithPropagationDisabled_ServerSpanHasNoRemoteParent()
    {
        var activities = new List<Activity>();
        using var listener = ListenAll(activities);

        var (client, server) = CreatePair(propagate: false);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("square", (int x) => x * x);
        server.StartListening();
        client.StartListening();

        _ = await client.InvokeAsync<int>("square", 6);

        Activity serverActivity = activities.Single(a => a.Kind == ActivityKind.Server && a.DisplayName == "square");
        Assert.IsFalse(serverActivity.HasRemoteParent);
        Assert.AreEqual(default, serverActivity.ParentSpanId);
    }

    [TestMethod]
    public async Task Invoke_WithPropagationEnabled_PropagatesTraceState()
    {
        var activities = new List<Activity>();
        using var listener = ListenAll(activities);

        var (client, server) = CreatePair(propagate: true);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("square", (int x) => x * x);
        server.StartListening();
        client.StartListening();

        // The CurlyRpc client span is created as a child of this outer span, inheriting its tracestate,
        // which is then carried across the wire and restored on the server span.
        using (Activity outer = s_testSource.StartActivity("outer", ActivityKind.Internal)!)
        {
            outer.TraceStateString = "vendor=abc123";
            _ = await client.InvokeAsync<int>("square", 6);
        }

        Activity serverActivity = activities.Single(a => a.Kind == ActivityKind.Server && a.DisplayName == "square");
        Assert.AreEqual("vendor=abc123", serverActivity.TraceStateString);
    }

    [TestMethod]
    public async Task Notify_WithPropagationEnabled_LinksServerSpanToCallerTrace()
    {
        var activities = new List<Activity>();
        using var listener = ListenAll(activities);

        var (client, server) = CreatePair(propagate: true);
        await using var _c = client;
        await using var _s = server;

        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("notice", () => handled.TrySetResult());
        server.StartListening();
        client.StartListening();

        ActivityTraceId callerTraceId;
        ActivitySpanId callerSpanId;
        using (Activity outer = s_testSource.StartActivity("caller", ActivityKind.Internal)!)
        {
            callerTraceId = outer.TraceId;
            callerSpanId = outer.SpanId;
            await client.NotifyAsync("notice");
            await handled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Activity serverActivity = activities.Single(a => a.Kind == ActivityKind.Server && a.DisplayName == "notice");
        Assert.AreEqual(callerTraceId, serverActivity.TraceId);
        Assert.AreEqual(callerSpanId, serverActivity.ParentSpanId);
        Assert.IsTrue(serverActivity.HasRemoteParent);
    }
}
