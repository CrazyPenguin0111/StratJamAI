using System.Security.Cryptography;
using StratJamAI.Core.Games;

namespace StratJamAI.Web;

public sealed record PvpSettings(int InitialSeconds = 120, int IncrementSeconds = 15);
public sealed record PvpPlayer(int Seat, string DisplayName, string ColorPreference, int? Color,
    bool Ready, bool IsYou, bool IsHost);
public sealed record PvpBoard(int Turn, int MoveNumber, int ActionsRemaining, bool Finished,
    double[] Scores, double[] Areas, EnclosurePoint[][] Nodes, EnclosureSegment[][] Segments,
    EnclosurePosition[][][] Territories, BoardAction[] LegalActions, BoardMove[] History);
public sealed record PvpState(string? Code, long Revision, string Status, PvpSettings Settings,
    PvpPlayer[] Players, int? ViewerColor, PvpBoard? Board, double[] RemainingMilliseconds,
    DateTimeOffset ServerTimeUtc, int? Winner, string? ResultReason);
public sealed class PvpCapacityException() : InvalidOperationException(
    "All multiplayer rooms are currently occupied. Please try again shortly.");

/// <summary>Authoritative human games. Membership, moves and monotonic clocks share one lock.</summary>
public sealed class PvpGames : IDisposable
{
    private sealed class Member(string identity, int seat, string preference, string name)
    {
        public readonly string Identity = identity;
        public readonly int Seat = seat;
        public string Preference = preference;
        public string Name = name;
        public bool Ready;
        public int? Color;
    }

    private sealed class Room(string code, string host, PvpSettings settings, bool quick, long now)
    {
        public readonly string Code = code;
        public string Host = host;
        public PvpSettings Settings = settings;
        public readonly bool Quick = quick;
        public readonly List<Member> Members = [];
        public string Status = quick ? "queued" : "waiting";
        public long Revision = 1;
        public long Touched = now;
        public long ClockTimestamp = now;
        public long FinishedTimestamp;
        public readonly double[] Remaining = new double[2];
        public Enclosure? Game;
        public readonly List<BoardMove> History = [];
        public int? Winner;
        public string? ResultReason;
    }

    private readonly object sync = new();
    private readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> currentByIdentity = new(StringComparer.Ordinal);
    private readonly int maxRooms;
    private readonly TimeProvider clock;
    private readonly TimeSpan queueIdleTimeout, waitingIdleTimeout, finishedRetention;
    private readonly ITimer timer;
    private bool disposed;

