#!/usr/bin/env node
// Access Roles' sync hooks under races in a running sandbox (a busy database at user creation, a sign-in right after it,
// the restore of a user created under default roles, the restore hold):
//   1. A user created under default roles while the database is busy: a locker process of the probe holds BEGIN IMMEDIATE
//      on the SANDBOX's jellyfin.db for 4 s from the moment Jellyfin's UserCreated activity entry for the new user's row
//      appears (inside POST /Users/New, after the UserCreated event's first consumer, Jellyfin's activity log, and before
//      the core's save of the password; Access Roles' consumer, registered after the core's, marks the user as being
//      created and saves the default roles meanwhile), while a store change (a role saved, its run 2 s later) and a manual
//      sync run: the creation answers 200, POST Sync counts the user as managed and lists them as being created and writes
//      nobody, the password signs in, an empty password is refused, and the user gets the default role's library.
//      --attempts of them (4), and one control without default roles.
//   2. Create, then sign in at once (--signins, 6): both 200, the response's User and the new token's /UserViews show only
//      the default role's library, and the server still enforces it after the first run.
//   3. Restore of a user created under default roles: the Unmanage and role-delete previews say "Restores to no
//      libraries: created under default roles.", and the sync writes no library.
//   4. The restore hold (its file written by the probe, as an earlier process leaves it): a managed user whose access
//      drifted wider is refused at sign-in (503, Retry-After, the token revoked, audited), a managed administrator isn't;
//      while an admin has the sync switched off nobody is refused; confirmRestore before a dry run is 409 DryRunRequired;
//      the dry run, then the confirmation, lift the hold and write the user back.
//   5. Every user the probe didn't create: never written; the log without an unexpected Access Roles warning.
// It writes only what is its own: its users (hookrace-..., random passwords kept in memory for their sign-ins, never printed
// or saved), their policies, its role ("Hook race probe ..."), the default roles (none again in a finally), the plugin's
// settings (put back in a finally) and the hold file in the sandbox's Access Roles folder (deleted in a finally). It reads
// the sandbox's database read-only through node:sqlite (never the password column); its one process, the locker, opens
// the sandbox's database only, takes BEGIN IMMEDIATE and ends with ROLLBACK (no row is written). Node 22 built-ins only.
// Loopback only, never port 8096. Tokens come from credentials.json and sign-ins, and are never printed.
//
//   node scripts/accessroles-sync-hooks-race-probe.mjs --sandbox access [--attempts 4] [--signins 6] [--out result.json]
//
// Start the sandbox first (scripts/README.md, this probe's section). Prints one JSON result (pass, checks, transcript,
// evidence) and exits with 0 when every check passed.

import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PLUGIN = 'AccessRoles';
const PREFIX = 'Hook race probe ';
const PROBE_USER = 'hookrace-';
const HOLD_MS = 4000;
const NO_LIBRARIES_NOTE = 'Restores to no libraries: created under default roles.';

function usage() {
    return 'usage: node scripts/accessroles-sync-hooks-race-probe.mjs --sandbox <name> [--attempts <n>] [--signins <n>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, attempts: 4, signins: 6 };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--attempts') o.attempts = Number(argv[++i]);
        else if (a === '--signins') o.signins = Number(argv[++i]);
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!Number.isInteger(o.attempts) || o.attempts < 1 || o.attempts > 20) throw new Error(`--attempts 1..20. ${usage()}`);
    if (!Number.isInteger(o.signins) || o.signins < 1 || o.signins > 30) throw new Error(`--signins 1..30. ${usage()}`);
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
const CONFIGURATION_PATH = `/Plugins/${plugin.guid}/Configuration`;

// The sandbox's files: the database (read only, and the locker's), the log and the restore hold's file. All must be inside
// .sandbox, never under ProgramData\Jellyfin.
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
const dbFile = path.join(dataDir, 'data', 'jellyfin.db');
const holdFile = path.join(dataDir, 'data', 'accessroles', 'library-sync-hold.json');
function assertInSandbox(p) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), path.resolve(p));
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(path.resolve(p))) throw new Error(`refusing to touch ${p}`);
    return path.resolve(p);
}
for (const p of [dbFile, holdFile, path.join(sandboxRoot, 'log')]) assertInSandbox(p);

