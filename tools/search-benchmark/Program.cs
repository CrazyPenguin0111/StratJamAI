using System.Diagnostics;
using System.Text.Json;
using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;

var mode = args.FirstOrDefault() ?? "benchmark";
var samples = args.Length > 1 ? int.Parse(args[1]) : 5;
if (mode is not ("benchmark" or "fixed") || samples < 1)
    throw new ArgumentException("Usage: SearchBenchmark.dll [benchmark|fixed] [positive sample count]");

var positions = FixedPositions();
var results = new List<object>();
foreach (var (name, game) in positions)
{
    var bot = new EnclosureAlphaBetaBot(new(120_000));
    for (var warmup = 0; warmup < 3; warmup++)
        bot.Search(game, TimeSpan.FromMilliseconds(100));

    if (mode == "fixed")
    {
        var depth = name == "opening" ? 3 : 2;
        bot = new EnclosureAlphaBetaBot(new(120_000, depth));
        for (var sample = 0; sample < samples; sample++)
            Measure("fixed", name, game, bot, 120_000, depth, sample);
    }
    else
    {
        foreach (var budget in new[] { 5d, 50d, 1000d })
            for (var sample = 0; sample < samples; sample++)
                Measure("budget", name, game, bot, budget, 120, sample);
    }
}
Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

void Measure(string kind, string name, Enclosure game, EnclosureAlphaBetaBot bot,
    double budget, int requestedDepth, int sample)
{
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    var started = Stopwatch.GetTimestamp();
    var result = bot.Search(game, TimeSpan.FromMilliseconds(budget));
    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;

    // Validation is outside the measured region and leaves the fixture unchanged.
    if (!game.IsLegal(result.Action)) throw new InvalidDataException("Illegal search result.");
    var replay = game.Copy();
    foreach (var action in result.PrincipalVariation)
    {
        if (!replay.IsLegal(action)) throw new InvalidDataException("Illegal principal variation.");
        replay.Play(action);
    }
    results.Add(new
    {
        kind, position = name, move = game.MoveNumber, legal = game.GenerateLegalActions().Length,
        budget, requestedDepth, sample, elapsed, bytes, result.Action, result.CompletedDepth,
        result.Nodes, result.Value, PV = result.PrincipalVariation.ToArray()
    });
    Console.Error.WriteLine($"{kind} {name}: {elapsed:F3} ms, depth {result.CompletedDepth}, " +
        $"{result.Nodes} nodes, action {result.Action}, value {result.Value:R}");
}

static List<(string Name, Enclosure Game)> FixedPositions()
{
    // Matches EnclosureBenchmarks.FixedPositions without depending on the training executable.
    var game = new Enclosure();
    var positions = new List<(string, Enclosure)> { ("opening", game.Copy()) };
    var random = new RandomSource(72891);
    foreach (var (move, name) in new[] { (60, "middle"), (110, "late") })
    {
        while (game.MoveNumber < move)
        {
            var actions = game.GenerateLegalActions();
            game.Play(actions[random.NextInt(actions.Length)]);
        }
        positions.Add((name, game.Copy()));
    }
    return positions;
}
