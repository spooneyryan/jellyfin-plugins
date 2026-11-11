// The My Clips page's "All users" scope (Web/js/75-page-clips-admin.js) in the kit's fake page, with the
// built ClientScript. PageHostingTests runs it:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfbm node --test plugins/Bookmarks/tests/Jellyfin.Plugin.Bookmarks.Tests/Web/page-clips-admin.test.mjs
// The switch ("My clips" / "All users") and the Owner select only for an admin the server allows it (CanManageAllClips); a
// hash naming the scope for anyone else shows their own clips and is left as it came; in the scope the list, the summary,
// the poll and a row read again ask the admin routes, with the owner; each row names its owner (bidi-isolated) and its
// file's path (with Copy), never a note; the scope and the owner in the hash (Back, Forward, deep links); the scope's
// actions only (Preview and Download through the admin file route, Play, Cancel, Delete, Forget, Dismiss; no Rename, remake
// or Edit), their dialogs naming whose clip it is with the focus on Cancel and the server's refusals said; the bulk delete
// through the admins' route with its outcome per reason; the TV layout's switch. Fake-DOM nodes are compared with === inside
// assert.ok (page-clips.test.mjs says why).
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');

const SHELL = (key) => '<div class="jfbm-page" data-jfbm-page="' + key + '"><p class="jfbm-shell-note">This page needs the Bookmarks web UI, which is turned off or failed to load.</p></div>';
const CAPABILITIES = {
    ClipsEnabled: true,
    CanCreateClips: true,
    CanDownloadClips: true,
    CanManageAllClips: true,
    MaxClipDurationSeconds: 600,
    DefaultClipMode: 'Accurate',
    IsAdmin: true,
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
const HEAT = 'aaaa0000000000000000000000000001';
const ALICE = 'a11ce000000000000000000000000001';
const BOB = 'b0b00000000000000000000000000002';
const ADMIN = 'ad000000000000000000000000000003';
const T = 10000000;
const key = (id) => String(id).replace(/-/g, '').toLowerCase();
const PLAY = 'Play the video at the clip\'s start';
const PROBLEM = { contentType: 'application/problem+json' };
const problem = (status, detail) => ({ title: 'Problem', status, detail });

// A clip as GET /Bookmarks/Admin/Clips answers it (BookmarkAdminClipItemDto: no Note member), a Ready Fast one of alice's.
function adminClip(id, owner, extra, clipExtra) {
    const names = { [ALICE]: 'alice', [BOB]: 'Deleted user (' + BOB + ')', [ADMIN]: 'admin' };
    return Object.assign({
        Id: id,
        UserId: owner || ALICE,
        UserName: names[owner || ALICE],
        UserDeleted: (owner || ALICE) === BOB,
        BookmarkDeleted: false,
        Origin: 'Native',
        Name: null,
        ItemId: HEAT,
        ItemName: 'Heat (1995)',
        ItemType: 'Movie',
        ItemAvailable: true,
        BookmarkStartTicks: 60 * T,
        BookmarkEndTicks: 64 * T,
        Clip: Object.assign({
            Status: 'Completed',
            Mode: 'Fast',
            StartTicks: 60 * T,
            EndTicks: 64 * T,
            IsOutdated: false,
            FileName: 'Heat (1995) - ' + id + ' [00.01.00-00.01.04].mkv',
            FileSize: 1258291,
            RequestedAt: '2026-10-10T10:00:00.0000000Z',
            CompletedAt: '2026-10-10T10:00:05.0000000Z',
            State: 'Ready',
            Container: 'mkv',
            DurationTicks: 4 * T,
            FileExists: true,
            IsNameOutdated: false,
            LastJobFailed: false,
            InLibrary: false,
            RootAvailable: true,
            MadeAt: '2026-10-10T10:00:05.0000000Z'
        }, clipExtra || {}),
        FilePath: 'C:\\Jellyfin\\data\\bookmarks\\clips\\' + (owner === BOB ? 'bob' : 'alice') + '\\Heat (1995) - ' + id + ' [00.01.00-00.01.04].mkv',
        RootId: '0123456789ab',
        CreatedAt: '2026-10-01T10:00:00.0000000Z',
        UpdatedAt: '2026-10-05T10:00:00.0000000Z'
    }, extra || {});
}

// The admin's own clip as GET /Bookmarks/Clips answers it (the owner's list, a note of their own).
function ownClip(id) {
    const c = adminClip(id, ADMIN, { Name: 'Mine', Note: 'my own note' });
    delete c.UserId;
    delete c.UserName;
    delete c.UserDeleted;
    delete c.FilePath;
    delete c.RootId;
    return c;
}

function fixtures() {
    return [
        adminClip('m1', ALICE, { Name: 'Making', FilePath: null }, { Status: 'Running', State: 'InProgress', Progress: 40, FileName: null, FileSize: null, CompletedAt: null, Container: null, FileExists: false, RootAvailable: null, MadeAt: null }),
        adminClip('a1', ALICE, { Name: 'Bank job' }),
        adminClip('a2', ALICE, { Name: 'Gone' }, { State: 'Missing', FileExists: false, MadeAt: null }),
        adminClip('b1', BOB, { Name: 'Kept', BookmarkDeleted: true }),
        adminClip('b2', BOB, { Name: 'Failed one', FilePath: null }, { Status: 'Failed', State: 'Failed', FileName: null, FileSize: null, Error: 'ffmpeg failed', Container: null, FileExists: false, RootAvailable: null, MadeAt: null }),
        adminClip('b3', BOB, { Name: 'On the stick' }, { State: 'FolderUnavailable', FileExists: false, RootAvailable: false, MadeAt: null }),
        adminClip('a3', ALICE, { Name: 'Held for a rename' }, { State: 'RenameUnfinished', FileExists: false }),
        adminClip('x1', ADMIN, { Name: 'Mine', ItemAvailable: false })
    ];
}

function summaryOf(clips) {
    const users = [ADMIN, ALICE, BOB].map((id) => {
        const mine = clips.filter((c) => c.UserId === id);
        return { UserId: id, UserName: id === ALICE ? 'alice' : id === ADMIN ? 'admin' : 'Deleted user (' + id + ')', UserDeleted: id === BOB, Count: mine.length, TotalBytes: mine.reduce((n, c) => n + (c.Clip.FileSize || 0), 0) };
    }).filter((u) => u.Count > 0);
    return { Users: users, Count: clips.length, TotalBytes: users.reduce((n, u) => n + u.TotalBytes, 0) };
}

async function start(options) {
    options = options || {};
    const page = createPage();
    page.context.location.hash = options.hash || '#/home';
    if (options.layout) {
        page.document.documentElement.classList.add('layout-' + options.layout);
    }

    const island = page.element('script', { type: 'application/json', 'data-jfbm-head': '', 'data-jfbm-pages': '' });
    island.textContent = JSON.stringify({ pages: ['bookmarks', 'clips'], off: [], mode: 'FallbackOnly', nav: true });
    page.document.head.append(island);
    page.context.LibraryMenu = { setTitle: () => {} };
    page.respond('GET', '/Bookmarks/Capabilities', Object.assign({}, CAPABILITIES, options.capabilities || {}));
    page.respond('GET', '/Bookmarks/Pages/clips', SHELL('clips'), 200, { contentType: 'text/html; charset=utf-8' });
    page.respond('GET', '/Bookmarks', []);
    page.data = { clips: options.clips || fixtures(), own: [ownClip('x1')] };
    const list = (items, q) => {
        const states = q.state ? q.state.split(',') : null;
        const filtered = items.filter((c) => (!states || states.indexOf(c.Clip.State) >= 0)
            && (!q.itemId || key(c.ItemId) === key(q.itemId))
            && (!q.userId || key(c.UserId) === key(q.userId)));
        const begin = Number(q.startIndex || 0);
        return { Items: filtered.slice(begin, begin + Number(q.limit || 50)), TotalRecordCount: filtered.length, StartIndex: begin };
    };
    page.respond('GET', '/Bookmarks/Clips', (entry) => list(page.data.own, entry.query));
    page.respond('GET', '/Bookmarks/Clips/Summary', { Count: 1, TotalBytes: 4, InProgress: 0, Failed: 0, Missing: 0, FolderUnavailable: 0 });
    page.respond('GET', '/Bookmarks/Admin/Clips', (entry) => list(page.data.clips, entry.query));
    page.respond('GET', '/Bookmarks/Admin/Clips/Summary', () => summaryOf(page.data.clips));
    page.respond('GET', '/Bookmarks/Admin/Clips/Status', (entry) => String(entry.query.ids || '').split(',')
        .map((id) => page.data.clips.find((c) => key(c.Id) === id)).filter(Boolean)
        .map((c) => ({ Id: key(c.Id), Status: c.Clip.Status, State: c.Clip.State, Progress: c.Clip.Progress, FileName: c.Clip.FileName, FileSize: c.Clip.FileSize })));
    page.run(source, file);
    await page.advance(30);
    page.installApiClient({ userId: ADMIN, user: { Id: ADMIN, Name: 'admin', Policy: { IsAdministrator: true } } });
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

async function opened(options) {
    const page = await start(Object.assign({ hash: '#/jfbm-clips' }, options || {}));
    showFallbackPage(page);
    await page.advance(120);
    return page;
}

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

function scopeButtons(page) {
    return root(page).querySelectorAll('.jfbm-clips-scope-button');
}

function ownerSelect(page) {
    return root(page).querySelector('.jfbm-clips-owner-select');
}

function rows(page) {
    return root(page).querySelectorAll('.jfbm-clips-row');
}

function row(page, id) {
    return root(page).querySelector('.jfbm-clips-row[data-jfbm-clip="' + key(id) + '"]');
}

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

function choose(page, select, value) {
    select.value = value;
    page.dispatch(select, 'change');
}

// The page in the "All users" scope, as a click on the switch leaves it.
async function allUsers(options) {
    const page = await opened(options);
    scopeButtons(page)[1].click();
    await page.advance(120);
    return page;
}

test('the switch and the Owner select only for an admin the server allows it; a hash naming the scope for anyone else shows their own clips and is left as it came', { skip }, async () => {
    for (const capabilities of [{ CanManageAllClips: false, IsAdmin: false }, { CanManageAllClips: false, IsAdmin: true }]) {
        const page = await opened({ hash: '#/jfbm-clips?scope=all&user=' + ALICE, capabilities });
        assert.ok(root(page).querySelector('.jfbm-clips-scope') === null, 'no switch');
        assert.ok(ownerSelect(page) === null, 'no Owner select');
        assert.equal(page.fetchLog.filter((f) => f.path.startsWith('/Bookmarks/Admin')).length, 0, 'nothing asked of the admin routes');
        assert.deepEqual(requests(page, '/Bookmarks/Clips').map((f) => f.query.userId), [undefined], 'the own list, without an owner');
        assert.deepEqual(rows(page).map((r) => r.getAttribute('data-jfbm-clip')), ['x1']);
        assert.equal(page.context.location.hash, '#/jfbm-clips?scope=all&user=' + ALICE, 'the hash as it came');
        assert.equal(page.errors.length, 0, page.errors.join('\n'));
    }

    const admin = await opened();
    const group = root(admin).querySelector('.jfbm-clips-scope');
    assert.equal(group.getAttribute('role'), 'group');
    assert.equal(group.getAttribute('aria-label'), 'Whose clips');
    assert.deepEqual(scopeButtons(admin).map((b) => [b.textContent, b.getAttribute('aria-pressed'), b.tagName]), [['My clips', 'true', 'BUTTON'], ['All users', 'false', 'BUTTON']]);
    const scopeBar = root(admin).querySelector('.jfbm-clips-scope-bar');
    assert.ok(!!scopeBar && scopeBar.contains(group) && scopeBar.contains(ownerSelect(admin)), 'a line of its own');
    assert.ok(!root(admin).querySelector('.jfbm-clips-toolbar').contains(group), 'the toolbar the same for everyone');
    assert.equal(ownerSelect(admin).classList.contains('hide'), true, 'the Owner select only in "All users"');
    assert.equal(ownerSelect(admin).getAttribute('aria-label'), 'Owner');
    assert.equal(admin.fetchLog.filter((f) => f.path.startsWith('/Bookmarks/Admin')).length, 0, 'the own scope asks no admin route');
    assert.ok(rows(admin)[0].querySelector('.jfbm-clip-owner') === null, 'the own rows name no owner');
    assert.ok(titled(rows(admin)[0], 'Edit bookmark'), 'the own rows keep the owner\'s actions');
    assert.equal(admin.errors.length, 0, admin.errors.join('\n'));
});

test('"All users": the admin routes, every user\'s clips with the owner (bidi-isolated) and the path, never a note; the summary of every user; the Owner select in the hash; Back and a deep link', { skip }, async () => {
    const page = await allUsers();
    assert.equal(page.context.location.hash, '#/jfbm-clips?scope=all');
    assert.deepEqual(scopeButtons(page).map((b) => b.getAttribute('aria-pressed')), ['false', 'true']);
    assert.ok(root(page).querySelector('.jfbm-clips-page').classList.contains('jfbm-clips-all-users'));
    const read = lastRequest(page, '/Bookmarks/Admin/Clips');
    assert.deepEqual([read.query.sortBy, read.query.startIndex, read.query.limit, read.query.userId], ['Newest', '0', '50', undefined]);
    assert.ok(requests(page, '/Bookmarks/Admin/Clips/Summary').length > 0);
    assert.deepEqual(rows(page).map((r) => r.getAttribute('data-jfbm-clip')), ['m1', 'a1', 'a2', 'b1', 'b2', 'b3', 'a3', 'x1']);

    const a1 = row(page, 'a1');
    const owner = a1.querySelector('.jfbm-clip-owner bdi');
    assert.deepEqual([owner.textContent, owner.getAttribute('dir')], ['alice', 'auto'], 'the name isolated, its direction its own');
    assert.equal(a1.getAttribute('data-jfbm-owner'), ALICE);
    assert.ok(row(page, 'b1').querySelector('.jfbm-clip-owner').classList.contains('jfbm-clip-owner-deleted'));
    assert.equal(row(page, 'b1').querySelector('.jfbm-clip-owner bdi').textContent, 'Deleted user (' + BOB + ')');
    const path = a1.querySelector('.jfbm-clip-path code');
    assert.deepEqual([path.textContent, path.getAttribute('dir')], ['C:\\Jellyfin\\data\\bookmarks\\clips\\alice\\Heat (1995) - a1 [00.01.00-00.01.04].mkv', 'ltr']);
    assert.ok(titled(a1.querySelector('.jfbm-clip-path'), 'Copy the path'));
    assert.ok(row(page, 'm1').querySelector('.jfbm-clip-path') === null, 'no path for a clip without a file');
    assert.doesNotMatch(root(page).textContent, /my own note/, 'the note of the own list is gone with it');

    // The summary: every user's totals; the Owner select's users from it.
    assert.equal(root(page).querySelector('.jfbm-clips-summary').textContent, 'All users · 8 clips · 7.2 MB · 3 users');
    const select = ownerSelect(page);
    assert.equal(select.classList.contains('hide'), false);
    assert.deepEqual(select.querySelectorAll('option').map((o) => [o.value || '', o.textContent]),
        [['', 'All owners'], [ADMIN, 'admin (1)'], [ALICE, 'alice (4)'], [BOB, 'Deleted user (' + BOB + ') (3)']]);

    choose(page, select, ALICE);
    await page.advance(120);
    assert.equal(page.context.location.hash, '#/jfbm-clips?scope=all&user=' + ALICE);
    assert.equal(lastRequest(page, '/Bookmarks/Admin/Clips').query.userId, ALICE);
    assert.deepEqual(rows(page).map((r) => r.getAttribute('data-jfbm-clip')), ['m1', 'a1', 'a2', 'a3']);
    assert.equal(root(page).querySelector('.jfbm-clips-summary .jfbm-clips-totals').textContent, 'alice · 4 clips · 3.6 MB');
    assert.equal(root(page).querySelector('.jfbm-clips-summary .jfbm-clips-matches').textContent, '4 clips match these filters');

    // Back: every user; Back again: the own clips, nothing of the admin routes asked for them.
    await navigate(page, '#/jfbm-clips?scope=all');
    assert.equal(lastRequest(page, '/Bookmarks/Admin/Clips').query.userId, undefined);
    assert.equal(ownerSelect(page).value, '');
    const asked = page.fetchLog.length;
    await navigate(page, '#/jfbm-clips');
    assert.deepEqual(scopeButtons(page).map((b) => b.getAttribute('aria-pressed')), ['true', 'false']);
    assert.equal(page.fetchLog.slice(asked).filter((f) => f.path.startsWith('/Bookmarks/Admin')).length, 0);
    assert.deepEqual(rows(page).map((r) => r.getAttribute('data-jfbm-clip')), ['x1']);

    // A deep link to one owner's clips, and one to a user the summary doesn't count (no clips any more).
    const deep = await opened({ hash: '#/jfbm-clips?scope=all&user=' + BOB + '&state=Ready' });
    assert.deepEqual([lastRequest(deep, '/Bookmarks/Admin/Clips').query.userId, lastRequest(deep, '/Bookmarks/Admin/Clips').query.state], [BOB, 'Ready']);
    assert.equal(ownerSelect(deep).value, BOB);
    assert.deepEqual(rows(deep).map((r) => r.getAttribute('data-jfbm-clip')), ['b1']);
    const none = await opened({ hash: '#/jfbm-clips?scope=all&user=cccc0000000000000000000000000009' });
    assert.equal(ownerSelect(none).value, 'cccc0000000000000000000000000009');
    assert.equal(ownerSelect(none).querySelectorAll('option').slice(-1)[0].textContent, 'A user without clips');
    assert.equal(page.errors.length + deep.errors.length + none.errors.length, 0, page.errors.concat(deep.errors, none.errors).join('\n'));
});

test('the scope\'s actions: Preview and Download through the admin file route, Play, Cancel, Delete, Forget, Dismiss; no Rename, remake or Edit; an offline or held row only Play and no checkbox', { skip }, async () => {
    const page = await allUsers();
    const more = 'More actions';
    assert.deepEqual(rows(page).map((r) => [r.getAttribute('data-jfbm-clip'), actionTitles(r)]), [
        ['m1', [PLAY, 'Cancel the clip', more]],
        ['a1', ['Preview', 'Download', PLAY, 'Delete clip', more]],
        ['a2', [PLAY, 'Forget clip', more]],
        ['b1', ['Preview', 'Download', PLAY, 'Delete clip', more]],
        ['b2', [PLAY, 'Dismiss', more]],
        ['b3', [PLAY, more]],
        ['a3', [PLAY, more]],
        ['x1', ['Preview', 'Download', 'Delete clip', more]]
    ]);
    assert.deepEqual(rows(page).filter((r) => !r.querySelector('.jfbm-row-select-box')).map((r) => r.getAttribute('data-jfbm-clip')), ['b3', 'a3']);

    const clicked = [];
    page.document.addEventListener('click', (event) => {
        if (event.target.tagName === 'A' && event.target.hasAttribute('download')) {
            clicked.push(event.target.getAttribute('href'));
        }
    });
    titled(row(page, 'b1'), 'Download').click();
    assert.deepEqual(clicked, ['http://127.0.0.1:8097/Bookmarks/Admin/Clips/b1/File?ApiKey=test-token&download=true']);
    titled(row(page, 'b1'), 'Preview').click();
    await page.advance(20);
    const preview = panel(page, 'Kept');
    assert.equal(preview.querySelector('video').getAttribute('src'), 'http://127.0.0.1:8097/Bookmarks/Admin/Clips/b1/File?ApiKey=test-token&download=false');
    assert.equal(preview.querySelector('a.jfbm-preview-download').getAttribute('href'), 'http://127.0.0.1:8097/Bookmarks/Admin/Clips/b1/File?ApiKey=test-token&download=true');
    dialogButton(preview, 'Close').click();

    // Without the download permission, no Preview or Download; read-only data, nothing that writes.
    const noDownload = await allUsers({ capabilities: { CanDownloadClips: false } });
    assert.deepEqual(actionTitles(row(noDownload, 'a1')), [PLAY, 'Delete clip', more]);
    const readOnly = await allUsers({ capabilities: { StoreReadOnly: true, StoreReadOnlyReason: 'NewerVersion' } });
    assert.deepEqual(actionTitles(row(readOnly, 'a1')), ['Preview', 'Download', PLAY, more]);
    assert.deepEqual(actionTitles(row(readOnly, 'm1')), [PLAY, more]);
    assert.equal(page.errors.length + noDownload.errors.length + readOnly.errors.length, 0, page.errors.join('\n'));
});

test('Delete of another user\'s clip names whose it is, starts on Cancel, and asks DELETE /Bookmarks/Admin/Clips/{id}; the server\'s refusal stays in the dialog; then the row goes. Forget and Dismiss never delete a file', { skip }, async () => {
    const page = await allUsers();
    page.respond('DELETE', '/Bookmarks/Admin/Clips/a1', problem(409, 'The clip file is in use (being played or downloaded); try again shortly.'), 409, PROBLEM);
    const opener = titled(row(page, 'a1'), 'Delete clip');
    opener.focus();
    opener.click();
    await page.advance(20);
    const dialog = panel(page, 'Delete clip?');
    assert.equal(dialog.querySelector('.jfbm-clip-dialog-text').textContent, 'Delete \u2068alice\u2069\'s clip file "\u2068Heat (1995) - a1 [00.01.00-00.01.04].mkv\u2069" (1.2 MB)? Their bookmark stays.',
        'the owner\'s name and the file name isolated (U+2068, U+2069)');
    assert.ok(page.document.activeElement === dialogButton(dialog, 'Cancel'), 'the focus on Cancel');
    const remove = dialogButton(dialog, 'Delete');
    remove.focus();
    remove.click();
    remove.blur();
    await page.advance(50);
    assert.equal(dialog.querySelector('.jfbm-error').textContent, 'The clip file is in use (being played or downloaded); try again shortly.');
    assert.ok(page.document.activeElement === remove, 'back on Delete for another try');
    assert.ok(row(page, 'a1'), 'the row as it was');

    page.respond('DELETE', '/Bookmarks/Admin/Clips/a1', () => {
        page.data.clips = page.data.clips.filter((c) => c.Id !== 'a1');
        return null;
    }, 204);
    remove.click();
    await page.advance(50);
    assert.ok(panel(page, 'Delete clip?') === null);
    assert.ok(row(page, 'a1') === null);
    assert.deepEqual(toasts(page).slice(-1), ['Clip deleted.']);
    assert.deepEqual(page.fetchLog.filter((f) => f.method === 'DELETE').map((f) => [f.path, f.query]), [['/Bookmarks/Admin/Clips/a1', {}], ['/Bookmarks/Admin/Clips/a1', {}]]);
    assert.equal(root(page).querySelector('.jfbm-clips-summary').textContent, 'All users · 7 clips · 6.0 MB · 3 users', 'the summary read again');

    // A deleted bookmark's kept clip of a deleted user.
    titled(row(page, 'b1'), 'Delete clip').click();
    await page.advance(20);
    assert.equal(panel(page, 'Delete clip?').querySelector('.jfbm-clip-dialog-text').textContent, 'Delete this deleted user\'s clip "\u2068Kept\u2069"? Their bookmark was already deleted, so it disappears from their list.');
    dialogButton(panel(page, 'Delete clip?'), 'Cancel').click();

    // Forget: onlyIfMissing, starting on Cancel; the file there after all is the server's 409, the row read again.
    page.respond('DELETE', '/Bookmarks/Admin/Clips/a2', problem(409, 'The clip file is there after all (made again or put back), so nothing was deleted. Reload to see it.'), 409, PROBLEM);
    titled(row(page, 'a2'), 'Forget clip').click();
    await page.advance(20);
    const forget = panel(page, 'Forget clip?');
    assert.equal(forget.querySelector('.jfbm-clip-dialog-text').textContent, 'Remove \u2068alice\u2069\'s entry for this clip? Its file is already gone. Their bookmark stays.');
    assert.ok(page.document.activeElement === dialogButton(forget, 'Cancel'));
    const reads = requests(page, '/Bookmarks/Admin/Clips').length;
    dialogButton(forget, 'Forget').click();
    await page.advance(100);
    assert.ok(panel(page, 'Forget clip?') === null);
    assert.deepEqual(lastRequest(page, '/Bookmarks/Admin/Clips/a2').query, { onlyIfMissing: 'true' });
    assert.deepEqual(toasts(page).slice(-1), ['The clip file is there after all (made again or put back), so nothing was deleted. Reload to see it.']);
    assert.ok(requests(page, '/Bookmarks/Admin/Clips').slice(reads).some((f) => f.query.itemId === HEAT), 'the row read again with its video');

    // Dismiss a failed clip without a file, without asking.
    page.respond('DELETE', '/Bookmarks/Admin/Clips/b2', () => {
        page.data.clips = page.data.clips.filter((c) => c.Id !== 'b2');
        return null;
    }, 204);
    titled(row(page, 'b2'), 'Dismiss').click();
    await page.advance(50);
    assert.deepEqual(lastRequest(page, '/Bookmarks/Admin/Clips/b2').query, { onlyIfMissing: 'true' });
    assert.ok(row(page, 'b2') === null);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Cancel of another user\'s clip asks the admins\' route; the poll in the scope asks the admins\' status, and the clip that ended is read again from the admins\' list', { skip }, async () => {
    const page = await allUsers();
    page.respond('POST', '/Bookmarks/Admin/Clips/m1/Cancel', () => {
        page.data.clips = page.data.clips.map((c) => c.Id === 'm1' ? adminClip('m1', ALICE, { Name: 'Making' }, { Status: 'Cancelled', State: 'Failed', FileName: null, FileSize: null, Container: null, FileExists: false, RootAvailable: null, MadeAt: null }) : c);
        return page.data.clips.find((c) => c.Id === 'm1');
    });
    await page.advance(2600);
    const polls = requests(page, '/Bookmarks/Admin/Clips/Status');
    assert.ok(polls.length >= 1, 'the admins\' status polled');
    assert.equal(polls[0].query.ids, 'm1');
    assert.equal(requests(page, '/Bookmarks/Clips/Status').length, 0, 'never the owner\'s status in the scope');

    titled(row(page, 'm1'), 'Cancel the clip').click();
    await page.advance(100);
    assert.equal(requests(page, '/Bookmarks/Admin/Clips/m1/Cancel').length, 1);
    assert.equal(requests(page, '/Bookmarks/m1/Clip/Cancel').length, 0);
    assert.equal(row(page, 'm1').getAttribute('data-jfbm-state'), 'Failed');
    assert.deepEqual(toasts(page).slice(-1), ['Clip cancelled.']);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the bulk delete in the scope through POST /Bookmarks/Admin/Clips/BulkDelete: the dialog says whose clips they are, starts on Cancel; the outcome counted per reason, the kept ones still selected; a change of scope drops the selection', { skip }, async () => {
    const page = await allUsers();
    ['a1', 'b1', 'm1'].forEach((id) => {
        const box = row(page, id).querySelector('.jfbm-row-select-box');
        box.checked = true;
        page.dispatch(box, 'change');
    });
    const bulk = root(page).querySelector('.jfbm-bulk-bar');
    assert.equal(bulk.querySelector('.jfbm-bulk-count').textContent, '3 clips selected');
    bulk.querySelector('.jfbm-bulk-delete').click();
    await page.advance(20);
    const dialog = panel(page, 'Delete 3 clips?');
    assert.equal(dialog.querySelector('.jfbm-bulk-description').textContent,
        '2 clip files (2.4 MB) will be deleted. 1 clip being made is kept; cancel it first. Their bookmarks stay; the clips of deleted bookmarks disappear from this list. They belong to 2 users.');
    assert.ok(page.document.activeElement === dialogButton(dialog, 'Cancel'));
    page.respond('POST', '/Bookmarks/Admin/Clips/BulkDelete', (entry) => {
        page.data.clips = page.data.clips.filter((c) => c.Id !== 'a1');
        return { FilesDeleted: ['a1'], RecordsRemoved: [], NotFound: [], InProgress: ['m1'], InUse: ['b1'], FolderUnavailable: [], Failed: [] };
    });
    dialogButton(dialog, 'Delete').click();
    await page.advance(300);
    assert.deepEqual(lastRequest(page, '/Bookmarks/Admin/Clips/BulkDelete').body, { Ids: ['a1', 'b1', 'm1'] });
    assert.equal(requests(page, '/Bookmarks/Clips/BulkDelete').length, 0);
    assert.equal(dialog.querySelector('.jfbm-error').textContent, '1 clip deleted. One could not be deleted: a clip file is in use (being played or downloaded). Try again shortly. One could not be deleted: its clip is being made. Cancel it, or wait for it, first.');
    assert.ok(row(page, 'a1') === null);
    assert.equal(bulk.querySelector('.jfbm-bulk-count').textContent, '2 clips selected');
    dialogButton(dialog, 'Cancel').click();

    // One owner's clips: whose they are, the name isolated.
    root(page).querySelectorAll('.jfbm-row-select-box').forEach((box) => {
        box.checked = false;
        page.dispatch(box, 'change');
    });
    ['a2'].forEach((id) => {
        const box = row(page, id).querySelector('.jfbm-row-select-box');
        box.checked = true;
        page.dispatch(box, 'change');
    });
    bulk.querySelector('.jfbm-bulk-delete').click();
    await page.advance(20);
    assert.match(panel(page, 'Delete 1 clip?').querySelector('.jfbm-bulk-description').textContent, / It is \u2068alice\u2069's clip\.$/);
    dialogButton(panel(page, 'Delete 1 clip?'), 'Cancel').click();

    // Back to the own clips: the selection was the other scope's.
    scopeButtons(page)[0].click();
    await page.advance(120);
    assert.equal(bulk.classList.contains('hide'), true, 'nothing selected');
    assert.equal(page.context.location.hash, '#/jfbm-clips');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the TV layout: the switch is two focusable buttons the D-pad moves between, the Owner select a native select, and the body carries jfbm-page-tv', { skip }, async () => {
    const page = await opened({ layout: 'tv' });
    assert.ok(root(page).querySelector('.jfbm-clips-page').classList.contains('jfbm-page-tv'));
    const [mine, all] = scopeButtons(page);
    mine.focus();
    assert.ok(page.document.activeElement === mine);
    all.focus();
    all.click();
    await page.advance(120);
    assert.equal(all.getAttribute('aria-pressed'), 'true');
    assert.equal(ownerSelect(page).tagName, 'SELECT');
    assert.equal(ownerSelect(page).classList.contains('hide'), false);

    // The path as text, without Copy (no clipboard on a TV, and one D-pad stop fewer per row).
    const path = row(page, 'a1').querySelector('.jfbm-clip-path');
    assert.equal(path.querySelector('code').textContent, 'C:\\Jellyfin\\data\\bookmarks\\clips\\alice\\Heat (1995) - a1 [00.01.00-00.01.04].mkv');
    assert.equal(path.querySelectorAll('button').length, 0, 'no Copy on the TV layout');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the Owner select\'s options are drawn again only when its choices change, not at every summary read again', { skip }, async () => {
    const page = await allUsers();
    const select = ownerSelect(page);
    const before = select.querySelectorAll('option');
    choose(page, select, ALICE);
    await page.advance(120);
    const same = select.querySelectorAll('option');
    assert.equal(same.length, before.length);
    assert.ok(same.every((option, i) => option === before[i]), 'the same option nodes after a load with the same users and counts');
    assert.equal(select.value, ALICE);

    // A clip of alice's gone (a count changes): the options are drawn again, the choice kept.
    page.respond('DELETE', '/Bookmarks/Admin/Clips/a1', () => {
        page.data.clips = page.data.clips.filter((c) => c.Id !== 'a1');
        return null;
    }, 204);
    titled(row(page, 'a1'), 'Delete clip').click();
    await page.advance(20);
    dialogButton(panel(page, 'Delete clip?'), 'Delete').click();
    await page.advance(120);
    const after = select.querySelectorAll('option');
    assert.ok(after[0] !== before[0], 'drawn again');
    assert.equal(after.find((o) => o.value === ALICE).textContent, 'alice (3)');
    assert.equal(select.value, ALICE);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('"Name changed" in the scope names the owner\'s action, not one the admin has there', { skip }, async () => {
    const page = await allUsers({ clips: [adminClip('a1', ALICE, { Name: 'Bank job' }, { IsNameOutdated: true })] });
    const badge = row(page, 'a1').querySelectorAll('.jfbm-badge').find((b) => b.textContent === 'Name changed');
    assert.equal(badge.getAttribute('title'),
        'The clip file\'s name isn\'t the one Bookmarks gives this clip now (the bookmark was renamed, or the name holds words of its note from an earlier version). Its owner can rename the file on their My Clips page (Rename file).');
    assert.ok(titled(row(page, 'a1'), 'Rename file') === null, 'no Rename file of another user\'s clip');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('an admin route\'s 403 (the setting turned off while the page is open): the capabilities read again, the page drawn again in "My clips" with the hash replaced, one toast, and the poll stops asking the admins\' routes', { skip }, async () => {
    const page = await allUsers();
    ['a1', 'b1'].forEach((id) => {
        const box = row(page, id).querySelector('.jfbm-row-select-box');
        box.checked = true;
        page.dispatch(box, 'change');
    });
    const capabilitiesReads = requests(page, '/Bookmarks/Capabilities').length;
    const historyLength = page.context.history.length;
    const turnedOff = problem(403, 'Admins\' access to other users\' clips is turned off in the Bookmarks settings ("Admins can see and manage every user\'s clips").');
    ['/Bookmarks/Admin/Clips', '/Bookmarks/Admin/Clips/Summary', '/Bookmarks/Admin/Clips/Status'].forEach((path) => page.respond('GET', path, turnedOff, 403, PROBLEM));
    page.respond('GET', '/Bookmarks/Capabilities', Object.assign({}, CAPABILITIES, { CanManageAllClips: false }));

    // The poll of m1 (being made) is the first to hear it.
    await page.advance(2600);
    await page.advance(200);
    assert.ok(requests(page, '/Bookmarks/Admin/Clips/Status').length >= 1, 'the poll asked');
    assert.ok(requests(page, '/Bookmarks/Capabilities').length > capabilitiesReads, 'the capabilities read again');
    assert.equal(page.context.location.hash, '#/jfbm-clips', 'the scope and its owner out of the hash');
    assert.equal(page.context.history.length, historyLength, 'replaced, not pushed: Back doesn\'t return to the scope');
    assert.ok(root(page).querySelector('.jfbm-clips-scope') === null, 'no switch now');
    assert.ok(ownerSelect(page) === null, 'no Owner select');
    assert.equal(root(page).querySelector('.jfbm-clips-page').classList.contains('jfbm-clips-all-users'), false);
    assert.equal(root(page).querySelectorAll('.jfbm-clips-page').length, 1, 'one page, drawn again');
    assert.deepEqual(rows(page).map((r) => r.getAttribute('data-jfbm-clip')), ['x1'], 'the own clips');
    assert.equal(root(page).querySelector('.jfbm-bulk-bar').classList.contains('hide'), true, 'the selection of the scope dropped');
    assert.deepEqual(toasts(page).filter((t) => /turned off/.test(t)), ['Admins\' access to other users\' clips is turned off now, so the page shows your own clips.'], 'one toast');
    assert.ok(root(page).querySelector('.jfbm-error') === null, 'no error notice in the list');

    // Nothing more of the admins' routes, however long the page stays.
    const asked = page.fetchLog.filter((f) => f.path.startsWith('/Bookmarks/Admin')).length;
    await page.advance(8000);
    assert.equal(page.fetchLog.filter((f) => f.path.startsWith('/Bookmarks/Admin')).length, asked, 'no admin route asked any more');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('an admin action\'s 403 closes its dialog and draws the page again in "My clips"; a refusal of another kind stays in the dialog', { skip }, async () => {
    const page = await allUsers();
    const turnedOff = problem(403, 'Admins\' access to other users\' clips is turned off in the Bookmarks settings ("Admins can see and manage every user\'s clips").');
    page.respond('DELETE', '/Bookmarks/Admin/Clips/a1', turnedOff, 403, PROBLEM);
    page.respond('GET', '/Bookmarks/Capabilities', Object.assign({}, CAPABILITIES, { CanManageAllClips: false }));
    titled(row(page, 'a1'), 'Delete clip').click();
    await page.advance(20);
    dialogButton(panel(page, 'Delete clip?'), 'Delete').click();
    await page.advance(200);
    assert.ok(panel(page, 'Delete clip?') === null, 'the dialog closed');
    assert.equal(page.context.location.hash, '#/jfbm-clips');
    assert.deepEqual(rows(page).map((r) => r.getAttribute('data-jfbm-clip')), ['x1']);
    assert.deepEqual(toasts(page).slice(-1), ['Admins\' access to other users\' clips is turned off now, so the page shows your own clips.']);

    // The admin's own scope after the setting is on again: a member's 403 elsewhere (the owner's routes) is no scope's end.
    const own = await opened();
    own.respond('GET', '/Bookmarks/Clips/Summary', problem(403, 'Forbidden'), 403, PROBLEM);
    await navigate(own, '#/jfbm-clips?state=Ready');
    assert.deepEqual(toasts(own).filter((t) => /turned off/.test(t)), [], 'no toast outside the scope');
    assert.equal(page.errors.length + own.errors.length, 0, page.errors.concat(own.errors).join('\n'));
});
