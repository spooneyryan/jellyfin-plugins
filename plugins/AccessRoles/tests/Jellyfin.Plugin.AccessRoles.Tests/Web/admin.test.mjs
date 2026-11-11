// Access Roles' dashboard page in the kit's fake page, with the built AdminScript module
// (WebTests writes it to a file and runs KIT_ADMIN_SCRIPT=<file> node --test admin.test.mjs). Each test imports its own
// copy of the module (a query string on the file URL) into a fresh page, so no kit state is shared. The module runs in
// Node's realm: the page's window, document, fetch and Event become globals for it.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { pathToFileURL } from 'node:url';
import { createPage } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_ADMIN_SCRIPT;
const skip = !file ? 'KIT_ADMIN_SCRIPT is not set' : false;
let imports = 0;

const MOVIES = 'a0000000000000000000000000000001';
const CARTOONS = 'a0000000000000000000000000000002';
const HORROR = 'a0000000000000000000000000000003';
const BOXSETS = 'a0000000000000000000000000000004';
const KID = 'b0000000000000000000000000000001';
const FAMILY = 'b0000000000000000000000000000002';
const ANNA = 'c0000000000000000000000000000001';
const BEN = 'c0000000000000000000000000000002';
const ROOT = 'c0000000000000000000000000000003';
const UPDATED = '2026-10-30T10:00:00Z';
const NEWER = '2026-10-30T11:00:00Z';
const BASE = '/AccessRoles/Admin/';

function diagnostics(changes) {
    return Object.assign({
        PluginVersion: '0.9.9.0',
        KitVersion: '1.0.0',
        Abi: { Target: '10.11.5', Server: '10.11.5', Active: true },
        Store: { File: 'accessroles.json', Version: 1, CurrentVersion: 1, Status: 'Loaded', ReadOnly: false },
        Records: { Roles: 2, Assignments: 1, KnownLibraries: 4, LedgerEntries: 1, PendingReleases: 0 },
        Enforcement: { Active: true, SettingsLocks: true, Hardening: true, ServerWide: {}, Blocked: { Totals: {} }, NotEnforcedLocks: [] },
        LibrarySync: { State: 'Active', DriftPolicy: 'ReportOnly', Totals: {} },
        ServerUsers: { Users: 3, Administrators: 1, Disabled: 1, ListedThrough: 'IUserManager' }
    }, changes || {});
}

function libraries() {
    return [
        { Id: MOVIES, Name: 'Movies', Enabled: true, State: 'Active', CollectionType: 'movies', RoleIds: [KID], InNoRole: false, Locations: [], Recreated: false, Known: true },
        { Id: CARTOONS, Name: 'Cartoons', Enabled: false, State: 'Active', CollectionType: 'tvshows', RoleIds: [], InNoRole: true, Locations: [], Recreated: false, Known: true },
        { Id: HORROR, Name: 'Horror', Enabled: true, State: 'Active', CollectionType: 'movies', RoleIds: [], InNoRole: true, Locations: [], Recreated: false, Known: true },
        { Id: BOXSETS, Name: 'Collections', Enabled: true, State: 'Active', CollectionType: 'boxsets', RoleIds: [], InNoRole: true, Locations: [], Recreated: false, Known: true }
    ];
}

function role(id, name, changes) {
    return Object.assign({
        Id: id,
        Name: name,
        Description: null,
        Locks: [],
        Libraries: { Mode: 'NotManaged', Refs: [] },
        Dashboard: { BlocksAdminSurfaces: false, Enforced: true, NonAdministrators: 'The Dashboard is blocked by Jellyfin; its data and server calls are not.', Administrators: 'Not restricted.', AdministratorMembers: [] },
        MemberCount: 0,
        IsDefault: false,
        UnknownValues: [],
        CreatedUtc: '2026-10-01T10:00:00Z',
        UpdatedUtc: UPDATED,
        MembersToken: 'members-' + name
    }, changes || {});
}

function roles() {
    return [
        role(KID, 'Kid', {
            Locks: ['Profile', 'Home', 'AdminSurfaces', 'UserDirectory'],
            Libraries: { Mode: 'Only', Refs: [{ Id: MOVIES, Name: 'Movies', Orphaned: false }] },
            Dashboard: { BlocksAdminSurfaces: true, Enforced: true, NonAdministrators: 'Blocked: the Dashboard and its data.', Administrators: 'Not restricted.', AdministratorMembers: [] },
            MemberCount: 1
        }),
        role(FAMILY, 'Family', { Libraries: { Mode: 'All', Refs: [] } })
    ];
}

function user(id, name, changes) {
    return Object.assign({
        UserId: id, Name: name, IsAdministrator: false, IsDisabled: false, IsHidden: true, RoleIds: [], State: 'NotManaged',
        LibrariesManaged: false, LockedCount: 0, NoLibraries: false, PendingWrite: false, Drift: false, Summary: '', UpdatedUtc: null
    }, changes || {});
}

function users() {
    return [
        user(ANNA, 'Anna', { RoleIds: [KID], State: 'Managed', LibrariesManaged: true, LockedCount: 4, IsHidden: false, Drift: true, Summary: '4 settings locked; 1 library', UpdatedUtc: UPDATED }),
        user(BEN, 'Ben', { IsDisabled: true }),
        user(ROOT, 'Root', { IsAdministrator: true })
    ];
}

function lock(locked, source, enforced) {
    return { Locked: locked, Source: source, Enforced: enforced !== false };
}

function effective(id, changes) {
    const base = {
        UserId: id,
        UserName: id === ANNA ? 'Anna' : (id === BEN ? 'Ben' : 'Root'),
        IsAdministrator: id === ROOT,
        State: 'NotManaged',
        Roles: [],
        Locks: {},
        Libraries: { UserMode: null, StartingSet: 'Original', Managed: false, EnableAllFolders: true, NoLibraries: false, NoLibrariesConfirmed: false, Visible: [], Hidden: [], Native: { EnableAllFolders: true, EnabledFolders: [] }, MissingReferences: [], Drift: false },
        NativeFlags: { EnableUserPreferenceAccess: true, EnableLiveTvManagement: false, IsHidden: true, IsDisabled: false, BlockedMediaFolders: [] },
        Warnings: [],
        UnknownValues: [],
        UpdatedUtc: null,
        Assignment: null
    };
    return Object.assign(base, changes || {});
}

function annaEffective(changes) {
    return effective(ANNA, Object.assign({
        State: 'Managed',
        Roles: [{ Id: KID, Name: 'Kid' }],
        Locks: {
            Profile: lock(true, 'Role:Kid'),
            Home: lock(false, 'UserOverride'),
            Controls: lock(true, 'UserOverride', false),
            Playback: lock(false, 'Default'),
            AdminSurfaces: lock(true, 'Role:Kid'),
            UserDirectory: lock(true, 'Role:Kid')
        },
        Libraries: {
            UserMode: 'Inherit', StartingSet: 'Roles', Managed: true, EnableAllFolders: false, NoLibraries: false, NoLibrariesConfirmed: false,
            Visible: [{ Id: MOVIES, Name: 'Movies', Enabled: true, Sources: ['Role:Kid'] }],
            Hidden: [{ Id: HORROR, Name: 'Horror', Enabled: true, Reason: 'RemovedForUser' }, { Id: CARTOONS, Name: 'Cartoons', Enabled: false, Reason: 'NotGranted' }],
            Native: { EnableAllFolders: false, EnabledFolders: [MOVIES, HORROR] },
            LastWrite: { EnableAllFolders: false, EnabledFolders: [MOVIES] },
            MissingReferences: [],
            Drift: true
        },
        NativeFlags: { EnableUserPreferenceAccess: true, EnableLiveTvManagement: false, IsHidden: false, IsDisabled: false, BlockedMediaFolders: [] },
        UpdatedUtc: UPDATED,
        Assignment: {
            RoleIds: [KID],
            LockOverrides: { Home: 'Unlock', Controls: 'Lock' },
            Libraries: { Mode: 'Inherit', Add: [], Remove: [HORROR], ConfirmNoLibraries: false },
            State: 'Managed'
        }
    }, changes || {}));
}

function preview(changes) {
    return Object.assign({ Users: [], UnmanageChoiceRequired: [], NoLibraries: [], NoLedgerEntry: [], NoFullAdministrator: false, DefaultRole: [] }, changes || {});
}

function change(changes) {
    return Object.assign({ DryRun: false, Changed: [], Preview: preview(), AuditSeq: [1] }, changes || {});
}

// A refusal's ProblemDetails (the kit's, with the roles API's Code, Users and a refused dry run's Preview).
function problem(status, code, detail, extensions) {
    return Object.assign({ type: 'about:blank', title: 'Refused', status, detail, Code: code, Users: [] }, extensions || {});
}

// An answer that depends on the request, its status included, and may wait (a Promise): fn(entry) → { status, body }.
// It wraps the page's fetch (the module calls the global fetch at each request) and logs the request as the page does.
function serve(page, method, path, fn) {
    const before = globalThis.fetch;
    globalThis.fetch = (url, init) => {
        const m = (init && init.method) || 'GET';
        const parsed = new URL(String(url), 'http://127.0.0.1:8097');
        if (m !== method || (path instanceof RegExp ? !path.test(parsed.pathname) : parsed.pathname !== path)) {
            return before(url, init);
        }

        const entry = { method: m, path: parsed.pathname, query: Object.fromEntries(parsed.searchParams.entries()), body: init && init.body !== undefined ? JSON.parse(init.body) : undefined };
        page.fetchLog.push(entry);
        return Promise.resolve(fn(entry)).then((answer) => {
            const status = (answer && answer.status) || 200;
            const text = answer && answer.body !== undefined ? JSON.stringify(answer.body) : '';
            return {
                ok: status >= 200 && status < 300,
                status,
                headers: { get: (name) => (name.toLowerCase() === 'content-type' ? 'application/json' : null) },
                text: () => Promise.resolve(text),
                json: () => Promise.resolve(JSON.parse(text))
            };
        });
    };
}

// A dry run answers its preview, the save its change: the write's answer by ?dryRun.
function dryOrSave(dry, saved) {
    return (entry) => (entry.query.dryRun === 'true' ? { body: change(Object.assign({ DryRun: true }, typeof dry === 'function' ? dry(entry) : dry)) } : { body: saved });
}

// A promise and its resolve, for an answer that comes late.
function later() {
    let resolve;
    const promise = new Promise((r) => {
        resolve = r;
    });
    return { promise, resolve };
}

function leavingRow(choice, changes) {
    return Object.assign({ UserId: ANNA, Name: 'Anna', IsAdministrator: false, StateBefore: 'Managed', StateAfter: 'Managed', LocksAdded: [], LocksRemoved: [], LibrariesAdded: [], LibrariesRemoved: [], PolicyWrite: false, NoLibraries: false, NoLibrariesRefused: false, LeavesManagement: { Choice: choice, FrozenAt: choice === 'Keep' ? ['Movies'] : [] }, Warnings: [] }, changes || {});
}

// A fresh page with the module mounted into the dashboard page's skeleton.
async function start(options) {
    options = options || {};
    const page = createPage();
    globalThis.window = page.context;
    globalThis.document = page.document;
    globalThis.fetch = page.context.fetch;
    globalThis.Event = page.context.Event;
    if (options.tv) {
        page.document.documentElement.classList.add('layout-tv');
    }

    page.installApiClient({ userId: 'admin-1', admin: true });
    if (options.beforeMount) {
        options.beforeMount(page);
    }

    page.respond('GET', BASE + 'Diagnostics', options.diagnostics || diagnostics());
    page.respond('GET', BASE + 'Roles', options.roles || roles());
    page.respond('GET', BASE + 'Users', options.users || users());
    page.respond('GET', BASE + 'Libraries', options.libraries || libraries());
    page.respond('GET', BASE + 'Users/' + ANNA + '/Effective', annaEffective());
    page.respond('GET', BASE + 'Users/' + BEN + '/Effective', effective(BEN));
    page.respond('GET', BASE + 'Users/' + ROOT + '/Effective', effective(ROOT, {
        Warnings: ['Administrators are not restricted: the locks of this user\'s roles don\'t apply.'],
        Locks: { Profile: lock(false, 'Administrator') },
        Assignment: { RoleIds: [FAMILY], LockOverrides: {}, Libraries: { Mode: 'Inherit', Add: [], Remove: [], ConfirmNoLibraries: false }, State: 'Managed' },
        UpdatedUtc: UPDATED
    }));
    (options.responses || []).forEach(function (r) {
        page.respond(r[0], r[1], r[2], r[3]);
    });
    const module = await import(pathToFileURL(file).href + '?page=' + (++imports));
    const view = page.element('div', { id: 'AccessRolesConfigPage', 'data-role': 'page' });
    const note = page.element('p', { id: 'AccessRolesScriptNote' }, ['Loading the page…']);
    const root = page.element('div', { id: 'AccessRolesRoot' });
    view.append(note, root);
    page.document.body.append(view);
    module.mount(view, options.params || {});
    await page.flush();
    return { page, view, root, note };
}

function all(root, selector) {
    return root.querySelectorAll(selector);
}

function byText(root, selector, text) {
    const found = all(root, selector).filter((e) => e.textContent === text);
    assert.equal(found.length >= 1, true, 'no ' + selector + ' "' + text + '" in: ' + all(root, selector).map((e) => e.textContent).join(' | '));
    return found[found.length - 1];
}

function dialogs(page) {
    return all(page.document.body, '.jfar-panel');
}

function topDialog(page) {
    const open = dialogs(page);
    assert.ok(open.length > 0, 'no dialog is open');
    return open[open.length - 1];
}

function dialogTitle(panel) {
    return panel.querySelector('.jfar-panel-header h3').textContent;
}

async function click(page, element) {
    assert.ok(element, 'nothing to click');
    assert.equal(!!element.disabled, false, '"' + element.textContent + '" is disabled');
    element.click();
    await page.flush();
}

async function tick(page, input, checked) {
    input.checked = checked === undefined ? !input.checked : checked;
    page.dispatch(input, 'change');
    await page.flush();
}

async function choose(page, select, value) {
    select.value = value;
    page.dispatch(select, 'change');
    await page.flush();
}

function requests(page, method, path) {
    return page.fetchLog.filter((e) => e.method === method && (path instanceof RegExp ? path.test(e.path) : e.path === path));
}

function tab(env, name) {
    return env.root.querySelector('#AccessRolesTab' + name);
}

async function openTab(env, name) {
    await click(env.page, tab(env, name));
    return env.root.querySelector('#AccessRolesPanel' + name);
}

function lockBox(panel, lockName) {
    return panel.querySelector('input[data-lock="' + lockName + '"]');
}

function hidden(element) {
    for (let e = element; e; e = e.parentNode) {
        if (e.hasAttribute && e.hasAttribute('hidden')) {
            return true;
        }
    }

    return false;
}

test('the page mounts its tabs into the skeleton, hides the loading note and loads the four lists', { skip }, async () => {
    const env = await start();
    const tabs = all(env.root, '[role="tab"]');
    assert.deepEqual(tabs.map((t) => t.textContent), ['Overview', 'Roles', 'Users', 'Libraries', 'Audit', 'Settings', 'Data', 'Diagnostics']);
    assert.equal(tab(env, 'Overview').getAttribute('aria-selected'), 'true');
    assert.equal(hidden(env.root.querySelector('#AccessRolesPanelRoles')), true);
    assert.equal(env.note.hasAttribute('hidden'), true);
    for (const path of ['Diagnostics', 'Roles', 'Users', 'Libraries']) {
        assert.equal(requests(env.page, 'GET', BASE + path).length, 1, path);
    }

    // The audit and the settings only when their tab is shown.
    assert.equal(requests(env.page, 'GET', BASE + 'Audit').length, 0);
    assert.equal(requests(env.page, 'GET', BASE + 'Settings').length, 0);
    // The status tables are the Diagnostics tab's since 0.9.11, under the id the probes read.
    const status = env.root.querySelector('#AccessRolesPanelDiagnostics #AccessRolesStatus');
    assert.equal(all(status, 'table').length, 5);
    assert.ok(status.textContent.includes('0.9.9.0'));
    assert.ok(status.textContent.includes('Drift policy'));
    assert.equal(env.page.document.getElementById('jfar-admin-styles') !== null, true);
    assert.equal(env.page.errors.length, 0, env.page.errors.join('\n'));
});

test('the arrow keys, Home and End move between the tabs; on the TV layout every tab is the D-pad\'s to reach', { skip }, async () => {
    const env = await start();
    const overview = tab(env, 'Overview');
    overview.focus();
    env.page.key(overview, 'ArrowRight');
    assert.equal(tab(env, 'Roles').getAttribute('aria-selected'), 'true');
    assert.equal(env.page.document.activeElement, tab(env, 'Roles'));
    assert.equal(hidden(env.root.querySelector('#AccessRolesPanelRoles')), false);
    env.page.key(tab(env, 'Roles'), 'End');
    assert.equal(focusName(env.page), 'AccessRolesTabDiagnostics');
    env.page.key(tab(env, 'Diagnostics'), 'Home');
    assert.equal(focusName(env.page), 'AccessRolesTabOverview');
    env.page.key(overview, 'ArrowLeft');
    assert.equal(tab(env, 'Diagnostics').getAttribute('aria-selected'), 'true');
    assert.equal(overview.getAttribute('tabindex'), '-1');

    const tv = await start({ tv: true });
    assert.deepEqual(all(tv.root, '[role="tab"]').map((t) => t.getAttribute('tabindex')), ['0', '0', '0', '0', '0', '0', '0', '0']);
    tab(tv, 'Overview').focus();
    tv.page.key(tab(tv, 'Overview'), 'ArrowRight');
    assert.equal(tab(tv, 'Overview').getAttribute('aria-selected'), 'true', 'the D-pad moves the focus, OK opens the tab');
    await click(tv.page, tab(tv, 'Users'));
    assert.equal(tab(tv, 'Users').getAttribute('aria-selected'), 'true');
});

test('the banners: read-only data disables every write, and settings locks off says nothing is enforced', { skip }, async () => {
    const env = await start({
        diagnostics: diagnostics({
            Store: { File: 'accessroles.json', Version: 1, CurrentVersion: 1, Status: 'ReadOnly', ReadOnly: true, Message: 'The data file is newer than this build.' },
            Enforcement: { Active: true, SettingsLocks: false, SettingsLocksOffBecause: 'switched off in the settings', ServerWide: {}, Blocked: { Totals: {} } }
        })
    });
    const banners = env.root.querySelector('#AccessRolesBanners').textContent;
    assert.ok(banners.includes('The data file is newer than this build.'));
    assert.ok(banners.includes('Access Roles\' data is read-only'));
    assert.ok(banners.includes('Nothing is enforced; restrictions are only stored (switched off in the settings).'));
    assert.equal(all(env.root, '.jfar-page-banner-error').length, 2);
    const roles = await openTab(env, 'Roles');
    assert.equal(roles.querySelector('#AccessRolesNewRole').disabled, true);
    assert.equal(roles.querySelector('[data-role-id="' + KID + '"] [data-action="delete"]').disabled, true);
    const usersPanel = await openTab(env, 'Users');
    await click(env.page, byText(usersPanel, 'button', 'Anna'));
    await env.page.flush();
    assert.equal(topDialog(env.page).querySelector('#AccessRolesUserSave').disabled, true);
});

test('no role yet: the preset cards; Kid opens the editor with its locks, Only… and the Dashboard row, and Only… needs a library', { skip }, async () => {
    const env = await start({ roles: [], users: [user(BEN, 'Ben')] });
    const panel = await openTab(env, 'Roles');
    assert.ok(panel.textContent.includes('There are no roles yet.'));
    assert.deepEqual(all(panel, '[data-preset]').map((c) => c.getAttribute('data-preset')), ['Kid', 'Guest', 'Standard', 'Blank']);
    await click(env.page, panel.querySelector('[data-preset="Kid"]'));
    const editor = topDialog(env.page);
    assert.equal(dialogTitle(editor), 'New role: Kid');
    const checked = all(editor, 'input[data-lock]').filter((i) => i.checked).map((i) => i.getAttribute('data-lock'));
    assert.deepEqual(checked, ['Profile', 'DisplayAccount', 'Home', 'Controls', 'QuickConnect', 'EnhancedPanel', 'AdminSurfaces', 'UserDirectory']);
    assert.ok(editor.textContent.includes('hidden only'));
    assert.ok(editor.textContent.includes('server-enforced'));
    assert.equal(editor.querySelector('select').value, 'Only');

    // The Dashboard row (the role editor): the sentence follows the box; administrators are never restricted.
    const dashboard = editor.querySelector('.jfar-page-dashboard-text');
    assert.ok(dashboard.textContent.includes('also blocks the admin data and server calls'));
    await tick(env.page, lockBox(editor, 'AdminSurfaces'), false);
    assert.ok(dashboard.textContent.includes('doesn\'t block the admin data and server calls'));
    assert.ok(editor.textContent.includes('Administrators are not restricted.'));

    // The picker: every library, the disabled one and the Collections library marked.
    const picker = editor.querySelector('.jfar-page-picker');
    assert.ok(picker.textContent.includes('Cartoons') && picker.textContent.includes('disabled'));
    assert.ok(picker.textContent.includes('needed to browse any collection'));
    await click(env.page, byText(editor, 'button', 'Create'));
    assert.equal(editor.querySelector('.jfar-page-error').textContent, 'Pick at least one library.');
    assert.equal(requests(env.page, 'POST', BASE + 'Preview').length, 0);
});

test('creating a role: the preview is POST Roles?dryRun=true, then the same POST Roles with the preset; the list loads again', { skip }, async () => {
    const env = await start({ roles: [], users: [user(BEN, 'Ben')] });
    serve(env.page, 'POST', BASE + 'Roles', dryOrSave({ Preview: preview({ Users: [] }) }, change({ Role: role(KID, 'Kid') })));
    const panel = await openTab(env, 'Roles');
    await click(env.page, panel.querySelector('[data-preset="Kid"]'));
    const editor = topDialog(env.page);
    await tick(env.page, editor.querySelector('input[data-library-id="' + MOVIES + '"]'), true);
    await click(env.page, byText(editor, 'button', 'Create'));
    const dry = requests(env.page, 'POST', BASE + 'Roles')[0];
    assert.deepEqual(dry.query, { dryRun: 'true' });
    const sent = dry.body;
    assert.equal(sent.Name, 'Kid');
    assert.equal(sent.Preset, 'Kid');
    assert.deepEqual(sent.Libraries, { Mode: 'Only', Refs: [{ Id: MOVIES }] });
    assert.ok(sent.Locks.includes('AdminSurfaces'));
    const review = topDialog(env.page);
    assert.equal(dialogTitle(review), 'Create role Kid');
    assert.ok(review.textContent.includes('This changes nobody\'s access.'));
    await click(env.page, byText(review, 'button', 'Create'));
    const created = requests(env.page, 'POST', BASE + 'Roles');
    assert.equal(created.length, 2);
    assert.deepEqual(created[1].query, {});
    assert.deepEqual(created[1].body, sent, 'the save is the request the preview ran');
    assert.equal(requests(env.page, 'POST', BASE + 'Preview').length, 0);
    assert.equal(dialogs(env.page).length, 0);
    assert.equal(requests(env.page, 'GET', BASE + 'Roles').length, 2);
    assert.ok(env.page.document.querySelector('.jfar-toasts').textContent.includes('Role Kid saved.'));
});

