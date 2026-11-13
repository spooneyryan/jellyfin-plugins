// Unit tests of kit.actionSheet and kit.nativeDialog in kit/web/kit.js, in the
// fake page of fake-dom.mjs, with jellyfin-web's action sheet played the way it reads in the 10.11.5
// bundle: a new div.actionSheet in a div.dialogContainer appended to <body>, complete when attached; a dialog-level
// click listener that stores the clicked row's data-id and closes the sheet; close (not bubbling) after the history pop.
// Run with
//   node --test kit/web/tests
import test from 'node:test';
import assert from 'node:assert/strict';
import { anchorDeclarations, createPage, loadKit, plain } from './fake-dom.mjs';

const BUILD = { plugin: 'Kit Test', prefix: 'jfkt', route: 'Fx', version: '1.2.3.0', kit: '0.1.0', mvid: '0123abcd' };
const ITEM_MENU = ['resume', 'playallfromhere', 'addtocollection', 'refresh', 'moremediainfo', 'identify', 'edit', 'editimages', 'delete'];
const VIEWER_MENU = ['resume', 'playallfromhere', 'markplayed', 'moremediainfo'];
const BULK_MENU = ['selectall', 'addtocollection', 'playlist', 'groupvideos', 'markplayed', 'markunplayed', 'refresh'];
const VIEWER_BULK_MENU = ['selectall', 'addtocollection', 'playlist', 'markplayed', 'markunplayed'];
const ROW_CLASS = 'listItem listItem-button actionSheetMenuItem emby-button';

async function setup(options) {
    options = options || {};
    const page = createPage();
    page.respond('POST', '/Fx/Diagnostics/ClientHealth', null, 204);
    const exports = loadKit(page);
    const kit = exports.createKit(BUILD, '', { anchors: anchorDeclarations() });
    if (options.before) {
        options.before(kit);
    }

    if (options.start !== false) {
        kit.start();
    }

    page.installApiClient({ userId: options.userId || 'user-1', admin: options.admin !== false });
    await page.advance(60);
    return { page, kit, exports };
}

// An items container whose click listener stops the event as shortcuts.js does, so only a capture listener sees it.
function itemsContainer(page, children) {
    const container = page.element('div', { class: 'itemsContainer vertical-wrap' }, children);
    container.addEventListener('click', (event) => event.stopPropagation());
    container.addEventListener('command', (event) => event.stopPropagation());
    page.document.body.append(container);
    return container;
}

function card(page, id, attrs) {
    const menu = page.element('button', { is: 'paper-icon-button-light', class: 'cardOverlayButton itemAction', 'data-action': 'menu' },
        [page.element('span', { class: 'material-icons more_vert' })]);
    const node = page.element('div', Object.assign({ class: 'card portraitCard', 'data-id': id, 'data-type': 'Movie', 'data-isfolder': 'false', 'data-serverid': 'server-1' }, attrs || {}),
        [page.element('div', { class: 'cardBox' }, [page.element('div', { class: 'cardOverlayContainer' }, [menu])])]);
    node.menu = menu;
    return node;
}

function listRow(page, id, attrs) {
    const menu = page.element('button', { class: 'listItemButton itemAction', 'data-action': 'menu' });
    const node = page.element('div', Object.assign({ class: 'listItem listItem-border', 'data-id': id, 'data-type': 'Episode', 'data-isfolder': 'false', 'data-serverid': 'server-1' }, attrs || {}), [menu]);
    node.menu = menu;
    return node;
}

