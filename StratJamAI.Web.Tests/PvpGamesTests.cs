using System.Text.Json;
using StratJamAI.Core.Games;
using StratJamAI.Web;
using Xunit;

namespace StratJamAI.Web.Tests;

public sealed class PvpGamesTests
{
    private const string Alice = "alice-opaque-session-token";
    private const string Bob = "bob-opaque-session-token";

    [Fact]
    public void PrivateLobbyRequiresBothReadyAndResetsConsentWhenClockChanges()
    {
        using var games = new PvpGames();
        var room = games.Create(Alice, colorPreference: "red", displayName: "Alice");
        Assert.Matches("^[A-HJ-NP-Z2-9]{6}$", room.Code!);
        Assert.Equal("waiting", room.Status);
        Assert.Null(room.Board);
        room = games.Join(Bob, room.Code!.ToLowerInvariant(), "blue", "Bob");
        room = games.Ready(Alice, room.Code!, room.Revision);
        Assert.Equal("waiting", room.Status);
        Assert.Throws<UnauthorizedAccessException>(() => games.Settings(Bob, room.Code!, room.Revision, new(180, 15)));
        room = games.Settings(Alice, room.Code!, room.Revision, new(180, 15));
        Assert.All(room.Players, player => Assert.False(player.Ready));
        room = games.Ready(Alice, room.Code!, room.Revision);
        room = games.Ready(Bob, room.Code!, room.Revision);
        Assert.Equal("active", room.Status);
        Assert.Equal(0, room.ViewerColor);
        Assert.Equal(1, games.State(Alice).ViewerColor);
        Assert.Equal(180000, room.RemainingMilliseconds[0]);
        Assert.Empty(games.State(Alice).Board!.LegalActions);
        Assert.NotEmpty(room.Board!.LegalActions);
        var json = JsonSerializer.Serialize(room);
        Assert.DoesNotContain(Alice, json);
        Assert.DoesNotContain(Bob, json);
        Assert.DoesNotContain("Identity", json);
        Assert.Throws<InvalidOperationException>(() => games.Settings(Alice, room.Code!, room.Revision, new(120, 15)));
    }

    [Fact]
    public void QuickMatchingUsesIdenticalClocksAndCompatibleColorsAndIsIdempotentWhileQueued()
    {
        using var games = new PvpGames();
        var first = games.Quick(Alice, new(120, 15), "blue");
        var second = games.Quick(Bob, new(120, 15), "blue");
        Assert.NotEqual(first.Code, second.Code);
        Assert.Equal(second.Code, games.Quick(Bob).Code);
        var differentClock = games.Quick("third", new(60, 15), "red");
        Assert.Equal("queued", differentClock.Status);
        var matched = games.Quick("fourth", new(120, 15), "red");
        Assert.Equal(first.Code, matched.Code);
        Assert.Equal("active", matched.Status);
        Assert.Equal(1, matched.ViewerColor);
        Assert.Equal(0, games.State(Alice).ViewerColor);
        Assert.Equal("queued", games.State(Bob).Status);
        Assert.Throws<InvalidOperationException>(() => games.Create(Alice));
        Assert.Throws<InvalidOperationException>(() => games.Join(Alice, second.Code!));
        var left = games.Leave(Bob, second.Code!, second.Revision);
        Assert.Equal("idle", left.Status);
        Assert.Equal("idle", games.State(Bob).Status);
        Assert.Throws<KeyNotFoundException>(() => games.State(Bob, second.Code));
    }

