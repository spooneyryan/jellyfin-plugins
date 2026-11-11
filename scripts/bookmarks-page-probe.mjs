#!/usr/bin/env node
// Bookmarks' My Bookmarks page in a running sandbox, in the web client, with the server's side of it: the webchecks page-bookmarks.json (as admin: the
// page on its fallback route (#/<prefix>-<key>), the toolbar, both views, every sort, paging, the state in the hash, deep links, Back and
// Forward, See all, Open title and Back, the four filters), page-bookmarks-off.json (as admin: EnableBookmarksPage off
// and back on, through the configuration API), and page-unavailable.json (viewer's group Unavailable while
// the admin takes a library away, and back), page-actions.json (admin: the inline rename with a 409, the point
// bookmark's scissors, the bulk delete with and without the clips, a Select all over 205 rows
// deleted in two requests, two inline renames kept through a redraw and another 205 whose delete is
// closed with Escape during its first request, which stops it there), page-actions-inuse.json (admin: the bulk delete while the probe holds
// a clip file open as a download does, the way bookmarks-api-probe.mjs's safe clip-file checks do, so the server answers InUse
// for real), page-player-options.json (viewer: the Player options dialog, the device shortcut in the player, a
// reserved key refused inline), page-tv.json and page-phone.json (the TV layout and a 390x844 touch screen, the Light,
// Apple TV and Purple Haze themes), the capabilities, the shell endpoint and index.html's head fragment over raw HTTP,
// viewer2's view of viewer's bookmark (404), and the server log. Node 22 built-ins only. Loopback only, never port
// 8096. The users' tokens come from credentials.json and are never printed.
//
//   node scripts/bookmarks-page-probe.mjs --sandbox default|bare|baseurl [--skip-off] [--skip-layouts] [--timeout <scan seconds>] [--out result.json]
//
// It changes one Bookmarks setting (EnableBookmarksPage, through the off webcheck) and puts it back in a finally; every
// bookmark it or its webchecks make is named or noted "page-probe..." and deleted at the end (leftovers of a failed run
// at the next). For the Not in library filter it needs a detached bookmark (a video gone from the library),
// so it bookmarks the sandbox's "Tagged Movie (2012)", renames that movie's folder into its hold folder
// .sandbox/<name>/bookmarks-page-probe/held (recorded in moves.json there before the move, as bookmarks-rows-probe.mjs
// does for its relink; renames only inside .sandbox/media and the hold folder), scans, and renames it back and scans
// again in the finally, or at the start of the next run after a failed one (taking a library path out
// through the library API doesn't remove its items in a scan, so that was no way). For the Unavailable badge
// the unavailable webcheck takes "Movies 2" out of viewer's libraries as admin and gives it back; the probe reads
// viewer's policy and preferences first and puts both back in its finally whatever happened. It reads the log, never
// writes the store, and never restarts or deploys anything. The Plugin Pages host (page-bookmarks-pluginpages.json)
// needs a sandbox started in Auto mode and is run by hand (scripts/README.md).
//
// Checks:
//   1. the capabilities (admin and viewer): Pages.Bookmarks true, Pages.Clips true (since My Clips);
//   2. over raw HTTP: GET /Bookmarks/Pages/bookmarks is 200 text/html with the page marker for a user, 401 anonymous;
//      an unknown key is 404; index.html carries the head fragment once, with bookmarks known and on;
//   3. a bookmark on a movie whose folder is then held out and the library scanned is Detached (the identity service),
//      and page-bookmarks.json (admin) passes with the Not in library filter showing it;
//   4. page-bookmarks-off.json (admin) passes, unless --skip-off, and EnableBookmarksPage is on again afterwards;
//   5. viewer's bookmark on a "Movies 2" video is 404 for viewer2 and absent from viewer2's Query, and
//      page-unavailable.json passes with viewer's policy as it was afterwards;
//   6. page-actions.json (admin) passes (the row and bulk actions, the Select all over 205 rows, two renames open at once, a
//      delete closed during its first request stopping there); then, with the clip file of
//      the probe's "page-probe inuse held" bookmark held open from a PowerShell process the probe starts (read, sharing
//      read and write but not delete, the path through the environment), page-actions-inuse.json (admin) passes: the
//      bulk delete keeps that bookmark as InUse, selected; released, the bookmark deletes with its file (204);
//   7. page-player-options.json (viewer) passes (the Player options dialog, no clip buttons for a user who can't make clips), with viewer's
//      preferences as they were afterwards;
//   8. page-tv.json and page-phone.json (admin) pass, unless --skip-layouts;
//   9. no Error of Bookmarks or the kit in the server log, no line of theirs at Information or above naming a bookmark;
//      clean-up (the setting, viewer's policy and preferences as they were, the movie folder back with the bookmark
//      attached again, no page-probe bookmark left).
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PROBE = 'page-probe';

