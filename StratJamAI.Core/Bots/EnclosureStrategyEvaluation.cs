using StratJamAI.Core.Games;

namespace StratJamAI.Core.Bots;

/// <summary>A heuristic forecast, not banked territory or a guarantee that a boundary can be completed.</summary>
public readonly record struct EnclosureStrategyForecast(double CurrentArea, double PotentialArea, int CompletionMoves,
    int RemainingScoringEvents, int ProtectedEdges, double ReinforcedBoundary, double ProjectedScore,
    double ControlledSpace = 0, double ExpansionScore = 0, double BoundaryArea = 0,
    int BoundaryCompletionMoves = 0, double SafeBoundaryFraction = 0);

public static class EnclosureStrategyEvaluation
{
    private const int CacheSize = 8192;
    private const int SampleCount = 81;
    private readonly record struct Shape(double HullArea, int MissingMoves, double ReinforcedBoundary);
    private struct CacheEntry { public ulong Low, High; public Shape Shape; public bool Present; }
    // Like planar-area scratch, this is bounded and thread-owned. Search calls never share mutable entries.
    [ThreadStatic] private static CacheEntry[]? cache;

    public static double Evaluate(Enclosure game, int player)
    {
        if ((uint)player > 1) throw new ArgumentOutOfRangeException(nameof(player));
        if (game.Finished) return Math.Sign(game.Scores[player] - game.Scores[1 - player]);
        GetForecasts(game, out var blue, out var red);
        var raw = player == 0 ? blue.ProjectedScore - red.ProjectedScore : red.ProjectedScore - blue.ProjectedScore;
        return 0.99 * raw / (324 + Math.Abs(raw));
    }

    public static EnclosureStrategyForecast Forecast(Enclosure game, int player)
    {
        if ((uint)player > 1) throw new ArgumentOutOfRangeException(nameof(player));
        GetForecasts(game, out var blue, out var red);
        return player == 0 ? blue : red;
    }

    private static void GetForecasts(Enclosure game, out EnclosureStrategyForecast blue, out EnclosureStrategyForecast red)
    {
        if (game.Finished)
        {
            blue = new(game.Areas[0], 0, 0, 0, 0, 0, game.Scores[0]);
            red = new(game.Areas[1], 0, 0, 0, 0, 0, game.Scores[1]);
            return;
        }
        // Copy the small maps before probing the opposing key: their cache slots may collide.
        Span<byte> blueDistances = stackalloc byte[SampleCount];
        Span<byte> redDistances = stackalloc byte[SampleCount];
        var blueShape = GetShape(game, 0);
        var redShape = GetShape(game, 1);
        // Access is constrained by the opponent's geometry, including double walls whose
        // two crossings are illegal even after their temporary shields have expired.
        EnclosureStrategicAccess.FillDistances(game, 0, blueDistances);
        EnclosureStrategicAccess.FillDistances(game, 1, redDistances);
        double blueSpace = 0, redSpace = 0;
        var blueContested = false;
        var redContested = false;
        var remaining = Enclosure.LineLimit - game.MoveNumber;
        var ownPlacements = Math.Max(0, remaining / 2);
        for (var i = 0; i < SampleCount; i++)
        {
            // Each sample represents a 2x2 square. A three-unit distance advantage secures
            // it heuristically; equally distant frontiers share it. This measures access,
            // not ownership: crossings and future opposition can still deny the space.
            var share = Math.Clamp((redDistances[i] - blueDistances[i] + 3) / 6.0, 0, 1);
            if (blueDistances[i] < EnclosureStrategicAccess.UnreachableDistance &&
                (blueDistances[i] + 2) / 3 + 2 <= ownPlacements) blueSpace += 4 * share;
            if (redDistances[i] < EnclosureStrategicAccess.UnreachableDistance &&
                (redDistances[i] + 2) / 3 + 2 <= ownPlacements) redSpace += 4 * (1 - share);
            // Samples beside our frontier and within one line-length of opposing nodes
            // indicate construction under pressure. This is a proximity estimate; actual
            // capture legality and protection are handled by search, not by this discount.
            blueContested |= blueDistances[i] <= 1 && redDistances[i] <= 3;
            redContested |= redDistances[i] <= 1 && blueDistances[i] <= 3;
        }
        blue = CreateForecast(game, 0, blueShape, blueSpace, blueContested, redDistances);
        red = CreateForecast(game, 1, redShape, redSpace, redContested, blueDistances);
    }

