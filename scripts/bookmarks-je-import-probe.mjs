#!/usr/bin/env node
// Bookmarks' Jellyfin Enhanced import in the running je sandbox: the Jellyfin Enhanced bookmark import
// end to end with the synthetic 81-style fixture (kit/testdata/je-data/import-81: three user folders, 81 bookmarks, 72
// named), then the full switch-off checklist. It maps the fixture's made-up item ids to the sandbox's videos (by kind, name
// and year, or series and numbers) and writes the three bookmark.json files into the sandbox's JE data folder (the fixture's
// user ...81 as admin, ...82 as viewer, ...83 as a user the server doesn't have), gives viewer a bookmark of their own that
// one JE bookmark duplicates, and then: the gate closed (je-settings.json), the API's dry run with the expected numbers and
// nothing written (and again with viewer's folder under a second spelling, still one user), the settings
// page's Dry run and Import (je-import-ui.json), the bookmarks as the API lists them with each run's AppliedBy, a
// second import that changes nothing (the file's bytes), a JE entry changed later rewritten while a locally renamed one
// keeps its name, then JE's Bookmarks switched off step by step in the checklist's order with restarts (a cleared key
// coming back as B at JE's next start, Disabled lasting), and the admin's My Bookmarks page with the imported bookmarks
// (je-import-page.json).
// Node 22 built-ins only. Loopback only, never port 8096. Tokens come from credentials.json and are never printed.
//
//   node scripts/bookmarks-je-import-probe.mjs --sandbox je [--out result.json] [--skip-webchecks]
//
// It writes only under the sandbox: the JE bookmark files (deleted again), JE's, Custom Tabs' and Bookmarks' settings
// through their configuration APIs (put back), and bookmarks through Bookmarks' API; it restarts the sandbox through
// Stop-Sandbox.ps1 and dev-server.ps1 (-Background -NoBuild, so deploy a current build first; -Port as sandbox.json has it,
// so a sandbox on another port than its name's standard one comes back on its own), and in a finally, with the
// sandbox stopped, puts Bookmarks' data file back as it was (its copy is taken first) and deletes the snapshots the run's
// imports took; Initialize-Sandbox.ps1 -Name je then asserts the pinned fixture. It prints one JSON result (pass, checks,
// evidence) and exits with 0 when every check passed; the sandbox is left running.
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const JE_ID = 'f69e946a-4b3c-4e9a-8f0a-8d7c1b2c4d9b';
const CUSTOM_TABS_ID = 'fbacd0b6-fd46-4a05-b0a4-2045d6a135b0';
const BOOKMARKS_ID = 'd0ee1b2a-7f71-4654-8ff2-163a81441b03';
const JE_PAGE_ID = 'Jellyfin.Plugin.JellyfinEnhanced.BookmarksPage';
const JE_TAB_HTML = '<div class="sections bookmarks"></div>';
const FIXTURE = path.join(repoRoot, 'kit', 'testdata', 'je-data', 'import-81');
const FIXTURE_ADMIN = '00000000000000000000000000000081';
const FIXTURE_VIEWER = '00000000000000000000000000000082';
const FIXTURE_NOBODY = '00000000000000000000000000000083';
const TICKS = 10000000;

// What the fixture gives (JellyfinEnhancedBookmarkImporterTests asserts the same against the fake library).
const EXPECTED = {
    admin: { Found: 70, Import: 64, MatchedById: 57, MatchedByProviderId: 3, Ambiguous: 1, Detached: 3, Updated: 0, Duplicates: 2, AlreadyImported: 0, Skipped: 4, Clamped: 1 },
    viewer: { Found: 9, Import: 8, MatchedById: 7, MatchedByProviderId: 0, Ambiguous: 0, Detached: 1, Updated: 0, Duplicates: 1, AlreadyImported: 0, Skipped: 0, Clamped: 0 },
    nobody: { Found: 2, Import: 0, MatchedById: 0, MatchedByProviderId: 0, Ambiguous: 0, Detached: 0, Updated: 0, Duplicates: 0, AlreadyImported: 0, Skipped: 2, Clamped: 0 }
};

