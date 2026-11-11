// Move media's web client in the kit's fake page. WebTests runs it with the built ClientScript:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jflm node --test plugins/LibraryMaintenance/tests/Jellyfin.Plugin.LibraryMaintenance.Tests/Web/move.test.mjs
// jellyfin-web's action sheet and dialogHelper are played as the kit's own tests play them: a new
// div.actionSheet in a div.dialogContainer appended to <body>; a click on a row stores its data-id and closes the sheet;
// close (not bubbling) after the history pop.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage, plain } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');

const ITEM_MENU = ['resume', 'playallfromhere', 'addtocollection', 'refresh', 'moremediainfo', 'identify', 'edit', 'editimages', 'delete'];
const SERIES_MENU = ['shuffle', 'addtocollection', 'playlist', 'refresh', 'identify', 'edit', 'editimages', 'delete'];
const VIEWER_MENU = ['resume', 'playallfromhere', 'markplayed', 'moremediainfo'];
const BULK_MENU = ['selectall', 'addtocollection', 'playlist', 'groupvideos', 'markplayed', 'markunplayed', 'refresh'];
const ROW_CLASS = 'listItem listItem-button actionSheetMenuItem emby-button';

const MOVIE = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const MOVIE_DASHED = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const OTHER = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const MOVIES = '11111111-1111-1111-1111-111111111111';
const BIG = '22222222-2222-2222-2222-222222222222';
const MOVIES2 = '33333333-3333-3333-3333-333333333333';

const ADMIN_CAPS = { IsAdmin: true, MoveEnabled: true, MoveSupported: true, CanPlanMove: true, CanMove: false, ShowMoveInItemMenu: true, ShowMoveDetailPageButton: false, ShowMoveInTvLayout: false };

function looseSource(overrides) {
    return Object.assign({
        Item: { Id: MOVIE_DASHED, Name: 'Plain Loose', Year: 2005, Kind: 'Movie' },
        Library: { Id: MOVIES, Name: 'Movies', CollectionType: 'movies' },
        LocationPath: 'C:\\media\\Movies',
        Unit: {
            Kind: 'FileSet',
            Root: 'C:\\media\\Movies',
            Primary: 'C:\\media\\Movies\\Plain Loose (2005).mkv',
            Files: [{ Rel: 'Plain Loose (2005).mkv', Bytes: 1048576 }, { Rel: 'Plain Loose (2005).srt', Bytes: 2048 }, { Rel: 'Plain Loose (2005).nfo', Bytes: 1024 }],
            Dirs: [],
            TotalBytes: 1051648,
            Versions: [],
            Parts: [],
            Extras: []
        },
        Retargeted: null
    }, overrides || {});
}

function folderSource() {
    const root = 'C:\\media\\Movies 2\\Versions Film (2015)';
    return {
        Item: { Id: MOVIE_DASHED, Name: 'Versions Film', Year: 2015, Kind: 'Movie' },
        Library: { Id: MOVIES2, Name: 'Movies 2', CollectionType: 'movies' },
        LocationPath: 'C:\\media\\Movies 2',
        Unit: {
            Kind: 'Folder',
            Root: root,
            Primary: root + '\\Versions Film (2015) - 1080p.mkv',
            Files: [
                { Rel: 'Versions Film (2015) - 1080p.mkv', Bytes: 2048 }, { Rel: 'Versions Film (2015) - 720p.mkv', Bytes: 1024 },
                { Rel: 'poster.jpg', Bytes: 10 }, { Rel: 'trailers\\Teaser.mkv', Bytes: 100 }
            ],
            Dirs: ['trailers'],
            TotalBytes: 3182,
            Versions: [root + '\\Versions Film (2015) - 720p.mkv'],
            Parts: [],
            Extras: [root + '\\trailers\\Teaser.mkv']
        },
        Retargeted: 'This version belongs to Versions Film (2015); the whole folder moves.'
    };
}

function destinations() {
    return [
        { LibraryId: MOVIES, Name: 'Movies', CollectionType: 'movies', IsSource: true, Locations: [{ Index: 0, Path: 'C:\\media\\Movies', Label: null, Reachable: true, FreeBytes: 500 * 1024 * 1024 * 1024, SameVolumeAsSource: true, Selectable: true, Reason: null }] },
        { LibraryId: BIG, Name: 'Big Folders', CollectionType: 'movies', IsSource: false, Locations: [{ Index: 0, Path: 'C:\\media\\Big Folders', Label: null, Reachable: true, FreeBytes: null, SameVolumeAsSource: true, Selectable: true, Reason: null }] },
        { LibraryId: MOVIES2, Name: 'Movies 2', CollectionType: 'movies', IsSource: false, Locations: [{ Index: 0, Path: 'C:\\media\\Movies 2', Label: null, Reachable: true, FreeBytes: null, SameVolumeAsSource: false, Selectable: false, Reason: 'cross-volume-later' }] }
    ];
}

// GET /Move/Folders: Big Folders has 450 movie folders; Movies has a plain folder, a link, an ignored one and one movie's.
function folders(entry) {
    const q = entry.query;
    const start = Number(q.startIndex || 0);
    const limit = Number(q.limit || 200);
    let all;
    if (q.libraryId === BIG) {
        all = Array.from({ length: 450 }, (_, i) => {
            const name = 'Big Folder ' + String(i + 1).padStart(4, '0') + ' (1951)';
            return { Name: name, RelativePath: name, Kind: 'MovieFolder', Selectable: false, Reason: 'This is the folder of ' + name + '; one movie\'s folder can\'t hold another.' };
        });
    } else if (!q.relativePath) {
        all = [
            { Name: 'Collection', RelativePath: 'Collection', Kind: 'Plain', Selectable: true, Reason: null },
            { Name: 'Linked', RelativePath: 'Linked', Kind: 'ReparsePoint', Selectable: false, Reason: 'This is a junction or symbolic link; Move doesn\'t follow links.' },
            { Name: 'extras', RelativePath: 'extras', Kind: 'Ignored', Selectable: false, Reason: 'Jellyfin ignores this folder.' },
            { Name: '<b>Bold</b> (1999)', RelativePath: '<b>Bold</b> (1999)', Kind: 'MovieFolder', Selectable: false, Reason: 'This is the folder of <b>Bold</b>; one movie\'s folder can\'t hold another.' }
        ];
    } else {
        all = [{ Name: 'Inner', RelativePath: q.relativePath + '\\Inner', Kind: 'Plain', Selectable: true, Reason: null }];
    }

    const matching = q.filter ? all.filter((f) => f.Name.toLowerCase().includes(String(q.filter).toLowerCase())) : all;
    return { Items: matching.slice(start, start + limit), TotalRecordCount: matching.length, StartIndex: start, Path: 'C:\\media\\x' };
}

function plan(overrides) {
    return Object.assign({
        PlanId: 'p1',
        PlanHash: 'a'.repeat(64),
        ExpiresUtc: null,
        Method: 'Rename',
        Units: [{
            ItemId: MOVIE_DASHED,
            Title: 'Plain Loose (2005)',
            Kind: 'FileSet',
            From: 'C:\\media\\Movies\\Plain Loose (2005).mkv',
            To: 'C:\\media\\Movies\\Collection\\Plain Loose (2005).mkv',
            Files: 3,
            Bytes: 1051648,
            WrapFolderName: null,
            CreatedFolders: [],
            PredictedIds: []
        }],
        Volumes: [{ Root: 'C:\\', FreeBytes: 1024 * 1024 * 1024, RequiredBytes: 0 }],
        EstimatedSeconds: null,
        Preserved: { UserDataUsers: 2, Favorites: 1, ResumePoints: 1, DateCreated: '2021-03-04T10:00:00Z', Collections: ['Sci-fi'], Playlists: [], CarryOverMode: 'AllEditableFields', LockedFields: ['Overview'], InternalImages: 3, InternalSubtitles: null, TrickplayResolutions: null, HookConsumers: [] },
        Regenerated: ['Media info and chapters (probed again)'],
        Warnings: [],
        Blockers: []
    }, overrides || {});
}

function fakeDialogHelper(page) {
    const log = [];
    page.context.Dashboard = {
        dialogHelper: {
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
                    if (dlg.parentNode) {
                        dlg.parentNode.remove();
                    }
                }, 20);
            }
        }
    };
    return log;
}

