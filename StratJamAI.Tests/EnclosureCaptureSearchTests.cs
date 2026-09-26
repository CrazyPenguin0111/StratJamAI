using System.Reflection;
using System.Text.Json;
using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using Xunit;

namespace StratJamAI.Tests;

public sealed class EnclosureCaptureSearchTests
{
    [Fact]
    public void ExtendedSearchMatchesExhaustiveCapturesAcrossTwoActionsByTheSamePlayer()
    {
        var game = Replay(2, 27);
        Assert.Equal(0, game.Turn);
        Assert.Equal(2, game.ActionsRemaining);
        var expected = ExhaustiveRoot(game, 1);
        var before = game.GetSearchKey();
        var result = new EnclosureAlphaBetaBot(new(30_000, 1) { CaptureQuiescencePlies = 1, CaptureQuiescenceWidth = Enclosure.SegmentCount })
            .Search(game, Deadline.Never);
        Assert.Equal(1, result.CompletedDepth);
        Assert.Equal(1, result.CaptureQuiescencePlies);
        Assert.Equal(expected.Action, result.Action);
        Assert.Equal(expected.Value, result.Value, 10);
        Assert.Equal(before, game.GetSearchKey());
        Assert.Equal(2, result.PrincipalVariation.Count);
        foreach (var action in result.PrincipalVariation)
        {
            Assert.Equal(0, game.Turn);
            Assert.True(game.IsLegal(action));
            game.Play(action);
        }
        Assert.Equal(result.Value, EnclosureStrategyEvaluation.Evaluate(game, 0), 10);
    }

    [Fact]
    public void DefaultBoundedExtensionFollowsALegalSecondCaptureAndImprovesItsStaticEstimate()
    {
        var game = Replay(2, 27);
        var baseline = new EnclosureAlphaBetaBot(new(30_000, 1) { CaptureQuiescencePlies = 0 })
            .Search(game, Deadline.Never);
        var result = new EnclosureAlphaBetaBot(new(30_000, 1)).Search(game, Deadline.Never);
        Assert.Equal(1, result.CompletedDepth);
        Assert.Equal(2, result.CaptureQuiescencePlies);
        Assert.True(result.Value > baseline.Value);
        Assert.Equal(2, result.PrincipalVariation.Count);
        game.Play(result.Action);
        Assert.True(game.IsCapture(result.PrincipalVariation[1]));
        Assert.True(game.IsLegal(result.PrincipalVariation[1]));
        game.Play(result.PrincipalVariation[1]);
        Assert.Equal(1, game.Turn);
        Assert.Equal(result.Value, EnclosureStrategyEvaluation.Evaluate(game, 0), 10);
    }

    [Fact]
    public void CaptureExtensionCanBeDisabledWithoutChangingTheOptionsConstructor()
    {
        var game = Replay(2, 27);
        var result = new EnclosureAlphaBetaBot(new(30_000, 1) { CaptureQuiescencePlies = 0 })
            .Search(game, Deadline.Never);
        Assert.Equal(1, result.CompletedDepth);
        Assert.Single(result.PrincipalVariation);
        game.Play(result.Action);
        Assert.Equal(18, game.Areas[1], 8);
        Assert.Throws<ArgumentException>(() => new EnclosureAlphaBetaBot(new() { CaptureQuiescencePlies = -1 }));
        Assert.Throws<ArgumentException>(() => new EnclosureAlphaBetaBot(new() { CaptureQuiescencePlies = 5 }));
    }

    [Theory]
    [InlineData(100, 1000, 250, 0)]
    [InlineData(1000, 100, 250, 0)]
    [InlineData(1000, 1000, 250, 2)]
    [InlineData(100, 1000, 0, 2)]
    public void TacticalExtensionUsesTheEffectiveBudgetAndCanBeForced(int configured, int caller, double minimum, int expected)
    {
        var game = new Enclosure();
        var bot = new EnclosureAlphaBetaBot(new(configured, 1) { MinimumTacticalMilliseconds = minimum });
        var result = bot.Search(game, TimeSpan.FromMilliseconds(caller));
        Assert.Equal(1, result.CompletedDepth);
        Assert.Equal(expected, result.CaptureQuiescencePlies);
        Assert.True(game.IsLegal(result.Action));
        Assert.Throws<ArgumentException>(() => new EnclosureAlphaBetaBot(new() { MinimumTacticalMilliseconds = -1 }));
        Assert.Throws<ArgumentException>(() => new EnclosureAlphaBetaBot(new() { MinimumTacticalMilliseconds = double.NaN }));
    }

