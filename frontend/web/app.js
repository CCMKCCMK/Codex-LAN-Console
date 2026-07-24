if (navigator.userAgent.includes('CodexLanConsole/')) document.documentElement.classList.add('native-shell');

const $ = selector => document.querySelector(selector);
const tokenKey = 'codexLanToken';
const turnPageSize = 6;
const localResolutionCache = new Map();
const remoteFileCache = new Map();
const isNativeShell = document.documentElement.classList.contains('native-shell');
const maxAttachmentCount = 10;
const maxAttachmentBytes = 128 * 1024 * 1024;
const maxAttachmentRequestBytes = 256 * 1024 * 1024;
const navigationMarker = 'codexLanConsole';
const rootPage = 'overview';
const primaryPages = new Set(['overview', 'threads', 'projects', 'processes', 'approvals']);

let token = localStorage.getItem(tokenKey) || '';
let authenticated = false;
let currentThread = '';
let currentThreadCwd = '';
let currentTurns = [];
let currentTurnCursor = '';
let currentActiveTurnId = '';
let currentRuntimeState = null;
let currentThreadSignature = '';
let hasEarlierTurns = false;
let loadingOlder = false;
let sending = false;
let composing = false;
let selectedIntent = 'plain';
let pendingRequests = [];
let pendingSignature = null;
let approvalAutomation = { autoApproveAll: false, supported: true };
let approvalAutomationBusy = false;
let runtimeStates = {};
let allowedPermissionProfiles = new Set([':read-only', ':workspace', ':danger-full-access']);
let permissionsLoadedFor = '';
let availableSkills = [];
let skillsLoadedFor = '';
let availableTools = [];
let toolsLoadedFor = '';
let selectedSkills = [];
let selectedTools = [];
let selectedToolDetails = {};
let commandPanelFilter = '';
let lastDiagnosticText = '';
let navigationDepth = 0;
let navigationBackPending = false;
let navigationBackTimer = 0;
let notificationStatus = { supported: false };
let notificationBusy = false;
let riskResolver = null;
let summaryLoadPromise = null;
let lastSummaryRefreshAt = 0;
let lastApprovalSettingsRefreshAt = 0;
const latestThreadRequests = new Map();
const lastThreadRefreshAt = new Map();
const attachmentDrafts = new Map();

function apiError(path, method, status, data, raw, response, suppressDiagnostic = false) {
  const fallback = status ? `请求失败 ${status}` : '无法连接电脑端服务';
  const message = data?.error || data?.message || fallback;
  const error = new Error(message);
  error.status = status;
  error.path = path;
  error.method = method;
  error.requestId = response?.headers?.get('X-Request-ID') || data?.requestId || data?.traceId || '';
  error.detail = data?.detail || data?.details || raw || '';
  if (!suppressDiagnostic && (!status || status >= 500)) showDiagnostic(error);
  return error;
}

async function apiRequest(path, options = {}, form = false) {
  const headers = { ...(options.headers || {}) };
  if (token) headers.Authorization = `Bearer ${token}`;
  const suppressDiagnostic = Boolean(options.suppressDiagnostic);
  const request = { ...options, headers, credentials: 'same-origin' };
  delete request.suppressDiagnostic;
  if (!form && options.body !== undefined) {
    headers['Content-Type'] = 'application/json';
    request.body = JSON.stringify(options.body);
  }
  const method = String(request.method || 'GET').toUpperCase();
  let response;
  try {
    response = await fetch('/api' + path, request);
  } catch (cause) {
    const error = apiError(path, method, 0, null, cause?.message || String(cause), null, suppressDiagnostic);
    error.cause = cause;
    throw error;
  }
  if (response.status === 401) {
    authenticated = false;
    $('#pair').classList.remove('hidden');
    const error = new Error('需要重新配对');
    error.status = 401;
    error.path = path;
    error.method = method;
    throw error;
  }
  const raw = response.status === 204 ? '' : await response.text();
  let data = null;
  if (raw) {
    try { data = JSON.parse(raw); }
    catch { data = null; }
  }
  if (!response.ok) {
    throw apiError(path, method, response.status, data, raw, response, suppressDiagnostic);
  }
  return data;
}

async function api(path, options = {}) {
  return apiRequest(path, options, false);
}

async function apiForm(path, formData) {
  return apiRequest(path, { method: 'POST', body: formData }, true);
}

function showDiagnostic(error) {
  const panel = $('#diagnostic');
  if (!panel) return;
  const status = error.status || 'NETWORK';
  const title = error.status >= 500 ? '电脑端处理失败' : '无法连接电脑端';
  const details = {
    time: new Date().toISOString(),
    status,
    method: error.method || 'UNKNOWN',
    endpoint: error.path ? `/api${error.path}` : 'unknown',
    requestId: error.requestId || 'not provided',
    detail: String(error.detail || error.message || '').slice(0, 4000)
  };
  $('#diagnosticTitle').textContent = title;
  $('#diagnosticMessage').textContent = error.message || '操作没有完成。诊断信息已保留。';
  lastDiagnosticText = JSON.stringify(details, null, 2);
  $('#diagnosticDetails').textContent = lastDiagnosticText;
  panel.classList.remove('hidden');
}

const esc = value => String(value ?? '').replace(/[&<>"']/g, character => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
}[character]));

const when = value => value
  ? new Date(typeof value === 'number' ? value * 1000 : value).toLocaleString()
  : '--';

function toast(message) {
  const element = $('#toast');
  element.textContent = message;
  element.classList.add('show');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => element.classList.remove('show'), 2200);
}

function nativeNotificationBridge() {
  if (!isNativeShell) return null;
  const bridge = window.CodexAndroidNotifications;
  return bridge && typeof bridge.getStatus === 'function' ? bridge : null;
}

function parsedNotificationStatus(value) {
  let result = value;
  if (typeof result === 'string') {
    try { result = JSON.parse(result); }
    catch { return null; }
  }
  if (!result || typeof result !== 'object') return null;
  return {
    supported: result.supported !== false,
    configured: result.configured === true,
    enabled: result.enabled === true,
    permission: String(result.permission || 'required').toLowerCase(),
    serviceRunning: result.serviceRunning === true,
    batteryOptimized: result.batteryOptimized === true,
    backgroundRestricted: result.backgroundRestricted === true,
    manufacturer: String(result.manufacturer || '').toLowerCase(),
    lastError: String(result.lastError || '').trim().slice(0, 240)
  };
}

function renderNotificationStatus() {
  const card = $('#notificationCard');
  if (!card) return;
  const bridge = nativeNotificationBridge();
  const supported = Boolean(bridge && notificationStatus.supported !== false);
  card.classList.toggle('hidden', !supported);
  if (!supported) return;

  const badge = $('#notificationBadge');
  const message = $('#notificationMessage');
  const action = $('#notificationAction');
  const test = $('#notificationTest');
  const settings = $('#notificationSettings');
  const permission = notificationStatus.permission;
  const isApple = /apple|iphone|ipad|ios/.test(notificationStatus.manufacturer);
  const active = notificationStatus.configured && notificationStatus.enabled &&
    permission === 'granted' && (notificationStatus.serviceRunning || isApple);
  const blocked = permission === 'blocked';
  const needsPermission = permission === 'required' || permission === 'denied';
  const xiaomiPowerManagement = /xiaomi|redmi/.test(notificationStatus.manufacturer);
  const needsBackgroundSettings = !isApple && (notificationStatus.backgroundRestricted ||
    (notificationStatus.batteryOptimized && xiaomiPowerManagement));

  badge.className = 'notification-badge';
  if (active) {
    badge.textContent = isApple ? '系统通知已开启' : '后台运行中';
    badge.classList.add('active');
    message.textContent = isApple
      ? 'iOS 会按系统安排定时刷新任务；后台提醒可能不会立即送达。'
      : '任务完成、需要审批或等待你回答时，手机会发出通知。';
    action.textContent = '关闭';
    action.dataset.mode = 'disable';
  } else if (blocked) {
    badge.textContent = '需要系统授权';
    badge.classList.add('attention');
    message.textContent = `${isApple ? 'iOS' : 'Android'} 已阻止通知。请在系统设置中允许通知，然后返回这里开启。`;
    action.textContent = '重新检查';
    action.dataset.mode = 'refresh';
  } else if (needsPermission) {
    badge.textContent = '等待授权';
    badge.classList.add('attention');
    message.textContent = '允许通知后，即使没有停留在这个页面，也能收到任务提醒。';
    action.textContent = '允许通知';
    action.dataset.mode = 'enable';
  } else {
    badge.textContent = notificationStatus.enabled ? '等待启动' : '未开启';
    message.textContent = isApple
      ? '开启后，iOS 会在系统允许后台刷新时检查任务并发送提醒。'
      : '开启后会保留一条低打扰的后台连接状态，并在任务完成或需要你决定时提醒。';
    action.textContent = '开启通知';
    action.dataset.mode = 'enable';
  }

  if (notificationStatus.lastError) {
    badge.textContent = notificationStatus.enabled && notificationStatus.serviceRunning
      ? '正在重连'
      : '需要处理';
    badge.classList.remove('active');
    badge.classList.add('attention');
    message.textContent = notificationStatus.lastError;
  } else if (notificationStatus.enabled && notificationStatus.backgroundRestricted && isApple) {
    badge.textContent = '系统定时刷新';
    badge.classList.remove('attention');
    badge.classList.add('active');
    message.textContent = 'iOS 不允许持续后台连接；系统会择机唤醒应用检查任务，因此提醒可能延迟。';
  } else if (notificationStatus.enabled && notificationStatus.backgroundRestricted) {
    badge.textContent = '后台受限';
    badge.classList.remove('active');
    badge.classList.add('attention');
    message.textContent = 'Android 正在限制后台运行。请在后台设置中允许 Codex LAN 持续运行。';
  } else if (active && needsBackgroundSettings) {
    message.textContent = '后台提醒正在运行。为避免系统省电功能中断，可在后台设置中允许持续运行。';
  }

  action.disabled = notificationBusy || (!token && !notificationStatus.configured);
  test.classList.toggle('hidden', !active || typeof bridge.testNotification !== 'function');
  test.disabled = notificationBusy;
  const backgroundSettingsAvailable = needsBackgroundSettings &&
    typeof bridge.openBatterySettings === 'function';
  const notificationSettingsAvailable = typeof bridge.openSettings === 'function';
  settings.classList.toggle('hidden', !backgroundSettingsAvailable && !notificationSettingsAvailable);
  settings.dataset.target = backgroundSettingsAvailable && permission === 'granted'
    ? 'battery'
    : 'notification';
  settings.textContent = settings.dataset.target === 'battery' ? '后台设置' : '通知设置';
  settings.disabled = notificationBusy;
}

function refreshNativeNotificationStatus(detail) {
  const bridge = nativeNotificationBridge();
  if (!bridge) {
    notificationStatus = { supported: false };
    renderNotificationStatus();
    return notificationStatus;
  }
  let current = null;
  try { current = bridge.getStatus(); }
  catch { current = null; }
  const next = parsedNotificationStatus(detail) || parsedNotificationStatus(current);
  if (next) notificationStatus = next;
  renderNotificationStatus();
  return notificationStatus;
}

function callNativeNotification(method, ...args) {
  const bridge = nativeNotificationBridge();
  if (!bridge || typeof bridge[method] !== 'function') return 'unavailable';
  try { return bridge[method](...args); }
  catch { return 'unavailable'; }
}

async function enableNativeNotifications() {
  if (!token && !notificationStatus.configured) {
    toast('请重新配对一次以开启后台通知');
    return;
  }
  notificationBusy = true;
  renderNotificationStatus();
  try {
    if (token) {
      // A fresh pairing may point at a different computer. Hand the credential
      // directly to Android's encrypted store before retiring the JS copy.
      const configured = callNativeNotification('configure', token);
      if (configured !== 'configured') {
        toast('通知服务暂时无法启用');
        return;
      }
    }
    refreshNativeNotificationStatus();
    if (notificationStatus.permission !== 'granted') {
      // Persist the explicit opt-in before Android shows its permission sheet so
      // the service can start immediately when the user grants access.
      callNativeNotification('setEnabled', true);
      if (notificationStatus.permission === 'blocked' &&
          typeof nativeNotificationBridge()?.openSettings === 'function') {
        callNativeNotification('openSettings');
      } else {
        callNativeNotification('requestPermission');
      }
      toast('请允许 Codex LAN 发送任务通知');
      return;
    }
    callNativeNotification('setEnabled', true);
    toast('后台任务通知已开启');
  } finally {
    notificationBusy = false;
    setTimeout(() => refreshNativeNotificationStatus(), 250);
  }
}

function openNotificationThread(threadId) {
  const id = String(threadId || '').trim();
  if (!id) {
    showPage('threads');
    load();
    return true;
  }
  openThread(id).catch(error => toast(error.message));
  return true;
}

window.CodexConsoleOpenThread = openNotificationThread;
window.addEventListener('codex-notification-open', event => openNotificationThread(event.detail?.threadId));
window.addEventListener('codex-notification-status', event => refreshNativeNotificationStatus(event.detail));

function empty(message) {
  return `<div class="empty">${esc(message)}</div>`;
}

function itemText(item) {
  if (!item) return '';
  if (typeof item.text === 'string') return item.text;
  if (Array.isArray(item.content)) return item.content.map(part => part?.text || '').filter(Boolean).join('\n');
  return String(item.message || '');
}

function parsedPayload(item) {
  const raw = itemText(item).trim();
  if (!raw.startsWith('{') && !raw.startsWith('[')) return item;
  try {
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? parsed : item;
  } catch {
    return item;
  }
}

function plainSummary(value) {
  const values = Array.isArray(value) ? value : [value];
  return values
    .map(item => typeof item === 'string' ? item : (item?.text || item?.summary || ''))
    .filter(Boolean)
    .join(' · ')
    .replace(/\*\*/g, '')
    .trim();
}

function normalizedHost(hostname) {
  return String(hostname || '').toLowerCase().replace(/^\[/, '').replace(/\]$/, '').replace(/\.$/, '');
}

function isLocalDevelopmentUrl(url) {
  if (!(url instanceof URL) || !['http:', 'https:'].includes(url.protocol)) return false;
  const host = normalizedHost(url.hostname);
  return host === 'localhost' || host.endsWith('.localhost') || host === '0.0.0.0' ||
    host === '::' || host === '::1' || /^127(?:\.\d{1,3}){3}$/.test(host);
}

const fileExtensions = new Set([
  'apk', 'aab', 'zip', '7z', 'rar', 'pdf', 'doc', 'docx', 'xls', 'xlsx', 'csv', 'ppt', 'pptx',
  'txt', 'md', 'json', 'xml', 'yaml', 'yml', 'html', 'htm', 'css', 'js', 'mjs', 'cjs', 'ts', 'tsx',
  'jsx', 'py', 'java', 'kt', 'kts', 'cs', 'cpp', 'c', 'h', 'hpp', 'go', 'rs', 'sh', 'ps1', 'bat',
  'png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'bmp', 'heic', 'mp4', 'webm', 'mov', 'mkv', 'avi',
  'mp3', 'wav', 'm4a', 'ogg', 'log'
]);

function decodedReference(value) {
  try { return decodeURIComponent(value); }
  catch { return value; }
}

