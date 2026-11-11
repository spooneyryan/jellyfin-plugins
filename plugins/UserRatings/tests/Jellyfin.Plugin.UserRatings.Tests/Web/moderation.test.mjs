// User Ratings' Moderation tab: the AdminScript module as the kit builds it from
// Web/admin, imported into the kit's fake page and mounted into a panel, with the admin routes' answers scripted. WebTests
// runs it:
//   KIT_CLIENT_SCRIPT=<the AdminScript module> KIT_PREFIX=jfur node --test plugins/UserRatings/tests/Jellyfin.Plugin.UserRatings.Tests/Web/moderation.test.mjs
// Modules run in Node's own realm, so the fake page's window, document, fetch and timers become globals for each test (each
// test imports its own copy of the module, so nothing of one test's page reaches the next).
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createPage, plain } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');
const folder = skip ? null : fs.mkdtempSync(path.join(os.tmpdir(), 'jfur-moderation-'));
let copies = 0;
test.after(() => {
    if (folder) {
        fs.rmSync(folder, { recursive: true, force: true });
    }
});

const GLOBALS = ['window', 'document', 'fetch', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'requestAnimationFrame', 'MutationObserver', 'CSS', 'localStorage', 'location', 'history', 'Event', 'CustomEvent', 'KeyboardEvent'];
const saved = {};
for (const name of GLOBALS) {
    saved[name] = Object.getOwnPropertyDescriptor(globalThis, name);
}

test.afterEach(() => {
    for (const name of GLOBALS) {
        if (saved[name]) {
            Object.defineProperty(globalThis, name, saved[name]);
        } else {
            delete globalThis[name];
        }
    }
});

const CHANGED = 'Changed: this rating changed since the dry run, so nothing was removed. Run the dry run again to see what it removes now.';

function entry(n, over) {
    return Object.assign({
        RatingId: 'r' + n, UserId: 'u' + n, UserName: 'member ' + n, AuthorKind: 'Member', IsHiddenUser: false, IsDisabledUser: false,
        ItemId: 'i' + n, ItemName: 'Title ' + n, ItemKind: 'Movie', ProductionYear: 2000 + n, Stars: 3.5, Text: 'Review ' + n, IsSpoiler: false,
        CreatedUtc: '2026-10-20T10:00:00.000Z', UpdatedUtc: '2026-10-20T10:00:00.000Z', Edited: false, State: 'Attached'
    }, over || {});
}

function logEntry(n, over) {
    return Object.assign({ Id: n, AtUtc: '2026-10-30T10:00:00.000Z', ActorUserId: 'a1', ActorName: 'admin', Action: 'RemoveText', RatingId: 'r' + n, TargetUserId: 'u' + n, TargetName: 'member ' + n, ItemId: 'i' + n, ItemName: 'Title ' + n, Reason: 'Spoiler', Details: '{"Stars":0,"Reviews":1,"Deleted":0}' }, over || {});
}

// The fake page as the dashboard has it, the module imported (its own copy) and mounted into a panel; options.rows answers
// GET Admin/Reviews (an array, or a function of the request), options.log GET Admin/ModerationLog. The first reads go once
// the page's timer runs.
async function open(options) {
    options = options || {};
    const page = createPage();
    if (options.layout) {
        page.document.documentElement.classList.add('layout-' + options.layout);
    }

    for (const name of GLOBALS) {
        Object.defineProperty(globalThis, name, { value: page.context[name] === undefined && name === 'window' ? page.context : page.context[name], writable: true, configurable: true });
    }

    globalThis.window = page.context;
    globalThis.document = page.document;
    page.installApiClient({ userId: 'admin-1', admin: true });
    const rows = options.rows === undefined ? [entry(1), entry(2)] : options.rows;
    page.respond('GET', '/UserRatings/Admin/Reviews', (e) => {
        const list = typeof rows === 'function' ? rows(e) : rows;
        const start = Number(e.query.startIndex || 0);
        const limit = Number(e.query.limit || 50);
        return Array.isArray(list) ? { Items: list.slice(start, start + limit), TotalRecordCount: list.length, StartIndex: start } : list;
    }, options.rowsStatus);
    const log = options.log === undefined ? [logEntry(1)] : options.log;
    page.respond('GET', '/UserRatings/Admin/ModerationLog', (e) => {
        const list = typeof log === 'function' ? log(e) : log;
        const start = Number(e.query.startIndex || 0);
        const limit = Number(e.query.limit || 25);
        return { Items: list.slice(start, start + limit), TotalRecordCount: list.length, StartIndex: start };
    });
    const copy = path.join(folder, 'admin-' + (++copies) + '.mjs');
    fs.writeFileSync(copy, source);
    const module = await import(pathToFileURL(copy).href);
    const view = page.element('div', { id: 'UserRatingsModerationRoot' });
    page.document.body.append(view);
    const mounted = module.mount(view, { page: null });
    const before = page.fetchLog.length;
    await page.advance(10);
    const q = (selector) => page.document.querySelector(selector);
    const all = (selector) => page.document.querySelectorAll(selector);
    return { page, view, module, mounted, before, q, all };
}

