#!/usr/bin/env node
// The kit's record lifecycle and library reads against a real library in a running sandbox, through
// Bookmarks' developer library demo (/Bookmarks/Admin/LibraryDemo, behind the setting EnableLibraryDemo, which this probe
// turns on and restores), Bookmarks' Diagnostics and its daily "Bookmarks: Maintenance" task, and Jellyfin's own API as the
// independent side. Node 22 built-ins only. Loopback only, never port 8096. The users' tokens come from credentials.json
// and are never printed.
//
//   node scripts/library-demo-probe.mjs --sandbox default [--timeout <scan seconds>] [--skip-restart] [--out result.json]
//
// It changes the shared sandbox media and puts every change back: it renames folders only inside .sandbox/media and its
// hold folder .sandbox/<name>/library-demo-probe/held (same volume, plain renames), records each move in
// .sandbox/<name>/library-demo-probe/moves.json before making it, and undoes the moves in reverse order at the end, on
// failure, and at the start of the next run after a run was killed (then it scans once). For the fresh-read check it adds
// one folder, "Season 03" of the test series (a copy of its first episode), recorded in created.json first and removed the
// same way. It restarts the sandbox once (Stop-Sandbox.ps1, dev-server.ps1 -Background -NoBuild) for the startup sweep,
// unless --skip-restart. It changes the test user viewer's user data on the test series and its first season, viewer2's
// library access, and merges versions of seven sandbox movies with Jellyfin's own "Merge versions"; each is put back at
// the end. The demo's records and archive are emptied at the start and at the end.
//
// Checks (the kit's identity, detach and retention checks with a demo consumer, and the user-data fresh read):
//   1. the demo is off by default and admin-only; "Bookmarks: Maintenance" is a daily task at 03:10; Diagnostics has the
//      Verifier, Detached and Maintenance entries (none with an owner or step that can't be created)
//      and the startup sweep has run;
//   2. a movie removed and put back: its record detaches (Removed) and reattaches to the same id (Exact, media source kept);
//   3. the two-season series folder renamed: the episode records detach (container ids; a record without them through the
//      verify sweep of the removed folder) and relink by the series' TVDb id plus season and episode numbers (Strong, the
//      media source cleared) in the same scan, and the series record by its TVDb id; then Season 02 held out: its records
//      detach and Season 01's stay; renamed back: every record ends on its original item;
//   4. the edition pair: the 60 s copy's record stays detached as Ambiguous (Bookmarks' runtime check), in Diagnostics too;
//      two copies of equal runtime: the record relinks to the other (Strong, the media source cleared, marked);
//   5. lost events: a movie removed while the demo was off; after a restart the startup sweep detaches its record;
//   6. the daily maintenance task's sweep detaches a record whose item went while the demo was off;
//   7. retention: DetachedRetentionDays 1 and the developer clock 2 days on: maintenance moves the
//      detached record to archive/library-demo-archived.jsonl, it is in the export's Archive, and it moves back, attached,
//      when its movie returns;
//   8. Purge deletes a detached record after a pre-operation snapshot;
//   9. the fresh read: an episode refreshed leaves core's cached Season (its parent) without its user-data snapshot,
//      and a season refreshed the cached Series, so GetUserData (and Jellyfin's own GET /UserItems/{id}/UserData) answer
//      blank; FreshUserData returns the stored favourite, played, play count and resume position through
//      GetItemList(ItemIds) (and through IItemRepository.RetrieveItem), and core's cached instance reads right again
//      afterwards; an episode added in a new season and scanned is recorded as evidence;
//  10. LibraryAccess for viewer2 without Movies 2: the visible libraries, the factory's query (TopParentIds, no grouping, no
//      HasOwnerId) finding exactly the movies Jellyfin's /Items gives viewer2, no owned item; a Movies 2 id resolves to none;
//  11. VersionSets: a chain built by Jellyfin's own "Merge versions" (two pairs merged, then their primaries) is one set with
//      the folded member chained; split, four sets; a set merged across Movies and Movies 2 with its primary in Movies 2 is
//      represented for viewer2 by its Movies member and for the admin by its primary; a local alternate version (owned) is
//      never eligible;
//  12. no error of Bookmarks or the kit in the server log; clean-up.
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const DEMO = '/Bookmarks/Admin/LibraryDemo';
const TEN_MINUTES = 6_000_000_000;
const SERIES_FOLDER = 'Test Series (2020) [tvdbid-900401]';
const RENAMED_FOLDER = 'Test Series Renamed (2020) [tvdbid-900401]';
const NEW_SEASON = 'Season 03';

function usage() {
    return 'usage: node scripts/library-demo-probe.mjs --sandbox <name> [--timeout <scan seconds>] [--skip-restart] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, timeout: 600, out: null, skipRestart: false };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--skip-restart') o.skipRestart = true;
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
const workRoot = path.join(sandboxRoot, 'library-demo-probe');
const heldRoot = path.join(workRoot, 'held');
const movesFile = path.join(workRoot, 'moves.json');
const createdFile = path.join(workRoot, 'created.json');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
let state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
if (!logDir.startsWith(sandboxRoot + path.sep)) throw new Error(`refusing to read the log folder ${logDir}: not in the sandbox`);
const dataFolder = path.join(sandboxRoot, 'data', 'data', 'bookmarks');
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer', 'viewer2']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}
const bookmarksGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8')) || [])[1];
if (!bookmarksGuid) throw new Error('no guid in plugins/Bookmarks/build.yaml');

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error(`refusing to work in ${p}`);
    return full;
}
assertInside(dataFolder, sandboxRoot);

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, scans: [], restarts: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// Jellyfin writes GUIDs without dashes ("N"); ASP.NET Core's JSON writes them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (Array.isArray(v)) for (const item of v) url.searchParams.append(k, String(item));
        else if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="library-demo-probe", Device="sandbox", DeviceId="library-demo-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, type: res.headers.get('content-type') || '', json, text };
}

async function demo(method, p, options) {
    const r = await call('admin', method, DEMO + p, options);
    if (r.status !== 200 && r.status !== 204) throw new Error(`${method} ${p}: ${r.status} ${r.text.slice(0, 300)}`);
    return r.json;
}

async function records() {
    return demo('GET', '/Records');
}

async function recordOf(id) {
    return (await records()).Records.find((r) => guid(r.Id) === guid(id)) || null;
}

async function addRecord(itemId, note, extra = {}) {
    return demo('POST', '/Records', { query: { itemId, note, ...extra } });
}

async function waitFor(label, predicate, seconds = 30) {
    const until = Date.now() + seconds * 1000;
    let last;
    for (;;) {
        last = await predicate();
        if (last) return last;
        if (Date.now() > until) throw new Error(`timed out waiting for: ${label}`);
        await delay(500);
    }
}

async function getConfig() {
    const r = await call('admin', 'GET', `/Plugins/${bookmarksGuid}/Configuration`);
    if (r.status !== 200 || !r.json) throw new Error(`GET configuration: ${r.status}`);
    return r.json;
}

async function setConfig(changes) {
    const current = await getConfig();
    const posted = await call('admin', 'POST', `/Plugins/${bookmarksGuid}/Configuration`, { body: { ...current, ...changes } });
    if (posted.status !== 204) throw new Error(`POST configuration: ${posted.status}`);
}

