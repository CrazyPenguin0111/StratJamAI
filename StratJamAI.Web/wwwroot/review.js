"use strict";
(() => {
  const $ = id => document.getElementById(id), ns = "http://www.w3.org/2000/svg";
  const params = new URLSearchParams(location.search);
  const code = (params.get("code") || params.get("game") || "").trim().toUpperCase();
  const colors = ["#2865bf", "#c65347"], names = ["Blue", "Red"];
  let state, positions = [], selected = 0, busy = false, timer, queue = Promise.resolve(), renderedRows = 0;
  const number = value => new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(value);
  const estimate = value => `${value > 0 ? "+" : ""}${value.toFixed(3)}`;
  const label = action => action.pass ? "Pass" : `${coordinate(action.from)} – ${coordinate(action.to)}`;
  const coordinate = point => `${String.fromCharCode(65 + point.x)}${point.y + 1}`;
  const xy = point => ({ x: 52 + point.x * 32, y: 52 + (18 - point.y) * 32 });
  function svg(name, attributes) { const node = document.createElementNS(ns, name); for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value); return node; }
  function line(parent, from, to, attributes) { const a = xy(from), b = xy(to); parent.append(svg("line", { x1: a.x, y1: a.y, x2: b.x, y2: b.y, "stroke-linecap": "round", ...attributes })); }
  function error(message) { $("review-error").textContent = message || ""; $("review-error").hidden = !message; }
  function request(suffix = "", body) {
    const work = queue.then(async () => {
      const response = await fetch(`/api/pvp/${encodeURIComponent(code)}/review${suffix}`, body === undefined
        ? { cache: "no-store" } : { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      let value; try { value = await response.json(); } catch { /* A proxy can return HTML. */ }
      if (!response.ok) { const problem = new Error(value?.error || "The review could not be loaded."); problem.status = response.status; throw problem; }
      if (!value || !Array.isArray(value.moves)) throw new Error("The server returned an invalid review.");
      return value;
    });
    queue = work.catch(() => {}); return work;
  }
  function accept(next) {
    state = next;
    if (next.positions) positions = next.positions;
    selected = Math.min(selected, Math.max(0, positions.length - 1));
    error(next.error);
    render();
    clearTimeout(timer);
    if (state.status === "running" && !busy) timer = setTimeout(refresh, 700);
  }
  async function refresh() {
    if (busy) return;
    try { accept(await request(positions.length ? "" : "?positions=true")); }
    catch (problem) {
      error(problem.message);
      if (!problem.status || problem.status >= 500 || problem.status === 429) timer = setTimeout(refresh, 2500);
    }
  }
  async function mutate(suffix, body) {
    if (busy) return;
    busy = true; clearTimeout(timer); render();
    try { accept(await request(suffix, body)); }
    catch (problem) { error(problem.message); }
    finally { busy = false; render(); if (state?.status === "running") timer = setTimeout(refresh, 700); }
  }
  function setPosition(ply) { selected = Math.max(0, Math.min(positions.length - 1, ply)); renderPosition(); highlightRow(); }
  function renderPosition() {
    if (!positions.length) return;
    const position = positions[selected], row = state.moves.find(move => move.number === selected + 1);
    $("position-title").textContent = selected === positions.length - 1 ? "Final recorded position" : selected === 0 ? "Initial position" : `Before move ${selected + 1} · ${names[position.turn]} to play`;
    $("position-number").textContent = `${selected} / ${positions.length - 1}`;
    $("position-scores").textContent = `Scores: Blue ${number(position.scores[0])} · Red ${number(position.scores[1])}  |  Area: ${number(position.areas[0])} / ${number(position.areas[1])}`;
    $("position-slider").max = positions.length - 1; $("position-slider").value = selected;
    $("first-position").disabled = $("previous-position").disabled = selected === 0;
    $("next-position").disabled = $("last-position").disabled = selected === positions.length - 1;
    for (const id of ["review-territories", "review-lines", "review-nodes", "review-overlay"]) $(id).replaceChildren();
    for (let player = 0; player < 2; player++) {
      for (const polygon of position.territories[player]) $("review-territories").append(svg("polygon", { points: polygon.map(p => { const q = xy(p); return `${q.x},${q.y}`; }).join(" "), fill: colors[player], "fill-opacity": .13 }));
      for (const edge of position.segments[player]) {
        line($("review-lines"), edge.from, edge.to, { stroke: colors[player], "stroke-width": 3.1 });
        if (edge.invincible) { const a = xy(edge.from), b = xy(edge.to); $("review-lines").append(svg("circle", { cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2, r: 3.4, fill: "#fbf9f1", stroke: colors[player], "stroke-width": 1.6 })); }
      }
      for (const point of position.nodes[player]) { const p = xy(point); $("review-nodes").append(svg("circle", { cx: p.x, cy: p.y, r: 4.4, fill: colors[player], stroke: "#fbf9f1" })); }
    }
    if (row) {
      if (!row.played.pass) line($("review-overlay"), row.played.from, row.played.to, { stroke: "#ad7d43", "stroke-width": 6, "stroke-dasharray": "11 4", opacity: .8 });
      if ($("show-suggestion").checked && !row.suggested.pass) line($("review-overlay"), row.suggested.from, row.suggested.to, { stroke: "#43844f", "stroke-width": 4, "stroke-dasharray": "5 5" });
      $("selected-title").textContent = `Move ${row.number} · ${names[row.player]}`;
      $("selected-moves").textContent = `Played ${label(row.played)} · Suggested ${label(row.suggested)}`;
      $("selected-evaluation").textContent = `Static estimate: before ${estimate(row.estimateBefore)}, after played ${estimate(row.estimateAfterPlayed)}, after suggested ${estimate(row.estimateAfterSuggested)}. Alternative Δ ${estimate(row.alternativeDifference)}.`;
      $("selected-search").textContent = `Depth ${row.completedDepth} · ${number(row.nodes)} nodes · ${Math.round(row.elapsedMilliseconds)} ms. Suggested continuation: ${row.principalVariation.slice(0, 4).map(label).join(" → ")}`;
    } else {
      $("selected-title").textContent = selected === positions.length - 1 ? "End of the recorded match" : `Move ${selected + 1}`;
      $("selected-moves").textContent = selected === positions.length - 1 ? "Use Previous to inspect the final placement." : state.status === "running" ? "This position is waiting for analysis." : "Start or resume the review to analyze this position.";
      $("selected-evaluation").textContent = $("selected-search").textContent = "";
    }
  }
  function highlightRow() { for (const row of $("review-move-list").children) row.classList.toggle("selected", Number(row.dataset.ply) === selected); }
  function render() {
    if (!state) return;
    const running = state.status === "running";
    const labels = { idle: "Ready to review", running: "Reviewing the match…", completed: "Review complete", cancelled: "Review cancelled", error: "Review stopped" };
    $("review-status").textContent = labels[state.status] || state.status;
    $("review-progress-label").textContent = `${state.completedMoves} of ${state.totalMoves} placements reviewed · ${state.budgetMilliseconds} ms per placement`;
    $("review-progress").max = Math.max(1, state.totalMoves); $("review-progress").value = state.completedMoves;
    $("start-review").disabled = busy || running || state.status === "completed";
    $("start-review").textContent = state.status === "cancelled" || state.status === "error" ? "Restart review" : "Review with AI";
    $("cancel-review").hidden = !running; $("cancel-review").disabled = busy;
    $("review-budget").disabled = busy || running || state.status === "completed";
    if (running || state.status === "completed") $("review-budget").value = state.budgetMilliseconds;
    const final = positions.at(-1);
    for (const player of state.players) {
      const box = $("review-player-" + player.player); box.replaceChildren();
      const heading = document.createElement("strong"); heading.textContent = `${names[player.player]}${final ? ` · final score ${number(final.scores[player.player])}` : ""}`;
      const count = document.createElement("p"); count.textContent = `${player.reviewedMoves} placements reviewed · ${player.matchingSuggestions} matched suggestions`;
      const value = document.createElement("p"); value.textContent = `Average static change ${estimate(player.averagePlayedChange)} · Alternative Δ ${estimate(player.averageAlternativeDifference)}`;
      box.append(heading, count, value);
    }
    if (state.moves.length < renderedRows) { $("review-move-list").replaceChildren(); renderedRows = 0; }
    for (const move of state.moves.slice(renderedRows)) {
      const row = document.createElement("tr"); row.dataset.ply = move.number - 1;
      const first = document.createElement("td"), select = document.createElement("button"); select.textContent = move.number; select.setAttribute("aria-label", `Inspect move ${move.number}`); first.append(select); row.append(first);
      for (const text of [names[move.player], label(move.played), label(move.suggested), estimate(move.alternativeDifference), move.completedDepth]) { const cell = document.createElement("td"); cell.textContent = text; row.append(cell); }
      row.addEventListener("click", () => setPosition(move.number - 1)); $("review-move-list").append(row);
    }
    renderedRows = state.moves.length;
    $("review-row-count").textContent = `${renderedRows} / ${state.totalMoves} placements`;
    $("review-empty").hidden = renderedRows > 0;
    renderPosition(); highlightRow();
  }
  for (let i = 0; i < 19; i++) {
    const p = 52 + i * 32;
    $("review-grid").append(svg("line", { x1: 52, x2: 628, y1: p, y2: p, class: "grid-line" }), svg("line", { y1: 52, y2: 628, x1: p, x2: p, class: "grid-line" }));
    const column = svg("text", { x: p, y: 652, class: "coordinate" }); column.textContent = String.fromCharCode(65 + i);
    const rank = svg("text", { x: 27, y: p, class: "coordinate" }); rank.textContent = 19 - i;
    $("review-grid").append(column, rank);
  }
  $("first-position").addEventListener("click", () => setPosition(0));
  $("previous-position").addEventListener("click", () => setPosition(selected - 1));
  $("next-position").addEventListener("click", () => setPosition(selected + 1));
  $("last-position").addEventListener("click", () => setPosition(positions.length - 1));
  $("position-slider").addEventListener("input", event => setPosition(Number(event.target.value)));
  $("show-suggestion").addEventListener("change", renderPosition);
  $("start-review").addEventListener("click", () => mutate("", { budgetMilliseconds: Number($("review-budget").value) }));
  $("cancel-review").addEventListener("click", () => mutate("/cancel", {}));
  window.addEventListener("pagehide", () => clearTimeout(timer));
  if (!/^[A-Z0-9]{6}$/.test(code)) { $("review-status").textContent = "Choose a completed match"; error("Open Review with AI from your completed player-versus-player match."); return; }
  $("back-to-game").href = `/pvp.html?code=${encodeURIComponent(code)}`;
  refresh();
})();
