#!/usr/bin/env node
// Access Roles' roles and assignments API in a running sandbox, as a curl-style walkthrough that also checks the audit
// lines written: roles from the Kid, Guest and Standard presets, the sandbox's users
// assigned (one role, several roles, a per-user override, a library mode), Preview and Effective read, the refusals with
// their codes (EmptyOnlyRole, DuplicateName, NoLibraries, NoFullAdministrator, UnmanageChoiceRequired, DefaultRole), leaving
// management, the audit lines in the API and in audit/audit-<yyyyMM>.jsonl, admin-only throughout, and every user's policy
// (GET /Users/{id}) unchanged before, during and after: the roles API writes no policy and blocks no request. It also
// pauses a user (Excluded with Keep after their native access changed, as the library sync's write will change it) and
// checks that the ledger keeps their real original access, that Restore gives it back, that the pending release is shown with its
// baseline, that a dry run answers the stored token, and that Preview names a default role it would delete.
// It leaves the store as it found it (empty): its users unmanaged with Keep, its roles and the default roles removed, through
// the API, and its own users deleted.
// Since 0.9.3 the library sync writes managed users' library access, so the walkthrough pauses it: it sets the
// plugin's LibrarySyncEnabled off for its run (POST /Plugins/{id}/Configuration) and puts the setting back at the end, in
// a finally; its "no policy written" checks are about the roles API, which writes none. The sync itself is
// accessroles-sync-probe.mjs's.
// Node 22 built-ins only. Loopback only, never port 8096. Tokens come from credentials.json and are never printed. It
// changes the sandbox only through Access Roles' admin API and that one setting, except for its own two synthetic users
// (rolesprobe-guest and rolesprobe-solo), which it creates with a random password it never keeps, gives a native library
// access (standing in for an admin's edit or the library sync's write) and deletes at the end; no other user is written. It reads the sandbox's files under
// .sandbox, never starts or stops a server, and with --out writes its result there.
//
//   node scripts/accessroles-roles-probe.mjs --sandbox access [--out result.json]
//
// Start the sandbox first: ./scripts/dev-server.ps1 -Name access -Port 8103 -Plugins AccessRoles
// -Community FileTransformation,PluginPages -Background (scripts/README.md, this probe's section). Prints one JSON result
// (pass, checks, transcript, evidence) and exits with 0 when every check passed.

import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PLUGIN = 'AccessRoles';
const PREFIX = 'Roles probe ';
const PROBE_USER = 'rolesprobe-';
const KID_LOCKS = ['Profile', 'DisplayAccount', 'Home', 'Controls', 'QuickConnect', 'EnhancedPanel', 'AdminSurfaces', 'UserDirectory'];

function usage() {
    return 'usage: node scripts/accessroles-roles-probe.mjs --sandbox <name> [--out <file>]';
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
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer']) {
    if (!users[name] || !users[name].token) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}

const yaml = fs.readFileSync(path.join(repoRoot, 'plugins', PLUGIN, 'build.yaml'), 'utf8');
const scalar = (key) => (new RegExp(`^${key}:\\s*"?([^"\\r\\n]+)"?\\s*$`, 'm').exec(yaml) || [])[1];
const plugin = { name: scalar('name'), guid: scalar('guid'), version: scalar('version') };
const CONFIGURATION_PATH = `/Plugins/${plugin.guid}/Configuration`;

// The plugin's data file and audit folder and the log folder in the sandbox (Jellyfin's DataPath is <datadir>/data), only
// read.
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
const storeFile = path.join(dataDir, 'data', 'accessroles', 'accessroles.json');
const auditFolder = path.join(dataDir, 'data', 'accessroles', 'audit');
for (const p of [storeFile, auditFolder, path.join(sandboxRoot, 'log')]) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), p);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(p)) throw new Error(`refusing to read ${p}`);
}

const checks = [];
const transcript = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authHeader(user) {
    const token = user ? users[user].token : null;
    return `MediaBrowser Client="accessroles-roles-probe", Device="sandbox", DeviceId="accessroles-roles-probe", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

function request(method, p, user = null, body = null) {
    return new Promise((resolve, reject) => {
        const headers = { Authorization: authHeader(user), Accept: 'application/json' };
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
        req.setTimeout(30000, () => req.destroy(new Error(`timeout ${method} ${p}`)));
        if (body !== null) req.write(typeof body === 'string' ? body : JSON.stringify(body));
        req.end();
    });
}

// One step of the walkthrough: the request as curl would send it (the token as <user>, never its value), and its answer.
async function step(label, method, p, user, body = null) {
    const r = await request(method, p, user, body);
    const curl = `curl -X ${method} "http://127.0.0.1:${port}${baseUrl}${p}"${user ? ` -H "Authorization: MediaBrowser Token=<${user}>"` : ''}${body !== null ? ` -H "Content-Type: application/json" -d '${JSON.stringify(body)}'` : ''}`;
    transcript.push({ step: label, curl, status: r.status, code: r.json && r.json.Code ? r.json.Code : undefined });
    return r;
}

const ids = (list) => (list || []).map((x) => (x || '').replace(/-/g, '').toLowerCase());
const sameIds = (a, b) => JSON.stringify([...ids(a)].sort()) === JSON.stringify([...ids(b)].sort());
const names = (list) => (list || []).map((x) => x.Name);
const stable = (value) => JSON.stringify(value, (k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.keys(v).sort().map((key) => [key, v[key]])) : v));

// The probe's own synthetic users, the only users it writes: created (named rolesprobe-…, with a random password that is
// never kept or printed), given a native library access, and deleted. Any other user is refused here.
const own = new Map();
async function createOwnUser(name) {
    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);
    const r = await request('POST', '/Users/New', 'admin', { Name: name, Password: crypto.randomBytes(24).toString('base64url') });
    transcript.push({ step: `create the probe's user ${name}`, curl: `curl -X POST "http://127.0.0.1:${port}${baseUrl}/Users/New" -H "Authorization: MediaBrowser Token=<admin>" -d '{"Name":"${name}","Password":"<random>"}'`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.Id) throw new Error(`POST /Users/New ${name}: ${r.status}`);
    own.set(ids([r.json.Id])[0], name);
    return r.json;
}

