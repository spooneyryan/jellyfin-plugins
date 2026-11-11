// User Ratings' Settings tab: the AdminScript module as the kit builds it from Web/admin, imported
// into the kit's fake page and mounted into a panel with { tab: 'Settings' }, GET and POST /UserRatings/Admin/Settings
// answered by a scripted server that checks the version as the real one does. WebTests runs it:
//   KIT_CLIENT_SCRIPT=<the AdminScript module> KIT_PREFIX=jfur node --test plugins/UserRatings/tests/Jellyfin.Plugin.UserRatings.Tests/Web/settings.test.mjs
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
const folder = skip ? null : fs.mkdtempSync(path.join(os.tmpdir(), 'jfur-settings-'));
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

// Every setting in the tab's order, with its default (Configuration/PluginConfiguration.cs).
const DEFAULTS = {
    EnableRatings: true, EnableReviews: true, WhoCanRate: 'AllUsers', RaterUserIds: [],
    RateableItemTypes: ['Movie', 'Series', 'Season', 'Episode', 'Video', 'MusicVideo', 'MusicAlbum', 'Book', 'AudioBook'],
    RequirePlayedToRate: false, MaxReviewLength: 5000, AllowSpoilerFlag: true,
    HideReviewsFromHiddenUsers: false, HideReviewsFromDisabledUsers: true, ShowReviewerAvatars: true,
    ShowAverageChip: true, ShowHistogram: true, MinRatingsForAverage: 1, SectionExpandedByDefault: true, ShowDetailButton: 'Auto', ReviewsPageSize: 20,
    EnableWebUi: true, ReportWebAnchorProblems: true,
    SnapshotsToKeep: 7, DetachedRetentionDays: 90, ModerationLogDays: 365, DeletedUserPolicy: 'Delete',
    ShowJellyfinEnhancedBanner: true,
    ShowOnPosters: false, PosterBadgeContent: 'Average', PosterBadgePosition: 'top-start', EnableTopRatedPage: true, RegisterPluginPages: true,
    PageHostMode: 'FallbackOnly', ShowFallbackNav: true, MirrorToUserData: false
};
const ORDER = Object.keys(DEFAULTS);
const KINDS = ['Movie', 'Series', 'Season', 'Episode', 'Video', 'MusicVideo', 'Trailer', 'MusicAlbum', 'Audio', 'Book', 'AudioBook', 'BoxSet'];
const RESTART = ['EnableTopRatedPage', 'RegisterPluginPages', 'PageHostMode'];
const ALICE = 'a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1';
const BOB = 'b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2';
const SARA = 'c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3';
const GONE = 'd4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4';
const USERS = [
    { Id: ALICE, Name: 'Alice', Missing: false },
    { Id: BOB, Name: '<img src=x onerror=alert(1)>', Missing: false },
    { Id: SARA, Name: 'שרה‮ evil', Missing: false }
];
const LABELS = { EnableReviews: 'Reviews', EnableTopRatedPage: 'Top Rated page', ShowHistogram: 'Ratings histogram', ReviewsPageSize: 'Reviews per page', ShowDetailButton: 'Rate button', WhoCanRate: 'Who can rate and review', RaterUserIds: 'Users who can rate' };
const CHANGED = 'The settings were saved since you opened them (by someone else, or in another window): Ratings histogram. Reload them, then make your change again. Nothing was saved.';

const clone = (value) => JSON.parse(JSON.stringify(value));
const valueText = (v) => (typeof v === 'boolean' ? (v ? 'On' : 'Off') : Array.isArray(v) ? (v.length ? v.join(', ') : 'Nobody') : String(v));

// The Settings route as the server answers it (Services/RatingsSettingsService.cs): a dry run unless dryRun=false; a
// version that isn't the settings' is the 409 SettingsChanged for both; server.refuse(e) answers a call with a problem
// instead ({ status, body }); server.consequences(body) is the dry run's list.
function settingsServer(page, opts) {
    opts = opts || {};
    const server = {
        settings: Object.assign(clone(DEFAULTS), opts.settings || {}),
        users: opts.users || USERS,
        problems: opts.problems || [],
        version: 1,
        reads: 0,
        dryRuns: [],
        saves: [],
        refuse: null,
        readStatus: null,
        consequences: opts.consequences || (() => [])
    };
    server.versionText = () => 'v' + String(server.version).padStart(15, '0');
    page.respond('GET', '/UserRatings/Admin/Settings', () => {
        server.reads++;
        if (server.readStatus) {
            return { title: 'x', status: server.readStatus, detail: 'The ratings database is busy; try again in a moment.' };
        }

        return {
            Settings: clone(server.settings), Defaults: clone(DEFAULTS), Version: server.versionText(), Users: clone(server.users),
            ItemKinds: KINDS.slice(), RestartFields: RESTART.slice(), Problems: server.problems.slice()
        };
    }, () => server.readStatus || 200);
    let answer = null;
    page.respond('POST', '/UserRatings/Admin/Settings', () => answer.body, (e) => {
        answer = post(e);
        return answer.status;
    });

    function post(e) {
        if (server.refuse) {
            const refusal = server.refuse(e);
            if (refusal) {
                return refusal;
            }
        }

        if (e.query.version !== server.versionText()) {
            return { status: 409, body: { title: 'Conflict', status: 409, detail: CHANGED, code: 'SettingsChanged' } };
        }

        const body = clone(e.body);
        const changes = Object.keys(body).map((f) => ({ Field: f, Label: LABELS[f] || f, From: valueText(server.settings[f]), To: valueText(body[f]), RestartNeeded: RESTART.includes(f) }));
        const dry = e.query.dryRun !== 'false';
        if (dry) {
            server.dryRuns.push(body);
        } else {
            server.saves.push(body);
            Object.assign(server.settings, body);
            server.version++;
        }

        return {
            status: 200,
            body: { DryRun: dry, Applied: !dry, Version: server.versionText(), Settings: clone(server.settings), Changes: changes, Consequences: server.consequences(body), RestartNeeded: changes.some((c) => c.RestartNeeded) }
        };
    }

    return server;
}

