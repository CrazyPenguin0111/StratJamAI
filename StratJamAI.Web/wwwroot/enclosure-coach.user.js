// ==UserScript==
// @name         StratJamAI Enclosure live coach
// @namespace    StratJamAI
// @version      1.0.0
// @description  Follow Enclosure moves with a local AI coach. Suggestions only; no automatic moves.
// @match        https://meaf.us/sst1/*
// @match        https://meaf.us/sst1
// @grant        none
// @run-at       document-idle
// @sandbox      raw
// @inject-into  page
// ==/UserScript==

(() => {
  "use strict";
  const colors = ["blue", "red"];
  const point = p => Array.isArray(p) && p.length === 2 && p.every(n => Number.isInteger(n) && n >= 0 && n < 19);
  function isBoard(s) {
    return !!s && typeof s === "object" && Number.isInteger(s.moveNumber) && s.moveNumber >= 0 && s.moveNumber <= 120 &&
      colors.includes(s.turn) && Number.isInteger(s.actionsRemaining) && colors.every(c =>
        Array.isArray(s.nodes?.[c]) && s.nodes[c].every(point) && Array.isArray(s.segments?.[c]) &&
        s.segments[c].every(e => point(e.from) && point(e.to)) && Number.isFinite(s.scores?.[c]) && Number.isFinite(s.areas?.[c]));
  }
  const edgeKey = e => [e.from.join(","), e.to.join(",")].sort().join(":");
  function cleanMove(move) {
    if (move?.pass === true) return { pass: true };
    if (!point(move?.from) || !point(move?.to)) throw new Error("The site supplied an incomplete move history.");
    return { from: [...move.from], to: [...move.to] };
  }
  function moveBetween(before, after) {
    if (!isBoard(before) || !isBoard(after)) throw new Error("Unsupported board state.");
    const old = new Set(before.segments[before.turn].map(edgeKey));
    const added = after.segments[before.turn].filter(e => !old.has(edgeKey(e)));
    if (after.moveNumber === before.moveNumber + 1 && added.length === 1) return cleanMove(added[0]);
    if (added.length === 0 && after.moveNumber === before.moveNumber + before.actionsRemaining && before.turn !== after.turn)
      return { pass: true };
    throw new Error("A placement was missed. Reconnect from a complete history or start a new game.");
  }
  function practiceMoves(board, previous) {
    if (board.moveNumber === 0) return [];
    if (!Array.isArray(previous) || !previous.length || previous[0]?.moveNumber !== 0 || previous.length > 120)
      throw new Error("The practice history is incomplete. Reset the practice board to begin tracking.");
    return previous.map((s, i) => moveBetween(s, previous[i + 1] || board));
  }
  function summary(s) {
    const p = pair => ({ x: pair[0], y: pair[1] });
    return { moveNumber: s.moveNumber, turn: colors.indexOf(s.turn), actionsRemaining: s.actionsRemaining,
      scores: colors.map(c => s.scores[c]), areas: colors.map(c => s.areas[c]),
      nodes: colors.map(c => s.nodes[c].map(p)),
      segments: colors.map(c => s.segments[c].map(e => ({ from: p(e.from), to: p(e.to), invincible: !!e.invincible }))) };
  }
  function consumed(moves) {
    let count = 0, remaining = 1;
    for (const move of moves) {
      const used = move.pass ? remaining : 1;
      count += used; remaining -= used;
      if (!remaining) remaining = Math.min(2, 120 - count);
    }
    return count;
  }
  // Export only pure adapters for the repository's Node regression tests.
  if (typeof module !== "undefined" && module.exports) {
    module.exports = { isBoard, cleanMove, moveBetween, practiceMoves, summary, consumed };
    return;
  }
  if (document.getElementById("stratjam-coach-panel")) return;

  // Read the current React tree, not its potentially stale alternate. Inspect only
  // board-shaped values and the known game prop; account/chat data is never copied.
  function readPosition() {
    const container = document.getElementById("root");
    const rootKey = container && Object.keys(container).find(k => k.startsWith("__reactContainer$"));
    const root = rootKey && container[rootKey]?.stateNode?.current;
    if (!root) throw new Error("Waiting for the game site. If it has loaded, the site adapter may need updating.");
    const stack = [root]; let seen = 0;
    while (stack.length && seen++ < 12000) {
      const fiber = stack.pop();
      if (fiber.sibling) stack.push(fiber.sibling);
      if (fiber.child) stack.push(fiber.child);
      const game = fiber.memoizedProps?.game;
      if (isBoard(game)) {
        const props = fiber.memoizedProps;
        // The online component's first state is the replay cursor. Do not label
        // the latest live position as the earlier position displayed in a replay.
        if ((props.spectating || props.reviewMode) && fiber.memoizedState?.memoizedState < (game.moveHistory?.length || 0))
          throw new Error("Move the replay cursor to the latest position to use live coaching.");
        return { board: game, moves: game.moveHistory, id: String(game.gameID || "online"), practice: false };
      }
      const state = fiber.memoizedState?.memoizedState;
      if (isBoard(state) && Array.isArray(fiber.memoizedState?.next?.memoizedState))
        return { board: state, moves: practiceMoves(state, fiber.memoizedState.next.memoizedState), id: "practice", practice: true };
    }
    throw new Error("Open a practice board or a game to start live coaching.");
  }

  const panel = document.createElement("section"); panel.id = "stratjam-coach-panel";
  Object.assign(panel.style, { position: "fixed", right: "12px", bottom: "12px", zIndex: "2147483647", width: "285px",
    maxWidth: "calc(100vw - 24px)", padding: "14px", borderRadius: "10px", background: "#fffefa", color: "#253037",
    boxShadow: "0 3px 22px #0004", border: "1px solid #b9c8b7", font: "13px/1.5 system-ui,sans-serif" });
  const heading = document.createElement("strong"); heading.textContent = "StratJamAI coach";
  const input = document.createElement("input"); input.type = "url"; input.value = "http://127.0.0.1:5081";
  input.setAttribute("aria-label", "StratJamAI server address");
  Object.assign(input.style, { display: "block", width: "100%", boxSizing: "border-box", margin: "8px 0", color: "#253037", background: "white" });
  const connect = document.createElement("button"); connect.textContent = "Connect coach";
  const disconnect = document.createElement("button"); disconnect.textContent = "Disconnect"; disconnect.hidden = true;
  const status = document.createElement("p"); status.textContent = "Start your local server, then connect."; status.style.margin = "8px 0 0";
  panel.append(heading, input, connect, disconnect, status); document.body.append(panel);
  let popup, targetOrigin, token, ready = false, lastKey = "", currentKey = "", lastPosition = null;
  let remembered = null, lastSent = 0, overlay;
  const clearOverlay = () => { overlay?.remove(); overlay = null; };
  function highlight(hint) {
    clearOverlay();
    if (!hint || hint.pass) return;
    const svg = document.querySelector("svg.board");
    const grid = svg?.querySelector(".grid-lines line");
    if (!grid || Number(grid.getAttribute("x2")) <= Number(grid.getAttribute("x1"))) return;
    const start = Number(grid.getAttribute("x1")), size = Number(grid.getAttribute("x2")) - start;
    overlay = document.createElementNS("http://www.w3.org/2000/svg", "line");
    const attrs = { x1: start + hint.from.x * size / 18, y1: start + (18 - hint.from.y) * size / 18,
      x2: start + hint.to.x * size / 18, y2: start + (18 - hint.to.y) * size / 18,
      stroke: "#57b86b", "stroke-width": 5, "stroke-dasharray": "7 5", "pointer-events": "none", opacity: .9 };
    for (const [key, value] of Object.entries(attrs)) overlay.setAttribute(key, value);
    svg.append(overlay);
  }
  function positionMessage() {
    const found = readPosition(); const board = found.board;
    let moves = Array.isArray(found.moves) ? found.moves.map(cleanMove) : [];
    if (moves.length > 120) throw new Error("The move history exceeds the supported game length.");
    if (consumed(moves) !== board.moveNumber) {
      // Live online clients sometimes omit history. Track observed legal state
      // transitions from the opening; never guess a midgame's accumulated score.
      if (remembered?.id === found.id && board.moveNumber === remembered.board.moveNumber) moves = remembered.moves;
      else if (remembered?.id === found.id) moves = [...remembered.moves, moveBetween(remembered.board, board)];
      else throw new Error("Waiting for the complete history. Connect before the first move if this game does not provide it.");
    }
    if (consumed(moves) !== board.moveNumber) throw new Error("Waiting for the remaining move history…");
    remembered = { id: found.id, board: structuredClone(board), moves };
    const expected = summary(board);
    const history = JSON.stringify({ formatVersion: 1, game: "enclosure", moves });
    const key = JSON.stringify([found.id, history, expected]);
    return { type: "position", key, history, expected };
  }
  function poll(force = false) {
    if (!popup) return;
    if (popup.closed) { stop(); status.textContent = "Coach window closed. Connect to resume."; return; }
    try {
      const message = positionMessage(); lastPosition = message;
      if (message.key !== currentKey) {
        currentKey = message.key; clearOverlay(); status.textContent = `Position ${message.expected.moveNumber} · updating suggestion…`;
      }
      if (ready && (force || message.key !== lastKey || Date.now() - lastSent > 5000)) {
        popup.postMessage({ channel: "stratjam-coach-v1", token, ...message }, targetOrigin);
        lastKey = message.key; lastSent = Date.now();
      }
    } catch (error) { currentKey = ""; clearOverlay(); status.textContent = error.message; }
  }
  function stop() { popup = null; ready = false; lastKey = currentKey = ""; remembered = lastPosition = null; clearOverlay(); connect.hidden = false; disconnect.hidden = true; }
  connect.addEventListener("click", () => {
    try {
      const url = new URL(input.value);
      if (!["http:", "https:"].includes(url.protocol) || url.username || url.password) throw new Error("Enter an HTTP or HTTPS server address.");
      token = [...crypto.getRandomValues(new Uint8Array(16))].map(n => n.toString(16).padStart(2, "0")).join("");
      targetOrigin = url.origin; url.pathname = "/"; url.search = "?coach=1"; url.hash = `token=${token}`;
      popup = window.open(url.href, "_blank", "popup,width=1150,height=850");
      if (!popup) throw new Error("Allow popups for this site, then connect again.");
      ready = false; lastKey = currentKey = ""; connect.hidden = true; disconnect.hidden = false;
      status.textContent = "Opening the coach. Keep its window open."; poll();
    } catch (error) { status.textContent = error.message; }
  });
  disconnect.addEventListener("click", () => { stop(); status.textContent = "Disconnected."; });
  window.addEventListener("message", event => {
    const data = event.data;
    if (event.source !== popup || event.origin !== targetOrigin || data?.channel !== "stratjam-coach-v1" || data.token !== token) return;
    if (data.type === "ready") { ready = true; poll(true); return; }
    if (data.type !== "suggestion") return;
    // Re-read before displaying, covering a move between the latest poll and reply.
    poll();
    if (!currentKey || data.key !== currentKey) return;
    if (data.error) { clearOverlay(); status.textContent = data.error; return; }
    if (data.hint) {
      const label = p => `${String.fromCharCode(65 + p.x)}${p.y + 1}`;
      status.textContent = `${colors[lastPosition.expected.turn]}: ${data.hint.pass ? "Pass" : `${label(data.hint.from)} — ${label(data.hint.to)}`} · depth ${data.search?.completedDepth ?? 0}`;
      highlight(data.hint);
    } else { clearOverlay(); status.textContent = data.finished ? "Game complete." : data.thinking ? "Analysing the current position…" : "Position synced. Enable Live suggestions in the coach window."; }
  });
  setInterval(poll, 300);
})();
