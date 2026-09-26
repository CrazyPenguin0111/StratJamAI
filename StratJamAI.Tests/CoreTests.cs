using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace StratJamAI.Tests;

public sealed class CoreTests
{
    [Fact]
    public void CandidateRandomConsumptionDoesNotChangeOpponentRandomness()
    {
        var opponent = new TracingRandomBot();
        var definition = new TicTacToeDefinition();
        Evaluation.Run(definition, new FixedActionBot(0), [opponent], 8, 7831, Deadline.After(TimeSpan.FromSeconds(10)));
        var expected = opponent.Actions.ToArray();
        opponent.Actions.Clear();
        Evaluation.Run(definition, new FixedActionBot(100), [opponent], 8, 7831, Deadline.After(TimeSpan.FromSeconds(10)));
        Assert.Equal(expected, opponent.Actions);
    }

    [Fact]
    public void SimultaneousPolicyArtifactsPreserveMixedStrategies()
    {
        var definition = new FixtureDefinition(() => new SimultaneousGame());
        var weights = NetworkWeights.Create(definition.Spec, new RandomSource(81));
        Array.Clear(weights.Policy2.Weight);
        var artifact = new BotArtifact(1, definition.Spec, weights, "policy", new(), DateTimeOffset.UtcNow, 0, "mixed strategy");
        var bot = artifact.CreateBot(definition);
        var request = definition.Create().Frame.Decisions[0];
        var context = new BotContext(new RandomSource(71), Deadline.Never);
        var choices = Enumerable.Range(0, 100).Select(_ => bot.ChooseAction(request, context)).ToHashSet();
        Assert.Equal(2, choices.Count);
    }

    private sealed class FixedActionBot(int draws) : IBot
    {
        public string Name => "fixed";
        public int ChooseAction(DecisionRequest decision, BotContext context)
        {
            for (var i = 0; i < draws; i++) context.Random.NextUInt64();
            return decision.Actions[0].Id;
        }
    }
    private sealed class TracingRandomBot : IBot
    {
        public List<int> Actions { get; } = [];
        public string Name => "tracing-random";
        public int ChooseAction(DecisionRequest decision, BotContext context)
        {
            var action = decision.Actions[context.Random.NextInt(decision.Actions.Length)].Id;
            Actions.Add(action);
            return action;
        }
    }

    [Fact]
    public void GameRejectsIllegalActionsAndForksIndependently()
    {
        var game = new TicTacToe();
        var copy = game.Fork();
        copy.Step(new Dictionary<int, int> { [0] = 4 });
        Assert.Equal(9, game.Frame.Decisions[0].Actions.Length);
        Assert.Equal(8, copy.Frame.Decisions[0].Actions.Length);
        Assert.Throws<ArgumentException>(() => copy.Step(new Dictionary<int, int> { [1] = 4 }));
        Assert.Throws<ArgumentException>(() => game.Step(new Dictionary<int, int> { [1] = 0 }));
    }

    [Fact]
    public void TerminalRewardsBelongToNamedPlayers()
    {
        var game = new TicTacToe();
        foreach (var cell in new[] { 0, 3, 1, 4, 2 })
            game.Step(new Dictionary<int, int> { [game.Frame.Decisions[0].Player] = cell });
        Assert.True(game.Frame.Terminated);
        Assert.Empty(game.Frame.Decisions);
        Assert.Equal(new float[] { 1, -1 }, game.Frame.Rewards);
        Assert.Equal(new float[] { 1, -1 }, game.Frame.Returns);
    }

    [Fact]
    public void InvalidFeaturesAndDuplicateActionsAreRejected()
    {
        var request = new TicTacToe().Frame.Decisions[0];
        Assert.Throws<InvalidDataException>(() => (request with { Actions = [] }).Validate(TicTacToe.GameSpec));
        Assert.Throws<InvalidDataException>(() => (request with { Actions = [request.Actions[0], request.Actions[0]] }).Validate(TicTacToe.GameSpec));
        request.Observation[0] = float.NaN;
        Assert.Throws<InvalidDataException>(() => request.Validate(TicTacToe.GameSpec));
    }

    [Fact]
    public void CandidateScoringIsEquivariantToActionOrder()
    {
        var game = new TicTacToe();
        var model = new CpuNetwork(game.Spec, NetworkWeights.Create(game.Spec, new RandomSource(17)));
        var request = game.Frame.Decisions[0];
        var forward = model.Evaluate(request);
        var backward = model.Evaluate(request with { Actions = request.Actions.Reverse().ToArray() });
        Assert.Equal(forward.Value, backward.Value);
        for (var i = 0; i < forward.Probabilities.Length; i++)
            Assert.InRange(Math.Abs(forward.Probabilities[i] - backward.Probabilities[^(i + 1)]), 0, 1e-6);
        Assert.InRange(forward.Probabilities.Sum(), 0.99999f, 1.00001f);
    }