// jellyfin-web's sheet: built whole, then attached; the click on a row stores its data-id and closes; close follows
// the history pop (closeAfter ms, none when null); the container goes 100 ms later.
function openSheet(page, commands, opts) {
    opts = opts || {};
    const sheet = page.element('div', { class: 'focuscontainer dialog actionsheet-not-fullscreen actionSheet', 'data-history': 'true', 'data-removeonclose': 'true' });
    const scroller = page.element('div', { class: 'actionSheetScroller scrollY' });
    commands.forEach((command, i) => {
        scroller.append(page.element('button', { is: 'emby-button', type: 'button', class: ROW_CLASS + (i === 0 && opts.selectedFirst ? ' selected' : ''), 'data-id': command }, [
            page.element('span', { class: 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons play_arrow', 'aria-hidden': 'true' }),
            page.element('div', { class: 'listItemBody actionsheetListItemBody' }, [page.element('div', { class: 'listItemBodyText actionSheetItemText' })])
        ]));
    });
    sheet.append(page.element('div', { class: 'actionSheetContent' }, [opts.noScroller ? page.element('div', {}) : scroller]));
    if (opts.top !== undefined) {
        sheet.style.position = 'fixed';
        sheet.style.top = opts.top + 'px';
        sheet.style.left = opts.left + 'px';
    }

    const state = { selected: null, closed: false };
    const container = page.element('div', { class: 'dialogContainer' }, [sheet]);
    sheet.addEventListener('click', (event) => {
        const row = event.target.closest('.actionSheetMenuItem');
        if (!row || state.closing) {
            return;
        }

        state.closing = true;
        state.selected = row.getAttribute('data-id');
        if (opts.closeAfter !== null) {
            page.context.setTimeout(() => {
                state.closed = true;
                sheet.dispatchEvent(new page.Event('close', { bubbles: false }));
                page.context.setTimeout(() => container.remove(), 100);
            }, opts.closeAfter === undefined ? 40 : opts.closeAfter);
        }
    });
    page.document.body.append(container);
    return { sheet, scroller, container, state };
}

// A sheet opened, seen by the observer, and gone again.
async function shown(page, commands) {
    const sheet = openSheet(page, commands);
    await page.flush();
    sheet.container.remove();
    return sheet;
}

function rows(scroller) {
    return scroller.querySelectorAll('.actionSheetMenuItem').map((row) => row.getAttribute('data-id'));
}

function demo(kit, seen, extra) {
    return kit.actionSheet.addItem(Object.assign({
        id: 'demo',
        icon: 'drive_file_move',
        text: 'Move media to new location',
        when: (ctx) => {
            seen.push(ctx);
            return !ctx.isBulk;
        },
        onSelect: (ctx) => seen.push(['selected', ctx])
    }, extra || {}));
}

// ------------------------------------------------------------------------------------------------ the item menu

test('a card\'s "⋮": the entry goes right after Refresh metadata, shaped like the sheet\'s own rows', async () => {
    const { kit, page } = await setup();
    const seen = [];
    demo(kit, seen);
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);
    page.dispatch(movie.menu.firstChild, 'click');
    await page.advance(120);
    const { scroller } = openSheet(page, ITEM_MENU, { selectedFirst: true });
    await page.flush();

    assert.deepEqual(rows(scroller), ['resume', 'playallfromhere', 'addtocollection', 'refresh', 'jfkt-demo', 'moremediainfo', 'identify', 'edit', 'editimages', 'delete']);
    const row = scroller.querySelector('[data-id="jfkt-demo"]');
    assert.equal(row.tagName, 'BUTTON');
    assert.equal(row.getAttribute('is'), 'emby-button');
    assert.equal(row.getAttribute('type'), 'button');
    assert.equal(row.getAttribute('class'), ROW_CLASS, 'the first row\'s classes, without its "selected"');
    assert.equal(row.querySelector('.actionsheetMenuItemIcon').getAttribute('class'), 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons drive_file_move');
    assert.equal(row.querySelector('.listItemBody.actionsheetListItemBody .listItemBodyText.actionSheetItemText').textContent, 'Move media to new location');
    const ctx = seen[0];
    assert.deepEqual(plain({ itemIds: ctx.itemIds, surface: ctx.surface, isBulk: ctx.isBulk, itemType: ctx.itemType, isFolder: ctx.isFolder, serverId: ctx.serverId }),
        { itemIds: ['movie-1'], surface: 'card', isBulk: false, itemType: 'Movie', isFolder: false, serverId: 'server-1' });
    assert.deepEqual(plain(ctx.commands), ITEM_MENU);
    assert.ok(Object.isFrozen(ctx) && Object.isFrozen(ctx.itemIds));
    assert.equal(ctx.sheet.classList.contains('actionSheet'), true);
    assert.equal(ctx.trigger, movie.menu, 'the trigger: the card\'s menu button');
    assert.deepEqual(plain(kit.actionSheet.items()), ['jfkt-demo']);
    assert.equal(page.warnings.length, 0);
});

test('the click is not stopped: the sheet stores our data-id and closes itself; onSelect runs after its close event', async () => {
    const { kit, page } = await setup();
    const seen = [];
    demo(kit, seen);
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);
    page.dispatch(movie.menu, 'click');
    const { scroller, state } = openSheet(page, ITEM_MENU, { closeAfter: 300 });
    await page.flush();
    page.dispatch(scroller.querySelector('[data-id="jfkt-demo"] .actionSheetItemText'), 'click');

    assert.equal(state.selected, 'jfkt-demo', 'the sheet\'s own listener saw the click');
    await page.advance(299);
    assert.equal(seen.filter((s) => s[0] === 'selected').length, 0, 'not before close');
    await page.advance(1);
    assert.equal(state.closed, true);
    const selected = seen.filter((s) => s[0] === 'selected');
    assert.equal(selected.length, 1);
    assert.deepEqual(plain(selected[0][1].itemIds), ['movie-1']);
    await page.advance(2000);
    assert.equal(seen.filter((s) => s[0] === 'selected').length, 1, 'once, the fallback timer does nothing more');
});

test('without a close event onSelect still runs after 1.5 s; an onSelect that throws or rejects is reported', async () => {
    const { kit, page } = await setup();
    const calls = [];
    kit.actionSheet.addItem({ id: 'one', text: 'One', onSelect: () => { calls.push('one'); throw new Error('boom'); } });
    kit.actionSheet.addItem({ id: 'two', text: 'Two', onSelect: () => { calls.push('two'); return Promise.reject(new Error('later')); } });
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);
    page.dispatch(movie.menu, 'click');
    const first = openSheet(page, ITEM_MENU, { closeAfter: null });
    await page.flush();
    assert.deepEqual(rows(first.scroller).slice(3, 6), ['refresh', 'jfkt-one', 'jfkt-two'], 'in the order added');
    page.dispatch(first.scroller.querySelector('[data-id="jfkt-one"]'), 'click');
    await page.advance(1499);
    assert.deepEqual(calls, []);
    await page.advance(1);
    assert.deepEqual(calls, ['one']);
    assert.ok(page.warnings.some((w) => w.includes('boom')));

    first.container.remove();
    page.dispatch(movie.menu, 'click');
    const second = openSheet(page, ITEM_MENU, { closeAfter: 10 });
    await page.flush();
    page.dispatch(second.scroller.querySelector('[data-id="jfkt-two"]'), 'click');
    await page.advance(20);
    assert.deepEqual(calls, ['one', 'two']);
    assert.ok(page.warnings.some((w) => w.includes('later')));
});

test('a list row\'s "⋮" and a right-click (the command event) record their item; a bare command event has no type', async () => {
    const { kit, page } = await setup();
    const seen = [];
    demo(kit, seen);
    const episode = listRow(page, 'episode-1');
    const folder = card(page, 'series-1', { 'data-type': 'Series', 'data-isfolder': 'true' });
    const bare = page.element('div', { 'data-id': 'bare-1' });
    itemsContainer(page, [episode, folder, bare]);

    page.dispatch(episode.menu, 'click');
    await shown(page, ITEM_MENU);
    page.dispatch(folder, 'command', { detail: { command: 'menu' } });
    await shown(page, ITEM_MENU);
    page.dispatch(bare, 'command', { detail: { command: 'menu' } });
    await shown(page, ITEM_MENU);
    page.dispatch(folder, 'command', { detail: { command: 'back' } });
    await shown(page, ITEM_MENU);

    assert.deepEqual(plain(seen.map((c) => [c.itemIds[0], c.surface, c.itemType, c.isFolder])), [
        ['episode-1', 'list', 'Episode', false],
        ['series-1', 'card', 'Series', true],
        ['bare-1', 'list', null, null]
    ], 'another command records nothing');
    assert.deepEqual(seen.map((c) => c.trigger), [episode.menu, folder, bare], 'the row\'s menu button, the item a command came from');
});

