using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;

if (args.Length < 1 || args.Length > 5 || args.Length == 5 && args[4] != "--capture-pairs")
    throw new ArgumentException("Usage: DisruptionAnalysis.dll HISTORY.json [LABEL] [PREFIXES=102,106,70] [BUDGETS_MS=100,1000] [--capture-pairs]");
var historyPath = Path.GetFullPath(args[0]);
var label = args.Length > 1 ? args[1] : "engine";
var prefixes = (args.Length > 2 ? args[2] : "102,106,70").Split(',').Select(int.Parse).ToArray();
var budgets = (args.Length > 3 ? args[3] : "100,1000").Split(',').Select(int.Parse).ToArray();
var auditCapturePairs = args.Length == 5;
if (budgets.Any(budget => budget <= 0)) throw new ArgumentException("Budgets must be positive milliseconds.");
var actions = EnclosureHistory.Parse(File.ReadAllText(historyPath)).ActionIds();
var snapshots = new List<Enclosure>();
var replay = new Enclosure();
foreach (var action in actions)
{
    snapshots.Add(replay.Copy());
    replay.Play(action); // Full legal replay before selecting any analysis positions.
}
if (prefixes.Any(prefix => prefix < 0 || prefix >= snapshots.Count))
    throw new ArgumentException("Every prefix must point to an unfinished replay position.");

var results = new List<object>();
foreach (var prefix in prefixes)
{
    var position = snapshots[prefix];
    var player = position.Turn;
    var followingTurn = prefix + 1;
    while (followingTurn < snapshots.Count && snapshots[followingTurn].Turn == player) followingTurn++;
    var recordedActions = actions[prefix..followingTurn];
    // The warmup and reply enumeration are outside every timed search. This is a small
    // behavior check, not a statistically controlled throughput benchmark.
    new EnclosureAlphaBetaBot(new(50)).Search(position, TimeSpan.FromMilliseconds(50));
    var recorded = DescribeOutcome(position, recordedActions, followingTurn);
    foreach (var budget in budgets)
    {
        var bot = new EnclosureAlphaBetaBot(new(budget));
        var turn = position.Copy();
        var chosenActions = new List<int>();
        var searches = new List<PlacementSearch>();
        do
        {
            var started = Stopwatch.GetTimestamp();
            var result = bot.Search(turn, TimeSpan.FromMilliseconds(budget));
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!turn.IsLegal(result.Action)) throw new InvalidDataException("Search returned an illegal move.");
            var variation = turn.Copy();
            foreach (var action in result.PrincipalVariation) variation.Play(action);
            int? captureQuiescencePlies = null;
#if CAPTURE_QUIESCENCE
            captureQuiescencePlies = result.CaptureQuiescencePlies;
#endif
            searches.Add(new(Action(result.Action), result.CompletedDepth, result.Nodes, elapsed,
                result.ElapsedMilliseconds, result.Value, result.PrincipalVariation.Select(Action).ToArray(), captureQuiescencePlies));
            chosenActions.Add(result.Action);
            turn.Play(result.Action);
        } while (!turn.Finished && turn.Turn == player);
        var chosen = DescribeOutcome(position, chosenActions.ToArray(), followingTurn);
        var first = searches[0];
        results.Add(new
        {
            prefix, decisionMove = prefix + 1, player, budgetMilliseconds = budget,
            before = Summary(position), recorded, chosen, searches,
            first.CompletedDepth, first.Nodes, first.CaptureQuiescencePlies, elapsedMilliseconds = first.ElapsedMilliseconds,
            searchMilliseconds = first.SearchMilliseconds, first.Value, principalVariation = first.PrincipalVariation
        });
        Console.Error.WriteLine($"{label}: prefix {prefix}, {budget} ms per placement, " +
            string.Join("; ", searches.Select(result => $"{result.Action.Label}, depth {result.CompletedDepth}, capture extension {result.CaptureQuiescencePlies?.ToString() ?? "unavailable"}, {result.Nodes} nodes, {result.ElapsedMilliseconds:F3} ms")));
    }
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    label, history = Path.GetFileName(historyPath), actionCount = actions.Length,
    coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Enclosure).Assembly.Location))).ToLowerInvariant(),
    historySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(historyPath))).ToLowerInvariant(),
    final = Summary(replay), results
}, new JsonSerializerOptions { WriteIndented = true }));

