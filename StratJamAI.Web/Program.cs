using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using StratJamAI.Web;

var port = 5080;
double moveMilliseconds = 1000;
var openBrowser = true;
var publicAccess = false;
var maxSearches = 4;
var maxSessions = 128;
try
{
    for (var i = 0; i < args.Length; i++)
    {
        string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value after {args[i - 1]}.");
        switch (args[i])
        {
            case "--port": port = int.Parse(Value(), CultureInfo.InvariantCulture); break;
            case "--move-ms": moveMilliseconds = double.Parse(Value(), CultureInfo.InvariantCulture); break;
            case "--no-browser": openBrowser = false; break;
            case "--public": publicAccess = true; break;
            case "--max-searches": maxSearches = int.Parse(Value(), CultureInfo.InvariantCulture); break;
            case "--max-sessions": maxSessions = int.Parse(Value(), CultureInfo.InvariantCulture); break;
            default: throw new ArgumentException($"Unknown option: {args[i]}");
        }
    }
    if (port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
    if (!double.IsFinite(moveMilliseconds) || moveMilliseconds is < 50 or > 20000)
        throw new ArgumentException("Thinking time must be between 50 and 20,000 milliseconds per AI turn.");
    if (maxSearches is < 1 or > 64) throw new ArgumentException("--max-searches must be between 1 and 64.");
    if (maxSessions is < 1 or > 4096) throw new ArgumentException("--max-sessions must be between 1 and 4096.");
}
catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("Usage: StratJamAI.Web [--port 5080] [--move-ms 1000] [--no-browser] [--public] [--max-searches 4] [--max-sessions 128]");
    return 2;
}

var url = $"http://127.0.0.1:{port}";
if (await LocalUiInstance.TryOpenExistingAsync(url, openBrowser, publicAccess)) return 0;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
builder.WebHost.UseUrls(publicAccess ? $"http://0.0.0.0:{port}" : url);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});
builder.Services.AddSingleton(_ => new WebGameSessions(moveMilliseconds, maxSessions, maxSearches));
builder.Services.AddSingleton(_ => new PvpGames(maxRooms: maxSessions));
builder.Services.AddSingleton(provider => new GameReviews(provider.GetRequiredService<WebGameSessions>().SearchSlots));
// The pool owns the disposable session. A non-disposable request wrapper prevents
// scoped DI cleanup from cancelling the game at the end of every HTTP request.
builder.Services.AddScoped(provider => new GameRequestSession(
    (WebGameSession)provider.GetRequiredService<IHttpContextAccessor>().HttpContext!.Items["game-session"]!));
builder.Services.AddHttpContextAccessor();
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (context.Items["game-session-id"] is not string id)
            return RateLimitPartition.GetNoLimiter("anonymous");
        var mutation = HttpMethods.IsPost(context.Request.Method);
        return RateLimitPartition.GetFixedWindowLimiter(id + (mutation ? ":write" : ":read"), _ => new()
        {
            PermitLimit = mutation ? 20 : 120,
            Window = TimeSpan.FromSeconds(10),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.OnRejected = async (context, cancellation) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers.RetryAfter = "10";
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests. Please wait a few seconds and try again." }, cancellation);
    };
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();
app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.CacheControl = "no-store";
    else
        context.Response.Headers.CacheControl = "no-cache";
    if (!WebRequestPolicy.AllowsMutation(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Open the game in its own browser page to play." });
        return;
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();

// Only game endpoints allocate a session; health checks and static assets do not consume slots.
var gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "/api/state", "/api/new", "/api/settings", "/api/move", "/api/think", "/api/hint", "/api/undo", "/api/import", "/api/export", "/api/sync" };
var cookieName = $"enclosure-session-{port}";
app.Use(async (context, next) =>
{
    var multiplayer = context.Request.Path.StartsWithSegments("/api/pvp");
    if (!multiplayer && !gamePaths.Contains(context.Request.Path.Value ?? "")) { await next(context); return; }
    try
    {
        // A companion popup analyzes its own position without replacing the visitor's play game.
        var sessionCookieName = !multiplayer && context.Request.Headers["X-Enclosure-Coach"] == "1"
            ? cookieName + "-coach" : cookieName;
        using var lease = context.RequestServices.GetRequiredService<WebGameSessions>()
            .Acquire(context.Request.Cookies[sessionCookieName]);
        context.Items["game-session"] = lease.Session;
        context.Items["game-session-id"] = lease.Id;
        // This public fingerprint lets the page recognize a new game after expiry/restart
        // without exposing the bearer cookie to JavaScript.
        context.Response.Headers["X-Enclosure-Session"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lease.Id)))[..32];
        if (lease.IsNew)
            context.Response.Cookies.Append(sessionCookieName, lease.Id, new CookieOptions
            {
                HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
                Path = "/", IsEssential = true
            });
        await next(context);
    }
    catch (SessionCapacityException)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "30";
        await context.Response.WriteAsJsonAsync(new { error = "The server is full right now. Please try again shortly." });
    }
});
app.UseRateLimiter();