async function diagnostics() {
    const r = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    if (r.status !== 200 || !r.json) throw new Error(`GET /Bookmarks/Admin/Diagnostics: ${r.status}`);
    return r.json;
}

// ------------------------------------------------------------------------------------------ media (undoable)

function loadMoves() {
    return fs.existsSync(movesFile) ? readJson(movesFile) : [];
}

function saveMoves(moves) {
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(movesFile, JSON.stringify(moves, null, 2));
}

function move(from, to) {
    const src = path.resolve(from);
    const dst = path.resolve(to);
    for (const p of [src, dst]) {
        if (!(p.startsWith(mediaRoot + path.sep) || p.startsWith(heldRoot + path.sep))) throw new Error(`refusing to move ${p}: not in the sandbox media or the hold folder`);
    }
    assertInside(src, path.join(repoRoot, '.sandbox'));
    assertInside(dst, path.join(repoRoot, '.sandbox'));
    if (!fs.existsSync(src)) throw new Error(`${src} doesn't exist`);
    if (fs.existsSync(dst)) throw new Error(`${dst} exists already`);
    const moves = loadMoves();
    moves.push({ from: src, to: dst });
    saveMoves(moves);
    fs.mkdirSync(path.dirname(dst), { recursive: true });
    fs.renameSync(src, dst);
}

function holdOut(relative) {
    move(path.join(mediaRoot, relative), path.join(heldRoot, relative));
}

// Undoes recorded moves, newest first; each entry is removed once undone.
function undoMoves(untilCount = 0) {
    let moves = loadMoves();
    while (moves.length > untilCount) {
        const { from, to } = moves[moves.length - 1];
        if (fs.existsSync(to) && !fs.existsSync(from)) {
            fs.mkdirSync(path.dirname(from), { recursive: true });
            fs.renameSync(to, from);
        } else if (fs.existsSync(to)) {
            throw new Error(`cannot undo ${to} → ${from}: both exist; fix by hand`);
        }
        moves = moves.slice(0, -1);
        saveMoves(moves);
    }
}

// The one folder the probe creates: Season 03 of the test series, recorded before it is made; no file is overwritten.
function newSeasonFolder() {
    return path.join(mediaRoot, 'Shows', SERIES_FOLDER, NEW_SEASON);
}

function createNewSeason() {
    const dir = newSeasonFolder();
    const source = path.join(mediaRoot, 'Shows', SERIES_FOLDER, 'Season 01', 'Test Series (2020) S01E01.mkv');
    if (!fs.existsSync(source)) throw new Error(`no source file ${source}`);
    if (fs.existsSync(dir)) throw new Error(`${dir} exists already`);
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(createdFile, JSON.stringify([dir], null, 2));
    fs.mkdirSync(dir);
    fs.copyFileSync(source, path.join(dir, 'Test Series (2020) S03E01.mkv'), fs.constants.COPYFILE_EXCL);
}

function removeCreated() {
    if (!fs.existsSync(createdFile)) return 0;
    const created = readJson(createdFile);
    for (const dir of created) {
        if (path.resolve(dir) !== newSeasonFolder()) throw new Error(`refusing to remove ${dir}: not the probe's folder`);
        if (fs.existsSync(dir)) fs.rmSync(dir, { recursive: true });
    }
    fs.rmSync(createdFile);
    return created.length;
}

// ------------------------------------------------------------------------------------------ library and tasks

