'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const {
  deliveryReceiptPresentation,
  isScrollAtBottom,
  itemRenderKey,
  liveReconnectPlan,
  mergeTurnCollections,
  prependTurnCollections,
  reconcileThreadPageTurns,
  scrollAnchorAdjustment,
  shouldAutoFollow,
  shouldPollThreadDetail
} = require('./thread-stream.js');

test('synthetic summary messages collapse into canonical items without changing the UI key', () => {
  const existing = [{
    id: 'turn-1',
    itemsView: 'summary',
    items: [
      { id: 'item-1', type: 'userMessage', content: [{ type: 'text', text: '同一个问题' }] },
      { id: 'item-3', type: 'agentMessage', phase: 'commentary', text: '正在检查' }
    ]
  }];
  const canonical = [{
    id: 'turn-1',
    itemsView: 'recentFull',
    items: [
      { id: '019f-user', type: 'userMessage', content: [{ type: 'text', text: '同一个问题' }] },
      { id: 'msg-comment', type: 'agentMessage', phase: 'commentary', text: '正在检查并修复' }
    ]
  }];
  const result = reconcileThreadPageTurns(existing, canonical);
  assert.equal(result[0].items.length, 2);
  assert.deepEqual(result[0].items.map(item => item.id), ['019f-user', 'msg-comment']);
  assert.equal(result[0].items[1].text, '正在检查并修复');
  assert.equal(result[0].items[0].__uiKey, itemRenderKey(existing[0].items[0], 0));
});

test('different stable messages with identical text remain distinct', () => {
  const result = reconcileThreadPageTurns([], [{
    id: 'turn-1',
    itemsView: 'recentFull',
    items: [
      { id: 'user-a', type: 'userMessage', content: [{ type: 'text', text: '继续' }] },
      { id: 'user-b', type: 'userMessage', content: [{ type: 'text', text: '继续' }] }
    ]
  }]);
  assert.equal(result[0].items.length, 2);
});

test('external and persisted forms of one tool call merge in place', () => {
  const result = mergeTurnCollections([{
    id: 'turn-1',
    items: [{ id: 'call_demo', type: 'commandExecution', status: 'inProgress' }]
  }], [{
    id: 'turn-1',
    items: [{
      id: 'external-call-call_demo',
      callId: 'call_demo',
      type: 'commandExecution',
      status: 'completed'
    }]
  }]);
  assert.equal(result[0].items.length, 1);
  assert.equal(result[0].items[0].id, 'call_demo');
  assert.equal(result[0].items[0].status, 'completed');
});

test('older live turns are inserted before their canonical anchor instead of appended at the bottom', () => {
  const canonical = ['t3', 't4', 't5', 't6', 't7', 't8'].map(id => ({ id, items: [] }));
  const live = ['t1', 't2', 't3', 't4', 't5', 't6', 't7', 't8'].map(id => ({ id, items: [] }));
  assert.deepEqual(
    mergeTurnCollections(canonical, live).map(turn => turn.id),
    ['t1', 't2', 't3', 't4', 't5', 't6', 't7', 't8']
  );
});

test('loading an overlapping older page keeps each turn once', () => {
  const older = [{ id: 't1', items: [] }, { id: 't2', items: [{ id: 'old', type: 'reasoning' }] }];
  const current = [{ id: 't2', items: [{ id: 'live', type: 'agentMessage', text: 'still here' }] }, { id: 't3', items: [] }];
  const result = prependTurnCollections(older, current);
  assert.deepEqual(result.map(turn => turn.id), ['t1', 't2', 't3']);
  assert.deepEqual(result[1].items.map(item => item.id), ['old', 'live']);
});