function fileReference(rawValue) {
  let value = decodedReference(String(rawValue || '').trim()).replace(/^<|>$/g, '');
  if (!value || /^(?:https?|mailto|data|javascript):/i.test(value)) return '';
  if (/^sandbox:/i.test(value)) value = value.replace(/^sandbox:\/*/i, '/');
  if (/^file:/i.test(value)) {
    try {
      const url = new URL(value);
      const host = url.hostname ? `//${url.hostname}` : '';
      value = host + decodedReference(url.pathname);
      if (/^\/[A-Za-z]:\//.test(value)) value = value.slice(1);
    } catch { return ''; }
  }
  value = value.replace(/:(\d+)(?::\d+)?$/, '');
  const clean = value.split(/[?#]/, 1)[0];
  const extension = clean.match(/\.([A-Za-z0-9]{1,8})$/)?.[1]?.toLowerCase();
  if (!extension || !fileExtensions.has(extension)) return '';
  const absolute = /^[A-Za-z]:[\\/]/.test(clean) || /^\\\\[^\\/]+[\\/]/.test(clean) || /^\//.test(clean);
  const relative = /^(?:\.{1,2}[\\/]|[^:\s<>"'`]+[\\/])/.test(clean);
  const bareName = !/[\\/:<>"'`\r\n]/.test(clean) && clean.trim().length > extension.length + 1;
  return absolute || relative || bareName ? clean : '';
}

function markRemoteFile(element, path, image = false) {
  element.dataset.filePath = path;
  element.dataset.fileThread = currentThread || '';
  if (image) {
    element.removeAttribute('src');
    element.classList.add('remote-file-image-pending');
    element.setAttribute('aria-busy', 'true');
  } else {
    element.setAttribute('href', '#');
    element.classList.add('remote-file-link');
    element.removeAttribute('target');
    element.setAttribute('title', '从远程电脑打开或下载');
  }
}

function linkifyFileText(root) {
  const pattern = /(?:(?:sandbox:\/+|file:\/\/\/|[A-Za-z]:[\\/]|\\\\[^\\/\s]+[\\/]|\/(?:mnt\/data|home|tmp|workspace|Users|var\/tmp)\/|\.{1,2}[\\/]|(?:[\w.-]+[\\/]))[^\n<>"'`]*?\.|[\p{L}\p{N}_-][\p{L}\p{N}_.-]*\.)(?:apk|aab|zip|7z|rar|pdf|docx?|xlsx?|csv|pptx?|txt|md|json|xml|ya?ml|html?|css|m?js|cjs|tsx?|jsx|py|java|kts?|cs|cpp|c|h|hpp|go|rs|sh|ps1|bat|png|jpe?g|gif|webp|svg|bmp|heic|mp4|webm|mov|mkv|avi|mp3|wav|m4a|ogg|log)(?=(?::\d+(?::\d+)?)?(?:[\s)\],;.!?]|$))/giu;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const nodes = [];
  while (walker.nextNode()) {
    const node = walker.currentNode;
    if (!node.parentElement?.closest('a,code,pre,script,style,textarea')) nodes.push(node);
  }
  for (const node of nodes) {
    pattern.lastIndex = 0;
    const text = node.nodeValue || '';
    let match;
    let cursor = 0;
    let changed = false;
    const fragment = document.createDocumentFragment();
    while ((match = pattern.exec(text))) {
      const path = fileReference(match[0]);
      if (!path) continue;
      changed = true;
      fragment.append(document.createTextNode(text.slice(cursor, match.index)));
      const anchor = document.createElement('a');
      anchor.textContent = match[0];
      markRemoteFile(anchor, path);
      fragment.append(anchor);
      cursor = match.index + match[0].length;
    }
    if (changed) {
      fragment.append(document.createTextNode(text.slice(cursor)));
      node.replaceWith(fragment);
    }
  }
}

function markdown(text) {
  const source = String(text || '').replace(/</g, '&lt;');
  if (!window.marked) return esc(source).replace(/\n/g, '<br>');
  const template = document.createElement('template');
  template.innerHTML = window.marked.parse(source, { gfm: true, breaks: true });

  template.content.querySelectorAll('*').forEach(element => {
    for (const attribute of [...element.attributes]) {
      if (/^on/i.test(attribute.name) || attribute.name === 'style') element.removeAttribute(attribute.name);
    }

    if (element.hasAttribute('href')) {
      const rawHref = element.getAttribute('href');
      const path = fileReference(rawHref);
      if (path) {
        markRemoteFile(element, path);
        return;
      }
      try {
        const url = new URL(rawHref, location.href);
        if (!['http:', 'https:', 'mailto:'].includes(url.protocol)) {
          element.removeAttribute('href');
        } else if (isLocalDevelopmentUrl(url)) {
          element.dataset.localUrl = url.href;
          element.setAttribute('href', '#');
          element.classList.add('local-remote-link');
          element.removeAttribute('target');
          element.setAttribute('rel', 'noopener noreferrer');
          element.setAttribute('title', '通过远程电脑打开');
        } else {
          if (!isNativeShell) element.setAttribute('target', '_blank');
          else element.removeAttribute('target');
          element.setAttribute('rel', 'noopener noreferrer');
        }
      } catch {
        element.removeAttribute('href');
      }
    }

    if (element.tagName === 'IMG') {
      const rawSource = element.getAttribute('src');
      const path = fileReference(rawSource);
      if (path) {
        markRemoteFile(element, path, true);
        return;
      }
      try {
        const url = new URL(rawSource, location.href);
        if (!['http:', 'https:'].includes(url.protocol)) {
          element.replaceWith(document.createTextNode(element.getAttribute('alt') || '[图片]'));
        } else if (isLocalDevelopmentUrl(url)) {
          element.dataset.localUrl = url.href;
          element.removeAttribute('src');
          element.classList.add('local-image-pending');
          element.setAttribute('aria-busy', 'true');
        } else {
          element.setAttribute('loading', 'lazy');
        }
      } catch {
        element.remove();
      }
    }
  });
  linkifyFileText(template.content);
  return template.innerHTML;
}

async function resolveLocalLink(rawUrl) {
  if (['localhost', '127.0.0.1', '::1', '[::1]'].includes(location.hostname.toLowerCase())) return rawUrl;
  const cached = localResolutionCache.get(rawUrl);
  if (cached && cached.expiresAt > Date.now() + 30000) return cached.url;
  const result = await api('/local-links/resolve', {
    method: 'POST', body: { url: rawUrl }, suppressDiagnostic: true
  });
  const expiresAt = Date.parse(result.expiresAt || '') || Date.now() + 8 * 60 * 1000;
  localResolutionCache.set(rawUrl, { url: result.url, expiresAt });
  return result.url;
}

async function prepareLocalResources(root) {
  if (!root) return;
  const anchors = [...root.querySelectorAll('a[data-local-url]:not([data-resolving])')];
  const images = [...root.querySelectorAll('img[data-local-url]:not([data-resolving])')];
  await Promise.all([
    ...anchors.map(async anchor => {
      anchor.dataset.resolving = 'true';
      anchor.classList.add('local-link-loading');
      try {
        anchor.href = await resolveLocalLink(anchor.dataset.localUrl);
        anchor.classList.remove('local-link-loading', 'local-link-error');
      } catch {
        anchor.href = '#';
        anchor.classList.remove('local-link-loading');
        anchor.classList.add('local-link-error');
        anchor.title = '远程服务暂时无法访问，点击重试';
      } finally {
        delete anchor.dataset.resolving;
      }
    }),
    ...images.map(async image => {
    image.dataset.resolving = 'true';
    try {
      image.src = await resolveLocalLink(image.dataset.localUrl);
      image.loading = 'lazy';
      image.classList.remove('local-image-pending');
      image.removeAttribute('aria-busy');
    } catch (error) {
      const replacement = document.createElement('span');
      replacement.className = 'local-resource-error';
      replacement.textContent = image.alt ? `图片无法打开：${image.alt}` : '远程图片暂时无法打开';
      image.replaceWith(replacement);
    }
    })
  ]);
}

function formatBytes(value) {
  const bytes = Number(value);
  if (!Number.isFinite(bytes) || bytes < 0) return '';
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB'];
  let size = bytes / 1024;
  let unit = units[0];
  for (let index = 1; size >= 1024 && index < units.length; index += 1) {
    size /= 1024;
    unit = units[index];
  }
  return `${size >= 10 ? size.toFixed(0) : size.toFixed(1)} ${unit}`;
}

function artifactKind(file) {
  const mime = String(file.mime || file.contentType || '').toLowerCase();
  const extension = String(file.name || file.path || '').split('.').pop()?.toLowerCase();
  if (mime.startsWith('image/') || ['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'bmp', 'heic'].includes(extension)) return 'image';
  if (mime.startsWith('video/') || ['mp4', 'webm', 'mov', 'mkv', 'avi'].includes(extension)) return 'video';
  if (extension === 'apk' || extension === 'aab') return 'android';
  if (extension === 'pdf') return 'pdf';
  if (['zip', '7z', 'rar'].includes(extension)) return 'archive';
  return file.kind || 'file';
}

function artifactHint(file) {
  switch (artifactKind(file)) {
    case 'android': return '下载到手机后，在文件管理中打开安装；如系统提示，请仅为当前文件来源授予安装权限。';
    case 'pdf': return '可以直接预览，也可以下载后交给其他应用打开。';
    case 'archive': return '下载后解压；不要直接运行来源不明的压缩包内容。';
    case 'image': return '点击图片可查看原图，也可以保存到手机。';
    case 'video': return '可在线播放；网络不稳定时建议先下载。';
    default: return '可直接打开查看，或下载到手机后交给相应应用处理。';
  }
}

async function resolveRemoteFile(path, threadId = currentThread) {
  const key = `${threadId || ''}\n${path}`;
  const cached = remoteFileCache.get(key);
  if (cached && cached.expiresAt > Date.now() + 30000) return cached.file;
  const result = await api('/files/register', { method: 'POST', body: { path, threadId: threadId || null } });
  const file = result?.file || result || {};
  file.name ||= path.split(/[\\/]/).filter(Boolean).pop() || '交付文件';
  file.path ||= path;
  file.viewUrl ||= file.url || file.contentUrl || file.downloadUrl;
  file.downloadUrl ||= file.url || file.viewUrl;
  const expiresAt = Date.parse(file.expiresAt || result?.expiresAt || '') || Date.now() + 8 * 60 * 1000;
  remoteFileCache.set(key, { file, expiresAt });
  return file;
}

function artifactCard(file, label, includePreview = false) {
  const card = document.createElement('span');
  card.className = 'artifact-card';
  const heading = document.createElement('span');
  heading.className = 'artifact-heading';
  const icon = document.createElement('span');
  icon.className = 'artifact-icon';
  icon.textContent = ({ android: 'APK', pdf: 'PDF', archive: 'ZIP', image: 'IMG', video: 'VID' })[artifactKind(file)] || 'FILE';
  const copy = document.createElement('span');
  copy.className = 'artifact-copy';
  const title = document.createElement('b');
  title.textContent = file.name || label || '交付文件';
  title.title = file.path || file.name || '';
  const meta = document.createElement('small');
  const metaParts = [formatBytes(file.size), file.mime || file.contentType].filter(Boolean);
  meta.textContent = metaParts.join(' · ') || '来自远程电脑';
  copy.append(title, meta);
  heading.append(icon, copy);
  card.append(heading);

  const kind = artifactKind(file);
  if ((includePreview || kind === 'image' || kind === 'video') && file.viewUrl) {
    if (kind === 'image') {
      const image = document.createElement('img');
      image.className = 'artifact-preview-media';
      image.src = file.viewUrl;
      image.alt = label || file.name || '交付图片';
      image.loading = 'lazy';
      card.append(image);
    } else if (kind === 'video') {
      const video = document.createElement('video');
      video.className = 'artifact-preview-media';
      video.src = file.viewUrl;
      video.controls = true;
      video.preload = 'metadata';
      card.append(video);
    }
  }

  const hint = document.createElement('small');
  hint.className = 'artifact-hint';
  hint.textContent = artifactHint(file);
  card.append(hint);
  const actions = document.createElement('span');
  actions.className = 'artifact-actions';
  if (file.viewUrl && !['android', 'archive'].includes(kind)) {
    const open = document.createElement('a');
    open.className = 'artifact-open';
    open.href = file.viewUrl;
    open.textContent = '打开';
    open.rel = 'noopener noreferrer';
    if (!isNativeShell) open.target = '_blank';
    actions.append(open);
  }
  if (file.downloadUrl) {
    const download = document.createElement('a');
    download.className = 'artifact-download';
    download.href = file.downloadUrl;
    download.download = file.name || '';
    download.textContent = kind === 'android' ? '下载 APK' : '下载';
    actions.append(download);
  }
  card.append(actions);
  return card;
}

async function prepareRemoteFiles(root) {
  if (!root) return;
  const elements = [...root.querySelectorAll('[data-file-path]:not([data-file-resolving])')];
  await Promise.all(elements.map(async element => {
    element.dataset.fileResolving = 'true';
    const path = element.dataset.filePath;
    const threadId = element.dataset.fileThread || currentThread;
    try {
      const file = await resolveRemoteFile(path, threadId);
      const imageElement = element.tagName === 'IMG';
      element.replaceWith(artifactCard(file, imageElement ? element.alt : element.textContent, imageElement));
    } catch (error) {
      const replacement = document.createElement('span');
      replacement.className = 'artifact-unavailable';
      replacement.textContent = `${path.split(/[\\/]/).pop() || '文件'} 暂时无法从远程电脑读取`;
      replacement.title = error.message || '';
      element.replaceWith(replacement);
    }
  }));
}

async function prepareRichResources(root) {
  await Promise.all([prepareLocalResources(root), prepareRemoteFiles(root)]);
}

document.addEventListener('click', async event => {
  const anchor = event.target.closest?.('a[data-local-url]');
  if (!anchor) return;
  event.preventDefault();
  if (anchor.dataset.opening === 'true') return;
  anchor.dataset.opening = 'true';
  const original = anchor.textContent;
  anchor.setAttribute('aria-busy', 'true');
  toast('正在连接远程电脑上的服务');
  try {
    const mapped = await resolveLocalLink(anchor.dataset.localUrl);
    anchor.href = mapped;
    anchor.dataset.opening = 'false';
    location.assign(mapped);
  } catch (error) {
    toast(error.message || '远程服务无法访问');
    anchor.textContent = original;
    anchor.dataset.opening = 'false';
    anchor.removeAttribute('aria-busy');
  }
});

function toolLabel(type) {
  if (/command|exec|shell|terminal/i.test(type)) return '运行命令';
  if (/fileChange|patch|edit/i.test(type)) return '编辑文件';
  if (/image|screenshot|viewImage/i.test(type)) return '查看图片';
  if (/search|web/i.test(type)) return '搜索网络';
  if (/computer|browser/i.test(type)) return '操作界面';
  if (/agent|collab/i.test(type)) return '调用子 Agent';
  return '调用工具';
}

function userResourceMarkup(item) {
  const resources = [];
  for (const part of item?.content || []) {
    if (part?.type === 'localImage' || part?.type === 'mention') {
      const path = part.path || '';
      if (!path) continue;
      const name = part.name || path.split(/[\\/]/).filter(Boolean).pop() || '附件';
      resources.push(`<a class="remote-file-link" href="#" data-file-path="${esc(path)}" data-file-thread="${esc(currentThread)}">${esc(name)}</a>`);
    } else if (part?.type === 'image' && part.url) {
      resources.push(markdown(`![已发送图片](${part.url})`));
    } else if (part?.type === 'skill') {
      resources.push(`<span class="message-skill">技能：${esc(part.name || '已选择')}</span>`);
    }
  }
  return resources.length ? `<div class="message-resources">${resources.join('')}</div>` : '';
}

function presentItem(item) {
  const parsed = parsedPayload(item);
  const type = String(parsed?.type || item?.type || '');
  if (type === 'userMessage') return { kind: 'message', role: 'user', text: itemText(parsed) || itemText(item), resources: userResourceMarkup(parsed) };
  if (type === 'agentMessage') {
    const text = itemText(parsed) || itemText(item);
    const nested = parsedPayload({ text });
    if (nested !== parsed && nested?.type && nested.type !== 'agentMessage') return presentItem(nested);
    return { kind: 'message', role: 'assistant', text };
  }
  if (type === 'reasoning') return { kind: 'reasoning', text: plainSummary(parsed.summary) || '正在分析' };
  return { kind: 'tool', label: toolLabel(type) };
}

function renderTurn(turn) {
  const blocks = [];
  const pendingTools = [];
  const flushTools = () => {
    if (!pendingTools.length) return;
    const counts = new Map();
    for (const item of pendingTools) counts.set(item.label, (counts.get(item.label) || 0) + 1);
    blocks.push(`<div class="activity">${[...counts].map(([label, count]) =>
      `<span>${esc(label)}${count > 1 ? ` × ${count}` : ''}</span>`).join('')}</div>`);
    pendingTools.length = 0;
  };

  for (const item of turn.items || []) {
    const view = presentItem(item);
    if (view.kind === 'tool') {
      pendingTools.push(view);
      continue;
    }
    flushTools();
    if (view.kind === 'reasoning') {
      blocks.push(`<div class="reasoning-note markdown-body">${markdown(view.text)}</div>`);
    } else if (view.text.trim() || view.resources) {
      blocks.push(`<div class="turn ${view.role} markdown-body">${markdown(view.text)}${view.resources || ''}</div>`);
    }
  }
  flushTools();
  return blocks.join('');
}

function renderThreadHistory(preserve = false) {
  const scrolling = document.scrollingElement || document.documentElement;
  const oldHeight = scrolling.scrollHeight;
  const oldTop = scrolling.scrollTop;
  const earlier = hasEarlierTurns
    ? '<button class="load-older" type="button" onclick="loadOlder()">加载更早消息</button>'
    : '';
  const messages = currentTurns.map(renderTurn).join('');
  $('#turns').innerHTML = earlier + (messages || empty('任务还没有可显示的对话'));
  prepareRichResources($('#turns'));
  requestAnimationFrame(() => requestAnimationFrame(() => {
    if (preserve) scrolling.scrollTop = oldTop + (scrolling.scrollHeight - oldHeight);
    else scrolling.scrollTop = scrolling.scrollHeight;
  }));
}

async function loadOlder() {
  if (!hasEarlierTurns || loadingOlder || !currentThread || !currentTurnCursor) return;
  loadingOlder = true;
  const threadId = currentThread;
  const pagination = `paged=true&cursor=${encodeURIComponent(currentTurnCursor)}`;
  const button = document.querySelector('.load-older');
  if (button) {
    button.disabled = true;
    button.textContent = '加载中';
  }
  try {
    const result = await api(`/threads/${encodeURIComponent(threadId)}?${pagination}&limit=${turnPageSize}`);
    if (threadId !== currentThread) return;
    const thread = result.thread || result;
    currentTurns = [...(thread.turns || []), ...currentTurns];
    currentTurnCursor = result.nextCursor || '';
    hasEarlierTurns = Boolean(result.hasEarlier);
    currentThreadSignature = JSON.stringify(currentTurns);
    renderThreadHistory(true);
  } catch (error) {
    toast(error.message);
  } finally {
    loadingOlder = false;
  }
}

function pendingRuntimeState(threadId) {
  const requests = pendingRequests.filter(request => String(request.params?.threadId || '') === threadId);
  if (!requests.length) return null;
  const input = requests.some(isInputPendingRequest);
  const approval = requests.some(request => isResolvableApprovalRequest(request) || isToolApprovalElicitation(request));
  const phase = input && approval ? 'waitingAction' : input ? 'waitingInput' : 'waitingApproval';
  const latest = requests.reduce((selected, request) => {
    const selectedAt = CodexThreadStatus.timestampMs(selected?.createdAt);
    const requestAt = CodexThreadStatus.timestampMs(request?.createdAt);
    return !selected || Number.isFinite(requestAt) && (!Number.isFinite(selectedAt) || requestAt > selectedAt)
      ? request
      : selected;
  }, null);
  return { phase, observedAt: latest?.createdAt || null };
}

function normalizedRuntimeState(thread) {
  const id = String(thread?.id || '');
  const live = runtimeStates[id] || null;
  return CodexThreadStatus.normalizeThreadRuntime({
    thread,
    runtime: live,
    pending: pendingRuntimeState(id)
  });
}

function statusLabel(status) {
  return CodexThreadStatus.statusLabel(status);
}

function statusTitle(status) {
  const base = CodexThreadStatus.statusTitle(status);
  const observedAt = CodexThreadStatus.timestampMs(status?.observedAt);
  return Number.isFinite(observedAt) ? `${base}；状态记录更新于 ${new Date(observedAt).toLocaleString()}` : base;
}

function statusMeta(status, thread) {
  const source = CodexThreadStatus.statusSourceLabel(status);
  const observedAt = CodexThreadStatus.timestampMs(status?.observedAt);
  if (Number.isFinite(observedAt)) {
    const prefix = status?.stale ? '记录更新' : '状态更新';
    return `${prefix}：${new Date(observedAt).toLocaleString()} · ${source}`;
  }
  if (thread?.updatedAt) return `任务更新：${when(thread.updatedAt)} · ${source}`;
  return source;
}

function threadCard(thread) {
  const title = thread.name || thread.preview || '未命名任务';
  const cwd = thread.cwd || '';
  const runtime = normalizedRuntimeState(thread);
  return `<article class="card task-card" onclick="openThread('${esc(thread.id)}')">
    <div class="cardTop"><div><h3 title="${esc(title)}">${esc(title)}</h3><p title="${esc(cwd)}">${esc(cwd)}</p></div>
    <span class="badge" title="${esc(statusTitle(runtime))}">${esc(statusLabel(runtime))}</span></div><p class="task-time" title="${esc(statusTitle(runtime))}">${esc(statusMeta(runtime, thread))}</p></article>`;
}

function projectCard(project) {
  return `<article class="card project-card"><div class="cardTop"><div><h3>${esc(project.name)}</h3><p>${esc(project.path)}</p></div>
    <span class="badge">${esc(project.kind)}</span></div><div class="actions">
    <button onclick='event.stopPropagation();startThread(${JSON.stringify(project.path)})'>在此项目新建任务</button></div></article>`;
}

function processCard(process) {
  return `<article class="card process-card"><div><h3>${esc(process.name)} <small>PID ${process.pid}</small></h3>
    <p>${process.memoryMb} MB · ${when(process.startedAt)}</p></div>
    <button class="danger" onclick="stopProcess(${process.pid})">停止</button></article>`;
}

function isUserInputRequest(request) {
  return String(request?.method || '').toLowerCase().includes('requestuserinput');
}

function isMcpElicitationRequest(request) {
  return String(request?.method || '').toLowerCase() === 'mcpserver/elicitation/request';
}

function isInputPendingRequest(request) {
  const method = String(request?.method || '').toLowerCase();
  return isUserInputRequest(request) || (method === 'mcpserver/elicitation/request' && !isToolApprovalElicitation(request));
}

function isResolvableApprovalRequest(request) {
  return new Set([
    'item/commandexecution/requestapproval',
    'item/filechange/requestapproval',
    'item/permissions/requestapproval',
    'applypatchapproval',
    'execcommandapproval'
  ]).has(String(request?.method || '').toLowerCase());
}

function approvalTitle(request) {
  const method = String(request.method || '');
  if (/command|exec|shell/i.test(method)) return '允许 Codex 运行命令？';
  if (/file|patch|edit|change/i.test(method)) return '允许 Codex 修改文件？';
  if (/permission/i.test(method)) return '允许 Codex 使用这些权限？';
  if (/computer|browser/i.test(method)) return '允许 Codex 操作界面？';
  return 'Codex 正在等待你的确认';
}

function firstText(object, keys) {
  if (!object || typeof object !== 'object') return '';
  for (const key of keys) {
    const value = object[key];
    if (typeof value === 'string' && value.trim()) return value.trim();
    if (Array.isArray(value) && value.every(item => typeof item === 'string')) return value.join(' ');
  }
  for (const value of Object.values(object)) {
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      const nested = firstText(value, keys);
      if (nested) return nested;
    }
  }
  return '';
}

function approvalDetails(request) {
  const params = request.params || {};
  const reason = firstText(params, ['reason', 'justification', 'message', 'description']);
  const command = firstText(params, ['command', 'cmd', 'script']);
  const path = firstText(params, ['path', 'filePath', 'cwd']);
  const permissions = Array.isArray(params.permissions)
    ? params.permissions.map(item => typeof item === 'string' ? item : firstText(item, ['name', 'permission', 'description'])).filter(Boolean)
    : [];
  const lines = [];
  if (reason) lines.push(`<p>${esc(reason.slice(0, 320))}</p>`);
  if (command) lines.push(`<code class="approval-command">${esc(command.slice(0, 500))}</code>`);
  if (permissions.length) lines.push(`<code class="approval-command">${esc(permissions.slice(0, 8).join('\n'))}</code>`);
  if (path && path !== command) lines.push(`<small title="${esc(path)}">${esc(path)}</small>`);
  return lines.join('') || '<p>请在继续前确认这项操作。</p>';
}

function approvalCard(request, instance = 'list') {
  if (isUserInputRequest(request)) return questionCard(request, instance);
  if (isMcpElicitationRequest(request)) return elicitationCard(request, instance);
  if (!isResolvableApprovalRequest(request)) return genericPendingCard(request, instance);
  return `<article class="card approval-card" data-approval-key="${esc(request.key)}"><div class="question-heading"><span class="badge">等待批准</span><h3>${esc(approvalTitle(request))}</h3></div>${approvalDetails(request)}
    <div class="actions"><button onclick="approval('${esc(request.key)}','accept')">允许一次</button>
    <button onclick="approval('${esc(request.key)}','acceptForSession')">本次任务允许</button>
    <button class="danger" onclick="approval('${esc(request.key)}','decline')">拒绝</button></div></article>`;
}

function elicitationParams(request) {
  return request?.params && typeof request.params === 'object' ? request.params : request || {};
}

function elicitationMeta(request) {
  const params = elicitationParams(request);
  const meta = params._meta || params.meta || request?._meta || request?.meta;
  return meta && typeof meta === 'object' && !Array.isArray(meta) ? meta : {};
}

function isToolApprovalElicitation(request) {
  const params = elicitationParams(request);
  const meta = params._meta || request?._meta;
  if (!meta || typeof meta !== 'object' || Array.isArray(meta)) return false;
  return meta.codex_approval_kind === 'mcp_tool_call';
}

function normalizedSchema(value) {
  if (value && typeof value === 'object' && !Array.isArray(value)) return value;
  if (typeof value !== 'string' || !value.trim()) return {};
  try {
    const parsed = JSON.parse(value);
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

function schemaOptions(schema) {
  const direct = Array.isArray(schema?.enum) ? schema.enum : null;
  const directNames = Array.isArray(schema?.enumNames) ? schema.enumNames : [];
  if (direct) return direct.map((value, index) => ({ value, label: directNames[index] ?? String(value) }));
  const variants = Array.isArray(schema?.oneOf) ? schema.oneOf : Array.isArray(schema?.anyOf) ? schema.anyOf : null;
  if (!variants || !variants.every(option => option && Object.prototype.hasOwnProperty.call(option, 'const'))) return [];
  return variants.map(option => ({ value: option.const, label: option.title || String(option.const) }));
}

function schemaType(schema) {
  if (typeof schema?.type === 'string') return schema.type;
  if (Array.isArray(schema?.type)) return schema.type.find(type => type !== 'null') || schema.type[0];
  if (schema?.properties && typeof schema.properties === 'object') return 'object';
  const options = schemaOptions(schema);
  if (options.length) {
    const valueType = typeof options[0].value;
    return ['string', 'number', 'boolean'].includes(valueType) ? valueType : '';
  }
  return '';
}

function schemaPath(path) {
  return esc(JSON.stringify(path));
}

function optionValue(value) {
  return esc(JSON.stringify(value));
}

function inputTypeForSchema(name, schema) {
  const format = String(schema?.format || '').toLowerCase();
  if (schema?.writeOnly || schema?.secret || schema?.isSecret || format === 'password' || /(?:password|secret|token|api.?key)/i.test(name)) return 'password';
  if (format === 'email') return 'email';
  if (format === 'uri' || format === 'url') return 'url';
  if (format === 'date') return 'date';
  if (format === 'date-time') return 'datetime-local';
  if (format === 'time') return 'time';
  return 'text';
}

function jsonDefault(schema, type) {
  if (Object.prototype.hasOwnProperty.call(schema || {}, 'default')) return JSON.stringify(schema.default, null, 2);
  if (type === 'array') return '[]';
  if (type === 'object') return '{}';
  return '';
}

function renderSchemaField(name, schemaValue, required, path, instance, depth = 0) {
  const schema = normalizedSchema(schemaValue);
  const type = schemaType(schema);
  const title = schema.title || name || '内容';
  const description = schema.description || '';
  const id = `e-${instance}-${path.join('-') || 'root'}`.replace(/[^a-zA-Z0-9_-]/g, '-');
  const requiredMark = required ? '<span class="required-mark">必填</span>' : '<span class="optional-mark">可选</span>';
  const legend = `<legend>${esc(title)} ${requiredMark}</legend>${description ? `<p>${esc(description)}</p>` : ''}`;

  if (type === 'object' && schema.properties && depth < 4) {
    const requiredKeys = new Set(Array.isArray(schema.required) ? schema.required : []);
    const children = Object.entries(schema.properties).map(([childName, childSchema]) =>
      renderSchemaField(childName, childSchema, required && requiredKeys.has(childName), [...path, childName], instance, depth + 1)).join('');
    return `<fieldset class="question-field elicitation-object" data-elicit-object-path="${schemaPath(path)}" data-elicit-object-required="${required}">${legend}${children || `<textarea class="elicitation-json" data-elicit-kind="json" data-elicit-json-type="object" data-elicit-path="${schemaPath(path)}" data-elicit-required="${required}" rows="4" placeholder="请输入 JSON 对象">${esc(jsonDefault(schema, 'object'))}</textarea>`}</fieldset>`;
  }

  const options = schemaOptions(schema);
  if (type === 'array' && schema?.items && schemaOptions(schema.items).length) {
    const itemOptions = schemaOptions(schema.items);
    const defaults = Array.isArray(schema.default) ? schema.default : [];
    const minItems = Number.isInteger(Number(schema.minItems)) && Number(schema.minItems) >= 0 ? Number(schema.minItems) : null;
    const maxItems = Number.isInteger(Number(schema.maxItems)) && Number(schema.maxItems) >= 0 ? Number(schema.maxItems) : null;
    const constraints = [minItems !== null ? `至少选择 ${minItems} 项` : '', maxItems !== null ? `最多选择 ${maxItems} 项` : ''].filter(Boolean).join('，');
    const choices = itemOptions.map(option => `<label class="question-option"><input type="checkbox" data-elicit-kind="multi" data-elicit-path="${schemaPath(path)}" data-elicit-required="${required}" data-elicit-min-items="${minItems ?? ''}" data-elicit-max-items="${maxItems ?? ''}" value="${optionValue(option.value)}" ${defaults.some(value => JSON.stringify(value) === JSON.stringify(option.value)) ? 'checked' : ''}><span><b>${esc(option.label)}</b></span></label>`).join('');
    return `<fieldset class="question-field">${legend}<div class="question-options">${choices}</div>${constraints ? `<small>${esc(constraints)}</small>` : ''}</fieldset>`;
  }

  if (type === 'array') {
    const minItems = Number.isInteger(Number(schema.minItems)) && Number(schema.minItems) >= 0 ? Number(schema.minItems) : '';
    const maxItems = Number.isInteger(Number(schema.maxItems)) && Number(schema.maxItems) >= 0 ? Number(schema.maxItems) : '';
    return `<fieldset class="question-field elicitation-json-field">${legend}<textarea class="elicitation-json" data-elicit-kind="json" data-elicit-json-type="array" data-elicit-min-items="${minItems}" data-elicit-max-items="${maxItems}" data-elicit-path="${schemaPath(path)}" data-elicit-required="${required}" rows="4" ${required ? 'required' : ''} placeholder="请输入 JSON 数组">${esc(jsonDefault(schema, 'array'))}</textarea><small>使用 JSON 数组格式，例如 ["第一项", "第二项"]。${minItems !== '' ? `至少 ${minItems} 项。` : ''}${maxItems !== '' ? `最多 ${maxItems} 项。` : ''}</small></fieldset>`;
  }

  if (options.length) {
    const defaultValue = Object.prototype.hasOwnProperty.call(schema, 'default') ? JSON.stringify(schema.default) : '';
    const choices = options.map(option => `<option value="${optionValue(option.value)}" ${JSON.stringify(option.value) === defaultValue ? 'selected' : ''}>${esc(option.label)}</option>`).join('');
    return `<fieldset class="question-field">${legend}<select id="${esc(id)}" data-elicit-kind="enum" data-elicit-path="${schemaPath(path)}" data-elicit-required="${required}" ${required ? 'required' : ''}><option value="">请选择</option>${choices}</select></fieldset>`;
  }

  if (type === 'boolean') {
    const checked = schema.default === true ? 'checked' : '';
    return `<fieldset class="question-field">${legend}<label class="question-option boolean-option"><input id="${esc(id)}" type="checkbox" data-elicit-kind="boolean" data-elicit-path="${schemaPath(path)}" data-elicit-required="${required}" ${checked}><span><b>${esc(schema.checkboxLabel || title)}</b><small>点击切换</small></span></label></fieldset>`;
  }

  if (type === 'number' || type === 'integer') {
    const value = schema.default !== null && schema.default !== '' && Number.isFinite(Number(schema.default)) ? String(schema.default) : '';
    const min = Number.isFinite(Number(schema.minimum)) ? ` min="${esc(schema.minimum)}"` : '';
    const max = Number.isFinite(Number(schema.maximum)) ? ` max="${esc(schema.maximum)}"` : '';
    return `<fieldset class="question-field">${legend}<input id="${esc(id)}" type="number" data-elicit-kind="${type}" data-elicit-path="${schemaPath(path)}" data-elicit-required="${required}" value="${esc(value)}" step="${type === 'integer' ? '1' : 'any'}"${min}${max} ${required ? 'required' : ''}></fieldset>`;
  }

  if (type === 'string' || type === 'secret') {
    const inputType = type === 'secret' ? 'password' : inputTypeForSchema(name, schema);
    const value = schema.default == null ? '' : String(schema.default);
    const min = Number.isFinite(Number(schema.minLength)) ? ` minlength="${esc(schema.minLength)}"` : '';
    const max = Number.isFinite(Number(schema.maxLength)) ? ` maxlength="${esc(schema.maxLength)}"` : '';
    const pattern = typeof schema.pattern === 'string' ? ` pattern="${esc(schema.pattern)}"` : '';
    const common = `data-elicit-kind="string" data-elicit-path="${schemaPath(path)}" data-elicit-required="${required}" ${required ? 'required' : ''}${min}${max}${pattern}`;
    const control = schema.multiline || Number(schema.maxLength) > 240
      ? `<textarea id="${esc(id)}" ${common} rows="3">${esc(value)}</textarea>`
      : `<input id="${esc(id)}" type="${inputType}" ${common} value="${esc(value)}" autocomplete="${inputType === 'password' ? 'off' : 'on'}">`;
    return `<fieldset class="question-field">${legend}${control}</fieldset>`;
  }

  const fallback = jsonDefault(schema, type);
  const expectedType = type === 'object' ? ' data-elicit-json-type="object"' : '';
  return `<fieldset class="question-field elicitation-json-field">${legend}<textarea class="elicitation-json" data-elicit-kind="json"${expectedType} data-elicit-path="${schemaPath(path)}" data-elicit-required="${required}" rows="4" ${required ? 'required' : ''} placeholder="请输入有效的 JSON">${esc(fallback)}</textarea><small>此字段使用通用 JSON 输入。</small></fieldset>`;
}

function toolApprovalDetails(request) {
  const meta = elicitationMeta(request);
  const params = elicitationParams(request);
  const rows = Array.isArray(meta.tool_params_display) ? meta.tool_params_display : [];
  const details = rows.map(row => `<div><dt>${esc(row.display_name || row.name || '参数')}</dt><dd>${esc(row.value ?? '')}</dd></div>`).join('');
  const risk = String(meta.riskLevel || '').toLowerCase();
  const riskLabel = risk ? `<span class="elicitation-risk ${risk === 'high' ? 'high' : ''}">${risk === 'high' ? '高风险' : '需要授权'}</span>` : '';
  return `${riskLabel}<p>${esc(params.message || '工具需要你的允许才能继续。')}</p>${meta.subtitle ? `<p class="question-context">${esc(meta.subtitle)}</p>` : ''}${details ? `<dl class="elicitation-details">${details}</dl>` : ''}`;
}

function toolApprovalCard(request) {
  const meta = elicitationMeta(request);
  const persistence = new Set(Array.isArray(meta.persist) ? meta.persist : typeof meta.persist === 'string' ? [meta.persist] : []);
  const connector = meta.connector_name || (String(meta.connector_id || '').toLowerCase() === 'computer-use' ? 'Computer Use' : '工具');
  const session = persistence.has('session') ? `<button type="button" onclick="submitElicitationAction(event,'${esc(request.key)}','accept','session')">本次任务允许</button>` : '';
  const always = persistence.has('always') ? `<button type="button" onclick="submitElicitationAction(event,'${esc(request.key)}','accept','always')">始终允许</button>` : '';
  const manualFields = toolApprovalManualFields(request);
  return `<form class="card approval-card tool-approval-card" data-pending-key="${esc(request.key)}" onsubmit="event.preventDefault()"><div class="question-heading"><span class="badge">工具授权</span><h3>允许 ${esc(connector)} 继续？</h3></div>${toolApprovalDetails(request)}${manualFields ? `<div class="tool-approval-extra"><p>此工具还需要以下内容：</p>${manualFields}</div>` : ''}<div class="actions elicitation-actions"><button type="button" onclick="submitElicitationAction(event,'${esc(request.key)}','accept')">允许一次</button>${session}${always}<button type="button" class="danger" onclick="submitElicitationAction(event,'${esc(request.key)}','decline')">拒绝</button></div></form>`;
}

function safeElicitationUrl(value) {
  try {
    const url = new URL(String(value || ''));
    return ['http:', 'https:'].includes(url.protocol) ? url : null;
  } catch {
    return null;
  }
}

function urlElicitationCard(request) {
  const params = elicitationParams(request);
  const url = safeElicitationUrl(params.url);
  const local = url && isLocalDevelopmentUrl(url);
  const link = url
    ? `<a class="elicitation-link" href="${esc(url.href)}" ${local ? `data-local-url="${esc(url.href)}"` : ''} ${isNativeShell ? '' : 'target="_blank"'} rel="noopener noreferrer">打开验证页面</a>`
    : '<p class="elicitation-invalid">请求没有提供可打开的网页地址。</p>';
  return `<article class="card approval-card url-elicitation-card" data-pending-key="${esc(request.key)}"><div class="question-heading"><span class="badge">网页确认</span><h3>${esc(params.serverName || '工具需要网页操作')}</h3></div><p>${esc(params.message || '请打开页面完成操作，再选择下一步。')}</p>${link}<div class="actions elicitation-actions"><button type="button" onclick="submitElicitationAction(event,'${esc(request.key)}','accept')">已完成，继续</button><button type="button" class="secondary" onclick="submitElicitationAction(event,'${esc(request.key)}','decline')">拒绝</button><button type="button" class="danger" onclick="submitElicitationAction(event,'${esc(request.key)}','cancel')">取消任务</button></div></article>`;
}

function formElicitationCard(request, instance) {
  const params = elicitationParams(request);
  const schema = normalizedSchema(params.requestedSchema || params.schema);
  const type = schemaType(schema);
  const required = new Set(Array.isArray(schema.required) ? schema.required : []);
  let fields = '';
  if (type === 'object' || schema.properties) {
    fields = Object.entries(schema.properties || {}).map(([name, field]) =>
      renderSchemaField(name, field, required.has(name), [name], `${request.key}-${instance}`)).join('');
  } else if (Object.keys(schema).length) {
    fields = renderSchemaField('内容', schema, true, [], `${request.key}-${instance}`);
  }
  const rawFallback = fields
    ? '<details class="elicitation-advanced"><summary>高级：直接编辑 JSON</summary><textarea data-elicit-raw rows="5" placeholder="填写后将覆盖上面的字段"></textarea><small>仅在普通字段无法表达请求时使用。</small></details>'
    : `<div class="elicitation-json-fallback"><p>请填写工具需要的结构化内容。</p><textarea data-elicit-raw rows="6" required placeholder="请输入有效的 JSON">${esc(jsonDefault(schema, type || 'object'))}</textarea></div>`;
  return `<form class="card question-card elicitation-card" data-pending-key="${esc(request.key)}" onsubmit="submitElicitation(event,'${esc(request.key)}')"><div class="question-heading"><span class="badge">需要填写</span><h3>${esc(params.serverName || '工具请求信息')}</h3></div><p class="question-context">${esc(params.message || '填写后会直接交给正在运行的工具。')}</p>${fields}${rawFallback}<div class="question-submit elicitation-submit"><small>内容只用于完成当前请求。</small><button type="submit">提交并继续</button></div><div class="actions elicitation-secondary-actions"><button type="button" class="secondary" onclick="submitElicitationAction(event,'${esc(request.key)}','decline')">拒绝</button><button type="button" class="danger" onclick="submitElicitationAction(event,'${esc(request.key)}','cancel')">取消</button></div></form>`;
}

function elicitationCard(request, instance = 'list') {
  if (isToolApprovalElicitation(request)) return toolApprovalCard(request);
  const mode = String(elicitationParams(request).mode || 'form').toLowerCase();
  if (mode === 'url') return urlElicitationCard(request);
  return formElicitationCard(request, instance);
}

function genericPendingCard(request, instance = 'list') {
  const params = elicitationParams(request);
  const reason = firstText(params, ['message', 'reason', 'description']) || 'Codex 需要一项补充响应才能继续。';
  return `<form class="card question-card elicitation-card generic-pending-card" data-pending-key="${esc(request.key)}" onsubmit="submitElicitation(event,'${esc(request.key)}')"><div class="question-heading"><span class="badge">需要响应</span><h3>${esc(approvalTitle(request))}</h3></div><p class="question-context">${esc(reason)}</p><label class="question-title" for="generic-${esc(instance)}-${esc(request.key)}">响应内容 <small>可以输入文字，或填写 JSON</small></label><textarea id="generic-${esc(instance)}-${esc(request.key)}" data-elicit-generic rows="3" placeholder="输入回复；留空表示直接继续"></textarea><div class="actions elicitation-actions"><button type="submit">继续</button><button type="button" class="secondary" onclick="submitElicitationAction(event,'${esc(request.key)}','decline')">拒绝</button><button type="button" class="danger" onclick="submitElicitationAction(event,'${esc(request.key)}','cancel')">取消</button></div></form>`;
}

function questionCard(request, instance = 'list') {
  const params = request.params || {};
  const questions = Array.isArray(params.questions) ? params.questions : [];
  const fields = questions.map((question, index) => {
    const fieldId = `q-${request.key}-${instance}-${index}`.replace(/[^a-zA-Z0-9_-]/g, '-');
    const name = `${fieldId}-choice`;
    const options = Array.isArray(question.options) ? question.options : [];
    let control;
    if (options.length) {
      const optionHtml = options.map((option, optionIndex) => `<label class="question-option">
        <input type="radio" name="${esc(name)}" value="${esc(option.label)}" ${optionIndex === 0 ? 'required' : ''}>
        <span><b>${esc(option.label)}</b>${option.description ? `<small>${esc(option.description)}</small>` : ''}</span></label>`).join('');
      const hasOther = options.some(option => /^(other|其他)$/i.test(String(option.label || '').trim()));
      const otherHtml = hasOther ? '' : `<label class="question-option other-option"><input type="radio" name="${esc(name)}" value="__other__">
        <span><b>其他</b><small>输入一个不同的答案</small></span></label>
        <input class="other-answer hidden" data-other-for="${esc(name)}" aria-label="其他答案" placeholder="请输入你的答案">`;
      control = `<div class="question-options">${optionHtml}${otherHtml}</div>`;
    } else if (question.isSecret) {
      control = `<input class="question-free-answer" type="password" autocomplete="off" required placeholder="请输入答案（不会保存）">`;
    } else {
      control = '<textarea class="question-free-answer" rows="2" required placeholder="请输入你的回答"></textarea>';
    }
    return `<fieldset class="question-field" data-question-id="${esc(question.id)}"><legend>${esc(question.header || `问题 ${index + 1}`)}</legend>
      <p>${esc(question.question || '')}</p>${control}</fieldset>`;
  }).join('');
  const autoMs = Number(params.autoResolutionMs);
  const deadline = Number.isFinite(autoMs) && autoMs > 0 ? Date.parse(request.createdAt || '') + autoMs : 0;
  const countdown = deadline
    ? `<small class="pending-countdown" data-deadline="${deadline}">Codex 可能会自动继续</small>`
    : '<small>你的回答会直接交给正在运行的 Codex。</small>';
  return `<form class="card question-card" data-pending-key="${esc(request.key)}" onsubmit="submitAnswers(event,'${esc(request.key)}')">
    <div class="question-heading"><span class="badge">需要回答</span><h3>Codex 正在等你选择</h3></div>${fields || '<p>这项请求没有可显示的问题。</p>'}
    <div class="question-submit">${countdown}<button type="submit" ${fields ? '' : 'disabled'}>提交回答</button></div></form>`;
}

function updatePendingCountdowns() {
  document.querySelectorAll('.pending-countdown[data-deadline]').forEach(element => {
    const remaining = Math.max(0, Number(element.dataset.deadline) - Date.now());
    element.textContent = remaining > 0
      ? `约 ${Math.ceil(remaining / 1000)} 秒后 Codex 可能自动继续`
      : '正在同步最新状态';
  });
}

document.addEventListener('change', event => {
  const radio = event.target.closest?.('.question-option input[type="radio"]');
  if (!radio) return;
  const form = radio.closest('form');
  const other = form?.querySelector(`[data-other-for="${CSS.escape(radio.name)}"]`);
  if (!other) return;
  const useOther = radio.value === '__other__';
  other.classList.toggle('hidden', !useOther);
  other.required = useOther;
  if (useOther) other.focus();
});

async function submitAnswers(event, key) {
  event.preventDefault();
  const form = event.currentTarget;
  const submit = form.querySelector('button[type="submit"]');
  const answers = {};
  for (const field of form.querySelectorAll('.question-field')) {
    const id = field.dataset.questionId;
    const free = field.querySelector('.question-free-answer');
    let value = free?.value.trim() || '';
    if (!free) {
      const selected = field.querySelector('input[type="radio"]:checked');
      if (selected?.value === '__other__') value = field.querySelector('.other-answer')?.value.trim() || '';
      else value = selected?.value || '';
    }
    if (!value) {
      toast('请回答所有问题');
      field.querySelector('input:not([type="radio"]),textarea,input[type="radio"]')?.focus();
      return;
    }
    answers[id] = { answers: [value] };
  }
  submit.disabled = true;
  submit.textContent = '提交中';
  try {
    await api(`/pending/${encodeURIComponent(key)}/answers`, { method: 'POST', body: { answers } });
    toast('回答已提交给 Codex');
    await load();
    if (currentThread) await refreshCurrentThread(true);
  } catch (error) {
    toast(error.status === 404 ? '这项问题已在其他设备处理' : error.message);
    if (error.status === 404) await load();
    else {
      submit.disabled = false;
      submit.textContent = '提交回答';
    }
  }
}

function setPathValue(target, path, value) {
  if (!path.length) {
    target.value = value;
    return;
  }
  let cursor = target;
  path.forEach((segment, index) => {
    if (index === path.length - 1) cursor[segment] = value;
    else {
      if (!cursor[segment] || typeof cursor[segment] !== 'object' || Array.isArray(cursor[segment])) cursor[segment] = {};
      cursor = cursor[segment];
    }
  });
}

function elicitationPath(control) {
  try {
    const path = JSON.parse(control.dataset.elicitPath || '[]');
    return Array.isArray(path) ? path : [];
  } catch {
    return [];
  }
}

function parseElicitationValue(control) {
  const kind = control.dataset.elicitKind;
  if (kind === 'boolean') return control.checked;
  if (kind === 'number' || kind === 'integer') {
    if (control.value === '') return undefined;
    const value = Number(control.value);
    if (!Number.isFinite(value) || (kind === 'integer' && !Number.isInteger(value))) throw new Error('请输入有效的数字');
    return value;
  }
  if (kind === 'enum') return control.value === '' ? undefined : JSON.parse(control.value);
  if (kind === 'json') {
    const value = control.value.trim();
    if (!value) return undefined;
    try {
      const parsed = JSON.parse(value);
      if (control.dataset.elicitJsonType === 'array' && !Array.isArray(parsed)) throw new Error('请输入 JSON 数组');
      if (control.dataset.elicitJsonType === 'object' && (!parsed || typeof parsed !== 'object' || Array.isArray(parsed))) throw new Error('请输入 JSON 对象');
      if (Array.isArray(parsed)) {
        const minItems = control.dataset.elicitMinItems === '' ? null : Number(control.dataset.elicitMinItems);
        const maxItems = control.dataset.elicitMaxItems === '' ? null : Number(control.dataset.elicitMaxItems);
        if (Number.isInteger(minItems) && parsed.length < minItems) throw new Error(`请至少填写 ${minItems} 项`);
        if (Number.isInteger(maxItems) && parsed.length > maxItems) throw new Error(`最多只能填写 ${maxItems} 项`);
      }
      return parsed;
    } catch (error) {
      if (error.message === '请输入 JSON 数组' || error.message === '请输入 JSON 对象' ||
          error.message.startsWith('请至少填写 ') || error.message.startsWith('最多只能填写 ')) throw error;
      throw new Error('JSON 内容格式不正确');
    }
  }
  const value = control.value.trim();
  return value === '' ? undefined : value;
}

function collectElicitationContent(form) {
  const raw = form.querySelector('[data-elicit-raw]')?.value.trim();
  if (raw) {
    let parsed;
    try { parsed = JSON.parse(raw); }
    catch { throw new Error('高级 JSON 内容格式不正确'); }
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('表单响应必须是 JSON 对象');
    return parsed;
  }

  const generic = form.querySelector('[data-elicit-generic]');
  if (generic) {
    const value = generic.value.trim();
    if (!value) return {};
    try {
      const parsed = JSON.parse(value);
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : { answer: parsed };
    } catch {
      return { answer: value };
    }
  }

  const content = {};
  const multi = new Map();
  for (const marker of form.querySelectorAll('[data-elicit-object-path][data-elicit-object-required="true"]')) {
    setPathValue(content, elicitationPath({ dataset: { elicitPath: marker.dataset.elicitObjectPath } }), {});
  }
  for (const control of form.querySelectorAll('[data-elicit-kind]')) {
    const path = elicitationPath(control);
    const required = control.dataset.elicitRequired === 'true';
    if (control.dataset.elicitKind === 'multi') {
      const key = JSON.stringify(path);
      if (!multi.has(key)) multi.set(key, {
        path,
        required,
        minItems: control.dataset.elicitMinItems === '' ? null : Number(control.dataset.elicitMinItems),
        maxItems: control.dataset.elicitMaxItems === '' ? null : Number(control.dataset.elicitMaxItems),
        values: []
      });
      if (control.checked) multi.get(key).values.push(JSON.parse(control.value));
      continue;
    }
    const value = parseElicitationValue(control);
    if (value === undefined) {
      if (required) {
        control.focus();
        throw new Error('请填写所有必填内容');
      }
      continue;
    }
    setPathValue(content, path, value);
  }
  for (const group of multi.values()) {
    if (Number.isInteger(group.minItems) && group.values.length < group.minItems) throw new Error(`请至少选择 ${group.minItems} 项`);
    if (Number.isInteger(group.maxItems) && group.values.length > group.maxItems) throw new Error(`最多只能选择 ${group.maxItems} 项`);
    if (group.values.length || group.required) setPathValue(content, group.path, group.values);
  }
  if (Object.prototype.hasOwnProperty.call(content, 'value') && Object.keys(content).length === 1) return { value: content.value };
  return content;
}

function approvalValueScore(value, persistence) {
  const normalized = String(value ?? '').replace(/[^a-z0-9]/gi, '').toLowerCase();
  const target = persistence === 'always' ? 'always' : persistence === 'session' ? 'session' : 'once';
  let score = normalized.includes(target) ? 100 : 0;
  if (/allow|approve|accept|yes|continue|proceed/.test(normalized)) score += 50;
  if (/deny|decline|reject|cancel|no/.test(normalized)) score -= 200;
  return score;
}

function approvalValuePersistence(value) {
  const normalized = String(value ?? '').replace(/[^a-z0-9]/gi, '').toLowerCase();
  if (/always|permanent|forever|persist|remember|device/.test(normalized)) return 'always';
  if (/session|task/.test(normalized)) return 'session';
  if (/once|turn|onetime/.test(normalized)) return 'once';
  return null;
}

function isCompatibleApprovalValue(value, persistence) {
  if (typeof value !== 'string') return true;
  const scope = approvalValuePersistence(value);
  if (!persistence) return scope === null || scope === 'once';
  if (persistence === 'session') return scope === null || scope === 'once' || scope === 'session';
  return persistence === 'always';
}

function inferredToolApprovalValue(name, propertyValue, persistence) {
  const property = normalizedSchema(propertyValue);
  if (Object.prototype.hasOwnProperty.call(property, 'const')) return isCompatibleApprovalValue(property.const, persistence) ? property.const : undefined;
  if (Object.prototype.hasOwnProperty.call(property, 'default')) return isCompatibleApprovalValue(property.default, persistence) ? property.default : undefined;
  if (schemaType(property) === 'boolean' && /approve|allow|confirm|consent|permission/i.test(name)) return true;
  const options = schemaOptions(property).filter(option => isCompatibleApprovalValue(option.value, persistence));
  if (options.length) {
    const ranked = [...options].sort((left, right) => approvalValueScore(right.value, persistence) - approvalValueScore(left.value, persistence));
    if (approvalValueScore(ranked[0].value, persistence) > 0) return ranked[0].value;
  }
  return undefined;
}

function toolApprovalManualFields(request) {
  const schema = normalizedSchema(elicitationParams(request).requestedSchema || elicitationParams(request).schema);
  const properties = schema.properties && typeof schema.properties === 'object' ? schema.properties : {};
  const required = new Set(Array.isArray(schema.required) ? schema.required : []);
  return Object.entries(properties)
    .filter(([name, property]) => required.has(name) && inferredToolApprovalValue(name, property, null) === undefined)
    .map(([name, property]) => renderSchemaField(name, property, true, [name], `${request.key}-approval`))
    .join('');
}

function toolApprovalContent(request, persistence, provided = {}) {
  const schema = normalizedSchema(elicitationParams(request).requestedSchema || elicitationParams(request).schema);
  const properties = schema.properties && typeof schema.properties === 'object' ? schema.properties : {};
  const required = new Set(Array.isArray(schema.required) ? schema.required : []);
  const content = { ...provided };
  for (const [name, propertyValue] of Object.entries(properties)) {
    const relevant = required.has(name) || /approve|allow|confirm|consent|scope|decision|permission/i.test(name);
    if (!relevant) continue;
    if (Object.prototype.hasOwnProperty.call(content, name) && !isCompatibleApprovalValue(content[name], persistence))
      throw new Error(`${name} 超出了所选授权时限`);
    const value = inferredToolApprovalValue(name, propertyValue, persistence);
    if (value === undefined && required.has(name) && !Object.prototype.hasOwnProperty.call(content, name)) throw new Error(`授权表单仍需要填写：${name}`);
    if (value !== undefined) content[name] = value;
  }
  return content;
}

function setElicitationBusy(container, busy) {
  container?.querySelectorAll('button,input,select,textarea').forEach(control => { control.disabled = busy; });
  container?.setAttribute('aria-busy', String(busy));
}

async function sendElicitation(key, action, content, persistence, container) {
  setElicitationBusy(container, true);
  try {
    await api(`/pending/${encodeURIComponent(key)}/elicitation`, {
      method: 'POST', body: { action, content: action === 'accept' ? content : null, persistence: persistence || null }
    });
    toast(action === 'accept' ? '已提交，Codex 将继续运行' : action === 'decline' ? '已拒绝这项请求' : '已取消这项请求');
    await load();
    if (currentThread) await refreshCurrentThread(true);
  } catch (error) {
    toast(error.status === 404 ? '这项请求已在其他设备处理' : error.message);
    if (error.status === 404) await load();
    else setElicitationBusy(container, false);
  }
}

async function submitElicitation(event, key) {
  event.preventDefault();
  const form = event.currentTarget;
  if (!form.reportValidity()) return;
  try {
    await sendElicitation(key, 'accept', collectElicitationContent(form), null, form);
  } catch (error) {
    toast(error.message);
  }
}

async function submitElicitationAction(event, key, action, persistence = null) {
  event.preventDefault();
  const container = event.currentTarget.closest('[data-pending-key]');
  let content = {};
  if (action === 'accept') {
    const request = pendingRequests.find(item => item.key === key);
    if (request && String(elicitationParams(request).mode || '').toLowerCase() === 'url') content = null;
    else if (request && isToolApprovalElicitation(request)) {
      if (container?.matches('form') && !container.reportValidity()) return;
      try { content = toolApprovalContent(request, persistence, container?.matches('form') ? collectElicitationContent(container) : {}); }
      catch (error) { toast(error.message); return; }
    }
  }
  await sendElicitation(key, action, content, persistence, container);
}

function mergedProjects(scanned, threads) {
  const map = new Map();
  for (const project of scanned || []) map.set(String(project.path).toLowerCase(), project);
  for (const thread of threads) {
    const path = thread.cwd;
    if (!path) continue;
    const key = path.toLowerCase();
    if (!map.has(key)) {
      const parts = path.split(/[\\/]/).filter(Boolean);
      map.set(key, { name: parts.pop() || path, path, kind: 'Codex' });
    }
  }
  return [...map.values()];
}

function renderDetailPending() {
  if (!currentThread) {
    $('#detailPending').innerHTML = '';
    return;
  }
  const requests = pendingRequests.filter(request => String(request.params?.threadId || '') === currentThread);
  const root = $('#detailPending');
  root.innerHTML = requests.map((request, index) => approvalCard(request, `detail-${index}`)).join('');
  prepareLocalResources(root).catch(() => { /* The link can be retried by tapping it. */ });
}

