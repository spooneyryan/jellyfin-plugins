// Frame probe for the kit's page hosting, promoted from an earlier prototype's probe. Loaded
// with webcheck's initScript step, so it runs in every document before any page script, and samples every animation
// frame (requestAnimationFrame runs right before paint):
//   - a flash frame: FallbackRoute's own "Page not found" heading is rendered while the location is one of the plugin's
//     pages (its fallback route #/<prefix>-<key>, or a Plugin Pages link to its shell /<Route>/Pages/<key>), or while
//     #fallbackPage still holds the plugin's page root, shown or not (the prototype's definition: leaving for another
//     unknown route must not show "Page not found" before the hand-back has removed our root and restored the title);
//   - the header title (.skinHeader .pageTitle) and document.title on each frame, as run-length transitions;
//   - the view events, as marks.
// The plugin defaults to Bookmarks (prefix jfbm, route Bookmarks); an earlier initScript can set
// window.__pagesProbeConfig = { prefix, route } for another one. Read it with __pagesProbe.summary().
(function () {
    'use strict';
    if (window.__pagesProbe) {
        return;
    }

    var config = window.__pagesProbeConfig || {};
    var prefix = config.prefix || 'jfbm';
    var route = config.route || 'Bookmarks';
    var pageAttr = 'data-' + prefix + '-page';
    var routePattern = new RegExp('^/' + prefix + '-([a-z0-9-]+)$');
    var shellPattern = new RegExp('^/' + route + '/Pages/([a-z0-9-]+)');
    var t0 = performance.now();
    var MAX = 1500;
    var last = null;
    var P = window.__pagesProbe = {
        prefix: prefix,
        started: new Date().toISOString(),
        frames: 0,
        transitions: [],
        flash: [],
        events: [],
        overflow: false,
        mark: function (kind, detail) {
            push(P.events, { t: now(), kind: kind, detail: detail === undefined ? null : String(detail).slice(0, 300), hash: location.hash });
        },
        reset: function () {
            P.frames = 0;
            P.transitions = [];
            P.flash = [];
            P.events = [];
            P.overflow = false;
            last = null;
            t0 = performance.now();
        },
        summary: function () {
            var titles = {};
            var blank = 0;
            var ourFrames = 0;
            P.transitions.forEach(function (tr) {
                if (tr.loc) {
                    ourFrames += tr.frames;
                    titles[tr.title] = (titles[tr.title] || 0) + tr.frames;
                    if (!tr.root && !tr.nf) {
                        blank += tr.frames;
                    }
                }
            });
            return {
                frames: P.frames,
                framesOnOurRoutes: ourFrames,
                flashFrames: P.flash.length,
                firstFlash: P.flash[0] || null,
                headerTitlesOnOurRoutes: titles,
                framesOnOurRoutesWithoutContent: blank,
                transitions: P.transitions.length,
                overflow: P.overflow
            };
        }
    };

    function now() {
        return Math.round(performance.now() - t0);
    }

    function push(list, item) {
        if (list.length < MAX) {
            list.push(item);
        } else {
            P.overflow = true;
        }
    }

    // The page key the location names, or null.
    function ourLocation() {
        var hash = location.hash.replace(/^#!?/, '');
        var q = hash.indexOf('?');
        var path = q >= 0 ? hash.slice(0, q) : hash;
        var m = routePattern.exec(path);
        if (m) {
            return m[1];
        }

        if (/^\/userpluginsettings(\.html)?$/.test(path)) {
            var k = shellPattern.exec(new URLSearchParams(q >= 0 ? hash.slice(q + 1) : '').get('pageUrl') || '');
            if (k) {
                return k[1];
            }
        }

        return null;
    }

    function shown(node) {
        return !!node && node.getClientRects().length > 0 && getComputedStyle(node).visibility !== 'hidden';
    }

    function sample() {
        var fp = document.getElementById('fallbackPage');
        var nf = false;
        var ours = false;
        if (fp) {
            for (var i = 0; i < fp.children.length; i++) {
                var c = fp.children[i];
                if (c.hasAttribute(pageAttr)) {
                    ours = true;
                } else if (!nf) {
                    nf = shown(c.querySelector('h1'));
                }
            }
        }

        var root = null;
        var roots = document.querySelectorAll('[' + pageAttr + ']');
        for (var j = 0; j < roots.length && !root; j++) {
            if (shown(roots[j])) {
                root = roots[j].getAttribute(pageAttr) + (roots[j].hasAttribute('data-' + prefix + '-mounted') ? '' : '(unmounted)');
            }
        }

        var titleEl = document.querySelector('.skinHeader .pageTitle');
        return {
            hash: location.hash,
            loc: ourLocation(),
            title: titleEl ? titleEl.textContent.trim() : null,
            docTitle: document.title,
            nf: nf,
            ours: ours,
            root: root
        };
    }

    function frame() {
        P.frames++;
        try {
            var s = sample();
            if (s.nf && (s.loc || s.ours)) {
                push(P.flash, { t: now(), hash: s.hash, loc: s.loc, ours: s.ours });
            }

            var key = [s.hash, s.title, s.docTitle, s.nf, s.ours, s.root].join('|');
            if (last && last.key === key) {
                last.item.frames++;
            } else {
                var item = { t: now(), frames: 1, hash: s.hash, loc: s.loc, title: s.title, docTitle: s.docTitle, nf: s.nf, ours: s.ours, root: s.root };
                push(P.transitions, item);
                last = { key: key, item: item };
            }
        } catch (e) {
            P.error = String(e);
        }

        requestAnimationFrame(frame);
    }

    requestAnimationFrame(frame);

    ['viewbeforeshow', 'viewshow', 'pageshow', 'viewhide'].forEach(function (type) {
        document.addEventListener(type, function (e) {
            var t = e.target;
            var name = t && t.id ? '#' + t.id : (t && typeof t.className === 'string' ? '.' + t.className.trim().split(/\s+/).slice(0, 3).join('.') : '?');
            P.mark('event:' + type, name + (e.detail && e.detail.isRestored ? ' (restored)' : ''));
        }, true);
    });
    window.addEventListener('popstate', function () { P.mark('popstate', location.hash); }, true);
    window.addEventListener('hashchange', function () { P.mark('hashchange', location.hash); }, true);
})();
