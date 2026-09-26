# Local validation

Validated on September 25, 2026, on the RTX 5080 (16 GB) and i9-14900KF, using .NET SDK 10.0.112, TorchSharp 0.107.0, and LibTorch 2.10.0/CUDA 12.8.

The **30-test suite passed**. It covers the actual tensor PPO clipping objective, learning without a teacher/search, action masks, export parity, per-player reward and GAE handling, simultaneous-action privacy, mixed strategies, extra turns, independent evaluation randomness, optimizer/checkpoint recovery, configuration compatibility, and process-level deadlines.

The GPU `doctor` command executed forward/backward computation successfully. Its maximum difference between TorchSharp and exported C# predictions was `2.3841858e-7`.

## Five-minute training run

```sh
dotnet run --project StratJamAI -c Release --no-build -- train --config configs/default.json --minutes 5 --output runs/gpu-validation
```

| Measurement | Result |
| --- | ---: |
| Complete run, including preparation/evaluation/export | 277.28 seconds |
| Allowed budget | 300 seconds |
| Learner decisions collected | 7,785,344 |
| PPO updates | 950 |
| Training throughput | Approximately 28,533 decisions/second |
| Automatically selected CPU simulation workers | 8 |
| Observed process working set | 1,601.8–1,625.9 MiB |
| Selected bot | Learned policy without search |

The first run's final 96-game selection score was `0.7448` for the learned policy, versus `0.6510` for the tactical baseline. That selection used the initial deterministic minimax tie-break. The final evaluator broadens the oracle to sample different equally optimal moves and gives each player an independent random stream. The stronger audit below is the more useful strength measurement.

Process working set is CPU memory, not GPU memory. This short run checks for obvious growth; it does not establish memory behavior for every possible game adapter or a full hour.

## Independent portable-host audit

A separate executable referenced **only `StratJamAI.Core` and .NET**, with no TorchSharp assembly or native training libraries. It loaded the exported network and evaluated three candidates on the same fresh seeds and opponent suite: 1,536 games per candidate, with 512 against each opponent and balanced player positions. The initial network was reconstructed from the original seed (`1337`); evaluation used seed `43000019`.

Scores are **win = 1, draw = 0.5, loss = 0**, not win rates.

| Candidate | Random opponent | Tactical opponent | Minimax with varied optimal moves | Overall |
| --- | ---: | ---: | ---: | ---: |
| Initial untrained network | 0.4609 | 0.0000 | 0.0000 | 0.1536 |
| Exported trained network | 0.9746 | 0.7500 | 0.4297 | **0.7181** |
| Tactical baseline | 0.9531 | 0.5000 | 0.4482 | 0.6338 |

The trained network's approximate 95% bootstrap interval was `0.7015–0.7347`. Its measured p95 move time in this portable host was `0.0224 ms`. These latency measurements depend on CPU load and JIT warmup.

The learned model substantially improved on its initialization and beat the tactical baseline on the aggregate suite. It still lost some games against varied perfect play. This validates the learning pipeline without claiming that the model is optimal, or predicting strength in the unreleased event game.

## Recovery and deadline checks

- A short supervised run completed training, evaluation, checkpointing, and export inside its allotted budget.
- A deliberately tiny budget stopped the worker and retained its legal baseline artifact.
- Checkpoint corruption recovered the preceding intact generation.
- Restoring Adam state reproduced the next CPU optimization update within the test tolerance.
- A separate CUDA smoke run resumed on CPU, preserving training counters and optimizer state, and completed within its explicitly extended **45-second cumulative** budget.
- The normal `dotnet run ... -- --help` entry point and export command also ran successfully.

Machine-readable evidence is in [validation-summary.json](validation-summary.json). The local trained model is in `runs/gpu-validation/best.bot.json`; run artifacts are excluded by `.gitignore`. Setup and integration instructions are in the [README](../README.md).
