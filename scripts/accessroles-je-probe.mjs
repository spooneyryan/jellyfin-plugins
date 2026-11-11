#!/usr/bin/env node
// Access Roles next to Jellyfin Enhanced in a sandbox started with
// -Community FileTransformation,PluginPages,JellyfinEnhanced (JE 12.14), and Bookmarks if it is there:
//   1. Every JE user-settings save refused for a user whose role locks EnhancedPanel (403 Locked, nothing written): its
//      panel settings, shortcuts, Elsewhere, hidden content, spoiler blur and Ddd triggers, and Spoiler Guard's preferences
//      (POST spoiler-blur/user-prefs), also by another spelling of the route and the id; a user who isn't locked and the
//      administrator get JE's own answers.
//   2. Bookmarks still work for that user: JE's (add, read back, remove) and Bookmarks' own, when installed.
//   3. JE's global filters plus ours on /Items: with JE's Hidden Content on and a movie of the user's one library hidden
//      for the user by the administrator (JE's admin API), the user's /Items lists the other movie of that library, not the
//      hidden one (JE's filter) and not the other library's (the library role, and LibraryHardening's ids check); an
//      unmanaged user's lists all three.
//   4. The rule table's self-check lists JE's seven saves as locked, none unreviewed.
// With --webcheck also the web client in headless Chrome (webcheck.mjs) as credentials.json's viewer, given a lock-only
// role of the probe's (Profile, Quick Connect, Jellyfin Enhanced): accessroles-je-legacy.json (JE's drawer and
// Settings entries hidden; its D and Q keys end on Home, Q with the guard's toast) and accessroles-je-experimental.json
// (JE's cloned user-menu entry hidden; D and Q again).
// It writes only what is its own: its users (jeprobe-..., random passwords and sign-in tokens kept in memory, never printed or
// saved), their JE files and bookmarks (deleted with the users), its roles ("JE probe ..."), their assignments and
// viewer's (Unmanage Keep in a finally: lock-only, so nothing native is written for viewer), JE's Hidden Content setting
// (put back) and the hidden movie (unhidden). Node 22 built-ins only. Loopback only, never port 8096.
//
//   node scripts/accessroles-je-probe.mjs --sandbox access [--webcheck] [--out result.json]
//
// Start the sandbox first (scripts/README.md, this probe's section). Prints one JSON result (pass, checks, transcript,
// evidence) and exits with 0 when every check passed.

import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PLUGIN = 'AccessRoles';
const PREFIX = 'JE probe ';
const PROBE_USER = 'jeprobe-';
const LOCKED_TYPE = 'urn:jellyfin-plugin-kit:locked';
const JE_NAME = 'Jellyfin Enhanced';
const WEB_LOCKS = ['Profile', 'QuickConnect', 'EnhancedPanel'];
const WEBCHECKS = ['accessroles-je-legacy.json', 'accessroles-je-experimental.json'];

function usage() {
    return 'usage: node scripts/accessroles-je-probe.mjs --sandbox <name> [--webcheck] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, webcheck: false };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--webcheck') o.webcheck = true;
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
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
if (!credentials.admin || !credentials.admin.token) throw new Error('no admin in credentials.json; run Initialize-Sandbox.ps1');

const yaml = fs.readFileSync(path.join(repoRoot, 'plugins', PLUGIN, 'build.yaml'), 'utf8');
const scalar = (key) => (new RegExp(`^${key}:\\s*"?([^"\\r\\n]+)"?\\s*$`, 'm').exec(yaml) || [])[1];
const plugin = { name: scalar('name'), guid: scalar('guid'), version: scalar('version') };

// The sandbox's log: inside .sandbox, never under ProgramData\Jellyfin. The probe only reads it.
function assertInSandbox(p) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), path.resolve(p));
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(path.resolve(p))) throw new Error(`refusing to touch ${p}`);
    return path.resolve(p);
}
const logFolder = assertInSandbox(path.join(sandboxRoot, 'log'));

