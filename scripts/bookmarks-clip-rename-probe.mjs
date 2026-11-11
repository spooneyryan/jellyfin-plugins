#!/usr/bin/env node
// Bookmarks' clip renames (My Clips' Rename and Rename file) in a running
// sandbox: POST /Bookmarks/Clips/{id}/Name and /RenameFile over real clip files, and the My Clips page's dialogs in the
// web client (desktop and TV). Node 22 built-ins only. Loopback only, never port 8096. The users' tokens come from
// credentials.json and are never printed.
//
//   node scripts/bookmarks-clip-rename-probe.mjs --sandbox default [--skip-restart] [--skip-webchecks] [--timeout <seconds>] [--out result.json]
//
// It seeds admin's bookmarks and Fast clips of Long Test Movie (2021) through the API, every name starting with
// "clip-rename-probe", and viewer2's one. What it changes and puts back, as bookmarks-clips-page-probe.mjs
// does: Bookmarks' ClipOutputPath, set for one clip each to its work folder's usb and stray folders (inside the sandbox, so
// the sandbox still starts; they stay recorded in ClipRoots), and VideoPreset, set to slow while one long clip is made, each
// saved to saved-settings.json there before the change and put back right after, in a finally and at the start of the next
// run after a run was killed (withSettings); the renames it makes itself, recorded in moves.json before they are made and
// undone the same ways: usb and stray renamed aside (an unplugged drive), and, for the crash simulation below, one clip file
// moved to its new name as a rename leaves it (a move recorded as `adopted`: once a record names its new path, Bookmarks
// has taken it over, and it is dropped instead of undone); the empty folder it makes in place of stray (recorded in
// created.json first, removed with rmdir, which refuses a folder that isn't empty, before any move is undone); and a clip
// file held open from a PowerShell process of its own while it asks for the rename, and while page-clips-rename.json runs
// (read, sharing read and write but not delete, as a preview or a download in another tab holds it; the path through the
// environment). It deletes no file but its saved-settings file. It never writes bookmarks.json (only reads it, for a
// clip's path and its UpdatedAt) and never deploys anything.
//
// The crash simulation (a stop between a rename's move and its record's write), unless --skip-restart: the
// sandbox stopped through Stop-Sandbox.ps1; two journal entries written into Bookmarks' data folder as the server writes
// them before it moves a file (clip-rename-<id>.json: the record, both paths, the file's size and times, the name the
// rename gives), each recorded in planted.json before it is written: Crashed's file then moved to its new name (the move
// made, the record never written), NotMoved's left where it is (the stop before the move); the sandbox started again
// through dev-server.ps1 -Background -NoBuild. Bookmarks finishes both at its start; the probe never deletes an entry.
//
// Two more checks (5a and 5b), each through the API only: a restore after renames (an export read, the
// clip renamed with its file, the export imported back with Replace, the clip renamed again, the import's own safety
// snapshot restored: each time the record names the renamed file, never its old path; then deleted with its clip, no file
// left), and a clip named with an emoji at its name's 80th character (made by the worker and ffmpeg, then renamed: the file's
// name holds no half of the emoji, the record names a file that is there, also after the restart of the crash simulation).
// The restore and the import change nothing but this run's own records: the export is the data read moments before, and the
// snapshot restored is the one the import took of the data just before it.
//
// Checks:
//   1. the seed (admin): Name, Both, Collide and Target (one range), Case, Bad, Held, Making (a long range), Outdated
//      (renamed afterwards on My Bookmarks: its file keeps its name), Kept (its bookmark deleted keeping the
//      clip), Offline (in usb, unplugged), Stray (in stray, unplugged with an empty folder in its place: there, but not the
//      clips' own), Crashed and NotMoved, Web A to Web E for the webchecks; viewer2's Other;
//   2. the name only: 200, the name changed, the file the same, "Name changed"; the name and the file: 200, the file
//      renamed to the name Bookmarks gives the clip now, the old path gone, the response's file name, no path in it;
//   3. a taken name numbered (Collide renamed to Target's name: " (2)", Target's file untouched); a change of case renamed on
//      disk; names that aren't safe in a file name (Windows' invalid characters, a device name, trailing dots) giving safe
//      ones; a name over 100 characters 400 with nothing changed;
//   4. Rename file after the bookmark was renamed on My Bookmarks: the file gets the clip's name, its UpdatedAt kept; a
//      deleted bookmark's kept clip renamed with its file; another user's clip 404 (viewer2's, asked by admin, and admin's
//      asked by viewer2);
//   5. refusals with nothing changed (bookmarks.json byte for byte, every file where it was): the file held open 409 "in
//      use" after the server's three attempts, then renamed once released; a clip being made 409 "Wait for the clip to
//      finish or cancel it first." (then cancelled); a folder offline 503; a folder there but not recognised (its clip's
//      file gone) 503; IfUpdatedAt stale 409;
//   5a. a restore after renames: the import of an export from before a rename (Replace) and the restore of a snapshot from
//      before a second one each name the renamed file ("ClipsRelinked"), the clip Ready; its delete leaves no file;
//   5b. an emoji at the name's 80th character: the clip made and renamed under a name without half of it, the file there;
//   6. page-clips-rename.json (admin, Web D's file held open meanwhile) and 7. page-clips-rename-tv.json (admin, the TV
//      layout), unless --skip-webchecks;
//   8. the crash simulation (unless --skip-restart): after the start, Crashed's record names its renamed file with the
//      rename's name, NotMoved's record as it was, both entries gone, the start's log line "Clip file renames that weren't
//      finished: 1 finished …, 1 not moved"; the emoji clip of 5b still naming a file that is there after the start;
//   9. no Error of Bookmarks or the kit in the log, and no line of theirs at Information or above naming a probe bookmark;
//      the clean-up: the settings, the folders and the files as they were, the probe's bookmarks and clips gone, no journal
//      entry of its left.
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const TICKS = 10_000_000;
const PROBE = 'clip-rename-probe';
const UNPLUGGED = '.clip-rename-probe-unplugged';
const IN_USE = 'The clip file is in use (being played or downloaded); try again shortly.';
const IN_PROGRESS = 'Wait for the clip to finish or cancel it first.';

function usage() {
    return 'usage: node scripts/bookmarks-clip-rename-probe.mjs --sandbox <name> [--skip-restart] [--skip-webchecks] [--timeout <seconds>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { timeout: 180, restart: true, webchecks: true };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--skip-restart') o.restart = false;
        else if (a === '--skip-webchecks') o.webchecks = false;
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`unknown argument ${a}\n${usage()}`);
    }
    if (!o.sandbox || !/^[A-Za-z0-9_-]+$/.test(o.sandbox)) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const workRoot = path.join(sandboxRoot, 'bookmarks-clip-rename-probe');
