# Enclosure rules and feature schema

The `enclosure` adapter follows the [Enclosure practice board](https://meaf.us/sst1/), using the rules extracted from the site's `index-BCoKy2Nw.js` asset. The reference fixture records the extracted rules' SHA-256 as `c07aa32574295fd3e6ae7f26922095435e9f83f93ff63035db3673d7d01dab55`. This identifies the tested rules snapshot even if the website changes. The fixture covers three complete games, 363 positions, and SHA-256 fingerprints of every position's complete legal action list.

## Rules implemented

- The board contains integer coordinates `0..18` on both axes. Blue is player `0`, Red is player `1`. Blue starts with `(0,9)–(3,9)`; Red starts with `(18,9)–(15,9)`.
- Start from an existing node belonging to the moving player and choose a distinct endpoint at most three units away on each axis. The placement box permits diagonals such as `(3,3)` displacement.
- Own lines may cross, including at fractional coordinates. They may not overlap, pass through an existing own node, or place an endpoint in an existing own line's interior. Geometric crossings do not create playable nodes.
- A placement may intersect at most one enemy line. Endpoint touches and collinear overlaps count as intersections. An intersected enemy line is removed; protected enemy lines cannot be intersected.
- Remove enemy nodes that lose their final incident line after capture. Nodes still connected to another line remain. Captured lines' orphaned endpoints are not retained as future starting locations. Captures do not transfer ownership of enemy nodes; the moving player owns their new segment's two endpoints.
- Blue's opening turn has one placement. Subsequent turns have two, except the final turn is capped by the 120-placement limit. Newly placed lines are protected immediately. At turn end, all old protection expires and the lines placed during that turn stay protected through the opponent's following turn.
- Both players add their current enclosed area to their accumulated score at every turn end. Polygon subdivision retains fractional intersections, and nested disconnected closed components do not count twice. Areas and cumulative scores round to the nearest `1e-9`, matching the practice implementation.
- A forced pass is available only when no legal segment remains. It consumes all remaining placements in that turn, updates protection and scores, and passes play to the opponent. It uses the practice implementation's pass transition; it cannot be selected voluntarily in this adapter.
- The game ends at 120 consumed placements. The larger accumulated score wins. Training rewards remain zero until termination, when win/draw/loss returns are `+1/0/-1`; raw scores remain available separately.

## Stable action and coordinate mapping

Point index is `19*x+y` (X first). Enumerate all index pairs `a < b` in ascending order, retaining pairs whose endpoints differ by at most three on each axis. Their dense IDs are `0..7139`. This mapping includes geometrically possible segments irrespective of current legality. Pass has ID `7140`.

`Enclosure.GetActionId(x1,y1,x2,y2)` accepts either endpoint order and returns the same undirected segment ID. `DecodeAction(id)` returns endpoints in ascending point-index order. At least one endpoint must be an owned node when playing, so the returned first endpoint is not necessarily the human's selected starting node. Histories store coordinate pairs and replay with this undirected interpretation. Coordinates in histories and the UI are absolute; positive Y points upward.

## Observation schema

Schema ID: `enclosure-xmajor-relative-rot180-slots122x6-nodes722-globals7-action12-v1`.

There are 1,461 observation floats. Red's view rotates every coordinate by 180 degrees, `(x,y) → (18-x,18-y)`, and reverses segment endpoints to preserve ascending point index. Ownership is always relative to the observing player. This rotation applies only to features; IDs and public game coordinates stay absolute.

| Observation offsets | Contents |
| --- | --- |
| `0..731` | 122 active-segment slots, six floats each: `from.x/18`, `from.y/18`, `to.x/18`, `to.y/18`, owner (`+1` self, `-1` opponent), protection (`1` protected, `0` ordinary). |
| `732..1092` | Self node plane, one Boolean float per rotated X-major point. |
| `1093..1453` | Opponent node plane with the same indexing. |
| `1454..1455` | Self and opponent cumulative scores divided by `324*61`. |
| `1456..1457` | Self and opponent enclosed areas divided by `324`. |
| `1458` | Consumed placements divided by `120`. |
| `1459` | Placements remaining in the current turn divided by `2`. |
| `1460` | Acting-player indicator: `+1` if it is the observing player's turn, otherwise `-1`. |

Segment slots contain the two starting edges followed by surviving placements in chronological order. Capturing an edge compacts later slots; unused trailing slots are zero. Protection and whose turn it is determine which protected segments belong to the current turn. Any change to slot order, coordinate orientation, scaling or feature meanings requires a new schema version.

## Action feature schema

Every legal action supplies twelve floats. Segment endpoints use the moving player's rotated view and canonical endpoint order.

| Offset | Meaning |
| --- | --- |
| `0..3` | Endpoint coordinates `from.x/18`, `from.y/18`, `to.x/18`, `to.y/18`. |
| `4..5` | Endpoint displacement `dx/3`, `dy/3`. |
| `6` | Euclidean segment length divided by `sqrt(18)`. |
| `7..8` | Whether the respective endpoint is already an owned node. |
| `9` | Whether the placement cuts an enemy line. |
| `10` | Closure hint: both endpoints are owned, or the line meets an own line away from the starting node. This is a cheap possibility test; exact resulting area is computed by the simulator. |
| `11` | Pass indicator. A pass has all other features zero. |

Search uses compact game states and segment IDs directly. Its bounded area cache depends only on each color's undirected edge topology; turn, score and protection remain part of the separate full-state transposition key. Search copies update their keys and node/edge counts incrementally when a line is placed, captured, or loses protection. Legal moves and their capture/closure/expansion flags are written into reusable buffers from immutable bit masks. Tests compare these flags and cached fields against full recomputation across all 363 reference positions.

Search caches and search buffers belong to one invocation; synchronous area calculations reuse separate scratch storage on each thread, bounded by the game's 122-segment maximum. Territory rendering retains the independent polygon implementation used to check area parity. These optimizations preserve the action mapping, observation schema, and all legal choices.

The current strategy evaluation forecasts remaining scoring events and plausible connected enclosure completion. It also samples 81 board locations, using rival-relative endpoint distances to estimate space worth expanding toward. This is an unobstructed access estimate, not owned territory or a guarantee of crossing an opponent's walls. Its bounded contribution fades late in the game. Reinforcement and temporary protection do not award points by themselves; defensive moves are useful when they retain territory against searched responses. Thread-local caches retain bounded geometry and endpoint-distance maps. Final game results continue to use the actual accumulated scores only.