test('a change that takes users out of management: the refused dry run shows who leaves, the choice runs it again and goes with the save', { skip }, async () => {
    const leaving = preview({ Users: [leavingRow(null)], UnmanageChoiceRequired: [{ UserId: ANNA, Name: 'Anna' }] });
    const kept = preview({ Users: [leavingRow('Keep')] });
    const env = await start();
    serve(env.page, 'PUT', BASE + 'Roles/' + KID, (entry) => {
        if (entry.query.dryRun !== 'true') {
            return { body: change({ Role: role(KID, 'Kid') }) };
        }

        return entry.query.onUnmanage === 'Keep'
            ? { body: change({ DryRun: true, Preview: kept }) }
            : { status: 400, body: problem(400, 'UnmanageChoiceRequired', 'Anna is no longer managed for libraries after this change.', { Users: [{ UserId: ANNA, Name: 'Anna' }], Preview: leaving }) };
    });
    const panel = await openTab(env, 'Roles');
    await click(env.page, panel.querySelector('[data-role-id="' + KID + '"] [data-action="edit"]'));
    const editor = topDialog(env.page);
    assert.equal(dialogTitle(editor), 'Edit role Kid');
    await choose(env.page, editor.querySelector('select'), 'NotManaged');
    await click(env.page, byText(editor, 'button', 'Save'));
    const review = topDialog(env.page);
    const save = byText(review, 'button', 'Save');
    assert.equal(save.disabled, true);
    assert.equal(hidden(review.querySelector('.jfar-page-preview-choice')), false);
    assert.ok(review.textContent.includes('Anna is no longer managed for libraries.'));
    assert.ok(review.querySelector('.jfar-page-error').textContent.includes('Choose what the library access'));

    await tick(env.page, review.querySelector('input[type="radio"][value="Keep"]'), true);
    assert.deepEqual(requests(env.page, 'PUT', BASE + 'Roles/' + KID).at(-1).query, { dryRun: 'true', onUnmanage: 'Keep' });
    assert.ok(review.textContent.includes('Library access stays frozen at: Movies.'));
    assert.equal(save.disabled, false);
    await click(env.page, save);
    const put = requests(env.page, 'PUT', BASE + 'Roles/' + KID);
    assert.equal(put.length, 3);
    assert.deepEqual(put[2].query, { onUnmanage: 'Keep' });
    assert.equal(put[2].body.IfUpdatedAt, UPDATED);
    assert.equal(put[2].body.IfMembers, 'members-Kid');
    assert.equal(put[2].body.Libraries.Mode, 'NotManaged');
    assert.deepEqual(put[2].body, put[1].body);
    assert.equal(requests(env.page, 'POST', BASE + 'Preview').length, 0);
});

test('the blocking warnings: no enabled full administrator needs the box (a dry run of its own), a refused empty library set blocks Save', { skip }, async () => {
    const env = await start();
    const noAdmin = preview({ NoFullAdministrator: true });
    serve(env.page, 'PUT', BASE + 'Roles/' + FAMILY, (entry) => {
        if (entry.query.confirmNoFullAdmin !== 'true') {
            return { status: 409, body: problem(409, 'NoFullAdministrator', 'After this change no enabled administrator would have access to every library.', { Preview: noAdmin }) };
        }

        return { body: change({ DryRun: entry.query.dryRun === 'true', Preview: noAdmin, Role: role(FAMILY, 'Family') }) };
    });
    const panel = await openTab(env, 'Roles');
    await click(env.page, panel.querySelector('[data-role-id="' + FAMILY + '"] [data-action="edit"]'));
    await click(env.page, byText(topDialog(env.page), 'button', 'Save'));
    let review = topDialog(env.page);
    const save = byText(review, 'button', 'Save');
    assert.equal(save.disabled, true);
    assert.ok(review.textContent.includes('No enabled administrator would see every library afterwards'));
    await tick(env.page, review.querySelector('input[type="checkbox"]'), true);
    assert.deepEqual(requests(env.page, 'PUT', BASE + 'Roles/' + FAMILY).at(-1).query, { confirmNoFullAdmin: 'true', dryRun: 'true' }, 'the box runs the dry run again with it');
    assert.equal(save.disabled, false);
    await click(env.page, save);
    assert.deepEqual(requests(env.page, 'PUT', BASE + 'Roles/' + FAMILY).at(-1).query, { confirmNoFullAdmin: 'true' });

    const empty = preview({
        Users: [{ UserId: ANNA, Name: 'Anna', IsAdministrator: false, StateBefore: 'Managed', StateAfter: 'Managed', LocksAdded: [], LocksRemoved: [], LibrariesAdded: [], LibrariesRemoved: [{ Id: MOVIES, Name: 'Movies' }], PolicyWrite: true, NoLibraries: true, NoLibrariesRefused: true, Warnings: [] }],
        NoLibraries: [{ UserId: ANNA, Name: 'Anna' }]
    });
    serve(env.page, 'PUT', BASE + 'Roles/' + KID, () => ({ status: 409, body: problem(409, 'NoLibraries', 'Anna would have no libraries.', { Users: [{ UserId: ANNA, Name: 'Anna' }], Preview: empty }) }));
    await click(env.page, env.root.querySelector('[data-role-id="' + KID + '"] [data-action="edit"]'));
    await click(env.page, byText(topDialog(env.page), 'button', 'Save'));
    review = topDialog(env.page);
    assert.equal(byText(review, 'button', 'Save').disabled, true);
    assert.ok(review.textContent.includes('Anna would have no libraries. This is refused'));
    assert.ok(review.textContent.includes('Libraries removed: Movies'));
    assert.ok(review.textContent.includes('The library sync will write their library access.'));
});

test('deleting a role with its members reassigned: the preview is DELETE\'s dry run with the reassignment and the members token', { skip }, async () => {
    const env = await start();
    serve(env.page, 'DELETE', BASE + 'Roles/' + KID, dryOrSave({ Preview: preview({ Users: [leavingRow(null, { LeavesManagement: null, LocksRemoved: ['Profile'] })] }) }, change()));
    const panel = await openTab(env, 'Roles');
    const row = panel.querySelector('.jfar-page-role[data-role-id="' + KID + '"]');
    assert.ok(row.textContent.includes('Members: Anna'));
    assert.ok(row.textContent.includes('Settings locked: Profile (password, picture, name), Home screen'));
    assert.ok(row.textContent.includes('Libraries: Only: Movies'));
    assert.ok(row.textContent.includes('Dashboard: Blocked: the Dashboard and its data.'));
    assert.ok(row.textContent.includes('Other users: hidden'));
    await click(env.page, row.querySelector('[data-action="delete"]'));
    const dialog = topDialog(env.page);
    assert.ok(dialog.textContent.includes('Its 1 member loses it: Anna.'));
    await choose(env.page, dialog.querySelector('select'), FAMILY);
    await click(env.page, byText(dialog, 'button', 'Continue'));
    const deletes = requests(env.page, 'DELETE', BASE + 'Roles/' + KID);
    assert.deepEqual(deletes[0].query, { dryRun: 'true', reassignTo: FAMILY, ifUpdatedAt: UPDATED, ifMembers: 'members-Kid' });
    assert.ok(topDialog(env.page).textContent.includes('Unlocked: Profile (password, picture, name)'), 'the server\'s own preview of the reassignment');
    assert.equal(requests(env.page, 'GET', /\/Effective$/).length, 0, 'no client-side guess of the reassignment');
    await click(env.page, byText(topDialog(env.page), 'button', 'Delete'));
    assert.deepEqual(requests(env.page, 'DELETE', BASE + 'Roles/' + KID)[1].query, { reassignTo: FAMILY, ifUpdatedAt: UPDATED, ifMembers: 'members-Kid' });
    assert.equal(dialogs(env.page).length, 0);
});

test('the default roles: Save follows a change, previews PUT DefaultRoles?dryRun=true, then the same PUT with its PreviewToken', { skip }, async () => {
    const env = await start();
    serve(env.page, 'PUT', BASE + 'DefaultRoles', dryOrSave({ Preview: preview({ PreviewToken: 'defaults-1' }) }, change({ DefaultRoles: [{ Id: KID, Name: 'Kid' }] })));
    const panel = await openTab(env, 'Roles');
    const save = panel.querySelector('#AccessRolesDefaultRolesSave');
    assert.equal(save.disabled, true);
    await tick(env.page, panel.querySelector('#AccessRolesDefaultRoles input[data-role-id="' + KID + '"]'), true);
    assert.equal(save.disabled, false);
    await click(env.page, save);
    const puts = requests(env.page, 'PUT', BASE + 'DefaultRoles');
    assert.deepEqual([puts[0].query, puts[0].body], [{ dryRun: 'true' }, { RoleIds: [KID] }]);
    const review = topDialog(env.page);
    assert.ok(review.textContent.includes('New users will get: Kid.'));
    await click(env.page, byText(review, 'button', 'Save'));
    const saved = requests(env.page, 'PUT', BASE + 'DefaultRoles')[1];
    // The token covers the default roles and their definitions now.
    assert.deepEqual([saved.query, saved.body], [{ ifPreview: 'defaults-1' }, { RoleIds: [KID] }]);
});

test('the users list: roles, state, summary and badges; the bulk bar needs a selection', { skip }, async () => {
    const env = await start();
    const panel = await openTab(env, 'Users');
    const anna = panel.querySelector('[data-user-id="' + ANNA + '"]');
    assert.ok(anna.textContent.includes('Roles: Kid · Managed'));
    assert.ok(anna.textContent.includes('4 settings locked; 1 library'));
    assert.deepEqual(all(anna, '.jfar-page-badge').map((b) => b.textContent), ['Shown on the login screen', 'Changed outside Access Roles']);
    assert.deepEqual(all(panel.querySelector('[data-user-id="' + BEN + '"]'), '.jfar-page-badge').map((b) => b.textContent), ['Disabled']);
    assert.deepEqual(all(panel.querySelector('[data-user-id="' + ROOT + '"]'), '.jfar-page-badge').map((b) => b.textContent), ['Administrator']);
    assert.equal(panel.querySelector('#AccessRolesBulkAssign').disabled, true);
    assert.equal(panel.querySelector('.jfar-page-count').textContent, 'No user selected');
    await tick(env.page, panel.querySelector('#AccessRolesSelectAll'), true);
    assert.equal(panel.querySelector('.jfar-page-count').textContent, '3 users selected');
    assert.equal(panel.querySelector('#AccessRolesBulkAssign').disabled, false);
});

test('a no-library result nobody chose is its own state, never drift; a chosen one is managed like any other, so its drift shows with Keep and Revert', { skip }, async () => {
    // The server never reports drift for an unchosen one any more; the page doesn't show it even if it did.
    const none = (confirmed) => annaEffective({
        Libraries: Object.assign(annaEffective().Libraries, { NoLibraries: true, NoLibrariesConfirmed: confirmed, Visible: [], Native: { EnableAllFolders: false, EnabledFolders: [HORROR] }, Drift: true }),
        Warnings: confirmed ? [] : ['Anna would have no libraries.']
    });
    for (const confirmed of [false, true]) {
        const env = await start({
            users: [user(ANNA, 'Anna', { RoleIds: [KID], State: 'Managed', LibrariesManaged: true, NoLibraries: true, NoLibrariesConfirmed: confirmed, Drift: true, IsHidden: true })],
            responses: [['GET', BASE + 'Users/' + ANNA + '/Effective', none(confirmed)]]
        });
        const panel = await openTab(env, 'Users');
        const row = panel.querySelector('[data-user-id="' + ANNA + '"]');
        assert.deepEqual(all(row, '.jfar-page-badge').map((b) => b.textContent), confirmed ? ['Changed outside Access Roles', 'No libraries (as chosen)'] : ['No libraries']);
        assert.ok(panel.textContent.includes('Unless that was chosen (Only… with nothing, and the box ticked), the library sync leaves their access as it is, and it isn\'t drift.'));

        await click(env.page, byText(panel, 'button', 'Anna'));
        const dialog = topDialog(env.page);
        const own = dialog.querySelector('#AccessRolesNoLibraries');
        const drift = dialog.querySelector('#AccessRolesDrift');
        const summary = dialog.querySelector('.jfar-page-effective-summary').textContent;
        if (confirmed) {
            assert.equal(own, null);
            assert.deepEqual(all(drift, 'button').map((b) => b.textContent), ['Keep as override', 'Revert this user…', 'Revert all drifted users…']);
            assert.ok(drift.textContent.includes('They were chosen to see no library (Only… with nothing, and the box ticked), so the library sync manages that like any other access: a library given to them outside Access Roles is drift.'));
            assert.equal(summary, 'They see no library (as chosen).');
        } else {
            assert.equal(drift, null, 'no drift section, no Keep or Revert');
            assert.equal(own.querySelector('h4').textContent, 'No libraries');
            assert.ok(own.textContent.includes('The library sync doesn\'t write that by accident: their access stays as it is (Horror) until a role or an Add gives them a library'), own.textContent);
            assert.equal(own.querySelector('button'), null);
            assert.equal(summary, 'The roles would leave them no library, so the library sync leaves their access as it is.');
            assert.equal(dialog.textContent.includes('To be written by the library sync'), false);
        }
    }
});

test('bulk: the preview is POST Users/Bulk?dryRun=true with each user\'s IfUpdatedAt, the save the same request; the server says who changes', { skip }, async () => {
    const env = await start();
    serve(env.page, 'POST', BASE + 'Users/Bulk', (entry) => {
        const changed = entry.body.RemoveRoleIds ? [ANNA] : [ANNA, BEN];
        return { body: change({ DryRun: entry.query.dryRun === 'true', Changed: changed }) };
    });
    const panel = await openTab(env, 'Users');
    await tick(env.page, panel.querySelector('[data-user-id="' + ANNA + '"] input'), true);
    await tick(env.page, panel.querySelector('[data-user-id="' + BEN + '"] input'), true);
    await click(env.page, panel.querySelector('#AccessRolesBulkAssign'));
    let dialog = topDialog(env.page);
    assert.equal(dialogTitle(dialog), 'Assign roles for 2 users');
    await tick(env.page, dialog.querySelector('input[data-role-id="' + FAMILY + '"]'), true);
    await click(env.page, byText(dialog, 'button', 'Continue'));
    const bulk = requests(env.page, 'POST', BASE + 'Users/Bulk');
    const tokens = [{ UserId: ANNA, UpdatedUtc: UPDATED }, { UserId: BEN, UpdatedUtc: null }];
    assert.deepEqual([bulk[0].query, bulk[0].body], [{ dryRun: 'true' }, { UserIds: [ANNA, BEN], IfUpdatedAt: tokens, AddRoleIds: [FAMILY] }]);
    assert.equal(requests(env.page, 'GET', /\/Effective$/).length, 0, 'no effective view fetched to guess the assignments');
    await click(env.page, byText(topDialog(env.page), 'button', 'Assign'));
    const saved = requests(env.page, 'POST', BASE + 'Users/Bulk')[1];
    assert.deepEqual([saved.query, saved.body], [{}, bulk[0].body]);
    assert.equal(dialogs(env.page).length, 0);
    assert.ok(env.page.document.querySelector('.jfar-toasts').textContent.includes('Roles given to 2 users.'));
    assert.equal(env.root.querySelector('.jfar-page-count').textContent, 'No user selected');

    const again = env.root.querySelector('#AccessRolesPanelUsers');
    await tick(env.page, again.querySelector('[data-user-id="' + ANNA + '"] input'), true);
    await tick(env.page, again.querySelector('[data-user-id="' + BEN + '"] input'), true);
    await click(env.page, again.querySelector('#AccessRolesBulkRemove'));
    dialog = topDialog(env.page);
    await tick(env.page, dialog.querySelector('input[data-role-id="' + KID + '"]'), true);
    await click(env.page, byText(dialog, 'button', 'Continue'));
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Users/Bulk').at(-1).body.RemoveRoleIds, [KID]);
    await click(env.page, byText(topDialog(env.page), 'button', 'Remove'));
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Users/Bulk').at(-1).body, { UserIds: [ANNA, BEN], IfUpdatedAt: tokens, RemoveRoleIds: [KID] });
    assert.ok(env.page.document.querySelector('.jfar-toasts').textContent.includes('Roles removed from 1 user.'));

    // Nobody would change: the server's Changed is empty, and Save stays off with the reason.
    serve(env.page, 'POST', BASE + 'Users/Bulk', () => ({ body: change({ DryRun: true, Changed: [] }) }));
    const third = env.root.querySelector('#AccessRolesPanelUsers');
    await tick(env.page, third.querySelector('[data-user-id="' + BEN + '"] input'), true);
    await click(env.page, third.querySelector('#AccessRolesBulkRemove'));
    await tick(env.page, topDialog(env.page).querySelector('input[data-role-id="' + KID + '"]'), true);
    await click(env.page, byText(topDialog(env.page), 'button', 'Continue'));
    const review = topDialog(env.page);
    assert.equal(byText(review, 'button', 'Remove').disabled, true);
    assert.ok(review.querySelector('.jfar-page-error').textContent.includes('None of them has these roles.'));
});

test('the per-user panel: the stored assignment, the overrides with their sources, and the effective access', { skip }, async () => {
    const saved = annaEffective({ Assignment: { RoleIds: [KID, FAMILY], LockOverrides: { Home: 'Unlock', Controls: 'Lock' }, Libraries: { Mode: 'Inherit', Add: [], Remove: [HORROR], ConfirmNoLibraries: false }, State: 'Managed' } });
    const env = await start();
    serve(env.page, 'PUT', BASE + 'Users/' + ANNA, dryOrSave({}, change({ Effective: saved })));
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Anna'));
    const dialog = topDialog(env.page);
    assert.equal(dialogTitle(dialog), 'Anna');
    assert.equal(dialog.querySelector('#AccessRolesUserRoles input[data-role-id="' + KID + '"]').checked, true);
    assert.equal(dialog.querySelector('#AccessRolesUserRoles input[data-role-id="' + FAMILY + '"]').checked, false);
    assert.equal(dialog.querySelector('select[data-lock="Home"]').value, 'Unlock');
    assert.equal(dialog.querySelector('select[data-lock="Profile"]').value, 'Inherit');
    const overrides = dialog.querySelector('#AccessRolesUserOverrides').textContent;
    assert.ok(overrides.includes('Now: Locked by role Kid'));
    assert.ok(overrides.includes('Now: Unlocked for this user'));
    assert.ok(overrides.includes('Now: Locked for this user (hidden only)'));
    assert.equal(dialog.querySelector('#AccessRolesUserMode').value, 'Inherit');
    assert.equal(dialog.querySelector('[data-list="Remove"] input[data-library-id="' + HORROR + '"]').checked, true);
    assert.ok(dialog.textContent.includes('Jellyfin shows this user on the login screen'));
    // The stock Library access page stays an option for a user Access Roles doesn't otherwise manage.
    assert.ok(dialog.textContent.includes('can also stay on the Library access tab of their Jellyfin user page'));

    const view = dialog.querySelector('#AccessRolesEffective').textContent;
    assert.ok(view.includes('Movies: granted by role Kid'));
    assert.ok(view.includes('Horror: removed for this user'));
    assert.ok(view.includes('Cartoons: not granted by any role'));
    assert.ok(view.includes('the libraries of their roles'));
    assert.ok(view.includes('They see 1 library.'));

    await tick(env.page, dialog.querySelector('#AccessRolesUserRoles input[data-role-id="' + FAMILY + '"]'), true);
    await choose(env.page, dialog.querySelector('select[data-lock="Playback"]'), 'Lock');
    await click(env.page, dialog.querySelector('#AccessRolesUserSave'));
    const dry = requests(env.page, 'PUT', BASE + 'Users/' + ANNA)[0];
    assert.deepEqual(dry.query, { dryRun: 'true' });
    const body = dry.body;
    assert.deepEqual(body, {
        UserId: ANNA,
        RoleIds: [KID, FAMILY],
        LockOverrides: { Home: 'Unlock', Controls: 'Lock', Playback: 'Lock' },
        Libraries: { Mode: 'Inherit', Add: [], Remove: [HORROR], ConfirmNoLibraries: false },
        State: 'Managed',
        IfUpdatedAt: UPDATED
    });
    await click(env.page, byText(topDialog(env.page), 'button', 'Save'));
    assert.deepEqual(requests(env.page, 'PUT', BASE + 'Users/' + ANNA)[1].body, body);
    assert.deepEqual(requests(env.page, 'PUT', BASE + 'Users/' + ANNA)[1].query, {});
    const after = topDialog(env.page);
    assert.equal(dialogTitle(after), 'Anna', 'the panel stays open with the saved access');
    assert.equal(after.querySelector('#AccessRolesUserRoles input[data-role-id="' + FAMILY + '"]').checked, true);
    assert.ok(env.page.document.querySelector('.jfar-toasts').textContent.includes('Saved Anna.'));
});

test('an administrator\'s panel shows the stored roles the effective view leaves out, and says the locks don\'t apply', { skip }, async () => {
    const env = await start();
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Root'));
    const dialog = topDialog(env.page);
    assert.equal(dialog.querySelector('#AccessRolesUserRoles input[data-role-id="' + FAMILY + '"]').checked, true);
    assert.ok(dialog.textContent.includes('An administrator: the locks of their roles don\'t apply, the libraries do.'));
    assert.ok(dialog.textContent.includes('Not locked: administrators are not restricted'));
    assert.equal(dialog.querySelector('#AccessRolesDrift'), null);
});

test('drift under ReportOnly: Keep as override saves their current access as their own; Revert all drifted users previews the sync, then writes it', { skip }, async () => {
    const env = await start({ responses: [
        ['POST', BASE + 'Sync', { DryRun: true, Written: 0, PlanToken: 'plan-1', Users: [{ UserId: ANNA, Name: 'Anna', Action: 'Write', Reason: 'Drift', Native: { EnableAllFolders: false, EnabledFolders: [] }, Added: [], Removed: [{ Id: HORROR, Name: 'Horror' }] }] }]
    ] });
    serve(env.page, 'PUT', BASE + 'Users/' + ANNA, dryOrSave({}, change({ Effective: annaEffective({ Libraries: Object.assign({}, annaEffective().Libraries, { Drift: false }) }) })));
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Anna'));
    let dialog = topDialog(env.page);
    const drift = dialog.querySelector('#AccessRolesDrift').textContent;
    assert.ok(drift.includes('Now: Movies, Horror. Access Roles last wrote: Movies.'));
    await click(env.page, dialog.querySelector('#AccessRolesDriftKeep'));
    const keep = requests(env.page, 'PUT', BASE + 'Users/' + ANNA)[0].body;
    assert.deepEqual(keep.Libraries, { Mode: 'Only', Add: [MOVIES, HORROR], Remove: [], ConfirmNoLibraries: false });
    assert.equal(dialogTitle(topDialog(env.page)), 'Keep their current library access as their own');
    await click(env.page, byText(topDialog(env.page), 'button', 'Save'));
    assert.equal(requests(env.page, 'PUT', BASE + 'Users/' + ANNA).length, 2);
    dialog = topDialog(env.page);
    assert.equal(dialog.querySelector('#AccessRolesDrift'), null, 'the saved access has no drift');

    // Revert, from a panel that shows the drift.
    await click(env.page, byText(dialog, 'button', 'Close'));
    await click(env.page, byText(env.root.querySelector('#AccessRolesPanelUsers'), 'button', 'Anna'));
    const revert = topDialog(env.page).querySelector('#AccessRolesDriftRevert');
    assert.equal(revert.textContent, 'Revert all drifted users…');
    await click(env.page, revert);
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Sync')[0].query, { dryRun: 'true', reapplyDrift: 'true' });
    const confirm = topDialog(env.page);
    assert.equal(dialogTitle(confirm), 'Revert all drifted users');
    assert.ok(confirm.textContent.includes('Reverting writes the library access of 1 user:'));
    assert.ok(confirm.textContent.includes('Anna: added nothing; removed Horror'));
    await click(env.page, byText(confirm, 'button', 'Revert'));
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Sync')[1].query, { reapplyDrift: 'true', ifPlan: 'plan-1' });
});

function unmanagePreview(entry) {
    return preview({
        Users: [{ UserId: ANNA, Name: 'Anna', IsAdministrator: false, StateBefore: 'Managed', StateAfter: 'NotManaged', LocksAdded: [], LocksRemoved: ['Profile', 'Home'], LibrariesAdded: [], LibrariesRemoved: [], PolicyWrite: true, NoLibraries: false, NoLibrariesRefused: false, LeavesManagement: { Choice: entry.body.Choice, FrozenAt: ['Movies', 'Horror'], After: { EnableAllFolders: true, EnabledFolders: [] } }, Warnings: [] }]
    });
}

