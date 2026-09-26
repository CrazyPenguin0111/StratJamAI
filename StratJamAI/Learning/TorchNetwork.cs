using StratJamAI.Core;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;

namespace StratJamAI.Learning;

public sealed class TorchNetwork : nn.Module, IPolicyEvaluator
{
    private readonly Linear encoder1;
    private readonly Linear encoder2;
    private readonly Linear policy1;
    private readonly Linear policy2;
    private readonly Linear value;
    public GameSpec Spec { get; }
    public Device Device { get; }
    public int HiddenSize { get; }
    public int ActionHiddenSize { get; }
    public long MaxBatchBytes { get; set; } = 512L * 1024 * 1024;

    public TorchNetwork(GameSpec spec, NetworkWeights weights, Device device) : base("CandidateActorCritic")
    {
        weights.Validate(spec);
        Spec = spec;
        Device = device;
        HiddenSize = weights.HiddenSize;
        ActionHiddenSize = weights.ActionHiddenSize;
        encoder1 = nn.Linear(weights.Encoder1.Input, weights.Encoder1.Output, device: device);
        encoder2 = nn.Linear(weights.Encoder2.Input, weights.Encoder2.Output, device: device);
        policy1 = nn.Linear(weights.Policy1.Input, weights.Policy1.Output, device: device);
        policy2 = nn.Linear(weights.Policy2.Input, weights.Policy2.Output, device: device);
        value = nn.Linear(weights.Value.Input, weights.Value.Output, device: device);
        RegisterComponents();
        LoadWeights(weights);
    }

    public (Tensor Logits, Tensor Values) Forward(Tensor observations, Tensor actions, Tensor legalMask)
    {
        using var scope = NewDisposeScope();
        var hidden = encoder2.forward(encoder1.forward(observations).relu()).relu();
        // W[h,a] + b = Wh*h + b + Wa*a. Views preserve the original named parameter
        // and optimizer state while avoiding a [batch, actions, hidden + features] allocation.
        var shared = nn.functional.linear(hidden, policy1.weight.narrow(1, 0, HiddenSize), policy1.bias);
        var candidates = nn.functional.linear(actions,
            policy1.weight.narrow(1, HiddenSize, Spec.ActionFeatureSize));
        var logits = policy2.forward((candidates + shared.unsqueeze(1)).relu()).squeeze(-1)
            .masked_fill(legalMask.logical_not(), -1e9);
        var values = value.forward(hidden).squeeze(-1);
        return (logits.MoveToOuterDisposeScope(), values.MoveToOuterDisposeScope());
    }

    public PolicyOutput[] Evaluate(IReadOnlyList<DecisionRequest> decisions)
    {
        if (decisions.Count == 0) return [];
        using var scope = NewDisposeScope();
        using var noGrad = no_grad();
        var batch = MakeBatch(decisions);
        var (logits, values) = Forward(batch.Observations, batch.Actions, batch.Mask);
        var logs = logits.log_softmax(1);
        var probabilities = logs.exp().cpu().contiguous().data<float>().ToArray();
        var logArray = logs.cpu().contiguous().data<float>().ToArray();
        var valueArray = values.cpu().contiguous().data<float>().ToArray();
        return decisions.Select((d, i) => new PolicyOutput(
            probabilities.AsSpan(i * batch.MaxActions, d.Actions.Length).ToArray(), valueArray[i],
            logArray.AsSpan(i * batch.MaxActions, d.Actions.Length).ToArray())).ToArray();
    }

    public float[] Values(IReadOnlyList<float[]> observations)
    {
        if (observations.Count == 0) return [];
        using var scope = NewDisposeScope();
        using var noGrad = no_grad();
        foreach (var observation in observations)
            if (observation.Length != Spec.ObservationSize || observation.Any(x => !float.IsFinite(x)))
                throw new InvalidDataException("Invalid bootstrap observation.");
        var input = tensor(observations.SelectMany(o => o).ToArray(), device: Device)
            .reshape(observations.Count, Spec.ObservationSize);
        return value.forward(encoder2.forward(encoder1.forward(input).relu()).relu()).squeeze(-1)
            .cpu().contiguous().data<float>().ToArray();
    }