// The fake page as the dashboard has it, the module imported (its own copy) and mounted into the Settings panel; the first
// read goes once the page's timer runs.
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
    const server = settingsServer(page, options);
    if (options.before) {
        options.before(server);
    }

    const copy = path.join(folder, 'admin-' + (++copies) + '.mjs');
    fs.writeFileSync(copy, source);
    const module = await import(pathToFileURL(copy).href);
    const view = page.element('div', { id: 'UserRatingsSettingsRoot' });
    page.document.body.append(view);
    const mounted = module.mount(view, { page: null, tab: 'Settings' });
    const before = page.fetchLog.length;
    await page.advance(10);
    const q = (selector) => page.document.querySelector(selector);
    const all = (selector) => page.document.querySelectorAll(selector);
    const row = (field) => q('[data-jfur-field="' + field + '"]');
    return { page, view, module, mounted, before, server, q, all, row };
}

const text = (node) => (node ? node.textContent : null);
const shown = (node) => !!node && node.getClientRects().length > 0;
const calls = (page, method) => page.fetchLog.filter((e) => e.method === method && e.path === '/UserRatings/Admin/Settings');
const control = (t, field) => t.row(field).querySelector('.jfur-set-switch, .jfur-set-number, .jfur-set-choices, .jfur-set-checks');
const choice = (t, field, value) => t.row(field).querySelector('[data-jfur-value="' + value + '"]');
const help = (t, field) => text(t.row(field).querySelector('.jfur-set-help'));
const error = (t, field) => {
    const node = t.row(field).querySelector('.jfur-set-error');
    return shown(node) ? node.textContent : null;
};

function type(t, field, value) {
    const input = control(t, field);
    input.value = value;
    t.page.dispatch(input, 'input');
}

async function save(t) {
    const button = t.q('.jfur-set-save');
    button.focus();
    button.click();
    await t.page.advance(10);
}

test('importing adds nothing and sends nothing; mount builds the tab, and the first read goes after it returns: every setting, in its group, in order', { skip }, async () => {
    const t = await open();
    assert.equal(t.before, 0, 'nothing sent by import and mount');
    assert.equal(typeof t.mounted.refresh, 'function');
    assert.ok(t.page.document.getElementById('jfur-admin-styles'), 'the admin styles');
    assert.equal(calls(t.page, 'GET').length, 1);
    assert.deepEqual(plain(t.all('.jfur-set-group-title').map((h) => h.textContent)), ['Ratings and reviews', 'Reviewers', 'Details page', 'Web client', 'Data and retention', 'Jellyfin Enhanced', 'Coming with version 1.1']);
    assert.deepEqual(plain(t.all('.jfur-set-row').map((r) => r.getAttribute('data-jfur-field'))), ORDER);
    assert.equal(ORDER.length, 32);
    assert.equal(shown(t.q('.jfur-set-save')), true);
    assert.equal(t.q('.jfur-set-undo').disabled, true, 'nothing to undo');
    // Not one select: each choice is a button the TV layout's D-pad reaches, OK changes it.
    assert.equal(t.view.querySelectorAll('select').length, 0);
    t.mounted.refresh();
    await t.page.advance(10);
    assert.equal(calls(t.page, 'GET').length, 2, 'refresh reads again');
    assert.equal(t.page.errors.length, 0);
});