function usage() {
    return 'usage: node scripts/bookmarks-je-import-probe.mjs --sandbox <name> [--out <file>] [--skip-webchecks]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, webchecks: true };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--skip-webchecks') o.webchecks = false;
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const workRoot = path.join(sandboxRoot, 'bookmarks-je-import-probe');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
let state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
if (!(state.Community || []).includes('JellyfinEnhanced')) throw new Error('this sandbox has no Jellyfin Enhanced; use the je sandbox');
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const dashed = (n) => `${n.slice(0, 8)}-${n.slice(8, 12)}-${n.slice(12, 16)}-${n.slice(16, 20)}-${n.slice(20)}`;
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
const jeDataFolder = path.join(dataDir, 'plugins', 'configurations', 'Jellyfin.Plugin.JellyfinEnhanced');
const bookmarksFolder = path.join(dataDir, 'data', 'bookmarks');
const storeFile = path.join(bookmarksFolder, 'bookmarks.json');
const preOpFolder = path.join(bookmarksFolder, 'snapshots', 'pre-op');
const backupFolder = path.join(workRoot, 'store-backup');
function assertInside(p, root) {
    const rel = path.relative(root, p);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(p)) throw new Error(`refusing to work in ${p}`);
}
for (const p of [jeDataFolder, storeFile, preOpFolder, workRoot]) assertInside(p, sandboxRoot);
const jeTargets = { [FIXTURE_ADMIN]: guid(users.admin.id), [FIXTURE_VIEWER]: guid(users.viewer.id), [FIXTURE_NOBODY]: FIXTURE_NOBODY };

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, steps: [], restarts: [], webchecks: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

async function call(who, method, p, { body } = {}) {
    const url = new URL(baseUrl + p, origin);
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="bookmarks-je-import-probe", Device="sandbox", DeviceId="bookmarks-je-import-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = typeof body === 'string' ? body : JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const bytes = Buffer.from(await res.arrayBuffer());
    let json = null;
    if (bytes.length && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(bytes.toString('utf8')); } catch { json = null; }
    }
    return { status: res.status, json };
}

async function getPluginConfig(id) {
    const r = await call('admin', 'GET', `/Plugins/${id}/Configuration`);
    if (r.status !== 200 || !r.json) throw new Error(`GET /Plugins/${id}/Configuration: ${r.status}`);
    return r.json;
}

async function setPluginConfig(id, config) {
    const r = await call('admin', 'POST', `/Plugins/${id}/Configuration`, { body: config });
    if (r.status !== 204) throw new Error(`POST /Plugins/${id}/Configuration: ${r.status}`);
}

async function patchJe(patch) {
    const current = await getPluginConfig(JE_ID);
    await setPluginConfig(JE_ID, { ...current, ...patch });
}

async function setJeShortcut(name, key) {
    const current = await getPluginConfig(JE_ID);
    await setPluginConfig(JE_ID, { ...current, Shortcuts: (current.Shortcuts || []).map((s) => (s.Name === name ? { ...s, Key: key } : s)) });
}

async function importJe(dryRun, body) {
    const r = await call('admin', 'POST', `/Bookmarks/Admin/Import/JellyfinEnhanced${dryRun === null ? '' : `?dryRun=${dryRun}`}`, { body });
    if (r.status !== 200 || !r.json) throw new Error(`the import (dryRun=${dryRun}) answered ${r.status} ${JSON.stringify(r.json)}`);
    return r.json;
}

async function query(who, params) {
    const q = new URLSearchParams({ limit: '200', ...params });
    const r = await call(who, 'GET', `/Bookmarks/Query?${q}`);
    if (r.status !== 200 || !r.json) throw new Error(`GET /Bookmarks/Query as ${who}: ${r.status}`);
    return r.json;
}

const counts = (row) => row && Object.fromEntries(Object.keys(EXPECTED.admin).map((k) => [k, row[k]]));
const rowOf = (report, jeUser) => report.Users.find((u) => u.JeUserId === jeUser);
const storeBytes = () => fs.readFileSync(storeFile).toString('base64');

// ------------------------------------------------------------------------------------------ what the server says

