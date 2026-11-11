#!/usr/bin/env node
// Access Roles' web guard (the web rows of the bypass matrix) in a running sandbox, through the
// web client in headless Chrome (scripts/webcheck.mjs), as the credentials.json users viewer, viewer2 and admin:
//   1. index.html carries the head fragment once, inline before </head> (so it runs before jellyfin-web's deferred
//      bundles), its CSS holding the hide rules and its flags nothing but server-wide values; the ClientScript is served.
//   2. Legacy desktop, viewer with the probe's Kid role (accessroles-web-guard-legacy.json): the head ran before the bundles, the
//      locked Settings links hidden, a locked link's click, a JS navigation, a typed URL (a legacy view's first visit), a
//      percent-encoded one, routes behind a second '#', two locked navigations 400 ms apart and a cold load all end on
//      Home with the toast and no frame of a locked page, Back never returns to one, Display (account) disabled on the
//      Display page, the Display, Playback and Subtitles pages' own form saves (nothing kept and no toast of ours for an
//      unchanged save, the header and the toast for a locked field changed), a kept save's toast.
//   3. The first load (accessroles-web-guard-firstload.json): jfar-pending from the head with no cached answer, cleared by /Me;
//      a /Me held 4.5 s (pending gone at 3 s, the late answer applied and cached, a locked URL then guarded); the
//      ClientScript held 12 s (the head lifts its own pending at 3 s); a sign-out and viewer2's sign-in without a reload:
//      jfar-pending from the guard, then nothing (Managed false cached).
//   4. Experimental layout, Kid (accessroles-web-guard-experimental.json): the MUI user menu's hidden items aria-disabled, the
//      focus fix with the pointer and with Enter, the arrow keys; the late Display page's fields disabled; its form saves
//      and Playback's and Subtitles' as in the legacy one; locked URLs, one behind a second '#'.
//   5. TV layout, Kid (accessroles-web-guard-tv.json): the user button to the Settings menu, Sign out, the D-pad skipping
//      hidden links, a locked URL redirected, the layout selector enabled.
//   6. viewer with the probe's Guest role (every section): legacy (accessroles-web-guard-legacy-guest.json: the drawer's
//      Settings hidden, the note above Sign out), experimental (accessroles-web-guard-experimental-guest.json: only Sign out
//      in the menu, focused, no divider alone) and a 375 px phone (accessroles-web-guard-mobile.json).
//   7. admin (accessroles-web-guard-admin.json), Kid as the default role: the banner on viewer's stock Library access tab, none
//      for viewer2, the default role's banner on Add user.
//   8. viewer2 managed with no library (accessroles-web-guard-nolibraries.json): Home's message replaced.
// It writes only what is its own: its two roles ("Guard probe Kid", "Guard probe Guest", lock-only), viewer's assignment,
// viewer2's assignment (its library mode Only with nothing, confirmed, which the library sync writes; afterwards
// unmanaged with Restore, which the sync writes back) and the default roles (set to Kid for step 7, then back as they
// were); and, for the legacy and experimental webchecks' form saves, viewer's usersettings display preferences without
// their two Display (account) keys (the check of jellyfin-web's default posts needs a user who never stored them), as the admin, put
// back as they were afterwards. The webchecks save viewer's settings through jellyfin-web's own pages and ApiClient: the
// merges keep the locked values, and they put back what they change. Each webcheck is run up to six times when only its first sign-in fails (the login page's user cards, a known
// flake; a later sign-in is part of what it checks).
// viewer's and viewer2's policies and settings, and every other user's policy, must be as they were. jellyfin-web's forms
// post an empty text for a text setting never set (AudioLanguagePreference, SubtitleLanguagePreference, CastReceiverId:
// null on a freshly seeded sandbox), which Jellyfin stores, so those three are compared with null and "" the same;
// afterwards the admin writes viewer's and viewer2's configuration back exactly as it was when it
// differs. Node 22 built-ins only; loopback only, never port 8096; tokens are never printed.
//
//   node scripts/accessroles-web-guard-probe.mjs --sandbox access [--only legacy,tv] [--out result.json]
//
// Start the sandbox first (scripts/README.md, this probe's section). Prints one JSON result (pass, checks,
// transcript, evidence) and exits with 0 when every check passed.

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PREFIX = 'Guard probe ';
// Jellyfin.Api's display preferences of the web client, for a user (the admin may write any user's).
const USERSETTINGS = (userId) => `/DisplayPreferences/usersettings?userId=${userId}&client=emby`;
const KID_LOCKS = ['Profile', 'DisplayAccount', 'Home', 'Controls', 'QuickConnect', 'EnhancedPanel'];
const GUEST_LOCKS = ['Profile', 'DisplayAccount', 'DisplayDevice', 'Home', 'Playback', 'Subtitles', 'Controls', 'QuickConnect', 'EnhancedPanel'];
const SCENARIOS = [
    { name: 'legacy', file: 'accessroles-web-guard-legacy.json', viewer: 'Kid' },
    { name: 'firstload', file: 'accessroles-web-guard-firstload.json', viewer: 'Kid' },
    { name: 'experimental', file: 'accessroles-web-guard-experimental.json', viewer: 'Kid' },
    { name: 'tv', file: 'accessroles-web-guard-tv.json', viewer: 'Kid' },
    { name: 'legacy-guest', file: 'accessroles-web-guard-legacy-guest.json', viewer: 'Guest' },
    { name: 'experimental-guest', file: 'accessroles-web-guard-experimental-guest.json', viewer: 'Guest' },
    { name: 'mobile', file: 'accessroles-web-guard-mobile.json', viewer: 'Guest' },
    { name: 'admin', file: 'accessroles-web-guard-admin.json', viewer: 'Kid', defaultRole: 'Kid' },
    { name: 'nolibraries', file: 'accessroles-web-guard-nolibraries.json', viewer: null, viewer2NoLibraries: true }
];

