# Whole-turn thinking budget

The browser slider accepts 50–20,000 ms. In Play AI, the selected time covers the whole remaining AI turn. The server searches once, validates the returned continuation, and reuses its second same-player placement. If that continuation is unavailable or illegal, any replacement search receives only the actual time left. With no time left, it uses a legal fallback. Both placements are prepared on a copy and published together after the revision and cancellation checks.

The budget starts after obtaining the session and shared CPU slots. Queue wait and browser/network overhead are separate. Opening single-placement turns, imported partial turns, forced passes, and the final move stop according to the game rules. Hints and live coaching still search each requested position separately and never automatically play a pair. The terminal `play` and `suggest` commands retain their individual-move budgets; `ui --move-ms` sets the browser turn budget.

Search statistics retain the original position's depth and evaluation. Nodes include any fallback search; elapsed time measures the whole planning operation. At short budgets a missing continuation can require an immediate legal fallback, which is weaker than receiving another full search budget.

Validation passed: 68 Web tests, 12 launcher tests, and nine JavaScript coaching tests. Coverage includes PV reuse, stopping before an opponent move, malformed/short PVs, remaining-budget fallback, exhausted budgets, queue waits, single-placement hints, atomic cancellation, illegal-result rollback, pass/partial-turn handling, and the 20-second limit. The published host also started successfully with `--move-ms 20000`.

On the unchanged public URL, the actual 20-second browser test sent one `/api/think` request. Observed move counts were 0, 1, then 3; no partial AI turn was exposed. The server reported 20,001.8 ms total planning time and the browser observed completion in 20.481 seconds. Both recorded AI moves were Red's, and an out-of-range 20,001 ms settings request returned 400. There were no JavaScript errors. The [raw result](turn-budget-validation.json) records the published assembly hashes; the Core engine is byte-for-byte unchanged from defense-v4.

The existing public browser smoke check was repeated for play, live suggestions, analysis mode, applying a suggestion, visitor isolation, coach setup, and userscript availability. Refresh existing tabs to load the updated labels and slider; the host restart reset in-memory games.
