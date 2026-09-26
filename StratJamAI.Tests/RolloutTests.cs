using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;
using StratJamAI.Training;
using Xunit;

namespace StratJamAI.Tests;

public sealed class RolloutTests
{
    [Fact]
    public void RewardsIncludeOpponentTurnsAndUseElapsedStepDiscounts()
    {
        var definition = new FixtureDefinition(() => new SequenceGame());
        var config = new TrainingConfig { Environments = 1, RolloutDecisions = 2, Gamma = 0.5f, GaeLambda = 1 };
        var collector = new RolloutCollector(definition, config, new OpponentPool(definition, 2), 1);
        var batch = collector.Collect(new ConstantPolicy(10), Deadline.After(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, batch.Count);
        Assert.Equal(2, batch[0].Reward); // 1 + gamma * 2, from a transition on the opponent's turn.
        Assert.Equal(0.25f, batch[0].Discount);
        Assert.Equal(2.75f, batch[0].Return); // 1 + .5*2 + .5*.5*3
        Assert.Equal(3, batch[1].Reward);
        Assert.True(batch[1].Terminated);
        Assert.Equal(0, batch[1].NextValue);
    }

    [Fact]
    public void TimeLimitsBootstrapTheFinalVisibleObservation()
    {
        var definition = new FixtureDefinition(() => new SequenceGame());
        var config = new TrainingConfig { Environments = 1, RolloutDecisions = 1, Gamma = 0.5f, MaxEpisodeSteps = 1 };
        var collector = new RolloutCollector(definition, config, new OpponentPool(definition, 2), 1);
        var transition = Assert.Single(collector.Collect(new ConstantPolicy(10), Deadline.After(TimeSpan.FromSeconds(3))));
        Assert.False(transition.Terminated);
        Assert.True(transition.EpisodeBoundary);
        Assert.Equal(6, transition.Return);
    }

    [Fact]
    public void SimultaneousPlayersReceivePreStepObservationsAndNoSearchState()
    {
        var definition = new FixtureDefinition(() => new SimultaneousGame());
        var game = definition.Create();
        foreach (var request in game.Frame.Decisions)
            BotActions.Choose(new PrivacyBot(), game, request, new RandomSource(17), Deadline.Never);
        var config = new TrainingConfig { Environments = 1, RolloutDecisions = 4 };
        var collector = new RolloutCollector(definition, config, new OpponentPool(definition, 2), 1);
        var batch = collector.Collect(new ConstantPolicy(0), Deadline.After(TimeSpan.FromSeconds(3)));
        Assert.Equal(4, batch.Count);
        Assert.All(batch, t => { Assert.Equal(0, t.Decision.Observation[0]); Assert.True(t.Terminated); });
        Assert.Throws<NotSupportedException>(() => BotActions.Choose(new MctsBot(new()), game,
            game.Frame.Decisions[0], new RandomSource(1), Deadline.Never));
    }

    [Fact]
    public void SearchBacksUpValuesCorrectlyThroughExtraTurns()
    {
        var game = new ExtraTurnGame();
        var action = BotActions.Choose(new MctsBot(new(Simulations: 512, MoveMilliseconds: 1000)), game,
            game.Frame.Decisions[0], new RandomSource(89), Deadline.After(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, action); // Move 1 gives player 0 another turn and a forced win; move 2 draws.
    }

    private sealed class PrivacyBot : IBot
    {
        public string Name => "privacy-check";
        public int ChooseAction(DecisionRequest decision, BotContext context)
        {
            Assert.Null(context.SearchState);
            Assert.Equal(0, decision.Observation[0]);
            return decision.Actions[0].Id;
        }
    }
}

internal sealed class FixtureDefinition(Func<IGameAdapter> factory) : IGameDefinition
{
    public GameSpec Spec => factory().Spec;
    public IGameAdapter Create() => factory();
    public IBot? CreateTeacher() => null;
    public IBot? CreateOracle() => null;
}

internal sealed class ConstantPolicy(float value) : IPolicyEvaluator
{
    public PolicyOutput[] Evaluate(IReadOnlyList<DecisionRequest> decisions) => decisions.Select(d =>
        new PolicyOutput(Enumerable.Repeat(1f / d.Actions.Length, d.Actions.Length).ToArray(), value,
            Enumerable.Repeat(-MathF.Log(d.Actions.Length), d.Actions.Length).ToArray())).ToArray();
    public float[] Values(IReadOnlyList<float[]> observations) => observations.Select(_ => value).ToArray();
}

internal sealed class SequenceGame : IGameAdapter
{
    private int stage;
    public GameSpec Spec { get; } = new("sequence", "v1", 1, 1, 2, true, true, true, false);
    public GameFrame Frame { get; private set; }
    public SequenceGame() => Frame = Reset(0);
    public float[] Observe(int player) => [stage / 3f];
    public GameFrame Reset(ulong seed) { stage = 0; return Frame = Make([0, 0]); }
    public GameFrame Step(IReadOnlyDictionary<int, int> actions)
    {
        if (actions.Count != 1 || !actions.ContainsKey(stage == 1 ? 0 : 1)) throw new InvalidDataException("Wrong actor.");
        stage++;
        return Frame = Make([0, stage]);
    }
    private GameFrame Make(float[] rewards) => stage == 3 ? new([], rewards, true, false, [-1, 1]) :
        new([new(stage == 1 ? 0 : 1, Observe(0), [new(71, [1])])], rewards, false, false, [0, 0]);
}

internal sealed class SimultaneousGame : ISearchableGame
{
    public GameSpec Spec { get; } = new("simultaneous", "v1", 1, 1, 3, false, false, true, false);
    public GameFrame Frame { get; private set; }
    public SimultaneousGame() => Frame = Reset(0);
    public float[] Observe(int player) => [0];
    public GameFrame Reset(ulong seed) => Frame = new(Enumerable.Range(0, 3)
        .Select(p => new DecisionRequest(p, [0], [new(11, [0]), new(22, [1])])).ToArray(), [0, 0, 0], false, false, [0, 0, 0]);
    public GameFrame Step(IReadOnlyDictionary<int, int> actions)
    {
        Assert.Equal(3, actions.Count);
        Assert.All(Frame.Decisions, d => Assert.Equal(0, d.Observation[0]));
        return Frame = new([], [1, 0, -1], true, false, [1, 0, -1]);
    }
    public ISearchableGame Fork() => throw new InvalidOperationException("A hidden-information game must never be searched.");
}

internal sealed class ExtraTurnGame : ISearchableGame
{
    private int stage;
    public GameSpec Spec { get; } = new("extra-turn", "v1", 1, 1, 2, true, true, true, true);
    public GameFrame Frame { get; private set; }
    public ExtraTurnGame() => Frame = Reset(0);
    public GameFrame Reset(ulong seed)
    {
        stage = 0;
        return Frame = new([new(0, [0], [new(2, [0]), new(1, [1])])], [0, 0], false, false, [0, 0]);
    }
    public float[] Observe(int player) => [stage];
    public GameFrame Step(IReadOnlyDictionary<int, int> actions)
    {
        var action = actions[0];
        if (stage == 0 && action == 1)
        {
            stage = 1;
            return Frame = new([new(0, [1], [new(3, [1]), new(4, [0])])], [0, 0], false, false, [0, 0]);
        }
        float[] rewards = stage == 0 ? [0, 0] : action == 3 ? [1, -1] : [-1, 1];
        stage = 2;
        return Frame = new([], rewards, true, false, rewards);
    }
    public ISearchableGame Fork() => new ExtraTurnGame { stage = stage, Frame = Frame };
}