async function scan(label) {
    const started = Date.now();
    const r = await call('admin', 'POST', '/Library/Refresh');
    if (r.status !== 204) throw new Error(`POST /Library/Refresh: ${r.status}`);
    await delay(1500);
    for (;;) {
        const tasks = await call('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } });
        const task = (tasks.json || []).find((t) => t.Key === 'RefreshLibrary');
        const last = task && task.LastExecutionResult;
        if (task && task.State === 'Idle' && last && Date.parse(last.EndTimeUtc) >= started - 1000) {
            evidence.scans.push({ label, seconds: Math.round((Date.now() - started) / 100) / 10, status: last.Status });
            if (last.Status !== 'Completed') throw new Error(`the scan "${label}" ended ${last.Status}`);
            // Let the monitor see the scan end and send what came after the full pass (within about a second).
            await delay(2500);
            return;
        }
        if (Date.now() - started > opts.timeout * 1000) throw new Error(`the scan "${label}" did not finish within ${opts.timeout} s`);
        await delay(500);
    }
}

async function maintenanceTask() {
    const tasks = await call('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } });
    return (tasks.json || []).find((t) => t.Key === 'BookmarksMaintenance') || null;
}

// Starts "Bookmarks: Maintenance" as the dashboard's Run button does, and waits for it to end.
async function runMaintenance(label) {
    const task = await maintenanceTask();
    if (!task) throw new Error('no BookmarksMaintenance task');
    const started = Date.now();
    const r = await call('admin', 'POST', `/ScheduledTasks/Running/${task.Id}`);
    if (r.status !== 204) throw new Error(`POST /ScheduledTasks/Running: ${r.status}`);
    await delay(500);
    for (;;) {
        const now = await maintenanceTask();
        const last = now && now.LastExecutionResult;
        if (now && now.State === 'Idle' && last && Date.parse(last.EndTimeUtc) >= started - 1000) {
            evidence.scans.push({ label: `maintenance: ${label}`, seconds: Math.round((Date.now() - started) / 100) / 10, status: last.Status });
            if (last.Status !== 'Completed') throw new Error(`the maintenance "${label}" ended ${last.Status}`);
            return (await diagnostics()).Maintenance.LastRun;
        }
        if (Date.now() - started > 120000) throw new Error(`the maintenance "${label}" did not finish`);
        await delay(500);
    }
}

async function items(types) {
    const r = await call('admin', 'GET', '/Items', {
        query: { userId: users.admin.id, Recursive: 'true', IncludeItemTypes: types, Fields: 'Path,ProviderIds', EnableImages: 'false', EnableUserData: 'false' }
    });
    if (r.status !== 200) throw new Error(`GET /Items: ${r.status}`);
    return r.json.Items;
}

async function exists(id) {
    return (await call('admin', 'GET', `/Items/${id}`, { query: { userId: users.admin.id } })).status === 200;
}

function inFolder(item, folder) {
    return typeof item.Path === 'string' && item.Path.split(/[\\/]/).includes(folder);
}

function one(list, predicate, what) {
    const found = list.filter(predicate);
    if (found.length !== 1) throw new Error(`expected one ${what}, found ${found.length}; run Initialize-Sandbox.ps1 on fresh media`);
    return found[0];
}

// The newest log file and its size, then the lines written since.
function logPosition() {
    const files = fs.readdirSync(logDir).filter((f) => /^log_\d{8}\.log$/.test(f)).sort();
    const file = files[files.length - 1];
    return { file, size: file ? fs.statSync(path.join(logDir, file)).size : 0 };
}

function logSince(position) {
    const files = fs.readdirSync(logDir).filter((f) => /^log_\d{8}\.log$/.test(f)).sort().filter((f) => !position.file || f >= position.file);
    return files.map((f) => {
        const bytes = fs.readFileSync(path.join(logDir, f));
        return (f === position.file ? bytes.subarray(position.size) : bytes).toString('utf8');
    }).join('');
}

// A needle is a string or a regular expression; Serilog's file sink quotes string arguments ("library-demo").
const lineIndex = (text, needle) => text.split('\n').findIndex((l) => (needle instanceof RegExp ? needle.test(l) : l.includes(needle)));

// ------------------------------------------------------------------------------------------ sandbox control

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

async function restartSandbox(label) {
    evidence.restarts.push({ label, step: 'stop', seconds: runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox]) });
    evidence.restarts.push({ label, step: 'start', seconds: runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild']) });
    state = readJson(path.join(sandboxRoot, 'sandbox.json'));
    if (Number(state.Port) !== port) throw new Error(`the sandbox came back on port ${state.Port}`);
    for (let i = 0; i < 60; i++) {
        const r = await call('admin', 'GET', '/Bookmarks/Capabilities').catch(() => null);
        if (r && r.status === 200) return;
        await delay(1000);
    }
    throw new Error('Bookmarks did not answer after the start');
}

// ------------------------------------------------------------------------------------------ the run

const describeRecord = (r) => r && ({ Name: r.Name, Kind: r.Kind, S: r.Season, E: r.Episode, ItemId: guid(r.ItemId), DetachedBy: r.DetachedBy, Detached: !!r.DetachedUtc,
    RelinkStrength: r.RelinkStrength, Relinked: !!r.RelinkedUtc, MediaSourceId: r.MediaSourceId, LastOutcome: r.LastOutcome });

async function userData(itemId, userId, read, path) {
    return demo('GET', `/UserData/${itemId}`, { query: { userId, read, path } });
}

async function nativeUserData(itemId) {
    const r = await call('viewer', 'GET', `/UserItems/${itemId}/UserData`);
    if (r.status !== 200) throw new Error(`GET /UserItems/${itemId}/UserData: ${r.status}`);
    return r.json;
}

async function setUserData(itemId, data) {
    const r = await call('viewer', 'POST', `/UserItems/${itemId}/UserData`, { body: data });
    if (r.status !== 200) throw new Error(`POST /UserItems/${itemId}/UserData: ${r.status}`);
}

const stored = (d) => ({ IsFavorite: d.IsFavorite, Played: d.Played, PlayCount: d.PlayCount, PlaybackPositionTicks: d.PlaybackPositionTicks });

async function run(context) {
    // ---- 1. the demo, the task, Diagnostics
    const off = await call('admin', 'GET', `${DEMO}/Records`);
    context.saved = await getConfig();
    if (off.status !== 200) {
        check('the demo is off by default: 403 with the kit\'s feature-disabled type', off.status === 403 && off.json && off.json.type === 'urn:jellyfin-plugin-kit:feature-disabled',
            { status: off.status, type: off.json && off.json.type });
    }
    const viewer = await call('viewer', 'GET', `${DEMO}/Records`);
    check('viewer: the demo is admin-only (403)', viewer.status === 403, { status: viewer.status });
    await setConfig({ EnableLibraryDemo: true, DetachedRetentionDays: 0, DeveloperClockOffsetDays: 0 });
    await demo('DELETE', '/Records');
    await demo('POST', '/Purge', { query: { includeArchive: 'true' } });

    const task = await maintenanceTask();
    const trigger = task && (task.Triggers || [])[0];
    check('"Bookmarks: Maintenance" is a daily task at 03:10 in the Bookmarks category', task && task.Name === 'Bookmarks: Maintenance' && task.Category === 'Bookmarks'
        && task.Triggers.length === 1 && trigger.Type === 'DailyTrigger' && trigger.TimeOfDayTicks === 3 * 36_000_000_000 + 10 * 600_000_000,
        task && { Name: task.Name, Category: task.Category, Triggers: task.Triggers });
    const diag = await diagnostics();
    check('Diagnostics: Verifier (startup sweep done, the demo as its owner, every owner created), Detached and Maintenance entries (every step created); the demo is a library-change consumer',
        diag.Verifier && diag.Verifier.StartupSweepDone === true && (diag.Verifier.Owners || []).includes('library-demo') && diag.Detached && diag.Maintenance
        && Array.isArray(diag.Verifier.Unavailable) && diag.Verifier.Unavailable.length === 0
        && Array.isArray(diag.Maintenance.Unavailable) && diag.Maintenance.Unavailable.length === 0
        && (diag.LibraryChanges.Consumers || []).some((c) => c.Name === 'LibraryDemo'),
        { Verifier: diag.Verifier && { StartupSweepDone: diag.Verifier.StartupSweepDone, Owners: diag.Verifier.Owners, Sweeps: diag.Verifier.Sweeps, Unavailable: diag.Verifier.Unavailable },
            Maintenance: diag.Maintenance && { Steps: diag.Maintenance.Steps, Unavailable: diag.Maintenance.Unavailable } });

    const movies = await items('Movie');
    const movie = (folder, library = 'Movies') => one(movies, (m) => inFolder(m, folder) && m.Path.startsWith(path.join(mediaRoot, library) + path.sep), folder);
    const tagged = movie('Tagged Movie (2012) [tmdbid-900001]');
    const edition60 = movie('Edition Movie (2001) [tmdbid-900301]');
    const remake1999 = movie('Same Id Remake (1999) [tmdbid-900201]');
    const remake2005 = movie('Same Id Remake (2005) [tmdbid-900201]');
    const testPattern = movie('Test Pattern (2020)');
    const longTest = movie('Long Test Movie (2021)');
    const bladeRunner = movie('Blade Runner (1982)');
    const amelie = one(movies, (m) => m.Path.startsWith(path.join(mediaRoot, 'Movies') + path.sep) && /Am.lie \(2001\)/.test(m.Path), 'Amélie in Movies');
    const story1990 = movie('Remake Story (1990) [tmdbid-900101]');
    const story2015 = movie('Remake Story (2015) [tmdbid-900102]');
    const bigBuck = movie('Big Buck Test (2008)');
    const bigBuck2 = movie('Big Buck Test (2008)', 'Movies 2');
    const amelie2 = movie('Amelie (2001)', 'Movies 2');
    const versions = one(movies, (m) => inFolder(m, 'Versions Movie (2019)'), 'Versions Movie');
    const series = one(await items('Series'), (s) => inFolder(s, SERIES_FOLDER), 'Test Series');
    const seasons = (await items('Season')).filter((s) => inFolder(s, SERIES_FOLDER)).sort((a, b) => a.IndexNumber - b.IndexNumber);
    const episodes = (await items('Episode')).filter((e) => inFolder(e, SERIES_FOLDER))
        .sort((a, b) => (a.ParentIndexNumber - b.ParentIndexNumber) || (a.IndexNumber - b.IndexNumber));
    if (seasons.length !== 2 || episodes.length !== 4) throw new Error('the test series needs two seasons of two episodes; run Initialize-Sandbox.ps1 on fresh media');

    // ---- 2. a movie removed and put back
    const onTagged = await addRecord(tagged.Id, 'demo remove and re-add');
    let moves = loadMoves().length;
    holdOut(path.join('Movies', 'Tagged Movie (2012) [tmdbid-900001]'));
    await scan('a movie held out');
    const taggedGone = await waitFor('the record of the removed movie detached', async () => { const r = await recordOf(onTagged.Id); return r && r.DetachedUtc ? r : null; });
    const detachedDiag = (await diagnostics()).Detached.Owners.find((o) => o.Owner === 'library-demo');
    check('removed: the movie\'s record is detached by its ItemRemoved (Removed), and Diagnostics counts it', taggedGone.DetachedBy === 'Removed' && detachedDiag && detachedDiag.Count === 1,
        { record: describeRecord(taggedGone), detached: detachedDiag });
    undoMoves(moves);
    await scan('the movie put back');
    const taggedBack = await waitFor('the record reattached', async () => { const r = await recordOf(onTagged.Id); return r && !r.DetachedUtc ? r : null; });
    check('put back: the record reattaches to the same id (Exact: the media source kept, no relink mark)', guid(taggedBack.ItemId) === guid(tagged.Id)
        && guid(taggedBack.MediaSourceId) === guid(tagged.Id) && !taggedBack.RelinkStrength && !taggedBack.RelinkedUtc, describeRecord(taggedBack));

    // ---- 3. the series folder renamed, Season 02 held out, renamed back
    const onEpisodes = [];
    for (const e of episodes) onEpisodes.push(await addRecord(e.Id, `demo S${e.ParentIndexNumber}E${e.IndexNumber}`));
    const legacy = await addRecord(episodes[3].Id, 'demo without container ids', { withoutContainerIds: 'true' });
    const onSeries = await addRecord(series.Id, 'demo series');
    check('the legacy record has no container ids', !legacy.SeriesId && !legacy.SeasonId && onEpisodes[3].SeriesId, { legacy: { SeriesId: legacy.SeriesId, SeasonId: legacy.SeasonId } });
    const seriesMoves = loadMoves().length;
    const renameLog = logPosition();
    move(path.join(mediaRoot, 'Shows', SERIES_FOLDER), path.join(mediaRoot, 'Shows', RENAMED_FOLDER));
    await scan('series folder renamed');
    const newEpisodes = (await items('Episode')).filter((e) => inFolder(e, RENAMED_FOLDER))
        .sort((a, b) => (a.ParentIndexNumber - b.ParentIndexNumber) || (a.IndexNumber - b.IndexNumber));
    const newSeries = one(await items('Series'), (s) => inFolder(s, RENAMED_FOLDER), 'the renamed series');
    const expected = new Map([...onEpisodes.map((r, i) => [guid(r.Id), guid(newEpisodes[i].Id)]), [guid(legacy.Id), guid(newEpisodes[3].Id)], [guid(onSeries.Id), guid(newSeries.Id)]]);
    const relinked = await waitFor('every series record relinked to the renamed folder', async () => {
        const list = (await records()).Records.filter((r) => expected.has(guid(r.Id)));
        return list.length === expected.size && list.every((r) => !r.DetachedUtc && expected.get(guid(r.Id)) === guid(r.ItemId)) ? list : null;
    }, 30);
    const renameText = logSince(renameLog);
    const detachLine = lineIndex(renameText, 'Library demo: 1 records detached whose item was removed, 4 whose series, season or album was');
    // Since Bookmarks 1.1 the sweep covers the bookmarks too, so bookmarks on the series' episodes count in its line.
    const sweepLine = lineIndex(renameText, /Verify sweep \("?RemovedFolders"?\): [1-9]\d* records detached/);
    // The relinks follow the sweep, in the same full pass or in batches right after it (the log's lines are summed).
    const renameLines = renameText.split('\n');
    const relinkLineUnquoted = lineIndex(renameText, /detached records of "?library-demo"? were relinked/);
    const relinkedCount = renameLines.slice(Math.max(0, sweepLine)).map((l) => /(\d+) detached records of "?library-demo"? were relinked/.exec(l)).filter(Boolean).reduce((n, x) => n + Number(x[1]), 0);
    check('renamed, in one scan: the series record detached by its own event and the 4 episode records by their container ids, then the record without container ids by the verify sweep of the removed folder, then all 6 relinked',
        detachLine >= 0 && sweepLine > detachLine && relinkLineUnquoted > sweepLine && relinkedCount === 6,
        { detachLine, sweepLine, relinkLine: relinkLineUnquoted, relinkedCount, lines: renameLines.filter((l) => /Library demo:|Verify sweep|were relinked/.test(l)).map((l) => l.replace(/^\[[^\]]*\] /, '').trim()) });
    check('renamed: each episode record relinked by the series TVDb id plus season and episode (Strong: the media source cleared, marked), the series record by its TVDb id',
        relinked.every((r) => r.RelinkStrength === 'Strong' && !r.MediaSourceId && r.RelinkedUtc), relinked.map(describeRecord));
    const verifier = (await diagnostics()).Verifier;
    evidence.renameSweep = (verifier.Recent || []).find((s) => (s.Reasons || []).includes('RemovedFolders'));

    const seasonMoves = loadMoves().length;
    move(path.join(mediaRoot, 'Shows', RENAMED_FOLDER, 'Season 02'), path.join(heldRoot, 'Shows', 'Season 02'));
    await scan('Season 02 held out');
    const season2Ids = new Set([guid(onEpisodes[2].Id), guid(onEpisodes[3].Id), guid(legacy.Id)]);
    const afterSeason = await waitFor('Season 02\'s records detached', async () => {
        const list = (await records()).Records.filter((r) => expected.has(guid(r.Id)));
        return list.filter((r) => season2Ids.has(guid(r.Id))).every((r) => r.DetachedUtc) ? list : null;
    });
    check('Season 02 deleted: its three records detach (container ids), Season 01\'s and the series record stay attached',
        afterSeason.filter((r) => season2Ids.has(guid(r.Id))).every((r) => r.DetachedBy === 'Container') && afterSeason.filter((r) => !season2Ids.has(guid(r.Id))).every((r) => !r.DetachedUtc),
        afterSeason.map(describeRecord));
    undoMoves(seriesMoves);
    await scan('series folder renamed back, Season 02 put back');
    const original = new Map([...onEpisodes.map((r, i) => [guid(r.Id), guid(episodes[i].Id)]), [guid(legacy.Id), guid(episodes[3].Id)], [guid(onSeries.Id), guid(series.Id)]]);
    const home = await waitFor('every series record back on its original item', async () => {
        const list = (await records()).Records.filter((r) => original.has(guid(r.Id)));
        return list.every((r) => !r.DetachedUtc && original.get(guid(r.Id)) === guid(r.ItemId)) ? list : null;
    }, 30);
    check('renamed back: every record ends attached to its original item', home.length === 6, home.map(describeRecord));
    void seasonMoves;

    // ---- 4. the edition pair and two copies of equal runtime
    const onEdition = await addRecord(edition60.Id, 'demo 60 s cut');
    const onRemake = await addRecord(remake1999.Id, 'demo remake 1999');
    moves = loadMoves().length;
    holdOut(path.join('Movies', 'Edition Movie (2001) [tmdbid-900301]'));
    holdOut(path.join('Movies', 'Same Id Remake (1999) [tmdbid-900201]'));
    await scan('the 60 s cut and the 1999 copy held out');
    await waitFor('both records detached or relinked', async () => { const a = await recordOf(onEdition.Id); return a && a.DetachedUtc ? a : null; });
    await demo('POST', '/Resolve');
    const edition = await recordOf(onEdition.Id);
    const remake = await recordOf(onRemake.Id);
    const ambiguous = (await diagnostics()).Detached.Owners.find((o) => o.Owner === 'library-demo');
    check('edition pair: the 60 s cut\'s record stays detached, Ambiguous (the 90 s cut fails Bookmarks\' runtime check), and Diagnostics counts it',
        edition.DetachedUtc && edition.LastOutcome === 'Ambiguous: FailedValidation' && ambiguous.Ambiguous === 1, { record: describeRecord(edition), detached: ambiguous });
    check('equal runtime: the 1999 copy\'s record relinks to the 2005 copy (Strong: the media source cleared, "relinked, check positions")',
        !remake.DetachedUtc && guid(remake.ItemId) === guid(remake2005.Id) && remake.RelinkStrength === 'Strong' && !remake.MediaSourceId && remake.RelinkedUtc, describeRecord(remake));
    undoMoves(moves);
    await scan('both put back');
    const editionBack = await waitFor('the 60 s record reattached', async () => { const r = await recordOf(onEdition.Id); return r && !r.DetachedUtc ? r : null; });
    check('put back: the 60 s cut\'s record reattaches to its own item (Exact)', guid(editionBack.ItemId) === guid(edition60.Id) && guid(editionBack.MediaSourceId) === guid(edition60.Id), describeRecord(editionBack));

    // ---- 5. lost events: the startup sweep
    const onPattern = await addRecord(testPattern.Id, 'demo startup sweep');
    await setConfig({ EnableLibraryDemo: false });
    moves = loadMoves().length;
    holdOut(path.join('Movies', 'Test Pattern (2020)'));
    await scan('Test Pattern held out while the demo is off');
    await setConfig({ EnableLibraryDemo: true });
    const stale = await recordOf(onPattern.Id);
    check('the event was lost: the demo was off, so the record is still attached to an item the library no longer has', !stale.DetachedUtc && !(await exists(testPattern.Id)), describeRecord(stale));
    if (opts.skipRestart) {
        check('startup sweep: skipped (--skip-restart)', true, null);
    } else {
        const restartLog = logPosition();
        await restartSandbox('the startup sweep');
        const swept = await waitFor('the startup sweep detached the record', async () => {
            const r = await records();
            const record = r.Records.find((x) => guid(x.Id) === guid(onPattern.Id));
            return r.Verifier.StartupSweepDone && record && record.DetachedUtc ? { record, verifier: r.Verifier } : null;
        }, 60);
        const startup = (swept.verifier.Recent || []).find((s) => (s.Reasons || []).includes('Startup'));
        check('after a restart the startup sweep (after the gate) detaches it (Sweep)', swept.record.DetachedBy === 'Sweep' && startup && startup.Detached === 1,
            { record: describeRecord(swept.record), startup });
        const text = logSince(restartLog);
        check('the server log says so: "Verify sweep (Startup): 1 records detached"', lineIndex(text, /Verify sweep \("?Startup"?\): 1 records detached/) >= 0,
            text.split('\n').filter((l) => l.includes('Verify sweep')).map((l) => l.replace(/^\[[^\]]*\] /, '').trim()));
    }
    undoMoves(moves);
    await scan('Test Pattern put back');
    await waitFor('the Test Pattern record reattached', async () => { const r = await recordOf(onPattern.Id); return r && !r.DetachedUtc ? r : null; });

    // ---- 6. the daily maintenance's sweep
    const onLong = await addRecord(longTest.Id, 'demo daily sweep');
    await setConfig({ EnableLibraryDemo: false });
    moves = loadMoves().length;
    holdOut(path.join('Movies', 'Long Test Movie (2021)'));
    await scan('Long Test Movie held out while the demo is off');
    await setConfig({ EnableLibraryDemo: true });
    const daily = await runMaintenance('daily sweep');
    const longGone = await recordOf(onLong.Id);
    const sweepStep = (daily.Steps || []).find((s) => s.Name === 'VerifySweep');
    check('the daily "Bookmarks: Maintenance" runs its steps in order, none failing, and its verify sweep detaches the record (Sweep)',
        longGone.DetachedBy === 'Sweep' && daily.Failed === 0 && sweepStep && sweepStep.Result.Detached === 1 && sweepStep.Result.Reasons.includes('Daily')
        && (daily.Steps || []).map((s) => s.Name).join() === 'Snapshots,Archive,Users,VerifySweep,ResolveDetached,Reattach,ClipFiles',
        { record: describeRecord(longGone), steps: (daily.Steps || []).map((s) => ({ Name: s.Name, Outcome: s.Outcome, Milliseconds: s.Milliseconds })) });
    undoMoves(moves);
    await scan('Long Test Movie put back');
    await waitFor('the Long Test Movie record reattached', async () => { const r = await recordOf(onLong.Id); return r && !r.DetachedUtc ? r : null; });

    // ---- 7. retention and the archive
    const onBlade = await addRecord(bladeRunner.Id, 'demo retention: the tears in rain');
    moves = loadMoves().length;
    holdOut(path.join('Movies', 'Blade Runner (1982)'));
    await scan('Blade Runner held out');
    await waitFor('the Blade Runner record detached', async () => { const r = await recordOf(onBlade.Id); return r && r.DetachedUtc ? r : null; });
    await setConfig({ DetachedRetentionDays: 1, DeveloperClockOffsetDays: 2 });
    const moved = await runMaintenance('retention with the clock two days on');
    const afterArchive = await records();
    const archiveFile = path.join(dataFolder, 'archive', 'library-demo-archived.jsonl');
    const archiveText = fs.existsSync(archiveFile) ? fs.readFileSync(archiveFile, 'utf8') : '';
    const archiveStep = (moved.Steps || []).find((s) => s.Name === 'Archive');
    check('retention 1 day with the developer clock 2 days on: maintenance moves the record out of the store into archive/library-demo-archived.jsonl',
        afterArchive.ClockOffsetHonoured === true && !afterArchive.Records.some((r) => guid(r.Id) === guid(onBlade.Id))
        && afterArchive.Archived.some((r) => guid(r.Id) === guid(onBlade.Id)) && archiveText.includes('the tears in rain') && archiveStep.Result.Owners.find((o) => o.Owner === 'library-demo').Archived === 1,
        { Now: afterArchive.Now, Archived: afterArchive.Archived, archiveLines: archiveText.split('\n').filter(Boolean).length, step: archiveStep.Result });
    const exported = await call('admin', 'GET', `${DEMO}/Export`);
    const envelope = exported.status === 200 ? JSON.parse(exported.text) : null;
    check('the export is an attachment with no-store, and its Archive carries the archived record (and Data doesn\'t)', envelope && envelope.Format === 'jellyfin-plugin-export/1'
        && envelope.Archive.some((r) => guid(r.Id) === guid(onBlade.Id) && r.Note === 'demo retention: the tears in rain')
        && !envelope.Data.Records.some((r) => guid(r.Id) === guid(onBlade.Id)),
        envelope && { Archive: envelope.Archive.length, Records: envelope.Data.Records.length, Users: envelope.Users.length });
    undoMoves(moves);
    await scan('Blade Runner put back');
    const back = await runMaintenance('reattach from the archive');
    const afterReattach = await records();
    const reattached = afterReattach.Records.find((r) => guid(r.Id) === guid(onBlade.Id));
    const reattachStep = (back.Steps || []).find((s) => s.Name === 'Reattach');
    check('its movie back: the next maintenance moves it back into the store, attached (Exact), and out of the archive',
        reattached && !reattached.DetachedUtc && guid(reattached.ItemId) === guid(bladeRunner.Id) && !afterReattach.Archived.some((r) => guid(r.Id) === guid(onBlade.Id))
        && reattachStep.Result.Owners.find((o) => o.Owner === 'library-demo').Relinked === 1, { record: describeRecord(reattached), step: reattachStep.Result });
    await setConfig({ DetachedRetentionDays: 0, DeveloperClockOffsetDays: 0 });

    // ---- 8. Purge
    const onAmelie = await addRecord(amelie.Id, 'demo purge');
    moves = loadMoves().length;
    holdOut(path.relative(mediaRoot, path.dirname(amelie.Path)));
    await scan('Amélie held out');
    await waitFor('the Amélie record detached', async () => { const r = await recordOf(onAmelie.Id); return r && r.DetachedUtc ? r : null; });
    const purged = await demo('POST', '/Purge', { query: { includeArchive: 'true' } });
    const snapshot = purged.Snapshots && purged.Snapshots[0];
    check('Purge deletes the detached record (the only hard delete) after a pre-operation snapshot', purged.Purged === 1 && snapshot
        && fs.existsSync(path.join(dataFolder, 'snapshots', 'pre-op', snapshot)) && !(await recordOf(onAmelie.Id)), purged);
    undoMoves(moves);
    await scan('Amélie put back');

    // ---- 9. the fresh read
    await freshRead(context, series, seasons[0], episodes[0]);

    // ---- 10. LibraryAccess, 11. VersionSets
    await libraryAccessAndVersions(context, { movies, bigBuck, bigBuck2, amelie2, versions, story1990, story2015, amelie, testPattern });
}

