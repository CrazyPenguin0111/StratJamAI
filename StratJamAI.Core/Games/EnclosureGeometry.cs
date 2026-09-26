namespace StratJamAI.Core.Games;

/// <summary>Planar subdivision matching the public Enclosure practice game's area rules.</summary>
internal static class EnclosureGeometry
{
    private const double Epsilon = 1e-9;
    internal readonly record struct Point(double X, double Y);
    private sealed class Vertex(Point point)
    {
        public readonly Point Point = point;
        public readonly HashSet<int> Neighbors = [];
        public int[] Ordered = [];
    }
    internal static double Round(double value) => Math.Floor(value / Epsilon + .5) * Epsilon;
    private static double Cross(Point a, Point b) => a.X * b.Y - a.Y * b.X;
    private static Point Sub(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
    private static bool Equal(Point a, Point b) => Math.Abs(a.X - b.X) < Epsilon && Math.Abs(a.Y - b.Y) < Epsilon;
    private static bool On(Point p, Point a, Point b) => Math.Abs(Cross(Sub(p, a), Sub(b, a))) < Epsilon &&
        p.X >= Math.Min(a.X, b.X) - Epsilon && p.X <= Math.Max(a.X, b.X) + Epsilon &&
        p.Y >= Math.Min(a.Y, b.Y) - Epsilon && p.Y <= Math.Max(a.Y, b.Y) + Epsilon;

    // 0 = disjoint, 1 = a point, 2 = a collinear interval.
    internal static int Intersection(Point a, Point b, Point c, Point d, out Point point)
    {
        point = default;
        var u = Sub(b, a); var v = Sub(d, c); var w = Sub(c, a);
        var cross = Cross(u, v);
        if (Math.Abs(cross) < Epsilon)
        {
            if (Math.Abs(Cross(w, u)) >= Epsilon) return 0;
            var t = Math.Abs(u.X) >= Epsilon ? (c.X - a.X) / u.X : (c.Y - a.Y) / u.Y;
            var z = Math.Abs(u.X) >= Epsilon ? (d.X - a.X) / u.X : (d.Y - a.Y) / u.Y;
            var start = Math.Max(0, Math.Min(t, z)); var end = Math.Min(1, Math.Max(t, z));
            if (start > end + Epsilon) return 0;
            if (Math.Abs(start - end) >= Epsilon) return 2;
            point = new(a.X + start * u.X, a.Y + start * u.Y);
            return 1;
        }
        var first = Cross(w, v) / cross; var second = Cross(w, u) / cross;
        if (first < -Epsilon || first > 1 + Epsilon || second < -Epsilon || second > 1 + Epsilon) return 0;
        point = new(a.X + first * u.X, a.Y + first * u.Y);
        return 1;
    }

    [ThreadStatic] private static AreaWorkspace? areaWorkspace;

    internal static double Area(ReadOnlySpan<int> actions)
    {
        if (actions.Length < 3) return 0;
        // Keep retained scratch bounded by the maximum reachable game position. The general
        // polygon routine remains available for larger synthetic geometry and UI territories.
        if (actions.Length > Enclosure.LineLimit + 2) return Calculate(actions, false).Area;
        return (areaWorkspace ??= new()).Calculate(actions);
    }

    /// <summary>
    /// The same planar subdivision as Calculate, with thread-owned reusable storage. Calls are
    /// synchronous and invoke no user callbacks, so parallel training/search never shares it.
    /// Flat face/path buffers avoid retaining a separate maximum-sized list for every face.
    /// </summary>
    private sealed class AreaWorkspace : IComparer<Point>
    {
        private const int Capacity = Enclosure.LineLimit + 2;
        private readonly Point[] from = new Point[Capacity], to = new Point[Capacity], probes = new Point[Capacity];
        private readonly int[] parents = new int[Capacity];
        private readonly bool[] probePresent = new bool[Capacity], nested = new bool[Capacity];
        private readonly List<Point>[] splits = Enumerable.Range(0, Capacity).Select(_ => new List<Point>(4)).ToArray();
        private readonly Dictionary<(long, long), int> keys = [];
        private readonly List<AreaVertex> vertices = [];
        private readonly HashSet<(int, int)> visited = [];
        private readonly List<Face> faces = [];
        private readonly List<int> path = [], facePoints = [];
        private readonly Comparison<Point> comparePoints;
        private int vertexCount;
        private double sortDx, sortDy;

        private readonly record struct Neighbor(int Id, double Angle);
        private readonly record struct Face(int Component, int Start, int Count, double Area);

        private sealed class AreaVertex
        {
            public Point Point;
            public int Component;
            public readonly HashSet<int> Neighbors = [];
            public Neighbor[] Ordered = [];
        }

        public AreaWorkspace() => comparePoints = Compare;

        public int Compare(Point a, Point b) =>
            (Math.Abs(sortDx) >= Epsilon ? (a.X - b.X) / sortDx : (a.Y - b.Y) / sortDy).CompareTo(0);

        private int Root(int i)
        {
            while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; }
            return i;
        }

        private int VertexId(Point point, int component)
        {
            var key = ((long)Math.Floor(point.X / Epsilon + .5), (long)Math.Floor(point.Y / Epsilon + .5));
            if (keys.TryGetValue(key, out var id)) return id;
            id = vertexCount++;
            keys.Add(key, id);
            if (id == vertices.Count) vertices.Add(new());
            var vertex = vertices[id];
            vertex.Point = point; vertex.Component = component; vertex.Neighbors.Clear();
            return id;
        }

        public double Calculate(ReadOnlySpan<int> actions)
        {
            var n = actions.Length;
            vertexCount = 0; keys.Clear(); visited.Clear(); faces.Clear(); facePoints.Clear();
            Array.Clear(probePresent, 0, n); Array.Clear(nested, 0, n);
            for (var i = 0; i < n; i++)
            {
                var segment = EnclosureTables.Segments[actions[i]];
                from[i] = new(segment.From.X, segment.From.Y); to[i] = new(segment.To.X, segment.To.Y);
                var points = splits[i]; points.Clear(); points.Add(from[i]); points.Add(to[i]);
                parents[i] = i;
            }
            for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                if (!EnclosureTables.Intersects(actions[i], actions[j])) continue;
                parents[Root(j)] = Root(i);
                if (Intersection(from[i], to[i], from[j], to[j], out var point) == 1)
                { splits[i].Add(point); splits[j].Add(point); }
            }
            for (var i = 0; i < n; i++)
            {
                var component = Root(i);
                if (!probePresent[component]) { probePresent[component] = true; probes[component] = from[i]; }
                sortDx = to[i].X - from[i].X; sortDy = to[i].Y - from[i].Y;
                var points = splits[i]; points.Sort(comparePoints);
                for (var j = 0; j + 1 < points.Count; j++)
                {
                    if (Equal(points[j], points[j + 1])) continue;
                    var a = VertexId(points[j], component); var b = VertexId(points[j + 1], component);
                    vertices[a].Neighbors.Add(b); vertices[b].Neighbors.Add(a);
                }
            }
            for (var i = 0; i < vertexCount; i++)
            {
                var vertex = vertices[i]; var count = vertex.Neighbors.Count;
                if (vertex.Ordered.Length < count) vertex.Ordered = new Neighbor[Math.Max(4, count)];
                var index = 0;
                foreach (var neighborId in vertex.Neighbors)
                {
                    var point = vertices[neighborId].Point;
                    var neighbor = new Neighbor(neighborId, Math.Atan2(point.Y - vertex.Point.Y, point.X - vertex.Point.X));
                    // Degrees are normally two or four. Stable insertion order also matches the
                    // general routine's OrderBy for equal angles without per-vertex sort objects.
                    var insert = index++;
                    while (insert > 0 && vertex.Ordered[insert - 1].Angle > neighbor.Angle)
                    { vertex.Ordered[insert] = vertex.Ordered[insert - 1]; insert--; }
                    vertex.Ordered[insert] = neighbor;
                }
            }
            for (var start = 0; start < vertexCount; start++)
            foreach (var next in vertices[start].Neighbors)
            {
                if (visited.Contains((start, next))) continue;
                var a = start; var b = next; path.Clear();
                while (visited.Add((a, b)))
                {
                    path.Add(a);
                    var vertex = vertices[b]; var count = vertex.Neighbors.Count; var index = 0;
                    while (vertex.Ordered[index].Id != a) index++;
                    var following = vertex.Ordered[(index - 1 + count) % count].Id;
                    a = b; b = following;
                }
                if (a != start || b != next) continue;
                double signed = 0;
                for (var j = 0; j < path.Count; j++)
                    signed += Cross(vertices[path[j]].Point, vertices[path[(j + 1) % path.Count]].Point);
                signed /= 2;
                if (signed <= Epsilon) continue;
                faces.Add(new(vertices[start].Component, facePoints.Count, path.Count, signed));
                facePoints.AddRange(path);
            }
            foreach (var face in faces)
            {
                if (nested[face.Component]) continue;
                foreach (var other in faces)
                    if (other.Component != face.Component && Inside(probes[face.Component], other))
                    { nested[face.Component] = true; break; }
            }
            double area = 0;
            foreach (var face in faces) if (!nested[face.Component]) area += face.Area;
            return Round(area);
        }

