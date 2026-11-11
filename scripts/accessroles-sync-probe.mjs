#!/usr/bin/env node
// Access Roles' library sync in a running sandbox: the probe creates its own roles,
// users and library, lets the sync write, and compares every policy field before and after (GET /Users/{id}, and the
// stored fields in the sandbox's database: the user row but the password, every permission, every preference, the
// access schedules): only EnableAllFolders and EnabledFolders may change. Then: a user's libraries changed natively and
// DriftPolicy=Reapply putting them back, ReportOnly only reporting (and reapplyDrift reverting), SAFE-MODE and
// EnforcementEnabled=false writing nothing, the library deleted (the filter sees it, the grant goes), created again (a new
// library, not re-granted), granted again, renamed and renamed by letter case only (re-pointed to the new id), a sign-in
// racing a write (10.11.5's user cache; the post-write check heals it), the releases with a matching and a stale baseline,
// and every user the probe didn't create never written.
// Since 0.9.4 an admin's policy save of a managed user is pre-merged with the rules' result, so the probe makes a
// native edit of a managed user drift the way an edit while the sync is paused does (driftNatively: LibrarySyncEnabled off
// for the edit, then on again); the hooks' own probe, accessroles-sync-hooks-probe.mjs, checks the pre-merge itself.
// It writes only what is its own: its users (syncprobe-..., random passwords kept in memory for the sign-ins, never printed
// or saved), their policies (standing in for an admin's native edit), its roles (named "Sync probe ..."), its library (named
// "Sync probe ...", over an empty folder it makes under .sandbox/<name>/syncprobe), the plugin's own settings (put back in a
// finally), and the plugin's SAFE-MODE file (removed in a finally). It reads the sandbox's files under .sandbox (the data
// file, the audit folder, the log, the database read-only through node:sqlite, never the password column).
// Node 22 built-ins only. Loopback only, never port 8096. Tokens come from credentials.json and are never printed.
//
//   node scripts/accessroles-sync-probe.mjs --sandbox access [--race 8] [--out result.json]
//
// Start the sandbox first: ./scripts/dev-server.ps1 -Name access -Port 8103 -Plugins AccessRoles
// -Community FileTransformation,PluginPages -Background (scripts/README.md, this probe's section). Prints one JSON result
// (pass, checks, transcript, evidence) and exits with 0 when every check passed.

import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PLUGIN = 'AccessRoles';
const PREFIX = 'Sync probe ';
const PROBE_USER = 'syncprobe-';
const PROBE_LIBRARY = 'Sync probe Lib';
const MANAGED_FIELDS = ['EnableAllFolders', 'EnabledFolders'];

function usage() {
    return 'usage: node scripts/accessroles-sync-probe.mjs --sandbox <name> [--race <attempts>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, race: 8 };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--race') o.race = Number(argv[++i]);
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox) || !Number.isInteger(o.race) || o.race < 0 || o.race > 40) throw new Error(usage());
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

// The sandbox's files: the plugin's data file, audit folder and SAFE-MODE file, the database (read only), the log, and
// the folder of the probe's own library. Every one must be inside .sandbox, never under ProgramData\Jellyfin.
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
const storeFile = path.join(dataDir, 'data', 'accessroles', 'accessroles.json');
const auditFolder = path.join(dataDir, 'data', 'accessroles', 'audit');
const safeModeFile = path.join(dataDir, 'data', 'accessroles', 'SAFE-MODE');
const dbFile = path.join(dataDir, 'data', 'jellyfin.db');
const libraryFolder = path.join(sandboxRoot, 'syncprobe', 'library');
function assertInSandbox(p) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), path.resolve(p));
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(path.resolve(p))) throw new Error(`refusing to touch ${p}`);
    return path.resolve(p);
}
for (const p of [storeFile, auditFolder, safeModeFile, dbFile, libraryFolder, path.join(sandboxRoot, 'log')]) assertInSandbox(p);

const checks = [];
const transcript = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authHeader(token, deviceId = 'accessroles-sync-probe') {
    return `MediaBrowser Client="accessroles-sync-probe", Device="sandbox", DeviceId="${deviceId}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

function request(method, p, user = 'admin', body = null, deviceId = undefined) {
    return new Promise((resolve, reject) => {
        const token = user ? credentials[user].token : null;
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
const until = async (label, condition, timeoutMs = 15000) => {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        const value = await condition();
        if (value) return { ok: true, ms: Date.now() - started, value };
        await delay(200);
    }
    return { ok: false, ms: Date.now() - started, label };
};

// ---- the probe's own users: the only users it writes ------------------------------------------------------------------
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

async function writeOwnUser(method, id, policy = null) {
    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);
    const r = method === 'DELETE' ? await request('DELETE', `/Users/${id}`, 'admin') : await request('POST', `/Users/${id}/Policy`, 'admin', policy);
    transcript.push({ step: `${method === 'DELETE' ? 'delete' : 'set the native policy of'} the probe's user ${own.get(idN(id))}`, status: r.status });
    if (r.status !== 204) throw new Error(`${method} the probe's user ${id}: ${r.status}`);
    if (method === 'DELETE') { own.delete(idN(id)); secrets.delete(idN(id)); }
    return r;
}