test('the details page: the version chosen in its picker, else the hash id; the item\'s kind is unknown there', async () => {
    const { kit, page } = await setup();
    const seen = [];
    demo(kit, seen);
    const versions = page.element('select', { is: 'emby-select', class: 'selectSource detailTrackSelect' });
    versions.value = 'version-2';
    const more = page.element('button', { is: 'emby-button', class: 'button-flat btnMoreCommands detailButton' }, [page.element('span', { class: 'material-icons more_vert' })]);
    const detail = page.element('div', { id: 'itemDetailPage', 'data-role': 'page', class: 'page libraryPage itemDetailPage' }, [versions, more]);
    // A cached, hidden details page with the same id comes first.
    const cached = page.element('div', { id: 'itemDetailPage', class: 'page itemDetailPage hide' }, [page.element('select', { class: 'selectSource', value: 'stale' })]);
    page.document.body.append(cached, detail);
    page.window.location.hash = '#/details?id=movie-1&serverId=server-9';

    page.dispatch(more.firstChild, 'click');
    await shown(page, ITEM_MENU);
    versions.value = '';
    page.dispatch(more, 'click');
    await shown(page, ITEM_MENU);

    assert.deepEqual(plain(seen.map((c) => [c.itemIds[0], c.surface, c.itemType, c.isFolder, c.serverId])), [
        ['version-2', 'details', null, null, 'server-9'],
        ['movie-1', 'details', null, null, 'server-9']
    ]);
    assert.deepEqual(seen.map((c) => c.trigger), [more, more], 'the details page\'s More');
    assert.ok(seen[0].commands.includes('moremediainfo'));
});

// --------------------------------------------------------------------------------------------------- the guards

test('a non-admin\'s menu never qualifies: no admin-only command, or isAdmin() false with one', async () => {
    const viewer = await setup({ admin: false });
    const seen = [];
    demo(viewer.kit, seen);
    const movie = card(viewer.page, 'movie-1');
    itemsContainer(viewer.page, [movie]);
    viewer.page.dispatch(movie.menu, 'click');
    const plainMenu = openSheet(viewer.page, VIEWER_MENU);
    await viewer.page.flush();
    assert.deepEqual(rows(plainMenu.scroller), VIEWER_MENU);
    plainMenu.container.remove();
    viewer.page.dispatch(movie.menu, 'click');
    const marked = openSheet(viewer.page, ITEM_MENU);
    await viewer.page.flush();
    assert.deepEqual(rows(marked.scroller), ITEM_MENU, 'kit.user.isAdmin() answers false');
    assert.equal(seen.length, 0);
    assert.equal(viewer.page.warnings.length, 0);
});

test('sheets nothing recorded opened (sort, filter, audio, now playing) get nothing; another click drops the record', async () => {
    const { kit, page } = await setup();
    const seen = [];
    demo(kit, seen);
    const movie = card(page, 'movie-1');
    const sort = page.element('button', { class: 'btnSort' });
    const nowPlaying = page.element('button', { class: 'btnToggleContextMenu' });
    itemsContainer(page, [movie]);
    page.document.body.append(sort, nowPlaying);

    page.dispatch(sort, 'click');
    const sortMenu = openSheet(page, ['SortName', 'DateCreated']);
    await page.flush();
    sortMenu.container.remove();
    page.dispatch(movie.menu, 'click');
    page.dispatch(nowPlaying, 'click');
    const nowPlayingMenu = openSheet(page, ITEM_MENU);
    await page.flush();
    assert.deepEqual(rows(nowPlayingMenu.scroller), ITEM_MENU);
    nowPlayingMenu.container.remove();

    // A record is taken by the first sheet only.
    page.dispatch(movie.menu, 'click');
    const first = openSheet(page, ['SortName']);
    await page.flush();
    const second = openSheet(page, ITEM_MENU);
    await page.flush();
    assert.deepEqual(rows(first.scroller), ['SortName']);
    assert.deepEqual(rows(second.scroller), ITEM_MENU);
    assert.equal(seen.length, 0);
});

test('a record older than 8 s is dropped, and counts as a miss of kit.actionSheet', async () => {
    const { kit, page } = await setup();
    const seen = [];
    demo(kit, seen);
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);
    page.dispatch(movie.menu, 'click');
    await page.advance(8001);
    const late = openSheet(page, ITEM_MENU);
    await page.flush();

    assert.deepEqual(rows(late.scroller), ITEM_MENU);
    assert.equal(seen.length, 0);
    assert.deepEqual(plain(kit.health.missing()), [{ anchor: 'kit.actionSheet', view: 'menus', count: 1 }]);
    assert.ok(page.warnings.includes('[jfkt] web anchor "kit.actionSheet" missing on menus (web 10.11.5)'));

    // A record that a sheet took, or that a click dropped, is no miss.
    late.container.remove();
    page.dispatch(movie.menu, 'click');
    await shown(page, ITEM_MENU);
    page.dispatch(movie.menu, 'click');
    page.dispatch(page.document.body, 'click');
    await page.advance(9000);
    assert.equal(kit.health.missing()[0].count, 1);
});

test('a sheet without rows is a miss of kit.actionSheetRows; a click inside a dialog records nothing', async () => {
    const { kit, page } = await setup();
    const seen = [];
    demo(kit, seen);
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);
    page.dispatch(movie.menu, 'click');
    openSheet(page, ITEM_MENU, { noScroller: true });
    await page.flush();
    assert.deepEqual(plain(kit.health.missing()), [{ anchor: 'kit.actionSheetRows', view: 'menus', count: 1 }]);

    // The metadata editor's own "⋮" (a card-like [data-id] inside a dialog) belongs to that dialog.
    const inDialog = card(page, 'movie-2');
    page.document.body.append(page.element('div', { class: 'dialogContainer' }, [page.element('div', { class: 'dialog formDialog' }, [inDialog])]));
    await page.flush();
    page.dispatch(inDialog.menu, 'click');
    openSheet(page, ITEM_MENU);
    await page.flush();
    assert.equal(seen.length, 0);
});

test('when(ctx) decides per entry; one that throws is reported and left out; an entry is added to a sheet once', async () => {
    const { kit, page } = await setup();
    kit.actionSheet.addItem({ id: 'movies', text: 'Movies only', when: (ctx) => ctx.itemType === 'Movie' && ctx.isFolder !== true, onSelect: () => {} });
    kit.actionSheet.addItem({ id: 'broken', text: 'Broken', when: () => { throw new Error('when failed'); }, onSelect: () => {} });
    kit.actionSheet.addItem({ id: 'named', text: (ctx) => 'For ' + ctx.itemIds[0], onSelect: () => {} });
    const movie = card(page, 'movie-1');
    const series = card(page, 'series-1', { 'data-type': 'Series', 'data-isfolder': 'true' });
    itemsContainer(page, [movie, series]);

    page.dispatch(series.menu, 'click');
    const seriesMenu = openSheet(page, ITEM_MENU);
    await page.flush();
    assert.deepEqual(rows(seriesMenu.scroller).slice(3, 5), ['refresh', 'jfkt-named']);
    assert.equal(seriesMenu.scroller.querySelector('[data-id="jfkt-named"] .actionSheetItemText').textContent, 'For series-1');
    assert.ok(page.warnings.some((w) => w.includes('when failed')));
    seriesMenu.container.remove();

    page.dispatch(movie.menu, 'click');
    const movieMenu = openSheet(page, ITEM_MENU);
    await page.flush();
    // Another body mutation scans the sheet again: nothing is added twice.
    page.document.body.append(page.element('div', {}));
    await page.flush();
    assert.deepEqual(rows(movieMenu.scroller).slice(3, 6), ['refresh', 'jfkt-movies', 'jfkt-named']);
    assert.equal(movieMenu.scroller.querySelectorAll('[data-id="jfkt-movies"]').length, 1);
});

