using System.Reflection;
using StratJamAI.Core.Games;
using Xunit;

namespace StratJamAI.Tests;

public sealed class EnclosureDisruptionHistoryTests
{
    private delegate int GenerateCapturesMethod(Enclosure game, Span<int> actions, Span<byte> classifications);
    private static readonly GenerateCapturesMethod GenerateCaptures = typeof(Enclosure)
        .GetMethod("GenerateLegalCaptures", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<GenerateCapturesMethod>();

    [Theory]
    [InlineData("EnclosureDisruption1.json", 4271.942857146, 3174.9125, 107.1, 2.7)]
    [InlineData("EnclosureDisruption2.json", 1229.067765569, 622.085256412, 75.5, 1)]
    public void CompleteHumanHistoriesReplayWithExpectedScoresAndAreas(string fixture,
        double blueScore, double redScore, double blueArea, double redArea)
    {
        var history = Read(fixture);
        Assert.Equal(120, history.Moves.Length);
        var game = history.Replay();
        Assert.True(game.Finished);
        Assert.Equal(120, game.MoveNumber);
        Assert.Equal(blueScore, game.Scores[0], 7);
        Assert.Equal(redScore, game.Scores[1], 7);
        Assert.Equal(blueArea, game.Areas[0], 7);
        Assert.Equal(redArea, game.Areas[1], 7);
    }

    [Theory]
    [InlineData("EnclosureDisruption1.json")]
    [InlineData("EnclosureDisruption2.json")]
    public void CaptureGeneratorMatchesAllLegalCapturesThroughoutHumanHistories(string fixture)
    {
        var game = new Enclosure();
        var actions = new int[Enclosure.SegmentCount];
        var flags = new byte[actions.Length];
        foreach (var action in Read(fixture).ActionIds())
        {
            var expected = game.GenerateLegalActions().Where(game.IsCapture).ToArray();
            var count = GenerateCaptures(game, actions, flags);
            Assert.Equal(expected, actions.AsSpan(0, count).ToArray());
            for (var i = 0; i < count; i++)
            {
                var edge = Enclosure.DecodeAction(actions[i]);
                var expectedFlags = 1 | (game.IsClosure(actions[i]) ? 2 : 0) |
                    (!game.HasNode(game.Turn, edge.From) || !game.HasNode(game.Turn, edge.To) ? 4 : 0);
                Assert.Equal((byte)expectedFlags, flags[i]);
            }
            game.Play(action);
        }
        Assert.Equal(0, GenerateCaptures(game, actions, flags));
    }

    [Fact]
    public void SecondHumanGameHasASmallerEarlyClosureThatSurvivesSetupAndCapture()
    {
        var actions = Read("EnclosureDisruption2.json").ActionIds();
        var game = EnclosureHistory.FromActions(actions.Take(10)).Replay();
        Assert.Equal(1, game.Turn);
        Assert.Equal(1, game.ActionsRemaining);
        var historical = game.Copy();
        historical.Play(actions[10]);
        Assert.Equal(0, historical.Areas[1], 7);

        var vulnerable = game.Copy();
        vulnerable.Play(Enclosure.GetActionId(9, 12, 12, 9)); // J13-M10 closes 18.
        Assert.Equal(18, vulnerable.Areas[1], 7);
        Assert.DoesNotContain(vulnerable.GenerateLegalActions(), vulnerable.IsCapture);
        vulnerable.Play(Enclosure.GetActionId(3, 9, 4, 6)); // D10-E7: quiet access move.
        vulnerable.Play(Enclosure.GetActionId(4, 6, 7, 8)); // E7-H9: newly enabled cut.
        Assert.Equal(0, vulnerable.Areas[1], 7);

        game.Play(Enclosure.GetActionId(7, 12, 9, 12)); // H13-J13 closes a smaller protected pocket.
        Assert.Equal(3, game.Areas[1], 7);
        foreach (var first in game.GenerateLegalActions())
        {
            var reply = game.Copy();
            reply.Play(first);
            Assert.Equal(3, reply.Areas[1], 7);
            // A quiet second move cannot remove Red edges or change its area.
            foreach (var second in reply.GenerateLegalActions().Where(reply.IsCapture))
            {
                var continuation = reply.Copy();
                continuation.Play(second);
                Assert.Equal(3, continuation.Areas[1], 7);
            }
        }
    }

    [Fact]
    public void SecondHumanGameUsesFirstCaptureEndpointToOpenTerritoryOnItsSecondPlacement()
    {
        var actions = Read("EnclosureDisruption2.json").ActionIds();
        var game = EnclosureHistory.FromActions(actions.Take(27)).Replay();
        Assert.Equal(0, game.Turn);
        Assert.Equal(2, game.ActionsRemaining);
        Assert.Equal(18, game.Areas[1], 7);

        // No currently legal single capture opens Red's enclosure. The first move
        // creates K6, making the second capture possible within the same Blue turn.
        var captures = game.GenerateLegalActions().Where(game.IsCapture).ToArray();
        Assert.NotEmpty(captures);
        foreach (var capture in captures)
        {
            var reply = game.Copy();
            reply.Play(capture);
            Assert.Equal(18, reply.Areas[1], 7);
        }

        var first = Enclosure.GetActionId(8, 8, 10, 5); // I9-K6
        var second = Enclosure.GetActionId(7, 7, 10, 5); // H8-K6
        Assert.Equal(first, actions[27]);
        Assert.Equal(second, actions[28]);
        Assert.False(game.IsLegal(second));
        Assert.False(game.HasNode(0, new EnclosurePoint(10, 5)));
        Assert.True(game.IsCapture(first));

        game.Play(first);
        Assert.Equal(0, game.Turn);
        Assert.Equal(1, game.ActionsRemaining);
        Assert.True(game.HasNode(0, new EnclosurePoint(10, 5)));
        Assert.Equal(18, game.Areas[1], 7);
        Assert.True(game.IsLegal(second));
        Assert.True(game.IsCapture(second));

        game.Play(second);
        Assert.Equal(1, game.Turn);
        Assert.Equal(0, game.Areas[1], 7);
    }

    private static EnclosureHistory Read(string fixture) =>
        EnclosureHistory.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)));
}
