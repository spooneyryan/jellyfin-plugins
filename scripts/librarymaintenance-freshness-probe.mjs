#!/usr/bin/env node
// Library Maintenance's index freshness against a running sandbox: GET /LibraryMaintenance/Duplicates/Index,
// POST …/Index/Rebuild, the index's and the kit's library-change monitor's Diagnostics entries and the server log, with
// Jellyfin's own scans and refreshes as the triggers. Node 22 built-ins only. Loopback only, never port 8096. The admin
// token comes from credentials.json and is never printed.
//
//   node scripts/librarymaintenance-freshness-probe.mjs --sandbox default [--phases add,scan] [--library Movies] [--timeout <scan seconds>] [--out result.json]
//   node scripts/librarymaintenance-freshness-probe.mjs --sandbox scale --cold [--timeout <scan seconds>] [--out result.json]
//
// Phases:
//   add  (a) a movie file added: it copies one of the library's own movie files into a new folder "Freshness Probe Movie (2026)"
//        in the library's first folder (inside .sandbox/media only; recorded in .sandbox/<name>/freshness-probe/created.json
//        before it is made, removed at the end, on failure, and at the start of the next run), and has Jellyfin notice it
//        with a refresh of the library (POST /Items/{library}/Refresh, "Scan for new and updated files": the realtime
//        monitor's own path, a ValidateChildren of the library's folders, not a library scan; the sandbox's libraries
//        have realtime monitoring off). Checks: the change waits in Duplicates' queue of the kit's monitor, no build runs
//        until it has been quiet for 60 s, then one batch and one build ("after library changes") with the movie in the
//        index; the same again when the folder is removed, and the movie is gone.
//   scan (b) a full library scan (POST /Library/Refresh), with a Rebuild sent while it runs: the Rebuild is put off
//        (Dirty, not Rebuilding, ScanInProgress), the index goes Dirty → Rebuilding → a higher Version once the scan has
//        read the libraries, the log has exactly one post-scan build ("after a library scan"), which isn't
//        BuiltDuringScan, and no other build follows when the scan ends (the post-scan build covered the Rebuild). A scan
//        too short to send the Rebuild during it (the default sandbox's take well under a second) is reported, and the
//        rest is checked.
//   --cold (c) a cold start during a running scan, on a server just started (Version 0, no build yet; restart the
//        sandbox first): a scan, a Rebuild while it runs builds the index once with BuiltDuringScan = true ("during a
//        library scan"), a second Rebuild during the scan is put off, and the post-scan build follows once.
// Every phase also checks that the log has no Library Maintenance error. It prints one JSON result (pass, checks,
// evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const INDEX = '/LibraryMaintenance/Duplicates/Index';
const REBUILD = '/LibraryMaintenance/Duplicates/Index/Rebuild';
const DIAGNOSTICS = '/LibraryMaintenance/Admin/Diagnostics';
const CONSUMER = 'LibraryMaintenance.Duplicates';
const MOVIE = 'Freshness Probe Movie (2026)';
// The kit's defaults: 60 s of quiet; the index looks for the scan's end every 5 s.
const QUIET_SECONDS = 60;
const SCAN_POLL_SECONDS = 5;
const BUILD_LINE = /Library Maintenance indexed (\d+) movies in (\d+) entries \((\d+) with a broken merge\) from (\d+) libraries in (\d+) ms \(([^)]*)\)/;
const REASON = {
    rebuild: 'a rebuild was asked for',
    changes: 'after library changes',
    scan: 'after a library scan',
    duringScan: ', during a library scan'
};

