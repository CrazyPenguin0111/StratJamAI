# Space-seeking strategy validation

The final candidate won **3 of 4** development matches against the preserved v2 engine on September 26, 2026. Both engines received 100 ms per placement. This is a small, timing-dependent check across two openings, not an estimate of general playing strength.

The change responds to the reported tendency to reinforce walls without claiming enough space. The latest user screenshot showed Blue/human 220.64 and Red/AI 78.57. Its full position and move history were not available, so these tests do not recreate that game or demonstrate that its double-wall strategy is solved.

## Versions and method

| Version | Core DLL SHA-256 |
| --- | --- |
| Preserved v2 | `a1c487d12dc870163df5ef7f53b053dc7f28e4b9ec6f85c8306f7b517d8c1e2a` |
| Final candidate, spatial coefficient 0.10 | `c941b6983b3ce975185af95c7060b4afa921210b67f10f88b9886b5b89c9315c` |
| Rejected first candidate, coefficient 0.35 | `2a35485cd1445a463533f0490a007e7f606f30b3b7c6e90d20232df68fb578e9` |

The final evaluator adds a modest reward for contested reachable space, including straight expansion that has no convex-hull area yet. It removes passive reinforcement rewards and retains the current-area and closure-potential forecast. Endpoint-set caching limits repeated distance calculations.

Each opening was played with the candidate as Blue and as Red. Opening 0 is the standard initial position. Opening 1 first plays these three legal segments, using zero-based coordinates: `(3,9)-(6,12)`, `(15,9)-(12,6)`, `(12,6)-(12,9)`. Both engines use a maximum depth of 120 and receive three 30 ms warmup searches before the matches.

The dependency-free [match harness](../tools/strategy-match/README.md) loads each Core DLL independently and replays every chosen action through both simulators. Exact scores and areas matched after every action. The [complete JSON report](enclosure-space-strategy-validation.json) includes all four final matches, scores and areas every 20 moves, search diagnostics, rejected-candidate results, and exploratory script results. Arrays use Blue, then Red order.

## Final candidate results

| Opening | Candidate color | Candidate score | v2 score | Candidate final area | v2 final area | Result |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| Standard | Blue | 134.60 | 412.50 | 4.30 | 14.70 | Loss |
| Standard | Red | 844.21 | 543.26 | 10.43 | 18.00 | Win |
| Fixed three moves | Blue | 212.70 | 156.80 | 5.20 | 0.70 | Win |
| Fixed three moves | Red | 297.10 | 176.33 | 11.65 | 7.17 | Win |

Territory outcomes remain mixed. The candidate's mean final area was 7.89 versus v2's 10.14; cumulative scores determine the winner. Its standard Blue game still lost substantially. The 100 ms games generally completed only one or two search plies later in the game, so these results do not establish performance at the public UI's longer default budget.

The initial 0.35 candidate was rejected after **0 wins in 4** matched games. It deferred early scoring too strongly. Reducing the spatial coefficient to 0.10, with better cache reuse, produced the final results above.

A separate mirrored perimeter script was tested at 50 ms against v2 and the rejected 0.35 candidate. Both engines won both colors. The script repairs missing planned edges and falls back to the tactical teacher, making it much weaker than the reported human double-wall play. Those games are preserved for transparency and provide no evidence that the human strategy is solved. The final 0.10 candidate was not run against this script.

## Performance and correctness checks

An independent three-sample check used the same opening and deterministic move-60 fixture for both DLLs. The following values are medians:

| Check | v2 | Final candidate |
| --- | ---: | ---: |
| Opening depth-1 elapsed | 0.1195 ms | 0.1345 ms |
| Move-60 depth-1 elapsed | 6.2970 ms | 6.5243 ms |
| Opening nodes in 100 ms | 204,659 | 100,404 |
| Move-60 nodes in 100 ms | 9,351 | 8,989 |
| Opening completed depth in 100 ms | 4 | 4 |
| Move-60 completed depth in 100 ms | 2 | 2 |

The richer evaluator does not improve search throughput: opening node count fell about 51%, and move-60 node count about 4%. Median managed allocations were unchanged for these fixtures. The longest measured candidate 100 ms search took 100.037 ms. Node count depends on evaluation and pruning as well as per-node cost, so it is not an isolated microbenchmark of the evaluator. Raw performance samples are included in the JSON report.

The final validation also passed 92 Core/CLI tests, 55 Web tests, and 9 JavaScript tests (156 total), including strategic behavior, search, official-rule parity, and coaching regressions. Larger match sets and replaying the actual human game remain useful follow-up evidence.
