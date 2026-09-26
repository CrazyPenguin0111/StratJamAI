using System.Reflection;
using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using Xunit;

namespace StratJamAI.Tests;

public sealed class EnclosureTests
{
    private static int Move(int x1, int y1, int x2, int y2) => Enclosure.GetActionId(x1, y1, x2, y2);

    [Fact]
    public void OpeningAndTwoMoveTurnsMatchThePracticeBoard()
    {
        var game = new Enclosure();
        Assert.Equal(new[] { new EnclosurePoint(0, 9), new EnclosurePoint(3, 9) }, game.Nodes(0));
        Assert.Equal(new[] { new EnclosurePoint(15, 9), new EnclosurePoint(18, 9) }, game.Nodes(1));
        Assert.Equal(1, game.ActionsRemaining);
        var blue = Move(3, 9, 3, 12);
        game.Play(blue);
        Assert.Equal(1, game.Turn); Assert.Equal(2, game.ActionsRemaining);
        Assert.True(game.Segments(0).Single(edge => edge.ActionId == blue).Invincible);
        var red1 = Move(15, 9, 15, 12); var red2 = Move(15, 12, 18, 12);
        game.Play(red1);
        Assert.Equal(1, game.Turn); Assert.Equal(1, game.ActionsRemaining);
        Assert.True(game.Segments(0).Single(edge => edge.ActionId == blue).Invincible);
        game.Play(red2);
        Assert.Equal(0, game.Turn); Assert.Equal(2, game.ActionsRemaining);
        Assert.False(game.Segments(0).Single(edge => edge.ActionId == blue).Invincible);
        Assert.All(game.Segments(1).Where(edge => edge.ActionId == red1 || edge.ActionId == red2), edge => Assert.True(edge.Invincible));
        game.Frame.Validate(game.Spec);
        Assert.DoesNotContain(Enclosure.PassActionId, game.GenerateLegalActions());
    }

    [Fact]
    public void CapturesRemoveOnlyNewlyIsolatedEnemyNodes()
    {
        var game = Position([Move(5, 5, 4, 5)], [Move(4, 6, 6, 6), Move(6, 6, 7, 7)]);
        var capture = Move(5, 5, 5, 7);
        Assert.True(game.IsLegal(capture)); Assert.True(game.IsCapture(capture));
        game.Play(capture);
        Assert.False(game.HasNode(1, new(4, 6)));
        Assert.True(game.HasNode(1, new(6, 6)));
        Assert.Single(game.Segments(1));
        Assert.True(game.HasNode(0, new(5, 7)));
    }

    [Fact]
    public void EnemyEndpointTouchesCountAsCutsAndProtectedEdgesCannotBeCut()
    {
        var touch = Move(5, 5, 5, 6);
        var game = Position([Move(5, 5, 4, 5)], [Move(5, 6, 7, 6)]);
        Assert.True(game.IsCapture(touch)); Assert.True(game.IsLegal(touch));
        var flags = Field<byte[]>(game, "flags"); flags[1] = 1;
        Assert.False(game.IsLegal(touch));
        Assert.DoesNotContain(touch, game.GenerateLegalActions());
        flags[1] = 0;
        var multiple = Position([Move(5, 5, 4, 5)], [Move(4, 6, 6, 6), Move(4, 7, 6, 7)]);
        Assert.False(multiple.IsLegal(Move(5, 5, 5, 8)));
    }

    [Fact]
    public void OwnCrossingsAreAllowedButOverlapsAndInteriorNodesAreRejected()
    {
        var game = Position([Move(5, 5, 4, 5), Move(4, 6, 6, 6)], []);
        Assert.True(game.IsLegal(Move(5, 5, 5, 7)));
        Assert.False(game.IsLegal(Move(5, 5, 5, 6)));
        Assert.False(game.IsLegal(Move(5, 5, 3, 5)));
        var through = Position([Move(5, 5, 4, 5), Move(5, 6, 6, 6)], []);
        Assert.False(through.IsLegal(Move(5, 5, 5, 7)));
        var crossing = Move(5, 5, 5, 7);
        Assert.Contains(crossing, game.GenerateLegalActions());
        game.Play(crossing);
        Assert.Equal(0, game.Areas[0]);
    }