const text = (node) => (node ? node.textContent : null);
const shown = (node) => !!node && node.getClientRects().length > 0;
const calls = (page, method, re) => page.fetchLog.filter((e) => e.method === method && re.test(e.path));

// The moderation route as the server answers it: a dry run unless dryRun=false, its token for the row and the
// removal; server.stale makes the next real run the 409 Changed; server.status answers every call with it.
function removals(page, opts) {
    opts = opts || {};
    const server = { version: 1, stale: false, status: null, conflict: null, removed: [] };
    page.respond('DELETE', /^\/UserRatings\/Admin\/Reviews\/[^/]+$/, (e) => {
        if (server.conflict && e.query.dryRun === 'false') {
            const conflict = server.conflict;
            server.conflict = null;
            return Object.assign({ title: 'Conflict', status: 409 }, conflict);
        }

        if (server.status) {
            return { title: 'x', status: server.status, detail: 'This rating wasn\'t found, or you can\'t moderate it.' };
        }

        const textOnly = e.query.textOnly !== 'false';
        if (e.query.dryRun === 'false') {
            if (server.stale) {
                server.stale = false;
                server.version++;
                return { title: 'Conflict', status: 409, detail: CHANGED, code: 'Changed' };
            }

            server.removed.push({ id: e.path.split('/').pop(), textOnly, reason: e.query.reason || null, token: e.query.ifPreview });
            if (opts.after) {
                opts.after(e);
            }

            return { DryRun: false, Action: textOnly ? 'RemoveText' : 'RemoveEntry', PreviewToken: e.query.ifPreview, LogId: 9 };
        }

        return { DryRun: true, Action: textOnly ? 'RemoveText' : 'RemoveEntry', RemovesStars: !textOnly, RemovesReview: true, DeletesRating: !textOnly, ArchiveLines: !!opts.archive, PreviewToken: 'm1-' + (textOnly ? 'aaaa' : 'bbbb') + String(server.version).padStart(16, '0') };
    }, (e) => (server.conflict && e.query.dryRun === 'false' ? 409 : server.status ? server.status : (e.query.dryRun === 'false' && server.stale ? 409 : 200)));
    return server;
}

test('importing adds nothing and sends nothing; mount builds the tab, and the first reads go after it returns: the first 50 rows and the log\'s first 25', { skip }, async () => {
    const t = await open();
    assert.equal(t.before, 0, 'nothing sent by import and mount');
    assert.equal(typeof t.mounted.refresh, 'function');
    assert.ok(t.page.document.getElementById('jfur-admin-styles'), 'the admin styles');
    const reads = calls(t.page, 'GET', /Admin\/Reviews$/);
    assert.equal(reads.length, 1);
    assert.deepEqual(plain(reads[0].query), { startIndex: '0', limit: '50' });
    assert.deepEqual(plain(calls(t.page, 'GET', /ModerationLog$/)[0].query), { startIndex: '0', limit: '25' });
    assert.deepEqual(plain(t.all('.jfur-mod-row').map((r) => r.getAttribute('data-jfur-rating'))), ['r1', 'r2']);
    assert.deepEqual(plain(t.all('.jfur-mod-table thead th').map((th) => th.textContent)).slice(0, 5), ['Member', 'Title', 'Rating', 'Changed', 'Actions']);
    assert.equal(text(t.q('.jfur-mod-row .jfur-mod-title')), 'Title 1 (2001)');
    assert.equal(text(t.q('.jfur-mod-row .jfur-mod-stars')), '3.5 out of 5');
    assert.equal(text(t.q('.jfur-mod-row .jfur-mod-text')), 'Review 1');
    assert.equal(shown(t.q('.jfur-mod-pager')), false, 'one page: no pager');
    t.mounted.refresh();
    await t.page.advance(10);
    assert.equal(calls(t.page, 'GET', /Admin\/Reviews$/).length, 2, 'refresh reads again');
    assert.equal(t.page.errors.length, 0);
});