test('without Refresh metadata the entry goes after the next anchor command, else last', async () => {
    const { kit, page } = await setup();
    demo(kit, []);
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);
    page.dispatch(movie.menu, 'click');
    const noRefresh = openSheet(page, ['resume', 'identify', 'edit', 'delete']);
    await page.flush();
    assert.deepEqual(rows(noRefresh.scroller), ['resume', 'identify', 'jfkt-demo', 'edit', 'delete']);
    noRefresh.container.remove();

    page.dispatch(movie.menu, 'click');
    const custom = openSheet(page, ['resume', 'editlyricsx', 'download', 'editimagesx', 'editx']);
    await page.flush();
    assert.deepEqual(rows(custom.scroller), ['resume', 'editlyricsx', 'download', 'editimagesx', 'editx'], 'no admin-only command: not the item menu');
    custom.container.remove();

    page.dispatch(movie.menu, 'click');
    const onlyEdit = openSheet(page, ['resume', 'download', 'edit']);
    await page.flush();
    assert.deepEqual(rows(onlyEdit.scroller), ['resume', 'download', 'edit', 'jfkt-demo']);
});

test('the sheet is clamped again when our row pushes it off screen (actionSheet.ts\'s rule)', async () => {
    const { kit, page } = await setup();
    demo(kit, []);
    page.window.innerHeight = 800;
    page.window.innerWidth = 1200;
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);

    page.dispatch(movie.menu, 'click');
    const low = openSheet(page, ITEM_MENU, { top: 700, left: 1100 });
    low.sheet.offsetHeight = 200;
    low.sheet.offsetWidth = 160;
    await page.flush();
    assert.equal(low.sheet.style.top, '580px', '700 + 200 - 800 = 100 over: back by 120');
    assert.equal(low.sheet.style.left, '1020px');
    low.container.remove();

    page.dispatch(movie.menu, 'click');
    const fits = openSheet(page, ITEM_MENU, { top: 100, left: 10 });
    fits.sheet.offsetHeight = 300;
    fits.sheet.offsetWidth = 160;
    await page.flush();
    assert.equal(fits.sheet.style.top, '100px');
    assert.equal(fits.sheet.style.left, '10px');
    fits.container.remove();

    page.dispatch(movie.menu, 'click');
    const tall = openSheet(page, ITEM_MENU, { top: 50, left: 10 });
    tall.sheet.offsetHeight = 900;
    await page.flush();
    assert.equal(tall.sheet.style.top, '10px', 'never above 10 px');

    page.dispatch(movie.menu, 'click');
    const fullscreen = openSheet(page, ITEM_MENU);
    fullscreen.sheet.offsetHeight = 900;
    await page.flush();
    assert.equal(fullscreen.sheet.style.top, undefined, 'a sheet without an inline position (TV) is left alone');
});

// ----------------------------------------------------------------------------------------------- the bulk menu

function selection(page, ids, checked) {
    const cards = ids.map((id) => {
        const box = page.element('input', { type: 'checkbox', is: 'emby-checkbox', class: 'chkItemSelect' });
        box.checked = checked.includes(id);
        const node = card(page, id);
        node.querySelector('.cardBox').append(page.element('div', { class: 'itemSelectionPanel' }, [page.element('label', { class: 'checkboxContainer' }, [box])]));
        return node;
    });
    itemsContainer(page, cards);
    const options = page.element('button', { is: 'paper-icon-button-light', class: 'btnSelectionPanelOptions autoSize' }, [page.element('span', { class: 'material-icons more_vert' })]);
    const close = page.element('button', { is: 'paper-icon-button-light', class: 'btnCloseSelectionPanel autoSize' });
    const panel = page.element('div', { class: 'selectionCommandsPanel' }, [close, page.element('h1', { class: 'itemSelectionCount' }), options]);
    page.document.body.append(panel);
    return { options, close, panel };
}

test('the bulk menu: the checked cards\' ids, after Refresh metadata; entries opt in with when(ctx)', async () => {
    const { kit, page } = await setup();
    const seen = [];
    demo(kit, []);
    kit.actionSheet.addItem({
        id: 'bulk',
        icon: 'drive_file_move',
        text: 'Move selected media…',
        when: (ctx) => {
            seen.push(ctx);
            return ctx.isBulk && ctx.itemIds.length > 0;
        },
        onSelect: (ctx) => {
            seen.push(['selected', plain(ctx.itemIds)]);
            ctx.exitSelection();
        }
    });
    const bar = selection(page, ['m1', 'm2', 'm3', 'm4'], ['m1', 'm3', 'm4']);
    let closedSelection = 0;
    bar.close.addEventListener('click', () => closedSelection++);

    page.dispatch(bar.options.firstChild, 'click');
    const sheet = openSheet(page, BULK_MENU, { closeAfter: 30 });
    await page.flush();
    assert.deepEqual(rows(sheet.scroller), ['selectall', 'addtocollection', 'playlist', 'groupvideos', 'markplayed', 'markunplayed', 'refresh', 'jfkt-bulk'],
        'only the bulk entry (the item entry\'s default when is the item menu only)');
    const ctx = seen[0];
    assert.deepEqual(plain({ itemIds: ctx.itemIds, surface: ctx.surface, isBulk: ctx.isBulk, itemType: ctx.itemType, isFolder: ctx.isFolder }),
        { itemIds: ['m1', 'm3', 'm4'], surface: 'bulk', isBulk: true, itemType: null, isFolder: null });
    assert.equal(ctx.trigger, bar.options, 'the selection bar\'s menu button');

    page.dispatch(sheet.scroller.querySelector('[data-id="jfkt-bulk"]'), 'click');
    assert.equal(sheet.state.selected, 'jfkt-bulk');
    await page.advance(30);
    assert.deepEqual(seen[1], ['selected', ['m1', 'm3', 'm4']]);
    assert.equal(closedSelection, 1, 'exitSelection clicked the bar\'s close button');
});