    private static EnclosureStrategyForecast CreateForecast(Enclosure game, int player, Shape shape, double controlledSpace,
        bool contested, ReadOnlySpan<byte> enemyDistances)
    {
        var area = game.Areas[player];
        var events = 1 + (Math.Max(0, Enclosure.LineLimit - game.MoveNumber - game.ActionsRemaining) + 1) / 2;
        var additional = Math.Max(0, shape.HullArea - area);
        var moves = Math.Max(1, shape.MissingMoves);
        // Scoring happens at each turn boundary, including the opponent's. A second required
        // placement cannot be credited this turn when only one of our placements remains.
        var completionEvent = player == game.Turn
            ? moves <= game.ActionsRemaining ? 1 : 1 + 2 * ((moves - game.ActionsRemaining + 1) / 2)
            : 2 * ((moves + 1) / 2);
        var availableEvents = Math.Max(0, events - completionEvent + 1);
        var potential = availableEvents == 0 ? 0 : additional / (1 + 0.3 * moves);
        var protectedEdges = game.ProtectedEdgeCount(player);
        // Reserving distant space needs construction time. This bounded, contested estimate
        // rewards moving a frontier outward even before three points outline a hull, but
        // fades during the final 48 placements and gives no credit in the last six events.
        var phase = Math.Min(1, (Enclosure.LineLimit - game.MoveNumber) / 48.0);
        var expansion = 0.10 * controlledSpace * Math.Max(0, events - 6) * phase;
        // Reinforcement and temporary invincibility remain diagnostic features. They do not
        // create points on their own; defensive moves must retain area against searched attacks.
        // Contested open hulls may be cheap to repair and equally cheap to cut again. Credit
        // at most one round of speculative income until search closes and tests the shape.
        // Distant construction keeps its long-term incentive to claim a larger area.
        var potentialEvents = contested ? Math.Min(2, availableEvents) : availableEvents;
        var potentialIncome = potential * potentialEvents;
        var completionMoves = additional > 0 ? moves : 0;
        var boundary = EnclosureBoundaryPlan.Forecast(game, player);
        var safety = BoundarySafety(boundary, enemyDistances);
        if (boundary.CompletionMoves > 0)
        {
            var boundaryMoves = boundary.CompletionMoves;
            var boundaryEvent = player == game.Turn
                ? boundaryMoves <= game.ActionsRemaining ? 1 : 1 + 2 * ((boundaryMoves - game.ActionsRemaining + 1) / 2)
                : 2 * ((boundaryMoves + 1) / 2);
            var boundaryEvents = Math.Max(0, events - boundaryEvent + 1);
            var boundaryPotential = Math.Max(0, boundary.TotalArea - area) / (1 + .3 * boundaryMoves);
            // Pressure belongs to the region this route would enclose. An opponent on the
            // other side of a braid must not erase the value of all the safe backspace.
            // Exposed regions keep the short speculative horizon used by tactical defense.
            var shortHorizon = Math.Min(2, boundaryEvents);
            var income = boundaryPotential * (shortHorizon + (boundaryEvents - shortHorizon) * safety * safety);
            if (income > potentialIncome)
            {
                potentialIncome = income; potential = boundaryPotential; completionMoves = boundaryMoves;
            }
        }
        var projected = game.Scores[player] + area * events + potentialIncome + expansion;
        return new(area, potential, completionMoves, events, protectedEdges, shape.ReinforcedBoundary,
            projected, controlledSpace, expansion, boundary.TotalArea, boundary.CompletionMoves, safety);
    }

    private static double BoundarySafety(EnclosureBoundaryForecast boundary, ReadOnlySpan<byte> enemyDistances)
    {
        double safe = 0;
        var count = 0;
        for (var sample = 0; sample < SampleCount; sample++)
        {
            var inside = sample < 64 ? (boundary.SampleMaskLow & (1UL << sample)) != 0 :
                (boundary.SampleMaskHigh & (1UL << (sample - 64))) != 0;
            if (!inside) continue;
            count++;
            safe += Math.Clamp((enemyDistances[sample] - 3) / 3.0, 0, 1);
        }
        return count == 0 ? 0 : safe / count;
    }

    private static Shape GetShape(Enclosure game, int player)
    {
        var key = game.StrategyTopologyKey(player);
        var entries = cache ??= new CacheEntry[CacheSize];
        var index = (int)(key.Low & (CacheSize - 1));
        ref var entry = ref entries[index];
        if (!entry.Present || entry.Low != key.Low || entry.High != key.High)
        {
            Span<int> actions = stackalloc int[Enclosure.LineLimit + 2];
            var count = game.CopyStrategyEdges(player, actions);
            var shape = CalculateShape(actions[..count]);
            entry = new() { Low = key.Low, High = key.High, Shape = shape, Present = true };
        }
        return entry.Shape;
    }