function usage() {
    return 'usage: node scripts/accessroles-web-guard-probe.mjs --sandbox <name> [--only <scenario,...>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, only: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--only') o.only = String(argv[++i] || '').split(',').filter(Boolean);
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (o.only && o.only.some((n) => !SCENARIOS.some((s) => s.name === n))) throw new Error(`--only takes ${SCENARIOS.map((s) => s.name).join(', ')}`);
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const credentials = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer', 'viewer2']) {
    if (!credentials[name]) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}

const yaml = fs.readFileSync(path.join(repoRoot, 'plugins', 'AccessRoles', 'build.yaml'), 'utf8');
const plugin = { version: (/^version:\s*"?([^"\r\n]+)"?\s*$/m.exec(yaml) || [])[1] };

const checks = [];
const transcript = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin, webchecks: {} };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// An admin request (credentials.json's token, never printed).
function request(method, p, body = null) {
    return new Promise((resolve, reject) => {
        const headers = { Authorization: `MediaBrowser Client="accessroles-web-guard-probe", Device="sandbox", DeviceId="accessroles-web-guard-probe", Version="1.0.0", Token="${credentials.admin.token}"`, Accept: 'application/json' };
        if (body !== null) headers['Content-Type'] = 'application/json';
        const req = http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let json = null;
                try { json = text && /json/.test(res.headers['content-type'] || '') ? JSON.parse(text) : null; } catch { json = null; }
                resolve({ status: res.statusCode, headers: res.headers, text, json });
            });
        });
        req.on('error', reject);
        req.setTimeout(60000, () => req.destroy(new Error(`timeout ${method} ${p}`)));
        if (body !== null) req.write(JSON.stringify(body));
        req.end();
    });
}

async function step(label, method, p, body = null) {
    const r = await request(method, p, body);
    transcript.push({ step: label, request: `${method} ${p}`, body: body === null ? undefined : body, status: r.status, code: r.json && r.json.Code ? r.json.Code : undefined });
    return r;
}

const idN = (x) => (x || '').replace(/-/g, '').toLowerCase();
const stable = (value) => JSON.stringify(value, (k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.keys(v).sort().map((key) => [key, v[key]])) : v));
const access = (u) => ({ EnableAllFolders: u.Policy.EnableAllFolders, EnabledFolders: [...(u.Policy.EnabledFolders || [])].sort() });
// The text settings jellyfin-web's forms post as "" when they were never set (null): the same, for the comparisons.
const BLANK_TEXTS = ['AudioLanguagePreference', 'SubtitleLanguagePreference', 'CastReceiverId'];
const blank = (configuration) => Object.assign({}, configuration, Object.fromEntries(BLANK_TEXTS.map((k) => [k, (configuration || {})[k] || ''])));
const usersRows = async () => (await request('GET', '/AccessRoles/Admin/Users')).json || [];

