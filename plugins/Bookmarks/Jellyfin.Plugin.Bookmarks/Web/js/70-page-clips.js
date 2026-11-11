// The My Clips page: every clip of the signed-in user's across all videos, the
// clips kept when a bookmark was deleted included, in the kit's page host (the kit's own route #/jfbm-clips linked
// from "Library tools" after My Bookmarks, Plugin Pages' host in Auto/PluginPagesOnly). A header with the My Bookmarks
// link, the notices (read-only data, clips turned off), a toolbar (search, State, Mode, Type, Sort and "Include clips of
// deleted bookmarks"), the summary line ("14 clips · 1.6 GB · 1 in progress", GET /Bookmarks/Clips/Summary, with
// how many match while filters are on), the list (GET /Bookmarks/Clips, 50 a page, the clips being made first whatever
// the sort) and "Load more" (the kit's pager), with the state in the hash: q,
// state, mode, type, sort, deleted (0 leaves deleted bookmarks' clips out), page (how many pages are shown) and highlight
// (one clip to scroll to, mark and focus: the clip-ready toast's and My Bookmarks' "View in My Clips"; on a later page the
// list is read on to it, at most PAGE_MAX_PAGES pages, and one the list doesn't hold is said above it), written in place
// through kit.pages.setParams as My Bookmarks does (a toolbar change pushes an entry, a search typed or a page loaded
// replaces it, a highlight that has landed is dropped in place: it has one landing), and read back on Back, Forward, a
// reload and a deep link; a value the hash says wrongly is the default, and the hash is left as it came.
//
// Each row is the kit's list item, so it carries the clip's video as data-id and Jellyfin Enhanced's Hidden Content hides
// the clips of a video the user hid, as on My Bookmarks, with a 16:9 box of our own in place of the kit's image
// box (the video's poster only while the viewer can open it, ItemAvailable, since core serves images unchecked; a
// placeholder otherwise and when the image fails), the clip's title (its bookmark's name, else its note's first line,
// else the clip file's name without its extension), its state (Ready, Queued, being made with its progress, Failed,
// Cancelled, File missing, Clip folder offline) with its mode and size, the video (a link while the viewer can open it),
// the clip's range, its length and when it was made, the badges (Bookmark deleted, Range changed, Name changed, Last
// remake failed, In library "…") and a failed clip's reason.
//
// The row actions, as buttons and, on a narrow screen, one More button opening the same list as our own
// menu (35-lists.js's openActionMenu): Preview (our dialog's <video>, an MKV tried too, with a sentence and
// Download when the browser can't play it, 30-editor.js), Download (the file route with the token, as 1.0's links), Play the
// video at the clip's start (while the viewer can open it), Edit bookmark (a live bookmark's), Make the clip again (a live
// bookmark's range, the mode chosen in a small dialog; marked when its range changed or its file is missing), Cancel (a
// clip being made), Delete (a file that is there; the server's 409 "in use" said in the dialog, the row as it was), Forget (a
// file gone: DELETE …/Clip?onlyIfMissing=true, which never deletes a file) and Dismiss (a failed clip without a file). A
// clip whose folder is offline, or is there but not recognised as the clips' own (its marker file; the server reads both as
// FolderUnavailable), offers only Play and Edit, and no checkbox: nothing that deletes, forgets or makes its file again. A
// checkbox per row and the bulk bar under the list (60-page-bookmarks.js's, with Select all, Clear and Delete…; on a TV
// behind the toolbar's Select toggle) delete through POST /Bookmarks/Clips/BulkDelete, 200 a request with the progress
// (deleteInBatches), the ones the server keeps (in use, being made, their folder offline, refused) staying selected with why;
// closing the dialog stops after the request under way, the outcome then a toast. A row changed by an action is read again
// with its video's other clips under the page's filters (or taken out), the summary after it; a change made elsewhere (the
// editor, another page) reaches the page through notifyChanged the same way. Read-only data leaves Preview, Download, Play
// and Edit. The focus: a row's dialog takes it (Cancel first in the ones that delete or
// forget), gives it back to the row's action as the row is drawn now, and a row that goes passes it to the next row's first
// action (else the previous row's, else Load more or the toolbar).
//
// Since 1.2.0, Rename (a clip that isn't being made and whose folder is there): a dialog with the clip's name and, for a
// file that is there, "Also rename the file" (ticked, unless the clip is in a library: then unticked with what renaming it
// does to the library's video), through POST /Bookmarks/Clips/{id}/Name, both or nothing (a file in use leaves the name as
// it was); and Rename file for a clip whose file still has its old name ("Name changed"), through POST
// /Bookmarks/Clips/{id}/RenameFile, with a confirmation. The rename dialog starts on its name field (typing is what it is
// for, and nothing it does destroys anything: a file renamed can be renamed back), as the player's name dialog and My
// Bookmarks' inline rename do; Rename file's confirmation, which changes a file and has nothing to type, starts on Cancel,
// as every dialog that changes files does.
//
// Also: Rename file sends the clip's UpdatedAt, so a rename in another tab is a 409 and the confirmation then
// names the clip's name as it is now, as the rename dialog does after a 409 "changed elsewhere" (the other tab's name in
// the field, said, before a second Rename can replace it); Rename file isn't marked for a clip in a library (renaming its
// file there costs what the warning says); and a clip whose last rename isn't finished ("Rename not finished": the server
// stopped between the file's move and its record's write, or couldn't write either) offers only Play and Edit, and no
// checkbox, until the server finishes it.
//
// While a shown clip is being made, one GET /Bookmarks/Clips/Status for all of them (200 ids a request) every 2.5 s,
// paused while the tab is hidden and asked at once when it shows again, patches each one's state in
// place (no list read for a tick); a clip whose job has ended is read again (GET /Bookmarks/Clips with the page's filters
// and its video's id) and drawn in its place, then the summary; a clip the answer leaves out (no clip of the user's any
// more) goes from the list. Nothing is polled once no shown clip is being made, or once the page isn't shown (left, or
// kept hidden in Plugin Pages' host view: the kit's context.isShown()). "Load more" asks from a little earlier after such
// changes (a clip whose job ended leaves the top of the server's order; one that ends while a Load more is under way counts
// for the next) and skips the rows the page shows already. Every text is a text node (the kit's h), and the page asks only
// Bookmarks' endpoints, which list the caller's own clips and ask the library as the caller.

// The page as the server's KitPage declares it (BookmarksKit; the key, title, icon and Id suffix must match). Linked after
// My Bookmarks in "Library tools".
const CLIPS_PAGE = Object.freeze({ key: 'clips', title: 'My Clips', icon: 'video_library', idSuffix: 'ClipsPage', order: 20 });

const CLIPS_PER_PAGE = 50;
const CLIPS_STATUS_POLL_MS = 2500;
// The most ids one GET /Bookmarks/Clips/Status takes.
const CLIPS_STATUS_BATCH = 200;
// The poster's height in a row (fillHeight=90).
const CLIPS_POSTER_HEIGHT = 90;

// The toolbar's choices, each with the API value it sends; the first of each is its default.
const CLIPS_STATES = Object.freeze([
    Object.freeze({ value: '', label: 'All states' }),
    Object.freeze({ value: 'Ready', label: 'Ready' }),
    Object.freeze({ value: 'InProgress', label: 'In progress' }),
    Object.freeze({ value: 'Failed', label: 'Failed' }),
    Object.freeze({ value: 'Missing', label: 'File missing' }),
    Object.freeze({ value: 'FolderUnavailable', label: 'Clip folder offline or not recognised' }),
    Object.freeze({ value: 'RenameUnfinished', label: 'Rename not finished' })
]);
const CLIPS_MODES = Object.freeze([
    Object.freeze({ value: '', label: 'Any mode' }),
    Object.freeze({ value: 'Accurate', label: 'Accurate (MP4)' }),
    Object.freeze({ value: 'Fast', label: 'Fast (MKV)' })
]);
const CLIPS_TYPES = Object.freeze([
    Object.freeze({ value: '', label: 'All types' }),
    Object.freeze({ value: 'Movie', label: 'Movies' }),
    Object.freeze({ value: 'Episode', label: 'Episodes' }),
    Object.freeze({ value: 'Other', label: 'Other' })
]);
const CLIPS_SORTS = Object.freeze([
    Object.freeze({ value: 'Newest', label: 'Newest' }),
    Object.freeze({ value: 'Oldest', label: 'Oldest' }),
    Object.freeze({ value: 'Name', label: 'Name A–Z' }),
    Object.freeze({ value: 'ItemName', label: 'Title A–Z' }),
    Object.freeze({ value: 'Size', label: 'Largest' }),
    Object.freeze({ value: 'Duration', label: 'Longest' })
]);

// The choice a hash value names (any case), else the list's first (its default).
function clipsChoice(list, value) {
    const wanted = String(value || '').toLowerCase();
    return list.filter(function (choice) {
        return !!choice.value && choice.value.toLowerCase() === wanted;
    })[0] || list[0];
}

// The page's state as the hash carries it; anything the hash doesn't say, or says wrongly, is the default.
function readClipsState(params) {
    const p = params || {};
    const pages = parseInt(p.page, 10);
    return {
        q: Array.from(String(p.q || '').trim()).slice(0, PAGE_SEARCH_MAX_LENGTH).join(''),
        state: clipsChoice(CLIPS_STATES, p.state).value,
        mode: clipsChoice(CLIPS_MODES, p.mode).value,
        type: clipsChoice(CLIPS_TYPES, p.type).value,
        sort: clipsChoice(CLIPS_SORTS, p.sort).value,
        deleted: !(p.deleted === '0' || p.deleted === 'false'),
        pages: Number.isFinite(pages) ? Math.max(1, Math.min(PAGE_MAX_PAGES, pages)) : 1,
        highlight: pageIdParam(p.highlight),
        // The admins' "All users" scope and its owner (75-page-clips-admin.js).
        scope: String(p.scope || '').toLowerCase() === 'all' ? 'all' : '',
        user: pageIdParam(p.user)
    };
}

// The hash parameters of a state: only what differs from the defaults, so the plain route stays plain.
function writeClipsState(s) {
    const out = {};
    if (s.q) {
        out.q = s.q;
    }

    ['state', 'mode', 'type'].forEach(function (name) {
        if (s[name]) {
            out[name] = s[name];
        }
    });
    if (s.sort !== CLIPS_SORTS[0].value) {
        out.sort = s.sort;
    }

    if (!s.deleted) {
        out.deleted = '0';
    }

    if (s.pages > 1) {
        out.page = String(s.pages);
    }

    if (s.highlight) {
        out.highlight = s.highlight;
    }

    if (s.scope) {
        out.scope = s.scope;
        if (s.user) {
            out.user = s.user;
        }
    }

    return out;
}

// The query of GET /Bookmarks/Clips for a state and a page, and in the admins' scope of GET /Bookmarks/Admin/Clips
// with its owner.
function clipsQuery(s, startIndex, limit, admin) {
    const query = { sortBy: s.sort, startIndex: startIndex, limit: limit };
    if (admin && s.user) {
        query.userId = s.user;
    }

    if (s.q) {
        query.searchTerm = s.q;
    }

    if (s.state) {
        query.state = s.state;
    }

    if (s.mode) {
        query.mode = s.mode;
    }

    if (s.type) {
        query.itemTypes = s.type;
    }

    if (!s.deleted) {
        query.includeDeleted = 'false';
    }

    return query;
}

function clipsHasFilters(s) {
    return !!(s.q || s.state || s.mode || s.type || !s.deleted || (s.scope === 'all' && s.user));
}

// The page shown now, for the visibility listener below; a render makes a new one.
let currentClipsPage = null;

