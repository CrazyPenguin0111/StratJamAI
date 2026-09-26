# Strategy match harness

Compare two preserved `StratJamAI.Core.dll` files without loading Torch, building either engine, or changing a running server. The harness has no package or project references and loads the two assemblies into separate `AssemblyLoadContext` instances.

From the repository root:

```sh
dotnet build tools/strategy-match/StrategyMatch.csproj -c Release --artifacts-path /tmp/stratjam-strategy-match-build
dotnet /tmp/stratjam-strategy-match-build/bin/StrategyMatch/release/StrategyMatch.dll /absolute/path/to/baseline/StratJamAI.Core.dll /absolute/path/to/candidate/StratJamAI.Core.dll /tmp/strategy-matches.json paired
```

The last `paired` argument runs four matches at 100 ms per placement: two opening positions, each with the candidate playing Blue and Red. Both engines have a maximum depth of 120 and receive three 30 ms warmup searches. Every action is replayed through both simulators; differing scores or areas fail the run. A JSON record is printed and the report file is updated after each completed game. Expect roughly 48 seconds plus initialization.

Omit `paired` to also run four exploratory perimeter-script games at 50 ms per AI placement. The script tries its first missing legal planned edge before falling back to the corresponding engine's tactical teacher, using random seed 481. Red's path mirrors Blue horizontally. This is a weak behavioral probe, not a simulation of a skilled human or a strength benchmark.

The output uses color 0 = Blue, color 1 = Red. `scores` contains cumulative scores and `areas` current enclosed areas in that order. `candidateResult` is 1 for a candidate win, 0 for a draw, and -1 for a loss. Snapshots are recorded every 20 placements. `beforeMeanDepth` and `candidateMeanDepth` average completed search depth over each engine's moves; node totals include all searches during that game.

Opening 0 starts from the normal initial board. Opening 1 starts after these zero-based-coordinate segments:

1. Blue `(3,9)-(6,12)`.
2. Red `(15,9)-(12,6)`.
3. Red `(12,6)-(12,9)`.

Wall-clock budgets mean exact games can change with machine speed, runtime warmup, and competing CPU work. Preserve the DLL hashes with results and run comparisons without concurrent benchmarks. The four games are a small development check, not a general win-rate estimate. See [the recorded validation](../../docs/enclosure-space-strategy-validation.md).

To compare Red engines against an adaptive Blue disruption script derived from supplied histories:

```sh
dotnet /tmp/stratjam-strategy-match-build/bin/StrategyMatch/release/StrategyMatch.dll /absolute/path/to/baseline/StratJamAI.Core.dll /absolute/path/to/candidate/StratJamAI.Core.dll /tmp/disruption-matches.json disruption StratJamAI.Tests/Fixtures/EnclosureDisruption1.json StratJamAI.Tests/Fixtures/EnclosureDisruption2.json
```

This mode first validates each entire supplied history. In each new game, Blue prioritizes legal captures that remove the most current Red area, breaking ties by retained Blue area and then action ID. If no capture reduces Red area, it plays the next original Blue move when legal, otherwise the tactical teacher. Each Blue placement consumes one original planned move, including when an adaptive capture or fallback replaces it. Red receives 100 ms per placement. Both engines face the same policy from the initial board, and counts of scripted, disruptive, and fallback moves are reported. These games are synthetic continuations of the supplied attack motifs, not exact replays or a simulation of the human's complete decision process.

Use `disruption-baseline` or `disruption-candidate` to run only that engine when coordinating separate benchmark windows. Both DLL path arguments are still required.

To compare the engines against the supplied braid construction from frozen positions:

```sh
dotnet /tmp/stratjam-strategy-match-build/bin/StrategyMatch/release/StrategyMatch.dll /absolute/path/to/baseline/StratJamAI.Core.dll /absolute/path/to/candidate/StratJamAI.Core.dll /tmp/braid-matches.json braid StratJamAI.Tests/Fixtures/EnclosureBraid.json 0,25,33 100
```

The optional final arguments are comma-separated history prefixes (default `0,25,33`) and milliseconds per Red placement (default `100`). A prefix counts recorded history moves, before either engine makes a new decision. The complete history is validated in both simulators before warmups or challenge searches; each continuation also replays every action through both simulators and checks exact scores and areas.

For each prefix, Blue continues its recorded schedule from the first 65 history moves. Each Blue decision consumes the next scheduled Blue move. If it is illegal, or the schedule is exhausted, Blue plays the first missing legal construction edge from that same plan; otherwise it uses the corresponding engine's tactical teacher with seed 481. This gives the construction a limited repair ability without inventing a new human strategy. Red is controlled by the baseline and candidate in separate continuations from the identical prefix. Starting before the reported barrier closes tests opportunities to disrupt construction; it does not imply that a later sealed position can be rescued.

The output is a JSON object with DLL and fixture SHA-256 hashes, the original replay's final scores, and a `results` dictionary keyed by prefix and engine. Each result includes scripted/repair/fallback counts, search depths and timing, score/area snapshots every ten placements, individual choices, and a complete replayable `history`. Results are written after each completed continuation. These are diagnostic challenges derived from one history, not a general win-rate estimate. Run them without concurrent CPU benchmarks.