async function surfaces(label, { restarted = false } = {}) {
    await delay(700); // the kit's reader keeps one snapshot for 500 ms
    const diagnostics = (await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics')).json || {};
    const je = diagnostics.JellyfinEnhanced || {};
    const gate = diagnostics.JeImport || {};
    const publicConfig = (await call('admin', 'GET', '/JellyfinEnhanced/public-config')).json || {};
    const pages = (await call('admin', 'GET', '/PluginPages/User')).json || {};
    const pageItems = Array.isArray(pages.Items) ? pages.Items : Array.isArray(pages) ? pages : [];
    const tabs = (await getPluginConfig(CUSTOM_TABS_ID)).Tabs || [];
    const jeConfig = await getPluginConfig(JE_ID);
    const record = {
        label,
        restarted,
        je: {
            bookmarksEnabled: !!jeConfig.BookmarksEnabled,
            bookmarkCurrentTimeKeys: (jeConfig.Shortcuts || []).filter((s) => s.Name === 'BookmarkCurrentTime').map((s) => s.Key),
            publicBookmarksEnabled: publicConfig.BookmarksEnabled
        },
        pluginPagesLink: pageItems.some((p) => p.Id === JE_PAGE_ID),
        customTab: tabs.some((t) => String(t.ContentHtml || '').trim() === JE_TAB_HTML),
        gate: { users: gate.UsersWithBookmarks, pending: (gate.Pending || []).map((p) => p.UserName || p.JeUserId), ready: gate.ChecklistReady, runs: (gate.Runs || []).length, appliedBy: (gate.Runs || []).map((r) => r.AppliedByName || r.AppliedBy || null), imported: gate.ImportedBookmarks },
        ours: {
            banner: je.Overlap && je.Overlap.Banner,
            checklist: ((je.Overlap && je.Overlap.Checklist) || []).map((i) => ({ key: i.Key, done: i.Done, needsRestart: i.NeedsRestart })),
            complete: je.Overlap && je.Overlap.Complete,
            canUseB: je.Overlap && je.Overlap.CanUseB,
            useBNote: je.Overlap && je.Overlap.UseBNote
        }
    };
    evidence.steps.push(record);
    return record;
}

const item = (record, key) => record.ours.checklist.find((i) => i.key === key) || {};

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

const stopSandbox = () => runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox]);

async function startSandbox(label, stopSeconds) {
    const start = runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild']);
    state = readJson(path.join(sandboxRoot, 'sandbox.json'));
    if (Number(state.Port) !== port) throw new Error(`the sandbox came back on port ${state.Port}`);
    evidence.restarts.push({ label, stopSeconds, startSeconds: start });
    for (let i = 0; i < 60; i++) {
        const r = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics').catch(() => null);
        const je = await call('admin', 'GET', '/JellyfinEnhanced/public-config').catch(() => null);
        if (r && r.status === 200 && je && je.status === 200) return;
        await delay(1000);
    }
    throw new Error('the sandbox did not answer after the restart');
}

const restart = async (label) => startSandbox(label, stopSandbox());

function webcheck(steps, label) {
    if (!opts.webchecks) return null;
    fs.mkdirSync(workRoot, { recursive: true });
    const out = path.join(workRoot, `${label}.json`);
    const w = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', steps, '--out', out], {
        cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'], timeout: 900000, windowsHide: true, encoding: 'utf8'
    });
    let result = null;
    try { result = readJson(out); } catch { result = null; }
    const summary = { steps, label, exit: w.status, ok: !!(result && result.ok === true), stepsRun: result && Array.isArray(result.steps) ? result.steps.length : null, failed: result && Array.isArray(result.steps) ? result.steps.filter((s) => s.ok === false).map((s) => ({ name: s.name, error: s.error })) : null, leftovers: result && result.leftovers };
    evidence.webchecks.push(summary);
    check(`webcheck ${steps} (${label})`, summary.ok, summary);
    return result;
}

// ------------------------------------------------------------------------------------------ the fixture in the sandbox

async function mapLibrary() {
    // The admin's videos, with provider ids; each fixture item is the sandbox video of the same kind, name and year (a film),
    // or the same series and numbers (an episode).
    const r = await call('admin', 'GET', `/Items?userId=${users.admin.id}&Recursive=true&IncludeItemTypes=Movie,Episode&Fields=ProviderIds,Path&EnableImages=false&Limit=2000`);
    if (r.status !== 200 || !r.json) throw new Error(`GET /Items: ${r.status}`);
    const videos = r.json.Items || [];
    const library = readJson(path.join(FIXTURE, 'library.json'));
    const map = {};
    const missing = [];
    // The sandbox's libraries fetch no metadata, so a name is the folder's: "Long Test Movie (2021)", "Tagged Movie (2012)
    // [tmdbid-900001]", "Test Series (2020) [tvdbid-900401]"; the year and the tag are left out for the comparison.
    const bare = (name) => String(name || '').replace(/\s*\[[^\]]*\]\s*$/, '').replace(/\s*\(\d{4}\)\s*$/, '').normalize('NFC');
    for (const it of library.Items) {
        const found = it.Kind === 'Episode'
            ? videos.find((v) => v.Type === 'Episode' && bare(v.SeriesName) === it.Series && v.ParentIndexNumber === it.Season && v.IndexNumber === it.Episode)
            : videos.filter((v) => v.Type === 'Movie' && bare(v.Name) === it.Name.normalize('NFC') && v.ProductionYear === it.Year).sort((x, y) => String(x.Path).localeCompare(String(y.Path)))[0];
        if (found) map[it.Id] = { key: it.Key, id: guid(found.Id), tmdb: (found.ProviderIds || {}).Tmdb || null, runtime: found.RunTimeTicks || null };
        else missing.push(it.Key);
    }
    return { map, missing, videos };
}

