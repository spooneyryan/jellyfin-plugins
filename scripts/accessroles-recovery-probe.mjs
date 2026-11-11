#!/usr/bin/env node
// Access Roles' lockout and recovery in a running sandbox.
//   1. The optional native mirror (MirrorPreferenceLockToPolicy): a managed non-admin whose role locks all five
//      server-stored sections loses EnableUserPreferenceAccess at the next sync (audited PreferenceAccessMirrored), a user
//      whose role locks four keeps it (the negative control), an admin's native write of it back on is written off again at
//      the next sync (WrittenBackOn), and the setting saved off on the Settings tab puts it back on within 20 s with no POST
//      Sync (PreferenceAccessRestored, MirrorOff), saved on again turns it off the same way.
//   2. Release all locks (POST /AccessRoles/Admin/ReleaseLocks): 403 for a non-admin; the dry run lists only the users it
//      writes (with restoreLibraries=true the ones whose original library access it restores); a confirmation whose
//      PreviewToken is stale is a 409 Changed naming only the user who changed, and writes nothing; then the flag is back on,
//      the original library access back, both users Excluded with their roles kept, audited LocksReleased; a later sync
//      writes neither again.
//   3. Degraded/Resolve on a usable store: 409 NotDegraded.
// With --scratch (the throwaway access-scratch sandbox only, which must listen on the loopback address alone) it
// runs the restart rows instead, restarting the server through POST /System/Restart as the administrator:
//   S0. The record before the flag: with an exclusive handle on the store's temp file (every
//       store write fails) the mirror turns no flag off (WriteAborted PreferenceMirrorFailed); let go, it turns both off.
//   S1. The emergency stop at the start: the SAFE-MODE file, then EnforcementEnabled false in the plugin's XML (written by
//       POST /Plugins/{id}/Configuration, as the page's "Turn enforcement back on" writes it back), each with a restart:
//       the admin script answers, every other action 503 naming the stop, the kid's locked password change reaches the core
//       (inert: nothing filters it), the flag the mirror turned off stays off; back on, the lock applies again.
//   S2. The store at the start, each with a restart: (a) the data file and its .bak unreadable (a broken schema) with no
//       snapshot: ReadOnlyDataUnreadable and Degraded mode, the kid (managed in the file, read leniently) locked, the viewer
//       (never managed) not, and Release all locks on the read-only store puts the kid's flag back,
//       nobody Excluded; both removed, the store starts empty and Degraded mode stays (FromEarlierStart), the pal's mirror
//       record taken back from the marker, until Degraded/Resolve's dry run and confirmation end it and the sync it wakes
//       puts the pal's flag back; (b) a user managed after a snapshot, then the files a broken schema: RestoredFromSnapshot,
//       Degraded mode locking only that user (its record taken back) until it is resolved and the restore hold confirmed,
//       its flag back on; (c) the data file of a newer schema version: ReadOnlyNewerVersion, its own assignments enforced
//       (not Degraded), a write 503; (d) one this build can't parse: Degraded from the lenient read, not resolvable. The
//       files put back each time.
//   S3. An administrator in a narrow library role recovers through the plugin's page API: the page's admin calls answer
//       it, and its own Release all locks with restoreLibraries gives it every library back.
//   S4. Uninstalling through the Dashboard (DELETE /Plugins/{id}/{version}) with the mirror on: the flags are back on at
//       once (OnUninstalling), before any restart. The sandbox is left with the restart pending: stop it and delete it.
// It writes only what is its own: its users (recoveryprobe-..., random passwords and sign-in tokens kept in memory, never
// printed or saved), their policies, its roles ("Recovery probe ..."), their assignments (Unmanage Keep in a finally) and
// Access Roles' settings (put back); with --scratch also the SAFE-MODE file (removed again), the store's temp file (held
// exclusively for a moment, then removed), the data file and its .bak
// (copied under .sandbox/access-scratch/recoveryprobe first and put back), the Degraded marker's end through the API, and the
// uninstall. Node 22 built-ins only. Loopback only, never port 8096.
//
//   node scripts/accessroles-recovery-probe.mjs --sandbox access [--out result.json]
//   node scripts/accessroles-recovery-probe.mjs --sandbox access-scratch --scratch [--out result.json]
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
const PREFIX = 'Recovery probe ';
const PROBE_USER = 'recoveryprobe-';
const SCRATCH_SANDBOX = 'access-scratch';
const LOCKED_TYPE = 'urn:jellyfin-plugin-kit:locked';
const FIVE = ['Profile', 'DisplayAccount', 'Home', 'Playback', 'Subtitles'];

function usage() {
    return 'usage: node scripts/accessroles-recovery-probe.mjs --sandbox <name> [--scratch] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, scratch: false };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--scratch') o.scratch = true;
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    // The restarts, the files and the uninstall only in the throwaway access-scratch sandbox.
    if (o.scratch && o.sandbox !== SCRATCH_SANDBOX) throw new Error(`--scratch runs only in the throwaway ${SCRATCH_SANDBOX} sandbox`);
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

// The sandbox's files: inside .sandbox, never under ProgramData\Jellyfin.
function assertInSandbox(p) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), path.resolve(p));
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(path.resolve(p))) throw new Error(`refusing to touch ${p}`);
    return path.resolve(p);
}
const logFolder = assertInSandbox(path.join(sandboxRoot, 'log'));
const workFolder = assertInSandbox(path.join(sandboxRoot, 'recoveryprobe'));
const storeFolder = assertInSandbox(path.join(sandboxRoot, 'data', 'data', 'accessroles'));
const safeModeFile = assertInSandbox(path.join(storeFolder, 'SAFE-MODE'));
const dataFile = assertInSandbox(path.join(storeFolder, 'accessroles.json'));
const backupFile = assertInSandbox(path.join(storeFolder, 'accessroles.json.bak'));
const tempFile = assertInSandbox(path.join(storeFolder, 'accessroles.json.tmp'));
// libuv's exclusive open on Windows (FILE_SHARE none), which fs.constants doesn't name.
const UV_FS_O_EXLOCK = 0x10000000;

