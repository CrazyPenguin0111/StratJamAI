"use strict";

(() => {
  const $ = id => document.getElementById(id);
  const svgNS = "http://www.w3.org/2000/svg";
  const colors = ["#2865bf", "#c65347"];
  const names = ["Blue", "Red"];
  const origin = 52, step = 32;
  const coachConnection = new URLSearchParams(location.search).get("coach") === "1";
  let state, selected = null, hovered = null, pending = false, pollTimer;
  let keyboardPoint = { x: 9, y: 9 }, keyboardVisible = false, lastHistoryCount = -1;
  let lastAnnouncement = "", lastRenderedRevision = -1, lastHintAction = null;
  let sessionIdentity = null, requestQueue = Promise.resolve(), retryAt = 0, retryDelay = 0;
  let syncQueue = Promise.resolve();
  let connectionError = false;
  const responseSessions = new WeakMap();
  const same = (a, b) => a && b && a.x === b.x && a.y === b.y;
  const xy = p => ({ x: origin + p.x * step, y: origin + (18 - p.y) * step });
  const coordinate = p => `${String.fromCharCode(65 + p.x)}${p.y + 1}`;
  const actionLabel = action => action.pass ? "Pass" : `${coordinate(action.from)} — ${coordinate(action.to)}`;
  const number = n => new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(n);
  const timeLabel = ms => ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)} s`;

  function element(name, attributes = {}, text) {
    const node = document.createElementNS(svgNS, name);
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function buildGrid() {
    for (let i = 0; i < 19; i++) {
      const position = origin + i * step;
      const kind = i % 3 === 0 ? "grid-line major" : "grid-line";
      $("grid").append(element("line", { x1: origin, x2: origin + 18 * step, y1: position, y2: position, class: kind }));
      $("grid").append(element("line", { y1: origin, y2: origin + 18 * step, x1: position, x2: position, class: kind }));
      $("grid").append(element("text", { x: position, y: 652, class: "coordinate" }, String.fromCharCode(65 + i)));
      $("grid").append(element("text", { x: 27, y: position, class: "coordinate" }, 19 - i));
    }
    for (const x of [3, 9, 15]) for (const y of [3, 9, 15]) {
      const p = xy({ x, y });
      $("grid").append(element("circle", { cx: p.x, cy: p.y, r: 2, fill: "#bdbfaf" }));
    }
    for (let x = 0; x < 19; x++) for (let y = 0; y < 19; y++) {
      const p = xy({ x, y });
      $("hit-targets").append(element("circle", { cx: p.x, cy: p.y, r: 15, fill: "transparent", "data-x": x, "data-y": y }));
    }
  }

  function showError(message) {
    $("error").textContent = message || "";
    $("error").hidden = !message;
  }

  function api(path, body) {
    // Keep cookie renewal and state replies ordered when a browser session expires.
    const operation = requestQueue.then(async () => {
      let response;
      try {
        const headers = coachConnection ? { "X-Enclosure-Coach": "1" } : {};
        response = await fetch(`/api/${path}`, body === undefined ? { cache: "no-store", headers } : {
          method: "POST", headers: { ...headers, "Content-Type": "application/json" }, body: JSON.stringify(body)
        });
      } catch {
        const error = new Error("Unable to reach the game server. Reconnecting…");
        error.retryable = true;
        throw error;
      }
      let value;
      try { value = await response.json(); }
      catch { /* A busy proxy can return HTML instead of JSON. */ }
      if (!response.ok) {
        const busy = response.status === 429 || response.status === 503;
        const error = new Error(value?.error || (busy
          ? "The server is busy. Please try again in a moment."
          : "The request could not be completed."));
        error.status = response.status;
        error.retryable = busy;
        const retryAfter = response.headers.get("Retry-After");
        error.retryAfter = retryAfter && (/^\d+$/.test(retryAfter)
          ? Number(retryAfter) * 1000 : Date.parse(retryAfter) - Date.now());
        throw error;
      }
      if (!value || typeof value !== "object") {
        const error = new Error("The game server did not return a valid response. Reconnecting…");
        error.retryable = true;
        throw error;
      }
      responseSessions.set(value, response.headers.get("X-Enclosure-Session"));
      retryAt = 0;
      retryDelay = 0;
      return value;
    }).catch(error => {
      if (error.retryable) {
        retryDelay = Math.min(15000, Math.max(1500, retryDelay * 2));
        retryAt = Date.now() + Math.max(retryDelay, error.retryAfter || 0);
        connectionError = true;
      }
      throw error;
    });
    requestQueue = operation.catch(() => {});
    return operation;
  }

  function accept(next) {
    if (connectionError) { connectionError = false; showError(null); }
    const identity = responseSessions.get(next);
    if (identity && identity !== sessionIdentity) {
      const replaced = sessionIdentity !== null;
      sessionIdentity = identity;
      state = null;
      selected = null;
      hovered = null;
      lastHistoryCount = -1;
      lastRenderedRevision = -1;
      lastHintAction = null;
      lastAnnouncement = "";
      delete $("history-list").dataset.revision;
      if (replaced) showError("Your previous session ended. A new game is ready; load a saved game to continue it.");
    }
    if (state && next.revision < state.revision) return;
    const changed = !state || next.revision !== state.revision;
    if (!state) {
      $("seat").value = next.humanPlayer;
    }
    if (!state || next.moveMilliseconds !== state.moveMilliseconds) $("move-time").value = next.moveMilliseconds;
    $("game-mode").value = next.analysisMode ? "analysis" : "play";
    $("live-coach").checked = !!next.liveCoach;
    state = next;
    if (changed) { selected = null; hovered = null; }
    render();
    schedule();
  }

  async function mutate(path, body) {
    if (pending) return;
    if (coachConnection && ["new", "move", "undo", "import", "think"].includes(path)) return;
    pending = true;
    showError(null);
    renderControls();
    try { accept(await api(path, body)); }
    catch (error) {
      if (!error.retryable) {
        try { accept(await api("state")); } catch { /* Keep the last usable position. */ }
      }
      showError(error.message);
    } finally {
      pending = false;
      render();
      schedule();
    }
  }

  function schedule() {
    clearTimeout(pollTimer);
    if (retryAt > Date.now()) {
      pollTimer = setTimeout(refresh, retryAt - Date.now());
      return;
    }
    if (!state) return;
    if (state.thinking) pollTimer = setTimeout(refresh, 150);
    else if (needsAiTurn())
      // One job plans and publishes the remaining AI turn. Recheck after the delay in
      // case a reset, mode change, or another state reply arrived before this callback.
      pollTimer = setTimeout(() => {
        if (needsAiTurn()) mutate("think", { revision: state.revision });
      }, 180);
    else if (coachConnection || state.analysisMode || state.liveCoach)
      pollTimer = setTimeout(refresh, 1000);
  }

  function needsAiTurn() {
    return state && !coachConnection && !state.analysisMode && !pending && !state.thinking &&
      !state.finished && state.turn !== state.humanPlayer && !state.error;
  }

  function renderBudget() {
    const suggestion = coachConnection || !!state?.analysisMode;
    const unit = suggestion ? "suggestion" : "AI turn";
    $("move-time-label").textContent = suggestion ? "Suggestion budget" : "AI turn budget";
    $("move-time-value").textContent = `${timeLabel(Number($("move-time").value))} / ${unit}`;
    $("move-time-maximum").textContent = `Deeper · 20 s / ${unit}`;
  }

  async function refresh() {
    try { accept(await api("state")); }
    catch (error) {
      showError(error.message);
      clearTimeout(pollTimer);
      pollTimer = setTimeout(refresh, Math.max(1500, retryAt - Date.now()));
    }
  }

  function canPlay() {
    return state && !coachConnection && !state.finished && (state.analysisMode || state.turn === state.humanPlayer) && !pending;
  }

  function currentHint() {
    if (!state?.hint || state.finished || state.hintRevision !== state.revision) return null;
    return state.legalActions.find(action => action.id === state.hint.id) || null;
  }

  function connectedActions(point) {
    return state.legalActions.filter(action => !action.pass && (same(action.from, point) || same(action.to, point)));
  }

  function destination(action, from) { return same(action.from, from) ? action.to : action.from; }

  function selectPoint(point) {
    if (!canPlay()) return;
    if (same(selected, point)) { selected = null; hovered = null; renderBoard(); renderStatus(); return; }
    if (selected) {
      const action = connectedActions(selected).find(action => same(destination(action, selected), point));
      if (action) { mutate("move", { revision: state.revision, action: action.id }); return; }
    }
    if (state.nodes[state.turn].some(node => same(node, point)) && connectedActions(point).length) {
      selected = point;
      hovered = null;
      renderBoard();
      renderStatus();
    } else {
      $("selection-label").textContent = selected ? "Choose a highlighted endpoint, or another node." : `Start from one of ${names[state.turn]}'s nodes.`;
    }
  }

  function drawLine(container, from, to, attributes = {}) {
    const a = xy(from), b = xy(to);
    container.append(element("line", { x1: a.x, y1: a.y, x2: b.x, y2: b.y, "stroke-linecap": "round", ...attributes }));
  }

  function renderBoard() {
    if (!state) return;
    for (const name of ["territories", "lines", "nodes", "legal-markers"]) $(name).replaceChildren();
    for (let player = 0; player < 2; player++) {
      for (const polygon of state.territories[player]) {
        const points = polygon.map(p => { const v = xy(p); return `${v.x},${v.y}`; }).join(" ");
        $("territories").append(element("polygon", { points, fill: colors[player], "fill-opacity": .13, stroke: "none" }));
      }
      for (const segment of state.segments[player]) {
        if (segment.invincible) drawLine($("lines"), segment.from, segment.to, { stroke: colors[player], "stroke-width": 8, "stroke-opacity": .12 });
        drawLine($("lines"), segment.from, segment.to, { stroke: colors[player], "stroke-width": 3.1 });
        if (segment.invincible) {
          const a = xy(segment.from), b = xy(segment.to);
          $("lines").append(element("circle", { cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2, r: 3.3, fill: "#fbf9f1", stroke: colors[player], "stroke-width": 1.7 }));
        }
      }
      for (const point of state.nodes[player]) {
        const p = xy(point);
        $("nodes").append(element("circle", { cx: p.x, cy: p.y, r: 4.4, fill: colors[player], stroke: "#fbf9f1", "stroke-width": 1 }));
      }
    }
    const last = state.history.at(-1);
    if (last && !last.action.pass) {
      for (const point of [last.action.from, last.action.to]) {
        const p = xy(point);
        $("legal-markers").append(element("circle", { cx: p.x, cy: p.y, r: 7.2, fill: "none", stroke: colors[last.player], "stroke-opacity": .35, "stroke-width": 1 }));
      }
    }
    if (selected && canPlay()) {
      const start = xy(selected);
      $("legal-markers").append(element("circle", { cx: start.x, cy: start.y, r: 9, fill: "none", stroke: colors[state.turn], "stroke-width": 2 }));
      for (const action of connectedActions(selected)) {
        const end = xy(destination(action, selected));
        $("legal-markers").append(element("circle", { cx: end.x, cy: end.y, r: 8.7, fill: colors[state.turn], "fill-opacity": .09, stroke: colors[state.turn], "stroke-opacity": .5, "stroke-width": 1.2 }));
        $("legal-markers").append(element("circle", { cx: end.x, cy: end.y, r: 2.1, fill: colors[state.turn], "fill-opacity": .65 }));
      }
    }
    const hint = currentHint();
    if (hint && !hint.pass) {
      drawLine($("legal-markers"), hint.from, hint.to, { stroke: "#688848", "stroke-width": 4, "stroke-dasharray": "5 5", "stroke-opacity": .85 });
    }
    renderPreview();
    renderKeyboard();
  }

  function renderPreview() {
    $("preview").replaceChildren();
    if (!selected || !hovered || !canPlay()) return;
    if (connectedActions(selected).some(action => same(destination(action, selected), hovered)))
      drawLine($("preview"), selected, hovered, { stroke: colors[state.turn], "stroke-width": 2.6, "stroke-dasharray": "5 4", "stroke-opacity": .65 });
  }

  function renderKeyboard() {
    $("keyboard-marker").replaceChildren();
    if (!keyboardVisible) return;
    const p = xy(keyboardPoint);
    $("keyboard-marker").append(element("rect", { x: p.x - 11, y: p.y - 11, width: 22, height: 22, rx: 5, fill: "none", stroke: "#657760", "stroke-width": 1.6 }));
  }

  function renderStatus() {
    if (!state) return;
    let title, description;
    const remaining = `${state.actionsRemaining} placement${state.actionsRemaining === 1 ? "" : "s"} remaining`;
    if (state.finished) {
      const winner = state.scores[0] === state.scores[1] ? null : (state.scores[0] > state.scores[1] ? 0 : 1);
      title = winner === null ? "An even match." : `${names[winner]} wins.`;
      description = `Final score: Blue ${number(state.scores[0])} · Red ${number(state.scores[1])}`;
    } else if (state.thinking) {
      title = selected ? `From ${coordinate(selected)} — choose an endpoint` : state.hintThinking
        ? `Finding a suggestion for ${names[state.turn]}…` : `${names[state.turn]} is planning its turn…`;
      description = `${remaining} · ${timeLabel(state.moveMilliseconds)} ${state.hintThinking ? "per suggestion" : "for this AI turn"}`;
    } else if (state.analysisMode || coachConnection) {
      title = selected ? `From ${coordinate(selected)} — choose an endpoint` : `${names[state.turn]} to move.`;
      description = `${coachConnection ? "Live coach" : "Analysis"} · ${remaining}`;
    } else if (state.turn === state.humanPlayer) {
      title = selected ? `From ${coordinate(selected)} — choose an endpoint` : "Your turn.";
      description = `${names[state.turn]} · ${remaining}`;
    } else { title = `${names[state.turn]} to move.`; description = `AI · ${remaining}`; }
    $("turn-title").textContent = title;
    $("turn-description").textContent = description;
    $("turn-dot").className = `turn-dot ${state.turn === 0 ? "blue" : "red"}`;
    document.querySelector(".turn-info").classList.toggle("thinking", state.thinking);
    $("move-counter").replaceChildren(document.createTextNode(`${state.moveNumber} `));
    const limit = document.createElement("span"); limit.textContent = "/ 120"; $("move-counter").append(limit);
    $("selection-label").textContent = coachConnection ? "Play moves in the original tab." : state.finished ? "Start a new game for a rematch." : selected ? `${connectedActions(selected).length} legal endpoints · click the node again to cancel` : canPlay() ? `Select one of ${names[state.turn]}'s nodes to begin.` : "The next move is taking shape.";
    $("game-status").textContent = state.finished ? "Complete" : state.analysisMode ? "Analysis" : "In progress";
    $("mode-tag").textContent = coachConnection ? "Connected live coach" : state.analysisMode ? "Analyse both colors" : "Play against AI";
    $("settings-heading").textContent = state.analysisMode || coachConnection ? "Your analysis coach" : "Your opponent";
    $("seat-label").textContent = state.analysisMode ? "Your color in Play AI mode" : "Play as";
    renderBudget();
    $("settings-note").textContent = coachConnection
      ? "The board follows the original site. Each suggestion uses this budget for the current position. Play your chosen move in the original tab."
      : state.analysisMode
        ? "Enter moves for either color or load a saved game. Each suggestion uses this budget for the current position. Play suggestion applies one move."
        : "The AI plans its remaining placements together within one turn budget. Hints and live suggestions use this time for each position.";
    if (title !== lastAnnouncement) { $("announcement").textContent = `${title} ${description}`; lastAnnouncement = title; }
  }

  function renderControls() {
    if (!state) return;
    const hint = currentHint();
    $("undo").disabled = coachConnection || pending || !state.canUndo;
    $("hint").disabled = pending || state.thinking || state.finished || (!state.analysisMode && state.turn !== state.humanPlayer);
    $("new-game").disabled = coachConnection || pending;
    $("load").disabled = coachConnection || pending;
    $("seat").disabled = coachConnection || pending || !!state.analysisMode;
    $("game-mode").disabled = coachConnection || pending;
    $("live-coach").disabled = pending;
    $("move-time").disabled = pending;
    $("save").disabled = pending;
    $("pass").hidden = !canPlay() || !state.legalActions.some(action => action.pass);
    $("pass").disabled = pending;
    $("play-suggestion").hidden = !hint;
    $("play-suggestion").disabled = !canPlay() || !hint;
    $("play-suggestion").title = coachConnection ? "Play this move in the original tab." : "Apply the current suggestion to this board.";
    $("hint-label").textContent = hint ? `${names[state.turn]}: ${actionLabel(hint)}` : state.hintThinking ? "Updating the suggestion…" : "";
  }

  function render() {
    if (!state) return;
    renderStatus();
    renderControls();
    const hint = currentHint();
    if (lastRenderedRevision !== state.revision || (hint?.id ?? null) !== lastHintAction || selected) renderBoard();
    lastRenderedRevision = state.revision;
    lastHintAction = hint?.id ?? null;
    for (let player = 0; player < 2; player++) {
      const color = player === 0 ? "blue" : "red";
      $(`${color}-score`).textContent = number(state.scores[player]);
      $(`${color}-area`).textContent = number(state.areas[player]);
      $(`${color}-role`).textContent = state.analysisMode || coachConnection
        ? (coachConnection ? "Original site" : "You control") : player === state.humanPlayer ? "You" : "AI";
      $(`${color}-card`).classList.toggle("active", !state.finished && state.turn === player);
    }
    const currentSearch = state.lastSearch?.positionRevision === state.revision;
    const search = state.lastSearch && (currentSearch || !state.analysisMode && !state.liveCoach && !coachConnection) ? state.lastSearch : null;
    $("search-context").textContent = search ? (currentSearch ? "Current position" : "Last AI turn") : state.hintThinking ? "Updating…" : "Depth search";
    if (search) {
      $("search-depth").textContent = search.completedDepth;
      $("search-nodes").textContent = number(search.nodes);
      $("search-time").textContent = timeLabel(search.elapsedMilliseconds);
      $("search-line").textContent = `${currentSearch ? "Suggested continuation" : "Previous continuation"}: ${search.principalVariation.slice(0, 4).map(actionLabel).join(" → ")}`;
      $("search-line").hidden = search.principalVariation.length === 0;
    } else {
      for (const field of ["search-depth", "search-nodes", "search-time"]) $(field).textContent = "—";
      $("search-line").hidden = true;
    }
    $("history-count").textContent = `${state.history.length} placement${state.history.length === 1 ? "" : "s"}`;
    const list = $("history-list");
    if (state.history.length !== lastHistoryCount || state.revision !== Number(list.dataset.revision)) {
      list.replaceChildren();
      if (state.history.length === 0) {
        const item = document.createElement("li"); item.className = "empty-history"; item.textContent = "Every enclosure starts with a line."; list.append(item);
      }
      for (const move of state.history) {
        const item = document.createElement("li");
        const index = document.createElement("span"); index.className = "history-number"; index.textContent = `${move.number}.`;
        const dot = document.createElement("span"); dot.className = `player-dot ${move.player === 0 ? "blue" : "red"}`; dot.setAttribute("aria-label", names[move.player]);
        const label = document.createElement("span"); label.className = "history-move"; label.textContent = actionLabel(move.action);
        const actor = document.createElement("span"); actor.className = "history-actor";
        actor.textContent = state.analysisMode || coachConnection ? names[move.player].toUpperCase() : move.player === state.humanPlayer ? "YOU" : "AI";
        item.append(index, dot, label, actor); list.append(item);
      }
      if (state.history.length > lastHistoryCount) list.scrollTop = list.scrollHeight;
      lastHistoryCount = state.history.length;
      list.dataset.revision = state.revision;
    }
    if (state.error) showError(state.error);
    window.dispatchEvent(new CustomEvent("enclosure-state", { detail: state }));
  }

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
    const directions = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, 1], ArrowDown: [0, -1] };
    if (directions[event.key]) {
      event.preventDefault(); keyboardVisible = true;
      const [dx, dy] = directions[event.key];
      keyboardPoint = { x: Math.max(0, Math.min(18, keyboardPoint.x + dx)), y: Math.max(0, Math.min(18, keyboardPoint.y + dy)) };
      hovered = keyboardPoint; renderKeyboard(); renderPreview();
      $("announcement").textContent = coordinate(keyboardPoint);
    } else if (event.key === "Enter" || event.key === " ") { event.preventDefault(); keyboardVisible = true; selectPoint(keyboardPoint); renderKeyboard(); }
    else if (event.key === "Escape") { selected = null; hovered = null; renderBoard(); renderStatus(); }
  });
  $("board").addEventListener("blur", () => { keyboardVisible = false; renderKeyboard(); });
  $("new-game").addEventListener("click", () => mutate("new", {
    humanPlayer: Number($("seat").value), moveMilliseconds: Number($("move-time").value),
    analysisMode: $("game-mode").value === "analysis", liveCoach: $("live-coach").checked
  }));
  $("undo").addEventListener("click", () => mutate("undo", { revision: state.revision }));
  $("hint").addEventListener("click", () => mutate("hint", { revision: state.revision }));
  $("play-suggestion").addEventListener("click", () => {
    const hint = currentHint();
    if (hint && canPlay()) mutate("move", { revision: state.revision, action: hint.id });
  });
  $("pass").addEventListener("click", () => mutate("move", { revision: state.revision, action: state.legalActions.find(action => action.pass).id }));
  $("game-mode").addEventListener("change", () => {
    if (!coachConnection) mutate("settings", { moveMilliseconds: Number($("move-time").value), analysisMode: $("game-mode").value === "analysis" });
  });
  $("live-coach").addEventListener("change", () => mutate("settings", { moveMilliseconds: Number($("move-time").value), liveCoach: $("live-coach").checked }));
  $("move-time").addEventListener("input", renderBudget);
  $("move-time").addEventListener("change", () => mutate("settings", { moveMilliseconds: Number($("move-time").value) }));
  $("save").addEventListener("click", async () => {
    try {
      const history = await api("export");
      const url = URL.createObjectURL(new Blob([JSON.stringify(history, null, 2)], { type: "application/json" }));
      const link = document.createElement("a"); link.href = url; link.download = "enclosure-game.json";
      document.body.append(link); link.click(); link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (error) { showError(error.message); }
  });
  $("load").addEventListener("click", () => $("load-file").click());
  $("load-file").addEventListener("change", async event => {
    const file = event.target.files[0]; event.target.value = "";
    if (!file || coachConnection) return;
    if (file.size > 60 * 1024) { showError("Choose an Enclosure history JSON file smaller than 60 KB."); return; }
    try { await mutate("import", { history: await file.text() }); }
    catch (error) { showError(`The file could not be read: ${error.message}`); }
  });
  window.enclosureCoach = {
    state: () => state,
    refresh,
    sync(history) {
      if (!coachConnection) return Promise.reject(new Error("Open the connected coach window to sync an original-site game."));
      const operation = syncQueue.then(async () => {
        await requestQueue;
        if (!state) accept(await api("state"));
        pending = true;
        renderControls();
        try {
          let next;
          try { next = await api("sync", { revision: state.revision, history }); }
          catch (error) {
            if (error.status !== 409) throw error;
            accept(await api("state"));
            next = await api("sync", { revision: state.revision, history });
          }
          accept(next);
          return next;
        } finally {
          pending = false;
          render();
          schedule();
        }
      });
      syncQueue = operation.catch(() => {});
      return operation;
    }
  };
  document.body.classList.toggle("coach-connected", coachConnection);
  $("coach-connection").hidden = !coachConnection;
  buildGrid();
  refresh();
})();
