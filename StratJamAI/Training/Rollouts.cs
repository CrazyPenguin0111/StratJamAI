using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;

namespace StratJamAI.Training;

public sealed class OpponentPool(IGameDefinition definition, int capacity)
{
    private readonly List<NetworkWeights> snapshots = [];
    private readonly List<CpuNetwork> evaluators = [];
    public IReadOnlyList<NetworkWeights> Snapshots => snapshots;
    public void Add(NetworkWeights weights)
    {
        snapshots.Add(weights);
        evaluators.Add(new CpuNetwork(definition.Spec, weights));
        if (snapshots.Count > capacity) { snapshots.RemoveAt(0); evaluators.RemoveAt(0); }
    }
    public IBot Draw(RandomSource random)
    {
        var choice = random.NextDouble();
        if (choice < 0.2) return new RandomBot();
        if (choice < 0.5 && definition.CreateTeacher() is { } teacher) return teacher;
        return snapshots.Count == 0 ? new RandomBot() :
            new PolicyBot(evaluators[random.NextInt(evaluators.Count)], sample: true);
    }
}

public sealed record RolloutStats(long Decisions, long Episodes, long Truncations, double MeanReturn);

public sealed class RolloutCollector
{
    private sealed class Slot(IGameAdapter game, RandomSource random, int initialSeat)
    {
        public readonly IGameAdapter Game = game;
        public readonly RandomSource Random = random;
        public int LearnerSeat = initialSeat;
        public int Steps;
        public bool NeedsReset = true;
        public IBot Opponent = new RandomBot();
    }
    private readonly Slot[] slots;
    private readonly OpponentPool pool;
    private readonly TrainingConfig config;
    private readonly int workers;
    private long episodes;
    private long truncations;
    private double returnSum;
    private readonly object statsLock = new();
    public RolloutStats Stats { get; private set; } = new(0, 0, 0, 0);
    public ulong[] RandomStates => slots.Select(s => s.Random.State).ToArray();

    public RolloutCollector(IGameDefinition definition, TrainingConfig config, OpponentPool pool, int workers,
        ulong[]? randomStates = null)
    {
        this.config = config;
        this.pool = pool;
        this.workers = Math.Max(1, workers);
        slots = Enumerable.Range(0, config.Environments).Select(i => new Slot(definition.Create(),
            new RandomSource(randomStates is not null && i < randomStates.Length ? randomStates[i] : config.Seed + (ulong)(i * 104729)),
            i % definition.Spec.PlayerCount)).ToArray();
    }

