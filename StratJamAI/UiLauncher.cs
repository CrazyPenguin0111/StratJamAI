using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StratJamAI;

internal static class UiLauncher
{
    private const string CloudflaredInstall = "https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/downloads/";

    public static async Task<int> Run(int port, double milliseconds, bool noBrowser,
        bool publicAccess = false, bool share = false, int maxSearches = 4, int maxSessions = 128)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("--port must be between 1 and 65535.");
        if (!double.IsFinite(milliseconds) || milliseconds is < 50 or > 20000)
            throw new ArgumentException("--move-ms must be between 50 and 20000 per AI turn.");
        if (maxSearches is < 1 or > 64) throw new ArgumentException("--max-searches must be between 1 and 64.");
        if (maxSessions is < 1 or > 4096) throw new ArgumentException("--max-sessions must be between 1 and 4096.");
        var url = $"http://127.0.0.1:{port}";
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            context => { context.Cancel = true; cancellation.Cancel(); });
        Process? server = null, tunnel = null;
        Task? output = null, errors = null;
        try
        {
            var existing = share ? await Probe(url, publicAccess, cancellation.Token) : default;
            if (existing.Present && !existing.Compatible) throw Incompatible(port);
            var executable = share ? FindCloudflared() : null;
            if (!existing.Compatible)
            {
                server = Process.Start(ServerStart(port, milliseconds, noBrowser, publicAccess, maxSearches, maxSessions))
                    ?? throw new InvalidOperationException("Unable to launch the UI host.");
                if (!share)
                {
                    await server.WaitForExitAsync(cancellation.Token);
                    return server.ExitCode;
                }
                await WaitUntilReady(url, publicAccess, server, port, cancellation.Token);
                // A competing launcher may already have started this same version and our child exited.
                // Its existing server is intentionally not owned by this launcher.
                if (server.HasExited)
                {
                    server.Dispose(); server = null;
                }
            }
            else Console.WriteLine($"Sharing the Enclosure server already running at {url}.");

            var start = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("tunnel"); start.ArgumentList.Add("--no-autoupdate");
            start.ArgumentList.Add("--url"); start.ArgumentList.Add(url);
            tunnel = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch cloudflared.");
            output = Relay(tunnel.StandardOutput);
            errors = Relay(tunnel.StandardError);
            Console.WriteLine("Starting the public link. Keep this command running; press Ctrl+C to close it.");
            var tunnelExit = tunnel.WaitForExitAsync(cancellation.Token);
            if (server is not null)
            {
                var serverExit = server.WaitForExitAsync(cancellation.Token);
                if (await Task.WhenAny(tunnelExit, serverExit) == serverExit)
                {
                    await serverExit;
                    if (server.ExitCode == 0 && (await Probe(url, publicAccess, cancellation.Token)).Compatible)
                    {
                        server.Dispose(); server = null; // Another launcher won the startup race.
                    }
                    else
                    {
                        Console.Error.WriteLine("The UI host stopped; closing the public tunnel.");
                        return server.ExitCode == 0 ? 1 : server.ExitCode;
                    }
                }
            }
            await tunnelExit;
            await Task.WhenAll(output, errors);
            Console.Error.WriteLine($"The public tunnel stopped (exit code {tunnel.ExitCode}).");
            return tunnel.ExitCode == 0 ? 1 : tunnel.ExitCode;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
        finally
        {
            Console.CancelKeyPress -= handler;
            await Stop(tunnel); await Stop(server);
            if (output is not null && errors is not null) await Task.WhenAll(output, errors);
            tunnel?.Dispose(); server?.Dispose();
        }
    }

    private static ProcessStartInfo ServerStart(int port, double milliseconds, bool noBrowser, bool publicAccess,
        int maxSearches, int maxSessions)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ui", "StratJamAI.Web.dll");
        if (!File.Exists(path)) throw new FileNotFoundException("UI host is missing. Build StratJamAI.sln in Release configuration first.", path);
        var privateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        var privateHost = Path.Combine(privateRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var aspNet = Path.Combine(privateRoot, "shared", "Microsoft.AspNetCore.App");
        var host = File.Exists(privateHost) && Directory.Exists(aspNet) &&
            Directory.EnumerateDirectories(aspNet, "10.*").Any() ? privateHost : "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("--port"); start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--move-ms"); start.ArgumentList.Add(milliseconds.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--max-searches"); start.ArgumentList.Add(maxSearches.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--max-sessions"); start.ArgumentList.Add(maxSessions.ToString(CultureInfo.InvariantCulture));
        if (noBrowser) start.ArgumentList.Add("--no-browser");
        if (publicAccess) start.ArgumentList.Add("--public");
        return start;
    }

    private static string FindCloudflared()
    {
        var name = OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared";
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        foreach (var directory in directories)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            if (OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) &
                (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return Path.GetFullPath(path);
        }
        throw new FileNotFoundException($"--share requires cloudflared. Install it and put it on PATH or in ~/.local/bin, then rerun this command. Official downloads: {CloudflaredInstall}");
    }

    private static async Task<(bool Present, bool Compatible)> Probe(string url, bool publicAccess, CancellationToken token)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1), MaxResponseContentBufferSize = 16384 };
            using var response = await client.GetAsync(url + "/api/info", token);
            if (!response.IsSuccessStatusCode) return (true, false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement;
            var compatible = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("application", out var application) && application.ValueKind == JsonValueKind.String &&
                application.GetString() == "StratJamAI.Enclosure" &&
                root.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.Number &&
                version.TryGetInt32(out var number) && number == 2 &&
                root.TryGetProperty("publicAccess", out var mode) && mode.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                mode.GetBoolean() == publicAccess;
            return (true, compatible);
        }
        catch (JsonException) { return (true, false); }
        catch (HttpRequestException) { return (false, false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (false, false); }
    }

    private static async Task WaitUntilReady(string url, bool publicAccess, Process server, int port, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            token.ThrowIfCancellationRequested();
            var status = await Probe(url, publicAccess, token);
            if (status.Compatible) return;
            if (status.Present) throw Incompatible(port);
            if (server.HasExited) throw new InvalidOperationException($"UI host exited before becoming ready (exit code {server.ExitCode}).");
            await Task.Delay(100, token);
        }
        throw new TimeoutException("The UI host did not become ready; no public tunnel was started.");
    }

    private static InvalidOperationException Incompatible(int port) => new(
        $"Port {port} serves an older or incompatible UI, or a different --public setting. Stop that UI and restart the current build, or use --port {(port == 65535 ? 5081 : port + 1)}. Sharing requires the multi-session UI protocol version 2.");

    private static async Task Relay(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            Console.Error.WriteLine(line);
            var match = Regex.Match(line, @"https://[a-zA-Z0-9-]+\.trycloudflare\.com", RegexOptions.CultureInvariant);
            if (match.Success) Console.WriteLine($"Public Enclosure link: {match.Value}");
        }
    }

    private static async Task Stop(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        await process.WaitForExitAsync();
    }
}