    [Fact]
    public void ReconnectingRetainsAssignedColorsAndCannotTakeAnotherSeat()
    {
        using var games = new PvpGames();
        var room = games.Quick(Alice);
        room = games.Quick(Bob);
        var color = room.ViewerColor;
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(color, games.State(Bob).ViewerColor);
            Assert.Equal(color, games.Join(Bob, room.Code!, "blue").ViewerColor);
        }
        Assert.Equal(2, games.State(Alice).Players.Length);
        Assert.Equal(1 - color, games.State(Alice).ViewerColor);
        Assert.Throws<InvalidOperationException>(() => games.Join("outsider", room.Code!));
        Assert.Throws<UnauthorizedAccessException>(() => games.State("outsider", room.Code));
    }

    [Fact]
    public void ClockRunsAcrossBothPlacementsAndAddsIncrementOnlyAfterTheTurn()
    {
        var clock = new ManualClock();
        using var games = new PvpGames(timeProvider: clock);
        var room = Start(games);
        clock.Advance(TimeSpan.FromSeconds(3));
        room = PlayFirst(games, Alice, room);
        Assert.Equal(132000, room.RemainingMilliseconds[0]); // Opening uses one placement.
        Assert.Equal(120000, room.RemainingMilliseconds[1]);
        clock.Advance(TimeSpan.FromSeconds(4));
        room = PlayFirst(games, Bob, room);
        Assert.Equal(116000, room.RemainingMilliseconds[1]);
        Assert.Equal(1, room.Board!.Turn);
        Assert.Equal(1, room.Board.ActionsRemaining);
        clock.Advance(TimeSpan.FromSeconds(5));
        room = PlayFirst(games, Bob, room);
        Assert.Equal(126000, room.RemainingMilliseconds[1]);
        Assert.Equal(0, room.Board!.Turn);
        clock.Advance(TimeSpan.FromSeconds(2));
        var refreshed = games.State(Alice);
        Assert.Equal(130000, refreshed.RemainingMilliseconds[0]);
        Assert.Equal(126000, refreshed.RemainingMilliseconds[1]);
        Assert.Equal(room.Revision, refreshed.Revision); // Clock ticks do not stale legal moves.
    }

    [Fact]
    public void DeadlineOnSecondPlacementRejectsMoveAndFinishesWithoutIncrement()
    {
        var clock = new ManualClock();
        using var games = new PvpGames(timeProvider: clock);
        var room = Start(games, new(10, 15));
        room = PlayFirst(games, Alice, room);
        clock.Advance(TimeSpan.FromSeconds(4));
        room = PlayFirst(games, Bob, room);
        var bob = games.State(Bob);
        var action = bob.Board!.LegalActions[0].Id;
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Throws<InvalidOperationException>(() => games.Move(Bob, room.Code!, room.Revision, action));
        var ended = games.State(Bob);
        Assert.Equal("finished", ended.Status);
        Assert.Equal("timeout", ended.ResultReason);
        Assert.Equal(0, ended.Winner);
        Assert.Equal(0, ended.RemainingMilliseconds[1]);
        Assert.Equal(2, ended.Board!.History.Length);
        Assert.True(ended.Board.Finished);
        Assert.Empty(ended.Board.LegalActions);
        var replay = games.FinishedHistory(Bob, room.Code!).Replay();
        Assert.Equal(2, replay.MoveNumber);
        Assert.False(replay.Finished);
    }

    [Fact]
    public void MembershipTurnRevisionAndLegalityAreAuthoritative()
    {
        using var games = new PvpGames();
        var room = Start(games);
        var blue = games.State(Alice);
        var action = blue.Board!.LegalActions[0].Id;
        Assert.Throws<UnauthorizedAccessException>(() => games.Move("outsider", room.Code!, room.Revision, action));
        Assert.Throws<InvalidOperationException>(() => games.Move(Bob, room.Code!, room.Revision, action));
        Assert.Throws<InvalidOperationException>(() => games.Move(Alice, room.Code!, room.Revision - 1, action));
        Assert.Throws<ArgumentException>(() => games.Move(Alice, room.Code!, room.Revision, Enclosure.PassActionId));
        Assert.Empty(games.State(Alice).Board!.History);
        Assert.Throws<InvalidOperationException>(() => games.ExportHistory(Alice, room.Code!));
        Assert.Throws<UnauthorizedAccessException>(() => games.FinishedHistory("outsider", room.Code!));
        Assert.Throws<InvalidOperationException>(() => games.Leave(Alice, room.Code!, room.Revision));
    }

    [Fact]
    public void RepeatedConcurrentMoveRequestsCommitOnce()
    {
        using var games = new PvpGames();
        var room = Start(games);
        var action = games.State(Alice).Board!.LegalActions[0].Id;
        var successes = 0;
        Parallel.For(0, 24, _ =>
        {
            try { games.Move(Alice, room.Code!, room.Revision, action); Interlocked.Increment(ref successes); }
            catch (InvalidOperationException) { }
        });
        Assert.Equal(1, successes);
        Assert.Single(games.State(Alice).Board!.History);
        Assert.Equal(1, games.State(Bob).Board!.Turn);
    }

    [Fact]
    public void ConcurrentJoinsCannotCreateAThirdSeat()
    {
        using var games = new PvpGames();
        var room = games.Create(Alice);
        var successes = 0;
        Parallel.For(0, 16, i =>
        {
            try { games.Join("guest-" + i, room.Code!); Interlocked.Increment(ref successes); }
            catch (InvalidOperationException) { }
        });
        Assert.Equal(1, successes);
        Assert.Equal(2, games.State(Alice).Players.Length);
    }

    [Fact]
    public void ConflictingColorChangesDoNotAlterLobbyOrReadiness()
    {
        using var games = new PvpGames();
        var room = games.Create(Alice, colorPreference: "blue");
        Assert.Throws<ArgumentException>(() => games.Join(Bob, room.Code!, "blue"));
        room = games.Join(Bob, room.Code!, "red");
        room = games.Ready(Alice, room.Code!, room.Revision);
        Assert.Throws<ArgumentException>(() => games.Settings(Bob, room.Code!, room.Revision, colorPreference: "blue"));
        Assert.Equal(room.Revision, games.State(Alice).Revision);
        Assert.True(games.State(Alice).Players.Single(player => player.IsYou).Ready);
        room = games.Settings(Bob, room.Code!, room.Revision, colorPreference: "random");
        Assert.All(room.Players, player => Assert.False(player.Ready));
    }

    [Fact]
    public void ResignationRetainsParticipantHistoryAndLeavingDoesNotGrantSpectatorAccess()
    {
        using var games = new PvpGames();
        var room = Start(games);
        room = PlayFirst(games, Alice, room);
        room = games.Resign(Bob, room.Code!, room.Revision);
        Assert.Equal("resignation", room.ResultReason);
        Assert.Equal(0, room.Winner);
        var history = games.ExportHistory(Alice, room.Code!);
        Assert.Single(EnclosureHistory.Parse(history).Moves);
        Assert.Equal("idle", games.Leave(Alice, room.Code!, room.Revision).Status);
        Assert.Equal(history, games.ExportHistory(Alice, room.Code!));
        var next = games.Create(Alice);
        Assert.NotEqual(next.Code, room.Code);
        Assert.Equal("finished", games.State(Alice, room.Code).Status);
        Assert.Equal(next.Code, games.State(Alice).Code);
        Assert.Throws<UnauthorizedAccessException>(() => games.ExportHistory("outsider", room.Code!));
    }

    [Fact]
    public void FullLegalGameEndsByCumulativeScoresAndExportsExactly()
    {
        using var games = new PvpGames();
        var room = Start(games);
        var reference = new Enclosure();
        var actions = new List<int>();
        var random = new Random(913);
        while (!reference.Finished)
        {
            var legal = reference.GenerateLegalActions();
            var action = legal[random.Next(legal.Length)];
            var actor = reference.Turn == 0 ? Alice : Bob;
            room = games.Move(actor, room.Code!, room.Revision, action);
            reference.Play(action); actions.Add(action);
        }
        Assert.Equal("finished", room.Status);
        Assert.Equal("score", room.ResultReason);
        var difference = Math.Sign(reference.Scores[0] - reference.Scores[1]);
        Assert.Equal(difference == 0 ? (int?)null : difference > 0 ? 0 : 1, room.Winner);
        Assert.Equal(reference.Scores.ToArray(), room.Board!.Scores);
        Assert.Equal(actions, games.FinishedHistory(Alice, room.Code!).ActionIds());
        Assert.Throws<InvalidOperationException>(() => games.Move(Alice, room.Code!, room.Revision, 0));
    }

    [Fact]
    public void HostLeavingTransfersLobbyControlAndClearsReadiness()
    {
        using var games = new PvpGames();
        var room = games.Create(Alice);
        room = games.Join(Bob, room.Code!);
        room = games.Ready(Bob, room.Code!, room.Revision);
        games.Leave(Alice, room.Code!, room.Revision);
        room = games.State(Bob);
        Assert.Single(room.Players);
        Assert.True(room.Players[0].IsHost);
        Assert.False(room.Players[0].Ready);
        room = games.Settings(Bob, room.Code!, room.Revision, new(60, 0));
        Assert.Equal(60, room.Settings.InitialSeconds);
        Assert.Throws<UnauthorizedAccessException>(() => games.State(Alice, room.Code));
    }

    [Fact]
    public void MonotonicClockIgnoresWallClockChangesAndTimeoutPrecedesResignation()
    {
        var clock = new ManualClock();
        using var games = new PvpGames(timeProvider: clock);
        var room = Start(games, new(10, 0));
        clock.WallClock += TimeSpan.FromDays(2);
        Assert.Equal(10000, games.State(Alice).RemainingMilliseconds[0]);
        clock.WallClock -= TimeSpan.FromDays(4);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Throws<InvalidOperationException>(() => games.Resign(Alice, room.Code!, room.Revision));
        room = games.State(Alice);
        Assert.Equal("timeout", room.ResultReason);
        Assert.Equal(1, room.Winner);
    }

    [Fact]
    public void CleanupExpiresDisconnectedQueuesButHeartbeatKeepsSearchAlive()
    {
        var clock = new ManualClock();
        using var games = new PvpGames(timeProvider: clock);
        var first = games.Quick(Alice, colorPreference: "blue");
        clock.Advance(TimeSpan.FromSeconds(60));
        games.State(Alice);
        clock.Advance(TimeSpan.FromSeconds(60));
        clock.FireTimers();
        Assert.Equal(first.Code, games.State(Alice).Code);
        clock.Advance(TimeSpan.FromSeconds(91));
        clock.FireTimers();
        Assert.Equal("idle", games.State(Alice).Status);
        var second = games.Quick(Bob, colorPreference: "red");
        Assert.Equal("queued", second.Status);
        Assert.NotEqual(first.Code, second.Code);
    }

    [Fact]
    public void CleanupRunsClocksWithoutPollingAndRetainsFinishedRoomUntilExpiry()
    {
        var clock = new ManualClock();
        using var games = new PvpGames(timeProvider: clock);
        var room = Start(games, new(10, 0));
        clock.Advance(TimeSpan.FromSeconds(11));
        clock.FireTimers();
        Assert.Equal("finished", games.State(Alice).Status);
        Assert.NotNull(games.FinishedHistory(Bob, room.Code!));
        clock.Advance(TimeSpan.FromHours(2));
        clock.FireTimers();
        Assert.Equal("idle", games.State(Alice).Status);
        Assert.Throws<KeyNotFoundException>(() => games.State(Bob, room.Code));
    }

    [Fact]
    public void CapacityNeverEvictsActiveRoomButCanReclaimFinishedHistory()
    {
        using var games = new PvpGames(maxRooms: 1);
        var room = Start(games);
        Assert.Throws<PvpCapacityException>(() => games.Create("outsider"));
        room = games.Resign(Alice, room.Code!, room.Revision);
        var replacement = games.Create("outsider");
        Assert.NotEqual(room.Code, replacement.Code);
        Assert.Throws<KeyNotFoundException>(() => games.State(Alice, room.Code));
    }

    [Fact]
    public void InvalidSettingsNamesAndDisposedServiceAreRejected()
    {
        using var games = new PvpGames();
        Assert.Throws<ArgumentException>(() => games.Create(Alice, new(9, 15)));
        Assert.Throws<ArgumentException>(() => games.Create(Alice, new(3601, 15)));
        Assert.Throws<ArgumentException>(() => games.Create(Alice, new(120, -1)));
        Assert.Throws<ArgumentException>(() => games.Create(Alice, new(120, 121)));
        Assert.Throws<ArgumentException>(() => games.Create(Alice, colorPreference: "green"));
        Assert.Throws<ArgumentException>(() => games.Create(Alice, displayName: new string('x', 21)));
        Assert.Throws<ArgumentException>(() => games.Create(Alice, displayName: "line\nbreak"));
        Assert.Throws<ArgumentException>(() => games.Create(""));
        Assert.Throws<ArgumentException>(() => games.Join(Alice, "bad"));
        games.Dispose();
        Assert.Throws<ObjectDisposedException>(() => games.State(Alice));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PvpGames(maxRooms: 0));
    }

    private static PvpState Start(PvpGames games, PvpSettings? settings = null)
    {
        games.Quick(Alice, settings, "blue");
        return games.Quick(Bob, settings, "red");
    }

    private static PvpState PlayFirst(PvpGames games, string identity, PvpState room)
    {
        var state = games.State(identity, room.Code);
        return games.Move(identity, room.Code!, state.Revision, state.Board!.LegalActions[0].Id);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        private readonly List<ManualTimer> timers = [];
        public DateTimeOffset WallClock = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => WallClock;
        public void Advance(TimeSpan elapsed) { timestamp += elapsed.Ticks; WallClock += elapsed; }
        public void FireTimers() { foreach (var timer in timers) timer.Fire(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state); timers.Add(timer); return timer;
        }
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