test('Unmanage user: the choice is always asked first, then POST Unmanage?dryRun=true with it and the assignment\'s IfUpdatedAt, and the save sends the same', { skip }, async () => {
    const env = await start();
    serve(env.page, 'POST', BASE + 'Users/' + ANNA + '/Unmanage', dryOrSave((entry) => ({ Preview: unmanagePreview(entry) }), change({ Effective: effective(ANNA) })));
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Anna'));
    await click(env.page, topDialog(env.page).querySelector('#AccessRolesUnmanage'));
    const review = topDialog(env.page);
    assert.equal(dialogTitle(review), 'Unmanage Anna');
    const save = byText(review, 'button', 'Unmanage');
    assert.equal(save.disabled, true);
    assert.equal(requests(env.page, 'POST', BASE + 'Users/' + ANNA + '/Unmanage').length, 0, 'no dry run without a choice');
    assert.ok(review.textContent.includes('Choose what their library access becomes; the preview follows.'));
    await tick(env.page, review.querySelector('input[type="radio"][value="Restore"]'), true);
    const dry = requests(env.page, 'POST', BASE + 'Users/' + ANNA + '/Unmanage')[0];
    assert.deepEqual([dry.query, dry.body], [{ dryRun: 'true' }, { Choice: 'Restore', IfUpdatedAt: UPDATED }]);
    assert.ok(review.textContent.includes('Managed → Not managed'));
    assert.ok(review.textContent.includes('Unlocked: Profile (password, picture, name), Home screen'));
    assert.ok(review.textContent.includes('Their original access is restored: every library.'));
    await click(env.page, save);
    const sent = requests(env.page, 'POST', BASE + 'Users/' + ANNA + '/Unmanage')[1];
    assert.deepEqual(sent.body, { Choice: 'Restore', IfUpdatedAt: UPDATED });
    assert.deepEqual(sent.query, {});
});

test('Enter (a TV remote\'s OK) toggles a checkbox on the page and in a dialog', { skip }, async () => {
    const env = await start({ tv: true });
    const panel = await openTab(env, 'Users');
    const select = panel.querySelector('[data-user-id="' + ANNA + '"] input');
    select.focus();
    env.page.key(select, 'Enter');
    await env.page.flush();
    assert.equal(select.checked, true);
    assert.equal(panel.querySelector('.jfar-page-count').textContent, '1 user selected');
    // OK on a button is the browser's click; the button has the focus first.
    panel.querySelector('#AccessRolesBulkAssign').focus();
    await click(env.page, panel.querySelector('#AccessRolesBulkAssign'));
    const dialog = topDialog(env.page);
    const box = dialog.querySelector('input[data-role-id="' + FAMILY + '"]');
    box.focus();
    env.page.key(box, 'Enter');
    await env.page.flush();
    assert.equal(box.checked, true);
    assert.equal(byText(dialog, 'button', 'Continue').disabled, false);
    env.page.key(box, 'Escape');
    await env.page.flush();
    assert.equal(dialogs(env.page).length, 0);
    assert.equal(env.page.document.activeElement, env.root.querySelector('#AccessRolesBulkAssign'), 'the focus is back on the button that opened it');
});

test('every server value is written as text', { skip }, async () => {
    const markup = '<img src=x onerror=alert(1)>';
    const env = await start({ roles: [role(KID, markup, { Description: '<b>bold</b>' })], users: [user(ANNA, '<script>x</script>', { RoleIds: [KID], Summary: '<i>i</i>' })] });
    const roles = await openTab(env, 'Roles');
    assert.equal(roles.querySelector('.jfar-page-role-name').textContent, markup);
    await openTab(env, 'Users');
    assert.equal(all(env.page.document.body, 'img').length, 0);
    assert.equal(all(env.page.document.body, 'b').length, 0);
    assert.equal(all(env.page.document.body, 'script').length, 0);
    assert.equal(all(env.page.document.body, 'i').length, 0);
});

test('a list that fails to load says so on its tab; the page keeps working', { skip }, async () => {
    const env = await start({ responses: [['GET', BASE + 'Roles', { title: 'Server error', detail: 'The roles are on fire.', status: 500 }, 500]] });
    const panel = await openTab(env, 'Roles');
    assert.ok(panel.querySelector('.jfar-page-banner-error').textContent.includes('The roles couldn\'t be loaded: The roles are on fire.'));
    const usersPanel = await openTab(env, 'Users');
    assert.ok(usersPanel.querySelector('[data-user-id="' + ANNA + '"]'));
    assert.equal(env.page.errors.length, 0);
});

test('a view shown again from the cache loads again; the viewshow right after mount doesn\'t load twice', { skip }, async () => {
    const env = await start();
    env.page.dispatch(env.view, 'viewshow');
    await env.page.flush();
    assert.equal(requests(env.page, 'GET', BASE + 'Roles').length, 1);
    const realNow = Date.now;
    Date.now = () => realNow() + 5000;
    try {
        env.page.dispatch(env.view, 'viewshow');
        await env.page.flush();
    } finally {
        Date.now = realNow;
    }

    assert.equal(requests(env.page, 'GET', BASE + 'Roles').length, 2);
});

// Late answers, 409 Changed, and Reload and keep my changes.

test('a late dry run never replaces a newer one, and Save sends the request on screen', { skip }, async () => {
    const env = await start();
    const held = later();
    serve(env.page, 'POST', BASE + 'Users/' + ANNA + '/Unmanage', (entry) => {
        if (entry.query.dryRun !== 'true') {
            return { body: change({ Effective: effective(ANNA) }) };
        }

        const answer = { body: change({ DryRun: true, Preview: unmanagePreview(entry) }) };
        return entry.body.Choice === 'Restore' ? held.promise.then(() => answer) : answer;
    });
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Anna'));
    await click(env.page, topDialog(env.page).querySelector('#AccessRolesUnmanage'));
    const review = topDialog(env.page);
    const save = byText(review, 'button', 'Unmanage');
    await tick(env.page, review.querySelector('input[type="radio"][value="Restore"]'), true);
    assert.equal(save.disabled, true, 'nothing on screen while the dry run runs');
    await tick(env.page, review.querySelector('input[type="radio"][value="Keep"]'), true);
    assert.ok(review.textContent.includes('Library access stays frozen at: Movies, Horror.'));
    assert.equal(save.disabled, false);

    // The Restore dry run answers last: it is dropped.
    held.resolve();
    await env.page.flush();
    assert.ok(review.textContent.includes('Library access stays frozen at: Movies, Horror.'));
    assert.equal(review.textContent.includes('Their original access is restored'), false);
    await click(env.page, save);
    const sent = requests(env.page, 'POST', BASE + 'Users/' + ANNA + '/Unmanage').filter((e) => e.query.dryRun !== 'true');
    assert.deepEqual(sent.map((e) => e.body), [{ Choice: 'Keep', IfUpdatedAt: UPDATED }]);
});

test('a 409 Changed says so in plain words, Save stays off, and Reload and keep my changes puts the edit over the new assignment', { skip }, async () => {
    const env = await start();
    // Since the panel was read, someone gave Anna a Playback lock and took her Home override away.
    const now = annaEffective({ UpdatedUtc: NEWER, Assignment: { RoleIds: [KID], LockOverrides: { Controls: 'Lock', Playback: 'Lock' }, Libraries: { Mode: 'Inherit', Add: [], Remove: [HORROR], ConfirmNoLibraries: false }, State: 'Managed' } });
    serve(env.page, 'GET', BASE + 'Users/' + ANNA + '/Effective', (entry) => ({ body: requests(env.page, 'GET', entry.path).length > 1 ? now : annaEffective() }));
    serve(env.page, 'PUT', BASE + 'Users/' + ANNA, (entry) => (entry.body.IfUpdatedAt === NEWER
        ? { body: change({ DryRun: entry.query.dryRun === 'true', Effective: now }) }
        : { status: 409, body: problem(409, 'Changed', 'The assignment was changed since you read it; nothing was changed. Read it again.', { Users: [{ UserId: ANNA, Name: 'Anna' }] }) }));
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Anna'));
    const dialog = topDialog(env.page);
    await tick(env.page, dialog.querySelector('#AccessRolesUserRoles input[data-role-id="' + FAMILY + '"]'), true);
    await click(env.page, dialog.querySelector('#AccessRolesUserSave'));
    const review = topDialog(env.page);
    const save = byText(review, 'button', 'Save');
    const stale = review.querySelector('.jfar-page-stale');
    assert.equal(hidden(stale), false);
    assert.ok(stale.textContent.includes('This changed after you opened it (Anna), in another tab or by another administrator, so nothing was saved.'));
    assert.equal(save.disabled, true);
    assert.equal(review.querySelector('.jfar-page-preview-users'), null, 'no stale preview stays on screen');

    await click(env.page, byText(review, 'button', 'Reload and keep my changes'));
    const puts = requests(env.page, 'PUT', BASE + 'Users/' + ANNA);
    assert.deepEqual(puts.at(-1).query, { dryRun: 'true' });
    assert.deepEqual(puts.at(-1).body, {
        UserId: ANNA,
        RoleIds: [KID, FAMILY],
        LockOverrides: { Controls: 'Lock', Playback: 'Lock' },
        Libraries: { Mode: 'Inherit', Add: [], Remove: [HORROR], ConfirmNoLibraries: false },
        State: 'Managed',
        IfUpdatedAt: NEWER
    }, 'the admin\'s Family over the assignment as it is now, with its token');
    assert.equal(hidden(stale), true);
    assert.equal(save.disabled, false);
    await click(env.page, save);
    assert.deepEqual([requests(env.page, 'PUT', BASE + 'Users/' + ANNA).at(-1).query, requests(env.page, 'PUT', BASE + 'Users/' + ANNA).at(-1).body], [{}, puts.at(-1).body]);
});

test('a 409 Changed from Save itself leaves Save off for the stale token; the bulk refusal names the users and Reload reads the list again', { skip }, async () => {
    const env = await start();
    // Read again after the 409: someone changed Ben meanwhile.
    serve(env.page, 'GET', BASE + 'Users', () => ({ body: users().map((u) => (u.UserId === BEN ? Object.assign(u, { UpdatedUtc: NEWER }) : u)) }));
    serve(env.page, 'POST', BASE + 'Users/Bulk', (entry) => {
        const ben = entry.body.IfUpdatedAt.filter((t) => t.UserId === BEN)[0];
        if (entry.query.dryRun === 'true' || ben.UpdatedUtc === NEWER) {
            return { body: change({ DryRun: entry.query.dryRun === 'true', Changed: [ANNA, BEN] }) };
        }

        return { status: 409, body: problem(409, 'Changed', 'Ben was changed since you read them; nothing was changed. Read them again.', { Users: [{ UserId: BEN, Name: 'Ben' }] }) };
    });
    await env.page.flush();
    const panel = await openTab(env, 'Users');
    await tick(env.page, panel.querySelector('#AccessRolesSelectAll'), true);
    await click(env.page, panel.querySelector('#AccessRolesBulkAssign'));
    await tick(env.page, topDialog(env.page).querySelector('input[data-role-id="' + FAMILY + '"]'), true);
    await click(env.page, byText(topDialog(env.page), 'button', 'Continue'));
    const review = topDialog(env.page);
    const save = byText(review, 'button', 'Assign');
    assert.equal(save.disabled, false);
    await click(env.page, save);
    assert.ok(review.querySelector('.jfar-page-stale').textContent.includes('(Ben)'));
    assert.equal(save.disabled, true, 'Save stays off after the 409');
    assert.equal(dialogs(env.page).length, 2, 'nothing saved, the review stays open');

    await click(env.page, byText(review, 'button', 'Reload and keep my changes'));
    const bulk = requests(env.page, 'POST', BASE + 'Users/Bulk');
    assert.deepEqual(bulk.at(-1).query, { dryRun: 'true' });
    assert.deepEqual(bulk.at(-1).body.IfUpdatedAt.filter((t) => t.UserId === BEN)[0].UpdatedUtc, NEWER);
    assert.deepEqual(bulk.at(-1).body.AddRoleIds, [FAMILY], 'the roles to give stay the admin\'s');
    await click(env.page, save);
    assert.equal(dialogs(env.page).length, 0);
    assert.ok(env.page.document.querySelector('.jfar-toasts').textContent.includes('Roles given to 2 users.'));
});

test('Reload and keep my changes on a role puts the admin\'s edit over the role as it is now, with its new tokens', { skip }, async () => {
    const env = await start();
    // Read again after the 409: since the editor opened, someone added the Playback lock.
    serve(env.page, 'GET', BASE + 'Roles', () => ({ body: roles().map((r) => (r.Id === KID ? Object.assign(r, { Locks: r.Locks.concat(['Playback']), UpdatedUtc: NEWER, MembersToken: 'members-Kid-2' }) : r)) }));
    serve(env.page, 'PUT', BASE + 'Roles/' + KID, (entry) => (entry.body.IfUpdatedAt === NEWER && entry.body.IfMembers === 'members-Kid-2'
        ? { body: change({ DryRun: entry.query.dryRun === 'true', Role: role(KID, 'Kid') }) }
        : { status: 409, body: problem(409, 'Changed', 'The role was changed since you read it; nothing was changed. Read it again.') }));
    await env.page.flush();
    const panel = await openTab(env, 'Roles');
    await click(env.page, panel.querySelector('[data-role-id="' + KID + '"] [data-action="edit"]'));
    const editor = topDialog(env.page);
    await tick(env.page, lockBox(editor, 'Home'), false);
    await click(env.page, byText(editor, 'button', 'Save'));
    const review = topDialog(env.page);
    assert.ok(review.querySelector('.jfar-page-stale').textContent.includes('This changed after you opened it, in another tab or by another administrator, so nothing was saved.'));
    await click(env.page, byText(review, 'button', 'Reload and keep my changes'));
    const sent = requests(env.page, 'PUT', BASE + 'Roles/' + KID).at(-1);
    assert.deepEqual(sent.body.Locks.slice().sort(), ['AdminSurfaces', 'Playback', 'Profile', 'UserDirectory'], 'Home off (the admin\'s), Playback on (theirs)');
    assert.equal(sent.body.IfUpdatedAt, NEWER);
    assert.equal(sent.body.IfMembers, 'members-Kid-2');
    assert.equal(byText(review, 'button', 'Save').disabled, false);
});

test('Revert all drifted users lists whom it leaves out and why, offers Write anyway, and sends the dry run\'s PlanToken', { skip }, async () => {
    const env = await start();
    serve(env.page, 'POST', BASE + 'Sync', (entry) => {
        const confirmed = entry.query.confirmNoFullAdmin === 'true';
        if (entry.query.dryRun === 'true') {
            return { body: { DryRun: true, Written: 0, PlanToken: confirmed ? 'plan-confirmed' : 'plan-1', Users: [
                { UserId: ANNA, Name: 'Anna', Action: 'Write', Reason: 'Drift', Added: [], Removed: [{ Id: HORROR, Name: 'Horror' }] },
                { UserId: ROOT, Name: 'Root', Action: confirmed ? 'Write' : 'Skip', Reason: 'NoFullAdministrator', Added: [], Removed: [{ Id: MOVIES, Name: 'Movies' }] },
                { UserId: BEN, Name: 'Ben', Action: 'Skip', Reason: 'NoLibraries', Added: [], Removed: [] }
            ] } };
        }

        return entry.query.ifPlan === 'plan-confirmed'
            ? { body: { DryRun: false, Written: 2, Users: [] } }
            : { status: 409, body: problem(409, 'Changed', 'What the library sync would write changed since the dry run you confirmed; nothing was written.', { Users: [{ UserId: ANNA, Name: 'Anna' }] }) };
    });
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Anna'));
    await click(env.page, topDialog(env.page).querySelector('#AccessRolesDriftRevert'));
    const dialog = topDialog(env.page);
    const text = () => dialog.querySelector('#AccessRolesRevertPreview').textContent;
    assert.ok(text().includes('Reverting writes the library access of 1 user:'));
    assert.ok(text().includes('Root: no enabled administrator would see every library afterwards'));
    assert.ok(text().includes('Ben: they would have no libraries, and nobody confirmed that'));
    const box = dialog.querySelector('#AccessRolesRevertConfirmNoFullAdmin');
    assert.equal(hidden(box), false);

    // The first Revert: the plan changed meanwhile, so nothing is written and Revert stays off.
    const go = dialog.querySelector('#AccessRolesRevertConfirm');
    await click(env.page, go);
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Sync').at(-1).query, { reapplyDrift: 'true', ifPlan: 'plan-1' });
    assert.ok(dialog.querySelector('.jfar-page-stale').textContent.includes('What the library sync would write changed after this preview (Anna), so nothing was written.'));
    assert.equal(go.disabled, true);

    // Write anyway: the dry run again with confirmNoFullAdmin, and Revert sends both.
    await tick(env.page, box, true);
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Sync').at(-1).query, { dryRun: 'true', reapplyDrift: 'true', confirmNoFullAdmin: 'true' });
    assert.ok(text().includes('Reverting writes the library access of 2 users:'));
    await click(env.page, go);
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Sync').at(-1).query, { reapplyDrift: 'true', ifPlan: 'plan-confirmed', confirmNoFullAdmin: 'true' });
    assert.equal(dialogs(env.page).length, 1, 'the Revert dialog closed; the panel loads again');
    assert.ok(env.page.document.querySelector('.jfar-toasts').textContent.includes('Library access written for 2 users.'));
});

test('the panel sends no list its library mode hides: All sends no Add, Only no Remove', { skip }, async () => {
    const env = await start();
    serve(env.page, 'PUT', BASE + 'Users/' + ANNA, dryOrSave({}, change({ Effective: annaEffective() })));
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Anna'));
    const dialog = topDialog(env.page);
    await choose(env.page, dialog.querySelector('#AccessRolesUserMode'), 'Only');
    assert.equal(hidden(dialog.querySelector('[data-list="Remove"]')), true);
    await tick(env.page, dialog.querySelector('[data-list="Add"] input[data-library-id="' + MOVIES + '"]'), true);
    await click(env.page, dialog.querySelector('#AccessRolesUserSave'));
    assert.deepEqual(requests(env.page, 'PUT', BASE + 'Users/' + ANNA).at(-1).body.Libraries, { Mode: 'Only', Add: [MOVIES], Remove: [], ConfirmNoLibraries: false });
    await click(env.page, byText(topDialog(env.page), 'button', 'Cancel'));

    await choose(env.page, dialog.querySelector('#AccessRolesUserMode'), 'All');
    await click(env.page, dialog.querySelector('#AccessRolesUserSave'));
    assert.deepEqual(requests(env.page, 'PUT', BASE + 'Users/' + ANNA).at(-1).body.Libraries, { Mode: 'All', Add: [], Remove: [HORROR], ConfirmNoLibraries: false });
});

test('the tab: remembered in this browser, the route\'s tab wins, the Overview while the import is offered; storage off is fine', { skip }, async () => {
    const env = await start();
    await click(env.page, tab(env, 'Users'));
    assert.equal(env.page.context.localStorage.getItem('jfar:adminTab'), '"Users"');

    const remembered = await start({ beforeMount: (page) => page.context.localStorage.setItem('jfar:adminTab', '"Roles"') });
    assert.equal(tab(remembered, 'Roles').getAttribute('aria-selected'), 'true');

    const routed = await start({ params: { tab: 'Users' }, beforeMount: (page) => page.context.localStorage.setItem('jfar:adminTab', '"Roles"') });
    assert.equal(tab(routed, 'Users').getAttribute('aria-selected'), 'true');

    const importing = await start({
        diagnostics: diagnostics({ Records: { Roles: 0, Assignments: 0, KnownLibraries: 4, LedgerEntries: 0, PendingReleases: 0 } }),
        beforeMount: (page) => page.context.localStorage.setItem('jfar:adminTab', '"Users"')
    });
    assert.equal(tab(importing, 'Overview').getAttribute('aria-selected'), 'true', 'the import is offered on the Overview');
    assert.equal(hidden(importing.root.querySelector('#AccessRolesImport')), false);

    const held = await start({
        diagnostics: diagnostics({ LibrarySync: { State: 'Paused', PausedBecause: 'StoreRestored', DriftPolicy: 'ReportOnly', Totals: {} } }),
        beforeMount: (page) => page.context.localStorage.setItem('jfar:adminTab', '"Roles"')
    });
    assert.equal(tab(held, 'Overview').getAttribute('aria-selected'), 'true', 'the hold after a restore is on the Overview');

    const blocked = await start({
        beforeMount: (page) => Object.defineProperty(page.context, 'localStorage', { configurable: true, get() {
            throw new Error('The operation is insecure.');
        } })
    });
    assert.equal(tab(blocked, 'Overview').getAttribute('aria-selected'), 'true');
    await click(blocked.page, tab(blocked, 'Roles'));
    assert.equal(tab(blocked, 'Roles').getAttribute('aria-selected'), 'true');
    assert.equal(blocked.page.errors.length, 0);
});

test('names an admin typed are isolated (bdi, dir="auto")', { skip }, async () => {
    const env = await start({ roles: [role(KID, 'אבג Kid'), role(FAMILY, 'Family')], users: [user(ANNA, 'علي', { RoleIds: [KID], UpdatedUtc: UPDATED })] });
    const roles = await openTab(env, 'Roles');
    assert.equal(roles.querySelector('.jfar-page-role-name').getAttribute('dir'), 'auto');
    const line = all(roles, '.jfar-page-role-line')[0];
    assert.equal(line.textContent, 'Members: علي');
    assert.equal(line.querySelector('bdi').textContent, 'علي');
    const usersPanel = await openTab(env, 'Users');
    assert.equal(usersPanel.querySelector('.jfar-page-user-name').getAttribute('dir'), 'auto');
});

// Each dry run answers its own PreviewToken (a counter), so a save shows which answer it took.
function tokened(prefix, changes) {
    let n = 0;
    return (entry) => Object.assign({ Preview: preview({ PreviewToken: prefix + '-' + (++n) }) }, typeof changes === 'function' ? changes(entry) : changes || {});
}

// What has the focus, by its id or its text (never the element itself: assert would print the whole fake page).
function focusName(page) {
    const active = page.document.activeElement;
    return !active ? null : (active === page.document.body ? 'body' : (active.id || active.tagName + ': ' + active.textContent));
}

function saves(page, method, path) {
    return requests(page, method, path).filter((e) => e.query.dryRun !== 'true');
}

