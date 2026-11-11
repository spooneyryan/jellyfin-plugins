// Bookmarks requests probe (a page of ours asks only Bookmarks' endpoints). Installed
// through webcheck's initScript step, so it runs before any page script. It records every fetch and XMLHttpRequest whose
// call stack runs through the Bookmarks ClientScript (/Bookmarks/ClientScript, under any BaseUrl): direct when the
// caller is the script itself (the kit's api), else through jellyfin-web code the script called (ApiClient.getItem, a
// page event the kit dispatched). jellyfin-web's own requests (its home sections' /Items/Latest, still answering after
// the page opened on a fresh server) are never recorded, so a webcheck can assert on ours alone.
// window.__bookmarksRequests: { list: [{ kind, method, url, direct, t }], reset() }.
// A fetch wrapped after the probe (Access Roles' saved-sections watch, Jellyfin Enhanced's) used to stand between the
// caller and the probe, so the script's own requests counted as not direct (Bookmarks 1.2.1): window.fetch is now an
// accessor whose getter hands out a gate around whatever was last assigned, and the outermost gate of a call records it,
// its caller the frame right below it; the gates a wrapper reaches through the fetch it kept record nothing more.
(function () {
    'use strict';
    if (window.__bookmarksRequests) {
        return;
    }

    const SCRIPT = /\/Bookmarks\/ClientScript\b/;
    const list = [];
    window.__bookmarksRequests = {
        list: list,
        reset: function () {
            list.length = 0;
        }
    };

    // The stack's frames below this probe's own: the first is the caller.
    function callers() {
        const limit = Error.stackTraceLimit;
        Error.stackTraceLimit = 100;
        try {
            const lines = String(new Error().stack || '').split('\n').slice(1).filter((line) => /^\s*at /.test(line));
            // This function, record() and the wrapper.
            return lines.slice(3);
        } finally {
            Error.stackTraceLimit = limit;
        }
    }

    function record(kind, url, method) {
        try {
            const frames = callers();
            if (!frames.some((line) => SCRIPT.test(line))) {
                return;
            }

            list.push({
                kind: kind,
                method: String(method || 'GET').toUpperCase(),
                url: new URL(String(url), location.href).href,
                direct: frames.length > 0 && SCRIPT.test(frames[0]),
                t: Math.round(performance.now())
            });
        } catch (e) {
            // Never in the way of the request.
        }
    }

    // The gate around a fetch: the outermost one of a call records it (callers() skips callers, record and this gate), then
    // calls the fetch it stands for; the wrapper's own properties (Access Roles' __jfarKept) are copied onto it.
    let inside = false;
    function gate(fn) {
        const gated = function (input, init) {
            if (inside) {
                return fn.apply(this, arguments);
            }

            inside = true;
            try {
                const request = input && typeof input === 'object' && 'url' in input ? input : null;
                record('fetch', request ? request.url : input, (init && init.method) || (request && request.method));
                return fn.apply(this, arguments);
            } finally {
                inside = false;
            }
        };
        Object.getOwnPropertyNames(fn).forEach((name) => {
            if (['length', 'name', 'prototype', 'arguments', 'caller'].indexOf(name) < 0) {
                try {
                    Object.defineProperty(gated, name, Object.getOwnPropertyDescriptor(fn, name));
                } catch (e) {
                    // A property that can't be copied.
                }
            }
        });
        return gated;
    }

    const fetch0 = window.fetch;
    if (typeof fetch0 === 'function') {
        let current = gate(fetch0);
        try {
            Object.defineProperty(window, 'fetch', {
                configurable: true,
                enumerable: true,
                get: function () {
                    return current;
                },
                set: function (value) {
                    current = typeof value === 'function' ? gate(value) : value;
                }
            });
        } catch (e) {
            window.fetch = current;
        }
    }

    const open0 = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url) {
        record('xhr', url, method);
        return open0.apply(this, arguments);
    };
})();
