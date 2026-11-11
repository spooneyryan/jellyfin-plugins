/*
 * The jellyfin-web anchors Access Roles uses (by the kit's anchor rules). Their selectors, with fallbacks,
 * are declared once in Web/anchors.json, which the kit embeds in this script, the head fragment's CSS is built from, and
 * the server checks health reports against; the code finds them by name through the helpers below. No file of this
 * folder writes a jellyfin-web selector (the kit's coupling checks refuse any CSS selector here).
 */

// The first element of an anchor inside scope (the document by default), or null.
function findAnchor(name, scope) {
    return kit.anchors.find(name, scope);
}

// Every element any of an anchor's selectors matches inside scope: the anchors whose selectors name different elements
// (the Jellyfin Enhanced entries, a field in either layout) need them all.
function everyAnchor(name, scope) {
    const declared = kit.anchors.get(name);
    const root = scope || document;
    const found = [];
    if (!declared || !root) {
        return found;
    }

    declared.css.forEach(function (selector) {
        let matches = [];
        try {
            matches = Array.prototype.slice.call(root.querySelectorAll(selector));
        } catch (e) {
            matches = [];
        }

        matches.forEach(function (element) {
            if (found.indexOf(element) < 0) {
                found.push(element);
            }
        });
    });
    return found;
}

// Whether an element is one of an anchor's.
function isAnchor(element, name) {
    const declared = kit.anchors.get(name);
    if (!declared || !element || typeof element.matches !== 'function') {
        return false;
    }

    return declared.css.some(function (selector) {
        try {
            return element.matches(selector);
        } catch (e) {
            return false;
        }
    });
}

// The element itself or its closest ancestor that is one of an anchor's, or null.
function closestAnchor(element, name) {
    for (let node = element; node && node.nodeType === 1; node = node.parentNode) {
        if (isAnchor(node, name)) {
            return node;
        }
    }

    return null;
}

// What the head fragment's CSS hides (Web/AccessRolesHead.HideRules, PendingAnchors and BlockingAnchors; a test keeps them
// equal): each anchor with the classes on <html> that hide it. The code reads it to tell what is hidden (the MUI menu's
// keyboard fix, the webchecks' view of the page).
const HIDE_RULES = Object.freeze([
    Object.freeze({ classes: Object.freeze(['jfar-lock-allsettings']), anchor: 'drawerSettings' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-allsettings']), anchor: 'userMenuSettings' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-profile']), anchor: 'userMenuProfile' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-profile']), anchor: 'settingsProfileLink' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-quickconnect']), anchor: 'userMenuQuickConnect' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-quickconnect']), anchor: 'settingsQuickConnectLink' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-displaydevice']), anchor: 'settingsDisplayLink' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-home']), anchor: 'settingsHomeLink' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-playback']), anchor: 'settingsPlaybackLink' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-subtitles']), anchor: 'settingsSubtitlesLink' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-controls']), anchor: 'settingsControlsLink' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-enhancedpanel']), anchor: 'jeEntries' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-allsettings', 'jfar-lock-profile', 'jfar-lock-enhancedpanel']), anchor: 'userMenuDivider' }),
    Object.freeze({ classes: Object.freeze(['jfar-lock-allsettings', 'jfar-lock-profile']), anchor: 'userMenuDividerNoJe' })
]);
const PENDING_ANCHORS = Object.freeze([
    'drawerSettings', 'userMenuProfile', 'userMenuSettings', 'userMenuQuickConnect', 'jeEntries', 'settingsProfileLink', 'settingsDisplayLink',
    'settingsHomeLink', 'settingsPlaybackLink', 'settingsSubtitlesLink', 'settingsControlsLink', 'settingsQuickConnectLink'
]);
const BLOCKING_ANCHORS = Object.freeze(['preferencePage']);

// Whether the head's CSS hides an element now: a rule whose classes <html> carries, or the first-load rule's.
function hiddenByUs(element) {
    const root = document.documentElement;
    const has = function (name) {
        return root.classList.contains(name);
    };
    if (has('jfar-pending') && PENDING_ANCHORS.some(function (anchor) {
        return isAnchor(element, anchor);
    })) {
        return true;
    }

    return HIDE_RULES.some(function (rule) {
        return rule.classes.every(has) && isAnchor(element, rule.anchor);
    });
}

// The entry points the head's CSS hides on the page now, by anchor name (the webchecks read it).
function hiddenEntryPoints() {
    const names = [];
    HIDE_RULES.concat(PENDING_ANCHORS.map(function (anchor) {
        return { anchor: anchor };
    })).forEach(function (rule) {
        if (names.indexOf(rule.anchor) < 0 && everyAnchor(rule.anchor).some(hiddenByUs)) {
            names.push(rule.anchor);
        }
    });
    return names;
}

// The settings pages' roots, by anchor, with the sections whose lock blocks them. Subtitles reuses the Home
// screen page's id (as jellyfin-web 10.11.5 does), so for that page the hash decides.
const PAGE_SECTIONS = Object.freeze({
    'profilePage': Object.freeze(['profile']),
    'quickConnectPage': Object.freeze(['quickconnect']),
    'displayPage': Object.freeze(['displaydevice']),
    'homeScreenPage': Object.freeze(['home', 'subtitles']),
    'playbackPage': Object.freeze(['playback']),
    'controlsPage': Object.freeze(['controls'])
});

// The Display (account) fields, in either layout (checked on jellyfin-web 10.11.5).
const DISPLAY_ACCOUNT_FIELDS = Object.freeze(['displayMissingEpisodes', 'episodeImagesInNextUp', 'dashboardTheme']);

// The health self-check (the kit's anchor health reports): on each viewshow of a view the guard works on, the anchors
// it needs.
function checkViewAnchors(view) {
    if (view === 'settings') {
        kit.health.expect(['settingsPage', { name: 'settingsSignOut', within: 'settingsPage' }], 'settings');
    } else if (view === 'display') {
        kit.health.expect(['displayPage'], 'display');
    }
}