function usage() {
    return 'usage: node scripts/bookmarks-page-probe.mjs --sandbox <name> [--skip-off] [--skip-layouts] [--timeout <scan seconds>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, off: true, layouts: true, timeout: 180 };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--skip-off') o.off = false;
        else if (a === '--skip-layouts') o.layouts = false;
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!Number.isFinite(o.timeout) || o.timeout < 10) throw new Error(`--timeout needs seconds (10 or more). ${usage()}`);
    return o;
}

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const workRoot = path.join(sandboxRoot, 'bookmarks-page-probe');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
if (!logDir.startsWith(sandboxRoot + path.sep)) throw new Error(`refusing to read the log folder ${logDir}: not in the sandbox`);
const storeFile = path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'bookmarks.json');
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer', 'viewer2']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}

const pluginGuid = /guid:\s*"?([0-9a-f-]{36})"?/i.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8'))[1];
const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error(`refusing to work in ${p}`);
    return full;
}
for (const p of [storeFile, workRoot]) assertInside(p, sandboxRoot);
// The shared sandbox media, the probe's hold folder and the record of the moves made (written before each move, undone
// in the finally and at the start of the next run), as bookmarks-rows-probe.mjs has them.
const mediaRoot = path.join(repoRoot, '.sandbox', 'media');
const heldRoot = path.join(workRoot, 'held');
const movesFile = path.join(workRoot, 'moves.json');
const HELD_MOVIE = 'Tagged Movie (2012) [tmdbid-900001]';
const DETACHED_NAME = `${PROBE} detached`;
// The library the unavailable webcheck takes out of viewer's policy, and viewer's bookmark on one of its videos.
const UNAVAILABLE_LIBRARY = 'Movies 2';
const UNAVAILABLE_NAME = `${PROBE} unavailable`;
// Admin's bookmark whose clip file the probe holds open while page-actions-inuse.json runs.
const HELD_CLIP_NAME = `${PROBE} inuse held`;
// The detached seed of this run (its id, for the attached-again check after the folder is put back).
let detachedSeed = null;

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, scans: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

async function call(who, method, p, { body, query, accept } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: accept || 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="bookmarks-page-probe", Device="sandbox", DeviceId="bookmarks-page-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text, contentType: res.headers.get('content-type') || '' };
}

async function getConfiguration() {
    const r = await call('admin', 'GET', CONFIGURATION_PATH);
    if (r.status !== 200 || !r.json) throw new Error(`GET ${CONFIGURATION_PATH}: ${r.status}`);
    return r.json;
}

