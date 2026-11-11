#!/usr/bin/env node
// The kit's users and data services in a running sandbox, through Bookmarks' developer user data demo
// (/Bookmarks/Admin/UserDataDemo, behind the setting EnableUserDataDemo, which this probe turns on and restores). These
// are the kit's user checks with a demo consumer (a deleted user gets the policy; a restored data folder deletes nothing
// and Remap by name restores ownership) and its JSON-store steps (export, import dry run, Merge, Replace, snapshots rotate,
// Restore snapshot in the running server, the manual procedure), plus snapshots taken before a store started empty
// surviving a restart, and the later fixes (a deleted user's records never go to a new user of the same name; export while the store is read-only; orphan
// marks kept while the store is left out; restoring a later snapshot keeps the earlier ones protected).
// Node 22 built-ins only. Loopback only, never port 8096. Tokens come from credentials.json and are never printed; the
// two throwaway users it creates get random passwords that are never stored or printed.
//
//   node scripts/users-probe.mjs --sandbox default [--out result.json] [--skip-webcheck]
//
// It stops and starts the sandbox several times, only through the repository's Stop-Sandbox.ps1 and dev-server.ps1
// (-Background -NoBuild, so deploy a current build first), and between those it changes only the demo's own files in the
// sandbox's Bookmarks data folder (userdata-demo.json*, snapshots of userdata-demo, kit-users.json). It prints one JSON
// result (pass, checks, evidence) and exits with 0 when every check passed; the sandbox is left running.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const DEMO = '/Bookmarks/Admin/UserDataDemo';
const STORE = 'userdata-demo';

function usage() {
    return 'usage: node scripts/users-probe.mjs --sandbox <name> [--out <file>] [--skip-webcheck]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, webcheck: true };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--skip-webcheck') o.webcheck = false;
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const workRoot = path.join(sandboxRoot, 'users-probe');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
let state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer', 'viewer2']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}
const bookmarksGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8')) || [])[1];
if (!bookmarksGuid) throw new Error('no guid in plugins/Bookmarks/build.yaml');

// The Bookmarks data folder of the sandbox (Jellyfin's DataPath is <datadir>/data); the only folder this probe writes in.
const dataFolder = path.join(path.resolve(state.DataDir || path.join(sandboxRoot, 'data')), 'data', 'bookmarks');
const snapshotFolder = path.join(dataFolder, 'snapshots');
const preOpFolder = path.join(snapshotFolder, 'pre-op');
const storeFile = path.join(dataFolder, STORE + '.json');
const marksFile = path.join(dataFolder, 'kit-users.json');
for (const p of [dataFolder, workRoot]) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), p);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(p)) throw new Error(`refusing to work in ${p}`);
}

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, restarts: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const ids = { admin: guid(users.admin.id), viewer: guid(users.viewer.id), viewer2: guid(users.viewer2.id) };

async function call(who, method, p, { body, query, raw } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="users-probe", Device="sandbox", DeviceId="users-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (raw !== undefined) { payload = raw; h['Content-Type'] = 'application/json'; }
    else if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const bytes = Buffer.from(await res.arrayBuffer());
    let json = null;
    if (bytes.length && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(bytes.toString('utf8')); } catch { json = null; }
    }
    return { status: res.status, type: res.headers.get('content-type') || '', headers: res.headers, json, bytes };
}

async function demo(method, p, options) {
    const r = await call('admin', method, DEMO + p, options);
    if (r.status !== 200 && r.status !== 204) throw new Error(`${method} ${p}: ${r.status} ${JSON.stringify(r.json)}`);
    return r.json;
}

const problem = (r, status) => r.status === status && /application\/problem\+json/.test(r.type) && r.json && typeof r.json.detail === 'string';

// ------------------------------------------------------------------------------------------ sandbox control

// The output goes to a file, not a pipe: the server dev-server.ps1 starts in the background inherits the script's handles,
// and a pipe would stay open (and spawnSync wait) until that server exits.
let scriptRuns = 0;

function runScript(script, args) {
    const started = Date.now();
    fs.mkdirSync(workRoot, { recursive: true });
    const logFile = path.join(workRoot, `script-output-${++scriptRuns}.log`);
    const log = fs.openSync(logFile, 'w');
    let r;
    try {
        r = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repoRoot, 'scripts', script), ...args], {
            cwd: repoRoot, stdio: ['ignore', log, log], timeout: 900000, windowsHide: true
        });
    } finally {
        fs.closeSync(log);
    }
    if (r.status !== 0) {
        const tail = fs.readFileSync(logFile, 'utf8').trim().split(/\r?\n/).slice(-12).join('\n');
        throw new Error(`${script} ${args.join(' ')} exited ${r.status}${r.error ? ` (${r.error.message})` : ''}:\n${tail}`);
    }
    return Math.round((Date.now() - started) / 100) / 10;
}

function stopSandbox(label) {
    const seconds = runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox]);
    evidence.restarts.push({ label, step: 'stop', seconds });
}

