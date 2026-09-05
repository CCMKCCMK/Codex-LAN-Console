(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  root.CodexAdministratorMode = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  function normalize(value) {
    const detected = value?.detected === true;
    return {
      detected,
      active: detected && value?.active === true,
      scope: value?.scope === 'bridgeOwnedTasksOnly' ? value.scope : 'bridgeOwnedTasksOnly'
    };
  }

  function presentation(value) {
    const status = normalize(value);
    if (!status.detected) {
      return {
        ...status,
        badge: '状态未知',
        detail: '暂时无法读取电脑端进程权限。管理员操作可能仍会在电脑上弹出确认。'
      };
    }
    if (status.active) {
      return {
        ...status,
        badge: '已启用',
        detail: '仅本机或 Tailscale 可用；首次启用可能需重新配对。仅 Bridge 新建或手机发起的新轮次继承，其他 Codex 不继承。'
      };
    }
    return {
      ...status,
      badge: '未启用',
      detail: '电脑端 Bridge 当前没有管理员权限。首次启用需在电脑确认，之后仅允许本机或 Tailscale 连接。'
    };
  }

  return { normalize, presentation };
});
