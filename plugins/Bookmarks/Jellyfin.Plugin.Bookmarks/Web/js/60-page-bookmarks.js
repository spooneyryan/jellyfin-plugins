// The My Bookmarks page: every bookmark of the signed-in user across all videos, in the
// kit's page host (the kit's own route #/jfbm-bookmarks linked from "Library tools" by default, Plugin Pages' host in
// Auto/PluginPagesOnly). A header with Player options (and My Clips once the server offers that page), a toolbar
// (search, type, sort, view, filters; on a TV a Select toggle), the grouped view (one group per video with its
// bookmarks by position, GET /Bookmarks/Groups, 25 groups a page) and the flat view (one row per bookmark with the
// video under its title, GET /Bookmarks/Query, 50 a page), "Load more", and the state in the hash: view, q,
// type, sort, the four filters, item (the details page's "See all"), page (how many pages are shown) and group (the
// video opened from the page, scrolled back to on Back, then dropped from the hash in place: it has one landing), so a
// reload, a deep link and Back show the same list; a value the hash says wrongly (a sort the view lacks, an item or
// group that isn't a video id) is the default, and the hash is left as it came. A toolbar change writes the hash
// through kit.pages.setParams (a new history entry, or the same one for a search typed or a page loaded) and reloads
// the list itself; the kit renders the page again from the hash on Back, Forward, a link and a restored view.
//
// The rows are 35-lists.js's (play from here, Name it, edit, the clip, delete, the badges), with the page's own row
// options: the inline rename (Name it and Rename turn the title into a field; Enter saves through the Name
// endpoint with IfUpdatedAt, Escape cancels, a 409 shows the latest name), a checkbox per row for the bulk delete (the
// bulk bar under the list: "n selected", Select all, Clear, Delete… with "Also delete their clips" as the single delete
// has it, through POST /Bookmarks/BulkDelete in batches of 200, the server's most, stopped after the request under way
// when its dialog is closed, the outcome then a toast), the scissors on a point bookmark (the editor with End
// focused) and, on a narrow screen, one "More" button opening our own menu of the actions. A change made from a row
// (or from the editor, the player, the details page) reaches the page through notifyChanged and redraws only what
// changed: a clip's progress tick the row in place, anything else the video's group read again with the Groups
// endpoint and the page's filters (in the flat view the video's rows, drawn again in place), then the count line; the
// whole list is read again only for a video the page doesn't show or for many videos at once, and a change that comes
// while a load is under way waits for its end. A redraw keeps the selection of the rows
// it draws again and every row whose inline rename is still being typed. The lists
// are lists (role="list" and "listitem"). On the TV layout the checkbox column is hidden until the
// toolbar's Select toggle shows it; the dialogs take the D-pad and the Back key through the kit's scopedKeys. The page
// asks only the Bookmarks endpoints, which list the caller's own bookmarks and ask the library as the caller, and
// requests a poster only for a video the caller can open (ItemAvailable; core serves images unchecked).

// The page as the server's KitPage declares it (BookmarksKit; the key, title, icon and Id suffix must match).
const BOOKMARKS_PAGE = Object.freeze({ key: 'bookmarks', title: 'My Bookmarks', icon: 'collections_bookmark', idSuffix: 'BookmarksPage', order: 10 });

const PAGE_GROUPS_PER_PAGE = 25;
const PAGE_ROWS_PER_PAGE = 50;
const PAGE_MAX_LIMIT = 200;
const PAGE_MAX_PAGES = 20;
const PAGE_SEARCH_MAX_LENGTH = 100;
const PAGE_LOADING_DELAY_MS = 300;
const PAGE_RELOAD_DELAY_MS = 100;
const PAGE_HIGHLIGHT_MS = 2500;
// More changed videos than this in one go (a bulk delete) read the whole list again rather than each video's part.
const PAGE_PATCH_MAX_ITEMS = 5;
// The most ids one POST /Bookmarks/BulkDelete takes (BookmarkManager.MaxBulkDelete); a larger selection goes
// in batches of this many, one after the other.
const BULK_DELETE_BATCH = 200;
// How long the toast of a bulk delete whose dialog was closed before the answer stays (it can name three outcomes).
const BULK_OUTCOME_TOAST_MS = 10000;
const CLIP_IN_USE_BULK = 'a clip file is in use (being played or downloaded). Try again shortly.';
const CLIP_FAILED_BULK = 'the clip file couldn\'t be removed (its folder isn\'t available or isn\'t recognised as its clips\' own, or isn\'t in the server\'s clip folders).';

// The page's sorts, each with its API value in the views that have it: the first of a view is its default. A sort
// both views have (Recently changed, Title A–Z) is kept when the view changes; any other goes back to the default.
const PAGE_SORTS = Object.freeze([
    Object.freeze({ label: 'Recently changed', grouped: 'LatestUpdated', flat: 'UpdatedAt' }),
    Object.freeze({ label: 'Recently created', flat: 'CreatedAt' }),
    Object.freeze({ label: 'Name A–Z', flat: 'Name' }),
    Object.freeze({ label: 'Title A–Z', grouped: 'ItemName', flat: 'ItemName' }),
    Object.freeze({ label: 'Position in title', flat: 'Position' }),
    Object.freeze({ label: 'Most bookmarks', grouped: 'Count' })
]);
const PAGE_TYPES = Object.freeze([
    Object.freeze({ value: '', label: 'All' }),
    Object.freeze({ value: 'Movie', label: 'Movies' }),
    Object.freeze({ value: 'Episode', label: 'Episodes' }),
    Object.freeze({ value: 'Other', label: 'Other' })
]);
// The filters, each a hash key (`range=1`) and the query it sends.
const PAGE_FILTERS = Object.freeze([
    Object.freeze({ key: 'range', label: 'Has range', query: 'hasRange', value: 'true' }),
    Object.freeze({ key: 'clip', label: 'Has clip', query: 'hasClip', value: 'true' }),
    Object.freeze({ key: 'unnamed', label: 'Needs a name', query: 'named', value: 'false' }),
    Object.freeze({ key: 'missing', label: 'Not in library', query: 'availability', value: 'Missing' })
]);
const PAGE_VIEWS = Object.freeze([
    Object.freeze({ value: 'grouped', label: 'Grouped', endpoint: 'Bookmarks/Groups', size: PAGE_GROUPS_PER_PAGE }),
    Object.freeze({ value: 'flat', label: 'Flat', endpoint: 'Bookmarks/Query', size: PAGE_ROWS_PER_PAGE })
]);

function pageView(value) {
    return PAGE_VIEWS.filter(function (view) {
        return view.value === value;
    })[0] || PAGE_VIEWS[0];
}

function pageSorts(view) {
    return PAGE_SORTS.filter(function (sort) {
        return !!sort[view];
    });
}

// The sort of a view named by a hash or API value (any case), else the view's default.
function pageSortFor(view, value) {
    const wanted = String(value || '').toLowerCase();
    return pageSorts(view).filter(function (sort) {
        return sort[view].toLowerCase() === wanted;
    })[0] || pageSorts(view)[0];
}

function pageSortAcross(fromView, value, toView) {
    const current = pageSortFor(fromView, value);
    return current[toView] ? current[toView] : pageSorts(toView)[0][toView];
}

function pageType(value) {
    const wanted = String(value || '').toLowerCase();
    return PAGE_TYPES.filter(function (type) {
        return type.value && type.value.toLowerCase() === wanted;
    })[0] || PAGE_TYPES[0];
}

// A video id as the page keys it (the N format, as the kit's data-id).
function pageItemKey(id) {
    return String(id || '').replace(/-/g, '').toLowerCase();
}

// A video id as a hash parameter (item, group): its key when it is a GUID in any form, else '' (a malformed deep link
// asks the server for nothing and looks nothing up in the DOM).
function pageIdParam(value) {
    const key = pageItemKey(String(value || '').trim());
    return /^[0-9a-f]{32}$/.test(key) ? key : '';
}

// A value for an attribute selector (CSS.escape where the browser has it; the keys and ids here are plain already).
function cssKey(value) {
    return typeof CSS !== 'undefined' && CSS && typeof CSS.escape === 'function' ? CSS.escape(String(value)) : String(value);
}

// The page's state as the hash carries it; anything the hash doesn't say, or says wrongly, is the default.
function readPageState(params) {
    const p = params || {};
    const view = pageView(p.view).value;
    const filters = {};
    PAGE_FILTERS.forEach(function (filter) {
        filters[filter.key] = p[filter.key] === '1' || p[filter.key] === 'true';
    });
    const pages = parseInt(p.page, 10);
    return {
        view: view,
        q: Array.from(String(p.q || '').trim()).slice(0, PAGE_SEARCH_MAX_LENGTH).join(''),
        type: pageType(p.type).value,
        sort: pageSortFor(view, p.sort)[view],
        filters: filters,
        item: pageIdParam(p.item),
        pages: Number.isFinite(pages) ? Math.max(1, Math.min(PAGE_MAX_PAGES, pages)) : 1,
        group: pageIdParam(p.group)
    };
}

