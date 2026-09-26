# Enclosure search performance

Measured September 26, 2026, on an Intel Core i9-14900KF, Linux x64, .NET SDK 10.0.112 and runtime 10.0.12. These measurements compare the preserved search engine with the optimized search engine on identical positions and evaluation rules.

These results predate the subsequent strategy evaluation for unfinished enclosures and reinforced walls. They measure the search optimizations in isolation, not the current evaluator's speed or playing strength.

## Same result at the same depth

The primary comparison is elapsed time to finish the same depth. Every one of the 30 fixed-depth searches returned the same action and evaluation as the original engine. All returned principal variations were legal when replayed.

| Position | Completed depth | Before mean | After mean | Speedup | Allocated bytes before → after |
| --- | ---: | ---: | ---: | ---: | ---: |
| Opening, move 0 | 3 | 77.25 ms | 13.64 ms | **5.66×** | 1,163,736 → 328,256 |
| Middle, move 60 | 2 | 152.92 ms | 23.51 ms | **6.50×** | 3,291,424 → 326,128 |
| Late, move 110 | 2 | 322.86 ms | 49.31 ms | **6.55×** | 5,497,520 → 326,128 |

The middle position searched exactly 3,583 nodes in both versions. Opening nodes fell from 169,612 to 17,526; late nodes fell from 8,777 to 6,357. Better pruning reduces the work needed for an identical answer, so node throughput alone would miss part of the improvement.

## Depth within a move budget

Ranges show the minimum and maximum completed depth across five samples. Depth counts individual placements or forced passes, including consecutive actions by the same player.

| Position | Budget | Before depth | After depth | After p95 latency |
| --- | ---: | ---: | ---: | ---: |
| Opening | 5 ms | 2 | 2 | 5.032 ms |
| Opening | 50 ms | 2 | **4** | 50.039 ms |
| Opening | 1,000 ms | 5 | 5 | 1,000.063 ms |
| Middle | 5 ms | 1 | 1 | 5.072 ms |
| Middle | 50 ms | 1 | **2** | 50.044 ms |
| Middle | 1,000 ms | 2 | 2 | 1,000.046 ms |
| Late | 5 ms | 0 | 0 | 5.027 ms |
| Late | 50 ms | 1 | **1–2** | 50.062 ms |
| Late | 1,000 ms | 2 | 2 | 1,000.111 ms |

At a one-second budget the optimized engine visited approximately 3.12 million, 127 thousand, and 58 thousand nodes respectively, compared with 1.84 million, 112 thousand, and 44 thousand. This did not complete an additional depth on these three positions. Depth zero at five milliseconds means the engine returned its legal ordered fallback before completing depth one.

With five samples, the nearest-rank p95 is the largest observed latency for that row. All measured optimized calls finished within their requested budget plus 0.112 ms. This is a local timing observation, not a hard real-time guarantee.

## Measurement and profiling

Both versions ran in separate Core-only Release harnesses referencing copied immutable DLLs. The public web host and its build outputs remained untouched. Fixtures use `EnclosureBenchmarks.FixedPositions` with seed `72891`: 69, 930, and 1,096 legal moves respectively. Each process warmed every position with three 100 ms searches. Five rounds alternated which engine ran first, with one recorded sample per workload per process. Fixed-depth searches used a 120-second safety budget; their reported depths all completed.

The stopwatch enclosed only `Search`. Allocation counts used `GC.GetAllocatedBytesForCurrentThread`; legality and PV replay checks followed the measured section. The public host and unrelated desktop workloads remained active, so these results are specific to this machine and workload. No CPU affinity or clock controls were imposed.

Direct baseline operation timings supported prioritizing move ordering. At the late fixture, computing all priorities and sorting 1,096 moves took about 160.1 µs, versus 26.4 µs for both players' full area calculations, 9.8 µs for legal-move generation, 0.51 µs for the search key, and 0.32 µs for evaluation with cached areas. These are isolated operation costs, not percentages of total search time.

A 36-second search workload was also recorded with `dotnet-trace` and converted to Speedscope, following the [Microsoft documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace). The sampled thread-time output was dominated by GC safepoint frames and long finalizer intervals; it was unsuitable for precise CPU cost percentages. The raw local files remain in `/tmp/stratjam-search-before/search.nettrace` and `search.speedscope.json`.

The [machine-readable report](enclosure-search-performance.json) contains every timing sample, actions, values, PVs, assembly hashes, and operation timings. The full isolated test suite passed 126 tests: 82 Core/CLI tests and 44 web tests. Search correctness tests additionally compare exact fixed-depth values and PV leaf evaluations against an independent minimax reference.

## Reproduce the comparison

Preserve the original and candidate `StratJamAI.Core.dll` files separately before rebuilding either engine, then run the [isolated comparison script](../tools/search-benchmark/compare.py):

```sh
python tools/search-benchmark/compare.py \
  --before-core /path/to/before/StratJamAI.Core.dll \
  --after-core /path/to/after/StratJamAI.Core.dll \
  --output /tmp/enclosure-search-comparison
```

The script copies both assemblies into the output directory, builds the Core-only harness with `CoreAssemblyPath`, and isolates all build artifacts there. It runs five alternating rounds by default and writes `report.json`, every raw sample, and logs. It does not build the engine project or replace any running application's files. Use `--samples` to increase repetitions, and `--dotnet` to choose an explicit .NET executable. Run other CPU-intensive validation separately from the timing comparison.
