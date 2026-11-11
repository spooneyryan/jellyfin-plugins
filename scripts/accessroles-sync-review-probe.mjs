#!/usr/bin/env node
// Races and edge cases of Access Roles' library sync in a running sandbox:
//   1. POST Sync racing an admin's Dashboard-style policy save (GET /Users/{id}, then POST /Users/{id}/Policy with the user
//      disabled and a field changed) at offsets -4..20 ms: every save the server accepted keeps its fields, in the server's
//      cache (GET /Users/{id}) and in its database; the writer never changes IsAdministrator or IsDisabled. Since 0.9.4
//      the admin's save is pre-merged with the rules' result under the user's policy lock, so what the sync has to
//      write comes from the role: its library changes before each attempt, and the race starts before the change's
//      debounced run.
//   2. A rename (refreshLibrary=true, then refreshLibrary=false and a scan while the sync is paused) of a library an
//      AllExcept role and a user's Remove leave out: those users never see it (their /UserViews polled every ~50 ms), and
//      an Only user of it gets it back once the rename is re-pointed.
//   3. DriftPolicy=ReportOnly, a user drifted by a native add: a deleted library's id goes, the admin's add stays, the
//      library created again under the same name isn't visible.
//   4. Excluded with Keep, the native access edited while unmanaged, managed again: a FirstWrite, not drift.
// It writes only what is its own: its users (syncrace-..., random passwords kept in memory for their sign-ins, never
// printed or saved), their policies (an admin's native edit, the race's Dashboard save), its roles ("Sync race probe ..."), its
// libraries ("Sync race probe ...", over empty folders it makes under .sandbox/<name>/syncrace), a library scan
// (POST /Library/Refresh, for the unrefreshed rename) and the plugin's own settings (put back in a finally). It reads the
// sandbox's database read-only through node:sqlite (never the password column), and its audit folder and log.
// Node 22 built-ins only. Loopback only, never port 8096. Tokens come from credentials.json and are never printed.
//
//   node scripts/accessroles-sync-review-probe.mjs --sandbox access [--race 26] [--out result.json]
//
// Start the sandbox first (scripts/README.md, this probe's section). Prints one JSON result (pass, checks, transcript,
// evidence) and exits with 0 when every check passed.

import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PLUGIN = 'AccessRoles';
const PREFIX = 'Sync race probe ';
const PROBE_USER = 'syncrace-';
const SECRET = `${PREFIX}Secret`;
const PROBE_LIBRARY = `${PREFIX}Lib`;
const OFFSETS = [-4, -2, 0, 1, 2, 3, 4, 5, 6, 8, 10, 12, 14, 16, 18, 20];

function usage() {
    return 'usage: node scripts/accessroles-sync-review-probe.mjs --sandbox <name> [--race <attempts>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, race: 26 };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--race') o.race = Number(argv[++i]);
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox) || !Number.isInteger(o.race) || o.race < 0 || o.race > 64) throw new Error(usage());
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

// The sandbox's files: the database (read only), the log, and the folders of the probe's own libraries. Every one must be
// inside .sandbox, never under ProgramData\Jellyfin.
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
const dbFile = path.join(dataDir, 'data', 'jellyfin.db');
const secretFolder = path.join(sandboxRoot, 'syncrace', 'secret');
const libraryFolder = path.join(sandboxRoot, 'syncrace', 'library');
function assertInSandbox(p) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), path.resolve(p));
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(path.resolve(p))) throw new Error(`refusing to touch ${p}`);
    return path.resolve(p);
}
for (const p of [dbFile, secretFolder, libraryFolder, path.join(sandboxRoot, 'log')]) assertInSandbox(p);