// The hash parameters of a state: only what differs from the defaults, so the plain route stays plain.
function writePageState(s) {
    const out = {};
    if (s.view !== PAGE_VIEWS[0].value) {
        out.view = s.view;
    }

    if (s.q) {
        out.q = s.q;
    }

    if (s.type) {
        out.type = s.type;
    }

    if (s.sort !== pageSorts(s.view)[0][s.view]) {
        out.sort = s.sort;
    }

    PAGE_FILTERS.forEach(function (filter) {
        if (s.filters[filter.key]) {
            out[filter.key] = '1';
        }
    });
    if (s.item) {
        out.item = s.item;
    }

    if (s.pages > 1) {
        out.page = String(s.pages);
    }

    if (s.group) {
        out.group = s.group;
    }

    return out;
}

// The query of GET /Bookmarks/Groups or /Query for a state and a page.
function pageQuery(s, startIndex, limit) {
    const query = { sortBy: s.sort, startIndex: startIndex, limit: limit };
    if (s.q) {
        query.searchTerm = s.q;
    }

    if (s.item) {
        query.itemId = s.item;
    }

    if (s.type) {
        query.itemTypes = s.type;
    }

    PAGE_FILTERS.forEach(function (filter) {
        if (s.filters[filter.key]) {
            query[filter.query] = filter.value;
        }
    });
    return query;
}

function pageHasFilters(s) {
    return !!(s.q || s.type || s.item || PAGE_FILTERS.some(function (filter) {
        return s.filters[filter.key];
    }));
}

function plural(count, noun) {
    return count + ' ' + noun + (count === 1 ? '' : 's');
}

// The page shown now, for the change listener below; a render makes a new one.
let currentBookmarksPage = null;

// A bookmark changed from a row, the editor, the player or the details page (35-lists.js's notifyChanged): the changes
// of a moment are collected per video and applied together a moment later, so several in a row cost one pass
// (applyPendingChanges: the row in place, the video's group or rows read again, the count line). A progress tick of a
// clip being made (every two seconds, watchClip) only redraws that bookmark's row, when it is on the page. Nothing is
// done while the page isn't shown: in Plugin Pages' host the page left stays current in its hidden view until it shows
// again, and the kit renders it again then, which reads everything afresh.
state.changeListeners.add(function (itemId, detail) {
    const page = currentBookmarksPage;
    if (!page || !page.context.isShown()) {
        return;
    }

    if (detail && detail.clipTick) {
        patchPageRow(page, detail.bookmark);
        return;
    }

    page.pending.add(pageItemKey(itemId));
    clearTimeout(page.reloadTimer);
    page.reloadTimer = setTimeout(function () {
        applyPendingChanges(page);
    }, PAGE_RELOAD_DELAY_MS);
});

// kit.pages.register's render: the header, the toolbar, the count line, the list, the pager and the bulk bar, then the
// first load. The filters row starts open when the hash has a filter on, and is the user's to fold and unfold after
// that. On the TV layout the root carries jfbm-page-tv (the stylesheet hides the checkbox column until Select).
function renderBookmarksPage(body, params, context) {
    const state = readPageState(params);
    const page = {
        body: body,
        context: context,
        state: state,
        generation: 0,
        loading: false,
        loaded: 0,
        total: null,
        otherTotal: null,
        capabilities: null,
        filtersOpen: PAGE_FILTERS.some(function (filter) {
            return state.filters[filter.key];
        }),
        reloadTimer: null,
        // The videos (keys) whose bookmarks changed since the last pass of applyPendingChanges.
        pending: new Set(),
        // The bookmarks shown (id → bookmark) and those selected for the bulk delete (id → bookmark).
        shown: new Map(),
        selected: new Map(),
        selecting: false,
        tv: kit.layout.isTv()
    };
    currentBookmarksPage = page;
    return getCapabilities().then(function (capabilities) {
        if (!context.isCurrent()) {
            return null;
        }

        page.capabilities = capabilities;
        body.classList.toggle('jfbm-page-tv', page.tv);
        buildPageHeader(page);
        buildPageToolbar(page);
        page.count = h('p', { class: 'jfbm-page-count', role: 'status' });
        page.content = h('div', { class: 'jfbm-page-content' });
        page.pager = kit.pager({
            onMore: function () {
                loadMorePage(page);
            }
        });
        buildBulkBar(page);
        kit.append(body, page.header, page.readOnly, page.toolbar, page.filtersRow, page.count, page.content, page.pager.element, page.bulkBar);
        syncPageToolbar(page);
        return loadPageContent(page);
    });
}

// Why the bookmark data is read-only (Capabilities' StoreReadOnlyReason, the server's BookmarkStoreReadOnlyReason), in a
// plain sentence: the reason's name is never shown.
const READ_ONLY_REASONS = {
    NewerVersion: 'the bookmark data was written by a newer version of Bookmarks',
    MigrationFailed: 'the bookmark data couldn\'t be upgraded to this version of Bookmarks',
    DataMissing: 'the bookmark data is missing, and none of its snapshots could be restored',
    DataUnreadable: 'the bookmark data couldn\'t be read, and no snapshot could take its place'
};

// The read-only notice of My Bookmarks and My Clips: the reason (a reason this version doesn't know
// says only that the data is read-only), then what to do: an admin is sent to the plugin's settings page, which a user
// can't open, so a user is told to ask the server's administrator.
function readOnlyNoticeText(capabilities) {
    const reason = Object.prototype.hasOwnProperty.call(READ_ONLY_REASONS, capabilities.StoreReadOnlyReason) ? READ_ONLY_REASONS[capabilities.StoreReadOnlyReason] : null;
    return 'Bookmarks are read-only right now' + (reason ? ': ' + reason + '.' : '.')
        + (capabilities.IsAdmin === true ? ' The plugin\'s settings page (Dashboard, Plugins, Bookmarks) says what to do.' : ' Ask your server\'s administrator.');
}

// The header's right side (its links): My Clips only once the server offers that page (1.2.0), and the
// Player options dialog (below). A read-only store (a newer plugin version wrote it, its upgrade failed, it is missing or
// can't be read) is said above the toolbar (readOnlyNoticeText); the rows' writes then show the server's sentence.
function buildPageHeader(page) {
    const capabilities = page.capabilities || {};
    const links = [];
    if (offeredPages(capabilities).clips) {
        links.push(kit.pages.link('clips', null, 'My Clips', { class: kit.cls('link') + ' jfbm-page-clips-link' }));
    }

    links.push(kit.button('Player options', {
        icon: 'settings',
        className: 'jfbm-page-options',
        onClick: function () {
            openPlayerOptions(page);
        }
    }));
    page.header = h('div', { class: 'jfbm-page-header' }, links);
    kit.upgrade(page.header);
    page.readOnly = capabilities.StoreReadOnly === true
        ? kit.notice({ kind: 'info', text: readOnlyNoticeText(capabilities) })
        : null;
    if (page.readOnly) {
        page.readOnly.classList.add('jfbm-page-readonly');
    }
}

function buildPageToolbar(page) {
    const s = page.state;
    page.search = kit.searchBox({
        placeholder: 'Search names, notes and titles…',
        label: 'Search bookmarks',
        value: s.q,
        maxLength: PAGE_SEARCH_MAX_LENGTH,
        onSearch: function (text) {
            changePageState(page, { q: text }, { replace: true });
        }
    });
    page.typeButtons = PAGE_TYPES.map(function (type) {
        return h('button', {
            type: 'button',
            class: 'jfbm-page-segment',
            'data-jfbm-type': type.value,
            'aria-pressed': 'false',
            text: type.label,
            onclick: function () {
                changePageState(page, { type: type.value });
            }
        });
    });
    page.sortSelect = kit.select({ class: 'jfbm-page-sort', 'aria-label': 'Sort' });
    page.sortSelect.addEventListener('change', function () {
        changePageState(page, { sort: pageSortFor(page.state.view, page.sortSelect.value)[page.state.view] });
    });
    page.viewButtons = PAGE_VIEWS.map(function (view) {
        return h('button', {
            type: 'button',
            class: 'jfbm-page-segment',
            'data-jfbm-view': view.value,
            'aria-pressed': 'false',
            text: view.label,
            onclick: function () {
                changePageView(page, view.value);
            }
        });
    });
    page.filterBoxes = PAGE_FILTERS.map(function (filter) {
        const box = h('input', {
            type: 'checkbox',
            'data-jfbm-filter': filter.key,
            onchange: function () {
                const filters = {};
                filters[filter.key] = box.checked;
                changePageState(page, { filters: filters });
            }
        });
        return box;
    });
    page.filtersButton = h('button', {
        type: 'button',
        class: 'jfbm-page-segment jfbm-page-filters-button',
        'aria-expanded': 'false',
        text: 'Filters',
        onclick: function () {
            page.filtersOpen = !page.filtersOpen;
            syncPageToolbar(page);
        }
    });
    // The TV layout's Select toggle: the checkbox column is hidden there until it is on, and the remote's
    // focus order stays short; turning it off drops the selection.
    page.selectButton = page.tv ? h('button', {
        type: 'button',
        class: 'jfbm-page-segment jfbm-page-select-button',
        'aria-pressed': 'false',
        text: 'Select',
        onclick: function () {
            setPageSelecting(page, !page.selecting);
        }
    }) : null;
    page.filtersRow = h('div', { class: 'jfbm-page-filters', role: 'group', 'aria-label': 'Filters' },
        PAGE_FILTERS.map(function (filter, index) {
            return h('label', { class: kit.cls('checkbox') + ' jfbm-page-filter' }, [page.filterBoxes[index], h('span', { text: filter.label })]);
        }));
    page.toolbar = h('div', { class: 'jfbm-page-toolbar' }, [
        page.search,
        h('div', { class: 'jfbm-page-segments', role: 'group', 'aria-label': 'Type' }, page.typeButtons),
        page.sortSelect,
        h('div', { class: 'jfbm-page-segments', role: 'group', 'aria-label': 'View' }, page.viewButtons),
        page.filtersButton,
        page.selectButton
    ]);
}

