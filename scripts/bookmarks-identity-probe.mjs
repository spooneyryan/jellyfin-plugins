#!/usr/bin/env node
// Bookmarks' identity service against a real library in a running sandbox: real bookmarks made through
// Bookmarks' REST API, the sandbox's media moved around, Jellyfin's scans, a restart, Bookmarks' Diagnostics, its
// bookmarks.json (read only) and its daily "Bookmarks: Maintenance" task: a video removed and put back, with the relinks
// around it.
// Node 22 built-ins only. Loopback only, never port 8096. The users' tokens come from credentials.json and are never
// printed.
//
//   node scripts/bookmarks-identity-probe.mjs --sandbox default [--timeout <scan seconds>] [--skip-restart] [--out result.json]
//
// It changes the shared sandbox media and puts every change back: it renames folders only inside .sandbox/media and its
// hold folder .sandbox/<name>/bookmarks-identity-probe/held (plain renames), records each move in moves.json there before
// making it, and undoes the moves in reverse order at the end, on failure, and at the start of the next run after a run
// was killed (then it scans once). It writes the sandbox's move journal (<data>/librarymaintenance/moves.jsonl) as Library
// Maintenance would, after moving an existing one aside, and puts that one back at the end (or removes its own). While the
// sandbox is stopped for the restart it edits bookmarks.json once: one bookmark's identity snapshot is cut back to what the
// 1→2 upgrade leaves of a 1.0 bookmark, for the backfill, and another one is made such a bookmark on an id the library
// doesn't have (its video gone before the first 1.1 start). The bookmarks it makes are deleted at the end.
//
// Checks:
//   1. Diagnostics: the identity service is running (Identity, StartupDone), the bookmarks are the verify sweep's and the
//      detached records' owner, a library-change consumer and a user data owner (nothing in any Unavailable list);
//   2. a movie's folder held out and scanned: its bookmark is "Not in library" (Detached in the API,
//      DetachedUtc in bookmarks.json), Diagnostics counts it; put back and scanned: reattached to the same id (Exact: the
//      media source and audio track kept, not marked relinked);
//   3. the edition pair (the same TMDb id, 60 s and 90 s): the 60 s cut held out: its bookmark stays detached and
//      ambiguous (Bookmarks' runtime check), never moved onto the 90 s cut, also after the daily maintenance's re-resolution;
//      put back: reattached (Exact);
//   4. two copies of equal runtime and the same TMDb id: the 1999 copy held out: its bookmark goes to the 2005 copy (Strong:
//      media source and audio track cleared, "Relinked" in the API); a rename (the badge's "Looks right") clears it;
//   5. the test series' folder renamed: its four episodes' bookmarks detach and relink in the same scan (Strong, by the
//      series' TVDb id plus numbers); renamed back: they return to the original episodes;
//   6. a restart (Stop-Sandbox.ps1, dev-server.ps1 -NoBuild) with a movie moved by "Library Maintenance" meanwhile (a
//      Committed journal line to the id Jellyfin will give it) and a bookmark cut back to a 1.0 snapshot: the startup
//      catch-up relinks the moved movie's bookmark to the new id (Exact, pending until the scan, the media source moved
//      with it) and stores the journal position; the backfill completes the other bookmark (name, year, runtime, path)
//      without touching its UpdatedAt; after the scan the bookmark is on the new item; a Move back line and the folder
//      moved back: the bookmark follows through the journal (Exact) in the scan; a bookmark upgraded from 1.0 whose video
//      was already gone (another id, no runtime) with one TMDb candidate in the library: detached by the startup sweep,
//      it stays detached and ambiguous, never moved onto the candidate, whose runtime it can't check;
//   7. the daily maintenance runs its eight steps in order (Snapshots, Archive, Users, VerifySweep, ResolveDetached,
//      Reattach, ClipFiles, MissingClips: the last since Bookmarks 1.2), none failing, and writes a snapshot of bookmarks.json;
//   8. no error of Bookmarks or the kit in the server log; clean-up.
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const MOVIE_TYPE = 'MediaBrowser.Controller.Entities.Movies.Movie';
const SERIES_FOLDER = 'Test Series (2020) [tvdbid-900401]';
const RENAMED_FOLDER = 'Test Series Renamed (2020) [tvdbid-900401]';
const MOVED_FOLDER = 'bookmarks-identity-probe-moved';
const PROBE_NOTE = 'bm identity probe';