test('each setting has its label, its help and its default, as text; a switch, a group of choices, a number with its unit; the restart settings say so; 1.1\'s note; Mirror can\'t be turned on', { skip }, async () => {
    const t = await open();
    const ratings = control(t, 'EnableRatings');
    assert.equal(ratings.tagName, 'BUTTON');
    assert.equal(ratings.getAttribute('role'), 'switch');
    assert.equal(ratings.getAttribute('aria-checked'), 'true');
    assert.equal(text(ratings.querySelector('.jfur-set-switch-label')), 'Ratings');
    assert.equal(ratings.querySelector('.jfur-set-switch-state').getAttribute('aria-hidden'), 'true', 'the state is aria-checked\'s, not read twice');
    assert.equal(help(t, 'EnableRatings'), 'Off hides every star, review and average in the web client. Nothing is deleted, and the API keeps working. Default: On.');
    assert.equal(help(t, 'HideReviewsFromHiddenUsers'), 'Ratings and reviews of users hidden from the sign-in screen show only to them and to moderators, and aren\'t counted. Default: Off.');
    // The described-by ids are the field's own help, note and message.
    for (const field of ORDER) {
        const c = control(t, field);
        assert.ok(c, field);
        const ids = c.getAttribute('aria-describedby').split(' ');
        assert.equal(ids.length, 3, field);
        assert.ok(ids.every((id) => t.page.document.getElementById(id)), field);
        assert.ok(help(t, field).includes('Default: '), field);
    }

    const who = control(t, 'WhoCanRate');
    assert.equal(who.getAttribute('role'), 'radiogroup');
    assert.equal(text(t.page.document.getElementById(who.getAttribute('aria-labelledby'))), 'Who can rate and review');
    assert.deepEqual(plain(who.querySelectorAll('button').map((b) => [b.getAttribute('role'), b.textContent, b.getAttribute('aria-checked')])), [['radio', 'Every user', 'true'], ['radio', 'Only the users chosen below', 'false']]);
    assert.equal(help(t, 'DeletedUserPolicy'), 'What happens to their ratings and reviews. Users deleted before a change stay as they are. Default: Delete their ratings and reviews.');
    const length = control(t, 'MaxReviewLength');
    assert.equal(length.tagName, 'INPUT');
    assert.equal(length.getAttribute('inputmode'), 'numeric');
    assert.equal(length.value, '5000');
    assert.equal(text(t.row('MaxReviewLength').querySelector('.jfur-set-unit')), 'characters');
    assert.equal(help(t, 'MaxReviewLength'), 'From 2,000 to 20,000. Lowering it never shortens a review already written; it must fit when it is edited. Default: 5000.');
    assert.equal(help(t, 'ModerationLogDays'), 'From 0 to 3,650. The daily maintenance deletes older rows; 0 keeps every row. The log holds who did what, never a review\'s text. Default: 365.');
    const kinds = control(t, 'RateableItemTypes');
    assert.equal(kinds.getAttribute('role'), 'group');
    assert.deepEqual(plain(kinds.querySelectorAll('button').map((b) => [b.getAttribute('role'), b.textContent, b.getAttribute('aria-checked')])), [
        ['checkbox', 'Movies', 'true'], ['checkbox', 'Series', 'true'], ['checkbox', 'Seasons', 'true'], ['checkbox', 'Episodes', 'true'], ['checkbox', 'Videos', 'true'],
        ['checkbox', 'Music videos', 'true'], ['checkbox', 'Trailers', 'false'], ['checkbox', 'Albums', 'true'], ['checkbox', 'Tracks', 'false'], ['checkbox', 'Books', 'true'],
        ['checkbox', 'Audiobooks', 'true'], ['checkbox', 'Collections', 'false']]);
    assert.ok(help(t, 'RateableItemTypes').endsWith(' Default: Movies, Series, Seasons, Episodes, Videos, Music videos, Albums, Books, Audiobooks.'));
    assert.ok(help(t, 'RaterUserIds').endsWith(' Default: nobody chosen.'));
    for (const field of RESTART) {
        assert.ok(help(t, field).endsWith(' Takes effect after a restart.'), field);
    }

    assert.ok(!help(t, 'ShowFallbackNav').includes('restart'));
    assert.equal(text(t.q('[data-jfur-group="later"] .jfur-set-group-note')), 'Kept for what version 1.1 brings: rating badges on posters, the Top Rated page, and the copy into Jellyfin\'s own ratings. They change nothing yet.');
    assert.ok(t.q('[data-jfur-group="je"] .jfur-set-group-note') === null, 'the banner setting works: no "later" note');
    const mirror = control(t, 'MirrorToUserData');
    assert.equal(mirror.disabled, true);
    assert.equal(mirror.getAttribute('aria-checked'), 'false');
    assert.ok(help(t, 'MirrorToUserData').includes('It can\'t be turned on until that is checked. This can\'t be turned on yet. Default: Off.'));
    mirror.click();
    assert.equal(mirror.getAttribute('aria-checked'), 'false', 'it stays off');
    assert.equal(t.page.errors.length, 0);
});

test('every value from the server is text and every user name isolated: markup in a name stays text, an RTL name in its own element; a user the settings hold who is gone says so; the server\'s problems as text', { skip }, async () => {
    const t = await open({
        settings: { WhoCanRate: 'SelectedUsers', RaterUserIds: [ALICE, GONE] },
        users: USERS.concat([{ Id: GONE, Name: '', Missing: true }]),
        problems: ['PosterBadgePosition is "<b>middle</b>", which isn\'t one of its values: top-start is used instead.']
    });
    const users = control(t, 'RaterUserIds').querySelectorAll('button');
    assert.deepEqual(plain(users.map((b) => [b.textContent, b.getAttribute('aria-checked')])), [
        ['Alice', 'true'], ['<img src=x onerror=alert(1)>', 'false'], ['שרה‮ evil', 'false'], ['A user no longer on the server (' + GONE + ')', 'true']]);
    assert.ok(t.view.querySelector('img') === null);
    const names = users.map((b) => b.querySelector('.jfur-set-check-text'));
    assert.deepEqual(plain(names.slice(0, 3).map((n) => [n.className.includes('jfur-isolate'), n.getAttribute('dir')])), [[true, 'auto'], [true, 'auto'], [true, 'auto']]);
    assert.equal(shown(t.row('RaterUserIds')), true, 'shown for "Only the users chosen below"');
    assert.equal(text(t.q('.jfur-set-problem-list li')), 'PosterBadgePosition is "<b>middle</b>", which isn\'t one of its values: top-start is used instead.');
    assert.ok(t.q('.jfur-set-problem-list b') === null);
    assert.equal(error(t, 'RaterUserIds'), null, 'Alice is a user of the server');
    assert.equal(t.page.errors.length, 0);
});