const checks = [];
const transcript = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin, webchecks: {} };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authHeader(token, deviceId = 'accessroles-je-probe') {
    return `MediaBrowser Client="accessroles-je-probe", Device="sandbox", DeviceId="${deviceId}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// A request as the admin (credentials.json), one of the probe's users (a token from its sign-in) or anonymous (null). A
// string body is sent as it is (JE's invalid bodies), an object as JSON.
const tokens = new Map();
function request(method, p, user = 'admin', body = null) {
    return new Promise((resolve, reject) => {
        const token = user === null ? null : user === 'admin' ? credentials.admin.token : tokens.get(user);
        if (user !== null && !token) throw new Error(`no token for ${user}`);
        const headers = { Authorization: authHeader(token, user && user !== 'admin' ? `jeprobe-${user.slice(0, 8)}` : undefined), Accept: 'application/json' };
        const payload = body === null ? null : Buffer.from(typeof body === 'string' ? body : JSON.stringify(body), 'utf8');
        if (payload) {
            headers['Content-Type'] = 'application/json';
            headers['Content-Length'] = payload.length;
        }
        const req = http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let json = null;
                try { json = text && /json/.test(res.headers['content-type'] || '') ? JSON.parse(text.replace(/^﻿/, '')) : null; } catch { json = null; }
                resolve({ status: res.statusCode, type: res.headers['content-type'] || '', text, json });
            });
        });
        req.on('error', reject);
        req.setTimeout(120000, () => req.destroy(new Error(`timeout ${method} ${p}`)));
        if (payload) req.write(payload);
        req.end();
    });
}

async function step(label, method, p, body = null, user = 'admin') {
    const r = await request(method, p, user, body);
    transcript.push({ step: label, curl: `curl -X ${method} "http://127.0.0.1:${port}${baseUrl}${p}" -H "Authorization: MediaBrowser Token=<${user === 'admin' ? 'admin' : 'user'}>"${body !== null ? ` -d '${(typeof body === 'string' ? body : JSON.stringify(body)).slice(0, 300)}'` : ''}`, status: r.status, code: r.json && r.json.Code ? r.json.Code : undefined });
    return r;
}

const idN = (x) => (x || '').replace(/-/g, '').toLowerCase();
const enc = encodeURIComponent;
const stable = (value) => JSON.stringify(value, (k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.keys(v).sort().map((key) => [key, v[key]])) : v));
const locked = (r) => r.status === 403 && !!r.json && r.json.type === LOCKED_TYPE;

// ---- the probe's own users ----------------------------------------------------------------------------------------
const own = new Map();
const secrets = new Map();
async function createOwnUser(name) {
    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);
    const password = crypto.randomBytes(24).toString('base64url');
    const r = await request('POST', '/Users/New', 'admin', { Name: name, Password: password });
    transcript.push({ step: `create the probe's user ${name}`, curl: `curl -X POST "http://127.0.0.1:${port}${baseUrl}/Users/New" -H "Authorization: MediaBrowser Token=<admin>" -d '{"Name":"${name}","Password":"<random>"}'`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.Id) throw new Error(`POST /Users/New ${name}: ${r.status}`);
    own.set(idN(r.json.Id), name);
    secrets.set(idN(r.json.Id), password);
    return r.json;
}

async function deleteOwnUser(id) {
    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);
    const r = await request('DELETE', `/Users/${id}`, 'admin');
    transcript.push({ step: `delete the probe's user ${own.get(idN(id))}`, status: r.status });
    if (r.status !== 204) throw new Error(`delete the probe's user ${id}: ${r.status}`);
    own.delete(idN(id));
    secrets.delete(idN(id));
    tokens.delete(idN(id));
}

