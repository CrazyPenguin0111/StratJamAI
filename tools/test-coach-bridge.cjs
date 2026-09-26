// Run: node --test tools/test-coach-bridge.cjs
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../StratJamAI.Web/wwwroot/coach-bridge.js'), 'utf8');
const token = '0123456789abcdef0123456789abcdef';
const flush = () => new Promise(resolve => setImmediate(resolve));
const position = (move = 1, revision = 3) => ({
  moveNumber: move, turn: 1, actionsRemaining: 2, scores: [0, 0], areas: [0, 0],
  nodes: [[{ x: 0, y: 9 }], [{ x: 18, y: 9 }]], segments: [[], []],
  revision, hintRevision: revision, hint: { id: move, pass: false }, hintThinking: false,
  lastSearch: { positionRevision: revision, completedDepth: 2 }, finished: false, error: null
});
function harness(initial = position()) {
  const handlers = {}, messages = [], calls = [], timers = [], classes = new Set();
  let state = initial, resolver;
  const opener = { postMessage: (data, origin) => messages.push({ data, origin }) };
  const element = {};
  const api = {
    state: () => state,
    sync: async history => {
      calls.push(history);
      const next = resolver ? await resolver(history) : state;
      state = next;
      handlers['enclosure-state']?.({ detail: state });
      return next;
    }
  };
  const context = {
    URLSearchParams, location: { search: '?coach=1', hash: '#token=' + token, pathname: '/' },
    history: { replaceState() {} },
    document: {
      body: { classList: { add: (...names) => names.forEach(n => classes.add(n)), remove: n => classes.delete(n) } },
      createElement: () => element, querySelector: () => ({ after() {} })
    },
    fetch: () => { throw new Error('Bridge must use the same-origin application API.'); },
    setInterval: fn => timers.push(fn),
    window: { opener, enclosureCoach: api, addEventListener: (name, fn) => { handlers[name] = fn; } }
  };
  vm.runInNewContext(source, context);
  return {
    messages, calls, classes, element,
    resolveWith: fn => { resolver = fn; },
    receive(expected = initial, key = 'position', overrides = {}) {
      handlers.message({ source: opener, origin: 'https://meaf.us',
        data: { channel: 'stratjam-coach-v1', token, type: 'position', history: key, key, expected }, ...overrides });
    },
    update(next) { state = next; handlers['enclosure-state']({ detail: next }); },
    tick: () => timers.forEach(fn => fn()),
    suggestions: () => messages.filter(m => m.data.type === 'suggestion').map(m => m.data)
  };
}

test('authenticates opener, origin, channel and token before accepting history', async () => {
  const h = harness();
  h.receive(position(), 'bad-source', { source: {} });
  h.receive(position(), 'bad-origin', { origin: 'https://other.example' });
  h.receive(position(), 'bad-token', { data: { channel: 'stratjam-coach-v1', token: 'wrong', type: 'position' } });
  h.receive(position(), 'bad-channel', { data: { channel: 'wrong', token, type: 'position' } });
  await flush();
  assert.equal(h.calls.length, 0);
  h.receive(); await flush();
  assert.equal(h.calls.length, 1);
  assert.equal(h.suggestions().length, 1);
  assert.ok(h.messages.every(message => message.origin === 'https://meaf.us'));
  assert.ok(h.messages.every(message => ['ready', 'suggestion'].includes(message.data.type)));
});

test('session expiry or server restart replays an unchanged external position', async () => {
  const expected = position(3, 7), h = harness(expected);
  h.receive(expected); await flush();
  h.update(position(0, 1));
  assert.ok(h.classes.has('external-coach-unsynced'));
  assert.equal(h.messages.at(-1).data.type, 'ready');
  h.resolveWith(async () => position(3, 2));
  h.receive(expected); await flush();
  assert.equal(h.calls.length, 2);
  assert.ok(!h.classes.has('external-coach-unsynced'));
  assert.equal(h.suggestions().at(-1).hint.id, 3);
});

test('new positions supersede an in-flight replay and only the latest hint is published', async () => {
  const h = harness(), resolvers = [];
  h.resolveWith(() => new Promise(resolve => resolvers.push(resolve)));
  h.receive(position(1), 'first');
  h.receive(position(2, 4), 'second');
  resolvers[0](position(1)); await flush();
  assert.equal(h.suggestions().length, 0);
  assert.deepEqual(h.calls, ['first', 'second']);
  resolvers[1](position(2, 4)); await flush();
  assert.equal(h.suggestions().length, 1);
  assert.equal(h.suggestions()[0].key, 'second');
  assert.equal(h.suggestions()[0].hint.id, 2);
  h.update({ ...position(2, 5), hintRevision: 4 });
  assert.equal(h.suggestions().at(-1).hint, null);
});

test('replay differences in score, area, geometry, protection or turn suppress suggestions', async () => {
  const mutations = [
    s => { s.scores[0] = 1; }, s => { s.areas[1] = 2; }, s => { s.turn = 0; },
    s => { s.actionsRemaining = 1; }, s => { s.nodes[0][0].x = 1; },
    s => { s.segments[0].push({ from: { x: 0, y: 9 }, to: { x: 0, y: 8 }, invincible: true }); }
  ];
  for (const mutate of mutations) {
    const expected = position(), h = harness();
    mutate(expected); h.receive(expected); await flush();
    assert.ok(h.classes.has('external-coach-unsynced'));
    assert.equal(h.suggestions().length, 1);
    assert.equal(h.suggestions()[0].hint, null);
    assert.match(h.suggestions()[0].error, /differs from the replay/);
  }
});

test('duplicate healthy snapshots do not repeat server replay or suggestion messages', async () => {
  const h = harness();
  h.receive(); await flush();
  h.receive(); h.tick(); await flush();
  assert.equal(h.calls.length, 1);
  assert.equal(h.suggestions().length, 1);
});