// The toolbar after a state change: the pressed type and view, the view's sort list with the sort chosen, the filter
// boxes and the Filters button's count (the button only expands and folds the row: aria-expanded, never aria-pressed);
// the search field only when its text differs (never while it is being typed in), through the search box's own setter,
// so the same text typed again is searched again.
function syncPageToolbar(page) {
    const s = page.state;
    page.typeButtons.forEach(function (button) {
        button.setAttribute('aria-pressed', button.getAttribute('data-jfbm-type') === s.type ? 'true' : 'false');
    });
    page.viewButtons.forEach(function (button) {
        button.setAttribute('aria-pressed', button.getAttribute('data-jfbm-view') === s.view ? 'true' : 'false');
    });
    page.sortSelect.replaceChildren.apply(page.sortSelect, pageSorts(s.view).map(function (sort) {
        return h('option', { value: sort[s.view], text: sort.label });
    }));
    page.sortSelect.value = s.sort;
    let active = 0;
    page.filterBoxes.forEach(function (box) {
        box.checked = !!s.filters[box.getAttribute('data-jfbm-filter')];
        active += box.checked ? 1 : 0;
    });
    page.filtersButton.textContent = active > 0 ? 'Filters (' + active + ')' : 'Filters';
    page.filtersButton.setAttribute('aria-expanded', page.filtersOpen ? 'true' : 'false');
    kit.setHidden(page.filtersRow, !page.filtersOpen);
    if (document.activeElement !== page.search && String(page.search.value || '').trim() !== s.q) {
        page.search.setSearch(s.q);
    }
}

// A toolbar change: the state, the hash (pushed, so Back returns to the previous list, or replaced), the toolbar, and
// the list from its first page.
function changePageState(page, changes, opts) {
    opts = opts || {};
    const next = Object.assign({}, page.state, changes, { filters: Object.assign({}, page.state.filters, changes.filters || {}) });
    next.pages = 1;
    next.group = '';
    page.state = next;
    kit.pages.setParams(BOOKMARKS_PAGE.key, writePageState(next), { replace: !!opts.replace });
    syncPageToolbar(page);
    return loadPageContent(page);
}

function changePageView(page, view) {
    if (view !== page.state.view) {
        changePageState(page, { view: view, sort: pageSortAcross(page.state.view, page.state.sort, view) });
    }
}

function clearPageFilters(page) {
    const filters = {};
    PAGE_FILTERS.forEach(function (filter) {
        filters[filter.key] = false;
    });
    // The field is cleared even while it has the focus (the toolbar sync leaves a focused field alone), with the search
    // box's memory, so the term that matched nothing can be typed again.
    page.search.setSearch('');
    changePageState(page, { q: '', type: '', item: '', filters: filters });
}

// The list as the state says: the pages the hash counts (one request of up to 200, then more), with the other view's
// total for the count line ("84 bookmarks on 36 titles"); "Loading…" shows after 300 ms. A load the state
// moved on from (a quicker toolbar change, a render) is dropped.
async function loadPageContent(page) {
    const generation = ++page.generation;
    const s = page.state;
    const view = pageView(s.view);
    page.loading = true;
    page.loaded = 0;
    page.total = null;
    page.otherTotal = null;
    page.shown = new Map();
    page.pending = new Set();
    clearTimeout(page.reloadTimer);
    // The focus in the list it replaces (a row's button): back on a row once it is read (restoreListFocus).
    page.listFocus = page.listFocus || page.content.contains(document.activeElement);
    page.content.replaceChildren();
    page.content.removeAttribute('role');
    page.content.setAttribute('aria-busy', 'true');
    page.count.textContent = '';
    page.pager.update({ hasMore: false, busy: true });
    const loading = kit.notice({ kind: 'info', text: 'Loading…' });
    const loadingTimer = setTimeout(function () {
        if (page.generation === generation) {
            page.content.append(loading);
        }
    }, PAGE_LOADING_DELAY_MS);
    const current = function () {
        return page.generation === generation && page.context.isCurrent();
    };
    try {
        const otherCount = pageOtherCount(page);
        const wanted = s.pages * view.size;
        let total = 0;
        let startIndex = 0;
        while (startIndex < wanted) {
            const limit = Math.min(wanted - startIndex, PAGE_MAX_LIMIT);
            const result = await api('GET', view.endpoint, undefined, pageQuery(s, startIndex, limit));
            if (!current()) {
                return;
            }

            const items = (result && result.Items) || [];
            total = result && typeof result.TotalRecordCount === 'number' ? result.TotalRecordCount : startIndex + items.length;
            appendPageItems(page, items);
            startIndex += items.length;
            if (items.length < limit || startIndex >= total) {
                break;
            }
        }

        page.total = total;
        try {
            const other = await otherCount;
            page.otherTotal = other && typeof other.TotalRecordCount === 'number' ? other.TotalRecordCount : null;
        } catch (e) {
            report(e);
        }

        if (!current()) {
            return;
        }

        finishPageLoad(page);
    } catch (e) {
        if (current()) {
            showPageError(page, e);
        }
    } finally {
        if (page.generation === generation) {
            page.loading = false;
            if (page.listFocus && page.context.isCurrent()) {
                page.listFocus = false;
                restoreListFocus(page, '.jfbm-row', pageRowFocusTarget);
            }

            // A change that came while this load was under way (applyPendingChanges put it back) is applied now.
            if (page.pending.size > 0) {
                clearTimeout(page.reloadTimer);
                page.reloadTimer = setTimeout(function () {
                    applyPendingChanges(page);
                }, PAGE_RELOAD_DELAY_MS);
            }
        }

        clearTimeout(loadingTimer);
        loading.remove();
    }
}

// The other view's total for the count line, asked with limit=1 (its TotalRecordCount is all that is read); a refusal
// is never an unhandled rejection (the caller awaits it after the list).
function pageOtherCount(page) {
    const s = page.state;
    const otherView = pageView(s.view === 'flat' ? 'grouped' : 'flat');
    const request = api('GET', otherView.endpoint, undefined, pageQuery(Object.assign({}, s, { sort: pageSorts(otherView.value)[0][otherView.value] }), 0, 1));
    request.catch(function () {});
    return request;
}

// "Load more": the next page appended, the hash's page count moved on in place (Back still returns to the previous
// filters, not to a shorter list).
async function loadMorePage(page) {
    const generation = page.generation;
    const s = page.state;
    const view = pageView(s.view);
    page.pager.update({ busy: true });
    try {
        const result = await api('GET', view.endpoint, undefined, pageQuery(s, page.loaded, view.size));
        if (page.generation !== generation || !page.context.isCurrent()) {
            return;
        }

        appendPageItems(page, (result && result.Items) || []);
        if (result && typeof result.TotalRecordCount === 'number') {
            page.total = result.TotalRecordCount;
        }

        s.pages = Math.min(PAGE_MAX_PAGES, Math.max(1, Math.ceil(page.loaded / view.size)));
        kit.pages.setParams(BOOKMARKS_PAGE.key, writePageState(s), { replace: true });
        updatePageCount(page);
        page.pager.update({ busy: false, hasMore: page.loaded < page.total });
        kit.upgrade(page.content);
    } catch (e) {
        if (page.generation === generation) {
            page.pager.update({ busy: false });
            toast(e.message);
        }
    }
}

// The items appended: the content is a list (role="list") of groups or rows once it has any; a notice alone isn't one.
function appendPageItems(page, items) {
    const list = pageRowList(page);
    if (items.length > 0) {
        page.content.setAttribute('role', 'list');
    }

    items.forEach(function (item) {
        page.content.append(page.state.view === 'flat' ? renderPageRow(page, item, list) : renderPageGroup(page, item, list));
        rememberShown(page, item);
    });
    page.loaded += items.length;
}

