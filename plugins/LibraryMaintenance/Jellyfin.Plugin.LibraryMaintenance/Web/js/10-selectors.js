/*
 * Library Maintenance's web client for jellyfin-web 10.11, on the shared kit. The kit's
 * EmbeddedScript concatenates kit.js and the files of this folder in name order into one IIFE, served by
 * GET /LibraryMaintenance/ClientScript and added to index.html by the kit's middleware while EnableWebUi is on. It adds:
 *   - "Move media to new location" to the item "⋮" menu of administrators: cards, list rows, right-click and the
 *     details page's More (40-move-menu.js, through kit.actionSheet; never on the TV layout);
 *   - the Move dialog (50-move-dialog.js, 51-move-picker.js, 55-move-preview.js): what moves, where to, and the
 *     server's plan as a preview with its warnings and blockers. This build only previews: Capabilities.CanMove is
 *     false, no Move button is shown and nothing on disk changes.
 * It only talks to the plugin's REST API (/LibraryMaintenance), whose Move routes are all RequiresElevation; what the
 * client shows or hides is cosmetic.
 *
 * This file: the jellyfin-web anchors Move's code relies on beyond the kit's.
 * The item menu itself (the sheet, its rows, the triggers, the details page's version select) is the kit's
 * (kit.actionSheet). The selectors are declared once in Web/anchors.json, which the kit embeds in this script and
 * the server checks health reports against; the code finds them by name through the helpers below. No file of this
 * folder, this one included, writes a jellyfin-web selector (the kit's coupling checks refuse any CSS selector here).
 */

// The health view of the item menu's anchors (anchors.json "views"; the kit reports its own sheet anchors there too).
const MOVE_MENU_VIEW = 'menus';

// The item menu's command for "Media info", in ctx.commands: an item with media sources.
const MOVE_MEDIA_INFO_COMMAND = 'moremediainfo';

// The jellyfin-web route of an item's details page (#/details?id=<id>&serverId=<sid>).
const MOVE_DETAILS_ROUTE = '/details';

// Whether the menu's commands include "Media info".
function moveSheetHasMediaInfo(commands) {
    return (commands || []).indexOf(MOVE_MEDIA_INFO_COMMAND) >= 0;
}

// The path of the current route: '/details' for #/details?id=…, '/home' for #/home.
function moveRoutePath() {
    return String(window.location.hash || '').replace(/^#!?/, '').split('?')[0];
}

function moveOnDetailsPage() {
    return moveRoutePath() === MOVE_DETAILS_ROUTE;
}

// The item id the details page shows (its hash's id), or null on another route.
function moveDetailsItemId() {
    return moveOnDetailsPage() ? kit.hashParams().get('id') : null;
}

// The details page's More button (the kit's anchor): checked on each viewshow of a details page while the entry is on;
// missing, the details entry can't be reached, and the kit's health report says so once per visit.
function moveCheckDetailsAnchors() {
    return kit.health.expect(['kit.detailsMoreButton'], MOVE_MENU_VIEW);
}

// The "Media info" row, checked on a details page's sheet when the server says the item is eligible: missing there, the
// marker the entry falls back to while the eligibility isn't known has gone (reported once per visit).
function moveCheckMediaInfoRow() {
    return kit.health.expect(['mediaInfoRow'], MOVE_MENU_VIEW, { grace: 0 });
}

// The Move row's icon: the glyph class of anchors.json "moveIcon" (the last class of its selector), so the name is
// written once, where Test-WebAnchors.ps1 checks it; jellyfin-web's folder_open without the declaration.
function moveIconName() {
    const declared = kit.anchors.get('moveIcon');
    const match = declared ? /[A-Za-z0-9_-]+$/.exec(declared.css[0]) : null;
    return match ? match[0] : 'folder_open';
}

// Whether the Move row's icon in a sheet has its glyph (anchors.json "moveIcon"): true when it has, or when this browser
// can't tell; false when jellyfin-web's stylesheet has no rule for it (the row then shows its text only).
function moveIconHasGlyph(sheet) {
    const icon = kit.anchors.find('moveIcon', sheet);
    if (!icon) {
        return false;
    }

    try {
        const content = window.getComputedStyle(icon, '::before').content;
        return !(content === 'none' || content === 'normal' || content === '');
    } catch (e) {
        return true;
    }
}