    public sealed record Batch(Tensor Observations, Tensor Actions, Tensor Mask, int MaxActions);

    // Tensors are owned by the caller's dispose scope.
    public Batch MakeBatch(IReadOnlyList<DecisionRequest> decisions)
    {
        if (decisions.Count == 0) throw new ArgumentException("An empty tensor batch is not valid.");
        foreach (var decision in decisions) decision.Validate(Spec);
        var maxActions = decisions.Max(d => d.Actions.Length);
        // Conservative forward/backward allowance including host staging, activations, gradients,
        // logits/masks and loss workspaces. Hidden state is projected once per decision.
        var estimatedBytes = checked(24L * decisions.Count * maxActions *
            (Spec.ActionFeatureSize + ActionHiddenSize + 4L) +
            24L * decisions.Count * (Spec.ObservationSize + encoder1.weight.shape[0] + HiddenSize + ActionHiddenSize));
        if (estimatedBytes > MaxBatchBytes)
            throw new InvalidOperationException("The padded tensor batch exceeds BufferMemoryMiB. Reduce environments/minibatch size or increase the memory budget; actions are never silently discarded.");
        var observations = new float[checked(decisions.Count * Spec.ObservationSize)];
        var actions = new float[checked(decisions.Count * maxActions * Spec.ActionFeatureSize)];
        var mask = new bool[checked(decisions.Count * maxActions)];
        for (var i = 0; i < decisions.Count; i++)
        {
            decisions[i].Observation.CopyTo(observations, i * Spec.ObservationSize);
            for (var a = 0; a < decisions[i].Actions.Length; a++)
            {
                decisions[i].Actions[a].Features.CopyTo(actions, (i * maxActions + a) * Spec.ActionFeatureSize);
                mask[i * maxActions + a] = true;
            }
        }
        return new(tensor(observations, device: Device).reshape(decisions.Count, Spec.ObservationSize),
            tensor(actions, device: Device).reshape(decisions.Count, maxActions, Spec.ActionFeatureSize),
            tensor(mask, device: Device).reshape(decisions.Count, maxActions), maxActions);
    }

    public NetworkWeights ExportWeights()
    {
        using var scope = NewDisposeScope();
        using var noGrad = no_grad();
        DenseWeights Export(Linear layer) => new((int)layer.weight.shape[1], (int)layer.weight.shape[0],
            layer.weight.detach().cpu().contiguous().data<float>().ToArray(),
            layer.bias!.detach().cpu().contiguous().data<float>().ToArray());
        return new(Export(encoder1), Export(encoder2), Export(policy1), Export(policy2), Export(value));
    }

    public void LoadWeights(NetworkWeights weights)
    {
        weights.Validate(Spec);
        using var scope = NewDisposeScope();
        using var noGrad = no_grad();
        void Load(Linear layer, DenseWeights data)
        {
            layer.weight.copy_(tensor(data.Weight, device: Device).reshape(data.Output, data.Input));
            layer.bias!.copy_(tensor(data.Bias, device: Device));
        }
        Load(encoder1, weights.Encoder1);
        Load(encoder2, weights.Encoder2);
        Load(policy1, weights.Policy1);
        Load(policy2, weights.Policy2);
        Load(value, weights.Value);
    }
}

public static class Devices
{
    public static Device Select(string name)
    {
        set_num_threads(1);
        if (name == "cpu") return CPU;
        if (name != "cuda") throw new ArgumentException("Device must be 'cpu' or 'cuda'.");
        if (!cuda.is_available())
            throw new InvalidOperationException("CUDA is unavailable. Run doctor and check the NVIDIA driver/native packages, or explicitly select --device cpu.");
        return CUDA;
    }
}
