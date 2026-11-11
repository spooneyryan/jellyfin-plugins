// The jellyfin-web anchors Smart Search uses (the kit's anchor rules). Their selectors, with fallbacks, are declared
// once in Web/anchors.json: jellyfin-web's own search button in the header, which the Advanced search button goes
// before, and the collection's item list on its details page, which an auto-collection's note goes before. The kit embeds that list in this script and the server checks health reports against it; the code finds the
// anchors by name through the helpers below. No Smart Search file, this one included, writes a jellyfin-web selector
// (the kit's coupling checks refuse any CSS selector here).

// The view the header's anchor is checked on (anchors.json "views"): every view that shows the legacy header.
const SS_HEADER_VIEW = 'header';

// jellyfin-web's search button in the header, or null when the header has none (the experimental layout, or a header
// jellyfin-web hasn't built yet); the header button is then left out.
function ssHeaderSearchButton() {
    return kit.anchors.find('headerSearch');
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
    return kit.health.expect(['headerSearch'], SS_HEADER_VIEW);
}
