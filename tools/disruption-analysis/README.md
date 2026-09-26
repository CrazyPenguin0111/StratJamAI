This small Core-only replay check compares selected moves against the moves recorded in a game history. It fully validates the history, searches at the requested budgets, and enumerates every legal immediate capture reply to measure retained area. It also tries the recorded following turn, stopping if a recorded reply becomes illegal. It does not claim to exhaust all combinations of two opponent placements.

Append `--capture-pairs` after the budgets argument to enumerate every legal pair of consecutive captures in the opponent's turn as well. This audit includes the worst retained area and both players' strategic forecasts. It costs extra analysis time outside the timed searches. It still excludes a non-capturing setup placement followed by a capture, so its retained area is not a guarantee against every possible turn.

Build against a preserved engine DLL so a running public host is unaffected:

```sh
dotnet build tools/disruption-analysis/DisruptionAnalysis.csproj -c Release \
  -p:CoreAssemblyPath=/absolute/path/to/StratJamAI.Core.dll \
  --artifacts-path /tmp/enclosure-disruption-check
dotnet /tmp/enclosure-disruption-check/bin/DisruptionAnalysis/release/DisruptionAnalysis.dll \
  StratJamAI.Tests/Fixtures/EnclosureDisruption1.json baseline 102,106,70 100,1000 \
  > /tmp/enclosure-disruption-baseline.json
```

Use a separate artifacts directory for a candidate DLL. When a private .NET installation supplies the runtime, invoke its `dotnet` executable for the second command. Prefixes count already-played moves; prefix `102` analyzes the choice for move `103`. When two placements remain, the tool searches both consecutively with the requested budget for each, then measures the opponent's replies. For example, prefixes `101,105` test full Red turns. The default prefixes target the first disruption fixture; pass appropriate prefixes for another history.

JSON includes engine/history hashes, exact moves, scores and areas, completed depth, visited nodes, elapsed time, and legality/protection checks. Diagnostics go to stderr. The program references an existing Core assembly directly and never builds the engine, web host, or training project. There is one short warmup per position; these samples validate behavior and deadline adherence, not statistical throughput or win rate. Run compared engines separately under similar load.

For engines that expose `EnclosureSearchResult.CaptureQuiescencePlies`, add `-p:DefineConstants=CAPTURE_QUIESCENCE` to the build command to include the number of completed tactical extension plies. Leave it out when testing older assemblies; their corresponding JSON field is `null`.
