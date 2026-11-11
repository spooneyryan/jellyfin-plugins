// Move's entry in the item "⋮" menu: "Move media to new location" after "Refresh
// metadata", through kit.actionSheet.addItem. The kit adds it only to the item menu of an administrator (the menu's
// admin-only commands and kit.user.isAdmin()), records the card, row, right-click or details page it was opened from, and
// runs onSelect after the sheet's own close event. Here, when(ctx) decides synchronously from what is already known:
//   - the capabilities (an administrator, Move on and supported, ShowInItemMenu on), loaded when the user is known and
//     again on a viewshow at most once a minute; until they are known the entry is off;
//   - never the TV layout (by design, for now), never the bulk menu (one item at a time in this version), one item;
//   - the item: a Movie, Episode or MusicVideo that isn't a folder on cards and rows (the kit reads data-type and
//     data-isfolder); on the details page, where the kit knows no type, a sheet with "Media info";
//   - the server's eligibility when it is known (GET /Move/Eligibility, batched and kept for 5 minutes): an item it says
//     can't move has no entry. An item it hasn't said anything about is asked about then, in the next batch, so the menu
//     is right from the next time on; the dialog answers for it the first time.
// A details page asks about its item on each viewshow, in one batch with anything else waiting. Cards are never asked
// about one by one, nor a whole page of them.

const MOVE_MENU_TEXT = 'Move media to new location';
const MOVE_MENU_ICON = moveIconName();
const MOVE_ITEM_KINDS = Object.freeze(['Movie', 'Episode', 'MusicVideo']);

const moveMenu = { iconChecked: false };

function moveMenuWanted(ctx) {
    if (ctx.isBulk || ctx.itemIds.length !== 1 || kit.layout.isTv()) {
        return false;
    }

    // Stale capabilities still answer now; the next viewshow or menu asks again.
    moveLoadCapabilities(false).then(null, function () {});
    if (!moveMenuOn()) {
        return false;
    }

    const id = ctx.itemIds[0];
    const known = moveEligibilityOf(id);
    if (ctx.surface === 'details') {
        if (known) {
            if (known.eligible && !moveSheetHasMediaInfo(ctx.commands)) {
                moveCheckMediaInfoRow();
            }

            return !moveKnownIneligible(id);
        }

        moveLookupEligibility([id]);
        return moveSheetHasMediaInfo(ctx.commands);
    }

    if (MOVE_ITEM_KINDS.indexOf(ctx.itemType) < 0 || ctx.isFolder === true) {
        return false;
    }

    if (!known) {
        moveLookupEligibility([id]);
        return true;
    }

    return !moveKnownIneligible(id);
}

// Once a page load, after the first row is in a sheet: whether jellyfin-web still has the row's glyph (moveIcon).
function moveCheckIcon(sheet) {
    if (moveMenu.iconChecked) {
        return;
    }

    setTimeout(function () {
        if (moveMenu.iconChecked || !sheet.isConnected || !kit.anchors.find('moveIcon', sheet)) {
            return;
        }

        moveMenu.iconChecked = true;
        if (!moveIconHasGlyph(sheet)) {
            kit.health.note('moveIcon', 'jellyfin-web has no glyph for the icon of the Move entry (' + MOVE_MENU_ICON + '); the entry shows its text only');
        }
    }, 0);
}

kit.actionSheet.addItem({
    id: 'move',
    icon: MOVE_MENU_ICON,
    text: MOVE_MENU_TEXT,
    when: function (ctx) {
        const wanted = moveMenuWanted(ctx);
        if (wanted) {
            moveCheckIcon(ctx.sheet);
        }

        return wanted;
    },
    onSelect: function (ctx) {
        openMoveDialog({ itemId: ctx.itemIds[0], serverId: ctx.serverId, surface: ctx.surface, trigger: ctx.trigger || null });
    }
});

// The user: another user's or none's capabilities and answers are forgotten at once, and the new user's
// asked for (with the details page's item, when the page loaded on one).
kit.user.onChange(function (id) {
    moveForgetUser();
    if (id) {
        moveOnViewShow();
    }
});

// On each viewshow: the capabilities again when a minute old; on a details page, its item's eligibility and the More
// button's presence, for an administrator with the entry on.
function moveOnViewShow() {
    if (!kit.user.id() || kit.layout.isTv()) {
        return;
    }

    moveLoadCapabilities(false).then(function () {
        const id = moveDetailsItemId();
        if (!id || !moveMenuOn()) {
            return;
        }

        moveLookupEligibility([id]);
        moveCheckDetailsAnchors();
    }, function () {});
}

kit.onView(moveOnViewShow);