    public List<Transition> Collect(IPolicyEvaluator policy, Deadline deadline)
    {
        var samples = new List<Transition>(config.RolloutDecisions);
        long bytes = 0;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = workers };
        while (samples.Count < config.RolloutDecisions && !deadline.Expired)
        {
            var ready = new bool[slots.Length];
            Parallel.For(0, slots.Length, parallelOptions, i => ready[i] = Prepare(slots[i], deadline));
            if (deadline.Expired) break;
            var indices = Enumerable.Range(0, slots.Length).Where(i => ready[i])
                .Take(config.RolloutDecisions - samples.Count).ToArray();
            if (indices.Length == 0) break;
            var requests = indices.Select(i => slots[i].Game.Frame.Decisions.Single(d => d.Player == slots[i].LearnerSeat).Copy()).ToArray();
            var roundBytes = requests.Sum(d => 128L + 4L * d.Observation.Length + d.Actions.Sum(a => 32L + 4L * a.Features.Length));
            if (bytes + roundBytes > config.BufferMemoryMiB * 1024L * 1024L)
            {
                if (samples.Count == 0) throw new InvalidOperationException("One rollout round exceeds BufferMemoryMiB. Reduce environments or action features.");
                break;
            }
            var predictions = policy.Evaluate(requests);
            var round = new Transition?[indices.Length];
            var nextObservations = new float[indices.Length][];
            Parallel.For(0, indices.Length, parallelOptions, j =>
            {
                var slot = slots[indices[j]];
                var prediction = predictions[j];
                var actionIndex = slot.Random.Sample(prediction.Probabilities);
                var reward = 0f;
                var discount = 1f;
                var firstStep = true;
                while (!deadline.Expired)
                {
                    var frame = slot.Game.Frame;
                    if (!firstStep && (frame.Finished || slot.Steps >= config.MaxEpisodeSteps ||
                        frame.Decisions.Any(d => d.Player == slot.LearnerSeat))) break;
                    var actions = new Dictionary<int, int>();
                    // Every player sees the pre-step frame, including simultaneous opponents.
                    foreach (var decision in frame.Decisions)
                        actions.Add(decision.Player, decision.Player == slot.LearnerSeat
                            ? requests[j].Actions[actionIndex].Id
                            : BotActions.Choose(slot.Opponent, slot.Game, decision, slot.Random, deadline));
                    frame = slot.Game.Step(actions);
                    frame.Validate(slot.Game.Spec);
                    slot.Steps++;
                    reward += discount * frame.Rewards[slot.LearnerSeat];
                    discount *= config.Gamma;
                    firstStep = false;
                }
                if (deadline.Expired) return;
                var end = slot.Game.Frame;
                var boundary = end.Finished || slot.Steps >= config.MaxEpisodeSteps;
                if (!end.Terminated) nextObservations[j] = slot.Game.Observe(slot.LearnerSeat);
                round[j] = new()
                {
                    Decision = requests[j], Environment = indices[j], ActionIndex = actionIndex,
                    OldLogProbability = prediction.LogProbabilities[actionIndex], OldValue = prediction.Value,
                    Reward = reward, Discount = discount, Terminated = end.Terminated, EpisodeBoundary = boundary
                };
                if (boundary) Finish(slot, end.Terminated);
            });
            if (deadline.Expired) break; // Discard this incomplete round, never manufacture terminal transitions.
            var bootstrapObservations = nextObservations.Where(o => o is not null).ToArray();
            var nextValues = bootstrapObservations.Length == 0 ? [] : policy.Values(bootstrapObservations);
            var nextValueIndex = 0;
            for (var j = 0; j < round.Length; j++)
            {
                var transition = round[j]!;
                transition.NextValue = transition.Terminated ? 0 : nextValues[nextValueIndex++];
                samples.Add(transition);
            }
            bytes += roundBytes;
        }
        Advantages.Compute(samples, config.GaeLambda);
        Stats = new(Stats.Decisions + samples.Count, episodes, truncations, episodes == 0 ? 0 : returnSum / episodes);
        return samples;
    }

    private bool Prepare(Slot slot, Deadline deadline)
    {
        while (!deadline.Expired)
        {
            if (slot.NeedsReset)
            {
                slot.Game.Reset(slot.Random.NextUInt64()).Validate(slot.Game.Spec);
                slot.LearnerSeat = (slot.LearnerSeat + 1) % slot.Game.Spec.PlayerCount;
                slot.Steps = 0;
                slot.Opponent = pool.Draw(slot.Random);
                slot.NeedsReset = false;
            }
            if (slot.Game.Frame.Finished || slot.Steps >= config.MaxEpisodeSteps)
            {
                Finish(slot, slot.Game.Frame.Terminated);
                continue;
            }
            if (slot.Game.Frame.Decisions.Any(d => d.Player == slot.LearnerSeat)) return true;
            var actions = slot.Game.Frame.Decisions.ToDictionary(d => d.Player,
                d => BotActions.Choose(slot.Opponent, slot.Game, d, slot.Random, deadline));
            slot.Game.Step(actions).Validate(slot.Game.Spec);
            slot.Steps++;
        }
        return false;
    }
    private void Finish(Slot slot, bool terminal)
    {
        lock (statsLock)
        {
            if (terminal) { episodes++; returnSum += slot.Game.Frame.Returns[slot.LearnerSeat]; }
            else truncations++;
        }
        slot.NeedsReset = true;
    }
}