// The tab hidden: the poll pauses; shown again: it asks at once, as the rows may be seconds old.
document.addEventListener('visibilitychange', function () {
    const page = currentClipsPage;
    if (!page) {
        return;
    }

    if (clipsTabHidden()) {
        stopClipsPolling(page);
    } else if (page.context.isShown() && !page.poll.busy && clipsInProgress(page).length > 0) {
        stopClipsPolling(page);
        pollClips(page);
    }
});

function clipsTabHidden() {
    return document.visibilityState === 'hidden';
}

// kit.pages.register's render: the header, the notices, the toolbar, the summary line, the list and the pager, then the
// first load. On the TV layout the body carries jfbm-page-tv, as My Bookmarks' does (a narrower search box).
function renderClipsPage(body, params, context) {
    const page = {
        body: body,
        context: context,
        state: readClipsState(params),
        generation: 0,
        loading: false,
        // The clips shown (key → clip as last read), in the list's order.
        shown: new Map(),
        // The server's index of the next clip "Load more" asks for, and how many of the shown clips have moved in or left
        // the server's order since (a clip whose job ended, a clip gone): "Load more" asks from that much earlier.
        nextIndex: 0,
        overlap: 0,
        total: null,
        summary: null,
        capabilities: null,
        // The notice of a highlight the list doesn't hold, above the list until the next load.
        missing: null,
        poll: { timer: null, busy: false },
        // The clips selected for the bulk delete (key → clip as last read), the TV layout's Select toggle, and the rows to
        // read again after changes made elsewhere (notifyChanged), collected for a moment.
        selected: new Map(),
        selecting: false,
        pendingKeys: new Set(),
        changeTimer: null,
        notifying: false,
        tv: kit.layout.isTv()
    };
    stopClipsPolling(currentClipsPage);
    currentClipsPage = page;
    return getCapabilities().then(function (capabilities) {
        if (!context.isCurrent()) {
            return null;
        }

        page.capabilities = capabilities;
        if (!capabilities || capabilities.CanManageAllClips !== true) {
            // The "All users" scope only for an admin the server allows it: anyone else's hash naming it shows their
            // own clips, and the hash is left as it came.
            page.state.scope = '';
            page.state.user = '';
        } else if (page.state.scope !== 'all') {
            page.state.user = '';
        }

        body.classList.add('jfbm-clips-page');
        body.classList.toggle('jfbm-clips-all-users', clipsAdminScope(page));
        body.classList.toggle('jfbm-page-tv', page.tv);
        buildClipsHeader(page);
        buildClipsToolbar(page);
        // The admins' scope switch and Owner select, in a line of their own above the toolbar (75-page-clips-admin.js).
        page.scopeBar = buildClipsScope(page);
        page.summaryLine = h('p', { class: 'jfbm-clips-summary', role: 'status' });
        page.content = h('div', { class: 'jfbm-clips-content' });
        page.pager = kit.pager({
            onMore: function () {
                loadMoreClips(page);
            }
        });
        buildBulkBar(page, {
            noun: 'clip',
            label: 'Selected clips',
            rowSelector: '.jfbm-clips-row',
            keyOf: function (row) {
                return row.getAttribute('data-jfbm-clip');
            },
            selectable: function (clip) {
                return clipSelectable(page, clip);
            },
            onDelete: function () {
                confirmClipsBulkDelete(page);
            }
        });
        kit.append(body, page.header, page.notices, page.scopeBar, page.toolbar, page.summaryLine, page.content, page.pager.element, page.bulkBar);
        syncClipsToolbar(page);
        return loadClipsContent(page);
    });
}

// The header's link to My Bookmarks (while the server offers that page), and the notices above the toolbar:
// read-only data (the list still reads; changes would be refused; worded as My Bookmarks does,
// readOnlyNoticeText) and clips turned off (existing clips are still listed and downloadable).
function buildClipsHeader(page) {
    const capabilities = page.capabilities || {};
    const links = [];
    if (offeredPages(capabilities).bookmarks) {
        links.push(kit.pages.link('bookmarks', null, 'My Bookmarks', { class: kit.cls('link') + ' jfbm-clips-bookmarks-link' }));
    }

    page.header = h('div', { class: 'jfbm-page-header jfbm-clips-header' }, links);
    kit.upgrade(page.header);
    const notices = [];
    if (capabilities.StoreReadOnly === true) {
        notices.push(kit.notice({ kind: 'info', text: readOnlyNoticeText(capabilities) }));
    }

    if (capabilities.ClipsEnabled === false) {
        notices.push(kit.notice({ kind: 'info', text: 'Making new clips is turned off by your server admin.' }));
    }

    page.notices = notices.length > 0 ? h('div', { class: 'jfbm-clips-notices' }, notices) : null;
}

// A select of the toolbar, labelled for screen readers by what it filters (the first choice says it for the eye).
function clipsSelect(label, choices, className, onChange) {
    const select = kit.select({ class: 'jfbm-clips-filter ' + className, 'aria-label': label }, choices.map(function (choice) {
        return { value: choice.value, text: choice.label };
    }));
    select.addEventListener('change', function () {
        onChange(select.value);
    });
    return select;
}

function buildClipsToolbar(page) {
    page.search = kit.searchBox({
        placeholder: 'Search clips…',
        label: 'Search clips',
        value: page.state.q,
        maxLength: PAGE_SEARCH_MAX_LENGTH,
        onSearch: function (text) {
            changeClipsState(page, { q: text }, { replace: true });
        }
    });
    page.stateSelect = clipsSelect('State', CLIPS_STATES, 'jfbm-clips-state-select', function (value) {
        changeClipsState(page, { state: clipsChoice(CLIPS_STATES, value).value });
    });
    page.modeSelect = clipsSelect('Mode', CLIPS_MODES, 'jfbm-clips-mode-select', function (value) {
        changeClipsState(page, { mode: clipsChoice(CLIPS_MODES, value).value });
    });
    page.typeSelect = clipsSelect('Type', CLIPS_TYPES, 'jfbm-clips-type-select', function (value) {
        changeClipsState(page, { type: clipsChoice(CLIPS_TYPES, value).value });
    });
    page.sortSelect = clipsSelect('Sort', CLIPS_SORTS, 'jfbm-clips-sort-select', function (value) {
        changeClipsState(page, { sort: clipsChoice(CLIPS_SORTS, value).value });
    });
    page.deletedBox = h('input', {
        type: 'checkbox',
        class: 'jfbm-clips-deleted',
        onchange: function () {
            changeClipsState(page, { deleted: page.deletedBox.checked });
        }
    });
    // The TV layout's Select toggle, as My Bookmarks': the checkboxes show only while it is on, and turning it off
    // drops the selection. Not while the data is read-only, when nothing can be deleted.
    const capabilities = page.capabilities || {};
    page.selectButton = page.tv && capabilities.StoreReadOnly !== true ? h('button', {
        type: 'button',
        class: 'jfbm-page-segment jfbm-page-select-button',
        'aria-pressed': 'false',
        text: 'Select',
        onclick: function () {
            setPageSelecting(page, !page.selecting);
        }
    }) : null;
    page.toolbar = h('div', { class: 'jfbm-page-toolbar jfbm-clips-toolbar' }, [
        page.search,
        page.stateSelect,
        page.modeSelect,
        page.typeSelect,
        page.sortSelect,
        h('label', { class: kit.cls('checkbox') + ' jfbm-clips-deleted-label' }, [page.deletedBox, h('span', { text: 'Include clips of deleted bookmarks' })]),
        page.selectButton
    ]);
}

// The toolbar after a state change; the search field only when its text differs (never while it is being typed in),
// through the search box's own setter, so the same text typed again is searched again.
function syncClipsToolbar(page) {
    const s = page.state;
    page.stateSelect.value = s.state;
    page.modeSelect.value = s.mode;
    page.typeSelect.value = s.type;
    page.sortSelect.value = s.sort;
    page.deletedBox.checked = s.deleted;
    if (document.activeElement !== page.search && String(page.search.value || '').trim() !== s.q) {
        page.search.setSearch(s.q);
    }

    syncClipsScope(page);
}

// A toolbar change: the state, the hash (pushed, so Back returns to the previous list, or replaced), the toolbar, and the
// list from its first page. A change of scope drops the selection: its clips were the other scope's.
function changeClipsState(page, changes, opts) {
    const next = Object.assign({}, page.state, changes);
    next.pages = 1;
    next.highlight = '';
    const scopeChanged = next.scope !== page.state.scope;
    page.state = next;
    if (scopeChanged) {
        page.selected.clear();
        page.body.classList.toggle('jfbm-clips-all-users', clipsAdminScope(page));
        updateBulkBar(page);
    }

    kit.pages.setParams(CLIPS_PAGE.key, writeClipsState(next), { replace: !!(opts && opts.replace) });
    syncClipsToolbar(page);
    return loadClipsContent(page);
}

function clearClipsFilters(page) {
    // The field is cleared even while it has the focus, with the search box's memory (as My Bookmarks' Clear filters); in
    // the admins' scope the owner too (it stays the scope).
    page.search.setSearch('');
    changeClipsState(page, { q: '', state: '', mode: '', type: '', deleted: true, user: '' });
}

// The list as the state says: the pages the hash counts (one request of up to 200, then more), with the summary asked
// next to it; "Loading…" shows after 300 ms. A highlight not on those pages (a link names a clip, not its
// page) reads on a page at a time until it shows, the list ends or PAGE_MAX_PAGES pages are shown, and the hash then
// counts the pages shown. A load the state moved on from (a quicker toolbar change, a render) is dropped.
async function loadClipsContent(page) {
    const generation = ++page.generation;
    const s = page.state;
    stopClipsPolling(page);
    page.loading = true;
    page.shown = new Map();
    page.nextIndex = 0;
    page.overlap = 0;
    page.total = null;
    if (page.missing) {
        page.missing.remove();
        page.missing = null;
    }

    // The focus in the list it replaces (Retry, Clear filters, a row's button): back on a row once it is read.
    page.listFocus = page.listFocus || page.content.contains(document.activeElement);
    page.content.replaceChildren();
    page.content.removeAttribute('role');
    page.content.setAttribute('aria-busy', 'true');
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
        const summary = clipsSummaryRequest(page);
        let wanted = s.pages * CLIPS_PER_PAGE;
        let total = 0;
        let startIndex = 0;
        for (;;) {
            if (startIndex >= wanted) {
                if (!s.highlight || page.shown.has(s.highlight) || wanted >= PAGE_MAX_PAGES * CLIPS_PER_PAGE) {
                    break;
                }

                // The highlight on a later page: one more.
                wanted += CLIPS_PER_PAGE;
            }

            const limit = Math.min(wanted - startIndex, PAGE_MAX_LIMIT);
            const result = await api('GET', clipsRoute(page, ''), undefined, clipsQuery(s, startIndex, limit, clipsAdminScope(page)));
            if (!current()) {
                return;
            }

            const items = (result && result.Items) || [];
            total = result && typeof result.TotalRecordCount === 'number' ? result.TotalRecordCount : startIndex + items.length;
            appendClipRows(page, items);
            startIndex += items.length;
            if (items.length < limit || startIndex >= total) {
                break;
            }
        }

        page.nextIndex = startIndex;
        page.total = total;
        if (wanted > s.pages * CLIPS_PER_PAGE) {
            // Read on for the highlight: the hash counts the pages shown now (finishClipsLoad writes it).
            s.pages = Math.min(PAGE_MAX_PAGES, Math.max(1, Math.ceil(page.shown.size / CLIPS_PER_PAGE)));
        }

        page.summary = await summary;
        if (!current()) {
            return;
        }

        finishClipsLoad(page);
    } catch (e) {
        if (current() && !clipsAdminTurnedOff(page, e)) {
            showClipsError(page, e);
        }
    } finally {
        if (page.generation === generation) {
            page.loading = false;
            if (page.listFocus && page.context.isCurrent()) {
                page.listFocus = false;
                restoreListFocus(page, '.jfbm-clips-row', clipRowFocusTarget);
            }

            // The poll of the clips being made, now that the list is the page's (none after a refused load).
            syncClipsPolling(page);
        }

        clearTimeout(loadingTimer);
        loading.remove();
    }
}

