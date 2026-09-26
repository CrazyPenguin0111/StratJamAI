using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;
using StratJamAI.Training;

namespace StratJamAI;

public static class CommandLine
{
    public static async Task<int> Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Help(); return 0; }
            var options = new Options(args.Skip(1).ToArray());
            switch (args[0])
            {
                case "doctor":
                    options.Known("device");
                    Print(Diagnostics.Doctor(options.Get("device", "cuda")));
                    return 0;
                case "benchmark":
                    options.Known("game", "envs", "seconds");
                    var benchmarkGame = GameRegistry.Get(options.Get("game", "tic-tac-toe"));
                    var seconds = options.Number("seconds", 10);
                    if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentException("--seconds must be positive.");
                    var workerCount = Diagnostics.Calibrate(benchmarkGame, options.Integer("envs", 64),
                        Deadline.After(TimeSpan.FromSeconds(seconds)), Console.WriteLine);
                    Console.WriteLine($"selected workers: {workerCount}");
                    return 0;
                case "train":
                    return await Train(options);
                case "benchmark-enclosure":
                    options.Known("samples", "compare", "pairs", "seed", "output");
                    var enclosureBenchmark = EnclosureBenchmarks.Run(options.Integer("samples", 5), options.Has("compare"),
                        options.Integer("pairs", 2), options.Unsigned("seed", 72891), Console.Error.WriteLine);
                    Print(enclosureBenchmark);
                    if (options.Has("output")) JsonFiles.WriteAtomic(options.Required("output"), enclosureBenchmark);
                    return 0;
                case "ui":
                    options.Known("port", "move-ms", "no-browser", "public", "share", "max-searches", "max-sessions");
                    return await UiLauncher.Run(options.Integer("port", 5080), options.Number("move-ms", 1000), options.Has("no-browser"),
                        options.Has("public"), options.Has("share"), options.Integer("max-searches", 4), options.Integer("max-sessions", 128));
                case "play":
                    options.Known("game", "model", "bot", "seat", "load", "save", "move-ms");
                    return EnclosureConsole.Play(options);
                case "suggest":
                    options.Known("history", "model", "bot", "move-ms");
                    return EnclosureConsole.Suggest(options);
                case "_worker":
                    options.Known("run", "start", "consumed", "continuation");
                    var run = options.Required("run");
                    var config = JsonFiles.Read<TrainingConfig>(Path.Combine(run, "config.json"));
                    var budget = new RunBudget(config.Minutes * 60, options.Number("consumed", 0),
                        long.Parse(options.Required("start"), CultureInfo.InvariantCulture));
                    return Trainer.Run(config, run, budget, options.Has("continuation"));
                case "evaluate":
                    options.Known("model", "bot", "game", "move-ms", "pairs", "seconds", "output", "seed");
                    var artifact = options.Has("model") ? JsonFiles.Read<BotArtifact>(options.Required("model")) : null;
                    var definition = GameRegistry.Get(options.Get("game", artifact?.Game.Id ?? "enclosure"));
                    artifact?.Validate(definition.Spec);
                    var moveMilliseconds = options.Number("move-ms", artifact?.Search.MoveMilliseconds ?? 1000);
                    if (!double.IsFinite(moveMilliseconds) || moveMilliseconds <= 0) throw new ArgumentException("--move-ms must be positive.");
                    var evaluationBot = artifact is not null && !options.Has("bot") ? artifact.CreateBot(definition) :
                        EnclosureConsole.CreateBot(options.Get("bot", "alpha-beta"), artifact, definition, moveMilliseconds);
                    var evaluation = Evaluation.Run(definition, evaluationBot, Evaluation.FixedOpponents(definition),
                        options.Integer("pairs", 32), options.Unsigned("seed", 43000019),
                        Deadline.After(TimeSpan.FromSeconds(options.Number("seconds", 60))), moveMilliseconds);
                    Print(evaluation);
                    if (options.Has("output")) JsonFiles.WriteAtomic(options.Required("output"), evaluation);
                    return evaluation.Complete ? 0 : 2;
                case "export":
                    options.Known("run", "output", "policy-only");
                    var source = Path.Combine(options.Required("run"), options.Has("policy-only") ? "best-policy.bot.json" : "best.bot.json");
                    var exported = JsonFiles.Read<BotArtifact>(source);
                    exported.Validate(GameRegistry.Get(exported.Game.Id).Spec);
                    JsonFiles.WriteAtomic(options.Required("output"), exported);
                    Console.WriteLine($"exported {exported.SelectedBot}: {Path.GetFullPath(options.Required("output"))}");
                    return 0;
                case "play-demo":
                    return Play(options);
                default:
                    throw new ArgumentException($"Unknown command '{args[0]}'. Run with --help.");
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static async Task<int> Train(Options options)
    {
        options.Known("config", "game", "device", "minutes", "seed", "envs", "workers", "rollout", "batch", "epochs",
            "output", "resume", "no-warmup", "pairs", "move-ms", "simulations");
        var resume = options.Has("resume");
        var run = Path.GetFullPath(resume ? options.Required("resume") : options.Get("output",
            Path.Combine("runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6])));
        if (resume && options.Has("output")) throw new ArgumentException("--resume specifies the existing run; do not also provide --output.");
        if (!resume && Directory.Exists(run) && Directory.EnumerateFileSystemEntries(run).Any())
            throw new ArgumentException("The output directory is not empty. Choose a new directory or use --resume.");
        var original = resume ? JsonFiles.Read<TrainingConfig>(Path.Combine(run, "config.json")) : options.Has("config")
            ? JsonFiles.Read<TrainingConfig>(options.Required("config")) : new TrainingConfig();
        var config = original with
        {
            Game = options.Get("game", original.Game), Device = options.Get("device", original.Device),
            Minutes = options.Number("minutes", original.Minutes), Seed = options.Unsigned("seed", original.Seed),
            Environments = options.Integer("envs", original.Environments), Workers = options.Integer("workers", original.Workers),
            RolloutDecisions = options.Integer("rollout", original.RolloutDecisions), MinibatchSize = options.Integer("batch", original.MinibatchSize),
            Epochs = options.Integer("epochs", original.Epochs), WarmStart = !options.Has("no-warmup") && original.WarmStart,
            EvaluationPairs = options.Integer("pairs", original.EvaluationPairs),
            Search = original.Search with { MoveMilliseconds = options.Number("move-ms", original.Search.MoveMilliseconds),
                Simulations = options.Integer("simulations", original.Search.Simulations) }
        };
        config.Validate();
        var definition = GameRegistry.Get(config.Game);
        double consumed = 0;
        if (resume)
        {
            if ((config with { Minutes = original.Minutes, Device = original.Device, Workers = original.Workers }) != original)
                throw new ArgumentException("On resume, only --minutes, --device and --workers may change. Minutes is the total cumulative budget.");
            var checkpoint = Checkpoints.Load(run, definition.Spec).State;
            if ((config with { Minutes = checkpoint.Config.Minutes, Device = checkpoint.Config.Device,
                    Workers = checkpoint.Config.Workers }) != checkpoint.Config)
                throw new ArgumentException("Run configuration differs from the saved checkpoint. Start a new run to change learning settings.");
            consumed = checkpoint.ConsumedSeconds;
            var supervisorPath = Path.Combine(run, "supervisor.json");
            if (File.Exists(supervisorPath)) consumed = Math.Max(consumed, JsonFiles.Read<SupervisorState>(supervisorPath).ConsumedSeconds);
        }
        if (consumed >= config.Minutes * 60)
            throw new ArgumentException("This run has used its budget. An explicitly larger --minutes value is required to continue.");
        Directory.CreateDirectory(run);
        JsonFiles.WriteAtomic(Path.Combine(run, "config.json"), config);
        var start = Stopwatch.GetTimestamp();
        var budget = new RunBudget(config.Minutes * 60, consumed, start);
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("_worker");
        info.ArgumentList.Add("--run"); info.ArgumentList.Add(run);
        info.ArgumentList.Add("--start"); info.ArgumentList.Add(start.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--consumed"); info.ArgumentList.Add(consumed.ToString("R", CultureInfo.InvariantCulture));
        if (resume) info.ArgumentList.Add("--continuation");
        Console.WriteLine($"run directory: {run}");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        var status = "running";
        var exitCode = 1;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start the training worker.");
        try
        {
            var exit = process.WaitForExitAsync();
            while (!exit.IsCompleted && !budget.End.Expired && !cancellation.IsCancellationRequested)
            {
                JsonFiles.WriteAtomic(Path.Combine(run, "supervisor.json"), new SupervisorState(status, budget.Consumed, config.Minutes * 60));
                await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(Math.Min(1, budget.End.RemainingSeconds)), cancellation.Token));
            }
            if (!process.HasExited)
            {
                status = cancellation.IsCancellationRequested ? "interrupted" : "budget-exhausted";
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
                exitCode = cancellation.IsCancellationRequested ? 130 : 124;
                Console.WriteLine($"{status}: worker stopped; existing atomic exports and checkpoints were retained.");
            }
            else { exitCode = process.ExitCode; status = exitCode == 0 ? "completed" : "failed"; }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
            Console.CancelKeyPress -= handler;
            JsonFiles.WriteAtomic(Path.Combine(run, "supervisor.json"), new SupervisorState(status, budget.Consumed, config.Minutes * 60));
        }
        return exitCode;
    }

    private static int Play(Options options)
    {
        options.Known("model", "games", "human");
        var definition = new TicTacToeDefinition();
        var bot = options.Has("model") ? BotArtifact.Load(options.Required("model"), definition.Spec).CreateBot(definition) : new TicTacToeTacticalBot();
        var count = options.Integer("games", 1);
        if (count < 1) throw new ArgumentException("--games must be positive.");
        var random = new RandomSource(9781);
        for (var match = 0; match < count; match++)
        {
            var game = new TicTacToe();
            while (!game.Frame.Finished)
            {
                var request = game.Frame.Decisions.Single();
                int action;
                if (options.Has("human") && request.Player == 0)
                {
                    Draw(game);
                    Console.Write("Your cell (0–8): ");
                    var line = Console.ReadLine();
                    if (line is null) return 0;
                    if (!int.TryParse(line, out action) || !request.Actions.Any(a => a.Id == action))
                    { Console.WriteLine("Choose an empty cell."); continue; }
                }
                else action = BotActions.Choose(request.Player == 1 ? bot : new RandomBot(), game, request, random,
                    Deadline.After(TimeSpan.FromMilliseconds(50)));
                game.Step(new Dictionary<int, int> { [request.Player] = action });
            }
            Draw(game);
            Console.WriteLine($"returns: X={game.Frame.Returns[0]}, O ({bot.Name})={game.Frame.Returns[1]}");
        }
        return 0;
    }
    private static void Draw(TicTacToe game)
    {
        var observation = game.Observe(0);
        for (var row = 0; row < 3; row++)
            Console.WriteLine(string.Join(" | ", Enumerable.Range(row * 3, 3).Select(i =>
                observation[i] == 1 ? "X" : observation[i + 9] == 1 ? "O" : i.ToString(CultureInfo.InvariantCulture))));
    }
    private static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value,
        new JsonSerializerOptions(JsonFiles.Options) { WriteIndented = true }));
    private static void Help() => Console.WriteLine("""
        StratJamAI — C# strategy-bot training and portable inference

        ui         [--port 5080] [--move-ms 1000] [--no-browser]
                   [--public] [--share] [--max-searches 4] [--max-sessions 128]
        play       [--game enclosure] [--bot alpha-beta|mcts|tactical|random|policy]
                   [--model bot.json] [--seat blue|red] [--load game.json] [--save game.json]
                   [--move-ms 1000]
        suggest    --history game.json [--bot alpha-beta] [--model bot.json] [--move-ms 1000]
        doctor     [--device cuda|cpu]
        benchmark  [--game tic-tac-toe] [--envs 64] [--seconds 10]
        benchmark-enclosure [--samples 5] [--compare] [--pairs 2] [--seed 72891] [--output report.json]
        train      [--config configs/default.json] [--minutes 60] [--device cuda]
                   [--output runs/example] [--seed 1337] [--envs 64] [--workers 0]
                   [--rollout 8192] [--batch 512] [--epochs 4] [--no-warmup]
                   [--pairs 16] [--move-ms 50] [--simulations 256]
        train      --resume runs/example [--minutes TOTAL_CUMULATIVE_MINUTES]
        evaluate   --model runs/example/best.bot.json [--pairs 32] [--seconds 60]
                   [--seed 43000019] [--output evaluation.json]
        evaluate   --game enclosure --bot alpha-beta [--move-ms 1000] [--pairs 4] [--seconds 600]
        export     --run runs/example --output bot.json [--policy-only]
        play-demo  [--model bot.json] [--games 1] [--human]

        Dependencies/builds are prepared before the training clock starts.
        Enclosure training: train --config configs/enclosure.json --output runs/enclosure
        The UI runs locally; no model or GPU is required for depth-search play.
        --public listens on all network interfaces; --share prints a temporary public link using cloudflared.
        --share works with the default loopback server. Keep the command running to keep the link available.
        """);
}