test('the bulk menu of a non-admin gets nothing; the item menu\'s exitSelection does nothing', async () => {
    // Jellyfin 12 offers groupvideos only for video items, so the bulk menu is told by selectall and the user's role.
    const { kit, page } = await setup({ admin: false });
    const seen = [];
    kit.actionSheet.addItem({ id: 'any', text: 'Any', when: (ctx) => { seen.push(ctx); return true; }, onSelect: (ctx) => ctx.exitSelection() });
    const bar = selection(page, ['m1', 'm2'], ['m1']);
    let closedSelection = 0;
    bar.close.addEventListener('click', () => closedSelection++);
    page.dispatch(bar.options, 'click');
    const viewerSheet = openSheet(page, VIEWER_BULK_MENU);
    await page.flush();
    assert.deepEqual(rows(viewerSheet.scroller), VIEWER_BULK_MENU);
    assert.equal(seen.length, 0);
    viewerSheet.container.remove();
});

test('the item menu\'s exitSelection does nothing, and a sheet with selectall after a card\'s menu is not the item menu', async () => {
    const { kit, page } = await setup();
    const seen = [];
    kit.actionSheet.addItem({ id: 'any', text: 'Any', when: (ctx) => { seen.push(ctx); return true; }, onSelect: (ctx) => ctx.exitSelection() });
    const bar = selection(page, ['m1', 'm2'], ['m1']);
    let closedSelection = 0;
    bar.close.addEventListener('click', () => closedSelection++);

    // The item menu has no selectall; a sheet with selectall after a card's menu is not the item menu. (The card's
    // command event: in selection mode its click records nothing, below.)
    const movie = page.document.querySelector('[data-id="m2"]');
    page.dispatch(movie, 'command', { detail: { command: 'menu' } });
    const wrong = openSheet(page, BULK_MENU);
    await page.flush();
    assert.deepEqual(rows(wrong.scroller), BULK_MENU);
    wrong.container.remove();

    page.dispatch(movie, 'command', { detail: { command: 'menu' } });
    const itemMenu = openSheet(page, ITEM_MENU, { closeAfter: 5 });
    await page.flush();
    page.dispatch(itemMenu.scroller.querySelector('[data-id="jfkt-any"]'), 'click');
    await page.advance(10);
    assert.equal(seen.length, 1);
    assert.equal(closedSelection, 0);
});

// ----------------------------------------------------------------------------------------------- the API itself

test('addItem: the id is checked, added once, removable; the listeners wait for start() and come once', async () => {
    const { kit, page } = await setup({ start: false });
    assert.throws(() => kit.actionSheet.addItem({ id: 'Bad Id', onSelect: () => {} }), /a-z, 0-9 and dashes/);
    assert.throws(() => kit.actionSheet.addItem({ id: 'x' }), /onSelect/);
    assert.throws(() => kit.actionSheet.addItem({ id: 'x', when: 'yes', onSelect: () => {} }), /onSelect/);
    const seen = [];
    const early = demo(kit, seen);
    assert.throws(() => demo(kit, seen), /added already/);
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);
    page.dispatch(movie.menu, 'click');
    await shown(page, ITEM_MENU);
    assert.equal(seen.length, 0, 'nothing before start()');

    kit.start();
    await page.advance(60);
    kit.actionSheet.addItem({ id: 'second', text: 'Second', onSelect: () => {} });
    const captures = page.document._listeners.filter((l) => l.capture && (l.type === 'click' || l.type === 'command'));
    assert.deepEqual(captures.map((l) => l.type), ['click', 'command'], 'one capture listener each, whatever the number of entries');
    page.dispatch(movie.menu, 'click');
    const sheet = openSheet(page, ITEM_MENU);
    await page.flush();
    assert.equal(seen.length, 1);
    assert.deepEqual(rows(sheet.scroller).slice(3, 6), ['refresh', 'jfkt-demo', 'jfkt-second']);

    early.remove();
    early.remove();
    sheet.container.remove();
    assert.deepEqual(plain(kit.actionSheet.items()), ['jfkt-second']);
    page.dispatch(movie.menu, 'click');
    const after = openSheet(page, ITEM_MENU);
    await page.flush();
    assert.deepEqual(rows(after.scroller).slice(3, 5), ['refresh', 'jfkt-second']);
});

test('the listeners only read: they neither stop nor prevent the trigger\'s click', async () => {
    const { kit, page } = await setup();
    demo(kit, []);
    const movie = card(page, 'movie-1');
    const container = itemsContainer(page, [movie]);
    let reached = 0;
    container.addEventListener('click', (event) => {
        reached++;
        assert.equal(event.defaultPrevented, false);
    });
    page.dispatch(movie.menu, 'click', { cancelable: true });
    assert.equal(reached, 1);
});

test('the row is upgraded through CustomElements.upgradeSubtree when there is one', async () => {
    const upgraded = [];
    const { kit, page } = await setup();
    page.context.CustomElements = { upgradeSubtree: (node) => upgraded.push(node.getAttribute('data-id')) };
    demo(kit, []);
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);
    page.dispatch(movie.menu, 'click');
    openSheet(page, ITEM_MENU);
    await page.flush();
    assert.deepEqual(upgraded, ['jfkt-demo']);
});

// ----------------------------------------------------------------------------------------------- nativeDialog

function fakeDialogHelper(page) {
    const log = [];
    const helper = {
        createDialog: (options) => {
            log.push(['create', plain(options)]);
            return page.element('div', { class: 'focuscontainer dialog dialog-fixedSize dialog-small', 'data-history': 'true', 'data-removeonclose': 'true' });
        },
        open: (dlg) => {
            log.push(['open']);
            page.document.body.append(page.element('div', { class: 'dialogContainer' }, [dlg]));
            return Promise.resolve();
        },
        close: (dlg) => {
            log.push(['close']);
            page.context.setTimeout(() => {
                dlg.dispatchEvent(new page.Event('close', { bubbles: false }));
                dlg.parentNode.remove();
            }, 20);
        }
    };
    page.context.Dashboard = { dialogHelper: helper };
    return log;
}