// The page with the ClientScript running and a signed-in user (an admin unless options.admin is false).
async function start(options) {
    options = options || {};
    const page = createPage();
    if (options.tv) {
        page.document.documentElement.classList.add('layout-tv');
    }

    page.respond('GET', '/LibraryMaintenance/Capabilities', Object.assign({}, ADMIN_CAPS, options.caps || {}));
    page.respond('POST', '/LibraryMaintenance/Diagnostics/ClientHealth', null, 204);
    page.respond('GET', '/LibraryMaintenance/Move/Eligibility', options.eligibility || ((entry) => entry.query.ids.split(',').map((id) => ({ ItemId: id, Eligible: true, UnitKind: 'FileSet' }))));
    page.respond('GET', '/LibraryMaintenance/Move/Source', options.source || looseSource());
    page.respond('GET', '/LibraryMaintenance/Move/Destinations', options.destinations || destinations());
    page.respond('GET', '/LibraryMaintenance/Move/Folders', options.folders || folders);
    page.respond('POST', '/LibraryMaintenance/Move/Plan', options.plan || (() => plan()));
    page.dialogs = fakeDialogHelper(page);
    page.replaced = [];
    page.context.location.replace = (href) => page.replaced.push(href);
    page.run(source, file);
    await page.advance(50);
    page.installApiClient({ userId: options.userId || 'admin-1', admin: options.admin !== false });
    await page.advance(400);
    return page;
}

function itemsContainer(page, children) {
    const container = page.element('div', { class: 'itemsContainer vertical-wrap' }, children);
    container.addEventListener('click', (event) => event.stopPropagation());
    page.document.body.append(container);
    return container;
}

function card(page, id, attrs) {
    const menu = page.element('button', { is: 'paper-icon-button-light', class: 'cardOverlayButton itemAction', 'data-action': 'menu' });
    const node = page.element('div', Object.assign({ class: 'card portraitCard', 'data-id': id, 'data-type': 'Movie', 'data-isfolder': 'false', 'data-serverid': 'server-1' }, attrs || {}), [menu]);
    node.menu = menu;
    return node;
}