    public PvpGames(int maxRooms = 128, TimeProvider? timeProvider = null,
        TimeSpan? queueIdleTimeout = null, TimeSpan? waitingIdleTimeout = null, TimeSpan? finishedRetention = null)
    {
        if (maxRooms < 1) throw new ArgumentOutOfRangeException(nameof(maxRooms));
        this.maxRooms = maxRooms;
        clock = timeProvider ?? TimeProvider.System;
        this.queueIdleTimeout = queueIdleTimeout ?? TimeSpan.FromSeconds(90);
        this.waitingIdleTimeout = waitingIdleTimeout ?? TimeSpan.FromHours(2);
        this.finishedRetention = finishedRetention ?? TimeSpan.FromHours(2);
        if (this.queueIdleTimeout <= TimeSpan.Zero || this.waitingIdleTimeout <= TimeSpan.Zero || this.finishedRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(queueIdleTimeout), "Room lifetimes must be positive.");
        timer = clock.CreateTimer(_ => Cleanup(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public PvpState Create(string identity, PvpSettings? settings = null,
        string colorPreference = "random", string? displayName = null)
    {
        ValidateIdentity(identity);
        settings ??= new(); ValidateSettings(settings);
        var preference = Preference(colorPreference); var name = Name(displayName);
        lock (sync)
        {
            var now = Begin();
            EnsureAvailable(identity);
            EnsureCapacity();
            var room = NewRoom(identity, settings, false, now);
            room.Members.Add(new(identity, 0, preference, name ?? "Player 1"));
            return Snapshot(room, identity, now);
        }
    }

    public PvpState Quick(string identity, PvpSettings? settings = null,
        string colorPreference = "random", string? displayName = null)
    {
        ValidateIdentity(identity);
        settings ??= new(); ValidateSettings(settings);
        var preference = Preference(colorPreference); var name = Name(displayName);
        lock (sync)
        {
            var now = Begin();
            if (Current(identity) is { Status: "queued" } queued)
            {
                queued.Touched = now;
                return Snapshot(queued, identity, now);
            }
            EnsureAvailable(identity);
            var match = rooms.Values.FirstOrDefault(room => room.Status == "queued" && room.Settings == settings &&
                Compatible(room.Members[0].Preference, preference));
            if (match is not null)
            {
                match.Members.Add(new(identity, 1, preference, name ?? "Player 2") { Ready = true });
                currentByIdentity[identity] = match.Code;
                match.Revision++;
                Start(match, now);
                return Snapshot(match, identity, now);
            }
            EnsureCapacity();
            var room = NewRoom(identity, settings, true, now);
            room.Members.Add(new(identity, 0, preference, name ?? "Player 1") { Ready = true });
            return Snapshot(room, identity, now);
        }
    }

    public PvpState Join(string identity, string code, string colorPreference = "random", string? displayName = null)
    {
        ValidateIdentity(identity);
        var preference = Preference(colorPreference); var name = Name(displayName);
        lock (sync)
        {
            var now = Begin();
            var room = Find(code);
            if (room.Members.Any(member => member.Identity == identity))
            {
                room.Touched = now;
                return Snapshot(room, identity, now);
            }
            EnsureAvailable(identity);
            if (room.Status != "waiting" || room.Members.Count != 1)
                throw new InvalidOperationException("This room is not accepting another player.");
            if (!Compatible(room.Members[0].Preference, preference))
                throw new ArgumentException("Both players cannot request the same fixed color.");
            var seat = room.Members[0].Seat == 0 ? 1 : 0;
            room.Members.Add(new(identity, seat, preference, name ?? $"Player {seat + 1}"));
            room.Touched = now;
            room.Revision++;
            currentByIdentity[identity] = room.Code;
            return Snapshot(room, identity, now);
        }
    }

    public PvpState State(string identity, string? code = null)
    {
        ValidateIdentity(identity);
        lock (sync)
        {
            var now = Begin();
            var room = code is null ? Current(identity) : Authorized(identity, code);
            if (room is null) return Idle();
            room.Touched = now;
            return Snapshot(room, identity, now);
        }
    }

    public PvpState Settings(string identity, string code, long revision, PvpSettings? settings = null,
        string? colorPreference = null, string? displayName = null)
    {
        ValidateIdentity(identity);
        if (settings is not null) ValidateSettings(settings);
        var preference = colorPreference is null ? null : Preference(colorPreference);
        var name = displayName is null ? null : Name(displayName);
        lock (sync)
        {
            var now = Begin(); var room = Authorized(identity, code);
            RequireRevision(room, revision);
            if (room.Status != "waiting") throw new InvalidOperationException("Settings can only change in a private lobby before the game starts.");
            if (settings is not null && settings != room.Settings && room.Host != identity)
                throw new UnauthorizedAccessException("Only the host can change the room clock.");
            var member = room.Members.Single(player => player.Identity == identity);
            if (preference is not null && room.Members.Any(other => other != member && !Compatible(other.Preference, preference)))
                throw new ArgumentException("Both players cannot request the same fixed color.");
            var rulesChanged = settings is not null && settings != room.Settings || preference is not null && preference != member.Preference;
            if (settings is not null) room.Settings = settings;
            if (preference is not null) member.Preference = preference;
            if (name is not null) member.Name = name;
            if (rulesChanged) foreach (var player in room.Members) player.Ready = false;
            room.Touched = now; room.Revision++;
            return Snapshot(room, identity, now);
        }
    }

    public PvpState Ready(string identity, string code, long revision, bool ready = true)
    {
        ValidateIdentity(identity);
        lock (sync)
        {
            var now = Begin(); var room = Authorized(identity, code);
            RequireRevision(room, revision);
            if (room.Status != "waiting") throw new InvalidOperationException("Ready is only available in a private lobby.");
            room.Members.Single(member => member.Identity == identity).Ready = ready;
            room.Touched = now; room.Revision++;
            if (room.Members.Count == 2 && room.Members.All(member => member.Ready)) Start(room, now);
            return Snapshot(room, identity, now);
        }
    }

    public PvpState Move(string identity, string code, long revision, int action)
    {
        ValidateIdentity(identity);
        lock (sync)
        {
            var now = Begin(); var room = Authorized(identity, code);
            RequireActive(room); RequireRevision(room, revision);
            var member = room.Members.Single(player => player.Identity == identity);
            var game = room.Game!;
            if (member.Color != game.Turn) throw new InvalidOperationException("It is the other player's turn.");
            if (!game.IsLegal(action)) throw new ArgumentException("That line is not legal in this position.");
            var actor = game.Turn;
            game.Play(action);
            room.History.Add(new(room.History.Count + 1, actor, BoardAction.FromId(action)));
            room.Revision++; room.Touched = now;
            if (game.Turn != actor || game.Finished)
                room.Remaining[actor] += room.Settings.IncrementSeconds * 1000d;
            room.ClockTimestamp = now;
            if (game.Finished)
            {
                var comparison = Math.Sign(game.Scores[0] - game.Scores[1]);
                Finish(room, comparison == 0 ? null : comparison > 0 ? 0 : 1, "score", now);
            }
            return Snapshot(room, identity, now);
        }
    }

    public PvpState Resign(string identity, string code, long revision)
    {
        ValidateIdentity(identity);
        lock (sync)
        {
            var now = Begin(); var room = Authorized(identity, code);
            RequireActive(room); RequireRevision(room, revision);
            var color = room.Members.Single(member => member.Identity == identity).Color!.Value;
            Finish(room, 1 - color, "resignation", now);
            return Snapshot(room, identity, now);
        }
    }

    public PvpState Leave(string identity, string code, long revision)
    {
        ValidateIdentity(identity);
        lock (sync)
        {
            var now = Begin(); var room = Authorized(identity, code); RequireRevision(room, revision);
            if (room.Status == "active") throw new InvalidOperationException("Resign explicitly before leaving an active game.");
            if (currentByIdentity.GetValueOrDefault(identity) == room.Code) currentByIdentity.Remove(identity);
            if (room.Status != "finished")
            {
                room.Members.RemoveAll(member => member.Identity == identity);
                if (room.Members.Count == 0) Remove(room);
                else
                {
                    if (room.Host == identity) room.Host = room.Members[0].Identity;
                    foreach (var member in room.Members) member.Ready = false;
                    room.Touched = now; room.Revision++;
                }
            }
            return Idle();
        }
    }

    public string ExportHistory(string identity, string code) => FinishedHistory(identity, code).ToJson();

    public EnclosureHistory FinishedHistory(string identity, string code)
    {
        ValidateIdentity(identity);
        lock (sync)
        {
            Begin(); var room = Authorized(identity, code);
            if (room.Status != "finished") throw new InvalidOperationException("History export and review are available after the game finishes.");
            return EnclosureHistory.FromActions(room.History.Select(move => move.Action.Id));
        }
    }

    private Room NewRoom(string identity, PvpSettings settings, bool quick, long now)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        string code;
        Span<char> characters = stackalloc char[6];
        do
        {
            for (var i = 0; i < characters.Length; i++) characters[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            code = new(characters);
        } while (rooms.ContainsKey(code));
        var room = new Room(code, identity, settings, quick, now);
        rooms.Add(code, room); currentByIdentity[identity] = code;
        return room;
    }

    private void Start(Room room, long now)
    {
        var first = room.Members[0]; var second = room.Members[1];
        first.Color = first.Preference == "blue" ? 0 : first.Preference == "red" ? 1 :
            second.Preference == "blue" ? 1 : second.Preference == "red" ? 0 : RandomNumberGenerator.GetInt32(2);
        second.Color = 1 - first.Color;
        room.Game = new(); room.Status = "active"; room.Touched = now; room.ClockTimestamp = now;
        room.Remaining[0] = room.Remaining[1] = room.Settings.InitialSeconds * 1000d;
    }

    private void UpdateClock(Room room, long now)
    {
        if (room.Status != "active") return;
        var actor = room.Game!.Turn;
        var elapsed = Math.Max(0, clock.GetElapsedTime(room.ClockTimestamp, now).TotalMilliseconds);
        room.Remaining[actor] = Math.Max(0, room.Remaining[actor] - elapsed);
        room.ClockTimestamp = now;
        if (room.Remaining[actor] <= 0) Finish(room, 1 - actor, "timeout", now);
    }

    private static void Finish(Room room, int? winner, string reason, long now)
    {
        room.Status = "finished"; room.Winner = winner; room.ResultReason = reason;
        room.FinishedTimestamp = now; room.Revision++;
    }

    private long Begin()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var now = clock.GetTimestamp();
        CleanupLocked(now);
        return now;
    }

    private void CleanupLocked(long now)
    {
        foreach (var room in rooms.Values.ToArray())
        {
            UpdateClock(room, now);
            var expired = room.Status switch
            {
                "queued" => clock.GetElapsedTime(room.Touched, now) >= queueIdleTimeout,
                "waiting" => clock.GetElapsedTime(room.Touched, now) >= waitingIdleTimeout,
                "finished" => clock.GetElapsedTime(room.FinishedTimestamp, now) >= finishedRetention,
                _ => false
            };
            if (expired) Remove(room);
        }
    }

    private void EnsureCapacity()
    {
        if (rooms.Count < maxRooms) return;
        var finished = rooms.Values.Where(room => room.Status == "finished").MinBy(room => room.FinishedTimestamp);
        if (finished is not null) Remove(finished);
        if (rooms.Count >= maxRooms) throw new PvpCapacityException();
    }

    private void EnsureAvailable(string identity)
    {
        if (Current(identity) is { Status: not "finished" })
            throw new InvalidOperationException("Leave your current lobby or finish your active game before joining another.");
    }

    private Room? Current(string identity) => currentByIdentity.TryGetValue(identity, out var code) ? rooms.GetValueOrDefault(code) : null;
    private Room Find(string code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        if (normalized is not { Length: 6 }) throw new ArgumentException("Enter the six-character room code.");
        return rooms.TryGetValue(normalized, out var room) ? room : throw new KeyNotFoundException("This room does not exist or has expired.");
    }
    private Room Authorized(string identity, string code)
    {
        var room = Find(code);
        if (!room.Members.Any(member => member.Identity == identity))
            throw new UnauthorizedAccessException("Only the players in this room can view or change it.");
        return room;
    }
    private static void RequireRevision(Room room, long revision)
    {
        if (room.Revision != revision) throw new InvalidOperationException("The room changed. Refresh it and try again.");
    }
    private static void RequireActive(Room room)
    {
        if (room.Status != "active") throw new InvalidOperationException("This game is not active.");
    }
    private void Remove(Room room)
    {
        rooms.Remove(room.Code);
        foreach (var member in room.Members)
            if (currentByIdentity.GetValueOrDefault(member.Identity) == room.Code) currentByIdentity.Remove(member.Identity);
    }

    private PvpState Snapshot(Room room, string identity, long now)
    {
        UpdateClock(room, now);
        var viewer = room.Members.Single(member => member.Identity == identity);
        var game = room.Game;
        PvpBoard? board = game is null ? null : new(game.Turn, game.MoveNumber, game.ActionsRemaining,
            room.Status == "finished", game.Scores.ToArray(), game.Areas.ToArray(),
            [game.Nodes(0), game.Nodes(1)], [game.Segments(0), game.Segments(1)],
            [game.Territories(0), game.Territories(1)],
            room.Status == "active" && viewer.Color == game.Turn ? game.GenerateLegalActions().Select(BoardAction.FromId).ToArray() : [],
            room.History.ToArray());
        return new(room.Code, room.Revision, room.Status, room.Settings,
            room.Members.OrderBy(member => member.Seat).Select(member => new PvpPlayer(member.Seat, member.Name,
                member.Preference, member.Color, member.Ready, member.Identity == identity, member.Identity == room.Host)).ToArray(),
            viewer.Color, board, room.Remaining.ToArray(), clock.GetUtcNow(), room.Winner, room.ResultReason);
    }

    private PvpState Idle() => new(null, 0, "idle", new(), [], null, null, [0, 0], clock.GetUtcNow(), null, null);
    private static bool Compatible(string first, string second) => first == "random" || second == "random" || first != second;
    private static string Preference(string preference) => preference?.Trim().ToLowerInvariant() switch
    {
        "blue" => "blue", "red" => "red", "random" => "random",
        _ => throw new ArgumentException("Choose random, blue or red for the color preference.")
    };
    private static string? Name(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (name.Length > 20 || name.Any(char.IsControl)) throw new ArgumentException("Display names must contain at most 20 visible characters.");
        return name;
    }
    private static void ValidateIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 256) throw new ArgumentException("A valid player session is required.");
    }
    private static void ValidateSettings(PvpSettings settings)
    {
        if (settings.InitialSeconds is < 10 or > 3600 || settings.IncrementSeconds is < 0 or > 120)
            throw new ArgumentException("Choose 10–3600 initial seconds and 0–120 increment seconds.");
    }
    private void Cleanup()
    {
        lock (sync)
            if (!disposed) CleanupLocked(clock.GetTimestamp());
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; rooms.Clear(); currentByIdentity.Clear();
        }
        timer.Dispose();
    }
}
