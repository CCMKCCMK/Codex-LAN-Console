'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const read = file => fs.readFileSync(path.join(__dirname, file), 'utf8');
const source = read('app.js');
const html = read('index.html');
function fn(name) {
  const value = source.match(new RegExp('^(?:async )?function ' + name + '\\([\\s\\S]*?^}', 'm'));
  assert.ok(value, name + ' is present');
  return value[0];
}
function navigation(url) {
  const context = vm.createContext({ URL, URLSearchParams, location: new URL(url), history: {
    state: null, replaceState(s, _, url) { this.state = s; this.url = url; },
    pushState(s, _, url) { this.state = s; this.url = url; }
  }, renderPage(page) { context.rendered = page; }, $() { return {}; }, empty: text => text });
  vm.runInContext(`const navigationMarker = 'codexLanConsole';
    ${source.match(/^const rootPage = .*$/m)[0]}
    ${source.match(/^const primaryPages = .*$/m)[0]}
    let navigationDepth = 0;
    ${['normalizedNavigationState', 'navigationState', 'navigationUrl', 'commitNavigation', 'showPage', 'initializeNavigation'].map(fn).join('\n')}`, context);
  vm.runInContext('initializeNavigation()', context);
  return context;
}

test('the main tab order is tasks, commute, remote control, settings on both pages', () => {
  const labels = input => [...input.match(/<nav[\s\S]*?<\/nav>/)[0].matchAll(/<span>(.*?)<\/span>/g)].map(x => x[1]);
  assert.deepEqual(labels(html), ['任务', '通勤', '远程控制', '设置']);
  assert.deepEqual(labels(read('commute/index.html')), labels(html));
  assert.match(html, /<a href="\/commute\/" data-page="commute">/);
  assert.doesNotMatch(html, /id="(?:overview|projects)"/);
});

test('new visits open tasks; commute return links open the requested tab', () => {
  for (const [query, page] of [['', 'threads'], ['?page=remote', 'remote'], ['?page=settings', 'settings'], ['?page=invalid', 'threads']]) {
    const ctx = navigation('http://100.64.0.10:8787/' + query);
    assert.equal(ctx.rendered, page);
    assert.equal(ctx.history.state.page, page);
    assert.equal(ctx.history.url, '/?page=' + page);
  }
});

test('navigation preserves the selected tab across reloads and browser back state', () => {
  const ctx = navigation('http://100.64.0.10:8787/');
  vm.runInContext("showPage('settings'); showPage('processes');", ctx);
  assert.equal(ctx.history.state.page, 'processes');
  assert.equal(ctx.history.url, '/?page=processes');
  assert.equal(ctx.history.state.depth, 2);
  assert.equal(navigation('http://100.64.0.10:8787' + ctx.history.url).rendered, 'processes');
});

test('returning from a commute notification retains the requested real task ID', () => {
  const ctx = navigation('http://100.64.0.10:8787/?page=threadDetail&threadId=some-real-thread');
  assert.equal(ctx.history.state.threadId, 'some-real-thread');
  assert.equal(ctx.rendered, 'threadDetail');
  assert.match(read('commute/commute.js'), /threadId='\+encodeURIComponent\(id\)/);
});

test('project selection belongs to new task creation, and remote form is a page', () => {
  const dialog = html.match(/<dialog id="newTaskDialog"[\s\S]*?<\/dialog>/)[0];
  assert.match(dialog, /id="projectList"/);
  assert.match(dialog, /项目路径（可选）/);
  assert.match(html.match(/<section id="remote"[\s\S]*?<\/section>/)[0], /id="remoteWorkForm"/);
  assert.doesNotMatch(source, /remoteWorkPanel|closeRemoteWorkPanel|manageAutoApprove|recentApprovals/);
});

test('commute cannot silently use cached Console HTML on network failure', () => {
  const listener = read('sw.js').slice(read('sw.js').indexOf("self.addEventListener('fetch'"));
  let fetchListener;
  vm.runInNewContext(listener, { URL, self: {
    location: { origin: 'http://100.64.0.10:8787' },
    addEventListener(_, callback) { fetchListener = callback; }
  } });
  let intercepted = false;
  fetchListener({ request: { method: 'GET', url: 'http://100.64.0.10:8787/commute/' }, respondWith() { intercepted = true; } });
  assert.equal(intercepted, false);
});

test('creating a normal task allows no project and suppresses double-submit', async () => {
  let calls = 0, opened = '', resolvePost;
  const elements = {
    '#newTaskDialog': { open: true, close() { this.open = false; }, showModal() { this.open = true; } },
    '#createNewTask': {}, '#newTaskError': {}
  };
  const ctx = vm.createContext({
    $: id => elements[id], confirmExecutionSettings: async () => true,
    executionSettings: () => ({ approvalPolicy: 'never' }),
    api: async (route, request) => { calls++; assert.equal(route, '/threads'); assert.equal(request.body.cwd, null); await new Promise(r => { resolvePost = r; }); return { thread: { id: 'new-task' } }; },
    openTask: async id => { opened = id; }, toast() {}, load() {}
  });
  vm.runInContext('let newTaskBusy = false;\n' + fn('startThread'), ctx);
  const first = vm.runInContext('startThread(null)', ctx);
  const second = vm.runInContext('startThread(null)', ctx);
  await new Promise(r => setImmediate(r));
  assert.equal(calls, 1);
  resolvePost(); await Promise.all([first, second]);
  assert.equal(opened, 'new-task');
  assert.equal(elements['#createNewTask'].disabled, false);
});

test('static event bindings reference existing HTML IDs', () => {
  const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(x => x[1]));
  assert.equal(ids.size, [...html.matchAll(/\bid="([^"]+)"/g)].length, 'no duplicate IDs');
  for (const match of source.matchAll(/\$\('#([^']+)'\)\.(?:onclick|onsubmit|addEventListener)/g)) {
    assert.ok(ids.has(match[1]), 'missing event target ' + match[1]);
  }
});

test('task composer stays above the persistent bottom navigation', () => {
  const css = read('styles.css');
  assert.match(css, /\.detail-open #composer\s*\{\s*bottom: var\(--nav-height\)/);
  assert.doesNotMatch(css, /body\.detail-open nav\s*\{\s*display: none/);
});

test('Console and Commute share their theme, header and navigation styling', () => {
  const commute = read('commute/index.html');
  for (const page of [html, commute]) {
    assert.match(page, /href="\/console-theme.css\?v=2"/);
    assert.match(page, /<header class="console-header">/);
    assert.match(page, /<nav class="[^"]*console-nav[^"]*"/);
    assert.match(page, /<meta name="theme-color" content="#0c0f0e"/);
  }
  assert.doesNotMatch(commute, /class="rail"|class="brand"|TRITON DAILY/);
  const tokens = read('console-theme.css');
  assert.match(tokens, /--bg: #0c0f0e/);
  assert.match(tokens, /--green: #7bf1bd/);
  assert.doesNotMatch(read('commute/commute.css'), /color-scheme:light|#1648d5|#123bb8|background:white/);
  const defined = new Set([...tokens.matchAll(/(--[\w-]+)\s*:/g)].map(x => x[1]));
  for (const variable of read('commute/commute.css').matchAll(/var\((--[\w-]+)\)/g)) {
    assert.ok(defined.has(variable[1]), 'undefined shared token ' + variable[1]);
  }
  const manifest = JSON.parse(read('commute/manifest.webmanifest'));
  assert.equal(manifest.theme_color, '#0c0f0e');
  assert.equal(manifest.background_color, '#0c0f0e');
});