    [Fact]
    public void FractionalCrossingsAndNestedDisconnectedLoopsHaveExactArea()
    {
        var bowTie = Position([Move(0, 0, 3, 3), Move(3, 3, 0, 3), Move(0, 3, 3, 0), Move(3, 0, 0, 0)], []);
        Assert.Equal(4.5, bowTie.Areas[0], 8);
        Assert.Equal(2, bowTie.Territories(0).Length);
        Assert.Contains(bowTie.Territories(0).SelectMany(polygon => polygon), p => p.X == 1.5 && p.Y == 1.5);
        var nested = Position([
            Move(0, 0, 3, 0), Move(3, 0, 6, 0), Move(6, 0, 6, 3), Move(6, 3, 6, 6),
            Move(6, 6, 3, 6), Move(3, 6, 0, 6), Move(0, 6, 0, 3), Move(0, 3, 0, 0),
            Move(1, 1, 3, 1), Move(3, 1, 1, 3), Move(1, 3, 1, 1)], []);
        Assert.Equal(36, nested.Areas[0], 8);
        Assert.Single(nested.Territories(0));
    }

    [Fact]
    public void BothPlayersScoreOnlyAtTurnEndAndFinalReturnsAreNormalized()
    {
        var game = Position([Move(0, 0, 3, 0), Move(3, 0, 0, 3), Move(0, 3, 0, 0)],
            [Move(10, 10, 12, 10), Move(12, 10, 10, 12), Move(10, 12, 10, 10)], move: 118, remaining: 2);
        game.Play(Move(0, 3, 0, 4));
        Assert.Equal(new double[] { 0, 0 }, game.Scores);
        Assert.False(game.Finished);
        game.Play(Move(0, 4, 0, 5));
        Assert.Equal(new[] { 4.5, 2.0 }, game.Scores);
        Assert.True(game.Finished); Assert.Equal(0, game.ActionsRemaining);
        Assert.Equal(new float[] { 1, -1 }, game.Frame.Returns);
        Assert.Empty(game.GenerateLegalActions());
        Assert.Throws<ArgumentException>(() => game.Play(Enclosure.PassActionId));
        game.Frame.Validate(game.Spec);
    }

    [Fact]
    public void ForcedPassConsumesRemainingPlacementsAndExpiresProtection()
    {
        var game = Position([], [Move(10, 10, 12, 10)], move: 119, remaining: 1);
        Field<byte[]>(game, "flags")[0] = 1;
        Assert.Equal(new[] { Enclosure.PassActionId }, game.GenerateLegalActions());
        game.Play(Enclosure.PassActionId);
        Assert.True(game.Finished); Assert.Equal(120, game.MoveNumber);
        Assert.False(game.Segments(1)[0].Invincible);
        Assert.Equal(new float[] { 0, 0 }, game.Frame.Returns);
        var initial = new Enclosure();
        Assert.Throws<ArgumentException>(() => initial.Play(Enclosure.PassActionId));
    }

    [Fact]
    public void CopiesAreIndependentAndInvalidActionsAreAtomic()
    {
        var game = new Enclosure(); var originalKey = game.GetSearchKey();
        var copy = game.Copy();
        Assert.Equal(originalKey, copy.GetSearchKey());
        copy.Play(Move(3, 9, 6, 12));
        Assert.Equal(originalKey, game.GetSearchKey());
        Assert.NotEqual(originalKey, copy.GetSearchKey());
        Assert.Equal(2, game.Nodes(0).Length);
        Assert.Throws<ArgumentException>(() => game.Play(Move(0, 0, 3, 3)));
        Assert.Equal(originalKey, game.GetSearchKey());
        copy.CopyFrom(game);
        Assert.Equal(originalKey, copy.GetSearchKey());
        Assert.Equal(game.Observe(0), copy.Observe(0));
    }

