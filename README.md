# StratJamAI

An all-C# framework for training and evaluating strategy-game bots under a wall-clock budget. Training uses TorchSharp on an NVIDIA GPU; the exported feed-forward network runs in plain .NET without a native ML runtime.

The main game is **Enclosure**, played on a 19×19 board using the current [official practice game rules](https://meaf.us/sst1/). It includes a browser interface, online multiplayer, and a CPU alpha-beta opponent. Tic-Tac-Toe remains as a small training and regression demo. The event server's wire protocol is not implemented.

## Play Enclosure

With the .NET 10 SDK and ASP.NET Core 10 runtime installed:

```sh
dotnet run --project StratJamAI -c Release -- ui
```

This opens **http://127.0.0.1:5080**. Select one of your nodes, then a highlighted endpoint. Choose Blue or Red before starting a new game. The interface shows territory, protected lines, cumulative scores, move history, search depth, and the AI's planned continuation. Hint, Undo, and Save/Load are included. Arrow keys move the board cursor; Enter selects a point.

Enable **Live suggestions** for automatic hints after each position changes. **Analyse game** lets you enter both players' moves while the AI coaches the side to move. To follow the original website automatically, use **Connect the original site** and its userscript; it opens a separate coaching session and follows the move history without submitting moves. See [live coaching setup and API](docs/live-coaching.md).

Choose **Play together** (`/pvp.html`) to play another person. **Find an opponent** matches players with the same clock and compatible color choices; **Create a private room** gives you a six-character code and invitation link. Private rooms start when both players are ready. Defaults are **2:00 + 15 seconds per completed turn**, with **random colors**. The host can change the lobby clock, and each player can choose Blue, Red, or Random before starting. PvP has no AI moves or live hints. After the match, **Review with AI** replays the board and compares each placement with the engine's suggestion. [Multiplayer details and hosting limits](docs/multiplayer.md).

The strategy evaluation projects territory income through the remaining game, estimates unfinished enclosures, and values space the two players can compete for. Extra walls do not earn a passive evaluation bonus. The space estimate declines near the end so closing territory takes priority over further expansion. These are estimates, not a guarantee of beating the double-wall strategy; the game rules and legal captures are unchanged. See [strategy validation](docs/enclosure-space-strategy-validation.md).

The opponent uses **iterative-deepening alpha-beta search**, with a default budget of **one second for the whole AI turn**. The slider ranges from **50 ms to 20 seconds**. The AI plans both remaining placements, then plays them together; a 20-second setting means 20 seconds total, not 20 seconds for each line. The opening single-placement turn uses the same total budget. Hints and live coaching use the selected time per suggestion. Adjust the budget in the page, or launch with `ui --move-ms 20000 --port 5081`. Use `--no-browser` to print the address without opening it. Playing needs neither a trained model nor CUDA; the web host references only the managed game core. You can also run it directly with `dotnet run --project StratJamAI.Web -c Release`.

## Let other people play

For permanent hosting on a CPU VDS with your own domain, follow the [Arch Linux VDS setup guide](docs/vds-setup.md). It includes Caddy HTTPS and systemd service templates, deployment commands, and the current limits of in-memory game storage.

For an Internet link without router changes:

```sh
dotnet run --project StratJamAI -c Release -- ui --share --port 5081
```

Copy the printed **Public Enclosure link** and send it to other players. Keep the computer awake and the hosting command running. Ctrl+C closes its tunnel and any server it started. The sharing command uses [Cloudflare Quick Tunnels](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/trycloudflare/); its temporary HTTPS address changes when a new tunnel starts. `cloudflared` must be on PATH or in `~/.local/bin` ([official downloads](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/downloads/)). It has been installed in `~/.local/bin` on the development machine. Quick Tunnels are for temporary sharing; a named tunnel with your own domain is appropriate for a stable address.

Each browser gets its **own AI game and player identity**. PvP participants share their room's board, while only the player whose turn it is can move. Refreshing reconnects; tabs in the same browser share the same player identity. Use different browsers or private browser sessions to test both seats. Sessions expire after two hours without API activity, and all unsaved games disappear when the server restarts. Save a game to keep it longer.

The defaults allow 128 browser sessions and four simultaneous searches. Opponent play, hints, and game reviews share that CPU limit; reviews release their slot between placements. Additional searches wait for a slot, then receive their full thinking budget. Use `--max-searches 2` to use less CPU or `--max-sessions 256` to allow more visitors. Requests have per-session rate limits and uploads are limited to 64 KiB.

For direct LAN access or your own reverse proxy/router configuration:

```sh
dotnet run --project StratJamAI -c Release -- ui --public --port 5081 --no-browser
```

Visitors on your network use `http://YOUR-LAN-IP:5081`. `--public` binds all IPv4 interfaces; reaching it from outside your network additionally requires your router/firewall configuration or a reverse proxy. `--share` uses a loopback server and does not need `--public`. A local HTTPS reverse proxy should preserve the public `Host` and send `X-Forwarded-Proto: https`; forwarded scheme headers are trusted only from loopback.

Port 5081 in these examples avoids the earlier local server on 5080. An older server must be stopped or a different port selected before sharing: the launcher refuses to expose the old single-game implementation. Repeat launches reuse a compatible server; its existing limits and thinking-time defaults remain in effect.

Search considers every legal move and returns the last fully completed depth. It tries cached and previously successful moves first, orders the remaining candidates only as needed, and uses principal-variation search to reduce repeated work. Board counts, position hashes, and move classifications are cached or updated incrementally. It handles the same player taking consecutive placements. Large middle-game action lists limit depth; at very small budgets, it returns a legal ordered fallback. See [Enclosure rules and encoding](docs/enclosure-rules.md), [measured validation](docs/enclosure-validation.md), and the [before/after search benchmarks](docs/enclosure-search-performance.md).

The defensive search also examines a bounded selection of immediate captures beyond its ordinary depth, including two captures during the same turn. It ranks cuts by the territory they remove and keeps alternative endpoints that can enable a second attack. Open territory near opposing nodes receives less speculative future income, so repeatedly repairing an exposed enclosure is less attractive. Distant construction and expansion retain their incentives. A quick ordinary search supplies a fallback before the additional tactical work. The time limit still applies; this selective extension does not exhaust every possible attack. See the [disruption replay checks](docs/enclosure-disruption-validation.md).

After updating the code, stop and restart the hosting command to use the new engine. Launching `ui` again while a compatible server is already running reuses that process; it does not reload its code.

Terminal play and history-based move suggestions use the same engine; their `--move-ms` option still sets the budget per individual move search:

```sh
dotnet run --project StratJamAI -c Release -- play --seat blue --save game.json
dotnet run --project StratJamAI -c Release -- suggest --history game.json --move-ms 1000
dotnet run --project StratJamAI -c Release -- evaluate --game enclosure --bot alpha-beta --move-ms 50 --pairs 4 --seconds 180
dotnet run --project StratJamAI -c Release -- benchmark-enclosure --samples 5 --compare --pairs 2 --output benchmark.json
```

`play` accepts `--bot alpha-beta|mcts|tactical|random|policy` and optional `--model bot.json`. Histories store zero-based coordinate pairs and replay every move to reconstruct scores and protection. A minimal history is `{"formatVersion":1,"game":"enclosure","moves":[]}`. Browser saves and terminal histories are interchangeable; invalid histories leave the current browser game intact.

## Train an Enclosure policy

Training is optional and separate from the browser opponent:

```sh
dotnet run --project StratJamAI -c Release -- doctor --device cuda
dotnet run --project StratJamAI -c Release -- train --config configs/enclosure.json --output runs/enclosure
dotnet run --project StratJamAI -c Release -- export --run runs/enclosure --output enclosure.bot.json
dotnet run --project StratJamAI -c Release -- play --model enclosure.bot.json
```

The Enclosure configuration uses smaller minibatches for its much larger legal-action lists. Pass `--device cpu` for CPU training, or `--minutes 5` for a shorter run. Final selection compares learned policies with tactical, random, alpha-beta, and MCTS candidates; the selected artifact may be a baseline. A short smoke run verifies the pipeline, not playing strength. Existing Tic-Tac-Toe artifacts remain compatible.

## Train the Tic-Tac-Toe demo

From the solution directory, with .NET 10 and the NVIDIA driver installed:

```sh
dotnet build StratJamAI.sln -c Release
dotnet test StratJamAI.sln -c Release --no-build
dotnet run --project StratJamAI -c Release --no-build -- doctor --device cuda
dotnet run --project StratJamAI -c Release --no-build -- train --config configs/default.json --output runs/first
```

The default is 60 minutes on CUDA. Package restore and compilation happen **before** the clock starts. The CUDA runtime is included in the TorchSharp package; a separate Python environment is unnecessary. Linux/CachyOS with the RTX 5080 is the verified platform. The project also selects the appropriate TorchSharp package on Windows or macOS, but those platforms have not been tested here.

For a shorter run or CPU training:

```sh
dotnet run --project StratJamAI -c Release --no-build -- train --minutes 5 --output runs/demo
dotnet run --project StratJamAI -c Release --no-build -- train --device cpu --minutes 1 --envs 8 --rollout 256 --batch 64 --output runs/cpu-demo
```

An existing nonempty output directory is never overwritten by a new run. To continue from a checkpoint:

```sh
dotnet run --project StratJamAI -c Release --no-build -- train --resume runs/first
```

Resume uses the original configuration and subtracts already consumed wall time. `--minutes` on resume means the **total cumulative budget**, not additional minutes. Increasing it is an explicit extension. You can also change `--device` or `--workers`; other settings require a new run. Environment episodes and partially collected rollouts restart; checkpointed weights, Adam state, random-generator states, opponent snapshots, and counters are restored. GPU runs are not promised to be bit-for-bit reproducible.

```sh
dotnet run --project StratJamAI -c Release --no-build -- evaluate --model runs/first/best.bot.json --pairs 64 --seconds 120 --output runs/first/audit.json
dotnet run --project StratJamAI -c Release --no-build -- export --run runs/first --output runs/first/bot.json
dotnet run --project StratJamAI -c Release --no-build -- export --run runs/first --output runs/first/network.json --policy-only
dotnet run --project StratJamAI -c Release --no-build -- play-demo --model runs/first/bot.json --human
```

`play-demo` without `--human` runs the exported bot as O against a random X. `benchmark` measures simulator throughput and selects a CPU worker count. `doctor` runs forward/backward computation on the requested device and checks portable inference parity. CUDA failure produces an error; CPU mode is an explicit choice.

## Components

| Project | Responsibility |
| --- | --- |
| `StratJamAI.Core` | Enclosure and demo rules, managed inference, alpha-beta/PUCT search, evaluation, artifacts |
| `StratJamAI` | CLI, TorchSharp model and PPO, parallel rollouts, training supervisor, checkpoints |
| `StratJamAI.Web` | Browser AI games, online PvP lobbies and clocks, and post-game review, without TorchSharp |
| `StratJamAI.Tests` | Algorithm, game-contract, export, checkpoint, and process-level budget tests |
| `StratJamAI.Web.Tests` | UI sessions, multiplayer clocks and membership, review jobs, cancellation, and history tests |

```mermaid
flowchart LR
    G[Game adapter] --> O[Player observations and legal actions]
    O --> R[Parallel self-play and batched inference]
    R --> P[PPO updates on GPU]
    P --> R
    P --> W[Portable network weights]
    W --> E[Held-out evaluation]
    B[Tactical and search baselines] --> E
    E --> A[Selected bot artifact]
    A --> C[Plain C# bot core]
```

The network has two shared 128-unit ReLU layers. Each legal action is represented by a feature vector; a 64-unit action head scores that vector with the state embedding. The state contribution is computed once and shared across actions, preserving the original concatenated-head weights and outputs while reducing work. A separate scalar head estimates the acting player's return. This allows a different number of legal actions in each state without a hard-coded action vocabulary. Dimensions and feature meanings remain fixed within a versioned game schema.

Exported policies choose the highest-scoring action in sequential, fully observed games. They sample the learned distribution by default in simultaneous or partially observed games, where a predictable pure strategy can be exploitable. The artifact's optional `samplePolicy` setting overrides that default. Each evaluated player has an independent random stream, so one bot's search cannot change its opponent's randomness.

PPO defaults: 64 environments, 8,192 learner decisions per rollout, four epochs, minibatches of 512, Adam at `3e-4`, gamma `0.99`, lambda `0.95`, ratio clipping `0.2`, entropy weight `0.01`, value weight `0.5`, gradient clipping `0.5`, and a KL stop threshold of `0.02`. All are configurable through JSON. Padding is masked both when sampling and when recomputing log probabilities. GAE discounts by elapsed simulator steps; lambda applies per learner decision. True terminal states do not bootstrap; time limits bootstrap the final visible observation and end the episode's trace.

Each match has one learner seat, rotated between episodes. Opponents are approximately 20% random, 30% tactical when available, and 50% recent frozen policy snapshots. One opponent policy is chosen per match. At most four snapshots are retained by default. Rollout inference is batched on the training device; independent CPU game instances advance in parallel. Weights remain fixed during rollout collection. Optional tactical imitation precedes PPO and is never mislabeled as on-policy experience.

PUCT search is available only for deterministic, sequential, fully observable, two-player zero-sum games with a clonable simulator. It uses actual player identities during backup, including extra turns. Search is bounded by simulations, depth, and a per-move deadline. A legal fallback is selected before search starts. An interrupted simulation is not backed up as a draw. The model's policy and value can guide search, but the final selection also evaluates search without the learned model.

## Time, evaluation, and artifacts

| Portion of a 60-minute run | Work |
| --- | --- |
| First 2 minutes | Device/adapter checks, throughput calibration, initial-policy evaluation |
| Through minute 5 | Optional tactical imitation, capped at 128 updates by default |
| Through minute 55 | PPO, periodic policy evaluation, opponent snapshots, checkpoints |
| Through minute 59 | Compare the incumbent, policy candidates, random/tactical bots, and eligible search bots |
| Remaining minute | Final checkpoint, export, and report |

These phase boundaries scale with the total budget; unused optional preparation time goes to PPO. Very short budgets can expire during startup. The parent supervises a separate worker, records consumed time, and kills that worker if it exceeds the overall deadline. Exit code `124` means the watchdog stopped the worker; `130` means interruption. Existing atomic artifacts remain usable. Adapter operations should be fast and bounded; the watchdog is the backstop for a stalled native call or simulator.

Evaluation uses separate seeds from training, all player positions, and a fixed suite of available random, tactical, and exact-oracle opponents. Scores map adapter returns from `[-1, 1]` into `[0, 1]`; in Tic-Tac-Toe this is win = 1, draw = 0.5, loss = 0. Reports include opponent-specific means, move latency, and an approximate 95% bootstrap interval over seat-balanced groups. The highest complete mean score wins; ties retain the incumbent. An incomplete match or suite is never treated as a draw or used to promote a bot. The exact minimax opponent is used for evaluation only.

Files in each run:

| File | Meaning |
| --- | --- |
| `best.bot.json` | Selected complete bot, including its kind and search settings; initially a legal baseline |
| `best-policy.bot.json` | Strongest evaluated neural-network candidate, without search |
| `latest.bot.json` | Latest training network, which may be weaker than the best candidate |
| `config.json`, `doctor.json` | Effective settings and actual device/parity check |
| `metrics.jsonl`, `progress.json` | Training losses, entropy, KL, throughput, process memory, episode statistics |
| `initial-policy.evaluation.json`, `policy-evaluations.json` | Initial and periodic learned-policy results |
| `final-evaluations.json`, `report.json` | Final candidate comparison and run summary |
| `supervisor.json` | Parent status and charged wall time, including evaluation and export |
| `checkpoint.json`, `checkpoints/` | Atomic generation pointer and two checksummed resumable generations |

A checkpoint is committed only after its model/configuration data and Adam state are written. If the newest generation is damaged, loading falls back to the preceding intact generation. Files inside unfinished checkpoint directories are ignored. Export files are also replaced atomically.

`BufferMemoryMiB` bounds stored rollout data and rejects padded tensor batches whose estimated working storage exceeds that amount. It is not a cap on the CUDA context, native allocator, or total process RSS. Reduce `environments` or `minibatchSize` if a game's action lists are large. No legal actions are silently discarded to fit a buffer.

## Add another game

1. Implement `IGameDefinition` and `IGameAdapter`, then register the definition in `GameRegistry.Get`. The registry is the only game-specific selection point in the CLI/trainer.
2. Give the adapter a stable `GameSpec.Id` and a versioned `FeatureSchema`. Supply fixed-length, finite, normalized observation/action features. Version rules/reward changes in the game ID or schema, and change the schema whenever feature meanings, scaling, or action interpretation change.
3. `Reset(seed)` initializes a reproducible independent match. `Frame.Decisions` identifies every currently acting player. `Step(jointActions)` applies all supplied actions together. Every request's IDs must be unique and legal for that frame; include an explicit pass/no-op when required.
4. `Observe(player)` must contain only information available to that player. It must work between their turns and at truncation, for value bootstrapping. The adapter owns any observation-history encoding. Do not pass simulator secrets to a policy in a partially observed game.
5. `Frame.Rewards[player]` is the immediate reward from the last simulator transition, including transitions made by opponents. `Returns[player]` is the normalized final match utility in `[-1, 1]` used for evaluation. Set exactly one of `Terminated` or `Truncated` at an episode boundary and expose no action requests afterward.
6. Requests are snapshots: do not mutate their arrays before the next `Step`. The rollout collector makes an owned copy before advancing the simulator. Each worker needs an independent environment and random state; game factories and teacher factories must be safe to call from multiple workers.
7. Optionally provide an observation-respecting tactical teacher and independent evaluation oracle. Only implement `ISearchableGame.Fork` if the declared search capabilities are true. A fork must preserve the exact state and be independently mutable.
8. Add a separate event transport that converts official inputs into these requests and converts returned action IDs into official commands. Configure the actual move limit and validate the simulator against official examples before training.

To use an artifact in a bot host, reference **only** `StratJamAI.Core`:

```csharp
var game = GameRegistry.Get("enclosure");
var artifact = BotArtifact.Load("bot.json", game.Spec);
IBot bot = artifact.CreateBot(game);

int actionId = bot.ChooseAction(request,
    new BotContext(random, Deadline.After(TimeSpan.FromMilliseconds(50)), searchableState));
```

The host must keep action IDs mapped to the current request and provide `searchableState` only where full-information search is supported. The artifact's selected tactical/search bot also requires that game's C# implementation. Native TorchSharp files are not needed by this host.

Continuous-action games, pixel-only observations, learned recurrent memory, hidden-information search, and the event wire protocol are outside this version.

## Validation

```sh
dotnet test StratJamAI.sln -c Release
```

Tests include a contextual bandit learned by PPO without demonstrations or search; inference parity and padding masks; terminal/truncated GAE; rewards on opponent turns; simultaneous-player privacy; extra-turn search; checkpoint/Adam continuation and corruption recovery; and subprocess budget enforcement. The GPU `doctor` check is separate from the CPU test suite.

Enclosure tests additionally compare 363 positions against the official JavaScript simulator, including exact legal-action sets, areas, scores, protection, and terminal results. They cover search deadlines, cancellation, consecutive placements, exhaustive shallow-search agreement, portable-network parity, and browser session state. The web tests require the ASP.NET Core 10 runtime. If it is installed separately under `~/.dotnet`, select that host explicitly:

```sh
dotnet test StratJamAI.sln -c Release -- RunConfiguration.DotNetHostPath="$HOME/.dotnet/dotnet"
```

See [validation results](docs/validation.md) for the measured local demonstration run.

Algorithm references: [PPO](https://arxiv.org/abs/1707.06347), [invalid-action masking](https://arxiv.org/abs/2006.14171), and [TorchSharp memory management](https://github.com/dotnet/TorchSharp/wiki/Memory-Management).
