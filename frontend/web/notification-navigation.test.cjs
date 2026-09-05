'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRouter } = require('./notification-navigation.js');
function fixture(storage = new Map()) {
  const f = { time: 100000, page: 'commute', opens: [], offers: 0, cancels: 0 };
  f.router = createRouter({ now: () => f.time,
    storage: { getItem: k => storage.get(k), setItem: (k,v) => storage.set(k,v) },
    current: () => f.page, open: id => { f.opens.push(id); f.page = id; },
    offer: accept => { f.offers++; f.accept = accept; },
    clearOffer: () => {}, cancelNative: () => f.cancels++ });
  return f;
}
test('old Android page-finished/resume replays never navigate away from commute', () => {
  const f = fixture();
  for (let i = 0; i < 50; i++) f.router.legacy('old-task');
  assert.deepEqual(f.opens, []); assert.equal(f.offers, 1);
  f.accept();
  assert.deepEqual(f.opens, ['old-task']); assert.equal(f.cancels, 1);
});
test('a manual tab choice supersedes an already queued notification, including across page loads', () => {
  const storage = new Map(), first = fixture(storage);
  first.router.manual();
  const second = fixture(storage);
  assert.equal(second.router.receive('old-task', 'old-request', 99999), true);
  assert.deepEqual(second.opens, []);
  second.time = 100100;
  assert.equal(second.router.receive('new-task', 'new-request', 100100), true);
  assert.deepEqual(second.opens, ['new-task']);
});
test('navigation acknowledgement is independent of task-data retrieval', () => {
  const f = fixture();
  assert.equal(f.router.receive('task-with-offline-data', 'request', f.time), true);
  assert.equal(f.page, 'task-with-offline-data');
  f.router.receive('task-with-offline-data', 'request', f.time);
  assert.equal(f.opens.length, 1);
});
test('expired requests are consumed without navigation; commute notifications remain valid', () => {
  const f = fixture();
  assert.equal(f.router.receive('expired-task', 'expired', 1000), true);
  f.page = 'some-task';
  assert.equal(f.router.receive('commute', 'commute-request', f.time), true);
  assert.deepEqual(f.opens, ['commute']);
});
test('unready/throwing route handlers can retry rather than losing the notification', () => {
  let ready = false, calls = 0;
  const router = createRouter({ now: () => 1000, current: () => null,
    open: () => { calls++; if (!ready) throw new Error('not ready'); },
    offer() {}, clearOffer() {}, cancelNative() {} });
  assert.throws(() => router.receive('task', 'request', 1000));
  ready = true;
  assert.equal(router.receive('task', 'request', 1000), true);
  assert.equal(calls, 2);
});
test('both pages load the router and expose safe legacy hooks, independent of UI navigation', () => {
  const read = file => fs.readFileSync(path.join(__dirname, file), 'utf8');
  for (const file of ['index.html', 'commute/index.html']) assert.match(read(file), /notification-navigation\.js\?v=1/);
  for (const file of ['app.js', 'commute/commute.js']) assert.match(read(file), /window\.openThread\s*=\s*async id\s*=>\s*window\.ConsoleNotificationNavigation\.legacy\(id\)/);
  assert.doesNotMatch(read('app.js'), /onclick="openThread\(/);
  const java = read('../android/app/src/main/java/local/codex/lanconsole/MainActivity.java');
  const route = java.slice(java.indexOf('private void openPendingNotificationThread()'), java.indexOf('private void acknowledgeNotificationRoute('));
  assert.match(route, /CodexConsoleReceiveNotification/);
  assert.doesNotMatch(route, /refreshCurrentThread/);
  assert.match(java, /request\.isForMainFrame\(\) && request\.hasGesture\(\)/);
  assert.match(java, /public void cancelPendingThreadOpen\(\)/);
});
