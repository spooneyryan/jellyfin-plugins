#!/usr/bin/env node
// Library Maintenance's movie index against a running sandbox: GET /LibraryMaintenance/Duplicates/Index,
// POST …/Index/Rebuild and the index's Diagnostics entry, with Jellyfin's own API as the independent side. Node 22 built-ins
// only. Loopback only, never port 8096. The users' tokens come from credentials.json and are never printed.
//
//   node scripts/librarymaintenance-index-probe.mjs --sandbox default [--setups] [--rebuilds <n>] [--timeout <build seconds>] [--out result.json]
//
// Without --setups it changes nothing on disk or in the library: a rebuild only replaces the plugin's in-memory index. With
// --setups it first adds, inside the sandbox, three libraries named "LM Probe …" over the sandbox's own
// media folders (one sharing Movies' folder, one with no folder, one disabled over Movies 2's folder) and merges the
// edition pair of Movies with Jellyfin's own POST /Videos/MergeVersions, scans, runs the checks, and in a finally splits the
// pair (DELETE /Videos/{id}/AlternateSources), removes those libraries, scans and rebuilds again. While each rebuild runs it
// samples the server process's working set and private bytes every 100 ms (one PowerShell Get-Process loop on the PID
// sandbox.json records, after checking that the PID's executable is jellyfin.exe and its command line names this sandbox's
// data folder).
//
// Jellyfin's own counts: /Items with a user shows merged versions once (10.11.5 groups by PresentationUniqueKey), so a
// library's movies are its listed movies plus the versions folded into them (their MediaSources of Type "Grouping") that
// are in that library too; local alternates are MediaSources of Type "Default" with another item's id.
//
// Checks:
//   1. anonymous gets 401 and the test user viewer 403 from both routes, and viewer's POST starts no build;
//   2. each POST is 202 with Rebuilding or a newer Version, and the index is built (Version up by one) within --timeout;
//   3. the index's libraries are the sandbox's enabled movies and mixed libraries (Jellyfin's /Library/VirtualFolders), each
//      library's movie count is Jellyfin's own, the index has a movie once however many libraries share its folder, and one
//      entry per version set Jellyfin merged (so movies saved on 10.11 are indexed: a HasOwnerId filter would make the counts
//      near zero);
//   4. a movie with local alternate versions (Jellyfin's "- 1080p"/"- 720p" files) is one movie: none of its alternates'
//      files is a movie of its own, and Diagnostics counts no owned item;
//   5. Diagnostics has DuplicatesIndex with the last build's milliseconds, one library query per library that has a folder,
//      one item query per 500 ids read, megabytes allocated and the working set;
//   6. the server log has the index's "indexed … movies" line for every build and no Library Maintenance error;
//   7. (--setups) the shared-folder library has Movies' count, the folderless one is listed with none, the disabled one isn't
//      read, and the merged pair is one entry;
//   8. (--setups) afterwards the sandbox has its libraries and merges back, and the index says so.
// With --rebuilds n it times n builds and reports the median, the allocations and the working set. It prints one
// JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const INDEX = '/LibraryMaintenance/Duplicates/Index';
const REBUILD = '/LibraryMaintenance/Duplicates/Index/Rebuild';
const DIAGNOSTICS = '/LibraryMaintenance/Admin/Diagnostics';
const CHUNK = 500;
const SETUP_PREFIX = 'LM Probe ';
const EDITION_PAIR = [/\\Edition Movie \(2001\) \[tmdbid-900301\]\\/i, /\\Edition Movie Extended \(2001\) \[tmdbid-900301\]\\/i];

function usage() {
    return 'usage: node scripts/librarymaintenance-index-probe.mjs --sandbox <name> [--setups] [--rebuilds <n>] [--timeout <build seconds>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, setups: false, rebuilds: 1, timeout: 300, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--setups') o.setups = true;
        else if (a === '--rebuilds') o.rebuilds = Number(argv[++i]);
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!Number.isInteger(o.rebuilds) || o.rebuilds < 1 || o.rebuilds > 50) throw new Error(usage());
    if (!Number.isFinite(o.timeout) || o.timeout <= 0) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const mediaRoot = path.join(repoRoot, '.sandbox', 'media');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