// The bookmarks of a group or a flat row remembered (Select all, the selection's bookmarks; a selected one as just
// read, so the delete dialog counts its clip file as it is now), and running clips watched.
function rememberShown(page, item) {
    (page.state.view === 'flat' ? [item] : item.Bookmarks || []).forEach(function (bookmark) {
        page.shown.set(bookmark.Id, bookmark);
        if (page.selected.has(bookmark.Id)) {
            page.selected.set(bookmark.Id, bookmark);
        }

        if (bookmark.Clip && (bookmark.Clip.Status === 'Queued' || bookmark.Clip.Status === 'Running')) {
            watchClip(bookmark);
        }
    });
}

function finishPageLoad(page) {
    const s = page.state;
    page.content.removeAttribute('aria-busy');
    kit.upgrade(page.content);
    updatePageCount(page);
    page.pager.update({ busy: false, hasMore: page.loaded < page.total });
    syncSelection(page);
    if (page.loaded === 0) {
        showPageEmpty(page);
        return;
    }

    if (s.group) {
        // The group has this one landing (Back, or a deep link): the hash drops it in place, so a reload of the list
        // (a row changed) doesn't scroll to it again, and the entry stays where it is.
        highlightPageGroup(page, s.group);
        s.group = '';
        kit.pages.setParams(BOOKMARKS_PAGE.key, writePageState(s), { replace: true });
    }
}

// The empty states: with filters on, "No bookmarks match these filters." and Clear filters; with none, the
// first sentence with the shortcut in effect on this device.
function showPageEmpty(page) {
    page.content.removeAttribute('role');
    page.content.replaceChildren(pageHasFilters(page.state)
        ? kit.notice({
            text: 'No bookmarks match these filters.',
            action: {
                label: 'Clear filters',
                onClick: function () {
                    clearPageFilters(page);
                }
            }
        })
        : kit.notice({ text: pageEmptyText(page.capabilities) }));
}

// The first empty state, with the shortcut in effect on this device; an admin also reads where Jellyfin Enhanced's
// bookmarks are imported while some wait for it (the capabilities' JellyfinEnhancedImportPending; false for everyone
// else).
function pageEmptyText(capabilities) {
    const combo = bookmarkNowCombo(capabilities);
    const text = combo
        ? 'No bookmarks yet. Press ' + combo + ' while watching, or use the bookmark button in the player.'
        : 'No bookmarks yet. Use the bookmark button in the player while watching.';
    return capabilities && capabilities.IsAdmin && capabilities.JellyfinEnhancedImportPending
        ? text + ' Have bookmarks in Jellyfin Enhanced? Import them in the plugin settings.'
        : text;
}

// "84 bookmarks on 36 titles": the shown view's total and the other view's, each as far as it is known; with the
// details page's video filter, a way back to every video.
function updatePageCount(page) {
    const s = page.state;
    const bookmarks = s.view === 'flat' ? page.total : page.otherTotal;
    const titles = s.view === 'flat' ? page.otherTotal : page.total;
    const parts = [];
    if (typeof bookmarks === 'number') {
        parts.push(plural(bookmarks, 'bookmark'));
    }

    if (typeof titles === 'number') {
        parts.push((parts.length ? 'on ' : '') + plural(titles, 'title'));
    }

    page.count.replaceChildren(h('span', { text: parts.join(' ') }));
    if (s.item) {
        page.count.append(' ', linkButton('Show every video', function () {
            changePageState(page, { item: '' });
        }, { class: kit.cls('link') + ' jfbm-page-all-videos' }));
    }
}

function showPageError(page, error) {
    page.content.removeAttribute('aria-busy');
    page.content.replaceChildren(kit.notice({
        kind: 'error',
        text: error && error.message ? error.message : 'The bookmarks could not be loaded.',
        action: {
            label: 'Retry',
            onClick: function () {
                loadPageContent(page);
            }
        }
    }));
    page.pager.update({ busy: false, hasMore: false });
}

// What 35-lists.js's rows need of a list: the page's capabilities, a play from here, and the
// page's row options: the inline rename, the selection, the point bookmark's scissors and the compact menu.
function pageRowList(page) {
    return {
        mode: 'page',
        itemId: null,
        container: page.content,
        capabilities: page.capabilities,
        inlineRename: true,
        pointClip: true,
        compactMenu: true,
        select: {
            isSelected: function (id) {
                return page.selected.has(id);
            },
            toggle: function (bookmark, checked) {
                if (checked) {
                    page.selected.set(bookmark.Id, bookmark);
                } else {
                    page.selected.delete(bookmark.Id);
                }

                updateBulkBar(page);
            }
        },
        onJump: function (bookmark) {
            playFrom(bookmark).catch(function (e) {
                toast(e.message);
            });
        }
    };
}

// The group opened from the page (Open title, or a row's video): kept in the hash in place, so Back finds it again.
function rememberPageGroup(page, itemId) {
    page.state.group = pageItemKey(itemId);
    kit.pages.setParams(BOOKMARKS_PAGE.key, writePageState(page.state), { replace: true });
}

// A video's poster at a height (120 px for a group here, 90 px for a row of My Clips), asked only for
// a video the viewer can open (ItemAvailable): core serves images unchecked.
function posterUrl(itemId, fillHeight) {
    const client = kit.client();
    return client ? client.getUrl('Items/' + pageItemKey(itemId) + '/Images/Primary', { fillHeight: fillHeight || 120, quality: 90 }) : null;
}

// A group's own badge when the viewer can't open its video: Not in library when every bookmark is detached, else
// Unavailable.
function groupUnavailableReason(group) {
    if (group.ItemAvailable === true) {
        return null;
    }

    const bookmarks = group.Bookmarks || [];
    const detached = bookmarks.length > 0 && bookmarks.every(function (bookmark) {
        return bookmark.Detached === true;
    });
    return unavailableReason({ Detached: detached, ItemAvailable: false });
}

// One video in the grouped view: the poster (only when the viewer can open the video; a placeholder
// otherwise, and when the image fails), the display name, the count and the latest change, Open title, then its
// bookmarks by position; a group cut at 100 offers the flat view of that video.
function renderPageGroup(page, group, list) {
    const available = group.ItemAvailable === true;
    const unavailable = groupUnavailableReason(group);
    const header = kit.listItem({
        id: group.ItemId,
        type: group.ItemType || null,
        title: group.ItemName || '',
        subtitle: plural(group.Count, 'bookmark') + ' · changed ' + kit.format.ago(group.LatestUpdatedAt),
        imageUrl: available ? posterUrl(group.ItemId) : null,
        icon: group.ItemType === 'Episode' ? 'tv' : 'movie',
        href: available ? kit.itemHref(group.ItemId) : false
    }, {
        className: 'jfbm-group-header',
        // The heading of the group's list item, not a list item itself.
        role: false,
        actions: available
            ? [h('a', {
                class: kit.cls('link') + ' jfbm-open-title',
                href: kit.itemHref(group.ItemId),
                text: 'Open title',
                onclick: function () {
                    rememberPageGroup(page, group.ItemId);
                }
            })]
            : [h('span', { class: 'jfbm-badge ' + unavailable.className, text: unavailable.badge, title: unavailable.title })]
    });
    const image = header.querySelector('img');
    if (image) {
        image.addEventListener('error', function () {
            const holder = image.parentNode;
            image.remove();
            if (holder) {
                holder.append(icon('movie'));
            }
        });
    }

    header.querySelectorAll('a').forEach(function (link) {
        link.addEventListener('click', function () {
            rememberPageGroup(page, group.ItemId);
        });
    });
    const rows = (group.Bookmarks || []).map(function (bookmark) {
        return renderRow(bookmark, list);
    });
    if (group.BookmarksTruncated) {
        rows.push(h('div', { class: 'jfbm-page-truncated', role: 'listitem' }, linkButton('Show all ' + plural(group.Count, 'bookmark') + ' of this title', function () {
            changePageState(page, { view: 'flat', item: group.ItemId, sort: 'Position' });
        })));
    }

    // One item of the page's list, holding the header and its own list of rows.
    return h('div', { class: 'jfbm-group', 'data-jfbm-group': pageItemKey(group.ItemId), role: 'listitem' }, [header, h('div', { class: 'jfbm-list jfbm-group-rows', role: 'list' }, rows)]);
}

// One bookmark in the flat view: the row with its video named under the title (a link while the viewer can open it).
function renderPageRow(page, bookmark, list) {
    const available = bookmark.ItemAvailable === true && !bookmark.Detached;
    const row = renderRow(bookmark, list, {
        itemLine: h('div', { class: 'jfbm-row-item' }, available
            ? h('a', {
                class: 'jfbm-row-item-link',
                href: kit.itemHref(bookmark.ItemId),
                text: bookmark.ItemName || '',
                onclick: function () {
                    rememberPageGroup(page, bookmark.ItemId);
                }
            })
            : h('span', { text: bookmark.ItemName || '' }))
    });
    row.setAttribute('data-jfbm-item', pageItemKey(bookmark.ItemId));
    return row;
}

