// The details page: the "Add bookmark" button, and the Bookmarks and Clips rows (52-detail-rows.js; until 1.6.0 / 2.4.0 a
// "My bookmarks" list).

function visibleDetailPage() {
    const page = findAnchor('detailPage');
    return page && findAnchor('detailContent', page) ? page : null;
}

function isBookmarkable(item) {
    return !!item && item.MediaType === 'Video' && !item.IsFolder
        && NON_BOOKMARKABLE_TYPES.indexOf(item.Type) === -1 && item.LocationType !== 'Virtual';
}

function removeDetailUi(page) {
    page.querySelectorAll('.jfbm-section, .jfbm-detail-clips, .btnBookmarkItem, .jfbm-export-playlist').forEach(function (node) {
        node.remove();
    });
    state.lists.forEach(function (list) {
        if (!list.container.isConnected || page.contains(list.container)) {
            state.lists.delete(list);
        }
    });
}

// Each detail page remembers which item it was checked for, so an item is only fetched once per visit even though the
// page is scanned on every DOM change (the now-playing bar changes constantly).
async function syncDetailPage() {
    const page = visibleDetailPage();
    const itemId = page ? kit.hashParams().get('id') : null;
    if (!page || !itemId) {
        return;
    }

    const known = state.detailPages.get(page);
    if (known && known.itemId === itemId) {
        if (!known.loading && (known.item || known.playlist)) {
            ensureDetailUi(page, known);
        }

        return;
    }

    const entry = { itemId: itemId, loading: true, item: null, rows: null, playlist: null };
    state.detailPages.set(page, entry);
    removeDetailUi(page);
    try {
        const client = kit.client();
        const item = await client.getItem(kit.user.id(), itemId);
        entry.item = isBookmarkable(item) ? item : null;
        // A playlist's page gets "Export to clips folder" (72-playlist-export.js) for a user who may make clips.
        if (isExportablePlaylist(item)) {
            const capabilities = await getCapabilities();
            entry.playlist = capabilities && capabilities.CanCreateClips === true ? item : null;
        }
    } catch (e) {
        report(e);
    } finally {
        entry.loading = false;
    }

    if (state.detailPages.get(page) === entry && (entry.item || entry.playlist)) {
        ensureDetailUi(page, entry);
    }
}

function ensureDetailUi(page, entry) {
    if (entry.playlist) {
        addPlaylistExportButton(page, entry.playlist);
        return;
    }

    if (!page.querySelector('.btnBookmarkItem')) {
        addDetailButton(page, entry.item);
    }

    const section = page.querySelector('.jfbm-section');
    if (!section || section.getAttribute('data-item-id') !== entry.itemId) {
        page.querySelectorAll('.jfbm-section, .jfbm-detail-clips').forEach(function (node) {
            node.remove();
        });
        entry.rows = addDetailRows(page, entry.item);
    } else if (entry.rows) {
        // The page was restored from jellyfin-web's view cache (for example after Back).
        [entry.rows.bookmarks, entry.rows.clips].forEach(function (list) {
            if (!state.lists.has(list)) {
                registerList(list);
            }
        });
    }
}

function addDetailButton(page, item) {
    const buttons = findAnchor('detailButtons', page);
    if (!buttons) {
        return;
    }

    const button = kit.detailButton({
        className: 'btnBookmarkItem',
        icon: 'bookmark_add',
        title: 'Add bookmark',
        onClick: function () {
            openEditor({ itemId: item.Id });
        }
    });
    buttons.insertBefore(button, findAnchor('detailButtonsBefore', buttons));
}

// Jellyfin Enhanced 12.14's Reviews on the TV layout (a focus trap found in testing 1.2.0): while its closed Reviews
// section sits above our rows, the D-pad can't get past it. The kit's mitigation (kit.je.tvSectionJump) puts a "Bookmarks" button in
// the details buttons row, before More, that takes the focus to the first control of the Bookmarks row (else the Clips
// row), and takes the reviews' hidden controls out of jellyfin-web's focus order while the section is closed, so Down and
// Up go past it again. Nothing of it outside the TV layout, nor while the reviews are open or both rows are hidden.
kit.je.tvSectionJump({
    label: 'Bookmarks',
    icon: 'bookmarks',
    page: visibleDetailPage,
    buttons: function (page) {
        return findAnchor('detailButtons', page);
    },
    before: function (buttons) {
        return findAnchor('detailButtonsBefore', buttons);
    },
    target: function (page) {
        const rows = Array.prototype.slice.call(page.querySelectorAll('.jfbm-section, .jfbm-detail-clips'));
        return rows.filter(function (row) {
            return !kit.isHidden(row);
        })[0] || null;
    }
});
