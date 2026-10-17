// Caches the app shell so the courier can open the app with no signal. API calls are never cached here:
// the app keeps its own job list copy and its own queue of actions waiting to be sent.
const CACHE = 'nexa-courier-v2';
const SHELL = ['./', 'index.html', 'app.js', 'sync.js', 'style.css', 'manifest.webmanifest', 'icon.svg'];

self.addEventListener('install', e => { e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL))); self.skipWaiting(); });
self.addEventListener('activate', e => {
  e.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k)))));
  self.clients.claim();
});
self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== location.origin || !url.pathname.includes('/courier-app/')) return;
  e.respondWith(fetch(e.request).then(r => { caches.open(CACHE).then(c => c.put(e.request, r.clone())); return r; }).catch(() => caches.match(e.request)));
});
