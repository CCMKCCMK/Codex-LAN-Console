'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const {
  externalActiveFreshnessMs,
  mergeRuntimeStates,
  normalizeThreadRuntime,
  statusLabel
} = require('./thread-status.js');

const now = Date.parse('2026-07-24T06:00:00Z');
const notLoaded = { id: 'thread-1', status: { type: 'notLoaded' } };

test('a fresh external lifecycle record can supplement notLoaded', () => {
  const state = normalizeThreadRuntime({
    thread: notLoaded,
    runtime: {
      phase: 'running', isRunning: true, canControl: false, source: 'rollout',
      observedAt: new Date(now - 60_000).toISOString()
    },
    now
  });
  assert.equal(state.phase, 'running');
  assert.equal(statusLabel(state), '运行中（其他 Codex 客户端）');
});

test('server freshness keeps a long-running task live after recent file activity', () => {
  const state = normalizeThreadRuntime({
    thread: notLoaded,
    runtime: {
      phase: 'running', isRunning: true, canControl: false, source: 'rollout',
      observedAt: new Date(now - 60 * 60 * 1000).toISOString(),
      freshUntil: new Date(now + 29 * 60 * 1000).toISOString()
    },
    now
  });
  assert.equal(state.phase, 'running');
  assert.equal(state.stale, undefined);
});

test('an old unmatched external start is not displayed as currently running', () => {
  const state = normalizeThreadRuntime({
    thread: notLoaded,
    runtime: {
      phase: 'running', isRunning: true, canControl: false, source: 'rollout',
      observedAt: new Date(now - externalActiveFreshnessMs - 1).toISOString()
    },
    now
  });
  assert.equal(state.phase, 'unknown');
  assert.equal(state.isRunning, null);
  assert.equal(state.stale, true);
  assert.equal(statusLabel(state), '状态待确认');
});

test('external running evidence without an update time is not trusted', () => {
  const state = normalizeThreadRuntime({
    thread: notLoaded,
    runtime: { phase: 'running', isRunning: true, source: 'rollout' },
    now
  });
  assert.equal(state.isRunning, null);
  assert.equal(state.staleReason, 'missingTimestamp');
});

test('notLoaded rejects an app-server running snapshot from an older refresh', () => {
  const state = normalizeThreadRuntime({
    thread: notLoaded,
    runtime: { phase: 'running', isRunning: true, canControl: true, source: 'appServer' },
    now
  });
  assert.deepEqual(
    { phase: state.phase, isRunning: state.isRunning, source: state.source },
    { phase: 'unknown', isRunning: null, source: 'history' }
  );
});

test('notLoaded preserves an authoritative terminal turn result', () => {
  const state = normalizeThreadRuntime({
    thread: notLoaded,
    runtime: {
      phase: 'idle', isRunning: false, canControl: true, source: 'history',
      lastOutcome: 'interrupted', observedAt: new Date(now - 60_000).toISOString()
    },
    now
  });
  assert.equal(state.phase, 'idle');
  assert.equal(state.isRunning, false);
  assert.equal(statusLabel(state), '已停止');
});

test('a current pending request is stronger evidence than an old lifecycle record', () => {
  const state = normalizeThreadRuntime({
    thread: notLoaded,
    runtime: {
      phase: 'running', isRunning: true, canControl: true, source: 'rollout',
      observedAt: new Date(now - externalActiveFreshnessMs - 1).toISOString()
    },
    pending: { phase: 'waitingInput', observedAt: new Date(now).toISOString() },
    now
  });
  assert.equal(state.phase, 'waitingInput');
  assert.equal(state.isRunning, true);
  assert.equal(state.source, 'pending');
});

test('process presence alone never changes an invisible task into running', () => {
  const state = normalizeThreadRuntime({
    thread: notLoaded,
    processes: [{ name: 'codex', pid: 1234 }],
    now
  });
  assert.equal(state.phase, 'unknown');
  assert.equal(state.isRunning, null);
});

test('a persisted active thread needs current runtime evidence', () => {
  const state = normalizeThreadRuntime({
    thread: { id: 'thread-1', status: { type: 'active' } },
    now
  });
  assert.equal(state.phase, 'unknown');
  assert.equal(state.isRunning, null);
  assert.equal(state.historicalPhase, 'active');
});

test('old terminal lifecycle records remain useful and do not claim activity', () => {
  const state = normalizeThreadRuntime({
    thread: notLoaded,
    runtime: {
      phase: 'idle', isRunning: false, source: 'rollout', lastOutcome: 'completed',
      observedAt: new Date(now - 24 * 60 * 60 * 1000).toISOString()
    },
    now
  });
  assert.equal(state.isRunning, false);
  assert.equal(statusLabel(state), '已完成');
});

test('an older summary cannot reinsert running after a newer detail cleared it', () => {
  const merged = mergeRuntimeStates(
    {},
    { 'thread-1': { phase: 'running', isRunning: true, observedAt: new Date(now).toISOString() } },
    new Map([['thread-1', 200]]),
    100
  );
  assert.equal(merged['thread-1'], undefined);
});

test('runtime merging keeps the state with the newer observation time', () => {
  const newer = { phase: 'idle', isRunning: false, observedAt: new Date(now).toISOString() };
  const older = { phase: 'running', isRunning: true, observedAt: new Date(now - 60_000).toISOString() };
  const merged = mergeRuntimeStates({ 'thread-1': newer }, { 'thread-1': older }, null, 100);
  assert.equal(merged['thread-1'], newer);
});