// Back on the page after a video was opened from it: that video's group (or its first row) scrolled into view and
// marked for a moment, when it is among the pages shown. The key is a video id (readPageState), escaped all the same.
function highlightPageGroup(page, itemKey) {
    const key = cssKey(itemKey);
    const target = page.content.querySelector('[data-jfbm-group="' + key + '"], .jfbm-row[data-jfbm-item="' + key + '"]');
    if (!target) {
        return;
    }

    target.classList.add('jfbm-page-highlight');
    if (typeof target.scrollIntoView === 'function') {
        target.scrollIntoView({ block: 'start' });
    }

    // The mark goes from whatever carries it then: a row or group drawn again meanwhile keeps it for the rest of the time.
    setTimeout(function () {
        page.content.querySelectorAll('.jfbm-page-highlight').forEach(function (node) {
            node.classList.remove('jfbm-page-highlight');
        });
    }, PAGE_HIGHLIGHT_MS);
}

// Whether a control can take the focus now: in the page, not disabled, and shown (the browser gives an element under
// display:none, such as the row buttons a phone keeps behind More, no box, and won't focus it).
function pageCanFocus(el) {
    return !!el && el.isConnected === true && !el.disabled && typeof el.focus === 'function' && el.getClientRects().length > 0;
}

// The first of some controls that can take the focus, or null.
function pageFirstFocusable(candidates) {
    for (let i = 0; i < candidates.length; i++) {
        if (pageCanFocus(candidates[i])) {
            return candidates[i];
        }
    }

    return null;
}

// A My Bookmarks row's control for the focus, as keepFocus picks it: its Rename button, else its time button, else its
// first button that can take it.
function pageRowFocusTarget(row) {
    return pageFirstFocusable([row.querySelector('button[title="Rename"]'), row.querySelector('.jfbm-time-button')].concat(Array.prototype.slice.call(row.querySelectorAll('button'))));
}

// A group's: its first row's, else its header's first control.
function pageGroupFocusTarget(group) {
    const rows = group.querySelectorAll('.jfbm-row');
    for (let i = 0; i < rows.length; i++) {
        const target = pageRowFocusTarget(rows[i]);
        if (target) {
            return target;
        }
    }

    return pageFirstFocusable(Array.prototype.slice.call(group.querySelectorAll('a[href], button')));
}

// Where the focus goes on a page when what had it is gone and no row can take it (both pages): Load more, else the
// toolbar's first control that isn't a text field (a TV opens no on-screen keyboard for it), else its search box.
function pageFallbackFocus(page) {
    const toolbar = page.toolbar ? Array.prototype.slice.call(page.toolbar.querySelectorAll('button, select')) : [];
    return pageFirstFocusable([page.pager ? page.pager.element.querySelector('button') : null].concat(toolbar, [page.search || null]));
}

// The focus out of a node about to leave a page's list (a row or a group deleted, or no longer matching the filters),
// when it is inside it: to the next node of its kind's control (targetOf), else the previous one's, else the page's
// fallback, so a keyboard or a remote isn't dropped at the top of the page (it used to fall to the body).
function moveFocusFrom(page, node, selector, targetOf) {
    const active = document.activeElement;
    if (!node || !active || (active !== node && !node.contains(active))) {
        return;
    }

    const all = Array.prototype.slice.call(page.content.querySelectorAll(selector));
    const index = all.indexOf(node);
    const others = (index < 0 ? all : all.slice(index + 1).concat(all.slice(0, index).reverse())).filter(function (other) {
        return !node.contains(other) && !other.contains(node);
    });
    for (let i = 0; i < others.length; i++) {
        const target = targetOf(others[i]);
        if (target) {
            target.focus();
            return;
        }
    }

    const fallback = pageFallbackFocus(page);
    if (fallback) {
        fallback.focus();
    }
}

// After the whole list was read again while the focus was in it (a bulk delete over many videos, Retry): the first row's
// control, else the page's fallback, unless the focus has gone somewhere that is still there meanwhile.
function restoreListFocus(page, rowSelector, targetOf) {
    const active = document.activeElement;
    if (active && active !== document.body && active.isConnected === true) {
        return;
    }

    const rows = page.content.querySelectorAll(rowSelector);
    for (let i = 0; i < rows.length; i++) {
        const target = targetOf(rows[i]);
        if (target) {
            target.focus();
            return;
        }
    }

    const fallback = pageFallbackFocus(page);
    if (fallback) {
        fallback.focus();
    }
}

// The row (or button) to focus in a node drawn again, when the focus was inside what it replaces (or a row of it was
// being renamed, which a disabled Save button may have blurred): the same bookmark's Rename or time button, else the
// first button, so a keyboard or remote user isn't dropped at the top of the page.
function keepFocus(old, fresh) {
    const renaming = old.querySelector('.jfbm-row-renaming');
    if (!old.contains(document.activeElement) && !renaming) {
        return;
    }

    const active = old.contains(document.activeElement) && document.activeElement.closest ? document.activeElement.closest('.jfbm-row') : null;
    const row = active || renaming;
    const id = row ? row.getAttribute('data-id') : null;
    const scope = id ? fresh.querySelector('.jfbm-row[data-id="' + cssKey(id) + '"]') || fresh : fresh;
    const target = scope.querySelector('button[title="Rename"]') || scope.querySelector('.jfbm-time-button') || scope.querySelector('button');
    if (target) {
        target.focus();
    }
}

// One row drawn again in place from the bookmark as just read (a clip's progress tick, a rename), when it is on the
// page: the group's header and the count line don't change with it. Nothing happens for a bookmark the page doesn't
// show. A row whose inline rename is still being typed keeps its node (the typed name, the focus):
// only what the page remembers of the bookmark follows, and the row is drawn again once the rename ends (a save
// draws the video again anyway; a Cancel of a row kept so asks for it, jfbm-row-rename-kept).
function patchPageRow(page, bookmark) {
    if (!bookmark || !page.content) {
        return false;
    }

    const row = page.content.querySelector('.jfbm-row[data-id="' + cssKey(bookmark.Id) + '"]');
    if (!row || !row.parentNode) {
        return false;
    }

    if (isRenameOpen(row)) {
        page.shown.set(bookmark.Id, bookmark);
        if (page.selected.has(bookmark.Id)) {
            page.selected.set(bookmark.Id, bookmark);
        }

        // The row now shows less than the page knows: a Cancel asks for the video again (startInlineRename).
        row.classList.add('jfbm-row-rename-kept');
        return true;
    }

    const list = pageRowList(page);
    const fresh = page.state.view === 'flat' ? renderPageRow(page, bookmark, list) : renderRow(bookmark, list);
    if (row.classList.contains('jfbm-page-highlight')) {
        fresh.classList.add('jfbm-page-highlight');
    }

    row.parentNode.insertBefore(fresh, row);
    keepFocus(row, fresh);
    row.remove();
    kit.upgrade(fresh);
    page.shown.set(bookmark.Id, bookmark);
    if (page.selected.has(bookmark.Id)) {
        page.selected.set(bookmark.Id, bookmark);
    }

    return true;
}

// The changes collected by the listener above, applied: for each video, its group read again with the Groups endpoint
// and the page's filters (gone from the result: the group goes; the flat view: the video's rows read again and drawn
// in place, rows that no longer match removed), then the count line, the pager and the empty state. The whole list is
// read again instead for more videos than PAGE_PATCH_MAX_ITEMS at once, for a video the page doesn't show, when a
// bookmark the page doesn't show now matches (a filter it fell under), or when nothing is shown at all; a change that
// comes while a load is under way waits for its end.
async function applyPendingChanges(page) {
    const pending = page.pending;
    page.pending = new Set();
    if (!page.context.isShown() || pending.size === 0) {
        return;
    }

    const s = page.state;
    const keys = Array.from(pending).filter(function (key) {
        // With the details page's video filter, another video's change is nothing to this list.
        return !s.item || key === s.item;
    });
    if (keys.length === 0) {
        return;
    }

    if (page.loading) {
        // The load under way reads fresh data for what it asked before the change; the rest waits for its end.
        keys.forEach(function (key) {
            page.pending.add(key);
        });
        return;
    }

    const shownKeys = keys.filter(function (key) {
        return !!pageItemNode(page, key);
    });
    if (page.loaded === 0 || keys.length > PAGE_PATCH_MAX_ITEMS || shownKeys.length !== keys.length) {
        loadPageContent(page);
        return;
    }

    const generation = page.generation;
    try {
        for (let i = 0; i < keys.length; i++) {
            const patched = s.view === 'flat' ? await reloadPageRows(page, keys[i], generation) : await reloadPageGroup(page, keys[i], generation);
            if (page.generation !== generation || !page.context.isCurrent()) {
                return;
            }

            if (!patched) {
                loadPageContent(page);
                return;
            }
        }

        await refreshPageCounts(page, generation);
    } catch (e) {
        if (page.generation === generation && page.context.isCurrent()) {
            report(e);
            loadPageContent(page);
        }
    }
}

