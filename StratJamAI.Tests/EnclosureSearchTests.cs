using System.Reflection;
using System.Text.Json;
using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using Xunit;

namespace StratJamAI.Tests;

public sealed class EnclosureSearchTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public void SearchMatchesExhaustiveMinimaxAcrossOrdinaryAndConsecutiveTurns(bool redTurn, int depth)
    {
        var game = new Enclosure();
        if (redTurn) game.Play(Enclosure.GetActionId(3, 9, 6, 12));
        var before = Snapshot(game);
        var expected = ExhaustiveRoot(game, depth);
        var result = new EnclosureAlphaBetaBot(new(30_000, depth) { CaptureQuiescencePlies = 0 }).Search(game, Deadline.Never);

        Assert.Equal(depth, result.CompletedDepth);
        Assert.Equal(expected.Action, result.Action);
        Assert.Equal(expected.Value, result.Value, 10);
        Assert.Equal(before, Snapshot(game));
        Assert.Equal(result.Action, result.PrincipalVariation[0]);
        Assert.Equal(depth, result.PrincipalVariation.Count);
        var continuation = game.Copy();
        foreach (var action in result.PrincipalVariation)
        {
            Assert.True(continuation.IsLegal(action));
            continuation.Play(action);
        }
        if (redTurn)
        {
            // Both searched placements are Red's: the pair can close the starting triangle.
            Assert.Equal(0, continuation.Turn);
        }
    }

    [Theory]
    [InlineData(6, 731)]
    [InlineData(7, 1183)]
    [InlineData(8, 9011)]
    public void OrderedScoutSearchPreservesExactValueAndLowestRootActionAcrossDifferentPositions(int moves, ulong seed)
    {
        var game = new Enclosure();
        var random = new RandomSource(seed);
        while (game.MoveNumber < moves)
        {
            var legal = game.GenerateLegalActions();
            game.Play(legal[random.NextInt(legal.Length)]);
        }
        var before = Snapshot(game);
        var expected = ExhaustiveRoot(game, 2);
        var result = new EnclosureAlphaBetaBot(new(30_000, 2) { CaptureQuiescencePlies = 0 }).Search(game, Deadline.Never);
        Assert.Equal(2, result.CompletedDepth);
        Assert.Equal(expected.Action, result.Action);
        Assert.Equal(expected.Value, result.Value, 10);
        Assert.Equal(before, Snapshot(game));
        var continuation = game.Copy();
        Assert.Equal(2, result.PrincipalVariation.Count);
        foreach (var action in result.PrincipalVariation)
        {
            Assert.True(continuation.IsLegal(action));
            continuation.Play(action);
        }
        Assert.Equal(result.Value, LeafValue(continuation, game.Turn), 10);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(60, 2)]
    [InlineData(110, 2)]
    public void StrategicSearchProducesAnExactLegalPrincipalVariationOnBenchmarkPositions(int moves, int depth)
    {
        var game = new Enclosure();
        var random = new RandomSource(72891);
        while (game.MoveNumber < moves)
        {
            var legal = game.GenerateLegalActions();
            game.Play(legal[random.NextInt(legal.Length)]);
        }
        var result = new EnclosureAlphaBetaBot(new(30_000, depth) { CaptureQuiescencePlies = 0 }).Search(game, Deadline.Never);
        Assert.Equal(depth, result.CompletedDepth);
        Assert.True(game.IsLegal(result.Action));
        var continuation = game.Copy();
        Assert.Equal(depth, result.PrincipalVariation.Count);
        foreach (var next in result.PrincipalVariation)
        {
            Assert.True(continuation.IsLegal(next));
            continuation.Play(next);
        }
        Assert.Equal(result.Value, LeafValue(continuation, game.Turn), 12);
    }

    [Fact]
    public void FinalPlacementPrefersATerminalWinOverExpansion()
    {
        var game = LastMoveWithOpenTriangle();
        var before = Snapshot(game);
        var result = new EnclosureAlphaBetaBot(new(30_000, 4)).Search(game, Deadline.Never);
        Assert.Equal(Enclosure.GetActionId(0, 9, 3, 12), result.Action);
        Assert.Equal(1, result.Value);
        Assert.Equal(1, result.CompletedDepth);
        Assert.Equal(before, Snapshot(game));
        var copy = game.Copy();
        copy.Play(result.Action);
        Assert.True(copy.Finished);
        Assert.True(copy.Scores[0] > copy.Scores[1]);
    }

    [Fact]
    public void TerminalDrawsIgnoreExpansionAndChooseTheLowestActionId()
    {
        var game = new Enclosure();
        SetProperty(game, nameof(Enclosure.MoveNumber), 119);
        var first = game.GenerateLegalActions().Min();
        var result = new EnclosureAlphaBetaBot(new(30_000, 1)).Search(game, Deadline.Never);
        Assert.Equal(first, result.Action);
        Assert.Equal(0, result.Value);
        Assert.Equal(1, result.CompletedDepth);
    }

    [Fact]
    public void ForcedPassIsSearchedWithoutInventingAnOrdinaryMove()
    {
        var game = new Enclosure();
        // A captured last line leaves a player with no nodes. Construct that compact state
        // directly so this test does not depend on a long tactical capture sequence.
        var edges = Field<int[]>(game, "edges");
        edges[0] = edges[1];
        Field<byte[]>(game, "owners")[0] = 1;
        Array.Clear(Field<bool[]>(game, "nodes"), 0, 361);
        SetField(game, "edgeCount", 1);
        SetProperty(game, nameof(Enclosure.ActionsRemaining), 2);
        SetProperty(game, nameof(Enclosure.MoveNumber), 3);
        var before = Snapshot(game);

        Assert.Equal(new[] { Enclosure.PassActionId }, game.GenerateLegalActions());
        var result = new EnclosureAlphaBetaBot(new(30_000, 2) { CaptureQuiescencePlies = 0 }).Search(game, Deadline.Never);
        Assert.Equal(Enclosure.PassActionId, result.Action);
        Assert.Equal(2, result.CompletedDepth);
        Assert.Equal(before, Snapshot(game));
        game.Play(result.Action);
        Assert.Equal(5, game.MoveNumber);
        Assert.Equal(1, game.Turn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiryAndCancellationReturnALegalFallbackWithoutSearching(bool cancelled)
    {
        var game = new Enclosure();
        var before = Snapshot(game);
        using var source = new CancellationTokenSource();
        if (cancelled) source.Cancel();
        var deadline = cancelled ? Deadline.Never : Deadline.After(TimeSpan.Zero);
        var result = new EnclosureAlphaBetaBot().Search(game, deadline, source.Token);
        Assert.True(game.IsLegal(result.Action));
        Assert.Equal(0, result.CompletedDepth);
        Assert.Equal(0, result.Nodes);
        Assert.Equal(before, Snapshot(game));
    }

    [Fact]
    public async Task ASharedBotKeepsConcurrentGamesIndependent()
    {
        var opening = new Enclosure();
        var final = LastMoveWithOpenTriangle();
        var bot = new EnclosureAlphaBetaBot(new(30_000, 1));
        var expectedOpening = bot.Search(opening, Deadline.Never);
        var expectedFinal = bot.Search(final, Deadline.Never);
        var results = await Task.WhenAll(Task.Run(() => bot.Search(opening, Deadline.Never)),
            Task.Run(() => bot.Search(final, Deadline.Never)));
        Assert.Equal(expectedOpening.Action, results[0].Action);
        Assert.Equal(expectedOpening.Value, results[0].Value);
        Assert.Equal(expectedFinal.Action, results[1].Action);
        Assert.Equal(expectedFinal.Value, results[1].Value);
    }

    [Fact]
    public void TranspositionKeysIncludeScoresProtectionAndTurnProgress()
    {
        var game = new Enclosure();
        var key = game.GetSearchKey();
        var scores = game.Copy();
        Field<double[]>(scores, "scores")[0] = 1;
        var protectedEdge = game.Copy();
        Field<byte[]>(protectedEdge, "flags")[0] = 1;
        var currentTurnEdge = game.Copy();
        Field<byte[]>(currentTurnEdge, "flags")[0] = 2;
        var turn = game.Copy();
        SetProperty(turn, nameof(Enclosure.Turn), 1);
        var progress = game.Copy();
        SetProperty(progress, nameof(Enclosure.MoveNumber), 2);
        var slots = game.Copy();
        SetProperty(slots, nameof(Enclosure.ActionsRemaining), 2);

        foreach (var different in new[] { scores, protectedEdge, currentTurnEdge, turn, progress, slots })
            Assert.NotEqual(key, different.GetSearchKey());
        Assert.Equal(key, game.Copy().GetSearchKey());
    }

    [Fact]
    public void EquivalentMoveOrdersHaveTheSameSearchKey()
    {
        var first = new Enclosure();
        first.Play(Enclosure.GetActionId(3, 9, 6, 12));
        var second = first.Copy();
        var a = Enclosure.GetActionId(15, 9, 12, 6);
        var b = Enclosure.GetActionId(18, 9, 18, 12);
        first.Play(a); first.Play(b);
        second.Play(b); second.Play(a);
        Assert.Equal(first.GetSearchKey(), second.GetSearchKey());
        var bot = new EnclosureAlphaBetaBot(new(30_000, 1));
        Assert.Equal(bot.Search(first, Deadline.Never).Action, bot.Search(second, Deadline.Never).Action);
    }

    [Fact]
    public void SearchRejectsFinishedGamesAndInvalidOptions()
    {
        var game = new Enclosure();
        SetProperty(game, nameof(Enclosure.MoveNumber), 120);
        Assert.Throws<ArgumentException>(() => new EnclosureAlphaBetaBot().Search(game, Deadline.Never));
        foreach (var invalid in new[] { new DepthSearchOptions(0), new(double.NaN), new(double.PositiveInfinity), new(1000, 0), new(1000, 121) })
            Assert.Throws<ArgumentException>(() => new EnclosureAlphaBetaBot(invalid));
    }

    private static (int Action, double Value) ExhaustiveRoot(Enclosure state, int depth)
    {
        var best = double.NegativeInfinity;
        var chosen = -1;
        foreach (var action in state.GenerateLegalActions().Order())
        {
            var child = state.Copy(); child.Play(action);
            var value = Exhaustive(child, depth - 1, state.Turn);
            if (value > best) { best = value; chosen = action; }
        }
        return (chosen, best);
    }

    private static double Exhaustive(Enclosure state, int depth, int root)
    {
        if (depth == 0 || state.Finished) return LeafValue(state, root);
        var values = state.GenerateLegalActions().Select(action =>
        {
            var copy = state.Copy(); copy.Play(action);
            return Exhaustive(copy, depth - 1, root);
        });
        return state.Turn == root ? values.Max() : values.Min();
    }

    private static double LeafValue(Enclosure state, int root) => EnclosureStrategyEvaluation.Evaluate(state, root);

    private static Enclosure LastMoveWithOpenTriangle()
    {
        var game = new Enclosure();
        game.Play(Enclosure.GetActionId(3, 9, 3, 12));
        SetProperty(game, nameof(Enclosure.Turn), 0);
        SetProperty(game, nameof(Enclosure.MoveNumber), 119);
        SetProperty(game, nameof(Enclosure.ActionsRemaining), 1);
        return game;
    }

    private static string Snapshot(Enclosure game) => JsonSerializer.Serialize(new
    {
        game.Turn, game.MoveNumber, game.ActionsRemaining, game.Finished,
        Scores = game.Scores.ToArray(), Areas = game.Areas.ToArray(),
        BlueNodes = game.Nodes(0), RedNodes = game.Nodes(1), BlueEdges = game.Segments(0), RedEdges = game.Segments(1)
    });
    private static T Field<T>(Enclosure game, string name) =>
        (T)typeof(Enclosure).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
    private static void SetField(Enclosure game, string name, object value) =>
        typeof(Enclosure).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, value);
    private static void SetProperty(Enclosure game, string name, object value) =>
        typeof(Enclosure).GetProperty(name)!.SetValue(game, value);
}
