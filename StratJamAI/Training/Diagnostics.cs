using System.Diagnostics;
using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;
using StratJamAI.Learning;
using static TorchSharp.torch;

namespace StratJamAI.Training;

public static class Diagnostics
{
    public static object Doctor(string deviceName)
    {
        var watch = Stopwatch.StartNew();
        var device = Devices.Select(deviceName);
        var game = new TicTacToe();
        var request = game.Frame.Decisions[0];
        var weights = NetworkWeights.Create(game.Spec, new RandomSource(73));
        using var model = new TorchNetwork(game.Spec, weights, device);
        using (var scope = NewDisposeScope())
        {
            var batch = model.MakeBatch([request]);
            var (logits, values) = model.Forward(batch.Observations, batch.Actions, batch.Mask);
            var loss = logits.pow(2).mean() + (values - 1).pow(2).mean();
            loss.backward();
            var gradients = model.parameters().Where(p => p.grad is not null).Select(p => p.grad!.abs().sum().item<float>()).ToArray();
            if (gradients.Length == 0 || gradients.Any(x => !float.IsFinite(x)) || gradients.Sum() == 0)
                throw new InvalidOperationException("The forward/backward device check failed.");
        }
        var native = model.Evaluate([request])[0];
        var portable = new CpuNetwork(game.Spec, model.ExportWeights()).Evaluate(request);
        var difference = native.Probabilities.Zip(portable.Probabilities, (a, b) => Math.Abs(a - b))
            .Append(Math.Abs(native.Value - portable.Value)).Max();
        if (difference > 1e-4) throw new InvalidOperationException($"Export parity failed: {difference}.");
        return new { device = deviceName, forwardBackwardPassed = true, exportMaxError = difference,
            torchSharp = typeof(TorchSharp.torch).Assembly.GetName().Version?.ToString(), elapsedSeconds = watch.Elapsed.TotalSeconds };
    }

    public static int Calibrate(IGameDefinition definition, int environments, Deadline deadline,
        Action<string>? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(environments, 1);
        var candidates = new[] { 1, 4, 8, 16 }.Where(n => n <= Math.Max(1, Environment.ProcessorCount) && n <= environments).ToArray();
        var bestWorkers = 1;
        double bestRate = -1;
        foreach (var workers in candidates)
        {
            if (deadline.Expired) break;
            var calls = 0L;
            var watch = Stopwatch.StartNew();
            Parallel.For(0, environments, new ParallelOptions { MaxDegreeOfParallelism = workers }, index =>
            {
                var game = definition.Create();
                var random = new RandomSource((ulong)index + 83973);
                var bot = new RandomBot();
                var frame = game.Reset(random.NextUInt64());
                for (var step = 0; step < 128 && !deadline.Expired; step++)
                {
                    if (frame.Finished) frame = game.Reset(random.NextUInt64());
                    var actions = frame.Decisions.ToDictionary(d => d.Player,
                        d => BotActions.Choose(bot, game, d, random, deadline));
                    frame = game.Step(actions);
                    frame.Validate(game.Spec);
                    Interlocked.Increment(ref calls);
                }
            });
            var rate = calls / Math.Max(0.000001, watch.Elapsed.TotalSeconds);
            log?.Invoke($"benchmark: {workers} workers, {rate:F0} simulator steps/s");
            if (rate > bestRate) { bestRate = rate; bestWorkers = workers; }
        }
        return bestWorkers;
    }
}
