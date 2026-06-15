namespace CurlyRpc.Tests.Harness;

/// <summary>
/// Creates a connected pair of <see cref="JsonRpc"/> peers over in-memory pipes, for full-duplex
/// integration testing without any real transport.
/// </summary>
internal static class DuplexConnection
{
    public static (HeaderDelimitedMessageHandler First, HeaderDelimitedMessageHandler Second) CreateHandlerPair()
    {
        var pipe1 = new PipeStream();
        var pipe2 = new PipeStream();
        var first = new HeaderDelimitedMessageHandler(sendStream: pipe1, receiveStream: pipe2);
        var second = new HeaderDelimitedMessageHandler(sendStream: pipe2, receiveStream: pipe1);
        return (first, second);
    }
}
