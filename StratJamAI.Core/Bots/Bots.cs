using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;

namespace StratJamAI.Core.Bots;

public sealed record BotContext(RandomSource Random, Deadline Deadline, ISearchableGame? SearchState = null);

public interface IBot
{
    string Name { get; }
    int ChooseAction(DecisionRequest decision, BotContext context);
}

public sealed class RandomBot : IBot
{
    public string Name => "random";
    public int ChooseAction(DecisionRequest decision, BotContext context) =>
        decision.Actions[context.Random.NextInt(decision.Actions.Length)].Id;
}

public sealed class PolicyBot(CpuNetwork network, bool sample = false) : IBot
{
    public string Name => "policy";
    public int ChooseAction(DecisionRequest decision, BotContext context)
    {
        if (context.Deadline.Expired) return decision.Actions[0].Id;
        var probabilities = network.Evaluate(decision).Probabilities;
        var index = sample ? context.Random.Sample(probabilities) : Array.IndexOf(probabilities, probabilities.Max());
        return decision.Actions[index].Id;
    }
}

public static class BotActions
{
    public static int Choose(IBot bot, IGameAdapter game, DecisionRequest decision, RandomSource random, Deadline deadline)
    {
        decision.Validate(game.Spec);
        var searchable = game.Spec.SupportsSearch ? game as ISearchableGame : null;
        var action = bot.ChooseAction(decision, new(random, deadline, searchable));
        if (!decision.Actions.Any(a => a.Id == action))
            throw new InvalidDataException($"Bot '{bot.Name}' selected illegal action {action}.");
        return action;
    }
}

public sealed record SearchOptions(int Simulations = 256, double MoveMilliseconds = 50, float Exploration = 1.4f,
    int MaxDepth = 256)
{
    public void Validate()
    {
        if (Simulations < 1 || !double.IsFinite(MoveMilliseconds) || MoveMilliseconds <= 0 || MaxDepth < 1 ||
            !float.IsFinite(Exploration) || Exploration <= 0)
            throw new ArgumentException("Search limits and exploration must be finite and positive.");
    }
}

public sealed class MctsBot : IBot
{
    private readonly CpuNetwork? network;
    private readonly SearchOptions options;
    public string Name => network is null ? "search" : "search-policy";
    public MctsBot(SearchOptions options, CpuNetwork? network = null)
    {
        options.Validate();
        this.options = options;
        this.network = network;
    }
    private sealed class Node
    {
        public int Visits;
        public List<Edge>? Edges;
    }
    private sealed class Edge(int action, float prior, int player)
    {
        public readonly int Action = action;
        public readonly float Prior = prior;
        public readonly int Player = player;
        public readonly Node Child = new();
        public int Visits;
        public double Value;
    }
    public int ChooseAction(DecisionRequest decision, BotContext context)
    {
        // A fallback is available even if no complete simulation fits the move budget.
        var fallback = decision.Actions[0].Id;
        var state = context.SearchState;
        if (state is null || !state.Spec.SupportsSearch)
            throw new NotSupportedException("PUCT requires an explicitly searchable, deterministic, sequential, full-information two-player zero-sum game.");
        if (state.Frame.Finished || state.Frame.Decisions[0].Player != decision.Player ||
            !state.Frame.Decisions[0].Actions.Select(a => a.Id).SequenceEqual(decision.Actions.Select(a => a.Id)))
            throw new InvalidDataException("Search state does not match the decision.");
        var deadline = context.Deadline.Limit(TimeSpan.FromMilliseconds(options.MoveMilliseconds));
        if (deadline.Expired) return fallback;
        var root = new Node();
        for (var simulation = 0; simulation < options.Simulations && !deadline.Expired; simulation++)
        {
            var game = state.Fork();
            var node = root;
            var path = new List<Edge>();
            float[]? returns = null;
            var depth = 0;
            while (!deadline.Expired && depth++ < options.MaxDepth)
            {
                if (game.Frame.Finished) { returns = game.Frame.Returns; break; }
                var request = game.Frame.Decisions.Single();
                if (node.Edges is null)
                {
                    var output = network?.Evaluate(request);
                    node.Edges = request.Actions.Select((a, i) => new Edge(a.Id,
                        output?.Probabilities[i] ?? 1f / request.Actions.Length, request.Player)).ToList();
                    if (output is not null)
                    {
                        var value = Math.Clamp(output.Value, -1, 1);
                        returns = request.Player == 0 ? [value, -value] : [-value, value];
                    }
                    else returns = Rollout(game, context.Random, deadline, options.MaxDepth - depth);
                    break;
                }
                var edge = node.Edges.MaxBy(e => (e.Visits == 0 ? 0 : e.Value / e.Visits) +
                    options.Exploration * e.Prior * Math.Sqrt(node.Visits + 1) / (1 + e.Visits))!;
                path.Add(edge);
                game.Step(new Dictionary<int, int> { [request.Player] = edge.Action });
                node = edge.Child;
            }
            if (deadline.Expired) break; // Never back up an unfinished rollout as a draw.
            returns ??= [0, 0]; // Explicit depth-limit evaluation for the search, not a game result.
            root.Visits++;
            foreach (var edge in path)
            {
                edge.Visits++;
                edge.Value += returns[edge.Player]; // Correct even if a player receives an extra turn.
                edge.Child.Visits++;
            }
        }
        return root.Edges?.Where(e => e.Visits > 0).OrderByDescending(e => e.Visits)
            .ThenByDescending(e => e.Value / e.Visits).FirstOrDefault()?.Action ?? fallback;
    }
    private static float[]? Rollout(ISearchableGame game, RandomSource random, Deadline deadline, int depth)
    {
        while (!game.Frame.Finished && depth-- > 0 && !deadline.Expired)
        {
            var decision = game.Frame.Decisions.Single();
            game.Step(new Dictionary<int, int> { [decision.Player] = decision.Actions[random.NextInt(decision.Actions.Length)].Id });
        }
        return deadline.Expired ? null : game.Frame.Finished ? game.Frame.Returns : [0, 0];
    }
}