function usage() {
    return 'usage: node scripts/librarymaintenance-freshness-probe.mjs --sandbox <name> [--phases add,scan] [--library <name>] [--cold] [--timeout <scan seconds>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, phases: ['add', 'scan'], library: 'Movies', cold: false, timeout: 900, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--phases') o.phases = String(argv[++i] || '').split(',').filter(Boolean);
        else if (a === '--library') o.library = argv[++i];
        else if (a === '--cold') o.cold = true;
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!o.phases.length || o.phases.some((p) => p !== 'add' && p !== 'scan')) throw new Error(usage());
    if (!o.library || typeof o.library !== 'string') throw new Error(usage());
    if (!Number.isFinite(o.timeout) || o.timeout <= 0) throw new Error(usage());
    if (o.cold) o.phases = [];
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const mediaRoot = path.join(repoRoot, '.sandbox', 'media');
const workRoot = path.join(sandboxRoot, 'freshness-probe');
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
if (!users.admin || !users.admin.token || !users.admin.id) throw new Error('no admin in credentials.json; run Initialize-Sandbox.ps1');

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, phases: opts.cold ? ['cold'] : opts.phases, scans: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

const sum = (o) => Object.values(o || {}).reduce((a, v) => a + (typeof v === 'number' ? v : 0), 0);
const diff = (after, before) => Object.fromEntries(Object.keys({ ...after, ...before }).map((k) => [k, (after?.[k] || 0) - (before?.[k] || 0)]));

async function call(who, method, p, { query, body } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="librarymaintenance-freshness-probe", Device="sandbox", DeviceId="librarymaintenance-freshness-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    if (body !== undefined) h['Content-Type'] = 'application/json';
    const res = await fetch(url, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text };
}

async function admin(method, p, options) {
    const r = await call('admin', method, p, options);
    if (r.status < 200 || r.status >= 300) throw new Error(`${method} ${p}: ${r.status} ${r.text.slice(0, 300)}`);
    return r.json;
}

const brief = (i) => i && { Version: i.Version, Dirty: i.Dirty, Rebuilding: i.Rebuilding, ScanInProgress: i.ScanInProgress, BuiltDuringScan: i.BuiltDuringScan, Movies: i.Movies };

// The index (GET /Index, which counts as a use of the page), its Diagnostics entry, and Duplicates' queue in the monitor's.
async function sample() {
    const index = await admin('GET', INDEX);
    const diagnostics = await admin('GET', DIAGNOSTICS);
    const changes = diagnostics.LibraryChanges || {};
    const consumer = (changes.Consumers || []).find((c) => c.Name === CONSUMER) || null;
    return { t: Date.now(), index, duplicates: diagnostics.DuplicatesIndex || {}, changes, consumer };
}

const pendingCount = (consumer) => {
    const p = (consumer && consumer.Pending) || {};
    return (p.Added || 0) + (p.Updated || 0) + (p.Removed || 0) + (p.RemovedFolders || 0) + (p.UserData || 0);
};

// GET /Index every `ms` until stopped, keeping each state that differs from the one before (Dirty, Rebuilding, Version).
function watchIndex(ms) {
    const states = [];
    let stopped = false;
    const done = (async () => {
        while (!stopped) {
            const r = await call('admin', 'GET', INDEX);
            if (r.status === 200 && r.json) {
                const s = brief(r.json);
                const last = states[states.length - 1];
                if (!last || last.Version !== s.Version || last.Dirty !== s.Dirty || last.Rebuilding !== s.Rebuilding || last.ScanInProgress !== s.ScanInProgress) {
                    states.push({ ms: Date.now(), ...s });
                }
            }
            await delay(ms);
        }
    })();
    return { states, stop: async () => { stopped = true; await done; return states; } };
}

async function waitFor(what, predicate, seconds, ms = 250) {
    const deadline = Date.now() + seconds * 1000;
    for (;;) {
        const s = await sample();
        if (predicate(s)) return s;
        if (Date.now() > deadline) throw new Error(`${what} didn't happen within ${seconds} s (index ${JSON.stringify(brief(s.index))})`);
        await delay(ms);
    }
}

// ------------------------------------------------------------------ the log

function logLines() {
    const files = fs.readdirSync(logDir).filter((f) => /^log_\d+\.log$/.test(f)).sort();
    return files.flatMap((f) => fs.readFileSync(path.join(logDir, f), 'utf8').split(/\r?\n/));
}

