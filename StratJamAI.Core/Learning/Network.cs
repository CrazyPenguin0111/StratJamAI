using System.Buffers;
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
    // Scratch is private to each invocation: the same exported network can serve concurrent games.
    private const int StackFloats = 1024;
    private int ScratchSize => checked(Weights.Encoder1.Output + Weights.HiddenSize + 2 * Weights.ActionHiddenSize);

    public PolicyOutput Evaluate(DecisionRequest decision)
    {
        decision.Validate(Spec);
        var size = ScratchSize;
        float[]? rented = null;
        Span<float> scratch = size <= StackFloats ? stackalloc float[size] : (rented = ArrayPool<float>.Shared.Rent(size));
        try
        {
            EncodePolicy(decision.Observation, scratch);
            var hidden = scratch.Slice(Weights.Encoder1.Output, Weights.HiddenSize);
            Span<float> value = stackalloc float[1];
            Weights.Value.Forward(hidden, value);
            var shared = scratch.Slice(Weights.Encoder1.Output + Weights.HiddenSize, Weights.ActionHiddenSize);
            var actionHidden = scratch.Slice(Weights.Encoder1.Output + Weights.HiddenSize + Weights.ActionHiddenSize,
                Weights.ActionHiddenSize);
            var logits = new float[decision.Actions.Length];
            for (var i = 0; i < decision.Actions.Length; i++)
                logits[i] = ActionLogit(decision.Actions[i].Features, shared, actionHidden);
            var (probabilities, logs) = Softmax(logits);
            if (!float.IsFinite(value[0])) throw new InvalidDataException("Non-finite value prediction.");
            return new(probabilities, value[0], logs);
        }
        finally { if (rented is not null) ArrayPool<float>.Shared.Return(rented); }
    }

    // Returns an opaque action ID, with stable first-action tie breaking and a legal deadline fallback.
    // No value head, logits array, log probabilities, or softmax is needed for deterministic play.
    public int ArgMaxAction(DecisionRequest decision, Deadline deadline)
    {
        if (decision.Actions.Length == 0) throw new InvalidDataException("An action is required for policy inference.");
        var bestId = decision.Actions[0].Id;
        if (deadline.Expired) return bestId;
        decision.Validate(Spec);
        if (deadline.Expired) return bestId;
        var size = ScratchSize;
        float[]? rented = null;
        Span<float> scratch = size <= StackFloats ? stackalloc float[size] : (rented = ArrayPool<float>.Shared.Rent(size));
        try
        {
            EncodePolicy(decision.Observation, scratch);
            var shared = scratch.Slice(Weights.Encoder1.Output + Weights.HiddenSize, Weights.ActionHiddenSize);
            var actionHidden = scratch.Slice(Weights.Encoder1.Output + Weights.HiddenSize + Weights.ActionHiddenSize,
                Weights.ActionHiddenSize);
            var bestLogit = float.NegativeInfinity;
            foreach (var action in decision.Actions)
            {
                if (deadline.Expired) break;
                var logit = ActionLogit(action.Features, shared, actionHidden);
                if (!float.IsFinite(logit)) throw new InvalidDataException("Non-finite policy logit.");
                if (logit > bestLogit) { bestLogit = logit; bestId = action.Id; }
            }
            return bestId;
        }
        finally { if (rented is not null) ArrayPool<float>.Shared.Return(rented); }
    }

    private void EncodePolicy(ReadOnlySpan<float> observation, Span<float> scratch)
    {
        var first = scratch[..Weights.Encoder1.Output];
        var hidden = scratch.Slice(first.Length, Weights.HiddenSize);
        var shared = scratch.Slice(first.Length + hidden.Length, Weights.ActionHiddenSize);
        Weights.Encoder1.Forward(observation, first, true);
        Weights.Encoder2.Forward(first, hidden, true);
        // Keep the original [hidden, action] parameter layout for portable/checkpoint compatibility.
        for (var row = 0; row < shared.Length; row++)
            shared[row] = AddDot(Weights.Policy1.Bias[row], hidden,
                Weights.Policy1.Weight.AsSpan(row * Weights.Policy1.Input, hidden.Length));
    }

    private float ActionLogit(ReadOnlySpan<float> features, ReadOnlySpan<float> shared, Span<float> actionHidden)
    {
        for (var row = 0; row < shared.Length; row++)
            actionHidden[row] = Math.Max(0, AddDot(shared[row], features,
                Weights.Policy1.Weight.AsSpan(row * Weights.Policy1.Input + Weights.HiddenSize, features.Length)));
        Span<float> logit = stackalloc float[1];
        Weights.Policy2.Forward(actionHidden, logit);
        return logit[0];
    }

    private static float AddDot(float sum, ReadOnlySpan<float> input, ReadOnlySpan<float> weights)
    {
        var i = 0;
        for (; i <= input.Length - Vector<float>.Count; i += Vector<float>.Count)
            sum += Vector.Dot(new Vector<float>(input[i..]), new Vector<float>(weights[i..]));
        for (; i < input.Length; i++) sum += input[i] * weights[i];
        return sum;
    }

    public PolicyOutput[] Evaluate(IReadOnlyList<DecisionRequest> decisions) => decisions.Select(Evaluate).ToArray();
    public float[] Values(IReadOnlyList<float[]> observations) => observations.Select(Value).ToArray();
    private float Value(float[] observation)
    {
        if (observation.Length != Spec.ObservationSize || observation.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Invalid value observation.");
        var size = checked(Weights.Encoder1.Output + Weights.HiddenSize);
        float[]? rented = null;
        Span<float> scratch = size <= StackFloats ? stackalloc float[size] : (rented = ArrayPool<float>.Shared.Rent(size));
        try
        {
            var first = scratch[..Weights.Encoder1.Output];
            var hidden = scratch.Slice(first.Length, Weights.HiddenSize);
            Span<float> value = stackalloc float[1];
            Weights.Encoder1.Forward(observation, first, true);
            Weights.Encoder2.Forward(first, hidden, true);
            Weights.Value.Forward(hidden, value);
            if (!float.IsFinite(value[0])) throw new InvalidDataException("Non-finite value prediction.");
            return value[0];
        }
        finally { if (rented is not null) ArrayPool<float>.Shared.Return(rented); }
    }
    public static (float[] Probabilities, float[] Logs) Softmax(float[] logits)
    {
        if (logits.Length == 0) throw new InvalidDataException("Invalid logits.");
        var max = float.NegativeInfinity;
        foreach (var logit in logits)
        {
            if (!float.IsFinite(logit)) throw new InvalidDataException("Invalid logits.");
            max = Math.Max(max, logit);
        }
        double sum = 0;
        foreach (var logit in logits) sum += Math.Exp(logit - max);
        var logSum = max + Math.Log(sum);
        var probabilities = new float[logits.Length];
        var logs = new float[logits.Length];
        for (var i = 0; i < logits.Length; i++)
        {
            logs[i] = (float)(logits[i] - logSum);
            probabilities[i] = MathF.Exp(logs[i]);
        }
        return (probabilities, logs);
    }
}