test('every value is text and every name and title isolated: markup in a review or a name stays text, RTL and long names in their own element; the authors\' kinds and flags, a detached row', { skip }, async () => {
    const long = 'A'.repeat(300);
    const t = await open({ rows: [
        entry(1, { UserName: '<img src=x onerror=alert(1)>', Text: '<b>bold</b> <script>alert(1)</script>' }),
        entry(2, { UserName: 'שלום‮ evil', ItemName: long, IsHiddenUser: true, IsDisabledUser: true }),
        entry(3, { UserName: 'Former name', AuthorKind: 'FormerMember', Stars: null, Text: null }),
        entry(4, { UserName: null, AuthorKind: 'Orphan' }),
        entry(5, { State: 'Detached', DetachReason: 'Missing', SeriesName: 'Show', SeasonNumber: 2, EpisodeNumber: 3, ItemName: 'Pilot' }),
        entry(6, { ItemName: 'Heat (2006) [tmdbid-949]' })
    ] });
    assert.equal(t.view.querySelectorAll('img').length, 0);
    assert.equal(t.view.querySelectorAll('b').length + t.view.querySelectorAll('script').length, 0);
    const rows = t.all('.jfur-mod-row');
    assert.equal(text(rows[0].querySelector('.jfur-mod-name')), '<img src=x onerror=alert(1)>');
    assert.equal(text(rows[0].querySelector('.jfur-mod-text')), '<b>bold</b> <script>alert(1)</script>');
    for (const node of t.view.querySelectorAll('.jfur-mod-name').concat(t.view.querySelectorAll('.jfur-mod-title'), t.view.querySelectorAll('.jfur-mod-text'))) {
        assert.equal(node.getAttribute('dir'), 'auto');
        assert.ok(node.classList.contains('jfur-isolate'));
    }

    assert.equal(text(rows[1].querySelector('.jfur-mod-flags')), ' (hidden, disabled)');
    assert.equal(text(rows[1].querySelector('.jfur-mod-title')), long + ' (2002)');
    assert.equal(text(rows[2].querySelector('.jfur-mod-name')), 'Former name');
    assert.ok(rows[2].querySelector('.jfur-mod-member .jfur-mod-only') === null, 'no "Only" for a former member');
    assert.equal(text(rows[2].querySelector('.jfur-mod-stars')), 'No stars');
    assert.equal(text(rows[2].querySelector('.jfur-mod-none')), 'No review');
    assert.equal(text(rows[3].querySelector('.jfur-mod-name')), 'Unknown user');
    assert.equal(text(rows[4].querySelector('.jfur-mod-title')), 'Show S2E3 · Pilot');
    assert.equal(text(rows[5].querySelector('.jfur-mod-title')), 'Heat (2006) [tmdbid-949]', 'a name with its year has it once');
    assert.equal(text(rows[4].querySelector('.jfur-mod-detached')), ' Detached (missing from the library)');
    assert.equal(rows[4].querySelectorAll('.jfur-mod-only').length, 1, 'a detached row: only its member');
    assert.equal(rows[1].querySelector('.jfur-mod-remove').getAttribute('aria-label'), 'Remove the rating of שלום‮ evil on ' + long + ' (2002)…');
    assert.equal(t.page.errors.length, 0);
});

test('the filters: search (Enter), shown, review, changed since, a member\'s and a title\'s "Only" with their chips, Clear filters; each from the first page', { skip }, async () => {
    const t = await open({ rows: (e) => [entry(1), entry(2)].map((r) => Object.assign({}, r, { Text: JSON.stringify(e.query) })) });
    const last = () => plain(calls(t.page, 'GET', /Admin\/Reviews$/).pop().query);
    const search = t.q('.jfur-mod-search input');
    assert.equal(search.getAttribute('maxlength'), '200');
    search.value = '  heist  ';
    t.page.key(search, 'Enter');
    await t.page.advance(10);
    assert.deepEqual(last(), { startIndex: '0', limit: '50', searchTerm: 'heist' });
    const state = t.q('.jfur-mod-state');
    state.value = 'Detached';
    t.page.dispatch(state, 'change');
    await t.page.advance(10);
    assert.equal(last().state, 'Detached');
    const hasText = t.q('.jfur-mod-hastext');
    hasText.value = 'false';
    t.page.dispatch(hasText, 'change');
    await t.page.advance(10);
    assert.equal(last().hasText, 'false');
    const since = t.q('.jfur-mod-since');
    since.value = '2026-10-01';
    t.page.dispatch(since, 'change');
    await t.page.advance(10);
    assert.equal(last().since, new Date('2026-10-01T00:00:00').toISOString(), 'local midnight of the day');

    const only = t.q('[data-jfur-rating="r2"] .jfur-mod-member .jfur-mod-only');
    assert.equal(text(only), 'Only member 2');
    only.focus();
    only.click();
    await t.page.advance(10);
    assert.equal(last().userId, 'u2');
    const chips = t.all('.jfur-mod-chip');
    assert.equal(chips.length, 1);
    assert.equal(text(chips[0]), 'Member: member 2');
    assert.equal(chips[0].querySelector('.jfur-isolate').getAttribute('dir'), 'auto');
    assert.ok(t.page.document.activeElement === search, 'the focus to the search box, not the page\'s body');
    t.q('[data-jfur-rating="r1"] .jfur-mod-item .jfur-mod-only').click();
    await t.page.advance(10);
    assert.equal(last().itemId, 'i1');
    assert.deepEqual(plain(t.all('.jfur-mod-chip').map((c) => c.textContent)), ['Member: member 2', 'Title: Title 1 (2001)']);
    t.all('.jfur-mod-chip')[0].click();
    await t.page.advance(10);
    assert.equal(last().userId, undefined, 'the member\'s chip gone, its filter too');
    assert.equal(last().itemId, 'i1');

    t.q('.jfur-mod-clear').click();
    await t.page.advance(10);
    assert.deepEqual(last(), { startIndex: '0', limit: '50' });
    assert.equal(search.value, '');
    assert.equal(state.value, '');
    assert.equal(t.all('.jfur-mod-chip').length, 0);
    assert.equal(t.page.errors.length, 0);
});