// GET /Bookmarks/Clips/Summary (in the admins' scope /Bookmarks/Admin/Clips/Summary), or null when it is refused
// (the line is then left out; the list says what it can).
function clipsSummaryRequest(page) {
    return api('GET', clipsRoute(page, 'Summary')).then(null, function (e) {
        if (!clipsAdminTurnedOff(page, e)) {
            report(e);
        }

        return null;
    });
}

// The clips appended, each once (a clip "Load more" answers again after the order moved is skipped); the content is a
// list (role="list") once it has rows; a notice alone isn't one.
function appendClipRows(page, items) {
    items.forEach(function (clip) {
        const key = pageItemKey(clip.Id);
        if (page.shown.has(key)) {
            return;
        }

        page.shown.set(key, clip);
        if (page.selected.has(key)) {
            // Selected before a reload of the list: the clip as just read (the bulk delete's dialog counts it so).
            page.selected.set(key, clip);
        }

        page.content.append(renderClipRow(page, clip));
    });
    if (page.shown.size > 0) {
        page.content.setAttribute('role', 'list');
    }
}

function finishClipsLoad(page) {
    const s = page.state;
    page.content.removeAttribute('aria-busy');
    kit.upgrade(page.content);
    updateClipsSummary(page);
    page.pager.update({ busy: false, hasMore: page.shown.size < page.total });
    // A clip selected before the list was read again stays selected while it is shown and may still be selected.
    syncSelection(page);
    if (page.shown.size === 0) {
        showClipsEmpty(page);
    }

    if (s.highlight) {
        // The clip has this one landing (a toast's link, My Bookmarks' link, a deep link): the hash drops it in place, so
        // Back, a reload or a change of the list doesn't scroll to it again, and the entry stays where it is. A clip the
        // list doesn't hold (deleted since the link was made, or one the filters leave out) is said above the list.
        if (!highlightClip(page, s.highlight)) {
            page.missing = kit.notice({ kind: 'info', text: 'That clip isn\'t in this list: it was deleted, or the filters leave it out.' });
            page.missing.classList.add('jfbm-clips-missing');
            page.content.parentNode.insertBefore(page.missing, page.content);
        }

        s.highlight = '';
        kit.pages.setParams(CLIPS_PAGE.key, writeClipsState(s), { replace: true });
    }
}

// The empty states: with filters on, "No clips match these filters." and Clear filters; with none, how a clip
// is made, or why this user can't make one.
function showClipsEmpty(page) {
    page.content.removeAttribute('role');
    page.content.replaceChildren(clipsHasFilters(page.state)
        ? kit.notice({
            text: 'No clips match these filters.',
            action: {
                label: 'Clear filters',
                onClick: function () {
                    clearClipsFilters(page);
                }
            }
        })
        : kit.notice({ text: clipsAdminScope(page) ? 'Nobody has a clip on this server yet.' : clipsEmptyText(page.capabilities) }));
}

function clipsEmptyText(capabilities) {
    const c = capabilities || {};
    if (c.ClipsEnabled === false) {
        return 'No clips yet.';
    }

    return c.CanCreateClips === false
        ? 'No clips yet. Ask your server admin for download permission to make clips.'
        : 'No clips yet. Mark a range with the scissors button in the player, then choose Save & make clip.';
}

function showClipsError(page, error) {
    page.content.removeAttribute('aria-busy');
    page.content.removeAttribute('role');
    page.content.replaceChildren(kit.notice({
        kind: 'error',
        text: error && error.message ? error.message : 'The clips could not be loaded.',
        action: {
            label: 'Retry',
            onClick: function () {
                loadClipsContent(page);
            }
        }
    }));
    page.pager.update({ busy: false, hasMore: false });
}

// "14 clips · 1.6 GB · 1 in progress", the other states' counts when there are any, and with filters on how
// many of them match.
function updateClipsSummary(page) {
    const parts = [];
    const summary = page.summary;
    if (clipsAdminScope(page)) {
        // The admins' scope: every user's totals, or the owner's; the Owner select's users come from them too.
        const text = clipsAdminSummaryText(summary, page.state.user);
        if (text) {
            parts.push(h('span', { class: 'jfbm-clips-totals', text: text }));
        }

        fillOwnerSelect(page);
    } else if (summary && typeof summary.Count === 'number') {
        parts.push(h('span', { class: 'jfbm-clips-totals', text: clipsSummaryText(summary) }));
    }

    if (clipsHasFilters(page.state) && typeof page.total === 'number') {
        parts.push(h('span', { class: 'jfbm-clips-matches', text: page.total === 1 ? '1 clip matches these filters' : page.total + ' clips match these filters' }));
    }

    page.summaryLine.replaceChildren.apply(page.summaryLine, parts);
    kit.setHidden(page.summaryLine, parts.length === 0);
}

function clipsSummaryText(summary) {
    const parts = [plural(summary.Count, 'clip'), formatSize(summary.TotalBytes || 0)];
    if (summary.InProgress > 0) {
        parts.push(summary.InProgress + ' in progress');
    }

    if (summary.Failed > 0) {
        parts.push(summary.Failed + ' failed');
    }

    if (summary.Missing > 0) {
        parts.push(summary.Missing === 1 ? '1 file missing' : summary.Missing + ' files missing');
    }

    if (summary.FolderUnavailable > 0) {
        // Offline, or there but not recognised as the clips' own (the server counts both as FolderUnavailable).
        parts.push(summary.FolderUnavailable + ' in an offline or unrecognised clip folder');
    }

    return parts.join(' · ');
}

// "Load more": the next page appended, asked from earlier by the shown clips that moved in the server's order since
// (their rows are skipped), and the hash's page count moved on in place (Back still returns to the previous filters).
// Only the moves this request was asked after are settled by its answer: a clip the poll ends or removes while it is under
// way counts for the next one (counting a move twice only asks again for a clip the page shows, which it skips).
async function loadMoreClips(page) {
    const generation = page.generation;
    const s = page.state;
    page.pager.update({ busy: true });
    try {
        const used = page.overlap;
        const start = Math.max(0, page.nextIndex - used);
        const result = await api('GET', clipsRoute(page, ''), undefined, clipsQuery(s, start, CLIPS_PER_PAGE, clipsAdminScope(page)));
        if (page.generation !== generation || !page.context.isCurrent()) {
            return;
        }

        const items = (result && result.Items) || [];
        appendClipRows(page, items);
        page.nextIndex = start + items.length;
        page.overlap = Math.max(0, page.overlap - used);
        if (result && typeof result.TotalRecordCount === 'number') {
            page.total = result.TotalRecordCount;
        }

        s.pages = Math.min(PAGE_MAX_PAGES, Math.max(1, Math.ceil(page.shown.size / CLIPS_PER_PAGE)));
        kit.pages.setParams(CLIPS_PAGE.key, writeClipsState(s), { replace: true });
        updateClipsSummary(page);
        page.pager.update({ busy: false, hasMore: page.shown.size < page.total });
        kit.upgrade(page.content);
        syncClipsPolling(page);
    } catch (e) {
        if (page.generation === generation && !clipsAdminTurnedOff(page, e)) {
            page.pager.update({ busy: false });
            toast(e.message);
        }
    }
}

// The clip marked for a moment, scrolled into the middle and given the focus (the row itself, focusable by script only, so
// a keyboard, a remote or a screen reader lands there too); false when the list doesn't hold it (deleted, or filtered
// out) or not within the pages read.
function highlightClip(page, key) {
    const target = page.content.querySelector('[data-jfbm-clip="' + cssKey(key) + '"]');
    if (!target) {
        return false;
    }

    target.classList.add('jfbm-page-highlight');
    target.setAttribute('tabindex', '-1');
    // Focused before the scroll, so the row ends in the middle where the browser doesn't know preventScroll (older TVs).
    target.focus({ preventScroll: true });
    if (typeof target.scrollIntoView === 'function') {
        target.scrollIntoView({ block: 'center' });
    }

    setTimeout(function () {
        page.content.querySelectorAll('.jfbm-page-highlight').forEach(function (node) {
            node.classList.remove('jfbm-page-highlight');
        });
    }, PAGE_HIGHLIGHT_MS);
    return true;
}

// ------------------------------------------------------------------------------------------ a row

// What the page calls a clip (its title): its bookmark's name, else its note's first line, else the clip file's
// name without its extension, else its range.
function clipTitle(clip) {
    const c = clip.Clip || {};
    const own = clip.Name || noteFirstLine(clip.Note);
    if (own) {
        return own;
    }

    if (c.FileName) {
        return String(c.FileName).replace(/\.[^.]*$/, '');
    }

    return 'Clip ' + formatTime(c.StartTicks) + '–' + formatTime(c.EndTicks);
}

// Accurate or Fast: a clip being made by its job's mode, any other with a file by its file's.
function clipModeLabel(clip) {
    const c = clip.Clip || {};
    if (c.State !== 'InProgress' && c.Container) {
        return c.Container === 'mkv' ? 'Fast' : 'Accurate';
    }

    return c.Mode === 'Fast' ? 'Fast' : c.Mode === 'Accurate' ? 'Accurate' : '';
}

// One clip: kit.listItem with the video as its item (data-id, data-type) and the clip's own key
// (data-jfbm-clip), its video's (data-jfbm-item) and its state (data-jfbm-state); its actions as icon
// buttons with the More button a narrow screen shows alone (the stylesheet), and a checkbox for the bulk delete before the
// poster, ticked while the clip is selected, for a clip that may be deleted (not one whose folder is offline or not
// recognised, nor while the data is read-only).
function renderClipRow(page, clip) {
    const c = clip.Clip || {};
    const key = pageItemKey(clip.Id);
    const title = clipTitle(clip);
    // The admins' scope has its own actions (no rename, remake or edit of other users' clips), the owner and the
    // file's path (75-page-clips-admin.js).
    const admin = clipsAdminScope(page);
    const actions = admin ? clipAdminActions(page, clip) : clipActions(page, clip);
    const buttons = actions.map(function (action) {
        const button = iconButton(action.icon, action.label, action.run);
        if (action.suggested) {
            button.classList.add('jfbm-clip-action-suggested');
        }

        return button;
    });
    if (actions.length > 0) {
        const more = iconButton('more_vert', 'More actions', function () {
            openActionMenu(title, actions);
        });
        more.classList.add('jfbm-row-more');
        buttons.push(more);
    }

    const row = kit.listItem({ id: clip.ItemId, type: clip.ItemType || null, title: title, href: false }, {
        className: 'jfbm-clips-row',
        thumb: clipThumb(clip),
        lines: (admin ? [clipOwnerLine(clip)] : []).concat([clipStateLine(clip), clipFactsLine(clip), clipBadges(clip, admin), clipErrorLine(clip), clipNoteLine(clip, page.capabilities)], admin ? [clipPathLine(clip, page.tv)] : []),
        actions: buttons.length > 0 ? [h('div', { class: 'jfbm-row-actions jfbm-clip-actions' }, buttons)] : null
    });
    if (clipSelectable(page, clip)) {
        const box = h('input', { type: 'checkbox', class: 'jfbm-row-select-box', 'aria-label': 'Select ' + title, checked: page.selected.has(key) });
        box.addEventListener('change', function () {
            if (box.checked && page.shown.has(key)) {
                page.selected.set(key, page.shown.get(key));
            } else {
                page.selected.delete(key);
            }

            updateBulkBar(page);
        });
        row.insertBefore(h('label', { class: 'jfbm-row-select', title: 'Select' }, box), row.firstChild);
    } else if (!(page.capabilities && page.capabilities.StoreReadOnly === true)) {
        // The checkbox's place kept, so the posters stay in one column.
        row.insertBefore(h('span', { class: 'jfbm-row-select jfbm-row-select-none', 'aria-hidden': 'true' }), row.firstChild);
    }

    row.setAttribute('data-jfbm-clip', key);
    row.setAttribute('data-jfbm-item', pageItemKey(clip.ItemId));
    row.setAttribute('data-jfbm-state', c.State || '');
    if (admin) {
        row.setAttribute('data-jfbm-owner', pageItemKey(clip.UserId));
    }

    return row;
}