// The lines written since `ms` (a line without a time, an exception's, takes the time of the line before it).
function logSince(ms) {
    const out = [];
    let t = 0;
    for (const line of logLines()) {
        const m = /^\[(\d{4}-\d\d-\d\d) (\d\d:\d\d:\d\d\.\d+) ([+-]\d\d:\d\d)\]/.exec(line);
        if (m) t = Date.parse(`${m[1]}T${m[2]}${m[3]}`);
        if (t >= ms) out.push({ t, line });
    }
    return out;
}

function buildsSince(ms) {
    return logSince(ms).map(({ t, line }) => {
        const m = BUILD_LINE.exec(line);
        return m && { t, movies: Number(m[1]), entries: Number(m[2]), ms: Number(m[5]), reason: m[6] };
    }).filter(Boolean);
}

const errorsSince = (ms) => logSince(ms).filter(({ line }) => /\[(ERR|FTL)\]/.test(line) && /LibraryMaintenance|Library Maintenance/.test(line)).map(({ line }) => line.slice(0, 300));

// ------------------------------------------------------------------ the media (undoable)

function inMedia(p) {
    const full = path.resolve(p);
    const rel = path.relative(mediaRoot, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /^c:\\programdata\\jellyfin/i.test(full) || /^z:/i.test(full)) throw new Error(`refusing to touch ${full}: not inside ${mediaRoot}`);
    return full;
}

function probeFolder(p) {
    const full = inMedia(p);
    if (path.basename(full) !== MOVIE) throw new Error(`refusing to touch ${full}: not the probe's folder`);
    return full;
}

const loadCreated = () => (fs.existsSync(createdFile) ? readJson(createdFile) : []);

// Copies one of the library's own movie files into the probe's folder; the folder is recorded before it is made, and no
// file is overwritten.
function addMovie(location) {
    const folder = probeFolder(path.join(inMedia(location), MOVIE));
    const sources = fs.readdirSync(location, { withFileTypes: true })
        .filter((d) => d.isDirectory() && d.name !== MOVIE)
        .flatMap((d) => fs.readdirSync(path.join(location, d.name)).filter((f) => /\.(mkv|mp4)$/i.test(f) && !/ - /.test(f)).map((f) => path.join(location, d.name, f)))
        .map((f) => ({ f, size: fs.statSync(f).size }))
        .sort((a, b) => a.size - b.size);
    if (!sources.length) throw new Error(`no movie file in ${location}`);
    const created = loadCreated();
    if (!created.includes(folder)) {
        fs.mkdirSync(workRoot, { recursive: true });
        fs.writeFileSync(createdFile, JSON.stringify([...created, folder], null, 2));
    }
    fs.mkdirSync(folder, { recursive: true });
    const file = path.join(folder, MOVIE + path.extname(sources[0].f));
    fs.copyFileSync(sources[0].f, file, fs.constants.COPYFILE_EXCL);
    return { folder, file: path.relative(mediaRoot, file), from: path.relative(mediaRoot, sources[0].f), bytes: sources[0].size };
}

// Removes the probe's folder (only those recorded, only the probe's name inside the media folder).
function removeCreated() {
    const created = loadCreated();
    for (const folder of created) {
        const full = probeFolder(folder);
        if (fs.existsSync(full)) fs.rmSync(full, { recursive: true });
    }
    if (fs.existsSync(createdFile)) fs.rmSync(createdFile);
    return created.length;
}

// ------------------------------------------------------------------ the library

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
            evidence.scans.push({ label, seconds, status: last.Status, endUtc: last.EndTimeUtc, ranHook });
            if (last.Status !== 'Completed') throw new Error(`the scan "${label}" ended ${last.Status}`);
            return { endMs: Date.parse(last.EndTimeUtc), ranHook };
        }
        if (Date.now() - started > opts.timeout * 1000) throw new Error(`the scan "${label}" did not finish within ${opts.timeout} s`);
        await delay(ranHook ? 500 : 50);
    }
}

