#!/usr/bin/env node
// The kit's library-change monitor and post-scan pattern against real library scans in a running sandbox,
// through Bookmarks' developer library change demo (/Bookmarks/Admin/LibraryChangeDemo, behind the setting
// EnableLibraryChangeDemo, which this probe turns on and restores) and the LibraryChanges entry of Bookmarks' Diagnostics.
// Node 22 built-ins only. Loopback only, never port 8096. The admin token comes from credentials.json and is never printed.
//
//   node scripts/library-changes-probe.mjs --sandbox default [--timeout <scan seconds>] [--out result.json]
//
// It adds a movie folder (with an NFO naming a genre and a studio no other item has) and a series folder of 12 episodes
// to the shared sandbox media (.sandbox/media/Movies and .sandbox/media/Shows, copies of the standard media's own files)
// and removes them again: each folder is recorded in
// .sandbox/<name>/library-changes-probe/created.json before it is made, and removed at the end, on failure, and at the
// start of the next run if a run was killed (then it scans once). Nothing else in the media is touched.
//
// Checks (a sandbox scan triggers exactly one full-pass flush with DuringPostScan=true):
//   1. the demo is off by default (403, the kit's feature-disabled type) and admin-only; Diagnostics shows the monitor
//      watching the library with the demo as its consumer;
//   2. a scan with nothing new: exactly one full-pass batch reaches the consumer during the scan, with DuringPostScan
//      and IsScanRunning true, no other batch while the scan runs, one more full pass in Diagnostics, and one more
//      "Library scan finished: full pass" line in the server log;
//   3. a scan that finds the new movie and series: the same, and the full pass lists them as added (the movie, the series,
//      its season and episodes), each with its library's folder as the top parent (what Jellyfin stores as TopParentId);
//      a movie marked a favourite while this scan runs (a change the timer path would send after 0.5 s of quiet) goes
//      out in the full pass (or, marked after it, in the batch right after the scan), never as a batch while the scan
//      runs (the deferrals counted meanwhile are evidence only); the NFO's genre and studio, which Jellyfin's own genres
//      and studios post-scan tasks create after the plugins' post-scan tasks, are not in the full pass but in one batch
//      with AfterFullPass (not a full pass, no scan running) that arrives within 3 s of the scan's end;
//   4. the timer path outside a scan: marking the movie a favourite reaches the consumer within seconds as an ordinary
//      batch (not a full pass, no scan running) with the user data change;
//   5. a scan after both folders were deleted: the same, the full pass lists the movie and the series as removed, with
//      their identities captured in the handler, the series in RemovedFolders, and neither its season nor its episodes
//      anywhere (one event for the folder); the dead genre and studio, deleted by those post-scan tasks, are
//      removed in the batch with AfterFullPass right after the scan, not in the full pass;
//   6. the server log since the start has no post-scan task error and no error from the monitor; clean-up.
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const DEMO = '/Bookmarks/Admin/LibraryChangeDemo';
const MOVIE = 'Changes Probe Movie (2026)';
const SERIES = 'Changes Probe Series (2026)';
const EPISODES = 12;
// Names no other item in the sandbox has: Jellyfin's genres and studios post-scan tasks create them from the NFO after the
// plugins' post-scan tasks, and delete them when the movie is gone.
const GENRE = 'Changes Probe Genre';
const STUDIO = 'Changes Probe Studio';
// How long after the scan's end the batch of the changes made after the full pass may arrive (the monitor looks every second).
const AFTER_FULL_PASS_SECONDS = 3;
// Serilog quotes a string argument in the log file.
const FULL_PASS_LINE = 'Library scan finished: full pass to "LibraryChangeDemo"';

function usage() {
    return 'usage: node scripts/library-changes-probe.mjs --sandbox <name> [--timeout <scan seconds>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, timeout: 600, out: null };
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
const mediaRoot = path.join(repoRoot, '.sandbox', 'media');
const workRoot = path.join(sandboxRoot, 'library-changes-probe');
const createdFile = path.join(workRoot, 'created.json');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^\uFEFF/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
if (!logDir.startsWith(sandboxRoot + path.sep)) throw new Error(`refusing to read the log folder ${logDir}: not in the sandbox`);
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}
const bookmarksGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8')) || [])[1];
if (!bookmarksGuid) throw new Error('no guid in plugins/Bookmarks/build.yaml');

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, scans: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// Jellyfin writes GUIDs without dashes ("N"); ASP.NET Core's JSON writes them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="library-changes-probe", Device="sandbox", DeviceId="library-changes-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json };
}