// Whether a clip may be selected for the bulk delete: not one whose
// folder is offline or not recognised, nor one whose last rename isn't finished (the server holds it), and
// nothing while the data is read-only.
function clipSelectable(page, clip) {
    const c = (clip && clip.Clip) || {};
    return !!c.State && c.State !== 'FolderUnavailable' && c.State !== 'RenameUnfinished' && !(page.capabilities && page.capabilities.StoreReadOnly === true);
}

// The actions of a row, in order, for its buttons and its menu alike: { icon, label, run, suggested }.
// Preview and Download for a file that is there, when the viewer may download (Capabilities.CanDownloadClips); Play while
// the viewer can open the video; Edit bookmark for a live bookmark; Make the clip again for a live bookmark with a range
// whose video the viewer can open, while clips can be made and none is being made, never for a folder offline (it would
// write a new file elsewhere and fail to delete the old one), marked when the range changed or the file is missing; Cancel
// for a clip being made; Delete for a file that is there, Forget for one gone, Dismiss for a failed clip without a file. A
// clip whose last rename isn't finished offers Play and Edit only, as one whose folder is offline. While the
// data is read-only, only what writes nothing.
function clipActions(page, clip) {
    const capabilities = page.capabilities || {};
    const c = clip.Clip || {};
    const state = c.State;
    const writable = capabilities.StoreReadOnly !== true;
    const live = clip.BookmarkDeleted !== true;
    const actions = [];
    if (state === 'Ready' && capabilities.CanDownloadClips === true) {
        actions.push({ icon: 'ondemand_video', label: 'Preview', run: function () {
            openPreview(clip, {
                title: clipTitle(clip),
                returnFocus: clipRowReturnFocus(page, pageItemKey(clip.Id), 'Preview'),
                // The file route refused it (deleted in another tab, its folder offline): the row read again.
                onUnavailable: function () {
                    refreshClipKeys(page, [pageItemKey(clip.Id)]);
                }
            });
        } });
        actions.push({ icon: 'file_download', label: 'Download', run: function () {
            downloadClip(clip);
        } });
    }

    if (clip.ItemAvailable === true) {
        actions.push({ icon: 'play_arrow', label: 'Play the video at the clip\'s start', run: function () {
            playClipSource(clip);
        } });
    }

    if (live) {
        actions.push({ icon: 'edit', label: 'Edit bookmark', run: function () {
            editClipBookmark(page, clip);
        } });
    }

    // Rename and Rename file: not while the clip is being made, nor for a folder offline or not
    // recognised (nothing that renames its file), nor while its last rename isn't finished; Rename file when the file still
    // has its old name, marked unless the clip is in a library (renaming its file there costs what Jellyfin
    // kept for it, so it isn't suggested).
    if (writable && (state === 'Ready' || state === 'Missing' || state === 'Failed')) {
        actions.push({ icon: 'label', label: 'Rename clip', run: function () {
            openClipRename(page, clip);
        } });
    }

    if (writable && state === 'Ready' && c.IsNameOutdated === true) {
        actions.push({ icon: 'insert_drive_file', label: 'Rename file', suggested: c.InLibrary !== true, run: function () {
            confirmClipRenameFile(page, clip);
        } });
    }

    if (writable && live && state !== 'InProgress' && state !== 'FolderUnavailable' && state !== 'RenameUnfinished' && capabilities.CanCreateClips === true
        && clip.ItemAvailable === true && typeof clip.BookmarkEndTicks === 'number') {
        actions.push({ icon: 'content_cut', label: 'Make the clip again', suggested: c.IsOutdated === true || state === 'Missing', run: function () {
            confirmRemake(page, clip);
        } });
    }

    if (writable && live && state === 'InProgress') {
        actions.push({ icon: 'cancel', label: 'Cancel the clip', run: function () {
            cancelClipJob(page, clip);
        } });
    }

    if (writable && state === 'Ready') {
        actions.push({ icon: 'delete', label: 'Delete clip', run: function () {
            confirmClipDelete(page, clip);
        } });
    } else if (writable && state === 'Missing') {
        actions.push({ icon: 'remove_circle_outline', label: 'Forget clip', run: function () {
            confirmClipForget(page, clip);
        } });
    } else if (writable && state === 'Failed') {
        actions.push({ icon: 'clear', label: 'Dismiss', run: function () {
            dismissClip(page, clip);
        } });
    }

    return actions;
}

// The 16:9 box: the video's poster, linked to the video, only while the viewer can open it; a
// placeholder otherwise, and when the image fails. A plain box of ours, out of the native card classes Jellyfin
// Enhanced's card tags decorate.
function clipThumb(clip) {
    const available = clip.ItemAvailable === true;
    const placeholder = clip.ItemType === 'Episode' ? 'tv' : 'movie';
    const src = available ? posterUrl(clip.ItemId, CLIPS_POSTER_HEIGHT) : null;
    const box = h(available ? 'a' : 'div', {
        class: 'jfbm-clip-thumb',
        href: available ? kit.itemHref(clip.ItemId) : null,
        tabindex: available ? '-1' : null,
        'aria-hidden': 'true'
    }, src ? h('img', { src: src, loading: 'lazy', alt: '' }) : icon(placeholder));
    const image = box.querySelector('img');
    if (image) {
        image.addEventListener('error', function () {
            image.remove();
            box.append(icon(placeholder));
        });
    }

    return box;
}

// "Ready · Accurate · 45 MB", "Queued · Fast", "Making the clip 43% · Accurate" with its bar, "Failed · Fast", "File
// missing · Fast · 1.2 MB", "Clip folder offline · Fast · 1.2 MB".
function clipStateLine(clip) {
    const line = h('div', { class: 'jfbm-clip-state' });
    fillClipState(line, clip);
    return line;
}

function fillClipState(line, clip) {
    const c = clip.Clip || {};
    let label;
    let symbol;
    let bar = null;
    switch (c.State) {
        case 'Ready':
            label = 'Ready';
            symbol = 'check_circle';
            break;
        case 'InProgress':
            if (c.Status === 'Running') {
                const percent = typeof c.Progress === 'number' ? Math.max(0, Math.min(100, Math.floor(c.Progress))) : 0;
                label = 'Making the clip ' + percent + '%';
                bar = h('div', { class: 'jfbm-progress', role: 'progressbar', 'aria-label': 'Making the clip', 'aria-valuenow': String(percent), 'aria-valuemin': '0', 'aria-valuemax': '100' },
                    h('div', { style: 'width:' + percent + '%' }));
            } else {
                label = 'Queued';
            }

            symbol = 'hourglass_empty';
            break;
        case 'Missing':
            label = 'File missing';
            symbol = 'error';
            break;
        case 'FolderUnavailable':
            // RootAvailable true: the folder is there, but isn't recognised as the clips' own (no marker file of its own),
            // so its missing file isn't taken for gone.
            label = c.RootAvailable === true ? 'Clip folder not recognised' : 'Clip folder offline';
            symbol = 'portable_wifi_off';
            break;
        case 'RenameUnfinished':
            label = 'Rename not finished';
            symbol = 'sync_problem';
            break;
        default:
            label = c.Status === 'Cancelled' ? 'Cancelled' : 'Failed';
            symbol = c.Status === 'Cancelled' ? 'cancel' : 'error';
            break;
    }

    const parts = [label];
    const mode = clipModeLabel(clip);
    if (mode) {
        parts.push(mode);
    }

    if (c.FileName && typeof c.FileSize === 'number' && c.State !== 'InProgress' && c.State !== 'Failed') {
        parts.push(formatSize(c.FileSize));
    }

    line.replaceChildren(icon(symbol), h('span', { class: 'jfbm-clip-state-text', text: parts.join(' · ') }));
    if (bar) {
        line.append(bar);
    }
}

// "Heat (1995) · 1:02:03–1:05:00 (2:57) · made 10/2/2026": the video (a link while the viewer can open it), the clip's
// range and length, and when the file was made (its job's end, else the file's own time), else when it was asked for.
function clipFactsLine(clip) {
    const c = clip.Clip || {};
    const facts = [];
    if (typeof c.StartTicks === 'number' && typeof c.EndTicks === 'number') {
        const length = typeof c.DurationTicks === 'number' ? c.DurationTicks : c.EndTicks - c.StartTicks;
        facts.push(formatTime(c.StartTicks) + '–' + formatTime(c.EndTicks) + (length > 0 ? ' (' + formatTime(length) + ')' : ''));
    }

    // A file that isn't there to look at (missing, its folder offline) is dated by its job's end.
    const made = c.MadeAt || ((c.State === 'Missing' || c.State === 'FolderUnavailable') && c.Status === 'Completed' ? c.CompletedAt : null);
    if (made) {
        facts.push('made ' + kit.format.date(made));
    } else if (c.RequestedAt) {
        facts.push('asked for ' + kit.format.ago(c.RequestedAt));
    }

    const video = clip.ItemAvailable === true
        ? h('a', { class: 'jfbm-clip-item-link', href: kit.itemHref(clip.ItemId), text: clip.ItemName || '' })
        : h('span', { class: 'jfbm-clip-item', text: clip.ItemName || '' });
    return h('div', { class: 'jfbm-clip-facts' }, [video].concat(facts.length > 0 ? [' · ' + facts.join(' · ')] : []));
}

// What "Name changed" says (IsNameOutdated), before the action that gives the file its name.
const CLIP_NAME_CHANGED_TITLE = 'The clip file\'s name isn\'t the one Bookmarks gives this clip now (the bookmark was renamed, or the name holds words of its note from an earlier version).';

// The badges: what the state doesn't say already. In the admins' scope "Name changed" names its owner's
// action, not one the admin has there (no rename of other users' clips).
function clipBadges(clip, admin) {
    const c = clip.Clip || {};
    const badges = [];
    if (clip.BookmarkDeleted === true) {
        badges.push(['Bookmark deleted', 'jfbm-badge-deleted', 'The bookmark was deleted and its clip kept.']);
    }

    if (c.IsOutdated === true) {
        badges.push(['Range changed', 'jfbm-badge-outdated', 'The bookmark\'s range changed after this clip was made.']);
    }

    if (c.IsNameOutdated === true) {
        // Since 1.2.0 a clip file's name is the video's name, the bookmark's name and the range, never the note (an
        // admin sees clip paths): a clip of a bookmark without a name whose file an earlier version named after its note
        // shows it too.
        badges.push(['Name changed', 'jfbm-badge-renamed', admin
            ? CLIP_NAME_CHANGED_TITLE + ' Its owner can rename the file on their My Clips page (Rename file).'
            : CLIP_NAME_CHANGED_TITLE + ' Rename file gives it its name.']);
    }

    if (c.LastJobFailed === true) {
        badges.push(['Last remake failed', 'jfbm-badge-remake', c.Error ? 'The last remake failed: ' + c.Error : 'The last remake ended without a new file; this is the earlier clip.']);
    }

    if (c.InLibrary === true) {
        badges.push([c.LibraryName ? 'In library "' + c.LibraryName + '"' : 'In a library', 'jfbm-badge-library', 'Everyone who can open that library sees this clip file.']);
    }

    return badges.length === 0 ? null : h('div', { class: 'jfbm-clip-badges' }, badges.map(function (badge) {
        return h('span', { class: 'jfbm-badge ' + badge[1], text: badge[0], title: badge[2] });
    }));
}

