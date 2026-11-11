#!/usr/bin/env node
// Bookmarks' free-space guard in a running sandbox: with MinFreeSpaceMegabytes above C:'s free space a make or remake is
// refused with the message (admin wording vs viewer wording), also for a user whose per-user clip folder doesn't exist
// yet; then, with it restored, a long Accurate clip started and the setting raised above twice C:'s free space
// mid-encode, the watchdog stops the job and no .partial remains. Node 22 built-ins only. Loopback only, never port
// 8096. The sandbox users' tokens come from credentials.json and are never printed. It changes only Bookmarks' settings in
// the sandbox (MinFreeSpaceMegabytes, ClipOutputPath, VideoPreset), through the sandbox's API, and puts them back in a
// finally; it writes files only under .sandbox/<name>/clip-guard-probe (its results and the webcheck's).
//
//   node scripts/clip-guard-probe.mjs --sandbox default [--movie "Long Test Movie"] [--skip-webcheck] [--timeout 180] [--out result.json]
//
// The run, as admin unless noted ("viewer2" is the sandbox user with download permission and no admin rights):
//   0. Diagnostics' ClipStorage for the sandbox's clip folder: there, Ok, on C:\, its free space what the probe itself reads
//      (fs.statfs, within 2 GB), 2 GB to keep free, no LowFreeSpace, no warning.
//   1. With room: a Fast clip is made (it keeps an estimate in bookmarks.json, EstimatedBytes).
//   2. MinFreeSpaceMegabytes above C:'s free space: making a clip is 503 with ProblemDetails, the drive and the numbers for
//      admin and the user's wording for viewer2, whose per-user clip folder doesn't exist and isn't created; a remake of the
//      clip of step 1 is 503 and leaves it as it was; nothing is queued; Diagnostics says LowFreeSpace with its warning; the
//      log has the refusals at Information; the settings page shows the line and the warning under the Clip folder field
//      (webcheck clip-guard-settings.json), and viewer2's Make a clip on the details page shows the refusal as a toast in
//      the user's words (webcheck clip-guard-refused.json).
//   3. A clip folder that doesn't exist yet (ClipOutputPath set to a new folder in the probe's work folder): still 503 for
//      both (measured by the folder above), nothing created, Diagnostics "doesn't exist yet".
//   4. A clip folder on a drive letter that isn't there: 503 "The clip folder isn't available right now" (admin: with the
//      path), even with the reserve back at 2 GB; Diagnostics FolderUnavailable.
//   5. The check when a job starts: a long Accurate clip runs (VideoPreset slow, so it runs long enough), a short Fast one
//      waits behind it (its request passed); MinFreeSpaceMegabytes is raised above C:'s free space (but below twice it, so
//      the watchdog leaves the running one alone), the long one is cancelled, and the waiting one fails with the user's
//      wording when it is about to start, writing nothing; the log has the Warning.
//   6. The watchdog: with the reserve back, a long Accurate clip starts; once it reports progress MinFreeSpaceMegabytes is
//      raised above twice C:'s free space; the job fails with "Stopped: the clip drive is almost full." within the
//      watchdog's 5 s (plus a probe), and no .partial is left; the log has the Warning.
//   7. The log of the run has no Error from Bookmarks and no entry at Information or above naming a note; with the
//      settings back, the settings page shows the line without a warning (webcheck again).
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const TICKS = 10_000_000;
const MIB = 1024 * 1024;
const GIB = 1024 * MIB;
const NOTE = 'clip-guard-probe';
const USER_LOW_SPACE = 'Not enough free space on the server for this clip. Ask your server admin.';
const USER_UNAVAILABLE = "The clip folder isn't available right now. Ask your server admin.";
const ALMOST_FULL = 'Stopped: the clip drive is almost full.';

