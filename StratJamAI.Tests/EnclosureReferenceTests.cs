using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StratJamAI.Core.Games;
using Xunit;

namespace StratJamAI.Tests;

public sealed class EnclosureReferenceTests
{
    [Fact]
    public void FullGamesMatchOfficialPracticeRulesAndLegalActionSets()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EnclosureReference.json")));
        foreach (var trace in document.RootElement.GetProperty("traces").EnumerateArray())
        {
            var game = new Enclosure();
            foreach (var expected in trace.GetProperty("frames").EnumerateArray())
            {
                Assert.Equal(expected.GetProperty("moveNumber").GetInt32(), game.MoveNumber);
                Assert.Equal(expected.GetProperty("turn").GetInt32(), game.Turn);
                Assert.Equal(expected.GetProperty("actionsRemaining").GetInt32(), game.ActionsRemaining);
                for (var player = 0; player < 2; player++)
                {
                    Assert.InRange(Math.Abs(expected.GetProperty("scores")[player].GetDouble() - game.Scores[player]), 0, 2e-7);
                    Assert.InRange(Math.Abs(expected.GetProperty("areas")[player].GetDouble() - game.Areas[player]), 0, 2e-8);
                    Assert.Equal(expected.GetProperty("nodes")[player].EnumerateArray().Select(n => n.GetInt32()),
                        game.Nodes(player).Select(p => 19 * p.X + p.Y).Order());
                    Assert.Equal(expected.GetProperty("segments")[player].EnumerateArray().Select(s => (s.GetProperty("id").GetInt32(), s.GetProperty("protected").GetBoolean())),
                        game.Segments(player).OrderBy(s => s.ActionId).Select(s => (s.ActionId, s.Invincible)));
                }
                var actions = game.GenerateLegalActions();
                Assert.Equal(expected.GetProperty("legalCount").GetInt32(), actions.Length);
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', actions))));
                Assert.Equal(expected.GetProperty("legalHash").GetString(), hash);
                if (expected.GetProperty("action").ValueKind != JsonValueKind.Null) game.Play(expected.GetProperty("action").GetInt32());
            }
            Assert.True(game.Finished);
        }
    }

    [Fact]
    public void HistoryRoundTripsAndReportsInvalidMoveNumber()
    {
        var first = Enclosure.GetActionId(3, 9, 5, 10);
        var second = Enclosure.GetActionId(15, 9, 13, 10);
        var history = EnclosureHistory.FromActions([first, second]);
        var game = EnclosureHistory.Parse(history.ToJson()).Replay();
        Assert.Equal(2, game.MoveNumber);
        Assert.Equal(1, game.Turn);
        Assert.Equal(1, game.ActionsRemaining);
        var invalid = EnclosureHistory.FromActions([first, first]);
        Assert.Contains("move 2", Assert.Throws<InvalidDataException>(() => invalid.Replay()).Message);
        Assert.Throws<InvalidDataException>(() => EnclosureHistory.Parse("{\"formatVersion\":2}"));
        Assert.Throws<InvalidDataException>(() => EnclosureHistory.FromActions([Enclosure.PassActionId]).Replay());
    }
}
