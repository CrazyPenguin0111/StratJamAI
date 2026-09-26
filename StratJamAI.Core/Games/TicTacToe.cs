using StratJamAI.Core.Bots;

namespace StratJamAI.Core.Games;

public sealed class TicTacToeDefinition : IGameDefinition
{
    public GameSpec Spec => TicTacToe.GameSpec;
    public IGameAdapter Create() => new TicTacToe();
    public IBot CreateTeacher() => new TicTacToeTacticalBot();
    public IBot CreateOracle() => new TicTacToeOracle();
}

public static class GameRegistry
{
    // Add the event's definition here once its simulator and protocol are available.
    public static IGameDefinition Get(string id) => id == "tic-tac-toe" ? new TicTacToeDefinition() :
        throw new ArgumentException($"Unknown game '{id}'. Available: tic-tac-toe. Implement IGameDefinition to add a game.");
}

public sealed class TicTacToe : ISearchableGame
{
    public static readonly GameSpec GameSpec = new("tic-tac-toe", "relative-planes-27-cell-onehot-9-v1", 27, 9, 2,
        Sequential: true, FullyObservable: true, Deterministic: true, ZeroSum: true);
    public static readonly int[][] Lines = [[0,1,2], [3,4,5], [6,7,8], [0,3,6], [1,4,7], [2,5,8], [0,4,8], [2,4,6]];
    private readonly int[] board = new int[9];
    private int toPlay;
    public GameSpec Spec => GameSpec;
    public GameFrame Frame { get; private set; } = null!;
    public TicTacToe() => Reset(0);
    public GameFrame Reset(ulong seed)
    {
        Array.Fill(board, -1);
        toPlay = 0;
        return Frame = MakeFrame([0, 0], false);
    }
    public float[] Observe(int player)
    {
        if (player is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(player));
        var observation = new float[27];
        for (var i = 0; i < 9; i++)
            observation[(board[i] == player ? 0 : board[i] == -1 ? 18 : 9) + i] = 1;
        return observation;
    }
    public GameFrame Step(IReadOnlyDictionary<int, int> actions)
    {
        if (Frame.Finished || actions.Count != 1 || !actions.TryGetValue(toPlay, out var cell) ||
            cell is < 0 or > 8 || board[cell] != -1)
            throw new ArgumentException("A move must name the acting player and an empty cell.");
        board[cell] = toPlay;
        var won = Lines.Any(line => line.All(i => board[i] == toPlay));
        float[] rewards = won ? (toPlay == 0 ? [1, -1] : [-1, 1]) : [0, 0];
        toPlay = 1 - toPlay;
        return Frame = MakeFrame(rewards, won || board.All(x => x != -1));
    }
    private GameFrame MakeFrame(float[] rewards, bool finished)
    {
        if (finished) return new([], rewards, true, false, (float[])rewards.Clone());
        var actions = Enumerable.Range(0, 9).Where(i => board[i] == -1).Select(i =>
        {
            var features = new float[9];
            features[i] = 1;
            return new LegalAction(i, features);
        }).ToArray();
        return new([new(toPlay, Observe(toPlay), actions)], rewards, false, false, [0, 0]);
    }
    public ISearchableGame Fork()
    {
        var copy = new TicTacToe { toPlay = toPlay };
        Array.Copy(board, copy.board, board.Length);
        copy.Frame = copy.MakeFrame((float[])Frame.Rewards.Clone(), Frame.Terminated);
        return copy;
    }
}

public sealed class TicTacToeTacticalBot : IBot
{
    public string Name => "tactical";
    public int ChooseAction(DecisionRequest decision, BotContext context)
    {
        foreach (var plane in new[] { 0, 9 })
            foreach (var action in decision.Actions)
                if (TicTacToe.Lines.Any(line => line.Contains(action.Id) &&
                    line.Where(i => i != action.Id).All(i => decision.Observation[plane + i] == 1)))
                    return action.Id;
        if (decision.Actions.Any(a => a.Id == 4)) return 4;
        var corners = decision.Actions.Where(a => a.Id is 0 or 2 or 6 or 8).ToArray();
        var choices = corners.Length > 0 ? corners : decision.Actions;
        return choices[context.Random.NextInt(choices.Length)].Id;
    }
}

public sealed class TicTacToeOracle : IBot
{
    public string Name => "minimax";
    public int ChooseAction(DecisionRequest decision, BotContext context)
    {
        // Derive the board from the same legal observation provided to every other bot.
        var board = Enumerable.Range(0, 9).Select(i => decision.Observation[i] == 1 ? 1 :
            decision.Observation[9 + i] == 1 ? -1 : 0).ToArray();
        var best = new List<int>();
        var score = -2;
        foreach (var action in decision.Actions)
        {
            if (context.Deadline.Expired) break;
            board[action.Id] = 1;
            var value = Solve(board, -1, context.Deadline);
            board[action.Id] = 0;
            if (context.Deadline.Expired) break;
            if (value > score) { score = value; best.Clear(); }
            if (value == score) best.Add(action.Id);
        }
        return best.Count == 0 ? decision.Actions[0].Id : best[context.Random.NextInt(best.Count)];
    }
    private static int Solve(int[] board, int player, Deadline deadline)
    {
        foreach (var line in TicTacToe.Lines)
            if (board[line[0]] != 0 && line.All(i => board[i] == board[line[0]])) return board[line[0]];
        if (board.All(x => x != 0) || deadline.Expired) return 0;
        var best = player == 1 ? -2 : 2;
        for (var i = 0; i < 9; i++)
        {
            if (board[i] != 0) continue;
            board[i] = player;
            var value = Solve(board, -player, deadline);
            board[i] = 0;
            best = player == 1 ? Math.Max(best, value) : Math.Min(best, value);
            if (best == player) break;
        }
        return best;
    }
}