function renderPendingLists() {
  const recent = $('#recentApprovals');
  const all = $('#approvalList');
  recent.innerHTML = pendingRequests.map((request, index) => approvalCard(request, `recent-${index}`)).join('') || empty('暂无待处理事项');
  all.innerHTML = pendingRequests.map((request, index) => approvalCard(request, `all-${index}`)).join('') || empty('暂无待处理事项');
  prepareLocalResources(recent).catch(() => { /* The link can be retried by tapping it. */ });
  prepareLocalResources(all).catch(() => { /* The link can be retried by tapping it. */ });
  renderDetailPending();
  renderApprovalControls();
  updatePendingCountdowns();
}

function supportedPendingApprovals() {
  return pendingRequests.filter(request => isResolvableApprovalRequest(request) || isToolApprovalElicitation(request));
}

function renderApprovalControls() {
  const count = supportedPendingApprovals().length;
  for (const selector of ['#approveRecent', '#approveAllPending']) {
    const button = $(selector);
    if (!button) continue;
    button.classList.toggle('hidden', count === 0);
    button.disabled = approvalAutomationBusy || count === 0;
    button.textContent = count ? `一键允许全部待审批 (${count})` : '一键允许全部待审批';
  }

  const enabled = approvalAutomation.autoApproveAll === true;
  const supported = approvalAutomation.supported !== false;
  const card = $('#approvalAutomation');
  const toggle = $('#autoApproveAll');
  const badge = $('#autoApproveBadge');
  const description = $('#autoApproveDescription');
  if (toggle) {
    toggle.checked = enabled;
    toggle.disabled = approvalAutomationBusy || !supported;
  }
  if (card) card.classList.toggle('auto-enabled', enabled);
  if (badge) {
    badge.textContent = !supported ? '不可用' : enabled ? '全部点 Yes' : '已关闭';
    badge.classList.toggle('active', enabled);
    badge.classList.toggle('attention', enabled || !supported);
  }
  if (description) {
    description.textContent = !supported
      ? '当前桥接服务版本不支持自动批准；更新服务后即可使用。'
      : enabled
        ? '已开启：电脑端桥接服务会自动允许 Console 管理的新审批。需要回答的问题和表单仍会停下来。'
        : '开启后，电脑端桥接服务会对 Console 管理的新审批自动选择允许。需要你回答的问题仍会停下来。';
  }
  $('#autoApproveBanner')?.classList.toggle('hidden', !enabled);
  document.body.classList.toggle('auto-approval-enabled', enabled);
  updatePermissionHint();
}

