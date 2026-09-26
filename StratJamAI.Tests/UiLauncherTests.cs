using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StratJamAI.Training;
using Xunit;

namespace StratJamAI.Tests;

public sealed class UiLauncherTests
{
    [Theory]
    [InlineData("--max-searches", "0")]
    [InlineData("--max-sessions", "0")]
    [InlineData("--max-searches", "65")]
    [InlineData("--max-sessions", "4097")]
    [InlineData("--move-ms", "20001")]
    public async Task PublicUiLimitsFailBeforeStartingTheHost(string option, string value)
    {
        var result = await Run(null, "ui", "--public", "--share", option, value);
        Assert.Equal(1, result.Code);
        Assert.Contains(option + " must be between", result.Output);
    }

    [Theory]
    [InlineData("{\"application\":\"StratJamAI.Enclosure\",\"protocolVersion\":1,\"publicAccess\":false}")]
    [InlineData("{\"application\":\"StratJamAI.Enclosure\",\"protocolVersion\":2,\"publicAccess\":true}")]
    [InlineData("{\"application\":\"StratJamAI.Enclosure\",\"protocolVersion\":\"2\",\"publicAccess\":false}")]
    [InlineData("<html>old single-session game</html>")]
    public async Task ShareRejectsOldOrIncompatibleServersBeforeStartingATunnel(string info)
    {
        await using var server = new InfoServer(info);
        var result = await Run(null, "ui", "--share", "--no-browser", "--port", server.Port.ToString());
        Assert.Equal(1, result.Code);
        Assert.Contains("older or incompatible UI", result.Output);
        Assert.Contains("protocol version 2", result.Output);
        Assert.DoesNotContain("Starting the public link", result.Output);
    }

    [Fact]
    public async Task SharingReusesPrivateVersionTwoServerAndRelaysThePublicLink()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var executable = Path.Combine(directory.Path, "cloudflared");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s\\n' 'https://fixture-link.trycloudflare.com'\nexit 7\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var server = new InfoServer("{\"application\":\"StratJamAI.Enclosure\",\"protocolVersion\":2,\"publicAccess\":false}");
        var result = await Run(directory.Path, "ui", "--share", "--no-browser", "--port", server.Port.ToString(), "--move-ms", "20000");
        Assert.Equal(7, result.Code);
        Assert.Contains("already running", result.Output);
        Assert.Contains("Public Enclosure link: https://fixture-link.trycloudflare.com", result.Output);
        Assert.Contains("public tunnel stopped", result.Output);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        Assert.Contains("protocolVersion", await client.GetStringAsync($"http://127.0.0.1:{server.Port}/api/info"));
    }

    [Fact]
    public async Task StoppingTheLauncherTerminatesItsTunnelButPreservesTheReusedHost()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var executable = Path.Combine(directory.Path, "cloudflared");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s\\n' $$ >&2\nexec /bin/sleep 60\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var server = new InfoServer("{\"application\":\"StratJamAI.Enclosure\",\"protocolVersion\":2,\"publicAccess\":false}");
        using var process = Start(directory.Path, "ui", "--share", "--no-browser", "--port", server.Port.ToString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var line = await process.StandardError.ReadLineAsync(timeout.Token);
            Assert.True(int.TryParse(line, out var tunnelPid), line);
            using var signal = Process.Start(new ProcessStartInfo("/bin/kill")
            { ArgumentList = { "-TERM", process.Id.ToString() }, UseShellExecute = false })!;
            await signal.WaitForExitAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(130, process.ExitCode);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(tunnelPid));
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            Assert.Contains("protocolVersion", await client.GetStringAsync($"http://127.0.0.1:{server.Port}/api/info"));
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    [Fact]
    public async Task HelpDocumentsSharingWithoutRequiringPublicBinding()
    {
        var result = await Run(null, "help");
        Assert.Equal(0, result.Code);
        Assert.Contains("--share works with the default loopback server", result.Output);
        Assert.Contains("--max-searches 4", result.Output);
        Assert.Contains("--max-sessions 128", result.Output);
    }

    private static async Task<(int Code, string Output)> Run(string? executablePath, params string[] arguments)
    {
        using var process = Start(executablePath, arguments);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await output + await errors);
    }

    private static Process Start(string? executablePath, params string[] arguments)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(typeof(TrainingConfig).Assembly.Location);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (executablePath is not null) info.Environment["PATH"] = executablePath;
        return Process.Start(info)!;
    }

    private sealed class InfoServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task loop;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public InfoServer(string info)
        {
            listener.Start();
            loop = Task.Run(async () =>
            {
                while (!stopping.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stopping.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    while (await reader.ReadLineAsync(stopping.Token) is { Length: > 0 }) { }
                    var body = Encoding.UTF8.GetBytes(info);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, stopping.Token);
                    await stream.WriteAsync(body, stopping.Token);
                }
            });
        }
        public async ValueTask DisposeAsync()
        {
            stopping.Cancel(); listener.Stop();
            try { await loop; }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException) { }
            stopping.Dispose();
        }
    }
}