async function freshRead(context, series, season, episode) {
    const viewerId = users.viewer.id;
    const before = { series: await userData(series.Id, viewerId), season: await userData(season.Id, viewerId) };
    context.userData = { [series.Id]: stored(before.series), [season.Id]: stored(before.season) };
    const seriesData = { IsFavorite: true, Played: true, PlayCount: 3, PlaybackPositionTicks: TEN_MINUTES };
    const seasonData = { IsFavorite: true, Played: true, PlayCount: 2, PlaybackPositionTicks: TEN_MINUTES / 2 };
    await setUserData(series.Id, seriesData);
    await setUserData(season.Id, seasonData);
    const baseline = await userData(series.Id, viewerId, 'Cached');
    check('fresh read, baseline: viewer\'s stored data on the series, read through core\'s cached instance, which has its snapshot',
        baseline.HasSnapshot === true && baseline.IsFavorite === true && baseline.PlayCount === 3 && baseline.PlaybackPositionTicks === TEN_MINUTES, baseline);

    // (a) An episode refreshed (FullRefresh saves it): core sets its parent's cached snapshot to null (LibraryManager.cs:2169).
    const refreshed = await call('admin', 'POST', `/Items/${episode.Id}/Refresh`, { query: { metadataRefreshMode: 'FullRefresh', imageRefreshMode: 'None', replaceAllMetadata: 'false', replaceAllImages: 'false' } });
    if (refreshed.status !== 204) throw new Error(`POST /Items/${episode.Id}/Refresh: ${refreshed.status}`);
    const blankSeason = await waitFor('the cached season lost its snapshot after the episode refresh', async () => {
        const d = await userData(season.Id, viewerId, 'Cached');
        return d.HasSnapshot === false ? d : null;
    }, 60);
    const nativeSeason = await nativeUserData(season.Id);
    const seriesAfterEpisode = await userData(series.Id, viewerId, 'Cached');
    evidence.episodeRefresh = { season: blankSeason, nativeSeason: { IsFavorite: nativeSeason.IsFavorite, PlayCount: nativeSeason.PlayCount, PlaybackPositionTicks: nativeSeason.PlaybackPositionTicks },
        seriesSnapshotAfterEpisodeRefresh: seriesAfterEpisode.HasSnapshot };
    check('an episode refreshed: GetUserData on the cached Season (its parent) answers blank, and so does Jellyfin\'s own GET /UserItems/{season}/UserData',
        blankSeason.IsFavorite === false && blankSeason.Played === false && blankSeason.PlayCount === 0 && blankSeason.PlaybackPositionTicks === 0
        && nativeSeason.IsFavorite === false && nativeSeason.PlayCount === 0 && nativeSeason.PlaybackPositionTicks === 0, evidence.episodeRefresh);
    const freshSeason = await userData(season.Id, viewerId);
    check('FreshUserData on that Season: the stored favourite, played, play count and resume position, through GetItemList(ItemIds) (no fallback)',
        freshSeason.Source === 'Query' && freshSeason.HadSnapshot === false && freshSeason.IsFavorite === true && freshSeason.Played === true
        && freshSeason.PlayCount === 2 && freshSeason.PlaybackPositionTicks === TEN_MINUTES / 2, freshSeason);
    const repaired = await userData(season.Id, viewerId, 'Cached');
    check('afterwards core\'s cached Season reads right again (FreshUserData put the rows back on it)', repaired.HasSnapshot === true && repaired.IsFavorite === true && repaired.PlayCount === 2, repaired);

    // (b) One of its seasons refreshed (FullRefresh saves it): its parent, core's cached Series (BaseItem.GetParent goes
    // through the cache, BaseItem.cs:1008-1017), loses its snapshot.
    const blankSeries = await blankAfterSeasonRefresh(series, season, viewerId);
    const nativeSeries = await nativeUserData(series.Id);
    evidence.seriesBlank = { cached: blankSeries, native: { IsFavorite: nativeSeries.IsFavorite, Played: nativeSeries.Played, PlayCount: nativeSeries.PlayCount, PlaybackPositionTicks: nativeSeries.PlaybackPositionTicks } };
    check('fresh read, the stale cache reproduced: a season refreshed, GetUserData on the cached Series answers blank (unplayed, not a favourite, play count 0, no position), and so does Jellyfin\'s own GET /UserItems/{series}/UserData',
        blankSeries.IsFavorite === false && blankSeries.Played === false && blankSeries.PlayCount === 0 && blankSeries.PlaybackPositionTicks === 0
        && nativeSeries.IsFavorite === false && nativeSeries.PlayCount === 0 && nativeSeries.PlaybackPositionTicks === 0, evidence.seriesBlank);
    const freshSeries = await userData(series.Id, viewerId);
    check('FreshUserData on the Series returns the stored favourite, played, play count and resume position, through GetItemList(ItemIds) (the kit\'s read path; no fallback)',
        freshSeries.Source === 'Query' && freshSeries.ReadPath === 'Query' && freshSeries.HadSnapshot === false && freshSeries.IsFavorite === true && freshSeries.Played === true
        && freshSeries.PlayCount === 3 && freshSeries.PlaybackPositionTicks === TEN_MINUTES, freshSeries);
    const nativeAfter = await nativeUserData(series.Id);
    const cachedAfter = await userData(series.Id, viewerId, 'Cached');
    check('afterwards the cached Series and Jellyfin\'s own API read right again', cachedAfter.HasSnapshot === true && cachedAfter.IsFavorite === true && cachedAfter.PlayCount === 3
        && nativeAfter.IsFavorite === true && nativeAfter.PlayCount === 3 && nativeAfter.PlaybackPositionTicks === TEN_MINUTES,
        { cached: stored(cachedAfter), native: { IsFavorite: nativeAfter.IsFavorite, PlayCount: nativeAfter.PlayCount, PlaybackPositionTicks: nativeAfter.PlaybackPositionTicks } });

    // (c) The other trigger, an episode added and scanned (in a new season), observed: whether the scan leaves the cached
    // Series blank depends on which instance the scan's validation holds (it registers the repository's instance in the
    // cache through UpdateImagesAsync, LibraryManager.cs:2146, only for an unchanged folder), so it is evidence, not a check.
    createNewSeason();
    await scan('Season 03 added');
    await delay(1000);
    evidence.addSeasonScan = { seriesSnapshotAfterScan: (await userData(series.Id, viewerId, 'Cached')).HasSnapshot };
    removeCreated();
    await scan('Season 03 removed');
    evidence.addSeasonScan.seriesSnapshotAfterRemoval = (await userData(series.Id, viewerId, 'Cached')).HasSnapshot;

    // (d) The fallback path on a blank Series: a season refreshed again, then IItemRepository.RetrieveItem.
    await blankAfterSeasonRefresh(series, season, viewerId);
    const fromRepository = await userData(series.Id, viewerId, 'Fresh', 'Repository');
    check('the fallback path, IItemRepository.RetrieveItem, reads the same stored values', fromRepository.Source === 'Repository' && fromRepository.HadSnapshot === false
        && fromRepository.IsFavorite === true && fromRepository.Played === true && fromRepository.PlayCount === 3 && fromRepository.PlaybackPositionTicks === TEN_MINUTES, fromRepository);

    for (const [id, data] of Object.entries(context.userData)) await setUserData(id, data);
    const restored = await userData(series.Id, viewerId);
    check('viewer\'s user data on the series and season put back as it was', restored.IsFavorite === before.series.IsFavorite && restored.PlayCount === before.series.PlayCount,
        { before: stored(before.series), after: stored(restored) });
    context.userData = null;
}