test('paging: 120 rows are 50 a page, Previous and Next with "1–50 of 120"; the last page; an empty list says so', { skip }, async () => {
    const many = Array.from({ length: 120 }, (_, i) => entry(i + 1));
    const t = await open({ rows: many });
    const count = t.q('.jfur-mod-count');
    assert.equal(text(count), '1–50 of 120');
    assert.equal(shown(t.q('.jfur-mod-pager')), true);
    assert.equal(t.q('.jfur-mod-previous').disabled, true);
    const next = t.q('.jfur-mod-next');
    next.focus();
    next.click();
    await t.page.advance(10);
    assert.equal(text(count), '51–100 of 120');
    next.click();
    await t.page.advance(10);
    assert.equal(text(count), '101–120 of 120');
    assert.equal(next.disabled, true);
    assert.ok(t.page.document.activeElement !== t.page.document.body, 'the focus not lost as Next turns off');
    t.q('.jfur-mod-previous').click();
    await t.page.advance(10);
    assert.equal(text(count), '51–100 of 120');

    const none = await open({ rows: [] });
    assert.equal(text(none.q('.jfur-mod-message')), 'Nobody has rated anything yet.');
    assert.equal(shown(none.q('.jfur-mod-table')), false);
    assert.equal(t.page.errors.length + none.page.errors.length, 0);
});

test('a list that can\'t be read says why, with Try again', { skip }, async () => {
    const t = await open({ rows: { title: 'x', status: 503, detail: 'The database is busy; try again in a moment.' }, rowsStatus: 503 });
    assert.equal(text(t.q('.jfur-mod-message .jfur-notice-text')), 'The ratings couldn\'t be loaded. The database is busy; try again in a moment.');
    t.page.respond('GET', '/UserRatings/Admin/Reviews', { Items: [entry(1)], TotalRecordCount: 1, StartIndex: 0 });
    t.q('.jfur-mod-message button').click();
    await t.page.advance(10);
    assert.equal(t.all('.jfur-mod-row').length, 1);
    assert.equal(shown(t.q('.jfur-mod-message')), false);
});