test('a switch, a choice and a number change their value; Save sends only what changed, a dry run with the version, then the save; no consequences, no question; the toast; the settings read again', { skip }, async () => {
    const t = await open();
    const histogram = control(t, 'ShowHistogram');
    histogram.click();
    assert.equal(histogram.getAttribute('aria-checked'), 'false');
    assert.equal(text(histogram.querySelector('.jfur-set-switch-state')), 'Off');
    choice(t, 'ShowDetailButton', 'Always').click();
    assert.deepEqual(plain(control(t, 'ShowDetailButton').querySelectorAll('button').map((b) => b.getAttribute('aria-checked'))), ['false', 'true', 'false']);
    type(t, 'ReviewsPageSize', '30');
    assert.equal(error(t, 'ReviewsPageSize'), null);
    assert.equal(t.q('.jfur-set-undo').disabled, false);
    const button = t.q('.jfur-set-save');
    button.focus();
    button.click();
    button.click();
    await t.page.advance(10);

    const posts = calls(t.page, 'POST');
    assert.equal(posts.length, 2, 'a second click while it waited sent nothing');
    assert.deepEqual(plain(posts[0].query), { version: 'v000000000000001' });
    assert.deepEqual(plain(posts[0].body), { ShowHistogram: false, ShowDetailButton: 'Always', ReviewsPageSize: 30 });
    assert.deepEqual(plain(posts[1].query), { version: 'v000000000000001', dryRun: 'false' });
    assert.deepEqual(plain(posts[1].body), plain(posts[0].body));
    assert.ok(t.q('.jfur-panel') === null, 'no question without consequences');
    assert.deepEqual(plain(t.all('.jfur-toast').map((n) => n.textContent)), ['Settings saved.']);
    assert.equal(calls(t.page, 'GET').length, 2, 'read again');
    assert.equal(control(t, 'ShowHistogram').getAttribute('aria-checked'), 'false');
    assert.equal(control(t, 'ReviewsPageSize').value, '30');
    assert.equal(t.q('.jfur-set-undo').disabled, true);
    assert.ok(t.page.document.activeElement === t.q('.jfur-set-save'), 'the focus stays on Save');
    // The next save is made from the new version.
    choice(t, 'ShowDetailButton', 'Never').click();
    await save(t);
    assert.deepEqual(plain(calls(t.page, 'POST')[2].query), { version: 'v000000000000002' });
    assert.deepEqual(t.server.saves.length, 2);
    assert.equal(t.page.errors.length, 0);
});

test('Undo puts every value back; Save with nothing changed says so and sends nothing', { skip }, async () => {
    const t = await open();
    control(t, 'EnableReviews').click();
    type(t, 'MaxReviewLength', '1');
    choice(t, 'DeletedUserPolicy', 'Anonymize').click();
    t.q('.jfur-set-undo').click();
    assert.equal(control(t, 'EnableReviews').getAttribute('aria-checked'), 'true');
    assert.equal(control(t, 'MaxReviewLength').value, '5000');
    assert.equal(error(t, 'MaxReviewLength'), null, 'its message goes with its value');
    assert.equal(choice(t, 'DeletedUserPolicy', 'Delete').getAttribute('aria-checked'), 'true');
    assert.ok(t.page.document.activeElement === t.q('.jfur-set-save'));
    await save(t);
    assert.equal(calls(t.page, 'POST').length, 0);
    assert.equal(text(t.q('.jfur-set-status')), 'Nothing changed.');
    // A value changed and changed back is no change either.
    control(t, 'ShowHistogram').click();
    control(t, 'ShowHistogram').click();
    await save(t);
    assert.equal(calls(t.page, 'POST').length, 0);
});