static IResult Invoke(Func<BoardState> operation)
{
    try { return Results.Ok(operation()); }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
    {
        return Results.Json(new { error = exception.Message }, statusCode:
            exception is InvalidOperationException ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
    }
}

app.MapGet("/api/state", (GameRequestSession game) => Results.Ok(game.Session.State));
app.MapGet("/api/info", () => Results.Ok(new
{
    application = "StratJamAI.Enclosure", protocolVersion = 2, publicAccess, maxSearches, maxSessions,
    engine = "alpha-beta-defense-v4", turnThinking = true, maxThinkingMilliseconds = 20000,
    hostingVersion = "pvp-v1",
    features = new[] { "live-coach", "analysis", "history-sync", "whole-turn-search", "pvp", "quick-play", "lobby-codes", "pvp-clocks", "post-game-review" }
}));
app.MapPost("/api/new", (NewGameRequest request, GameRequestSession game) =>
    Invoke(() => game.Session.NewGame(request.HumanPlayer, request.MoveMilliseconds, request.AnalysisMode, request.LiveCoach)));
app.MapPost("/api/settings", (SettingsRequest request, GameRequestSession game) =>
    Invoke(() => game.Session.Settings(request.MoveMilliseconds, request.LiveCoach, request.AnalysisMode)));
app.MapPost("/api/move", (MoveRequest request, GameRequestSession game) =>
    Invoke(() => game.Session.Move(request.Revision, request.Action)));
app.MapPost("/api/think", (RevisionRequest request, GameRequestSession game) =>
    Invoke(() => game.Session.StartSearch(request.Revision)));
app.MapPost("/api/hint", (RevisionRequest request, GameRequestSession game) =>
    Invoke(() => game.Session.StartSearch(request.Revision, isHint: true)));
app.MapPost("/api/undo", (RevisionRequest request, GameRequestSession game) =>
    Invoke(() => game.Session.Undo(request.Revision)));
app.MapPost("/api/import", (ImportRequest request, GameRequestSession game) =>
    Invoke(() => game.Session.Import(request.History)));
app.MapPost("/api/sync", (SyncRequest request, GameRequestSession game) =>
    Invoke(() => game.Session.Synchronize(request.Revision, request.Actions, request.History)));
app.MapGet("/api/export", (GameRequestSession game) =>
    Results.File(Encoding.UTF8.GetBytes(game.Session.Export()), "application/json", "enclosure-game.json"));
app.MapPvpEndpoints();

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"Play Enclosure: {url}");
    Console.WriteLine($"Online PvP and private lobbies: {url}/pvp.html");
    if (publicAccess) Console.WriteLine($"LAN hosting enabled on port {port}. Other players can use http://YOUR-LAN-IP:{port}.");
    Console.WriteLine($"Up to {maxSessions} browser sessions and {maxSearches} simultaneous AI searches across play, coaching, and reviews.");
    Console.WriteLine("Press Ctrl+C to stop the local server.");
    if (!openBrowser) return;
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception exception)
    {
        Console.WriteLine($"Open the address above in your browser. ({exception.Message})");
    }
});
try { await app.RunAsync(); }
catch (IOException exception) when (exception.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
{
    // Another launch can win the port between our initial check and Kestrel binding.
    if (await LocalUiInstance.TryOpenExistingAsync(url, openBrowser, publicAccess)) return 0;
    Console.Error.WriteLine($"Port {port} is already used by another application or an older/different hosting mode. Stop that server or start this UI with --port {(port == 65535 ? 5081 : port + 1)}.");
    return 1;
}
return 0;

public sealed record NewGameRequest(int HumanPlayer = 0, double MoveMilliseconds = 1000,
    bool AnalysisMode = false, bool LiveCoach = false);
public sealed record SettingsRequest(double MoveMilliseconds, bool? LiveCoach = null, bool? AnalysisMode = null);
public sealed record MoveRequest(long Revision, int Action);
public sealed record RevisionRequest(long Revision);
public sealed record ImportRequest(string History);
public sealed record SyncRequest(long Revision, int[]? Actions = null, string? History = null);
public sealed record GameRequestSession(WebGameSession Session);