async function loadApprovalSettings() {
  try {
    const result = await api('/approval-settings');
    approvalAutomation = { ...approvalAutomation, ...(result?.settings || result), supported: true };
  } catch (error) {
    if (error.status === 404) approvalAutomation = { autoApproveAll: false, supported: false };
    else if (authenticated) toast('审批设置暂时无法同步');
  } finally {
    lastApprovalSettingsRefreshAt = Date.now();
    renderApprovalControls();
  }
}

async function load() {
  if (summaryLoadPromise) return summaryLoadPromise;
  const summaryStartedAt = Date.now();
  const request = (async () => {
    try {
      const summary = await api('/summary');
      lastSummaryRefreshAt = Date.now();
      $('#machine').textContent = summary.machine;
      $('#state').textContent = summary.codexReady ? 'Codex 已连接 · 局域网模式' : '桥接在线，Codex 正在启动';
      $('#dot').classList.toggle('online', summary.codexReady);
      const threads = summary.threads?.data || summary.threads?.threads || [];
      runtimeStates = CodexThreadStatus.mergeRuntimeStates(
        runtimeStates,
        summary.runtimeStates || {},
        lastThreadRefreshAt,
        summaryStartedAt
      );
      const nextPending = summary.pending || [];
      const nextSignature = nextPending.map(request => `${request.key}:${request.createdAt}`).join('|');
      pendingRequests = nextPending;
      const projects = mergedProjects(summary.projects, threads);
      $('#threadCount').textContent = threads.length;
      $('#projectCount').textContent = projects.length;
      $('#processCount').textContent = summary.processes.length;
      $('#recentThreads').innerHTML = threads.slice(0, 5).map(threadCard).join('') || empty('暂无任务');
      $('#threadList').innerHTML = threads.map(threadCard).join('') || empty('暂无任务');
      $('#projectList').innerHTML = projects.map(projectCard).join('') || empty('没有发现项目');
      $('#processList').innerHTML = summary.processes.map(processCard).join('') || empty('没有相关进程');
      if (currentThread) {
        const currentMetadata = threads.find(thread => String(thread.id) === currentThread) || { id: currentThread, status: { type: 'notLoaded' } };
        currentRuntimeState = normalizedRuntimeState(currentMetadata);
        currentActiveTurnId = currentRuntimeState.canControl ? (currentRuntimeState.activeTurnId || '') : '';
        updateComposerState();
      }
      if (nextSignature !== pendingSignature) {
        pendingSignature = nextSignature;
        renderPendingLists();
      }
    } catch (error) {
      if (authenticated && error.status !== 401) toast(error.message);
    }
  })();
  summaryLoadPromise = request;
  try {
    return await request;
  } finally {
    if (summaryLoadPromise === request) summaryLoadPromise = null;
  }
}

