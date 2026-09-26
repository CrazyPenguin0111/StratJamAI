using System.Text.Json;
using System.Text.Json.Serialization;

namespace StratJamAI.Core.Games;

public sealed record EnclosureHistoryMove
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? From { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? To { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Pass { get; init; }

    public int ActionId()
    {
        if (Pass)
        {
            if (From is not null || To is not null) throw new InvalidDataException("A pass cannot contain endpoints.");
            return Enclosure.PassActionId;
        }
        if (From is not { Length: 2 } || To is not { Length: 2 })
            throw new InvalidDataException("A move requires from/to coordinate pairs.");
        return Enclosure.GetActionId(From[0], From[1], To[0], To[1]);
    }

    public static EnclosureHistoryMove FromAction(int action)
    {
        if (action == Enclosure.PassActionId) return new() { Pass = true };
        var segment = Enclosure.DecodeAction(action);
        return new() { From = [segment.From.X, segment.From.Y], To = [segment.To.X, segment.To.Y] };
    }
}

/// <summary>Replay, rather than a board drawing, preserves cumulative scores and line protection.</summary>
public sealed record EnclosureHistory
{
    public int FormatVersion { get; init; } = 1;
    public string Game { get; init; } = "enclosure";
    public EnclosureHistoryMove[] Moves { get; init; } = [];

    public static EnclosureHistory Parse(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<EnclosureHistory>(json, JsonFiles.Options)
                ?? throw new InvalidDataException("The game history is empty.");
            result.ValidateHeader();
            return result;
        }
        catch (JsonException e) { throw new InvalidDataException("Invalid game history JSON: " + e.Message, e); }
    }

    public static EnclosureHistory FromActions(IEnumerable<int> actions) => new()
    {
        Moves = actions.Select(EnclosureHistoryMove.FromAction).ToArray()
    };
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonFiles.Options) { WriteIndented = true });

    private void ValidateHeader()
    {
        if (FormatVersion != 1 || Game != "enclosure") throw new InvalidDataException("Unsupported history version or game; expected Enclosure version 1.");
        if (Moves is null || Moves.Length > 120) throw new InvalidDataException("A game history must contain at most 120 moves.");
    }

    public int[] ActionIds()
    {
        ValidateHeader();
        var actions = new int[Moves.Length];
        for (var i = 0; i < actions.Length; i++)
            try { actions[i] = (Moves[i] ?? throw new InvalidDataException("Empty move.")).ActionId(); }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or IndexOutOfRangeException)
            { throw new InvalidDataException($"Invalid history at move {i + 1}: {e.Message}", e); }
        return actions;
    }

    public Enclosure Replay()
    {
        var actions = ActionIds();
        var game = new Enclosure();
        for (var i = 0; i < actions.Length; i++)
            try { game.Play(actions[i]); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            { throw new InvalidDataException($"Invalid history at move {i + 1}: {e.Message}", e); }
        return game;
    }
}
