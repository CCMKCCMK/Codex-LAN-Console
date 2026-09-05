'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { normalize, presentation } = require('./administrator-mode.js');

test('active mode is only trusted when Windows token detection succeeded', () => {
  assert.deepEqual(normalize({ detected: false, active: true }), {
    detected: false,
    active: false,
    scope: 'bridgeOwnedTasksOnly'
  });
  assert.equal(normalize({ detected: true, active: true }).active, true);
});

test('active copy states the inheritance boundary instead of promising global elevation', () => {
  const active = presentation({ detected: true, active: true, scope: 'bridgeOwnedTasksOnly' });
  assert.equal(active.badge, '已启用');
  assert.match(active.detail, /Bridge/);
  assert.match(active.detail, /新建或手机发起的新轮次/);
  assert.match(active.detail, /其他 Codex 不继承/);
  assert.match(active.detail, /本机或 Tailscale/);
  assert.match(active.detail, /重新配对/);
});

test('inactive and unknown states warn that Windows confirmation may remain', () => {
  assert.equal(presentation({ detected: true, active: false }).badge, '未启用');
  assert.match(presentation({ detected: true, active: false }).detail, /电脑确认/);
  assert.match(presentation({ detected: true, active: false }).detail, /本机或 Tailscale/);
  assert.equal(presentation(null).badge, '状态未知');
});