function activePage() {
  return document.querySelector('.page.active')?.id || rootPage;
}

function normalizedNavigationState(value = history.state) {
  if (!value || value.app !== navigationMarker) return null;
  const page = value.page === 'threadDetail' || primaryPages.has(value.page) ? value.page : rootPage;
  const threadId = page === 'threadDetail' ? String(value.threadId || '') : '';
  if (page === 'threadDetail' && !threadId) return null;
  const depth = Number.isInteger(value.depth) && value.depth >= 0 ? value.depth : 0;
  return { app: navigationMarker, internal: true, depth, page, threadId };
}

function navigationState(page, threadId = '', depth = navigationDepth) {
  return {
    app: navigationMarker,
    internal: true,
    depth: Math.max(0, Number.isInteger(depth) ? depth : 0),
    page,
    threadId: page === 'threadDetail' ? String(threadId || '') : ''
  };
}

function renderPage(id) {
  const previousDetail = $('#threadDetail').classList.contains('active');
  if (previousDetail && id !== 'threadDetail') saveDraft();
  document.querySelectorAll('.page').forEach(page => page.classList.toggle('active', page.id === id));
  document.querySelectorAll('nav button').forEach(button => button.classList.toggle('active', button.dataset.page === id));
  document.body.classList.toggle('detail-open', id === 'threadDetail');
  const scrolling = document.scrollingElement || document.documentElement;
  scrolling.scrollTop = 0;
  document.documentElement.scrollTop = 0;
  document.body.scrollTop = 0;
  scrollTo({ top: 0, left: 0, behavior: 'auto' });
}

function commitNavigation(page, threadId = '', mode = 'push', depth = null) {
  const current = normalizedNavigationState();
  if (mode === 'none') {
    navigationDepth = depth ?? current?.depth ?? 0;
    return current;
  }
  if (mode === 'push' && current?.page === page && current.threadId === (page === 'threadDetail' ? String(threadId) : '')) {
    navigationDepth = current.depth;
    return current;
  }
  const nextDepth = depth ?? (mode === 'replace' ? current?.depth ?? navigationDepth : (current?.depth ?? navigationDepth) + 1);
  const next = navigationState(page, threadId, nextDepth);
  if (mode === 'replace') history.replaceState(next, '', location.href);
  else history.pushState(next, '', location.href);
  navigationDepth = next.depth;
  return next;
}

function showPage(id, options = {}) {
  const page = primaryPages.has(id) ? id : rootPage;
  commitNavigation(page, '', options.historyMode || 'push', options.depth ?? null);
  renderPage(page);
}

function closeTransientLayer() {
  if (!$('#commandPanel').classList.contains('hidden')) {
    closeCommandPanel(false);
    return true;
  }
  if (!$('#diagnostic').classList.contains('hidden')) {
    $('#diagnostic').classList.add('hidden');
    return true;
  }
  return false;
}

function settleBackTransition() {
  navigationBackPending = false;
  if (navigationBackTimer) clearTimeout(navigationBackTimer);
  navigationBackTimer = 0;
}

function beginBackTransition() {
  navigationBackPending = true;
  if (navigationBackTimer) clearTimeout(navigationBackTimer);
  navigationBackTimer = setTimeout(settleBackTransition, 1200);
  history.back();
}

function handleBackNavigation() {
  if (navigationBackPending) return true;
  if (closeTransientLayer()) return true;
  const state = normalizedNavigationState();
  const page = state?.page || activePage();
  const depth = state?.depth ?? navigationDepth;
  if (depth > 0) {
    beginBackTransition();
    return true;
  }
  if (page === 'threadDetail') {
    const threads = navigationState('threads', '', 0);
    history.replaceState(threads, '', location.href);
    navigationDepth = 0;
    renderPage('threads');
    return true;
  }
  if (page !== rootPage) {
    const root = navigationState(rootPage, '', 0);
    history.replaceState(root, '', location.href);
    navigationDepth = 0;
    renderPage(rootPage);
    return true;
  }
  return false;
}

window.CodexConsoleHandleBack = handleBackNavigation;

async function restoreNavigation(value, loadThread = true) {
  const state = normalizedNavigationState(value) || navigationState(rootPage, '', 0);
  navigationDepth = state.depth;
  if (state.page === 'threadDetail') {
    renderPage('threadDetail');
    if (loadThread) await openThread(state.threadId, false, 'none');
    else currentThread = state.threadId;
    return;
  }
  renderPage(state.page);
}

function initializeNavigation() {
  let state = normalizedNavigationState();
  if (!state) {
    state = navigationState(rootPage, '', 0);
    history.replaceState(state, '', location.href);
  }
  navigationDepth = state.depth;
  if (state.page === 'threadDetail') {
    $('#turns').innerHTML = empty('加载中');
  }
  renderPage(state.page);
}

window.addEventListener('popstate', event => {
  settleBackTransition();
  const state = normalizedNavigationState(event.state);
  if (!state) return;
  restoreNavigation(state, true).catch(error => toast(error.message));
});

