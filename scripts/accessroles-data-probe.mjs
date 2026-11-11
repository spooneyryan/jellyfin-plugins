#!/usr/bin/env node
// Access Roles' Data tab and Diagnostics actions (export, import dry run, Merge and Replace) in a running sandbox:
//   1. Export (GET /AccessRoles/Admin/Export): the kit's envelope (jellyfin-plugin-export/1) as an attachment that isn't
//      cached, with the roles, the assignments, the ledger, the mirror's records, the users' names and the settings.
//   2. Import, Merge: the dry run reports what the export adds (a role and an assignment deleted since) and writes nothing;
//      a confirmation whose PreviewToken is stale is a 409 Changed with nothing written; the confirmation takes a snapshot
//      first and puts both back with the export's ids, audited ImportApplied; an export of this server fills in the ledger
//      entry of a user it manages again, one of another server never does.
//   3. Import, Replace: without onUnmanage it is refused (UnmanageChoiceRequired) when it takes a user out of management;
//      with Keep the roles and assignments are the export's, the user out of management keeps its access.
//   4. The lockout guard: an import that would leave no enabled administrator with every library is a 409
//      NoFullAdministrator, its dry run too, and nothing is written.
//   5. Another server's export, users by name: a user found by name gets the role; one the export says was deleted there,
//      and one no user here has the name of, get nothing; libraries by id, then folders, then name, and one that matches
//      nothing is listed and left out, never guessed; the settings never applied; the ledger not imported.
//   6. A deleted user is never resurrected: by id and by name (a new user with the deleted user's name gets nothing).
//   7. Refusals with their codes: NotAnExport, ExportFormatNotSupported, NotThisPlugin, NewerSchemaVersion, a bad mode or
//      mapUsersBy; every Data and Diagnostics action 403 for a non-admin.
//   8. Restore a snapshot (POST Snapshots/Restore): the dry run, then the restore after its own snapshot; the library sync
//      held until its dry run is seen (confirmRestore before it: 409 DryRunRequired), then confirmed.
//   9. Diagnostics' actions: Remap by name (400 without ifPlan, 409 with a stale one, then its PlanToken), Apply deleted-user
//      policy refused for a current user, Run maintenance now (the task runs).
// It writes only what is its own: its users (exportprobe-..., random passwords kept in memory, never printed or saved), their
// policies, its roles ("Export probe ..."), their assignments (Unmanage Keep in a finally) and, through the imports and the
// restore, the store as it was when the probe exported it plus the probe's own records. Every other user's policy and
// Access Roles' settings are compared before and after. Node 22 built-ins only. Loopback only, never port 8096.
//
//   node scripts/accessroles-data-probe.mjs --sandbox access [--out result.json]
//
// Start the sandbox first (scripts/README.md, this probe's section). Prints one JSON result (pass, checks, transcript, evidence)
// and exits with 0 when every check passed.

import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PLUGIN = 'AccessRoles';
const PREFIX = 'Export probe ';
const PROBE_USER = 'exportprobe-';
const FORMAT = 'jellyfin-plugin-export/1';

function usage() {
    return 'usage: node scripts/accessroles-data-probe.mjs --sandbox <name> [--out <file>]';
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
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^\uFEFF/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const credentials = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
if (!credentials.admin || !credentials.admin.token) throw new Error('no admin in credentials.json; run Initialize-Sandbox.ps1');

const yaml = fs.readFileSync(path.join(repoRoot, 'plugins', PLUGIN, 'build.yaml'), 'utf8');
const scalar = (key) => (new RegExp(`^${key}:\\s*"?([^"\\r\\n]+)"?\\s*$`, 'm').exec(yaml) || [])[1];
const plugin = { name: scalar('name'), guid: scalar('guid'), version: scalar('version') };

// The sandbox's files: inside .sandbox, never under ProgramData\Jellyfin. The probe only reads them.
function assertInSandbox(p) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), path.resolve(p));
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(path.resolve(p))) throw new Error(`refusing to touch ${p}`);
    return path.resolve(p);
}
const logFolder = assertInSandbox(path.join(sandboxRoot, 'log'));
const holdFile = assertInSandbox(path.join(sandboxRoot, 'data', 'data', 'accessroles', 'library-sync-hold.json'));

