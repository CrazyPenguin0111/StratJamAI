"use strict";

// This page talks only to the window that opened it on the supported game site.
// All game API requests remain same-origin and use a separate coaching session.
(() => {
  if (new URLSearchParams(location.search).get("coach") !== "1") return;
  const sourceOrigin = "https://meaf.us";
  const token = new URLSearchParams(location.hash.slice(1)).get("token");
  const opener = window.opener;
  if (!opener || !token || !/^[a-f0-9]{32}$/.test(token)) return;
  history.replaceState(null, "", location.pathname + location.search);
  document.body.classList.add("external-coach", "external-coach-unsynced");
  let pending = null, active = null, processing = false, synced = false, lastReply = "";
  const send = data => opener.postMessage({ channel: "stratjam-coach-v1", token, ...data }, sourceOrigin);
  const status = document.createElement("div");
  status.className = "error-banner";
  status.textContent = "Live coach: waiting for the original site's position. Play moves in that tab.";
  document.querySelector(".app-header").after(status);

  function samePosition(state, expected) {
    if (!state || !expected || state.moveNumber !== expected.moveNumber ||
        state.turn !== expected.turn || state.actionsRemaining !== expected.actionsRemaining) return false;
    const point = p => `${p.x},${p.y}`;
    const edge = e => [point(e.from), point(e.to)].sort().join(":") + (e.invincible ? ":1" : ":0");
    for (let p = 0; p < 2; p++) {
      if (Math.abs(state.scores[p] - expected.scores[p]) > 0.00001 ||
          Math.abs(state.areas[p] - expected.areas[p]) > 0.00001 ||
          JSON.stringify(state.nodes[p].map(point).sort()) !== JSON.stringify(expected.nodes[p].map(point).sort()) ||
          JSON.stringify(state.segments[p].map(edge).sort()) !== JSON.stringify(expected.segments[p].map(edge).sort())) return false;
    }
    return true;
  }

  function publish(state) {
    if (!synced || pending || processing || !active || !samePosition(state, active.expected)) return;
    const hint = state.hintRevision === state.revision ? state.hint : null;
    const message = { type: "suggestion", key: active.key, hint, thinking: state.hintThinking,
      finished: state.finished, error: state.error, search: hint ? state.lastSearch : null };
    const fingerprint = JSON.stringify(message);
    if (fingerprint === lastReply) return;
    lastReply = fingerprint;
    send(message);
  }

  async function drain() {
    if (processing || !pending || !window.enclosureCoach) return;
    processing = true;
    const next = pending; pending = null; synced = false;
    document.body.classList.add("external-coach-unsynced");
    try {
      const state = await window.enclosureCoach.sync(next.history);
      if (pending) return;
      if (!samePosition(state, next.expected)) throw new Error("The site's position differs from the replay. Suggestions paused; reconnect after checking the rules/history.");
      active = next; synced = true;
      document.body.classList.remove("external-coach-unsynced");
      status.textContent = `Live coach connected · ${state.moveNumber} / 120 placements. Play moves in the original tab.`;
    } catch (error) {
      status.textContent = error.message;
      send({ type: "suggestion", key: next.key, hint: null, error: error.message });
    } finally {
      processing = false;
      if (pending) void drain();
      else publish(window.enclosureCoach.state());
    }
  }

  window.addEventListener("message", event => {
    const data = event.data;
    if (event.source !== opener || event.origin !== sourceOrigin || data?.channel !== "stratjam-coach-v1" || data.token !== token) return;
    if (data.type !== "position" || typeof data.key !== "string" || data.key.length > 40000 ||
        typeof data.history !== "string" || data.history.length > 30000 || !data.expected) return;
    if (active?.key === data.key && synced && samePosition(window.enclosureCoach?.state(), active.expected)) {
      publish(window.enclosureCoach.state()); return;
    }
    pending = data; synced = false; lastReply = "";
    document.body.classList.add("external-coach-unsynced");
    void drain();
  });
  window.addEventListener("enclosure-state", event => {
    if (synced && active && !samePosition(event.detail, active.expected)) {
      synced = false;
      document.body.classList.add("external-coach-unsynced");
      status.textContent = "Reconnecting the coach to the original position…";
      send({ type: "ready" });
    }
    publish(event.detail);
  });
  setInterval(() => { if (!synced) send({ type: "ready" }); void drain(); }, 1000);
  send({ type: "ready" });
})();
