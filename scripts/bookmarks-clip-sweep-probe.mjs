#!/usr/bin/env node
// Bookmarks' missing-clip sweep in a running sandbox: the daily "Bookmarks: Maintenance",
// run through POST /Bookmarks/Admin/Maintenance/Run, marks a clip file gone from a clip folder that is there, clears the
// mark of one that is back, leaves a clip folder that isn't there (an unplugged drive) out whole, never marking, forgetting
// or archiving anything in it even past the retention, checks it again once it is back, and after MissingClipRetentionDays
// (30) moves a deleted bookmark's kept clip into the archive (reason ClipMissing, in the export) and forgets a bookmark's own
// clip, the bookmark staying; no file is ever deleted by it. The lifecycle's clock is moved on with the Debug build's
// DeveloperClockOffsetDays. Also Diagnostics' ClipSweep and ClipRoots entries (the ClipRootsV1 facade's
// answer in a running server). Node 22 built-ins only. Loopback only, never port 8096. The users' tokens come from
// credentials.json and are never printed.
//
//   node scripts/bookmarks-clip-sweep-probe.mjs --sandbox default [--timeout <seconds>] [--out result.json]
//
// It changes only what it puts back: Bookmarks' ClipOutputPath (for two clips, to its work folder's usb-clips, inside the
// sandbox, so the sandbox still starts; that folder stays recorded in ClipRoots, empty afterwards), MissingClipRetentionDays
// (30, the default, set to make sure) and DeveloperClockOffsetDays (0, then 31), all saved in saved-settings.json before the
// first change and put back in a finally (and at the start of the next run after a run was killed); and renames, recorded
// in moves.json before they are made and undone the same ways: four clip files moved into its hold folder held/ (deleted by
// hand, as far as Bookmarks can tell) and usb-clips renamed aside (an unplugged drive). Two of those files belong, at the
// end, to clips Bookmarks has let go (a deleted bookmark's clip archived, a bookmark's clip forgotten): their held copies,
// in held/ only and only once no record names their path, are the files the probe deletes itself (also at the next start
// after a killed run). It leaves one record behind on purpose: the deleted bookmark archived by the retention ("clip-sweep-probe
// Charlie"), since the archive is append-only and only an admin's Purge deletes from it (a Purge would take the sandbox's
// other detached bookmarks with it). Every other bookmark and clip goes through the API; it never writes bookmarks.json or
// the archive, and never restarts or deploys anything. It refuses to run while a clip of the sandbox's that isn't the
// probe's would be marked missing (its file gone from a clip folder that is there) or is marked already: moving the clock
// on would let the retention take it. Every name it gives starts with "clip-sweep-probe". Each maintenance run also takes
// its daily snapshot (SnapshotsToKeep rotates the oldest out), as the daily task does.
//
// The clips (admin's Fast clips of Long Test Movie (2021)):
//   Alpha    a bookmark's clip, its file there throughout: never marked;
//   Bravo    a bookmark's clip, its file held out: marked, then 31 days on its clip entry is forgotten (the bookmark stays);
//   Charlie  a deleted bookmark's kept clip, its file held out: marked, then 31 days on moved into the archive;
//   Delta    a bookmark's clip, its file held out, then put back: marked, then cleared;
//   Echo     a bookmark's clip in the second clip folder, its file there: not checked while the folder is unplugged;
//   Foxtrot  a deleted bookmark's kept clip in the second clip folder, its file held out: marked while the folder is there,
//            then, unplugged, left alone 31 days on (due, never archived), and with its file and folder back, cleared.
// The runs: S1 (clock +0, all there but the held files), S2 (Delta's file back, usb-clips unplugged), S3 (clock +31),
// S4 (Foxtrot's file back, usb-clips plugged in, clock +31).
// Then experiment E1, in a third clip folder of its work folder, e1-drive\clips: Golf and Hotel (bookmarks'
// clips) and India (a deleted bookmark's kept clip) made there, which writes the folder's marker file; the folder renamed
// aside (unplugged); a clip asked for (Juliet) while ClipOutputPath still points there, whose job must fail without making
// the folder again (an earlier build made it again and wrote the clip there); an empty folder made in its place by the probe
// (as that build left it, or an empty mount point), not recognised; the clock 31 days on: nothing marked, forgotten or
// archived; the folder back: every clip there, Juliet's made; then every file of the folder held out at once (the second
// guard: left out, not marked) and put back. The empty stray folders it made are recorded in created.json and removed with
// rmdir (empty folders only), also at the next start after a killed run.
// Then the follow-up cases, in the same folder and in the sandbox's default clip folder:
//   E1-8 to E1-10, the stray clip: the folder unplugged again, a stray folder made in its place and Juliet's file moved
//            into it from the unplugged folder (as Bookmarks 1.1.x's worker left one: it made the folder again and wrote the
//            clip there); the run must leave it out as Unproven, write no marker and mark nothing, refuse a new clip (Kilo)
//            with the remedy named, and 31 days on still forget and archive nothing; then the file back, the stray folder
//            removed, the folder plugged in and judged again.
//   E1-11 to E1-14, the emptied folder confirmed: every file of the folder and its marker file held out (a 1.1 folder
//            emptied by hand): NoMarker, the Clip folder field's FolderUnrecognised warning, Kilo refused; the admin's
//            "This is the clip folder; its clips are gone" (POST /Bookmarks/Admin/ClipFolders/{rootId}/Confirm, the dry run
//            first) writes a new marker with a new token and marks the four clips, after a snapshot; Kilo's clip is made;
//            the files back, the marks cleared, and the held copy of the old marker file discarded (the folder has its new
//            one).
//   D1 to D3, the default clip folder deleted: the sandbox's data\bookmarks\clips renamed into the work folder
//            (default-clips-away, recorded as a move): the Clip folder field's FolderDeleted warning, the run makes the folder
//            again with its marker and marks its clips (FoldersMadeAgain), a clip asked for (Lima) is made there, not refused;
//            then Lima deleted, the folder Bookmarks made set aside (default-clips-remade: only its marker file and empty
//            folders, or the probe stops), the real one renamed back, and the run gives it a new marker (its old token is
//            another folder's now) and clears every mark. The made-again folder's marker file is the third kind of file
//            the probe deletes itself, in default-clips-remade only.
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const TICKS = 10_000_000;
const PROBE = 'clip-sweep-probe';
const UNPLUGGED = '.clip-sweep-probe-unplugged';
const DAY = 24 * 3600 * 1000;

function usage() {
    return 'usage: node scripts/bookmarks-clip-sweep-probe.mjs --sandbox <name> [--timeout <seconds>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, timeout: 300, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!Number.isFinite(o.timeout) || o.timeout <= 0) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const workRoot = path.join(sandboxRoot, 'bookmarks-clip-sweep-probe');
const usbRoot = path.join(workRoot, 'usb-clips');
const e1Root = path.join(workRoot, 'e1-drive', 'clips');
const heldRoot = path.join(workRoot, 'held');
const movesFile = path.join(workRoot, 'moves.json');
const createdFile = path.join(workRoot, 'created.json');
const savedSettingsFile = path.join(workRoot, 'saved-settings.json');
const MARKER = '.jfbm-clip-root';
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
if (!logDir.startsWith(sandboxRoot + path.sep)) throw new Error(`refusing to read the log folder ${logDir}: not in the sandbox`);
const dataRoot = path.join(sandboxRoot, 'data', 'data', 'bookmarks');
const storeFile = path.join(dataRoot, 'bookmarks.json');
const archiveFile = path.join(dataRoot, 'archive', 'bookmarks-archived.jsonl');
const clipRoot = path.join(dataRoot, 'clips');
const snapshotsRoot = path.join(dataRoot, 'snapshots');
// The D runs: the sandbox's default clip folder "deleted" (renamed here), and the one Bookmarks makes
// again in its place, set aside here before the real one goes back.
const defaultAway = path.join(workRoot, 'default-clips-away');
const defaultRemade = path.join(workRoot, 'default-clips-remade');
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
if (!users.admin || !users.admin.token || !users.admin.id) throw new Error('no admin in credentials.json; run Initialize-Sandbox.ps1');
const pluginGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8')) || [])[1];
if (!pluginGuid) throw new Error('no guid in plugins/Bookmarks/build.yaml');
const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error(`refusing to work in ${p}`);
    return full;
}
for (const p of [storeFile, archiveFile, clipRoot, workRoot, usbRoot, e1Root, heldRoot, defaultAway, defaultRemade, snapshotsRoot]) assertInside(p, sandboxRoot);

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, configuration: state.Configuration, time: new Date().toISOString() }, runs: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// Jellyfin writes GUIDs without dashes ("N"); bookmarks.json and the archive write them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const lower = (p) => path.resolve(p).toLowerCase();

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (Array.isArray(v)) for (const each of v) url.searchParams.append(k, String(each));
        else if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="bookmarks-clip-sweep-probe", Device="sandbox", DeviceId="bookmarks-clip-sweep-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = typeof body === 'string' ? body : JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(180000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text };
}