function writeJeFiles(map) {
    const written = [];
    for (const [fixtureUser, target] of Object.entries(jeTargets)) {
        const folder = path.join(jeDataFolder, target);
        assertInside(folder, jeDataFolder);
        const file = path.join(folder, 'bookmark.json');
        if (fs.existsSync(file)) throw new Error(`${file} is there already; the probe writes only its own JE files`);
        const created = !fs.existsSync(folder);
        let text = fs.readFileSync(path.join(FIXTURE, fixtureUser, 'bookmark.json'), 'utf8');
        for (const [fixtureId, to] of Object.entries(map)) text = text.split(fixtureId).join(to.id);
        fs.mkdirSync(folder, { recursive: true });
        fs.writeFileSync(file, text);
        written.push({ file, folder, created });
    }
    return written;
}

function removeJeFiles(written) {
    for (const w of written) {
        if (fs.existsSync(w.file)) fs.rmSync(w.file);
        if (w.created && fs.existsSync(w.folder) && fs.readdirSync(w.folder).length === 0) fs.rmdirSync(w.folder);
    }
}

// ------------------------------------------------------------------------------------------ the run

async function main() {
    let failure = null;
    const original = { je: null, tabs: null, bookmarks: null };
    let written = [];
    let backedUp = false;
    let preOpBefore = [];
    try {
        original.je = await getPluginConfig(JE_ID);
        original.tabs = await getPluginConfig(CUSTOM_TABS_ID);
        original.bookmarks = await getPluginConfig(BOOKMARKS_ID);
        evidence.jeVersion = ((await call('admin', 'GET', '/Plugins')).json || []).filter((p) => guid(p.Id) === guid(JE_ID)).map((p) => `${p.Version} ${p.Status}`);

        // Bookmarks' data file as it was, for the finally (put back with the sandbox stopped).
        fs.rmSync(backupFolder, { recursive: true, force: true });
        fs.mkdirSync(backupFolder, { recursive: true });
        for (const name of ['bookmarks.json', 'bookmarks.json.bak']) {
            if (fs.existsSync(path.join(bookmarksFolder, name))) fs.copyFileSync(path.join(bookmarksFolder, name), path.join(backupFolder, name));
        }
        backedUp = true;
        preOpBefore = fs.existsSync(preOpFolder) ? fs.readdirSync(preOpFolder) : [];

        // 0. The fixture as pinned: JE's Bookmarks on, B, its link and tab; no JE bookmarks yet, so the gate is open.
        const start = await surfaces('0 fixture');
        check('0. the fixture is in place (JE Bookmarks on, B, the Plugin Pages link, the Custom Tab), no JE bookmarks: the checklist may show',
            start.je.bookmarksEnabled && start.je.bookmarkCurrentTimeKeys.join() === 'B' && start.pluginPagesLink && start.customTab && start.gate.users === 0 && start.gate.ready === true, start);

        // 1. The fixture's videos in the sandbox.
        const library = await mapLibrary();
        evidence.mapping = Object.fromEntries(Object.values(library.map).map((m) => [m.key, m.id]));
        check('1. every video of the fixture is in the sandbox library (16: films by name and year, episodes by series and numbers)', library.missing.length === 0 && Object.keys(library.map).length === 16, { missing: library.missing });
        const id = (key) => Object.values(library.map).find((m) => m.key === key).id;
        const runtime = (key) => Object.values(library.map).find((m) => m.key === key).runtime;
        check('1. their TMDb ids as the folder names give them: Tagged Movie 900001, both Same Id Remakes 900201',
            library.map[Object.keys(library.map).find((k) => library.map[k].key === 'tagged')].tmdb === '900001'
                && ['sameid1999', 'sameid2005'].every((k) => Object.values(library.map).find((m) => m.key === k).tmdb === '900201'), evidence.mapping);

        // 2. viewer's own "Bank job" at 12.4 s on Chaptered Movie, which one of viewer's JE bookmarks duplicates.
        const native = await call('viewer', 'POST', '/Bookmarks', { body: { ItemId: id('chaptered'), StartTicks: 124000000, Name: 'Bank job' } });
        check('2. viewer has a bookmark of their own: "Bank job" at 0:12 on Chaptered Movie', native.status === 201, native.status);
        const before = { admin: (await query('admin', {})).TotalRecordCount, viewer: (await query('viewer', {})).TotalRecordCount };

        // 3. The JE files: the fixture's ids made the sandbox's.
        written = writeJeFiles(library.map);
        const waiting = await surfaces('3 JE bookmarks written');
        check('3. three JE users with bookmarks wait for the import: the gate closed (admin, viewer and the folder of no user)',
            waiting.gate.users === 3 && waiting.gate.ready === false && waiting.gate.pending.length === 3 && waiting.gate.pending.includes('admin') && waiting.gate.pending.includes('viewer') && waiting.gate.pending.includes(FIXTURE_NOBODY), waiting.gate);
        const adminCaps = (await call('admin', 'GET', '/Bookmarks/Capabilities')).json || {};
        const viewerCaps = (await call('viewer', 'GET', '/Bookmarks/Capabilities')).json || {};
        check('3. the capabilities point the admin at the import, never the viewer', adminCaps.JellyfinEnhancedImportPending === true && viewerCaps.JellyfinEnhancedImportPending === false, { admin: adminCaps.JellyfinEnhancedImportPending, viewer: viewerCaps.JellyfinEnhancedImportPending });
        webcheck('je-settings.json', 'settings-gate-closed');

        // 4. The API's dry run (no query, no body: a dry run): the expected numbers, nothing written.
        const fileBefore = storeBytes();
        const dry = await importJe(null, undefined);
        evidence.dryRun = { users: dry.Users.map((u) => ({ jeUserId: u.JeUserId, userName: u.UserName, ...counts(u), errors: u.Errors })), samples: dry.Samples.length, more: dry.MoreEntries };
        check('4. the dry run is the default and reports per user what the fixture holds (admin 70 found, 64 to import: 57 by id, 3 by TMDb or TVDb, 1 of two copies, 3 not in the library; 2 duplicates, 4 skipped, 1 moved off the end)',
            dry.DryRun === true && JSON.stringify(counts(rowOf(dry, guid(users.admin.id)))) === JSON.stringify(EXPECTED.admin), evidence.dryRun);
        check('4. viewer: 9 found, 8 to import (1 not in the library), 1 duplicate of their own "Bank job"; the folder of no user: 2 skipped, with the error',
            JSON.stringify(counts(rowOf(dry, guid(users.viewer.id)))) === JSON.stringify(EXPECTED.viewer) && JSON.stringify(counts(rowOf(dry, FIXTURE_NOBODY))) === JSON.stringify(EXPECTED.nobody)
                && /^No user on this server has the id 00000000000000000000000000000083/.test((rowOf(dry, FIXTURE_NOBODY).Errors || [])[0] || ''), evidence.dryRun);
        check('4. 50 samples, the unusual outcomes first, 31 more; nothing written', dry.Samples.length === 50 && dry.MoreEntries === 31 && dry.Samples[0].Outcome === 'Skipped' && storeBytes() === fileBefore && (await surfaces('4 after the dry run')).gate.runs === 0, { samples: dry.Samples.slice(0, 18).map((s) => s.Outcome) });

        // 4b. Viewer's file under the D spelling too (JE 12.8+ renames other spellings to the N form
        //     at its start, but leaves one when the N folder is there): viewer is still one user, read once. A dry run only;
        //     the copy goes again before anything else.
        const dashedFolder = path.join(jeDataFolder, dashed(guid(users.viewer.id)));
        assertInside(dashedFolder, jeDataFolder);
        if (fs.existsSync(dashedFolder)) throw new Error(`${dashedFolder} is there already; the probe writes only its own JE files`);
        const dashedCopy = { file: path.join(dashedFolder, 'bookmark.json'), folder: dashedFolder, created: true };
        written.push(dashedCopy);
        fs.mkdirSync(dashedFolder);
        fs.copyFileSync(written.find((w) => w.folder.endsWith(guid(users.viewer.id))).file, dashedCopy.file);
        const twice = await surfaces('4b viewer under two folder spellings');
        const dryTwice = await importJe(true, undefined);
        removeJeFiles([dashedCopy]);
        written = written.filter((w) => w !== dashedCopy);
        evidence.twoSpellings = { gate: twice.gate, rows: dryTwice.Users.map((u) => ({ jeUserId: u.JeUserId, ...counts(u) })), removed: !fs.existsSync(dashedFolder) };
        check('4b. a user whose JE folder is there under two spellings (N and D) is one user, read once: three users, viewer pending once, viewer\'s row once with the same numbers, nothing written',
            twice.gate.users === 3 && twice.gate.pending.filter((p) => p === 'viewer').length === 1 && dryTwice.Users.length === 3
                && dryTwice.Users.filter((u) => u.JeUserId === guid(users.viewer.id)).length === 1
                && JSON.stringify(counts(rowOf(dryTwice, guid(users.viewer.id)))) === JSON.stringify(EXPECTED.viewer)
                && storeBytes() === fileBefore && evidence.twoSpellings.removed, evidence.twoSpellings);

        // 5. The settings page: Dry run, then Import (the apply this run checks below).
        if (opts.webchecks) {
            webcheck('je-import-ui.json', 'import-ui');
        } else {
            await importJe(false, '{}');
        }

        // 6. The bookmarks as the API lists them.
        const after = { admin: (await query('admin', {})).TotalRecordCount, viewer: (await query('viewer', {})).TotalRecordCount };
        check('6. the import added 64 bookmarks for admin and 8 for viewer', after.admin === before.admin + 64 && after.viewer === before.viewer + 8, { before, after });
        const one = async (who, term) => (await query(who, { searchTerm: term })).Items;
        const pastEnd = await one('admin', 'Past the end');
        const tagged = await one('admin', 'Tagged');
        const coldOpen = await one('admin', 'Cold open');
        const remake = await one('admin', 'Which remake');
        const gone = await one('admin', 'Gone film');
        const bankJob = await one('viewer', 'Bank job');
        evidence.records = { pastEnd, tagged: tagged.map((b) => [b.Name, b.ItemId]), coldOpen: coldOpen.map((b) => [b.ItemId, b.ItemName]), remake: remake.map((b) => b.ItemId), gone, bankJob: bankJob.map((b) => [b.Origin, b.StartTicks]) };
        evidence.records.longRuntime = runtime('long');
        check('6. names and positions kept, from Jellyfin Enhanced: the bookmark past the end of Long Test Movie (4:10) a second before its end, the two TMDb-only ones on Tagged Movie, the episode by its series TVDb id and numbers on S01E02, the one of two copies on a Same Id Remake',
            pastEnd.length === 1 && runtime('long') > 170 * TICKS && pastEnd[0].StartTicks === runtime('long') - TICKS && guid(pastEnd[0].ItemId) === id('long') && pastEnd[0].Origin === 'JellyfinEnhanced'
                && tagged.length === 2 && tagged.every((b) => guid(b.ItemId) === id('tagged') && !b.Detached)
                && coldOpen.length === 1 && guid(coldOpen[0].ItemId) === id('s01e02')
                && remake.length === 1 && [id('sameid1999'), id('sameid2005')].includes(guid(remake[0].ItemId)), evidence.records);
        check('6. a JE bookmark of a film not in the library is kept as Not in library; viewer\'s "Bank job" is only their own',
            gone.length === 1 && gone[0].Detached === true && gone[0].Origin === 'JellyfinEnhanced' && gone[0].ItemName === 'A Film Not Here (1999)' && bankJob.length === 1 && bankJob[0].Origin === 'Native', evidence.records);
        const imported = await surfaces('6 imported');
        check('6. the gate open: every JE user covered by the applied import, three runs, 72 imported', imported.gate.ready === true && imported.gate.pending.length === 0 && imported.gate.runs === 3 && imported.gate.imported === 72, imported.gate);
        check('6. each of the three runs names the admin who applied it (AppliedBy)', imported.gate.appliedBy.length === 3 && imported.gate.appliedBy.every((n) => n === 'admin'), imported.gate);
        check('6. the admin\'s hint gone', ((await call('admin', 'GET', '/Bookmarks/Capabilities')).json || {}).JellyfinEnhancedImportPending === false);

        // 7. Again: nothing new, nothing written (the file's bytes), no snapshot.
        const fileAfterImport = storeBytes();
        const preOpAfterImport = fs.existsSync(preOpFolder) ? fs.readdirSync(preOpFolder).length : 0;
        const again = await importJe(false, '{}');
        evidence.again = again.Users.map((u) => ({ jeUserId: u.JeUserId, ...counts(u) }));
        check('7. running the import again is a no-op: every entry already imported or still skipped, the file byte for byte, no snapshot',
            again.Users.every((u) => u.Import === 0 && u.Updated === 0) && rowOf(again, guid(users.admin.id)).AlreadyImported === 66 && rowOf(again, guid(users.viewer.id)).AlreadyImported === 9
                && rowOf(again, FIXTURE_NOBODY).Skipped === 2 && !again.Snapshot && storeBytes() === fileAfterImport && (fs.existsSync(preOpFolder) ? fs.readdirSync(preOpFolder).length : 0) === preOpAfterImport, evidence.again);

        // 8. JE entries changed later: one not changed here is rewritten; one renamed here keeps the local name.
        const adminFile = written.find((w) => w.folder.endsWith(guid(users.admin.id))).file;
        const je = readJson(adminFile);
        const keyOf = (label) => Object.keys(je.Bookmarks).find((k) => je.Bookmarks[k].Label === label);
        const editedKey = keyOf('Opening shot');
        const renamedKey = keyOf('The reveal');
        const named = async (name) => (await one('admin', name)).filter((b) => b.Name === name);
        const renamed = (await named('The reveal'))[0];
        const rename = await call('admin', 'POST', `/Bookmarks/${renamed.Id}/Name`, { body: { Name: 'The reveal, renamed here', IfUpdatedAt: renamed.UpdatedAt } });
        je.Bookmarks[editedKey] = { ...je.Bookmarks[editedKey], Label: 'Opening shot, edited in JE', Timestamp: je.Bookmarks[editedKey].Timestamp + 1, UpdatedAt: '2026-10-01T10:00:00.000Z' };
        je.Bookmarks[renamedKey] = { ...je.Bookmarks[renamedKey], Label: 'The reveal, edited in JE', UpdatedAt: '2026-10-01T10:00:00.000Z' };
        fs.writeFileSync(adminFile, JSON.stringify(je, null, 2));
        await delay(700);
        const update = await importJe(false, '{}');
        const updatedRow = rowOf(update, guid(users.admin.id));
        const edited = (await one('admin', 'Opening shot')).filter((b) => b.Id !== undefined && /^Opening shot(, edited in JE)?$/.test(b.Name));
        const local = (await one('admin', 'The reveal')).filter((b) => b.Id === renamed.Id);
        evidence.update = { row: counts(updatedRow), edited: edited.map((b) => [b.Name, b.StartTicks]), local: local.map((b) => b.Name), samples: update.Samples.filter((s) => s.Outcome === 'Updated' || /wins/.test(s.Reason)).map((s) => [s.Outcome, s.Reason]) };
        check('8. a JE entry changed later is rewritten while ours is unchanged (Updated 1); one renamed here keeps the local name (the kit\'s JE import rules)',
            rename.status === 200 && updatedRow.Updated === 1 && updatedRow.Import === 0 && edited.length === 1 && edited[0].Name === 'Opening shot, edited in JE' && local.length === 1 && local[0].Name === 'The reveal, renamed here', evidence.update);

        // 9. The switch-off checklist, in its order: JE's four toggles off (then saved), the Custom Tab, the restart for the
        //    link, and last the shortcut: a cleared key comes back as B at JE's next start; Disabled lasts.
        await patchJe({ BookmarksEnabled: false, BookmarksUsePluginPages: false, BookmarksUseCustomTabs: false, BookmarksAutoCreateCustomTab: false });
        const toggled = await surfaces('9 JE Bookmarks toggles off');
        check('9a. the four toggles tick and the banner goes; JE\'s public config says Bookmarks off; its B stays',
            ['BookmarksEnabled', 'BookmarksUsePluginPages', 'BookmarksUseCustomTabs', 'BookmarksAutoCreateCustomTab'].every((k) => item(toggled, k).done) && toggled.ours.banner === null
                && toggled.je.publicBookmarksEnabled === false && item(toggled, 'ShortcutAdmin').done === false, toggled);
        await setPluginConfig(CUSTOM_TABS_ID, { ...original.tabs, Tabs: (original.tabs.Tabs || []).filter((t) => String(t.ContentHtml || '').trim() !== JE_TAB_HTML) });
        const tabGone = await surfaces('9 Custom Tab deleted');
        check('9b. the Custom Tab item ticks', item(tabGone, 'CustomTab').done === true && tabGone.customTab === false, tabGone);
        await restart('9c restart for the link');
        let linkGone = await surfaces('9c restart 1', { restarted: true });
        if (linkGone.pluginPagesLink) {
            await restart('9c second restart (the link survived one)');
            linkGone = await surfaces('9c restart 2', { restarted: true });
        }
        check('9c. after the restart (or two) JE\'s Plugin Pages link is gone; the import stays done', linkGone.pluginPagesLink === false && item(linkGone, 'PluginPagesLink').done === true && linkGone.gate.ready === true, linkGone);
        await setJeShortcut('BookmarkCurrentTime', '');
        const cleared = await surfaces('9d BookmarkCurrentTime cleared');
        check('9d. last, the shortcut: cleared, it ticks only with the restart caveat, and Use B is withheld',
            item(cleared, 'ShortcutAdmin').done === true && item(cleared, 'ShortcutAdmin').needsRestart === true && cleared.ours.canUseB === false && /isn't offered yet/.test(cleared.ours.useBNote || ''), cleared);
        webcheck('je-settings.json', 'settings-cleared-key');
        await restart('9e restart: the cleared key');
        const back = await surfaces('9e restart', { restarted: true });
        check('9e. the cleared key came back as B at JE\'s next start: the shortcut item pending again',
            back.je.bookmarkCurrentTimeKeys.join() === 'B' && item(back, 'ShortcutAdmin').done === false && back.ours.canUseB === false, back);
        await setJeShortcut('BookmarkCurrentTime', 'Disabled');
        const disabled = await surfaces('9f BookmarkCurrentTime = Disabled');
        check('9f. set to Disabled: ticked for good, the checklist complete, Use B offered', item(disabled, 'ShortcutAdmin').done === true && item(disabled, 'ShortcutAdmin').needsRestart === false && disabled.ours.complete === true && disabled.ours.canUseB === true, disabled);
        webcheck('je-settings.json', 'settings-complete');
        await restart('9g restart: Disabled');
        const kept = await surfaces('9g restart', { restarted: true });
        check('9g. Disabled survives the restart; JE\'s Bookmarks stays off; the checklist stays complete', kept.je.bookmarkCurrentTimeKeys.join() === 'Disabled' && kept.je.bookmarksEnabled === false && kept.ours.complete === true, kept);

        // 10. Our page shows the imported bookmarks.
        webcheck('je-import-page.json', 'page');
        const listed = await query('admin', {});
        check('10. the admin\'s 64 imported bookmarks are still there after the restarts (names kept)', listed.Items.filter((b) => b.Origin === 'JellyfinEnhanced').length === 64 && listed.Items.some((b) => b.Name === 'Opening shot, edited in JE'), listed.TotalRecordCount);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        // The fixture back: the JE files gone, JE's, Custom Tabs' and Bookmarks' settings as they were; then with the sandbox
        // stopped Bookmarks' data file as it was and the run's snapshots deleted; a start (and one more when Plugin Pages
        // needs it), asserted by Initialize-Sandbox.ps1.
        try {
            removeJeFiles(written);
            if (original.je) await setPluginConfig(JE_ID, original.je);
            if (original.tabs) await setPluginConfig(CUSTOM_TABS_ID, original.tabs);
            if (original.bookmarks) await setPluginConfig(BOOKMARKS_ID, original.bookmarks);
            const stopped = stopSandbox();
            if (backedUp) {
                for (const name of ['bookmarks.json', 'bookmarks.json.bak']) {
                    const from = path.join(backupFolder, name);
                    const to = path.join(bookmarksFolder, name);
                    assertInside(to, sandboxRoot);
                    if (fs.existsSync(from)) fs.copyFileSync(from, to);
                    else if (fs.existsSync(to)) fs.rmSync(to);
                }
            }
            const made = fs.existsSync(preOpFolder) ? fs.readdirSync(preOpFolder).filter((f) => !preOpBefore.includes(f) && /^bookmarks-import-/.test(f)) : [];
            for (const f of made) fs.rmSync(path.join(preOpFolder, f));
            evidence.cleanup = { snapshotsDeleted: made.length, storeRestored: backedUp };
            await startSandbox('clean-up 1', stopped);
            let restored = await surfaces('clean-up restart 1', { restarted: true });
            if (!restored.pluginPagesLink) {
                await restart('clean-up 2 (the link needs Plugin Pages to read the entry JE re-added)');
                restored = await surfaces('clean-up restart 2', { restarted: true });
            }
            runScript('Initialize-Sandbox.ps1', ['-Name', opts.sandbox]);
            // The data file as it was: the same records, no import ledger or runs (the server may have written it again since
            // it started, a backfill say, so the records are compared, not the bytes).
            // A server that never had Bookmarks (as in a first install's rehearsal) has no data file until its first write: then there
            // must still be none, or one without records.
            const ids = (file) => (fs.existsSync(file) ? readJson(file).Bookmarks || [] : []).map((b) => b.Id).sort().join();
            const now = fs.existsSync(storeFile) ? readJson(storeFile) : {};
            check('clean-up: the fixture back (Initialize-Sandbox.ps1 asserts it), no JE bookmarks, the data file as it was',
                restored.pluginPagesLink && restored.customTab && restored.je.bookmarkCurrentTimeKeys.join() === 'B' && restored.gate.users === 0
                    && (!backedUp || (ids(storeFile) === ids(path.join(backupFolder, 'bookmarks.json')) && Object.keys(now.ImportLedger || {}).length === 0 && !now.ImportRuns)), restored);
        } catch (error) {
            check('clean-up: the fixture restored', false, String(error && error.stack || error));
        }
        for (const f of fs.existsSync(workRoot) ? fs.readdirSync(workRoot).filter((name) => name.startsWith('script-output-')) : []) {
            try { fs.rmSync(path.join(workRoot, f)); } catch { /* still open in the server it started; left for the next run */ }
        }
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
