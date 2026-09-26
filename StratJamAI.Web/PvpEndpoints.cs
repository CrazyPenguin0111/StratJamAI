using System.Text;

namespace StratJamAI.Web;

/// <summary>Browser identity comes only from the server-issued session cookie.</summary>
public static class PvpEndpoints
{
    public static void MapPvpEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pvp");
        group.MapGet("/state", (HttpContext context, PvpGames games, string? code) =>
            Invoke(() => games.State(Player(context), code)));
        group.MapPost("/create", (HttpContext context, PvpGames games, PvpCreateRequest request) =>
            Invoke(() => games.Create(Player(context), request.Settings, request.ColorPreference, request.DisplayName)));
        group.MapPost("/quick", (HttpContext context, PvpGames games, PvpCreateRequest request) =>
            Invoke(() => games.Quick(Player(context), request.Settings, request.ColorPreference, request.DisplayName)));
        group.MapPost("/join", (HttpContext context, PvpGames games, PvpJoinRequest request) =>
            Invoke(() => games.Join(Player(context), request.Code, request.ColorPreference, request.DisplayName)));
        group.MapPost("/{code}/settings", (HttpContext context, PvpGames games, string code, PvpLobbySettingsRequest request) =>
            Invoke(() => games.Settings(Player(context), code, request.Revision, request.Settings,
                request.ColorPreference, request.DisplayName)));
        group.MapPost("/{code}/ready", (HttpContext context, PvpGames games, string code, PvpReadyRequest request) =>
            Invoke(() => games.Ready(Player(context), code, request.Revision, request.Ready)));
        group.MapPost("/{code}/move", (HttpContext context, PvpGames games, string code, MoveRequest request) =>
            Invoke(() => games.Move(Player(context), code, request.Revision, request.Action)));
        group.MapPost("/{code}/resign", (HttpContext context, PvpGames games, string code, RevisionRequest request) =>
            Invoke(() => games.Resign(Player(context), code, request.Revision)));
        group.MapPost("/{code}/leave", (HttpContext context, PvpGames games, string code, RevisionRequest request) =>
            Invoke(() => games.Leave(Player(context), code, request.Revision)));
        group.MapGet("/{code}/export", (HttpContext context, PvpGames games, string code) => InvokeResult(() =>
            Results.File(Encoding.UTF8.GetBytes(games.ExportHistory(Player(context), code)),
                "application/json", "enclosure-pvp.json")));

        // Recheck membership and the completed result on every read, start, and cancellation.
        // A lobby code is an invitation, not authorization to inspect or analyse its game.
        group.MapGet("/{code}/review", (HttpContext context, PvpGames games, GameReviews reviews,
            string code, bool? positions) => Invoke(() =>
                reviews.Get(ReviewKey(code), games.FinishedHistory(Player(context), code).ToJson(), positions ?? false)));
        group.MapPost("/{code}/review", (HttpContext context, PvpGames games, GameReviews reviews,
            string code, PvpReviewRequest request) => Invoke(() =>
                reviews.Start(ReviewKey(code), games.FinishedHistory(Player(context), code).ToJson(), request.BudgetMilliseconds)));
        group.MapPost("/{code}/review/cancel", (HttpContext context, PvpGames games, GameReviews reviews,
            string code) => Invoke(() =>
        {
            _ = games.FinishedHistory(Player(context), code);
            return reviews.Cancel(ReviewKey(code));
        }));
    }

    private static string Player(HttpContext context) =>
        context.Items["game-session-id"] as string ?? throw new UnauthorizedAccessException("Open the game in your browser first.");

    private static string ReviewKey(string code) => code.Trim().ToUpperInvariant();

    private static IResult Invoke<T>(Func<T> operation) => InvokeResult(() => Results.Ok(operation()));

    private static IResult InvokeResult(Func<IResult> operation)
    {
        try { return operation(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
        {
            var status = exception switch
            {
                UnauthorizedAccessException => StatusCodes.Status403Forbidden,
                KeyNotFoundException => StatusCodes.Status404NotFound,
                SessionCapacityException => StatusCodes.Status503ServiceUnavailable,
                PvpCapacityException => StatusCodes.Status503ServiceUnavailable,
                GameReviewCapacityException => StatusCodes.Status503ServiceUnavailable,
                InvalidOperationException => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest
            };
            return Results.Json(new { error = exception.Message }, statusCode: status);
        }
    }
}

public sealed record PvpCreateRequest(PvpSettings? Settings = null, string ColorPreference = "random", string? DisplayName = null);
public sealed record PvpJoinRequest(string Code, string ColorPreference = "random", string? DisplayName = null);
public sealed record PvpLobbySettingsRequest(long Revision, PvpSettings? Settings = null, string? ColorPreference = null,
    string? DisplayName = null);
public sealed record PvpReadyRequest(long Revision, bool Ready = true);
public sealed record PvpReviewRequest(double BudgetMilliseconds = 100);