if (!logDir.startsWith(sandboxRoot + path.sep)) throw new Error(`refusing to read the log folder ${logDir}: not in the sandbox`);
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
if (!dataDir.startsWith(sandboxRoot + path.sep)) throw new Error(`refusing the data folder ${dataDir}: not in the sandbox`);
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, pid: state.Pid, time: new Date().toISOString() }, setups: opts.setups, builds: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// Jellyfin writes GUIDs without dashes ("N"); ASP.NET Core's JSON writes them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const round = (n, d = 1) => (typeof n === 'number' ? Math.round(n * 10 ** d) / 10 ** d : n);
const median = (values) => {
    const sorted = values.filter((v) => typeof v === 'number').sort((a, b) => a - b);
    if (sorted.length === 0) return null;
    const mid = Math.floor(sorted.length / 2);
    return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
};

async function call(who, method, p, { query, body } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="librarymaintenance-index-probe", Device="sandbox", DeviceId="librarymaintenance-index-probe", Version="1.0.0", Token="${users[who].token}"`;
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

// The server process, checked before it is sampled: jellyfin.exe with this sandbox's data folder on its command line.
function serverProcess() {
    const pid = Number(state.Pid);
    if (!Number.isInteger(pid) || pid <= 0) return null;
    const r = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
        `$p = Get-CimInstance Win32_Process -Filter "ProcessId = ${pid}"; if ($p) { $p.ExecutablePath; $p.CommandLine }`], { encoding: 'utf8' });
    const [exe, commandLine] = (r.stdout || '').split(/\r?\n/);
    if (!exe || !/\\jellyfin\.exe$/i.test(exe.trim()) || !commandLine || !commandLine.toLowerCase().includes(dataDir.toLowerCase())) return null;
    return pid;
}

// Samples the working set and private bytes every 100 ms until stopped, each sample with the time it arrived.
function startSampler(pid) {
    if (!pid) return { samples: [], stop: async () => [] };
    const child = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
        `while ($true) { $p = Get-Process -Id ${pid} -ErrorAction SilentlyContinue; if (-not $p) { break }; '{0} {1}' -f $p.WorkingSet64, $p.PrivateMemorySize64; [Console]::Out.Flush(); Start-Sleep -Milliseconds 100 }`],
    { stdio: ['ignore', 'pipe', 'ignore'] });
    const samples = [];
    let buffer = '';
    child.stdout.on('data', (chunk) => {
        buffer += chunk.toString();
        const lines = buffer.split(/\r?\n/);
        buffer = lines.pop();
        for (const line of lines) {
            const [ws, priv] = line.trim().split(' ').map(Number);
            if (Number.isFinite(ws)) samples.push({ t: Date.now(), ws, priv });
        }
    });
    return {
        samples,
        stop: async () => {
            child.kill();
            await delay(50);
            return samples;
        }
    };
}

function logLines() {
    const files = fs.readdirSync(logDir).filter((f) => /^log_\d+\.log$/.test(f)).sort();
    return files.flatMap((f) => fs.readFileSync(path.join(logDir, f), 'utf8').split(/\r?\n/));
}

async function waitBuilt(version) {
    const deadline = Date.now() + opts.timeout * 1000;
    for (;;) {
        const index = await admin('GET', INDEX);
        if (!index.Rebuilding && index.Version > version) return index;
        if (Date.now() > deadline) throw new Error(`the index wasn't built within ${opts.timeout} s (Version ${index.Version}, Rebuilding ${index.Rebuilding})`);
        await delay(100);
    }
}