test('nativeDialog opens a dialogHelper dialog shaped like jellyfin-web\'s form dialogs; its close event closes it', async () => {
    const { kit, page } = await setup();
    const log = fakeDialogHelper(page);
    const dialog = kit.nativeDialog({ title: 'Move media' });
    let closed = 0;
    dialog.onClose(() => closed++);
    dialog.body.append(page.element('p', {}, []));
    await page.flush();

    assert.equal(dialog.native, true);
    assert.deepEqual(log, [['create', { size: 'small', removeOnClose: true, scrollY: false }], ['open']]);
    const panel = dialog.panel;
    assert.ok(panel.classList.contains('formDialog') && panel.classList.contains('jfkt-native-dialog'));
    assert.equal(panel.parentNode.classList.contains('dialogContainer'), true);
    const title = panel.querySelector('.formDialogHeader h3.formDialogHeaderTitle');
    assert.equal(title.textContent, 'Move media');
    assert.equal(panel.getAttribute('aria-labelledby'), title.id);
    assert.equal(panel.getAttribute('role'), 'dialog');
    assert.ok(panel.querySelector('.formDialogContent.smoothScrollY > .dialogContentInner.dialog-content-centered') === dialog.body);
    const back = panel.querySelector('.formDialogHeader button.btnCancel');
    assert.equal(back.getAttribute('is'), 'paper-icon-button-light');

    page.dispatch(back, 'click');
    assert.deepEqual(log.slice(2), [['close']]);
    assert.equal(closed, 0, 'not before the close event');
    await page.advance(20);
    assert.equal(closed, 1);
    dialog.close();
    assert.equal(log.filter((l) => l[0] === 'close').length, 1, 'closed once');

    // Back (dialogHelper's history pop) closes it too: the close event alone.
    const second = kit.nativeDialog('Second');
    let secondClosed = 0;
    second.onClose(() => secondClosed++);
    second.panel.dispatchEvent(new page.Event('close', { bubbles: false }));
    assert.equal(secondClosed, 1);
});

test('nativeDialog: trapFocus\'s Tab order takes a details\' summary, as the browser\'s does', async () => {
    const { kit, page } = await setup();
    fakeDialogHelper(page);
    const dialog = kit.nativeDialog({ title: 'Move media', trapFocus: true });
    const summary = page.element('summary', {}, []);
    const first = page.element('select');
    const last = page.element('button');
    dialog.body.append(page.element('details', {}, [summary, page.element('ul')]), first, last);
    const back = dialog.panel.querySelector('.formDialogHeader button');
    back.focus();
    page.key(back, 'Tab');
    assert.equal(page.document.activeElement, summary, 'from the header\'s Close to the summary');
    page.key(summary, 'Tab');
    assert.equal(page.document.activeElement, first);
    page.key(first, 'Tab', { shiftKey: true });
    assert.equal(page.document.activeElement, summary);
});

test('nativeDialog: autoFocus false asks dialogHelper not to move the focus after its entry animation', async () => {
    const { kit, page } = await setup();
    const log = fakeDialogHelper(page);
    kit.nativeDialog({ title: 'Move media', autoFocus: false });
    kit.nativeDialog({ title: 'Other', autoFocus: true });
    await page.flush();
    assert.deepEqual(log.filter((l) => l[0] === 'create').map((l) => l[1]), [
        { size: 'small', removeOnClose: true, scrollY: false, autoFocus: false },
        { size: 'small', removeOnClose: true, scrollY: false }
    ]);
});

test('nativeDialog: trapFocus keeps Tab inside and closes on Escape, on desktop too, while its dialog is the topmost', async () => {
    // dialogHelper has no Tab trap and closes on Escape only on the TV layout.
    const { kit, page } = await setup();
    const log = fakeDialogHelper(page);
    let pageSaw = 0;
    page.document.addEventListener('keydown', () => pageSaw++, true);
    const plainDialog = kit.nativeDialog({ title: 'Plain' });
    const plainButton = page.element('button');
    plainDialog.body.append(plainButton);
    plainButton.focus();
    page.key(plainButton, 'Escape');
    assert.equal(log.filter((l) => l[0] === 'close').length, 0, 'without trapFocus Escape is jellyfin-web\'s (TV only)');
    assert.equal(pageSaw, 1);
    plainDialog.close();
    await page.advance(20);

    const dialog = kit.nativeDialog({ title: 'Move media', trapFocus: true });
    let closed = 0;
    dialog.onClose(() => closed++);
    const first = page.element('select');
    const last = page.element('button');
    dialog.body.append(first, last);
    const back = dialog.panel.querySelector('.formDialogHeader button');
    pageSaw = 0;

    last.focus();
    assert.equal(page.key(last, 'Tab'), false, 'prevented');
    assert.equal(page.document.activeElement, back, 'Tab from the last wraps to the header\'s Close');
    page.key(back, 'Tab');
    assert.equal(page.document.activeElement, first);
    page.key(first, 'Tab', { shiftKey: true });
    assert.equal(page.document.activeElement, back);
    page.key(back, 'Tab', { shiftKey: true });
    assert.equal(page.document.activeElement, last, 'Shift+Tab from the first wraps to the last');
    page.document.activeElement.blur();
    page.key(page.document.body, 'Tab');
    assert.equal(page.document.activeElement, back, 'from the page\'s body, into the dialog');
    assert.equal(pageSaw, 0, 'Tab never reached the page (or Jellyfin Enhanced\'s capture listener)');

    // Other keys, and a composition's Escape, are the page's.
    page.key(first, 'ArrowDown');
    page.key(first, 'Escape', { isComposing: true });
    assert.equal(pageSaw, 2);
    assert.equal(log.filter((l) => l[0] === 'close').length, 1);

    // A jellyfin-web dialog over it keeps its own keys.
    const over = page.element('div', { class: 'dialogContainer' }, [page.element('div', { class: 'dialog' }, [page.element('button')])]);
    page.document.body.append(over);
    page.key(last, 'Escape');
    assert.equal(closed, 0);
    over.remove();

    page.key(last, 'Escape');
    assert.deepEqual(log.slice(-1), [['close']]);
    await page.advance(20);
    assert.equal(closed, 1);
    pageSaw = 0;
    page.key(page.document.body, 'Tab');
    page.key(page.document.body, 'Escape');
    assert.equal(pageSaw, 2, 'the listener went with the dialog');
});

