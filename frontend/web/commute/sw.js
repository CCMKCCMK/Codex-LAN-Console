const CACHE='triton-daily-v5';
const ASSETS=['/commute/','/console-theme.css?v=2','/notification-navigation.js?v=1','/commute/commute.css?v=3','/commute/commute.js?v=5','/commute/scooter.js?v=1','/commute/scooter.css?v=1','/commute/vendor/leaflet.js','/commute/vendor/leaflet.css','/icon.svg'];
self.addEventListener('install',e=>e.waitUntil(caches.open(CACHE).then(c=>c.addAll(ASSETS)).then(()=>self.skipWaiting())));
self.addEventListener('activate',e=>e.waitUntil(caches.keys().then(keys=>Promise.all(keys.filter(k=>k.startsWith('triton-daily-')&&k!==CACHE).map(k=>caches.delete(k)))).then(()=>self.clients.claim())));
self.addEventListener('fetch',e=>{
  const url=new URL(e.request.url);
  if(url.origin!==location.origin||!(url.pathname.startsWith('/commute/')||url.pathname==='/notification-navigation.js'||url.pathname==='/console-theme.css'||url.pathname==='/icon.svg')||e.request.method!=='GET')return;
  e.respondWith(fetch(e.request).then(r=>{if(r.ok)caches.open(CACHE).then(c=>c.put(e.request,r.clone()));return r;}).catch(()=>caches.match(e.request).then(r=>r||Response.error())));
});