test('Remove…: the dry run first ("Remove text only" chosen), Cancel focused; the other choice checks again; Remove sends the token and the reason; the list and the log are read again, the focus on the row in its place', { skip }, async () => {
    let list = [entry(1), entry(2), entry(3)];
    const t = await open({ rows: () => list });
    const server = removals(t.page, { archive: true, after: () => { list = [entry(1), entry(3)]; } });
    const remove = t.q('[data-jfur-rating="r2"] .jfur-mod-remove');
    remove.focus();
    remove.click();
    await t.page.advance(10);
    const dialog = t.q('.jfur-panel');
    assert.equal(text(dialog.querySelector('h3')), 'Remove a review');
    assert.equal(text(dialog.querySelector('.jfur-mod-by')), 'By member 2, on Title 2 (2002)');
    const radios = dialog.querySelectorAll('.jfur-mod-radio');
    assert.deepEqual(plain(radios.map((r) => [r.checked, r.disabled])), [[true, false], [false, false]]);
    assert.ok(t.page.document.activeElement === dialog.querySelector('.jfur-mod-cancel'), 'Cancel focused');
    let sent = calls(t.page, 'DELETE', /Admin\/Reviews\/r2$/);
    assert.deepEqual(plain(sent[0].query), { textOnly: 'true' }, 'a dry run');
    assert.equal(text(dialog.querySelector('.jfur-mod-preview')), 'The review goes; the stars stay (3.5 out of 5). Its earlier copies in the archive go too. A snapshot of the ratings database is taken first, kept for 7 days.');
    radios[1].checked = true;
    t.page.dispatch(radios[1], 'change');
    assert.equal(dialog.querySelector('.jfur-mod-confirm').disabled, true, 'Remove waits for the new check');
    dialog.querySelector('.jfur-mod-confirm').click();
    await t.page.advance(10);
    sent = calls(t.page, 'DELETE', /Admin\/Reviews\/r2$/);
    assert.ok(sent.every((c) => plain(c.query).dryRun === undefined), 'a click while it waits removes nothing');
    assert.deepEqual(plain(sent[1].query), { textOnly: 'false' });
    assert.ok(text(dialog.querySelector('.jfur-mod-preview')).startsWith('The rating and the review go.'));
    dialog.querySelector('.jfur-mod-reason').value = 'Spam';
    const logReads = calls(t.page, 'GET', /ModerationLog$/).length;
    dialog.querySelector('.jfur-mod-confirm').click();
    await t.page.advance(10);
    assert.deepEqual(server.removed, [{ id: 'r2', textOnly: false, reason: 'Spam', token: 'm1-bbbb0000000000000001' }]);
    assert.ok(t.q('.jfur-panel') === null);
    assert.deepEqual(plain(t.all('.jfur-mod-row').map((r) => r.getAttribute('data-jfur-rating'))), ['r1', 'r3']);
    assert.equal(calls(t.page, 'GET', /ModerationLog$/).length, logReads + 1, 'the log read again');
    assert.ok(t.page.document.activeElement === t.q('[data-jfur-rating="r3"] .jfur-mod-remove'), 'the focus on the row now in its place');
    assert.deepEqual(plain(t.all('.jfur-toast').map((n) => n.textContent)), ['The rating and the review were removed.']);
    assert.equal(t.page.errors.length, 0);
});

test('Remove… on a rating without a review: "Remove text only" is off and the dry run is the whole rating\'s', { skip }, async () => {
    const t = await open({ rows: [entry(1, { Text: null })] });
    removals(t.page);
    t.q('.jfur-mod-remove').click();
    await t.page.advance(10);
    const radios = t.q('.jfur-panel').querySelectorAll('.jfur-mod-radio');
    assert.deepEqual(plain(radios.map((r) => [r.checked, r.disabled])), [[false, true], [true, false]]);
    assert.equal(text(t.q('.jfur-panel .jfur-mod-choice-hint')), 'This rating has no review.');
    assert.deepEqual(plain(calls(t.page, 'DELETE', /./)[0].query), { textOnly: 'false' });
});

