(function exposeModelSettings(root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (root) root.CodexModelSettings = api;
}(typeof globalThis === 'object' ? globalThis : this, function createModelSettings() {
  'use strict';

  const FALLBACK_CATALOG = Object.freeze([
    Object.freeze({
      id: 'gpt-5.6-sol', model: 'gpt-5.6-sol', displayName: '5.6 Sol', description: '',
      defaultReasoningEffort: 'low', supportedReasoningEfforts: Object.freeze([
        Object.freeze({ effort: 'low', description: '' }),
        Object.freeze({ effort: 'medium', description: '' }),
        Object.freeze({ effort: 'high', description: '' }),
        Object.freeze({ effort: 'xhigh', description: '' }),
        Object.freeze({ effort: 'max', description: '' }),
        Object.freeze({ effort: 'ultra', description: '' })
      ]), isDefault: false
    }),
    Object.freeze({
      id: 'gpt-5.6-terra', model: 'gpt-5.6-terra', displayName: '5.6 Terra', description: '',
      defaultReasoningEffort: 'medium', supportedReasoningEfforts: Object.freeze([
        Object.freeze({ effort: 'low', description: '' }),
        Object.freeze({ effort: 'medium', description: '' }),
        Object.freeze({ effort: 'high', description: '' }),
        Object.freeze({ effort: 'xhigh', description: '' }),
        Object.freeze({ effort: 'max', description: '' }),
        Object.freeze({ effort: 'ultra', description: '' })
      ]), isDefault: false
    })
  ]);

  function cleanProtocolValue(value, maximumLength) {
    const result = typeof value === 'string' ? value.trim() : '';
    return result && result.length <= maximumLength && !/[\u0000-\u001f\u007f]/.test(result) ? result : '';
  }

  function cleanModel(value) {
    return cleanProtocolValue(value, 200);
  }

  function cleanEffort(value) {
    return cleanProtocolValue(value, 100).toLowerCase();
  }

  function normalize(value = {}) {
    const model = cleanModel(value?.model);
    return {
      model,
      reasoningEffort: model ? cleanEffort(value?.reasoningEffort) : ''
    };
  }

  function normalizeCatalog(raw) {
    const source = Array.isArray(raw) ? raw : Array.isArray(raw?.data) ? raw.data : [];
    const catalog = [];
    const models = new Set();
    for (const item of source) {
      const model = cleanModel(item?.model || item?.id);
      const id = cleanModel(item?.id || model);
      if (!model || !id || models.has(model)) continue;
      const efforts = [];
      const seenEfforts = new Set();
      for (const advertised of Array.isArray(item?.supportedReasoningEfforts)
        ? item.supportedReasoningEfforts : []) {
        const effort = cleanEffort(advertised?.effort || advertised);
        if (!effort || seenEfforts.has(effort)) continue;
        seenEfforts.add(effort);
        efforts.push({
          effort,
          description: cleanProtocolValue(advertised?.description, 500)
        });
      }
      models.add(model);
      catalog.push({
        id,
        model,
        displayName: cleanProtocolValue(item?.displayName, 200) || model,
        description: cleanProtocolValue(item?.description, 1000),
        defaultReasoningEffort: cleanEffort(item?.defaultReasoningEffort),
        supportedReasoningEfforts: efforts,
        isDefault: item?.isDefault === true
      });
    }
    return catalog;
  }

  function fallbackCatalog() {
    return FALLBACK_CATALOG.map(item => ({
      ...item,
      supportedReasoningEfforts: item.supportedReasoningEfforts.map(effort => ({ ...effort }))
    }));
  }

  function findModel(catalog, value) {
    const model = cleanModel(value);
    return (Array.isArray(catalog) ? catalog : []).find(item =>
      item.model === model || item.id === model) || null;
  }

  function availableEfforts(catalog, model) {
    return findModel(catalog, model)?.supportedReasoningEfforts || [];
  }

  function reconcileSelection(value, catalog) {
    const selection = normalize(value);
    const selectedModel = findModel(catalog, selection.model);
    if (!selectedModel) return normalize();
    const effort = availableEfforts(catalog, selectedModel.model)
      .some(option => option.effort === selection.reasoningEffort)
      ? selection.reasoningEffort
      : '';
    return { model: selectedModel.model, reasoningEffort: effort };
  }

  function parsePreference(raw) {
    try {
      return normalize(typeof raw === 'string' ? JSON.parse(raw) : raw);
    } catch {
      return normalize();
    }
  }

  function requestFields(value) {
    const selection = normalize(value);
    return {
      ...(selection.model ? { model: selection.model } : {}),
      ...(selection.reasoningEffort ? { reasoningEffort: selection.reasoningEffort } : {})
    };
  }

  return {
    availableEfforts,
    cleanEffort,
    cleanModel,
    fallbackCatalog,
    findModel,
    normalize,
    normalizeCatalog,
    parsePreference,
    reconcileSelection,
    requestFields
  };
}));