// "Scan for new and updated files" on one library: Jellyfin validates its folders' children (no library scan).
async function refreshLibrary(library) {
    const r = await call('admin', 'POST', `/Items/${library.ItemId}/Refresh`, {
        query: { Recursive: true, MetadataRefreshMode: 'Default', ImageRefreshMode: 'Default', ReplaceAllMetadata: false, ReplaceAllImages: false }
    });
    if (r.status !== 204) throw new Error(`POST /Items/{library}/Refresh: ${r.status}`);
}

async function rebuildAndWait() {
    const before = await admin('GET', INDEX);
    const post = await call('admin', 'POST', REBUILD);
    if (post.status !== 202) throw new Error(`POST ${REBUILD}: ${post.status}`);
    return waitFor('the rebuild', (s) => s.index.Version > before.Version && !s.index.Rebuilding, 300);
}

// Waits until nothing waits in Duplicates' queue and no build runs, then for the index to stay put for 2 s.
async function settle() {
    await waitFor('a quiet index', (s) => pendingCount(s.consumer) === 0 && !s.index.Rebuilding && !s.index.ScanInProgress, QUIET_SECONDS * 3 + 30, 500);
    const a = await sample();
    await delay(2000);
    const b = await sample();
    if (b.index.Version !== a.index.Version || pendingCount(b.consumer) > 0) return settle();
    return b;
}

// ------------------------------------------------------------------ (a) a movie file added, then removed

// One change, noticed by a refresh of the library: it waits out the quiet period in the monitor, then one batch, one build.
async function observeChange(label, library, change) {
    const before = await settle();
    const t0 = Date.now();
    const detail = change();
    await refreshLibrary(library);
    const watch = { states: [], firstPending: null, lastPendingChange: null, batch: null, buildBeforeBatch: null, scanSeen: false };
    let previous = { t: t0, pending: pendingCount(before.consumer) };
    const deadline = t0 + (QUIET_SECONDS + 240) * 1000;
    let s;
    for (;;) {
        s = await sample();
        const pending = pendingCount(s.consumer);
        const st = brief(s.index);
        const last = watch.states[watch.states.length - 1];
        if (!last || last.Version !== st.Version || last.Dirty !== st.Dirty || last.Rebuilding !== st.Rebuilding || last.pending !== pending) {
            watch.states.push({ ms: s.t - t0, ...st, pending });
        }
        if (st.ScanInProgress) watch.scanSeen = true;
        if (pending !== previous.pending && pending > 0 && !watch.batch) {
            // A change arrived between the sample before this one and this one (the batch empties the queue later).
            watch.lastPendingChange = { afterMs: previous.t, byMs: s.t };
            if (!watch.firstPending) watch.firstPending = { ms: s.t, sinceUtc: s.consumer.Pending.SinceUtc, pending: { ...s.consumer.Pending } };
        }
        if (!watch.batch && s.consumer && s.consumer.Batches > before.consumer.Batches) {
            watch.batch = { utcMs: Date.parse(s.consumer.LastBatchUtc), recent: (s.consumer.Recent || [])[0] };
        }
        if (!watch.batch && s.index.Version > before.index.Version) watch.buildBeforeBatch = brief(s.index);
        if (watch.batch && s.index.Version > before.index.Version && !s.index.Rebuilding) break;
        if (Date.now() > deadline) break;
        previous = { t: s.t, pending };
        await delay(250);
    }

    const builds = buildsSince(t0);
    const sinceMs = watch.firstPending && Date.parse(watch.firstPending.sinceUtc);
    const quietAfterFirst = watch.batch && sinceMs ? (watch.batch.utcMs - sinceMs) / 1000 : null;
    const quietAfterLast = watch.batch && watch.lastPendingChange ? { atLeast: (watch.batch.utcMs - watch.lastPendingChange.byMs) / 1000, atMost: (watch.batch.utcMs - watch.lastPendingChange.afterMs) / 1000 } : null;
    const result = {
        label,
        change: detail,
        before: brief(before.index),
        after: brief(s.index),
        states: watch.states,
        firstPending: watch.firstPending,
        batch: watch.batch && { ...watch.batch, utc: new Date(watch.batch.utcMs).toISOString() },
        quietSecondsAfterFirstChange: quietAfterFirst,
        quietSecondsAfterLastChangeSeen: quietAfterLast,
        builds: builds.map((b) => ({ ms: b.t - t0, reason: b.reason, movies: b.movies, buildMs: b.ms })),
        buildsByReason: diff(s.duplicates.Builds, before.duplicates.Builds),
        lastBuildReason: s.duplicates.LastBuildReason
    };
    return { result, before, after: s, watch, builds };
}