function usage() {
    return 'usage: node scripts/clip-guard-probe.mjs --sandbox <name> [--movie <name>] [--skip-webcheck] [--timeout <s>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, movie: 'Long Test Movie', webcheck: true, timeout: 180, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--movie') o.movie = argv[++i];
        else if (a === '--skip-webcheck') o.webcheck = false;
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
const workRoot = path.join(sandboxRoot, 'clip-guard-probe');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^\uFEFF/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer2']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}
const bookmarksGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8')) || [])[1];
if (!bookmarksGuid) throw new Error('no guid in plugins/Bookmarks/build.yaml');
const dataFolder = path.join(path.resolve(state.DataDir || path.join(sandboxRoot, 'data')), 'data', 'bookmarks');
const storeFile = path.join(dataFolder, 'bookmarks.json');
for (const p of [dataFolder, workRoot]) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), p);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(p)) throw new Error(`refusing to work in ${p}`);
}

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
// The log names ids with dashes ("D"); the API writes them without ("N").
const dashed = (id) => guid(id).replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5');
const samePath = (a, b) => typeof a === 'string' && typeof b === 'string' && path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json', Authorization: `MediaBrowser Client="clip-guard-probe", Device="sandbox", DeviceId="clip-guard-probe", Version="1.0.0", Token="${users[who].token}"` };
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(60000) });
    const text = await res.text();
    const type = res.headers.get('content-type') || '';
    let json = null;
    if (text && /json/i.test(type)) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, type, json };
}

const problem = (r, status) => r.status === status && /application\/problem\+json/i.test(r.type) && r.json && typeof r.json.detail === 'string';
const detail = (r) => (r.json ? { status: r.status, detail: r.json.detail } : { status: r.status });

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

async function diagnostics() {
    const r = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    if (r.status !== 200 || !r.json) throw new Error(`GET /Bookmarks/Admin/Diagnostics: ${r.status}`);
    return r.json.ClipStorage;
}

// The free space of the volume a folder is on, as this machine reports it (the nearest existing folder at or above it).
function freeBytes(folder) {
    let current = path.resolve(folder);
    while (!fs.existsSync(current)) current = path.dirname(current);
    const s = fs.statfsSync(current);
    return Number(s.bavail) * Number(s.bsize);
}

async function findMovie() {
    const r = await call('admin', 'GET', '/Items', { query: { userId: users.admin.id, IncludeItemTypes: 'Movie', Recursive: 'true', SearchTerm: opts.movie, Limit: 5 } });
    const item = r.json && r.json.Items && r.json.Items.find((i) => i.Name === opts.movie || i.Name.startsWith(`${opts.movie} (`));
    if (!item) throw new Error(`the sandbox has no movie named ${opts.movie}; run Initialize-Sandbox.ps1`);
    return { id: item.Id, name: item.Name, runTimeTicks: item.RunTimeTicks };
}

const created = [];
async function bookmark(who, movie, startSeconds, endSeconds, label) {
    const r = await call(who, 'POST', '/Bookmarks', { body: { ItemId: movie.id, StartTicks: startSeconds * TICKS, EndTicks: endSeconds * TICKS, Note: `${NOTE} ${label}` } });
    if (r.status !== 201) throw new Error(`the bookmark "${label}" could not be created: ${r.status}`);
    created.push({ who, id: r.json.Id });
    return r.json;
}

async function clipOf(who, id) {
    const r = await call(who, 'GET', `/Bookmarks/${id}`);
    return r.json && r.json.Clip;
}

async function waitFor(label, condition, seconds = opts.timeout) {
    const until = Date.now() + seconds * 1000;
    for (;;) {
        const value = await condition();
        if (value) return value;
        if (Date.now() > until) throw new Error(`${label}: timed out after ${seconds} s`);
        await delay(200);
    }
}

async function waitForEnd(who, id, label, seconds = opts.timeout) {
    return waitFor(`${label} to end`, async () => {
        const clip = await clipOf(who, id);
        return clip && !['Queued', 'Running'].includes(clip.Status) ? clip : null;
    }, seconds);
}

function storedRecord(id) {
    return (readJson(storeFile).Bookmarks || []).find((r) => guid(r.Id) === guid(id)) || null;
}

function listFiles(folder, pattern) {
    return fs.existsSync(folder) ? fs.readdirSync(folder).filter((f) => pattern.test(f)) : [];
}