// A failed clip's reason (its job's error, the server's words), under the facts.
function clipErrorLine(clip) {
    const c = clip.Clip || {};
    return c.State === 'Failed' && c.Status !== 'Cancelled' && c.Error ? h('div', { class: 'jfbm-clip-error', text: c.Error }) : null;
}

// Why a clip whose folder is offline offers so little (only Play and Edit, nothing that deletes, forgets or makes
// its file again): its file is probably fine where the folder is. Also a clip whose last rename isn't
// finished. A folder that isn't recognised can be confirmed by an admin: told so to an admin as their own step, to
// anyone else as their server admin's.
function clipNoteLine(clip, capabilities) {
    const c = clip.Clip || {};
    if (c.State === 'RenameUnfinished') {
        return h('div', {
            class: 'jfbm-clip-note',
            text: 'Its file\'s last rename isn\'t finished (the server stopped during it, or couldn\'t save it). The server finishes it as soon as it can reach the clip\'s folder and save the bookmark data, and tries again after a minute, then less often while nothing changes; until then nothing deletes, renames or makes this clip again.'
        });
    }

    if (c.State !== 'FolderUnavailable') {
        return null;
    }

    return h('div', {
        class: 'jfbm-clip-note',
        text: c.RootAvailable === true
            ? 'Its folder is there, but isn\'t recognised as the one this clip was made in (a drive or share that isn\'t mounted?), so the file isn\'t taken for gone. '
                + (capabilities && capabilities.IsAdmin === true
                    ? 'You can confirm the folder on the Bookmarks settings page\'s Diagnostics tab (Dashboard, Plugins, Bookmarks).'
                    : 'Your server admin can confirm the folder.')
            : 'The drive or folder with this clip isn\'t available right now; its file isn\'t taken for gone.'
    });
}

function clipRowNode(page, key) {
    return page.content.querySelector('.jfbm-clips-row[data-jfbm-clip="' + cssKey(key) + '"]');
}

// A row's control for the focus: its first action that can take it (on a phone the More button, the others hidden), else
// its checkbox or its video's link, else the row itself (focusable by script only, as a highlight's landing).
function clipRowFocusTarget(row) {
    const all = function (selector) {
        return Array.prototype.slice.call(row.querySelectorAll(selector));
    };
    const target = pageFirstFocusable(all('.jfbm-clip-actions button').concat(all('.jfbm-row-select-box'), all('a.jfbm-clip-item-link')));
    if (target) {
        return target;
    }

    row.setAttribute('tabindex', '-1');
    return row;
}

// Where a row's dialog gives the focus back when the button that opened it was drawn again meanwhile (a row read again
// after a refusal) or went (the clip gone while the dialog was open): the row as drawn now, on the action of that name
// (title), else its first; the row gone, the next row's (in the order shown when the dialog opened), else the previous
// one's, else the page's fallback.
function clipRowReturnFocus(page, key, title) {
    const order = Array.prototype.slice.call(page.content.querySelectorAll('.jfbm-clips-row')).map(function (row) {
        return row.getAttribute('data-jfbm-clip');
    });
    return function () {
        const row = clipRowNode(page, key);
        if (row) {
            const same = title ? Array.prototype.slice.call(row.querySelectorAll('.jfbm-clip-actions button')).filter(function (button) {
                return button.getAttribute('title') === title && pageCanFocus(button);
            })[0] : null;
            return same || clipRowFocusTarget(row);
        }

        const index = order.indexOf(key);
        const others = index < 0 ? [] : order.slice(index + 1).concat(order.slice(0, index).reverse());
        for (let i = 0; i < others.length; i++) {
            const other = clipRowNode(page, others[i]);
            if (other) {
                return clipRowFocusTarget(other);
            }
        }

        return pageFallbackFocus(page);
    };
}

// A dialog's button enabled again after a refusal (the dialog stays for another try): the focus back on it when the
// disabled button had lost it (the browser drops it to the body).
function refocusDialogButton(dialog, button) {
    if (!dialog.panel.contains(document.activeElement)) {
        button.focus();
    }
}

// A clip drawn again in its place from the clip as just read, ticked while it is selected (My Bookmarks' rule: a redraw
// keeps the selection; one that may not be selected any more, its folder gone offline, leaves it at the next
// syncSelection); the focus stays in the row when it was there, on the same action when the row still has it.
function replaceClipRow(page, key, clip) {
    const old = clipRowNode(page, key);
    page.shown.set(key, clip);
    if (page.selected.has(key)) {
        page.selected.set(key, clip);
    }

    if (!old || !old.parentNode) {
        return;
    }

    const fresh = renderClipRow(page, clip);
    if (old.classList.contains('jfbm-page-highlight')) {
        fresh.classList.add('jfbm-page-highlight');
    }

    const active = document.activeElement;
    const focused = old.contains(active);
    const rowFocused = active === old;
    const focusedTitle = focused && active && typeof active.getAttribute === 'function' ? active.getAttribute('title') : null;
    old.parentNode.insertBefore(fresh, old);
    old.remove();
    kit.upgrade(fresh);
    if (rowFocused) {
        // The row itself had the focus (a highlight's landing): the fresh row takes it.
        fresh.setAttribute('tabindex', '-1');
        fresh.focus({ preventScroll: true });
    } else if (focused) {
        const same = focusedTitle ? Array.prototype.slice.call(fresh.querySelectorAll('button')).filter(function (button) {
            return button.getAttribute('title') === focusedTitle;
        })[0] : null;
        const target = same || fresh.querySelector('a[href]:not([tabindex="-1"]), button');
        if (target) {
            target.focus();
        }
    }
}

// A clip that isn't one any more (deleted, elsewhere or here, or no longer matching the filters) taken out of the list and
// the selection; the focus in it goes to the next row's first action, else the previous row's, else Load more or the
// toolbar (it used to fall to the page's body, where a keyboard or a remote starts again from the top).
function removeClipRow(page, key) {
    const old = clipRowNode(page, key);
    if (old) {
        moveFocusFrom(page, old, '.jfbm-clips-row', clipRowFocusTarget);
        old.remove();
    }

    page.shown.delete(key);
    if (page.selected.delete(key)) {
        updateBulkBar(page);
    }
}

// ------------------------------------------------------------------------------------------ the poll

function clipsInProgress(page) {
    const keys = [];
    if (page) {
        page.shown.forEach(function (clip, key) {
            if (clip.Clip && clip.Clip.State === 'InProgress') {
                keys.push(key);
            }
        });
    }

    return keys;
}

// The next poll in 2.5 s while a shown clip is being made, the page is shown and the tab isn't hidden; none otherwise.
// Shown, not only current: in Plugin Pages' host the page left stays current in its hidden view until it shows again,
// when the kit renders it again (a new page, which polls for itself).
function syncClipsPolling(page) {
    if (!page) {
        return;
    }

    if (page.retired) {
        // Drawn again in "My clips" after an admin route's 403 (75-page-clips-admin.js): the new page polls for itself.
        stopClipsPolling(page);
        return;
    }

    if (!page.context.isShown() || clipsInProgress(page).length === 0) {
        stopClipsPolling(page);
        return;
    }

    if (page.poll.timer !== null || page.poll.busy || page.loading || clipsTabHidden()) {
        return;
    }

    page.poll.timer = setTimeout(function () {
        page.poll.timer = null;
        pollClips(page);
    }, CLIPS_STATUS_POLL_MS);
}

function stopClipsPolling(page) {
    if (page && page.poll.timer !== null) {
        clearTimeout(page.poll.timer);
        page.poll.timer = null;
    }
}

// One poll: GET /Bookmarks/Clips/Status for every shown clip being made (200 ids a request), then
// each one's state in place; the ones whose job has ended read again and drawn in place, the ones left out removed, and
// the summary asked again after either. A refused poll is tried again at the next tick. A page no longer shown (left, or
// rendered again) stops at the next request's answer.
async function pollClips(page) {
    if (page.retired || !page.context.isShown() || page.loading || clipsTabHidden()) {
        return;
    }

    const keys = clipsInProgress(page);
    if (keys.length === 0) {
        return;
    }

    const generation = page.generation;
    page.poll.busy = true;
    try {
        const answers = [];
        for (let i = 0; i < keys.length; i += CLIPS_STATUS_BATCH) {
            const result = await api('GET', clipsRoute(page, 'Status'), undefined, { ids: keys.slice(i, i + CLIPS_STATUS_BATCH).join(',') });
            if (page.generation !== generation || !page.context.isShown()) {
                return;
            }

            (result || []).forEach(function (status) {
                answers.push(status);
            });
        }

        await applyClipStatuses(page, keys, answers, generation);
    } catch (e) {
        if (!clipsAdminTurnedOff(page, e)) {
            report(e);
        }
    } finally {
        page.poll.busy = false;
        // Also after a poll the page moved on from (a load meanwhile): the next tick follows the clips shown now, or none
        // while that load is still under way (it asks for the poll itself when it ends).
        syncClipsPolling(page);
    }
}

async function applyClipStatuses(page, keys, answers, generation) {
    const byKey = new Map();
    answers.forEach(function (status) {
        byKey.set(pageItemKey(status.Id), status);
    });
    const ended = [];
    let changed = false;
    keys.forEach(function (key) {
        const clip = page.shown.get(key);
        if (!clip) {
            return;
        }

        const status = byKey.get(key);
        if (!status) {
            // No clip of the user's any more (deleted elsewhere, or its bookmark deleted with it).
            removeClipRow(page, key);
            page.overlap++;
            changed = true;
            return;
        }

        clip.Clip = Object.assign({}, clip.Clip, {
            Status: status.Status,
            State: status.State,
            Progress: status.Progress,
            Error: status.Error,
            FileName: status.FileName,
            FileSize: status.FileSize
        });
        if (status.State === 'InProgress') {
            const row = clipRowNode(page, key);
            if (row) {
                fillClipState(row.querySelector('.jfbm-clip-state'), clip);
            }
        } else {
            ended.push(key);
        }
    });
    if (ended.length > 0) {
        await refreshClipRows(page, ended, generation);
        changed = true;
    }

    if (changed && page.generation === generation && page.context.isShown()) {
        await refreshClipsTotals(page, generation);
    }
}

