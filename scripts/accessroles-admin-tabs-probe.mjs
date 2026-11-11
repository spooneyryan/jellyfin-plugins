#!/usr/bin/env node
// Access Roles' admin page, its Libraries, Audit, Settings, Data and Diagnostics tabs, in a running sandbox:
//   1. The Settings API (GET and PUT /AccessRoles/Admin/Settings): the dry run shows the change and its PreviewToken and
//      saves nothing; a save with the token is saved although another administrator changed another setting meanwhile
//      (the body carries only what changes, and the other change stays); a save whose token another administrator's change
//      of the same setting made stale is a 409 Changed naming it with nothing saved, and the same save without the token
//      is saved (the negative control); adding a key that carries secrets to the configuration-read allow-list is a 400
//      SecretKeys without confirmSecretKeys, nothing saved; each save is one SettingsChanged line by the admin.
//   2. The probe's three libraries (empty folders under .sandbox/<name>/tabsprobe), its roles "Tabs probe Pair" (Only the
//      three) and "Tabs probe Gone only" (Only one) and its users tabsprobe-kid (Pair) and tabsprobe-teen (Gone only):
//      GET Libraries says who opens each library and why; one library renamed to a 81-character name (one unbroken word
//      after the prefix) is re-pointed with a LibraryRenamed line naming its new id; one deleted is Orphaned with the roles'
//      references kept, so "Gone only" grants nothing and the teen has no library; one deleted and created again under the
//      same id is Recreated, and nobody opens it.
//   3. The drift's per-user scope (POST Sync?userId=) under DriftPolicy ReportOnly, both users' library access changed
//      natively: the kid's is drift, the teen's isn't (the roles would leave it no library, and nobody chose that: the
//      sync leaves its access as it is); the kid's dry run lists only the kid, with a PlanToken;
//      a userId of no value is a 400, nothing written; another administrator changes the kid's own
//      library access, so the write with ifPlan is a 409 Changed naming only the kid, nothing written,
//      and once it is put back the same token is good again; another administrator then adds a library for the kid,
//      so the write with ifPlan is a 409 Changed naming only the kid, nothing written; the same write without ifPlan (the
//      negative control) writes the kid alone, the teen left; the kid is put back to Pair alone, then its access changed
//      natively again (one drifted user in Diagnostics).
//   4. With --webcheck, the web client as admin: on a 375 px touch phone (webchecks/accessroles-admin-tabs-phone.json:
//      Libraries, Audit, Settings, Data and Diagnostics fit, every control 44 px, the long library name wrapping), on the
//      TV layout (webchecks/accessroles-admin-tabs-tv.json: the remembered Libraries tab, its cards and the audit lines in the
//      D-pad's reach, the settings review opened with OK and closed with Backspace, the focus back on Review, nothing
//      saved), and at 1366 px (webchecks/accessroles-admin-tabs-desktop.json): the Overview's chips and server-wide box; the
//      Libraries tab's renamed, deleted and created-again cards, Grant again and Remove references each previewed and
//      saved; the kid's Revert this user previewed for the kid alone and written; the Audit tab's lines, its action
//      filter and Download (the export captured in the page, never saved to disk); the Settings tab's change previewed,
//      another administrator's change of the same setting making Save a 409, Look again and Save; the Data tab; the
//      Diagnostics tab's tables and rule search. Then through the API: the kid's access is the roles' (the library given
//      again and the renamed one) with no drift, the teen as it was, the deleted library's references are gone, and the audit lines of
//      each write by the admin.
//   5. A settings save applies at once: the kid drifted under ReportOnly, saving DriftPolicy
//      Reapply wakes the library sync, whose run (trigger SettingsChanged) writes the kid back within 15 s; the teen's own
//      access is left.
//   6. Afterwards nothing of the probe's is left (libraries, folders, roles, users), the settings are as they were, every
//      other user's policy was never written, and the log has no error of Access Roles' since the probe started.
// A rule name joined with + (an action with more than one hardening rule) doesn't arise on 10.11.5: no Jellyfin.Api
// action has two hardening rules, so the page tests and the server tests cover it, not this probe.
// It writes only what is its own: its users (tabsprobe-..., random passwords kept in memory, never printed or saved, and
// never used to sign in), their assignments and library access, its roles ("Tabs probe ..."), its libraries ("Tabs probe
// ..."), and the plugin's settings DriftPolicy and ReconcileIntervalMinutes, put back at the end. Each webcheck is run up
// to five times when only its first sign-in fails (the login page's user cards, a known flake). Node 22 built-ins only.
// Loopback only, never port 8096; tokens are never printed.
//
//   node scripts/accessroles-admin-tabs-probe.mjs --sandbox access [--webcheck] [--out result.json]
//
// Start the sandbox first (scripts/README.md, this probe's section). Prints one JSON result (pass, checks,
// transcript, evidence) and exits with 0 when every check passed.

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
const PREFIX = 'Tabs probe ';
const PROBE_USER = 'tabsprobe-';
const KID = 'tabsprobe-kid';
const TEEN = 'tabsprobe-teen';
const PAIR_ROLE = `${PREFIX}Pair`;
const GONE_ROLE = `${PREFIX}Gone only`;
const LIB_AGAIN = `${PREFIX}Again`;
const LIB_GONE = `${PREFIX}Gone`;
const LIB_OLD = `${PREFIX}Old`;
// The renamed library's new name: 81 characters, one unbroken word after the prefix (the phone check wraps it).
const LIB_NEW = `${PREFIX}${'N'.repeat(70)}`;
const FOLDERS = { [LIB_AGAIN]: 'again', [LIB_GONE]: 'gone', [LIB_OLD]: 'old' };
const WEBCHECKS = { desktop: 'accessroles-admin-tabs-desktop.json', tv: 'accessroles-admin-tabs-tv.json', phone: 'accessroles-admin-tabs-phone.json' };

function usage() {
    return 'usage: node scripts/accessroles-admin-tabs-probe.mjs --sandbox <name> [--webcheck] [--out <file>]';
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

// The sandbox's files: inside .sandbox, never under ProgramData\Jellyfin.
function assertInSandbox(p) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), path.resolve(p));
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(path.resolve(p))) throw new Error(`refusing to touch ${p}`);
    return path.resolve(p);
}
const logFolder = assertInSandbox(path.join(sandboxRoot, 'log'));
const libraryRoot = assertInSandbox(path.join(sandboxRoot, 'tabsprobe'));

