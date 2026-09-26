using System.Reflection;
using StratJamAI.Core;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;
using StratJamAI.Learning;
using TorchSharp;
using TorchSharp.Modules;
using Xunit;
using static TorchSharp.torch;

namespace StratJamAI.Tests;

public sealed class NetworkOptimizationTests
{
    private static DecisionRequest Request(GameSpec spec, int actions, RandomSource random) => new(0,
        Enumerable.Range(0, spec.ObservationSize).Select(_ => (float)random.NextDouble() * 2 - 1).ToArray(),
        Enumerable.Range(0, actions).Select(i => new LegalAction(i * 7 + 3,
            Enumerable.Range(0, spec.ActionFeatureSize).Select(_ => (float)random.NextDouble() * 2 - 1).ToArray())).ToArray());

    [Theory]
    [InlineData(17, 13)]
    [InlineData(513, 65)]
    public void FactoredCpuMatchesOriginalJoinedHeadAndConcurrentArgmax(int hidden, int actionHidden)
    {
        var spec = new GameSpec("large", "test", 43, 11, 2, true, true, true, true);
        var random = new RandomSource(997);
        var weights = NetworkWeights.Create(spec, random, hidden, actionHidden);
        var decision = Request(spec, 361, random);
        var expected = Original(weights, decision);
        var model = new CpuNetwork(spec, weights);
        var actual = model.Evaluate(decision);
        Assert.InRange(Math.Abs(expected.Value - actual.Value), 0, 1e-5);
        AssertClose(expected.Probabilities, actual.Probabilities);
        AssertClose(expected.LogProbabilities, actual.LogProbabilities);
        var best = decision.Actions[Array.IndexOf(expected.Probabilities, expected.Probabilities.Max())].Id;
        Parallel.For(0, 12, _ => Assert.Equal(best, model.ArgMaxAction(decision, Deadline.Never)));
        Assert.Equal(decision.Actions[0].Id, model.ArgMaxAction(decision, Deadline.After(TimeSpan.Zero)));
        Array.Clear(weights.Policy2.Weight);
        Assert.Equal(decision.Actions[0].Id, model.ArgMaxAction(decision, Deadline.Never));
    }

    [Fact]
    public void FactoredTorchOutputsAndAllParameterGradientsMatchOriginalJoinedHead()
    {
        var spec = new GameSpec("large", "test", 43, 11, 2, true, true, true, true);
        var random = new RandomSource(997);
        var weights = NetworkWeights.Create(spec, random, 17, 13);
        var requests = new[] { Request(spec, 361, random), Request(spec, 3, random) };
        using var actual = new TorchNetwork(spec, weights, Devices.Select("cpu"));
        using var expected = new TorchNetwork(spec, weights, Devices.Select("cpu"));
        using var scope = NewDisposeScope();
        var a = actual.MakeBatch(requests); var b = expected.MakeBatch(requests);
        var (actualLogits, actualValues) = actual.Forward(a.Observations, a.Actions, a.Mask);
        Linear Layer(string name) => (Linear)typeof(TorchNetwork).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(expected)!;
        var hidden = Layer("encoder2").forward(Layer("encoder1").forward(b.Observations).relu()).relu();
        var repeated = hidden.unsqueeze(1).expand(requests.Length, b.MaxActions, expected.HiddenSize);
        var expectedLogits = Layer("policy2").forward(Layer("policy1").forward(cat([repeated, b.Actions], 2)).relu())
            .squeeze(-1).masked_fill(b.Mask.logical_not(), -1e9);
        var expectedValues = Layer("value").forward(hidden).squeeze(-1);
        AssertClose(expectedLogits.data<float>().ToArray(), actualLogits.data<float>().ToArray());
        AssertClose(expectedValues.data<float>().ToArray(), actualValues.data<float>().ToArray());
        var actualLoss = actualLogits.masked_select(a.Mask).pow(2).mean() + actualValues.pow(2).mean();
        var expectedLoss = expectedLogits.masked_select(b.Mask).pow(2).mean() + expectedValues.pow(2).mean();
        actualLoss.backward(); expectedLoss.backward();
        var actualParameters = actual.parameters().ToArray(); var expectedParameters = expected.parameters().ToArray();
        Assert.Equal(expectedParameters.Length, actualParameters.Length);
        for (var i = 0; i < actualParameters.Length; i++)
        {
            Assert.NotNull(actualParameters[i].grad); Assert.NotNull(expectedParameters[i].grad);
            AssertClose(expectedParameters[i].grad!.data<float>().ToArray(), actualParameters[i].grad!.data<float>().ToArray());
        }
    }

    private static void AssertClose(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, 1e-5 + 1e-4 * Math.Abs(expected[i]));
    }

    private static PolicyOutput Original(NetworkWeights weights, DecisionRequest request)
    {
        var first = new float[weights.Encoder1.Output]; var hidden = new float[weights.HiddenSize];
        weights.Encoder1.Forward(request.Observation, first, true); weights.Encoder2.Forward(first, hidden, true);
        var joined = new float[weights.Policy1.Input]; hidden.CopyTo(joined, 0);
        var actionHidden = new float[weights.ActionHiddenSize]; var logits = new float[request.Actions.Length];
        var value = new float[1]; weights.Value.Forward(hidden, value);
        for (var i = 0; i < request.Actions.Length; i++)
        {
            request.Actions[i].Features.CopyTo(joined, hidden.Length);
            weights.Policy1.Forward(joined, actionHidden, true); weights.Policy2.Forward(actionHidden, logits.AsSpan(i, 1));
        }
        var (probabilities, logs) = CpuNetwork.Softmax(logits);
        return new(probabilities, value[0], logs);
    }
}
