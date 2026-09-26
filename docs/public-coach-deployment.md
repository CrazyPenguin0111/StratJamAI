# Public coaching deployment — September 26, 2026

**Current status: stopped at the owner’s request.** The local Web hosts on ports 5080, 5081, and 5082, their CLI launchers, and the Cloudflare sharing tunnel were stopped on September 26, 2026. The temporary public address is offline. The deployment checks below describe the earlier running release; they are historical evidence, not a current availability check. Further engine work does not restart these services or deploy to the VDS.

The previous deployment at `https://brain-utilities-font-origin.trycloudflare.com` served online PvP, quick play, private lobby codes, clocks, and optional post-game review, alongside the defensive v4 AI and coaching interface on port 5081. Open `/pvp.html` or choose Play a friend from the main page. The Cloudflare tunnel was preserved during the server replacement. In-memory browser games were reset; saved histories can be loaded again. Visitors should refresh existing tabs.

`GET /api/info` identifies this deployment with `hostingVersion: "pvp-v1"`, `engine: "alpha-beta-defense-v4"`, and the new features `pvp`, `quick-play`, `lobby-codes`, `pvp-clocks`, and `post-game-review`. It retains `live-coach`, `analysis`, `history-sync`, `whole-turn-search`, `turnThinking: true`, and `maxThinkingMilliseconds: 20000`. The same engine serves browser opponents, live suggestions, and completed-game reviews. V4 reduces speculative income from exposed unfinished territory and adds a bounded capture extension at longer budgets. Short budgets retain ordinary depth search. See [disruption validation](enclosure-disruption-validation.md) for replay results, rejected prototypes, mixed match evidence, and limitations.

The Web host was published separately from the older CLI binaries into `/home/eugene/.local/share/stratjamai/public-20260926-pvp-v1`. Its PID is recorded in that directory's `server.pid`, with output in `server.log`. The previous release remains in `public-20260926-turn-budget` for rollback. The original sharing command still owns the public tunnel; stopping that command closes the public link but leaves this replacement Web host running.

To restart this published Web host after stopping its current process:

```sh
~/.dotnet/dotnet ~/.local/share/stratjamai/public-20260926-pvp-v1/StratJamAI.Web.dll --port 5081 --move-ms 1000 --max-searches 4 --max-sessions 128 --no-browser
```

All identified local Enclosure hosts and the sharing tunnel have now been stopped. Future source updates still require publishing/building and restarting the relevant host. Opening a running compatible server does not hot-reload it.

PvP defaults to 2:00 + 15 seconds per completed turn and random colors. Private rooms require both players to ready up. PvP has no AI moves or live suggestions; either participant can request review after the game ends. All search modes share the four-search CPU limit. Games and reviews remain in memory, so a restart loses them. See [multiplayer usage and VDS hosting notes](multiplayer.md).

For this release, all 95 Web tests and nine coaching JavaScript tests passed. Public two-browser checks passed private invitations, lobby color/clock edits, ready resets, three real placements, turn increments, quick play and cancellation, reconnecting, resignation, history downloads, and mobile layout. A separate public review check verified twelve reviewed placements, progress, cancellation/restart, historical navigation, and results shared by both participants. Play AI and live coaching passed again. No JavaScript errors occurred. Public HTML/CSS/JavaScript bytes matched the published files and retained `Cache-Control: no-cache`. See [multiplayer validation](multiplayer-validation.md) and its [deployment evidence](multiplayer-validation.json).

Before the preceding defensive-engine release, all 108 Core/CLI tests, 55 Web tests, and 9 JavaScript coaching tests passed (172 total). For the whole-turn update, all 68 Web tests, 12 launcher tests, and 9 JavaScript coaching tests passed (89 total). The published Core DLL matches the Web-tested artifact byte-for-byte (SHA256 `991dae460b01107888adb7741ee570e6fa8de3cec45397ce333260180c7e5602`). Test and match builds in other artifact directories have different assembly hashes; source hashes are preserved in the validation report.

The public `/api/info` returned `alpha-beta-defense-v4`, whole-turn thinking, and the 20,000 ms limit after replacement. Public browser checks verified two AI replies, live suggestions, analysis mode, applying a suggestion, isolation between visitors, the public address on the connection setup page, and userscript availability, with no JavaScript errors.

During the defensive-engine release, a separate coach session synchronized the first supplied history through move 102. The public engine returned D18–G15 (action 1639), completed depth two, and a hint revision matching the imported position. This is the improved move verified by the capture-pair regression. The search completed in about one second. Static responses still require cache revalidation; API responses use `no-store`.

The original-site companion integration was exercised during the preceding v3 deployment against the official frontend bundle with mocked authentication/services. That adapter was unchanged in v4; its nine JavaScript tests passed again. No real tournament account or remote game was used for these checks.

The current public slider still accepts 20 seconds. During the whole-turn release, an actual browser run issued one `/api/think` request and observed move counts 0 → 1 → 3: both Red placements appeared together. The AI used 20,001.8 ms, and the browser observed completion in 20.481 seconds including scheduling/network overhead. A 20,001 ms settings request was rejected. Hints remain per-position searches; see [turn-budget validation](turn-budget-validation.md).
