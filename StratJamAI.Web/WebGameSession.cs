using System.Diagnostics;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;

namespace StratJamAI.Web;

public sealed record BoardAction(int Id, EnclosurePoint? From, EnclosurePoint? To, bool Pass)
{
    public static BoardAction FromId(int action)
    {
        if (action == Enclosure.PassActionId) return new(action, null, null, true);
        var segment = Enclosure.DecodeAction(action);
        return new(action, segment.From, segment.To, false);
    }
}

public sealed record BoardMove(int Number, int Player, BoardAction Action);
public sealed record SearchDetails(int CompletedDepth, long Nodes, double ElapsedMilliseconds, double Value,
    BoardAction[] PrincipalVariation, long PositionRevision = 0);
public sealed record BoardState(long Revision, int HumanPlayer, double MoveMilliseconds, int Turn, int MoveNumber,
    int ActionsRemaining, bool Finished, double[] Scores, double[] Areas, EnclosurePoint[][] Nodes,
    EnclosureSegment[][] Segments, EnclosurePosition[][][] Territories, BoardAction[] LegalActions,
    BoardMove[] History, bool Thinking, bool HintThinking, bool CanUndo, SearchDetails? LastSearch,
    BoardAction? Hint, string? Error, bool AnalysisMode, bool LiveCoach, long? HintRevision);

/// <summary>Owns one local game; every mutation and search publication is revision-checked.</summary>
public sealed class WebGameSession : IDisposable
{
    private sealed record SearchJob(long Revision, bool Hint, CancellationTokenSource Cancellation);
    private readonly object sync = new();
    private readonly SemaphoreSlim searchGate = new(1, 1);
    private readonly SemaphoreSlim? sharedSearchSlots;
    private readonly Func<Enclosure, TimeSpan, CancellationToken, EnclosureSearchResult> search;
    private Enclosure game = new();
    private readonly List<BoardMove> history = [];
    private long revision = 1;
    private int humanPlayer;
    private double moveMilliseconds;
    private bool analysisMode;
    private bool liveCoach;
    private SearchJob? work;
    private Task completion = Task.CompletedTask;
    private SearchDetails? lastSearch;
    private BoardAction? hint;
    private string? error;
    private bool disposed;

    public WebGameSession(double moveMilliseconds = 1000,
        Func<Enclosure, TimeSpan, CancellationToken, EnclosureSearchResult>? search = null,
        SemaphoreSlim? sharedSearchSlots = null)
    {
        ValidateBudget(moveMilliseconds);
        this.moveMilliseconds = moveMilliseconds;
        this.sharedSearchSlots = sharedSearchSlots;
        this.search = search ?? ((state, budget, cancellation) =>
            new EnclosureAlphaBetaBot(new DepthSearchOptions(MoveMilliseconds: budget.TotalMilliseconds))
                .Search(state, budget, cancellation));
    }

    public BoardState State { get { lock (sync) return Snapshot(); } }
    public Task SearchCompletion { get { lock (sync) return completion; } }

    public BoardState NewGame(int player, double milliseconds, bool analysisMode = false, bool liveCoach = false)
    {
        ValidatePlayer(player);
        ValidateBudget(milliseconds);
        lock (sync)
        {
            ThrowIfDisposed();
            CancelSearch();
            game = new();
            humanPlayer = player;
            moveMilliseconds = milliseconds;
            this.analysisMode = analysisMode;
            this.liveCoach = liveCoach;
            history.Clear();
            lastSearch = null;
            hint = null;
            error = null;
            revision++;
            StartLiveCoach();
            return Snapshot();
        }
    }

    public BoardState Settings(double milliseconds, bool? liveCoach = null, bool? analysisMode = null)
    {
        ValidateBudget(milliseconds);
        lock (sync)
        {
            ThrowIfDisposed();
            if (moveMilliseconds == milliseconds && this.liveCoach == (liveCoach ?? this.liveCoach)
                && this.analysisMode == (analysisMode ?? this.analysisMode)) return Snapshot();
            CancelSearch();
            moveMilliseconds = milliseconds;
            this.liveCoach = liveCoach ?? this.liveCoach;
            this.analysisMode = analysisMode ?? this.analysisMode;
            ClearAnalysis();
            revision++;
            StartLiveCoach();
            return Snapshot();
        }
    }

    public BoardState Move(long expectedRevision, int action)
    {
        lock (sync)
        {
            RequireRevision(expectedRevision);
            if (game.Finished) throw new InvalidOperationException("The game has ended. Start a new game to play again.");
            if (!analysisMode && game.Turn != humanPlayer) throw new InvalidOperationException("Wait for the AI to finish its turn.");
            if (!game.IsLegal(action)) throw new ArgumentException("That line is not legal in this position.");
            CancelSearch();
            ApplyMove(action);
            StartLiveCoach();
            return Snapshot();
        }
    }

