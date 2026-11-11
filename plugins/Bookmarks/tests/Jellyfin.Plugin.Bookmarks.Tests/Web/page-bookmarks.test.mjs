// The My Bookmarks page (Web/js/60-page-bookmarks.js) in the kit's fake page, with the built ClientScript.
// PageHostingTests runs it:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfbm node --test plugins/Bookmarks/tests/Jellyfin.Plugin.Bookmarks.Tests/Web/page-bookmarks.test.mjs
// The page is registered with the kit's page host and linked from "Library tools"; its toolbar has each view's own sort
// list; the grouped and flat views read the Groups and Query endpoints with the state the hash carries (deep links,
// Back; an item or group that isn't a video id is ignored and the hash left as it came; the group is marked once, then
// dropped from the hash in place); paging appends; the empty and error states use the kit's notice; a change from a
// row reloads only that video's group (the flat view its rows) and the count line, the whole list for a video not
// shown (a clip's progress tick redraws its row); Clear filters lets the same term be searched again; the lists carry
// list roles and the Filters button folds with a filter on; the player's list and the details section link to the page
// while the capabilities offer it; a turned-off page shows the server's note. Then the inline rename (Name it and
// Rename; Enter, Escape and the Back key; a 409 shows the latest name), the selection and the bulk delete (the bar,
// Select all, the clip choice, InUse), the Player options dialog (the three AskForName states, the pause, the device
// shortcut with the reserved-key check, Reset to default), the Unavailable group and row, the TV layout (the Select
// toggle, the D-pad in the dialog, a row's focus order), the row menu of a narrow screen, and the read-only notice. And
// the fixes: a redraw of a video keeps its rows' selection (a sibling's rename, the InUse bookmark of a partial bulk
// delete) and an inline rename still being typed; the bulk delete in batches of 200 with the progress and the answers
// merged; InUse and Failed named apart; the server's default named in Player options whatever the user chose; the row
// menu's arrow keys; the bulk delete's dialog closed while the batches go stops them after the request under way, with
// the outcome as a toast; every inline rename open in a group survives its redraw, the focus where it was; a ticked
// bookmark's clip finishing is offered in the delete dialog (the selection's copy refreshed); the page left in Plugin
// Pages' hidden host view reads nothing there when a bookmark changes, and renders afresh when shown.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage, plain } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');

const SHELL = '<div class="jfbm-page" data-jfbm-page="bookmarks"><p class="jfbm-shell-note">This page needs the Bookmarks web UI, which is turned off or failed to load.</p></div>';
const SHELL_OFF = '<div class="jfbm-page"><p class="jfbm-shell-note">My Bookmarks is turned off by your server admin.</p></div>';
const CAPABILITIES = {
    ClipsEnabled: true,
    CanCreateClips: true,
    MaxClipDurationSeconds: 600,
    DefaultClipMode: 'Accurate',
    IsAdmin: false,
    StoreReadOnly: false,
    MaxNameLength: 100,
    MaxNoteLength: 500,
    Shortcuts: { BookmarkNow: 'Shift+B' },
    ReservedShortcuts: ['M', 'K', 'Shift+P', 'B'],
    ShowPlayerButtons: true,
    AskForName: false,
    AskForNameIsDefault: true,
    AskForNameDefault: false,
    PauseWhileNaming: false,
    SuggestChapterNames: true,
    Pages: { Bookmarks: true, Clips: false }
};
const HEAT = 'aaaa0000-0000-0000-0000-000000000001';
const GONE = 'bbbb0000-0000-0000-0000-000000000002';
const SHOW = 'cccc0000-0000-0000-0000-000000000003';

function bookmark(id, itemId, itemName, startSeconds, extra) {
    return Object.assign({
        Id: id,
        ItemId: itemId,
        ItemName: itemName,
        ItemType: 'Movie',
        StartTicks: startSeconds * 10000000,
        EndTicks: null,
        Name: null,
        Note: null,
        Clip: null,
        CreatedAt: '2026-10-01T10:00:00.0000000Z',
        UpdatedAt: '2026-10-05T10:00:00.0000000Z',
        ItemAvailable: true,
        Detached: false,
        Relinked: false,
        Origin: 'Native'
    }, extra || {});
}

function fixtures() {
    const heat = [
        bookmark('b1', HEAT, 'Heat (1995)', 3723, { Name: 'Bank job', Note: 'Watch the reflection', EndTicks: 3900 * 10000000 }),
        bookmark('b2', HEAT, 'Heat (1995)', 5400)
    ];
    const gone = [bookmark('b3', GONE, 'Gone (2001)', 60, { Name: 'Lost scene', ItemAvailable: false, Detached: true })];
    const show = [bookmark('b4', SHOW, 'Show S01E02 - Pilot', 120, { ItemType: 'Episode', Note: 'episode note' })];
    const groups = [
        { ItemId: HEAT, ItemName: 'Heat (1995)', ItemType: 'Movie', Year: 1995, ItemAvailable: true, Count: 2, LatestUpdatedAt: '2026-10-05T10:00:00.0000000Z', Bookmarks: heat, BookmarksTruncated: false },
        { ItemId: GONE, ItemName: 'Gone (2001)', ItemType: 'Movie', Year: 2001, ItemAvailable: false, Count: 1, LatestUpdatedAt: '2026-10-04T10:00:00.0000000Z', Bookmarks: gone, BookmarksTruncated: false },
        { ItemId: SHOW, ItemName: 'Show S01E02 - Pilot', ItemType: 'Episode', ItemAvailable: true, Count: 1, LatestUpdatedAt: '2026-10-03T10:00:00.0000000Z', Bookmarks: show, BookmarksTruncated: false }
    ];
    return { groups, flat: heat.concat(gone, show) };
}

// A page shaped like jellyfin-web around the script: the head fragment (the page known and on unless `off`), the
// LibraryMenu, the capabilities, the shell and the two endpoints answering slices of the fixtures.
async function start(options) {
    options = options || {};
    const page = createPage();
    page.context.location.hash = options.hash || '#/home';
    if (options.head !== null) {
        const island = page.element('script', { type: 'application/json', 'data-jfbm-head': '', 'data-jfbm-pages': '' });
        island.textContent = JSON.stringify(Object.assign({ pages: ['bookmarks', 'demo-a', 'demo-b'], off: ['demo-a', 'demo-b'], mode: 'FallbackOnly', nav: true }, options.head || {}));
        page.document.head.append(island);
    }

    const titles = [];
    page.context.LibraryMenu = { setTitle: (t) => titles.push(t) };
    page.titles = titles;
    page.respond('GET', '/Bookmarks/Capabilities', Object.assign({}, CAPABILITIES, options.capabilities || {}));
    page.respond('GET', '/Bookmarks/Pages/bookmarks', options.off ? SHELL_OFF : SHELL, 200, { contentType: 'text/html; charset=utf-8' });
    page.respond('GET', '/Bookmarks/Preferences', { AskForName: null, PauseWhileNaming: false });
    page.respond('POST', '/Bookmarks/Preferences', null, 204);
    page.respond('GET', '/Bookmarks', []);
    page.respond('GET', '/Sessions', [{ Id: 's1', DeviceId: 'device-1' }]);
    page.respond('POST', '/Sessions/s1/Playing/Seek', null, 204);
    page.respond('POST', '/Sessions/s1/Playing', null, 204);
    const data = options.data || fixtures();
    page.data = data;
    // The endpoints answer slices of the fixtures (the arrays may be changed by a test, so a reload shows the change);
    // a request with itemId answers that video's part, as the server does (the page's per-video reload).
    const slice = (name) => (entry) => {
        const start = Number(entry.query.startIndex || 0);
        const limit = Number(entry.query.limit || 50);
        const key = (id) => String(id).replace(/-/g, '').toLowerCase();
        const items = entry.query.itemId ? page.data[name].filter((x) => key(x.ItemId) === key(entry.query.itemId)) : page.data[name];
        return { Items: items.slice(start, start + limit), TotalRecordCount: items.length, StartIndex: start };
    };
    page.respond('GET', '/Bookmarks/Groups', slice('groups'));
    page.respond('GET', '/Bookmarks/Query', slice('flat'));
    page.run(source, file);
    await page.advance(30);
    page.installApiClient({ userId: 'user-1', user: { Id: 'user-1', Name: 'viewer', Policy: { IsAdministrator: false } } });
    await page.advance(60);
    return page;
}

function showFallbackPage(page) {
    const view = page.element('div', { id: 'fallbackPage', 'data-role': 'page', class: 'page', 'data-title': 'Page not found' }, [
        page.element('div', {}, [page.element('h1', {}, ['Page not found'])])
    ]);
    page.document.body.append(view);
    ['viewbeforeshow', 'pagebeforeshow', 'viewshow', 'pageshow'].forEach((type) => page.dispatch(view, type, { detail: { isRestored: false } }));
    return view;
}

function drawer(page) {
    const container = page.element('div', { class: 'mainDrawer-scrollContainer' }, [page.element('div', { class: 'userMenuOptions' })]);
    page.document.body.append(container);
    return container;
}

// The page shown on its fallback route, rendered.
async function opened(options) {
    const page = await start(Object.assign({ hash: '#/jfbm-bookmarks' }, options || {}));
    showFallbackPage(page);
    await page.advance(120);
    return page;
}

function root(page) {
    return page.document.querySelector('#fallbackPage > [data-jfbm-page="bookmarks"]');
}

function requests(page, path) {
    return page.fetchLog.filter((f) => f.path === path);
}

function lastRequest(page, path) {
    const all = requests(page, path);
    return all[all.length - 1];
}

function toolbar(page) {
    const r = root(page);
    return {
        search: r.querySelector('.jfbm-page-toolbar input[type="search"]'),
        types: r.querySelectorAll('.jfbm-page-toolbar [data-jfbm-type]'),
        pressedType: r.querySelector('[data-jfbm-type][aria-pressed="true"]').getAttribute('data-jfbm-type'),
        sort: r.querySelector('.jfbm-page-sort'),
        sortLabels: r.querySelectorAll('.jfbm-page-sort option').map((o) => o.textContent),
        views: r.querySelectorAll('.jfbm-page-toolbar [data-jfbm-view]'),
        pressedView: r.querySelector('[data-jfbm-view][aria-pressed="true"]').getAttribute('data-jfbm-view'),
        filtersButton: r.querySelector('.jfbm-page-filters-button'),
        filtersRow: r.querySelector('.jfbm-page-filters'),
        boxes: r.querySelectorAll('[data-jfbm-filter]'),
        count: r.querySelector('.jfbm-page-count'),
        content: r.querySelector('.jfbm-page-content'),
        pager: r.querySelector('.jfbm-pager')
    };
}

function clickType(page, value) {
    toolbar(page).types.find((b) => b.getAttribute('data-jfbm-type') === value).click();
}

function clickView(page, value) {
    toolbar(page).views.find((b) => b.getAttribute('data-jfbm-view') === value).click();
}

function chooseSort(page, value) {
    const select = toolbar(page).sort;
    select.value = value;
    page.dispatch(select, 'change');
}

function tick(page, key) {
    const box = toolbar(page).boxes.find((b) => b.getAttribute('data-jfbm-filter') === key);
    box.checked = !box.checked;
    page.dispatch(box, 'change');
}

function renders(page) {
    return page.context.__jfkit.jfbm.instance.pages.state().renders.bookmarks || 0;
}

function panel(page, title) {
    return page.document.querySelectorAll('.jfbm-backdrop .jfbm-panel').find((p) => p.querySelector('h3').textContent === title) || null;
}

