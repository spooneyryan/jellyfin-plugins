#!/usr/bin/env node
// Bookmarks' own data through the shared kit's users and data services, in a running sandbox: the kit's user checks
// (a deleted user, a restored data folder), its season-folder check, and its export, import and retention checks, with
// Bookmarks' real store. The developer demos (users-probe.mjs, library-demo-probe.mjs) check the same in the kit alone;
// this probe checks Bookmarks as the kit's real consumer.
// Node 22 built-ins only. Loopback only, never port 8096. The sandbox users' tokens come from credentials.json and are
// never printed; the two throwaway users it creates (tmp-orphan, tmp-delete) get random passwords that are only held in
// memory.
//
//   node scripts/bookmarks-data-probe.mjs --sandbox default [--timeout <scan seconds>] [--skip-webcheck] [--out result.json]
//
// Checks, in order (every bookmark it makes is named or noted "data-probe ..."):
//   1. a deleted user with DeletedUserPolicy KeepAsOrphan: a throwaway user's bookmark without a clip is deleted and the one
//      with a clip file becomes a clip-only tombstone of the deleted id (its file kept), after a "user-deleted" snapshot;
//      Diagnostics lists the user as an orphan marked Deleted, and the per-user counts as unknown with one tombstone;
//   2. DeletedUserPolicy Delete: another throwaway user's bookmarks and clip file go with the user; then Apply deleted-user
//      policy (POST /Bookmarks/Admin/Users/ApplyPolicy) removes the first user's tombstone and its clip file;
//   3. a restored data folder: with the sandbox stopped, the stored ids of admin, viewer and viewer2 in bookmarks.json are
//      replaced by ids the server doesn't know; started and maintenance run: every bookmark is still there, the guard
//      trips, the banner shows (webcheck users-banner.json); Remap by name maps the three back, ownership exactly as before,
//      after a "remap" snapshot, and the banner is gone;
//   4. export (the kit's envelope, an attachment with no-store), a dry run, Merge, Replace and Replace again of it, and a
//      dry run matching users by name;
//   5. the daily snapshots rotate to SnapshotsToKeep;
//   6. Restore snapshot in the running server (the import's safety snapshot), and its undo; the manual procedure (stopped,
//      a daily snapshot copied over bookmarks.json, started);
//   7. the test series' Season 02 folder held out: its episode's bookmark detaches and the Season 01 one doesn't; put back:
//      reattached (Exact);
//   8. retention: DetachedRetentionDays 1 with the developer clock 2 days on (a Debug build): maintenance moves a detached
//      bookmark into archive/bookmarks-archived.jsonl, the export's Archive carries it (Data doesn't); its movie back, the
//      next maintenance moves it back, attached (Exact), and out of the archive;
//   9. no Error of Bookmarks or the kit in the log, and no line of theirs at Information or above naming a bookmark.
// Clean-up: the settings as they were, the throwaway users deleted, the media put back; then, with the sandbox stopped,
// Bookmarks' data folder files it changes (bookmarks.json and its .bak, kit-users.json, the snapshots and the archive) are
// put back from the copies taken at the start, and the sandbox is started again (dev-server.ps1 -Background -NoBuild).
// It changes nothing outside the sandbox; media renames happen only inside .sandbox/media and its hold folder
// .sandbox/<name>/bookmarks-data-probe/held, recorded in moves.json first and undone at the end, on failure and at the
// start of the next run. It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PROBE = 'data-probe';
const TICKS = 10_000_000;
const SERIES_FOLDER = 'Test Series (2020) [tvdbid-900401]';
const RETENTION_FOLDER = 'Tagged Movie (2012) [tmdbid-900001]';
const THROWAWAY = ['tmp-orphan', 'tmp-delete'];

