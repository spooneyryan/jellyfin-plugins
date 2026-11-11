// Which URLs the sandbox tools may reach (scripts/README.md "Safety rules"). Node 22 built-ins only.
//
// - Loopback hosts only, never the real server's port (PRODUCTION_PORTS: 8096).
// - Not even on loopback: routes that make the sandbox server itself download from the internet. Jellyfin Enhanced's
//   CDN proxy (GET /JellyfinEnhanced/cdn/{source}/{path}, CdnAssetService.GetAsync in JE 12.8) fetches every asset its
//   cache lacks from jsDelivr, flagcdn, Wikimedia and others, and a sandbox keeps that cache empty (its "Refresh CDN
//   Assets" task has no triggers there). JE's client asks for such assets when a feature needs one: audio-language
//   flags on details pages (ShowAudioLanguages, on by default), theme colours, icons, award logos, and Jellyfin
//   Elsewhere's region and provider lists (with TMDB configured). JE carries on without them when the request fails.

export const PRODUCTION_PORT = 8096;

// Every port the real server listens on (only 8096): no sandbox tool, probe or webcheck may reach one. The one list
// they all use. 8097 is the shared `default` sandbox's port, not the real server's.
export const PRODUCTION_PORTS = Object.freeze([PRODUCTION_PORT]);

// Whether a port is one of the real server's.
export function isProductionPort(port) {
    return PRODUCTION_PORTS.includes(Number(port));
}

const LOOPBACK_HOSTS = ['127.0.0.1', 'localhost', '::1'];
const PAGE_PROTOCOLS = ['data:', 'blob:', 'about:', 'chrome:', 'devtools:', 'chrome-extension:'];

// Matched against the URL's path, after any BaseUrl or legacy /emby prefix; ASP.NET routes ignore case.
export const SERVER_DOWNLOAD_ROUTES = [
    { name: 'Jellyfin Enhanced CDN proxy', pattern: /\/JellyfinEnhanced\/cdn(\/|$)/i }
];

// Throws unless the URL is on a loopback host and not on one of the real server's ports.
export function assertSafeUrl(url) {
    const parsed = new URL(url);
    const host = parsed.hostname.replace(/^\[|\]$/g, '');
    if (!LOOPBACK_HOSTS.includes(host)) throw new Error(`Refusing ${url}: loopback only`);
    const port = Number(parsed.port || (parsed.protocol === 'https:' ? 443 : 80));
    if (isProductionPort(port)) throw new Error(`Refusing ${url}: port ${port} is the real server's`);
    return parsed;
}

// The server-download route a URL's path hits, or null.
export function serverDownloadRoute(url) {
    let parsed;
    try { parsed = new URL(url); } catch { return null; }
    let pathname = parsed.pathname;
    try { pathname = decodeURIComponent(pathname); } catch { /* keep the raw path */ }
    const route = SERVER_DOWNLOAD_ROUTES.find((r) => r.pattern.test(pathname) || r.pattern.test(parsed.pathname));
    return route ? route.name : null;
}

// Whether a page request may go out: page-internal URLs, and loopback URLs that are neither the real server's ports nor a route
// that makes the server download from the internet. allowExternal lifts every restriction (webcheck --allow-external).
export function isAllowedRequest(url, { allowExternal = false } = {}) {
    if (allowExternal) return true;
    let parsed;
    try { parsed = new URL(url); } catch { return false; }
    if (PAGE_PROTOCOLS.includes(parsed.protocol)) return true;
    try { assertSafeUrl(url); } catch { return false; }
    return serverDownloadRoute(url) === null;
}