function openSheet(page, commands) {
    const sheet = page.element('div', { class: 'focuscontainer dialog actionsheet-not-fullscreen actionSheet', 'data-history': 'true', 'data-removeonclose': 'true' });
    const scroller = page.element('div', { class: 'actionSheetScroller scrollY' });
    commands.forEach((command) => {
        scroller.append(page.element('button', { is: 'emby-button', type: 'button', class: ROW_CLASS, 'data-id': command }, [
            page.element('span', { class: 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons play_arrow', 'aria-hidden': 'true' }),
            page.element('div', { class: 'listItemBody actionsheetListItemBody' }, [page.element('div', { class: 'listItemBodyText actionSheetItemText' })])
        ]));
    });
    sheet.append(page.element('div', { class: 'actionSheetContent' }, [scroller]));
    const container = page.element('div', { class: 'dialogContainer' }, [sheet]);
    sheet.addEventListener('click', (event) => {
        const row = event.target.closest('.actionSheetMenuItem');
        if (!row) {
            return;
        }

        page.context.setTimeout(() => {
            sheet.dispatchEvent(new page.Event('close', { bubbles: false }));
            page.context.setTimeout(() => container.remove(), 100);
        }, 40);
    });
    page.document.body.append(container);
    return { sheet, scroller, container };
}

function rows(scroller) {
    return scroller.querySelectorAll('.actionSheetMenuItem').map((row) => row.getAttribute('data-id'));
}

// A card's "⋮" and the sheet it opens, after the kit filled it.
async function menuOf(page, node, commands) {
    page.dispatch(node.menu, 'click');
    const sheet = openSheet(page, commands || ITEM_MENU);
    await page.flush();
    return sheet;
}

async function closeSheet(page, sheet) {
    sheet.container.remove();
    await page.flush();
}

function detailsPage(page, id) {
    const versions = page.element('select', { is: 'emby-select', class: 'selectSource detailTrackSelect' });
    const more = page.element('button', { is: 'emby-button', class: 'button-flat btnMoreCommands detailButton' });
    const view = page.element('div', { id: 'itemDetailPage', 'data-role': 'page', class: 'page libraryPage itemDetailPage' }, [page.element('div', { class: 'mainDetailButtons' }, [more]), versions]);
    page.document.body.append(view);
    page.context.location.hash = '#/details?id=' + id + '&serverId=server-1';
    page.dispatch(view, 'viewshow');
    return { view, more, versions };
}

function fetches(page, path) {
    return page.fetchLog.filter((e) => e.path === '/LibraryMaintenance' + path);
}

// The Move dialog, open on the loose movie (or what options.source says), loaded.
async function openDialog(options) {
    const page = await start(options);
    const movie = card(page, MOVIE);
    itemsContainer(page, [movie]);
    const sheet = await menuOf(page, movie);
    page.dispatch(sheet.scroller.querySelector('[data-id="jflm-move"]'), 'click');
    await page.advance(200);
    const dialog = page.document.querySelector('.jflm-move-dialog');
    assert.ok(dialog, 'the dialog is open');
    return { page, dialog };
}

function text(node) {
    return node ? node.textContent : null;
}

function select(page, element, value) {
    element.value = value;
    page.dispatch(element, 'change');
}

function type(page, input, value) {
    input.value = value;
    page.dispatch(input, 'input');
}

// ------------------------------------------------------------------------------------------------ the menu entry

test('the entry: after Refresh metadata on a card, for an admin with Move on, with its text and icon; one Eligibility request', { skip }, async () => {
    const page = await start();
    const movie = card(page, MOVIE);
    itemsContainer(page, [movie]);
    assert.equal(fetches(page, '/Capabilities').length, 1, 'asked once the user was known');
    assert.equal(fetches(page, '/Move/Eligibility').length, 0, 'no card is asked about before its menu');

    const { scroller } = await menuOf(page, movie);
    assert.deepEqual(rows(scroller).slice(3, 6), ['refresh', 'jflm-move', 'moremediainfo']);
    const row = scroller.querySelector('[data-id="jflm-move"]');
    assert.equal(text(row), 'Move media to new location');
    assert.ok(row.querySelector('.material-icons.drive_file_move'));
    await page.advance(60);
    const asked = fetches(page, '/Move/Eligibility');
    assert.equal(asked.length, 1);
    assert.equal(asked[0].query.ids, MOVIE);
});

test('the rule: a movie, episode or music video that isn\'t a folder; never a series, a collection, a folder, the bulk menu or TV', { skip }, async () => {
    const page = await start();
    const nodes = {
        episode: card(page, 'e'.repeat(32), { 'data-type': 'Episode' }),
        video: card(page, 'f'.repeat(32), { 'data-type': 'MusicVideo' }),
        series: card(page, '1'.repeat(32), { 'data-type': 'Series', 'data-isfolder': 'true' }),
        boxset: card(page, '2'.repeat(32), { 'data-type': 'BoxSet', 'data-isfolder': 'true' }),
        season: card(page, '3'.repeat(32), { 'data-type': 'Season', 'data-isfolder': 'true' }),
        folderMovie: card(page, '4'.repeat(32), { 'data-isfolder': 'true' })
    };
    itemsContainer(page, Object.values(nodes));
    const seen = {};
    for (const [name, node] of Object.entries(nodes)) {
        const sheet = await menuOf(page, node, name === 'series' || name === 'boxset' || name === 'season' ? SERIES_MENU : ITEM_MENU);
        seen[name] = rows(sheet.scroller).includes('jflm-move');
        await closeSheet(page, sheet);
    }

    assert.deepEqual(seen, { episode: true, video: true, series: false, boxset: false, season: false, folderMovie: false });

    // The selection bar's bulk menu: never (one item at a time in this version).
    const panel = page.element('div', { class: 'selectionCommandsPanel' }, [page.element('button', { class: 'btnSelectionPanelOptions' })]);
    page.document.body.append(panel);
    page.dispatch(panel.firstChild, 'click');
    const bulk = openSheet(page, BULK_MENU);
    await page.flush();
    assert.equal(rows(bulk.scroller).includes('jflm-move'), false);
    await closeSheet(page, bulk);
    panel.remove();

    // The TV layout: no entry, and no capabilities asked for.
    const tv = await start({ tv: true });
    const movie = card(tv, MOVIE);
    itemsContainer(tv, [movie]);
    const sheet = await menuOf(tv, movie);
    assert.equal(rows(sheet.scroller).includes('jflm-move'), false);
    await closeSheet(tv, sheet);
    // A second menu, after anything the first one asked for could have come back: still none, and nothing asked.
    const again = await menuOf(tv, movie);
    assert.equal(rows(again.scroller).includes('jflm-move'), false);
    assert.equal(fetches(tv, '/Capabilities').length, 0, 'the TV layout never asks for the capabilities');
    assert.equal(fetches(tv, '/Move/Eligibility').length, 0, 'nor for an item\'s eligibility');
});

test('the capabilities: a viewer never asks and never sees it; Move off, unsupported or its menu setting off hide it; a new user\'s own', { skip }, async () => {
    const viewer = await start({ admin: false, userId: 'viewer-1' });
    const movie = card(viewer, MOVIE);
    itemsContainer(viewer, [movie]);
    const viewerSheet = await menuOf(viewer, movie, VIEWER_MENU);
    assert.equal(rows(viewerSheet.scroller).includes('jflm-move'), false);
    assert.equal(fetches(viewer, '/Capabilities').length, 0, 'a viewer\'s page never asks');
    await closeSheet(viewer, viewerSheet);
    const markedSheet = await menuOf(viewer, movie, ITEM_MENU);
    assert.equal(rows(markedSheet.scroller).includes('jflm-move'), false, 'not even with an admin-only command in the sheet');

    for (const caps of [{ MoveEnabled: false, CanPlanMove: false, ShowMoveInItemMenu: false }, { MoveSupported: false, CanPlanMove: false, ShowMoveInItemMenu: false }, { ShowMoveInItemMenu: false }]) {
        const page = await start({ caps });
        const node = card(page, MOVIE);
        itemsContainer(page, [node]);
        const sheet = await menuOf(page, node);
        assert.equal(rows(sheet.scroller).includes('jflm-move'), false, JSON.stringify(caps));
        await page.advance(100);
        assert.equal(fetches(page, '/Move/Eligibility').length, 0);
    }

    // Another user signs in without a reload: the first user's capabilities are gone, the new one's asked for.
    const page = await start();
    assert.equal(fetches(page, '/Capabilities').length, 1);
    page.respond('GET', '/LibraryMaintenance/Capabilities', Object.assign({}, ADMIN_CAPS, { ShowMoveInItemMenu: false }));
    page.clientState.userId = 'admin-2';
    page.dispatch(page.document.body, 'viewshow');
    await page.advance(100);
    assert.equal(fetches(page, '/Capabilities').length, 2);
    assert.equal(fetches(page, '/Capabilities')[1].userId, 'admin-2');
    const node = card(page, MOVIE);
    itemsContainer(page, [node]);
    const sheet = await menuOf(page, node);
    assert.equal(rows(sheet.scroller).includes('jflm-move'), false);
});

test('eligibility: asked in batches of 100 at most, kept 5 minutes; a refused item loses the entry, a busy one keeps it', { skip }, async () => {
    const page = await start({
        eligibility: (entry) => entry.query.ids.split(',').map((id) => id === OTHER
            ? { ItemId: id, Eligible: false, Code: 'move.not-eligible', Reason: 'This item is a channel\'s.' }
            : (id === 'c'.repeat(32) ? { ItemId: id, Eligible: false, Code: 'move.item-busy', Reason: 'Busy.' } : { ItemId: MOVIE_DASHED, Eligible: true }))
    });
    const movie = card(page, MOVIE);
    const other = card(page, OTHER);
    const busy = card(page, 'c'.repeat(32));
    itemsContainer(page, [movie, other, busy]);

    // Three menus within 50 ms: one request.
    for (const node of [movie, other, busy]) {
        const sheet = await menuOf(page, node);
        assert.equal(rows(sheet.scroller).includes('jflm-move'), true, 'not known yet: the dialog would answer');
        await closeSheet(page, sheet);
    }

    await page.advance(60);
    assert.deepEqual(fetches(page, '/Move/Eligibility').map((e) => e.query.ids.split(',').length), [3]);

    const seen = {};
    for (const [name, node] of [['movie', movie], ['other', other], ['busy', busy]]) {
        const sheet = await menuOf(page, node);
        seen[name] = rows(sheet.scroller).includes('jflm-move');
        await closeSheet(page, sheet);
    }

    assert.deepEqual(seen, { movie: true, other: false, busy: true }, 'the answer with dashes is the card\'s id without');
    await page.advance(100);
    assert.equal(fetches(page, '/Move/Eligibility').length, 1, 'kept: not asked again');

    // Five minutes on, asked again.
    await page.advance(5 * 60 * 1000);
    const sheet = await menuOf(page, other);
    assert.equal(rows(sheet.scroller).includes('jflm-move'), true);
    await page.advance(60);
    assert.equal(fetches(page, '/Move/Eligibility').length, 2);
});

test('eligibility: 250 ids at once are three requests, not one a card', { skip }, async () => {
    const page = await start();
    await page.flush();
    // The dialog's and the details page's lookups share the batcher; 250 ids queued within 50 ms.
    const many = Array.from({ length: 250 }, (_, i) => String(i).padStart(32, '0'));
    const nodes = many.map((id) => card(page, id));
    itemsContainer(page, nodes);
    for (const node of nodes) {
        const sheet = await menuOf(page, node);
        await closeSheet(page, sheet);
    }

    await page.advance(60);
    assert.deepEqual(fetches(page, '/Move/Eligibility').map((e) => e.query.ids.split(',').length), [100, 100, 50]);
});

test('the details page: its item asked about on viewshow; the entry follows the answer, else "Media info"; a missing marker reported once', { skip }, async () => {
    const page = await start({
        eligibility: (entry) => entry.query.ids.split(',').map((id) => ({ ItemId: id, Eligible: id === MOVIE, Code: id === MOVIE ? null : 'move.not-eligible' }))
    });
    const details = detailsPage(page, MOVIE);
    await page.advance(100);
    assert.deepEqual(fetches(page, '/Move/Eligibility').map((e) => e.query.ids), [MOVIE], 'one request on viewshow');

    // Eligible: shown, and a sheet without "Media info" is a miss of the marker, warned and reported once.
    page.dispatch(details.more, 'click');
    let sheet = openSheet(page, ITEM_MENU.filter((c) => c !== 'moremediainfo'));
    await page.flush();
    assert.equal(rows(sheet.scroller).includes('jflm-move'), true);
    await page.advance(10);
    await closeSheet(page, sheet);
    page.dispatch(details.more, 'click');
    sheet = openSheet(page, ITEM_MENU.filter((c) => c !== 'moremediainfo'));
    await page.flush();
    await page.advance(10);
    await closeSheet(page, sheet);
    assert.equal(page.warnings.filter((w) => w.includes('"mediaInfoRow" missing on menus')).length, 1);
    await page.advance(11 * 60 * 1000);
    const reports = fetches(page, '/Diagnostics/ClientHealth');
    assert.equal(reports.length, 1);
    assert.deepEqual(plain(reports[0].body.Missing), [{ Anchor: 'mediaInfoRow', View: 'menus', Count: 1 }]);

    // Not eligible (another item's page): no entry, even with "Media info".
    detailsPage(page, OTHER);
    await page.advance(100);
    page.dispatch(page.document.querySelectorAll('.btnMoreCommands')[1], 'click');
    sheet = openSheet(page, ITEM_MENU);
    await page.flush();
    assert.equal(rows(sheet.scroller).includes('jflm-move'), false);
    await closeSheet(page, sheet);
});

test('the details page before the answer: "Media info" decides, and a series page\'s sheet has no entry', { skip }, async () => {
    const page = await start({ eligibility: () => [] });
    page.respond('GET', '/LibraryMaintenance/Move/Eligibility', null, 0, { hang: true });
    const details = detailsPage(page, MOVIE);
    await page.advance(100);
    page.dispatch(details.more, 'click');
    let sheet = openSheet(page, ITEM_MENU);
    await page.flush();
    assert.equal(rows(sheet.scroller).includes('jflm-move'), true);
    await closeSheet(page, sheet);
    page.dispatch(details.more, 'click');
    sheet = openSheet(page, SERIES_MENU);
    await page.flush();
    assert.equal(rows(sheet.scroller).includes('jflm-move'), false);
});

test('the details page\'s More button missing is reported once per visit', { skip }, async () => {
    const page = await start();
    const view = page.element('div', { id: 'itemDetailPage', class: 'page libraryPage itemDetailPage' }, [page.element('div', { class: 'mainDetailButtons' })]);
    page.document.body.append(view);
    page.context.location.hash = '#/details?id=' + MOVIE + '&serverId=server-1';
    page.dispatch(view, 'viewshow');
    page.dispatch(view, 'viewshow');
    await page.advance(4500);
    assert.equal(page.warnings.filter((w) => w.includes('"kit.detailsMoreButton" missing on menus')).length, 1);
    await page.advance(11 * 60 * 1000);
    assert.deepEqual(fetches(page, '/Diagnostics/ClientHealth').map((e) => plain(e.body.Missing)), [[{ Anchor: 'kit.detailsMoreButton', View: 'menus', Count: 1 }]]);
});

// ----------------------------------------------------------------------------------------------- the dialog

test('choosing the entry opens the dialog after the sheet closed: its Source and Destinations, the preview-only note, no Move button', { skip }, async () => {
    const { page, dialog } = await openDialog();
    assert.deepEqual(page.dialogs.slice(0, 2), [['create', { size: 'small', removeOnClose: true, scrollY: false, autoFocus: false }], ['open']], 'dialogHelper leaves the focus to the dialog');
    assert.equal(text(dialog.querySelector('.formDialogHeader h3')), 'Move media');
    assert.equal(fetches(page, '/Move/Source')[0].query.itemId, MOVIE);
    assert.equal(fetches(page, '/Move/Destinations')[0].query.itemId, MOVIE);
    assert.ok(text(dialog.querySelector('.jflm-move-preview-only')).includes('only previews'));
    assert.equal(dialog.querySelector('.jflm-move-preview-only').classList.contains('hide'), false);
    assert.equal(dialog.querySelector('.jflm-move-run').classList.contains('hide'), true, 'no Move button while CanMove is false');
    // Preview only: the intro says it first, and the footer with the note is the dialog's last row, outside
    // the scrolling content, so it never scrolls away.
    assert.equal(text(dialog.querySelector('.jflm-move-intro')), 'Preview only: nothing is moved.Choose where it goes; the preview below shows what the move would do.');
    assert.equal(dialog.querySelector('.jflm-move-preview-only-lead').classList.contains('hide'), false);
    const footer = dialog.querySelector('.jflm-move-footer');
    assert.equal(footer.parentNode, dialog, 'the footer is in the dialog itself');
    assert.equal(dialog.children[dialog.children.length - 1], footer, 'its last row');
    assert.equal(dialog.querySelector('.formDialogContent').contains(footer), false, 'not in the scrolling content');
    assert.ok(footer.contains(dialog.querySelector('.jflm-move-preview-only')));
    assert.equal(fetches(page, '/Move/Plan').length, 0, 'nothing is planned before a place is chosen');
    assert.deepEqual(dialog.querySelectorAll('h4').map(text), ['What moves', 'Where to', 'Preview']);
    assert.equal(page.document.activeElement, dialog.querySelector('select.jflm-move-library'), 'the focus starts on the library');
});

test('what moves: a loose file with its sidecars, the item\'s kind and place', { skip }, async () => {
    const { dialog } = await openDialog();
    const what = dialog.querySelector('.jflm-move-source');
    assert.equal(text(what.querySelector('.jflm-move-item-title')), 'Plain Loose (2005)');
    assert.equal(text(what.querySelector('.jflm-move-chip')), 'Movie');
    assert.equal(text(what.querySelector('.jflm-move-where')), 'In Movies: C:\\media\\Movies\\Plain Loose (2005).mkv');
    assert.equal(text(what.querySelector('.jflm-move-summary')), 'This file and 2 sidecar files move together: 1.0 MB.');
    const lists = what.querySelectorAll('.jflm-move-list');
    assert.deepEqual(lists.map((l) => text(l.querySelector('.jflm-move-list-label'))), ['Sidecar files (2)']);
    assert.equal(text(what.querySelector('details summary')), 'Every file (3)');
    assert.equal(what.querySelector('.jflm-move-retargeted'), null);

    // A file alone moves; two versions of a loose movie move together.
    const unit = looseSource().Unit;
    const alone = await openDialog({ source: looseSource({ Unit: Object.assign({}, unit, { Files: [unit.Files[0]], TotalBytes: 1048576 }) }) });
    assert.equal(text(alone.dialog.querySelector('.jflm-move-summary')), 'This file moves: 1.0 MB.');
    assert.equal(alone.dialog.querySelectorAll('.jflm-move-list').length, 0);
    const second = 'C:\\media\\Movies\\Plain Loose (2005) - 720p.mkv';
    const versions = await openDialog({ source: looseSource({ Unit: Object.assign({}, unit, { Files: [unit.Files[0], { Rel: 'Plain Loose (2005) - 720p.mkv', Bytes: 1024 }], Versions: [second], TotalBytes: 1049600 }) }) });
    assert.match(text(versions.dialog.querySelector('.jflm-move-summary')), /^These 2 versions move together: /);
    assert.deepEqual(versions.dialog.querySelectorAll('.jflm-move-list-label').map(text), ['Versions (1)']);

    // A name Jellyfin kept with its year (a folder it didn't parse) doesn't get the year twice.
    const named = await openDialog({ source: looseSource({ Item: { Id: MOVIE_DASHED, Name: 'Folder Movie (2008)', Year: 2008, Kind: 'Movie' } }) });
    assert.equal(text(named.dialog.querySelector('.jflm-move-item-title')), 'Folder Movie (2008)');
});

test('what moves: a folder with versions and extras, opened on a version, says whose it is', { skip }, async () => {
    const { dialog } = await openDialog({ source: folderSource() });
    const what = dialog.querySelector('.jflm-move-source');
    assert.match(text(what.querySelector('.jflm-move-summary')), /^The whole folder Versions Film \(2015\) moves: 4 files, 1 subfolder, /);
    assert.equal(text(what.querySelector('.jflm-move-retargeted')), 'This version belongs to Versions Film (2015); the whole folder moves.');
    assert.deepEqual(what.querySelectorAll('.jflm-move-list-label').map(text), ['Versions (1)', 'Extras (1)']);
    assert.deepEqual(what.querySelectorAll('.jflm-move-list li').map(text), ['Versions Film (2015) - 720p.mkv', 'trailers\\Teaser.mkv']);
    assert.equal(text(what.querySelector('.jflm-move-where')), 'In Movies 2: C:\\media\\Movies 2\\Versions Film (2015)');
});

// A name or path from the server goes in its own <bdi>, so a right-to-left name can't reorder
// what is beside it, and a bidi control in it (U+202E turns the rest of a line around) is shown as [U+202E], never
// obeyed; sizes and years are nodes of their own, outside the names.
test('names are isolated: a bidi control in an item, file, folder, collection or playlist name is shown, never obeyed', { skip }, async () => {
    const RLO = String.fromCharCode(0x202E);
    const HEBREW = String.fromCharCode(0x05E9, 0x05DC, 0x05D5, 0x05DD);
    const evil = 'Evil' + RLO + 'vkm.exe';
    const unit = looseSource().Unit;
    const source = looseSource({
        Item: { Id: MOVIE_DASHED, Name: 'Evil' + RLO + 'gnp', Year: 2005, Kind: 'Movie' },
        Unit: Object.assign({}, unit, { Primary: 'C:\\media\\Movies\\' + evil, Files: [{ Rel: evil, Bytes: 1048576 }, { Rel: 'Evil' + RLO + 'trs.exe', Bytes: 2048 }], TotalBytes: 1050624 })
    });
    const rows = (entry) => (entry.query.relativePath ? folders(entry) : { Items: [{ Name: 'Shows' + RLO + 'eivom', RelativePath: 'Shows' + RLO + 'eivom', Kind: 'Plain', Selectable: true, Reason: null }, { Name: HEBREW, RelativePath: HEBREW, Kind: 'Plain', Selectable: true, Reason: null }], TotalRecordCount: 2, StartIndex: 0, Path: 'C:\\media\\x' });
    const planned = () => plan({
        Units: [Object.assign({}, plan().Units[0], { From: 'C:\\media\\Movies\\' + evil, To: 'C:\\media\\Movies\\Shows' + RLO + 'eivom\\' + evil, CreatedFolders: ['C:\\media\\Movies\\New' + RLO + 'X'], WrapFolderName: 'Wrap' + RLO + 'X' })],
        Preserved: Object.assign({}, plan().Preserved, { Collections: ['Sci' + RLO + 'fi', HEBREW], Playlists: ['Mine' + RLO + 'X'], HookConsumers: ['Hook' + RLO + 'X'] }),
        Warnings: [{ Code: 'move.sidecar-left-behind', Message: evil + ' looks related and stays behind.', Paths: ['C:\\media\\Movies\\' + evil] }]
    });
    const { page, dialog } = await openDialog({ source, folders: rows, plan: planned });
    page.dispatch(dialog.querySelector('.jflm-move-preview-again'), 'click');
    await page.advance(50);

    // Nothing in the dialog obeys a bidi control: each is shown, framed, inside its name's <bdi>.
    assert.equal(/[\u202A-\u202E\u2066-\u2069\u200E\u200F\u061C]/.test(dialog.textContent), false, 'no bidi control is left to act');
    const controls = dialog.querySelectorAll('.jflm-move-bidi-control');
    assert.ok(controls.length >= 12, controls.length + ' shown');
    assert.ok(controls.every((c) => text(c) === '[U+202E]' && c.parentNode.tagName === 'BDI'));

    // The title: the name isolated, the year outside it.
    const title = dialog.querySelector('.jflm-move-item-title');
    assert.equal(text(title), 'Evil[U+202E]gnp (2005)');
    assert.deepEqual(title.children.map((c) => [c.tagName, text(c)]), [['BDI', 'Evil[U+202E]gnp'], ['SPAN', '(2005)']]);

    // The place, the sidecar with its size beside it (not inside), every file.
    assert.deepEqual(dialog.querySelector('.jflm-move-where').querySelectorAll('bdi').map(text), ['Movies', 'C:\\media\\Movies\\Evil[U+202E]vkm.exe']);
    const sidecar = dialog.querySelector('.jflm-move-source .jflm-move-list li');
    assert.deepEqual(sidecar.children.map((c) => [c.tagName, text(c)]), [['BDI', 'Evil[U+202E]trs.exe'], ['SPAN', '(2.0 KB)']]);
    assert.deepEqual(dialog.querySelectorAll('details.jflm-move-files li').map((li) => text(li.children[0])), ['Evil[U+202E]vkm.exe', 'Evil[U+202E]trs.exe']);

    // The folder rows (a right-to-left name too) and the breadcrumb.
    assert.deepEqual(dialog.querySelectorAll('.jflm-move-folder-name').map((n) => [n.children.length, n.children[0].tagName, text(n)]), [[1, 'BDI', 'Shows[U+202E]eivom'], [1, 'BDI', HEBREW]]);
    assert.equal(dialog.querySelectorAll('button.jflm-move-folder')[0].title, 'Open Shows[U+202E]eivom');
    assert.equal(dialog.querySelector('.jflm-move-crumb').children[0].tagName, 'BDI');

    // The preview: From, To, the folder it makes and its own folder; the warning's sentence and path; the kept
    // collections (a right-to-left one too), playlist and plugin, each its own <bdi>.
    const facts = {};
    dialog.querySelectorAll('dl.jflm-move-facts dt').forEach((dt) => {
        facts[text(dt)] = dt.nextElementSibling;
    });
    assert.deepEqual(['From', 'To', 'Creates', 'Its own folder'].map((k) => facts[k].querySelectorAll('bdi').map(text)),
        [['C:\\media\\Movies\\Evil[U+202E]vkm.exe'], ['C:\\media\\Movies\\Shows[U+202E]eivom\\Evil[U+202E]vkm.exe'], ['C:\\media\\Movies\\New[U+202E]X'], ['Wrap[U+202E]X']]);
    const warning = dialog.querySelector('li.jflm-move-warning');
    assert.equal(text(warning.querySelector('.jflm-move-issue-message')), 'Evil[U+202E]vkm.exe looks related and stays behind.');
    assert.deepEqual(warning.querySelectorAll('.jflm-move-names li bdi').map(text), ['C:\\media\\Movies\\Evil[U+202E]vkm.exe']);
    const kept = dialog.querySelectorAll('.jflm-move-preview-output .jflm-move-list li');
    const line = (start) => kept.filter((li) => text(li).indexOf(start) === 0)[0];
    assert.deepEqual(line('2 collections: ').querySelectorAll('bdi').map(text), ['Sci[U+202E]fi', HEBREW]);
    assert.equal(text(line('2 collections: ')), '2 collections: Sci[U+202E]fi, ' + HEBREW);
    assert.deepEqual(line('1 playlist: ').querySelectorAll('bdi').map(text), ['Mine[U+202E]X']);
    assert.deepEqual(line('Updated in other plugins: ').querySelectorAll('bdi').map(text), ['Hook[U+202E]X']);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the picker: its own library first; another drive\'s folder can\'t be chosen and says why; badges and reasons; text only', { skip }, async () => {
    const { page, dialog } = await openDialog();
    const library = dialog.querySelector('select.jflm-move-library');
    assert.deepEqual(library.querySelectorAll('option').map(text), ['Movies (its library now)', 'Big Folders', 'Movies 2 (can\'t be chosen)']);
    assert.equal(fetches(page, '/Move/Folders').length, 1);
    assert.deepEqual(plain(fetches(page, '/Move/Folders')[0].query), { libraryId: MOVIES, locationIndex: '0', startIndex: '0', limit: '200', itemId: MOVIE });
    const list = dialog.querySelector('.jflm-move-folders');
    const buttons = list.querySelectorAll('button.jflm-move-folder');
    assert.deepEqual(buttons.map((b) => text(b.querySelector('.jflm-move-folder-name'))), ['Collection', 'Linked', 'extras', '<b>Bold</b> (1999)']);
    assert.deepEqual(buttons.map((b) => b.getAttribute('aria-disabled')), [null, 'true', 'true', 'true']);
    assert.deepEqual(buttons.map((b) => text(b.querySelector('.jflm-move-badge'))), [null, 'Link (not allowed)', 'Ignored by Jellyfin', 'Movie folder']);
    assert.equal(list.querySelector('b'), null, 'names are text');
    const reason = list.querySelectorAll('.jflm-move-folder-reason')[0];
    assert.equal(text(reason), 'This is a junction or symbolic link; Move doesn\'t follow links.');
    assert.equal(buttons[1].getAttribute('aria-describedby'), reason.id);
    assert.equal(text(dialog.querySelector('.jflm-move-count')), '4 folders.');

    // A row that can't be chosen does nothing; Movies 2 is on another drive.
    page.dispatch(buttons[1], 'click');
    await page.advance(500);
    assert.equal(fetches(page, '/Move/Folders').length, 1);
    select(page, library, '2');
    await page.advance(500);
    assert.match(text(dialog.querySelector('.jflm-move-location-note')), /Movies 2 \(C:\\media\\Movies 2\) can't be chosen\. It is on another drive\. Moving between drives comes with series and season moves/);
    assert.equal(dialog.querySelector('.jflm-move-folders').classList.contains('hide'), true);
    assert.equal(fetches(page, '/Move/Folders').length, 1, 'not browsed');
    assert.equal(fetches(page, '/Move/Plan').length, 0, 'not planned');
    assert.equal(text(dialog.querySelector('.jflm-move-preview-status')), 'This place can\'t be previewed; choose another.');
});

test('the folder browser: 200 a page with More folders, a filter, the breadcrumb, and the item\'s own folder', { skip }, async () => {
    const { page, dialog } = await openDialog({ source: folderSource(), destinations: destinations().slice(0, 2).concat([{ LibraryId: MOVIES2, Name: 'Movies 2', CollectionType: 'movies', IsSource: true, Locations: [{ Index: 0, Path: 'C:\\media\\Movies 2', Selectable: true }] }]) });
    const library = dialog.querySelector('select.jflm-move-library');
    select(page, library, '1');
    await page.advance(100);
    const list = dialog.querySelector('.jflm-move-folders');
    assert.equal(list.querySelectorAll('li').length, 200);
    assert.equal(text(dialog.querySelector('.jflm-move-count')), 'Showing 200 of 450 folders.');
    const more = dialog.querySelector('.jflm-pager-more');
    assert.equal(text(more), 'More folders');
    page.dispatch(more, 'click');
    await page.advance(100);
    page.dispatch(more, 'click');
    await page.advance(100);
    assert.equal(list.querySelectorAll('li').length, 450);
    assert.deepEqual(fetches(page, '/Move/Folders').slice(-3).map((e) => e.query.startIndex), ['0', '200', '400']);
    assert.equal(dialog.querySelector('.jflm-pager').classList.contains('hide'), true);

    const filter = dialog.querySelector('input.jflm-searchbox');
    assert.equal(filter.getAttribute('aria-label'), 'Filter folders');
    type(page, filter, 'folder 000');
    await page.advance(400);
    assert.equal(fetches(page, '/Move/Folders').slice(-1)[0].query.filter, 'folder 000');
    assert.equal(list.querySelectorAll('li').length, 9);

    // Its own library: the item's own folder is badged and can't be chosen; a plain folder opens.
    select(page, library, '2');
    await page.advance(100);
    page.respond('GET', '/LibraryMaintenance/Move/Folders', () => ({ Items: [
        { Name: 'Versions Film (2015)', RelativePath: 'Versions Film (2015)', Kind: 'MovieFolder', Selectable: false, Reason: 'This is the item\'s own folder.' },
        { Name: 'Shelf', RelativePath: 'Shelf', Kind: 'Plain', Selectable: true }
    ], TotalRecordCount: 2, StartIndex: 0, Path: 'C:\\media\\Movies 2' }));
    select(page, library, '2');
    await page.advance(100);
    const rowsNow = dialog.querySelectorAll('button.jflm-move-folder');
    assert.equal(text(rowsNow[0].querySelector('.jflm-move-badge')), 'This item\'s folder');
    rowsNow[1].focus();
    page.dispatch(rowsNow[1], 'click');
    await page.advance(100);
    assert.equal(fetches(page, '/Move/Folders').slice(-1)[0].query.relativePath, 'Shelf');
    const crumbs = dialog.querySelectorAll('.jflm-move-crumb');
    assert.deepEqual(crumbs.map(text), ['Movies 2', 'Shelf']);
    assert.equal(crumbs[1].getAttribute('aria-current'), 'location');
    assert.equal(page.document.activeElement, crumbs[1], 'the focus follows to where it is now (never the filter: a phone\'s keyboard)');
    page.dispatch(crumbs[0], 'click');
    await page.advance(100);
    assert.equal(fetches(page, '/Move/Folders').slice(-1)[0].query.relativePath, undefined);
});

test('the preview: 400 ms after the last change, one plan at a time; From, To, how, files, space, kept and regenerated', { skip }, async () => {
    const { page, dialog } = await openDialog();
    const newFolder = dialog.querySelector('input.jflm-move-new-folder');
    type(page, newFolder, 'N');
    await page.advance(100);
    type(page, newFolder, 'New');
    await page.advance(399);
    assert.equal(fetches(page, '/Move/Plan').length, 0, 'still typing');
    await page.advance(10);
    const plans = fetches(page, '/Move/Plan');
    assert.equal(plans.length, 1);
    assert.deepEqual(plain(plans[0].body), { ItemIds: [MOVIE], Destination: { LibraryId: MOVIES, LocationIndex: 0, RelativePath: null, NewFolderName: 'New', Wrap: null, WrapFolderName: null } });

    const output = dialog.querySelector('.jflm-move-preview-output');
    const facts = output.querySelectorAll('dl.jflm-move-facts dt').map(text);
    assert.deepEqual(facts, ['From', 'To', 'How', 'Moves', 'Space', 'Creates']);
    const values = output.querySelectorAll('dl.jflm-move-facts dd').map(text);
    assert.equal(values[2], 'Instant rename (the same drive)');
    assert.match(values[3], /^3 files, /);
    assert.match(values[4], /^C:\\: no extra space needed \(a rename\), /);
    assert.equal(values[5], 'No new folder');
    assert.equal(text(dialog.querySelector('.jflm-move-preview-status')), 'It could move there: nothing stands in the way.', 'could: this build cannot move');
    const kept = output.querySelectorAll('.jflm-move-list')[0];
    assert.match(text(kept), /Watch state of 2 users \(1 favourite, 1 resume point\)/);
    assert.match(text(kept), /1 collection: Sci-fi/);
    assert.match(text(kept), /Metadata edits: every field edited in Jellyfin; locked: Overview/);
    assert.match(text(output.querySelectorAll('.jflm-move-list')[1]), /Media info and chapters/);

    // A library change plans at once; an older answer that comes late is dropped.
    page.respond('POST', '/LibraryMaintenance/Move/Plan', null, 0, { hang: true });
    select(page, dialog.querySelector('select.jflm-move-library'), '1');
    await page.advance(10);
    page.respond('POST', '/LibraryMaintenance/Move/Plan', () => plan({ Method: 'CopyVerifyDelete', Volumes: [{ Root: 'D:\\', FreeBytes: 0, RequiredBytes: 1024 }], Blockers: [{ Code: 'move.cross-volume-later', Message: 'Big Folders is on another drive.', Paths: null }] }));
    type(page, newFolder, '');
    await page.advance(500);
    assert.equal(fetches(page, '/Move/Plan').length, 3);
    assert.equal(text(dialog.querySelector('.jflm-move-preview-status')), '1 blocker: it can\'t move there now.');
    assert.equal(output.querySelectorAll('dl.jflm-move-facts dd').map(text)[2], 'Copy, verify, then delete the originals (another drive)');
    assert.match(output.querySelectorAll('dl.jflm-move-facts dd').map(text)[4], /^D:\\: 1\.0 KB needed, 0 B free$/);
});

test('blockers and warnings: each with its title, the server\'s sentence and its paths; Check again plans anew', { skip }, async () => {
    const issues = {
        Blockers: [
            { Code: 'move.playing', Message: 'Being played by viewer on Chrome.', Paths: null },
            { Code: 'move.in-use', Message: 'A file is in use by another program.', Paths: ['C:\\media\\Movies\\Plain Loose (2005).mkv'] },
            { Code: 'move.reparse-point', Message: '<i>Linked</i> is a link.', Paths: ['C:\\media\\Movies\\Linked'] },
            { Code: 'move.collision', Message: 'It exists.', Paths: null },
            { Code: 'move.into-movie-folder', Message: 'A movie\'s folder.', Paths: null },
            { Code: 'move.episode-needs-series', Message: 'Choose a series folder.', Paths: null },
            { Code: 'move.foreign-media', Message: 'Other videos.', Paths: null },
            { Code: 'move.extras-folder-name', Message: 'Extras.', Paths: null },
            { Code: 'move.destination-invalid', Message: 'Invalid.', Paths: null },
            { Code: 'move.cross-volume-later', Message: 'Another drive.', Paths: null },
            { Code: 'move.some-new-code', Message: 'A new check.', Paths: null }
        ],
        Warnings: [
            { Code: 'move.episode-other-season', Message: 'Season 1 to Season 2.', Paths: null },
            { Code: 'move.sidecar-left-behind', Message: 'One file stays.', Paths: Array.from({ length: 25 }, (_, i) => 'C:\\f' + i) }
        ]
    };
    const { page, dialog } = await openDialog({ plan: () => plan(issues) });
    page.dispatch(dialog.querySelector('.jflm-move-preview-again'), 'click');
    await page.advance(50);
    const blockers = dialog.querySelectorAll('li.jflm-move-blocker');
    assert.deepEqual(blockers.map((b) => text(b.querySelector('.jflm-move-issue-title'))), [
        'Someone is playing it', 'A file is in use', 'A junction or symbolic link is in the way', 'Something of that name is there already',
        'That is another movie\'s own folder', 'An episode needs a series folder', 'The folder holds other videos',
        'Jellyfin would read that folder as extras', 'This destination can\'t be used', 'Moving to another drive comes later', 'Move can\'t do this'
    ]);
    assert.equal(text(blockers[2].querySelector('.jflm-move-issue-message')), '<i>Linked</i> is a link.');
    assert.equal(blockers[2].querySelector('i'), null);
    assert.deepEqual(blockers[1].querySelectorAll('.jflm-move-names li').map(text), ['C:\\media\\Movies\\Plain Loose (2005).mkv']);
    const warnings = dialog.querySelectorAll('li.jflm-move-warning');
    assert.deepEqual(warnings.map((w) => text(w.querySelector('.jflm-move-issue-title'))), ['The episode moves to another season', 'A file that looks related stays behind']);
    assert.equal(warnings[1].querySelectorAll('.jflm-move-names li').length, 21, '20 paths and "and 5 more"');
    assert.equal(text(dialog.querySelector('.jflm-move-preview-status')), '11 blockers: it can\'t move there now.');
    const again = dialog.querySelector('.jflm-move-preview-again');
    assert.equal(text(again), 'Check again');
    page.respond('POST', '/LibraryMaintenance/Move/Plan', () => plan());
    page.dispatch(again, 'click');
    await page.advance(50);
    assert.equal(fetches(page, '/Move/Plan').length, 2);
    assert.equal(dialog.querySelectorAll('li.jflm-move-blocker').length, 0);
});

test('the wrap choice of a loose movie: as the settings say until changed, then yes or no; its name; Use the default', { skip }, async () => {
    const wrapped = (entry) => plan({ Units: [Object.assign({}, plan().Units[0], { WrapFolderName: entry.body.Destination.Wrap === false ? null : (entry.body.Destination.WrapFolderName || 'Plain Loose (2005)'), CreatedFolders: entry.body.Destination.Wrap === false ? [] : ['C:\\media\\Big Folders\\Plain Loose (2005)'] })] });
    const { page, dialog } = await openDialog({ plan: wrapped });
    const box = dialog.querySelector('input.jflm-move-wrap');
    const reset = dialog.querySelector('.jflm-move-wrap-reset');
    const name = dialog.querySelector('input.jflm-move-wrap-name');
    assert.equal(reset.classList.contains('hide'), true);
    select(page, dialog.querySelector('select.jflm-move-library'), '1');
    await page.advance(50);
    assert.equal(fetches(page, '/Move/Plan').slice(-1)[0].body.Destination.Wrap, null, 'left to LooseFileFolderMode');
    assert.equal(box.checked, true, 'shows what the server chose');
    assert.equal(text(dialog.querySelector('.jflm-move-wrap-state')), 'As the settings say for this folder.');
    assert.equal(name.placeholder, 'Plain Loose (2005)');
    assert.match(dialog.querySelectorAll('dl.jflm-move-facts dd').map(text).join('|'), /Plain Loose \(2005\)$/);

    box.checked = false;
    page.dispatch(box, 'change');
    await page.advance(50);
    assert.equal(fetches(page, '/Move/Plan').slice(-1)[0].body.Destination.Wrap, false);
    assert.equal(reset.classList.contains('hide'), false);
    box.checked = true;
    page.dispatch(box, 'change');
    type(page, name, 'Plain Loose - Director (2005)');
    await page.advance(450);
    assert.deepEqual(plain(fetches(page, '/Move/Plan').slice(-1)[0].body.Destination), { LibraryId: BIG, LocationIndex: 0, RelativePath: null, NewFolderName: null, Wrap: true, WrapFolderName: 'Plain Loose - Director (2005)' });
    type(page, name, 'Bad: name');
    await page.advance(450);
    assert.equal(text(dialog.querySelectorAll('.jflm-move-field-error')[1]), 'A folder name can\'t hold any of < > : " / \\ | ? *.');
    assert.equal(name.getAttribute('aria-invalid'), 'true');
    const count = fetches(page, '/Move/Plan').length;
    page.dispatch(reset, 'click');
    await page.advance(50);
    assert.equal(fetches(page, '/Move/Plan').length, count, 'a bad name isn\'t sent');
    type(page, name, '');
    await page.advance(450);
    assert.equal(fetches(page, '/Move/Plan').slice(-1)[0].body.Destination.Wrap, null);

    // An episode and a folder unit have no wrap choice, and send none.
    const episode = await openDialog({ source: looseSource({ Item: { Id: MOVIE_DASHED, Name: 'Pilot Show S01E03', Year: null, Kind: 'Episode' } }) });
    assert.equal(episode.dialog.querySelector('.jflm-move-wrap-group').classList.contains('hide'), true);
    const folder = await openDialog({ source: folderSource() });
    assert.equal(folder.dialog.querySelector('.jflm-move-wrap-group').classList.contains('hide'), true);
    folder.page.dispatch(folder.dialog.querySelector('.jflm-move-preview-again'), 'click');
    await folder.page.advance(50);
    assert.equal('Wrap' in fetches(folder.page, '/Move/Plan').slice(-1)[0].body.Destination, false);
});

test('new folder: checked as typed; an episode at the library folder gets "New series folder" with its series as a hint', { skip }, async () => {
    const { page, dialog } = await openDialog();
    const input = dialog.querySelector('input.jflm-move-new-folder');
    const error = dialog.querySelectorAll('.jflm-move-field-error')[0];
    type(page, input, 'Trailing.');
    await page.advance(450);
    assert.equal(text(error), 'A folder name can\'t end with a dot or a space.');
    assert.equal(input.getAttribute('aria-describedby'), error.id);
    assert.equal(fetches(page, '/Move/Plan').length, 0);
    assert.equal(text(dialog.querySelector('.jflm-move-preview-status')), 'This place can\'t be previewed; choose another.');
    type(page, input, 'Fine');
    await page.advance(450);
    assert.equal(text(error), '');
    assert.equal(fetches(page, '/Move/Plan').length, 1);

    const shows = 'C:\\media\\Shows';
    const episode = await openDialog({
        source: looseSource({ Item: { Id: MOVIE_DASHED, Name: 'Pilot Show S01E03', Year: null, Kind: 'Episode' }, LocationPath: shows, Unit: Object.assign({}, looseSource().Unit, { Root: shows + '\\Pilot Show (2020) [tvdbid-920001]\\Season 01', Primary: shows + '\\Pilot Show (2020) [tvdbid-920001]\\Season 01\\Pilot Show S01E03.mkv' }) }),
        destinations: [{ LibraryId: MOVIES, Name: 'Shows', CollectionType: 'tvshows', IsSource: true, Locations: [{ Index: 0, Path: shows, Selectable: true }] }]
    });
    const label = episode.dialog.querySelector('input.jflm-move-new-folder').parentNode.parentNode.querySelector('label');
    assert.equal(text(label), 'New series folder (optional)');
    assert.equal(episode.dialog.querySelector('input.jflm-move-new-folder').placeholder, 'Pilot Show (2020) [tvdbid-920001]');
    page.dispatch(episode.dialog.querySelector('button.jflm-move-folder'), 'click');
    await episode.page.advance(50);
    assert.equal(text(label), 'New folder (optional)');
});

test('errors: Move turned off, the item gone, an item that can\'t move (the entry goes), a network failure with Try again', { skip }, async () => {
    const off = await openDialog({ source: () => ({ type: 'urn:jellyfin-plugin-kit:feature-disabled', title: 'Off', status: 403, detail: 'Move media is off.' }) });
    off.page.respond('GET', '/LibraryMaintenance/Move/Source', { type: 'urn:jellyfin-plugin-kit:feature-disabled', title: 'Off', status: 403, detail: 'Move media is off.' }, 403);
    const offAgain = await openDialogWith(off.page, off.dialog);
    assert.equal(text(offAgain.querySelector('.jflm-move-error-title')), 'Move media is turned off');
    assert.equal(offAgain.querySelector('.jflm-move-retry'), null);

    const page = await start();
    // The menu's own question is never answered here: the dialog's refusal alone hides the entry.
    page.respond('GET', '/LibraryMaintenance/Move/Eligibility', null, 0, { hang: true });
    page.respond('GET', '/LibraryMaintenance/Move/Source', { title: 'Conflict', status: 409, detail: 'This item is on a link.', code: 'move.reparse-point' }, 409);
    const movie = card(page, MOVIE);
    itemsContainer(page, [movie]);
    let sheet = await menuOf(page, movie);
    page.dispatch(sheet.scroller.querySelector('[data-id="jflm-move"]'), 'click');
    await page.advance(200);
    let dialog = page.document.querySelector('.jflm-move-dialog');
    assert.equal(text(dialog.querySelector('.jflm-move-error-title')), 'A junction or symbolic link is in the way');
    assert.equal(text(dialog.querySelector('.jflm-move-error-detail')), 'This item is on a link.');
    assert.equal(dialog.querySelector('[role="alert"]').classList.contains('jflm-move-error'), true);
    page.dispatch(dialog.querySelector('.jflm-move-close'), 'click');
    await page.advance(50);
    await closeSheet(page, sheet);
    sheet = await menuOf(page, movie);
    assert.equal(rows(sheet.scroller).includes('jflm-move'), false, 'the refusal hides the entry');
    await closeSheet(page, sheet);

    // Gone: 404.
    const other = card(page, OTHER);
    itemsContainer(page, [other]);
    page.respond('GET', '/LibraryMaintenance/Move/Source', { title: 'Not Found', status: 404, detail: 'The item isn\'t there, or you can\'t open its library.' }, 404);
    sheet = await menuOf(page, other);
    page.dispatch(sheet.scroller.querySelector('[data-id="jflm-move"]'), 'click');
    await page.advance(200);
    dialog = page.document.querySelector('.jflm-move-dialog');
    assert.equal(text(dialog.querySelector('.jflm-move-error-title')), 'Not found');
    page.dispatch(dialog.querySelector('.jflm-move-close'), 'click');
    await page.advance(50);
    await closeSheet(page, sheet);

    // The network: Try again loads it.
    const third = card(page, 'd'.repeat(32));
    itemsContainer(page, [third]);
    page.respond('GET', '/LibraryMaintenance/Move/Source', null, 0, { networkError: true });
    sheet = await menuOf(page, third);
    page.dispatch(sheet.scroller.querySelector('[data-id="jflm-move"]'), 'click');
    await page.advance(200);
    dialog = page.document.querySelector('.jflm-move-dialog');
    assert.equal(text(dialog.querySelector('.jflm-move-error-title')), 'The server could not be reached');
    const retry = dialog.querySelector('.jflm-move-retry');
    assert.equal(page.document.activeElement, retry, 'the focus goes to Try again');
    page.respond('GET', '/LibraryMaintenance/Move/Source', looseSource());
    page.dispatch(retry, 'click');
    await page.advance(100);
    assert.ok(dialog.querySelector('.jflm-move-source'));
});

// Opens the dialog again on the first card of a started page (after a close).
async function openDialogWith(page, previous) {
    page.dispatch(previous.querySelector('.jflm-move-close'), 'click');
    await page.advance(50);
    const node = page.document.querySelector('.card');
    const sheet = await menuOf(page, node);
    page.dispatch(sheet.scroller.querySelector('[data-id="jflm-move"]'), 'click');
    await page.advance(200);
    return page.document.querySelector('.jflm-move-dialog');
}

test('a Plan refusal is shown in the preview: 400 with its code, 409 busy with Check again, a network failure', { skip }, async () => {
    const { page, dialog } = await openDialog();
    page.respond('POST', '/LibraryMaintenance/Move/Plan', { title: 'Bad Request', status: 400, detail: 'That folder isn\'t in the library.', code: 'move.destination-invalid' }, 400);
    page.dispatch(dialog.querySelector('.jflm-move-preview-again'), 'click');
    await page.advance(50);
    const output = dialog.querySelector('.jflm-move-preview-output');
    assert.equal(text(output.querySelector('.jflm-move-error-title')), 'This destination can\'t be used');
    assert.equal(text(output.querySelector('.jflm-move-error-detail')), 'That folder isn\'t in the library.');
    assert.equal(text(dialog.querySelector('.jflm-move-preview-status')), 'No preview: This destination can\'t be used.');
    page.respond('POST', '/LibraryMaintenance/Move/Plan', { title: 'Conflict', status: 409, detail: 'A merge is using it.', code: 'move.item-busy' }, 409);
    page.dispatch(dialog.querySelector('.jflm-move-preview-again'), 'click');
    await page.advance(50);
    assert.equal(text(output.querySelector('.jflm-move-error-title')), 'Another operation is using this item');
    assert.ok(output.querySelector('.jflm-move-retry'));
    page.respond('POST', '/LibraryMaintenance/Move/Plan', null, 0, { networkError: true });
    page.dispatch(output.querySelector('.jflm-move-retry'), 'click');
    await page.advance(50);
    assert.equal(text(output.querySelector('.jflm-move-error-title')), 'The server could not be reached');
});

test('a plan past its time is out of date; Preview again plans anew', { skip }, async () => {
    const { page, dialog } = await openDialog();
    page.respond('POST', '/LibraryMaintenance/Move/Plan', () => plan({ ExpiresUtc: new page.context.Date(page.context.Date.now() + 15 * 60 * 1000).toISOString() }));
    page.dispatch(dialog.querySelector('.jflm-move-preview-again'), 'click');
    await page.advance(50);
    assert.equal(text(dialog.querySelector('.jflm-move-preview-status')), 'It could move there: nothing stands in the way.');
    await page.advance(15 * 60 * 1000);
    assert.equal(text(dialog.querySelector('.jflm-move-preview-status')), 'This preview is out of date. Preview again to see what a move would do now.');
    assert.ok(dialog.querySelector('.jflm-move-preview-output').classList.contains('jflm-move-stale'));
    page.dispatch(dialog.querySelector('.jflm-move-preview-again'), 'click');
    await page.advance(50);
    assert.equal(fetches(page, '/Move/Plan').length, 2);
    assert.equal(dialog.querySelector('.jflm-move-preview-output').classList.contains('jflm-move-stale'), false);
});

test('keys: Tab stays in the dialog, Escape closes it on the desktop layout, Close too; nothing navigates while nothing moved', { skip }, async () => {
    const { page, dialog } = await openDialog();
    const back = dialog.querySelector('.formDialogHeader button');
    const close = dialog.querySelector('.jflm-move-close');
    let pageKeys = 0;
    page.document.addEventListener('keydown', () => pageKeys++, true);
    close.focus();
    page.key(close, 'Tab');
    assert.equal(page.document.activeElement, back, 'from the last control back to the first');
    page.key(back, 'Tab', { shiftKey: true });
    assert.ok(dialog.contains(page.document.activeElement));
    assert.equal(pageKeys, 0);
    const before = page.context.location.hash;
    page.key(page.document.activeElement, 'Escape');
    await page.advance(50);
    assert.equal(page.document.querySelector('.jflm-move-dialog'), null, 'closed');
    assert.equal(page.context.location.hash, before);
    assert.deepEqual(page.replaced, []);

    // Close, and the history pop (Back) alone.
    const second = await openDialog();
    page.dispatch(second.dialog.querySelector('.jflm-move-close'), 'click');
    await second.page.advance(50);
    assert.equal(second.page.document.querySelector('.jflm-move-dialog'), null);
    const third = await openDialog();
    third.dialog.dispatchEvent(new third.page.Event('close', { bubbles: false }));
    await third.page.advance(50);
    assert.equal(third.page.dialogs.filter((l) => l[0] === 'close').length, 0, 'Back closed it; no second close');
});

// Escape in a text field with text empties it first, the next Escape closes; once closed,
// the focus is back on what opened the menu (the card's menu button here; the details page's More in the webcheck).
test('keys: Escape empties Filter folders or New folder first, the next closes; the focus goes back to the menu button', { skip }, async () => {
    const { page, dialog } = await openDialog();
    const menuButton = page.document.querySelector('.card .itemAction');
    const filter = dialog.querySelector('input.jflm-searchbox');
    filter.focus();
    type(page, filter, 'Coll');
    await page.advance(500);
    assert.equal(fetches(page, '/Move/Folders').slice(-1)[0].query.filter, 'Coll');
    page.key(filter, 'Escape');
    assert.equal(filter.value, '');
    assert.ok(page.document.querySelector('.jflm-move-dialog'), 'still open');
    await page.advance(500);
    assert.equal(fetches(page, '/Move/Folders').slice(-1)[0].query.filter, undefined, 'the list without the filter again');

    const newFolder = dialog.querySelector('input.jflm-move-new-folder');
    newFolder.focus();
    type(page, newFolder, 'Later');
    await page.advance(500);
    const plans = fetches(page, '/Move/Plan').length;
    assert.equal(fetches(page, '/Move/Plan').slice(-1)[0].body.Destination.NewFolderName, 'Later', 'planned with it');
    page.key(newFolder, 'Escape');
    assert.equal(newFolder.value, '');
    assert.ok(page.document.querySelector('.jflm-move-dialog'), 'still open');
    await page.advance(500);
    assert.equal(fetches(page, '/Move/Plan').length, plans + 1, 'planned again');
    assert.equal(fetches(page, '/Move/Plan').slice(-1)[0].body.Destination.NewFolderName, null, 'planned without the new folder');

    page.key(newFolder, 'Escape');
    await page.advance(100);
    assert.equal(page.document.querySelector('.jflm-move-dialog'), null, 'the second Escape closes');
    assert.equal(page.document.activeElement, menuButton, 'the focus is back on the card\'s menu button');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('with CanMove the Move button shows, disabled while planning or blocked, and the preview-only note goes', { skip }, async () => {
    const { page, dialog } = await openDialog({ caps: { CanMove: true }, plan: () => plan({ Blockers: [{ Code: 'move.playing', Message: 'Playing.' }] }) });
    const button = dialog.querySelector('.jflm-move-run');
    assert.equal(button.classList.contains('hide'), false);
    assert.equal(dialog.querySelector('.jflm-move-preview-only').classList.contains('hide'), true);
    assert.equal(dialog.querySelector('.jflm-move-preview-only-lead').classList.contains('hide'), true, 'no "Preview only" in the intro');
    assert.equal(button.disabled, true);
    page.dispatch(dialog.querySelector('.jflm-move-preview-again'), 'click');
    await page.advance(50);
    assert.equal(button.disabled, true, 'blocked');
    assert.equal(text(button), 'Move 3 files');
    page.respond('POST', '/LibraryMaintenance/Move/Plan', () => plan());
    page.dispatch(dialog.querySelector('.jflm-move-preview-again'), 'click');
    await page.advance(50);
    assert.equal(button.disabled, false);
    assert.equal(text(dialog.querySelector('.jflm-move-preview-status')), 'It can move there: nothing stands in the way.', 'can: a build that moves');
});