async function phaseAdd() {
    const folders = await admin('GET', '/Library/VirtualFolders');
    const library = folders.find((f) => f.Name === opts.library);
    if (!library || !(library.Locations || []).length) throw new Error(`the sandbox has no library "${opts.library}" with a folder`);
    const location = inMedia(library.Locations[0]);

    for (const [label, change, delta] of [
        ['added', () => addMovie(location), 1],
        ['removed', () => ({ removed: removeCreated() }), -1]
    ]) {
        const { result, before, after, watch, builds } = await observeChange(label, library, change);
        evidence[`movie ${label}`] = result;
        const changeBuilds = builds.filter((b) => b.reason === REASON.changes);
        check(`(a) a movie file ${label}: the change waits in Duplicates' queue of the kit's monitor, with no build and no scan meanwhile`,
            !!watch.firstPending && !watch.buildBeforeBatch && !watch.scanSeen && result.states.some((st) => st.pending > 0 && st.Version === before.index.Version),
            { firstPending: watch.firstPending, buildBeforeBatch: watch.buildBeforeBatch, scanSeen: watch.scanSeen });
        check(`(a) a movie file ${label}: the batch goes out once the change has been quiet for ${QUIET_SECONDS} s, not before`,
            !!watch.batch && result.quietSecondsAfterFirstChange >= QUIET_SECONDS - 0.1 && result.quietSecondsAfterLastChangeSeen && result.quietSecondsAfterLastChangeSeen.atMost >= QUIET_SECONDS - 0.1,
            { batch: result.batch, quietSecondsAfterFirstChange: result.quietSecondsAfterFirstChange, quietSecondsAfterLastChangeSeen: result.quietSecondsAfterLastChangeSeen });
        check(`(a) a movie file ${label}: then one build after library changes, a higher Version, the movie counted ${delta > 0 ? 'in' : 'out'}`,
            !!watch.batch && after.index.Version === before.index.Version + 1 && !after.index.Rebuilding && !after.index.Dirty
                && changeBuilds.length === 1 && builds.length === 1 && after.duplicates.LastBuildReason === 'Changes'
                && after.index.Movies === before.index.Movies + delta && !after.index.BuiltDuringScan,
            { versions: [before.index.Version, after.index.Version], movies: [before.index.Movies, after.index.Movies], builds: result.builds, lastBuildReason: after.duplicates.LastBuildReason });
    }
}

// ------------------------------------------------------------------ (b) a full scan, (c) a cold start during one

