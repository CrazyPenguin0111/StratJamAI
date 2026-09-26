using StratJamAI.Core;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;
using StratJamAI.Training;
using TorchSharp.Modules;
using static TorchSharp.torch;

namespace StratJamAI.Learning;

public sealed record UpdateResult(int Minibatches, double PolicyLoss, double ValueLoss, double Entropy, double ApproximateKl);

public sealed class PpoOptimizer : IDisposable
{
    private readonly TorchNetwork network;
    private readonly TrainingConfig config;
    private readonly Adam optimizer;
    public PpoOptimizer(TorchNetwork network, TrainingConfig config)
    {
        this.network = network;
        this.config = config;
        optimizer = optim.Adam(network.parameters(), lr: config.LearningRate);
    }

    public UpdateResult Update(IReadOnlyList<Transition> transitions, RandomSource random, Deadline deadline)
    {
        if (transitions.Count == 0) return new(0, 0, 0, 0, 0);
        var order = Enumerable.Range(0, transitions.Count).ToArray();
        var count = 0;
        double policyTotal = 0, valueTotal = 0, entropyTotal = 0, klTotal = 0;
        var stop = false;
        for (var epoch = 0; epoch < config.Epochs && !stop && !deadline.Expired; epoch++)
        {
            random.Shuffle(order);
            for (var start = 0; start < order.Length && !deadline.Expired; start += config.MinibatchSize)
            {
                using var scope = NewDisposeScope();
                var items = order.Skip(start).Take(config.MinibatchSize).Select(i => transitions[i]).ToArray();
                var batch = network.MakeBatch(items.Select(t => t.Decision).ToArray());
                var (logits, values) = network.Forward(batch.Observations, batch.Actions, batch.Mask);
                var logProbabilities = logits.log_softmax(1);
                var indices = tensor(items.Select(t => (long)t.ActionIndex).ToArray(), device: network.Device).unsqueeze(1);
                var selectedLogs = logProbabilities.gather(1, indices).squeeze(1);
                var oldLogs = tensor(items.Select(t => t.OldLogProbability).ToArray(), device: network.Device);
                var advantages = tensor(items.Select(t => t.Advantage).ToArray(), device: network.Device);
                var returns = tensor(items.Select(t => t.Return).ToArray(), device: network.Device);
                var oldValues = tensor(items.Select(t => t.OldValue).ToArray(), device: network.Device);
                var ratio = (selectedLogs - oldLogs).exp();
                var policyLoss = -minimum(ratio * advantages, ratio.clamp(1 - config.Clip, 1 + config.Clip) * advantages).mean();
                var clippedValues = oldValues + (values - oldValues).clamp(-config.Clip, config.Clip);
                var valueLoss = maximum((values - returns).pow(2), (clippedValues - returns).pow(2)).mean() * 0.5;
                // Finite masked logits keep 0 * log(0) out of the entropy calculation.
                var entropy = -(logProbabilities.exp() * logProbabilities).sum(1).mean();
                var approximateKl = ((ratio - 1) - (selectedLogs - oldLogs)).mean().item<float>();
                if (!float.IsFinite(approximateKl)) throw new ArithmeticException("Non-finite PPO KL divergence.");
                if (approximateKl > config.TargetKl) { stop = true; break; }
                var loss = policyLoss + config.ValueWeight * valueLoss - config.EntropyWeight * entropy;
                Apply(loss);
                count++;
                policyTotal += policyLoss.item<float>();
                valueTotal += valueLoss.item<float>();
                entropyTotal += entropy.item<float>();
                klTotal += approximateKl;
            }
        }
        return count == 0 ? new(0, 0, 0, 0, 0) :
            new(count, policyTotal / count, valueTotal / count, entropyTotal / count, klTotal / count);
    }

    public double Imitate(IReadOnlyList<DecisionRequest> decisions, int[] targetIndices)
    {
        using var scope = NewDisposeScope();
        if (targetIndices.Length != decisions.Count) throw new ArgumentException("Teacher targets do not match observations.");
        for (var i = 0; i < targetIndices.Length; i++)
            if (targetIndices[i] < 0 || targetIndices[i] >= decisions[i].Actions.Length)
                throw new ArgumentException("Teacher selected an illegal action index.");
        var batch = network.MakeBatch(decisions);
        var (logits, _) = network.Forward(batch.Observations, batch.Actions, batch.Mask);
        var targets = tensor(targetIndices.Select(x => (long)x).ToArray(), device: network.Device).unsqueeze(1);
        var loss = -logits.log_softmax(1).gather(1, targets).mean();
        Apply(loss);
        return loss.item<float>();
    }

    private void Apply(Tensor loss)
    {
        if (!float.IsFinite(loss.item<float>())) throw new ArithmeticException("Non-finite training loss.");
        optimizer.zero_grad();
        loss.backward();
        var norm = nn.utils.clip_grad_norm_(network.parameters(), config.GradientClip);
        if (!double.IsFinite(norm)) throw new ArithmeticException("Non-finite gradients.");
        optimizer.step();
    }

    public void Save(string path) => optimizer.save_state_dict(path);
    public void Load(string path) => optimizer.load_state_dict(path);
    public void Dispose() => optimizer.Dispose();
}
