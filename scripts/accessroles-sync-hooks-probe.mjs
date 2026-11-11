#!/usr/bin/env node
// Access Roles' import, policy-save, sign-in and user-created hooks in a running sandbox:
//   1. Import current setup on a layout shaped like a household server's (9 users, 3 administrators,
//      7 of the 9 restricted natively; here the probe's own synthetic users, next to the sandbox's), through the status
//      page's wizard in a headless browser (webcheck accessroles-import.json: preview, then apply): no change for any
//      imported user, no policy written, then every imported user taken out again with Keep and the roles deleted.
//   2. An admin's Dashboard-style policy save of a managed user (GET /Users/{id}, then POST /Users/{id}/Policy with other
//      libraries ticked and a field changed): one write carrying the role's libraries and the admin's field, audited
//      ManualEditOverridden by the admin; a body without its provider ids still the core's 400; under ReportOnly the
//      admin's add kept and DriftDetected once.
//   3. The sign-in hook: a managed user's access drifted while the sync was paused, then the user signs in: the response's
//      User carries the role's libraries, the server enforces them, the new token's /UserViews lists only them, audited
//      PolicySynced with the trigger SignIn.
//   4. Default roles: a user created while DefaultRoleIds is set gets the roles and the role's libraries, audited
//      UserCreatedDefaulted; the stock Add user page's policy post right after (libraries ticked) is pre-merged.
//   5. Leaving management: the last role removed with each onUnmanage choice (Restore and Keep through PUT Users/{id},
//      GrantAll through POST Users/{id}/Unmanage), written by the sync and audited.
//   6. Every user the probe didn't create: policy and stored permissions and preferences as before; the log without an
//      Access Roles warning.
// It writes only what is its own: its users (hook-..., random passwords kept in memory for their sign-ins, never printed or
// saved), their policies, its roles ("Hook probe ..."), the default roles (put back to none in a finally), the plugin's
// settings (put back in a finally), and the import's assignments of the sandbox's own non-admin users (store records only,
// no policy written; taken out again with Keep). It reads the sandbox's database read-only through node:sqlite (never the
// password column), its audit through the API and its log. It runs webcheck.mjs once, with its own steps file.
// Node 22 built-ins only. Loopback only, never port 8096. Tokens come from credentials.json and are never printed.
//
//   node scripts/accessroles-sync-hooks-probe.mjs --sandbox access [--out result.json]
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
const PREFIX = 'Hook probe ';
const PROBE_USER = 'hook-';

function usage() {
    return 'usage: node scripts/accessroles-sync-hooks-probe.mjs --sandbox <name> [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
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
const CONFIGURATION_PATH = `/Plugins/${plugin.guid}/Configuration`;

// The sandbox's files: the database (read only) and the log. Both must be inside .sandbox, never under ProgramData\Jellyfin.
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
const dbFile = path.join(dataDir, 'data', 'jellyfin.db');
function assertInSandbox(p) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), path.resolve(p));
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(path.resolve(p))) throw new Error(`refusing to touch ${p}`);
    return path.resolve(p);
}
for (const p of [dbFile, path.join(sandboxRoot, 'log')]) assertInSandbox(p);