const checks = [];
const transcript = [];
const probeStarted = new Date();
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: probeStarted.toISOString() }, plugin };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// A request as the admin (credentials.json's token, never printed) or anonymous (null).
function request(method, p, user = 'admin', body = null) {
    return new Promise((resolve, reject) => {
        const token = user === 'admin' ? credentials.admin.token : null;
        const headers = { Authorization: `MediaBrowser Client="accessroles-admin-tabs-probe", Device="sandbox", DeviceId="accessroles-admin-tabs-probe", Version="1.0.0"${token ? `, Token="${token}"` : ''}`, Accept: '*/*' };
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
        if (body !== null) req.write(JSON.stringify(body));
        req.end();
    });
}

// One admin request of the walkthrough, as curl would send it (the token as <admin>, never its value).
async function step(label, method, p, body = null) {
    const r = await request(method, p, 'admin', body);
    transcript.push({ step: label, curl: `curl -X ${method} "http://127.0.0.1:${port}${baseUrl}${p}" -H "Authorization: MediaBrowser Token=<admin>"${body !== null ? ` -d '${JSON.stringify(body).slice(0, 300)}'` : ''}`, status: r.status, code: r.json && r.json.Code ? r.json.Code : undefined });
    return r;
}

const idN = (x) => (x || '').replace(/-/g, '').toLowerCase();
const stable = (value) => JSON.stringify(value, (k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.keys(v).sort().map((key) => [key, v[key]])) : v));
const sameIds = (a, b) => stable([...(a || [])].map(idN).sort()) === stable([...(b || [])].map(idN).sort());
const usersRows = async () => (await request('GET', '/AccessRoles/Admin/Users')).json || [];
const rowOf = async (user) => (await usersRows()).find((r) => idN(r.UserId) === idN(user.Id)) || {};
const rolesList = async () => (await request('GET', '/AccessRoles/Admin/Roles')).json || [];
const arLibraries = async () => (await request('GET', '/AccessRoles/Admin/Libraries')).json || [];
const settings = async () => (await request('GET', '/AccessRoles/Admin/Settings')).json || {};
const diagnostics = async () => (await request('GET', '/AccessRoles/Admin/Diagnostics')).json || {};
const policyOf = async (user) => ((await request('GET', `/Users/${user.Id}`)).json || {}).Policy || {};
const access = (policy) => ({ all: !!policy.EnableAllFolders, folders: [...(policy.EnabledFolders || [])].map(idN).sort() });
const lastSeq = async () => ((((await request('GET', '/AccessRoles/Admin/Audit?limit=1')).json || {}).Items || [])[0] || {}).Seq || 0;
async function auditSince(seq, action = null) {
    const r = await request('GET', `/AccessRoles/Admin/Audit?limit=200${action ? `&action=${action}` : ''}`);
    return ((r.json && r.json.Items) || []).filter((e) => e.Seq > seq).reverse();
}

async function waitFor(label, fn, timeoutMs = 60000) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
        const value = await fn();
        if (value) return value;
        if (Date.now() > deadline) throw new Error(`timed out waiting for ${label}`);
        await delay(500);
    }
}

// ---- the probe's own users ----------------------------------------------------------------------------------------------
const own = new Map();
async function createOwnUser(name) {
    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);
    // A random password nobody needs: the webchecks sign in as credentials.json's admin; it is never printed or saved.
    const password = crypto.randomBytes(24).toString('base64url');
    const r = await request('POST', '/Users/New', 'admin', { Name: name, Password: password });
    transcript.push({ step: `create the probe's user ${name}`, curl: `curl -X POST "http://127.0.0.1:${port}${baseUrl}/Users/New" -H "Authorization: MediaBrowser Token=<admin>" -d '{"Name":"${name}","Password":"<random>"}'`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.Id) throw new Error(`POST /Users/New ${name}: ${r.status}`);
    own.set(idN(r.json.Id), name);
    return r.json;
}

async function deleteOwnUser(id) {
    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);
    const r = await request('DELETE', `/Users/${id}`, 'admin');
    transcript.push({ step: `delete the probe's user ${own.get(idN(id))}`, status: r.status });
    if (r.status !== 204) throw new Error(`delete the probe's user ${id}: ${r.status}`);
    own.delete(idN(id));
}

// A native edit of one of the probe's users, as an admin's save on the stock page: the full policy, libraries changed.
async function setOwnLibraries(user, folders) {
    if (!own.has(idN(user.Id))) throw new Error(`refusing to write user ${user.Id}: it isn't one of the probe's own`);
    const current = await policyOf(user);
    const r = await request('POST', `/Users/${user.Id}/Policy`, 'admin', { ...current, EnableAllFolders: false, EnabledFolders: folders });
    transcript.push({ step: `set the native library access of the probe's user ${user.Name}`, status: r.status });
    if (r.status !== 204) throw new Error(`POST /Users/${user.Id}/Policy: ${r.status}`);
}

// ---- the probe's own libraries: empty folders under .sandbox/<name>/tabsprobe -------------------------------------------
const isOwnLibrary = (name) => name.startsWith(PREFIX);
async function addOwnLibrary(name) {
    if (!isOwnLibrary(name) || !FOLDERS[name]) throw new Error(`refusing to add the library ${name}`);
    const folder = assertInSandbox(path.join(libraryRoot, FOLDERS[name]));
    fs.mkdirSync(folder, { recursive: true });
    const query = `/Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=homevideos&paths=${encodeURIComponent(folder)}&refreshLibrary=true`;
    const r = await step(`add the probe's library ${name}`, 'POST', query, { LibraryOptions: { Enabled: true, EnableRealtimeMonitor: false, EnableInternetProviders: false, MetadataSavers: [], TypeOptions: [] } });
    if (r.status !== 204) throw new Error(`add ${name}: ${r.status}`);
}