    private static Shape CalculateShape(ReadOnlySpan<int> actions)
    {
        if (actions.Length < 2) return default;
        Span<int> parents = stackalloc int[361];
        parents.Fill(-1);
        Span<int> points = stackalloc int[(Enclosure.LineLimit + 2) * 2];
        var count = 0;
        foreach (var action in actions)
        {
            var segment = Enclosure.DecodeAction(action);
            var a = segment.From.X * 19 + segment.From.Y;
            var b = segment.To.X * 19 + segment.To.Y;
            if (parents[a] < 0) { parents[a] = a; points[count++] = a; }
            if (parents[b] < 0) { parents[b] = b; points[count++] = b; }
            parents[Root(parents, b)] = Root(parents, a);
        }
        // Endpoint connectivity is conservative: disconnected fragments cannot obtain the
        // huge fictitious potential produced by a bounding box around all of a player's nodes.
        for (var i = 0; i < count; i++) points[i] += Root(parents, points[i]) * 361;
        points[..count].Sort();
        Span<EnclosurePoint> hull = stackalloc EnclosurePoint[(Enclosure.LineLimit + 2) * 4];
        var best = default(Shape);
        for (var start = 0; start < count;)
        {
            var component = points[start] / 361;
            var end = start + 1;
            while (end < count && points[end] / 361 == component) end++;
            if (end - start >= 3)
            {
                var size = Hull(points[start..end], hull);
                double twiceArea = 0;
                var missingMoves = 0;
                var perimeter = 0;
                var backedBoundary = 0;
                for (var i = 0; i < size; i++)
                {
                    var a = hull[i]; var b = hull[(i + 1) % size];
                    twiceArea += a.X * b.Y - a.Y * b.X;
                    var distance = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                    var covered = 0;
                    var backed = 0;
                    foreach (var action in actions)
                    {
                        var edge = Enclosure.DecodeAction(action);
                        if (On(edge.From, a, b) && On(edge.To, a, b))
                            covered += Math.Max(Math.Abs(edge.To.X - edge.From.X), Math.Abs(edge.To.Y - edge.From.Y));
                        else backed += ParallelBacking(edge, a, b);
                    }
                    // Each move spans at most three units on either axis. Existing collinear
                    // boundary segments count as progress; interior triangles do not close a gap.
                    missingMoves += (Math.Max(0, distance - covered) + 2) / 3;
                    perimeter += distance;
                    backedBoundary += Math.Min(covered, backed);
                }
                var area = Math.Abs(twiceArea) * 0.5;
                if (area > best.HullArea || area == best.HullArea && missingMoves < best.MissingMoves)
                    best = new(area, missingMoves, perimeter == 0 ? 0 : (double)backedBoundary / perimeter);
            }
            start = end;
        }
        return best;
    }

    private static int Root(Span<int> parents, int point)
    {
        while (parents[point] != point) { parents[point] = parents[parents[point]]; point = parents[point]; }
        return point;
    }

    private static int Hull(ReadOnlySpan<int> points, Span<EnclosurePoint> hull)
    {
        var size = 0;
        foreach (var encoded in points)
        {
            var index = encoded % 361;
            var point = new EnclosurePoint(index / 19, index % 19);
            while (size >= 2 && Cross(hull[size - 2], hull[size - 1], point) <= 0) size--;
            hull[size++] = point;
        }
        var upperStart = size + 1;
        for (var i = points.Length - 2; i >= 0; i--)
        {
            var index = points[i] % 361;
            var point = new EnclosurePoint(index / 19, index % 19);
            while (size >= upperStart && Cross(hull[size - 2], hull[size - 1], point) <= 0) size--;
            hull[size++] = point;
        }
        return size - 1;
    }

    private static int Cross(EnclosurePoint a, EnclosurePoint b, EnclosurePoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static int ParallelBacking(EnclosureSegment edge, EnclosurePoint a, EnclosurePoint b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        if (dx * (edge.To.Y - edge.From.Y) != dy * (edge.To.X - edge.From.X)) return 0;
        var offset = Cross(a, b, edge.From);
        if (offset <= 0 || offset * offset > dx * dx + dy * dy) return 0;
        var horizontal = Math.Abs(dx) >= Math.Abs(dy);
        var from = horizontal ? a.X : a.Y; var to = horizontal ? b.X : b.Y;
        var edgeFrom = horizontal ? edge.From.X : edge.From.Y; var edgeTo = horizontal ? edge.To.X : edge.To.Y;
        return Math.Max(0, Math.Min(Math.Max(from, to), Math.Max(edgeFrom, edgeTo)) -
            Math.Max(Math.Min(from, to), Math.Min(edgeFrom, edgeTo)));
    }

    private static bool On(EnclosurePoint point, EnclosurePoint a, EnclosurePoint b) =>
        Cross(a, b, point) == 0 && point.X >= Math.Min(a.X, b.X) && point.X <= Math.Max(a.X, b.X) &&
        point.Y >= Math.Min(a.Y, b.Y) && point.Y <= Math.Max(a.Y, b.Y);
}