async function waitFor(label, fn, timeoutMs = 30000) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
        const value = await fn();
        if (value) return value;
        if (Date.now() > deadline) throw new Error(`timed out waiting for ${label}`);
        await delay(500);
    }
}

// One assignment save of the probe's: viewer's or viewer2's, by the row's token.
async function assign(user, body, label, query = '') {
    const row = (await usersRows()).find((r) => idN(r.UserId) === idN(user.Id));
    const r = await step(label, 'PUT', `/AccessRoles/Admin/Users/${user.Id}${query}`, Object.assign({}, body, row && row.UpdatedUtc ? { IfUpdatedAt: row.UpdatedUtc } : {}));
    if (r.status !== 200) throw new Error(`${label}: ${r.status} ${r.text.slice(0, 300)}`);
    return r.json;
}

// Whether a webcheck's run failed in its first sign-in only (the login page's user cards, a known flake): the one flake
// worth another run. A later sign-in, such as the first-load webcheck's second user's without a reload, is what the
// webcheck tests, so its failure stands.
function signInFlake(result) {
    const steps = (result && result.steps) || [];
    const failed = steps.find((s) => !s.ok);
    return !!failed && failed.action === 'login' && steps.find((s) => s.action === 'login') === failed;
}

async function runWebcheck(file) {
    const attempts = [];
    let result = null;
    for (let attempt = 1; attempt <= 6; attempt++) {
        if (attempt > 1) await delay(5000);
        const run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', file], { encoding: 'utf8', timeout: 300000 });
        try { result = JSON.parse(run.stdout); } catch { result = null; }
        const failed = result && (result.steps || []).find((s) => !s.ok);
        attempts.push({ attempt, status: run.status, failedStep: failed ? failed.name : null, error: failed ? failed.error : (result ? undefined : (run.stderr || '').slice(0, 300)) });
        if (run.status === 0 && result && result.ok === true) break;
        if (!signInFlake(result)) break;
    }
    return { result, attempts };
}

let roles = {};
let users = {};
let before = null;
let defaultRolesBefore = null;
let viewerPrefsBefore = null;
// Whether each user's configuration was as it was but for "" over null before the write back, and whose was written back.
const configBeforeWriteBack = {};
let configWrittenBack = null;
const cleanupErrors = [];

