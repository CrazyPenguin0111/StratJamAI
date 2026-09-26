// Run: node --test tools/test-live-coach.cjs
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const adapter = require('../StratJamAI.Web/wwwroot/enclosure-coach.user.js');
const reference = JSON.parse(fs.readFileSync(path.join(__dirname, '../StratJamAI.Tests/Fixtures/EnclosureReference.json')));
const point = i => [Math.floor(i / 19), i % 19];
const segments = [];
for (let a = 0; a < 361; a++) for (let b = a + 1; b < 361; b++) {
  const from = point(a), to = point(b);
  if (Math.abs(from[0] - to[0]) <= 3 && Math.abs(from[1] - to[1]) <= 3) segments.push({ from, to });
}
const pair = callback => Object.fromEntries(['blue', 'red'].map((c, i) => [c, callback(i)]));
const board = f => ({ moveNumber: f.moveNumber, turn: ['blue', 'red'][f.turn], actionsRemaining: f.actionsRemaining,
  scores: pair(i => f.scores[i]), areas: pair(i => f.areas[i]), nodes: pair(i => f.nodes[i].map(point)),
  segments: pair(i => f.segments[i].map(e => ({ ...segments[e.id], invincible: e.protected }))) });

test('practice adapter reconstructs every move, capture and score across three complete official games', () => {
  for (const trace of reference.traces) {
    const states = trace.frames.map(board);
    for (const state of states) assert.ok(adapter.isBoard(state));
    const moves = adapter.practiceMoves(states.at(-1), states.slice(0, -1));
    assert.equal(moves.length, trace.frames.length - 1);
    for (let i = 0; i < moves.length; i++) {
      const action = trace.frames[i].action;
      assert.deepEqual(moves[i], action === segments.length ? { pass: true } : segments[action]);
      const output = adapter.summary(states[i]);
      assert.deepEqual(output.scores, trace.frames[i].scores);
      assert.deepEqual(output.areas, trace.frames[i].areas);
    }
    assert.equal(adapter.consumed(moves), 120);
    // Undo, then redo, uses the site's current history rather than retained future moves.
    assert.deepEqual(adapter.practiceMoves(states[50], states.slice(0, 50)), moves.slice(0, 50));
    assert.deepEqual(adapter.practiceMoves(states[51], states.slice(0, 51)), moves.slice(0, 51));
    assert.deepEqual(adapter.practiceMoves(states[0], []), []);
  }
});

test('adapter refuses gaps and malformed histories rather than guessing', () => {
  const states = reference.traces[0].frames.map(board);
  assert.throws(() => adapter.moveBetween(states[0], states[2]), /missed/);
  assert.throws(() => adapter.practiceMoves(states[5], [states[4]]), /incomplete/);
  assert.throws(() => adapter.cleanMove({ from: [0, 9], to: [99, 0] }), /incomplete/);
  assert.equal(adapter.isBoard({ ...states[0], turn: 'green' }), false);
});

test('forwarded summary contains only game data, even if the source includes account fields', () => {
  const source = { ...board(reference.traces[0].frames[0]), username: 'private', passH1: 'private', chat: ['private'] };
  const serialized = JSON.stringify(adapter.summary(source));
  assert.ok(!serialized.includes('private'));
  assert.deepEqual(adapter.cleanMove({ ...segments[0], username: 'private', clocksAfter: {} }), segments[0]);
});

test('forced passes consume the remainder of the current turn', () => {
  assert.equal(adapter.consumed([{ pass: true }]), 1);
  assert.equal(adapter.consumed([{ pass: true }, { pass: true }]), 3);
  assert.equal(adapter.consumed([{ from: [0, 9], to: [0, 6] }, { pass: true }, { pass: true }]), 5);
});