test('Remove…: a stale token\'s 409 removes nothing, says so, reads the row again and shows its stars and words as they are now, and only then checks again, Cancel focused; the next Remove takes the new token; a 404 says it is gone; Cancel sends nothing and the focus is back on Remove…', { skip }, async () => {
    // The dialog once checked again with the list's stale stars and never showed the words.
    const edited = entry(1, { Stars: 2, Text: 'Edited: <b>new</b> words', UpdatedUtc: '2026-10-21T10:00:00.000Z' });
    const t = await open({ rows: (e) => (e.query.userId ? [edited] : [entry(1), entry(2)]) });
    const server = removals(t.page);
    t.q('.jfur-mod-remove').click();
    await t.page.advance(10);
    const dialog = t.q('.jfur-panel');
    assert.equal(shown(dialog.querySelector('.jfur-mod-now')), false, 'nothing shown before a change');
    server.stale = true;
    const before = t.page.fetchLog.length;
    dialog.querySelector('.jfur-mod-confirm').click();
    await t.page.advance(10);
    assert.equal(server.removed.length, 0);
    assert.ok(t.q('.jfur-panel') === dialog, 'the dialog stays');
    assert.equal(text(dialog.querySelector('.jfur-mod-problem')), 'This rating changed after it was checked, so nothing was removed. It is shown below as it is now; Remove removes what is checked for it now.');
    const after = t.page.fetchLog.slice(before).map((e) => e.method + (e.query.dryRun === 'false' ? ' real' : e.method === 'GET' ? ' ' + (e.query.userId || '') + '/' + (e.query.itemId || '') : ' dry'));
    assert.deepEqual(after, ['DELETE real', 'GET u1/i1', 'DELETE dry'], 'the row read again, then the dry run');
    assert.equal(text(dialog.querySelector('.jfur-mod-now-stars')), 'Now: 2 out of 5');
    const words = dialog.querySelector('.jfur-mod-now-text');
    assert.equal(text(words), 'Edited: <b>new</b> words');
    assert.equal(words.getAttribute('dir'), 'auto');
    assert.equal(dialog.querySelectorAll('.jfur-mod-now b').length, 0, 'the words are text');
    assert.match(text(dialog.querySelector('.jfur-mod-preview')), /\(2 out of 5\)/, 'the preview names the stars as they are now');
    assert.ok(t.page.document.activeElement === dialog.querySelector('.jfur-mod-cancel'), 'Cancel focused');
    dialog.querySelector('.jfur-mod-confirm').click();
    await t.page.advance(10);
    assert.deepEqual(server.removed.map((r) => [r.token, r.reason]), [['m1-aaaa0000000000000002', null]], 'the new token, no reason');

    // The row lost its review meanwhile: "Remove text only" is off, the whole rating's check; one gone says so, unarmed.
    const s = await open({ rows: (e) => (e.query.userId ? [entry(1, { Text: null })] : [entry(1)]) });
    const sServer = removals(s.page);
    s.q('.jfur-mod-remove').click();
    await s.page.advance(10);
    sServer.stale = true;
    s.q('.jfur-panel .jfur-mod-confirm').click();
    await s.page.advance(10);
    const radios = s.all('.jfur-panel .jfur-mod-radio');
    assert.deepEqual([radios[0].disabled, radios[0].checked, radios[1].checked], [true, false, true]);
    assert.equal(text(s.q('.jfur-panel .jfur-mod-now-text')), 'No review now.');
    assert.equal(calls(s.page, 'DELETE', /./).filter((e) => e.query.dryRun === undefined).pop().query.textOnly, 'false');
    const v = await open({ rows: (e) => (e.query.userId ? [] : [entry(1)]) });
    const vServer = removals(v.page);
    v.q('.jfur-mod-remove').click();
    await v.page.advance(10);
    vServer.stale = true;
    v.q('.jfur-panel .jfur-mod-confirm').click();
    await v.page.advance(10);
    assert.equal(text(v.q('.jfur-panel .jfur-mod-problem')), 'This rating was moved or removed after it was checked, so nothing was removed.');
    assert.equal(v.q('.jfur-panel .jfur-mod-confirm').disabled, true);
    assert.equal(calls(v.page, 'DELETE', /./).filter((e) => e.query.dryRun === undefined).length, 1, 'no check of a row that is gone');
    assert.ok(v.page.document.activeElement === v.q('.jfur-panel .jfur-mod-cancel'));
    assert.equal(s.page.errors.length + v.page.errors.length, 0);

    const g = await open();
    const gone = removals(g.page);
    gone.status = 404;
    const button = g.q('.jfur-mod-remove');
    button.focus();
    button.click();
    await g.page.advance(10);
    const d = g.q('.jfur-panel');
    assert.equal(text(d.querySelector('.jfur-mod-problem')), 'This rating was already removed, or you can\'t remove it.');
    assert.equal(d.querySelector('.jfur-mod-confirm').disabled, true);
    d.querySelector('.jfur-mod-cancel').click();
    await g.page.advance(10);
    assert.ok(g.q('.jfur-panel') === null);
    assert.ok(g.page.document.activeElement === button, 'back on Remove…');
    assert.equal(calls(g.page, 'DELETE', /./).filter((e) => e.query.dryRun === 'false').length, 0);
    assert.equal(t.page.errors.length + g.page.errors.length, 0);
});

test('Remove…: the 409 is told apart by its code, never by its words: code Changed reads the row again whatever the words, and a 409 whose words start "Changed" with another code is no reload', { skip }, async () => {
    // The dialog once matched the message against /^Changed/.
    const t = await open({ rows: (e) => (e.query.userId ? [entry(1, { Stars: 3 })] : [entry(1)]) });
    const server = removals(t.page);
    t.q('.jfur-mod-remove').click();
    await t.page.advance(10);
    server.conflict = { detail: 'Some other words.', code: 'Changed' };
    const before = t.page.fetchLog.length;
    t.q('.jfur-panel .jfur-mod-confirm').click();
    await t.page.advance(10);
    assert.deepEqual(t.page.fetchLog.slice(before).map((e) => e.method + (e.query.userId ? ' row' : e.query.dryRun === 'false' ? ' real' : ' dry')), ['DELETE real', 'GET row', 'DELETE dry'], 'the row read again on the code');
    assert.equal(text(t.q('.jfur-panel .jfur-mod-now-stars')), 'Now: 3 out of 5');

    const n = await open({ rows: (e) => (e.query.userId ? [entry(1, { Stars: 3 })] : [entry(1)]) });
    const nServer = removals(n.page);
    n.q('.jfur-mod-remove').click();
    await n.page.advance(10);
    nServer.conflict = { detail: CHANGED, code: 'NoReview' };
    const first = n.page.fetchLog.length;
    n.q('.jfur-panel .jfur-mod-confirm').click();
    await n.page.advance(10);
    assert.deepEqual(n.page.fetchLog.slice(first).map((e) => e.method + (e.query.dryRun === 'false' ? ' real' : '')), ['DELETE real'], 'no reload on the words alone');
    assert.equal(n.page.errors.length + t.page.errors.length, 0);
});

