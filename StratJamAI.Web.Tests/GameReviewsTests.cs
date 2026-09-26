using System.Text.Json;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Web;
using Xunit;

namespace StratJamAI.Web.Tests;

public sealed class GameReviewsTests
{
    private static EnclosureSearchResult FirstLegal(Enclosure game, TimeSpan _, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var action = game.GenerateLegalActions()[0];
        return new(action, 2, 17, 1, 999, [action, -1]);
    }

    [Fact]
    public void OpeningAReviewBuildsImmutableReplayPositionsWithoutSearching()
    {
        using var slots = new SemaphoreSlim(1, 1);
        using var reviews = new GameReviews(slots, search: (_, _, _) => throw new Exception("Must not search before Start"));
        var history = History(8);
        var state = reviews.Get("opaque-owner-key", history.ToJson(), includePositions: true);
        Assert.Equal("idle", state.Status);
        Assert.Equal(8, state.TotalMoves);
        Assert.Equal(9, state.Positions!.Length);
        Assert.Empty(state.Moves);
        Assert.Equal(0, state.Positions[0].MoveNumber);
        Assert.Equal(history.Replay().Scores, state.Positions[^1].Scores);
        var first = EnclosureHistory.FromActions(history.ActionIds().Take(1)).Replay();
        Assert.Equal(first.Segments(0), state.Positions[1].Segments[0]);
        Assert.Equal(first.Nodes(0), state.Positions[1].Nodes[0]);
        Assert.Null(reviews.Get("opaque-owner-key", history.ToJson()).Positions);
        Assert.DoesNotContain("opaque-owner-key", JsonSerializer.Serialize(state));
        Assert.Throws<InvalidOperationException>(() => reviews.Get("opaque-owner-key", History(9).ToJson()));
    }

    [Fact]
    public async Task ReviewUsesComparableStaticEstimatesAndPreservesActualHistory()
    {
        using var slots = new SemaphoreSlim(1, 1);
        using var reviews = new GameReviews(slots, search: (game, budget, cancellation) =>
        {
            Assert.Equal(100, budget.TotalMilliseconds);
            var action = game.GenerateLegalActions()[^1];
            return new(action, 3, 42, 999999, -999, [action]);
        });
        var history = History(6);
        reviews.Start("game", history.ToJson());
        await reviews.Completion("game").WaitAsync(TimeSpan.FromSeconds(5));
        var result = reviews.Get("game", history.ToJson(), includePositions: true);
        Assert.Equal("completed", result.Status);
        Assert.Equal(6, result.CompletedMoves);
        Assert.Equal(6, result.Players.Sum(player => player.ReviewedMoves));
        var actual = new Enclosure();
        foreach (var row in result.Moves)
        {
            var player = actual.Turn;
            Assert.Equal(player, row.Player);
            Assert.Equal(EnclosureStrategyEvaluation.Evaluate(actual, player), row.EstimateBefore);
            var alternative = actual.Copy(); alternative.Play(row.Suggested.Id);
            actual.Play(row.Played.Id);
            Assert.Equal(EnclosureStrategyEvaluation.Evaluate(actual, player), row.EstimateAfterPlayed);
            Assert.Equal(EnclosureStrategyEvaluation.Evaluate(alternative, player), row.EstimateAfterSuggested);
            Assert.Equal(row.EstimateAfterSuggested - row.EstimateAfterPlayed, row.AlternativeDifference);
            Assert.InRange(row.EstimateBefore, -1, 1); // Arbitrary engine search values were not called move losses.
            Assert.InRange(row.ElapsedMilliseconds, 0, 5000);
            Assert.Equal(3, row.CompletedDepth);
            Assert.Equal(42, row.Nodes);
        }
        Assert.Equal(history.ActionIds(), result.Moves.Select(row => row.Played.Id));
        Assert.Equal(history.Replay().Areas, result.Positions![^1].Areas);
        Assert.Equal(1, slots.CurrentCount);
    }

