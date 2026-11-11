// Kit start-up timing probe: when window.ApiClient appears, when the signed-in
// user appears, when each plugin's kit saw the client, and every request to a plugin route with the user id at the
// moment it was sent. Installed with webcheck's initScript step, so it runs at document start before any page script.
// It only observes: the ApiClient property is redefined as a plain value on its first assignment, and fetch/XHR are
// wrapped to record, then call through unchanged. Query strings are never recorded (they can carry api_key).
//
//   window.__kitBoot.summary(routes) -> { apiClientAt, userAt, kits: { <prefix>: {...} }, requests: [...], legacy }
(function () {
    'use strict';
    if (window.top !== window || window.__kitBoot) {
        return;
    }

    var now = function () { return Math.round(performance.now() * 10) / 10; };
    var state = { apiClientAt: null, apiClientHow: null, userAt: null, userId: null, kits: {}, legacy: null, requests: [] };

    function currentUserId() {
        try {
            var client = window.ApiClient;
            return client && typeof client.getCurrentUserId === 'function' ? client.getCurrentUserId() || null : null;
        } catch (e) {
            return null;
        }
    }

    // The exact moment jellyfin-web assigns window.ApiClient (measured: after DOMContentLoaded and after our deferred script).
    try {
        var existing = Object.getOwnPropertyDescriptor(window, 'ApiClient');
        if (!existing) {
            Object.defineProperty(window, 'ApiClient', {
                configurable: true,
                enumerable: true,
                get: function () { return undefined; },
                set: function (value) {
                    Object.defineProperty(window, 'ApiClient', { configurable: true, enumerable: true, writable: true, value: value });
                    if (value && state.apiClientAt === null) {
                        state.apiClientAt = now();
                        state.apiClientHow = 'setter';
                    }
                }
            });
        }
    } catch (e) {
        state.apiClientHow = 'poll';
    }

    function record(method, url) {
        var path;
        try {
            path = new URL(url, location.href).pathname;
        } catch (e) {
            path = String(url).split('?')[0];
        }

        state.requests.push({ t: now(), method: String(method || 'GET').toUpperCase(), path: path, userId: currentUserId() });
        if (state.requests.length > 500) {
            state.requests.shift();
        }
    }

    var originalFetch = window.fetch;
    if (typeof originalFetch === 'function') {
        window.fetch = function (input, init) {
            try {
                record((init && init.method) || (input && input.method) || 'GET', typeof input === 'string' ? input : (input && input.url) || String(input));
            } catch (e) {
                // never break the page
            }

            return originalFetch.apply(this, arguments);
        };
    }

    var originalOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url) {
        try {
            record(method, url);
        } catch (e) {
            // never break the page
        }

        return originalOpen.apply(this, arguments);
    };

    // Poll for the user and for each kit's own start marker (window.__jfkit[<prefix>].timing.client), plus Bookmarks
    // 1.0's marker (its hook flag on the ApiClient prototype) for a before/after comparison. setTimeout(0) is clamped to
    // about 4 ms, which bounds the probe's own error; the kit's timing values are the kit's own performance.now().
    var started = performance.now();
    (function poll() {
        var t = now();
        var client = window.ApiClient;
        if (client && state.apiClientAt === null) {
            state.apiClientAt = t;
            state.apiClientHow = state.apiClientHow || 'poll';
        }

        var userId = currentUserId();
        if (userId && state.userAt === null) {
            state.userAt = t;
            state.userId = userId;
        }

        var kits = window.__jfkit || {};
        Object.keys(kits).forEach(function (prefix) {
            var entry = kits[prefix];
            var seen = state.kits[prefix] || (state.kits[prefix] = { scriptSeenAt: t, clientSeenByProbeAt: null });
            if (entry && entry.timing && entry.timing.client !== null && entry.timing.client !== undefined && seen.clientSeenByProbeAt === null) {
                seen.clientSeenByProbeAt = t;
            }
        });

        try {
            if (client && state.legacy === null && Object.getPrototypeOf(client).__bookmarksHooked) {
                state.legacy = { bookmarksHookedAt: t };
            }
        } catch (e) {
            // ignore
        }

        if (performance.now() - started < 15000) {
            setTimeout(poll, 0);
        }
    })();

    window.__kitBoot = {
        state: state,
        summary: function (routes) {
            var prefixes = (routes || []).map(function (r) { return '/' + r.toLowerCase(); });
            var kits = {};
            Object.keys(state.kits).forEach(function (prefix) {
                var entry = (window.__jfkit || {})[prefix] || {};
                var timing = entry.timing || {};
                kits[prefix] = {
                    version: entry.version || null,
                    kit: entry.kit || null,
                    timing: timing,
                    startLagMs: typeof timing.client === 'number' && state.apiClientAt !== null ? Math.round((timing.client - state.apiClientAt) * 10) / 10 : null,
                    userLagMs: typeof timing.user === 'number' && state.userAt !== null ? Math.round((timing.user - state.userAt) * 10) / 10 : null
                };
            });
            var requests = state.requests.filter(function (r) {
                var p = r.path.toLowerCase();
                // <BaseUrl>/<Route> or <BaseUrl>/<Route>/..., but not the script tag's own ClientScript.
                return p.indexOf('/clientscript') < 0 && (prefixes.length === 0 || prefixes.some(function (prefix) {
                    return p.slice(-prefix.length) === prefix || p.indexOf(prefix + '/') >= 0;
                }));
            });
            return {
                apiClientAt: state.apiClientAt,
                apiClientHow: state.apiClientHow,
                userAt: state.userAt,
                signedIn: !!state.userId,
                kits: kits,
                legacy: state.legacy ? { bookmarksHookedAt: state.legacy.bookmarksHookedAt, lagMs: state.apiClientAt !== null ? Math.round((state.legacy.bookmarksHookedAt - state.apiClientAt) * 10) / 10 : null } : null,
                pluginRequests: requests,
                pluginRequestsWithoutUser: requests.filter(function (r) { return !r.userId; }).length,
                pluginRequestsBeforeUser: requests.filter(function (r) { return state.userAt === null || r.t < state.userAt; }).length
            };
        }
    };
})();
