// Published service worker: makes the app installable and makes the shell load from cache.
//
// self.assetsManifest is emitted at publish time by the ServiceWorkerAssetsManifest property in
// the client csproj. It carries a content hash per asset, so a new deploy produces a new cache
// name and the old cache is dropped wholesale rather than being reconciled entry by entry.

self.importScripts('./service-worker-assets.js');

self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'porepolinetracker-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;

// The app shell only. Everything the Blazor runtime needs to boot, plus the static assets the
// first paint depends on.
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff2?$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/];
const offlineAssetsExclude = [/^service-worker\.js$/];

// Paths that must NEVER be answered from the cached shell.
//
// This is the whole reason this file is not the stock template. The template answers every
// navigation with index.html, which for this app would break sign-in outright: the GitHub OAuth
// round-trip is a sequence of top-level NAVIGATIONS through /auth/login and the provider's
// callback, and serving the Blazor shell in place of any of them strands the user on a page that
// thinks it is still logged out. /api and /hubs are here for the same reason — a cached shell
// returned to an XHR or a SignalR negotiate is a 200 carrying HTML, which fails in whatever way
// the caller happens to parse it, rather than the 401 the caller knows how to handle.
// /badge/ and /share/ are server-rendered public pages: opened by the owner in a tab the worker
// controls, a share link would otherwise show the app shell instead of the page they shared.
const serverRoutePrefixes = ['/api/', '/auth/', '/hubs/', '/health', '/signin-', '/signout-', '/badge/', '/share/'];
const apiCacheName = 'porepolinetracker-api-cache';
// /auth/me is in the list because the app asks it "who am I?" before rendering anything. With no
// cached answer an offline cold start failed that request, the app concluded "signed out" and
// redirected to /login — so the cached repositories and insights behind it were unreachable.
// Network-first like the rest: a live 401 is returned as-is, never replaced by a stale 200.
// The whole cache is per-user and is deleted on logout (LoginDisplay.razor).
const offlineApiEndpoints = ['/api/repositories', '/api/insights/portfolio', '/auth/me'];

async function onInstall() {
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));

    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
}

async function onActivate() {
    // Every deploy changes the manifest version and so the cache name. Anything else under our
    // prefix is a previous deploy's shell and is dead weight.
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
}

async function onFetch(event) {
    const url = new URL(event.request.url);

    // Feature 3: Stale-while-revalidate / network-first for read-only portfolio & repo data
    // Allows viewing dashboard offline when disconnected.
    if (event.request.method === 'GET' && url.origin === self.location.origin && offlineApiEndpoints.includes(url.pathname)) {
        return fetch(event.request)
            .then(async response => {
                if (response && response.ok) {
                    const cache = await caches.open(apiCacheName);
                    cache.put(event.request, response.clone());
                }
                return response;
            })
            .catch(async () => {
                const cache = await caches.open(apiCacheName);
                const cached = await cache.match(event.request);
                if (cached) return cached;
                throw new Error('Offline and no cached data available.');
            });
    }

    // Same-origin GETs only, and never a server route. A cross-origin request (the Google Fonts
    // stylesheets, for instance) is left to the network untouched, and a POST/PUT/DELETE has no
    // cacheable answer by definition.
    const isServerRoute = url.origin === self.location.origin
        && serverRoutePrefixes.some(prefix => url.pathname.startsWith(prefix));

    if (event.request.method !== 'GET' || isServerRoute) {
        return fetch(event.request);
    }

    // A client-side route (/insights, /code-health, /repositories/<guid>) has no file behind it —
    // the server answers it with index.html via MapFallbackToFile, and so do we.
    const shouldServeIndexHtml = event.request.mode === 'navigate';
    const request = shouldServeIndexHtml ? 'index.html' : event.request;

    const cache = await caches.open(cacheName);
    const cachedResponse = await cache.match(request);

    // Network is still the fallback, not the other way round: an asset added after the cached
    // deploy (or excluded from it) must still load rather than 404 from an empty cache hit.
    return cachedResponse || fetch(event.request);
}