async function startSandbox(label) {
    const seconds = runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild']);
    state = readJson(path.join(sandboxRoot, 'sandbox.json'));
    if (Number(state.Port) !== port) throw new Error(`the sandbox came back on port ${state.Port}`);
    evidence.restarts.push({ label, step: 'start', seconds });
    // The plugin answers once the server does; give the first request a few tries.
    for (let i = 0; i < 30; i++) {
        const r = await call('admin', 'GET', '/Bookmarks/Capabilities').catch(() => null);
        if (r && r.status === 200) return;
        await delay(1000);
    }
    throw new Error('Bookmarks did not answer after the start');
}

// Only the demo's own files in the Bookmarks data folder, and the kit's marks.
function demoFiles() {
    const found = [];
    const add = (folder, test) => {
        if (!fs.existsSync(folder)) return;
        for (const f of fs.readdirSync(folder)) if (test(f)) found.push(path.join(folder, f));
    };
    add(dataFolder, (f) => f === 'kit-users.json' || f.startsWith('kit-users.json.') || f.startsWith(STORE + '.json'));
    add(snapshotFolder, (f) => f.startsWith(STORE + '-'));
    add(preOpFolder, (f) => f.startsWith(STORE + '-'));
    return found;
}

function removeDemoFiles() {
    for (const f of demoFiles()) fs.rmSync(f);
}

const sha = (f) => crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');

// ------------------------------------------------------------------------------------------ helpers

async function getConfig() {
    const r = await call('admin', 'GET', `/Plugins/${bookmarksGuid}/Configuration`);
    if (r.status !== 200 || !r.json) throw new Error(`GET configuration: ${r.status}`);
    return r.json;
}

async function setConfig(patch) {
    const current = await getConfig();
    const r = await call('admin', 'POST', `/Plugins/${bookmarksGuid}/Configuration`, { body: { ...current, ...patch } });
    if (r.status !== 204) throw new Error(`POST configuration: ${r.status}`);
}

async function createUser(name) {
    const existing = await call('admin', 'GET', '/Users');
    for (const u of existing.json || []) {
        if (u.Name === name) await call('admin', 'DELETE', `/Users/${u.Id}`);
    }
    const r = await call('admin', 'POST', '/Users/New', { body: { Name: name, Password: crypto.randomBytes(24).toString('base64url') } });
    if (r.status !== 200 || !r.json || !r.json.Id) throw new Error(`POST /Users/New: ${r.status}`);
    return guid(r.json.Id);
}

async function records() {
    const r = await demo('GET', '/Records');
    return {
        store: r.Store,
        known: r.KnownUsers,
        list: r.Records.map((x) => ({ Id: guid(x.Id), UserId: guid(x.UserId), UserKnown: x.UserKnown, Text: x.Text, HasFile: x.HasFile, DeletedUtc: x.DeletedUtc || null, CreatedAt: x.CreatedAt, UpdatedAt: x.UpdatedAt }))
            .sort((a, b) => a.Id.localeCompare(b.Id))
    };
}

const content = (list) => JSON.stringify(list.map(({ UserKnown, ...rest }) => rest));

async function add(userId, text, hasFile = false) {
    return demo('POST', '/Records', { body: { UserId: userId, Text: text, HasFile: hasFile } });
}

async function snapshots() {
    return demo('GET', '/Snapshots');
}

async function takeSnapshot(keep) {
    const r = await demo('POST', '/Snapshots', { query: { keep } });
    await delay(1100); // snapshot names have whole seconds
    return r.Snapshot;
}

async function importFile(bytes, query) {
    return call('admin', 'POST', `${DEMO}/Import`, { raw: bytes, query });
}

// ------------------------------------------------------------------------------------------ the run

