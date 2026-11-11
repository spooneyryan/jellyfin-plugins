// User Ratings' Data tab: the AdminScript module as the kit builds it from
// Web/admin, imported into the kit's fake page and mounted twice, as configPage.html does ({ tab: 'Data' } and
// { tab: 'Detached' }), with the admin routes' answers scripted. WebTests runs it:
//   KIT_CLIENT_SCRIPT=<the AdminScript module> KIT_PREFIX=jfur node --test plugins/UserRatings/tests/Jellyfin.Plugin.UserRatings.Tests/Web/data.test.mjs
// Modules run in Node's own realm, so the fake page's window, document, fetch and timers become globals for each test (each
// test imports its own copy of the module, so nothing of one test's page reaches the next).
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createPage } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');
const folder = skip ? null : fs.mkdtempSync(path.join(os.tmpdir(), 'jfur-data-'));
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

const PLAN = '0123456789abcdef0123456789abcdef';
const EXPORT_TEXT = '{ "Plugin": "User Ratings",\n  "Data": { "Ratings": [ { "Id": "r1", "ReviewText": "caf\\u00e9" } ] } }';

function snapshot(name, over) {
    return Object.assign({ Name: name, Kind: 'Daily', Operation: null, Utc: '2026-10-30T03:00:00.000Z', Size: 40960, SchemaVersion: 3, Kept: false }, over || {});
}

function detached(n, over) {
    return Object.assign({
        Id: 'd' + n, UserId: 'u' + n, UserName: 'member ' + n, ItemId: 'gone' + n, Kind: 'Movie', Name: 'Title ' + n, Year: 2000 + n, SeriesName: null, Season: null, Episode: null,
        Stars: 4, HasReview: true, DetachedUtc: '2026-10-29T10:00:00.000Z', DetachReason: 'Removed',
        Candidates: [{ ItemId: 'c' + n, Name: 'Title ' + n, Kind: 'Movie', Year: 2000 + n, SeriesName: null, Strength: 'Strong', MemberRatesIt: false }]
    }, over || {});
}

function importReport(over) {
    return Object.assign({
        DryRun: true, Mode: 'Merge', MapUsersBy: 'Id', SourcePluginVersion: '0.3.18.0', ExportedUtc: '2026-10-31T12:00:00.000Z', SchemaVersion: 1,
        Users: { Mapped: [{ Id: 'u1', NewId: 'u1', Name: 'alice', Records: 2 }], Unknown: [{ Id: 'u9', Name: 'zed', Records: 1, Reason: 'NotFound' }] },
        Records: { Incoming: 3, Added: 1, Updated: 1, Unchanged: 0, KeptNewer: 0, Removed: 0, SkippedUnknownUser: 1, Skipped: 0 },
        Archive: { Records: 0, Imported: 0, Note: null }, Notes: [], SafetySnapshot: null,
        PlanToken: PLAN, Outcomes: { Added: 1, Updated: 1, SkippedUnknownUser: 1 }, Unusable: 0
    }, over || {});
}

// The data jobs as the server runs them: each POST starts one whose answers to GET Admin/Jobs/{id} are server.script's
// steps (partial jobs), one a poll, the last kept.
function jobServer(page) {
    const server = { jobs: {}, posts: [], deletes: [], next: 1, refuse: null, deleteRefuse: null, script: null };
    const dto = (job, step) => Object.assign({ Id: job.id, Kind: job.kind, DryRun: job.dryRun, State: 'Running', Phase: 'Read', Writing: false, Processed: 0, Total: 0, Report: null, Error: null, ErrorCode: null, ErrorStatus: null, HasFile: false }, step);
    const start = (kind) => (e) => {
        if (server.refuse) {
            return server.refuse;
        }

        const id = 'job' + (server.next++);
        const job = { id, kind, dryRun: kind === 'Export' || e.query.dryRun !== 'false', at: 0, steps: server.script(kind, e) };
        server.jobs[id] = job;
        server.posts.push({ kind, query: e.query, body: e.rawBody });
        return dto(job, { State: 'Queued', Phase: null });
    };
    const status = (e) => (server.refuse ? server.refuse.status : 202);
    page.respond('POST', '/UserRatings/Admin/Export', start('Export'), status);
    page.respond('POST', '/UserRatings/Admin/Import', start('Import'), status);
    page.respond('POST', '/UserRatings/Admin/Import/JellyfinEnhanced', start('ImportJellyfinEnhanced'), status);
    page.respond('GET', /^\/UserRatings\/Admin\/Jobs\/[^/]+$/, (e) => {
        const job = server.jobs[e.path.split('/').pop()];
        const step = job.steps[Math.min(job.at, job.steps.length - 1)];
        job.at++;
        return dto(job, typeof step === 'function' ? step(job) : step);
    });
    page.respond('GET', /^\/UserRatings\/Admin\/Jobs\/[^/]+\/File$/, EXPORT_TEXT);
    page.respond('DELETE', /^\/UserRatings\/Admin\/Jobs\/[^/]+$/, (e) => {
        server.deletes.push(e.path.split('/').pop());
        if (server.deleteRefuse) {
            return server.deleteRefuse;
        }

        const job = server.jobs[e.path.split('/').pop()];
        job.steps = [{ State: 'Cancelled', Phase: 'Done' }];
        job.at = 0;
        return null;
    }, () => (server.deleteRefuse ? server.deleteRefuse.status : 204));
    return server;
}

// The fake page as the dashboard has it, the module imported (its own copy) and mounted with { tab } into a panel.
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
    page.respond('GET', '/UserRatings/Admin/Snapshots', options.snapshotsBody || {
        Snapshots: options.snapshots || [snapshot('userratings-20261030.db'), snapshot('userratings-import-20261031T120000Z.db', { Kind: 'PreOp', Operation: 'import', Utc: '2026-10-31T12:00:00.000Z' })],
        Integrity: options.integrity || { Status: 'Ok', ReadOnly: false, Damaged: false, Message: null, LastRecovery: null, Quarantines: [] }
    }, options.snapshotsStatus);
    page.respond('GET', '/UserRatings/Admin/Diagnostics', Object.assign({ Users: options.users || { DeletedUserPolicy: 'KeepAsOrphan', OrphanUsers: [] } }, options.je ? { JellyfinEnhanced: options.je } : {}));
    page.respond('GET', '/UserRatings/Admin/Jobs', options.jobs || []);
    const list = options.detached === undefined ? [detached(1), detached(2)] : options.detached;
    page.respond('GET', '/UserRatings/Admin/Detached', (e) => {
        const rows = (typeof list === 'function' ? list(e) : list).filter((r) => !e.query.reason || r.DetachReason === e.query.reason);
        const start = Number(e.query.startIndex || 0);
        return { Items: rows.slice(start, start + Number(e.query.limit || 50)), TotalRecordCount: rows.length, StartIndex: start };
    });
    const copy = path.join(folder, 'admin-' + (++copies) + '.mjs');
    fs.writeFileSync(copy, source);
    const module = await import(pathToFileURL(copy).href);
    const view = page.element('div', { id: options.tab === 'Detached' ? 'UserRatingsDetachedListRoot' : 'UserRatingsDataRoot' });
    page.document.body.append(view);
    const changes = [];
    page.document.body.addEventListener('jfur-data-changed', () => changes.push(page.clock.now));
    const mounted = module.mount(view, { page: null, tab: options.tab || 'Data' });
    const before = page.fetchLog.length;
    await page.advance(10);
    const q = (selector) => page.document.querySelector(selector);
    const all = (selector) => page.document.querySelectorAll(selector);
    return { page, view, module, mounted, before, q, all, changes };
}

const text = (node) => (node ? node.textContent : null);
const calls = (page, method, re) => page.fetchLog.filter((e) => e.method === method && re.test(e.path));

// A file as an <input type="file"> gives it: a File (a Blob with its name), as the page sends it; over sets its name or size.
// Its text() throws: the page sends the file itself, never a string read from it.
function choose(t, content, over) {
    const input = t.q('.jfur-data-file');
    const file = new File([content], 'userratings-export.json', { type: 'application/json' });
    Object.entries(Object.assign({ text: () => { throw new Error('the page read the file as text'); } }, over || {})).forEach(([key, value]) => Object.defineProperty(file, key, { value }));
    input.files = [file];
    t.page.dispatch(input, 'change');
    return input.files[0];
}

const JE_PLAN = 'fedcba9876543210fedcba9876543210';
const JE_USER = '99999999999999999999999999999999';

function jeReport(over) {
    return Object.assign({
        DryRun: true, SourceFile: 'Jellyfin.Plugin.JellyfinEnhanced/reviews.json', Format: 'JellyfinEnhanced.Reviews/1', Copies: 'First', StoreUnmatched: true, RecreatePurged: false,
        Total: 7, Created: 0, Updated: 0, WouldCreate: 3, WouldUpdate: 0, Matched: 4, MatchedMultiple: 1, Unmatched: 1, StoredDetached: 1, UnknownUser: 1, Empty: 0, Invalid: 1, Adjusted: 1,
        AlreadyImported: 0, AlreadyImportedArchived: 0, SkippedNewer: 0, SkippedDeleted: 0, PreviouslyPurged: 0, Conflict: 0, ConflictKeptLocal: 0, ConflictTookJe: 0, DuplicateInSource: 0,
        KeyMismatch: 0, Clamped: 1, HiddenUserReviews: 0, DisabledUserReviews: 0,
        CopiesNote: '1 review matches more than one copy. <b>Copies</b>', MirrorNote: 'Mirror review ratings to Jellyfin is off.', SpoilerNote: 'No spoiler flag per review.',
        JeSettings: { SettingsFile: true }, SourceFields: ['UserId', 'TmdbId', 'MediaType', 'Content', 'Rating', 'CreatedAt', 'UpdatedAt'],
        UnknownUserIds: [{ JeUserId: JE_USER, Reviews: 1 }],
        LeftOut: [{ Key: 'bad:key', Reason: 'The key isn\'t <user>:<movie|tv>:<tmdb>.' }],
        Samples: [{ Key: 'k1', User: 'alice', Item: '<i>Heat</i>', Stars: 4.5, HasReview: true, Outcome: 'Created', AlsoMatched: 1 }, { Key: 'k2', User: null, Item: null, Stars: null, HasReview: false, Outcome: 'UnknownUser', AlsoMatched: 0 },
            { Key: JE_USER + ':movie:<b>550</b>', User: null, Item: null, Stars: null, HasReview: false, Outcome: 'Empty', AlsoMatched: 0 }, { Key: 'k4', User: null, Item: null, Stars: 3, HasReview: false, Outcome: 'UnknownUser', AlsoMatched: 0 },
            { Key: 'k5', User: 'alice', Item: null, Stars: null, HasReview: true, Outcome: 'Unmatched', AlsoMatched: 0 }],
        Outcomes: { Created: 3 }, Notes: ['1 review was dated ahead of this server\'s clock.'], PlanToken: JE_PLAN, SafetySnapshot: null
    }, over || {});
}