async function ok(who, method, p, options) {
    const r = await call(who, method, p, options);
    if (r.status < 200 || r.status > 299) throw new Error(`${method} ${p}: ${r.status} ${r.text.slice(0, 300)}`);
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

// ------------------------------------------------------------------------------------------ undoable changes

function loadMoves() {
    return fs.existsSync(movesFile) ? readJson(movesFile) : [];
}

function saveMoves(moves) {
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(movesFile, JSON.stringify(moves, null, 2));
}

// A rename inside the sandbox's clip folders and the probe's own folders only, recorded before it is made; and the sandbox's
// default clip folder itself into defaultAway (the D runs, key 'default'), nothing else.
function move(key, from, to) {
    const src = path.resolve(from);
    const dst = path.resolve(to);
    const wholeDefault = key === 'default' && src === clipRoot && dst === defaultAway;
    for (const p of wholeDefault ? [] : [src, dst]) {
        if (!(p.startsWith(clipRoot + path.sep) || p.startsWith(workRoot + path.sep))) throw new Error(`refusing to move ${p}: not in the sandbox's clip folders or the probe's folder`);
    }
    if (!fs.existsSync(src)) throw new Error(`${src} doesn't exist`);
    if (fs.existsSync(dst)) throw new Error(`${dst} exists already`);
    const moves = loadMoves();
    if (moves.some((m) => m.key === key)) throw new Error(`a move ${key} is recorded already`);
    moves.push({ key, from: src, to: dst });
    saveMoves(moves);
    fs.mkdirSync(path.dirname(dst), { recursive: true });
    fs.renameSync(src, dst);
}

// bookmarks.json as it is (read only). A sandbox without a bookmark yet has no file: an empty document (once the
// probe crashed there with ENOENT).
function readStore() {
    return fs.existsSync(storeFile) ? readJson(storeFile) : { Bookmarks: [], ClipRoots: [] };
}

// The clip paths bookmarks.json names now (read only).
function referencedPaths() {
    return new Set((readStore().Bookmarks || []).filter((b) => b && b.Clip && b.Clip.FilePath).map((b) => lower(b.Clip.FilePath)));
}

// The empty folders the probe made itself (E1's stray folder in place of the unplugged one), recorded before they are made
// and removed with rmdir, which refuses a folder that isn't empty, so a file in one stops the run (fix by hand).
function loadCreated() {
    return fs.existsSync(createdFile) ? readJson(createdFile) : [];
}

function createEmptyFolder(folder) {
    const full = assertInside(folder, workRoot);
    if (fs.existsSync(full)) throw new Error(`${full} exists already`);
    const created = loadCreated();
    created.push(full);
    fs.writeFileSync(createdFile, JSON.stringify(created, null, 2));
    fs.mkdirSync(full, { recursive: true });
}

function removeCreated() {
    for (const folder of [...loadCreated()].reverse()) {
        // The folder and the empty folders above it that the recursive mkdir made, up to (not including) a recorded move's
        // folder or the work folder.
        let current = assertInside(folder, workRoot);
        const stop = new Set([lower(workRoot), lower(path.dirname(e1Root))]);
        while (fs.existsSync(current) && !stop.has(lower(current))) {
            fs.rmdirSync(current);
            current = path.dirname(current);
        }
        fs.writeFileSync(createdFile, JSON.stringify(loadCreated().filter((f) => f !== folder), null, 2));
    }
}

// The probe's own copy, in its hold folder, of a clip file Bookmarks has let go of (no record names its path any more: a
// deleted bookmark's clip archived, a bookmark's clip forgotten): the one kind of file the probe deletes itself.
function discardHeldCopy({ from, to }) {
    const held = assertInside(to, heldRoot);
    if (path.dirname(held) !== heldRoot || !fs.statSync(held).isFile() || !/\.(mkv|mp4)$/i.test(held)) throw new Error(`refusing to delete ${held}: not a held clip file`);
    if (referencedPaths().has(lower(from))) throw new Error(`refusing to delete ${held}: a record still names ${from}`);
    fs.rmSync(held);
}

// The folders Bookmarks made itself in the D phase (the default clip folder made again, set aside in defaultRemade) hold
// nothing but its marker file and empty folders; anything else stops the probe (fix by hand).
function assertOnlyMarker(folder) {
    for (const entry of fs.readdirSync(folder, { withFileTypes: true, recursive: true })) {
        const full = path.join(entry.parentPath ?? entry.path, entry.name);
        if (entry.isDirectory()) continue;
        if (!(entry.isFile() && entry.name === MARKER && path.dirname(full) === folder)) throw new Error(`refusing to discard ${folder}: it holds ${full}; fix by hand`);
    }
}

// The default clip folder Bookmarks made again, set aside in defaultRemade: its marker file deleted, its folders removed with
// rmdir (each must be empty), deepest first.
function discardRemade() {
    if (!fs.existsSync(defaultRemade)) return;
    const folder = assertInside(defaultRemade, workRoot);
    assertOnlyMarker(folder);
    const marker = path.join(folder, MARKER);
    if (fs.existsSync(marker)) fs.rmSync(marker);
    const folders = fs.readdirSync(folder, { withFileTypes: true, recursive: true }).filter((e) => e.isDirectory())
        .map((e) => path.join(e.parentPath ?? e.path, e.name)).sort((a, b) => b.length - a.length);
    for (const current of [...folders, folder]) fs.rmdirSync(current);
}

// Puts one recorded move back: the file or folder goes back, or, for a held clip file no record names any more, its held
// copy goes; a held marker file whose folder has a marker again (the admin's confirmation wrote a new one) is discarded too.
// A file goes back only into a folder that is there (never into the path of a folder that is unplugged, which would make a
// new folder there). The default clip folder goes back once the one Bookmarks made in its place is set aside.
function undoOne(m) {
    if (m.key === 'default' && fs.existsSync(m.to) && fs.existsSync(m.from)) {
        if (fs.existsSync(defaultRemade)) throw new Error(`${defaultRemade} is there already; fix by hand`);
        assertOnlyMarker(m.from);
        fs.renameSync(m.from, defaultRemade);
    }
    if (fs.existsSync(m.to) && !fs.existsSync(m.from)) {
        if (path.dirname(path.resolve(m.to)) === heldRoot && !referencedPaths().has(lower(m.from)) && path.basename(m.to) !== MARKER) {
            discardHeldCopy(m);
        } else {
            const unplugged = loadMoves().find((u) => u !== m && lower(m.from).startsWith(lower(u.from) + path.sep));
            if (unplugged && fs.existsSync(unplugged.to)) throw new Error(`cannot put ${m.from} back while its clip folder is unplugged; plug it in first`);
            fs.mkdirSync(path.dirname(m.from), { recursive: true });
            fs.renameSync(m.to, m.from);
        }
    } else if (fs.existsSync(m.to) && path.resolve(m.to) === path.join(heldRoot, MARKER) && fs.existsSync(m.from)) {
        discardHeldMarker(m);
    } else if (fs.existsSync(m.to)) {
        throw new Error(`cannot undo ${m.to} → ${m.from}: both exist; fix by hand`);
    }
    saveMoves(loadMoves().filter((x) => x.key !== m.key));
}

// Undoes one recorded move (key) or all of them (latest first). A file the probe moved into a stray folder it made goes back
// first, whichever key is asked for, and then the stray folders go, so the real folder can come back. Returns whether the
// default clip folder was put back (the next maintenance run clears its clips' marks).
function undoMoves(key = null) {
    for (const m of [...loadMoves()].reverse()) {
        if (loadCreated().some((c) => lower(m.to).startsWith(lower(c) + path.sep))) undoOne(m);
    }
    removeCreated();
    let restoredDefault = false;
    let moves = loadMoves();
    for (const m of [...moves].reverse()) {
        if (key !== null && m.key !== key) continue;
        restoredDefault = restoredDefault || m.key === 'default';
        undoOne(m);
        moves = loadMoves();
    }
    discardRemade();
    return restoredDefault;
}

// The held copy of a clip folder's old marker file (E1-11), once the folder has a marker again: the admin's confirmation
// wrote a new one with a new token, and the old one would name a token that is no longer the folder's.
function discardHeldMarker({ from, to }) {
    const held = assertInside(to, heldRoot);
    if (held !== path.join(heldRoot, MARKER) || !fs.statSync(held).isFile() || !fs.existsSync(from)) throw new Error(`refusing to delete ${held}: not a held marker file whose folder has a new one`);
    fs.rmSync(held);
}

async function getConfiguration() {
    return ok('admin', 'GET', CONFIGURATION_PATH);
}

async function setConfiguration(patch) {
    const current = await getConfiguration();
    const r = await call('admin', 'POST', CONFIGURATION_PATH, { body: { ...current, ...patch } });
    if (r.status !== 204) throw new Error(`POST ${CONFIGURATION_PATH}: ${r.status}`);
}

// The settings are saved to a file before they change, so a killed run's next start puts them back.
async function restoreSavedSettings() {
    if (!fs.existsSync(savedSettingsFile)) return false;
    await setConfiguration(readJson(savedSettingsFile));
    fs.rmSync(savedSettingsFile);
    return true;
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

// ------------------------------------------------------------------------------------------ bookmarks, clips, maintenance

const made = [];

async function createBookmark(item, { start, end, name }) {
    const body = { ItemId: item.Id, StartTicks: start * TICKS, EndTicks: end * TICKS, Name: name };
    const r = await call('admin', 'POST', '/Bookmarks', { body });
    if (r.status !== 201 || !r.json) throw new Error(`POST /Bookmarks on ${item.Name}: ${r.status} ${r.text.slice(0, 200)}`);
    made.push(r.json.Id);
    return r.json;
}

async function makeClip(bookmark) {
    const r = await call('admin', 'POST', `/Bookmarks/${bookmark.Id}/Clip`, { body: { Mode: 'Fast' } });
    if (r.status !== 202) throw new Error(`POST /Bookmarks/${bookmark.Id}/Clip: ${r.status} ${r.text.slice(0, 200)}`);
    return waitFor(`the clip of ${bookmark.Id}`, async () => {
        const read = await call('admin', 'GET', `/Bookmarks/${bookmark.Id}`);
        const status = read.json && read.json.Clip && read.json.Clip.Status;
        if (status === 'Completed') return read.json;
        if (status && !['Queued', 'Running'].includes(status)) throw new Error(`the clip of ${bookmark.Id} ended ${status}: ${read.json.Clip.Error}`);
        return null;
    }, opts.timeout);
}

// A record as bookmarks.json has it (read only).
function stored(id) {
    return (readStore().Bookmarks || []).find((b) => guid(b.Id) === guid(id)) || null;
}

// The archive's folded records (read only): the last Archived line of each id not followed by Reattached or Superseded.
function archived() {
    if (!fs.existsSync(archiveFile)) return [];
    const current = new Map();
    for (const line of fs.readFileSync(archiveFile, 'utf8').split('\n')) {
        if (!line.trim()) continue;
        let entry;
        try { entry = JSON.parse(line); } catch { continue; }
        if (entry.V !== 1) continue;
        if (entry.Op === 'Archived' && entry.Id) current.set(guid(entry.Id), entry);
        else if ((entry.Op === 'Reattached' || entry.Op === 'Superseded') && Array.isArray(entry.Ids)) for (const id of entry.Ids) current.delete(guid(id));
    }
    return [...current.values()];
}

async function diagnostics() {
    return ok('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
}

async function maintenanceTask() {
    const tasks = await ok('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } });
    return (tasks || []).find((t) => t.Key === 'BookmarksMaintenance') || null;
}

// "Run the daily maintenance now" (POST /Bookmarks/Admin/Maintenance/Run, 202), waited for through the task's last result;
// then the sweep's step and Diagnostics' ClipSweep entry.
async function runMaintenance(label) {
    const started = Date.now();
    const queued = await call('admin', 'POST', '/Bookmarks/Admin/Maintenance/Run');
    if (queued.status !== 202) throw new Error(`POST /Bookmarks/Admin/Maintenance/Run: ${queued.status} ${queued.text.slice(0, 200)}`);
    await delay(500);
    for (;;) {
        const now = await maintenanceTask();
        const last = now && now.LastExecutionResult;
        if (now && now.State === 'Idle' && last && Date.parse(last.EndTimeUtc) >= started - 1000) {
            const d = await diagnostics();
            const run = d.Maintenance && d.Maintenance.LastRun;
            const sweepStep = ((run && run.Steps) || []).find((s) => s.Name === 'MissingClips') || null;
            const sweep = d.ClipSweep && d.ClipSweep.LastRun;
            evidence.runs.push({
                label, seconds: Math.round((Date.now() - started) / 100) / 10, status: last.Status, failed: run && run.Failed,
                steps: ((run && run.Steps) || []).map((s) => `${s.Order} ${s.Name} ${s.Outcome}`),
                sweep: sweep && {
                    StartedUtc: sweep.StartedUtc, RetentionDays: sweep.RetentionDays, Checked: sweep.Checked, Present: sweep.Present, Missing: sweep.Missing,
                    Marked: sweep.Marked, Cleared: sweep.Cleared, InProgress: sweep.InProgress, FolderUnavailable: sweep.FolderUnavailable, MarkersWritten: sweep.MarkersWritten,
                    UnavailableFolders: sweep.UnavailableFolders, Unreadable: sweep.Unreadable, Refused: sweep.Refused, Changed: sweep.Changed,
                    Archived: sweep.Archived, ClipsForgotten: sweep.ClipsForgotten, Error: sweep.Error
                }
            });
            if (last.Status !== 'Completed') throw new Error(`the maintenance "${label}" ended ${last.Status}`);
            await delay(1100); // snapshot names have whole seconds
            return { run, sweepStep, sweep, diagnostics: d };
        }
        if (Date.now() - started > 120000) throw new Error(`the maintenance "${label}" did not finish`);
        await delay(500);
    }
}

async function clips(query = {}) {
    const r = await call('admin', 'GET', '/Bookmarks/Clips', { query: { searchTerm: PROBE, limit: 200, ...query } });
    if (r.status !== 200 || !r.json) throw new Error(`GET /Bookmarks/Clips: ${r.status} ${r.text.slice(0, 300)}`);
    return r.json;
}

const missingSince = (id) => { const b = stored(id); return b && b.Clip ? (b.Clip.MissingSinceUtc ?? null) : undefined; };
const offline = (sweep) => ((sweep && sweep.UnavailableFolders) || []).find((f) => lower(f.Folder) === lower(usbRoot)) || null;

// Clip records of the sandbox that aren't the probe's and that the sweep would mark (their file gone from a clip folder that
// is there), or that are marked already: with the clock moved on, the retention would archive or forget them.
function othersAtStake() {
    const document = readStore();
    const roots = (document.ClipRoots || []).map((r) => path.resolve(r));
    const rootOf = (p) => roots.filter((r) => lower(p).startsWith(lower(r) + path.sep)).sort((x, y) => y.length - x.length)[0] || null;
    return (document.Bookmarks || []).filter((b) => b && b.Clip && b.Clip.FilePath && !String(b.Name || '').startsWith(PROBE))
        .filter((b) => !['Queued', 'Running'].includes(b.Clip.Status))
        .filter((b) => {
            if (b.Clip.MissingSinceUtc) return true;
            const root = rootOf(b.Clip.FilePath);
            return root !== null && fs.existsSync(root) && fs.statSync(root).isDirectory() && !fs.existsSync(b.Clip.FilePath);
        })
        .map((b) => ({ id: b.Id, marked: !!b.Clip.MissingSinceUtc, deleted: !!b.DeletedUtc }));
}

// ------------------------------------------------------------------------------------------ the run

async function run(context) {
    if (state.Configuration !== 'Debug') throw new Error(`the sandbox runs a ${state.Configuration} build: the retention check needs the Debug build's DeveloperClockOffsetDays`);
    const atStake = othersAtStake();
    if (atStake.length > 0) throw new Error(`refusing to run: ${atStake.length} clip(s) of the sandbox that aren't the probe's would be marked missing or are marked already, and the retention would take them once the clock moves on: ${JSON.stringify(atStake)}`);

    const config = await getConfiguration();
    if (config.DeveloperClockOffsetDays === undefined || config.MissingClipRetentionDays === undefined) throw new Error('DeveloperClockOffsetDays or MissingClipRetentionDays is missing: deploy a build with the missing-clip sweep');
    context.savedSettings = { ClipOutputPath: config.ClipOutputPath ?? '', MissingClipRetentionDays: config.MissingClipRetentionDays, DeveloperClockOffsetDays: config.DeveloperClockOffsetDays };
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(savedSettingsFile, JSON.stringify(context.savedSettings));
    await setConfiguration({ MissingClipRetentionDays: 30, DeveloperClockOffsetDays: 0 });

    // ---- the clips
    const movies = await ok('admin', 'GET', '/Items', { query: { userId: users.admin.id, Recursive: 'true', IncludeItemTypes: 'Movie', Fields: 'Path', EnableImages: 'false', EnableUserData: 'false' } });
    const longTest = movies.Items.filter((m) => typeof m.Path === 'string' && m.Path.split(/[\\/]/).includes('Long Test Movie (2021)'));
    if (longTest.length !== 1) throw new Error(`expected one Long Test Movie, found ${longTest.length}; run Initialize-Sandbox.ps1 on fresh media`);
    const movie = longTest[0];
    const a = await createBookmark(movie, { start: 10, end: 14, name: `${PROBE} Alpha` });
    const b = await createBookmark(movie, { start: 20, end: 24, name: `${PROBE} Bravo` });
    const c = await createBookmark(movie, { start: 30, end: 34, name: `${PROBE} Charlie` });
    const d = await createBookmark(movie, { start: 40, end: 44, name: `${PROBE} Delta` });
    const e = await createBookmark(movie, { start: 50, end: 54, name: `${PROBE} Echo` });
    const f = await createBookmark(movie, { start: 60, end: 64, name: `${PROBE} Foxtrot` });
    context.ids = { a: a.Id, b: b.Id, c: c.Id, d: d.Id, e: e.Id, f: f.Id };
    evidence.bookmarks = {
        a: 'a bookmark\'s clip, its file there throughout', b: 'a bookmark\'s clip, its file held out: marked, then forgotten 31 days on',
        c: 'a deleted bookmark\'s kept clip, its file held out: marked, then archived 31 days on', d: 'a bookmark\'s clip, its file held out, then put back: marked, then cleared',
        e: 'a bookmark\'s clip in usb-clips: not checked while it is unplugged', f: 'a deleted bookmark\'s kept clip in usb-clips, its file held out: marked, then left alone while unplugged (due), then cleared with its file and folder back'
    };
    for (const x of [a, b, c, d]) await makeClip(x);
    fs.mkdirSync(usbRoot, { recursive: true });
    await setConfiguration({ ClipOutputPath: usbRoot });
    await makeClip(e);
    await makeClip(f);
    await setConfiguration({ ClipOutputPath: context.savedSettings.ClipOutputPath });

    const files = Object.fromEntries(Object.entries(context.ids).map(([k, id]) => [k, stored(id).Clip.FilePath]));
    context.files = files;
    for (const k of ['c', 'f']) {
        const r = await call('admin', 'DELETE', `/Bookmarks/${context.ids[k]}`, { query: { deleteClip: 'false' } });
        if (r.status !== 204) throw new Error(`DELETE /Bookmarks/${context.ids[k]}?deleteClip=false: ${r.status}`);
    }
    const before = Object.fromEntries(Object.entries(context.ids).map(([k, id]) => [k, stored(id).UpdatedAt]));
    const inClips = ['a', 'b', 'c', 'd'].every((k) => lower(files[k]).startsWith(lower(clipRoot) + path.sep) && fs.existsSync(files[k]));
    const inUsb = ['e', 'f'].every((k) => lower(files[k]).startsWith(lower(usbRoot) + path.sep) && fs.existsSync(files[k]));
    check('set-up: four Fast clips in the sandbox\'s clip folder, Echo\'s and Foxtrot\'s in usb-clips (recorded in ClipRoots); Charlie and Foxtrot deleted with their clips kept; the clip folder setting put back; the retention 30 days, the clock +0',
        inClips && inUsb && stored(c.Id).DeletedUtc && stored(f.Id).DeletedUtc
        && (readJson(storeFile).ClipRoots || []).some((r) => lower(r) === lower(usbRoot))
        && ((await getConfiguration()).ClipOutputPath ?? '') === context.savedSettings.ClipOutputPath,
        { inClips, inUsb });

    // ---- S1: Bravo's, Charlie's, Delta's and Foxtrot's files held out (deleted by hand, as far as Bookmarks can tell)
    for (const k of ['b', 'c', 'd', 'f']) move(k, files[k], path.join(heldRoot, path.basename(files[k])));
    const s1 = await runMaintenance('S1: four files gone, every clip folder there');
    const marks1 = Object.fromEntries(Object.keys(context.ids).map((k) => [k, missingSince(context.ids[k])]));
    evidence.s1 = { marks: marks1, step: s1.sweepStep && s1.sweepStep.Outcome, unavailable: s1.sweep && s1.sweep.UnavailableFolders };
    check('S1: the maintenance ran its last step MissingClips (after ClipFiles) without a failure; the sweep\'s run is Diagnostics\' ClipSweep entry',
        s1.run && s1.run.Failed === 0 && s1.sweepStep && s1.sweepStep.Outcome === 'Done' && s1.run.Steps[s1.run.Steps.length - 1].Name === 'MissingClips'
        && s1.sweep && !s1.sweep.Skipped && !s1.sweep.Error && s1.sweep.RetentionDays === 30, evidence.runs[evidence.runs.length - 1]);
    check('S1: the four files gone from a clip folder that is there are marked missing (Bravo, Charlie, Delta, Foxtrot); Alpha and Echo, there, aren\'t',
        ['b', 'c', 'd', 'f'].every((k) => typeof marks1[k] === 'string') && marks1.a === null && marks1.e === null
        && s1.sweep.Marked >= 4 && s1.sweep.Missing >= 4 && !offline(s1.sweep), evidence.s1);
    const listed1 = Object.fromEntries((await clips()).Items.map((r) => [Object.keys(context.ids).find((k) => guid(context.ids[k]) === guid(r.Id)), r.Clip.State]));
    evidence.s1.list = listed1;
    check('S1: the clip list agrees: Bravo, Charlie, Delta and Foxtrot Missing, Alpha and Echo Ready',
        same(listed1, { ...listed1, a: 'Ready', b: 'Missing', c: 'Missing', d: 'Missing', e: 'Ready', f: 'Missing' }) && Object.keys(listed1).length === 6, listed1);

    // ---- S2: Delta's file put back; usb-clips unplugged
    undoMoves('d');
    move('usb', usbRoot, usbRoot + UNPLUGGED);
    const s2 = await runMaintenance('S2: Delta\'s file back, usb-clips unplugged');
    const marks2 = Object.fromEntries(Object.keys(context.ids).map((k) => [k, missingSince(context.ids[k])]));
    evidence.s2 = { marks: marks2, unavailable: s2.sweep && s2.sweep.UnavailableFolders };
    check('S2: Delta\'s file is back: its mark cleared; Bravo\'s and Charlie\'s marks as S1 wrote them',
        marks2.d === null && marks2.b === marks1.b && marks2.c === marks1.c && s2.sweep.Cleared >= 1, evidence.s2);
    check('S2: usb-clips unplugged: left out whole, its two clips neither checked nor marked (Echo still unmarked, Foxtrot\'s mark as it was), and Diagnostics names the folder ("Clip folder unavailable") with its 2 clips',
        offline(s2.sweep) && offline(s2.sweep).Clips === 2 && marks2.e === null && marks2.f === marks1.f && s2.sweep.FolderUnavailable >= 2, evidence.s2);

    // ---- S3: 31 days on (the Debug build's lifecycle clock)
    await setConfiguration({ DeveloperClockOffsetDays: 31 });
    const s3 = await runMaintenance('S3: the clock 31 days on');
    const afterB = await call('admin', 'GET', `/Bookmarks/${b.Id}`);
    const afterC = await call('admin', 'GET', `/Bookmarks/${c.Id}/Clip/File`);
    const charlie = archived().find((x) => guid(x.Id) === guid(c.Id)) || null;
    const exported = await call('admin', 'GET', '/Bookmarks/Admin/Export');
    const exportedCharlie = exported.json && (exported.json.Archive || []).find((x) => guid(x.Id) === guid(c.Id));
    const owner = ((s3.diagnostics.Detached && s3.diagnostics.Detached.Owners) || []).find((o) => o.Owner === 'bookmarks') || {};
    const listed3 = (await clips()).Items.map((r) => Object.keys(context.ids).find((k) => guid(context.ids[k]) === guid(r.Id))).sort();
    evidence.s3 = {
        sweep: s3.sweep, bravo: { status: afterB.status, name: afterB.json && afterB.json.Name, clip: afterB.json && afterB.json.Clip ? 'still there' : 'gone' },
        charlie: charlie && { Reason: charlie.Reason, DetachedUtc: charlie.DetachedUtc, Name: charlie.Record && charlie.Record.Name, DeletedUtc: !!(charlie.Record && charlie.Record.DeletedUtc), MissingSinceUtc: charlie.Record && charlie.Record.Clip && charlie.Record.Clip.MissingSinceUtc },
        charlieInStore: !!stored(c.Id), charlieClipFile: afterC.status, exported: !!exportedCharlie, archivedFor: owner.ArchivedFor, listed: listed3,
        foxtrot: { inStore: !!stored(f.Id), mark: missingSince(f.Id) }
    };
    check('S3 (+31 days): Charlie, a deleted bookmark\'s clip missing for 31 days, moved into the archive (reason ClipMissing, its detach time its missing mark, its name kept): gone from the store and the list, in the export\'s Archive, counted by Diagnostics\' Detached entry',
        s3.sweep.Archived >= 1 && charlie && charlie.Reason === 'ClipMissing' && charlie.DetachedUtc === marks1.c && charlie.Record.Name === `${PROBE} Charlie`
        && !stored(c.Id) && afterC.status === 404 && !!exportedCharlie && owner.ArchivedFor && owner.ArchivedFor.ClipMissing >= 1 && !listed3.includes('c'), evidence.s3);
    check('S3: Bravo, a bookmark\'s clip missing for 31 days, forgotten: the bookmark stays (200, its name and UpdatedAt as they were), without its clip entry',
        s3.sweep.ClipsForgotten >= 1 && afterB.status === 200 && afterB.json.Name === `${PROBE} Bravo` && !afterB.json.Clip && stored(b.Id).UpdatedAt === before.b && !listed3.includes('b'), evidence.s3);
    check('S3: usb-clips still unplugged: Foxtrot, marked 31 days ago, is due but stays (the offline folder is left out, nothing archived), Echo too',
        offline(s3.sweep) && offline(s3.sweep).Clips === 2 && stored(f.Id) && missingSince(f.Id) === marks1.f && stored(e.Id) && missingSince(e.Id) === null
        && !archived().some((x) => guid(x.Id) === guid(f.Id)), evidence.s3);
    const heldNow = fs.readdirSync(heldRoot);
    const notDeleted = fs.existsSync(files.a) && fs.existsSync(files.d) && ['b', 'c', 'f'].every((k) => heldNow.includes(path.basename(files[k])))
        && fs.existsSync(path.join(usbRoot + UNPLUGGED, path.relative(usbRoot, files.e)));
    check('S3: no clip file was deleted, moved or renamed by Bookmarks: Alpha\'s and Delta\'s in place, the held ones in the hold folder, Echo\'s on the unplugged drive', notDeleted, { held: heldNow.length });

    // ---- S4: usb-clips plugged in and Foxtrot's file back, still 31 days on (the folder first: the file goes into it)
    undoMoves('usb');
    undoMoves('f');
    const s4 = await runMaintenance('S4: Foxtrot\'s file back, usb-clips plugged in, the clock still +31');
    evidence.s4 = { sweep: s4.sweep, marks: { e: missingSince(e.Id), f: missingSince(f.Id) } };
    check('S4: usb-clips back: checked again, Echo there (no mark), Foxtrot\'s file back so its mark is cleared, never archived; nothing archived or forgotten in this run',
        !offline(s4.sweep) && missingSince(e.Id) === null && missingSince(f.Id) === null && stored(f.Id) && s4.sweep.Archived === 0 && s4.sweep.ClipsForgotten === 0
        && !archived().some((x) => guid(x.Id) === guid(f.Id)), evidence.s4);
    check('S4: UpdatedAt (the users\' IfUpdatedAt token) of every record still there is as it was before the first run',
        ['a', 'b', 'd', 'e', 'f'].every((k) => stored(context.ids[k]).UpdatedAt === before[k]), Object.fromEntries(['a', 'b', 'd', 'e', 'f'].map((k) => [k, stored(context.ids[k]).UpdatedAt === before[k]])));

    // ---- Diagnostics: the facade's answer in a running server
    const facade = s4.diagnostics.ClipRoots || {};
    const roots = (readJson(storeFile).ClipRoots || []).map(lower);
    evidence.facade = { Facade: facade.Facade, Answering: facade.Answering, Calls: facade.Calls, Folders: (facade.Folders || []).length };
    check('ClipRootsV1: Diagnostics\' ClipRoots entry says it answers in the server, the recorded clip folders (usb-clips among them) and the current one',
        facade.Facade === 'Jellyfin.Plugin.Bookmarks.Integration.ClipRootsV1.Get' && facade.Answering === true
        && roots.every((r) => (facade.Folders || []).map(lower).includes(r)) && (facade.Folders || []).map(lower).includes(lower(clipRoot))
        && (facade.Folders || []).map(lower).includes(lower(usbRoot)), evidence.facade);

    await runE1(context, movie);
    await runDefaultFolder(context, movie);
}

// Experiment E1 (above): a clip folder away must never be made again by a job, nor judged by the sweep while
// a folder is in its place that isn't its own; nothing in it is marked, forgotten or archived, also 31 days on.
async function runE1(context, movie) {
    await setConfiguration({ DeveloperClockOffsetDays: 0 });
    fs.mkdirSync(e1Root, { recursive: true });
    await setConfiguration({ ClipOutputPath: e1Root });
    const g = await createBookmark(movie, { start: 70, end: 74, name: `${PROBE} Golf` });
    const h = await createBookmark(movie, { start: 80, end: 84, name: `${PROBE} Hotel` });
    const i = await createBookmark(movie, { start: 90, end: 94, name: `${PROBE} India` });
    const j = await createBookmark(movie, { start: 100, end: 104, name: `${PROBE} Juliet` });
    context.e1 = { g: g.Id, h: h.Id, i: i.Id, j: j.Id };
    for (const x of [g, h, i]) await makeClip(x);
    const deleted = await call('admin', 'DELETE', `/Bookmarks/${i.Id}`, { query: { deleteClip: 'false' } });
    if (deleted.status !== 204) throw new Error(`DELETE /Bookmarks/${i.Id}?deleteClip=false: ${deleted.status}`);
    const files = Object.fromEntries(['g', 'h', 'i'].map((k) => [k, stored(context.e1[k]).Clip.FilePath]));
    context.e1Files = files;
    const marker = path.join(e1Root, MARKER);
    const before = Object.fromEntries(['g', 'h', 'i'].map((k) => [k, stored(context.e1[k])]));
    const e1Folder = (sweep) => ((sweep && sweep.UnavailableFolders) || []).find((f) => lower(f.Folder) === lower(e1Root)) || null;
    const unchanged = () => ['g', 'h', 'i'].every((k) => same(stored(context.e1[k]), before[k]));
    const anyArchived = () => archived().some((x) => ['g', 'h', 'i'].some((k) => guid(x.Id) === guid(context.e1[k])));
    // The marker names a token of its own too, which bookmarks.json records for the folder.
    const markerLines = fs.existsSync(marker) ? fs.readFileSync(marker, 'utf8').trim().split('\n').map((l) => l.trim()) : [];
    const recordedToken = Object.entries(readStore().ClipRootTokens || {}).find(([r]) => lower(r) === lower(e1Root));
    evidence.e1 = {
        markerWritten: fs.existsSync(marker), markerText: markerLines.filter((l) => /^(RootId|Token)=/.test(l)),
        tokenRecorded: !!recordedToken && markerLines.includes('Token=' + recordedToken[1])
    };
    check('E1 set-up: Golf, Hotel and India (deleted, its clip kept) made in e1-drive\\clips, which got its marker file (.jfbm-clip-root) with the first clip, its token recorded; ClipOutputPath points there',
        ['g', 'h', 'i'].every((k) => lower(files[k]).startsWith(lower(e1Root) + path.sep) && fs.existsSync(files[k])) && stored(i.Id).DeletedUtc
        && evidence.e1.markerWritten && evidence.e1.markerText.length === 2 && /^RootId=[0-9a-f]{12}$/.test(evidence.e1.markerText[0])
        && /^Token=[0-9a-f]{32}$/.test(evidence.e1.markerText[1]) && evidence.e1.tokenRecorded
        && (readStore().ClipRoots || []).some((r) => lower(r) === lower(e1Root)), evidence.e1);

    // E1-1: the folder unplugged.
    move('e1', e1Root, e1Root + UNPLUGGED);
    const r1 = await runMaintenance('E1-1: e1-drive\\clips unplugged');
    evidence.e1.away = e1Folder(r1.sweep);
    check('E1-1: e1-drive\\clips unplugged: left out whole (NotThere, its 3 clips), nothing marked',
        e1Folder(r1.sweep) && e1Folder(r1.sweep).Clips === 3 && e1Folder(r1.sweep).Reason === 'NotThere' && unchanged(), evidence.e1.away);

    // E1-2: a clip asked for meanwhile, into the folder that is away (an earlier build made the folder again and wrote it there).
    const asked = await askClip(j);
    evidence.e1.juliet = { status: asked.Clip && asked.Clip.Status, error: asked.Clip && asked.Clip.Error, folderMadeAgain: fs.existsSync(e1Root) };
    check('E1-2: Juliet\'s clip, asked for while the folder is away: its job fails ("The clip folder isn\'t available right now …"), and the folder isn\'t made again',
        asked.Clip && asked.Clip.Status === 'Failed' && /^The clip folder isn't available right now/.test(asked.Clip.Error || '') && !fs.existsSync(e1Root), evidence.e1.juliet);
    const r2 = await runMaintenance('E1-2: after the refused job');
    check('E1-2: the next run still leaves the folder out (NotThere), nothing marked', e1Folder(r2.sweep) && e1Folder(r2.sweep).Reason === 'NotThere' && r2.sweep.Marked === 0 && unchanged(), e1Folder(r2.sweep));

    // E1-3: a folder in its place anyway: empty, as an earlier build's job left it (or an empty mount point).
    createEmptyFolder(path.join(e1Root, 'admin'));
    const r3 = await runMaintenance('E1-3: an empty folder in place of the unplugged one');
    const again = await askClip(j);
    evidence.e1.stray = { folder: e1Folder(r3.sweep), juliet: { status: again.Clip && again.Clip.Status, error: again.Clip && again.Clip.Error }, entries: fs.readdirSync(e1Root, { recursive: true }) };
    check('E1-3: a folder there in its place, without the marker file and without any of its clips: not recognised (NoMarker), nothing marked; Juliet\'s job fails again, writing nothing there',
        e1Folder(r3.sweep) && e1Folder(r3.sweep).Reason === 'NoMarker' && e1Folder(r3.sweep).Clips === 3 && r3.sweep.Marked === 0 && r3.sweep.MarkersWritten === 0 && unchanged()
        && again.Clip && again.Clip.Status === 'Failed' && /^The clip folder doesn't look like the one its clips were made in/.test(again.Clip.Error || '')
        && same(fs.readdirSync(e1Root, { recursive: true }), ['admin']), evidence.e1.stray);

    // E1-4: 31 days on.
    await setConfiguration({ DeveloperClockOffsetDays: 31 });
    const r4 = await runMaintenance('E1-4: the clock 31 days on, the stray folder still in place');
    evidence.e1.later = { folder: e1Folder(r4.sweep), ClipsForgotten: r4.sweep.ClipsForgotten, Archived: r4.sweep.Archived };
    check('E1-4 (+31 days): nothing in the folder forgotten or archived: Golf and Hotel keep their clip entries, India its record, none in the archive',
        e1Folder(r4.sweep) && e1Folder(r4.sweep).Reason === 'NoMarker' && unchanged() && !anyArchived() && stored(g.Id).Clip && stored(h.Id).Clip && stored(i.Id), evidence.e1.later);

    // E1-5: the drive back (the stray folder removed first): every clip there; Juliet's clip made now.
    undoMoves('e1');
    const r5 = await runMaintenance('E1-5: e1-drive\\clips plugged in, the clock still +31');
    const made5 = await makeClip(j);
    files.j = stored(j.Id).Clip.FilePath;
    evidence.e1.back = { folder: e1Folder(r5.sweep), present: ['g', 'h', 'i'].filter((k) => fs.existsSync(files[k])).length, juliet: made5.Clip.Status };
    check('E1-5: the folder back: all three files there, none marked, nothing forgotten or archived, every record as before; Juliet\'s clip is made into it now',
        !e1Folder(r5.sweep) && unchanged() && !anyArchived() && evidence.e1.back.present === 3 && made5.Clip.Status === 'Completed'
        && lower(files.j).startsWith(lower(e1Root) + path.sep) && fs.existsSync(files.j), evidence.e1.back);

    // E1-6: the second guard: every file of the (marked) folder gone at once.
    for (const k of ['g', 'h', 'i', 'j']) move(`e1${k}`, files[k], path.join(heldRoot, path.basename(files[k])));
    const r6 = await runMaintenance('E1-6: every file of e1-drive\\clips held out at once');
    evidence.e1.allGone = e1Folder(r6.sweep);
    check('E1-6: every clip of the folder gone at once (4, none marked before): left out (AllMissing), none marked',
        e1Folder(r6.sweep) && e1Folder(r6.sweep).Reason === 'AllMissing' && e1Folder(r6.sweep).Clips === 4
        && ['g', 'h', 'i', 'j'].every((k) => missingSince(context.e1[k]) === null), evidence.e1.allGone);
    for (const k of ['j', 'i', 'h', 'g']) undoMoves(`e1${k}`);
    const r7 = await runMaintenance('E1-7: the files back');
    check('E1-7: the files back: the folder judged again, all four there, none marked',
        !e1Folder(r7.sweep) && ['g', 'h', 'i', 'j'].every((k) => missingSince(context.e1[k]) === null && fs.existsSync(files[k])), { present: r7.sweep && r7.sweep.Present });

    await runStrayClip(context, movie, { files, e1Folder, anyArchived });
    await runEmptiedConfirm(context, { files, e1Folder });
    await setConfiguration({ DeveloperClockOffsetDays: 0, ClipOutputPath: context.savedSettings.ClipOutputPath });
}

// The stray clip in a running server (above, E1-8 to E1-10): a stray folder holding a clip an older version wrote
// there while the real folder was away is not proven by it.
async function runStrayClip(context, movie, { files, e1Folder, anyArchived }) {
    const keys = ['g', 'h', 'i', 'j'];
    const before = Object.fromEntries(keys.map((k) => [k, stored(context.e1[k])]));
    const unchanged = () => keys.every((k) => same(stored(context.e1[k]), before[k]));
    const token = (readStore().ClipRootTokens || {})[Object.keys(readStore().ClipRootTokens || {}).find((r) => lower(r) === lower(e1Root))];
    move('e1', e1Root, e1Root + UNPLUGGED);
    createEmptyFolder(path.dirname(files.j));
    move('e1stray', path.join(e1Root + UNPLUGGED, path.relative(e1Root, files.j)), files.j);
    const r8 = await runMaintenance('E1-8: a stray folder in place of the unplugged one, holding only Juliet\'s file');
    const kilo = await createBookmark(movie, { start: 110, end: 114, name: `${PROBE} Kilo` });
    context.e1.k = kilo.Id;
    const refused = await askClip(kilo);
    evidence.e1.strayClip = {
        folder: e1Folder(r8.sweep), Marked: r8.sweep.Marked, MarkersWritten: r8.sweep.MarkersWritten, markerInStray: fs.existsSync(path.join(e1Root, MARKER)),
        entries: fs.readdirSync(e1Root, { recursive: true }).sort(), kilo: { status: refused.Clip && refused.Clip.Status, error: refused.Clip && refused.Clip.Error }
    };
    check('E1-8: the stray folder holding only Juliet\'s file isn\'t proven by it: left out (Unproven, its 4 clips), no marker written, nothing marked; Kilo\'s clip refused with the remedy named, nothing written there',
        e1Folder(r8.sweep) && e1Folder(r8.sweep).Reason === 'Unproven' && e1Folder(r8.sweep).Clips === 4 && e1Folder(r8.sweep).Confirmable === true
        && r8.sweep.Marked === 0 && r8.sweep.MarkersWritten === 0 && !evidence.e1.strayClip.markerInStray && unchanged()
        && refused.Clip && refused.Clip.Status === 'Failed' && /^The clip folder isn't recognised as the one its clips were made in \(its marker file is missing and some of its clips are gone\)/.test(refused.Clip.Error || '')
        && /your server admin can confirm the folder on the Bookmarks settings page's Diagnostics tab/i.test(refused.Clip.Error || '')
        && same(evidence.e1.strayClip.entries, ['admin', path.join('admin', path.basename(files.j))].sort()), evidence.e1.strayClip);

    // 31 days more: still nothing forgotten or archived (an earlier build forgot two clips and archived India here).
    await setConfiguration({ DeveloperClockOffsetDays: 62 });
    const r9 = await runMaintenance('E1-9: the clock 62 days on, the stray folder still in place');
    evidence.e1.strayLater = { folder: e1Folder(r9.sweep), ClipsForgotten: r9.sweep.ClipsForgotten, Archived: r9.sweep.Archived, Marked: r9.sweep.Marked };
    check('E1-9 (+62 days): the stray folder still Unproven; nothing marked, forgotten or archived; Golf\'s, Hotel\'s and India\'s files intact on the unplugged drive',
        e1Folder(r9.sweep) && e1Folder(r9.sweep).Reason === 'Unproven' && r9.sweep.Marked === 0 && unchanged() && !anyArchived()
        && ['g', 'h', 'i'].every((k) => fs.existsSync(path.join(e1Root + UNPLUGGED, path.relative(e1Root, files[k])))), evidence.e1.strayLater);

    // The file back, the stray folder gone, the real folder plugged in: judged by its marker (the token recorded for it).
    undoMoves('e1');
    const r10 = await runMaintenance('E1-10: e1-drive\\clips plugged in again');
    check('E1-10: the real folder back: judged by its own marker (the token recorded for it unchanged), all four there, none marked',
        !e1Folder(r10.sweep) && unchanged() && r10.sweep.MarkersWritten === 0 && typeof token === 'string' && /^[0-9a-f]{32}$/.test(token)
        && fs.readFileSync(path.join(e1Root, MARKER), 'utf8').includes('Token=' + token), { token: !!token, present: r10.sweep.Present });
}

// The emptied folder in a running server (above, E1-11 to E1-14): a folder emptied by hand, its marker too, confirmed by
// the admin.
async function runEmptiedConfirm(context, { files, e1Folder }) {
    await setConfiguration({ DeveloperClockOffsetDays: 0 });
    const keys = ['g', 'h', 'i', 'j'];
    const rootId = (folder) => folder && folder.RootId;
    const oldToken = tokenOf(e1Root);
    for (const k of keys) move(`e1${k}`, files[k], path.join(heldRoot, path.basename(files[k])));
    move('e1marker', path.join(e1Root, MARKER), path.join(heldRoot, MARKER));
    const r11 = await runMaintenance('E1-11: every file of e1-drive\\clips and its marker file held out (emptied by hand)');
    const refused = await askClip({ Id: context.e1.k });
    const field = ((r11.diagnostics.ClipStorage || {}).Warnings || []).find((w) => w.Code === 'FolderUnrecognised') || null;
    evidence.e1.emptied = { folder: e1Folder(r11.sweep), field, kilo: { status: refused.Clip && refused.Clip.Status, error: refused.Clip && refused.Clip.Error } };
    check('E1-11: a folder emptied by hand, its marker too: NoMarker (4 clips, confirmable, its RootId), the Clip folder field\'s FolderUnrecognised warning with the RootId, Kilo refused with the remedy named',
        e1Folder(r11.sweep) && e1Folder(r11.sweep).Reason === 'NoMarker' && e1Folder(r11.sweep).Clips === 4 && e1Folder(r11.sweep).Confirmable === true && /^[0-9a-f]{12}$/.test(rootId(e1Folder(r11.sweep)) || '')
        && field && field.RootId === rootId(e1Folder(r11.sweep)) && /confirm the folder/.test(field.Message)
        && refused.Clip && refused.Clip.Status === 'Failed' && /^The clip folder doesn't look like the one its clips were made in/.test(refused.Clip.Error || '')
        && /your server admin can confirm the folder/i.test(refused.Clip.Error || '') && keys.every((k) => missingSince(context.e1[k]) === null), evidence.e1.emptied);

    // The admin's confirmation: the dry run, then the confirmation itself (after a snapshot).
    const id = rootId(e1Folder(r11.sweep));
    const confirmPath = `/Bookmarks/Admin/ClipFolders/${id}/Confirm`;
    const preOp = () => (fs.existsSync(path.join(snapshotsRoot, 'pre-op')) ? fs.readdirSync(path.join(snapshotsRoot, 'pre-op')).filter((f) => f.startsWith('bookmarks-other-')) : []);
    const snapshotsBefore = preOp().length;
    const dry = await call('admin', 'POST', confirmPath);
    const viewer = await call(Object.keys(users).find((u) => u !== 'admin') || 'anonymous', 'POST', confirmPath, { query: { dryRun: 'false' } });
    const unknown = await call('admin', 'POST', '/Bookmarks/Admin/ClipFolders/0123456789ab/Confirm', { query: { dryRun: 'false' } });
    const done = await call('admin', 'POST', confirmPath, { query: { dryRun: 'false' } });
    const after = await diagnostics();
    const newToken = tokenOf(e1Root);
    evidence.e1.confirm = {
        dry: dry.json, viewer: viewer.status, unknown: unknown.status, done: done.json, snapshots: preOp().length - snapshotsBefore,
        marks: Object.fromEntries(keys.map((k) => [k, missingSince(context.e1[k])])), tokenChanged: !!newToken && newToken !== oldToken,
        stillListed: !!(((after.ClipSweep || {}).LastRun || {}).UnavailableFolders || []).find((f) => lower(f.Folder) === lower(e1Root))
    };
    check('E1-12: the dry run counts the 4 clips it would mark and changes nothing; a user is refused (403), an unknown id is 404; the confirmation writes a new marker (a new token, recorded), marks the 4 clips missing, after a snapshot, and the folder leaves Diagnostics\' list',
        dry.status === 200 && dry.json.DryRun === true && dry.json.Marked === 4 && dry.json.Existed === true && dry.json.Present === 0
        && (viewer.status === 403 || viewer.status === 401) && unknown.status === 404
        && done.status === 200 && done.json.DryRun === false && done.json.Marker === 'Written' && done.json.Marked === 4
        && keys.every((k) => typeof evidence.e1.confirm.marks[k] === 'string') && evidence.e1.confirm.tokenChanged
        && evidence.e1.confirm.snapshots >= 1 && !evidence.e1.confirm.stillListed, evidence.e1.confirm);

    // A clip asked for now is made into the confirmed folder.
    const made = await makeClip({ Id: context.e1.k });
    files.k = stored(context.e1.k).Clip.FilePath;
    check('E1-13: Kilo\'s clip is made into the confirmed folder now',
        made.Clip.Status === 'Completed' && lower(files.k).startsWith(lower(e1Root) + path.sep) && fs.existsSync(files.k), { status: made.Clip.Status });

    // The files back (the old marker's held copy discarded: the folder has its new one): the marks cleared.
    for (const k of ['j', 'i', 'h', 'g']) undoMoves(`e1${k}`);
    undoMoves('e1marker');
    const r14 = await runMaintenance('E1-14: the files back');
    check('E1-14: the files back: judged by the new marker, the four marks cleared, nothing archived; the old marker\'s held copy discarded, the new one in place',
        !e1Folder(r14.sweep) && r14.sweep.Cleared >= 4 && keys.every((k) => missingSince(context.e1[k]) === null && fs.existsSync(files[k]))
        && tokenOf(e1Root) === newToken && !fs.existsSync(path.join(heldRoot, MARKER)), { cleared: r14.sweep.Cleared });
}

// The emptied-folder case for the default clip folder (above, D1 to D3), at the clock +0 so no mark is ever due.
async function runDefaultFolder(context, movie) {
    await setConfiguration({ DeveloperClockOffsetDays: 0, ClipOutputPath: '' });
    const inDefault = () => (readStore().Bookmarks || []).filter((b) => b && b.Clip && b.Clip.FilePath && lower(b.Clip.FilePath).startsWith(lower(clipRoot) + path.sep) && !['Queued', 'Running'].includes(b.Clip.Status));
    const before = inDefault().map((b) => b.Id);
    const oldToken = tokenOf(clipRoot);
    move('default', clipRoot, defaultAway);
    const away = await diagnostics();
    const deletedWarning = ((away.ClipStorage || {}).Warnings || []).find((w) => w.Code === 'FolderDeleted') || null;
    const d1 = await runMaintenance('D1: the sandbox\'s default clip folder deleted');
    const marked = inDefault().filter((b) => before.includes(b.Id) && b.Clip.MissingSinceUtc).length;
    const madeAgain = await diagnostics();
    evidence.d1 = {
        clips: before.length, deletedWarning: deletedWarning && deletedWarning.Message, FoldersMadeAgain: d1.sweep.FoldersMadeAgain, MarkersWritten: d1.sweep.MarkersWritten,
        Marked: d1.sweep.Marked, markedNow: marked, leftOut: (d1.sweep.UnavailableFolders || []).find((f) => lower(f.Folder) === lower(clipRoot)) || null,
        fieldAfter: ((madeAgain.ClipStorage || {}).Warnings || []).map((w) => w.Code), token: !!tokenOf(clipRoot) && tokenOf(clipRoot) !== oldToken
    };
    check('D1: the default clip folder deleted: the Clip folder field says FolderDeleted; the run makes it again with its marker (FoldersMadeAgain 1, a new token) and marks every clip that was in it, leaving nothing out; no warning once it is there',
        before.length >= 2 && deletedWarning && /makes it again and marks its clips missing/.test(deletedWarning.Message)
        && d1.sweep.FoldersMadeAgain === 1 && d1.sweep.MarkersWritten >= 1 && marked === before.length && !evidence.d1.leftOut && evidence.d1.token
        && !evidence.d1.fieldAfter.includes('FolderDeleted') && !evidence.d1.fieldAfter.includes('FolderUnavailable'), evidence.d1);

    // A clip asked for now is made there: no user's clips are refused.
    const lima = await createBookmark(movie, { start: 120, end: 124, name: `${PROBE} Lima` });
    const madeLima = await makeClip(lima);
    const limaFile = stored(lima.Id).Clip.FilePath;
    evidence.d2 = { status: madeLima.Clip.Status, inDefault: lower(limaFile).startsWith(lower(clipRoot) + path.sep) };
    check('D2: Lima\'s clip is made into the default folder made again, not refused',
        madeLima.Clip.Status === 'Completed' && evidence.d2.inDefault && fs.existsSync(limaFile), evidence.d2);
    const removed = await call('admin', 'DELETE', `/Bookmarks/${lima.Id}`, { query: { deleteClip: 'true' } });
    if (removed.status !== 204 || fs.existsSync(limaFile)) throw new Error(`DELETE /Bookmarks/${lima.Id}?deleteClip=true: ${removed.status}`);

    // The real folder back: its old marker's token isn't the folder's any more, but every clip of it is there, so the run
    // gives it a new marker and clears every mark.
    undoMoves('default');
    const d3 = await runMaintenance('D3: the sandbox\'s default clip folder put back');
    const stillMarked = inDefault().filter((b) => b.Clip.MissingSinceUtc).length;
    evidence.d3 = { Cleared: d3.sweep.Cleared, MarkersWritten: d3.sweep.MarkersWritten, stillMarked, remadeLeft: fs.existsSync(defaultRemade), leftOut: (d3.sweep.UnavailableFolders || []).length };
    check('D3: the real folder back (the one Bookmarks made set aside and discarded): proven by its clips, given a new marker, every mark cleared; the sandbox\'s own clips unmarked',
        d3.sweep.MarkersWritten >= 1 && d3.sweep.Cleared >= before.length && stillMarked === 0 && !evidence.d3.remadeLeft && othersAtStake().length === 0
        && before.every((id) => stored(id) && stored(id).Clip && fs.existsSync(stored(id).Clip.FilePath)), evidence.d3);
    await setConfiguration({ ClipOutputPath: context.savedSettings.ClipOutputPath });
}

// The token a clip folder's marker file names (read only), or null.
function tokenOf(folder) {
    const marker = path.join(folder, MARKER);
    if (!fs.existsSync(marker)) return null;
    const line = fs.readFileSync(marker, 'utf8').split('\n').map((l) => l.trim()).find((l) => l.startsWith('Token='));
    return line ? line.slice('Token='.length).toLowerCase() : null;
}

// Asks for a clip and waits for its job to end, whatever it ends as.
async function askClip(bookmark) {
    const before = (stored(bookmark.Id) && stored(bookmark.Id).Clip) ? stored(bookmark.Id).Clip.RequestedAt : null;
    const r = await call('admin', 'POST', `/Bookmarks/${bookmark.Id}/Clip`, { body: { Mode: 'Fast' } });
    if (r.status !== 202) throw new Error(`POST /Bookmarks/${bookmark.Id}/Clip: ${r.status} ${r.text.slice(0, 200)}`);
    return waitFor(`the clip job of ${bookmark.Id}`, async () => {
        const read = await call('admin', 'GET', `/Bookmarks/${bookmark.Id}`);
        const clip = read.json && read.json.Clip;
        return clip && !['Queued', 'Running'].includes(clip.Status) && stored(bookmark.Id).Clip.RequestedAt !== before ? read.json : null;
    }, opts.timeout);
}

async function deleteLeftovers() {
    // Bookmarks of a run that was killed: through the API, once their files are back.
    const deleted = [];
    const query = await call('admin', 'GET', '/Bookmarks/Query', { query: { searchTerm: PROBE, limit: 200 } });
    for (const x of (query.json && query.json.Items) || []) {
        if (!String(x.Name || '').startsWith(PROBE)) continue;
        const r = await call('admin', 'DELETE', `/Bookmarks/${x.Id}`, { query: { deleteClip: 'true' } });
        deleted.push({ id: x.Id, status: r.status });
    }
    for (const x of (await clips()).Items) {
        if (!String(x.Name || '').startsWith(PROBE)) continue;
        const r = await call('admin', 'DELETE', `/Bookmarks/${x.Id}/Clip`);
        deleted.push({ id: x.Id, clip: r.status });
    }
    return deleted;
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { savedSettings: null, ids: null, files: null, e1: null, e1Files: null };
    const startLog = logPosition();
    evidence.leftovers = { moves: loadMoves().length, settings: fs.existsSync(savedSettingsFile) };
    const restoredDefault = undoMoves();
    await restoreSavedSettings();
    if (restoredDefault) {
        // A run killed in the D phase left the default clip folder's clips marked: the folder is back, so a run clears them.
        await runMaintenance('start: the default clip folder put back');
    }

    evidence.leftovers.bookmarks = await deleteLeftovers();
    made.length = 0;
    let failure = null;
    try {
        await run(context);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        const cleanup = { settingsRestored: null, deleted: [] };
        try {
            cleanup.settingsRestored = await restoreSavedSettings();
            undoMoves();
            for (const id of made) {
                const removed = await call('admin', 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } });
                const clip = await call('admin', 'DELETE', `/Bookmarks/${id}/Clip`);
                cleanup.deleted.push({ id, bookmark: removed.status, clip: clip.status });
            }
        } catch (error) {
            check('clean-up: the settings, the folder, the files and the bookmarks put back', false, String(error));
        }
        evidence.cleanup = cleanup;
        const text = logSince(startLog);
        const lines = text.split('\n');
        const errors = lines.filter((l) => /\[ERR\]|\[FTL\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        const naming = lines.filter((l) => /\[(INF|WRN|ERR|FTL)\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l)
            && (l.includes(PROBE) || l.toLowerCase().includes(lower(usbRoot)) || l.toLowerCase().includes(lower(e1Root))));
        const sweepLines = lines.filter((l) => /\[INF\]/.test(l) && l.includes('Missing clip files:'));
        evidence.log = { sweepLines: sweepLines.length, sample: (sweepLines[sweepLines.length - 1] || '').replace(/^.*?\] /, '').slice(0, 400) };
        check('the log: no Error of Bookmarks or the kit during the run; the sweep\'s count line at Information at each run; no line of theirs at Information or above naming a probe bookmark or an unplugged folder',
            errors.length === 0 && naming.length === 0 && sweepLines.length >= 18, { errors: errors.slice(0, 5), naming: naming.slice(0, 5), sweepLines: sweepLines.length });
        const left = [];
        for (const id of made) if ((await call('admin', 'GET', `/Bookmarks/${id}`)).status !== 404) left.push(id);
        const filesLeft = [...Object.values(context.files || {}), ...Object.values(context.e1Files || {})].filter((p) => p && fs.existsSync(p));
        const heldLeft = fs.existsSync(heldRoot) ? fs.readdirSync(heldRoot) : [];
        const config = await getConfiguration();
        const saved = context.savedSettings || {};
        check('clean-up: no move left, nothing in the hold folder, usb-clips, e1-drive\\clips and the default clip folder in place, no stray or made-again folder left, the settings as they were (the clock +0), the probe\'s bookmarks and clip files gone (Charlie stays archived, as the archive keeps it)',
            loadMoves().length === 0 && heldLeft.length === 0 && fs.existsSync(usbRoot) && !fs.existsSync(usbRoot + UNPLUGGED) && !fs.existsSync(savedSettingsFile)
            && !fs.existsSync(e1Root + UNPLUGGED) && loadCreated().length === 0
            && fs.existsSync(clipRoot) && !fs.existsSync(defaultAway) && !fs.existsSync(defaultRemade)
            && config.DeveloperClockOffsetDays === (saved.DeveloperClockOffsetDays ?? 0) && config.MissingClipRetentionDays === (saved.MissingClipRetentionDays ?? 30)
            && (config.ClipOutputPath ?? '') === (saved.ClipOutputPath ?? '') && left.length === 0 && filesLeft.length === 0,
            { moves: loadMoves().length, held: heldLeft.length, created: loadCreated().length, left, filesLeft: filesLeft.length, clock: config.DeveloperClockOffsetDays });
    }
    const pass = !failure && checks.every((x) => x.pass);
    const result = { pass, passed: checks.filter((x) => x.pass).length, total: checks.length, checks, evidence };
    const output = JSON.stringify(result, null, 2);
    if (opts.out) {
        fs.mkdirSync(path.dirname(path.resolve(opts.out)), { recursive: true });
        fs.writeFileSync(opts.out, output);
    }
    console.log(output);
    process.exit(pass ? 0 : 1);
}

await main();
