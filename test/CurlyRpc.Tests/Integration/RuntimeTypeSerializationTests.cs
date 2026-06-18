using System.Text.Json;
using System.Text.Json.Serialization;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

/// <summary>
/// Positional/by-name arguments are serialized by their <em>runtime</em> type, so under a
/// source-generated <see cref="JsonSerializerContext"/> a concrete runtime type that was never
/// registered (a lazy LINQ projection, an iterator result, an anonymous type) has no metadata and
/// cannot be serialized. These tests lock in the actionable <see cref="NotSupportedException"/> raised
/// in that case and verify that materializing to a registered type works.
/// </summary>
[TestClass]
public sealed partial class RuntimeTypeSerializationTests
{
    private static JsonRpcOptions SourceGenOptions()
        => new()
        {
            SerializerOptions = new JsonSerializerOptions
            {
                TypeInfoResolver = SerializationContext.Default,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            },
        };

    [TestMethod]
    public async Task InvokeAsync_LazyProjectionArgument_ThrowsActionableNotSupported()
    {
        var (h1, _) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, SourceGenOptions());
        client.StartListening();

        var source = new[] { 1, 2, 3 };
        // A lazy Select iterator: declared IEnumerable<Item> but a compiler-generated runtime type that
        // the source-gen context cannot know about.
        IEnumerable<Item> lazy = source.Select(n => new Item(n));

        var ex = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            async () => await client.InvokeAsync<int>("display", new object?[] { lazy }, CancellationToken.None));

        // The message must name the method, the argument index, and point at the fix.
        StringAssert.Contains(ex.Message, "'display'");
        StringAssert.Contains(ex.Message, "index 0");
        StringAssert.Contains(ex.Message, ".ToArray()");
        Assert.IsNotNull(ex.InnerException, "The original serializer exception should be preserved.");
    }

    [TestMethod]
    public async Task InvokeAsync_MaterializedArrayArgument_SerializesAndDispatches()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, SourceGenOptions());
        await using var server = new JsonRpc(h2, SourceGenOptions());

        Item[]? received = null;
        server.AddLocalRpcMethod("display", (Item[] items) =>
        {
            received = items;
            return items.Length;
        });
        server.StartListening();
        client.StartListening();

        // Materializing the lazy projection to the registered Item[] type is the documented fix.
        Item[] items = new[] { 1, 2, 3 }.Select(n => new Item(n)).ToArray();
        int count = await client.InvokeAsync<int>("display", new object?[] { items }, CancellationToken.None);

        Assert.AreEqual(3, count);
        Assert.IsNotNull(received);
        Assert.AreEqual(3, received!.Length);
        Assert.AreEqual(2, received[1].Value);
    }

    public sealed record Item(int Value);

    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(Item))]
    [JsonSerializable(typeof(Item[]))]
    [JsonSerializable(typeof(IEnumerable<Item>))]
    internal sealed partial class SerializationContext : JsonSerializerContext
    {
    }
}