    [Fact]
    public void EveryGeneratedActionIsLegalThroughACompleteDeterministicGame()
    {
        var game = new Enclosure(); var random = new RandomSource(187);
        while (!game.Finished)
        {
            var actions = game.GenerateLegalActions();
            Assert.All(actions, action => Assert.True(game.IsLegal(action), $"Illegal generated action {action}."));
            var action = actions[random.NextInt(actions.Length)];
            game.Play(action);
            game.Frame.Validate(game.Spec);
        }
        Assert.Equal(120, game.MoveNumber);
    }

    [Fact]
    public void TacticalTeacherReturnsLegalActionsAtExpiredAndNormalDeadlines()
    {
        var game = new Enclosure(); var decision = game.Frame.Decisions[0];
        var bot = new EnclosureTacticalBot();
        var expired = bot.ChooseAction(decision, new(new(8), Deadline.After(TimeSpan.Zero), game));
        var normal = bot.ChooseAction(decision, new(new(8), Deadline.After(TimeSpan.FromMilliseconds(100)), game));
        Assert.True(game.IsLegal(expired)); Assert.True(game.IsLegal(normal)); Assert.Equal(0, game.MoveNumber);
    }

    [Fact]
    public void SharedSearchAreaCachePreservesExactScoresAcrossIndependentCopies()
    {
        var game = new Enclosure(); var random = new RandomSource(691);
        for (var i = 0; i < 60; i++)
        {
            var actions = game.GenerateLegalActions(); game.Play(actions[random.NextInt(actions.Length)]);
        }
        var cached = game.Copy();
        typeof(Enclosure).GetMethod("EnableSearchAreaCache", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cached, null);
        var cachedChild = cached.Copy(); var plainChild = game.Copy();
        var candidates = game.GenerateLegalActions().Take(40).ToArray();
        // Revisit identical geometries with separate children, including cache hits after CopyFrom.
        for (var repeat = 0; repeat < 2; repeat++)
        foreach (var action in candidates)
        {
            cachedChild.CopyFrom(cached); plainChild.CopyFrom(game);
            cachedChild.Play(action); plainChild.Play(action);
            Assert.Equal(plainChild.Areas, cachedChild.Areas);
            Assert.Equal(plainChild.Scores, cachedChild.Scores);
            Assert.Equal(plainChild.GetSearchKey(), cachedChild.GetSearchKey());
        }
        Assert.Equal(game.GetSearchKey(), cached.GetSearchKey());
    }

    // Small valid geometry fixtures isolate rules without requiring unrelated opening moves.
    private static Enclosure Position(int[] blue, int[] red, int move = 0, int remaining = 1)
    {
        var game = new Enclosure(); var edges = Field<int[]>(game, "edges");
        var owners = Field<byte[]>(game, "owners"); var nodes = Field<bool[]>(game, "nodes");
        Array.Clear(nodes);
        var count = 0;
        foreach (var (actions, owner) in new[] { (blue, 0), (red, 1) })
        foreach (var action in actions)
        {
            edges[count] = action; owners[count++] = (byte)owner;
            var segment = Enclosure.DecodeAction(action);
            nodes[owner * 361 + segment.From.X * 19 + segment.From.Y] = true;
            nodes[owner * 361 + segment.To.X * 19 + segment.To.Y] = true;
        }
        typeof(Enclosure).GetField("edgeCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, count);
        typeof(Enclosure).GetProperty(nameof(Enclosure.MoveNumber))!.SetValue(game, move);
        typeof(Enclosure).GetProperty(nameof(Enclosure.ActionsRemaining))!.SetValue(game, remaining);
        Array.Fill(Field<bool[]>(game, "areaDirty"), true);
        return game;
    }

    private static T Field<T>(Enclosure game, string name) =>
        (T)typeof(Enclosure).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
}
