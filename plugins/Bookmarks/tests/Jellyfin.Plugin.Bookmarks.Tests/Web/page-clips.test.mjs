// The My Clips page (Web/js/70-page-clips.js) in the kit's fake page, with the built ClientScript.
// PageHostingTests runs it:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfbm node --test plugins/Bookmarks/tests/Jellyfin.Plugin.Bookmarks.Tests/Web/page-clips.test.mjs
// The page is registered as the server declares it and linked after My Bookmarks in "Library tools"; its toolbar (the
// search, State, Mode, Type, Sort, the deleted bookmarks' clips) reads and writes the state in the hash (deep links, Back
// and Forward; a value the hash says wrongly is the default and the hash is left as it came); the rows show each state,
// the badges, the poster and the link only for a video the viewer can open, and text as text; the summary line; paging;
// the poll of the clips being made (one Status request for all of them every 2.5 s, paused while the tab is hidden,
// stopped when none is left or the page is left, the state patched in place, a clip whose job ended read again with its
// video's id and drawn in place, a clip gone removed); the one landing of a highlight; the empty, no-match, error,
// loading, clips-off and read-only states; the page turned off; the links to it (My Bookmarks' header and clip lines,
// the player's list, the clip-ready toast), the TV layout and Plugin Pages' host; a Load more under way while the poll
// ends a clip skips none; the page left in Plugin Pages' hidden host view polls and reads nothing there; a highlight on
// a later page reads on to it (one the list doesn't hold is said), and the focus lands on its row. The row actions:
// each state's actions (an offline row, or one whose folder isn't recognised, only Play and
// Edit, no checkbox; read-only data, the download permission and clips turned off), Preview (MP4 and MKV, the error
// event's sentences), Download, Play, Edit bookmark, Make the clip again (its dialog, a refusal, the poll after), Cancel,
// Delete (the 409 in the dialog), Forget (409 and 503), Dismiss, the More menu, the bulk delete (Select all, batches of 200
// across pages with the progress, what the server keeps named apart and still selected, stopped when its dialog closes), the
// selection kept through redraws and the TV layout's Select toggle, and a clip started from the editor picked up.
// Fake-DOM nodes are compared with === inside assert.ok: a failing assert.equal on two of them makes Node inspect the
// whole fake document (found in testing: it ran out of memory).
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage, plain } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');

const SHELL = (key) => '<div class="jfbm-page" data-jfbm-page="' + key + '"><p class="jfbm-shell-note">This page needs the Bookmarks web UI, which is turned off or failed to load.</p></div>';
const SHELL_OFF = '<div class="jfbm-page"><p class="jfbm-shell-note">My Clips is turned off by your server admin.</p></div>';
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
    Shortcuts: { BookmarkNow: 'Shift+B' },
    ReservedShortcuts: ['M', 'K', 'B'],
    ShowPlayerButtons: true,
    AskForName: false,
    AskForNameIsDefault: true,
    AskForNameDefault: false,
    PauseWhileNaming: false,
    SuggestChapterNames: true,
    Pages: { Bookmarks: true, Clips: true }
};
const HEAT = 'aaaa0000-0000-0000-0000-000000000001';
const GONE = 'bbbb0000-0000-0000-0000-000000000002';
const SHOW = 'cccc0000-0000-0000-0000-000000000003';
// A bookmark's id, also its clip's (a GUID as the server writes it, without dashes).
const B1 = 'b1b1b1b1000000000000000000000001';
const T = 10000000;
const key = (id) => String(id).replace(/-/g, '').toLowerCase();

// A clip as GET /Bookmarks/Clips answers it (BookmarkClipItemDto), a Ready Fast one of Heat by default.
function clip(id, extra, clipExtra) {
    return Object.assign({
        Id: id,
        BookmarkDeleted: false,
        Origin: 'Native',
        Name: null,
        Note: null,
        ItemId: HEAT,
        ItemName: 'Heat (1995)',
        ItemType: 'Movie',
        ItemAvailable: true,
        MediaSourceId: null,
        AudioStreamIndex: null,
        BookmarkStartTicks: 60 * T,
        BookmarkEndTicks: 64 * T,
        Clip: Object.assign({
            Status: 'Completed',
            Mode: 'Fast',
            StartTicks: 60 * T,
            EndTicks: 64 * T,
            IsOutdated: false,
            Progress: 100,
            FileName: 'Heat (1995) - ' + id + ' [00.01.00-00.01.04].mkv',
            FileSize: 1258291,
            RequestedAt: '2026-10-10T10:00:00.0000000Z',
            CompletedAt: '2026-10-10T10:00:05.0000000Z',
            Error: null,
            State: 'Ready',
            Container: 'mkv',
            DurationTicks: 4 * T,
            FileExists: true,
            IsNameOutdated: false,
            LastJobFailed: false,
            InLibrary: false,
            LibraryName: null,
            RootAvailable: true,
            MadeAt: '2026-10-10T10:00:05.0000000Z'
        }, clipExtra || {}),
        CreatedAt: '2026-10-01T10:00:00.0000000Z',
        UpdatedAt: '2026-10-05T10:00:00.0000000Z'
    }, extra || {});
}

const running = (progress) => ({ Status: 'Running', State: 'InProgress', Progress: progress, FileName: null, FileSize: null, CompletedAt: null, Container: null, FileExists: false, RootAvailable: null, MadeAt: null });
const queued = () => ({ Status: 'Queued', State: 'InProgress', Progress: null, FileName: null, FileSize: null, CompletedAt: null, Container: null, FileExists: false, RootAvailable: null, MadeAt: null });

// Every state and badge, in the server's order (the clips being made first).
function fixtures() {
    return [
        clip('c2', { Name: 'Car chase' }, running(43)),
        clip('c3', { Note: 'Pilot cold open\nsecond line', ItemId: SHOW, ItemName: 'Show S01E02 - Pilot', ItemType: 'Episode' }, Object.assign(queued(), { Mode: 'Accurate' })),
        clip('c1', { Name: 'Bank job' }, { Mode: 'Accurate', Container: 'mp4', FileName: 'Heat (1995) - Bank job [01.02.03-01.05.00].mp4', FileSize: 47185920, StartTicks: 3723 * T, EndTicks: 3900 * T, DurationTicks: 177 * T, InLibrary: true, LibraryName: 'Clips' }),
        clip('c4', {}, { Status: 'Failed', State: 'Failed', FileName: null, FileSize: null, CompletedAt: '2026-10-09T10:00:00.0000000Z', Error: 'The clip drive is full.', Container: null, FileExists: false, RootAvailable: null, MadeAt: null }),
        clip('c5', { Name: 'Called off' }, { Status: 'Cancelled', State: 'Failed', FileName: null, FileSize: null, Error: null, Container: null, FileExists: false, RootAvailable: null, MadeAt: null }),
        clip('c6', { Name: 'Gone file' }, { State: 'Missing', FileExists: false, MadeAt: null }),
        clip('c7', { Name: 'On the stick' }, { State: 'FolderUnavailable', FileExists: false, RootAvailable: false, MadeAt: null }),
        clip('c8', { Name: 'Kept one', BookmarkDeleted: true }, {}),
        clip('c9', { Name: 'Renamed' }, { IsOutdated: true, IsNameOutdated: true, LastJobFailed: true, Status: 'Failed', Error: 'ffmpeg exited with code 1.' }),
        clip('c10', { Name: '<b>tag</b>', ItemId: GONE, ItemName: 'Gone (2001)', ItemAvailable: false }, {})
    ];
}

function summaryOf(clips) {
    const count = (state) => clips.filter((c) => c.Clip.State === state).length;
    return {
        Count: clips.length,
        TotalBytes: clips.filter((c) => c.Clip.FileName && c.Clip.State !== 'Missing').reduce((n, c) => n + (c.Clip.FileSize || 0), 0),
        InProgress: count('InProgress'),
        Failed: count('Failed'),
        Missing: count('Missing'),
        FolderUnavailable: count('FolderUnavailable')
    };
}

// A page shaped like jellyfin-web around the script: the head fragment (both pages known and on unless `head` says
// otherwise), the LibraryMenu, the capabilities, the shells and the clip endpoints over page.data (filtered by the
// state, the video and the deleted bookmarks, as the server does; a test may change the data between requests).
async function start(options) {
    options = options || {};
    const page = createPage();
    page.context.location.hash = options.hash || '#/home';
    if (options.layout) {
        page.document.documentElement.classList.add('layout-' + options.layout);
    }

    const island = page.element('script', { type: 'application/json', 'data-jfbm-head': '', 'data-jfbm-pages': '' });
    island.textContent = JSON.stringify(Object.assign({ pages: ['bookmarks', 'clips', 'demo-a', 'demo-b'], off: ['demo-a', 'demo-b'], mode: 'FallbackOnly', nav: true }, options.head || {}));
    page.document.head.append(island);
    const titles = [];
    page.context.LibraryMenu = { setTitle: (t) => titles.push(t) };
    page.titles = titles;
    page.respond('GET', '/Bookmarks/Capabilities', Object.assign({}, CAPABILITIES, options.capabilities || {}));
    page.respond('GET', '/Bookmarks/Pages/clips', options.off ? SHELL_OFF : SHELL('clips'), 200, { contentType: 'text/html; charset=utf-8' });
    page.respond('GET', '/Bookmarks/Pages/bookmarks', SHELL('bookmarks'), 200, { contentType: 'text/html; charset=utf-8' });
    page.respond('GET', '/Bookmarks', []);
    page.data = { clips: options.clips || fixtures() };
    page.respond('GET', '/Bookmarks/Clips', (entry) => {
        const q = entry.query;
        const states = q.state ? q.state.split(',') : null;
        let items = page.data.clips.filter((c) => (!states || states.indexOf(c.Clip.State) >= 0)
            && (!q.itemId || key(c.ItemId) === key(q.itemId))
            && (q.includeDeleted !== 'false' || !c.BookmarkDeleted));
        const start = Number(q.startIndex || 0);
        const limit = Number(q.limit || 50);
        return { Items: items.slice(start, start + limit), TotalRecordCount: items.length, StartIndex: start };
    });
    page.respond('GET', '/Bookmarks/Clips/Summary', () => summaryOf(page.data.clips));
    page.respond('GET', '/Bookmarks/Clips/Status', (entry) => {
        const ids = String(entry.query.ids || '').split(',');
        return ids.map((id) => page.data.clips.find((c) => key(c.Id) === id)).filter(Boolean)
            .map((c) => ({ Id: key(c.Id), Status: c.Clip.Status, State: c.Clip.State, Progress: c.Clip.Progress, Error: c.Clip.Error, FileName: c.Clip.FileName, FileSize: c.Clip.FileSize }));
    });
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
    const page = await start(Object.assign({ hash: '#/jfbm-clips' }, options || {}));
    showFallbackPage(page);
    await page.advance(120);
    return page;
}

// Back or Forward to a hash: the kit renders the page shown there again.
async function navigate(page, hash) {
    page.context.location.hash = hash;
    page.context.dispatchEvent(new page.Event('popstate'));
    await page.advance(120);
}

function root(page) {
    return page.document.querySelector('#fallbackPage > [data-jfbm-page="clips"]');
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
        search: r.querySelector('.jfbm-clips-toolbar input[type="search"]'),
        state: r.querySelector('.jfbm-clips-state-select'),
        mode: r.querySelector('.jfbm-clips-mode-select'),
        type: r.querySelector('.jfbm-clips-type-select'),
        sort: r.querySelector('.jfbm-clips-sort-select'),
        deleted: r.querySelector('.jfbm-clips-deleted'),
        summary: r.querySelector('.jfbm-clips-summary'),
        content: r.querySelector('.jfbm-clips-content'),
        pager: r.querySelector('.jfbm-pager')
    };
}

function rows(page) {
    return toolbar(page).content.querySelectorAll('.jfbm-clips-row');
}

function row(page, id) {
    return toolbar(page).content.querySelector('.jfbm-clips-row[data-jfbm-clip="' + key(id) + '"]');
}

function stateText(r) {
    return r.querySelector('.jfbm-clip-state-text').textContent;
}

// Requests that match held back: answered as the server had it when they were asked, delivered on release().
function hold(page, match) {
    const original = page.context.fetch;
    const waiting = [];
    page.context.fetch = (url, init) => {
        const parsed = new URL(String(url), 'http://127.0.0.1:8097');
        const answer = original(url, init);
        if (!match(parsed.pathname, Object.fromEntries(parsed.searchParams.entries()))) {
            return answer;
        }

        return new Promise((resolve, reject) => waiting.push(() => answer.then(resolve, reject)));
    };
    return {
        count: () => waiting.length,
        release: () => {
            page.context.fetch = original;
            waiting.splice(0).forEach((go) => go());
        }
    };
}

// The page in Plugin Pages' host (Auto): the host view jellyfin-web shows, with the shell it fetched.
async function openedInPluginPages(options, query) {
    const page = await start(Object.assign({ head: { mode: 'Auto' }, hash: '#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fclips' + (query || '') }, options || {}));
    const view = page.element('div', { id: 'userPluginPreferencesPage', class: 'page libraryPage userPreferencesPage', 'data-role': 'page' });
    const shell = page.element('div', { class: 'jfbm-page', 'data-jfbm-page': 'clips' }, [page.element('p', { class: 'jfbm-shell-note' }, ['This page needs the Bookmarks web UI, which is turned off or failed to load.'])]);
    view.append(shell);
    page.document.body.append(view);
    ['viewbeforeshow', 'pagebeforeshow', 'viewshow', 'pageshow'].forEach((type) => page.dispatch(view, type, { detail: { isRestored: false } }));
    await page.advance(120);
    page.hostView = view;
    page.shell = shell;
    return page;
}

function choose(page, select, value) {
    select.value = value;
    page.dispatch(select, 'change');
}

function renders(page) {
    return page.context.__jfkit.jfbm.instance.pages.state().renders.clips || 0;
}

