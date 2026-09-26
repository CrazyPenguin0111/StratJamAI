using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;

namespace StratJamAI.Web;

public sealed record ReviewPosition(int Ply, int MoveNumber, int Turn, int ActionsRemaining,
    double[] Scores, double[] Areas, EnclosurePoint[][] Nodes, EnclosureSegment[][] Segments,
    EnclosurePosition[][][] Territories);
public sealed record ReviewMove(int Number, int Player, BoardAction Played, BoardAction Suggested,
    int CompletedDepth, long Nodes, double ElapsedMilliseconds, double EstimateBefore,
    double EstimateAfterPlayed, double EstimateAfterSuggested, double AlternativeDifference,
    BoardAction[] PrincipalVariation);
public sealed record ReviewPlayerSummary(int Player, int ReviewedMoves, int MatchingSuggestions,
    double AveragePlayedChange, double AverageAlternativeDifference);
public sealed record ReviewState(string Status, int CompletedMoves, int TotalMoves, double BudgetMilliseconds,
    ReviewMove[] Moves, ReviewPlayerSummary[] Players, ReviewPosition[]? Positions, string? Error);
public sealed class GameReviewCapacityException() : InvalidOperationException(
    "The review queue is full. Please try again after an older review expires.");

/// <summary>
/// Bounded reviews of completed matches. The caller must authorize a participant and obtain the
/// finished room's history before every operation; timeout/resignation histories may be shorter than 120 moves.
/// </summary>
public sealed class GameReviews : IDisposable
{
    private sealed class Entry(string hash, int[] actions, ReviewPosition[] positions, DateTimeOffset touched)
    {
        public readonly string Hash = hash;
        public readonly int[] Actions = actions;
        public readonly ReviewPosition[] Positions = positions;
        public readonly List<ReviewMove> Moves = [];
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTimeOffset Touched = touched;
        public double Budget = 100;
        public string Status = "idle";
        public string? Error;
        public Job? Work;
        public Task Completion = Task.CompletedTask;
    }
    private sealed record Job(CancellationTokenSource Cancellation);

    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim searchSlots;
    private readonly Func<Enclosure, TimeSpan, CancellationToken, EnclosureSearchResult> search;
    private readonly int maxReviews;
    private readonly TimeSpan idleTimeout;
    private readonly TimeProvider clock;
    private readonly ITimer timer;
    private bool disposed;