test('each save on the Roles tab sends its shown dry run\'s PreviewToken as ifPreview', { skip }, async () => {
    // Creating a role.
    let env = await start({ roles: [], users: [user(BEN, 'Ben')] });
    serve(env.page, 'POST', BASE + 'Roles', dryOrSave(tokened('create'), change({ Role: role(KID, 'Kid') })));
    let panel = await openTab(env, 'Roles');
    await click(env.page, panel.querySelector('[data-preset="Kid"]'));
    await tick(env.page, topDialog(env.page).querySelector('input[data-library-id="' + MOVIES + '"]'), true);
    await click(env.page, byText(topDialog(env.page), 'button', 'Create'));
    await click(env.page, byText(topDialog(env.page), 'button', 'Create'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Roles').map((e) => e.query), [{ ifPreview: 'create-1' }]);

    // Deleting one with its members reassigned, and the default roles.
    env = await start();
    serve(env.page, 'DELETE', BASE + 'Roles/' + KID, dryOrSave(tokened('delete'), change()));
    serve(env.page, 'PUT', BASE + 'DefaultRoles', dryOrSave(tokened('defaults'), change({ DefaultRoles: [{ Id: KID, Name: 'Kid' }] })));
    panel = await openTab(env, 'Roles');
    await click(env.page, panel.querySelector('.jfar-page-role[data-role-id="' + KID + '"] [data-action="delete"]'));
    await choose(env.page, topDialog(env.page).querySelector('select'), FAMILY);
    await click(env.page, byText(topDialog(env.page), 'button', 'Continue'));
    await click(env.page, byText(topDialog(env.page), 'button', 'Delete'));
    assert.deepEqual(saves(env.page, 'DELETE', BASE + 'Roles/' + KID).map((e) => e.query), [{ ifPreview: 'delete-1', reassignTo: FAMILY, ifUpdatedAt: UPDATED, ifMembers: 'members-Kid' }]);
    await tick(env.page, panel.querySelector('#AccessRolesDefaultRoles input[data-role-id="' + KID + '"]'), true);
    await click(env.page, panel.querySelector('#AccessRolesDefaultRolesSave'));
    await click(env.page, byText(topDialog(env.page), 'button', 'Save'));
    assert.deepEqual(saves(env.page, 'PUT', BASE + 'DefaultRoles').map((e) => e.query), [{ ifPreview: 'defaults-1' }]);
});

test('Save sends the shown request, options and PreviewToken, whatever changed behind the dialog', { skip }, async () => {
    const env = await start();
    serve(env.page, 'PUT', BASE + 'Users/' + ANNA, dryOrSave(tokened('put'), change({ Effective: annaEffective() })));
    serve(env.page, 'POST', BASE + 'Users/Bulk', dryOrSave(tokened('bulk', { Changed: [ANNA, BEN] }), change({ Changed: [ANNA, BEN] })));
    const panel = await openTab(env, 'Users');

    // Bulk.
    await tick(env.page, panel.querySelector('[data-user-id="' + ANNA + '"] input'), true);
    await tick(env.page, panel.querySelector('[data-user-id="' + BEN + '"] input'), true);
    await click(env.page, panel.querySelector('#AccessRolesBulkAssign'));
    await tick(env.page, topDialog(env.page).querySelector('input[data-role-id="' + FAMILY + '"]'), true);
    await click(env.page, byText(topDialog(env.page), 'button', 'Continue'));
    await click(env.page, byText(topDialog(env.page), 'button', 'Assign'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Users/Bulk').map((e) => e.query), [{ ifPreview: 'bulk-1' }]);

    // The panel: the admin's form changes behind the review, and its box is ticked with no change event; Save sends
    // the request and the options the dry run on screen ran with.
    await click(env.page, byText(panel, 'button', 'Anna'));
    const form = topDialog(env.page);
    await tick(env.page, form.querySelector('#AccessRolesUserRoles input[data-role-id="' + FAMILY + '"]'), true);
    await click(env.page, form.querySelector('#AccessRolesUserSave'));
    const review = topDialog(env.page);
    const shown = requests(env.page, 'PUT', BASE + 'Users/' + ANNA).at(-1);
    assert.deepEqual([shown.query, shown.body.RoleIds], [{ dryRun: 'true' }, [KID, FAMILY]]);
    await tick(env.page, form.querySelector('#AccessRolesUserRoles input[data-role-id="' + FAMILY + '"]'), false);
    review.querySelector('input[data-ar-focus="confirm-no-full-admin"]').checked = true;
    assert.equal(requests(env.page, 'PUT', BASE + 'Users/' + ANNA).length, 1, 'no dry run of the form behind the review');
    await click(env.page, byText(review, 'button', 'Save'));
    const sent = saves(env.page, 'PUT', BASE + 'Users/' + ANNA);
    assert.deepEqual(sent.map((e) => [e.query, e.body]), [[{ ifPreview: 'put-1' }, shown.body]]);
});

test('a late dry-run error is dropped, and Unmanage sends the shown choice\'s PreviewToken', { skip }, async () => {
    const env = await start();
    const held = later();
    const next = tokened('unmanage');
    serve(env.page, 'POST', BASE + 'Users/' + ANNA + '/Unmanage', (entry) => {
        if (entry.query.dryRun !== 'true') {
            return { body: change({ Effective: effective(ANNA) }) };
        }

        const answer = { body: change(Object.assign({ DryRun: true }, next(entry), { Preview: Object.assign(unmanagePreview(entry), { PreviewToken: 'unmanage-' + entry.body.Choice }) })) };
        return entry.body.Choice === 'Restore' ? held.promise : answer;
    });
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Anna'));
    await click(env.page, topDialog(env.page).querySelector('#AccessRolesUnmanage'));
    const review = topDialog(env.page);
    const save = byText(review, 'button', 'Unmanage');
    await tick(env.page, review.querySelector('input[type="radio"][value="Restore"]'), true);
    await tick(env.page, review.querySelector('input[type="radio"][value="Keep"]'), true);
    assert.ok(review.textContent.includes('Library access stays frozen at: Movies, Horror.'));

    // The Restore dry run fails after Keep's answered: nothing of it shows.
    held.resolve({ status: 500, body: problem(500, 'Error', 'The Restore dry run broke late.') });
    await env.page.flush();
    assert.equal(review.textContent.includes('broke late'), false);
    assert.ok(review.textContent.includes('Library access stays frozen at: Movies, Horror.'));
    assert.equal(save.disabled, false);
    await click(env.page, save);
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Users/' + ANNA + '/Unmanage').map((e) => [e.query, e.body]), [[{ ifPreview: 'unmanage-Keep' }, { Choice: 'Keep', IfUpdatedAt: UPDATED }]]);
});

test('a closed review takes no late answer, and a late 409 doesn\'t take the focus', { skip }, async () => {
    const env = await start();
    const held = later();
    serve(env.page, 'POST', BASE + 'Users/' + ANNA + '/Unmanage', () => held.promise);
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Anna'));
    await click(env.page, topDialog(env.page).querySelector('#AccessRolesUnmanage'));
    const review = topDialog(env.page);
    await tick(env.page, review.querySelector('input[type="radio"][value="Keep"]'), true);
    await click(env.page, byText(review, 'button', 'Cancel'));
    assert.equal(dialogs(env.page).length, 1, 'the review closed; the panel stays');
    const focused = env.page.document.activeElement;

    held.resolve({ status: 409, body: problem(409, 'Changed', 'The assignment was changed since you read it; nothing was changed. Read it again.', { Users: [{ UserId: ANNA, Name: 'Anna' }] }) });
    await env.page.flush();
    assert.ok(env.page.document.activeElement === focused, 'the focus stays where the close put it, not on ' + focusName(env.page));
    assert.equal(hidden(review.querySelector('.jfar-page-stale')), true);
    assert.equal(review.textContent.includes('This changed after you opened it'), false);
});

test('Revert all drifted users drops a late dry run, success or error, and a closed one takes none', { skip }, async () => {
    const env = await start();
    const holds = [];
    let holdUnconfirmed = false;
    serve(env.page, 'POST', BASE + 'Sync', (entry) => {
        const confirmed = entry.query.confirmNoFullAdmin === 'true';
        if (entry.query.dryRun !== 'true') {
            return { body: { DryRun: false, Written: 1, Users: [] } };
        }

        if (confirmed || holdUnconfirmed) {
            const h = later();
            holds.push(h);
            return h.promise;
        }

        return { body: { DryRun: true, Written: 0, PlanToken: 'plan-1', Users: [
            { UserId: ANNA, Name: 'Anna', Action: 'Write', Reason: 'Drift', Added: [], Removed: [{ Id: HORROR, Name: 'Horror' }] },
            { UserId: ROOT, Name: 'Root', Action: 'Skip', Reason: 'NoFullAdministrator', Added: [], Removed: [] }
        ] } };
    });
    const confirmedAnswer = { body: { DryRun: true, Written: 0, PlanToken: 'plan-confirmed', Users: [
        { UserId: ANNA, Name: 'Anna', Action: 'Write', Reason: 'Drift', Added: [], Removed: [{ Id: HORROR, Name: 'Horror' }] },
        { UserId: ROOT, Name: 'Root', Action: 'Write', Reason: 'NoFullAdministrator', Added: [], Removed: [{ Id: MOVIES, Name: 'Movies' }] }
    ] } };
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Anna'));
    await click(env.page, topDialog(env.page).querySelector('#AccessRolesDriftRevert'));
    const dialog = topDialog(env.page);
    const text = () => dialog.querySelector('#AccessRolesRevertPreview').textContent;
    const box = dialog.querySelector('#AccessRolesRevertConfirmNoFullAdmin');
    const go = dialog.querySelector('#AccessRolesRevertConfirm');
    assert.ok(text().includes('Reverting writes the library access of 1 user:'));

    // Write anyway, then not: the confirmed dry run answers last, and is dropped.
    await tick(env.page, box, true);
    await tick(env.page, box, false);
    holds[0].resolve(confirmedAnswer);
    await env.page.flush();
    assert.ok(text().includes('Reverting writes the library access of 1 user:'));
    assert.equal(go.disabled, false);

    // The same with an error.
    await tick(env.page, box, true);
    await tick(env.page, box, false);
    holds[1].resolve({ status: 500, body: problem(500, 'Error', 'The confirmed dry run broke late.') });
    await env.page.flush();
    assert.equal(dialog.textContent.includes('broke late'), false);
    assert.ok(text().includes('Reverting writes the library access of 1 user:'));
    await click(env.page, go);
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Sync').map((e) => e.query), [{ reapplyDrift: 'true', ifPlan: 'plan-1' }]);

    // A Revert dialog closed while its dry run runs: its answer is dropped.
    holdUnconfirmed = true;
    await click(env.page, topDialog(env.page).querySelector('#AccessRolesDriftRevert'));
    const closed = topDialog(env.page);
    assert.equal(dialogTitle(closed), 'Revert all drifted users');
    await click(env.page, byText(closed, 'button', 'Cancel'));
    holds[2].resolve(confirmedAnswer);
    await env.page.flush();
    assert.equal(closed.querySelector('#AccessRolesRevertPreview').textContent, '');
    assert.equal(closed.querySelector('#AccessRolesRevertConfirm').disabled, true);
});

test('while enforcement is off the Overview previews the sync\'s first run (a dry run only), and with enforcement on or a hold the box is gone', { skip }, async () => {
    const off = { LibrarySync: { State: 'Paused', PausedBecause: 'EnforcementDisabled', DriftPolicy: 'ReportOnly', Totals: {} } };
    const env = await start({ diagnostics: diagnostics(off) });
    let answer = { DryRun: true, Written: 0, Paused: 'EnforcementDisabled', PlanToken: 'p-1', Users: [] };
    serve(env.page, 'POST', BASE + 'Sync', () => ({ body: answer }));
    const box = env.root.querySelector('#AccessRolesEnforcementOff');
    const result = env.root.querySelector('#AccessRolesEnforcementOffResult');
    assert.equal(hidden(box), false);
    assert.ok(box.textContent.includes('After Import current setup it must write nobody'), box.textContent);
    await click(env.page, env.root.querySelector('#AccessRolesEnforcementOffPreview'));
    assert.ok(result.textContent.includes('Its first run writes nobody\'s library access, and leaves nobody out.'), result.textContent);

    answer = { DryRun: true, Written: 0, Paused: 'EnforcementDisabled', PlanToken: 'p-2', Users: [
        { UserId: ANNA, Name: 'Anna', Action: 'Write', Reason: 'FirstWrite', Added: [], Removed: [{ Id: HORROR, Name: 'Horror' }] },
        { UserId: BEN, Name: 'Ben', Action: 'Skip', Reason: 'NoLibraries', Added: [], Removed: [] }
    ] };
    await click(env.page, env.root.querySelector('#AccessRolesEnforcementOffPreview'));
    assert.ok(result.textContent.includes('Its first run writes the library access of 1 user:'), result.textContent);
    assert.ok(result.textContent.includes('Anna: added nothing; removed Horror'));
    assert.ok(result.textContent.includes('Ben: Skip (NoLibraries)'));
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Sync').map((e) => e.query), [{ dryRun: 'true' }, { dryRun: 'true' }], 'only dry runs, nothing confirmed');

    for (const changes of [{}, { LibrarySync: { State: 'Paused', PausedBecause: 'StoreRestored', DriftPolicy: 'ReportOnly', Totals: {} } }]) {
        const other = await start({ diagnostics: diagnostics(changes) });
        assert.equal(hidden(other.root.querySelector('#AccessRolesEnforcementOff')), true);
    }
});

test('the hold\'s Resume sends its preview\'s PlanToken as ifPlan, and the preview counts only the rows the run writes', { skip }, async () => {
    const env = await start({ diagnostics: diagnostics({ LibrarySync: { State: 'Paused', PausedBecause: 'StoreRestored', DriftPolicy: 'ReportOnly', Totals: {} } }) });
    let plans = 0;
    serve(env.page, 'POST', BASE + 'Sync', (entry) => {
        if (entry.query.dryRun === 'true') {
            return { body: { DryRun: true, Written: 0, PlanToken: 'hold-' + (++plans), Users: [
                { UserId: ANNA, Name: 'Anna', Action: 'Write', Reason: 'RoleChanged', Added: [{ Id: MOVIES, Name: 'Movies' }], Removed: [] },
                { UserId: BEN, Name: 'Ben', Action: 'Drift', Reason: 'ReportOnly', Added: [], Removed: [{ Id: HORROR, Name: 'Horror' }] },
                { UserId: ROOT, Name: 'Root', Action: 'Drift', Reason: 'Drift', Target: { EnableAllFolders: false, EnabledFolders: [MOVIES] }, Added: [], Removed: [{ Id: HORROR, Name: 'Horror' }] }
            ] } };
        }

        return entry.query.ifPlan === 'hold-2'
            ? { body: { DryRun: false, Written: 2, Users: [] } }
            : { status: 409, body: problem(409, 'Changed', 'What the library sync would write changed since the dry run you confirmed; nothing was written.', { Users: [{ UserId: ANNA, Name: 'Anna' }] }) };
    });
    const preview = env.root.querySelector('#AccessRolesHoldPreview');
    const confirm = env.root.querySelector('#AccessRolesHoldConfirm');
    const result = env.root.querySelector('#AccessRolesHoldResult');
    assert.equal(hidden(env.root.querySelector('#AccessRolesRestoreHold')), false);
    // A restore from the Data tab holds the sync too, so the box doesn't say the restore was at load only.
    assert.ok(env.root.querySelector('#AccessRolesRestoreHold').textContent.includes('restored from a snapshot (on the Data tab, or when it loaded)'));
    assert.equal(confirm.disabled, true);
    await click(env.page, preview);
    assert.ok(result.textContent.includes('Confirming writes the library access of 2 users:'), result.textContent);
    assert.ok(result.textContent.includes('Anna: added Movies; removed nothing'));
    assert.ok(result.textContent.includes('Root: added nothing; removed Horror'));
    assert.equal(result.textContent.includes('Ben'), false, 'a Drift row ReportOnly only reports isn\'t written');

    // The first Resume (the focus on it, as the D-pad leaves it): the plan changed since; nothing is written, Confirm stays
    // off until the sync is previewed again, and the focus goes to Preview.
    confirm.focus();
    await click(env.page, confirm);
    await click(env.page, byText(topDialog(env.page), 'button', 'Resume'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Sync').map((e) => e.query), [{ confirmRestore: 'true', ifPlan: 'hold-1' }]);
    assert.ok(result.textContent.includes('nothing was written'));
    assert.equal(confirm.disabled, true);
    assert.equal(focusName(env.page), 'AccessRolesHoldPreview', 'the focus on Preview, the way on');
    await click(env.page, preview);
    await click(env.page, confirm);
    await click(env.page, byText(topDialog(env.page), 'button', 'Resume'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Sync').at(-1).query, { confirmRestore: 'true', ifPlan: 'hold-2' });
    assert.equal(result.textContent, '');
});

test('Apply the import sends its preview\'s PreviewToken as ifPreview; after a 409 it needs a new preview', { skip }, async () => {
    const env = await start({ diagnostics: diagnostics({ Records: { Roles: 0, Assignments: 0, KnownLibraries: 4, LedgerEntries: 0, PendingReleases: 0 } }) });
    let previews = 0;
    serve(env.page, 'POST', BASE + 'ImportExisting', (entry) => {
        const dry = entry.query.dryRun === 'true';
        if (!dry && entry.query.ifPreview !== 'import-2') {
            return { status: 409, body: problem(409, 'Changed', 'What the import does isn\'t what its preview showed any more (for Ben). Nothing was changed. Preview it again.', { Users: [{ UserId: BEN, Name: 'Ben' }] }) };
        }

        return { body: {
            DryRun: dry,
            ProposedRoles: [{ Id: dry ? null : KID, Name: 'Movies', Mode: 'Only', Libraries: [{ Id: MOVIES, Name: 'Movies' }], Members: [{ UserId: BEN, Name: 'Ben' }] }],
            Assignments: [{ UserId: BEN, Name: 'Ben', RoleId: dry ? null : KID, RoleName: 'Movies' }],
            EffectiveChanges: [],
            NoChange: 1,
            Warnings: [],
            AuditSeq: [],
            PreviewToken: dry ? 'import-' + (++previews) : ''
        } };
    });
    const box = env.root.querySelector('#AccessRolesImport');
    const apply = env.root.querySelector('#AccessRolesImportApply');
    assert.equal(hidden(box), false);
    await tick(env.page, env.root.querySelector('#AccessRolesImportEveryLibrary'), true);
    await click(env.page, env.root.querySelector('#AccessRolesImportPreview'));
    assert.deepEqual(requests(env.page, 'POST', BASE + 'ImportExisting').at(-1).query, { dryRun: 'true', manageUsersWithEveryLibrary: 'true' });

    // Cancel: nothing sent, the focus back on Apply.
    apply.focus();
    await click(env.page, apply);
    await click(env.page, byText(topDialog(env.page), 'button', 'Cancel'));
    assert.equal(focusName(env.page), 'AccessRolesImportApply', 'the focus back on Apply');
    assert.equal(saves(env.page, 'POST', BASE + 'ImportExisting').length, 0);
    assert.equal(apply.disabled, false);

    await click(env.page, apply);
    await click(env.page, byText(topDialog(env.page), 'button', 'Apply'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'ImportExisting').map((e) => e.query), [{ ifPreview: 'import-1', manageUsersWithEveryLibrary: 'true' }]);
    assert.ok(env.root.querySelector('#AccessRolesImportResult').textContent.includes('Preview it again.'));
    assert.equal(apply.disabled, true, 'Apply stays off after the 409');
    assert.equal(focusName(env.page), 'AccessRolesImportPreview', 'the focus on Preview, the way on');

    await click(env.page, env.root.querySelector('#AccessRolesImportPreview'));
    await click(env.page, apply);
    await click(env.page, byText(topDialog(env.page), 'button', 'Apply'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'ImportExisting').at(-1).query, { ifPreview: 'import-2', manageUsersWithEveryLibrary: 'true' });
    assert.ok(env.root.querySelector('#AccessRolesImportResult').textContent.includes('Imported: 1 roles, 1 users.'));
});

// The dashboard stub (Configuration/AccessRolesJs.js), as jellyfin-web runs it: a module whose
// default export gets the view, imported from a data: URL here; ApiClient.getUrl names the AdminScript module.
const STUB = new URL('../../../Jellyfin.Plugin.AccessRoles/Configuration/AccessRolesJs.js', import.meta.url);

async function runStub(adminScriptUrl) {
    const page = createPage();
    globalThis.window = page.context;
    globalThis.document = page.document;
    page.installApiClient({ userId: 'admin-1', admin: true });
    globalThis.ApiClient = { getUrl: () => adminScriptUrl };
    const unhandled = [];
    const listener = (reason) => unhandled.push(reason);
    process.on('unhandledRejection', listener);
    try {
        const stub = await import('data:text/javascript;base64,' + Buffer.from(fs.readFileSync(STUB, 'utf8')).toString('base64'));
        const view = page.element('div', { id: 'AccessRolesConfigPage' });
        const note = page.element('p', { id: 'AccessRolesScriptNote', hidden: '' }, ['Loading the page…']);
        view.append(note);
        page.document.body.append(view);
        new stub.default(view, {});
        await new Promise((resolve) => setTimeout(resolve, 50));
        await page.flush();
        return { note, unhandled };
    } finally {
        process.off('unhandledRejection', listener);
        delete globalThis.ApiClient;
    }
}

test('the stub says so on the page when the script doesn\'t load or its mount throws, with no unhandled rejection', { skip: false }, async () => {
    const throwing = await runStub('data:text/javascript,' + encodeURIComponent('export function mount() { throw new Error("broken"); }'));
    assert.equal(throwing.note.textContent, 'The Access Roles page could not start. Reload the page; if it stays like this, the server log has the details.');
    assert.equal(throwing.note.getAttribute('role'), 'alert');
    assert.equal(throwing.note.hasAttribute('hidden'), false);
    assert.deepEqual(throwing.unhandled, []);

    const missing = await runStub(new URL('./no-such-admin-script.mjs', import.meta.url).href);
    assert.equal(missing.note.textContent, 'The Access Roles page could not load its script. Reload the page; if it stays like this, the server log has the details.');
    assert.deepEqual(missing.unhandled, []);

    const mounted = await runStub('data:text/javascript,' + encodeURIComponent('export function mount(view) { view.setAttribute("data-mounted", "yes"); }'));
    assert.equal(mounted.note.textContent, 'Loading the page…');
    assert.deepEqual(mounted.unhandled, []);
});

// ---------------------------------------------------------------------------------------------- the other tabs

const OLDKIDS = 'a0000000000000000000000000000005';
const ANIME = 'a0000000000000000000000000000006';
const CLEO = 'c0000000000000000000000000000004';
const JOINED = 'LibraryVisibility+ItemIds';

function enforcementSample(changes) {
    return Object.assign({
        Active: true,
        SettingsLocks: true,
        Hardening: true,
        Message: 'Enforced.',
        ServerWide: { ConfigurationReads: 'AllowList', ConfigurationReadAllowList: ['encoding'], BlockNonAdminRestart: true, TrimPublicUsers: true, LibraryHardening: 'Standard', FirstLoadMode: 'HideUntilKnown' },
        Blocked: { Totals: { Refused: 4, Reverted: 0, FailedClosed: 0, Hidden: 7 }, OpenWindows: { [JOINED + ' Hidden']: 3, 'Settings.Profile Refused': 1 }, WindowMinutes: 5 },
        NotEnforcedLocks: [],
        Filter: { FilterErrors: 2, LastError: 'The rule threw.', LastErrorUtc: UPDATED, DegradedBecause: null },
        Snapshot: { ManagedUsers: 1, LockedUsers: 1, DegradedBecause: null },
        Cost: { [JOINED]: { Count: 9, P50Ms: 0.2, P95Ms: 0.5, MaxMs: 1.1 }, ItemIds: { Count: 3, P50Ms: 0.1, P95Ms: 0.1, MaxMs: 0.2 } }
    }, changes || {});
}

function diagnosticsSample(changes) {
    return diagnostics(Object.assign({
        Enforcement: enforcementSample(),
        LibrarySync: { State: 'Active', DriftPolicy: 'ReportOnly', DriftCount: 1, ReconcileIntervalMinutes: 5, LastRun: { Utc: UPDATED, Trigger: 'Interval', DurationMs: 12, Written: 0, ManagedUsers: 1, Skipped: 0 }, Totals: {} },
        RuleTable: {
            CheckedUtc: UPDATED, FilterRegistered: true, EnforcementActive: true, Bound: 110, WithRules: 110, Missing: [], Changed: [], Unreviewed: [],
            JellyfinEnhanced: { Installed: true, Locked: ['SaveUserSettings'], Uncovered: [], UncoveredUnreviewed: [] },
            PluginActions: { 'Jellyfin.Plugin.AccessRoles': 30, 'Jellyfin.Plugin.SSO_Auth': 4, 'Jellyfin.Plugin.JellyfinEnhanced': 50 },
            Flagged: []
        },
        ActionManifest: { JellyfinVersion: '10.11.5', Source: 'jellyfin v10.11.5', Actions: 390, WithRules: 110, Server: { Matches: true, Actions: 390 } }
    }, changes || {}));
}

// The chips by their data-chip, as [kind, text], in order.
function chipMap(root) {
    const map = {};
    all(root, '#AccessRolesChips [data-chip]').forEach((c) => {
        map[c.getAttribute('data-chip')] = [c.getAttribute('data-kind'), c.textContent];
    });
    return map;
}

// The web client's script ran in this browser (window.__jfkit.jfar), of a version or of the page's own (none given).
function webClient(version) {
    return (page) => {
        page.context.__jfkit = { jfar: version ? { version } : {} };
    };
}

test('the Overview: the chips say what enforces, the web client, the sync and its drift, who has no library or is on the login screen, other policy managers, JE and what failed', { skip }, async () => {
    const cleo = user(CLEO, 'Cleo', { RoleIds: [KID], State: 'Managed', LibrariesManaged: true, NoLibraries: true });
    const env = await start({ diagnostics: diagnosticsSample(), users: users().concat([cleo]), beforeMount: webClient() });
    const chips = chipMap(env.root);
    assert.deepEqual(Object.keys(chips), ['enforcement', 'webui', 'sync', 'drift', 'nolibraries', 'administrators', 'loginscreen', 'policymanagers', 'je', 'filtererrors', 'degraded']);
    assert.deepEqual(chips.enforcement, ['ok', 'Enforcement active']);
    assert.deepEqual(chips.webui, ['ok', 'Web UI injected']);
    assert.equal(chips.sync[0], 'ok');
    assert.ok(/^Last sync: .+ \(Interval\)$/.test(chips.sync[1]), chips.sync[1]);
    assert.deepEqual(chips.drift, ['warning', 'Drift: 1 user changed outside Access Roles (Users tab)']);
    assert.deepEqual(chips.nolibraries, ['error', 'Users with no libraries: Cleo']);
    assert.deepEqual(chips.administrators, ['info', 'Administrators (locks never apply): Root']);
    assert.deepEqual(chips.loginscreen, ['warning', 'Managed users shown on the login screen: Anna'], 'Cleo is hidden, Ben unmanaged and disabled, Root an administrator');
    assert.deepEqual(chips.policymanagers, ['warning', 'Other policy managers detected: Jellyfin.Plugin.SSO_Auth']);
    assert.deepEqual(chips.je, ['info', 'Jellyfin Enhanced detected']);
    assert.deepEqual(chips.filtererrors, ['error', 'Filter errors: 2 since the start (they fail closed; Diagnostics tab)']);
    assert.deepEqual(chips.degraded, ['ok', 'Enforcement not degraded']);
    assert.equal(env.root.querySelector('[data-chip="nolibraries"] bdi').textContent, 'Cleo', 'names are isolated');

    // The server-wide behaviours: everything that applies to every non-admin, as ServerWide says.
    const wide = env.root.querySelector('#AccessRolesPanelOverview #AccessRolesServerWide');
    assert.equal(hidden(wide), false);
    const items = all(wide, 'li').map((li) => li.textContent);
    assert.equal(items.length, 5);
    assert.ok(items[0].includes('non-admins read only encoding, with its known secrets stripped'), items[0]);
    assert.equal(items[1], 'Restart: administrators only, anonymous callers included. Shutdown: administrators only, as Jellyfin itself allows.');
    assert.ok(items[2].includes('is trimmed'), items[2]);
    assert.ok(items[3].startsWith('Library leak hardening: Standard;'), items[3]);
    assert.ok(items[4].includes('(HideUntilKnown)'), items[4]);
    assert.ok(wide.textContent.includes('Since the server started they hid 7 values.'));
    assert.equal(env.root.querySelector('#AccessRolesPanelOverview #AccessRolesStatus'), null, 'the tables are the Diagnostics tab\'s');

    // What isn't right is amber or red: the API differs, no web client here, a degraded enforcement, every rule off.
    const off = await start({
        diagnostics: diagnosticsSample({
            ActionManifest: { JellyfinVersion: '10.11.5', Source: 'x', Actions: 390, WithRules: 110, Server: { Matches: false, Actions: 391, Unreviewed: ['X.Y'] } },
            Enforcement: enforcementSample({
                Filter: { FilterErrors: 0 },
                Snapshot: { DegradedBecause: 'ReadOnlyDataUnreadable' },
                // Degraded mode since 0.9.13: the users the newest readable copy lists, by name.
                Degraded: { Because: 'ReadOnlyDataUnreadable', EveryNonAdmin: false, Users: [KID], Source: 'accessroles.json', SinceUtc: UPDATED, FromEarlierStart: false, CanResolve: false },
                ServerWide: { ConfigurationReads: 'Redact', ConfigurationReadAllowList: [], BlockNonAdminRestart: false, TrimPublicUsers: false, LibraryHardening: 'Off', FirstLoadMode: 'ShowUntilKnown' }
            })
        }),
        users: [user(ROOT, 'Root', { IsAdministrator: true }), user(KID, 'Kid')]
    });
    const offChips = chipMap(off.root);
    assert.deepEqual(offChips.enforcement, ['error', 'Enforcement not fully active: this server\'s API differs from the reviewed list']);
    assert.deepEqual(offChips.webui, ['warning', 'Web UI not injected in this browser']);
    assert.deepEqual(offChips.nolibraries, ['ok', 'No user is left with no libraries']);
    assert.deepEqual(offChips.loginscreen, ['ok', 'No managed user is shown on the login screen']);
    assert.deepEqual(offChips.filtererrors, ['ok', 'No filter errors']);
    assert.deepEqual(offChips.degraded, ['error', 'Enforcement degraded (ReadOnlyDataUnreadable): settings locked for Kid']);
    const degradedBanner = all(off.root, '[role="alert"], .jfar-page-banner').map((b) => b.textContent).filter((t) => t.includes('can\'t be trusted'));
    assert.equal(degradedBanner.length, 1, 'one banner says how to get out of it');
    assert.ok(degradedBanner[0].includes('the 1 user it listed as managed (read from accessroles.json)'), degradedBanner[0]);
    assert.ok(degradedBanner[0].includes('until a readable copy of the file is put back'), degradedBanner[0]);
    assert.ok(!degradedBanner[0].includes('until the server restarts'), 'no longer only until a restart');
    assert.equal(off.root.querySelector('#AccessRolesDegraded').hasAttribute('hidden'), true, 'a read-only file can\'t be ended from the page');
    const offItems = all(off.root.querySelector('#AccessRolesServerWide'), 'li').map((li) => li.textContent);
    assert.ok(offItems[0].includes('(Redact)'), offItems[0]);
    assert.equal(offItems[1], 'Restart: not blocked; anyone who can reach the server can restart it, as on a stock 10.11.5. Shutdown: administrators only, as Jellyfin itself allows.');
    assert.ok(offItems[2].includes('is not trimmed'), offItems[2]);
    assert.ok(offItems[3].startsWith('Library leak hardening: Off;'), offItems[3]);
    assert.ok(offItems[4].includes('(ShowUntilKnown)'), offItems[4]);

    // Enforcement switched off: the chip says so, and the box that its rules don't apply now; an older web client.
    const disabled = await start({
        diagnostics: diagnosticsSample({ Enforcement: enforcementSample({ SettingsLocks: false, Hardening: false, SettingsLocksOffBecause: 'EnforcementDisabled' }) }),
        beforeMount: webClient('0.0.1.0')
    });
    const disabledChips = chipMap(disabled.root);
    assert.deepEqual(disabledChips.enforcement, ['error', 'Enforcement off (EnforcementDisabled)']);
    assert.equal(disabledChips.webui[0], 'warning');
    assert.ok(/^Web UI injected, version 0\.0\.1\.0 \(this page is \d+\.\d+\.\d+\.\d+: reload the browser\)$/.test(disabledChips.webui[1]), disabledChips.webui[1]);
    assert.ok(disabled.root.querySelector('#AccessRolesServerWide').textContent.includes('Not applied now: EnforcementDisabled.'));
});

test('another policy manager is found by a word of its assembly\'s name, not by letters inside a word', { skip }, async () => {
    const names = [
        'Jellyfin.Plugin.SSO_Auth', 'Jellyfin.Plugin.LDAP-Auth', 'Jellyfin.Plugin.LdapAuth', 'Jellyfin.Plugin.OAuth2Login', 'Keycloak.Jellyfin',
        'Jellyfin.Plugin.Authelia', 'Jellyfin.Plugin.OpenIdConnect', 'Jellyfin.Plugin.SAMLAuth', 'Jellyfin.Plugin.UserManager', 'Jellyfin.Plugin.User_Management',
        'Jellyfin.Plugin.usermanagement',
        // One word, a manager's and a sign-in word written together.
        'Jellyfin.Plugin.ldapauth', 'Jellyfin.Plugin.ssoauth', 'Jellyfin.Plugin.oauthprovider', 'jellyfin.plugin.openidconnect',
        // Not one: the letters of a rule inside another word, or a word that only starts with one.
        'Jellyfin.Plugin.Lessons', 'Jellyfin.Plugin.Avoidcache', 'Jellyfin.Plugin.Classroom', 'Jellyfin.Plugin.Oauthor', 'Jellyfin.Plugin.Superuser', 'Jellyfin.Plugin.Usermanual',
        'Jellyfin.Plugin.Ldapper', 'Jellyfin.Plugin.Ssoapy'
    ];
    const found = [];
    for (const name of names) {
        const env = await start({
            diagnostics: diagnosticsSample({
                RuleTable: { CheckedUtc: UPDATED, FilterRegistered: true, EnforcementActive: true, Bound: 110, WithRules: 110, Missing: [], Changed: [], Unreviewed: [], JellyfinEnhanced: {}, PluginActions: { 'Jellyfin.Plugin.AccessRoles': 30, [name]: 1 }, Flagged: [] }
            })
        });
        const chip = chipMap(env.root).policymanagers;
        if (chip[0] === 'warning') {
            assert.equal(chip[1], 'Other policy managers detected: ' + name);
            found.push(name);
        } else {
            assert.deepEqual(chip, ['ok', 'No other policy manager detected']);
        }
    }

    assert.deepEqual(found, names.slice(0, 15));
});

test('the Diagnostics tab: the five tables, the filter\'s state, a rule name joined with + shown as its rules, and the search finds a rule or the name as it is', { skip }, async () => {
    const env = await start({ diagnostics: diagnosticsSample() });
    const panel = await openTab(env, 'Diagnostics');
    assert.equal(all(panel, '#AccessRolesStatus table').length, 5);
    assert.ok(panel.querySelector('#AccessRolesStatus').textContent.includes('Drift policy'));
    const filter = all(panel, '#AccessRolesDiagnosticsMore tr').map((tr) => tr.querySelector('th').textContent + '=' + tr.querySelector('td').textContent);
    assert.ok(filter.includes('Filter errors since the start=2'), filter.join(' | '));
    assert.ok(filter.includes('Filter registered=Yes'));
    assert.ok(filter.some((row) => row.startsWith('Other plugins\' actions=') && row.includes('Jellyfin.Plugin.SSO_Auth: 4')));

    const joined = panel.querySelector('#AccessRolesOpenWindows tr[data-rule-name="' + JOINED + '"]');
    assert.deepEqual(all(joined, 'code[data-rule]').map((c) => c.textContent), ['LibraryVisibility', 'ItemIds']);
    assert.equal(joined.querySelector('td').textContent, 'LibraryVisibility + ItemIds');
    assert.deepEqual(all(joined, 'td').map((td) => td.textContent).slice(1), ['Hidden', '3']);
    const rows = (id) => all(panel, '#' + id + ' tbody tr').map((tr) => tr.getAttribute('data-rule-name'));
    const search = panel.querySelector('#AccessRolesRuleSearch input');
    async function find(text) {
        search.value = text;
        env.page.dispatch(search, 'input');
        await env.page.flush();
        return [rows('AccessRolesOpenWindows'), rows('AccessRolesRuleCost')];
    }

    assert.deepEqual(await find(''), [[JOINED, 'Settings.Profile'], [JOINED, 'ItemIds']]);
    assert.deepEqual(await find('itemids'), [[JOINED], [JOINED, 'ItemIds']], 'one of its rules finds a joined name');
    assert.deepEqual(await find('LibraryVisibility + ItemIds'), [[JOINED], [JOINED]], 'the name as it is finds it, spaces or not');
    assert.deepEqual(await find('Library'), [[], []], 'a part of a rule\'s name is not a rule');
    assert.ok(panel.querySelector('#AccessRolesRules').textContent.includes('No rule of the open window matches.'));

    const toggle = panel.querySelector('#AccessRolesDiagnosticsRawToggle');
    assert.equal(hidden(panel.querySelector('#AccessRolesDiagnosticsRaw')), true);
    await click(env.page, toggle);
    assert.equal(hidden(panel.querySelector('#AccessRolesDiagnosticsRaw')), false);
    assert.equal(toggle.getAttribute('aria-expanded'), 'true');
    assert.ok(panel.querySelector('#AccessRolesDiagnosticsRaw').textContent.includes('"FilterErrors": 2'));
});

function librariesSample() {
    const list = libraries().map((l) => Object.assign({ Users: [], AddedFor: [], RemovedFor: [], CreatedUtc: '2026-09-01T10:00:00Z' }, l));
    list[0].Users = [
        { UserId: ANNA, Name: 'Anna', IsAdministrator: false, IsDisabled: false, Sources: ['Role:Kid'] },
        { UserId: ROOT, Name: 'Root', IsAdministrator: true, IsDisabled: false, Sources: ['Native'] }
    ];
    list[2].Users = [{ UserId: ROOT, Name: 'Root', IsAdministrator: true, IsDisabled: false, Sources: ['Native'] }];
    list[2].RemovedFor = [{ UserId: ANNA, Name: 'Anna' }];
    list.push({ Id: OLDKIDS, Name: 'Old Kids', Enabled: false, State: 'Orphaned', CreatedUtc: null, CollectionType: null, RoleIds: [FAMILY], InNoRole: false, Locations: [], Recreated: false, Known: true, Users: [], AddedFor: [{ UserId: BEN, Name: 'Ben' }], RemovedFor: [] });
    list.push({ Id: ANIME, Name: 'Anime', Enabled: true, State: 'Active', CreatedUtc: NEWER, CollectionType: 'tvshows', RoleIds: [], InNoRole: true, Locations: [], Recreated: true, Known: true, Users: [], AddedFor: [], RemovedFor: [] });
    return list;
}

function rolesSample() {
    return [
        roles()[0],
        role(FAMILY, 'Family', { Libraries: { Mode: 'Only', Refs: [{ Id: OLDKIDS, Name: 'Old Kids', Orphaned: true }] }, MemberCount: 1 })
    ];
}

function auditLine(seq, action, changes) {
    return Object.assign({ V: 1, Seq: seq, Utc: UPDATED, Actor: { Kind: 'System', UserId: null, Name: null }, Action: action, TargetUserId: null, TargetRoleId: null, TargetName: null, Details: {} }, changes || {});
}

function libraryCard(panel, id) {
    return panel.querySelector('[data-library-id="' + id + '"]');
}

test('the Libraries tab: who opens each library and why, the overrides that refer to it, in no role, renamed, deleted and created again', { skip }, async () => {
    const cleo = user(CLEO, 'Cleo', { RoleIds: [FAMILY], State: 'Managed', LibrariesManaged: true, NoLibraries: true });
    const env = await start({
        libraries: librariesSample(),
        roles: rolesSample(),
        users: users().concat([cleo]),
        responses: [['GET', BASE + 'Audit', { Items: [auditLine(7, 'LibraryRenamed', { Utc: NEWER, TargetName: 'Movies', Details: { LibraryId: MOVIES, Name: 'Movies', OldName: 'Films' } })], TotalRecordCount: 1, StartIndex: 0 }]]
    });
    assert.equal(requests(env.page, 'GET', BASE + 'Audit').length, 0, 'the renames are read when the tab is shown');
    const panel = await openTab(env, 'Libraries');
    assert.deepEqual(requests(env.page, 'GET', BASE + 'Audit')[0].query, { action: 'LibraryRenamed', limit: '50' });
    assert.equal(panel.querySelector('#AccessRolesLibrariesSummary').textContent, '5 libraries; 4 in no role; 1 deleted with references kept; 1 created again.');

    const movies = libraryCard(panel, MOVIES);
    assert.deepEqual(all(movies, '[data-line="users"] li').map((li) => li.textContent), ['Anna: granted by role Kid', 'Root: their own Jellyfin access (not managed)']);
    assert.equal(movies.querySelector('[data-line="roles"]').textContent, 'Granted by the roles: Kid');
    assert.ok(movies.textContent.includes('Library renamed: Films → Movies; roles re-pointed ('), movies.textContent);
    assert.ok(movies.textContent.includes('Id ' + MOVIES + ' · created '));
    assert.equal(libraryCard(panel, HORROR).querySelector('[data-line="removed"]').textContent, 'Removed for: Anna');
    const cartoons = libraryCard(panel, CARTOONS);
    assert.ok(cartoons.textContent.includes('Library Cartoons is in no role: of the managed users, only those with every library or with it added for them can see it.'));
    assert.deepEqual(all(cartoons, '.jfar-page-badge').map((b) => b.textContent), ['Disabled in Jellyfin', 'In no role', 'tvshows']);
    assert.equal(cartoons.textContent.includes('Library renamed'), false, 'a rename is shown on its library only');

    const deleted = libraryCard(panel, OLDKIDS);
    assert.equal(deleted.querySelector('.jfar-page-library-warning').textContent,
        'Library Old Kids was deleted. Role Family and 1 user override referred to it; role Family now grants no library, so 1 user has no libraries. Their references are kept until you remove them.');
    assert.equal(deleted.querySelector('.jfar-page-library-warning-error') !== null, true, 'a role left with no library is red');
    assert.equal(deleted.querySelector('[data-line="added"]').textContent, 'Added for: Ben');
    assert.equal(deleted.querySelector('[data-line="users"]'), null);
    assert.equal(deleted.querySelector('[data-action="purge"]').textContent, 'Remove references…');
    const anime = libraryCard(panel, ANIME);
    assert.ok(anime.textContent.includes('Library Anime was created again. It is a new library: grant it again?'));
    assert.equal(anime.querySelector('[data-action="grant-again"]').textContent, 'Grant again…');
    assert.equal(libraryCard(panel, MOVIES).querySelector('[data-action]'), null);

    // What each role grants and what each user can open, the other way round.
    assert.deepEqual(all(panel, '#AccessRolesLibrariesByRole li').map((li) => li.textContent), ['Kid: Movies', 'Family: no library']);
    assert.deepEqual(all(panel, '#AccessRolesLibrariesByUser li').map((li) => li.textContent), ['Anna: Movies', 'Ben: no library', 'Root (administrator): Movies, Horror', 'Cleo: no library']);
    assert.equal(all(panel, '#AccessRolesLibrariesByUser .jfar-page-library-none').length, 2);
});

test('the Libraries tab lists who opens each library now, marking each user whose access isn\'t what the roles give, and whom the roles give it who can\'t open it yet', { skip }, async () => {
    const list = librariesSample();
    list[0].Users = [
        { UserId: ANNA, Name: 'Anna', IsAdministrator: false, IsDisabled: false, Sources: ['Role:Kid'], Mismatch: null },
        { UserId: BEN, Name: 'Ben', IsAdministrator: false, IsDisabled: false, Sources: [], Mismatch: 'Drift' },
        { UserId: CLEO, Name: 'Cleo <b>', IsAdministrator: false, IsDisabled: true, Sources: [], Mismatch: 'NoLibraries' }
    ];
    list[2].Users = [];
    list[2].NotOpenedBy = [{ UserId: ANNA, Name: 'Anna', IsAdministrator: false, IsDisabled: false, Sources: ['Role:Kid'], Mismatch: 'LibrarySyncDisabled' }];
    const env = await start({ libraries: list, roles: rolesSample(), responses: [['GET', BASE + 'Audit', { Items: [], TotalRecordCount: 0 }]] });
    const panel = await openTab(env, 'Libraries');

    const movies = libraryCard(panel, MOVIES);
    assert.deepEqual(all(movies, '[data-line="users"] li').map((li) => li.textContent), [
        'Anna: granted by role Kid',
        'Ben: not given by the roles (changed outside Access Roles; DriftPolicy ReportOnly keeps it until it is reverted)',
        'Cleo <b>: not given by the roles (the roles would leave this user no library, so the library sync leaves their access as it is) (disabled)'
    ]);
    assert.deepEqual(all(movies, '[data-line="users"] li').map((li) => li.getAttribute('data-mismatch')), [null, 'Drift', 'NoLibraries']);
    assert.equal(all(movies, '.jfar-page-library-mismatch').length, 2);
    assert.equal(movies.querySelector('b'), null, 'a name is text');
    assert.ok(movies.textContent.includes('Who can open it now (3):'));
    assert.ok(all(movies, '.jfar-page-badge').some((b) => b.textContent === 'Not as the roles give'));
    assert.equal(movies.querySelector('[data-line="not-opened"]'), null);

    const horror = libraryCard(panel, HORROR);
    assert.ok(horror.textContent.includes('Nobody can open it now.'));
    assert.ok(horror.textContent.includes('Given by the roles, not open to them yet (1):'));
    assert.deepEqual(all(horror, '[data-line="not-opened"] li').map((li) => li.textContent), ['Anna: granted by role Kid (the library sync is switched off)']);
    assert.ok(all(horror, '.jfar-page-badge').some((b) => b.textContent === 'Not as the roles give'));
    const cartoons = libraryCard(panel, CARTOONS);
    assert.equal(all(cartoons, '.jfar-page-badge').some((b) => b.textContent === 'Not as the roles give'), false, 'a library everyone opens as the roles give has no badge');

    // What each user can open, the other way round, is what they open now, marked as the cards mark it.
    assert.deepEqual(all(panel, '#AccessRolesLibrariesByUser li').map((li) => li.textContent), [
        'Anna: Movies',
        'Ben: Movies (not as the roles give: changed outside Access Roles; DriftPolicy ReportOnly keeps it until it is reverted)',
        'Root (administrator): no library'
    ]);
    assert.deepEqual(all(panel, '#AccessRolesLibrariesByUser li').map((li) => li.getAttribute('data-mismatch')), [null, 'Drift', null]);
    assert.equal(all(panel, '#AccessRolesLibrariesByUser .jfar-page-library-mismatch').length, 1);
});

test('the Libraries tab: Remove references and Grant again are previewed as their own dry run and saved with its PreviewToken', { skip }, async () => {
    const env = await start({ libraries: librariesSample(), roles: rolesSample(), responses: [['GET', BASE + 'Audit', { Items: [], TotalRecordCount: 0 }]] });
    const purgePath = BASE + 'Libraries/' + OLDKIDS + '/Purge';
    const againPath = BASE + 'Libraries/' + ANIME + '/GrantAgain';
    const row = { UserId: ANNA, Name: 'Anna', IsAdministrator: false, StateBefore: 'Managed', StateAfter: 'Managed', LocksAdded: [], LocksRemoved: [], LibrariesAdded: [{ Id: ANIME, Name: 'Anime' }], LibrariesRemoved: [], PolicyWrite: true, NoLibraries: false, NoLibrariesRefused: false, LeavesManagement: null, Warnings: [] };
    serve(env.page, 'POST', purgePath, dryOrSave(tokened('purge'), change()));
    serve(env.page, 'POST', againPath, dryOrSave({ Preview: preview({ Users: [row], PreviewToken: 'again-1' }) }, change()));
    let panel = await openTab(env, 'Libraries');
    await click(env.page, libraryCard(panel, OLDKIDS).querySelector('[data-action="purge"]'));
    let review = topDialog(env.page);
    assert.equal(dialogTitle(review), 'Remove the references to Old Kids');
    assert.deepEqual(requests(env.page, 'POST', purgePath).map((e) => e.query), [{ dryRun: 'true' }]);
    await click(env.page, byText(review, 'button', 'Remove references'));
    assert.deepEqual(saves(env.page, 'POST', purgePath).map((e) => e.query), [{ ifPreview: 'purge-1' }]);
    assert.equal(dialogs(env.page).length, 0);
    assert.equal(requests(env.page, 'GET', BASE + 'Libraries').length, 2, 'the lists load again after the write');
    assert.ok(env.page.document.querySelector('.jfar-toasts').textContent.includes('The references to Old Kids are removed.'));

    panel = env.root.querySelector('#AccessRolesPanelLibraries');
    await click(env.page, libraryCard(panel, ANIME).querySelector('[data-action="grant-again"]'));
    review = topDialog(env.page);
    assert.equal(dialogTitle(review), 'Grant Anime again');
    assert.ok(review.textContent.includes('Libraries added: Anime'));
    await click(env.page, byText(review, 'button', 'Grant again'));
    assert.deepEqual(saves(env.page, 'POST', againPath).map((e) => e.query), [{ ifPreview: 'again-1' }]);

    // Read-only data: neither can be pressed.
    const readOnly = await start({
        libraries: librariesSample(),
        roles: rolesSample(),
        diagnostics: diagnostics({ Store: { File: 'accessroles.json', Version: 1, CurrentVersion: 1, Status: 'ReadOnly', ReadOnly: true } }),
        responses: [['GET', BASE + 'Audit', { Items: [], TotalRecordCount: 0 }]]
    });
    const roPanel = await openTab(readOnly, 'Libraries');
    assert.equal(libraryCard(roPanel, OLDKIDS).querySelector('[data-action="purge"]').disabled, true);
    assert.equal(libraryCard(roPanel, ANIME).querySelector('[data-action="grant-again"]').disabled, true);
});

test('1.0.1, the purge finding: Remove references names the All except… roles and the Remove lists that keep leaving the library out, and with only those left there is nothing to remove', { skip }, async () => {
    const NOKIDS = 'b0000000000000000000000000000003';
    const list = librariesSample();
    const oldKids = list.find((l) => l.Id === OLDKIDS);
    oldKids.RoleIds = [FAMILY, NOKIDS];
    oldKids.RemovedFor = [{ UserId: ANNA, Name: 'Anna <i>' }];
    oldKids.Purgeable = true;
    const roleList = rolesSample().concat([role(NOKIDS, 'No <b>kids</b>', { Libraries: { Mode: 'AllExcept', Refs: [{ Id: OLDKIDS, Name: 'Old Kids', Orphaned: true }] }, MemberCount: 1 })]);
    const env = await start({ libraries: list, roles: roleList, responses: [['GET', BASE + 'Audit', { Items: [], TotalRecordCount: 0 }]] });
    const purgePath = BASE + 'Libraries/' + OLDKIDS + '/Purge';
    serve(env.page, 'POST', purgePath, dryOrSave(tokened('purge'), change()));
    const panel = await openTab(env, 'Libraries');
    await click(env.page, libraryCard(panel, OLDKIDS).querySelector('[data-action="purge"]'));
    const review = topDialog(env.page);
    assert.equal(review.querySelector('p').textContent,
        'Library Old Kids was deleted. Removing its references takes it out of what grants it: the Only… roles\' lists, the users\' own Add lists and the original access recorded for them. A role whose only library it was then grants nothing. What leaves it out stays: the All except… role No <b>kids</b> and the own Remove list of Anna <i> keep leaving it out. If a library is created again under its id, they leave it out too, and Grant again doesn\'t change that: to stop one leaving it out, untick the library in that role or in that user\'s Remove list.');
    // 1.0.2: Grant again leaves the exclusions as they are (LibraryLifecycleTests, SyncApiTests), so no text says "until you grant it again".
    assert.ok(!review.textContent.includes('until you grant it again'), review.textContent);
    assert.equal(review.querySelector('b'), null, 'names are text');
    assert.equal(review.querySelector('i'), null, 'names are text');

    // With only those exclusions left (Purgeable false), the card says they stay, and there is no Remove references.
    const only = librariesSample();
    const kept = only.find((l) => l.Id === OLDKIDS);
    Object.assign(kept, { RoleIds: [NOKIDS], AddedFor: [], RemovedFor: [{ UserId: ANNA, Name: 'Anna' }], Purgeable: false });
    const rolesKept = [roles()[0], role(FAMILY, 'Family', { Libraries: { Mode: 'Only', Refs: [] }, MemberCount: 1 }), roleList[2]];
    const second = await start({ libraries: only, roles: rolesKept, responses: [['GET', BASE + 'Audit', { Items: [], TotalRecordCount: 0 }]] });
    const card = libraryCard(await openTab(second, 'Libraries'), OLDKIDS);
    assert.equal(card.querySelector('.jfar-page-library-warning').textContent,
        'Library Old Kids was deleted. Role No <b>kids</b> and 1 user override referred to it. Only what leaves it out (All except… roles, users\' own Remove lists) refers to it now, and that stays: if a library is created again under its id, they leave it out too, and Grant again doesn\'t change that. To stop one leaving it out, untick the library in that role or in that user\'s Remove list.');
    assert.equal(card.querySelector('[data-action="purge"]'), null);
    const hint = all(second.root, 'p').map((p) => p.textContent).find((t) => t.startsWith('What the server has and what Access Roles knows'));
    assert.ok(hint.endsWith('A deleted library\'s references are kept until you remove them, and what leaves it out (All except… roles, users\' own Remove lists) stays even then; a library created again under the same id is a new library until you grant it again, and what leaves it out still does.'), hint);

    // Nothing leaves it out: the dialog says nothing will refer to it.
    const plain = await start({ libraries: librariesSample(), roles: rolesSample(), responses: [['GET', BASE + 'Audit', { Items: [], TotalRecordCount: 0 }]] });
    serve(plain.page, 'POST', purgePath, dryOrSave(tokened('purge'), change()));
    await click(plain.page, libraryCard(await openTab(plain, 'Libraries'), OLDKIDS).querySelector('[data-action="purge"]'));
    assert.ok(topDialog(plain.page).querySelector('p').textContent.endsWith('A role whose only library it was then grants nothing. Nothing leaves it out, so nothing refers to it any more.'));
});

// The audit's answers, newest first (lines: 60 by default, the first two blocked requests; an array the test may add to),
// with the server's cursor: before= keeps the lines older than that Seq, and the total counts them.
function auditServer(page, lines) {
    const every = lines || auditLines();
    serve(page, 'GET', BASE + 'Audit', (entry) => {
        const before = entry.query.before === undefined ? Infinity : Number(entry.query.before);
        const older = every.filter((line) => line.Seq < before);
        return { body: { Items: older.slice(0, Number(entry.query.limit || 50)), TotalRecordCount: older.length, StartIndex: 0 } };
    });
}

function auditLines() {
    const lines = [
        auditLine(60, 'RequestsBlocked', { TargetName: 'Anna', TargetUserId: ANNA, Details: { Rule: JOINED, Endpoint: 'ItemsController.GetItems', Outcome: 'Hidden', Count: 5, WindowStartUtc: UPDATED, UserId: ANNA } }),
        auditLine(59, 'RequestsBlocked', { TargetName: 'Anna', TargetUserId: ANNA, Details: { Rule: JOINED, Endpoint: 'ItemsController.GetResumeItems', Outcome: 'Hidden', Count: 2, WindowStartUtc: UPDATED, UserId: ANNA } }),
        auditLine(58, 'SettingsChanged', { Actor: { Kind: 'User', UserId: ROOT, Name: 'Root' }, Details: { Changes: [{ Name: 'DriftPolicy', Before: 'Reapply', After: 'ReportOnly' }], SecretKeysAdded: [] } })
    ];
    for (let seq = 57; seq >= 1; seq--) {
        lines.push(auditLine(seq, 'RoleUpdated', { Actor: { Kind: 'User', UserId: ROOT, Name: 'Root' }, TargetRoleId: KID, TargetName: 'Kid', Details: { Name: 'Kid', Locks: ['Profile', 'Home'] } }));
    }

    return lines;
}

function auditFilter(panel, name) {
    return panel.querySelector('[data-audit-filter="' + name + '"]');
}

test('the Audit tab: the first page when shown, Load more, the blocked requests added up by rule, a joined rule shown as its rules', { skip }, async () => {
    const env = await start();
    const lines = auditLines();
    auditServer(env.page, lines);
    const panel = await openTab(env, 'Audit');
    assert.deepEqual(requests(env.page, 'GET', BASE + 'Audit').map((e) => e.query), [{ limit: '50' }]);
    assert.equal(all(panel, '.jfar-page-audit-entry').length, 50);
    assert.equal(panel.querySelector('#AccessRolesAuditSummary').textContent, 'Showing 50 of 60 lines.');
    const first = panel.querySelector('.jfar-page-audit-entry[data-seq="60"]');
    assert.deepEqual(all(first, 'code[data-rule]').map((c) => c.textContent), ['LibraryVisibility', 'ItemIds']);
    assert.ok(first.textContent.includes('Requests blocked · by Access Roles · Anna'), first.textContent);
    assert.ok(first.textContent.includes('5 requests hidden by LibraryVisibility + ItemIds on ItemsController.GetItems (window from '), first.textContent);
    const settingsLine = panel.querySelector('.jfar-page-audit-entry[data-seq="58"]');
    assert.ok(settingsLine.textContent.includes('Settings changed · by Root'));
    assert.ok(settingsLine.textContent.includes('DriftPolicy: Reapply → ReportOnly'));
    assert.equal(panel.querySelector('.jfar-page-audit-entry[data-seq="57"] bdi').textContent, 'Root', 'the actor\'s name is isolated');
    const blocked = all(panel, '#AccessRolesAuditBlocked li');
    assert.deepEqual(blocked.map((li) => li.textContent), ['LibraryVisibility + ItemIds: 7 requests hidden in 2 windows']);
    assert.equal(all(blocked[0], 'code[data-rule]').length, 2);

    // Two lines written before Load more: the next page is the lines older than the last
    // one shown, by its Seq, so no line is shown twice (by startIndex=50 it was 12 and 11 again) and none is missed.
    lines.unshift(auditLine(62, 'RoleCreated'), auditLine(61, 'RoleCreated'));
    await click(env.page, panel.querySelector('#AccessRolesAuditMore button'));
    assert.deepEqual(requests(env.page, 'GET', BASE + 'Audit')[1].query, { limit: '50', before: '11' });
    const seqs = all(panel, '.jfar-page-audit-entry').map((e) => Number(e.getAttribute('data-seq')));
    assert.deepEqual(seqs, Array.from({ length: 60 }, (_, i) => 60 - i), 'each line once, newest first');
    assert.equal(panel.querySelector('#AccessRolesAuditSummary').textContent, 'Showing 60 of 60 lines.');
    assert.equal(panel.querySelector('#AccessRolesAuditMore button').classList.contains('hide'), true, 'nothing more to load');

    // Shown again: no second first page (Show reads it again).
    await openTab(env, 'Overview');
    await openTab(env, 'Audit');
    assert.equal(requests(env.page, 'GET', BASE + 'Audit').length, 2);
});

test('the Audit tab: the filters as the query (a joined rule as its name), a range that ends before it starts, and a late answer dropped', { skip }, async () => {
    const env = await start();
    const gate = later();
    let calls = 0;
    serve(env.page, 'GET', BASE + 'Audit', (entry) => {
        calls++;
        const body = { Items: [auditLine(calls, entry.query.action === 'RoleUpdated' ? 'RoleUpdated' : 'RequestsBlocked', { Details: { Rule: 'ItemIds', Outcome: 'Refused', Count: 1 } })], TotalRecordCount: 1, StartIndex: 0 };
        return calls === 2 ? gate.promise.then(() => ({ body })) : { body };
    });
    const panel = await openTab(env, 'Audit');
    await choose(env.page, auditFilter(panel, 'user'), ANNA);
    await choose(env.page, auditFilter(panel, 'action'), 'RequestsBlocked');
    auditFilter(panel, 'from').value = '2026-10-01';
    auditFilter(panel, 'to').value = '2026-10-30';
    auditFilter(panel, 'rule').value = ' LibraryVisibility + ItemIds ';
    panel.querySelector('#AccessRolesAuditShow').click();
    await env.page.flush();
    assert.deepEqual(requests(env.page, 'GET', BASE + 'Audit')[1].query, {
        limit: '50',
        userId: ANNA,
        action: 'RequestsBlocked',
        fromUtc: new Date('2026-10-01T00:00:00').toISOString(),
        toUtc: new Date('2026-10-31T00:00:00').toISOString(),
        rule: JOINED
    });

    // A newer query while that one waits (Show is off until it answers; the filters' change runs it): its answer shows,
    // the late one is dropped.
    await choose(env.page, auditFilter(panel, 'action'), 'RoleUpdated');
    auditFilter(panel, 'rule').value = '';
    panel.querySelector('#AccessRolesAuditShow').disabled = false;
    await click(env.page, panel.querySelector('#AccessRolesAuditShow'));
    gate.resolve();
    await env.page.flush();
    assert.deepEqual(all(panel, '.jfar-page-audit-entry').map((e) => e.getAttribute('data-action')), ['RoleUpdated']);

    auditFilter(panel, 'from').value = '2026-10-30';
    auditFilter(panel, 'to').value = '2026-10-01';
    panel.querySelector('#AccessRolesAuditShow').disabled = false;
    await click(env.page, panel.querySelector('#AccessRolesAuditShow'));
    assert.equal(panel.querySelector('#AccessRolesAuditError').textContent, 'The range ends before it starts.');
    assert.equal(requests(env.page, 'GET', BASE + 'Audit').length, 3, 'no request for a wrong range');
});

test('the Audit tab: Download saves the export of the date range under the server\'s file name', { skip }, async () => {
    const env = await start();
    auditServer(env.page, []);
    const panel = await openTab(env, 'Audit');
    const before = globalThis.fetch;
    globalThis.fetch = (url, init) => {
        const parsed = new URL(String(url), 'http://127.0.0.1:8097');
        if (parsed.pathname !== BASE + 'Audit/Export') {
            return before(url, init);
        }

        env.page.fetchLog.push({ method: 'GET', path: parsed.pathname, query: Object.fromEntries(parsed.searchParams.entries()) });
        return Promise.resolve({
            ok: true,
            status: 200,
            headers: { get: (name) => (name.toLowerCase() === 'content-disposition' ? 'attachment; filename=accessroles-audit-20261030-100000.jsonl; filename*=UTF-8\'\'accessroles-audit-20261030-100000.jsonl' : 'application/x-ndjson') },
            text: () => Promise.resolve('{"Seq":1}\n')
        });
    };
    const made = [];
    const saved = [];
    const original = { create: URL.createObjectURL, revoke: URL.revokeObjectURL };
    URL.createObjectURL = (blob) => {
        made.push(blob);
        return 'blob:test-1';
    };
    URL.revokeObjectURL = () => {};
    env.page.document.body.addEventListener('click', (e) => {
        if (e.target.getAttribute && e.target.getAttribute('download')) {
            saved.push([e.target.getAttribute('download'), e.target.getAttribute('href')]);
        }
    });
    try {
        auditFilter(panel, 'from').value = '2026-10-01';
        await click(env.page, panel.querySelector('#AccessRolesAuditExport'));
        await env.page.flush();
        assert.deepEqual(requests(env.page, 'GET', BASE + 'Audit/Export').map((e) => e.query), [{ fromUtc: new Date('2026-10-01T00:00:00').toISOString() }]);
        assert.deepEqual(saved, [['accessroles-audit-20261030-100000.jsonl', 'blob:test-1']]);
        assert.equal(await made[0].text(), '{"Seq":1}\n');
        assert.equal(env.page.document.querySelector('a[download]'), null, 'the link goes again');
    } finally {
        URL.createObjectURL = original.create;
        URL.revokeObjectURL = original.revoke;
    }
});

function settingsSample(changes) {
    return Object.assign({
        EnableWebUi: true, ReportWebAnchorProblems: true, SnapshotsToKeep: 7, EnforcementEnabled: true, LibrarySyncEnabled: true, ReconcileIntervalMinutes: 5,
        DriftPolicy: 'Reapply', LibraryHardening: 'Standard', NonAdminConfigurationReads: 'AllowList', ConfigurationReadAllowList: ['encoding'], BlockNonAdminRestart: true,
        TrimPublicUsers: true, MirrorPreferenceLockToPolicy: false, FirstLoadMode: 'HideUntilKnown', BlockedNavigationNotice: 'Toast', ShowManagedNoticeOnUserPages: true, AuditRetentionMonths: 12
    }, changes || {});
}

function setting(root, name) {
    return root.querySelector('[data-setting="' + name + '"]');
}

async function typeInto(page, input, text) {
    input.value = text;
    page.dispatch(input, 'input');
    await page.flush();
}

test('the Settings tab: only what changed goes to the dry run, the changes and warnings are listed, a key that carries secrets needs the box, and Save sends the PreviewToken', { skip }, async () => {
    const env = await start({ responses: [['GET', BASE + 'Settings', settingsSample({ LibraryHardening: 'Strict' })]] });
    let token = 0;
    let refuse = true;
    serve(env.page, 'PUT', BASE + 'Settings', (entry) => {
        if (entry.query.dryRun === 'true') {
            const secrets = (entry.body.ConfigurationReadAllowList || []).filter((k) => k === 'network');
            return { body: {
                DryRun: true,
                Settings: settingsSample(entry.body),
                Changes: Object.keys(entry.body).map((name) => ({ Name: name, Before: name === 'EnforcementEnabled' ? 'true' : (name === 'DriftPolicy' ? 'Reapply' : 'encoding'), After: entry.body[name] === false ? 'false' : [].concat(entry.body[name]).join(', ') })),
                SecretKeysAdded: secrets,
                Warnings: entry.body.EnforcementEnabled === false ? ['Nothing is enforced: restrictions are only stored.'] : [],
                PreviewToken: 'settings-' + (++token)
            } };
        }

        if (refuse) {
            refuse = false;
            return { status: 409, body: problem(409, 'Changed', 'The settings changed since the preview you saw (EnforcementEnabled); nothing was saved. Look at the preview again.') };
        }

        return { body: { DryRun: false, Settings: settingsSample(Object.assign({ LibraryHardening: 'Strict' }, entry.body)), Changes: [], SecretKeysAdded: [], Warnings: [], PreviewToken: 'x' } };
    });
    const panel = await openTab(env, 'Settings');
    assert.equal(requests(env.page, 'GET', BASE + 'Settings').length, 1);
    const hardening = setting(panel, 'LibraryHardening');
    assert.equal(hardening.value, 'Strict', 'a stored Strict is shown as it is');
    assert.ok(all(hardening, 'option').some((o) => o.textContent.startsWith('Strict (stored; arrives in 1.1')));
    // 1.0.2: the restart box claims only the restart; Jellyfin itself keeps the shutdown for administrators.
    const restart = setting(panel, 'BlockNonAdminRestart').parentNode.textContent;
    assert.ok(restart.startsWith('Only administrators can restart the server'), restart);
    assert.ok(!restart.includes('shut down the server'), restart);
    assert.ok(restart.includes('Shutting down is for administrators either way: Jellyfin itself allows only them.'), restart);
    const review = panel.querySelector('#AccessRolesSettingsReview');
    assert.equal(review.disabled, true);
    assert.equal(panel.querySelector('#AccessRolesSettingsStatus').textContent, 'No changes.');

    await tick(env.page, setting(panel, 'EnforcementEnabled'), false);
    await typeInto(env.page, setting(panel, 'ConfigurationReadAllowList'), 'encoding, Network');
    assert.equal(panel.querySelector('#AccessRolesSettingsStatus').textContent, '2 settings changed, not saved yet.');
    await typeInto(env.page, setting(panel, 'ReconcileIntervalMinutes'), '0');
    assert.ok(panel.querySelector('#AccessRolesSettingsError').textContent.includes('a whole number from 1 to 60.'));
    assert.equal(review.disabled, true, 'a value out of range isn\'t sent');
    await typeInto(env.page, setting(panel, 'ReconcileIntervalMinutes'), '5');
    assert.equal(review.disabled, false);

    await click(env.page, review);
    const dry = requests(env.page, 'PUT', BASE + 'Settings')[0];
    assert.deepEqual([dry.query, dry.body], [{ dryRun: 'true' }, { EnforcementEnabled: false, ConfigurationReadAllowList: ['encoding', 'network'] }]);
    let dialog = topDialog(env.page);
    assert.equal(dialogTitle(dialog), 'Review the settings');
    assert.deepEqual(all(dialog, '#AccessRolesSettingsChanges li').map((li) => li.textContent), [
        'Enforce the roles (EnforcementEnabled): true → false',
        'Configuration keys non-admins may read (comma-separated) (ConfigurationReadAllowList): encoding → encoding, network'
    ]);
    assert.deepEqual(all(dialog, '#AccessRolesSettingsWarnings li').map((li) => li.textContent), ['Nothing is enforced: restrictions are only stored.']);
    const save = dialog.querySelector('#AccessRolesSettingsSave');
    const secrets = dialog.querySelector('#AccessRolesSettingsConfirmSecrets');
    assert.equal(hidden(secrets), false);
    assert.ok(secrets.parentNode.textContent.includes('Add anyway: network carries secrets.'));
    assert.equal(save.disabled, true, 'a key that carries secrets needs the box');
    await tick(env.page, secrets, true);
    assert.equal(save.disabled, false);

    // A 409 Changed: nothing saved, Save off, Look again previews the same body again.
    await click(env.page, save);
    let sent = saves(env.page, 'PUT', BASE + 'Settings');
    assert.deepEqual([sent[0].query, sent[0].body], [{ ifPreview: 'settings-1', confirmSecretKeys: 'true' }, { EnforcementEnabled: false, ConfigurationReadAllowList: ['encoding', 'network'] }]);
    assert.ok(dialog.textContent.includes('The settings changed since the preview you saw (EnforcementEnabled); nothing was saved.'));
    assert.equal(save.disabled, true);
    assert.equal(focusName(env.page), 'AccessRolesSettingsLookAgain');
    await click(env.page, dialog.querySelector('#AccessRolesSettingsLookAgain'));
    assert.equal(requests(env.page, 'PUT', BASE + 'Settings').filter((e) => e.query.dryRun === 'true').length, 2);
    assert.equal(secrets.checked, true, 'the same keys: the box stays ticked');
    await click(env.page, save);
    sent = saves(env.page, 'PUT', BASE + 'Settings');
    assert.deepEqual(sent[1].query, { ifPreview: 'settings-2', confirmSecretKeys: 'true' });
    assert.equal(dialogs(env.page).length, 0);
    assert.ok(env.page.document.querySelector('.jfar-toasts').textContent.includes('Settings saved.'));
    assert.equal(setting(panel, 'EnforcementEnabled').checked, false, 'the form shows what was saved');
    assert.equal(panel.querySelector('#AccessRolesSettingsStatus').textContent, 'No changes.');
    assert.equal(requests(env.page, 'GET', BASE + 'Diagnostics').length, 2, 'the status loads again');

    // No key that carries secrets: no confirmation, and Save sends only the token.
    await choose(env.page, setting(panel, 'DriftPolicy'), 'ReportOnly');
    await click(env.page, panel.querySelector('#AccessRolesSettingsReview'));
    dialog = topDialog(env.page);
    assert.deepEqual(requests(env.page, 'PUT', BASE + 'Settings').filter((e) => e.query.dryRun === 'true').pop().body, { DriftPolicy: 'ReportOnly' });
    assert.equal(hidden(dialog.querySelector('#AccessRolesSettingsConfirmSecrets')), true);
    await click(env.page, dialog.querySelector('#AccessRolesSettingsSave'));
    assert.deepEqual(saves(env.page, 'PUT', BASE + 'Settings')[2].query, { ifPreview: 'settings-3' });
});

test('the Settings review: a new preview that names other keys carrying secrets needs the box ticked again', { skip }, async () => {
    const env = await start({ responses: [['GET', BASE + 'Settings', settingsSample()]] });
    let dryRuns = 0;
    serve(env.page, 'PUT', BASE + 'Settings', (entry) => {
        if (entry.query.dryRun === 'true') {
            dryRuns++;
            return { body: {
                DryRun: true,
                Settings: settingsSample(entry.body),
                Changes: [{ Name: 'ConfigurationReadAllowList', Before: 'encoding', After: 'encoding, network' }],
                SecretKeysAdded: dryRuns === 1 ? ['network'] : ['network', 'livetv'],
                Warnings: [],
                PreviewToken: 'settings-' + dryRuns
            } };
        }

        return { status: 409, body: problem(409, 'Changed', 'The settings changed since the preview you saw (ConfigurationReadAllowList); nothing was saved. Look at the preview again.') };
    });
    const panel = await openTab(env, 'Settings');
    await typeInto(env.page, setting(panel, 'ConfigurationReadAllowList'), 'encoding, network');
    await click(env.page, panel.querySelector('#AccessRolesSettingsReview'));
    const dialog = topDialog(env.page);
    const secrets = dialog.querySelector('#AccessRolesSettingsConfirmSecrets');
    const save = dialog.querySelector('#AccessRolesSettingsSave');
    await tick(env.page, secrets, true);
    await click(env.page, save);
    assert.deepEqual(saves(env.page, 'PUT', BASE + 'Settings')[0].query, { ifPreview: 'settings-1', confirmSecretKeys: 'true' });
    await click(env.page, dialog.querySelector('#AccessRolesSettingsLookAgain'));
    assert.equal(dryRuns, 2);
    assert.ok(secrets.parentNode.textContent.includes('Add anyway: network, livetv carry secrets.'));
    assert.equal(secrets.checked, false, 'other keys: the box is unticked');
    assert.equal(save.disabled, true);
    assert.equal(saves(env.page, 'PUT', BASE + 'Settings').length, 1, 'nothing more is sent');
});

test('the Data tab: the data file, the copies kept next to it, the records and what the settings keep', { skip }, async () => {
    const env = await start({
        diagnostics: diagnostics({
            Store: { Name: 'accessroles', File: 'C:/data/accessroles/accessroles.json', Version: 1, CurrentVersion: 1, Status: 'Ok', ReadOnly: false, LastSnapshotUtc: UPDATED, KeptFiles: [{ Name: 'accessroles.v1-20261001.json', Kind: 'Upgrade', SchemaVersion: 1, Utc: UPDATED, Size: 2048 }], KeptFileCount: 1 },
            Records: { Roles: 2, Assignments: 1, DefaultRoles: 1, KnownLibraries: 4, PendingRenames: 0, LedgerEntries: 1, PendingReleases: 0, UnknownValues: 0 }
        }),
        responses: [['GET', BASE + 'Settings', settingsSample({ SnapshotsToKeep: 7, AuditRetentionMonths: 12 })]]
    });
    const panel = await openTab(env, 'Data');
    const rows = {};
    all(panel, 'tr').forEach((tr) => {
        rows[tr.querySelector('th').textContent] = tr.querySelector('td').textContent;
    });
    assert.equal(rows['Snapshots kept'], 'The last 7');
    assert.equal(rows['Audit log kept'], '12 months, 50 MB at most');
    assert.equal(rows['Default roles'], '1');
    assert.equal(rows.Status, 'Ok');
    assert.deepEqual(all(panel, '#AccessRolesKeptFiles li').map((li) => li.textContent), ['accessroles.v1-20261001.json: Upgrade, schema 1, ' + new Date(UPDATED).toLocaleString() + ', 2.0 KB']);
    assert.equal(requests(env.page, 'GET', BASE + 'Settings').length, 1);
});

test('drift: Revert this user previews POST Sync?userId= and writes with its PlanToken and the userId; a 409 names who changed', { skip }, async () => {
    const env = await start();
    let plan = 0;
    let refuse = true;
    serve(env.page, 'POST', BASE + 'Sync', (entry) => {
        if (entry.query.dryRun === 'true') {
            return { body: { DryRun: true, Written: 0, UserId: ANNA, PlanToken: 'plan-anna-' + (++plan), Users: [{ UserId: ANNA, Name: 'Anna', Action: 'Write', Reason: 'Drift', Added: [], Removed: [{ Id: HORROR, Name: 'Horror' }] }] } };
        }

        if (refuse) {
            refuse = false;
            return { status: 409, body: problem(409, 'Changed', 'The sync would write other than its preview.', { Users: [{ UserId: ANNA, Name: 'Anna' }] }) };
        }

        return { body: { DryRun: false, Written: 1, UserId: ANNA, Users: [] } };
    });
    const panel = await openTab(env, 'Users');
    await click(env.page, byText(panel, 'button', 'Anna'));
    const drift = topDialog(env.page).querySelector('#AccessRolesDrift');
    assert.deepEqual(all(drift, 'button').map((b) => b.textContent), ['Keep as override', 'Revert this user…', 'Revert all drifted users…']);
    await click(env.page, drift.querySelector('#AccessRolesDriftRevertUser'));
    const confirm = topDialog(env.page);
    assert.equal(dialogTitle(confirm), 'Revert the library access of Anna');
    assert.ok(confirm.textContent.includes('The library sync writes the library access the roles give Anna, and nobody else\'s.'));
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Sync')[0].query, { dryRun: 'true', reapplyDrift: 'true', userId: ANNA });
    assert.ok(confirm.textContent.includes('Anna: added nothing; removed Horror'));
    await click(env.page, byText(confirm, 'button', 'Revert'));
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Sync')[1].query, { reapplyDrift: 'true', ifPlan: 'plan-anna-1', userId: ANNA });
    assert.ok(confirm.textContent.includes('What the library sync would write changed after this preview (Anna), so nothing was written.'));
    await click(env.page, byText(confirm, 'button', 'Look again'));
    await click(env.page, byText(confirm, 'button', 'Revert'));
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Sync')[3].query, { reapplyDrift: 'true', ifPlan: 'plan-anna-2', userId: ANNA });
    assert.equal(dialogTitle(topDialog(env.page)), 'Anna', 'the panel is open again, Revert\'s dialog closed');
    assert.ok(env.page.document.querySelector('.jfar-toasts').textContent.includes('Library access written for 1 user.'));
});

test('every server value on the Libraries, Audit, Settings, Data and Diagnostics tabs is written as text', { skip }, async () => {
    const markup = '<img src=x onerror=alert(1)>';
    const env = await start({
        diagnostics: diagnosticsSample({
            Store: { Name: markup, File: markup, Version: 1, CurrentVersion: 1, Status: 'Ok', ReadOnly: false, KeptFiles: [{ Name: markup, Kind: '<b>k</b>', Utc: UPDATED, Size: 1 }], KeptFileCount: 1 },
            Enforcement: enforcementSample({ Filter: { FilterErrors: 1, LastError: markup }, Blocked: { Totals: {}, OpenWindows: { [markup + '+<b>x</b> Refused']: 1 } }, Cost: { [markup]: { Count: 1 } } }),
            RuleTable: { CheckedUtc: UPDATED, FilterRegistered: true, EnforcementActive: true, Bound: 1, WithRules: 1, Missing: [markup], Changed: [], Unreviewed: [], JellyfinEnhanced: {}, PluginActions: { [markup + 'sso']: 1 }, Flagged: [] }
        }),
        users: [user(ANNA, '<script>x</script>', { State: 'Managed', NoLibraries: true, IsHidden: false }), user(ROOT, '<i>root</i>', { IsAdministrator: true })],
        libraries: [
            { Id: MOVIES, Name: markup, Enabled: true, State: 'Active', CollectionType: '<b>c</b>', RoleIds: [KID], InNoRole: true, Locations: ['<b>path</b>'], Recreated: true, Known: true, Users: [{ UserId: ANNA, Name: '<b>b</b>', Sources: ['Role:<i>i</i>'] }], AddedFor: [{ UserId: ANNA, Name: markup }], RemovedFor: [] },
            { Id: OLDKIDS, Name: markup, Enabled: false, State: 'Orphaned', RoleIds: [KID], InNoRole: false, Locations: [], Recreated: false, Known: true, Users: [], AddedFor: [], RemovedFor: [{ UserId: ANNA, Name: markup }] }
        ],
        roles: [role(KID, markup, { Libraries: { Mode: 'Only', Refs: [{ Id: OLDKIDS, Name: markup, Orphaned: true }] } })],
        responses: [['GET', BASE + 'Settings', settingsSample()]]
    });
    serve(env.page, 'GET', BASE + 'Audit', () => ({ body: { Items: [
        auditLine(2, 'RequestsBlocked', { Actor: { Kind: 'User', Name: '<i>a</i>' }, TargetName: markup, Details: { Rule: markup + '+<b>r</b>', Endpoint: '<b>e</b>', Outcome: '<i>o</i>', Count: 1 } }),
        auditLine(1, 'LibraryRenamed', { TargetName: markup, Details: { LibraryId: MOVIES, Name: markup, OldName: '<b>old</b>', Nested: { X: markup } } })
    ], TotalRecordCount: 2 } }));
    serve(env.page, 'PUT', BASE + 'Settings', () => ({ body: { DryRun: true, Changes: [{ Name: markup, Before: '<b>b</b>', After: '<i>a</i>' }], SecretKeysAdded: [markup], Warnings: [markup], PreviewToken: 't' } }));
    for (const name of ['Overview', 'Libraries', 'Audit', 'Data', 'Diagnostics', 'Settings']) {
        await openTab(env, name);
    }

    await choose(env.page, setting(env.root, 'DriftPolicy'), 'ReportOnly');
    await click(env.page, env.root.querySelector('#AccessRolesSettingsReview'));
    await click(env.page, env.root.querySelector('#AccessRolesDiagnosticsRawToggle'));
    for (const tag of ['img', 'script', 'b', 'i']) {
        assert.equal(all(env.page.document.body, tag).length, 0, tag);
    }

    const text = env.page.document.body.textContent;
    for (const shown of ['Users with no libraries: <script>x</script>', 'Library renamed: <b>old</b> → ' + markup, '<b>b</b>: granted by role <i>i</i>', markup + ': <b>b</b> → <i>a</i>', 'Add anyway: ' + markup, '<b>e</b>']) {
        assert.ok(text.includes(shown), shown);
    }
});

test('on the TV layout: a library card and an audit line take the focus, Down and Up on a card go through the tab, and Backspace closes the settings review back to Review', { skip }, async () => {
    const env = await start({ tv: true, libraries: librariesSample(), roles: rolesSample(), responses: [['GET', BASE + 'Settings', settingsSample()]] });
    auditServer(env.page);
    const libs = await openTab(env, 'Libraries');
    const cards = all(libs, '.jfar-page-library');
    assert.equal(cards.length, 6);
    assert.equal(cards.every((c) => c.getAttribute('tabindex') === '0'), true);
    // Down and Up on a card go to the next or previous control of the tab in the page's order, never the dashboard's menu.
    const order = all(libs, 'button, input, select, textarea, a[href], [tabindex="0"]').filter((el) => !el.disabled && el.getClientRects().length > 0);
    const second = order[order.indexOf(cards[1]) + 1];
    cards[1].focus();
    env.page.key(cards[1], 'ArrowDown');
    await env.page.flush();
    assert.equal(env.page.document.activeElement, second, 'Down: the next control of the tab');
    cards[1].focus();
    env.page.key(cards[1], 'ArrowUp');
    await env.page.flush();
    assert.equal(env.page.document.activeElement, order[order.indexOf(cards[1]) - 1], 'Up: the one before');
    const audit = await openTab(env, 'Audit');
    assert.equal(all(audit, '.jfar-page-audit-entry').every((e) => e.getAttribute('tabindex') === '0'), true);

    const panel = await openTab(env, 'Settings');
    await choose(env.page, setting(panel, 'DriftPolicy'), 'ReportOnly');
    serve(env.page, 'PUT', BASE + 'Settings', () => ({ body: { DryRun: true, Changes: [{ Name: 'DriftPolicy', Before: 'Reapply', After: 'ReportOnly' }], SecretKeysAdded: [], Warnings: [], PreviewToken: 't' } }));
    const review = panel.querySelector('#AccessRolesSettingsReview');
    review.focus();
    await click(env.page, review);
    const dialog = topDialog(env.page);
    const save = dialog.querySelector('#AccessRolesSettingsSave');
    save.focus();
    env.page.key(save, 'Backspace');
    await env.page.flush();
    assert.equal(dialogs(env.page).length, 0, 'Backspace is the Hisense remote\'s Back, and Back on the TV layout');
    assert.equal(focusName(env.page), 'AccessRolesSettingsReview');
    assert.equal(saves(env.page, 'PUT', BASE + 'Settings').length, 0);
});

test('a remembered Audit or Settings tab reads its data when the page opens on it', { skip }, async () => {
    const audit = await start({ beforeMount: (page) => page.context.localStorage.setItem('jfar:adminTab', '"Audit"'), responses: [['GET', BASE + 'Audit', { Items: [], TotalRecordCount: 0 }]] });
    assert.equal(tab(audit, 'Audit').getAttribute('aria-selected'), 'true');
    assert.equal(requests(audit.page, 'GET', BASE + 'Audit').length, 1);

    const settings = await start({ responses: [['GET', BASE + 'Settings', settingsSample()]], beforeMount: (page) => page.context.localStorage.setItem('jfar:adminTab', '"Settings"') });
    assert.equal(tab(settings, 'Settings').getAttribute('aria-selected'), 'true');
    assert.equal(requests(settings.page, 'GET', BASE + 'Settings').length, 1);
    assert.equal(hidden(settings.root.querySelector('#AccessRolesSettingsForm')), false);
});

test('Degraded mode after an earlier start: the Overview lists whom it locks, and Stop locking them sends its preview\'s PreviewToken', { skip }, async () => {
    const degraded = { Because: 'ReadOnlyDataUnreadable', EveryNonAdmin: false, Users: [ANNA], Source: 'accessroles.json', SinceUtc: UPDATED, FromEarlierStart: true, CanResolve: true };
    const env = await start({ diagnostics: diagnostics({ Enforcement: { Active: true, SettingsLocks: true, Hardening: true, ServerWide: {}, Blocked: { Totals: {} }, NotEnforcedLocks: [], Snapshot: { DegradedBecause: 'ReadOnlyDataUnreadable' }, Degraded: degraded } }) });
    let previews = 0;
    serve(env.page, 'POST', BASE + 'Degraded/Resolve', (entry) => {
        if (entry.query.dryRun === 'true') {
            return { body: { Because: degraded.Because, EveryNonAdmin: false, Users: [{ UserId: ANNA, Name: 'Anna' }], DryRun: true, PreviewToken: 'degraded-' + (++previews), Resolved: false } };
        }

        return entry.query.ifPreview === 'degraded-2'
            ? { body: { Because: degraded.Because, Users: [{ UserId: ANNA, Name: 'Anna' }], DryRun: false, Resolved: true } }
            : { status: 409, body: problem(409, 'Changed', 'Degraded mode doesn\'t lock the users its preview showed any more (for Ben). Nothing was changed. Preview it again.', { Users: [{ UserId: BEN, Name: 'Ben' }] }) };
    });
    const box = env.root.querySelector('#AccessRolesDegraded');
    const preview = env.root.querySelector('#AccessRolesDegradedPreview');
    const confirm = env.root.querySelector('#AccessRolesDegradedConfirm');
    const result = env.root.querySelector('#AccessRolesDegradedResult');
    assert.equal(hidden(box), false, 'the usable file after an earlier degraded start can be ended here');
    assert.deepEqual(chipMap(env.root).degraded, ['error', 'Enforcement degraded (ReadOnlyDataUnreadable): settings locked for Anna']);
    const banner = all(env.root, '[role="alert"]').map((b) => b.textContent).filter((t) => t.includes('earlier start'))[0];
    assert.ok(banner && banner.includes('until you confirm below that their roles alone decide their settings locks'), banner);
    // What ending it does, as it is (the mirror's flags back on, library access as last written).
    assert.ok(box.textContent.includes('their settings locks then come from their roles alone'), box.textContent);
    assert.ok(box.textContent.includes('Library access stays as it was last written'), box.textContent);
    assert.ok(!box.textContent.includes('only what their roles give them'));
    assert.equal(confirm.disabled, true);

    await click(env.page, preview);
    assert.ok(result.textContent.includes('It locks the settings of 1 user:'), result.textContent);
    assert.ok(result.textContent.includes('Anna'));
    await click(env.page, confirm);
    assert.ok(topDialog(env.page).textContent.includes('library access stays as it was last written'), topDialog(env.page).textContent);
    await click(env.page, byText(topDialog(env.page), 'button', 'Stop locking them'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Degraded/Resolve').map((e) => e.query), [{ ifPreview: 'degraded-1' }]);
    assert.ok(result.textContent.includes('for Ben'), result.textContent);
    assert.equal(confirm.disabled, true, 'after a 409 it needs a new preview');
    await click(env.page, preview);
    await click(env.page, confirm);
    // The Overview refreshes after the confirmation: Degraded mode is over.
    env.page.respond('GET', BASE + 'Diagnostics', diagnostics());
    await click(env.page, byText(topDialog(env.page), 'button', 'Stop locking them'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Degraded/Resolve').at(-1).query, { ifPreview: 'degraded-2' });

    // The result stays in view after the refresh, the actions go, and the focus is on Refresh.
    assert.equal(hidden(box), false, 'the box stays with its result');
    assert.ok(result.textContent.includes('Degraded mode ended'), result.textContent);
    assert.equal(hidden(preview), true);
    assert.ok(env.page.document.activeElement === env.root.querySelector('#AccessRolesRefresh'), 'the focus is on ' + focusName(env.page));
});

test('Release all locks previews whom it releases and how, the box adds the original library access, and the confirmation sends the preview\'s PreviewToken', { skip }, async () => {
    const env = await start();
    const row = (extra) => Object.assign({ UserId: ANNA, Name: 'Anna', PreferenceAccess: true, Libraries: null, RecreatedLeftOut: [], LibrariesSkipped: null, Excluded: true }, extra);
    let previews = 0;
    serve(env.page, 'POST', BASE + 'ReleaseLocks', (entry) => {
        const restore = entry.query.restoreLibraries === 'true';
        const rows = restore
            ? [row({ Libraries: { EnableAllFolders: false, EnabledFolders: [MOVIES] } }), row({ UserId: ROOT, Name: 'Root', PreferenceAccess: false, LibrariesSkipped: 'NoFullAdministrator', Excluded: false })]
            : [row()];
        if (entry.query.dryRun === 'true') {
            return { body: { DryRun: true, PreviewToken: 'release-' + (++previews), RestoreLibraries: restore, Released: rows } };
        }

        return entry.query.ifPreview !== 'release-1'
            ? { body: { DryRun: false, RestoreLibraries: restore, Released: rows.map((r) => Object.assign({}, r, { PreferenceOutcome: r.PreferenceAccess ? 'TurnedOn' : null, LibrariesOutcome: r.Libraries ? 'Written' : null })) } }
            : { status: 409, body: problem(409, 'Changed', 'The locks to release aren\'t the ones the preview showed any more (for Ben). Nothing was changed. Preview it again.', { Users: [{ UserId: BEN, Name: 'Ben' }] }) };
    });
    const box = env.root.querySelector('#AccessRolesRelease');
    const preview = env.root.querySelector('#AccessRolesReleasePreview');
    const confirm = env.root.querySelector('#AccessRolesReleaseConfirm');
    const result = env.root.querySelector('#AccessRolesReleaseResult');
    const restore = env.root.querySelector('#AccessRolesReleaseLibraries');
    assert.equal(hidden(box), false);
    assert.ok(box.textContent.includes('Before disabling or deleting Access Roles'));
    // On 10.11.5 Disable waits for a restart, and a disabled plugin's uninstall isn't called.
    assert.ok(box.textContent.includes('or uninstall, while Access Roles is still enabled'), box.textContent);
    assert.ok(box.textContent.includes('takes effect only at the next server restart'), box.textContent);
    assert.ok(box.textContent.includes('Jellyfin 10.11 deletes a disabled plugin\'s folder at that restart'), box.textContent);
    assert.ok(box.textContent.includes('uninstalling a disabled plugin can\'t put anything back'), box.textContent);
    assert.ok(!box.textContent.includes('stops its settings locks at once'));
    assert.equal(confirm.disabled, true, 'nothing to confirm before a preview');

    await click(env.page, preview);
    assert.deepEqual(requests(env.page, 'POST', BASE + 'ReleaseLocks').map((e) => e.query), [{ dryRun: 'true' }]);
    assert.ok(result.textContent.includes('Releasing writes 1 user:'), result.textContent);
    assert.ok(result.textContent.includes('Anna: preference access back on; becomes Excluded'), result.textContent);
    assert.equal(confirm.disabled, false);
    await click(env.page, confirm);
    await click(env.page, byText(topDialog(env.page), 'button', 'Release them'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'ReleaseLocks').map((e) => e.query), [{ ifPreview: 'release-1' }]);
    assert.ok(result.textContent.includes('for Ben'), result.textContent);
    assert.equal(confirm.disabled, true, 'after a 409 it needs a new preview');

    // The box: a new preview with restoreLibraries, the original access by name, and a write the guard keeps back.
    await click(env.page, preview);
    assert.equal(confirm.disabled, false);
    await tick(env.page, restore, true);
    assert.equal(confirm.disabled, true, 'ticking the box drops the preview');
    assert.equal(result.textContent, '');
    await click(env.page, preview);
    assert.deepEqual(requests(env.page, 'POST', BASE + 'ReleaseLocks').at(-1).query, { dryRun: 'true', restoreLibraries: 'true' });
    assert.ok(result.textContent.includes('Releasing writes 2 users:'), result.textContent);
    assert.ok(result.textContent.includes('Anna: preference access back on; original library access: Movies; becomes Excluded'), result.textContent);
    assert.ok(result.textContent.includes('Root: preference access as it is; library access kept: it would leave no administrator with every library'), result.textContent);
    await click(env.page, confirm);
    await click(env.page, byText(topDialog(env.page), 'button', 'Release them'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'ReleaseLocks').at(-1).query, { ifPreview: 'release-3', restoreLibraries: 'true' });
    assert.ok(result.textContent.includes('Released 2 users; the audit log has the details.'), result.textContent);
    assert.equal(confirm.disabled, true, 'done: a new preview first');
});

test('a restore that lost managed users says an older copy took the file\'s place, and can be ended here', { skip }, async () => {
    const degraded = { Because: 'RestoredFromSnapshot', EveryNonAdmin: false, Users: [ANNA], Source: 'accessroles.json.corrupt-20261101190908', SinceUtc: UPDATED, FromEarlierStart: true, CanResolve: true };
    const env = await start({ diagnostics: diagnostics({ Enforcement: { Active: true, SettingsLocks: true, Hardening: true, ServerWide: {}, Blocked: { Totals: {} }, NotEnforcedLocks: [], Snapshot: { DegradedBecause: 'RestoredFromSnapshot' }, Degraded: degraded } }) });
    const banner = all(env.root, '[role="alert"]').map((b) => b.textContent).filter((t) => t.includes('older copy'))[0];
    assert.ok(banner && banner.includes('read from accessroles.json.corrupt-20261101190908'), banner);
    assert.ok(banner.includes('until you confirm below'), banner);
    assert.equal(hidden(env.root.querySelector('#AccessRolesDegraded')), false);
});

test('Release all locks on a read-only data file says nobody becomes Excluded, in the preview and the dialog', { skip }, async () => {
    const env = await start();
    const rows = [{ UserId: ANNA, Name: 'Anna', PreferenceAccess: true, Libraries: null, RecreatedLeftOut: [], LibrariesSkipped: null, Excluded: false }];
    serve(env.page, 'POST', BASE + 'ReleaseLocks', (entry) => entry.query.dryRun === 'true'
        ? { body: { DryRun: true, PreviewToken: 'release-ro', RestoreLibraries: false, StoreReadOnly: true, Released: rows } }
        : { body: { DryRun: false, RestoreLibraries: false, StoreReadOnly: true, Released: rows.map((r) => Object.assign({}, r, { PreferenceOutcome: 'TurnedOn' })) } });
    const result = env.root.querySelector('#AccessRolesReleaseResult');
    await click(env.page, env.root.querySelector('#AccessRolesReleasePreview'));
    assert.ok(result.textContent.includes('Anna: preference access back on'), result.textContent);
    assert.ok(!result.textContent.includes('; becomes Excluded'));
    assert.ok(result.textContent.includes('read-only, so nobody becomes Excluded'), result.textContent);
    await click(env.page, env.root.querySelector('#AccessRolesReleaseConfirm'));
    assert.ok(topDialog(env.page).textContent.includes('They stay managed: the data file is read-only.'), topDialog(env.page).textContent);
    await click(env.page, byText(topDialog(env.page), 'button', 'Release them'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'ReleaseLocks').map((e) => e.query), [{ ifPreview: 'release-ro' }]);
});

test('the emergency stop at the start: a 503 naming it shows only the inert view, and Turn enforcement back on saves the whole configuration with EnforcementEnabled true', { skip }, async () => {
    const PLUGIN = '/Plugins/7cbc1019-49a5-4b97-8aa2-279de946ef88/Configuration';
    const stop = (safeMode, disabled) => problem(503, undefined, 'Access Roles is inert (the emergency stop).', { inert: { SafeMode: safeMode, EnforcementDisabled: disabled, SafeModeFile: '/srv/jellyfin/data/accessroles/SAFE-MODE', ConfigurationFile: '/srv/jellyfin/plugins/configurations/Jellyfin.Plugin.AccessRoles.xml' } });
    const both = await start({ responses: [['GET', BASE + 'Diagnostics', stop(true, true), 503]] });
    const view = both.root.querySelector('#AccessRolesInert');
    assert.ok(view, 'the inert view');
    // What stays as last written, that an inert uninstall puts nothing back, and the README's
    // headings that exist.
    const leftBehind = both.root.querySelector('#AccessRolesInertLeftBehind').textContent;
    assert.ok(leftBehind.includes('native preference access its optional mirror turned off and the library access'), leftBehind);
    assert.ok(leftBehind.includes('Uninstalling it while it is inert puts nothing back'), leftBehind);
    assert.ok(view.textContent.includes('"Lockout and recovery": "The emergency stop"'), view.textContent);
    assert.ok(view.textContent.includes('"Recovery after the plugin folder was deleted with the mirror on"'));
    assert.ok(!view.textContent.includes('"Emergency stop"'));
    assert.ok(both.root.querySelector('#AccessRolesInertSafeMode').textContent.includes('/srv/jellyfin/data/accessroles/SAFE-MODE'));
    assert.ok(both.root.querySelector('#AccessRolesInertDisabled').textContent.includes('Jellyfin.Plugin.AccessRoles.xml'));
    assert.ok(Array.from(both.root.querySelector('.jfar-page-app').children).filter((c) => c !== view).every(hidden), 'the tabs are hidden');
    assert.equal(all(both.root, '[role="tab"]').filter((t) => !t.closest('[hidden]')).length, 0);

    const off = await start({ responses: [['GET', BASE + 'Diagnostics', stop(false, true), 503]] });
    assert.equal(off.root.querySelector('#AccessRolesInertSafeMode'), null, 'only the switch in effect');
    off.page.respond('GET', PLUGIN, { EnforcementEnabled: false, LibrarySyncEnabled: true, AuditRetentionMonths: 6 });
    serve(off.page, 'POST', PLUGIN, () => ({ status: 204 }));
    const enable = off.root.querySelector('#AccessRolesInertEnable');
    await click(off.page, enable);
    await click(off.page, byText(topDialog(off.page), 'button', 'Turn it back on'));
    assert.deepEqual(saves(off.page, 'POST', PLUGIN).map((e) => e.body), [{ EnforcementEnabled: true, LibrarySyncEnabled: true, AuditRetentionMonths: 6 }]);
    assert.ok(off.root.querySelector('#AccessRolesInertResult').textContent.includes('Restart the server'));
    // The button is enabled again and has the focus, not the page.
    assert.equal(enable.disabled, false);
    assert.ok(off.page.document.activeElement === enable, 'the focus is on ' + focusName(off.page));

    // The negative control: a 503 without the stop is the usual failed status, with the tabs.
    const failed = await start({ responses: [['GET', BASE + 'Diagnostics', problem(503, undefined, 'Access Roles can\'t read its data just now.'), 503]] });
    assert.equal(failed.root.querySelector('#AccessRolesInert'), null);
    assert.ok(all(failed.root, '[role="alert"]').some((b) => b.textContent.includes('The status couldn\'t be loaded')));
});

// The Data tab's import and restore answers: the server's AccessRolesDataImportDto.
function dataReport(changes) {
    return Object.assign({
        DryRun: true, Mode: 'Merge', MapUsersBy: 'Id', Source: 'Export', SourceName: '0.9.15.0', SourceUtc: UPDATED, SchemaVersion: 1, SameServer: false,
        Users: { Mapped: [{ Id: ANNA, NewId: ANNA, Name: 'Anna', Records: 1 }], Unknown: [{ Id: 'd0000000000000000000000000000001', Name: 'Ghost', Records: 2, Reason: 'NotOnThisServer' }] },
        Libraries: { Mapped: [{ ExportId: MOVIES, Id: MOVIES, Name: 'Movies', By: 'Id' }, { ExportId: 'e0000000000000000000000000000001', Id: HORROR, Name: 'Horror', By: 'Locations' }], Unmatched: [{ ExportId: 'e0000000000000000000000000000002', Name: 'Anime', Locations: ['X:\\Anime'], Reason: 'NoSuchLibrary' }] },
        Records: { Roles: { Added: 1, Updated: 0, Unchanged: 1, KeptNewer: 0, Removed: 0 }, Assignments: { Added: 1, Updated: 0, Unchanged: 0, KeptNewer: 0, Removed: 0 }, Ledger: { Incoming: 1, Added: 0, SameServer: false }, MirrorRecords: 0 },
        Skipped: [
            { Kind: 'RoleLibrary', Id: 'e0000000000000000000000000000002', Name: 'Anime', Reason: 'NoSuchLibrary', Of: 'Family', Libraries: null, Detail: null },
            { Kind: 'Role', Id: 'e0000000000000000000000000000003', Name: 'NoHorror', Reason: 'LibrariesUnmatched', Of: null, Libraries: [{ ExportId: 'e0000000000000000000000000000004', Name: 'Scary', Locations: [], Reason: 'Ambiguous' }], Detail: null },
            { Kind: 'Role', Id: 'e0000000000000000000000000000005', Name: 'Every but nothing', Reason: 'InvalidLibraries', Of: null, Libraries: null, Detail: 'All except… needs at least one library to leave out; a role for every library is All.' },
            { Kind: 'Assignment', Id: 'e0000000000000000000000000000006', Name: 'kid', Reason: 'UserLibrariesUnmatched', Of: null, Libraries: [{ ExportId: 'e0000000000000000000000000000004', Name: 'Scary', Locations: [], Reason: 'Ambiguous' }, { ExportId: 'e0000000000000000000000000000007', Name: null, Locations: [], Reason: 'NoSuchLibrary' }], Detail: null }
        ],
        Preview: preview({ Users: [leavingRow(null, { LeavesManagement: null, LibrariesAdded: [{ Id: MOVIES, Name: 'Movies' }] })], PreviewToken: 'import-1' }),
        NoChange: false, SafetySnapshot: null, SyncHeld: false,
        Notes: ['The export holds the settings for reference; an import never applies them (the Settings tab changes them).'],
        AuditSeq: []
    }, changes || {});
}

// A file the admin picked in the import's file field (the fake DOM has no file chooser).
async function pickFile(page, input, text) {
    input.files = [{ name: 'accessroles-20261101.json', text: () => Promise.resolve(text) }];
    page.dispatch(input, 'change');
    await page.flush();
}

test('the Data tab: Export saves the server\'s file under its name', { skip }, async () => {
    const env = await start({ responses: [['GET', BASE + 'Settings', settingsSample()], ['GET', BASE + 'Snapshots', { Snapshots: [], SyncHeld: false }]] });
    const panel = await openTab(env, 'Data');
    const before = globalThis.fetch;
    globalThis.fetch = (url, init) => {
        const parsed = new URL(String(url), 'http://127.0.0.1:8097');
        if (parsed.pathname !== BASE + 'Export') {
            return before(url, init);
        }

        env.page.fetchLog.push({ method: 'GET', path: parsed.pathname, query: {} });
        return Promise.resolve({
            ok: true,
            status: 200,
            headers: { get: (name) => (name.toLowerCase() === 'content-disposition' ? 'attachment; filename=accessroles-20261101-100000.json' : 'application/json') },
            text: () => Promise.resolve('{"Format":"jellyfin-plugin-export/1"}')
        });
    };
    const saved = [];
    const original = { create: URL.createObjectURL, revoke: URL.revokeObjectURL };
    URL.createObjectURL = () => 'blob:export-1';
    URL.revokeObjectURL = () => {};
    env.page.document.body.addEventListener('click', (e) => {
        if (e.target.getAttribute && e.target.getAttribute('download')) {
            saved.push(e.target.getAttribute('download'));
        }
    });
    try {
        await click(env.page, panel.querySelector('#AccessRolesDataExport'));
        await env.page.flush();
        assert.deepEqual(saved, ['accessroles-20261101-100000.json']);
        assert.equal(panel.querySelector('#AccessRolesDataLater'), null, 'the 0.9 placeholder is gone');
    } finally {
        URL.createObjectURL = original.create;
        URL.revokeObjectURL = original.revoke;
    }
});

test('the Data tab: Check the file is Merge\'s dry run of the parsed file, its report lists who and what isn\'t found, and Merge saves with its preview\'s PreviewToken', { skip }, async () => {
    const env = await start({ responses: [['GET', BASE + 'Settings', settingsSample()], ['GET', BASE + 'Snapshots', { Snapshots: [], SyncHeld: false }]] });
    const panel = await openTab(env, 'Data');
    const merge = panel.querySelector('#AccessRolesImportMerge');
    const replace = panel.querySelector('#AccessRolesImportReplace');
    assert.equal(merge.disabled, true, 'nothing to merge before a check');
    serve(env.page, 'POST', BASE + 'Import', (entry) => (entry.query.dryRun === 'true'
        ? { body: dataReport({ Mode: entry.query.mode, MapUsersBy: entry.query.mapUsersBy }) }
        : { body: dataReport({ DryRun: false, Mode: entry.query.mode, SafetySnapshot: 'accessroles-20261101-100000-import.json', AuditSeq: [7] }) }));

    // A file that isn't JSON is refused on the page: nothing is sent.
    await pickFile(env.page, panel.querySelector('[data-ar-import="file"]'), 'not json');
    await click(env.page, panel.querySelector('#AccessRolesImportCheck'));
    assert.ok(panel.textContent.includes('This file isn\'t JSON'), panel.textContent);
    assert.equal(requests(env.page, 'POST', BASE + 'Import').length, 0);

    const exported = { Format: 'jellyfin-plugin-export/1', PluginId: '7cbc1019-49a5-4b97-8aa2-279de946ef88', Data: { Roles: [] } };
    await pickFile(env.page, panel.querySelector('[data-ar-import="file"]'), JSON.stringify(exported));
    await click(env.page, panel.querySelector('#AccessRolesImportCheck'));
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Import').map((e) => [e.query, e.body]), [[{ dryRun: 'true', mode: 'Merge', mapUsersBy: 'Id' }, exported]]);
    const report = panel.querySelector('#AccessRolesImportReport').textContent;
    assert.ok(report.includes('Ghost: not on this server, 2 records'), report);
    assert.ok(report.includes('Anime (X:\\Anime): no library here has its folders or its name'), report);
    assert.ok(report.includes('Horror (by folders)'), report);
    assert.ok(report.includes('A role\'s library Anime of Family'), report);
    assert.ok(report.includes('Role NoHorror: libraries it names aren\'t found here (Scary), and without them it would grant more than the export had it'), report);
    assert.ok(report.includes('Role Every but nothing: All except… needs at least one library to leave out'), report);
    // 1.0.1: a user whose own Remove list names a library not found here is left out whole.
    assert.ok(report.includes('The roles and overrides of kid: libraries their own Remove list leaves out aren\'t found here (Scary, id e0000000000000000000000000000007), and without them they would see more than the export had them see'), report);
    assert.ok(report.includes('What it does to each user (1)'), report);
    assert.ok(report.includes('an import never applies them'), report);
    assert.equal(merge.disabled, false);
    assert.equal(replace.disabled, false);

    await click(env.page, merge);
    const dialog = topDialog(env.page);
    assert.equal(dialogTitle(dialog), 'Import: merge');
    await click(env.page, byText(dialog, 'button', 'Merge'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Import').map((e) => e.query), [{ dryRun: 'false', mode: 'Merge', mapUsersBy: 'Id', ifPreview: 'import-1' }]);
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Import')[0].body, exported, 'the file as checked');
    assert.ok(panel.querySelector('#AccessRolesImportReport').textContent.includes('accessroles-20261101-100000-import.json'));
    assert.equal(merge.disabled, true, 'a saved import needs a new check');
});

test('the Data tab: Replace by name sends its mode and match, and another match needs another check', { skip }, async () => {
    const env = await start({ responses: [['GET', BASE + 'Settings', settingsSample()], ['GET', BASE + 'Snapshots', { Snapshots: [], SyncHeld: false }]] });
    const panel = await openTab(env, 'Data');
    serve(env.page, 'POST', BASE + 'Import', (entry) => ({ body: dataReport({ DryRun: entry.query.dryRun === 'true', Mode: entry.query.mode, MapUsersBy: entry.query.mapUsersBy }) }));
    await pickFile(env.page, panel.querySelector('[data-ar-import="file"]'), '{"Format":"jellyfin-plugin-export/1"}');
    await choose(env.page, panel.querySelector('[data-ar-import="users-by"]'), 'Name');
    await click(env.page, panel.querySelector('#AccessRolesImportCheck'));
    await click(env.page, panel.querySelector('#AccessRolesImportReplace'));
    assert.equal(dialogTitle(topDialog(env.page)), 'Import: replace');
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Import').map((e) => e.query), [
        { dryRun: 'true', mode: 'Merge', mapUsersBy: 'Name' },
        { dryRun: 'true', mode: 'Replace', mapUsersBy: 'Name' }
    ]);
    await click(env.page, byText(topDialog(env.page), 'button', 'Cancel'));
    await choose(env.page, panel.querySelector('[data-ar-import="users-by"]'), 'Id');
    assert.equal(panel.querySelector('#AccessRolesImportReplace').disabled, true);
    assert.equal(panel.querySelector('#AccessRolesImportReport').textContent, '');

    // Another file chosen after a check: the check was of the other file, so Merge and Replace wait for a new one.
    await click(env.page, panel.querySelector('#AccessRolesImportCheck'));
    assert.equal(panel.querySelector('#AccessRolesImportMerge').disabled, false);
    await pickFile(env.page, panel.querySelector('[data-ar-import="file"]'), '{"Format":"jellyfin-plugin-export/1","Other":true}');
    assert.equal(panel.querySelector('#AccessRolesImportMerge').disabled, true);
    assert.equal(panel.querySelector('#AccessRolesImportReplace').disabled, true);
    assert.equal(panel.querySelector('#AccessRolesImportReport').textContent, '');
});

test('the Data tab: the snapshots listed when shown, Restore previews its dry run, saves with its PreviewToken, and sends the admin to the Overview\'s hold', { skip }, async () => {
    const snapshot = { Name: 'accessroles-20261031-020000.json', Kind: 'Daily', Operation: null, Utc: UPDATED, Size: 4096, SchemaVersion: 1, Kept: false };
    const env = await start({ responses: [['GET', BASE + 'Settings', settingsSample()]] });
    let held = false;
    serve(env.page, 'GET', BASE + 'Snapshots', () => ({ body: { Snapshots: [snapshot, { Name: 'accessroles-20261030-090000-import.json', Kind: 'PreOp', Operation: 'import', Utc: UPDATED, Size: 2048, SchemaVersion: 1 }], SyncHeld: held } }));
    serve(env.page, 'POST', BASE + 'Snapshots/Restore', (entry) => {
        if (entry.query.dryRun === 'true') {
            return { body: dataReport({ Mode: 'Replace', Source: 'Snapshot', SourceName: entry.query.name, SameServer: true, Preview: preview({ Users: [leavingRow(null, { LeavesManagement: null, LibrariesRemoved: [{ Id: HORROR, Name: 'Horror' }] })], PreviewToken: 'restore-1' }) }) };
        }

        held = true;
        return { body: dataReport({ DryRun: false, Mode: 'Replace', Source: 'Snapshot', SourceName: entry.query.name, SyncHeld: true, SafetySnapshot: 'accessroles-20261101-100500-restore.json' }) };
    });
    const panel = await openTab(env, 'Data');
    const items = all(panel, '#AccessRolesSnapshots li').map((li) => li.textContent);
    assert.equal(items.length, 2);
    assert.ok(items[1].includes('before import'), items[1]);

    await click(env.page, panel.querySelector('button[data-snapshot="accessroles-20261031-020000.json"]'));
    const dialog = topDialog(env.page);
    assert.equal(dialogTitle(dialog), 'Restore accessroles-20261031-020000.json');
    assert.ok(dialog.textContent.includes('Libraries removed: Horror'), dialog.textContent);
    await click(env.page, byText(dialog, 'button', 'Restore'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Snapshots/Restore').map((e) => e.query), [{ name: 'accessroles-20261031-020000.json', dryRun: 'false', ifPreview: 'restore-1' }]);
    const result = panel.querySelector('#AccessRolesSnapshotsResult').textContent;
    assert.ok(result.includes('The library sync is held'), result);
    assert.ok(result.includes('accessroles-20261101-100500-restore.json'), result);
    assert.ok(panel.querySelector('#AccessRolesSnapshots').textContent.includes('The library sync is held after a restore'));

    await click(env.page, panel.querySelector('#AccessRolesSnapshotsOverview'));
    assert.equal(tab(env, 'Overview').getAttribute('aria-selected'), 'true');
});

test('the Data tab: a read-only data file can\'t be restored or imported into', { skip }, async () => {
    const env = await start({
        diagnostics: diagnostics({ Store: { File: 'accessroles.json', Version: 2, CurrentVersion: 1, Status: 'ReadOnlyNewerVersion', ReadOnly: true } }),
        responses: [['GET', BASE + 'Settings', settingsSample()], ['GET', BASE + 'Snapshots', { Snapshots: [{ Name: 'accessroles-20261031-020000.json', Kind: 'Daily', Utc: UPDATED }], SyncHeld: false }]]
    });
    serve(env.page, 'POST', BASE + 'Import', () => ({ body: dataReport() }));
    const panel = await openTab(env, 'Data');
    assert.equal(panel.querySelector('button[data-snapshot]').disabled, true);
    await pickFile(env.page, panel.querySelector('[data-ar-import="file"]'), '{}');
    await click(env.page, panel.querySelector('#AccessRolesImportCheck'));
    assert.equal(panel.querySelector('#AccessRolesImportMerge').disabled, true);
    assert.ok(panel.textContent.includes('The data file is read-only'));
});

test('the Diagnostics tab: the unknown users listed, Remap by name sends its dry run\'s PlanToken as ifPlan, a 409 needs a new check, Apply deleted-user policy and Run maintenance now', { skip }, async () => {
    const GONE = 'd0000000000000000000000000000001';
    const env = await start({
        diagnostics: diagnostics({ Users: { DeletedUserPolicy: 'Delete', OrphanUsers: [{ UserId: GONE, LastKnownName: 'Anna', Records: 2, Marked: false }] } })
    });
    let plans = 0;
    serve(env.page, 'POST', BASE + 'Users/Remap', (entry) => (entry.query.dryRun === 'true'
        ? { body: { DryRun: true, Mapped: [{ OldId: GONE, NewId: ANNA, Name: 'Anna', Records: 2 }], Unmatched: [], MovedRecords: 0, PlanToken: 'plan-' + (++plans) } }
        : entry.query.ifPlan === 'plan-2'
            ? { body: { DryRun: false, Mapped: [{ OldId: GONE, NewId: ANNA, Name: 'Anna', Records: 2 }], Unmatched: [], MovedRecords: 2 } }
            : { status: 409, body: problem(409, undefined, 'The remap changed since its dry run.') }));
    serve(env.page, 'POST', BASE + 'Users/ApplyPolicy', () => ({ body: { Policy: 'Delete', Affected: 2, Users: [{ UserId: GONE, Affected: 2 }] } }));
    serve(env.page, 'POST', BASE + 'Maintenance/Run', () => ({ body: { Queued: true, Task: 'Access Roles: Maintenance' } }));
    const panel = await openTab(env, 'Diagnostics');
    assert.ok(panel.querySelector('#AccessRolesOrphanUsers').textContent.includes('Anna (id ' + GONE + '): 2 records'));

    await click(env.page, panel.querySelector('#AccessRolesRemap'));
    let dialog = topDialog(env.page);
    assert.ok(dialog.textContent.includes('Anna: id ' + GONE + ' becomes this server\'s Anna (2 records)'), dialog.textContent);
    await click(env.page, byText(dialog, 'button', 'Remap'));
    assert.ok(dialog.textContent.includes('changed since the check, or the data cannot be written now; nothing was changed'), dialog.textContent);
    assert.equal(byText(dialog, 'button', 'Remap').disabled, true);
    await click(env.page, byText(dialog, 'button', 'Check again'));
    await click(env.page, byText(dialog, 'button', 'Remap'));
    assert.deepEqual(saves(env.page, 'POST', BASE + 'Users/Remap').map((e) => e.query), [
        { by: 'Name', dryRun: 'false', ifPlan: 'plan-1' },
        { by: 'Name', dryRun: 'false', ifPlan: 'plan-2' }
    ]);
    assert.ok(panel.querySelector('#AccessRolesUserActionsResult').textContent.includes('Remapped 1 user (2 records)'));

    await click(env.page, panel.querySelector('#AccessRolesApplyPolicy'));
    dialog = topDialog(env.page);
    assert.ok(dialog.textContent.includes('Anna: 2 records'));
    await click(env.page, byText(dialog, 'button', 'Delete their records'));
    assert.deepEqual(requests(env.page, 'POST', BASE + 'Users/ApplyPolicy').map((e) => e.body), [{ UserIds: [GONE] }]);

    await click(env.page, panel.querySelector('#AccessRolesRunMaintenance'));
    assert.equal(requests(env.page, 'POST', BASE + 'Maintenance/Run').length, 1);
    assert.ok(panel.querySelector('#AccessRolesUserActionsResult').textContent.includes('Access Roles: Maintenance is queued'));
});

test('on the TV layout: after the last unknown user is handled, the focus moves to Run maintenance now; with one left it stays', { skip }, async () => {
    const GONE = 'd0000000000000000000000000000001';
    const LEFT = 'd0000000000000000000000000000002';
    const orphan = (id, name) => ({ UserId: id, LastKnownName: name, Records: 1, Marked: false });
    const env = await start({ tv: true, diagnostics: diagnostics({ Users: { DeletedUserPolicy: 'Delete', OrphanUsers: [orphan(GONE, 'Anna'), orphan(LEFT, 'Carl')] } }) });
    serve(env.page, 'POST', BASE + 'Users/ApplyPolicy', (entry) => ({ body: { Policy: 'Delete', Affected: entry.body.UserIds.length, Users: entry.body.UserIds.map((id) => ({ UserId: id, Affected: 1 })) } }));
    serve(env.page, 'POST', BASE + 'Users/Remap', (entry) => ({ body: { DryRun: entry.query.dryRun === 'true', Mapped: [{ OldId: GONE, NewId: ANNA, Name: 'Anna', Records: 1 }], Unmatched: [], MovedRecords: 1, PlanToken: 'plan-1' } }));
    const panel = await openTab(env, 'Diagnostics');

    // One of two remapped: Carl is left, so the focus stays on Remap by name.
    env.page.respond('GET', BASE + 'Diagnostics', diagnostics({ Users: { DeletedUserPolicy: 'Delete', OrphanUsers: [orphan(LEFT, 'Carl')] } }));
    panel.querySelector('#AccessRolesRemap').focus();
    await click(env.page, panel.querySelector('#AccessRolesRemap'));
    await click(env.page, byText(topDialog(env.page), 'button', 'Remap'));
    assert.ok(panel.querySelector('#AccessRolesUserActionsResult').textContent.includes('Remapped 1 user'));
    assert.equal(panel.querySelector('#AccessRolesRemap').disabled, false);
    assert.equal(focusName(env.page), 'AccessRolesRemap', 'one unknown user left: the focus stays');

    // The last one deleted: Remap and Apply are off, the focus is on Run maintenance now.
    env.page.respond('GET', BASE + 'Diagnostics', diagnostics({ Users: { DeletedUserPolicy: 'Delete', OrphanUsers: [] } }));
    panel.querySelector('#AccessRolesApplyPolicy').focus();
    await click(env.page, panel.querySelector('#AccessRolesApplyPolicy'));
    await click(env.page, byText(topDialog(env.page), 'button', 'Delete their records'));
    assert.ok(panel.querySelector('#AccessRolesOrphanUsers').textContent.includes('every record belongs to a user of this server'));
    assert.equal(panel.querySelector('#AccessRolesApplyPolicy').disabled, true);
    assert.equal(panel.querySelector('#AccessRolesRemap').disabled, true);
    assert.equal(focusName(env.page), 'AccessRolesRunMaintenance', 'the last unknown user handled: the focus on Run maintenance now');
});

test('the Diagnostics tab: with no unknown user the actions for them are off', { skip }, async () => {
    const env = await start({ diagnostics: diagnostics({ Users: { DeletedUserPolicy: 'Delete', OrphanUsers: [] } }) });
    const panel = await openTab(env, 'Diagnostics');
    assert.ok(panel.querySelector('#AccessRolesOrphanUsers').textContent.includes('every record belongs to a user of this server'));
    assert.equal(panel.querySelector('#AccessRolesRemap').disabled, true);
    assert.equal(panel.querySelector('#AccessRolesApplyPolicy').disabled, true);
    assert.equal(panel.querySelector('#AccessRolesRunMaintenance').disabled, false);
});

test('on the TV layout: the Data tab\'s buttons and a snapshot\'s Restore take the focus', { skip }, async () => {
    const env = await start({ tv: true, responses: [['GET', BASE + 'Settings', settingsSample()], ['GET', BASE + 'Snapshots', { Snapshots: [{ Name: 'accessroles-20261031-020000.json', Kind: 'Daily', Utc: UPDATED }], SyncHeld: false }]] });
    const panel = await openTab(env, 'Data');
    for (const element of [panel.querySelector('#AccessRolesDataExport'), panel.querySelector('#AccessRolesImportCheck'), panel.querySelector('button[data-snapshot]')]) {
        element.focus();
        assert.ok(env.page.document.activeElement === element, 'the focus is on ' + focusName(env.page));
    }
});