public sealed record SupervisorState(string Status, double ConsumedSeconds, double BudgetSeconds);

internal sealed class Options
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Flags = ["no-warmup", "policy-only", "human", "continuation", "no-browser", "compare", "public", "share"];
    public Options(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Expected an option, got '{args[i]}'.");
            var name = args[i][2..];
            var value = Flags.Contains(name) ? "true" : i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i] : throw new ArgumentException($"Missing value for --{name}.");
            if (!values.TryAdd(name, value)) throw new ArgumentException($"Duplicate option --{name}.");
        }
    }
    public void Known(params string[] names)
    {
        foreach (var name in values.Keys)
            if (!names.Contains(name)) throw new ArgumentException($"Unknown option --{name}.");
    }
    public bool Has(string name) => values.ContainsKey(name);
    public string Get(string name, string fallback) => values.GetValueOrDefault(name, fallback);
    public string Required(string name) => values.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Missing --{name}.");
    public int Integer(string name, int fallback) => Has(name) ? int.Parse(Required(name), CultureInfo.InvariantCulture) : fallback;
    public double Number(string name, double fallback) => Has(name) ? double.Parse(Required(name), CultureInfo.InvariantCulture) : fallback;
    public ulong Unsigned(string name, ulong fallback) => Has(name) ? ulong.Parse(Required(name), CultureInfo.InvariantCulture) : fallback;
}
