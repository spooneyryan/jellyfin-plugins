// The star widget of User Ratings on a real details page, for the userratings-stars-*.json
// webchecks (scripts/webchecks/gen-stars.mjs writes them). Loaded by webcheck's initScript into every document before any
// page script, so its first key listener runs before jellyfin-web's and Jellyfin Enhanced's.
//
// window.__jfurStars.mount() takes the Web/js/30-stars.js part of the ClientScript the server serves (the script tag's own
// URL, as the kit's EmbeddedScript labels each file) and runs it with User Ratings' own kit (window.__jfkit.jfur.instance:
// its strings, its styles already in the page): the code under test is what ships, and the shipped script exposes nothing
// for it. mount() then builds the details section's layout: "Your rating" and the slider alone in its row, Clear and "Write a review"
// on the next, read-only stars below; before #castCollapsible, the sectionBefore anchor. onCommit stands in for the
// page's PUT: it records the value and answers after answerMs (failing the next `fail` saves). Nothing here calls User
// Ratings' API.
//
// Since 0.3.5 the details page mounts its own section (40-details.js; the userratings-details-*.json webchecks test
// it), whose slider would come first on Tab and on the D-pad. So the probe answers the section's two reads itself, before
// they leave the page: Capabilities and the item's read get a 404, as for an item that can't be rated, and the section
// shows nothing; probe.stubbed counts them (the webchecks assert the section asked and no request to
// User Ratings' routes reached the server but the script).
//
// window.__jfurStars:
//   mount({ value, rtl, disabled })  the section on the shown details page (idempotent: a second call replaces it); rtl
//            sets dir="rtl" on <html> and <body>, as jellyfin-web does for a right-to-left language; direction('ltr') undoes it
//   state(), styles(), collisions(), jeReviews()   what the webchecks assert
//   log      key events at the window's capture phase (first of all), on the slider after its own listener, and at the
//            window's bubble phase (where jellyfin-web's keyboard navigation listens; prevented there means it skips the
//            key); focus moves; pointer events on the stars; value changes; saves
(function () {
    'use strict';

    if (window.__jfurStars) {
        return;
    }

    var t0 = performance.now();
    var probe = window.__jfurStars = { log: [], commits: [], fail: 0, answerMs: 150, widget: null, api: null, stubbed: [] };

    // The details section's reads answered here with a 404, so its own slider stays out of the page under test.
    var originalFetch = window.fetch.bind(window);
    window.fetch = function (input, init) {
        var url = typeof input === 'string' ? input : (input && input.url) || '';
        var method = ((init && init.method) || 'GET').toUpperCase();
        if (method === 'GET' && /\/UserRatings\/(Capabilities|Items\/[0-9a-f-]+)(\?|$)/i.test(url)) {
            probe.stubbed.push(url.replace(/^.*?(\/UserRatings\/)/i, '$1').split('?')[0]);
            return Promise.resolve(new Response(JSON.stringify({ title: 'Not Found', status: 404, detail: 'The star probe keeps the details section out.' }), { status: 404, headers: { 'Content-Type': 'application/problem+json' } }));
        }

        return originalFetch(input, init);
    };

    function now() {
        return Math.round(performance.now() - t0);
    }

    function describe(el) {
        if (!el || el.nodeType !== 1) {
            return String(el);
        }

        var cls = typeof el.className === 'string' ? el.className.split(/\s+/).filter(Boolean).slice(0, 3).join('.') : '';
        var text = (el.getAttribute('aria-label') || el.getAttribute('title') || el.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 30);
        return el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + (cls ? '.' + cls : '') + (text ? ' "' + text + '"' : '');
    }

    probe.push = function (kind, data) {
        var entry = Object.assign({ t: now(), kind: kind }, data || {});
        probe.log.push(entry);
        return entry;
    };

    window.addEventListener('keydown', function (e) {
        probe.push('key-capture', { key: e.key, keyCode: e.keyCode, prevented: e.defaultPrevented, target: describe(e.target) });
    }, true);
    window.addEventListener('keydown', function (e) {
        probe.push('key-bubble', { key: e.key, prevented: e.defaultPrevented, target: describe(e.target) });
    }, false);
    document.addEventListener('focusin', function (e) {
        probe.push('focus', { target: describe(e.target) });
    }, true);
    ['pointerdown', 'pointermove', 'pointerup', 'pointercancel'].forEach(function (type) {
        window.addEventListener(type, function (e) {
            if (e.target && e.target.closest && e.target.closest('.jfur-stars') && (type !== 'pointermove' || e.pointerType !== 'mouse')) {
                probe.push('pointer', { type: type, pointerType: e.pointerType, x: Math.round(e.clientX * 100) / 100, y: Math.round(e.clientY * 100) / 100 });
            }
        }, true);
    });

    function el(tag, cls, text) {
        var node = document.createElement(tag);
        if (cls) {
            node.className = cls;
        }

        if (text) {
            node.textContent = text;
        }

        return node;
    }

    // The 30-stars.js part of the ClientScript the page loaded, run with User Ratings' kit.
    probe.load = function () {
        if (probe.api) {
            return Promise.resolve(probe.api);
        }

        var entry = window.__jfkit && window.__jfkit.jfur;
        var tag = document.querySelector('script[data-jfur-plugin]');
        if (!entry || !tag) {
            return Promise.reject(new Error('User Ratings\' ClientScript is not loaded'));
        }

        return fetch(tag.src, { cache: 'no-store' }).then(function (r) {
            return r.text();
        }).then(function (text) {
            var marker = '// ---- Web/js/30-stars.js\n';
            var start = text.indexOf(marker);
            var end = text.indexOf('\n// ---- ', start + marker.length);
            if (start < 0 || end < 0) {
                throw new Error('no Web/js/30-stars.js in ' + tag.src);
            }

            // eslint-disable-next-line no-new-func
            probe.api = new Function('kit', text.substring(start + marker.length, end)
                + '\nreturn { createStarSlider: createStarSlider, renderStars: renderStars, valueFromPointer: valueFromPointer };')(entry.instance);
            probe.version = entry.version;
            return probe.api;
        });
    };

    probe.mount = function (opts) {
        opts = opts || {};
        return probe.load().then(function (api) {
            var page = document.querySelector('#itemDetailPage:not(.hide)');
            if (!page) {
                return { ok: false, why: 'no shown details page' };
            }

            var old = document.querySelector('.jfur-probe-section');
            if (old) {
                if (probe.widget) {
                    probe.widget.destroy();
                }

                old.remove();
            }

            if (opts.rtl) {
                probe.direction('rtl');
            }

            var content = page.querySelector('.detailPageSecondaryContainer .detailPageContent') || page.querySelector('.detailPageContent');
            var before = content && (content.querySelector('#castCollapsible') || content.querySelector('#guestCastCollapsible') || content.querySelector('#similarCollapsible'));
            var section = el('div', 'verticalSection detailVerticalSection jfur-probe-section');
            section.setAttribute('data-jfur-item', (location.hash.match(/id=([0-9a-f]+)/) || [])[1] || '');
            section.appendChild(el('h2', 'sectionTitle', 'Member ratings & reviews'));
            var label = el('span', 'jfur-probe-label', 'Your rating');
            label.id = 'jfur-probe-label';
            probe.commits = [];
            probe.widget = api.createStarSlider({
                value: opts.value || 0,
                labelledBy: label.id,
                onCommit: function (value) {
                    var c = probe.push('commit', { value: value });
                    probe.commits.push(c);
                    return new Promise(function (resolve, reject) {
                        setTimeout(function () {
                            if (probe.fail > 0) {
                                probe.fail--;
                                probe.push('save-failed', { value: value });
                                reject(new Error('probe: the save failed'));
                            } else {
                                probe.push('saved', { value: value });
                                resolve({});
                            }
                        }, probe.answerMs);
                    });
                },
                onChange: function (value, how) {
                    probe.push('change', { value: value, how: how });
                }
            });
            var row = el('div', 'jfur-probe-row');
            row.style.cssText = 'display:flex;align-items:center;gap:1em;flex-wrap:wrap;margin:.6em 0';
            row.appendChild(label);
            row.appendChild(probe.widget.element);
            section.appendChild(row);
            var row2 = el('div', 'jfur-probe-row');
            row2.style.cssText = 'display:flex;gap:.6em;margin:.6em 0';
            var clear = el('button', 'raised jfur-probe-clear', 'Clear');
            clear.type = 'button';
            clear.setAttribute('is', 'emby-button');
            clear.addEventListener('click', function () {
                probe.widget.setValue(0);
                probe.widget.announce('Rating cleared');
                probe.push('clear');
            });
            var write = el('button', 'raised jfur-probe-write', 'Write a review');
            write.type = 'button';
            write.setAttribute('is', 'emby-button');
            row2.appendChild(clear);
            row2.appendChild(write);
            section.appendChild(row2);
            var row3 = el('div', 'jfur-probe-row jfur-probe-display');
            row3.style.cssText = 'display:flex;align-items:center;gap:1.5em;margin:.6em 0';
            probe.display = api.renderStars(4.2, { label: 'Member average 4.2 out of 5, 7 ratings', showValue: true });
            row3.appendChild(probe.display);
            row3.appendChild(api.renderStars(3.5));
            section.appendChild(row3);
            if (before && before.parentNode === content) {
                content.insertBefore(section, before);
            } else {
                content.appendChild(section);
            }

            if (opts.disabled) {
                probe.widget.setDisabled(opts.disabled);
            }

            var r = probe.widget.slider.getBoundingClientRect();
            return {
                ok: true,
                version: probe.version,
                layout: document.documentElement.className.split(/\s+/).filter(function (c) { return c.indexOf('layout-') === 0; }),
                dir: document.documentElement.getAttribute('dir'),
                placedBefore: before ? describe(before) : 'appended',
                slider: { x: r.left, y: r.top, w: r.width, h: r.height }
            };
        });
    };

    // The page's direction as jellyfin-web sets it for the UI language: dir on <html> and on <body>.
    probe.direction = function (dir) {
        document.documentElement.setAttribute('dir', dir);
        document.body.setAttribute('dir', dir);
        return getComputedStyle(document.body).direction;
    };

    probe.onSlider = function () {
        return !!probe.widget && document.activeElement === probe.widget.slider;
    };

    probe.active = function () {
        return describe(document.activeElement);
    };

    probe.since = function (t) {
        return probe.log.filter(function (e) {
            return e.t >= t;
        });
    };

    probe.state = function () {
        var w = probe.widget;
        var s = w.slider;
        return {
            value: w.value(),
            saved: w.savedValue(),
            valuenow: s.getAttribute('aria-valuenow'),
            valuetext: s.getAttribute('aria-valuetext'),
            shown: w.element.querySelector('.jfur-stars-value').textContent,
            fills: Array.prototype.map.call(s.querySelectorAll('.jfur-star-fill'), function (f) { return f.style.width; }),
            live: w.element.querySelector('.jfur-stars-live').textContent,
            disabled: s.getAttribute('aria-disabled'),
            reason: s.getAttribute('data-jfur-reason'),
            onSlider: probe.onSlider(),
            active: probe.active(),
            commits: probe.commits.map(function (c) { return c.value; }),
            classes: w.element.className,
            scrollX: window.scrollX,
            scrollY: Math.round(window.scrollY),
            hash: location.hash.slice(0, 60)
        };
    };

    // The pointer's x on the stars for a value's half boundary: x = left + width × value / 5 (the middle of star 3 is 2.5).
    probe.xFor = function (stars, rtl) {
        var r = probe.widget.slider.getBoundingClientRect();
        return rtl ? r.right - r.width * stars / 5 : r.left + r.width * stars / 5;
    };

    probe.box = function (selector) {
        var node = typeof selector === 'string' ? document.querySelector(selector) : selector;
        var r = node.getBoundingClientRect();
        return { x: r.left, y: r.top, w: r.width, h: r.height, right: r.right, cx: r.left + r.width / 2, cy: r.top + r.height / 2 };
    };

    function rgb(text) {
        var m = /rgba?\(([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?\)/.exec(text || '');
        return m ? { r: +m[1], g: +m[2], b: +m[3], a: m[4] === undefined ? 1 : +m[4] } : null;
    }

    function luminance(c) {
        var f = function (v) {
            v /= 255;
            return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
        };
        return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
    }

    function contrast(a, b) {
        var l1 = luminance(a);
        var l2 = luminance(b);
        return Math.round(((Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05)) * 100) / 100;
    }

    // The colour behind an element: the first ancestor with an opaque background.
    function backgroundOf(node) {
        for (var n = node; n && n.nodeType === 1; n = n.parentNode) {
            var c = rgb(getComputedStyle(n).backgroundColor);
            if (c && c.a > 0.9) {
                return c;
            }
        }

        return rgb(getComputedStyle(document.documentElement).backgroundColor) || { r: 255, g: 255, b: 255, a: 1 };
    }

    probe.styles = function () {
        var w = probe.widget;
        var s = w.slider;
        var root = w.element;
        var cs = function (node) { return getComputedStyle(node); };
        var round = function (v) { return Math.round(v * 100) / 100; };
        var box = function (node) { var b = node.getBoundingClientRect(); return { x: round(b.left), right: round(b.right), w: round(b.width), h: round(b.height) }; };
        var fills = s.querySelectorAll('.jfur-star-fill');
        var stroke = cs(s.querySelector('.jfur-star-off polygon')).stroke;
        var onFill = cs(s.querySelector('.jfur-star-on polygon')).fill;
        var bg = backgroundOf(root);
        var displayed = function (node) { return cs(node).display !== 'none' && node.getClientRects().length > 0; };
        var focusables = Array.prototype.filter.call(root.parentNode.querySelectorAll('button, a, input, select, textarea, [tabindex], .focusable'), function (n) {
            return displayed(n) && !n.disabled;
        });
        return {
            touchAction: cs(s).touchAction,
            cursor: cs(s).cursor,
            userSelect: cs(s).userSelect || cs(s).webkitUserSelect,
            star: box(s.querySelector('.jfur-star')),
            slider: box(s),
            minus: cs(root.querySelector('.jfur-stars-minus')).display,
            plus: cs(root.querySelector('.jfur-stars-plus')).display,
            minusBox: displayed(root.querySelector('.jfur-stars-minus')) ? box(root.querySelector('.jfur-stars-minus')) : null,
            coarse: matchMedia('(pointer: coarse)').matches,
            hoverNone: matchMedia('(hover: none)').matches,
            outline: { style: cs(s).outlineStyle, width: cs(s).outlineWidth, offset: cs(s).outlineOffset, color: cs(s).outlineColor },
            focused: document.activeElement === s,
            focusVisible: s.matches(':focus-visible'),
            valueFontSize: cs(root.querySelector('.jfur-stars-value')).fontSize,
            // Where the value's text itself is drawn (not its box): beside the stars on their end side.
            valueInk: (function () { var range = document.createRange(); range.selectNodeContents(root.querySelector('.jfur-stars-value')); return box(range); })(),
            rowFontSize: cs(root).fontSize,
            fillTransition: cs(fills[0]).transitionDuration,
            stroke: stroke,
            onFill: onFill,
            offFill: cs(s.querySelector('.jfur-star-off polygon')).fill,
            forcedColorAdjust: cs(s.querySelector('.jfur-star')).forcedColorAdjust,
            background: bg,
            contrast: { stroke: rgb(stroke) ? contrast(rgb(stroke), bg) : null, fill: rgb(onFill) ? contrast(rgb(onFill), bg) : null },
            isolate: Array.prototype.map.call(root.querySelectorAll('.jfur-isolate'), function (n) {
                return { cls: n.className, dir: n.getAttribute('dir'), unicodeBidi: cs(n).unicodeBidi };
            }),
            displayIsolate: Array.prototype.map.call(document.querySelectorAll('.jfur-probe-display .jfur-isolate'), function (n) {
                return { cls: n.className, dir: n.getAttribute('dir'), unicodeBidi: cs(n).unicodeBidi };
            }),
            direction: cs(s).direction,
            rowFocusables: focusables.map(describe),
            fills: Array.prototype.map.call(fills, function (f) {
                return { star: box(f.parentNode), fill: box(f), svg: box(f.querySelector('svg')) };
            }),
            display: probe.display ? { role: probe.display.getAttribute('role'), label: probe.display.getAttribute('aria-label'), tabIndex: probe.display.tabIndex, fills: Array.prototype.map.call(probe.display.querySelectorAll('.jfur-star-fill'), function (f) { return f.style.width; }), star: box(probe.display.querySelector('.jfur-star')) } : null
        };
    };

    // Style rules of sheets other than User Ratings' own that select any node of the widget, with where each sheet comes
    // from: jellyfin-web's own (its CSS files under /web/, the inline style of index.html, MUI's emotion styles) is expected;
    // anything else (Jellyfin Enhanced, another plugin) would be a collision. Also our ids: each one exactly once in the
    // document, and no element of anyone else's with a jfur- class or id.
    probe.collisions = function () {
        var nodes = [probe.widget.element].concat(Array.prototype.slice.call(probe.widget.element.querySelectorAll('*')));
        if (probe.display) {
            nodes = nodes.concat([probe.display], Array.prototype.slice.call(probe.display.querySelectorAll('*')));
        }

        var hits = [];
        var unreadable = [];
        function origin(sheet) {
            var owner = sheet.ownerNode;
            if (sheet.href) {
                return sheet.href.replace(location.origin, '');
            }

            if (!owner) {
                return '(constructed)';
            }

            var attrs = Array.prototype.map.call(owner.attributes || [], function (a) { return a.name + (a.value ? '=' + a.value.slice(0, 30) : ''); }).join(' ');
            return '<' + owner.tagName.toLowerCase() + (attrs ? ' ' + attrs : '') + '> ' + (owner.textContent || '').replace(/\s+/g, ' ').slice(0, 60);
        }

        function walk(rules, sheet, media) {
            Array.prototype.forEach.call(rules, function (rule) {
                if (rule.selectorText) {
                    var matched = nodes.filter(function (n) {
                        try {
                            return n.matches(rule.selectorText);
                        } catch (e) {
                            return false;
                        }
                    });
                    if (matched.length > 0) {
                        hits.push({ selector: rule.selectorText.slice(0, 160), media: media || null, origin: origin(sheet), nodes: matched.slice(0, 3).map(describe) });
                    }
                } else if (rule.cssRules) {
                    walk(rule.cssRules, sheet, rule.conditionText || rule.media && rule.media.mediaText || media);
                }
            });
        }

        Array.prototype.forEach.call(document.styleSheets, function (sheet) {
            if (sheet.ownerNode && sheet.ownerNode.id === 'jfur-styles') {
                return;
            }

            try {
                walk(sheet.cssRules, sheet, null);
            } catch (e) {
                unreadable.push(sheet.href || origin(sheet));
            }
        });
        var jellyfinWeb = function (h) {
            return /^\/web\/[^?]*\.css(\?|$)/.test(h.origin) || /^<style(?: data-emotion=[^>]*)?> \.backgroundContainer-transparent/.test(h.origin) || /^<style data-emotion=/.test(h.origin);
        };
        var ids = Array.prototype.map.call(document.querySelectorAll('[id^="jfur-"]'), function (n) { return n.id; });
        var foreignJfur = Array.prototype.filter.call(document.querySelectorAll('[class*="jfur-"], [id^="jfur-"]'), function (n) {
            return !n.closest('.jfur-probe-section') && !n.closest('.jfur-toasts') && n.id !== 'jfur-styles';
        }).map(describe);
        return {
            hits: hits,
            foreign: hits.filter(function (h) { return !jellyfinWeb(h); }),
            unreadable: unreadable,
            ids: ids,
            duplicateIds: ids.filter(function (id, i) { return ids.indexOf(id) !== i || document.querySelectorAll('[id="' + id + '"]').length !== 1; }),
            foreignJfur: foreignJfur,
            sheets: Array.prototype.map.call(document.styleSheets, origin).slice(0, 60)
        };
    };

    // Jellyfin Enhanced's review parts on the shown details page (jeReviews), with any JE class or id
    // that names a review, translation or AniList.
    probe.jeReviews = function () {
        var page = document.querySelector('#itemDetailPage:not(.hide)') || document;
        var names = {};
        Array.prototype.forEach.call(page.querySelectorAll('*'), function (n) {
            String(typeof n.className === 'string' ? n.className : '').split(/\s+/).concat(n.id ? ['#' + n.id] : []).forEach(function (c) {
                if (/review|translat|anilist/i.test(c) && !/^jfur-/.test(c)) {
                    names[c] = (names[c] || 0) + 1;
                }
            });
        });
        return {
            chip: page.querySelectorAll('.je-avg-user-rating-chip').length,
            writeButton: page.querySelectorAll('.je-review-write-btn').length,
            tmdbSection: page.querySelectorAll('details.tmdb-reviews-section').length,
            names: names,
            sectionOrder: Array.prototype.map.call((page.querySelector('.detailPageSecondaryContainer .detailPageContent') || page).children, function (n) {
                return describe(n).slice(0, 60);
            }).slice(0, 20)
        };
    };
})();
