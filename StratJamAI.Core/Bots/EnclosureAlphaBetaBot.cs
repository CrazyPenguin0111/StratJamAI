using System.Buffers;
using System.Diagnostics;
using StratJamAI.Core.Games;

namespace StratJamAI.Core.Bots;

public sealed record DepthSearchOptions(double MoveMilliseconds = 1000, int MaxDepth = 120)
{
    /// <summary>Additional legal captures considered at a nominal search horizon.</summary>
    public int CaptureQuiescencePlies { get; init; } = 2;
    public int CaptureQuiescenceWidth { get; init; } = 8;
    public double MinimumTacticalMilliseconds { get; init; } = 250;

    public void Validate()
    {
        if (!double.IsFinite(MoveMilliseconds) || MoveMilliseconds <= 0 || MaxDepth is < 1 or > Enclosure.LineLimit || CaptureQuiescencePlies is < 0 or > 4 ||
            CaptureQuiescenceWidth is < 1 or > Enclosure.SegmentCount ||
            !double.IsFinite(MinimumTacticalMilliseconds) || MinimumTacticalMilliseconds < 0)
            throw new ArgumentException("Depth search needs a positive finite budget, depth 1–120, capture depth 0–4, capture width 1–7140, and a finite nonnegative tactical budget threshold.");
    }
}

public sealed record EnclosureSearchResult(int Action, int CompletedDepth, long Nodes, double ElapsedMilliseconds,
    double Value, IReadOnlyList<int> PrincipalVariation)
{
    public int CaptureQuiescencePlies { get; init; }
}

/// <summary>Time-bounded minimax. Depth counts placements/passes, including consecutive actions by one player.</summary>
public sealed class EnclosureAlphaBetaBot : IBot
{
    private readonly DepthSearchOptions options;
    public string Name => "alpha-beta";

    public EnclosureAlphaBetaBot(DepthSearchOptions? options = null)
    {
        this.options = options ?? new();
        this.options.Validate();
    }

    public int ChooseAction(DecisionRequest decision, BotContext context)
    {
        if (context.SearchState is not Enclosure game)
            throw new NotSupportedException("Enclosure depth search requires an Enclosure simulation state.");
        if (game.Finished || game.Turn != decision.Player || decision.Actions.Length == 0)
            throw new InvalidDataException("The decision does not match the search state.");
        if (context.Deadline.Expired) return decision.Actions[0].Id;
        var result = Search(game, context.Deadline);
        if (!decision.Actions.Any(action => action.Id == result.Action))
            throw new InvalidDataException("The decision's legal actions do not match the search state.");
        return result.Action;
    }

    public EnclosureSearchResult Search(Enclosure state, TimeSpan budget, CancellationToken cancellationToken = default) =>
        Search(state, Deadline.After(budget), cancellationToken);

    public EnclosureSearchResult Search(Enclosure state, Deadline deadline, CancellationToken cancellationToken = default)
    {
        if (state.Finished) throw new ArgumentException("Cannot choose a move in a finished game.", nameof(state));
        var effectiveDeadline = deadline.Limit(TimeSpan.FromMilliseconds(options.MoveMilliseconds));
        // Short UI budgets need ordinary completed iterations more than selective extra
        // captures. Decide once per search so transposition bounds retain one leaf regime.
        var captureDepth = effectiveDeadline.RemainingSeconds * 1000 >= options.MinimumTacticalMilliseconds
            ? options.CaptureQuiescencePlies : 0;
        using var search = new SearchSession(state, effectiveDeadline,
            options.MaxDepth, captureDepth, options.CaptureQuiescenceWidth, cancellationToken);
        return search.Run();
    }

    private enum Bound : byte { Empty, Exact, Lower, Upper }
    private struct Entry
    {
        public ulong Low, High;
        public double Value;
        public int Depth, Action;
        public Bound Bound;
    }
    private readonly record struct OrderedAction(int Id, int Priority, byte Classification);
    private sealed class SearchStoppedException : Exception;