async function demo() {
    const r = await call('admin', 'GET', DEMO);
    if (r.status !== 200) throw new Error(`GET ${DEMO}: ${r.status} ${JSON.stringify(r.json)}`);
    return r.json;
}

async function monitorDiagnostics() {
    const r = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    if (r.status !== 200 || !r.json || !r.json.LibraryChanges) throw new Error(`GET /Bookmarks/Admin/Diagnostics: ${r.status}, no LibraryChanges`);
    const changes = r.json.LibraryChanges;
    return { changes, consumer: (changes.Consumers || []).find((c) => c.Name === 'LibraryChangeDemo') };
}

async function setDemo(enabled) {
    const p = `/Plugins/${bookmarksGuid}/Configuration`;
    const current = await call('admin', 'GET', p);
    if (current.status !== 200 || !current.json) throw new Error(`GET ${p}: ${current.status}`);
    const before = current.json.EnableLibraryChangeDemo === true;
    if (before !== enabled) {
        const posted = await call('admin', 'POST', p, { body: { ...current.json, EnableLibraryChangeDemo: enabled } });
        if (posted.status !== 204) throw new Error(`POST ${p}: ${posted.status}`);
    }
    return before;
}

// ------------------------------------------------------------------------------------------ media (undoable)

function inMedia(p) {
    const full = path.resolve(p);
    const rel = path.relative(mediaRoot, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /^c:\\programdata\\jellyfin/i.test(full)) throw new Error(`refusing to touch ${full}: not inside ${mediaRoot}`);
    const top = rel.split(path.sep);
    if (!(top[0] === 'Movies' && top[1] === MOVIE) && !(top[0] === 'Shows' && top[1] === SERIES)) throw new Error(`refusing to touch ${full}: not one of the probe's folders`);
    return full;
}

const loadCreated = () => (fs.existsSync(createdFile) ? readJson(createdFile) : []);
function saveCreated(list) {
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(createdFile, JSON.stringify(list, null, 2));
}

// Copies one of the standard media's files into a probe folder; the probe's top folder is recorded before anything is
// written, and no file is overwritten.
function copyInto(folder, fileName, source) {
    const dir = inMedia(folder);
    const src = path.resolve(source);
    if (!src.startsWith(mediaRoot + path.sep) || !fs.existsSync(src)) throw new Error(`no source file ${src}`);
    const top = inMedia(path.join(mediaRoot, path.relative(mediaRoot, dir).split(path.sep).slice(0, 2).join(path.sep)));
    const created = loadCreated();
    if (!created.includes(top)) saveCreated([...created, top]);
    fs.mkdirSync(dir, { recursive: true });
    fs.copyFileSync(src, path.join(dir, fileName), fs.constants.COPYFILE_EXCL);
}

// Writes a small text file (the movie's NFO) into a probe folder, recorded like copyInto; never overwrites.
function writeInto(folder, fileName, text) {
    const dir = inMedia(folder);
    const top = inMedia(path.join(mediaRoot, path.relative(mediaRoot, dir).split(path.sep).slice(0, 2).join(path.sep)));
    const created = loadCreated();
    if (!created.includes(top)) saveCreated([...created, top]);
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, fileName), text, { encoding: 'utf8', flag: 'wx' });
}

// Removes the probe's folders (only those recorded, only the probe's names inside the media folder).
function removeCreated() {
    const created = loadCreated();
    for (const folder of created) {
        const full = inMedia(folder);
        if (fs.existsSync(full)) fs.rmSync(full, { recursive: true });
    }
    if (fs.existsSync(createdFile)) fs.rmSync(createdFile);
    return created.length;
}

// ------------------------------------------------------------------------------------------ library