// Refreshes a season with FullRefresh (which saves it) and waits until the cached series has lost its user-data snapshot.
async function blankAfterSeasonRefresh(series, season, viewerId) {
    const refreshed = await call('admin', 'POST', `/Items/${season.Id}/Refresh`, { query: { metadataRefreshMode: 'FullRefresh', imageRefreshMode: 'None', replaceAllMetadata: 'false', replaceAllImages: 'false' } });
    if (refreshed.status !== 204) throw new Error(`POST /Items/${season.Id}/Refresh: ${refreshed.status}`);
    return waitFor('the cached series lost its snapshot after the season refresh', async () => {
        const d = await userData(series.Id, viewerId, 'Cached');
        return d.HasSnapshot === false ? d : null;
    }, 60);
}

async function libraryAccessAndVersions(context, m) {
    const folders = await call('admin', 'GET', '/Library/VirtualFolders');
    const libraries = folders.json;
    const movies2 = libraries.find((l) => l.Name === 'Movies 2');
    const viewer2 = await call('admin', 'GET', `/Users/${users.viewer2.id}`);
    context.viewer2Policy = viewer2.json.Policy;
    const enabled = libraries.filter((l) => l.Name !== 'Movies 2').map((l) => l.ItemId);
    const restricted = await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: { ...context.viewer2Policy, EnableAllFolders: false, EnabledFolders: enabled } });
    if (restricted.status !== 204) throw new Error(`POST /Users/viewer2/Policy: ${restricted.status}`);

    // ---- 10. LibraryAccess
    const forViewer2 = await demo('GET', '/Libraries', { query: { userId: users.viewer2.id } });
    const forAdmin = await demo('GET', '/Libraries');
    const native = await call('viewer2', 'GET', '/Items', { query: { userId: users.viewer2.id, Recursive: 'true', IncludeItemTypes: 'Movie', IsMissing: 'false', EnableImages: 'false', EnableUserData: 'false' } });
    const nativeIds = new Set(native.json.Items.map((i) => guid(i.Id)));
    const ourIds = new Set(forViewer2.Query.MovieIds.map(guid));
    const visibleNames = forViewer2.Visible.map((l) => l.Name).sort();
    check('LibraryAccess for viewer2 without Movies 2: the visible libraries, the factory\'s query (explicit TopParentIds, no core grouping, no HasOwnerId)',
        !visibleNames.includes('Movies 2') && visibleNames.includes('Movies') && forViewer2.Query.UserSet === true && forViewer2.Query.GroupByPresentationUniqueKey === false
        && forViewer2.Query.HasOwnerId === null && forViewer2.Query.TopParentIds.length === forViewer2.Visible.reduce((n, l) => n + l.PhysicalFolderIds.length, 0),
        { visible: visibleNames, query: { ...forViewer2.Query, MovieIds: undefined } });
    check('the query finds exactly the movies Jellyfin\'s own /Items gives viewer2, none owned (the local alternate version isn\'t one)',
        ourIds.size === nativeIds.size && [...ourIds].every((id) => nativeIds.has(id)) && forViewer2.Query.OwnedMovies === 0 && !ourIds.has(guid(m.bigBuck2.Id)),
        { ours: ourIds.size, native: nativeIds.size, owned: forViewer2.Query.OwnedMovies });
    const onlyMovies2 = await demo('GET', '/Libraries', { query: { userId: users.viewer2.id, libraryIds: movies2.ItemId } });
    check('a Movies 2 id asked for as viewer2 resolves to no library, and the factory gives no query (never TopParentIds that mean "everything")',
        onlyMovies2.Resolved.length === 0 && onlyMovies2.Query === null, { Resolved: onlyMovies2.Resolved, Query: onlyMovies2.Query });
    check('the admin sees every library', forAdmin.Visible.length === libraries.length && forAdmin.Visible.some((l) => l.Name === 'Movies 2'), forAdmin.Visible.map((l) => l.Name));

    // ---- 11. VersionSets: a chain built by Jellyfin's own Merge versions, then split
    const merge = async (a, b) => {
        const r = await call('admin', 'POST', '/Videos/MergeVersions', { query: { ids: `${a},${b}` } });
        if (r.status !== 204) throw new Error(`POST /Videos/MergeVersions: ${r.status}`);
        context.merged.push([a, b]);
    };
    const sets = (ids, userId) => demo('GET', '/VersionSets', { query: { itemIds: ids, userId } });
    const [a, b, c, d] = [m.story1990.Id, m.story2015.Id, m.amelie.Id, m.testPattern.Id];
    await merge(a, b);
    const pair1 = (await sets([a, b])).Sets[0];
    await merge(c, d);
    const pair2 = (await sets([c, d])).Sets[0];
    await merge(pair1.RootId, pair2.RootId);
    const chain = await sets([a, b, c, d]);
    const set = chain.Sets[0];
    const root = guid(set.RootId);
    const folded = [pair1, pair2].find((p) => guid(p.RootId) !== root);
    const foldedSecondary = folded.MemberIds.find((id) => guid(id) !== guid(folded.RootId));
    check('native Merge versions of two merged pairs\' primaries: one set of four; the folded pair\'s secondary still points at its old primary (Chained), nothing unlisted or listed-not-pointing',
        chain.Sets.length === 1 && set.MemberIds.length === 4 && set.ChainedIds.length === 1 && guid(set.ChainedIds[0]) === guid(foldedSecondary)
        && set.UnlistedIds.length === 0 && set.ListedNotPointingIds.length === 0 && set.State === 'Chained',
        { pairs: [pair1.RootId, pair2.RootId], set });
    const repViewer2 = (await sets([a, b, c, d], users.viewer2.id)).Representatives;
    check('Collapse: the chain is one row, its primary', repViewer2.length === 1 && guid(repViewer2[0].Id) === root && repViewer2[0].IsRoot === true, repViewer2);
    const split = await call('admin', 'DELETE', `/Videos/${set.RootId}/AlternateSources`);
    if (split.status !== 204) throw new Error(`DELETE /Videos/AlternateSources: ${split.status}`);
    context.merged = context.merged.filter(([x, y]) => ![a, b, c, d].includes(x) && ![a, b, c, d].includes(y));
    const afterSplit = await sets([a, b, c, d]);
    check('split with Jellyfin\'s own "Split versions": four standalone sets', afterSplit.Sets.length === 4 && afterSplit.Sets.every((s) => s.MemberIds.length === 1 && s.State === 'None'),
        afterSplit.Sets.map((s) => s.State));

    // A set across Movies and Movies 2 whose primary is in Movies 2: two Movies 2 videos merged first, so that native Merge
    // versions takes their primary (the only one with more than one source) for the set's primary when the Movies video
    // joins. viewer2, without Movies 2, gets the Movies member (not the primary); the admin gets the primary.
    await merge(m.bigBuck2.Id, m.amelie2.Id);
    const movies2Primary = (await sets([m.bigBuck2.Id, m.amelie2.Id])).Sets[0].RootId;
    await merge(movies2Primary, m.bigBuck.Id);
    const triple = [m.bigBuck.Id, m.bigBuck2.Id, m.amelie2.Id];
    const crossViewer2 = await sets(triple, users.viewer2.id);
    const crossAdmin = await sets(triple);
    const crossRoot = guid(crossAdmin.Sets[0].RootId);
    check('a set across Movies and Movies 2 with its primary in Movies 2: viewer2 gets the Movies member (its only eligible one, not the primary), the admin the primary',
        crossAdmin.Sets.length === 1 && crossAdmin.Sets[0].MemberIds.length === 3 && crossRoot === guid(movies2Primary)
        && crossViewer2.Representatives.length === 1 && guid(crossViewer2.Representatives[0].Id) === guid(m.bigBuck.Id) && crossViewer2.Representatives[0].IsRoot === false
        && crossViewer2.Eligible.map(guid).join() === guid(m.bigBuck.Id) && crossAdmin.Representatives.length === 1 && guid(crossAdmin.Representatives[0].Id) === crossRoot,
        { set: crossAdmin.Sets[0], viewer2: crossViewer2.Representatives, admin: crossAdmin.Representatives });
    const unmerge = await call('admin', 'DELETE', `/Videos/${crossAdmin.Sets[0].RootId}/AlternateSources`);
    if (unmerge.status !== 204) throw new Error(`DELETE /Videos/AlternateSources: ${unmerge.status}`);
    context.merged = [];
    const unmerged = await sets(triple);
    check('split again: three standalone sets', unmerged.Sets.length === 3 && unmerged.Sets.every((x) => x.State === 'None'), unmerged.Sets.map((x) => x.State));

    // The local alternate version of "Versions Movie (2019)" is an owned item: never eligible.
    const full = await call('admin', 'GET', `/Items/${m.versions.Id}`, { query: { userId: users.admin.id, Fields: 'MediaSources' } });
    const alternate = (full.json.MediaSources || []).map((s) => s.Id).find((id) => guid(id) !== guid(m.versions.Id));
    const owned = alternate ? await sets([alternate]) : null;
    check('a local alternate version (an owned item with no top parent) is never eligible', alternate && owned.Eligible.length === 0 && owned.Representatives.length === 0,
        { alternate: !!alternate, Eligible: owned && owned.Eligible });

    await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: context.viewer2Policy });
    context.viewer2Policy = null;
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { saved: null, userData: null, viewer2Policy: null, merged: [] };
    const startLog = logPosition();
    const leftover = loadMoves().length + (fs.existsSync(createdFile) ? 1 : 0);
    if (leftover > 0) {
        undoMoves(0);
        removeCreated();
        await scan('left-over changes of an earlier run undone');
        evidence.undidLeftovers = leftover;
    }
    let failure = null;
    try {
        await run(context);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        try {
            const changed = loadMoves().length + removeCreated();
            undoMoves(0);
            if (changed > 0) await scan('clean-up: media put back');
        } catch (error) {
            check('clean-up: every media change undone', false, String(error));
        }
        try {
            for (const [a] of context.merged) await call('admin', 'DELETE', `/Videos/${a}/AlternateSources`);
            if (context.viewer2Policy) await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: context.viewer2Policy });
            if (context.userData) for (const [id, data] of Object.entries(context.userData)) await setUserData(id, data);
            if (context.saved) {
                await setConfig({ EnableLibraryDemo: true, DetachedRetentionDays: 0, DeveloperClockOffsetDays: 0 });
                await demo('DELETE', '/Records');
                await demo('POST', '/Purge', { query: { includeArchive: 'true' } });
                await setConfig({ EnableLibraryDemo: context.saved.EnableLibraryDemo === true, DetachedRetentionDays: context.saved.DetachedRetentionDays ?? 0,
                    DeveloperClockOffsetDays: context.saved.DeveloperClockOffsetDays ?? 0 });
            }
        } catch (error) {
            check('clean-up: the settings, users and versions put back', false, String(error));
        }
        const text = logSince(startLog);
        const errors = text.split('\n').filter((l) => /\[ERR\]|\[FTL\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        check('no error of Bookmarks or the kit in the server log during the run', errors.length === 0, errors.slice(0, 5));
        check('clean-up: no media change left, the demo setting as it was', loadMoves().length === 0 && !fs.existsSync(createdFile)
            && (context.saved === null || (await getConfig()).EnableLibraryDemo === (context.saved.EnableLibraryDemo === true)), { moves: loadMoves().length });
        if (fs.existsSync(heldRoot) && fs.readdirSync(heldRoot, { recursive: true }).every((f) => fs.statSync(path.join(heldRoot, f)).isDirectory())) fs.rmSync(heldRoot, { recursive: true });
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
