using StratJamAI.Core;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;
using StratJamAI.Learning;
using StratJamAI.Training;
using Xunit;

namespace StratJamAI.Tests;

public sealed class LearningTests
{
    [Fact]
    public void TensorPpoObjectiveMatchesAnIndependentClippingCalculation()
    {
        var game = new TicTacToe();
        var request = game.Frame.Decisions[0];
        using var network = new TorchNetwork(game.Spec, NetworkWeights.Create(game.Spec, new RandomSource(991)), Devices.Select("cpu"));
        var prediction = network.Evaluate([request])[0];
        var ratios = new float[] { 2, 2, 0.5f, 0.5f };
        var advantages = new float[] { 1, -1, 1, -1 };
        var samples = ratios.Select((ratio, i) => new Transition
        {
            Decision = request, ActionIndex = 0, OldValue = prediction.Value,
            OldLogProbability = prediction.LogProbabilities[0] - MathF.Log(ratio), Advantage = advantages[i], Return = 0
        }).ToArray();
        using var optimizer = new PpoOptimizer(network, new TrainingConfig
            { Device = "cpu", Epochs = 1, MinibatchSize = 4, TargetKl = 10, EntropyWeight = 0, ValueWeight = 0 });
        var result = optimizer.Update(samples, new RandomSource(3), Deadline.After(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, result.Minibatches);
        // -(1.2 - 2 + .5 - .8) / 4 = .275, including both signs of advantage.
        Assert.InRange(Math.Abs(result.PolicyLoss - 0.275), 0, 1e-5);
    }

    [Fact]
    public void PaddedBatchesRespectTheMemoryGuard()
    {
        var game = new TicTacToe();
        using var network = new TorchNetwork(game.Spec, NetworkWeights.Create(game.Spec, new RandomSource(12)), Devices.Select("cpu"))
            { MaxBatchBytes = 1 };
        Assert.Throws<InvalidOperationException>(() => network.Evaluate([game.Frame.Decisions[0]]));
    }

    [Fact]
    public void TorchPaddingIsMaskedAndPortableInferenceMatches()
    {
        var device = Devices.Select("cpu");
        var game = new TicTacToe();
        var first = game.Frame.Decisions[0].Copy();
        foreach (var cell in new[] { 0, 1, 2, 4, 3, 5, 7, 6 })
            game.Step(new Dictionary<int, int> { [game.Frame.Decisions[0].Player] = cell });
        var last = game.Frame.Decisions[0];
        Assert.Single(last.Actions);
        using var network = new TorchNetwork(game.Spec, NetworkWeights.Create(game.Spec, new RandomSource(71)), device);
        var predictions = network.Evaluate([first, last]);
        var portable = new CpuNetwork(game.Spec, network.ExportWeights());
        foreach (var (request, output) in new[] { (first, predictions[0]), (last, predictions[1]) })
        {
            var cpu = portable.Evaluate(request);
            Assert.InRange(Math.Abs(cpu.Value - output.Value), 0, 1e-5);
            for (var i = 0; i < output.Probabilities.Length; i++)
                Assert.InRange(Math.Abs(cpu.Probabilities[i] - output.Probabilities[i]), 0, 1e-5);
        }
        Assert.Equal(1, predictions[1].Probabilities[0]);
        Assert.Equal(0, predictions[1].LogProbabilities[0]);
    }

    [Fact]
    public void PpoLearnsAContextualBanditWithoutSearchOrATeacher()
    {
        var spec = new GameSpec("bandit", "v1", 2, 2, 1, true, true, true, false);
        var random = new RandomSource(141);
        using var network = new TorchNetwork(spec, NetworkWeights.Create(spec, random, 32, 16), Devices.Select("cpu"));
        var config = new TrainingConfig { Device = "cpu", LearningRate = 0.003f, Epochs = 4,
            MinibatchSize = 64, TargetKl = 0.1f, EntropyWeight = 0.01f };
        using var optimizer = new PpoOptimizer(network, config);
        DecisionRequest Request(int correct) => new(0, correct == 0 ? [1, 0] : [0, 1],
            [new(11, [1, 0]), new(22, [0, 1])]);
        var before = network.Evaluate([Request(0), Request(1)]);
        for (var update = 0; update < 60; update++)
        {
            var requests = Enumerable.Range(0, 256).Select(i => Request(i % 2)).ToArray();
            var outputs = network.Evaluate(requests);
            var samples = requests.Select((request, i) =>
            {
                var action = random.Sample(outputs[i].Probabilities);
                return new Transition { Decision = request, Environment = i, ActionIndex = action,
                    OldLogProbability = outputs[i].LogProbabilities[action], OldValue = outputs[i].Value,
                    Reward = action == i % 2 ? 1 : -1, Discount = 0, Terminated = true, EpisodeBoundary = true };
            }).ToArray();
            Advantages.Compute(samples, 0.95f);
            optimizer.Update(samples, random, Deadline.After(TimeSpan.FromSeconds(20)));
        }
        var after = network.Evaluate([Request(0), Request(1)]);
        var initialAccuracy = (before[0].Probabilities[0] + before[1].Probabilities[1]) / 2;
        var finalAccuracy = (after[0].Probabilities[0] + after[1].Probabilities[1]) / 2;
        Assert.True(finalAccuracy > 0.9, $"Bandit accuracy {initialAccuracy:F3} -> {finalAccuracy:F3}");
        Assert.True(finalAccuracy > initialAccuracy + 0.3);
    }

    [Fact]
    public void OptimizerCheckpointsResumeTheSameUpdateAndRecoverPreviousGeneration()
    {
        using var directory = new TemporaryDirectory();
        var game = new TicTacToe();
        var config = new TrainingConfig { Device = "cpu" };
        using var first = new TorchNetwork(game.Spec, NetworkWeights.Create(game.Spec, new RandomSource(12)), Devices.Select("cpu"));
        using var optimizer = new PpoOptimizer(first, config);
        var requests = Enumerable.Repeat(game.Frame.Decisions[0], 8).ToArray();
        var labels = Enumerable.Repeat(4, 8).ToArray();
        optimizer.Imitate(requests, labels);
        var weights = first.ExportWeights();
        var state = new CheckpointState(1, game.Spec, config, weights, weights, 0.5, 71, [51], [weights], 8, 1, 1, true, 1);
        Checkpoints.Save(directory.Path, state, optimizer);
        var loaded = Checkpoints.Load(directory.Path, game.Spec);
        using var resumed = new TorchNetwork(game.Spec, loaded.State.Weights, Devices.Select("cpu"));
        using var resumedOptimizer = new PpoOptimizer(resumed, config);
        resumedOptimizer.Load(loaded.OptimizerPath);
        optimizer.Imitate(requests, labels);
        resumedOptimizer.Imitate(requests, labels);
        var expected = first.Evaluate(requests)[0];
        var actual = resumed.Evaluate(requests)[0];
        Assert.Equal(expected.Value, actual.Value, 5);
        for (var i = 0; i < expected.Probabilities.Length; i++)
            Assert.Equal(expected.Probabilities[i], actual.Probabilities[i], 5);
        Checkpoints.Save(directory.Path, state with { Weights = first.ExportWeights(), Updates = 2, ConsumedSeconds = 2 }, optimizer);
        var newest = Checkpoints.Load(directory.Path, game.Spec);
        File.WriteAllText(newest.OptimizerPath, "incomplete write");
        var recovered = Checkpoints.Load(directory.Path, game.Spec);
        Assert.Equal(1, recovered.State.Updates);
        Assert.Equal(1, recovered.State.ConsumedSeconds);
    }
}
