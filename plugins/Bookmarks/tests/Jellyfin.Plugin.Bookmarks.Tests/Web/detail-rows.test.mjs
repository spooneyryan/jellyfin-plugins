// The details page's Bookmarks and Clips rows (52-detail-rows.js, since 1.6.0 / 2.4.0) in the kit's fake page with the
// built ClientScript. PageHostingTests runs it:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfbm node --test plugins/Bookmarks/tests/Jellyfin.Plugin.Bookmarks.Tests/Web/detail-rows.test.mjs
// The cards' images (the trickplay tile as jellyfin-web's scrubber finds it, else the chapter image, else the artwork; a
// clip's own artwork first), their order, the rows hidden while empty, what a click plays, the menus' actions (the list's
// handlers and My Clips' clipActions), the refresh on a change but not on a clip's progress tick, and the mobile and TV
// cards. bookmarks.test.mjs covers the Bookmarks row's markup, its menu's labels and its dialogs; detail-tv.test.mjs the
// TV jump past Jellyfin Enhanced's reviews.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');

const CAPABILITIES = {
    ClipsEnabled: true,
    CanCreateClips: true,
    CanDownloadClips: true,
    MaxClipDurationSeconds: 600,
    DefaultClipMode: 'Accurate',
    IsAdmin: false,
    StoreReadOnly: false,
    MaxNameLength: 100,
    MaxNoteLength: 500,
    Pages: { Bookmarks: true, Clips: true }
};

// A 60-second movie with jellyfin-web's trickplay of the sandbox's: 320x180 tiles, 10x10 a sheet, one every 10 s.
function movie(extra) {
    return Object.assign({ Id: 'item1', Name: 'Chaptered Movie', Type: 'Movie', MediaType: 'Video', IsFolder: false, LocationType: 'FileSystem' }, extra || {});
}

function trickplay(info, sourceId) {
    const byWidth = {};
    byWidth[String(info.Width)] = Object.assign({ TileWidth: 10, TileHeight: 10, Interval: 10000, Bandwidth: 1000 }, info);
    const all = {};
    all[sourceId || 'item1'] = byWidth;
    return all;
}

function bookmark(id, seconds, extra) {
    return Object.assign({ Id: id, ItemId: 'item1', StartTicks: seconds * 10000000, EndTicks: null, Name: 'At ' + seconds, Note: null, Clip: null, UpdatedAt: '2026-11-01T10:00:00Z' }, extra || {});
}

// A clip as GET /Bookmarks/Clips lists it; extra.Clip merges into its clip.
function clip(id, startSeconds, endSeconds, extra) {
    const o = extra || {};
    const c = Object.assign({
        Status: 'Completed', State: 'Ready', Mode: 'Accurate', StartTicks: startSeconds * 10000000, EndTicks: endSeconds * 10000000,
        DurationTicks: (endSeconds - startSeconds) * 10000000, IsOutdated: false, FileName: 'Chaptered Movie - ' + id + '.mp4',
        FileSize: 4096, FileExists: true, IsNameOutdated: false, InLibrary: false, Container: 'mp4', RootAvailable: true
    }, o.Clip || {});
    return Object.assign({
        Id: id, BookmarkDeleted: false, Origin: 'Bookmarks', Name: 'Clip ' + id, Note: null, ItemId: 'item1', ItemName: 'Chaptered Movie',
        ItemType: 'Movie', ItemAvailable: true, MediaSourceId: null, AudioStreamIndex: null, BookmarkStartTicks: c.StartTicks,
        BookmarkEndTicks: c.EndTicks, CreatedAt: '2026-11-01T10:00:00Z', UpdatedAt: '2026-11-01T10:00:00Z'
    }, o, { Clip: c });
}