// Scans the libraries (POST /Library/Refresh) and waits for the RefreshLibrary task; `whileRunning` runs once, as soon
// as the task is seen running.
async function scan(label, whileRunning) {
    const started = Date.now();
    const r = await call('admin', 'POST', '/Library/Refresh');
    if (r.status !== 204) throw new Error(`POST /Library/Refresh: ${r.status}`);
    let ranHook = !whileRunning;
    for (;;) {
        const tasks = await call('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } });
        const task = (tasks.json || []).find((t) => t.Key === 'RefreshLibrary');
        const last = task && task.LastExecutionResult;
        if (!ranHook && task && task.State === 'Running') {
            ranHook = true;
            await whileRunning();
        }
        if (Date.now() - started >= 1500 && task && task.State === 'Idle' && last && Date.parse(last.EndTimeUtc) >= started - 1000) {
            const seconds = Math.round((Date.now() - started) / 100) / 10;
            evidence.scans.push({ label, seconds, status: last.Status, endUtc: last.EndTimeUtc });
            if (last.Status !== 'Completed') throw new Error(`the scan "${label}" ended ${last.Status}`);
            return last.EndTimeUtc;
        }
        if (Date.now() - started > opts.timeout * 1000) throw new Error(`the scan "${label}" did not finish within ${opts.timeout} s`);
        await delay(ranHook ? 500 : 50);
    }
}

// The genre or studio of that name, or null (GET /Genres, /Studios).
async function findByName(route, name) {
    const r = await call('admin', 'GET', route, { query: { userId: users.admin.id, searchTerm: name, EnableImages: 'false', EnableUserData: 'false' } });
    if (r.status !== 200) throw new Error(`GET ${route}: ${r.status}`);
    return (r.json.Items || []).find((i) => i.Name === name) || null;
}

async function findItems(types, folder) {
    const r = await call('admin', 'GET', '/Items', {
        query: { userId: users.admin.id, Recursive: 'true', IncludeItemTypes: types, Fields: 'Path,ParentId', EnableImages: 'false', EnableUserData: 'false' }
    });
    if (r.status !== 200) throw new Error(`GET /Items: ${r.status}`);
    return r.json.Items.filter((i) => typeof i.Path === 'string' && i.Path.split(/[\\/]/).includes(folder));
}

// The server log's lines from a position on (the newest log file, and its successor if it rolled over).
function logPosition() {
    const files = fs.readdirSync(logDir).filter((f) => /^log_\d{8}\.log$/.test(f)).sort();
    const file = files[files.length - 1];
    return { file, size: file ? fs.statSync(path.join(logDir, file)).size : 0 };
}

function logSince(position) {
    const files = fs.readdirSync(logDir).filter((f) => /^log_\d{8}\.log$/.test(f)).sort().filter((f) => !position.file || f >= position.file);
    return files.map((f) => {
        const text = fs.readFileSync(path.join(logDir, f), 'utf8');
        return f === position.file ? text.slice(position.size) : text;
    }).join('');
}

const count = (text, needle) => text.split('\n').filter((l) => l.includes(needle)).length;
const ids = (list) => (list || []).map((c) => guid(c.ItemId));

// One scan: the batches the demo received meanwhile and right after it, the Diagnostics counters and the log lines. After
// the scan it waits (at most 10 s) until the monitor has seen the scan end and nothing marked as after the full pass waits.
async function scanAndCollect(label, whileRunning) {
    const before = await demo();
    const beforeDiag = await monitorDiagnostics();
    const log = logPosition();
    const endUtc = await scan(label, whileRunning);
    const settleStarted = Date.now();
    let afterDiag = await monitorDiagnostics();
    while ((afterDiag.changes.AfterFullPass !== false || afterDiag.consumer.Pending.AfterFullPass !== false) && Date.now() - settleStarted < 10000) {
        await delay(250);
        afterDiag = await monitorDiagnostics();
    }
    await delay(500);
    const after = await demo();
    afterDiag = await monitorDiagnostics();
    const batches = after.Batches.filter((b) => b.Seq > before.Received);
    const fullPasses = batches.filter((b) => b.FullPass);
    const afterFullPass = batches.filter((b) => b.AfterFullPass);
    const logText = logSince(log);
    const result = {
        batches,
        endUtc,
        full: fullPasses[0],
        fullPasses: fullPasses.length,
        afterFullPass,
        settled: afterDiag.changes.AfterFullPass === false && afterDiag.consumer.Pending.AfterFullPass === false,
        duringScan: batches.filter((b) => b.ScanRunning).length,
        timerDuringScan: batches.filter((b) => b.ScanRunning && !b.FullPass).length,
        diagFullPasses: afterDiag.consumer.FullPasses - beforeDiag.consumer.FullPasses,
        deferrals: afterDiag.consumer.Deferrals - beforeDiag.consumer.Deferrals,
        logLines: count(logText, FULL_PASS_LINE),
    };
    const secondsAfterEnd = (b) => Math.round((Date.parse(b.ReceivedUtc) - Date.parse(endUtc)) / 100) / 10;
    evidence.scans[evidence.scans.length - 1].batches = batches.map((b) => ({
        Seq: b.Seq, FullPass: b.FullPass, DuringPostScan: b.DuringPostScan, AfterFullPass: b.AfterFullPass, ScanRunning: b.ScanRunning,
        secondsAfterScanEnd: secondsAfterEnd(b), Counts: b.Counts, Kinds: b.Kinds
    }));
    evidence.scans[evidence.scans.length - 1].deferrals = result.deferrals;
    result.secondsAfterEnd = secondsAfterEnd;
    return result;
}

// Jellyfin's own post-scan tasks run after the plugins' (ApplicationHost.cs:822-826): what they did comes in one batch
// with AfterFullPass right after the scan, not in the full pass.
function checkAfterFullPass(label, r, list, expected) {
    const inFull = r.full ? [...ids(r.full[list]), ...ids(r.full.Updated)] : [];
    const tail = r.afterFullPass.find((b) => expected.every((e) => ids(b[list]).includes(guid(e.Id))));
    const seconds = tail ? r.secondsAfterEnd(tail) : null;
    return check(label,
        r.settled && r.afterFullPass.length === 1 && tail && tail.FullPass === false && tail.DuringPostScan === false && tail.ScanRunning === false
            && expected.every((e) => !inFull.includes(guid(e.Id))) && seconds !== null && seconds <= AFTER_FULL_PASS_SECONDS,
        { settled: r.settled, afterFullPassBatches: r.afterFullPass.length, inFullPass: expected.filter((e) => inFull.includes(guid(e.Id))).map((e) => e.Type),
          found: tail ? tail[list].filter((c) => expected.some((e) => guid(e.Id) === guid(c.ItemId))).map((c) => c.Kind) : [],
          secondsAfterScanEnd: seconds, ScanRunning: tail && tail.ScanRunning, counts: tail && tail.Counts, kinds: tail && tail.Kinds });
}

function checkOneFullPass(label, r) {
    return check(`${label}: exactly one full-pass flush, dispatched during the scan with DuringPostScan and IsScanRunning true, and no other batch while the scan ran`,
        r.fullPasses === 1 && r.full.DuringPostScan === true && r.full.ScanRunning === true && r.timerDuringScan === 0 && r.duringScan === 1
            && r.diagFullPasses === 1 && r.logLines === 1,
        { fullPasses: r.fullPasses, DuringPostScan: r.full && r.full.DuringPostScan, ScanRunning: r.full && r.full.ScanRunning, batchesDuringScan: r.duringScan,
          timerBatchesDuringScan: r.timerDuringScan, diagnosticsFullPasses: r.diagFullPasses, logLines: r.logLines, deferralsMeanwhile: r.deferrals,
          counts: r.full && r.full.Counts });
}

// ------------------------------------------------------------------------------------------ the run

async function run(context) {
    const off = await call('admin', 'GET', DEMO);
    const wasOn = off.status === 200;
    context.demoWasOn = wasOn ? true : await setDemo(true);
    if (!wasOn) {
        check('the demo is off by default: 403 with the kit\'s feature-disabled type', off.status === 403 && off.json && off.json.type === 'urn:jellyfin-plugin-kit:feature-disabled',
            { status: off.status, type: off.json && off.json.type });
    }
    const viewer = await call('viewer', 'GET', DEMO);
    check('viewer: the demo is admin-only (403)', viewer.status === 403, { status: viewer.status });
    const cleared = await call('admin', 'DELETE', DEMO);
    check('DELETE clears the demo\'s batches (204)', cleared.status === 204, { status: cleared.status });

    const diag = await monitorDiagnostics();
    check('Diagnostics: the monitor watches the library, with the demo as its consumer (all kinds, user data, 0.5 s quiet, 5 s maximum wait)',
        diag.changes.Watching === true && diag.changes.UserData === true && diag.consumer && diag.consumer.Kinds == null && diag.consumer.UserData === true
            && diag.consumer.QuietSeconds === 0.5 && diag.consumer.MaxWaitSeconds === 5,
        { Watching: diag.changes.Watching, UserData: diag.changes.UserData, consumers: (diag.changes.Consumers || []).map((c) => c.Name), consumer: diag.consumer && { Kinds: diag.consumer.Kinds, QuietSeconds: diag.consumer.QuietSeconds, MaxWaitSeconds: diag.consumer.MaxWaitSeconds } });

    // The libraries' folders, the top parents.
    const folders = await call('admin', 'GET', '/Library/VirtualFolders');
    const library = (name) => guid(((folders.json || []).find((f) => f.Name === name) || {}).ItemId);
    const moviesLibrary = library('Movies');
    const showsLibrary = library('Shows');
    check('the sandbox has its Movies and Shows libraries', moviesLibrary && showsLibrary, { moviesLibrary, showsLibrary });

    // ---- 2. a scan with nothing new
    const quiet = await scanAndCollect('nothing new');
    checkOneFullPass('a scan with nothing new', quiet);

    // ---- 3. a scan that finds a new movie and a new series, with a favourite marked while it runs
    const testPattern = (await findItems('Movie', 'Test Pattern (2020)'))[0];
    if (!testPattern) throw new Error('no Test Pattern (2020) movie; run Initialize-Sandbox.ps1 on fresh media');
    if (await findByName('/Genres', GENRE) || await findByName('/Studios', STUDIO)) throw new Error(`the sandbox already has a genre "${GENRE}" or a studio "${STUDIO}"`);
    copyInto(path.join(mediaRoot, 'Movies', MOVIE), `${MOVIE}.mkv`, path.join(mediaRoot, 'Movies', 'Test Pattern (2020)', 'Test Pattern (2020).mkv'));
    writeInto(path.join(mediaRoot, 'Movies', MOVIE), `${MOVIE}.nfo`,
        `<?xml version="1.0" encoding="utf-8" standalone="yes"?>\n<movie>\n  <title>Changes Probe Movie</title>\n  <year>2026</year>\n  <genre>${GENRE}</genre>\n  <studio>${STUDIO}</studio>\n</movie>\n`);
    for (let e = 1; e <= EPISODES; e++) {
        const number = String(e).padStart(2, '0');
        copyInto(path.join(mediaRoot, 'Shows', SERIES, 'Season 01'), `${SERIES} S01E${number}.mkv`,
            path.join(mediaRoot, 'Shows', 'Test Series (2020) [tvdbid-900401]', 'Season 01', `Test Series (2020) S01E0${e % 2 === 1 ? 1 : 2}.mkv`));
    }
    let favouredUtc = null;
    const added = await scanAndCollect('a new movie and series', async () => {
        const r = await call('admin', 'POST', `/UserFavoriteItems/${testPattern.Id}`);
        if (r.status !== 200) throw new Error(`POST /UserFavoriteItems: ${r.status}`);
        context.favourites.push(testPattern.Id);
        favouredUtc = Date.now();
    });
    checkOneFullPass('a scan that finds a new movie and series', added);
    const movie = (await findItems('Movie', MOVIE))[0];
    const series = (await findItems('Series', SERIES))[0];
    const season = (await findItems('Season', SERIES))[0];
    const episodes = await findItems('Episode', SERIES);
    if (!movie || !series || !season || episodes.length !== EPISODES) {
        throw new Error(`the scan didn't add the probe's items: ${JSON.stringify({ movie: !!movie, series: !!series, season: !!season, episodes: episodes.length })}`);
    }

    // The top parent is what Jellyfin stores as an item's TopParentId: the library path's folder under the root (a
    // library's PhysicalFolderIds), not the library's own id.
    const fullAdded = added.full ? added.full.Added : [];
    const topOf = (id) => guid((fullAdded.find((c) => guid(c.ItemId) === guid(id)) || {}).TopParentId);
    const folderPath = async (id) => {
        const r = id ? await call('admin', 'GET', `/Items/${id}`, { query: { userId: users.admin.id } }) : { status: 0 };
        return r.status === 200 ? { Type: r.json.Type, Path: r.json.Path } : { status: r.status };
    };
    const movieTop = await folderPath(topOf(movie.Id));
    const showsTop = await folderPath(topOf(series.Id));
    const moviesLocation = (((folders.json || []).find((f) => f.Name === 'Movies') || {}).Locations || [])[0];
    const showsLocation = (((folders.json || []).find((f) => f.Name === 'Shows') || {}).Locations || [])[0];
    check('the full pass lists the new movie, series, season and every episode as added, each with its library\'s folder as the top parent',
        [movie, series, season, ...episodes].every((i) => ids(fullAdded).includes(guid(i.Id)))
            && movieTop.Path === moviesLocation && showsTop.Path === showsLocation
            && [season, ...episodes].every((i) => topOf(i.Id) === topOf(series.Id)) && topOf(movie.Id) !== topOf(series.Id),
        { added: added.full && added.full.Counts, kinds: added.full && added.full.Kinds, movieTop, showsTop, moviesLibrary, showsLibrary });

    const favouredIn = (b) => b && (b.UserData || []).find((c) => guid(c.ItemId) === guid(testPattern.Id));
    const favoured = favouredIn(added.full) || added.afterFullPass.map(favouredIn).find(Boolean);
    const outlasted = favouredUtc !== null && added.full ? Date.parse(added.full.ReceivedUtc) - favouredUtc : null;
    // Whether the timer path found the batch due before the scan ended (a deferral) depends on the scan: the item events
    // keep the demo's 0.5 s quiet period from running out, and a sandbox scan lasts well under the 5 s maximum wait. The
    // deferral itself is the unit tests' (fake clock); here it is evidence only. A favourite marked after the full pass
    // went out, while Jellyfin's own post-scan tasks ran, comes in the batch right after the scan.
    check('a favourite marked while the scan ran went out in the full pass (or, marked after it, in the batch right after the scan), never as a batch while the scan ran',
        favouredUtc !== null && favoured && favoured.Reason === 'UpdateUserRating' && added.timerDuringScan === 0,
        { markedWhileRunning: favouredUtc !== null, inFullPass: !!favouredIn(added.full), inBatchAfterFullPass: !favouredIn(added.full) && !!favoured,
          millisecondsFromFavouriteToFullPass: outlasted, deferrals: added.deferrals });

    // Jellyfin's genres and studios post-scan tasks run after Bookmarks' (the plugins' come first): the NFO's genre and
    // studio are created after the full pass and reach the consumer in one batch right after the scan.
    const genre = await findByName('/Genres', GENRE);
    const studio = await findByName('/Studios', STUDIO);
    if (!genre || !studio) throw new Error(`the scan didn't create the NFO's genre and studio: ${JSON.stringify({ genre: !!genre, studio: !!studio })}`);
    checkAfterFullPass(`Jellyfin's own post-scan tasks run after the full pass: the NFO's new genre and studio are added in one batch with AfterFullPass (no full pass, no scan running) within ${AFTER_FULL_PASS_SECONDS} s of the scan's end, not in the full pass`,
        added, 'Added', [genre, studio]);

    // ---- 4. the timer path, outside a scan
    const beforeFavourite = (await demo()).Received;
    const favourite = await call('admin', 'POST', `/UserFavoriteItems/${movie.Id}`);
    if (favourite.status !== 200) throw new Error(`POST /UserFavoriteItems: ${favourite.status}`);
    context.favourites.push(movie.Id);
    const started = Date.now();
    let timerBatch = null;
    while (!timerBatch && Date.now() - started < 15000) {
        await delay(250);
        timerBatch = (await demo()).Batches.filter((b) => b.Seq > beforeFavourite)
            .find((b) => (b.UserData || []).some((c) => guid(c.ItemId) === guid(movie.Id)));
    }
    const change = timerBatch && timerBatch.UserData.find((c) => guid(c.ItemId) === guid(movie.Id));
    check('the timer path: a favourite reaches the consumer within seconds as an ordinary batch (no full pass, no scan running) with the user data change',
        timerBatch && timerBatch.FullPass === false && timerBatch.DuringPostScan === false && timerBatch.ScanRunning === false
            && guid(change.UserId) === guid(users.admin.id) && change.Reason === 'UpdateUserRating',
        timerBatch ? { seconds: Math.round((Date.parse(timerBatch.ReceivedUtc) - started) / 100) / 10, FullPass: timerBatch.FullPass, ScanRunning: timerBatch.ScanRunning,
            Reason: change && change.Reason, sameUser: change && guid(change.UserId) === guid(users.admin.id) } : null);
    for (const id of context.favourites.splice(0)) await call('admin', 'DELETE', `/UserFavoriteItems/${id}`);

    // ---- 5. a scan after the folders were deleted
    removeCreated();
    const removed = await scanAndCollect('the movie and series deleted');
    checkOneFullPass('a scan after the movie and series folders were deleted', removed);
    const fullRemoved = removed.full ? removed.full.Removed : [];
    const removedMovie = fullRemoved.find((c) => guid(c.ItemId) === guid(movie.Id));
    const removedSeries = fullRemoved.find((c) => guid(c.ItemId) === guid(series.Id));
    check('the full pass lists the movie and the series as removed, with identities captured in the handler, the series in RemovedFolders, its season and episodes nowhere',
        removedMovie && removedSeries && removedMovie.Identity && removedMovie.Identity.Kind === 'Movie' && removedMovie.Identity.HasName && removedMovie.Identity.HasPath
            && removedSeries.IsFolder === true && removedSeries.Identity && removedSeries.Identity.Kind === 'Series'
            && ids(removed.full.RemovedFolders).includes(guid(series.Id)) && !ids(removed.full.RemovedFolders).includes(guid(movie.Id))
            && ![season, ...episodes].some((i) => ids(fullRemoved).includes(guid(i.Id)) || ids(removed.full.RemovedFolders).includes(guid(i.Id))),
        { removed: fullRemoved.map((c) => ({ Kind: c.Kind, IsFolder: c.IsFolder, Identity: c.Identity })), removedFolders: (removed.full ? removed.full.RemovedFolders : []).map((c) => c.Kind) });
    checkAfterFullPass(`the dead genre and studio, deleted by Jellyfin's own post-scan tasks after the full pass, are removed in one batch with AfterFullPass within ${AFTER_FULL_PASS_SECONDS} s of the scan's end, not in the full pass`,
        removed, 'Removed', [genre, studio]);
    const gone = !(await findByName('/Genres', GENRE)) && !(await findByName('/Studios', STUDIO));
    check('the genre and studio are gone from the sandbox again', gone, { gone });
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { demoWasOn: null, favourites: [] };
    const log = logPosition();
    if (loadCreated().length > 0) {
        evidence.undidLeftovers = removeCreated();
        await scan('left-over probe media of an earlier run removed');
    }
    let failure = null;
    try {
        await run(context);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        try {
            for (const id of context.favourites.splice(0)) await call('admin', 'DELETE', `/UserFavoriteItems/${id}`);
            if (removeCreated() > 0) await scan('clean-up: probe media removed');
        } catch (error) {
            check('clean-up: the probe media removed', false, String(error));
        }
        if (context.demoWasOn === false) await setDemo(false).catch((error) => check('clean-up: the demo setting restored', false, String(error)));
        const text = logSince(log);
        const errors = text.split('\n').filter((l) => /\[ERR\]/.test(l) && /LibraryChanges|PostScan|post-scan/i.test(l)).concat(
            text.split('\n').filter((l) => /Error running post-scan task|Error in Item(Added|Updated|Removed) event handler/.test(l)));
        check('the server log since the start: no post-scan task error, no event handler error, no error from the monitor', errors.length === 0, errors.slice(0, 5));
        check('clean-up: no probe media left, the demo setting as it was', loadCreated().length === 0
            && !fs.existsSync(path.join(mediaRoot, 'Movies', MOVIE)) && !fs.existsSync(path.join(mediaRoot, 'Shows', SERIES))
            && (context.demoWasOn !== false || (await call('admin', 'GET', DEMO)).status === 403), { demoWasOn: context.demoWasOn });
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
