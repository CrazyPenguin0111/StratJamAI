using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Web;
using Xunit;

namespace StratJamAI.Web.Tests;

public sealed class SharedSearchConcurrencyTests
{
    private static EnclosureSearchResult FirstLegal(Enclosure game)
    {
        var action = game.GenerateLegalActions()[0];
        return new(action, 1, 1, 1, 0, [action]);
    }

    [Fact]
    public async Task CancelledQueuedSearchDoesNotRunOrReleaseAnUnownedSlot()
    {
        using var slots = new SemaphoreSlim(0, 1);
        var calls = 0;
        using var session = new WebGameSession(search: (game, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return FirstLegal(game);
        }, sharedSearchSlots: slots);

        session.StartSearch(session.State.Revision, isHint: true);
        var cancelled = session.SearchCompletion;
        session.NewGame(0, 50);
        await cancelled.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, calls);
        Assert.Equal(0, slots.CurrentCount);

        slots.Release();
        session.StartSearch(session.State.Revision, isHint: true);
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal(1, slots.CurrentCount);
        Assert.NotNull(session.State.Hint);
    }

    [Fact]
    public async Task MultipleGamesRespectSharedSearchConcurrency()
    {
        using var slots = new SemaphoreSlim(2, 2);
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var active = 0;
        var maximumActive = 0;
        var calls = 0;
        var countLock = new object();
        EnclosureSearchResult Search(Enclosure game, TimeSpan _, CancellationToken cancellation)
        {
            lock (countLock)
            {
                active++;
                maximumActive = Math.Max(active, maximumActive);
                if (++calls <= 2) entered.Signal();
            }
            try
            {
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                return FirstLegal(game);
            }
            finally { lock (countLock) active--; }
        }

        using var first = new WebGameSession(search: Search, sharedSearchSlots: slots);
        using var second = new WebGameSession(search: Search, sharedSearchSlots: slots);
        using var third = new WebGameSession(search: Search, sharedSearchSlots: slots);
        first.StartSearch(first.State.Revision, isHint: true);
        second.StartSearch(second.State.Revision, isHint: true);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            third.StartSearch(third.State.Revision, isHint: true);
            await Task.Delay(30);
            lock (countLock) Assert.Equal(2, calls);
            Assert.False(third.SearchCompletion.IsCompleted);
            release.Set();
            await Task.WhenAll(first.SearchCompletion, second.SearchCompletion, third.SearchCompletion)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, maximumActive);
            Assert.Equal(3, calls);
            Assert.Equal(2, slots.CurrentCount);
            Assert.NotNull(first.State.Hint);
            Assert.NotNull(second.State.Hint);
            Assert.NotNull(third.State.Hint);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ThrowingEvaluatorReleasesItsSharedSlot()
    {
        using var slots = new SemaphoreSlim(1, 1);
        using var failing = new WebGameSession(search: (_, _, _) => throw new InvalidOperationException("test failure"),
            sharedSearchSlots: slots);
        using var next = new WebGameSession(search: (game, _, _) => FirstLegal(game), sharedSearchSlots: slots);
        failing.StartSearch(failing.State.Revision, isHint: true);
        await failing.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(failing.State.Error);
        Assert.Equal(1, slots.CurrentCount);
        next.StartSearch(next.State.Revision, isHint: true);
        await next.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(next.State.Hint);
        Assert.Equal(1, slots.CurrentCount);
    }

    [Fact]
    public async Task DisposingQueuedGameCancelsItsSearchWithoutLeakingSlot()
    {
        using var slots = new SemaphoreSlim(0, 1);
        var calls = 0;
        var session = new WebGameSession(search: (game, _, _) =>
        {
            calls++;
            return FirstLegal(game);
        }, sharedSearchSlots: slots);
        session.StartSearch(session.State.Revision, isHint: true);
        session.Dispose();
        await session.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, calls);
        Assert.Equal(0, slots.CurrentCount);
    }
}
