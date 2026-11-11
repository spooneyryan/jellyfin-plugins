// Helpers for Library Maintenance's Move webchecks (initScript, in every document before jellyfin-web).
// They only read the page and click what a person would click. Library Maintenance's own requests (fetch, as kit.api
// sends them) are listed in window.__lmmLog: { method, path, query }, never a header or a token.
window.__s = {};
window.__lmmLog = [];

(function () {
    const original = window.fetch;
    if (typeof original !== 'function') {
        return;
    }

    window.fetch = function (input, init) {
        try {
            const url = new URL(typeof input === 'string' ? input : input.url, location.href);
            const at = url.pathname.indexOf('/LibraryMaintenance/');
            if (at >= 0) {
                const query = {};
                url.searchParams.forEach(function (value, key) {
                    if (!/token|api_key/i.test(key)) {
                        query[key] = value;
                    }
                });
                window.__lmmLog.push({ method: String((init && init.method) || 'GET').toUpperCase(), path: url.pathname.substring(at), query: query, t: Date.now() });
            }
        } catch (e) {
            // not a URL this log keeps
        }

        return original.apply(this, arguments);
    };
})();

window.__lmm = {
    page: function () {
        return Array.prototype.slice.call(document.querySelectorAll('[data-role="page"]')).filter(function (p) {
            return !p.classList.contains('hide');
        })[0] || document.body;
    },
    sheet: function () {
        const sheets = document.querySelectorAll('.dialogContainer > .actionSheet');
        return sheets.length ? sheets[sheets.length - 1] : null;
    },
    rows: function () {
        const sheet = window.__lmm.sheet();
        return sheet ? Array.prototype.map.call(sheet.querySelectorAll('.actionSheetMenuItem'), function (b) {
            return b.getAttribute('data-id');
        }) : null;
    },
    afterRefresh: function (id) {
        const rows = window.__lmm.rows() || [];
        return rows.indexOf(id) > 0 && rows.indexOf(id) === rows.indexOf('refresh') + 1;
    },
    // Library Maintenance's rows in the open sheet.
    ours: function () {
        return (window.__lmm.rows() || []).filter(function (id) {
            return id.indexOf('jflm-') === 0;
        });
    },
    card: function (type, index) {
        return Array.prototype.slice.call(window.__lmm.page().querySelectorAll('.card[data-type="' + type + '"]'))[index || 0] || null;
    },
    cardNamed: function (name) {
        return Array.prototype.slice.call(window.__lmm.page().querySelectorAll('.card[data-id]')).filter(function (c) {
            return (c.textContent || '').indexOf(name) >= 0;
        })[0] || null;
    },
    menu: function (host) {
        host.querySelector('.itemAction[data-action="menu"]').click();
        return host.getAttribute('data-id');
    },
    close: function () {
        const open = window.__lmm.sheet() || document.querySelector('.dialogContainer > .dialog');
        if (!open) {
            return false;
        }

        if (open.getAttribute('data-history') === 'true') {
            history.back();
        } else {
            window.Dashboard.dialogHelper.close(open);
        }

        return true;
    },
    // The Move dialog and its parts.
    dialog: function () {
        return document.querySelector('.dialogContainer .jflm-move-dialog');
    },
    q: function (selector) {
        const d = window.__lmm.dialog();
        return d ? d.querySelector(selector) : null;
    },
    all: function (selector) {
        const d = window.__lmm.dialog();
        return d ? Array.prototype.slice.call(d.querySelectorAll(selector)) : [];
    },
    text: function (selector) {
        const node = window.__lmm.q(selector);
        return node ? node.textContent : null;
    },
    loaded: function () {
        return !!window.__lmm.q('.jflm-move-source') && !!window.__lmm.q('select.jflm-move-library');
    },
    // The preview has settled: not planning, and a status other than the first hint.
    settled: function () {
        const out = window.__lmm.q('.jflm-move-preview-output');
        const status = window.__lmm.text('.jflm-move-preview-status') || '';
        return !!out && out.getAttribute('aria-busy') !== 'true' && status !== '' && status.indexOf('Previewing') < 0 && status.indexOf('Choose where') < 0;
    },
    // Chooses a library of the picker by its name (the option's text starts with it).
    library: function (name) {
        const select = window.__lmm.q('select.jflm-move-library');
        const option = Array.prototype.slice.call(select.options).filter(function (o) {
            return o.textContent.indexOf(name) === 0;
        })[0];
        if (!option) {
            return null;
        }

        select.value = option.value;
        select.dispatchEvent(new Event('change', { bubbles: true }));
        return option.textContent;
    },
    folderRows: function () {
        return window.__lmm.all('button.jflm-move-folder').map(function (b) {
            const badge = b.querySelector('.jflm-move-badge');
            const reason = b.getAttribute('aria-describedby') ? document.getElementById(b.getAttribute('aria-describedby')) : null;
            return { name: b.querySelector('.jflm-move-folder-name').textContent, badge: badge ? badge.textContent : null, disabled: b.getAttribute('aria-disabled') === 'true', reason: reason ? reason.textContent : null };
        });
    },
    folder: function (name) {
        return window.__lmm.all('button.jflm-move-folder').filter(function (b) {
            return b.querySelector('.jflm-move-folder-name').textContent === name;
        })[0] || null;
    },
    facts: function () {
        const out = {};
        const terms = window.__lmm.all('.jflm-move-preview-output dl.jflm-move-facts dt');
        terms.forEach(function (dt) {
            out[dt.textContent] = dt.nextElementSibling ? dt.nextElementSibling.textContent : null;
        });
        return out;
    },
    issues: function (kind) {
        return window.__lmm.all('li.jflm-move-' + kind).map(function (li) {
            return { code: li.getAttribute('data-code'), title: (li.querySelector('.jflm-move-issue-title') || {}).textContent, message: (li.querySelector('.jflm-move-issue-message') || {}).textContent };
        });
    },
    // Nothing of the dialog is wider than the window (a phone never scrolls sideways).
    fits: function () {
        const d = window.__lmm.dialog();
        if (!d) {
            return null;
        }

        const wide = Array.prototype.slice.call(d.querySelectorAll('*')).filter(function (el) {
            const r = el.getBoundingClientRect();
            return r.width > 0 && (r.right > innerWidth + 1 || r.left < -1);
        }).map(function (el) {
            return el.tagName + '.' + String(el.className).split(' ').join('.');
        });
        return { innerWidth: innerWidth, docScroll: document.documentElement.scrollWidth, dialogScroll: d.scrollWidth, dialogClient: d.clientWidth, wide: wide.slice(0, 10) };
    },
    // Whether the preview-only notice is on screen without scrolling the page: the intro's
    // lead and the footer's note, each inside the window, shown, and the topmost element at its middle (nothing covers
    // it); and how far the dialog's content has scrolled.
    notice: function () {
        const one = function (el) {
            if (!el) {
                return null;
            }

            const r = el.getBoundingClientRect();
            const hit = r.width > 0 && r.height > 0 ? document.elementFromPoint(Math.min(innerWidth - 1, Math.max(0, (r.left + r.right) / 2)), Math.min(innerHeight - 1, Math.max(0, (r.top + r.bottom) / 2))) : null;
            return {
                text: el.textContent,
                shown: !el.classList.contains('hide') && el.getClientRects().length > 0,
                inView: r.width > 0 && r.height > 0 && r.top >= 0 && r.left >= 0 && r.bottom <= innerHeight && r.right <= innerWidth,
                onTop: !!hit && (hit === el || el.contains(hit))
            };
        };
        const content = window.__lmm.q('.formDialogContent');
        return {
            view: [innerWidth, innerHeight],
            lead: one(window.__lmm.q('.jflm-move-preview-only-lead')),
            note: one(window.__lmm.q('.jflm-move-preview-only')),
            close: one(window.__lmm.q('.jflm-move-close')),
            contentScrollTop: content ? content.scrollTop : null,
            contentScrolls: content ? content.scrollHeight > content.clientHeight + 1 : null,
            pageScrollY: window.scrollY
        };
    },
    // Whether a notice() part is in view, shown and on top.
    seen: function (part) {
        return !!part && part.shown && part.inView && part.onTop;
    },
    // What the dialog shows now, in one object (the webchecks compare its parts).
    state: function () {
        const L = window.__lmm;
        const d = L.dialog();
        if (!d) {
            return null;
        }

        const select = L.q('select.jflm-move-library');
        const shown = function (node) {
            return !!node && !node.classList.contains('hide') && node.getClientRects().length > 0;
        };
        const newFolder = L.q('input.jflm-move-new-folder');
        const label = newFolder ? newFolder.closest('.jflm-field').querySelector('label') : null;
        const wrapBox = L.q('input.jflm-move-wrap');
        const wrapName = L.q('input.jflm-move-wrap-name');
        return {
            item: L.text('.jflm-move-item-title'),
            chip: L.text('.jflm-move-chip'),
            where: L.text('.jflm-move-where'),
            retargeted: L.text('.jflm-move-retargeted'),
            summary: L.text('.jflm-move-summary'),
            lists: L.all('.jflm-move-source .jflm-move-list-label').map(function (n) { return n.textContent; }),
            listItems: L.all('.jflm-move-source .jflm-move-list li').map(function (n) { return n.textContent; }),
            libraries: select ? Array.prototype.map.call(select.options, function (o) { return o.textContent; }) : null,
            library: select && select.selectedOptions[0] ? select.selectedOptions[0].textContent : null,
            locationShown: shown(L.q('select.jflm-move-location')),
            locationNote: L.text('.jflm-move-location-note'),
            crumbs: L.all('.jflm-move-crumb').map(function (n) { return n.textContent; }),
            count: L.text('.jflm-move-count'),
            folders: L.folderRows(),
            more: (function () { const b = L.q('.jflm-pager-more'); return !!b && shown(b); })(),
            browseShown: shown(L.q('.jflm-move-folders')) || shown(L.q('.jflm-move-count')),
            newFolderShown: shown(newFolder),
            newFolderLabel: label ? label.textContent : null,
            newFolderPlaceholder: newFolder ? newFolder.placeholder : null,
            newFolderError: newFolder ? newFolder.closest('.jflm-field').querySelector('.jflm-move-field-error').textContent : null,
            newFolderInvalid: newFolder ? newFolder.getAttribute('aria-invalid') : null,
            wrapShown: shown(L.q('.jflm-move-wrap-group')),
            wrapChecked: wrapBox ? wrapBox.checked : null,
            wrapState: L.text('.jflm-move-wrap-state'),
            wrapReset: shown(L.q('.jflm-move-wrap-reset')),
            wrapNameShown: shown(wrapName),
            wrapNamePlaceholder: wrapName ? wrapName.placeholder : null,
            status: L.text('.jflm-move-preview-status'),
            again: L.text('.jflm-move-preview-again'),
            blockers: L.issues('blocker'),
            warnings: L.issues('warning'),
            facts: L.facts(),
            kept: L.all('.jflm-move-preview-output .jflm-move-list-label').map(function (n) { return n.textContent; }),
            error: L.text('.jflm-move-preview-output .jflm-move-error-title'),
            runShown: shown(L.q('.jflm-move-run')),
            noteShown: shown(L.q('.jflm-move-preview-only')),
            plans: L.log('/LibraryMaintenance/Move/Plan').length
        };
    },
    // Marks one element for a real click (webcheck's click step takes a selector): [data-lmm-target="<name>"].
    target: function (el, name) {
        Array.prototype.forEach.call(document.querySelectorAll('[data-lmm-target="' + name + '"]'), function (n) {
            n.removeAttribute('data-lmm-target');
        });
        if (el) {
            el.setAttribute('data-lmm-target', name);
        }

        return !!el;
    },
    // Sets an input's text as typing would end (an input event), for clearing a field.
    input: function (selector, value) {
        const el = window.__lmm.q(selector);
        el.value = value;
        el.dispatchEvent(new Event('input', { bubbles: true }));
        return el.value;
    },
    // The focused element, briefly, and whether it is inside the Move dialog.
    focused: function () {
        const el = document.activeElement;
        if (!el || el === document.body) {
            return { tag: 'BODY', inDialog: false };
        }

        const d = window.__lmm.dialog();
        const dialog = d ? d.closest('.dialog') || d : null;
        return {
            tag: el.tagName,
            cls: String(el.className || '').split(' ').filter(function (c) { return c.indexOf('jflm-move-') === 0 || c === 'btnCancel' || c === 'actionSheetMenuItem' || c === 'btnMoreCommands'; }).join(' '),
            id: el.getAttribute('data-id') || null,
            text: (el.getAttribute('aria-label') || el.textContent || el.placeholder || '').trim().slice(0, 60),
            inDialog: !!dialog && dialog.contains(el)
        };
    },
    // Whether the focused element matches a selector (and, for a folder row, has that folder's name).
    focusIs: function (selector, name) {
        const el = document.activeElement;
        if (!el || !el.matches || !el.matches(selector)) {
            return false;
        }

        const label = el.querySelector('.jflm-move-folder-name');
        return name === undefined || (label ? label.textContent : el.textContent.trim()) === name;
    },
    // The last n parts of a path, joined with "/" (a webcheck never names the sandbox's own folders).
    tail: function (path, n) {
        return String(path || '').split(/[\\/]/).filter(Boolean).slice(-n).join('/');
    },
    log: function (path) {
        return window.__lmmLog.filter(function (e) {
            return !path || e.path.indexOf(path) === 0;
        });
    },
    health: function () {
        return window.__jfkit && window.__jfkit.jflm ? window.__jfkit.jflm.health.missing() : null;
    },
    key: function (id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }
};
