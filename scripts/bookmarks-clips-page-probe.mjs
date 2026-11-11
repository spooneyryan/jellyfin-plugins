#!/usr/bin/env node
// Bookmarks' My Clips page in a running sandbox, in the web client,
// with the server's side of it: the capabilities (Pages.Clips), the page's shell and index.html's head fragment over raw
// HTTP, and the webchecks page-clips.json (as admin: "Library tools" with My Clips after My Bookmarks; the page on its
// fallback route (#/<prefix>-<key>) with a clip in every state the list works out, seeded below: Ready, a deleted bookmark's kept clip, being made
// (three long Accurate clips the webcheck starts, so the page polls them: one GET /Bookmarks/Clips/Status for all of them
// every 2.5 s, the rows patched in place, none once they are done), Failed, File missing and Clip folder offline; the
// summary line; the filters, the sorts and the search in the hash, a deep link reloaded, Back and Forward; the clip-ready
// toast's "View in My Clips" landing on the clip, marked once; the links between the pages; the delete dialogs keeping
// the clip by default now that the page is there), page-clips-tv.json and page-clips-phone.json (the TV layout
// and a 390x844 touch screen), and page-clips-deeplink.json (a deep link loaded cold, Back and Forward), and the server
// log. With --deep-link-only (the bare and baseurl sandboxes: the fallback route without Plugin Pages, the BaseUrl /jf) it seeds
// three clips and runs the deep-link webcheck alone. Node 22 built-ins only. Loopback only, never port 8096. The users'
// tokens come from credentials.json and are never printed.
//
//   node scripts/bookmarks-clips-page-probe.mjs --sandbox default|bare|baseurl [--deep-link-only] [--skip-layouts] [--timeout <seconds>] [--out result.json]
//
// It seeds admin's bookmarks and clips through the API, every name starting with "clips-page-probe" (and viewer2's one,
// which admin's page must never show), and changes only what it puts back, as bookmarks-clips-probe.mjs does:
// Bookmarks' ClipOutputPath, set for one clip to its work folder's usb-clips (inside the sandbox, so the sandbox still
// starts; that folder stays recorded in ClipRoots), and VideoPreset, set to slow while page-clips.json runs (so its three
// long Accurate clips are still being made when the page opens and take a few polls, as clip-guard-probe.mjs slows its
// long clip), each saved to saved-settings.json there before the change and put back right after that clip or that
// webcheck, in a finally and at the start of the next run after a run was killed; and two renames, recorded
// in moves.json before they are made and undone the same ways: usb-clips renamed aside (an unplugged drive) and one clip
// file moved into held/ (a file deleted by hand). It never writes bookmarks.json (only reads it, for a clip's path), never
// deletes a file itself but its saved-settings file, and never restarts or deploys anything. Every bookmark and clip it
// or its webchecks make is deleted at the end through the API (leftovers of a failed run at the next start).
//
// Checks:
//   1. the capabilities (admin and viewer): Pages.Clips true and Pages.Bookmarks true;
//   2. over raw HTTP: GET /Bookmarks/Pages/clips is 200 text/html with the page marker for a user, 401 anonymous, an
//      unknown key 404; index.html carries the head fragment once, with clips known, on and titled "My Clips";
//   3. the seed (admin, Long Test Movie unless said): Alpha Ready (Fast), Bravo a deleted bookmark's kept clip, Charlie in
//      the unplugged second clip folder, Delta's file held out, Echo cancelled before it had a file, Foxtrot Accurate
//      (MP4), Juliet an episode's (Test Series S01E01), Golf, Hotel and India long ranges for the webcheck's clips and Kilo
//      a range for the toast's; viewer2's Whiskey; each judged through GET /Bookmarks/{id} only, so the page's list makes
//      the clip folders' first verdicts;
//   4. page-clips.json (admin) passes, with its evidence of the poll;
//   5. page-clips-tv.json and page-clips-phone.json (admin) pass, unless --skip-layouts;
//   6. page-clips-deeplink.json (admin) passes;
//   7. no Error of Bookmarks or the kit in the log, and no line of theirs at Information or above naming a probe bookmark;
//      the clean-up: the setting, the folder and the file as they were, the probe's bookmarks and clips gone.
// The row actions: after check 6 (or alone, with --actions-only), with clips of its
// own named "clips-page-probe Act …" (admin, Long Test Movie): Mkv (Fast) and Mp4 (Accurate), Remake (its range changed after
// its clip was made), Forget (its file moved into held/), Delete, Held, Kept, BulkA to BulkC, Cancel (a long range without a
// clip; the webcheck asks for an Accurate one), Offline (in its work folder's cp-usb, unplugged) and Stray (in cp-stray,
// unplugged with an empty folder made in its place: there, but not recognised as the clips' own):
//   8. the seed as the API lists it, and the unrecognised folder's clip judged FolderUnavailable with RootAvailable true,
//      never Missing; its Forget 503 with the server's sentence and the clips' bulk delete FolderUnavailable for it and the
//      offline one, bookmarks.json byte for byte as before;
//   9. page-clips-actions.json (admin, VideoPreset slow while it runs): every row's actions by its state, Cancel, Dismiss,
//      Preview of the MKV and of the MP4 (each playing), Download, Play, Make the clip again, Delete, Forget, the refusals,
//      Select all, a bulk delete, and a bookmark deleted on My Bookmarks keeping its clip, which is then deleted here;
//  10. page-clips-inuse.json (admin) with Held's clip file held open from a PowerShell process the probe starts (as a preview
//      or a download holds it): the 409 sentence in the dialog with the row as it was; released, its delete works (200, the
//      file gone).
// With --je (the je sandbox) it seeds as --deep-link-only does and runs page-clips-je.json (Jellyfin
// Enhanced's tags never touch our rows) and the deep link. The probe deletes one file itself besides its saved settings: the
// held copy of Forget's clip file, in held/ only and only once no record names its path (the clip forgotten), so putting the
// moves back never leaves an untracked clip file in the clip folder. The empty folder it makes is recorded in created.json
// before it is made and removed with rmdir (which refuses a folder that isn't empty) before any move is undone.
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const TICKS = 10_000_000;
const PROBE = 'clips-page-probe';
const UNPLUGGED = '.clips-page-probe-unplugged';

