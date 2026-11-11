// Console probe ("the console shows no errors from our scripts"). Installed through
// webcheck's initScript step, so it runs before any page script. It records every console.error and console.warn call,
// uncaught error and unhandled rejection with its stack, and window.__consoleProbe.ours() returns the ones that come
// from a plugin script of ours: a stack frame or file in a /<Route>/ClientScript or /<Route>/AdminScript, or a message
// tagged [<Plugin>] (kit.report) or [<prefix>] (the kit's health warnings), for every plugin in window.__jfkit.
(function () {
    'use strict';
    if (window.__consoleProbe) {
        return;
    }

    const entries = [];
    const cut = (value, n) => String(value === undefined || value === null ? '' : value).slice(0, n);
    const describe = (args) => args.map((a) => {
        if (a instanceof Error) {
            return a.name + ': ' + a.message;
        }

        if (typeof a === 'string') {
            return a;
        }

        try {
            return JSON.stringify(a);
        } catch (e) {
            return String(a);
        }
    }).join(' ');

    ['error', 'warn'].forEach((level) => {
        const original = console[level];
        console[level] = function () {
            const args = Array.prototype.slice.call(arguments);
            const error = args.find((a) => a instanceof Error);
            entries.push({
                level,
                text: cut(describe(args), 500),
                stack: cut((error && error.stack) || new Error('console.' + level).stack, 3000),
                hash: location.hash,
                t: Math.round(performance.now())
            });
            return original.apply(this, arguments);
        };
    });

    window.addEventListener('error', (e) => {
        const target = e.target;
        if (target && target !== window && target.nodeType === 1) {
            // A resource that failed to load (script, image, stylesheet): the capture phase sees it with no message.
            entries.push({
                level: 'resource',
                text: cut(target.tagName.toLowerCase() + ' ' + (target.src || target.href || target.currentSrc || ''), 500),
                stack: '',
                hash: location.hash,
                t: Math.round(performance.now())
            });
            return;
        }

        entries.push({
            level: 'uncaught',
            text: cut(e.message, 500),
            stack: cut(e.error && e.error.stack, 3000),
            file: cut(e.filename, 500),
            hash: location.hash,
            t: Math.round(performance.now())
        });
    }, true);
    window.addEventListener('unhandledrejection', (e) => {
        const r = e.reason;
        entries.push({
            level: 'rejection',
            text: cut(r && r.message !== undefined ? r.message : r, 500),
            stack: cut(r && r.stack, 3000),
            hash: location.hash,
            t: Math.round(performance.now())
        });
    });

    const escape = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    function oursPattern() {
        const kits = window.__jfkit ? Object.keys(window.__jfkit).map((prefix) => ({ prefix, plugin: window.__jfkit[prefix].plugin })) : [];
        const parts = ['/ClientScript\\b', '/AdminScript\\b'];
        kits.forEach((k) => {
            parts.push('\\[' + escape(k.prefix) + '\\]');
            if (k.plugin) {
                parts.push('\\[' + escape(k.plugin) + '\\]');
            }
        });
        return new RegExp(parts.join('|'));
    }

    window.__consoleProbe = {
        entries,
        ours: () => {
            const re = oursPattern();
            return entries.filter((e) => re.test(e.text) || re.test(e.stack) || re.test(e.file || ''));
        },
        summary: () => ({
            total: entries.length,
            byLevel: entries.reduce((m, e) => Object.assign(m, { [e.level]: (m[e.level] || 0) + 1 }), {}),
            texts: entries.map((e) => e.level + ' ' + e.text.slice(0, 160))
        })
    };
})();