test('a number out of its range or not a number: the message under it as it is typed, aria-invalid; Save sends nothing and puts the focus on the first setting to fix; fixed, the message goes', { skip }, async () => {
    const t = await open();
    // Only the field takes the number class (its 8em width); the row is jfur-set-row-number, as wide as the tab.
    assert.deepEqual(plain(t.all('.jfur-set-number').map((n) => n.tagName.toLowerCase())), ['input', 'input', 'input', 'input', 'input', 'input']);
    assert.equal(t.row('MaxReviewLength').classList.contains('jfur-set-row-number'), true);
    type(t, 'MaxReviewLength', '1999');
    assert.equal(error(t, 'MaxReviewLength'), 'Enter a whole number from 2000 to 20000.');
    assert.equal(control(t, 'MaxReviewLength').getAttribute('aria-invalid'), 'true');
    assert.ok(control(t, 'MaxReviewLength').getAttribute('aria-describedby').split(' ').includes(t.row('MaxReviewLength').querySelector('.jfur-set-error').id));
    type(t, 'ModerationLogDays', '3651');
    assert.equal(error(t, 'ModerationLogDays'), 'Enter a whole number from 0 to 3650.');
    type(t, 'ReviewsPageSize', '2.5');
    assert.equal(error(t, 'ReviewsPageSize'), 'Enter a whole number from 5 to 50.');
    type(t, 'SnapshotsToKeep', 'seven');
    assert.equal(error(t, 'SnapshotsToKeep'), 'Enter a whole number from 0 to 365.');
    assert.equal(control(t, 'SnapshotsToKeep').value, 'seven', 'what was typed stays');
    await save(t);
    assert.equal(calls(t.page, 'POST').length, 0);
    assert.equal(text(t.q('.jfur-set-status')), 'Some settings need fixing (marked below); nothing was saved.');
    assert.ok(t.page.document.activeElement === control(t, 'MaxReviewLength'), 'the first setting to fix');
    type(t, 'MaxReviewLength', '20000');
    type(t, 'ModerationLogDays', '0');
    type(t, 'ReviewsPageSize', '5');
    type(t, 'SnapshotsToKeep', ' 0 ');
    assert.equal(error(t, 'MaxReviewLength'), null);
    assert.equal(control(t, 'MaxReviewLength').getAttribute('aria-invalid'), null);
    await save(t);
    assert.deepEqual(plain(calls(t.page, 'POST')[0].body), { MaxReviewLength: 20000, ReviewsPageSize: 5, SnapshotsToKeep: 0, ModerationLogDays: 0 });
    assert.equal(t.page.errors.length, 0);
});

test('who can rate: the users picker shows for "Only the users chosen below"; nobody chosen is marked and sends nothing, the focus on the first user; a chosen user is sent by id', { skip }, async () => {
    const t = await open();
    assert.equal(shown(t.row('RaterUserIds')), false, 'hidden for every user');
    choice(t, 'WhoCanRate', 'SelectedUsers').click();
    assert.equal(shown(t.row('RaterUserIds')), true);
    assert.equal(error(t, 'RaterUserIds'), 'Choose at least one user: with only chosen users and nobody chosen, nobody could rate or review.');
    assert.equal(control(t, 'RaterUserIds').getAttribute('aria-invalid'), 'true');
    await save(t);
    assert.equal(calls(t.page, 'POST').length, 0);
    const users = control(t, 'RaterUserIds').querySelectorAll('button');
    assert.ok(t.page.document.activeElement === users[0], 'the focus on the first user');
    users[2].click();
    assert.equal(users[2].getAttribute('aria-checked'), 'true');
    assert.equal(error(t, 'RaterUserIds'), null);
    users[0].click();
    users[2].click();
    await save(t);
    assert.deepEqual(plain(calls(t.page, 'POST')[0].body), { WhoCanRate: 'SelectedUsers', RaterUserIds: [ALICE] });
    // Back to every user: the picker hides and its message with it; the list chosen stays as it was saved.
    choice(t, 'WhoCanRate', 'AllUsers').click();
    assert.equal(shown(t.row('RaterUserIds')), false);
    assert.equal(error(t, 'RaterUserIds'), null);
    assert.equal(t.page.errors.length, 0);
});

test('only a gone user chosen is nobody chosen: marked at once when the tab is read, as the server would refuse it', { skip }, async () => {
    const t = await open({ settings: { WhoCanRate: 'SelectedUsers', RaterUserIds: [GONE] }, users: USERS.concat([{ Id: GONE, Name: '', Missing: true }]) });
    assert.equal(error(t, 'RaterUserIds'), 'Choose at least one user: with only chosen users and nobody chosen, nobody could rate or review.');
});

test('the kinds that can be rated: one turned off and one on are sent as the list; none at all is marked and sends nothing', { skip }, async () => {
    const t = await open();
    choice(t, 'RateableItemTypes', 'Movie').click();
    choice(t, 'RateableItemTypes', 'Trailer').click();
    await save(t);
    assert.deepEqual(plain(calls(t.page, 'POST')[0].body), { RateableItemTypes: ['Series', 'Season', 'Episode', 'Video', 'MusicVideo', 'MusicAlbum', 'Book', 'AudioBook', 'Trailer'] });
    const u = await open();
    for (const kind of DEFAULTS.RateableItemTypes) {
        choice(u, 'RateableItemTypes', kind).click();
    }

    assert.equal(error(u, 'RateableItemTypes'), 'Choose at least one kind: with none, nothing could be rated.');
    await save(u);
    assert.equal(calls(u.page, 'POST').length, 0);
    assert.ok(u.page.document.activeElement === choice(u, 'RateableItemTypes', 'Movie'));
});

