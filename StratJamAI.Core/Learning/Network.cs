using System.Numerics;
using StratJamAI.Core.Games;

namespace StratJamAI.Core.Learning;

public sealed record DenseWeights(int Input, int Output, float[] Weight, float[] Bias)
{
    public void Validate()
    {
        if (Input < 1 || Output < 1 || Weight.Length != (long)Input * Output || Bias.Length != Output ||
            Weight.Any(x => !float.IsFinite(x)) || Bias.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Invalid dense layer weights.");
    }
    public void Forward(ReadOnlySpan<float> input, Span<float> output, bool relu = false)
    {
        for (var row = 0; row < Output; row++)
        {
            var weights = Weight.AsSpan(row * Input, Input);
            float sum = Bias[row];
            var i = 0;
            for (; i <= Input - Vector<float>.Count; i += Vector<float>.Count)
                sum += Vector.Dot(new Vector<float>(input.Slice(i)), new Vector<float>(weights.Slice(i)));
            for (; i < Input; i++) sum += input[i] * weights[i];
            output[row] = relu ? Math.Max(0, sum) : sum;
        }
    }
}

public sealed record NetworkWeights(DenseWeights Encoder1, DenseWeights Encoder2, DenseWeights Policy1,
    DenseWeights Policy2, DenseWeights Value)
{
    public int HiddenSize => Encoder2.Output;
    public int ActionHiddenSize => Policy1.Output;
    public void Validate(GameSpec spec)
    {
        spec.Validate();
        foreach (var layer in new[] { Encoder1, Encoder2, Policy1, Policy2, Value }) layer.Validate();
        if (Encoder1.Input != spec.ObservationSize || Encoder2.Input != Encoder1.Output ||
            Policy1.Input != Encoder2.Output + spec.ActionFeatureSize || Policy2.Input != Policy1.Output ||
            Policy2.Output != 1 || Value.Input != Encoder2.Output || Value.Output != 1)
            throw new InvalidDataException("The network and game feature schemas do not match.");
    }
    public static NetworkWeights Create(GameSpec spec, RandomSource random, int hiddenSize = 128, int actionHiddenSize = 64)
    {
        DenseWeights Layer(int input, int output, float gain)
        {
            var weights = new float[checked(input * output)];
            var scale = gain * MathF.Sqrt(3f / input);
            for (var i = 0; i < weights.Length; i++) weights[i] = ((float)random.NextDouble() * 2 - 1) * scale;
            return new(input, output, weights, new float[output]);
        }
        return new(Layer(spec.ObservationSize, hiddenSize, MathF.Sqrt(2)), Layer(hiddenSize, hiddenSize, MathF.Sqrt(2)),
            Layer(hiddenSize + spec.ActionFeatureSize, actionHiddenSize, MathF.Sqrt(2)),
            Layer(actionHiddenSize, 1, 0.01f), Layer(hiddenSize, 1, 1));
    }
}

public sealed record PolicyOutput(float[] Probabilities, float Value, float[] LogProbabilities);

public interface IPolicyEvaluator
{
    PolicyOutput[] Evaluate(IReadOnlyList<DecisionRequest> decisions);
    float[] Values(IReadOnlyList<float[]> observations);
}

// No native libraries or ML runtime are required by the exported bot.
public sealed class CpuNetwork : IPolicyEvaluator
{
    public GameSpec Spec { get; }
    public NetworkWeights Weights { get; }
    public CpuNetwork(GameSpec spec, NetworkWeights weights)
    {
        weights.Validate(spec);
        Spec = spec;
        Weights = weights;
    }
    public PolicyOutput Evaluate(DecisionRequest decision)
    {
        decision.Validate(Spec);
        var h1 = new float[Weights.Encoder1.Output];
        var h2 = new float[Weights.Encoder2.Output];
        Weights.Encoder1.Forward(decision.Observation, h1, true);
        Weights.Encoder2.Forward(h1, h2, true);
        Span<float> value = stackalloc float[1];
        Weights.Value.Forward(h2, value);
        var joined = new float[Weights.Policy1.Input];
        h2.CopyTo(joined, 0);
        var actionHidden = new float[Weights.Policy1.Output];
        var logits = new float[decision.Actions.Length];
        for (var i = 0; i < decision.Actions.Length; i++)
        {
            decision.Actions[i].Features.CopyTo(joined, h2.Length);
            Weights.Policy1.Forward(joined, actionHidden, true);
            Weights.Policy2.Forward(actionHidden, logits.AsSpan(i, 1));
        }
        var (probabilities, logs) = Softmax(logits);
        if (!float.IsFinite(value[0])) throw new InvalidDataException("Non-finite value prediction.");
        return new(probabilities, value[0], logs);
    }
    public PolicyOutput[] Evaluate(IReadOnlyList<DecisionRequest> decisions) => decisions.Select(Evaluate).ToArray();
    public float[] Values(IReadOnlyList<float[]> observations) => observations.Select(Value).ToArray();
    private float Value(float[] observation)
    {
        if (observation.Length != Spec.ObservationSize || observation.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Invalid value observation.");
        var h1 = new float[Weights.Encoder1.Output];
        var h2 = new float[Weights.Encoder2.Output];
        Span<float> value = stackalloc float[1];
        Weights.Encoder1.Forward(observation, h1, true);
        Weights.Encoder2.Forward(h1, h2, true);
        Weights.Value.Forward(h2, value);
        return value[0];
    }
    public static (float[] Probabilities, float[] Logs) Softmax(float[] logits)
    {
        if (logits.Length == 0 || logits.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Invalid logits.");
        var max = logits.Max();
        var logSum = max + Math.Log(logits.Sum(x => Math.Exp(x - max)));
        var logs = logits.Select(x => (float)(x - logSum)).ToArray();
        return (logs.Select(MathF.Exp).ToArray(), logs);
    }
}