async function waitForIndexScan(seconds) {
    const deadline = Date.now() + seconds * 1000;
    for (;;) {
        const r = await call('admin', 'GET', INDEX);
        if (r.status === 200 && r.json && r.json.ScanInProgress) return r.json;
        const tasks = await call('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } });
        const task = (tasks.json || []).find((t) => t.Key === 'RefreshLibrary');
        if (!task || task.State !== 'Running' || Date.now() > deadline) return null;
        await delay(25);
    }
}

async function phaseScan(cold) {
    let before = cold ? await sample() : await settle();
    if (cold) {
        check('(c) a cold start: no index and no build since the server started',
            before.index.Version === 0 && sum(before.duplicates.Builds) === 0 && !before.index.Rebuilding,
            { index: brief(before.index), builds: before.duplicates.Builds });
    } else if (before.index.Version === 0) {
        await rebuildAndWait();
        before = await settle();
    }

    const t0 = Date.now();
    const watch = watchIndex(100);
    const during = {};
    const { endMs } = await scan(cold ? 'cold start' : 'full scan', async () => {
        const seen = await waitForIndexScan(20);
        if (!seen) {
            during.skipped = 'the scan ended before the index saw it running';
            return;
        }
        const first = await call('admin', 'POST', REBUILD);
        during.rebuild = { ms: Date.now(), status: first.status, ...brief(first.json) };
        if (!cold) return;

        // The cold build: it runs although the scan does, once.
        const built = await waitFor('the cold build', (s) => s.index.Version >= 1 && !s.index.Rebuilding, 300, 100);
        during.coldBuilt = brief(built.index);
        const again = await admin('GET', INDEX);
        if (again.ScanInProgress) {
            const second = await call('admin', 'POST', REBUILD);
            during.second = { ms: Date.now(), status: second.status, ...brief(second.json) };
        } else {
            during.second = 'not sent: the scan had ended';
        }
    });

    // The index looks for the scan's end every 5 s; the monitor sends what came after the full pass once the scan ended.
    await delay((SCAN_POLL_SECONDS * 2 + 3) * 1000);
    const states = await watch.stop();
    const after = await sample();
    const builds = buildsSince(t0);
    const scanBuilds = builds.filter((b) => b.reason === REASON.scan);
    const duringScan = builds.filter((b) => b.reason.endsWith(REASON.duringScan));
    const result = {
        before: brief(before.index),
        after: brief(after.index),
        during: JSON.parse(JSON.stringify(during, (k, v) => (k === 'ms' && typeof v === 'number' ? v - t0 : v))),
        states: states.map((st) => ({ ...st, ms: st.ms - t0 })),
        scanEndMs: endMs - t0,
        builds: builds.map((b) => ({ ms: b.t - t0, reason: b.reason, movies: b.movies, buildMs: b.ms })),
        buildsByReason: diff(after.duplicates.Builds, before.duplicates.Builds),
        deferredByScan: (after.duplicates.DeferredByScan || 0) - (before.duplicates.DeferredByScan || 0),
        fullPasses: (after.consumer?.FullPasses || 0) - (before.consumer?.FullPasses || 0),
        lastBuildReason: after.duplicates.LastBuildReason
    };
    evidence[cold ? 'cold start' : 'full scan'] = result;

    // The index's states in time: GET /Index's every 100 ms and the Rebuilds' own answers (the same body).
    const observed = [...states, ...[during.rebuild, during.second].filter((o) => o && typeof o === 'object')].sort((a, b) => a.ms - b.ms);
    const iDirty = observed.findIndex((st) => st.Dirty && !st.Rebuilding && st.ScanInProgress);
    const iRebuilding = observed.findIndex((st, i) => i > iDirty && st.Rebuilding);
    const iHigher = observed.findIndex((st, i) => i > iDirty && st.Version > (cold ? 1 : before.index.Version) && !st.Rebuilding);
    const sequence = iDirty >= 0 && iHigher > iDirty && (iRebuilding < 0 || iRebuilding < iHigher);

    if (cold) {
        check('(c) a Rebuild during the scan builds the index once anyway, marked BuiltDuringScan, logged "during a library scan"',
            during.rebuild && during.rebuild.status === 202 && (during.rebuild.Rebuilding === true || during.rebuild.Version === 1) && during.coldBuilt && during.coldBuilt.Version === 1 && during.coldBuilt.BuiltDuringScan === true
                && duringScan.length === 1 && duringScan[0].reason === REASON.rebuild + REASON.duringScan,
            { during, duringScan });
        check('(c) a second Rebuild during the scan is put off: Dirty, not Rebuilding, the same Version',
            typeof during.second === 'object' && during.second.status === 202 && during.second.Dirty === true && during.second.Rebuilding === false && during.second.ScanInProgress === true && during.second.Version === 1,
            { second: during.second });
        check('(c) then the post-scan build, once (Dirty → Rebuilding → Version 2), not BuiltDuringScan, and nothing more',
            scanBuilds.length === 1 && builds.length === 2 && after.index.Version === 2 && after.index.BuiltDuringScan === false && !after.index.Dirty && !after.index.Rebuilding
                && result.fullPasses === 1 && after.duplicates.LastBuildReason === 'Scan' && sequence,
            { builds: result.builds, after: result.after, fullPasses: result.fullPasses, sequence: { iDirty, iRebuilding, iHigher } });
    } else {
        if (during.rebuild) {
            check('(b) a Rebuild during the scan is put off: 202, Dirty, not Rebuilding, ScanInProgress, the same Version',
                during.rebuild.status === 202 && during.rebuild.Dirty === true && during.rebuild.Rebuilding === false && during.rebuild.ScanInProgress === true && during.rebuild.Version === before.index.Version,
                { rebuild: during.rebuild, versionBefore: before.index.Version });
            check('(b) GET /Index goes Dirty → Rebuilding → a higher Version',
                sequence, { sequence: { iDirty, iRebuilding, iHigher }, observed: observed.map((st) => ({ ...st, ms: st.ms - t0 })) });
        } else {
            evidence['full scan'].note = 'The scan ended before a Rebuild could be sent during it; the rest is checked.';
        }
        check('(b) the scan rebuilds the index exactly once, in the post-scan build, which is not BuiltDuringScan; nothing follows when the scan ends',
            scanBuilds.length === 1 && builds.length === 1 && duringScan.length === 0 && after.index.Version === before.index.Version + 1
                && after.index.BuiltDuringScan === false && !after.index.Dirty && !after.index.Rebuilding && result.fullPasses === 1 && after.duplicates.LastBuildReason === 'Scan'
                && (!during.rebuild || result.deferredByScan >= 1),
            { builds: result.builds, versions: [before.index.Version, after.index.Version], fullPasses: result.fullPasses, deferredByScan: result.deferredByScan, buildsByReason: result.buildsByReason });
    }
}

// ------------------------------------------------------------------ the run

let exitCode = 1;
const started = Date.now();
try {
    // A run killed before its clean-up left its movie folder: remove it, and scan so that the library forgets it.
    if (loadCreated().length) {
        evidence.leftovers = removeCreated();
        await scan('leftovers');
    }

    const diagnostics = await admin('GET', DIAGNOSTICS);
    const consumer = ((diagnostics.LibraryChanges || {}).Consumers || []).find((c) => c.Name === CONSUMER);
    check("Diagnostics shows the kit's monitor watching the library, with Duplicates' consumer (movies and videos, 60 s quiet, 10 min at most)",
        diagnostics.LibraryChanges && diagnostics.LibraryChanges.Watching === true && consumer
            && JSON.stringify(consumer.Kinds) === JSON.stringify(['Movie', 'Video']) && consumer.QuietSeconds === QUIET_SECONDS && consumer.MaxWaitSeconds === 600 && consumer.UserData === false,
        { watching: diagnostics.LibraryChanges && diagnostics.LibraryChanges.Watching, consumer: consumer && { Kinds: consumer.Kinds, QuietSeconds: consumer.QuietSeconds, MaxWaitSeconds: consumer.MaxWaitSeconds, UserData: consumer.UserData } });

    if (opts.cold) await phaseScan(true);
    for (const phase of opts.phases) {
        if (phase === 'scan') await phaseScan(false);
        if (phase === 'add') await phaseAdd();
    }

    const errors = errorsSince(started);
    check('the server log has no Library Maintenance error since the start', errors.length === 0, { errors: errors.slice(0, 5) });
    exitCode = checks.every((c) => c.pass) ? 0 : 1;
} catch (e) {
    check('the probe ran to the end', false, { error: String(e && e.message ? e.message : e) });
} finally {
    if (loadCreated().length) {
        evidence.cleanup = { removed: removeCreated() };
        try {
            const folders = await admin('GET', '/Library/VirtualFolders');
            const library = folders.find((f) => f.Name === opts.library);
            if (library) await refreshLibrary(library);
        } catch (e) {
            evidence.cleanup.error = String(e && e.message ? e.message : e);
        }
    }
}

const text = JSON.stringify({ pass: exitCode === 0, checks, evidence }, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(exitCode);
