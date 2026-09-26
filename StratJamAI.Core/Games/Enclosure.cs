using System.Numerics;
using StratJamAI.Core.Bots;

namespace StratJamAI.Core.Games;

public sealed class EnclosureDefinition : IGameDefinition
{
    public GameSpec Spec => Enclosure.GameSpec;
    public IGameAdapter Create() => new Enclosure();
    public IBot CreateTeacher() => new EnclosureTacticalBot();
    public IBot? CreateOracle() => null;
}

/// <summary>The deterministic 19×19 Enclosure practice rules. Coordinates are zero based.</summary>
public sealed class Enclosure : ISearchableGame
{
    public const int BoardSize = 19;
    public const int LineLimit = 120;
    public const int SegmentCount = 7140;
    public const int PassActionId = SegmentCount;
    private const int Capacity = LineLimit + 2;
    // Six features per placement slot, two relative node planes, and seven global values.
    public static readonly GameSpec GameSpec = new("enclosure",
        "enclosure-xmajor-relative-rot180-slots122x6-nodes722-globals7-action12-v1", 1461, 12, 2,
        Sequential: true, FullyObservable: true, Deterministic: true, ZeroSum: true);

    private readonly int[] edges = new int[Capacity];
    private readonly byte[] owners = new byte[Capacity];
    // Bit 0: protected; bit 1: placed in this turn.
    private readonly byte[] flags = new byte[Capacity];
    private readonly bool[] nodes = new bool[2 * BoardSize * BoardSize];
    private readonly double[] scores = new double[2];
    private readonly double[] areas = new double[2];
    private readonly bool[] areaDirty = new bool[2];
    private readonly EnclosurePosition[][]?[] polygons = new EnclosurePosition[][]?[2];
    private int edgeCount;
    private GameFrame? frame;
    private SearchAreaCache? searchAreaCache;
    private bool searchMetadataReady;
    private readonly int[] searchNodeCounts = new int[2], searchEdgeCounts = new int[2];
    private readonly (ulong Low, ulong High)[] searchAreaKeys = new (ulong, ulong)[2];
    private (ulong Low, ulong High) searchKey;

    // One bounded cache per search call, shared only by that call's reusable simulation copies.
    // Protection, players and scores do not affect a color's polygon union.
    private sealed class SearchAreaCache
    {
        private const int Size = 8192;
        private struct Entry { public ulong Low, High; public double Area; public bool Present; }
        private readonly Entry[] entries = new Entry[Size];

        public bool TryGet((ulong Low, ulong High) key, out double area)
        {
            ref var entry = ref entries[(int)(key.Low & (Size - 1))];
            area = entry.Area;
            return entry.Present && entry.Low == key.Low && entry.High == key.High;
        }

        public void Store((ulong Low, ulong High) key, double area) =>
            entries[(int)(key.Low & (Size - 1))] = new() { Low = key.Low, High = key.High, Area = area, Present = true };
    }

    public int Turn { get; private set; }
    public int MoveNumber { get; private set; }
    public int ActionsRemaining { get; private set; }
    public bool Finished => MoveNumber >= LineLimit;
    public IReadOnlyList<double> Scores => scores;
    public IReadOnlyList<double> Areas { get { EnsureAreas(); return areas; } }
    public GameSpec Spec => GameSpec;
    public GameFrame Frame => frame ??= MakeFrame();

    public Enclosure() => Initialize();

    private void Initialize()
    {
        Turn = 0; MoveNumber = 0; ActionsRemaining = 1; edgeCount = 2; frame = null; searchAreaCache = null;
        searchMetadataReady = false;
        Array.Clear(nodes); Array.Clear(flags); Array.Clear(scores); Array.Clear(areas); Array.Clear(areaDirty);
        Array.Clear(polygons);
        edges[0] = GetActionId(0, 9, 3, 9); edges[1] = GetActionId(18, 9, 15, 9);
        owners[0] = 0; owners[1] = 1;
        foreach (var player in new[] { 0, 1 })
        {
            var segment = DecodeAction(edges[player]);
            nodes[player * 361 + EnclosureTables.Index(segment.From)] = true;
            nodes[player * 361 + EnclosureTables.Index(segment.To)] = true;
        }
    }