const checks = [];
const transcript = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authHeader(token, deviceId = 'accessroles-sync-review-probe') {
    return `MediaBrowser Client="accessroles-sync-review-probe", Device="sandbox", DeviceId="${deviceId}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// A request as the admin (credentials.json), as one of the probe's users (a token from its sign-in), or anonymous.
function request(method, p, user = 'admin', body = null, deviceId = undefined) {
    return new Promise((resolve, reject) => {
        const token = user === null ? null : user === 'admin' ? credentials.admin.token : tokens.get(user);
        if (user !== null && !token) throw new Error(`no token for ${user}`);
        const headers = { Authorization: authHeader(token, deviceId), Accept: 'application/json' };
        if (body !== null) headers['Content-Type'] = 'application/json';
        const req = http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let json = null;
                try { json = text && /json/.test(res.headers['content-type'] || '') ? JSON.parse(text) : null; } catch { json = null; }
                resolve({ status: res.statusCode, type: res.headers['content-type'] || '', headers: res.headers, text, json });
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
// The post-write check runs about 5 s after each write; a native edit of the same user before it would look
// like the 10.11.5 race's revert to it, so the walkthrough lets it pass first.
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
    transcript.push({ step: `create the probe's user ${name}`, curl: `curl -X POST "http://127.0.0.1:${port}${baseUrl}/Users/New" -H "Authorization: MediaBrowser Token=<admin>" -d '{"Name":"${name}","Password":"<random>"}'`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.Id) throw new Error(`POST /Users/New ${name}: ${r.status}`);
    own.set(idN(r.json.Id), name);
    secrets.set(idN(r.json.Id), password);
    return r.json;
}

// A policy save of one of the probe's users, as an admin's on the stock page; the race's save may lose (500), so the
// answer is returned, not checked.
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