const usbRoot = path.join(workRoot, 'usb');
const strayRoot = path.join(workRoot, 'stray');
const movesFile = path.join(workRoot, 'moves.json');
const createdFile = path.join(workRoot, 'created.json');
const plantedFile = path.join(workRoot, 'planted.json');
const savedSettingsFile = path.join(workRoot, 'saved-settings.json');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
let state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port}: not a sandbox's`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
const dataFolder = path.join(sandboxRoot, 'data', 'data', 'bookmarks');
const storeFile = path.join(dataFolder, 'bookmarks.json');
const clipRoot = path.join(dataFolder, 'clips');
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const who of ['admin', 'viewer2']) if (!users[who] || !users[who].token) throw new Error(`no ${who} in credentials.json: run Initialize-Sandbox.ps1`);
const pluginGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8')) || [])[1];
if (!pluginGuid) throw new Error('no guid in plugins/Bookmarks/build.yaml');
const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error(`refusing to work in ${p}`);
    return full;
}
for (const p of [dataFolder, storeFile, clipRoot, workRoot, logDir]) assertInside(p, sandboxRoot);
for (const p of [usbRoot, strayRoot, movesFile, createdFile, plantedFile, savedSettingsFile]) assertInside(p, workRoot);

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// Jellyfin writes GUIDs without dashes ("N"); bookmarks.json writes them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const N = (name) => `${PROBE} ${name}`;

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="bookmarks-clip-rename-probe", Device="sandbox", DeviceId="bookmarks-clip-rename-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(180000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text, detail: json && json.detail };
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

// A rename inside the sandbox's clip folders and the probe's own folders only, recorded before it is made. `adopted`: a
// clip file moved as a rename moves it, which Bookmarks takes over once a record names its new path (discardAdopted).
function move(from, to, adopted = false) {
    const src = path.resolve(from);
    const dst = path.resolve(to);
    for (const p of [src, dst]) {
        if (!(p.startsWith(clipRoot + path.sep) || p.startsWith(workRoot + path.sep))) throw new Error(`refusing to move ${p}: not in the sandbox's clip folders or the probe's folder`);
    }
    if (!fs.existsSync(src)) throw new Error(`${src} doesn't exist`);
    if (fs.existsSync(dst)) throw new Error(`${dst} exists already`);
    const moves = loadMoves();
    moves.push(adopted ? { from: src, to: dst, adopted: true } : { from: src, to: dst });
    saveMoves(moves);
    fs.renameSync(src, dst);
}

function undoMoves() {
    let moves = loadMoves();
    while (moves.length > 0) {
        const { from, to } = moves[moves.length - 1];
        if (fs.existsSync(to) && !fs.existsSync(from)) {
            fs.renameSync(to, from);
        } else if (fs.existsSync(to)) {
            throw new Error(`cannot undo ${to} → ${from}: both exist; fix by hand`);
        }
        moves = moves.slice(0, -1);
        saveMoves(moves);
    }
}

// A move recorded as adopted whose new path a record names now: Bookmarks finished the rename it stands for, so it is kept
// as it is (undoing it would leave the record naming a path that is gone).
function discardAdopted() {
    const referenced = new Set((fs.existsSync(storeFile) ? readJson(storeFile).Bookmarks || [] : [])
        .map((b) => b.Clip && b.Clip.FilePath).filter(Boolean).map((p) => path.resolve(p).toLowerCase()));
    const kept = [];
    const adopted = [];
    for (const m of loadMoves()) {
        if (m.adopted && referenced.has(path.resolve(m.to).toLowerCase())) {
            adopted.push(path.basename(m.to));
            continue;
        }

        kept.push(m);
    }

    saveMoves(kept);
    return adopted;
}

function loadCreated() {
    return fs.existsSync(createdFile) ? readJson(createdFile) : [];
}

function makeFolder(folder) {
    const dir = path.resolve(folder);
    if (!dir.startsWith(workRoot + path.sep)) throw new Error(`refusing to make ${dir}: not in the probe's folder`);
    if (fs.existsSync(dir)) throw new Error(`${dir} exists already`);
    const created = loadCreated();
    created.push(dir);
    fs.writeFileSync(createdFile, JSON.stringify(created, null, 2));
    fs.mkdirSync(dir);
}

function removeCreated() {
    let created = loadCreated();
    while (created.length > 0) {
        const dir = created[created.length - 1];
        if (fs.existsSync(dir)) fs.rmdirSync(dir);
        created = created.slice(0, -1);
        fs.writeFileSync(createdFile, JSON.stringify(created, null, 2));
    }
}

async function getConfiguration() {
    const r = await call('admin', 'GET', CONFIGURATION_PATH);
    if (r.status !== 200 || !r.json) throw new Error(`GET ${CONFIGURATION_PATH}: ${r.status}`);
    return r.json;
}

async function setConfiguration(patch) {
    const current = await getConfiguration();
    const r = await call('admin', 'POST', CONFIGURATION_PATH, { body: { ...current, ...patch } });
    if (r.status !== 204) throw new Error(`POST ${CONFIGURATION_PATH}: ${r.status}`);
}

async function restoreSavedSettings() {
    if (!fs.existsSync(savedSettingsFile)) return false;
    await setConfiguration(readJson(savedSettingsFile));
    fs.rmSync(savedSettingsFile);
    return true;
}

// Settings changed for one step, saved to a file first and put back right after it, in the finally and at the next start.
async function withSettings(context, patch, step) {
    const current = await getConfiguration();
    context.savedSettings = Object.fromEntries(Object.keys(patch).map((k) => [k, current[k] ?? '']));
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(savedSettingsFile, JSON.stringify(context.savedSettings));
    await setConfiguration(patch);
    try {
        return await step();
    } finally {
        await restoreSavedSettings();
        context.savedSettings = null;
    }
}

