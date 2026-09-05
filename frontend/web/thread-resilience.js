(function exposeThreadResilience(root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (root) root.CodexThreadResilience = api;
}(typeof globalThis === 'object' ? globalThis : this, function createThreadResilience() {
  'use strict';

  const retryDelays = [4000, 8000, 15000, 30000, 60000];

  function textValue(value) {
    if (typeof value === 'string') return value.trim();
    if (!value || typeof value !== 'object') return '';
    return textValue(value.error) || textValue(value.message) || textValue(value.title);
  }

  function parsedJson(value) {
    if (typeof value !== 'string') return null;
    const trimmed = value.trim();
    if (!(trimmed.startsWith('{') || trimmed.startsWith('['))) return null;
    try { return JSON.parse(trimmed); }
    catch { return null; }
  }

  function messageFromPayload(data, fallback) {
    return textValue(data?.error) || textValue(data?.message) || textValue(data) || fallback;
  }

  function detailFromPayload(data, raw) {
    // Once JSON has been parsed, never use the original JSON text as detail.
    // Doing so makes the UI encode the same response a second time.
    if (data !== null && data !== undefined) {
      if (!data || typeof data !== 'object') return '';
      const explicit = data.detail ?? data.details;
      if (explicit === null || explicit === undefined) return '';
      if (typeof explicit !== 'string') return textValue(explicit);
      const nested = parsedJson(explicit);
      return nested ? textValue(nested) : explicit.trim();
    }
    return String(raw || '').trim();
  }

  function retryableThreadError(error) {
    const status = Number(error?.status || 0);
    return status === 0 || status >= 500;
  }

  function retryDelay(failures) {
    const index = Math.min(Math.max(Number(failures || 1) - 1, 0), retryDelays.length - 1);
    return retryDelays[index];
  }

  function requestTimeout(method, path, explicit) {
    if (Number.isFinite(Number(explicit)) && Number(explicit) > 0) return Number(explicit);
    if (String(path || '').includes('/live?')) return 32000;
    return String(method || 'GET').toUpperCase() === 'GET' ? 20000 : 45000;
  }

  function createRequestDeadline(externalSignal, timeoutMs) {
    const controller = new AbortController();
    let timedOut = false;
    const relayAbort = () => controller.abort(externalSignal?.reason);
    if (externalSignal?.aborted) relayAbort();
    else externalSignal?.addEventListener?.('abort', relayAbort, { once: true });
    const timer = setTimeout(() => {
      timedOut = true;
      controller.abort(new DOMException('Request timed out', 'TimeoutError'));
    }, Math.max(1, Number(timeoutMs) || 1));
    return {
      signal: controller.signal,
      timedOut: () => timedOut,
      dispose() {
        clearTimeout(timer);
        externalSignal?.removeEventListener?.('abort', relayAbort);
      }
    };
  }

  return { createRequestDeadline, detailFromPayload, messageFromPayload, requestTimeout, retryableThreadError, retryDelay };
}));