test('the TV layout: the D-pad moves through the dialog without a trap, Back closes it, the focus back on Remove…', { skip }, async () => {
    const t = await open({ layout: 'tv' });
    removals(t.page);
    const remove = t.q('.jfur-mod-remove');
    remove.focus();
    remove.click();
    await t.page.advance(10);
    const dialog = t.q('.jfur-panel');
    const cancel = dialog.querySelector('.jfur-mod-cancel');
    assert.ok(t.page.document.activeElement === cancel);
    t.page.key(cancel, 'ArrowDown');
    assert.ok(t.page.document.activeElement === dialog.querySelector('.jfur-mod-confirm'), 'down: Remove');
    t.page.key(t.page.document.activeElement, 'ArrowDown');
    assert.ok(t.page.document.activeElement === dialog.querySelector('.jfur-mod-confirm'), 'the end: it stays (no wrap)');
    t.page.key(t.page.document.activeElement, 'ArrowUp');
    t.page.key(t.page.document.activeElement, 'ArrowUp');
    assert.ok(t.page.document.activeElement === dialog.querySelector('.jfur-mod-reason'), 'up twice: the reason');
    t.page.key(t.page.document.activeElement, 'GoBack', { keyCode: 461 });
    await t.page.advance(10);
    assert.ok(t.q('.jfur-panel') === null, 'Back closed it');
    assert.ok(t.page.document.activeElement === remove, 'back on Remove…');
    assert.equal(t.page.errors.length, 0);
});

test('Remove… whose button went while the dialog was open (its row built again behind it): the dialog\'s close puts the focus on a row\'s Remove…, else the search box, never the page\'s body', { skip }, async () => {
    const t = await open();
    removals(t.page);
    const remove = t.q('[data-jfur-rating="r1"] .jfur-mod-remove');
    remove.focus();
    remove.click();
    await t.page.advance(10);
    t.q('[data-jfur-rating="r1"]').remove();
    t.q('.jfur-panel .jfur-mod-cancel').click();
    await t.page.advance(10);
    assert.ok(t.q('.jfur-panel') === null);
    assert.ok(t.page.document.activeElement === t.q('[data-jfur-rating="r2"] .jfur-mod-remove'), 'the Remove… of the row left');

    const e = await open({ rows: [entry(1)], layout: 'tv' });
    removals(e.page);
    const only = e.q('.jfur-mod-remove');
    only.focus();
    only.click();
    await e.page.advance(10);
    e.q('.jfur-mod-row').remove();
    e.page.key(e.page.document.activeElement, 'GoBack', { keyCode: 461 });
    await e.page.advance(10);
    assert.ok(e.q('.jfur-panel') === null, 'Back closed it');
    const active = e.page.document.activeElement;
    assert.ok(active !== e.page.document.body && !!active.closest && !!active.closest('.jfur-mod-search'), 'no row left: the search box');
    assert.equal(t.page.errors.length + e.page.errors.length, 0);
});