// returnFocus gives the focus back to the menu's trigger when dialogHelper leaves it nowhere.
test('nativeDialog: returnFocus takes the focus once it closed, unless dialogHelper put it somewhere; without it, as before', async () => {
    const { kit, page } = await setup();
    fakeDialogHelper(page);
    const trigger = page.element('button', { class: 'itemAction' });
    const other = page.element('button');
    page.document.body.append(page.element('div', {}, [trigger, other]));

    const dialog = kit.nativeDialog({ title: 'Move media', trapFocus: true, returnFocus: trigger });
    const inside = page.element('button');
    dialog.body.append(inside);
    inside.focus();
    dialog.close();
    await page.advance(40);
    assert.equal(page.document.activeElement, trigger, 'the focus went with the dialog; back to the trigger');

    // dialogHelper (or the caller) put it somewhere: left there.
    const second = kit.nativeDialog({ title: 'Move media', returnFocus: () => trigger });
    second.onClose(() => other.focus());
    second.close();
    await page.advance(40);
    assert.equal(page.document.activeElement, other);

    // A trigger that is gone takes nothing; without returnFocus nothing moves the focus.
    trigger.remove();
    const third = kit.nativeDialog({ title: 'Move media', returnFocus: trigger });
    page.document.activeElement.blur();
    third.close();
    await page.advance(40);
    assert.equal(page.document.activeElement, page.document.body);
    const fourth = kit.nativeDialog({ title: 'Plain' });
    const own = page.element('button');
    fourth.body.append(own);
    own.focus();
    fourth.close();
    await page.advance(40);
    assert.equal(page.document.activeElement === other || page.document.activeElement === trigger, false, 'nothing chose a place for it');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

// escapeClearsText, with trapFocus: Escape in a text field with text empties it first.
test('nativeDialog: escapeClearsText empties a text field on the first Escape and closes on the next; without it Escape closes', async () => {
    const { kit, page } = await setup();
    const log = fakeDialogHelper(page);
    const dialog = kit.nativeDialog({ title: 'Move media', trapFocus: true, escapeClearsText: true });
    const filter = page.element('input', { type: 'search' });
    const name = page.element('input', { type: 'text' });
    const box = page.element('input', { type: 'checkbox' });
    dialog.body.append(filter, name, box);
    const typed = [];
    filter.addEventListener('input', () => typed.push(['filter', filter.value]));
    name.addEventListener('input', () => typed.push(['name', name.value]));
    let pageSaw = 0;
    page.document.addEventListener('keydown', () => pageSaw++, true);

    filter.value = 'heat';
    filter.focus();
    assert.equal(page.key(filter, 'Escape'), false, 'prevented: the browser doesn\'t clear it as well');
    assert.equal(filter.value, '');
    assert.deepEqual(typed, [['filter', '']], 'an input event, as typing would');
    assert.equal(log.filter((l) => l[0] === 'close').length, 0, 'still open');
    name.value = 'New';
    name.focus();
    page.key(name, 'Escape');
    assert.equal(name.value, '');
    assert.equal(log.filter((l) => l[0] === 'close').length, 0);
    assert.equal(pageSaw, 0, 'those Escapes went no further');
    page.key(name, 'Escape');
    assert.equal(log.filter((l) => l[0] === 'close').length, 1, 'the second Escape, on an empty field, closes');
    await page.advance(20);

    // A checkbox or a button has no text to clear; without the option a field's text doesn't keep it open.
    const second = kit.nativeDialog({ title: 'Move media', trapFocus: true, escapeClearsText: true });
    const check = page.element('input', { type: 'checkbox' });
    second.body.append(check);
    check.focus();
    page.key(check, 'Escape');
    assert.equal(log.filter((l) => l[0] === 'close').length, 2);
    await page.advance(20);
    const third = kit.nativeDialog({ title: 'Other', trapFocus: true });
    const field = page.element('input', { type: 'text' });
    third.body.append(field);
    field.value = 'text';
    field.focus();
    page.key(field, 'Escape');
    assert.equal(field.value, 'text');
    assert.equal(log.filter((l) => l[0] === 'close').length, 3, 'Escape closes, as before');
});

test('nativeDialog without dialogHelper is the kit\'s own dialog', async () => {
    const { kit, page } = await setup();
    const dialog = kit.nativeDialog({ title: 'Move media' });
    assert.equal(dialog.native, false);
    assert.ok(dialog.panel.classList.contains('jfkt-panel'));
    let closed = 0;
    dialog.onClose(() => closed++);
    dialog.close();
    assert.equal(closed, 1);
    assert.equal(page.document.querySelector('.jfkt-backdrop'), null);
});

test('two kits of one prefix on a page (the ClientScript\'s and an AdminScript\'s) add an entry once', async () => {
    const { kit, page, exports } = await setup();
    const other = exports.createKit(BUILD, '', { anchors: anchorDeclarations() });
    other.start();
    await page.advance(60);
    demo(kit, []);
    demo(other, []);
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);
    page.dispatch(movie.menu, 'click');
    const sheet = openSheet(page, ITEM_MENU);
    await page.flush();
    assert.deepEqual(rows(sheet.scroller).slice(3, 6), ['refresh', 'jfkt-demo', 'moremediainfo']);
});

// ------------------------------------------------------------------- clicks while the sheet closes, selection mode

test('a second click while the sheet closes, on our row or another of ours, runs no second onSelect', async () => {
    // The review's double click: the sheet's rows stay ~100 ms after the first click, and a second click 40 ms later
    // reached our row's listener again, so onSelect ran twice after the one close event.
    const { kit, page } = await setup();
    const calls = [];
    kit.actionSheet.addItem({ id: 'one', text: 'One', onSelect: (ctx) => calls.push(['one', ctx.itemIds[0]]) });
    kit.actionSheet.addItem({ id: 'two', text: 'Two', onSelect: (ctx) => calls.push(['two', ctx.itemIds[0]]) });
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);
    page.dispatch(movie.menu, 'click');
    const sheet = openSheet(page, ITEM_MENU, { closeAfter: 300 });
    await page.flush();
    const one = sheet.scroller.querySelector('[data-id="jfkt-one"]');
    page.dispatch(one, 'click');
    await page.advance(40);
    assert.equal(one.isConnected, true, 'the row is still there while the sheet closes');
    page.dispatch(one, 'click');
    page.dispatch(sheet.scroller.querySelector('[data-id="jfkt-two"]'), 'click');
    await page.advance(260);
    assert.deepEqual(calls, [['one', 'movie-1']], 'once, after the close event');
    await page.advance(3000);
    assert.deepEqual(calls, [['one', 'movie-1']], 'the fallback timers add nothing');

    // The next sheet is a choice of its own.
    sheet.container.remove();
    page.dispatch(movie.menu, 'click');
    const next = openSheet(page, ITEM_MENU, { closeAfter: 10 });
    await page.flush();
    page.dispatch(next.scroller.querySelector('[data-id="jfkt-two"]'), 'click');
    await page.advance(20);
    assert.deepEqual(calls, [['one', 'movie-1'], ['two', 'movie-1']]);
});