// Shown clips read again with the page's filters, a video at a time (GET /Bookmarks/Clips with its itemId), and drawn in
// their place: the clips whose job has ended (the poll), and the ones a row action or a change made elsewhere
// changed. One no longer in the answer goes (deleted, or a state filter it fell out of); when the answer is cut short and
// leaves it out, the row shows what the page knew. Their places in the server's order may have moved, so the next "Load
// more" asks from earlier (counting a clip that didn't move only asks again for a clip the page shows, which it skips).
async function refreshClipRows(page, keys, generation) {
    const byVideo = new Map();
    keys.forEach(function (key) {
        const clip = page.shown.get(key);
        if (!clip) {
            return;
        }

        const video = pageItemKey(clip.ItemId);
        byVideo.set(video, (byVideo.get(video) || []).concat([key]));
    });
    const videos = Array.from(byVideo.keys());
    for (let i = 0; i < videos.length; i++) {
        let result = null;
        try {
            result = await api('GET', clipsRoute(page, ''), undefined, Object.assign(clipsQuery(page.state, 0, PAGE_MAX_LIMIT, clipsAdminScope(page)), { itemId: videos[i] }));
        } catch (e) {
            if (clipsAdminTurnedOff(page, e)) {
                return;
            }

            report(e);
        }

        if (page.generation !== generation || !page.context.isShown()) {
            return;
        }

        const items = (result && result.Items) || [];
        const complete = !!result && typeof result.TotalRecordCount === 'number' && result.TotalRecordCount <= items.length;
        const fresh = new Map();
        items.forEach(function (clip) {
            fresh.set(pageItemKey(clip.Id), clip);
        });
        byVideo.get(videos[i]).forEach(function (key) {
            if (!page.shown.has(key)) {
                // Taken out meanwhile (another refresh, an action's answer).
                return;
            }

            page.overlap++;
            if (fresh.has(key)) {
                replaceClipRow(page, key, fresh.get(key));
            } else if (complete) {
                removeClipRow(page, key);
            } else {
                replaceClipRow(page, key, page.shown.get(key));
            }
        });
    }
}

// The summary line after a change, and with filters on the list's total (limit 1); the empty state when nothing is
// shown any more.
async function refreshClipsTotals(page, generation) {
    const requests = [clipsSummaryRequest(page)];
    if (clipsHasFilters(page.state)) {
        requests.push(api('GET', clipsRoute(page, ''), undefined, clipsQuery(page.state, 0, 1, clipsAdminScope(page))).then(null, function (e) {
            if (!clipsAdminTurnedOff(page, e)) {
                report(e);
            }

            return null;
        }));
    }

    const answers = await Promise.all(requests);
    if (page.generation !== generation || !page.context.isShown()) {
        return;
    }

    if (answers[0]) {
        page.summary = answers[0];
    }

    if (answers[1] && typeof answers[1].TotalRecordCount === 'number') {
        page.total = answers[1].TotalRecordCount;
    } else if (!clipsHasFilters(page.state) && page.summary && typeof page.summary.Count === 'number') {
        // Without filters the list is every clip the summary counts.
        page.total = page.summary.Count;
    }

    updateClipsSummary(page);
    page.pager.update({ hasMore: page.shown.size < page.total });
    syncSelection(page);
    if (page.shown.size === 0) {
        showClipsEmpty(page);
    }
}

// ------------------------------------------------------------------------------------------ the row actions

// Why the bulk delete kept a clip (each after "One could not be deleted: "; CLIP_IN_USE_BULK is My Bookmarks').
const CLIP_IN_PROGRESS_BULK = 'its clip is being made. Cancel it, or wait for it, first.';
const CLIP_OFFLINE_BULK = 'its clip folder isn\'t available (offline, or not recognised as the one its clips were made in).';
const CLIP_REFUSED_BULK = 'its clip file couldn\'t be removed (it isn\'t in the server\'s clip folders, or its folder can\'t be read).';

// A change of a bookmark made elsewhere (35-lists.js's notifyChanged: the editor opened from a row here, a rename or a delete
// on another page, the player): the page's clips of that video read again a moment later, together. A clip watcher's report
// (watchClip follows a clip being made for its clip-ready toast) counts only when the page shows that clip otherwise and isn't
// following it itself: a clip being made is the page's own poll's. Nothing while the page isn't shown (the kit renders a
// hidden view afresh when it shows again), while a load is under way (it reads everything afresh), or for the page's own
// notifications (notifyFromPage).
state.changeListeners.add(function (itemId, detail) {
    const page = currentClipsPage;
    if (!page || page.notifying || page.loading || !page.content || !page.context.isShown()) {
        return;
    }

    const keys = [];
    if (detail && detail.bookmark) {
        const key = pageItemKey(detail.bookmark.Id);
        const shown = page.shown.get(key);
        const now = (shown && shown.Clip) || {};
        const report = detail.bookmark.Clip || {};
        if (!shown || now.State === 'InProgress' || (report.Status === now.Status && (report.FileName || null) === (now.FileName || null))) {
            return;
        }

        keys.push(key);
    } else {
        const video = pageItemKey(itemId);
        page.shown.forEach(function (clip, key) {
            if (pageItemKey(clip.ItemId) === video) {
                keys.push(key);
            }
        });
    }

    if (keys.length === 0) {
        return;
    }

    keys.forEach(function (key) {
        page.pendingKeys.add(key);
    });
    clearTimeout(page.changeTimer);
    page.changeTimer = setTimeout(function () {
        const pending = Array.from(page.pendingKeys);
        page.pendingKeys.clear();
        refreshClipKeys(page, pending);
    }, PAGE_RELOAD_DELAY_MS);
});

// The other lists of the video (its details page's, the player's) told of a change made here, without the page reading
// itself again for it.
function notifyFromPage(page, itemId) {
    page.notifying = true;
    try {
        notifyChanged(itemId);
    } finally {
        page.notifying = false;
    }
}

// Some shown clips read again (refreshClipRows) and drawn in place, then the summary, the selection and the poll (a clip
// made again is being made now).
async function refreshClipKeys(page, keys) {
    if (page.loading || !page.context.isShown()) {
        return;
    }

    const generation = page.generation;
    await refreshClipRows(page, keys, generation);
    if (page.generation !== generation || !page.context.isShown()) {
        return;
    }

    await refreshClipsTotals(page, generation);
    syncClipsPolling(page);
}

// A clip deleted, forgotten or dismissed from here: its row out of the list and the selection, one more move for Load more,
// the other lists told, then the summary (and the empty state when it was the last).
function clipGone(page, clip) {
    removeClipRow(page, pageItemKey(clip.Id));
    page.overlap++;
    notifyFromPage(page, clip.ItemId);
    refreshClipsTotals(page, page.generation);
    syncClipsPolling(page);
}

// An action the server answered 404 (the clip gone elsewhere, such as in another tab) or 409 (the file there
// after all): the server's sentence, and the row read again, which takes a clip that is gone out of the list.
function clipActionRefused(page, clip, error) {
    toast(error && error.message ? error.message : 'This clip changed elsewhere.', { timeout: 8000 });
    refreshClipKeys(page, [pageItemKey(clip.Id)]);
}

// "Play the video at the clip's start": the video from the clip's own start (its file's range, else
// the job's), with the bookmark's media source and audio track, through the Sessions API as a bookmark row plays
// (40-player.js's playFrom: a seek when this browser is playing that video already).
function playClipSource(clip) {
    const c = clip.Clip || {};
    playFrom({
        ItemId: clip.ItemId,
        StartTicks: typeof c.StartTicks === 'number' ? c.StartTicks : clip.BookmarkStartTicks,
        MediaSourceId: clip.MediaSourceId,
        AudioStreamIndex: clip.AudioStreamIndex
    }).catch(function (e) {
        toast(e.message);
    });
}

// "Edit bookmark": the bookmark as the server has it now, in the editor (30-editor.js); a save reaches the page
// through notifyChanged. A bookmark deleted meanwhile says so, and its row is read again.
async function editClipBookmark(page, clip) {
    let bookmark;
    try {
        bookmark = await api('GET', 'Bookmarks/' + clip.Id);
    } catch (e) {
        if (e.status === 404) {
            clipActionRefused(page, clip, { message: 'This bookmark was deleted elsewhere.' });
        } else {
            toast(e.message);
        }

        return;
    }

    openEditor({ bookmark: bookmark });
}

// "Make the clip again": a small dialog with the bookmark's range as it is now (what the new clip takes),
// why a remake helps when the range changed or the file is gone, and the mode (the server's default first);
// POST /Bookmarks/{id}/Clip. The current file stays until the new one is there. The
// server's refusals (not enough free space, the clip folder away, a limit) are said in the dialog; once queued, the row is
// read again and followed by the page's poll, and the clip-ready toast comes wherever the user is then (watchClip).
function confirmRemake(page, clip) {
    const capabilities = page.capabilities || {};
    const c = clip.Clip || {};
    const key = pageItemKey(clip.Id);
    const start = clip.BookmarkStartTicks;
    const end = clip.BookmarkEndTicks;
    const dialog = kit.dialog({ title: 'Make the clip again?', modal: true, scopedKeys: true, returnFocus: clipRowReturnFocus(page, key, 'Make the clip again') });
    const lines = [h('p', { class: 'jfbm-clip-dialog-text', text: 'A new clip of the bookmark from ' + formatTime(start) + ' to ' + formatTime(end) + ' (' + formatTime(end - start) + ').' })];
    if (c.IsOutdated === true) {
        lines.push(h('p', { class: kit.cls('hint'), text: 'The bookmark\'s range changed after this clip was made; the new clip has the range it has now.' }));
    }

    if (c.State === 'Missing') {
        lines.push(h('p', { class: kit.cls('hint'), text: 'Its file is gone; the new clip takes its place.' }));
    } else if (c.FileName) {
        lines.push(h('p', { class: kit.cls('hint'), text: 'The current file stays until the new one is made, and is deleted then.' }));
    }

    const mode = kit.select({ class: 'jfbm-remake-mode', 'aria-label': 'Clip mode' }, [
        { value: 'Accurate', text: 'Accurate (MP4)' },
        { value: 'Fast', text: 'Fast (copy, MKV)' }
    ]);
    mode.value = capabilities.DefaultClipMode === 'Fast' ? 'Fast' : 'Accurate';
    const error = h('div', { class: kit.cls('error') + ' jfbm-clip-dialog-error', role: 'alert' });
    const make = kit.button('Make the clip', {
        variant: 'primary',
        className: 'jfbm-remake-confirm',
        onClick: async function () {
            make.disabled = true;
            error.textContent = '';
            let updated;
            try {
                updated = await api('POST', 'Bookmarks/' + clip.Id + '/Clip', { Mode: mode.value });
            } catch (e) {
                make.disabled = false;
                if (e.status === 404) {
                    dialog.close();
                    clipActionRefused(page, clip, e);
                } else {
                    error.textContent = e.message;
                    refocusDialogButton(dialog, make);
                }

                return;
            }

            dialog.close();
            toast('Making the clip.');
            if (updated && updated.Clip) {
                watchClip(updated);
            }

            notifyFromPage(page, clip.ItemId);
            refreshClipKeys(page, [key]);
        }
    });
    kit.append.apply(null, [dialog.body].concat(lines, [kit.field('Mode', mode), error, h('div', { class: kit.cls('actions') }, [kit.button('Cancel', { onClick: dialog.close }), make])]));
    make.focus();
    return dialog;
}

// What renaming a clip file inside a library does there: Jellyfin sees the old video go and a new one come; also what
// else follows the old video
// (playlists, other users' bookmarks of it) and what stays under the old name (Jellyfin's .nfo and trickplay files).
function clipLibraryRenameWarning(clip) {
    const c = clip.Clip || {};
    const where = c.LibraryName ? 'in the library "' + c.LibraryName + '"' : 'in a library';
    return 'This clip\'s file is ' + where + '. Renaming the file makes Jellyfin see a new video there: what it keeps for the old one (played, favourite, collections, playlists, and anyone\'s bookmarks of that video) starts over, and files Jellyfin wrote next to it (an .nfo, trickplay images) stay under the old name.';
}

// The sentence a dialog says after a 409 once the clip was read again and its UpdatedAt moved (renamed in
// another tab), with the name it has now, as My Bookmarks' rename says it.
function clipChangedElsewhereText(latest, action) {
    return latest.Name
        ? 'Changed elsewhere: it is named "' + latest.Name + '" now. ' + action + ' replaces that.'
        : 'Changed elsewhere: it has no name now. ' + action + ' names it.';
}

