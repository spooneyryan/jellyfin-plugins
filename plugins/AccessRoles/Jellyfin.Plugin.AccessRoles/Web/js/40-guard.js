/*
 * The route guard (built on jellyfin-web 10.11's router, as measured on a 10.11.5 sandbox): a locked section's page is
 * never shown. Redirect on the blocked page's own viewshow (the hash is still the blocked route), or at once on a
 * HISTORY_UPDATE whose pending target is blocked (location.replace abandons the lazy navigation, so the page is never
 * built); never on an idle update alone (a legacy view loads after it and would show under #/home). A cold load is checked
 * once at startup (legacy views emit no HISTORY_UPDATE then). Clicks on a locked link stop in the capture phase.
 * jfar-blocking keeps the settings pages invisible from a blocked target until a non-blocked page's viewshow. viewshow's
 * propagation is never stopped (the router waits for it). Every redirect and blocked click shows the toast, the
 * only feedback the stock pages give, unless BlockedNavigationNotice is Silent; one toast for what happens
 * within NOTICE_REPEAT_MS. A route ends at a second '#' as the router reads it; a page event decides by
 * the router's last location (HISTORY_UPDATE), the hash only before the router said anything; a blocked hash met while a
 * redirect is under way is replaced again, and checked once more when that redirect's window ends.
 */

// The settings routes each lock blocks (jellyfin-web 10.11's routes). The Settings menu itself stays reachable: in the TV
// layout it is the only place with Sign out.
const SECTION_ROUTES = Object.freeze({
    profile: '/userprofile',
    quickconnect: '/quickconnect',
    displaydevice: '/mypreferencesdisplay',
    home: '/mypreferenceshome',
    playback: '/mypreferencesplayback',
    subtitles: '/mypreferencessubtitles',
    controls: '/mypreferencescontrols'
});

const HOME_ROUTE = '#/home';
const REDIRECT_RESET_MS = 1500;
const NOTICE_REPEAT_MS = 1500;

const GUARD = { redirecting: false, staleCount: 0, hooked: false, log: [], lastLocation: null, noticeAt: -Infinity };

function guardLog(type, data) {
    const entry = { t: Math.round(window.performance && window.performance.now ? window.performance.now() : 0), type: type };
    Object.keys(data || {}).forEach(function (key) {
        entry[key] = data[key];
    });
    GUARD.log.push(entry);
    if (GUARD.log.length > 200) {
        GUARD.log.shift();
    }
}