    // All mutable buffers belong to a call, so a bot may safely serve concurrent games.
    private sealed class SearchSession : IDisposable
    {
        private const int TableSize = 65536;
        private readonly Entry[] table = ArrayPool<Entry>.Shared.Rent(TableSize);
        private readonly Enclosure?[] positions;
        private readonly OrderedAction[]?[] ordered;
        private readonly int[]?[] legalIds;
        private readonly byte[]?[] classifications;
        private readonly int[,] variation;
        private readonly int[] variationLength;
        private readonly int[] firstKiller, secondKiller;
        private readonly int[] history = new int[2 * (Enclosure.SegmentCount + 1)];
        private readonly Deadline deadline;
        private readonly CancellationToken cancellation;
        private readonly int maxDepth, quiescenceDepth, quiescenceWidth, rootPlayer;
        private int activeQuiescenceDepth;
        private readonly long started = Stopwatch.GetTimestamp();
        private long nodes;
        private int initialRootCount = -1;

        public SearchSession(Enclosure state, Deadline deadline, int maxDepth, int quiescenceDepth, int quiescenceWidth, CancellationToken cancellation)
        {
            this.deadline = deadline;
            this.cancellation = cancellation;
            this.maxDepth = Math.Min(maxDepth, Enclosure.LineLimit - state.MoveNumber);
            this.quiescenceDepth = quiescenceDepth;
            this.quiescenceWidth = quiescenceWidth;
            var capacity = this.maxDepth + quiescenceDepth + 1;
            rootPlayer = state.Turn;
            Array.Clear(table, 0, TableSize);
            positions = new Enclosure[capacity];
            positions[0] = state.Copy();
            positions[0]!.EnableSearchAreaCache();
            ordered = new OrderedAction[capacity][];
            legalIds = new int[capacity][];
            classifications = new byte[capacity][];
            variation = new int[capacity, capacity];
            variationLength = new int[capacity];
            firstKiller = new int[capacity]; secondKiller = new int[capacity];
            Array.Fill(firstKiller, -1); Array.Fill(secondKiller, -1);
        }

        public EnclosureSearchResult Run()
        {
            var root = positions[0]!;
            var actionCount = Generate(root, 0);
            if (actionCount == 0) throw new InvalidDataException("An unfinished Enclosure game needs a legal action or forced pass.");
            var chosen = legalIds[0]![0];
            var completed = 0;
            var completedCaptures = 0;
            var value = Evaluate(root);
            int[] pv = [chosen];
            try
            {
                var firstOrder = BuildHeap(root, legalIds[0].AsSpan(0, actionCount), classifications[0].AsSpan(0, actionCount), 0, []);
                initialRootCount = firstOrder.Count;
                chosen = firstOrder.Items[0].Id;
                pv[0] = chosen;
                // Finish one inexpensive all-legal static iteration before spending the
                // remaining budget on tactical continuations. An interrupted extension must
                // never replace a searched move with an arbitrary move-order fallback.
                if (quiescenceDepth > 0)
                {
                    var staticValue = Visit(root, 1, 0, double.NegativeInfinity, double.PositiveInfinity, true);
                    CheckDeadline();
                    chosen = variation[0, 0]; value = staticValue; completed = 1;
                    pv = [chosen];
                    Array.Clear(table, 0, TableSize); // Static and extended leaf values differ.
                }
                activeQuiescenceDepth = quiescenceDepth;
                for (var depth = 1; depth <= maxDepth; depth++)
                {
                    CheckDeadline();
                    var candidateValue = Visit(root, depth, 0, double.NegativeInfinity, double.PositiveInfinity, principalVariation: true);
                    // An iteration becomes visible only after every required root branch completed.
                    CheckDeadline();
                    chosen = variation[0, 0];
                    value = candidateValue;
                    completed = depth;
                    completedCaptures = activeQuiescenceDepth;
                    pv = new int[variationLength[0]];
                    for (var i = 0; i < pv.Length; i++) pv[i] = variation[0, i];
                    if (Math.Abs(value) == 1) break; // A terminal win/loss already dominates every nonterminal leaf.
                }
            }
            catch (SearchStoppedException) { }
            return new(chosen, completed, nodes, Stopwatch.GetElapsedTime(started).TotalMilliseconds, value, pv)
                { CaptureQuiescencePlies = completedCaptures };
        }