    public GameReviews(SemaphoreSlim sharedSearchSlots, int maxReviews = 32, TimeSpan? idleTimeout = null,
        TimeProvider? timeProvider = null,
        Func<Enclosure, TimeSpan, CancellationToken, EnclosureSearchResult>? search = null)
    {
        ArgumentNullException.ThrowIfNull(sharedSearchSlots);
        if (maxReviews < 1) throw new ArgumentOutOfRangeException(nameof(maxReviews));
        this.idleTimeout = idleTimeout ?? TimeSpan.FromHours(1);
        if (this.idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        searchSlots = sharedSearchSlots;
        this.maxReviews = maxReviews;
        clock = timeProvider ?? TimeProvider.System;
        this.search = search ?? ((position, budget, cancellation) =>
            new EnclosureAlphaBetaBot(new(MoveMilliseconds: budget.TotalMilliseconds)).Search(position, budget, cancellation));
        timer = clock.CreateTimer(_ => Cleanup(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    public ReviewState Get(string gameKey, string finishedHistoryJson, bool includePositions = false)
    {
        var entry = GetEntry(gameKey, finishedHistoryJson);
        lock (sync) { ThrowIfDisposed(); return Snapshot(entry, includePositions); }
    }

    public ReviewState Start(string gameKey, string finishedHistoryJson, double budgetMilliseconds = 100)
    {
        if (!double.IsFinite(budgetMilliseconds) || budgetMilliseconds is < 50 or > 1000)
            throw new ArgumentException("Review thinking time must be between 50 and 1,000 milliseconds per placement.");
        var entry = GetEntry(gameKey, finishedHistoryJson);
        lock (sync)
        {
            ThrowIfDisposed();
            if (entry.Status is "running" or "completed") return Snapshot(entry, false);
            entry.Moves.Clear();
            entry.Error = null;
            entry.Budget = budgetMilliseconds;
            entry.Status = "running";
            var job = new Job(new());
            entry.Work = job;
            entry.Completion = Task.Run(() => Run(entry, job));
            return Snapshot(entry, false);
        }
    }

    public ReviewState Cancel(string gameKey)
    {
        Job? job;
        ReviewState result;
        lock (sync)
        {
            ThrowIfDisposed();
            if (!entries.TryGetValue(gameKey, out var entry)) throw new InvalidOperationException("Open the review before cancelling it.");
            entry.Touched = clock.GetUtcNow();
            job = entry.Work;
            if (job is not null) { entry.Work = null; entry.Status = "cancelled"; }
            result = Snapshot(entry, false);
        }
        CancelJob(job);
        return result;
    }

    public Task Completion(string gameKey)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            return entries.TryGetValue(gameKey, out var entry) ? entry.Completion : Task.CompletedTask;
        }
    }

    private Entry GetEntry(string key, string history)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A completed game is required.");
        if (history is null || history.Length > 64 * 1024) throw new ArgumentException("The game history is missing or too large.");
        Cleanup();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(history)));
        lock (sync)
        {
            ThrowIfDisposed();
            if (entries.TryGetValue(key, out var existing)) return Touch(existing, hash);
            if (entries.Count >= maxReviews) throw new GameReviewCapacityException();
        }
        var actions = EnclosureHistory.Parse(history).ActionIds();
        var game = new Enclosure();
        var positions = new ReviewPosition[actions.Length + 1];
        positions[0] = Position(game, 0);
        for (var i = 0; i < actions.Length; i++)
        {
            try { game.Play(actions[i]); }
            catch (ArgumentException exception) { throw new InvalidDataException($"Invalid history at move {i + 1}: {exception.Message}", exception); }
            positions[i + 1] = Position(game, i + 1);
        }
        lock (sync)
        {
            ThrowIfDisposed();
            if (entries.TryGetValue(key, out var existing)) return Touch(existing, hash);
            if (entries.Count >= maxReviews) throw new GameReviewCapacityException();
            var entry = new Entry(hash, actions, positions, clock.GetUtcNow());
            entries.Add(key, entry);
            return entry;
        }
    }

    private Entry Touch(Entry entry, string hash)
    {
        if (entry.Hash != hash) throw new InvalidOperationException("This completed game's history has changed; its review cannot be reused.");
        entry.Touched = clock.GetUtcNow();
        return entry;
    }

    private async Task Run(Entry entry, Job job)
    {
        var heldGate = false;
        try
        {
            await entry.Gate.WaitAsync(job.Cancellation.Token);
            heldGate = true;
            var game = new Enclosure();
            for (var i = 0; i < entry.Actions.Length; i++)
            {
                await searchSlots.WaitAsync(job.Cancellation.Token);
                ReviewMove row;
                try
                {
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    var player = game.Turn;
                    var started = Stopwatch.GetTimestamp();
                    var result = search(game.Copy(), TimeSpan.FromMilliseconds(entry.Budget), job.Cancellation.Token);
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    if (!game.IsLegal(result.Action)) throw new InvalidOperationException("The AI returned an illegal suggested move.");
                    var actual = game.Copy(); actual.Play(entry.Actions[i]);
                    var suggested = game.Copy(); suggested.Play(result.Action);
                    // Both alternatives use the same static evaluator, player and one-placement
                    // horizon. A time-limited search value is deliberately not called a move loss.
                    var before = EnclosureStrategyEvaluation.Evaluate(game, player);
                    var playedEstimate = EnclosureStrategyEvaluation.Evaluate(actual, player);
                    var suggestedEstimate = EnclosureStrategyEvaluation.Evaluate(suggested, player);
                    var variation = new List<BoardAction>();
                    var line = game.Copy();
                    if (result.PrincipalVariation is { Count: > 0 } pv && pv[0] == result.Action)
                        foreach (var action in pv)
                        {
                            if (line.Finished || !line.IsLegal(action)) break;
                            variation.Add(BoardAction.FromId(action)); line.Play(action);
                        }
                    if (variation.Count == 0) variation.Add(BoardAction.FromId(result.Action));
                    row = new(i + 1, player, BoardAction.FromId(entry.Actions[i]), BoardAction.FromId(result.Action),
                        result.CompletedDepth, result.Nodes, elapsed, before, playedEstimate, suggestedEstimate,
                        suggestedEstimate - playedEstimate, variation.ToArray());
                    game = actual;
                }
                finally { searchSlots.Release(); }
                lock (sync)
                {
                    if (disposed || entry.Work != job || job.Cancellation.IsCancellationRequested) return;
                    entry.Moves.Add(row);
                }
                // Yield between positions so interactive searches can claim a released CPU slot.
                await Task.Yield();
            }
            lock (sync)
                if (!disposed && entry.Work == job && !job.Cancellation.IsCancellationRequested)
                { entry.Work = null; entry.Status = "completed"; }
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (sync)
                if (!disposed && entry.Work == job)
                { entry.Error = "Review failed: " + exception.Message; entry.Work = null; entry.Status = "error"; }
        }
        finally
        {
            if (heldGate) entry.Gate.Release();
            job.Cancellation.Dispose();
        }
    }

    private static ReviewState Snapshot(Entry entry, bool includePositions)
    {
        var rows = entry.Moves.ToArray();
        var players = new ReviewPlayerSummary[2];
        for (var player = 0; player < 2; player++)
        {
            var own = rows.Where(row => row.Player == player).ToArray();
            players[player] = new(player, own.Length, own.Count(row => row.Played.Id == row.Suggested.Id),
                own.Length == 0 ? 0 : own.Average(row => row.EstimateAfterPlayed - row.EstimateBefore),
                own.Length == 0 ? 0 : own.Average(row => row.AlternativeDifference));
        }
        return new(entry.Status, rows.Length, entry.Actions.Length, entry.Budget, rows, players,
            includePositions ? entry.Positions.ToArray() : null, entry.Error);
    }

    private static ReviewPosition Position(Enclosure game, int ply) => new(ply, game.MoveNumber, game.Turn,
        game.ActionsRemaining, game.Scores.ToArray(), game.Areas.ToArray(), [game.Nodes(0), game.Nodes(1)],
        [game.Segments(0), game.Segments(1)], [game.Territories(0), game.Territories(1)]);

    private void Cleanup()
    {
        var jobs = new List<Job?>();
        lock (sync)
        {
            if (disposed) return;
            var now = clock.GetUtcNow();
            foreach (var pair in entries)
            {
                if (now - pair.Value.Touched < idleTimeout) continue;
                jobs.Add(pair.Value.Work); pair.Value.Work = null;
                entries.Remove(pair.Key);
            }
        }
        foreach (var job in jobs) CancelJob(job);
    }

    private static void CancelJob(Job? job)
    {
        try { job?.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* The worker finished between removal and cancellation. */ }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        Job?[] jobs;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            jobs = entries.Values.Select(entry => entry.Work).ToArray();
            entries.Clear();
        }
        timer.Dispose();
        foreach (var job in jobs) CancelJob(job);
        // The shared semaphore belongs to WebGameSessions, and cancelled workers release their own slots.
    }
}
