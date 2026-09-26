using System.Globalization;
using System.Text.Json;
using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;

namespace StratJamAI;

internal static class EnclosureConsole
{
    internal static IBot CreateBot(string name, BotArtifact? artifact, IGameDefinition definition, double milliseconds) => name switch
    {
        "alpha-beta" when definition.Spec.Id == "enclosure" => new EnclosureAlphaBetaBot(new(milliseconds)),
        "mcts" or "search" => new MctsBot(new(MoveMilliseconds: milliseconds)),
        "tactical" => definition.CreateTeacher() ?? throw new ArgumentException("This game has no tactical bot."),
        "random" => new RandomBot(),
        "policy" when artifact is not null => new PolicyBot(new CpuNetwork(definition.Spec, artifact.Weights)),
        "policy" => throw new ArgumentException("The policy bot requires --model."),
        _ => throw new ArgumentException($"Unsupported bot '{name}' for {definition.Spec.Id}.")
    };

    private static (IBot Bot, double Milliseconds) Settings(Options options)
    {
        var time = options.Number("move-ms", 1000);
        if (!double.IsFinite(time) || time <= 0) throw new ArgumentException("--move-ms must be positive.");
        var definition = new EnclosureDefinition();
        var artifact = options.Has("model") ? BotArtifact.Load(options.Required("model"), definition.Spec) : null;
        return (artifact is not null && !options.Has("bot") ? artifact.CreateBot(definition) :
            CreateBot(options.Get("bot", "alpha-beta"), artifact, definition, time), time);
    }

    public static int Suggest(Options options)
    {
        var game = EnclosureHistory.Parse(File.ReadAllText(options.Required("history"))).Replay();
        if (game.Finished) throw new ArgumentException("This game is complete; there is no next move.");
        var (bot, time) = Settings(options);
        var action = BotActions.Choose(bot, game, game.Frame.Decisions[0], new RandomSource(1337), Deadline.After(TimeSpan.FromMilliseconds(time)));
        Console.WriteLine(JsonSerializer.Serialize(new { move = EnclosureHistoryMove.FromAction(action), coordinate = Describe(action), bot = bot.Name }, JsonFiles.Options));
        return 0;
    }

    public static int Play(Options options)
    {
        if (options.Get("game", "enclosure") != "enclosure") throw new ArgumentException("Use play-demo for Tic-Tac-Toe.");
        var human = options.Get("seat", "blue") switch { "blue" => 0, "red" => 1, _ => throw new ArgumentException("--seat must be blue or red.") };
        var history = options.Has("load") ? EnclosureHistory.Parse(File.ReadAllText(options.Required("load"))) : new EnclosureHistory();
        var game = history.Replay();
        var actions = history.ActionIds().ToList();
        var (bot, time) = Settings(options);
        var random = new RandomSource(1337);
        void Save() { if (options.Has("save")) JsonFiles.WriteAtomic(options.Required("save"), EnclosureHistory.FromActions(actions)); }
        Console.WriteLine("Enter a line as A10 D11. Commands: hint, undo, save, quit. Coordinates: A–S, 1–19.");
        while (!game.Finished)
        {
            Draw(game);
            var legal = game.GenerateLegalActions();
            int action;
            if (legal.Length == 1 && legal[0] == Enclosure.PassActionId) action = legal[0];
            else if (game.Turn != human)
                action = BotActions.Choose(bot, game, game.Frame.Decisions[0], random, Deadline.After(TimeSpan.FromMilliseconds(time)));
            else
            {
                Console.Write("Your move> ");
                var line = Console.ReadLine()?.Trim();
                if (line is null || line.Equals("quit", StringComparison.OrdinalIgnoreCase)) { Save(); return 0; }
                if (line.Equals("save", StringComparison.OrdinalIgnoreCase))
                { Save(); Console.WriteLine(options.Has("save") ? "Saved." : "Start with --save PATH to save this game."); continue; }
                if (line.Equals("hint", StringComparison.OrdinalIgnoreCase))
                {
                    var hint = new EnclosureAlphaBetaBot(new(time)).Search(game, Deadline.After(TimeSpan.FromMilliseconds(time)));
                    Console.WriteLine($"Hint: {Describe(hint.Action)} (depth {hint.CompletedDepth})"); continue;
                }
                if (line.Equals("undo", StringComparison.OrdinalIgnoreCase))
                {
                    var replay = new Enclosure(); var target = -1;
                    for (var i = 0; i < actions.Count; i++) { if (replay.Turn == human) target = i; replay.Play(actions[i]); }
                    if (target >= 0) { actions.RemoveRange(target, actions.Count - target); game = EnclosureHistory.FromActions(actions).Replay(); Save(); }
                    continue;
                }
                try
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2) throw new ArgumentException("Enter two coordinates, for example A10 D11.");
                    var from = ParsePoint(parts[0]); var to = ParsePoint(parts[1]);
                    action = Enclosure.GetActionId(from.X, from.Y, to.X, to.Y);
                    if (!legal.Contains(action)) throw new ArgumentException("That line is not legal in this position.");
                }
                catch (ArgumentException e) { Console.WriteLine(e.Message); continue; }
            }
            Console.WriteLine($"{(game.Turn == 0 ? "Blue" : "Red")}: {Describe(action)}");
            game.Play(action); actions.Add(action); Save();
        }
        Draw(game); Save();
        Console.WriteLine(game.Scores[0] == game.Scores[1] ? "Draw." : $"{(game.Scores[0] > game.Scores[1] ? "Blue" : "Red")} wins.");
        return 0;
    }

    private static EnclosurePoint ParsePoint(string text)
    {
        if (text.Length < 2 || char.ToUpperInvariant(text[0]) is < 'A' or > 'S' ||
            !int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row is < 1 or > 19)
            throw new ArgumentException("Coordinates range from A1 to S19.");
        return new(char.ToUpperInvariant(text[0]) - 'A', row - 1);
    }
    internal static string Describe(int action)
    {
        if (action == Enclosure.PassActionId) return "pass";
        var s = Enclosure.DecodeAction(action);
        return $"{(char)('A' + s.From.X)}{s.From.Y + 1} → {(char)('A' + s.To.X)}{s.To.Y + 1}";
    }
    private static void Draw(Enclosure game)
    {
        Console.WriteLine($"\nMove {game.MoveNumber}/120 · {(game.Turn == 0 ? "Blue" : "Red")} · {game.ActionsRemaining} placements left");
        Console.WriteLine($"Score Blue {game.Scores[0]:0.##} (+{game.Areas[0]:0.##}) / Red {game.Scores[1]:0.##} (+{game.Areas[1]:0.##})");
        var blue = game.Nodes(0).ToHashSet(); var red = game.Nodes(1).ToHashSet();
        Console.WriteLine("    A B C D E F G H I J K L M N O P Q R S");
        for (var y = 18; y >= 0; y--)
            Console.WriteLine($"{y + 1,2}  " + string.Join(' ', Enumerable.Range(0, 19).Select(x =>
                blue.Contains(new(x, y)) ? "B" : red.Contains(new(x, y)) ? "R" : ".")));
        for (var player = 0; player < 2; player++)
            Console.WriteLine($"{(player == 0 ? "Blue" : "Red")} lines (* protected): " + string.Join(", ", game.Segments(player)
                .Select(s => Describe(s.ActionId) + (s.Invincible ? "*" : ""))));
    }
}
