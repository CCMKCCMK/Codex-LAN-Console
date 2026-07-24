(function attachThreadStatus(root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.CodexThreadStatus = api;
})(typeof globalThis === 'object' ? globalThis : this, function createThreadStatus() {
  'use strict';

  // Rollout files provide lifecycle events, not a heartbeat. After this window,
  // an unmatched "started" event is evidence that a task ran, not proof that it
  // is still running now.
  const externalActiveFreshnessMs = 30 * 60 * 1000;

  function timestampMs(value) {
    if (value === null || value === undefined || value === '') return NaN;
    if (typeof value === 'number') return value < 1e12 ? value * 1000 : value;
    return Date.parse(value);
  }

  function threadStatusType(thread) {
    const status = thread && thread.status;
    return typeof status === 'string' ? status : status && status.type;
  }

  function staleRuntime(runtime, reason) {
    return {
      ...runtime,
      phase: 'unknown',
      isRunning: null,
      activeTurnId: null,
      activeFlags: [],
      canControl: true,
      stale: true,
      staleReason: reason,
      originalPhase: runtime && runtime.phase
    };
  }

  function checkedRuntime(runtime, now) {
    if (!runtime) return null;
    if (runtime.stale) return staleRuntime(runtime, runtime.staleReason || 'server');
    if (runtime.source !== 'rollout' || runtime.isRunning !== true) return runtime;

    const freshUntil = timestampMs(runtime.freshUntil);
    if (Number.isFinite(freshUntil)) {
      if (now > freshUntil) return staleRuntime(runtime, 'expired');
      return runtime;
    }
    const observedAt = timestampMs(runtime.observedAt);
    if (!Number.isFinite(observedAt)) return staleRuntime(runtime, 'missingTimestamp');
    if (now - observedAt > externalActiveFreshnessMs) return staleRuntime(runtime, 'expired');
    return runtime;
  }

  function normalizeThreadRuntime({ thread, runtime, pending, now = Date.now() } = {}) {
    const live = checkedRuntime(runtime, now);
    if (pending && pending.phase) {
      return {
        ...(live || {}),
        phase: pending.phase,
        isRunning: true,
        canControl: live ? live.canControl !== false : true,
        source: 'pending',
        observedAt: pending.observedAt || (live && live.observedAt) || null,
        stale: false
      };
    }

    const type = threadStatusType(thread);
    if (type === 'notLoaded') {
      // notLoaded explicitly means this app-server cannot see the task. Only a
      // fresh external lifecycle record may assert that it is still running.
      // A persisted terminal result remains authoritative even when the live
      // app-server cannot materialize the thread.
      if (live && live.isRunning === false) return live;
      if (live && live.source === 'rollout' && !live.stale) return live;
      if (live && live.stale) return live;
      return {
        phase: 'unknown',
        isRunning: null,
        canControl: false,
        source: 'history',
        notLoaded: true
      };
    }

    if (live) return live;

    const status = thread && thread.status;
    const flags = Array.isArray(status && status.activeFlags) ? status.activeFlags : [];
    if (type === 'active') {
      // thread/list is read from the persisted state database. Its active value
      // can outlive the process that owned the turn, so it is only a hint unless
      // runtime or pending evidence above confirms current activity.
      return {
        phase: 'unknown',
        isRunning: null,
        canControl: false,
        activeFlags: flags,
        source: 'history',
        historicalPhase: 'active'
      };
    }
    if (type === 'idle') {
      return { phase: 'idle', isRunning: false, canControl: true, source: 'threadStatus' };
    }
    if (type === 'systemError') {
      return { phase: 'error', isRunning: false, canControl: true, source: 'threadStatus' };
    }
    return { phase: 'unknown', isRunning: null, canControl: false, source: 'history' };
  }

  function mergeRuntimeStates(current = {}, incoming = {}, detailRefreshes, summaryStartedAt = 0) {
    const merged = {};
    const ids = new Set([...Object.keys(current || {}), ...Object.keys(incoming || {})]);
    for (const id of ids) {
      const detailAt = typeof detailRefreshes?.get === 'function'
        ? Number(detailRefreshes.get(id) || 0)
        : Number(detailRefreshes?.[id] || 0);
      if (detailAt > summaryStartedAt) {
        if (current[id]) merged[id] = current[id];
        continue;
      }
      const next = incoming[id];
      if (!next) continue;
      const previous = current[id];
      const previousAt = timestampMs(previous?.observedAt);
      const nextAt = timestampMs(next?.observedAt);
      merged[id] = previous && Number.isFinite(previousAt) &&
        (!Number.isFinite(nextAt) || previousAt > nextAt)
        ? previous
        : next;
    }
    return merged;
  }

  function statusLabel(status) {
    const phase = status && (status.phase || (typeof status === 'string' ? status : status.type));
    if (status && status.stale) return '状态待确认';
    if (phase === 'idle' && status && status.lastOutcome === 'completed') return '已完成';
    if (phase === 'idle' && status && status.lastOutcome === 'interrupted') return '已停止';
    if (phase === 'idle' && status && status.lastOutcome === 'failed') return '上次失败';
    if (phase === 'running' && status && status.source === 'rollout') return '运行中（其他 Codex 客户端）';
    return ({
      waitingInput: '等待你的回答', waitingApproval: '等待你的批准', waitingAction: '等待你的操作',
      running: '正在运行', idle: '空闲', error: '运行出错', unavailable: '连接中断',
      notLoaded: '状态不可见', unknown: '状态不可见', inProgress: '正在运行', active: '正在运行',
      completed: '已完成', interrupted: '已停止', failed: '失败', systemError: '系统错误'
    })[phase] || '状态未知';
  }

  function statusSourceLabel(status) {
    if (!status) return '来源未知';
    if (status.source === 'pending') return '待处理请求';
    if (status.source === 'rollout') return status.stale ? '过期的外部记录' : '其他 Codex 任务记录';
    if (status.source === 'appServer') return '当前桥接状态';
    if (status.source === 'threadStatus') return '任务状态';
    if (status.source === 'history') return '任务历史';
    return '状态来源未知';
  }

  function statusTitle(status) {
    if (status && status.stale) {
      return status.staleReason === 'missingTimestamp'
        ? '外部运行记录没有更新时间，无法确认当前是否仍在运行；进程列表不能定位到具体任务，因此不参与判断'
        : '外部运行记录已超过 30 分钟没有更新，无法确认当前是否仍在运行；进程列表不能定位到具体任务，因此不参与判断';
    }
    if (status && status.source === 'rollout' && status.isRunning === true) {
      return '根据其他 Codex 客户端写入的任务记录判断；进程列表不能定位到具体任务，因此不参与判断';
    }
    if (status && status.source === 'rollout') return '根据其他 Codex 客户端写入的任务记录判断；该记录显示轮次已经结束';
    if (status && status.source === 'appServer') return '来自当前桥接连接的实时任务状态';
    if (status && status.source === 'pending') return '当前桥接连接仍有待处理的回答或批准请求';
    if (status && status.source === 'threadStatus') return '来自本次任务列表刷新返回的任务状态';
    if (status && (status.source === 'history' || status.phase === 'unknown')) {
      return status.historicalPhase === 'active'
        ? '任务历史仍标记为 active，但当前没有实时运行证据，无法确认是否仍在运行'
        : '当前桥接连接看不到该任务的实时状态，无法可靠判断是否正在运行';
    }
    return statusLabel(status);
  }

  return Object.freeze({
    externalActiveFreshnessMs,
    mergeRuntimeStates,
    normalizeThreadRuntime,
    statusLabel,
    statusSourceLabel,
    statusTitle,
    timestampMs
  });
});
