using System.Diagnostics;
using StratJamAI.Core;
using StratJamAI.Core.Bots;

namespace StratJamAI.Training;

public sealed record TrainingConfig
{
    public string Game { get; init; } = "tic-tac-toe";
    public string Device { get; init; } = "cuda";
    public double Minutes { get; init; } = 60;
    public ulong Seed { get; init; } = 1337;
    public int Environments { get; init; } = 64;
    public int RolloutDecisions { get; init; } = 8192;
    public int Epochs { get; init; } = 4;
    public int MinibatchSize { get; init; } = 512;
    public int Workers { get; init; } = 0; // 0 benchmarks 1, 4, 8 and 16 workers.
    public int HiddenSize { get; init; } = 128;
    public int ActionHiddenSize { get; init; } = 64;
    public float LearningRate { get; init; } = 0.0003f;
    public float Gamma { get; init; } = 0.99f;
    public float GaeLambda { get; init; } = 0.95f;
    public float Clip { get; init; } = 0.2f;
    public float EntropyWeight { get; init; } = 0.01f;
    public float ValueWeight { get; init; } = 0.5f;
    public float GradientClip { get; init; } = 0.5f;
    public float TargetKl { get; init; } = 0.02f;
    public bool WarmStart { get; init; } = true;
    public int WarmStartUpdates { get; init; } = 128;
    public int MaxEpisodeSteps { get; init; } = 512;
    public int BufferMemoryMiB { get; init; } = 512;
    public int SnapshotCount { get; init; } = 4;
    public double CheckpointSeconds { get; init; } = 30;
    public double EvaluationSeconds { get; init; } = 60;
    public int EvaluationPairs { get; init; } = 16;
    public SearchOptions Search { get; init; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Game) || Device is not ("cuda" or "cpu") || !double.IsFinite(Minutes) || Minutes <= 0 ||
            Environments < 1 || RolloutDecisions < Environments || Epochs < 1 || MinibatchSize < 1 || Workers < 0 ||
            HiddenSize < 1 || ActionHiddenSize < 1 || MaxEpisodeSteps < 1 || BufferMemoryMiB < 1 || SnapshotCount < 1 ||
            EvaluationPairs < 1 || WarmStartUpdates < 0 || !double.IsFinite(CheckpointSeconds) || CheckpointSeconds <= 0 ||
            !double.IsFinite(EvaluationSeconds) || EvaluationSeconds <= 0)
            throw new ArgumentException("Invalid training dimensions, device, time budget, or interval.");
        foreach (var x in new[] { LearningRate, Gamma, GaeLambda, Clip, EntropyWeight, ValueWeight, GradientClip, TargetKl })
            if (!float.IsFinite(x)) throw new ArgumentException("Hyperparameters must be finite.");
        if (LearningRate <= 0 || Gamma is < 0 or > 1 || GaeLambda is < 0 or > 1 || Clip is <= 0 or >= 1 ||
            EntropyWeight < 0 || ValueWeight < 0 || GradientClip <= 0 || TargetKl <= 0)
            throw new ArgumentException("Invalid PPO hyperparameters.");
        Search.Validate();
    }
}

public sealed class RunBudget(double totalSeconds, double previouslyConsumed, long startTimestamp)
{
    public double TotalSeconds { get; } = totalSeconds;
    public double Consumed => previouslyConsumed + Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
    public Deadline End => AtFraction(1);
    public Deadline AtFraction(double fraction) => new(startTimestamp +
        (long)((TotalSeconds * fraction - previouslyConsumed) * Stopwatch.Frequency));
}