// The sandbox server's log since its last start.
function serverLogSinceStart() {
    const logDir = state.LogDir || path.join(sandboxRoot, 'log');
    if (!fs.existsSync(logDir)) return '';
    const newest = fs.readdirSync(logDir).filter((f) => /^log_.*\.log$/.test(f))
        .map((f) => ({ file: path.join(logDir, f), mtime: fs.statSync(path.join(logDir, f)).mtimeMs }))
        .sort((a, b) => b.mtime - a.mtime)[0];
    if (!newest) return '';
    const text = fs.readFileSync(newest.file, 'utf8');
    const start = text.lastIndexOf('Main: Jellyfin version:');
    return start < 0 ? text : text.slice(start);
}

// Log entries at a level or above ('INF': [INF], [WRN], [ERR], [FTL]) with their continuation lines.
function logEntriesAtOrAbove(text, level) {
    const levels = ['VRB', 'DBG', 'INF', 'WRN', 'ERR', 'FTL'];
    const entries = [];
    let current = null;
    for (const line of text.split(/\r?\n/)) {
        const head = /^\[[^\]]+\] \[([A-Z]{3})\]/.exec(line);
        if (head) {
            current = { level: head[1], text: line };
            entries.push(current);
        } else if (current) {
            current.text += '\n' + line;
        }
    }
    return entries.filter((e) => levels.indexOf(e.level) >= levels.indexOf(level));
}

async function waitForLog(since, pattern, seconds = 15) {
    return waitFor(`the log line ${pattern}`, async () => serverLogSinceStart().slice(since).split(/\r?\n/).find((l) => pattern.test(l)) || null, seconds).catch(() => null);
}

// A webcheck of scripts/webchecks (`<name>.json`, whose screenshot is `<name>.png`); its result and screenshot are kept in
// the work folder under the given label.
function webcheck(name, label) {
    const out = path.join(workRoot, `webcheck-${name}-${label}.json`);
    const started = Date.now();
    const w = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', `${name}.json`, '--out', out], {
        cwd: repoRoot, encoding: 'utf8', timeout: 300000, windowsHide: true
    });
    let result = null;
    try { result = readJson(out); } catch { result = null; }
    const shot = path.join(sandboxRoot, 'browser', 'screenshots', `${name}.png`);
    if (fs.existsSync(shot)) fs.copyFileSync(shot, path.join(workRoot, `${name}-${label}.png`));
    const shown = result && result.steps && (result.steps.find((s) => s.name === 'what the page shows') || {}).value;
    return { ok: w.status === 0 && !!result && result.ok === true, exit: w.status, seconds: Math.round((Date.now() - started) / 1000), shown, leftovers: result && result.leftovers };
}

const settingsWebcheck = (label) => webcheck('clip-guard-settings', label);

// A size as the guard writes it (ClipStorageGuard.FormatSize): "465 KB", "1.5 MB", "0.3 GB".
const SIZE = String.raw`[0-9.]+ (?:KB|MB|GB)`;
const ADMIN_LOW_SPACE = new RegExp(String.raw`^Not enough free space for this clip: C:\\ has ${SIZE} free; this clip needs about ${SIZE}(?:, the other clips waiting or being made about ${SIZE})?, and ${SIZE} must stay free \("Keep free on the clip drive" in the Bookmarks settings\)\.$`);