async function rebuildOnce() {
    const index = await admin('GET', INDEX);
    const post = await call('admin', 'POST', REBUILD);
    if (post.status !== 202) throw new Error(`POST ${REBUILD}: ${post.status}`);
    return waitBuilt(index.Version);
}

// ------------------------------------------------------------------ Jellyfin's side

const isMovieLibrary = (f) => !f.CollectionType || f.CollectionType === 'movies' || f.CollectionType === 'mixed';
const isEnabled = (f) => !(f.LibraryOptions && f.LibraryOptions.Enabled === false);

async function movieQuery(folder, extra) {
    return admin('GET', '/Items', { query: { userId: users.admin.id, ParentId: folder.ItemId, Recursive: true, IncludeItemTypes: 'Movie', EnableImages: false, EnableUserData: false, ...extra } });
}

// Every enabled movies or mixed library with its movies, merged versions counted, and the version sets Jellyfin merged.
async function jellyfinSide() {
    const folders = await admin('GET', '/Library/VirtualFolders');
    const libraries = [];
    for (const folder of folders.filter((f) => isMovieLibrary(f) && isEnabled(f))) {
        const located = (folder.Locations || []).length > 0;
        const listed = located ? (await movieQuery(folder, { Fields: 'Path,MediaSources' })).Items : [];
        const listedIds = new Set(listed.map((i) => guid(i.Id)));
        const grouping = [...new Set(listed.flatMap((i) => (i.MediaSources || []).filter((s) => s.Type === 'Grouping').map((s) => guid(s.Id))))]
            .filter((id) => !listedIds.has(id));
        const folded = [];
        for (const id of grouping) {
            if ((await movieQuery(folder, { Ids: id })).TotalRecordCount > 0) folded.push(id);
        }
        libraries.push({ id: guid(folder.ItemId), name: folder.Name, kind: folder.CollectionType || 'mixed', located, listed: listed.length, movies: listed.length + folded.length, ids: [...listedIds, ...folded], items: listed });
    }

    // The version sets: each listed movie with the versions folded into it (union-find over the links).
    const parent = new Map();
    const find = (x) => { while (parent.get(x) !== x) { parent.set(x, parent.get(parent.get(x))); x = parent.get(x); } return x; };
    const ids = new Set(libraries.flatMap((l) => l.ids));
    for (const id of ids) parent.set(id, id);
    for (const item of libraries.flatMap((l) => l.items)) {
        for (const source of (item.MediaSources || []).filter((s) => s.Type === 'Grouping')) {
            const a = find(guid(item.Id)); const b = parent.has(guid(source.Id)) ? find(guid(source.Id)) : null;
            if (b && a !== b) parent.set(a, b);
        }
    }
    const sizes = new Map();
    for (const id of ids) sizes.set(find(id), (sizes.get(find(id)) || 0) + 1);
    return {
        folders,
        libraries,
        movies: ids.size,
        entries: sizes.size,
        merged: [...sizes.values()].filter((n) => n > 1).length,
        disabled: folders.filter((f) => isMovieLibrary(f) && !isEnabled(f)).map((f) => ({ id: guid(f.ItemId), name: f.Name }))
    };
}

// ------------------------------------------------------------------ the setups (--setups)

const setup = { created: [], merged: null };

function inMedia(p) {
    const resolved = path.resolve(p).toLowerCase();
    return resolved.startsWith(path.resolve(mediaRoot).toLowerCase() + path.sep);
}

function libraryOptions(enabled, paths) {
    return {
        LibraryOptions: {
            Enabled: enabled, EnableRealtimeMonitor: false, EnableInternetProviders: false, SaveLocalMetadata: false,
            EnableChapterImageExtraction: false, ExtractChapterImagesDuringLibraryScan: false, EnableTrickplayImageExtraction: false,
            ExtractTrickplayImagesDuringLibraryScan: false, EnableEmbeddedTitles: false, AutomaticRefreshIntervalDays: 0,
            MetadataSavers: [], DisabledSubtitleFetchers: [],
            TypeOptions: [{ Type: 'Movie', MetadataFetchers: [], MetadataFetcherOrder: [], ImageFetchers: [], ImageFetcherOrder: [] }],
            PathInfos: paths.map((p) => ({ Path: p }))
        }
    };
}