    public GameFrame Reset(ulong seed) { Initialize(); return Frame; }

    public static int GetActionId(int x1, int y1, int x2, int y2)
    {
        if ((uint)x1 >= BoardSize || (uint)y1 >= BoardSize || (uint)x2 >= BoardSize || (uint)y2 >= BoardSize)
            throw new ArgumentOutOfRangeException(nameof(x1), "Endpoints must be on the 19×19 board.");
        var action = EnclosureTables.Ids[(x1 * BoardSize + y1) * 361 + x2 * BoardSize + y2];
        if (action < 0) throw new ArgumentException("Choose distinct endpoints within the three-unit placement box.");
        return action;
    }

    public static EnclosureSegment DecodeAction(int actionId) => (uint)actionId < SegmentCount
        ? EnclosureTables.Segments[actionId] : throw new ArgumentOutOfRangeException(nameof(actionId));

    public bool HasNode(int player, EnclosurePoint point)
    {
        ValidatePlayer(player);
        return (uint)point.X < BoardSize && (uint)point.Y < BoardSize && nodes[player * 361 + EnclosureTables.Index(point)];
    }

    public EnclosurePoint[] Nodes(int player)
    {
        ValidatePlayer(player);
        var result = new List<EnclosurePoint>();
        for (var i = 0; i < 361; i++) if (nodes[player * 361 + i]) result.Add(EnclosureTables.Point(i));
        return result.ToArray();
    }

    public int NodeCount(int player)
    {
        ValidatePlayer(player);
        if (searchMetadataReady) return searchNodeCounts[player];
        var result = 0;
        for (var i = 0; i < 361; i++) if (nodes[player * 361 + i]) result++;
        return result;
    }

    public int SegmentCountFor(int player)
    {
        ValidatePlayer(player);
        if (searchMetadataReady) return searchEdgeCounts[player];
        var result = 0;
        for (var i = 0; i < edgeCount; i++) if (owners[i] == player) result++;
        return result;
    }

    public EnclosureSegment[] Segments(int player)
    {
        ValidatePlayer(player);
        var result = new List<EnclosureSegment>();
        for (var i = 0; i < edgeCount; i++)
            if (owners[i] == player) result.Add(DecodeAction(edges[i]) with { Invincible = (flags[i] & 1) != 0 });
        return result.ToArray();
    }

    public EnclosurePosition[][] Territories(int player)
    {
        ValidatePlayer(player);
        if (polygons[player] is null)
        {
            Span<int> actions = stackalloc int[Capacity];
            var count = GetEdges(player, actions);
            var result = EnclosureGeometry.Calculate(actions[..count]);
            areas[player] = result.Area; areaDirty[player] = false; polygons[player] = result.Territories;
        }
        return polygons[player]!.Select(polygon => (EnclosurePosition[])polygon.Clone()).ToArray();
    }

    /// <summary>Produces only action IDs; search does not allocate neural feature arrays.</summary>
    public int[] GenerateLegalActions()
    {
        if (Finished) return [];
        const int words = EnclosureTables.Words;
        Span<ulong> candidates = stackalloc ulong[words];
        Span<ulong> enemySeen = stackalloc ulong[words];
        var count = BuildLegalMasks(candidates, enemySeen, []);
        if (count == 0) return [PassActionId];
        var result = new int[count];
        WriteLegalActions(candidates, enemySeen, [], result, []);
        return result;
    }

    // Classification bits for legal actions: capture=1, possible closure=2, new endpoint=4.
    // Search owns and reuses the destination buffers; no feature or action arrays are allocated.
    internal int GenerateLegalActions(Span<int> destination, Span<byte> classifications)
    {
        if (Finished) return 0;
        const int words = EnclosureTables.Words;
        Span<ulong> candidates = stackalloc ulong[words];
        Span<ulong> captures = stackalloc ulong[words];
        Span<ulong> closures = stackalloc ulong[words];
        var count = BuildLegalMasks(candidates, captures, closures);
        var required = Math.Max(1, count);
        if (destination.Length < required || classifications.Length < required)
            throw new ArgumentException("The legal action and classification buffers are too small.");
        if (count == 0) { destination[0] = PassActionId; classifications[0] = 0; return 1; }
        WriteLegalActions(candidates, captures, closures, destination, classifications);
        return count;
    }