test('the moderation log: newest first as the server pages it, who (an admin, an API key, the server), what, the member, the title (or that it is no longer in the library), the reason and the counts, all as text; filtered by action; Previous and Next', { skip }, async () => {
    const log = [
        logEntry(3, { Action: 'RemoveEntry', ActorUserId: null, ActorName: null, Reason: 'Offensive', Details: '{"Stars":1,"Reviews":1,"Deleted":1}', ItemName: null }),
        logEntry(2, { ActorName: '<b>root</b>', TargetName: 'שלום' }),
        logEntry(1, { Action: 'UserDeleted', ActorUserId: null, ActorName: null, RatingId: null, ItemId: null, ItemName: null, Reason: null, Details: '{"Ratings":4,"Note":"<i>x</i>"}' })
    ].concat(Array.from({ length: 30 }, (_, i) => logEntry(100 + i)));
    // Rows of the other writers, past the first page: Detach now (the reason the rating was held for), a merged version's
    // relink, and a code from a newer server.
    log.push(
        logEntry(200, { Action: 'Detach', ActorUserId: null, ActorName: null, Reason: 'LibraryFolderUnavailable', Details: '{"HeldSinceUtc":"2026-10-30T10:00:00.000Z","MassDetach":true}' }),
        logEntry(201, { Action: 'Relink', ActorUserId: null, ActorName: null, Reason: 'MergedVersion', Details: null }),
        logEntry(202, { Action: 'SomethingNew', Reason: 'Unheard' }));
    const t = await open({ log: (e) => (e.query.action ? log.filter((l) => l.Action === e.query.action) : log) });
    const rows = t.all('.jfur-mod-log-row');
    assert.equal(rows.length, 25);
    const cells = (row) => plain(row.querySelectorAll('td').map((c) => c.textContent));
    assert.deepEqual(cells(rows[0]).slice(1), ['An API key', 'Removed a rating and review', 'member 3', 'A title no longer in the library', 'Offensive', 'Stars 1, Reviews 1, Deleted 1']);
    assert.deepEqual(cells(rows[1]).slice(1), ['<b>root</b>', 'Removed a review', 'שלום', 'Title 2', 'Spoiler', 'Stars 0, Reviews 1, Deleted 0']);
    assert.deepEqual(cells(rows[2]).slice(1), ['The server', 'A deleted member\'s ratings deleted', 'member 1', '', '', 'Ratings 4']);
    assert.equal(t.view.querySelectorAll('.jfur-mod-log-table b, .jfur-mod-log-table i').length, 0);
    assert.equal(text(t.all('.jfur-mod-count')[1]), '1–25 of 36');
    t.q('.jfur-mod-log-next').click();
    await t.page.advance(10);
    assert.equal(text(t.all('.jfur-mod-count')[1]), '26–36 of 36');
    assert.equal(plain(calls(t.page, 'GET', /ModerationLog$/).pop().query).startIndex, '25');
    const last = t.all('.jfur-mod-log-row').slice(-3);
    assert.deepEqual(cells(last[0]).slice(1, 6), ['An API key', 'Detached a rating kept on a title that is gone', 'member 200', 'Title 200', 'Its library folder was missing, empty or unreadable']);
    assert.deepEqual(cells(last[1]).slice(1, 7), ['The server', 'Relinked', 'member 201', 'Title 201', 'A merged version', '']);
    assert.deepEqual(cells(last[2]).slice(2, 6), ['SomethingNew', 'member 202', 'Title 202', 'Unheard'], 'a newer server\'s codes as it wrote them');
    const action = t.q('.jfur-mod-log-action');
    assert.deepEqual(plain(action.querySelectorAll('option').map((o) => o.value)), ['', 'RemoveText', 'RemoveEntry', 'Relink', 'Archive', 'Purge', 'ImportJe', 'ImportNative', 'ImportEnvelope', 'UserDeleted', 'UserAnonymized', 'UserOrphaned', 'UserRemapped', 'MirrorBackfill', 'RestoreSnapshot', 'Detach', 'StartEmpty'], 'every action the server logs');
    assert.ok(action.querySelectorAll('option').every((o) => o.textContent.indexOf('mod.') < 0), 'each with its words');
    action.value = 'UserDeleted';
    t.page.dispatch(action, 'change');
    await t.page.advance(10);
    assert.deepEqual(plain(calls(t.page, 'GET', /ModerationLog$/).pop().query), { startIndex: '0', limit: '25', action: 'UserDeleted' });
    assert.equal(t.all('.jfur-mod-log-row').length, 1);
    assert.equal(shown(t.q('.jfur-mod-log-table').parentNode.nextSibling), false, 'one page: no pager');
    assert.equal(t.page.errors.length, 0);
});

test('a phone at 375 px: each row a card with its column\'s name, every control at least 44 px high (the admin styles)', { skip }, async () => {
    const t = await open();
    const css = t.page.document.getElementById('jfur-admin-styles').textContent;
    const phone = css.substring(css.indexOf('@media (max-width:599px){'));
    assert.ok(phone.includes('.jfur-mod-table td::before{content:attr(data-label)'), 'the cells carry their column\'s name');
    assert.ok(phone.includes('.jfur-mod button,.jfur-mod select,.jfur-mod input{min-height:44px}'), '44 px controls');
    assert.ok(phone.includes('.jfur-mod-cancel,.jfur-mod-confirm,.jfur-mod-reason{min-height:44px}'), 'the dialog\'s too');
    assert.ok(css.includes('.jfur-mod-choice{display:flex;align-items:flex-start;gap:.6em;min-height:44px'));
    assert.ok(css.includes('@media (max-width:599px),(pointer:coarse){.jfur-panel .jfur-icon-button{min-width:44px;min-height:44px}}'), 'the dialog\'s Close (the kit\'s 40 px icon button) too');
    assert.ok(css.includes('.jfur-isolate{unicode-bidi:isolate}'));
    assert.deepEqual(plain(t.all('.jfur-mod-row')[0].querySelectorAll('td').map((c) => c.getAttribute('data-label'))), ['Member', 'Title', 'Rating', 'Changed', 'Actions']);
});