function usage() {
    return 'usage: node scripts/bookmarks-identity-probe.mjs --sandbox <name> [--timeout <scan seconds>] [--skip-restart] [--out <file>]';
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
const workRoot = path.join(sandboxRoot, 'bookmarks-identity-probe');
const heldRoot = path.join(workRoot, 'held');
const movesFile = path.join(workRoot, 'moves.json');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
let state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
if (!logDir.startsWith(sandboxRoot + path.sep)) throw new Error(`refusing to read the log folder ${logDir}: not in the sandbox`);
const dataRoot = path.join(sandboxRoot, 'data', 'data');
const storeFile = path.join(dataRoot, 'bookmarks', 'bookmarks.json');
const journalFile = path.join(dataRoot, 'librarymaintenance', 'moves.jsonl');
const journalAside = path.join(workRoot, 'moves.jsonl.before-probe');
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error(`refusing to work in ${p}`);
    return full;
}
for (const p of [storeFile, journalFile, workRoot]) assertInside(p, sandboxRoot);

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, scans: [], restarts: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// Jellyfin writes GUIDs without dashes ("N"); ASP.NET Core's JSON and bookmarks.json write them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="bookmarks-identity-probe", Device="sandbox", DeviceId="bookmarks-identity-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text };
}

async function diagnostics() {
    const r = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    if (r.status !== 200 || !r.json) throw new Error(`GET /Bookmarks/Admin/Diagnostics: ${r.status}`);
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

// ------------------------------------------------------------------------------------------ bookmarks

const made = [];

async function createBookmark(who, item, { mediaSourceId = true, audio = 1, name = null } = {}) {
    const body = { ItemId: item.Id, StartTicks: 50_000_000, EndTicks: 150_000_000, Note: PROBE_NOTE, Name: name };
    if (mediaSourceId) body.MediaSourceId = guid(item.Id);
    if (audio !== null) body.AudioStreamIndex = audio;
    const r = await call(who, 'POST', '/Bookmarks', { body });
    if (r.status !== 201 || !r.json) throw new Error(`POST /Bookmarks on ${item.Name}: ${r.status} ${r.text.slice(0, 200)}`);
    made.push({ who, id: r.json.Id });
    return r.json;
}

async function dto(id, who = 'admin') {
    const r = await call(who, 'GET', `/Bookmarks/${id}`);
    if (r.status !== 200) throw new Error(`GET /Bookmarks/${id}: ${r.status}`);
    return r.json;
}

// The record as bookmarks.json holds it (read only).
function stored(id) {
    const doc = readJson(storeFile);
    return (doc.Bookmarks || []).find((b) => guid(b.Id) === guid(id)) || null;
}

const describe = (b) => b && ({ ItemId: guid(b.ItemId), Detached: b.Detached ?? !!b.DetachedUtc, Relinked: b.Relinked ?? !!b.RelinkedUtc,
    MediaSourceId: b.MediaSourceId ?? null, AudioStreamIndex: b.AudioStreamIndex ?? null, RelinkStrength: b.RelinkStrength, AttachedUtc: b.AttachedUtc });

function bookmarksOwner(diag) {
    return ((diag.Detached && diag.Detached.Owners) || []).find((o) => o.Owner === 'bookmarks') || null;
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
    // An emptied folder the probe created for a move (the moved-to parent) goes too.
    const movedParent = path.join(mediaRoot, 'Movies', MOVED_FOLDER);
    if (untilCount === 0 && fs.existsSync(movedParent) && fs.readdirSync(movedParent).length === 0) fs.rmdirSync(movedParent);
}

// GetNewItemId (LibraryManager.cs:631-657): MD5 over UTF-16LE of the type's full name plus the path (lower case unless
// EnableCaseSensitiveItemIds), read as a .NET Guid (its first three fields little-endian).
function predictId(type, key, lowerCase) {
    const hash = crypto.createHash('md5').update(Buffer.from(type + (lowerCase ? key.toLowerCase() : key), 'utf16le')).digest();
    const hex = (bytes) => Buffer.from(bytes).toString('hex');
    return hex([hash[3], hash[2], hash[1], hash[0]]) + hex([hash[5], hash[4]]) + hex([hash[7], hash[6]]) + hex(hash.subarray(8));
}

// ------------------------------------------------------------------------------------------ library, tasks, log

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

async function startSandbox(label) {
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

function stopSandbox(label) {
    evidence.restarts.push({ label, step: 'stop', seconds: runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox]) });
}