    // Capture-only horizon extension; no pass or quiet move is manufactured here.
    internal int GenerateLegalCaptures(Span<int> destination, Span<byte> classifications)
    {
        if (Finished) return 0;
        const int words = EnclosureTables.Words;
        Span<ulong> candidates = stackalloc ulong[words];
        Span<ulong> captures = stackalloc ulong[words];
        Span<ulong> closures = stackalloc ulong[words];
        BuildLegalMasks(candidates, captures, closures);
        var count = 0;
        for (var word = 0; word < words; word++)
        {
            candidates[word] &= captures[word];
            count += BitOperations.PopCount(candidates[word]);
        }
        if (destination.Length < count || classifications.Length < count)
            throw new ArgumentException("The capture buffers are too small.");
        WriteLegalActions(candidates, captures, closures, destination, classifications);
        return count;
    }

    internal int CapturedEdgeAction(int action)
    {
        for (var i = 0; i < edgeCount; i++)
            if (owners[i] != Turn && EnclosureTables.Intersects(edges[i], action)) return edges[i];
        return -1;
    }

    private int BuildLegalMasks(Span<ulong> candidates, Span<ulong> enemySeen, Span<ulong> closures)
    {
        const int words = EnclosureTables.Words;
        Span<ulong> forbidden = stackalloc ulong[words];
        candidates.Clear(); forbidden.Clear(); enemySeen.Clear(); closures.Clear();
        for (var point = 0; point < 361; point++)
        {
            if (!nodes[Turn * 361 + point]) continue;
            var offset = point * words;
            for (var word = 0; word < words; word++)
            {
                candidates[word] |= EnclosureTables.Incident[offset + word];
                forbidden[word] |= EnclosureTables.Through[offset + word];
            }
        }
        for (var edge = 0; edge < edgeCount; edge++)
        {
            var offset = edges[edge] * words;
            if (owners[edge] == Turn)
            {
                if (closures.IsEmpty)
                    for (var word = 0; word < words; word++) forbidden[word] |= EnclosureTables.OwnConflicts[offset + word];
                else
                    for (var word = 0; word < words; word++)
                    {
                        forbidden[word] |= EnclosureTables.OwnConflicts[offset + word];
                        closures[word] |= EnclosureTables.OwnCrossings[offset + word];
                    }
            }
            else
            {
                var protectedEdge = (flags[edge] & 1) != 0;
                for (var word = 0; word < words; word++)
                {
                    var intersections = EnclosureTables.Intersections[offset + word];
                    forbidden[word] |= protectedEdge ? intersections : enemySeen[word] & intersections;
                    enemySeen[word] |= intersections;
                }
            }
        }
        var count = 0;
        for (var word = 0; word < words; word++) { candidates[word] &= ~forbidden[word]; count += BitOperations.PopCount(candidates[word]); }
        return count;
    }

    private void WriteLegalActions(ReadOnlySpan<ulong> candidates, ReadOnlySpan<ulong> captures,
        ReadOnlySpan<ulong> closures, Span<int> result, Span<byte> classifications)
    {
        var index = 0;
        for (var word = 0; word < candidates.Length; word++)
        {
            var value = candidates[word];
            while (value != 0)
            {
                var bit = BitOperations.TrailingZeroCount(value);
                var action = word * 64 + bit;
                result[index] = action;
                if (!classifications.IsEmpty)
                {
                    var edge = EnclosureTables.Segments[action];
                    var bothNodes = nodes[Turn * 361 + EnclosureTables.Index(edge.From)] &&
                        nodes[Turn * 361 + EnclosureTables.Index(edge.To)];
                    var mask = 1UL << bit;
                    classifications[index] = (byte)(((captures[word] & mask) != 0 ? 1 : 0) |
                        (bothNodes || (closures[word] & mask) != 0 ? 2 : 0) | (bothNodes ? 0 : 4));
                }
                index++;
                value &= value - 1;
            }
        }
    }