function usage() {
    return 'usage: node scripts/bookmarks-clips-page-probe.mjs --sandbox <name> [--deep-link-only | --actions-only | --je] [--skip-layouts] [--timeout <seconds>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, timeout: 300, out: null, deepLinkOnly: false, actionsOnly: false, je: false, layouts: true };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--deep-link-only') o.deepLinkOnly = true;
        else if (a === '--actions-only') o.actionsOnly = true;
        else if (a === '--je') { o.je = true; o.deepLinkOnly = true; }
        else if (a === '--skip-layouts') o.layouts = false;
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!Number.isFinite(o.timeout) || o.timeout <= 0) throw new Error(usage());
    if (o.actionsOnly && o.deepLinkOnly) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const workRoot = path.join(sandboxRoot, 'bookmarks-clips-page-probe');
const usbRoot = path.join(workRoot, 'usb-clips');
const heldRoot = path.join(workRoot, 'held');
const movesFile = path.join(workRoot, 'moves.json');
const savedSettingsFile = path.join(workRoot, 'saved-settings.json');
// The row actions: a second clip folder to unplug (an offline drive), a third to unplug with an empty folder made in its place (there,
// but not the clips' own), and the record of the folders the probe makes.
const cpUsbRoot = path.join(workRoot, 'cp-usb');
const cpStrayRoot = path.join(workRoot, 'cp-stray');
const createdFile = path.join(workRoot, 'created.json');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
if (!logDir.startsWith(sandboxRoot + path.sep)) throw new Error(`refusing to read the log folder ${logDir}: not in the sandbox`);
const storeFile = path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'bookmarks.json');
const clipRoot = path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'clips');
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer', 'viewer2']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}
const pluginGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8')) || [])[1];
if (!pluginGuid) throw new Error('no guid in plugins/Bookmarks/build.yaml');
const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error(`refusing to work in ${p}`);
    return full;
}
for (const p of [storeFile, clipRoot, workRoot, usbRoot, heldRoot]) assertInside(p, sandboxRoot);
for (const p of [cpUsbRoot, cpStrayRoot, createdFile]) assertInside(p, workRoot);

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString(), mode: opts.deepLinkOnly ? 'deep-link-only' : 'full' } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// Jellyfin writes GUIDs without dashes ("N"); bookmarks.json writes them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);