test('consequences: the dry run\'s changes and what they mean, as text, the values isolated; Cancel (focused) saves nothing, the focus back on Save; Save in the dialog saves', { skip }, async () => {
    const t = await open({ consequences: (body) => (body.EnableReviews === false ? ['The reviews (<b>3</b>) stop showing, and the review box goes. Nothing is deleted.'] : []) });
    control(t, 'EnableReviews').click();
    await save(t);
    let dialog = t.q('.jfur-panel');
    assert.ok(dialog);
    assert.equal(text(dialog.querySelector('h3')), 'Save these settings?');
    assert.deepEqual(plain(dialog.querySelectorAll('h4').map((h) => h.textContent)), ['What changes', 'What it means']);
    assert.equal(text(dialog.querySelector('.jfur-set-changes li')), 'Reviews: On → Off');
    assert.deepEqual(plain(dialog.querySelectorAll('.jfur-set-changes .jfur-isolate').map((n) => [n.textContent, n.getAttribute('dir')])), [['On', 'auto'], ['Off', 'auto']]);
    assert.equal(text(dialog.querySelector('.jfur-set-consequences li')), 'The reviews (<b>3</b>) stop showing, and the review box goes. Nothing is deleted.');
    assert.ok(dialog.querySelector('b') === null);
    assert.ok(t.page.document.activeElement === dialog.querySelector('.jfur-set-cancel'), 'Cancel focused');
    dialog.querySelector('.jfur-set-cancel').click();
    await t.page.advance(10);
    assert.ok(t.q('.jfur-panel') === null);
    assert.equal(t.server.saves.length, 0, 'nothing saved');
    assert.ok(t.page.document.activeElement === t.q('.jfur-set-save'), 'the focus back on Save');
    assert.equal(control(t, 'EnableReviews').getAttribute('aria-checked'), 'false', 'the change is kept');

    await save(t);
    dialog = t.q('.jfur-panel');
    dialog.querySelector('.jfur-set-confirm').click();
    await t.page.advance(10);
    assert.ok(t.q('.jfur-panel') === null);
    assert.deepEqual(plain(t.server.saves), [{ EnableReviews: false }]);
    assert.deepEqual(plain(calls(t.page, 'POST').map((c) => c.query.dryRun || 'dry')), ['dry', 'dry', 'false']);
    assert.deepEqual(plain(t.all('.jfur-toast').map((n) => n.textContent)), ['Settings saved.']);
    assert.ok(t.page.document.activeElement === t.q('.jfur-set-save'));
    assert.equal(t.page.errors.length, 0);
});

test('the dialog in the tab\'s words: its labels, the choices\' names, the users chosen by name, each isolated (markup a text), a gone user by id', { skip }, async () => {
    const t = await open({ users: USERS.concat([{ Id: GONE, Name: '', Missing: true }]), settings: { RaterUserIds: [GONE] }, consequences: () => ['2 users can no longer rate or review. Their ratings and reviews stay and still show, and they can still delete them.'] });
    choice(t, 'WhoCanRate', 'SelectedUsers').click();
    choice(t, 'RaterUserIds', SARA).click();
    choice(t, 'RaterUserIds', BOB).click();
    choice(t, 'RateableItemTypes', 'Movie').click();
    control(t, 'HideReviewsFromHiddenUsers').click();
    await save(t);
    const dialog = t.q('.jfur-panel');
    assert.deepEqual(plain(dialog.querySelectorAll('.jfur-set-changes li').map((li) => li.textContent)), [
        'Who can rate and review: Every user → Only the users chosen below',
        'Users who can rate: A user no longer on the server (' + GONE + ') → <img src=x onerror=alert(1)>, שרה‮ evil, A user no longer on the server (' + GONE + ')',
        'What can be rated: Movies, Series, Seasons, Episodes, Videos, Music videos, Albums, Books, Audiobooks → Series, Seasons, Episodes, Videos, Music videos, Albums, Books, Audiobooks',
        'Leave out hidden users\' ratings: Off → On'
    ]);
    const names = dialog.querySelectorAll('.jfur-set-changes .jfur-set-change-users .jfur-isolate');
    assert.deepEqual(plain(names.map((n) => [n.textContent, n.getAttribute('dir')])), [['<img src=x onerror=alert(1)>', 'auto'], ['שרה‮ evil', 'auto']]);
    assert.ok(dialog.querySelector('img') === null);
    dialog.querySelector('.jfur-set-confirm').click();
    await t.page.advance(10);
    assert.deepEqual(plain(t.server.saves[0].RaterUserIds), [GONE, SARA, BOB]);
    assert.equal(t.page.errors.length, 0);
});

test('a setting that needs a restart: the dry run says so, and once saved a notice names it', { skip }, async () => {
    const t = await open({ consequences: (body) => (body.EnableTopRatedPage === false ? ['Takes effect after the server restarts: Top Rated page.'] : []) });
    control(t, 'EnableTopRatedPage').click();
    await save(t);
    assert.equal(text(t.q('.jfur-panel .jfur-set-consequences li')), 'Takes effect after the server restarts: Top Rated page.');
    t.q('.jfur-panel .jfur-set-confirm').click();
    await t.page.advance(10);
    assert.equal(text(t.q('.jfur-set-message .jfur-notice-text')), 'Restart the server for these to take effect: Top Rated page.');
    assert.equal(t.q('.jfur-set-message .jfur-notice').getAttribute('role'), 'status');
});