test('in selection mode a card\'s "⋮" records nothing and is no miss; the selection menu still records', async () => {
    // The review: in selection mode a card's click selects it and no sheet opens, so a recorded "⋮" was a miss of
    // kit.actionSheet 8 s later.
    const { kit, page } = await setup();
    const seen = [];
    kit.actionSheet.addItem({ id: 'bulk', text: 'Bulk', when: (ctx) => { seen.push(ctx); return ctx.isBulk; }, onSelect: () => {} });
    const bar = selection(page, ['m1', 'm2'], ['m1']);
    const m2 = page.document.querySelector('[data-id="m2"]');
    page.dispatch(m2.menu.firstChild, 'click');
    await page.advance(9000);
    assert.deepEqual(plain(kit.health.missing()), [], 'no miss');

    // A record made before is dropped by that click, as by any other.
    page.dispatch(bar.options, 'click');
    page.dispatch(m2.menu, 'click');
    const after = openSheet(page, BULK_MENU);
    await page.flush();
    assert.deepEqual(rows(after.scroller), BULK_MENU);
    assert.equal(seen.length, 0);
    after.container.remove();

    page.dispatch(bar.options.firstChild, 'click');
    const bulk = openSheet(page, BULK_MENU);
    await page.flush();
    assert.deepEqual(rows(bulk.scroller), [...BULK_MENU, 'jfkt-bulk']);
    assert.deepEqual(plain(seen[0].itemIds), ['m1']);
    bulk.container.remove();

    // Out of selection mode the card's "⋮" records again (and a sheet that never follows is a miss).
    bar.panel.remove();
    page.dispatch(m2.menu, 'click');
    await page.advance(8001);
    assert.deepEqual(plain(kit.health.missing()), [{ anchor: 'kit.actionSheet', view: 'menus', count: 1 }]);
});

test('a sheet gone before the admin check answers gets nothing (the isConnected check)', async () => {
    const { kit, page } = await setup();
    const seen = [];
    demo(kit, seen);
    const movie = card(page, 'movie-1');
    itemsContainer(page, [movie]);
    page.dispatch(movie.menu, 'click');
    await page.advance(10);
    const sheet = openSheet(page, ITEM_MENU);
    // After the observer's turn, before the admin check's answer: the sheet closed (Escape) at once.
    queueMicrotask(() => sheet.container.remove());
    await page.flush();
    assert.equal(sheet.sheet.isConnected, false);
    assert.deepEqual(rows(sheet.scroller), ITEM_MENU, 'nothing added to a sheet that is gone');
    assert.equal(seen.length, 0, 'when(ctx) not asked');
    assert.deepEqual(plain(kit.health.missing()), []);
});

test('the bulk menu\'s ids: a card shown twice (two rows of one page) counts once', async () => {
    const { kit, page } = await setup();
    const seen = [];
    kit.actionSheet.addItem({ id: 'bulk', text: 'Bulk', when: (ctx) => { seen.push(plain(ctx.itemIds)); return ctx.isBulk; }, onSelect: () => {} });
    const bar = selection(page, ['m1', 'm2', 'm1', 'm3'], ['m1', 'm3']);
    page.dispatch(bar.options, 'click');
    openSheet(page, BULK_MENU);
    await page.flush();
    assert.deepEqual(seen, [['m1', 'm3']]);
});

test('an entry without when(ctx) is the item menu\'s only: never in the bulk menu', async () => {
    const { kit, page } = await setup();
    const calls = [];
    kit.actionSheet.addItem({ id: 'plain', text: 'Plain', onSelect: (ctx) => calls.push(ctx.isBulk) });
    const bar = selection(page, ['m1', 'm2'], ['m1', 'm2']);
    page.dispatch(bar.options, 'click');
    const bulk = openSheet(page, BULK_MENU);
    await page.flush();
    assert.deepEqual(rows(bulk.scroller), BULK_MENU, 'not in the bulk menu');
    bulk.container.remove();

    bar.panel.remove();
    page.dispatch(page.document.querySelector('[data-id="m2"]').menu, 'click');
    const item = openSheet(page, ITEM_MENU, { closeAfter: 5 });
    await page.flush();
    assert.deepEqual(rows(item.scroller).slice(3, 5), ['refresh', 'jfkt-plain'], 'in the item menu');
    page.dispatch(item.scroller.querySelector('[data-id="jfkt-plain"]'), 'click');
    await page.advance(10);
    assert.deepEqual(calls, [false]);
});

test('a sheet still closing never takes the next record: the next sheet does (the seen list)', async () => {
    const { kit, page } = await setup();
    const seen = [];
    demo(kit, seen);
    const first = card(page, 'movie-1');
    const second = card(page, 'movie-2');
    itemsContainer(page, [first, second]);
    page.dispatch(first.menu, 'click');
    const old = openSheet(page, ITEM_MENU, { closeAfter: null });
    await page.flush();
    assert.deepEqual(rows(old.scroller).slice(3, 5), ['refresh', 'jfkt-demo']);

    // The old sheet's container is still in the body (closing) when the next "⋮" opens its own sheet.
    page.dispatch(second.menu, 'click');
    const next = openSheet(page, ITEM_MENU);
    await page.flush();
    assert.deepEqual(rows(next.scroller).slice(3, 5), ['refresh', 'jfkt-demo'], 'the next sheet has the entry');
    assert.deepEqual(plain(seen.map((c) => [c.itemIds[0], c.sheet === next.sheet])), [['movie-1', false], ['movie-2', true]]);
});

test('nativeDialog: a second close event (a history pop after close()) runs the close handlers once', async () => {
    const { kit, page } = await setup();
    const log = fakeDialogHelper(page);
    const dialog = kit.nativeDialog({ title: 'Move media' });
    let closed = 0;
    dialog.onClose(() => closed++);
    await page.flush();
    dialog.panel.dispatchEvent(new page.Event('close', { bubbles: false }));
    dialog.panel.dispatchEvent(new page.Event('close', { bubbles: false }));
    assert.equal(closed, 1);
    dialog.close();
    assert.equal(log.filter((l) => l[0] === 'close').length, 0, 'closed already: dialogHelper.close is not called');
});
