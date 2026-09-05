'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const {
  availableEfforts,
  fallbackCatalog,
  normalize,
  normalizeCatalog,
  parsePreference,
  reconcileSelection,
  requestFields
} = require('./model-settings.js');

test('a chosen model and reasoning depth become request fields for either send route', () => {
  const fields = requestFields({ model: 'gpt-5.6-sol', reasoningEffort: 'xhigh' });
  assert.deepEqual(fields, { model: 'gpt-5.6-sol', reasoningEffort: 'xhigh' });
  assert.deepEqual({ text: 'new turn', ...fields }, {
    text: 'new turn', model: 'gpt-5.6-sol', reasoningEffort: 'xhigh'
  });
  assert.deepEqual({ turnId: 'turn-1', text: 'steer', ...fields }, {
    turnId: 'turn-1', text: 'steer', model: 'gpt-5.6-sol', reasoningEffort: 'xhigh'
  });
});

test('following the task default omits both optional fields', () => {
  assert.deepEqual(requestFields({ model: '', reasoningEffort: '' }), {});
  assert.deepEqual(requestFields({ model: '', reasoningEffort: 'ultra' }), {});
});

test('stored preferences are normalized and control characters are rejected', () => {
  assert.deepEqual(parsePreference('{"model":" gpt-5.6-terra ","reasoningEffort":"HIGH"}'), {
    model: 'gpt-5.6-terra', reasoningEffort: 'high'
  });
  assert.deepEqual(normalize({ model: 'future/model', reasoningEffort: 'experimental' }), {
    model: 'future/model', reasoningEffort: 'experimental'
  });
  assert.equal(normalize({ model: 'bad\nmodel', reasoningEffort: 'high' }).model, '');
  assert.deepEqual(parsePreference('{broken'), { model: '', reasoningEffort: '' });
});

test('the authenticated model catalog preserves every advertised model and effort', () => {
  const catalog = normalizeCatalog({ data: [
    {
      id: 'sol-id', model: 'gpt-sol', displayName: 'Sol', isDefault: true,
      defaultReasoningEffort: 'medium',
      supportedReasoningEfforts: [
        { effort: 'low', description: 'Fast' },
        { effort: 'high', description: 'Careful' }
      ]
    },
    {
      id: 'future-id', model: 'future/model', displayName: 'Future',
      supportedReasoningEfforts: [{ effort: 'experimental', description: 'New' }]
    }
  ] });
  assert.deepEqual(catalog.map(item => item.model), ['gpt-sol', 'future/model']);
  assert.deepEqual(availableEfforts(catalog, 'future-id').map(item => item.effort), ['experimental']);
});

test('changing model clears an unsupported remembered effort', () => {
  const catalog = normalizeCatalog({ data: [
    { id: 'a', model: 'a', supportedReasoningEfforts: [{ effort: 'low' }, { effort: 'high' }] },
    { id: 'b', model: 'b', supportedReasoningEfforts: [{ effort: 'medium' }] }
  ] });
  assert.deepEqual(reconcileSelection({ model: 'a', reasoningEffort: 'high' }, catalog), {
    model: 'a', reasoningEffort: 'high'
  });
  assert.deepEqual(reconcileSelection({ model: 'b', reasoningEffort: 'high' }, catalog), {
    model: 'b', reasoningEffort: ''
  });
  assert.deepEqual(reconcileSelection({ model: '', reasoningEffort: 'medium' }, catalog), {
    model: '', reasoningEffort: ''
  });
});

test('fallback catalog is limited to Sol and Terra', () => {
  assert.deepEqual(fallbackCatalog().map(item => item.model), ['gpt-5.6-sol', 'gpt-5.6-terra']);
});