test('the page is registered as the server declares it and linked first in "Library tools"; the demo pages, off, have no link', { skip }, async () => {
    const page = await start();
    const kit = page.context.__jfkit.jfbm.instance;
    // The demo registers at load (60-pagedemo.js), the page from 90-main.js; the nav goes by order, not by this.
    assert.deepEqual(plain(kit.pages.state().keys), ['demo-a', 'demo-b', 'bookmarks']);
    assert.equal(kit.pages.href('bookmarks', { item: HEAT }), '#/jfbm-bookmarks?item=' + HEAT);
    const container = drawer(page);
    await page.advance(400);
    const links = container.querySelectorAll('.jfkit-nav a');
    assert.deepEqual(links.map((a) => [a.getAttribute('data-jfbm-nav'), a.getAttribute('href'), a.textContent, a.getAttribute('data-order')]), [['bookmarks', '#/jfbm-bookmarks', 'My Bookmarks', '10']]);
    assert.ok(links[0].querySelector('.collections_bookmark'), 'the page\'s icon');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the grouped view by default: the toolbar with its sort list, the count line, one group per video with the poster only when it can be opened, rows by position', { skip }, async () => {
    const page = await opened();
    const r = root(page);
    assert.equal(r.getAttribute('data-jfbm-state'), 'on');
    assert.ok(page.titles.includes('My Bookmarks'));
    const t = toolbar(page);
    assert.equal(t.search.getAttribute('placeholder'), 'Search names, notes and titles…');
    assert.equal(t.search.getAttribute('maxlength'), '100');
    assert.deepEqual(t.types.map((b) => b.textContent), ['All', 'Movies', 'Episodes', 'Other']);
    assert.equal(t.pressedType, '');
    assert.deepEqual(t.sortLabels, ['Recently changed', 'Title A–Z', 'Most bookmarks']);
    assert.equal(t.sort.value, 'LatestUpdated');
    assert.deepEqual(t.views.map((b) => b.textContent), ['Grouped', 'Flat']);
    assert.equal(t.pressedView, 'grouped');
    assert.equal(t.filtersButton.textContent, 'Filters');
    assert.equal(t.filtersRow.classList.contains('hide'), true, 'the filters are folded while none is on');
    assert.deepEqual(t.boxes.map((b) => b.nextSibling.textContent), ['Has range', 'Has clip', 'Needs a name', 'Not in library']);

    assert.deepEqual(lastRequest(page, '/Bookmarks/Groups').query, { sortBy: 'LatestUpdated', startIndex: '0', limit: '25' });
    assert.deepEqual(lastRequest(page, '/Bookmarks/Query').query, { sortBy: 'UpdatedAt', startIndex: '0', limit: '1' }, 'the flat total for the count line');
    assert.equal(t.count.textContent, '4 bookmarks on 3 titles');
    assert.equal(t.content.hasAttribute('aria-busy'), false);
    const groups = t.content.querySelectorAll('.jfbm-group');
    assert.deepEqual(groups.map((g) => g.getAttribute('data-jfbm-group')), [HEAT, GONE, SHOW].map((id) => id.replace(/-/g, '')));

    const heat = groups[0].querySelector('.jfbm-group-header');
    assert.equal(heat.getAttribute('data-id'), HEAT.replace(/-/g, ''), 'the kit\'s row markup, so Jellyfin Enhanced\'s Hidden Content filter sees it');
    assert.equal(heat.getAttribute('data-type'), 'Movie');
    assert.equal(heat.querySelector('.listItemBodyText a').textContent, 'Heat (1995)');
    assert.equal(heat.querySelector('.listItemBodyText a').getAttribute('href'), '#/details?id=' + HEAT.replace(/-/g, '') + '&serverId=server-1');
    assert.match(heat.querySelector('.listItemBodyText.secondary').textContent, /^2 bookmarks · changed .+ ago$|^2 bookmarks · changed \d/);
    assert.equal(heat.querySelector('img').getAttribute('src'), 'http://127.0.0.1:8097/Items/' + HEAT.replace(/-/g, '') + '/Images/Primary?fillHeight=120&quality=90');
    assert.equal(heat.querySelector('.jfbm-open-title').textContent, 'Open title');
    const heatRows = groups[0].querySelectorAll('.jfbm-row');
    assert.deepEqual(heatRows.map((row) => row.getAttribute('data-id')), ['b1', 'b2']);
    assert.deepEqual(heatRows.map((row) => row.querySelector('.jfbm-time-button').textContent), ['1:02:03 – 1:05:00', '1:30:00']);
    assert.deepEqual(heatRows.map((row) => row.querySelector('.jfbm-row-title').textContent), ['Bank job', 'Bookmark at 1:30:00']);
    assert.equal(heatRows[0].querySelector('.jfbm-row-subtitle').textContent, 'Watch the reflection');
    assert.ok(heatRows[1].querySelector('.jfbm-name-it'));
    assert.equal(heatRows[0].querySelector('.jfbm-time-button').getAttribute('title'), 'Play from here');
    assert.deepEqual(heatRows[0].querySelectorAll('.jfbm-row-actions button').map((b) => b.getAttribute('title')), ['Rename', 'Make a clip', 'Edit', 'Delete', 'More actions'], 'a named range (Rename, and the More button of a narrow screen)');
    assert.deepEqual(heatRows[1].querySelectorAll('.jfbm-row-actions button').map((b) => b.getAttribute('title')), ['Make a clip (set an end first)', 'Edit', 'Delete', 'More actions'], 'an unnamed point bookmark: Name it in the title line, the scissors open the editor on End');
    assert.equal(groups[0].querySelector('.jfbm-row-item'), null, 'no video line inside a group');

    const gone = groups[1].querySelector('.jfbm-group-header');
    assert.equal(gone.querySelector('img'), null, 'no poster request for a video the viewer can\'t open');
    assert.equal(gone.querySelectorAll('a').length, 0, 'no link to it either');
    assert.equal(gone.querySelector('.jfbm-badge').textContent, 'Not in library');
    const goneRow = groups[1].querySelector('.jfbm-row');
    assert.equal(goneRow.querySelector('.jfbm-time-button').disabled, true);
    assert.equal(goneRow.querySelector('.jfbm-badge').textContent, 'Not in library');
    assert.ok(groups[2].querySelector('.jfbm-group-header img'));
    assert.equal(groups[2].querySelector('.jfbm-group-header').getAttribute('data-type'), 'Episode');

    assert.equal(t.pager.classList.contains('hide'), true, 'everything fits one page');
    assert.equal(page.fetchLog.filter((f) => f.path.startsWith('/Items')).length, 0, 'never a core items endpoint (the kit\'s privacy rule)');
    assert.ok(page.fetchLog.every((f) => f.userId === 'user-1'));
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the flat view, each view\'s sort list, and the sort kept or reset when the view changes; every change is a history entry without a kit render', { skip }, async () => {
    const page = await opened();
    const length = page.context.history.length;
    assert.equal(renders(page), 1);

    clickView(page, 'flat');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?view=flat');
    assert.equal(page.context.history.length, length + 1);
    assert.equal(renders(page), 1, 'the page changed itself; no render by the kit');
    let t = toolbar(page);
    assert.equal(t.pressedView, 'flat');
    assert.deepEqual(t.sortLabels, ['Recently changed', 'Recently created', 'Name A–Z', 'Title A–Z', 'Position in title']);
    assert.equal(t.sort.value, 'UpdatedAt', 'Recently changed kept across the views');
    assert.deepEqual(lastRequest(page, '/Bookmarks/Query').query, { sortBy: 'UpdatedAt', startIndex: '0', limit: '50' });
    assert.deepEqual(lastRequest(page, '/Bookmarks/Groups').query, { sortBy: 'LatestUpdated', startIndex: '0', limit: '1' });
    assert.equal(t.count.textContent, '4 bookmarks on 3 titles');
    const rows = t.content.querySelectorAll('.jfbm-row');
    assert.deepEqual(rows.map((row) => row.getAttribute('data-id')), ['b1', 'b2', 'b3', 'b4']);
    assert.equal(t.content.querySelectorAll('.jfbm-group').length, 0);
    assert.equal(rows[0].querySelector('.jfbm-row-item a').textContent, 'Heat (1995)');
    assert.equal(rows[0].querySelector('.jfbm-row-item a').getAttribute('href'), '#/details?id=' + HEAT.replace(/-/g, '') + '&serverId=server-1');
    assert.equal(rows[2].querySelector('.jfbm-row-item a'), null, 'a video that can\'t be opened is named, not linked');
    assert.equal(rows[2].querySelector('.jfbm-row-item').textContent, 'Gone (2001)');
    assert.equal(rows[2].querySelector('.jfbm-badge').textContent, 'Not in library');

    chooseSort(page, 'ItemName');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?view=flat&sort=ItemName');
    assert.equal(lastRequest(page, '/Bookmarks/Query').query.sortBy, 'ItemName');
    clickView(page, 'grouped');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?sort=ItemName', 'Title A–Z exists in both views, so it is kept');
    assert.equal(lastRequest(page, '/Bookmarks/Groups').query.sortBy, 'ItemName');
    assert.equal(toolbar(page).sort.value, 'ItemName');

    clickView(page, 'flat');
    await page.advance(50);
    chooseSort(page, 'Position');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?view=flat&sort=Position');
    clickView(page, 'grouped');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks', 'a flat-only sort goes back to the grouped default');
    t = toolbar(page);
    assert.equal(t.sort.value, 'LatestUpdated');
    assert.equal(lastRequest(page, '/Bookmarks/Groups').query.sortBy, 'LatestUpdated');
    assert.equal(page.context.history.length, length + 6);
    assert.equal(renders(page), 1);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('type, the filters and the search: the hash and the query follow; a search is typed into one history entry and keeps the focus; Clear filters', { skip }, async () => {
    const page = await opened();
    const length = page.context.history.length;

    clickType(page, 'Movie');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?type=Movie');
    assert.equal(toolbar(page).pressedType, 'Movie');
    assert.equal(lastRequest(page, '/Bookmarks/Groups').query.itemTypes, 'Movie');

    toolbar(page).filtersButton.click();
    assert.equal(toolbar(page).filtersRow.classList.contains('hide'), false);
    assert.equal(toolbar(page).filtersButton.getAttribute('aria-expanded'), 'true');
    tick(page, 'range');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?type=Movie&range=1');
    assert.equal(lastRequest(page, '/Bookmarks/Groups').query.hasRange, 'true');
    assert.equal(toolbar(page).filtersButton.textContent, 'Filters (1)');
    tick(page, 'missing');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?type=Movie&range=1&missing=1');
    assert.equal(lastRequest(page, '/Bookmarks/Groups').query.availability, 'Missing');
    assert.equal(toolbar(page).filtersButton.textContent, 'Filters (2)');
    tick(page, 'range');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?type=Movie&missing=1');
    assert.equal(lastRequest(page, '/Bookmarks/Groups').query.hasRange, undefined);
    assert.equal(page.context.history.length, length + 4);

    const search = toolbar(page).search;
    search.focus();
    search.value = 'he';
    page.dispatch(search, 'input');
    await page.advance(100);
    search.value = 'heat';
    page.dispatch(search, 'input');
    await page.advance(100);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?type=Movie&missing=1', 'nothing before 300 ms of quiet');
    await page.advance(250);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?q=heat&type=Movie&missing=1');
    assert.equal(page.context.history.length, length + 4, 'a search replaces the entry');
    assert.equal(lastRequest(page, '/Bookmarks/Groups').query.searchTerm, 'heat');
    assert.equal(page.document.activeElement, search, 'the field keeps the focus');
    assert.equal(search.value, 'heat');
    assert.equal(renders(page), 1);

    // Nothing matches: the empty state with Clear filters, which clears the search, the type and the filters.
    page.respond('GET', '/Bookmarks/Groups', { Items: [], TotalRecordCount: 0, StartIndex: 0 });
    page.respond('GET', '/Bookmarks/Query', { Items: [], TotalRecordCount: 0, StartIndex: 0 });
    tick(page, 'unnamed');
    await page.advance(50);
    const notice = toolbar(page).content.querySelector('.jfbm-notice');
    assert.equal(notice.querySelector('.jfbm-notice-text').textContent, 'No bookmarks match these filters.');
    assert.equal(toolbar(page).count.textContent, '0 bookmarks on 0 titles');
    notice.querySelector('button').click();
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks');
    assert.equal(search.value, '');
    assert.equal(toolbar(page).pressedType, '');
    assert.deepEqual(toolbar(page).boxes.map((b) => b.checked), [false, false, false, false]);
    assert.equal(toolbar(page).filtersButton.textContent, 'Filters');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a deep link: every parameter read into the toolbar and the query (two pages in one request), a group scrolled back to; a sort the view lacks is its default', { skip }, async () => {
    const page = await opened({ hash: '#/jfbm-bookmarks?view=flat&q=heat&type=Movie&sort=Name&range=1&clip=1&unnamed=1&missing=1&item=' + HEAT + '&page=2&group=' + HEAT.replace(/-/g, '') });
    const length = page.context.history.length;
    assert.deepEqual(lastRequest(page, '/Bookmarks/Query').query, {
        searchTerm: 'heat', itemId: HEAT.replace(/-/g, ''), itemTypes: 'Movie', hasRange: 'true', hasClip: 'true', named: 'false', availability: 'Missing', sortBy: 'Name', startIndex: '0', limit: '100'
    });
    assert.deepEqual(lastRequest(page, '/Bookmarks/Groups').query, {
        searchTerm: 'heat', itemId: HEAT.replace(/-/g, ''), itemTypes: 'Movie', hasRange: 'true', hasClip: 'true', named: 'false', availability: 'Missing', sortBy: 'LatestUpdated', startIndex: '0', limit: '1'
    });
    const t = toolbar(page);
    assert.equal(t.pressedView, 'flat');
    assert.equal(t.pressedType, 'Movie');
    assert.equal(t.sort.value, 'Name');
    assert.equal(t.search.value, 'heat');
    assert.deepEqual(t.boxes.map((b) => b.checked), [true, true, true, true]);
    assert.equal(t.filtersButton.textContent, 'Filters (4)');
    assert.equal(t.filtersRow.classList.contains('hide'), false);
    assert.equal(t.count.querySelector('.jfbm-page-all-videos').textContent, 'Show every video');
    assert.equal(t.content.querySelector('.jfbm-row[data-jfbm-item="' + HEAT.replace(/-/g, '') + '"]').classList.contains('jfbm-page-highlight'), true, 'the group opened before Back (the fixture honours the item filter, so only this video\'s rows are there)');
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?view=flat&q=heat&type=Movie&sort=Name&range=1&clip=1&unnamed=1&missing=1&item=' + HEAT.replace(/-/g, '') + '&page=2', 'the group dropped in place once it landed; the item as the page keys it');
    assert.equal(page.context.history.length, length, 'rewritten in place');
    await page.advance(3000);
    assert.equal(t.content.querySelector('.jfbm-page-highlight'), null);
    t.count.querySelector('.jfbm-page-all-videos').click();
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?view=flat&q=heat&type=Movie&sort=Name&range=1&clip=1&unnamed=1&missing=1');

    const bad = await opened({ hash: '#/jfbm-bookmarks?sort=Position&view=grouped&page=x' });
    assert.equal(toolbar(bad).sort.value, 'LatestUpdated');
    assert.equal(lastRequest(bad, '/Bookmarks/Groups').query.sortBy, 'LatestUpdated');
    assert.equal(bad.context.location.hash, '#/jfbm-bookmarks?sort=Position&view=grouped&page=x', 'the hash is left as it came');
    assert.equal(page.errors.length + bad.errors.length, 0);
});

test('a deep link whose group or item isn\'t a video id: the list loads with the default, nothing is asked or looked up for it, the hash is left as it came; an id in any form is taken', { skip }, async () => {
    // A malformed group used to reach querySelector unescaped (a DOMException wiping the list into the error state).
    const badGroup = await opened({ hash: '#/jfbm-bookmarks?group=x%22%5D' });
    let t = toolbar(badGroup);
    assert.equal(t.content.querySelectorAll('.jfbm-group').length, 3);
    assert.equal(t.content.querySelector('.jfbm-notice'), null, 'no error state');
    assert.equal(t.content.querySelector('.jfbm-page-highlight'), null);
    assert.equal(badGroup.scrolled.length, 0);
    assert.equal(badGroup.context.location.hash, '#/jfbm-bookmarks?group=x%22%5D', 'left as it came');
    assert.equal(badGroup.errors.length, 0, badGroup.errors.join('\n'));

    // A malformed item used to be sent to the server, whose 400 became the page's error.
    const badItem = await opened({ hash: '#/jfbm-bookmarks?item=not-a-guid' });
    t = toolbar(badItem);
    assert.equal(lastRequest(badItem, '/Bookmarks/Groups').query.itemId, undefined);
    assert.equal(lastRequest(badItem, '/Bookmarks/Query').query.itemId, undefined);
    assert.equal(t.count.textContent, '4 bookmarks on 3 titles');
    assert.equal(t.count.querySelector('.jfbm-page-all-videos'), null, 'no video filter, so no Show every video');
    assert.equal(badItem.context.location.hash, '#/jfbm-bookmarks?item=not-a-guid');
    assert.equal(badItem.errors.length, 0, badItem.errors.join('\n'));

    // A GUID with dashes or in upper case is the same video.
    const dashed = await opened({ hash: '#/jfbm-bookmarks?item=' + HEAT.toUpperCase() + '&group=' + HEAT.toUpperCase() });
    t = toolbar(dashed);
    assert.equal(lastRequest(dashed, '/Bookmarks/Groups').query.itemId, HEAT.replace(/-/g, ''));
    assert.equal(t.count.querySelector('.jfbm-page-all-videos').textContent, 'Show every video');
    assert.equal(t.content.querySelector('.jfbm-group[data-jfbm-group="' + HEAT.replace(/-/g, '') + '"]').classList.contains('jfbm-page-highlight'), true);
    assert.equal(dashed.context.location.hash, '#/jfbm-bookmarks?item=' + HEAT.replace(/-/g, ''), 'the group dropped once it landed, the item as the page keys it');
    assert.equal(dashed.errors.length, 0, dashed.errors.join('\n'));
});

test('the group is marked once: the hash drops it in place as it lands, and a change reloading the list neither marks nor scrolls to it again', { skip }, async () => {
    const page = await opened({ hash: '#/jfbm-bookmarks?group=' + GONE.replace(/-/g, '') });
    const length = page.context.history.length;
    const t = toolbar(page);
    const target = t.content.querySelector('.jfbm-group[data-jfbm-group="' + GONE.replace(/-/g, '') + '"]');
    assert.equal(target.classList.contains('jfbm-page-highlight'), true);
    assert.equal(page.scrolled.length, 1);
    assert.equal(page.scrolled[0].element, target);
    assert.deepEqual(plain(page.scrolled[0].options), { block: 'start' });
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks', 'group has one landing');
    assert.equal(page.context.history.length, length, 'rewritten in place');
    assert.equal(renders(page), 1);

    // A delete from a row of the marked group reads that group again: the group drawn again keeps the mark for
    // the rest of its time, nothing scrolls, and the mark goes with the others when the time is up.
    const before = requests(page, '/Bookmarks/Groups').length;
    page.respond('DELETE', '/Bookmarks/b3', null, 204);
    page.data.groups[1].Bookmarks = [];
    page.data.groups.splice(1, 1);
    page.data.flat.splice(2, 1);
    root(page).querySelector('.jfbm-row[data-id="b3"] button[title="Delete"]').click();
    await page.advance(20);
    panel(page, 'Delete bookmark?').querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Delete').click();
    await page.advance(300);
    assert.equal(requests(page, '/Bookmarks/Groups').length, before + 2, 'the group (itemId) and the count line');
    assert.equal(t.content.querySelector('.jfbm-group[data-jfbm-group="' + GONE.replace(/-/g, '') + '"]'), null, 'the emptied group is gone');
    assert.equal(t.content.querySelector('.jfbm-page-highlight'), null, 'with its mark');
    assert.equal(page.scrolled.length, 1, 'no second scroll');
    await page.advance(3000);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('after Clear filters the same term typed again is searched again, and Enter too (the search box\'s memory follows the field)', { skip }, async () => {
    const page = await opened();
    const search = toolbar(page).search;
    page.respond('GET', '/Bookmarks/Groups', { Items: [], TotalRecordCount: 0, StartIndex: 0 });
    page.respond('GET', '/Bookmarks/Query', { Items: [], TotalRecordCount: 0, StartIndex: 0 });
    search.focus();
    search.value = 'zzqx-nomatch';
    page.dispatch(search, 'input');
    await page.advance(350);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?q=zzqx-nomatch');
    const clear = () => {
        const notice = toolbar(page).content.querySelector('.jfbm-notice');
        assert.equal(notice.querySelector('.jfbm-notice-text').textContent, 'No bookmarks match these filters.');
        notice.querySelector('button').click();
    };
    clear();
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks');
    assert.equal(search.value, '');

    let before = requests(page, '/Bookmarks/Groups').length;
    search.value = 'zzqx-nomatch';
    page.dispatch(search, 'input');
    await page.advance(350);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?q=zzqx-nomatch', 'the same term is a search again');
    assert.equal(lastRequest(page, '/Bookmarks/Groups').query.searchTerm, 'zzqx-nomatch');
    assert.equal(requests(page, '/Bookmarks/Groups').length, before + 1);

    clear();
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks');
    before = requests(page, '/Bookmarks/Groups').length;
    search.value = 'zzqx-nomatch';
    page.key(search, 'Enter');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?q=zzqx-nomatch', 'and on Enter');
    assert.equal(requests(page, '/Bookmarks/Groups').length, before + 1);
    assert.equal(renders(page), 1);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the lists are lists (role list and listitem); the Filters button only expands and folds its row, with a filter on too', { skip }, async () => {
    const page = await opened({ hash: '#/jfbm-bookmarks?range=1' });
    let t = toolbar(page);
    assert.equal(t.content.getAttribute('role'), 'list');
    const groups = t.content.querySelectorAll('.jfbm-group');
    assert.deepEqual(groups.map((g) => g.getAttribute('role')), ['listitem', 'listitem', 'listitem']);
    assert.equal(groups[0].querySelector('.jfbm-group-header').hasAttribute('role'), false, 'the heading of the group\'s item, not an item');
    assert.equal(groups[0].querySelector('.jfbm-group-rows').getAttribute('role'), 'list');
    assert.deepEqual(groups[0].querySelectorAll('.jfbm-group-rows > .jfbm-row').map((row) => row.getAttribute('role')), ['listitem', 'listitem']);

    assert.equal(t.filtersButton.hasAttribute('aria-pressed'), false, 'aria-expanded alone');
    assert.equal(t.filtersButton.getAttribute('aria-expanded'), 'true', 'open: the hash has a filter on');
    assert.equal(t.filtersRow.classList.contains('hide'), false);
    assert.equal(t.filtersButton.textContent, 'Filters (1)');
    t.filtersButton.click();
    assert.equal(t.filtersButton.getAttribute('aria-expanded'), 'false', 'folds with the filter still on');
    assert.equal(t.filtersRow.classList.contains('hide'), true);
    assert.equal(t.filtersButton.textContent, 'Filters (1)');
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?range=1');
    t.filtersButton.click();
    assert.equal(t.filtersButton.getAttribute('aria-expanded'), 'true');
    tick(page, 'range');
    await page.advance(50);
    assert.equal(t.filtersButton.textContent, 'Filters');
    assert.equal(t.filtersButton.getAttribute('aria-expanded'), 'true', 'stays as the user left it');

    clickView(page, 'flat');
    await page.advance(50);
    t = toolbar(page);
    assert.equal(t.content.getAttribute('role'), 'list');
    const rows = t.content.querySelectorAll('.jfbm-row');
    assert.equal(rows.length, 4);
    assert.ok(rows.every((row) => row.parentNode === t.content && row.getAttribute('role') === 'listitem'), 'the rows are the list\'s items');

    page.respond('GET', '/Bookmarks/Groups', { Items: [], TotalRecordCount: 0, StartIndex: 0 });
    page.respond('GET', '/Bookmarks/Query', { Items: [], TotalRecordCount: 0, StartIndex: 0 });
    tick(page, 'unnamed');
    await page.advance(50);
    assert.equal(t.content.hasAttribute('role'), false, 'a notice alone is no list');
    assert.ok(t.content.querySelector('.jfbm-notice'));
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a clip\'s progress tick redraws its row in place (no reload of the pages shown); the clip\'s end reloads the list', { skip }, async () => {
    const data = fixtures();
    const running = (progress) => ({ Status: 'Running', Progress: progress, FileName: null, FileSize: null, Error: null });
    data.groups[0].Bookmarks[0].Clip = running(10);
    const page = await opened({ data });
    const t = toolbar(page);
    const row = () => t.content.querySelector('.jfbm-row[data-id="b1"]');
    const first = row();
    const holder = first.parentNode;
    assert.equal(first.querySelector('.jfbm-progress').getAttribute('aria-valuenow'), '10');
    const groupsBefore = requests(page, '/Bookmarks/Groups').length;

    page.respond('GET', '/Bookmarks/b1', Object.assign({}, data.flat[0], { Clip: running(55) }));
    await page.advance(2100);
    assert.equal(requests(page, '/Bookmarks/b1').length, 1, 'one poll');
    assert.equal(row().querySelector('.jfbm-progress').getAttribute('aria-valuenow'), '55');
    assert.notEqual(row(), first, 'the row drawn again');
    assert.equal(row().parentNode, holder, 'in its place');
    assert.equal(holder.childNodes.indexOf(row()), 0);
    assert.equal(t.content.querySelectorAll('.jfbm-row').length, 4);
    assert.equal(requests(page, '/Bookmarks/Groups').length, groupsBefore, 'no reload for a tick');

    const done = Object.assign({}, data.flat[0], { Clip: { Status: 'Completed', Progress: 100, FileName: 'Heat - Bank job.mp4', FileSize: 2048, Error: null } });
    data.groups[0].Bookmarks[0] = done;
    data.flat[0] = done;
    page.respond('GET', '/Bookmarks/b1', done);
    await page.advance(2400);
    assert.equal(requests(page, '/Bookmarks/b1').length, 2);
    assert.equal(requests(page, '/Bookmarks/Groups').length, groupsBefore + 2, 'the end reads the group (itemId) and the count line again');
    assert.equal(lastRequest(page, '/Bookmarks/Groups').query.itemId, undefined, 'the count line last');
    assert.equal(requests(page, '/Bookmarks/Groups')[groupsBefore].query.itemId, HEAT.replace(/-/g, ''), 'the group first');
    assert.ok(row().querySelector('.jfbm-clip-ready'));
    assert.ok(page.document.body.textContent.includes('Clip ready: Heat - Bank job.mp4'));
    assert.equal(renders(page), 1);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('paging: 50 rows (25 groups) a page, Load more appends the next and counts it in the hash in place; Back renders the shorter list again', { skip }, async () => {
    const flat = [];
    for (let i = 0; i < 60; i++) {
        flat.push(bookmark('f' + i, HEAT, 'Heat (1995)', i * 10));
    }

    const groups = [];
    for (let i = 0; i < 30; i++) {
        groups.push({ ItemId: 'dddd0000-0000-0000-0000-' + String(i).padStart(12, '0'), ItemName: 'Title ' + i, ItemType: 'Movie', ItemAvailable: true, Count: 2, LatestUpdatedAt: '2026-10-05T10:00:00.0000000Z', Bookmarks: [flat[i], flat[i + 30]], BookmarksTruncated: false });
    }

    const page = await opened({ hash: '#/jfbm-bookmarks?view=flat', data: { groups, flat } });
    let t = toolbar(page);
    assert.equal(t.content.querySelectorAll('.jfbm-row').length, 50);
    assert.equal(t.count.textContent, '60 bookmarks on 30 titles');
    assert.equal(t.pager.classList.contains('hide'), false);
    const more = t.pager.querySelector('button');
    assert.equal(more.textContent, 'Load more');
    const length = page.context.history.length;
    more.click();
    await page.advance(50);
    assert.deepEqual(lastRequest(page, '/Bookmarks/Query').query, { sortBy: 'UpdatedAt', startIndex: '50', limit: '50' });
    assert.equal(t.content.querySelectorAll('.jfbm-row').length, 60);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?view=flat&page=2');
    assert.equal(page.context.history.length, length, 'the page count replaces the entry');
    assert.equal(t.pager.classList.contains('hide'), true, 'no more');
    assert.equal(renders(page), 1);

    // Back to the entry before Load more: the kit renders the page from the hash, one page of rows.
    page.context.location.hash = '#/jfbm-bookmarks?view=flat';
    page.context.dispatchEvent(new page.Event('popstate'));
    await page.advance(120);
    assert.equal(renders(page), 2);
    t = toolbar(page);
    assert.equal(t.content.querySelectorAll('.jfbm-row').length, 50);
    assert.equal(t.pager.classList.contains('hide'), false);

    // The grouped view pages by groups.
    clickView(page, 'grouped');
    await page.advance(50);
    t = toolbar(page);
    assert.deepEqual(lastRequest(page, '/Bookmarks/Groups').query, { sortBy: 'LatestUpdated', startIndex: '0', limit: '25' });
    assert.equal(t.content.querySelectorAll('.jfbm-group').length, 25);
    t.pager.querySelector('button').click();
    await page.advance(50);
    assert.deepEqual(lastRequest(page, '/Bookmarks/Groups').query, { sortBy: 'LatestUpdated', startIndex: '25', limit: '25' });
    assert.equal(t.content.querySelectorAll('.jfbm-group').length, 30);
    assert.equal(page.context.location.hash, '#/jfbm-bookmarks?page=2');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('no bookmarks at all names the shortcut; a refused request shows its sentence with Retry; "Loading…" after 300 ms', { skip }, async () => {
    const empty = await opened({ data: { groups: [], flat: [] } });
    assert.equal(toolbar(empty).content.querySelector('.jfbm-notice-text').textContent, 'No bookmarks yet. Press Shift+B while watching, or use the bookmark button in the player.');
    assert.equal(toolbar(empty).count.textContent, '0 bookmarks on 0 titles');

    // An admin also reads where Jellyfin Enhanced's bookmarks are imported while some wait for it.
    const admin = await opened({ data: { groups: [], flat: [] }, capabilities: { IsAdmin: true, JellyfinEnhancedImportPending: true } });
    assert.equal(toolbar(admin).content.querySelector('.jfbm-notice-text').textContent,
        'No bookmarks yet. Press Shift+B while watching, or use the bookmark button in the player. Have bookmarks in Jellyfin Enhanced? Import them in the plugin settings.');
    const imported = await opened({ data: { groups: [], flat: [] }, capabilities: { IsAdmin: true, JellyfinEnhancedImportPending: false } });
    assert.equal(toolbar(imported).content.querySelector('.jfbm-notice-text').textContent, 'No bookmarks yet. Press Shift+B while watching, or use the bookmark button in the player.');

    const page = await start({ hash: '#/jfbm-bookmarks' });
    page.respond('GET', '/Bookmarks/Groups', { title: 'Unavailable', status: 503, detail: 'Bookmarks are read-only right now.' }, 503, { contentType: 'application/problem+json' });
    showFallbackPage(page);
    await page.advance(120);
    const t = toolbar(page);
    const notice = t.content.querySelector('.jfbm-notice-error');
    assert.equal(notice.getAttribute('role'), 'alert');
    assert.equal(notice.querySelector('.jfbm-notice-text').textContent, 'Bookmarks are read-only right now.');
    assert.equal(t.content.hasAttribute('aria-busy'), false);
    assert.equal(t.pager.classList.contains('hide'), true);
    page.respond('GET', '/Bookmarks/Groups', null, 200, { hang: true });
    notice.querySelector('button').click();
    await page.advance(100);
    assert.equal(t.content.getAttribute('aria-busy'), 'true');
    assert.equal(t.content.querySelector('.jfbm-notice'), null, 'no "Loading…" yet');
    await page.advance(250);
    assert.equal(t.content.querySelector('.jfbm-notice-info').textContent, 'Loading…');
    const data = fixtures();
    page.respond('GET', '/Bookmarks/Groups', { Items: data.groups, TotalRecordCount: 3, StartIndex: 0 });
    notice.querySelector('button').click();
    await page.advance(50);
    assert.equal(t.content.querySelectorAll('.jfbm-group').length, 3);
    assert.equal(t.content.querySelector('.jfbm-notice'), null);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a change made from a row (a delete) reads only that video\'s group again, with the page\'s filters, and the count line; the group is drawn in place', { skip }, async () => {
    const page = await opened({ hash: '#/jfbm-bookmarks?type=Movie' });
    const t = toolbar(page);
    const before = requests(page, '/Bookmarks/Groups').length;
    const queriesBefore = requests(page, '/Bookmarks/Query').length;
    const heatBefore = t.content.querySelector('.jfbm-group[data-jfbm-group="' + HEAT.replace(/-/g, '') + '"]');
    page.respond('DELETE', '/Bookmarks/b2', null, 204);
    page.data.groups[0].Bookmarks.splice(1, 1);
    page.data.groups[0].Count = 1;
    page.data.flat.splice(1, 1);
    root(page).querySelector('.jfbm-row[data-id="b2"] button[title="Delete"]').click();
    await page.advance(20);
    const dialog = panel(page, 'Delete bookmark?');
    dialog.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Delete').click();
    await page.advance(300);
    assert.equal(panel(page, 'Delete bookmark?'), null);
    const groupRequests = requests(page, '/Bookmarks/Groups').slice(before);
    assert.equal(groupRequests.length, 2, 'the group, then the count line; never the shown pages');
    assert.deepEqual(groupRequests[0].query, { sortBy: 'LatestUpdated', startIndex: '0', limit: '1', itemTypes: 'Movie', itemId: HEAT.replace(/-/g, '') }, 'the video\'s group with the page\'s filters');
    assert.deepEqual(groupRequests[1].query, { sortBy: 'LatestUpdated', startIndex: '0', limit: '1', itemTypes: 'Movie' }, 'the count line\'s total');
    assert.equal(requests(page, '/Bookmarks/Query').length, queriesBefore + 1, 'and the other view\'s total');
    const groups = t.content.querySelectorAll('.jfbm-group');
    assert.deepEqual(groups.map((g) => g.getAttribute('data-jfbm-group')), [HEAT, GONE, SHOW].map((id) => id.replace(/-/g, '')), 'the groups where they were');
    assert.notEqual(groups[0], heatBefore, 'drawn again');
    assert.deepEqual(groups[0].querySelectorAll('.jfbm-row').map((r) => r.getAttribute('data-id')), ['b1']);
    assert.equal(groups[0].querySelector('.listItemBodyText.secondary').textContent.indexOf('1 bookmark ·'), 0, 'the header follows');
    assert.equal(t.count.textContent, '3 bookmarks on 3 titles');
    assert.equal(renders(page), 1, 'by itself, not by a kit render');

    // A change of a video the page doesn't show reads the whole list again (where would its group go?): here a quick
    // bookmark made in the player (the shortcut's notifyChanged) on a fourth video.
    const all = requests(page, '/Bookmarks/Groups').length;
    const other = 'eeee0000-0000-0000-0000-000000000009';
    const made = bookmark('b9', other, 'Other (2010)', 9);
    page.data.groups.push({ ItemId: other, ItemName: 'Other (2010)', ItemType: 'Movie', ItemAvailable: true, Count: 1, LatestUpdatedAt: '2026-10-06T10:00:00.0000000Z', Bookmarks: [made], BookmarksTruncated: false });
    page.data.flat.unshift(made);
    page.respond('POST', '/Bookmarks', made, 201);
    const buttons = page.element('div', { class: 'buttons' }, [page.element('button', { class: 'btnSubtitles' })]);
    page.document.body.append(page.element('div', { class: 'videoPlayerContainer' }, [page.element('video')]), page.element('div', { id: 'videoOsdPage', class: 'page' }, [page.element('div', { class: 'videoOsdBottom' }, [buttons])]));
    await page.context.ApiClient.reportPlaybackStart({ ItemId: other, PositionTicks: 90000000, IsPaused: false });
    await page.advance(400);
    page.key(page.document.body, 'B', { shiftKey: true });
    await page.advance(300);
    assert.equal(requests(page, '/Bookmarks').filter((r) => r.method === 'POST').length, 1, 'the quick bookmark');
    const afterOther = requests(page, '/Bookmarks/Groups').slice(all);
    assert.ok(afterOther.length >= 1 && afterOther.every((r) => r.query.itemId === undefined), 'the whole list, not an unshown group');
    assert.equal(t.content.querySelectorAll('.jfbm-group').length, 4);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the focus: a row\'s Delete dialog takes it on Cancel; after the delete it goes to the video\'s row left, or to the next video\'s (the last one\'s: the previous); the bulk delete\'s dialog on Cancel, then to the first row left; the flat view\'s next row', { skip }, async () => {
    const page = await opened();
    const remove = (id, groupIndex) => {
        page.respond('DELETE', '/Bookmarks/' + id, null, 204);
        const group = page.data.groups[groupIndex];
        group.Bookmarks = group.Bookmarks.filter((b) => b.Id !== id);
        group.Count = group.Bookmarks.length;
        if (group.Count === 0) {
            page.data.groups.splice(groupIndex, 1);
        }

        page.data.flat = page.data.flat.filter((b) => b.Id !== id);
    };
    const deleteRow = async (id) => {
        const button = titled(row(page, id), 'Delete');
        button.focus();
        button.click();
        await page.advance(20);
        const dialog = panel(page, 'Delete bookmark?');
        assert.ok(page.document.activeElement === dialogButton(dialog, 'Cancel'), id + ': the focus in the dialog, on Cancel');
        dialogButton(dialog, 'Delete').click();
        await page.advance(300);
        assert.equal(panel(page, 'Delete bookmark?'), null);
    };

    // b2 of Heat's two: the group drawn again, the focus on the row left (keepFocus's rule).
    remove('b2', 0);
    await deleteRow('b2');
    assert.ok(row(page, 'b1').contains(page.document.activeElement), 'on Heat\'s row left');

    // b4, the last video's only bookmark: its group goes, the focus to the previous group's row.
    remove('b4', 2);
    await deleteRow('b4');
    assert.ok(page.document.activeElement !== page.document.body && row(page, 'b3').contains(page.document.activeElement), 'on Gone\'s row, not dropped to the page');

    // The bulk delete: Cancel first; everything selected gone, the bar hidden, the focus on the first row left.
    tickRow(page, 'b1');
    const bar = root(page).querySelector('.jfbm-bulk-bar');
    bar.querySelector('.jfbm-bulk-delete').focus();
    bar.querySelector('.jfbm-bulk-delete').click();
    await page.advance(20);
    const bulk = panel(page, 'Delete 1 bookmark?');
    assert.ok(page.document.activeElement === dialogButton(bulk, 'Cancel'), 'the bulk dialog\'s focus on Cancel');
    page.respond('POST', '/Bookmarks/BulkDelete', { Deleted: ['b1'], Tombstoned: [], NotFound: [], InUse: [], Failed: [] });
    page.data.groups.splice(0, 1);
    page.data.flat = page.data.flat.filter((b) => b.Id !== 'b1');
    dialogButton(bulk, 'Delete').click();
    await page.advance(300);
    assert.equal(panel(page, 'Delete 1 bookmark?'), null);
    assert.equal(bar.classList.contains('hide'), true);
    assert.equal(row(page, 'b1'), null);
    assert.ok(row(page, 'b3').contains(page.document.activeElement), 'on the first row left, not the hidden bar');

    // The flat view: the row deleted, the focus on the next one.
    const flat = await opened({ hash: '#/jfbm-bookmarks?view=flat' });
    flat.respond('DELETE', '/Bookmarks/b2', null, 204);
    flat.data.flat = flat.data.flat.filter((b) => b.Id !== 'b2');
    flat.data.groups[0].Bookmarks = flat.data.groups[0].Bookmarks.filter((b) => b.Id !== 'b2');
    const button = titled(row(flat, 'b2'), 'Delete');
    button.focus();
    button.click();
    await flat.advance(20);
    dialogButton(panel(flat, 'Delete bookmark?'), 'Delete').click();
    await flat.advance(300);
    assert.equal(row(flat, 'b2'), null);
    assert.ok(row(flat, 'b3').contains(flat.document.activeElement), 'the flat view\'s next row');
    assert.equal(page.errors.length + flat.errors.length, 0, page.errors.concat(flat.errors).join('\n'));
});

test('the player\'s list offers "All my bookmarks" while the capabilities offer the page, and the details section "See all"', { skip }, async () => {
    const page = await start();
    page.respond('GET', '/Bookmarks', [bookmark('b1', 'item1', 'Long Test Movie', 15)]);
    const buttons = page.element('div', { class: 'buttons' }, [page.element('button', { class: 'btnSubtitles' })]);
    const osd = page.element('div', { id: 'videoOsdPage', class: 'page' }, [page.element('div', { class: 'videoOsdBottom' }, [buttons])]);
    page.document.body.append(page.element('div', { class: 'videoPlayerContainer' }, [page.element('video')]), osd);
    await page.context.ApiClient.reportPlaybackProgress({ ItemId: 'item1', PositionTicks: 200000000 });
    await page.advance(400);
    page.document.querySelector('.btnBookmarkList').click();
    await page.advance(50);
    const list = panel(page, 'Bookmarks');
    const link = list.querySelector('.jfbm-list-toolbar a[data-jfbm-link="bookmarks"]');
    assert.equal(link.textContent, 'All my bookmarks');
    assert.equal(link.getAttribute('href'), '#/jfbm-bookmarks');
    link.click();
    await page.advance(10);
    assert.equal(panel(page, 'Bookmarks'), null, 'the dialog closes as the link navigates');

    const without = await start({ capabilities: { Pages: { Bookmarks: false, Clips: false } } });
    without.respond('GET', '/Bookmarks', [bookmark('b1', 'item1', 'Long Test Movie', 15)]);
    const buttons2 = without.element('div', { class: 'buttons' }, [without.element('button', { class: 'btnSubtitles' })]);
    without.document.body.append(without.element('div', { class: 'videoPlayerContainer' }, [without.element('video')]),
        without.element('div', { id: 'videoOsdPage', class: 'page' }, [without.element('div', { class: 'videoOsdBottom' }, [buttons2])]));
    await without.context.ApiClient.reportPlaybackProgress({ ItemId: 'item1', PositionTicks: 200000000 });
    await without.advance(400);
    without.document.querySelector('.btnBookmarkList').click();
    await without.advance(50);
    assert.equal(panel(without, 'Bookmarks').querySelector('a[data-jfbm-link]'), null);
    assert.equal(page.errors.length + without.errors.length, 0);
});

test('turned off on the server: no link, the route shows the server\'s note, and nothing is asked of the endpoints', { skip }, async () => {
    const page = await start({ head: { off: ['bookmarks', 'demo-a', 'demo-b'] }, hash: '#/jfbm-bookmarks', off: true });
    showFallbackPage(page);
    const container = drawer(page);
    await page.advance(400);
    const r = root(page);
    assert.equal(r.getAttribute('data-jfbm-state'), 'off');
    assert.equal(r.querySelector('.jfbm-page-note').textContent, 'My Bookmarks is turned off by your server admin.');
    assert.equal(r.querySelector('.jfbm-page-toolbar'), null);
    assert.equal(container.querySelector('.jfkit-nav'), null);
    assert.equal(requests(page, '/Bookmarks/Groups').length + requests(page, '/Bookmarks/Query').length, 0);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('in Plugin Pages\' host the page renders from the shell and keeps its state in that link', { skip }, async () => {
    const page = await start({ head: { mode: 'Auto' }, hash: '#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fbookmarks&view=flat' });
    const view = page.element('div', { class: 'page userPreferencesPage', 'data-role': 'page' });
    const shell = page.element('div', { class: 'jfbm-page', 'data-jfbm-page': 'bookmarks' }, [page.element('p', { class: 'jfbm-shell-note' }, ['This page needs the Bookmarks web UI, which is turned off or failed to load.'])]);
    view.append(shell);
    page.document.body.append(view);
    ['viewbeforeshow', 'pagebeforeshow', 'viewshow', 'pageshow'].forEach((type) => page.dispatch(view, type, { detail: { isRestored: false } }));
    await page.advance(120);
    assert.equal(view.getAttribute('data-title'), 'My Bookmarks');
    assert.equal(shell.getAttribute('data-jfbm-host'), 'PluginPages');
    const rows = shell.querySelectorAll('.jfbm-row');
    assert.equal(rows.length, 4);
    shell.querySelectorAll('[data-jfbm-type]').find((b) => b.getAttribute('data-jfbm-type') === 'Episode').click();
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fbookmarks&view=flat&type=Episode');
    assert.equal(lastRequest(page, '/Bookmarks/Query').query.itemTypes, 'Episode');
    assert.equal(renders(page), 1);

    // Back over the page's own entry: no page event from jellyfin-web in this host, so the kit renders from the hash.
    page.context.location.hash = '#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fbookmarks&view=flat';
    page.context.dispatchEvent(new page.Event('popstate'));
    await page.advance(120);
    assert.equal(renders(page), 2);
    assert.equal(lastRequest(page, '/Bookmarks/Query').query.itemTypes, undefined);
    assert.equal(shell.querySelector('[data-jfbm-type][aria-pressed="true"]').getAttribute('data-jfbm-type'), '');
    assert.equal(shell.querySelectorAll('.jfbm-row').length, 4);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('in Plugin Pages\' host the page left stays in its hidden view: a change made meanwhile reads nothing there; shown again, the page renders afresh', { skip }, async () => {
    const data = fixtures();
    const running = (progress) => ({ Status: 'Running', Progress: progress, FileName: null, FileSize: null, Error: null });
    data.groups[0].Bookmarks[0].Clip = running(10);
    const page = await start({ data, head: { mode: 'Auto' }, hash: '#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fbookmarks' });
    const view = page.element('div', { id: 'userPluginPreferencesPage', class: 'page libraryPage userPreferencesPage', 'data-role': 'page' });
    const shell = page.element('div', { class: 'jfbm-page', 'data-jfbm-page': 'bookmarks' }, [page.element('p', { class: 'jfbm-shell-note' }, ['This page needs the Bookmarks web UI, which is turned off or failed to load.'])]);
    view.append(shell);
    page.document.body.append(view);
    const show = (target, isRestored) => ['viewbeforeshow', 'pagebeforeshow', 'viewshow', 'pageshow'].forEach((type) => page.dispatch(target, type, { detail: { isRestored } }));
    show(view, false);
    await page.advance(120);
    assert.equal(shell.querySelectorAll('.jfbm-row').length, 4);

    // Another page: the host view kept, hidden. b1's clip, watched for its toast, ends meanwhile.
    view.classList.add('hide');
    page.context.location.hash = '#/home';
    const home = page.element('div', { id: 'indexPage', class: 'page homePage libraryPage', 'data-role': 'page' });
    page.document.body.append(home);
    show(home, false);
    const before = page.fetchLog.length;
    const done = Object.assign({}, data.flat[0], { Clip: { Status: 'Completed', Progress: 100, FileName: 'Heat - Bank job.mp4', FileSize: 2048, Error: null } });
    data.groups[0].Bookmarks[0] = done;
    data.flat[0] = done;
    page.respond('GET', '/Bookmarks/b1', done);
    await page.advance(5000);
    assert.ok(page.document.body.textContent.includes('Clip ready: Heat - Bank job.mp4'), 'the clip-ready toast still comes, wherever the user is');
    assert.deepEqual(page.fetchLog.slice(before).map((f) => f.path).filter((p) => p !== '/Bookmarks/b1'), [], 'no group, list or count read off the page');
    assert.equal(renders(page), 1);

    // Back: the view shown again and rendered by the kit, the list read afresh with the clip ready.
    home.classList.add('hide');
    view.classList.remove('hide');
    page.context.location.hash = '#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fbookmarks';
    show(view, true);
    await page.advance(120);
    assert.equal(renders(page), 2);
    assert.ok(shell.querySelector('.jfbm-row[data-id="b1"] .jfbm-clip-ready'));
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

// ----------------------------------------------------- the inline rename, the selection, Player options, the TV layout

function row(page, id) {
    return toolbar(page).content.querySelector('.jfbm-row[data-id="' + id + '"]');
}

function nameRequests(page) {
    return page.fetchLog.filter((f) => f.method === 'POST' && /^\/Bookmarks\/b\d+\/Name$/.test(f.path));
}

function toasts(page) {
    return page.document.querySelectorAll('.jfbm-toast').map((t) => t.textContent);
}

// A button by its title (the fake DOM's selectors take no space inside an attribute value).
function titled(scope, title) {
    return scope.querySelectorAll('button').find((b) => b.getAttribute('title') === title) || null;
}

// The page's body element (the kit's .jfbm-page-body inside the root), which carries the layout classes.
function pageBody(page) {
    return root(page).querySelector('.jfbm-page-body');
}

// A dialog's form submitted (what a click on its Save, a submit button, does in a browser; the fake DOM has no
// implicit submission).
function submit(page, dialog) {
    dialog.querySelector('form').dispatchEvent(new page.Event('submit', { cancelable: true }));
}

// The server renames: the fixture follows, so the page's re-read of the video shows the new name.
function respondRename(page, extra) {
    page.respond('POST', /^\/Bookmarks\/b\d+\/Name$/, (entry) => {
        const id = entry.path.split('/')[2];
        const found = page.data.flat.find((b) => b.Id === id);
        const named = Object.assign({}, found, { Name: entry.body.Name, UpdatedAt: '2026-10-06T12:00:00.0000000Z' }, extra || {});
        page.data.flat = page.data.flat.map((b) => (b.Id === id ? named : b));
        page.data.groups.forEach((g) => {
            g.Bookmarks = g.Bookmarks.map((b) => (b.Id === id ? named : b));
        });
        return named;
    });
}

test('inline rename: Name it turns the title into a field; Enter saves through the Name endpoint with IfUpdatedAt and the video\'s group is drawn again; Rename on a named row; Escape, a TV Back key and Cancel restore the title; an empty name on a named row unnames it', { skip }, async () => {
    const page = await opened();
    const t = toolbar(page);
    respondRename(page);
    const groupsBefore = requests(page, '/Bookmarks/Groups').length;

    // Name it (an unnamed point bookmark): the field, empty, focused; Enter saves.
    row(page, 'b2').querySelector('.jfbm-name-it').click();
    let form = row(page, 'b2').querySelector('.jfbm-rename');
    assert.ok(form, 'the title line became the rename form');
    let input = form.querySelector('input');
    assert.equal(input.value, '');
    assert.equal(input.getAttribute('maxlength'), '100');
    assert.equal(input.getAttribute('aria-label'), 'Name');
    assert.equal(page.document.activeElement, input);
    assert.deepEqual(form.querySelectorAll('button').map((b) => b.getAttribute('title')), ['Save the name', 'Cancel renaming']);
    assert.equal(row(page, 'b2').querySelector('.jfbm-row-title'), null, 'no title while renaming');
    assert.equal(row(page, 'b2').classList.contains('jfbm-row-renaming'), true);
    input.value = 'Bank vault';
    assert.equal(page.key(input, 'Enter'), false, 'Enter is the rename\'s (prevented)');
    await page.advance(10);
    assert.equal(nameRequests(page).length, 1);
    assert.deepEqual(nameRequests(page)[0].body, { Name: 'Bank vault', IfUpdatedAt: '2026-10-05T10:00:00.0000000Z' }, 'the UpdatedAt the row was read with');
    assert.equal(input.readOnly, true, 'busy while it saves (read-only, never disabled: a browser blurs a disabled field)');
    assert.equal(input.getAttribute('aria-busy'), 'true');
    await page.advance(300);
    assert.equal(row(page, 'b2').querySelector('.jfbm-row-title').textContent, 'Bank vault', 'the row drawn again from the server\'s answer');
    assert.equal(row(page, 'b2').querySelector('.jfbm-name-it'), null);
    assert.equal(row(page, 'b2').querySelector('button[title="Rename"]') !== null, true, 'a named row offers Rename');
    assert.equal(requests(page, '/Bookmarks/Groups').length, groupsBefore + 2, 'the group (itemId) and the count line, not the pages');
    assert.equal(requests(page, '/Bookmarks/Groups')[groupsBefore].query.itemId, HEAT.replace(/-/g, ''));
    assert.deepEqual(toasts(page), ['Named "Bank vault".']);
    assert.equal(page.document.activeElement, row(page, 'b2').querySelector('button[title="Rename"]'), 'the focus stays with the row');
    assert.equal(renders(page), 1);

    // Rename on a named row: the name, selected; Escape cancels without a request and the title comes back.
    row(page, 'b1').querySelector('button[title="Rename"]').click();
    form = row(page, 'b1').querySelector('.jfbm-rename');
    input = form.querySelector('input');
    assert.equal(input.value, 'Bank job');
    assert.deepEqual([input.selectionStart, input.selectionEnd], [0, 8], 'selected, so typing replaces it');
    input.value = 'Something else';
    assert.equal(page.key(input, 'Escape'), false);
    assert.equal(row(page, 'b1').querySelector('.jfbm-rename'), null);
    assert.equal(row(page, 'b1').querySelector('.jfbm-row-title').textContent, 'Bank job', 'the title line as it was');
    assert.equal(row(page, 'b1').querySelector('.jfbm-row-subtitle').textContent, 'Watch the reflection', 'with its note line');
    assert.equal(nameRequests(page).length, 1, 'nothing sent');
    assert.equal(page.document.activeElement, row(page, 'b1').querySelector('button[title="Rename"]'));

    // A TV remote's Back key cancels too (keyCode 10009, Tizen), and the Cancel button.
    row(page, 'b1').querySelector('button[title="Rename"]').click();
    assert.equal(page.key(row(page, 'b1').querySelector('.jfbm-rename input'), 'Unidentified', { keyCode: 10009 }), false);
    assert.equal(row(page, 'b1').querySelector('.jfbm-rename'), null);
    row(page, 'b1').querySelector('button[title="Rename"]').click();
    titled(row(page, 'b1'), 'Cancel renaming').click();
    assert.equal(row(page, 'b1').querySelector('.jfbm-rename'), null);
    assert.equal(nameRequests(page).length, 1);

    // The Save button; an empty name on a named row unnames it (Name null), as the editor's empty field does.
    row(page, 'b1').querySelector('button[title="Rename"]').click();
    row(page, 'b1').querySelector('.jfbm-rename input').value = '   ';
    titled(row(page, 'b1'), 'Save the name').click();
    await page.advance(300);
    assert.deepEqual(nameRequests(page)[1].body, { Name: null, IfUpdatedAt: '2026-10-05T10:00:00.0000000Z' });
    assert.equal(row(page, 'b1').querySelector('.jfbm-row-title').textContent, 'Watch the reflection', 'the note is the title now');
    assert.ok(row(page, 'b1').querySelector('.jfbm-name-it'), 'and Name it is back');
    assert.deepEqual(toasts(page).slice(-1), ['Name removed.']);

    // An empty name on an unnamed row is a Cancel: nothing sent.
    row(page, 'b1').querySelector('.jfbm-name-it').click();
    page.key(row(page, 'b1').querySelector('.jfbm-rename input'), 'Enter');
    await page.advance(50);
    assert.equal(nameRequests(page).length, 2);
    assert.equal(row(page, 'b1').querySelector('.jfbm-rename'), null);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('inline rename: a 409 reads the bookmark again and shows the name it has now, selected, under "Changed elsewhere"; the next Save carries the latest UpdatedAt; one deleted meanwhile ends the rename with a toast; another refusal shows its sentence', { skip }, async () => {
    const page = await opened();
    const latest = Object.assign({}, page.data.flat[0], { Name: 'Heist', UpdatedAt: '2026-10-06T11:00:00.0000000Z' });
    page.respond('POST', '/Bookmarks/b1/Name', { title: 'Conflict', status: 409, detail: 'Changed elsewhere. Reload to see the latest.' }, 409, { contentType: 'application/problem+json' });
    page.respond('GET', '/Bookmarks/b1', latest);
    row(page, 'b1').querySelector('button[title="Rename"]').click();
    const input = row(page, 'b1').querySelector('.jfbm-rename input');
    input.value = 'Mine';
    page.key(input, 'Enter');
    await page.advance(50);
    assert.equal(requests(page, '/Bookmarks/b1').length, 1, 'read again after the 409');
    assert.equal(input.value, 'Heist', 'the name it has now');
    assert.deepEqual([input.selectionStart, input.selectionEnd], [0, 5], 'selected');
    assert.equal(input.disabled, false);
    assert.equal(row(page, 'b1').querySelector('.jfbm-rename-error').textContent, 'Changed elsewhere: it is named "Heist" now. Save replaces that.');
    assert.equal(page.document.activeElement, input);

    respondRename(page);
    input.value = 'Mine after all';
    page.key(input, 'Enter');
    await page.advance(300);
    const sent = nameRequests(page);
    assert.equal(sent.length, 2);
    assert.deepEqual(sent[1].body, { Name: 'Mine after all', IfUpdatedAt: '2026-10-06T11:00:00.0000000Z' }, 'the latest UpdatedAt');
    assert.equal(row(page, 'b1').querySelector('.jfbm-row-title').textContent, 'Mine after all');

    // Unnamed meanwhile: the sentence says so.
    page.respond('POST', '/Bookmarks/b2/Name', { title: 'Conflict', status: 409, detail: 'Changed elsewhere.' }, 409, { contentType: 'application/problem+json' });
    page.respond('GET', '/Bookmarks/b2', Object.assign({}, page.data.flat[1], { Name: null, UpdatedAt: '2026-10-06T11:30:00.0000000Z' }));
    row(page, 'b2').querySelector('.jfbm-name-it').click();
    row(page, 'b2').querySelector('.jfbm-rename input').value = 'x';
    page.key(row(page, 'b2').querySelector('.jfbm-rename input'), 'Enter');
    await page.advance(50);
    assert.equal(row(page, 'b2').querySelector('.jfbm-rename-error').textContent, 'Changed elsewhere: it has no name now. Save names it.');
    page.key(row(page, 'b2').querySelector('.jfbm-rename input'), 'Escape');

    // Deleted meanwhile: the re-read is a 404; the rename ends, the toast, and the video's group is read again.
    const groupsBefore = requests(page, '/Bookmarks/Groups').length;
    page.respond('POST', '/Bookmarks/b4/Name', { title: 'Conflict', status: 409, detail: 'Changed elsewhere.' }, 409, { contentType: 'application/problem+json' });
    page.respond('GET', '/Bookmarks/b4', { title: 'Not found', status: 404, detail: 'No such bookmark.' }, 404, { contentType: 'application/problem+json' });
    page.data.groups.splice(2, 1);
    page.data.flat.splice(3, 1);
    row(page, 'b4').querySelector('.jfbm-name-it').click();
    row(page, 'b4').querySelector('.jfbm-rename input').value = 'Gone';
    page.key(row(page, 'b4').querySelector('.jfbm-rename input'), 'Enter');
    await page.advance(300);
    assert.deepEqual(toasts(page).slice(-1), ['This bookmark was deleted elsewhere.']);
    assert.equal(row(page, 'b4'), null, 'its group gone with it');
    assert.equal(requests(page, '/Bookmarks/Groups').length, groupsBefore + 2);
    assert.equal(toolbar(page).count.textContent, '3 bookmarks on 2 titles');

    // Another refusal (a 400, say) shows its sentence in the row and keeps the field.
    page.respond('POST', '/Bookmarks/b1/Name', { title: 'Bad request', status: 400, detail: 'Names can be at most 100 characters long.' }, 400, { contentType: 'application/problem+json' });
    row(page, 'b1').querySelector('button[title="Rename"]').click();
    row(page, 'b1').querySelector('.jfbm-rename input').value = 'too long, says the server';
    page.key(row(page, 'b1').querySelector('.jfbm-rename input'), 'Enter');
    await page.advance(50);
    assert.equal(row(page, 'b1').querySelector('.jfbm-rename-error').textContent, 'Names can be at most 100 characters long.');
    assert.equal(row(page, 'b1').querySelector('.jfbm-rename input').value, 'too long, says the server');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the flat view: a rename reads only that video\'s rows again (the Query endpoint with itemId) and draws them in place; a row that no longer matches a filter is removed; one that now matches reads the whole list', { skip }, async () => {
    // The fixture's Query ignores the filters, so only the unnamed ones are given to it: what the filter would show.
    const data = fixtures();
    data.flat = data.flat.filter((b) => !b.Name);
    const page = await opened({ hash: '#/jfbm-bookmarks?view=flat&unnamed=1', data });
    const t = toolbar(page);
    const queriesBefore = requests(page, '/Bookmarks/Query').length;
    const groupsBefore = requests(page, '/Bookmarks/Groups').length;
    respondRename(page);
    // Naming b2 takes it out of "Needs a name": the server's answer for the video no longer lists it.
    page.respond('GET', '/Bookmarks/Query', (entry) => {
        const key = (id) => String(id).replace(/-/g, '').toLowerCase();
        const items = page.data.flat.filter((b) => !b.Name && (!entry.query.itemId || key(b.ItemId) === key(entry.query.itemId)));
        return { Items: items, TotalRecordCount: items.length, StartIndex: 0 };
    });
    const rowsBefore = t.content.querySelectorAll('.jfbm-row').map((r) => r.getAttribute('data-id'));
    row(page, 'b2').querySelector('.jfbm-name-it').click();
    row(page, 'b2').querySelector('.jfbm-rename input').value = 'Named now';
    page.key(row(page, 'b2').querySelector('.jfbm-rename input'), 'Enter');
    await page.advance(300);
    const queries = requests(page, '/Bookmarks/Query').slice(queriesBefore);
    assert.equal(queries.length, 2, 'the video\'s rows, then the count line');
    assert.deepEqual(queries[0].query, { sortBy: 'UpdatedAt', startIndex: '0', limit: '200', named: 'false', itemId: HEAT.replace(/-/g, '') }, 'with the page\'s filters');
    assert.equal(requests(page, '/Bookmarks/Groups').length, groupsBefore + 1, 'the other view\'s total');
    assert.equal(row(page, 'b2'), null, 'no longer matching, removed');
    assert.deepEqual(t.content.querySelectorAll('.jfbm-row').map((r) => r.getAttribute('data-id')), rowsBefore.filter((id) => id !== 'b2'));
    assert.equal(renders(page), 1);

    // A bookmark of a shown video the page didn't show matches now (one unnamed meanwhile, beyond the page): the
    // video's answer holds it, and only the whole list knows where it goes.
    page.data.flat.push(bookmark('b6', SHOW, 'Show S01E02 - Pilot', 300));
    page.respond('POST', '/Bookmarks/b4/Name', (entry) => Object.assign({}, page.data.flat.find((b) => b.Id === 'b4'), { Name: entry.body.Name }));
    const before = requests(page, '/Bookmarks/Query').length;
    row(page, 'b4').querySelector('.jfbm-name-it').click();
    row(page, 'b4').querySelector('.jfbm-rename input').value = 'Pilot';
    page.data.flat = page.data.flat.map((b) => (b.Id === 'b4' ? Object.assign({}, b, { Name: 'Pilot' }) : b));
    page.key(row(page, 'b4').querySelector('.jfbm-rename input'), 'Enter');
    await page.advance(300);
    const after = requests(page, '/Bookmarks/Query').slice(before);
    assert.ok(after.length >= 2 && after[0].query.itemId === SHOW.replace(/-/g, '') && after.slice(1).some((q) => q.query.itemId === undefined && q.query.limit === '50'), 'the video first, then the whole list');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('bulk select: a checkbox per row, the bar with the count, Select all and Clear; Delete… asks with "Also delete their clips" as the single delete does and posts BulkDelete; a clip file in use keeps its bookmark selected with the sentence', { skip }, async () => {
    const data = fixtures();
    data.groups[0].Bookmarks[0].Clip = { Status: 'Completed', FileName: 'Heat - Bank job.mp4', FileSize: 2048 };
    data.flat[0] = data.groups[0].Bookmarks[0];
    const page = await opened({ data });
    const t = toolbar(page);
    const bar = root(page).querySelector('.jfbm-bulk-bar');
    assert.equal(bar.getAttribute('role'), 'toolbar');
    assert.equal(bar.classList.contains('hide'), true, 'hidden while nothing is selected');
    const boxes = t.content.querySelectorAll('.jfbm-row-select-box');
    assert.equal(boxes.length, 4, 'one per row');
    assert.deepEqual(boxes.slice(0, 2).map((b) => b.getAttribute('aria-label')), ['Select Bank job', 'Select Bookmark at 1:30:00']);
    assert.ok(boxes.every((b) => b.closest('.jfbm-row-select')), 'in a 40 px label');
    assert.equal(pageBody(page).classList.contains('jfbm-page-tv'), false);
    assert.equal(root(page).querySelector('.jfbm-page-select-button'), null, 'no Select toggle off the TV layout');

    boxes[0].checked = true;
    page.dispatch(boxes[0], 'change');
    assert.equal(bar.classList.contains('hide'), false);
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '1 bookmark selected');
    bar.querySelector('.jfbm-bulk-all').click();
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '4 bookmarks selected');
    assert.ok(t.content.querySelectorAll('.jfbm-row-select-box').every((b) => b.checked));
    bar.querySelector('.jfbm-bulk-clear').click();
    assert.equal(bar.classList.contains('hide'), true);
    assert.ok(t.content.querySelectorAll('.jfbm-row-select-box').every((b) => !b.checked));

    // Two selected, one with a clip file: the dialog, the choice ticked (1.1.0) with the hint when unticked.
    for (const id of ['b1', 'b3']) {
        const box = row(page, id).querySelector('.jfbm-row-select-box');
        box.checked = true;
        page.dispatch(box, 'change');
    }

    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '2 bookmarks selected');
    bar.querySelector('.jfbm-bulk-delete').click();
    await page.advance(20);
    const dialog = panel(page, 'Delete 2 bookmarks?');
    assert.ok(dialog);
    assert.equal(dialog.querySelector('.jfbm-bulk-description').textContent, '2 bookmarks on 2 titles will be deleted.');
    const choice = dialog.querySelector('.jfbm-delete-clip input');
    assert.equal(choice.checked, true, 'ticked in 1.1.0');
    assert.equal(choice.nextSibling.textContent, 'Also delete their clips (1 clip file)');
    assert.equal(dialog.querySelector('.jfbm-delete-hint').classList.contains('hide'), true);
    choice.checked = false;
    page.dispatch(choice, 'change');
    assert.equal(dialog.querySelector('.jfbm-delete-hint').classList.contains('hide'), false, 'the next-update hint');

    // In use: b1's clip file is being played; b3 goes.
    page.respond('POST', '/Bookmarks/BulkDelete', { Deleted: ['b3'], Tombstoned: [], NotFound: [], InUse: ['b1'], Failed: [] });
    page.data.groups.splice(1, 1);
    page.data.flat.splice(2, 1);
    const groupsBefore = requests(page, '/Bookmarks/Groups').length;
    dialog.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Delete').click();
    await page.advance(300);
    const sent = page.fetchLog.filter((f) => f.path === '/Bookmarks/BulkDelete');
    assert.equal(sent.length, 1);
    assert.deepEqual(sent[0].body, { Ids: ['b1', 'b3'], DeleteClips: false });
    assert.ok(panel(page, 'Delete 2 bookmarks?'), 'the dialog stays for the one that is in use');
    assert.equal(dialog.querySelector('.jfbm-error').textContent, '1 bookmark deleted. One could not be deleted: a clip file is in use (being played or downloaded). Try again shortly.');
    assert.equal(dialog.querySelector('.jfbm-bulk-description').textContent, '"Bank job" will be deleted.');
    assert.equal(row(page, 'b3'), null, 'b3\'s group read again and gone');
    assert.equal(requests(page, '/Bookmarks/Groups').length, groupsBefore + 2);
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '1 bookmark selected', 'b1 stays selected');
    assert.equal(row(page, 'b1').querySelector('.jfbm-row-select-box').checked, true);

    // Try again: the clip file free now; everything deleted, the toast, the dialog closed, the selection empty.
    page.respond('POST', '/Bookmarks/BulkDelete', { Deleted: ['b1'], Tombstoned: [], NotFound: [], InUse: [], Failed: [] });
    page.data.groups[0].Bookmarks.splice(0, 1);
    page.data.groups[0].Count = 1;
    page.data.flat.splice(0, 1);
    dialog.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Delete').click();
    await page.advance(300);
    assert.equal(panel(page, 'Delete 2 bookmarks?'), null);
    assert.deepEqual(toasts(page).slice(-1), ['Bookmark deleted.']);
    assert.equal(bar.classList.contains('hide'), true);
    assert.equal(row(page, 'b1'), null);
    assert.equal(t.count.textContent, '2 bookmarks on 2 titles');
    assert.equal(renders(page), 1);

    // Without a clip file among the selected: no choice, and the default (delete the clips) is sent all the same.
    const box = row(page, 'b2').querySelector('.jfbm-row-select-box');
    box.checked = true;
    page.dispatch(box, 'change');
    bar.querySelector('.jfbm-bulk-delete').click();
    await page.advance(20);
    const single = panel(page, 'Delete 1 bookmark?');
    assert.equal(single.querySelector('.jfbm-delete-clip'), null);
    assert.equal(single.querySelector('.jfbm-bulk-description').textContent, '"Bookmark at 1:30:00" will be deleted.');
    page.respond('POST', '/Bookmarks/BulkDelete', { Deleted: ['b2'], Tombstoned: [], NotFound: [], InUse: [], Failed: [] });
    single.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Delete').click();
    await page.advance(300);
    assert.deepEqual(page.fetchLog.filter((f) => f.path === '/Bookmarks/BulkDelete').slice(-1)[0].body, { Ids: ['b2'], DeleteClips: true });
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

function tickRow(page, id) {
    const box = row(page, id).querySelector('.jfbm-row-select-box');
    box.checked = true;
    page.dispatch(box, 'change');
}

function dialogButton(dialog, label) {
    return dialog.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === label);
}

test('a video drawn again keeps its rows\' selection: a sibling\'s rename keeps a ticked row ticked, and after a partial bulk delete the InUse bookmark of the same video stays selected; Failed is named apart from InUse; a refused batch keeps its bookmarks selected with the sentence; a ticked bookmark whose clip finishes is offered for deletion with its clip file', { skip }, async () => {
    const data = fixtures();
    data.groups[0].Bookmarks[0].Clip = { Status: 'Completed', FileName: 'Heat - Bank job.mp4', FileSize: 2048 };
    data.flat[0] = data.groups[0].Bookmarks[0];
    const page = await opened({ data });
    const bar = root(page).querySelector('.jfbm-bulk-bar');
    respondRename(page);

    // b2 ticked, b1 renamed: Heat's group is drawn again, with b2 still ticked (it used to lose its tick with the group).
    tickRow(page, 'b2');
    row(page, 'b1').querySelector('button[title="Rename"]').click();
    row(page, 'b1').querySelector('.jfbm-rename input').value = 'Heist';
    page.key(row(page, 'b1').querySelector('.jfbm-rename input'), 'Enter');
    await page.advance(300);
    assert.equal(row(page, 'b1').querySelector('.jfbm-row-title').textContent, 'Heist', 'the group drawn again');
    assert.equal(row(page, 'b2').querySelector('.jfbm-row-select-box').checked, true, 'the sibling keeps its tick');
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '1 bookmark selected');
    assert.equal(bar.classList.contains('hide'), false);

    // Both of Heat ticked; the server deletes b2 and keeps b1 (its clip file in use): the group is drawn again without
    // b2, and b1 is still ticked in it, the bar still up, the dialog going on with it.
    tickRow(page, 'b1');
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '2 bookmarks selected');
    bar.querySelector('.jfbm-bulk-delete').click();
    await page.advance(20);
    const dialog = panel(page, 'Delete 2 bookmarks?');
    page.respond('POST', '/Bookmarks/BulkDelete', { Deleted: ['b2'], Tombstoned: [], NotFound: [], InUse: ['b1'], Failed: [] });
    page.data.groups[0].Bookmarks.splice(1, 1);
    page.data.groups[0].Count = 1;
    page.data.flat.splice(1, 1);
    dialogButton(dialog, 'Delete').click();
    await page.advance(300);
    assert.ok(panel(page, 'Delete 2 bookmarks?'), 'the dialog stays for the one in use');
    assert.equal(dialog.querySelector('.jfbm-error').textContent, '1 bookmark deleted. One could not be deleted: a clip file is in use (being played or downloaded). Try again shortly.');
    assert.equal(dialog.querySelector('.jfbm-bulk-description').textContent, '"Heist" will be deleted.');
    assert.equal(row(page, 'b2'), null, 'b2 gone with the group drawn again');
    assert.equal(row(page, 'b1').querySelector('.jfbm-row-select-box').checked, true, 'b1 ticked in the group drawn again');
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '1 bookmark selected');
    assert.equal(bar.classList.contains('hide'), false, 'the bar still up');
    dialogButton(dialog, 'Cancel').click();
    await page.advance(10);

    // b1 in use and b4 whose clip file couldn't be removed: each named with its own sentence, both still selected.
    tickRow(page, 'b4');
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '2 bookmarks selected');
    bar.querySelector('.jfbm-bulk-delete').click();
    await page.advance(20);
    const second = panel(page, 'Delete 2 bookmarks?');
    page.respond('POST', '/Bookmarks/BulkDelete', { Deleted: [], Tombstoned: [], NotFound: [], InUse: ['b1'], Failed: ['b4'] });
    dialogButton(second, 'Delete').click();
    await page.advance(300);
    assert.equal(second.querySelector('.jfbm-error').textContent,
        'One could not be deleted: a clip file is in use (being played or downloaded). Try again shortly. '
        + 'One could not be deleted: the clip file couldn\'t be removed (its folder isn\'t available or isn\'t recognised as its clips\' own, or isn\'t in the server\'s clip folders).');
    assert.equal(second.querySelector('.jfbm-bulk-description').textContent, '2 bookmarks on 2 titles will be deleted.');
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '2 bookmarks selected');
    assert.ok(row(page, 'b1').querySelector('.jfbm-row-select-box').checked && row(page, 'b4').querySelector('.jfbm-row-select-box').checked);

    // A refused request (the data read-only meanwhile): nothing deleted, both still selected, the server's sentence.
    page.respond('POST', '/Bookmarks/BulkDelete', { title: 'Unavailable', status: 503, detail: 'Bookmarks are read-only right now.' }, 503, { contentType: 'application/problem+json' });
    dialogButton(second, 'Delete').click();
    await page.advance(300);
    assert.ok(panel(page, 'Delete 2 bookmarks?'));
    assert.equal(second.querySelector('.jfbm-error').textContent, '2 could not be deleted. Bookmarks are read-only right now.');
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '2 bookmarks selected');
    assert.equal(dialogButton(second, 'Delete').disabled, false, 'Delete can be tried again');

    // A ticked bookmark whose clip finishes afterwards: the group drawn again for the clip's end refreshes the
    // selection's copy of it, so Delete… offers its clip file (once held as it was ticked,
    // the dialog offered no choice and 1.1.0's default deleted the clip file all the same).
    const clipData = fixtures();
    clipData.groups[0].Bookmarks[1].Clip = { Status: 'Running', Progress: 40, FileName: null, FileSize: null, Error: null };
    clipData.flat[1] = clipData.groups[0].Bookmarks[1];
    const clipping = await opened({ data: clipData });
    const clipBar = root(clipping).querySelector('.jfbm-bulk-bar');
    tickRow(clipping, 'b2');
    const ticked = row(clipping, 'b2');
    const done = Object.assign({}, clipData.groups[0].Bookmarks[1], { Clip: { Status: 'Completed', Progress: 100, FileName: 'Heat - 1.30.00.mp4', FileSize: 4096, Error: null } });
    clipData.groups[0].Bookmarks[1] = done;
    clipData.flat[1] = done;
    clipping.respond('GET', '/Bookmarks/b2', done);
    await clipping.advance(2400);
    assert.ok(row(clipping, 'b2') !== ticked, 'the group drawn again for the clip\'s end');
    assert.ok(row(clipping, 'b2').querySelector('.jfbm-clip-ready'));
    assert.equal(row(clipping, 'b2').querySelector('.jfbm-row-select-box').checked, true, 'still ticked');
    clipBar.querySelector('.jfbm-bulk-delete').click();
    await clipping.advance(20);
    const clipDialog = panel(clipping, 'Delete 1 bookmark?');
    const clipChoice = clipDialog.querySelector('.jfbm-delete-clip input');
    assert.ok(clipChoice, 'the clip choice offered for the clip that finished after the tick');
    assert.equal(clipChoice.nextSibling.textContent, 'Also delete their clips (1 clip file)');
    clipChoice.checked = false;
    clipping.dispatch(clipChoice, 'change');
    clipping.respond('POST', '/Bookmarks/BulkDelete', { Deleted: ['b2'], Tombstoned: [], NotFound: [], InUse: [], Failed: [] });
    dialogButton(clipDialog, 'Delete').click();
    await clipping.advance(300);
    assert.deepEqual(clipping.fetchLog.filter((f) => f.path === '/Bookmarks/BulkDelete').slice(-1)[0].body, { Ids: ['b2'], DeleteClips: false }, 'the clip file kept as chosen');
    assert.equal(page.errors.length + clipping.errors.length, 0, [page.errors, clipping.errors].join('\n'));
});

function manyBookmarks(count) {
    const flat = [];
    for (let i = 0; i < count; i++) {
        flat.push(bookmark('m' + i, HEAT, 'Heat (1995)', i * 10));
    }

    const groups = [{ ItemId: HEAT, ItemName: 'Heat (1995)', ItemType: 'Movie', Year: 1995, ItemAvailable: true, Count: count, LatestUpdatedAt: '2026-10-05T10:00:00.0000000Z', Bookmarks: flat.slice(0, 100), BookmarksTruncated: true }];
    return { groups, flat };
}

test('Select all over more than 200 rows: the delete goes in batches of 200 with the progress in the dialog and the answers merged; a bookmark the last batch keeps stays selected with the merged count', { skip }, async () => {
    const page = await opened({ hash: '#/jfbm-bookmarks?view=flat&page=5', data: manyBookmarks(205) });
    const t = toolbar(page);
    assert.equal(t.content.querySelectorAll('.jfbm-row').length, 205);
    const bar = root(page).querySelector('.jfbm-bulk-bar');
    tickRow(page, 'm0');
    bar.querySelector('.jfbm-bulk-all').click();
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '205 bookmarks selected');
    bar.querySelector('.jfbm-bulk-delete').click();
    await page.advance(20);
    const dialog = panel(page, 'Delete 205 bookmarks?');
    assert.equal(dialog.querySelector('.jfbm-bulk-description').textContent, '205 bookmarks on 1 title will be deleted.');
    assert.equal(dialog.querySelector('.jfbm-bulk-progress').textContent, '');
    const progress = [];
    page.respond('POST', '/Bookmarks/BulkDelete', (entry) => {
        progress.push(dialog.querySelector('.jfbm-bulk-progress').textContent);
        const ids = entry.body.Ids;
        page.data.flat = page.data.flat.filter((b) => !ids.includes(b.Id));
        return { Deleted: ids, Tombstoned: [], NotFound: [], InUse: [], Failed: [] };
    });
    dialogButton(dialog, 'Delete').click();
    await page.advance(500);
    const sent = page.fetchLog.filter((f) => f.path === '/Bookmarks/BulkDelete');
    assert.deepEqual(sent.map((f) => f.body.Ids.length), [200, 5], 'two requests, the server\'s most then the rest');
    assert.deepEqual(sent[0].body.Ids.slice(0, 3), ['m0', 'm1', 'm2']);
    assert.deepEqual(sent[1].body.Ids, ['m200', 'm201', 'm202', 'm203', 'm204']);
    assert.ok(sent.every((f) => f.body.DeleteClips === true));
    assert.deepEqual(progress, ['Deleting 1–200 of 205…', 'Deleting 201–205 of 205…'], 'the progress line as each batch was sent');
    assert.equal(panel(page, 'Delete 205 bookmarks?'), null);
    assert.deepEqual(toasts(page).slice(-1), ['205 bookmarks deleted.']);
    assert.equal(t.content.querySelectorAll('.jfbm-row').length, 0);
    assert.ok(t.content.querySelector('.jfbm-notice'), 'the empty state');
    assert.equal(bar.classList.contains('hide'), true);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));

    // The answers merged: the last batch keeps one (in use); the dialog counts across the batches.
    const again = await opened({ hash: '#/jfbm-bookmarks?view=flat&page=5', data: manyBookmarks(205) });
    const barAgain = root(again).querySelector('.jfbm-bulk-bar');
    tickRow(again, 'm0');
    barAgain.querySelector('.jfbm-bulk-all').click();
    barAgain.querySelector('.jfbm-bulk-delete').click();
    await again.advance(20);
    const dialogAgain = panel(again, 'Delete 205 bookmarks?');
    again.respond('POST', '/Bookmarks/BulkDelete', (entry) => {
        const ids = entry.body.Ids.filter((id) => id !== 'm204');
        again.data.flat = again.data.flat.filter((b) => !ids.includes(b.Id));
        return { Deleted: ids, Tombstoned: [], NotFound: [], InUse: entry.body.Ids.includes('m204') ? ['m204'] : [], Failed: [] };
    });
    dialogButton(dialogAgain, 'Delete').click();
    await again.advance(500);
    assert.ok(panel(again, 'Delete 205 bookmarks?'), 'the dialog stays for the one in use');
    assert.equal(dialogAgain.querySelector('.jfbm-error').textContent, '204 bookmarks deleted. One could not be deleted: a clip file is in use (being played or downloaded). Try again shortly.');
    assert.equal(dialogAgain.querySelector('.jfbm-bulk-description').textContent, '"Bookmark at 34:00" will be deleted.');
    assert.equal(dialogAgain.querySelector('.jfbm-bulk-progress').textContent, '', 'the progress line cleared');
    assert.equal(toolbar(again).content.querySelectorAll('.jfbm-row').length, 1);
    assert.equal(row(again, 'm204').querySelector('.jfbm-row-select-box').checked, true);
    assert.equal(barAgain.querySelector('.jfbm-bulk-count').textContent, '1 bookmark selected');
    assert.equal(again.errors.length, 0, again.errors.join('\n'));
});

test('the bulk delete closed while its batches go (Cancel, Escape) stops after the request under way, and a toast says what was deleted, what was kept and why, and that the rest is still selected', { skip }, async () => {
    // 405 rows, three batches; Cancel during the first request: that one is answered, the other two are never sent.
    const page = await opened({ hash: '#/jfbm-bookmarks?view=flat&page=9', data: manyBookmarks(405) });
    const t = toolbar(page);
    assert.equal(t.content.querySelectorAll('.jfbm-row').length, 405);
    const bar = root(page).querySelector('.jfbm-bulk-bar');
    tickRow(page, 'm0');
    bar.querySelector('.jfbm-bulk-all').click();
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '405 bookmarks selected');
    bar.querySelector('.jfbm-bulk-delete').click();
    await page.advance(20);
    const dialog = panel(page, 'Delete 405 bookmarks?');
    let progressSeen = null;
    page.respond('POST', '/Bookmarks/BulkDelete', (entry) => {
        if (progressSeen === null) {
            progressSeen = dialog.querySelector('.jfbm-bulk-progress').textContent;
            assert.equal(dialogButton(dialog, 'Cancel').disabled, false, 'Cancel can be used while the batches go');
            dialogButton(dialog, 'Cancel').click();
        }

        const ids = entry.body.Ids;
        page.data.flat = page.data.flat.filter((b) => !ids.includes(b.Id));
        return { Deleted: ids, Tombstoned: [], NotFound: [], InUse: [], Failed: [] };
    });
    dialogButton(dialog, 'Delete').click();
    await page.advance(500);
    assert.equal(progressSeen, 'Deleting 1–200 of 405…');
    assert.deepEqual(page.fetchLog.filter((f) => f.path === '/Bookmarks/BulkDelete').map((f) => f.body.Ids.length), [200], 'the batch under way answered, the rest never sent');
    assert.equal(panel(page, 'Delete 405 bookmarks?'), null, 'the dialog closed');
    assert.deepEqual(toasts(page).slice(-1), ['200 bookmarks deleted. Stopped: 205 bookmarks were not deleted. They are still selected.']);
    assert.equal(t.content.querySelectorAll('.jfbm-row').length, 205, 'the list read again');
    assert.ok(t.content.querySelectorAll('.jfbm-row-select-box').every((b) => b.checked), 'what was left still ticked');
    assert.equal(bar.querySelector('.jfbm-bulk-count').textContent, '205 bookmarks selected');
    assert.equal(bar.classList.contains('hide'), false);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));

    // Escape during the first of two: the same stop; the batch under way keeps one (in use), named in the toast.
    const escaped = await opened({ hash: '#/jfbm-bookmarks?view=flat&page=5', data: manyBookmarks(205) });
    const escapedBar = root(escaped).querySelector('.jfbm-bulk-bar');
    tickRow(escaped, 'm0');
    escapedBar.querySelector('.jfbm-bulk-all').click();
    escapedBar.querySelector('.jfbm-bulk-delete').click();
    await escaped.advance(20);
    const escapedDialog = panel(escaped, 'Delete 205 bookmarks?');
    let escapedOnce = false;
    escaped.respond('POST', '/Bookmarks/BulkDelete', (entry) => {
        if (!escapedOnce) {
            escapedOnce = true;
            escaped.key(dialogButton(escapedDialog, 'Cancel'), 'Escape');
        }

        const ids = entry.body.Ids.filter((id) => id !== 'm7');
        escaped.data.flat = escaped.data.flat.filter((b) => !ids.includes(b.Id));
        return { Deleted: ids, Tombstoned: [], NotFound: [], InUse: entry.body.Ids.includes('m7') ? ['m7'] : [], Failed: [] };
    });
    dialogButton(escapedDialog, 'Delete').click();
    await escaped.advance(500);
    assert.equal(escaped.fetchLog.filter((f) => f.path === '/Bookmarks/BulkDelete').length, 1);
    assert.equal(panel(escaped, 'Delete 205 bookmarks?'), null);
    assert.deepEqual(toasts(escaped).slice(-1), ['199 bookmarks deleted. One could not be deleted: a clip file is in use (being played or downloaded). Try again shortly. '
        + 'Stopped: 5 bookmarks were not deleted. They are still selected.']);
    assert.equal(escapedBar.querySelector('.jfbm-bulk-count').textContent, '6 bookmarks selected');
    assert.equal(row(escaped, 'm7').querySelector('.jfbm-row-select-box').checked, true);
    assert.equal(escaped.errors.length, 0, escaped.errors.join('\n'));

    // One request, the dialog closed while it goes and the server keeping one: nothing to stop, the outcome a toast.
    const data = fixtures();
    data.groups[0].Bookmarks[0].Clip = { Status: 'Completed', FileName: 'Heat - Bank job.mp4', FileSize: 2048 };
    data.flat[0] = data.groups[0].Bookmarks[0];
    const single = await opened({ data });
    const singleBar = root(single).querySelector('.jfbm-bulk-bar');
    tickRow(single, 'b1');
    tickRow(single, 'b4');
    singleBar.querySelector('.jfbm-bulk-delete').click();
    await single.advance(20);
    const singleDialog = panel(single, 'Delete 2 bookmarks?');
    single.respond('POST', '/Bookmarks/BulkDelete', () => {
        singleDialog.querySelector('button[title="Close"]').click();
        single.data.groups.splice(2, 1);
        single.data.flat.splice(3, 1);
        return { Deleted: ['b4'], Tombstoned: [], NotFound: [], InUse: ['b1'], Failed: [] };
    });
    dialogButton(singleDialog, 'Delete').click();
    await single.advance(300);
    assert.equal(panel(single, 'Delete 2 bookmarks?'), null);
    assert.deepEqual(toasts(single).slice(-1), ['1 bookmark deleted. One could not be deleted: a clip file is in use (being played or downloaded). Try again shortly. It is still selected.']);
    assert.equal(row(single, 'b4'), null);
    assert.equal(row(single, 'b1').querySelector('.jfbm-row-select-box').checked, true);
    assert.equal(singleBar.querySelector('.jfbm-bulk-count').textContent, '1 bookmark selected');
    assert.equal(single.errors.length, 0, single.errors.join('\n'));
});

test('an inline rename still being typed survives a change of another bookmark of the same video (the group drawn again, the flat view\'s rows, a clip\'s progress tick) and saves what was typed; a saved rename is replaced by the fresh row', { skip }, async () => {
    const page = await opened();
    respondRename(page);

    // Rename b1, type, don't save; delete b2 (same video): the group is drawn again around b1's row, which stays as it is.
    row(page, 'b1').querySelector('button[title="Rename"]').click();
    const renaming = row(page, 'b1');
    const input = renaming.querySelector('.jfbm-rename input');
    input.value = 'Heist';
    page.respond('DELETE', '/Bookmarks/b2', null, 204);
    page.data.groups[0].Bookmarks.splice(1, 1);
    page.data.groups[0].Count = 1;
    page.data.flat.splice(1, 1);
    row(page, 'b2').querySelector('button[title="Delete"]').click();
    await page.advance(20);
    dialogButton(panel(page, 'Delete bookmark?'), 'Delete').click();
    await page.advance(300);
    assert.equal(row(page, 'b2'), null, 'the group drawn again');
    assert.equal(toolbar(page).content.querySelector('.jfbm-group[data-jfbm-group="' + HEAT.replace(/-/g, '') + '"] .listItemBodyText.secondary').textContent.indexOf('1 bookmark ·'), 0, 'its header follows');
    assert.equal(row(page, 'b1'), renaming, 'the renaming row is the same node');
    assert.equal(row(page, 'b1').querySelector('.jfbm-rename input'), input, 'with its field');
    assert.equal(input.value, 'Heist', 'and what was typed');
    assert.equal(page.document.activeElement, input, 'the focus still in it');
    assert.equal(row(page, 'b1').parentNode.getAttribute('role'), 'list', 'in the fresh group\'s list');
    page.key(input, 'Enter');
    await page.advance(300);
    assert.deepEqual(nameRequests(page).slice(-1)[0].body, { Name: 'Heist', IfUpdatedAt: '2026-10-05T10:00:00.0000000Z' });
    assert.notEqual(row(page, 'b1'), renaming, 'saved, the row is drawn again from the server\'s answer');
    assert.equal(row(page, 'b1').querySelector('.jfbm-rename'), null);
    assert.equal(row(page, 'b1').querySelector('.jfbm-row-title').textContent, 'Heist');
    assert.equal(page.document.activeElement, row(page, 'b1').querySelector('button[title="Rename"]'));

    // The flat view: Name it on b2, type; then b1 (the same video) renamed from its own row: the video's rows are read
    // again, b1's drawn again, b2's left as it is.
    const flat = await opened({ hash: '#/jfbm-bookmarks?view=flat' });
    respondRename(flat);
    row(flat, 'b2').querySelector('.jfbm-name-it').click();
    const flatRow = row(flat, 'b2');
    flatRow.querySelector('.jfbm-rename input').value = 'Second';
    const b1Row = row(flat, 'b1');
    b1Row.querySelector('button[title="Rename"]').click();
    b1Row.querySelector('.jfbm-rename input').value = 'Elsewhere';
    flat.key(b1Row.querySelector('.jfbm-rename input'), 'Enter');
    await flat.advance(300);
    assert.equal(row(flat, 'b1').querySelector('.jfbm-row-title').textContent, 'Elsewhere', 'b1 drawn again');
    assert.notEqual(row(flat, 'b1'), b1Row);
    assert.equal(row(flat, 'b2'), flatRow, 'b2\'s row kept');
    assert.equal(row(flat, 'b2').querySelector('.jfbm-rename input').value, 'Second');
    flat.key(row(flat, 'b2').querySelector('.jfbm-rename input'), 'Enter');
    await flat.advance(300);
    assert.equal(row(flat, 'b2').querySelector('.jfbm-row-title').textContent, 'Second');

    // A clip's progress tick on the row being renamed leaves it alone; the next tick after the rename ends draws it.
    const data = fixtures();
    const running = (progress) => ({ Status: 'Running', Progress: progress, FileName: null, FileSize: null, Error: null });
    data.groups[0].Bookmarks[0].Clip = running(10);
    const ticking = await opened({ data });
    ticking.respond('GET', '/Bookmarks/b1', Object.assign({}, data.flat[0], { Clip: running(55) }));
    row(ticking, 'b1').querySelector('button[title="Rename"]').click();
    const tickingRow = row(ticking, 'b1');
    await ticking.advance(2100);
    assert.equal(requests(ticking, '/Bookmarks/b1').length, 1, 'one poll');
    assert.equal(row(ticking, 'b1'), tickingRow, 'the row kept through the tick');
    assert.ok(row(ticking, 'b1').querySelector('.jfbm-rename'));
    assert.ok(tickingRow.classList.contains('jfbm-row-rename-kept'), 'marked as kept');
    const groupsBeforeEscape = requests(ticking, '/Bookmarks/Groups').length;
    ticking.key(row(ticking, 'b1').querySelector('.jfbm-rename input'), 'Escape');
    await ticking.advance(300);
    assert.ok(requests(ticking, '/Bookmarks/Groups').slice(groupsBeforeEscape).some((r) => r.query.itemId), 'cancelled, the video is read again at once');
    assert.notEqual(row(ticking, 'b1'), tickingRow, 'and the row drawn afresh');
    await ticking.advance(1800);
    assert.equal(row(ticking, 'b1').querySelector('.jfbm-progress').getAttribute('aria-valuenow'), '55', 'then the next tick');
    assert.equal(page.errors.length + flat.errors.length + ticking.errors.length, 0, [page.errors, flat.errors, ticking.errors].join('\n'));
});

test('two inline renames open in one group both survive its redraw with what was typed, the focus staying in the one it was in; one saved, the other is still kept and keeps the focus the user gave it', { skip }, async () => {
    const data = fixtures();
    const b5 = bookmark('b5', HEAT, 'Heat (1995)', 6000, { EndTicks: 6060 * 10000000, Clip: { Status: 'Running', Progress: 30, FileName: null, FileSize: null, Error: null } });
    data.groups[0].Bookmarks.push(b5);
    data.groups[0].Count = 3;
    data.flat.splice(2, 0, b5);
    const page = await opened({ data });
    respondRename(page);

    // Rename on b1 ("A typed"), then Name it on b2 ("B typed", the focus in its field): two renames open at once.
    row(page, 'b1').querySelector('button[title="Rename"]').click();
    const first = row(page, 'b1');
    const firstInput = first.querySelector('.jfbm-rename input');
    firstInput.value = 'A typed';
    row(page, 'b2').querySelector('.jfbm-name-it').click();
    const second = row(page, 'b2');
    const secondInput = second.querySelector('.jfbm-rename input');
    secondInput.value = 'B typed';
    assert.ok(page.document.activeElement === secondInput, 'the focus in b2\'s field');
    assert.ok(first.querySelector('.jfbm-rename'), 'the first still open');

    // b5's clip completes: Heat's group is drawn again around both rows.
    const done = Object.assign({}, b5, { Clip: { Status: 'Completed', Progress: 100, FileName: 'Heat - 1.40.00.mp4', FileSize: 2048, Error: null } });
    page.data.groups[0].Bookmarks[2] = done;
    page.data.flat[2] = done;
    page.respond('GET', '/Bookmarks/b5', done);
    await page.advance(2400);
    assert.ok(row(page, 'b5').querySelector('.jfbm-clip-ready'), 'the group drawn again');
    assert.ok(row(page, 'b1') === first, 'the first rename kept');
    assert.equal(firstInput.value, 'A typed');
    assert.ok(row(page, 'b2') === second, 'and the second');
    assert.equal(secondInput.value, 'B typed');
    assert.ok(page.document.activeElement === secondInput, 'the focus still in the field it was in');
    assert.ok(first.classList.contains('jfbm-row-rename-kept') && second.classList.contains('jfbm-row-rename-kept'));
    const list = row(page, 'b5').parentNode;
    assert.deepEqual([first, second, row(page, 'b5')].map((r) => r.parentNode === list && list.childNodes.indexOf(r)), [0, 1, 2], 'in the fresh group\'s list, in order');

    // b2 saved, and the focus moved into b1's field before the redraw: b2 drawn afresh, b1 still kept with the focus.
    page.key(secondInput, 'Enter');
    firstInput.focus();
    await page.advance(300);
    assert.deepEqual(nameRequests(page).slice(-1)[0].body, { Name: 'B typed', IfUpdatedAt: '2026-10-05T10:00:00.0000000Z' });
    assert.ok(row(page, 'b2') !== second, 'the saved rename replaced');
    assert.equal(row(page, 'b2').querySelector('.jfbm-row-title').textContent, 'B typed');
    assert.ok(row(page, 'b1') === first, 'the open one kept again');
    assert.equal(firstInput.value, 'A typed');
    assert.ok(page.document.activeElement === firstInput, 'its focus not taken by the saved row');
    page.key(firstInput, 'Enter');
    await page.advance(300);
    assert.deepEqual(nameRequests(page).slice(-1)[0].body, { Name: 'A typed', IfUpdatedAt: '2026-10-05T10:00:00.0000000Z' });
    assert.equal(row(page, 'b1').querySelector('.jfbm-row-title').textContent, 'A typed');
    assert.ok(page.document.activeElement === row(page, 'b1').querySelector('button[title="Rename"]'), 'saved, the focus on its Rename');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a rename kept through its video\'s redraw and then cancelled reads the video again, so the row shows what changed meanwhile (its clip finished); a rename never kept reads nothing when cancelled', { skip }, async () => {
    const page = await opened();
    row(page, 'b1').querySelector('button[title="Rename"]').click();
    const renaming = row(page, 'b1');
    const input = renaming.querySelector('.jfbm-rename input');
    input.value = 'Typed';

    // Meanwhile b1's clip finished and b2 was deleted from its row: the group is drawn again around b1's row.
    const ready = Object.assign({}, page.data.groups[0].Bookmarks[0], { Clip: { Status: 'Completed', FileName: 'Heat - Bank job.mp4', FileSize: 2048 } });
    page.data.groups[0].Bookmarks = [ready];
    page.data.groups[0].Count = 1;
    page.data.flat = page.data.flat.filter((b) => b.Id !== 'b2').map((b) => (b.Id === 'b1' ? ready : b));
    page.respond('DELETE', '/Bookmarks/b2', null, 204);
    row(page, 'b2').querySelector('button[title="Delete"]').click();
    await page.advance(20);
    dialogButton(panel(page, 'Delete bookmark?'), 'Delete').click();
    await page.advance(300);
    assert.equal(row(page, 'b2'), null, 'the group drawn again');
    assert.equal(row(page, 'b1'), renaming, 'b1\'s row kept');
    assert.ok(renaming.classList.contains('jfbm-row-rename-kept'));
    assert.equal(renaming.querySelector('.jfbm-clip-ready'), null, 'the kept row still shows what it was drawn with');

    // Escape: the title back, then the video read again and the row drawn afresh, with the clip, the focus on Rename.
    const groupsBefore = requests(page, '/Bookmarks/Groups').length;
    page.key(input, 'Escape');
    await page.advance(300);
    assert.ok(requests(page, '/Bookmarks/Groups').slice(groupsBefore).some((r) => r.query.itemId), 'the video read again');
    assert.notEqual(row(page, 'b1'), renaming, 'drawn afresh');
    assert.ok(row(page, 'b1').querySelector('.jfbm-clip-ready'), 'with its finished clip');
    assert.equal(row(page, 'b1').querySelector('.jfbm-row-title').textContent, 'Bank job', 'the name as it was');
    assert.equal(page.document.activeElement, row(page, 'b1').querySelector('button[title="Rename"]'), 'the focus on Rename');
    assert.equal(nameRequests(page).length, 0, 'nothing saved');

    // A rename that was never kept (nothing changed meanwhile) reads nothing when cancelled.
    const before = page.fetchLog.length;
    row(page, 'b1').querySelector('button[title="Rename"]').click();
    page.key(row(page, 'b1').querySelector('.jfbm-rename input'), 'Escape');
    await page.advance(300);
    assert.equal(page.fetchLog.length, before, 'nothing read');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the flat view\'s per-video reload with more of the video than one request answers (over 200) reads the whole list instead of dropping the rows it didn\'t get', { skip }, async () => {
    const page = await opened({ hash: '#/jfbm-bookmarks?view=flat&page=5', data: manyBookmarks(205) });
    const t = toolbar(page);
    assert.equal(t.content.querySelectorAll('.jfbm-row').length, 205);
    page.respond('POST', '/Bookmarks/m3/Name', (entry) => {
        const named = Object.assign({}, page.data.flat[3], { Name: entry.body.Name, UpdatedAt: '2026-10-06T12:00:00.0000000Z' });
        page.data.flat[3] = named;
        return named;
    });
    const queriesBefore = requests(page, '/Bookmarks/Query').length;
    row(page, 'm3').querySelector('.jfbm-name-it').click();
    row(page, 'm3').querySelector('.jfbm-rename input').value = 'Third';
    page.key(row(page, 'm3').querySelector('.jfbm-rename input'), 'Enter');
    await page.advance(400);
    const queries = requests(page, '/Bookmarks/Query').slice(queriesBefore);
    assert.ok(queries.some((q) => q.query.itemId && String(q.query.limit) === '200'), 'the video\'s rows asked first (200 of 205)');
    assert.ok(queries.some((q) => !q.query.itemId && String(q.query.startIndex) === '200'), 'then the whole list, its second request too');
    assert.equal(t.content.querySelectorAll('.jfbm-row').length, 205, 'no row dropped');
    assert.equal(row(page, 'm3').querySelector('.jfbm-row-title').textContent, 'Third');
    assert.ok(row(page, 'm204'), 'the last row still there');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Player options: the preferences read fresh, the three AskForName states and the pause saved together; the device shortcut stored, a reserved or invalid key refused inline with Save disabled, Reset to default, off on this device; the capabilities read again and the empty state follows', { skip }, async () => {
    const page = await opened({ data: { groups: [], flat: [] } });
    const t = toolbar(page);
    assert.equal(t.content.querySelector('.jfbm-notice-text').textContent, 'No bookmarks yet. Press Shift+B while watching, or use the bookmark button in the player.');
    const button = root(page).querySelector('.jfbm-page-header .jfbm-page-options');
    assert.equal(button.textContent, 'Player options');
    assert.equal(root(page).querySelector('.jfbm-page-clips-link'), null, 'no My Clips in 1.1.0');
    const capabilitiesBefore = requests(page, '/Bookmarks/Capabilities').length;
    button.click();
    await page.advance(20);
    const dialog = panel(page, 'Player options');
    assert.ok(dialog);
    assert.equal(requests(page, '/Bookmarks/Preferences').length, 1, 'read when the dialog opens, not the page\'s copy');
    const ask = dialog.querySelector('.jfbm-options-ask');
    assert.deepEqual(ask.querySelectorAll('option').map((o) => [o.value, o.textContent]), [
        ['default', 'Use the server\'s default (don\'t ask)'],
        ['always', 'Always: the name dialog opens at once'],
        ['never', 'Never: the toast offers "Name it"']
    ]);
    assert.equal(ask.value, 'default');
    assert.equal(page.document.activeElement, ask);
    const pause = dialog.querySelector('.jfbm-options-pause');
    assert.equal(pause.checked, false);
    const shortcut = dialog.querySelector('.jfbm-options-shortcut');
    assert.equal(shortcut.value, '');
    assert.equal(shortcut.getAttribute('placeholder'), 'Server default: Shift+B');
    const off = dialog.querySelector('.jfbm-options-off');
    assert.equal(off.checked, false);
    const save = dialog.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Save');
    const error = dialog.querySelector('.jfbm-options-shortcut-error');
    assert.equal(error.getAttribute('role'), 'alert');

    // A reserved key (jellyfin-web's M, from Capabilities.ReservedShortcuts) and text that isn't a combination are
    // refused as typed; Save is disabled until the field is right.
    shortcut.value = 'm';
    page.dispatch(shortcut, 'input');
    assert.equal(error.textContent, 'M is taken by the video player or Jellyfin Enhanced, so the bookmark shortcut wouldn\'t work with it. Choose another key.');
    assert.equal(save.disabled, true);
    shortcut.value = 'Shift+';
    page.dispatch(shortcut, 'input');
    assert.equal(error.textContent, '"Shift+" isn\'t a key combination; use a key with optional modifiers, such as Shift+B.');
    assert.equal(save.disabled, true);
    shortcut.value = 'shift+y';
    page.dispatch(shortcut, 'input');
    assert.equal(error.textContent, '');
    assert.equal(save.disabled, false);
    shortcut.value = 'b';
    page.dispatch(shortcut, 'input');
    assert.equal(save.disabled, true, 'B is Jellyfin Enhanced\'s (in the list the server sent)');
    dialog.querySelector('.jfbm-options-reset').click();
    assert.equal(shortcut.value, '');
    assert.equal(save.disabled, false);
    assert.equal(page.document.activeElement, shortcut);

    // Save: Always, the pause, Shift+Y on this device.
    ask.value = 'always';
    pause.checked = true;
    shortcut.value = 'shift+y';
    page.dispatch(shortcut, 'input');
    let fresh = Object.assign({}, CAPABILITIES, { AskForName: true, AskForNameIsDefault: false, PauseWhileNaming: true });
    page.respond('GET', '/Bookmarks/Capabilities', fresh);
    assert.equal(save.getAttribute('type'), 'submit', 'Save submits the form, as Enter in the field does');
    submit(page, dialog);
    await page.advance(50);
    const posts = page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/Preferences');
    assert.equal(posts.length, 1);
    assert.deepEqual(posts[0].body, { AskForName: true, PauseWhileNaming: true });
    assert.equal(page.context.localStorage.getItem('jfbm:shortcut:bookmarkNow'), '"Shift+Y"', 'the device override, as the kit reads it');
    assert.equal(requests(page, '/Bookmarks/Capabilities').length, capabilitiesBefore + 1, 'read again after the save');
    assert.equal(panel(page, 'Player options'), null);
    assert.deepEqual(toasts(page), ['Player options saved.']);
    assert.equal(t.content.querySelector('.jfbm-notice-text').textContent, 'No bookmarks yet. Press Shift+Y while watching, or use the bookmark button in the player.', 'the empty state names the device\'s key');
    assert.deepEqual(plain(page.context.__jfkit.jfbm.instance.shortcuts.bindings()), ['Shift+Y'], 'the player\'s binding follows at once');

    // Open again: the preferences as the server has them now, the override in the field; Never and the server's key.
    page.respond('GET', '/Bookmarks/Preferences', { AskForName: true, PauseWhileNaming: true });
    button.click();
    await page.advance(20);
    const again = panel(page, 'Player options');
    assert.equal(again.querySelector('.jfbm-options-ask').value, 'always');
    assert.equal(again.querySelector('.jfbm-options-ask option').textContent, 'Use the server\'s default (don\'t ask)', 'the server\'s default named while the user has chosen too (AskForNameDefault)');
    assert.equal(again.querySelector('.jfbm-options-pause').checked, true);
    assert.equal(again.querySelector('.jfbm-options-shortcut').value, 'Shift+Y');
    again.querySelector('.jfbm-options-ask').value = 'never';
    again.querySelector('.jfbm-options-shortcut').value = 'Shift+B';
    page.dispatch(again.querySelector('.jfbm-options-shortcut'), 'input');
    // The admin turned the server's default on meanwhile: the next dialog names it so.
    fresh = Object.assign({}, CAPABILITIES, { AskForName: false, AskForNameIsDefault: false, AskForNameDefault: true, PauseWhileNaming: true });
    page.respond('GET', '/Bookmarks/Capabilities', fresh);
    submit(page, again);
    await page.advance(50);
    assert.deepEqual(page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/Preferences')[1].body, { AskForName: false, PauseWhileNaming: true });
    assert.equal(page.context.localStorage.getItem('jfbm:shortcut:bookmarkNow'), null, 'the server\'s own key keeps no override');
    assert.equal(panel(page, 'Player options'), null);

    // Off on this device: '' in the store; the empty state says so; the server default chosen again.
    button.click();
    await page.advance(20);
    const third = panel(page, 'Player options');
    assert.equal(third.querySelector('.jfbm-options-ask option').textContent, 'Use the server\'s default (ask)');
    third.querySelector('.jfbm-options-off').checked = true;
    page.dispatch(third.querySelector('.jfbm-options-off'), 'change');
    assert.equal(third.querySelector('.jfbm-options-shortcut').disabled, true);
    third.querySelector('.jfbm-options-ask').value = 'default';
    fresh = Object.assign({}, CAPABILITIES);
    page.respond('GET', '/Bookmarks/Capabilities', fresh);
    submit(page, third);
    await page.advance(50);
    assert.deepEqual(page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/Preferences')[2].body, { AskForName: null, PauseWhileNaming: true });
    assert.equal(page.context.localStorage.getItem('jfbm:shortcut:bookmarkNow'), '""');
    assert.equal(t.content.querySelector('.jfbm-notice-text').textContent, 'No bookmarks yet. Use the bookmark button in the player while watching.');
    assert.deepEqual(plain(page.context.__jfkit.jfbm.instance.shortcuts.bindings()), ['']);

    // A refused save shows the server's sentence and keeps the dialog.
    button.click();
    await page.advance(20);
    const fourth = panel(page, 'Player options');
    page.respond('POST', '/Bookmarks/Preferences', { title: 'Unavailable', status: 503, detail: 'Bookmarks are read-only right now.' }, 503, { contentType: 'application/problem+json' });
    submit(page, fourth);
    await page.advance(50);
    assert.ok(panel(page, 'Player options'));
    assert.equal(fourth.querySelectorAll('.jfbm-error').slice(-1)[0].textContent, 'Bookmarks are read-only right now.');
    page.key(fourth.querySelector('.jfbm-options-ask'), 'Escape');
    assert.equal(panel(page, 'Player options'), null);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));

    // A server without AskForNameDefault (an older build): the default is named only while the user has no choice.
    const older = await opened({ data: { groups: [], flat: [] }, capabilities: { AskForNameDefault: undefined, AskForName: true, AskForNameIsDefault: true } });
    older.respond('GET', '/Bookmarks/Preferences', { PauseWhileNaming: false });
    root(older).querySelector('.jfbm-page-options').click();
    await older.advance(20);
    assert.equal(panel(older, 'Player options').querySelector('.jfbm-options-ask option').textContent, 'Use the server\'s default (ask)');
    older.key(panel(older, 'Player options').querySelector('.jfbm-options-ask'), 'Escape');
    const chosen = await opened({ data: { groups: [], flat: [] }, capabilities: { AskForNameDefault: undefined, AskForName: true, AskForNameIsDefault: false } });
    chosen.respond('GET', '/Bookmarks/Preferences', { AskForName: true, PauseWhileNaming: false });
    root(chosen).querySelector('.jfbm-page-options').click();
    await chosen.advance(20);
    assert.equal(panel(chosen, 'Player options').querySelector('.jfbm-options-ask option').textContent, 'Use the server\'s default', 'unknown: the resolved AskForName is the user\'s own');
    assert.equal(older.errors.length + chosen.errors.length, 0);
});

test('availability: a group the viewer can\'t open shows Unavailable with a placeholder and no link, its rows unplayable without scissors but renamable, selectable, editable and deletable; the same row in the flat view; a Not in library row renames inline', { skip }, async () => {
    const data = fixtures();
    const lost = 'ffff0000-0000-0000-0000-000000000004';
    const row5 = bookmark('b5', lost, 'Lost Access (2003)', 30, { Name: 'Kept', ItemAvailable: false, Detached: false, EndTicks: 60 * 10000000 });
    data.groups.push({ ItemId: lost, ItemName: 'Lost Access (2003)', ItemType: 'Movie', ItemAvailable: false, Count: 1, LatestUpdatedAt: '2026-10-02T10:00:00.0000000Z', Bookmarks: [row5], BookmarksTruncated: false });
    data.flat.push(row5);
    const page = await opened({ data });
    const t = toolbar(page);
    const group = t.content.querySelector('.jfbm-group[data-jfbm-group="' + lost.replace(/-/g, '') + '"]');
    const header = group.querySelector('.jfbm-group-header');
    assert.equal(header.querySelector('img'), null, 'no poster request');
    assert.ok(header.querySelector('.jfbm-listitem-image .material-icons'), 'the placeholder');
    assert.equal(header.querySelectorAll('a').length, 0, 'no link');
    assert.equal(header.querySelector('.jfbm-badge').textContent, 'Unavailable');
    assert.equal(header.querySelector('.jfbm-badge').getAttribute('title'), 'You can\'t open this video any more.');
    assert.equal(page.fetchLog.filter((f) => f.path.includes('/Items/' + lost.replace(/-/g, ''))).length, 0);
    const r = group.querySelector('.jfbm-row[data-id="b5"]');
    assert.equal(r.querySelector('.jfbm-time-button').disabled, true);
    assert.equal(r.querySelector('.jfbm-badge').textContent, 'Unavailable');
    assert.equal(r.classList.contains('jfbm-row-detached'), false, 'not greyed: it is in the library, only not for this viewer');
    assert.deepEqual(r.querySelectorAll('.jfbm-row-actions button').map((b) => b.getAttribute('title')), ['Rename', 'Edit', 'Delete', 'More actions'], 'no scissors for a range the viewer can\'t open');
    assert.ok(r.querySelector('.jfbm-row-select-box'));
    r.querySelector('button[title="Rename"]').click();
    assert.equal(r.querySelector('.jfbm-rename input').value, 'Kept');
    page.key(r.querySelector('.jfbm-rename input'), 'Escape');

    // The detached row (Not in library): greyed, Name it inline all the same.
    const gone = row(page, 'b3');
    assert.equal(gone.classList.contains('jfbm-row-detached'), true);
    assert.equal(gone.querySelector('.jfbm-time-button').disabled, true);
    assert.deepEqual(gone.querySelectorAll('.jfbm-row-actions button').map((b) => b.getAttribute('title')), ['Rename', 'Edit', 'Delete', 'More actions']);
    gone.querySelector('button[title="Rename"]').click();
    assert.equal(gone.querySelector('.jfbm-rename input').value, 'Lost scene');
    page.key(gone.querySelector('.jfbm-rename input'), 'Escape');

    clickView(page, 'flat');
    await page.advance(50);
    const flat = row(page, 'b5');
    assert.equal(flat.querySelector('.jfbm-badge').textContent, 'Unavailable');
    assert.equal(flat.querySelector('.jfbm-row-item a'), null, 'the video named, not linked');
    assert.equal(flat.querySelector('.jfbm-row-item').textContent, 'Lost Access (2003)');
    assert.equal(flat.querySelector('.jfbm-time-button').disabled, true);
    assert.equal(page.fetchLog.filter((f) => f.path.startsWith('/Items')).length, 0);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the TV layout: the Select toggle shows the checkbox column and the bar, off drops the selection; the row\'s focus order; the Player options dialog takes the D-pad in order and the Back key closes it', { skip }, async () => {
    const page = await start({ hash: '#/jfbm-bookmarks' });
    page.document.documentElement.classList.add('layout-tv');
    showFallbackPage(page);
    await page.advance(120);
    const r = pageBody(page);
    assert.equal(r.classList.contains('jfbm-page-tv'), true, 'the body carries the layout class for the stylesheet');
    const select = r.querySelector('.jfbm-page-toolbar .jfbm-page-select-button');
    assert.equal(select.textContent, 'Select');
    assert.equal(select.getAttribute('aria-pressed'), 'false');
    assert.equal(r.classList.contains('jfbm-page-selecting'), false, 'the stylesheet hides the checkboxes and the bar until Select');
    select.click();
    assert.equal(select.getAttribute('aria-pressed'), 'true');
    assert.equal(r.classList.contains('jfbm-page-selecting'), true);
    const box = row(page, 'b1').querySelector('.jfbm-row-select-box');
    box.checked = true;
    page.dispatch(box, 'change');
    assert.equal(r.querySelector('.jfbm-bulk-count').textContent, '1 bookmark selected');
    select.click();
    assert.equal(r.classList.contains('jfbm-page-selecting'), false);
    assert.equal(r.querySelector('.jfbm-bulk-bar').classList.contains('hide'), true, 'the selection dropped with it');
    assert.equal(box.checked, false);

    // Every action is a focusable button, in reading order; jellyfin-web's own navigation moves between them.
    const focusable = row(page, 'b1').querySelectorAll('button, input, a').filter((e) => !e.disabled);
    assert.deepEqual(focusable.map((e) => e.getAttribute('aria-label') || e.getAttribute('title') || e.textContent), ['Select Bank job', 'Play from here', 'Rename', 'Make a clip', 'Edit', 'Delete', 'More actions']);
    const unnamed = row(page, 'b2').querySelectorAll('button, input, a').filter((e) => !e.disabled);
    assert.deepEqual(unnamed.map((e) => e.getAttribute('aria-label') || e.getAttribute('title') || e.textContent), ['Select Bookmark at 1:30:00', 'Play from here', 'Name it', 'Make a clip (set an end first)', 'Edit', 'Delete', 'More actions']);
    const pager = r.querySelector('.jfbm-pager');
    assert.ok(pager, 'the kit\'s pager, which the remote presses on a TV');

    // The dialog: the D-pad moves through its controls in order without wrapping (the kit's scopedKeys), Back closes.
    r.querySelector('.jfbm-page-options').click();
    await page.advance(20);
    const dialog = panel(page, 'Player options');
    const ask = dialog.querySelector('.jfbm-options-ask');
    assert.equal(page.document.activeElement, ask);
    const order = [];
    for (let i = 0; i < 8; i++) {
        assert.equal(page.key(page.document.activeElement, 'ArrowDown'), false, 'handled by the dialog');
        const active = page.document.activeElement;
        order.push(active.getAttribute('aria-label') || active.getAttribute('title') || active.className.split(' ').filter((c) => c.startsWith('jfbm-options'))[0] || active.textContent);
    }

    assert.deepEqual(order, ['jfbm-options-pause', 'jfbm-options-shortcut', 'jfbm-options-reset', 'jfbm-options-off', 'Cancel', 'Save', 'Save', 'Save'], 'down through the controls, no wrap past the last');
    page.key(page.document.activeElement, 'ArrowUp');
    assert.equal(page.document.activeElement.textContent, 'Cancel');
    assert.equal(page.key(page.document.activeElement, 'Unidentified', { keyCode: 461 }), false, 'webOS Back');
    assert.equal(panel(page, 'Player options'), null);

    // The row menu of a narrow screen takes the D-pad too, and Back.
    row(page, 'b1').querySelector('.jfbm-row-more').click();
    await page.advance(10);
    const menu = panel(page, 'Bank job');
    assert.equal(page.document.activeElement.textContent, 'Play from here');
    page.key(page.document.activeElement, 'ArrowDown');
    assert.equal(page.document.activeElement.textContent, 'Rename');
    assert.equal(page.key(page.document.activeElement, 'Back'), false);
    assert.equal(panel(page, 'Bank job'), null);
    page.document.documentElement.classList.remove('layout-tv');
    assert.ok(menu);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a narrow screen: the row\'s More button opens our own menu of its actions (play first, disabled for a video that can\'t be played), each of which acts after closing it; the arrow keys move through it on every layout', { skip }, async () => {
    const page = await opened();
    const more = row(page, 'b1').querySelector('.jfbm-row-more');
    assert.equal(more.getAttribute('title'), 'More actions');
    assert.equal(more.getAttribute('aria-label'), 'More actions');
    more.click();
    await page.advance(10);
    const menu = panel(page, 'Bank job');
    assert.ok(menu, 'titled with the bookmark');
    assert.equal(menu.querySelector('.jfbm-row-menu').getAttribute('role'), 'menu');
    const items = menu.querySelectorAll('.jfbm-row-menu-item');
    assert.deepEqual(items.map((b) => [b.textContent, b.disabled]), [['Play from here', false], ['Rename', false], ['Make a clip', false], ['Edit', false], ['Delete', false]]);
    assert.ok(items.every((b) => b.getAttribute('role') === 'menuitem'));

    // Not the TV layout, and still a menu's keys: Down and Up through the items, wrapping, Home and End.
    assert.equal(pageBody(page).classList.contains('jfbm-page-tv'), false);
    assert.equal(page.document.activeElement, items[0]);
    const press = (key) => {
        const prevented = page.key(page.document.activeElement, key) === false;
        return [page.document.activeElement.textContent, prevented];
    };
    assert.deepEqual(press('ArrowDown'), ['Rename', true]);
    assert.deepEqual(press('ArrowUp'), ['Play from here', true]);
    assert.deepEqual(press('ArrowUp'), ['Delete', true], 'wraps');
    assert.deepEqual(press('ArrowDown'), ['Play from here', true]);
    assert.deepEqual(press('End'), ['Delete', true]);
    assert.deepEqual(press('Home'), ['Play from here', true]);
    items[4].click();
    await page.advance(20);
    assert.equal(panel(page, 'Bank job'), null, 'the menu closed');
    assert.ok(panel(page, 'Delete bookmark?'), 'then the action');
    page.key(panel(page, 'Delete bookmark?').querySelector('button'), 'Escape');

    row(page, 'b3').querySelector('.jfbm-row-more').click();
    await page.advance(10);
    const gone = panel(page, 'Lost scene');
    assert.deepEqual(gone.querySelectorAll('.jfbm-row-menu-item').map((b) => [b.textContent, b.disabled]), [['Play from here', true], ['Rename', false], ['Edit', false], ['Delete', false]], 'Not in library: no play, no clip');
    assert.equal(page.document.activeElement.textContent, 'Rename', 'the first that can be used');
    page.key(page.document.activeElement, 'ArrowUp');
    assert.equal(page.document.activeElement.textContent, 'Delete', 'the keys skip the disabled play');
    page.key(page.document.activeElement, 'ArrowDown');
    assert.equal(page.document.activeElement.textContent, 'Rename');
    gone.querySelectorAll('.jfbm-row-menu-item')[1].click();
    await page.advance(10);
    assert.ok(row(page, 'b3').querySelector('.jfbm-rename'), 'Rename from the menu is the inline rename');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a read-only store is said above the toolbar, each reason in a plain sentence, and what to do: a user asks the admin, an admin opens the settings page', { skip }, async () => {
    const page = await opened({ capabilities: { StoreReadOnly: true, StoreReadOnlyReason: 'DataUnreadable' } });
    const notice = root(page).querySelector('.jfbm-page-readonly');
    assert.equal(notice.querySelector('.jfbm-notice-text').textContent, 'Bookmarks are read-only right now: the bookmark data couldn\'t be read, and no snapshot could take its place. Ask your server\'s administrator.');
    assert.equal(notice.nextElementSibling.className, 'jfbm-page-toolbar');
    const sentences = {
        NewerVersion: 'Bookmarks are read-only right now: the bookmark data was written by a newer version of Bookmarks.',
        MigrationFailed: 'Bookmarks are read-only right now: the bookmark data couldn\'t be upgraded to this version of Bookmarks.',
        DataMissing: 'Bookmarks are read-only right now: the bookmark data is missing, and none of its snapshots could be restored.',
        DataUnreadable: 'Bookmarks are read-only right now: the bookmark data couldn\'t be read, and no snapshot could take its place.'
    };
    const others = [];
    for (const [reason, sentence] of Object.entries(sentences)) {
        const admin = await opened({ capabilities: { StoreReadOnly: true, StoreReadOnlyReason: reason, IsAdmin: true } });
        assert.equal(root(admin).querySelector('.jfbm-page-readonly .jfbm-notice-text').textContent, sentence + ' The plugin\'s settings page (Dashboard, Plugins, Bookmarks) says what to do.');
        const user = await opened({ capabilities: { StoreReadOnly: true, StoreReadOnlyReason: reason } });
        assert.equal(root(user).querySelector('.jfbm-page-readonly .jfbm-notice-text').textContent, sentence + ' Ask your server\'s administrator.');
        assert.ok(!root(user).textContent.includes(reason), 'the reason\'s name isn\'t shown: ' + reason);
        others.push(admin, user);
    }

    // A reason this version doesn't know (a newer server's), or none: only that the data is read-only.
    for (const reason of ['SomethingNew', undefined, null]) {
        const unknown = await opened({ capabilities: { StoreReadOnly: true, StoreReadOnlyReason: reason } });
        assert.equal(root(unknown).querySelector('.jfbm-page-readonly .jfbm-notice-text').textContent, 'Bookmarks are read-only right now. Ask your server\'s administrator.');
        others.push(unknown);
    }

    const without = await opened();
    assert.equal(root(without).querySelector('.jfbm-page-readonly'), null);
    assert.equal([page, without, ...others].reduce((n, p) => n + p.errors.length, 0), 0);
});