async function call(who, method, p, { body, query, accept } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: accept || 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="bookmarks-clips-page-probe", Device="sandbox", DeviceId="bookmarks-clips-page-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(180000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text, contentType: res.headers.get('content-type') || '' };
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

// A rename inside the sandbox's clip folders and the probe's own folders only, recorded before it is made. `forgettable`:
// a clip file held out that the run may forget (discardForgotten).
function move(from, to, forgettable = false) {
    const src = path.resolve(from);
    const dst = path.resolve(to);
    for (const p of [src, dst]) {
        if (!(p.startsWith(clipRoot + path.sep) || p.startsWith(workRoot + path.sep))) throw new Error(`refusing to move ${p}: not in the sandbox's clip folders or the probe's folder`);
    }
    if (!fs.existsSync(src)) throw new Error(`${src} doesn't exist`);
    if (fs.existsSync(dst)) throw new Error(`${dst} exists already`);
    const moves = loadMoves();
    moves.push(forgettable ? { from: src, to: dst, forgettable: true } : { from: src, to: dst });
    saveMoves(moves);
    fs.mkdirSync(path.dirname(dst), { recursive: true });
    fs.renameSync(src, dst);
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

// The setting is saved to a file before it changes, so a killed run's next start puts it back.
async function restoreSavedSettings() {
    if (!fs.existsSync(savedSettingsFile)) return false;
    await setConfiguration(readJson(savedSettingsFile));
    fs.rmSync(savedSettingsFile);
    return true;
}

// The row actions: settings changed for one step, saved to the file first (as above) and put back right after it, in the finally and
// at the next start.
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

// An empty folder the probe makes in place of an unplugged one (the row actions' folder there but not recognised), in its work
// folder only, recorded before it is made; removed with rmdir, which refuses a folder that isn't empty, before any move is
// undone (also at the next start after a killed run).
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

// The held copy of a clip file whose clip Bookmarks has let go (the row actions' Forget): a move recorded as `forgettable`, whose
// path no record names any more, is not put back (that would leave an untracked clip file in the clip folder); its held
// copy is deleted, in held/ only.
function discardForgotten() {
    const referenced = new Set((fs.existsSync(storeFile) ? readJson(storeFile).Bookmarks || [] : [])
        .map((b) => b.Clip && b.Clip.FilePath).filter(Boolean).map((p) => path.resolve(p).toLowerCase()));
    const kept = [];
    const discarded = [];
    for (const m of loadMoves()) {
        const held = path.resolve(m.to);
        if (m.forgettable && !referenced.has(path.resolve(m.from).toLowerCase()) && held.startsWith(heldRoot + path.sep) && fs.existsSync(held)) {
            fs.rmSync(held);
            discarded.push(path.basename(held));
            continue;
        }

        kept.push(m);
    }

    saveMoves(kept);
    return discarded;
}

// Holds a file open as a download or a preview does (read, sharing read and write but not delete: Kestrel's PhysicalFile
// opens clip files so), from a PowerShell process the probe starts, until the returned release() ends it; the path goes
// through the environment, never a command line (as bookmarks-page-probe.mjs holds its file).
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
        closed: result && result.closed,
        value
    };
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

// A clip made and done, judged through GET /Bookmarks/{id} only (the clip list's verdicts are the page's first).
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

// A record's clip file as bookmarks.json has it (read only).
function storedClipPath(id) {
    const record = (readJson(storeFile).Bookmarks || []).find((b) => guid(b.Id) === guid(id));
    return record && record.Clip && record.Clip.FilePath;
}

// The probe's bookmarks and clips of a failed earlier run (or of this one), for admin and viewer2: the live ones deleted
// with their clips, 200 a request, then the clips kept when one was deleted (DELETE /Bookmarks/{id}/Clip).
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
    const episode = await findItem('Episode', (p) => /Test Series \(2020\) S01E01\.mkv$/.test(p));
    const s = {};
    s.alpha = await createBookmark('admin', movie, { start: 10, end: 14, name: `${PROBE} Alpha` });
    s.bravo = await createBookmark('admin', movie, { start: 20, end: 24, name: `${PROBE} Bravo` });
    s.foxtrot = await createBookmark('admin', movie, { start: 60, end: 64, name: `${PROBE} Foxtrot` });
    await makeClip('admin', s.alpha);
    await makeClip('admin', s.bravo);
    await makeClip('admin', s.foxtrot, 'Accurate');
    const tombstoned = await call('admin', 'DELETE', `/Bookmarks/${s.bravo.Id}`, { query: { deleteClip: 'false' } });
    if (tombstoned.status !== 204) throw new Error(`DELETE /Bookmarks/${s.bravo.Id}?deleteClip=false: ${tombstoned.status}`);
    const facts = { movie: movie.Id, episode: episode.Id };
    if (opts.deepLinkOnly) {
        return { ids: Object.fromEntries(Object.entries(s).map(([k, v]) => [k, v.Id])), facts };
    }

    s.delta = await createBookmark('admin', movie, { start: 40, end: 44, name: `${PROBE} Delta` });
    s.echo = await createBookmark('admin', movie, { start: 0, end: 175, name: `${PROBE} Echo` });
    s.juliet = await createBookmark('admin', episode, { start: 5, end: 9, name: `${PROBE} Juliet` });
    s.golf = await createBookmark('admin', movie, { start: 0, end: 175, name: `${PROBE} Golf` });
    s.hotel = await createBookmark('admin', movie, { start: 2, end: 177, name: `${PROBE} Hotel` });
    s.india = await createBookmark('admin', movie, { start: 4, end: 179, name: `${PROBE} India` });
    s.kilo = await createBookmark('admin', movie, { start: 70, end: 74, name: `${PROBE} Kilo` });
    s.whiskey = await createBookmark('viewer2', movie, { start: 12, end: 16, name: `${PROBE} Whiskey` });
    await makeClip('admin', s.delta);
    await makeClip('admin', s.juliet);
    await makeClip('viewer2', s.whiskey);

    // Echo: a long Accurate clip cancelled while it is being made, so it has no file (the list's Failed).
    const echo = await call('admin', 'POST', `/Bookmarks/${s.echo.Id}/Clip`, { body: { Mode: 'Accurate' } });
    if (echo.status !== 202) throw new Error(`POST /Bookmarks/${s.echo.Id}/Clip: ${echo.status}`);
    const cancelled = await call('admin', 'POST', `/Bookmarks/${s.echo.Id}/Clip/Cancel`);
    if (cancelled.status !== 200) throw new Error(`POST /Bookmarks/${s.echo.Id}/Clip/Cancel: ${cancelled.status}`);
    facts.echo = await waitFor('Echo cancelled', async () => {
        const read = await call('admin', 'GET', `/Bookmarks/${s.echo.Id}`);
        const clip = read.json && read.json.Clip;
        return clip && clip.Status === 'Cancelled' ? { Status: clip.Status, FileName: clip.FileName || null } : null;
    }, 60);

    // Charlie: a clip in a second clip folder inside the sandbox (an admin's other drive), the setting saved first.
    const config = await getConfiguration();
    context.savedSettings = { ClipOutputPath: config.ClipOutputPath ?? '' };
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(savedSettingsFile, JSON.stringify(context.savedSettings));
    fs.mkdirSync(usbRoot, { recursive: true });
    await setConfiguration({ ClipOutputPath: usbRoot });
    s.charlie = await createBookmark('admin', movie, { start: 30, end: 34, name: `${PROBE} Charlie` });
    await makeClip('admin', s.charlie);
    await restoreSavedSettings();
    context.savedSettings = null;

    // Delta's file held out (deleted by hand, as far as Bookmarks can tell) and the second clip folder unplugged.
    const files = { charlie: storedClipPath(s.charlie.Id), delta: storedClipPath(s.delta.Id) };
    context.files = files;
    facts.charlieInUsb = !!files.charlie && path.resolve(files.charlie).toLowerCase().startsWith(usbRoot.toLowerCase() + path.sep) && fs.existsSync(files.charlie);
    facts.deltaInClips = !!files.delta && path.resolve(files.delta).toLowerCase().startsWith(clipRoot.toLowerCase() + path.sep) && fs.existsSync(files.delta);
    move(files.delta, path.join(heldRoot, path.basename(files.delta)));
    move(usbRoot, usbRoot + UNPLUGGED);
    facts.settingBack = ((await getConfiguration()).ClipOutputPath ?? '') === (config.ClipOutputPath ?? '');
    return { ids: Object.fromEntries(Object.entries(s).map(([k, v]) => [k, v.Id])), facts };
}

