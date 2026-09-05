const CACHE = 'codex-lan-v55';
const ASSETS = [
  '/', '/console-theme.css?v=2', '/notification-navigation.js?v=1', '/styles.css?v=53', '/mobile.css?v=53', '/app.js?v=54',
  '/thread-status.js?v=52', '/thread-resilience.js?v=52', '/thread-stream.js?v=52',
  '/model-settings.js?v=52',
  '/remote-artifacts.js?v=52', '/administrator-mode.js?v=52', '/marked.umd.js?v=52',
  '/manifest.webmanifest', '/icon.svg'
];

self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(ASSETS)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
  event.waitUntil(Promise.all([
    caches.keys().then(keys => Promise.all(keys.filter(key => key.startsWith('codex-lan-') && key !== CACHE).map(key => caches.delete(key)))),
    self.clients.claim()
  ]));
});

self.addEventListener('message', event => {
  if (event.data === 'SKIP_WAITING') self.skipWaiting();
});

self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  // The commute app has its own page and worker. Never replace it with Console
  // HTML on a transient network failure; that looks like a navigation no-op.
  if (event.request.method !== 'GET' || url.origin !== self.location.origin || url.pathname.startsWith('/api/') || url.pathname.startsWith('/commute')) return;
  event.respondWith(
    fetch(event.request, { cache: 'no-store' })
      .then(response => {
        if (response.ok && event.request.method === 'GET') {
          const copy = response.clone();
          caches.open(CACHE).then(cache => cache.put(event.request, copy)).catch(() => {});
        }
        return response;
      })
      .catch(() => caches.match(event.request).then(cached =>
        cached || (event.request.mode === 'navigate' ? caches.match('/') : Response.error())))
  );
});