    public bool IsLegal(int actionId)
    {
        if (Finished) return false;
        if (actionId == PassActionId) return GenerateLegalActions()[0] == PassActionId;
        if ((uint)actionId >= SegmentCount) return false;
        var segment = DecodeAction(actionId);
        if (!HasNode(Turn, segment.From) && !HasNode(Turn, segment.To)) return false;
        for (var point = 0; point < 361; point++)
            if (nodes[Turn * 361 + point] && Test(EnclosureTables.Through, point, actionId)) return false;
        var intersections = 0;
        for (var i = 0; i < edgeCount; i++)
        {
            if (owners[i] == Turn)
            {
                if (Test(EnclosureTables.OwnConflicts, edges[i], actionId)) return false;
            }
            else if (EnclosureTables.Intersects(edges[i], actionId) && ((flags[i] & 1) != 0 || ++intersections > 1)) return false;
        }
        return true;
    }

    public bool IsCapture(int actionId)
    {
        if ((uint)actionId >= SegmentCount) return false;
        for (var i = 0; i < edgeCount; i++)
            if (owners[i] != Turn && EnclosureTables.Intersects(edges[i], actionId)) return true;
        return false;
    }

    /// <summary>Cheap move ordering signal; exact enclosed area is evaluated after playing.</summary>
    public bool IsClosure(int actionId)
    {
        if ((uint)actionId >= SegmentCount) return false;
        var segment = DecodeAction(actionId);
        if (HasNode(Turn, segment.From) && HasNode(Turn, segment.To)) return true;
        // Crossing an existing own line away from the starting node can also close a face.
        var start = HasNode(Turn, segment.From) ? segment.From : segment.To;
        for (var i = 0; i < edgeCount; i++)
        {
            if (owners[i] != Turn || !EnclosureTables.Intersects(edges[i], actionId)) continue;
            var other = DecodeAction(edges[i]);
            if (!EnclosureTables.On(start, other.From, other.To)) return true;
        }
        return false;
    }

    public void Play(int actionId)
    {
        if (!IsLegal(actionId)) throw new ArgumentException($"Illegal Enclosure action {actionId} at move {MoveNumber + 1}.");
        PlayGenerated(actionId);
    }

    // Only for engines that obtained the action from this exact state's legal action list.
    internal void PlayGenerated(int actionId) => PlayGeneratedCore(actionId, IsClosure(actionId));

    internal void PlayGenerated(int actionId, byte classification) =>
        PlayGeneratedCore(actionId, (classification & 2) != 0);

    private void PlayGeneratedCore(int actionId, bool possibleClosure)
    {
        frame = null;
        if (searchMetadataReady) ToggleSearchKey(CounterKey());
        if (actionId == PassActionId)
        {
            MoveNumber += ActionsRemaining; ActionsRemaining = 0; FinishTurn();
            if (searchMetadataReady) ToggleSearchKey(CounterKey());
            return;
        }
        var mover = Turn; var enemy = 1 - mover;
        for (var i = 0; i < edgeCount; i++)
        {
            if (owners[i] != enemy || !EnclosureTables.Intersects(edges[i], actionId)) continue;
            var captured = DecodeAction(edges[i]);
            ChangeEdgeMetadata(edges[i], enemy, flags[i], -1);
            // Stable chronological slot order makes exported observations deterministic.
            Array.Copy(edges, i + 1, edges, i, edgeCount - i - 1);
            Array.Copy(owners, i + 1, owners, i, edgeCount - i - 1);
            Array.Copy(flags, i + 1, flags, i, edgeCount - i - 1);
            edgeCount--;
            RemoveIsolated(enemy, captured.From); RemoveIsolated(enemy, captured.To);
            areaDirty[enemy] = true; polygons[enemy] = null;
            break;
        }
        var edge = DecodeAction(actionId);
        // A new dangling edge cannot change area, saving the planar calculation in most openings.
        if (possibleClosure) { areaDirty[mover] = true; polygons[mover] = null; }
        edges[edgeCount] = actionId; owners[edgeCount] = (byte)mover; flags[edgeCount++] = 3;
        ChangeEdgeMetadata(actionId, mover, 3, 1);
        SetNode(mover, edge.From, true); SetNode(mover, edge.To, true);
        MoveNumber++; ActionsRemaining--;
        if (ActionsRemaining == 0 || Finished) FinishTurn();
        if (searchMetadataReady) ToggleSearchKey(CounterKey());
    }