async function signIn(user) {
    const password = secrets.get(idN(user.Id));
    if (!password) throw new Error(`no password for ${user.Name}`);
    const r = await request('POST', '/Users/AuthenticateByName', null, { Username: user.Name, Pw: password });
    transcript.push({ step: `${user.Name} signs in`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.AccessToken) throw new Error(`sign-in of ${user.Name}: ${r.status}`);
    tokens.set(idN(user.Id), r.json.AccessToken);
}

const as = (user) => (user === 'admin' ? 'admin' : idN(user.Id));

// ---- Access Roles' API -------------------------------------------------------------------------------------------------
const usersRows = async () => (await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || [];
const rowOf = async (id) => (await usersRows()).find((u) => idN(u.UserId) === idN(id)) || {};
const roles = async () => (await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || [];
const diagnostics = async () => (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).json || {};

async function createRole(name, locks, libraries) {
    const r = await step(`the probe's role ${name}`, 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}${name}`, Locks: locks || [], Libraries: libraries || { Mode: 'NotManaged', Refs: [] } });
    if (r.status !== 200 || !r.json || !r.json.Role) throw new Error(`create the role ${name}: ${r.status} ${r.text.slice(0, 200)}`);
    return r.json.Role;
}

async function assignRole(user, role) {
    const row = await rowOf(user.Id);
    const r = await step(`${user.Name} gets the role ${role.Name}`, 'PUT', `/AccessRoles/Admin/Users/${user.Id}`, { RoleIds: [role.Id], IfUpdatedAt: row.UpdatedUtc });
    if (r.status !== 200) throw new Error(`assign ${role.Name} to ${user.Name}: ${r.status} ${r.text.slice(0, 200)}`);
}

// ---- JE's settings: Hidden Content on for the probe, put back in the finally -------------------------------------------
let je = null;
let jeConfigBefore = null;
async function setJeConfig(label, changes) {
    const current = (await request('GET', `/Plugins/${je.Id}/Configuration`, 'admin')).json;
    if (!jeConfigBefore) jeConfigBefore = current;
    const r = await step(`Jellyfin Enhanced's settings: ${label}`, 'POST', `/Plugins/${je.Id}/Configuration`, { ...current, ...changes });
    if (r.status !== 204) throw new Error(`POST JE's settings: ${r.status}`);
}

async function restoreJeConfig() {
    if (!jeConfigBefore) return true;
    const r = await step('Jellyfin Enhanced\'s settings as they were', 'POST', `/Plugins/${je.Id}/Configuration`, jeConfigBefore);
    const now = (await request('GET', `/Plugins/${je.Id}/Configuration`, 'admin')).json;
    return r.status === 204 && stable(now) === stable(jeConfigBefore);
}

// ---- the server log ---------------------------------------------------------------------------------------------------
function logSince(sinceIso) {
    const since = Date.parse(sinceIso || '1970-01-01T00:00:00Z');
    const lines = [];
    for (const file of fs.readdirSync(logFolder).filter((f) => /^log_\d{8}\.log$/.test(f)).sort()) {
        for (const line of fs.readFileSync(path.join(logFolder, file), 'utf8').split(/\r?\n/)) {
            const m = /^\[(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2})\] \[(\w{3})\]/.exec(line);
            if (m && Date.parse(`${m[1]}T${m[2]}${m[3]}`) >= since - 1000) lines.push({ level: m[4], text: line });
        }
    }
    return lines;
}

// ---- the web checks ---------------------------------------------------------------------------------------------------
// Whether a webcheck's run failed in its first sign-in only (the login page's user cards, a known flake).
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

// ---- cleanup: everything of the probe's, through the API ----------------------------------------------------------------
const cleanupErrors = [];
let viewer = null;
let hidden = null;
async function cleanup(label) {
    if (hidden && hidden.kid) {
        const r = await step(`${label}: unhide the movie for the probe's kid`, 'POST', `/JellyfinEnhanced/admin/hidden-content/${idN(hidden.kid)}/unhide`, [idN(hidden.item)]);
        if (r.status < 200 || r.status >= 300) cleanupErrors.push(`${label}: unhide ${r.status}`);
        hidden = null;
    }
    for (const u of (await usersRows()).filter((x) => x.State !== 'NotManaged' && (x.Name.toLowerCase().startsWith(PROBE_USER) || (viewer && idN(x.UserId) === idN(viewer.Id))))) {
        await step(`${label}: unmanage ${u.Name} (Keep)`, 'POST', `/AccessRoles/Admin/Users/${u.UserId}/Unmanage`, { Choice: 'Keep' });
    }
    for (const r of (await roles()).filter((x) => x.Name.startsWith(PREFIX))) {
        await step(`${label}: delete ${r.Name}`, 'DELETE', `/AccessRoles/Admin/Roles/${r.Id}?onUnmanage=Keep`);
    }
    for (const u of ((await request('GET', '/Users', 'admin')).json || []).filter((x) => x.Name.toLowerCase().startsWith(PROBE_USER))) {
        own.set(idN(u.Id), u.Name);
        try {
            await deleteOwnUser(u.Id);
        } catch (e) {
            cleanupErrors.push(`${label}: ${e.message}`);
        }
    }
}

async function othersPolicies() {
    const users = ((await request('GET', '/Users', 'admin')).json || []).filter((u) => !u.Name.toLowerCase().startsWith(PROBE_USER));
    return Object.fromEntries(users.map((u) => [idN(u.Id), stable(u.Policy)]));
}

const moviesOf = async (parentId) => (((await request('GET', `/Items?ParentId=${parentId}&Recursive=true&IncludeItemTypes=Movie&EnableImages=false`, 'admin')).json || {}).Items) || [];
const itemsAs = async (user, query) => {
    const r = await request('GET', `/Items?${query}`, as(user));
    return { status: r.status, ids: (((r.json || {}).Items) || []).map((i) => idN(i.Id)) };
};

// =========================================================================================================================
async function main() {
    const installed = (await request('GET', '/Plugins', 'admin')).json || [];
    je = installed.find((p) => p.Name === JE_NAME);
    const bookmarks = installed.find((p) => p.Name === 'Bookmarks' && p.Status === 'Active');
    const d0 = await diagnostics();
    const table = (d0.RuleTable || {}).JellyfinEnhanced || {};
    evidence.start = { PluginVersion: d0.PluginVersion, je: je && { version: je.Version, status: je.Status }, bookmarks: bookmarks && bookmarks.Version, ruleTable: table };
    if (!check('Jellyfin Enhanced 12.14 is installed and active, Access Roles routes its saves (the rule table\'s self-check)', je && /^12\.14\./.test(je.Version) && je.Status === 'Active' && table.Installed === true && d0.PluginVersion === plugin.version, evidence.start)) return;
    const othersBefore = await othersPolicies();
    viewer = ((await request('GET', '/Users', 'admin')).json || []).find((u) => u.Name === 'viewer');
    if (opts.webcheck && !check('credentials.json\'s viewer is on the server and not managed (the webchecks give it the probe\'s lock-only role)', !!viewer && credentials.viewer && (await rowOf(viewer.Id)).State === 'NotManaged', viewer && (await rowOf(viewer.Id)).State)) return;

    // Two libraries with movies: two or more in the first.
    const libraries = ((await request('GET', '/AccessRoles/Admin/Libraries', 'admin')).json || []).filter((l) => l.State === 'Active');
    let libA = null; let libB = null; let moviesA = []; let moviesB = [];
    for (const lib of libraries) {
        const movies = await moviesOf(lib.Id);
        if (!libA && movies.length >= 2) { libA = lib; moviesA = movies; } else if (!libB && movies.length >= 1) { libB = lib; moviesB = movies; }
    }
    if (!check('the sandbox has a library with two movies and another with one', !!libA && !!libB, libraries.map((l) => l.Name))) return;

    const kid = await createOwnUser(`${PROBE_USER}kid`);
    const open = await createOwnUser(`${PROBE_USER}open`);
    const kidRole = await createRole('Kid', ['EnhancedPanel', 'QuickConnect'], { Mode: 'Only', Refs: [{ Id: libA.Id }] });
    await assignRole(kid, kidRole);
    await step('POST Sync: the kid gets its one library', 'POST', '/AccessRoles/Admin/Sync');
    await signIn(kid);
    await signIn(open);

    // ---- 1. JE's saves --------------------------------------------------------------------------------------------------
    const files = ['settings.json', 'shortcuts.json', 'elsewhere.json', 'hidden-content.json', 'spoilerblur.json', 'ddd-triggers.json'];
    const before = {};
    for (const f of files) before[f] = (await request('GET', `/JellyfinEnhanced/user-settings/${idN(kid.Id)}/${f}`, as(kid))).text;
    const saves = {};
    for (const who of [kid, open, 'admin']) {
        const name = who === 'admin' ? 'admin' : who === kid ? 'kid' : 'open';
        const target = who === 'admin' ? kid : who;
        const answers = {};
        for (const f of files) {
            // The kid's body is valid JSON (a write if it got through); the others' isn't, so JE refuses it and writes nothing.
            const r = await request('POST', `/JellyfinEnhanced/user-settings/${idN(target.Id)}/${f}`, as(who), who === kid ? '{}' : '{ not json');
            answers[f] = { status: r.status, locked: locked(r) };
        }
        const prefs = await request('POST', '/JellyfinEnhanced/spoiler-blur/user-prefs', as(who), who === kid ? '{}' : '{ not json');
        answers['spoiler-blur/user-prefs'] = { status: prefs.status, locked: locked(prefs) };
        saves[name] = answers;
    }
    transcript.push({ step: 'every JE settings save as the kid, the unlocked user and the administrator', status: Object.fromEntries(Object.entries(saves).map(([k, v]) => [k, Object.values(v).map((x) => x.status)])) });
    const variant = await request('POST', `/jellyfinenhanced/USER-SETTINGS/${kid.Id.toUpperCase().replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5')}/Settings.json`, as(kid), '{}');
    transcript.push({ step: 'the kid\'s settings save by another spelling of the route and its id (D form, upper case)', status: variant.status });
    const after = {};
    for (const f of files) after[f] = (await request('GET', `/JellyfinEnhanced/user-settings/${idN(kid.Id)}/${f}`, as(kid))).text;
    const unchanged = files.filter((f) => after[f] === before[f]);
    evidence.saves = { saves, variant: { status: variant.status, locked: locked(variant) }, unchanged };
    check('every JE settings save is refused for the user whose role locks EnhancedPanel (403 Locked: settings, shortcuts, Elsewhere, hidden content, spoiler blur, Ddd triggers and Spoiler Guard\'s preferences), also by another spelling, and its files read back as before; the unlocked user and the administrator get JE\'s own answers, never Locked',
        Object.values(saves.kid).every((x) => x.locked) && evidence.saves.variant.locked && unchanged.length === files.length
            && Object.values(saves.open).every((x) => !x.locked && x.status !== 403) && Object.values(saves.admin).every((x) => !x.locked), evidence.saves);

    // ---- 2. Bookmarks still work ------------------------------------------------------------------------------------------
    const [movie1, movie2] = moviesA;
    const label = `jeprobe ${crypto.randomBytes(4).toString('hex')}`;
    const add = await request('POST', `/JellyfinEnhanced/user-settings/${idN(kid.Id)}/bookmark.json/add`, as(kid), { ItemId: idN(movie2.Id), Name: movie2.Name, MediaType: 'movie', Timestamp: 30, Label: label });
    const read = (await request('GET', `/JellyfinEnhanced/user-settings/${idN(kid.Id)}/bookmark.json`, as(kid))).json || {};
    const list = read.bookmarks || read.Bookmarks || {};
    const entries = Array.isArray(list) ? list.map((b) => [b.id || b.Id, b]) : Object.entries(list);
    const mine = entries.find(([, b]) => (b.label || b.Label) === label);
    const removed = mine ? await request('DELETE', `/JellyfinEnhanced/user-settings/${idN(kid.Id)}/bookmark.json/${enc(mine[0])}`, as(kid)) : { status: null };
    const readAgain = JSON.stringify((await request('GET', `/JellyfinEnhanced/user-settings/${idN(kid.Id)}/bookmark.json`, as(kid))).json || {});
    transcript.push({ step: 'the kid adds, reads back and removes a JE bookmark', status: [add.status, !!mine, removed.status] });
    const ours = {};
    if (bookmarks) {
        const made = await request('POST', '/Bookmarks', as(kid), { ItemId: movie2.Id, StartTicks: 30_000_000, Name: label });
        const query = made.status === 201 ? (await request('GET', `/Bookmarks/Query?searchTerm=${enc(label)}&limit=10`, as(kid))).json : null;
        const gone = made.status === 201 ? await request('DELETE', `/Bookmarks/${made.json.Id}`, as(kid)) : { status: null };
        Object.assign(ours, { created: made.status, found: !!query && (query.Items || []).some((b) => b.Name === label), deleted: gone.status });
        transcript.push({ step: 'the kid creates, finds and deletes a bookmark of the Bookmarks plugin', status: [made.status, ours.found, gone.status] });
    }
    evidence.bookmarks = { je: { add: add.status, found: !!mine, removed: removed.status, gone: !readAgain.includes(label) }, ours: bookmarks ? ours : 'Bookmarks isn\'t installed in this sandbox' };
    check('bookmarks still work for the locked user: JE\'s added, read back and removed; Bookmarks\' own created, found and deleted when installed',
        add.status >= 200 && add.status < 300 && !!mine && removed.status >= 200 && removed.status < 300 && evidence.bookmarks.je.gone
            && (!bookmarks || (ours.created === 201 && ours.found && ours.deleted === 204)), evidence.bookmarks);

    // ---- 3. /Items: JE's Hidden Content and ours ----------------------------------------------------------------------
    await setJeConfig('HiddenContentEnabled on', { HiddenContentEnabled: true });
    const hideBody = [{ ItemId: idN(movie1.Id), Name: movie1.Name, Type: 'Movie', TmdbId: '', HiddenAt: new Date().toISOString(), PosterPath: '', SeriesId: '', SeriesName: '', SeasonNumber: null, EpisodeNumber: null, HideScope: 'global' }];
    const hide = await step('the administrator hides a movie of the kid\'s library for the kid (JE\'s admin API)', 'POST', `/JellyfinEnhanced/admin/hidden-content/${idN(kid.Id)}/hide`, hideBody);
    if (hide.status >= 200 && hide.status < 300) hidden = { kid: kid.Id, item: movie1.Id };
    const listing = 'Recursive=true&IncludeItemTypes=Movie&EnableImages=false';
    const kidAll = await itemsAs(kid, `userId=${idN(kid.Id)}&${listing}`);
    const kidIds = await itemsAs(kid, `userId=${idN(kid.Id)}&ids=${[movie1.Id, movie2.Id, moviesB[0].Id].map(idN).join(',')}&EnableImages=false`);
    const openAll = await itemsAs(open, `userId=${idN(open.Id)}&ids=${[movie1.Id, movie2.Id, moviesB[0].Id].map(idN).join(',')}&EnableImages=false`);
    const kidB = await request('GET', `/Items/${idN(moviesB[0].Id)}?userId=${idN(kid.Id)}`, as(kid));
    const has = (r, m) => r.ids.includes(idN(m.Id));
    evidence.items = {
        hide: hide.status, kidAll: { status: kidAll.status, movie1: has(kidAll, movie1), movie2: has(kidAll, movie2), movieB: has(kidAll, moviesB[0]), count: kidAll.ids.length },
        kidIds: { status: kidIds.status, movie1: has(kidIds, movie1), movie2: has(kidIds, movie2), movieB: has(kidIds, moviesB[0]) },
        openIds: { status: openAll.status, movie1: has(openAll, movie1), movie2: has(openAll, movie2), movieB: has(openAll, moviesB[0]) }, kidB: kidB.status
    };
    check('/Items with JE\'s filters and ours: the kid\'s listing has its library\'s other movie, not the movie hidden for it (JE\'s Hidden Content) nor the other library\'s (its role); its ids request not the other library\'s movie (LibraryHardening\'s ids check) and that movie\'s own page 404; the unmanaged user gets all three',
        hide.status >= 200 && hide.status < 300 && kidAll.status === 200 && !evidence.items.kidAll.movie1 && evidence.items.kidAll.movie2 && !evidence.items.kidAll.movieB
            && kidIds.status === 200 && evidence.items.kidIds.movie2 && !evidence.items.kidIds.movieB && kidB.status === 404
            && openAll.status === 200 && evidence.items.openIds.movie1 && evidence.items.openIds.movie2 && evidence.items.openIds.movieB, evidence.items);

    // ---- 4. The rule table ------------------------------------------------------------------------------------------------
    const t = ((await diagnostics()).RuleTable || {}).JellyfinEnhanced || {};
    evidence.ruleTable = t;
    const lockedList = t.Locked || t.LockedSaves || [];
    check('the rule table\'s self-check: JE\'s seven saves locked, none unreviewed', (Array.isArray(lockedList) ? lockedList.length : lockedList) === 7 && ((t.Unreviewed || []).length === 0), t);

    // ---- web ---------------------------------------------------------------------------------------------------------
    if (opts.webcheck) {
        const webRole = await createRole('web', WEB_LOCKS);
        await assignRole(viewer, webRole);
        for (const file of WEBCHECKS) {
            const { result, attempts } = await runWebcheck(file);
            const steps = ((result && result.steps) || []).map((s) => ({ name: s.name, ok: s.ok, error: s.ok ? undefined : s.error }));
            evidence.webchecks[file] = { ok: !!(result && result.ok), attempts, steps: steps.length, failed: steps.filter((s) => !s.ok), values: result && result.vars, exceptions: result && result.exceptions, failedRequests: result && result.failedRequests, screenshots: result && result.screenshots };
            check(`webcheck ${file} as viewer with Profile, Quick Connect and Jellyfin Enhanced locked: every step passed`, !!(result && result.ok), evidence.webchecks[file]);
        }
    }

    const othersAfter = await othersPolicies();
    const changed = Object.keys(othersBefore).filter((id) => othersAfter[id] !== othersBefore[id]);
    check('no other user\'s policy was written (viewer\'s among them: its role locks settings only)', changed.length === 0, { users: Object.keys(othersBefore).length, changed });
}

let failure = null;
const startedIso = new Date().toISOString();
try {
    await cleanup('before');
    await main();
} catch (e) {
    failure = e.stack || String(e);
} finally {
    const cleanupResult = {};
    try {
        await cleanup('after');
        cleanupResult.jeSettings = je ? await restoreJeConfig() : true;
        cleanupResult.probeLeft = {
            roles: (await roles()).filter((r) => r.Name.startsWith(PREFIX)).length,
            users: ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Name.toLowerCase().startsWith(PROBE_USER)).length,
            viewer: viewer ? (await rowOf(viewer.Id)).State : null
        };
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + (e.stack || String(e));
    }
    evidence.cleanup = cleanupResult;
    if (cleanupErrors.length) failure = (failure ? failure + ' | ' : '') + 'cleanup: ' + cleanupErrors.join('; ');
    if (!failure) {
        const left = cleanupResult.probeLeft || {};
        check('afterwards nothing of the probe\'s is left (its roles and users, viewer not managed) and JE\'s settings are as before', left.roles === 0 && left.users === 0 && (left.viewer === null || left.viewer === 'NotManaged') && cleanupResult.jeSettings === true, cleanupResult);
        const log = logSince(startedIso).filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text) && (l.level === 'ERR' || l.level === 'FTL'));
        check('the log has no error of Access Roles', log.length === 0, log.map((l) => l.text.slice(0, 240)));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