async function cleanup() {
    // viewer's usersettings document back as it was; default roles back as they were; viewer unassigned; viewer2 unmanaged
    // with Restore (written back by the sync); the probe's roles deleted.
    try {
        if (viewerPrefsBefore !== null) {
            const r = await step('viewer\'s usersettings display preferences back as they were', 'POST', `/DisplayPreferences/usersettings?userId=${users.viewer.Id}&client=emby`, viewerPrefsBefore);
            if (r.status !== 204) cleanupErrors.push(`viewer's display preferences: ${r.status}`);
        }
    } catch (e) { cleanupErrors.push(String(e)); }

    try {
        if (defaultRolesBefore !== null) {
            const r = await step('the default roles back as they were', 'PUT', '/AccessRoles/Admin/DefaultRoles', { RoleIds: defaultRolesBefore });
            if (r.status !== 200 && r.status !== 204) cleanupErrors.push(`default roles: ${r.status}`);
        }
    } catch (e) { cleanupErrors.push(String(e)); }

    for (const row of (await usersRows()).filter((r) => r.State !== 'NotManaged')) {
        const user = Object.values(users).find((u) => idN(u.Id) === idN(row.UserId));
        if (!user) { cleanupErrors.push(`a managed user the probe didn't manage: ${row.Name}`); continue; }
        try {
            const r = await step(`unmanage ${user.Name} (Restore)`, 'POST', `/AccessRoles/Admin/Users/${user.Id}/Unmanage`, { Choice: 'Restore' });
            if (r.status !== 200) {
                const plain = await step(`remove ${user.Name}'s assignment`, 'PUT', `/AccessRoles/Admin/Users/${user.Id}`, { RoleIds: [], IfUpdatedAt: row.UpdatedUtc });
                if (plain.status !== 200) cleanupErrors.push(`${user.Name}: ${r.status}/${plain.status}`);
            }
        } catch (e) { cleanupErrors.push(String(e)); }
    }

    if (before) {
        // The release of viewer2's original access is the sync's to write (2 s after the change).
        try {
            await waitFor('viewer2\'s library access back as it was', async () => stable(access((await request('GET', `/Users/${users.viewer2.Id}`)).json)) === stable(before.access.viewer2), 60000);
        } catch (e) { cleanupErrors.push(String(e)); }
    }

    for (const r of ((await request('GET', '/AccessRoles/Admin/Roles')).json || []).filter((x) => x.Name.startsWith(PREFIX))) {
        const d = await step(`delete the probe's role ${r.Name}`, 'DELETE', `/AccessRoles/Admin/Roles/${r.Id}`);
        if (d.status !== 204 && d.status !== 200) cleanupErrors.push(`role ${r.Name}: ${d.status}`);
    }

    // viewer's and viewer2's configuration as it was: what the forms saved differs only by ""
    // for a text never set, which the check below takes as the same; the admin, whom no lock applies to and who manages
    // nobody now, writes back the configuration exactly as it was read, so the next probe starts from it.
    if (before) {
        configWrittenBack = [];
        for (const name of ['viewer', 'viewer2']) {
            try {
                const now = ((await request('GET', `/Users/${users[name].Id}`)).json || {}).Configuration;
                configBeforeWriteBack[name] = now ? stable(blank(now)) === stable(blank(before.configuration[name])) : false;
                if (now && stable(now) !== before.config[name]) {
                    const r = await step(`${name}'s configuration back exactly as it was (the admin)`, 'POST', `/Users/Configuration?userId=${users[name].Id}`, before.configuration[name]);
                    if (r.status !== 204) cleanupErrors.push(`${name}'s configuration: ${r.status}`);
                    configWrittenBack.push(name);
                }
            } catch (e) { cleanupErrors.push(String(e)); }
        }
    }
}

