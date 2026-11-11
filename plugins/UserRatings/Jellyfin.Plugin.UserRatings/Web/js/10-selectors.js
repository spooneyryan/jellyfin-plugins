// The jellyfin-web anchors User Ratings uses (the kit's anchor rules). Their
// selectors, with fallbacks, are declared once in Web/anchors.json (the details page, its
// content column and the section the ratings go before; the media-info row the chip goes into, the
// buttons row and the button the Rate button goes before). The kit embeds that list in this script and the server
// checks health reports against it; the code finds the anchors by name through the helpers below. No User Ratings file,
// this one included, writes a jellyfin-web selector (the kit's coupling checks refuse any CSS selector here).

// The views whose anchors User Ratings checks on every viewshow (anchors.json "views"), with the jellyfin-web route each
// shows on. An anchor inside another is looked for there ({ name, within }).
const RATINGS_VIEWS = Object.freeze({
    details: Object.freeze({
        route: '/details',
        anchors: Object.freeze([
            'detailPage', { name: 'detailContent', within: 'detailPage' }, { name: 'sectionBefore', within: 'detailContent' },
            { name: 'miscInfo', within: 'detailPage' }, { name: 'detailButtons', within: 'detailPage' }, { name: 'detailButtonsBefore', within: 'detailButtons' }
        ])
    })
});

// The first element of an anchor inside scope (the document by default): its primary selector, else a fallback; null
// when jellyfin-web has none, and the feature that needs it stays off for that view.
function findAnchor(name, scope) {
    return kit.anchors.find(name, scope);
}

// The User Ratings view of the current route ('details'), or null.
function currentRatingsView() {
    const path = String(window.location.hash || '').replace(/^#!?/, '').split('?')[0];
    const names = Object.keys(RATINGS_VIEWS);
    for (let i = 0; i < names.length; i++) {
        if (path === RATINGS_VIEWS[names[i]].route) {
            return names[i];
        }
    }

    return null;
}

// The details page that is shown, with its content column; null on any other view, or when an anchor it needs is
// missing ("Anchor missing": the feature is off for that view, and the health check below reports it).
function shownDetailPage() {
    if (currentRatingsView() !== 'details') {
        return null;
    }

    const page = findAnchor('detailPage');
    return page && findAnchor('detailContent', page) ? page : null;
}

// Where the section goes in a details page (sectionBefore): before the cast (else the guest cast, else
// More like this) inside the content column, or at the column's end. Returns { parent, before }, or null without the column.
function sectionPlace(page) {
    const content = findAnchor('detailContent', page);
    if (!content) {
        return null;
    }

    const before = findAnchor('sectionBefore', content);
    return before && before.parentNode ? { parent: before.parentNode, before: before } : { parent: content, before: null };
}

// The media-info row of a details page, the chip's place (miscInfo), or null.
function chipRow(page) {
    return findAnchor('miscInfo', page);
}

// Where the Rate button goes in a details page (detailButtons, detailButtonsBefore): before More in
// the buttons row, else at the row's end. Returns { parent, before }, or null without the row.
function rateButtonPlace(page) {
    const buttons = findAnchor('detailButtons', page);
    if (!buttons) {
        return null;
    }

    const before = findAnchor('detailButtonsBefore', buttons);
    return { parent: buttons, before: before && before.parentNode === buttons ? before : null };
}

// The health self-check (the kit's): on each viewshow of a User Ratings view, the kit checks the anchors its
// features need; a missing one warns once in the console and is reported to the server.
function checkRatingsAnchors() {
    const view = currentRatingsView();
    if (view) {
        kit.health.expect(RATINGS_VIEWS[view].anchors, view);
    }
}