        private double Visit(Enclosure state, int depth, int ply, double alpha, double beta, bool principalVariation)
        {
            CheckDeadline();
            nodes++;
            variationLength[ply] = 0;
            if (state.Finished) return Evaluate(state);
            if (depth == 0) return Quiescence(state, activeQuiescenceDepth, ply, alpha, beta, principalVariation, state.Turn);
            var originalAlpha = alpha;
            var originalBeta = beta;
            var key = state.GetSearchKey();
            var index = (int)(key.Low & (TableSize - 1));
            var entry = table[index];
            var preferred = -1;
            if (entry.Bound != Bound.Empty && entry.Low == key.Low && entry.High == key.High)
            {
                preferred = entry.Action;
                if (entry.Depth >= depth)
                {
                    if (entry.Bound == Bound.Lower) alpha = Math.Max(alpha, entry.Value);
                    if (entry.Bound == Bound.Upper) beta = Math.Min(beta, entry.Value);
                    if (entry.Bound == Bound.Exact || alpha >= beta)
                    {
                        if (principalVariation) RestoreVariation(state, depth, ply);
                        return entry.Value;
                    }
                }
            }

            // Most cut nodes need only a remembered move. Avoid generating and classifying the
            // complete legal set until the transposition move and legal killers fail to cut off.
            Span<int> early = stackalloc int[3];
            var earlyCount = 0;
            if (preferred >= 0 && state.IsLegal(preferred)) early[earlyCount++] = preferred;
            var killer = firstKiller[ply];
            if (killer >= 0 && !early[..earlyCount].Contains(killer) && state.IsLegal(killer)) early[earlyCount++] = killer;
            killer = secondKiller[ply];
            if (killer >= 0 && !early[..earlyCount].Contains(killer) && state.IsLegal(killer)) early[earlyCount++] = killer;
            var earlyIndex = 0;
            OrderedAction[]? candidates = null;
            var candidateCount = 0;
            var maximizing = state.Turn == rootPlayer;
            var best = maximizing ? double.NegativeInfinity : double.PositiveInfinity;
            var bestAction = -1;
            var child = positions[ply + 1] ??= state.Copy();
            var searched = 0;
            while (true)
            {
                CheckDeadline();
                int action;
                byte classification = 0;
                var classified = false;
                if (earlyIndex < earlyCount) action = early[earlyIndex++];
                else
                {
                    if (candidates is null)
                    {
                        if (ply == 0 && initialRootCount >= 0)
                        {
                            candidates = ordered[0]!; candidateCount = initialRootCount; initialRootCount = -1;
                        }
                        else
                        {
                            var count = Generate(state, ply);
                            CheckDeadline();
                            if (count == 0) throw new InvalidDataException("An unfinished search position has no legal action.");
                            (candidates, candidateCount) = BuildHeap(state, legalIds[ply].AsSpan(0, count),
                                classifications[ply].AsSpan(0, count), ply, early[..earlyCount]);
                        }
                    }
                    if (candidateCount == 0) break;
                    var next = Pop(candidates, ref candidateCount);
                    action = next.Id; classification = next.Classification; classified = true;
                }
                child.CopyFrom(state);
                if (classified) child.PlayGenerated(action, classification);
                else child.PlayGenerated(action);
                CheckDeadline();
                double value;
                if (searched == 0 || depth == 1 || maximizing && !double.IsFinite(alpha) || !maximizing && !double.IsFinite(beta))
                    value = Visit(child, depth - 1, ply + 1, alpha, beta, principalVariation);
                else if (maximizing)
                {
                    // Adjacent doubles form an exact null window without an arbitrary score epsilon.
                    // No sign changes are used: a child can belong to the same player as its parent.
                    value = Visit(child, depth - 1, ply + 1, alpha, Math.BitIncrement(alpha), false);
                    if (value > alpha && value < beta)
                        value = Visit(child, depth - 1, ply + 1, alpha, beta, principalVariation);
                }
                else
                {
                    value = Visit(child, depth - 1, ply + 1, Math.BitDecrement(beta), beta, false);
                    if (value < beta && value > alpha)
                        value = Visit(child, depth - 1, ply + 1, alpha, beta, principalVariation);
                }
                searched++;
                // Equality from a fail-low scout is only an upper bound. Verify it before the
                // root replaces a proven move just to obtain the smallest stable action ID.
                if (ply == 0 && bestAction >= 0 && value == best && action < bestAction)
                    value = Visit(child, depth - 1, ply + 1, double.NegativeInfinity, double.PositiveInfinity, true);
                if (bestAction < 0 || (maximizing ? value > best : value < best) || ply == 0 && value == best && action < bestAction)
                {
                    best = value;
                    bestAction = action;
                    if (principalVariation)
                    {
                        variation[ply, ply] = action;
                        var length = variationLength[ply + 1];
                        for (var j = 0; j < length; j++) variation[ply, ply + 1 + j] = variation[ply + 1, ply + 1 + j];
                        variationLength[ply] = length + 1;
                    }
                }
                if (maximizing) alpha = Math.Max(alpha, best);
                else beta = Math.Min(beta, best);
                if (alpha >= beta)
                {
                    RememberCutoff(state.Turn, ply, action, depth);
                    break;
                }
            }
            CheckDeadline();
            if (table[index].Bound == Bound.Empty || table[index].Depth <= depth ||
                table[index].Low == key.Low && table[index].High == key.High)
                table[index] = new Entry
                {
                    Low = key.Low, High = key.High, Depth = depth, Action = bestAction, Value = best,
                    Bound = best <= originalAlpha ? Bound.Upper : best >= originalBeta ? Bound.Lower : Bound.Exact
                };
            return best;
        }