    private void RemoveIsolated(int player, EnclosurePoint point)
    {
        for (var i = 0; i < edgeCount; i++)
        {
            if (owners[i] != player) continue;
            var edge = DecodeAction(edges[i]);
            if (EnclosureTables.On(point, edge.From, edge.To)) return;
        }
        SetNode(player, point, false);
    }

    private void SetNode(int player, EnclosurePoint point, bool present)
    {
        var index = player * 361 + EnclosureTables.Index(point);
        if (nodes[index] == present) return;
        nodes[index] = present;
        if (!searchMetadataReady) return;
        searchNodeCounts[player] += present ? 1 : -1;
        ToggleSearchKey(EnclosureTables.NodeKeys[index]);
    }

    private void ChangeEdgeMetadata(int action, int player, byte protection, int delta)
    {
        if (!searchMetadataReady) return;
        ToggleSearchKey(EnclosureTables.EdgeKeys[action * 8 + player * 4 + protection]);
        searchEdgeCounts[player] += delta;
        var token = EnclosureTables.AreaEdgeKeys[action];
        searchAreaKeys[player].Low ^= token.Low; searchAreaKeys[player].High ^= token.High;
    }

    private void FinishTurn()
    {
        for (var i = 0; i < edgeCount; i++)
        {
            var next = (byte)((flags[i] & 2) != 0 ? 1 : 0);
            if (searchMetadataReady && flags[i] != next)
            {
                var offset = edges[i] * 8 + owners[i] * 4;
                ToggleSearchKey(EnclosureTables.EdgeKeys[offset + flags[i]]);
                ToggleSearchKey(EnclosureTables.EdgeKeys[offset + next]);
            }
            flags[i] = next;
        }
        EnsureAreas();
        for (var player = 0; player < 2; player++)
        {
            var next = EnclosureGeometry.Round(scores[player] + areas[player]);
            var before = BitConverter.DoubleToUInt64Bits(scores[player]); var after = BitConverter.DoubleToUInt64Bits(next);
            if (searchMetadataReady && before != after)
            {
                var prefix = player == 0 ? 0x200000UL : 0x300000UL;
                ToggleSearchKey(EnclosureTables.StateKeyToken(prefix + before));
                ToggleSearchKey(EnclosureTables.StateKeyToken(prefix + after));
            }
            scores[player] = next;
        }
        Turn = 1 - Turn; ActionsRemaining = Math.Min(2, LineLimit - MoveNumber);
    }

    public GameFrame Step(IReadOnlyDictionary<int, int> actions)
    {
        if (actions.Count != 1 || !actions.TryGetValue(Turn, out var action))
            throw new ArgumentException("A move must name the acting player and one legal segment.");
        Play(action); return Frame;
    }

    public Enclosure Copy()
    {
        var result = new Enclosure(); result.CopyFrom(this); return result;
    }

    public void CopyFrom(Enclosure other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Turn = other.Turn; MoveNumber = other.MoveNumber; ActionsRemaining = other.ActionsRemaining; edgeCount = other.edgeCount;
        Array.Copy(other.edges, edges, edgeCount); Array.Copy(other.owners, owners, edgeCount); Array.Copy(other.flags, flags, edgeCount);
        Array.Copy(other.nodes, nodes, nodes.Length); Array.Copy(other.scores, scores, 2); Array.Copy(other.areas, areas, 2);
        Array.Copy(other.areaDirty, areaDirty, 2); Array.Copy(other.polygons, polygons, 2);
        frame = null; searchAreaCache = other.searchAreaCache;
        searchMetadataReady = other.searchMetadataReady; searchKey = other.searchKey;
        if (searchMetadataReady)
        {
            Array.Copy(other.searchNodeCounts, searchNodeCounts, 2); Array.Copy(other.searchEdgeCounts, searchEdgeCounts, 2);
            Array.Copy(other.searchAreaKeys, searchAreaKeys, 2);
        }
    }

