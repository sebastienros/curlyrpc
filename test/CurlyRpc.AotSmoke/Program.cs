using System.Text.Json;
using System.Text.Json.Serialization;
using CurlyRpc;

// This app is a compile/publish smoke test: it exercises the source-generated client proxy and the
// JsonTypeInfo-based serialization path under Native AOT. It must publish with zero trim/AOT warnings.

var options = new JsonSerializerOptions
{
    TypeInfoResolver = AotSmokeContext.Default,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

var stream = new MemoryStream();
await using var rpc = new JsonRpc(stream, new JsonRpcOptions { SerializerOptions = options });

// Exercise the AOT-safe strongly-typed local method registration (the shape Aspire's Native AOT
// CLI uses for its callback target). These must publish with zero trim/AOT warnings.
rpc.AddLocalRpcMethod("getCliVersion", () => Task.FromResult("9.0.0"));
rpc.AddLocalRpcMethod<Point, string>("describePoint", p => Task.FromResult($"{p.X},{p.Y}"));
rpc.AddLocalRpcMethod("stopCli", () => Task.CompletedTask);

ICalculator calculator = rpc.CreateICalculatorProxy();

// Fire the generated proxy methods to keep their bodies reachable for the AOT analyzer.
// The connection is not listening, so the returned tasks intentionally never complete.
_ = calculator.AddAsync(2, 3);
_ = calculator.DescribeAsync(new Point(1, 2));

Console.WriteLine("CurlyRpc AOT smoke OK");

[JsonRpcProxy]
public interface ICalculator
{
    Task<int> AddAsync(int a, int b, CancellationToken cancellationToken = default);

    Task<string> DescribeAsync(Point point);

    IAsyncEnumerable<int> CountAsync(int count, CancellationToken cancellationToken = default);
}

public sealed record Point(int X, int Y);

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Point))]
internal sealed partial class AotSmokeContext : JsonSerializerContext
{
}