const checks = [];
const transcript = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin, attempts: opts.attempts, signins: opts.signins };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authHeader(token, deviceId = 'accessroles-sync-hooks-race-probe') {
    return `MediaBrowser Client="accessroles-sync-hooks-race-probe", Device="sandbox", DeviceId="${deviceId}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// A request as the admin (credentials.json), as one of the probe's users (a token from its sign-in), or anonymous.
function request(method, p, user = 'admin', body = null, deviceId = undefined) {
    return new Promise((resolve, reject) => {
        const token = user === null ? null : user === 'admin' ? credentials.admin.token : tokens.get(user);
        if (user !== null && !token) throw new Error(`no token for ${user}`);
        const headers = { Authorization: authHeader(token, deviceId), Accept: 'application/json' };
        if (body !== null) headers['Content-Type'] = 'application/json';
        const started = Date.now();
        const req = http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let json = null;
                try { json = text && /json/.test(res.headers['content-type'] || '') ? JSON.parse(text) : null; } catch { json = null; }
                resolve({ status: res.statusCode, type: res.headers['content-type'] || '', headers: res.headers, text, json, ms: Date.now() - started });
            });
        });
        req.on('error', reject);
        req.setTimeout(60000, () => req.destroy(new Error(`timeout ${method} ${p}`)));
        if (body !== null) req.write(typeof body === 'string' ? body : JSON.stringify(body));
        req.end();
    });
}

// One request of the walkthrough, as curl would send it (the token as <admin>, never its value).
async function step(label, method, p, body = null) {
    const r = await request(method, p, 'admin', body);
    transcript.push({ step: label, curl: `curl -X ${method} "http://127.0.0.1:${port}${baseUrl}${p}" -H "Authorization: MediaBrowser Token=<admin>"${body !== null ? ` -d '${JSON.stringify(body)}'` : ''}`, status: r.status, code: r.json && r.json.Code ? r.json.Code : undefined });
    return r;
}

const idN = (x) => (x || '').replace(/-/g, '').toLowerCase();
const ids = (list) => (list || []).map(idN);
const sameIds = (a, b) => JSON.stringify([...ids(a)].sort()) === JSON.stringify([...ids(b)].sort());
const stable = (value) => JSON.stringify(value, (k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.keys(v).sort().map((key) => [key, v[key]])) : v));
const names = (list) => (list || []).map((x) => x.Name);
// The post-write check runs about 5 s after each write; the walkthrough lets it pass before a native edit.
const settle = () => delay(5500);
const until = async (label, condition, timeoutMs = 15000, everyMs = 200) => {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        const value = await condition();
        if (value) return { ok: true, ms: Date.now() - started, value };
        await delay(everyMs);
    }
    return { ok: false, ms: Date.now() - started, label };
};

// ---- the probe's own users: the only users it writes ------------------------------------------------------------------
const own = new Map();
const secrets = new Map();
const tokens = new Map();
async function createOwnUser(name) {
    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);
    const password = crypto.randomBytes(24).toString('base64url');
    const r = await request('POST', '/Users/New', 'admin', { Name: name, Password: password });
    transcript.push({ step: `create the probe's user ${name}`, curl: `curl -X POST "http://127.0.0.1:${port}${baseUrl}/Users/New" -H "Authorization: MediaBrowser Token=<admin>" -d '{"Name":"${name}","Password":"<random>"}'`, status: r.status, ms: r.ms });
    if (r.status === 200 && r.json && r.json.Id) {
        own.set(idN(r.json.Id), name);
        secrets.set(idN(r.json.Id), password);
    }
    return { status: r.status, ms: r.ms, user: r.status === 200 && r.json && r.json.Id ? r.json : null };
}

async function createOwnUserOrThrow(name) {
    const created = await createOwnUser(name);
    if (!created.user) throw new Error(`POST /Users/New ${name}: ${created.status}`);
    return created.user;
}

// A policy save of one of the probe's users, as an admin's on the stock Dashboard page (the answer is returned).
async function postOwnPolicy(id, policy) {
    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);
    const r = await request('POST', `/Users/${id}/Policy`, 'admin', policy);
    transcript.push({ step: `set the native policy of the probe's user ${own.get(idN(id))}`, status: r.status });
    return r;
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

