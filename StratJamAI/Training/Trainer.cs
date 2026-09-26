using System.Diagnostics;
using System.Text.Json;
using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using StratJamAI.Core.Learning;
using StratJamAI.Learning;

namespace StratJamAI.Training;

public static class Trainer
{
    public static int Run(TrainingConfig config, string runDirectory, RunBudget budget, bool resume)
    {
        config.Validate();
        var definition = GameRegistry.Get(config.Game);
        var loaded = resume ? Checkpoints.Load(runDirectory, definition.Spec) : null;
        var random = new RandomSource(loaded?.State.RandomState ?? config.Seed);
        var weights = loaded?.State.Weights ?? NetworkWeights.Create(definition.Spec, random, config.HiddenSize, config.ActionHiddenSize);
        var bestPolicy = loaded?.State.BestPolicy ?? weights;
        var bestPolicyScore = loaded?.State.BestPolicyScore ?? -1;
        var decisions = loaded?.State.Decisions ?? 0;
        var updates = loaded?.State.Updates ?? 0;
        var warmStartComplete = loaded?.State.WarmStartComplete ?? false;
        var pool = new OpponentPool(definition, config.SnapshotCount);
        foreach (var snapshot in loaded?.State.Opponents ?? [weights]) pool.Add(snapshot);
        var opponents = Evaluation.FixedOpponents(definition);
        BotArtifact Artifact(NetworkWeights model, string kind, string description) => new(BotArtifact.CurrentFormatVersion,
            definition.Spec, model, kind, config.Search, DateTimeOffset.UtcNow, decisions, description);
        var bestPath = Path.Combine(runDirectory, "best.bot.json");
        // A legal baseline exists before native initialization or any training operation.
        if (!resume || !File.Exists(bestPath)) JsonFiles.WriteAtomic(bestPath,
            Artifact(weights, definition.CreateTeacher() is null ? "random" : "tactical", "Initial legal baseline; no strength claim."));
        JsonFiles.WriteAtomic(Path.Combine(runDirectory, "latest.bot.json"), Artifact(weights, "policy", "Latest network."));

        Console.WriteLine($"preflight: {config.Device}, {config.Environments} environments, {budget.TotalSeconds:F1}s total budget");
        var doctor = Diagnostics.Doctor(config.Device);
        JsonFiles.WriteAtomic(Path.Combine(runDirectory, "doctor.json"), doctor);
        var workers = config.Workers > 0 ? config.Workers : loaded?.State.Workers ??
            Diagnostics.Calibrate(definition, config.Environments, budget.AtFraction(2.0 / 60), Console.WriteLine);
        using var network = new TorchNetwork(definition.Spec, weights, Devices.Select(config.Device))
            { MaxBatchBytes = config.BufferMemoryMiB * 1024L * 1024L };
        using var optimizer = new PpoOptimizer(network, config);
        if (loaded is not null) optimizer.Load(loaded.OptimizerPath);
        var collector = new RolloutCollector(definition, config, pool, workers, loaded?.State.EnvironmentRandomStates);
        var initialEvaluation = resume ? null : Evaluation.Run(definition, Artifact(weights, "policy", "Initial policy.").CreateBot(definition),
            opponents, config.EvaluationPairs, 900001, budget.AtFraction(2.0 / 60), config.Search.MoveMilliseconds, config.MaxEpisodeSteps);
        if (initialEvaluation is not null)
        {
            JsonFiles.WriteAtomic(Path.Combine(runDirectory, "initial-policy.evaluation.json"), initialEvaluation);
            if (initialEvaluation.Complete) bestPolicyScore = initialEvaluation.MeanScore;
        }

        void SaveCheckpoint()
        {
            weights = network.ExportWeights();
            var state = new CheckpointState(1, definition.Spec, config, weights, bestPolicy, bestPolicyScore, random.State,
                collector.RandomStates, pool.Snapshots.ToArray(), decisions, updates, budget.Consumed, warmStartComplete, workers);
            Checkpoints.Save(runDirectory, state, optimizer);
            JsonFiles.WriteAtomic(Path.Combine(runDirectory, "latest.bot.json"), Artifact(weights, "policy", "Latest training network."));
            JsonFiles.WriteAtomic(Path.Combine(runDirectory, "best-policy.bot.json"), Artifact(bestPolicy, "policy", "Best validated network."));
        }

        if (!warmStartComplete && config.WarmStart && definition.CreateTeacher() is { } teacher)
        {
            var warmDeadline = budget.AtFraction(5.0 / 60);
            Console.WriteLine("warm start: imitation from the tactical teacher");
            for (var update = 0; update < config.WarmStartUpdates && !warmDeadline.Expired; update++)
            {
                var examples = new List<DecisionRequest>();
                var targets = new List<int>();
                var game = definition.Create();
                game.Reset(random.NextUInt64());
                var episodeSteps = 0;
                while (examples.Count < config.MinibatchSize && !warmDeadline.Expired)
                {
                    if (game.Frame.Finished || episodeSteps >= config.MaxEpisodeSteps)
                    {
                        game.Reset(random.NextUInt64());
                        episodeSteps = 0;
                    }
                    var actions = new Dictionary<int, int>();
                    foreach (var request in game.Frame.Decisions)
                    {
                        var target = BotActions.Choose(teacher, game, request, random, warmDeadline);
                        examples.Add(request.Copy());
                        targets.Add(Array.FindIndex(request.Actions, a => a.Id == target));
                        actions.Add(request.Player, random.NextDouble() < 0.5 ? target : request.Actions[random.NextInt(request.Actions.Length)].Id);
                    }
                    game.Step(actions).Validate(definition.Spec);
                    episodeSteps++;
                }
                if (examples.Count > 0 && !warmDeadline.Expired) optimizer.Imitate(examples, targets.ToArray());
            }
        }
        warmStartComplete = true;
        SaveCheckpoint();
        var trainingDeadline = budget.AtFraction(55.0 / 60);
        var lastCheckpoint = budget.Consumed;
        var lastEvaluation = budget.Consumed - config.EvaluationSeconds;
        var lastProgress = double.NegativeInfinity;
        var lastPoolUpdate = updates;
        var newDecisions = 0L;
        var trainingStart = budget.Consumed;
        var evaluationRecords = new List<EvaluationResult>();
        using var log = new StreamWriter(Path.Combine(runDirectory, "metrics.jsonl"), append: resume) { AutoFlush = true };
        Console.WriteLine($"training: {workers} simulation workers; PPO until {budget.TotalSeconds * 55 / 60:F1}s");
        while (!trainingDeadline.Expired)
        {
            var batch = collector.Collect(network, trainingDeadline);
            decisions += batch.Count;
            newDecisions += batch.Count;
            if (batch.Count == 0 || trainingDeadline.Expired) break;
            var update = optimizer.Update(batch, random, trainingDeadline);
            updates++;
            if (updates - lastPoolUpdate >= 10)
            {
                pool.Add(network.ExportWeights());
                lastPoolUpdate = updates;
            }
            if (budget.Consumed - lastProgress >= 5)
            {
                var rate = newDecisions / Math.Max(0.001, budget.Consumed - trainingStart);
                var metrics = new { elapsedSeconds = budget.Consumed, decisions, updates, decisionsPerSecond = rate,
                    rollout = collector.Stats, update, workingSetMiB = Process.GetCurrentProcess().WorkingSet64 / 1048576.0,
                    bestPolicyScore, workers };
                log.WriteLine(JsonSerializer.Serialize(metrics, JsonFiles.Options));
                JsonFiles.WriteAtomic(Path.Combine(runDirectory, "progress.json"), metrics);
                Console.WriteLine($"{budget.Consumed,7:F1}s | {decisions,9} decisions | {rate,7:F0}/s | policy score {bestPolicyScore:F3} | entropy {update.Entropy:F3}");
                lastProgress = budget.Consumed;
            }
            if (budget.Consumed - lastEvaluation >= config.EvaluationSeconds && !trainingDeadline.Expired)
            {
                weights = network.ExportWeights();
                var result = Evaluation.Run(definition, Artifact(weights, "policy", "Policy validation.").CreateBot(definition), opponents,
                    config.EvaluationPairs, 900001, trainingDeadline.Limit(TimeSpan.FromSeconds(Math.Min(5, budget.TotalSeconds / 60))),
                    config.Search.MoveMilliseconds, config.MaxEpisodeSteps);
                evaluationRecords.Add(result);
                if (result.Complete && result.MeanScore > bestPolicyScore + 1e-9)
                {
                    bestPolicy = weights;
                    bestPolicyScore = result.MeanScore;
                }
                JsonFiles.WriteAtomic(Path.Combine(runDirectory, "policy-evaluations.json"), evaluationRecords);
                lastEvaluation = budget.Consumed;
            }
            if (budget.Consumed - lastCheckpoint >= config.CheckpointSeconds && !trainingDeadline.Expired)
            {
                SaveCheckpoint();
                lastCheckpoint = budget.Consumed;
            }
        }

        Console.WriteLine("selection: comparing learned policy, tactical play and supported search bots");
        SaveCheckpoint();
        var selected = BotArtifact.Load(bestPath, definition.Spec);
        var candidates = new List<BotArtifact> { selected,
            Artifact(bestPolicy, "policy", "Best validated network."), Artifact(weights, "policy", "Final network.") };
        if (definition.CreateTeacher() is not null && selected.SelectedBot != "tactical")
            candidates.Add(Artifact(bestPolicy, "tactical", "Tactical baseline."));
        candidates.Add(Artifact(bestPolicy, "random", "Random baseline."));
        if (definition.Spec.SupportsSearch && definition.Create() is ISearchableGame)
        {
            if (definition.Spec.Id == "enclosure")
                candidates.Add(Artifact(bestPolicy, "alpha-beta", "Iterative-deepening alpha-beta with geometric evaluation."));
            candidates.Add(Artifact(bestPolicy, "search", "PUCT with random rollouts."));
            candidates.Add(Artifact(bestPolicy, "search-policy", "PUCT with the learned policy and value."));
        }
        var finalResults = new List<EvaluationResult>();
        var selectedScore = double.NegativeInfinity;
        var finalPolicyScore = double.NegativeInfinity;
        var selectionDeadline = budget.AtFraction(59.0 / 60);
        for (var i = 0; i < candidates.Count && !selectionDeadline.Expired; i++)
        {
            // Each candidate gets a bounded share; incomplete evaluations cannot win selection.
            var candidateDeadline = selectionDeadline.Limit(TimeSpan.FromSeconds(selectionDeadline.RemainingSeconds / (candidates.Count - i)));
            var candidate = candidates[i].SelectedBot == "search-policy" ? candidates[i] with { Weights = bestPolicy } : candidates[i];
            var result = Evaluation.Run(definition, candidate.CreateBot(definition), opponents, config.EvaluationPairs,
                17000003, candidateDeadline, config.Search.MoveMilliseconds, config.MaxEpisodeSteps);
            finalResults.Add(result);
            if (candidate.SelectedBot == "policy" && result.Complete && result.MeanScore > finalPolicyScore + 1e-9)
            {
                bestPolicy = candidate.Weights;
                bestPolicyScore = result.MeanScore;
                finalPolicyScore = result.MeanScore;
            }
            Console.WriteLine($"  {candidate.SelectedBot,-14} score={result.MeanScore:F3}, complete={result.Complete}, p95={result.P95MoveMilliseconds:F2}ms");
            if (result.Complete && result.MeanScore > selectedScore + 1e-9)
            {
                selected = candidate;
                selectedScore = result.MeanScore;
                JsonFiles.WriteAtomic(bestPath, selected);
            }
        }

        SaveCheckpoint();
        JsonFiles.WriteAtomic(Path.Combine(runDirectory, "final-evaluations.json"), finalResults);
        JsonFiles.WriteAtomic(Path.Combine(runDirectory, "report.json"), new
        {
            status = "completed", game = config.Game, device = config.Device, elapsedSeconds = budget.Consumed,
            budgetSeconds = budget.TotalSeconds, decisions, updates, workers, selectedBot = selected.SelectedBot,
            selectedScore = double.IsFinite(selectedScore) ? (double?)selectedScore : null,
            bestPolicyScore, initialPolicy = initialEvaluation, finalEvaluations = finalResults,
            trainingDecisionsPerSecond = newDecisions / Math.Max(0.001, Math.Min(budget.Consumed, budget.TotalSeconds * 55 / 60) - trainingStart),
            observations = "Numeric player-visible features; game-specific scaling belongs to the versioned adapter.",
            limitation = "Demonstration-game results do not establish playing strength in the unreleased event game."
        });
        Console.WriteLine($"complete: {selected.SelectedBot}; portable bot: {bestPath}");
        return 0;
    }
}
