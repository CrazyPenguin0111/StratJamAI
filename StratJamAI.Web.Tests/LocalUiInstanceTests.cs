using System.Net;
using System.Net.Sockets;
using System.Text;
using StratJamAI.Web;
using Xunit;

namespace StratJamAI.Web.Tests;

public sealed class LocalUiInstanceTests
{
    [Theory]
    [InlineData("{\"game\":\"enclosure\",\"formatVersion\":1,\"moves\":[]}", true)]
    [InlineData("{\"game\":\"another-game\",\"formatVersion\":1,\"moves\":[]}", false)]
    [InlineData("{\"game\":\"enclosure\",\"formatVersion\":2,\"moves\":[]}", false)]
    [InlineData("{\"game\":\"enclosure\",\"formatVersion\":1,\"moves\":null}", false)]
    [InlineData("<html>Unrelated local service</html>", false)]
    public async Task ReusesOnlyCompatibleEnclosureService(string body, bool expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reply = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var request = new byte[4096];
            _ = await stream.ReadAsync(request);
            var payload = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(payload);
        });
        Assert.Equal(expected, await LocalUiInstance.TryOpenExistingAsync($"http://127.0.0.1:{port}", false));
        await reply.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
