// The details page: the "Add bookmark" button and the "My bookmarks" section.

function visibleDetailPage() {
    const page = findAnchor('detailPage');
    return page && findAnchor('detailContent', page) ? page : null;
}

function isBookmarkable(item) {
    return !!item && item.MediaType === 'Video' && !item.IsFolder
        && NON_BOOKMARKABLE_TYPES.indexOf(item.Type) === -1 && item.LocationType !== 'Virtual';
}

function removeDetailUi(page) {
    page.querySelectorAll('.jfbm-section, .btnBookmarkItem').forEach(function (node) {
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
        if (!known.loading && known.item) {
            ensureDetailUi(page, known);
        }

        return;
    }

    const entry = { itemId: itemId, loading: true, item: null, list: null };
    state.detailPages.set(page, entry);
    removeDetailUi(page);
    try {
        const client = kit.client();
        const item = await client.getItem(kit.user.id(), itemId);
        entry.item = isBookmarkable(item) ? item : null;
    } catch (e) {
        report(e);
    } finally {
        entry.loading = false;
    }

    if (state.detailPages.get(page) === entry && entry.item) {
        ensureDetailUi(page, entry);
    }
}

function ensureDetailUi(page, entry) {
    if (!page.querySelector('.btnBookmarkItem')) {
        addDetailButton(page, entry.item);
    }

    const section = page.querySelector('.jfbm-section');
    if (!section || section.getAttribute('data-item-id') !== entry.itemId) {
        if (section) {
            section.remove();
        }

        entry.list = addDetailSection(page, entry.item);
    } else if (entry.list && !state.lists.has(entry.list)) {
        // The page was restored from jellyfin-web's view cache (for example after Back).
        registerList(entry.list);
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

// "My bookmarks (n)" (1.0's "Bookmarks (n)"), first in the page's content, hidden while the video has none.
function addDetailSection(page, item) {
    const content = findAnchor('detailContent', page);
    const parts = kit.section({ title: 'My bookmarks', className: 'jfbm-section', bodyClass: 'jfbm-list', hidden: true, attrs: { 'data-item-id': item.Id } });
    const links = h('div', { class: 'jfbm-section-links' });
    kit.setHidden(links, true);
    parts.section.insertBefore(links, parts.body);
    content.insertBefore(parts.section, content.firstChild);

    const list = {
        mode: 'details',
        itemId: item.Id,
        container: parts.body,
        onJump: function (bookmark) {
            playFrom(bookmark).catch(function (e) {
                toast(e.message);
            });
        },
        onRendered: function (bookmarks) {
            kit.setHidden(parts.section, bookmarks.length === 0);
            parts.title.textContent = 'My bookmarks (' + bookmarks.length + ')';
            renderSectionLinks(links, item.Id, list.capabilities);
        }
    };
    registerList(list);
    return list;
}

// The section's page links, each only while the server offers its page (Capabilities.Pages): "See all"
// opens My Bookmarks filtered to this video and "My Clips" the clips page (since 1.2.0). Bookmarks 1.1.0
// offers no My Clips page, so it never shows that link.
function renderSectionLinks(container, itemId, capabilities) {
    const pages = offeredPages(capabilities);
    const links = [];
    if (pages.bookmarks) {
        links.push(kit.pages.link('bookmarks', { item: itemId }, 'See all'));
    }

    if (pages.clips) {
        links.push(kit.pages.link('clips', null, 'My Clips'));
    }

    container.replaceChildren.apply(container, links);
    kit.setHidden(container, links.length === 0);
    kit.upgrade(container);
}

// Jellyfin Enhanced 12.14's Reviews on the TV layout (a focus trap found in testing 1.2.0): while its closed Reviews
// section sits above "My bookmarks", the D-pad can't get past it. The kit's mitigation (kit.je.tvSectionJump) puts a "Bookmarks" button in
// the details buttons row, before More, that takes the focus to the section's first control, and takes the reviews' hidden
// controls out of jellyfin-web's focus order while the section is closed, so Down and Up go past it again. Nothing of it
// outside the TV layout, nor while the reviews are open or the video has no bookmarks (the section hidden).
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
        const section = page.querySelector('.jfbm-section');
        return section && !kit.isHidden(section) ? section : null;
    }
});