    [Fact]
    public async Task ConcurrentParticipantsShareOneJobAndCompletedResultsAreReused()
    {
        using var slots = new SemaphoreSlim(0, 1);
        var calls = 0;
        using var reviews = new GameReviews(slots, search: (game, budget, cancellation) =>
        { Interlocked.Increment(ref calls); return FirstLegal(game, budget, cancellation); });
        var history = History(3).ToJson();
        reviews.Start("same-game", history);
        var originalTask = reviews.Completion("same-game");
        Parallel.For(0, 12, _ => Assert.Equal("running", reviews.Start("same-game", history, 1000).Status));
        Assert.Same(originalTask, reviews.Completion("same-game"));
        slots.Release();
        await originalTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, calls);
        Assert.Equal("completed", reviews.Start("same-game", history).Status);
        Assert.Equal(100, reviews.Get("same-game", history).BudgetMilliseconds);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ProgressIsPublishedPerMoveAndCancellationDiscardsALateResult()
    {
        using var slots = new SemaphoreSlim(1, 1);
        using var secondEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var reviews = new GameReviews(slots, search: (game, budget, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            { secondEntered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
            return FirstLegal(game, budget, CancellationToken.None);
        });
        var history = History(4).ToJson();
        reviews.Start("game", history);
        try
        {
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, reviews.Get("game", history).CompletedMoves);
            var cancelled = reviews.Cancel("game");
            Assert.Equal("cancelled", cancelled.Status);
            Assert.Equal(1, cancelled.CompletedMoves);
            release.Set();
            await reviews.Completion("game").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(reviews.Get("game", history).Moves);
            Assert.Equal(1, slots.CurrentCount);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task CancelledJobCannotOverlapOrPublishIntoItsRestart()
    {
        using var slots = new SemaphoreSlim(2, 2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0; var active = 0; var maximum = 0;
        using var reviews = new GameReviews(slots, search: (game, budget, _) =>
        {
            var count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count);
            try
            {
                if (Interlocked.Increment(ref calls) == 1)
                { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
                return FirstLegal(game, budget, CancellationToken.None);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var history = History(2).ToJson();
        reviews.Start("game", history);
        var old = reviews.Completion("game");
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            reviews.Cancel("game");
            reviews.Start("game", history, 50);
            var restarted = reviews.Completion("game");
            release.Set();
            await Task.WhenAll(old, restarted).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, maximum);
            Assert.Equal(3, calls);
            Assert.Equal("completed", reviews.Get("game", history).Status);
            Assert.Equal(2, reviews.Get("game", history).CompletedMoves);
            Assert.Equal(2, slots.CurrentCount);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ReviewsShareTheInteractiveGamesCpuLimit()
    {
        using var slots = new SemaphoreSlim(1, 1);
        using var interactiveEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reviewCalls = 0;
        using var interactive = new WebGameSession(sharedSearchSlots: slots, search: (game, budget, cancellation) =>
        { interactiveEntered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return FirstLegal(game, budget, cancellation); });
        using var reviews = new GameReviews(slots, search: (game, budget, cancellation) =>
        { Interlocked.Increment(ref reviewCalls); return FirstLegal(game, budget, cancellation); });
        interactive.StartSearch(interactive.State.Revision, isHint: true);
        try
        {
            Assert.True(interactiveEntered.Wait(TimeSpan.FromSeconds(5)));
            var history = History(2).ToJson();
            reviews.Start("first", history); reviews.Start("second", history);
            await Task.Delay(20);
            Assert.Equal(0, reviewCalls);
            release.Set();
            await Task.WhenAll(interactive.SearchCompletion, reviews.Completion("first"), reviews.Completion("second"))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, reviewCalls);
            Assert.Equal(1, slots.CurrentCount);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task InvalidSearchFailsWithoutPublishingAndCanBeRetried()
    {
        using var slots = new SemaphoreSlim(1, 1);
        var fail = true;
        using var reviews = new GameReviews(slots, search: (game, budget, cancellation) => fail
            ? new(Enclosure.PassActionId, 1, 1, 1, 0, []) : FirstLegal(game, budget, cancellation));
        var history = History(1).ToJson();
        reviews.Start("game", history);
        await reviews.Completion("game").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("error", reviews.Get("game", history).Status);
        Assert.Empty(reviews.Get("game", history).Moves);
        Assert.Contains("illegal", reviews.Get("game", history).Error);
        Assert.Equal(1, slots.CurrentCount);
        fail = false;
        reviews.Start("game", history);
        await reviews.Completion("game").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("completed", reviews.Get("game", history).Status);
        Assert.Single(reviews.Get("game", history).Moves[0].PrincipalVariation); // Illegal PV tail was discarded.
    }

    [Fact]
    public async Task ExpirationCancelsQueuedWorkAndReclaimsBoundedCapacity()
    {
        using var slots = new SemaphoreSlim(0, 1);
        var clock = new ManualClock();
        using var reviews = new GameReviews(slots, maxReviews: 1, idleTimeout: TimeSpan.FromMinutes(1), timeProvider: clock,
            search: (_, _, _) => throw new Exception("No CPU slot is available"));
        var history = History(2).ToJson();
        reviews.Start("old", history);
        var pending = reviews.Completion("old");
        Assert.Throws<GameReviewCapacityException>(() => reviews.Get("new", history));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal("idle", reviews.Get("new", history).Status);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, slots.CurrentCount);
    }

    [Fact]
    public async Task DisposalCancelsQueuedReviewsWithoutDisposingTheSharedSemaphore()
    {
        using var slots = new SemaphoreSlim(0, 1);
        var reviews = new GameReviews(slots, search: FirstLegal);
        reviews.Start("game", History(1).ToJson());
        var pending = reviews.Completion("game");
        reviews.Dispose(); reviews.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => reviews.Get("game", History(1).ToJson()));
        Assert.Equal(0, slots.CurrentCount);
        slots.Release();
        Assert.Equal(1, slots.CurrentCount);
    }

    [Fact]
    public async Task EmptyCompletedMatchNeedsNoSearchAndInvalidInputsDoNotConsumeCapacity()
    {
        using var slots = new SemaphoreSlim(1, 1);
        using var reviews = new GameReviews(slots, maxReviews: 1, search: (_, _, _) => throw new Exception("No moves"));
        var empty = History(0).ToJson();
        Assert.Throws<ArgumentException>(() => reviews.Start("game", empty, 49));
        Assert.Throws<ArgumentException>(() => reviews.Start("game", empty, 1001));
        Assert.Throws<ArgumentException>(() => reviews.Start("game", empty, double.NaN));
        Assert.Throws<InvalidDataException>(() => reviews.Get("bad", "not JSON"));
        Assert.Throws<InvalidDataException>(() => reviews.Get("bad", EnclosureHistory.FromActions([Enclosure.PassActionId]).ToJson()));
        reviews.Start("empty-finished-by-clock", empty, 1000);
        await reviews.Completion("empty-finished-by-clock").WaitAsync(TimeSpan.FromSeconds(5));
        var state = reviews.Get("empty-finished-by-clock", empty, true);
        Assert.Equal("completed", state.Status);
        Assert.Single(state.Positions!);
        Assert.Equal(0, state.TotalMoves);
        Assert.All(state.Players, player => Assert.Equal(0, player.ReviewedMoves));
    }

    private static EnclosureHistory History(int count)
    {
        var game = new Enclosure();
        var actions = new List<int>();
        while (actions.Count < count && !game.Finished)
        { var action = game.GenerateLegalActions()[0]; actions.Add(action); game.Play(action); }
        return EnclosureHistory.FromActions(actions);
    }
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