    [Fact]
    public void GaeBootstrapsTruncationWithoutJoiningEpisodesOrPlayers()
    {
        Transition Make(int env, float reward, float value, float next, bool boundary, bool terminal) => new()
        {
            Decision = new TicTacToe().Frame.Decisions[0], Environment = env, Reward = reward, OldValue = value,
            NextValue = next, Discount = 0.5f, EpisodeBoundary = boundary, Terminated = terminal
        };
        var transitions = new[] { Make(0, 1, 2, 3, false, false), Make(1, 5, 2, 7, true, false),
            Make(0, 4, 3, 99, true, true), Make(1, 100, 0, 0, true, true) };
        Advantages.Compute(transitions, 1, normalize: false);
        Assert.Equal(3, transitions[0].Return);
        Assert.Equal(8.5f, transitions[1].Return);
        Assert.Equal(4, transitions[2].Return);
        Assert.Equal(100, transitions[3].Return);
    }

    [Theory]
    [InlineData(2, 1, 1.2)]
    [InlineData(2, -1, -2)]
    [InlineData(0.5, 1, 0.5)]
    [InlineData(0.5, -1, -0.8)]
    public void PpoClippingUsesThePessimisticObjective(double ratio, double advantage, double expected) =>
        Assert.Equal(expected, Advantages.ClippedSurrogate(Math.Log(ratio), 0, advantage, 0.2), 6);

    [Fact]
    public void SearchFindsImmediateWinAndPreservesOriginalState()
    {
        var game = new TicTacToe();
        foreach (var cell in new[] { 0, 3, 1, 4 })
            game.Step(new Dictionary<int, int> { [game.Frame.Decisions[0].Player] = cell });
        var request = game.Frame.Decisions[0];
        var bot = new MctsBot(new(Simulations: 1000, MoveMilliseconds: 2000));
        var action = BotActions.Choose(bot, game, request, new RandomSource(37), Deadline.After(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, action);
        Assert.Equal(5, game.Frame.Decisions[0].Actions.Length);
    }

    [Fact]
    public void SearchHasALegalFallbackAtAnExpiredDeadline()
    {
        var game = new TicTacToe();
        game.Step(new Dictionary<int, int> { [0] = 0 });
        var decision = game.Frame.Decisions[0];
        var action = BotActions.Choose(new MctsBot(new()), game, decision, new RandomSource(1), Deadline.After(TimeSpan.Zero));
        Assert.Contains(decision.Actions, a => a.Id == action);
    }

    [Fact]
    public void RandomStateCanBeRestoredExactly()
    {
        var source = new RandomSource(97);
        for (var i = 0; i < 17; i++) source.NextInt(9);
        var resumed = new RandomSource(source.State);
        for (var i = 0; i < 100; i++) Assert.Equal(source.NextUInt64(), resumed.NextUInt64());
    }

    [Fact]
    public void ArtifactsRoundTripAndRejectSchemaMismatch()
    {
        using var directory = new TemporaryDirectory();
        var game = new TicTacToe();
        var artifact = new BotArtifact(1, game.Spec, NetworkWeights.Create(game.Spec, new RandomSource(7)), "policy",
            new(), DateTimeOffset.UtcNow, 0, "test");
        var path = Path.Combine(directory.Path, "bot.json");
        JsonFiles.WriteAtomic(path, artifact);
        var loaded = BotArtifact.Load(path, game.Spec);
        var decision = game.Frame.Decisions[0];
        Assert.Equal(new CpuNetwork(game.Spec, artifact.Weights).Evaluate(decision).Probabilities,
            new CpuNetwork(game.Spec, loaded.Weights).Evaluate(decision).Probabilities);
        Assert.Throws<InvalidDataException>(() => BotArtifact.Load(path, game.Spec with { FeatureSchema = "different-v2" }));
        Assert.Throws<InvalidDataException>(() => (loaded with { FormatVersion = 99 }).Validate(game.Spec));
    }

    [Fact]
    public void ExactOracleNeverLosesToRandomAndEvaluationBalancesSeats()
    {
        var result = Evaluation.Run(new TicTacToeDefinition(), new TicTacToeOracle(), [new RandomBot()],
            32, 29832, Deadline.After(TimeSpan.FromSeconds(10)), moveMilliseconds: 1000);
        Assert.True(result.Complete);
        Assert.Equal(64, result.Games);
        Assert.InRange(result.MeanScore, 0.75, 1);
        Assert.InRange(result.Lower95, 0, result.Upper95);
    }

    [Fact]
    public void IncompleteEvaluationCannotBeMistakenForADraw()
    {
        var result = Evaluation.Run(new TicTacToeDefinition(), new RandomBot(), [new RandomBot()],
            8, 73, Deadline.After(TimeSpan.Zero));
        Assert.False(result.Complete);
        Assert.Equal(0, result.Games);
    }

    [Fact]
    public void OracleVariesItsOptimalOpeningAcrossSeeds()
    {
        var game = new TicTacToe();
        var oracle = new TicTacToeOracle();
        var openings = Enumerable.Range(0, 16).Select(seed => oracle.ChooseAction(game.Frame.Decisions[0],
            new BotContext(new RandomSource((ulong)seed), Deadline.After(TimeSpan.FromSeconds(2))))).ToHashSet();
        Assert.True(openings.Count > 1);
    }
}

public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stratjam-test-" + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
}
