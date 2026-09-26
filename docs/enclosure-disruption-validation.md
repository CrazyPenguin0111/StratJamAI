# Disruption replay validation

The two supplied human games are preserved as `StratJamAI.Tests/Fixtures/EnclosureDisruption1.json` and `EnclosureDisruption2.json`. All 120 placements in each history replay legally. These are regression positions, not claims that a new bot would win either complete game from the initial position.

| History | Final Blue score | Final Red score | Final Blue area | Final Red area |
| --- | ---: | ---: | ---: | ---: |
| First | 4271.942857146 | 3174.9125 | 107.1 | 2.7 |
| Second | 1229.067765569 | 622.085256412 | 75.5 | 1 |

The first game contains repeated reconstruction followed by immediate cuts. Before move 103, Red can preserve at least 47 area against every legal single Blue capture by playing E12–G13. However, two captures reduce that alternative to just 2.667: the single-capture metric overstates its safety. The recorded M16–P13 reconstruction leaves 53.333 area initially, 2.667 after the strongest immediate cut, and zero after two captures. Validation therefore also enumerates every legal capture pair, rather than only blocking the move recorded in the history.

The second game contains a different horizon problem. After move 27, every currently legal single Blue capture leaves Red's 18 area intact. Blue's I9–K6 capture creates an endpoint, allowing H8–K6 on its second placement to open the enclosure completely. Extending only one capture misses this attack.

## Search behavior

The normal alpha-beta tree retains every legal move, consecutive placements use the actual player to choose minimax direction, and deadlines/cancellation remain active. At the nominal horizon, a bounded capture extension follows legal moves through the real simulator, including protection expiry, score collection, and endpoint removal. The extension is selective and uses a static stand-pat estimate; it is not an exhaustive search of quiet setup moves or arbitrary attack sequences. Ordinary completed depth and tactical continuation are distinct.

An initial implementation that expanded every capture at both additional plies was rejected: on crowded positions it failed to complete even depth one within a second. The bounded implementation must retain a completed ordinary search when its additional work is interrupted.

The bounded search alone also failed validation: it lost all four short paired games against v3. The remaining evaluation problem was speculative reconstruction: even after an enclosure had been destroyed, its open convex hull could still receive income over almost every remaining scoring event. In one audited position this granted about 391 future points to Red despite having zero current area.

The revised evaluator caps speculative open-hull income at two scoring events when the existing distance samples place the player's frontier near opposing nodes. This proximity heuristic does not prove a legal capture; protected or doubled boundaries can still trigger it. Current area, banked scores, and actual capture legality are unchanged. Distant construction keeps its prior completion forecast, preserving the large unfinished-boundary investment and space-expansion regressions. An unconditional cap was tested and rejected because it harmed that construction behavior.

At the nominal horizon the default tactical width is eight, grouped by removed opposing edge and ordered by actual area loss. A zero-damage capture that creates an endpoint may be retained to enable the second placement. This extension stops when the actor changes. `CompletedDepth` counts ordinary placements; `EnclosureSearchResult.CaptureQuiescencePlies` reports whether the published result completed the configured tactical extension (zero when only the initial ordinary search completed). It does not mean every branch used that many extra moves.

With less than 250 ms remaining in the effective move budget, the engine uses ordinary iterative deepening directly with the improved evaluator. This preserves search depth at fast browser settings. The default one-second setting enables the tactical extension. `DepthSearchOptions.MinimumTacticalMilliseconds` can override that threshold; zero forces the configured extension. An unconditional tactical extension plus the pressure discount won only one of four development games at 100 ms, despite retaining more final territory in all four, so it was not selected for those short budgets.

## Targeted outcomes

At a one-second budget per placement, the revised engine improved the following exact capture-pair checks. Prefixes count moves already played. The whole-turn case searches both Red placements before checking Blue's reply.

| First history position | V3 minimum Red area after two captures | Revised minimum Red area |
| --- | ---: | ---: |
| Prefix 102, Red's last placement | 0 | 10 |
| Prefix 105, complete Red turn | 2.7 | 12.6667 |

These minima cover every legal sequence of two captures from the resulting position, including captures enabled by the first move. They do not cover a quiet setup followed by a capture, longer plans, or a different earlier game. In the second history's early position, an 18-area closure survives the recorded reply but can still be opened by a different quiet setup and cut; it is not counted as proof of robust defense. Losing late positions need not have a move that saves all territory.

## Final validation and deployment

The final adaptive candidate won three of four paired 100 ms games against preserved public v3, with greater final territory in all four and comparable ordinary search depth. This is a small, timing-dependent comparison. One completed game against a separate synthetic disruption policy was a loss (Red 300.70 versus Blue 746.49); an earlier v3 diagnostic won against that policy, although its timing overlapped other analysis. The second candidate synthetic game was stopped before completion. These mixed outcomes are recorded in the [match artifact](enclosure-disruption-matches.json); the change improves the audited defenses but does not establish general dominance against disruption.

All 108 Core/CLI, 55 Web, and 9 JavaScript tests passed (172 total). Fixed-depth regression tests enumerate every legal capture pair for the two first-history defenses above. Earlier construction, expansion, rules, consecutive-turn minimax, deadline/cancellation, visitor isolation, and coaching tests remain covered. Full reports and source/assembly hashes are in [the replay artifact](enclosure-disruption-validation.json).

The public host was replaced at the same Cloudflare address with `alpha-beta-defense-v4`. Browser checks passed for two AI replies, analysis mode, live hints, applying a suggestion, separate visitors, coach setup, and userscript availability. A separate HTTPS coach-session check imported the first history through move 102 and returned the improved D18–G15 suggestion, completed depth two, a matching hint revision, and a legal continuation within its one-second budget. See [deployment details](public-coach-deployment.md).

## Reproduction

The [Core-only analysis harness](../tools/disruption-analysis/README.md) compares preserved DLLs without modifying a running server. It records assembly and fixture hashes, thinking budgets, moves, completed depth, nodes, wall time, minimum retained area across all immediate legal captures, and the legal part of the recorded following turn. Whole-turn checks also search both friendly placements before assessing the opponent's replies.

The [match harness](../tools/strategy-match/README.md) provides a separate small paired comparison. Wall-clock searches vary with CPU load and runtime warmup; these development checks are not a measured general win rate against humans.