// A native edit of one of the probe's users: the full policy with some fields changed.
async function setOwnPolicy(user, changes) {
    const current = (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
    const r = await postOwnPolicy(user.Id, { ...current, ...changes });
    if (r.status !== 204) throw new Error(`set the native policy of ${user.Name}: ${r.status}`);
    return (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
}

// The probe's user signs in: its password is only ever in this request's body; the token stays in memory.
async function signIn(user) {
    const password = secrets.get(idN(user.Id));
    if (!password) throw new Error(`no password for ${user.Name}`);
    const r = await request('POST', '/Users/AuthenticateByName', null, { Username: user.Name, Pw: password }, `syncrace-${idN(user.Id).slice(0, 8)}`);
    transcript.push({ step: `${user.Name} signs in`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.AccessToken) throw new Error(`sign-in of ${user.Name}: ${r.status}`);
    tokens.set(idN(user.Id), r.json.AccessToken);
}

const policyOf = async (user) => (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
const sameAccess = (policy, all, folders) => policy && !!policy.EnableAllFolders === all && (all || sameIds(policy.EnabledFolders, folders));
const viewsOf = async (user) => {
    const r = await request('GET', `/UserViews?userId=${user.Id}`, idN(user.Id));
    return r.status === 200 && r.json ? names(r.json.Items) : null;
};

// ---- the probe's own libraries: empty folders under .sandbox/<name>/syncrace -----------------------------------------
const isOwnLibrary = (name) => name.toLowerCase().startsWith(PREFIX.toLowerCase());
async function addOwnLibrary(name, folder) {
    if (!isOwnLibrary(name)) throw new Error(`refusing to add the library ${name}`);
    fs.mkdirSync(assertInSandbox(folder), { recursive: true });
    const query = `/Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=homevideos&paths=${encodeURIComponent(assertInSandbox(folder))}&refreshLibrary=true`;
    const r = await step(`add the probe's library ${name}`, 'POST', query, { LibraryOptions: { Enabled: true, EnableRealtimeMonitor: false, EnableInternetProviders: false, MetadataSavers: [], TypeOptions: [] } });
    if (r.status !== 204) throw new Error(`add ${name}: ${r.status}`);
}

async function removeOwnLibrary(name) {
    if (!isOwnLibrary(name)) throw new Error(`refusing to remove the library ${name}`);
    return step(`remove the probe's library ${name}`, 'DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(name)}&refreshLibrary=true`);
}

async function renameOwnLibrary(name, newName, refresh) {
    if (!isOwnLibrary(name) || !isOwnLibrary(newName)) throw new Error(`refusing to rename the library ${name}`);
    return step(`rename the probe's library ${name} to ${newName} (refreshLibrary=${refresh})`, 'POST', `/Library/VirtualFolders/Name?name=${encodeURIComponent(name)}&newName=${encodeURIComponent(newName)}&refreshLibrary=${refresh ? 'true' : 'false'}`);
}

const virtualFolders = async () => (await request('GET', '/Library/VirtualFolders', 'admin')).json || [];
const arLibraries = async () => (await request('GET', '/AccessRoles/Admin/Libraries', 'admin')).json || [];

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

// PermissionKind: IsAdministrator 0, IsDisabled 2, EnableAllFolders 16; PreferenceKind EnabledFolders 5.
const storedFolders = (stored) => (stored['Preference.5'] || '').split(',').filter((x) => x);

// ---- the audit log and the server log ------------------------------------------------------------------------------
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

const diagnostics = async () => (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).json || {};
const sync = (query = '') => step(`POST Sync${query}`, 'POST', `/AccessRoles/Admin/Sync${query}`);
const usersRow = async (user) => ((await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || []).find((u) => idN(u.UserId) === idN(user.Id));
const putUser = async (label, user, body, query = '') => step(label, 'PUT', `/AccessRoles/Admin/Users/${user.Id}${query}`, { ...body, IfUpdatedAt: (await usersRow(user) || {}).UpdatedUtc });

// ---- cleanup: everything of the probe's, through the API --------------------------------------------------------------
async function cleanup(label) {
    const users = (await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || [];
    for (const u of users.filter((x) => x.Name.startsWith(PROBE_USER) && x.State !== 'NotManaged')) {
        await step(`${label}: unmanage ${u.Name} (Keep)`, 'POST', `/AccessRoles/Admin/Users/${u.UserId}/Unmanage?confirmNoFullAdmin=true`, { Choice: 'Keep' });
    }
    const roles = (await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || [];
    for (const role of roles.filter((r) => r.Name.startsWith(PREFIX))) {
        await step(`${label}: delete ${role.Name}`, 'DELETE', `/AccessRoles/Admin/Roles/${role.Id}?onUnmanage=Keep&confirmNoFullAdmin=true`);
    }
    for (const v of (await virtualFolders()).filter((x) => isOwnLibrary(x.Name))) await removeOwnLibrary(v.Name);
    for (const u of ((await request('GET', '/Users', 'admin')).json || []).filter((x) => x.Name.startsWith(PROBE_USER))) {
        own.set(idN(u.Id), u.Name);
        await deleteOwnUser(u.Id);
    }
    await sync('?confirmNoFullAdmin=false');
    for (const l of (await arLibraries()).filter((x) => x.State === 'Orphaned' && isOwnLibrary(x.Name))) {
        await step(`${label}: purge ${l.Name}`, 'POST', `/AccessRoles/Admin/Libraries/${l.Id}/Purge`);
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
    await cleanup('earlier run');
    const startSeq = await lastSeq();

    // Every user the probe didn't create: policies and stored permissions and preferences, before.
    const others = (await request('GET', '/Users', 'admin')).json || [];
    const othersBefore = {};
    for (const u of others) othersBefore[u.Id] = { policy: stable((await request('GET', `/Users/${u.Id}`, 'admin')).json.Policy), stored: stable(Object.fromEntries(Object.entries(await dumpUser(u.Id)).filter(([k]) => !k.startsWith('User.')))) };

    let folders = await virtualFolders();
    const libA = folders.find((l) => l.CollectionType === 'homevideos' && !isOwnLibrary(l.Name)) || folders.find((l) => !isOwnLibrary(l.Name));
    const libB = folders.find((l) => l !== libA && !isOwnLibrary(l.Name) && l.CollectionType === 'movies') || folders.find((l) => l !== libA && !isOwnLibrary(l.Name));
    const libC = folders.find((l) => l !== libA && l !== libB && !isOwnLibrary(l.Name) && l.CollectionType === 'tvshows') || folders.find((l) => l !== libA && l !== libB && !isOwnLibrary(l.Name));
    check('the sandbox has three libraries or more', !!libA && !!libB && !!libC, names(folders));
    await addOwnLibrary(SECRET, secretFolder);
    await addOwnLibrary(PROBE_LIBRARY, libraryFolder);
    const listed = await until('the probe libraries are listed with their items', async () => {
        const v = await virtualFolders();
        return v.find((x) => x.Name === SECRET && x.ItemId) && v.find((x) => x.Name === PROBE_LIBRARY && x.ItemId) ? v : null;
    });
    const secretId = listed.value && listed.value.find((x) => x.Name === SECRET).ItemId;
    const probeId = listed.value && listed.value.find((x) => x.Name === PROBE_LIBRARY).ItemId;
    check('the probe\'s two libraries are listed with their items', listed.ok, { secret: idN(secretId), probe: idN(probeId) });
    evidence.libraries = { a: libA.Name, b: libB.Name, c: libC.Name };
    // The library calls run the sync at once and again 2 s and 10 s later.
    await delay(11000);

    // ---------------------------------------------------------------------------------------------------------------
    // 1. The race: POST Sync against an admin's Dashboard-style policy save.
    const race = await createOwnUser(`${PROBE_USER}race`);
    const kidR = await step('Kid: Only the first library', 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}Kid`, Libraries: { Mode: 'Only', Refs: [{ Id: libA.ItemId }] } });
    const kid = kidR.json && kidR.json.Role;
    await putUser(`${race.Name} gets Kid`, race, { RoleIds: [kid.Id] });
    const raceWritten = await until('the race user is written', async () => sameAccess(await policyOf(race), false, [libA.ItemId]));
    check('the race user is managed and written', kidR.status === 200 && raceWritten.ok, { ms: raceWritten.ms });
    const attempts = [];
    const putKid = async (library) => {
        const current = (await request('GET', `/AccessRoles/Admin/Roles/${kid.Id}`, 'admin')).json;
        return step(`Kid: Only ${library === libA ? 'the first' : 'the second'} library`, 'PUT', `/AccessRoles/Admin/Roles/${kid.Id}`, { Name: kid.Name, Locks: [], Libraries: { Mode: 'Only', Refs: [{ Id: library.ItemId }] }, IfUpdatedAt: current.UpdatedUtc });
    };
    for (let i = 0; i < opts.race; i++) {
        const offset = OFFSETS[i % OFFSETS.length];
        await settle();
        await setOwnPolicy(race, { IsDisabled: false, MaxActiveSessions: 1 });

        // The rules change (the role's library flips), so the sync has a write to make; the store change's own run comes
        // 2 s later, long after the race.
        const library = i % 2 === 0 ? libB : libA;
        const flipped = await putKid(library);
        if (flipped.status !== 200) throw new Error(`PUT Kid: ${flipped.status}`);
        const page = await policyOf(race);
        const wanted = { ...page, IsDisabled: true, MaxActiveSessions: 2 + i };
        const seq = await lastSeq();
        const syncCall = () => request('POST', '/AccessRoles/Admin/Sync', 'admin');
        const adminCall = () => postOwnPolicy(race.Id, wanted);
        const [syncR, adminR] = offset < 0
            ? await Promise.all([delay(-offset).then(syncCall), adminCall()])
            : await Promise.all([syncCall(), delay(offset).then(adminCall)]);
        await delay(6500);
        const cached = await policyOf(race);
        const stored = await dumpUser(race.Id);
        const row = syncR.json && (syncR.json.Users || []).find((u) => idN(u.UserId) === idN(race.Id));
        const lines = (await auditSince(seq)).filter((e) => idN(e.TargetUserId) === idN(race.Id));
        const accepted = adminR.status === 204;
        attempts.push({
            offset,
            sync: syncR.status,
            action: row && `${row.Action}/${row.Reason}/${row.Outcome}`,
            saveFailed: lines.some((e) => e.Details && e.Details.SaveFailed === true),
            committedFields: row ? row.CommittedFields : [],
            admin: adminR.status,
            cacheKept: accepted ? cached.IsDisabled === true && cached.MaxActiveSessions === 2 + i : null,
            databaseKept: accepted ? stored['Permission.2'] === '1' && stored['User.MaxActiveSessions'] === String(2 + i) : null,
            administrator: cached.IsAdministrator === true || stored['Permission.0'] === '1',
            role: library === libA ? 'a' : 'b',
            library: cached.EnableAllFolders ? 'all' : ids(cached.EnabledFolders).map((x) => (x === idN(libA.ItemId) ? 'a' : x === idN(libB.ItemId) ? 'b' : x)).join(','),
            stored: storedFolders(stored).map((x) => (idN(x) === idN(libA.ItemId) ? 'a' : idN(x) === idN(libB.ItemId) ? 'b' : x)).join(',')
        });
    }
    const restoredKid = await putKid(libA);
    if (restoredKid.status !== 200) throw new Error(`PUT Kid back: ${restoredKid.status}`);
    evidence.race = {
        attempts,
        accepted: attempts.filter((a) => a.admin === 204).length,
        failedSaves: attempts.filter((a) => a.saveFailed).length,
        lost: attempts.filter((a) => a.admin === 204 && !(a.cacheKept && a.databaseKept)).length,
        // The role's library is what both the sync and the pre-merged save write: it holds in the cache and the database.
        libraryAsTheRole: attempts.filter((a) => a.library === a.role && a.stored === a.role).length
    };
    check('the race: every admin save the server accepted keeps its fields (IsDisabled, MaxActiveSessions) in the cache and the database, whatever the order; IsAdministrator never set; the library is the role\'s',
        attempts.length === opts.race && evidence.race.accepted > 0 && evidence.race.lost === 0 && attempts.every((a) => !a.administrator && a.sync === 200) && evidence.race.libraryAsTheRole === attempts.length,
        { attempts: attempts.length, accepted: evidence.race.accepted, failedSaves: evidence.race.failedSaves, lost: evidence.race.lost, libraryAsTheRole: evidence.race.libraryAsTheRole });

    // ---------------------------------------------------------------------------------------------------------------
    // 2. The rename: AllExcept [Secret] and a Remove [Secret] never see it renamed; Only [Secret] gets it once re-pointed.
    const onlyU = await createOwnUser(`${PROBE_USER}only`);
    const allButU = await createOwnUser(`${PROBE_USER}allbut`);
    const removeU = await createOwnUser(`${PROBE_USER}remove`);
    const secretR = await step('OnlySecret: Only the secret library', 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}OnlySecret`, Libraries: { Mode: 'Only', Refs: [{ Id: secretId }] } });
    const allButR = await step('AllButSecret: every library but the secret one', 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}AllButSecret`, Libraries: { Mode: 'AllExcept', Refs: [{ Id: secretId }] } });
    const allR = await step('All: every library', 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}All`, Libraries: { Mode: 'All' } });
    await putUser(`${onlyU.Name} gets OnlySecret`, onlyU, { RoleIds: [secretR.json.Role.Id] });
    await putUser(`${allButU.Name} gets AllButSecret`, allButU, { RoleIds: [allButR.json.Role.Id] });
    await putUser(`${removeU.Name} gets All, without the secret library`, removeU, { RoleIds: [allR.json.Role.Id], Libraries: { Remove: [secretId] } });
    const renameReady = await until('the three are written', async () => sameAccess(await policyOf(onlyU), false, [secretId])
        && !(await policyOf(allButU)).EnableAllFolders && !ids((await policyOf(allButU)).EnabledFolders).includes(idN(secretId))
        && !(await policyOf(removeU)).EnableAllFolders && !ids((await policyOf(removeU)).EnabledFolders).includes(idN(secretId)));
    for (const u of [onlyU, allButU, removeU]) await signIn(u);
    check('the rename\'s users are written: Only the secret library; the others everything but it', renameReady.ok, { ms: renameReady.ms });

    const polls = { [onlyU.Name]: new Set(), [allButU.Name]: new Set(), [removeU.Name]: new Set() };
    const leaks = [];
    let pollCount = 0;
    let polling = true;
    // Each user's /UserViews about every 50 ms, from before the first rename to after the last re-pointing.
    const poller = (async () => {
        while (polling) {
            const round = [onlyU, allButU, removeU];
            const views = await Promise.all(round.map(viewsOf));
            round.forEach((u, i) => {
                pollCount++;
                for (const n of views[i] || []) polls[u.Name].add(n);
                if (u !== onlyU && (views[i] || []).some((n) => n.startsWith(SECRET))) leaks.push({ user: u.Name, at: new Date().toISOString(), views: views[i].filter((n) => n.startsWith(SECRET)) });
            });
            await delay(50);
        }
    })();
    const secret2 = `${SECRET}2`;
    const secret3 = `${SECRET}3`;
    const rename2 = await renameOwnLibrary(SECRET, secret2, true);
    const onlySees2 = await until('the Only user sees the renamed library', async () => (await viewsOf(onlyU) || []).includes(secret2), 15000);
    await delay(11000);
    const rename3 = await renameOwnLibrary(secret2, secret3, false);
    await delay(11000);
    const withheld = await sync('?dryRun=true');
    await setConfiguration({ LibrarySyncEnabled: false });
    const scan = await step('scan the libraries (the unrefreshed rename gets its item)', 'POST', '/Library/Refresh');
    const scanned = await until('the renamed library has its item', async () => (await virtualFolders()).find((v) => v.Name === secret3 && v.ItemId), 90000, 500);
    await delay(3000);
    await setConfiguration({ LibrarySyncEnabled: true });
    const repointed = await sync();
    const onlySees3 = await until('the Only user sees it again', async () => (await viewsOf(onlyU) || []).includes(secret3), 15000);
    await delay(3000);
    polling = false;
    await poller;
    const finalIds = { [allButU.Name]: ids((await policyOf(allButU)).EnabledFolders), [removeU.Name]: ids((await policyOf(removeU)).EnabledFolders) };
    const newIds = [scanned.value && idN(scanned.value.ItemId), ...((await arLibraries()).filter((l) => l.Name === secret2).map((l) => idN(l.Id)))].filter((x) => x);
    evidence.rename = {
        polls: pollCount,
        leaks,
        withheldWhileWaiting: withheld.json && ids(withheld.json.WithheldLibraries),
        scanMs: scanned.ms,
        seen: Object.fromEntries(Object.entries(polls).map(([k, v]) => [k, [...v].filter((n) => n.startsWith(PREFIX))]))
    };
    check('a rename (refreshLibrary=true, then false with a scan while the sync was paused): the AllExcept role\'s and the Remove\'s users never saw it (/UserViews every ~50 ms), and it is never in their access',
        rename2.status === 204 && rename3.status === 204 && scan.status === 204 && scanned.ok && repointed.status === 200 && pollCount > 100 && leaks.length === 0
            && Object.values(finalIds).every((list) => !newIds.some((x) => list.includes(x))),
        { polls: pollCount, leaks: leaks.slice(0, 5), withheld: evidence.rename.withheldWhileWaiting });
    check('the Only user of the renamed library gets it under each new name once the rename is re-pointed (the positive control)', onlySees2.ok && onlySees3.ok, { secret2Ms: onlySees2.ms, secret3Ms: onlySees3.ms });

    // ---------------------------------------------------------------------------------------------------------------
    // 3. ReportOnly: a drifted user still loses a deleted library's id, and doesn't get it back created again.
    const drifted = await createOwnUser(`${PROBE_USER}drift`);
    const kidLibR = await step('KidLib: Only the first library and the probe library', 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}KidLib`, Libraries: { Mode: 'Only', Refs: [{ Id: libA.ItemId }, { Id: probeId }] } });
    await putUser(`${drifted.Name} gets KidLib`, drifted, { RoleIds: [kidLibR.json.Role.Id] });
    const driftWritten = await until('the drift user is written', async () => sameAccess(await policyOf(drifted), false, [libA.ItemId, probeId]));
    await settle();
    await setConfiguration({ DriftPolicy: 'ReportOnly' });
    await setOwnPolicy(drifted, { EnabledFolders: [libA.ItemId, probeId, libC.ItemId] });
    const reported = await sync();
    const driftRow = reported.json && reported.json.Users.find((u) => idN(u.UserId) === idN(drifted.Id));
    const driftSeq = await lastSeq();
    await removeOwnLibrary(PROBE_LIBRARY);
    const stripped = await until('the deleted library\'s id leaves the drifted user\'s access', async () => !ids((await policyOf(drifted)).EnabledFolders).includes(idN(probeId)));
    const afterDelete = ids((await policyOf(drifted)).EnabledFolders);
    const appliedLine = (await auditSince(driftSeq, 'DriftDetected')).find((e) => idN(e.TargetUserId) === idN(drifted.Id) && e.Details.Applied && (e.Details.Applied.Deleted || []).map(idN).includes(idN(probeId)));
    check('ReportOnly: the drifted user (a native add) is reported, and when a library is deleted its id leaves their access while the admin\'s add stays (DriftDetected with Applied.Deleted)',
        driftWritten.ok && driftRow && driftRow.Action === 'Drift' && names(driftRow.Kept).includes(libC.Name) && stripped.ok && afterDelete.includes(idN(libC.ItemId)) && afterDelete.includes(idN(libA.ItemId)) && !!appliedLine,
        { row: driftRow && { action: driftRow.Action, kept: names(driftRow.Kept) }, ms: stripped.ms, applied: !!appliedLine });
    await addOwnLibrary(PROBE_LIBRARY, libraryFolder);
    const recreated = await until('the library created again shows as re-created', async () => (await arLibraries()).find((l) => idN(l.Id) === idN(probeId) && l.Recreated === true), 20000);
    await delay(2500);
    await sync();
    await signIn(drifted);
    const driftViews = await viewsOf(drifted);
    check('ReportOnly: the library created again under the same name is new: not in the drifted user\'s access, not in their /UserViews',
        recreated.ok && !ids((await policyOf(drifted)).EnabledFolders).includes(idN(probeId)) && driftViews && !driftViews.includes(PROBE_LIBRARY) && driftViews.length > 0,
        { recreated: recreated.ok, views: driftViews });

    // ---------------------------------------------------------------------------------------------------------------
    // 4. A pause: Excluded with Keep, the native access edited while unmanaged, managed again: a first write.
    const paused = await createOwnUser(`${PROBE_USER}pause`);
    await putUser(`${paused.Name} gets Kid`, paused, { RoleIds: [kid.Id] });
    const pauseWritten = await until('the pause user is written', async () => sameAccess(await policyOf(paused), false, [libA.ItemId]));
    await settle();
    const excluded = await putUser(`${paused.Name} excluded, Keep`, paused, { RoleIds: [kid.Id], State: 'Excluded' }, '?onUnmanage=Keep');
    await setOwnPolicy(paused, { EnabledFolders: [libA.ItemId, libB.ItemId, libC.ItemId] });
    const resumed = await putUser(`${paused.Name} managed again`, paused, { RoleIds: [kid.Id], State: 'Managed' });
    const dry = await sync('?dryRun=true');
    const dryRow = dry.json && dry.json.Users.find((u) => idN(u.UserId) === idN(paused.Id));
    const pauseSeq = await lastSeq();
    const real = await sync();
    const realRow = real.json && real.json.Users.find((u) => idN(u.UserId) === idN(paused.Id));
    check('a user managed again after Excluded with Keep and a native edit is a FirstWrite (not drift) under ReportOnly: written, no DriftDetected',
        pauseWritten.ok && excluded.status === 200 && resumed.status === 200 && dryRow && dryRow.Action === 'Write' && dryRow.Reason === 'FirstWrite'
            && realRow && realRow.Outcome === 'Written' && sameAccess(await policyOf(paused), false, [libA.ItemId])
            && !(await auditSince(pauseSeq, 'DriftDetected')).some((e) => idN(e.TargetUserId) === idN(paused.Id)),
        { dry: dryRow && `${dryRow.Action}/${dryRow.Reason}`, real: realRow && `${realRow.Action}/${realRow.Reason}/${realRow.Outcome}` });
    await setConfiguration({ DriftPolicy: 'Reapply' });

    // Every user the probe didn't create: never written.
    const changed = [];
    for (const u of others) {
        const policy = stable((await request('GET', `/Users/${u.Id}`, 'admin')).json.Policy);
        const stored = stable(Object.fromEntries(Object.entries(await dumpUser(u.Id)).filter(([k]) => !k.startsWith('User.'))));
        if (policy !== othersBefore[u.Id].policy || stored !== othersBefore[u.Id].stored) changed.push(u.Name);
    }
    check('no user the probe didn\'t create was written: their policies and their stored permissions and preferences are as before', changed.length === 0, { users: others.length, changed });
    const lines = await auditSince(startSeq);
    evidence.audit = Object.fromEntries([...new Set(lines.map((e) => e.Action))].map((x) => [x, lines.filter((e) => e.Action === x).length]));
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
        await cleanup('cleanup');
        if (configurationToRestore) await setConfiguration(configurationToRestore);
        cleanupResult.probeLeft = {
            roles: ((await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || []).filter((r) => r.Name.startsWith(PREFIX)).length,
            managed: ((await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || []).filter((u) => u.Name.startsWith(PROBE_USER)).length,
            users: ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Name.startsWith(PROBE_USER)).length,
            libraries: (await virtualFolders()).filter((v) => isOwnLibrary(v.Name)).length,
            knownLibraries: (await arLibraries()).filter((l) => isOwnLibrary(l.Name)).length
        };
        const configuration = await getConfiguration();
        cleanupResult.configurationRestored = !configurationToRestore || Object.entries(configurationToRestore).every(([k, v]) => configuration[k] === v);
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + (e.stack || String(e));
    }
    evidence.cleanup = cleanupResult;
    if (!failure) {
        const left = cleanupResult.probeLeft || {};
        check('afterwards nothing of the probe\'s is left (roles, users, libraries, known libraries) and the settings are as before',
            left.roles === 0 && left.managed === 0 && left.users === 0 && left.libraries === 0 && left.knownLibraries === 0 && cleanupResult.configurationRestored, cleanupResult);
        const log = logSinceStart().filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text) && (l.level === 'WRN' || l.level === 'ERR' || l.level === 'FTL'));
        // The race's own: the post-write check's finding, a save that lost and was made again from the stored row, a write
        // aborted or written back because an admin's save changed the user meanwhile.
        const expected = log.filter((l) => l.level === 'WRN' && /the server enforces .* \((Reverted|Changed)\)|write of the library access of user [0-9a-f-]+ failed|writes the library access of user [0-9a-f-]+ again in|their policy changed again while it rebuilt the write|wrote the stored policy of user [0-9a-f-]+ back instead/.test(l.text));
        evidence.log = { warnings: log.length, expectedRaceWarnings: expected.length };
        check('the log has no warning or error of Access Roles but the race\'s', log.length === expected.length, log.filter((l) => !expected.includes(l)).map((l) => l.text.slice(0, 240)));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
