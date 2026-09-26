using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length < 2) throw new ArgumentException("old DLL, candidate DLL, optional output JSON path");
var braidMode = args.Length > 3 && args[3] == "braid";
var braidBudget = braidMode && args.Length > 6 ? int.Parse(args[6], CultureInfo.InvariantCulture) : 100;
if (braidBudget is < 1 or > 60000) throw new ArgumentException("The braid move budget must be 1–60000 milliseconds.");
var before = new Engine(args[0], braidBudget);
var after = new Engine(args[1], braidBudget);
var report = new List<object>();
var options = new JsonSerializerOptions { WriteIndented = true };
var output = args.Length > 2 ? args[2] : "/tmp/enclosure-strategy-match-results.json";
if (braidMode)
{
    if (args.Length is < 5 or > 7)
        throw new ArgumentException("Braid mode needs history.json, optionally comma-separated initial prefixes and a move budget in milliseconds.");
    var prefixes = (args.Length > 5 ? args[5] : "0,25,33").Split(',', StringSplitOptions.TrimEntries)
        .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).Distinct().ToArray();
    const int constructionHistoryLimit = 65;
    using var document = JsonDocument.Parse(File.ReadAllText(args[4]));
    var source = document.RootElement;
    if (source.GetProperty("formatVersion").GetInt32() != 1 || source.GetProperty("game").GetString() != "enclosure")
        throw new InvalidDataException("Expected a version 1 Enclosure history.");
    var sourceMoves = source.GetProperty("moves");
    if (sourceMoves.ValueKind != JsonValueKind.Array || sourceMoves.GetArrayLength() > 120)
        throw new InvalidDataException("An Enclosure history needs at most 120 moves.");
    var history = new List<int>();
    var bluePlan = new List<(int HistoryIndex, int Action)>();
    foreach (var move in sourceMoves.EnumerateArray())
    {
        try
        {
            var pass = move.TryGetProperty("pass", out var passValue) && passValue.GetBoolean();
            int action;
            if (pass)
            {
                if (move.TryGetProperty("from", out _) || move.TryGetProperty("to", out _))
                    throw new InvalidDataException("A pass cannot have endpoints.");
                action = 7140;
            }
            else
            {
                var from = move.GetProperty("from"); var to = move.GetProperty("to");
                if (from.GetArrayLength() != 2 || to.GetArrayLength() != 2)
                    throw new InvalidDataException("Expected two coordinates per endpoint.");
                action = before.Id(from[0].GetInt32(), from[1].GetInt32(), to[0].GetInt32(), to[1].GetInt32());
                if (action != after.Id(from[0].GetInt32(), from[1].GetInt32(), to[0].GetInt32(), to[1].GetInt32()))
                    throw new InvalidDataException("Engine action vocabularies differ.");
            }
            if (before.Turn == 0 && history.Count < constructionHistoryLimit)
                bluePlan.Add((history.Count + 1, action));
            before.Play(action); after.Play(action);
            CheckSimulators();
            history.Add(action);
        }
        catch (Exception e) { throw new InvalidDataException($"Invalid braid history at move {history.Count + 1}: {e.GetBaseException().Message}", e); }
    }
    if (prefixes.Any(prefix => prefix < 0 || prefix >= history.Count))
        throw new ArgumentException("Each initial prefix must be nonnegative and shorter than the supplied history.");
    var original = new { moves = history.Count, scores = before.Scores, areas = before.Areas, finished = before.Finished };
    var results = new SortedDictionary<int, Dictionary<string, object>>();
    string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    var summary = new
    {
        kind = "braid-construction-challenge", fixture = Path.GetFullPath(args[4]), fixtureSha256 = Hash(args[4]),
        baselineSha256 = Hash(args[0]), candidateSha256 = Hash(args[1]), moveBudgetMs = braidBudget,
        rootPrefixes = prefixes, constructionHistoryLimit, original,
        description = "Frozen supplied prefixes; Blue follows the first 65 history moves, repairs its first missing legal construction edge, then uses the tactical teacher. Diagnostic continuations, not a win-rate estimate.",
        results
    };
    // Validate the complete history before warmups or any timed challenge continuation.
    foreach (var engine in new[] { before, after })
    {
        engine.Reset();
        for (var i = 0; i < 3; i++) engine.Search(30);
    }
    foreach (var prefix in prefixes)
    {
        var byEngine = results[prefix] = new Dictionary<string, object>();
        foreach (var (label, engine) in new[] { ("baseline", before), ("candidate", after) })
        {
            before.Reset(); after.Reset();
            foreach (var action in history.Take(prefix)) { before.Play(action); after.Play(action); }
            CheckSimulators();
            if (engine.Finished) throw new ArgumentException($"History prefix {prefix} is already terminal.");
            var initialMoveNumber = engine.MoveNumber;
            var planIndex = bluePlan.FindIndex(move => move.HistoryIndex > prefix);
            if (planIndex < 0) planIndex = bluePlan.Count;
            var played = history.Take(prefix).ToList();
            var scripted = 0; var repair = 0; var fallback = 0;
            var depths = new List<int>(); var latencies = new List<double>(); long nodes = 0;
            var choices = new List<object>();
            var snapshots = new List<object> { new { move = engine.MoveNumber, scores = engine.Scores, areas = engine.Areas } };
            while (!engine.Finished)
            {
                var player = engine.Turn;
                var kind = "search";
                int action;
                int? depth = null;
                double? elapsedMilliseconds = null;
                if (player == 0)
                {
                    var scheduled = planIndex < bluePlan.Count ? bluePlan[planIndex++].Action : -1;
                    var legal = engine.Legal().ToHashSet();
                    if (legal.Contains(scheduled)) { action = scheduled; scripted++; kind = "scripted"; }
                    else
                    {
                        var existing = engine.Segments(0);
                        action = bluePlan.Select(move => move.Action)
                            .FirstOrDefault(id => id != 7140 && !existing.Contains(id) && legal.Contains(id), -1);
                        if (action >= 0) { repair++; kind = "repair"; }
                        else { action = engine.Tactical(); fallback++; kind = "fallback"; }
                    }
                }
                else
                {
                    var watch = Stopwatch.StartNew();
                    var search = engine.Search(braidBudget);
                    elapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
                    action = search.Action; depth = search.Depth;
                    depths.Add(search.Depth); nodes += search.Nodes; latencies.Add(elapsedMilliseconds.Value);
                }
                before.Play(action); after.Play(action); CheckSimulators(); played.Add(action);
                choices.Add(new { moveNumber = engine.MoveNumber, player, kind, action = engine.Describe(action), depth, elapsedMilliseconds });
                if (engine.MoveNumber % 10 == 0 || engine.Finished)
                    snapshots.Add(new { move = engine.MoveNumber, scores = engine.Scores, areas = engine.Areas });
            }
            var result = new
            {
                rootPrefix = prefix, initialMoveNumber, engine = label, aiSeat = 1,
                scores = engine.Scores, areas = engine.Areas, aiResult = Math.Sign(engine.Scores[1] - engine.Scores[0]),
                scripted, repair, fallback, aiMoves = depths.Count, aiMeanDepth = depths.Count == 0 ? 0 : depths.Average(),
                aiNodes = nodes, aiMeanMilliseconds = latencies.Count == 0 ? 0 : latencies.Average(),
                aiMaxMilliseconds = latencies.Count == 0 ? 0 : latencies.Max(), snapshots, choices,
                history = new { formatVersion = 1, game = "enclosure", moves = played.Select(engine.Describe).ToArray() }
            };
            byEngine[label] = result;
            Console.WriteLine(JsonSerializer.Serialize(new { rootPrefix = prefix, engine = label, result.scores, result.areas,
                result.aiResult, scripted, repair, fallback, result.aiMeanDepth, result.aiMaxMilliseconds }));
            File.WriteAllText(output, JsonSerializer.Serialize(summary, options));
        }
    }
    return;

    void CheckSimulators()
    {
        if (before.Turn != after.Turn || before.MoveNumber != after.MoveNumber || before.Finished != after.Finished ||
            !before.Scores.SequenceEqual(after.Scores) || !before.Areas.SequenceEqual(after.Areas))
            throw new InvalidDataException("Engine simulators diverged.");
    }
}
foreach (var engine in new[] { before, after }) for (var i = 0; i < 3; i++) engine.Search(30);
if (args.Length > 3 && args[3] is "disruption" or "disruption-baseline" or "disruption-candidate")
{
    if (args.Length < 5) throw new ArgumentException("Disruption mode requires at least one history JSON path.");
    foreach (var fixture in args.Skip(4))
    {
        using var document = JsonDocument.Parse(File.ReadAllText(fixture));
        var bluePlan = new List<int>();
        before.Reset();
        foreach (var move in document.RootElement.GetProperty("moves").EnumerateArray())
        {
            var action = move.TryGetProperty("pass", out var pass) && pass.GetBoolean() ? 7140 :
                before.Id(move.GetProperty("from")[0].GetInt32(), move.GetProperty("from")[1].GetInt32(),
                    move.GetProperty("to")[0].GetInt32(), move.GetProperty("to")[1].GetInt32());
            if (before.Turn == 0) bluePlan.Add(action);
            before.Play(action);
        }
        var engines = args[3] == "disruption-baseline" ? new[] { ("baseline", before) } :
            args[3] == "disruption-candidate" ? new[] { ("candidate", after) } :
            new[] { ("baseline", before), ("candidate", after) };
        foreach (var (label, engine) in engines)
        {
            engine.Reset();
            var planIndex = 0; var scripted = 0; var disruptive = 0; var fallback = 0;
            var depths = new List<int>(); long nodes = 0;
            var snapshots = new List<object>();
            while (!engine.Finished)
            {
                int action;
                if (engine.Turn == 0)
                {
                    var planned = planIndex < bluePlan.Count ? bluePlan[planIndex] : -1;
                    planIndex++;
                    var legal = engine.Legal();
                    var current = engine.Areas;
                    action = -1; double bestDamage = 0, bestOwnArea = double.NegativeInfinity;
                    foreach (var candidate in legal)
                    {
                        if (!engine.IsCapture(candidate)) continue;
                        var next = engine.ProbeAreas(candidate);
                        var damage = current[1] - next[1];
                        if (damage <= 1e-8 || damage < bestDamage - 1e-8 ||
                            Math.Abs(damage - bestDamage) <= 1e-8 && next[0] <= bestOwnArea) continue;
                        action = candidate; bestDamage = damage; bestOwnArea = next[0];
                    }
                    if (action >= 0) disruptive++;
                    else if (legal.Contains(planned)) { action = planned; scripted++; }
                    else { action = engine.Tactical(); fallback++; }
                }
                else
                {
                    var result = engine.Search(100); action = result.Action;
                    depths.Add(result.Depth); nodes += result.Nodes;
                }
                engine.Play(action);
                if (engine.MoveNumber % 20 == 0 || engine.Finished)
                    snapshots.Add(new { move = engine.MoveNumber, areas = engine.Areas, scores = engine.Scores });
            }
            var record = new { kind = "adaptive-disruption", fixture = Path.GetFileName(fixture), engine = label,
                aiSeat = 1, moveBudgetMs = 100, scores = engine.Scores, areas = engine.Areas,
                aiResult = Math.Sign(engine.Scores[1] - engine.Scores[0]), scripted, disruptive, fallback,
                aiMeanDepth = depths.Average(), aiNodes = nodes, snapshots };
            report.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
            File.WriteAllText(output, JsonSerializer.Serialize(report, options));
        }
    }
    return;
}
for (var book = 0; book < 2; book++)
for (var candidateSeat = 0; candidateSeat < 2; candidateSeat++)
{
    before.Reset(); after.Reset();
    if (book == 1)
        foreach (var action in new[] { before.Id(3,9,6,12), before.Id(15,9,12,6), before.Id(12,6,12,9) })
        { before.Play(action); after.Play(action); }
    var snapshots = new List<object>(); var depths = new List<int>[2] { [], [] }; var nodes = new long[2];
    while (!before.Finished)
    {
        var candidateTurn = before.Turn == candidateSeat;
        var result = (candidateTurn ? after : before).Search(100);
        var index = candidateTurn ? 1 : 0;
        depths[index].Add(result.Depth); nodes[index] += result.Nodes;
        before.Play(result.Action); after.Play(result.Action);
        if (!before.Scores.SequenceEqual(after.Scores) || !before.Areas.SequenceEqual(after.Areas)) throw new InvalidDataException("Engine simulators diverged.");
        if (before.MoveNumber % 20 == 0 || before.Finished) snapshots.Add(new { move = before.MoveNumber, areas = before.Areas, scores = before.Scores });
    }
    var record = new { kind = "head-to-head", book, candidateSeat, moveBudgetMs = 100, scores = before.Scores, areas = before.Areas,
        candidateResult = Math.Sign(before.Scores[candidateSeat] - before.Scores[1 - candidateSeat]),
        beforeMeanDepth = depths[0].Average(), candidateMeanDepth = depths[1].Average(), beforeNodes = nodes[0], candidateNodes = nodes[1], snapshots };
    report.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
    File.WriteAllText(output, JsonSerializer.Serialize(report, options));
}
if (args.Length > 3 && args[3] == "paired") return;
foreach (var (label, engine) in new[] { ("v2", before), ("candidate", after) })
for (var scriptSeat = 0; scriptSeat < 2; scriptSeat++)
{
    engine.Reset(); var snapshots = new List<object>(); var scriptedPlacements = 0; var fallbackPlacements = 0;
    var depths = new List<int>(); long nodes = 0;
    var path = new (int X,int Y)[] { (0,9),(0,12),(0,15),(2,18),(5,18),(8,18),(11,18),(14,18),(17,18),(17,15),(15,12),(12,9),(9,6),(6,3),(3,0),(0,0),(0,3),(0,6),(0,9) };
    var plan = path.Zip(path.Skip(1)).Select(pair => engine.Id(scriptSeat == 0 ? pair.First.X : 18-pair.First.X, pair.First.Y,
        scriptSeat == 0 ? pair.Second.X : 18-pair.Second.X, pair.Second.Y)).ToArray();
    while (!engine.Finished)
    {
        int action;
        if (engine.Turn == scriptSeat)
        {
            var existing = engine.Segments(scriptSeat); var legal = engine.Legal().ToHashSet();
            action = plan.FirstOrDefault(id => !existing.Contains(id) && legal.Contains(id), -1);
            if (action < 0) { action = engine.Tactical(); fallbackPlacements++; }
            else scriptedPlacements++;
        }
        else
        {
            var result = engine.Search(50); action = result.Action; depths.Add(result.Depth); nodes += result.Nodes;
        }
        engine.Play(action);
        if (engine.MoveNumber % 20 == 0 || engine.Finished) snapshots.Add(new { move = engine.MoveNumber, areas = engine.Areas, scores = engine.Scores });
    }
    var record = new { kind = "perimeter-challenge", engine = label, scriptSeat, moveBudgetMs = 50, scores = engine.Scores, areas = engine.Areas,
        aiResult = Math.Sign(engine.Scores[1-scriptSeat]-engine.Scores[scriptSeat]), scriptedPlacements, fallbackPlacements,
        aiMeanDepth = depths.Average(), aiNodes = nodes, snapshots };
    report.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
    File.WriteAllText(output, JsonSerializer.Serialize(report, options));
}

