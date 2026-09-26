# Online multiplayer

Open `/pvp.html` through the same host as Play AI. Players need separate browser sessions, not accounts. The server-issued, HttpOnly session cookie identifies a player; a room code is an invitation and does not authorize another visitor to move or read a finished match.

## Playing

- **Quick play:** select your clock and color preference, then choose Find an opponent. The queue matches equal clock settings and compatible colors. Two players requesting the same fixed color do not match. Matching starts the game immediately.
- **Private rooms:** create a room and send its code or link. The recipient chooses Join. Both players select Ready to start. The host can edit the clock; either player can edit their own color. Clock/color changes clear readiness so both players accept the new settings.
- **Defaults:** 120 seconds per player, a 15-second increment after a completed turn, and random colors. Blue's opening turn has one placement; later turns have two. The clock continues between the two placements, and the increment is added once. Supported clocks are 10–3,600 starting seconds and 0–120 increment seconds.
- **Reconnect:** refreshing in the same browser restores the room. Clocks continue during disconnection. Resigning explicitly ends an active match; closing a tab does not pause it.
- **Finish:** reaching the placement limit uses the game's cumulative score. Running out of time or resigning awards the game to the opponent. Finished matches can be saved in the existing portable JSON history format.

PvP never starts AI moves or provides live hints. Play AI and its existing coaching controls remain available from the navigation.

## Optional review

After the game, either participant can open **Review with AI**. Replay works immediately; analysis starts only when requested. Both participants share one review job and can cancel it. Reviews use the same managed CPU engine as Play AI, with 50–1,000 ms per placement (default 100 ms), and release their shared search slot between positions. Longer budgets take longer; a full 120-placement review at one second per placement needs roughly two minutes of search plus queue and processing time.

The review shows the played move, suggested alternative, search depth and continuation, historical boards, and per-player summaries. Static estimates compare both immediate resulting positions from the moving player's perspective. They are heuristic estimates, not win percentages, proven mistakes, or an accuracy rating. Short games ending in resignation or timeout can also be reviewed.

## Server lifetime and CPU hosting

For the complete Arch Linux setup with a custom domain, HTTPS, automatic startup, and updates, follow [VDS setup](vds-setup.md). The Caddy and systemd templates are included in `deploy/vds`.

The web application needs only the managed Core project and ASP.NET Core 10; TorchSharp, CUDA, and a trained model are unnecessary. Publish just the Web project:

```sh
dotnet publish StratJamAI.Web/StratJamAI.Web.csproj -c Release -o ./publish-web
dotnet ./publish-web/StratJamAI.Web.dll --port 5081 --no-browser --max-searches 2 --max-sessions 128
```

This binds loopback for a local reverse proxy. The existing `--public` option binds all IPv4 interfaces when direct access is desired. For a VDS, run the published host under the machine's service manager and put the stable HTTPS address on the reverse proxy or named tunnel. The current temporary tunnel is not a permanent address. The proxy should preserve `Host` and send `X-Forwarded-Proto`; forwarded scheme headers are trusted from loopback only.

Matches and reviews currently live in memory in one server process. Restarting loses rooms, sessions, and reviews. Multiple independent server replicas do not share a queue or game state. Export histories before restarting if they must be kept. Durable accounts, saved-match storage, and multi-server matchmaking are outside this version.

The default limits are 128 browser sessions, at most 128 rooms, 32 cached reviews, and four concurrent AI searches shared across modes. Two players consume two browser sessions. Quick-play entries expire after 90 seconds without polling; waiting private rooms after two hours of inactivity. Finished rooms are kept for up to two hours and may be evicted earlier when room capacity is needed. Reviews expire after one hour without access. API requests have per-session limits; each polling page uses approximately one state request per second.

`GET /api/info` reports `hostingVersion: "pvp-v1"` and the features `pvp`, `quick-play`, `lobby-codes`, `pvp-clocks`, and `post-game-review`. The engine remains `alpha-beta-defense-v4`, including the 20-second whole-turn thinking option for Play AI.
