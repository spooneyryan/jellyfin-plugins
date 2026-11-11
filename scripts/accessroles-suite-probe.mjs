#!/usr/bin/env node
// The rest of the suite next to Access Roles in the access sandbox, with the released builds of Bookmarks and Library
// Maintenance deployed next to it (scripts/README.md, this probe's section), and one more suite plugin's checks when that
// plugin is installed too (without it, the result's "skipped" says which part didn't run):
//   A. a narrow library role (the probe's kid: only Movies and the second movie library, --visible-library): every
//      endpoint of theirs that takes an item, scope or library id answers for a hidden library's item exactly as for an id
//      no item has (404 or an empty list), the word and count answers are the visible titles' (computed here from the
//      metadata), no response carries a hidden item's name, id or a word only hidden titles carry; the kid's own bookmark
//      and clip on a title it lost stay listed as "Unavailable" with what they stored and can't be cut again (the stated
//      exception of Bookmarks); every admin route of the plugins 403 for the kid;
//   B. no route of the plugins answers without a token but ClientScript and AdminScript;
//   C. an administrator in the same narrow role (the probe's admin2): Library Maintenance's index and duplicates cover its
//      libraries only and its Diagnostics hide the server-wide sources (the Scope line); the plugins' admin endpoints that
//      take item ids answer for a hidden item as for no item; stated exceptions recorded (Library Maintenance's Settings
//      list the server's libraries; Access Roles' own admin API) and findings beyond them reported;
//   D. Access Roles disabled (POST /Plugins/{id}/{version}/Disable and an in-process POST /System/Restart): the other
//      plugins' answers to the kid, an unmanaged user, admin2 and the administrator are the same as with it enabled, the
//      anonymous sweep the same, a bookmark can still be made; then enabled again (and restarted) with its roles intact;
//   E. admin2 made a non-admin by credentials.json's admin on jellyfin-web's stock Dashboard page (webcheck
//      accessroles-suite-demote.json; Access Roles has no demote action, by design): at the next request its token gets 403
//      from every admin route of the plugins and Capabilities.IsAdmin is false (a demotion applies at once).
// It writes only what is its own: its users (suiteprobe-..., random passwords and sign-in tokens kept in memory, never printed
// or saved), admin2's admin flag, its role ("Suite probe ...") and the assignments (Unmanage Keep in a finally), the kid's
// bookmarks and its one clip (deleted with the clip file in the finally), Access Roles' enabled state (enabled again in the
// finally if a run stops while it is disabled) and the two restarts. The admin sweeps send a write only after every read of
// the same caller was refused, and with a body that can't bind. Node 22 built-ins only. Loopback only, never port 8096.
//
//   node scripts/accessroles-suite-probe.mjs --sandbox access [--visible-library <name>] [--out result.json]
//
// Prints one JSON result (pass, checks, findings, transcript, evidence) and exits with 0 when every check passed.

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
const PREFIX = 'Suite probe ';
const PROBE_USER = 'suiteprobe-';
// The suite plugins next to Access Roles. The optional one's part runs only when it is installed (main() decides).
const OPTIONAL = 'Tag Cloud';
let SUITE = ['Bookmarks', OPTIONAL, 'Library Maintenance'];
let optional = true;
const skipped = [];
// "A, B and C" (or "A, B or C"), and a count in words, for the checks' names.
const listOf = (names, last = 'and') => (names.length < 2 ? names.join('') : `${names.slice(0, -1).join(', ')} ${last} ${names[names.length - 1]}`);
const inWords = (n) => ['no', 'one', 'two', 'three', 'four', 'five'][n] || String(n);
const ROUTES = /^\/(AccessRoles|Bookmarks|TagCloud|LibraryMaintenance)\//;
const OPEN = /\/(ClientScript|AdminScript)$/;
const DEMOTE_WEBCHECK = 'accessroles-suite-demote.json';
const TICKS = 10_000_000;

function usage() {
    return 'usage: node scripts/accessroles-suite-probe.mjs --sandbox <name> [--visible-library <name>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, visibleLibrary: 'Visible Movies' };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--visible-library') o.visibleLibrary = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!o.visibleLibrary || o.visibleLibrary === 'Movies' || o.visibleLibrary === 'Movies 2') throw new Error(`--visible-library names a movie library besides Movies and Movies 2. ${usage()}`);
    return o;
}

const opts = parseArgs(process.argv.slice(2));
// The kid's role keeps Movies and a second movie library with a few titles; every other library is hidden from it.
const ALLOWED_LIBRARIES = ['Movies', opts.visibleLibrary];
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
const plugin = { name: scalar('name'), guid: scalar('guid') };

// The sandbox's log: inside .sandbox, never under ProgramData\Jellyfin. The probe only reads it.
function assertInSandbox(p) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), path.resolve(p));
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(path.resolve(p))) throw new Error(`refusing to touch ${p}`);
    return path.resolve(p);
}
const logFolder = assertInSandbox(path.join(sandboxRoot, 'log'));

const checks = [];
const findings = [];
const transcript = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authHeader(token, deviceId = 'accessroles-suite-probe') {
    return `MediaBrowser Client="accessroles-suite-probe", Device="sandbox", DeviceId="${deviceId}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// A request as the admin (credentials.json), one of the probe's users (a token from its sign-in) or anonymous (null). A
// string body is sent as it is (the sweeps' unbindable bodies), an object as JSON.
const tokens = new Map();
function request(method, p, user = 'admin', body = null) {
    return new Promise((resolve, reject) => {
        const token = user === null ? null : user === 'admin' ? credentials.admin.token : tokens.get(user);
        if (user !== null && !token) throw new Error(`no token for ${user}`);
        const headers = { Authorization: authHeader(token, user && user !== 'admin' ? `suiteprobe-${user.slice(0, 8)}` : undefined), Accept: 'application/json' };
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
    transcript.push({ step: label, curl: `curl -X ${method} "http://127.0.0.1:${port}${baseUrl}${p}" -H "Authorization: MediaBrowser Token=<${user === 'admin' ? 'admin' : user === null ? 'none' : 'user'}>"${body !== null ? ` -d '${(typeof body === 'string' ? body : JSON.stringify(body)).slice(0, 300)}'` : ''}`, status: r.status, code: r.json && r.json.Code ? r.json.Code : undefined });
    return r;
}

const idN = (x) => (x || '').replace(/-/g, '').toLowerCase();
const idD = (x) => idN(x).replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5');
const enc = encodeURIComponent;
const stable = (value) => JSON.stringify(value, (k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.keys(v).sort().map((key) => [key, v[key]])) : v));
const randomId = () => crypto.randomBytes(16).toString('hex');

async function waitUntil(label, fn, timeoutMs = 180000, everyMs = 1000) {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
        try {
            const v = await fn();
            if (v) return v;
        } catch (e) {
            last = e.message;
        }
        await delay(everyMs);
    }
    throw new Error(`timed out waiting for ${label}${last ? ` (${last})` : ''}`);
}

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

const policyOf = async (id) => ((await request('GET', `/Users/${id}`, 'admin')).json || {}).Policy || {};
const access = (p) => ({ admin: p.IsAdministrator === true, all: p.EnableAllFolders === true, folders: p.EnableAllFolders ? [] : (p.EnabledFolders || []).map(idN).sort() });

async function postOwnPolicy(id, changes, label) {
    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);
    const policy = await policyOf(id);
    const r = await request('POST', `/Users/${id}/Policy`, 'admin', { ...policy, ...changes });
    transcript.push({ step: label, status: r.status });
    if (r.status !== 204) throw new Error(`policy of ${id}: ${r.status}`);
}

async function signIn(user) {
    const password = secrets.get(idN(user.Id));
    if (!password) throw new Error(`no password for ${user.Name}`);
    const r = await request('POST', '/Users/AuthenticateByName', null, { Username: user.Name, Pw: password });
    transcript.push({ step: `${user.Name} signs in`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.AccessToken) throw new Error(`sign-in of ${user.Name}: ${r.status}`);
    tokens.set(idN(user.Id), r.json.AccessToken);
}

const as = (user) => (user === 'admin' ? 'admin' : user === null ? null : idN(user.Id));

// ---- Access Roles' API -------------------------------------------------------------------------------------------------
const usersRows = async () => (await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || [];
const rowOf = async (id) => (await usersRows()).find((u) => idN(u.UserId) === idN(id)) || {};
const roles = async () => (await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || [];

async function createRole(name, libraries) {
    const r = await step(`the probe's role ${name}`, 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}${name}`, Locks: [], Libraries: libraries });
    if (r.status !== 200 || !r.json || !r.json.Role) throw new Error(`create the role ${name}: ${r.status} ${r.text.slice(0, 200)}`);
    return r.json.Role;
}