        private double Quiescence(Enclosure state, int depth, int ply, double alpha, double beta, bool principalVariation, int tacticalPlayer)
        {
            CheckDeadline();
            variationLength[ply] = 0;
            var best = Evaluate(state);
            if (depth == 0 || state.Finished || state.Turn != tacticalPlayer || state.Areas[1 - state.Turn] <= 0) return best;
            var maximizing = state.Turn == rootPlayer;
            // Keeping the static estimate is a heuristic stand-pat choice, not a played pass.
            // Only actual legal captures enter the PV; both actions of the same player retain
            // the same minimax direction and normal Play applies protection expiry/scoring.
            if (maximizing)
            {
                if (best >= beta) return best;
                alpha = Math.Max(alpha, best);
            }
            else
            {
                if (best <= alpha) return best;
                beta = Math.Min(beta, best);
            }
            var actions = legalIds[ply] ??= ArrayPool<int>.Shared.Rent(Enclosure.SegmentCount + 1);
            var flags = classifications[ply] ??= ArrayPool<byte>.Shared.Rent(Enclosure.SegmentCount + 1);
            var count = state.GenerateLegalCaptures(actions, flags);
            if (count == 0) return best;
            var (candidates, remaining) = BuildHeap(state, actions.AsSpan(0, count), flags.AsSpan(0, count), ply, [], useHistory: false);
            var child = positions[ply + 1] ??= state.Copy();
            if (remaining > quiescenceWidth)
                remaining = SelectCaptureBeam(state, child, candidates, remaining, depth > 1 && state.ActionsRemaining > 1);
            while (remaining > 0)
            {
                CheckDeadline();
                var next = Pop(candidates, ref remaining);
                child.CopyFrom(state);
                child.PlayGenerated(next.Id, next.Classification);
                nodes++;
                var value = Quiescence(child, depth - 1, ply + 1, alpha, beta, principalVariation, tacticalPlayer);
                if (maximizing ? value > best : value < best)
                {
                    best = value;
                    if (principalVariation)
                    {
                        variation[ply, ply] = next.Id;
                        var length = variationLength[ply + 1];
                        for (var j = 0; j < length; j++) variation[ply, ply + 1 + j] = variation[ply + 1, ply + 1 + j];
                        variationLength[ply] = length + 1;
                    }
                }
                if (maximizing) alpha = Math.Max(alpha, best);
                else beta = Math.Min(beta, best);
                if (alpha >= beta) break;
            }
            return best;
        }