function jeDiagnostics(over, checklist) {
    return Object.assign({
        Installed: true, Active: true, Version: '12.14.0.0', SettingsFile: true,
        Settings: { ShowUserReviews: true, HideReviewsFromHiddenUsers: true, HideReviewsFromDisabledUsers: true, MirrorReviewRatingsToJellyfin: false },
        Overlap: {
            Feature: 'Reviews', On: true, Banner: 'Jellyfin Enhanced\'s User Reviews is on.', Complete: false,
            Checklist: checklist || [
                { Key: 'ShowUserReviews', Text: 'Enable User Written Reviews <b>off</b>', Done: false, Detail: 'Its review panel goes at the next page load.', NeedsRestart: false, Optional: false },
                { Key: 'ShowReviews', Text: 'The TMDB reviews section may stay on (optional)', Done: false, Detail: 'On.', NeedsRestart: false, Optional: true },
                { Key: 'ReviewTranslationEnabled', Text: 'Review translation off, its URL and key cleared', Done: false, Detail: 'Still to do: a service URL is set.', NeedsRestart: false, Optional: false },
                { Key: 'ActivityFeedShowReviewed', Text: 'Show recently reviewed off', Done: true, Detail: null, NeedsRestart: false, Optional: false }
            ]
        }
    }, over || {});
}

const dryRunSteps = (report) => () => [{ Phase: 'Resolve', Processed: 1, Total: 3 }, { State: 'Succeeded', Phase: 'Done', Report: report || importReport() }];

test('importing sends nothing; mount builds the tab and the first reads go after it returns: the snapshots and integrity, the users, the jobs', { skip }, async () => {
    const t = await open();
    assert.equal(t.before, 0, 'nothing at import or mount');
    assert.deepEqual(t.page.fetchLog.map((e) => e.method + ' ' + e.path), ['GET /UserRatings/Admin/Snapshots', 'GET /UserRatings/Admin/Diagnostics', 'GET /UserRatings/Admin/Jobs']);
    assert.ok(t.page.document.getElementById('jfur-admin-styles'), 'the admin styles');
    assert.deepEqual(t.all('.jfur-data-snapshot').map((li) => text(li.querySelector('.jfur-data-snapshot-name'))), ['userratings-20261030.db', 'userratings-import-20261031T120000Z.db']);
    assert.match(text(t.all('.jfur-data-snapshot')[1]), /Before an import/);
    assert.match(text(t.all('.jfur-data-snapshot')[0]), /Daily · .* · 40 KB/);
    assert.ok(t.q('.jfur-data-integrity').classList.contains('hide'), 'no banner for a healthy database');
    assert.match(text(t.q('.jfur-data-users-section')), /Every rating belongs to a user of this server\./);
    assert.equal(t.all('select').length, 0, 'no select: the TV layout\'s D-pad and OK work');
    // jellyfin-web's TV focus manager skips <input type="file"> unless it has the class "focusable".
    assert.ok(t.q('.jfur-data-file').classList.contains('focusable'), 'the file field a stop of the D-pad');
    assert.ok(t.q('.jfur-data-dryrun').disabled && t.q('.jfur-data-import').disabled);
    assert.equal(text(t.q('.jfur-data-hint')), 'Choose an export file, then run the dry run.');
    assert.equal(t.page.errors.length, 0);
});

