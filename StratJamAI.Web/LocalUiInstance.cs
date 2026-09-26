using System.Diagnostics;
using System.Text.Json;

namespace StratJamAI.Web;

public static class LocalUiInstance
{
    public static async Task<bool> TryOpenExistingAsync(string url, bool openBrowser, bool? publicAccess = null)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1), MaxResponseContentBufferSize = 64 * 1024 };
            using var response = await client.GetAsync(url.TrimEnd('/') + (publicAccess.HasValue ? "/api/info" : "/api/export"));
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (publicAccess.HasValue)
            {
                if (!root.TryGetProperty("application", out var application) || application.ValueKind != JsonValueKind.String ||
                    application.GetString() != "StratJamAI.Enclosure" ||
                    !root.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.Number ||
                    !protocol.TryGetInt32(out var protocolVersion) || protocolVersion != 2 ||
                    !root.TryGetProperty("publicAccess", out var access) || access.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    access.GetBoolean() != publicAccess.Value) return false;
            }
            else if (
                !root.TryGetProperty("game", out var game) || game.ValueKind != JsonValueKind.String || game.GetString() != "enclosure" ||
                !root.TryGetProperty("formatVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var formatVersion) || formatVersion != 1 ||
                !root.TryGetProperty("moves", out var moves) || moves.ValueKind != JsonValueKind.Array)
                return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or ArgumentException)
        {
            return false;
        }

        Console.WriteLine($"Enclosure is already running: {url}");
        if (openBrowser)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception exception) { Console.WriteLine($"Open the address above in your browser. ({exception.Message})"); }
        }
        return true;
    }
}
