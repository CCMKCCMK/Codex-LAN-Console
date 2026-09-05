'use strict';

// Native notification delivery and user navigation are separate operations.
// A successful delivery never depends on a task's network/data refresh.
(function (root) {
  function createRouter({ now, storage, current, open, offer, clearOffer, cancelNative }) {
    const key = 'codexNavigationIntentAt';
    let manualAt = 0;
    try { manualAt = Number(storage?.getItem(key)) || 0; } catch {}
    const delivered = new Set();
    let offered = '';
    function manual() {
      manualAt = now();
      try { storage?.setItem(key, String(manualAt)); } catch {}
      offered = '';
      clearOffer();
      cancelNative();
    }
    function legacy(id) {
      if (!id || current() === id || offered === id) return true;
      // Old APKs send only a task ID. A replay and a new notification tap cannot
      // be distinguished, so require a visible user action instead of redirecting.
      offered = id;
      offer(() => { manual(); open(id); });
      return true;
    }
    function receive(id, requestId, receivedAt) {
      if (!id || !requestId || !Number.isFinite(receivedAt)) return false;
      if (delivered.has(requestId)) return true;
      if (receivedAt > manualAt && now() - receivedAt <= 60000) {
        clearOffer();
        if (current() !== id && open(id) === false) return false;
      }
      delivered.add(requestId);
      if (delivered.size > 64) delivered.delete(delivered.values().next().value);
      return true;
    }
    return { manual, legacy, receive };
  }
  if (typeof module === 'object' && module.exports) module.exports = { createRouter };
  if (!root?.document) return;
  let storage;
  try { storage = root.sessionStorage; } catch {}
  const clearOffer = () => root.document.getElementById('notificationRouteNotice')?.remove();
  const router = createRouter({
    now: () => Date.now(), storage,
    current: () => {
      const state = root.normalizedNavigationState?.();
      return state?.page === 'threadDetail' ? state.threadId : null;
    },
    open: id => root.CodexConsoleOpenThread(id),
    clearOffer,
    cancelNative: () => { try { root.CodexAndroidNotifications?.cancelPendingThreadOpen?.(); } catch {} },
    offer: accept => {
      clearOffer();
      const notice = root.document.createElement('aside');
      notice.id = 'notificationRouteNotice';
      notice.className = 'notification-route-notice';
      notice.setAttribute('role', 'status');
      const label = root.document.createElement('span');
      label.textContent = '收到任务打开请求，已保留当前页面';
      const button = root.document.createElement('button');
      button.type = 'button'; button.textContent = '查看任务'; button.onclick = accept;
      const dismiss = root.document.createElement('button');
      dismiss.type = 'button'; dismiss.textContent = '×';
      dismiss.setAttribute('aria-label', '忽略任务打开请求');
      // Keep this ID deduplicated when an old APK retries after dismissing it.
      dismiss.onclick = clearOffer;
      notice.append(label, button, dismiss);
      root.document.body.append(notice);
    }
  });
  root.ConsoleNotificationNavigation = router;
  root.CodexConsoleReceiveNotification = (id, requestId, receivedAt) =>
    typeof root.CodexConsoleOpenThread === 'function' && router.receive(id, requestId, receivedAt);
  root.document.addEventListener('click', event => {
    if (event.isTrusted && event.target.closest?.('.console-nav a, .console-nav button, .task-card')) router.manual();
  }, true);
  root.addEventListener('popstate', () => router.manual());
})(typeof window === 'undefined' ? null : window);