async function assignRole(user, role) {
    const row = await rowOf(user.Id);
    const r = await step(`${user.Name} gets the role ${role.Name}`, 'PUT', `/AccessRoles/Admin/Users/${user.Id}`, { RoleIds: [role.Id], IfUpdatedAt: row.UpdatedUtc });
    if (r.status !== 200) throw new Error(`assign ${role.Name} to ${user.Name}: ${r.status} ${r.text.slice(0, 200)}`);
}

// ---- what the library holds, as the administrator sees it (the expected answers come from here, not from the plugins) ---
async function libraryItems(lib) {
    const r = await request('GET', `/Items?ParentId=${lib.Id}&Recursive=true&IncludeItemTypes=Movie,Series&Fields=Genres,Studios,People,Tags&EnableImages=false`, 'admin');
    return ((r.json || {}).Items || []).map((i) => ({ id: idN(i.Id), name: i.Name, type: i.Type, lib: idN(lib.Id),
        words: { Genres: i.Genres || [], Studios: (i.Studios || []).map((s) => s.Name), Tags: i.Tags || [], Actors: (i.People || []).filter((p) => p.Type === 'Actor').map((p) => p.Name) } }));
}

function expectedWords(items, source) {
    const counts = new Map();
    for (const it of items) for (const w of new Set(it.words[source].map((x) => x.toLowerCase()))) counts.set(w, (counts.get(w) || 0) + 1);
    return Object.fromEntries([...counts.entries()].sort(([a], [b]) => a.localeCompare(b)));
}

// What a response may never carry for the caller: the hidden items' ids (both forms) and names and the words only hidden
// titles carry, and the hidden libraries' ids and names, each only when no visible thing has it as part of its own name.
let needles = [];
function buildNeedles(visible, hidden, visibleLibs, hiddenLibs) {
    const visibleText = [...visible.map((i) => i.name), ...visible.flatMap((i) => Object.values(i.words).flat()), ...visibleLibs.map((l) => l.Name)].map((s) => s.toLowerCase());
    const textNeedle = (s) => !!s && s.length >= 4 && !visibleText.some((v) => v.includes(s.toLowerCase()));
    const out = new Map();
    for (const it of hidden) {
        out.set(it.id, `id of ${it.name}`);
        out.set(idD(it.id), `id of ${it.name}`);
        if (textNeedle(it.name)) out.set(it.name.toLowerCase(), `name ${it.name}`);
        for (const w of Object.values(it.words).flat()) if (textNeedle(w)) out.set(w.toLowerCase(), `word ${w}`);
    }
    for (const l of hiddenLibs) {
        out.set(idN(l.Id), `library id of ${l.Name}`);
        out.set(idD(l.Id), `library id of ${l.Name}`);
        // A one-word name ("Shows") is also an option's name in the plugins' answers: only names of two words or more.
        if (/\s/.test(l.Name.trim()) && textNeedle(l.Name)) out.set(l.Name.toLowerCase(), `library ${l.Name}`);
    }
    needles = [...out.entries()].map(([needle, what]) => ({ needle, what }));
}

const leaksIn = (text, allowed = []) => {
    const t = (text || '').toLowerCase();
    return needles.filter((n) => !allowed.includes(n.needle) && t.includes(n.needle)).map((n) => n.what);
};

// Every answer the scan saw, by caller: [{ label, status, leaks }].
const scanned = { kid: [], admin2: [], 'admin2-clips': [], anonymous: [] };
async function scan(who, label, method, p, user, body = null, allowed = []) {
    const r = await request(method, p, user, body);
    scanned[who].push({ label, status: r.status, leaks: leaksIn(r.text, allowed) });
    return r;
}

// The answer for a hidden id and for an id no item has, with the id replaced and trace ids dropped: they must be equal.
const normalizeFor = (text, ids) => {
    let t = (text || '').replace(/"traceId"\s*:\s*"[^"]*"/g, '"traceId":"-"');
    for (const id of ids) for (const form of [idN(id), idD(id)]) if (form) t = t.split(form).join('<id>').split(form.toUpperCase()).join('<id>');
    return t;
};
async function samePair(who, label, method, make, user, hiddenId, makeBody = null) {
    const fake = randomId();
    const a = await scan(who, `${label} (hidden)`, method, make(hiddenId), user, makeBody ? makeBody(hiddenId) : null);
    const b = await request(method, make(fake), user, makeBody ? makeBody(fake) : null);
    const same = a.status === b.status && normalizeFor(a.text, [hiddenId]) === normalizeFor(b.text, [fake]);
    return { label, hidden: a.status, none: b.status, same, body: same ? undefined : { hidden: a.text.slice(0, 300), none: b.text.slice(0, 300) } };
}

