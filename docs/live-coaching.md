# Live Enclosure coaching

Start the updated host on a free port:

```sh
dotnet run --project StratJamAI -c Release -- ui --port 5082
```

Using a different port lets an existing public game keep running. Relaunching on an occupied compatible port reuses that process and does not update its code.

## On this app's board

Enable **Live suggestions** to receive a fresh suggestion each time the position changes. In **Play AI**, hints are for your turn and the opponent continues to play normally. Choose **Analyse game** to enter both players' moves yourself, with a suggestion for whichever color is to move. **Play suggestion** applies the displayed move only when you click it.

Thinking time ranges from 50 ms to 20 seconds. In Play AI, that is one shared budget for the opponent's entire turn: both remaining lines are planned before either appears. Hints and live suggestions use that time for each requested position and still suggest only one placement to apply. Waiting for a shared CPU slot is separate from thinking time.

Moving while analysis runs cancels the old search. Undo, load, reset, mode changes, and thinking-time changes also invalidate old hints. In analysis mode Undo removes one placement; in Play AI it returns to before your latest human placement, including subsequent AI moves. Search statistics and suggested continuations carry the position revision they belong to.

## Following the original site

Open **Connect the original site** in the app, install its userscript in a browser userscript manager, and reload [the Enclosure site](https://meaf.us/sst1/). The script adds a **StratJamAI coach** panel. Enter your running host's address, click **Connect coach**, and allow the popup. Keep both tabs open. Play moves in the original tab; suggestions and a dashed green line appear there, with a full reconstructed board in the coach window.

The default address in the script is `http://127.0.0.1:5081`; change it to your actual port, such as `http://127.0.0.1:5082`, or your host's HTTPS address. The popup uses a separate session cookie and does not replace an ordinary game on the same host. No CORS exception or browser security setting is needed: the two windows exchange messages restricted to the expected origin, opener, and connection token. API calls stay on the host's origin.

Public visitors can use the hosted AI directly; they do not need a local server. The setup page displays the public address to enter in the userscript. Each visitor's ordinary game and coaching session remain separate from other visitors' sessions.

After refreshing the coach popup, click **Disconnect**, then **Connect coach** in the original tab to establish a new connection.

The adapter observes the current React game state. Practice undo-history allows connection midway through a game, including undo, redo, and reset. For an online game, it uses the complete move history when available; otherwise it tracks each observed transition from the opening. Missing history or skipped placements stop suggestions. Connecting before the first placement avoids depending on the server supplying old moves.

Moves are replayed through the C# rules. The bridge compares the reconstructed move count, turn, remaining placements, scores, areas, nodes, and protected segments with the source position before publishing a hint. The adapter sends only board and move data, not account details or chat. It never submits a move on the original site. A changed site state layout or rules can require an adapter update; mismatched positions show an error instead of a suggestion.

## Integration API

Each API client must retain its session cookie. `X-Enclosure-Coach: 1` selects the separate coaching cookie. Read `GET /api/state`, then call:

```json
POST /api/sync
{
  "revision": 1,
  "history": "{\"formatVersion\":1,\"game\":\"enclosure\",\"moves\":[]}"
}
```

Use the current `revision` from state, not the example's literal `1`. Supply either `history` (the existing save-file JSON as a string) or `actions` (an array of action IDs), never both. Full replay must be legal and at most 120 moves. Invalid input leaves the existing position intact. A stale revision returns 409; fetch current state and resubmit the latest complete history. Identical histories preserve a running search and its revision. Sync enables analysis mode and live coaching.

Poll `GET /api/state` while `thinking` is true. A suggestion is current only when `hintRevision == revision`; `lastSearch.positionRevision` identifies its search position. A suggestion never changes the game. `POST /api/settings` accepts `moveMilliseconds`, optional `analysisMode`, and optional `liveCoach`. Normal hosting session limits, rate limits, request-size limits, and shared search concurrency apply to coach sessions too.

## Verification

Backend tests cover both colors, consecutive placements, automatic hints after changes, stale results from evaluators that ignore cancellation, history replay parity, idempotence, and rejected histories. Run `node --test tools/test-live-coach.cjs` for adapter tests against three complete official-reference games (363 board states), malformed/missing transitions, privacy filtering, and pass accounting.