// The group or flat row(s) of a video on the page, or null.
function pageItemNode(page, key) {
    return page.content.querySelector('.jfbm-group[data-jfbm-group="' + cssKey(key) + '"], .jfbm-row[data-jfbm-item="' + cssKey(key) + '"]');
}

// One video's group read again (GET /Bookmarks/Groups with itemId and the page's filters, limit 1) and drawn in place;
// a group the filters no longer match is removed. The fresh rows render ticked from the selection as it stands (a
// bookmark the server kept in a bulk delete, a sibling of a renamed one), and each row whose inline
// rename is still being typed moves into the fresh group as it is (keepRenamingRows). Answers false when the page
// moved on.
async function reloadPageGroup(page, key, generation) {
    const s = page.state;
    const result = await api('GET', 'Bookmarks/Groups', undefined, Object.assign(pageQuery(s, 0, 1), { itemId: key }));
    if (page.generation !== generation || !page.context.isCurrent()) {
        return false;
    }

    const old = page.content.querySelector('.jfbm-group[data-jfbm-group="' + cssKey(key) + '"]');
    if (!old) {
        return false;
    }

    forgetShown(page, old);
    const group = result && result.Items && result.Items[0];
    if (!group) {
        moveFocusFrom(page, old, '.jfbm-group', pageGroupFocusTarget);
        old.remove();
        page.loaded = Math.max(0, page.loaded - 1);
        return true;
    }

    const fresh = renderPageGroup(page, group, pageRowList(page));
    if (old.classList.contains('jfbm-page-highlight')) {
        fresh.classList.add('jfbm-page-highlight');
    }

    const active = document.activeElement;
    const kept = keepRenamingRows(old, fresh);
    old.parentNode.insertBefore(fresh, old);
    if (active && kept.some(function (row) {
        return row.contains(active);
    }) && typeof active.focus === 'function') {
        // The browser blurs a focused field taken out of the document, even for a moment: the focus back in it now
        // that the fresh group is in place (the field keeps its text and its caret). It stays there: the old group's
        // other rows (a rename that has saved) don't take it.
        active.focus();
    } else {
        keepFocus(old, fresh);
    }

    old.remove();
    kit.upgrade(fresh);
    rememberShown(page, group);
    return true;
}

// Every row of the old group whose inline rename is still being typed (more than one can be open, as in the flat
// view), each moved into the fresh group in place of its fresh row (the same bookmark), so the typed names and the
// renames' own handlers stay; a save still carries the UpdatedAt its row was read with, so a change made to that
// bookmark meanwhile is the 409 the rename handles. A renaming row of a bookmark the fresh group no longer holds goes
// with the old group. Answers the rows moved (once only the first was, and the one being
// typed in, when it wasn't the first, lost its name and the focus).
function keepRenamingRows(old, fresh) {
    const kept = [];
    Array.prototype.slice.call(old.querySelectorAll('.jfbm-row-renaming')).filter(isRenameOpen).forEach(function (renaming) {
        const id = renaming.getAttribute('data-id');
        const target = id ? fresh.querySelector('.jfbm-row[data-id="' + cssKey(id) + '"]') : null;
        if (!target || !target.parentNode) {
            return;
        }

        target.parentNode.insertBefore(renaming, target);
        target.remove();
        // Its bookmark may have changed too (a clip finished): a Cancel asks for the video again (startInlineRename).
        renaming.classList.add('jfbm-row-rename-kept');
        kept.push(renaming);
    });
    return kept;
}

// One video's rows of the flat view read again (GET /Bookmarks/Query with itemId and the page's filters) and drawn in
// place: a shown row that is still there is replaced, one that isn't (deleted, or no longer matching) removed; a
// bookmark in the answer the page didn't show (it now matches a filter) needs the whole list for its place, so false;
// so does an answer cut short (more of the video match than one request's 200).
async function reloadPageRows(page, key, generation) {
    const s = page.state;
    const result = await api('GET', 'Bookmarks/Query', undefined, Object.assign(pageQuery(s, 0, PAGE_MAX_LIMIT), { itemId: key }));
    if (page.generation !== generation || !page.context.isCurrent()) {
        return false;
    }

    const items = (result && result.Items) || [];
    if (result && typeof result.TotalRecordCount === 'number' && result.TotalRecordCount > items.length) {
        // More of this video match than one request answers (over PAGE_MAX_LIMIT): a shown row missing from the
        // answer may still be there, so only the whole list can tell (it used to remove those rows).
        return false;
    }

    const rows = Array.prototype.slice.call(page.content.querySelectorAll('.jfbm-row[data-jfbm-item="' + cssKey(key) + '"]'));
    const shownIds = rows.map(function (row) {
        return row.getAttribute('data-id');
    });
    const fresh = new Map();
    items.forEach(function (bookmark) {
        fresh.set(bookmark.Id, bookmark);
    });
    if (Array.from(fresh.keys()).some(function (id) {
        return shownIds.indexOf(id) < 0;
    })) {
        // Something of this video the page didn't show matches now (a filter it fell under; a bookmark beyond the
        // shown pages); only the whole list knows where it goes.
        return false;
    }

    rows.forEach(function (row) {
        const id = row.getAttribute('data-id');
        const bookmark = fresh.get(id);
        if (bookmark) {
            patchPageRow(page, bookmark);
        } else {
            forgetShown(page, row);
            moveFocusFrom(page, row, '.jfbm-row', pageRowFocusTarget);
            row.remove();
            page.loaded = Math.max(0, page.loaded - 1);
        }
    });
    return true;
}

// The bookmarks of a group or row leaving the page: out of the shown map. The selection is left alone here: a group
// drawn again reads it for its fresh rows (a bookmark the server kept in a bulk delete stays ticked), and
// syncSelection (refreshPageCounts, after every patch) drops what is no longer shown.
function forgetShown(page, node) {
    const rows = node.classList.contains('jfbm-row') ? [node] : node.querySelectorAll('.jfbm-row');
    rows.forEach(function (row) {
        page.shown.delete(row.getAttribute('data-id'));
    });
}

// The count line after a change: both totals asked again (limit 1 each), then the pager, the selection's bar and the
// empty state when nothing is shown any more.
async function refreshPageCounts(page, generation) {
    const s = page.state;
    const view = pageView(s.view);
    const counts = await Promise.all([
        api('GET', view.endpoint, undefined, pageQuery(s, 0, 1)),
        pageOtherCount(page)
    ]);
    if (page.generation !== generation || !page.context.isCurrent()) {
        return;
    }

    page.total = counts[0] && typeof counts[0].TotalRecordCount === 'number' ? counts[0].TotalRecordCount : page.loaded;
    page.otherTotal = counts[1] && typeof counts[1].TotalRecordCount === 'number' ? counts[1].TotalRecordCount : null;
    updatePageCount(page);
    page.pager.update({ hasMore: page.loaded < page.total });
    syncSelection(page);
    if (page.loaded === 0) {
        showPageEmpty(page);
    }
}

// ------------------------------------------------------------------------------------------ bulk select and delete

// The bulk bar under a page's list (since 1.2.0 the My Clips page's too): how many are selected, Select all
// (every item shown that may be selected), Clear and Delete…; hidden while nothing is selected. On a TV it shows only while
// Select is on. options (My Bookmarks' by default): noun (what the count names), label (the bar's), rowSelector (a row of
// the page, holding its .jfbm-row-select-box), keyOf(row) (the row's key in page.shown and page.selected), selectable(item)
// (whether an item shown may be selected; Select all takes only those, and syncSelection drops the others) and onDelete.
function buildBulkBar(page, options) {
    page.bulkOptions = Object.assign({
        noun: 'bookmark',
        label: 'Selected bookmarks',
        rowSelector: '.jfbm-row',
        keyOf: function (row) {
            return row.getAttribute('data-id');
        },
        selectable: function () {
            return true;
        },
        onDelete: function () {
            confirmBulkDelete(page);
        }
    }, options || {});
    page.bulkCount = h('span', { class: 'jfbm-bulk-count', role: 'status' });
    page.bulkDelete = kit.button('Delete…', {
        variant: 'primary',
        className: 'jfbm-bulk-delete',
        onClick: function () {
            page.bulkOptions.onDelete();
        }
    });
    page.bulkBar = h('div', { class: 'jfbm-bulk-bar', role: 'toolbar', 'aria-label': page.bulkOptions.label }, [
        page.bulkCount,
        kit.button('Select all', {
            className: 'jfbm-bulk-all',
            onClick: function () {
                page.shown.forEach(function (item, id) {
                    if (page.bulkOptions.selectable(item)) {
                        page.selected.set(id, item);
                    }
                });
                syncSelection(page);
            }
        }),
        kit.button('Clear', {
            className: 'jfbm-bulk-clear',
            onClick: function () {
                page.selected.clear();
                syncSelection(page);
            }
        }),
        page.bulkDelete
    ]);
    updateBulkBar(page);
}