    public BoardState Undo(long expectedRevision)
    {
        lock (sync)
        {
            RequireRevision(expectedRevision);
            var beforeHumanMove = analysisMode ? history.Count - 1 : history.FindLastIndex(move => move.Player == humanPlayer);
            if (beforeHumanMove < 0) throw new InvalidOperationException("There is no move to undo yet.");
            CancelSearch();
            history.RemoveRange(beforeHumanMove, history.Count - beforeHumanMove);
            game = EnclosureHistory.FromActions(history.Select(move => move.Action.Id)).Replay();
            hint = null;
            lastSearch = null;
            error = null;
            revision++;
            StartLiveCoach();
            return Snapshot();
        }
    }

    public BoardState Import(string json)
    {
        var imported = EnclosureHistory.Parse(json);
        // Validate fully before replacing a live game or cancelling its search.
        var restored = imported.Replay();
        var playback = new Enclosure();
        var moves = new List<BoardMove>();
        foreach (var action in imported.ActionIds())
        {
            moves.Add(new(moves.Count + 1, playback.Turn, BoardAction.FromId(action)));
            playback.Play(action);
        }
        lock (sync)
        {
            ThrowIfDisposed();
            CancelSearch();
            game = restored;
            history.Clear();
            history.AddRange(moves);
            lastSearch = null;
            hint = null;
            error = null;
            revision++;
            StartLiveCoach();
            return Snapshot();
        }
    }

    /// <summary>Replays a complete external history; client scores, protection and turn are never trusted.</summary>
    public BoardState Synchronize(long expectedRevision, int[]? actions, string? jsonHistory = null)
    {
        if ((actions is null) == (jsonHistory is null))
            throw new ArgumentException("Supply either an action list or a game history, but not both.");
        if (jsonHistory is not null) actions = EnclosureHistory.Parse(jsonHistory).ActionIds();
        if (actions!.Length > 120)
            throw new ArgumentException("A game history must contain at most 120 moves.");
        // Own the input while validating; callers cannot mutate the published move list.
        actions = (int[])actions.Clone();
        lock (sync)
        {
            RequireRevision(expectedRevision);
            if (analysisMode && liveCoach && history.Select(move => move.Action.Id).SequenceEqual(actions))
                return Snapshot();
        }
        var candidate = new Enclosure();
        var moves = new List<BoardMove>(actions.Length);
        for (var i = 0; i < actions.Length; i++)
        {
            try
            {
                var player = candidate.Turn;
                candidate.Play(actions[i]);
                moves.Add(new(i + 1, player, BoardAction.FromId(actions[i])));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw new InvalidDataException($"Invalid history at move {i + 1}: {exception.Message}", exception);
            }
        }
        lock (sync)
        {
            // A user move made during replay wins; never overwrite it with an older snapshot.
            RequireRevision(expectedRevision);
            CancelSearch();
            game = candidate;
            history.Clear();
            history.AddRange(moves);
            analysisMode = true;
            liveCoach = true;
            ClearAnalysis();
            revision++;
            StartLiveCoach();
            return Snapshot();
        }
    }

    public string Export()
    {
        lock (sync) return EnclosureHistory.FromActions(history.Select(move => move.Action.Id)).ToJson();
    }

    public BoardState StartSearch(long expectedRevision, bool isHint = false)
    {
        lock (sync)
        {
            RequireRevision(expectedRevision);
            if (game.Finished) throw new InvalidOperationException("The game has ended.");
            if (isHint && !analysisMode && game.Turn != humanPlayer) throw new InvalidOperationException("Hints are available on your turn.");
            if (!isHint && analysisMode) throw new InvalidOperationException("Analysis mode suggests moves without playing them.");
            if (!isHint && game.Turn == humanPlayer) throw new InvalidOperationException("It is your turn.");
            if (work is not null) return Snapshot();
            QueueSearch(isHint);
            return Snapshot();
        }
    }

    // These helpers are called with sync held. There is at most one active/queued job per session;
    // a superseded evaluator must relinquish searchGate before its replacement can run.
    private void StartLiveCoach()
    {
        if (liveCoach && !game.Finished && work is null && (analysisMode || game.Turn == humanPlayer))
            QueueSearch(isHint: true);
    }

    private void QueueSearch(bool isHint)
    {
        var job = new SearchJob(revision, isHint, new CancellationTokenSource());
        work = job;
        ClearAnalysis();
        var position = game.Copy();
        var budget = TimeSpan.FromMilliseconds(moveMilliseconds);
        completion = Task.Run(() => RunSearch(job, position, budget));
    }

    private void ClearAnalysis()
    {
        hint = null;
        lastSearch = null;
        error = null;
    }

