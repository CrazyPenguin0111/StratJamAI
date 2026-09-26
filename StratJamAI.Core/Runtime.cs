using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StratJamAI.Core;

// SplitMix64: explicit, serializable state makes sampling independent of .NET's Random implementation.
public sealed class RandomSource(ulong state)
{
    public ulong State { get; set; } = state;
    public ulong NextUInt64()
    {
        var z = State += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    public int NextInt(int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var bound = (ulong)max;
        var threshold = unchecked(0UL - bound) % bound;
        ulong value;
        do { value = NextUInt64(); } while (value < threshold);
        return (int)(value % bound);
    }
    public void Shuffle<T>(T[] values)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = NextInt(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
    public int Sample(ReadOnlySpan<float> probabilities)
    {
        double sum = 0;
        foreach (var p in probabilities)
        {
            if (!float.IsFinite(p) || p < 0) throw new InvalidDataException("Invalid action probability.");
            sum += p;
        }
        if (sum <= 0) throw new InvalidDataException("The action distribution has zero mass.");
        var draw = NextDouble() * sum;
        for (var i = 0; i < probabilities.Length; i++)
            if ((draw -= probabilities[i]) < 0) return i;
        return probabilities.Length - 1;
    }
}

public readonly record struct Deadline(long Timestamp)
{
    public static Deadline After(TimeSpan duration) => new(Stopwatch.GetTimestamp() +
        (long)(Math.Max(0, duration.TotalSeconds) * Stopwatch.Frequency));
    public static Deadline Never => new(long.MaxValue);
    public bool Expired => Stopwatch.GetTimestamp() >= Timestamp;
    public double RemainingSeconds => Math.Max(0, (Timestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
    public Deadline Limit(TimeSpan duration) => new(Math.Min(Timestamp, After(duration).Timestamp));
    public void ThrowIfExpired()
    {
        if (Expired) throw new OperationCanceledException("The time budget has expired.");
    }
}

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Empty JSON file: {path}");
    public static void WriteAtomic<T>(string path, T value)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