async function run(context) {
    // ---- 1. the capabilities
    const capabilities = {};
    for (const who of ['admin', 'viewer']) {
        const r = await call(who, 'GET', '/Bookmarks/Capabilities');
        capabilities[who] = r.json && { status: r.status, Pages: r.json.Pages, CanCreateClips: r.json.CanCreateClips };
    }
    evidence.capabilities = capabilities;
    check('1. the capabilities offer My Clips (Pages.Clips true) and My Bookmarks to admin and viewer',
        ['admin', 'viewer'].every((who) => capabilities[who] && capabilities[who].status === 200 && capabilities[who].Pages.Clips === true && capabilities[who].Pages.Bookmarks === true),
        capabilities);

    // ---- 2. the shell and the head fragment over raw HTTP
    const shell = await call('viewer', 'GET', '/Bookmarks/Pages/clips', { accept: 'text/html' });
    const anonymous = await call('anonymous', 'GET', '/Bookmarks/Pages/clips', { accept: 'text/html' });
    const unknown = await call('viewer', 'GET', '/Bookmarks/Pages/nope', { accept: 'text/html' });
    const index = await call('anonymous', 'GET', '/web/index.html', { accept: 'text/html' });
    const head = /<script type="application\/json" data-jfbm-head data-jfbm-pages>([^<]*)<\/script>/.exec(index.text);
    const headState = head ? JSON.parse(head[1]) : null;
    evidence.shell = { status: shell.status, contentType: shell.contentType, marker: shell.text.includes('data-jfbm-page="clips"'), body: shell.text.slice(0, 300) };
    evidence.anonymous = anonymous.status;
    evidence.unknown = unknown.status;
    evidence.index = { status: index.status, headStyles: (index.text.match(/<style data-jfbm-head>/g) || []).length, state: headState, titled: index.text.includes('"clips":"My Clips"'), tags: (index.text.match(/data-jfbm-plugin=/g) || []).length };
    check('2. the clips shell is 200 text/html with the page marker for a user and 401 anonymous, an unknown key is 404, and index.html carries the head fragment once with clips known, on and titled "My Clips"',
        shell.status === 200 && /^text\/html/.test(shell.contentType) && evidence.shell.marker && anonymous.status === 401 && unknown.status === 404 && index.status === 200
        && evidence.index.headStyles === 1 && evidence.index.tags === 1 && !!headState && headState.pages.includes('clips') && !headState.off.includes('clips') && evidence.index.titled,
        { shell: evidence.shell, anonymous, unknown: evidence.unknown, index: evidence.index });

    if (opts.actionsOnly) {
        await runC4(context);
        return;
    }

    // ---- 3. the seed
    const seeded = await seed(context);
    evidence.seed = seeded;
    if (opts.deepLinkOnly) {
        check('3. the seed (admin, Long Test Movie): Alpha Ready (Fast), Bravo a deleted bookmark\'s kept clip, Foxtrot Accurate',
            !!seeded.ids.alpha && !!seeded.ids.bravo && !!seeded.ids.foxtrot, seeded);
    } else {
        check('3. the seed (admin): Alpha Ready, Bravo a deleted bookmark\'s kept clip, Charlie in the unplugged second clip folder, Delta\'s file held out, Echo cancelled with no file, Foxtrot Accurate, Juliet an episode\'s; Golf, Hotel, India and Kilo ranges without a clip yet; viewer2\'s Whiskey; the setting put back',
            Object.values(seeded.ids).every(Boolean) && seeded.facts.echo.Status === 'Cancelled' && !seeded.facts.echo.FileName
            && seeded.facts.charlieInUsb && seeded.facts.deltaInClips && seeded.facts.settingBack
            && (readJson(storeFile).ClipRoots || []).some((r) => path.resolve(r).toLowerCase() === usbRoot.toLowerCase())
            && !fs.existsSync(usbRoot) && fs.existsSync(usbRoot + UNPLUGGED) && !fs.existsSync(context.files.delta), seeded);

        // ---- 4. the page, with the long clips slowed down (VideoPreset slow, saved first, put back right after)
        const before = await getConfiguration();
        context.savedSettings = { VideoPreset: before.VideoPreset };
        fs.writeFileSync(savedSettingsFile, JSON.stringify(context.savedSettings));
        await setConfiguration({ VideoPreset: 'slow' });
        let page;
        try {
            page = webcheck('page-clips');
        } finally {
            await restoreSavedSettings();
            context.savedSettings = null;
        }
        evidence.videoPreset = { before: before.VideoPreset, during: 'slow', after: (await getConfiguration()).VideoPreset };
        evidence.page = {
            ok: page.ok, exit: page.exit, seconds: page.seconds, steps: page.steps, failed: page.failed, failedRequests: page.failedRequests, exceptions: page.exceptions, closed: page.closed,
            seed: page.value('the probe\'s clips by name, as the API lists them'),
            drawer: page.value('\'Library tools\' with My Bookmarks, then My Clips (#/jfbm-clips, video_library), right before User'),
            rendered: page.value('the page renders on its route: the title, no \'Page not found\', the toolbar, the summary line, the rows'),
            rows: page.value('the probe\'s rows: each state, the badges, the poster only for a video the viewer can open, the video\'s link'),
            polled: page.value('the clips being made: their rows patched in place until each is done'),
            network: page.value('the poll as the network saw it: one Status request for every clip being made, 2.5 s apart; no list read for a tick; nothing once they were done'),
            summary: page.value('the summary line as the API counts, no clip in progress any more'),
            filters: page.value('each state, mode and type as the API counts them for the probe\'s search, and the sorts in the API\'s order'),
            deepLink: page.value('after the reload: the toolbar and the list as the hash says'),
            back: page.value('Back: the hash and the toolbar of the entry before, rendered by the kit'),
            toast: page.value('the clip-ready toast: Download and View in My Clips'),
            landed: page.value('My Clips with Kilo\'s clip marked and in view, highlight= dropped from the hash in place'),
            landedFocus: page.value('the focus on Kilo\'s row, for a keyboard, a remote or a screen reader'),
            dialogs: page.value('the delete confirmation keeps the clip by default now that My Clips lists it'),
            bulkDialog: page.value('the bulk delete keeps the clips by default too'),
            links: page.value('the header links and My Bookmarks\' clip line link to the other page'),
            requests: page.value('the requests the Bookmarks script made since the page opened (fetch and XMLHttpRequest through /Bookmarks/ClientScript; jellyfin-web\'s own never count)')
        };
        check('4. page-clips.json (admin): My Clips in Library tools after My Bookmarks, the page on its route with every state, the badges, the summary; the poll of the clips being made (one Status request for all of them every 2.5 s, the rows patched in place, none once done); the filters, the sorts and the search in the hash, a deep link reloaded, Back; the toast\'s View in My Clips landing once; the links between the pages; the delete dialogs keeping the clip by default; VideoPreset as it was afterwards',
            page.ok && evidence.videoPreset.after === evidence.videoPreset.before, { exit: page.exit, failed: page.failed, videoPreset: evidence.videoPreset });

        // ---- 5. the TV layout and a phone
        if (opts.layouts) {
            const tv = webcheck('page-clips-tv');
            evidence.tv = { ok: tv.ok, exit: tv.exit, seconds: tv.seconds, steps: tv.steps, failed: tv.failed, failedRequests: tv.failedRequests, exceptions: tv.exceptions, closed: tv.closed,
                page: tv.value('TV: the page with jfbm-page-tv, the toolbar\'s controls on its lines, every control and video link focusable'),
                box: tv.value('TV: the deleted bookmarks\' box at least 24 px square (the native box was small to see from a sofa)') };
            check('5. page-clips-tv.json (admin, the TV layout): the page body carries jfbm-page-tv, the toolbar fits, every control and row link is a focusable element, the deleted bookmarks\' box at TV size, the keyboard moves through the toolbar',
                tv.ok, { exit: tv.exit, failed: tv.failed });
            const phone = webcheck('page-clips-phone');
            evidence.phone = { ok: phone.ok, exit: phone.exit, seconds: phone.seconds, steps: phone.steps, failed: phone.failed, failedRequests: phone.failedRequests, exceptions: phone.exceptions, closed: phone.closed,
                fits: phone.value('mobile: the page fits in 390 px, every row inside it, the toolbar\'s controls and the header link at least 40 px tall'),
                summary: phone.value('mobile: the summary line\'s parts each whole, the matches on one line of their own (they wrapped mid-line after the totals)') };
            check('5. page-clips-phone.json (admin, 390x844 touch, the mobile layout): no sideways scroll, every row inside the screen, the toolbar\'s controls and the header link 40 px tall at least, the summary\'s matches whole on one line',
                phone.ok, { exit: phone.exit, failed: phone.failed });
        }
    }

    // ---- 6. a deep link loaded cold, Back and Forward
    const deep = webcheck('page-clips-deeplink');
    evidence.deepLink = { ok: deep.ok, exit: deep.exit, seconds: deep.seconds, steps: deep.steps, failed: deep.failed, failedRequests: deep.failedRequests, exceptions: deep.exceptions, closed: deep.closed,
        loaded: deep.value('the deep link loaded cold: the title, no \'Page not found\', the toolbar and the rows as the hash says'),
        back: deep.value('Back: the deep link\'s state again'),
        forward: deep.value('Forward: the state after the change again') };
    check(`6. page-clips-deeplink.json (admin, ${opts.sandbox}${baseUrl ? ', BaseUrl ' + baseUrl : ''}): a deep link with the search, a state, a sort and deleted=0 loaded cold shows the toolbar and the API's rows as the hash says, no "Page not found"; a toolbar change, Back and Forward render from the hash`,
        deep.ok, { exit: deep.exit, failed: deep.failed });

    if (opts.je) {
        // ---- 11. Jellyfin Enhanced's tags next to our rows
        const jeRun = webcheck('page-clips-je');
        evidence.je = { ok: jeRun.ok, exit: jeRun.exit, seconds: jeRun.seconds, steps: jeRun.steps, failed: jeRun.failed, failedRequests: jeRun.failedRequests, exceptions: jeRun.exceptions, closed: jeRun.closed,
            tags: jeRun.value('JE\'s tags on'), homeTags: jeRun.value('JE\'s tags on the home page (evidence only)'), apiCount: jeRun.value('the API\'s count of the probe\'s clips'),
            rows: jeRun.value('every row as drawn, shown, with its actions; nothing of JE\'s in them') };
        check('11. page-clips-je.json (admin, the je sandbox with Jellyfin Enhanced\'s quality and rating tags on): every clip the API lists rendered, no row changed after JE\'s observers had their time, nothing of JE\'s in them, every row shown with its actions; a preview plays',
            jeRun.ok, { exit: jeRun.exit, failed: jeRun.failed });
    } else if (!opts.deepLinkOnly) {
        await runC4(context);
    }
}