function updateBulkBar(page) {
    const n = page.selected.size;
    page.bulkCount.textContent = n > 0 ? plural(n, page.bulkOptions.noun) + ' selected' : '';
    kit.setHidden(page.bulkBar, n === 0);
}

// The checkboxes and the bar after a load, a patch or a Select all: a selected item the page no longer shows, or that may
// not be selected any more (a clip whose folder went offline), is dropped, and every shown box follows the selection.
function syncSelection(page) {
    const options = page.bulkOptions;
    Array.from(page.selected.keys()).forEach(function (id) {
        if (!page.shown.has(id) || !options.selectable(page.shown.get(id))) {
            page.selected.delete(id);
        }
    });
    page.content.querySelectorAll('.jfbm-row-select-box').forEach(function (box) {
        const row = box.closest(options.rowSelector);
        box.checked = !!row && page.selected.has(options.keyOf(row));
    });
    updateBulkBar(page);
}

// The TV layout's Select toggle: the checkbox column shown while it is on; off, the selection goes with it.
function setPageSelecting(page, on) {
    page.selecting = !!on;
    page.body.classList.toggle('jfbm-page-selecting', page.selecting);
    if (page.selectButton) {
        page.selectButton.setAttribute('aria-pressed', page.selecting ? 'true' : 'false');
    }

    if (!page.selecting) {
        page.selected.clear();
        syncSelection(page);
    }
}

// "Delete n bookmarks?" (as the single delete has it): "Also delete their clips" when any of them
// has a clip file, ticked in 1.1.0 with the next-update hint when unticked (clipDeleteChoice); without a clip file the
// delete sends the same default. POST /Bookmarks/BulkDelete, in batches of 200 (the server refuses more in one
// request; Select all over several pages can hold up to 1000), one after the other with the progress under the
// description, their answers merged; a bookmark whose clip file is in use (InUse), or couldn't be deleted (Failed:
// its folder isn't available or isn't a clip folder) stays, selected, and the dialog says which, so the rest can be
// deleted again later; a batch refused outright (a 503, the network) keeps what it and the ones after it hold, with
// the server's sentence. The dialog closed while the batches go (Cancel, Escape, Back, the cross) stops the run after
// the request under way, whose answer can't be called back, and the outcome comes as a toast (what was deleted, what
// was kept and why, that it stopped, and that what stays is still selected; an early build let the
// later batches go on, clip files included, and put the outcome into the closed dialog). The lists and the page follow
// through notifyChanged, one per deleted bookmark (the page reads the whole list again for many videos).
async function confirmBulkDelete(page) {
    const capabilities = page.capabilities || await getCapabilities();
    let bookmarks = Array.from(page.selected.values());
    if (bookmarks.length === 0) {
        return;
    }

    // The focus starts on Cancel (deleting can't be undone: the least destructive control), and
    // comes back to the bar's Delete…, or, once the bar is hidden (nothing selected any more), to the first row the delete
    // kept, else the page's fallback.
    let cancel = null;
    const deleted = new Set();
    const returnTarget = function () {
        const rows = Array.prototype.slice.call(page.content.querySelectorAll('.jfbm-row')).filter(function (row) {
            return !deleted.has(pageItemKey(row.getAttribute('data-id')));
        });
        return pageFirstFocusable(rows.map(pageRowFocusTarget)) || pageFallbackFocus(page);
    };
    const dialog = kit.dialog({
        title: 'Delete ' + plural(bookmarks.length, 'bookmark') + '?',
        modal: true,
        scopedKeys: true,
        initialFocus: function () {
            return cancel;
        },
        returnFocus: returnTarget
    });
    let closed = false;
    dialog.onClose(function () {
        closed = true;
    });
    const withClips = bookmarks.filter(function (bookmark) {
        return !!(bookmark.Clip && bookmark.Clip.FileName);
    });
    const choice = withClips.length > 0 ? clipDeleteChoice(capabilities, 'Also delete their clips (' + plural(withClips.length, 'clip file') + ')') : null;
    const alsoDeleteClips = function () {
        return choice ? choice.checked() : !offeredPages(capabilities).clips;
    };
    const describe = function () {
        const titles = new Set(bookmarks.map(function (bookmark) {
            return pageItemKey(bookmark.ItemId);
        })).size;
        return bookmarks.length === 1
            ? '"' + displayTitle(bookmarks[0]) + '" will be deleted.'
            : plural(bookmarks.length, 'bookmark') + ' on ' + plural(titles, 'title') + ' will be deleted.';
    };
    const description = h('p', { class: 'jfbm-bulk-description', text: describe() });
    const progress = h('p', { class: 'jfbm-bulk-progress ' + kit.cls('hint'), role: 'status' });
    const error = h('div', { class: kit.cls('error'), role: 'alert' });
    const deleteButton = kit.button('Delete', {
        variant: 'primary',
        onClick: async function () {
            deleteButton.disabled = true;
            error.textContent = '';
            const result = await bulkDeleteInBatches(bookmarks, alsoDeleteClips(), progress, function () {
                return closed;
            });
            const gone = new Set([].concat(result.Deleted, result.Tombstoned, result.NotFound).map(pageItemKey));
            const inUse = new Set(result.InUse.map(pageItemKey));
            const failed = new Set(result.Failed.map(pageItemKey));
            const kept = bookmarks.filter(function (bookmark) {
                return !gone.has(pageItemKey(bookmark.Id));
            });
            const removed = bookmarks.filter(function (bookmark) {
                return gone.has(pageItemKey(bookmark.Id));
            });
            removed.forEach(function (bookmark) {
                deleted.add(pageItemKey(bookmark.Id));
                page.selected.delete(bookmark.Id);
                notifyChanged(bookmark.ItemId);
            });
            updateBulkBar(page);
            if (closed && !pageCanFocus(document.activeElement)) {
                // Closed while the request went: the bar's Delete… it went back to is hidden now.
                const back = returnTarget();
                if (back) {
                    back.focus();
                }
            }

            if (kept.length === 0 && !result.error) {
                dialog.close();
                toast(removed.length === 1 ? 'Bookmark deleted.' : plural(removed.length, 'bookmark') + ' deleted.');
                return;
            }

            // Some stay (a clip file in use or one that couldn't be removed; a batch refused; the run stopped):
            // the dialog goes on with them, so the user can try again or keep them; closed meanwhile, a toast says so.
            bookmarks = kept;
            const parts = [];
            if (removed.length > 0) {
                parts.push(plural(removed.length, 'bookmark') + ' deleted.');
            }

            const count = function (ids) {
                return kept.filter(function (bookmark) {
                    return ids.has(pageItemKey(bookmark.Id));
                }).length;
            };
            const couldNot = function (n, why) {
                return (n === 1 ? 'One could not be deleted' : n + ' could not be deleted') + (why ? ': ' + why : '.');
            };
            const inUseCount = count(inUse);
            const failedCount = count(failed);
            if (inUseCount > 0) {
                parts.push(couldNot(inUseCount, CLIP_IN_USE_BULK));
            }

            if (failedCount > 0) {
                parts.push(couldNot(failedCount, CLIP_FAILED_BULK));
            }

            const unanswered = kept.length - inUseCount - failedCount;
            if (result.error) {
                parts.push((unanswered > 0 ? couldNot(unanswered, '') + ' ' : '') + result.error.message);
            } else if (result.stopped && unanswered > 0) {
                parts.push('Stopped: ' + plural(unanswered, 'bookmark') + (unanswered === 1 ? ' was' : ' were') + ' not deleted.');
            } else if (unanswered > 0) {
                parts.push(couldNot(unanswered, ''));
            }

            if (closed) {
                // The dialog is gone (closed while the request went): the outcome as a toast, long enough to be read.
                parts.push(kept.length === 1 ? 'It is still selected.' : 'They are still selected.');
                toast(parts.join(' '), { timeout: BULK_OUTCOME_TOAST_MS });
                return;
            }

            error.textContent = parts.join(' ');
            description.textContent = describe();
            deleteButton.disabled = false;
            if (!dialog.panel.contains(document.activeElement)) {
                // The disabled button lost the focus: back on it for another try.
                deleteButton.focus();
            }
        }
    });
    cancel = kit.button('Cancel', { onClick: dialog.close });
    kit.append(dialog.body,
        description,
        choice ? choice.node : null,
        progress,
        error,
        h('div', { class: kit.cls('actions') }, [
            cancel,
            deleteButton
        ]));
    return dialog;
}

// The bulk delete's requests, BULK_DELETE_BATCH ids at a time, one after the other (the server's writes are one at a
// time anyway), with "Deleting 1–200 of 405…" in the dialog's progress line while there is more than one; the answers
// merged into one { Deleted, Tombstoned, NotFound, InUse, Failed }. A refused batch ends the run there: `error` holds
// the refusal, and the bookmarks of that batch and the ones after it are in none of the lists (they stay selected).
// `stop()` is asked before each request: true (the dialog closed) ends the run there with `stopped`, the bookmarks of
// the batches not sent in none of the lists either.
function bulkDeleteInBatches(bookmarks, deleteClips, progress, stop) {
    return deleteInBatches(bookmarks, function (batch) {
        return api('POST', 'Bookmarks/BulkDelete', {
            Ids: batch.map(function (bookmark) {
                return bookmark.Id;
            }),
            DeleteClips: deleteClips
        });
    }, ['Deleted', 'Tombstoned', 'NotFound', 'InUse', 'Failed'], progress, stop);
}