// A Dashboard-style edit of one of the probe's users: the full policy the page loaded, with some fields changed.
async function setOwnPolicy(user, changes) {
    const current = (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
    const r = await postOwnPolicy(user.Id, { ...current, ...changes });
    if (r.status !== 204) throw new Error(`set the native policy of ${user.Name}: ${r.status}`);
    return (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
}

// The probe's user signs in, with its password (or, to show an empty one is refused, with none): the password is only
// ever in this request's body; the token stays in memory. The status, the answer's User and Retry-After are returned.
const deviceOf = (user) => `hookrace-${idN(user.Id).slice(0, 8)}`;
async function signIn(user, withPassword = true) {
    const password = withPassword ? secrets.get(idN(user.Id)) : '';
    if (withPassword && !password) throw new Error(`no password for ${user.Name}`);
    const r = await request('POST', '/Users/AuthenticateByName', null, { Username: user.Name, Pw: password }, deviceOf(user));
    transcript.push({ step: `${user.Name} signs in${withPassword ? '' : ' with an empty password'}`, status: r.status, ms: r.ms });
    if (r.status === 200 && r.json && r.json.AccessToken) tokens.set(idN(user.Id), r.json.AccessToken);
    return { status: r.status, user: r.status === 200 && r.json ? r.json.User : null, retryAfter: r.headers['retry-after'], detail: r.json && r.json.detail };
}

const policyOf = async (user) => (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
const sameAccess = (policy, all, folders) => policy && !!policy.EnableAllFolders === all && (all || sameIds(policy.EnabledFolders, folders));
const viewsOf = async (user) => {
    if (!tokens.has(idN(user.Id))) return null;
    const r = await request('GET', `/UserViews?userId=${user.Id}`, idN(user.Id));
    return r.status === 200 && r.json ? names(r.json.Items) : null;
};
const virtualFolders = async () => (await request('GET', '/Library/VirtualFolders', 'admin')).json || [];
// Whether the server still has a device (a session's token) for the device id of the probe's user; nothing else of the
// answer is kept (it holds tokens).
const hasDevice = async (user) => ((((await request('GET', '/Devices', 'admin')).json || {}).Items) || []).some((d) => d.Id === deviceOf(user));

// ---- the plugin's own settings ------------------------------------------------------------------------------------
async function getConfiguration() {
    const r = await request('GET', CONFIGURATION_PATH, 'admin');
    if (r.status !== 200 || !r.json) throw new Error(`GET ${CONFIGURATION_PATH}: ${r.status}`);
    return r.json;
}

async function setConfiguration(values) {
    const r = await request('POST', CONFIGURATION_PATH, 'admin', { ...(await getConfiguration()), ...values });
    transcript.push({ step: `settings: ${JSON.stringify(values)}`, status: r.status });
    if (r.status !== 204) throw new Error(`POST ${CONFIGURATION_PATH}: ${r.status}`);
}

// ---- the sandbox's database, read only: every stored field of a user, never the password -------------------------------
let sqlite = null;
async function dumpUser(userId) {
    sqlite = sqlite || (await import('node:sqlite'));
    const db = new sqlite.DatabaseSync(assertInSandbox(dbFile), { readOnly: true });
    try {
        const columns = db.prepare('PRAGMA table_info(Users)').all().map((c) => c.name).filter((n) => n !== 'Password');
        const key = idN(userId);
        const row = db.prepare(`SELECT ${columns.map((c) => `"${c}"`).join(', ')} FROM Users WHERE replace(lower(Id), '-', '') = ?`).get(key) || {};
        const fields = {};
        for (const c of columns) fields[`User.${c}`] = row[c] === null || row[c] === undefined ? null : String(row[c]);
        for (const p of db.prepare('SELECT Kind, Value FROM Permissions WHERE replace(lower(UserId), \'-\', \'\') = ? ORDER BY Kind').all(key)) fields[`Permission.${p.Kind}`] = String(p.Value);
        for (const p of db.prepare('SELECT Kind, Value FROM Preferences WHERE replace(lower(UserId), \'-\', \'\') = ? ORDER BY Kind').all(key)) fields[`Preference.${p.Kind}`] = p.Value;
        return fields;
    } finally {
        db.close();
    }
}

// ---- the locker: a process of the probe's own that holds the sandbox database's write lock --------------------------
// It opens the sandbox's jellyfin.db (the path asserted above), polls Users for the name every 2 ms, then ActivityLogs for
// that user's UserCreated entry every 1 ms, takes BEGIN IMMEDIATE as soon as the entry is there and ends with ROLLBACK
// after holdMs: it reads, holds and writes nothing. Why the entry: 10.11.5 publishes UserCreatedEventArgs
// right after the row is saved and runs its consumers one after the other, Jellyfin's own first (registered before the
// plugins', v10.11.5:Emby.Server.Implementations/ApplicationHost.cs:460-462); its UserCreatedLogger writes that entry
// (Jellyfin.Server.Implementations/Events/Consumers/Users/UserCreatedLogger.cs). A lock taken as soon as the row appears
// often caught that write, so Access Roles' consumer ran only after the lock and the manual sync met a user with no
// assignment yet: not being created, not managed, nothing to write.
const LOCKER = [
    "import fs from 'node:fs';",
    "import { DatabaseSync } from 'node:sqlite';",
    'const [file, name, holdMs] = process.argv.slice(1);',
    'const sleep = (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);',
    "const say = (o) => fs.writeSync(1, (typeof o === 'string' ? o : JSON.stringify(o)) + '\\n');",
    'const db = new DatabaseSync(file);',
    "const find = db.prepare('SELECT Id FROM Users WHERE Username = ?');",
    "const created = db.prepare(\"SELECT Id FROM ActivityLogs WHERE UserId = ? AND Type = 'UserCreated'\");",
    "say('ready');",
    'const started = Date.now();',
    'let row = null;',
    'while (!row && Date.now() - started < 60000) { try { row = find.get(name); } catch { row = null; } if (!row) sleep(2); }',
    "if (!row) { say({ error: 'the row never appeared' }); process.exit(2); }",
    'const seen = Date.now();',
    'let logged = null;',
    'while (!logged && Date.now() - seen < 10000) { try { logged = created.get(row.Id); } catch { logged = null; } if (!logged) sleep(1); }',
    "if (!logged) { say({ error: 'the UserCreated activity entry never appeared' }); process.exit(2); }",
    'const loggedAt = Date.now();',
    "for (;;) { try { db.exec('BEGIN IMMEDIATE'); break; } catch (e) { if (!/busy|locked/i.test(e.message) || Date.now() - loggedAt > 10000) throw e; sleep(1); } }",
    'const locked = Date.now();',
    'say({ locked: true, at: locked, id: row.Id, afterRowMs: locked - seen, activityAfterRowMs: loggedAt - seen, afterActivityMs: locked - loggedAt });',
    'sleep(Number(holdMs));',
    "db.exec('ROLLBACK');",
    'db.close();',
    'say({ released: true, at: Date.now(), heldMs: Date.now() - locked });'
].join('\n');
const lockers = new Set();
function startLocker(name, holdMs) {
    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to lock for ${name}`);
    const child = spawn(process.execPath, ['--no-warnings', '--input-type=module', '-e', LOCKER, assertInSandbox(dbFile), name, String(holdMs)], { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
    lockers.add(child);
    const lines = [];
    const waiters = [];
    let buffer = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => {
        buffer += chunk.toString('utf8');
        let at;
        while ((at = buffer.indexOf('\n')) >= 0) {
            const line = buffer.slice(0, at).trim();
            buffer = buffer.slice(at + 1);
            lines.push(line);
            for (const w of [...waiters]) if (w.test(line)) { waiters.splice(waiters.indexOf(w), 1); w.resolve(line); }
        }
    });
    child.stderr.on('data', (chunk) => { stderr += chunk.toString('utf8'); });
    const exited = new Promise((resolve) => child.on('exit', (code) => { lockers.delete(child); resolve(code); }));
    const waitFor = (test, timeoutMs) => {
        const found = lines.find(test);
        if (found) return Promise.resolve(found);
        return Promise.race([
            new Promise((resolve) => waiters.push({ test, resolve })),
            delay(timeoutMs).then(() => null)
        ]);
    };
    return { waitFor, exited, lines, stderr: () => stderr };
}
const parsed = (line) => { try { return JSON.parse(line); } catch { return null; } };

// ---- the audit log and the server log ------------------------------------------------------------------------------
const diagnostics = async () => (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).json || {};
const lastSeq = async () => (((await diagnostics()).Audit || {}).LastSeq || 0);
async function auditSince(seq, action = null) {
    const r = await request('GET', `/AccessRoles/Admin/Audit?limit=200${action ? `&action=${action}` : ''}`, 'admin');
    return ((r.json && r.json.Items) || []).filter((e) => e.Seq > seq).reverse();
}

function logSinceStart() {
    const folder = assertInSandbox(path.join(sandboxRoot, 'log'));
    const since = Date.parse(state.StartedUtc || '1970-01-01T00:00:00Z');
    const lines = [];
    for (const file of fs.readdirSync(folder).filter((f) => /^log_\d{8}\.log$/.test(f)).sort()) {
        for (const line of fs.readFileSync(path.join(folder, file), 'utf8').split(/\r?\n/)) {
            const m = /^\[(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2})\] \[(\w{3})\]/.exec(line);
            if (m && Date.parse(`${m[1]}T${m[2]}${m[3]}`) >= since - 1000) lines.push({ level: m[4], text: line });
        }
    }
    return lines;
}

const sync = (query = '') => step(`POST Sync${query}`, 'POST', `/AccessRoles/Admin/Sync${query}`);
const usersRows = async () => (await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || [];
const usersRow = async (user) => (await usersRows()).find((u) => idN(u.UserId) === idN(user.Id));
const putUser = async (label, user, body, query = '') => step(label, 'PUT', `/AccessRoles/Admin/Users/${user.Id}${query}`, { ...body, IfUpdatedAt: (await usersRow(user) || {}).UpdatedUtc });
const setDefaultRoles = (label, roleIds) => step(label, 'PUT', '/AccessRoles/Admin/DefaultRoles', { RoleIds: roleIds });

// ---- the restore hold's file: as an earlier process leaves it (RestoreHold); deleted again in a finally ----------------
function writeHoldFile() {
    fs.writeFileSync(assertInSandbox(holdFile), JSON.stringify({ Reason: 'StoreRestored', SinceUtc: new Date().toISOString(), Message: 'Written by accessroles-sync-hooks-race-probe.mjs (a sandbox check of the hold).', Confirm: 'POST /AccessRoles/Admin/Sync?dryRun=true, then POST /AccessRoles/Admin/Sync?confirmRestore=true.' }, null, 2));
    transcript.push({ step: 'the restore hold\'s file written in the sandbox\'s Access Roles folder' });
}

function removeHoldFile() {
    if (fs.existsSync(assertInSandbox(holdFile))) {
        fs.unlinkSync(assertInSandbox(holdFile));
        transcript.push({ step: 'the restore hold\'s file deleted' });
        return true;
    }
    return false;
}

// ---- cleanup: everything of the probe's, through the API --------------------------------------------------------------
const cleanupErrors = [];
async function cleanup(label) {
    await setDefaultRoles(`${label}: no default roles`, []);
    const roles = ((await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || []).filter((r) => r.Name.startsWith(PREFIX));
    // The probe's users: out with Keep, which writes nothing.
    for (const u of (await usersRows()).filter((x) => x.State !== 'NotManaged' && x.Name.startsWith(PROBE_USER))) {
        await step(`${label}: unmanage ${u.Name} (Keep)`, 'POST', `/AccessRoles/Admin/Users/${u.UserId}/Unmanage?confirmNoFullAdmin=true`, { Choice: 'Keep' });
    }
    for (const role of roles) {
        await step(`${label}: delete ${role.Name}`, 'DELETE', `/AccessRoles/Admin/Roles/${role.Id}?onUnmanage=Keep&confirmNoFullAdmin=true`);
    }
    for (const u of ((await request('GET', '/Users', 'admin')).json || []).filter((x) => x.Name.startsWith(PROBE_USER))) {
        own.set(idN(u.Id), u.Name);
        // One user that can't be deleted doesn't stop the rest of the cleanup.
        try {
            await deleteOwnUser(u.Id);
        } catch (e) {
            cleanupErrors.push(`${label}: ${e.message}`);
        }
    }
}

let configurationToRestore = null;

async function main() {
    const info = await request('GET', '/System/Info/Public', null);
    check('the sandbox answers, Jellyfin 10.11.5', info.status === 200 && info.json && info.json.Version === '10.11.5', { status: info.status, version: info.json && info.json.Version });
    const installed = (await request('GET', '/Plugins', 'admin')).json || [];
    const ours = installed.find((p) => idN(p.Id) === idN(plugin.guid));
    check('Access Roles is loaded and Active, at the build.yaml version', !!ours && ours.Version === plugin.version && ours.Status === 'Active', ours ? { version: ours.Version, status: ours.Status } : 'not listed');

    const configuration = await getConfiguration();
    configurationToRestore = { EnforcementEnabled: configuration.EnforcementEnabled, LibrarySyncEnabled: configuration.LibrarySyncEnabled, DriftPolicy: configuration.DriftPolicy };
    evidence.configurationBefore = configurationToRestore;
    await setConfiguration({ EnforcementEnabled: true, LibrarySyncEnabled: true, DriftPolicy: 'Reapply' });
    removeHoldFile();
    await cleanup('earlier run');
    const startSeq = await lastSeq();
    check('the sync is active, nothing held', ((await diagnostics()).LibrarySync || {}).State === 'Active', (await diagnostics()).LibrarySync && (await diagnostics()).LibrarySync.PausedBecause);

    // Every user the probe didn't create: policies and stored permissions and preferences, before.
    const others = (await request('GET', '/Users', 'admin')).json || [];
    const othersBefore = {};
    for (const u of others) othersBefore[u.Id] = { policy: stable((await request('GET', `/Users/${u.Id}`, 'admin')).json.Policy), stored: stable(Object.fromEntries(Object.entries(await dumpUser(u.Id)).filter(([k]) => !k.startsWith('User.')))) };

    const folders = (await virtualFolders()).filter((l) => l.ItemId);
    check('the sandbox has three libraries or more', folders.length >= 3, names(folders));
    const [libA, libB, libC] = folders;
    evidence.libraries = { a: libA.Name, b: libB.Name, c: libC.Name, count: folders.length };
    const kidR = await step('Kid: Only the first library', 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}Kid`, Libraries: { Mode: 'Only', Refs: [{ Id: libA.ItemId }] } });
    const kidRole = kidR.json && kidR.json.Role;
    check('the role Kid is created', kidR.status === 200 && !!kidRole, { status: kidR.status });
    const onlyA = (views) => !!views && views.includes(libA.Name) && !views.includes(libB.Name) && !views.includes(libC.Name);

    // ---------------------------------------------------------------------------------------------------------------
    // 1. Created under default roles while the database is busy.
    const lockedCreation = async (name, label, defaulted = true) => {
        // The users the rules manage before this one (a dry run writes nothing): the manual sync counts the new user too
        // once their default roles are saved.
        const managedBefore = ((await sync('?dryRun=true')).json || {}).ManagedUsers;
        const locker = startLocker(name, HOLD_MS);
        const ready = await locker.waitFor((l) => l === 'ready', 15000);
        if (!ready) throw new Error(`the locker didn't start: ${locker.stderr().slice(0, 400)}`);
        const startedAt = Date.now();
        const creating = createOwnUser(name).then((r) => Object.assign(r, { doneAt: Date.now() }));
        const lockedLine = await locker.waitFor((l) => !!(parsed(l) || {}).locked, 30000);
        const lock = parsed(lockedLine) || {};
        // While the request waits for the database: a store change (its run 2 s later) and a manual sync, once Access
        // Roles' consumer has saved the new user's default roles (its mark is set before that save), so the sync meets a
        // user who is managed and being created.
        let manual = null;
        let stored = null;
        let manualSentAt = null;
        let manualDoneAt = null;
        const assigned = lock.locked && defaulted
            ? await until('the default roles saved', async () => (await usersRows()).some((r) => r.Name === name && r.State !== 'NotManaged'), 3000, 20)
            : null;
        if (lock.locked && kidRole) {
            const role = (await request('GET', `/AccessRoles/Admin/Roles/${kidRole.Id}`, 'admin')).json || {};
            stored = await step(`${label}: Kid saved while the database is locked`, 'PUT', `/AccessRoles/Admin/Roles/${kidRole.Id}`, { Name: role.Name, Description: `${label} ${Date.now()}`, Locks: [], Libraries: { Mode: 'Only', Refs: [{ Id: libA.ItemId }] }, IfUpdatedAt: role.UpdatedUtc });
            manualSentAt = Date.now();
            manual = await sync();
            manualDoneAt = Date.now();
        }
        const created = await creating;
        const exitCode = await locker.exited;
        const released = parsed(locker.lines.find((l) => !!(parsed(l) || {}).released) || '') || {};
        // The attempt's timeline, in ms from the creation's request: the lock, the manual sync (sent, its run's start as the
        // server reports it, answered), the creation's answer.
        const at = (t) => (Number.isFinite(t) ? t - startedAt : null);
        const timing = { lockedAt: at(lock.at), releasedAt: at(released.at), manualSentAt: at(manualSentAt), manualRunStartedAt: manual && manual.json && manual.json.StartedUtc ? at(Date.parse(manual.json.StartedUtc)) : null, manualRunMs: manual && manual.json ? manual.json.DurationMs : null, manualAnsweredAt: at(manualDoneAt), createdAt: at(created.doneAt) };
        timing.assignedAfterLockMs = assigned && assigned.ok ? assigned.ms : null;
        return { timing, created, lock, released, exitCode, stored, manual, managedBefore, stderr: locker.stderr().slice(0, 400) };
    };

    await setDefaultRoles('Kid is the default role of new users', [kidRole.Id]);
    const lockRuns = [];
    for (let i = 1; i <= opts.attempts; i++) {
        const run = await lockedCreation(`${PROBE_USER}lock${i}`, `attempt ${i}`);
        const user = run.created.user;
        const entry = { attempt: i, status: run.created.status, createMs: run.created.ms, lockedAfterRowMs: run.lock.afterRowMs, activityAfterRowMs: run.lock.activityAfterRowMs, activityAtLock: run.lock.activityAtLock, heldMs: run.released.heldMs, lockerExit: run.exitCode, storeChange: run.stored && run.stored.status, manualSync: run.manual && run.manual.status };
        entry.timing = run.timing;
        if (user) {
            entry.managedBefore = run.managedBefore;
            entry.managedAtManualSync = run.manual && run.manual.json ? run.manual.json.ManagedUsers : null;
            entry.assignedAtManualSync = Number.isInteger(entry.managedBefore) && Number.isInteger(entry.managedAtManualSync) && entry.managedAtManualSync === entry.managedBefore + 1;
            entry.beingCreatedInManualSync = !!(run.manual && run.manual.json && (run.manual.json.UsersBeingCreated || []).some((x) => idN(x) === idN(user.Id)));
            entry.writtenByManualSync = !!(run.manual && run.manual.json && (run.manual.json.Users || []).some((u) => idN(u.UserId) === idN(user.Id)));
            const withPassword = await signIn(user);
            const empty = await signIn(user, false);
            entry.signInStatus = withPassword.status;
            entry.emptySignInStatus = empty.status;
            const written = await until('the default role written', async () => sameAccess(await policyOf(user), false, [libA.ItemId]), 15000);
            entry.writtenMs = written.ms;
            entry.written = written.ok;
            entry.views = withPassword.status === 200 ? await viewsOf(user) : null;
        } else {
            entry.stderr = run.stderr;
        }
        lockRuns.push(entry);
    }
    evidence.lockedCreations = lockRuns;
    const lockOk = (r) => r.status === 200 && r.lockerExit === 0 && r.heldMs >= HOLD_MS - 50 && r.createMs >= HOLD_MS - 500 && r.signInStatus === 200 && r.emptySignInStatus === 401 && r.written && onlyA(r.views) && r.assignedAtManualSync && r.beingCreatedInManualSync && !r.writtenByManualSync && r.storeChange === 200
        && !!r.timing && r.timing.manualAnsweredAt !== null && r.timing.manualAnsweredAt < r.timing.releasedAt && r.timing.releasedAt < r.timing.createdAt;
    check(`${opts.attempts} creations under default roles with the database locked 4 s from the moment the UserCreated activity entry appeared (a store change and a manual sync meanwhile, the sync once the default roles were saved, answered before the lock was released): every one 200, the manual sync counts the user as managed, lists them as being created and writes nobody, the password signs in (200), an empty one is refused (401), the default role's library written and the only one /UserViews lists`,
        lockRuns.length === opts.attempts && lockRuns.every(lockOk), { passed: lockRuns.filter(lockOk).length, of: lockRuns.length });

    // The control: the same lock without default roles (the core's own creation under a busy database).
    await setDefaultRoles('no default roles (the control)', []);
    const control = await lockedCreation(`${PROBE_USER}lockcontrol`, 'the control', false);
    let controlSignIn = null;
    if (control.created.user) controlSignIn = await signIn(control.created.user);
    evidence.lockControl = { status: control.created.status, createMs: control.created.ms, heldMs: control.released.heldMs, signIn: controlSignIn && controlSignIn.status };
    check('the control without default roles: 200, the password signs in', control.created.status === 200 && controlSignIn && controlSignIn.status === 200, evidence.lockControl);

    // ---------------------------------------------------------------------------------------------------------------
    // 2. Create, then sign in at once.
    await setDefaultRoles('Kid is the default role of new users again', [kidRole.Id]);
    const signInRuns = [];
    const signInUsers = [];
    for (let i = 1; i <= opts.signins; i++) {
        const created = await createOwnUser(`${PROBE_USER}signin${i}`);
        const entry = { attempt: i, create: created.status };
        if (created.user) {
            const answered = await signIn(created.user);
            entry.signIn = answered.status;
            entry.answered = answered.user && answered.user.Policy ? (answered.user.Policy.EnableAllFolders ? 'all' : answered.user.Policy.EnabledFolders.length) : null;
            entry.answeredOnlyA = !!answered.user && sameAccess(answered.user.Policy, false, [libA.ItemId]);
            entry.views = answered.status === 200 ? await viewsOf(created.user) : null;
            signInUsers.push(created.user);
        }
        signInRuns.push(entry);
    }
    evidence.createThenSignIn = signInRuns;
    await delay(4500);
    for (const [i, user] of signInUsers.entries()) {
        signInRuns[i].enforcedAfterFirstRun = sameAccess(await policyOf(user), false, [libA.ItemId]);
        signInRuns[i].viewsAfterFirstRun = await viewsOf(user);
    }

    const signInOk = (r) => r.create === 200 && r.signIn === 200 && r.answeredOnlyA && onlyA(r.views) && r.enforcedAfterFirstRun && onlyA(r.viewsAfterFirstRun);
    check(`${opts.signins} creations each followed at once by the new user's sign-in: 200 and 200, the response's User and /UserViews only the default role's library, still so after the first run`,
        signInRuns.length === opts.signins && signInRuns.every(signInOk), { passed: signInRuns.filter(signInOk).length, of: signInRuns.length });

    // ---------------------------------------------------------------------------------------------------------------
    // 3. Restore of a user created under default roles (decided: no libraries).
    const restoreUser = await createOwnUserOrThrow(`${PROBE_USER}restore`);
    const restoreWritten = await until('the restore user written', async () => sameAccess(await policyOf(restoreUser), false, [libA.ItemId]), 15000);
    await signIn(restoreUser);
    await settle();
    const unmanageDry = await step('Unmanage (Restore), dry run', 'POST', `/AccessRoles/Admin/Users/${restoreUser.Id}/Unmanage?dryRun=true`, { Choice: 'Restore' });
    const leavingOf = (answer) => ((((answer.json || {}).Preview || {}).Users || []).find((u) => idN(u.UserId) === idN(restoreUser.Id)) || {}).LeavesManagement || null;
    const unmanageLeaving = leavingOf(unmanageDry);
    await setDefaultRoles('no default roles (so Kid may be deleted)', []);
    const deleteDry = await step('delete Kid (Restore), dry run', 'DELETE', `/AccessRoles/Admin/Roles/${kidRole.Id}?dryRun=true&onUnmanage=Restore`);
    const deleteLeaving = leavingOf(deleteDry);
    evidence.restorePreviews = { unmanage: unmanageLeaving, delete: deleteLeaving && { note: deleteLeaving.Note, createdUnderDefaultRoles: deleteLeaving.CreatedUnderDefaultRoles } };
    check('the Unmanage and role-delete previews of Restore say "Restores to no libraries: created under default roles." and give no library',
        restoreWritten.ok && unmanageDry.status === 200 && deleteDry.status === 200 && !!unmanageLeaving && unmanageLeaving.Note === NO_LIBRARIES_NOTE && unmanageLeaving.CreatedUnderDefaultRoles === true
            && !!unmanageLeaving.After && unmanageLeaving.After.EnableAllFolders === false && (unmanageLeaving.After.EnabledFolders || []).length === 0 && !!deleteLeaving && deleteLeaving.Note === NO_LIBRARIES_NOTE,
        evidence.restorePreviews);
    const seqRestore = await lastSeq();
    const restored = await step('Unmanage (Restore)', 'POST', `/AccessRoles/Admin/Users/${restoreUser.Id}/Unmanage`, { Choice: 'Restore' });
    const none = await until('the restore user has no library', async () => sameAccess(await policyOf(restoreUser), false, []), 15000);
    const restoredViews = await viewsOf(restoreUser);
    const restoredLine = (await auditSince(seqRestore, 'LibraryAccessRestored')).find((e) => idN(e.TargetUserId) === idN(restoreUser.Id));
    evidence.restore = { status: restored.status, ms: none.ms, views: restoredViews, line: !!restoredLine };
    check('Restore writes no library (never every library), /UserViews lists none of A, B, C, audited LibraryAccessRestored',
        restored.status === 200 && none.ok && !!restoredViews && ![libA.Name, libB.Name, libC.Name].some((n) => restoredViews.includes(n)) && !!restoredLine, evidence.restore);

    // ---------------------------------------------------------------------------------------------------------------
    // 4. The restore hold.
    const held = await createOwnUserOrThrow(`${PROBE_USER}held`);
    const heldAdmin = await createOwnUserOrThrow(`${PROBE_USER}admin`);
    await setOwnPolicy(heldAdmin, { IsAdministrator: true });
    await putUser(`${held.Name} gets Kid`, held, { RoleIds: [kidRole.Id] });
    await putUser(`${heldAdmin.Name} gets Kid`, heldAdmin, { RoleIds: [kidRole.Id] });
    const bothWritten = await until('the held user and the administrator written', async () => sameAccess(await policyOf(held), false, [libA.ItemId]) && sameAccess(await policyOf(heldAdmin), false, [libA.ItemId]), 15000);
    check('a managed user and a managed administrator, written Kid\'s library', bothWritten.ok, { ms: bothWritten.ms });
    await settle();
    writeHoldFile();
    const pausedBy = ((await diagnostics()).LibrarySync || {}).PausedBecause;
    await setOwnPolicy(held, { EnableAllFolders: true });
    await setOwnPolicy(heldAdmin, { EnableAllFolders: true });
    const drifted = sameAccess(await policyOf(held), true, []) && sameAccess(await policyOf(heldAdmin), true, []);
    const seqHold = await lastSeq();
    const refused = await signIn(held);
    const deviceLeft = await hasDevice(held);
    const adminIn = await signIn(heldAdmin);
    const refusedLine = (await auditSince(seqHold, 'SignInRefused')).find((e) => idN(e.TargetUserId) === idN(held.Id));
    evidence.hold = { pausedBy, drifted, refused: { status: refused.status, retryAfter: refused.retryAfter, deviceLeft }, admin: adminIn.status, line: refusedLine && { reason: refusedLine.Details.Reason, held: refusedLine.Details.Held, tokenRevoked: refusedLine.Details.TokenRevoked } };
    check('held (StoreRestored): the managed user whose access drifted to every library is refused (503, Retry-After 60, the token revoked: no device left), audited SignInRefused (Reason StoreRestored, Held, TokenRevoked true); the administrator signs in',
        pausedBy === 'StoreRestored' && drifted && refused.status === 503 && refused.retryAfter === '60' && !deviceLeft && adminIn.status === 200
            && !!refusedLine && refusedLine.Details.Reason === 'StoreRestored' && refusedLine.Details.Held === true && refusedLine.Details.TokenRevoked === true,
        evidence.hold);

    await setConfiguration({ LibrarySyncEnabled: false });
    const adminPause = await signIn(held);
    const untouched = sameAccess(await policyOf(held), true, []);
    await setConfiguration({ LibrarySyncEnabled: true });
    evidence.adminPause = { status: adminPause.status, untouched };
    check('an admin\'s pause (the sync switched off): the same user signs in (200), library access untouched', adminPause.status === 200 && untouched, evidence.adminPause);

    const early = await sync('?confirmRestore=true');
    const stillHeld = fs.existsSync(holdFile) && ((await diagnostics()).LibrarySync || {}).PausedBecause === 'StoreRestored';
    const dryRun = await sync('?dryRun=true');
    const dryRow = dryRun.json && (dryRun.json.Users || []).find((u) => idN(u.UserId) === idN(held.Id));
    const noted = fs.existsSync(holdFile) && /DryRunUtc/.test(fs.readFileSync(holdFile, 'utf8'));
    const seqConfirm = await lastSeq();
    // The confirmation names the plan seen (0.9.16.0, the pre-1.0 review): the dry run's PlanToken as ifPlan.
    const confirmed = await sync('?confirmRestore=true&ifPlan=' + encodeURIComponent((dryRun.json || {}).PlanToken || ''));
    const heldBack = await until('the held user written back', async () => sameAccess(await policyOf(held), false, [libA.ItemId]), 15000);
    const resumed = (await auditSince(seqConfirm, 'LibrarySyncResumed')).length;
    const afterSignIn = await signIn(held);
    evidence.confirm = { early: { status: early.status, code: early.json && early.json.Code, stillHeld }, dryRun: { status: dryRun.status, paused: dryRun.json && dryRun.json.Paused, row: dryRow && `${dryRow.Action}/${dryRow.Reason}`, noted }, confirmed: { status: confirmed.status, paused: confirmed.json && confirmed.json.Paused, holdFile: fs.existsSync(holdFile), ms: heldBack.ms, resumed }, signIn: afterSignIn.status };
    check('confirmRestore before a dry run: 409 DryRunRequired, still held; the dry run (noted in the hold file) shows the user\'s write; the confirmation lifts the hold (its file gone, LibrarySyncResumed) and writes the user back; they sign in',
        early.status === 409 && early.json && early.json.Code === 'DryRunRequired' && stillHeld && dryRun.status === 200 && (dryRun.json || {}).Paused === 'StoreRestored' && !!dryRow && noted
            && confirmed.status === 200 && !(confirmed.json || {}).Paused && !fs.existsSync(holdFile) && heldBack.ok && resumed >= 1 && afterSignIn.status === 200,
        evidence.confirm);

    // ---------------------------------------------------------------------------------------------------------------
    // 5. Every user the probe didn't create: never written.
    const changed = [];
    for (const u of others) {
        const policy = stable((await request('GET', `/Users/${u.Id}`, 'admin')).json.Policy);
        const stored = stable(Object.fromEntries(Object.entries(await dumpUser(u.Id)).filter(([k]) => !k.startsWith('User.'))));
        if (policy !== othersBefore[u.Id].policy || stored !== othersBefore[u.Id].stored) changed.push(u.Name);
    }
    check('no user the probe didn\'t create was written: their policies and their stored permissions and preferences are as before', changed.length === 0, { users: others.length, changed });
    const lines = await auditSince(startSeq);
    evidence.audit = Object.fromEntries([...new Set(lines.map((e) => e.Action))].map((x) => [x, lines.filter((e) => e.Action === x).length]));
    evidence.hooks = ((await diagnostics()).LibrarySync || {}).Hooks;
}

let failure = null;
try {
    await main();
} catch (e) {
    failure = e.stack || String(e);
} finally {
    // Everything of the probe's goes, and the settings are as the probe found them.
    const cleanupResult = {};
    try {
        for (const child of lockers) child.kill();
        cleanupResult.holdFileRemoved = removeHoldFile();
        await cleanup('cleanup');
        if (configurationToRestore) await setConfiguration(configurationToRestore);
        cleanupResult.probeLeft = {
            roles: ((await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || []).filter((r) => r.Name.startsWith(PREFIX)).length,
            managed: (await usersRows()).filter((u) => u.State !== 'NotManaged').length,
            users: ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Name.startsWith(PROBE_USER)).length,
            defaultRoles: ((await diagnostics()).Records || {}).DefaultRoles,
            holdFile: fs.existsSync(holdFile),
            paused: ((await diagnostics()).LibrarySync || {}).PausedBecause || null
        };
        const configuration = await getConfiguration();
        cleanupResult.configurationRestored = !configurationToRestore || Object.entries(configurationToRestore).every(([k, v]) => configuration[k] === v);
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + (e.stack || String(e));
    }
    evidence.cleanup = cleanupResult;
    if (cleanupErrors.length) failure = (failure ? failure + ' | ' : '') + 'cleanup: ' + cleanupErrors.join('; ');
    if (!failure) {
        const left = cleanupResult.probeLeft || {};
        check('afterwards nothing of the probe\'s is left (roles, managed users, users, default roles, the hold file), the sync not held, the settings as before',
            left.roles === 0 && left.managed === 0 && left.users === 0 && left.defaultRoles === 0 && !left.holdFile && left.paused !== 'StoreRestored' && cleanupResult.configurationRestored, cleanupResult);
        const log = logSinceStart().filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text) && (l.level === 'WRN' || l.level === 'ERR' || l.level === 'FTL'));
        // Expected: the 10.11.5 race both ways (a post-write check's finding; a failed save written again from the stored row,
        // or that row written back, seen in earlier runs of the sync probe), the hold's refused sign-in, and the
        // early confirmation; and since 0.9.13 the recovery probe's Release all locks, and since 0.9.15 the data probe's imports, restore and
        // stale remap (the kit logs an admin's import or restore as a warning, and a remap refused for its plan), which run
        // before this probe on the battery's start (scripts/README.md, the recovery and data probes' sections).
        const expected = log.filter((l) => l.level === 'WRN' && (/the server enforces .* \((Reverted|Changed)\)/.test(l.text) || /refused the sign-in of user .*: the library sync is held \("?StoreRestored"?\)/.test(l.text) || /library sync stays held: confirmRestore needs a dry run/.test(l.text)
            || /write of the library access of user [0-9a-f-]+ failed|writes the library access of user [0-9a-f-]+ again in|wrote the stored policy of user [0-9a-f-]+ back instead/.test(l.text)
            || /An admin released Access Roles' native locks of \d+ users/.test(l.text)
            || /An admin "(imported|restored a snapshot of)" the Access Roles data \((Merge|Replace)\)/.test(l.text)
            || /Remap by name was refused: its plan changed since the dry run/.test(l.text)));
        evidence.log = { warnings: log.length, expected: expected.length };
        check('the log has no warning or error of Access Roles but the expected ones (a post-write check\'s finding, the hold\'s refusal, the early confirmation, the recovery probe\'s Release all locks, the data probe\'s imports, restore and stale remap)', log.length === expected.length, log.filter((l) => !expected.includes(l)).map((l) => l.text.slice(0, 240)));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