object DescribeOutcome(Enclosure before, int[] turnActions, int followingTurn)
{
    var player = before.Turn;
    var next = before.Copy();
    foreach (var action in turnActions) next.Play(action);
    var worst = next.Copy();
    int? worstAction = null;
    var captureReplies = 0;
    var protectedSegments = next.Segments(player).Where(edge => edge.Invincible).Select(Id).ToHashSet();
    var previousEdges = next.Segments(player).Select(Id).ToHashSet();
    foreach (var reply in next.GenerateLegalActions())
    {
        if (!next.IsCapture(reply)) continue;
        captureReplies++;
        var candidate = next.Copy();
        candidate.Play(reply);
        var remaining = candidate.Segments(player).Select(Id).ToHashSet();
        var removed = previousEdges.Where(id => !remaining.Contains(id)).ToArray();
        if (removed.Length != 1 || removed.Any(protectedSegments.Contains))
            throw new InvalidDataException("A legal capture removed multiple or protected opposing segments.");
        if (candidate.Areas[player] < worst.Areas[player]) { worst = candidate; worstAction = reply; }
    }
    var actualReplies = next.Copy();
    var appliedReplies = new List<ActionDescription>();
    int? blockedMove = null;
    // Replay the user's actual following turn until it becomes illegal. Do not invent a
    // replacement or claim a guarantee against all possible two-placement combinations.
    for (var index = followingTurn; index < actions.Length && snapshots[index].Turn != player; index++)
    {
        if (!actualReplies.IsLegal(actions[index])) { blockedMove = index + 1; break; }
        actualReplies.Play(actions[index]);
        appliedReplies.Add(Action(actions[index]));
    }
    return new
    {
        action = Action(turnActions[0]), actions = turnActions.Select(Action).ToArray(),
        afterMove = Summary(next), legalCaptureReplies = captureReplies,
        worstSingleCapture = worstAction.HasValue ? Action(worstAction.Value) : null,
        worstSingleCapturePosition = Summary(worst), retainedAreaAfterWorstSingleCapture = worst.Areas[player],
        capturePairAudit = auditCapturePairs ? CapturePairs(next, player) : null,
        actualFollowingTurn = new { appliedReplies, blockedMove, position = Summary(actualReplies) },
        captureAndProtectionChecksPassed = true
    };
}

static object CapturePairs(Enclosure position, int defendedPlayer)
{
    var initialEdges = position.Segments(defendedPlayer).Select(Id).ToHashSet();
    var worst = position.Copy();
    int[] worstActions = [];
    var pairCount = 0;
    var areaCache = new Dictionary<(int, int), double>();
    foreach (var first in position.GenerateLegalActions())
    {
        if (!position.IsCapture(first)) continue;
        var afterFirst = position.Copy();
        afterFirst.Play(first);
        var remaining = afterFirst.Segments(defendedPlayer).Select(Id).ToHashSet();
        var removed = initialEdges.Single(id => !remaining.Contains(id));
        if (!areaCache.TryGetValue((removed, -1), out var firstArea))
            areaCache[(removed, -1)] = firstArea = afterFirst.Areas[defendedPlayer];
        if (firstArea < worst.Areas[defendedPlayer]) { worst = afterFirst; worstActions = [first]; }
        if (afterFirst.Turn == defendedPlayer || afterFirst.Finished) continue;
        foreach (var second in afterFirst.GenerateLegalActions())
        {
            if (!afterFirst.IsCapture(second)) continue;
            pairCount++;
            var afterSecond = afterFirst.Copy();
            afterSecond.Play(second);
            var survivors = afterSecond.Segments(defendedPlayer).Select(Id).ToHashSet();
            var removedSecond = remaining.Single(id => !survivors.Contains(id));
            var key = (Math.Min(removed, removedSecond), Math.Max(removed, removedSecond));
            // With captures only, the defended geometry depends on the removed edges,
            // not on the attacking endpoints. Legality is still checked for every pair.
            if (!areaCache.TryGetValue(key, out var area))
                areaCache[key] = area = afterSecond.Areas[defendedPlayer];
            if (area < worst.Areas[defendedPlayer]) { worst = afterSecond; worstActions = [first, second]; }
        }
    }
    return new
    {
        legalCapturePairCount = pairCount, worstActions = worstActions.Select(Action).ToArray(),
        retainedArea = worst.Areas[defendedPlayer], position = Summary(worst),
        defendedForecast = EnclosureStrategyEvaluation.Forecast(worst, defendedPlayer),
        opponentForecast = EnclosureStrategyEvaluation.Forecast(worst, 1 - defendedPlayer),
        defendedEvaluation = EnclosureStrategyEvaluation.Evaluate(worst, defendedPlayer)
    };
}

static object Summary(Enclosure game) => new
{
    game.MoveNumber, game.Turn, game.ActionsRemaining, game.Finished,
    scores = game.Scores.ToArray(), areas = game.Areas.ToArray()
};
static int Id(EnclosureSegment edge) => Enclosure.GetActionId(edge.From.X, edge.From.Y, edge.To.X, edge.To.Y);
static ActionDescription Action(int id)
{
    if (id == Enclosure.PassActionId) return new(id, "Pass", null, null);
    var edge = Enclosure.DecodeAction(id);
    return new(id, $"{(char)('A' + edge.From.X)}{edge.From.Y + 1}–{(char)('A' + edge.To.X)}{edge.To.Y + 1}", edge.From, edge.To);
}
sealed record ActionDescription(int Id, string Label, EnclosurePoint? From, EnclosurePoint? To);
sealed record PlacementSearch(ActionDescription Action, int CompletedDepth, long Nodes,
    double ElapsedMilliseconds, double SearchMilliseconds, double Value, ActionDescription[] PrincipalVariation,
    int? CaptureQuiescencePlies);