    public ISearchableGame Fork() => Copy();

    internal void EnableSearchAreaCache()
    {
        searchMetadataReady = false;
        Array.Clear(searchNodeCounts); Array.Clear(searchEdgeCounts); Array.Clear(searchAreaKeys);
        searchKey = GetSearchKey();
        for (var i = 0; i < nodes.Length; i++) if (nodes[i]) searchNodeCounts[i / 361]++;
        for (var i = 0; i < edgeCount; i++)
        {
            var player = owners[i]; var token = EnclosureTables.AreaEdgeKeys[edges[i]];
            searchEdgeCounts[player]++;
            searchAreaKeys[player].Low ^= token.Low; searchAreaKeys[player].High ^= token.High;
        }
        searchMetadataReady = true;
        searchAreaCache = new();
        EnsureAreas();
        for (var player = 0; player < 2; player++)
            searchAreaCache.Store(SearchAreaKey(player), areas[player]);
    }

    private static (ulong Low, ulong High) AreaKey(ReadOnlySpan<int> actions)
    {
        var (low, high) = EnclosureTables.AreaCountKeys[actions.Length];
        foreach (var action in actions)
        {
            var token = EnclosureTables.AreaEdgeKeys[action];
            low ^= token.Low; high ^= token.High;
        }
        return (low, high);
    }

    private (ulong Low, ulong High) SearchAreaKey(int player)
    {
        var count = EnclosureTables.AreaCountKeys[searchEdgeCounts[player]];
        return (searchAreaKeys[player].Low ^ count.Low, searchAreaKeys[player].High ^ count.High);
    }

    internal (ulong Low, ulong High) StrategyTopologyKey(int player)
    {
        if (searchMetadataReady) return SearchAreaKey(player);
        Span<int> actions = stackalloc int[Capacity];
        var count = GetEdges(player, actions);
        return AreaKey(actions[..count]);
    }

    internal int CopyStrategyEdges(int player, Span<int> destination) => GetEdges(player, destination);

    internal int ProtectedEdgeCount(int player)
    {
        var count = 0;
        for (var i = 0; i < edgeCount; i++) if (owners[i] == player && (flags[i] & 1) != 0) count++;
        return count;
    }

    private (ulong Low, ulong High) CounterKey()
    {
        var counter = MoveNumber * 8 + ActionsRemaining * 2 + Turn;
        return (uint)counter < EnclosureTables.CounterKeys.Length ? EnclosureTables.CounterKeys[counter] :
            EnclosureTables.StateKeyToken(0x100000UL + (ulong)counter);
    }

    private void ToggleSearchKey((ulong Low, ulong High) token)
    { searchKey.Low ^= token.Low; searchKey.High ^= token.High; }

    public (ulong Low, ulong High) GetSearchKey()
    {
        if (searchMetadataReady) return searchKey;
        ulong low = 0, high = 0;
        void Add((ulong Low, ulong High) token) { low ^= token.Low; high ^= token.High; }
        Add(CounterKey());
        Add(EnclosureTables.StateKeyToken(0x200000UL + BitConverter.DoubleToUInt64Bits(scores[0])));
        Add(EnclosureTables.StateKeyToken(0x300000UL + BitConverter.DoubleToUInt64Bits(scores[1])));
        for (var i = 0; i < nodes.Length; i++) if (nodes[i]) Add(EnclosureTables.NodeKeys[i]);
        for (var i = 0; i < edgeCount; i++) Add(EnclosureTables.EdgeKeys[edges[i] * 8 + owners[i] * 4 + flags[i]]);
        return (low, high);
    }

