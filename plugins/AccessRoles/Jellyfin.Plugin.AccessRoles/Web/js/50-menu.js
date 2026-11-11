/*
 * The Settings page's note, the MUI user menu's keyboard fix and the Display (account) fields. The
 * hiding itself is the head's CSS; these change only attributes and add one node of our own, re-applied by the kit's
 * observer when React or jellyfin-web re-render (we never remove their nodes: React would break).
 */

const NOTE_ATTRIBUTE = 'data-jfar-note';
const DISABLED_ATTRIBUTE = 'data-jfar-disabled';
const FOCUS_FIX_DELAYS_MS = Object.freeze([60, 200, 400]);
const DISPLAY_REAPPLY_MS = 4000;

// The Settings page's note above Sign out while every section is locked (the menu page stays reachable for Sign
// out and Select server, the TV layout's only ones).
function syncSettingsNote() {
    const page = findAnchor('settingsPage');
    const existing = Array.prototype.slice.call(document.querySelectorAll('[' + NOTE_ATTRIBUTE + ']'));
    const wanted = !!page && allSettingsLocked();
    existing.forEach(function (note) {
        if (!wanted || !page.contains(note)) {
            note.remove();
        }
    });
    if (!wanted || page.querySelector('[' + NOTE_ATTRIBUTE + ']')) {
        return;
    }

    const message = (RULES.rules && RULES.rules.AllSettingsLocked && RULES.rules.Message && !RULES.rules.NoLibraries) ? RULES.rules.Message : kit.t('settingsManaged');
    const note = kit.h('div', { class: kit.cls('note'), role: 'note', [NOTE_ATTRIBUTE]: '1', text: message });
    const signOut = findAnchor('settingsSignOut', page);
    if (signOut && signOut.parentNode) {
        signOut.parentNode.insertBefore(note, signOut);
    } else {
        page.append(note);
    }
}

// Whether a menu item is hidden: by one of our locks (or the first-load rule), or by any other style that hides it.
function menuItemHidden(item) {
    if (hiddenByUs(item)) {
        return true;
    }

    try {
        return typeof window.getComputedStyle === 'function' && window.getComputedStyle(item).display === 'none';
    } catch (e) {
        return false;
    }
}

// aria-disabled on the hidden items, so MUI's MenuList skips them with the arrow keys (React keeps
// the attribute across re-renders); taken off again when an item shows.
function syncMenu() {
    const menu = findAnchor('userMenu');
    if (!menu) {
        return;
    }

    everyAnchor('userMenuItem', menu).forEach(function (item) {
        if (menuItemHidden(item)) {
            if (item.getAttribute('aria-disabled') !== 'true') {
                item.setAttribute('aria-disabled', 'true');
            }

            item.setAttribute(DISABLED_ATTRIBUTE, '1');
        } else if (item.getAttribute(DISABLED_ATTRIBUTE)) {
            item.removeAttribute('aria-disabled');
            item.removeAttribute(DISABLED_ATTRIBUTE);
        }
    });
}

// The focus fix: MUI focuses the first item its own props don't disable; a hidden first item (Profile) can't
// take it, so it stays on the menu's paper, outside the list, and the arrow keys do nothing. After the menu opens (pointer
// or key on the avatar), the focus goes to the first visible item that isn't aria-disabled.
function fixMenuFocus() {
    FOCUS_FIX_DELAYS_MS.forEach(function (ms) {
        setTimeout(function () {
            const menu = findAnchor('userMenu');
            if (!menu) {
                return;
            }

            const items = everyAnchor('userMenuItem', menu);
            const active = document.activeElement;
            if (!active || !menu.contains(active) || (items.indexOf(active) >= 0 && !menuItemHidden(active))) {
                // The menu isn't open (MUI puts the focus inside it when it opens), or the focus is on a visible item.
                return;
            }

            const first = items.filter(function (item) {
                return !menuItemHidden(item) && item.getAttribute('aria-disabled') !== 'true';
            })[0];
            if (first) {
                first.focus();
                guardLog('menu-focus', { ms: ms });
            }
        }, ms);
    });
}

function onAvatar(event) {
    if (!closestAnchor(event.target, 'userMenuButton')) {
        return;
    }

    if (event.type === 'keydown' && ['Enter', ' ', 'Spacebar', 'ArrowDown'].indexOf(event.key) < 0) {
        return;
    }

    syncMenu();
    fixMenuFocus();
}

// The Display (account) fields, disabled, not hidden: the page stays for the layout and language. The experimental
// page renders late, so the disabling is re-applied for a few seconds after viewshow; the layout selector
// stays enabled.
function syncDisplayFields() {
    const page = findAnchor('displayPage');
    if (!page) {
        return;
    }

    const locked = isLocked('displayaccount');
    DISPLAY_ACCOUNT_FIELDS.forEach(function (name) {
        everyAnchor(name, page).forEach(function (field) {
            if (locked && !field.disabled) {
                field.disabled = true;
                field.setAttribute(DISABLED_ATTRIBUTE, '1');
            } else if (!locked && field.getAttribute(DISABLED_ATTRIBUTE)) {
                field.disabled = false;
                field.removeAttribute(DISABLED_ATTRIBUTE);
            }
        });
    });
}

function reapplyDisplayFields() {
    const until = Date.now() + DISPLAY_REAPPLY_MS;
    const tick = function () {
        syncDisplayFields();
        if (Date.now() < until) {
            setTimeout(tick, 100);
        }
    };
    tick();
}

function syncMenusAndPages() {
    syncSettingsNote();
    syncMenu();
    syncDisplayFields();
}

function startMenus() {
    window.addEventListener('pointerdown', onAvatar, true);
    window.addEventListener('keydown', onAvatar, true);
    kit.observe(syncMenusAndPages);
    kit.onView(function (view) {
        syncMenusAndPages();
        if (isAnchor(view, 'settingsPage')) {
            checkViewAnchors('settings');
        }

        if (isAnchor(view, 'displayPage')) {
            checkViewAnchors('display');
            reapplyDisplayFields();
        }
    });
    onRulesChanged(function () {
        syncMenusAndPages();
    });
}