async function run(context) {
    // A fresh process without demo data or marks.
    stopSandbox('baseline');
    removeDemoFiles();
    await startSandbox('baseline');
    const config = await getConfig();
    context.saved = { EnableUserDataDemo: config.EnableUserDataDemo === true, DeletedUserPolicy: config.DeletedUserPolicy };
    check('the settings have their defaults: the demo off, DeletedUserPolicy KeepAsOrphan', config.EnableUserDataDemo === false && config.DeletedUserPolicy === 'KeepAsOrphan', context.saved);

    // 1. Off by default: 403 "turned off"; a viewer is refused by the admin policy.
    const off = await call('admin', 'GET', `${DEMO}/Records`);
    check('the demo is off: 403 with the kit\'s feature-disabled type', off.status === 403 && off.json && off.json.type === 'urn:jellyfin-plugin-kit:feature-disabled', { status: off.status });
    const viewerCall = await call('viewer', 'GET', `${DEMO}/Records`);
    check('a viewer gets 403', viewerCall.status === 403, { status: viewerCall.status });

    // 2. On, with records of five users.
    await setConfig({ EnableUserDataDemo: true, DeletedUserPolicy: 'KeepAsOrphan' });
    await demo('POST', '/Reset');
    const keepUser = await createUser('users-probe-keep');
    const deleteUser = await createUser('users-probe-delete');
    context.tempUsers = [keepUser, deleteUser];
    for (const [who, count, withFile] of [[ids.admin, 4, 1], [ids.viewer, 3, 1], [ids.viewer2, 2, 0], [keepUser, 3, 1], [deleteUser, 2, 1]]) {
        for (let i = 0; i < count; i++) await add(who, `record ${i + 1}`, i < withFile);
    }
    let r = await records();
    check('14 records of five users, each user\'s name recorded on its write (KnownUsers)', r.list.length === 14 && Object.keys(r.known).length === 5 && r.known[keepUser] === 'users-probe-keep' && r.known[ids.viewer] === 'viewer',
        { records: r.list.length, knownUsers: Object.keys(r.known).length });
    let run1 = await demo('POST', '/Reconcile');
    check('the reconciler finds no unknown user', run1.GuardTripped === false && run1.OrphanUsers.length === 0 && run1.StoredUsers === 5, { StoredUsers: run1.StoredUsers, ServerUsers: run1.ServerUsers });

    // 3. "Delete a user: the policy is applied": KeepAsOrphan.
    const deleted = await call('admin', 'DELETE', `/Users/${keepUser}`);
    context.tempUsers = context.tempUsers.filter((u) => u !== keepUser);
    r = await records();
    const kept = r.list.filter((x) => x.UserId === keepUser);
    check('deleting users-probe-keep (KeepAsOrphan): its 2 records without a file are gone, the one with a file is a tombstone under its id',
        deleted.status === 204 && kept.length === 1 && kept[0].HasFile === true && kept[0].DeletedUtc !== null && r.list.length === 12,
        { status: deleted.status, left: kept.length, records: r.list.length });
    let usersNow = await demo('GET', '/Users');
    const orphan = usersNow.OrphanUsers.find((o) => guid(o.UserId) === keepUser);
    check('it is listed as an orphan, marked "Deleted", with its last name, and the name is kept', !!orphan && orphan.Marked === true && orphan.Source === 'Deleted' && orphan.Records === 1
        && orphan.LastKnownName === 'users-probe-keep' && r.known[keepUser] === 'users-probe-keep', orphan);
    let snaps = await snapshots();
    check('a pre-operation snapshot "user-deleted" was taken first', snaps.Snapshots.some((s) => s.Kind === 'PreOp' && s.Operation === 'user-deleted'), snaps.Snapshots.map((s) => s.Name));

    // The Delete policy.
    await setConfig({ DeletedUserPolicy: 'Delete' });
    const deleted2 = await call('admin', 'DELETE', `/Users/${deleteUser}`);
    context.tempUsers = [];
    r = await records();
    check('deleting users-probe-delete (Delete): all its records and its name are gone', deleted2.status === 204 && !r.list.some((x) => x.UserId === deleteUser) && !(deleteUser in r.known) && r.list.length === 10,
        { status: deleted2.status, records: r.list.length });
    await setConfig({ DeletedUserPolicy: 'KeepAsOrphan' });
    run1 = await demo('POST', '/Reconcile');
    usersNow = await demo('GET', '/Users');
    check('the next reconciliation: one orphan (the kept one), no banner, nothing changed', run1.GuardTripped === false && run1.OrphanUsers.length === 1 && usersNow.Banner === null && usersNow.StoredUsersMismatch === false
        && usersNow.Message === '1 records belong to 1 unknown users.', { message: usersNow.Message });
    const diagnostics = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    check('Diagnostics has the Users entry: policy, orphan list', diagnostics.status === 200 && diagnostics.json.Users && diagnostics.json.Users.DeletedUserPolicy === 'KeepAsOrphan' && diagnostics.json.Users.OrphanUsers.length === 1,
        diagnostics.json && diagnostics.json.Users && { policy: diagnostics.json.Users.DeletedUserPolicy, orphans: diagnostics.json.Users.OrphanUsers.length });

    // 4. Export, then import dry run, Merge, then Replace.
    const beforeExport = await records();
    const exported = await call('admin', 'GET', `${DEMO}/Export`);
    const envelope = exported.json;
    const disposition = exported.headers.get('content-disposition') || '';
    check('Export: 200, an attachment named bookmarks-userdata-demo-export-<ts>.json, Cache-Control no-store', exported.status === 200 && /attachment/.test(disposition) && /bookmarks-userdata-demo-export-\d{8}-\d{6}\.json/.test(disposition)
        && /no-store/.test(exported.headers.get('cache-control') || ''), { disposition, cacheControl: exported.headers.get('cache-control') });
    check('the envelope: format, plugin id, schema, server id, the users with names, settings, data and an archive', envelope && envelope.Format === 'jellyfin-plugin-export/1' && envelope.PluginId === bookmarksGuid
        && envelope.SchemaVersion === 1 && typeof envelope.ServerId === 'string' && envelope.Users.length === 4 && envelope.Users.every((u) => /^[0-9a-f]{32}$/.test(u.Id) && u.Name)
        && envelope.Settings && envelope.Settings.EnableUserDataDemo === true && envelope.Data.Records.length === 10 && Array.isArray(envelope.Archive),
        envelope && { Users: envelope.Users.length, Records: envelope.Data.Records.length, keys: Object.keys(envelope) });
    fs.mkdirSync(workRoot, { recursive: true });
    const exportBytes = exported.bytes;
    const hashBefore = sha(storeFile);
    const exportedOnly = (list) => content(list.filter((x) => x.UserId !== keepUser));
    let rep = await importFile(exportBytes);
    check('Import dry run (the default) of the same data by id: 9 unchanged; the deleted user\'s tombstone is reported with its user and skipped; nothing written',
        rep.status === 200 && rep.json.DryRun === true && rep.json.Records.Unchanged === 9 && rep.json.Records.SkippedUnknownUser === 1 && rep.json.Users.Unknown.length === 1
        && guid(rep.json.Users.Unknown[0].Id) === keepUser && rep.json.Users.Unknown[0].Name === 'users-probe-keep' && rep.json.Users.Unknown[0].Reason === 'DeletedOnThisServer'
        && rep.json.SafetySnapshot === null && sha(storeFile) === hashBefore,
        rep.json && { records: rep.json.Records, unknown: rep.json.Users.Unknown.length });
    const full = await takeSnapshot(7);
    await demo('POST', '/Reset');
    rep = await importFile(exportBytes, { dryRun: true });
    check('into an empty store, a dry run says 9 would be added', rep.status === 200 && rep.json.Records.Added === 9 && (await records()).list.length === 0, rep.json && rep.json.Records);
    rep = await importFile(exportBytes, { dryRun: false });
    let after = await records();
    check('Merge: 9 added, the records equal the exported ones of this server\'s users, after an "import" snapshot', rep.status === 200 && rep.json.DryRun === false && rep.json.Records.Added === 9
        && content(after.list) === exportedOnly(beforeExport.list) && /^userdata-demo-import-/.test(rep.json.SafetySnapshot || ''), { counts: rep.json && rep.json.Records, snapshot: rep.json && rep.json.SafetySnapshot });
    rep = await importFile(exportBytes, { dryRun: false, mode: 'Merge' });
    check('Merge again: 9 unchanged', rep.status === 200 && rep.json.Records.Unchanged === 9 && rep.json.Records.Added === 0, rep.json && rep.json.Records);
    await add(ids.viewer2, 'local only');
    rep = await importFile(exportBytes, { dryRun: false, mode: 'Replace' });
    after = await records();
    check('Replace: the local record is removed and the store equals the export', rep.status === 200 && rep.json.Records.Removed === 1 && content(after.list) === exportedOnly(beforeExport.list), rep.json && rep.json.Records);
    rep = await importFile(exportBytes, { dryRun: false, mode: 'Replace' });
    check('Replace with the same data: 9 unchanged, nothing removed', rep.status === 200 && rep.json.Records.Unchanged === 9 && rep.json.Records.Removed === 0, rep.json && rep.json.Records);
    rep = await importFile(exportBytes, { mapUsersBy: 'Name' });
    check('mapUsersBy=Name: admin, viewer and viewer2 found by name; the deleted user reported and its record skipped', rep.status === 200 && rep.json.Users.Mapped.length === 3 && rep.json.Users.Unknown.length === 1
        && guid(rep.json.Users.Unknown[0].Id) === keepUser && rep.json.Records.SkippedUnknownUser === 1 && rep.json.Records.Unchanged === 9, rep.json && rep.json.Users);
    const bad = await call('admin', 'POST', `${DEMO}/Import`, { body: { Format: 'something/1' } });
    const newer = await importFile(Buffer.from(JSON.stringify({ ...envelope, SchemaVersion: 99, Data: { ...envelope.Data, Version: 99 } })));
    const other = await importFile(Buffer.from(JSON.stringify({ ...envelope, PluginId: '00000000-0000-0000-0000-000000000001' })));
    check('refused: not an export 400, a newer schema 409, another plugin\'s export 400 (ProblemDetails)', problem(bad, 400) && problem(newer, 409) && problem(other, 400),
        { notAnExport: bad.status, newer: newer.status, otherPlugin: other.status });
    const back = await demo('POST', '/Snapshots/Restore', { query: { name: full } });
    after = await records();
    check('the snapshot taken before the import tests brings back all 10, the tombstone included', back.Records === 10 && content(after.list) === content(beforeExport.list), { records: back.Records });

    // A new user takes the deleted user's name. Neither an import by name (with the export's Deleted flag, or
    // without it: this server's marks) nor Remap by name gives it the deleted user's tombstone.
    const flagged = envelope.Users.filter((u) => u.Deleted === true);
    check('the export flags the deleted user: Deleted true, with its last name; no one else', flagged.length === 1 && guid(flagged[0].Id) === keepUser && flagged[0].Name === 'users-probe-keep',
        envelope.Users.map((u) => ({ name: u.Name, deleted: u.Deleted === true })));
    const sameName = await createUser('users-probe-keep');
    context.tempUsers.push(sameName);
    const unflaggedBytes = Buffer.from(JSON.stringify({ ...envelope, Users: envelope.Users.map(({ Deleted, ...u }) => u) }));
    const byName = [await importFile(exportBytes, { mapUsersBy: 'Name' }), await importFile(unflaggedBytes, { mapUsersBy: 'Name' })];
    check('a new user named users-probe-keep: mapUsersBy=Name, with and without the flag, still skips the deleted user\'s record (DeletedOnThisServer); nothing is mapped to the new user',
        byName.every((x) => x.status === 200 && x.json.Users.Mapped.length === 3 && !x.json.Users.Mapped.some((m) => guid(m.NewId) === sameName)
            && x.json.Users.Unknown.length === 1 && guid(x.json.Users.Unknown[0].Id) === keepUser && x.json.Users.Unknown[0].Reason === 'DeletedOnThisServer' && x.json.Records.SkippedUnknownUser === 1),
        byName.map((x) => x.json && { mapped: x.json.Users.Mapped.map((m) => m.Name), unknown: x.json.Users.Unknown.map((u) => u.Reason) }));
    const remapSameName = await demo('POST', '/Users/Remap', { query: { by: 'Name', dryRun: true } });
    check('Remap by name (dry run) with the new user: the deleted user stays unmatched (DeletedOnThisServer)', remapSameName.Mapped.length === 0 && remapSameName.Unmatched.length === 1
        && guid(remapSameName.Unmatched[0].OldId) === keepUser && remapSameName.Unmatched[0].Reason === 'DeletedOnThisServer', remapSameName.Unmatched);
    await call('admin', 'DELETE', `/Users/${sameName}`);
    context.tempUsers = context.tempUsers.filter((u) => u !== sameName);

    // 5. Snapshots rotate (daily class, keep 3) and the pre-operation class keeps the newest 5 (seven were taken: two
    //    user-deleted, four imports, one restore).
    const taken = [];
    for (let i = 0; i < 4; i++) taken.push(await takeSnapshot(3));
    snaps = await snapshots();
    const daily = snaps.Snapshots.filter((s) => s.Kind === 'Daily');
    const preOp = snaps.Snapshots.filter((s) => s.Kind === 'PreOp');
    check('four daily snapshots with keep=3: the newest three are kept', daily.length === 3 && daily.map((s) => s.Name).join() === taken.slice(1).reverse().join() && daily.every((s) => s.SchemaVersion === 1 && s.Size > 0),
        daily.map((s) => s.Name));
    check('pre-operation snapshots: their own class, the newest 5 of the 7 (no user-deleted one is left)', preOp.length === 5 && preOp.every((s) => /^userdata-demo-(import|restore)-/.test(s.Name)), preOp.map((s) => s.Name));

    // 6. Restore snapshot while the server runs.
    const good = await records();
    const S = taken[3];
    await add(ids.admin, 'after the snapshot 1');
    await add(ids.admin, 'after the snapshot 2');
    let restored = await demo('POST', '/Snapshots/Restore', { query: { name: S } });
    after = await records();
    check('Restore snapshot: the data is the snapshot\'s again, after a "restore" safety snapshot', restored.RestoredFrom === S && restored.Records === 10 && content(after.list) === content(good.list)
        && /^userdata-demo-restore-/.test(restored.SafetySnapshot || ''), restored);
    const safety = restored.SafetySnapshot;
    const unknownName = await call('admin', 'POST', `${DEMO}/Snapshots/Restore`, { query: { name: 'userdata-demo-20200101-000000.json' } });
    const traversal = await call('admin', 'POST', `${DEMO}/Snapshots/Restore`, { query: { name: '..\\userdata-demo.json' } });
    const newerFile = path.join(snapshotFolder, 'userdata-demo-20200102-000000.json');
    fs.writeFileSync(newerFile, JSON.stringify({ Version: 2, Records: [] }));
    const newerRestore = await call('admin', 'POST', `${DEMO}/Snapshots/Restore`, { query: { name: 'userdata-demo-20200102-000000.json' } });
    fs.rmSync(newerFile);
    check('refused: an unknown name 404, a path 404, a newer schema 409, and the data is unchanged', problem(unknownName, 404) && problem(traversal, 404) && problem(newerRestore, 409)
        && content((await records()).list) === content(good.list), { unknown: unknownName.status, traversal: traversal.status, newer: newerRestore.status });
    restored = await demo('POST', '/Snapshots/Restore', { query: { name: safety } });
    check('the safety snapshot undoes the restore', restored.Records === 12, restored);
    await demo('POST', '/Snapshots/Restore', { query: { name: S } });

    // 7. The manual procedure: stop, copy the snapshot over the store file, start.
    const S2 = await takeSnapshot(3);
    await add(ids.viewer, 'written after S2');
    stopSandbox('manual restore');
    fs.copyFileSync(path.join(snapshotFolder, S2), storeFile);
    await startSandbox('manual restore');
    after = await records();
    check('manual restore (server stopped, snapshot copied over userdata-demo.json, started): the snapshot\'s data, store Ok', content(after.list) === content(good.list) && after.store.Status === 'Ok', { status: after.store.Status, records: after.list.length });

    // 7b. The store written by a newer version (a downgrade): export still works, at the file's version; the
    //     reconciler leaves the store out, and an orphan's mark stays, so it doesn't count as new when the store is back.
    const strayUser = crypto.randomUUID();
    const withStray = readJson(storeFile);
    withStray.Records.push({ ...withStray.Records[0], Id: crypto.randomUUID(), UserId: strayUser, Text: 'of a user deleted while the plugin was off', HasFile: false });
    delete withStray.Records[withStray.Records.length - 1].DeletedUtc;
    const straySnapshot = 'userdata-demo-20200103-000000.json';
    fs.writeFileSync(path.join(snapshotFolder, straySnapshot), JSON.stringify(withStray, null, 2));
    await demo('POST', '/Snapshots/Restore', { query: { name: straySnapshot } });
    fs.rmSync(path.join(snapshotFolder, straySnapshot));
    let strayRun = await demo('POST', '/Reconcile');
    const marksNow = () => (fs.existsSync(marksFile) ? readJson(marksFile).Orphans : []).map((m) => ({ id: guid(m.UserId), source: m.Source }));
    check('a record of an unknown user (one of 11; restored from a snapshot, which the demo follows with a reconciliation): marked by the reconciler, no guard',
        strayRun.GuardTripped === false && strayRun.OrphanUsers.some((o) => guid(o.UserId) === guid(strayUser) && o.Source === 'Reconciler')
        && marksNow().some((m) => m.id === guid(strayUser) && m.source === 'Reconciler'), { orphans: strayRun.OrphanUsers.map((o) => o.Source), marks: marksNow().length });
    stopSandbox('newer version');
    const v1Bytes = fs.readFileSync(storeFile);
    const newerDoc = { ...JSON.parse(v1Bytes.toString('utf8')), Version: 2, AddedInVersion2: { Note: 'a field this version does not know' } };
    fs.writeFileSync(storeFile, JSON.stringify(newerDoc, null, 2));
    await startSandbox('newer version');
    const newerState = await records();
    const newerExport = await call('admin', 'GET', `${DEMO}/Export`);
    check('store of a newer version: ReadOnlyNewerVersion; Export 200 at SchemaVersion 2 with the file\'s data as it is (the unknown field too)', newerState.store.Status === 'ReadOnlyNewerVersion' && newerExport.status === 200
        && newerExport.json && newerExport.json.SchemaVersion === 2 && JSON.stringify(newerExport.json.Data) === JSON.stringify(newerDoc) && newerExport.json.Users.length >= 4,
        { status: newerState.store.Status, exportStatus: newerExport.status, schema: newerExport.json && newerExport.json.SchemaVersion });
    const newerImport = await importFile(newerExport.bytes);
    check('that export is refused here: 409 newer schema', problem(newerImport, 409), { status: newerImport.status });
    strayRun = await demo('POST', '/Reconcile');
    check('the reconciler leaves the read-only store out and keeps the orphan\'s mark', strayRun.SkippedOwners.includes(STORE) && strayRun.OrphanUsers.length === 0
        && marksNow().some((m) => m.id === guid(strayUser) && m.source === 'Reconciler'), { skipped: strayRun.SkippedOwners, marks: marksNow() });
    stopSandbox('the version back');
    fs.writeFileSync(storeFile, v1Bytes);
    await startSandbox('the version back');
    strayRun = await demo('POST', '/Reconcile');
    check('the store back: no new orphan, no guard, no banner', strayRun.GuardTripped === false && strayRun.NewlyOrphaned.length === 0 && strayRun.OrphanUsers.some((o) => guid(o.UserId) === guid(strayUser))
        && (await demo('GET', '/Users')).Banner === null, { newly: strayRun.NewlyOrphaned, orphans: strayRun.OrphanUsers.length });
    await demo('POST', '/Snapshots/Restore', { query: { name: S2 } });
    strayRun = await demo('POST', '/Reconcile');
    after = await records();
    check('restoring S2 removes the stray record, and its mark goes', content(after.list) === content(good.list) && !marksNow().some((m) => m.id === guid(strayUser)), { records: after.list.length });

    // 8. A data folder restored onto a rebuilt server. The stored ids of admin, viewer and viewer2 become
    //    ids this server doesn't know, in a copy of the data that is then put in place, as a restore would.
    stopSandbox('restored data folder');
    const copyRoot = path.join(workRoot, 'restored-copy');
    fs.rmSync(copyRoot, { recursive: true, force: true });
    fs.mkdirSync(copyRoot, { recursive: true });
    const foreign = { [ids.admin]: crypto.randomUUID().replace(/-/g, ''), [ids.viewer]: crypto.randomUUID().replace(/-/g, ''), [ids.viewer2]: crypto.randomUUID().replace(/-/g, '') };
    const dashed = (n) => `${n.slice(0, 8)}-${n.slice(8, 12)}-${n.slice(12, 16)}-${n.slice(16, 20)}-${n.slice(20)}`;
    for (const name of fs.readdirSync(dataFolder).filter((f) => f === STORE + '.json' || f === STORE + '.json.bak')) {
        let text = fs.readFileSync(path.join(dataFolder, name), 'utf8');
        for (const [from, to] of Object.entries(foreign)) {
            text = text.split(from).join(to).split(dashed(from)).join(dashed(to));
        }
        fs.writeFileSync(path.join(copyRoot, name), text);
    }
    for (const name of fs.readdirSync(copyRoot)) fs.copyFileSync(path.join(copyRoot, name), path.join(dataFolder, name));
    await startSandbox('restored data folder');
    const restoredFolder = await records();
    check('after the restore: the same 10 records, none of admin\'s, viewer\'s or viewer2\'s ids known to the server', restoredFolder.list.length === 10 && restoredFolder.list.filter((x) => !x.UserKnown).length === 10
        && restoredFolder.list.filter((x) => Object.values(foreign).includes(x.UserId)).length === 9, { records: restoredFolder.list.length });
    const reconcile = await demo('POST', '/Reconcile');
    const unchanged = await records();
    check('maintenance (the reconciler): the guard trips, three ids would become orphans, nothing is deleted or changed', reconcile.GuardTripped === true && reconcile.NewlyOrphaned.length === 3
        && content(unchanged.list) === content(restoredFolder.list), { GuardTripped: reconcile.GuardTripped, NewlyOrphaned: reconcile.NewlyOrphaned.length });
    usersNow = await demo('GET', '/Users');
    const diag2 = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    check('the "unknown users" banner: in the Users entry and in Diagnostics', usersNow.StoredUsersMismatch === true && /Stored users don't match this server/.test(usersNow.Banner || '')
        && diag2.json && diag2.json.Users && diag2.json.Users.Banner === usersNow.Banner && usersNow.Message === '10 records belong to 4 unknown users.', { banner: usersNow.Banner, message: usersNow.Message });
    if (opts.webcheck) {
        const started = Date.now();
        const w = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', 'users-banner.json', '--out', path.join(workRoot, 'users-banner.json')], {
            cwd: repoRoot, encoding: 'utf8', timeout: 300000, windowsHide: true
        });
        let result = null;
        try { result = readJson(path.join(workRoot, 'users-banner.json')); } catch { result = null; }
        check('the settings page shows the banner (webcheck users-banner.json)', w.status === 0 && result && result.ok === true,
            { exit: w.status, seconds: Math.round((Date.now() - started) / 1000), leftovers: result && result.leftovers, steps: result && result.steps && result.steps.length });
    }
    const dry = await demo('POST', '/Users/Remap', { query: { by: 'Name', dryRun: true } });
    const expected = Object.fromEntries(Object.entries(foreign).map(([real, fake]) => [fake, real]));
    check('Remap by name, dry run: admin, viewer and viewer2 map back to their ids; the deleted user is left alone; nothing changed', dry.DryRun === true && dry.Mapped.length === 3
        && dry.Mapped.every((m) => expected[guid(m.OldId)] === guid(m.NewId)) && dry.Unmatched.length === 1 && guid(dry.Unmatched[0].OldId) === keepUser && dry.Unmatched[0].Reason === 'DeletedOnThisServer'
        && content((await records()).list) === content(restoredFolder.list), { mapped: dry.Mapped.map((m) => m.Name), unmatched: dry.Unmatched.map((u) => u.Reason) });
    const applied = await demo('POST', '/Users/Remap', { query: { by: 'Name', dryRun: false } });
    after = await records();
    check('Remap by name: 9 records moved back, ownership restored exactly, after a "remap" snapshot', applied.MovedRecords === 9 && applied.SafetySnapshots.length === 1 && /^userdata-demo-remap-/.test(applied.SafetySnapshots[0])
        && content(after.list) === content(good.list), { moved: applied.MovedRecords, snapshots: applied.SafetySnapshots });
    const clean = await demo('POST', '/Reconcile');
    usersNow = await demo('GET', '/Users');
    check('the next reconciliation: no banner, the one deleted user\'s tombstone is the only orphan', clean.GuardTripped === false && usersNow.Banner === null && usersNow.OrphanUsers.length === 1, { message: usersNow.Message });

    // 9. Snapshots taken before the store started empty survive a restart.
    const earlier = [];
    for (let i = 0; i < 3; i++) earlier.push(await takeSnapshot(3));
    stopSandbox('store unreadable');
    fs.writeFileSync(storeFile, '[]');
    fs.writeFileSync(storeFile + '.bak', 'garbage');
    await startSandbox('store unreadable');
    let now = await records();
    snaps = await snapshots();
    check('the file and its .bak unreadable: RecoveredEmpty, no records, and the three earlier snapshots are kept', now.store.Status === 'RecoveredEmpty' && now.list.length === 0 && snaps.KeepSnapshotsBeforeUtc
        && earlier.every((e) => snaps.Snapshots.some((s) => s.Name === e && s.Kept === true)) && fs.existsSync(storeFile + '.keep-snapshots'),
        { status: now.store.Status, keepBefore: snaps.KeepSnapshotsBeforeUtc });
    await add(ids.admin, 'written while empty');
    for (let i = 0; i < 4; i++) await takeSnapshot(3);
    snaps = await snapshots();
    check('four more snapshots with keep=3: the earlier three are kept, three of the later ones', earlier.every((e) => snaps.Snapshots.some((s) => s.Name === e && s.Kept))
        && snaps.Snapshots.filter((s) => s.Kind === 'Daily' && !s.Kept).length === 3, snaps.Snapshots.filter((s) => s.Kind === 'Daily').map((s) => `${s.Name}${s.Kept ? ' (kept)' : ''}`));
    stopSandbox('restart after starting empty');
    await startSandbox('restart after starting empty');
    now = await records();
    await takeSnapshot(3);
    snaps = await snapshots();
    check('after a restart (the store Ok again): still kept, the later ones still rotate', now.store.Status === 'Ok' && snaps.KeepSnapshotsBeforeUtc && earlier.every((e) => snaps.Snapshots.some((s) => s.Name === e && s.Kept))
        && snaps.Snapshots.filter((s) => s.Kind === 'Daily' && !s.Kept).length === 3, { status: now.store.Status, daily: snaps.Snapshots.filter((s) => s.Kind === 'Daily').length });
    const laterDaily = snaps.Snapshots.find((s) => s.Kind === 'Daily' && !s.Kept);
    restored = await demo('POST', '/Snapshots/Restore', { query: { name: laterDaily.Name } });
    snaps = await snapshots();
    check('snapshots: restoring a snapshot taken after the store started empty keeps the earlier three protected', restored.KeepSnapshotsBeforeUtc && snaps.KeepSnapshotsBeforeUtc
        && earlier.every((e) => snaps.Snapshots.some((s) => s.Name === e && s.Kept)) && fs.existsSync(storeFile + '.keep-snapshots'),
        { restored: laterDaily.Name, keepBefore: restored.KeepSnapshotsBeforeUtc });
    restored = await demo('POST', '/Snapshots/Restore', { query: { name: earlier[2] } });
    now = await records();
    snaps = await snapshots();
    check('restoring the newest of them brings the data back and releases them', restored.Records === 10 && content(now.list) === content(good.list) && !snaps.KeepSnapshotsBeforeUtc
        && !fs.existsSync(storeFile + '.keep-snapshots'), { records: restored.Records, keepBefore: snaps.KeepSnapshotsBeforeUtc });
    await takeSnapshot(3);
    snaps = await snapshots();
    check('rotation is as usual again: three daily snapshots', snaps.Snapshots.filter((s) => s.Kind === 'Daily').length === 3, snaps.Snapshots.filter((s) => s.Kind === 'Daily').map((s) => s.Name));
    evidence.finalRecords = now.list.length;
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { saved: null, tempUsers: [] };
    let failure = null;
    try {
        await run(context);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        for (const id of context.tempUsers || []) await call('admin', 'DELETE', `/Users/${id}`).catch(() => null);
        if (context.saved) {
            await setConfig({ EnableUserDataDemo: context.saved.EnableUserDataDemo, DeletedUserPolicy: context.saved.DeletedUserPolicy })
                .catch((error) => check('clean-up: the settings restored', false, String(error)));
            const offAgain = await call('admin', 'GET', `${DEMO}/Records`).catch(() => ({ status: 0 }));
            check('clean-up: the demo setting and the policy as they were', context.saved.EnableUserDataDemo || offAgain.status === 403, { status: offAgain.status });
        }
        // The copies hold user names; the export was only kept in memory.
        fs.rmSync(path.join(workRoot, 'restored-copy'), { recursive: true, force: true });
        for (const f of fs.readdirSync(workRoot).filter((name) => name.startsWith('script-output-'))) {
            try { fs.rmSync(path.join(workRoot, f)); } catch { /* still open in the server it started; left for the next run */ }
        }
        for (const f of fs.existsSync(dataFolder) ? fs.readdirSync(dataFolder) : []) {
            if (f.startsWith(STORE + '.json.corrupt-')) fs.rmSync(path.join(dataFolder, f));
        }
    }
    const pass = !failure && checks.every((c) => c.pass);
    const result = { pass, passed: checks.filter((c) => c.pass).length, total: checks.length, checks, evidence };
    const text = JSON.stringify(result, null, 2);
    if (opts.out) {
        fs.mkdirSync(path.dirname(path.resolve(opts.out)), { recursive: true });
        fs.writeFileSync(opts.out, text);
    }
    console.log(text);
    process.exit(pass ? 0 : 1);
}

await main();
