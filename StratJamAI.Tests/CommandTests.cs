using System.Diagnostics;
using StratJamAI.Core;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;
using StratJamAI.Training;
using Xunit;

namespace StratJamAI.Tests;

public sealed class CommandTests
{
    [Fact]
    public async Task ShortRunCompletesAndProducesAnEvaluatedPortableBot()
    {
        using var directory = new TemporaryDirectory();
        var run = Path.Combine(directory.Path, "run");
        var watch = Stopwatch.StartNew();
        var result = await Run("train", "--device", "cpu", "--minutes", "0.2", "--output", run,
            "--envs", "4", "--rollout", "64", "--batch", "32", "--epochs", "1", "--no-warmup",
            "--pairs", "2", "--simulations", "32", "--move-ms", "5");
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(watch.Elapsed.TotalSeconds < 15, result.Output);
        var artifact = BotArtifact.Load(Path.Combine(run, "best.bot.json"), TicTacToe.GameSpec);
        var checkpoint = Checkpoints.Load(run, TicTacToe.GameSpec);
        Assert.True(checkpoint.State.Decisions > 0);
        Assert.True(checkpoint.State.Updates > 0);
        Assert.Equal("completed", JsonFiles.Read<SupervisorState>(Path.Combine(run, "supervisor.json")).Status);
        Assert.NotNull(artifact.CreateBot(new TicTacToeDefinition()));
        var export = Path.Combine(directory.Path, "export.json");
        Assert.Equal(0, (await Run("export", "--run", run, "--output", export)).ExitCode);
        Assert.Equal(artifact.Weights.Policy2.Weight, BotArtifact.Load(export, TicTacToe.GameSpec).Weights.Policy2.Weight);
        var original = JsonFiles.Read<TrainingConfig>(Path.Combine(run, "config.json"));
        JsonFiles.WriteAtomic(Path.Combine(run, "config.json"), original with { Gamma = 0.5f });
        var incompatible = await Run("train", "--resume", run, "--minutes", "0.3");
        Assert.Equal(1, incompatible.ExitCode);
        Assert.Contains("differs from the saved checkpoint", incompatible.Output);
    }

    [Fact]
    public async Task SupervisorStopsAWorkerAtTheBudgetAndKeepsItsBaseline()
    {
        using var directory = new TemporaryDirectory();
        var run = Path.Combine(directory.Path, "run");
        var watch = Stopwatch.StartNew();
        var result = await Run("train", "--device", "cpu", "--minutes", "0.01", "--output", run);
        Assert.True(result.ExitCode is 0 or 124, result.Output);
        Assert.True(watch.Elapsed.TotalSeconds < 4, result.Output);
        Assert.NotNull(BotArtifact.Load(Path.Combine(run, "best.bot.json"), TicTacToe.GameSpec));
        var status = JsonFiles.Read<SupervisorState>(Path.Combine(run, "supervisor.json"));
        Assert.True(status.ConsumedSeconds < 2);
    }

    [Fact]
    public async Task UnknownOptionsFailBeforeCreatingARun()
    {
        var result = await Run("train", "--minute", "5");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown option --minute", result.Output);
    }

    private static async Task<(int ExitCode, string Output)> Run(params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(TrainingConfig).Assembly.Location);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
}