// Holds a file open as a download or a preview does (read, sharing read and write but not delete: Kestrel's PhysicalFile
// opens clip files so), from a PowerShell process the probe starts, until the returned release() ends it; the path goes
// through the environment, never a command line.
async function holdOpen(file) {
    const child = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
        "$s = [System.IO.File]::Open($env:BOOKMARKS_PROBE_HOLD, 'Open', 'Read', 'ReadWrite'); [Console]::Out.WriteLine('held'); [Console]::Out.Flush(); [void][Console]::In.ReadLine(); $s.Dispose()"],
    { env: { ...process.env, BOOKMARKS_PROBE_HOLD: file }, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    const exited = new Promise((resolve) => child.on('exit', resolve));
    let out = '';
    let err = '';
    child.stderr.on('data', (d) => { err += d; });
    const held = await Promise.race([
        new Promise((resolve) => child.stdout.on('data', (d) => { out += d; if (out.includes('held')) resolve(true); })),
        exited.then(() => false),
        delay(30000).then(() => false)
    ]);
    if (!held) {
        child.kill();
        throw new Error(`the clip file could not be held open: ${err.trim().slice(0, 300)}`);
    }
    return async () => {
        child.stdin.end('\n');
        const code = await Promise.race([exited, delay(10000).then(() => 'timeout')]);
        if (code === 'timeout') child.kill();
        return code;
    };
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

// A webcheck of scripts/webchecks (`<name>.json`); its result and screenshots are kept in the work folder.
function webcheck(name) {
    const out = path.join(workRoot, `webcheck-${name}.json`);
    const started = Date.now();
    const w = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', `${name}.json`, '--out', out], {
        cwd: repoRoot, encoding: 'utf8', timeout: 1200000, windowsHide: true
    });
    let result = null;
    try { result = readJson(out); } catch { result = null; }
    for (const shot of (result && result.screenshots) || []) {
        if (fs.existsSync(shot)) fs.copyFileSync(shot, path.join(workRoot, path.basename(shot)));
    }
    const failed = result && result.steps ? result.steps.filter((s) => !s.ok).map((s) => ({ index: s.index, name: s.name, error: s.error })) : null;
    const value = (stepName) => (result && result.steps ? (result.steps.find((s) => s.name === stepName) || {}).value : undefined);
    return {
        ok: w.status === 0 && !!result && result.ok === true,
        exit: w.status,
        seconds: Math.round((Date.now() - started) / 1000),
        steps: result && result.steps ? result.steps.length : 0,
        failed,
        failedRequests: result && result.failedRequests ? result.failedRequests.length : null,
        exceptions: result && result.exceptions ? result.exceptions.length : null,
        value
    };
}

// The sandbox stopped and started again, through the repository's scripts only (never another server).
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
    evidence.restarts = evidence.restarts || [];
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

// ------------------------------------------------------------------------------------------ the journal entries planted

// A .NET DateTime of a file's time to the 100 ns (Node's bigint stat), as the server writes it: 2026-10-26T12:34:56.1234567Z.
function dotnetUtc(ns) {
    const ticks = ns / 100n;
    const ms = Number(ticks / 10000n);
    const rest = Number(ticks % 10000n);
    return new Date(ms).toISOString().slice(0, 23) + String(rest).padStart(4, '0') + 'Z';
}

function loadPlanted() {
    return fs.existsSync(plantedFile) ? readJson(plantedFile) : [];
}

// One journal entry as the server writes it before a move (Clips/ClipRenameJournal), only while the sandbox is stopped, into
// Bookmarks' data folder, recorded in planted.json first.
function plantEntry(record, to, name) {
    if (running) throw new Error('refusing to write a journal entry while the sandbox runs');
    const from = path.resolve(record.Clip.FilePath);
    for (const p of [from, path.resolve(to)]) {
        if (!p.startsWith(clipRoot + path.sep)) throw new Error(`refusing to name ${p} in a journal entry: not in the sandbox's clip folder`);
    }
    const stat = fs.statSync(from, { bigint: true });
    const entry = {
        BookmarkId: record.Id,
        From: from,
        To: path.resolve(to),
        // The size and the last write time (no creation time, which NTFS tunnels); no file id, which Node
        // reads otherwise than the server (a 32-bit volume serial), so the entry is judged as a file system's without ids.
        File: { Length: Number(stat.size), WrittenUtc: dotnetUtc(stat.mtimeNs) },
        NameChanged: true,
        Name: name,
        UpdatedAt: record.UpdatedAt,
        StartedUtc: new Date().toISOString()
    };
    const file = path.join(dataFolder, `clip-rename-${guid(record.Id)}.json`);
    assertInside(file, dataFolder);
    if (fs.existsSync(file)) throw new Error(`${file} exists already`);
    const planted = loadPlanted();
    planted.push(file);
    fs.writeFileSync(plantedFile, JSON.stringify(planted, null, 2));
    fs.writeFileSync(file, JSON.stringify(entry, null, 2));
    return { file, entry };
}

// ------------------------------------------------------------------------------------------ bookmarks and clips

const made = [];

async function findItem(type, test) {
    const r = await call('admin', 'GET', '/Items', {
        query: { userId: users.admin.id, Recursive: 'true', IncludeItemTypes: type, Fields: 'Path', EnableImages: 'false', EnableUserData: 'false' }
    });
    if (r.status !== 200 || !r.json) throw new Error(`GET /Items: ${r.status}`);
    const found = r.json.Items.filter((i) => typeof i.Path === 'string' && test(i.Path));
    if (found.length !== 1) throw new Error(`expected one ${type} for the seed, found ${found.length}; run Initialize-Sandbox.ps1 on fresh media`);
    return found[0];
}

async function createBookmark(who, item, { start, end = null, name }) {
    const body = { ItemId: item.Id, StartTicks: start * TICKS, EndTicks: end === null ? null : end * TICKS, Name: name };
    const r = await call(who, 'POST', '/Bookmarks', { body });
    if (r.status !== 201 || !r.json) throw new Error(`POST /Bookmarks on ${item.Name}: ${r.status} ${r.text.slice(0, 200)}`);
    made.push({ who, id: r.json.Id });
    return r.json;
}

async function makeClip(who, bookmark, mode = 'Fast') {
    const r = await call(who, 'POST', `/Bookmarks/${bookmark.Id}/Clip`, { body: { Mode: mode } });
    if (r.status !== 202) throw new Error(`POST /Bookmarks/${bookmark.Id}/Clip: ${r.status} ${r.text.slice(0, 200)}`);
    return waitFor(`the clip of ${bookmark.Id}`, async () => {
        const read = await call(who, 'GET', `/Bookmarks/${bookmark.Id}`);
        const status = read.json && read.json.Clip && read.json.Clip.Status;
        if (status === 'Completed') return read.json;
        if (status && !['Queued', 'Running'].includes(status)) throw new Error(`the clip of ${bookmark.Id} ended ${status}: ${read.json.Clip.Error}`);
        return null;
    }, opts.timeout);
}

// A record as bookmarks.json has it (read only).
function stored(id) {
    return (readJson(storeFile).Bookmarks || []).find((b) => guid(b.Id) === guid(id)) || null;
}

// A clip of the user's as GET /Bookmarks/Clips lists it.
async function listed(who, id, name) {
    const r = await call(who, 'GET', '/Bookmarks/Clips', { query: { searchTerm: name || PROBE, limit: 200 } });
    return ((r.json && r.json.Items) || []).find((c) => guid(c.Id) === guid(id)) || null;
}

const rename = (who, id, body) => call(who, 'POST', `/Bookmarks/Clips/${guid(id)}/Name`, { body });
const renameFile = (who, id, body) => call(who, 'POST', `/Bookmarks/Clips/${guid(id)}/RenameFile`, body ? { body } : {});

