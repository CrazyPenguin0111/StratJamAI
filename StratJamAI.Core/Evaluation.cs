using System.Diagnostics;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;

namespace StratJamAI.Core;

public sealed record OpponentScore(string Opponent, int Games, double MeanScore);
public sealed record EvaluationResult(string Bot, bool Complete, int Games, double MeanScore,
    double Lower95, double Upper95, double MeanMoveMilliseconds, double P95MoveMilliseconds,
    double ElapsedSeconds, OpponentScore[] Opponents);

public static class Evaluation
{
    public static IBot[] FixedOpponents(IGameDefinition definition)
    {
        var opponents = new List<IBot> { new RandomBot() };
        if (definition.CreateTeacher() is { } teacher) opponents.Add(teacher);
        if (definition.CreateOracle() is { } oracle) opponents.Add(oracle);
        return opponents.ToArray();
    }

    public static EvaluationResult Run(IGameDefinition definition, IBot candidate, IReadOnlyList<IBot> opponents,
        int seedPairs, ulong seed, Deadline deadline, double moveMilliseconds = 50, int maxSteps = 512)
    {
        if (seedPairs < 1 || opponents.Count == 0 || maxSteps < 1 || moveMilliseconds <= 0)
            throw new ArgumentException("Evaluation requires positive limits and at least one opponent.");
        var watch = Stopwatch.StartNew();
        var latencies = new List<double>();
        var groups = new List<double>();
        var scores = new List<OpponentScore>();
        var games = 0;
        double scoreSum = 0;
        var complete = true;
        for (var opponentIndex = 0; opponentIndex < opponents.Count && complete; opponentIndex++)
        {
            var opponentGames = 0;
            double opponentSum = 0;
            for (var pair = 0; pair < seedPairs && complete; pair++)
            {
                double groupScore = 0;
                var groupGames = 0;
                for (var seat = 0; seat < definition.Spec.PlayerCount; seat++)
                {
                    if (deadline.Expired) { complete = false; break; }
                    var game = definition.Create();
                    var gameSeed = seed + (ulong)(pair * 104729 + opponentIndex * 1000003);
                    var frame = game.Reset(gameSeed);
                    frame.Validate(game.Spec);
                    // A bot's sampling/search must not advance its opponent's random stream.
                    var randoms = Enumerable.Range(0, definition.Spec.PlayerCount).Select(player =>
                        new RandomSource(gameSeed + (ulong)(seat * 8191) + (ulong)(player + 1) * 32452843UL)).ToArray();
                    var steps = 0;
                    while (!frame.Finished && steps++ < maxSteps && !deadline.Expired)
                    {
                        var actions = new Dictionary<int, int>();
                        foreach (var decision in frame.Decisions)
                        {
                            var bot = decision.Player == seat ? candidate : opponents[opponentIndex];
                            var start = Stopwatch.GetTimestamp();
                            var action = BotActions.Choose(bot, game, decision, randoms[decision.Player],
                                deadline.Limit(TimeSpan.FromMilliseconds(moveMilliseconds)));
                            if (decision.Player == seat) latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                            actions.Add(decision.Player, action);
                        }
                        if (deadline.Expired) break;
                        frame = game.Step(actions);
                        frame.Validate(game.Spec);
                    }
                    // Incomplete games are never recorded as draws or used to promote a bot.
                    if (!frame.Terminated) { complete = false; break; }
                    var value = frame.Returns[seat];
                    if (value is < -1 or > 1) throw new InvalidDataException("Evaluation returns must be normalized to [-1, 1].");
                    var score = (value + 1) * 0.5;
                    groupScore += score;
                    groupGames++;
                    opponentSum += score;
                    opponentGames++;
                    scoreSum += score;
                    games++;
                }
                if (groupGames == definition.Spec.PlayerCount) groups.Add(groupScore / groupGames);
            }
            scores.Add(new(opponents[opponentIndex].Name, opponentGames, opponentGames == 0 ? 0 : opponentSum / opponentGames));
        }
        var (lower, upper) = Bootstrap(groups);
        latencies.Sort();
        return new(candidate.Name, complete, games, games == 0 ? 0 : scoreSum / games, lower, upper,
            latencies.Count == 0 ? 0 : latencies.Average(),
            latencies.Count == 0 ? 0 : latencies[(int)Math.Floor((latencies.Count - 1) * 0.95)],
            watch.Elapsed.TotalSeconds, scores.ToArray());
    }

    // Resample complete seat-balanced groups, rather than treating correlated seat swaps as independent.
    private static (double Lower, double Upper) Bootstrap(List<double> values)
    {
        if (values.Count < 2) return (0, 1);
        var rng = new RandomSource(812873);
        var samples = new double[1000];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            double total = 0;
            for (var j = 0; j < values.Count; j++) total += values[rng.NextInt(values.Count)];
            samples[sample] = total / values.Count;
        }
        Array.Sort(samples);
        return (samples[25], samples[974]);
    }
}