        private struct CaptureGroup
        {
            public int Victim;
            public OrderedAction First, Second;
            public bool HasSecond;
            public double Damage;
        }

        private int SelectCaptureBeam(Enclosure state, Enclosure scratch, OrderedAction[] moves, int count, bool preserveSetup)
        {
            Span<CaptureGroup> groups = stackalloc CaptureGroup[Enclosure.LineLimit + 2];
            var groupCount = 0;
            // Heap order supplies cheap tie-breaking, while every distinct removed edge gets
            // an exact area-impact probe. Quiet primary moves are never filtered this way.
            while (count > 0)
            {
                CheckDeadline();
                var move = Pop(moves, ref count);
                var victim = state.CapturedEdgeAction(move.Id);
                var index = 0;
                while (index < groupCount && groups[index].Victim != victim) index++;
                if (index == groupCount)
                {
                    groups[groupCount++] = new() { Victim = victim, First = move };
                }
                else if (!groups[index].HasSecond)
                {
                    groups[index].Second = move;
                    groups[index].HasSecond = true;
                }
            }
            var enemyArea = state.Areas[1 - state.Turn];
            var setup = -1;
            for (var i = 0; i < groupCount; i++)
            {
                CheckDeadline();
                ref var group = ref groups[i];
                scratch.CopyFrom(state);
                scratch.PlayGenerated(group.First.Id, group.First.Classification);
                group.Damage = Math.Max(0, enemyArea - scratch.Areas[1 - state.Turn]);
                // At most324area means this priority remains safely within a signed integer.
                var priority = (int)Math.Round(group.Damage * 1_000_000) + (group.First.Priority % 100_000_000) / 1024;
                group.First = group.First with { Priority = priority };
                if (group.HasSecond) group.Second = group.Second with { Priority = priority - 1 };
                if (preserveSetup && group.Damage < 1e-8 && (group.First.Classification & 4) != 0 &&
                    (setup < 0 || Before(group.First, groups[setup].First))) setup = i;
            }
            // Reserve one zero-damage cut that creates a node: the next same-player placement
            // can reach a different boundary through that node (seen in the supplied replay).
            var selected = 0;
            if (setup >= 0) moves[selected++] = groups[setup].First;
            for (var i = 0; i < groupCount; i++)
            {
                if (i == setup) continue;
                moves[selected++] = groups[i].First;
            }
            // Distinct victims precede second endpoint variants to avoid spending the entire
            // tactical allowance on geometrically equivalent cuts of a single boundary.
            Span<OrderedAction> choices = stackalloc OrderedAction[Enclosure.LineLimit + 2];
            moves.AsSpan(0, selected).CopyTo(choices);
            var kept = 0;
            if (setup >= 0) moves[kept++] = groups[setup].First;
            for (var i = 0; i < selected; i++)
            {
                var candidate = choices[i];
                if (setup >= 0 && candidate.Id == groups[setup].First.Id) continue;
                var at = kept;
                while (at > (setup >= 0 ? 1 : 0) && Before(candidate, moves[at - 1])) at--;
                if (at >= quiescenceWidth) continue;
                var end = Math.Min(kept, quiescenceWidth - 1);
                for (var j = end; j > at; j--) moves[j] = moves[j - 1];
                moves[at] = candidate;
                kept = Math.Min(quiescenceWidth, kept + 1);
            }
            for (var i = 0; i < groupCount && kept < quiescenceWidth; i++)
                if (groups[i].HasSecond) moves[kept++] = groups[i].Second;
            for (var i = kept / 2 - 1; i >= 0; i--) SiftDown(moves, i, kept);
            return kept;
        }

        private void RememberCutoff(int player, int ply, int action, int depth)
        {
            if (firstKiller[ply] != action)
            {
                secondKiller[ply] = firstKiller[ply];
                firstKiller[ply] = action;
            }
            var index = player * (Enclosure.SegmentCount + 1) + action;
            var bonus = depth * depth;
            // Gravity keeps scores bounded even in long searches; history only orders moves.
            history[index] += bonus - history[index] * bonus / 16384;
        }

