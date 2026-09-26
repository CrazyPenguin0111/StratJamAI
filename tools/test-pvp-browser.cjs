// Requires Playwright. Run: node tools/test-pvp-browser.cjs http://127.0.0.1:PORT
// Uses fresh browser contexts and only games created by this script.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const base = process.argv[2] || 'http://127.0.0.1:5081';
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
// Unusual matching settings keep smoke-test queue entries separate from normal games.
const quickSeconds = String(3000 + Math.floor(Math.random() * 600));
const quickIncrement = String(60 + Math.floor(Math.random() * 61));

(async () => {
  const browser = await chromium.launch({ headless: true });
  const errors = [];
  const contexts = [];
  const fresh = async viewport => {
    const context = await browser.newContext({ viewport, acceptDownloads: true });
    contexts.push(context);
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    return { context, page };
  };
  const snapshot = async (context, code) => {
    const response = await context.request.get(`${base}/api/pvp/state${code ? '?code=' + encodeURIComponent(code) : ''}`);
    assert.equal(response.status(), 200, await response.text());
    return response.json();
  };
  const roomVisible = page => page.locator('#room-card').waitFor({ state: 'visible' });
  const active = page => page.waitForFunction(() => document.body.classList.contains('pvp-has-board'));
  const play = async (context, page) => {
    const before = await snapshot(context);
    const action = before.board.legalActions[0];
    assert.ok(action, 'moving player receives legal actions');
    await page.waitForFunction(() => document.getElementById('turn-title').textContent === 'Your turn.');
    if (action.pass) await page.locator('#pass').click();
    else {
      const ownedFrom = before.board.nodes[before.viewerColor].some(point => point.x === action.from.x && point.y === action.from.y);
      const from = ownedFrom ? action.from : action.to;
      const to = ownedFrom ? action.to : action.from;
      await page.locator(`#hit-targets [data-x="${from.x}"][data-y="${from.y}"]`).click();
      await page.locator(`#hit-targets [data-x="${to.x}"][data-y="${to.y}"]`).click();
    }
    await page.waitForFunction(number => document.getElementById('move-counter').textContent.trim().startsWith(number + ' '), before.board.moveNumber + 1);
    return snapshot(context);
  };
  try {
    const a = await fresh({ width: 1320, height: 1000 });
    const b = await fresh({ width: 390, height: 844 });
    await a.page.goto(`${base}/pvp.html`);
    await a.page.locator('#display-name').fill('<b>Alice</b>');
    await a.page.locator('#color-preference').selectOption('blue');
    await a.page.locator('#create-room').click();
    await roomVisible(a.page);
    const code = (await a.page.locator('#room-code').textContent()).trim();
    assert.ok(code);
    await a.page.locator('#copy-link').click();
    assert.ok((await a.page.locator('#share-url').inputValue()).endsWith('code=' + code));

    let automaticJoins = 0;
    b.page.on('request', request => { if (request.url().endsWith('/api/pvp/join') && request.method() === 'POST') automaticJoins++; });
    await b.page.goto(`${base}/pvp.html?code=${code}`);
    assert.equal(await b.page.locator('#join-code').inputValue(), code);
    await wait(200);
    assert.equal(automaticJoins, 0, 'invitation never joins automatically');
    await b.page.locator('#display-name').fill('Bob');
    await b.page.locator('#color-preference').selectOption('blue');
    await b.page.locator('#join-room').click();
    await b.page.locator('#error').waitFor({ state: 'visible' });
    assert.match(await b.page.locator('#error').textContent(), /color/i);
    await b.page.locator('#color-preference').selectOption('red');
    await b.page.locator('#join-room').click();
    await roomVisible(b.page);
    assert.equal(await b.page.locator('#room-edit').isVisible(), false, 'guest cannot edit clocks');
    assert.equal(await b.page.locator('#room-players b').count(), 0, 'names remain plain text');
    await a.page.waitForFunction(() => document.querySelectorAll('#room-players li').length === 2 && document.getElementById('room-players').textContent.includes('Bob'));

    await a.page.locator('#room-color').selectOption('red');
    await a.page.locator('#error').waitFor({ state: 'visible' });
    assert.match(await a.page.locator('#error').textContent(), /color/i);
    await a.page.locator('#room-color').selectOption('random');
    await a.page.waitForFunction(() => document.getElementById('room-color').value === 'random' && !document.getElementById('room-color').disabled);
    await a.page.locator('#ready').click();
    await a.page.waitForFunction(() => document.getElementById('ready').textContent === 'Not ready yet');
    await a.page.locator('#room-initial').fill('90');
    await a.page.locator('#room-increment').fill('5');
    await a.page.locator('#update-settings').click();
    await a.page.waitForFunction(() => document.getElementById('ready').textContent === "I'm ready");
    await b.page.waitForFunction(() => document.getElementById('room-clock').textContent.startsWith('1:30 + 5'));
    await a.page.locator('#ready').click();
    await b.page.waitForFunction(() => document.querySelectorAll('#room-players .pvp-ready-state.ready').length === 1);
    await b.page.locator('#ready').click();
    await Promise.all([active(a.page), active(b.page)]);
    assert.equal((await snapshot(a.context)).viewerColor, 0);
    assert.equal((await snapshot(b.context)).viewerColor, 1);
    assert.equal(await a.page.locator('#save').isVisible(), false);
    assert.equal(await a.page.locator('#review').isVisible(), false);
    const afterBlue = await play(a.context, a.page);
    assert.equal(afterBlue.board.turn, 1);
    assert.ok(afterBlue.remainingMilliseconds[0] > 90000, 'Blue receives the whole-turn increment');
    const redBefore = await snapshot(b.context);
    const redFirst = await play(b.context, b.page);
    assert.equal(redFirst.board.turn, 1);
    assert.equal(redFirst.board.actionsRemaining, 1);
    assert.ok(redFirst.remainingMilliseconds[1] <= redBefore.remainingMilliseconds[1] + 100, 'no increment after first Red placement');
    const redSecond = await play(b.context, b.page);
    assert.equal(redSecond.board.turn, 0);
    assert.ok(redSecond.remainingMilliseconds[1] > redFirst.remainingMilliseconds[1] + 2500, 'increment follows second Red placement');
    const mobile = await b.page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth }));
    assert.equal(mobile.scroll, mobile.width);
    await b.page.screenshot({ path: '/tmp/enclosure-pvp-active-mobile.png', fullPage: true });

    await b.page.reload();
    await active(b.page);
    assert.equal((await snapshot(b.context)).code, code);
    await b.context.setOffline(true);
    await b.page.locator('#connection-notice').waitFor({ state: 'visible' });
    await b.context.setOffline(false);
    await b.page.locator('#connection-notice').waitFor({ state: 'hidden', timeout: 20000 });
    await b.page.locator('#resign').click();
    await b.page.locator('#resign-yes').click();
    await b.page.locator('#review').waitFor({ state: 'visible' });
    await a.page.locator('#review').waitFor({ state: 'visible' });
    assert.match(await a.page.locator('#review').getAttribute('href'), new RegExp('code=' + code));
    const finished = await snapshot(a.context);
    assert.equal(finished.status, 'finished');
    assert.equal(finished.winner, 0);
    assert.equal(finished.resultReason, 'resignation');
    const downloadPromise = a.page.waitForEvent('download');
    await a.page.locator('#save').click();
    const download = await downloadPromise;
    const exported = JSON.parse(await fs.readFile(await download.path(), 'utf8'));
    assert.equal(exported.moves.length, 3);

    await a.page.locator('#leave').click();
    await a.page.locator('#setup-card').waitFor({ state: 'visible' });
    await a.page.locator('#create-room').click();
    await roomVisible(a.page);
    const newerCode = (await a.page.locator('#room-code').textContent()).trim();
    assert.notEqual(newerCode, code);
    const prior = await a.context.newPage();
    prior.on('pageerror', error => errors.push(error.message));
    await prior.goto(`${base}/pvp.html?code=${code}`);
    await prior.locator('#review').waitFor({ state: 'visible' });
    assert.equal((await prior.locator('#room-code').textContent()).trim(), code, 'old participant link reopens its finished room');
    await prior.close();
    await a.page.locator('#leave').click();
    await b.page.locator('#leave').click();
    await Promise.all([a.page.locator('#setup-card').waitFor({ state: 'visible' }), b.page.locator('#setup-card').waitFor({ state: 'visible' })]);

    await a.page.locator('#initial-seconds').fill(quickSeconds);
    await a.page.locator('#increment-seconds').fill(quickIncrement);
    await a.page.locator('#color-preference').selectOption('blue');
    await a.page.locator('#quick-play').click();
    await a.page.waitForFunction(() => document.getElementById('room-status').textContent === 'Finding a match');
    await a.page.locator('#leave').click();
    await a.page.locator('#setup-card').waitFor({ state: 'visible' });
    await a.page.locator('#quick-play').click();
    await a.page.waitForFunction(() => document.getElementById('room-status').textContent === 'Finding a match');
    await b.page.locator('#initial-seconds').fill(quickSeconds);
    await b.page.locator('#increment-seconds').fill(quickIncrement);
    await b.page.locator('#color-preference').selectOption('red');
    await b.page.locator('#quick-play').click();
    await Promise.all([active(a.page), active(b.page)]);
    assert.equal((await snapshot(a.context)).code, (await snapshot(b.context)).code);
    await b.page.locator('#resign').click();
    await b.page.locator('#resign-yes').click();
    await b.page.locator('#review').waitFor({ state: 'visible' });
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ privateLobby: true, explicitInviteJoin: true, colorConflicts: true, hostClockAndReadyReset: true, twoPlacementsAndIncrement: true, mobileNoOverflow: true, reconnect: true, resignationAndExport: true, previousRoomLink: true, quickPlayAndCancellation: true, javascriptErrors: errors }));
  } finally {
    for (const context of contexts) await context.close();
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