document.querySelectorAll('nav button').forEach(button => {
  button.onclick = () => showPage(button.dataset.page);
});

$('#pairForm').onsubmit = async event => {
  event.preventDefault();
  $('#pairError').textContent = '';
  try {
    const result = await api('/pair', {
      method: 'POST',
      body: { code: $('#code').value, deviceName: navigator.userAgent }
    });
    token = result.token;
    const nativeConfigured = !nativeNotificationBridge() ||
      callNativeNotification('configure', token) === 'configured';
    refreshNativeNotificationStatus();
    $('#pair').classList.add('hidden');
    await api('/session', { method: 'POST' });
    authenticated = true;
    localStorage.removeItem(tokenKey);
    if (nativeConfigured) token = '';
    await Promise.all([load(), loadApprovalSettings()]);
    await restoreNavigation(history.state, true);
  } catch (error) {
    $('#pairError').textContent = error.message;
  }
};

function updateComposerState() {
  const running = currentRuntimeState?.isRunning === true || Boolean(currentActiveTurnId);
  const unknown = currentRuntimeState?.isRunning !== true && currentRuntimeState?.isRunning !== false;
  const controllable = currentRuntimeState?.canControl !== false;
  const phase = currentRuntimeState?.phase || '';
  $('#interruptTurn').classList.toggle('hidden', !currentActiveTurnId || !controllable);
  $('#sendMode').textContent = unknown
    ? '当前状态不可见；发送时会由电脑端重新确认'
    : !running
      ? '发送新的指令'
      : !controllable
        ? '当前轮次正在运行；结束后即可续接'
        : phase === 'waitingInput'
          ? '任务正在等待你的回答'
          : phase === 'waitingApproval'
            ? '任务正在等待批准'
            : '当前任务运行中，将作为补充指令发送';
  $('#sendMode').title = statusTitle(currentRuntimeState);
  if (!sending) $('#sendMessage').textContent = running && controllable ? '追加' : '发送';
  updatePermissionHint();
}

const quickCommands = [
  { id: 'go', alias: '/go', title: '继续完成', description: '接着当前进度做完剩余工作并验收', intent: 'continue', seed: '继续完成当前任务。请先检查已有进展，再执行剩余工作并完成验证。' },
  { id: 'goal', alias: '/goal', title: '设置工作目标', description: '让 Codex 建立并持续追踪一个明确目标', intent: 'plain', seed: '请将下面的内容设置为当前工作目标，并持续推进直到完成：\n' },
  { id: 'skills', alias: '/skills', title: '选择技能', description: '从电脑已经安装的技能中选择，不用输入名称', action: 'skills' },
  { id: 'tools', alias: '/tools', title: '选择工具', description: '选择浏览器、桌面、联网或文件能力', action: 'tools' },
  { id: 'compact', alias: '/compact', title: '压缩当前上下文', description: '让 Codex 正式压缩冗长历史，保留关键进度', action: 'compact', intent: 'plain', seed: '请整理当前任务上下文，只保留工作目标、已完成内容、重要决定、未解决问题和下一步，然后继续处理。' },
  { id: 'status', alias: '/status', title: '查看任务状态', description: '汇报完成情况、阻碍和下一步', intent: 'explain', seed: '请简要汇报当前任务状态：已经完成什么、正在处理什么、是否存在阻碍，以及下一步是什么。' },
  { id: 'stop', alias: '/stop', title: '停止当前任务', description: '中断正在运行的这一轮', action: 'stop' },
  { id: 'new', alias: '/new', title: '新建任务', description: '为另一个项目开始独立任务', action: 'new' },
  { id: 'fix', alias: '/fix', title: '修复问题', description: '定位原因、修改并回归测试', intent: 'fix', seed: '请定位当前问题的根因，完成修复，并进行必要的回归测试。' },
  { id: 'explain', alias: '/explain', title: '解释当前结果', description: '用清楚的语言说明原理和现状', intent: 'explain', seed: '请解释当前结果、关键原理和需要我注意的事项。' },
  { id: 'verify', alias: '/verify', title: '全面验收', description: '检查功能、窄屏、异常和交付物', intent: 'test', seed: '请对当前成果进行全面验收，覆盖主要功能、异常情况、手机窄屏和最终交付物。' }
];

const toolChoices = [
  { id: 'browser', title: '网页与登录', description: '使用电脑上已经登录的浏览器', instruction: '需要查看或操作网页时，请使用电脑上现有的登录会话。' },
  { id: 'computer', title: 'Windows 应用', description: '操作桌面软件或系统界面', instruction: '需要桌面交互时，请直接操作相应的 Windows 应用并核对结果。' },
  { id: 'web', title: '联网查资料', description: '搜索并核实最新官方信息', instruction: '涉及会变化的信息时，请联网查询并优先使用官方来源。' },
  { id: 'visual', title: '图片与界面检查', description: '查看截图、图片或逐页检查界面', instruction: '涉及视觉结果时，请实际查看图片或界面，不要只根据代码推测。' },
  { id: 'files', title: '文件与代码', description: '读取、修改、构建并验证本地文件', instruction: '可以在任务范围内处理本地文件；完成后请构建或运行相关检查。' }
];

function visibleToolChoices() {
  if (!availableTools.length) return toolChoices;
  const names = new Set(availableTools.map(tool => tool.title.toLowerCase()));
  return [...availableTools, ...toolChoices.filter(tool => !names.has(tool.title.toLowerCase()))];
}

function toolForId(id) {
  return visibleToolChoices().find(tool => tool.id === id) || selectedToolDetails[id];
}

function attachmentsFor(threadId = currentThread) {
  if (!threadId) return [];
  if (!attachmentDrafts.has(threadId)) attachmentDrafts.set(threadId, []);
  return attachmentDrafts.get(threadId);
}

function attachmentKind(file) {
  if (file.type.startsWith('image/')) return 'image';
  if (file.type.startsWith('video/')) return 'video';
  return 'file';
}

function renderAttachments() {
  const container = $('#attachmentPreview');
  const items = attachmentsFor();
  container.classList.toggle('hidden', items.length === 0);
  container.innerHTML = items.map(item => {
    const kind = attachmentKind(item.file);
    const preview = kind === 'image'
      ? `<img src="${esc(item.previewUrl)}" alt="">`
      : kind === 'video'
        ? `<video src="${esc(item.previewUrl)}" muted preload="metadata"></video>`
        : `<span class="attachment-file-icon">${esc((item.file.name.split('.').pop() || 'FILE').slice(0, 5).toUpperCase())}</span>`;
    return `<article class="attachment-item" data-attachment-id="${esc(item.id)}">${preview}<div><b title="${esc(item.file.name)}">${esc(item.file.name)}</b><small>${formatBytes(item.file.size)}${item.uploaded ? ' · 已上传' : ''}</small></div><button type="button" class="attachment-remove" aria-label="移除 ${esc(item.file.name)}">×</button></article>`;
  }).join('');
}

function addAttachments(files) {
  const items = attachmentsFor();
  for (const file of files) {
    if (items.length >= maxAttachmentCount) {
      toast(`每次最多添加 ${maxAttachmentCount} 个附件`);
      break;
    }
    if (file.size > maxAttachmentBytes) {
      toast(`${file.name} 超过 128 MB，未添加`);
      continue;
    }
    if (items.reduce((sum, item) => sum + item.file.size, 0) + file.size > maxAttachmentRequestBytes) {
      toast('本次附件总大小不能超过 256 MB');
      break;
    }
    if (items.some(item => item.file.name === file.name && item.file.size === file.size && item.file.lastModified === file.lastModified)) continue;
    const kind = attachmentKind(file);
    items.push({
      id: messageId(),
      file,
      previewUrl: kind === 'image' || kind === 'video' ? URL.createObjectURL(file) : '',
      uploaded: null
    });
  }
  renderAttachments();
}

function removeAttachment(id) {
  const items = attachmentsFor();
  const index = items.findIndex(item => item.id === id);
  if (index < 0) return;
  const [removed] = items.splice(index, 1);
  if (removed.previewUrl) URL.revokeObjectURL(removed.previewUrl);
  renderAttachments();
}

function clearAttachments(threadId = currentThread) {
  const items = attachmentsFor(threadId);
  for (const item of items) if (item.previewUrl) URL.revokeObjectURL(item.previewUrl);
  attachmentDrafts.delete(threadId);
  if (threadId === currentThread) renderAttachments();
}

async function uploadAttachments(threadId) {
  const items = attachmentsFor(threadId);
  const waiting = items.filter(item => !item.uploaded);
  if (waiting.length) {
    const form = new FormData();
    for (const item of waiting) form.append('files', item.file, item.file.name);
    const result = await apiForm(`/files/upload?threadId=${encodeURIComponent(threadId)}`, form);
    const uploaded = result?.files || [];
    if (uploaded.length !== waiting.length) throw new Error('电脑端没有确认全部附件，请重试');
    waiting.forEach((item, index) => { item.uploaded = uploaded[index]; });
    if (threadId === currentThread) renderAttachments();
  }
  return items.map(item => ({
    id: item.uploaded.id,
    name: item.uploaded.name || item.file.name,
    path: item.uploaded.path,
    size: item.uploaded.size ?? item.file.size,
    mime: item.uploaded.mime || item.uploaded.contentType || item.file.type,
    kind: item.uploaded.kind || attachmentKind(item.file)
  }));
}

function renderSelectedCommands() {
  const container = $('#selectedCommands');
  const chips = [
    ...selectedTools.map(id => {
      const tool = toolForId(id);
      return tool ? `<button type="button" class="selected-chip" data-remove-tool="${esc(id)}">工具：${esc(tool.title)} <span>×</span></button>` : '';
    }),
    ...selectedSkills.map(skill => `<button type="button" class="selected-chip" data-remove-skill="${esc(skill.path)}">技能：${esc(skill.interface?.displayName || skill.name)} <span>×</span></button>`)
  ].filter(Boolean);
  container.innerHTML = chips.join('');
  container.classList.toggle('hidden', chips.length === 0);
}

function flattenedSkills(result) {
  const entries = Array.isArray(result) ? result : (result?.data || []);
  const map = new Map();
  for (const entry of entries) {
    const skills = entry?.skills ? entry.skills : (entry?.name ? [entry] : []);
    for (const skill of skills) if (skill?.enabled !== false && skill?.path) map.set(skill.path, skill);
  }
  return [...map.values()].sort((a, b) => String(a.interface?.displayName || a.name).localeCompare(String(b.interface?.displayName || b.name)));
}

async function loadSkills(force = false) {
  const key = currentThreadCwd || currentThread || 'default';
  if (!force && skillsLoadedFor === key) return;
  try {
    const query = currentThreadCwd ? `?cwd=${encodeURIComponent(currentThreadCwd)}` : '';
    availableSkills = flattenedSkills(await api('/skills' + query));
    skillsLoadedFor = key;
  } catch (error) {
    availableSkills = [];
    skillsLoadedFor = key;
    if (error.status !== 404) toast('暂时无法读取技能列表');
  }
}

function flattenedTools(result) {
  const source = result?.tools || result?.data || result || [];
  const rows = Array.isArray(source) ? source : [];
  const items = rows.flatMap(row => Array.isArray(row?.tools)
    ? row.tools.map(tool => ({ ...tool, server: tool.server || row.name }))
    : [row]);
  for (const server of result?.servers || []) {
    for (const tool of server.tools || []) items.push({ ...tool, server: server.name });
  }
  for (const app of result?.apps || []) {
    if (app?.enabled === false || app?.accessible === false) continue;
    items.push({ ...app, server: 'App', type: 'app' });
  }
  const map = new Map();
  for (const tool of items) {
    const name = String(tool?.name || tool?.id || '').trim();
    if (!name) continue;
    const server = String(tool.server || tool.source || tool.type || '').trim();
    const id = `remote:${server}:${name}`;
    const title = String(tool.displayName || tool.title || name);
    const description = String(tool.description || tool.summary || (server ? `来自 ${server}` : '电脑当前可用工具'));
    map.set(id, {
      id,
      title,
      description,
      instruction: `如果任务适合，请优先使用 ${title} 工具${description ? `（${description}）` : ''}。`
    });
  }
  return [...map.values()].sort((a, b) => a.title.localeCompare(b.title));
}

async function loadTools(force = false) {
  const key = currentThread || 'default';
  if (!force && toolsLoadedFor === key) return;
  try {
    const query = currentThread ? `?threadId=${encodeURIComponent(currentThread)}` : '';
    availableTools = flattenedTools(await api('/tools' + query));
    toolsLoadedFor = key;
  } catch (error) {
    availableTools = [];
    toolsLoadedFor = key;
    if (error.status !== 404) toast('暂时无法读取电脑工具列表，已显示常用能力');
  }
}

function commandMatches(item, filter) {
  if (!filter) return true;
  return [item.title, item.description, item.alias, item.name, item.shortDescription, item.interface?.shortDescription]
    .filter(Boolean).join(' ').toLowerCase().includes(filter.toLowerCase());
}