async function start(options) {
    const o = options || {};
    const page = createPage();
    if (o.layout) {
        page.document.documentElement.classList.add('layout-' + o.layout);
    }

    page.respond('GET', '/Bookmarks/Capabilities', Object.assign({}, CAPABILITIES, o.capabilities || {}));
    page.respond('GET', '/Bookmarks', o.list || []);
    page.respond('GET', '/Bookmarks/Clips', { Items: o.clips || [], TotalRecordCount: (o.clips || []).length, StartIndex: 0 });
    page.respond('GET', '/Bookmarks/Clips/Media', o.media || []);
    page.respond('GET', '/Sessions', [{ Id: 's1', DeviceId: 'device-1' }]);
    page.respond('POST', '/Sessions/s1/Playing', null, 204);
    page.run(source, file);
    await page.advance(50);
    page.installApiClient({ userId: 'user-1', items: Object.assign({ item1: o.movie || movie() }, o.items || {}) });
    await page.advance(50);
    const buttons = page.element('div', { class: 'mainDetailButtons' }, [page.element('button', { class: 'btnMoreCommands' })]);
    const content = page.element('div', { class: 'detailPageContent' }, (o.sections || MOVIE_SECTIONS).map((id) => section(page, id)));
    const view = page.element('div', { id: 'itemDetailPage', class: 'page' }, [buttons, content]);
    page.document.body.append(view);
    page.context.location.hash = '#/details?id=item1&serverId=server-1';
    page.dispatch(view, 'viewshow');
    await page.advance(400);
    return { page, view, content, bookmarksRow: content.querySelector('.jfbm-detail-bookmarks'), clipsRow: content.querySelector('.jfbm-detail-clips') };
}

// jellyfin-web's sections of a movie's page, in its order (10.11.5 and 12.4: Cast & Crew, then Guest Stars, hidden
// without guest stars but always there, Special Features, Scenes, then More Like This); '-hide' marks a hidden one.
const MOVIE_SECTIONS = ['castCollapsible', 'guestCastCollapsible-hide', 'specialsCollapsible', 'scenesCollapsible', 'similarCollapsible'];

function section(page, id) {
    const hidden = /-hide$/.test(id);
    return page.element('div', { id: id.replace(/-hide$/, ''), class: 'verticalSection detailVerticalSection' + (hidden ? ' hide' : '') });
}

// The content's children: a section's id, ours by their jfbm-detail class.
function order(content) {
    return content.children.map((c) => c.id || c.className.split(' ').filter((n) => n.indexOf('jfbm-detail') === 0)[0]);
}

function bookmarkCards(root) {
    return root.querySelectorAll('.jfbm-bookmark-card');
}

function clipCards(root) {
    return root.querySelectorAll('.jfbm-clip-card');
}

function texts(card) {
    return card.querySelectorAll('.cardText').map((t) => t.textContent);
}

// What paints a card's image: the frame box of a tile not 16:9, else the image box.
function painted(card) {
    return card.querySelector('.jfbm-rowcard-frame') || card.querySelector('.cardImageContainer');
}

function menu(page) {
    return page.document.querySelectorAll('.jfbm-backdrop .jfbm-panel').find((p) => !!p.querySelector('.jfbm-row-menu')) || null;
}

function panel(page, title) {
    return page.document.querySelectorAll('.jfbm-backdrop .jfbm-panel').find((p) => p.querySelector('h3').textContent === title) || null;
}

function labels(dialog) {
    return dialog.querySelectorAll('.jfbm-row-menu-item').map((b) => b.textContent);
}

async function openMenu(page, card) {
    card.querySelector('.cardOverlayButton-br button').click();
    await page.advance(10);
    return menu(page);
}

async function act(page, card, label) {
    const dialog = await openMenu(page, card);
    const item = dialog.querySelectorAll('.jfbm-row-menu-item').find((b) => b.textContent === label);
    assert.ok(item, label + ' in: ' + labels(dialog).join(', '));
    item.click();
    await page.advance(10);
}

function requests(page, method, path) {
    return page.fetchLog.filter((f) => f.method === method && (path instanceof RegExp ? path.test(f.path) : f.path === path));
}