// The batches themselves, for any bulk delete (the bookmarks' above; since 1.2.0 the clips' of My Clips, whose
// POST /Bookmarks/Clips/BulkDelete takes 200 too): send(batch) answers one batch's result, whose `lists` (each a list of
// ids) are merged; `error` and `stopped` as above.
async function deleteInBatches(items, send, lists, progress, stop) {
    const merged = { error: null, stopped: false };
    lists.forEach(function (list) {
        merged[list] = [];
    });
    const total = items.length;
    for (let start = 0; start < total; start += BULK_DELETE_BATCH) {
        if (stop && stop()) {
            merged.stopped = true;
            break;
        }

        const batch = items.slice(start, start + BULK_DELETE_BATCH);
        progress.textContent = total > BULK_DELETE_BATCH ? 'Deleting ' + (start + 1) + '–' + (start + batch.length) + ' of ' + total + '…' : '';
        let result;
        try {
            result = await send(batch);
        } catch (e) {
            merged.error = e;
            break;
        }

        lists.forEach(function (list) {
            merged[list] = merged[list].concat((result && result[list]) || []);
        });
    }

    progress.textContent = '';
    return merged;
}

// ------------------------------------------------------------------------------------------ Player options

// The Player options dialog (a header link): the user's own AskForName (Use the server's
// default, Always, Never) and PauseWhileNaming, read fresh from GET /Bookmarks/Preferences when the dialog opens and
// saved together with POST /Bookmarks/Preferences; and the bookmark shortcut on this device (localStorage
// jfbm:shortcut:bookmarkNow, as the player reads it: a combination, '' for off, absent for the server's default),
// checked as it is typed with the kit's shortcuts module against Capabilities.ReservedShortcuts: text that isn't a
// combination or a key jellyfin-web's player or Jellyfin Enhanced takes is refused inline and Save is disabled until it
// is changed; Reset to default gives the server's key back. After a save the capabilities are read again, so the
// player's binding, the OSD tooltip and the page's empty-state sentence follow at once.
async function openPlayerOptions(page) {
    const capabilities = page.capabilities || await getCapabilities();
    const dialog = kit.dialog({ title: 'Player options', modal: true, scopedKeys: true });
    const loading = h('p', { class: 'jfbm-options-loading', text: 'Loading…', role: 'status' });
    dialog.body.append(loading);
    let preferences;
    try {
        preferences = await api('GET', 'Bookmarks/Preferences');
    } catch (e) {
        loading.textContent = e.message;
        loading.classList.add(kit.cls('error'));
        return dialog;
    }

    loading.remove();
    preferences = preferences || {};
    const shortcuts = capabilities.Shortcuts || {};
    const serverCombo = kit.shortcuts.normalize(typeof shortcuts.BookmarkNow === 'string' ? shortcuts.BookmarkNow : BOOKMARK_NOW_DEFAULT) || '';
    // The server's default, named in the third choice whatever the user chose: AskForNameDefault since 1.1.0;
    // a server without it (an older build) only tells it while the user has no choice of their own (AskForName is the
    // default then), else the choice is plain.
    const serverDefault = typeof capabilities.AskForNameDefault === 'boolean'
        ? capabilities.AskForNameDefault
        : (capabilities.AskForNameIsDefault === true ? capabilities.AskForName === true : null);
    const askSelect = kit.select({ class: 'jfbm-options-ask', 'aria-label': 'Ask for a name when bookmarking' }, [
        { value: 'default', text: 'Use the server\'s default' + (serverDefault === null ? '' : (serverDefault ? ' (ask)' : ' (don\'t ask)')) },
        { value: 'always', text: 'Always: the name dialog opens at once' },
        { value: 'never', text: 'Never: the toast offers "Name it"' }
    ]);
    askSelect.value = preferences.AskForName === true ? 'always' : preferences.AskForName === false ? 'never' : 'default';
    const pauseBox = h('input', { type: 'checkbox', class: 'jfbm-options-pause', checked: preferences.PauseWhileNaming === true });
    const override = kit.storage.get(BOOKMARK_NOW_OVERRIDE, null);
    const shortcutInput = kit.input({
        class: 'jfbm-options-shortcut',
        value: typeof override === 'string' && override !== '' ? override : '',
        placeholder: serverCombo ? 'Server default: ' + serverCombo : 'Off on the server',
        autocomplete: 'off',
        spellcheck: 'false'
    });
    const offBox = h('input', { type: 'checkbox', class: 'jfbm-options-off', checked: override === '' });
    const shortcutError = h('div', { class: kit.cls('error') + ' jfbm-options-shortcut-error', role: 'alert' });
    const error = h('div', { class: kit.cls('error'), role: 'alert' });
    const saveButton = kit.button('Save', { variant: 'primary', type: 'submit' });
    const resetLink = linkButton('Reset to default', function () {
        shortcutInput.value = '';
        offBox.checked = false;
        validate();
        shortcutInput.focus();
    }, { class: kit.cls('link') + ' jfbm-options-reset' });

    // The device key as typed: '' (the server's default), a combination, or null for text that isn't one; reserved
    // keys are refused (the list is the caller's own).
    const validate = function () {
        shortcutInput.disabled = offBox.checked;
        if (offBox.checked) {
            shortcutError.textContent = '';
            saveButton.disabled = false;
            return '';
        }

        const text = shortcutInput.value.trim();
        const combo = kit.shortcuts.normalize(text);
        if (combo === null) {
            shortcutError.textContent = '"' + text + '" isn\'t a key combination; use a key with optional modifiers, such as Shift+B.';
            saveButton.disabled = true;
            return null;
        }

        if (combo && kit.shortcuts.reserved(combo, capabilities.ReservedShortcuts).length > 0) {
            shortcutError.textContent = combo + ' is taken by the video player or Jellyfin Enhanced, so the bookmark shortcut wouldn\'t work with it. Choose another key.';
            saveButton.disabled = true;
            return null;
        }

        shortcutError.textContent = '';
        saveButton.disabled = false;
        return combo;
    };
    shortcutInput.addEventListener('input', validate);
    offBox.addEventListener('change', validate);
    validate();

    const save = async function () {
        const combo = validate();
        if (combo === null) {
            shortcutInput.focus();
            return;
        }

        saveButton.disabled = true;
        error.textContent = '';
        const ask = askSelect.value === 'always' ? true : askSelect.value === 'never' ? false : null;
        try {
            await api('POST', 'Bookmarks/Preferences', { AskForName: ask, PauseWhileNaming: pauseBox.checked });
        } catch (e) {
            error.textContent = e.message;
            saveButton.disabled = false;
            return;
        }

        // The device's key: off, the server's default (no override kept, so an admin's change follows), or its own.
        if (offBox.checked) {
            kit.storage.set(BOOKMARK_NOW_OVERRIDE, '');
        } else if (!combo || combo === serverCombo) {
            kit.storage.remove(BOOKMARK_NOW_OVERRIDE);
        } else {
            kit.storage.set(BOOKMARK_NOW_OVERRIDE, combo);
        }

        // The capabilities read again (the server resolves AskForName), then everything that shows them follows.
        state.capabilitiesAt = 0;
        const fresh = await getCapabilities();
        page.capabilities = fresh;
        syncBookmarkNowShortcut(fresh);
        if (page.loaded === 0 && page.content.querySelector('.' + kit.cls('notice')) && !pageHasFilters(page.state)) {
            showPageEmpty(page);
        }

        toast('Player options saved.');
        dialog.close();
    };

    const form = h('form', {
        class: 'jfbm-form jfbm-options',
        onsubmit: function (event) {
            event.preventDefault();
            save();
        }
    }, [
        kit.field('Ask for a name when bookmarking', askSelect),
        h('label', { class: kit.cls('checkbox') + ' jfbm-options-pause-label' }, [pauseBox, h('span', { text: 'Pause while naming' })]),
        kit.field('Bookmark shortcut on this device', shortcutInput, resetLink),
        h('div', { class: kit.cls('hint') + ' jfbm-options-shortcut-hint', text: 'A key with optional modifiers, such as Shift+B. Empty uses the server\'s key' + (serverCombo ? ' (' + serverCombo + ')' : '') + '.' }),
        shortcutError,
        h('label', { class: kit.cls('checkbox') + ' jfbm-options-off-label' }, [offBox, h('span', { text: 'Turn the shortcut off on this device' })]),
        error,
        h('div', { class: kit.cls('actions') }, [
            kit.button('Cancel', { onClick: dialog.close }),
            saveButton
        ])
    ]);
    dialog.body.append(form);
    kit.upgrade(form);
    askSelect.focus();
    return dialog;
}