        private bool Inside(Point point, Face face)
        {
            var inside = false;
            for (var i = 0; i < face.Count; i++)
            {
                var a = vertices[facePoints[face.Start + i]].Point;
                var b = vertices[facePoints[face.Start + (i + 1) % face.Count]].Point;
                if (On(point, a, b)) return false;
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y) > point.X) inside = !inside;
            }
            return inside;
        }
    }

    internal static (double Area, EnclosurePosition[][] Territories) Calculate(ReadOnlySpan<int> actions,
        bool includeTerritories = true)
    {
        if (actions.Length < 3) return (0, []);
        var n = actions.Length;
        var from = new Point[n]; var to = new Point[n];
        var splits = new List<Point>[n]; var parents = new int[n];
        for (var i = 0; i < n; i++)
        {
            var segment = EnclosureTables.Segments[actions[i]];
            from[i] = new(segment.From.X, segment.From.Y); to[i] = new(segment.To.X, segment.To.Y);
            splits[i] = [from[i], to[i]]; parents[i] = i;
        }
        int Root(int i) { while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; } return i; }
        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            if (!EnclosureTables.Intersects(actions[i], actions[j])) continue;
            parents[Root(j)] = Root(i);
            if (Intersection(from[i], to[i], from[j], to[j], out var p) == 1)
            { splits[i].Add(p); splits[j].Add(p); }
        }
        var vertices = new List<Vertex>();
        var keys = new Dictionary<(long, long), int>();
        int VertexId(Point p)
        {
            var key = ((long)Math.Floor(p.X / Epsilon + .5), (long)Math.Floor(p.Y / Epsilon + .5));
            if (keys.TryGetValue(key, out var id)) return id;
            keys.Add(key, vertices.Count); vertices.Add(new(p)); return vertices.Count - 1;
        }
        var probes = new Dictionary<int, Point>();
        var vertexComponent = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var component = Root(i); probes.TryAdd(component, from[i]);
            var points = splits[i]; var dx = to[i].X - from[i].X; var dy = to[i].Y - from[i].Y;
            points.Sort((a, b) => (Math.Abs(dx) >= Epsilon ? (a.X - b.X) / dx : (a.Y - b.Y) / dy).CompareTo(0));
            for (var j = 0; j + 1 < points.Count; j++)
            {
                if (Equal(points[j], points[j + 1])) continue;
                var a = VertexId(points[j]); var b = VertexId(points[j + 1]);
                while (vertexComponent.Count < vertices.Count) vertexComponent.Add(component);
                vertices[a].Neighbors.Add(b); vertices[b].Neighbors.Add(a);
            }
        }
        foreach (var vertex in vertices)
            vertex.Ordered = vertex.Neighbors.OrderBy(id => Math.Atan2(vertices[id].Point.Y - vertex.Point.Y,
                vertices[id].Point.X - vertex.Point.X)).ToArray();
        var visited = new HashSet<(int, int)>();
        var faces = new List<(int Component, List<Point> Polygon, double Area)>();
        for (var start = 0; start < vertices.Count; start++)
        foreach (var next in vertices[start].Neighbors)
        {
            if (visited.Contains((start, next))) continue;
            var a = start; var b = next; var polygon = new List<Point>();
            while (visited.Add((a, b)))
            {
                polygon.Add(vertices[a].Point);
                var ordered = vertices[b].Ordered; var index = Array.IndexOf(ordered, a);
                var following = ordered[(index - 1 + ordered.Length) % ordered.Length];
                a = b; b = following;
            }
            if (a != start || b != next) continue;
            double signed = 0;
            for (var j = 0; j < polygon.Count; j++)
                signed += Cross(polygon[j], polygon[(j + 1) % polygon.Count]);
            signed /= 2;
            if (signed > Epsilon) faces.Add((vertexComponent[start], polygon, signed));
        }
        // A closed disconnected component nested in another closed component adds no area.
        var nested = new HashSet<int>();
        foreach (var face in faces)
            if (faces.Any(other => other.Component != face.Component && Inside(probes[face.Component], other.Polygon)))
                nested.Add(face.Component);
        var exteriorFaces = faces.Where(face => !nested.Contains(face.Component)).ToArray();
        return (Round(exteriorFaces.Sum(face => face.Area)), includeTerritories
            ? exteriorFaces.Select(face => face.Polygon.Select(point => new EnclosurePosition(point.X, point.Y)).ToArray()).ToArray()
            : []);
    }
    private static bool Inside(Point point, List<Point> polygon)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            if (On(point, a, b)) return false;
            if ((a.Y > point.Y) != (b.Y > point.Y) && a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y) > point.X)
                inside = !inside;
        }
        return inside;
    }
}