// ------------------------------------------------------------------------------------------------------ the row actions

// The clips of the actions' webchecks (admin, Long Test Movie), each named "clips-page-probe Act <Name>"; see the header.
async function seedC4(context) {
    const movie = await findItem('Movie', (p) => p.split(/[\\/]/).includes('Long Test Movie (2021)'));
    const N = (n) => `${PROBE} Act ${n}`;
    const s = {};
    const plain = [['Mkv', 10, 14, 'Fast'], ['Mp4', 20, 24, 'Accurate'], ['Remake', 30, 34, 'Fast'], ['Forget', 40, 44, 'Fast'], ['Delete', 50, 54, 'Fast'],
        ['Held', 60, 64, 'Fast'], ['Kept', 70, 74, 'Fast'], ['BulkA', 80, 84, 'Fast'], ['BulkB', 90, 94, 'Fast'], ['BulkC', 100, 104, 'Fast']];
    for (const [name, start, end, mode] of plain) {
        s[name] = await createBookmark('admin', movie, { start, end, name: N(name) });
        await makeClip('admin', s[name], mode);
    }

    // Remake's range changed after its clip was made: its row says "Range changed" and the remake is marked.
    const current = (await call('admin', 'GET', `/Bookmarks/${s.Remake.Id}`)).json;
    const moved = await call('admin', 'POST', `/Bookmarks/${s.Remake.Id}`, { body: { StartTicks: 30 * TICKS, EndTicks: 35 * TICKS, Name: N('Remake'), Note: null, IfUpdatedAt: current.UpdatedAt } });
    if (moved.status !== 200) throw new Error(`POST /Bookmarks/${s.Remake.Id}: ${moved.status} ${moved.text.slice(0, 200)}`);
    s.Cancel = await createBookmark('admin', movie, { start: 0, end: 175, name: N('Cancel') });

    // Offline and Stray: a clip each in a clip folder of the work folder (the setting saved first and put back at once).
    fs.mkdirSync(cpUsbRoot, { recursive: true });
    fs.mkdirSync(cpStrayRoot, { recursive: true });
    await withSettings(context, { ClipOutputPath: cpUsbRoot }, async () => {
        s.Offline = await createBookmark('admin', movie, { start: 110, end: 114, name: N('Offline') });
        await makeClip('admin', s.Offline);
    });
    await withSettings(context, { ClipOutputPath: cpStrayRoot }, async () => {
        s.Stray = await createBookmark('admin', movie, { start: 120, end: 124, name: N('Stray') });
        await makeClip('admin', s.Stray);
    });

    const files = Object.fromEntries(['Forget', 'Held', 'Offline', 'Stray', 'Remake'].map((n) => [n, storedClipPath(s[n].Id)]));
    context.c4Files = files;
    const facts = {
        offlineInUsb: !!files.Offline && path.resolve(files.Offline).toLowerCase().startsWith(cpUsbRoot.toLowerCase() + path.sep),
        strayInStray: !!files.Stray && path.resolve(files.Stray).toLowerCase().startsWith(cpStrayRoot.toLowerCase() + path.sep),
        strayMarker: fs.existsSync(path.join(cpStrayRoot, '.jfbm-clip-root'))
    };

    // Forget's file held out (deleted by hand, as far as Bookmarks can tell), cp-usb unplugged, cp-stray unplugged with an
    // empty folder in its place (an empty mount point, a disk that isn't mounted).
    move(files.Forget, path.join(heldRoot, path.basename(files.Forget)), true);
    move(cpUsbRoot, cpUsbRoot + UNPLUGGED);
    move(cpStrayRoot, cpStrayRoot + UNPLUGGED);
    makeFolder(cpStrayRoot);
    return { ids: Object.fromEntries(Object.entries(s).map(([k, v]) => [k, guid(v.Id)])), facts };
}

