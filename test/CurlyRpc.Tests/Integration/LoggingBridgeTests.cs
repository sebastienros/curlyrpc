using System.Text.Json;
using CurlyRpc.Extensions.Logging;
using CurlyRpc.Tests.Harness;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class LoggingBridgeTests
{
    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    [TestMethod]
    public async Task Bridge_LogsCompletedCall()
    {
        var logger = new CapturingLogger();
        using var bridge = new JsonRpcLoggingBridge(logger);

        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, Options());
        await using var server = new JsonRpc(h2, Options());

        server.AddLocalRpcMethod("echo", (string s) => s);
        server.StartListening();
        client.StartListening();

        _ = await client.InvokeAsync<string>("echo", "hi");

        Assert.IsTrue(logger.Entries.Any(e => e.Contains("echo")));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
