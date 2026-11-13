// The jellyfin-web anchors Smart Search uses (the kit's anchor rules). Their selectors, with fallbacks, are declared
// once in Web/anchors.json: jellyfin-web's own search button in the header, which the Advanced search button goes
// before, and the collection's item list on its details page, which an auto-collection's note goes before. The kit embeds that list in this script and the server checks health reports against it; the code finds the
// anchors by name through the helpers below. No Smart Search file, this one included, writes a jellyfin-web selector
// (the kit's coupling checks refuse any CSS selector here).

// The view the header's anchor is checked on (anchors.json "views"): every view that shows a header.
const SS_HEADER_VIEW = 'header';

// The header's search button of the layout in use: the legacy header's, or the modern app bar's (Jellyfin 12's default
// layout, 10.11's experimental one).
function ssHeaderAnchorName() {
    return kit.layout.isModern() ? 'headerSearchModern' : 'headerSearch';
}

// jellyfin-web's search button in the header, or null when the header has none (one jellyfin-web hasn't built yet); the
// header button is then left out.
function ssHeaderSearchButton() {
    return kit.anchors.find(ssHeaderAnchorName());
}

// The view the details page's anchor is checked on.
const SS_DETAILS_VIEW = 'details';

// What the note goes right before in the shown details view, or null (another view, or one jellyfin-web hasn't built
// yet).
function ssCollectionNoteBefore() {
    return kit.anchors.find('collectionNoteBefore');
}

// The health self-check on an auto-collection's details page: a missing place warns once and is reported.
function ssCheckCollectionNoteAnchor() {
    return kit.health.expect(['collectionNoteBefore'], SS_DETAILS_VIEW);
}

// The health self-check (the kit's): on a view that shows the header, the kit checks that the search button is there;
// a missing one warns once in the console and is reported to the server.
function ssCheckHeaderAnchor() {
    return kit.health.expect([ssHeaderAnchorName()], SS_HEADER_VIEW);
}