    [Fact]
    public void RecordedLateRepairIsReplacedByAreaThatSurvivesEveryTwoCaptureReply()
    {
        var game = Replay(1, 102);
        var before = game.GetSearchKey();
        var result = new EnclosureAlphaBetaBot(new(30_000, 2)).Search(game, Deadline.Never);
        Assert.Equal(2, result.CompletedDepth);
        Assert.Equal(2, result.CaptureQuiescencePlies);
        Assert.Equal(before, game.GetSearchKey());
        game.Play(result.Action);
        Assert.True(MinimumAreaAfterCaptureReplies(game, 1) >= 10 - 1e-8);
        Assert.Equal(0, MinimumAreaAfterCaptureReplies(Replay(1, 103), 1), 8);
    }

    [Fact]
    public void BothPlacementsOfTheDefensiveTurnSurviveTheOpponentsCapturePair()
    {
        var game = Replay(1, 105);
        Assert.Equal(1, game.Turn);
        Assert.Equal(2, game.ActionsRemaining);
        foreach (var depth in new[] { 1, 2 })
        {
            var before = game.GetSearchKey();
            var result = new EnclosureAlphaBetaBot(new(30_000, depth)).Search(game, Deadline.Never);
            Assert.Equal(depth, result.CompletedDepth);
            Assert.Equal(2, result.CaptureQuiescencePlies);
            Assert.Equal(before, game.GetSearchKey());
            Assert.True(game.IsLegal(result.Action));
            game.Play(result.Action);
        }
        Assert.Equal(0, game.Turn);
        Assert.True(MinimumAreaAfterCaptureReplies(game, 1) >= 12.66666666);
        Assert.Equal(2.7, MinimumAreaAfterCaptureReplies(Replay(1, 107), 1), 8);
    }

    // This deliberately checks all legal capture endpoints, rather than the engine's beam
    // or only the opponent's recorded response. Quiet setup moves are outside this assertion.
    private static double MinimumAreaAfterCaptureReplies(Enclosure game, int defender)
    {
        var original = game.Copy();
        typeof(Enclosure).GetMethod("EnableSearchAreaCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(original, null);
        var first = original.Copy();
        var second = original.Copy();
        var smallest = original.Areas[defender];
        foreach (var a in original.GenerateLegalActions().Where(original.IsCapture))
        {
            first.CopyFrom(original);
            first.Play(a);
            smallest = Math.Min(smallest, first.Areas[defender]);
            if (smallest == 0) return 0;
            if (first.Turn != original.Turn || first.Finished) continue;
            foreach (var b in first.GenerateLegalActions().Where(first.IsCapture))
            {
                second.CopyFrom(first);
                second.Play(b);
                smallest = Math.Min(smallest, second.Areas[defender]);
                if (smallest == 0) return 0;
            }
        }
        return smallest;
    }

    [Fact]
    public void DeadlineDuringAnExtendedCaptureSearchKeepsThePositionAndReturnsALegalMove()
    {
        var game = Replay(1, 102);
        var before = game.GetSearchKey();
        var result = new EnclosureAlphaBetaBot().Search(game, TimeSpan.FromMilliseconds(20));
        Assert.True(game.IsLegal(result.Action));
        Assert.Equal(before, game.GetSearchKey());
        Assert.InRange(result.ElapsedMilliseconds, 0, 1000);
        foreach (var action in result.PrincipalVariation)
        {
            Assert.True(game.IsLegal(action));
            game.Play(action);
        }
    }

    private static (int Action, double Value) ExhaustiveRoot(Enclosure game, int captures)
    {
        var action = -1;
        var best = double.NegativeInfinity;
        var child = game.Copy();
        foreach (var candidate in game.GenerateLegalActions())
        {
            child.CopyFrom(game);
            child.Play(candidate);
            var value = CaptureReference(child, game.Turn, captures);
            if (value > best) { best = value; action = candidate; }
        }
        return (action, best);
    }

    private static double CaptureReference(Enclosure game, int root, int remaining)
    {
        var best = EnclosureStrategyEvaluation.Evaluate(game, root);
        if (remaining == 0 || game.Finished || game.Areas[1 - game.Turn] <= 0) return best;
        var child = game.Copy();
        foreach (var action in game.GenerateLegalActions().Where(game.IsCapture))
        {
            child.CopyFrom(game);
            child.Play(action);
            var value = CaptureReference(child, root, remaining - 1);
            best = game.Turn == root ? Math.Max(best, value) : Math.Min(best, value);
        }
        return best;
    }

    private static Enclosure Replay(int history, int placements)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", $"EnclosureDisruption{history}.json")));
        var game = new Enclosure();
        foreach (var move in document.RootElement.GetProperty("moves").EnumerateArray().Take(placements))
        {
            var from = move.GetProperty("from");
            var to = move.GetProperty("to");
            game.Play(Enclosure.GetActionId(from[0].GetInt32(), from[1].GetInt32(), to[0].GetInt32(), to[1].GetInt32()));
        }
        return game;
    }
}
