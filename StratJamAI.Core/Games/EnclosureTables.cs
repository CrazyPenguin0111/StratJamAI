namespace StratJamAI.Core.Games;

public readonly record struct EnclosurePoint(int X, int Y);
public readonly record struct EnclosurePosition(double X, double Y);
public readonly record struct EnclosureSegment(int ActionId, EnclosurePoint From, EnclosurePoint To, bool Invincible);

/// <summary>Shared immutable geometry masks and hash tokens for all positions and games.</summary>
internal static class EnclosureTables
{
    internal const int Words = (Enclosure.SegmentCount + 63) / 64;
    internal static readonly EnclosureSegment[] Segments;
    internal static readonly int[] Ids = new int[361 * 361];
    internal static readonly ulong[] Intersections = new ulong[Enclosure.SegmentCount * Words];
    internal static readonly ulong[] OwnConflicts = new ulong[Enclosure.SegmentCount * Words];
    internal static readonly ulong[] OwnCrossings = new ulong[Enclosure.SegmentCount * Words];
    internal static readonly ulong[] Incident = new ulong[361 * Words];
    internal static readonly ulong[] Through = new ulong[361 * Words];
    internal static readonly (ulong Low, ulong High)[] NodeKeys = new (ulong, ulong)[722];
    internal static readonly (ulong Low, ulong High)[] EdgeKeys = new (ulong, ulong)[Enclosure.SegmentCount * 8];
    internal static readonly (ulong Low, ulong High)[] CounterKeys = new (ulong, ulong)[Enclosure.LineLimit * 8 + 8];
    internal static readonly (ulong Low, ulong High)[] AreaEdgeKeys = new (ulong, ulong)[Enclosure.SegmentCount];
    internal static readonly (ulong Low, ulong High)[] AreaCountKeys = new (ulong, ulong)[Enclosure.LineLimit + 3];
    static EnclosureTables()
    {
        Array.Fill(Ids, -1);
        var segments = new List<EnclosureSegment>(Enclosure.SegmentCount);
        for (var a = 0; a < 361; a++)
        for (var b = a + 1; b < 361; b++)
        {
            var from = Point(a); var to = Point(b);
            if (Math.Abs(from.X - to.X) > 3 || Math.Abs(from.Y - to.Y) > 3) continue;
            var id = segments.Count; segments.Add(new(id, from, to, false));
            Ids[a * 361 + b] = Ids[b * 361 + a] = id;
            Set(Incident, a, id); Set(Incident, b, id);
            for (var x = Math.Min(from.X, to.X); x <= Math.Max(from.X, to.X); x++)
            for (var y = Math.Min(from.Y, to.Y); y <= Math.Max(from.Y, to.Y); y++)
            {
                var point = new EnclosurePoint(x, y);
                if (point != from && point != to && On(point, from, to)) Set(Through, Index(point), id);
            }
        }
        Segments = segments.ToArray();
        if (Segments.Length != Enclosure.SegmentCount) throw new InvalidOperationException("Unexpected Enclosure action vocabulary.");
        for (var i = 0; i < NodeKeys.Length; i++) NodeKeys[i] = StateKeyToken(0x400000UL + (ulong)i);
        for (var i = 0; i < EdgeKeys.Length; i++) EdgeKeys[i] = StateKeyToken(0x500000UL + (ulong)i);
        for (var i = 0; i < CounterKeys.Length; i++) CounterKeys[i] = StateKeyToken(0x100000UL + (ulong)i);
        for (var i = 0; i < AreaEdgeKeys.Length; i++)
            AreaEdgeKeys[i] = (Mix((ulong)i + 0x9e3779b97f4a7c15UL), Mix((ulong)i + 0x94d049bb133111ebUL));
        for (var i = 0; i < AreaCountKeys.Length; i++)
            AreaCountKeys[i] = (Mix((ulong)i), Mix((ulong)i + 0xd1b54a32d192ed03UL));
        for (var i = 0; i < Segments.Length; i++)
        for (var j = i; j < Segments.Length; j++)
        {
            var a = Segments[i]; var b = Segments[j];
            if (Math.Max(a.From.X, a.To.X) < Math.Min(b.From.X, b.To.X) ||
                Math.Max(b.From.X, b.To.X) < Math.Min(a.From.X, a.To.X) ||
                Math.Max(a.From.Y, a.To.Y) < Math.Min(b.From.Y, b.To.Y) ||
                Math.Max(b.From.Y, b.To.Y) < Math.Min(a.From.Y, a.To.Y)) continue;
            var kind = EnclosureGeometry.Intersection(new(a.From.X, a.From.Y), new(a.To.X, a.To.Y),
                new(b.From.X, b.From.Y), new(b.To.X, b.To.Y), out _);
            if (kind == 0) continue;
            Set(Intersections, i, j); Set(Intersections, j, i);
            if (kind == 2 || Interior(a.From, b) || Interior(a.To, b) || Interior(b.From, a) || Interior(b.To, a))
            { Set(OwnConflicts, i, j); Set(OwnConflicts, j, i); }
            else if (a.From != b.From && a.From != b.To && a.To != b.From && a.To != b.To)
            { Set(OwnCrossings, i, j); Set(OwnCrossings, j, i); }
        }
    }

    internal static (ulong Low, ulong High) StateKeyToken(ulong value) =>
        (Mix(value + 0x9e3779b97f4a7c15UL), Mix(value + 0xd1b54a32d192ed03UL));

    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
    private static bool Interior(EnclosurePoint point, EnclosureSegment segment) =>
        point != segment.From && point != segment.To && On(point, segment.From, segment.To);
    internal static bool On(EnclosurePoint p, EnclosurePoint a, EnclosurePoint b) =>
        (p.X - a.X) * (b.Y - a.Y) == (p.Y - a.Y) * (b.X - a.X) &&
        p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) &&
        p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
    private static void Set(ulong[] masks, int row, int id) => masks[row * Words + (id >> 6)] |= 1UL << (id & 63);
    internal static bool Intersects(int a, int b) => (Intersections[a * Words + (b >> 6)] & (1UL << (b & 63))) != 0;
    internal static int Index(EnclosurePoint point) => point.X * 19 + point.Y;
    internal static EnclosurePoint Point(int index) => new(index / 19, index % 19);
}
