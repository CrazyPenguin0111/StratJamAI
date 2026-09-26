using StratJamAI.Web;
using Xunit;

namespace StratJamAI.Web.Tests;

public sealed class WebGameSessionsTests
{
    [Fact]
    public void KnownCookieRetainsItsGameAndOtherCookiesGetIndependentGames()
    {
        using var pool = new WebGameSessions(moveMilliseconds: 250);
        using var first = pool.Acquire(null);
        Assert.True(first.IsNew);
        Assert.Matches("^[0-9A-F]{64}$", first.Id);
        var state = first.Session.State;
        first.Session.Move(state.Revision, state.LegalActions[0].Id);

        using var restored = pool.Acquire(first.Id);
        Assert.False(restored.IsNew);
        Assert.Same(first.Session, restored.Session);
        Assert.Single(restored.Session.State.History);

        using var other = pool.Acquire(null);
        Assert.NotEqual(first.Id, other.Id);
        Assert.NotSame(first.Session, other.Session);
        Assert.Empty(other.Session.State.History);
        Assert.Equal(250, other.Session.State.MoveMilliseconds);
    }

    [Theory]
    [InlineData("untrusted-cookie")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void UnknownCookieCannotChooseTheSessionIdentifier(string supplied)
    {
        using var pool = new WebGameSessions();
        using var lease = pool.Acquire(supplied);
        Assert.True(lease.IsNew);
        Assert.NotEqual(supplied, lease.Id);
        Assert.Matches("^[0-9A-F]{64}$", lease.Id);
    }

    [Fact]
    public void FullPoolRejectsNewGamesButExistingBrowserStillWorks()
    {
        using var pool = new WebGameSessions(maxSessions: 1);
        using var first = pool.Acquire(null);
        Assert.Throws<SessionCapacityException>(() => pool.Acquire(null));
        Assert.Throws<SessionCapacityException>(() => pool.Acquire(new string('B', 64)));
        using var again = pool.Acquire(first.Id);
        Assert.False(again.IsNew);
        Assert.Same(first.Session, again.Session);
    }

    [Fact]
    public void IdleCleanupReclaimsCapacityAndRotatesExpiredCookies()
    {
        var clock = new ManualClock();
        using var pool = new WebGameSessions(maxSessions: 2, idleTimeout: TimeSpan.FromMinutes(10), timeProvider: clock);
        var first = pool.Acquire(null);
        var second = pool.Acquire(null);
        var oldId = first.Id;
        first.Dispose();
        second.Dispose();
        clock.Advance(TimeSpan.FromMinutes(11));
        clock.FireTimers();
        Assert.Throws<ObjectDisposedException>(() => first.Session.Settings(100));
        Assert.Throws<ObjectDisposedException>(() => second.Session.Settings(100));

        using var next = pool.Acquire(oldId);
        Assert.True(next.IsNew);
        Assert.NotEqual(oldId, next.Id);
        using var another = pool.Acquire(null);
        Assert.True(another.IsNew);
    }

    [Fact]
    public void InFlightLeaseCannotExpireAndReleaseStartsANewIdleWindow()
    {
        var clock = new ManualClock();
        using var pool = new WebGameSessions(maxSessions: 1, idleTimeout: TimeSpan.FromMinutes(10), timeProvider: clock);
        var first = pool.Acquire(null);
        clock.Advance(TimeSpan.FromHours(1));
        clock.FireTimers();
        Assert.Throws<SessionCapacityException>(() => pool.Acquire(null));
        using (var stillActive = pool.Acquire(first.Id)) Assert.Same(first.Session, stillActive.Session);

        first.Dispose();
        first.Dispose(); // Lease release is idempotent; it must not make the lease count negative.
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Throws<SessionCapacityException>(() => pool.Acquire(null));
        clock.Advance(TimeSpan.FromMinutes(2));
        using var replacement = pool.Acquire(null);
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Throws<ObjectDisposedException>(() => first.Session.Settings(100));
    }

    [Fact]
    public void SuccessfulRequestsRefreshIdleExpiration()
    {
        var clock = new ManualClock();
        using var pool = new WebGameSessions(maxSessions: 1, idleTimeout: TimeSpan.FromMinutes(10), timeProvider: clock);
        string id;
        using (var first = pool.Acquire(null)) id = first.Id;
        clock.Advance(TimeSpan.FromMinutes(9));
        using (var same = pool.Acquire(id)) Assert.False(same.IsNew);
        clock.Advance(TimeSpan.FromMinutes(9));
        clock.FireTimers();
        using var stillSame = pool.Acquire(id);
        Assert.False(stillSame.IsNew);
    }

    [Fact]
    public void DisposeCancelsGamesAndRejectsFurtherRequests()
    {
        var pool = new WebGameSessions();
        using var lease = pool.Acquire(null);
        pool.Dispose();
        pool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.Session.Settings(50));
        Assert.Throws<ObjectDisposedException>(() => pool.Acquire(lease.Id));
    }

    [Fact]
    public void ConcurrentRequestsForOneCookieShareOneSession()
    {
        using var pool = new WebGameSessions(maxSessions: 1);
        using var initial = pool.Acquire(null);
        Parallel.For(0, 32, _ =>
        {
            using var lease = pool.Acquire(initial.Id);
            Assert.False(lease.IsNew);
            Assert.Same(initial.Session, lease.Session);
        });
    }

    [Fact]
    public void RejectsInvalidPoolLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebGameSessions(maxSessions: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebGameSessions(maxSearches: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebGameSessions(idleTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new WebGameSessions(moveMilliseconds: double.NaN));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            timers.Add(timer);
            return timer;
        }
        public void FireTimers() { foreach (var timer in timers) timer.Fire(); }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        public void Fire() { if (!disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
        public void Dispose() => disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