// The row drawn from the clip the server answered with (a rename's), then its video's rows read again under the page's
// filters (the new name may no longer match the search, or sort elsewhere), the other lists told.
function clipRenamed(page, clip, updated) {
    const key = pageItemKey(clip.Id);
    if (updated && updated.Id) {
        replaceClipRow(page, key, updated);
    }

    notifyFromPage(page, clip.ItemId);
    refreshClipKeys(page, [key]);
}

// "Rename": the clip's name (its bookmark's, also a deleted bookmark's kept clip's), and for a file
// that is there "Also rename the file", ticked unless the clip is in a library (then unticked, with what it does there).
// POST /Bookmarks/Clips/{id}/Name with the clip's UpdatedAt: both are renamed or nothing is. The server's
// refusals are said in the dialog, which stays for another try: a file in use (409, after its three attempts), the clip
// changed elsewhere or being made (409: the row read again, and the next try carries what it is now), its folder offline
// or not recognised or the data not written (503: the row read again). A clip gone (404) closes it with the row read again.
// The focus starts in the name, selected, so typing replaces it (nothing here destroys anything); Enter there
// renames, Enter on the box ticks it (a remote's OK), on a button is the button's.
function openClipRename(page, clip) {
    const capabilities = page.capabilities || {};
    const key = pageItemKey(clip.Id);
    let current = clip;
    let saving = false;
    let input = null;
    let save = null;
    const dialog = kit.dialog({
        title: 'Rename clip',
        modal: true,
        scopedKeys: true,
        initialFocus: function () {
            return input;
        },
        returnFocus: clipRowReturnFocus(page, key, 'Rename clip'),
        onKey: function (event) {
            const target = event.target;
            if (event.key !== 'Enter' || !target || target.tagName === 'BUTTON') {
                return false;
            }

            event.preventDefault();
            if (box && target === box) {
                box.checked = !box.checked;
            } else {
                save();
            }

            return true;
        }
    });
    input = kit.input({
        class: 'jfbm-clip-rename-input',
        value: clip.Name || '',
        maxlength: String(capabilities.MaxNameLength || 100),
        autocomplete: 'off',
        placeholder: 'No name'
    });
    const c = clip.Clip || {};
    const hasFile = c.State === 'Ready' && !!c.FileName;
    let box = null;
    const fileLines = [];
    if (hasFile) {
        box = h('input', { type: 'checkbox', class: 'jfbm-clip-rename-file-box', checked: c.InLibrary !== true });
        fileLines.push(h('label', { class: kit.cls('checkbox') + ' jfbm-clip-rename-file' }, [box, h('span', { text: 'Also rename the file' })]));
        fileLines.push(h('p', { class: kit.cls('hint') + ' jfbm-clip-rename-current', text: 'The file is "' + c.FileName + '" now.' }));
        if (c.InLibrary === true) {
            fileLines.push(h('p', { class: kit.cls('hint') + ' jfbm-clip-rename-library', text: clipLibraryRenameWarning(clip) }));
        }
    }

    const error = h('div', { class: kit.cls('error') + ' jfbm-clip-dialog-error', role: 'alert' });
    const cancel = kit.button('Cancel', { onClick: dialog.close });
    const rename = kit.button('Rename', {
        variant: 'primary',
        className: 'jfbm-clip-rename-confirm',
        onClick: function () {
            save();
        }
    });
    const setBusy = function (busy) {
        saving = busy;
        [cancel, rename, input].concat(box ? [box] : []).forEach(function (control) {
            control.disabled = busy;
        });
    };

    // The row read again after a refusal: the next try carries the clip as it is now (its UpdatedAt); a clip that went
    // meanwhile closes the dialog. A clip renamed elsewhere meanwhile (its UpdatedAt moved) puts its name as it
    // is now in the field, selected, and says so, before a second Rename can replace it.
    const reread = async function () {
        const before = current;
        await refreshClipKeys(page, [key]);
        const latest = page.shown.get(key);
        if (!latest) {
            dialog.close();
            toast('This clip changed elsewhere.');
            return;
        }

        current = latest;
        if (latest.UpdatedAt !== before.UpdatedAt) {
            input.value = latest.Name || '';
            if (typeof input.select === 'function') {
                input.select();
            }

            error.textContent = clipChangedElsewhereText(latest, 'Rename');
        }
    };

    save = async function () {
        if (saving) {
            return;
        }

        const renameFile = !!(box && box.checked);
        setBusy(true);
        error.textContent = '';
        let updated;
        try {
            updated = await api('POST', 'Bookmarks/Clips/' + clip.Id + '/Name', {
                Name: input.value.trim() || null,
                RenameFile: renameFile,
                IfUpdatedAt: current.UpdatedAt || null
            });
        } catch (e) {
            setBusy(false);
            if (e.status === 404) {
                dialog.close();
                clipActionRefused(page, clip, e);
                return;
            }

            error.textContent = e.message;
            if (e.status === 409 || e.status === 503) {
                await reread();
            }

            if (dialog.panel.isConnected && !dialog.panel.contains(document.activeElement)) {
                input.focus();
            }

            return;
        }

        const before = (current.Clip || {}).FileName;
        const after = updated && updated.Clip ? updated.Clip.FileName : null;
        clipRenamed(page, clip, updated);
        dialog.close();
        toast(renameFile && after && after !== before ? 'Clip and its file renamed.' : 'Clip renamed.');
    };

    const form = h('form', {
        class: 'jfbm-form jfbm-clip-rename',
        onsubmit: function (event) {
            event.preventDefault();
            save();
        }
    }, [kit.field('Name', input)].concat(fileLines, [error, h('div', { class: kit.cls('actions') }, [cancel, rename])]));
    dialog.body.append(form);
    input.focus();
    if (typeof input.select === 'function') {
        input.select();
    }

    return dialog;
}

// "Rename file" for a clip whose file still has the name it had before its bookmark was renamed
// ("Name changed"): a confirmation naming the file (and, in a library, what renaming it does there), then POST
// /Bookmarks/Clips/{id}/RenameFile, which gives it the name Bookmarks gives the clip now. Refusals as the rename's, said in
// the dialog. It changes a file and has nothing to type, so the focus starts on Cancel (as every dialog of ours that changes
// files); on a TV, one ArrowDown reaches Rename file.
function confirmClipRenameFile(page, clip) {
    const c = clip.Clip || {};
    const key = pageItemKey(clip.Id);
    let cancel = null;
    const dialog = kit.dialog({
        title: 'Rename file?',
        modal: true,
        scopedKeys: true,
        initialFocus: function () {
            return cancel;
        },
        returnFocus: clipRowReturnFocus(page, key, 'Rename file')
    });
    let current = clip;
    const question = h('p', { class: 'jfbm-clip-dialog-text', text: 'Rename the clip file "' + (c.FileName || clipTitle(clip)) + '" to match the clip\'s name, "' + clipTitle(clip) + '"?' });
    const lines = [question];
    if (c.InLibrary === true) {
        lines.push(h('p', { class: kit.cls('hint') + ' jfbm-clip-rename-library', text: clipLibraryRenameWarning(clip) }));
    }

    const error = h('div', { class: kit.cls('error') + ' jfbm-clip-dialog-error', role: 'alert' });
    const rename = kit.button('Rename file', {
        variant: 'primary',
        className: 'jfbm-clip-rename-file-confirm',
        onClick: async function () {
            rename.disabled = true;
            error.textContent = '';
            let updated;
            try {
                // The clip's UpdatedAt: renamed in another tab since, the file would get another name than the
                // one this dialog says, so the server answers 409 instead.
                updated = await api('POST', 'Bookmarks/Clips/' + clip.Id + '/RenameFile', { IfUpdatedAt: current.UpdatedAt || null });
            } catch (e) {
                rename.disabled = false;
                if (e.status === 404) {
                    dialog.close();
                    clipActionRefused(page, clip, e);
                } else {
                    error.textContent = e.message;
                    refocusDialogButton(dialog, rename);
                    if (e.status === 409 || e.status === 503) {
                        await rereadRenameFile();
                    }
                }

                return;
            }

            const after = updated && updated.Clip ? updated.Clip.FileName : null;
            clipRenamed(page, clip, updated);
            dialog.close();
            toast(after && after !== c.FileName ? 'File renamed to "' + after + '".' : 'The file has that name already.');
        }
    });
    // The row read again after a refusal: the next try carries its UpdatedAt; renamed elsewhere, the question
    // names the clip's name as it is now, and the dialog says so; a clip that went, or whose file has its name now, closes it.
    const rereadRenameFile = async function () {
        const before = current;
        await refreshClipKeys(page, [key]);
        const latest = page.shown.get(key);
        if (!latest || !latest.Clip || latest.Clip.State !== 'Ready' || latest.Clip.IsNameOutdated !== true) {
            dialog.close();
            toast(latest ? 'This clip changed elsewhere.' : 'This clip isn\'t here any more.');
            return;
        }

        current = latest;
        if (latest.UpdatedAt !== before.UpdatedAt) {
            question.textContent = 'Rename the clip file "' + (latest.Clip.FileName || clipTitle(latest)) + '" to match the clip\'s name, "' + clipTitle(latest) + '"?';
            error.textContent = latest.Name ? 'Changed elsewhere: the clip is named "' + latest.Name + '" now; Rename file names its file for that.' : 'Changed elsewhere: the clip has no name now; Rename file names its file for its note or video.';
        }
    };
    cancel = kit.button('Cancel', { onClick: dialog.close });
    kit.append.apply(null, [dialog.body].concat(lines, [error, h('div', { class: kit.cls('actions') }, [cancel, rename])]));
    return dialog;
}

// "Cancel": POST /Bookmarks/{id}/Clip/Cancel; a queued clip ends at once, a running one when its job stops
// (the poll follows it); any earlier file stays.
async function cancelClipJob(page, clip) {
    try {
        await api('POST', 'Bookmarks/' + clip.Id + '/Clip/Cancel');
    } catch (e) {
        if (e.status === 404) {
            clipActionRefused(page, clip, e);
        } else {
            toast(e.message);
        }

        return;
    }

    toast('Clip cancelled.');
    notifyFromPage(page, clip.ItemId);
    await refreshClipKeys(page, [pageItemKey(clip.Id)]);
}

// "Delete": "Delete the clip file "…" (45 MB)? The bookmark stays.", or for a deleted bookmark's kept clip
// "Delete this clip? Its bookmark was already deleted, so it will disappear from this list."; DELETE /Bookmarks/{id}/Clip,
// the file first. A file in use (another tab's preview, a download, Jellyfin playing it) is the server's 409 "The clip file
// is in use …", said in the dialog with the row as it was, so the user can try again; any other refusal likewise, and a 503
// (its folder offline or not recognised now) reads the row again, which then offers what such a row does.
// The focus starts on Cancel: a deleted file can't be brought back, so the least destructive control.
function confirmClipDelete(page, clip) {
    const c = clip.Clip || {};
    const key = pageItemKey(clip.Id);
    let cancel = null;
    const dialog = kit.dialog({
        title: 'Delete clip?',
        modal: true,
        scopedKeys: true,
        initialFocus: function () {
            return cancel;
        },
        returnFocus: clipRowReturnFocus(page, key, 'Delete clip')
    });
    const text = clip.BookmarkDeleted === true
        ? 'Delete this clip? Its bookmark was already deleted, so it will disappear from this list.'
        : 'Delete the clip file "' + (c.FileName || clipTitle(clip)) + '"' + (typeof c.FileSize === 'number' ? ' (' + formatSize(c.FileSize) + ')' : '') + '? The bookmark stays.';
    const error = h('div', { class: kit.cls('error') + ' jfbm-clip-dialog-error', role: 'alert' });
    const remove = kit.button('Delete', {
        variant: 'primary',
        className: 'jfbm-clip-delete-confirm',
        onClick: async function () {
            remove.disabled = true;
            error.textContent = '';
            try {
                await api('DELETE', 'Bookmarks/' + clip.Id + '/Clip');
            } catch (e) {
                remove.disabled = false;
                if (e.status === 404) {
                    dialog.close();
                    clipActionRefused(page, clip, e);
                } else {
                    error.textContent = e.message;
                    refocusDialogButton(dialog, remove);
                    if (e.status === 503) {
                        refreshClipKeys(page, [key]);
                    }
                }

                return;
            }

            dialog.close();
            toast('Clip deleted.');
            clipGone(page, clip);
        }
    });
    cancel = kit.button('Cancel', { onClick: dialog.close });
    kit.append(dialog.body, h('p', { class: 'jfbm-clip-dialog-text', text: text }), error, h('div', { class: kit.cls('actions') }, [cancel, remove]));
    return dialog;
}