function usage() {
    return 'usage: node scripts/bookmarks-data-probe.mjs --sandbox <name> [--timeout <scan seconds>] [--skip-webcheck] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, timeout: 300, out: null, webcheck: true };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--skip-webcheck') o.webcheck = false;
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!Number.isFinite(o.timeout) || o.timeout <= 0) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const mediaRoot = path.join(repoRoot, '.sandbox', 'media');
const workRoot = path.join(sandboxRoot, 'bookmarks-data-probe');
const heldRoot = path.join(workRoot, 'held');
const movesFile = path.join(workRoot, 'moves.json');
const backupRoot = path.join(workRoot, 'backup');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
let state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
const dataFolder = path.join(path.resolve(state.DataDir || path.join(sandboxRoot, 'data')), 'data', 'bookmarks');
const storeFile = path.join(dataFolder, 'bookmarks.json');
const snapshotFolder = path.join(dataFolder, 'snapshots');
const preOpFolder = path.join(snapshotFolder, 'pre-op');
const archiveFolder = path.join(dataFolder, 'archive');
const archiveFile = path.join(archiveFolder, 'bookmarks-archived.jsonl');
const credentials = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
const tokens = {};
for (const name of ['admin', 'viewer', 'viewer2']) {
    if (!credentials[name] || !credentials[name].token || !credentials[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
    tokens[name] = credentials[name].token;
}
const bookmarksGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8')) || [])[1];
if (!bookmarksGuid) throw new Error('no guid in plugins/Bookmarks/build.yaml');

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error(`refusing to work in ${p}`);
    return full;
}
for (const p of [dataFolder, workRoot, logDir]) assertInside(p, sandboxRoot);

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, scans: [], restarts: [], maintenance: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const dashed = (n) => `${n.slice(0, 8)}-${n.slice(8, 12)}-${n.slice(12, 16)}-${n.slice(16, 20)}-${n.slice(20)}`;
const ids = { admin: guid(credentials.admin.id), viewer: guid(credentials.viewer.id), viewer2: guid(credentials.viewer2.id) };
const sha = (f) => crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');

async function call(who, method, p, { body, query, raw } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json' };
    const auth = `MediaBrowser Client="bookmarks-data-probe", Device="sandbox", DeviceId="bookmarks-data-probe-${who}", Version="1.0.0"`;
    if (who === 'login') h.Authorization = auth;
    else if (who !== 'anonymous') h.Authorization = `${auth}, Token="${tokens[who]}"`;
    let payload;
    if (raw !== undefined) { payload = raw; h['Content-Type'] = 'application/json'; }
    else if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const bytes = Buffer.from(await res.arrayBuffer());
    const text = bytes.toString('utf8');
    let json = null;
    if (bytes.length && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, headers: res.headers, json, text, bytes };
}

async function ok(who, method, p, options, expected = [200, 204]) {
    const r = await call(who, method, p, options);
    if (!expected.includes(r.status)) throw new Error(`${method} ${p} as ${who}: ${r.status} ${r.text.slice(0, 300)}`);
    return r.json;
}

async function waitFor(label, predicate, seconds = 30) {
    const until = Date.now() + seconds * 1000;
    for (;;) {
        const last = await predicate();
        if (last) return last;
        if (Date.now() > until) throw new Error(`timed out waiting for: ${label}`);
        await delay(500);
    }
}

// ------------------------------------------------------------------------------------------ settings, users

async function getConfig() {
    return ok('admin', 'GET', `/Plugins/${bookmarksGuid}/Configuration`);
}

async function setConfig(patch) {
    const current = await getConfig();
    await ok('admin', 'POST', `/Plugins/${bookmarksGuid}/Configuration`, { body: { ...current, ...patch } }, [204]);
    const back = await getConfig();
    for (const [k, v] of Object.entries(patch)) {
        if (back[k] !== v) throw new Error(`the setting ${k} reads ${back[k]}, not ${v}`);
    }
}

async function deleteUserNamed(name) {
    const list = await ok('admin', 'GET', '/Users');
    for (const u of list || []) if (u.Name === name) await ok('admin', 'DELETE', `/Users/${u.Id}`);
}

// A throwaway user with a random password (memory only), library access and the download permission, signed in.
async function createUser(name) {
    await deleteUserNamed(name);
    const password = crypto.randomBytes(24).toString('base64url');
    const created = await ok('admin', 'POST', '/Users/New', { body: { Name: name, Password: password } });
    const id = guid(created.Id);
    const policy = created.Policy || (await ok('admin', 'GET', `/Users/${id}`)).Policy;
    if (!policy.EnableAllFolders || !policy.EnableContentDownloading) {
        await ok('admin', 'POST', `/Users/${id}/Policy`, { body: { ...policy, EnableAllFolders: true, EnableContentDownloading: true } }, [204]);
    }
    const auth = await ok('login', 'POST', '/Users/AuthenticateByName', { body: { Username: name, Pw: password } });
    tokens[name] = auth.AccessToken;
    return id;
}

// ------------------------------------------------------------------------------------------ bookmarks, store

const made = [];

async function createBookmark(who, item, { start, end = null, name = null, note = null }) {
    const r = await call(who, 'POST', '/Bookmarks', { body: { ItemId: item.Id, StartTicks: start * TICKS, EndTicks: end === null ? null : end * TICKS, Name: name, Note: note } });
    if (r.status !== 201 || !r.json) throw new Error(`POST /Bookmarks as ${who}: ${r.status} ${r.text.slice(0, 200)}`);
    made.push({ who, id: r.json.Id });
    await delay(20);
    return r.json;
}

async function makeClip(who, id) {
    await ok(who, 'POST', `/Bookmarks/${id}/Clip`, { body: { Mode: 'Fast' } }, [202]);
    return waitFor(`the clip of ${id}`, async () => {
        const r = await call(who, 'GET', `/Bookmarks/${id}`);
        const status = r.json && r.json.Clip && r.json.Clip.Status;
        if (status === 'Completed') return r.json;
        if (status && !['Queued', 'Running'].includes(status)) throw new Error(`the clip of ${id} ended ${status}: ${r.json.Clip.Error}`);
        return null;
    }, 180);
}

function readStore() {
    return readJson(storeFile);
}

function stored(id, doc = readStore()) {
    return (doc.Bookmarks || []).find((b) => guid(b.Id) === guid(id)) || null;
}

// Every bookmark of a user, through their own Query (200 a page).
async function userBookmarks(who) {
    const all = [];
    for (let start = 0; ; start += 200) {
        const page = await ok(who, 'GET', '/Bookmarks/Query', { query: { startIndex: start, limit: 200 } });
        all.push(...page.Items);
        if (all.length >= page.TotalRecordCount || page.Items.length === 0) return all;
    }
}

const idSet = (list) => list.map((b) => guid(b.Id)).sort().join(',');

async function diagnostics() {
    return ok('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
}

function preOpSnapshots() {
    return fs.existsSync(preOpFolder) ? fs.readdirSync(preOpFolder).filter((f) => f.startsWith('bookmarks-')) : [];
}

function dailySnapshots() {
    return fs.existsSync(snapshotFolder) ? fs.readdirSync(snapshotFolder).filter((f) => /^bookmarks-\d{8}-\d{6}(-\d+)?\.json$/.test(f)) : [];
}

// ------------------------------------------------------------------------------------------ media (undoable)

function loadMoves() {
    return fs.existsSync(movesFile) ? readJson(movesFile) : [];
}

function saveMoves(moves) {
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(movesFile, JSON.stringify(moves, null, 2));
}

function holdOut(relative) {
    const src = path.join(mediaRoot, relative);
    const dst = path.join(heldRoot, relative);
    if (!fs.existsSync(src)) throw new Error(`${src} doesn't exist`);
    if (fs.existsSync(dst)) throw new Error(`${dst} exists already`);
    const moves = loadMoves();
    moves.push({ from: src, to: dst });
    saveMoves(moves);
    fs.mkdirSync(path.dirname(dst), { recursive: true });
    fs.renameSync(src, dst);
}

function undoMoves() {
    let moves = loadMoves();
    while (moves.length > 0) {
        const { from, to } = moves[moves.length - 1];
        for (const p of [from, to]) {
            if (!(path.resolve(p).startsWith(mediaRoot + path.sep) || path.resolve(p).startsWith(heldRoot + path.sep))) throw new Error(`refusing to move ${p}`);
        }
        if (fs.existsSync(to) && !fs.existsSync(from)) {
            fs.mkdirSync(path.dirname(from), { recursive: true });
            fs.renameSync(to, from);
        } else if (fs.existsSync(to)) {
            throw new Error(`cannot undo ${to} -> ${from}: both exist; fix by hand`);
        }
        moves = moves.slice(0, -1);
        saveMoves(moves);
    }
}

// ------------------------------------------------------------------------------------------ library, tasks, sandbox, log

async function scan(label) {
    const started = Date.now();
    await ok('admin', 'POST', '/Library/Refresh', {}, [204]);
    await delay(1500);
    for (;;) {
        const tasks = await ok('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } });
        const task = (tasks || []).find((t) => t.Key === 'RefreshLibrary');
        const last = task && task.LastExecutionResult;
        if (task && task.State === 'Idle' && last && Date.parse(last.EndTimeUtc) >= started - 1000) {
            evidence.scans.push({ label, seconds: Math.round((Date.now() - started) / 100) / 10, status: last.Status });
            if (last.Status !== 'Completed') throw new Error(`the scan "${label}" ended ${last.Status}`);
            await delay(2500);
            return;
        }
        if (Date.now() - started > opts.timeout * 1000) throw new Error(`the scan "${label}" did not finish within ${opts.timeout} s`);
        await delay(500);
    }
}

async function maintenanceTask() {
    const tasks = await ok('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } });
    return (tasks || []).find((t) => t.Key === 'BookmarksMaintenance') || null;
}

// "Run the daily maintenance now" (POST /Bookmarks/Admin/Maintenance/Run, 202), waited for through the task's last result.
async function runMaintenance(label) {
    const started = Date.now();
    const queued = await call('admin', 'POST', '/Bookmarks/Admin/Maintenance/Run');
    if (queued.status !== 202) throw new Error(`POST /Bookmarks/Admin/Maintenance/Run: ${queued.status} ${queued.text.slice(0, 200)}`);
    await delay(500);
    for (;;) {
        const now = await maintenanceTask();
        const last = now && now.LastExecutionResult;
        if (now && now.State === 'Idle' && last && Date.parse(last.EndTimeUtc) >= started - 1000) {
            const run = (await diagnostics()).Maintenance.LastRun;
            evidence.maintenance.push({ label, seconds: Math.round((Date.now() - started) / 100) / 10, status: last.Status, failed: run && run.Failed });
            if (last.Status !== 'Completed') throw new Error(`the maintenance "${label}" ended ${last.Status}`);
            await delay(1100); // snapshot names have whole seconds
            return run;
        }
        if (Date.now() - started > 120000) throw new Error(`the maintenance "${label}" did not finish`);
        await delay(500);
    }
}

const step = (run, name) => ((run && run.Steps) || []).find((s) => s.Name === name) || null;
const ownerResult = (s) => (s && s.Result && (s.Result.Owners || []).find((o) => o.Owner === 'bookmarks')) || null;

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

let running = true;
function stopSandbox(label) {
    evidence.restarts.push({ label, step: 'stop', seconds: runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox]) });
    running = false;
}

async function startSandbox(label) {
    evidence.restarts.push({ label, step: 'start', seconds: runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild']) });
    running = true;
    state = readJson(path.join(sandboxRoot, 'sandbox.json'));
    if (Number(state.Port) !== port) throw new Error(`the sandbox came back on port ${state.Port}`);
    for (let i = 0; i < 60; i++) {
        const r = await call('admin', 'GET', '/Bookmarks/Capabilities').catch(() => null);
        if (r && r.status === 200) return;
        await delay(1000);
    }
    throw new Error('Bookmarks did not answer after the start');
}

function logFiles() {
    return fs.readdirSync(logDir).filter((f) => /^log_\d{8}\.log$/.test(f)).sort();
}

function logPosition() {
    const files = logFiles();
    const file = files[files.length - 1];
    return { file, size: file ? fs.statSync(path.join(logDir, file)).size : 0 };
}

function logSince(position) {
    return logFiles().filter((f) => !position.file || f >= position.file).map((f) => {
        const bytes = fs.readFileSync(path.join(logDir, f));
        return (f === position.file ? bytes.subarray(position.size) : bytes).toString('utf8');
    }).join('');
}

// ------------------------------------------------------------------------------------------ backup of the data it changes

function copyTree(from, to) {
    fs.mkdirSync(to, { recursive: true });
    for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
        const a = path.join(from, entry.name);
        const b = path.join(to, entry.name);
        if (entry.isDirectory()) copyTree(a, b);
        else if (entry.isFile()) fs.copyFileSync(a, b);
    }
}

const topFiles = () => fs.readdirSync(dataFolder).filter((f) => /^bookmarks\.json(\.bak|\.keep-snapshots|\.last-restore)?$/.test(f) || f === 'kit-users.json');

function takeBackup() {
    fs.rmSync(backupRoot, { recursive: true, force: true });
    fs.mkdirSync(path.join(backupRoot, 'top'), { recursive: true });
    const files = topFiles();
    for (const f of files) fs.copyFileSync(path.join(dataFolder, f), path.join(backupRoot, 'top', f));
    const had = { files, snapshots: fs.existsSync(snapshotFolder), archive: fs.existsSync(archiveFolder) };
    if (had.snapshots) copyTree(snapshotFolder, path.join(backupRoot, 'snapshots'));
    if (had.archive) copyTree(archiveFolder, path.join(backupRoot, 'archive'));
    fs.writeFileSync(path.join(backupRoot, 'had.json'), JSON.stringify(had));
    return { ...had, sha: sha(storeFile) };
}

// Only with the sandbox stopped.
function restoreBackup() {
    const had = readJson(path.join(backupRoot, 'had.json'));
    for (const f of topFiles()) if (!had.files.includes(f)) fs.rmSync(path.join(dataFolder, f));
    for (const f of had.files) fs.copyFileSync(path.join(backupRoot, 'top', f), path.join(dataFolder, f));
    for (const [folder, name] of [[snapshotFolder, 'snapshots'], [archiveFolder, 'archive']]) {
        fs.rmSync(folder, { recursive: true, force: true });
        if (had[name]) copyTree(path.join(backupRoot, name), folder);
    }
}

// ------------------------------------------------------------------------------------------ the run

async function findItems() {
    const list = await ok('admin', 'GET', '/Items', { query: { userId: credentials.admin.id, Recursive: 'true', IncludeItemTypes: 'Movie,Episode', Fields: 'Path', EnableImages: 'false', EnableUserData: 'false' } });
    const one = (predicate, what) => {
        const found = list.Items.filter(predicate);
        if (found.length !== 1) throw new Error(`expected one ${what}, found ${found.length}`);
        return found[0];
    };
    const inFolder = (item, folder) => typeof item.Path === 'string' && item.Path.split(/[\\/]/).includes(folder);
    return {
        movie: one((i) => i.Type === 'Movie' && inFolder(i, 'Long Test Movie (2021)'), 'Long Test Movie'),
        tagged: one((i) => i.Type === 'Movie' && inFolder(i, RETENTION_FOLDER), 'Tagged Movie'),
        s01e01: one((i) => i.Type === 'Episode' && inFolder(i, SERIES_FOLDER) && i.ParentIndexNumber === 1 && i.IndexNumber === 1, 'S01E01'),
        s02e01: one((i) => i.Type === 'Episode' && inFolder(i, SERIES_FOLDER) && i.ParentIndexNumber === 2 && i.IndexNumber === 1, 'S02E01')
    };
}

async function run(context) {
    const config = await getConfig();
    context.saved = config;
    check('the settings the run starts from: DeletedUserPolicy KeepAsOrphan, DetachedRetentionDays 0, the developer clock 0, Debug build (the clock setting exists)',
        config.DeletedUserPolicy === 'KeepAsOrphan' && config.DetachedRetentionDays === 0 && config.DeveloperClockOffsetDays === 0,
        { DeletedUserPolicy: config.DeletedUserPolicy, SnapshotsToKeep: config.SnapshotsToKeep, DetachedRetentionDays: config.DetachedRetentionDays, DeveloperClockOffsetDays: config.DeveloperClockOffsetDays });
    if (config.DeveloperClockOffsetDays === undefined) throw new Error('DeveloperClockOffsetDays is missing: retention needs a Debug build');
    const items = await findItems();
    const store0 = (await diagnostics()).Store;
    context.backup = takeBackup();
    evidence.start = { store: { Status: store0.Status, Version: store0.Version }, bookmarks: readStore().Bookmarks.length, backup: context.backup };

    // ---- 1. a deleted user, KeepAsOrphan ("Delete a user: the policy is applied")
    const orphan = await createUser(THROWAWAY[0]);
    context.users.push(THROWAWAY[0]);
    const a = await createBookmark(THROWAWAY[0], items.movie, { start: 10, note: `${PROBE} orphan A` });
    const b = await createBookmark(THROWAWAY[0], items.movie, { start: 20, end: 22, name: `${PROBE} orphan B` });
    const bClip = await makeClip(THROWAWAY[0], b.Id);
    const bFile = stored(b.Id).Clip.FilePath;
    const preOpBefore = preOpSnapshots();
    await ok('admin', 'DELETE', `/Users/${orphan}`, {}, [204]);
    context.users = context.users.filter((n) => n !== THROWAWAY[0]);
    const afterDelete = await waitFor('the deleted user\'s bookmarks handled', async () => {
        const doc = readStore();
        const sa = stored(a.Id, doc);
        const sb = stored(b.Id, doc);
        return !sa && sb && sb.DeletedUtc ? { sb, doc } : null;
    }, 30);
    const newPreOp = preOpSnapshots().filter((f) => !preOpBefore.includes(f));
    const d1 = await diagnostics();
    const orphanEntry = (d1.Users.OrphanUsers || []).find((u) => guid(u.UserId) === orphan);
    const orphanCounts = ((d1.BookmarkCounts && d1.BookmarkCounts.Users) || []).find((u) => guid(u.UserId) === orphan);
    check('a deleted user with KeepAsOrphan: the bookmark without a clip is deleted, the one with a clip file becomes a clip-only tombstone of the deleted id with its file kept, after a "user-deleted" snapshot',
        guid(afterDelete.sb.UserId) === orphan && !!afterDelete.sb.DeletedUtc && afterDelete.sb.Clip && afterDelete.sb.Clip.FilePath === bFile && fs.existsSync(bFile)
        && bClip.Clip.Status === 'Completed' && newPreOp.some((f) => /^bookmarks-user-deleted-/.test(f)),
        { tombstone: { DeletedUtc: afterDelete.sb.DeletedUtc, Clip: afterDelete.sb.Clip && { Status: afterDelete.sb.Clip.Status, Mode: afterDelete.sb.Clip.Mode, FileSize: afterDelete.sb.Clip.FileSize } }, fileKept: fs.existsSync(bFile), snapshots: newPreOp });
    check('Diagnostics: the deleted user is an orphan marked Deleted with its last name; the per-user counts show it unknown with one kept clip and no bookmark',
        orphanEntry && orphanEntry.LastKnownName === THROWAWAY[0] && orphanEntry.Marked === true && orphanEntry.Source === 'Deleted'
        && orphanCounts && orphanCounts.Known === false && orphanCounts.Tombstones === 1 && orphanCounts.Bookmarks === 0,
        { orphan: orphanEntry, counts: orphanCounts });
    const reconciled = await runMaintenance('reconcile after the deletion');
    const usersStep = step(reconciled, 'Users');
    const d1b = await diagnostics();
    check('the next maintenance: no guard, no banner, the tombstone still there',
        usersStep && usersStep.Outcome === 'Done' && d1b.Users.StoredUsersMismatch === false && d1b.Users.Banner === null && !!stored(b.Id),
        { step: usersStep && { Outcome: usersStep.Outcome }, message: d1b.Users.Message });

    // ---- 2. DeletedUserPolicy Delete, then Apply deleted-user policy on the first user's tombstone
    await setConfig({ DeletedUserPolicy: 'Delete' });
    const deleting = await createUser(THROWAWAY[1]);
    context.users.push(THROWAWAY[1]);
    const c = await createBookmark(THROWAWAY[1], items.movie, { start: 30, end: 32, name: `${PROBE} delete C` });
    const dPoint = await createBookmark(THROWAWAY[1], items.movie, { start: 40, note: `${PROBE} delete D` });
    await makeClip(THROWAWAY[1], c.Id);
    const cFile = stored(c.Id).Clip.FilePath;
    await ok('admin', 'DELETE', `/Users/${deleting}`, {}, [204]);
    context.users = context.users.filter((n) => n !== THROWAWAY[1]);
    await waitFor('the second user\'s bookmarks deleted', async () => (!stored(c.Id) && !stored(dPoint.Id) ? true : null), 30);
    await waitFor('the clip file deleted', async () => (!fs.existsSync(cFile) ? true : null), 15).catch(() => null);
    check('DeletedUserPolicy Delete: the user\'s bookmarks and their clip file go with the user',
        !stored(c.Id) && !stored(dPoint.Id) && !fs.existsSync(cFile) && !(readStore().PendingFileDeletes || []).some((p) => (p.Path || p) === cFile),
        { fileGone: !fs.existsSync(cFile) });
    const applied = await call('admin', 'POST', '/Bookmarks/Admin/Users/ApplyPolicy', { body: { UserIds: [dashed(orphan)] } });
    await waitFor('the tombstone\'s clip file deleted', async () => (!fs.existsSync(bFile) ? true : null), 15).catch(() => null);
    check('Apply deleted-user policy (Delete) on the first user: its tombstone and its clip file go, after a snapshot',
        applied.status === 200 && applied.json && applied.json.Policy === 'Delete' && applied.json.Affected === 1 && (applied.json.SafetySnapshots || []).length === 1
        && !stored(b.Id) && !fs.existsSync(bFile), applied.json);
    await setConfig({ DeletedUserPolicy: 'KeepAsOrphan' });

    // ---- 3. a restored data folder
    for (const who of ['admin', 'viewer', 'viewer2']) await createBookmark(who, items.movie, { start: 50, name: `${PROBE} restored ${who}` });
    const before = {};
    for (const who of ['admin', 'viewer', 'viewer2']) before[who] = idSet(await userBookmarks(who));
    const recordsBefore = readStore().Bookmarks.length;
    stopSandbox('restored data folder');
    const foreign = { admin: guid(crypto.randomUUID()), viewer: guid(crypto.randomUUID()), viewer2: guid(crypto.randomUUID()) };
    for (const name of ['bookmarks.json', 'bookmarks.json.bak']) {
        const file = path.join(dataFolder, name);
        if (!fs.existsSync(file)) continue;
        let text = fs.readFileSync(file, 'utf8');
        for (const who of Object.keys(foreign)) {
            for (const [from, to] of [[ids[who], foreign[who]], [dashed(ids[who]), dashed(foreign[who])]]) {
                text = text.split(from).join(to).split(from.toUpperCase()).join(to);
            }
        }
        fs.writeFileSync(file, text);
    }
    await startSandbox('restored data folder');
    const restoredText = fs.readFileSync(storeFile, 'utf8').toLowerCase();
    const emptyNow = {};
    for (const who of ['admin', 'viewer', 'viewer2']) emptyNow[who] = (await userBookmarks(who)).length;
    check('after the restore: every bookmark still in bookmarks.json, none of admin\'s, viewer\'s or viewer2\'s ids left in it, so their lists are empty',
        readStore().Bookmarks.length === recordsBefore && Object.values(ids).every((id) => !restoredText.includes(id) && !restoredText.includes(dashed(id)))
        && Object.values(emptyNow).every((n) => n === 0), { records: readStore().Bookmarks.length, lists: emptyNow });
    const guardRun = await runMaintenance('restored data folder');
    const guardStep = step(guardRun, 'Users');
    const d3 = await diagnostics();
    check('maintenance (the reconciler): the guard trips, nothing is deleted or changed; the banner in Diagnostics',
        guardStep && guardStep.Outcome === 'Done' && guardStep.Result && guardStep.Result.GuardTripped === true
        && readStore().Bookmarks.length === recordsBefore && d3.Users.StoredUsersMismatch === true && /^Stored users don't match this server/.test(d3.Users.Banner || ''),
        { step: guardStep && guardStep.Result && { GuardTripped: guardStep.Result.GuardTripped, NewlyOrphaned: (guardStep.Result.NewlyOrphaned || []).length }, banner: d3.Users.Banner, message: d3.Users.Message });
    if (opts.webcheck) {
        const out = path.join(workRoot, 'users-banner.json');
        const w = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', 'users-banner.json', '--out', out], {
            cwd: repoRoot, encoding: 'utf8', timeout: 300000, windowsHide: true
        });
        let result = null;
        try { result = readJson(out); } catch { result = null; }
        check('the settings page shows the banner at its top and the unknown users\' line (webcheck users-banner.json)', w.status === 0 && result && result.ok === true,
            { exit: w.status, steps: result && result.steps && result.steps.length, leftovers: result && result.leftovers, error: result && !result.ok ? result.error : undefined });
    }
    const dry = await ok('admin', 'POST', '/Bookmarks/Admin/Users/Remap', { query: { by: 'Name', dryRun: true } });
    const expected = Object.fromEntries(Object.keys(foreign).map((who) => [foreign[who], ids[who]]));
    const ours = (dry.Mapped || []).filter((m) => expected[guid(m.OldId)]);
    check('Remap by name, dry run: admin, viewer and viewer2 map back to their ids; nothing changed',
        dry.DryRun === true && ours.length === 3 && ours.every((m) => expected[guid(m.OldId)] === guid(m.NewId)) && readStore().Bookmarks.length === recordsBefore
        && (await userBookmarks('admin')).length === 0, { mapped: (dry.Mapped || []).map((m) => m.Name), unmatched: (dry.Unmatched || []).map((u) => u.Reason) });
    const remap = await ok('admin', 'POST', '/Bookmarks/Admin/Users/Remap', { query: { by: 'Name', dryRun: false } });
    const after = {};
    for (const who of ['admin', 'viewer', 'viewer2']) after[who] = idSet(await userBookmarks(who));
    const d3b = await diagnostics();
    check('Remap by name: ownership exactly as before, after a "remap" snapshot; no banner',
        remap.DryRun === false && remap.MovedRecords > 0 && (remap.SafetySnapshots || []).length === 1 && /^bookmarks-remap-/.test(remap.SafetySnapshots[0])
        && Object.keys(before).every((who) => before[who] === after[who]) && d3b.Users.StoredUsersMismatch === false && d3b.Users.Banner === null,
        { moved: remap.MovedRecords, snapshots: remap.SafetySnapshots, same: Object.fromEntries(Object.keys(before).map((who) => [who, before[who] === after[who]])) });

    // ---- 4. export and import
    const exported = await call('admin', 'GET', '/Bookmarks/Admin/Export');
    const e1 = exported.status === 200 ? JSON.parse(exported.text) : null;
    const yDto = (await userBookmarks('admin')).find((x) => x.Name === `${PROBE} restored admin`);
    check('the export: an attachment, not cached, in the kit\'s envelope of Bookmarks at schema 2 with every bookmark of the file',
        e1 && /attachment/i.test(exported.headers.get('content-disposition') || '') && /no-store/i.test(exported.headers.get('cache-control') || '')
        && e1.Format === 'jellyfin-plugin-export/1' && e1.Plugin === 'Bookmarks' && e1.SchemaVersion === 2 && e1.Data.Bookmarks.length === readStore().Bookmarks.length
        && e1.Data.Bookmarks.some((x) => guid(x.Id) === guid(yDto.Id)),
        e1 && { status: exported.status, disposition: exported.headers.get('content-disposition'), cache: exported.headers.get('cache-control'), bookmarks: e1.Data.Bookmarks.length, users: e1.Users.length, archive: e1.Archive.length, version: e1.PluginVersion });
    const x = await createBookmark('admin', items.movie, { start: 60, name: `${PROBE} import X` });
    await ok('admin', 'DELETE', `/Bookmarks/${yDto.Id}`, { query: { deleteClip: 'true' } }, [204]);
    const shaBefore = sha(storeFile);
    const preOpBeforeImport = preOpSnapshots();
    const importOf = (query) => call('admin', 'POST', '/Bookmarks/Admin/Import', { raw: exported.bytes, query });
    const dryMerge = await importOf({ mode: 'Merge', dryRun: true });
    check('import dry run (Merge): the deleted bookmark would come back, nothing removed; nothing written, no snapshot',
        dryMerge.status === 200 && dryMerge.json.DryRun === true && dryMerge.json.Records.Added === 1 && dryMerge.json.Records.Removed === 0 && !dryMerge.json.SafetySnapshot
        && sha(storeFile) === shaBefore && preOpSnapshots().length === preOpBeforeImport.length && (await call('admin', 'GET', `/Bookmarks/${yDto.Id}`)).status === 404,
        dryMerge.json && dryMerge.json.Records);
    const merge = await importOf({ mode: 'Merge', dryRun: false });
    const yBack = await call('admin', 'GET', `/Bookmarks/${yDto.Id}`);
    check('Merge: the deleted bookmark back with its id and name, the newer one kept, after an "import" snapshot',
        merge.status === 200 && merge.json.Records.Added === 1 && merge.json.Records.Removed === 0 && /^bookmarks-import-/.test(merge.json.SafetySnapshot || '')
        && yBack.status === 200 && yBack.json.Name === `${PROBE} restored admin` && (await call('admin', 'GET', `/Bookmarks/${x.Id}`)).status === 200,
        { records: merge.json && merge.json.Records, snapshot: merge.json && merge.json.SafetySnapshot });
    const dryReplace = await importOf({ mode: 'Replace', dryRun: true });
    const replace = await importOf({ mode: 'Replace', dryRun: false });
    context.replaceSnapshot = replace.json && replace.json.SafetySnapshot;
    check('Replace (after its dry run): the bookmark made since the export removed, the bookmarks exactly the export\'s, after an "import" snapshot',
        dryReplace.status === 200 && dryReplace.json.Records.Removed === 1 && replace.status === 200 && replace.json.Records.Removed === 1
        && /^bookmarks-import-/.test(context.replaceSnapshot || '') && (await call('admin', 'GET', `/Bookmarks/${x.Id}`)).status === 404
        && idSet(readStore().Bookmarks) === idSet(e1.Data.Bookmarks),
        { dry: dryReplace.json && dryReplace.json.Records, applied: replace.json && replace.json.Records });
    const again = await importOf({ mode: 'Replace', dryRun: false });
    check('Replace again: nothing added, updated or removed',
        again.status === 200 && again.json.Records.Added === 0 && again.json.Records.Updated === 0 && again.json.Records.Removed === 0 && idSet(readStore().Bookmarks) === idSet(e1.Data.Bookmarks),
        again.json && again.json.Records);
    const byName = await importOf({ mode: 'Merge', dryRun: true, mapUsersBy: 'Name' });
    check('a dry run matching users by name: the sandbox\'s users found by name, nothing to add, update or remove',
        byName.status === 200 && byName.json.MapUsersBy === 'Name' && ['admin', 'viewer', 'viewer2'].every((who) => (byName.json.Users.Mapped || []).some((m) => guid(m.NewId || '') === ids[who] && m.Name === who))
        && byName.json.Records.Added === 0 && byName.json.Records.Updated === 0 && byName.json.Records.Removed === 0,
        byName.json && { users: byName.json.Users, records: byName.json.Records });

    // ---- 5. the daily snapshots rotate
    await setConfig({ SnapshotsToKeep: 2 });
    for (let i = 0; i < 3; i++) await runMaintenance(`snapshot rotation ${i + 1}`);
    const listed = await ok('admin', 'GET', '/Bookmarks/Admin/Snapshots');
    const dailyListed = (listed.Snapshots || []).filter((s) => s.Kind === 'Daily');
    const rotating = dailyListed.filter((s) => !s.Kept);
    check('SnapshotsToKeep 2 and three maintenance runs: two daily snapshots left (besides any kept from before an empty start); the list shows them',
        rotating.length === 2 && dailySnapshots().length === dailyListed.length && listed.SnapshotsToKeep === 2,
        { daily: dailyListed.map((s) => ({ Name: s.Name, Kept: s.Kept })), files: dailySnapshots().length });
    await setConfig({ SnapshotsToKeep: context.saved.SnapshotsToKeep });

    // ---- 6. Restore snapshot, running and by hand
    const restored = await call('admin', 'POST', '/Bookmarks/Admin/Snapshots/Restore', { query: { name: context.replaceSnapshot } });
    const xAfterRestore = await call('admin', 'GET', `/Bookmarks/${x.Id}`);
    check('Restore snapshot in the running server (the snapshot taken before Replace): the bookmark Replace removed is back, after a "restore" snapshot',
        restored.status === 200 && restored.json.RestoredFrom === context.replaceSnapshot && /^bookmarks-restore-/.test(restored.json.SafetySnapshot || '') && xAfterRestore.status === 200,
        restored.json && { RestoredFrom: restored.json.RestoredFrom, SafetySnapshot: restored.json.SafetySnapshot, Bookmarks: restored.json.Bookmarks, ClipJobsEnded: restored.json.ClipJobsEnded });
    const undo = await call('admin', 'POST', '/Bookmarks/Admin/Snapshots/Restore', { query: { name: restored.json && restored.json.SafetySnapshot } });
    check('the restore undone through its own snapshot: that bookmark gone again',
        undo.status === 200 && (await call('admin', 'GET', `/Bookmarks/${x.Id}`)).status === 404, undo.json && { RestoredFrom: undo.json.RestoredFrom });
    const z = await createBookmark('admin', items.movie, { start: 70, name: `${PROBE} manual Z` });
    const withZ = await runMaintenance('the snapshot for the manual restore');
    const zSnapshot = step(withZ, 'Snapshots') && step(withZ, 'Snapshots').Result && step(withZ, 'Snapshots').Result.Snapshot;
    await ok('admin', 'DELETE', `/Bookmarks/${z.Id}`, { query: { deleteClip: 'true' } }, [204]);
    const zFile = zSnapshot ? path.join(snapshotFolder, zSnapshot) : null;
    const zInSnapshot = !!zFile && fs.existsSync(zFile) && (readJson(zFile).Bookmarks || []).some((r) => guid(r.Id) === guid(z.Id));
    stopSandbox('manual restore');
    if (zFile && fs.existsSync(zFile)) fs.copyFileSync(zFile, storeFile);
    await startSandbox('manual restore');
    const zBack = await call('admin', 'GET', `/Bookmarks/${z.Id}`);
    const store6 = (await diagnostics()).Store;
    check('the manual procedure: stopped, the daily snapshot copied over bookmarks.json, started: the bookmark deleted since is back, the store writable',
        zInSnapshot && zBack.status === 200 && zBack.json.Name === `${PROBE} manual Z` && store6.ReadOnly === false && /^(Ok|Migrated)$/.test(store6.Status),
        { snapshot: zSnapshot, inSnapshot: zInSnapshot, status: zBack.status, store: { Status: store6.Status, Version: store6.Version } });

    // ---- 7. a season folder deleted
    const e01 = await createBookmark('viewer', items.s01e01, { start: 5, name: `${PROBE} season 1` });
    const e02 = await createBookmark('viewer', items.s02e01, { start: 5, name: `${PROBE} season 2` });
    holdOut(path.join('Shows', SERIES_FOLDER, 'Season 02'));
    await scan('Season 02 held out');
    const s2gone = await waitFor('the Season 02 bookmark detached', async () => { const r = await call('viewer', 'GET', `/Bookmarks/${e02.Id}`); return r.json && r.json.Detached ? r.json : null; }, 30);
    const s1still = (await call('viewer', 'GET', `/Bookmarks/${e01.Id}`)).json;
    check('Season 02\'s folder held out and scanned: its episode\'s bookmark is "Not in library" (detached by its season), the Season 01 one isn\'t',
        s2gone.Detached === true && guid(s2gone.ItemId) === guid(items.s02e01.Id) && s1still.Detached === false,
        { season2: { Detached: s2gone.Detached, DetachedUtc: (stored(e02.Id) || {}).DetachedUtc }, season1: { Detached: s1still.Detached } });
    undoMoves();
    await scan('Season 02 put back');
    const s2back = await waitFor('the Season 02 bookmark reattached', async () => { const r = await call('viewer', 'GET', `/Bookmarks/${e02.Id}`); return r.json && !r.json.Detached ? r.json : null; }, 30);
    check('put back and scanned: reattached to the same episode (Exact, not marked relinked)',
        guid(s2back.ItemId) === guid(items.s02e01.Id) && s2back.Relinked === false, { ItemId: s2back.ItemId, Relinked: s2back.Relinked });

    // ---- 8. retention and the archive
    const r1 = await createBookmark('admin', items.tagged, { start: 15, name: `${PROBE} retention` });
    holdOut(path.join('Movies', RETENTION_FOLDER));
    await scan('Tagged Movie held out');
    await waitFor('the retention bookmark detached', async () => { const s = stored(r1.Id); return s && s.DetachedUtc ? s : null; }, 30);
    evidence.detachedBeforeRetention = readStore().Bookmarks.filter((s) => s.DetachedUtc && !s.DeletedUtc).length;
    await setConfig({ DetachedRetentionDays: 1, DeveloperClockOffsetDays: 2 });
    context.retention = true;
    const archived = await runMaintenance('retention with the clock two days on');
    const archiveText = fs.existsSync(archiveFile) ? fs.readFileSync(archiveFile, 'utf8') : '';
    const archiveStep = ownerResult(step(archived, 'Archive'));
    check('retention 1 day with the developer clock 2 days on: maintenance moves the detached bookmark out of bookmarks.json into archive/bookmarks-archived.jsonl; the API no longer has it',
        !stored(r1.Id) && archiveText.includes(dashed(guid(r1.Id))) && archiveStep && archiveStep.Archived >= 1 && (await call('admin', 'GET', `/Bookmarks/${r1.Id}`)).status === 404,
        { step: archiveStep, archiveLines: archiveText.split('\n').filter(Boolean).length, detachedBefore: evidence.detachedBeforeRetention });
    const exported2 = await call('admin', 'GET', '/Bookmarks/Admin/Export');
    const e2 = exported2.status === 200 ? JSON.parse(exported2.text) : null;
    check('the export\'s Archive carries the archived bookmark, and its Data doesn\'t',
        e2 && e2.Archive.some((r) => guid(r.Id) === guid(r1.Id) && r.Name === `${PROBE} retention`) && !e2.Data.Bookmarks.some((r) => guid(r.Id) === guid(r1.Id)),
        e2 && { archive: e2.Archive.length, bookmarks: e2.Data.Bookmarks.length });
    undoMoves();
    await scan('Tagged Movie put back');
    const back = await runMaintenance('reattach from the archive');
    await setConfig({ DetachedRetentionDays: 0, DeveloperClockOffsetDays: 0 });
    context.retention = false;
    const reattachStep = ownerResult(step(back, 'Reattach'));
    const r1Back = await call('admin', 'GET', `/Bookmarks/${r1.Id}`);
    const exported3 = await call('admin', 'GET', '/Bookmarks/Admin/Export');
    const e3 = exported3.status === 200 ? JSON.parse(exported3.text) : null;
    check('its movie back: the next maintenance moves it back into bookmarks.json, attached to the same movie (Exact), and out of the archive',
        r1Back.status === 200 && r1Back.json.Detached === false && r1Back.json.Relinked === false && guid(r1Back.json.ItemId) === guid(items.tagged.Id)
        && stored(r1.Id) && !stored(r1.Id).DetachedUtc && reattachStep && reattachStep.Relinked >= 1 && e3 && !e3.Archive.some((r) => guid(r.Id) === guid(r1.Id)),
        { step: reattachStep, status: r1Back.status, archive: e3 && e3.Archive.length });
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { users: [], saved: null, backup: null, retention: false };
    const startLog = logPosition();
    if (loadMoves().length > 0) {
        undoMoves();
        await scan('left-over changes of an earlier run undone');
        evidence.undidLeftovers = true;
    }
    let failure = null;
    try {
        await run(context);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        try {
            if (!running) await startSandbox('after a failure');
            if (context.saved) {
                const now = await getConfig();
                await ok('admin', 'POST', `/Plugins/${bookmarksGuid}/Configuration`, { body: { ...now, ...context.saved } }, [204]);
            }
            for (const name of [...THROWAWAY]) await deleteUserNamed(name);
            const changed = loadMoves().length;
            undoMoves();
            if (changed > 0) await scan('clean-up: media put back');
        } catch (error) {
            check('clean-up: settings, users and media put back', false, String(error && error.stack || error));
        }
        // The log is read before the restore's restart (whose start writes nothing of the run).
        const text = logSince(startLog);
        const lines = text.split(/\r?\n/);
        const errors = lines.filter((l) => /\[ERR\]|\[FTL\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        check('no error of Bookmarks or the kit in the server log during the run', errors.length === 0, errors.slice(0, 5));
        const named = lines.filter((l) => /\[(INF|WRN|ERR|FTL)\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l) && l.includes(PROBE));
        check('no line of Bookmarks or the kit at Information or above names a bookmark', named.length === 0, named.slice(0, 3));
        try {
            if (context.backup) {
                stopSandbox('clean-up: the data put back');
                // A throwaway user's clip folder (inside the sandbox's default clip folder) goes too.
                for (const name of THROWAWAY) {
                    const folder = path.join(dataFolder, 'clips', name);
                    if (fs.existsSync(folder)) fs.rmSync(assertInside(folder, dataFolder), { recursive: true, force: true });
                }
                restoreBackup();
                const same = sha(storeFile) === context.backup.sha;
                await startSandbox('clean-up: the data put back');
                const store = (await diagnostics()).Store;
                check('clean-up: Bookmarks\' data as it was before the run (bookmarks.json byte for byte, the snapshots, the archive, kit-users.json), the store writable at schema 2',
                    same && store.ReadOnly === false && store.Version === 2, { sha: same, store: { Status: store.Status, Version: store.Version } });
            }
        } catch (error) {
            check('clean-up: Bookmarks\' data put back', false, String(error && error.stack || error));
        }
        if (fs.existsSync(heldRoot) && fs.readdirSync(heldRoot, { recursive: true }).every((f) => fs.statSync(path.join(heldRoot, f)).isDirectory())) fs.rmSync(heldRoot, { recursive: true });
    }
    const pass = !failure && checks.every((c) => c.pass);
    const result = { pass, passed: checks.filter((c) => c.pass).length, total: checks.length, checks, evidence };
    const out = JSON.stringify(result, null, 2);
    if (opts.out) {
        fs.mkdirSync(path.dirname(path.resolve(opts.out)), { recursive: true });
        fs.writeFileSync(opts.out, out);
    }
    console.log(out);
    process.exit(pass ? 0 : 1);
}

await main();
