using System.Security.Cryptography;

namespace StratJamAI.Web;

public sealed class SessionCapacityException() : InvalidOperationException(
    "The server has reached its game-session capacity. Try again after inactive games expire.");

/// <summary>A request keeps its game alive until its lease is released.</summary>
public sealed class GameSessionLease : IDisposable
{
    private Action? release;
    public string Id { get; }
    public WebGameSession Session { get; }
    public bool IsNew { get; }

    internal GameSessionLease(string id, WebGameSession session, bool isNew, Action release)
    {
        Id = id;
        Session = session;
        IsNew = isNew;
        this.release = release;
    }

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

/// <summary>Bounded, idle-expiring browser games with a shared CPU search limit.</summary>
public sealed class WebGameSessions : IDisposable
{
    private sealed class Entry(string id, WebGameSession session, DateTimeOffset touched)
    {
        public readonly string Id = id;
        public readonly WebGameSession Session = session;
        public DateTimeOffset Touched = touched;
        public int Leases;
    }

    private readonly object sync = new();
    private readonly Dictionary<string, Entry> sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim searchSlots;
    private readonly double moveMilliseconds;
    private readonly int maxSessions;
    private readonly TimeSpan idleTimeout;
    private readonly TimeProvider timeProvider;
    private readonly ITimer cleanupTimer;
    private bool disposed;

    // Opponents, hints, and post-game reviews all share the same CPU allowance.
    internal SemaphoreSlim SearchSlots => searchSlots;

    public WebGameSessions(double moveMilliseconds = 1000, int maxSessions = 128, int maxSearches = 4,
        TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null)
    {
        if (!double.IsFinite(moveMilliseconds) || moveMilliseconds is < 50 or > 20000)
            throw new ArgumentException("Thinking time must be between 50 and 20,000 milliseconds.", nameof(moveMilliseconds));
        if (maxSessions < 1) throw new ArgumentOutOfRangeException(nameof(maxSessions));
        if (maxSearches < 1) throw new ArgumentOutOfRangeException(nameof(maxSearches));
        this.idleTimeout = idleTimeout ?? TimeSpan.FromHours(2);
        if (this.idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        this.moveMilliseconds = moveMilliseconds;
        this.maxSessions = maxSessions;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        searchSlots = new(maxSearches, maxSearches);
        cleanupTimer = this.timeProvider.CreateTimer(_ => Cleanup(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    public GameSessionLease Acquire(string? cookieId)
    {
        var expired = new List<WebGameSession>();
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var now = timeProvider.GetUtcNow();
                RemoveExpired(now, expired);
                var isNew = false;
                Entry? entry = null;
                if (cookieId is { Length: 64 }) sessions.TryGetValue(cookieId, out entry);
                if (entry is null)
                {
                    if (sessions.Count >= maxSessions) throw new SessionCapacityException();
                    string id;
                    do { id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); }
                    while (sessions.ContainsKey(id));
                    entry = new(id, new WebGameSession(moveMilliseconds, sharedSearchSlots: searchSlots), now);
                    sessions.Add(id, entry);
                    isNew = true;
                }
                entry.Touched = now;
                entry.Leases++;
                return new(entry.Id, entry.Session, isNew, () => Release(entry));
            }
        }
        finally
        {
            // Cancellation can run user callbacks; never cancel while holding the pool lock.
            foreach (var session in expired) session.Dispose();
        }
    }

    private void Release(Entry entry)
    {
        lock (sync)
        {
            entry.Leases--;
            entry.Touched = timeProvider.GetUtcNow();
        }
    }

    private void RemoveExpired(DateTimeOffset now, List<WebGameSession> removed)
    {
        foreach (var entry in sessions.Values)
        {
            if (entry.Leases != 0 || now - entry.Touched < idleTimeout) continue;
            sessions.Remove(entry.Id);
            removed.Add(entry.Session);
        }
    }

    private void Cleanup()
    {
        var expired = new List<WebGameSession>();
        lock (sync)
        {
            if (disposed) return;
            RemoveExpired(timeProvider.GetUtcNow(), expired);
        }
        foreach (var session in expired) session.Dispose();
    }

    public void Dispose()
    {
        WebGameSession[] removed;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            removed = sessions.Values.Select(entry => entry.Session).ToArray();
            sessions.Clear();
        }
        cleanupTimer.Dispose();
        foreach (var session in removed) session.Dispose();
        // Queued/running searches release this semaphore after cancellation. No OS wait handle
        // is created, so let it be collected with those jobs rather than disposing it too early.
    }
}