// A native edit of one of the probe's users, as an admin's save on the stock page: the full policy with some fields changed.
async function setOwnPolicy(user, changes) {
    const current = (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
    await writeOwnUser('POST', user.Id, { ...current, ...changes });
    return (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
}

// A native edit of a managed user that stays as drift: since 0.9.4 a policy save of a managed user is pre-merged with the
// rules' result unless the sync is paused, so the edit is made with LibrarySyncEnabled off, and the switch put back.
async function driftNatively(user, changes) {
    await setConfiguration({ LibrarySyncEnabled: false });
    try {
        return await setOwnPolicy(user, changes);
    } finally {
        await setConfiguration({ LibrarySyncEnabled: true });
    }
}

// The probe's user signs in (the race): its password is only ever in this request's body.
function signIn(user, deviceId) {
    const password = secrets.get(idN(user.Id));
    if (!password) throw new Error(`no password for ${user.Name}`);
    return request('POST', '/Users/AuthenticateByName', null, { Username: user.Name, Pw: password }, deviceId);
}

const policyOf = async (user) => (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
const libs = (policy) => ({ all: !!policy.EnableAllFolders, folders: policy.EnableAllFolders ? [] : [...ids(policy.EnabledFolders)].sort() });
const sameAccess = (policy, all, folders) => policy && !!policy.EnableAllFolders === all && (all || sameIds(policy.EnabledFolders, folders));

// ---- the probe's own library: an empty folder under .sandbox/<name>/syncprobe ------------------------------------------
const isOwnLibrary = (name) => name.toLowerCase().startsWith(PREFIX.toLowerCase());
async function addOwnLibrary(name) {
    if (!isOwnLibrary(name)) throw new Error(`refusing to add the library ${name}`);
    fs.mkdirSync(assertInSandbox(libraryFolder), { recursive: true });
    const query = `/Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=homevideos&paths=${encodeURIComponent(libraryFolder)}&refreshLibrary=true`;
    const r = await step(`add the probe's library ${name}`, 'POST', query, { LibraryOptions: { Enabled: true, EnableRealtimeMonitor: false, EnableInternetProviders: false, MetadataSavers: [], TypeOptions: [] } });
    if (r.status !== 204) throw new Error(`add ${name}: ${r.status}`);
}

async function removeOwnLibrary(name) {
    if (!isOwnLibrary(name)) throw new Error(`refusing to remove the library ${name}`);
    return step(`remove the probe's library ${name}`, 'DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(name)}&refreshLibrary=true`);
}

async function renameOwnLibrary(name, newName) {
    if (!isOwnLibrary(name) || !isOwnLibrary(newName)) throw new Error(`refusing to rename the library ${name}`);
    return step(`rename the probe's library ${name} to ${newName}`, 'POST', `/Library/VirtualFolders/Name?name=${encodeURIComponent(name)}&newName=${encodeURIComponent(newName)}&refreshLibrary=true`);
}

const virtualFolders = async () => (await request('GET', '/Library/VirtualFolders', 'admin')).json || [];
const arLibraries = async () => (await request('GET', '/AccessRoles/Admin/Libraries', 'admin')).json || [];

// ---- the plugin's own settings and SAFE-MODE file ---------------------------------------------------------------------
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

function setSafeMode(on) {
    const file = assertInSandbox(safeModeFile);
    if (on) fs.writeFileSync(file, 'accessroles-sync-probe\n');
    else if (fs.existsSync(file)) fs.unlinkSync(file);
    transcript.push({ step: `SAFE-MODE ${on ? 'created' : 'removed'}` });
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
        fields.AccessSchedules = db.prepare('SELECT DayOfWeek, StartHour, EndHour FROM AccessSchedules WHERE replace(lower(UserId), \'-\', \'\') = ? ORDER BY Id').all(key).map((s) => `${s.DayOfWeek} ${s.StartHour}-${s.EndHour}`).join(';');
        return fields;
    } finally {
        db.close();
    }
}

const differences = (a, b) => [...new Set([...Object.keys(a), ...Object.keys(b)])].filter((k) => a[k] !== b[k]).sort();
const policyDifferences = (a, b) => [...new Set([...Object.keys(a), ...Object.keys(b)])].filter((k) => stable(a[k]) !== stable(b[k])).sort();

// ---- the audit log and the server log ------------------------------------------------------------------------------
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
        await writeOwnUser('DELETE', u.Id);
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

    // The settings the walkthrough needs, put back at the end: the sync on, enforcement on, Reapply.
    const configuration = await getConfiguration();
    configurationToRestore = { EnforcementEnabled: configuration.EnforcementEnabled, LibrarySyncEnabled: configuration.LibrarySyncEnabled, DriftPolicy: configuration.DriftPolicy };
    evidence.configurationBefore = configurationToRestore;
    await setConfiguration({ EnforcementEnabled: true, LibrarySyncEnabled: true, DriftPolicy: 'Reapply' });
    setSafeMode(false);

    // An earlier run that stopped early is cleaned up first.
    await cleanup('earlier run');
    const startSeq = ((await diagnostics()).Audit || {}).LastSeq || 0;
    const records = (await diagnostics()).Records || {};
    check('the store starts without the probe\'s roles, assignments or ledger entries', records.Assignments === 0 && records.LedgerEntries === 0 && records.PendingReleases === 0, records);

    // Every user the probe didn't create: policies (GET /Users/{id}) and stored permissions and preferences, before.
    const others = (await request('GET', '/Users', 'admin')).json || [];
    const othersBefore = {};
    for (const u of others) othersBefore[u.Id] = { policy: stable((await request('GET', `/Users/${u.Id}`, 'admin')).json.Policy), stored: stable(Object.fromEntries(Object.entries(await dumpUser(u.Id)).filter(([k]) => !k.startsWith('User.')))) };
    evidence.others = { users: others.length, administrators: others.filter((u) => u.Policy && u.Policy.IsAdministrator).length };

    // The libraries: the sandbox's, and the probe's own.
    let folders = await virtualFolders();
    const libA = folders.find((l) => l.CollectionType === 'homevideos') || folders[0];
    const libB = folders.find((l) => l !== libA && l.CollectionType === 'movies') || folders.find((l) => l !== libA);
    const libC = folders.find((l) => l !== libA && l !== libB && l.CollectionType === 'tvshows') || folders.find((l) => l !== libA && l !== libB);
    check('the sandbox has three libraries or more', !!libA && !!libB && !!libC, names(folders));
    await addOwnLibrary(PROBE_LIBRARY);
    const added = await until('the probe library is listed with its item', async () => (await virtualFolders()).find((v) => v.Name === PROBE_LIBRARY && v.ItemId));
    let probeLib = (await arLibraries()).find((l) => l.Name === PROBE_LIBRARY);
    check('the probe\'s library is listed with its item, and Access Roles has the id the core gives it', added.ok && !!probeLib && idN(probeLib.Id) === idN(added.value.ItemId), { listed: added.ok, accessRolesId: probeLib && idN(probeLib.Id), itemId: added.value && idN(added.value.ItemId) });
    evidence.libraries = { kid: libA.Name, family: libB.Name, third: libC.Name, probe: PROBE_LIBRARY, probeId: probeLib && idN(probeLib.Id) };

    // The library structure call runs the sync at once and again 2 s and 10 s later (a rename completes after its
    // response), and an earlier probe's sign-ins ask for runs 5 s and 30 s after each (the sign-in trigger): those runs
    // pass before the assignments, so the store change is what writes them below. (In an earlier run, right after the hooks
    // probe, one such follow-up run took the store change into its own and was recorded with the trigger SignIn.)
    await delay(31000);

    // The probe's users: a with a deliberately non-default policy (natively the family library and the third), b with
    // every library, and an administrator with every library.
    const a = await createOwnUser(`${PROBE_USER}a`);
    const b = await createOwnUser(`${PROBE_USER}b`);
    const adm = await createOwnUser(`${PROBE_USER}admin`);
    const aNative = await setOwnPolicy(a, {
        EnableAllFolders: false, EnabledFolders: [libB.ItemId, libC.ItemId], IsHidden: true, MaxActiveSessions: 3, LoginAttemptsBeforeLockout: 10,
        BlockedTags: ['syncprobe-blocked'], AllowedTags: ['syncprobe-allowed'], EnableLyricManagement: true, EnableSubtitleManagement: true, RemoteClientBitrateLimit: 8000000,
        EnableRemoteControlOfOtherUsers: true, EnableContentDownloading: false, SyncPlayAccess: 'JoinGroups', MaxParentalRating: 13,
        AccessSchedules: [{ DayOfWeek: 'Everyday', StartHour: 0, EndHour: 24, UserId: a.Id }], EnabledDevices: ['syncprobe-device'], EnableAllDevices: true
    });
    await setOwnPolicy(adm, { IsAdministrator: true, EnableAllFolders: true });
    const aStoredBefore = await dumpUser(a.Id);
    const bPolicyBefore = await policyOf(b);
    const admPolicyBefore = await policyOf(adm);
    check('the probe\'s users exist with the native access it gave them', !aNative.EnableAllFolders && sameIds(aNative.EnabledFolders, [libB.ItemId, libC.ItemId]) && bPolicyBefore.EnableAllFolders && admPolicyBefore.IsAdministrator && admPolicyBefore.EnableAllFolders,
        { a: names(folders.filter((l) => ids(aNative.EnabledFolders).includes(idN(l.ItemId)))), storedFields: Object.keys(aStoredBefore).length });

    // Roles and assignments: the sync writes on its own (the store change, debounced 2 s).
    const kidR = await step('Kid: Only the kid library and the probe library', 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}Kid`, Locks: ['Profile'], Libraries: { Mode: 'Only', Refs: [{ Id: libA.ItemId }, { Id: probeLib.Id }] } });
    const allButR = await step('AllBut: every library but the family one', 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}AllBut`, Libraries: { Mode: 'AllExcept', Refs: [{ Id: libB.ItemId }] } });
    const kid = kidR.json && kidR.json.Role;
    const allBut = allButR.json && allButR.json.Role;
    const assigned = [];
    for (const [user, role] of [[a, kid], [b, allBut], [adm, kid]]) assigned.push(await step(`${user.Name} gets ${role.Name}`, 'PUT', `/AccessRoles/Admin/Users/${user.Id}`, { RoleIds: [role.Id] }));
    check('the roles and assignments are saved', kidR.status === 200 && allButR.status === 200 && assigned.every((r) => r.status === 200), assigned.map((r) => r.status));
    const kidTarget = [libA.ItemId, probeLib.Id];
    const allButTarget = (await virtualFolders()).filter((v) => idN(v.ItemId) !== idN(libB.ItemId)).map((v) => v.ItemId);
    const written = await until('the sync writes the three users', async () => sameAccess(await policyOf(a), false, kidTarget) && sameAccess(await policyOf(b), false, allButTarget) && sameAccess(await policyOf(adm), false, kidTarget));
    // The run is recorded in Diagnostics once it has finished, a moment after its writes.
    const firstRun = await until('Diagnostics shows the run', async () => {
        const run = ((await diagnostics()).LibrarySync || {}).LastRun;
        return run && run.Trigger === 'StoreChanged' && run.Written === 3 ? run : null;
    }, 5000);
    check('the library sync wrote the three managed users on its own, after the store change (the administrator too: library roles apply to admins)',
        written.ok && firstRun.ok, { ms: written.ms, lastRun: firstRun.value || null });

    // Every policy field before and after: only the two library fields (and in the database the row version).
    const aPolicyAfter = await policyOf(a);
    const aStoredAfter = await dumpUser(a.Id);
    const policyDiff = policyDifferences(aNative, aPolicyAfter);
    const storedDiff = differences(aStoredBefore, aStoredAfter);
    evidence.fieldDiff = {
        policyFields: Object.keys(aNative).length,
        policyChanged: policyDiff,
        storedFields: Object.keys(aStoredBefore).length,
        storedChanged: storedDiff.map((k) => ({ field: k, before: aStoredBefore[k], after: aStoredAfter[k] })),
        lyric: { before: aStoredBefore['Permission.23'], after: aStoredAfter['Permission.23'] }
    };
    check('a\'s policy: of every field GET /Users/{id} gives, only EnabledFolders changed (EnableAllFolders was already off)', policyDiff.length === 1 && policyDiff[0] === 'EnabledFolders', policyDiff);
    check('a\'s stored fields: only the EnabledFolders preference and the row version changed; the lyric permission and the schedule kept',
        stable(storedDiff) === stable(['Preference.5', 'User.RowVersion']) && aStoredAfter['Permission.23'] === '1' && aStoredAfter.AccessSchedules === aStoredBefore.AccessSchedules,
        evidence.fieldDiff.storedChanged);
    const bDiff = policyDifferences(bPolicyBefore, await policyOf(b));
    const admDiff = policyDifferences(admPolicyBefore, await policyOf(adm));
    check('b\'s and the administrator\'s policies: only EnableAllFolders and EnabledFolders changed', bDiff.every((k) => MANAGED_FIELDS.includes(k)) && admDiff.every((k) => MANAGED_FIELDS.includes(k)) && bDiff.length === 2 && admDiff.length === 2, { b: bDiff, admin: admDiff });
    const synced = await auditSince(startSeq, 'PolicySynced');
    check('each write is audited PolicySynced by the plugin (System), reason FirstWrite, trigger StoreChanged',
        [a, b, adm].every((u) => synced.some((e) => idN(e.TargetUserId) === idN(u.Id) && e.Actor.Kind === 'System' && e.Details.Reason === 'FirstWrite' && e.Details.Trigger === 'StoreChanged')),
        synced.map((e) => ({ user: e.TargetName, reason: e.Details.Reason, trigger: e.Details.Trigger })));
    const ledger = readJson(storeFile).Ledger || {};
    const aEntry = Object.entries(ledger).find(([k]) => idN(k) === idN(a.Id));
    check('the ledger keeps a\'s original access and the last write', !!aEntry && sameIds(aEntry[1].OriginalEnabledFolders, [libB.ItemId, libC.ItemId]) && aEntry[1].LastWrite && sameIds(aEntry[1].LastWrite.EnabledFolders, kidTarget),
        aEntry && { original: aEntry[1].OriginalEnabledFolders.length, lastWrite: aEntry[1].LastWrite && aEntry[1].LastWrite.EnabledFolders.length });

    // Drift with Reapply: a's libraries changed natively (while the sync was paused, so the save wasn't pre-merged); the
    // sync puts them back.
    await settle();
    await driftNatively(a, { EnabledFolders: [libB.ItemId] });
    const reapplied = await sync();
    const reRow = reapplied.json && reapplied.json.Users.find((u) => idN(u.UserId) === idN(a.Id));
    check('DriftPolicy=Reapply: a native change of a\'s libraries is written back, reason Drift', reapplied.status === 200 && reRow && reRow.Reason === 'Drift' && reRow.Outcome === 'Written' && sameAccess(await policyOf(a), false, kidTarget),
        reRow && { action: reRow.Action, reason: reRow.Reason, outcome: reRow.Outcome, removed: names(reRow.Removed), added: names(reRow.Added) });

    // ReportOnly: reported, left; reapplyDrift reverts it.
    await settle();
    await setConfiguration({ DriftPolicy: 'ReportOnly' });
    await setOwnPolicy(a, { EnabledFolders: [libB.ItemId] });
    const reported = await sync();
    const repRow = reported.json && reported.json.Users.find((u) => idN(u.UserId) === idN(a.Id));
    const aRow = ((await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || []).find((u) => idN(u.UserId) === idN(a.Id));
    const driftDiag = (await diagnostics()).LibrarySync || {};
    check('DriftPolicy=ReportOnly: the drift is reported (Drift, the Users row, Diagnostics\' DriftCount) and the native change stays',
        repRow && repRow.Action === 'Drift' && repRow.Reason === 'ReportOnly' && aRow && aRow.Drift === true && driftDiag.DriftCount === 1 && sameAccess(await policyOf(a), false, [libB.ItemId]),
        { row: repRow && { action: repRow.Action, reason: repRow.Reason }, usersRowDrift: aRow && aRow.Drift, driftCount: driftDiag.DriftCount });
    const reverted = await sync('?reapplyDrift=true');
    const revRow = reverted.json && reverted.json.Users.find((u) => idN(u.UserId) === idN(a.Id));
    check('reapplyDrift=true (the admin\'s Revert) writes the rules\' result over the reported drift', revRow && revRow.Outcome === 'Written' && sameAccess(await policyOf(a), false, kidTarget), revRow && { reason: revRow.Reason, outcome: revRow.Outcome });
    await setConfiguration({ DriftPolicy: 'Reapply' });
    check('DriftDetected was audited once for the reported drift', (await auditSince(startSeq, 'DriftDetected')).filter((e) => idN(e.TargetUserId) === idN(a.Id)).length === 1, null);

    // SAFE-MODE and EnforcementEnabled=false: nothing written.
    await settle();
    await driftNatively(a, { EnabledFolders: [libB.ItemId] });
    setSafeMode(true);
    const safe = await sync();
    const safeDiag = (await diagnostics()).LibrarySync || {};
    setSafeMode(false);
    await setConfiguration({ EnforcementEnabled: false });
    const off = await sync();
    const aWhileOff = await policyOf(a);
    await setConfiguration({ EnforcementEnabled: true });
    const resumed = await sync();
    check('SAFE-MODE and EnforcementEnabled=false pause the sync: nothing written, the reason said; afterwards it writes again',
        safe.json && safe.json.Paused === 'SafeMode' && safeDiag.State === 'Paused' && off.json && off.json.Paused === 'EnforcementDisabled' && sameAccess(aWhileOff, false, [libB.ItemId])
            && resumed.json && !resumed.json.Paused && resumed.json.Written === 1 && sameAccess(await policyOf(a), false, kidTarget),
        { safe: safe.json && safe.json.Paused, diagnostics: safeDiag.State, off: off.json && off.json.Paused, resumed: resumed.json && resumed.json.Written });
    const pauses = (await auditSince(startSeq)).filter((e) => e.Action === 'LibrarySyncPaused' || e.Action === 'LibrarySyncResumed').map((e) => `${e.Action}:${e.Details.Reason}`);
    check('the pauses and the resume are audited', pauses.includes('LibrarySyncPaused:SafeMode') && pauses.includes('LibrarySyncPaused:EnforcementDisabled') && pauses.includes('LibrarySyncResumed:EnforcementDisabled'), pauses);

    // The library lifecycle: deleted, created again, granted again, renamed, renamed by letter case only.
    await settle();
    const probeId = probeLib.Id;
    await removeOwnLibrary(PROBE_LIBRARY);
    const gone = await until('the deleted library\'s grant goes', async () => !ids((await policyOf(a)).EnabledFolders).includes(idN(probeId)));
    const orphan = (await arLibraries()).find((l) => idN(l.Id) === idN(probeId));
    const removedLine = (await auditSince(startSeq, 'LibraryRemoved')).find((e) => e.Details.LibraryId === idN(probeId));
    check('a deleted library (the filter saw the call): its id leaves a\'s native access at once, the reference is Orphaned, audited LibraryRemoved',
        gone.ok && orphan && orphan.State === 'Orphaned' && removedLine && removedLine.Details.SeenCall === true, { ms: gone.ms, state: orphan && orphan.State, seenCall: removedLine && removedLine.Details.SeenCall });
    await addOwnLibrary(PROBE_LIBRARY);
    await until('the re-created library is listed', async () => (await virtualFolders()).find((v) => v.Name === PROBE_LIBRARY && v.ItemId));
    const recreated = await until('it shows as re-created', async () => (await arLibraries()).find((l) => idN(l.Id) === idN(probeId) && l.Recreated === true));
    await delay(2500);
    const aEffective = (await request('GET', `/AccessRoles/Admin/Users/${a.Id}/Effective`, 'admin')).json;
    const hidden = aEffective && aEffective.Libraries.Hidden.find((h) => idN(h.Id) === idN(probeId));
    check('the library created again under the same id is new: shown Recreated, not granted to a (hidden as Recreated), audited LibraryRecreated',
        recreated.ok && !ids((await policyOf(a)).EnabledFolders).includes(idN(probeId)) && hidden && hidden.Reason === 'Recreated' && (await auditSince(startSeq, 'LibraryRecreated')).length >= 1,
        { recreated: recreated.ok, hidden: hidden && hidden.Reason });
    const grant = await step('grant the re-created library again', 'POST', `/AccessRoles/Admin/Libraries/${probeId}/GrantAgain`);
    const regranted = await until('a gets it again', async () => ids((await policyOf(a)).EnabledFolders).includes(idN(probeId)));
    check('"grant it again": the earlier grant gives it again, written by the sync', grant.status === 200 && regranted.ok && !(await arLibraries()).find((l) => idN(l.Id) === idN(probeId)).Recreated, { status: grant.status, ms: regranted.ms });

    const renamedName = `${PROBE_LIBRARY}2`;
    const renamed = await renameOwnLibrary(PROBE_LIBRARY, renamedName);
    const renamedLib = await until('the renamed library is listed with its item', async () => (await arLibraries()).find((l) => l.Name === renamedName && l.Known));
    const renamedId = renamedLib.value && renamedLib.value.Id;
    const repointed = await until('a\'s grant is re-pointed to the new id', async () => renamedId && ids((await policyOf(a)).EnabledFolders).includes(idN(renamedId)) && !ids((await policyOf(a)).EnabledFolders).includes(idN(probeId)));
    const kidRole = ((await request('GET', `/AccessRoles/Admin/Roles/${kid.Id}`, 'admin')).json || {}).Libraries;
    check('a rename the filter saw: re-pointed to the new id once it has its item, the role\'s reference too, audited LibraryRenamed',
        renamed.status === 204 && repointed.ok && kidRole && kidRole.Refs.some((r) => idN(r.Id) === idN(renamedId)) && (await auditSince(startSeq, 'LibraryRenamed')).some((e) => e.Details.SeenCall === true),
        { ms: repointed.ms, newId: renamedId && idN(renamedId), oldId: idN(probeId) });

    const lowerName = renamedName.toLowerCase();
    const caseRenamed = await renameOwnLibrary(renamedName, lowerName);
    const lowerLib = await until('the case-renamed library is known', async () => (await arLibraries()).find((l) => l.Name === lowerName && l.Known));
    const lowerId = lowerLib.value && lowerLib.value.Id;
    const caseRepointed = await until('a\'s grant follows the case-only rename', async () => lowerId && ids((await policyOf(a)).EnabledFolders).includes(idN(lowerId)));
    const reportedItem = (await virtualFolders()).find((v) => v.Name === lowerName);
    evidence.caseOnlyRename = { accessRolesId: lowerId && idN(lowerId), before: renamedId && idN(renamedId), getVirtualFoldersItemId: reportedItem && idN(reportedItem.ItemId) };
    check('a case-only rename (Windows): a new id (GetNewItemId of the new name), a\'s grant re-pointed to it',
        caseRenamed.status === 204 && caseRepointed.ok && lowerId && idN(lowerId) !== idN(renamedId), evidence.caseOnlyRename);

    // A sign-in racing a write (10.11.5's user cache): a's libraries changed natively, a signs in, and while the password is checked
    // the sync writes them back. When the sign-in puts the old cached user back, the post-write check (5 s) writes again.
    // (a's access schedule is every day, all day, so the sign-in is allowed at any hour.)
    // The renames' runs again 2 s and 10 s later pass first.
    await delay(11000);
    const raceTarget = (await policyOf(a)).EnabledFolders;
    const raceSeq = ((await diagnostics()).Audit || {}).LastSeq || 0;
    const attempts = [];
    for (let i = 0; i < opts.race; i++) {
        const offset = [0, 10, 20, 30, 40, 50, 60, 75][i % 8];
        await driftNatively(a, { EnabledFolders: [libB.ItemId] });
        const signing = signIn(a, `syncprobe-race-${i}`);
        await delay(offset);
        const write = await request('POST', '/AccessRoles/Admin/Sync', 'admin');
        const signed = await signing;
        const right = await policyOf(a);
        const healed = await until('healed', async () => sameAccess(await policyOf(a), false, raceTarget), 15000);
        attempts.push({ offset, signIn: signed.status, written: write.json && write.json.Written, staleAfterBoth: !sameAccess(right, false, raceTarget), healed: healed.ok, ms: healed.ms });
        await delay(6000);
    }
    const reverts = (await auditSince(raceSeq, 'PolicySynced')).filter((e) => e.Details.Reason === 'RevertedAfterWrite');
    evidence.race = { attempts, revertedAfterWrite: reverts.length, failedSignIns: attempts.filter((x) => x.signIn !== 200).length, staleCaches: attempts.filter((x) => x.staleAfterBoth).length };
    check('every racing attempt ends with the server enforcing the written access; a sign-in that reverted the cache was healed by the post-write check (PolicySynced, RevertedAfterWrite)',
        attempts.length === opts.race && attempts.every((x) => x.healed && (x.signIn === 200 || x.signIn === 500)) && (evidence.race.staleCaches === 0 || reverts.length >= 1), evidence.race);
    // The race the other way round: the sign-in's save first, ours failing after 10.11.5 put the entity into its cache;
    // the writer writes again, forced, so the database holds what the server enforces.
    const raceStored = await dumpUser(a.Id);
    evidence.race.database = raceStored['Preference.5'];
    check('after the race the database holds the written access too, not only the cache of the user manager',
        sameIds((raceStored['Preference.5'] || '').split(',').filter((x) => x), raceTarget) && raceStored['Permission.16'] === '0', { stored: raceStored['Preference.5'], target: ids(raceTarget) });

    // Releases: Restore with a matching baseline is written; one whose baseline went stale is dropped.
    await settle();
    const restore = await step('a leaves management with Restore', 'POST', `/AccessRoles/Admin/Users/${a.Id}/Unmanage`, { Choice: 'Restore' });
    const restored = await until('a gets the original access back', async () => sameAccess(await policyOf(a), false, [libB.ItemId, libC.ItemId]));
    const restoredLine = (await auditSince(startSeq, 'LibraryAccessRestored')).find((e) => idN(e.TargetUserId) === idN(a.Id));
    const aLedger = Object.keys(readJson(storeFile).Ledger || {}).some((k) => idN(k) === idN(a.Id));
    check('Restore: the sync writes the original access (the baseline matched), audited LibraryAccessRestored, the ledger entry gone',
        restore.status === 200 && restored.ok && restoredLine && restoredLine.Details.PolicyWritten === true && !aLedger, { ms: restored.ms, choice: restoredLine && restoredLine.Details.Choice });
    await setConfiguration({ LibrarySyncEnabled: false });
    const restoreB = await step('b leaves management with Restore, the sync paused', 'POST', `/AccessRoles/Admin/Users/${b.Id}/Unmanage`, { Choice: 'Restore' });
    await setOwnPolicy(b, { EnableAllFolders: false, EnabledFolders: [libC.ItemId] });
    await setConfiguration({ LibrarySyncEnabled: true });
    const dropped = await sync();
    const dropRow = dropped.json && dropped.json.Users.find((u) => idN(u.UserId) === idN(b.Id));
    const droppedLine = (await auditSince(startSeq, 'ReleaseDropped')).find((e) => idN(e.TargetUserId) === idN(b.Id));
    check('a release whose baseline no longer matches (an admin edited b natively since) is dropped without a write, audited ReleaseDropped',
        restoreB.status === 200 && dropRow && dropRow.Action === 'DropRelease' && dropRow.Reason === 'BaselineChanged' && sameAccess(await policyOf(b), false, [libC.ItemId]) && !!droppedLine,
        dropRow && { action: dropRow.Action, reason: dropRow.Reason });

    // Every user the probe didn't create: never written.
    const changed = [];
    for (const u of others) {
        const policy = stable((await request('GET', `/Users/${u.Id}`, 'admin')).json.Policy);
        const stored = stable(Object.fromEntries(Object.entries(await dumpUser(u.Id)).filter(([k]) => !k.startsWith('User.'))));
        if (policy !== othersBefore[u.Id].policy || stored !== othersBefore[u.Id].stored) changed.push(u.Name);
    }
    check('no user the probe didn\'t create was written: their policies and their stored permissions and preferences are as before', changed.length === 0, { users: others.length, changed });

    const d = await diagnostics();
    evidence.diagnostics = { LibrarySync: d.LibrarySync, Enforcement: d.Enforcement };
    check('Diagnostics shows the library sync: active, its last run, the drift count, the releases waiting, the totals', d.LibrarySync && d.LibrarySync.State === 'Active' && d.LibrarySync.LastRun && d.LibrarySync.Totals.Writes > 0 && d.LibrarySync.PendingReleases === 0 && d.LibrarySync.DriftCount === 0,
        d.LibrarySync && { lastRun: d.LibrarySync.LastRun && d.LibrarySync.LastRun.Trigger, totals: d.LibrarySync.Totals });
    const lines = await auditSince(startSeq);
    evidence.audit = Object.fromEntries([...new Set(lines.map((e) => e.Action))].map((x) => [x, lines.filter((e) => e.Action === x).length]));
}

let failure = null;
try {
    await main();
} catch (e) {
    failure = e.stack || String(e);
} finally {
    // Everything of the probe's goes, and the settings and SAFE-MODE are as the probe found them.
    const cleanupResult = { steps: [] };
    try {
        setSafeMode(false);
        await cleanup('cleanup');
        if (configurationToRestore) await setConfiguration(configurationToRestore);
        const after = (await diagnostics()).Records || {};
        const store = readJson(storeFile);
        cleanupResult.records = after;
        cleanupResult.probeLeft = {
            roles: (store.Roles || []).filter((r) => (r.Name || '').startsWith(PREFIX)).length,
            assignments: (store.Assignments || []).length,
            ledger: Object.keys(store.Ledger || {}).length,
            knownProbeLibraries: (store.KnownLibraries || []).filter((k) => isOwnLibrary(k.Name || '')).length,
            users: ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Name.startsWith(PROBE_USER)).length,
            libraries: (await virtualFolders()).filter((v) => isOwnLibrary(v.Name)).length
        };
        const configuration = await getConfiguration();
        cleanupResult.configurationRestored = !configurationToRestore || Object.entries(configurationToRestore).every(([k, v]) => configuration[k] === v);
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + (e.stack || String(e));
    }
    evidence.cleanup = cleanupResult;
    if (!failure) {
        const left = cleanupResult.probeLeft || {};
        check('afterwards nothing of the probe\'s is left (roles, assignments, ledger entries, users, libraries, known libraries) and the settings are as before',
            left.roles === 0 && left.assignments === 0 && left.ledger === 0 && left.users === 0 && left.libraries === 0 && left.knownProbeLibraries === 0 && cleanupResult.configurationRestored, cleanupResult);
        const log = logSinceStart().filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text) && (l.level === 'WRN' || l.level === 'ERR' || l.level === 'FTL'));
        // The race's own: the post-write check's finding, and a save that lost to the sign-in's and was made again, or the
        // stored row written back when the save that came first changed the library fields too (seen in
        // earlier runs when the last attempt's sign-in won and its hook corrected the user).
        const expected = log.filter((l) => /the server enforces .* \((Reverted|Changed)\)|write of the library access of user [0-9a-f-]+ failed|writes the library access of user [0-9a-f-]+ again in|wrote the stored policy of user [0-9a-f-]+ back instead/.test(l.text));
        evidence.log = { warnings: log.length, expectedRaceWarnings: expected.length };
        check('the log has no warning or error of Access Roles but the post-write check\'s about the race', log.length === expected.length, log.filter((l) => !expected.includes(l)).map((l) => l.text.slice(0, 240)));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