const checks = [];
const transcript = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authHeader(token, deviceId = 'accessroles-recovery-probe') {
    return `MediaBrowser Client="accessroles-recovery-probe", Device="sandbox", DeviceId="${deviceId}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// A request as the admin (credentials.json), one of the probe's users (a token from its sign-in) or anonymous (null).
const tokens = new Map();
function request(method, p, user = 'admin', body = null) {
    return new Promise((resolve, reject) => {
        const token = user === null ? null : user === 'admin' ? credentials.admin.token : tokens.get(user);
        if (user !== null && !token) throw new Error(`no token for ${user}`);
        const headers = { Authorization: authHeader(token, user && user !== 'admin' ? `recoveryprobe-${user.slice(0, 8)}` : undefined), Accept: 'application/json' };
        if (body !== null) headers['Content-Type'] = 'application/json';
        const req = http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let json = null;
                try { json = text && /json/.test(res.headers['content-type'] || '') ? JSON.parse(text) : null; } catch { json = null; }
                resolve({ status: res.statusCode, type: res.headers['content-type'] || '', text, json });
            });
        });
        req.on('error', reject);
        req.setTimeout(120000, () => req.destroy(new Error(`timeout ${method} ${p}`)));
        if (body !== null) req.write(typeof body === 'string' ? body : JSON.stringify(body));
        req.end();
    });
}

// One admin request of the walkthrough, as curl would send it (the token as <admin>, never its value).
async function step(label, method, p, body = null, user = 'admin') {
    const r = await request(method, p, user, body);
    transcript.push({ step: label, curl: `curl -X ${method} "http://127.0.0.1:${port}${baseUrl}${p}" -H "Authorization: MediaBrowser Token=<${user === 'admin' ? 'admin' : 'user'}>"${body !== null ? ` -d '${JSON.stringify(body).slice(0, 300)}'` : ''}`, status: r.status, code: r.json && r.json.Code ? r.json.Code : undefined });
    return r;
}

const idN = (x) => (x || '').replace(/-/g, '').toLowerCase();
const enc = encodeURIComponent;
const locked = (r) => r.status === 403 && !!r.json && r.json.type === LOCKED_TYPE;
const inert = (r) => r.status === 503 && !!r.json && !!r.json.inert;
const stable = (value) => JSON.stringify(value, (k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.keys(v).sort().map((key) => [key, v[key]])) : v));

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

// A native policy save of one of the probe's users, as the Dashboard's user page sends it: the whole policy.
async function postOwnPolicy(id, changes, label) {
    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);
    const policy = await policyOf(id);
    const r = await request('POST', `/Users/${id}/Policy`, 'admin', { ...policy, ...changes });
    transcript.push({ step: label || `set the native policy of the probe's user ${own.get(idN(id))}: ${Object.keys(changes).join(', ')}`, status: r.status });
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

// The Profile lock's refused save: a password change with a wrong current password. Locked, it is 403 Locked before the
// action; not locked, the core refuses the wrong password itself, so nothing changes either way.
async function passwordChange(user) {
    const r = await request('POST', '/Users/Password', idN(user.Id), { CurrentPw: `wrong-${crypto.randomBytes(6).toString('hex')}`, NewPw: crypto.randomBytes(12).toString('base64url') });
    return { status: r.status, locked: locked(r) };
}

// ---- Access Roles' settings, put back in the finally --------------------------------------------------------------
let settingsBefore = null;
const settingsPath = () => `/Plugins/${plugin.guid}/Configuration`;
async function setSettings(label, changes) {
    const current = (await request('GET', settingsPath(), 'admin')).json;
    if (!settingsBefore) settingsBefore = current;
    const r = await step(`Access Roles' settings: ${label}`, 'POST', settingsPath(), { ...current, ...changes });
    if (r.status !== 204) throw new Error(`POST the plugin's settings: ${r.status}`);
}

// The Settings tab's save (its dry run, then the save with its PreviewToken): the page's path, whose save wakes the sync.
async function saveOnSettingsTab(label, changes) {
    if (!settingsBefore) settingsBefore = (await request('GET', settingsPath(), 'admin')).json;
    const dry = await step(`the Settings tab's dry run: ${label}`, 'PUT', '/AccessRoles/Admin/Settings?dryRun=true', changes);
    const save = await step(`the Settings tab's save: ${label}`, 'PUT', `/AccessRoles/Admin/Settings?ifPreview=${enc((dry.json || {}).PreviewToken || '')}`, changes);
    return save.status;
}

// Polls a user's EnableUserPreferenceAccess for up to 20 s: the seconds it took to read the value, or null.
async function flagWithin(user, value, seconds = 20) {
    const t0 = Date.now();
    while (Date.now() - t0 < seconds * 1000) {
        if ((await policyOf(user.Id)).EnableUserPreferenceAccess === value) return Math.round((Date.now() - t0) / 1000);
        await delay(1000);
    }
    return null;
}

async function restoreSettings() {
    if (!settingsBefore) return true;
    const r = await step('Access Roles\' settings as they were', 'POST', settingsPath(), settingsBefore);
    const now = (await request('GET', settingsPath(), 'admin')).json;
    return r.status === 204 && stable(now) === stable(settingsBefore);
}

// ---- the probe's roles and assignments -----------------------------------------------------------------------------
const usersRows = async () => (await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || [];
const rowOf = async (id) => (await usersRows()).find((u) => idN(u.UserId) === idN(id)) || {};
async function createRole(name, locks, libraries) {
    const r = await step(`the probe's role ${name}`, 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}${name}`, Locks: locks, Libraries: libraries || { Mode: 'NotManaged', Refs: [] } });
    if (r.status !== 200 || !r.json || !r.json.Role) throw new Error(`create the role ${name}: ${r.status} ${r.text.slice(0, 200)}`);
    return r.json.Role;
}

async function assignRole(user, role) {
    const row = await rowOf(user.Id);
    const r = await step(`${user.Name} gets the role ${role.Name}`, 'PUT', `/AccessRoles/Admin/Users/${user.Id}`, { RoleIds: [role.Id], IfUpdatedAt: row.UpdatedUtc });
    if (r.status !== 200) throw new Error(`assign ${role.Name} to ${user.Name}: ${r.status} ${r.text.slice(0, 200)}`);
}

const sync = (label = 'POST Sync') => step(label, 'POST', '/AccessRoles/Admin/Sync');
const diagnostics = async () => (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).json || {};
async function audit(action) {
    const r = await request('GET', `/AccessRoles/Admin/Audit?action=${enc(action)}&limit=200`, 'admin');
    return ((r.json || {}).Items || []);
}
const forUser = (lines, id) => lines.filter((l) => idN(l.TargetUserId) === idN(id));
const detail = (line, key) => (line && line.Details ? line.Details[key] : undefined);

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

// ---- cleanup: everything of the probe's, through the API ----------------------------------------------------------------
const cleanupErrors = [];
async function cleanup(label) {
    for (const u of (await usersRows()).filter((x) => x.State !== 'NotManaged' && x.Name.toLowerCase().startsWith(PROBE_USER))) {
        await step(`${label}: unmanage ${u.Name} (Keep)`, 'POST', `/AccessRoles/Admin/Users/${u.UserId}/Unmanage`, { Choice: 'Keep' });
    }
    for (const r of ((await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || []).filter((x) => x.Name.startsWith(PREFIX))) {
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

// The other users' policies, to show the probe wrote none of them.
async function othersPolicies() {
    const users = ((await request('GET', '/Users', 'admin')).json || []).filter((u) => !u.Name.toLowerCase().startsWith(PROBE_USER));
    return Object.fromEntries(users.map((u) => [idN(u.Id), stable(u.Policy)]));
}

// =========================================================================================================================
// The live checks (the access sandbox)
// =========================================================================================================================
async function liveMain() {
    const d0 = await diagnostics();
    evidence.start = { PluginVersion: d0.PluginVersion, Abi: d0.Abi, Store: d0.Store && d0.Store.Status, Degraded: d0.Enforcement && d0.Enforcement.Degraded };
    if (!check('Diagnostics answers the admin: this build, active, the store usable and not degraded', d0.PluginVersion === plugin.version && d0.Abi && d0.Abi.Active === true && d0.Store && d0.Store.Status && !d0.Store.ReadOnly && !(d0.Enforcement && d0.Enforcement.Degraded), evidence.start)) return;
    const othersBefore = await othersPolicies();

    const libraries = ((await request('GET', '/AccessRoles/Admin/Libraries', 'admin')).json || []).filter((l) => l.State !== 'Orphaned');
    if (!check('the sandbox has two libraries or more', libraries.length >= 2, libraries.map((l) => l.Name))) return;
    const [libA] = libraries;

    const kid = await createOwnUser(`${PROBE_USER}kid`);
    const four = await createOwnUser(`${PROBE_USER}four`);
    const lib = await createOwnUser(`${PROBE_USER}lib`);
    const five = await createRole('five locks', FIVE);
    const fourRole = await createRole('four locks', FIVE.filter((l) => l !== 'Subtitles'));
    const narrow = await createRole('one library', [], { Mode: 'Only', Refs: [{ Id: libA.Id }] });
    await assignRole(kid, five);
    await assignRole(four, fourRole);
    await assignRole(lib, narrow);
    await signIn(kid);

    // ---- 1. the mirror --------------------------------------------------------------------------------------------------
    await sync('POST Sync with the mirror off');
    const offBefore = { kid: (await policyOf(kid.Id)).EnableUserPreferenceAccess, four: (await policyOf(four.Id)).EnableUserPreferenceAccess };
    check('the negative control: with MirrorPreferenceLockToPolicy off, nobody\'s preference access is written', offBefore.kid === true && offBefore.four === true, offBefore);

    await setSettings('MirrorPreferenceLockToPolicy on', { MirrorPreferenceLockToPolicy: true });
    await sync('POST Sync with the mirror on');
    const on = { kid: (await policyOf(kid.Id)).EnableUserPreferenceAccess, four: (await policyOf(four.Id)).EnableUserPreferenceAccess, lib: (await policyOf(lib.Id)).EnableUserPreferenceAccess };
    const kidSees = ((await request('GET', `/Users/${kid.Id}`, idN(kid.Id))).json || {}).Policy || {};
    const mirrored = forUser(await audit('PreferenceAccessMirrored'), kid.Id);
    const records = ((await diagnostics()).Records || {}).MirroredPreferenceAccess;
    evidence.mirror = { on, kidSees: kidSees.EnableUserPreferenceAccess, mirrored: mirrored.map((l) => detail(l, 'Reason')), records };
    check('the mirror: the kid (all five sections locked) loses EnableUserPreferenceAccess, which its own GET /Users/{id} reads; the user with four locks and the library-only user keep it; one record; audited AllSectionsLocked',
        on.kid === false && kidSees.EnableUserPreferenceAccess === false && on.four === true && on.lib === true && records === 1 && mirrored.length === 1 && detail(mirrored[0], 'Reason') === 'AllSectionsLocked', evidence.mirror);
    check('the kid\'s Profile lock still refuses its password change (403 Locked)', (await passwordChange(kid)).locked);

    await postOwnPolicy(kid.Id, { EnableUserPreferenceAccess: true }, 'an admin turns the kid\'s preference access back on natively (the whole policy)');
    await sync('POST Sync after the native write');
    const again = forUser(await audit('PreferenceAccessMirrored'), kid.Id).map((l) => detail(l, 'Reason'));
    check('an admin\'s native write of the flag back on is written off again at the next sync (WrittenBackOn)', (await policyOf(kid.Id)).EnableUserPreferenceAccess === false && again.includes('WrittenBackOn'), again);

    // The Settings tab's save wakes the sync: no POST Sync here, and the reconcile interval is
    // minutes away.
    const offSave = await saveOnSettingsTab('MirrorPreferenceLockToPolicy off', { MirrorPreferenceLockToPolicy: false });
    const offAfter = await flagWithin(kid, true);
    const restored = forUser(await audit('PreferenceAccessRestored'), kid.Id).map((l) => detail(l, 'Reason'));
    const recordsOff = ((await diagnostics()).Records || {}).MirroredPreferenceAccess;
    check('the setting saved off on the Settings tab puts the flag back on within 20 s with no POST Sync (PreferenceAccessRestored, MirrorOff) and drops the record',
        offSave === 200 && offAfter !== null && restored.includes('MirrorOff') && recordsOff === 0, { save: offSave, seconds: offAfter, restored, records: recordsOff });
    const onSave = await saveOnSettingsTab('MirrorPreferenceLockToPolicy on again', { MirrorPreferenceLockToPolicy: true });
    const onAfter = await flagWithin(kid, false);
    check('saved on again on the Settings tab: the kid\'s flag off within 20 s with no POST Sync', onSave === 200 && onAfter !== null, { save: onSave, seconds: onAfter });
    await sync('POST Sync with the mirror on again');
    const libNarrow = await policyOf(lib.Id);
    check('again: the kid\'s flag off; the library-only user\'s access is its role\'s one library', (await policyOf(kid.Id)).EnableUserPreferenceAccess === false && libNarrow.EnableAllFolders === false && stable((libNarrow.EnabledFolders || []).map(idN)) === stable([idN(libA.Id)]),
        { EnableAllFolders: libNarrow.EnableAllFolders, EnabledFolders: (libNarrow.EnabledFolders || []).length });

    // ---- 2. Release all locks ---------------------------------------------------------------------------------------
    await signIn(four);
    const nonAdmin = await request('POST', '/AccessRoles/Admin/ReleaseLocks?dryRun=true', idN(four.Id), {});
    check('a non-admin\'s ReleaseLocks is refused (403)', nonAdmin.status === 403, nonAdmin.status);
    const degraded = await step('POST Degraded/Resolve?dryRun=true on a usable store', 'POST', '/AccessRoles/Admin/Degraded/Resolve?dryRun=true', {});
    check('Degraded/Resolve on a store that isn\'t degraded: 409 NotDegraded', degraded.status === 409 && degraded.json && degraded.json.Code === 'NotDegraded', degraded.json && degraded.json.Code);

    const names = (r) => ((r.json || {}).Released || []).map((x) => x.Name).sort();
    const flagsOnly = await step('POST ReleaseLocks?dryRun=true', 'POST', '/AccessRoles/Admin/ReleaseLocks?dryRun=true', {});
    const withLibraries = await step('POST ReleaseLocks?dryRun=true&restoreLibraries=true', 'POST', '/AccessRoles/Admin/ReleaseLocks?dryRun=true&restoreLibraries=true', {});
    const libRow = ((withLibraries.json || {}).Released || []).find((x) => idN(x.UserId) === idN(lib.Id)) || {};
    const kidRow = ((withLibraries.json || {}).Released || []).find((x) => idN(x.UserId) === idN(kid.Id)) || {};
    evidence.releaseDry = { flagsOnly: names(flagsOnly), withLibraries: names(withLibraries), lib: { Libraries: libRow.Libraries, Excluded: libRow.Excluded }, kid: { PreferenceAccess: kidRow.PreferenceAccess, Excluded: kidRow.Excluded } };
    check('the dry run lists only what it writes: the kid (the flag); with restoreLibraries the library-only user too (its original access, every library); both become Excluded; nothing written yet',
        stable(names(flagsOnly)) === stable([kid.Name]) && stable(names(withLibraries)) === stable([kid.Name, lib.Name].sort()) && libRow.Libraries && libRow.Libraries.EnableAllFolders === true && libRow.Excluded === true && kidRow.PreferenceAccess === true && kidRow.Excluded === true
            && (await policyOf(kid.Id)).EnableUserPreferenceAccess === false && !!withLibraries.json.PreviewToken, evidence.releaseDry);

    // A native change of the kid's flag after the preview (an admin's policy save; the pre-merge would put a library change
    // of a managed user back): the confirmation names only the kid, and the library-only user isn't written.
    await postOwnPolicy(kid.Id, { EnableUserPreferenceAccess: true }, 'after the preview, an admin turns the kid\'s preference access on natively');
    const stale = await step('POST ReleaseLocks?restoreLibraries=true with the stale PreviewToken', 'POST', `/AccessRoles/Admin/ReleaseLocks?restoreLibraries=true&ifPreview=${enc(withLibraries.json.PreviewToken)}`, {});
    const staleNames = ((stale.json || {}).Users || []).map((u) => u.Name);
    const untouched = await policyOf(lib.Id);
    check('the stale confirmation: 409 Changed naming only the user who changed, and nothing written (the library-only user still on its one library, both still managed)',
        stale.status === 409 && stale.json && stale.json.Code === 'Changed' && stable(staleNames) === stable([kid.Name]) && untouched.EnableAllFolders === false && (await rowOf(lib.Id)).State === 'Managed' && (await rowOf(kid.Id)).State === 'Managed',
        { status: stale.status, code: stale.json && stale.json.Code, users: staleNames, lib: untouched.EnableAllFolders });

    await postOwnPolicy(kid.Id, { EnableUserPreferenceAccess: false }, 'the kid\'s preference access off again, as the mirror left it');
    const fresh = await step('POST ReleaseLocks?dryRun=true&restoreLibraries=true again', 'POST', '/AccessRoles/Admin/ReleaseLocks?dryRun=true&restoreLibraries=true', {});
    const released = await step('POST ReleaseLocks?restoreLibraries=true with its PreviewToken', 'POST', `/AccessRoles/Admin/ReleaseLocks?restoreLibraries=true&ifPreview=${enc(fresh.json.PreviewToken)}`, {});
    const after = { kid: await policyOf(kid.Id), lib: await policyOf(lib.Id), kidRow: await rowOf(kid.Id), libRow: await rowOf(lib.Id) };
    const releasedLine = (await audit('LocksReleased'))[0];
    const releasedFor = forUser(await audit('PreferenceAccessRestored'), kid.Id).map((l) => detail(l, 'ReleasedFor'));
    evidence.released = { status: released.status, rows: ((released.json || {}).Released || []).map((x) => ({ Name: x.Name, PreferenceOutcome: x.PreferenceOutcome, LibrariesOutcome: x.LibrariesOutcome, Excluded: x.Excluded })), states: [after.kidRow.State, after.libRow.State], audit: releasedLine && releasedLine.Details, releasedFor };
    const kidReleased = ((released.json || {}).Released || []).find((x) => idN(x.UserId) === idN(kid.Id)) || {};
    check('released: the kid\'s flag back on (TurnedOn), the library-only user\'s original access (every library) back, both Excluded with their roles kept, audited LocksReleased (and PreferenceAccessRestored for ReleaseLocks)',
        released.status === 200 && kidReleased.PreferenceOutcome === 'TurnedOn' && after.kid.EnableUserPreferenceAccess === true && after.lib.EnableAllFolders === true && after.kidRow.State === 'Excluded' && after.libRow.State === 'Excluded'
            && (after.kidRow.RoleIds || []).map(idN).includes(idN(five.Id)) && (after.libRow.RoleIds || []).map(idN).includes(idN(narrow.Id)) && !!releasedLine && releasedFor.includes('ReleaseLocks'), evidence.released);

    await sync('POST Sync after the release');
    const later = await step('POST ReleaseLocks?dryRun=true&restoreLibraries=true after the release', 'POST', '/AccessRoles/Admin/ReleaseLocks?dryRun=true&restoreLibraries=true', {});
    check('a later sync writes neither lock again, and a new dry run has nobody to release', (await policyOf(kid.Id)).EnableUserPreferenceAccess === true && (await policyOf(lib.Id)).EnableAllFolders === true && names(later).length === 0, names(later));

    const othersAfter = await othersPolicies();
    check('no policy of a user the probe didn\'t create was written', stable(othersAfter) === stable(othersBefore), Object.keys(othersBefore).filter((k) => othersBefore[k] !== othersAfter[k]));
}

// =========================================================================================================================
// The restart rows (the access-scratch sandbox)
// =========================================================================================================================
const startups = () => logSince(state.StartedUtc).filter((l) => /Startup complete/.test(l.text)).length;
async function restart(label) {
    const before = startups();
    const r = await step(`POST /System/Restart (${label})`, 'POST', '/System/Restart');
    if (r.status !== 204) throw new Error(`restart (${label}): ${r.status}`);
    const t0 = Date.now();
    while (Date.now() - t0 < 180000) {
        await delay(1000);
        if (startups() > before) {
            try {
                if ((await request('GET', '/System/Info/Public', null)).status === 200) {
                    await delay(2000);
                    tokens.clear();
                    return Date.now() - t0;
                }
            } catch { /* still down */ }
        }
    }
    throw new Error(`the server didn't come back after the restart (${label})`);
}

function keepCopy(file) {
    fs.mkdirSync(workFolder, { recursive: true });
    const copy = assertInSandbox(path.join(workFolder, path.basename(file) + '.kept'));
    fs.writeFileSync(copy, fs.readFileSync(file));
    return copy;
}

function putBack(copy, file) {
    fs.writeFileSync(assertInSandbox(file), fs.readFileSync(assertInSandbox(copy)));
}

async function inertAnswers(kid) {
    const script = await request('GET', '/AccessRoles/AdminScript', 'admin');
    const answers = {};
    for (const [method, p] of [['GET', '/AccessRoles/Admin/Diagnostics'], ['GET', '/AccessRoles/Admin/Roles'], ['POST', '/AccessRoles/Admin/ReleaseLocks?dryRun=true'], ['POST', '/AccessRoles/Admin/Sync?dryRun=true']]) {
        const r = await request(method, p, 'admin', method === 'POST' ? {} : null);
        answers[`${method} ${p}`] = { status: r.status, inert: r.json && r.json.inert };
    }
    await signIn(kid);
    const me = await request('GET', '/AccessRoles/Me', idN(kid.Id));
    return { script: script.status, answers, me: { status: me.status, inert: !!(me.json && me.json.inert) }, profileSave: await passwordChange(kid), flag: (await policyOf(kid.Id)).EnableUserPreferenceAccess };
}

async function scratchMain() {
    const network = (await request('GET', '/System/Configuration/network', 'admin')).json || {};
    const loopbackOnly = Array.isArray(network.LocalNetworkAddresses) && network.LocalNetworkAddresses.length > 0 && network.LocalNetworkAddresses.every((a) => a === '127.0.0.1' || a === '::1');
    evidence.network = { LocalNetworkAddresses: network.LocalNetworkAddresses, EnableRemoteAccess: network.EnableRemoteAccess };
    if (!check(`the ${SCRATCH_SANDBOX} sandbox listens on the loopback address only`, loopbackOnly, evidence.network)) return;
    const d0 = await diagnostics();
    if (!check('Diagnostics answers: this build, active', d0.PluginVersion === plugin.version && d0.Abi && d0.Abi.Active === true, { PluginVersion: d0.PluginVersion })) return;
    const libraries = ((await request('GET', '/AccessRoles/Admin/Libraries', 'admin')).json || []).filter((l) => l.State !== 'Orphaned');

    const kid = await createOwnUser(`${PROBE_USER}kid`);
    const pal = await createOwnUser(`${PROBE_USER}pal`);
    const late = await createOwnUser(`${PROBE_USER}late`);
    const viewer = await createOwnUser(`${PROBE_USER}viewer`);
    const five = await createRole('five locks', FIVE);
    await assignRole(kid, five);
    await assignRole(pal, five);
    await sync('POST Sync with the mirror off');
    const flagOf = async (user) => (await policyOf(user.Id)).EnableUserPreferenceAccess;
    const lockedNow = async (user) => {
        await signIn(user);
        return (await passwordChange(user)).locked;
    };
    const mirrorRecords = async () => ((await diagnostics()).Records || {}).MirroredPreferenceAccess;
    const tookBack = () => logSince(state.StartedUtc).filter((l) => /took back the records of 1 users/.test(l.text)).length;

    // ---- S0. the mirror's record before its flag ----------------------------------------------------------------------
    // An exclusive handle on the store's temp file makes every store write fail (as a full disk, an antivirus or a backup
    // tool holding it would): the mirror can't save its record, so it turns no flag off.
    fs.writeFileSync(tempFile, '');
    const held = fs.openSync(tempFile, fs.constants.O_RDWR | UV_FS_O_EXLOCK);
    transcript.push({ step: 'an exclusive handle on data/accessroles/accessroles.json.tmp' });
    let whileHeld;
    try {
        await setSettings('MirrorPreferenceLockToPolicy on (the store\'s temp file held)', { MirrorPreferenceLockToPolicy: true });
        const run = await sync('POST Sync with the store\'s temp file held');
        await delay(4000);
        const aborted = (await audit('WriteAborted')).filter((l) => detail(l, 'Reason') === 'PreferenceMirrorFailed');
        whileHeld = { sync: run.status, kid: await flagOf(kid), pal: await flagOf(pal), records: await mirrorRecords(), aborted: { kid: forUser(aborted, kid.Id).length, pal: forUser(aborted, pal.Id).length } };
    } finally {
        fs.closeSync(held);
        fs.rmSync(tempFile, { force: true });
        transcript.push({ step: 'the handle let go, the temp file removed' });
    }
    evidence.recordFirst = whileHeld;
    check('the store\'s temp file held (every store write fails) with the mirror on: no flag turned off without its record (the kid\'s and the pal\'s flags still on, no record), each audited WriteAborted PreferenceMirrorFailed',
        whileHeld.kid === true && whileHeld.pal === true && whileHeld.records === 0 && whileHeld.aborted.kid >= 1 && whileHeld.aborted.pal >= 1, whileHeld);
    await sync('POST Sync with the temp file let go');
    await signIn(kid);
    await signIn(viewer);
    const seeded = { flag: await flagOf(kid), pal: await flagOf(pal), records: await mirrorRecords(), kid: await passwordChange(kid), viewer: await passwordChange(viewer) };
    check('seeded: the kid and the pal managed (their flags off by the mirror, two records, the kid\'s password change 403 Locked), the viewer not managed (the core answers it)',
        seeded.flag === false && seeded.pal === false && seeded.records === 2 && seeded.kid.locked && !seeded.viewer.locked, seeded);

    // ---- S1. the emergency stop at the start -------------------------------------------------------------------------
    fs.writeFileSync(safeModeFile, '');
    transcript.push({ step: 'create data/accessroles/SAFE-MODE' });
    await restart('with SAFE-MODE');
    const safe = await inertAnswers(kid);
    evidence.safeMode = safe;
    check('SAFE-MODE at the start: the admin script 200, every other action 503 naming the stop (SafeMode), /Me too; the kid\'s locked password change reaches the core (nothing filters it); the flag the mirror turned off stays off',
        safe.script === 200 && Object.values(safe.answers).every((a) => a.status === 503 && a.inert && a.inert.SafeMode === true) && safe.me.status === 503 && !safe.profileSave.locked && safe.profileSave.status !== 503 && safe.flag === false, safe);
    fs.rmSync(safeModeFile);
    transcript.push({ step: 'remove data/accessroles/SAFE-MODE' });
    await restart('SAFE-MODE removed');
    await signIn(kid);
    check('SAFE-MODE removed and restarted: Diagnostics 200, the kid locked again', (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).status === 200 && (await passwordChange(kid)).locked);

    await setSettings('EnforcementEnabled false (the running plugin pauses)', { EnforcementEnabled: false });
    await signIn(kid);
    const paused = { diagnostics: (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).status, kid: await passwordChange(kid) };
    check('EnforcementEnabled false while running: a pause, not inert (Diagnostics 200), and the kid\'s change reaches the core', paused.diagnostics === 200 && !paused.kid.locked, paused);
    await restart('with EnforcementEnabled false');
    const disabled = await inertAnswers(kid);
    evidence.enforcementDisabled = disabled;
    check('EnforcementEnabled false at the start: inert as with SAFE-MODE, the stop named as EnforcementDisabled', disabled.script === 200 && Object.values(disabled.answers).every((a) => a.status === 503 && a.inert && a.inert.EnforcementDisabled === true && a.inert.SafeMode === false) && !disabled.profileSave.locked, disabled);
    // "Turn enforcement back on" on the inert page: GET then POST of the whole configuration with EnforcementEnabled true.
    const config = (await request('GET', settingsPath(), 'admin')).json;
    const back = await step('the inert page\'s "Turn enforcement back on": the whole configuration with EnforcementEnabled true', 'POST', settingsPath(), { ...config, EnforcementEnabled: true });
    await restart('enforcement back on');
    await signIn(kid);
    check('enforcement back on and restarted: Diagnostics 200, the kid locked again', back.status === 204 && (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).status === 200 && (await passwordChange(kid)).locked);

    // ---- S2. Degraded mode ---------------------------------------------------------------------------------------------
    const keptData = keepCopy(dataFile);
    const keptBackup = fs.existsSync(backupFile) ? keepCopy(backupFile) : null;
    const degradedRows = async (label) => {
        const d = await diagnostics();
        await signIn(kid);
        await signIn(viewer);
        return { label, store: d.Store && d.Store.Status, degraded: d.Enforcement && d.Enforcement.Degraded, kid: await passwordChange(kid), viewer: await passwordChange(viewer) };
    };
    const lists = (row) => row.degraded && row.degraded.EveryNonAdmin === false && (row.degraded.Users || []).map(idN).includes(idN(kid.Id)) && !(row.degraded.Users || []).map(idN).includes(idN(viewer.Id));
    const putBackBoth = (label) => {
        putBack(keptData, dataFile);
        if (keptBackup) putBack(keptBackup, backupFile);
        transcript.push({ step: label });
    };

    // (a) Before any snapshot: the data file and its .bak of a broken schema (Roles isn't a list), Assignments intact.
    try {
        const broken = JSON.stringify({ ...JSON.parse(fs.readFileSync(keptData, 'utf8').replace(/^\uFEFF/, '')), Roles: 5 });
        fs.writeFileSync(dataFile, broken);
        if (keptBackup) fs.writeFileSync(backupFile, broken);
        transcript.push({ step: 'the data file and its .bak given a broken schema (Roles: 5), no snapshot' });
        await restart('the data file and .bak unreadable');
        const unreadable = await degradedRows('unreadable');
        evidence.unreadable = unreadable;
        check('the data file and its .bak unreadable at the start, no snapshot: ReadOnlyDataUnreadable, Degraded mode lists the kid (managed in the file, read leniently), not the viewer; the kid locked, the viewer not',
            unreadable.store === 'ReadOnlyDataUnreadable' && lists(unreadable) && unreadable.kid.locked && !unreadable.viewer.locked, unreadable);
        check('the mirror\'s records of the kid and the pal, read from the file, are its evidence (MirrorEvidence)',
            stable(((unreadable.degraded || {}).MirrorEvidence || []).map(idN).sort()) === stable([idN(kid.Id), idN(pal.Id)].sort()), unreadable.degraded && unreadable.degraded.MirrorEvidence);

        // Release all locks on the read-only store: the flag the file's record names put back,
        // nobody made Excluded (the file can't be written), and Degraded mode still locks the kid.
        const roDry = await step('POST ReleaseLocks?dryRun=true for the kid on the read-only store', 'POST', '/AccessRoles/Admin/ReleaseLocks?dryRun=true', { UserIds: [kid.Id] });
        const roRows = (roDry.json || {}).Released || [];
        const roReal = roDry.json && roDry.json.PreviewToken ? await step('POST ReleaseLocks for the kid with its PreviewToken', 'POST', `/AccessRoles/Admin/ReleaseLocks?ifPreview=${enc(roDry.json.PreviewToken)}`, { UserIds: [kid.Id] }) : { status: null };
        const roDegraded = ((await diagnostics()).Enforcement || {}).Degraded || {};
        const ro = { dry: roDry.status, storeReadOnly: roDry.json && roDry.json.StoreReadOnly, rows: roRows.map((x) => ({ Name: x.Name, PreferenceAccess: x.PreferenceAccess, Excluded: x.Excluded })), status: roReal.status, outcome: (((roReal.json || {}).Released || [])[0] || {}).PreferenceOutcome, kid: await flagOf(kid), pal: await flagOf(pal), locked: await lockedNow(kid), evidence: roDegraded.MirrorEvidence };
        evidence.readOnlyRelease = ro;
        check('Release all locks on the read-only store: the dry run names the kid (StoreReadOnly, not to become Excluded), its confirmation turns the kid\'s flag back on (TurnedOn); the kid still locked by Degraded mode, the pal\'s flag still off and still the evidence',
            ro.dry === 200 && ro.storeReadOnly === true && stable(ro.rows.map((x) => x.Name)) === stable([kid.Name]) && ro.rows[0].Excluded === false && ro.status === 200 && ro.outcome === 'TurnedOn' && ro.kid === true && ro.pal === false && ro.locked
                && stable((ro.evidence || []).map(idN)) === stable([idN(pal.Id)]), ro);

        // Then the store put back empty (both files gone): the locks stay, from the earlier start, until confirmed, and the
        // mirror's record of the pal (the marker's evidence) is taken back into the new file.
        fs.rmSync(dataFile);
        if (keptBackup) fs.rmSync(backupFile);
        transcript.push({ step: 'the data file and its .bak removed (the store put back empty)' });
        const tookBackBefore = tookBack();
        await restart('the store put back empty');
        const empty = await degradedRows('empty');
        Object.assign(empty, { records: await mirrorRecords(), pal: await flagOf(pal), tookBack: tookBack() - tookBackBefore });
        evidence.empty = empty;
        check('the store put back empty: Ok, and Degraded mode from the earlier start still locks the kid and the pal (FromEarlierStart, CanResolve), not the viewer; the pal\'s record taken back from the evidence (one record, logged), its flag still off',
            empty.store === 'Ok' && lists(empty) && (empty.degraded.Users || []).map(idN).includes(idN(pal.Id)) && empty.degraded.FromEarlierStart === true && empty.degraded.CanResolve === true && empty.kid.locked && !empty.viewer.locked
                && empty.records === 1 && (empty.degraded.MirrorEvidence || []).length === 0 && empty.tookBack === 1 && empty.pal === false, empty);
        const dry = await step('POST Degraded/Resolve?dryRun=true', 'POST', '/AccessRoles/Admin/Degraded/Resolve?dryRun=true', {});
        const resolved = dry.json && dry.json.PreviewToken ? await step('POST Degraded/Resolve with its PreviewToken', 'POST', `/AccessRoles/Admin/Degraded/Resolve?ifPreview=${enc(dry.json.PreviewToken)}`, {}) : null;
        const palBack = await flagWithin(pal, true);
        const ended = await degradedRows('resolved');
        evidence.resolved = { dry: dry.status, users: dry.json && (dry.json.Users || []).map((u) => u.Name).sort(), resolved: resolved && resolved.status, palBack, palRestored: forUser(await audit('PreferenceAccessRestored'), pal.Id).length, records: await mirrorRecords(), ended };
        check('Degraded/Resolve\'s dry run names the kid and the pal, its confirmation ends Degraded mode: the kid (in no role now) no longer locked, and the sync it wakes puts the pal\'s flag back on within 20 s (PreferenceAccessRestored), no record left',
            dry.status === 200 && stable(evidence.resolved.users) === stable([kid.Name, pal.Name].sort()) && resolved && resolved.status === 200 && !ended.degraded && !ended.kid.locked && palBack !== null && evidence.resolved.palRestored >= 1 && evidence.resolved.records === 0, evidence.resolved);
    } finally {
        putBackBoth('the data file and its .bak put back');
    }
    await restart('the files put back');
    const backRow = await degradedRows('back');
    check('the files put back: Ok, not degraded, the kid locked by its role again', backRow.store === 'Ok' && !backRow.degraded && backRow.kid.locked && !backRow.viewer.locked, backRow);
    await sync('POST Sync after the files were put back');
    const backFlags = { kid: await flagOf(kid), pal: await flagOf(pal), records: await mirrorRecords() };
    check('and the mirror writes the kid\'s and the pal\'s flags off again (their records back with the file)', backFlags.kid === false && backFlags.pal === false && backFlags.records === 2, backFlags);

    // (b) With a snapshot: a user managed after it (the late one), then the data file and its .bak a broken schema.
    // A snapshot of the usable file (the kit's maintenance task).
    const task = ((await request('GET', '/ScheduledTasks', 'admin')).json || []).find((t) => /Access Roles/i.test(t.Name || '') || /AccessRoles/i.test(t.Key || ''));
    if (task) {
        await step(`run ${task.Name} (a snapshot)`, 'POST', `/ScheduledTasks/Running/${task.Id}`);
        for (let i = 0; i < 60; i++) {
            await delay(1000);
            if (((await request('GET', `/ScheduledTasks/${task.Id}`, 'admin')).json || {}).State === 'Idle') break;
        }
    }
    evidence.snapshot = { task: task && task.Name, last: ((await diagnostics()).Store || {}).LastSnapshotUtc };

    await assignRole(late, five);
    await sync('POST Sync: the late user managed after the snapshot');
    const lateBefore = { flag: await flagOf(late), records: await mirrorRecords(), locked: await lockedNow(late) };
    check('after the snapshot: the late user managed (locked), its flag off by the mirror, three records', lateBefore.flag === false && lateBefore.records === 3 && lateBefore.locked, lateBefore);

    try {
        const broken = JSON.stringify({ ...JSON.parse(fs.readFileSync(dataFile, 'utf8').replace(/^﻿/, '')), Roles: 5 });
        fs.writeFileSync(dataFile, broken);
        if (keptBackup) fs.writeFileSync(backupFile, broken);
        transcript.push({ step: 'the data file and its .bak (the late user managed in them) given a broken schema, a snapshot kept' });
        const tookBackBefore = tookBack();
        await restart('unreadable, a snapshot kept');
        const restored = await degradedRows('restored');
        Object.assign(restored, { late: { locked: await lockedNow(late), flag: await flagOf(late) }, records: await mirrorRecords(), tookBack: tookBack() - tookBackBefore });
        evidence.restored = restored;
        check('the data file and its .bak unreadable with a snapshot kept: RestoredFromSnapshot, and Degraded mode locks only the late user, whom the copy moved aside manages and the snapshot doesn\'t (FromEarlierStart, CanResolve); the kid still locked by its role, the viewer not; the late user\'s record taken back (three records, logged), its flag still off',
            restored.store === 'RestoredFromSnapshot' && !!restored.degraded && stable((restored.degraded.Users || []).map(idN)) === stable([idN(late.Id)]) && restored.degraded.FromEarlierStart === true && restored.degraded.CanResolve === true
                && restored.kid.locked && !restored.viewer.locked && restored.late.locked && restored.late.flag === false && restored.records === 3 && restored.tookBack === 1, restored);

        // Degraded mode ended, then the restore hold confirmed: the sync puts the late user's flag back on.
        const lateDry = await step('POST Degraded/Resolve?dryRun=true (the late user)', 'POST', '/AccessRoles/Admin/Degraded/Resolve?dryRun=true', {});
        const lateResolved = lateDry.json && lateDry.json.PreviewToken ? await step('POST Degraded/Resolve with its PreviewToken', 'POST', `/AccessRoles/Admin/Degraded/Resolve?ifPreview=${enc(lateDry.json.PreviewToken)}`, {}) : { status: null };
        const holdDry = await step('POST Sync?dryRun=true (the restore hold)', 'POST', '/AccessRoles/Admin/Sync?dryRun=true');
        const holdConfirm = await step('POST Sync?confirmRestore=true with its PlanToken', 'POST', `/AccessRoles/Admin/Sync?confirmRestore=true&ifPlan=${enc((holdDry.json || {}).PlanToken || '')}`);
        const lateBack = await flagWithin(late, true);
        evidence.hold = { resolveDry: lateDry.status, users: lateDry.json && (lateDry.json.Users || []).map((u) => u.Name), resolved: lateResolved.status, dry: holdDry.status, paused: holdDry.json && holdDry.json.Paused, confirm: holdConfirm.status, lateBack, lateLocked: await lockedNow(late), records: await mirrorRecords() };
        check('Degraded/Resolve names the late user and ends Degraded mode; the restore hold\'s dry run and confirmRestore: the late user (in no role of the snapshot) no longer locked, its flag back on within 20 s, two records',
            lateDry.status === 200 && stable(evidence.hold.users) === stable([late.Name]) && lateResolved.status === 200 && holdConfirm.status === 200 && lateBack !== null && !evidence.hold.lateLocked && evidence.hold.records === 2, evidence.hold);
    } finally {
        putBackBoth('the data file and its .bak put back');
    }
    await restart('the files put back');
    const backAgain = await degradedRows('back again');
    check('the files put back: Ok, not degraded, the kid locked, the viewer not', backAgain.store === 'Ok' && !backAgain.degraded && backAgain.kid.locked && !backAgain.viewer.locked, backAgain);

    // (c) The data file of a newer schema version whose assignments this build reads; (d) one it can't parse.
    try {
        const text = fs.readFileSync(dataFile, 'utf8');
        fs.writeFileSync(dataFile, text.replace(/"Version"\s*:\s*\d+/, '"Version": 99'));
        transcript.push({ step: 'the data file given schema version 99' });
        await restart('a newer schema version');
        const newer = await degradedRows('newer');
        const write = await step('POST Sync on the read-only store', 'POST', '/AccessRoles/Admin/Sync');
        evidence.newer = { ...newer, write: write.status };
        check('the data file of a newer schema version at the start: ReadOnlyNewerVersion, its own assignments read (not degraded), the kid still locked, the viewer not, a write 503',
            newer.store === 'ReadOnlyNewerVersion' && !newer.degraded && newer.kid.locked && !newer.viewer.locked && write.status === 503, evidence.newer);

        // (d) Its Roles a shape this build can't parse: the parse is empty, so Degraded mode locks
        // the users the lenient read finds (fail closed), and only a readable copy put back ends it.
        const future = JSON.parse(text.replace(/^﻿/, ''));
        fs.writeFileSync(dataFile, JSON.stringify({ ...future, Version: 99, Roles: { Future: future.Roles } }));
        transcript.push({ step: 'the data file given schema version 99 and Roles of a shape this build can\'t parse' });
        await restart('a newer schema version this build can\'t parse');
        const unparsed = await degradedRows('newer, unparsed');
        const refused = await step('POST Degraded/Resolve?dryRun=true on the newer version\'s store', 'POST', '/AccessRoles/Admin/Degraded/Resolve?dryRun=true', {});
        evidence.newerUnparsed = { ...unparsed, resolve: refused.status, code: refused.json && refused.json.Code };
        check('the data file of a newer schema version this build can\'t parse: ReadOnlyNewerVersion and Degraded mode listing the kid and the pal (the lenient read), not the viewer, not resolvable (Degraded/Resolve 409 StoreReadOnly); the kid locked, the viewer not',
            unparsed.store === 'ReadOnlyNewerVersion' && lists(unparsed) && (unparsed.degraded.Users || []).map(idN).includes(idN(pal.Id)) && unparsed.degraded.CanResolve === false && refused.status === 409 && evidence.newerUnparsed.code === 'StoreReadOnly'
                && unparsed.kid.locked && !unparsed.viewer.locked, evidence.newerUnparsed);
    } finally {
        putBackBoth('the data file and its .bak put back');
    }
    await restart('the file put back again');
    const again = await degradedRows('again');
    check('the file put back: Ok, not degraded, the kid locked, the viewer not', again.store === 'Ok' && !again.degraded && again.kid.locked && !again.viewer.locked, again);

    // ---- S3. an administrator in a narrow library role -----------------------------------------------------------------
    const boss = await createOwnUser(`${PROBE_USER}admin`);
    await postOwnPolicy(boss.Id, { IsAdministrator: true }, 'the probe\'s user made an administrator');
    const narrow = await createRole('one library', [], { Mode: 'Only', Refs: [{ Id: libraries[0].Id }] });
    await assignRole(boss, narrow);
    await sync('POST Sync: the administrator gets the one library');
    const narrowed2 = await policyOf(boss.Id);
    await signIn(boss);
    const page = { users: (await request('GET', '/AccessRoles/Admin/Users', idN(boss.Id))).status, script: (await request('GET', '/AccessRoles/AdminScript', idN(boss.Id))).status };
    const selfDry = await request('POST', '/AccessRoles/Admin/ReleaseLocks?dryRun=true&restoreLibraries=true', idN(boss.Id), { UserIds: [boss.Id] });
    const self = selfDry.json && selfDry.json.PreviewToken ? await request('POST', `/AccessRoles/Admin/ReleaseLocks?restoreLibraries=true&ifPreview=${enc(selfDry.json.PreviewToken)}`, idN(boss.Id), { UserIds: [boss.Id] }) : { status: null };
    transcript.push({ step: 'the narrow administrator releases its own locks with restoreLibraries (dry run, then the confirmation)', status: [selfDry.status, self.status] });
    const recovered = await policyOf(boss.Id);
    evidence.narrowAdmin = { narrowed: narrowed2.EnableAllFolders, page, dry: selfDry.status, release: self.status, recovered: recovered.EnableAllFolders, state: (await rowOf(boss.Id)).State };
    check('an administrator in a narrow library role (one library) still reaches the plugin\'s page and API, and its own Release all locks with restoreLibraries gives it every library back (Excluded)',
        narrowed2.EnableAllFolders === false && page.users === 200 && page.script === 200 && selfDry.status === 200 && self.status === 200 && recovered.EnableAllFolders === true && evidence.narrowAdmin.state === 'Excluded', evidence.narrowAdmin);

    // ---- S4. the uninstall -----------------------------------------------------------------------------------------------
    const beforeUninstall = { kid: await flagOf(kid), pal: await flagOf(pal) };
    check('before the uninstall: the mirror on, the kid\'s and the pal\'s flags off', beforeUninstall.kid === false && beforeUninstall.pal === false, beforeUninstall);
    const installed = ((await request('GET', '/Plugins', 'admin')).json || []).find((p) => idN(p.Id) === idN(plugin.guid));
    const removed = installed ? await step('uninstall Access Roles through the Dashboard\'s API', 'DELETE', `/Plugins/${installed.Id}/${installed.Version}`) : { status: null };
    const flags = { kid: await flagOf(kid), pal: await flagOf(pal) };
    const log = logSince(state.StartedUtc).filter((l) => /being uninstalled/.test(l.text)).map((l) => l.text.slice(0, 240));
    evidence.uninstall = { version: installed && installed.Version, status: removed.status, flags, log };
    check('uninstalled through the Dashboard with the mirror on: the kid\'s and the pal\'s EnableUserPreferenceAccess back on at once (OnUninstalling), before any restart, and the log says so',
        (removed.status === 204 || removed.status === 200) && flags.kid === true && flags.pal === true && log.some((l) => /put back the preference access of 2 users/.test(l)), evidence.uninstall);
    uninstalled = true;
}

let uninstalled = false;
let failure = null;
try {
    await cleanup('before');
    if (opts.scratch) await scratchMain();
    else await liveMain();
} catch (e) {
    failure = e.stack || String(e);
} finally {
    const cleanupResult = {};
    try {
        if (opts.scratch && fs.existsSync(safeModeFile)) {
            fs.rmSync(safeModeFile);
            cleanupResult.safeModeRemoved = true;
        }
        if (!uninstalled) {
            await cleanup('after');
            cleanupResult.settings = await restoreSettings();
            const roles = ((await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || []).filter((r) => r.Name.startsWith(PREFIX)).length;
            const users = ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Name.toLowerCase().startsWith(PROBE_USER)).length;
            cleanupResult.probeLeft = { roles, users };
        }
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + (e.stack || String(e));
    }
    evidence.cleanup = cleanupResult;
    if (cleanupErrors.length) failure = (failure ? failure + ' | ' : '') + 'cleanup: ' + cleanupErrors.join('; ');
    if (!failure && !uninstalled) {
        const left = cleanupResult.probeLeft || {};
        check('afterwards nothing of the probe\'s is left (its roles and users) and Access Roles\' settings are as before', left.roles === 0 && left.users === 0 && cleanupResult.settings === true, cleanupResult);
    }
    if (!failure && !opts.scratch) {
        const log = logSince(state.StartedUtc).filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text) && (l.level === 'ERR' || l.level === 'FTL'));
        check('the log has no error of Access Roles', log.length === 0, log.map((l) => l.text.slice(0, 240)));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
