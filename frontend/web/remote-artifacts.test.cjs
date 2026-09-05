'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const {
  cacheKey,
  artifactKind,
  createSingleFlight,
  displayName,
  escapeMarkdownHtmlPreservingFileLinks,
  failurePresentation,
  normalizeReferencePath
} = require('./remote-artifacts.js');

test('remote file labels stay compact for Windows and Unix paths', () => {
  assert.equal(displayName('C:\\work\\output\\report.pdf'), 'report.pdf');
  assert.equal(displayName('/tmp/screens/mobile.png'), 'mobile.png');
  assert.equal(cacheKey('thread-1', 'report.pdf'), 'thread-1\nreport.pdf');
});

test('public CA certificate files become downloadable certificate artifacts without accepting private keys', () => {
  assert.equal(artifactKind({ name: 'codex-console-ca.cer' }), 'certificate');
  assert.equal(artifactKind({ path: 'C:\\certs\\root.crt' }), 'certificate');
  assert.equal(artifactKind({ name: 'root.der' }), 'certificate');
  assert.equal(artifactKind({ name: 'identity.p12' }), 'file');
  assert.equal(artifactKind({ name: 'private.key' }), 'file');
});

test('Markdown URL-style Windows paths normalize without weakening other colon checks', () => {
  assert.equal(normalizeReferencePath('/C:/Users/i/result.png'), 'C:/Users/i/result.png');
  assert.equal(normalizeReferencePath('C:/Users/i/result.png'), 'C:/Users/i/result.png');
  assert.equal(normalizeReferencePath('/tmp/result.png'), '/tmp/result.png');
  assert.equal(normalizeReferencePath('/not-a-drive:/result.png'), '/not-a-drive:/result.png');
});

test('Windows Markdown link destinations survive HTML escaping while raw HTML does not', () => {
  const source = '[APK](</C:/Users/i/My Project/PaperConsole-local-debug.apk>) <script>alert(1)</script>';
  const escaped = escapeMarkdownHtmlPreservingFileLinks(source);
  assert.match(escaped, /\]\(<\/C:\/Users\/i\/My Project\/PaperConsole-local-debug\.apk>\)/);
  assert.doesNotMatch(escaped, /<script>/);
  assert.match(escaped, /&lt;script>/);
});

test('remote file failures explain permission and missing-file cases', () => {
  assert.match(failurePresentation({ status: 403 }, 'result.png').title, /尚未允许读取/);
  assert.match(failurePresentation({ status: 404 }, 'result.png').title, /无法按这条引用定位/);
  assert.match(failurePresentation({ status: 500 }, 'result.png').detail, /不会因此丢失/);
});

test('parallel rendering shares one register request and permits a later retry', async () => {
  const requests = createSingleFlight();
  let calls = 0;
  let release;
  const operation = () => {
    calls += 1;
    return new Promise(resolve => { release = resolve; });
  };
  const first = requests.run('thread\nfile', operation);
  const second = requests.run('thread\nfile', operation);
  assert.equal(first, second);
  await Promise.resolve();
  assert.equal(calls, 1);
  release({ ok: true });
  assert.deepEqual(await first, { ok: true });
  assert.equal(requests.size, 0);
  await requests.run('thread\nfile', async () => { calls += 1; });
  assert.equal(calls, 2);
});

test('a failed register request is not cached and can be retried', async () => {
  const requests = createSingleFlight();
  let calls = 0;
  await assert.rejects(
    requests.run('thread\nmissing', async () => {
      calls += 1;
      throw new Error('temporary failure');
    }),
    /temporary failure/
  );
  assert.equal(requests.size, 0);
  assert.equal(
    await requests.run('thread\nmissing', async () => {
      calls += 1;
      return 'available';
    }),
    'available'
  );
  assert.equal(calls, 2);
});
