# Enclosure validation

Validated on September 26, 2026, on an Intel Core i9-14900KF and NVIDIA RTX 5080, using .NET 10.0.112 and TorchSharp 0.107.0.

All **76 tests pass** (61 core/training and 15 browser-session tests). The simulator matches 363 official reference positions across three complete games, including exact legal-action sets, captures, protection, fractional areas, scores, and termination. Search tests cover consecutive placements, exhaustive depth-three agreement, transpositions, cancellation, deadlines, and concurrent calls. Browser session tests cover hints, undo, stale results, and atomic history imports.

Playwright Chromium checks passed for both seats, both AI placements, hints, cancellation, undo, downloaded saves, reloading, invalid imports, and completed-game territory rendering. Desktop and 390-pixel mobile layouts showed no JavaScript errors or horizontal overflow.

## Search and inference

Release build, five warmed search samples per position, fixed seed 72891. Reproduce with:

```sh
dotnet run --project StratJamAI -c Release -- benchmark-enclosure --samples 5 --compare --pairs 2 --output benchmark.json
```

| Fixture | Legal moves | Completed depth at 1 second | Mean nodes | p95 time | Allocations/search |
| --- | ---: | ---: | ---: | ---: | ---: |
| Opening | 69 | 4 | 1,731,845 | 1000.043 ms | 14.08 MiB |
| Middle | 930 | 2 | 110,845 | 1000.056 ms | 3.59 MiB |
| Late | 1,096 | 2 | 45,762 | 1000.040 ms | 5.41 MiB |

Reusable geometry storage increased middle/late search throughput by 1.89×/1.77× versus the previous implementation with the same area cache. Middle-game allocations fell from about 3,400 MiB to 3.59 MiB per one-second search. These are temporary allocated bytes, not retained memory. The warmed area routine itself allocated at most 1 KiB across 100 regression-test calls.

Factoring the portable policy head improved full inference by 2.40×, 4.35×, and 4.46× on the three fixtures. Outputs and chosen actions matched the original joined-head calculation. Torch output and parameter-gradient parity tests also pass.

At equal 50 ms limits, alpha-beta won four games against tactical play and four against untrained random-rollout MCTS, with balanced seats. This is a small smoke comparison; it does not establish superiority to trained policy-guided MCTS. Large branching factors limit completed depth. Very short budgets can return the legal ordered fallback before depth one finishes.

## Training and portable export

A 90-second CUDA smoke budget completed in 85.94 seconds, collecting 90,688 decisions and 44 PPO updates with eight simulation workers. A CPU run resumed its checkpoint and finished within an extended 45-second cumulative budget, reaching 26,492 decisions and 413 updates. Both selected the tactical baseline in the small final evaluation, so these runs establish pipeline operation rather than a strong trained policy. These training runs preceded the final geometry allocation optimization.

The GPU forward/backward doctor check passed, with exported prediction error 2.3841858e-7. A separate executable referencing only StratJamAI.Core loaded the exported GPU-trained policy and played 20 legal Enclosure placements without loading TorchSharp. The normal CLI export and history-based suggestion commands passed as well.

Measured data: [benchmark](enclosure-benchmark.json), [validation evidence](enclosure-validation.json). The event server wire protocol is not included; use the local UI, terminal, or coordinate-history suggestion command.