// ------------------------------------------------------------------------------------------ the run

async function run(context) {
    // ---- 1. Diagnostics: the identity service and its registrations
    const diag0 = await diagnostics();
    const identity0 = diag0.Identity || {};
    check('Diagnostics: the identity service runs (StartupDone), the bookmarks are an owner of the verify sweep and the detached records, a library-change consumer ("Bookmarks") and a user data owner, and no owner, consumer or step is unavailable',
        identity0.StartupDone === true && identity0.ReadOnly === false && (diag0.Verifier.Owners || []).includes('bookmarks') && !!bookmarksOwner(diag0)
        && (diag0.LibraryChanges.Consumers || []).some((c) => c.Name === 'Bookmarks')
        && ['Verifier', 'Maintenance', 'LibraryChanges', 'Users'].every((k) => Array.isArray(diag0[k].Unavailable) && diag0[k].Unavailable.length === 0)
        && (diag0.Users.SkippedOwners || []).length === 0,
        { Identity: identity0, Owners: diag0.Verifier.Owners, Consumers: (diag0.LibraryChanges.Consumers || []).map((c) => c.Name),
            Unavailable: Object.fromEntries(['Verifier', 'Maintenance', 'LibraryChanges', 'Users'].map((k) => [k, diag0[k].Unavailable])) });

    const movies = await items('Movie');
    const movie = (folder) => one(movies, (m) => inFolder(m, folder) && m.Path.startsWith(path.join(mediaRoot, 'Movies') + path.sep), folder);
    const tagged = movie('Tagged Movie (2012) [tmdbid-900001]');
    const edition60 = movie('Edition Movie (2001) [tmdbid-900301]');
    const edition90 = movie('Edition Movie Extended (2001) [tmdbid-900301]');
    const remake1999 = movie('Same Id Remake (1999) [tmdbid-900201]');
    const remake2005 = movie('Same Id Remake (2005) [tmdbid-900201]');
    const bladeRunner = movie('Blade Runner (1982)');
    const remakeStory = movie('Remake Story (1990) [tmdbid-900101]');
    const series = one(await items('Series'), (s) => inFolder(s, SERIES_FOLDER), 'Test Series');
    const episodes = (await items('Episode')).filter((e) => inFolder(e, SERIES_FOLDER))
        .sort((a, b) => (a.ParentIndexNumber - b.ParentIndexNumber) || (a.IndexNumber - b.IndexNumber));
    if (episodes.length !== 4) throw new Error('the test series needs two seasons of two episodes; run Initialize-Sandbox.ps1 on fresh media');

    // ---- 2. a movie removed and put back
    const onTagged = await createBookmark('viewer', tagged);
    let moves = loadMoves().length;
    holdOut(path.join('Movies', 'Tagged Movie (2012) [tmdbid-900001]'));
    await scan('Tagged Movie held out');
    const gone = await waitFor('the bookmark "Not in library"', async () => { const b = await dto(onTagged.Id, 'viewer'); return b.Detached ? b : null; });
    const goneStored = stored(onTagged.Id);
    const countedGone = bookmarksOwner(await diagnostics());
    check('removed and put back: the movie\'s folder held out and scanned: its bookmark is "Not in library" (Detached in the API, DetachedUtc in bookmarks.json, still on its item with its snapshot), and Diagnostics counts it',
        gone.Detached === true && gone.Relinked === false && guid(gone.ItemId) === guid(tagged.Id) && !!goneStored.DetachedUtc
        && goneStored.Identity && goneStored.Identity.RunTimeTicks > 0 && countedGone && countedGone.Count >= 1,
        { api: describe(gone), stored: describe(goneStored), detached: countedGone });
    undoMoves(moves);
    await scan('Tagged Movie put back');
    const back = await waitFor('the bookmark reattached', async () => { const b = await dto(onTagged.Id, 'viewer'); return !b.Detached ? b : null; });
    check('removed and put back: put back and scanned: reattached to the same id (Exact: the media source and audio track kept, not marked relinked)',
        guid(back.ItemId) === guid(tagged.Id) && guid(back.MediaSourceId) === guid(tagged.Id) && back.AudioStreamIndex === 1 && back.Relinked === false
        && !stored(onTagged.Id).RelinkStrength, describe(back));

    // ---- 3. the edition pair, one cut deleted
    const onEdition = await createBookmark('viewer', edition60);
    moves = loadMoves().length;
    holdOut(path.join('Movies', 'Edition Movie (2001) [tmdbid-900301]'));
    await scan('the 60 s cut held out');
    await waitFor('the 60 s cut\'s bookmark detached', async () => ((await dto(onEdition.Id, 'viewer')).Detached ? true : null));
    const editionMaintenance = await runMaintenance('re-resolution with the 60 s cut gone');
    const edition = await dto(onEdition.Id, 'viewer');
    const ambiguous = bookmarksOwner(await diagnostics());
    const resolveStep = (editionMaintenance.Steps || []).find((s) => s.Name === 'ResolveDetached');
    check('the edition pair: the 60 s cut deleted, the 90 s cut (the same TMDb id) still there: its bookmark stays detached and Ambiguous (Bookmarks\' runtime check), never moved onto the other cut, also after the daily re-resolution',
        edition.Detached === true && guid(edition.ItemId) === guid(edition60.Id) && guid(edition.ItemId) !== guid(edition90.Id) && guid(edition.MediaSourceId) === guid(edition60.Id)
        && ambiguous && ambiguous.Ambiguous >= 1,
        { api: describe(edition), detached: ambiguous, resolve: resolveStep && resolveStep.Result });
    undoMoves(moves);
    await scan('the 60 s cut put back');
    const editionBack = await waitFor('the 60 s cut\'s bookmark reattached', async () => { const b = await dto(onEdition.Id, 'viewer'); return !b.Detached ? b : null; });
    check('the edition pair: the 60 s cut put back: its bookmark reattaches to it (Exact, the media source kept)', guid(editionBack.ItemId) === guid(edition60.Id)
        && guid(editionBack.MediaSourceId) === guid(edition60.Id) && editionBack.Relinked === false, describe(editionBack));

    // ---- 4. two copies of equal runtime: Strong relink
    const onRemake = await createBookmark('viewer', remake1999, { audio: 2 });
    moves = loadMoves().length;
    holdOut(path.join('Movies', 'Same Id Remake (1999) [tmdbid-900201]'));
    await scan('the 1999 copy held out');
    let remake = await dto(onRemake.Id, 'viewer');
    if (remake.Detached) {
        await runMaintenance('re-resolution with the 1999 copy gone');
        remake = await dto(onRemake.Id, 'viewer');
    }
    const remakeStored = stored(onRemake.Id);
    check('the 1999 copy deleted: its bookmark goes to the 2005 copy (one Strong candidate of equal runtime): the media source and audio track cleared, "Relinked" in the API, RelinkStrength Strong in bookmarks.json, the positions kept',
        remake.Detached === false && guid(remake.ItemId) === guid(remake2005.Id) && remake.MediaSourceId == null && remake.AudioStreamIndex == null && remake.Relinked === true
        && remakeStored.RelinkStrength === 'Strong' && remake.StartTicks === 50_000_000 && remake.EndTicks === 150_000_000,
        { api: describe(remake), stored: describe(remakeStored) });
    undoMoves(moves);
    await scan('the 1999 copy put back');
    const renamed = await call('viewer', 'POST', `/Bookmarks/${onRemake.Id}/Name`, { body: { Name: null, IfUpdatedAt: remake.UpdatedAt } });
    const afterLooksRight = await dto(onRemake.Id, 'viewer');
    check('the copy put back, the bookmark stays on the 2005 copy; a rename ("Looks right") clears the relinked mark',
        renamed.status === 200 && guid(afterLooksRight.ItemId) === guid(remake2005.Id) && afterLooksRight.Relinked === false && !stored(onRemake.Id).RelinkedUtc,
        { status: renamed.status, api: describe(afterLooksRight) });

    // ---- 5. the series folder renamed and back
    const onEpisodes = [];
    for (const e of episodes) onEpisodes.push(await createBookmark('viewer', e));
    const seriesMoves = loadMoves().length;
    move(path.join(mediaRoot, 'Shows', SERIES_FOLDER), path.join(mediaRoot, 'Shows', RENAMED_FOLDER));
    await scan('series folder renamed');
    const newEpisodes = (await items('Episode')).filter((e) => inFolder(e, RENAMED_FOLDER))
        .sort((a, b) => (a.ParentIndexNumber - b.ParentIndexNumber) || (a.IndexNumber - b.IndexNumber));
    const relinkedEpisodes = await waitFor('the episodes\' bookmarks on the renamed folder', async () => {
        const list = [];
        for (const b of onEpisodes) list.push(await dto(b.Id, 'viewer'));
        return list.every((b, i) => !b.Detached && newEpisodes[i] && guid(b.ItemId) === guid(newEpisodes[i].Id)) ? list : null;
    }, 30);
    check('the series folder renamed: its four episodes\' bookmarks detach and relink in the same scan, each to its episode in the renamed folder (Strong: the series\' TVDb id plus numbers; media source cleared, "Relinked")',
        relinkedEpisodes.every((b) => b.Relinked === true && b.MediaSourceId == null), relinkedEpisodes.map(describe));
    undoMoves(seriesMoves);
    await scan('series folder renamed back');
    const home = await waitFor('the episodes\' bookmarks back on the original episodes', async () => {
        const list = [];
        for (const b of onEpisodes) list.push(await dto(b.Id, 'viewer'));
        return list.every((b, i) => !b.Detached && guid(b.ItemId) === guid(episodes[i].Id)) ? list : null;
    }, 30);
    check('renamed back: every episode bookmark is on its original episode again', home.length === 4, home.map(describe));
    void series;

    // ---- 6. a restart: the move journal's catch-up and the backfill
    const onBlade = await createBookmark('viewer', bladeRunner);
    // Tagged Movie's display name (the bookmark's ItemName, the upgrade's provisional snapshot name) isn't its name.
    const onLong = await createBookmark('viewer', tagged, { name: 'Backfill me' });
    const longBefore = stored(onLong.Id);
    const oldPath = bladeRunner.Path;
    const newPath = path.join(path.dirname(path.dirname(oldPath)), MOVED_FOLDER, path.basename(path.dirname(oldPath)), path.basename(oldPath));
    const variant = [false, true].find((lower) => predictId(MOVIE_TYPE, oldPath, lower) === guid(bladeRunner.Id));
    check('the id prediction (GetNewItemId) matches the movie\'s current id', variant !== undefined, { lowerCase: variant ?? null });
    const predicted = predictId(MOVIE_TYPE, newPath, variant === true);
    const journalId = crypto.randomUUID().replace(/-/g, '');
    const iso = () => new Date().toISOString();
    const line = (seq, oldId, newId, from, to) => JSON.stringify({ V: 1, JournalId: journalId, Seq: seq, Status: 'Committed', Utc: iso(), OldId: guid(oldId), NewId: guid(newId),
        Kind: 'Movie', OldPath: from, NewPath: to, JobId: crypto.randomUUID().replace(/-/g, ''), Role: 'Primary' });
    if (opts.skipRestart) {
        check('restart: skipped (--skip-restart)', true, null);
    } else {
        // Made on Remake Story (1990), the only movie with TMDb id 900101, and moved below to a video 1.0 had and lost.
        const onVanished = await createBookmark('viewer', remakeStory);
        const vanished = crypto.randomUUID();
        const restartLog = logPosition();
        stopSandbox('journal and backfill');
        context.stopped = true;

        // Library Maintenance moves Blade Runner while the server is off and commits the journal line (its move's last step).
        const journalMoves = loadMoves().length;
        move(path.dirname(oldPath), path.dirname(newPath));
        if (fs.existsSync(journalFile) && !fs.existsSync(journalAside)) {
            fs.mkdirSync(workRoot, { recursive: true });
            fs.renameSync(journalFile, journalAside);
        }
        context.journalWritten = true;
        fs.mkdirSync(path.dirname(journalFile), { recursive: true });
        const committed = line(1, bladeRunner.Id, predicted, oldPath, newPath);
        fs.writeFileSync(journalFile, [JSON.stringify({ V: 1, Header: true, JournalId: journalId, CreatedUtc: iso() }), committed].join('\n') + '\n');
        const lineUtc = JSON.parse(committed).Utc;

        // One bookmark cut back to what the 1→2 upgrade leaves of a 1.0 bookmark: kind, the display name, provider ids.
        const doc = readJson(storeFile);
        const record = doc.Bookmarks.find((b) => guid(b.Id) === guid(onLong.Id));
        record.Identity = { ItemId: record.Identity.ItemId, Kind: record.Identity.Kind, Name: record.ItemName, ProviderIds: record.Identity.ProviderIds || {} };
        // Another one as the upgrade leaves a 1.0 bookmark whose video was already gone (a file replaced under 1.0, so
        // another id): on that id, with its media source, and without a runtime, since the backfill can't look it up.
        const upgraded = doc.Bookmarks.find((b) => guid(b.Id) === guid(onVanished.Id));
        const vanishedIds = upgraded.Identity.ProviderIds || {};
        upgraded.ItemId = vanished;
        upgraded.MediaSourceId = guid(vanished);
        upgraded.Identity = { ItemId: vanished, Kind: upgraded.Identity.Kind, Name: upgraded.ItemName, ProviderIds: vanishedIds };
        fs.writeFileSync(storeFile, JSON.stringify(doc, null, 2));

        await startSandbox('journal and backfill');
        context.stopped = false;
        const startup = await waitFor('the identity service\'s startup work', async () => { const d = await diagnostics(); return d.Identity && d.Identity.StartupDone ? d : null; }, 60);
        const blade = await dto(onBlade.Id, 'viewer');
        const bladeStored = stored(onBlade.Id);
        check('restart: the startup catch-up moved the bookmark to the id Library Maintenance predicted (Exact: attached since the line, the media source moved with it, not marked relinked), before any scan: a pending relink the verify sweep leaves attached',
            guid(blade.ItemId) === predicted && blade.Detached === false && blade.Relinked === false && guid(blade.MediaSourceId) === predicted && blade.AudioStreamIndex === 1
            && Date.parse(bladeStored.AttachedUtc) === Date.parse(lineUtc) && !(await exists(predicted)),
            { api: describe(blade), stored: describe(bladeStored), predicted, lineUtc });
        check('restart: Diagnostics Identity holds the journal position (the journal id, LastMoveSeq 1) and the catch-up\'s count; bookmarks.json too',
            guid(startup.Identity.Journal.JournalId) === journalId && startup.Identity.Journal.LastMoveSeq === 1 && startup.Identity.CatchUp.Relinked === 1
            && guid(readJson(storeFile).MoveJournalId) === journalId && readJson(storeFile).LastMoveSeq === 1,
            { Journal: startup.Identity.Journal, CatchUp: startup.Identity.CatchUp });
        const longAfter = stored(onLong.Id);
        const longItem = (await call('admin', 'GET', `/Items/${tagged.Id}`, { query: { userId: users.admin.id, Fields: 'Path' } })).json;
        check('restart: the backfill completed the bookmark cut back to a 1.0 snapshot (the name, year, runtime and path of its video), and nothing a user sees: its name, note and UpdatedAt',
            longAfter.Identity.Name === longItem.Name && longAfter.Identity.Name !== longAfter.ItemName && longAfter.Identity.Year === longItem.ProductionYear
            && longAfter.Identity.RunTimeTicks === longItem.RunTimeTicks && longAfter.Identity.Path === longItem.Path
            && longAfter.Name === longBefore.Name && longAfter.Note === longBefore.Note && longAfter.UpdatedAt === longBefore.UpdatedAt && startup.Identity.Backfill.Updated >= 1,
            { identity: longAfter.Identity, itemName: longAfter.ItemName, backfill: startup.Identity.Backfill });
        const restartText = logSince(restartLog);
        check('restart: the log says "Move journal: 1 bookmarks followed their moved videos" and "Identity backfill: N bookmarks completed"',
            /Move journal: 1 bookmarks followed their moved videos/.test(restartText) && /Identity backfill: \d+ bookmarks completed/.test(restartText),
            restartText.split('\n').filter((l) => /Move journal|Identity backfill|Verify sweep/.test(l)).map((l) => l.replace(/^\[[^\]]*\] /, '').trim()));

        await scan('Blade Runner at its new place');
        const arrived = await dto(onBlade.Id, 'viewer');
        check('after the scan: the movie has the predicted id, and the bookmark is on it, attached', await exists(predicted) && !(await exists(bladeRunner.Id))
            && guid(arrived.ItemId) === predicted && arrived.Detached === false, describe(arrived));

        // Moved back by Library Maintenance: a Move back line, the folder back; the scan relinks through the journal.
        fs.appendFileSync(journalFile, line(2, predicted, bladeRunner.Id, newPath, oldPath) + '\n');
        undoMoves(journalMoves);
        await scan('Blade Runner moved back');
        const home2 = await waitFor('the bookmark back on the old id', async () => { const b = await dto(onBlade.Id, 'viewer'); return !b.Detached && guid(b.ItemId) === guid(bladeRunner.Id) ? b : null; }, 30);
        check('moved back (a Move back line, the folder back, a scan): the bookmark follows through the journal to its old id (Exact: the media source moved back, not marked relinked)',
            guid(home2.MediaSourceId) === guid(bladeRunner.Id) && home2.Relinked === false, describe(home2));

        // The upgraded 1.0 bookmark whose video was gone: detached by the startup sweep, and after the scans above and a
        // re-resolution still not on Remake Story (1990), the one TMDb candidate, whose runtime it can't check.
        const vanishedMaintenance = await runMaintenance('re-resolution of the upgraded bookmark');
        const vanishedDto = await dto(onVanished.Id, 'viewer');
        const vanishedStored = stored(onVanished.Id);
        const vanishedOwner = bookmarksOwner(await diagnostics());
        const vanishedStep = (vanishedMaintenance.Steps || []).find((s) => s.Name === 'ResolveDetached');
        const vanishedResolve = vanishedStep && vanishedStep.Outcome === 'Done'
            ? ((vanishedStep.Result && vanishedStep.Result.Owners) || []).find((o) => o.Owner === 'bookmarks') || null : null;
        check('an upgraded 1.0 bookmark whose video was gone before the start (another id, no runtime): the backfill finds it missing, the startup sweep detaches it, and it stays "Not in library" and Ambiguous, never moved onto the one movie with its TMDb id',
            vanishedIds.Tmdb === '900101' && startup.Identity.Backfill.Missing >= 1
            && vanishedDto.Detached === true && vanishedDto.Relinked === false && guid(vanishedDto.ItemId) === guid(vanished) && guid(vanishedDto.ItemId) !== guid(remakeStory.Id)
            && guid(vanishedDto.MediaSourceId) === guid(vanished) && vanishedDto.AudioStreamIndex === 1
            && !!vanishedStored.DetachedUtc && !vanishedStored.RelinkStrength && !(vanishedStored.Identity.RunTimeTicks > 0)
            && vanishedOwner && vanishedOwner.Ambiguous >= 1 && vanishedResolve && vanishedResolve.Ambiguous >= 1,
            { providerIds: vanishedIds, backfill: startup.Identity.Backfill, api: describe(vanishedDto), stored: describe(vanishedStored),
                detached: vanishedOwner, resolve: vanishedResolve });
    }

    // ---- 7. the daily maintenance
    const daily = await runMaintenance('daily');
    const snapshots = fs.existsSync(path.join(dataRoot, 'bookmarks', 'snapshots'))
        ? fs.readdirSync(path.join(dataRoot, 'bookmarks', 'snapshots')).filter((f) => /^bookmarks-\d{8}-\d{6}(-\d+)?\.json$/.test(f)) : [];
    const snapshotStep = (daily.Steps || []).find((s) => s.Name === 'Snapshots');
    // MissingClips (the missing-clip sweep) is the task's last step since Bookmarks 1.2.0.
    check('the daily "Bookmarks: Maintenance" runs Snapshots, Archive, Users, VerifySweep, ResolveDetached, Reattach, ClipFiles and MissingClips in that order, none failing, and snapshots bookmarks.json',
        daily.Failed === 0 && (daily.Steps || []).map((s) => s.Name).join() === 'Snapshots,Archive,Users,VerifySweep,ResolveDetached,Reattach,ClipFiles,MissingClips'
        && snapshotStep && snapshots.includes(snapshotStep.Result.Snapshot),
        { steps: (daily.Steps || []).map((s) => ({ Name: s.Name, Outcome: s.Outcome, Milliseconds: s.Milliseconds })), snapshot: snapshotStep && snapshotStep.Result, snapshots: snapshots.length });
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { stopped: false, journalWritten: false };
    let startLog = logPosition();
    if (loadMoves().length > 0) {
        undoMoves(0);
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
            if (context.stopped) {
                await startSandbox('clean-up');
                startLog = { file: null, size: 0 };
            }
            if (context.journalWritten) {
                if (fs.existsSync(journalFile)) fs.rmSync(journalFile);
                if (fs.existsSync(journalAside)) fs.renameSync(journalAside, journalFile);
            }
            const changed = loadMoves().length;
            undoMoves(0);
            if (changed > 0) await scan('clean-up: media put back');
            for (const { who, id } of made) await call(who, 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } });
        } catch (error) {
            check('clean-up: media, journal and bookmarks put back', false, String(error));
        }
        const text = logSince(startLog);
        const errors = text.split('\n').filter((l) => /\[ERR\]|\[FTL\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        check('no error of Bookmarks or the kit in the server log during the run', errors.length === 0, errors.slice(0, 5));
        const left = [];
        for (const { who, id } of made) if ((await call(who, 'GET', `/Bookmarks/${id}`)).status !== 404) left.push(id);
        check('clean-up: no media change left, the journal as it was, the probe\'s bookmarks deleted', loadMoves().length === 0 && left.length === 0
            && !fs.existsSync(path.join(mediaRoot, 'Movies', MOVED_FOLDER)) && !fs.existsSync(journalAside), { moves: loadMoves().length, left });
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