async function runC4(context) {
    // ---- 8. the seed, and the folder that is there but not the clips' own judged as offline by the server
    const seeded = await seedC4(context);
    evidence.c4Seed = seeded;
    const listed = await call('admin', 'GET', '/Bookmarks/Clips', { query: { searchTerm: `${PROBE} Act`, limit: 200 } });
    const byName = Object.fromEntries(((listed.json && listed.json.Items) || []).map((c) => [c.Name.slice(`${PROBE} Act `.length), c]));
    const states = Object.fromEntries(Object.entries(byName).map(([n, c]) => [n, { state: c.Clip.State, rootAvailable: c.Clip.RootAvailable, fileExists: c.Clip.FileExists, outdated: c.Clip.IsOutdated }]));
    const before = fs.readFileSync(storeFile);
    const forget = await call('admin', 'DELETE', `/Bookmarks/${seeded.ids.Stray}/Clip`, { query: { onlyIfMissing: 'true' } });
    const bulk = await call('admin', 'POST', '/Bookmarks/Clips/BulkDelete', { body: { Ids: [seeded.ids.Stray, seeded.ids.Offline] } });
    const after = fs.readFileSync(storeFile);
    evidence.c4States = states;
    evidence.c4Refused = { forget: { status: forget.status, detail: forget.json && forget.json.detail }, bulk: bulk.json, unchanged: before.equals(after) };
    const S = (n) => states[n] || {};
    check('8. the row actions\' seed (admin): Mkv and Mp4 Ready, Remake Ready with its range changed, Forget Missing, Offline FolderUnavailable (its folder unplugged), Stray FolderUnavailable with RootAvailable true (its folder there, empty, without the clips\' marker: never Missing); the server leaves Stray as it is: Forget 503 with its sentence, the clips\' bulk delete FolderUnavailable for it and Offline, bookmarks.json byte for byte',
        S('Mkv').state === 'Ready' && S('Mp4').state === 'Ready' && S('Remake').state === 'Ready' && S('Remake').outdated === true && S('Forget').state === 'Missing'
        && S('Offline').state === 'FolderUnavailable' && S('Offline').rootAvailable === false && S('Stray').state === 'FolderUnavailable' && S('Stray').rootAvailable === true && S('Stray').fileExists === false
        && seeded.facts.offlineInUsb && seeded.facts.strayInStray && seeded.facts.strayMarker
        && forget.status === 503 && /^The clip's folder is there, but it isn't recognised as the one its clips were made in/.test((forget.json && forget.json.detail) || '')
        && bulk.status === 200 && bulk.json && [...bulk.json.FolderUnavailable].map(guid).sort().join() === [seeded.ids.Stray, seeded.ids.Offline].sort().join()
        && bulk.json.FilesDeleted.length === 0 && bulk.json.RecordsRemoved.length === 0 && before.equals(after),
        { states, refused: evidence.c4Refused, facts: seeded.facts });

    // ---- 9. the row actions, VideoPreset slow while the webcheck runs (its long clip is cancelled while it is being made)
    const actionsRun = await withSettings(context, { VideoPreset: 'slow' }, async () => webcheck('page-clips-actions'));
    const remadeFile = storedClipPath(seeded.ids.Remake);
    evidence.actions = {
        ok: actionsRun.ok, exit: actionsRun.exit, seconds: actionsRun.seconds, steps: actionsRun.steps, failed: actionsRun.failed, failedRequests: actionsRun.failedRequests, exceptions: actionsRun.exceptions, closed: actionsRun.closed,
        rows: actionsRun.value('each row\'s actions by its state'),
        cancelled: actionsRun.value('the row read again: Cancelled (no file: the Failed state), with Dismiss'),
        dismissed: actionsRun.value('gone from the list; the bookmark stays without a clip'),
        previewMkv: actionsRun.value('the dialog\'s <video> plays the MKV: the picture decoded, the time moving, no sentence of failure; the sound note under it'),
        previewMp4: actionsRun.value('the MP4 plays, without the sound note'),
        download: actionsRun.value('Download: the link it clicks (recorded, not followed: nothing is saved), and the route answering it'),
        played: actionsRun.value('jellyfin-web\'s player plays the movie from the clip\'s start (10 s)'),
        remakeDialog: actionsRun.value('the dialog: the bookmark\'s range now, why, the server\'s default mode'),
        remade: actionsRun.value('the row is followed until the new clip is ready: the new range\'s file, the badge gone'),
        deleteText: actionsRun.value('the confirmation names the file and its size; the bookmark stays'),
        deleted: actionsRun.value('deleted: the row gone, the bookmark without its clip, its file route 404'),
        forgotten: actionsRun.value('forgotten: the row gone, the bookmark without its clip'),
        refused: actionsRun.value('the clip in the folder that isn\'t recognised: Forget through the API is 503 with the server\'s sentence, the clips\' bulk delete answers FolderUnavailable for it and the offline one; both still listed as they were'),
        selectAll: actionsRun.value('tick one, then Select all'),
        bulk: actionsRun.value('the three deleted: their rows gone, the bar hidden, the bookmarks without clips'),
        keepDialog: actionsRun.value('the confirmation keeps the clip by default: the box unticked, "The clip stays in My Clips."'),
        kept: actionsRun.value('the bookmark gone (404), its clip kept and listed as a deleted bookmark\'s'),
        keptRow: actionsRun.value('its row: Bookmark deleted, no Edit and no remake'),
        keptGone: actionsRun.value('gone: the row, the clip from the list, its file route 404'),
        remadeFile: remadeFile ? path.basename(remadeFile) : null,
        oldRemakeFileGone: !!context.c4Files.Remake && !fs.existsSync(context.c4Files.Remake),
        offlineFileKept: fs.existsSync(path.join(cpUsbRoot + UNPLUGGED, path.relative(cpUsbRoot, context.c4Files.Offline))),
        strayFileKept: fs.existsSync(path.join(cpStrayRoot + UNPLUGGED, path.relative(cpStrayRoot, context.c4Files.Stray)))
    };
    check('9. page-clips-actions.json (admin): every row\'s actions by its state (offline and unrecognised rows only Play and Edit, no checkbox); a clip being made cancelled and dismissed; the MKV and the MP4 previewed, each playing; Download\'s link; Play at the clip\'s start; a clip whose range changed made again (Fast) to a new file, the old one deleted by Bookmarks; one deleted; one forgotten; the unrecognised folder refused; Select all skipping the offline rows; three deleted at once; a bookmark deleted keeping its clip (the default), listed with Bookmark deleted, deleted from My Clips; the offline and unrecognised folders\' files untouched; VideoPreset put back',
        actionsRun.ok && evidence.actions.oldRemakeFileGone && evidence.actions.offlineFileKept && evidence.actions.strayFileKept && !fs.existsSync(savedSettingsFile),
        { exit: actionsRun.exit, failed: actionsRun.failed, oldRemakeFileGone: evidence.actions.oldRemakeFileGone, offlineFileKept: evidence.actions.offlineFileKept, strayFileKept: evidence.actions.strayFileKept });

    // ---- 10. a clip file in use: the 409 on the page; released, the delete works
    context.releaseHeld = await holdOpen(context.c4Files.Held);
    let inUse;
    try {
        inUse = webcheck('page-clips-inuse');
    } finally {
        const code = await context.releaseHeld();
        context.releaseHeld = null;
        evidence.heldRelease = code;
    }
    const afterRelease = await call('admin', 'DELETE', `/Bookmarks/${seeded.ids.Held}/Clip`);
    evidence.inUse = { ok: inUse.ok, exit: inUse.exit, seconds: inUse.seconds, steps: inUse.steps, failed: inUse.failed, failedRequests: inUse.failedRequests, exceptions: inUse.exceptions, closed: inUse.closed,
        shown: inUse.value('the server\'s 409 in the dialog, after its three attempts; the row as it was; the clip and its file still there'),
        afterRelease: afterRelease.status, fileGone: !fs.existsSync(context.c4Files.Held) };
    check('10. page-clips-inuse.json (admin) with Held\'s clip file held open as a download holds it: the dialog says "The clip file is in use (being played or downloaded); try again shortly." after the server\'s three attempts, the row as it was, the clip and its file still there; released, its delete answers 200 and the file is gone',
        inUse.ok && afterRelease.status === 200 && evidence.inUse.fileGone, { exit: inUse.exit, failed: inUse.failed, afterRelease: afterRelease.status, fileGone: evidence.inUse.fileGone });
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { savedSettings: null, files: null, c4Files: null, releaseHeld: null };
    const startLog = logPosition();
    evidence.leftovers = { moves: loadMoves().length, created: loadCreated().length, settings: fs.existsSync(savedSettingsFile) };
    removeCreated();
    evidence.leftovers.discarded = discardForgotten();
    undoMoves(0);
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

            removeCreated();
            cleanup.discarded = discardForgotten();
            undoMoves(0);
            cleanup.settingsRestored = await restoreSavedSettings();
            for (const { who, id } of made) {
                const removed = await call(who, 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } });
                const clip = await call(who, 'DELETE', `/Bookmarks/${id}/Clip`);
                cleanup.deleted.push({ id, bookmark: removed.status, clip: clip.status });
            }
            cleanup.leftovers = await deleteLeftovers();
        } catch (error) {
            check('clean-up: the setting, the folder, the file and the bookmarks put back', false, String(error));
        }
        evidence.cleanup = cleanup;
        const text = logSince(startLog);
        const lines = text.split('\n');
        const errors = lines.filter((l) => /\[ERR\]|\[FTL\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        const naming = lines.filter((l) => /\[(INF|WRN|ERR|FTL)\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l) && l.includes(PROBE));
        check('7. no Error of Bookmarks or the kit in the server log during the run, and no line of theirs at Information or above naming a clips-page-probe bookmark',
            errors.length === 0 && naming.length === 0, { errors: errors.slice(0, 5), naming: naming.slice(0, 5) });
        const left = [];
        for (const who of ['admin', 'viewer2']) {
            const live = await call(who, 'GET', '/Bookmarks/Query', { query: { searchTerm: PROBE, limit: 1 } });
            const clips = await call(who, 'GET', '/Bookmarks/Clips', { query: { searchTerm: PROBE, limit: 1 } });
            if ((live.json && live.json.TotalRecordCount > 0) || (clips.json && clips.json.TotalRecordCount > 0)) left.push(who);
        }
        const filesLeft = Object.values(context.files || {}).concat(Object.values(context.c4Files || {})).filter((f) => f && fs.existsSync(f));
        const heldLeft = fs.existsSync(heldRoot) ? fs.readdirSync(heldRoot) : [];
        const settingsNow = await getConfiguration();
        const settingNow = settingsNow.ClipOutputPath ?? '';
        const c3Folders = opts.deepLinkOnly || opts.actionsOnly || (fs.existsSync(usbRoot) && !fs.existsSync(usbRoot + UNPLUGGED));
        const c4Folders = !context.c4Files || [cpUsbRoot, cpStrayRoot].every((f) => fs.existsSync(f) && !fs.existsSync(f + UNPLUGGED));
        check('7. clean-up: no move or made folder left, nothing in the hold folder, the probe\'s clip folders plugged in, the settings as they were (ClipOutputPath not the probe\'s folder, VideoPreset), no clips-page-probe bookmark or clip left, their files gone',
            loadMoves().length === 0 && loadCreated().length === 0 && heldLeft.length === 0 && c3Folders && c4Folders && !fs.existsSync(savedSettingsFile)
            && [usbRoot, cpUsbRoot, cpStrayRoot].every((f) => path.resolve(settingNow || '.').toLowerCase() !== f.toLowerCase()) && settingNow === (startSettings.ClipOutputPath ?? '') && settingsNow.VideoPreset === startSettings.VideoPreset
            && left.length === 0 && filesLeft.length === 0,
            { moves: loadMoves().length, created: loadCreated().length, held: heldLeft.length, left, filesLeft: filesLeft.length, setting: settingNow ? 'set' : 'default', videoPreset: settingsNow.VideoPreset });
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