// The path of a route, normalised: '#' and '!' stripped, the query and a fragment dropped (the router reads
// '#/mypreferenceshome#x' as /mypreferenceshome), each segment percent-decoded, lower case, no trailing '/'
// or '.html'. Takes a hash or href string, or a router location ({ pathname }).
function normalizePath(raw) {
    let path = raw && typeof raw === 'object' ? String(raw.pathname || '') : String(raw || '');
    if (path.charAt(0) === '#') {
        path = path.substring(1);
    }

    if (path.charAt(0) === '!') {
        path = path.substring(1);
    }

    const end = path.search(/[?#]/);
    if (end >= 0) {
        path = path.substring(0, end);
    }

    if (path.charAt(0) !== '/') {
        path = '/' + path;
    }

    path = path.split('/').map(function (segment) {
        try {
            return decodeURIComponent(segment);
        } catch (e) {
            return segment;
        }
    }).join('/').toLowerCase();
    while (path.length > 1 && path.charAt(path.length - 1) === '/') {
        path = path.substring(0, path.length - 1);
    }

    if (path.length > 5 && path.substring(path.length - 5) === '.html') {
        path = path.substring(0, path.length - 5);
    }

    return path;
}

// The locked section a route belongs to, or null. A locked user is never an administrator (no lock applies to one), so
// ?userId=<other> exempts nothing here (in jellyfin-web it only lets an administrator edit another user).
function blockedSection(raw) {
    const path = normalizePath(raw);
    const locked = lockedSections();
    const sections = Object.keys(SECTION_ROUTES);
    for (let i = 0; i < sections.length; i++) {
        if (locked.indexOf(sections[i]) >= 0 && SECTION_ROUTES[sections[i]] === path) {
            return sections[i];
        }
    }

    return null;
}

// The route a page event belongs to: the router's last location (HISTORY_UPDATE), which the page shows; the hash before
// the router said anything (a cold load of a legacy view emits no update).
function currentRoute() {
    return GUARD.lastLocation || window.location.hash;
}

// The locked section a settings page belongs to, by its page id: a page shared by two sections (Home screen and
// Subtitles) is blocked only when both are locked, else the route decides.
function pageSection(page) {
    const names = Object.keys(PAGE_SECTIONS);
    const locked = lockedSections();
    for (let i = 0; i < names.length; i++) {
        if (isAnchor(page, names[i])) {
            const sections = PAGE_SECTIONS[names[i]];
            const hit = sections.filter(function (s) {
                return locked.indexOf(s) >= 0;
            });
            if (hit.length === 0) {
                return null;
            }

            return hit.length === sections.length ? hit[0] : (blockedSection(currentRoute()) || blockedSection(window.location.hash));
        }
    }

    return null;
}

// The toast, once for what happens within NOTICE_REPEAT_MS (a redirect and the late view it leaves behind, two clicks).
function notify() {
    const now = Date.now();
    if (HEAD.notice === 'Silent' || now - GUARD.noticeAt < NOTICE_REPEAT_MS) {
        return;
    }

    GUARD.noticeAt = now;
    kit.toast(kit.t('pageManaged'));
}

// Home instead of a blocked route. While a redirect is under way (its replace not yet seen by the router) another one is
// skipped, unless the hash is blocked again: a second locked navigation within the window (which used to leave a blank
// page) is replaced too. When the window ends the hash is checked once more.
function redirectHome(reason) {
    document.documentElement.classList.add(BLOCKING_CLASS);
    if (GUARD.redirecting && !blockedSection(window.location.hash)) {
        guardLog('redirect-skipped', { reason: reason });
        return;
    }

    guardLog(GUARD.redirecting ? 'redirect-again' : 'redirect', { reason: reason });
    const first = !GUARD.redirecting;
    GUARD.redirecting = true;
    window.location.replace(HOME_ROUTE);
    notify();
    if (first) {
        setTimeout(function () {
            GUARD.redirecting = false;
            checkLocation('redirect-window');
        }, REDIRECT_RESET_MS);
    }
}

// A blocked legacy view that showed after the route moved on (its view loaded late): the hash is no longer
// blocked, so a replace with the same hash would do nothing; another query makes the router show Home again. With the
// toast, unless the redirect it follows has just shown it.
function redirectStale(reason) {
    document.documentElement.classList.add(BLOCKING_CLASS);
    GUARD.staleCount++;
    guardLog('redirect-stale', { reason: reason });
    window.location.replace(HOME_ROUTE + '?jfar=' + GUARD.staleCount);
    notify();
}

// The startup check (a cold load of a legacy settings view emits no HISTORY_UPDATE), run again whenever the rules
// change (a cached answer, then /Me's).
function checkLocation(reason) {
    if (blockedSection(window.location.hash)) {
        redirectHome(reason);
    }
}

// jellyfin-web's router events (window.Events, HISTORY_UPDATE): a pending lazy navigation to a blocked route is
// abandoned at once; an idle update only marks the pages hidden (the page's own viewshow redirects).
function onHistoryUpdate(event, state) {
    const navigation = state && state.navigation;
    const pending = !!navigation && navigation.state !== 'idle' && !!navigation.location;
    if (state && state.location) {
        GUARD.lastLocation = state.location;
    }

    const target = pending ? navigation.location : (state && state.location);
    const section = blockedSection(target);
    guardLog('history', { navigation: navigation ? navigation.state : null, blocked: section });
    if (!section) {
        return;
    }

    document.documentElement.classList.add(BLOCKING_CLASS);
    if (pending && navigation.state === 'loading') {
        redirectHome('history-pending');
    }
}

function hookHistory() {
    if (GUARD.hooked) {
        return true;
    }

    const events = window.Events;
    if (!events || typeof events.on !== 'function') {
        return false;
    }

    events.on(document, 'HISTORY_UPDATE', onHistoryUpdate);
    GUARD.hooked = true;
    return true;
}

// The link a click lands on (an <a> with an href), or null.
function linkOf(target) {
    for (let node = target; node && node.nodeType === 1; node = node.parentNode) {
        if (String(node.tagName).toUpperCase() === 'A' && node.getAttribute('href')) {
            return node;
        }
    }

    return null;
}

// Capture phase, before emby-linkbutton's handler and React's root listener.
function onClick(event) {
    const link = linkOf(event.target);
    const href = link ? link.getAttribute('href') || '' : '';
    if (href.charAt(0) !== '#' || !blockedSection(href)) {
        return;
    }

    event.preventDefault();
    event.stopPropagation();
    if (event.stopImmediatePropagation) {
        event.stopImmediatePropagation();
    }

    guardLog('click-blocked', {});
    notify();
}

// viewshow and pageshow, capture phase, never stopped: a blocked settings page redirects on its own viewshow; another
// page's viewshow lifts jfar-blocking. The blocked page's pageshow, which comes after the redirect, is ignored. The route
// is the router's (currentRoute), the hash too: either blocked blocks.
function onPageEvent(page, type) {
    const preference = isAnchor(page, 'preferencePage');
    const section = preference ? (blockedSection(window.location.hash) || blockedSection(currentRoute()) || pageSection(page)) : null;
    if (section) {
        document.documentElement.classList.add(BLOCKING_CLASS);
        if (type === 'viewshow') {
            if (blockedSection(window.location.hash)) {
                redirectHome('viewshow');
            } else {
                redirectStale('viewshow');
            }
        }

        return;
    }

    if (type === 'viewshow' && page && page.getAttribute && page.getAttribute('data-role') === 'page') {
        document.documentElement.classList.remove(BLOCKING_CLASS);
    }
}

// A hash change alone never lifts jfar-blocking; without the router's events (a jellyfin-web without window.Events), it
// redirects itself.
function onHashChange() {
    if (!blockedSection(window.location.hash)) {
        return;
    }

    document.documentElement.classList.add(BLOCKING_CLASS);
    if (!GUARD.hooked) {
        redirectHome('hashchange');
    }
}

function startGuard() {
    window.addEventListener('click', onClick, true);
    ['viewshow', 'pageshow'].forEach(function (name) {
        document.addEventListener(name, function (event) {
            try {
                onPageEvent(event.target, event.type);
            } catch (e) {
                kit.report(e);
            }
        }, true);
    });
    window.addEventListener('hashchange', onHashChange, true);
    window.addEventListener('popstate', onHashChange, true);

    // window.Events is assigned before this script runs (on 10.11.5); poll briefly for a slower bundle.
    let tries = 0;
    const tryHook = function () {
        if (!hookHistory() && ++tries < 200) {
            setTimeout(tryHook, 25);
        }
    };
    tryHook();
    checkLocation('startup');
    onRulesChanged(function (reason) {
        checkLocation(reason);
    });
}