// ---- the routes of the plugins, from the server's own OpenAPI document -------------------------------------------------
let routes = [];
async function loadRoutes() {
    const doc = (await request('GET', '/api-docs/openapi.json', 'admin')).json || { paths: {} };
    routes = [];
    for (const [p, ops] of Object.entries(doc.paths || {})) {
        if (!ROUTES.test(p)) continue;
        for (const [m, op] of Object.entries(ops)) {
            if (!['get', 'post', 'put', 'delete'].includes(m)) continue;
            const params = op.parameters || [];
            const concrete = p.replace(/\{([^}]+)\}/g, (all, name) => {
                const s = (params.find((x) => x.name === name && x.in === 'path') || {}).schema || {};
                return s.format === 'uuid' ? randomId() : s.type === 'integer' ? '999999' : 'suiteprobe-none';
            });
            routes.push({ method: m.toUpperCase(), template: p, path: concrete, body: !!op.requestBody, open: OPEN.test(p), admin: /\/Admin\//.test(p) || /^\/LibraryMaintenance\/Duplicates/.test(p) });
        }
    }
    return routes;
}

// A sweep: every read first; the writes only when every read was refused with the expected status (so a caller who got
// through a read never gets to send a write), each write with a body that can't bind.
async function sweep(who, user, list, expected) {
    const out = [];
    const reads = list.filter((r) => r.method === 'GET');
    const writes = list.filter((r) => r.method !== 'GET');
    for (const r of reads) {
        const a = await request('GET', r.path, user);
        out.push({ route: `${r.method} ${r.template}`, status: a.status, ok: a.status === (r.open ? 200 : expected), leaks: leaksIn(a.text) });
    }
    const readsRefused = out.every((x) => x.ok);
    if (readsRefused) {
        for (const r of writes) {
            const method = r.method;
            const a = await request(method, r.path, user, r.body ? '{ not json' : null);
            out.push({ route: `${r.method} ${r.template}`, status: a.status, ok: a.status === expected, leaks: leaksIn(a.text) });
        }
    }
    transcript.push({ step: `sweep as ${who}: ${list.length} routes`, status: out.reduce((m, x) => ({ ...m, [x.status]: (m[x.status] || 0) + 1 }), {}) });
    return { routes: list.length, readsRefused, writesSent: readsRefused ? writes.length : 0, wrong: out.filter((x) => !x.ok), leaks: out.filter((x) => x.leaks.length) };
}

// ---- restarts ----------------------------------------------------------------------------------------------------------
async function plugins() {
    return (await request('GET', '/Plugins', 'admin')).json || [];
}

async function arAnswers() {
    try {
        return (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).status;
    } catch {
        return null;
    }
}

async function indexesReady() {
    if (optional) await waitUntil('Tag Cloud\'s index', async () => ((await request('GET', '/TagCloud/Admin/Status', 'admin')).json || {}).State === 'Ready', 180000, 2000);
    await waitUntil('Library Maintenance\'s duplicates index', async () => (await request('GET', '/LibraryMaintenance/Duplicates/Groups', 'admin')).status === 200, 180000, 2000);
}

let arDisabled = false;
async function setAccessRoles(enabled) {
    const ar = (await plugins()).find((p) => idN(p.Id) === idN(plugin.guid));
    if (!ar) throw new Error('Access Roles is not in /Plugins');
    const r = await step(`${enabled ? 'enable' : 'disable'} Access Roles`, 'POST', `/Plugins/${idN(ar.Id)}/${enc(ar.Version)}/${enabled ? 'Enable' : 'Disable'}`);
    if (r.status !== 204) throw new Error(`${enabled ? 'enable' : 'disable'} Access Roles: ${r.status}`);
    if (!enabled) arDisabled = true;
    const started = Date.now();
    const restart = await step('restart the sandbox (in-process)', 'POST', '/System/Restart');
    if (restart.status !== 204) throw new Error(`POST /System/Restart: ${restart.status}`);
    // The old host keeps answering for a moment: wait until Access Roles' routes are gone (disabled) or back (enabled).
    await waitUntil(`the restart with Access Roles ${enabled ? 'enabled' : 'disabled'}`, async () => (await arAnswers()) === (enabled ? 200 : 404), 240000, 2000);
    await indexesReady();
    if (enabled) arDisabled = false;
    const now = (await plugins()).find((p) => idN(p.Id) === idN(plugin.guid));
    return { seconds: Math.round((Date.now() - started) / 1000), status: now && now.Status };
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

// ---- the web check ----------------------------------------------------------------------------------------------------
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
const kidBookmarks = new Set();
let kid = null;
async function cleanup(label) {
    if (arDisabled) {
        try {
            evidence.cleanupEnable = await setAccessRoles(true);
        } catch (e) {
            cleanupErrors.push(`${label}: enable Access Roles again: ${e.message}`);
            return;
        }
    }
    // Every bookmark of the kid is the probe's (the user is): each deleted with its clip file, by the kid.
    if (kid && tokens.has(idN(kid.Id))) {
        const list = ((await request('GET', '/Bookmarks/Query?Limit=200', as(kid))).json || {}).Items || [];
        for (const id of new Set([...kidBookmarks, ...list.map((b) => b.Id)])) {
            const r = await step(`${label}: the kid deletes its bookmark with its clip`, 'DELETE', `/Bookmarks/${id}?deleteClip=true`, null, as(kid));
            if (r.status !== 204 && r.status !== 404) cleanupErrors.push(`${label}: delete bookmark ${id}: ${r.status}`);
        }
        kidBookmarks.clear();
    }
    for (const u of (await usersRows()).filter((x) => x.State !== 'NotManaged' && x.Name.toLowerCase().startsWith(PROBE_USER))) {
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

// The answers compared with Access Roles enabled and disabled, without the fields a restart changes (index generations,
// build times and versions, trace ids).
const VOLATILE = new Set(['Generation', 'IndexedUtc', 'GeneratedUtc', 'BuildMs', 'Index', 'Version', 'BuiltDuringScan', 'Dirty', 'Rebuilding', 'ScanInProgress', 'Progress', 'traceId', 'PageUsedUtc']);
const steady = (r) => ({ status: r.status, body: r.json === null ? r.text : JSON.parse(JSON.stringify(r.json, (k, v) => (VOLATILE.has(k) ? undefined : v))) });
async function answers(set) {
    const out = {};
    for (const [label, p, user] of set) out[label] = steady(await request('GET', p, user));
    return out;
}

// =========================================================================================================================
async function main() {
    const installed = await plugins();
    const ar = installed.find((p) => idN(p.Id) === idN(plugin.guid));
    optional = installed.some((p) => p.Name === OPTIONAL);
    if (!optional) {
        SUITE = SUITE.filter((name) => name !== OPTIONAL);
        skipped.push(`${OPTIONAL} is not installed in this sandbox: its checks (its answers to the kid in A, its capabilities in C and E, its answers in D) are skipped`);
        console.error(`skipped: ${skipped[skipped.length - 1]}`);
    }
    const suite = SUITE.map((name) => installed.find((p) => p.Name === name));
    evidence.plugins = installed.filter((p) => p.Name === plugin.name || SUITE.includes(p.Name)).map((p) => ({ name: p.Name, version: p.Version, status: p.Status }));
    if (!check(`Access Roles and the released ${listOf(SUITE)} are installed and active`, ar && ar.Status === 'Active' && suite.every((p) => p && p.Status === 'Active'), evidence.plugins)) return;
    await indexesReady();
    const othersBefore = await othersPolicies();

    // The libraries: the kid's role keeps Movies and the second movie library (--visible-library); every other library is hidden from it.
    const libraries = ((await request('GET', '/AccessRoles/Admin/Libraries', 'admin')).json || []).filter((l) => l.State === 'Active');
    const allowedLibs = libraries.filter((l) => ALLOWED_LIBRARIES.includes(l.Name));
    const hiddenLibs = libraries.filter((l) => !ALLOWED_LIBRARIES.includes(l.Name));
    const movies2 = hiddenLibs.find((l) => l.Name === 'Movies 2');
    if (!check(`the sandbox has Movies, Movies 2 and ${opts.visibleLibrary}`, allowedLibs.length === 2 && !!movies2, libraries.map((l) => l.Name))) return;
    const visible = (await Promise.all(allowedLibs.map(libraryItems))).flat();
    const hidden = (await Promise.all(hiddenLibs.map(libraryItems))).flat();
    buildNeedles(visible, hidden, allowedLibs, hiddenLibs);
    const visibleMovies = visible.filter((i) => i.type === 'Movie');
    const hiddenMovies = hidden.filter((i) => i.type === 'Movie');
    const clipMovie = hiddenMovies.find((i) => i.lib === idN(movies2.Id) && i.name.toLowerCase().startsWith('amelie')) || hiddenMovies.find((i) => i.lib === idN(movies2.Id));
    const pairMovie = hiddenMovies.find((i) => i !== clipMovie && /hidden/i.test(i.name)) || hiddenMovies.find((i) => i !== clipMovie);
    const visibleMovie = visibleMovies[0];
    evidence.libraries = { allowed: allowedLibs.map((l) => l.Name), hidden: hiddenLibs.map((l) => l.Name), visibleTitles: visible.length, hiddenTitles: hidden.length, needles: needles.length, clipMovie: clipMovie && clipMovie.name, pairMovie: pairMovie && pairMovie.name };

    // ---- the probe's users --------------------------------------------------------------------------------------------
    kid = await createOwnUser(`${PROBE_USER}kid`);
    const open = await createOwnUser(`${PROBE_USER}open`);
    const admin2 = await createOwnUser(`${PROBE_USER}admin2`);
    await postOwnPolicy(admin2.Id, { IsAdministrator: true }, 'make the probe\'s admin2 an administrator');
    for (const u of [kid, open, admin2]) await signIn(u);

    // Before its role: the kid bookmarks a Movies 2 movie and cuts a clip of it (both its own, kept when it loses the title).
    const made = await step('the kid bookmarks a Movies 2 movie (a range, before its role)', 'POST', '/Bookmarks', { ItemId: clipMovie.id, StartTicks: 2 * TICKS, EndTicks: 6 * TICKS, Name: 'suiteprobe clip' }, as(kid));
    if (made.status !== 201 || !made.json) throw new Error(`the kid's bookmark: ${made.status} ${made.text.slice(0, 200)}`);
    const ownBookmark = made.json;
    kidBookmarks.add(ownBookmark.Id);
    const clip = await step('the kid cuts a clip of it (Fast)', 'POST', `/Bookmarks/${ownBookmark.Id}/Clip`, { Mode: 'Fast' }, as(kid));
    if (clip.status !== 202) throw new Error(`the kid's clip: ${clip.status} ${clip.text.slice(0, 200)}`);
    const clipped = await waitUntil('the kid\'s clip', async () => {
        const read = (await request('GET', `/Bookmarks/${ownBookmark.Id}`, as(kid))).json;
        const status = read && read.Clip && read.Clip.Status;
        if (status && !['Queued', 'Running', 'Completed'].includes(status)) throw new Error(`the clip ended ${status}`);
        return status === 'Completed' ? read : null;
    }, 120000, 1000);
    const ownName = (clipped.ItemName || clipMovie.name).toLowerCase();
    const visibleBm = await step('the kid bookmarks a visible movie', 'POST', '/Bookmarks', { ItemId: visibleMovie.id, StartTicks: 5 * TICKS, Name: 'suiteprobe visible' }, as(kid));
    if (visibleBm.status === 201 && visibleBm.json) kidBookmarks.add(visibleBm.json.Id);

    // ---- the narrow role ------------------------------------------------------------------------------------------------
    const narrow = await createRole('Narrow', { Mode: 'Only', Refs: allowedLibs.map((l) => ({ Id: l.Id })) });
    await assignRole(kid, narrow);
    await assignRole(admin2, narrow);
    await step('POST Sync: the kid and admin2 get the two libraries', 'POST', '/AccessRoles/Admin/Sync');
    const allowedIds = allowedLibs.map((l) => idN(l.Id)).sort();
    const nativeKid = access(await policyOf(kid.Id));
    const nativeAdmin2 = access(await policyOf(admin2.Id));
    evidence.native = { kid: nativeKid, admin2: nativeAdmin2 };
    if (!check('the role is applied natively: the kid and admin2 (still an administrator) have only the two libraries', !nativeKid.all && stable(nativeKid.folders) === stable(allowedIds) && nativeAdmin2.admin && !nativeAdmin2.all && stable(nativeAdmin2.folders) === stable(allowedIds), evidence.native)) return;

    await loadRoutes();
    evidence.routes = { total: routes.length, open: routes.filter((r) => r.open).map((r) => `${r.method} ${r.template}`), admin: routes.filter((r) => r.admin).length };

    // ---- A. the kid under the narrow role ------------------------------------------------------------------------------
    const bm = {};
    bm.pairs = [
        await samePair('kid', 'POST /Bookmarks on a hidden title', 'POST', () => '/Bookmarks', as(kid), pairMovie.id, (id) => ({ ItemId: id, StartTicks: TICKS, Name: 'suiteprobe pair' })),
        await samePair('kid', 'GET /Bookmarks/Query?ItemId', 'GET', (id) => `/Bookmarks/Query?ItemId=${id}`, as(kid), pairMovie.id),
        await samePair('kid', 'GET /Bookmarks/Groups?ItemId', 'GET', (id) => `/Bookmarks/Groups?ItemId=${id}`, as(kid), pairMovie.id),
        await samePair('kid', 'GET /Bookmarks/Clips?ItemId', 'GET', (id) => `/Bookmarks/Clips?ItemId=${id}`, as(kid), pairMovie.id)
    ];
    const allowOwn = [clipMovie.id, idD(clipMovie.id), ownName];
    const query = await scan('kid', 'GET /Bookmarks/Query', 'GET', '/Bookmarks/Query', as(kid), null, allowOwn);
    const groups = await scan('kid', 'GET /Bookmarks/Groups', 'GET', '/Bookmarks/Groups', as(kid), null, allowOwn);
    const clips = await scan('kid', 'GET /Bookmarks/Clips', 'GET', '/Bookmarks/Clips', as(kid), null, allowOwn);
    const one = await scan('kid', 'GET /Bookmarks/{id} (its own, on the lost title)', 'GET', `/Bookmarks/${ownBookmark.Id}`, as(kid), null, allowOwn);
    const remake = await step('the kid asks for its clip again (the title is lost: refused)', 'POST', `/Bookmarks/${ownBookmark.Id}/Clip`, { Mode: 'Fast' }, as(kid));
    const ownRow = ((query.json || {}).Items || []).find((b) => b.Id === ownBookmark.Id) || {};
    const visibleRow = ((query.json || {}).Items || []).find((b) => visibleBm.json && b.Id === visibleBm.json.Id) || {};
    const ownClip = ((clips.json || {}).Items || []).find((c) => c.Id === ownBookmark.Id || c.BookmarkId === ownBookmark.Id) || {};
    bm.own = { listed: !!ownRow.Id, itemAvailable: ownRow.ItemAvailable, keys: Object.keys(ownRow).filter((k) => /image|library|path/i.test(k)), one: one.status, oneAvailable: one.json && one.json.ItemAvailable, clipListed: !!ownClip.Id || ((clips.json || {}).Items || []).length > 0, clipAvailable: ownClip.ItemAvailable, groups: groups.status, remake: { status: remake.status, code: remake.json && (remake.json.Code || remake.json.code || remake.json.title) } };
    bm.visibleAvailable = visibleRow.ItemAvailable;
    bm.capabilities = ((await scan('kid', 'GET /Bookmarks/Capabilities', 'GET', '/Bookmarks/Capabilities', as(kid))).json || {}).IsAdmin;
    evidence.kidBookmarks = bm;
    check('Bookmarks under the narrow role: a hidden title\'s id answers exactly as an id no item has (create 404, Query, Groups and Clips by ItemId empty)',
        bm.pairs.every((x) => x.same) && bm.pairs[0].hidden === 404 && bm.pairs.slice(1).every((x) => x.hidden === 200 || x.hidden === 404), bm.pairs);
    check('Bookmarks\' stated exception: the kid\'s own bookmark and clip on the title it lost stay listed as Unavailable, with no image, library or path field, and the clip can\'t be cut again (4xx); its bookmark on a visible title is available',
        bm.own.listed && bm.own.itemAvailable === false && bm.own.one === 200 && bm.own.keys.every((k) => ownRow[k] === null || ownRow[k] === undefined || (Array.isArray(ownRow[k]) && ownRow[k].length === 0)) && bm.own.clipListed && bm.own.clipAvailable !== true
            && remake.status >= 400 && remake.status < 500 && bm.visibleAvailable === true && bm.capabilities === false, { ...bm.own, visibleAvailable: bm.visibleAvailable, ownRow: Object.keys(ownRow) });

    // The optional plugin, when it is installed: the libraries, the words and their counts are the visible titles'.
    let hiddenGenre = null;
    let visibleGenre = null;
    if (optional) {
        const tc = { words: {}, expected: {}, pairs: [] };
        const tcLibs = (await scan('kid', 'GET /TagCloud/Libraries?scope=Both', 'GET', '/TagCloud/Libraries?scope=Both', as(kid))).json || [];
        const expectLibs = allowedLibs.map((l) => ({ id: idN(l.Id), count: visible.filter((i) => i.lib === idN(l.Id)).length })).filter((l) => l.count > 0).sort((a, b) => a.id.localeCompare(b.id));
        tc.libraries = { got: tcLibs.map((l) => ({ id: idN(l.Id), count: l.TitleCount })).sort((a, b) => a.id.localeCompare(b.id)), expected: expectLibs };
        for (const source of ['Genres', 'Studios', 'Actors', 'Tags']) {
            const r = await scan('kid', `GET /TagCloud/Words?source=${source}`, 'GET', `/TagCloud/Words?source=${source}&scope=Movies&limit=200`, as(kid));
            const got = Object.fromEntries(((r.json || {}).Words || []).map((w) => [w.Key.toLowerCase(), w.Count]).sort(([a], [b]) => a.localeCompare(b)));
            tc.words[source] = { status: r.status, got, titleCount: (r.json || {}).TitleCount };
            tc.expected[source] = expectedWords(visibleMovies, source);
        }
        const hiddenOnly = (source) => Object.keys(expectedWords(hiddenMovies, source)).find((k) => !(k in expectedWords(visibleMovies, source)));
        hiddenGenre = hiddenOnly('Genres');
        visibleGenre = Object.keys(tc.expected.Genres)[0];
        tc.pairs.push(await samePair('kid', 'Words?libraryIds=<a hidden library>', 'GET', (id) => `/TagCloud/Words?source=Genres&scope=Movies&libraryIds=${id}`, as(kid), movies2.Id));
        tc.pairs.push(await samePair('kid', 'Titles?libraryIds=<a hidden library>', 'GET', (id) => `/TagCloud/Titles?source=Genres&key=${enc(visibleGenre)}&scope=Movies&libraryIds=${id}`, as(kid), movies2.Id));
        const fakeKey = `suiteprobe${crypto.randomBytes(4).toString('hex')}`;
        const keyPair = async (label, make) => {
            const a = await scan('kid', `${label} (hidden word)`, 'GET', make(hiddenGenre), as(kid), null, [hiddenGenre]);
            const b = await request('GET', make(fakeKey), as(kid));
            const same = a.status === b.status && a.text.split(hiddenGenre).join('<k>') === b.text.split(fakeKey).join('<k>');
            return { label, hidden: a.status, none: b.status, same, body: same ? undefined : { hidden: a.text.slice(0, 300), none: b.text.slice(0, 300) } };
        };
        tc.pairs.push(await keyPair('Titles?key=<a word only hidden titles carry>', (k) => `/TagCloud/Titles?source=Genres&key=${enc(k)}&scope=Movies`));
        tc.pairs.push(await keyPair('Words?narrow=<a word only hidden titles carry>', (k) => `/TagCloud/Words?source=Studios&scope=Movies&narrow=${enc(k)}`));
        const titles = (await scan('kid', 'GET /TagCloud/Titles (a visible word)', 'GET', `/TagCloud/Titles?source=Genres&key=${enc(visibleGenre)}&scope=Movies&limit=200`, as(kid))).json || {};
        tc.titles = { key: visibleGenre, ids: (titles.Items || []).map((i) => idN(i.Id)), allVisible: (titles.Items || []).every((i) => visible.some((v) => v.id === idN(i.Id))), total: titles.Total };
        await scan('kid', 'GET /TagCloud/Clouds', 'GET', '/TagCloud/Clouds', as(kid));
        tc.capabilities = ((await scan('kid', 'GET /TagCloud/Capabilities', 'GET', '/TagCloud/Capabilities', as(kid))).json || {}).IsAdmin;
        evidence.kidTagCloud = { ...tc, hiddenGenre };
        check('Tag Cloud under the narrow role: the libraries are the two with their visible titles\' counts, every cloud\'s words and counts are the visible titles\' (from the metadata), the title count is the visible movies\'',
            stable(tc.libraries.got) === stable(tc.libraries.expected) && Object.keys(tc.words).every((s) => tc.words[s].status === 200 && stable(tc.words[s].got) === stable(tc.expected[s]))
                && tc.words.Genres.titleCount === visibleMovies.length && tc.capabilities === false, { libraries: tc.libraries, words: tc.words, expected: tc.expected, visibleMovies: visibleMovies.length });
        check('Tag Cloud: a hidden library\'s id and a word only hidden titles carry answer exactly as an id or a word nobody has (Words and Titles by libraryIds, Titles by key, Words narrowed); a visible word\'s titles are all visible',
            !!hiddenGenre && tc.pairs.every((x) => x.same) && tc.titles.allVisible && tc.titles.total === tc.expected.Genres[visibleGenre], { pairs: tc.pairs, titles: tc.titles });
    }

    // Every admin route of the plugins: 403 for the kid.
    const kidSweep = await sweep('kid', as(kid), routes.filter((r) => r.admin && !r.open), 403);
    evidence.kidSweep = kidSweep;
    check(`every admin route of ${listOf(['Access Roles', ...SUITE])} answers 403 to the kid (reads, then writes with a body that can't bind)`, kidSweep.readsRefused && kidSweep.wrong.length === 0 && kidSweep.writesSent > 0, kidSweep);
    const kidLeaks = scanned.kid.filter((x) => x.leaks.length).concat(kidSweep.leaks);
    check('no answer to the kid carries a hidden title\'s name or id, a word only hidden titles carry or a hidden library (its own lost bookmark\'s stored id and name excepted)', kidLeaks.length === 0, { scanned: scanned.kid.length, leaks: kidLeaks });

    // ---- B. without a token ------------------------------------------------------------------------------------------
    const anon = await sweep('none', null, routes, 401);
    evidence.anonymous = anon;
    check(`no route of the ${inWords(SUITE.length + 1)} plugins answers without a token (${routes.length} routes) but ClientScript and AdminScript (200, nothing hidden in them)`, anon.readsRefused && anon.wrong.length === 0 && anon.leaks.length === 0 && routes.filter((r) => r.open).length >= 3, { ...anon, open: evidence.routes.open });

    // ---- C. admin2, an administrator in the narrow role ------------------------------------------------------------------
    const a2 = {};
    a2.capabilities = { bookmarks: ((await request('GET', '/Bookmarks/Capabilities', as(admin2))).json || {}).IsAdmin };
    if (optional) a2.capabilities.tagCloud = ((await request('GET', '/TagCloud/Capabilities', as(admin2))).json || {}).IsAdmin;
    const adminIndex = (await request('GET', '/LibraryMaintenance/Duplicates/Index', 'admin')).json || {};
    const index = (await scan('admin2', 'GET /LibraryMaintenance/Duplicates/Index', 'GET', '/LibraryMaintenance/Duplicates/Index', as(admin2))).json || {};
    const indexedAllowed = (adminIndex.Libraries || []).map((l) => idN(l.Id)).filter((id) => allowedIds.includes(id)).sort();
    a2.index = { libraries: (index.Libraries || []).map((l) => `${l.Name} ${l.Movies}`), movies: index.Movies, expectedLibraries: indexedAllowed.length, expectedMovies: visibleMovies.filter((m) => indexedAllowed.includes(m.lib)).length };
    const g = (await scan('admin2', 'GET /LibraryMaintenance/Duplicates/Groups', 'GET', '/LibraryMaintenance/Duplicates/Groups?limit=200', as(admin2))).json || {};
    const entryLibs = (g.Items || []).flatMap((x) => (x.Entries || []).flatMap((e) => (e.Libraries || []).map((l) => idN(l.Id))));
    a2.groups = { count: g.GroupCount, effective: (g.EffectiveLibraryIds || []).map(idN).sort(), entryLibs: [...new Set(entryLibs)], totalMovies: g.TotalMovies };
    const adminGroups = ((await request('GET', '/LibraryMaintenance/Duplicates/Groups?limit=200', 'admin')).json || {}).Items || [];
    a2.outside = [];
    for (const group of adminGroups.filter((x) => (x.Entries || []).some((e) => (e.Libraries || []).some((l) => !allowedIds.includes(idN(l.Id)))))) {
        const a = await scan('admin2', `GET /LibraryMaintenance/Duplicates/Groups/{a group with a hidden member}`, 'GET', `/LibraryMaintenance/Duplicates/Groups/${enc(group.GroupId)}`, as(admin2));
        const b = await request('GET', `/LibraryMaintenance/Duplicates/Groups/${crypto.randomBytes(8).toString('hex')}`, as(admin2));
        a2.outside.push({ group: group.DisplayTitle, status: a.status, same: a.status === b.status && normalizeFor(a.text, []) === normalizeFor(b.text, []) });
    }
    a2.pairs = [
        await samePair('admin2', 'LM Groups?libraryIds=<a hidden library>', 'GET', (id) => `/LibraryMaintenance/Duplicates/Groups?libraryIds=${id}`, as(admin2), movies2.Id),
        await samePair('admin2', 'Bookmarks Admin/IdentityDemo/Items/<a hidden title>', 'GET', (id) => `/Bookmarks/Admin/IdentityDemo/Items/${id}`, as(admin2), pairMovie.id),
        await samePair('admin2', 'Bookmarks Admin/LibraryDemo/UserData/<a hidden title>', 'GET', (id) => `/Bookmarks/Admin/LibraryDemo/UserData/${id}`, as(admin2), pairMovie.id),
        await samePair('admin2', 'Bookmarks Admin/LibraryDemo/VersionSets?itemIds=<a hidden title>', 'GET', (id) => `/Bookmarks/Admin/LibraryDemo/VersionSets?itemIds=${id}`, as(admin2), pairMovie.id)
    ];
    const lmDiag = (await scan('admin2', 'GET /LibraryMaintenance/Admin/Diagnostics', 'GET', '/LibraryMaintenance/Admin/Diagnostics', as(admin2))).json || {};
    a2.lmDiagnostics = { DuplicatesIndex: lmDiag.DuplicatesIndex, DuplicateGroups: lmDiag.DuplicateGroups };
    await scan('admin2', 'GET /Bookmarks/Admin/Diagnostics', 'GET', '/Bookmarks/Admin/Diagnostics', as(admin2));
    await scan('admin2', 'GET /Bookmarks/Admin/Clips/Summary', 'GET', '/Bookmarks/Admin/Clips/Summary', as(admin2));
    evidence.admin2 = a2;
    const scopeOnly = (x) => !!x && typeof x === 'object' && Object.keys(x).length === 1 && typeof x.Scope === 'string';
    check('admin2 in the narrow role is still an administrator to the plugins (Capabilities.IsAdmin true)', a2.capabilities.bookmarks === true && (!optional || a2.capabilities.tagCloud === true), a2.capabilities);
    check('Library Maintenance for admin2: the index lists and counts its libraries only, the duplicates cover its libraries only (no group of a hidden member: those are 404 as a group nobody has), and its Diagnostics hide the server-wide sources (the Scope line)',
        a2.index.libraries.length === a2.index.expectedLibraries && index.Movies === a2.index.expectedMovies && (index.Libraries || []).every((l) => allowedIds.includes(idN(l.Id)))
            && a2.groups.effective.every((id) => allowedIds.includes(id)) && a2.groups.entryLibs.every((id) => allowedIds.includes(id)) && a2.outside.length > 0 && a2.outside.every((x) => x.status === 404 && x.same)
            && (typeof lmDiag.DuplicatesIndex === 'string' || scopeOnly(lmDiag.DuplicatesIndex)) && !JSON.stringify(lmDiag.DuplicatesIndex || '').includes('Server-wide'), a2);
    check('the plugins\' admin endpoints that take an item or library id answer admin2 for a hidden one exactly as for an id nobody has (LM Groups by library; Bookmarks\' developer demos by item; Bookmarks\' admin clip view below, as a finding)', a2.pairs.every((x) => x.same), a2.pairs);

    // The stated exceptions and what goes beyond them.
    const lmSettings = await request('GET', '/LibraryMaintenance/Admin/Settings', as(admin2));
    const settingsLibs = ((lmSettings.json || {}).Libraries || []).map((l) => l.Name);
    evidence.statedExceptions = { lmSettingsLibraries: settingsLibs, lmSettingsItemLeaks: leaksIn(lmSettings.text).filter((w) => !/^library/.test(w)), accessRolesAdminLibraries: ((await request('GET', '/AccessRoles/Admin/Libraries', as(admin2))).json || []).length };
    check('stated exceptions as stated: Library Maintenance\'s Settings list the server\'s movie libraries to every administrator and name no item; Access Roles\' own admin page works for a narrow administrator', evidence.statedExceptions.lmSettingsItemLeaks.length === 0 && settingsLibs.includes(movies2.Name) && evidence.statedExceptions.accessRolesAdminLibraries >= libraries.length, evidence.statedExceptions);
    if (optional) {
        const tcStatus = (await request('GET', '/TagCloud/Admin/Status', as(admin2))).json || {};
        const tcStatusAdmin = (await request('GET', '/TagCloud/Admin/Status', 'admin')).json || {};
        if (tcStatus.Titles !== undefined && tcStatus.Titles !== visible.length) {
            findings.push({ id: 'TC-admin-status', plugin: 'Tag Cloud 1.0.0', what: 'GET /TagCloud/Admin/Status (and Admin/Diagnostics\' Index, and the settings page\'s index line) give an administrator in a narrow library role the index\'s server-wide totals: titles and words per source over every library, hidden ones included. The suite\'s isolation rules say a total never covers items the caller can\'t see; Library Maintenance hides its server-wide sources from such an administrator (the Scope line). No stated exception covers it; the totals name no title and no word.', admin2: { Titles: tcStatus.Titles, Words: tcStatus.Words }, admin: { Titles: tcStatusAdmin.Titles, Words: tcStatusAdmin.Words }, visibleTitles: visible.length });
        }
    }
    // Bookmarks' admin clip view lists every user's clips; Bookmarks states it as the one exception to isolation. Whether a
    // library role narrows it is what the suite's isolation rules ask (admin endpoints aren't exempt from library access):
    // checked by item id, reported as a finding.
    const clipPair = await samePair('admin2-clips', 'Bookmarks Admin/Clips?itemId=<the kid\'s clip\'s hidden title>', 'GET', (id) => `/Bookmarks/Admin/Clips?itemId=${id}`, as(admin2), clipMovie.id);
    evidence.admin2.clipView = clipPair;
    const adminClips = await request('GET', `/Bookmarks/Admin/Clips?userId=${idN(kid.Id)}`, as(admin2));
    const kidClipForAdmin2 = ((adminClips.json || {}).Items || []).find((c) => c.Id === ownBookmark.Id) || null;
    if (kidClipForAdmin2) {
        const shown = Object.fromEntries(Object.entries(kidClipForAdmin2).filter(([k]) => /item|name|path|file|title/i.test(k)).map(([k, v]) => [k, typeof v === 'string' && leaksIn(v).length ? `<${leaksIn(v).join(', ')}>` : v]));
        findings.push({ id: 'BM-admin-clips', plugin: 'Bookmarks 1.2.0', what: 'GET /Bookmarks/Admin/Clips (My Clips\' "All users", AdminsCanManageAllClips on) lists another user\'s clip of a title the narrow administrator can\'t open, with the bookmark\'s stored title, its item id and the clip\'s path (ItemAvailable false: the admin\'s), also when asked by that item\'s id (itemId=), where the suite\'s isolation check wants the same answer as for an id nobody has. Bookmarks states the admin clip view as the one exception to isolation (the Name, title, range and file name of clips) and its DEVELOPMENT.md says ItemAvailable is the admin\'s; the suite\'s isolation rules say admin endpoints are not exempt from library access.', byItemId: { same: clipPair.same, hidden: clipPair.hidden, none: clipPair.none }, leaks: leaksIn(adminClips.text), fields: shown });
    }
    const admin2Leaks = scanned.admin2.filter((x) => x.leaks.length);
    check('Bookmarks\' admin clip view for admin2: the kid\'s clip of the lost title is either left out (by item id the same answer as an id nobody has) or reported as finding BM-admin-clips', clipPair.same || findings.some((x) => x.id === 'BM-admin-clips'), clipPair);
    check('no other answer to admin2 carries a hidden title, a word only hidden titles carry or a hidden library (outside the stated exceptions and the findings)', admin2Leaks.length === 0, { scanned: scanned.admin2.length, leaks: admin2Leaks });

    // ---- D. Access Roles disabled ---------------------------------------------------------------------------------------
    const set = [];
    for (const [who, user] of [['kid', as(kid)], ['open', as(open)]]) {
        for (const p of ['/Bookmarks/Query', '/Bookmarks/Groups', '/Bookmarks/Clips', '/Bookmarks/Capabilities', `/Bookmarks/Query?ItemId=${pairMovie.id}`, ...(optional ? ['/TagCloud/Libraries?scope=Both', '/TagCloud/Capabilities', '/TagCloud/Clouds',
            `/TagCloud/Titles?source=Genres&key=${enc(visibleGenre)}&scope=Both`, `/TagCloud/Titles?source=Genres&key=${enc(hiddenGenre)}&scope=Both`, `/TagCloud/Words?source=Genres&scope=Movies&libraryIds=${idN(movies2.Id)}`] : [])]) set.push([`${who} ${p}`, p, user]);
        if (optional) for (const s of ['Genres', 'Studios', 'Actors']) set.push([`${who} Words ${s}`, `/TagCloud/Words?source=${s}&scope=Both&limit=200`, user]);
    }
    for (const p of ['/LibraryMaintenance/Duplicates/Index', '/LibraryMaintenance/Duplicates/Groups?limit=200', `/LibraryMaintenance/Duplicates/Groups?libraryIds=${idN(movies2.Id)}`, '/Bookmarks/Admin/Clips', '/Bookmarks/Capabilities', ...(optional ? ['/TagCloud/Capabilities', '/TagCloud/Words?source=Actors&scope=Both&limit=200'] : [])]) set.push([`admin2 ${p}`, p, as(admin2)]);
    for (const p of ['/LibraryMaintenance/Duplicates/Groups?limit=200', ...(optional ? ['/TagCloud/Words?source=Genres&scope=Both&limit=200', '/TagCloud/Libraries?scope=Both'] : []), '/Bookmarks/Admin/Clips']) set.push([`admin ${p}`, p, 'admin']);
    const enabledAnswers = await answers(set);
    const suiteRoutes = routes.filter((r) => !/^\/AccessRoles\//.test(r.template));
    const off = {};
    off.disable = await setAccessRoles(false);
    off.plugins = (await plugins()).filter((p) => p.Name === plugin.name || SUITE.includes(p.Name)).map((p) => `${p.Name} ${p.Version} ${p.Status}`);
    // Jellyfin 10.11.5's start (PluginManager.DiscoverPlugins) tries to delete a disabled plugin's folder when an enabled one
    // sorts after it (fixed upstream in 12.0, a272efb9a2): in this in-process restart the loaded DLL can't be deleted, so the
    // plugin is marked Deleted (its .pdb gone) until Enable; after a real restart the folder would be gone.
    off.deleteAttempt = logSince(startedIso).filter((l) => /PluginManager: Unable to delete .*Access Roles_/.test(l.text) || /PluginManager: Skipping disabled plugin .*Access Roles/.test(l.text)).map((l) => l.text.slice(0, 200));
    if (off.disable.status === 'Deleted') {
        findings.push({ id: 'JF-disable-deletes', plugin: 'Jellyfin 10.11.5', what: 'Disabling a plugin (Dashboard → Plugins → Disable, or POST /Plugins/{id}/{version}/Disable) and restarting makes 10.11.5\'s PluginManager.DiscoverPlugins delete the plugin\'s folder at that start (a disabled plugin falls through to the "older version" cleanup when an enabled plugin sorts after it by name; upstream fixed it for 12.0 in a272efb9a2, "Fix disabled plugins being re-enabled on restart"). Here, in an in-process restart, the loaded DLL couldn\'t be deleted (UnauthorizedAccessException), so the folder lost its .pdb and the plugin was marked Deleted until Enable; after a real restart Access Roles would be gone, without OnUninstalling. Its data and settings files stay.', log: off.deleteAttempt });
    }
    const disabledAnswers = await answers(set);
    off.differ = Object.keys(enabledAnswers).filter((k) => stable(enabledAnswers[k]) !== stable(disabledAnswers[k])).map((k) => ({ request: k, enabled: JSON.stringify(enabledAnswers[k]).slice(0, 300), disabled: JSON.stringify(disabledAnswers[k]).slice(0, 300) }));
    off.nativeKid = access(await policyOf(kid.Id));
    const fresh = await step('Access Roles disabled: the kid bookmarks a visible movie', 'POST', '/Bookmarks', { ItemId: visibleMovie.id, StartTicks: 7 * TICKS, Name: 'suiteprobe disabled' }, as(kid));
    if (fresh.status === 201 && fresh.json) kidBookmarks.add(fresh.json.Id);
    const gone = fresh.status === 201 && fresh.json ? await step('and deletes it', 'DELETE', `/Bookmarks/${fresh.json.Id}`, null, as(kid)) : { status: null };
    if (gone.status === 204) kidBookmarks.delete(fresh.json.Id);
    off.bookmark = { created: fresh.status, deleted: gone.status };
    off.anonymous = await sweep('none', null, suiteRoutes.map((r) => ({ ...r })), 401);
    off.enable = await setAccessRoles(true);
    const rowsAfter = await usersRows();
    off.rolesKept = { kid: (rowsAfter.find((u) => idN(u.UserId) === idN(kid.Id)) || {}).State, admin2: (rowsAfter.find((u) => idN(u.UserId) === idN(admin2.Id)) || {}).State, role: (await roles()).some((r) => r.Id === narrow.Id) };
    evidence.disabled = { ...off, compared: set.length };
    check(`with Access Roles disabled (and the server restarted) the ${inWords(SUITE.length)} plugins answer the kid, an unmanaged user, admin2 and the administrator as before (${set.length} requests), the kid keeps its native libraries, a bookmark can be made and deleted, nothing answers without a token but the scripts`,
        ['Disabled', 'Deleted'].includes(off.disable.status) && off.deleteAttempt.some((l) => /Skipping disabled plugin/.test(l)) && off.differ.length === 0 && !off.nativeKid.all && stable(off.nativeKid.folders) === stable(allowedIds) && off.bookmark.created === 201 && off.bookmark.deleted === 204
            && off.anonymous.readsRefused && off.anonymous.wrong.length === 0, { disable: off.disable, plugins: off.plugins, differ: off.differ, nativeKid: off.nativeKid, bookmark: off.bookmark, anonymous: { routes: off.anonymous.routes, wrong: off.anonymous.wrong } });
    check('Access Roles enabled again (and restarted): active, its role and the two assignments kept', off.enable.status === 'Active' && off.rolesKept.role && off.rolesKept.kid !== 'NotManaged' && off.rolesKept.admin2 !== 'NotManaged', { enable: off.enable, rolesKept: off.rolesKept });

    // ---- E. admin2 made a non-admin on the stock Dashboard page --------------------------------------------------------
    const before = { bookmarks: ((await request('GET', '/Bookmarks/Capabilities', as(admin2))).json || {}).IsAdmin, lm: (await request('GET', '/LibraryMaintenance/Duplicates/Index', as(admin2))).status };
    const { result, attempts } = await runWebcheck(DEMOTE_WEBCHECK);
    const steps = ((result && result.steps) || []).map((s) => ({ name: s.name, ok: s.ok, error: s.ok ? undefined : s.error }));
    evidence.demote = { before, webcheck: { ok: !!(result && result.ok), attempts, steps: steps.length, failed: steps.filter((s) => !s.ok), exceptions: result && result.exceptions, failedRequests: result && result.failedRequests, screenshots: result && result.screenshots } };
    check(`webcheck ${DEMOTE_WEBCHECK}: credentials.json's admin unticks "Allow this user to manage the server" for admin2 on the stock Dashboard page and saves`, before.bookmarks === true && before.lm === 200 && !!(result && result.ok), evidence.demote);
    const capabilitiesAfter = { bookmarks: ((await request('GET', '/Bookmarks/Capabilities', as(admin2))).json || {}).IsAdmin };
    if (optional) capabilitiesAfter.tagCloud = ((await request('GET', '/TagCloud/Capabilities', as(admin2))).json || {}).IsAdmin;
    const after = { capabilities: capabilitiesAfter, native: access(await policyOf(admin2.Id)), user: (await request('GET', '/Bookmarks/Query', as(admin2))).status };
    const demoted = await sweep('admin2 (demoted)', as(admin2), routes.filter((r) => r.admin && !r.open), 403);
    evidence.demote.after = { ...after, sweep: demoted };
    check(`at the next request admin2's token gets 403 from every admin route of the ${inWords(SUITE.length + 1)} plugins and Capabilities.IsAdmin false; it is a non-admin natively, its role's libraries kept, its user routes still answer`,
        after.capabilities.bookmarks === false && (!optional || after.capabilities.tagCloud === false) && !after.native.admin && stable(after.native.folders) === stable(allowedIds) && after.user === 200
            && demoted.readsRefused && demoted.wrong.length === 0 && demoted.writesSent > 0, evidence.demote.after);

    const othersAfter = await othersPolicies();
    const changed = Object.keys(othersBefore).filter((id) => othersAfter[id] !== othersBefore[id]);
    check('no other user\'s policy was written', changed.length === 0, { users: Object.keys(othersBefore).length, changed });
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
        const ar = (await plugins()).find((p) => idN(p.Id) === idN(plugin.guid));
        cleanupResult.probeLeft = {
            accessRoles: ar && ar.Status,
            roles: (await roles()).filter((r) => r.Name.startsWith(PREFIX)).length,
            users: ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Name.toLowerCase().startsWith(PROBE_USER)).length,
            clips: kid ? ((await request('GET', `/Bookmarks/Admin/Clips?userId=${idN(kid.Id)}&includeDeleted=true`, 'admin')).json || { Items: [] }).Items.length : 0
        };
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + (e.stack || String(e));
    }
    evidence.cleanup = cleanupResult;
    if (cleanupErrors.length) failure = (failure ? failure + ' | ' : '') + 'cleanup: ' + cleanupErrors.join('; ');
    if (!failure) {
        const left = cleanupResult.probeLeft || {};
        check('afterwards nothing of the probe\'s is left (its role, users, bookmarks and clip) and Access Roles is active', left.accessRoles === 'Active' && left.roles === 0 && left.users === 0 && left.clips === 0, cleanupResult);
        const log = logSince(startedIso).filter((l) => /Jellyfin\.Plugin\.(AccessRoles|Bookmarks|TagCloud|LibraryMaintenance)/.test(l.text) && (l.level === 'ERR' || l.level === 'FTL'));
        check(`the log has no error of ${listOf(['Access Roles', ...SUITE], 'or')}`, log.length === 0, log.map((l) => l.text.slice(0, 240)));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, findings, ...(skipped.length ? { skipped } : {}), transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