test('settings saved since the tab was read: the 409 saves nothing and says so, with Reload, which reads them again', { skip }, async () => {
    const t = await open();
    t.server.settings.ShowHistogram = false;
    t.server.version++;
    type(t, 'ReviewsPageSize', '40');
    await save(t);
    assert.equal(t.server.saves.length, 0);
    assert.equal(text(t.q('.jfur-set-message .jfur-notice-text')), CHANGED, 'the server words once (they say nothing was saved)');
    assert.equal(t.q('.jfur-set-message .jfur-notice').getAttribute('role'), 'alert');
    const reload = t.q('.jfur-set-message button');
    assert.equal(text(reload), 'Reload');
    reload.click();
    await t.page.advance(10);
    assert.equal(calls(t.page, 'GET').length, 2);
    assert.equal(shown(t.q('.jfur-set-message .jfur-notice')), false);
    assert.equal(control(t, 'ShowHistogram').getAttribute('aria-checked'), 'false', 'as saved meanwhile');
    assert.equal(control(t, 'ReviewsPageSize').value, '20', 'the settings as they are now');
    type(t, 'ReviewsPageSize', '40');
    await save(t);
    assert.deepEqual(plain(t.server.saves), [{ ReviewsPageSize: 40 }]);
    assert.equal(t.page.errors.length, 0);
});

test('the tab selected again (the page calls refresh): read again when nothing is changed; a change not saved stays and nothing is read; a change made while it reads stays too, saved with the version it started from', { skip }, async () => {
    const t = await open();
    t.server.settings.ShowHistogram = false;
    t.server.version++;
    t.mounted.refresh();
    await t.page.advance(10);
    assert.equal(calls(t.page, 'GET').length, 2, 'nothing changed: read again');
    assert.equal(control(t, 'ShowHistogram').getAttribute('aria-checked'), 'false', 'as saved meanwhile');
    type(t, 'ReviewsPageSize', '30');
    t.mounted.refresh();
    await t.page.advance(10);
    assert.equal(calls(t.page, 'GET').length, 2, 'a change not saved: nothing read');
    assert.equal(control(t, 'ReviewsPageSize').value, '30', 'the change kept');
    assert.equal(t.q('.jfur-set-undo').disabled, false);
    t.q('.jfur-set-undo').click();
    // The click right after the tab is selected, before the read answers: it stays, and the form isn't built again.
    t.server.settings.ShowHistogram = true;
    t.server.version++;
    t.mounted.refresh();
    const reviews = control(t, 'EnableReviews');
    reviews.click();
    await t.page.advance(10);
    assert.equal(calls(t.page, 'GET').length, 3);
    assert.ok(control(t, 'EnableReviews') === reviews, 'the same control');
    assert.equal(reviews.getAttribute('aria-checked'), 'false', 'the click kept');
    assert.equal(t.q('.jfur-set-undo').disabled, false);
    await save(t);
    assert.deepEqual(plain(calls(t.page, 'POST').map((e) => e.query.version)), ['v000000000000002'], 'the version the change started from');
    assert.equal(t.server.saves.length, 0, 'saved since: the 409');
    assert.equal(text(t.q('.jfur-set-message .jfur-notice-text')), CHANGED);
    assert.equal(t.page.errors.length, 0);
});

test('a value the server refuses goes under its setting (its code names it), the focus on it; any other refusal says why above the settings', { skip }, async () => {
    const t = await open();
    t.server.refuse = () => ({ status: 400, body: { title: 'Bad Request', status: 400, detail: 'MaxReviewLength must be a whole number from 2000 to 20000. Nothing was saved.', code: 'InvalidSetting:MaxReviewLength' } });
    type(t, 'MaxReviewLength', '6000');
    await save(t);
    assert.equal(error(t, 'MaxReviewLength'), 'MaxReviewLength must be a whole number from 2000 to 20000. Nothing was saved.');
    assert.equal(control(t, 'MaxReviewLength').getAttribute('aria-invalid'), 'true');
    assert.ok(t.page.document.activeElement === control(t, 'MaxReviewLength'));
    t.server.refuse = () => ({ status: 503, body: { title: 'Service Unavailable', status: 503, detail: 'User Ratings isn\'t loaded, so its settings can\'t be saved. Restart the server and try again.', code: 'x' } });
    type(t, 'MaxReviewLength', '6001');
    await save(t);
    assert.equal(text(t.q('.jfur-set-message .jfur-notice-text')), 'Nothing was saved. User Ratings isn\'t loaded, so its settings can\'t be saved. Restart the server and try again.');
    assert.equal(t.server.saves.length, 0);
    assert.equal(t.page.errors.length, 0);
});

