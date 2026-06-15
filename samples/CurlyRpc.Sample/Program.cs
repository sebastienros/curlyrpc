using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CurlyRpc;

// A self-contained, runnable sample: two JsonRpc peers talk over a TCP loopback connection,
// demonstrating handshake authentication, a typed generated proxy, request/response, and streaming.

const string sharedSecret = "correct horse battery staple";

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;

Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

using var clientConnection = new TcpClient();
await clientConnection.ConnectAsync(IPAddress.Loopback, port);
using TcpClient serverConnection = await acceptTask;

var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// ---- Server peer -------------------------------------------------------------------------------
await using var server = new JsonRpc(serverConnection.GetStream(), new JsonRpcOptions
{
    SerializerOptions = serializerOptions,
    InboundMiddleware = new HandshakeAuthenticationMiddleware(sharedSecret),
});
server.AddLocalRpcTarget(new CalculatorService());
server.StartListening();

// ---- Client peer -------------------------------------------------------------------------------
await using var client = new JsonRpc(clientConnection.GetStream(), new JsonRpcOptions
{
    SerializerOptions = serializerOptions,
});
client.StartListening();

// Liveness probe is allowed before authentication.
Console.WriteLine($"ping -> {await client.InvokeAsync<bool>("ping")}");

// Authenticate, then use a generated, strongly-typed proxy.
Console.WriteLine($"authenticate -> {await client.InvokeAsync<bool>("authenticate", sharedSecret)}");

ICalculator calculator = client.CreateICalculatorProxy();
Console.WriteLine($"add(20, 22) -> {await calculator.AddAsync(20, 22)}");

Console.Write("count(5) -> ");
await foreach (int value in calculator.CountAsync(5))
{
    Console.Write($"{value} ");
}

Console.WriteLine();
Console.WriteLine("Done.");

[JsonRpcProxy]
public interface ICalculator
{
    Task<int> AddAsync(int a, int b, CancellationToken cancellationToken = default);

    IAsyncEnumerable<int> CountAsync(int count, CancellationToken cancellationToken = default);
}

public sealed class CalculatorService
{
    public int AddAsync(int a, int b) => a + b;

    public async IAsyncEnumerable<int> CountAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return i;
        }
    }
}