const checks = [];
const transcript = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authHeader(token, deviceId = 'accessroles-sync-hooks-probe') {
    return `MediaBrowser Client="accessroles-sync-hooks-probe", Device="sandbox", DeviceId="${deviceId}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
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
    transcript.push({ step: `create the probe's user ${name}`, curl: `curl -X POST "http://127.0.0.1:${port}${baseUrl}/Users/New" -H "Authorization: MediaBrowser Token=<admin>" -d '{"Name":"${name}","Password":"<random>"}'`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.Id) throw new Error(`POST /Users/New ${name}: ${r.status}`);
    own.set(idN(r.json.Id), name);
    secrets.set(idN(r.json.Id), password);
    return r.json;
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

// The probe's user signs in: its password is only ever in this request's body; the token stays in memory. The answer's
// User (with its Policy) is returned, without the token.
async function signIn(user) {
    const password = secrets.get(idN(user.Id));
    if (!password) throw new Error(`no password for ${user.Name}`);
    const r = await request('POST', '/Users/AuthenticateByName', null, { Username: user.Name, Pw: password }, `hook-${idN(user.Id).slice(0, 8)}`);
    transcript.push({ step: `${user.Name} signs in`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.AccessToken) throw new Error(`sign-in of ${user.Name}: ${r.status}`);
    tokens.set(idN(user.Id), r.json.AccessToken);
    return r.json.User;
}

const policyOf = async (user) => (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
const sameAccess = (policy, all, folders) => policy && !!policy.EnableAllFolders === all && (all || sameIds(policy.EnabledFolders, folders));
const viewsOf = async (user) => {
    const r = await request('GET', `/UserViews?userId=${user.Id}`, idN(user.Id));
    return r.status === 200 && r.json ? names(r.json.Items) : null;
};
const virtualFolders = async () => (await request('GET', '/Library/VirtualFolders', 'admin')).json || [];

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

// ---- cleanup: everything of the probe's, through the API --------------------------------------------------------------
// The roles the import created (by id once known, else by the dry run's names and the import's description).
const importedRoleIds = new Set();
const cleanupErrors = [];
const importedRoleNames = new Set();
const isProbeRole = (r) => r.Name.startsWith(PREFIX) || importedRoleIds.has(idN(r.Id)) || (importedRoleNames.has(r.Name) && /^Imported on /.test(r.Description || ''));
async function cleanup(label) {
    await step(`${label}: no default roles`, 'PUT', '/AccessRoles/Admin/DefaultRoles', { RoleIds: [] });
    const roles = (await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || [];
    const probeRoles = roles.filter(isProbeRole).map((r) => idN(r.Id));
    // The probe's users, and whoever has only the probe's roles (the import assigned the sandbox's own users): out with
    // Keep, which writes nothing.
    for (const u of (await usersRows()).filter((x) => x.State !== 'NotManaged' && (x.Name.startsWith(PROBE_USER) || (x.RoleIds.length > 0 && x.RoleIds.every((r) => probeRoles.includes(idN(r))))))) {
        await step(`${label}: unmanage ${u.Name} (Keep)`, 'POST', `/AccessRoles/Admin/Users/${u.UserId}/Unmanage?confirmNoFullAdmin=true`, { Choice: 'Keep' });
    }
    for (const role of roles.filter(isProbeRole)) {
        await step(`${label}: delete ${role.Name}`, 'DELETE', `/AccessRoles/Admin/Roles/${role.Id}?onUnmanage=Keep&confirmNoFullAdmin=true`);
    }
    for (const u of ((await request('GET', '/Users', 'admin')).json || []).filter((x) => x.Name.startsWith(PROBE_USER))) {
        own.set(idN(u.Id), u.Name);
        // One user that can't be deleted (the core's own save race) doesn't stop the rest of the cleanup.
        try {
            await deleteOwnUser(u.Id);
        } catch (e) {
            cleanupErrors.push(`${label}: ${e.message}`);
        }
    }
}

// ---- the import wizard in a headless browser (webcheck accessroles-import.json) ---------------------------------------
// Up to three runs, 5 s apart, when only the sign-in fails, before any step of the import: the login page's user cards
// sometimes hide the manual form again while webcheck waits for it (a known flake; earlier runs met it in this probe three
// times in a row).
async function webcheckImport() {
    let run = null;
    let result = null;
    const attempts = [];
    for (let attempt = 1; attempt <= 3; attempt++) {
        if (attempt > 1) await delay(5000);
        run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', 'accessroles-import.json'], { encoding: 'utf8', timeout: 240000 });
        try { result = JSON.parse(run.stdout); } catch { result = null; }
        const failed = result && (result.steps || []).find((x) => !x.ok);
        attempts.push({ attempt, status: run.status, failedStep: failed ? failed.name : null });
        if (run.status === 0 && result && result.ok === true) break;
        if (!failed || failed.name !== 'sign in as admin') break;
    }
    return { run, result, attempts };
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

    const folders = (await virtualFolders()).filter((l) => l.ItemId);
    check('the sandbox has three libraries or more', folders.length >= 3, names(folders));
    const [libA, libB, libC] = folders;
    evidence.libraries = { a: libA.Name, b: libB.Name, c: libC.Name, count: folders.length };

    // ---------------------------------------------------------------------------------------------------------------
    // 1. Import current setup on a household server's layout, through the wizard; nobody's access changes.
    const records = (await diagnostics()).Records || {};
    check('the store manages nobody before the import', records.Assignments === 0, records);
    const layout = [
        ['admin1', { IsAdministrator: true, EnableAllFolders: true }],
        ['admin2', { IsAdministrator: true, EnableAllFolders: true }],
        ['admin3', { IsAdministrator: true, EnableAllFolders: false, EnabledFolders: [libA.ItemId, libB.ItemId] }],
        ['kid1', { EnableAllFolders: false, EnabledFolders: [libA.ItemId] }],
        ['kid2', { EnableAllFolders: false, EnabledFolders: [libA.ItemId] }],
        ['teen', { EnableAllFolders: false, EnabledFolders: [libA.ItemId, libB.ItemId] }],
        ['guest', { EnableAllFolders: false, EnabledFolders: [libC.ItemId] }],
        ['family', { EnableAllFolders: false, EnabledFolders: [libA.ItemId, libB.ItemId, libC.ItemId], EnableLiveTvManagement: true }],
        ['viewer', { EnableAllFolders: false, EnabledFolders: [libB.ItemId, libC.ItemId] }]
    ];
    const layoutUsers = [];
    for (const [name, changes] of layout) {
        const user = await createOwnUser(`${PROBE_USER}${name}`);
        await setOwnPolicy(user, changes);
        layoutUsers.push(user);
    }
    const layoutRestricted = (await Promise.all(layoutUsers.map(policyOf))).filter((p) => !p.EnableAllFolders).length;
    check('the probe\'s layout: 9 users, 3 administrators, 7 restricted natively', layoutUsers.length === 9 && layoutRestricted === 7, { restricted: layoutRestricted });
    // A non-admin who sees every library (the core's default for a new user): left unmanaged unless the admin opts in (a
    // deliberate choice).
    const everything = await createOwnUser(`${PROBE_USER}everything`);
    // Every user's policy and stored permissions and preferences, and for the layout's users their whole row (the row
    // version too); the sandbox's admin signs in for the wizard, which changes its row's sign-in columns.
    const fullRow = new Set(layoutUsers.map((u) => idN(u.Id)));
    const rowOf = async (id) => stable(Object.fromEntries(Object.entries(await dumpUser(id)).filter(([k]) => fullRow.has(idN(id)) || !k.startsWith('User.'))));
    const before = {};
    for (const u of (await request('GET', '/Users', 'admin')).json || []) before[u.Id] = { policy: stable((await request('GET', `/Users/${u.Id}`, 'admin')).json.Policy), stored: await rowOf(u.Id) };

    const rolesBefore = new Set(((await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || []).map((r) => idN(r.Id)));
    const dry = await step('Import current setup, dry run', 'POST', '/AccessRoles/Admin/ImportExisting?dryRun=true');
    const proposed = dry.json || {};
    for (const r of proposed.ProposedRoles || []) importedRoleNames.add(r.Name);
    const roleOf = (user) => ((proposed.Assignments || []).find((a) => idN(a.UserId) === idN(user.Id)) || {}).RoleName;
    const warned = (result, code, user) => (result.Warnings || []).some((w) => w.Code === code && (w.Users || []).some((u) => idN(u.UserId) === idN(user.Id)));
    evidence.importDryRun = { roles: (proposed.ProposedRoles || []).map((r) => ({ name: r.Name, mode: r.Mode, members: names(r.Members) })), assignments: (proposed.Assignments || []).length, noChange: proposed.NoChange, warnings: (proposed.Warnings || []).map((w) => `${w.Code}:${names(w.Users).join('+')}`) };
    check('the dry run: one role per distinct access of the non-admins, kid1 and kid2 sharing one, the admins and the every-library user left out, no change for every user it assigns, Live TV in one warning, nothing saved',
        dry.status === 200 && proposed.DryRun === true && roleOf(layoutUsers[3]) && roleOf(layoutUsers[3]) === roleOf(layoutUsers[4]) && !roleOf(layoutUsers[0]) && !roleOf(layoutUsers[2]) && !roleOf(everything)
            && [5, 6, 7, 8].every((i) => roleOf(layoutUsers[i]) && roleOf(layoutUsers[i]) !== roleOf(layoutUsers[3]))
            && proposed.NoChange === (proposed.Assignments || []).length && (proposed.EffectiveChanges || []).every((c) => c.LibrariesAdded.length === 0 && c.LibrariesRemoved.length === 0 && !c.PolicyWrite)
            && warned(proposed, 'Administrator', layoutUsers[2]) && warned(proposed, 'EveryLibrary', everything) && warned(proposed, 'LiveTvManagement', layoutUsers[7])
            && (proposed.Warnings || []).filter((w) => w.Code === 'LiveTvManagement' || w.Code === 'EveryLibrary').length === 2
            && ((await diagnostics()).Records || {}).Assignments === 0,
        evidence.importDryRun);
    const optedIn = await step('Import current setup, dry run, users who see every library managed too', 'POST', '/AccessRoles/Admin/ImportExisting?dryRun=true&manageUsersWithEveryLibrary=true');
    const opted = optedIn.json || {};
    const allRole = (opted.ProposedRoles || []).find((r) => r.Mode === 'All');
    evidence.importOptedIn = { roles: (opted.ProposedRoles || []).map((r) => `${r.Name}:${r.Mode}:${r.Members.length}`), noChange: opted.NoChange, assignments: (opted.Assignments || []).length, warnings: (opted.Warnings || []).map((w) => w.Code) };
    check('opted in (the wizard\'s box): the every-library user gets the "All libraries" role, still no change for anyone, nothing saved',
        optedIn.status === 200 && !!allRole && allRole.Name.startsWith('All libraries') && allRole.Members.some((m) => idN(m.UserId) === idN(everything.Id)) && warned(opted, 'EveryLibraryManaged', everything)
            && !(opted.Warnings || []).some((w) => w.Code === 'EveryLibrary') && opted.NoChange === (opted.Assignments || []).length && ((await diagnostics()).Records || {}).Assignments === 0,
        evidence.importOptedIn);

    const web = await webcheckImport();
    evidence.importWebcheck = web.result
        ? { ok: web.result.ok, attempts: web.attempts, steps: (web.result.steps || []).map((s) => ({ name: s.name, ok: s.ok, skipped: s.skipped, value: s.value, error: s.error })), exceptions: web.result.exceptions, screenshots: web.result.screenshots }
        : { attempts: web.attempts, status: web.run.status, stderr: (web.run.stderr || '').slice(0, 2000) };
    check('the status page\'s wizard: Preview shows the proposal, Apply (after the dashboard\'s confirmation) saves it (webcheck accessroles-import.json)', web.run.status === 0 && web.result && web.result.ok === true, { status: web.run.status, attempts: web.attempts });

    for (const r of (await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || []) {
        if (!rolesBefore.has(idN(r.Id))) importedRoleIds.add(idN(r.Id));
    }
    const afterImport = (await diagnostics()).Records || {};
    const importedSync = await sync('?dryRun=true');
    const importedLines = await auditSince(startSeq, 'ImportApplied');
    check('applied: the roles and the assignments saved, audited ImportApplied, and the sync has nothing to write',
        afterImport.Assignments === (proposed.Assignments || []).length && importedLines.length === 1 && importedSync.json && importedSync.json.Users.filter((u) => u.Action === 'Write' || u.Action === 'Drift').length === 0,
        { assignments: afterImport.Assignments, lines: importedLines.length, sync: importedSync.json && importedSync.json.Users.map((u) => `${u.Action}/${u.Reason}`) });
    await sync();
    await delay(3000);
    const unchanged = [];
    for (const [id, was] of Object.entries(before)) {
        const policy = stable((await request('GET', `/Users/${id}`, 'admin')).json.Policy);
        const stored = await rowOf(id);
        if (policy !== was.policy || stored !== was.stored) unchanged.push(id);
    }
    check('after the import and a sync, no user\'s policy or stored permissions and preferences changed, nor a layout user\'s row version: no policy was written', unchanged.length === 0, { changed: unchanged.length });
    // Out again: every imported user with Keep (no write), the imported roles deleted, the layout's users deleted.
    await cleanup('after the import');
    check('the import is taken out again: nobody managed', ((await diagnostics()).Records || {}).Assignments === 0, (await diagnostics()).Records);

    // ---------------------------------------------------------------------------------------------------------------
    // 2. An admin's Dashboard-style save of a managed user: pre-merged, one write.
    const kid = await createOwnUser(`${PROBE_USER}kid`);
    const kidR = await step('Kid: Only the first library', 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}Kid`, Libraries: { Mode: 'Only', Refs: [{ Id: libA.ItemId }] } });
    const kidRole = kidR.json && kidR.json.Role;
    await putUser(`${kid.Name} gets Kid`, kid, { RoleIds: [kidRole.Id] });
    const kidWritten = await until('kid is written', async () => sameAccess(await policyOf(kid), false, [libA.ItemId]));
    check('kid is managed and written', kidR.status === 200 && kidWritten.ok, { ms: kidWritten.ms });
    await settle();
    const versionBefore = Number((await dumpUser(kid.Id))['User.RowVersion']);
    const seqSave = await lastSeq();
    const page = await policyOf(kid);
    const saved = await postOwnPolicy(kid.Id, { ...page, EnableAllFolders: false, EnabledFolders: [libB.ItemId, libC.ItemId], MaxActiveSessions: 3 });
    await delay(3000);
    const kidPolicy = await policyOf(kid);
    const kidStored = await dumpUser(kid.Id);
    const overridden = (await auditSince(seqSave, 'ManualEditOverridden')).find((e) => idN(e.TargetUserId) === idN(kid.Id));
    evidence.preMerge = { status: saved.status, folders: kidPolicy.EnabledFolders.length, sessions: kidPolicy.MaxActiveSessions, rowVersion: { before: versionBefore, after: Number(kidStored['User.RowVersion']) }, line: overridden && { actor: overridden.Actor.Kind, overridden: overridden.Details.Overridden } };
    check('the save is pre-merged: the role\'s library, the admin\'s MaxActiveSessions, in the cache and the database, in one write (row version +1)',
        saved.status === 204 && sameAccess(kidPolicy, false, [libA.ItemId]) && kidPolicy.MaxActiveSessions === 3 && sameIds(storedFolders(kidStored), [libA.ItemId]) && kidStored['User.MaxActiveSessions'] === '3'
            && Number(kidStored['User.RowVersion']) === versionBefore + 1,
        evidence.preMerge);
    check('audited ManualEditOverridden by the admin, naming the ticked libraries it undid',
        !!overridden && overridden.Actor.Kind === 'Admin' && sameIds(overridden.Details.Overridden.Added, [libB.ItemId, libC.ItemId]) && sameIds(overridden.Details.Overridden.Removed, [libA.ItemId]), evidence.preMerge.line);
    const invalid = { ...page };
    delete invalid.AuthenticationProviderId;
    delete invalid.PasswordResetProviderId;
    const refused = await postOwnPolicy(kid.Id, { ...invalid, EnabledFolders: [libB.ItemId] });
    check('a save without its provider ids is still the core\'s 400, nothing changed', refused.status === 400 && sameAccess(await policyOf(kid), false, [libA.ItemId]), { status: refused.status });

    await settle();
    await setConfiguration({ DriftPolicy: 'ReportOnly' });
    const seqReport = await lastSeq();
    await setOwnPolicy(kid, { EnabledFolders: [libA.ItemId, libC.ItemId] });
    const reported = await sync();
    const reportRow = reported.json && reported.json.Users.find((u) => idN(u.UserId) === idN(kid.Id));
    const driftLines = (await auditSince(seqReport, 'DriftDetected')).filter((e) => idN(e.TargetUserId) === idN(kid.Id));
    check('ReportOnly: the admin\'s add is kept, reported once (DriftDetected by the admin), the run that follows says nothing again',
        sameAccess(await policyOf(kid), false, [libA.ItemId, libC.ItemId]) && reportRow && reportRow.Action === 'Drift' && driftLines.length === 1 && driftLines[0].Actor.Kind === 'Admin',
        { row: reportRow && `${reportRow.Action}/${reportRow.Reason}`, lines: driftLines.length });
    await setConfiguration({ DriftPolicy: 'Reapply' });
    const reapplied = await sync();
    check('Reapply again: the rules\' result written back', reapplied.status === 200 && sameAccess(await policyOf(kid), false, [libA.ItemId]), null);

    // ---------------------------------------------------------------------------------------------------------------
    // 3. The sign-in hook: drifted while the sync was paused, corrected before the token leaves.
    await settle();
    await setConfiguration({ LibrarySyncEnabled: false });
    await setOwnPolicy(kid, { EnableAllFolders: true });
    await setConfiguration({ LibrarySyncEnabled: true });
    const drifted = await policyOf(kid);
    const seqSignIn = await lastSeq();
    const answered = await signIn(kid);
    const enforced = await policyOf(kid);
    const views = await viewsOf(kid);
    const signInLine = (await auditSince(seqSignIn, 'PolicySynced')).find((e) => idN(e.TargetUserId) === idN(kid.Id));
    evidence.signIn = { drifted: drifted.EnableAllFolders, answered: answered && answered.Policy && { all: answered.Policy.EnableAllFolders, folders: answered.Policy.EnabledFolders.length }, views, line: signInLine && { trigger: signInLine.Details.Trigger, reason: signInLine.Details.Reason } };
    check('a sign-in of a drifted managed user: the response\'s User has the role\'s library, the server enforces it, the new token sees only it, audited PolicySynced (SignIn)',
        drifted.EnableAllFolders === true && answered && answered.Policy && sameAccess(answered.Policy, false, [libA.ItemId]) && sameAccess(enforced, false, [libA.ItemId])
            && views && views.includes(libA.Name) && !views.includes(libB.Name) && !views.includes(libC.Name) && signInLine && signInLine.Details.Trigger === 'SignIn',
        evidence.signIn);

    // ---------------------------------------------------------------------------------------------------------------
    // 4. Default roles for new users, and the Add user page's own policy post right after.
    await step('Kid is the default role of new users', 'PUT', '/AccessRoles/Admin/DefaultRoles', { RoleIds: [kidRole.Id] });
    const seqNew = await lastSeq();
    const created = await createOwnUser(`${PROBE_USER}new1`);
    const createdRow = await usersRow(created);
    const createdWritten = await until('the new user gets the role\'s library', async () => sameAccess(await policyOf(created), false, [libA.ItemId]), 15000);
    const defaulted = (await auditSince(seqNew, 'UserCreatedDefaulted')).find((e) => idN(e.TargetUserId) === idN(created.Id));
    check('a new user gets the default role at once (UserCreatedDefaulted by the plugin) and the role\'s library within seconds',
        createdRow && createdRow.State === 'Managed' && sameIds(createdRow.RoleIds, [kidRole.Id]) && createdWritten.ok && !!defaulted && defaulted.Actor.Kind === 'System',
        { roles: createdRow && createdRow.RoleIds.length, ms: createdWritten.ms });
    // CreateUserByName saves the password after the event; a write of ours before that save failed it (500, no password).
    let newSignIn = null;
    try {
        const answeredNew = await signIn(created);
        newSignIn = { folders: answeredNew && answeredNew.Policy && answeredNew.Policy.EnabledFolders.length, views: await viewsOf(created) };
    } catch (e) {
        newSignIn = { error: e.message };
    }
    evidence.newUserSignIn = newSignIn;
    check('the new user signs in with the password it was created with (the creating request\'s own save not broken by the sync) and sees only the role\'s library',
        !!newSignIn && !newSignIn.error && !!newSignIn.views && newSignIn.views.includes(libA.Name) && !newSignIn.views.includes(libB.Name) && !newSignIn.views.includes(libC.Name), newSignIn);
    const pageUser = await createOwnUser(`${PROBE_USER}new2`);
    const pagePolicy = await policyOf(pageUser);
    const pagePost = await postOwnPolicy(pageUser.Id, { ...pagePolicy, EnableAllFolders: false, EnabledFolders: [libB.ItemId] });
    const pageLine = (await auditSince(seqNew, 'ManualEditOverridden')).find((e) => idN(e.TargetUserId) === idN(pageUser.Id));
    check('the Add user page\'s policy post right after the creation is pre-merged: the role\'s library, the ticked one audited ManualEditOverridden',
        pagePost.status === 204 && sameAccess(await policyOf(pageUser), false, [libA.ItemId]) && !!pageLine && sameIds(pageLine.Details.Overridden.Added, [libB.ItemId]), { status: pagePost.status });
    await step('no default roles again', 'PUT', '/AccessRoles/Admin/DefaultRoles', { RoleIds: [] });

    // ---------------------------------------------------------------------------------------------------------------
    // 5. Leaving management with each choice: the last role removed (Restore, Keep), and Unmanage (GrantAll).
    const leavers = {};
    for (const choice of ['Restore', 'Keep', 'GrantAll']) {
        const user = await createOwnUser(`${PROBE_USER}${choice.toLowerCase()}`);
        await setOwnPolicy(user, { EnableAllFolders: false, EnabledFolders: [libB.ItemId, libC.ItemId] });
        await putUser(`${user.Name} gets Kid`, user, { RoleIds: [kidRole.Id] });
        leavers[choice] = user;
    }
    const leaversWritten = await until('the three are written', async () => (await Promise.all(Object.values(leavers).map(policyOf))).every((p) => sameAccess(p, false, [libA.ItemId])));
    await settle();
    const seqLeave = await lastSeq();
    const restore = await putUser('Restore: the last role removed', leavers.Restore, { RoleIds: [] }, '?onUnmanage=Restore');
    const keep = await putUser('Keep: the last role removed', leavers.Keep, { RoleIds: [] }, '?onUnmanage=Keep');
    const grant = await step('GrantAll: Unmanage', 'POST', `/AccessRoles/Admin/Users/${leavers.GrantAll.Id}/Unmanage`, { Choice: 'GrantAll' });
    const restored = await until('Restore and GrantAll are written', async () => sameAccess(await policyOf(leavers.Restore), false, [libB.ItemId, libC.ItemId]) && sameAccess(await policyOf(leavers.GrantAll), true, []));
    const leaveLines = await auditSince(seqLeave);
    evidence.leaving = { written: leaversWritten.ok, statuses: [restore.status, keep.status, grant.status], ms: restored.ms, actions: leaveLines.filter((e) => Object.values(leavers).some((u) => idN(u.Id) === idN(e.TargetUserId))).map((e) => `${e.TargetName}:${e.Action}`) };
    check('Restore gives the original access back, Keep freezes the role\'s, GrantAll every library; audited LibraryAccessRestored twice and LibraryAccessFrozen',
        leaversWritten.ok && restore.status === 200 && keep.status === 200 && grant.status === 200 && restored.ok && sameAccess(await policyOf(leavers.Keep), false, [libA.ItemId])
            && leaveLines.filter((e) => e.Action === 'LibraryAccessRestored').length === 2 && leaveLines.some((e) => e.Action === 'LibraryAccessFrozen' && idN(e.TargetUserId) === idN(leavers.Keep.Id)),
        evidence.leaving);

    // ---------------------------------------------------------------------------------------------------------------
    // 6. Every user the probe didn't create: never written.
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
        await cleanup('cleanup');
        if (configurationToRestore) await setConfiguration(configurationToRestore);
        cleanupResult.probeLeft = {
            roles: ((await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || []).filter((r) => r.Name.startsWith(PREFIX)).length,
            managed: (await usersRows()).filter((u) => u.State !== 'NotManaged').length,
            users: ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Name.startsWith(PROBE_USER)).length,
            defaultRoles: ((await diagnostics()).Records || {}).DefaultRoles
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
        check('afterwards nothing of the probe\'s is left (roles, managed users, users, default roles) and the settings are as before',
            left.roles === 0 && left.managed === 0 && left.users === 0 && left.defaultRoles === 0 && cleanupResult.configurationRestored, cleanupResult);
        const log = logSinceStart().filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text) && (l.level === 'WRN' || l.level === 'ERR' || l.level === 'FTL'));
        // A post-write check's finding after a sign-in or a save of the walkthrough is the 10.11.5 race's, not an error.
        const expected = log.filter((l) => l.level === 'WRN' && /the server enforces .* \((Reverted|Changed)\)/.test(l.text));
        evidence.log = { warnings: log.length, expectedRaceWarnings: expected.length };
        check('the log has no warning or error of Access Roles (but a post-write check\'s finding)', log.length === expected.length, log.filter((l) => !expected.includes(l)).map((l) => l.text.slice(0, 240)));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