// "Forget" for a clip whose file is gone: "Remove this entry? Its file is already gone." with what happens to the
// bookmark; DELETE /Bookmarks/{id}/Clip?onlyIfMissing=true, which never deletes a file: one that is there after all (made
// again or put back since the page read it) is the server's 409, said in a toast with the row read again; a folder that
// isn't there, or isn't recognised, is its 503, said in the dialog, with the row read again (it then
// offers what an offline row does, no Forget). The focus starts on Cancel, as Delete's.
function confirmClipForget(page, clip) {
    const key = pageItemKey(clip.Id);
    let cancel = null;
    const dialog = kit.dialog({
        title: 'Forget clip?',
        modal: true,
        scopedKeys: true,
        initialFocus: function () {
            return cancel;
        },
        returnFocus: clipRowReturnFocus(page, key, 'Forget clip')
    });
    const text = clip.BookmarkDeleted === true
        ? 'Remove this entry? Its file is already gone, and its bookmark was deleted, so it disappears from this list.'
        : 'Remove this entry? Its file is already gone. The bookmark stays.';
    const error = h('div', { class: kit.cls('error') + ' jfbm-clip-dialog-error', role: 'alert' });
    const forget = kit.button('Forget', {
        variant: 'primary',
        className: 'jfbm-clip-forget-confirm',
        onClick: async function () {
            forget.disabled = true;
            error.textContent = '';
            try {
                await api('DELETE', 'Bookmarks/' + clip.Id + '/Clip', undefined, { onlyIfMissing: 'true' });
            } catch (e) {
                forget.disabled = false;
                if (e.status === 404 || e.status === 409) {
                    dialog.close();
                    clipActionRefused(page, clip, e);
                } else {
                    error.textContent = e.message;
                    refocusDialogButton(dialog, forget);
                    if (e.status === 503) {
                        refreshClipKeys(page, [key]);
                    }
                }

                return;
            }

            dialog.close();
            toast('Clip forgotten.');
            clipGone(page, clip);
        }
    });
    cancel = kit.button('Cancel', { onClick: dialog.close });
    kit.append(dialog.body, h('p', { class: 'jfbm-clip-dialog-text', text: text }), error, h('div', { class: kit.cls('actions') }, [cancel, forget]));
    return dialog;
}

// "Dismiss" for a failed or cancelled clip without a file ("Clear the failed clip; the bookmark stays."): nothing
// of value goes, so no confirmation; the same route as Forget.
async function dismissClip(page, clip) {
    try {
        await api('DELETE', 'Bookmarks/' + clip.Id + '/Clip', undefined, { onlyIfMissing: 'true' });
    } catch (e) {
        if (e.status === 404 || e.status === 409 || e.status === 503) {
            clipActionRefused(page, clip, e);
        } else {
            toast(e.message);
        }

        return;
    }

    toast('Failed clip cleared; the bookmark stays.');
    clipGone(page, clip);
}

// "Delete n clips?" (Select, then Delete…): what goes (the files there with their size, the entries without a file),
// what the server keeps (a clip being made, whose job and any earlier file stay; any own clip
// may be selected), then POST /Bookmarks/Clips/BulkDelete in batches of 200 (the server's most; Select all over
// several pages holds up to 1000) through 60-page-bookmarks.js's deleteInBatches, with the progress under the description.
// What the server deleted (FilesDeleted, RecordsRemoved) and what is no clip of the user's any more (NotFound) leave the
// list; what it kept stays selected, named apart: a file in use (InUse), a clip being made (InProgress), its folder
// unavailable (FolderUnavailable, whose rows are read again and show it), another refusal (Failed), and a batch refused
// outright with the server's sentence. Closing the dialog meanwhile stops after the request under way, and the outcome
// comes as a toast. The focus starts on Cancel (files deleted can't be brought back) and comes back to the bar's Delete…,
// or, once nothing is selected and the bar is hidden, to the first row's action (else Load more or the toolbar).
function confirmClipsBulkDelete(page) {
    let clips = Array.from(page.selected.values());
    if (clips.length === 0) {
        return null;
    }

    let cancel = null;
    const returnTarget = function () {
        return pageFirstFocusable(Array.prototype.slice.call(page.content.querySelectorAll('.jfbm-clips-row')).map(clipRowFocusTarget)) || pageFallbackFocus(page);
    };
    const dialog = kit.dialog({
        title: 'Delete ' + plural(clips.length, 'clip') + '?',
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
    const describe = function () {
        const stateOf = function (clip) {
            return clip.Clip ? clip.Clip.State : null;
        };
        const files = clips.filter(function (clip) {
            return stateOf(clip) === 'Ready';
        });
        const making = clips.filter(function (clip) {
            return stateOf(clip) === 'InProgress';
        }).length;
        const bytes = files.reduce(function (sum, clip) {
            return sum + (typeof clip.Clip.FileSize === 'number' ? clip.Clip.FileSize : 0);
        }, 0);
        const parts = [];
        if (files.length > 0) {
            parts.push((files.length === 1 ? '1 clip file' : files.length + ' clip files') + ' (' + formatSize(bytes) + ') will be deleted.');
        }

        const others = clips.length - files.length - making;
        if (others > 0) {
            parts.push((others === 1 ? '1 clip without a file (gone or failed) is' : others + ' clips without a file (gone or failed) are') + ' taken off the list.');
        }

        if (making > 0) {
            parts.push(making === 1 ? '1 clip being made is kept; cancel it first.' : making + ' clips being made are kept; cancel them first.');
        }

        parts.push(files.length + others > 0 ? 'Their bookmarks stay; the clips of deleted bookmarks disappear from this list.' : 'Their bookmarks stay.');
        if (clipsAdminScope(page)) {
            // Whose clips they are: the admins' scope deletes other users' clips.
            const owners = new Set(clips.map(function (clip) {
                return pageItemKey(clip.UserId);
            }));
            parts.push(owners.size > 1 ? 'They belong to ' + owners.size + ' users.'
                : clips.length === 1 ? 'It is ' + clipOwnerPossessive(clips[0]) + ' clip.' : 'They are ' + clipOwnerPossessive(clips[0]) + ' clips.');
        }

        return parts.join(' ');
    };
    const description = h('p', { class: 'jfbm-bulk-description', text: describe() });
    const progress = h('p', { class: 'jfbm-bulk-progress ' + kit.cls('hint'), role: 'status' });
    const error = h('div', { class: kit.cls('error'), role: 'alert' });
    const deleteButton = kit.button('Delete', {
        variant: 'primary',
        onClick: async function () {
            deleteButton.disabled = true;
            error.textContent = '';
            const result = await deleteInBatches(clips, function (batch) {
                return api('POST', clipsRoute(page, 'BulkDelete'), {
                    Ids: batch.map(function (clip) {
                        return clip.Id;
                    })
                });
            }, ['FilesDeleted', 'RecordsRemoved', 'NotFound', 'InProgress', 'InUse', 'FolderUnavailable', 'Failed'], progress, function () {
                return closed;
            });
            if (result.error && clipsAdminTurnedOff(page, result.error)) {
                // The admins' scope ended meanwhile (75-page-clips-admin.js): the page is drawn again in "My clips".
                dialog.close();
                return;
            }
            const keysOf = function (ids) {
                return new Set(ids.map(pageItemKey));
            };
            const gone = keysOf([].concat(result.FilesDeleted, result.RecordsRemoved, result.NotFound));
            const removed = clips.filter(function (clip) {
                return gone.has(pageItemKey(clip.Id));
            });
            const kept = clips.filter(function (clip) {
                return !gone.has(pageItemKey(clip.Id));
            });
            const videos = new Set();
            removed.forEach(function (clip) {
                removeClipRow(page, pageItemKey(clip.Id));
                page.overlap++;
                videos.add(clip.ItemId);
            });
            videos.forEach(function (itemId) {
                notifyFromPage(page, itemId);
            });
            updateBulkBar(page);
            if (closed && document.activeElement === page.bulkDelete && !pageCanFocus(page.bulkDelete)) {
                // Closed while the request went: the bar's Delete… it went back to is hidden now.
                const back = returnTarget();
                if (back) {
                    back.focus();
                }
            }

            // The rows the server found otherwise than the page shows them (their folder offline, a clip being made) read
            // again, then the summary.
            const reread = keysOf([].concat(result.FolderUnavailable, result.InProgress));
            const stale = kept.filter(function (clip) {
                return reread.has(pageItemKey(clip.Id));
            }).map(function (clip) {
                return pageItemKey(clip.Id);
            });
            if (stale.length > 0) {
                refreshClipKeys(page, stale);
            } else {
                refreshClipsTotals(page, page.generation);
            }

            if (kept.length === 0 && !result.error) {
                dialog.close();
                toast(removed.length === 1 ? 'Clip deleted.' : plural(removed.length, 'clip') + ' deleted.');
                return;
            }

            clips = kept;
            const parts = [];
            if (removed.length > 0) {
                parts.push(plural(removed.length, 'clip') + ' deleted.');
            }

            const count = function (ids) {
                const keys = keysOf(ids);
                return kept.filter(function (clip) {
                    return keys.has(pageItemKey(clip.Id));
                }).length;
            };
            const couldNot = function (n, why) {
                return (n === 1 ? 'One could not be deleted' : n + ' could not be deleted') + (why ? ': ' + why : '.');
            };
            const reasons = [[result.InUse, CLIP_IN_USE_BULK], [result.InProgress, CLIP_IN_PROGRESS_BULK], [result.FolderUnavailable, CLIP_OFFLINE_BULK], [result.Failed, CLIP_REFUSED_BULK]];
            let answered = 0;
            reasons.forEach(function (reason) {
                const n = count(reason[0]);
                answered += n;
                if (n > 0) {
                    parts.push(couldNot(n, reason[1]));
                }
            });
            const unanswered = kept.length - answered;
            if (result.error) {
                parts.push((unanswered > 0 ? couldNot(unanswered, '') + ' ' : '') + result.error.message);
            } else if (result.stopped && unanswered > 0) {
                parts.push('Stopped: ' + plural(unanswered, 'clip') + (unanswered === 1 ? ' was' : ' were') + ' not deleted.');
            } else if (unanswered > 0) {
                parts.push(couldNot(unanswered, ''));
            }

            if (closed) {
                parts.push(kept.length === 1 ? 'It is still selected.' : 'They are still selected.');
                toast(parts.join(' '), { timeout: BULK_OUTCOME_TOAST_MS });
                return;
            }

            error.textContent = parts.join(' ');
            description.textContent = describe();
            deleteButton.disabled = false;
            refocusDialogButton(dialog, deleteButton);
        }
    });
    cancel = kit.button('Cancel', { onClick: dialog.close });
    kit.append(dialog.body,
        description,
        progress,
        error,
        h('div', { class: kit.cls('actions') }, [
            cancel,
            deleteButton
        ]));
    return dialog;
}
