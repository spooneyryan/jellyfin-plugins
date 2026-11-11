// The details page's "Member ratings & reviews" section of User Ratings 0.3.5 and later, as the served
// ClientScript mounts it, for the userratings-details-*.json webchecks (scripts/webchecks/gen-details.mjs writes them).
// Loaded by webcheck's initScript into every document before any page script, so its first key listener runs before
// jellyfin-web's and Jellyfin Enhanced's, and its fetch wrapper sees every request of the section.
//
// window.__jfurDetails:
//   log        key events at the window's capture phase (first of all) and bubble phase (where jellyfin-web's keyboard
//              navigation listens; prevented there means it skipped the key), focus moves
//   requests   every request of the page to /UserRatings/ but the ClientScript: { t, method, path, query, body, status,
//              injected }; the probe's own reads (server()) aren't in it
//   inject(rule)  the next request matching { method, path (a RegExp source) } is answered by the probe: mode 'stub'
//              answers { status, body } without the server (a 503 "busy" before any write); mode 'after' lets it reach the
//              server and then answers { status, body } instead (a write saved whose read after it was a 404); mode
//              'partial' lets it reach the server and answers its JSON as IsPartial (no Summary, no Reviews); mode
//              'patch' lets it reach the server and answers its JSON with `patch`'s fields put over it (Can with a
//              Reason such as ReadOnly, whose real state is a read-only store, userratings-api-probe.mjs's drill); once by
//              default, or `times`
//   storage()  this plugin's localStorage entries, names and lengths only (a draft's words never leave the page)
//   server(method, path, body)  a request of the signed-in member's own, past the wrapper: what the server holds now
//   section(), state(), styles()  what the webchecks assert
//   onSlider(), active(), box(selector)
//   extras(), expectedChip(itemId)  the chip and the Rate button as shown, and the chip the server's summary
//              calls for
(function () {
    'use strict';

    if (window.__jfurDetails) {
        return;
    }

    var t0 = performance.now();
    var originalFetch = window.fetch.bind(window);
    var probe = window.__jfurDetails = { log: [], requests: [], rules: [] };

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

    probe.describe = describe;
    window.addEventListener('keydown', function (e) {
        probe.log.push({ t: now(), kind: 'key-capture', key: e.key, prevented: e.defaultPrevented, target: describe(e.target) });
    }, true);
    window.addEventListener('keydown', function (e) {
        probe.log.push({ t: now(), kind: 'key-bubble', key: e.key, prevented: e.defaultPrevented, target: describe(e.target) });
    }, false);
    document.addEventListener('focusin', function (e) {
        probe.log.push({ t: now(), kind: 'focus', target: describe(e.target) });
    }, true);

    function answer(status, body) {
        var empty = status === 204 || body === null || body === undefined;
        return new Response(empty ? null : JSON.stringify(body), { status: status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
    }

    probe.inject = function (rule) {
        probe.rules.push(Object.assign({ mode: 'stub', times: 1 }, rule));
        return probe.rules.length;
    };

    function take(method, path) {
        for (var i = 0; i < probe.rules.length; i++) {
            var r = probe.rules[i];
            if (r.times > 0 && r.method === method && new RegExp(r.path).test(path)) {
                r.times--;
                return r;
            }
        }

        return null;
    }

    window.fetch = function (input, init) {
        var url = typeof input === 'string' ? input : (input && input.url) || '';
        var parsed;
        try {
            parsed = new URL(url, location.href);
        } catch (e) {
            return originalFetch(input, init);
        }

        if (!/\/UserRatings\//i.test(parsed.pathname) || /\/UserRatings\/ClientScript/i.test(parsed.pathname)) {
            return originalFetch(input, init);
        }

        var method = ((init && init.method) || 'GET').toUpperCase();
        var entry = { t: now(), method: method, path: parsed.pathname.replace(/^.*?(\/UserRatings\/)/i, '$1'), query: parsed.search, body: undefined, status: null, injected: null };
        var sent = init && init.body;
        if (sent && typeof Blob !== 'undefined' && sent instanceof Blob) {
            // A file sent as it is (the Data tab's import): what it is in the log, the Blob itself kept for a later read,
            // not enumerable, so the log stays plain JSON.
            entry.body = { blob: Object.prototype.toString.call(sent).slice(8, -1), name: sent.name || null, size: sent.size, type: sent.type };
            Object.defineProperty(entry, 'blob', { value: sent, enumerable: false });
        } else {
            try {
                entry.body = sent ? JSON.parse(sent) : undefined;
            } catch (e) {
                entry.body = '(not JSON)';
            }
        }

        probe.requests.push(entry);
        var rule = take(method, entry.path);
        if (rule && rule.mode === 'stub') {
            entry.injected = rule.status;
            entry.status = rule.status;
            return Promise.resolve(answer(rule.status, rule.body));
        }

        return originalFetch(input, init).then(function (res) {
            entry.status = res.status;
            entry.done = now();
            if (!rule) {
                return res;
            }

            if (rule.mode === 'partial') {
                return res.json().then(function (json) {
                    entry.injected = 'partial';
                    return answer(200, Object.assign({}, json, { IsPartial: true, Summary: null, Reviews: null }));
                });
            }

            if (rule.mode === 'patch') {
                return res.json().then(function (json) {
                    entry.injected = 'patch';
                    return answer(res.status, Object.assign({}, json, rule.patch || {}));
                });
            }

            entry.injected = rule.status;
            return answer(rule.status, rule.body);
        });
    };

    // A request of the member's own past the wrapper (the token never leaves the page and is never returned).
    probe.server = function (method, path, body) {
        var c = window.ApiClient;
        var header = 'MediaBrowser Client="ur-details-probe", Device="probe", DeviceId="' + c.deviceId() + '", Version="1.0.0", Token="' + c.accessToken() + '"';
        var init = { method: method, headers: { Accept: 'application/json', Authorization: header } };
        if (body !== undefined) {
            init.headers['Content-Type'] = 'application/json';
            init.body = JSON.stringify(body);
        }

        return originalFetch(c.getUrl(path), init).then(function (res) {
            return res.text().then(function (text) {
                var json = null;
                try {
                    json = text ? JSON.parse(text) : null;
                } catch (e) {
                    json = null;
                }

                return { status: res.status, json: json };
            });
        });
    };

    // What the server holds for the member on an item: their stars, text, flag and Edited, the row's time and the review's
    // own (ReviewUpdatedUtc, which only a change of the text moves), and the summary.
    probe.mine = function (itemId) {
        return probe.server('GET', 'UserRatings/Items/' + itemId).then(function (r) {
            var m = r.json && r.json.Mine;
            var s = r.json && r.json.Summary;
            return { status: r.status, stars: m ? (m.Stars === undefined ? null : m.Stars) : null, text: m ? (m.Text === undefined ? null : m.Text) : null, edited: m ? !!m.Edited : null, spoiler: m ? !!m.IsSpoiler : null, updated: m ? m.UpdatedUtc : null, reviewUpdated: m ? (m.ReviewUpdatedUtc === undefined ? null : m.ReviewUpdatedUtc) : null, count: s ? s.Count : null, average: s ? (s.Average === undefined ? null : s.Average) : null, others: r.json && r.json.Reviews ? r.json.Reviews.TotalRecordCount : null };
        });
    };

    probe.section = function () {
        return document.querySelector('#itemDetailPage:not(.hide) .jfur-section');
    };

    // User Ratings' entries in localStorage (the drafts): each name with its value's length, never the value.
    probe.storage = function () {
        var found = {};
        for (var i = 0; i < localStorage.length; i++) {
            var key = localStorage.key(i);
            if (key && key.indexOf('jfur:') === 0) {
                found[key] = (localStorage.getItem(key) || '').length;
            }
        }

        return found;
    };

    // Whether a text is anywhere in the page as the browser finds it (Ctrl+F's window.find), the selection put back.
    probe.findable = function (text) {
        var found = window.find(text, false, false, true, false, false, false);
        var selection = window.getSelection();
        if (selection) {
            selection.removeAllRanges();
        }

        return found || document.body.textContent.indexOf(text) >= 0;
    };

    probe.slider = function () {
        var s = probe.section();
        return s ? s.querySelector('.jfur-stars') : null;
    };

    probe.onSlider = function () {
        var s = probe.slider();
        return !!s && document.activeElement === s;
    };

    probe.active = function () {
        return describe(document.activeElement);
    };

    function displayed(node) {
        return !!node && node.getClientRects().length > 0 && getComputedStyle(node).display !== 'none' && getComputedStyle(node).visibility !== 'hidden';
    }

    probe.displayed = displayed;

    function texts(nodes) {
        return Array.prototype.map.call(nodes, function (n) {
            return n.textContent;
        });
    }

    probe.state = function () {
        var s = probe.section();
        if (!s) {
            return { mounted: false, sections: document.querySelectorAll('.jfur-section').length, hash: location.hash.slice(0, 80) };
        }

        var slider = s.querySelector('.jfur-stars');
        var stars = s.querySelector('.jfur-summary .jfur-stars-display');
        var notices = Array.prototype.filter.call(s.querySelectorAll('.jfur-notice'), displayed);
        return {
            mounted: true,
            sections: document.querySelectorAll('.jfur-section').length,
            item: s.getAttribute('data-jfur-item'),
            next: s.nextElementSibling ? (s.nextElementSibling.id || s.nextElementSibling.className) : null,
            expanded: s.querySelector('.jfur-section-toggle').getAttribute('aria-expanded'),
            summaryKind: s.querySelector('.jfur-summary').getAttribute('data-jfur-summary'),
            summary: s.querySelector('.jfur-summary').textContent,
            average: stars ? stars.getAttribute('aria-label') : null,
            histogram: s.querySelector('.jfur-histogram') ? { label: s.querySelector('.jfur-histogram').getAttribute('aria-label'), shown: displayed(s.querySelector('.jfur-histogram')) } : null,
            copies: texts(s.querySelectorAll('.jfur-copy')),
            value: slider ? Number(slider.getAttribute('aria-valuenow')) : null,
            valuetext: slider ? slider.getAttribute('aria-valuetext') : null,
            shown: s.querySelector('.jfur-stars-value') ? s.querySelector('.jfur-stars-value').textContent : null,
            disabled: slider ? slider.getAttribute('aria-disabled') : null,
            live: s.querySelector('.jfur-stars-live') ? s.querySelector('.jfur-stars-live').textContent : null,
            sectionLive: s.querySelector('.jfur-section-live') ? s.querySelector('.jfur-section-live').textContent : null,
            clear: displayed(s.querySelector('.jfur-clear')),
            write: displayed(s.querySelector('.jfur-write')),
            own: s.querySelector('.jfur-own .jfur-review-text') ? s.querySelector('.jfur-own .jfur-review-text').textContent : null,
            ownEdited: !!s.querySelector('.jfur-own .jfur-review-edited'),
            form: !!s.querySelector('.jfur-review-form'),
            formText: s.querySelector('.jfur-review-form textarea') ? s.querySelector('.jfur-review-form textarea').value : null,
            notices: notices.map(function (n) {
                return { role: n.getAttribute('role'), text: n.querySelector('.jfur-notice-text').textContent, action: n.querySelector('button') ? n.querySelector('button').textContent : null };
            }),
            reviews: Array.prototype.map.call(s.querySelectorAll('.jfur-review'), function (r) {
                return r.getAttribute('data-jfur-review');
            }),
            names: texts(s.querySelectorAll('.jfur-review .jfur-review-name')),
            pager: displayed(s.querySelector('.jfur-pager button')),
            reviewsOff: displayed(s.querySelector('.jfur-reviews-off')),
            toolbar: displayed(s.querySelector('.jfur-reviews-toolbar')),
            sort: s.querySelector('select.jfur-sort') ? s.querySelector('select.jfur-sort').value : null,
            // The read-only stars and their note, the disabled slider's note, the form's flag, the text kept beside
            // the form, the 409's buttons, a draft put back, the buttons' words, the rows marked as spoilers.
            sliderShown: displayed(slider),
            reason: slider ? slider.getAttribute('data-jfur-reason') : null,
            sliderNote: slider && slider.getAttribute('aria-describedby') && document.getElementById(slider.getAttribute('aria-describedby')) ? document.getElementById(slider.getAttribute('aria-describedby')).textContent : null,
            readOnly: displayed(s.querySelector('.jfur-mine-readonly')) ? { label: s.querySelector('.jfur-mine-readonly .jfur-stars-display').getAttribute('aria-label'), note: s.querySelector('.jfur-mine-readonly .jfur-mine-note').textContent, focusables: s.querySelector('.jfur-mine-readonly').querySelectorAll('button, a, input, select, textarea, [tabindex]').length } : null,
            formSpoiler: s.querySelector('.jfur-review-form input.jfur-review-spoiler') ? s.querySelector('.jfur-review-form input.jfur-review-spoiler').checked : null,
            formReadOnly: s.querySelector('.jfur-review-form textarea') ? s.querySelector('.jfur-review-form textarea').readOnly : null,
            aside: displayed(s.querySelector('.jfur-review-aside')) ? { label: s.querySelector('.jfur-aside-label').textContent, text: s.querySelector('.jfur-aside-text').textContent, use: s.querySelector('.jfur-aside-use').textContent } : null,
            conflict: !!s.querySelector('.jfur-conflict-reload') && !!s.querySelector('.jfur-conflict-overwrite'),
            // Every text kept beside the form (Reload after Reload), and the 409's panel with the review as it is now
            // (Question 4), Overwrite shown once it is read.
            kept: Array.prototype.filter.call(s.querySelectorAll('.jfur-review-aside'), displayed).map(function (g) {
                return { label: g.querySelector('.jfur-aside-label').textContent, text: g.querySelector('.jfur-aside-text').textContent, use: g.querySelector('.jfur-aside-use').textContent };
            }),
            conflictPanel: s.querySelector('.jfur-conflict-review') ? {
                label: s.querySelector('.jfur-conflict-review .jfur-conflict-label') ? s.querySelector('.jfur-conflict-review .jfur-conflict-label').textContent : null,
                text: s.querySelector('.jfur-conflict-newer .jfur-review-text') ? s.querySelector('.jfur-conflict-newer .jfur-review-text').textContent : null,
                dir: s.querySelector('.jfur-conflict-newer .jfur-review-text') ? s.querySelector('.jfur-conflict-newer .jfur-review-text').getAttribute('dir') : null,
                spoiler: !!s.querySelector('.jfur-conflict-newer .jfur-spoiler-toggle'),
                status: s.querySelector('.jfur-conflict-review .jfur-conflict-status') ? s.querySelector('.jfur-conflict-review .jfur-conflict-status').textContent : null,
                editable: s.querySelectorAll('.jfur-conflict-review textarea, .jfur-conflict-review input').length
            } : null,
            overwrite: displayed(s.querySelector('.jfur-conflict-overwrite')),
            restored: displayed(s.querySelector('.jfur-review-restored')),
            writeLabel: s.querySelector('.jfur-write') ? s.querySelector('.jfur-write').textContent : null,
            editLabel: s.querySelector('.jfur-own-edit') ? s.querySelector('.jfur-own-edit').textContent : null,
            ownSpoiler: !!s.querySelector('.jfur-own .jfur-spoiler-flag'),
            spoilerRows: Array.prototype.map.call(s.querySelectorAll('.jfur-review'), function (r) {
                return r.querySelector('.jfur-spoiler-toggle') ? r.getAttribute('data-jfur-review') : null;
            }).filter(Boolean),
            onSlider: probe.onSlider(),
            active: probe.active(),
            hash: location.hash.slice(0, 80)
        };
    };

    // Computed styles: the slider's row, every member's word isolated, the histogram, the section's width.
    probe.styles = function () {
        var s = probe.section();
        var row = s.querySelector('.jfur-mine-row');
        var focusables = Array.prototype.filter.call(row.querySelectorAll('button, a, input, select, textarea, [tabindex], .focusable'), function (n) {
            return displayed(n) && !n.disabled && n.getAttribute('tabindex') !== '-1';
        });
        var isolated = Array.prototype.map.call(s.querySelectorAll('.jfur-review-name, .jfur-review-text, .jfur-copy-library'), function (n) {
            return { cls: n.className, dir: n.getAttribute('dir'), unicodeBidi: getComputedStyle(n).unicodeBidi, whiteSpace: getComputedStyle(n).whiteSpace };
        });
        var r = s.getBoundingClientRect();
        var toggle = s.querySelector('.jfur-section-toggle');
        return {
            rowFocusables: focusables.map(describe),
            isolated: isolated,
            histogram: s.querySelector('.jfur-histogram') ? getComputedStyle(s.querySelector('.jfur-histogram')).display : null,
            section: { x: r.left, right: r.right, w: r.width },
            viewport: { w: document.documentElement.clientWidth, scrollW: document.documentElement.scrollWidth },
            overflow: Array.prototype.filter.call(s.querySelectorAll('*'), function (n) {
                var b = n.getBoundingClientRect();
                return displayed(n) && b.width > 0 && (b.right > document.documentElement.clientWidth + 1 || b.left < -1);
            }).slice(0, 5).map(describe),
            toggle: { tabIndex: toggle.tabIndex, tabindexAttr: toggle.getAttribute('tabindex'), fontSize: getComputedStyle(toggle).fontSize, color: getComputedStyle(toggle).color, background: getComputedStyle(toggle).backgroundColor },
            titleFontSize: getComputedStyle(toggle.parentNode).fontSize,
            tv: s.classList.contains('jfur-section-tv')
        };
    };

    // Style rules of sheets other than User Ratings' own that select a node of the section, with where each sheet comes
    // from: jellyfin-web's own (its CSS under /web/, index.html's inline style, MUI's emotion styles) is expected, anything
    // else (Jellyfin Enhanced, another plugin) would be a collision; our ids each once; no jfur- class or id on anyone
    // else's element (the widget's check, for the whole section). The section's item's chip in the
    // media-info row is checked as the section is, and it and the Rate button in the buttons row are ours outside the section.
    probe.collisions = function () {
        var s = probe.section();
        var page = document.querySelector('#itemDetailPage:not(.hide)');
        var item = s ? s.getAttribute('data-jfur-item') : null;
        var ownChip = page && item ? page.querySelector('.jfur-chip[data-jfur-chip="' + item + '"]') : null;
        var ownRate = page ? page.querySelector('.mainDetailButtons .jfur-rate') : null;
        var nodes = [s].concat(Array.prototype.slice.call(s.querySelectorAll('*')));
        if (ownChip) {
            nodes = nodes.concat([ownChip], Array.prototype.slice.call(ownChip.querySelectorAll('*')));
        }
        var hits = [];
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

        function walk(rules, sheet) {
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
                        hits.push({ selector: rule.selectorText.slice(0, 160), origin: origin(sheet), nodes: matched.slice(0, 3).map(describe) });
                    }
                } else if (rule.cssRules) {
                    walk(rule.cssRules, sheet);
                }
            });
        }

        Array.prototype.forEach.call(document.styleSheets, function (sheet) {
            if (sheet.ownerNode && sheet.ownerNode.id === 'jfur-styles') {
                return;
            }

            try {
                walk(sheet.cssRules, sheet);
            } catch (e) {
                // a sheet of another origin: unreadable
            }
        });
        var jellyfinWeb = function (h) {
            return /^\/web\/[^?]*\.css(\?|$)/.test(h.origin) || /^<style(?: data-emotion=[^>]*)?> \.backgroundContainer-transparent/.test(h.origin) || /^<style data-emotion=/.test(h.origin);
        };
        var ids = Array.prototype.map.call(document.querySelectorAll('[id^="jfur-"]'), function (n) { return n.id; });
        return {
            foreign: hits.filter(function (h) { return !jellyfinWeb(h); }),
            jellyfinWeb: hits.filter(jellyfinWeb).length,
            duplicateIds: ids.filter(function (id, i) { return ids.indexOf(id) !== i; }),
            foreignJfur: Array.prototype.filter.call(document.querySelectorAll('[class*="jfur-"], [id^="jfur-"]'), function (n) {
                return !n.closest('.jfur-section') && !n.closest('.jfur-toasts') && !n.closest('.jfur-backdrop') && n.id !== 'jfur-styles' &&
                    !(ownChip && ownChip.contains(n)) && !(ownRate && ownRate.contains(n));
            }).map(describe),
            chip: !!ownChip,
            rate: !!ownRate
        };
    };

    // Jellyfin Enhanced's review parts on the shown details page and the order of the content column's sections.
    probe.jeReviews = function () {
        var page = document.querySelector('#itemDetailPage:not(.hide)') || document;
        return {
            chip: page.querySelectorAll('.je-avg-user-rating-chip').length,
            writeButton: page.querySelectorAll('.je-review-write-btn').length,
            tmdbSection: page.querySelectorAll('details.tmdb-reviews-section').length,
            order: Array.prototype.map.call((page.querySelector('.detailPageSecondaryContainer .detailPageContent') || page).children, function (n) {
                return describe(n).slice(0, 60);
            }).slice(0, 20)
        };
    };

    // The chip and the Rate button on the shown details page, and how many of ours (and
    // of the kit's TV jump buttons) the whole document holds.
    probe.extras = function () {
        var page = document.querySelector('#itemDetailPage:not(.hide)');
        var row = page ? page.querySelector('.itemMiscInfo-primary') : null;
        var buttons = page ? page.querySelector('.mainDetailButtons') : null;
        var chip = row ? row.querySelector('.jfur-chip') : null;
        var rate = buttons ? buttons.querySelector('.jfur-rate') : null;
        var part = function (cls) {
            return chip ? chip.querySelector(cls) : null;
        };
        var icon = part('.jfur-chip-icon');
        var word = part('.jfur-chip-word');
        var b = chip ? chip.getBoundingClientRect() : null;
        // More after the Rate button in its row, whatever another plugin's button (Bookmarks' "Add
        // bookmark", which also goes before More) came between them.
        var more = buttons ? buttons.querySelector('.btnMoreCommands') : null;
        var beforeMore = !!rate && !!more && more.parentNode === rate.parentNode && !!(rate.compareDocumentPosition(more) & Node.DOCUMENT_POSITION_FOLLOWING);
        return {
            chips: document.querySelectorAll('.jfur-chip').length,
            rates: document.querySelectorAll('.jfur-rate').length,
            jumps: document.querySelectorAll('.jfur-je-jump').length,
            row: row ? { hidden: row.classList.contains('hide'), displayed: displayed(row), unhid: row.hasAttribute('data-jfur-unhid'), items: row.children.length, chipLast: !!chip && row.lastElementChild === chip } : null,
            chip: chip ? {
                item: chip.getAttribute('data-jfur-chip'),
                displayed: displayed(chip),
                value: part('.jfur-chip-value').textContent,
                word: word.textContent,
                wordDisplayed: displayed(word),
                label: part('.jfur-chip-label').textContent,
                title: chip.getAttribute('title'),
                iconWidth: icon ? Math.round(icon.getBoundingClientRect().width) : 0,
                x: Math.round(b.left),
                right: Math.round(b.right),
                viewport: window.innerWidth
            } : null,
            rate: rate ? { displayed: displayed(rate), title: rate.getAttribute('title'), label: rate.getAttribute('aria-label'), next: describe(rate.nextElementSibling), beforeMore: beforeMore } : null,
            buttons: buttons ? Array.prototype.filter.call(buttons.children, displayed).map(function (n) {
                return describe(n).slice(0, 50);
            }) : null,
            jeReviews: page ? page.querySelectorAll('details.tmdb-reviews-section').length : 0
        };
    };

    // The chip the server's summary of an item calls for (an average from MinRatingsForAverage ratings), as the
    // page should write it: { value, label }, or null.
    probe.expectedChip = function (itemId) {
        return Promise.all([probe.server('GET', 'UserRatings/Items/' + itemId), probe.server('GET', 'UserRatings/Capabilities')]).then(function (r) {
            var s = r[0].json && r[0].json.Summary;
            var d = r[1].json && r[1].json.Display;
            var min = Math.max(1, (d && d.MinRatingsForAverage) || 1);
            if (!s || !(s.Count >= min) || !(s.Average > 0) || (d && d.ShowAverageChip === false)) {
                return null;
            }

            var avg = (Math.round(Math.min(5, s.Average) * 10) / 10).toFixed(1);
            return { value: avg + '/5 (' + s.Count + ')', label: 'Member average ' + avg + ' out of 5, ' + (s.Count === 1 ? '1 rating' : s.Count + ' ratings') };
        });
    };

    probe.box = function (selector) {
        var node = typeof selector === 'string' ? document.querySelector(selector) : selector;
        var b = node.getBoundingClientRect();
        return { x: b.left, y: b.top, w: b.width, h: b.height, right: b.right, cx: b.left + b.width / 2, cy: b.top + b.height / 2 };
    };

    // The pointer's x on the shown slider for a value's half boundary (the middle of star 3 is 2.5).
    probe.xFor = function (stars) {
        var b = probe.slider().getBoundingClientRect();
        return b.left + b.width * stars / 5;
    };

    // Requests of the section since a mark, as "METHOD /path [status]" (the injected answer, when there was one).
    probe.since = function (mark) {
        return probe.requests.slice(mark).map(function (r) {
            return r.method + ' ' + r.path + ' ' + (r.injected !== null ? r.injected + ' (injected; server ' + (r.injected === r.status ? 'not reached' : r.status) + ')' : r.status);
        });
    };
})();
