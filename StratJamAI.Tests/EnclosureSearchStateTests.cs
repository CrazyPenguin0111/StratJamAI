using System.Reflection;
using System.Text.Json;
using StratJamAI.Core.Games;
using Xunit;

namespace StratJamAI.Tests;

public sealed class EnclosureSearchStateTests
{
    private delegate int GenerateMethod(Enclosure game, Span<int> actions, Span<byte> classifications);
    private static readonly Action<Enclosure> Enable = typeof(Enclosure)
        .GetMethod("EnableSearchAreaCache", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<Enclosure>>();
    private static readonly GenerateMethod Generate = typeof(Enclosure)
        .GetMethod("GenerateLegalActions", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<GenerateMethod>();
    private static readonly Action<Enclosure, int, byte> Play = typeof(Enclosure)
        .GetMethod("PlayGenerated", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(int), typeof(byte)])!
        .CreateDelegate<Action<Enclosure, int, byte>>();

    [Fact]
    public void IncrementalSearchStateAndClassificationsMatchFullRecomputationThroughoutReferenceGames()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EnclosureReference.json")));
        var actions = new int[Enclosure.SegmentCount + 1]; var flags = new byte[actions.Length];
        var sawCapture = false;
        foreach (var trace in document.RootElement.GetProperty("traces").EnumerateArray())
        {
            var plain = new Enclosure(); var cached = new Enclosure(); var copied = new Enclosure();
            Enable(cached);
            foreach (var expected in trace.GetProperty("frames").EnumerateArray())
            {
                AssertStateEqual(plain, cached);
                copied.CopyFrom(cached); AssertStateEqual(plain, copied);
                var legal = plain.GenerateLegalActions();
                var count = Generate(cached, actions, flags);
                Assert.Equal(legal, actions.AsSpan(0, count).ToArray());
                if (plain.MoveNumber % 12 == 0)
                    for (var i = 0; i < count; i++) Assert.Equal(Classify(plain, actions[i]), flags[i]);
                if (expected.GetProperty("action").ValueKind == JsonValueKind.Null) continue;
                var action = expected.GetProperty("action").GetInt32();
                var index = Array.BinarySearch(actions, 0, count, action);
                Assert.True(index >= 0);
                Assert.Equal(Classify(plain, action), flags[index]);
                sawCapture |= (flags[index] & 1) != 0;
                plain.Play(action); Play(cached, action, flags[index]);
            }
            Assert.True(cached.Finished);
        }
        Assert.True(sawCapture, "Reference traces must exercise captured edges and newly isolated nodes.");
    }

    [Fact]
    public void CachedMetadataHandlesForcedPassProtectionExpiryAndReset()
    {
        var plain = new Enclosure();
        var edges = Field<int[]>(plain, "edges"); edges[0] = edges[1];
        Field<byte[]>(plain, "owners")[0] = 1;
        Field<byte[]>(plain, "flags")[0] = 1;
        Array.Clear(Field<bool[]>(plain, "nodes"), 0, 361);
        typeof(Enclosure).GetField("edgeCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plain, 1);
        typeof(Enclosure).GetProperty(nameof(Enclosure.MoveNumber))!.SetValue(plain, 118);
        typeof(Enclosure).GetProperty(nameof(Enclosure.ActionsRemaining))!.SetValue(plain, 2);
        var cached = plain.Copy(); Enable(cached);
        var actions = new int[Enclosure.SegmentCount + 1]; var flags = new byte[actions.Length];
        Assert.Equal(1, Generate(cached, actions, flags));
        Assert.Equal(Enclosure.PassActionId, actions[0]); Assert.Equal(0, flags[0]);
        plain.Play(actions[0]); Play(cached, actions[0], flags[0]);
        AssertStateEqual(plain, cached); Assert.True(cached.Finished);
        cached.Reset(0); AssertStateEqual(new Enclosure(), cached);
        Enable(cached); cached.CopyFrom(new Enclosure());
        // Copying a normal state disables cached metadata and keeps synthetic fixture edits visible.
        Field<double[]>(cached, "scores")[0] = 8;
        Assert.NotEqual(new Enclosure().GetSearchKey(), cached.GetSearchKey());
    }

    [Fact]
    public void ReusedMoveGenerationAndSearchMetadataQueriesAllocateNoArrays()
    {
        var game = new Enclosure(); Enable(game);
        var actions = new int[Enclosure.SegmentCount + 1]; var flags = new byte[actions.Length];
        for (var i = 0; i < 40; i++) Generate(game, actions, flags);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            Generate(game, actions, flags); game.GetSearchKey(); game.NodeCount(0); game.SegmentCountFor(1);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static byte Classify(Enclosure game, int action)
    {
        if (action == Enclosure.PassActionId) return 0;
        var edge = Enclosure.DecodeAction(action);
        return (byte)((game.IsCapture(action) ? 1 : 0) | (game.IsClosure(action) ? 2 : 0) |
            (!game.HasNode(game.Turn, edge.From) || !game.HasNode(game.Turn, edge.To) ? 4 : 0));
    }

    private static void AssertStateEqual(Enclosure expected, Enclosure actual)
    {
        Assert.Equal(expected.GetSearchKey(), actual.GetSearchKey());
        Assert.Equal(expected.MoveNumber, actual.MoveNumber); Assert.Equal(expected.Turn, actual.Turn);
        Assert.Equal(expected.ActionsRemaining, actual.ActionsRemaining);
        Assert.Equal(expected.Scores, actual.Scores); Assert.Equal(expected.Areas, actual.Areas);
        for (var player = 0; player < 2; player++)
        {
            Assert.Equal(expected.NodeCount(player), actual.NodeCount(player));
            Assert.Equal(expected.SegmentCountFor(player), actual.SegmentCountFor(player));
        }
    }

    private static T Field<T>(Enclosure game, string name) =>
        (T)typeof(Enclosure).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
}
