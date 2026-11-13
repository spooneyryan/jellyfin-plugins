// Entry points outside the nav: the "Advanced search" button in jellyfin-web's header, right before its own search
// button (10-selectors.js). The button is there only while the server's head fragment lists the page as on
// (EnableSearchPage) and ShowHeaderButton is on (the entry points' JSON block, SmartSearchKit.EntryPointsFragment), and
// it copies the native button's hide class through an attribute observer, so it shows exactly when that button does:
// after a local user signs in, not on the login page, not after signing out. It is added once and checked again on every
// scan and viewshow, so a header jellyfin-web builds again gets it back. In the modern layout (Jellyfin 12's default)
// the native button is the app bar's, which React draws: the button goes before it the same way. Without the native
// button there is no button; the page's own link remains, and the health check reports the missing anchor once.

const SS_HEADER_ATTR = 'data-jfss-header';
const SS_ENTRY_POINTS_ATTR = 'data-jfss-entrypoints';

let ssEntryPointsRead = null;
const ssHeader = { observer: null, watched: null };

// The entry points' settings from the head fragment ({ headerButton, collectionNote }), read once a page load; the
// defaults without it.
function ssEntryPoints() {
    if (ssEntryPointsRead === null) {
        ssEntryPointsRead = { headerButton: true, collectionNote: true };
        const node = document.querySelector('script[type="application/json"][' + SS_ENTRY_POINTS_ATTR + ']');
        if (node) {
            try {
                const value = JSON.parse(node.textContent);
                if (value && typeof value === 'object') {
                    ssEntryPointsRead.headerButton = value.headerButton !== false;
                    ssEntryPointsRead.collectionNote = value.collectionNote !== false;
                }
            } catch (e) {
                kit.report(e);
            }
        }
    }

    return ssEntryPointsRead;
}

// Whether the header button belongs on this page load: the page is on and the setting wants the button.
function ssHeaderButtonWanted() {
    return kit.pages.enabled(SS_PAGE.key) && ssEntryPoints().headerButton;
}

function ssHeaderButtons() {
    return Array.prototype.slice.call(document.querySelectorAll('button[' + SS_HEADER_ATTR + ']'));
}

function ssOpenSearchPage() {
    const href = kit.pages.href(SS_PAGE.key);
    if (href && window.location.hash !== href) {
        window.location.hash = href;
    }
}

function ssWatchHeaderSearch(native) {
    if (ssHeader.watched === native) {
        return;
    }

    if (ssHeader.observer) {
        ssHeader.observer.disconnect();
        ssHeader.observer = null;
    }

    ssHeader.watched = native;
    if (native && typeof MutationObserver === 'function') {
        ssHeader.observer = new MutationObserver(function () {
            try {
                ssSyncHeaderButton();
            } catch (e) {
                kit.report(e);
            }
        });
        ssHeader.observer.observe(native, { attributes: true, attributeFilter: ['class'] });
    }
}

// Adds the button before jellyfin-web's search button once, keeps it there, and gives it that button's visibility.
// Never throws; without the native button nothing is added (the health check, on viewshow, reports it).
function ssSyncHeaderButton() {
    if (!ssHeaderButtonWanted()) {
        ssHeaderButtons().forEach(function (button) {
            button.remove();
        });
        ssWatchHeaderSearch(null);
        return;
    }

    const native = ssHeaderSearchButton();
    if (!native || !native.parentNode) {
        ssWatchHeaderSearch(null);
        return;
    }

    let button = null;
    ssHeaderButtons().forEach(function (candidate) {
        // One button, in the header that is shown now: one left in a header jellyfin-web replaced goes.
        if (!button && candidate.parentNode === native.parentNode) {
            button = candidate;
        } else {
            candidate.remove();
        }
    });
    if (!button) {
        button = kit.headerButton({ icon: SS_PAGE.icon, title: SS_TEXT.headerButton, onClick: ssOpenSearchPage });
        button.setAttribute(SS_HEADER_ATTR, '');
        native.parentNode.insertBefore(button, native);
        kit.upgrade(button);
    } else if (button.nextSibling !== native) {
        native.parentNode.insertBefore(button, native);
    }

    if (kit.isHidden(button) !== kit.isHidden(native)) {
        kit.setHidden(button, kit.isHidden(native));
    }

    ssWatchHeaderSearch(native);
}

// On a view's show, and when the user becomes known (the first view of a page load shows before that): the button, then
// the health check of its anchor (the header exists on every view of the legacy layout; jellyfin-web may build it a
// moment after the first view, which the check's grace allows).
function ssOnViewShowHeader() {
    ssSyncHeaderButton();
    if (ssHeaderButtonWanted() && kit.user.id()) {
        ssCheckHeaderAnchor().then(function (present) {
            if (present) {
                ssSyncHeaderButton();
            }
        });
    }
}

kit.observe(ssSyncHeaderButton);
kit.onView(ssOnViewShowHeader);
kit.user.onChange(function (id) {
    if (id) {
        ssOnViewShowHeader();
    }
});
