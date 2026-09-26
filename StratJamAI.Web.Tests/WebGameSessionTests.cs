using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Web;
using System.Reflection;
using Xunit;

namespace StratJamAI.Web.Tests;

public sealed class WebGameSessionTests
{
    private static EnclosureSearchResult FirstLegal(Enclosure position, TimeSpan budget, CancellationToken cancellation)
    {
        var action = position.GenerateLegalActions()[0];
        return new(action, 1, 5, 1, 0, [action]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task BothSeatsHaveCorrectHumanAndAiTurns(int player)
    {
        using var session = new WebGameSession(search: FirstLegal);
        var state = session.NewGame(player, 50);
        Assert.Equal(player, state.HumanPlayer);
        Assert.Equal(0, state.Turn);
        Assert.NotEmpty(state.LegalActions);
        if (player == 1)
        {
            Assert.Throws<InvalidOperationException>(() => session.Move(state.Revision, state.LegalActions[0].Id));
            session.StartSearch(state.Revision);
            await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            state = session.State;
            Assert.Equal(player, state.Turn);
            Assert.Single(state.History);
            Assert.Equal(0, state.History[0].Player);
        }
        state = session.Move(state.Revision, state.LegalActions[0].Id);
        Assert.Equal(player, state.History[^1].Player);
        Assert.True(state.CanUndo);
    }

    [Fact]
    public async Task UndoRemovesHumanPlacementAndBothFollowingAiPlacements()
    {
        using var session = new WebGameSession(search: FirstLegal);
        var initial = session.State;
        var state = session.Move(initial.Revision, initial.LegalActions[0].Id);
        Assert.Equal(1, state.Turn);
        session.StartSearch(state.Revision);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        state = session.State;
        Assert.Equal(3, state.History.Length);
        Assert.Equal(0, state.Turn);
        var undone = session.Undo(state.Revision);
        Assert.Empty(undone.History);
        Assert.Equal(initial.Turn, undone.Turn);
        Assert.Equal(initial.ActionsRemaining, undone.ActionsRemaining);
        Assert.Equal(initial.Scores, undone.Scores);
        Assert.Equal(initial.LegalActions.Select(a => a.Id), undone.LegalActions.Select(a => a.Id));
        Assert.False(undone.CanUndo);
    }

    [Fact]
    public async Task HintIsLegalAndDoesNotChangeHistoryOrRevision()
    {
        using var session = new WebGameSession(search: FirstLegal);
        var before = session.State;
        session.StartSearch(before.Revision, isHint: true);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        var after = session.State;
        Assert.NotNull(after.Hint);
        Assert.Contains(after.LegalActions, action => action.Id == after.Hint.Id);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Empty(after.History);
        Assert.False(after.Thinking);
        Assert.NotNull(after.LastSearch);
    }

    [Fact]
    public async Task NewGameDiscardsCancelledAiResultEvenWhenEvaluatorIgnoresCancellation()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var session = new WebGameSession(search: (position, budget, cancellation) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return FirstLegal(position, budget, cancellation);
        });
        var state = session.NewGame(1, 50);
        session.StartSearch(state.Revision);
        var oldCompletion = session.SearchCompletion;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var reset = session.NewGame(0, 50);
            Assert.False(reset.Thinking);
            release.Set();
            await oldCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(reset.Revision, session.State.Revision);
            Assert.Empty(session.State.History);
            Assert.Equal(0, session.State.HumanPlayer);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndoOrImportDuringSearchCannotPublishStaleMove(bool import)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var session = new WebGameSession(search: (position, budget, cancellation) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return FirstLegal(position, budget, cancellation);
        });
        var initial = session.State;
        var state = session.Move(initial.Revision, initial.LegalActions[0].Id);
        session.StartSearch(state.Revision);
        var oldCompletion = session.SearchCompletion;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var replaced = import ? session.Import(EnclosureHistory.FromActions([]).ToJson()) : session.Undo(state.Revision);
            release.Set();
            await oldCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(session.State.History);
            Assert.False(session.State.Thinking);
            Assert.Equal(replaced.Revision, session.State.Revision);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task CancellationSerializesSearchesAcrossGames()
    {
        var active = 0;
        var maximumActive = 0;
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var calls = 0;
        using var session = new WebGameSession(search: (position, budget, cancellation) =>
        {
            var running = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, running);
            try
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.Set();
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
                }
                return FirstLegal(position, budget, cancellation);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var firstGame = session.NewGame(1, 50);
        session.StartSearch(firstGame.Revision);
        var firstCompletion = session.SearchCompletion;
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));
            var secondGame = session.NewGame(1, 50);
            session.StartSearch(secondGame.Revision);
            var secondCompletion = session.SearchCompletion;
            releaseFirst.Set();
            await Task.WhenAll(firstCompletion, secondCompletion).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, maximumActive);
            Assert.Equal(2, calls);
            Assert.Single(session.State.History);
            Assert.False(session.State.Thinking);
        }
        finally { releaseFirst.Set(); }
    }

    [Fact]
    public async Task DuplicateThinkRequestsShareOneSearchAndOnePlacement()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var session = new WebGameSession(search: (position, budget, cancellation) =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return FirstLegal(position, budget, cancellation);
        });
        var before = session.NewGame(1, 50);
        session.StartSearch(before.Revision);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Parallel.For(0, 8, _ => Assert.True(session.StartSearch(before.Revision).Thinking));
            release.Set();
            await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, calls);
            Assert.Single(session.State.History);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task HumanMoveCancelsOutstandingHint()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var session = new WebGameSession(search: (position, budget, cancellation) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return FirstLegal(position, budget, cancellation);
        });
        var before = session.State;
        session.StartSearch(before.Revision, isHint: true);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var moved = session.Move(before.Revision, before.LegalActions[^1].Id);
            release.Set();
            await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(moved.Revision, session.State.Revision);
            Assert.Null(session.State.Hint);
            Assert.Single(session.State.History);
            Assert.False(session.State.Thinking);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task IllegalSearchResultDoesNotPartiallyCommitAMove()
    {
        using var session = new WebGameSession(search: (_, _, _) =>
            new(Enclosure.PassActionId, 1, 5, 1, 0, [-10]));
        var before = session.NewGame(1, 50);
        session.StartSearch(before.Revision);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(before.Revision, session.State.Revision);
        Assert.Empty(session.State.History);
        Assert.NotNull(session.State.Error);
    }

    [Fact]
    public void StaleAndIllegalMovesCannotMutateBoard()
    {
        using var session = new WebGameSession(search: FirstLegal);
        var before = session.State;
        Assert.Throws<InvalidOperationException>(() => session.Move(before.Revision - 1, before.LegalActions[0].Id));
        Assert.Throws<ArgumentException>(() => session.Move(before.Revision, Enclosure.PassActionId));
        Assert.Equal(before.Revision, session.State.Revision);
        Assert.Empty(session.State.History);
    }

    [Fact]
    public void SaveLoadReplaysScoreProtectionAndCoordinates()
    {
        using var session = new WebGameSession(search: FirstLegal);
        var game = new Enclosure();
        var actions = new List<int>();
        var random = new Random(1977);
        for (var i = 0; i < 25; i++)
        {
            var legal = game.GenerateLegalActions();
            var action = legal[random.Next(legal.Length)];
            actions.Add(action);
            game.Play(action);
        }
        var imported = session.Import(EnclosureHistory.FromActions(actions).ToJson());
        Assert.Equal(game.MoveNumber, imported.MoveNumber);
        Assert.Equal(game.Turn, imported.Turn);
        Assert.Equal(game.Scores, imported.Scores);
        Assert.Equal(game.Areas, imported.Areas);
        for (var player = 0; player < 2; player++)
        {
            Assert.Equal(game.Nodes(player), imported.Nodes[player]);
            Assert.Equal(game.Segments(player), imported.Segments[player]);
        }
        Assert.Equal(actions, EnclosureHistory.Parse(session.Export()).ActionIds());
        var before = session.State;
        var exception = Assert.Throws<InvalidDataException>(() => session.Import("{\"formatVersion\":1,\"game\":\"enclosure\",\"moves\":[{\"pass\":true}]}"));
        Assert.Contains("move 1", exception.Message);
        Assert.Equal(before.Revision, session.State.Revision);
        Assert.Equal(actions, EnclosureHistory.Parse(session.Export()).ActionIds());
    }

    [Fact]
    public async Task SearchErrorsAreVisibleAndDoNotMutateGame()
    {
        using var session = new WebGameSession(search: (_, _, _) => throw new InvalidOperationException("test failure"));
        var before = session.NewGame(1, 50);
        session.StartSearch(before.Revision);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(before.Revision, session.State.Revision);
        Assert.False(session.State.Thinking);
        Assert.Empty(session.State.History);
        Assert.Contains("test failure", session.State.Error);
    }

    [Fact]
    public void SettingsValidateBudgetsAndSeat()
    {
        using var session = new WebGameSession(search: FirstLegal);
        Assert.Throws<ArgumentException>(() => session.NewGame(2, 1000));
        Assert.Throws<ArgumentException>(() => session.Settings(49));
        Assert.Throws<ArgumentException>(() => session.Settings(double.NaN));
        Assert.Throws<ArgumentException>(() => session.Settings(20001));
        Assert.Throws<ArgumentException>(() => session.Settings(double.PositiveInfinity));
        Assert.Equal(50, session.Settings(50).MoveMilliseconds);
        Assert.Equal(20000, session.Settings(20000).MoveMilliseconds);
        Assert.Equal(20000, session.NewGame(0, 20000).MoveMilliseconds);
    }

    [Fact]
    public async Task OneSearchPlaysBothAiPlacementsFromItsPvAndNeverPlaysTheOpponentsMove()
    {
        var calls = 0;
        int[] planned = [];
        using var session = new WebGameSession(search: (position, budget, _) =>
        {
            calls++;
            Assert.Equal(20000, budget.TotalMilliseconds);
            planned = LegalSequence(position, 3);
            return new(planned[0], 3, 123, 999999, 0.25, planned);
        });
        var initial = session.NewGame(0, 20000);
        var before = session.Move(initial.Revision, initial.LegalActions[0].Id);
        session.StartSearch(before.Revision);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        var after = session.State;
        Assert.Equal(1, calls);
        Assert.Equal(planned.Take(2), after.History.Skip(1).Select(move => move.Action.Id));
        Assert.Equal(0, after.Turn);
        Assert.Equal(2, after.ActionsRemaining);
        Assert.Equal(before.Revision + 2, after.Revision);
        Assert.Equal(123, after.LastSearch!.Nodes);
        Assert.Equal(3, after.LastSearch.CompletedDepth);
        Assert.Equal(0.25, after.LastSearch.Value);
        Assert.Equal(before.Revision, after.LastSearch.PositionRevision);
        Assert.Equal(planned, after.LastSearch.PrincipalVariation.Select(action => action.Id));
        Assert.InRange(after.LastSearch.ElapsedMilliseconds, 0, 5000);
        Assert.False(after.Thinking);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("illegal")]
    [InlineData("mismatched")]
    public async Task MissingOrUnusablePvContinuationUsesOnlyTheRemainingBudget(string kind)
    {
        var budgets = new List<double>();
        using var session = new WebGameSession(search: (position, budget, _) =>
        {
            budgets.Add(budget.TotalMilliseconds);
            var legal = position.GenerateLegalActions();
            if (budgets.Count == 1)
            {
                Thread.Sleep(40);
                int[] pv = kind switch
                {
                    "illegal" => [legal[0], Enclosure.PassActionId],
                    "mismatched" => [legal[^1], -10],
                    _ => [legal[0]]
                };
                return new(legal[0], 1, 7, 1, 0.2, pv);
            }
            return new(legal[0], 3, 13, 1, 0.9, LegalSequence(position, 3));
        });
        var initial = session.NewGame(0, 1000);
        var before = session.Move(initial.Revision, initial.LegalActions[0].Id);
        session.StartSearch(before.Revision);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        var after = session.State;
        Assert.Equal(2, budgets.Count);
        Assert.Equal(1000, budgets[0]);
        Assert.InRange(budgets[1], double.Epsilon, 975);
        Assert.Equal(3, after.History.Length);
        Assert.Equal(0, after.Turn);
        Assert.Null(after.Error);
        Assert.Equal(20, after.LastSearch!.Nodes);
        Assert.Equal(1, after.LastSearch.CompletedDepth); // Conditional continuation does not deepen the root search.
        Assert.Equal(0.2, after.LastSearch.Value);
        Assert.True(after.LastSearch.ElapsedMilliseconds >= 35);
        Assert.Equal(after.History.Skip(1).Select(move => move.Action.Id),
            after.LastSearch.PrincipalVariation.Take(2).Select(action => action.Id));
    }

    [Fact]
    public async Task ExhaustedTurnBudgetUsesALegalContinuationWithoutStartingAnotherSearch()
    {
        var calls = 0;
        using var session = new WebGameSession(search: (position, _, _) =>
        {
            calls++;
            Thread.Sleep(70);
            return FirstLegal(position, TimeSpan.Zero, CancellationToken.None);
        });
        var initial = session.NewGame(0, 50);
        var before = session.Move(initial.Revision, initial.LegalActions[0].Id);
        session.StartSearch(before.Revision);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        var after = session.State;
        Assert.Equal(1, calls);
        Assert.Equal(3, after.History.Length);
        Assert.Equal(0, after.Turn);
        Assert.Null(after.Error);
        Assert.True(after.LastSearch!.ElapsedMilliseconds >= 50);
        Assert.Equal(2, after.LastSearch.PrincipalVariation.Length);
        EnclosureHistory.Parse(session.Export()).Replay();
    }

    [Theory]
    [InlineData("new")]
    [InlineData("undo")]
    [InlineData("import")]
    public async Task CancellingDuringContinuationPublishesNeitherAiPlacement(string operation)
    {
        using var enteredContinuation = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var session = new WebGameSession(search: (position, budget, cancellation) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                enteredContinuation.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
            return FirstLegal(position, budget, cancellation);
        });
        var initial = session.NewGame(0, 1000);
        var before = session.Move(initial.Revision, initial.LegalActions[0].Id);
        session.StartSearch(before.Revision);
        var running = session.SearchCompletion;
        try
        {
            Assert.True(enteredContinuation.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(before.Revision, session.State.Revision);
            Assert.Single(session.State.History); // The first AI placement is still on the detached board.
            var reset = operation switch
            {
                "new" => session.NewGame(0, 1000),
                "undo" => session.Undo(before.Revision),
                _ => session.Import(EnclosureHistory.FromActions([]).ToJson())
            };
            release.Set();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(reset.Revision, session.State.Revision);
            Assert.Empty(session.State.History);
            Assert.False(session.State.Thinking);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task IllegalFallbackCannotPartiallyPublishTheFirstPlacement()
    {
        var calls = 0;
        using var session = new WebGameSession(search: (position, budget, cancellation) =>
            ++calls == 1 ? FirstLegal(position, budget, cancellation)
                : new(Enclosure.PassActionId, 1, 1, 1, 0, [Enclosure.PassActionId]));
        var initial = session.State;
        var before = session.Move(initial.Revision, initial.LegalActions[0].Id);
        session.StartSearch(before.Revision);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(before.Revision, session.State.Revision);
        Assert.Single(session.State.History);
        Assert.Contains("illegal continuation", session.State.Error);
    }

    [Fact]
    public async Task HintsRemainSinglePositionSearchesEvenWhenTwoPlacementsRemain()
    {
        var calls = 0;
        using var session = new WebGameSession(search: (position, budget, _) =>
        {
            calls++;
            Assert.Equal(20000, budget.TotalMilliseconds);
            var pv = LegalSequence(position, 3);
            return new(pv[0], 3, 10, 1, 0, pv);
        });
        var initial = session.NewGame(1, 20000, analysisMode: true);
        var before = session.Move(initial.Revision, initial.LegalActions[0].Id);
        Assert.Equal(2, before.ActionsRemaining);
        session.StartSearch(before.Revision, isHint: true);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal(before.Revision, session.State.Revision);
        Assert.Single(session.State.History);
        Assert.NotNull(session.State.Hint);
    }

    [Fact]
    public async Task ImportedSinglePlacementRemainderDoesNotPlayTheOpponentsPvMove()
    {
        var calls = 0;
        using var session = new WebGameSession(search: (position, _, _) =>
        {
            calls++;
            var pv = LegalSequence(position, 3);
            return new(pv[0], 3, 10, 1, 0, pv);
        });
        var actions = LegalSequence(new Enclosure(), 2);
        var before = session.Import(EnclosureHistory.FromActions(actions).ToJson());
        Assert.Equal(1, before.ActionsRemaining);
        Assert.Equal(1, before.Turn);
        session.StartSearch(before.Revision);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal(3, session.State.History.Length);
        Assert.Equal(0, session.State.Turn);
    }

    [Fact]
    public async Task ForcedPassConsumesBothRemainingPlacementsWithoutPlayingAnOpponentMove()
    {
        var calls = 0;
        using var session = new WebGameSession(search: (position, _, _) =>
        {
            calls++;
            Assert.Equal(new[] { Enclosure.PassActionId }, position.GenerateLegalActions());
            return new(Enclosure.PassActionId, 1, 1, 1, 0, [Enclosure.PassActionId]);
        });
        session.NewGame(1, 50);
        var game = (Enclosure)typeof(WebGameSession).GetField("game", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var nodes = (bool[])typeof(Enclosure).GetField("nodes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        Array.Clear(nodes, 0, 361);
        typeof(Enclosure).GetProperty(nameof(Enclosure.ActionsRemaining))!.SetValue(game, 2);
        var before = session.State;
        session.StartSearch(before.Revision);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Single(session.State.History);
        Assert.True(session.State.History[0].Action.Pass);
        Assert.Equal(2, session.State.MoveNumber);
        Assert.Equal(1, session.State.Turn);
        Assert.Null(session.State.Error);
    }

    [Fact]
    public async Task WaitingForSharedCpuSlotDoesNotConsumeTheTurnBudget()
    {
        using var slots = new SemaphoreSlim(0, 1);
        double supplied = 0;
        using var session = new WebGameSession(search: (position, budget, cancellation) =>
        {
            supplied = budget.TotalMilliseconds;
            return FirstLegal(position, budget, cancellation);
        }, sharedSearchSlots: slots);
        var before = session.NewGame(1, 50);
        session.StartSearch(before.Revision);
        await Task.Delay(80);
        slots.Release();
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(50, supplied);
        Assert.Single(session.State.History);
        Assert.Equal(1, slots.CurrentCount);
    }

    private static int[] LegalSequence(Enclosure position, int length)
    {
        var replay = position.Copy();
        var actions = new List<int>();
        while (actions.Count < length && !replay.Finished)
        {
            var action = replay.GenerateLegalActions()[0];
            actions.Add(action);
            replay.Play(action);
        }
        return actions.ToArray();
    }
}