// Whether a string holds half of a character (a lone surrogate): what the 80-character cut once left.
const hasLoneSurrogate = (text) => /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/.test(text);
const EMOJI = '\u{1F3C6}';
const filesIn = (folder) => (fs.existsSync(folder) ? fs.readdirSync(folder) : []);

// The probe's bookmarks and clips of a failed earlier run (or of this one): the live ones deleted with their clips, then the
// clips kept when one was deleted.
async function deleteLeftovers() {
    const left = [];
    for (const who of ['admin', 'viewer2']) {
        for (let round = 0; round < 10; round++) {
            const r = await call(who, 'GET', '/Bookmarks/Query', { query: { searchTerm: PROBE, limit: 200 } });
            const ids = ((r.json && r.json.Items) || []).filter((b) => (b.Name || '').startsWith(PROBE)).map((b) => b.Id);
            if (ids.length === 0) break;
            await call(who, 'POST', '/Bookmarks/BulkDelete', { body: { Ids: ids, DeleteClips: true } });
            left.push(...ids);
        }
        const kept = await call(who, 'GET', '/Bookmarks/Clips', { query: { searchTerm: PROBE, limit: 200 } });
        for (const c of ((kept.json && kept.json.Items) || []).filter((x) => (x.Name || '').startsWith(PROBE))) {
            const d = await call(who, 'DELETE', `/Bookmarks/${c.Id}/Clip`);
            left.push({ clip: c.Id, status: d.status });
        }
    }
    return left;
}

// ------------------------------------------------------------------------------------------ the run

async function seed(context) {
    const movie = await findItem('Movie', (p) => p.split(/[\\/]/).includes('Long Test Movie (2021)'));
    const s = {};
    const plain = [['Name', 10, 14], ['Both', 16, 20], ['Collide', 22, 26], ['Target', 22, 26], ['Case', 28, 32], ['Bad', 34, 38], ['Held', 40, 44],
        ['Outdated', 46, 50], ['Kept', 52, 56], ['Stale', 58, 62], ['Crashed', 64, 68], ['NotMoved', 70, 74],
        ['Web A', 80, 84], ['Web B', 86, 90], ['Web C', 92, 96], ['Web D', 98, 102], ['Web E', 104, 108]];
    for (const [name, start, end] of plain) {
        s[name] = await createBookmark('admin', movie, { start, end, name: N(name) });
        await makeClip('admin', s[name]);
    }

    s.Making = await createBookmark('admin', movie, { start: 0, end: 175, name: N('Making') });
    s.Other = await createBookmark('viewer2', movie, { start: 12, end: 16, name: N('Other') });
    await makeClip('viewer2', s.Other);

    // Outdated, Web B and Web E renamed on My Bookmarks (their files keep their names); Kept's bookmark deleted
    // keeping its clip.
    for (const name of ['Outdated', 'Web B', 'Web E']) {
        const r = await call('admin', 'POST', `/Bookmarks/${s[name].Id}/Name`, { body: { Name: N(name + ' renamed') } });
        if (r.status !== 200) throw new Error(`POST /Bookmarks/${s[name].Id}/Name: ${r.status}`);
    }
    const kept = await call('admin', 'DELETE', `/Bookmarks/${s.Kept.Id}`, { query: { deleteClip: 'false' } });
    if (kept.status !== 204) throw new Error(`DELETE /Bookmarks/${s.Kept.Id}?deleteClip=false: ${kept.status}`);

    // Offline and Stray: a clip each in a clip folder of the work folder (the setting saved first and put back at once).
    fs.mkdirSync(usbRoot, { recursive: true });
    fs.mkdirSync(strayRoot, { recursive: true });
    await withSettings(context, { ClipOutputPath: usbRoot }, async () => {
        s.Offline = await createBookmark('admin', movie, { start: 110, end: 114, name: N('Offline') });
        await makeClip('admin', s.Offline);
    });
    await withSettings(context, { ClipOutputPath: strayRoot }, async () => {
        s.Stray = await createBookmark('admin', movie, { start: 116, end: 120, name: N('Stray') });
        await makeClip('admin', s.Stray);
    });

    const files = Object.fromEntries(Object.keys(s).filter((k) => k !== 'Making').map((k) => [k, stored(s[k].Id) && stored(s[k].Id).Clip && stored(s[k].Id).Clip.FilePath]));
    context.files = files;
    move(usbRoot, usbRoot + UNPLUGGED);
    move(strayRoot, strayRoot + UNPLUGGED);
    makeFolder(strayRoot);
    return { ids: Object.fromEntries(Object.entries(s).map(([k, v]) => [k, guid(v.Id)])), files: Object.fromEntries(Object.entries(files).map(([k, v]) => [k, v ? path.basename(v) : null])) };
}