test('the page is registered as the server declares it and linked after My Bookmarks in "Library tools"', { skip }, async () => {
    const page = await start();
    const kit = page.context.__jfkit.jfbm.instance;
    assert.deepEqual(plain(kit.pages.state().keys), ['demo-a', 'demo-b', 'bookmarks', 'clips']);
    assert.equal(kit.pages.href('clips', { highlight: 'c1' }), '#/jfbm-clips?highlight=c1');
    const container = drawer(page);
    await page.advance(400);
    const links = container.querySelectorAll('.jfkit-nav a');
    assert.deepEqual(links.map((a) => [a.getAttribute('data-jfbm-nav'), a.getAttribute('href'), a.textContent, a.getAttribute('data-order')]),
        [['bookmarks', '#/jfbm-bookmarks', 'My Bookmarks', '10'], ['clips', '#/jfbm-clips', 'My Clips', '20']]);
    assert.ok(links[1].querySelector('.video_library'), 'the page\'s icon');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the page by default: the header link, the toolbar with its labels, the first page of clips and the summary line', { skip }, async () => {
    const page = await opened();
    const r = root(page);
    assert.equal(r.getAttribute('data-jfbm-state'), 'on');
    assert.ok(page.titles.includes('My Clips'));
    const link = r.querySelector('.jfbm-clips-header a.jfbm-clips-bookmarks-link');
    assert.equal(link.textContent, 'My Bookmarks');
    assert.equal(link.getAttribute('href'), '#/jfbm-bookmarks');
    const t = toolbar(page);
    assert.equal(t.search.getAttribute('placeholder'), 'Search clips…');
    assert.equal(t.search.getAttribute('aria-label'), 'Search clips');
    assert.equal(t.search.getAttribute('maxlength'), '100');
    assert.deepEqual([t.state, t.mode, t.type, t.sort].map((s) => s.getAttribute('aria-label')), ['State', 'Mode', 'Type', 'Sort'], 'every control labelled');
    assert.deepEqual(t.state.querySelectorAll('option').map((o) => o.textContent), ['All states', 'Ready', 'In progress', 'Failed', 'File missing', 'Clip folder offline or not recognised', 'Rename not finished']);
    assert.deepEqual(t.mode.querySelectorAll('option').map((o) => o.textContent), ['Any mode', 'Accurate (MP4)', 'Fast (MKV)']);
    assert.deepEqual(t.type.querySelectorAll('option').map((o) => o.textContent), ['All types', 'Movies', 'Episodes', 'Other']);
    assert.deepEqual(t.sort.querySelectorAll('option').map((o) => o.textContent), ['Newest', 'Oldest', 'Name A–Z', 'Title A–Z', 'Largest', 'Longest']);
    assert.deepEqual([t.state.value, t.mode.value, t.type.value, t.sort.value], ['', '', '', 'Newest']);
    assert.equal(t.deleted.checked, true, 'the deleted bookmarks\' clips are listed by default');
    assert.equal(t.deleted.parentNode.textContent, 'Include clips of deleted bookmarks');

    assert.deepEqual(requests(page, '/Bookmarks/Clips').map((x) => x.query), [{ sortBy: 'Newest', startIndex: '0', limit: '50' }]);
    assert.equal(requests(page, '/Bookmarks/Clips/Summary').length, 1);
    assert.equal(t.summary.textContent, '10 clips · 50 MB · 2 in progress · 2 failed · 1 file missing · 1 in an offline or unrecognised clip folder', 'the sizes of every file not known to be gone');
    assert.equal(t.content.getAttribute('role'), 'list');
    assert.equal(t.content.hasAttribute('aria-busy'), false);
    assert.deepEqual(rows(page).map((x) => x.getAttribute('data-jfbm-clip')), ['c2', 'c3', 'c1', 'c4', 'c5', 'c6', 'c7', 'c8', 'c9', 'c10'], 'in the server\'s order, the clips being made first');
    assert.ok(rows(page).every((x) => x.getAttribute('role') === 'listitem' && x.parentNode === t.content), 'the list\'s items');
    assert.equal(t.pager.classList.contains('hide'), true);
    assert.equal(page.fetchLog.filter((f) => f.path.startsWith('/Items')).length, 0, 'never a core items endpoint (the kit\'s privacy rule)');
    assert.ok(page.fetchLog.every((f) => f.userId === 'user-1'));
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('each state on its row: Ready, being made with its progress, Queued, Failed with its reason, Cancelled, File missing, Clip folder offline', { skip }, async () => {
    const page = await opened();
    assert.deepEqual(rows(page).map((x) => [x.getAttribute('data-jfbm-clip'), x.getAttribute('data-jfbm-state'), stateText(x)]), [
        ['c2', 'InProgress', 'Making the clip 43% · Fast'],
        ['c3', 'InProgress', 'Queued · Accurate'],
        ['c1', 'Ready', 'Ready · Accurate · 45 MB'],
        ['c4', 'Failed', 'Failed · Fast'],
        ['c5', 'Failed', 'Cancelled · Fast'],
        ['c6', 'Missing', 'File missing · Fast · 1.2 MB'],
        ['c7', 'FolderUnavailable', 'Clip folder offline · Fast · 1.2 MB'],
        ['c8', 'Ready', 'Ready · Fast · 1.2 MB'],
        ['c9', 'Ready', 'Ready · Fast · 1.2 MB'],
        ['c10', 'Ready', 'Ready · Fast · 1.2 MB']
    ]);
    const bar = row(page, 'c2').querySelector('[role="progressbar"]');
    assert.equal(bar.getAttribute('aria-valuenow'), '43');
    assert.equal(bar.getAttribute('aria-label'), 'Making the clip');
    assert.equal(row(page, 'c3').querySelector('[role="progressbar"]'), null);
    assert.equal(row(page, 'c4').querySelector('.jfbm-clip-error').textContent, 'The clip drive is full.');
    assert.equal(row(page, 'c5').querySelector('.jfbm-clip-error'), null, 'a cancelled clip has no reason to show');

    // The titles: the name, else the note's first line, else the file name without its extension, else the range.
    const title = (id) => row(page, id).querySelector('bdi').textContent;
    assert.equal(title('c1'), 'Bank job');
    assert.equal(title('c3'), 'Pilot cold open');
    assert.equal(title('c4'), 'Clip 1:00–1:04');
    const noName = await opened({ clips: [clip('n1')] });
    assert.equal(row(noName, 'n1').querySelector('bdi').textContent, 'Heat (1995) - n1 [00.01.00-00.01.04]');

    // The facts: the video, the clip's range and length, when it was made (the job's end for a file that isn't there to
    // look at), else when it was asked for.
    const facts = (id) => row(page, id).querySelector('.jfbm-clip-facts').textContent;
    const date = (value) => new page.context.Date(value).toLocaleDateString();
    assert.equal(facts('c1'), 'Heat (1995) · 1:02:03–1:05:00 (2:57) · made ' + date('2026-10-10T10:00:05.0000000Z'));
    assert.equal(facts('c6'), 'Heat (1995) · 1:00–1:04 (0:04) · made ' + date('2026-10-10T10:00:05.0000000Z'), 'Missing: dated by its job\'s end');
    assert.match(facts('c2'), /^Heat \(1995\) · 1:00–1:04 \(0:04\) · asked for /);
    assert.match(facts('c4'), / · asked for /, 'a failed clip was never made');
    assert.equal(facts('c3').indexOf('Show S01E02 - Pilot · '), 0);
    assert.equal(page.errors.length + noName.errors.length, 0, page.errors.join('\n'));
});

test('the badges, the poster and the video link only for a video the viewer can open, the video as the row\'s item, and text as text', { skip }, async () => {
    const page = await opened();
    const badges = (id) => row(page, id).querySelectorAll('.jfbm-badge').map((b) => b.textContent);
    assert.deepEqual(badges('c1'), ['In library "Clips"']);
    assert.deepEqual(badges('c8'), ['Bookmark deleted']);
    assert.deepEqual(badges('c9'), ['Range changed', 'Name changed', 'Last remake failed']);
    assert.equal(row(page, 'c9').querySelectorAll('.jfbm-badge')[2].getAttribute('title'), 'The last remake failed: ffmpeg exited with code 1.');
    assert.equal(row(page, 'c9').querySelectorAll('.jfbm-badge')[1].getAttribute('title'),
        'The clip file\'s name isn\'t the one Bookmarks gives this clip now (the bookmark was renamed, or the name holds words of its note from an earlier version). Rename file gives it its name.',
        'what the badge means since the file name never holds the note, and the owner\'s way to fix it');
    assert.deepEqual(badges('c2'), []);
    assert.equal(row(page, 'c2').querySelector('.jfbm-clip-badges'), null);
    const inLibrary = await opened({ clips: [clip('l1', {}, { InLibrary: true, LibraryName: null })] });
    assert.deepEqual(row(inLibrary, 'l1').querySelectorAll('.jfbm-badge').map((b) => b.textContent), ['In a library'], 'a library the viewer may not open isn\'t named');

    const heat = row(page, 'c1');
    assert.equal(heat.getAttribute('data-id'), key(HEAT), 'the kit\'s row with the video\'s id, so Jellyfin Enhanced\'s Hidden Content hides it');
    assert.equal(heat.getAttribute('data-type'), 'Movie');
    assert.equal(heat.getAttribute('data-jfbm-item'), key(HEAT));
    assert.equal(row(page, 'c3').getAttribute('data-type'), 'Episode');
    const thumb = heat.querySelector('.jfbm-clip-thumb');
    assert.ok(heat.firstChild.classList.contains('jfbm-row-select') && thumb === heat.firstChild.nextSibling, 'the bulk delete\'s checkbox, then a 16:9 box of ours');
    assert.equal(heat.querySelector('.listItemImage'), null, 'not the kit\'s image box');
    assert.equal(thumb.querySelector('img').getAttribute('src'), 'http://127.0.0.1:8097/Items/' + key(HEAT) + '/Images/Primary?fillHeight=90&quality=90');
    assert.equal(thumb.getAttribute('href'), '#/details?id=' + key(HEAT) + '&serverId=server-1');
    assert.equal(thumb.getAttribute('tabindex'), '-1');
    const video = heat.querySelector('.jfbm-clip-facts a.jfbm-clip-item-link');
    assert.equal(video.textContent, 'Heat (1995)');
    assert.equal(video.getAttribute('href'), '#/details?id=' + key(HEAT) + '&serverId=server-1');
    assert.equal(heat.querySelector('bdi').parentNode.querySelector('a'), null, 'the title isn\'t a link');

    const gone = row(page, 'c10');
    assert.equal(gone.querySelector('img'), null, 'no poster request for a video the viewer can\'t open (core serves images unchecked)');
    assert.equal(gone.querySelectorAll('a').length, 0, 'and no link to it');
    assert.ok(gone.querySelector('.jfbm-clip-thumb .material-icons'), 'a placeholder');
    assert.equal(gone.querySelector('.jfbm-clip-facts').textContent.indexOf('Gone (2001) · '), 0);
    assert.equal(gone.querySelector('bdi').textContent, '<b>tag</b>', 'a name is text');
    assert.equal(gone.querySelectorAll('b').length, 0);

    // A poster that fails leaves the placeholder.
    const image = heat.querySelector('img');
    page.dispatch(image, 'error');
    assert.equal(heat.querySelector('img'), null);
    assert.ok(heat.querySelector('.jfbm-clip-thumb .material-icons'));
    assert.equal(page.errors.length + inLibrary.errors.length, 0, page.errors.join('\n'));
});

test('the toolbar writes the state into the hash (a change one history entry, a search typed in place) and the query follows; Clear filters', { skip }, async () => {
    const page = await opened();
    const length = page.context.history.length;
    let t = toolbar(page);

    choose(page, t.state, 'Missing');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-clips?state=Missing');
    assert.deepEqual(lastRequest(page, '/Bookmarks/Clips').query, { sortBy: 'Newest', startIndex: '0', limit: '50', state: 'Missing' });
    assert.deepEqual(rows(page).map((x) => x.getAttribute('data-jfbm-clip')), ['c6']);
    assert.equal(toolbar(page).summary.querySelector('.jfbm-clips-totals').textContent, '10 clips · 50 MB · 2 in progress · 2 failed · 1 file missing · 1 in an offline or unrecognised clip folder', 'the user\'s clips in numbers, whatever the filters');
    assert.equal(toolbar(page).summary.querySelector('.jfbm-clips-matches').textContent, '1 clip matches these filters');
    choose(page, t.mode, 'Fast');
    await page.advance(50);
    choose(page, t.type, 'Episode');
    await page.advance(50);
    choose(page, t.sort, 'Size');
    await page.advance(50);
    t.deleted.checked = false;
    page.dispatch(t.deleted, 'change');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-clips?state=Missing&mode=Fast&type=Episode&sort=Size&deleted=0');
    assert.deepEqual(lastRequest(page, '/Bookmarks/Clips').query, { sortBy: 'Size', startIndex: '0', limit: '50', state: 'Missing', mode: 'Fast', itemTypes: 'Episode', includeDeleted: 'false' });
    assert.equal(page.context.history.length, length + 5, 'one entry per toolbar change');
    assert.equal(renders(page), 1, 'the page changed itself; no render by the kit');

    const search = toolbar(page).search;
    search.focus();
    search.value = 'ba';
    page.dispatch(search, 'input');
    await page.advance(100);
    search.value = 'bank';
    page.dispatch(search, 'input');
    await page.advance(100);
    assert.equal(page.context.location.hash, '#/jfbm-clips?state=Missing&mode=Fast&type=Episode&sort=Size&deleted=0', 'nothing before 300 ms of quiet');
    await page.advance(250);
    assert.equal(page.context.location.hash, '#/jfbm-clips?q=bank&state=Missing&mode=Fast&type=Episode&sort=Size&deleted=0');
    assert.equal(page.context.history.length, length + 5, 'a search replaces the entry');
    assert.equal(lastRequest(page, '/Bookmarks/Clips').query.searchTerm, 'bank');
    assert.equal(page.document.activeElement, search, 'the field keeps the focus');

    // Nothing matches: the notice with Clear filters, which clears every filter and the search.
    page.data.clips = [];
    choose(page, toolbar(page).state, 'Ready');
    await page.advance(50);
    t = toolbar(page);
    const notice = t.content.querySelector('.jfbm-notice');
    assert.equal(notice.querySelector('.jfbm-notice-text').textContent, 'No clips match these filters.');
    assert.equal(t.content.hasAttribute('role'), false, 'a notice alone is no list');
    page.data.clips = fixtures();
    notice.querySelector('button').click();
    await page.advance(50);
    t = toolbar(page);
    assert.equal(page.context.location.hash, '#/jfbm-clips?sort=Size', 'the sort stays, as on My Bookmarks');
    assert.deepEqual([t.search.value, t.state.value, t.mode.value, t.type.value, t.sort.value, t.deleted.checked], ['', '', '', '', 'Size', true]);
    assert.equal(rows(page).length, 10);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a deep link: every parameter read into the toolbar and the query (two pages in one request); Back and Forward render from the hash', { skip }, async () => {
    const page = await opened({ hash: '#/jfbm-clips?q=heat&state=Ready&mode=Accurate&type=Movie&sort=Name&deleted=0&page=2' });
    assert.deepEqual(lastRequest(page, '/Bookmarks/Clips').query, { sortBy: 'Name', startIndex: '0', limit: '100', searchTerm: 'heat', state: 'Ready', mode: 'Accurate', itemTypes: 'Movie', includeDeleted: 'false' });
    const t = toolbar(page);
    assert.deepEqual([t.search.value, t.state.value, t.mode.value, t.type.value, t.sort.value, t.deleted.checked], ['heat', 'Ready', 'Accurate', 'Movie', 'Name', false]);
    assert.equal(page.context.location.hash, '#/jfbm-clips?q=heat&state=Ready&mode=Accurate&type=Movie&sort=Name&deleted=0&page=2', 'the hash as it came');

    choose(page, t.state, '');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/jfbm-clips?q=heat&mode=Accurate&type=Movie&sort=Name&deleted=0');
    assert.equal(renders(page), 1);
    await navigate(page, '#/jfbm-clips?q=heat&state=Ready&mode=Accurate&type=Movie&sort=Name&deleted=0&page=2');
    assert.equal(renders(page), 2, 'Back renders the page from the hash');
    assert.equal(toolbar(page).state.value, 'Ready');
    assert.equal(lastRequest(page, '/Bookmarks/Clips').query.state, 'Ready');
    await navigate(page, '#/jfbm-clips?q=heat&mode=Accurate&type=Movie&sort=Name&deleted=0');
    assert.equal(renders(page), 3, 'and Forward');
    assert.equal(toolbar(page).state.value, '');
    assert.equal(lastRequest(page, '/Bookmarks/Clips').query.state, undefined);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a deep link that says things wrongly is the default, and the hash is left as it came', { skip }, async () => {
    const hash = '#/jfbm-clips?state=Gone&mode=Slow&type=Show&sort=Biggest&deleted=perhaps&page=x&highlight=nope';
    const page = await opened({ hash });
    assert.deepEqual(lastRequest(page, '/Bookmarks/Clips').query, { sortBy: 'Newest', startIndex: '0', limit: '50' });
    const t = toolbar(page);
    assert.deepEqual([t.state.value, t.mode.value, t.type.value, t.sort.value, t.deleted.checked], ['', '', '', 'Newest', true]);
    assert.equal(page.context.location.hash, hash);
    assert.equal(page.scrolled.length, 0, 'an id that isn\'t one looks nothing up');
    const cased = await opened({ hash: '#/jfbm-clips?state=missing&mode=FAST&sort=duration' });
    assert.deepEqual(lastRequest(cased, '/Bookmarks/Clips').query, { sortBy: 'Duration', startIndex: '0', limit: '50', state: 'Missing', mode: 'Fast' }, 'any case');
    assert.equal(page.errors.length + cased.errors.length, 0, page.errors.join('\n'));
});

test('a highlight lands once: the clip marked and scrolled to, then dropped from the hash in place', { skip }, async () => {
    // A clip's id is its bookmark's GUID; the toast's link writes it in the N form, a hand-typed link may have dashes.
    const kept = 'dddd0000-0000-0000-0000-00000000000A';
    const clips = fixtures().concat([clip(kept, { Name: 'By its GUID', BookmarkDeleted: true })]);
    const page = await start({ clips, hash: '#/jfbm-clips?state=Ready&highlight=' + kept });
    const length = page.context.history.length;
    showFallbackPage(page);
    await page.advance(120);
    assert.equal(page.context.location.hash, '#/jfbm-clips?state=Ready', 'dropped in place');
    assert.equal(page.context.history.length, length, 'no entry of its own');
    const target = row(page, kept);
    assert.ok(target.classList.contains('jfbm-page-highlight'));
    assert.equal(page.scrolled.length, 1);
    assert.ok(page.scrolled[0].element === target, 'the marked clip scrolled into view');
    assert.deepEqual(plain(page.scrolled[0].options), { block: 'center' });
    assert.ok(page.document.activeElement === target, 'the focus lands on the row');
    await page.advance(2600);
    assert.equal(target.classList.contains('jfbm-page-highlight'), false, 'the mark goes after a moment');

    // The N form; a clip the list doesn't hold (deleted, filtered out) marks nothing, says so, and is dropped too.
    const nForm = await opened({ clips, hash: '#/jfbm-clips?highlight=' + key(kept) });
    assert.ok(row(nForm, kept).classList.contains('jfbm-page-highlight'));
    assert.equal(nForm.context.location.hash, '#/jfbm-clips');

    // A clip being made, landed on: the poll draws its row again when it ends, and the fresh row keeps the focus.
    const making = 'dddd0000-0000-0000-0000-00000000000B';
    const busy = await opened({ clips: [clip(making, { Name: 'Being made' }, running(20))].concat(fixtures()), hash: '#/jfbm-clips?highlight=' + making });
    const landed = row(busy, making);
    assert.ok(busy.document.activeElement === landed);
    busy.data.clips[0] = clip(making, { Name: 'Being made' }, {});
    await busy.advance(2500);
    const fresh = row(busy, making);
    assert.ok(fresh !== landed && fresh.getAttribute('data-jfbm-state') === 'Ready', 'drawn again');
    assert.ok(busy.document.activeElement === fresh, 'the focus kept on the clip\'s row');
    assert.equal(fresh.getAttribute('tabindex'), '-1');
    assert.equal(busy.errors.length, 0, busy.errors.join('\n'));
    const elsewhere = await opened({ hash: '#/jfbm-clips?highlight=eeee0000000000000000000000000001' });
    assert.equal(elsewhere.scrolled.length, 0);
    assert.equal(elsewhere.context.location.hash, '#/jfbm-clips');
    assert.equal(requests(elsewhere, '/Bookmarks/Clips').length, 1, 'the whole list is shown already: nothing more to ask');
    const missing = root(elsewhere).querySelector('.jfbm-clips-missing');
    assert.equal(missing.querySelector('.jfbm-notice-text').textContent, 'That clip isn\'t in this list: it was deleted, or the filters leave it out.');
    assert.equal(missing.getAttribute('role'), 'status');
    assert.ok(missing.nextSibling === toolbar(elsewhere).content, 'above the list, not in it');
    assert.equal(rows(elsewhere).length, 10);
    choose(elsewhere, toolbar(elsewhere).state, 'Ready');
    await elsewhere.advance(50);
    assert.equal(root(elsewhere).querySelector('.jfbm-clips-missing'), null, 'gone with the next list');
    assert.equal(page.errors.length + nForm.errors.length + elsewhere.errors.length, 0, page.errors.join('\n'));
});

test('a highlight on a later page of the list: the pages are read on until it shows (the hash counts them in place), and the focus lands on its row', { skip }, async () => {
    // Clip ids are GUIDs (a highlight takes nothing else).
    const id = (i) => 'abcd' + String(i).padStart(28, '0');
    const clips = [];
    for (let i = 0; i < 160; i++) {
        clips.push(clip(id(i), { Name: 'Clip ' + i }));
    }

    const page = await start({ clips, hash: '#/jfbm-clips?highlight=' + id(105) });
    const length = page.context.history.length;
    showFallbackPage(page);
    await page.advance(120);
    assert.deepEqual(requests(page, '/Bookmarks/Clips').map((x) => [x.query.startIndex, x.query.limit]), [['0', '50'], ['50', '50'], ['100', '50']], 'a page at a time until it shows');
    assert.equal(rows(page).length, 150);
    const target = row(page, id(105));
    assert.ok(target.classList.contains('jfbm-page-highlight'));
    assert.ok(page.scrolled.length === 1 && page.scrolled[0].element === target);
    assert.ok(page.document.activeElement === target, 'the focus on the row, for a keyboard, a remote or a screen reader');
    assert.equal(target.getAttribute('tabindex'), '-1', 'focusable by script, out of the Tab order');
    assert.equal(page.context.location.hash, '#/jfbm-clips?page=3', 'the pages shown counted, the highlight dropped, in place');
    assert.equal(page.context.history.length, length);
    assert.equal(root(page).querySelector('.jfbm-clips-missing'), null);
    assert.equal(toolbar(page).pager.classList.contains('hide'), false, 'Load more for the rest');
    toolbar(page).pager.querySelector('button').click();
    await page.advance(50);
    assert.deepEqual([lastRequest(page, '/Bookmarks/Clips').query.startIndex, rows(page).length], ['150', 160]);

    // Not in the list at all: read on to its end (at most PAGE_MAX_PAGES pages), then said.
    const absent = await start({ clips, hash: '#/jfbm-clips?state=Ready&highlight=eeee0000000000000000000000000001' });
    showFallbackPage(absent);
    await absent.advance(120);
    assert.deepEqual(requests(absent, '/Bookmarks/Clips').map((x) => x.query.startIndex), ['0', '50', '100', '150']);
    assert.equal(rows(absent).length, 160);
    assert.equal(root(absent).querySelector('.jfbm-clips-missing .jfbm-notice-text').textContent, 'That clip isn\'t in this list: it was deleted, or the filters leave it out.');
    assert.equal(absent.context.location.hash, '#/jfbm-clips?state=Ready&page=4');
    assert.equal(absent.scrolled.length, 0);
    const many = [];
    for (let i = 0; i < 1100; i++) {
        many.push(clip(id(i), { Name: 'Clip ' + i }));
    }

    const far = await start({ clips: many, hash: '#/jfbm-clips?highlight=' + id(1050) });
    showFallbackPage(far);
    await far.advance(400);
    assert.equal(requests(far, '/Bookmarks/Clips').length, 20, 'never more than PAGE_MAX_PAGES pages');
    assert.equal(rows(far).length, 1000);
    assert.ok(root(far).querySelector('.jfbm-clips-missing'));
    assert.equal(far.context.location.hash, '#/jfbm-clips?page=20');
    assert.equal(page.errors.length + absent.errors.length + far.errors.length, 0, page.errors.concat(absent.errors, far.errors).join('\n'));
});

test('paging: 50 a page, Load more appends the next and counts it in the hash in place; after clips ended it asks from earlier and skips what is shown', { skip }, async () => {
    const clips = [];
    for (let i = 0; i < 60; i++) {
        clips.push(clip('p' + i, { Name: 'Clip ' + i }));
    }

    const page = await opened({ clips });
    let t = toolbar(page);
    assert.equal(rows(page).length, 50);
    assert.equal(t.pager.classList.contains('hide'), false);
    const length = page.context.history.length;
    t.pager.querySelector('button').click();
    await page.advance(50);
    assert.deepEqual(lastRequest(page, '/Bookmarks/Clips').query, { sortBy: 'Newest', startIndex: '50', limit: '50' });
    assert.equal(rows(page).length, 60);
    assert.equal(page.context.location.hash, '#/jfbm-clips?page=2');
    assert.equal(page.context.history.length, length, 'the page count replaces the entry');
    assert.equal(t.pager.classList.contains('hide'), true);

    // Back to one page: the kit renders the shorter list; a reload of page=2 reads both pages in one request.
    await navigate(page, '#/jfbm-clips');
    assert.equal(rows(page).length, 50);
    await navigate(page, '#/jfbm-clips?page=2');
    assert.deepEqual(lastRequest(page, '/Bookmarks/Clips').query, { sortBy: 'Newest', startIndex: '0', limit: '100' });
    assert.equal(rows(page).length, 60);

    // The first clip being made, then done: it leaves the top of the server's order, so the next page is asked from one
    // earlier and the clip shown already isn't added twice.
    const many = [clip('m0', { Name: 'Being made' }, running(10))];
    for (let i = 1; i < 60; i++) {
        many.push(clip('m' + i, { Name: 'Clip ' + i }));
    }

    const moving = await opened({ clips: many });
    assert.equal(rows(moving).length, 50);
    moving.data.clips[0] = clip('m0', { Name: 'Being made' }, {});
    // It now sorts after the first page (the server's order without the pin), and the rest move up by one.
    moving.data.clips = moving.data.clips.slice(1, 55).concat([moving.data.clips[0]], moving.data.clips.slice(55));
    await moving.advance(2600);
    assert.equal(row(moving, 'm0').getAttribute('data-jfbm-state'), 'Ready', 'drawn in its place');
    t = toolbar(moving);
    t.pager.querySelector('button').click();
    await moving.advance(50);
    assert.equal(lastRequest(moving, '/Bookmarks/Clips').query.startIndex, '49');
    const keys = rows(moving).map((x) => x.getAttribute('data-jfbm-clip'));
    assert.equal(keys.length, 60, 'every clip once: the one that moved down isn\'t added twice, the one that moved up isn\'t skipped');
    assert.equal(new Set(keys).size, 60);
    assert.equal(page.errors.length + moving.errors.length, 0, page.errors.join('\n'));
});

test('Load more under way while the poll ends a clip: what the poll counted meanwhile is kept for the next page, so no clip is skipped', { skip }, async () => {
    const many = [clip('m0', { Name: 'Being made' }, running(10))];
    for (let i = 1; i < 120; i++) {
        many.push(clip('m' + i, { Name: 'Clip ' + i }));
    }

    const page = await opened({ clips: many });
    assert.equal(rows(page).length, 50);
    // The second page asked for and held, answered as the list was then (m50 to m99).
    const held = hold(page, (path, query) => path === '/Bookmarks/Clips' && query.startIndex === '50' && !query.itemId);
    toolbar(page).pager.querySelector('button').click();
    await page.advance(10);
    assert.equal(held.count(), 1);
    // Meanwhile m0's clip is made: it sorts after m110 now, the rest move up by one, and the poll reads it again.
    page.data.clips = many.slice(1, 111).concat([clip('m0', { Name: 'Being made' }, {})], many.slice(111));
    await page.advance(2600);
    assert.equal(row(page, 'm0').getAttribute('data-jfbm-state'), 'Ready', 'the poll drew it in its place');
    held.release();
    await page.advance(50);
    assert.equal(rows(page).length, 100);
    toolbar(page).pager.querySelector('button').click();
    await page.advance(50);
    assert.equal(lastRequest(page, '/Bookmarks/Clips').query.startIndex, '99', 'asked from one earlier: m100 moved up to 99');
    const keys = rows(page).map((x) => x.getAttribute('data-jfbm-clip'));
    assert.equal(keys.length, 120, 'every clip once');
    assert.equal(new Set(keys).size, 120);
    assert.ok(keys.includes('m100'));
    assert.equal(toolbar(page).pager.classList.contains('hide'), true);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the poll: one Status request for every clip being made every 2.5 s, the state patched in place; a clip that ends is read again with its video\'s id and drawn in place; then no more polls', { skip }, async () => {
    const page = await opened();
    const t = toolbar(page);
    const listReads = requests(page, '/Bookmarks/Clips').length;
    const summaries = requests(page, '/Bookmarks/Clips/Summary').length;
    const carRow = row(page, 'c2');
    const carState = carRow.querySelector('.jfbm-clip-state');
    assert.equal(requests(page, '/Bookmarks/Clips/Status').length, 0, 'nothing before the first 2.5 s');
    page.data.clips[0].Clip.Progress = 70;
    await page.advance(2500);
    assert.deepEqual(requests(page, '/Bookmarks/Clips/Status').map((x) => x.query), [{ ids: 'c2,c3' }], 'one request for both, in the list\'s order');
    assert.ok(row(page, 'c2') === carRow, 'the row is the same node');
    assert.ok(carRow.querySelector('.jfbm-clip-state') === carState, 'its state line too, its text patched');
    assert.equal(stateText(carRow), 'Making the clip 70% · Fast');
    assert.equal(carRow.querySelector('[role="progressbar"]').getAttribute('aria-valuenow'), '70');
    assert.equal(requests(page, '/Bookmarks/Clips').length, listReads, 'no list read for a tick');
    assert.equal(requests(page, '/Bookmarks/Clips/Summary').length, summaries, 'nor the summary');

    // The queued one starts, then both end: the one ending is read again with its video's id and the page's filters and
    // drawn in place; the summary follows.
    Object.assign(page.data.clips[1].Clip, { Status: 'Running', Progress: 5 });
    await page.advance(2500);
    assert.equal(stateText(row(page, 'c3')), 'Making the clip 5% · Accurate');
    page.data.clips[0] = clip('c2', { Name: 'Car chase' }, { FileSize: 3 * 1048576 });
    page.data.clips[1] = clip('c3', { Note: 'Pilot cold open', ItemId: SHOW, ItemName: 'Show S01E02 - Pilot', ItemType: 'Episode' }, { Mode: 'Accurate', Container: 'mp4', FileSize: 2 * 1048576 });
    await page.advance(2500);
    const reads = requests(page, '/Bookmarks/Clips').slice(listReads);
    assert.deepEqual(reads.map((x) => x.query), [
        { sortBy: 'Newest', startIndex: '0', limit: '200', itemId: key(HEAT) },
        { sortBy: 'Newest', startIndex: '0', limit: '200', itemId: key(SHOW) }
    ], 'each video\'s clips once, with the page\'s filters');
    assert.equal(requests(page, '/Bookmarks/Clips/Summary').length, summaries + 1);
    assert.deepEqual(rows(page).slice(0, 2).map((x) => [x.getAttribute('data-jfbm-clip'), x.getAttribute('data-jfbm-state'), stateText(x)]), [
        ['c2', 'Ready', 'Ready · Fast · 3.0 MB'],
        ['c3', 'Ready', 'Ready · Accurate · 2.0 MB']
    ], 'in their places');
    assert.ok(row(page, 'c2') !== carRow, 'drawn again from the clip as read');
    assert.equal(t.summary.textContent, '10 clips · 55 MB · 2 failed · 1 file missing · 1 in an offline or unrecognised clip folder', 'no clip in progress any more');
    const polls = requests(page, '/Bookmarks/Clips/Status').length;
    await page.advance(10000);
    assert.equal(requests(page, '/Bookmarks/Clips/Status').length, polls, 'nothing is polled once no clip is being made');
    assert.equal(renders(page), 1);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the poll pauses while the tab is hidden and asks at once when it shows again; it stops when the page is left', { skip }, async () => {
    const page = await opened();
    page.document.visibilityState = 'hidden';
    page.dispatch(page.document, 'visibilitychange');
    await page.advance(10000);
    assert.equal(requests(page, '/Bookmarks/Clips/Status').length, 0, 'nothing while hidden');
    page.document.visibilityState = 'visible';
    page.dispatch(page.document, 'visibilitychange');
    await page.advance(10);
    assert.equal(requests(page, '/Bookmarks/Clips/Status').length, 1, 'at once when shown again');
    await page.advance(2500);
    assert.equal(requests(page, '/Bookmarks/Clips/Status').length, 2, 'then every 2.5 s');

    // Leaving for another page: no more polls.
    page.respond('GET', '/Bookmarks/Groups', { Items: [], TotalRecordCount: 0, StartIndex: 0 });
    page.respond('GET', '/Bookmarks/Query', { Items: [], TotalRecordCount: 0, StartIndex: 0 });
    await navigate(page, '#/jfbm-bookmarks');
    assert.ok(page.document.querySelector('#fallbackPage > [data-jfbm-page="bookmarks"]'), 'My Bookmarks shown');
    const polls = requests(page, '/Bookmarks/Clips/Status').length;
    await page.advance(10000);
    assert.equal(requests(page, '/Bookmarks/Clips/Status').length, polls);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('in Plugin Pages\' host the page left stays in its hidden view: nothing is polled or read there; shown again, it renders and polls again', { skip }, async () => {
    const page = await openedInPluginPages();
    const view = page.hostView;
    assert.equal(page.shell.querySelectorAll('.jfbm-clips-row').length, 10);
    await page.advance(2500);
    assert.equal(requests(page, '/Bookmarks/Clips/Status').length, 1, 'polled while shown');

    // Another page: jellyfin-web keeps the host view, hidden, and shows the next one.
    const before = page.fetchLog.length;
    view.classList.add('hide');
    page.context.location.hash = '#/home';
    const home = page.element('div', { id: 'indexPage', class: 'page homePage libraryPage', 'data-role': 'page' });
    page.document.body.append(home);
    ['viewbeforeshow', 'pagebeforeshow', 'viewshow', 'pageshow'].forEach((type) => page.dispatch(home, type, { detail: { isRestored: false } }));
    page.data.clips[0].Clip.Progress = 80;
    await page.advance(10000);
    // The browser tab hidden and shown again meanwhile asks nothing either.
    page.document.visibilityState = 'hidden';
    page.dispatch(page.document, 'visibilitychange');
    page.document.visibilityState = 'visible';
    page.dispatch(page.document, 'visibilitychange');
    await page.advance(5000);
    assert.deepEqual(page.fetchLog.slice(before).map((f) => f.path).filter((p) => p.indexOf('/Bookmarks/Clips') === 0), [], 'no Status, list or Summary request off the page');

    // Back: the host view shown again (restored), which the kit renders again: the list read afresh and polled.
    home.classList.add('hide');
    view.classList.remove('hide');
    page.context.location.hash = '#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fclips';
    ['viewbeforeshow', 'pagebeforeshow', 'viewshow', 'pageshow'].forEach((type) => page.dispatch(view, type, { detail: { isRestored: true } }));
    await page.advance(120);
    assert.equal(renders(page), 2);
    assert.equal(stateText(page.shell.querySelector('.jfbm-clips-row[data-jfbm-clip="c2"]')), 'Making the clip 80% · Fast', 'the rows as they are now');
    const polls = requests(page, '/Bookmarks/Clips/Status').length;
    await page.advance(2500);
    assert.equal(requests(page, '/Bookmarks/Clips/Status').length, polls + 1, 'polled again');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the poll: a clip the answer leaves out is no clip of the user\'s any more and goes; a clip that ends out of a state filter goes too', { skip }, async () => {
    const page = await opened();
    page.data.clips.splice(1, 1);
    await page.advance(2500);
    assert.equal(row(page, 'c3'), null, 'gone from the list');
    assert.equal(rows(page).length, 9);
    assert.equal(toolbar(page).summary.textContent.indexOf('9 clips · '), 0, 'the summary follows');

    const filtered = await opened({ hash: '#/jfbm-clips?state=InProgress' });
    assert.deepEqual(rows(filtered).map((x) => x.getAttribute('data-jfbm-clip')), ['c2', 'c3']);
    filtered.data.clips[0] = clip('c2', { Name: 'Car chase' }, {});
    await filtered.advance(2500);
    assert.deepEqual(rows(filtered).map((x) => x.getAttribute('data-jfbm-clip')), ['c3'], 'no longer in progress, so no longer listed');
    assert.equal(toolbar(filtered).summary.querySelector('.jfbm-clips-matches').textContent, '1 clip matches these filters');
    filtered.data.clips[1] = clip('c3', {}, {});
    await filtered.advance(2500);
    assert.equal(rows(filtered).length, 0);
    assert.equal(toolbar(filtered).content.querySelector('.jfbm-notice-text').textContent, 'No clips match these filters.');
    assert.equal(page.errors.length + filtered.errors.length, 0, page.errors.join('\n'));
});

test('the empty states: how a clip is made, or why this user can\'t; clips turned off; the read-only notice; an error with Retry; "Loading…" after 300 ms', { skip }, async () => {
    const empty = await opened({ clips: [] });
    assert.equal(toolbar(empty).content.querySelector('.jfbm-notice-text').textContent, 'No clips yet. Mark a range with the scissors button in the player, then choose Save & make clip.');
    assert.equal(toolbar(empty).summary.textContent, '0 clips · 0 B');
    const noDownloads = await opened({ clips: [], capabilities: { CanCreateClips: false } });
    assert.equal(toolbar(noDownloads).content.querySelector('.jfbm-notice-text').textContent, 'No clips yet. Ask your server admin for download permission to make clips.');
    const off = await opened({ clips: [], capabilities: { ClipsEnabled: false, CanCreateClips: false } });
    assert.equal(root(off).querySelector('.jfbm-clips-notices .jfbm-notice-text').textContent, 'Making new clips is turned off by your server admin.');
    assert.equal(toolbar(off).content.querySelector('.jfbm-notice-text').textContent, 'No clips yet.');
    const readOnly = await opened({ capabilities: { StoreReadOnly: true, StoreReadOnlyReason: 'NewerVersion' } });
    assert.equal(root(readOnly).querySelector('.jfbm-clips-notices .jfbm-notice-text').textContent, 'Bookmarks are read-only right now: the bookmark data was written by a newer version of Bookmarks. Ask your server\'s administrator.');
    assert.equal(rows(readOnly).length, 10, 'the list still reads');

    // In a plain sentence, as on My Bookmarks; an admin is sent to the settings page.
    const unreadable = await opened({ capabilities: { StoreReadOnly: true, StoreReadOnlyReason: 'DataUnreadable', IsAdmin: true } });
    assert.equal(root(unreadable).querySelector('.jfbm-clips-notices .jfbm-notice-text').textContent, 'Bookmarks are read-only right now: the bookmark data couldn\'t be read, and no snapshot could take its place. The plugin\'s settings page (Dashboard, Plugins, Bookmarks) says what to do.');
    const unknown = await opened({ capabilities: { StoreReadOnly: true, StoreReadOnlyReason: 'SomethingNew' } });
    assert.equal(root(unknown).querySelector('.jfbm-clips-notices .jfbm-notice-text').textContent, 'Bookmarks are read-only right now. Ask your server\'s administrator.');
    assert.equal(unreadable.errors.length + unknown.errors.length, 0);

    const page = await start({ hash: '#/jfbm-clips' });
    page.respond('GET', '/Bookmarks/Clips', { title: 'Unavailable', status: 503, detail: 'The clips can\'t be listed right now.' }, 503, { contentType: 'application/problem+json' });
    showFallbackPage(page);
    await page.advance(120);
    const t = toolbar(page);
    const notice = t.content.querySelector('.jfbm-notice-error');
    assert.equal(notice.getAttribute('role'), 'alert');
    assert.equal(notice.querySelector('.jfbm-notice-text').textContent, 'The clips can\'t be listed right now.');
    page.respond('GET', '/Bookmarks/Clips', null, 200, { hang: true });
    notice.querySelector('button').click();
    await page.advance(100);
    assert.equal(t.content.getAttribute('aria-busy'), 'true');
    assert.equal(t.content.querySelector('.jfbm-notice'), null, 'no "Loading…" yet');
    await page.advance(250);
    assert.equal(t.content.querySelector('.jfbm-notice-info').textContent, 'Loading…');
    page.respond('GET', '/Bookmarks/Clips', { Items: fixtures(), TotalRecordCount: 10, StartIndex: 0 });
    notice.querySelector('button').click();
    await page.advance(50);
    assert.equal(rows(page).length, 10);
    assert.equal(t.content.querySelector('.jfbm-notice'), null);
    assert.equal([empty, noDownloads, off, readOnly, page].reduce((n, p) => n + p.errors.length, 0), 0);
});

test('turned off on the server: no link, the route shows the server\'s note, and nothing is asked of the clip endpoints', { skip }, async () => {
    const page = await start({ head: { off: ['clips', 'demo-a', 'demo-b'] }, hash: '#/jfbm-clips', off: true, capabilities: { Pages: { Bookmarks: true, Clips: false } } });
    showFallbackPage(page);
    const container = drawer(page);
    await page.advance(400);
    const r = root(page);
    assert.equal(r.getAttribute('data-jfbm-state'), 'off');
    assert.equal(r.querySelector('.jfbm-page-note').textContent, 'My Clips is turned off by your server admin.');
    assert.equal(r.querySelector('.jfbm-clips-toolbar'), null);
    assert.deepEqual(container.querySelectorAll('.jfkit-nav a').map((a) => a.getAttribute('data-jfbm-nav')), ['bookmarks']);
    assert.equal(page.fetchLog.filter((f) => f.path.indexOf('/Bookmarks/Clips') === 0).length, 0);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the links to the page while the server offers it: My Bookmarks\' header and clip lines, the player\'s list, the details section', { skip }, async () => {
    const heat = { Id: B1, ItemId: HEAT, ItemName: 'Heat (1995)', ItemType: 'Movie', StartTicks: 60 * T, EndTicks: 64 * T, Name: 'Bank job', Note: null, CreatedAt: '2026-10-01T10:00:00.0000000Z', UpdatedAt: '2026-10-05T10:00:00.0000000Z', ItemAvailable: true, Detached: false, Relinked: false, Origin: 'Native',
        Clip: { Status: 'Completed', Mode: 'Fast', StartTicks: 60 * T, EndTicks: 64 * T, IsOutdated: false, Progress: 100, FileName: 'Heat (1995) - Bank job [00.01.00-00.01.04].mkv', FileSize: 2048, RequestedAt: '2026-10-10T10:00:00.0000000Z', CompletedAt: '2026-10-10T10:00:05.0000000Z', Error: null } };
    const group = { ItemId: HEAT, ItemName: 'Heat (1995)', ItemType: 'Movie', ItemAvailable: true, Count: 1, LatestUpdatedAt: heat.UpdatedAt, Bookmarks: [heat], BookmarksTruncated: false };
    const bookmarksPage = async (capabilities) => {
        const page = await start({ hash: '#/jfbm-bookmarks', capabilities });
        page.respond('GET', '/Bookmarks/Groups', { Items: [group], TotalRecordCount: 1, StartIndex: 0 });
        page.respond('GET', '/Bookmarks/Query', { Items: [heat], TotalRecordCount: 1, StartIndex: 0 });
        showFallbackPage(page);
        await page.advance(120);
        return page;
    };
    const on = await bookmarksPage();
    const r = on.document.querySelector('#fallbackPage > [data-jfbm-page="bookmarks"]');
    const header = r.querySelector('.jfbm-page-header .jfbm-page-clips-link');
    assert.equal(header.textContent, 'My Clips');
    assert.equal(header.getAttribute('href'), '#/jfbm-clips');
    const chip = r.querySelector('.jfbm-row[data-id="' + B1 + '"] .jfbm-clip-ready a.jfbm-clip-in-clips');
    assert.equal(chip.textContent, 'View in My Clips');
    assert.deepEqual(r.querySelector('.jfbm-row[data-id="' + B1 + '"] .jfbm-clip-ready').querySelectorAll('button.jfbm-link').map((b) => b.textContent), ['Preview', 'Delete clip'],
        'a Fast clip (MKV) has Preview too (Chrome, Edge and Firefox play Matroska)');
    assert.equal(chip.getAttribute('href'), '#/jfbm-clips?highlight=' + key(B1), 'the clip marked there');
    const offPage = await bookmarksPage({ Pages: { Bookmarks: true, Clips: false } });
    const r2 = offPage.document.querySelector('#fallbackPage > [data-jfbm-page="bookmarks"]');
    assert.equal(r2.querySelector('.jfbm-page-clips-link'), null);
    assert.equal(r2.querySelector('.jfbm-clip-in-clips'), null, 'no link while the server doesn\'t offer the page');
    assert.equal(r2.querySelector('.jfbm-row[data-id="' + B1 + '"] .jfbm-clip-ready').textContent.indexOf('My Clips'), -1);

    // The player's list: All my bookmarks and My Clips, each closing the dialog.
    const player = await start({ capabilities: {} });
    player.respond('GET', '/Bookmarks', [heat]);
    const buttons = player.element('div', { class: 'buttons' }, [player.element('button', { class: 'btnSubtitles' })]);
    player.document.body.append(player.element('div', { class: 'videoPlayerContainer' }, [player.element('video')]),
        player.element('div', { id: 'videoOsdPage', class: 'page' }, [player.element('div', { class: 'videoOsdBottom' }, [buttons])]));
    await player.context.ApiClient.reportPlaybackProgress({ ItemId: HEAT, PositionTicks: 200000000 });
    await player.advance(400);
    player.document.querySelector('.btnBookmarkList').click();
    await player.advance(50);
    const panel = player.document.querySelector('.jfbm-backdrop .jfbm-panel');
    const links = panel.querySelectorAll('.jfbm-list-toolbar a[data-jfbm-link]');
    assert.deepEqual(links.map((a) => [a.textContent, a.getAttribute('href')]), [['All my bookmarks', '#/jfbm-bookmarks'], ['My Clips', '#/jfbm-clips']]);
    links[1].click();
    await player.advance(10);
    assert.equal(player.document.querySelector('.jfbm-backdrop'), null, 'the dialog closes as the link navigates');
    assert.equal(on.errors.length + offPage.errors.length + player.errors.length, 0, on.errors.concat(player.errors).join('\n'));
});

test('the clip-ready toast offers "View in My Clips" while the page is there; the link opens My Clips with the clip marked once', { skip }, async () => {
    const make = (status) => ({ Id: B1, ItemId: HEAT, ItemName: 'Heat (1995)', ItemType: 'Movie', StartTicks: 60 * T, EndTicks: 64 * T, Name: 'Bank job', Note: null, CreatedAt: '2026-10-01T10:00:00.0000000Z', UpdatedAt: '2026-10-05T10:00:00.0000000Z', ItemAvailable: true, Detached: false, Relinked: false, Origin: 'Native',
        Clip: status === 'Running'
            ? { Status: 'Running', Mode: 'Fast', StartTicks: 60 * T, EndTicks: 64 * T, IsOutdated: false, Progress: 30, FileName: null, FileSize: null, RequestedAt: '2026-10-10T10:00:00.0000000Z', CompletedAt: null, Error: null }
            : { Status: 'Completed', Mode: 'Fast', StartTicks: 60 * T, EndTicks: 64 * T, IsOutdated: false, Progress: 100, FileName: 'Heat (1995) - Bank job [00.01.00-00.01.04].mkv', FileSize: 2048, RequestedAt: '2026-10-10T10:00:00.0000000Z', CompletedAt: '2026-10-10T10:00:05.0000000Z', Error: null } });
    const run = async (capabilities) => {
        const page = await start({ hash: '#/jfbm-bookmarks', capabilities, clips: fixtures().concat([clip(B1, { Name: 'Bank job' })]) });
        const group = { ItemId: HEAT, ItemName: 'Heat (1995)', ItemType: 'Movie', ItemAvailable: true, Count: 1, LatestUpdatedAt: '2026-10-05T10:00:00.0000000Z', Bookmarks: [make('Running')], BookmarksTruncated: false };
        page.respond('GET', '/Bookmarks/Groups', { Items: [group], TotalRecordCount: 1, StartIndex: 0 });
        page.respond('GET', '/Bookmarks/Query', { Items: [make('Running')], TotalRecordCount: 1, StartIndex: 0 });
        page.respond('GET', '/Bookmarks/' + B1, make('Running'));
        showFallbackPage(page);
        await page.advance(120);
        page.respond('GET', '/Bookmarks/' + B1, make('Completed'));
        await page.advance(2100);
        return page;
    };
    const page = await run();
    const toast = page.document.querySelectorAll('.jfbm-toast').find((x) => x.textContent.indexOf('Clip ready: ') === 0);
    const actions = toast.querySelectorAll('.jfbm-toast-action');
    assert.deepEqual(actions.map((a) => [a.tagName, a.textContent]), [['BUTTON', 'Download'], ['A', 'View in My Clips']]);
    assert.equal(actions[1].getAttribute('href'), '#/jfbm-clips?highlight=' + key(B1));
    assert.equal(actions[1].getAttribute('is'), 'emby-linkbutton', 'jellyfin-web\'s router shows it, no handler of ours');

    // The link followed (what the router does with it): My Clips shows the clip marked, the highlight dropped in place.
    const length = page.context.history.length;
    await navigate(page, actions[1].getAttribute('href'));
    const target = row(page, B1);
    assert.ok(target && target.classList.contains('jfbm-page-highlight'));
    assert.equal(page.context.location.hash, '#/jfbm-clips');
    assert.equal(page.context.history.length, length);

    const without = await run({ Pages: { Bookmarks: true, Clips: false } });
    const plainToast = without.document.querySelectorAll('.jfbm-toast').find((x) => x.textContent.indexOf('Clip ready: ') === 0);
    assert.deepEqual(plainToast.querySelectorAll('.jfbm-toast-action').map((a) => a.textContent), ['Download'], 'only Download while the page is off');
    assert.equal(page.errors.length + without.errors.length, 0, page.errors.concat(without.errors).join('\n'));
});

test('the TV layout: the body carries jfbm-page-tv, and every control is a focusable field, select, box or link', { skip }, async () => {
    const page = await opened({ layout: 'tv' });
    const r = root(page);
    assert.ok(r.querySelector('.jfbm-clips-page').classList.contains('jfbm-page-tv'));
    const t = toolbar(page);
    assert.deepEqual([t.search, t.state, t.mode, t.type, t.sort, t.deleted].map((x) => x.tagName), ['INPUT', 'SELECT', 'SELECT', 'SELECT', 'SELECT', 'INPUT']);
    assert.ok(rows(page).filter((x) => x.querySelector('a.jfbm-clip-item-link')).length === 9, 'every row of a video the viewer can open has its link');
    const desktop = await opened();
    assert.equal(root(desktop).querySelector('.jfbm-clips-page').classList.contains('jfbm-page-tv'), false);
    assert.equal(page.errors.length + desktop.errors.length, 0, page.errors.join('\n'));
});

test('in Plugin Pages\' host the page renders from the shell and keeps its state in that link', { skip }, async () => {
    const page = await start({ head: { mode: 'Auto' }, hash: '#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fclips&state=Ready' });
    const view = page.element('div', { class: 'page userPreferencesPage', 'data-role': 'page' });
    const shell = page.element('div', { class: 'jfbm-page', 'data-jfbm-page': 'clips' }, [page.element('p', { class: 'jfbm-shell-note' }, ['This page needs the Bookmarks web UI, which is turned off or failed to load.'])]);
    view.append(shell);
    page.document.body.append(view);
    ['viewbeforeshow', 'pagebeforeshow', 'viewshow', 'pageshow'].forEach((type) => page.dispatch(view, type, { detail: { isRestored: false } }));
    await page.advance(120);
    assert.equal(view.getAttribute('data-title'), 'My Clips');
    assert.equal(shell.getAttribute('data-jfbm-host'), 'PluginPages');
    assert.equal(lastRequest(page, '/Bookmarks/Clips').query.state, 'Ready');
    const select = shell.querySelector('.jfbm-clips-sort-select');
    choose(page, select, 'Oldest');
    await page.advance(50);
    assert.equal(page.context.location.hash, '#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fclips&state=Ready&sort=Oldest');
    assert.equal(lastRequest(page, '/Bookmarks/Clips').query.sortBy, 'Oldest');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

// ----------------------------------------------------------------------------------------------------- the row actions

// A button of a row by its title (the fake DOM's selectors take no space inside an attribute value).
function titled(scope, title) {
    return scope.querySelectorAll('button').find((b) => b.getAttribute('title') === title) || null;
}

function actionTitles(r) {
    return r.querySelectorAll('.jfbm-clip-actions button').map((b) => b.getAttribute('title'));
}

function panel(page, title) {
    return page.document.querySelectorAll('.jfbm-backdrop .jfbm-panel').find((p) => p.querySelector('h3').textContent === title) || null;
}

function dialogButton(dialog, label) {
    return dialog.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === label) || null;
}

function toasts(page) {
    return page.document.querySelectorAll('.jfbm-toast').map((t) => t.querySelector('span').textContent);
}

function tickClip(page, id) {
    const box = row(page, id).querySelector('.jfbm-row-select-box');
    box.checked = true;
    page.dispatch(box, 'change');
}

function bar(page) {
    return root(page).querySelector('.jfbm-bulk-bar');
}

// What GET /Bookmarks/{id} answers for a clip of the fixtures (a BookmarkDto), as the editor and the clip watcher read it.
function bookmarkOf(c) {
    return {
        Id: c.Id, ItemId: c.ItemId, ItemName: c.ItemName, ItemType: c.ItemType, StartTicks: c.BookmarkStartTicks, EndTicks: c.BookmarkEndTicks,
        Name: c.Name, Note: c.Note, CreatedAt: c.CreatedAt, UpdatedAt: c.UpdatedAt, ItemAvailable: c.ItemAvailable, Detached: false, Relinked: false, Origin: 'Native',
        MediaSourceId: c.MediaSourceId, AudioStreamIndex: c.AudioStreamIndex,
        Clip: { Status: c.Clip.Status, Mode: c.Clip.Mode, StartTicks: c.Clip.StartTicks, EndTicks: c.Clip.EndTicks, IsOutdated: c.Clip.IsOutdated, Progress: c.Clip.Progress, FileName: c.Clip.FileName, FileSize: c.Clip.FileSize, RequestedAt: c.Clip.RequestedAt, CompletedAt: c.Clip.CompletedAt, Error: c.Clip.Error }
    };
}

const PLAY = 'Play the video at the clip\'s start';
const PROBLEM = { contentType: 'application/problem+json' };
const problem = (status, detail) => ({ title: 'Problem', status, detail });

test('the row actions by state: Preview and Download for a file that is there, Play while the video can be opened, Edit for a live bookmark, Make the clip again (marked when the range changed or the file is gone), Cancel, Delete, Forget, Dismiss; an offline row only Play and Edit, and no checkbox', { skip }, async () => {
    const page = await opened();
    const more = 'More actions';
    assert.deepEqual(rows(page).map((r) => [r.getAttribute('data-jfbm-clip'), actionTitles(r)]), [
        ['c2', [PLAY, 'Edit bookmark', 'Cancel the clip', more]],
        ['c3', [PLAY, 'Edit bookmark', 'Cancel the clip', more]],
        ['c1', ['Preview', 'Download', PLAY, 'Edit bookmark', 'Rename clip', 'Make the clip again', 'Delete clip', more]],
        ['c4', [PLAY, 'Edit bookmark', 'Rename clip', 'Make the clip again', 'Dismiss', more]],
        ['c5', [PLAY, 'Edit bookmark', 'Rename clip', 'Make the clip again', 'Dismiss', more]],
        ['c6', [PLAY, 'Edit bookmark', 'Rename clip', 'Make the clip again', 'Forget clip', more]],
        ['c7', [PLAY, 'Edit bookmark', more]],
        ['c8', ['Preview', 'Download', PLAY, 'Rename clip', 'Delete clip', more]],
        ['c9', ['Preview', 'Download', PLAY, 'Edit bookmark', 'Rename clip', 'Rename file', 'Make the clip again', 'Delete clip', more]],
        ['c10', ['Preview', 'Download', 'Edit bookmark', 'Rename clip', 'Delete clip', more]]
    ], 'a deleted bookmark\'s clip has no Edit and no remake; a video the viewer can\'t open no Play and no remake; Rename for every clip not being made whose folder is there, Rename file for a file with its old name');
    assert.ok(titled(row(page, 'c9'), 'Rename file').classList.contains('jfbm-clip-action-suggested'), 'Rename file marked, as the remake of a range that changed');
    assert.deepEqual(rows(page).filter((r) => titled(r, 'Make the clip again') && titled(r, 'Make the clip again').classList.contains('jfbm-clip-action-suggested')).map((r) => r.getAttribute('data-jfbm-clip')), ['c6', 'c9'],
        'marked for a file that is gone and a range that changed');
    assert.deepEqual(rows(page).filter((r) => !r.querySelector('.jfbm-row-select-box')).map((r) => r.getAttribute('data-jfbm-clip')), ['c7'], 'an offline row can\'t be selected');
    const offline = row(page, 'c7');
    assert.ok(offline.firstChild.classList.contains('jfbm-row-select-none') && offline.firstChild.getAttribute('aria-hidden') === 'true', 'the place of the checkbox kept, so the posters stay in one column');
    assert.equal(stateText(offline), 'Clip folder offline · Fast · 1.2 MB');
    assert.equal(offline.querySelector('.jfbm-clip-note').textContent, 'The drive or folder with this clip isn\'t available right now; its file isn\'t taken for gone.');
    assert.equal(row(page, 'c1').querySelector('.jfbm-row-select-box').getAttribute('aria-label'), 'Select Bank job');

    // A clip whose last rename isn't finished offers Play and Edit only, no checkbox, and says why; a clip in
    // a library has Rename file unmarked (renaming its file there costs what Jellyfin kept for it).
    const held = await opened({ clips: [clip('r1', { Name: 'Held' }, { State: 'RenameUnfinished', FileExists: false }), clip('l2', { Name: 'In lib' }, { IsNameOutdated: true, InLibrary: true, LibraryName: 'Clips' })] });
    const r1 = row(held, 'r1');
    assert.equal(stateText(r1), 'Rename not finished · Fast · 1.2 MB');
    assert.deepEqual(actionTitles(r1), [PLAY, 'Edit bookmark', more], 'no Rename, no remake, no delete or forget');
    assert.equal(r1.querySelector('.jfbm-row-select-box'), null, 'not selectable');
    assert.match(r1.querySelector('.jfbm-clip-note').textContent, /^Its file's last rename isn't finished .* tries again after a minute, then less often while nothing changes; until then nothing deletes, renames or makes this clip again\.$/);
    assert.ok(titled(row(held, 'l2'), 'Rename file'), 'offered');
    assert.equal(titled(row(held, 'l2'), 'Rename file').classList.contains('jfbm-clip-action-suggested'), false, 'not marked in a library');
    assert.equal(held.errors.length, 0, held.errors.join('\n'));
    assert.ok(row(page, 'c1').querySelectorAll('.jfbm-clip-actions button').every((b) => b.getAttribute('aria-label') === b.getAttribute('title')), 'every action labelled');
    assert.ok(titled(row(page, 'c1'), more).classList.contains('jfbm-row-more'), 'the More button a narrow screen shows alone');

    // A folder that is there but not recognised as the clips' own (no marker; the server's FolderUnavailable with RootAvailable).
    const stray = await opened({ clips: [clip('u1', { Name: 'Stray' }, { State: 'FolderUnavailable', FileExists: false, RootAvailable: true, MadeAt: null })] });
    const u1 = row(stray, 'u1');
    assert.equal(stateText(u1), 'Clip folder not recognised · Fast · 1.2 MB');
    assert.match(u1.querySelector('.jfbm-clip-note').textContent, /^Its folder is there, but isn't recognised as the one this clip was made in/);
    assert.match(u1.querySelector('.jfbm-clip-note').textContent, / Your server admin can confirm the folder\.$/, 'a member is pointed to their admin');
    const strayAdmin = await opened({ capabilities: { IsAdmin: true }, clips: [clip('u1', { Name: 'Stray' }, { State: 'FolderUnavailable', FileExists: false, RootAvailable: true, MadeAt: null })] });
    assert.match(row(strayAdmin, 'u1').querySelector('.jfbm-clip-note').textContent,
        / so the file isn't taken for gone\. You can confirm the folder on the Bookmarks settings page's Diagnostics tab \(Dashboard, Plugins, Bookmarks\)\.$/,
        'an admin is told it is their own step');
    assert.equal(strayAdmin.errors.length, 0, strayAdmin.errors.join('\n'));
    assert.deepEqual(actionTitles(u1), [PLAY, 'Edit bookmark', more], 'no Forget, no remake, no delete');
    assert.equal(u1.querySelector('.jfbm-row-select-box'), null);

    // Without the download permission no Preview or Download; with clips turned off no remake, the download still there.
    const noDownload = await opened({ capabilities: { CanDownloadClips: false, CanCreateClips: false } });
    assert.deepEqual(actionTitles(row(noDownload, 'c1')), [PLAY, 'Edit bookmark', 'Rename clip', 'Delete clip', more]);
    const clipsOff = await opened({ capabilities: { ClipsEnabled: false, CanCreateClips: false } });
    assert.deepEqual(actionTitles(row(clipsOff, 'c1')), ['Preview', 'Download', PLAY, 'Edit bookmark', 'Rename clip', 'Delete clip', more]);

    // Read-only data: nothing that writes, no checkboxes.
    const readOnly = await opened({ capabilities: { StoreReadOnly: true, StoreReadOnlyReason: 'NewerVersion' } });
    assert.deepEqual(actionTitles(row(readOnly, 'c1')), ['Preview', 'Download', PLAY, 'Edit bookmark', more]);
    assert.deepEqual(actionTitles(row(readOnly, 'c2')), [PLAY, 'Edit bookmark', more]);
    assert.equal(toolbar(readOnly).content.querySelectorAll('.jfbm-row-select-box').length, 0);
    assert.equal(toolbar(readOnly).content.querySelectorAll('.jfbm-row-select').length, 0, 'no column at all while nothing can be selected');
    assert.equal([page, stray, noDownload, clipsOff, readOnly].reduce((n, p) => n + p.errors.length, 0), 0, page.errors.join('\n'));
});

test('Preview: our dialog\'s <video> of the file with the token, an MP4 and an MKV alike; a Fast clip says why it may be silent; the focus starts on Close; the video\'s error event gives way to the sentence with Download focused when the file is served; closing lets the file go', { skip }, async () => {
    const page = await opened();
    const opener = titled(row(page, 'c1'), 'Preview');
    opener.focus();
    opener.click();
    await page.advance(20);
    const dialog = panel(page, 'Bank job');
    assert.ok(page.document.activeElement === dialogButton(dialog, 'Close'), 'the focus in the dialog, on Close');
    const video = dialog.querySelector('video');
    assert.equal(video.getAttribute('src'), 'http://127.0.0.1:8097/Bookmarks/c1/Clip/File?ApiKey=test-token&download=false', 'the token in the URL, as Jellyfin\'s own media URLs');
    assert.ok(video.hasAttribute('controls') && video.hasAttribute('autoplay'));
    assert.equal(dialog.querySelector('.jfbm-preview-hint'), null, 'an MP4 has no sound note');
    const download = dialog.querySelector('a.jfbm-preview-download');
    assert.equal(download.getAttribute('href'), 'http://127.0.0.1:8097/Bookmarks/c1/Clip/File?ApiKey=test-token&download=true');
    assert.equal(download.getAttribute('download'), 'Heat (1995) - Bank job [01.02.03-01.05.00].mp4');

    // A format the browser won't take (MEDIA_ERR_SRC_NOT_SUPPORTED, which is also how a 404 often reads): the route is asked
    // for the file's first byte, with the same URL and token; served, the sentence is the browser's.
    page.respond('GET', '/Bookmarks/c1/Clip/File', '', 206, { contentType: 'video/mp4' });
    video.error = { code: 4 };
    page.dispatch(video, 'error', { bubbles: false });
    await page.advance(20);
    const probe = lastRequest(page, '/Bookmarks/c1/Clip/File');
    assert.deepEqual([probe.query, probe.headers.Range], [{ ApiKey: 'test-token', download: 'false' }, 'bytes=0-0'], 'the video\'s own URL, one byte of it');
    assert.equal(dialog.querySelector('video'), null, 'the picture gone');
    assert.equal(dialog.querySelector('.jfbm-preview-message').textContent, 'This clip couldn\'t be played here. Download it instead.');
    assert.equal(dialog.querySelector('.jfbm-preview-message').classList.contains('hide'), false);
    assert.ok(page.document.activeElement === download, 'the focus on Download');
    dialogButton(dialog, 'Close').click();
    assert.equal(panel(page, 'Bank job'), null);
    assert.equal(video.hasAttribute('src'), false, 'the file let go');
    assert.ok(page.document.activeElement === opener, 'back on the row\'s Preview');

    // A Fast clip (MKV), tried too: its note, and the codec sentence when the browser can't decode it (MEDIA_ERR_DECODE: the
    // file came, so the route isn't asked).
    titled(row(page, 'c8'), 'Preview').click();
    await page.advance(20);
    const fast = panel(page, 'Kept one');
    assert.equal(fast.querySelector('.jfbm-preview-hint').textContent, 'No sound? A Fast clip keeps the video\'s own sound format, which some browsers don\'t play: download it to hear it.');
    const fastVideo = fast.querySelector('video');
    assert.equal(fastVideo.getAttribute('src'), 'http://127.0.0.1:8097/Bookmarks/c8/Clip/File?ApiKey=test-token&download=false');
    fastVideo.error = { code: 3 };
    page.dispatch(fastVideo, 'error', { bubbles: false });
    await page.advance(20);
    assert.equal(fast.querySelector('.jfbm-preview-message').textContent, 'Your browser can\'t play this clip: a Fast clip keeps the video\'s own format, and this browser doesn\'t play it. Download it instead.');
    assert.equal(fast.querySelector('.jfbm-preview-hint'), null);
    assert.equal(requests(page, '/Bookmarks/c8/Clip/File').length, 0, 'a decode error asks the route nothing');
    page.key(fast.querySelector('a.jfbm-preview-download'), 'Escape');
    assert.equal(panel(page, 'Kept one'), null);
    assert.equal(page.fetchLog.filter((f) => f.path.indexOf('/Clip/File') >= 0).length, 1, 'otherwise the browser loads the file itself, through the element');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Preview of a clip the file route refuses (deleted in another tab, its folder offline, the session expired): the route\'s sentence, no Download, the focus on Close, and the row read again', { skip }, async () => {
    const page = await opened();
    titled(row(page, 'c1'), 'Preview').click();
    await page.advance(20);
    const dialog = panel(page, 'Bank job');

    // Deleted in another tab: the file route's 404, then the row read again is gone from the list.
    page.data.clips = page.data.clips.filter((c) => c.Id !== 'c1');
    page.respond('GET', '/Bookmarks/c1/Clip/File', { title: 'Not Found', status: 404, detail: 'Bookmark not found.' }, 404, { contentType: 'application/problem+json' });
    const before = requests(page, '/Bookmarks/Clips').length;
    page.dispatch(dialog.querySelector('video'), 'error', { bubbles: false });
    await page.advance(50);
    assert.equal(dialog.querySelector('.jfbm-preview-message').textContent, 'This clip isn\'t available any more.');
    assert.equal(dialog.querySelector('a.jfbm-preview-download'), null, 'no Download that would fail too');
    assert.ok(page.document.activeElement === dialogButton(dialog, 'Close'));
    const reread = requests(page, '/Bookmarks/Clips').slice(before);
    assert.deepEqual(reread.map((f) => f.query.itemId), [key(HEAT)], 'the row\'s video read again');
    assert.equal(row(page, 'c1'), null, 'the row gone');
    assert.ok(panel(page, 'Bank job'), 'the dialog stays with its sentence');
    dialogButton(dialog, 'Close').click();
    assert.ok(page.document.activeElement !== page.document.body && row(page, 'c4').contains(page.document.activeElement), 'the focus on the next row (c4 came after c1), not dropped');

    // Its folder offline (the route's 503 says so) and the session expired (401): their sentences.
    for (const [status, body, sentence] of [
        [503, { title: 'Service Unavailable', status: 503, detail: 'The clip folder isn\'t available right now.' }, 'The clip folder isn\'t available right now.'],
        [401, '', 'Your session expired. Sign in again.']
    ]) {
        titled(row(page, 'c8'), 'Preview').click();
        await page.advance(20);
        const other = panel(page, 'Kept one');
        page.respond('GET', '/Bookmarks/c8/Clip/File', body, status, { contentType: 'application/problem+json' });
        other.querySelector('video').error = { code: 2 };
        page.dispatch(other.querySelector('video'), 'error', { bubbles: false });
        await page.advance(50);
        assert.equal(other.querySelector('.jfbm-preview-message').textContent, sentence, String(status));
        assert.equal(other.querySelector('a.jfbm-preview-download'), null);
        dialogButton(other, 'Close').click();
    }

    // The server not reached: said, and nothing read again.
    titled(row(page, 'c8'), 'Preview').click();
    await page.advance(20);
    const unreachable = panel(page, 'Kept one');
    page.respond('GET', '/Bookmarks/c8/Clip/File', null, 0, { networkError: true });
    const count = requests(page, '/Bookmarks/Clips').length;
    page.dispatch(unreachable.querySelector('video'), 'error', { bubbles: false });
    await page.advance(50);
    assert.equal(unreachable.querySelector('.jfbm-preview-message').textContent, 'The server could not be reached.');
    assert.equal(requests(page, '/Bookmarks/Clips').length, count);
    dialogButton(unreachable, 'Close').click();
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Download is the file route with the token (as 1.0\'s links), and Play starts the video at the clip\'s own start with the bookmark\'s media source and audio track', { skip }, async () => {
    const clips = fixtures();
    clips[2] = clip('c1', { Name: 'Bank job', MediaSourceId: 'ms1', AudioStreamIndex: 2 }, { Mode: 'Accurate', Container: 'mp4', FileName: 'Heat (1995) - Bank job [01.02.03-01.05.00].mp4', FileSize: 47185920, StartTicks: 3723 * T, EndTicks: 3900 * T, DurationTicks: 177 * T });
    const page = await opened({ clips });
    const clicked = [];
    page.document.addEventListener('click', (event) => {
        if (event.target.tagName === 'A' && event.target.hasAttribute('download')) {
            clicked.push([event.target.getAttribute('href'), event.target.getAttribute('download')]);
        }
    });
    titled(row(page, 'c1'), 'Download').click();
    assert.deepEqual(clicked, [['http://127.0.0.1:8097/Bookmarks/c1/Clip/File?ApiKey=test-token&download=true', 'Heat (1995) - Bank job [01.02.03-01.05.00].mp4']]);

    page.respond('GET', '/Sessions', [{ Id: 's1', DeviceId: 'device-1' }]);
    page.respond('POST', '/Sessions/s1/Playing', null, 204);
    titled(row(page, 'c1'), PLAY).click();
    await page.advance(50);
    await page.context.ApiClient.reportPlaybackStart({ ItemId: HEAT, PositionTicks: 3723 * T });
    await page.advance(50);
    const plays = page.fetchLog.filter((f) => f.path === '/Sessions/s1/Playing');
    assert.deepEqual(plays.map((f) => f.query), [{ playCommand: 'PlayNow', itemIds: HEAT, startPositionTicks: String(3723 * T), mediaSourceId: 'ms1', audioStreamIndex: '2' }]);
    assert.equal(page.fetchLog.filter((f) => f.path.startsWith('/Items')).length, 0);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Edit bookmark opens the editor on the bookmark as the server has it; a save reads that video\'s clips again and draws them in place; one deleted meanwhile says so', { skip }, async () => {
    const page = await opened();
    page.respond('GET', '/Bookmarks/c1', () => bookmarkOf(page.data.clips[2]));
    page.respond('POST', '/Bookmarks/c1', (entry) => {
        page.data.clips[2] = clip('c1', { Name: entry.body.Name }, page.data.clips[2].Clip);
        return bookmarkOf(page.data.clips[2]);
    });
    const reads = requests(page, '/Bookmarks/Clips').length;
    titled(row(page, 'c1'), 'Edit bookmark').click();
    await page.advance(50);
    const editor = panel(page, 'Edit bookmark');
    const name = editor.querySelector('input');
    assert.equal(name.value, 'Bank job');
    name.value = 'Vault';
    editor.querySelector('form').dispatchEvent(new page.Event('submit', { cancelable: true }));
    await page.advance(300);
    assert.equal(panel(page, 'Edit bookmark'), null);
    const after = requests(page, '/Bookmarks/Clips').slice(reads);
    assert.deepEqual(after.map((f) => f.query.itemId), [key(HEAT)], 'that video\'s clips once, not the whole list');
    assert.equal(row(page, 'c1').querySelector('.listItemBodyText bdi').textContent, 'Vault');

    // Deleted meanwhile: the sentence, and the row read again (gone from the list now).
    page.respond('GET', '/Bookmarks/c4', problem(404, 'Bookmark not found.'), 404, PROBLEM);
    page.data.clips = page.data.clips.filter((c) => c.Id !== 'c4');
    titled(row(page, 'c4'), 'Edit bookmark').click();
    await page.advance(100);
    assert.deepEqual(toasts(page).slice(-1), ['This bookmark was deleted elsewhere.']);
    assert.equal(panel(page, 'Edit bookmark'), null);
    assert.equal(row(page, 'c4'), null);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Make the clip again: the dialog names the bookmark\'s range and why, the mode starts at the server\'s default; queued, the row is read again and polled; a refusal is said in the dialog', { skip }, async () => {
    const page = await opened();
    page.respond('POST', '/Bookmarks/c6/Clip', problem(503, 'Not enough free space on the server for this clip. Ask your server admin.'), 503, PROBLEM);
    titled(row(page, 'c6'), 'Make the clip again').click();
    await page.advance(20);
    const dialog = panel(page, 'Make the clip again?');
    assert.deepEqual(dialog.querySelectorAll('p').map((p) => p.textContent), [
        'A new clip of the bookmark from 1:00 to 1:04 (0:04).',
        'Its file is gone; the new clip takes its place.'
    ]);
    const mode = dialog.querySelector('select.jfbm-remake-mode');
    assert.equal(mode.value, 'Accurate', 'the server\'s default mode');
    assert.ok(page.document.activeElement === dialogButton(dialog, 'Make the clip'));
    dialogButton(dialog, 'Make the clip').click();
    await page.advance(50);
    assert.equal(dialog.querySelector('.jfbm-error').textContent, 'Not enough free space on the server for this clip. Ask your server admin.');
    assert.ok(panel(page, 'Make the clip again?'), 'the dialog stays');
    assert.equal(dialogButton(dialog, 'Make the clip').disabled, false);
    assert.equal(row(page, 'c6').getAttribute('data-jfbm-state'), 'Missing', 'the row as it was');

    // Queued: the row read again shows it being made, and the page's poll follows it.
    page.respond('POST', '/Bookmarks/c6/Clip', (entry) => {
        page.data.clips[5] = clip('c6', { Name: 'Gone file' }, Object.assign(queued(), { Mode: entry.body.Mode }));
        return bookmarkOf(page.data.clips[5]);
    }, 202);
    page.respond('GET', '/Bookmarks/c6', () => bookmarkOf(page.data.clips[5]));
    mode.value = 'Fast';
    dialogButton(dialog, 'Make the clip').click();
    await page.advance(50);
    assert.equal(panel(page, 'Make the clip again?'), null);
    assert.deepEqual(page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/c6/Clip').map((f) => f.body), [{ Mode: 'Accurate' }, { Mode: 'Fast' }]);
    assert.deepEqual(toasts(page).slice(-1), ['Making the clip.']);
    await page.advance(150);
    assert.equal(row(page, 'c6').getAttribute('data-jfbm-state'), 'InProgress');
    assert.equal(stateText(row(page, 'c6')), 'Queued · Fast');
    const polls = requests(page, '/Bookmarks/Clips/Status').length;
    await page.advance(2500);
    assert.ok(requests(page, '/Bookmarks/Clips/Status').slice(polls).some((f) => f.query.ids.split(',').includes('c6')), 'polled with the others');

    // A range that changed says so; a file that is there stays until the new one is made.
    titled(row(page, 'c9'), 'Make the clip again').click();
    await page.advance(20);
    assert.deepEqual(panel(page, 'Make the clip again?').querySelectorAll('p').map((p) => p.textContent), [
        'A new clip of the bookmark from 1:00 to 1:04 (0:04).',
        'The bookmark\'s range changed after this clip was made; the new clip has the range it has now.',
        'The current file stays until the new one is made, and is deleted then.'
    ]);
    dialogButton(panel(page, 'Make the clip again?'), 'Cancel').click();
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Cancel stops a clip being made: the row is read again (a queued one cancelled at once)', { skip }, async () => {
    const page = await opened();
    page.respond('POST', '/Bookmarks/c3/Clip/Cancel', () => {
        page.data.clips[1] = clip('c3', { Note: 'Pilot cold open', ItemId: SHOW, ItemName: 'Show S01E02 - Pilot', ItemType: 'Episode' }, { Status: 'Cancelled', State: 'Failed', Mode: 'Accurate', FileName: null, FileSize: null, Error: null, Container: null, FileExists: false, RootAvailable: null, MadeAt: null });
        return bookmarkOf(page.data.clips[1]);
    });
    titled(row(page, 'c3'), 'Cancel the clip').click();
    await page.advance(200);
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/c3/Clip/Cancel').length, 1);
    assert.equal(row(page, 'c3').getAttribute('data-jfbm-state'), 'Failed');
    assert.equal(stateText(row(page, 'c3')), 'Cancelled · Accurate');
    assert.deepEqual(actionTitles(row(page, 'c3')), [PLAY, 'Edit bookmark', 'Rename clip', 'Make the clip again', 'Dismiss', 'More actions']);
    assert.deepEqual(toasts(page).slice(-1), ['Clip cancelled.']);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Delete: the confirmation names the file and its size (a deleted bookmark\'s clip otherwise); a file in use is the server\'s 409, said in the dialog with the row as it was; then deleted, the row goes, the summary follows', { skip }, async () => {
    const page = await opened();
    const summaries = requests(page, '/Bookmarks/Clips/Summary').length;
    const reads = requests(page, '/Bookmarks/Clips').length;
    const before = row(page, 'c1');
    page.respond('DELETE', '/Bookmarks/c1/Clip', problem(409, 'The clip file is in use (being played or downloaded); try again shortly.'), 409, PROBLEM);
    const opener = titled(before, 'Delete clip');
    opener.focus();
    opener.click();
    await page.advance(20);
    const dialog = panel(page, 'Delete clip?');
    assert.equal(dialog.querySelector('.jfbm-clip-dialog-text').textContent, 'Delete the clip file "Heat (1995) - Bank job [01.02.03-01.05.00].mp4" (45 MB)? The bookmark stays.');
    assert.ok(page.document.activeElement === dialogButton(dialog, 'Cancel'), 'the focus in the dialog, on Cancel');
    const remove = dialogButton(dialog, 'Delete');
    remove.focus();
    remove.click();
    // The browser drops the focus of a button disabled while its request runs.
    remove.blur();
    await page.advance(50);
    assert.equal(dialog.querySelector('.jfbm-error').textContent, 'The clip file is in use (being played or downloaded); try again shortly.');
    assert.ok(panel(page, 'Delete clip?'), 'the dialog stays, to try again');
    assert.ok(page.document.activeElement === remove, 'the focus back on Delete for another try');
    assert.ok(row(page, 'c1') === before, 'the row as it was');
    assert.equal(requests(page, '/Bookmarks/Clips/Summary').length, summaries);

    page.respond('DELETE', '/Bookmarks/c1/Clip', () => {
        page.data.clips = page.data.clips.filter((c) => c.Id !== 'c1');
        return null;
    }, 200);
    dialogButton(dialog, 'Delete').click();
    await page.advance(50);
    assert.equal(panel(page, 'Delete clip?'), null);
    assert.equal(row(page, 'c1'), null);
    assert.ok(page.document.activeElement === titled(row(page, 'c4'), PLAY), 'the focus on the next row\'s first action, not dropped to the page');
    assert.deepEqual(toasts(page).slice(-1), ['Clip deleted.']);
    assert.deepEqual(page.fetchLog.filter((f) => f.method === 'DELETE').map((f) => [f.path, f.query]), [['/Bookmarks/c1/Clip', {}], ['/Bookmarks/c1/Clip', {}]], 'the plain delete, file first (never onlyIfMissing)');
    assert.equal(requests(page, '/Bookmarks/Clips/Summary').length, summaries + 1);
    assert.equal(requests(page, '/Bookmarks/Clips').slice(reads).filter((f) => f.query.itemId).length, 0, 'its own change read nothing again');
    assert.equal(toolbar(page).summary.textContent, '9 clips · 4.8 MB · 2 in progress · 2 failed · 1 file missing · 1 in an offline or unrecognised clip folder');

    // A deleted bookmark's kept clip: 204, gone with its record.
    page.respond('DELETE', '/Bookmarks/c8/Clip', () => {
        page.data.clips = page.data.clips.filter((c) => c.Id !== 'c8');
        return null;
    }, 204);
    titled(row(page, 'c8'), 'Delete clip').click();
    await page.advance(20);
    const kept = panel(page, 'Delete clip?');
    assert.equal(kept.querySelector('.jfbm-clip-dialog-text').textContent, 'Delete this clip? Its bookmark was already deleted, so it will disappear from this list.');
    dialogButton(kept, 'Delete').click();
    await page.advance(50);
    assert.equal(row(page, 'c8'), null);

    // Gone elsewhere (another tab): the server's 404, said, and the row read again, which takes it out.
    page.respond('DELETE', '/Bookmarks/c9/Clip', problem(404, 'Bookmark not found.'), 404, PROBLEM);
    page.data.clips = page.data.clips.filter((c) => c.Id !== 'c9');
    titled(row(page, 'c9'), 'Delete clip').click();
    await page.advance(20);
    dialogButton(panel(page, 'Delete clip?'), 'Delete').click();
    await page.advance(100);
    assert.equal(panel(page, 'Delete clip?'), null);
    assert.deepEqual(toasts(page).slice(-1), ['Bookmark not found.']);
    assert.equal(row(page, 'c9'), null);

    // The last row's delete: the focus goes to the previous row; with no row left, to the toolbar (not its search box).
    const last = rows(page)[rows(page).length - 1];
    const lastKey = last.getAttribute('data-jfbm-clip');
    const previous = rows(page)[rows(page).length - 2];
    page.respond('DELETE', '/Bookmarks/' + lastKey + '/Clip', () => {
        page.data.clips = page.data.clips.filter((c) => key(c.Id) !== lastKey);
        return null;
    }, 200);
    titled(last, 'Delete clip').focus();
    titled(last, 'Delete clip').click();
    await page.advance(20);
    dialogButton(panel(page, 'Delete clip?'), 'Delete').click();
    await page.advance(50);
    assert.ok(previous.contains(page.document.activeElement), 'the previous row\'s action');
    const alone = await opened({ clips: [clip('c1', { Name: 'Bank job' }, { Mode: 'Accurate', Container: 'mp4', FileName: 'Heat (1995) - Bank job [01.02.03-01.05.00].mp4' })] });
    alone.respond('DELETE', '/Bookmarks/c1/Clip', () => {
        alone.data.clips = [];
        return null;
    }, 200);
    titled(row(alone, 'c1'), 'Delete clip').focus();
    titled(row(alone, 'c1'), 'Delete clip').click();
    await alone.advance(20);
    dialogButton(panel(alone, 'Delete clip?'), 'Delete').click();
    await alone.advance(50);
    assert.ok(alone.document.activeElement === toolbar(alone).state, 'the toolbar\'s first control that isn\'t a text field');
    assert.equal(page.errors.length + alone.errors.length, 0, page.errors.concat(alone.errors).join('\n'));
});

test('Forget a clip whose file is gone (onlyIfMissing, never a delete of a file); the file there after all is the server\'s 409, the row read again; a folder that isn\'t recognised is its 503, in the dialog; Dismiss a failed clip without asking', { skip }, async () => {
    const page = await opened();
    page.respond('DELETE', '/Bookmarks/c6/Clip', problem(503, 'The clip\'s folder is there, but it isn\'t recognised as the one its clips were made in (a drive or share that isn\'t mounted, or a folder in its place?), so its file isn\'t taken for gone and nothing was changed. Your server admin can confirm the folder on the Bookmarks settings page\'s Diagnostics tab.'), 503, PROBLEM);
    const opener = titled(row(page, 'c6'), 'Forget clip');
    opener.focus();
    opener.click();
    await page.advance(20);
    const dialog = panel(page, 'Forget clip?');
    assert.equal(dialog.querySelector('.jfbm-clip-dialog-text').textContent, 'Remove this entry? Its file is already gone. The bookmark stays.');
    assert.ok(page.document.activeElement === dialogButton(dialog, 'Cancel'), 'the focus on Cancel');
    const reads = requests(page, '/Bookmarks/Clips').length;
    dialogButton(dialog, 'Forget').click();
    await page.advance(200);
    assert.match(dialog.querySelector('.jfbm-error').textContent, /^The clip's folder is there, but it isn't recognised/);
    assert.ok(row(page, 'c6'));
    assert.deepEqual(requests(page, '/Bookmarks/Clips').slice(reads).map((f) => f.query.itemId), [key(HEAT)], 'the 503 reads the row again');
    assert.ok(row(page, 'c6') !== opener.closest('.jfbm-clips-row'), 'drawn again');

    // There after all (made again or put back): the server says so, nothing deleted, the row shows the file.
    page.respond('DELETE', '/Bookmarks/c6/Clip', () => {
        page.data.clips[5] = clip('c6', { Name: 'Gone file' }, {});
        return problem(409, 'The clip file is there after all (made again or put back), so nothing was deleted. Reload to see it.');
    }, 409, PROBLEM);
    dialogButton(dialog, 'Forget').click();
    await page.advance(200);
    assert.equal(panel(page, 'Forget clip?'), null);
    assert.deepEqual(toasts(page).slice(-1), ['The clip file is there after all (made again or put back), so nothing was deleted. Reload to see it.']);
    assert.equal(row(page, 'c6').getAttribute('data-jfbm-state'), 'Ready');
    assert.ok(page.fetchLog.filter((f) => f.method === 'DELETE').every((f) => f.path === '/Bookmarks/c6/Clip' && f.query.onlyIfMissing === 'true'));

    // Cancel after the row was drawn again under the dialog: the focus on the row as it is now, on its Forget.
    const redrawn = await opened();
    redrawn.respond('DELETE', '/Bookmarks/c6/Clip', problem(503, 'The clip folder isn\'t available right now.'), 503, PROBLEM);
    titled(row(redrawn, 'c6'), 'Forget clip').focus();
    titled(row(redrawn, 'c6'), 'Forget clip').click();
    await redrawn.advance(20);
    dialogButton(panel(redrawn, 'Forget clip?'), 'Forget').click();
    await redrawn.advance(200);
    dialogButton(panel(redrawn, 'Forget clip?'), 'Cancel').click();
    assert.ok(redrawn.document.activeElement === titled(row(redrawn, 'c6'), 'Forget clip'), 'back on the fresh row\'s Forget');

    // Forgotten: the row goes.
    const missing = await opened();
    missing.respond('DELETE', '/Bookmarks/c6/Clip', () => {
        missing.data.clips = missing.data.clips.filter((c) => c.Id !== 'c6');
        return null;
    }, 200);
    titled(row(missing, 'c6'), 'Forget clip').click();
    await missing.advance(20);
    dialogButton(panel(missing, 'Forget clip?'), 'Forget').click();
    await missing.advance(50);
    assert.equal(row(missing, 'c6'), null);
    assert.deepEqual(toasts(missing).slice(-1), ['Clip forgotten.']);

    // Dismiss: at once, the same route.
    missing.respond('DELETE', '/Bookmarks/c4/Clip', () => {
        missing.data.clips = missing.data.clips.filter((c) => c.Id !== 'c4');
        return null;
    }, 200);
    titled(row(missing, 'c4'), 'Dismiss').click();
    await missing.advance(50);
    assert.equal(missing.document.querySelector('.jfbm-backdrop'), null, 'no confirmation: nothing of value goes');
    assert.equal(row(missing, 'c4'), null);
    assert.deepEqual(missing.fetchLog.filter((f) => f.method === 'DELETE').map((f) => [f.path, f.query.onlyIfMissing]), [['/Bookmarks/c6/Clip', 'true'], ['/Bookmarks/c4/Clip', 'true']]);
    assert.deepEqual(toasts(missing).slice(-1), ['Failed clip cleared; the bookmark stays.']);
    assert.equal(page.errors.length + missing.errors.length + redrawn.errors.length, 0, page.errors.concat(missing.errors, redrawn.errors).join('\n'));
});

test('More opens the row\'s actions as our own menu, in the buttons\' order; the arrow keys move through it, and an item closes it and acts', { skip }, async () => {
    const page = await opened();
    titled(row(page, 'c1'), 'More actions').click();
    await page.advance(20);
    const menu = panel(page, 'Bank job');
    const items = menu.querySelectorAll('.jfbm-row-menu-item');
    assert.deepEqual(items.map((b) => b.textContent), ['Preview', 'Download', PLAY, 'Edit bookmark', 'Rename clip', 'Make the clip again', 'Delete clip']);
    assert.ok(page.document.activeElement === items[0]);
    page.key(items[0], 'ArrowDown');
    assert.ok(page.document.activeElement === items[1]);
    page.key(items[1], 'End');
    assert.ok(page.document.activeElement === items[6]);
    items[6].click();
    await page.advance(20);
    assert.equal(panel(page, 'Bank job'), null);
    assert.ok(panel(page, 'Delete clip?'), 'the item acts');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the bulk delete: Select all takes every clip that may be deleted (not an offline one), the dialog says what goes; across pages in batches of 200 with the progress; what the server keeps stays selected, named apart (in use, being made, the folder unavailable, refused)', { skip }, async () => {
    const small = await opened();
    tickClip(small, 'c1');
    assert.equal(bar(small).classList.contains('hide'), false);
    assert.equal(bar(small).getAttribute('aria-label'), 'Selected clips');
    assert.equal(bar(small).querySelector('.jfbm-bulk-count').textContent, '1 clip selected');
    bar(small).querySelector('.jfbm-bulk-all').click();
    assert.equal(bar(small).querySelector('.jfbm-bulk-count').textContent, '9 clips selected', 'every row but the offline one');
    assert.equal(row(small, 'c1').querySelector('.jfbm-row-select-box').checked, true);
    bar(small).querySelector('.jfbm-bulk-delete').focus();
    bar(small).querySelector('.jfbm-bulk-delete').click();
    await small.advance(20);
    assert.equal(panel(small, 'Delete 9 clips?').querySelector('.jfbm-bulk-description').textContent,
        '4 clip files (49 MB) will be deleted. 3 clips without a file (gone or failed) are taken off the list. 2 clips being made are kept; cancel them first. Their bookmarks stay; the clips of deleted bookmarks disappear from this list.',
        'the clips being made counted apart: the server keeps them');
    assert.ok(small.document.activeElement === dialogButton(panel(small, 'Delete 9 clips?'), 'Cancel'), 'the focus on Cancel');
    dialogButton(panel(small, 'Delete 9 clips?'), 'Cancel').click();
    assert.ok(small.document.activeElement === bar(small).querySelector('.jfbm-bulk-delete'), 'back on the bar\'s Delete…');
    bar(small).querySelector('.jfbm-bulk-clear').click();
    tickClip(small, 'c2');
    bar(small).querySelector('.jfbm-bulk-delete').click();
    await small.advance(20);
    assert.equal(panel(small, 'Delete 1 clip?').querySelector('.jfbm-bulk-description').textContent, '1 clip being made is kept; cancel it first. Their bookmarks stay.');
    dialogButton(panel(small, 'Delete 1 clip?'), 'Cancel').click();
    bar(small).querySelector('.jfbm-bulk-clear').click();
    assert.equal(bar(small).classList.contains('hide'), true);

    // 450 clips on nine pages (one request of 200, 200, 50), deleted in three batches.
    const clips = [];
    for (let i = 0; i < 450; i++) {
        clips.push(clip('p' + i, { Name: 'Clip ' + i }));
    }

    const page = await opened({ hash: '#/jfbm-clips?page=9', clips });
    assert.deepEqual(requests(page, '/Bookmarks/Clips').map((f) => [f.query.startIndex, f.query.limit]), [['0', '200'], ['200', '200'], ['400', '50']]);
    assert.equal(rows(page).length, 450);
    tickClip(page, 'p0');
    bar(page).querySelector('.jfbm-bulk-all').click();
    assert.equal(bar(page).querySelector('.jfbm-bulk-count').textContent, '450 clips selected');
    bar(page).querySelector('.jfbm-bulk-delete').click();
    await page.advance(20);
    const dialog = panel(page, 'Delete 450 clips?');
    assert.match(dialog.querySelector('.jfbm-bulk-description').textContent, /^450 clip files \(\d+ MB\) will be deleted\. Their bookmarks stay/);
    const progress = [];
    const keptBack = { p5: 'InUse', p6: 'InProgress', p7: 'FolderUnavailable', p8: 'Failed' };
    page.respond('POST', '/Bookmarks/Clips/BulkDelete', (entry) => {
        progress.push(dialog.querySelector('.jfbm-bulk-progress').textContent);
        const result = { FilesDeleted: [], RecordsRemoved: [], NotFound: [], InProgress: [], InUse: [], FolderUnavailable: [], Failed: [] };
        entry.body.Ids.forEach((id) => (keptBack[id] ? result[keptBack[id]] : result.FilesDeleted).push(id));
        page.data.clips = page.data.clips.filter((c) => keptBack[c.Id] || !entry.body.Ids.includes(c.Id));
        // What the server found: p6 being made, p7's folder offline.
        page.data.clips = page.data.clips.map((c) => c.Id === 'p6' ? clip('p6', { Name: 'Clip 6' }, running(5)) : c.Id === 'p7' ? clip('p7', { Name: 'Clip 7' }, { State: 'FolderUnavailable', FileExists: false, RootAvailable: false, MadeAt: null }) : c);
        return result;
    });
    dialogButton(dialog, 'Delete').click();
    await page.advance(500);
    const sent = page.fetchLog.filter((f) => f.path === '/Bookmarks/Clips/BulkDelete');
    assert.deepEqual(sent.map((f) => f.body.Ids.length), [200, 200, 50], 'the server\'s most a request, one after the other');
    assert.deepEqual(progress, ['Deleting 1–200 of 450…', 'Deleting 201–400 of 450…', 'Deleting 401–450 of 450…']);
    assert.ok(panel(page, 'Delete 450 clips?'), 'the dialog stays for the ones kept');
    assert.equal(dialog.querySelector('.jfbm-error').textContent, '446 clips deleted. One could not be deleted: a clip file is in use (being played or downloaded). Try again shortly. '
        + 'One could not be deleted: its clip is being made. Cancel it, or wait for it, first. '
        + 'One could not be deleted: its clip folder isn\'t available (offline, or not recognised as the one its clips were made in). '
        + 'One could not be deleted: its clip file couldn\'t be removed (it isn\'t in the server\'s clip folders, or its folder can\'t be read).');
    assert.equal(dialog.querySelector('.jfbm-bulk-progress').textContent, '');
    assert.deepEqual(rows(page).map((r) => r.getAttribute('data-jfbm-clip')).sort(), ['p5', 'p6', 'p7', 'p8']);
    assert.equal(row(page, 'p7').getAttribute('data-jfbm-state'), 'FolderUnavailable', 'read again: its folder is offline');
    assert.equal(row(page, 'p6').getAttribute('data-jfbm-state'), 'InProgress');
    assert.deepEqual(['p5', 'p6', 'p8'].map((id) => row(page, id).querySelector('.jfbm-row-select-box').checked), [true, true, true], 'still selected');
    assert.equal(bar(page).querySelector('.jfbm-bulk-count').textContent, '3 clips selected', 'the offline one can\'t stay selected');
    assert.equal(toolbar(page).summary.textContent.indexOf('4 clips'), 0, 'the summary read again');

    // Every one deleted: the dialog closes, and the focus, whose bar's Delete… is hidden now, goes to the first row left.
    const whole = await opened();
    tickClip(whole, 'c1');
    tickClip(whole, 'c8');
    bar(whole).querySelector('.jfbm-bulk-delete').focus();
    bar(whole).querySelector('.jfbm-bulk-delete').click();
    await whole.advance(20);
    whole.respond('POST', '/Bookmarks/Clips/BulkDelete', (entry) => {
        whole.data.clips = whole.data.clips.filter((c) => !entry.body.Ids.includes(c.Id));
        return { FilesDeleted: entry.body.Ids, RecordsRemoved: [], NotFound: [], InProgress: [], InUse: [], FolderUnavailable: [], Failed: [] };
    });
    dialogButton(panel(whole, 'Delete 2 clips?'), 'Delete').click();
    await whole.advance(200);
    assert.equal(panel(whole, 'Delete 2 clips?'), null);
    assert.equal(bar(whole).classList.contains('hide'), true);
    assert.ok(rows(whole)[0].contains(whole.document.activeElement), 'the first row\'s action, not the hidden bar');
    assert.equal(small.errors.length + page.errors.length + whole.errors.length, 0, page.errors.concat(whole.errors).join('\n'));
});

test('the bulk delete closed while its batches go stops after the request under way; a toast says what was deleted and that the rest is still selected', { skip }, async () => {
    const clips = [];
    for (let i = 0; i < 250; i++) {
        clips.push(clip('p' + i, { Name: 'Clip ' + i }));
    }

    const page = await opened({ hash: '#/jfbm-clips?page=5', clips });
    tickClip(page, 'p0');
    bar(page).querySelector('.jfbm-bulk-all').click();
    bar(page).querySelector('.jfbm-bulk-delete').click();
    await page.advance(20);
    const dialog = panel(page, 'Delete 250 clips?');
    page.respond('POST', '/Bookmarks/Clips/BulkDelete', (entry) => {
        dialogButton(dialog, 'Cancel').click();
        page.data.clips = page.data.clips.filter((c) => !entry.body.Ids.includes(c.Id));
        return { FilesDeleted: entry.body.Ids, RecordsRemoved: [], NotFound: [], InProgress: [], InUse: [], FolderUnavailable: [], Failed: [] };
    });
    dialogButton(dialog, 'Delete').click();
    await page.advance(500);
    assert.equal(page.fetchLog.filter((f) => f.path === '/Bookmarks/Clips/BulkDelete').length, 1, 'the rest never sent');
    assert.equal(panel(page, 'Delete 250 clips?'), null);
    assert.deepEqual(toasts(page).slice(-1), ['200 clips deleted. Stopped: 50 clips were not deleted. They are still selected.']);
    assert.equal(rows(page).length, 50);
    assert.ok(rows(page).every((r) => r.querySelector('.jfbm-row-select-box').checked));
    assert.equal(bar(page).querySelector('.jfbm-bulk-count').textContent, '50 clips selected');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the selection is kept through a redraw: a row being made patched or drawn again stays ticked; a row whose folder went offline leaves the selection; the TV layout\'s Select toggle shows the boxes and, off, drops the selection', { skip }, async () => {
    const page = await opened();
    tickClip(page, 'c2');
    tickClip(page, 'c1');
    assert.equal(bar(page).querySelector('.jfbm-bulk-count').textContent, '2 clips selected');
    await page.advance(2500);
    assert.equal(row(page, 'c2').querySelector('.jfbm-row-select-box').checked, true, 'patched in place, still ticked');
    // c2 ends: drawn again from the clip as read, still ticked.
    page.data.clips[0] = clip('c2', { Name: 'Car chase' }, {});
    await page.advance(2600);
    assert.equal(row(page, 'c2').getAttribute('data-jfbm-state'), 'Ready');
    assert.equal(row(page, 'c2').querySelector('.jfbm-row-select-box').checked, true, 'drawn again, still ticked');
    // A change of the video made elsewhere (here the editor's save of another of its bookmarks) reads its clips again;
    // c1's folder went offline meanwhile, so it leaves the selection.
    page.data.clips[2] = clip('c1', { Name: 'Bank job' }, { Mode: 'Accurate', Container: 'mp4', FileName: 'Heat (1995) - Bank job [01.02.03-01.05.00].mp4', FileSize: 47185920, State: 'FolderUnavailable', FileExists: false, RootAvailable: false, MadeAt: null });
    page.respond('GET', '/Bookmarks/c9', () => bookmarkOf(page.data.clips[8]));
    page.respond('POST', '/Bookmarks/c9', () => bookmarkOf(page.data.clips[8]));
    titled(row(page, 'c9'), 'Edit bookmark').click();
    await page.advance(50);
    panel(page, 'Edit bookmark').querySelector('form').dispatchEvent(new page.Event('submit', { cancelable: true }));
    await page.advance(300);
    assert.equal(row(page, 'c1').getAttribute('data-jfbm-state'), 'FolderUnavailable');
    assert.ok(row(page, 'c1').querySelector('.jfbm-row-select-box') === null, 'no checkbox any more');
    assert.equal(row(page, 'c2').querySelector('.jfbm-row-select-box').checked, true);
    assert.equal(bar(page).querySelector('.jfbm-bulk-count').textContent, '1 clip selected');

    const tv = await opened({ layout: 'tv' });
    const body = root(tv).querySelector('.jfbm-clips-page');
    const toggle = root(tv).querySelector('.jfbm-clips-toolbar .jfbm-page-select-button');
    assert.equal(toggle.getAttribute('aria-pressed'), 'false');
    assert.equal(body.classList.contains('jfbm-page-selecting'), false, 'the boxes hidden until Select (the stylesheet)');
    toggle.click();
    assert.equal(toggle.getAttribute('aria-pressed'), 'true');
    assert.equal(body.classList.contains('jfbm-page-selecting'), true);
    tickClip(tv, 'c1');
    assert.equal(bar(tv).querySelector('.jfbm-bulk-count').textContent, '1 clip selected');
    toggle.click();
    assert.equal(bar(tv).classList.contains('hide'), true, 'off: the selection dropped');
    assert.equal(row(tv, 'c1').querySelector('.jfbm-row-select-box').checked, false);
    const readOnly = await opened({ layout: 'tv', capabilities: { StoreReadOnly: true } });
    assert.equal(root(readOnly).querySelector('.jfbm-page-select-button'), null, 'nothing to select while read-only');
    assert.equal(page.errors.length + tv.errors.length + readOnly.errors.length, 0, page.errors.join('\n'));
});

test('a clip started from the editor opened here (Save & make clip) is picked up: the clip watcher\'s first report reads the row again, the page\'s poll follows it, and its later reports ask nothing more', { skip }, async () => {
    const page = await opened();
    page.respond('GET', '/Bookmarks/c1', () => bookmarkOf(page.data.clips[2]));
    page.respond('POST', '/Bookmarks/c1', () => bookmarkOf(page.data.clips[2]));
    // The list doesn't show the clip being made yet when the save's change reads it (a slow server, say).
    const making = clip('c1', { Name: 'Bank job' }, Object.assign(running(10), { Mode: 'Accurate' }));
    page.respond('POST', '/Bookmarks/c1/Clip', () => bookmarkOf(making), 202);
    titled(row(page, 'c1'), 'Edit bookmark').click();
    await page.advance(50);
    const editor = panel(page, 'Edit bookmark');
    dialogButton(editor, 'Save & make clip').click();
    await page.advance(300);
    const reads = () => requests(page, '/Bookmarks/Clips').filter((f) => f.query.itemId === key(HEAT)).length;
    const afterSave = reads();
    assert.equal(afterSave, 1, 'the save\'s change: the video\'s clips read once');
    assert.equal(row(page, 'c1').getAttribute('data-jfbm-state'), 'Ready');
    // The watcher's first report (2 s after): the row shows Ready, the report Running, so the row is read again.
    page.data.clips[2] = making;
    await page.advance(2100);
    assert.equal(row(page, 'c1').getAttribute('data-jfbm-state'), 'InProgress');
    assert.equal(reads(), afterSave + 1);
    // Its later reports, while the page follows the clip itself, ask nothing.
    await page.advance(4100);
    assert.equal(reads(), afterSave + 1, 'the watcher\'s ticks read nothing');
    assert.ok(requests(page, '/Bookmarks/Clips/Status').some((f) => f.query.ids.split(',').includes('c1')), 'the page\'s poll follows it');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

// --------------------------------------------------------------------------------------------------------- the renames

const IN_USE = 'The clip file is in use (being played or downloaded); try again shortly.';

function renameDialog(page) {
    return panel(page, 'Rename clip');
}

function title(r) {
    return r.querySelector('.listItemBodyText bdi').textContent;
}

test('Rename: one dialog, the focus in the name with its text selected; "Also rename the file" ticked for a file that is there, with its name now; Enter renames both in one POST …/Name with the clip\'s UpdatedAt; the row drawn from the answer, the focus back on its Rename, the toast; a deleted bookmark\'s kept clip renamed too', { skip }, async () => {
    const page = await opened();
    let selected = 0;
    page.respond('POST', '/Bookmarks/Clips/c8/Name', (entry) => {
        page.data.clips[7] = clip('c8', { Name: entry.body.Name, BookmarkDeleted: true, UpdatedAt: '2026-10-11T10:00:00.0000000Z' }, { FileName: 'Heat (1995) - ' + entry.body.Name + ' [00.01.00-00.01.04].mkv' });
        return page.data.clips[7];
    });
    const opener = titled(row(page, 'c8'), 'Rename clip');
    opener.focus();
    opener.click();
    await page.advance(20);
    const dialog = renameDialog(page);
    assert.ok(dialog, 'the dialog');
    assert.equal(page.document.querySelectorAll('.jfbm-backdrop').length, 1, 'exactly one dialog');
    const input = dialog.querySelector('.jfbm-clip-rename-input');
    assert.ok(page.document.activeElement === input, 'the focus in the name (a rename destroys nothing; typing is what the dialog is for)');
    input.select = () => selected++;
    assert.equal(input.value, 'Kept one');
    assert.equal(input.getAttribute('maxlength'), '100');
    assert.equal(dialog.querySelector('label[for="' + input.id + '"]').textContent, 'Name', 'the field labelled');
    const box = dialog.querySelector('.jfbm-clip-rename-file-box');
    assert.equal(box.checked, true, 'ticked: not in a library');
    assert.equal(box.parentNode.textContent, 'Also rename the file');
    assert.equal(dialog.querySelector('.jfbm-clip-rename-current').textContent, 'The file is "Heat (1995) - c8 [00.01.00-00.01.04].mkv" now.');
    assert.equal(dialog.querySelector('.jfbm-clip-rename-library'), null);
    assert.deepEqual(dialog.querySelectorAll('.jfbm-actions button').map((b) => b.textContent), ['Cancel', 'Rename']);
    input.value = '  New name  ';
    page.key(input, 'Enter');
    await page.advance(150);
    const sent = page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/Clips/c8/Name');
    assert.deepEqual(sent.map((f) => f.body), [{ Name: 'New name', RenameFile: true, IfUpdatedAt: '2026-10-05T10:00:00.0000000Z' }], 'trimmed, with the file, the clip\'s UpdatedAt as read');
    assert.equal(renameDialog(page), null, 'closed');
    assert.equal(title(row(page, 'c8')), 'New name');
    assert.ok(page.document.activeElement === titled(row(page, 'c8'), 'Rename clip'), 'back on the row\'s Rename, the row as drawn now');
    assert.deepEqual(toasts(page).slice(-1), ['Clip and its file renamed.']);
    assert.ok(requests(page, '/Bookmarks/Clips').some((f) => f.query.itemId === key(HEAT)), 'its video\'s clips read again under the filters');

    // Unticked, only the name: the toast says so; an empty name leaves it unnamed (null).
    page.respond('POST', '/Bookmarks/Clips/c10/Name', (entry) => {
        page.data.clips[9] = clip('c10', { Name: entry.body.Name, ItemId: GONE, ItemName: 'Gone (2001)', ItemAvailable: false, UpdatedAt: '2026-10-11T10:00:00.0000000Z' }, { IsNameOutdated: !entry.body.RenameFile });
        return page.data.clips[9];
    });
    titled(row(page, 'c10'), 'Rename clip').click();
    await page.advance(20);
    const second = renameDialog(page);
    second.querySelector('.jfbm-clip-rename-input').value = '   ';
    second.querySelector('.jfbm-clip-rename-file-box').checked = false;
    dialogButton(second, 'Rename').click();
    await page.advance(150);
    assert.deepEqual(page.fetchLog.filter((f) => f.path === '/Bookmarks/Clips/c10/Name').map((f) => f.body), [{ Name: null, RenameFile: false, IfUpdatedAt: '2026-10-05T10:00:00.0000000Z' }]);
    assert.deepEqual(toasts(page).slice(-1), ['Clip renamed.']);
    assert.deepEqual(actionTitles(row(page, 'c10')), ['Preview', 'Download', 'Edit bookmark', 'Rename clip', 'Rename file', 'Delete clip', 'More actions'], 'the file keeps its name: Rename file offered');

    // Escape closes without a request; Cancel too.
    titled(row(page, 'c10'), 'Rename clip').click();
    await page.advance(20);
    page.key(renameDialog(page).querySelector('.jfbm-clip-rename-input'), 'Escape');
    assert.equal(renameDialog(page), null);
    assert.equal(page.fetchLog.filter((f) => f.path === '/Bookmarks/Clips/c10/Name').length, 1);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Rename of a clip in a library: "Also rename the file" starts unticked, with what renaming it does to the library\'s video; a clip without a file (missing, failed) has no box and renames its name only', { skip }, async () => {
    const page = await opened();
    titled(row(page, 'c1'), 'Rename clip').click();
    await page.advance(20);
    const dialog = renameDialog(page);
    assert.equal(dialog.querySelector('.jfbm-clip-rename-file-box').checked, false, 'off in a library');
    assert.equal(dialog.querySelector('.jfbm-clip-rename-library').textContent,
        'This clip\'s file is in the library "Clips". Renaming the file makes Jellyfin see a new video there: what it keeps for the old one (played, favourite, collections, playlists, and anyone\'s bookmarks of that video) starts over, and files Jellyfin wrote next to it (an .nfo, trickplay images) stay under the old name.');
    dialogButton(dialog, 'Cancel').click();
    assert.equal(renameDialog(page), null);

    for (const id of ['c6', 'c4']) {
        page.respond('POST', '/Bookmarks/Clips/' + id + '/Name', (entry) => Object.assign({}, page.data.clips.find((c) => c.Id === id), { Name: entry.body.Name }));
        titled(row(page, id), 'Rename clip').click();
        await page.advance(20);
        const d = renameDialog(page);
        assert.equal(d.querySelector('.jfbm-clip-rename-file-box'), null, id + ': no file to rename');
        assert.equal(d.querySelector('.jfbm-clip-rename-current'), null);
        d.querySelector('.jfbm-clip-rename-input').value = 'Renamed ' + id;
        dialogButton(d, 'Rename').click();
        await page.advance(150);
        assert.deepEqual(page.fetchLog.filter((f) => f.path === '/Bookmarks/Clips/' + id + '/Name').map((f) => f.body.RenameFile), [false]);
    }

    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Rename refused: a file in use (409) is said in the dialog, which stays with the name kept for another try and the focus back in it; changed elsewhere (409) reads the row again and the next try carries its UpdatedAt; a folder gone offline (503) reads the row again; a clip gone (404) closes with the row read again', { skip }, async () => {
    const page = await opened();
    page.respond('POST', '/Bookmarks/Clips/c8/Name', problem(409, IN_USE), 409, PROBLEM);
    titled(row(page, 'c8'), 'Rename clip').click();
    await page.advance(20);
    const dialog = renameDialog(page);
    const input = dialog.querySelector('.jfbm-clip-rename-input');
    input.value = 'Try again';
    dialogButton(dialog, 'Rename').click();
    await page.advance(150);
    assert.ok(renameDialog(page) === dialog, 'the dialog stays');
    assert.equal(dialog.querySelector('.jfbm-clip-dialog-error').textContent, IN_USE);
    assert.equal(input.value, 'Try again', 'what was typed kept');
    assert.equal(input.disabled, false);
    assert.ok(page.document.activeElement === input, 'the focus back in the name');
    assert.equal(title(row(page, 'c8')), 'Kept one', 'the row as it was');

    // Changed elsewhere: the row read again (its UpdatedAt moved), the other tab's name in the field and said (before
    // a second Rename can replace it), the next try carries its UpdatedAt.
    page.data.clips[7] = clip('c8', { Name: 'Renamed elsewhere', BookmarkDeleted: true, UpdatedAt: '2026-10-12T10:00:00.0000000Z' }, {});
    page.respond('POST', '/Bookmarks/Clips/c8/Name', problem(409, 'Changed elsewhere. Reload to see the latest.'), 409, PROBLEM);
    dialogButton(dialog, 'Rename').click();
    await page.advance(200);
    assert.equal(dialog.querySelector('.jfbm-clip-dialog-error').textContent, 'Changed elsewhere: it is named "Renamed elsewhere" now. Rename replaces that.');
    assert.equal(input.value, 'Renamed elsewhere', 'the other tab\'s name in the field');
    assert.equal(title(row(page, 'c8')), 'Renamed elsewhere', 'the row read again');
    input.value = 'Try again';
    page.respond('POST', '/Bookmarks/Clips/c8/Name', (entry) => {
        page.data.clips[7] = Object.assign({}, page.data.clips[7], { Name: entry.body.Name, UpdatedAt: '2026-10-13T10:00:00.0000000Z' });
        return page.data.clips[7];
    });
    dialogButton(dialog, 'Rename').click();
    await page.advance(150);
    const tries = page.fetchLog.filter((f) => f.path === '/Bookmarks/Clips/c8/Name').map((f) => f.body.IfUpdatedAt);
    assert.deepEqual(tries, ['2026-10-05T10:00:00.0000000Z', '2026-10-05T10:00:00.0000000Z', '2026-10-12T10:00:00.0000000Z']);
    assert.equal(renameDialog(page), null);
    assert.equal(title(row(page, 'c8')), 'Try again');

    // Its folder gone offline: 503 in the dialog, and the row read again shows it offline (no Rename any more).
    page.respond('POST', '/Bookmarks/Clips/c9/Name', problem(503, 'The clip\'s folder isn\'t available (an unplugged drive, or a folder that was moved or removed); nothing was changed. Try again once it is back.'), 503, PROBLEM);
    titled(row(page, 'c9'), 'Rename clip').click();
    await page.advance(20);
    page.data.clips[8] = clip('c9', { Name: 'Renamed' }, { State: 'FolderUnavailable', FileExists: false, RootAvailable: false, MadeAt: null });
    dialogButton(renameDialog(page), 'Rename').click();
    await page.advance(200);
    assert.match(renameDialog(page).querySelector('.jfbm-clip-dialog-error').textContent, /^The clip's folder isn't available/);
    assert.equal(row(page, 'c9').getAttribute('data-jfbm-state'), 'FolderUnavailable');
    assert.equal(titled(row(page, 'c9'), 'Rename clip'), null, 'no rename for an offline folder');
    dialogButton(renameDialog(page), 'Cancel').click();

    // Gone (deleted in another tab): 404, the dialog closed, the row read again (gone from the list).
    page.respond('POST', '/Bookmarks/Clips/c1/Name', problem(404, 'Clip not found.'), 404, PROBLEM);
    titled(row(page, 'c1'), 'Rename clip').click();
    await page.advance(20);
    page.data.clips = page.data.clips.filter((c) => c.Id !== 'c1');
    dialogButton(renameDialog(page), 'Rename').click();
    await page.advance(200);
    assert.equal(renameDialog(page), null);
    assert.equal(row(page, 'c1'), null);
    assert.deepEqual(toasts(page).slice(-1), ['Clip not found.']);
    assert.ok(page.document.activeElement !== page.document.body, 'the focus not dropped');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Rename file for a file with its old name: the confirmation names the file and the clip\'s name, the focus on Cancel (it changes a file); Rename file posts …/RenameFile, the row drawn again without the badge, the focus on its first action; in use, the server\'s 409 in the dialog', { skip }, async () => {
    const page = await opened();
    const opener = titled(row(page, 'c9'), 'Rename file');
    opener.focus();
    opener.click();
    await page.advance(20);
    const dialog = panel(page, 'Rename file?');
    assert.equal(page.document.querySelectorAll('.jfbm-backdrop').length, 1, 'exactly one dialog');
    assert.ok(page.document.activeElement === dialogButton(dialog, 'Cancel'), 'the focus on Cancel');
    assert.equal(dialog.querySelector('.jfbm-clip-dialog-text').textContent, 'Rename the clip file "Heat (1995) - c9 [00.01.00-00.01.04].mkv" to match the clip\'s name, "Renamed"?');
    page.respond('POST', '/Bookmarks/Clips/c9/RenameFile', problem(409, IN_USE), 409, PROBLEM);
    dialogButton(dialog, 'Rename file').click();
    await page.advance(150);
    assert.equal(dialog.querySelector('.jfbm-clip-dialog-error').textContent, IN_USE);
    assert.ok(panel(page, 'Rename file?') === dialog, 'the dialog stays');
    assert.equal(dialogButton(dialog, 'Rename file').disabled, false);
    page.respond('POST', '/Bookmarks/Clips/c9/RenameFile', () => {
        page.data.clips[8] = clip('c9', { Name: 'Renamed' }, { IsOutdated: true, LastJobFailed: true, Status: 'Failed', Error: 'ffmpeg exited with code 1.', FileName: 'Heat (1995) - Renamed [00.01.00-00.01.04].mkv' });
        return page.data.clips[8];
    });
    dialogButton(dialog, 'Rename file').click();
    await page.advance(150);
    assert.equal(panel(page, 'Rename file?'), null);
    assert.equal(row(page, 'c9').querySelectorAll('.jfbm-badge').map((b) => b.textContent).includes('Name changed'), false, 'the badge gone');
    assert.equal(titled(row(page, 'c9'), 'Rename file'), null);
    assert.ok(page.document.activeElement === row(page, 'c9').querySelector('.jfbm-clip-actions button'), 'the focus on the row\'s first action (Rename file is gone)');
    assert.deepEqual(toasts(page).slice(-1), ['File renamed to "Heat (1995) - Renamed [00.01.00-00.01.04].mkv".']);
    const renameFiles = page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/Clips/c9/RenameFile');
    assert.equal(renameFiles.length, 2);
    assert.ok(renameFiles.every((f) => f.body && f.body.IfUpdatedAt === clip('c9', { Name: 'Renamed' }, {}).UpdatedAt), 'the clip\'s UpdatedAt sent');

    // Renamed in another tab meanwhile: 409, the question names the clip's name now and says so, and the next
    // try carries its UpdatedAt.
    const other = await opened({ clips: [clip('o1', { Name: 'First' }, { IsNameOutdated: true })] });
    titled(row(other, 'o1'), 'Rename file').click();
    await other.advance(20);
    const otherDialog = panel(other, 'Rename file?');
    other.data.clips[0] = clip('o1', { Name: 'Second', UpdatedAt: '2026-10-14T10:00:00.0000000Z' }, { IsNameOutdated: true });
    other.respond('POST', '/Bookmarks/Clips/o1/RenameFile', problem(409, 'Changed elsewhere. Reload to see the latest.'), 409, PROBLEM);
    dialogButton(otherDialog, 'Rename file').click();
    await other.advance(200);
    assert.ok(panel(other, 'Rename file?') === otherDialog, 'the dialog stays');
    assert.equal(otherDialog.querySelector('.jfbm-clip-dialog-text').textContent, 'Rename the clip file "Heat (1995) - o1 [00.01.00-00.01.04].mkv" to match the clip\'s name, "Second"?');
    assert.equal(otherDialog.querySelector('.jfbm-clip-dialog-error').textContent, 'Changed elsewhere: the clip is named "Second" now; Rename file names its file for that.');
    other.respond('POST', '/Bookmarks/Clips/o1/RenameFile', () => Object.assign({}, other.data.clips[0], { Clip: Object.assign({}, other.data.clips[0].Clip, { IsNameOutdated: false, FileName: 'Heat (1995) - Second [00.01.00-00.01.04].mkv' }) }));
    dialogButton(otherDialog, 'Rename file').click();
    await other.advance(200);
    assert.equal(panel(other, 'Rename file?'), null);
    assert.deepEqual(other.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/Clips/o1/RenameFile').map((f) => f.body.IfUpdatedAt),
        [clip('o1', { Name: 'First' }, {}).UpdatedAt, '2026-10-14T10:00:00.0000000Z']);
    assert.equal(other.errors.length, 0, other.errors.join('\n'));

    // A clip in a library says what renaming its file does there.
    const library = await opened({ clips: [clip('l1', { Name: 'New' }, { IsNameOutdated: true, InLibrary: true, LibraryName: null })] });
    titled(row(library, 'l1'), 'Rename file').click();
    await library.advance(20);
    assert.equal(panel(library, 'Rename file?').querySelector('.jfbm-clip-rename-library').textContent,
        'This clip\'s file is in a library. Renaming the file makes Jellyfin see a new video there: what it keeps for the old one (played, favourite, collections, playlists, and anyone\'s bookmarks of that video) starts over, and files Jellyfin wrote next to it (an .nfo, trickplay images) stay under the old name.');
    assert.equal(page.errors.length + library.errors.length, 0, page.errors.concat(library.errors).join('\n'));
});

test('the renames on the TV layout: Enter on Rename opens one dialog with the focus in the name; the D-pad goes down to the box (OK ticks it), Cancel and Rename; Enter there renames and the focus comes back to the row; Rename file starts on Cancel, one ArrowDown to Rename file', { skip }, async () => {
    const page = await opened({ layout: 'tv' });
    page.respond('POST', '/Bookmarks/Clips/c8/Name', (entry) => {
        page.data.clips[7] = clip('c8', { Name: entry.body.Name, BookmarkDeleted: true, UpdatedAt: '2026-10-11T10:00:00.0000000Z' }, {});
        return page.data.clips[7];
    });
    const opener = titled(row(page, 'c8'), 'Rename clip');
    opener.focus();
    page.key(opener, 'Enter');
    opener.click();
    await page.advance(20);
    assert.equal(page.document.querySelectorAll('.jfbm-backdrop').length, 1, 'exactly one dialog');
    const dialog = renameDialog(page);
    const input = dialog.querySelector('.jfbm-clip-rename-input');
    const box = dialog.querySelector('.jfbm-clip-rename-file-box');
    assert.ok(page.document.activeElement === input, 'the focus in the name');
    page.key(input, 'ArrowDown');
    assert.ok(page.document.activeElement === box, 'down to the box');
    page.key(box, 'Enter');
    assert.equal(box.checked, false, 'OK on the box ticks it off (no rename)');
    assert.equal(page.fetchLog.filter((f) => f.path === '/Bookmarks/Clips/c8/Name').length, 0);
    page.key(box, 'Enter');
    assert.equal(box.checked, true);
    page.key(box, 'ArrowDown');
    assert.ok(page.document.activeElement === dialogButton(dialog, 'Cancel'), 'down to Cancel');
    page.key(page.document.activeElement, 'ArrowDown');
    const rename = dialogButton(dialog, 'Rename');
    assert.ok(page.document.activeElement === rename, 'down to Rename');
    assert.equal(page.document.querySelectorAll('.jfbm-backdrop').length, 1, 'still one dialog');
    page.key(rename, 'Enter');
    rename.click();
    await page.advance(150);
    assert.deepEqual(page.fetchLog.filter((f) => f.path === '/Bookmarks/Clips/c8/Name').map((f) => f.body.RenameFile), [true]);
    assert.equal(renameDialog(page), null);
    assert.ok(page.document.activeElement === titled(row(page, 'c8'), 'Rename clip'), 'back on the row');

    const fileOpener = titled(row(page, 'c9'), 'Rename file');
    fileOpener.focus();
    page.key(fileOpener, 'Enter');
    fileOpener.click();
    await page.advance(20);
    assert.equal(page.document.querySelectorAll('.jfbm-backdrop').length, 1, 'one dialog');
    const confirm = panel(page, 'Rename file?');
    assert.ok(page.document.activeElement === dialogButton(confirm, 'Cancel'));
    page.key(page.document.activeElement, 'ArrowDown');
    assert.ok(page.document.activeElement === dialogButton(confirm, 'Rename file'), 'one ArrowDown to Rename file');
    page.key(page.document.activeElement, 'Escape');
    assert.equal(panel(page, 'Rename file?'), null);
    assert.ok(page.document.activeElement === titled(row(page, 'c9'), 'Rename file'), 'back on the row\'s Rename file');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});