        private int Generate(Enclosure state, int ply)
        {
            var actions = legalIds[ply] ??= ArrayPool<int>.Shared.Rent(Enclosure.SegmentCount + 1);
            var flags = classifications[ply] ??= ArrayPool<byte>.Shared.Rent(Enclosure.SegmentCount + 1);
            return state.GenerateLegalActions(actions, flags);
        }

        private (OrderedAction[] Items, int Count) BuildHeap(Enclosure state, ReadOnlySpan<int> actions,
            ReadOnlySpan<byte> flags, int ply, ReadOnlySpan<int> excluded, bool useHistory = true)
        {
            CheckDeadline();
            var buffer = ordered[ply];
            if (buffer is null || buffer.Length < actions.Length)
            {
                var replacement = ArrayPool<OrderedAction>.Shared.Rent(actions.Length);
                if (buffer is not null) ArrayPool<OrderedAction>.Shared.Return(buffer);
                ordered[ply] = buffer = replacement;
            }
            var count = 0;
            for (var i = 0; i < actions.Length; i++)
            {
                if ((i & 31) == 0) CheckDeadline();
                var action = actions[i];
                if (excluded.Contains(action)) continue;
                var classification = flags[i];
                var priority = useHistory ? history[state.Turn * (Enclosure.SegmentCount + 1) + action] * 256 : 0;
                if (action != Enclosure.PassActionId)
                {
                    var segment = Enclosure.DecodeAction(action);
                    var from = segment.From;
                    var to = segment.To;
                    if ((classification & 1) != 0) priority += 100_000_000;
                    if ((classification & 2) != 0) priority += 1_000_000;
                    if ((classification & 4) != 0) priority += 10_000;
                    var forward = state.Turn == 0 ? from.X + to.X : 36 - from.X - to.X;
                    priority += forward * 100 + (to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y);
                }
                buffer[count++] = new(action, priority, classification);
            }
            for (var i = count / 2 - 1; i >= 0; i--) SiftDown(buffer, i, count);
            CheckDeadline();
            return (buffer, count);
        }

        private static bool Before(OrderedAction a, OrderedAction b) =>
            a.Priority > b.Priority || a.Priority == b.Priority && a.Id < b.Id;

        private static void SiftDown(OrderedAction[] actions, int index, int count)
        {
            var value = actions[index];
            while (index * 2 + 1 < count)
            {
                var child = index * 2 + 1;
                if (child + 1 < count && Before(actions[child + 1], actions[child])) child++;
                if (!Before(actions[child], value)) break;
                actions[index] = actions[child];
                index = child;
            }
            actions[index] = value;
        }

        private static OrderedAction Pop(OrderedAction[] actions, ref int count)
        {
            var action = actions[0];
            actions[0] = actions[--count];
            if (count > 0) SiftDown(actions, 0, count);
            return action;
        }

        private double Evaluate(Enclosure state) => EnclosureStrategyEvaluation.Evaluate(state, rootPlayer);

        private void RestoreVariation(Enclosure state, int depth, int ply)
        {
            // A transposition may terminate recursion; recover its cached legal continuation for the UI.
            var current = state;
            for (var i = 0; i < depth && !current.Finished; i++)
            {
                CheckDeadline();
                var key = current.GetSearchKey();
                var entry = table[(int)(key.Low & (TableSize - 1))];
                if (entry.Bound == Bound.Empty || entry.Low != key.Low || entry.High != key.High || !current.IsLegal(entry.Action)) break;
                variation[ply, ply + i] = entry.Action;
                variationLength[ply]++;
                var next = positions[ply + i + 1] ??= current.Copy();
                next.CopyFrom(current);
                next.PlayGenerated(entry.Action);
                current = next;
            }
        }

        private void CheckDeadline()
        {
            if (cancellation.IsCancellationRequested || deadline.Expired) throw new SearchStoppedException();
        }

        public void Dispose()
        {
            ArrayPool<Entry>.Shared.Return(table);
            foreach (var buffer in ordered)
                if (buffer is not null) ArrayPool<OrderedAction>.Shared.Return(buffer);
            foreach (var buffer in legalIds)
                if (buffer is not null) ArrayPool<int>.Shared.Return(buffer);
            foreach (var buffer in classifications)
                if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