test('a saved name this version doesn\'t know: no choice checked, a note says so, and it is sent only when a choice is made; a name in another case is the same name', { skip }, async () => {
    const t = await open({ settings: { PosterBadgePosition: 'middle', WhoCanRate: 'selectedusers', RaterUserIds: [BOB] } });
    assert.deepEqual(plain(control(t, 'PosterBadgePosition').querySelectorAll('button').map((b) => b.getAttribute('aria-checked'))), ['false', 'false', 'false', 'false']);
    assert.equal(text(t.row('PosterBadgePosition').querySelector('.jfur-set-note')), 'Saved as "middle", which this version doesn\'t know; it stays until you choose.');
    assert.equal(choice(t, 'WhoCanRate', 'SelectedUsers').getAttribute('aria-checked'), 'true');
    assert.equal(shown(t.row('RaterUserIds')), true);
    assert.equal(shown(t.row('WhoCanRate').querySelector('.jfur-set-note')), false);
    control(t, 'ShowOnPosters').click();
    await save(t);
    assert.deepEqual(plain(calls(t.page, 'POST')[0].body), { ShowOnPosters: true });
    choice(t, 'PosterBadgePosition', 'bottom-end').click();
    assert.equal(shown(t.row('PosterBadgePosition').querySelector('.jfur-set-note')), false);
    await save(t);
    assert.deepEqual(plain(calls(t.page, 'POST')[2].body), { PosterBadgePosition: 'bottom-end' });
});

test('settings that can\'t be read say why, with Try again; nothing to save until they are read', { skip }, async () => {
    const t = await open({ before: (server) => { server.readStatus = 503; } });
    assert.equal(text(t.q('.jfur-set-message .jfur-notice-text')), 'The settings couldn\'t be loaded. The ratings database is busy; try again in a moment.');
    assert.equal(shown(t.q('.jfur-set-save')), false);
    assert.equal(t.all('.jfur-set-row').length, 0);
    t.server.readStatus = null;
    t.q('.jfur-set-message button').click();
    await t.page.advance(10);
    assert.equal(t.all('.jfur-set-row').length, 32);
    assert.equal(shown(t.q('.jfur-set-save')), true);
    assert.equal(shown(t.q('.jfur-set-message .jfur-notice')), false);
});

test('the TV layout: every setting a button or a text field, no select; the D-pad moves through the dialog without a trap, Back closes it, the focus back on Save', { skip }, async () => {
    const t = await open({ layout: 'tv', consequences: () => ['N users can no longer rate or review.'] });
    assert.equal(t.view.querySelectorAll('select').length, 0);
    for (const field of ORDER) {
        const c = control(t, field);
        assert.ok(c.tagName === 'BUTTON' || c.tagName === 'INPUT' || Array.from(c.querySelectorAll('*')).some((n) => n.tagName === 'BUTTON'), field);
    }

    control(t, 'ShowHistogram').click();
    await save(t);
    const dialog = t.q('.jfur-panel');
    const cancel = dialog.querySelector('.jfur-set-cancel');
    assert.ok(t.page.document.activeElement === cancel);
    t.page.key(cancel, 'ArrowDown');
    assert.ok(t.page.document.activeElement === dialog.querySelector('.jfur-set-confirm'), 'down: Save');
    t.page.key(t.page.document.activeElement, 'ArrowDown');
    assert.ok(t.page.document.activeElement === dialog.querySelector('.jfur-set-confirm'), 'the end: it stays (no wrap)');
    t.page.key(t.page.document.activeElement, 'ArrowUp');
    assert.ok(t.page.document.activeElement === cancel, 'up: Cancel');
    t.page.key(t.page.document.activeElement, 'GoBack', { keyCode: 461 });
    await t.page.advance(10);
    assert.ok(t.q('.jfur-panel') === null, 'Back closed it');
    assert.ok(t.page.document.activeElement === t.q('.jfur-set-save'), 'back on Save');
    assert.equal(t.server.saves.length, 0);
    assert.equal(t.page.errors.length, 0);
});

test('a phone at 375 px: every control at least 44 px high, the choices a row each, nothing wider than the screen (the admin styles)', { skip }, async () => {
    const t = await open();
    const css = t.page.document.getElementById('jfur-admin-styles').textContent;
    const start = css.indexOf('/* Web/admin/css/settings.css */');
    assert.ok(start >= 0, 'the Settings tab\'s styles are in the admin styles');
    const own = css.substring(start);
    const phone = own.substring(own.indexOf('@media (max-width:599px){'));
    assert.ok(phone.startsWith('@media (max-width:599px){\n.jfur-set-switch,.jfur-set-choice,.jfur-set-check,.jfur-set button,.jfur-set input{min-height:44px}'), '44 px controls');
    assert.ok(phone.includes('.jfur-set-choice,.jfur-set-check{flex:1 1 100%}'), 'a choice a row');
    assert.ok(phone.includes('.jfur-set-cancel,.jfur-set-confirm{min-height:44px}'), 'the dialog\'s too');
    assert.ok(own.includes('@media (pointer:coarse){.jfur-set-switch,.jfur-set-choice,.jfur-set-check,.jfur-set button,.jfur-set input,.jfur-set-cancel,.jfur-set-confirm{min-height:44px}}'));
    assert.ok(own.includes('.jfur-set-choices,.jfur-set-checks{display:flex;flex-wrap:wrap;gap:.5em}'), 'the choices wrap');
    assert.ok(own.includes('\n.jfur-set .jfur-set-number{flex:0 0 auto;width:8em;max-width:100%;'), 'the field 8em wide, not grown by the kit\'s .jfur-input (flex:1)');
    assert.ok(own.includes('@media (forced-colors:active){'));
    // No width in px (outside the media queries) that a 375 px screen would scroll for.
    assert.deepEqual(own.match(/[{;](?:min-)?width:\s*(\d{3,})px/g), null);
});