sealed class Engine
{
    private readonly Assembly assembly;
    private readonly Type gameType, botType, optionsType;
    private readonly MethodInfo play, search, legal, id, segments, tacticalChoose, copy, isCapture, decode;
    private readonly PropertyInfo turn, finished, moveNumber, scores, areas, frame;
    private readonly object bot, tactical;
    private object random;
    private readonly object never;
    private readonly Type contextType, randomType;
    private object game;
    public Engine(string dll, int moveMilliseconds = 100)
    {
        assembly = new AssemblyLoadContext(Guid.NewGuid().ToString(), false).LoadFromAssemblyPath(Path.GetFullPath(dll));
        gameType = assembly.GetType("StratJamAI.Core.Games.Enclosure")!;
        botType = assembly.GetType("StratJamAI.Core.Bots.EnclosureAlphaBetaBot")!;
        optionsType = assembly.GetType("StratJamAI.Core.Bots.DepthSearchOptions")!;
        play = gameType.GetMethod("Play")!; legal = gameType.GetMethod("GenerateLegalActions", BindingFlags.Public|BindingFlags.Instance)!;
        copy = gameType.GetMethod("Copy")!; isCapture = gameType.GetMethod("IsCapture")!;
        id = gameType.GetMethod("GetActionId")!; segments = gameType.GetMethod("Segments")!;
        decode = gameType.GetMethod("DecodeAction")!;
        search = botType.GetMethods().Single(m=>m.Name=="Search"&&m.GetParameters()[1].ParameterType==typeof(TimeSpan));
        turn = gameType.GetProperty("Turn")!; finished = gameType.GetProperty("Finished")!; moveNumber = gameType.GetProperty("MoveNumber")!;
        scores = gameType.GetProperty("Scores")!; areas = gameType.GetProperty("Areas")!; frame = gameType.GetProperty("Frame")!;
        bot = Activator.CreateInstance(botType, Activator.CreateInstance(optionsType, (double)moveMilliseconds, 120))!;
        var tacticalType = assembly.GetType("StratJamAI.Core.Games.EnclosureTacticalBot")!;
        tactical = Activator.CreateInstance(tacticalType)!; tacticalChoose = tacticalType.GetMethod("ChooseAction")!;
        randomType = assembly.GetType("StratJamAI.Core.RandomSource")!; random = Activator.CreateInstance(randomType, 481UL)!;
        contextType = assembly.GetType("StratJamAI.Core.Bots.BotContext")!;
        never = assembly.GetType("StratJamAI.Core.Deadline")!.GetProperty("Never")!.GetValue(null)!;
        game = Activator.CreateInstance(gameType)!;
    }
    public void Reset() { game = Activator.CreateInstance(gameType)!; random = Activator.CreateInstance(randomType, 481UL)!; }
    public int Turn => (int)turn.GetValue(game)!;
    public bool Finished => (bool)finished.GetValue(game)!;
    public int MoveNumber => (int)moveNumber.GetValue(game)!;
    public double[] Scores => ((IReadOnlyList<double>)scores.GetValue(game)!).ToArray();
    public double[] Areas => ((IReadOnlyList<double>)areas.GetValue(game)!).ToArray();
    public int Id(int x1,int y1,int x2,int y2) => (int)id.Invoke(null,[x1,y1,x2,y2])!;
    public int[] Legal() => (int[])legal.Invoke(game,null)!;
    public void Play(int action) => play.Invoke(game,[action]);
    public object Describe(int action)
    {
        if (action == 7140) return new { pass = true };
        var edge = decode.Invoke(null, [action])!;
        int[] Point(string name)
        {
            var point = edge.GetType().GetProperty(name)!.GetValue(edge)!;
            return [(int)point.GetType().GetProperty("X")!.GetValue(point)!, (int)point.GetType().GetProperty("Y")!.GetValue(point)!];
        }
        return new { from = Point("From"), to = Point("To") };
    }
    public bool IsCapture(int action) => (bool)isCapture.Invoke(game,[action])!;
    public double[] ProbeAreas(int action)
    {
        var next = copy.Invoke(game,null)!;
        play.Invoke(next,[action]);
        return ((IReadOnlyList<double>)areas.GetValue(next)!).ToArray();
    }
    public HashSet<int> Segments(int player) => ((IEnumerable)segments.Invoke(game,[player])!).Cast<object>()
        .Select(edge=>(int)edge.GetType().GetProperty("ActionId")!.GetValue(edge)!).ToHashSet();
    public (int Action,int Depth,long Nodes) Search(int ms)
    {
        var result = search.Invoke(bot,[game,TimeSpan.FromMilliseconds(ms),CancellationToken.None])!;
        var type = result.GetType();
        return ((int)type.GetProperty("Action")!.GetValue(result)!, (int)type.GetProperty("CompletedDepth")!.GetValue(result)!, (long)type.GetProperty("Nodes")!.GetValue(result)!);
    }
    public int Tactical()
    {
        var current = frame.GetValue(game)!;
        var decisions = (Array)current.GetType().GetProperty("Decisions")!.GetValue(current)!;
        var context = Activator.CreateInstance(contextType,random,never,game)!;
        return (int)tacticalChoose.Invoke(tactical,[decisions.GetValue(0),context])!;
    }
}
