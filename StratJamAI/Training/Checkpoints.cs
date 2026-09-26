using System.Security.Cryptography;
using StratJamAI.Core;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;
using StratJamAI.Learning;

namespace StratJamAI.Training;

public sealed record CheckpointState(int FormatVersion, GameSpec Game, TrainingConfig Config, NetworkWeights Weights,
    NetworkWeights BestPolicy, double BestPolicyScore, ulong RandomState, ulong[] EnvironmentRandomStates,
    NetworkWeights[] Opponents, long Decisions, int Updates, double ConsumedSeconds, bool WarmStartComplete, int Workers);
public sealed record CheckpointPointer(string Directory, double ConsumedSeconds, long Decisions);
public sealed record CheckpointHashes(string StateSha256, string OptimizerSha256);
public sealed record LoadedCheckpoint(CheckpointState State, string OptimizerPath);

public static class Checkpoints
{
    public static void Save(string runDirectory, CheckpointState state, PpoOptimizer optimizer)
    {
        var root = Path.Combine(runDirectory, "checkpoints");
        Directory.CreateDirectory(root);
        var name = $"checkpoint-{DateTime.UtcNow.Ticks}-{Guid.NewGuid():N}";
        var temp = Path.Combine(root, "." + name + ".tmp");
        var destination = Path.Combine(root, name);
        Directory.CreateDirectory(temp);
        try
        {
            optimizer.Save(Path.Combine(temp, "optimizer.bin"));
            JsonFiles.WriteAtomic(Path.Combine(temp, "state.json"), state);
            JsonFiles.WriteAtomic(Path.Combine(temp, "hashes.json"), new CheckpointHashes(
                Hash(Path.Combine(temp, "state.json")), Hash(Path.Combine(temp, "optimizer.bin"))));
            Directory.Move(temp, destination);
            JsonFiles.WriteAtomic(Path.Combine(runDirectory, "checkpoint.json"),
                new CheckpointPointer(name, state.ConsumedSeconds, state.Decisions));
            // Keep the preceding complete generation for recovery, never delete arbitrary user paths.
            foreach (var old in Directory.GetDirectories(root, "checkpoint-*").OrderDescending().Skip(2))
                Directory.Delete(old, true);
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    public static LoadedCheckpoint Load(string runDirectory, GameSpec expected)
    {
        var root = Path.Combine(runDirectory, "checkpoints");
        if (!Directory.Exists(root)) throw new FileNotFoundException("No resumable checkpoint exists in this run.");
        var candidates = new List<string>();
        var pointerPath = Path.Combine(runDirectory, "checkpoint.json");
        if (File.Exists(pointerPath))
        {
            try
            {
                var pointer = JsonFiles.Read<CheckpointPointer>(pointerPath);
                if (Path.GetFileName(pointer.Directory) == pointer.Directory && pointer.Directory.StartsWith("checkpoint-", StringComparison.Ordinal))
                    candidates.Add(Path.Combine(root, pointer.Directory));
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException) { }
        }
        candidates.AddRange(Directory.GetDirectories(root, "checkpoint-*").OrderDescending());
        foreach (var candidate in candidates.Distinct())
        {
            try
            {
                var hashes = JsonFiles.Read<CheckpointHashes>(Path.Combine(candidate, "hashes.json"));
                var stateFile = Path.Combine(candidate, "state.json");
                var optimizerFile = Path.Combine(candidate, "optimizer.bin");
                if (Hash(stateFile) != hashes.StateSha256 || Hash(optimizerFile) != hashes.OptimizerSha256) continue;
                var state = JsonFiles.Read<CheckpointState>(stateFile);
                if (state.FormatVersion != 1 || state.Game != expected)
                    throw new InvalidDataException("Checkpoint format or game schema mismatch.");
                state.Config.Validate();
                state.Weights.Validate(expected);
                state.BestPolicy.Validate(expected);
                return new(state, optimizerFile);
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        }
        throw new InvalidDataException("No intact, compatible checkpoint generation was found.");
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