const checks = [];
const transcript = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authHeader(token, deviceId = 'accessroles-data-probe') {
    return `MediaBrowser Client="accessroles-data-probe", Device="sandbox", DeviceId="${deviceId}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// A request as the admin (credentials.json), one of the probe's users (a token from its sign-in) or anonymous (null). A
// string body is sent as it is (an export file), an object as JSON.
const tokens = new Map();
function request(method, p, user = 'admin', body = null) {
    return new Promise((resolve, reject) => {
        const token = user === null ? null : user === 'admin' ? credentials.admin.token : tokens.get(user);
        if (user !== null && !token) throw new Error(`no token for ${user}`);
        const headers = { Authorization: authHeader(token, user && user !== 'admin' ? `exportprobe-${user.slice(0, 8)}` : undefined), Accept: 'application/json' };
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
                try { json = text && /json/.test(res.headers['content-type'] || '') ? JSON.parse(text.replace(/^\uFEFF/, '')) : null; } catch { json = null; }
                resolve({ status: res.statusCode, type: res.headers['content-type'] || '', headers: res.headers, text, json });
            });
        });
        req.on('error', reject);
        req.setTimeout(120000, () => req.destroy(new Error(`timeout ${method} ${p}`)));
        if (payload) req.write(payload);
        req.end();
    });
}

// One request of the walkthrough, as curl would send it (the token as <admin>, never its value; an export body as a file).
async function step(label, method, p, body = null, user = 'admin') {
    const r = await request(method, p, user, body);
    const shown = body === null ? '' : typeof body === 'string' ? ' --data-binary @export.json' : ` -d '${JSON.stringify(body).slice(0, 300)}'`;
    transcript.push({ step: label, curl: `curl -X ${method} "http://127.0.0.1:${port}${baseUrl}${p}" -H "Authorization: MediaBrowser Token=<${user === 'admin' ? 'admin' : 'user'}>"${shown}`, status: r.status, code: r.json && r.json.Code ? r.json.Code : undefined });
    return r;
}

const idN = (x) => (x || '').replace(/-/g, '').toLowerCase();
const enc = encodeURIComponent;
const stable = (value) => JSON.stringify(value, (k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.keys(v).sort().map((key) => [key, v[key]])) : v));
const clone = (x) => JSON.parse(JSON.stringify(x));
const code = (r) => (r.json && r.json.Code) || undefined;

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
const access = (p) => ({ all: p.EnableAllFolders === true, folders: p.EnableAllFolders ? [] : (p.EnabledFolders || []).map(idN).sort() });
const sameAccess = (p, all, folders) => access(p).all === all && (all || stable(access(p).folders) === stable(folders.map(idN).sort()));

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

// ---- Access Roles' API -------------------------------------------------------------------------------------------------
const settingsPath = () => `/Plugins/${plugin.guid}/Configuration`;
const settingsNow = async () => (await request('GET', settingsPath(), 'admin')).json;
const usersRows = async () => (await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || [];
const rowOf = async (id) => (await usersRows()).find((u) => idN(u.UserId) === idN(id)) || {};
const roles = async () => (await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || [];
const roleNamed = async (name) => (await roles()).find((r) => r.Name === `${PREFIX}${name}`);
const diagnostics = async () => (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).json || {};
const snapshots = async () => (await request('GET', '/AccessRoles/Admin/Snapshots', 'admin')).json || {};
const snapshotNames = async () => ((await snapshots()).Snapshots || []).map((s) => s.Name);
const sync = (label = 'POST Sync', query = '') => step(label, 'POST', `/AccessRoles/Admin/Sync${query}`);
async function lastSeq() {
    const items = ((await request('GET', '/AccessRoles/Admin/Audit?limit=1', 'admin')).json || {}).Items || [];
    return items.length ? items[0].Seq : 0;
}
async function auditSince(seq, action) {
    const r = await request('GET', `/AccessRoles/Admin/Audit?action=${enc(action)}&limit=200`, 'admin');
    return (((r.json || {}).Items) || []).filter((l) => l.Seq > seq);
}
const detail = (line, key) => (line && line.Details ? line.Details[key] : undefined);

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

async function unmanage(user, choice) {
    const r = await step(`unmanage ${user.Name} (${choice})`, 'POST', `/AccessRoles/Admin/Users/${user.Id}/Unmanage`, { Choice: choice });
    if (r.status !== 200) throw new Error(`unmanage ${user.Name}: ${r.status} ${r.text.slice(0, 200)}`);
}

async function deleteRole(role) {
    const r = await step(`delete the role ${role.Name}`, 'DELETE', `/AccessRoles/Admin/Roles/${role.Id}?onUnmanage=Keep`);
    if (r.status !== 200 && r.status !== 204) throw new Error(`delete the role ${role.Name}: ${r.status} ${r.text.slice(0, 200)}`);
}

// An import of an export (the envelope as JSON text): its query as the page sends it.
const importQuery = (q) => '?' + Object.entries(q).filter(([, v]) => v !== undefined && v !== null).map(([k, v]) => `${k}=${enc(v)}`).join('&');
const importData = (label, envelope, q) => step(label, 'POST', `/AccessRoles/Admin/Import${importQuery(q)}`, typeof envelope === 'string' ? envelope : JSON.stringify(envelope));
const restoreSnapshot = (label, q) => step(label, 'POST', `/AccessRoles/Admin/Snapshots/Restore${importQuery(q)}`, {});
const counts = (r, kind) => (((r.json || {}).Records || {})[kind]) || {};
const unknownOf = (r) => (((r.json || {}).Users || {}).Unknown || []);
const mappedOf = (r) => (((r.json || {}).Users || {}).Mapped || []);
const previewUsers = (r) => ((((r.json || {}).Preview || {}).Users) || []).map((u) => idN(u.UserId));

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
async function releaseHold(label) {
    // A restore the probe made and didn't finish: its hold's dry run and confirmation, so no other probe finds it held.
    if (!(await snapshots()).SyncHeld) return false;
    const dry = await sync(`${label}: the held sync's dry run`, '?dryRun=true');
    await sync(`${label}: confirmRestore`, `?confirmRestore=true&ifPlan=${enc((dry.json || {}).PlanToken || '')}`);
    return true;
}