async function main() {
    // 1. The plugin, index.html and the ClientScript.
    const plugins = (await request('GET', '/Plugins')).json || [];
    const ours = plugins.find((p) => p.Name === 'Access Roles');
    check(`Access Roles ${plugin.version} is loaded and Active`, ours && ours.Version === plugin.version && ours.Status === 'Active', ours && { Version: ours.Version, Status: ours.Status });
    const index = await request('GET', '/web/index.html');
    const html = index.text;
    const headAt = html.indexOf('<script data-jfar-head>');
    const headEnd = html.toLowerCase().indexOf('</head>');
    const firstBundle = html.indexOf('<script defer');
    const flags = (/window\.__jfarHead=(\{[^;]*\});/.exec(html) || [])[1];
    evidence.indexHtml = { status: index.status, styles: (html.match(/<style data-jfar-head>/g) || []).length, scripts: (html.match(/<script data-jfar-head>/g) || []).length, headAt, headEnd, firstBundle, flags: flags ? JSON.parse(flags) : null };
    check('index.html carries the head fragment once, inline right before </head> (after the deferred bundles\' tags, so it runs first), with only server-wide flags',
        index.status === 200 && evidence.indexHtml.styles === 1 && evidence.indexHtml.scripts === 1 && headAt > firstBundle && headAt < headEnd && !!flags
            && Object.keys(JSON.parse(flags)).sort().join() === 'anyManaged,enforced,firstLoad,notice,userPages',
        evidence.indexHtml);
    const css = (/<style data-jfar-head>([^<]*)<\/style>/.exec(html) || [])[1] || '';
    check('the head\'s CSS hides the locked entry points by the declared anchors (the Settings links, the MUI user menu\'s items, the drawer\'s Settings), jfar-pending and jfar-blocking',
        /html\.jfar-lock-profile #myPreferencesMenuPage \.lnkUserProfile\{display:none!important\}/.test(css) && /html\.jfar-lock-allsettings \.mainDrawer \.btnSettings\{display:none!important\}/.test(css)
            && /html\.jfar-pending/.test(css) && /html\.jfar-blocking/.test(css), { bytes: css.length });
    const script = await request('GET', '/AccessRoles/ClientScript');
    check('the ClientScript is served with the guard', script.status === 200 && script.text.indexOf('// ---- Web/js/40-guard.js') >= 0, { status: script.status, bytes: script.text.length });

    // The users, and what must be as it was afterwards.
    const all = (await request('GET', '/Users')).json || [];
    for (const name of ['viewer', 'viewer2', 'admin']) {
        users[name] = all.find((u) => u.Name === name);
        if (!users[name]) throw new Error(`no ${name} in the sandbox`);
    }

    const managedNow = (await usersRows()).filter((r) => r.State !== 'NotManaged');
    if (managedNow.length > 0) throw new Error(`the sandbox manages ${managedNow.map((r) => r.Name).join(', ')} already; the probe starts from nobody managed`);
    before = {
        access: { viewer: access(users.viewer), viewer2: access(users.viewer2) },
        config: { viewer: stable(users.viewer.Configuration), viewer2: stable(users.viewer2.Configuration) },
        // As read, for the write back.
        configuration: { viewer: users.viewer.Configuration, viewer2: users.viewer2.Configuration },
        others: Object.fromEntries(all.filter((u) => !['viewer', 'viewer2'].includes(u.Name)).map((u) => [u.Id, stable(u.Policy)]))
    };
    defaultRolesBefore = ((await request('GET', '/AccessRoles/Admin/Roles')).json || []).filter((r) => r.IsDefault).map((r) => r.Id);

    // jellyfin-web posts its defaults for the two Display (account) keys on every Display save
    // (useEpisodeImagesInNextUpAndResume "false", its default theme), so the form saves need a user who never stored them.
    // viewer's are taken out by the admin, to whom no lock applies, and put back as they were in the cleanup.
    if (SCENARIOS.some((s) => ['legacy', 'experimental'].includes(s.name) && (!opts.only || opts.only.includes(s.name)))) {
        const prefs = await request('GET', USERSETTINGS(users.viewer.Id));
        if (prefs.status !== 200 || !prefs.json || !prefs.json.CustomPrefs) throw new Error(`viewer's display preferences: ${prefs.status}`);
        viewerPrefsBefore = prefs.json;
        const without = JSON.parse(JSON.stringify(prefs.json));
        delete without.CustomPrefs.useEpisodeImagesInNextUpAndResume;
        delete without.CustomPrefs.dashboardTheme;
        evidence.viewerDisplayAccountBefore = { useEpisodeImagesInNextUpAndResume: prefs.json.CustomPrefs.useEpisodeImagesInNextUpAndResume, dashboardTheme: prefs.json.CustomPrefs.dashboardTheme };
        const r = await step('viewer\'s usersettings display preferences without the Display (account) keys (the review\'s precondition)', 'POST', `/DisplayPreferences/usersettings?userId=${users.viewer.Id}&client=emby`, without);
        if (r.status !== 204) throw new Error(`viewer's display preferences: ${r.status} ${r.text.slice(0, 200)}`);
    }

    for (const [key, locks] of [['Kid', KID_LOCKS], ['Guest', GUEST_LOCKS]]) {
        const r = await step(`the probe's ${key} role (lock-only)`, 'POST', '/AccessRoles/Admin/Roles', { Name: PREFIX + key, Locks: locks, Libraries: { Mode: 'NotManaged', Refs: [] } });
        if (r.status !== 200 && r.status !== 201) throw new Error(`role ${key}: ${r.status} ${r.text.slice(0, 200)}`);
        roles[key] = r.json.Role;
    }

    let viewerRole = null;
    let defaultSet = false;
    for (const scenario of SCENARIOS.filter((s) => !opts.only || opts.only.includes(s.name))) {
        if (scenario.viewer && scenario.viewer !== viewerRole) {
            await assign(users.viewer, { RoleIds: [roles[scenario.viewer].Id] }, `viewer gets the probe's ${scenario.viewer} role`);
            viewerRole = scenario.viewer;
        }

        if (scenario.defaultRole && !defaultSet) {
            const r = await step('the probe\'s Kid role is the default role', 'PUT', '/AccessRoles/Admin/DefaultRoles', { RoleIds: [roles.Kid.Id] });
            if (r.status !== 200 && r.status !== 204) throw new Error(`default roles: ${r.status}`);
            defaultSet = true;
        }

        if (scenario.viewer2NoLibraries) {
            await assign(users.viewer2, { RoleIds: [roles.Kid.Id], Libraries: { Mode: 'Only', Add: [], Remove: [], ConfirmNoLibraries: true } }, 'viewer2 is managed with no library (Only, nothing, confirmed)');
            await waitFor('the sync writes viewer2\'s empty library access', async () => {
                const u = (await request('GET', `/Users/${users.viewer2.Id}`)).json;
                return !u.Policy.EnableAllFolders && (u.Policy.EnabledFolders || []).length === 0;
            }, 60000);
        }

        const { result, attempts } = await runWebcheck(scenario.file);
        const steps = result ? (result.steps || []).map((s) => ({ name: s.name, ok: s.ok, skipped: s.skipped, value: s.value, error: s.error })) : [];
        evidence.webchecks[scenario.name] = { file: scenario.file, ok: !!(result && result.ok), attempts, steps, exceptions: result && result.exceptions, failedRequests: result && result.failedRequests, screenshots: result && result.screenshots };
        const failedOurs = ((result && result.failedRequests) || []).filter((f) => /\/AccessRoles\//i.test(f.url || ''));
        check(`${scenario.name}: ${scenario.file} passes (${steps.length} steps), no request of Access Roles' failed`, !!(result && result.ok) && failedOurs.length === 0, { attempts, failed: steps.filter((s) => !s.ok).map((s) => `${s.name}: ${s.error}`), failedRequests: failedOurs });
    }
}

let failure = null;
try {
    await main();
} catch (e) {
    failure = e.stack || String(e);
} finally {
    try {
        await cleanup();
        if (before) {
            const after = (await request('GET', '/Users')).json || [];
            const viewer = after.find((u) => u.Name === 'viewer');
            const viewer2 = after.find((u) => u.Name === 'viewer2');
            const changed = after.filter((u) => before.others[u.Id] !== undefined && before.others[u.Id] !== stable(u.Policy)).map((u) => u.Name);
            const prefsAfter = viewerPrefsBefore === null ? null : ((await request('GET', USERSETTINGS(viewer.Id))).json || {}).CustomPrefs;
            evidence.cleanup = {
                roles: ((await request('GET', '/AccessRoles/Admin/Roles')).json || []).filter((r) => r.Name.startsWith(PREFIX)).length,
                managed: (await usersRows()).filter((r) => r.State !== 'NotManaged').length,
                defaultRoles: ((await request('GET', '/AccessRoles/Admin/Roles')).json || []).filter((r) => r.IsDefault).map((r) => r.Id),
                viewerAccess: stable(access(viewer)) === stable(before.access.viewer),
                viewer2Access: stable(access(viewer2)) === stable(before.access.viewer2),
                viewerConfig: configBeforeWriteBack.viewer === true && stable(viewer.Configuration) === before.config.viewer,
                viewer2Config: configBeforeWriteBack.viewer2 === true && stable(viewer2.Configuration) === before.config.viewer2,
                configWrittenBack,
                viewerPrefs: viewerPrefsBefore === null || stable(prefsAfter) === stable(viewerPrefsBefore.CustomPrefs),
                othersChanged: changed,
                errors: cleanupErrors
            };
            check('afterwards: the probe\'s roles gone, nobody managed, the default roles as they were, viewer\'s and viewer2\'s library access as they were, their settings as they were but for "" over a text never set (then written back exactly as they were), viewer\'s display preferences as they were, no other user\'s policy written',
                evidence.cleanup.roles === 0 && evidence.cleanup.managed === 0 && stable(evidence.cleanup.defaultRoles) === stable(defaultRolesBefore)
                    && evidence.cleanup.viewerAccess && evidence.cleanup.viewer2Access && evidence.cleanup.viewerConfig && evidence.cleanup.viewer2Config && evidence.cleanup.viewerPrefs && changed.length === 0 && cleanupErrors.length === 0,
                evidence.cleanup);
        }
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + (e.stack || String(e));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
