/*
 * Bookmarks web client for jellyfin-web 10.11, on the shared kit. The kit's EmbeddedScript concatenates
 * kit.js and the files of this folder in name order into one IIFE, served by GET /Bookmarks/ClientScript and added to
 * index.html by the kit's middleware. It provides:
 *   - player controls: bookmark the current moment (also the B key), mark a start/end range, and a list of the video's
 *     bookmarks to jump between; the video's bookmarks marked on the player's position slider;
 *   - on video detail pages: an "Add bookmark" button and a "My bookmarks" section with play-from-here, name it, edit,
 *     delete and clip controls, and the Not in library, Unavailable and Relinked badges;
 *   - an editor for name, position, optional end and note, which can also start clip extraction.
 *
 * It only talks to the plugin's REST API (/Bookmarks) and the standard Sessions API. The current playback position comes
 * from the reports the web client sends to the server (kit.playback.onReport).
 *
 * This file: the jellyfin-web anchors Bookmarks uses. Their selectors, with
 * fallbacks, are declared once in Web/anchors.json, which the kit embeds in this script and the server checks health
 * reports against; the code finds them by name through the helpers below. No Bookmarks file, this one included, writes a
 * jellyfin-web selector (the kit's coupling checks refuse any CSS selector here); native styling classes come from the
 * kit's helpers.
 */

// The views whose anchors Bookmarks checks on every viewshow (anchors.json "views"), with the jellyfin-web routes they
// show on. An anchor inside another is looked for there ({ name, within }).
const VIEWS = Object.freeze({
    details: Object.freeze({
        route: '/details',
        anchors: Object.freeze(['detailPage', { name: 'detailContent', within: 'detailPage' }, { name: 'detailButtons', within: 'detailPage' }, { name: 'detailButtonsBefore', within: 'detailPage' }])
    }),
    player: Object.freeze({
        route: '/video',
        anchors: Object.freeze(['playerPage', 'osdButtons', { name: 'osdInsertBefore', within: 'osdButtons' }, { name: 'osdPositionSlider', within: 'playerPage' }])
    })
});

// The first element of an anchor inside scope (the document by default): its primary selector, else a fallback; null
// when jellyfin-web has none, and the feature that needs it stays off.
function findAnchor(name, scope) {
    return kit.anchors.find(name, scope);
}

// Every element of an anchor inside scope.
function findAllAnchors(name, scope) {
    return kit.anchors.all(name, scope);
}

// The Bookmarks view of the current route ('details', 'player'), or null.
function currentView() {
    const path = String(window.location.hash || '').replace(/^#!?/, '').split('?')[0];
    const names = Object.keys(VIEWS);
    for (let i = 0; i < names.length; i++) {
        if (path === VIEWS[names[i]].route) {
            return names[i];
        }
    }

    return null;
}

// The health self-check: on each viewshow of a Bookmarks view, the kit checks the anchors its
// features need; a missing one warns once in the console and is reported to the server.
function checkViewAnchors() {
    const view = currentView();
    if (view) {
        kit.health.expect(VIEWS[view].anchors, view);
    }
}