    public float[] Observe(int player)
    {
        ValidatePlayer(player); EnsureAreas();
        var result = new float[GameSpec.ObservationSize];
        for (var i = 0; i < edgeCount; i++)
        {
            var segment = Relative(DecodeAction(edges[i]), player); var offset = i * 6;
            result[offset] = segment.From.X / 18f; result[offset + 1] = segment.From.Y / 18f;
            result[offset + 2] = segment.To.X / 18f; result[offset + 3] = segment.To.Y / 18f;
            result[offset + 4] = owners[i] == player ? 1 : -1;
            result[offset + 5] = (flags[i] & 1) != 0 ? 1 : 0;
        }
        for (var color = 0; color < 2; color++)
        for (var point = 0; point < 361; point++)
            if (nodes[color * 361 + point]) result[Capacity * 6 + (color == player ? 0 : 361) + (player == 0 ? point : 360 - point)] = 1;
        const int globals = Capacity * 6 + 722;
        result[globals] = (float)(scores[player] / (324 * 61));
        result[globals + 1] = (float)(scores[1 - player] / (324 * 61));
        result[globals + 2] = (float)(areas[player] / 324);
        result[globals + 3] = (float)(areas[1 - player] / 324);
        result[globals + 4] = MoveNumber / 120f; result[globals + 5] = ActionsRemaining / 2f;
        result[globals + 6] = Turn == player ? 1 : -1;
        return result;
    }

    private float[] ActionFeatures(int action)
    {
        var features = new float[GameSpec.ActionFeatureSize];
        if (action == PassActionId) { features[11] = 1; return features; }
        var raw = DecodeAction(action); var edge = Relative(raw, Turn);
        features[0] = edge.From.X / 18f; features[1] = edge.From.Y / 18f;
        features[2] = edge.To.X / 18f; features[3] = edge.To.Y / 18f;
        features[4] = (edge.To.X - edge.From.X) / 3f; features[5] = (edge.To.Y - edge.From.Y) / 3f;
        features[6] = MathF.Sqrt(features[4] * features[4] + features[5] * features[5]) / MathF.Sqrt(2);
        features[7] = HasNode(Turn, Turn == 0 ? raw.From : raw.To) ? 1 : 0;
        features[8] = HasNode(Turn, Turn == 0 ? raw.To : raw.From) ? 1 : 0;
        features[9] = IsCapture(action) ? 1 : 0; features[10] = IsClosure(action) ? 1 : 0;
        return features;
    }

    private static EnclosureSegment Relative(EnclosureSegment segment, int player) => player == 0 ? segment :
        segment with { From = new(18 - segment.To.X, 18 - segment.To.Y), To = new(18 - segment.From.X, 18 - segment.From.Y) };

    private GameFrame MakeFrame()
    {
        if (Finished)
        {
            var value = Math.Sign(scores[0] - scores[1]);
            return new([], [value, -value], true, false, [value, -value]);
        }
        var actions = GenerateLegalActions().Select(action => new LegalAction(action, ActionFeatures(action))).ToArray();
        return new([new(Turn, Observe(Turn), actions)], [0, 0], false, false, [0, 0]);
    }

    private int GetEdges(int player, Span<int> destination)
    {
        var count = 0;
        for (var i = 0; i < edgeCount; i++) if (owners[i] == player) destination[count++] = edges[i];
        return count;
    }

    private void EnsureAreas()
    {
        Span<int> actions = stackalloc int[Capacity];
        for (var player = 0; player < 2; player++)
        {
            if (!areaDirty[player]) continue;
            if (searchAreaCache is null) areas[player] = EnclosureGeometry.Area(actions[..GetEdges(player, actions)]);
            else
            {
                var key = searchMetadataReady ? SearchAreaKey(player) : AreaKey(actions[..GetEdges(player, actions)]);
                if (!searchAreaCache.TryGet(key, out areas[player]))
                {
                    areas[player] = EnclosureGeometry.Area(actions[..GetEdges(player, actions)]);
                    searchAreaCache.Store(key, areas[player]);
                }
            }
            areaDirty[player] = false;
        }
    }

    private static bool Test(ulong[] masks, int row, int action) =>
        (masks[row * EnclosureTables.Words + (action >> 6)] & (1UL << (action & 63))) != 0;
    private static void ValidatePlayer(int player)
    {
        if ((uint)player > 1) throw new ArgumentOutOfRangeException(nameof(player));
    }
}
