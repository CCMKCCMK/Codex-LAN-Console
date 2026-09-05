(function exposeThreadStream(root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (root) root.CodexThreadStream = api;
}(typeof globalThis === 'object' ? globalThis : this, function createThreadStream() {
  'use strict';

  function mergeStringArrays(existing, incoming) {
    const left = Array.isArray(existing) ? existing : [];
    const right = Array.isArray(incoming) ? incoming : [];
    const length = Math.max(left.length, right.length);
    return Array.from({ length }, (_, index) => {
      const before = String(left[index] || '');
      const after = String(right[index] || '');
      return after.length >= before.length ? after : before;
    });
  }

  function canonicalItemId(item) {
    const callId = String(item?.callId || item?.call_id || '').trim();
    if (callId) return `call:${callId}`;
    let id = String(item?.id || '').trim();
    if (!id) return '';
    if (id.startsWith('external-call-')) id = id.slice('external-call-'.length);
    if (/^(?:call[_-]|exec-)/.test(id)) return `call:${id}`;
    return `id:${id}`;
  }

  function weakItemId(item) {
    const id = String(item?.id || '').trim();
    return !id || /^item-\d+$/.test(id);
  }

  function itemMessageText(item) {
    const direct = String(item?.text || item?.message || '').trim();
    if (direct) return direct;
    if (!Array.isArray(item?.content)) return '';
    return item.content.map(part => {
      if (typeof part === 'string') return part;
      return typeof part?.text === 'string' ? part.text : '';
    }).filter(Boolean).join('\n').trim();
  }

  function normalizedMessage(item) {
    return itemMessageText(item).replace(/\s+/g, ' ').trim();
  }

  function messageDescriptor(item) {
    const type = String(item?.type || '');
    if (type !== 'userMessage' && type !== 'agentMessage') return null;
    const text = normalizedMessage(item);
    if (!text) return null;
    return { type, phase: type === 'agentMessage' ? String(item?.phase || '') : '', text };
  }

  function messagesEquivalent(left, right) {
    const a = messageDescriptor(left);
    const b = messageDescriptor(right);
    if (!a || !b || a.type !== b.type || a.phase !== b.phase) return false;
    if (a.text === b.text) return true;
    const shorter = a.text.length <= b.text.length ? a.text : b.text;
    const longer = shorter === a.text ? b.text : a.text;
    return shorter.length >= 4 && longer.startsWith(shorter);
  }

  function simpleHash(value) {
    let hash = 2166136261;
    for (let index = 0; index < value.length; index += 1) {
      hash ^= value.charCodeAt(index);
      hash = Math.imul(hash, 16777619);
    }
    return (hash >>> 0).toString(36);
  }

  function itemRenderKey(item, index = 0) {
    if (item?.__uiKey) return String(item.__uiKey);
    const identity = canonicalItemId(item);
    if (identity) return identity;
    const message = messageDescriptor(item);
    if (message) return `message:${message.type}:${message.phase}:${simpleHash(message.text)}`;
    const timestamp = String(item?.createdAt || item?.timestamp || '');
    if (timestamp) return `event:${String(item?.type || 'item')}:${timestamp}`;
    return `legacy:${String(item?.type || 'item')}:${index}`;
  }

  function mergeThreadItem(existing, incoming) {
    const type = String(incoming?.type || existing?.type || '');
    if (type === 'agentMessage') {
      const before = String(existing?.text || '');
      const after = String(incoming?.text || '');
      return {
        ...existing,
        ...incoming,
        text: after.length >= before.length ? after : before,
        phase: incoming?.phase || existing?.phase || null
      };
    }
    if (type === 'reasoning') {
      return {
        ...existing,
        ...incoming,
        summary: mergeStringArrays(existing?.summary, incoming?.summary),
        content: mergeStringArrays(existing?.content, incoming?.content)
      };
    }
    return { ...existing, ...incoming };
  }

  function matchedItemIndex(items, incoming) {
    const identity = canonicalItemId(incoming);
    if (identity) {
      const exact = items.findIndex(item => canonicalItemId(item) === identity);
      if (exact >= 0) return exact;
    }
    if (!messageDescriptor(incoming)) return -1;
    return items.findIndex(item =>
      messagesEquivalent(item, incoming) && (weakItemId(item) || weakItemId(incoming)));
  }

  function mergeMatchedItem(existing, incoming, index) {
    const merged = mergeThreadItem(existing, incoming);
    const existingIdentity = canonicalItemId(existing);
    const incomingIdentity = canonicalItemId(incoming);
    if ((!weakItemId(existing) && weakItemId(incoming)) ||
        (existingIdentity && existingIdentity === incomingIdentity && existing?.id))
      merged.id = existing.id;
    merged.__uiKey = existing?.__uiKey || itemRenderKey(existing, index);
    return merged;
  }

  // Keep live-only items until the canonical page has caught up. Canonical reads
  // can legitimately omit the newest commentary/tool events for several polls.
  function mergeTurnItems(existing, incoming) {
    const result = [];
    const appendOrMerge = item => {
      const position = matchedItemIndex(result, item);
      if (position >= 0) {
        result[position] = mergeMatchedItem(result[position], item, position);
      } else {
        result.push({ ...item, __uiKey: item?.__uiKey || itemRenderKey(item, result.length) });
      }
    };
    for (const item of existing || []) appendOrMerge(item);
    for (const item of incoming || []) appendOrMerge(item);
    return result;
  }

  function mergeTurns(existing, incoming, incomingIsCanonical = false) {
    const before = existing || {};
    const after = incoming || {};
    const canonicalItems = incomingIsCanonical &&
      /^(recentFull|full)$/i.test(String(after?.itemsView || ''));
    const mergedItems = canonicalItems
      ? mergeTurnItems(after?.items, before?.items)
      : mergeTurnItems(before?.items, after?.items);
    if (canonicalItems) {
      const previousItems = before?.items || [];
      for (const item of mergedItems) {
        const previousIndex = matchedItemIndex(previousItems, item);
        if (previousIndex < 0) continue;
        const previous = previousItems[previousIndex];
        item.__uiKey = previous?.__uiKey || itemRenderKey(previous, previousIndex);
      }
    }
    return {
      ...before,
      ...after,
      items: mergedItems
    };
  }

  function insertOrderedTurn(result, incomingTurns, incomingIndex, turn) {
    const nextAnchor = incomingTurns.slice(incomingIndex + 1)
      .map(item => String(item?.id || ''))
      .find(id => id && result.some(existing => String(existing?.id || '') === id));
    if (nextAnchor) {
      const position = result.findIndex(existing => String(existing?.id || '') === nextAnchor);
      result.splice(position, 0, turn);
      return;
    }
    const previousAnchors = incomingTurns.slice(0, incomingIndex)
      .map(item => String(item?.id || '')).filter(Boolean).reverse();
    const previousAnchor = previousAnchors.find(id =>
      result.some(existing => String(existing?.id || '') === id));
    if (previousAnchor) {
      const position = result.findIndex(existing => String(existing?.id || '') === previousAnchor);
      result.splice(position + 1, 0, turn);
      return;
    }
    result.push(turn);
  }

  function mergeTurnCollections(existing, incoming) {
    const result = [...(existing || [])];
    const incomingTurns = incoming || [];
    for (let incomingIndex = 0; incomingIndex < incomingTurns.length; incomingIndex += 1) {
      const turn = incomingTurns[incomingIndex];
      const id = String(turn?.id || '');
      const position = id ? result.findIndex(item => String(item?.id || '') === id) : -1;
      if (position >= 0) {
        result[position] = mergeTurns(result[position], turn);
      } else {
        insertOrderedTurn(result, incomingTurns, incomingIndex, turn);
      }
    }
    return result;
  }

  function reconcileThreadPageTurns(existing, incoming) {
    const incomingIds = new Set((incoming || []).map(turn => String(turn?.id || '')).filter(Boolean));
    const earlier = (existing || []).filter(turn => !incomingIds.has(String(turn?.id || '')));
    const existingById = new Map((existing || []).map(turn => [String(turn?.id || ''), turn]));
    const canonical = (incoming || []).map(turn => {
      const before = existingById.get(String(turn?.id || ''));
      if (!before) return turn;
      return mergeTurns(before, turn, true);
    });
    return mergeTurnCollections(earlier, canonical);
  }

  function prependTurnCollections(older, current) {
    return mergeTurnCollections(older || [], current || []);
  }

  function scrollDistanceFromBottom(metrics) {
    const scrollHeight = Number(metrics?.scrollHeight);
    const scrollTop = Number(metrics?.scrollTop);
    const clientHeight = Number(metrics?.clientHeight);
    if (![scrollHeight, scrollTop, clientHeight].every(Number.isFinite)) return Number.POSITIVE_INFINITY;
    return Math.max(0, scrollHeight - scrollTop - clientHeight);
  }

  function isScrollAtBottom(metrics, threshold = 32) {
    const distance = scrollDistanceFromBottom(metrics);
    const limit = Math.max(0, Number(threshold) || 0);
    return distance <= limit;
  }

  function shouldAutoFollow({ mode, wasNearBottom, forceFollow = false, userInteracting = false }) {
    if (forceFollow || mode === 'initial') return true;
    if (mode === 'anchor' || mode === 'position') return false;
    return Boolean(wasNearBottom) && !userInteracting;
  }

  function scrollAnchorAdjustment(beforeTop, afterTop) {
    const before = Number(beforeTop);
    const after = Number(afterTop);
    return Number.isFinite(before) && Number.isFinite(after) ? after - before : 0;
  }

  function shouldPollThreadDetail(state) {
    return state?.visible === true && state?.authenticated === true &&
      state?.page === 'threadDetail' && Boolean(state?.threadId);
  }

  function liveReconnectPlan(failures, fullRefreshAttempts) {
    const delays = [4000, 8000, 15000];
    const failureIndex = Math.min(Math.max(Number(failures || 1) - 1, 0), delays.length - 1);
    return {
      forceFullRefresh: Number(fullRefreshAttempts || 0) < 3,
      delayMs: delays[failureIndex]
    };
  }

  function deliveryReceiptPresentation(response, inserted = false) {
    const root = response && typeof response === 'object' ? response : {};
    const receipt = root.receipt || root.deliveryReceipt || root.delivery || root;
    const status = String(receipt.status || receipt.state || root.status || root.state ||
      (root.queued || receipt.queued ? 'queued' : 'accepted')).toLowerCase();
    const id = String(receipt.id || receipt.receiptId || root.receiptId || root.id || '');
    const queued = root.queued === true || receipt.queued === true || /queue|pending|wait/.test(status);
    const pending = !/delivered|failed|cancelled|canceled/.test(status);
    const message = String(receipt.message || root.message || (queued
      ? '已排队，电脑端会在当前轮次结束后继续发送'
      : inserted ? '已插入当前轮次' : '电脑端已接收指令'));
    const presentation = { id, status, queued, pending, message };
    if (status === 'failed' && receipt.lastError) presentation.error = String(receipt.lastError).slice(0, 1000);
    return presentation;
  }

  return {
    deliveryReceiptPresentation,
    liveReconnectPlan,
    mergeStringArrays,
    mergeThreadItem,
    mergeTurnCollections,
    mergeTurnItems,
    itemRenderKey,
    prependTurnCollections,
    reconcileThreadPageTurns,
    isScrollAtBottom,
    scrollDistanceFromBottom,
    scrollAnchorAdjustment,
    shouldAutoFollow,
    shouldPollThreadDetail
  };
}));