test('a bookmark card shows the trickplay tile of its moment, cropped as jellyfin-web\'s scrubber does, in time order', { skip }, async () => {
    const { page, content, bookmarksRow } = await start({
        movie: movie({ Trickplay: trickplay({ Width: 320, Height: 180, ThumbnailCount: 6 }) }),
        list: [bookmark('late', 75), bookmark('b1', 15), bookmark('b0', 0)]
    });
    assert.deepEqual(order(content), ['castCollapsible', 'guestCastCollapsible', 'jfbm-detail-bookmarks', 'specialsCollapsible', 'scenesCollapsible', 'jfbm-detail-clips', 'similarCollapsible'], 'Bookmarks right after Cast & Crew (and the hidden Guest Stars), Clips right after Scenes');
    assert.ok(bookmarksRow);
    const cards = bookmarkCards(content);
    assert.deepEqual(cards.map((c) => c.getAttribute('data-jfbm-card')), ['b0', 'b1', 'late'], 'in time order');
    // 15 s: thumbnail floor(15000 / 10000) = 1 of the 6 on sheet 0: column 1, row 0.
    const tile = painted(cards[1]);
    assert.equal(tile.style.backgroundImage, 'url("http://127.0.0.1:8097/Videos/item1/Trickplay/320/0.jpg?ApiKey=test-token&MediaSourceId=item1")');
    assert.equal(tile.style.backgroundSize, '1000% 1000%', 'ten columns, ten rows: Jellyfin pads a sheet to its full grid (six thumbnails here)');
    assert.equal(tile.style.backgroundPosition, '11.111% 0%', 'column 1 of 0..9');
    assert.equal(painted(cards[0]).style.backgroundPosition, '0% 0%');
    // Past the last thumbnail (75 s of a 60 s trickplay): the last one, 5.
    assert.equal(painted(cards[2]).style.backgroundPosition, '55.556% 0%');
    assert.equal(cards[1].querySelector('.jfbm-rowcard-frame'), null, 'a 16:9 tile paints the card\'s own image box');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

// Where the rows go: Bookmarks right after Cast & Crew (since 1.6.1 / 2.4.1), after Guest Stars when that comes right
// under it; Clips right after Scenes; without Cast & Crew both after Scenes, as in 1.6.0 / 2.4.0, else at the end.
const ROWS = { list: [bookmark('a', 5)], clips: [clip('c', 1, 3)], media: [{ Id: 'c', LibraryItemId: null, Artwork: [] }] };

test('an episode: Bookmarks after Guest Stars, which stays right under Cast & Crew, so before Special Features', { skip }, async () => {
    const { page, content, bookmarksRow, clipsRow } = await start(Object.assign({ sections: ['castCollapsible', 'guestCastCollapsible', 'specialsCollapsible', 'scenesCollapsible', 'similarCollapsible'] }, ROWS));
    assert.deepEqual(order(content), ['castCollapsible', 'guestCastCollapsible', 'jfbm-detail-bookmarks', 'specialsCollapsible', 'scenesCollapsible', 'jfbm-detail-clips', 'similarCollapsible']);
    assert.equal(bookmarksRow.classList.contains('hide'), false);
    assert.equal(clipsRow.classList.contains('hide'), false);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Guest Stars not right under Cast & Crew, or not there: Bookmarks right after Cast & Crew; a hidden Cast & Crew is still the place', { skip }, async () => {
    let s = await start(Object.assign({ sections: ['castCollapsible', 'specialsCollapsible', 'guestCastCollapsible', 'scenesCollapsible', 'similarCollapsible'] }, ROWS));
    assert.deepEqual(order(s.content), ['castCollapsible', 'jfbm-detail-bookmarks', 'specialsCollapsible', 'guestCastCollapsible', 'scenesCollapsible', 'jfbm-detail-clips', 'similarCollapsible']);
    s = await start(Object.assign({ sections: ['castCollapsible', 'scenesCollapsible', 'similarCollapsible'] }, ROWS));
    assert.deepEqual(order(s.content), ['castCollapsible', 'jfbm-detail-bookmarks', 'scenesCollapsible', 'jfbm-detail-clips', 'similarCollapsible']);
    // An item without people: jellyfin-web keeps both people sections, hidden; the rows go where they would.
    s = await start(Object.assign({ sections: ['castCollapsible-hide', 'guestCastCollapsible-hide', 'specialsCollapsible', 'scenesCollapsible-hide', 'similarCollapsible'] }, ROWS));
    assert.deepEqual(order(s.content), ['castCollapsible', 'guestCastCollapsible', 'jfbm-detail-bookmarks', 'specialsCollapsible', 'scenesCollapsible', 'jfbm-detail-clips', 'similarCollapsible']);
    assert.equal(s.page.errors.length, 0, s.page.errors.join('\n'));
});

test('without Cast & Crew both rows go right after Scenes, Bookmarks first (Guest Stars alone is no place); without Scenes too, at the end', { skip }, async () => {
    let s = await start(Object.assign({ sections: ['specialsCollapsible', 'scenesCollapsible', 'similarCollapsible'] }, ROWS));
    assert.deepEqual(order(s.content), ['specialsCollapsible', 'scenesCollapsible', 'jfbm-detail-bookmarks', 'jfbm-detail-clips', 'similarCollapsible']);
    s = await start(Object.assign({ sections: ['guestCastCollapsible', 'specialsCollapsible', 'scenesCollapsible', 'similarCollapsible'] }, ROWS));
    assert.deepEqual(order(s.content), ['guestCastCollapsible', 'specialsCollapsible', 'scenesCollapsible', 'jfbm-detail-bookmarks', 'jfbm-detail-clips', 'similarCollapsible']);
    s = await start(Object.assign({ sections: ['specialsCollapsible', 'similarCollapsible'] }, ROWS));
    assert.deepEqual(order(s.content), ['specialsCollapsible', 'similarCollapsible', 'jfbm-detail-bookmarks', 'jfbm-detail-clips']);
    // Cast & Crew but no Scenes: Bookmarks still after Cast & Crew, Clips at the end.
    s = await start(Object.assign({ sections: ['castCollapsible', 'specialsCollapsible', 'similarCollapsible'] }, ROWS));
    assert.deepEqual(order(s.content), ['castCollapsible', 'jfbm-detail-bookmarks', 'specialsCollapsible', 'similarCollapsible', 'jfbm-detail-clips']);
    assert.equal(s.page.errors.length, 0, s.page.errors.join('\n'));
});

test('the places hold on every visit: another item in the same view, back to the first, the cached view shown again, a redraw', { skip }, async () => {
    const expected = ['castCollapsible', 'guestCastCollapsible', 'jfbm-detail-bookmarks', 'specialsCollapsible', 'scenesCollapsible', 'jfbm-detail-clips', 'similarCollapsible'];
    const withClip = bookmark('done', 30, { EndTicks: 400000000, Clip: { Status: 'Completed', FileName: 'done.mp4', FileSize: 9, IsOutdated: false } });
    const { page, view, content } = await start(Object.assign({}, ROWS, { list: [bookmark('a', 5), withClip], items: { item2: movie({ Id: 'item2', Name: 'Other Movie' }) } }));
    assert.deepEqual(order(content), expected);
    const visit = async (id) => {
        page.context.location.hash = '#/details?id=' + id + '&serverId=server-1';
        page.dispatch(view, 'viewshow');
        await page.advance(400);
    };
    await visit('item2');
    assert.deepEqual(order(content), expected, 'one of each, in the same places');
    assert.equal(content.querySelector('.jfbm-detail-bookmarks').getAttribute('data-item-id'), 'item2');
    assert.equal(content.querySelector('.jfbm-detail-clips').getAttribute('data-item-id'), 'item2');
    await visit('item1');
    assert.deepEqual(order(content), expected);
    assert.equal(content.querySelector('.jfbm-detail-bookmarks').getAttribute('data-item-id'), 'item1');
    const rows = [content.querySelector('.jfbm-detail-bookmarks'), content.querySelector('.jfbm-detail-clips')];
    // Shown again from jellyfin-web's view cache (Back): the same rows, where they were.
    page.dispatch(view, 'viewshow');
    await page.advance(400);
    assert.deepEqual([content.querySelector('.jfbm-detail-bookmarks'), content.querySelector('.jfbm-detail-clips')], rows);
    assert.deepEqual(order(content), expected);
    // A change (a clip deleted from a bookmark card) reads and draws both rows again, in place.
    page.respond('DELETE', '/Bookmarks/done/Clip', null, 204);
    const reads = requests(page, 'GET', '/Bookmarks/Clips').length;
    await act(page, bookmarkCards(content)[1], 'Delete clip');
    await page.advance(400);
    assert.equal(requests(page, 'DELETE', '/Bookmarks/done/Clip').length, 1);
    assert.ok(requests(page, 'GET', '/Bookmarks/Clips').length > reads, 'the Clips row read again');
    assert.deepEqual([content.querySelector('.jfbm-detail-bookmarks'), content.querySelector('.jfbm-detail-clips')], rows);
    assert.deepEqual(order(content), expected);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('trickplay maths over several sheets: sheet, column and row, the last sheet a full grid too; the bookmark\'s source and a width up to 480', { skip }, async () => {
    const sources = {
        item1: { 320: { Width: 320, Height: 134, TileWidth: 10, TileHeight: 10, ThumbnailCount: 250, Interval: 10000 } },
        other: {
            320: { Width: 320, Height: 180, TileWidth: 10, TileHeight: 10, ThumbnailCount: 250, Interval: 10000 },
            480: { Width: 480, Height: 270, TileWidth: 5, TileHeight: 5, ThumbnailCount: 250, Interval: 10000 },
            640: { Width: 640, Height: 360, TileWidth: 10, TileHeight: 10, ThumbnailCount: 250, Interval: 10000 }
        }
    };
    const { content } = await start({
        movie: movie({ Trickplay: sources }),
        list: [bookmark('a', 1234), bookmark('b', 2234), bookmark('c', 1234, { MediaSourceId: 'other' })]
    });
    const [a, c, b] = bookmarkCards(content);
    // 1234 s: thumbnail 123, sheet 1 (100 a sheet), offset 23: column 3, row 2 of a full sheet (10 rows).
    let tile = painted(a);
    assert.match(tile.style.backgroundImage, /\/Videos\/item1\/Trickplay\/320\/1\.jpg\?ApiKey=test-token&MediaSourceId=item1"\)$/);
    assert.equal(tile.style.backgroundSize, '1000% 1000%');
    assert.equal(tile.style.backgroundPosition, '33.333% 22.222%');
    // A 320x134 tile (2.39:1) sits whole in a box of its shape, letterboxed, never stretched to 16:9.
    assert.equal(tile.className, 'jfbm-rowcard-frame');
    assert.equal(tile.style.aspectRatio, String(320 / 134));
    assert.equal(tile.style.width, '100%');
    assert.ok(a.querySelector('.cardImageContainer').classList.contains('jfbm-rowcard-letterbox'));
    // 2234 s: thumbnail 223, sheet 2, which holds the last 50 on a full 10x10 grid (padded): column 3, row 2 of 0..9.
    tile = painted(b);
    assert.match(tile.style.backgroundImage, /\/Trickplay\/320\/2\.jpg\?/);
    assert.equal(tile.style.backgroundSize, '1000% 1000%');
    assert.equal(tile.style.backgroundPosition, '33.333% 22.222%');
    // The bookmark's own media source, at the widest width up to 480 (5x5 tiles): thumbnail 123, sheet 4, offset 23.
    tile = painted(c);
    assert.equal(tile.style.backgroundImage, 'url("http://127.0.0.1:8097/Videos/item1/Trickplay/480/4.jpg?ApiKey=test-token&MediaSourceId=other")');
    assert.equal(tile.style.backgroundSize, '500% 500%');
    assert.equal(tile.style.backgroundPosition, '75% 100%');
});

test('without trickplay the last chapter image at or before the moment, else the video\'s backdrop, else its primary image, else an icon', { skip }, async () => {
    const chapters = [
        { StartPositionTicks: 0, Name: 'Opening', ImageTag: 'c0' },
        { StartPositionTicks: 120000000, Name: 'Two', ImageTag: 'c1' },
        { StartPositionTicks: 240000000, Name: 'Three', ImageTag: null }
    ];
    let s = await start({ movie: movie({ Chapters: chapters, BackdropImageTags: ['bd'] }), list: [bookmark('a', 5), bookmark('b', 15), bookmark('c', 30)] });
    assert.deepEqual(bookmarkCards(s.content).map((c) => painted(c).style.backgroundImage), [
        'url("http://127.0.0.1:8097/Items/item1/Images/Chapter/0?maxWidth=400&tag=c0")',
        'url("http://127.0.0.1:8097/Items/item1/Images/Chapter/1?maxWidth=400&tag=c1")',
        'url("http://127.0.0.1:8097/Items/item1/Images/Chapter/1?maxWidth=400&tag=c1")'
    ], 'the third chapter has no image: the second\'s');
    assert.equal(painted(bookmarkCards(s.content)[0]).style.backgroundSize, undefined, 'a whole image, as jellyfin-web\'s own chapter cards');

    s = await start({ movie: movie({ BackdropImageTags: ['bd'], ImageTags: { Primary: 'p' } }), list: [bookmark('a', 5)] });
    assert.equal(painted(bookmarkCards(s.content)[0]).style.backgroundImage, 'url("http://127.0.0.1:8097/Items/item1/Images/Backdrop/0?maxWidth=400&tag=bd")');
    s = await start({ movie: movie({ ImageTags: { Primary: 'p' } }), list: [bookmark('a', 5)] });
    assert.equal(painted(bookmarkCards(s.content)[0]).style.backgroundImage, 'url("http://127.0.0.1:8097/Items/item1/Images/Primary?maxWidth=400&tag=p")');
    s = await start({ list: [bookmark('a', 5)] });
    const box = bookmarkCards(s.content)[0].querySelector('.cardImageContainer');
    assert.ok(box.classList.contains('defaultCardBackground'));
    assert.equal(box.querySelector('.cardImageIcon').classList.contains('bookmark'), true);
});

test('both rows hidden while empty; clips off on the server: no clips request and no Clips row', { skip }, async () => {
    let s = await start();
    assert.ok(s.bookmarksRow.classList.contains('hide'));
    assert.ok(s.clipsRow.classList.contains('hide'));
    assert.equal(requests(s.page, 'GET', '/Bookmarks/Clips').length, 1);
    assert.equal(requests(s.page, 'GET', '/Bookmarks/Clips/Media').length, 0, 'no clips: no media request');

    s = await start({ capabilities: { ClipsEnabled: false, CanCreateClips: false }, list: [bookmark('a', 5)], clips: [clip('x', 1, 2)] });
    assert.equal(s.bookmarksRow.classList.contains('hide'), false);
    assert.ok(s.clipsRow.classList.contains('hide'));
    assert.equal(requests(s.page, 'GET', '/Bookmarks/Clips').length, 0);
    assert.equal(s.page.errors.length, 0, s.page.errors.join('\n'));
});

test('a clip card: only clips with a file, in time order; its own artwork, else the frame at its start; outdated ones marked', { skip }, async () => {
    const { page, content, clipsRow } = await start({
        movie: movie({ Trickplay: trickplay({ Width: 320, Height: 180, ThumbnailCount: 6 }) }),
        clips: [
            clip('b5', 30, 40),
            clip('b6', 5, 17, { Name: null, Clip: { IsOutdated: true } }),
            clip('b7', 1, 2, { Clip: { State: 'Missing', FileExists: false } }),
            clip('b8', 3, 4, { Clip: { State: 'InProgress', Status: 'Queued', FileName: null, FileExists: null } })
        ],
        media: [
            { Id: 'b5', LibraryItemId: null, Artwork: [{ Kind: 'Poster', Tag: '1' }, { Kind: 'Thumb', Tag: '638' }] },
            { Id: 'b6', LibraryItemId: 'lib6', Artwork: [] }
        ]
    });
    assert.equal(content.children[content.children.indexOf(clipsRow) - 1].id, 'scenesCollapsible', 'right after Scenes');
    assert.equal(clipsRow.classList.contains('hide'), false);
    assert.equal(clipsRow.querySelector('h2').firstChild.textContent, 'Clips');
    assert.deepEqual(requests(page, 'GET', '/Bookmarks/Clips/Media').map((f) => f.query), [{ ids: 'b6,b5' }]);
    const [six, five] = clipCards(content);
    assert.deepEqual(clipCards(content).map((c) => c.getAttribute('data-jfbm-card')), ['b6', 'b5'], 'no missing file nor a first clip being made');
    assert.deepEqual(texts(five), ['Clip b5', '0:10 · 0:30–0:40']);
    assert.equal(painted(five).style.backgroundImage, 'url("http://127.0.0.1:8097/Bookmarks/Clips/b5/Artwork/Thumb?tag=638&ApiKey=test-token")', 'the thumb before the poster');
    assert.deepEqual(texts(six), ['Chaptered Movie - b6', '0:12 · 0:05–0:17', 'Outdated: the range changed'], 'no name: its file\'s');
    assert.equal(six.querySelector('.jfbm-card-warning').textContent, 'Outdated: the range changed');
    assert.equal(six.querySelector('.indicatorIcon').classList.contains('update'), true);
    assert.equal(six.className, 'card overflowBackdropCard card-hoverable jfbm-rowcard jfbm-clip-card jfbm-clip-card-outdated');
    assert.match(painted(six).style.backgroundImage, /\/Videos\/item1\/Trickplay\/320\/0\.jpg/, 'no artwork: the frame at its start');
    assert.equal(painted(six).style.backgroundPosition, '0% 0%');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a clip card plays a clip in a library with Jellyfin\'s player, else My Clips\' Preview, else its video from the clip\'s start', { skip }, async () => {
    const clips = [clip('b5', 30, 40), clip('b6', 5, 17)];
    const media = [{ Id: 'b5', LibraryItemId: null, Artwork: [] }, { Id: 'b6', LibraryItemId: 'LIB-6', Artwork: [] }];
    let s = await start({ clips, media });
    const [six, five] = clipCards(s.content);
    six.querySelector('.cardOverlayFab-primary').click();
    await s.page.advance(50);
    assert.deepEqual(requests(s.page, 'POST', '/Sessions/s1/Playing').map((f) => f.query), [{ playCommand: 'PlayNow', itemIds: 'lib6', startPositionTicks: '0' }], 'the library item, from its start');
    five.querySelector('.cardImageContainer').click();
    await s.page.advance(20);
    const preview = panel(s.page, 'Clip b5');
    assert.ok(preview, 'the preview of a clip outside the libraries');
    assert.match(preview.querySelector('video').getAttribute('src'), /\/Bookmarks\/b5\/Clip\/File\?ApiKey=test-token&download=false$/);
    s.page.key(s.page.document.body, 'Escape');
    await s.page.advance(20);

    s = await start({ clips, media, capabilities: { CanDownloadClips: false } });
    s.page.key(clipCards(s.content)[1].querySelector('.cardImageContainer'), 'Enter');
    await s.page.advance(50);
    assert.deepEqual(requests(s.page, 'POST', '/Sessions/s1/Playing').map((f) => f.query), [{ playCommand: 'PlayNow', itemIds: 'item1', startPositionTicks: '300000000' }], 'no downloads: the video at the clip\'s start (Enter on the card)');
    assert.equal(panel(s.page, 'Clip b5'), null);
});

test('a clip card\'s menu is My Clips\' actions for it: Delete asks, deletes and reads both rows again; Edit bookmark opens the editor', { skip }, async () => {
    const { page, content } = await start({ list: [bookmark('b5', 30, { EndTicks: 400000000 })], clips: [clip('b5', 30, 40)], media: [{ Id: 'b5', LibraryItemId: null, Artwork: [] }] });
    page.respond('DELETE', '/Bookmarks/b5/Clip', null, 204);
    page.respond('GET', '/Bookmarks/b5', bookmark('b5', 30, { EndTicks: 400000000 }));
    const card = clipCards(content)[0];
    const dialog = await openMenu(page, card);
    assert.equal(dialog.querySelector('h3').textContent, 'Clip b5');
    assert.deepEqual(labels(dialog), ['Play the clip', 'Preview', 'Download', 'Play the video at the clip\'s start', 'Edit bookmark', 'Rename clip', 'Make the clip again', 'Delete clip', 'View in My Clips']);
    page.key(page.document.body, 'Escape');
    await page.advance(10);

    await act(page, card, 'Edit bookmark');
    await page.advance(20);
    assert.ok(panel(page, 'Edit bookmark'), 'the editor, on the bookmark as the server has it');
    page.key(page.document.body, 'Escape');
    await page.advance(10);

    const lists = requests(page, 'GET', '/Bookmarks').length;
    const clipLists = requests(page, 'GET', '/Bookmarks/Clips').length;
    page.respond('GET', '/Bookmarks/Clips', { Items: [], TotalRecordCount: 0, StartIndex: 0 });
    await act(page, card, 'Delete clip');
    const confirm = panel(page, 'Delete clip?');
    assert.ok(confirm, 'My Clips\' confirmation');
    confirm.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Delete').click();
    await page.advance(50);
    assert.equal(requests(page, 'DELETE', '/Bookmarks/b5/Clip').length, 1);
    assert.ok(requests(page, 'GET', '/Bookmarks').length > lists, 'the Bookmarks row read again');
    assert.ok(requests(page, 'GET', '/Bookmarks/Clips').length > clipLists, 'the Clips row read again');
    assert.ok(content.querySelector('.jfbm-detail-clips').classList.contains('hide'), 'its last clip gone: hidden');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a bookmark card\'s menu runs the list\'s handlers: make, cancel and delete its clip, play from here', { skip }, async () => {
    const running = bookmark('run', 10, { EndTicks: 200000000, Clip: { Status: 'Running', Progress: 10 } });
    const done = bookmark('done', 30, { EndTicks: 400000000, Clip: { Status: 'Completed', FileName: 'done.mp4', FileSize: 9, IsOutdated: false } });
    const { page, content } = await start({ list: [running, done] });
    page.respond('GET', '/Bookmarks/run', running);
    page.respond('POST', '/Bookmarks/run/Clip/Cancel', null, 204);
    page.respond('DELETE', '/Bookmarks/done/Clip', null, 204);
    page.respond('POST', '/Bookmarks/done/Clip', Object.assign({}, done, { Clip: { Status: 'Queued' } }));
    const [runCard, doneCard] = bookmarkCards(content);
    assert.deepEqual(texts(runCard), ['At 10', '0:10–0:20', 'Making the clip…']);
    await act(page, runCard, 'Cancel the clip');
    assert.equal(requests(page, 'POST', '/Bookmarks/run/Clip/Cancel').length, 1);
    await act(page, doneCard, 'Delete clip');
    assert.equal(requests(page, 'DELETE', '/Bookmarks/done/Clip').length, 1);
    await act(page, bookmarkCards(content)[1], 'Make the clip again');
    assert.deepEqual(requests(page, 'POST', '/Bookmarks/done/Clip').map((f) => f.body), [{ Mode: 'Accurate' }]);
    await act(page, bookmarkCards(content)[1], 'Play from here');
    await page.advance(50);
    assert.deepEqual(requests(page, 'POST', '/Sessions/s1/Playing')[0].query, { playCommand: 'PlayNow', itemIds: 'item1', startPositionTicks: '300000000' });
});

test('a clip\'s progress ticks never draw the rows again nor read the clips; its end does both', { skip }, async () => {
    const running = bookmark('run', 10, { EndTicks: 200000000, Clip: { Status: 'Running', Progress: 10 } });
    const { page, content } = await start({ list: [running] });
    const row = content.querySelector('.jfbm-detail-bookmarks');
    const slider = row.querySelector('.jfbm-row-slider');
    const clipReads = requests(page, 'GET', '/Bookmarks/Clips').length;
    const ticked = Object.assign({}, running, { Clip: { Status: 'Running', Progress: 60 } });
    page.respond('GET', '/Bookmarks/run', ticked);
    page.respond('GET', '/Bookmarks', [ticked]);
    const card = bookmarkCards(content)[0];
    card.querySelector('.cardImageContainer').focus();
    await page.advance(2100);
    assert.ok(requests(page, 'GET', '/Bookmarks/run').length >= 1, 'the clip polled');
    assert.equal(row.querySelector('.jfbm-row-slider'), slider, 'the same cards: the focus and the scroll stay');
    assert.equal(page.document.activeElement, card.querySelector('.cardImageContainer'));
    assert.equal(requests(page, 'GET', '/Bookmarks/Clips').length, clipReads, 'a tick reads no clips');

    const finished = Object.assign({}, running, { Clip: { Status: 'Completed', FileName: 'run.mp4', FileSize: 9, IsOutdated: false } });
    page.respond('GET', '/Bookmarks/run', finished);
    page.respond('GET', '/Bookmarks', [finished]);
    page.respond('GET', '/Bookmarks/Clips', { Items: [clip('run', 10, 20)], TotalRecordCount: 1, StartIndex: 0 });
    await page.advance(2100);
    assert.notEqual(row.querySelector('.jfbm-row-slider'), slider, 'drawn again');
    assert.equal(page.document.activeElement.closest('.jfbm-bookmark-card').getAttribute('data-jfbm-card'), 'run', 'the focus back on the same card');
    assert.ok(requests(page, 'GET', '/Bookmarks/Clips').length > clipReads, 'the clips read again');
    assert.deepEqual(clipCards(content).map((c) => c.getAttribute('data-jfbm-card')), ['run']);
});

test('on a phone the More button in the corner of the image opens the menu; on TV the card is the stop, and the menu key opens it', { skip }, async () => {
    let s = await start({ layout: 'mobile', list: [bookmark('a', 5)], clips: [clip('c', 1, 3)], media: [{ Id: 'c', LibraryItemId: null, Artwork: [] }] });
    const card = bookmarkCards(s.content)[0];
    assert.equal(card.querySelector('.cardOverlayContainer'), null);
    const more = card.querySelector('.cardScalable > .cardOverlayButton-br');
    assert.equal(more.getAttribute('title'), 'More actions');
    more.click();
    await s.page.advance(10);
    assert.deepEqual(labels(menu(s.page)), ['Play from here', 'Edit', 'Delete']);
    s.page.key(s.page.document.body, 'Escape');
    await s.page.advance(10);
    assert.ok(clipCards(s.content)[0].querySelector('.cardScalable > .cardOverlayButton-br'));

    s = await start({ layout: 'tv', list: [bookmark('a', 5)] });
    const tv = bookmarkCards(s.content)[0];
    assert.equal(tv.getAttribute('tabindex'), '0');
    assert.ok(tv.classList.contains('show-focus'));
    s.page.dispatch(tv, 'command', { detail: { command: 'menu' } });
    await s.page.advance(10);
    assert.ok(menu(s.page), 'the menu key');
    s.page.key(s.page.document.body, 'Escape');
    await s.page.advance(10);
    s.page.key(tv, 'Enter');
    await s.page.advance(50);
    assert.equal(requests(s.page, 'POST', '/Sessions/s1/Playing').length, 1, 'OK plays');
});