async function scanAndWait() {
    const started = Date.now();
    await admin('POST', '/Library/Refresh');
    const deadline = started + opts.timeout * 1000;
    for (;;) {
        await delay(500);
        const task = ((await admin('GET', '/ScheduledTasks', { query: { isHidden: false } })) || []).find((t) => t.Key === 'RefreshLibrary');
        const end = task && task.LastExecutionResult && Date.parse(task.LastExecutionResult.EndTimeUtc);
        if (task && task.State === 'Idle' && end >= started - 1000) return;
        if (Date.now() > deadline) throw new Error(`the library scan didn't finish within ${opts.timeout} s`);
    }
}

async function addLibrary(name, paths, enabled) {
    if (!name.startsWith(SETUP_PREFIX) || !paths.every(inMedia)) throw new Error(`refusing the library ${name}: only "${SETUP_PREFIX}…" over the sandbox's media`);
    const query = { name, collectionType: 'movies', refreshLibrary: false };
    if (paths.length) query.paths = paths.join(',');
    await admin('POST', '/Library/VirtualFolders', { query, body: libraryOptions(enabled, paths) });
    setup.created.push(name);
}

async function applySetups() {
    const folders = await admin('GET', '/Library/VirtualFolders');
    // A run that stopped before its finally left its libraries: they are this probe's, by name.
    for (const leftover of folders.filter((f) => f.Name.startsWith(SETUP_PREFIX))) setup.created.push(leftover.Name);
    if (setup.created.length) await removeSetups();
    const movies = folders.find((f) => f.Name === 'Movies');
    const movies2 = folders.find((f) => f.Name === 'Movies 2');
    if (!movies || !movies2 || !(movies.Locations || []).length || !(movies2.Locations || []).length) throw new Error('the sandbox has no Movies and Movies 2 libraries; run Initialize-Sandbox.ps1');
    await addLibrary(`${SETUP_PREFIX}Shared`, [movies.Locations[0]], true);
    await addLibrary(`${SETUP_PREFIX}Folderless`, [], true);
    await addLibrary(`${SETUP_PREFIX}Disabled`, [movies2.Locations[0]], false);
    await scanAndWait();

    // Jellyfin's own merge of the edition pair ("Group versions").
    const items = (await movieQuery(movies, { Fields: 'Path' })).Items;
    const pair = EDITION_PAIR.map((pattern) => items.find((i) => pattern.test(i.Path || '')));
    if (pair.some((i) => !i)) throw new Error('the edition pair is missing from Movies; run New-SandboxMedia.ps1');
    const ids = pair.map((i) => guid(i.Id));
    const merge = await call('admin', 'POST', '/Videos/MergeVersions', { query: { ids: ids.join(',') } });
    if (merge.status !== 204) throw new Error(`POST /Videos/MergeVersions: ${merge.status}`);
    setup.merged = ids;
    return { created: [...setup.created], merged: ids, names: pair.map((i) => i.Name) };
}

async function removeSetups() {
    const done = {};
    if (setup.merged) {
        const split = await call('admin', 'DELETE', `/Videos/${setup.merged[0]}/AlternateSources`);
        done.split = split.status;
        setup.merged = null;
    }
    // refreshLibrary true, as the dashboard sends it: 10.11.5 drops a removed library from its root folder's children only
    // in ValidateTopLibraryFolders(removeRoot: true), which a scan doesn't run, so with false /UserViews (and the index)
    // would keep it until the next start.
    for (const name of setup.created.splice(0)) {
        if (!name.startsWith(SETUP_PREFIX)) continue;
        done[name] = (await call('admin', 'DELETE', '/Library/VirtualFolders', { query: { name, refreshLibrary: true } })).status;
    }
    await scanAndWait();
    return done;
}

