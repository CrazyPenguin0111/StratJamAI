using System.Diagnostics;
using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;

namespace StratJamAI.Training;

public sealed record EnclosureTiming(double MeanMilliseconds, double P50Milliseconds, double P95Milliseconds,
    double MeanAllocatedBytes);
public sealed record EnclosureSearchBenchmark(double BudgetMilliseconds, EnclosureTiming Timing,
    double MeanNodes, int MinimumCompletedDepth, int MaximumCompletedDepth);
public sealed record EnclosureInferenceBenchmark(EnclosureTiming OriginalJoined, EnclosureTiming Factored,
    double MeanSpeedup, double MaximumOutputError, bool ArgMaxMatches);
public sealed record EnclosurePositionBenchmark(string Position, int MoveNumber, int LegalActions,
    double SimulatorStepsPerSecond, EnclosureSearchBenchmark[] Search, EnclosureInferenceBenchmark Inference);
public sealed record EnclosureBenchmarkReport(DateTimeOffset CreatedUtc, int Samples, ulong Seed,
    EnclosurePositionBenchmark[] Positions, EvaluationResult? Matches);

/// <summary>Reproducible CPU fixtures and seat-balanced smoke matches; run a Release build for useful timings.</summary>
public static class EnclosureBenchmarks
{
    public static EnclosureBenchmarkReport Run(int samples = 5, bool compare = false, int pairs = 2,
        ulong seed = 72891, Action<string>? log = null)
    {
        if (samples < 1 || pairs < 1) throw new ArgumentException("Benchmark samples and comparison pairs must be positive.");
        var positions = FixedPositions(seed);
        var weights = NetworkWeights.Create(Enclosure.GameSpec, new RandomSource(91381));
        var network = new CpuNetwork(Enclosure.GameSpec, weights);
        var bot = new EnclosureAlphaBetaBot();
        // Static geometry tables, policy methods and search are warmed before any recorded sample.
        foreach (var (_, game) in positions)
        {
            var request = game.Frame.Decisions.Single();
            for (var i = 0; i < 64; i++) { OriginalEvaluate(request, weights); network.Evaluate(request); }
            bot.Search(game, TimeSpan.FromMilliseconds(100));
        }
        var results = new List<EnclosurePositionBenchmark>();
        foreach (var (name, game) in positions)
        {
            log?.Invoke($"benchmark: {name}, move {game.MoveNumber}, {game.GenerateLegalActions().Length} legal actions");
            var search = new List<EnclosureSearchBenchmark>();
            foreach (var milliseconds in new[] { 5d, 50d, 1000d })
            {
                var latencies = new double[samples];
                var allocations = new long[samples];
                var nodes = new long[samples];
                var depths = new int[samples];
                for (var i = 0; i < samples; i++)
                {
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var started = Stopwatch.GetTimestamp();
                    var result = bot.Search(game, TimeSpan.FromMilliseconds(milliseconds));
                    latencies[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    allocations[i] = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    nodes[i] = result.Nodes; depths[i] = result.CompletedDepth;
                    if (!game.IsLegal(result.Action)) throw new InvalidDataException("The benchmark search returned an illegal move.");
                }
                search.Add(new(milliseconds, Timing(latencies, allocations), nodes.Average(), depths.Min(), depths.Max()));
            }
            var decision = game.Frame.Decisions.Single();
            var original = OriginalEvaluate(decision, weights);
            var factored = network.Evaluate(decision);
            var error = Math.Max(Math.Abs(original.Value - factored.Value),
                original.Probabilities.Zip(factored.Probabilities, (a, b) => Math.Abs(a - b)).Max());
            error = Math.Max(error, original.LogProbabilities.Zip(factored.LogProbabilities, (a, b) => Math.Abs(a - b)).Max());
            if (error > 1e-4) throw new InvalidDataException($"Joined/factored inference diverged: {error}.");
            var originalBest = Enumerable.Range(0, original.Probabilities.Length).MaxBy(i => original.Probabilities[i]);
            var argMaxMatches = network.ArgMaxAction(decision, Deadline.Never) == decision.Actions[originalBest].Id;
            if (!argMaxMatches) throw new InvalidDataException("Joined/factored inference selected different actions.");
            var originalTiming = Measure(() => OriginalEvaluate(decision, weights), Math.Max(20, samples));
            var factoredTiming = Measure(() => network.Evaluate(decision), Math.Max(20, samples));
            results.Add(new(name, game.MoveNumber, decision.Actions.Length, SimulatorRate(game), search.ToArray(),
                new(originalTiming, factoredTiming, originalTiming.MeanMilliseconds / factoredTiming.MeanMilliseconds, error, argMaxMatches)));
        }
        EvaluationResult? matches = null;
        if (compare)
        {
            log?.Invoke($"benchmark: {pairs} seat-balanced pairs per opponent, 50 ms per move; tactical and MCTS opponents");
            matches = Evaluation.Run(new EnclosureDefinition(), new EnclosureAlphaBetaBot(new(50)),
                [new EnclosureTacticalBot(), new MctsBot(new(Simulations: int.MaxValue, MoveMilliseconds: 50))],
                pairs, seed, Deadline.After(TimeSpan.FromSeconds(Math.Max(180, pairs * 90))), moveMilliseconds: 50);
        }
        return new(DateTimeOffset.UtcNow, samples, seed, results.ToArray(), matches);
    }

    private static List<(string Name, Enclosure Game)> FixedPositions(ulong seed)
    {
        var game = new Enclosure();
        var result = new List<(string, Enclosure)> { ("opening", game.Copy()) };
        var random = new RandomSource(seed);
        foreach (var (target, name) in new[] { (60, "middle"), (110, "late") })
        {
            while (game.MoveNumber < target)
            {
                var actions = game.GenerateLegalActions();
                game.Play(actions[random.NextInt(actions.Length)]);
            }
            result.Add((name, game.Copy()));
        }
        return result;
    }

    private static double SimulatorRate(Enclosure position)
    {
        var scratch = position.Copy();
        var count = 0;
        var watch = Stopwatch.StartNew();
        do
        {
            scratch.CopyFrom(position);
            var actions = scratch.GenerateLegalActions();
            scratch.Play(actions[count % actions.Length]);
            _ = scratch.Areas[0]; // Include resulting geometry, even between a player's consecutive actions.
            count++;
        } while (watch.Elapsed.TotalMilliseconds < 150);
        return count / watch.Elapsed.TotalSeconds;
    }

    private static EnclosureTiming Measure(Func<PolicyOutput> operation, int samples)
    {
        const int batch = 10;
        var milliseconds = new double[samples];
        var bytes = new long[samples];
        for (var i = 0; i < samples; i++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var j = 0; j < batch; j++) GC.KeepAlive(operation());
            milliseconds[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds / batch;
            bytes[i] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / batch;
        }
        return Timing(milliseconds, bytes);
    }

    private static EnclosureTiming Timing(double[] milliseconds, long[] bytes)
    {
        Array.Sort(milliseconds);
        double Percentile(double p) => milliseconds[Math.Clamp((int)Math.Ceiling(p * milliseconds.Length) - 1, 0, milliseconds.Length - 1)];
        return new(milliseconds.Average(), Percentile(0.5), Percentile(0.95), bytes.Average());
    }

    // Retain the original joined-head calculation as a benchmark reference, including its allocations
    // and softmax. This uses identical weights and validated decisions, not a smaller comparison model.
    private static PolicyOutput OriginalEvaluate(DecisionRequest decision, NetworkWeights weights)
    {
        decision.Validate(Enclosure.GameSpec);
        var h1 = new float[weights.Encoder1.Output];
        var h2 = new float[weights.Encoder2.Output];
        weights.Encoder1.Forward(decision.Observation, h1, true);
        weights.Encoder2.Forward(h1, h2, true);
        Span<float> value = stackalloc float[1];
        weights.Value.Forward(h2, value);
        var joined = new float[weights.Policy1.Input];
        h2.CopyTo(joined, 0);
        var hidden = new float[weights.Policy1.Output];
        var logits = new float[decision.Actions.Length];
        for (var i = 0; i < decision.Actions.Length; i++)
        {
            decision.Actions[i].Features.CopyTo(joined, h2.Length);
            weights.Policy1.Forward(joined, hidden, true);
            weights.Policy2.Forward(hidden, logits.AsSpan(i, 1));
        }
        if (logits.Length == 0 || logits.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Invalid reference logits.");
        var max = logits.Max();
        var logSum = max + Math.Log(logits.Sum(x => Math.Exp(x - max)));
        var logs = logits.Select(x => (float)(x - logSum)).ToArray();
        return new(logs.Select(MathF.Exp).ToArray(), value[0], logs);
    }
}