    private async Task RunSearch(SearchJob job, Enclosure position, TimeSpan budget)
    {
        var acquired = false;
        var sharedSlotAcquired = false;
        try
        {
            // A cancelled, slow evaluator cannot overlap the next game's CPU search.
            await searchGate.WaitAsync(job.Cancellation.Token);
            acquired = true;
            if (sharedSearchSlots is not null)
            {
                await sharedSearchSlots.WaitAsync(job.Cancellation.Token);
                sharedSlotAcquired = true;
            }
            job.Cancellation.Token.ThrowIfCancellationRequested();
            // Waiting for a shared CPU slot does not consume the visitor's thinking time.
            // Search and prepare the entire turn off-board; cancellation publishes no partial turn.
            var prepared = position.Copy();
            var player = prepared.Turn;
            var started = Stopwatch.GetTimestamp();
            var result = search(position, budget, job.Cancellation.Token);
            job.Cancellation.Token.ThrowIfCancellationRequested();
            if (!prepared.IsLegal(result.Action)) throw new InvalidOperationException("The AI returned an illegal move.");
            var variation = LegalVariation(prepared, result);
            var actions = new List<int>(2);
            var nodes = result.Nodes;
            var value = result.Value;
            if (!job.Hint)
            {
                actions.Add(result.Action);
                prepared.Play(result.Action);
                while (!prepared.Finished && prepared.Turn == player)
                {
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    int next;
                    if (actions.Count < variation.Count && prepared.IsLegal(variation[actions.Count]))
                        next = variation[actions.Count];
                    else
                    {
                        var remaining = budget - Stopwatch.GetElapsedTime(started);
                        if (remaining > TimeSpan.Zero)
                        {
                            var continuation = search(prepared.Copy(), remaining, job.Cancellation.Token);
                            job.Cancellation.Token.ThrowIfCancellationRequested();
                            if (!prepared.IsLegal(continuation.Action))
                                throw new InvalidOperationException("The AI returned an illegal continuation.");
                            next = continuation.Action;
                            nodes += continuation.Nodes;
                            variation = [.. actions, .. LegalVariation(prepared, continuation)];
                        }
                        else
                        {
                            // A shallow completed search may not contain a second placement.
                            // Do not start another full search after the turn deadline.
                            next = prepared.GenerateLegalActions()[0];
                            variation = [.. actions, next];
                        }
                    }
                    actions.Add(next);
                    prepared.Play(next);
                }
            }
            var details = new SearchDetails(result.CompletedDepth, nodes,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, value,
                variation.Select(BoardAction.FromId).ToArray(), job.Revision);
            lock (sync)
            {
                if (disposed || work != job || revision != job.Revision || job.Cancellation.IsCancellationRequested) return;
                if (job.Hint) hint = BoardAction.FromId(result.Action);
                else
                {
                    var moves = actions.Select((action, index) =>
                        new BoardMove(history.Count + index + 1, player, BoardAction.FromId(action))).ToArray();
                    game = prepared;
                    history.AddRange(moves);
                    revision += moves.Length;
                    ClearAnalysis();
                }
                lastSearch = details;
                work = null;
                if (!job.Hint) StartLiveCoach();
            }
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (sync)
                if (work == job && revision == job.Revision && !disposed)
                {
                    error = "AI search failed: " + exception.Message;
                    work = null;
                }
        }
        finally
        {
            if (sharedSlotAcquired) sharedSearchSlots!.Release();
            if (acquired) searchGate.Release();
            lock (sync)
                if (work == job) work = null;
            job.Cancellation.Dispose();
        }
    }

    private static List<int> LegalVariation(Enclosure position, EnclosureSearchResult result)
    {
        var actions = new List<int> { result.Action };
        var replay = position.Copy();
        replay.Play(result.Action);
        if (result.PrincipalVariation is not { Count: > 0 } variation || variation[0] != result.Action)
            return actions;
        foreach (var action in variation.Skip(1))
        {
            if (replay.Finished || !replay.IsLegal(action)) break;
            actions.Add(action);
            replay.Play(action);
        }
        return actions;
    }

    private void ApplyMove(int action)
    {
        var player = game.Turn;
        game.Play(action);
        history.Add(new(history.Count + 1, player, BoardAction.FromId(action)));
        ClearAnalysis();
        revision++;
    }

    private BoardState Snapshot() => new(revision, humanPlayer, moveMilliseconds, game.Turn, game.MoveNumber,
        game.ActionsRemaining, game.Finished, game.Scores.ToArray(), game.Areas.ToArray(),
        [game.Nodes(0), game.Nodes(1)], [game.Segments(0), game.Segments(1)],
        [game.Territories(0), game.Territories(1)], game.GenerateLegalActions().Select(BoardAction.FromId).ToArray(),
        history.ToArray(), work is not null, work?.Hint ?? false,
        analysisMode ? history.Count > 0 : history.Any(move => move.Player == humanPlayer),
        lastSearch, hint, error, analysisMode, liveCoach, hint is null ? null : revision);

    private void CancelSearch()
    {
        work?.Cancellation.Cancel();
        work = null;
    }

    private void RequireRevision(long expected)
    {
        ThrowIfDisposed();
        if (revision != expected) throw new InvalidOperationException("The board changed. Your view has been refreshed; try again.");
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private static void ValidatePlayer(int player)
    {
        if (player is not (0 or 1)) throw new ArgumentException("Choose Blue (0) or Red (1).");
    }
    private static void ValidateBudget(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 50 || milliseconds > 20000)
            throw new ArgumentException("Thinking time must be between 50 and 20,000 milliseconds.");
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            CancelSearch();
        }
    }
}