async function run(context) {
    // ---- 1. the seed
    const seeded = await seed(context);
    evidence.seed = seeded;
    const ids = seeded.ids;
    const file = (name) => context.files[name];
    const folder = path.dirname(file('Name'));
    check('1. the seed (admin, Long Test Movie, Fast clips): every clip made, the ones in the clip folder in admin\'s folder; Offline\'s folder unplugged, Stray\'s with an empty folder in its place; viewer2\'s Other',
        Object.values(ids).every(Boolean) && Object.entries(context.files).every(([k, f]) => !!f && (['Offline', 'Stray'].includes(k) || fs.existsSync(f)))
        && !fs.existsSync(path.join(usbRoot, path.basename(file('Offline')))) && fs.existsSync(path.join(usbRoot + UNPLUGGED, path.relative(usbRoot, file('Offline'))))
        && fs.existsSync(strayRoot) && !fs.existsSync(file('Stray')), seeded);

    // ---- 2. the name only; the name and the file
    const nameOnly = await rename('admin', ids.Name, { Name: N('Name renamed'), RenameFile: false });
    const both = await rename('admin', ids.Both, { Name: N('Both renamed'), RenameFile: true });
    const bothFile = stored(ids.Both).Clip.FilePath;
    evidence.renamed = {
        nameOnly: { status: nameOnly.status, name: nameOnly.json && nameOnly.json.Name, file: nameOnly.json && nameOnly.json.Clip.FileName, outdated: nameOnly.json && nameOnly.json.Clip.IsNameOutdated, fileKept: fs.existsSync(file('Name')) },
        both: { status: both.status, name: both.json && both.json.Name, file: both.json && both.json.Clip.FileName, outdated: both.json && both.json.Clip.IsNameOutdated, stored: path.basename(bothFile), oldGone: !fs.existsSync(file('Both')), newThere: fs.existsSync(bothFile), noPath: !both.text.toLowerCase().includes(sandboxRoot.toLowerCase().replace(/\\/g, '\\\\')) }
    };
    check('2. the name only: 200, the name changed, the file the same with "Name changed"; the name and the file: 200, the file renamed to "Long Test Movie (2021) - clip-rename-probe Both renamed [00.00.16-00.00.20].mkv", the old path gone, no path in the answer',
        nameOnly.status === 200 && evidence.renamed.nameOnly.name === N('Name renamed') && evidence.renamed.nameOnly.file === path.basename(file('Name')) && evidence.renamed.nameOnly.outdated === true && evidence.renamed.nameOnly.fileKept
        && both.status === 200 && evidence.renamed.both.file === `Long Test Movie (2021) - ${N('Both renamed')} [00.00.16-00.00.20].mkv` && evidence.renamed.both.stored === evidence.renamed.both.file
        && evidence.renamed.both.outdated === false && evidence.renamed.both.oldGone && evidence.renamed.both.newThere && evidence.renamed.both.noPath,
        evidence.renamed);

    // ---- 3. a taken name, a change of case, unsafe names, a name too long
    const targetBytes = fs.statSync(file('Target')).size;
    const collide = await rename('admin', ids.Collide, { Name: N('Target'), RenameFile: true });
    const caseRename = await rename('admin', ids.Case, { Name: N('CASE'), RenameFile: true });
    const caseListing = filesIn(folder).filter((f) => f.toLowerCase().includes('clip-rename-probe case'));
    const unsafe = await rename('admin', ids.Bad, { Name: 'a<b>c:d"e/f\\g|h?i*j...', RenameFile: true });
    const device = await rename('admin', ids.Bad, { Name: 'CON', RenameFile: true });
    const storeBefore = fs.readFileSync(storeFile);
    const tooLong = await rename('admin', ids.Bad, { Name: 'n'.repeat(101), RenameFile: true });
    const tooLongUnchanged = storeBefore.equals(fs.readFileSync(storeFile));

    // Back to a probe name, so a killed run's leftovers are found by it.
    const back = await rename('admin', ids.Bad, { Name: N('Bad'), RenameFile: true });
    evidence.names = {
        collide: { status: collide.status, file: collide.json && collide.json.Clip.FileName, targetKept: fs.existsSync(file('Target')) && fs.statSync(file('Target')).size === targetBytes },
        caseRename: { status: caseRename.status, file: caseRename.json && caseRename.json.Clip.FileName, listing: caseListing },
        unsafe: { status: unsafe.status, file: unsafe.json && unsafe.json.Clip.FileName },
        device: { status: device.status, file: device.json && device.json.Clip.FileName, onDisk: fs.existsSync(stored(ids.Bad).Clip.FilePath) },
        tooLong: { status: tooLong.status, detail: tooLong.detail, unchanged: tooLongUnchanged },
        back: { status: back.status, file: back.json && back.json.Clip.FileName }
    };
    check('3. a taken name numbered (" (2)", the other clip\'s file untouched); a change of case alone renamed on disk; Windows\' invalid characters and trailing dots replaced, a device name inside a clip\'s name kept safe; a name over 100 characters 400 with bookmarks.json unchanged',
        collide.status === 200 && evidence.names.collide.file === `Long Test Movie (2021) - ${N('Target')} [00.00.22-00.00.26] (2).mkv` && evidence.names.collide.targetKept
        && caseRename.status === 200 && evidence.names.caseRename.file === `Long Test Movie (2021) - ${N('CASE')} [00.00.28-00.00.32].mkv` && caseListing.length === 1 && caseListing[0] === evidence.names.caseRename.file
        && unsafe.status === 200 && evidence.names.unsafe.file === 'Long Test Movie (2021) - a_b_c -d_e_f_g_h_i_j [00.00.34-00.00.38].mkv'
        && device.status === 200 && evidence.names.device.file === 'Long Test Movie (2021) - CON [00.00.34-00.00.38].mkv' && evidence.names.device.onDisk
        && tooLong.status === 400 && tooLong.detail === 'Names can be at most 100 characters long.' && evidence.names.tooLong.unchanged
        && back.status === 200 && evidence.names.back.file === path.basename(file('Bad')),
        evidence.names);

    // ---- 4. Rename file after a rename elsewhere; a deleted bookmark's kept clip; another user's clip
    const outdatedBefore = await listed('admin', ids.Outdated, N('Outdated'));
    const updatedBefore = stored(ids.Outdated).UpdatedAt;
    const outdated = await renameFile('admin', ids.Outdated);
    const keptRename = await rename('admin', ids.Kept, { Name: N('Kept renamed'), RenameFile: true });
    const othersByAdmin = await rename('admin', ids.Other, { Name: 'not yours', RenameFile: true });
    const adminsByViewer = await rename('viewer2', ids.Name, { Name: 'not yours' });
    const othersFileByAdmin = await renameFile('admin', ids.Other);
    evidence.others = {
        outdated: { before: outdatedBefore && outdatedBefore.Clip.IsNameOutdated, status: outdated.status, file: outdated.json && outdated.json.Clip.FileName, outdated: outdated.json && outdated.json.Clip.IsNameOutdated, updatedKept: stored(ids.Outdated).UpdatedAt === updatedBefore },
        kept: { status: keptRename.status, deleted: keptRename.json && keptRename.json.BookmarkDeleted, file: keptRename.json && keptRename.json.Clip.FileName },
        othersByAdmin: othersByAdmin.status,
        adminsByViewer: adminsByViewer.status,
        othersFileByAdmin: othersFileByAdmin.status,
        otherKept: fs.existsSync(file('Other')) && stored(ids.Other).Name === N('Other')
    };
    check('4. Rename file after the bookmark was renamed on My Bookmarks: "Name changed" before, the file then named for the clip, its UpdatedAt kept; a deleted bookmark\'s kept clip renamed with its file; another user\'s clip 404 both ways, nothing of it changed',
        evidence.others.outdated.before === true && outdated.status === 200 && evidence.others.outdated.file === `Long Test Movie (2021) - ${N('Outdated renamed')} [00.00.46-00.00.50].mkv`
        && evidence.others.outdated.outdated === false && evidence.others.outdated.updatedKept
        && keptRename.status === 200 && evidence.others.kept.deleted === true && evidence.others.kept.file === `Long Test Movie (2021) - ${N('Kept renamed')} [00.00.52-00.00.56].mkv`
        && othersByAdmin.status === 404 && adminsByViewer.status === 404 && othersFileByAdmin.status === 404 && evidence.others.otherKept,
        evidence.others);

    // ---- 5. refusals with nothing changed
    const refusals = {};
    const unchanged = async (label, ask) => {
        const before = fs.readFileSync(storeFile);
        const r = await ask();
        refusals[label] = { status: r.status, detail: r.detail, unchanged: before.equals(fs.readFileSync(storeFile)) };
        return r;
    };
    context.releaseHeld = await holdOpen(file('Held'));
    try {
        const started = Date.now();
        await unchanged('held', () => rename('admin', ids.Held, { Name: N('Held renamed'), RenameFile: true }));
        refusals.held.ms = Date.now() - started;
        refusals.held.fileKept = fs.existsSync(file('Held'));
    } finally {
        refusals.held.release = await context.releaseHeld();
        context.releaseHeld = null;
    }
    const released = await rename('admin', ids.Held, { Name: N('Held renamed'), RenameFile: true });
    refusals.released = { status: released.status, file: released.json && released.json.Clip.FileName };
    await unchanged('offline', () => rename('admin', ids.Offline, { Name: N('Offline renamed'), RenameFile: true }));
    await unchanged('stray', () => rename('admin', ids.Stray, { Name: N('Stray renamed'), RenameFile: true }));
    const stale = stored(ids.Stale).UpdatedAt;
    await rename('admin', ids.Stale, { Name: N('Stale first'), IfUpdatedAt: stale });
    await unchanged('stale', () => rename('admin', ids.Stale, { Name: N('Stale second'), RenameFile: true, IfUpdatedAt: stale }));
    await withSettings(context, { VideoPreset: 'slow' }, async () => {
        const started = await call('admin', 'POST', `/Bookmarks/${ids.Making}/Clip`, { body: { Mode: 'Accurate' } });
        refusals.makingStarted = started.status;
        await waitFor('Making being made', async () => {
            const r = await call('admin', 'GET', `/Bookmarks/${ids.Making}`);
            return r.json && r.json.Clip && ['Queued', 'Running'].includes(r.json.Clip.Status);
        }, 30);
        await unchanged('making', () => rename('admin', ids.Making, { Name: N('Making renamed'), RenameFile: true }));
        await unchanged('makingFile', () => renameFile('admin', ids.Making));
        refusals.cancelled = (await call('admin', 'POST', `/Bookmarks/${ids.Making}/Clip/Cancel`)).status;
        await waitFor('Making cancelled', async () => {
            const r = await call('admin', 'GET', `/Bookmarks/${ids.Making}`);
            return r.json && r.json.Clip && !['Queued', 'Running'].includes(r.json.Clip.Status);
        }, 60);
    });
    refusals.offlineFileKept = fs.existsSync(path.join(usbRoot + UNPLUGGED, path.relative(usbRoot, file('Offline'))));
    refusals.strayFileKept = fs.existsSync(path.join(strayRoot + UNPLUGGED, path.relative(strayRoot, file('Stray'))));
    evidence.refusals = refusals;
    check('5. refused with bookmarks.json byte for byte and every file where it was: the file held open 409 "in use" after the server\'s three attempts (then renamed once released); a folder offline 503 and one there but not recognised 503, each with its sentence; IfUpdatedAt stale 409; a clip being made 409 "Wait for the clip to finish or cancel it first." for both routes',
        refusals.held.status === 409 && refusals.held.detail === IN_USE && refusals.held.unchanged && refusals.held.fileKept && refusals.held.ms >= 400
        && released.status === 200 && refusals.released.file === `Long Test Movie (2021) - ${N('Held renamed')} [00.00.40-00.00.44].mkv`
        && refusals.offline.status === 503 && /^The clip's folder isn't available/.test(refusals.offline.detail || '') && refusals.offline.unchanged
        && refusals.stray.status === 503 && /^The clip's folder is there, but it isn't recognised/.test(refusals.stray.detail || '') && refusals.stray.unchanged
        && refusals.stale.status === 409 && refusals.stale.detail === 'Changed elsewhere. Reload to see the latest.' && refusals.stale.unchanged
        && refusals.makingStarted === 202 && refusals.making.status === 409 && refusals.making.detail === IN_PROGRESS && refusals.making.unchanged
        && refusals.makingFile.status === 409 && refusals.makingFile.detail === IN_PROGRESS && refusals.makingFile.unchanged
        && refusals.offlineFileKept && refusals.strayFileKept && !fs.existsSync(savedSettingsFile),
        refusals);

    // ---- 5a. a restore after renames: an import from before a rename, a snapshot from before another
    const movie = await findItem('Movie', (p) => p.split(/[\\/]/).includes('Long Test Movie (2021)'));
    const restore = await createBookmark('admin', movie, { start: 122, end: 126, name: N('Restore') });
    await makeClip('admin', restore);
    const firstPath = stored(restore.Id).Clip.FilePath;
    const exported = await call('admin', 'GET', '/Bookmarks/Admin/Export');
    const renamedOnce = await rename('admin', restore.Id, { Name: N('Restore once'), RenameFile: true });
    const oncePath = stored(restore.Id).Clip.FilePath;
    const imported = await call('admin', 'POST', '/Bookmarks/Admin/Import', { query: { mode: 'Replace', dryRun: 'false' }, body: exported.json || JSON.parse(exported.text) });
    const afterImport = stored(restore.Id);
    const renamedTwice = await rename('admin', restore.Id, { Name: N('Restore twice'), RenameFile: true });
    const twicePath = stored(restore.Id).Clip.FilePath;
    const snapshot = imported.json && imported.json.SafetySnapshot;
    const restored = await call('admin', 'POST', '/Bookmarks/Admin/Snapshots/Restore', { query: { name: snapshot } });
    const afterRestore = stored(restore.Id);
    const restoredThere = !!afterRestore && fs.existsSync(afterRestore.Clip.FilePath);
    const restoreListed = await listed('admin', restore.Id, N('Restore'));
    const deletedRestore = await call('admin', 'DELETE', `/Bookmarks/${restore.Id}`, { query: { deleteClip: 'true' } });
    evidence.restore = {
        exported: exported.status,
        once: { status: renamedOnce.status, file: path.basename(oncePath), firstGone: !fs.existsSync(firstPath) },
        imported: { status: imported.status, notes: ((imported.json && imported.json.Notes) || []).filter((n) => /renamed since the export/.test(n)), names: afterImport && path.basename(afterImport.Clip.FilePath), name: afterImport && afterImport.Name },
        twice: { status: renamedTwice.status, file: path.basename(twicePath) },
        restored: { status: restored.status, from: restored.json && restored.json.RestoredFrom, relinked: restored.json && restored.json.ClipsRelinked, names: afterRestore && path.basename(afterRestore.Clip.FilePath), name: afterRestore && afterRestore.Name, state: restoreListed && restoreListed.Clip.State, there: restoredThere },
        deleted: { status: deletedRestore.status, noFileLeft: ![firstPath, oncePath, twicePath].some((f) => fs.existsSync(f)) }
    };
    check('5a. a restore after renames: an export read, the clip renamed with its file, the export imported back (Replace): the record names the renamed file, with a note; renamed again, the import\'s safety snapshot restored: "ClipsRelinked" 1, the record names the file of the second rename, Ready; then deleted with its clip: no file of it left',
        exported.status === 200 && renamedOnce.status === 200 && evidence.restore.once.firstGone
        && imported.status === 200 && evidence.restore.imported.notes.length === 1 && afterImport && afterImport.Clip.FilePath === oncePath
        && renamedTwice.status === 200 && twicePath !== oncePath
        && restored.status === 200 && restored.json.RestoredFrom === snapshot && restored.json.ClipsRelinked === 1 && afterRestore.Clip.FilePath === twicePath && evidence.restore.restored.there && evidence.restore.restored.state === 'Ready'
        && afterRestore.Name === N('Restore once')
        && deletedRestore.status === 204 && evidence.restore.deleted.noFileLeft,
        evidence.restore);

    // ---- 5b. an emoji at the name's 80th character: the cut never splits it
    const emojiName = N('g'.repeat(61)) + EMOJI + 'goal';
    const emoji = await createBookmark('admin', movie, { start: 128, end: 132, name: emojiName });
    await makeClip('admin', emoji);
    const madeEmoji = stored(emoji.Id);
    const madeFile = madeEmoji.Clip.FilePath;
    const partials = filesIn(path.dirname(madeFile)).filter((f) => f.endsWith('.partial'));
    const madeThere = fs.existsSync(madeFile);
    const madeSize = madeThere ? fs.statSync(madeFile).size : null;
    const download = await call('admin', 'GET', `/Bookmarks/${emoji.Id}/Clip/File`, { query: { download: 'true' } });
    const renamedEmoji = await rename('admin', emoji.Id, { Name: N('h'.repeat(61)) + EMOJI + 'rename', RenameFile: true });
    const renamedEmojiFile = stored(emoji.Id).Clip.FilePath;
    context.emoji = emoji.Id;
    evidence.emoji = {
        nameLength: emojiName.length,
        made: { file: path.basename(madeFile), lone: hasLoneSurrogate(madeFile), there: madeThere, size: madeSize, recordSize: madeEmoji.Clip.FileSize, partials, download: download.status, bytes: download.text ? download.text.length : 0 },
        renamed: { status: renamedEmoji.status, file: path.basename(renamedEmojiFile), lone: hasLoneSurrogate(renamedEmojiFile), there: fs.existsSync(renamedEmojiFile), oldGone: !fs.existsSync(madeFile), name: stored(emoji.Id).Name === N('h'.repeat(61)) + EMOJI + 'rename' }
    };
    check('5b. an emoji at the name\'s 80th character: the clip made by the worker and ffmpeg under "Long Test Movie (2021) - clip-rename-probe ggg… [00.02.08-00.02.12].mkv" (the emoji cut whole, no half of it in the name), the record naming that file, its size the file\'s, no .partial left, the download 200 with the file; renamed with its file under a name like it, the file there',
        madeEmoji.Clip.Status === 'Completed' && evidence.emoji.made.file === `Long Test Movie (2021) - ${N('g'.repeat(61))} [00.02.08-00.02.12].mkv` && !evidence.emoji.made.lone && evidence.emoji.made.there
        && evidence.emoji.made.size > 0 && evidence.emoji.made.size === madeEmoji.Clip.FileSize && partials.length === 0 && download.status === 200 && evidence.emoji.made.bytes > 0
        && renamedEmoji.status === 200 && evidence.emoji.renamed.file === `Long Test Movie (2021) - ${N('h'.repeat(61))} [00.02.08-00.02.12].mkv` && !evidence.emoji.renamed.lone
        && evidence.emoji.renamed.there && evidence.emoji.renamed.oldGone && evidence.emoji.renamed.name,
        evidence.emoji);

    // ---- 6. and 7. the page's dialogs (desktop with Web D's file held open meanwhile; the TV layout)
    if (opts.webchecks) {
        context.releaseHeld = await holdOpen(file('Web D'));
        let desktop;
        try {
            desktop = webcheck('page-clips-rename');
        } finally {
            evidence.webDRelease = await context.releaseHeld();
            context.releaseHeld = null;
        }
        evidence.desktop = { ok: desktop.ok, exit: desktop.exit, seconds: desktop.seconds, steps: desktop.steps, failed: desktop.failed, failedRequests: desktop.failedRequests, exceptions: desktop.exceptions,
            dialog: desktop.value('the dialog as it opens: the focus in the name (nothing it does destroys anything), the box ticked, the file named'),
            renamed: desktop.value('the clip on the server and the focus'),
            renameFile: desktop.value('its text and the focus'),
            renamedFile: desktop.value('B on the server'),
            inUse: desktop.value('the server\'s 409 in the dialog after its three attempts; the dialog stays with the name typed; nothing renamed'),
            webDKept: fs.existsSync(file('Web D')) };
        check('6. page-clips-rename.json (admin): Rename clip opens one dialog with the focus in the name, "Also rename the file" ticked, the file named; Enter renames the clip and its file, the row drawn again, the toast, the focus back on the row\'s Rename clip; Rename file asks with the focus on Cancel and renames the file, the badge gone; a file held open is the server\'s 409 in the dialog, which stays with the name typed and nothing renamed',
            desktop.ok && evidence.desktop.webDKept, { exit: desktop.exit, failed: desktop.failed });
        const tv = webcheck('page-clips-rename-tv');
        evidence.tv = { ok: tv.ok, exit: tv.exit, seconds: tv.seconds, steps: tv.steps, failed: tv.failed, failedRequests: tv.failedRequests, exceptions: tv.exceptions,
            opened: tv.value('where the focus is (TV)'), box: tv.value('TV: the box at the label\'s size (the TV rule), 24 px at least'),
            renamed: tv.value('C on the server and the focus (TV)'), renamedFile: tv.value('E on the server and the focus (TV)') };
        check('7. page-clips-rename-tv.json (admin, the TV layout): OK on Rename clip opens exactly one dialog with the focus in the name; the D-pad down to the box (at TV size), Cancel and Rename; OK renames, the focus back on the row; Rename file starts on Cancel, one ArrowDown to Rename file, OK renames the file',
            tv.ok, { exit: tv.exit, failed: tv.failed });
    }

    // ---- 8. the crash simulation: a stop between a rename's move and its record's write
    if (opts.restart) {
        const crashed = stored(ids.Crashed);
        const notMoved = stored(ids.NotMoved);
        const crashedTo = path.join(path.dirname(crashed.Clip.FilePath), `Long Test Movie (2021) - ${N('Crashed renamed')} [00.01.04-00.01.08].mkv`);
        const notMovedTo = path.join(path.dirname(notMoved.Clip.FilePath), `Long Test Movie (2021) - ${N('NotMoved renamed')} [00.01.10-00.01.14].mkv`);
        const position = logPosition();
        stopSandbox('crash simulation');
        const plantedCrashed = plantEntry(crashed, crashedTo, N('Crashed renamed'));
        const plantedNotMoved = plantEntry(notMoved, notMovedTo, N('NotMoved renamed'));
        move(crashed.Clip.FilePath, crashedTo, true);
        await startSandbox('crash simulation');
        await waitFor('the journal entries finished', async () => !fs.existsSync(plantedCrashed.file) && !fs.existsSync(plantedNotMoved.file), 120);
        const after = { crashed: stored(ids.Crashed), notMoved: stored(ids.NotMoved) };
        const startLog = logSince(position);
        const crashedListed = await listed('admin', ids.Crashed, N('Crashed'));
        evidence.crash = {
            planted: [path.basename(plantedCrashed.file), path.basename(plantedNotMoved.file)],
            identity: plantedCrashed.entry.File,
            crashed: { path: path.basename(after.crashed.Clip.FilePath), name: after.crashed.Name, there: fs.existsSync(after.crashed.Clip.FilePath), state: crashedListed && crashedListed.Clip.State, outdated: crashedListed && crashedListed.Clip.IsNameOutdated },
            notMoved: { unchanged: JSON.stringify(after.notMoved) === JSON.stringify(notMoved), there: fs.existsSync(notMoved.Clip.FilePath) },
            log: startLog.split('\n').filter((l) => /clip file renames|Finished the rename/i.test(l)).map((l) => l.trim().slice(0, 400)).slice(0, 6),
            adopted: discardAdopted(),
            emoji: context.emoji ? (() => { const e = stored(context.emoji); return { file: e && path.basename(e.Clip.FilePath), there: !!e && fs.existsSync(e.Clip.FilePath), lone: !!e && hasLoneSurrogate(e.Clip.FilePath) }; })() : null
        };
        check('8. the crash simulation (the sandbox stopped, two journal entries written as the server writes them, one clip file moved to its new name, the sandbox started): Crashed\'s record names its renamed file with the rename\'s name, Ready and not "Name changed"; NotMoved\'s record as it was, its file where it was; both entries gone; the start\'s log says "1 finished … 1 not moved"',
            evidence.crash.crashed.path === path.basename(crashedTo) && evidence.crash.crashed.name === N('Crashed renamed') && evidence.crash.crashed.there && evidence.crash.crashed.state === 'Ready' && evidence.crash.crashed.outdated === false
            && evidence.crash.notMoved.unchanged && evidence.crash.notMoved.there
            && evidence.crash.log.some((l) => /Clip file renames that weren't finished: 1 finished \(the record names the new file now\), 0 already written, 1 not moved/.test(l))
            && evidence.crash.adopted.length === 1
            && (!evidence.crash.emoji || (evidence.crash.emoji.there && !evidence.crash.emoji.lone)),
            evidence.crash);
    }
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { savedSettings: null, files: null, releaseHeld: null, emoji: null };
    const startLog = logPosition();
    evidence.leftovers = { moves: loadMoves().length, created: loadCreated().length, settings: fs.existsSync(savedSettingsFile), planted: loadPlanted().filter((f) => fs.existsSync(f)).length };
    removeCreated();
    evidence.leftovers.adopted = discardAdopted();
    undoMoves();
    await restoreSavedSettings();
    evidence.leftovers.deleted = await deleteLeftovers();
    const startSettings = await getConfiguration();
    let failure = null;
    try {
        await run(context);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        const cleanup = { settingsRestored: null, deleted: [] };
        try {
            if (context.releaseHeld) {
                cleanup.heldRelease = await context.releaseHeld();
            }

            if (!running) {
                await startSandbox('clean-up');
            }

            removeCreated();
            cleanup.adopted = discardAdopted();
            undoMoves();
            cleanup.settingsRestored = await restoreSavedSettings();
            for (const { who, id } of made) {
                const removed = await call(who, 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } });
                const clip = await call(who, 'DELETE', `/Bookmarks/${id}/Clip`);
                cleanup.deleted.push({ id, bookmark: removed.status, clip: clip.status });
            }
            cleanup.leftovers = await deleteLeftovers();
        } catch (error) {
            check('clean-up: the settings, the folders, the files and the bookmarks put back', false, String(error));
        }
        evidence.cleanup = cleanup;
        const text = logSince(startLog);
        const lines = text.split('\n');
        const errors = lines.filter((l) => /\[ERR\]|\[FTL\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        const naming = lines.filter((l) => /\[(INF|WRN|ERR|FTL)\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l) && l.includes(PROBE));
        check('9. no Error of Bookmarks or the kit in the server log during the run, and no line of theirs at Information or above naming a clip-rename-probe bookmark',
            errors.length === 0 && naming.length === 0, { errors: errors.slice(0, 5), naming: naming.slice(0, 5) });
        const left = [];
        for (const who of ['admin', 'viewer2']) {
            const live = await call(who, 'GET', '/Bookmarks/Query', { query: { searchTerm: PROBE, limit: 1 } });
            const clips = await call(who, 'GET', '/Bookmarks/Clips', { query: { searchTerm: PROBE, limit: 1 } });
            if ((live.json && live.json.TotalRecordCount > 0) || (clips.json && clips.json.TotalRecordCount > 0)) left.push(who);
        }
        const probeFiles = filesIn(path.join(clipRoot, 'admin')).concat(filesIn(path.join(clipRoot, 'viewer2'))).filter((f) => f.includes(PROBE) || f.includes('a_b_c') || f.includes(' - CON ['));
        const settingsNow = await getConfiguration();
        const entriesLeft = filesIn(dataFolder).filter((f) => /^clip-rename-.*\.json$/i.test(f));
        check('9. clean-up: no move or made folder left, the probe\'s clip folders plugged in, the settings as they were (ClipOutputPath, VideoPreset), no clip-rename-probe bookmark or clip left, none of their files in the clip folder, no journal entry left',
            loadMoves().length === 0 && loadCreated().length === 0 && fs.existsSync(usbRoot) && fs.existsSync(strayRoot) && !fs.existsSync(usbRoot + UNPLUGGED) && !fs.existsSync(strayRoot + UNPLUGGED)
            && !fs.existsSync(savedSettingsFile) && (settingsNow.ClipOutputPath ?? '') === (startSettings.ClipOutputPath ?? '') && settingsNow.VideoPreset === startSettings.VideoPreset
            && left.length === 0 && probeFiles.length === 0 && entriesLeft.length === 0,
            { moves: loadMoves().length, created: loadCreated().length, left, probeFiles, entriesLeft, setting: settingsNow.ClipOutputPath ? 'set' : 'default', videoPreset: settingsNow.VideoPreset });
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