async function removeOwnLibrary(name) {
    if (!isOwnLibrary(name)) throw new Error(`refusing to remove the library ${name}`);
    return step(`remove the probe's library ${name.slice(0, 40)}`, 'DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(name)}&refreshLibrary=true`);
}

async function renameOwnLibrary(name, newName) {
    if (!isOwnLibrary(name) || !isOwnLibrary(newName)) throw new Error(`refusing to rename the library ${name}`);
    return step(`rename the probe's library ${name} to ${newName.slice(0, 40)}…`, 'POST', `/Library/VirtualFolders/Name?name=${encodeURIComponent(name)}&newName=${encodeURIComponent(newName)}&refreshLibrary=true`);
}

const virtualFolders = async () => (await request('GET', '/Library/VirtualFolders')).json || [];
const libraryNamed = async (name) => (await arLibraries()).find((l) => l.Name === name) || null;

// ---- the plugin's settings, through the Settings API (a dry run, then the save with its token) -------------------------
async function putSettings(label, body) {
    const dry = await step(`${label}: the dry run`, 'PUT', '/AccessRoles/Admin/Settings?dryRun=true', body);
    if (dry.status !== 200 || !dry.json) throw new Error(`${label}: PUT Settings?dryRun=true ${dry.status}`);
    const saved = await step(`${label}: the save with its PreviewToken`, 'PUT', `/AccessRoles/Admin/Settings?ifPreview=${encodeURIComponent(dry.json.PreviewToken)}`, body);
    if (saved.status !== 200) throw new Error(`${label}: PUT Settings ${saved.status}`);
    return saved.json;
}

// ---- the webchecks ------------------------------------------------------------------------------------------------------
// Whether a webcheck's run failed in its first sign-in only (the login page's user cards, a known flake).
function signInFlake(result) {
    const steps = (result && result.steps) || [];
    const failed = steps.find((s) => !s.ok);
    return !!failed && failed.action === 'login' && steps.find((s) => s.action === 'login') === failed;
}

async function runWebcheck(file) {
    const attempts = [];
    let result = null;
    let run = null;
    for (let attempt = 1; attempt <= 5; attempt++) {
        if (attempt > 1) await delay(5000);
        run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', file], { encoding: 'utf8', timeout: 300000 });
        try { result = JSON.parse(run.stdout); } catch { result = null; }
        const failed = result && (result.steps || []).find((s) => !s.ok);
        attempts.push({ attempt, status: run.status, failedStep: failed ? failed.name : null, error: failed ? failed.error : (result ? result.error : (run.stderr || '').slice(0, 300)) });
        if (run.status === 0 && result && result.ok === true) break;
        if (!signInFlake(result)) break;
    }
    const failedRequests = ((result && result.failedRequests) || []).filter((f) => /\/AccessRoles\//i.test(f.url || ''));
    return {
        ok: !!run && run.status === 0 && !!result && result.ok === true,
        attempts,
        failedRequests,
        exceptions: ((result && result.exceptions) || []).filter((e) => !e.known).map((e) => (e.text || '').split('\n')[0]),
        screenshots: ((result && result.screenshots) || []).map((f) => path.relative(repoRoot, f)),
        steps: result ? (result.steps || []).map((s) => ({ name: s.name, ok: s.ok, value: s.value, error: s.error })) : (run && run.stderr || '').slice(0, 2000)
    };
}

// The failed requests as the Page probe reports them: no origin, a write's token as <token> (the stale save's ifPreview).
function webcheckDetail(run) {
    const failedRequests = (run.failedRequests || []).map((f) => ({ method: f.method, status: f.status, url: (f.url || '').replace(/^https?:\/\/[^/]+/, '').replace(/(ifPreview|ifPlan)=[0-9a-f]+/g, '$1=<token>') }));
    return { attempts: run.attempts, failedRequests, exceptions: run.exceptions, failed: Array.isArray(run.steps) ? run.steps.filter((s) => !s.ok) : run.steps };
}

// ---- cleanup: everything of the probe's, through the API ----------------------------------------------------------------
const cleanupErrors = [];
async function cleanup(label) {
    for (const u of (await usersRows()).filter((x) => x.State !== 'NotManaged' && x.Name.toLowerCase().startsWith(PROBE_USER))) {
        // The probe's own users only, deleted next: Keep needs no original access recorded.
        await step(`${label}: unmanage ${u.Name} (Keep)`, 'POST', `/AccessRoles/Admin/Users/${u.UserId}/Unmanage?confirmNoFullAdmin=true`, { Choice: 'Keep' });
    }
    for (const r of (await rolesList()).filter((x) => x.Name.startsWith(PREFIX))) {
        await step(`${label}: delete ${r.Name}`, 'DELETE', `/AccessRoles/Admin/Roles/${r.Id}?onUnmanage=Keep&confirmNoFullAdmin=true`);
    }
    for (const u of ((await request('GET', '/Users')).json || []).filter((x) => x.Name.toLowerCase().startsWith(PROBE_USER))) {
        own.set(idN(u.Id), u.Name);
        try {
            await deleteOwnUser(u.Id);
        } catch (e) {
            cleanupErrors.push(`${label}: ${e.message}`);
        }
    }
    let removed = false;
    for (const v of (await virtualFolders()).filter((x) => isOwnLibrary(x.Name || ''))) {
        const r = await removeOwnLibrary(v.Name);
        if (r.status !== 204) cleanupErrors.push(`${label}: remove ${v.Name}: ${r.status}`);
        removed = true;
    }
    if (removed) {
        try {
            await waitFor('the probe\'s libraries gone', async () => (await virtualFolders()).every((x) => !isOwnLibrary(x.Name || '')), 60000);
        } catch (e) {
            cleanupErrors.push(`${label}: ${e.message}`);
        }
    }
    // The deleted libraries' references the probe left (its roles are gone): purged, so Access Roles knows none of them.
    try {
        await waitFor('the probe\'s libraries Orphaned in Access Roles', async () => (await arLibraries()).every((x) => !isOwnLibrary(x.Name || '') || x.State === 'Orphaned'), 60000);
    } catch (e) {
        cleanupErrors.push(`${label}: ${e.message}`);
    }
    for (const l of (await arLibraries()).filter((x) => isOwnLibrary(x.Name || '') && x.State === 'Orphaned')) {
        const r = await step(`${label}: purge ${l.Name.slice(0, 40)}`, 'POST', `/AccessRoles/Admin/Libraries/${l.Id}/Purge`, {});
        if (r.status !== 200) cleanupErrors.push(`${label}: purge ${l.Name}: ${r.status}`);
    }
    if (fs.existsSync(libraryRoot)) fs.rmSync(assertInSandbox(libraryRoot), { recursive: true, force: true });
}

function logSince(since) {
    const lines = [];
    for (const file of fs.readdirSync(logFolder).filter((f) => /^log_\d{8}\.log$/.test(f)).sort()) {
        for (const line of fs.readFileSync(path.join(logFolder, file), 'utf8').split(/\r?\n/)) {
            const m = /^\[(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2})\] \[(\w{3})\]/.exec(line);
            if (m && Date.parse(`${m[1]}T${m[2]}${m[3]}`) >= since - 1000) lines.push({ level: m[4], text: line });
        }
    }
    return lines;
}

let others = [];
const othersBefore = {};
let defaultRolesBefore = null;
let settingsBefore = null;
let seed = [];

async function main() {
    const info = await request('GET', '/System/Info/Public', null);
    check('the sandbox answers, Jellyfin 10.11.5', info.status === 200 && info.json && info.json.Version === '10.11.5', { status: info.status, version: info.json && info.json.Version });
    const installed = (await request('GET', '/Plugins')).json || [];
    const ours = installed.find((p) => idN(p.Id) === idN(plugin.guid));
    check('Access Roles is loaded and Active, at the build.yaml version', !!ours && ours.Version === plugin.version && ours.Status === 'Active', ours ? { version: ours.Version, status: ours.Status } : 'not listed');
    await cleanup('earlier run');

    defaultRolesBefore = (await rolesList()).filter((r) => r.IsDefault).map((r) => r.Name).sort();
    if (!check('no default role for new users (the probe\'s users must start unmanaged)', defaultRolesBefore.length === 0, defaultRolesBefore)) return;
    others = ((await request('GET', '/Users')).json || []).filter((u) => !u.Name.toLowerCase().startsWith(PROBE_USER));
    for (const u of others) othersBefore[u.Id] = stable(u.Policy);
    evidence.managedBefore = (await usersRows()).filter((x) => x.State !== 'NotManaged').map((x) => x.Name);
    settingsBefore = await settings();
    evidence.settingsBefore = { DriftPolicy: settingsBefore.DriftPolicy, ReconcileIntervalMinutes: settingsBefore.ReconcileIntervalMinutes, EnforcementEnabled: settingsBefore.EnforcementEnabled, LibrarySyncEnabled: settingsBefore.LibrarySyncEnabled };
    if (!check('the settings to start from: enforcement and the library sync on, DriftPolicy Reapply', settingsBefore.EnforcementEnabled === true && settingsBefore.LibrarySyncEnabled === true && settingsBefore.DriftPolicy === 'Reapply', evidence.settingsBefore)) return;

    // ---------------------------------------------------------------------------------------------------------------
    // 1. The Settings API.
    const seqAtStart = await lastSeq();
    const minutes = settingsBefore.ReconcileIntervalMinutes;
    const other = (n) => (n % 60) + 1;
    const dry1 = await step('PUT Settings?dryRun=true: DriftPolicy ReportOnly', 'PUT', '/AccessRoles/Admin/Settings?dryRun=true', { DriftPolicy: 'ReportOnly' });
    const afterDry = await settings();
    evidence.settingsDryRun = dry1.json && { DryRun: dry1.json.DryRun, Changes: dry1.json.Changes, Warnings: dry1.json.Warnings, token: !!dry1.json.PreviewToken };
    check('the dry run shows the change (DriftPolicy: Reapply → ReportOnly) and a PreviewToken, and saves nothing',
        dry1.status === 200 && dry1.json.DryRun === true && stable(dry1.json.Changes) === stable([{ Name: 'DriftPolicy', Before: 'Reapply', After: 'ReportOnly' }]) && !!dry1.json.PreviewToken
            && afterDry.DriftPolicy === 'Reapply',
        evidence.settingsDryRun);
    const m1 = other(minutes);
    const otherWrite = await step('another administrator: ReconcileIntervalMinutes changed', 'PUT', '/AccessRoles/Admin/Settings', { ReconcileIntervalMinutes: m1 });
    const save1 = await step('PUT Settings with the dry run\'s PreviewToken', 'PUT', `/AccessRoles/Admin/Settings?ifPreview=${encodeURIComponent(dry1.json.PreviewToken)}`, { DriftPolicy: 'ReportOnly' });
    const afterSave1 = await settings();
    check('another setting changed meanwhile doesn\'t refuse the save, and stays: the body carries only what changes',
        otherWrite.status === 200 && save1.status === 200 && save1.json.DryRun === false && afterSave1.DriftPolicy === 'ReportOnly' && afterSave1.ReconcileIntervalMinutes === m1,
        { other: otherWrite.status, save: save1.status, after: { DriftPolicy: afterSave1.DriftPolicy, ReconcileIntervalMinutes: afterSave1.ReconcileIntervalMinutes } });

    const m2 = other(m1);
    const m3 = other(m2);
    const dry2 = await step('PUT Settings?dryRun=true: ReconcileIntervalMinutes', 'PUT', '/AccessRoles/Admin/Settings?dryRun=true', { ReconcileIntervalMinutes: m2 });
    const race = await step('another administrator: the same setting changed', 'PUT', '/AccessRoles/Admin/Settings', { ReconcileIntervalMinutes: m3 });
    const stale = await step('PUT Settings with the stale PreviewToken', 'PUT', `/AccessRoles/Admin/Settings?ifPreview=${encodeURIComponent(dry2.json.PreviewToken)}`, { ReconcileIntervalMinutes: m2 });
    const afterStale = await settings();
    evidence.settingsRace = { status: stale.status, code: stale.json && stale.json.Code, detail: stale.json && stale.json.detail, after: afterStale.ReconcileIntervalMinutes };
    check('a save whose token another administrator\'s change of the same setting made stale is a 409 Changed naming it, nothing saved',
        dry2.status === 200 && race.status === 200 && stale.status === 409 && stale.json && stale.json.Code === 'Changed' && /ReconcileIntervalMinutes/.test(stale.json.detail || '') && afterStale.ReconcileIntervalMinutes === m3,
        evidence.settingsRace);
    const control = await step('the negative control: the same save without ifPreview', 'PUT', '/AccessRoles/Admin/Settings', { ReconcileIntervalMinutes: m2 });
    check('the negative control: the same save without the token is saved', control.status === 200 && (await settings()).ReconcileIntervalMinutes === m2, { status: control.status });

    const list = settingsBefore.ConfigurationReadAllowList || [];
    if (!list.includes('network')) {
        const withSecret = { ConfigurationReadAllowList: list.concat(['network']) };
        const dry3 = await step('PUT Settings?dryRun=true: network added to the allow-list', 'PUT', '/AccessRoles/Admin/Settings?dryRun=true', withSecret);
        const refused = await step('PUT Settings with its token, without confirmSecretKeys', 'PUT', `/AccessRoles/Admin/Settings?ifPreview=${encodeURIComponent(dry3.json.PreviewToken)}`, withSecret);
        const afterRefused = await settings();
        check('adding a key that carries secrets: the dry run names it, and the save without confirmSecretKeys is a 400 SecretKeys, nothing saved',
            dry3.status === 200 && stable(dry3.json.SecretKeysAdded) === stable(['network']) && refused.status === 400 && refused.json && refused.json.Code === 'SecretKeys'
                && stable(afterRefused.ConfigurationReadAllowList) === stable(list),
            { dry: dry3.json && dry3.json.SecretKeysAdded, status: refused.status, code: refused.json && refused.json.Code });
    }
    const changedLines = await auditSince(seqAtStart, 'SettingsChanged');
    evidence.settingsAudit = changedLines.map((e) => ({ actor: e.Actor && e.Actor.Kind, changes: (e.Details.Changes || []).map((c) => `${c.Name}: ${c.Before} → ${c.After}`) }));
    check('each save is one SettingsChanged line by the admin, with its changes (four saves, the refused ones none)',
        changedLines.length === 4 && changedLines.every((e) => e.Actor && e.Actor.Kind === 'Admin')
            && stable(evidence.settingsAudit.map((e) => e.changes[0])) === stable([`ReconcileIntervalMinutes: ${minutes} → ${m1}`, 'DriftPolicy: Reapply → ReportOnly', `ReconcileIntervalMinutes: ${m1} → ${m3}`, `ReconcileIntervalMinutes: ${m3} → ${m2}`]),
        evidence.settingsAudit);

    // ---------------------------------------------------------------------------------------------------------------
    // 2. The probe's libraries, roles and users.
    for (const name of [LIB_AGAIN, LIB_GONE, LIB_OLD]) await addOwnLibrary(name);
    const known = await waitFor('Access Roles knows the probe\'s three libraries', async () => {
        const all = await arLibraries();
        const mine = [LIB_AGAIN, LIB_GONE, LIB_OLD].map((n) => all.find((l) => l.Name === n && l.State === 'Active' && l.Known));
        return mine.every(Boolean) ? mine : null;
    });
    const [again, gone, old] = known;
    const pairR = await step('the role Pair: Only the three', 'POST', '/AccessRoles/Admin/Roles', { Name: PAIR_ROLE, Libraries: { Mode: 'Only', Refs: [{ Id: again.Id }, { Id: gone.Id }, { Id: old.Id }] } });
    const goneR = await step('the role Gone only', 'POST', '/AccessRoles/Admin/Roles', { Name: GONE_ROLE, Libraries: { Mode: 'Only', Refs: [{ Id: gone.Id }] } });
    if (pairR.status !== 200 || goneR.status !== 200) throw new Error(`the roles: ${pairR.status} ${goneR.status}`);
    const pair = pairR.json.Role;
    const goneOnly = goneR.json.Role;
    const kid = await createOwnUser(KID);
    const teen = await createOwnUser(TEEN);
    const assigned = [await step('the kid gets Pair', 'PUT', `/AccessRoles/Admin/Users/${kid.Id}`, { RoleIds: [pair.Id] }), await step('the teen gets Gone only', 'PUT', `/AccessRoles/Admin/Users/${teen.Id}`, { RoleIds: [goneOnly.Id] })];
    if (!assigned.every((r) => r.status === 200)) throw new Error(`the assignments: ${assigned.map((r) => r.status)}`);
    await waitFor('the library sync writes both users', async () => stable(access(await policyOf(kid))) === stable({ all: false, folders: [again.Id, gone.Id, old.Id].map(idN).sort() })
        && stable(access(await policyOf(teen))) === stable({ all: false, folders: [idN(gone.Id)] }));
    const opened = await libraryNamed(LIB_GONE);
    const sourcesOf = (library, user) => ((library.Users || []).find((u) => idN(u.UserId) === idN(user.Id)) || {}).Sources || null;
    evidence.whoOpens = { users: (opened.Users || []).map((u) => ({ name: u.Name, sources: u.Sources })), roles: (opened.RoleIds || []).length };
    check('GET Libraries says who opens each library and why: Gone by the kid (Pair) and the teen (Gone only), in two roles',
        sourcesOf(opened, kid) && sourcesOf(opened, kid).some((s) => s.includes(PAIR_ROLE)) && sourcesOf(opened, teen) && sourcesOf(opened, teen).some((s) => s.includes(GONE_ROLE))
            && sameIds(opened.RoleIds, [pair.Id, goneOnly.Id]),
        evidence.whoOpens);

    // A rename: re-pointed, one LibraryRenamed line naming the new id.
    const seqBeforeLibraries = await lastSeq();
    await renameOwnLibrary(LIB_OLD, LIB_NEW);
    const renamed = await waitFor('the rename is re-pointed', async () => {
        const all = await arLibraries();
        const now = all.find((l) => l.Name === LIB_NEW && l.State === 'Active' && l.Known);
        return now && !all.some((l) => l.Name === LIB_OLD) && (now.RoleIds || []).some((id) => idN(id) === idN(pair.Id)) ? now : null;
    });
    const renameLines = await waitFor('the LibraryRenamed line', async () => {
        const lines = (await auditSince(seqBeforeLibraries, 'LibraryRenamed')).filter((e) => e.Details && idN(e.Details.LibraryId) === idN(renamed.Id));
        return lines.length ? lines : null;
    }, 20000);
    evidence.rename = { newId: idN(renamed.Id), oldId: idN(old.Id), line: renameLines[0].Details };
    check('a rename re-points the roles to the library\'s new id, and the LibraryRenamed line names it with its old id and name',
        renameLines.length === 1 && renameLines[0].Details.OldName === LIB_OLD && renameLines[0].Details.Name === LIB_NEW && idN(renameLines[0].Details.OldId) === idN(old.Id),
        evidence.rename);

    // A deletion: Orphaned, the references kept; Gone only grants nothing, the teen has no library.
    await removeOwnLibrary(LIB_GONE);
    const orphan = await waitFor('the deleted library is Orphaned', async () => {
        const l = await libraryNamed(LIB_GONE);
        return l && l.State === 'Orphaned' ? l : null;
    });
    await waitFor('the teen is written with no library', async () => stable(access(await policyOf(teen))) === stable({ all: false, folders: [] }));
    check('a deleted library is Orphaned with the roles\' references kept; the role whose only library it was grants nothing, so the teen has none',
        sameIds(orphan.RoleIds, [pair.Id, goneOnly.Id]),
        { state: orphan.State, roles: (orphan.RoleIds || []).length, teen: access(await policyOf(teen)) });

    // Deleted and created again under the same id: Recreated, given to nobody.
    await removeOwnLibrary(LIB_AGAIN);
    await waitFor('Again is Orphaned', async () => {
        const l = await libraryNamed(LIB_AGAIN);
        return l && l.State === 'Orphaned';
    });
    await addOwnLibrary(LIB_AGAIN);
    const recreated = await waitFor('Again is Recreated', async () => {
        const l = await libraryNamed(LIB_AGAIN);
        return l && l.State === 'Active' && l.Recreated ? l : null;
    });
    await waitFor('the kid is written with the renamed library only', async () => stable(access(await policyOf(kid))) === stable({ all: false, folders: [idN(renamed.Id)] }));
    check('a library created again under the same id is Recreated and none of the probe\'s users opens it (the users who see every library do); the kid keeps only the renamed library',
        idN(recreated.Id) === idN(again.Id) && !(recreated.Users || []).some((u) => own.has(idN(u.UserId))),
        { id: idN(recreated.Id), users: (recreated.Users || []).map((u) => u.Name) });

    // ---------------------------------------------------------------------------------------------------------------
    // 3. The drift's per-user scope, under ReportOnly (saved in section 1).
    seed = (await virtualFolders()).filter((v) => !isOwnLibrary(v.Name || '') && v.ItemId).map((v) => v.ItemId);
    if (seed.length < 2) throw new Error('the sandbox needs two libraries of its own');
    await setOwnLibraries(kid, [seed[0]]);
    await setOwnLibraries(teen, [seed[0]]);
    const reported = await step('POST Sync: the drift reported', 'POST', '/AccessRoles/Admin/Sync');
    const drifted = await waitFor('the kid\'s drift reported', async () => {
        const rows = await usersRows();
        const of = (u) => rows.find((r) => idN(r.UserId) === idN(u.Id)) || {};
        return of(kid).Drift === true ? { kid: of(kid), teen: of(teen) } : null;
    }, 20000).catch(() => null);
    evidence.drift = drifted && { kid: drifted.kid.Drift, teen: { Drift: drifted.teen.Drift, NoLibraries: drifted.teen.NoLibraries, NoLibrariesConfirmed: drifted.teen.NoLibrariesConfirmed } };
    check('under DriftPolicy ReportOnly the kid\'s native change is reported as drift and left; the teen\'s isn\'t drift (the roles would leave it no library, nobody chose that, so its access is left as it is)',
        reported.status === 200 && !!drifted && drifted.teen.Drift === false && drifted.teen.NoLibraries === true && drifted.teen.NoLibrariesConfirmed === false
            && sameIds(access(await policyOf(kid)).folders, [seed[0]]) && sameIds(access(await policyOf(teen)).folders, [seed[0]]),
        evidence.drift);
    const seedName = ((await virtualFolders()).find((v) => idN(v.ItemId) === idN(seed[1])) || {}).Name;
    const seedLibrary = (await arLibraries()).find((l) => idN(l.Id) === idN(seed[1]) || l.Name === seedName);
    if (!seedLibrary) throw new Error('the second sandbox library isn\'t one Access Roles knows');
    const dryAll = await step('POST Sync?reapplyDrift=true&dryRun=true (everyone)', 'POST', '/AccessRoles/Admin/Sync?reapplyDrift=true&dryRun=true');
    const dryUser = await step('POST Sync?userId=kid&reapplyDrift=true&dryRun=true', 'POST', `/AccessRoles/Admin/Sync?userId=${idN(kid.Id)}&reapplyDrift=true&dryRun=true`);
    const rowsOf = (dry) => ((dry.json && dry.json.Users) || []).map((u) => ({ name: u.Name, action: u.Action, reason: u.Reason }));
    evidence.perUserDryRun = { everyone: rowsOf(dryAll), user: dryUser.json && { userId: dryUser.json.UserId, users: rowsOf(dryUser), token: !!dryUser.json.PlanToken } };
    check('the per-user dry run lists the kid alone, with a PlanToken of its own; the run for everyone lists the teen too (a skip: no library would be left)',
        dryAll.status === 200 && rowsOf(dryAll).some((u) => u.name === TEEN && u.action === 'Skip' && u.reason === 'NoLibraries') && rowsOf(dryAll).some((u) => u.name === KID)
            && dryUser.status === 200 && dryUser.json.DryRun === true && idN(dryUser.json.UserId) === idN(kid.Id) && (dryUser.json.Users || []).length === 1 && idN(dryUser.json.Users[0].UserId) === idN(kid.Id)
            && !!dryUser.json.PlanToken && dryUser.json.PlanToken !== dryAll.json.PlanToken,
        evidence.perUserDryRun);
    // A userId of no value (a Revert whose id was lost) is a 400, never a run for everyone.
    const emptyUser = await step('POST Sync?userId= (no value)&reapplyDrift=true', 'POST', '/AccessRoles/Admin/Sync?userId=&reapplyDrift=true');
    check('a userId of no value is a 400, never a run for everyone: nothing written',
        emptyUser.status === 400 && sameIds(access(await policyOf(kid)).folders, [seed[0]]) && sameIds(access(await policyOf(teen)).folders, [seed[0]]),
        { status: emptyUser.status });
    // Another administrator changes the kid's own library access after the dry run: the plan
    // named the access it writes over, so the confirmed write is refused; put back, the same token is good again (below).
    await setOwnLibraries(kid, [seed[0], seed[1]]);
    const nativeRace = await step('POST Sync?userId=kid with the PlanToken, the kid\'s own access changed since', 'POST', `/AccessRoles/Admin/Sync?userId=${idN(kid.Id)}&reapplyDrift=true&ifPlan=${encodeURIComponent(dryUser.json.PlanToken)}`);
    evidence.perUserNativeRace = { status: nativeRace.status, code: nativeRace.json && nativeRace.json.Code, users: ((nativeRace.json && nativeRace.json.Users) || []).map((u) => u.Name || u), kid: access(await policyOf(kid)).folders.length };
    check('another administrator\'s change of the kid\'s own library access since the dry run makes the confirmed write a 409 Changed naming only the kid; nothing written',
        nativeRace.status === 409 && nativeRace.json.Code === 'Changed' && stable(evidence.perUserNativeRace.users) === stable([KID]) && sameIds(access(await policyOf(kid)).folders, [seed[0], seed[1]]),
        evidence.perUserNativeRace);
    await setOwnLibraries(kid, [seed[0]]);
    // Another administrator adds a library for the kid, so the run would now write the kid other access: the plan the dry
    // run showed is stale too (the PlanToken covers what the run writes and the access it writes over).
    const raced = await step('another administrator adds a library for the kid', 'PUT', `/AccessRoles/Admin/Users/${kid.Id}`, { RoleIds: [pair.Id], Libraries: { Mode: 'Inherit', Add: [seedLibrary.Id] }, IfUpdatedAt: (await rowOf(kid)).UpdatedUtc || null });
    const staleUser = await step('POST Sync?userId=kid with the stale PlanToken', 'POST', `/AccessRoles/Admin/Sync?userId=${idN(kid.Id)}&reapplyDrift=true&ifPlan=${encodeURIComponent(dryUser.json.PlanToken)}`);
    evidence.perUserRace = { assignment: raced.status, status: staleUser.status, code: staleUser.json && staleUser.json.Code, users: ((staleUser.json && staleUser.json.Users) || []).map((u) => u.Name || u) };
    check('another administrator\'s change of what the run writes for the kid makes the write a 409 Changed naming only the kid; nothing written (both drifts left)',
        raced.status === 200 && staleUser.status === 409 && staleUser.json.Code === 'Changed' && stable(evidence.perUserRace.users) === stable([KID])
            && sameIds(access(await policyOf(teen)).folders, [seed[0]]) && sameIds(access(await policyOf(kid)).folders, [seed[0]]),
        evidence.perUserRace);
    const controlUser = await step('the negative control: POST Sync?userId=kid without ifPlan', 'POST', `/AccessRoles/Admin/Sync?userId=${idN(kid.Id)}&reapplyDrift=true`);
    const teenRow = await rowOf(teen);
    check('the negative control: the same write without the token writes the kid alone (the renamed library and the one added), the teen left (not drift)',
        controlUser.status === 200 && controlUser.json.Written === 1 && sameIds(access(await policyOf(kid)).folders, [renamed.Id, seedLibrary.Id]) && teenRow.Drift === false && sameIds(access(await policyOf(teen)).folders, [seed[0]]),
        { status: controlUser.status, written: controlUser.json && controlUser.json.Written, kid: access(await policyOf(kid)), teenDrift: teenRow.Drift });
    // The kid back to Pair alone: the library sync writes them the renamed library.
    const back = await step('the kid back to Pair alone', 'PUT', `/AccessRoles/Admin/Users/${kid.Id}`, { RoleIds: [pair.Id], Libraries: { Mode: 'Inherit', Add: [] }, IfUpdatedAt: (await rowOf(kid)).UpdatedUtc || null });
    if (back.status !== 200) throw new Error(`the kid back to Pair alone: ${back.status} ${back.text.slice(0, 400)}`);
    await waitFor('the kid is written with the renamed library again', async () => sameIds(access(await policyOf(kid)).folders, [renamed.Id]));
    // The kid's access changed natively again, for the page's Revert this user; a run for everyone counts the drift.
    await setOwnLibraries(kid, [seed[0]]);
    await step('POST Sync: the kid\'s drift reported again', 'POST', '/AccessRoles/Admin/Sync');
    const driftAgain = await waitFor('the kid\'s drift counted', async () => {
        const sync = (await diagnostics()).LibrarySync || {};
        return (await rowOf(kid)).Drift === true && sync.DriftCount === 1 ? sync : null;
    }, 20000).catch(() => null);
    check('the kid drifted again and Diagnostics counts one drifted user (the teen, with no library left, is a skip, not drift)', !!driftAgain, driftAgain && { driftCount: driftAgain.DriftCount });

    // ---------------------------------------------------------------------------------------------------------------
    // 4. The web client.
    if (opts.webcheck) {
        const before = stable({ kid: await rowOf(kid), teen: await rowOf(teen), settings: await settings() });
        for (const [name, file, text] of [
            ['phone', WEBCHECKS.phone, 'a 375 px touch phone: Libraries, Audit, Settings, Data and Diagnostics with nothing past the right edge and no sideways scroll, every control 44 px tall, the 81-character library name wrapping inside the screen'],
            ['tv', WEBCHECKS.tv, 'the TV layout: the page opens on the remembered Libraries tab; every tab, library card and audit line in the D-pad\'s reach; OK on Review opens the settings review with the focus inside, Backspace closes it with the focus back on Review']
        ]) {
            const run = await runWebcheck(file);
            evidence[name] = run;
            check(`the web client as admin on ${text}; no uncaught exception, no failed request of Access Roles'`, run.ok && run.failedRequests.length === 0, webcheckDetail(run));
        }
        const after = stable({ kid: await rowOf(kid), teen: await rowOf(teen), settings: await settings() });
        check('the phone and TV checks wrote nothing (both users\' rows and the settings as before)', before === after, null);

        const seqBeforePage = await lastSeq();
        const minutesBeforePage = (await settings()).ReconcileIntervalMinutes;
        const run = await runWebcheck(WEBCHECKS.desktop);
        evidence.desktop = run;
        check('the web client as admin at 1366 px: the Overview\'s chips and server-wide box; the Libraries tab\'s renamed, deleted and created-again cards, Grant again and Remove references each previewed and saved; the kid\'s Revert this user previewed for the kid alone and written; the Audit tab\'s lines, its action filter and Download; the Settings tab\'s change, a 409 after another administrator\'s change of the same setting, Look again and Save; the Data and Diagnostics tabs; no uncaught exception, and of Access Roles\' requests only the stale save failed (409)',
            run.ok && run.failedRequests.length === 1 && /\/AccessRoles\/Admin\/Settings\?ifPreview=/.test(run.failedRequests[0].url) && run.failedRequests[0].status === 409 && run.failedRequests[0].method === 'PUT', webcheckDetail(run));

        const libraries = await arLibraries();
        const againNow = libraries.find((l) => idN(l.Id) === idN(again.Id));
        const kidAccess = await waitFor('the kid is written with the roles\' libraries', async () => {
            const a = access(await policyOf(kid));
            return sameIds(a.folders, [again.Id, renamed.Id]) && !a.all ? a : null;
        }, 20000).catch(() => access(null || {}));
        const pageLines = await auditSince(seqBeforePage);
        const by = (action) => pageLines.filter((e) => e.Action === action && e.Actor && e.Actor.Kind === 'Admin');
        const pairNow = (await rolesList()).find((r) => idN(r.Id) === idN(pair.Id)) || {};
        const goneNow = (await rolesList()).find((r) => idN(r.Id) === idN(goneOnly.Id)) || {};
        const refs = (role) => (((role.Libraries || {}).Refs) || []).map((r) => idN(r.Id));
        const pageSettings = by('SettingsChanged').map((e) => (e.Details.Changes || []).map((c) => `${c.Name}: ${c.Before} → ${c.After}`).join('; '));
        evidence.afterPage = {
            again: againNow && { recreated: againNow.Recreated, users: (againNow.Users || []).map((u) => u.Name) },
            goneListed: libraries.some((l) => idN(l.Id) === idN(gone.Id)),
            pairRefs: refs(pairNow).length,
            goneOnlyRefs: refs(goneNow).length,
            kid: kidAccess,
            kidDrift: (await rowOf(kid)).Drift,
            teenDrift: (await rowOf(teen)).Drift,
            teenNoLibraries: (await rowOf(teen)).NoLibraries,
            teen: access(await policyOf(teen)),
            lines: { grantedAgain: by('LibraryGrantedAgain').length, purged: by('LibraryPurged').length, policySynced: pageLines.filter((e) => e.Action === 'PolicySynced' && idN(e.TargetUserId) === idN(kid.Id)).length, settings: pageSettings }
        };
        check('after the page: the library given again is no longer Recreated and the kid opens it; the deleted one\'s references are gone (Pair keeps two, Gone only none) and it isn\'t listed; the kid\'s access is the roles\' (the library given again and the renamed one), the drift reverted by the page; the teen left as it was (no library by the rules, nobody chose that: its own access kept, not drift)',
            !!againNow && againNow.Recreated === false && (againNow.Users || []).some((u) => idN(u.UserId) === idN(kid.Id)) && !evidence.afterPage.goneListed
                && stable(refs(pairNow).sort()) === stable([again.Id, renamed.Id].map(idN).sort()) && refs(goneNow).length === 0
                && sameIds(kidAccess.folders, [again.Id, renamed.Id]) && evidence.afterPage.kidDrift === false && evidence.afterPage.teenDrift === false && evidence.afterPage.teenNoLibraries === true && sameIds(evidence.afterPage.teen.folders, [seed[0]]),
            evidence.afterPage);
        check('the audit lines of the page\'s writes by the admin: LibraryGrantedAgain, LibraryPurged, and two SettingsChanged (another administrator\'s, then the page\'s save after Look again)',
            evidence.afterPage.lines.grantedAgain === 1 && evidence.afterPage.lines.purged === 1
                && stable(pageSettings) === stable([`ReconcileIntervalMinutes: ${minutesBeforePage} → ${other(other(minutesBeforePage))}`, `ReconcileIntervalMinutes: ${other(other(minutesBeforePage))} → ${other(minutesBeforePage)}`]),
            evidence.afterPage.lines);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 5. A settings save applies at once: saving DriftPolicy Reapply wakes the library sync,
    // whose run (trigger SettingsChanged, 2 s after the save) writes the drifted kid back; the teen's own access is left.
    if ((await rowOf(kid)).Drift !== true) {
        await setOwnLibraries(kid, [seed[0]]);
        await step('POST Sync: the kid\'s drift reported', 'POST', '/AccessRoles/Admin/Sync');
        await waitFor('the kid drifted', async () => (await rowOf(kid)).Drift === true, 20000);
    }
    // What the roles give the kid now, as Effective resolves it (the library given again and the renamed one after the
    // page; the renamed one alone without --webcheck).
    const kidEffective = await step('GET Users/{kid}/Effective', 'GET', `/AccessRoles/Admin/Users/${kid.Id}/Effective`);
    const kidRoles = ((kidEffective.json && kidEffective.json.Libraries && kidEffective.json.Libraries.Visible) || []).map((l) => l.Id);
    const seqBeforeWake = await lastSeq();
    const savedAt = Date.now();
    await putSettings('DriftPolicy Reapply', { DriftPolicy: 'Reapply' });
    const woke = await waitFor('the settings save wakes the library sync', async () => {
        const lines = (await auditSince(seqBeforeWake, 'PolicySynced')).filter((e) => idN(e.TargetUserId) === idN(kid.Id) && e.Details && e.Details.Trigger === 'SettingsChanged');
        return lines.length && sameIds(access(await policyOf(kid)).folders, kidRoles) ? lines : null;
    }, 15000).catch(() => null);
    evidence.settingsWake = { lines: woke ? woke.length : 0, secs: Math.round((Date.now() - savedAt) / 1000), roles: kidRoles.length, kid: access(await policyOf(kid)).folders.length, teen: access(await policyOf(teen)).folders.length };
    check('saving DriftPolicy Reapply applies at once: within 15 s a run of trigger SettingsChanged writes the kid back to the roles\' libraries (one PolicySynced line); the teen\'s own access is left',
        kidRoles.length > 0 && !!woke && woke.length === 1 && sameIds(access(await policyOf(teen)).folders, [seed[0]]),
        evidence.settingsWake);
}

let failure = null;
try {
    await main();
} catch (e) {
    failure = e.stack || String(e);
} finally {
    const cleanupResult = {};
    try {
        await cleanup('cleanup');
        if (settingsBefore) {
            const now = await settings();
            const back = {};
            for (const key of ['DriftPolicy', 'ReconcileIntervalMinutes', 'ConfigurationReadAllowList']) {
                if (stable(now[key]) !== stable(settingsBefore[key])) back[key] = settingsBefore[key];
            }
            if (Object.keys(back).length) await putSettings('cleanup: the settings put back', back);
            cleanupResult.settings = Object.keys(back);
            cleanupResult.settingsAsBefore = stable(await settings()) === stable(settingsBefore);
        }
        cleanupResult.probeLeft = {
            roles: (await rolesList()).filter((r) => r.Name.startsWith(PREFIX)).length,
            users: ((await request('GET', '/Users')).json || []).filter((x) => x.Name.toLowerCase().startsWith(PROBE_USER)).length,
            libraries: (await virtualFolders()).filter((v) => isOwnLibrary(v.Name || '')).length,
            known: (await arLibraries()).filter((l) => isOwnLibrary(l.Name || '')).length,
            folder: fs.existsSync(libraryRoot)
        };
        cleanupResult.managed = (await usersRows()).filter((x) => x.State !== 'NotManaged').map((x) => x.Name);
        cleanupResult.defaultRoles = (await rolesList()).filter((r) => r.IsDefault).map((r) => r.Name).sort();
        const changed = [];
        for (const o of others) {
            const now = (await request('GET', `/Users/${o.Id}`)).json;
            if (!now || stable(now.Policy) !== othersBefore[o.Id]) changed.push(o.Name);
        }
        cleanupResult.othersChanged = changed;
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + (e.stack || String(e));
    }
    evidence.cleanup = cleanupResult;
    if (cleanupErrors.length) failure = (failure ? failure + ' | ' : '') + 'cleanup: ' + cleanupErrors.join('; ');
    if (!failure && defaultRolesBefore) {
        const left = cleanupResult.probeLeft || {};
        check('afterwards nothing of the probe\'s is left (its libraries and their folder, roles and users), the settings, the managed users and the default roles are as they were, and no policy of a user the probe didn\'t create was written',
            left.roles === 0 && left.users === 0 && left.libraries === 0 && left.known === 0 && left.folder === false && cleanupResult.settingsAsBefore === true
                && stable(cleanupResult.managed) === stable(evidence.managedBefore) && stable(cleanupResult.defaultRoles) === stable(defaultRolesBefore) && cleanupResult.othersChanged.length === 0,
            cleanupResult);
        const log = logSince(probeStarted.getTime()).filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text));
        const errors = log.filter((l) => l.level === 'ERR' || l.level === 'FTL');
        evidence.log = { lines: log.length, warnings: log.filter((l) => l.level === 'WRN').map((l) => l.text.slice(0, 240)), errors: errors.length };
        check('the log has no error of Access Roles\' since the probe started', errors.length === 0, errors.map((l) => l.text.slice(0, 240)));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