function renderCommandPanel() {
  const filter = commandPanelFilter.trim().replace(/^\//, '');
  const commands = quickCommands.filter(item => commandMatches(item, filter));
  const tools = visibleToolChoices().filter(item => commandMatches(item, filter));
  const skills = availableSkills.filter(item => commandMatches(item, filter));
  const section = (title, items) => items.length ? `<section class="command-group"><h3>${esc(title)}</h3>${items.join('')}</section>` : '';
  const commandHtml = commands.map(item => `<button type="button" class="command-item" data-command="${esc(item.id)}"><span><b>${esc(item.title)}</b><small>${esc(item.description)}</small></span><code>${esc(item.alias)}</code></button>`);
  const toolHtml = tools.map(item => `<button type="button" class="command-item ${selectedTools.includes(item.id) ? 'selected' : ''}" data-tool="${esc(item.id)}"><span><b>${esc(item.title)}</b><small>${esc(item.description)}</small></span><span class="command-check">${selectedTools.includes(item.id) ? '✓' : '+'}</span></button>`);
  const skillHtml = skills.map(item => {
    const selected = selectedSkills.some(skill => skill.path === item.path);
    return `<button type="button" class="command-item ${selected ? 'selected' : ''}" data-skill="${esc(item.path)}"><span><b>${esc(item.interface?.displayName || item.name)}</b><small>${esc(item.interface?.shortDescription || item.shortDescription || item.description || '使用这个技能处理任务')}</small></span><span class="command-check">${selected ? '✓' : '+'}</span></button>`;
  });
  $('#commandList').innerHTML = section('下一步', commandHtml) + section('可以调用的工具', toolHtml) + section('已安装技能', skillHtml) || empty(filter ? '没有匹配的操作' : '没有可显示的操作');
}

async function openCommandPanel(filter = '') {
  commandPanelFilter = filter;
  $('#commandSearch').value = filter;
  $('#commandPanel').classList.remove('hidden');
  document.body.classList.add('command-open');
  renderCommandPanel();
  await Promise.all([loadSkills(), loadTools()]);
  renderCommandPanel();
  renderSelectedCommands();
  requestAnimationFrame(() => $('#commandSearch').focus());
}

function closeCommandPanel(restoreFocus = true) {
  $('#commandPanel').classList.add('hidden');
  document.body.classList.remove('command-open');
  if (restoreFocus) $('#message').focus();
}

async function compactCurrentThread(command) {
  closeCommandPanel();
  if (!currentThread) {
    toast('请先打开一个任务');
    return;
  }
  try {
    await api(`/threads/${encodeURIComponent(currentThread)}/compact`, { method: 'POST' });
    toast('已开始压缩当前任务上下文');
    await refreshCurrentThread(true);
  } catch (error) {
    if (![404, 405].includes(error.status)) {
      toast(error.message);
      return;
    }
    setIntent(command.intent || 'plain');
    $('#message').value = command.seed;
    const end = $('#message').value.length;
    $('#message').setSelectionRange(end, end);
    saveDraft();
    toast('当前电脑端不支持直接压缩，已准备等效指令');
  }
}

function applyQuickCommand(id) {
  const command = quickCommands.find(item => item.id === id);
  if (!command) return;
  if (command.action === 'skills' || command.action === 'tools') {
    commandPanelFilter = '';
    $('#commandSearch').value = '';
    renderCommandPanel();
    const selector = command.action === 'skills' ? '[data-skill]' : '[data-tool]';
    requestAnimationFrame(() => $('#commandList').querySelector(selector)?.scrollIntoView({ block: 'center' }));
    return;
  }
  if (command.action === 'stop') {
    closeCommandPanel();
    if (currentActiveTurnId) $('#interruptTurn').click();
    else toast('当前没有正在运行的任务');
    return;
  }
  if (command.action === 'new') {
    closeCommandPanel();
    $('#newThread').click();
    return;
  }
  if (command.action === 'compact') {
    compactCurrentThread(command);
    return;
  }
  setIntent(command.intent);
  const current = $('#message').value.trim();
  if (!current || /^\/[a-z-]*$/i.test(current)) $('#message').value = command.seed;
  const end = $('#message').value.length;
  $('#message').setSelectionRange(end, end);
  saveDraft();
  closeCommandPanel();
}

function toggleTool(id) {
  const detail = toolForId(id);
  if (detail) selectedToolDetails[id] = detail;
  selectedTools = selectedTools.includes(id) ? selectedTools.filter(item => item !== id) : [...selectedTools, id];
  renderSelectedCommands();
  renderCommandPanel();
  saveDraft();
}

function toggleSkill(path) {
  const selected = selectedSkills.some(skill => skill.path === path);
  selectedSkills = selected ? selectedSkills.filter(skill => skill.path !== path) : [...selectedSkills, availableSkills.find(skill => skill.path === path)].filter(Boolean);
  renderSelectedCommands();
  renderCommandPanel();
  if (/^(?:\$[^\s]*|\/skills?)$/i.test($('#message').value.trim())) $('#message').value = '';
  saveDraft();
}

const executionSettingsKey = 'codexExecutionSettingsV2';
const legacyExecutionSettingsKey = 'codexExecutionSettings';
const defaultExecutionSettings = Object.freeze({ permissions: ':danger-full-access', approvalMode: 'never' });
const dangerAcknowledgedKey = 'codexDangerPermissionAcknowledgedV2';
const unrestrictedAcknowledgedKey = 'codexUnrestrictedAutonomyAcknowledgedV1';
const autoApproveAcknowledgedKey = 'codexAutoApproveAllAcknowledgedV1';

function executionSettings() {
  const mode = $('#approvalMode').value || defaultExecutionSettings.approvalMode;
  const approval = ({
    auto: { approvalPolicy: 'on-request', approvalsReviewer: 'auto_review' },
    ask: { approvalPolicy: 'on-request', approvalsReviewer: 'user' },
    never: { approvalPolicy: 'never', approvalsReviewer: 'user' },
    strict: { approvalPolicy: 'untrusted', approvalsReviewer: 'user' }
  })[mode] || { approvalPolicy: 'never', approvalsReviewer: 'user' };
  return { permissions: $('#runtimePermission').value || defaultExecutionSettings.permissions, ...approval };
}

function storedExecutionSettings() {
  try {
    const stored = JSON.parse(localStorage.getItem(executionSettingsKey) || 'null');
    if (stored && typeof stored === 'object') return { ...defaultExecutionSettings, ...stored };
    const legacy = JSON.parse(localStorage.getItem(legacyExecutionSettingsKey) || 'null');
    // Preserve an existing unrestricted choice. Older conservative defaults are
    // intentionally upgraded once because V2 makes phone-owned tasks autonomous.
    const migrated = legacy?.permissions === ':danger-full-access' && legacy?.approvalMode === 'never'
      ? { permissions: legacy.permissions, approvalMode: legacy.approvalMode }
      : { ...defaultExecutionSettings };
    localStorage.setItem(executionSettingsKey, JSON.stringify(migrated));
    return migrated;
  } catch {
    return { ...defaultExecutionSettings };
  }
}

function applyExecutionSettings(settings = {}) {
  if ([':read-only', ':workspace', ':danger-full-access'].includes(settings.permissions))
    $('#runtimePermission').value = settings.permissions;
  if (['auto', 'ask', 'never', 'strict'].includes(settings.approvalMode))
    $('#approvalMode').value = settings.approvalMode;
  updatePermissionHint();
}

function loadExecutionSettings() {
  applyExecutionSettings(storedExecutionSettings());
}

function saveExecutionSettings() {
  localStorage.setItem(executionSettingsKey, JSON.stringify({
    permissions: $('#runtimePermission').value,
    approvalMode: $('#approvalMode').value
  }));
  updatePermissionHint();
}

function activeExecutionPreset() {
  const permission = $('#runtimePermission').value;
  const mode = $('#approvalMode').value;
  if (permission === ':workspace' && mode === 'auto') return 'reviewed';
  if (permission === ':workspace' && mode === 'never') return 'workspace-autonomous';
  if (permission === ':danger-full-access' && mode === 'never') return 'unrestricted';
  return '';
}

function updateExecutionPresetState() {
  const active = activeExecutionPreset();
  document.querySelectorAll('[data-execution-preset]').forEach(button => {
    const selected = button.dataset.executionPreset === active;
    button.classList.toggle('active', selected);
    button.setAttribute('aria-pressed', String(selected));
    if (button.dataset.executionPreset === 'reviewed') {
      button.textContent = approvalAutomation.autoApproveAll ? '审核已覆盖' : '安全审核';
      button.title = approvalAutomation.autoApproveAll ? '自动批准开启时不会进行 Codex 风险审核' : '';
    }
  });
}

function updatePermissionHint() {
  const hint = $('#permissionHint');
  if (!hint) return;
  const permission = $('#runtimePermission').value;
  const mode = $('#approvalMode').value;
  const approvalSelect = $('#approvalMode');
  const approvalLabels = approvalAutomation.autoApproveAll ? {
    auto: 'Codex 审核（已被自动批准覆盖）',
    ask: '在手机上问我（已被自动批准覆盖）',
    never: '不发起审批，直接运行',
    strict: '严格询问（已被自动批准覆盖）'
  } : {
    auto: '交给 Codex 审核（不是全同意）',
    ask: '在手机上问我',
    never: '不发起审批，直接运行',
    strict: '更多操作都问我'
  };
  approvalSelect.querySelectorAll('option').forEach(option => {
    if (approvalLabels[option.value]) option.textContent = approvalLabels[option.value];
  });
  const nextTurn = currentRuntimeState?.isRunning === true ? ' 当前轮不会中途换权，新设置从下一轮生效。' : '';
  const ownership = permission === ':danger-full-access' && mode === 'never'
    ? ' 由其他 Codex 进程已经发起的活动轮次会保留原有设置；手机续跑时会使用这里的权限。'
    : '';
  let text = permission === ':read-only'
    ? '只读沙箱：Codex 可以查看内容，但不能修改文件。'
    : permission === ':danger-full-access'
      ? '无沙箱：Codex 可以访问整台电脑，包括项目外文件和已登录的应用。'
      : '项目沙箱：Codex 只能修改当前项目范围内的文件。';
  if (mode === 'auto') text += approvalAutomation.autoApproveAll
    ? ' 自动批准已覆盖风险审核，仍出现的请求会直接选择 Yes。'
    : ' Codex 风险审核会逐项判断并可能拒绝；这不是“全部点 Yes”。';
  if (mode === 'ask') text += approvalAutomation.autoApproveAll
    ? ' 自动批准已覆盖“在手机上问我”，审批会直接选择 Yes。'
    : ' 必要操作会在手机上询问。';
  if (mode === 'never') {
    text += permission === ':danger-full-access'
      ? ' 不会发起逐项审批，允许的操作会直接运行。'
      : ' 不会发起逐项审批；沙箱内操作直接运行，超出沙箱会失败。';
    if (approvalAutomation.autoApproveAll) text += ' 自动批准仍保持开启，但这个模式通常不会产生审批。';
  }
  if (mode === 'strict') text += approvalAutomation.autoApproveAll
    ? ' 自动批准已覆盖严格询问，审批会直接选择 Yes。'
    : ' 大多数外部操作都会要求确认。';
  hint.textContent = text + ownership + nextTurn;
  hint.classList.toggle('warning', permission === ':danger-full-access' || approvalAutomation.autoApproveAll);
  updateExecutionPresetState();
}

async function loadPermissionProfiles(cwd) {
  const key = String(cwd || '').toLowerCase();
  if (!key || key === permissionsLoadedFor) return;
  const result = await api(`/permissions?cwd=${encodeURIComponent(cwd)}`);
  if (key !== String(currentThreadCwd || cwd).toLowerCase() && currentThread) return;
  permissionsLoadedFor = key;
  const profiles = Array.isArray(result?.data) ? result.data : [];
  if (profiles.length) {
    allowedPermissionProfiles = new Set(profiles.filter(profile => profile?.allowed).map(profile => profile.id));
    $('#runtimePermission').querySelectorAll('option').forEach(option => {
      option.disabled = !allowedPermissionProfiles.has(option.value);
    });
    if (!allowedPermissionProfiles.has($('#runtimePermission').value)) {
      const fallback = [':workspace', ':read-only', ':danger-full-access'].find(value => allowedPermissionProfiles.has(value));
      if (fallback) {
        $('#runtimePermission').value = fallback;
        saveExecutionSettings();
        toast('当前电脑策略不允许原来的权限，已改用可用选项');
      }
    }
  }
  updatePermissionHint();
}

function showRiskConfirmation({ title, message, consequences }) {
  if (riskResolver) riskResolver(false);
  $('#riskTitle').textContent = title;
  $('#riskMessage').textContent = message;
  $('#riskConsequences').innerHTML = consequences.map(item => `<li>${esc(item)}</li>`).join('');
  $('#riskAcknowledged').checked = false;
  $('#riskConfirm').disabled = true;
  $('#riskPanel').classList.remove('hidden');
  document.body.classList.add('command-open');
  setTimeout(() => $('#riskAcknowledged').focus(), 0);
  return new Promise(resolve => { riskResolver = resolve; });
}

function closeRiskConfirmation(accepted) {
  if (!riskResolver) return;
  const resolve = riskResolver;
  riskResolver = null;
  $('#riskPanel').classList.add('hidden');
  if ($('#commandPanel').classList.contains('hidden')) document.body.classList.remove('command-open');
  resolve(Boolean(accepted));
}

$('#riskAcknowledged').addEventListener('change', event => {
  $('#riskConfirm').disabled = !event.target.checked;
});
$('#riskConfirm').onclick = () => closeRiskConfirmation(true);
$('#riskCancel').onclick = () => closeRiskConfirmation(false);
$('#riskBackdrop').onclick = () => closeRiskConfirmation(false);

async function confirmExecutionSettings() {
  const settings = executionSettings();
  if (settings.permissions !== ':danger-full-access') return true;
  const unrestricted = settings.approvalPolicy === 'never';
  const acknowledgementKey = unrestricted ? unrestrictedAcknowledgedKey : dangerAcknowledgedKey;
  if (localStorage.getItem(acknowledgementKey) === 'yes') return true;
  const accepted = await showRiskConfirmation(unrestricted ? {
    title: '启用完全自主运行？',
    message: '这会为由 Console 发起或续跑的轮次同时关闭电脑文件沙箱和逐项审批。Codex 获得与当前 Windows 用户相同的访问范围。',
    consequences: ['可以读写或删除项目外文件。', '可以运行命令并操作已登录的应用。', '其他 Codex 进程已经发起的活动轮次会保留原有设置。', '错误指令或恶意网页内容可能直接造成损失。']
  } : {
    title: '允许访问整台电脑？',
    message: '这会为由 Console 发起或续跑的轮次关闭文件沙箱，但仍保留你选择的审批策略。',
    consequences: ['Codex 可以访问当前项目以外的文件。', '审批审核不是文件隔离，不能替代沙箱。', '只应在你信任当前任务和输入来源时启用。']
  });
  if (accepted) {
    localStorage.setItem(acknowledgementKey, 'yes');
    if (unrestricted) localStorage.setItem(dangerAcknowledgedKey, 'yes');
  }
  return accepted;
}

async function applyExecutionPreset(name) {
  const previous = {
    permissions: $('#runtimePermission').value,
    approvalMode: $('#approvalMode').value
  };
  const next = ({
    reviewed: { permissions: ':workspace', approvalMode: 'auto' },
    'workspace-autonomous': { permissions: ':workspace', approvalMode: 'never' },
    unrestricted: { permissions: ':danger-full-access', approvalMode: 'never' }
  })[name];
  if (!next) return;
  if (!allowedPermissionProfiles.has(next.permissions)) {
    toast('当前电脑策略不允许这个权限预设');
    return;
  }
  applyExecutionSettings(next);
  if (!(await confirmExecutionSettings())) {
    applyExecutionSettings(previous);
    return;
  }
  saveExecutionSettings();
}

function draftKey(threadId = currentThread) {
  return `codexDraft:${threadId}`;
}

function draftData() {
  return {
    message: $('#message').value,
    scope: $('#messageScope').value,
    policy: $('#messagePolicy').value,
    done: $('#messageDone').value,
    intent: selectedIntent,
    tools: selectedTools.map(id => toolForId(id) || { id, title: id, description: '', instruction: '' }),
    skills: selectedSkills.map(skill => ({ name: skill.name, path: skill.path, interface: skill.interface, shortDescription: skill.shortDescription }))
  };
}

function saveDraft() {
  if (!currentThread) return;
  const draft = draftData();
  if (draft.message || draft.scope || draft.policy || draft.done || draft.intent !== 'plain' || draft.tools.length || draft.skills.length) {
    localStorage.setItem(draftKey(), JSON.stringify(draft));
  } else {
    localStorage.removeItem(draftKey());
  }
}

function setIntent(intent) {
  selectedIntent = ['plain', 'continue', 'fix', 'explain', 'test'].includes(intent) ? intent : 'plain';
  document.querySelectorAll('.intent').forEach(button => button.classList.toggle('active', button.dataset.intent === selectedIntent));
}

function loadDraft() {
  let draft = {};
  try { draft = JSON.parse(localStorage.getItem(draftKey()) || '{}'); } catch { draft = {}; }
  $('#message').value = draft.message || '';
  $('#messageScope').value = draft.scope || '';
  $('#messagePolicy').value = draft.policy || '';
  $('#messageDone').value = draft.done || '';
  selectedToolDetails = {};
  if (Array.isArray(draft.tools)) {
    selectedTools = draft.tools.map(tool => typeof tool === 'string' ? tool : tool?.id).filter(Boolean);
    for (const tool of draft.tools) if (tool && typeof tool === 'object' && tool.id) selectedToolDetails[tool.id] = tool;
  } else selectedTools = [];
  selectedSkills = Array.isArray(draft.skills) ? draft.skills.filter(skill => skill?.name && skill?.path) : [];
  setIntent(draft.intent || 'plain');
  $('#composerDetails').open = Boolean(draft.scope || draft.policy || draft.done);
  renderSelectedCommands();
  renderAttachments();
}

function buildMessageText() {
  let goal = $('#message').value.trim();
  if (!goal && attachmentsFor().length) goal = '请查看并处理我附上的文件。';
  if (!goal && selectedSkills.length) goal = '请使用我选择的技能继续处理当前任务。';
  if (!goal) return '';
  const scope = $('#messageScope').value.trim();
  const policy = $('#messagePolicy').value;
  const done = $('#messageDone').value.trim();
  if (selectedIntent === 'plain' && !scope && !policy && !done && !selectedTools.length) return goal;
  const intentLabels = { continue: '继续处理', fix: '修复问题', explain: '解释说明', test: '测试与验收' };
  const policyLabels = {
    analysis: '只分析并说明，不要修改文件。',
    edit: '可以在任务范围内修改文件。',
    verify: '可以修改文件；完成后必须测试并验收。'
  };
  const lines = [];
  if (intentLabels[selectedIntent]) lines.push(`任务类型：${intentLabels[selectedIntent]}`);
  lines.push(`目标：${goal}`);
  if (scope) lines.push(`涉及范围：${scope}`);
  if (policyLabels[policy]) lines.push(`操作要求：${policyLabels[policy]}`);
  const toolInstructions = selectedTools.map(id => toolForId(id)?.instruction).filter(Boolean);
  if (toolInstructions.length) lines.push(`工具偏好：${toolInstructions.join(' ')}`);
  if (done) lines.push(`完成标准：${done}`);
  return lines.join('\n');
}

function clearDraft() {
  localStorage.removeItem(draftKey());
  $('#message').value = '';
  $('#messageScope').value = '';
  $('#messagePolicy').value = '';
  $('#messageDone').value = '';
  selectedTools = [];
  selectedToolDetails = {};
  selectedSkills = [];
  $('#composerDetails').open = false;
  setIntent('plain');
  renderSelectedCommands();
  clearAttachments();
}

function messageId() {
  if (crypto?.randomUUID) return crypto.randomUUID();
  return `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

async function refreshCurrentThread(background = false) {
  if (!currentThread) return;
  if (background && (document.visibilityState !== 'visible' || !$('#threadDetail').classList.contains('active'))) return;
  const threadId = currentThread;
  const scrolling = document.scrollingElement || document.documentElement;
  const nearBottom = scrolling.scrollHeight - scrolling.scrollTop - scrolling.clientHeight < 180;
  let request = latestThreadRequests.get(threadId);
  if (!request) {
    request = api(`/threads/${encodeURIComponent(threadId)}?paged=true&limit=${turnPageSize}`);
    latestThreadRequests.set(threadId, request);
    request.finally(() => {
      if (latestThreadRequests.get(threadId) === request) latestThreadRequests.delete(threadId);
    }).catch(() => { /* The original await reports the request error. */ });
  }
  const result = await request;
  if (threadId !== currentThread) return;
  lastThreadRefreshAt.set(threadId, Date.now());
  const thread = result.thread || result;
  if (thread.cwd && thread.cwd !== currentThreadCwd) {
    currentThreadCwd = thread.cwd;
    skillsLoadedFor = '';
    permissionsLoadedFor = '';
    loadPermissionProfiles(thread.cwd).catch(error => toast(error.message));
  }
  $('#detailTitle').textContent = thread.name || thread.preview || '任务';
  const nextTurns = thread.turns || [];
  const nextSignature = JSON.stringify(nextTurns);
  currentTurns = nextTurns;
  currentTurnCursor = result.nextCursor || '';
  hasEarlierTurns = Boolean(result.hasEarlier);
  if (Object.prototype.hasOwnProperty.call(result, 'runtimeState')) {
    if (result.runtimeState) runtimeStates[threadId] = result.runtimeState;
    else delete runtimeStates[threadId];
  }
  currentRuntimeState = normalizedRuntimeState(thread);
  currentActiveTurnId = currentRuntimeState?.canControl && currentRuntimeState?.isRunning
    ? (currentRuntimeState.activeTurnId || '')
    : '';
  updateComposerState();
  if (!background || nextSignature !== currentThreadSignature) {
    currentThreadSignature = nextSignature;
    renderThreadHistory(background && !nearBottom);
  }
  renderDetailPending();
}

async function openThread(id, background = false, historyMode = 'push') {
  const switching = id !== currentThread;
  if (switching) {
    saveDraft();
    currentThread = id;
    currentThreadCwd = '';
    currentTurnCursor = '';
    currentRuntimeState = null;
    toolsLoadedFor = '';
    availableTools = [];
    currentThreadSignature = '';
    currentActiveTurnId = '';
    loadDraft();
  }
  if (!background) {
    if (historyMode === 'push') {
      const state = normalizedNavigationState();
      if (state?.page !== 'threads') commitNavigation('threads');
    }
    commitNavigation('threadDetail', id, historyMode);
    renderPage('threadDetail');
    $('#turns').innerHTML = empty('加载中');
  }
  try {
    await refreshCurrentThread(background);
  } catch (error) {
    if (!background) $('#turns').innerHTML = empty('加载失败，请点击右上角刷新后重试');
    toast(error.message);
  }
}

document.querySelectorAll('.intent').forEach(button => {
  button.onclick = () => {
    setIntent(button.dataset.intent);
    saveDraft();
  };
});

$('#attachmentInput').addEventListener('change', event => {
  addAttachments([...event.target.files]);
  event.target.value = '';
});

$('#attachmentPreview').addEventListener('click', event => {
  const button = event.target.closest?.('[data-attachment-id] .attachment-remove');
  if (button) removeAttachment(button.closest('[data-attachment-id]').dataset.attachmentId);
});

$('#selectedCommands').addEventListener('click', event => {
  const tool = event.target.closest?.('[data-remove-tool]');
  const skill = event.target.closest?.('[data-remove-skill]');
  if (tool) toggleTool(tool.dataset.removeTool);
  if (skill) toggleSkill(skill.dataset.removeSkill);
});

$('#commandTrigger').onclick = () => openCommandPanel();
$('#commandClose').onclick = closeCommandPanel;
$('.command-backdrop').onclick = closeCommandPanel;
$('#commandSearch').addEventListener('input', event => {
  commandPanelFilter = event.target.value;
  renderCommandPanel();
});
$('#commandList').addEventListener('click', event => {
  const item = event.target.closest?.('.command-item');
  if (!item) return;
  if (item.dataset.command) applyQuickCommand(item.dataset.command);
  else if (item.dataset.tool) toggleTool(item.dataset.tool);
  else if (item.dataset.skill) toggleSkill(item.dataset.skill);
});

document.addEventListener('keydown', event => {
  if (event.key !== 'Escape') return;
  if (!$('#riskPanel').classList.contains('hidden')) closeRiskConfirmation(false);
  else if (!$('#commandPanel').classList.contains('hidden')) closeCommandPanel();
});

async function copyText(value) {
  try {
    await navigator.clipboard.writeText(value);
  } catch {
    const area = document.createElement('textarea');
    area.value = value;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.append(area);
    area.select();
    document.execCommand('copy');
    area.remove();
  }
}

$('#diagnosticClose').onclick = () => $('#diagnostic').classList.add('hidden');
$('#diagnosticCopy').onclick = async () => {
  await copyText(lastDiagnosticText);
  toast('诊断信息已复制');
};
$('#diagnosticRefresh').onclick = async () => {
  try {
    await load();
    if (currentThread) await refreshCurrentThread(false);
    $('#diagnostic').classList.add('hidden');
    toast('已重新加载');
  } catch (error) { toast(error.message); }
};

['#message', '#messageScope', '#messagePolicy', '#messageDone'].forEach(selector => {
  const element = $(selector);
  element.addEventListener('input', saveDraft);
  element.addEventListener('change', saveDraft);
});

async function handleExecutionSelectionChange() {
  const previous = storedExecutionSettings();
  updatePermissionHint();
  if (!(await confirmExecutionSettings())) {
    applyExecutionSettings(Object.keys(previous).length ? previous : defaultExecutionSettings);
    return;
  }
  saveExecutionSettings();
}

$('#runtimePermission').addEventListener('change', handleExecutionSelectionChange);
$('#approvalMode').addEventListener('change', handleExecutionSelectionChange);
document.querySelectorAll('[data-execution-preset]').forEach(button => {
  button.addEventListener('click', () => applyExecutionPreset(button.dataset.executionPreset));
});

$('#message').addEventListener('input', event => {
  const value = event.target.value.trim();
  if (value === '/') openCommandPanel();
  else if (value === '/skill' || value === '/skills' || value === '/tools') openCommandPanel();
  else if (/^\$[^\s]*$/.test(value)) openCommandPanel(value.slice(1));
  else {
    const command = quickCommands.find(item => item.alias === value);
    if (command) applyQuickCommand(command.id);
  }
});

$('#message').addEventListener('compositionstart', () => { composing = true; });
$('#message').addEventListener('compositionend', () => { composing = false; });
$('#message').addEventListener('keydown', event => {
  if (!composing && event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
    event.preventDefault();
    $('#composer').requestSubmit();
  }
});

$('#composer').onsubmit = async event => {
  event.preventDefault();
  if (sending || !currentThread) return;
  if (currentRuntimeState?.isRunning === true && currentRuntimeState?.canControl === false) {
    toast('当前轮次正在运行，结束后即可从手机续接');
    return;
  }
  const rawCommand = $('#message').value.trim();
  if (/^\/skills?$/i.test(rawCommand) || rawCommand === '/tools' || /^\$[^\s]*$/.test(rawCommand)) {
    await openCommandPanel(rawCommand.startsWith('$') ? rawCommand.slice(1) : '');
    return;
  }
  const text = buildMessageText();
  if (!text) {
    $('#message').focus();
    return;
  }
  if (!(await confirmExecutionSettings())) return;
  const threadId = currentThread;
  const activeTurnId = currentActiveTurnId;
  const skills = selectedSkills.map(skill => ({ name: skill.name, path: skill.path }));
  sending = true;
  $('#sendMessage').disabled = true;
  $('#commandTrigger').disabled = true;
  $('#attachmentInput').disabled = true;
  $('#sendMessage').textContent = '发送中';
  try {
    const attachmentCount = attachmentsFor(threadId).length;
    if (attachmentCount) $('#sendMessage').textContent = `上传 ${attachmentCount} 个附件`;
    const uploadedFiles = await uploadAttachments(threadId);
    $('#sendMessage').textContent = '发送中';
    const payload = {
      text,
      attachmentIds: uploadedFiles.map(file => file.id),
      skills,
      clientUserMessageId: messageId(),
      ...executionSettings()
    };
    if (activeTurnId) {
      try {
        await api(`/threads/${encodeURIComponent(threadId)}/steer`, {
          method: 'POST', body: { turnId: activeTurnId, ...payload }
        });
      } catch (error) {
        if (![400, 404, 409].includes(error.status)) throw error;
        await api(`/threads/${encodeURIComponent(threadId)}/messages`, {
          method: 'POST', body: payload
        });
      }
    } else {
      await api(`/threads/${encodeURIComponent(threadId)}/messages`, {
        method: 'POST', body: payload
      });
    }
    if (threadId === currentThread) clearDraft();
    toast(activeTurnId ? '补充指令已发送' : '指令已发送');
    await refreshCurrentThread(true);
  } catch (error) {
    toast(error.message);
    saveDraft();
  } finally {
    sending = false;
    $('#sendMessage').disabled = false;
    $('#commandTrigger').disabled = false;
    $('#attachmentInput').disabled = false;
    updateComposerState();
  }
};

$('#interruptTurn').onclick = async () => {
  if (!currentThread || !currentActiveTurnId) return;
  if (!confirm('确定要停止当前正在运行的任务吗？')) return;
  const button = $('#interruptTurn');
  button.disabled = true;
  try {
    await api(`/threads/${encodeURIComponent(currentThread)}/interrupt`, {
      method: 'POST', body: { turnId: currentActiveTurnId }
    });
    toast('已请求停止任务');
    await refreshCurrentThread(true);
  } catch (error) {
    toast(error.message);
  } finally {
    button.disabled = false;
  }
};

async function startThread(path) {
  try {
    if (!(await confirmExecutionSettings())) return;
    const result = await api('/threads', { method: 'POST', body: { cwd: path, ...executionSettings() } });
    const id = result.thread?.id || result.id;
    if (id) openThread(id);
    else {
      toast('任务已建立');
      load();
    }
  } catch (error) {
    toast(error.message);
  }
}

$('#newThread').onclick = () => {
  const path = prompt('项目的 Windows 文件夹路径：');
  if (path) startThread(path);
};

async function approval(key, decision) {
  try {
    await api('/approvals/' + encodeURIComponent(key), { method: 'POST', body: { decision } });
    toast('已处理');
    await load();
    if (currentThread) await refreshCurrentThread(true);
  } catch (error) {
    toast(error.status === 404 ? '这项请求已在其他设备处理' : error.message);
    if (error.status === 404) await load();
  }
}

async function approveAllPending() {
  const count = supportedPendingApprovals().length;
  if (!count || approvalAutomationBusy) return;
  approvalAutomationBusy = true;
  renderApprovalControls();
  try {
    const result = await api('/approvals/approve-all', {
      method: 'POST', body: { decision: 'accept' }
    });
    const approved = Number(result?.approved || 0);
    const skipped = Number(result?.alreadyResolved || 0) + Number(result?.unsupported || 0);
    const failed = Number(result?.failed || 0);
    toast(approved
      ? `已允许 ${approved} 项${skipped ? `，跳过 ${skipped} 项` : ''}${failed ? `，失败 ${failed} 项` : ''}`
      : failed ? `${failed} 项审批未能处理` : '待审批已在其他设备处理');
    await load();
    if (currentThread) await refreshCurrentThread(true);
  } catch (error) {
    toast(error.message);
  } finally {
    approvalAutomationBusy = false;
    renderApprovalControls();
  }
}

async function setAutoApproveAll(enabled) {
  if (approvalAutomationBusy || approvalAutomation.supported === false) return;
  if (enabled && localStorage.getItem(autoApproveAcknowledgedKey) !== 'yes') {
    const accepted = await showRiskConfirmation({
      title: '自动允许所有后续审批？',
      message: '开启后，电脑端桥接服务会对 Console 管理的每一个支持审批自动选择 Yes，不再等你逐项检查。',
      consequences: ['命令、文件修改和细粒度权限请求都会被自动允许。', '这不是风险审核；桥接服务不会替你判断请求是否安全。', '其他 app-server 已开始的轮次会保持原有执行状态。', '普通问题和需要你填写的表单仍会等待你的回答。']
    });
    if (!accepted) {
      renderApprovalControls();
      return;
    }
    localStorage.setItem(autoApproveAcknowledgedKey, 'yes');
  }

  approvalAutomationBusy = true;
  renderApprovalControls();
  try {
    const result = await api('/approval-settings', {
      method: 'POST',
      body: {
        autoApproveAll: Boolean(enabled),
        ...(enabled ? { confirmation: 'AUTO APPROVE ALL' } : {})
      }
    });
    approvalAutomation = { ...approvalAutomation, ...(result?.settings || result), supported: true };
    const approvedNow = Number(result?.approvedNow?.approved || result?.approvedNow || 0);
    toast(enabled
      ? `自动批准已开启${approvedNow ? `，并允许了当前 ${approvedNow} 项` : ''}`
      : '自动批准已关闭');
    await load();
    if (currentThread) await refreshCurrentThread(true);
  } catch (error) {
    toast(error.message);
    try { await loadApprovalSettings(); } catch { /* Keep the last known state. */ }
  } finally {
    approvalAutomationBusy = false;
    renderApprovalControls();
    updatePermissionHint();
  }
}

$('#approveRecent').onclick = approveAllPending;
$('#approveAllPending').onclick = approveAllPending;
$('#autoApproveAll').addEventListener('change', event => {
  const enabled = event.target.checked;
  event.target.checked = approvalAutomation.autoApproveAll === true;
  setAutoApproveAll(enabled);
});
$('#manageAutoApprove').onclick = () => showPage('approvals');

async function stopProcess(pid) {
  const confirmation = prompt(`停止 PID ${pid} 会中断其工作。请输入 STOP ${pid} 确认：`);
  if (!confirmation) return;
  try {
    await api('/processes/' + pid + '/stop', { method: 'POST', body: { confirmation } });
    toast('停止指令已执行');
    load();
  } catch (error) {
    toast(error.message);
  }
}

$('#refresh').onclick = async () => {
  if (currentThread && $('#threadDetail').classList.contains('active')) await refreshCurrentThread(false);
  await Promise.all([load(), loadApprovalSettings()]);
};
$('#reloadProcesses').onclick = () => load();

$('#notificationAction').onclick = async () => {
  const mode = $('#notificationAction').dataset.mode;
  if (mode === 'disable') {
    notificationBusy = true;
    renderNotificationStatus();
    try {
      callNativeNotification('setEnabled', false);
      toast('后台任务通知已关闭');
    } finally {
      notificationBusy = false;
      setTimeout(() => refreshNativeNotificationStatus(), 150);
    }
    return;
  }
  if (mode === 'refresh') {
    refreshNativeNotificationStatus();
    if (notificationStatus.permission === 'blocked') toast('仍需要在系统设置中允许通知');
    return;
  }
  await enableNativeNotifications();
};

$('#notificationTest').onclick = () => {
  const result = callNativeNotification('testNotification');
  toast(result === 'unavailable' ? '测试通知暂时不可用' : '测试通知已发送');
};

$('#notificationSettings').onclick = () => callNativeNotification(
  $('#notificationSettings').dataset.target === 'battery' ? 'openBatterySettings' : 'openSettings');

async function boot() {
  try {
    await api('/session', { method: 'POST' });
    authenticated = true;
    if (token) {
      const nativeConfigured = !nativeNotificationBridge() ||
        callNativeNotification('configure', token) === 'configured';
      localStorage.removeItem(tokenKey);
      if (nativeConfigured) token = '';
      refreshNativeNotificationStatus();
    }
    await Promise.all([load(), loadApprovalSettings()]);
    await restoreNavigation(history.state, true);
  } catch (error) {
    if (error.status === 401) $('#pair').classList.remove('hidden');
    else toast(error.message);
  }
}

loadExecutionSettings();
initializeNavigation();
refreshNativeNotificationStatus();
boot();
setInterval(updatePendingCountdowns, 1000);
async function pollVisiblePage(force = false) {
  if (document.visibilityState !== 'visible' || !authenticated) return;
  const now = Date.now();
  if (force || now - lastApprovalSettingsRefreshAt >= 30000) {
    try { await loadApprovalSettings(); } catch { /* The next poll will retry. */ }
  }
  const page = activePage();
  if (page === 'threadDetail') {
    if (currentThread && !sending) {
      const detailInterval = currentRuntimeState?.isRunning === true ? 6000 : 15000;
      if (force || now - (lastThreadRefreshAt.get(currentThread) || 0) >= detailInterval) {
        try { await refreshCurrentThread(true); } catch { /* The next poll will retry. */ }
      }
    }
    // Pending approvals change independently from the visible conversation, but the
    // full summary/process scan does not need to run on every detail refresh.
    if (force || now - lastSummaryRefreshAt >= 30000) await load();
    return;
  }
  if (primaryPages.has(page) && (force || now - lastSummaryRefreshAt >= 15000)) await load();
}

setInterval(() => {
  pollVisiblePage().catch(() => { /* The next poll will retry. */ });
}, 4000);
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible') {
    pollVisiblePage(true).catch(() => { /* A later poll will retry. */ });
  }
});

if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js');
