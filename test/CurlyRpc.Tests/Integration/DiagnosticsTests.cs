using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class DiagnosticsTests
{
    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    private static (JsonRpc Client, JsonRpc Server) CreatePair()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        return (new JsonRpc(h1, Options()), new JsonRpc(h2, Options()));
    }

    [TestMethod]
    public async Task Invoke_EmitsClientAndServerActivities()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == JsonRpcDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("square", (int x) => x * x);
        server.StartListening();
        client.StartListening();

        int result = await client.InvokeAsync<int>("square", 6);
        Assert.AreEqual(36, result);

        Assert.IsTrue(activities.Any(a => a.Kind == ActivityKind.Client && a.DisplayName == "square"));
        Assert.IsTrue(activities.Any(a => a.Kind == ActivityKind.Server && a.DisplayName == "square"));
    }

    [TestMethod]
    public async Task Invoke_RecordsClientDurationMetric()
    {
        var measurements = new List<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == JsonRpcDiagnostics.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
        {
            lock (measurements)
            {
                measurements.Add(instrument.Name);
            }
        });
        meterListener.Start();

        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("ping", () => "pong");
        server.StartListening();
        client.StartListening();

        _ = await client.InvokeAsync<string>("ping");
        meterListener.Dispose();

        lock (measurements)
        {
            Assert.IsTrue(measurements.Contains("rpc.client.duration"));
            Assert.IsTrue(measurements.Contains("rpc.server.duration"));
        }
    }
}
