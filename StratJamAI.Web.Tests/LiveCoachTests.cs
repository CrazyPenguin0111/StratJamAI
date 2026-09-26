using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Web;
using Xunit;

namespace StratJamAI.Web.Tests;

public sealed class LiveCoachTests
{
    private static EnclosureSearchResult FirstLegal(Enclosure position, TimeSpan budget, CancellationToken cancellation)
    {
        var action = position.GenerateLegalActions()[0];
        return new(action, 1, 5, 1, 0, [action]);
    }

    [Fact]
    public async Task AnalysisCoachesBothColorsAfterEveryPlacementWithoutPlayingAMove()
    {
        using var session = new WebGameSession(search: FirstLegal);
        var state = session.NewGame(0, 50, analysisMode: true, liveCoach: true);
        Assert.True(state.Thinking);
        Assert.True(state.HintThinking);
        for (var i = 0; i < 4; i++)
        {
            await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            state = session.State;
            Assert.Equal(i, state.History.Length);
            Assert.Equal(state.Revision, state.HintRevision);
            Assert.Equal(state.Revision, state.LastSearch!.PositionRevision);
            Assert.Contains(state.LegalActions, action => action.Id == state.Hint!.Id);
            Assert.Throws<InvalidOperationException>(() => session.StartSearch(state.Revision));
            state = session.Move(state.Revision, state.Hint!.Id);
            Assert.Null(state.Hint);
            Assert.Null(state.HintRevision);
            Assert.Null(state.LastSearch);
        }
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 0, 1, 1, 0 }, session.State.History.Select(move => move.Player));
        var undone = session.Undo(session.State.Revision);
        Assert.Equal(3, undone.History.Length);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(undone.Revision, session.State.HintRevision);
    }

    [Fact]
    public async Task PlayingModeStartsCoachWhenTheBotFinishesItsTwoPlacements()
    {
        using var session = new WebGameSession(search: FirstLegal);
        session.NewGame(0, 50, liveCoach: true);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        var state = session.State;
        state = session.Move(state.Revision, state.Hint!.Id);
        Assert.False(state.Thinking);
        Assert.Equal(1, state.Turn);
        session.StartSearch(state.Revision);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        // The bot result queues a hint at the new revision. Await that job as well.
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        state = session.State;
        Assert.Equal(0, state.Turn);
        Assert.Equal(3, state.History.Length);
        Assert.Equal(state.Revision, state.HintRevision);
        Assert.Equal(state.Revision, state.LastSearch!.PositionRevision);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("undo")]
    [InlineData("import")]
    [InlineData("sync")]
    [InlineData("settings")]
    public async Task ChangedPositionDiscardsOldCoachResultEvenIfSearchIgnoresCancellation(string mutation)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var active = 0;
        var maximumActive = 0;
        using var session = new WebGameSession(search: (position, budget, cancellation) =>
        {
            var running = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, running);
            try
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                }
                return FirstLegal(position, budget, cancellation);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var state = session.NewGame(0, 50, analysisMode: true);
        state = session.Move(state.Revision, state.LegalActions[0].Id);
        state = session.Settings(50, liveCoach: true);
        var oldCompletion = session.SearchCompletion;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            state = mutation switch
            {
                "move" => session.Move(state.Revision, state.LegalActions[^1].Id),
                "undo" => session.Undo(state.Revision),
                "import" => session.Import(EnclosureHistory.FromActions([]).ToJson()),
                "sync" => session.Synchronize(state.Revision, []),
                _ => session.Settings(100)
            };
            Assert.Null(state.Hint);
            Assert.Null(state.HintRevision);
            Assert.Null(state.LastSearch);
            Assert.True(state.Thinking);
            var replacement = session.SearchCompletion;
            release.Set();
            await Task.WhenAll(oldCompletion, replacement).WaitAsync(TimeSpan.FromSeconds(5));
            var result = session.State;
            Assert.Equal(1, maximumActive);
            Assert.Equal(2, calls);
            Assert.Equal(state.Revision, result.Revision);
            Assert.Equal(state.Revision, result.HintRevision);
            Assert.Equal(state.Revision, result.LastSearch!.PositionRevision);
            Assert.Equal(state.History, result.History);
            Assert.Contains(result.LegalActions, action => action.Id == result.Hint!.Id);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task DisablingLiveCoachCancelsQueuedAnalysisAndClearsSuggestion()
    {
        using var slots = new SemaphoreSlim(0, 1);
        var calls = 0;
        using var session = new WebGameSession(search: (position, budget, cancellation) =>
        {
            Interlocked.Increment(ref calls);
            return FirstLegal(position, budget, cancellation);
        }, sharedSearchSlots: slots);
        var state = session.NewGame(0, 50, analysisMode: true, liveCoach: true);
        var pending = session.SearchCompletion;
        state = session.Settings(50, liveCoach: false);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, calls);
        Assert.Equal(0, slots.CurrentCount);
        Assert.False(state.Thinking);
        Assert.False(state.LiveCoach);
        Assert.True(state.AnalysisMode);
        Assert.Null(state.Hint);
    }

    [Fact]
    public async Task FullHistorySyncRebuildsScoresProtectionAndTurnAndIdenticalUpdatesAreIdempotent()
    {
        var game = new Enclosure();
        var actions = new List<int>();
        var random = new Random(72891);
        for (var i = 0; i < 60; i++)
        {
            var legal = game.GenerateLegalActions();
            var action = legal[random.Next(legal.Length)];
            actions.Add(action);
            game.Play(action);
        }
        var calls = 0;
        using var session = new WebGameSession(search: (position, budget, cancellation) =>
        {
            Interlocked.Increment(ref calls);
            return FirstLegal(position, budget, cancellation);
        });
        var state = session.Synchronize(session.State.Revision, null, EnclosureHistory.FromActions(actions).ToJson());
        Assert.True(state.AnalysisMode);
        Assert.True(state.LiveCoach);
        Assert.Equal(game.MoveNumber, state.MoveNumber);
        Assert.Equal(game.Turn, state.Turn);
        Assert.Equal(game.ActionsRemaining, state.ActionsRemaining);
        Assert.Equal(game.Scores, state.Scores);
        Assert.Equal(game.Areas, state.Areas);
        Assert.Equal(game.GenerateLegalActions(), state.LegalActions.Select(action => action.Id));
        for (var player = 0; player < 2; player++)
        {
            Assert.Equal(game.Nodes(player), state.Nodes[player]);
            Assert.Equal(game.Segments(player), state.Segments[player]);
        }
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        var analyzed = session.State;
        var same = session.Synchronize(analyzed.Revision, actions.ToArray());
        Assert.Equal(analyzed.Revision, same.Revision);
        Assert.Equal(analyzed.Hint, same.Hint);
        Assert.Equal(analyzed.HintRevision, same.HintRevision);
        Assert.False(same.Thinking);
        Assert.Equal(1, calls);
        Assert.Equal(actions, EnclosureHistory.Parse(session.Export()).ActionIds());
    }

    [Fact]
    public async Task IllegalOrStaleSyncDoesNotReplacePositionOrPublishedHint()
    {
        using var session = new WebGameSession(search: FirstLegal);
        var state = session.NewGame(0, 50, analysisMode: true, liveCoach: true);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        state = session.State;
        var legal = state.LegalActions[0].Id;
        Assert.Throws<InvalidDataException>(() => session.Synchronize(state.Revision, [legal, legal]));
        Assert.Throws<InvalidOperationException>(() => session.Synchronize(state.Revision - 1, [legal]));
        Assert.Throws<ArgumentException>(() => session.Synchronize(state.Revision, new int[121]));
        Assert.Throws<ArgumentException>(() => session.Synchronize(state.Revision, null));
        Assert.Throws<ArgumentException>(() => session.Synchronize(state.Revision, [], "{}"));
        Assert.Equal(state.Revision, session.State.Revision);
        Assert.Equal(state.Hint, session.State.Hint);
        Assert.Equal(state.LastSearch, session.State.LastSearch);
        Assert.Empty(session.State.History);
    }

    [Fact]
    public void CompletedExternalGameDoesNotStartSearch()
    {
        var game = new Enclosure();
        var actions = new List<int>();
        var random = new Random(72891);
        while (!game.Finished)
        {
            var legal = game.GenerateLegalActions();
            var action = legal[random.Next(legal.Length)];
            actions.Add(action);
            game.Play(action);
        }
        using var session = new WebGameSession(search: (_, _, _) => throw new Exception("Must not run"));
        var state = session.Synchronize(session.State.Revision, actions.ToArray());
        Assert.True(state.Finished);
        Assert.False(state.Thinking);
        Assert.Null(state.Hint);
        Assert.Null(state.Error);
    }
}