async function cleanup(label) {
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

// The other users' policies, to show the probe wrote none of them (the administrators among them).
async function othersPolicies() {
    const users = ((await request('GET', '/Users', 'admin')).json || []).filter((u) => !u.Name.toLowerCase().startsWith(PROBE_USER));
    return Object.fromEntries(users.map((u) => [idN(u.Id), stable(u.Policy)]));
}

// The export's own form of a user id in Users[] (the kit writes "N").
let exportIdForm = (id) => idN(id);

// =========================================================================================================================
async function main() {
    const d0 = await diagnostics();
    evidence.start = { PluginVersion: d0.PluginVersion, Store: d0.Store && d0.Store.Status, Degraded: d0.Enforcement && d0.Enforcement.Degraded, held: (await snapshots()).SyncHeld };
    if (!check('Diagnostics answers the admin: this build, the store usable, not degraded, the sync not held', d0.PluginVersion === plugin.version && d0.Store && !d0.Store.ReadOnly && !(d0.Enforcement && d0.Enforcement.Degraded) && evidence.start.held === false, evidence.start)) return;
    const othersBefore = await othersPolicies();
    const settingsBefore = await settingsNow();

    const libraries = ((await request('GET', '/AccessRoles/Admin/Libraries', 'admin')).json || []).filter((l) => l.State === 'Active' && (l.Locations || []).length > 0);
    if (!check('the sandbox has four libraries with folders or more', libraries.length >= 4, libraries.map((l) => l.Name))) return;
    const [lib0, lib1, lib2, lib3] = libraries;

    const a = await createOwnUser(`${PROBE_USER}a`);
    const b = await createOwnUser(`${PROBE_USER}b`);
    const c = await createOwnUser(`${PROBE_USER}c`);
    const e = await createOwnUser(`${PROBE_USER}e`);
    const f = await createOwnUser(`${PROBE_USER}f`);
    const N = await createRole('narrow', [], { Mode: 'Only', Refs: [{ Id: lib0.Id }] });
    const L = await createRole('locks', ['Home'], { Mode: 'Only', Refs: [{ Id: lib1.Id }] });
    await assignRole(a, N);
    await assignRole(b, L);
    await sync('POST Sync: a and b get their libraries');
    check('set up: a has the first library only, b the second', sameAccess(await policyOf(a.Id), false, [lib0.Id]) && sameAccess(await policyOf(b.Id), false, [lib1.Id]),
        { a: access(await policyOf(a.Id)), b: access(await policyOf(b.Id)) });

    // ---- 1. Export -----------------------------------------------------------------------------------------------------
    const exported = await step('GET Export', 'GET', '/AccessRoles/Admin/Export');
    const E1 = exported.json || {};
    const data1 = E1.Data || {};
    if (E1.Users && E1.Users[0] && /-/.test(E1.Users[0].Id)) exportIdForm = (id) => id;
    const configNow = await settingsNow();
    evidence.export = {
        status: exported.status, type: exported.type, cacheControl: exported.headers['cache-control'], disposition: exported.headers['content-disposition'],
        format: E1.Format, plugin: E1.Plugin, pluginId: E1.PluginId, version: E1.PluginVersion, schema: E1.SchemaVersion, serverId: !!E1.ServerId,
        keys: Object.keys(data1), roles: (data1.Roles || []).length, assignments: (data1.Assignments || []).length, ledger: Object.keys(data1.Ledger || {}).length,
        mirror: data1.PreferenceMirror && typeof data1.PreferenceMirror === 'object', users: (E1.Users || []).map((u) => u.Name).filter((n) => n.startsWith(PROBE_USER)),
        settings: E1.Settings && E1.Settings.ReconcileIntervalMinutes === configNow.ReconcileIntervalMinutes && E1.Settings.EnforcementEnabled === configNow.EnforcementEnabled
    };
    const ledgerIds = Object.keys(data1.Ledger || {}).map(idN);
    check('the export: 200, an attachment (accessroles-export-....json) not cached, the kit\'s envelope of this plugin and version at schema 1 with the server\'s id; its data the roles, the assignments, the default roles, the known libraries, the ledger (a\'s and b\'s entries) and the mirror\'s records; Users[] naming a and b; the settings as they are',
        exported.status === 200 && /application\/json/.test(exported.type) && evidence.export.cacheControl === 'no-store' && /attachment; filename=accessroles-export-\d{8}-\d{6}\.json/.test(evidence.export.disposition || '')
            && E1.Format === FORMAT && E1.Plugin === plugin.name && idN(E1.PluginId) === idN(plugin.guid) && E1.PluginVersion === plugin.version && E1.SchemaVersion === 1 && evidence.export.serverId
            && ['Roles', 'Assignments', 'DefaultRoleIds', 'KnownLibraries', 'Ledger', 'PreferenceMirror'].every((k) => k in data1)
            && (data1.Roles || []).some((r) => idN(r.Id) === idN(N.Id)) && (data1.Roles || []).some((r) => idN(r.Id) === idN(L.Id))
            && ledgerIds.includes(idN(a.Id)) && ledgerIds.includes(idN(b.Id)) && evidence.export.mirror
            && evidence.export.users.includes(a.Name) && evidence.export.users.includes(b.Name) && evidence.export.settings,
        evidence.export);
    const text1 = exported.text;

    // ---- 2. Merge ------------------------------------------------------------------------------------------------------
    await unmanage(b, 'Keep');
    await deleteRole(L);
    await assignRole(c, N);
    await sync('POST Sync: c gets the first library');

    const other = { ...clone(E1), ServerId: 'exportprobe-another-server' };
    const otherDry = await importData('Import dry run (Merge) of the export as another server\'s', other, { mode: 'Merge', dryRun: true });
    evidence.otherServer = { status: otherDry.status, sameServer: otherDry.json && otherDry.json.SameServer, ledger: counts(otherDry, 'Ledger'), notes: otherDry.json && otherDry.json.Notes };
    check('the same export as another server\'s (a dry run): SameServer false, the ledger\'s entries not taken (Added 0) and the notes say so',
        otherDry.status === 200 && otherDry.json.SameServer === false && evidence.otherServer.ledger.Added === 0 && evidence.otherServer.ledger.Incoming >= 2
            && (otherDry.json.Notes || []).some((n) => /another server/.test(n)), evidence.otherServer);

    const before = { snapshots: (await snapshotNames()).length, roles: stable((await roles()).map((r) => r.Id).sort()) };
    const mergeDry = await importData('Import dry run (Merge)', text1, { mode: 'Merge', dryRun: true });
    const afterDry = { snapshots: (await snapshotNames()).length, roles: stable((await roles()).map((r) => r.Id).sort()), L: !!(await roleNamed('locks')), b: (await rowOf(b.Id)).State };
    evidence.mergeDry = { status: mergeDry.status, dryRun: mergeDry.json && mergeDry.json.DryRun, roles: counts(mergeDry, 'Roles'), assignments: counts(mergeDry, 'Assignments'), ledger: counts(mergeDry, 'Ledger'), sameServer: mergeDry.json && mergeDry.json.SameServer, preview: previewUsers(mergeDry).includes(idN(b.Id)), token: !!(mergeDry.json && mergeDry.json.Preview && mergeDry.json.Preview.PreviewToken), afterDry };
    check('the Merge dry run: the role deleted since and b\'s assignment Added, a\'s and c\'s kept (nothing Removed), b\'s ledger entry filled in from this server\'s export (Added 1), b in the preview with a PreviewToken; nothing written (no snapshot, the role not back, b not managed)',
        mergeDry.status === 200 && mergeDry.json.DryRun === true && evidence.mergeDry.roles.Added === 1 && evidence.mergeDry.roles.Removed === 0 && evidence.mergeDry.assignments.Added === 1 && evidence.mergeDry.assignments.Removed === 0
            && evidence.mergeDry.ledger.Added === 1 && mergeDry.json.SameServer === true && evidence.mergeDry.preview && evidence.mergeDry.token
            && afterDry.snapshots === before.snapshots && afterDry.roles === before.roles && !afterDry.L && afterDry.b === 'NotManaged', evidence.mergeDry);

    const S = await createRole('stale', [], { Mode: 'NotManaged', Refs: [] });
    const stale = await importData('Import Merge with the stale PreviewToken', text1, { mode: 'Merge', dryRun: false, ifPreview: mergeDry.json.Preview.PreviewToken });
    const afterStale = { snapshots: (await snapshotNames()).length, L: !!(await roleNamed('locks')), b: (await rowOf(b.Id)).State };
    check('a role created between the dry run and the confirmation: 409 Changed, nothing written (no snapshot, the role not back, b not managed)',
        stale.status === 409 && code(stale) === 'Changed' && afterStale.snapshots === before.snapshots && !afterStale.L && afterStale.b === 'NotManaged', { status: stale.status, code: code(stale), afterStale });

    const seqMerge = await lastSeq();
    const mergeDry2 = await importData('Import dry run (Merge) again', text1, { mode: 'Merge', dryRun: true });
    const merged = await importData('Import Merge with its PreviewToken', text1, { mode: 'Merge', dryRun: false, ifPreview: ((mergeDry2.json || {}).Preview || {}).PreviewToken });
    const X1 = merged.json && merged.json.SafetySnapshot;
    const snaps1 = (await snapshots()).Snapshots || [];
    const Lback = await roleNamed('locks');
    const mergeLines = await auditSince(seqMerge, 'ImportApplied');
    await sync('POST Sync after the Merge');
    evidence.merge = { status: merged.status, dryRun: merged.json && merged.json.DryRun, safety: X1, listed: snaps1.find((s) => s.Name === X1), L: Lback && idN(Lback.Id) === idN(L.Id), b: (await rowOf(b.Id)).State, bAccess: access(await policyOf(b.Id)), lines: mergeLines.map((l) => ({ Source: detail(l, 'Source'), Mode: detail(l, 'Mode'), SameServer: detail(l, 'SameServer'), SafetySnapshot: detail(l, 'SafetySnapshot') })) };
    check('the Merge confirmed: 200, a snapshot taken first (listed, Operation import), the role back with the export\'s id, b managed again with the second library, audited ImportApplied (Export, Merge, this server, the snapshot named)',
        merged.status === 200 && merged.json.DryRun === false && !!X1 && evidence.merge.listed && /import/i.test(evidence.merge.listed.Operation || '') && evidence.merge.L && evidence.merge.b === 'Managed'
            && sameAccess(await policyOf(b.Id), false, [lib1.Id]) && mergeLines.length === 1 && detail(mergeLines[0], 'Source') === 'Export' && detail(mergeLines[0], 'Mode') === 'Merge'
            && detail(mergeLines[0], 'SameServer') === true && detail(mergeLines[0], 'SafetySnapshot') === X1, evidence.merge);

    const again = await importData('Import Merge of the same export once more', text1, { mode: 'Merge', dryRun: false });
    check('the same Merge once more: NoChange, no snapshot', again.status === 200 && again.json.NoChange === true && !again.json.SafetySnapshot && (await snapshotNames()).length === snaps1.length, { status: again.status, noChange: again.json && again.json.NoChange });

    // ---- 3. Replace ----------------------------------------------------------------------------------------------------
    const replaceNoChoice = await importData('Import dry run (Replace) without onUnmanage', text1, { mode: 'Replace', dryRun: true });
    const replaceDry = await importData('Import dry run (Replace) with onUnmanage=Keep', text1, { mode: 'Replace', dryRun: true, onUnmanage: 'Keep' });
    evidence.replaceDry = { noChoice: { status: replaceNoChoice.status, code: code(replaceNoChoice), preview: !!(replaceNoChoice.json && replaceNoChoice.json.Preview) }, status: replaceDry.status, roles: counts(replaceDry, 'Roles'), assignments: counts(replaceDry, 'Assignments') };
    check('the Replace dry run: without onUnmanage 400 UnmanageChoiceRequired with its preview (c leaves management); with Keep the role created since Removed and c\'s assignment Removed',
        replaceNoChoice.status === 400 && code(replaceNoChoice) === 'UnmanageChoiceRequired' && evidence.replaceDry.noChoice.preview
            && replaceDry.status === 200 && evidence.replaceDry.roles.Removed === 1 && evidence.replaceDry.assignments.Removed === 1 && evidence.replaceDry.assignments.Added === 0, evidence.replaceDry);
    const seqReplace = await lastSeq();
    const replaced = await importData('Import Replace with its PreviewToken and onUnmanage=Keep', text1, { mode: 'Replace', dryRun: false, onUnmanage: 'Keep', ifPreview: ((replaceDry.json || {}).Preview || {}).PreviewToken });
    const replaceLines = await auditSince(seqReplace, 'ImportApplied');
    evidence.replace = { status: replaced.status, safety: replaced.json && replaced.json.SafetySnapshot, stale: !!(await roleNamed('stale')), c: (await rowOf(c.Id)).State, cAccess: access(await policyOf(c.Id)), mode: replaceLines.map((l) => detail(l, 'Mode')) };
    check('the Replace confirmed: 200 after a snapshot, the roles and assignments the export\'s (the role created since gone, c out of management keeping the first library), audited ImportApplied (Replace)',
        replaced.status === 200 && !!evidence.replace.safety && !evidence.replace.stale && evidence.replace.c === 'NotManaged' && sameAccess(await policyOf(c.Id), false, [lib0.Id])
            && replaceLines.length === 1 && detail(replaceLines[0], 'Mode') === 'Replace', evidence.replace);

    // ---- 4. The lockout guard ------------------------------------------------------------------------------------------
    const admins = ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Policy && u.Policy.IsAdministrator && !u.Policy.IsDisabled);
    const lock = clone(E1);
    const template = (lock.Data.Assignments || []).find((x) => idN(x.UserId) === idN(a.Id));
    lock.Data.Assignments = (lock.Data.Assignments || []).filter((x) => !admins.some((u) => idN(u.Id) === idN(x.UserId)))
        .concat(admins.map((u) => ({ ...clone(template), UserId: u.Id.length === 32 ? u.Id.replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5') : u.Id, UpdatedUtc: new Date().toISOString() })));
    lock.Users = (lock.Users || []).concat(admins.map((u) => ({ Id: exportIdForm(u.Id), Name: u.Name, Deleted: false })));
    const snapsLock = (await snapshotNames()).length;
    const lockDry = await importData('Import dry run (Merge): every administrator in the narrow role', lock, { mode: 'Merge', dryRun: true });
    const lockSave = lockDry.status === 409 ? await importData('Import Merge without confirmNoFullAdmin', lock, { mode: 'Merge', dryRun: false }) : { status: null };
    const adminsAfter = await othersPolicies();
    evidence.lockout = { admins: admins.length, dry: { status: lockDry.status, code: code(lockDry), preview: !!(lockDry.json && lockDry.json.Preview) }, save: { status: lockSave.status, code: lockSave.json && lockSave.json.Code }, snapshots: (await snapshotNames()).length - snapsLock, adminsUnchanged: admins.every((u) => adminsAfter[idN(u.Id)] === othersBefore[idN(u.Id)]) };
    check('an import putting every administrator in a one-library role: its dry run and its save 409 NoFullAdministrator (the dry run with its preview), nothing written (no snapshot, every administrator\'s policy as before)',
        admins.length >= 1 && lockDry.status === 409 && evidence.lockout.dry.code === 'NoFullAdministrator' && evidence.lockout.dry.preview && lockSave.status === 409 && evidence.lockout.save.code === 'NoFullAdministrator'
            && evidence.lockout.snapshots === 0 && evidence.lockout.adminsUnchanged, evidence.lockout);

    // ---- 5. Another server's export, users by name ---------------------------------------------------------------------
    const known = (data1.KnownLibraries || []).find((k) => idN(k.Id) === idN(lib0.Id)) || { Enabled: true, State: 'Active' };
    const roleTemplate = (data1.Roles || []).find((r) => idN(r.Id) === idN(N.Id));
    const refTemplate = roleTemplate.Libraries.Refs[0];
    const [X, Y, Z, G1, G2, G3, MOVED] = Array.from({ length: 7 }, () => crypto.randomUUID());
    const now = new Date().toISOString();
    const elsewhere = {
        ...clone(E1),
        ServerId: 'exportprobe-another-server',
        Settings: { ...clone(E1.Settings), ReconcileIntervalMinutes: (E1.Settings.ReconcileIntervalMinutes || 5) + 7, TrimPublicUsers: !E1.Settings.TrimPublicUsers },
        Users: [{ Id: exportIdForm(G1), Name: e.Name, Deleted: false }, { Id: exportIdForm(G2), Name: f.Name, Deleted: true }, { Id: exportIdForm(G3), Name: `${PROBE_USER}ghost`, Deleted: false }],
        Data: {
            ...clone(data1),
            Roles: [{
                ...clone(roleTemplate), Id: MOVED, Name: `${PREFIX}moved`, UpdatedUtc: now, CreatedUtc: now,
                Libraries: {
                    ...clone(roleTemplate.Libraries), Mode: 'Only', Refs: [
                        { ...clone(refTemplate), Id: X, NameAtSelection: `${PREFIX}renamed there`, Locations: lib2.Locations },
                        { ...clone(refTemplate), Id: Z, NameAtSelection: lib3.Name, Locations: ['C:\\exportprobe-elsewhere\\folder'] },
                        { ...clone(refTemplate), Id: Y, NameAtSelection: `${PREFIX}no such library`, Locations: ['C:\\exportprobe-nowhere'] }
                    ]
                }
            }],
            Assignments: [G1, G2, G3].map((g) => ({ ...clone(template), UserId: g, RoleIds: [MOVED], UpdatedUtc: now })),
            DefaultRoleIds: [],
            KnownLibraries: [
                { ...clone(known), Id: X, Name: `${PREFIX}renamed there`, Locations: lib2.Locations },
                { ...clone(known), Id: Z, Name: lib3.Name, Locations: ['C:\\exportprobe-elsewhere\\folder'] },
                { ...clone(known), Id: Y, Name: `${PREFIX}no such library`, Locations: ['C:\\exportprobe-nowhere'] }
            ],
            Ledger: { [G1]: { ...clone(Object.values(data1.Ledger)[0]), OriginalEnableAllFolders: false, OriginalEnabledFolders: [] } },
            PreferenceMirror: {},
            KnownUsers: {}
        }
    };
    const fPolicy = stable(await policyOf(f.Id));
    const byNameDry = await importData('Import dry run (Merge, users by name) of another server\'s export', elsewhere, { mode: 'Merge', dryRun: true, mapUsersBy: 'Name' });
    const libs = (byNameDry.json || {}).Libraries || {};
    const mappedLib = (id) => (libs.Mapped || []).find((m) => idN(m.ExportId) === idN(id)) || {};
    const unknownBy = Object.fromEntries(unknownOf(byNameDry).map((u) => [idN(u.Id), u.Reason]));
    evidence.byName = {
        status: byNameDry.status, sameServer: byNameDry.json && byNameDry.json.SameServer, by: byNameDry.json && byNameDry.json.MapUsersBy,
        mapped: mappedOf(byNameDry).map((m) => ({ id: idN(m.Id) === idN(G1) ? 'G1' : m.Id, newId: idN(m.NewId) === idN(e.Id) ? 'e' : m.NewId, name: m.Name })),
        unknown: { G2: unknownBy[idN(G2)], G3: unknownBy[idN(G3)] },
        libraries: { X: [mappedLib(X).By, idN(mappedLib(X).Id) === idN(lib2.Id)], Z: [mappedLib(Z).By, idN(mappedLib(Z).Id) === idN(lib3.Id)], Y: ((libs.Unmatched || []).find((u) => idN(u.ExportId) === idN(Y)) || {}).Reason },
        skipped: ((byNameDry.json || {}).Skipped || []).map((s) => `${s.Kind}/${s.Reason}`), ledger: counts(byNameDry, 'Ledger'), notes: (byNameDry.json || {}).Notes || []
    };
    check('another server\'s export by name (the dry run): e found by name; f (deleted on the exporting server) and a name no user has unknown with their reasons; the library by its folders, the one by its name, the one that matches nothing Unmatched (NoSuchLibrary) and left out of the role; the ledger not taken; the notes name the settings, the other server, the libraries and the users',
        byNameDry.status === 200 && evidence.byName.sameServer === false && evidence.byName.by === 'Name' && evidence.byName.mapped.length === 1 && evidence.byName.mapped[0].id === 'G1' && evidence.byName.mapped[0].newId === 'e'
            && unknownBy[idN(G2)] === 'DeletedOnExportingServer' && unknownBy[idN(G3)] === 'NoUserWithThisName'
            && evidence.byName.libraries.X[0] === 'Locations' && evidence.byName.libraries.X[1] && evidence.byName.libraries.Z[0] === 'Name' && evidence.byName.libraries.Z[1] && evidence.byName.libraries.Y === 'NoSuchLibrary'
            && evidence.byName.skipped.includes('RoleLibrary/NoSuchLibrary') && evidence.byName.ledger.Added === 0 && evidence.byName.notes.length >= 4, evidence.byName);
    const byName = await importData('Import Merge (users by name) with its PreviewToken', elsewhere, { mode: 'Merge', dryRun: false, mapUsersBy: 'Name', ifPreview: ((byNameDry.json || {}).Preview || {}).PreviewToken });
    await sync('POST Sync after the import by name');
    const moved = await roleNamed('moved');
    const settingsAfter = await settingsNow();
    evidence.byNameSaved = {
        status: byName.status, moved: moved && moved.Libraries && moved.Libraries.Refs.map((r) => (idN(r.Id) === idN(lib2.Id) ? 'lib2' : idN(r.Id) === idN(lib3.Id) ? 'lib3' : r.Id)),
        e: (await rowOf(e.Id)).State, eAccess: access(await policyOf(e.Id)), f: (await rowOf(f.Id)).State, fUnchanged: stable(await policyOf(f.Id)) === fPolicy,
        ghost: ((await request('GET', '/Users', 'admin')).json || []).some((u) => u.Name === `${PROBE_USER}ghost`), settingsUnchanged: stable(settingsAfter) === stable(settingsBefore)
    };
    check('saved: the role holds the two libraries found (not the unmatched one), e has them, f is not managed and its policy is as before, no user was made for the unknown name, and Access Roles\' settings are as before (never applied)',
        byName.status === 200 && stable(evidence.byNameSaved.moved) === stable(['lib2', 'lib3']) && evidence.byNameSaved.e === 'Managed' && sameAccess(await policyOf(e.Id), false, [lib2.Id, lib3.Id])
            && evidence.byNameSaved.f === 'NotManaged' && evidence.byNameSaved.fUnchanged && !evidence.byNameSaved.ghost && evidence.byNameSaved.settingsUnchanged, evidence.byNameSaved);

    // ---- 6. A deleted user -------------------------------------------------------------------------------------------
    const g = await createOwnUser(`${PROBE_USER}gone`);
    await assignRole(g, N);
    const exported2 = await step('GET Export with the user who is deleted next', 'GET', '/AccessRoles/Admin/Export');
    const text2 = exported2.text;
    await deleteOwnUser(g.Id);
    const g2 = await createOwnUser(`${PROBE_USER}gone`);
    const g2Policy = stable(await policyOf(g2.Id));
    const goneById = await importData('Import dry run (Merge, by id) of the export with the deleted user', text2, { mode: 'Merge', dryRun: true });
    const goneByNameDry = await importData('Import dry run (Merge, by name) with a new user of the deleted user\'s name', text2, { mode: 'Merge', dryRun: true, mapUsersBy: 'Name' });
    const goneByName = await importData('Import Merge (by name) with its PreviewToken', text2, { mode: 'Merge', dryRun: false, mapUsersBy: 'Name', ifPreview: ((goneByNameDry.json || {}).Preview || {}).PreviewToken });
    const reasonOf = (r) => (unknownOf(r).find((u) => idN(u.Id) === idN(g.Id)) || {}).Reason;
    evidence.deleted = {
        byId: { status: goneById.status, reason: reasonOf(goneById) }, byName: { status: goneByNameDry.status, reason: reasonOf(goneByNameDry), mappedNew: mappedOf(goneByNameDry).some((m) => idN(m.NewId) === idN(g2.Id)) },
        saved: { status: goneByName.status, noChange: goneByName.json && goneByName.json.NoChange }, g2: (await rowOf(g2.Id)).State, g2Unchanged: stable(await policyOf(g2.Id)) === g2Policy
    };
    check('a deleted user is never resurrected: its records DeletedOnThisServer by id and by name, the new user with its name not mapped; the save changes nothing (NoChange), the new user not managed and its policy as before',
        goneById.status === 200 && evidence.deleted.byId.reason === 'DeletedOnThisServer' && goneByNameDry.status === 200 && evidence.deleted.byName.reason === 'DeletedOnThisServer' && !evidence.deleted.byName.mappedNew
            && goneByName.status === 200 && evidence.deleted.saved.noChange === true && evidence.deleted.g2 === 'NotManaged' && evidence.deleted.g2Unchanged, evidence.deleted);

    // ---- 7. Refusals -----------------------------------------------------------------------------------------------------
    const snapsCodes = (await snapshotNames()).length;
    const cases = [
        ['not JSON', 'this is not an export', {}, 400, 'NotAnExport'],
        ['JSON without a format', JSON.stringify({ Hello: 'world' }), {}, 400, 'NotAnExport'],
        ['another format version', JSON.stringify({ ...E1, Format: 'jellyfin-plugin-export/2' }), {}, 400, 'ExportFormatNotSupported'],
        ['another plugin\'s export', JSON.stringify({ ...E1, Plugin: 'Bookmarks', PluginId: crypto.randomUUID() }), {}, 400, 'NotThisPlugin'],
        ['a newer schema', JSON.stringify({ ...E1, SchemaVersion: 99, Data: { ...E1.Data, Version: 99 } }), {}, 409, 'NewerSchemaVersion'],
        ['a bad mode', text1, { mode: 'Overwrite' }, 400, null],
        ['a bad mapUsersBy', text1, { mapUsersBy: 'Email' }, 400, null]
    ];
    const answers = [];
    for (const [label, body, q, status, wanted] of cases) {
        const r = await importData(`Import: ${label}`, body, { dryRun: false, ...q });
        answers.push({ label, status: r.status, code: code(r), pass: r.status === status && (wanted === null || code(r) === wanted) });
    }
    evidence.refusals = { answers, snapshots: (await snapshotNames()).length - snapsCodes };
    check('the refusals with their codes, none writing (no snapshot): NotAnExport (twice), ExportFormatNotSupported, NotThisPlugin, NewerSchemaVersion, a bad mode or mapUsersBy',
        answers.every((x) => x.pass) && evidence.refusals.snapshots === 0, evidence.refusals);

    await signIn(a);
    const asUser = [];
    for (const [method, p, body] of [['GET', '/AccessRoles/Admin/Export', null], ['POST', '/AccessRoles/Admin/Import?dryRun=true', text1], ['GET', '/AccessRoles/Admin/Snapshots', null], ['POST', `/AccessRoles/Admin/Snapshots/Restore?name=${enc(X1)}`, {}], ['POST', '/AccessRoles/Admin/Users/Remap?by=Name&dryRun=true', {}], ['POST', '/AccessRoles/Admin/Users/ApplyPolicy', { UserIds: [crypto.randomUUID()] }], ['POST', '/AccessRoles/Admin/Maintenance/Run', {}]]) {
        const r = await request(method, p, idN(a.Id), body);
        asUser.push({ action: `${method} ${p.split('?')[0]}`, status: r.status });
    }
    transcript.push({ step: 'every Data and Diagnostics action as a non-admin', status: asUser.map((x) => x.status) });
    check('every Data and Diagnostics action answers a non-admin 403', asUser.every((x) => x.status === 403), asUser);

    // ---- 8. Restore a snapshot ---------------------------------------------------------------------------------------
    // X1 is the store before the Merge: N and the stale role; a and c in N; b and e not managed. c is given every library
    // natively first, so the hold shows: the restore manages it again, and only the confirmed sync narrows it.
    await postOwnPolicy(c.Id, { EnableAllFolders: true, EnabledFolders: [] }, 'c (not managed) given every library natively');
    const noName = await restoreSnapshot('Snapshots/Restore without a name', { dryRun: true });
    const noSuch = await restoreSnapshot('Snapshots/Restore of a name no snapshot has', { name: 'accessroles-import-20000101-000000.json', dryRun: true });
    const restoreNoChoice = await restoreSnapshot('Snapshots/Restore dry run without onUnmanage', { name: X1, dryRun: true });
    const restoreDry = await restoreSnapshot('Snapshots/Restore dry run with onUnmanage=Restore', { name: X1, dryRun: true, onUnmanage: 'Restore' });
    evidence.restoreDry = {
        noName: noName.status, noSuch: noSuch.status, noChoice: { status: restoreNoChoice.status, code: code(restoreNoChoice) },
        status: restoreDry.status, source: restoreDry.json && [restoreDry.json.Source, restoreDry.json.SourceName === X1, restoreDry.json.Mode], roles: counts(restoreDry, 'Roles'), assignments: counts(restoreDry, 'Assignments'), ledger: counts(restoreDry, 'Ledger'), held: (await snapshots()).SyncHeld
    };
    check('the restore\'s dry run: 400 without a name, 404 for a name no snapshot has, 400 UnmanageChoiceRequired without onUnmanage (b and e leave management); with Restore a Replace from the snapshot (two roles Removed, the stale one Added, c\'s assignment Added and b\'s and e\'s Removed, c\'s ledger entry from the snapshot), nothing held yet',
        noName.status === 400 && noSuch.status === 404 && restoreNoChoice.status === 400 && evidence.restoreDry.noChoice.code === 'UnmanageChoiceRequired'
            && restoreDry.status === 200 && restoreDry.json.Source === 'Snapshot' && restoreDry.json.SourceName === X1 && restoreDry.json.Mode === 'Replace'
            && evidence.restoreDry.roles.Removed === 2 && evidence.restoreDry.roles.Added === 1 && evidence.restoreDry.assignments.Added === 1 && evidence.restoreDry.assignments.Removed === 2 && evidence.restoreDry.ledger.Added === 1
            && evidence.restoreDry.held === false, evidence.restoreDry);

    const seqRestore = await lastSeq();
    const restored = await restoreSnapshot('Snapshots/Restore with its PreviewToken and onUnmanage=Restore', { name: X1, dryRun: false, onUnmanage: 'Restore', ifPreview: ((restoreDry.json || {}).Preview || {}).PreviewToken });
    const list = await snapshots();
    const restoreLines = await auditSince(seqRestore, 'ImportApplied');
    const heldNow = { listed: list.SyncHeld, paused: ((await diagnostics()).LibrarySync || {}).PausedBecause, file: fs.existsSync(holdFile), cAccess: access(await policyOf(c.Id)) };
    await delay(2000);
    heldNow.cAfter2s = access(await policyOf(c.Id));
    evidence.restore = { status: restored.status, held: restored.json && restored.json.SyncHeld, safety: restored.json && restored.json.SafetySnapshot, operation: ((list.Snapshots || []).find((s) => s.Name === (restored.json || {}).SafetySnapshot) || {}).Operation, heldNow, lines: restoreLines.map((l) => [detail(l, 'Source'), detail(l, 'SourceName') === X1]), stale: !!(await roleNamed('stale')), L: !!(await roleNamed('locks')), moved: !!(await roleNamed('moved')), c: (await rowOf(c.Id)).State };
    check('restored: 200 after its own snapshot (Operation restore), the stale role back and the two others gone, c managed again; the library sync held (Snapshots SyncHeld, Diagnostics StoreRestored, the hold file), so c keeps every library; audited ImportApplied (Snapshot, its name)',
        restored.status === 200 && evidence.restore.held === true && !!evidence.restore.safety && /restore/i.test(evidence.restore.operation || '') && evidence.restore.stale && !evidence.restore.L && !evidence.restore.moved && evidence.restore.c === 'Managed'
            && heldNow.listed === true && heldNow.paused === 'StoreRestored' && heldNow.file && heldNow.cAccess.all && heldNow.cAfter2s.all
            && restoreLines.length === 1 && detail(restoreLines[0], 'Source') === 'Snapshot' && detail(restoreLines[0], 'SourceName') === X1, evidence.restore);

    const early = await sync('POST Sync?confirmRestore=true before the dry run', '?confirmRestore=true');
    const holdDry = await sync('POST Sync?dryRun=true (the held sync\'s dry run)', '?dryRun=true');
    const confirm = await sync('POST Sync?confirmRestore=true with its PlanToken', `?confirmRestore=true&ifPlan=${enc((holdDry.json || {}).PlanToken || '')}`);
    await delay(1000);
    evidence.confirm = {
        early: { status: early.status, code: code(early) }, dry: { status: holdDry.status, paused: holdDry.json && holdDry.json.Paused }, confirm: { status: confirm.status, paused: confirm.json && confirm.json.Paused },
        held: (await snapshots()).SyncHeld, file: fs.existsSync(holdFile), c: access(await policyOf(c.Id)), b: access(await policyOf(b.Id)), e: access(await policyOf(e.Id)), bState: (await rowOf(b.Id)).State, eState: (await rowOf(e.Id)).State
    };
    check('the hold: confirmRestore before the dry run 409 DryRunRequired; the dry run (Paused StoreRestored), then the confirmation lifts it (no file, SyncHeld false); c has the first library only, b and e (Restore) every library again and not managed',
        early.status === 409 && evidence.confirm.early.code === 'DryRunRequired' && holdDry.status === 200 && evidence.confirm.dry.paused === 'StoreRestored' && confirm.status === 200 && !evidence.confirm.confirm.paused
            && evidence.confirm.held === false && !evidence.confirm.file && sameAccess(await policyOf(c.Id), false, [lib0.Id]) && evidence.confirm.b.all && evidence.confirm.e.all
            && evidence.confirm.bState === 'NotManaged' && evidence.confirm.eState === 'NotManaged', evidence.confirm);

    // ---- 9. Diagnostics' actions ---------------------------------------------------------------------------------------
    const remapNoPlan = await step('Users/Remap without ifPlan', 'POST', '/AccessRoles/Admin/Users/Remap?by=Name&dryRun=false', {});
    const remapDry = await step('Users/Remap dry run', 'POST', '/AccessRoles/Admin/Users/Remap?by=Name&dryRun=true', {});
    const remapStale = await step('Users/Remap with a PlanToken that isn\'t the dry run\'s', 'POST', '/AccessRoles/Admin/Users/Remap?by=Name&dryRun=false&ifPlan=0000', {});
    const remap = await step('Users/Remap with its PlanToken', 'POST', `/AccessRoles/Admin/Users/Remap?by=Name&dryRun=false&ifPlan=${enc((remapDry.json || {}).PlanToken || '')}`, {});
    evidence.remap = { noPlan: remapNoPlan.status, dry: { status: remapDry.status, token: !!(remapDry.json || {}).PlanToken, mapped: ((remapDry.json || {}).Mapped || []).length }, stale: remapStale.status, remap: { status: remap.status, moved: (remap.json || {}).MovedRecords } };
    check('Remap by name: 400 without ifPlan, the dry run with its PlanToken (no unknown id here: nothing to map), 409 with another token, 200 with its own (nothing moved)',
        remapNoPlan.status === 400 && remapDry.status === 200 && evidence.remap.dry.token && evidence.remap.dry.mapped === 0 && remapStale.status === 409 && remap.status === 200 && evidence.remap.remap.moved === 0, evidence.remap);

    const policyCurrent = await step('Users/ApplyPolicy with a current user\'s id', 'POST', '/AccessRoles/Admin/Users/ApplyPolicy', { UserIds: [a.Id] });
    check('Apply deleted-user policy refuses a current user\'s id (400) and a keeps its assignment', policyCurrent.status === 400 && (await rowOf(a.Id)).State === 'Managed', { status: policyCurrent.status, a: (await rowOf(a.Id)).State });

    const tasks = (await request('GET', '/ScheduledTasks', 'admin')).json || [];
    const task = tasks.find((t) => /Access Roles/i.test(t.Name || '') && /Maintenance/i.test(t.Name || ''));
    const lastBefore = task && task.LastExecutionResult ? task.LastExecutionResult.EndTimeUtc : null;
    const run = await step('Maintenance/Run', 'POST', '/AccessRoles/Admin/Maintenance/Run', {});
    let ran = null;
    for (let i = 0; task && i < 60 && !ran; i++) {
        await delay(1000);
        const t = (await request('GET', `/ScheduledTasks/${task.Id}`, 'admin')).json || {};
        if (t.State === 'Idle' && t.LastExecutionResult && t.LastExecutionResult.EndTimeUtc !== lastBefore) ran = t.LastExecutionResult;
    }
    evidence.maintenance = { status: run.status, queued: run.json && run.json.Queued, task: run.json && run.json.Task, found: !!task, ran: ran && { status: ran.Status, end: ran.EndTimeUtc } };
    check('Run maintenance now: 200 Queued, and the "Access Roles: Maintenance" task runs to Completed', run.status === 200 && evidence.maintenance.queued === true && !!ran && ran.Status === 'Completed', evidence.maintenance);

    const orphans = (((await diagnostics()).Users || {}).OrphanUsers) || [];
    check('Diagnostics lists no unknown user id afterwards (the deleted user\'s records went with it)', orphans.length === 0, orphans.map((o) => o.LastKnownName));

    evidence.othersUnchanged = await othersPolicies();
    const changed = Object.keys(othersBefore).filter((id) => evidence.othersUnchanged[id] !== othersBefore[id]);
    evidence.othersUnchanged = { users: Object.keys(othersBefore).length, changed };
    check('no other user\'s policy was written (the administrators\' among them)', changed.length === 0, evidence.othersUnchanged);
    evidence.settingsBefore = settingsBefore;
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
        cleanupResult.holdReleased = await releaseHold('after');
        await cleanup('after');
        const settings = await settingsNow();
        cleanupResult.settings = !evidence.settingsBefore || stable(settings) === stable(evidence.settingsBefore);
        delete evidence.settingsBefore;
        const left = {
            roles: (await roles()).filter((r) => r.Name.startsWith(PREFIX)).length,
            users: ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Name.toLowerCase().startsWith(PROBE_USER)).length,
            held: (await snapshots()).SyncHeld
        };
        cleanupResult.probeLeft = left;
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + (e.stack || String(e));
    }
    evidence.cleanup = cleanupResult;
    if (cleanupErrors.length) failure = (failure ? failure + ' | ' : '') + 'cleanup: ' + cleanupErrors.join('; ');
    if (!failure) {
        const left = cleanupResult.probeLeft || {};
        check('afterwards nothing of the probe\'s is left (its roles and users), the sync isn\'t held, and Access Roles\' settings are as before', left.roles === 0 && left.users === 0 && left.held === false && cleanupResult.settings === true, cleanupResult);
        const log = logSince(startedIso).filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text) && (l.level === 'ERR' || l.level === 'FTL'));
        check('the log has no error of Access Roles', log.length === 0, log.map((l) => l.text.slice(0, 240)));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