async function writeOwnUser(method, id, policy = null) {
    if (!own.has(ids([id])[0])) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);
    const r = method === 'DELETE' ? await request('DELETE', `/Users/${id}`, 'admin') : await request('POST', `/Users/${id}/Policy`, 'admin', policy);
    transcript.push({ step: `${method === 'DELETE' ? 'delete' : 'set the native library access of'} the probe's user ${own.get(ids([id])[0])}`, status: r.status });
    if (r.status !== 204) throw new Error(`${method} the probe's user ${id}: ${r.status}`);
    if (method === 'DELETE') own.delete(ids([id])[0]);
    return r;
}

// Gives one of the probe's users a native library access (EnableAllFolders off, these libraries), as an admin's edit on
// the stock page or the library sync's write would; returns the policy now stored.
async function setOwnAccess(user, libraryIds) {
    const current = (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
    await writeOwnUser('POST', user.Id, { ...current, EnableAllFolders: false, EnabledFolders: libraryIds });
    return (await request('GET', `/Users/${user.Id}`, 'admin')).json.Policy;
}

// The plugin's settings (the library sync's switch): read, and changed only for LibrarySyncEnabled.
async function getConfiguration() {
    const r = await request('GET', CONFIGURATION_PATH, 'admin');
    if (r.status !== 200 || !r.json) throw new Error(`GET ${CONFIGURATION_PATH}: ${r.status}`);
    return r.json;
}

async function setLibrarySync(enabled) {
    const r = await request('POST', CONFIGURATION_PATH, 'admin', { ...(await getConfiguration()), LibrarySyncEnabled: enabled });
    transcript.push({ step: `LibrarySyncEnabled ${enabled ? 'on' : 'off'}`, status: r.status });
    if (r.status !== 204) throw new Error(`POST ${CONFIGURATION_PATH}: ${r.status}`);
}

let librarySyncToRestore = null;

// The probe's user's ledger entry in the data file (read only).
function ledgerEntry(userId) {
    if (!fs.existsSync(storeFile)) return null;
    const ledger = readJson(storeFile).Ledger || {};
    const key = Object.keys(ledger).find((k) => ids([k])[0] === ids([userId])[0]);
    return key ? ledger[key] : null;
}

// Every user's policy, as GET /Users/{id} gives it to an admin.
async function policies(list) {
    const out = {};
    for (const u of list) {
        const r = await request('GET', `/Users/${u.Id}`, 'admin');
        out[u.Id] = r.status === 200 && r.json ? stable(r.json.Policy) : `status ${r.status}`;
    }
    return out;
}

function auditFileLines() {
    if (!fs.existsSync(auditFolder)) return [];
    const lines = [];
    for (const file of fs.readdirSync(auditFolder).filter((f) => /^audit-\d{6}\.jsonl$/.test(f)).sort()) {
        for (const line of fs.readFileSync(path.join(auditFolder, file), 'utf8').split('\n')) {
            if (!line.trim()) continue;
            try { lines.push({ file, entry: JSON.parse(line) }); } catch { /* a line cut short */ }
        }
    }
    return lines;
}

// Log lines since the sandbox's last start (the timestamps carry their offset).
function logSinceStart() {
    const folder = path.join(sandboxRoot, 'log');
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

// Takes every user of the probe's roles out of management (Keep: nothing to write) and deletes the probe's roles, through
// the API, so the store is as the probe found it.
async function cleanup(label) {
    await request('PUT', '/AccessRoles/Admin/DefaultRoles', 'admin', { RoleIds: [] });
    const listed = await request('GET', '/AccessRoles/Admin/Users', 'admin');
    for (const u of (listed.json || []).filter((x) => x.State !== 'NotManaged')) {
        await step(`${label}: unmanage ${u.Name} (Keep)`, 'POST', `/AccessRoles/Admin/Users/${u.UserId}/Unmanage?confirmNoFullAdmin=true`, 'admin', { Choice: 'Keep' });
    }
    const roles = await request('GET', '/AccessRoles/Admin/Roles', 'admin');
    for (const role of (roles.json || []).filter((r) => r.Name.startsWith(PREFIX))) {
        await step(`${label}: delete ${role.Name}`, 'DELETE', `/AccessRoles/Admin/Roles/${role.Id}?onUnmanage=Keep&confirmNoFullAdmin=true`, 'admin');
    }
}

async function main() {
    const info = await request('GET', '/System/Info/Public');
    check('the sandbox answers, Jellyfin 10.11.5', info.status === 200 && info.json && info.json.Version === '10.11.5', { status: info.status, version: info.json && info.json.Version });
    const installed = await request('GET', '/Plugins', 'admin');
    const installedOurs = (installed.json || []).find((p) => (p.Id || '').replace(/-/g, '').toLowerCase() === plugin.guid.replace(/-/g, '').toLowerCase());
    check('Access Roles is loaded and Active, at the build.yaml version', !!installedOurs && installedOurs.Version === plugin.version && installedOurs.Status === 'Active', installedOurs ? { version: installedOurs.Version, status: installedOurs.Status } : 'not listed');

    // The library sync (0.9.3) would write what this walkthrough assigns: it is paused for the run.
    const configurationBefore = await getConfiguration();
    librarySyncToRestore = configurationBefore.LibrarySyncEnabled !== false;
    await setLibrarySync(false);
    const syncPaused = ((await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).json || {}).LibrarySync;
    check('the library sync is paused for the walkthrough (LibrarySyncEnabled off), so no policy is written',
        syncPaused && syncPaused.State === 'Paused' && syncPaused.PausedBecause === 'LibrarySyncDisabled',
        syncPaused && { state: syncPaused.State, because: syncPaused.PausedBecause, before: configurationBefore.LibrarySyncEnabled });

    // The store must start without records of others (a run left none: it cleans up after itself), and without the
    // probe's own users (an earlier run that stopped early may have left them; deleting them removes their records).
    let diagnosticsBefore = await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin');
    let records = diagnosticsBefore.json && diagnosticsBefore.json.Records;
    const leftover = ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Name.startsWith(PROBE_USER));
    if ((records && (records.Roles > 0 || records.Assignments > 0 || records.LedgerEntries > 0)) || leftover.length > 0) {
        await cleanup('earlier run');
        for (const u of leftover) {
            own.set(ids([u.Id])[0], u.Name);
            await writeOwnUser('DELETE', u.Id);
        }
        diagnosticsBefore = await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin');
        records = diagnosticsBefore.json.Records;
    }
    check('the store starts without roles, assignments or ledger entries', records && records.Roles === 0 && records.Assignments === 0 && records.LedgerEntries === 0 && records.DefaultRoles === 0, records);
    const startSeq = (diagnosticsBefore.json && diagnosticsBefore.json.Audit && diagnosticsBefore.json.Audit.LastSeq) || 0;
    evidence.records = { before: records };

    // The server's users and their policies before anything (GET /Users/{id}); the probe's own users come later and are
    // checked on their own.
    const serverUsers = (await request('GET', '/Users', 'admin')).json || [];
    const before = await policies(serverUsers);
    const admin = serverUsers.find((u) => u.Policy && u.Policy.IsAdministrator);
    const nonAdmins = serverUsers.filter((u) => !(u.Policy && u.Policy.IsAdministrator)).sort((a, b) => a.Name.localeCompare(b.Name));
    const viewer = serverUsers.find((u) => u.Name === 'viewer');
    const viewer2 = serverUsers.find((u) => u.Name === 'viewer2') || nonAdmins.find((u) => u !== viewer);
    evidence.users = { total: serverUsers.length, administrators: serverUsers.filter((u) => u.Policy && u.Policy.IsAdministrator).length, picked: { admin: admin && admin.Name, viewer: viewer && viewer.Name, viewer2: viewer2 && viewer2.Name } };
    check('the sandbox has an administrator, viewer and a second non-admin user', !!admin && !!viewer && !!viewer2, evidence.users);
    const viewerViewsBefore = (await request('GET', `/UserViews?userId=${viewer.Id}`, 'viewer')).json;

    // Admin only: anonymous 401, a signed-in non-admin 403, an API key's kind of caller (admin token) 200.
    const anon = await step('roles, anonymously', 'GET', '/AccessRoles/Admin/Roles', null);
    const asViewer = await step('roles, as a non-admin', 'GET', '/AccessRoles/Admin/Roles', 'viewer');
    const writeAsViewer = await step('create a role, as a non-admin', 'POST', '/AccessRoles/Admin/Roles', 'viewer', { Name: `${PREFIX}Kid`, Preset: 'Kid' });
    const auditAsViewer = await step('the audit log, as a non-admin', 'GET', '/AccessRoles/Admin/Audit', 'viewer');
    check('the roles API is 401 anonymously and 403 for a non-admin, reads and writes alike', anon.status === 401 && asViewer.status === 403 && writeAsViewer.status === 403 && auditAsViewer.status === 403,
        { anonymous: anon.status, viewer: asViewer.status, viewerWrite: writeAsViewer.status, viewerAudit: auditAsViewer.status });

    // The libraries, as the role editor's picker lists them.
    const libraries = (await step('libraries', 'GET', '/AccessRoles/Admin/Libraries', 'admin')).json || [];
    evidence.libraries = libraries.map((l) => ({ name: l.Name, type: l.CollectionType, enabled: l.Enabled, state: l.State, inNoRole: l.InNoRole }));
    const virtualFolders = (await request('GET', '/Library/VirtualFolders', 'admin')).json || [];
    check('Libraries lists every library the server lists, with the id the core gives it, in no role yet',
        libraries.length === virtualFolders.length && virtualFolders.every((v) => libraries.some((l) => l.Name === v.Name && (!v.ItemId || ids([v.ItemId])[0] === ids([l.Id])[0]))) && libraries.every((l) => l.InNoRole && l.State === 'Active'),
        { accessRoles: names(libraries), server: names(virtualFolders) });
    const libA = libraries.find((l) => l.CollectionType === 'homevideos') || libraries[0];
    const libB = libraries.find((l) => l !== libA && l.CollectionType === 'movies') || libraries.find((l) => l !== libA);
    const libC = libraries.find((l) => l !== libA && l !== libB && l.CollectionType === 'tvshows') || libraries.find((l) => l !== libA && l !== libB);
    check('the sandbox has three libraries or more (a Kid library, a Family one and a third)', !!libA && !!libB && !!libC, names(libraries));

    // The probe's own synthetic users (the walkthrough doesn't depend on users another sandbox has):
    // rolesprobe-guest natively sees the Kid and Family libraries, rolesprobe-solo the Family library and the third one, so a
    // Remove without a library role leaves it one library.
    const guestUser = await createOwnUser(`${PROBE_USER}guest`);
    guestUser.Policy = await setOwnAccess(guestUser, [libA.Id, libB.Id]);
    const soloUser = await createOwnUser(`${PROBE_USER}solo`);
    soloUser.Policy = await setOwnAccess(soloUser, [libB.Id, libC.Id]);
    const ownPolicies = { [guestUser.Id]: stable(guestUser.Policy), [soloUser.Id]: stable(soloUser.Policy) };
    const soloAccess = libraries.filter((l) => ids(soloUser.Policy.EnabledFolders).includes(ids([l.Id])[0]));
    const soloRemove = libB;
    evidence.picked = { kidLibrary: libA.Name, familyLibrary: libB.Name, third: libC.Name, guest: guestUser.Name, solo: soloUser.Name, soloNative: names(soloAccess), soloRemoves: soloRemove.Name };
    check('the probe\'s own users exist with the native access it gave them',
        soloAccess.length === 2 && !guestUser.Policy.EnableAllFolders && sameIds(guestUser.Policy.EnabledFolders, [libA.Id, libB.Id]),
        { guest: guestUser.Policy.EnabledFolders.length, solo: names(soloAccess) });

    // Roles from the presets.
    const empty = await step('Kid preset with no library', 'POST', '/AccessRoles/Admin/Roles', 'admin', { Name: `${PREFIX}Kid`, Preset: 'Kid' });
    check('an Only… role with no library is 400 EmptyOnlyRole', empty.status === 400 && empty.json && empty.json.Code === 'EmptyOnlyRole', { status: empty.status, code: empty.json && empty.json.Code });
    const kidR = await step('Kid preset', 'POST', '/AccessRoles/Admin/Roles', 'admin', { Name: `${PREFIX}Kid`, Preset: 'Kid', Libraries: { Refs: [{ Id: libA.Id }] } });
    const kid = kidR.json && kidR.json.Role;
    check('the Kid preset locks its eight sections, grants Only… its library, and shows the Dashboard row',
        kidR.status === 200 && kid && JSON.stringify(kid.Locks) === JSON.stringify(KID_LOCKS) && kid.Libraries.Mode === 'Only' && kid.Libraries.Refs[0].Name === libA.Name
            && kid.Dashboard.BlocksAdminSurfaces === true && kid.Dashboard.Administrators === 'Administrators are not restricted.' && /also blocks the admin data/.test(kid.Dashboard.NonAdministrators),
        kid ? { locks: kid.Locks, libraries: kid.Libraries, dashboard: kid.Dashboard } : { status: kidR.status });
    const guestR = await step('Guest preset', 'POST', '/AccessRoles/Admin/Roles', 'admin', { Name: `${PREFIX}Guest`, Preset: 'Guest', Libraries: { Refs: [{ Id: libA.Id }] } });
    const standardR = await step('Standard preset', 'POST', '/AccessRoles/Admin/Roles', 'admin', { Name: `${PREFIX}Standard`, Preset: 'Standard' });
    const familyR = await step('Family role', 'POST', '/AccessRoles/Admin/Roles', 'admin', { Name: `${PREFIX}Family`, Description: 'Synthetic', Locks: ['Playback'], Libraries: { Mode: 'Only', Refs: [{ Id: libA.Id }, { Id: libB.Id }] } });
    const guest = guestR.json && guestR.json.Role;
    const standard = standardR.json && standardR.json.Role;
    const family = familyR.json && familyR.json.Role;
    check('the Guest preset locks all nine settings sections and both surfaces; Standard locks nothing and leaves libraries alone',
        guestR.status === 200 && guest.Locks.length === 11 && standardR.status === 200 && standard.Locks.length === 0 && standard.Libraries.Mode === 'NotManaged' && standard.Dashboard.BlocksAdminSurfaces === false
            && familyR.status === 200 && family.Libraries.Refs.length === 2,
        { guest: guest && guest.Locks.length, standard: standard && standard.Libraries.Mode, family: family && names(family.Libraries.Refs) });
    const dup = await step('a second role named like the first', 'POST', '/AccessRoles/Admin/Roles', 'admin', { Name: `${PREFIX}KID` });
    const badLock = await step('a lock that is none', 'POST', '/AccessRoles/Admin/Roles', 'admin', { Name: `${PREFIX}Bad`, Locks: ['Downloads'] });
    const badRef = await step('a library that is none', 'POST', '/AccessRoles/Admin/Roles', 'admin', { Name: `${PREFIX}Bad`, Libraries: { Mode: 'Only', Refs: [{ Id: '3c1d9f8e7b6a4c5d8e9f0a1b2c3d4e5f' }] } });
    check('validation: a taken name 409 DuplicateName, an unknown lock 400, an unknown library 400',
        dup.status === 409 && dup.json.Code === 'DuplicateName' && badLock.status === 400 && badRef.status === 400, { duplicate: dup.status, lock: badLock.status, library: badRef.status });

    // A rename with IfUpdatedAt (the kit's optimistic concurrency): without it 400, with a stale one 409, with the role's own 200.
    const renameBody = (token) => ({ Name: `${PREFIX}Family`, Description: 'Synthetic, renamed', Locks: ['Playback'], Libraries: { Mode: 'Only', Refs: [{ Id: libA.Id }, { Id: libB.Id }] }, IfUpdatedAt: token });
    const noToken = await step('change a role without IfUpdatedAt', 'PUT', `/AccessRoles/Admin/Roles/${family.Id}`, 'admin', renameBody(undefined));
    const staleToken = await step('change a role with a stale IfUpdatedAt', 'PUT', `/AccessRoles/Admin/Roles/${family.Id}`, 'admin', renameBody('2026-01-01T00:00:00Z'));
    const renamed = await step('change a role with its IfUpdatedAt', 'PUT', `/AccessRoles/Admin/Roles/${family.Id}`, 'admin', renameBody(family.UpdatedUtc));
    check('a role change needs its IfUpdatedAt: 400 without, 409 Changed when stale, 200 with it',
        noToken.status === 400 && staleToken.status === 409 && staleToken.json.Code === 'Changed' && renamed.status === 200 && renamed.json.Role.Description === 'Synthetic, renamed',
        { none: noToken.status, stale: staleToken.status, own: renamed.status });

    // Assignments: one role, several roles, a lock override, a library mode, a Remove without a library role.
    const put = (label, user, body, query = '') => step(label, 'PUT', `/AccessRoles/Admin/Users/${user.Id}${query}`, 'admin', body);
    const v1 = await put('viewer gets Kid', viewer, { RoleIds: [kid.Id] });
    const v2 = await put('viewer2 gets Kid and Family, with Quick Connect unlocked', viewer2, { RoleIds: [kid.Id, family.Id], LockOverrides: { QuickConnect: 'Unlock' } });
    const g1 = await put(`${guestUser.Name} gets Guest`, guestUser, { RoleIds: [guest.Id] });
    const s1 = await put(`${soloUser.Name} gets a Remove of ${soloRemove.Name} and no library role`, soloUser, { Libraries: { Remove: [soloRemove.Id] } });
    const a1 = await put('the admin gets Standard', admin, { RoleIds: [standard.Id] });
    check('assigning roles answers each user\'s effective access, managed',
        [v1, v2, a1, g1, s1].every((r) => r.status === 200 && r.json.Effective && r.json.Effective.State === 'Managed'),
        [v1, v2, a1, g1, s1].map((r) => ({ status: r.status, user: r.json && r.json.Effective && r.json.Effective.UserName, state: r.json && r.json.Effective && r.json.Effective.State, code: r.json && r.json.Code })));
    const noLibs = await put('viewer2 with Only… and nothing (no confirmation), as a dry run', viewer2, { Libraries: { Mode: 'Only' }, IfUpdatedAt: v2.json.Effective.UpdatedUtc }, '?dryRun=true');
    check('an accidental empty result is 409 NoLibraries, naming the user', noLibs.status === 409 && noLibs.json.Code === 'NoLibraries' && noLibs.json.Users[0].Name === viewer2.Name, { status: noLibs.status, code: noLibs.json && noLibs.json.Code });
    const lockout = await put('the admin gets Kid, as a dry run', admin, { RoleIds: [kid.Id], IfUpdatedAt: a1.json.Effective.UpdatedUtc }, '?dryRun=true');
    const confirmed = await put('the admin gets Kid, confirmed, as a dry run', admin, { RoleIds: [kid.Id], IfUpdatedAt: a1.json.Effective.UpdatedUtc }, '?dryRun=true&confirmNoFullAdmin=true');
    check('a library role for the only administrator needs confirmNoFullAdmin (409 NoFullAdministrator), and a dry run saves nothing',
        lockout.status === 409 && lockout.json.Code === 'NoFullAdministrator' && confirmed.status === 200 && confirmed.json.DryRun === true,
        { refused: lockout.status, confirmed: confirmed.status });

    // Effective: sources per lock and per library; the admin exempt from locks.
    const e2 = (await step('viewer2\'s effective access', 'GET', `/AccessRoles/Admin/Users/${viewer2.Id}/Effective`, 'admin')).json;
    const visibleA = e2 && e2.Libraries.Visible.find((v) => v.Name === libA.Name);
    const visibleB = e2 && e2.Libraries.Visible.find((v) => v.Name === libB.Name);
    evidence.effectiveViewer2 = e2 && { state: e2.State, roles: names(e2.Roles), locks: e2.Locks, libraries: { startingSet: e2.Libraries.StartingSet, visible: e2.Libraries.Visible, pendingWrite: e2.Libraries.PendingWrite, native: e2.Libraries.Native }, warnings: e2.Warnings };
    check('several roles: locks accumulate with their role as source, the override wins, libraries are the union with every role named',
        e2 && e2.Locks.Profile.Locked && e2.Locks.Profile.Source === `Role:${PREFIX}Kid` && e2.Locks.Playback.Source === `Role:${PREFIX}Family`
            && e2.Locks.QuickConnect.Locked === false && e2.Locks.QuickConnect.Source === 'UserOverride' && e2.Libraries.StartingSet === 'Roles'
            && visibleA && JSON.stringify(visibleA.Sources) === JSON.stringify([`Role:${PREFIX}Kid`, `Role:${PREFIX}Family`]) && visibleB && JSON.stringify(visibleB.Sources) === JSON.stringify([`Role:${PREFIX}Family`])
            && e2.Libraries.Visible.length === 2,
        evidence.effectiveViewer2 && { locks: { Profile: e2.Locks.Profile, Playback: e2.Locks.Playback, QuickConnect: e2.Locks.QuickConnect }, visible: e2.Libraries.Visible });
    const native2 = e2 && e2.Libraries.Native;
    check('the library sync\'s write is pending, not done: viewer2\'s native access is as before',
        e2 && (native2.EnableAllFolders ? e2.Libraries.PendingWrite && e2.Libraries.PendingWrite.EnableAllFolders === false : true)
            && stable({ all: native2.EnableAllFolders, folders: [...ids(native2.EnabledFolders)].sort() }) === stable({ all: viewer2.Policy.EnableAllFolders, folders: [...ids(viewer2.Policy.EnabledFolders)].sort() }),
        e2 && { native: native2, pendingWrite: e2.Libraries.PendingWrite });
    const ea = (await step('the admin\'s effective access', 'GET', `/AccessRoles/Admin/Users/${admin.Id}/Effective`, 'admin')).json;
    check('no lock applies to the administrator', ea && ea.IsAdministrator && Object.values(ea.Locks).every((l) => !l.Locked && l.Source === 'Administrator'), ea && { state: ea.State, sources: [...new Set(Object.values(ea.Locks).map((l) => l.Source))] });
    const es = (await step(`${soloUser.Name}'s effective access`, 'GET', `/AccessRoles/Admin/Users/${soloUser.Id}/Effective`, 'admin')).json;
    const original = soloAccess.map((l) => l.Name);
    check('a Remove without a library role starts from the original access (the ledger) and removes only that library',
        es && es.Libraries.StartingSet === 'Original' && JSON.stringify(names(es.Libraries.Visible).sort()) === JSON.stringify(original.filter((n) => n !== soloRemove.Name).sort())
            && es.Libraries.Hidden.some((h) => h.Name === soloRemove.Name && h.Reason === 'RemovedForUser'),
        es && { startingSet: es.Libraries.StartingSet, visible: names(es.Libraries.Visible), original });

    // A pause keeps the real original access. The solo user's native access becomes the Kid library (as
    // the library sync's write will change it); Excluded with Keep; managed again with Kid; Unmanage with Restore must give back the
    // original access, not the one written meanwhile. The pending release is then shown with its baseline, and an admin's
    // native edit afterwards makes it stale (the sync won't overwrite that edit).
    const soloOriginal = [libB.Id, libC.Id];
    const libraryNames = (list) => libraries.filter((l) => ids(list).includes(ids([l.Id])[0])).map((l) => l.Name);
    const tokenOf = async (user) => (((await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || []).find((u) => ids([u.UserId])[0] === ids([user.Id])[0]) || {}).UpdatedUtc;
    ownPolicies[soloUser.Id] = stable(await setOwnAccess(soloUser, [libA.Id]));
    const paused = await put(`${soloUser.Name} excluded, Keep`, soloUser, { Libraries: { Remove: [soloRemove.Id] }, State: 'Excluded', IfUpdatedAt: await tokenOf(soloUser) }, '?onUnmanage=Keep');
    const pausedEntry = ledgerEntry(soloUser.Id);
    const resumed = await put(`${soloUser.Name} managed again, with Kid`, soloUser, { RoleIds: [kid.Id], State: 'Managed', IfUpdatedAt: await tokenOf(soloUser) });
    const resumedEntry = ledgerEntry(soloUser.Id);
    evidence.pause = { nativeMeanwhile: [libA.Name], ledgerAfterPause: pausedEntry && libraryNames(pausedEntry.OriginalEnabledFolders), ledgerManagedAgain: resumedEntry && libraryNames(resumedEntry.OriginalEnabledFolders) };
    check('a pause (Excluded with Keep) keeps the ledger\'s original access, and managing the user again doesn\'t record the access written meanwhile',
        paused.status === 200 && paused.json.Effective.State === 'Excluded' && resumed.status === 200 && resumed.json.Effective.State === 'Managed'
            && !!pausedEntry && sameIds(pausedEntry.OriginalEnabledFolders, soloOriginal) && !pausedEntry.Release
            && !!resumedEntry && sameIds(resumedEntry.OriginalEnabledFolders, soloOriginal),
        { paused: paused.status, resumed: resumed.status, ...evidence.pause });
    const storedToken = await tokenOf(soloUser);
    const unlock = (token) => ({ RoleIds: [kid.Id], LockOverrides: { QuickConnect: 'Unlock' }, IfUpdatedAt: token });
    const unlockDry = await put(`${soloUser.Name}: Quick Connect unlocked, as a dry run`, soloUser, unlock(storedToken), '?dryRun=true');
    const unlockSaved = await put(`${soloUser.Name}: Quick Connect unlocked, saved with the dry run's token`, soloUser, unlock(unlockDry.json && unlockDry.json.Effective && unlockDry.json.Effective.UpdatedUtc));
    check('a dry run answers the stored token, so the save that follows with it is 200, not 409 Changed',
        unlockDry.status === 200 && unlockDry.json.DryRun === true && unlockDry.json.Effective.UpdatedUtc === storedToken && unlockSaved.status === 200,
        { dryRun: unlockDry.status, token: unlockDry.json && unlockDry.json.Effective && unlockDry.json.Effective.UpdatedUtc === storedToken ? 'the stored one' : 'another', saved: unlockSaved.status, code: unlockSaved.json && unlockSaved.json.Code });
    const restoreDry = await step(`${soloUser.Name} unmanaged with Restore, as a dry run`, 'POST', `/AccessRoles/Admin/Users/${soloUser.Id}/Unmanage?dryRun=true`, 'admin', { Choice: 'Restore' });
    const restoreRow = restoreDry.json && restoreDry.json.Preview.Users.find((u) => u.Name === soloUser.Name);
    check('Restore gives back the original access, not the one written meanwhile',
        restoreDry.status === 200 && restoreRow && restoreRow.LeavesManagement.Choice === 'Restore' && restoreRow.LeavesManagement.After && sameIds(restoreRow.LeavesManagement.After.EnabledFolders, soloOriginal),
        restoreRow && { choice: restoreRow.LeavesManagement.Choice, after: restoreRow.LeavesManagement.After && libraryNames(restoreRow.LeavesManagement.After.EnabledFolders) });
    const restored = await step(`${soloUser.Name} unmanaged with Restore`, 'POST', `/AccessRoles/Admin/Users/${soloUser.Id}/Unmanage`, 'admin', { Choice: 'Restore' });
    const pendingEffective = (await step(`${soloUser.Name}'s effective access, a release pending`, 'GET', `/AccessRoles/Admin/Users/${soloUser.Id}/Effective`, 'admin')).json;
    const soloRow = ((await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || []).find((u) => ids([u.UserId])[0] === ids([soloUser.Id])[0]);
    const pendingReleases = (((await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).json || {}).Records || {}).PendingReleases;
    const release = pendingEffective && pendingEffective.Libraries.PendingRelease;
    const pendingWrite = pendingEffective && pendingEffective.Libraries.PendingWrite;
    evidence.release = release && { choice: release.Choice, libraries: libraryNames(release.EnabledFolders), baseline: release.Baseline && libraryNames(release.Baseline.EnabledFolders), baselineMatches: release.BaselineMatches, pendingWrite: pendingWrite && { add: libraryNames(pendingWrite.Add), remove: libraryNames(pendingWrite.Remove) }, row: soloRow && { pendingWrite: soloRow.PendingWrite, summary: soloRow.Summary }, pendingReleases };
    check('the pending Restore is shown: Effective\'s PendingRelease with its baseline and PendingWrite, the Users row, Diagnostics\' count',
        restored.status === 200 && pendingEffective.State === 'NotManaged' && !!release && release.Choice === 'Restore' && sameIds(release.EnabledFolders, soloOriginal)
            && !!release.Baseline && sameIds(release.Baseline.EnabledFolders, [libA.Id]) && release.BaselineMatches === true
            && !!pendingWrite && sameIds(pendingWrite.Add, soloOriginal) && sameIds(pendingWrite.Remove, [libA.Id])
            && !!soloRow && soloRow.PendingWrite === true && pendingReleases === 1,
        evidence.release || { status: restored.status });
    ownPolicies[soloUser.Id] = stable(await setOwnAccess(soloUser, [libC.Id]));
    const staleEffective = (await step(`${soloUser.Name}'s effective access after an admin's native edit`, 'GET', `/AccessRoles/Admin/Users/${soloUser.Id}/Effective`, 'admin')).json;
    check('after an admin\'s native edit the release\'s baseline no longer matches: nothing is pending, and Effective says the sync won\'t overwrite it',
        staleEffective && staleEffective.Libraries.PendingRelease && staleEffective.Libraries.PendingRelease.BaselineMatches === false && !staleEffective.Libraries.PendingWrite
            && staleEffective.Warnings.some((w) => /won't overwrite that change/.test(w)),
        staleEffective && { baselineMatches: staleEffective.Libraries.PendingRelease && staleEffective.Libraries.PendingRelease.BaselineMatches, pendingWrite: staleEffective.Libraries.PendingWrite || null, warnings: staleEffective.Warnings });
    const simulated = (await step('viewer\'s effective access with Guest simulated', 'GET', `/AccessRoles/Admin/Users/${viewer.Id}/Effective?simulateRoleIds=${guest.Id}`, 'admin')).json;
    check('simulateRoleIds resolves the user with other roles without saving them', simulated && simulated.Locks.Playback.Source === `Role:${PREFIX}Guest` && simulated.Roles.length === 1 && simulated.Roles[0].Name === `${PREFIX}Guest`,
        simulated && { roles: names(simulated.Roles), playback: simulated.Locks.Playback });

    // Preview: what a change would do, per user, before saving.
    const widen = await step('preview: Kid also grants the Family library', 'POST', '/AccessRoles/Admin/Preview', 'admin',
        { Roles: [{ Id: kid.Id, Name: kid.Name, Locks: kid.Locks, Libraries: { Mode: 'Only', Refs: [{ Id: libA.Id }, { Id: libB.Id }] } }] });
    const widenViewer = widen.json && widen.json.Users.find((u) => u.Name === viewer.Name);
    check('Preview shows the libraries a change adds per user, and whether a policy write follows',
        widen.status === 200 && widenViewer && names(widenViewer.LibrariesAdded).includes(libB.Name) && typeof widenViewer.PolicyWrite === 'boolean' && !widen.json.Users.some((u) => u.Name === viewer2.Name && u.LibrariesAdded.length > 0),
        widen.json && widen.json.Users.map((u) => ({ name: u.Name, added: names(u.LibrariesAdded), removed: names(u.LibrariesRemoved), policyWrite: u.PolicyWrite })));
    const drop = await step('preview: delete Kid', 'POST', '/AccessRoles/Admin/Preview', 'admin', { DeleteRoleIds: [kid.Id] });
    const dropKeep = await step('preview: delete Kid, Keep', 'POST', '/AccessRoles/Admin/Preview', 'admin', { DeleteRoleIds: [kid.Id], OnUnmanage: 'Keep' });
    const keepViewer = dropKeep.json && dropKeep.json.Users.find((u) => u.Name === viewer.Name);
    check('Preview names who would leave library management, and with Keep the access it stays frozen at',
        drop.status === 200 && names(drop.json.UnmanageChoiceRequired).includes(viewer.Name) && keepViewer && keepViewer.LeavesManagement.Choice === 'Keep' && keepViewer.LeavesManagement.FrozenAt.length > 0,
        { choiceRequired: drop.json && names(drop.json.UnmanageChoiceRequired), frozenAt: keepViewer && keepViewer.LeavesManagement.FrozenAt });
    const previewLockout = await step('preview: the admin gets Kid', 'POST', '/AccessRoles/Admin/Preview', 'admin', { Assignments: [{ UserId: admin.Id, RoleIds: [kid.Id] }] });
    check('Preview flags a change that leaves no administrator with every library', previewLockout.status === 200 && previewLockout.json.NoFullAdministrator === true, previewLockout.json && { noFullAdministrator: previewLockout.json.NoFullAdministrator });

    // Default roles, and leaving management.
    const defaults = await step('Standard is the default role of new users', 'PUT', '/AccessRoles/Admin/DefaultRoles', 'admin', { RoleIds: [standard.Id] });
    const delDefault = await step('delete the default role', 'DELETE', `/AccessRoles/Admin/Roles/${standard.Id}`, 'admin');
    const previewDefault = await step('preview: delete the default role', 'POST', '/AccessRoles/Admin/Preview', 'admin', { DeleteRoleIds: [standard.Id] });
    check('default roles are stored, a default role can\'t be deleted (409 DefaultRole), and Preview names it as a refusal',
        defaults.status === 200 && defaults.json.DefaultRoles[0].Name === standard.Name && delDefault.status === 409 && delDefault.json.Code === 'DefaultRole'
            && previewDefault.status === 200 && names(previewDefault.json.DefaultRole).includes(standard.Name),
        { set: defaults.status, deleted: delDefault.status, preview: previewDefault.json && names(previewDefault.json.DefaultRole) });
    const viewerNow = (await request('GET', '/AccessRoles/Admin/Users', 'admin')).json.find((u) => u.UserId === ids([viewer.Id])[0] || u.Name === viewer.Name);
    const leave = await put('viewer loses Kid, without a choice', viewer, { RoleIds: [], IfUpdatedAt: viewerNow.UpdatedUtc });
    const bulk = await step('bulk: Kid off viewer, Keep', 'POST', '/AccessRoles/Admin/Users/Bulk', 'admin', { UserIds: [viewer.Id], RemoveRoleIds: [kid.Id], OnUnmanage: 'Keep' });
    const bulkRow = bulk.json && bulk.json.Preview.Users.find((u) => u.Name === viewer.Name);
    check('leaving library management needs onUnmanage (400 UnmanageChoiceRequired); with Keep the access stays as it is',
        leave.status === 400 && leave.json.Code === 'UnmanageChoiceRequired' && bulk.status === 200 && bulkRow && bulkRow.LeavesManagement.Choice === 'Keep' && bulkRow.PolicyWrite === false,
        { refused: leave.status, bulk: bulk.status, row: bulkRow && { choice: bulkRow.LeavesManagement.Choice, frozenAt: bulkRow.LeavesManagement.FrozenAt, policyWrite: bulkRow.PolicyWrite } });

    // Kid's UserDirectory lock is enforced since 0.9.7: the Kid-locked viewer lists only itself; with the library
    // sync paused it still sees its libraries; and nothing wrote a policy.
    await put('viewer gets Kid again', viewer, { RoleIds: [kid.Id] });
    const viewerUsers = await step('GET /Users as viewer (UserDirectory locked by Kid, enforced since 0.9.7)', 'GET', '/Users', 'viewer');
    const viewerViews = (await request('GET', `/UserViews?userId=${viewer.Id}`, 'viewer')).json;
    check('the Kid-locked viewer lists only itself (UserDirectory, since 0.9.7) and still sees its libraries (the sync paused)',
        viewerUsers.status === 200 && (viewerUsers.json || []).length === 1 && (viewerUsers.json || [])[0].Name === viewer.Name
            && stable(((viewerViews && viewerViews.Items) || []).map((i) => i.Name).sort()) === stable(((viewerViewsBefore && viewerViewsBefore.Items) || []).map((i) => i.Name).sort()),
        { users: (viewerUsers.json || []).map((u) => u.Name), views: ((viewerViews && viewerViews.Items) || []).map((i) => i.Name) });
    const during = await policies(serverUsers);
    const changedDuring = serverUsers.filter((u) => during[u.Id] !== before[u.Id]).map((u) => u.Name);
    check('no user\'s policy changed while roles were assigned (GET /Users/{id}, every user)', changedDuring.length === 0, { users: serverUsers.length, changed: changedDuring });

    // The audit log: in the API, newest first, admin only; in audit/audit-<yyyyMM>.jsonl on disk; the export.
    const audit = (await step('the audit log', 'GET', '/AccessRoles/Admin/Audit?limit=200', 'admin')).json;
    const ours = (audit && audit.Items || []).filter((e) => e.Seq > startSeq);
    const actions = ours.map((e) => e.Action);
    const count = (a) => actions.filter((x) => x === a).length;
    evidence.audit = { startSeq, lines: ours.length, actions: Object.fromEntries([...new Set(actions)].map((a) => [a, count(a)])), newest: ours.slice(0, 3) };
    check('audit lines were written for every change: roles created and changed, assignments, default roles, the frozen access',
        count('RoleCreated') === 4 && count('RoleUpdated') === 1 && count('DefaultRolesChanged') === 1 && count('AssignmentChanged') >= 5 && count('LibraryAccessFrozen') === 2
            && ours.every((e) => e.Actor.Kind === 'Admin' && e.Actor.Name === admin.Name) && ours.length === ours.map((e) => e.Seq).filter((s, i, a) => a.indexOf(s) === i).length,
        evidence.audit.actions);
    const fileLines = auditFileLines().filter((l) => l.entry.Seq > startSeq);
    evidence.audit.files = [...new Set(fileLines.map((l) => l.file))];
    check('the same lines are in data/accessroles/audit/audit-<yyyyMM>.jsonl, one JSON object each',
        fileLines.length === ours.length && fileLines.every((l) => l.entry.V === 1 && /^audit-\d{6}\.jsonl$/.test(l.file)) && stable(fileLines.map((l) => l.entry.Seq).sort((a, b) => a - b)) === stable(ours.map((e) => e.Seq).sort((a, b) => a - b)),
        { files: evidence.audit.files, onDisk: fileLines.length, api: ours.length });
    const exported = await step('the audit export', 'GET', '/AccessRoles/Admin/Audit/Export', 'admin');
    check('the export is JSON Lines, not cached, as an attachment', exported.status === 200 && /application\/x-ndjson/.test(exported.type) && /no-store/.test(exported.headers['cache-control'] || '') && /attachment/.test(exported.headers['content-disposition'] || ''),
        { status: exported.status, type: exported.type, cacheControl: exported.headers['cache-control'] });

    // Diagnostics with the records, the audit log and the kit's users.
    const diagnostics = (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).json || {};
    evidence.diagnostics = { Records: diagnostics.Records, Audit: diagnostics.Audit && { Files: diagnostics.Audit.Files, LastSeq: diagnostics.Audit.LastSeq, WriteErrors: diagnostics.Audit.WriteErrors }, Enforcement: diagnostics.Enforcement, Users: diagnostics.Users && { StoredUsers: diagnostics.Users.StoredUsers, OrphanUsers: (diagnostics.Users.OrphanUsers || []).length } };
    // Since 0.9.5 the settings locks are enforced (Kid's locks apply to its members' settings changes, which this
    // walkthrough doesn't make); the library sync, paused for the run, writes nothing.
    check('Diagnostics counts the roles and assignments, the audit log has no write error, and no library access is written (the sync paused; the settings locks enforced since 0.9.5)',
        diagnostics.Records && diagnostics.Records.Roles === 4 && diagnostics.Records.Assignments >= 4 && diagnostics.Audit && diagnostics.Audit.WriteErrors === 0 && diagnostics.Enforcement
            && diagnostics.Enforcement.LibraryAccess === 'Paused' && diagnostics.Enforcement.SettingsLocks === true,
        evidence.diagnostics);

    // The probe's own users carry only the access the probe gave them: Access Roles wrote nothing to them either.
    const ownNow = await policies([guestUser, soloUser]);
    check('the probe\'s own users\' policies are exactly what the probe last wrote (Access Roles wrote none)',
        ownNow[guestUser.Id] === ownPolicies[guestUser.Id] && ownNow[soloUser.Id] === ownPolicies[soloUser.Id], { guest: ownNow[guestUser.Id] === ownPolicies[guestUser.Id], solo: ownNow[soloUser.Id] === ownPolicies[soloUser.Id] });

    // Clean up through the API, delete the probe's own users (the solo user's pending release goes with them,
    // DeletedUserPolicy Delete, audited UserDeleted by the plugin itself), and check the store and every policy once more.
    await cleanup('cleanup');
    const soloId = soloUser.Id;
    await writeOwnUser('DELETE', guestUser.Id);
    await writeOwnUser('DELETE', soloUser.Id);
    let after = null;
    for (let i = 0; i < 20; i++) {
        after = (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).json.Records;
        if (after.LedgerEntries === 0) break;
        await new Promise((resolve) => setTimeout(resolve, 250));
    }
    check('afterwards the store is as the probe found it: no role, assignment, default role or ledger entry', after.Roles === 0 && after.Assignments === 0 && after.DefaultRoles === 0 && after.LedgerEntries === 0, after);
    const deletedLines = ((await request('GET', '/AccessRoles/Admin/Audit?action=UserDeleted&limit=200', 'admin')).json || {}).Items || [];
    const soloDeleted = deletedLines.filter((e) => e.Seq > startSeq && ids([e.TargetUserId])[0] === ids([soloId])[0]);
    check('deleting the probe\'s solo user removed its ledger entry with the pending release, audited UserDeleted by the plugin (System)',
        soloDeleted.length === 1 && soloDeleted[0].Actor.Kind === 'System' && soloDeleted[0].Details.HadLedgerEntry === true,
        soloDeleted.map((e) => ({ actor: e.Actor.Kind, hadLedgerEntry: e.Details.HadLedgerEntry })));
    const final = await policies(serverUsers);
    const changedAfter = serverUsers.filter((u) => final[u.Id] !== before[u.Id]).map((u) => u.Name);
    check('no user\'s policy changed from start to finish (GET /Users/{id}, every user)', changedAfter.length === 0, { users: serverUsers.length, changed: changedAfter });

    const log = logSinceStart();
    const ownLog = log.filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text) && (l.level === 'WRN' || l.level === 'ERR' || l.level === 'FTL'));
    evidence.log = { lines: log.length, saved: log.filter((l) => l.text.includes('Access Roles saved a change')).length };
    check('the log since the start has no warning or error of Access Roles', ownLog.length === 0, { own: ownLog.map((l) => l.text.slice(0, 220)) });
}

let failure = null;
try {
    await main();
} catch (e) {
    failure = e.stack || String(e);
} finally {
    // The library sync's switch as the probe found it.
    if (librarySyncToRestore !== null) {
        try {
            await setLibrarySync(librarySyncToRestore);
            evidence.librarySyncRestored = (await getConfiguration()).LibrarySyncEnabled === librarySyncToRestore;
        } catch (e) {
            failure = (failure ? failure + '\n' : '') + 'restoring LibrarySyncEnabled: ' + (e.stack || String(e));
        }
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
