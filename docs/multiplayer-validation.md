# Multiplayer validation — September 26, 2026

This change adds human-only online matches and optional post-game review without changing the Core rules or defensive v4 engine.

## Automated checks

- All **95 Web tests** passed: the existing 68 session/coaching/whole-turn tests, 17 new multiplayer tests, and 10 new review tests.
- All **9 JavaScript coaching tests** passed.
- Both new frontend scripts passed JavaScript syntax checking.

Multiplayer tests use an injected monotonic clock to check the opening increment, both placements sharing one running clock, increment after the second placement only, late-move timeout precedence, disconnected-player expiry, and wall-clock changes. Other cases cover lobby consent, colors, queue compatibility and heartbeat, concurrent moves/joins, reconnecting, participant authorization, capacity and expiration, and replay of a complete 120-placement match.

Review tests check comparable static evaluations, recorded-history preservation, position frames, shared jobs, cancellation and restart without stale results, bounded capacity/expiry, failed-search retry, empty games, and sharing the same CPU semaphore with interactive searches.

Run the Web tests with the ASP.NET Core 10 runtime available:

```sh
dotnet test StratJamAI.Web.Tests/StratJamAI.Web.Tests.csproj -c Release
node --test tools/test-live-coach.cjs tools/test-coach-bridge.cjs
```

On this machine the tests used `/tmp/stratjam-public-build` and `RunConfiguration.DotNetHostPath=/home/eugene/.dotnet/dotnet`, since the system SDK and ASP.NET runtime are installed separately.

## HTTP integration

`tools/test-pvp-http.py` passed against an isolated running host. Separate cookie jars exercised default clocks, invitation joining, host-only clock edits, ready reset, opening/second-placement increments, stale and wrong-player moves, forged identity fields, cross-origin mutations, resignation, export, review, lowercase room-code canonicalization, quick play, and an actual ten-second timeout. Play AI's board remained separate from the PvP board. Active games and outsiders could not open or start reviews.

An independent 29-case HTTP check also passed malformed/null body validation, membership checks on all review operations, and coach-cookie isolation.

To repeat the HTTP checks on a disposable host:

```sh
python3 tools/test-pvp-http.py http://127.0.0.1:5081
```

The script creates temporary browser sessions and games; it does not reuse existing players' cookies.

## Browser checks

The existing Play AI and coaching browser check passed on the staging host: two AI replies, live hints, analysis mode, applying a suggestion, visitor isolation, connection setup, and userscript availability. No JavaScript errors occurred.

The review browser check played twelve real placements with two independent participants, resigned, replayed all thirteen historical frames, observed progress, cancelled and restarted analysis, checked all twelve review rows and suggested lines, navigated the replay, and opened the shared result as the second player. Its 390×844 mobile layout had no horizontal overflow or JavaScript errors.

`tools/test-pvp-browser.cjs` passed on staging and again through the public HTTPS tunnel after deployment. It checks explicit invitation joining, color conflicts, editable lobby colors, host clock changes resetting readiness, real board clicks, per-turn increments, reload/offline recovery, resignation, downloaded history, links back to older finished rooms, and quick play/cancellation. It checks mobile overflow and safely renders names containing HTML-like text. Quick-play checks use unusual matching clock settings to keep test queue entries separate from normal games.

```sh
node tools/test-pvp-browser.cjs http://127.0.0.1:5081
```

This browser script needs Playwright available to Node. On this machine, `NODE_PATH=/tmp/stratjam-browser-check/node_modules` supplies the existing installation.

The review and existing AI/coaching browser checks also passed again on `https://brain-utilities-font-origin.trycloudflare.com`. The public host reports `hostingVersion: "pvp-v1"`. All new static files and the main page matched the published release byte for byte; they require cache revalidation. The unchanged Core DLL SHA256 is `991dae460b01107888adb7741ee570e6fa8de3cec45397ce333260180c7e5602`. [Deployment evidence](multiplayer-validation.json) records the published path, process, API capabilities, and hashes.

These are functional checks, not multiplayer load testing or evidence of improved AI playing strength. Match state and review storage remain in memory in one process, as described in [multiplayer hosting](multiplayer.md).