// ------------------------------------------------------------------ the run

let exitCode = 1;
let librariesBefore = null;
try {
    const startLines = logLines().length;
    librariesBefore = (await admin('GET', '/Library/VirtualFolders')).map((f) => f.Name).filter((n) => !n.startsWith(SETUP_PREFIX)).sort();
    if (opts.setups) evidence.setup = await applySetups();

    // 1. Who may call it.
    const before = await admin('GET', INDEX);
    const anonymous = [await call('anonymous', 'GET', INDEX), await call('anonymous', 'POST', REBUILD)].map((r) => r.status);
    const viewer = [await call('viewer', 'GET', INDEX), await call('viewer', 'POST', REBUILD)].map((r) => r.status);
    await delay(300);
    const afterViewer = await admin('GET', INDEX);
    check('anonymous gets 401, viewer 403, and viewer starts no build',
        anonymous.every((s) => s === 401) && viewer.every((s) => s === 403) && afterViewer.Version === before.Version && !afterViewer.Rebuilding,
        { anonymous, viewer, versionBefore: before.Version, versionAfter: afterViewer.Version });

    // 2. The builds, with the process sampled.
    const pid = serverProcess();
    evidence.sampled = !!pid;
    const sampler = startSampler(pid);
    let index = afterViewer;
    for (let i = 0; i < opts.rebuilds; i++) {
        // A second and a half of the idle server first (the sampler's PowerShell starts in about half a second).
        await delay(i === 0 ? 2000 : 1500);
        const t0 = Date.now();
        const idle = sampler.samples.filter((s) => s.t >= t0 - 1000 && s.t < t0);
        const post = await call('admin', 'POST', REBUILD);
        const accepted = post.status === 202 && post.json && (post.json.Rebuilding || post.json.Version > index.Version);
        const built = await waitBuilt(index.Version);
        const wall = Date.now() - t0;
        await delay(300);
        const samples = sampler.samples.filter((s) => s.t >= t0);
        const diagnostics = (await admin('GET', DIAGNOSTICS)).DuplicatesIndex || {};
        const idleWs = median(idle.map((s) => s.ws));
        const peakWs = samples.length ? Math.max(...samples.map((s) => s.ws)) : null;
        const peakPriv = samples.length ? Math.max(...samples.map((s) => s.priv)) : null;
        const idlePriv = median(idle.map((s) => s.priv));
        evidence.builds.push({
            n: i + 1,
            post: post.status,
            accepted: !!accepted,
            version: built.Version,
            buildMs: built.BuildMs,
            wallMs: wall,
            movies: built.Movies,
            entries: built.Entries,
            mergedEntries: built.MergedEntries,
            brokenEntries: built.BrokenEntries,
            builtDuringScan: built.BuiltDuringScan,
            libraryQueries: diagnostics.LibraryQueries,
            itemQueries: diagnostics.ItemQueries,
            allocatedMB: diagnostics.AllocatedMB,
            workingSetMB: diagnostics.WorkingSetMB,
            peakWorkingSetMB: diagnostics.PeakWorkingSetMB,
            sampled: { idleWsMB: round(idleWs / 1048576), peakWsMB: round(peakWs / 1048576), wsRiseMB: round((peakWs - idleWs) / 1048576), idlePrivateMB: round(idlePriv / 1048576), peakPrivateMB: round(peakPriv / 1048576), privateRiseMB: round((peakPriv - idlePriv) / 1048576), samples: samples.length }
        });
        check(`rebuild ${i + 1}: 202, then built (Version ${index.Version} → ${built.Version})`, accepted && built.Version === index.Version + 1, { status: post.status, buildMs: built.BuildMs });
        index = built;
    }

    await sampler.stop();

    // 3. The libraries and their counts, against Jellyfin's own.
    const jellyfin = await jellyfinSide();
    const indexed = index.Libraries.map((l) => ({ id: guid(l.Id), name: l.Name, kind: l.Kind, movies: l.Movies }));
    evidence.libraries = { index: indexed, jellyfin: jellyfin.libraries.map(({ items, ids, ...rest }) => rest), disabled: jellyfin.disabled };
    const sameLibraries = JSON.stringify(indexed.map((l) => l.id).sort()) === JSON.stringify(jellyfin.libraries.map((l) => l.id).sort());
    const sameCounts = jellyfin.libraries.every((j) => (indexed.find((l) => l.id === j.id) || {}).movies === j.movies);
    const noDisabled = jellyfin.disabled.every((d) => !indexed.some((l) => l.id === d.id));
    evidence.versionSets = { jellyfin: { movies: jellyfin.movies, entries: jellyfin.entries, merged: jellyfin.merged }, index: { movies: index.Movies, entries: index.Entries, merged: index.MergedEntries } };
    check("the index reads the enabled movies and mixed libraries, each with Jellyfin's own movie count, a movie once, a merged set once",
        sameLibraries && sameCounts && noDisabled && jellyfin.movies > 0 && index.Movies === jellyfin.movies && index.Entries === jellyfin.entries && index.MergedEntries === jellyfin.merged,
        { sameLibraries, sameCounts, noDisabled, ...evidence.versionSets });

    // 4. Local alternates: one movie, whose alternates' files are no movies of their own.
    const items = jellyfin.libraries.flatMap((j) => j.items);
    const alternatesOf = (i) => (i.MediaSources || []).filter((s) => s.Type === 'Default' && guid(s.Id) !== guid(i.Id));
    const withAlternates = items.filter((i) => alternatesOf(i).length > 0);
    const alternatePaths = [...new Set(withAlternates.flatMap((i) => alternatesOf(i).map((s) => s.Path).filter(Boolean)))];
    const moviePaths = new Set(items.flatMap((i) => [i.Path, ...(i.MediaSources || []).filter((s) => s.Type === 'Grouping').map((s) => s.Path)]).filter(Boolean).map((p) => p.toLowerCase()));
    const diagnostics = (await admin('GET', DIAGNOSTICS)).DuplicatesIndex || {};
    evidence.localAlternates = { movies: [...new Set(withAlternates.map((i) => i.Name))].map((name) => ({ name, sources: 1 + alternatesOf(withAlternates.find((i) => i.Name === name)).length })), alternateFiles: alternatePaths.map((p) => path.basename(p)), owned: diagnostics.Owned };
    check('a movie with local alternates is one movie, and no owned item is indexed',
        withAlternates.length > 0 && alternatePaths.length > 0 && alternatePaths.every((p) => !moviePaths.has(p.toLowerCase())) && index.Movies === jellyfin.movies && diagnostics.Owned === 0,
        evidence.localAlternates);

    // 5. Diagnostics.
    const located = jellyfin.libraries.filter((j) => j.located).length;
    const read = index.Movies + (index.Skipped || 0) + (index.Excluded || 0) + (diagnostics.Owned || 0);
    check('Diagnostics has the last build: its time, one library query per library with a folder, one item query per 500 ids, its cost',
        diagnostics.Version === index.Version && typeof diagnostics.BuildMs === 'number' && diagnostics.LibraryQueries === located
            && diagnostics.ItemQueries === Math.ceil(read / CHUNK) && typeof diagnostics.AllocatedMB === 'number' && diagnostics.PeakWorkingSetMB > 0,
        { Version: diagnostics.Version, BuildMs: diagnostics.BuildMs, LibraryQueries: diagnostics.LibraryQueries, located, ItemQueries: diagnostics.ItemQueries, read, AllocatedMB: diagnostics.AllocatedMB, WorkingSetMB: diagnostics.WorkingSetMB, PeakWorkingSetMB: diagnostics.PeakWorkingSetMB });

    // 6. The log.
    await delay(500);
    const lines = logLines().slice(startLines);
    const indexedLines = lines.filter((l) => /Library Maintenance indexed \d+ movies/.test(l));
    const errors = lines.filter((l) => /\[(ERR|FTL)\]/.test(l) && /LibraryMaintenance|Library Maintenance/.test(l));
    evidence.log = { indexedLines: indexedLines.slice(-3), errors: errors.slice(0, 5) };
    check('the log has an "indexed" line per build and no Library Maintenance error', indexedLines.length >= opts.rebuilds && errors.length === 0, { indexed: indexedLines.length, errors: errors.length });

    // 7. The setups, each a regression case.
    if (opts.setups) {
        const byName = (name) => indexed.find((l) => l.name === name);
        const shared = byName(`${SETUP_PREFIX}Shared`);
        const folderless = byName(`${SETUP_PREFIX}Folderless`);
        check('the setups: a shared folder counts in both libraries, a folderless library is listed with none, a disabled one is not read, the merged pair is one entry',
            !!shared && !!byName('Movies') && shared.movies === byName('Movies').movies && !!folderless && folderless.movies === 0
                && !byName(`${SETUP_PREFIX}Disabled`) && jellyfin.disabled.some((d) => d.name === `${SETUP_PREFIX}Disabled`)
                && jellyfin.merged >= 1 && index.MergedEntries === jellyfin.merged,
            { shared: shared && shared.movies, movies: byName('Movies') && byName('Movies').movies, folderless: folderless && folderless.movies, disabledIndexed: !!byName(`${SETUP_PREFIX}Disabled`), merged: index.MergedEntries, entries: index.Entries, indexMovies: index.Movies });
    }

    const builds = evidence.builds;
    evidence.summary = {
        movies: index.Movies,
        rebuilds: builds.length,
        medianBuildMs: median(builds.map((b) => b.buildMs)),
        buildMs: builds.map((b) => b.buildMs),
        medianAllocatedMB: median(builds.map((b) => b.allocatedMB)),
        medianWsRiseMB: median(builds.map((b) => b.sampled.wsRiseMB)),
        maxPeakWsMB: Math.max(...builds.map((b) => b.sampled.peakWsMB || 0)),
        processPeakWorkingSetMB: builds[builds.length - 1].peakWorkingSetMB
    };
    exitCode = checks.every((c) => c.pass) ? 0 : 1;
} catch (e) {
    check('the probe ran', false, String(e && e.stack || e));
} finally {
    if (opts.setups) {
        // 8. The sandbox as it was: the merge split, the probe's libraries gone, the index rebuilt over them.
        try {
            const removed = await removeSetups();
            const after = await rebuildOnce();
            const jellyfin = await jellyfinSide();
            const names = jellyfin.folders.map((f) => f.Name).sort();
            const views = ((await admin('GET', '/Library/MediaFolders')).Items || []).map((f) => f.Name).sort();
            evidence.teardown = { removed, libraries: names, mediaFolders: views, indexLibraries: after.Libraries.map((l) => l.Name), index: { movies: after.Movies, entries: after.Entries, merged: after.MergedEntries }, jellyfin: { movies: jellyfin.movies, entries: jellyfin.entries, merged: jellyfin.merged } };
            check('afterwards the sandbox has its libraries and merges back, and the index says so',
                JSON.stringify(names) === JSON.stringify(librariesBefore) && !views.some((n) => n.startsWith(SETUP_PREFIX))
                    && after.Movies === jellyfin.movies && after.Entries === jellyfin.entries && after.MergedEntries === jellyfin.merged && !after.Libraries.some((l) => l.Name.startsWith(SETUP_PREFIX)),
                evidence.teardown);
        } catch (e) {
            check('afterwards the sandbox has its libraries and merges back, and the index says so', false, String(e && e.stack || e));
        }
        exitCode = checks.every((c) => c.pass) ? 0 : 1;
    }
}

const result = { pass: exitCode === 0 && checks.every((c) => c.pass), checks, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