async function setConfiguration(values) {
    const configuration = Object.assign(await getConfiguration(), values);
    const r = await call('admin', 'POST', CONFIGURATION_PATH, { body: configuration });
    if (r.status !== 204) throw new Error(`POST ${CONFIGURATION_PATH}: ${r.status}`);
    return getConfiguration();
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
        cwd: repoRoot, encoding: 'utf8', timeout: 900000, windowsHide: true
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

async function waitFor(label, predicate, seconds) {
    const until = Date.now() + seconds * 1000;
    for (;;) {
        const last = await predicate();
        if (last) return last;
        if (Date.now() > until) throw new Error(`timed out waiting for: ${label}`);
        await delay(500);
    }
}

// One full library scan, awaited through the RefreshLibrary task (as bookmarks-identity-probe.mjs waits for its scans).
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

// ------------------------------------------------------------------------------------------ media (undoable)
// As bookmarks-rows-probe.mjs: a move is recorded before it is made and only ever inside the sandbox media or the hold
// folder; undoMoves puts the moves back, last first.

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

// The held movie as the library has it (its Path under the sandbox's Movies folder).
async function heldMovie() {
    const r = await call('admin', 'GET', '/Items', { query: { userId: users.admin.id, includeItemTypes: 'Movie', recursive: 'true', fields: 'Path', enableImages: 'false', enableUserData: 'false' } });
    if (r.status !== 200 || !r.json) throw new Error(`GET /Items: ${r.status}`);
    const folder = path.join(mediaRoot, 'Movies', HELD_MOVIE) + path.sep;
    return (r.json.Items || []).find((m) => typeof m.Path === 'string' && m.Path.startsWith(folder)) || null;
}

// A detached bookmark for the Not in library filter: admin's bookmark on the held movie, whose folder is then
// renamed into the hold folder and the library scanned, so the identity service detaches it (the move is recorded
// first, so putBackHeld undoes it whatever happens).
async function seedDetached() {
    const movie = await heldMovie();
    if (!movie) throw new Error(`no movie in ${path.join('Movies', HELD_MOVIE)} (New-SandboxMedia.ps1 and a scan make it)`);
    const made = await call('admin', 'POST', '/Bookmarks', { body: { ItemId: movie.Id, StartTicks: 50_000_000, Name: DETACHED_NAME } });
    if (made.status !== 201 || !made.json) throw new Error(`POST /Bookmarks for the detached seed: ${made.status}`);
    detachedSeed = { bookmarkId: made.json.Id, movieId: movie.Id, movieName: movie.Name };
    move(path.join(mediaRoot, 'Movies', HELD_MOVIE), path.join(heldRoot, 'Movies', HELD_MOVIE));
    await scan(`${HELD_MOVIE} held out`);
    const detached = await waitFor('the seed detached', async () => {
        const b = await call('admin', 'GET', `/Bookmarks/${made.json.Id}`);
        return b.json && b.json.Detached === true ? b.json : null;
    }, 60);
    return { ...detachedSeed, held: path.join('Movies', HELD_MOVIE), detached: { Id: detached.Id, Detached: detached.Detached, ItemName: detached.ItemName, Relinked: detached.Relinked } };
}

// The held folder back in the media (this run's move, or a failed earlier run's), a scan, and this run's seed attached
// again while it exists.
async function putBackHeld(label) {
    if (loadMoves().length === 0) return { restored: false, reason: 'nothing held' };
    undoMoves(0);
    await scan(`${HELD_MOVIE} put back (${label})`);
    const result = { restored: loadMoves().length === 0 && fs.existsSync(path.join(mediaRoot, 'Movies', HELD_MOVIE)), label };
    if (detachedSeed) {
        const back = await waitFor('the seed attached again', async () => {
            const b = await call('admin', 'GET', `/Bookmarks/${detachedSeed.bookmarkId}`);
            if (b.status === 404) return { gone: true };
            return b.json && b.json.Detached === false ? b.json : null;
        }, 60).catch((error) => ({ error: String(error.message || error) }));
        result.seed = back.gone ? 'already deleted' : back.error ? back.error : { Detached: back.Detached, Relinked: back.Relinked };
    }
    return result;
}

// The page-probe bookmarks a webcheck left behind (a failed run), with their clips, for both users: 200 a request (the
// bulk delete's most; the actions webcheck seeds 205 at once) until none is left.
async function deleteLeftovers() {
    const left = [];
    for (const who of ['admin', 'viewer']) {
        for (let round = 0; round < 10; round++) {
            const r = await call(who, 'GET', '/Bookmarks/Query', { query: { searchTerm: PROBE, limit: 200 } });
            const ids = ((r.json && r.json.Items) || []).filter((b) => (b.Name || '').startsWith(PROBE) || (b.Note || '').startsWith(PROBE)).map((b) => b.Id);
            if (ids.length === 0) break;
            await call(who, 'POST', '/Bookmarks/BulkDelete', { body: { Ids: ids, DeleteClips: true } });
            left.push(...ids);
        }
    }
    return left;
}

// A movie of the sandbox's library by its name, as admin sees it.
async function findMovie(name) {
    const r = await call('admin', 'GET', '/Items', { query: { userId: users.admin.id, includeItemTypes: 'Movie', recursive: 'true', searchTerm: name, enableImages: 'false', enableUserData: 'false', limit: 1 } });
    const movie = r.json && r.json.Items && r.json.Items[0];
    if (!movie) throw new Error(`no movie "${name}" in the library (New-SandboxMedia.ps1 and a scan make it)`);
    return movie;
}

// Admin's "page-probe inuse held" range on Long Test Movie with a Fast clip made and complete, and the clip file's path
// as the store records it (read only; it must be inside the sandbox), for the held-open check below.
async function seedHeldClip() {
    const movie = await findMovie('Long Test Movie');
    const made = await call('admin', 'POST', '/Bookmarks', { body: { ItemId: movie.Id, StartTicks: 500_000_000, EndTicks: 540_000_000, Name: HELD_CLIP_NAME } });
    if (made.status !== 201 || !made.json) throw new Error(`POST /Bookmarks for the held clip: ${made.status}`);
    const id = made.json.Id;
    const clip = await call('admin', 'POST', `/Bookmarks/${id}/Clip`, { body: { Mode: 'Fast' } });
    if (clip.status !== 202 && clip.status !== 200) throw new Error(`POST /Bookmarks/${id}/Clip: ${clip.status}`);
    const done = await waitFor('the held clip complete', async () => {
        const b = await call('admin', 'GET', `/Bookmarks/${id}`);
        const status = b.json && b.json.Clip && b.json.Clip.Status;
        if (status === 'Failed') throw new Error(`the held clip failed: ${b.json.Clip.Error}`);
        return status === 'Completed' && b.json.Clip.FileName ? b.json : null;
    }, 120);
    // The store writes the id as a GUID with dashes, the API without them: compared without dashes, in either case.
    const guid = (value) => String(value).replace(/-/g, '').toLowerCase();
    const record = (readJson(storeFile).Bookmarks || []).find((b) => guid(b.Id) === guid(id));
    const file = record && record.Clip && record.Clip.FilePath;
    if (!file) throw new Error('the held clip has no file path in bookmarks.json');
    assertInside(file, sandboxRoot);
    if (!fs.existsSync(file)) throw new Error('the held clip has no file on disk');
    return { id, movieId: movie.Id, fileName: done.Clip.FileName, file };
}

// Holds a file open as a download or a preview does (read, sharing read and write but not delete: Kestrel's PhysicalFile
// opens clip files so), from a PowerShell process the probe starts, until the returned release() ends it; the path goes
// through the environment, never a command line (as bookmarks-api-probe.mjs's safe clip-file checks hold theirs).
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

async function run() {
    // ---- 1. the capabilities
    const capabilities = {};
    for (const who of ['admin', 'viewer']) {
        const r = await call(who, 'GET', '/Bookmarks/Capabilities');
        capabilities[who] = r.json && { status: r.status, Pages: r.json.Pages, IsAdmin: r.json.IsAdmin };
    }
    evidence.capabilities = capabilities;
    check('1. the capabilities offer the My Bookmarks page to admin and viewer (Pages.Bookmarks true), and My Clips (Pages.Clips true since My Clips)',
        ['admin', 'viewer'].every((who) => capabilities[who] && capabilities[who].status === 200 && capabilities[who].Pages.Bookmarks === true && capabilities[who].Pages.Clips === true),
        capabilities);

    // ---- 2. the shell and the head fragment over raw HTTP
    const shell = await call('viewer', 'GET', '/Bookmarks/Pages/bookmarks', { accept: 'text/html' });
    const anonymous = await call('anonymous', 'GET', '/Bookmarks/Pages/bookmarks', { accept: 'text/html' });
    const unknown = await call('viewer', 'GET', '/Bookmarks/Pages/nope', { accept: 'text/html' });
    const index = await call('anonymous', 'GET', '/web/index.html', { accept: 'text/html' });
    const head = /<script type="application\/json" data-jfbm-head data-jfbm-pages>([^<]*)<\/script>/.exec(index.text);
    const headState = head ? JSON.parse(head[1]) : null;
    evidence.shell = { status: shell.status, contentType: shell.contentType, marker: shell.text.includes('data-jfbm-page="bookmarks"'), body: shell.text.slice(0, 300) };
    evidence.anonymous = anonymous.status;
    evidence.unknown = { status: unknown.status, body: unknown.text.slice(0, 200) };
    evidence.index = { status: index.status, headStyles: (index.text.match(/<style data-jfbm-head>/g) || []).length, state: headState, tags: (index.text.match(/data-jfbm-plugin=/g) || []).length };
    check('2. the shell is 200 text/html with the page marker for a user and 401 anonymous, an unknown key is 404 (the clips key is the My Clips page\'s; bookmarks-clips-page-probe.mjs checks its shell), and index.html carries the head fragment once with bookmarks known and on',
        shell.status === 200 && /^text\/html/.test(shell.contentType) && evidence.shell.marker && anonymous.status === 401 && unknown.status === 404 && index.status === 200
        && evidence.index.headStyles === 1 && evidence.index.tags === 1 && !!headState && headState.pages.includes('bookmarks') && !headState.off.includes('bookmarks'),
        { shell: evidence.shell, anonymous: evidence.anonymous, unknown: evidence.unknown, index: evidence.index });

    // ---- 3. a detached bookmark, then the page in the web client
    evidence.detached = await seedDetached();
    // GET /Bookmarks/{id} carries Detached and Relinked (ItemAvailable is the Query and Groups pages' per-viewer field).
    check('3. a bookmark on a movie whose folder is then held out of the sandbox media and the library scanned is Detached (Not in library; the identity service)',
        evidence.detached.detached.Detached === true && evidence.detached.detached.Relinked === false, evidence.detached);

    const page = webcheck('page-bookmarks');
    evidence.page = {
        ok: page.ok, exit: page.exit, seconds: page.seconds, steps: page.steps, failed: page.failed, failedRequests: page.failedRequests, exceptions: page.exceptions, closed: page.closed,
        counts: page.value('what the API counts: all, episodes, ranges, clips, unnamed, \'page-probe extra\', the groups, the detached'),
        grouped: page.value('the page renders grouped on its route: the title, no \'Page not found\', the toolbar, the count line, the groups'),
        flat: page.value('the flat view: 50 rows with the video named under each title, Load more, the flat sort list, one history entry, no kit render'),
        more: page.value('the next page appended, page=2 in the hash in place'),
        created: page.value('in the API\'s order for sortBy=CreatedAt, each created no later than the one before it'),
        position: page.value('in the API\'s order for sortBy=Position; within a title the positions don\'t decrease, in the API\'s items and on the rows'),
        missing: page.value('as the API counts (availability=Missing): every row detached, badged, unplayable, its video unlinked; with none, the no-match notice'),
        reloaded: page.value('after the reload: the toolbar and the list as the hash says'),
        landed: page.value('back on the page: that group marked and scrolled to, group= dropped from the hash in place (one landing)'),
        cleanup: page.value('clean-up: the seeded bookmarks deleted with their clip')
    };
    const missing = evidence.page.missing;
    check('3. page-bookmarks.json (admin): the Library tools link, the grouped and flat views with each view\'s sort list (Recently created and Position in title in the API\'s order), paging, the state in the hash (sort, type, filters, search, deep link reload, Back and Forward), Not in library showing the detached seed with the API\'s count, See all, Open title and Back (the group marked once), the shell',
        page.ok && !!missing && missing.api >= 1 && missing.seeded === true, { exit: page.exit, failed: page.failed, missing });

    // ---- 4. the page turned off
    if (opts.off) {
        const off = webcheck('page-bookmarks-off');
        const after = await getConfiguration();
        evidence.off = { ok: off.ok, exit: off.exit, seconds: off.seconds, steps: off.steps, failed: off.failed, failedRequests: off.failedRequests, closed: off.closed,
            route: off.value('the note under the page\'s own title, no \'Page not found\', no toolbar'), shell: off.value('the shell: 200 text/html, the note, no page marker'), enabledAfter: after.EnableBookmarksPage };
        check('4. page-bookmarks-off.json (admin): with EnableBookmarksPage off no link, the route and the shell show "My Bookmarks is turned off by your server admin." (200, no marker), the capabilities say so, no See all (My Clips still linked), no My Bookmarks link in the My Clips page\'s header; on again, the link is back',
            off.ok && after.EnableBookmarksPage === true, { exit: off.exit, failed: off.failed, enabledAfter: after.EnableBookmarksPage });
    }

    // ---- 5. viewer's bookmark on a Movies 2 video: viewer2 can't see it, then the Unavailable group
    const seed = await seedUnavailable();
    const asViewer2 = await call('viewer2', 'GET', `/Bookmarks/${seed.bookmarkId}`);
    const viewer2Query = await call('viewer2', 'GET', '/Bookmarks/Query', { query: { searchTerm: UNAVAILABLE_NAME, limit: 5 } });
    evidence.isolation = { bookmark: { movieName: seed.movieName, library: UNAVAILABLE_LIBRARY }, viewer2Status: asViewer2.status, viewer2Total: viewer2Query.json && viewer2Query.json.TotalRecordCount };
    check('5. viewer2 can\'t see viewer\'s bookmark: 404 on its id, and absent from viewer2\'s Query',
        asViewer2.status === 404 && viewer2Query.status === 200 && viewer2Query.json.TotalRecordCount === 0, evidence.isolation);
    const unavailable = webcheck('page-unavailable');
    const policyAfter = await viewerPolicy();
    evidence.unavailable = { ok: unavailable.ok, exit: unavailable.exit, seconds: unavailable.seconds, steps: unavailable.steps, failed: unavailable.failed, failedRequests: unavailable.failedRequests, exceptions: unavailable.exceptions, closed: unavailable.closed,
        group: unavailable.value('the group: Unavailable with the placeholder, no poster request, the name unlinked, no Open title; the row: the badge, play disabled, no scissors, Rename and Edit and Delete'),
        flat: unavailable.value('the flat row: the badge, the video named but not linked, play disabled'),
        regained: unavailable.value('the group available again: no badge, the poster requested, Open title, the row playable'),
        policyAsBefore: samePolicy(policyAfter, context.viewerPolicy) };
    check('5. page-unavailable.json: with "Movies 2" taken out of viewer\'s libraries the group shows Unavailable with a placeholder (no Items/{id}/Images request), its name unlinked, the row unplayable without scissors; the library back, the poster is requested again; viewer\'s policy as it was',
        unavailable.ok && evidence.unavailable.policyAsBefore, { exit: unavailable.exit, failed: unavailable.failed, policyAsBefore: evidence.unavailable.policyAsBefore });

    // ---- 6. the row actions
    const actions = webcheck('page-actions');
    evidence.actions = { ok: actions.ok, exit: actions.exit, seconds: actions.seconds, steps: actions.steps, failed: actions.failed, failedRequests: actions.failedRequests, exceptions: actions.exceptions, closed: actions.closed,
        rows: actions.value('the group with the four rows: A with Name it and the point scissors, B and D with the clip ready and View in My Clips, C with Rename and the point scissors; a checkbox per row; My Clips and Player options in the header'),
        renamed: actions.value('named: the row drawn again with the name and Rename, the server has it with the time unchanged, the toast; only the video\'s group and the count line were read (no page of 25 groups)'),
        conflict: actions.value('the field shows the name it has now, selected, under \'Changed elsewhere\''),
        bulkKept: actions.value('deleted: the dialog gone, the toast, the two rows gone and the count line following; B and D are 404, their clip files still download (tombstones)'),
        bulkGone: actions.value('deleted with the clip: E and F 404, E\'s clip file 404, two rows left'),
        manyRows: actions.value('the 205 rows shown, the count line counting them'),
        manyProgress: actions.value('Delete: the progress line\'s texts while the batches go (recorded by a MutationObserver)'),
        manyDeleted: actions.value('deleted in two requests (200 then 5): the dialog gone, the toast \'205 bookmarks deleted.\', no row left, the no-match notice, the bar hidden, the server has none left'),
        twoRenames: actions.value('the group drawn again (B\'s row a fresh node, queued or being made); C\'s row the same node with the typed name and the focus still in its field, and A\'s the same node with its typed name (the review: only the first open rename was kept)'),
        stopRunning: actions.value('Delete; while the first request is held, the progress line and Cancel enabled; the focus to Cancel'),
        stopped: actions.value('the held request answered, the run stopped there: the toast saying so, the five left in the list and still ticked, the bar \'5 bookmarks selected\', one BulkDelete request since the click, five left on the server') };
    check('6. page-actions.json (admin): the inline rename (Name it, Rename, Enter and Escape, a 409 showing the latest name, only the video\'s group read again), the point bookmark\'s scissors opening the editor on End, the bulk select bar, the bulk delete keeping the clips as tombstones (unticked by default since My Clips) and deleting them (ticked), Select all over 205 rows deleted in two requests with the progress line, two renames open at once both kept through the group\'s redraw, a delete of 205 closed with Escape during its first request stopping there with the toast; the header\'s My Clips link and the clip lines\' View in My Clips',
        actions.ok, { exit: actions.exit, failed: actions.failed });

    // ---- 6. the bulk delete with a clip file in use, for real: the file held open as a download holds it
    const held = await seedHeldClip();
    context.releaseHeld = await holdOpen(held.file);
    let inUse;
    try {
        inUse = webcheck('page-actions-inuse');
    } finally {
        await context.releaseHeld();
        context.releaseHeld = null;
    }
    const stillThere = await call('admin', 'GET', `/Bookmarks/${held.id}`);
    const released = await call('admin', 'DELETE', `/Bookmarks/${held.id}`, { query: { deleteClip: 'true' } });
    evidence.inUse = { ok: inUse.ok, exit: inUse.exit, seconds: inUse.seconds, steps: inUse.steps, failed: inUse.failed, failedRequests: inUse.failedRequests, exceptions: inUse.exceptions, closed: inUse.closed,
        fileName: held.fileName,
        result: inUse.value('the point deleted, the held one kept as InUse: the dialog stays with the sentence and the held one alone to delete; its row ticked in the group drawn again, the bar \'1 bookmark selected\'; the server still has it with its clip file'),
        keptWhileHeld: stillThere.status === 200 && !!stillThere.json && !!stillThere.json.Clip && stillThere.json.Clip.FileName === held.fileName,
        releasedDelete: released.status,
        fileGone: !fs.existsSync(held.file) };
    check('6. page-actions-inuse.json (admin): with the held bookmark\'s clip file held open the bulk delete with the clips deletes the point and keeps the held one as InUse after the server\'s three attempts, the dialog saying so with the held row still ticked in the group drawn again; released, the bookmark deletes with its file (204, the file gone)',
        inUse.ok && evidence.inUse.keptWhileHeld && released.status === 204 && evidence.inUse.fileGone, { exit: inUse.exit, failed: inUse.failed, keptWhileHeld: evidence.inUse.keptWhileHeld, releasedDelete: released.status, fileGone: evidence.inUse.fileGone });

    // ---- 7. Player options as viewer (the dialog; no clip buttons)
    const options = webcheck('page-player-options');
    const preferencesAfter = await call('viewer', 'GET', '/Bookmarks/Preferences');
    evidence.playerOptions = { ok: options.ok, exit: options.exit, seconds: options.seconds, steps: options.steps, failed: options.failed, failedRequests: options.failedRequests, exceptions: options.exceptions, closed: options.closed,
        dialog: options.value('the dialog: the preferences read fresh (GET /Bookmarks/Preferences), Use the server\'s default (don\'t ask), the pause off, the device field empty with the server\'s key as its placeholder, the focus on the first field'),
        reserved: options.value('refused inline, Save disabled'),
        saved: options.value('saved: the dialog gone, the toast, the server\'s preferences (AskForName true, PauseWhileNaming true), the device override in localStorage, the capabilities read again and following'),
        shiftB: options.value('nothing: no bookmark on the movie but the seeded range, no dialog, still playing'),
        shiftY: options.value('a bookmark at once, the name dialog open at once (Always ask) with \'Bank job\', the video paused (Pause while naming)'),
        preferencesAfter: preferencesAfter.json };
    check('7. page-player-options.json (viewer): viewer\'s rows offer no scissors; Player options reads the preferences fresh, refuses M, K and B inline with Save disabled, saves Always, the pause and Shift+Y; in the player Shift+B then does nothing and Shift+Y opens the name dialog at once with the video paused; Reset to default puts it back; viewer\'s preferences as they were',
        options.ok && !!preferencesAfter.json && askForNameOf(preferencesAfter.json) === context.viewerPreferences.AskForName && preferencesAfter.json.PauseWhileNaming === context.viewerPreferences.PauseWhileNaming,
        { exit: options.exit, failed: options.failed, preferencesAfter: preferencesAfter.json });

    // ---- 8. the TV layout and a phone
    if (opts.layouts) {
        const tv = webcheck('page-tv');
        evidence.tv = { ok: tv.ok, exit: tv.exit, seconds: tv.seconds, steps: tv.steps, failed: tv.failed, failedRequests: tv.failedRequests, exceptions: tv.exceptions, closed: tv.closed,
            page: tv.value('TV: the page with jfbm-page-tv, the Select toggle in the toolbar, the checkbox column and the bulk bar hidden, every row action a focusable button'),
            toolbarLine: tv.value('the toolbar at 1366 px: the Select toggle on the search box\'s line, not wrapped under it (the search box starts at 8em on a TV)'),
            selecting: tv.value('the checkbox column shown; tick the range') };
        check('8. page-tv.json (admin, the TV layout): the checkbox column and the bulk bar hidden until Select, dropped when it is turned off; the inline rename from the keyboard; the Player options dialog takes the D-pad in order and Escape; the row menu too',
            tv.ok, { exit: tv.exit, failed: tv.failed });
        const phone = webcheck('page-phone');
        evidence.phone = { ok: phone.ok, exit: phone.exit, seconds: phone.seconds, steps: phone.steps, failed: phone.failed, failedRequests: phone.failedRequests, exceptions: phone.exceptions, closed: phone.closed,
            rows: phone.value('mobile: the page fits in 390 px; each row\'s action buttons are hidden but More (at least 40 px); the checkbox label is at least 40 px; the toolbar\'s buttons are at least 40 px tall'),
            menu: phone.value('the menu: a full-width bottom sheet titled with the bookmark, its items at least 40 px'),
            light: phone.value('the page in Light: the theme sheet loaded, the accent resolved, the rows there'),
            appletv: phone.value('the page in Apple TV: the theme sheet loaded, the accent resolved, the rows there, the bulk bar\'s primary button in the accent too'),
            purplehaze: phone.value('the page in Purple Haze: the theme sheet loaded, the accent resolved, the rows there') };
        check('8. page-phone.json (admin, 390x844 touch, the mobile layout): the page fits, the actions collapse into a 40 px More button whose menu is a bottom sheet of 40 px items, the checkbox and rename targets are 40 px, the bulk delete and Player options dialogs are bottom sheets, the Light, Apple TV and Purple Haze themes (item 9)',
            phone.ok, { exit: phone.exit, failed: phone.failed });
    }
}

// A user's AskForName as the preferences endpoint answers it: null (the server's default) is left out of the JSON.
function askForNameOf(preferences) {
    return preferences && typeof preferences.AskForName === 'boolean' ? preferences.AskForName : null;
}

// Viewer's policy as the server has it (the whole object: the unavailable webcheck posts it back whole).
async function viewerPolicy() {
    const r = await call('admin', 'GET', `/Users/${users.viewer.id}`);
    if (r.status !== 200 || !r.json || !r.json.Policy) throw new Error(`GET /Users/viewer: ${r.status}`);
    return r.json.Policy;
}

// Whether two policies give the same libraries (what the unavailable webcheck changes and restores).
function samePolicy(a, b) {
    return !!a && !!b && a.EnableAllFolders === b.EnableAllFolders && JSON.stringify([...(a.EnabledFolders || [])].sort()) === JSON.stringify([...(b.EnabledFolders || [])].sort());
}

// Viewer's bookmark on a video of the "Movies 2" library (seen by viewer now; the webcheck takes the library away).
async function seedUnavailable() {
    const folders = await call('admin', 'GET', '/Library/VirtualFolders');
    const library = ((folders.json) || []).find((f) => f.Name === UNAVAILABLE_LIBRARY);
    if (!library) throw new Error(`no "${UNAVAILABLE_LIBRARY}" library (Initialize-Sandbox.ps1 makes it)`);
    const items = await call('viewer', 'GET', '/Items', { query: { userId: users.viewer.id, parentId: library.ItemId, includeItemTypes: 'Movie', recursive: 'true', enableImages: 'false', enableUserData: 'false', sortBy: 'SortName', limit: 1 } });
    const movie = items.json && items.json.Items && items.json.Items[0];
    if (!movie) throw new Error(`viewer sees no movie in "${UNAVAILABLE_LIBRARY}"`);
    const made = await call('viewer', 'POST', '/Bookmarks', { body: { ItemId: movie.Id, StartTicks: 70_000_000, Name: UNAVAILABLE_NAME } });
    if (made.status !== 201 || !made.json) throw new Error(`POST /Bookmarks for the unavailable seed: ${made.status}`);
    return { bookmarkId: made.json.Id, movieId: movie.Id, movieName: movie.Name };
}

// What the run must leave as it found it, besides the setting: viewer's policy (the unavailable webcheck changes it)
// and viewer's preferences (the Player options webcheck saves them); and the clip file held open for the in-use check,
// released in the finally when the run died while it was held (so the leftovers' delete can take its bookmark).
const context = { viewerPolicy: null, viewerPreferences: null, releaseHeld: null };

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const startLog = logPosition();
    const original = await getConfiguration();
    context.viewerPolicy = await viewerPolicy();
    const preferences = await call('viewer', 'GET', '/Bookmarks/Preferences');
    if (preferences.status !== 200 || !preferences.json) throw new Error(`GET /Bookmarks/Preferences as viewer: ${preferences.status}`);
    context.viewerPreferences = { AskForName: askForNameOf(preferences.json), PauseWhileNaming: preferences.json.PauseWhileNaming === true };
    // A failed earlier run's held folder first, then its bookmarks.
    evidence.undidHeld = await putBackHeld('an earlier run');
    evidence.undidLeftovers = await deleteLeftovers();
    let failure = null;
    try {
        await run();
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        const cleanup = { configurationRestored: null, deleted: [] };
        cleanup.held = null;
        cleanup.viewerPolicyRestored = null;
        cleanup.viewerPreferencesRestored = null;
        try {
            if (context.releaseHeld) {
                await context.releaseHeld();
                context.releaseHeld = null;
            }
            const current = await getConfiguration();
            if (current.EnableBookmarksPage !== original.EnableBookmarksPage) {
                await setConfiguration({ EnableBookmarksPage: original.EnableBookmarksPage });
            }
            cleanup.configurationRestored = (await getConfiguration()).EnableBookmarksPage === original.EnableBookmarksPage;
            if (!samePolicy(await viewerPolicy(), context.viewerPolicy)) {
                const restored = await call('admin', 'POST', `/Users/${users.viewer.id}/Policy`, { body: context.viewerPolicy });
                if (restored.status !== 204) throw new Error(`POST /Users/viewer/Policy (put back): ${restored.status}`);
            }
            cleanup.viewerPolicyRestored = samePolicy(await viewerPolicy(), context.viewerPolicy);
            const put = await call('viewer', 'POST', '/Bookmarks/Preferences', { body: context.viewerPreferences });
            const now = await call('viewer', 'GET', '/Bookmarks/Preferences');
            cleanup.viewerPreferencesRestored = put.status === 204 && !!now.json && askForNameOf(now.json) === context.viewerPreferences.AskForName && now.json.PauseWhileNaming === context.viewerPreferences.PauseWhileNaming;
            cleanup.held = await putBackHeld('this run');
            cleanup.deleted = await deleteLeftovers();
        } catch (error) {
            check('clean-up: the setting, the media and the bookmarks put back', false, String(error));
        }
        evidence.cleanup = cleanup;
        const text = logSince(startLog);
        const lines = text.split('\n');
        const errors = lines.filter((l) => /\[ERR\]|\[FTL\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        const naming = lines.filter((l) => /\[(INF|WRN|ERR|FTL)\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l) && l.includes(PROBE));
        check('9. no Error of Bookmarks or the kit in the server log during the run, and no line of theirs at Information or above naming a bookmark',
            errors.length === 0 && naming.length === 0, { errors: errors.slice(0, 5), naming: naming.slice(0, 5) });
        const left = [];
        for (const who of ['admin', 'viewer']) {
            const r = await call(who, 'GET', '/Bookmarks/Query', { query: { searchTerm: PROBE, limit: 1 } });
            if (r.json && r.json.TotalRecordCount > 0) left.push(who);
        }
        check('9. clean-up: EnableBookmarksPage, viewer\'s policy and preferences as they were, no media change left (the held folder back, the seed attached again before it was deleted), no page-probe bookmark left',
            cleanup.configurationRestored === true && cleanup.viewerPolicyRestored === true && cleanup.viewerPreferencesRestored === true && loadMoves().length === 0 && left.length === 0, { cleanup, moves: loadMoves().length, left });
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