test('the integrity banner: a damaged database, the last recovery and the quarantine folders, each as text; markup stays text', { skip }, async () => {
    const t = await open({
        integrity: {
            Status: 'ReadOnlyCorrupt', ReadOnly: true, Damaged: true, Message: '<b>database disk image is malformed</b>',
            LastRecovery: { Utc: '2026-10-31T08:00:00.000Z', Reason: 'integrity_check failed', RestoredFrom: 'userratings-20261030.db', LostSpan: '5 hours' },
            Quarantines: [{ Name: 'quarantine-20261031T080000Z', Utc: '2026-10-31T08:00:00.000Z', Files: ['userratings.db'], Size: 1 }]
        },
        snapshots: [snapshot('<i>userratings-20261030.db</i>')]
    });
    const notes = t.all('.jfur-data-integrity .jfur-notice');
    assert.equal(notes.length, 3);
    assert.equal(notes[0].getAttribute('role'), 'alert');
    assert.equal(text(notes[0]), 'The ratings database is damaged, so ratings can\'t be changed. Restore a snapshot below to repair it. <b>database disk image is malformed</b>');
    assert.match(text(notes[1]), /^The ratings database was restored from userratings-20261030\.db on .* after a problem \(integrity_check failed\); changes from the 5 hours before it were lost\.$/);
    assert.equal(text(notes[2]), 'Damaged copies are kept in the plugin\'s data folder until you delete them: quarantine-20261031T080000Z.');
    assert.equal(t.all('.jfur-data b, .jfur-data i').length, 0, 'markup is text');
    assert.equal(text(t.q('.jfur-data-snapshot-name')), '<i>userratings-20261030.db</i>');
    assert.equal(t.q('.jfur-data-snapshot-name').getAttribute('dir'), 'auto');

    // Restore on a damaged database says it repairs.
    t.q('.jfur-data-restore').click();
    await t.page.advance(10);
    assert.match(text(t.q('.jfur-panel')), /back to repair the database\? Changes made after it are lost, deletions too: the damaged database can't tell them\./);
    assert.equal(t.page.errors.length, 0);
});

test('a damaged database: Start empty asks first (Cancel focused), posts, says where the damaged one is kept, reads the tab again and keeps the focus', { skip }, async () => {
    // A damaged database with no snapshot that passes once had no way on from the page.
    const t = await open({ integrity: { Status: 'ReadOnlyCorrupt', ReadOnly: true, Damaged: true, Message: 'No snapshot passed its check.', LastRecovery: null, Quarantines: [] }, snapshots: [] });
    assert.equal(t.all('.jfur-data-start-empty').length, 1);
    const button = t.q('.jfur-data-integrity .jfur-data-start-empty');
    assert.equal(text(button), 'Start empty…');
    assert.equal(text(t.q('.jfur-data-start-empty-row .jfur-data-intro')), 'If no snapshot repairs it, start empty, then import an export if you have one. The damaged database and every snapshot are kept.');
    button.focus();
    button.click();
    await t.page.advance(10);
    const panel = t.q('.jfur-panel');
    assert.equal(text(panel.querySelector('p')), 'Start the ratings empty? Every rating and review is gone until a snapshot is restored or an export imported. The damaged database is moved to a quarantine folder and kept, and so is every snapshot. If a snapshot can repair it, restore that instead.');
    assert.ok(t.page.document.activeElement === panel.querySelector('.jfur-data-cancel'), 'Cancel focused');
    assert.equal(calls(t.page, 'POST', /\/StartEmpty$/).length, 0, 'nothing before the answer');

    t.page.respond('POST', '/UserRatings/Admin/Data/StartEmpty', { StartedEmpty: true, QuarantineFolder: '<b>quarantine-20261101-120000</b>', Reason: 'An admin chose to start empty' });
    t.page.respond('GET', '/UserRatings/Admin/Snapshots', { Snapshots: [], Integrity: { Status: 'RecoveredEmpty', ReadOnly: false, Damaged: false, Message: null, LastRecovery: null, Quarantines: [] } });
    panel.querySelector('.jfur-data-confirm').click();
    await t.page.advance(10);
    assert.equal(calls(t.page, 'POST', /^\/UserRatings\/Admin\/Data\/StartEmpty$/).length, 1);
    assert.match(text(t.q('.jfur-data')), /The ratings started empty\.The damaged database is kept in <b>quarantine-20261101-120000<\/b>\./);
    assert.equal(t.all('.jfur-data b').length, 0, 'markup is text');
    assert.equal(t.all('.jfur-data-start-empty').length, 0, 'the database is fine: no Start empty');
    assert.equal(text(t.page.document.activeElement), 'Snapshots', 'the focus goes to the snapshots, never to the page\'s body');
    assert.equal(t.page.errors.length, 0);
});

test('Start empty refused: the server\'s words and Retry; no Start empty on a database that is fine', { skip }, async () => {
    const fine = await open();
    assert.equal(fine.all('.jfur-data-start-empty').length, 0);

    const t = await open({ integrity: { Status: 'ReadOnlyCorrupt', ReadOnly: true, Damaged: true, Message: null, LastRecovery: null, Quarantines: [] }, snapshots: [] });
    t.page.respond('POST', '/UserRatings/Admin/Data/StartEmpty', { title: 'Conflict', status: 409, detail: 'An export is running. Wait for it to finish.', code: 'JobRunning' }, 409);
    t.q('.jfur-data-start-empty').click();
    await t.page.advance(10);
    t.q('.jfur-panel .jfur-data-confirm').click();
    await t.page.advance(10);
    assert.match(text(t.q('.jfur-data')), /Another data job is running. Wait for it to finish, or stop it.Try again/);
    assert.equal(t.all('.jfur-data-start-empty').length, 1, 'still damaged');
    assert.equal(t.page.errors.length, 0);
});

test('an admin without every library: 403 AllLibrariesNeeded hides export, import, restore and the unknown users and says why', { skip }, async () => {
    // The unknown users' tools once stayed (Remap and the policy change every library's ratings); the
    // server answers 403 to them and an empty OrphanUsers, and the page hides them too.
    const t = await open({
        snapshotsBody: { title: 'Forbidden', status: 403, detail: 'A restore covers every library\'s ratings (the server\'s words).', code: 'AllLibrariesNeeded' },
        snapshotsStatus: 403,
        users: { DeletedUserPolicy: 'Delete', OrphanUsers: [], OrphanUsersHidden: 'AllLibrariesNeeded' }
    });
    assert.ok(t.q('.jfur-data-sections').classList.contains('hide'));
    // 1.0.1 (found on a server in use): an administrator whose libraries were ticked one by one saw only why, not how.
    // The note names the fix: the stock Access tab's box (jellyfin-web 10.11.5's OptionEnableAccessToAllLibraries), Save,
    // a reload; and Access Roles, which writes that box for an account it manages. The page's own words, whatever the
    // server's detail says.
    assert.equal(text(t.q('.jfur-data-restricted')), 'Export, import, restore and the unknown users\' tools cover every library\'s ratings, so they are for administrators with access to every library. To use them: Dashboard → Users → your account → Access, tick "Enable access to all libraries", then Save, then reload this tab. If an Access Roles role manages this account\'s libraries, the role must give all libraries.');
    assert.ok(t.q('.jfur-data-users-section').classList.contains('hide'));
    assert.ok(t.q('.jfur-data-user-text') === null);
    assert.equal(calls(t.page, 'POST', /\/Users\//).length, 0);
    assert.equal(t.page.errors.length, 0);
});

test('Export: a job polled every second, then its file fetched with the session\'s authorization and saved under its name; the counts as text', { skip }, async () => {
    const t = await open();
    const server = jobServer(t.page);
    server.script = () => [{ Phase: 'Read' }, { State: 'Succeeded', Phase: 'Done', HasFile: true, Report: { Ratings: 12, Reviews: 5, Detached: 2, Archived: 1, FileName: 'userratings-export-20261101-1200.json' } }];
    const saved = [];
    t.page.document.body.addEventListener('click', (e) => {
        if (e.target.tagName === 'A') {
            saved.push({ name: e.target.getAttribute('download'), href: e.target.getAttribute('href') });
        }
    });
    const first = t.page.fetchLog.length;
    t.q('.jfur-data-export').click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-data-export-status')), 'Exporting…');
    assert.ok(t.q('.jfur-data-export').disabled);
    await t.page.advance(1000);
    assert.equal(calls(t.page, 'GET', /\/Jobs\/job1$/).length, 1, 'one poll a second');
    await t.page.advance(1000);
    const file = calls(t.page, 'GET', /\/Jobs\/job1\/File$/);
    assert.equal(file.length, 1);
    assert.match(file[0].headers.Authorization, /^MediaBrowser .*Token="test-token"$/);
    assert.equal(Object.keys(file[0].query).length, 0, 'never a token in the link');
    assert.equal(saved.length, 1);
    assert.equal(saved[0].name, 'userratings-export-20261101-1200.json');
    assert.match(saved[0].href, /^blob:/);
    assert.equal(t.all('a[download]').length, 0, 'the link goes once clicked');
    assert.equal(text(t.q('.jfur-data-export-status')), 'Exported 12 ratings (5 with a review, 2 detached) and 1 archived ones as userratings-export-20261101-1200.json.');
    assert.ok(!t.q('.jfur-data-export').disabled);
    assert.ok(!t.q('.jfur-data-download').classList.contains('hide'));
    assert.deepEqual(t.page.fetchLog.slice(first).map((e) => e.method + ' ' + e.path.replace('/UserRatings/Admin/', '')), ['POST Export', 'GET Jobs/job1', 'GET Jobs/job1', 'GET Jobs/job1/File']);

    // Download again fetches the file again.
    t.q('.jfur-data-download').click();
    await t.page.advance(10);
    assert.equal(calls(t.page, 'GET', /\/Jobs\/job1\/File$/).length, 2);
    assert.equal(saved.length, 2);
    assert.equal(t.page.errors.length, 0);
});

test('Export refused: another job running (409 JobRunning), a job that failed; each in words with Try again', { skip }, async () => {
    const t = await open();
    const server = jobServer(t.page);
    server.refuse = { title: 'Conflict', status: 409, detail: 'A data job is running.', code: 'JobRunning' };
    t.q('.jfur-data-export').click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-data-export-section .jfur-notice-text')), 'Another data job is running. Wait for it to finish, or stop it.');
    assert.equal(t.q('.jfur-data-export-section .jfur-notice').getAttribute('role'), 'alert');

    server.refuse = null;
    server.script = () => [{ State: 'Failed', Phase: 'Done', Error: 'The ratings data is read-only right now.', ErrorStatus: 503 }];
    t.q('.jfur-data-export-section .jfur-notice-action').click();
    await t.page.advance(1010);
    assert.equal(text(t.q('.jfur-data-export-section .jfur-notice-text')), 'It didn\'t work. The ratings data is read-only right now.');
    assert.equal(text(t.q('.jfur-data-export-status')), '');
    assert.equal(calls(t.page, 'GET', /\/File$/).length, 0);
    assert.equal(t.page.errors.length, 0);
});

test('Import: the dry run posts the file as it is with its options; its report as text; Import asks (Cancel focused), posts the plan, and the tab and the page read again', { skip }, async () => {
    const t = await open();
    const server = jobServer(t.page);
    server.script = (kind, e) => (e.query.dryRun === 'false'
        ? [{ Phase: 'Write', Writing: true, Total: 3 }, { State: 'Succeeded', Phase: 'Done', Report: importReport({ DryRun: false, SafetySnapshot: 'userratings-import-20261101T120000Z.db' }) }]
        : dryRunSteps(importReport({
            Users: { Mapped: [{ Id: 'u1', NewId: 'u1', Name: '<b>alice</b>', Records: 2 }], Unknown: [] },
            Unusable: 3,
            UnusableRows: [
                { Table: 'Ratings', Row: 2, Id: '<b>r2</b>', Reason: 'Its Origin \'<i>Bogus</i>\' isn\'t one this version knows.' },
                { Table: 'Archive', Row: 1, Id: null, Reason: 'This version can\'t read it.' }]
        }))());
    const chosen = choose(t, EXPORT_TEXT);
    assert.ok(!t.q('.jfur-data-dryrun').disabled);
    assert.ok(t.q('.jfur-data-import').disabled, 'Import waits for the dry run');
    assert.equal(text(t.q('.jfur-data-hint')), 'Run the dry run of this file and these options first; Import then does what it showed.');

    t.q('.jfur-data-dryrun').click();
    await t.page.advance(10);
    assert.equal(server.posts.length, 1);
    assert.deepEqual(server.posts[0].query, { mode: 'Merge', dryRun: 'true', mapUsersBy: 'Id' });
    assert.equal(server.posts[0].body, chosen, 'the file\'s bytes as they are, never parsed and written again');
    assert.equal(calls(t.page, 'POST', /\/Import$/)[0].headers['Content-Type'], 'application/json');
    await t.page.advance(1000);
    assert.equal(text(t.q('.jfur-data-progress')), 'Matching titles: 1 of 3');
    await t.page.advance(1000);
    const report = t.q('.jfur-data-report');
    assert.match(text(report.querySelector('.jfur-data-summary')), /^Dry run, nothing was changed\. The file is an export of User Ratings 0\.3\.18\.0 from .*, with 3 ratings\.$/);
    assert.deepEqual(Array.from(report.querySelectorAll('.jfur-data-outcomes li')).map(text), ['New ratings: 1', 'Updated (newer in the file): 1', 'Skipped: their member isn\'t on this server: 1']);
    assert.match(text(report), /Rows of the file that can't be imported, left out: 3/);
    // Row by row, with the reason; the server lists the first 100, the rest are counted.
    assert.deepEqual(Array.from(report.querySelectorAll('.jfur-data-unusable li')).map(text), [
        'Ratings, row 2 (<b>r2</b>): Its Origin \'<i>Bogus</i>\' isn\'t one this version knows.',
        'Archive, row 1: This version can\'t read it.',
        'And 1 more.']);
    assert.equal(report.querySelectorAll('i').length, 0);
    assert.match(text(report), /<b>alice<\/b>: found here \(2 ratings\)/);
    assert.equal(report.querySelectorAll('b').length, 0);
    assert.ok(!t.q('.jfur-data-import').disabled, 'the dry run arms Import');
    assert.equal(text(t.q('.jfur-data-progress')), '');

    const importButton = t.q('.jfur-data-import');
    importButton.focus();
    importButton.click();
    await t.page.advance(10);
    const panel = t.q('.jfur-panel');
    assert.equal(text(panel.querySelector('p')), 'Import the file\'s ratings as the dry run showed? A snapshot of the ratings as they are now is taken first.');
    assert.ok(t.page.document.activeElement === panel.querySelector('.jfur-data-cancel'), 'Cancel focused');
    assert.equal(server.posts.length, 1, 'nothing before the answer');
    panel.querySelector('.jfur-data-confirm').click();
    await t.page.advance(10);
    assert.equal(server.posts.length, 2);
    assert.deepEqual(server.posts[1].query, { mode: 'Merge', dryRun: 'false', mapUsersBy: 'Id', ifPlan: PLAN });
    assert.equal(server.posts[1].body, chosen);
    await t.page.advance(1000);
    assert.equal(text(t.q('.jfur-data-progress')), 'Writing 3 ratings…');
    assert.ok(t.q('.jfur-data-stop').classList.contains('hide'), 'no Stop once it writes');
    const reads = calls(t.page, 'GET', /\/Snapshots$/).length;
    await t.page.advance(1000);
    assert.match(text(t.q('.jfur-data-report .jfur-data-summary')), /^Imported\./);
    assert.match(text(t.q('.jfur-data-report')), /The ratings from just before were kept as userratings-import-20261101T120000Z\.db \(Snapshots, below\)\./);
    assert.equal(calls(t.page, 'GET', /\/Snapshots$/).length, reads + 1, 'the snapshots read again');
    assert.equal(t.changes.length, 1, 'the page told to read Diagnostics again');
    assert.ok(t.q('.jfur-data-import').disabled, 'a new import needs a new dry run');
    assert.equal(t.page.errors.length, 0);
});

test('another option, another file, or Cancel: Import sends nothing and needs another dry run; Replace asks in its own words', { skip }, async () => {
    const t = await open();
    const server = jobServer(t.page);
    server.script = dryRunSteps();
    choose(t, EXPORT_TEXT);
    t.q('.jfur-data-dryrun').click();
    await t.page.advance(2010);
    assert.ok(!t.q('.jfur-data-import').disabled);

    const replace = t.q('.jfur-data-import-section [data-jfur-value="Replace"]');
    replace.click();
    assert.equal(replace.getAttribute('aria-checked'), 'true');
    assert.ok(t.q('.jfur-data-import').disabled, 'another mode: another dry run');
    t.q('.jfur-data-dryrun').click();
    await t.page.advance(2010);
    assert.deepEqual(server.posts[1].query, { mode: 'Replace', dryRun: 'true', mapUsersBy: 'Id' });
    t.q('.jfur-data-import').click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-panel p')), 'Replace every rating on this server with the file\'s? Ratings that aren\'t in the file are removed. A snapshot of the ratings as they are now is taken first.');
    t.q('.jfur-panel .jfur-data-cancel').click();
    await t.page.advance(10);
    assert.ok(t.q('.jfur-panel') === null);
    assert.equal(server.posts.length, 2, 'Cancel sends nothing');
    assert.ok(t.page.document.activeElement === t.q('.jfur-data-import'), 'the focus back on Import');

    t.q('.jfur-data-import-section [data-jfur-value="Name"]').click();
    assert.ok(t.q('.jfur-data-import').disabled, 'another matching: another dry run');
    t.q('.jfur-data-dryrun').click();
    await t.page.advance(2010);
    assert.deepEqual(server.posts[2].query, { mode: 'Replace', dryRun: 'true', mapUsersBy: 'Name' });
    choose(t, EXPORT_TEXT + ' ', { name: 'other.json' });
    assert.ok(t.q('.jfur-data-import').disabled, 'another file: another dry run');
    assert.equal(t.q('.jfur-data-report').children.length, 0);
    assert.equal(server.posts.length, 3);
    assert.equal(t.page.errors.length, 0);
});

test('the plan changed since the dry run: the job\'s 409 Changed imports nothing, says so and asks for another dry run; a file over 100 MB is never sent', { skip }, async () => {
    const t = await open();
    const server = jobServer(t.page);
    server.script = (kind, e) => (e.query.dryRun === 'false'
        ? [{ State: 'Failed', Phase: 'Done', Error: 'Changed: the plan changed.', ErrorCode: 'Changed', ErrorStatus: 409 }]
        : dryRunSteps()());
    choose(t, EXPORT_TEXT);
    t.q('.jfur-data-dryrun').click();
    await t.page.advance(2010);
    t.q('.jfur-data-import').click();
    await t.page.advance(10);
    t.q('.jfur-panel .jfur-data-confirm').click();
    await t.page.advance(1010);
    assert.equal(text(t.q('.jfur-data-import-section .jfur-notice-text')), 'The ratings changed after the dry run, so nothing was imported. Run the dry run again to see what Import does now.');
    assert.ok(t.q('.jfur-data-import').disabled);
    assert.ok(!t.q('.jfur-data-dryrun').disabled);
    assert.equal(t.changes.length, 0);

    const big = await open();
    const bigServer = jobServer(big.page);
    bigServer.script = dryRunSteps();
    choose(big, EXPORT_TEXT, { size: 100 * 1024 * 1024 + 1 });
    big.q('.jfur-data-dryrun').click();
    await big.page.advance(10);
    assert.equal(bigServer.posts.length, 0);
    assert.equal(text(big.q('.jfur-data-import-section .jfur-notice-text')), 'The file is larger than 100 MB, so it can\'t be imported.');
    assert.equal(t.page.errors.length + big.page.errors.length, 0);
});

test('Stop while the job reads cancels it (nothing imported); refused while it writes (409 JobWriting), Stop goes and the job finishes', { skip }, async () => {
    const t = await open();
    const server = jobServer(t.page);
    server.script = () => [{ Phase: 'Resolve', Processed: 1, Total: 50 }];
    choose(t, EXPORT_TEXT);
    t.q('.jfur-data-dryrun').click();
    await t.page.advance(1010);
    const stop = t.q('.jfur-data-stop');
    assert.ok(!stop.classList.contains('hide'));
    stop.click();
    await t.page.advance(10);
    assert.deepEqual(server.deletes, ['job1']);
    await t.page.advance(1000);
    assert.equal(text(t.q('.jfur-data-import-section .jfur-notice-text')), 'Stopped. Nothing was imported.');
    assert.ok(stop.classList.contains('hide'));
    assert.ok(t.q('.jfur-data-import').disabled);

    server.script = () => [{ Phase: 'Resolve', Processed: 1, Total: 50 }, { Phase: 'Resolve', Processed: 2, Total: 50 }, { State: 'Succeeded', Phase: 'Done', Report: importReport() }];
    server.deleteRefuse = { title: 'Conflict', status: 409, detail: 'It is writing.', code: 'JobWriting' };
    t.q('.jfur-data-dryrun').click();
    await t.page.advance(1010);
    t.q('.jfur-data-stop').click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-data-import-section .jfur-notice-text')), 'It is writing now, so it can\'t be stopped; it finishes by itself.');
    assert.ok(t.q('.jfur-data-stop').classList.contains('hide'));
    await t.page.advance(2000);
    assert.ok(!t.q('.jfur-data-import').disabled, 'the dry run finished');
    assert.equal(t.page.errors.length, 0);
});

test('a job running when the tab opens is followed to its end; an export\'s offers its file', { skip }, async () => {
    const page = await open({ jobs: [{ Id: 'job7', Kind: 'Export', DryRun: true, State: 'Running', Phase: 'Read', Writing: false, Processed: 0, Total: 0 }] });
    page.page.respond('GET', /^\/UserRatings\/Admin\/Jobs\/job7$/, { Id: 'job7', Kind: 'Export', DryRun: true, State: 'Succeeded', Phase: 'Done', HasFile: true, Report: { Ratings: 1, Reviews: 0, Detached: 0, Archived: 0, FileName: 'userratings-export-x.json' } });
    assert.equal(text(page.q('.jfur-data-progress')), 'Reading…');
    assert.ok(page.q('.jfur-data-export').disabled);
    await page.page.advance(1000);
    assert.match(text(page.q('.jfur-data-export-status')), /as userratings-export-x\.json\.$/);
    assert.ok(!page.q('.jfur-data-download').classList.contains('hide'));
    assert.ok(!page.q('.jfur-data-export').disabled);
    assert.equal(page.page.errors.length, 0);
});

test('Restore… asks (Cancel focused; on TV the D-pad moves through it and Back closes it, the focus back), restores, says what it did and reads again; a refusal in the server\'s words with Try again', { skip }, async () => {
    const t = await open({ layout: 'tv' });
    let answer = { RestoredFrom: 'userratings-import-20261031T120000Z.db', SafetySnapshot: 'userratings-restore-20261101T120000Z.db', SchemaVersion: 3, MigratedFrom: null, ArchiveRestoredFrom: 'userratings-archived-import-20261031T120000Z.jsonl' };
    let status = 200;
    t.page.respond('POST', '/UserRatings/Admin/Snapshots/Restore', () => answer, () => status);
    const restore = t.all('.jfur-data-restore')[1];
    assert.equal(restore.getAttribute('aria-label'), 'Restore userratings-import-20261031T120000Z.db…');
    restore.focus();
    restore.click();
    await t.page.advance(10);
    const panel = t.q('.jfur-panel');
    const cancel = panel.querySelector('.jfur-data-cancel');
    assert.ok(t.page.document.activeElement === cancel);
    assert.match(text(panel.querySelector('p')), /^Put userratings-import-20261031T120000Z\.db from .* back\? Every change made since is undone, imports with their deletions too, except deletions made on this server, which stay deleted\. A snapshot of the ratings as they are now is taken first\.$/);
    t.page.key(cancel, 'ArrowRight');
    assert.ok(t.page.document.activeElement === panel.querySelector('.jfur-data-confirm'), 'the D-pad: Restore');
    t.page.key(t.page.document.activeElement, 'GoBack', { keyCode: 461 });
    await t.page.advance(10);
    assert.ok(t.q('.jfur-panel') === null, 'Back closed it');
    assert.ok(t.page.document.activeElement === restore, 'the focus back on Restore…');
    assert.equal(calls(t.page, 'POST', /Restore$/).length, 0);

    restore.click();
    await t.page.advance(10);
    t.q('.jfur-panel .jfur-data-confirm').click();
    await t.page.advance(10);
    const post = calls(t.page, 'POST', /Restore$/);
    assert.equal(post.length, 1);
    assert.deepEqual(post[0].query, { name: 'userratings-import-20261031T120000Z.db' });
    const report = t.q('.jfur-data-snapshots-section .jfur-data-report');
    assert.deepEqual(Array.from(report.querySelectorAll('p')).map(text), [
        'Restored userratings-import-20261031T120000Z.db.',
        'The archive\'s copy from that operation was put back too.',
        'The ratings from just before were kept as userratings-restore-20261101T120000Z.db (Snapshots, below).'
    ]);
    assert.equal(t.changes.length, 1);
    assert.equal(calls(t.page, 'GET', /\/Snapshots$/).length, 2);
    // The list read again replaced the button: the focus is on that snapshot's new Restore…, never the page's body.
    const focused = t.page.document.activeElement;
    assert.ok(focused !== restore && focused.isConnected && focused.getAttribute('data-jfur-name') === 'userratings-import-20261031T120000Z.db', 'the focus on that snapshot\'s Restore… again');

    status = 409;
    answer = { title: 'Conflict', status: 409, detail: 'The ratings data is read-only, so nothing can be changed.' };
    t.all('.jfur-data-restore')[0].click();
    await t.page.advance(10);
    t.q('.jfur-panel .jfur-data-confirm').click();
    await t.page.advance(10);
    assert.equal(text(report.querySelector('.jfur-notice-text')), 'It didn\'t work. The ratings data is read-only, so nothing can be changed.');
    assert.equal(text(report.querySelector('.jfur-notice-action')), 'Try again');
    assert.equal(t.changes.length, 1);
    assert.equal(t.page.errors.length, 0);
});

test('Restore: what it kept and what it undid of the imports, each in words; a restore its own check finds damaged reads the tab again, its banner and Start empty with it', { skip }, async () => {
    // Since 0.3.19: the restore rule's counts, and the 409 Damaged of a restore whose own check finds the database damaged.
    const t = await open();
    let answer = { RestoredFrom: 'userratings-20261030.db', SafetySnapshot: null, DeletionsKept: 2, ReviewRemovalsKept: 1, FormerMembersKept: 0, ImportDeletionsUndone: 3, ImportFormerMembersUndone: 1, DeletionsError: null };
    let status = 200;
    t.page.respond('POST', '/UserRatings/Admin/Snapshots/Restore', () => answer, () => status);
    t.all('.jfur-data-restore')[0].click();
    await t.page.advance(10);
    t.q('.jfur-panel .jfur-data-confirm').click();
    await t.page.advance(10);
    const report = t.q('.jfur-data-snapshots-section .jfur-data-report');
    assert.deepEqual(Array.from(report.querySelectorAll('p')).map(text), [
        'Restored userratings-20261030.db.',
        'Deletions made on this server since stayed: 2 deleted ratings, 1 removed reviews, 0 former members.',
        'Imports made since were undone, their deletions too: 3 deleted ratings and 1 former members came back.'
    ]);

    status = 409;
    answer = { title: 'Conflict', status: 409, detail: 'The data failed its integrity check (integrity_check before the restore snapshot: malformed), so nothing can be changed.', code: 'Damaged' };
    t.page.respond('GET', '/UserRatings/Admin/Snapshots', { Snapshots: [snapshot('userratings-20261030.db')], Integrity: { Status: 'ReadOnlyCorrupt', ReadOnly: true, Damaged: true, Message: 'Found by the restore.', LastRecovery: null, Quarantines: [] } });
    const reads = calls(t.page, 'GET', /\/Snapshots$/).length;
    t.all('.jfur-data-restore')[0].click();
    await t.page.advance(10);
    t.q('.jfur-panel .jfur-data-confirm').click();
    await t.page.advance(10);
    assert.match(text(report.querySelector('.jfur-notice-text')), /failed its integrity check/);
    assert.equal(calls(t.page, 'GET', /\/Snapshots$/).length, reads + 1, 'the tab read again');
    assert.equal(t.all('.jfur-data-integrity .jfur-data-start-empty').length, 1, 'Start empty offered');
    t.all('.jfur-data-restore')[0].click();
    await t.page.advance(10);
    assert.match(text(t.q('.jfur-panel p')), /^Put userratings-20261030\.db from .* back to repair the database\?/);
    assert.equal(t.page.errors.length, 0);
});

test('after a Start empty: its banner with the time and why; restoring a snapshot from before it says the deletions in between may come back, one from after it does not', { skip }, async () => {
    // Since 0.3.19: RestoredFrom is null after Start empty, and a snapshot from before it can't keep the deletions in between.
    const t = await open({
        integrity: { Status: 'RecoveredEmpty', ReadOnly: false, Damaged: false, Message: null, LastRecovery: { Utc: '2026-10-31T00:00:00.000Z', Reason: 'An admin chose to start empty', RestoredFrom: null, StartedEmpty: true }, Quarantines: [] },
        snapshots: [snapshot('userratings-20261030.db'), snapshot('userratings-import-20261031T120000Z.db', { Kind: 'PreOp', Operation: 'import', Utc: '2026-10-31T12:00:00.000Z' })]
    });
    const notes = t.all('.jfur-data-integrity .jfur-notice');
    assert.equal(notes.length, 1);
    assert.match(text(notes[0]), /^The ratings database was started empty on .* after a problem \(An admin chose to start empty\)\.$/);
    t.all('.jfur-data-restore')[0].click();
    await t.page.advance(10);
    assert.match(text(t.q('.jfur-panel p')), /^Put userratings-20261030\.db from .* back\? Every change made since is undone\. The ratings were started empty on .*, after it: deletions made between the two can't be told and may come back; deletions made on this server since the start empty stay deleted\./);
    assert.doesNotMatch(text(t.q('.jfur-panel p')), /except deletions made on this server, which stay deleted/);
    t.q('.jfur-panel .jfur-data-cancel').click();
    await t.page.advance(10);
    t.all('.jfur-data-restore')[1].click();
    await t.page.advance(10);
    assert.match(text(t.q('.jfur-panel p')), /except deletions made on this server, which stay deleted/);
    assert.equal(t.page.errors.length, 0);
});

test('unknown users: Remap by name\'s dry run, then its PlanToken after the question; the policy for one user or every one posts their ids; a 503 and a 409 alike', { skip }, async () => {
    const t = await open({ users: { DeletedUserPolicy: 'Anonymize', OrphanUsers: [{ UserId: 'u8', LastKnownName: '<b>yan</b>', Records: 2, PolicyPending: true }, { UserId: 'u9', LastKnownName: null, Records: 1 }] } });
    assert.deepEqual(t.all('.jfur-data-user-text').map(text), ['<b>yan</b>: 2 ratings (the policy is pending)', 'An unknown user (u9): 1 ratings']);
    assert.match(text(t.q('.jfur-data-users-section .jfur-data-intro')), /\(Anonymize \(kept as a former member's\)\)\.$/);
    const remaps = [];
    let remapStatus = 200;
    t.page.respond('POST', '/UserRatings/Admin/Users/Remap', (e) => {
        remaps.push(e.query);
        if (remapStatus !== 200) {
            return { title: 'x', status: remapStatus, detail: 'The ratings data is read-only, so nothing can be changed.' };
        }

        return e.query.dryRun === 'false'
            ? { DryRun: false, Mapped: [], Unmatched: [], MovedRecords: 2, SafetySnapshots: [] }
            : { DryRun: true, Mapped: [{ OldId: 'u8', NewId: 'n8', Name: '<b>yan</b>', Records: 2 }], Unmatched: [{ OldId: 'u9', Name: null, Records: 1, Reason: 'NoName' }], MovedRecords: 0, PlanToken: 'remap-plan', Ambiguous: 1, ArchivedRecords: 0 };
    }, () => remapStatus);
    t.q('.jfur-data-remap').click();
    await t.page.advance(10);
    assert.deepEqual(remaps, [{ by: 'Name', dryRun: 'true' }]);
    const panel = t.q('.jfur-panel');
    assert.deepEqual(Array.from(panel.querySelectorAll('li')).map(text), ['<b>yan</b>: 2 ratings to the user of this server with that name', 'u9: no match (NoName)']);
    assert.match(text(panel), /Ratings left detached as ambiguous, because both ids rated the title: 1/);
    assert.equal(panel.querySelectorAll('b').length, 0);
    panel.querySelector('.jfur-data-confirm').click();
    await t.page.advance(10);
    assert.deepEqual(remaps[1], { by: 'Name', dryRun: 'false', ifPlan: 'remap-plan' });
    assert.equal(text(t.q('.jfur-data-users-section .jfur-data-report')), 'Remapped: 2 ratings moved.');
    assert.equal(t.changes.length, 1);

    const policies = [];
    t.page.respond('POST', '/UserRatings/Admin/Users/ApplyPolicy', (e) => {
        policies.push(e.body);
        return { Policy: 'Anonymize', Affected: 2, Users: [], SafetySnapshots: [] };
    });
    // A click focuses the button, as in a browser.
    t.q('[data-jfur-user="u8"]').focus();
    t.q('[data-jfur-user="u8"]').click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-panel p')), 'Apply Anonymize (kept as a former member\'s) to the ratings of 1 unknown users? A snapshot is taken first.');
    t.q('.jfur-panel .jfur-data-confirm').click();
    await t.page.advance(10);
    // The users read again: the focus on that user's button again, not the one the list replaced.
    assert.ok(t.page.document.activeElement.isConnected && t.page.document.activeElement.getAttribute('data-jfur-user') === 'u8', 'the focus on u8\'s button again');
    t.q('.jfur-data-policy-all').click();
    await t.page.advance(10);
    t.q('.jfur-panel .jfur-data-confirm').click();
    await t.page.advance(10);
    assert.deepEqual(policies, [{ UserIds: ['u8'] }, { UserIds: ['u8', 'u9'] }]);
    assert.equal(text(t.q('.jfur-data-users-section .jfur-data-report')), 'The deleted-user policy was applied: 2 ratings affected.');

    for (const code of [503, 409]) {
        remapStatus = code;
        t.q('.jfur-data-remap').click();
        await t.page.advance(10);
        assert.equal(text(t.q('.jfur-data-users-section .jfur-data-report .jfur-notice-text')), 'It didn\'t work. The ratings data is read-only, so nothing can be changed.', String(code));
        assert.equal(text(t.q('.jfur-data-users-section .jfur-data-report .jfur-notice-action')), 'Try again');
    }

    assert.equal(t.page.errors.length, 0);
});

test('the detached list: each rating as text with its member, title, stars, review or not, when and why, and the titles it could go back to; paging; the reasons', { skip }, async () => {
    const rows = Array.from({ length: 60 }, (_, i) => detached(i + 1, i === 0 ? {
        UserName: '<b>alice</b>', Name: '<i>Heat</i>', Year: 1995, DetachReason: 'Ambiguous', HasReview: false, Stars: 0,
        Candidates: [{ ItemId: 'c1', Name: 'Heat', Kind: 'Movie', Year: 1995, Strength: 'Weak', MemberRatesIt: true }, { ItemId: 'c2', Name: 'Heat', Kind: 'Movie', Year: 1995, Strength: 'Strong', MemberRatesIt: false }]
    } : i === 1 ? { UserName: null, Kind: 'Episode', Name: 'Pilot', SeriesName: 'Lost', Season: 1, Episode: 1, Candidates: [] } : {}));
    const t = await open({ tab: 'Detached', detached: rows });
    assert.deepEqual(t.page.fetchLog.map((e) => e.method + ' ' + e.path + ' ' + JSON.stringify(e.query)), ['GET /UserRatings/Admin/Detached {"startIndex":"0","limit":"50"}']);
    const items = t.all('.jfur-det-row');
    assert.equal(items.length, 50);
    const first = items[0];
    assert.equal(text(first.querySelector('.jfur-det-title')), '<i>Heat</i> (1995)');
    assert.equal(first.querySelector('.jfur-det-title').getAttribute('dir'), 'auto');
    assert.match(text(first), /By <b>alice<\/b> · No stars, stars only/);
    assert.match(text(first), /Detached .*: ambiguous/);
    assert.equal(t.all('.jfur-det b, .jfur-det i').length, 0, 'markup is text');
    const relinks = Array.from(first.querySelectorAll('.jfur-det-relink'));
    assert.deepEqual(relinks.map((b) => b.disabled), [true, false], 'a title the member rates can\'t take it');
    assert.match(text(first.querySelectorAll('.jfur-det-candidate')[0]), /the same name and year · This member already rates it\./);
    assert.equal(relinks[1].getAttribute('aria-label'), 'Relink the rating of <b>alice</b> to Heat (1995)');
    assert.equal(text(items[1].querySelector('.jfur-det-title')), 'Lost S1E1 · Pilot');
    assert.match(text(items[1]), /By Deleted user/);
    assert.equal(text(items[1].querySelector('.jfur-det-none')), 'No title in the library matches.');
    assert.equal(text(t.q('.jfur-det-pager .jfur-mod-count')), '1–50 of 60');

    t.q('.jfur-det-next').click();
    await t.page.advance(10);
    assert.equal(t.all('.jfur-det-row').length, 10);
    assert.equal(t.page.fetchLog.at(-1).query.startIndex, '50');
    assert.ok(t.page.document.activeElement === t.q('.jfur-det-next') || t.page.document.activeElement.tagName === 'BUTTON');

    t.q('[data-jfur-value="Ambiguous"]').click();
    await t.page.advance(10);
    assert.deepEqual(t.page.fetchLog.at(-1).query, { startIndex: '0', limit: '50', reason: 'Ambiguous' });
    assert.equal(t.all('.jfur-det-row').length, 1);
    assert.ok(t.q('.jfur-det-pager').classList.contains('hide'));
    t.q('[data-jfur-value="Missing"]').click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-det .jfur-notice')), 'No detached ratings for this reason.');
    assert.equal(t.all('select').length, 0);
    assert.equal(t.page.errors.length, 0);
});

test('Relink asks, posts the title, says so and reads again, the focus on the next row; 409 AlreadyRated and 404 in words; Purge asks, deletes it for good and reads again', { skip }, async () => {
    let rows = [detached(1), detached(2), detached(3)];
    const t = await open({ tab: 'Detached', detached: () => rows });
    const posts = [];
    let relinkStatus = 200;
    t.page.respond('POST', /^\/UserRatings\/Admin\/Detached\/[^/]+\/Relink$/, (e) => {
        posts.push({ id: e.path.split('/')[4], body: e.body });
        if (relinkStatus === 409) {
            return { title: 'Conflict', status: 409, detail: 'The member already has a rating on that item.', code: 'AlreadyRated' };
        }

        if (relinkStatus === 404) {
            return { title: 'Not Found', status: 404, detail: 'No detached rating has that id.' };
        }

        rows = rows.filter((r) => r.Id !== e.path.split('/')[4]);
        return { Id: 'x' };
    }, () => relinkStatus);
    const relink = t.q('[data-jfur-rating="d2"] .jfur-det-relink');
    relink.focus();
    relink.click();
    await t.page.advance(10);
    assert.match(text(t.q('.jfur-panel p')), /^Give the rating of member 2 on Title 2 \(2002\) to Title 2 \(2002\)\?$/);
    assert.ok(t.page.document.activeElement === t.q('.jfur-panel .jfur-data-cancel'));
    t.q('.jfur-panel .jfur-data-confirm').click();
    await t.page.advance(10);
    assert.deepEqual(posts, [{ id: 'd2', body: { ItemId: 'c2' } }]);
    assert.equal(text(t.q('.jfur-toast')), 'The rating was relinked.');
    assert.deepEqual(t.all('.jfur-det-row').map((r) => r.getAttribute('data-jfur-rating')), ['d1', 'd3']);
    assert.equal(t.changes.length, 1);
    assert.ok(t.page.document.activeElement && t.page.document.activeElement.tagName === 'BUTTON' && t.page.document.activeElement.isConnected, 'the focus on a button of the list, not the page\'s body');

    for (const [status, words] of [[409, 'The member already rates that title, so nothing was changed. Purge this rating, or remove theirs first.'], [404, 'This rating isn\'t detached any more, or you can\'t change it. The list is read again.']]) {
        relinkStatus = status;
        const reads = calls(t.page, 'GET', /\/Detached$/).length;
        t.q('[data-jfur-rating="d1"] .jfur-det-relink').click();
        await t.page.advance(10);
        t.q('.jfur-panel .jfur-data-confirm').click();
        await t.page.advance(10);
        assert.equal(text(t.q('.jfur-det-notice .jfur-notice-text')), words);
        assert.equal(calls(t.page, 'GET', /\/Detached$/).length, reads + 1, 'read again');
        assert.ok(t.page.document.activeElement.isConnected && t.page.document.activeElement.tagName === 'BUTTON', 'the focus on the list read again, ' + status);
    }

    const deletes = [];
    t.page.respond('DELETE', /^\/UserRatings\/Admin\/Detached\/[^/]+$/, (e) => {
        deletes.push(e.path.split('/').pop());
        rows = rows.filter((r) => r.Id !== e.path.split('/').pop());
        return null;
    }, 204);
    const purge = t.q('[data-jfur-rating="d1"] .jfur-det-purge');
    assert.equal(purge.getAttribute('aria-label'), 'Purge the rating of member 1 on Title 1 (2001)…');
    purge.click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-panel p')), 'Delete the rating of member 1 on Title 1 (2001) for good? It can\'t come back from an import; a snapshot is taken first.');
    t.q('.jfur-panel .jfur-data-cancel').click();
    await t.page.advance(10);
    assert.deepEqual(deletes, []);
    purge.click();
    await t.page.advance(10);
    t.q('.jfur-panel .jfur-data-confirm').click();
    await t.page.advance(10);
    assert.deepEqual(deletes, ['d1']);
    assert.deepEqual(t.all('.jfur-det-row').map((r) => r.getAttribute('data-jfur-rating')), ['d3']);
    assert.equal(t.changes.length, 2);

    // The last row purged: no row left to take the focus, so the reasons do (it was once left on the body).
    const last = t.q('[data-jfur-rating="d3"] .jfur-det-purge');
    last.focus();
    last.click();
    await t.page.advance(10);
    t.q('.jfur-panel .jfur-data-confirm').click();
    await t.page.advance(10);
    assert.deepEqual(deletes, ['d1', 'd3']);
    assert.equal(t.all('.jfur-det-row').length, 0);
    assert.ok(t.page.document.activeElement.isConnected && t.page.document.activeElement.getAttribute('role') === 'radio', 'the focus on the reasons');
    assert.equal(t.page.errors.length, 0);
});

test('a phone at 375 px: the rows wrap, every control at least 44 px high and the buttons the row\'s width (the admin styles)', { skip }, async () => {
    const t = await open();
    const css = t.page.document.getElementById('jfur-admin-styles').textContent;
    assert.ok(css.includes('.jfur-data-snapshot,.jfur-data-user,.jfur-det-candidate{display:flex;flex-wrap:wrap'), 'the rows wrap');
    assert.ok(css.includes('@media (max-width:599px){\n.jfur-data button,.jfur-data-choice,.jfur-data-file,.jfur-data-cancel,.jfur-data-confirm{min-height:44px}') || css.includes('@media (max-width:599px){.jfur-data button,.jfur-data-choice,.jfur-data-file,.jfur-data-cancel,.jfur-data-confirm{min-height:44px}'), '44 px under 600 px');
    assert.ok(css.includes('@media (pointer:coarse){.jfur-data button'), '44 px with a coarse pointer');
    assert.ok(/\.jfur-data-intro\{[^}]*overflow-wrap:anywhere/.test(css), 'long words wrap');
    assert.equal(t.page.errors.length, 0);
});

test('the Jellyfin Enhanced import: a dry run with the options, its report as text, a JE member mapped, the dry run again, then Import with its PlanToken after a question', { skip }, async () => {
    const t = await open({ je: jeDiagnostics() });
    const server = jobServer(t.page);
    server.script = (kind, e) => [{ Phase: 'Resolve', Processed: 1, Total: 7 }, { State: 'Succeeded', Phase: 'Done', Report: e.query.dryRun === 'false'
        ? jeReport({ DryRun: false, WouldCreate: 0, Created: 3, PlanToken: JE_PLAN, SafetySnapshot: 'userratings-import-20261102T100000Z.db', UnknownUserIds: [] })
        : jeReport(server.posts.length > 1 ? { UnknownUserIds: [] } : {}) }];
    t.page.respond('GET', '/UserRatings/Admin/Settings', { Version: 'v1', Settings: {}, Users: [{ Id: 'u1', Name: 'alice' }, { Id: 'u2', Name: '<b>bob</b>' }, { Id: 'u3', Name: 'gone', Missing: true }] });
    assert.ok(t.q('.jfur-data-je-import').disabled, 'Import waits for a dry run');
    assert.equal(text(t.q('.jfur-data-je-hint')), 'Run the dry run with these options first; Import then does what it showed.');
    t.q('.jfur-data-je-section [data-jfur-value="All"]').click();
    t.q('.jfur-data-je-section [data-jfur-value="First"]').click();

    t.q('.jfur-data-je-dryrun').click();
    await t.page.advance(10);
    assert.equal(server.posts.length, 1);
    assert.equal(server.posts[0].kind, 'ImportJellyfinEnhanced');
    assert.equal(server.posts[0].query.dryRun, 'true');
    assert.deepEqual(JSON.parse(server.posts[0].body), { Copies: 'First', StoreUnmatched: true, RecreatePurged: false, UserMap: {} });
    await t.page.advance(3000);
    const report = t.q('.jfur-data-je-report');
    assert.match(text(report), /^Dry run, nothing was changed\. Jellyfin Enhanced's file has 7 reviews\./);
    assert.deepEqual(Array.from(report.querySelectorAll('[data-jfur-count]')).map((li) => li.getAttribute('data-jfur-count')),
        ['Created', 'Matched', 'MatchedMultiple', 'Unmatched', 'StoredDetached', 'UnknownUser', 'Invalid', 'Adjusted', 'Clamped']);
    assert.equal(text(report.querySelector('[data-jfur-count="Created"]')), 'New ratings: 3');
    assert.match(text(report), /1 review matches more than one copy\. <b>Copies<\/b>/);
    assert.match(text(report), /1 review was dated ahead of this server's clock\./);
    assert.match(text(report), /Mirror review ratings to Jellyfin is off\.No spoiler flag per review\./);
    assert.equal(text(report.querySelector('.jfur-data-je-fields')), 'Fields in Jellyfin Enhanced\'s file: UserId, TmdbId, MediaType, Content, Rating, CreatedAt, UpdatedAt');
    assert.equal(text(report.querySelector('.jfur-data-je-leftout li')), 'bad:key: The key isn\'t <user>:<movie|tv>:<tmdb>.');
    const samples = report.querySelectorAll('.jfur-data-je-samples li');
    assert.equal(text(samples[0]), 'alice · <i>Heat</i> · 4.5 out of 5 · with a review · New ratings (1 more copy matches)');
    // Since 1.0.1 (in 1.0 a sample row read "? · <key> · No stars · stars only · Skipped: no stars
    // and no text"): an entry without its member or title is named by its key, and one with neither stars nor text says
    // only what Import does with it.
    assert.equal(text(samples[1]), 'Jellyfin Enhanced\'s entry k2 · Skipped: their member isn\'t a user here');
    assert.equal(text(samples[2]), 'Jellyfin Enhanced\'s entry ' + JE_USER + ':movie:<b>550</b> · Skipped: no stars and no text');
    assert.equal(text(samples[3]), 'Jellyfin Enhanced\'s entry k4 · 3 out of 5 · stars only · Skipped: their member isn\'t a user here');
    assert.equal(text(samples[4]), 'alice · Jellyfin Enhanced\'s entry k5 · No stars · with a review · No title here matches');
    assert.ok(Array.from(samples).every((li) => !/\?/.test(text(li))), 'no "?" for a member or a title');
    assert.equal(samples[2].querySelector('.jfur-isolate').getAttribute('dir'), 'auto', 'the key isolated');
    assert.equal(samples[0].querySelector('.jfur-isolate').getAttribute('dir'), 'auto');
    assert.equal(t.all('.jfur-data b, .jfur-data i').length, 0, 'markup is text');
    assert.equal(t.q('.jfur-data-je-import').disabled, false, 'the plan is ready');

    // The JE member who isn't a user here: Leave out, or a user of this server (a gone one isn't offered).
    const row = t.q('[data-jfur-je-user="' + JE_USER + '"]');
    assert.match(text(row), new RegExp('^Jellyfin Enhanced user ' + JE_USER + ': 1 review(?!s)'));
    assert.deepEqual(Array.from(row.querySelectorAll('.jfur-data-choice')).map(text), ['Leave out', 'alice', '<b>bob</b>']);
    assert.equal(row.querySelector('[data-jfur-value=""]').getAttribute('aria-checked'), 'true');
    row.querySelector('[data-jfur-value="u1"]').click();
    assert.ok(t.q('.jfur-data-je-import').disabled, 'a mapping changed: another dry run first');
    t.q('.jfur-data-je-dryrun').click();
    await t.page.advance(3010);
    assert.deepEqual(JSON.parse(server.posts[1].body).UserMap, { [JE_USER]: 'u1' });
    const mapped = t.q('[data-jfur-je-user="' + JE_USER + '"]');
    assert.ok(mapped, 'the mapped member stays listed');
    assert.equal(mapped.querySelector('[data-jfur-value="u1"]').getAttribute('aria-checked'), 'true');

    t.q('.jfur-data-je-import').click();
    await t.page.advance(10);
    const panel = t.q('.jfur-panel');
    assert.equal(text(panel.querySelector('p')), 'Import Jellyfin Enhanced\'s reviews as the dry run showed? A snapshot of the ratings as they are now is taken first; restoring it undoes the import.');
    assert.ok(t.page.document.activeElement === panel.querySelector('.jfur-data-cancel'), 'Cancel focused');
    assert.equal(server.posts.length, 2, 'nothing before the answer');
    panel.querySelector('.jfur-data-confirm').click();
    await t.page.advance(10);
    assert.equal(server.posts[2].query.dryRun, 'false');
    assert.equal(server.posts[2].query.ifPlan, JE_PLAN);
    assert.deepEqual(JSON.parse(server.posts[2].body), { Copies: 'First', StoreUnmatched: true, RecreatePurged: false, UserMap: { [JE_USER]: 'u1' } });
    await t.page.advance(3000);
    assert.match(text(t.q('.jfur-data-je-report')), /^Imported\. Jellyfin Enhanced's file has 7 reviews\./);
    assert.match(text(t.q('.jfur-data-je-report')), /The ratings from just before were kept as userratings-import-20261102T100000Z\.db/);
    assert.ok(t.changes.length >= 1, 'the page reads Diagnostics again');
    assert.equal(calls(t.page, 'GET', /^\/UserRatings\/Admin\/Snapshots$/).length, 2, 'the snapshots read again');
    assert.equal(calls(t.page, 'POST', /\/Admin\/Settings$/).length, 0, 'nothing written but the import');
    assert.equal(t.page.errors.length, 0);
});

test('no JE reviews here (404 JeDataNotFound), a file of another shape (400 UnsupportedFormat) and another job running: each in words, nothing imported', { skip }, async () => {
    const t = await open({ je: jeDiagnostics() });
    const server = jobServer(t.page);
    server.refuse = { title: 'Not Found', status: 404, detail: 'Jellyfin Enhanced has no reviews here.', code: 'JeDataNotFound' };
    t.q('.jfur-data-je-dryrun').click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-data-je-message')), 'Jellyfin Enhanced has no reviews on this server: its reviews.json isn\'t there.');
    server.refuse = { title: 'Bad Request', status: 400, detail: 'Its Reviews isn\'t an object.', code: 'UnsupportedFormat' };
    t.q('.jfur-data-je-dryrun').click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-data-je-message')), 'Jellyfin Enhanced\'s reviews.json isn\'t in a shape this version reads, so nothing was imported. Its Reviews isn\'t an object.');
    server.refuse = { title: 'Conflict', status: 409, detail: 'An export is running.', code: 'JobRunning' };
    t.q('.jfur-data-je-dryrun').click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-data-je-message')), 'Another data job is running. Wait for it to finish, or stop it.');
    assert.ok(t.q('.jfur-data-je-import').disabled);
    assert.equal(t.page.errors.length, 0);
});

test('the switch-over checklist from Diagnostics, every item as text with its state; read only (nothing is posted); not installed or complete in words', { skip }, async () => {
    const t = await open({ je: jeDiagnostics() });
    const list = t.q('.jfur-data-je-checklist');
    assert.equal(text(list.querySelector('.jfur-notice')), 'Required steps still to do: 2.');
    const items = Array.from(list.querySelectorAll('.jfur-data-je-item'));
    assert.deepEqual(items.map((li) => li.getAttribute('data-jfur-key')), ['ShowUserReviews', 'ShowReviews', 'ReviewTranslationEnabled', 'ActivityFeedShowReviewed']);
    assert.deepEqual(items.map((li) => text(li.querySelector('.jfur-data-je-state'))), ['To do: ', 'Optional: ', 'To do: ', 'Done: ']);
    assert.equal(text(items[0]), 'To do: Enable User Written Reviews <b>off</b>Its review panel goes at the next page load.');
    assert.equal(text(items[2].querySelector('.jfur-data-je-detail')), 'Still to do: a service URL is set.');
    assert.ok(items[0].classList.contains('jfur-data-je-todo') && !items[1].classList.contains('jfur-data-je-todo') && !items[3].classList.contains('jfur-data-je-todo'));
    assert.equal(t.all('.jfur-data b').length, 0, 'markup is text');
    assert.equal(text(t.q('.jfur-data-je-hiding-intro')), 'Jellyfin Enhanced leaves out the reviews of hidden users: yes; of disabled users: yes. User Ratings has its own two settings for this (the Settings tab); this sets them the same, after showing the change.');
    assert.equal(t.page.fetchLog.filter((e) => e.method !== 'GET').length, 0, 'nothing written');

    const done = await open({ je: jeDiagnostics({ Overlap: { Feature: 'Reviews', On: false, Banner: null, Complete: true, Checklist: [{ Key: 'ShowUserReviews', Text: 'Off', Done: true, Detail: null, Optional: false }] } }) });
    assert.equal(text(done.q('.jfur-data-je-checklist .jfur-notice')), 'Every required step is done: Jellyfin Enhanced\'s user reviews are off.');
    const none = await open({ je: { Installed: false, Overlap: { Feature: 'Reviews', On: false, Banner: null, Complete: true, Checklist: [] } } });
    assert.equal(text(none.q('.jfur-data-je-checklist')), 'Jellyfin Enhanced isn\'t installed, so there is nothing to switch off.');
    assert.ok(none.q('.jfur-data-je-hiding-row').classList.contains('hide'));
    const absent = await open();
    assert.equal(text(absent.q('.jfur-data-je-checklist')), 'Jellyfin Enhanced isn\'t installed, so there is nothing to switch off.');
});

// JE's settings file there but unreadable (half written) was once read as JE's defaults, so user
// reviews showed Done. Every item is Unknown now, never Done, with the reason first and the files that couldn't be read;
// Use the same hiding rules reads JE's settings again and changes nothing while they can't be read.
test('since 0.3.22: JE\'s settings unreadable: every step Unknown, never Done, the reason and the unreadable files shown; Use the same hiding rules changes nothing', { skip }, async () => {
    const reason = 'Couldn\'t read Jellyfin Enhanced\'s settings, so this can\'t be checked.';
    const unknown = (key, words, optional) => ({ Key: key, Text: words, Done: false, Detail: reason, NeedsRestart: false, Optional: optional, Unknown: true });
    const broken = jeDiagnostics({
        SettingsReadable: false,
        Settings: { ShowUserReviews: false, HideReviewsFromHiddenUsers: true, HideReviewsFromDisabledUsers: true },
        Problems: ['Jellyfin.Plugin.JellyfinEnhanced.xml isn\'t well-formed XML at line 3, position 8 (the file may be being written; try again).'],
        Overlap: { Feature: 'Reviews', On: false, Banner: null, Complete: false, Checklist: [unknown('ShowUserReviews', 'Enable User Written Reviews off', false), unknown('ShowReviews', 'The TMDB reviews section may stay on (optional)', true)] }
    });
    const t = await open({ je: broken });
    const list = t.q('.jfur-data-je-checklist');
    const notice = list.querySelector('.jfur-notice');
    assert.equal(text(notice), 'Couldn\'t read Jellyfin Enhanced\'s settings, so whether its user reviews are off can\'t be told: every step shows Unknown. Try again in a moment; if it stays, open Jellyfin Enhanced\'s settings page and save them again.');
    assert.equal(notice.getAttribute('role'), 'alert');
    assert.equal(text(t.q('.jfur-data-je-problems')), 'Jellyfin Enhanced\'s files that couldn\'t be read (the server log has the details):Jellyfin.Plugin.JellyfinEnhanced.xml isn\'t well-formed XML at line 3, position 8 (the file may be being written; try again).');
    const items = Array.from(list.querySelectorAll('.jfur-data-je-item'));
    assert.deepEqual(items.map((li) => text(li.querySelector('.jfur-data-je-state'))), ['Unknown: ', 'Unknown: ']);
    assert.deepEqual(items.map((li) => [li.getAttribute('data-jfur-done'), li.getAttribute('data-jfur-state'), li.classList.contains('jfur-data-je-todo')]), [['false', 'unknown', true], ['false', 'unknown', true]]);
    assert.equal(text(items[0].querySelector('.jfur-data-je-detail')), reason);
    assert.equal(text(t.q('.jfur-data-je-hiding-intro')), 'Jellyfin Enhanced\'s hiding rules can\'t be read while its settings can\'t be.');
    t.q('.jfur-data-je-hiding').click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-data-je-hiding-message')), 'Jellyfin Enhanced\'s settings couldn\'t be read, so its hiding rules aren\'t known: nothing was changed. Try again in a moment.');
    assert.equal(t.page.fetchLog.filter((e) => e.path === '/UserRatings/Admin/Settings').length, 0, 'User Ratings\' settings neither read nor written');

    // Readable when the tab was shown, unreadable by the click: read again, so nothing is changed from stale values.
    const later = await open({ je: jeDiagnostics() });
    assert.equal(later.all('.jfur-data-je-problems').length, 0);
    assert.equal(text(later.q('.jfur-data-je-checklist .jfur-notice')), 'Required steps still to do: 2.');
    later.page.respond('GET', '/UserRatings/Admin/Diagnostics', { Users: { DeletedUserPolicy: 'KeepAsOrphan', OrphanUsers: [] }, JellyfinEnhanced: broken });
    later.q('.jfur-data-je-hiding').click();
    await later.page.advance(10);
    assert.equal(text(later.q('.jfur-data-je-hiding-message')), 'Jellyfin Enhanced\'s settings couldn\'t be read, so its hiding rules aren\'t known: nothing was changed. Try again in a moment.');
    assert.deepEqual(Array.from(later.all('.jfur-data-je-item')).map((li) => li.getAttribute('data-jfur-state')), ['unknown', 'unknown']);
    assert.equal(later.page.fetchLog.filter((e) => e.path === '/UserRatings/Admin/Settings').length, 0);
    assert.equal(t.page.errors.length + later.page.errors.length, 0);
});

// role="radio" buttons with the keys of a radio group (the APG's pattern).
test('since 0.3.22: the choice buttons are a radio group: one Tab stop, the arrows choose round the end; the TV layout\'s D-pad Up and Down leave it', { skip }, async () => {
    const t = await open({ je: jeDiagnostics() });
    const radios = () => Array.from(t.q('.jfur-data-je-section [role="radiogroup"]').querySelectorAll('[role="radio"]'));
    const [first, all] = radios();
    assert.deepEqual([text(first), text(all)], ['Rate one copy (the one its member can see)', 'Rate every copy']);
    assert.deepEqual(radios().map((b) => [b.getAttribute('aria-checked'), b.getAttribute('tabindex')]), [['true', '0'], ['false', '-1']]);
    first.focus();
    assert.equal(t.page.key(first, 'ArrowRight'), false, 'handled, so jellyfin-web never moves the focus too');
    assert.deepEqual(radios().map((b) => [b.getAttribute('aria-checked'), b.getAttribute('tabindex')]), [['false', '-1'], ['true', '0']]);
    assert.ok(t.page.document.activeElement === all, 'the focus follows');
    t.page.key(all, 'ArrowRight');
    assert.equal(first.getAttribute('aria-checked'), 'true', 'round the end');
    t.page.key(first, 'ArrowUp');
    assert.equal(all.getAttribute('aria-checked'), 'true');
    t.page.key(all, 'ArrowDown');
    t.page.key(first, 'ArrowLeft');
    assert.equal(all.getAttribute('aria-checked'), 'true');
    assert.equal(t.page.key(all, 'Enter'), true, 'other keys are left alone');
    assert.equal(t.page.key(all, 'ArrowLeft', { ctrlKey: true }), true);
    assert.equal(all.getAttribute('aria-checked'), 'true');

    const tv = await open({ je: jeDiagnostics(), layout: 'tv' });
    const [tvFirst, tvAll] = Array.from(tv.q('.jfur-data-je-section [role="radiogroup"]').querySelectorAll('[role="radio"]'));
    assert.equal(tv.page.key(tvFirst, 'ArrowDown'), true, 'Down is the D-pad\'s, to leave the group');
    assert.equal(tvFirst.getAttribute('aria-checked'), 'true');
    assert.equal(tv.page.key(tvFirst, 'GamepadDPadRight', { keyCode: 206 }), false, 'a gamepad\'s Right chooses');
    assert.equal(tvAll.getAttribute('aria-checked'), 'true');
    assert.equal(t.page.errors.length + tv.page.errors.length, 0);
});

test('Use the same hiding rules: the Settings route\'s dry run of only what differs, a question with the change, then the save; the same already: nothing posted', { skip }, async () => {
    const t = await open({ je: jeDiagnostics() });
    t.page.respond('GET', '/UserRatings/Admin/Settings', { Version: 'v7', Settings: { HideReviewsFromHiddenUsers: false, HideReviewsFromDisabledUsers: true }, Users: [] });
    const posts = [];
    t.page.respond('POST', '/UserRatings/Admin/Settings', (e) => {
        posts.push({ query: e.query, body: e.body });
        return { Changes: [{ Field: 'HideReviewsFromHiddenUsers', Label: 'Leave out hidden users\' ratings', From: false, To: true, RestartNeeded: false }], Consequences: e.query.dryRun === 'false' ? [] : ['Hidden users\' ratings <i>stop</i> counting.'] };
    });
    t.q('.jfur-data-je-hiding').click();
    await t.page.advance(10);
    assert.equal(posts.length, 1);
    assert.deepEqual(posts[0].query, { version: 'v7' });
    assert.deepEqual(posts[0].body, { HideReviewsFromHiddenUsers: true });
    const panel = t.q('.jfur-panel');
    assert.match(text(panel), /Change User Ratings' settings so it leaves out the same reviews as Jellyfin Enhanced\?Leave out hidden users' ratings: false → trueHidden users' ratings <i>stop<\/i> counting\./);
    assert.equal(panel.querySelectorAll('i').length, 0, 'markup is text');
    assert.ok(t.page.document.activeElement === panel.querySelector('.jfur-data-cancel'), 'Cancel focused');
    panel.querySelector('.jfur-data-confirm').click();
    await t.page.advance(10);
    assert.equal(posts.length, 2);
    assert.deepEqual(posts[1].query, { version: 'v7', dryRun: 'false' });
    assert.deepEqual(posts[1].body, { HideReviewsFromHiddenUsers: true });
    assert.equal(text(t.q('.jfur-data-je-hiding-message')), 'Saved: User Ratings leaves out the same reviews as Jellyfin Enhanced.');

    t.page.respond('GET', '/UserRatings/Admin/Settings', { Version: 'v8', Settings: { HideReviewsFromHiddenUsers: true, HideReviewsFromDisabledUsers: true }, Users: [] });
    t.q('.jfur-data-je-hiding').click();
    await t.page.advance(10);
    assert.equal(posts.length, 2, 'the same already: nothing posted');
    assert.equal(text(t.q('.jfur-data-je-hiding-message')), 'User Ratings already leaves out the same reviews as Jellyfin Enhanced.');
    assert.equal(t.page.errors.length, 0);
});

test('an option changed while the dry run runs: its PlanToken isn\'t these options\', so Import waits for another dry run', { skip }, async () => {
    const t = await open({ je: jeDiagnostics() });
    const server = jobServer(t.page);
    server.script = () => [{ Phase: 'Resolve', Processed: 1, Total: 7 }, { State: 'Succeeded', Phase: 'Done', Report: jeReport() }];
    t.page.respond('GET', '/UserRatings/Admin/Settings', { Version: 'v1', Settings: {}, Users: [] });
    t.q('.jfur-data-je-dryrun').click();
    await t.page.advance(10);
    t.q('.jfur-data-je-section [data-jfur-value="All"]').click();
    await t.page.advance(3000);
    assert.match(text(t.q('.jfur-data-je-report')), /^Dry run, nothing was changed\./);
    assert.ok(t.q('.jfur-data-je-import').disabled, 'the plan was made with Copies = First');
    assert.equal(text(t.q('.jfur-data-je-hint')), 'Run the dry run with these options first; Import then does what it showed.');
    t.q('.jfur-data-je-dryrun').click();
    await t.page.advance(3010);
    assert.equal(JSON.parse(server.posts[1].body).Copies, 'All');
    assert.equal(t.q('.jfur-data-je-import').disabled, false);
    assert.equal(t.page.errors.length, 0);
});

test('Export for Jellyfin Enhanced: fetched with the session\'s authorization, saved as reviews.json; the counts and what was left out in words; a refusal with Try again', { skip }, async () => {
    const t = await open();
    const saved = [];
    t.page.document.body.addEventListener('click', (e) => {
        if (e.target.tagName === 'A') {
            saved.push({ name: e.target.getAttribute('download'), href: e.target.getAttribute('href') });
        }
    });
    const route = /^\/UserRatings\/Admin\/Export\/JellyfinEnhanced$/;
    t.page.respond('GET', route, '{"Reviews":{}}', 200, { headers: { 'X-UserRatings-Exported': '1', 'X-UserRatings-Skipped': '3', 'X-UserRatings-Skipped-Detail': 'Detached=1,NoTmdbId=2,<b>Other</b>=9', 'X-UserRatings-Merged-Copies': '2' } });
    assert.equal(text(t.q('.jfur-data-je-back-title')), 'Going back to Jellyfin Enhanced');
    const first = t.page.fetchLog.length;
    t.q('.jfur-data-je-export').click();
    await t.page.advance(10);

    const got = calls(t.page, 'GET', route);
    assert.equal(got.length, 1);
    assert.match(got[0].headers.Authorization, /^MediaBrowser .*Token="test-token"$/);
    assert.deepEqual(got[0].query, { leaveOutRestricted: 'true' }, 'the choice\'s default, never a token in the link');
    assert.deepEqual(saved.map((s) => s.name), ['reviews.json']);
    assert.match(saved[0].href, /^blob:/);
    assert.equal(t.all('a[download]').length, 0, 'the link goes once clicked');
    assert.deepEqual(Array.from(t.all('.jfur-data-je-export-status p')).map(text), [
        'Saved reviews.json: 1 review.',
        'Left out, 3 ratings: detached and not from Jellyfin Enhanced (1); no TMDb id (2).',
        '2 ratings of another copy of a title went into the same review (the newer one is saved).'
    ]);
    assert.equal(t.all('.jfur-data-je-export-status b').length, 0, 'an unknown reason is never shown, as markup or text');
    assert.ok(!t.q('.jfur-data-je-export').disabled);
    assert.deepEqual(t.page.fetchLog.slice(first).map((e) => e.method + ' ' + e.path), ['GET /UserRatings/Admin/Export/JellyfinEnhanced'], 'one GET, nothing posted');

    // Nothing left out: one line, in the plural.
    t.page.respond('GET', route, '{"Reviews":{}}', 200, { headers: { 'X-UserRatings-Exported': '4', 'X-UserRatings-Skipped': '0', 'X-UserRatings-Skipped-Detail': '', 'X-UserRatings-Merged-Copies': '0' } });
    t.q('.jfur-data-je-export').click();
    await t.page.advance(10);
    assert.deepEqual(Array.from(t.all('.jfur-data-je-export-status p')).map(text), ['Saved reviews.json: 4 reviews.']);

    // Refused: the server's words with Try again, which asks again; nothing saved.
    t.page.respond('GET', route, { title: 'Service Unavailable', status: 503, detail: 'The ratings data can\'t be read now.' }, 503);
    t.q('.jfur-data-je-export').click();
    await t.page.advance(10);
    assert.match(text(t.q('.jfur-data-je-export-message .jfur-notice-text')), /The ratings data can't be read now\./);
    assert.equal(t.q('.jfur-data-je-export-message .jfur-notice').getAttribute('role'), 'alert');
    assert.equal(text(t.q('.jfur-data-je-export-status')), '');
    assert.equal(saved.length, 2);
    t.q('.jfur-data-je-export-message .jfur-notice-action').click();
    await t.page.advance(10);
    assert.equal(calls(t.page, 'GET', route).length, 4);
    assert.equal(t.page.errors.length, 0);
});

test('since 0.3.24: the JE export leaves out titles not every member can see unless the choice says otherwise; archived, restricted and over-limit counts in words; the focus back on the button', { skip }, async () => {
    const t = await open();
    t.page.document.body.addEventListener('click', (e) => {
        if (e.target.tagName === 'A') {
            e.preventDefault();
        }
    });
    const route = /^\/UserRatings\/Admin\/Export\/JellyfinEnhanced$/;
    t.page.respond('GET', route, '{"Reviews":{}}', 200, { headers: { 'X-UserRatings-Exported': '5', 'X-UserRatings-Skipped': '3', 'X-UserRatings-Skipped-Detail': 'Archived=1,RestrictedLibrary=2', 'X-UserRatings-Merged-Copies': '0', 'X-UserRatings-Over-Je-Limit': '2' } });
    // The privacy note before the choice, in words; the choice a radio group, Leave out chosen.
    assert.match(text(t.q('.jfur-data-je-back-privacy')), /Library access doesn't carry over: Jellyfin Enhanced gives every review of a title to any signed-in user/);
    // 1.0.1: its hiding rules checked while the server still runs (once it is stopped and the file in place, the next start serves it).
    assert.match(text(t.q('.jfur-data-je-back-privacy')), /Check its two hiding rules \(above\) while the server still runs, before you stop it to put the file in place\.$/);
    assert.match(text(t.q('.jfur-data-je-section')), /run the import above once more first \(a dry run, then Import\)/);
    const group = Array.from(t.all('.jfur-data-je-section [role="radiogroup"]')).pop();
    const [leave, include] = Array.from(group.querySelectorAll('[role="radio"]'));
    assert.deepEqual([text(leave), text(include)], ['Leave out reviews of titles that not every member can see', 'Include them (every signed-in user can read them in Jellyfin Enhanced)']);
    assert.deepEqual([leave.getAttribute('aria-checked'), include.getAttribute('aria-checked')], ['true', 'false']);

    const button = t.q('.jfur-data-je-export');
    button.focus();
    button.click();
    assert.ok(button.disabled, 'off while it runs');
    // A browser moves the focus off a focused button that is disabled (the focus fixup rule); the fake page doesn't.
    button.blur();
    await t.page.advance(10);
    assert.deepEqual(calls(t.page, 'GET', route).map((e) => e.query), [{ leaveOutRestricted: 'true' }]);
    assert.deepEqual(Array.from(t.all('.jfur-data-je-export-status p')).map(text), [
        'Saved reviews.json: 5 reviews.',
        'Left out, 3 ratings: archived and not from Jellyfin Enhanced (1); a title not every member can see (2).',
        '2 reviews are longer than the 2,000 characters Jellyfin Enhanced saves: it shows them whole, but one edited there must be shortened before it saves.'
    ]);
    assert.ok(!button.disabled);
    assert.ok(t.page.document.activeElement === button, 'the focus back on Export for Jellyfin Enhanced');

    // Include them: the next export asks for every title; one over the limit in the singular.
    include.click();
    t.page.respond('GET', route, '{"Reviews":{}}', 200, { headers: { 'X-UserRatings-Exported': '7', 'X-UserRatings-Skipped': '0', 'X-UserRatings-Skipped-Detail': '', 'X-UserRatings-Merged-Copies': '0', 'X-UserRatings-Over-Je-Limit': '1' } });
    button.click();
    await t.page.advance(10);
    assert.deepEqual(calls(t.page, 'GET', route).map((e) => e.query.leaveOutRestricted), ['true', 'false']);
    assert.equal(text(Array.from(t.all('.jfur-data-je-export-status p')).pop()), '1 review is longer than the 2,000 characters Jellyfin Enhanced saves: it shows it whole, but edited there it must be shortened before it saves.');
    // Clicked without the focus (a mouse after Tab elsewhere): the focus is left where it is.
    const other = t.q('.jfur-data-je-dryrun');
    other.focus();
    button.click();
    await t.page.advance(10);
    assert.ok(t.page.document.activeElement === other, 'never taken from where it went');
    assert.equal(t.page.errors.length, 0);
});

test('since 0.3.24: Export (the envelope) gives the focus back to its button after the job', { skip }, async () => {
    const t = await open();
    const server = jobServer(t.page);
    server.script = () => [{ Phase: 'Read' }, { State: 'Succeeded', Phase: 'Done', HasFile: true, Report: { Ratings: 1, Reviews: 1, Detached: 0, Archived: 0, FileName: 'userratings-export-20261102-120000.json' } }];
    t.page.document.body.addEventListener('click', (e) => {
        if (e.target.tagName === 'A') {
            e.preventDefault();
        }
    });
    const button = t.q('.jfur-data-export');
    button.focus();
    button.click();
    assert.ok(button.disabled);
    button.blur();
    await t.page.advance(3000);
    assert.equal(server.posts.length, 1);
    assert.ok(!button.disabled);
    assert.ok(t.page.document.activeElement === button, 'the focus back on Export');
    assert.equal(t.page.errors.length, 0);
});

test('since 0.3.24: in a right-to-left page Left chooses the next choice and Right the one before', { skip }, async () => {
    // The detached list's reasons: five choices, so the next and the one before differ (in a group of two they don't).
    const t = await open({ tab: 'Detached' });
    const real = globalThis.window.getComputedStyle;
    globalThis.window.getComputedStyle = (el) => Object.assign({}, real ? real(el) : {}, { direction: 'rtl' });
    const radios = () => Array.from(t.q('#UserRatingsDetachedListRoot [role="radiogroup"]').querySelectorAll('[role="radio"]'));
    const checked = () => radios().findIndex((b) => b.getAttribute('aria-checked') === 'true');
    assert.equal(radios().length, 5);
    assert.equal(checked(), 0);
    radios()[0].focus();
    assert.equal(t.page.key(radios()[0], 'ArrowLeft'), false);
    assert.equal(checked(), 1, 'Left points forward in RTL');
    assert.ok(t.page.document.activeElement === radios()[1]);
    t.page.key(radios()[1], 'ArrowLeft');
    assert.equal(checked(), 2);
    t.page.key(radios()[2], 'ArrowRight');
    assert.equal(checked(), 1, 'Right points back');
    t.page.key(radios()[1], 'ArrowRight');
    t.page.key(radios()[0], 'ArrowRight');
    assert.equal(checked(), 4, 'round the end, backwards');
    t.page.key(radios()[4], 'ArrowDown');
    assert.equal(checked(), 0, 'Down is the next one whatever the direction');
    await t.page.advance(10);
    assert.equal(t.page.errors.length, 0);
});