async function run(context) {
    fs.mkdirSync(workRoot, { recursive: true });
    const logStart = serverLogSinceStart().length;
    const movie = await findMovie();
    evidence.movie = movie;
    const config = await getConfig();
    context.saved = { MinFreeSpaceMegabytes: config.MinFreeSpaceMegabytes, ClipOutputPath: config.ClipOutputPath ?? '', VideoPreset: config.VideoPreset };
    evidence.savedSettings = context.saved;
    check('the setting MinFreeSpaceMegabytes is there, 2048 by default', config.MinFreeSpaceMegabytes === 2048, { MinFreeSpaceMegabytes: config.MinFreeSpaceMegabytes });
    const clipRoot = context.saved.ClipOutputPath ? path.resolve(sandboxRoot, context.saved.ClipOutputPath) : path.join(dataFolder, 'clips');
    const adminFolder = path.join(clipRoot, 'admin');
    const viewer2Folder = path.join(clipRoot, 'viewer2');

    const capsAdmin = (await call('admin', 'GET', '/Bookmarks/Capabilities')).json || {};
    const capsViewer2 = (await call('viewer2', 'GET', '/Bookmarks/Capabilities')).json || {};
    check('admin is an admin who can make clips; viewer2 can make clips and is no admin', capsAdmin.IsAdmin === true && capsAdmin.CanCreateClips === true
        && capsViewer2.IsAdmin === false && capsViewer2.CanCreateClips === true, { admin: capsAdmin, viewer2: capsViewer2 });

    // 0. Diagnostics with room.
    let storage = await diagnostics();
    let free = freeBytes(clipRoot);
    evidence.diagnosticsWithRoom = storage;
    check('0. ClipStorage: the clip folder, there, Ok on C:\\, its free space as this machine reads it (within 2 GB), 2 GB to keep free, no warning', !!storage
        && samePath(storage.Root, clipRoot) && storage.Exists === true && storage.FreeSpace === 'Ok' && storage.Volume === 'C:\\'
        && Math.abs(storage.FreeBytes - free) < 2 * GIB && storage.TotalBytes >= storage.FreeBytes && storage.MinFreeBytes === 2048 * MIB
        && storage.LowFreeSpace === false && Array.isArray(storage.Warnings) && storage.Warnings.length === 0
        && /^Free space for clips: C:\\ has [0-9.]+ GB free of [0-9.]+ GB\. Clips are refused when fewer than 2 GB would remain\.$/.test(storage.Message),
    { storage, statfsFree: free });

    // 1. A clip made with room keeps its estimate.
    const remake = await bookmark('admin', movie, 30, 33, 'remake');
    const queued = await call('admin', 'POST', `/Bookmarks/${remake.Id}/Clip`, { body: { Mode: 'Fast' } });
    const made = await waitForEnd('admin', remake.Id, 'the Fast clip');
    const madeRecord = storedRecord(remake.Id);
    evidence.withRoom = { status: queued.status, clip: made, estimatedBytes: madeRecord && madeRecord.Clip && madeRecord.Clip.EstimatedBytes };
    check('1. with room a Fast clip is 202 and made, and bookmarks.json keeps its estimate (EstimatedBytes)', queued.status === 202 && made.Status === 'Completed'
        && madeRecord && madeRecord.Clip && Number.isInteger(madeRecord.Clip.EstimatedBytes) && madeRecord.Clip.EstimatedBytes > 0, evidence.withRoom);

    // 2. The reserve above C:'s free space.
    free = freeBytes(clipRoot);
    const aboveFree = Math.ceil(free / MIB) + 1024;
    await setConfig({ MinFreeSpaceMegabytes: aboveFree });
    evidence.aboveFree = { freeBytes: free, MinFreeSpaceMegabytes: aboveFree };
    const refusedLog = serverLogSinceStart().length;
    const adminBookmark = await bookmark('admin', movie, 20, 26, 'admin');
    const adminRefused = await call('admin', 'POST', `/Bookmarks/${adminBookmark.Id}/Clip`, { body: { Mode: 'Accurate' } });
    check('2. above C:\'s free space, admin\'s clip is 503 with ProblemDetails naming the drive and the numbers', problem(adminRefused, 503) && ADMIN_LOW_SPACE.test(adminRefused.json.detail),
        detail(adminRefused));
    // An empty per-user folder left by an earlier probe's clips of viewer2 (Bookmarks never removes one) goes first, so the
    // check sees that a refused clip makes none; rmdir takes away an empty folder only.
    const viewer2FolderEmptied = fs.existsSync(viewer2Folder) && fs.readdirSync(viewer2Folder).length === 0;
    if (viewer2FolderEmptied) fs.rmdirSync(viewer2Folder);
    const viewer2FolderBefore = fs.existsSync(viewer2Folder);
    const viewer2Bookmark = await bookmark('viewer2', movie, 20, 26, 'viewer2');
    const viewer2Refused = await call('viewer2', 'POST', `/Bookmarks/${viewer2Bookmark.Id}/Clip`, { body: { Mode: 'Accurate' } });
    check('2. viewer2\'s clip is 503 with the user\'s wording (no drive, no numbers)', problem(viewer2Refused, 503) && viewer2Refused.json.detail === USER_LOW_SPACE, detail(viewer2Refused));
    check('2. viewer2\'s per-user clip folder didn\'t exist and still doesn\'t', !viewer2FolderBefore && !fs.existsSync(viewer2Folder), { before: viewer2FolderBefore, after: fs.existsSync(viewer2Folder), emptyOneRemovedFirst: viewer2FolderEmptied });
    const beforeRemake = storedRecord(remake.Id);
    const remakeRefused = await call('admin', 'POST', `/Bookmarks/${remake.Id}/Clip`, { body: { Mode: 'Accurate' } });
    const afterRemake = storedRecord(remake.Id);
    check('2. a remake is 503 too, and the clip stays as it was (record and file)', problem(remakeRefused, 503) && ADMIN_LOW_SPACE.test(remakeRefused.json.detail)
        && JSON.stringify(afterRemake) === JSON.stringify(beforeRemake) && fs.existsSync(beforeRemake.Clip.FilePath), detail(remakeRefused));
    check('2. nothing was queued', !storedRecord(adminBookmark.Id).Clip && !storedRecord(viewer2Bookmark.Id).Clip, null);
    storage = await diagnostics();
    evidence.diagnosticsAboveFree = storage;
    check('2. Diagnostics: LowFreeSpace, with its warning', storage.LowFreeSpace === true && storage.FreeSpace === 'Ok' && storage.Warnings.length === 1
        && storage.Warnings[0].Code === 'LowFreeSpace' && /^C:\\ has [0-9.]+ GB free\. Clips are refused when fewer than [0-9.]+ GB would remain\.$/.test(storage.Warnings[0].Message),
    { LowFreeSpace: storage.LowFreeSpace, Warnings: storage.Warnings });
    const refusals = logEntriesAtOrAbove(serverLogSinceStart().slice(refusedLog), 'INF').filter((e) => /Refused a clip of bookmark [0-9a-f-]{36}: "?C:\\ has /.test(e.text));
    check('2. the log has each refusal at Information, with the drive and the numbers', refusals.length >= 3 && refusals.every((e) => e.level === 'INF'), refusals.map((e) => e.text.slice(0, 220)));
    if (opts.webcheck) {
        const w = settingsWebcheck('low');
        evidence.webcheckLow = w;
        check('2. the settings page shows the line and the LowFreeSpace warning under the Clip folder field (webcheck clip-guard-settings.json)', w.ok
            && w.shown && w.shown.warnings.length === 1 && w.shown.warnings[0].code === 'LowFreeSpace' && String(w.shown.reserve) === String(aboveFree), w);
        const ui = webcheck('clip-guard-refused', 'viewer2');
        evidence.webcheckRefused = ui;
        check('2. in the web client, viewer2\'s Make a clip shows the refusal as a toast in the user\'s words, and nothing is queued (webcheck clip-guard-refused.json)', ui.ok, ui);
    }

    // 3. A clip folder that doesn't exist yet.
    const fresh = path.join(workRoot, 'not-yet', 'clips');
    if (fs.existsSync(path.join(workRoot, 'not-yet'))) throw new Error(`${path.join(workRoot, 'not-yet')} exists; remove it first`);
    await setConfig({ ClipOutputPath: fresh });
    const freshAdmin = await call('admin', 'POST', `/Bookmarks/${adminBookmark.Id}/Clip`, { body: { Mode: 'Accurate' } });
    const freshViewer2 = await call('viewer2', 'POST', `/Bookmarks/${viewer2Bookmark.Id}/Clip`, { body: { Mode: 'Fast' } });
    storage = await diagnostics();
    evidence.freshRoot = { admin: detail(freshAdmin), viewer2: detail(freshViewer2), storage };
    check('3. into a clip folder that doesn\'t exist yet: 503 for admin (numbers) and viewer2 (user\'s wording), measured by the folder above, nothing created',
        problem(freshAdmin, 503) && ADMIN_LOW_SPACE.test(freshAdmin.json.detail) && problem(freshViewer2, 503) && freshViewer2.json.detail === USER_LOW_SPACE
        && !fs.existsSync(path.join(workRoot, 'not-yet')), evidence.freshRoot);
    check('3. Diagnostics: the new folder, not there yet, Ok, LowFreeSpace, "doesn\'t exist yet"', samePath(storage.Root, fresh) && storage.Exists === false
        && storage.FreeSpace === 'Ok' && storage.LowFreeSpace === true && /^The folder doesn't exist yet; the first clip creates it\. Free space for clips: C:\\ has /.test(storage.Message),
    { Root: storage.Root, Exists: storage.Exists, FreeSpace: storage.FreeSpace, Message: storage.Message });

    // 4. A drive letter that isn't there.
    const letter = 'QRSTUVWXYPONMLKJIHG'.split('').find((l) => !fs.existsSync(`${l}:\\`));
    if (!letter) throw new Error('every drive letter is in use');
    const missing = `${letter}:\\clip-guard-probe\\clips`;
    await setConfig({ ClipOutputPath: missing, MinFreeSpaceMegabytes: 2048 });
    const missingAdmin = await call('admin', 'POST', `/Bookmarks/${adminBookmark.Id}/Clip`, { body: { Mode: 'Accurate' } });
    const missingViewer2 = await call('viewer2', 'POST', `/Bookmarks/${viewer2Bookmark.Id}/Clip`, { body: { Mode: 'Fast' } });
    storage = await diagnostics();
    evidence.missingDrive = { folder: missing, admin: detail(missingAdmin), viewer2: detail(missingViewer2), storage };
    check('4. a clip folder on a drive letter that isn\'t there: 503 "isn\'t available" (admin with its path), with the reserve back at 2 GB',
        problem(missingAdmin, 503) && missingAdmin.json.detail.startsWith(`The clip folder isn't available right now: ${missing}\\admin can't be reached`)
        && problem(missingViewer2, 503) && missingViewer2.json.detail === USER_UNAVAILABLE && !fs.existsSync(`${letter}:\\`), evidence.missingDrive);
    check('4. Diagnostics: Unavailable, FolderUnavailable', storage.FreeSpace === 'Unavailable' && storage.LowFreeSpace === false && storage.Warnings.length === 1
        && storage.Warnings[0].Code === 'FolderUnavailable', { FreeSpace: storage.FreeSpace, Warnings: storage.Warnings });
    await setConfig({ ClipOutputPath: context.saved.ClipOutputPath });

    // 5. The check when a job starts. x264's slowest preset, so the long clips of 5 and 6 run for a while.
    await setConfig({ VideoPreset: 'veryslow' });
    const longA = await bookmark('admin', movie, 5, 175, 'long A');
    const aQueued = await call('admin', 'POST', `/Bookmarks/${longA.Id}/Clip`, { body: { Mode: 'Accurate' } });
    await waitFor('the long clip A to run', async () => (await clipOf('admin', longA.Id))?.Status === 'Running', 60);
    const shortB = await bookmark('admin', movie, 40, 42, 'short B');
    const bQueued = await call('admin', 'POST', `/Bookmarks/${shortB.Id}/Clip`, { body: { Mode: 'Fast' } });
    const bRecord = storedRecord(shortB.Id);
    check('5. a long Accurate clip runs, and a short Fast one waits behind it (its request passed the check, its estimate kept)', aQueued.status === 202 && bQueued.status === 202
        && bQueued.json.Clip.Status === 'Queued' && bRecord.Clip.Status === 'Queued' && bRecord.Clip.EstimatedBytes > 0, { a: aQueued.status, b: bQueued.status, bEstimate: bRecord.Clip.EstimatedBytes });
    free = freeBytes(clipRoot);
    const claimMin = Math.ceil(free / MIB) + 1024;
    const claimLog = serverLogSinceStart().length;
    await setConfig({ MinFreeSpaceMegabytes: claimMin });
    evidence.claim = { freeBytes: free, MinFreeSpaceMegabytes: claimMin, belowTwiceFree: claimMin * MIB < 2 * free };
    const aStillRunning = (await clipOf('admin', longA.Id))?.Status === 'Running';
    const bPartialsBefore = listFiles(adminFolder, /\[00\.00\.40-00\.00\.42\].*\.partial$/);
    await delay(5500); // a watchdog tick of the running clip with the new reserve, which isn't below half of it
    await call('admin', 'POST', `/Bookmarks/${longA.Id}/Clip/Cancel`);
    const aEnd = await waitForEnd('admin', longA.Id, 'the long clip A');
    const bEnd = await waitForEnd('admin', shortB.Id, 'the short clip B', 60);
    const bFiles = listFiles(adminFolder, /\[00\.00\.40-00\.00\.42\]/);
    const claimWarning = await waitForLog(claimLog, new RegExp(`\\[WRN\\].*The clip of bookmark ${dashed(shortB.Id)} was refused when it was about to start: "?C:\\\\ has `));
    evidence.claim = { ...evidence.claim, aStillRunning, a: aEnd, b: bEnd, bFiles, bPartialsBefore, warning: claimWarning && claimWarning.slice(0, 300) };
    check('5. the reserve raised above C:\'s free space but below twice it, while the long clip runs: the watchdog leaves it alone (it ends cancelled, or made, never stopped)',
        evidence.claim.belowTwiceFree && aStillRunning && ['Cancelled', 'Completed'].includes(aEnd.Status) && aEnd.Error !== ALMOST_FULL,
    { belowTwiceFree: evidence.claim.belowTwiceFree, aStillRunning, a: aEnd.Status });
    check('5. the waiting clip fails when it is about to start, with the user\'s wording, and writes nothing (no .partial, no clip file)', bEnd.Status === 'Failed'
        && bEnd.Error === USER_LOW_SPACE && bFiles.length === 0 && bPartialsBefore.length === 0, { b: bEnd, bFiles });
    check('5. the log has the refusal when it was about to start as a Warning, with the drive and the numbers', !!claimWarning, claimWarning);
    await setConfig({ MinFreeSpaceMegabytes: 2048 });

    // 6. The watchdog.
    const longC = await bookmark('admin', movie, 4, 178, 'long C');
    const cQueued = await call('admin', 'POST', `/Bookmarks/${longC.Id}/Clip`, { body: { Mode: 'Accurate' } });
    const cRunning = await waitFor('the long clip C to report progress', async () => {
        const clip = await clipOf('admin', longC.Id);
        if (clip && !['Queued', 'Running'].includes(clip.Status)) throw new Error(`the long clip C ended (${clip.Status}) before it could be checked`);
        return clip && clip.Status === 'Running' && typeof clip.Progress === 'number' && clip.Progress > 0 && clip.Progress < 80 ? clip : null; // percent
    }, 90);
    const cPartials = listFiles(adminFolder, /\.partial$/);
    free = freeBytes(clipRoot);
    const twiceFree = Math.ceil((2 * free) / MIB) + 1024;
    const watchLog = serverLogSinceStart().length;
    await setConfig({ MinFreeSpaceMegabytes: twiceFree });
    const raisedAt = Date.now();
    const cEnd = await waitForEnd('admin', longC.Id, 'the long clip C', 60);
    const stoppedAfter = Math.round((Date.now() - raisedAt) / 100) / 10;
    const cRecord = storedRecord(longC.Id);
    const partialsAfter = listFiles(adminFolder, /\.partial$/);
    const watchWarning = await waitForLog(watchLog, new RegExp(`\\[WRN\\].*Stopped the clip of bookmark ${dashed(longC.Id)}: "?C:\\\\ has ${SIZE} free, less than half of the ${SIZE} that must stay free`));
    evidence.watchdog = { queued: cQueued.status, progressWhenRaised: cRunning.Progress, partialsWhileRunning: cPartials, freeBytes: free, MinFreeSpaceMegabytes: twiceFree, end: cEnd, stoppedAfter, partialsAfter, warning: watchWarning && watchWarning.slice(0, 300) };
    check('6. the reserve raised above twice C:\'s free space mid-encode: the watchdog stops the clip, which fails "Stopped: the clip drive is almost full." within 5 s and a probe',
        cQueued.status === 202 && cPartials.length === 1 && cEnd.Status === 'Failed' && cEnd.Error === ALMOST_FULL && stoppedAfter <= 12, evidence.watchdog);
    check('6. no .partial is left, and the record has no clip file', partialsAfter.length === 0 && cRecord && cRecord.Clip && !cRecord.Clip.FilePath, { partialsAfter, clip: cRecord && cRecord.Clip });
    check('6. the log has the watchdog\'s Warning, with the drive and the numbers', !!watchWarning, watchWarning);
    await setConfig({ MinFreeSpaceMegabytes: 2048, VideoPreset: context.saved.VideoPreset });

    // 7. The run's log, and the settings page with room.
    const runLog = serverLogSinceStart().slice(logStart);
    const errors = runLog.split(/\r?\n/).filter((l) => /\[ERR\]/.test(l) && /Bookmarks/.test(l));
    const notes = logEntriesAtOrAbove(runLog, 'INF').filter((e) => e.text.includes(NOTE));
    check('7. the log of the run has no Error from Bookmarks, and no entry at Information or above naming a note', errors.length === 0 && notes.length === 0,
        { errors: errors.slice(0, 5), notes: notes.slice(0, 3).map((e) => e.text.slice(0, 200)) });
    if (opts.webcheck) {
        const w = settingsWebcheck('room');
        evidence.webcheckRoom = w;
        check('7. with the settings back, the settings page shows the line and no warning (webcheck clip-guard-settings.json)', w.ok && w.shown && w.shown.warnings.length === 0
            && String(w.shown.reserve) === '2048', w);
    }
}

async function main() {
    const context = { saved: null };
    let fatal = null;
    try {
        await run(context);
    } catch (error) {
        fatal = String(error && error.stack || error);
    } finally {
        if (context.saved) {
            await setConfig(context.saved).catch((error) => check('clean-up: the settings put back', false, String(error)));
            const back = await getConfig().catch(() => null);
            check('clean-up: MinFreeSpaceMegabytes, ClipOutputPath and VideoPreset as they were', back && back.MinFreeSpaceMegabytes === context.saved.MinFreeSpaceMegabytes
                && (back.ClipOutputPath ?? '') === context.saved.ClipOutputPath && back.VideoPreset === context.saved.VideoPreset, back && { MinFreeSpaceMegabytes: back.MinFreeSpaceMegabytes, ClipOutputPath: back.ClipOutputPath, VideoPreset: back.VideoPreset });
        }
        for (const { who, id } of created.reverse()) {
            await call(who, 'POST', `/Bookmarks/${id}/Clip/Cancel`).catch(() => null);
            await call(who, 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } }).catch(() => null);
        }

        // A bookmark a failed webcheck left (its note starts like the probe's).
        for (const who of ['admin', 'viewer2']) {
            const listed = await call(who, 'GET', '/Bookmarks').catch(() => null);
            for (const left of (listed && Array.isArray(listed.json) ? listed.json : []).filter((b) => (b.Note || '').startsWith(NOTE))) {
                await call(who, 'DELETE', `/Bookmarks/${left.Id}`, { query: { deleteClip: 'true' } }).catch(() => null);
            }
        }
        const leftover = path.join(workRoot, 'not-yet');
        if (fs.existsSync(leftover) && fs.readdirSync(leftover).length === 0) fs.rmdirSync(leftover);
    }
    const result = { pass: !fatal && checks.length > 0 && checks.every((c) => c.pass), fatal, checks, evidence };
    const text = JSON.stringify(result, null, 2);
    if (opts.out) fs.writeFileSync(opts.out, text + '\n');
    console.log(text);
    process.exit(result.pass ? 0 : 1);
}

await main();
