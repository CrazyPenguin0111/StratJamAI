"use strict";

(() => {
  const $ = id => document.getElementById(id);
  const svgNS = "http://www.w3.org/2000/svg";
  const colors = ["#2865bf", "#c65347"], names = ["Blue", "Red"];
  const origin = 52, step = 32;
  const invitation = new URLSearchParams(location.search).get("code")?.slice(0, 16).toUpperCase();
  let viewCode = invitation || null;
  let state = null, pending = false, selected = null, hovered = null;
  let pollTimer, requestQueue = Promise.resolve(), retryDelay = 0, retryAt = 0;
  let sessionId = null, disconnected = false, clockReceivedAt = performance.now();
  let boardKey = "", historyKey = "", settingsKey = "", lastCanPlay = false, lastAnnouncement = "";
  let keyboardPoint = { x: 9, y: 9 }, keyboardVisible = false;
  const same = (a, b) => a && b && a.x === b.x && a.y === b.y;
  const xy = point => ({ x: origin + point.x * step, y: origin + (18 - point.y) * step });
  const coordinate = point => `${String.fromCharCode(65 + point.x)}${point.y + 1}`;
  const actionLabel = action => action.pass ? "Pass" : `${coordinate(action.from)} — ${coordinate(action.to)}`;
  const number = value => new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(value);
  const isMember = () => state?.players?.some(player => player.isYou);
  const myPlayer = () => state?.players?.find(player => player.isYou);
  const canPlay = () => !!state?.board && state.status === "active" && state.viewerColor === state.board.turn && !pending && !disconnected;
  const roomPath = action => `${encodeURIComponent(state.code)}/${action}`;
  const shareLink = code => `${location.origin}/pvp.html?code=${encodeURIComponent(code)}`;

  function element(name, attributes = {}, text) {
    const node = document.createElementNS(svgNS, name);
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function showError(message) {
    $("error").textContent = message || "";
    $("error").hidden = !message;
  }

  function clockLabel(milliseconds, running = false) {
    const value = Math.max(0, milliseconds || 0);
    if (running && value < 10_000) return (value / 1000).toFixed(1);
    const seconds = Math.ceil(value / 1000);
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
  }

  function clockRule(settings) {
    return `${clockLabel(settings.initialSeconds * 1000)} + ${settings.incrementSeconds} s per turn`;
  }

  function api(path, body) {
    const operation = requestQueue.then(async () => {
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 10_000);
      try {
        let response;
        try {
          response = await fetch(`/api/pvp/${path}`, body === undefined
            ? { cache: "no-store", signal: controller.signal }
            : { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body), signal: controller.signal });
        } catch {
          const error = new Error("Connection interrupted. Reconnecting to your game…");
          error.retryable = true;
          throw error;
        }
        let value;
        try { value = await response.json(); } catch { /* A proxy may return an HTML error. */ }
        if (!response.ok) {
          const error = new Error(value?.error || "The request could not be completed.");
          error.status = response.status;
          error.retryable = [429, 502, 503, 504].includes(response.status);
          const retryAfter = response.headers.get("Retry-After");
          error.retryAfter = retryAfter && (/^\d+$/.test(retryAfter) ? Number(retryAfter) * 1000 : Date.parse(retryAfter) - Date.now());
          throw error;
        }
        if (!value || typeof value !== "object") {
          const error = new Error("The server did not return a game state. Reconnecting…");
          error.retryable = true;
          throw error;
        }
        const identity = response.headers.get("X-Enclosure-Session");
        if (identity && identity !== sessionId) {
          const replaced = sessionId !== null;
          sessionId = identity;
          state = null; selected = null; hovered = null;
          boardKey = ""; historyKey = ""; settingsKey = "";
          if (replaced) showError("Your browser session ended. Join or create a new game to continue.");
        }
        retryAt = 0; retryDelay = 0;
        disconnected = false;
        return value;
      } finally { clearTimeout(timeout); }
    }).catch(error => {
      if (error.retryable) {
        retryDelay = Math.min(15_000, Math.max(1500, retryDelay * 2));
        retryAt = Date.now() + Math.max(retryDelay, error.retryAfter || 0);
        disconnected = true;
      }
      throw error;
    });
    requestQueue = operation.catch(() => {});
    return operation;
  }

  function accept(next) {
    if (state && state.code === next.code && next.revision < state.revision) return;
    const changed = !state || next.code !== state.code || next.revision !== state.revision || next.status !== state.status;
    const queueEnded = state?.status === "queued" && next.status === "idle" && !pending;
    const started = next.status === "active" && state?.status !== "active";
    const previousCode = state?.code;
    state = next;
    viewCode = next.code || null;
    $("room-color").value = myPlayer()?.colorPreference || "random";
    clockReceivedAt = performance.now();
    if (changed) { selected = null; hovered = null; $("resign-confirm").hidden = true; }
    if (queueEnded) showError("Your queue entry ended. Find an opponent again when you're ready.");
    if (state.code) {
      const url = new URL(location.href);
      url.searchParams.set("code", state.code);
      history.replaceState(null, "", url);
    } else if (previousCode) {
      const url = new URL(location.href);
      url.searchParams.delete("code");
      history.replaceState(null, "", url);
      $("join-code").value = "";
    }
    render();
    if (started) requestAnimationFrame(() => document.querySelector(".board-toolbar").scrollIntoView({ block: "start" }));
    schedule();
  }

  function schedule() {
    clearTimeout(pollTimer);
    pollTimer = setTimeout(refresh, Math.max(1000, retryAt - Date.now()));
  }

  async function readState() {
    // A participant can reopen a completed room from its link. Reading an invitation
    // never joins it; nonparticipants return to the ordinary lobby with the code filled in.
    if (viewCode) {
      try { return await api(`state?code=${encodeURIComponent(viewCode)}`); }
      catch (error) {
        if (![400, 403, 404].includes(error.status)) throw error;
        viewCode = null;
      }
    }
    return api("state");
  }

  async function refresh() {
    if (pending) { schedule(); return; }
    const recovering = disconnected;
    try {
      const next = await readState();
      if (recovering) showError(null);
      accept(next);
    } catch (error) {
      showError(error.message);
      render();
      schedule();
    }
  }

  async function mutate(path, body) {
    if (pending) return;
    pending = true;
    clearTimeout(pollTimer);
    showError(null);
    render();
    try { accept(await api(path, body)); }
    catch (error) {
      if (!error.retryable) {
        try { accept(await readState()); } catch { /* Preserve the most recent usable state. */ }
      }
      showError(error.message);
    } finally {
      pending = false;
      render();
      schedule();
    }
  }

  function readClock(initialId = "initial-seconds", incrementId = "increment-seconds") {
    for (const id of [initialId, incrementId]) {
      if (!$(id).reportValidity()) return null;
    }
    const initialSeconds = Number($(initialId).value), incrementSeconds = Number($(incrementId).value);
    if (!Number.isInteger(initialSeconds) || initialSeconds < 10 || initialSeconds > 3600 ||
        !Number.isInteger(incrementSeconds) || incrementSeconds < 0 || incrementSeconds > 120) {
      showError("Choose a starting clock from 10 to 3600 seconds and an increment from 0 to 120 seconds.");
      return null;
    }
    return { initialSeconds, incrementSeconds };
  }

  function preferences() {
    const settings = readClock();
    if (!settings) return null;
    const result = { settings, colorPreference: $("color-preference").value, displayName: $("display-name").value.trim() };
    try { localStorage.setItem("enclosure.pvp.preferences", JSON.stringify(result)); } catch { /* Storage is optional. */ }
    return result;
  }

  function buildGrid() {
    for (let i = 0; i < 19; i++) {
      const p = origin + i * step;
      const kind = i % 3 === 0 ? "grid-line major" : "grid-line";
      $("grid").append(element("line", { x1: origin, x2: origin + 18 * step, y1: p, y2: p, class: kind }));
      $("grid").append(element("line", { y1: origin, y2: origin + 18 * step, x1: p, x2: p, class: kind }));
      $("grid").append(element("text", { x: p, y: 652, class: "coordinate" }, String.fromCharCode(65 + i)));
      $("grid").append(element("text", { x: 27, y: p, class: "coordinate" }, 19 - i));
    }
    for (const x of [3, 9, 15]) for (const y of [3, 9, 15]) {
      const p = xy({ x, y }); $("grid").append(element("circle", { cx: p.x, cy: p.y, r: 2, fill: "#bdbfaf" }));
    }
    for (let x = 0; x < 19; x++) for (let y = 0; y < 19; y++) {
      const p = xy({ x, y });
      $("hit-targets").append(element("circle", { cx: p.x, cy: p.y, r: 15, fill: "transparent", "data-x": x, "data-y": y }));
    }
  }

  function drawLine(container, from, to, attributes = {}) {
    const a = xy(from), b = xy(to);
    container.append(element("line", { x1: a.x, y1: a.y, x2: b.x, y2: b.y, "stroke-linecap": "round", ...attributes }));
  }

  function connectedActions(point) {
    return (state?.board?.legalActions || []).filter(action => !action.pass && (same(action.from, point) || same(action.to, point)));
  }

  const destination = (action, from) => same(action.from, from) ? action.to : action.from;

  function selectPoint(point) {
    if (!canPlay()) return;
    if (same(selected, point)) { selected = null; hovered = null; renderBoard(); renderStatus(); return; }
    if (selected) {
      const action = connectedActions(selected).find(candidate => same(destination(candidate, selected), point));
      if (action) { mutate(roomPath("move"), { revision: state.revision, action: action.id }); return; }
    }
    if (state.board.nodes[state.viewerColor].some(node => same(node, point)) && connectedActions(point).length) {
      selected = point; hovered = null; renderBoard(); renderStatus();
    } else $("selection-label").textContent = selected ? "Choose a highlighted endpoint or another node." : "Start from one of your colored nodes.";
  }

  function renderBoard() {
    for (const name of ["territories", "lines", "nodes", "legal-markers"]) $(name).replaceChildren();
    const board = state?.board;
    if (board) {
      for (let player = 0; player < 2; player++) {
        for (const polygon of board.territories[player]) {
          const points = polygon.map(point => { const p = xy(point); return `${p.x},${p.y}`; }).join(" ");
          $("territories").append(element("polygon", { points, fill: colors[player], "fill-opacity": .13 }));
        }
        for (const edge of board.segments[player]) {
          if (edge.invincible) drawLine($("lines"), edge.from, edge.to, { stroke: colors[player], "stroke-width": 8, "stroke-opacity": .12 });
          drawLine($("lines"), edge.from, edge.to, { stroke: colors[player], "stroke-width": 3.1 });
          if (edge.invincible) {
            const a = xy(edge.from), b = xy(edge.to);
            $("lines").append(element("circle", { cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2, r: 3.3, fill: "#fbf9f1", stroke: colors[player], "stroke-width": 1.7 }));
          }
        }
        for (const point of board.nodes[player]) {
          const p = xy(point); $("nodes").append(element("circle", { cx: p.x, cy: p.y, r: 4.4, fill: colors[player], stroke: "#fbf9f1", "stroke-width": 1 }));
        }
      }
      const last = board.history.at(-1);
      if (last && !last.action.pass) for (const point of [last.action.from, last.action.to]) {
        const p = xy(point); $("legal-markers").append(element("circle", { cx: p.x, cy: p.y, r: 7.2, fill: "none", stroke: colors[last.player], "stroke-opacity": .4, "stroke-width": 1 }));
      }
      if (selected && canPlay()) {
        const p = xy(selected); $("legal-markers").append(element("circle", { cx: p.x, cy: p.y, r: 9, fill: "none", stroke: colors[board.turn], "stroke-width": 2 }));
        for (const action of connectedActions(selected)) {
          const end = xy(destination(action, selected));
          $("legal-markers").append(element("circle", { cx: end.x, cy: end.y, r: 8.7, fill: colors[board.turn], "fill-opacity": .09, stroke: colors[board.turn], "stroke-opacity": .5, "stroke-width": 1.2 }));
        }
      }
    }
    renderPreview(); renderKeyboard();
  }

  function renderPreview() {
    $("preview").replaceChildren();
    if (selected && hovered && canPlay() && connectedActions(selected).some(action => same(destination(action, selected), hovered)))
      drawLine($("preview"), selected, hovered, { stroke: colors[state.viewerColor], "stroke-width": 2.6, "stroke-dasharray": "5 4", "stroke-opacity": .65 });
  }

  function renderKeyboard() {
    $("keyboard-marker").replaceChildren();
    if (!keyboardVisible || !state?.board) return;
    const p = xy(keyboardPoint);
    $("keyboard-marker").append(element("rect", { x: p.x - 11, y: p.y - 11, width: 22, height: 22, rx: 5, fill: "none", stroke: "#657760", "stroke-width": 1.6 }));
  }

  function renderStatus() {
    const board = state?.board;
    const status = state?.status || "idle";
    let title = "Choose your match.", description = "Quick play or a private room.";
    if (status === "queued") { title = "Finding an opponent…"; description = "Matching your clock and color preference."; }
    else if (status === "waiting") { title = "A seat is waiting."; description = "Both players choose Ready to start."; }
    else if (status === "active") {
      title = state.viewerColor === board.turn ? "Your turn." : `${names[board.turn]} to move.`;
      description = `${board.actionsRemaining} placement${board.actionsRemaining === 1 ? "" : "s"} remaining · You are ${names[state.viewerColor]}`;
      if (selected && canPlay()) title = `From ${coordinate(selected)} — choose an endpoint`;
    } else if (status === "finished") {
      title = state.winner === null || state.winner === undefined ? "An even match." : `${names[state.winner]} wins.`;
      const reason = { timeout: "Time ran out", resignation: "By resignation", score: "Final score" }[state.resultReason] || "Game over";
      description = `${reason} · Blue ${number(board?.scores[0] || 0)} · Red ${number(board?.scores[1] || 0)}`;
    }
    $("turn-title").textContent = title;
    $("turn-description").textContent = description;
    $("turn-dot").className = `turn-dot ${board?.turn === 1 ? "red" : "blue"}`;
    const limit = document.createElement("span"); limit.textContent = "/ 120";
    $("move-counter").replaceChildren(document.createTextNode(`${board?.moveNumber || 0} `), limit);
    $("selection-label").textContent = disconnected ? "Reconnecting. Your clock continues." : status === "finished" ? "Save the game or review it with AI." : selected && canPlay() ? `${connectedActions(selected).length} legal endpoints · click your node again to cancel` : canPlay() ? "Choose one of your colored nodes." : status === "active" ? "Waiting for your opponent." : "Choose a match to begin.";
    $("placeholder-title").textContent = status === "queued" ? "Finding your next opponent…" : status === "waiting" ? "Your room is ready." : "Your next game starts here.";
    $("placeholder-description").textContent = status === "queued" ? "Keep this page open. Your game starts automatically when a compatible opponent joins." : status === "waiting" ? "Send your room link to a friend. Both choose Ready when you're set to play." : "Choose a clock and find an opponent, or send a friend your private room link.";
    const announcement = `${status}|${board?.turn}|${board?.actionsRemaining}|${title}`;
    if (announcement !== lastAnnouncement) { $("announcement").textContent = `${title} ${description}`; lastAnnouncement = announcement; }
  }

  function renderClocks() {
    if (!state?.board) return;
    for (let player = 0; player < 2; player++) {
      const running = state.status === "active" && state.board.turn === player;
      const milliseconds = Math.max(0, (state.remainingMilliseconds?.[player] || 0) - (running ? performance.now() - clockReceivedAt : 0));
      const clock = $(player === 0 ? "blue-clock" : "red-clock");
      clock.textContent = clockLabel(milliseconds, running);
      clock.classList.toggle("low-time", state.status === "active" && milliseconds < 15_000);
      clock.title = "Display estimate; the server decides when time expires.";
    }
  }

  function renderRoom() {
    const status = state?.status || "idle";
    const me = myPlayer();
    const waiting = status === "waiting";
    $("room-status").textContent = { queued: "Finding a match", waiting: "Lobby", active: "Playing", finished: "Complete" }[status] || "";
    $("room-heading").textContent = status === "queued" ? "Quick play" : "Your room";
    $("room-share").hidden = !waiting;
    $("room-code").textContent = state?.code || "";
    $("share-url").value = state?.code ? shareLink(state.code) : "";
    $("room-players").replaceChildren();
    for (const player of state?.players || []) {
      const li = document.createElement("li");
      const dot = document.createElement("span"); dot.className = "pvp-member-dot";
      if (player.color === 0 || player.color === 1) dot.style.background = colors[player.color];
      const label = document.createElement("span"); label.className = "pvp-member-name";
      label.textContent = `${player.displayName || "Player"}${player.isYou ? " · You" : ""}`;
      const detail = document.createElement("small");
      const color = player.color === 0 || player.color === 1 ? names[player.color] : player.colorPreference === "random" ? "Either color" : `Prefers ${player.colorPreference}`;
      detail.textContent = `${color}${player.isHost ? " · Host" : ""}`; label.append(detail);
      const ready = document.createElement("span"); ready.className = `pvp-ready-state${player.ready ? " ready" : ""}`;
      ready.textContent = waiting ? player.ready ? "✓ Ready" : "Not ready" : "";
      li.append(dot, label, ready); $("room-players").append(li);
    }
    if (waiting && state.players.length < 2) {
      const empty = document.createElement("li"); empty.className = "pvp-action-note"; empty.textContent = "Waiting for a friend to join…"; $("room-players").append(empty);
    }
    $("room-clock").textContent = state?.settings ? clockRule(state.settings) : "";
    $("room-player-settings").hidden = !waiting || !me;
    $("room-color").disabled = pending || disconnected;
    $("room-edit").hidden = !waiting || !me?.isHost;
    const nextSettingsKey = `${state?.code}|${state?.settings?.initialSeconds}|${state?.settings?.incrementSeconds}`;
    if (nextSettingsKey !== settingsKey && state?.settings) {
      $("room-initial").value = state.settings.initialSeconds;
      $("room-increment").value = state.settings.incrementSeconds;
      settingsKey = nextSettingsKey;
    }
    $("room-message").textContent = status === "queued" ? "Keep this page open. We'll start as soon as a player with compatible settings arrives." : waiting ? me?.isHost ? "Set the clock, invite a friend, then both choose Ready." : "The host sets the clock. Check the settings and choose Ready." : status === "finished" ? "The match is complete. Start a new room whenever you're ready." : "Your clock keeps running while you make your placements.";
    $("ready").hidden = !waiting || !me;
    $("ready").textContent = me?.ready ? "Not ready yet" : "I'm ready";
    $("ready").disabled = pending || disconnected;
    $("update-settings").disabled = pending || disconnected;
    $("room-initial").disabled = pending;
    $("room-increment").disabled = pending;
    $("leave").hidden = status === "active" || !me;
    $("leave").disabled = pending || disconnected;
    $("leave").textContent = status === "queued" ? "Cancel search" : status === "finished" ? "Find another game" : "Leave room";
  }

  function render() {
    const status = state?.status || "idle", board = state?.board;
    document.body.classList.toggle("pvp-has-board", !!board);
    $("welcome").hidden = !!board;
    $("connection-notice").hidden = !disconnected;
    $("setup-card").hidden = status !== "idle";
    $("room-card").hidden = status === "idle";
    $("score-card").hidden = !board;
    $("history-card").hidden = !board;
    $("board-placeholder").hidden = !!board;
    for (const id of ["display-name", "color-preference", "initial-seconds", "increment-seconds", "join-code", "quick-play", "create-room", "join-room"]) $(id).disabled = pending || disconnected;
    $("resign").hidden = status !== "active" || !isMember();
    $("resign").disabled = pending || disconnected;
    $("resign-yes").disabled = pending || disconnected;
    $("pass").hidden = !canPlay() || !board.legalActions.some(action => action.pass);
    $("pass").disabled = pending;
    $("save").hidden = status !== "finished" || !isMember();
    $("save").disabled = pending;
    $("review").hidden = status !== "finished" || !isMember();
    $("review").href = state?.code ? `/review.html?code=${encodeURIComponent(state.code)}` : "#";
    $("result-note").hidden = status !== "finished";
    $("result-note").textContent = "Review with AI explores this completed game. You can also save a copy of its move history.";
    renderStatus(); renderRoom(); renderClocks();
    const nextBoardKey = `${state?.code}|${state?.revision}|${state?.viewerColor}|${status}`;
    if (nextBoardKey !== boardKey || canPlay() !== lastCanPlay) {
      renderBoard(); boardKey = nextBoardKey; lastCanPlay = canPlay();
    }
    if (board) {
      $("clock-rule").textContent = `${clockLabel(state.settings.initialSeconds * 1000)} + ${state.settings.incrementSeconds} s`;
      for (let player = 0; player < 2; player++) {
        const color = player === 0 ? "blue" : "red";
        const member = state.players.find(person => person.color === player);
        $(`${color}-name`).textContent = member?.displayName || names[player];
        $(`${color}-role`).textContent = `${names[player]}${state.viewerColor === player ? " · You" : ""}`;
        $(`${color}-score`).textContent = number(board.scores[player]);
        $(`${color}-area`).textContent = number(board.areas[player]);
        $(`${color}-card`).classList.toggle("active", status === "active" && board.turn === player);
      }
      const nextHistoryKey = `${state.code}|${state.revision}|${board.history.length}`;
      if (nextHistoryKey !== historyKey) {
        $("history-list").replaceChildren();
        if (!board.history.length) { const li = document.createElement("li"); li.className = "empty-history"; li.textContent = "Every enclosure starts with a line."; $("history-list").append(li); }
        for (const move of board.history) {
          const li = document.createElement("li");
          const index = document.createElement("span"); index.className = "history-number"; index.textContent = `${move.number}.`;
          const dot = document.createElement("span"); dot.className = `player-dot ${move.player === 0 ? "blue" : "red"}`; dot.setAttribute("aria-label", names[move.player]);
          const label = document.createElement("span"); label.className = "history-move"; label.textContent = actionLabel(move.action);
          const actor = document.createElement("span"); actor.className = "history-actor"; actor.textContent = state.viewerColor === move.player ? "YOU" : names[move.player].toUpperCase();
          li.append(index, dot, label, actor); $("history-list").append(li);
        }
        $("history-list").scrollTop = $("history-list").scrollHeight;
        historyKey = nextHistoryKey;
      }
      $("history-count").textContent = `${board.history.length} placement${board.history.length === 1 ? "" : "s"}`;
    }
  }

  $("quick-play").addEventListener("click", () => { const body = preferences(); if (body) mutate("quick", body); });
  $("create-room").addEventListener("click", () => { const body = preferences(); if (body) mutate("create", body); });
  $("join-form").addEventListener("submit", event => {
    event.preventDefault();
    const code = $("join-code").value.trim().toUpperCase();
    if (!code) { $("join-code").focus(); showError("Enter the room code your friend shared."); return; }
    mutate("join", { code, colorPreference: $("color-preference").value, displayName: $("display-name").value.trim() });
  });
  $("update-settings").addEventListener("click", () => {
    const settings = readClock("room-initial", "room-increment");
    if (settings) mutate(roomPath("settings"), { revision: state.revision, settings });
  });
  $("room-color").addEventListener("change", () => mutate(roomPath("settings"), {
    revision: state.revision, colorPreference: $("room-color").value
  }));
  $("ready").addEventListener("click", () => mutate(roomPath("ready"), { revision: state.revision, ready: !myPlayer()?.ready }));
  $("leave").addEventListener("click", () => mutate(roomPath("leave"), { revision: state.revision }));
  $("copy-link").addEventListener("click", async () => {
    if (!state?.code) return;
    try {
      await navigator.clipboard.writeText(shareLink(state.code));
      $("copy-status").textContent = "Link copied. Send it to your friend.";
    } catch {
      $("share-url").focus(); $("share-url").select();
      $("copy-status").textContent = "Select and copy the room link above.";
    }
  });
  $("pass").addEventListener("click", () => {
    const action = state?.board?.legalActions.find(candidate => candidate.pass);
    if (canPlay() && action) mutate(roomPath("move"), { revision: state.revision, action: action.id });
  });
  $("resign").addEventListener("click", () => { $("resign-confirm").hidden = false; $("resign-yes").focus(); });
  $("resign-no").addEventListener("click", () => { $("resign-confirm").hidden = true; });
  $("resign-yes").addEventListener("click", () => mutate(roomPath("resign"), { revision: state.revision }));
  $("save").addEventListener("click", async () => {
    if (!state?.code) return;
    try {
      const history = await api(roomPath("export"));
      const url = URL.createObjectURL(new Blob([JSON.stringify(history, null, 2)], { type: "application/json" }));
      const link = document.createElement("a"); link.href = url; link.download = `enclosure-${state.code}.json`;
      document.body.append(link); link.click(); link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (error) { showError(error.message); schedule(); }
  });
  $("board").addEventListener("click", event => {
    const target = event.target.closest("[data-x]");
    if (target) { keyboardVisible = false; selectPoint({ x: Number(target.dataset.x), y: Number(target.dataset.y) }); }
  });
  $("board").addEventListener("pointermove", event => {
    const target = event.target.closest("[data-x]");
    const next = target ? { x: Number(target.dataset.x), y: Number(target.dataset.y) } : null;
    if (same(next, hovered) || !next && !hovered) return;
    hovered = next; renderPreview();
  });
  $("board").addEventListener("pointerleave", () => { hovered = null; renderPreview(); });
  $("board").addEventListener("keydown", event => {
    const direction = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, 1], ArrowDown: [0, -1] }[event.key];
    if (direction) {
      event.preventDefault(); keyboardVisible = true;
      keyboardPoint = { x: Math.max(0, Math.min(18, keyboardPoint.x + direction[0])), y: Math.max(0, Math.min(18, keyboardPoint.y + direction[1])) };
      hovered = keyboardPoint; renderKeyboard(); renderPreview(); $("announcement").textContent = coordinate(keyboardPoint);
    } else if (event.key === "Enter" || event.key === " ") { event.preventDefault(); keyboardVisible = true; selectPoint(keyboardPoint); renderKeyboard(); }
    else if (event.key === "Escape") { selected = null; hovered = null; renderBoard(); renderStatus(); }
  });
  $("board").addEventListener("blur", () => { keyboardVisible = false; renderKeyboard(); });
  window.addEventListener("online", () => { retryAt = 0; clearTimeout(pollTimer); refresh(); });
  document.addEventListener("visibilitychange", () => { if (!document.hidden) { clearTimeout(pollTimer); refresh(); } });

  try {
    const saved = JSON.parse(localStorage.getItem("enclosure.pvp.preferences") || "null");
    if (saved) {
      if (typeof saved.displayName === "string") $("display-name").value = saved.displayName.slice(0, 20);
      if (["random", "blue", "red"].includes(saved.colorPreference)) $("color-preference").value = saved.colorPreference;
      if (Number.isInteger(saved.settings?.initialSeconds) && saved.settings.initialSeconds >= 10 && saved.settings.initialSeconds <= 3600) $("initial-seconds").value = saved.settings.initialSeconds;
      if (Number.isInteger(saved.settings?.incrementSeconds) && saved.settings.incrementSeconds >= 0 && saved.settings.incrementSeconds <= 120) $("increment-seconds").value = saved.settings.incrementSeconds;
    }
  } catch { /* Defaults work when storage is unavailable. */ }
  if (invitation) $("join-code").value = invitation;
  buildGrid(); render(); refresh();
  setInterval(renderClocks, 100);
})();
