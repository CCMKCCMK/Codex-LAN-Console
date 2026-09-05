'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const {
  createRequestDeadline,
  detailFromPayload,
  messageFromPayload,
  retryableThreadError,
  requestTimeout,
  retryDelay
} = require('./thread-resilience.js');

test('parsed JSON errors never fall back to their raw JSON response', () => {
  const raw = JSON.stringify({ error: 'The bridge could not complete this request.', detail: null });
  assert.equal(detailFromPayload(JSON.parse(raw), raw), '');
  assert.equal(messageFromPayload(JSON.parse(raw), 'fallback'), 'The bridge could not complete this request.');
});

test('every request has a bounded deadline and a hung mutation is aborted', async () => {
  assert.equal(requestTimeout('GET', '/threads/1', null), 20000);
  assert.equal(requestTimeout('GET', '/threads/1/live?after=0', null), 32000);
  assert.equal(requestTimeout('POST', '/threads/1/messages', null), 45000);
  const deadline = createRequestDeadline(null, 10);
  await new Promise(resolve => deadline.signal.addEventListener('abort', resolve, { once: true }));
  assert.equal(deadline.timedOut(), true);
  deadline.dispose();
});

test('nested JSON detail is reduced to a useful message instead of displayed as a blob', () => {
  const nested = JSON.stringify({ error: 'Codex detail is temporarily unavailable.', requestId: 'request-1' });
  assert.equal(detailFromPayload({ detail: nested }, ''), 'Codex detail is temporarily unavailable.');
});

test('plain text error bodies remain available when JSON parsing failed', () => {
  assert.equal(detailFromPayload(null, 'gateway closed the connection'), 'gateway closed the connection');
});

test('task-detail retry uses bounded exponential backoff', () => {
  assert.deepEqual([1, 2, 3, 4, 5, 20].map(retryDelay), [4000, 8000, 15000, 30000, 60000, 60000]);
  assert.equal(retryableThreadError({ status: 500 }), true);
  assert.equal(retryableThreadError({ status: 0 }), true);
  assert.equal(retryableThreadError({ status: 404 }), false);
});