test('two live commentary messages survive an incomplete canonical refresh and stay before the final', () => {
  const live = [{
    id: 'turn-1', status: 'inProgress', items: [
      { id: 'comment-1', type: 'agentMessage', phase: 'commentary', text: '第一步' },
      { id: 'tool-1', type: 'commandExecution', status: 'completed' },
      { id: 'comment-2', type: 'agentMessage', phase: 'commentary', text: '第二步' }
    ]
  }];
  const canonical = [{
    id: 'turn-1', status: 'completed', items: [
      { id: 'final-1', type: 'agentMessage', phase: 'final', text: '完成' }
    ]
  }];
  const result = reconcileThreadPageTurns(live, canonical);
  assert.deepEqual(result[0].items.map(item => item.id), ['comment-1', 'tool-1', 'comment-2', 'final-1']);
  const next = mergeTurnCollections(result, [{
    id: 'turn-1', items: [{ id: 'comment-2', type: 'agentMessage', phase: 'commentary', text: '第二步已验证' }]
  }]);
  assert.equal(next[0].items[2].text, '第二步已验证');
});

test('live reconnect forces only three bounded canonical refreshes', () => {
  assert.deepEqual(liveReconnectPlan(1, 0), { forceFullRefresh: true, delayMs: 4000 });
  assert.deepEqual(liveReconnectPlan(2, 2), { forceFullRefresh: true, delayMs: 8000 });
  assert.deepEqual(liveReconnectPlan(8, 3), { forceFullRefresh: false, delayMs: 15000 });
});

test('a pending message request never disables independent detail polling', () => {
  assert.equal(shouldPollThreadDetail({
    visible: true, authenticated: true, page: 'threadDetail', threadId: 'thread-1', sending: true
  }), true);
});

test('repeated live updates preserve the reading anchor while bottom users continue following', () => {
  assert.equal(shouldAutoFollow({ mode: 'position', wasNearBottom: false }), false);
  assert.equal(scrollAnchorAdjustment(240, 240), 0);
  assert.equal(scrollAnchorAdjustment(240, 270), 30);
  assert.equal(shouldAutoFollow({ mode: 'bottom', wasNearBottom: true }), true);
  assert.equal(shouldAutoFollow({ mode: 'bottom', wasNearBottom: false }), false);
  assert.equal(shouldAutoFollow({ mode: 'bottom', wasNearBottom: true, userInteracting: true }), false);
  assert.equal(shouldAutoFollow({ mode: 'position', wasNearBottom: false, forceFollow: true }), true);
});

test('auto-follow requires the viewport to be genuinely at the bottom', () => {
  const metrics = { scrollHeight: 2000, clientHeight: 700 };
  assert.equal(isScrollAtBottom({ ...metrics, scrollTop: 1268 }), true);
  assert.equal(isScrollAtBottom({ ...metrics, scrollTop: 1267 }), false);
  assert.equal(isScrollAtBottom({ ...metrics, scrollTop: 1100 }), false);
});

test('live updates do not auto-follow while the user is touching or scrolling the page', () => {
  assert.equal(shouldAutoFollow({
    mode: 'bottom', wasNearBottom: true, userInteracting: true
  }), false);
  assert.equal(shouldAutoFollow({
    mode: 'bottom', wasNearBottom: true, userInteracting: false
  }), true);
});

test('queued delivery receipts are concise and actionable', () => {
  assert.deepEqual(
    deliveryReceiptPresentation({ queued: true, receipt: { id: 'receipt-1', status: 'queued' } }),
    { id: 'receipt-1', status: 'queued', queued: true, pending: true, message: '已排队，电脑端会在当前轮次结束后继续发送' }
  );
});

test('failed delivery receipts preserve the concrete reason for the mobile details panel', () => {
  const result = deliveryReceiptPresentation({ receipt: {
    id: 'failed-receipt', status: 'failed', message: '未能启动', lastError: 'thread/turns/list rejected the preflight'
  } });
  assert.equal(result.pending, false);
  assert.equal(result.error, 'thread/turns/list rejected the preflight');
  assert.equal(result.id, 'failed-receipt');
});
