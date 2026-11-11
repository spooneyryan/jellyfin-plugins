#!/usr/bin/env node
// Bookmarks' settings page and its admin tools in a running sandbox (the settings round-trip, and the help texts).
// Node 22 built-ins only. Loopback only, never port 8096. The sandbox users' tokens
// come from credentials.json and are never printed. It changes Bookmarks' settings in the sandbox through the sandbox's
// API (and the settings page) and puts every one of them back in a finally; it makes one bookmark of its own (note
// "settings-probe"), deleted again with its clip; its import imports the sandbox's own export (nothing new) and its
// restores restore the snapshot that import took and, in step 4, a daily snapshot of a few seconds before followed by the
// snapshot that restore took (the data as it was each time); it reads the sandbox's snapshot files and writes files only
// under .sandbox/<name>/bookmarks-settings-probe (its results and the webchecks').
//
//   node scripts/bookmarks-settings-probe.mjs --sandbox default [--movie "Long Test Movie"] [--skip-webcheck] [--timeout 180] [--out result.json]
//
// The run, as admin unless noted:
//   1. Diagnostics' BookmarkCounts: a row per user with bookmarks, each user's count what that user's own GET
//      /Bookmarks/Query says (admin, viewer, viewer2), the totals the rows' sums, the file's size, counts only (no
//      bookmark's name or note in it).
//   2. GET /Bookmarks/Admin/Shortcuts/Check: shift+b is Shift+B and free, m is jellyfin-web's, B is Jellyfin Enhanced's
//      (as configured in the je sandbox, its default elsewhere), "Shift+" isn't a combination; viewer gets 403.
//   3. Export and import: GET /Bookmarks/Admin/Export is the kit's envelope (an attachment, not cached); its dry-run import
//      adds and updates nothing; viewer gets 403 for both.
//   4. Clips being made: a long Accurate clip (VideoPreset slow) of the probe's bookmark is running; an import that writes
//      and a restore are 409 "Clips are being made right now…", the snapshots' list says so, and the refused import took
//      no snapshot. Then "Run the daily maintenance now" while the clip runs takes a daily snapshot that
//      holds the job; the clip cancelled, that snapshot restored: 200, the job ended as Failed with the restore's sentence,
//      nothing counted as being made, a new clip request runs (cancelled again); the restore's own snapshot restored (200:
//      not refused, the data as it was).
//   5. In the je sandbox while JE's Bookmarks is on: ShowJellyfinEnhancedBanner off leaves Diagnostics' banner out
//      (BannerHidden), on brings it back.
//   6. The settings page (webcheck settings-roundtrip.json): the tabs, every setting shown, set, saved, shown again after
//      a reload, the shortcut's check under its field, the Clip folder help; the settings put back.
//   7. The Data and Diagnostics tabs (webcheck settings-data.json): the snapshots, Export, the export imported back (dry
//      run, Import after the confirmation, the snapshot), Restore of that snapshot, the maintenance run now, the counts;
//      and on a phone (webcheck settings-phone.json): each tab no wider than 375 px, every table in a box
//      of its own inside the screen.
//   8. The log of the run has no Error from Bookmarks; the settings are as they were.
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const TICKS = 10_000_000;
const NOTE = 'settings-probe';
const CLIPS_BEING_MADE = 'Clips are being made right now.';

function usage() {
    return 'usage: node scripts/bookmarks-settings-probe.mjs --sandbox <name> [--movie <name>] [--skip-webcheck] [--timeout <s>] [--out <file>]';
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
const workRoot = path.join(sandboxRoot, 'bookmarks-settings-probe');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer', 'viewer2']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}
const bookmarksGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8')) || [])[1];
if (!bookmarksGuid) throw new Error('no guid in plugins/Bookmarks/build.yaml');
for (const p of [workRoot]) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), p);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(p)) throw new Error(`refusing to work in ${p}`);
}

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

async function call(who, method, p, { body, query, raw } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json', Authorization: `MediaBrowser Client="settings-probe", Device="sandbox", DeviceId="settings-probe", Version="1.0.0", Token="${users[who].token}"` };
    let payload;
    if (raw !== undefined) { payload = raw; h['Content-Type'] = 'application/json'; }
    else if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(60000) });
    const text = await res.text();
    const type = res.headers.get('content-type') || '';
    let json = null;
    if (text && /json/i.test(type)) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, type, json, text, headers: res.headers };
}

const problem = (r, status) => r.status === status && /application\/problem\+json/i.test(r.type) && r.json && typeof r.json.detail === 'string';

async function getConfig() {
    const r = await call('admin', 'GET', `/Plugins/${bookmarksGuid}/Configuration`);
    if (r.status !== 200 || !r.json) throw new Error(`GET configuration: ${r.status}`);
    return r.json;
}

async function setConfig(value) {
    const r = await call('admin', 'POST', `/Plugins/${bookmarksGuid}/Configuration`, { body: value });
    if (r.status !== 204) throw new Error(`POST configuration: ${r.status}`);
}

async function diagnostics() {
    const r = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    if (r.status !== 200 || !r.json) throw new Error(`GET /Bookmarks/Admin/Diagnostics: ${r.status}`);
    return r.json;
}

async function findMovie() {
    const r = await call('admin', 'GET', '/Items', { query: { userId: users.admin.id, IncludeItemTypes: 'Movie', Recursive: 'true', SearchTerm: opts.movie, Limit: 5 } });
    const item = r.json && r.json.Items && r.json.Items.find((i) => i.Name === opts.movie || i.Name.startsWith(`${opts.movie} (`));
    if (!item) throw new Error(`the sandbox has no movie named ${opts.movie}; run Initialize-Sandbox.ps1`);
    return { id: item.Id, name: item.Name, runTimeTicks: item.RunTimeTicks };
}

const created = [];
async function waitFor(label, condition, seconds = opts.timeout) {
    const until = Date.now() + seconds * 1000;
    for (;;) {
        const value = await condition();
        if (value) return value;
        if (Date.now() > until) throw new Error(`${label}: timed out after ${seconds} s`);
        await delay(200);
    }
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

// The clip status a snapshot of the sandbox's bookmark data holds for a bookmark (read only), or null.
function jobInSnapshot(name, bookmarkId) {
    if (!/^bookmarks-[a-z0-9-]+\.json$/i.test(name)) return null;
    const file = path.join(state.DataDir || path.join(sandboxRoot, 'data'), 'data', 'bookmarks', 'snapshots', name);
    const rel = path.relative(path.join(repoRoot, '.sandbox'), file);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel)) throw new Error(`refusing to read ${file}`);
    const id = String(bookmarkId).replace(/-/g, '').toLowerCase();
    const record = (readJson(file).Bookmarks || []).find((b) => b && String(b.Id).replace(/-/g, '').toLowerCase() === id);
    return record && record.Clip ? record.Clip.Status : null;
}

// A webcheck of scripts/webchecks (`<name>.json`); its result and screenshots are kept in the work folder.
function webcheck(name) {
    const out = path.join(workRoot, `webcheck-${name}.json`);
    const started = Date.now();
    const w = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', `${name}.json`, '--out', out], {
        cwd: repoRoot, encoding: 'utf8', timeout: 600000, windowsHide: true
    });
    let result = null;
    try { result = readJson(out); } catch { result = null; }
    for (const shot of (result && result.screenshots) || []) {
        const file = typeof shot === 'string' ? shot : shot && shot.file;
        if (file && fs.existsSync(file)) fs.copyFileSync(file, path.join(workRoot, path.basename(file)));
    }
    const failed = result && result.steps ? result.steps.find((s) => !s.ok) : null;
    return { ok: w.status === 0 && !!result && result.ok === true, exit: w.status, seconds: Math.round((Date.now() - started) / 1000), failed: failed && { name: failed.name, error: failed.error }, leftovers: result && result.leftovers };
}

async function run(context) {
    fs.mkdirSync(workRoot, { recursive: true });
    const logStart = serverLogSinceStart().length;
    context.saved = await getConfig();
    evidence.savedSettings = context.saved;
    const d0 = await diagnostics();
    evidence.pluginVersion = d0.PluginVersion;
    check('the plugin is 1.2.2.0', d0.PluginVersion === '1.2.2.0', d0.PluginVersion);

    // The probe's own bookmark, named and with a note: counted in step 1, its clip made in step 4. As long a range as the
    // movie has (the sandbox's is 3 minutes): an Accurate clip of it with the slow preset runs for seconds, long enough for
    // the requests of step 4.
    const movie = await findMovie();
    const runtime = Math.floor((movie.runTimeTicks || 0) / TICKS);
    const range = runtime > 20 ? [2, Math.min(runtime - 2, 590)] : [0, 10];
    const made = await call('admin', 'POST', '/Bookmarks', { body: { ItemId: movie.id, StartTicks: range[0] * TICKS, EndTicks: range[1] * TICKS, Name: `${NOTE} secret name`, Note: `${NOTE} secret note` } });
    if (made.status !== 201) throw new Error(`the probe's bookmark: ${made.status} ${made.text}`);
    created.push(made.json.Id);

    // 1. Each user's bookmarks counted, as that user's own list has them.
    const counts = (await diagnostics()).BookmarkCounts;
    const own = {};
    for (const who of ['admin', 'viewer', 'viewer2']) {
        const r = await call(who, 'GET', '/Bookmarks/Query', { query: { limit: 1 } });
        own[who] = r.json ? r.json.TotalRecordCount : null;
    }
    const rows = (counts && counts.Users) || [];
    const rowOf = (who) => rows.find((r) => r.UserId === users[who].id.replace(/-/g, '').toLowerCase()) || null;
    const sum = (key) => rows.reduce((n, r) => n + r[key], 0);
    evidence.counts = { total: counts && counts.Total, fileBytes: counts && counts.FileBytes, rows: rows.map((r) => ({ user: r.UserName, known: r.Known, bookmarks: r.Bookmarks, named: r.Named, clips: r.Clips })), own };
    check('1. Diagnostics BookmarkCounts: each user\'s count is what their own list says (users without bookmarks have no row)',
        !!counts && ['admin', 'viewer', 'viewer2'].every((who) => (rowOf(who) ? rowOf(who).Bookmarks : 0) === own[who] && (rowOf(who) === null || rowOf(who).UserName === who)), evidence.counts);
    check('1. the totals are the rows\' sums, with the users and the file\'s size',
        !!counts && counts.Total.Users === rows.length && counts.Total.Bookmarks === sum('Bookmarks') && counts.Total.Clips === sum('Clips') && counts.Total.ClipBytes === sum('ClipBytes') && counts.FileBytes > 0,
        counts && { total: counts.Total, fileBytes: counts.FileBytes });
    const names = [];
    for (const who of ['admin', 'viewer', 'viewer2']) {
        const r = await call(who, 'GET', '/Bookmarks/Query', { query: { limit: 200 } });
        for (const b of (r.json && r.json.Items) || []) {
            if (b.Name && b.Name.length > 3) names.push(b.Name);
            if (b.Note && b.Note.length > 3) names.push(b.Note);
        }
    }
    const countsText = JSON.stringify(counts);
    const userNames = rows.map((r) => r.UserName);
    const leaked = names.filter((n) => countsText.includes(JSON.stringify(n).slice(1, -1)) && !userNames.includes(n));
    check('1. counts only: no bookmark\'s name or note in the entry, the probe\'s own named one among them (the entry counts, never names)', leaked.length === 0 && names.includes(`${NOTE} secret name`), { names: names.length, leaked: leaked.slice(0, 3) });

    // 2. The shortcut check.
    const shortcut = async (combo, who = 'admin') => call(who, 'GET', '/Bookmarks/Admin/Shortcuts/Check', { query: { combo } });
    const shiftB = (await shortcut('shift+b')).json;
    const m = (await shortcut('m')).json;
    const b = (await shortcut('B')).json;
    const invalid = (await shortcut('Shift+')).json;
    const viewerCheck = await shortcut('B', 'viewer');
    const je = d0.JellyfinEnhanced || {};
    evidence.shortcutChecks = { shiftB, m: m && m.Warning, b: b && b.Warning, invalid: invalid && invalid.Warning, viewer: viewerCheck.status, jeInstalled: !!je.Installed, jeKey: je.Shortcuts && je.Shortcuts.BookmarkCurrentTime };
    check('2. Shortcuts/Check: shift+b is Shift+B and free, m is jellyfin-web\'s, "Shift+" isn\'t a combination; viewer gets 403',
        shiftB && shiftB.Combo === 'Shift+B' && shiftB.Valid === true && shiftB.Reserved === false
        && m && m.Reserved === true && m.Warning === "M is taken by jellyfin-web's video player, so Bookmark this moment won't work with it. Choose another key."
        && invalid && invalid.Valid === false && viewerCheck.status === 403, evidence.shortcutChecks);
    const jeTakesB = je.Installed && je.Shortcuts && je.Shortcuts.BookmarkCurrentTime === 'B';
    check('2. B is Jellyfin Enhanced\'s: taken while its Bookmark Current Time is B, its default where JE isn\'t installed',
        b && b.Reserved === true && (je.Installed ? (jeTakesB ? /^B is taken by Jellyfin Enhanced's Bookmark Current Time/.test(b.Warning) : true)
            : / by default, so Bookmark this moment would stop working if Jellyfin Enhanced is installed\.$/.test(b.Warning)), { warning: b && b.Warning, je: { installed: !!je.Installed, key: je.Shortcuts && je.Shortcuts.BookmarkCurrentTime } });

    // 3. Export and import.
    const exported = await call('admin', 'GET', '/Bookmarks/Admin/Export');
    let envelope = null;
    try { envelope = JSON.parse(exported.text); } catch { envelope = null; }
    const disposition = exported.headers.get('content-disposition') || '';
    evidence.export = { status: exported.status, disposition, cacheControl: exported.headers.get('cache-control'), bytes: exported.text.length,
        envelope: envelope && { format: envelope.Format, plugin: envelope.Plugin, version: envelope.PluginVersion, schema: envelope.SchemaVersion, users: envelope.Users.length, records: envelope.Data.Bookmarks.length, archive: envelope.Archive.length } };
    check('3. Export: the kit\'s envelope of Bookmarks 1.2.2.0, schema 2, as an attachment that isn\'t cached',
        exported.status === 200 && /attachment/i.test(disposition) && /bookmarks-export-\d{8}-\d{6}\.json/.test(disposition) && /no-store/i.test(exported.headers.get('cache-control') || '')
        && envelope && envelope.Format === 'jellyfin-plugin-export/1' && envelope.Plugin === 'Bookmarks' && envelope.PluginVersion === '1.2.2.0' && envelope.SchemaVersion === 2, evidence.export);
    const dry = await call('admin', 'POST', '/Bookmarks/Admin/Import', { raw: exported.text });
    evidence.importDryRun = dry.json && { records: dry.json.Records, users: dry.json.Users, clipsBeingMade: dry.json.ClipsBeingMade, notes: dry.json.Notes };
    check('3. its import, a dry run by default: nothing new, nothing written', dry.status === 200 && dry.json && dry.json.DryRun === true && dry.json.Records.Added === 0
        && dry.json.Records.Updated === 0 && dry.json.Records.Removed === 0 && dry.json.SafetySnapshot === null, evidence.importDryRun);
    const viewerExport = await call('viewer', 'GET', '/Bookmarks/Admin/Export');
    const viewerImport = await call('viewer', 'POST', '/Bookmarks/Admin/Import', { raw: exported.text });
    check('3. viewer gets 403 for both', viewerExport.status === 403 && viewerImport.status === 403, { exportStatus: viewerExport.status, importStatus: viewerImport.status });

    // 4. Clips being made: an import that writes and a restore are refused.
    await setConfig({ ...(await getConfig()), VideoPreset: 'slow' });
    const queued = await call('admin', 'POST', `/Bookmarks/${made.json.Id}/Clip`, { body: { Mode: 'Accurate' } });
    const running = await waitFor('the clip to run', async () => {
        const r = await call('admin', 'GET', `/Bookmarks/${made.json.Id}`);
        return r.json && r.json.Clip && r.json.Clip.Status === 'Running' ? r.json.Clip : null;
    }, 60).catch(() => null);
    const snapshotsBefore = await call('admin', 'GET', '/Bookmarks/Admin/Snapshots');
    const refusedImport = await call('admin', 'POST', '/Bookmarks/Admin/Import', { query: { dryRun: 'false' }, raw: exported.text });
    const listed = await call('admin', 'GET', '/Bookmarks/Admin/Snapshots');
    const anySnapshot = listed.json && listed.json.Snapshots && listed.json.Snapshots[0];
    const refusedRestore = await call('admin', 'POST', '/Bookmarks/Admin/Snapshots/Restore', { query: { name: anySnapshot ? anySnapshot.Name : 'bookmarks-20260101-000000.json' } });
    const dryMeanwhile = await call('admin', 'POST', '/Bookmarks/Admin/Import', { raw: exported.text });
    evidence.clipsBeingMade = { queued: queued.status, running: !!running, import: { status: refusedImport.status, detail: refusedImport.json && refusedImport.json.detail },
        restore: { status: refusedRestore.status, detail: refusedRestore.json && refusedRestore.json.detail, snapshot: anySnapshot && anySnapshot.Name },
        listedClipsBeingMade: listed.json && listed.json.ClipsBeingMade, dryRunClipsBeingMade: dryMeanwhile.json && dryMeanwhile.json.ClipsBeingMade };
    check('4. while a clip is being made, an import that writes is 409 "Clips are being made right now…"; its dry run says so',
        queued.status === 202 && !!running && problem(refusedImport, 409) && refusedImport.json.detail.startsWith(CLIPS_BEING_MADE) && dryMeanwhile.json && dryMeanwhile.json.ClipsBeingMade >= 1, evidence.clipsBeingMade);
    check('4. … and so is a restore (with a snapshot to restore when the sandbox has one), and the snapshots\' list says so',
        problem(refusedRestore, 409) && (!anySnapshot || refusedRestore.json.detail.startsWith(CLIPS_BEING_MADE)) && listed.json && listed.json.ClipsBeingMade >= 1, evidence.clipsBeingMade);

    // The refused import took no snapshot: one would hold the running job and rotate an older one out.
    const preOp = (r) => ((r.json && r.json.Snapshots) || []).filter((s) => s.Kind === 'PreOp').map((s) => s.Name);
    evidence.refusedImportSnapshots = { before: preOp(snapshotsBefore), after: preOp(listed) };
    check('4. the refused import took no snapshot: the pre-operation snapshots are as they were',
        snapshotsBefore.status === 200 && listed.status === 200 && JSON.stringify(preOp(listed)) === JSON.stringify(preOp(snapshotsBefore)), evidence.refusedImportSnapshots);

    // A snapshot that holds the job, restored after the clip was cancelled: the job is ended, so nothing
    // counts as being made, the owner can make the clip again, and the restore's own snapshot can be restored.
    const stopped = async () => waitFor('the clip to stop', async () => {
        const r = await call('admin', 'GET', `/Bookmarks/${made.json.Id}`);
        return r.json && r.json.Clip && !['Queued', 'Running'].includes(r.json.Clip.Status) ? r.json.Clip : null;
    }, 60);
    const known = ((listed.json && listed.json.Snapshots) || []).map((s) => s.Name);
    const runsBefore = (await diagnostics()).Maintenance.Runs;
    const maintenance = await call('admin', 'POST', '/Bookmarks/Admin/Maintenance/Run');
    const daily = await waitFor('the maintenance\'s snapshot', async () => {
        const r = await call('admin', 'GET', '/Bookmarks/Admin/Snapshots');
        return ((r.json && r.json.Snapshots) || []).find((s) => s.Kind === 'Daily' && !known.includes(s.Name)) || null;
    }, 90).catch(() => null);
    await waitFor('the maintenance to finish', async () => (await diagnostics()).Maintenance.Runs > runsBefore, 90).catch(() => null);
    const heldJob = daily ? jobInSnapshot(daily.Name, made.json.Id) : null;
    check('4. "Run the daily maintenance now" while the clip is made takes a daily snapshot that holds the job (Queued or Running)',
        maintenance.status === 202 && !!daily && ['Queued', 'Running'].includes(heldJob), { maintenance: maintenance.status, snapshot: daily && daily.Name, job: heldJob });
    await call('admin', 'POST', `/Bookmarks/${made.json.Id}/Clip/Cancel`);
    const cancelled = await stopped();
    const restoredDaily = daily ? await call('admin', 'POST', '/Bookmarks/Admin/Snapshots/Restore', { query: { name: daily.Name } }) : null;
    const ended = (await call('admin', 'GET', `/Bookmarks/${made.json.Id}`)).json;
    const listedAfter = (await call('admin', 'GET', '/Bookmarks/Admin/Snapshots')).json;
    const countsAfter = (await diagnostics()).BookmarkCounts;
    evidence.restoredJob = { cancelled: cancelled && cancelled.Status, restore: restoredDaily && { status: restoredDaily.status, clipJobsEnded: restoredDaily.json && restoredDaily.json.ClipJobsEnded, safety: restoredDaily.json && restoredDaily.json.SafetySnapshot },
        clip: ended && ended.Clip && { status: ended.Clip.Status, error: ended.Clip.Error }, snapshotsClipsBeingMade: listedAfter && listedAfter.ClipsBeingMade, countsClipsBeingMade: countsAfter && countsAfter.Total && countsAfter.Total.ClipsBeingMade };
    check('4. that snapshot restored after the clip was cancelled: 200, its job ended as Failed with the restore\'s sentence, nothing counted as being made',
        !!restoredDaily && restoredDaily.status === 200 && restoredDaily.json.ClipJobsEnded === 1 && !!ended && ended.Clip && ended.Clip.Status === 'Failed'
        && ended.Clip.Error === 'The bookmark data was restored from a snapshot taken while this clip was being made. Make it again.'
        && listedAfter && listedAfter.ClipsBeingMade === 0 && countsAfter && countsAfter.Total.ClipsBeingMade === 0, evidence.restoredJob);
    const again = await call('admin', 'POST', `/Bookmarks/${made.json.Id}/Clip`, { body: { Mode: 'Accurate' } });
    const runningAgain = await waitFor('the new clip to run', async () => {
        const r = await call('admin', 'GET', `/Bookmarks/${made.json.Id}`);
        return r.json && r.json.Clip && r.json.Clip.Status === 'Running' ? r.json.Clip : null;
    }, 60).catch(() => null);
    await call('admin', 'POST', `/Bookmarks/${made.json.Id}/Clip/Cancel`);
    const cancelledAgain = await stopped();
    evidence.restoredJob.again = { status: again.status, ran: !!runningAgain, then: cancelledAgain && cancelledAgain.Status };
    check('4. … the owner\'s new clip request is queued and runs, and Cancel stops it', again.status === 202 && !!runningAgain && !!cancelledAgain && cancelledAgain.Status === 'Cancelled', evidence.restoredJob.again);
    const safety = restoredDaily && restoredDaily.json && restoredDaily.json.SafetySnapshot;
    const undo = safety ? await call('admin', 'POST', '/Bookmarks/Admin/Snapshots/Restore', { query: { name: safety } }) : null;
    const undone = (await call('admin', 'GET', `/Bookmarks/${made.json.Id}`)).json;
    evidence.restoredJob.undo = { snapshot: safety, status: undo && undo.status, clipJobsEnded: undo && undo.json && undo.json.ClipJobsEnded, clip: undone && undone.Clip && undone.Clip.Status };
    check('4. … and the restore\'s own snapshot restores (200, not refused): the data as it was, the clip as it was cancelled',
        !!undo && undo.status === 200 && undo.json.ClipJobsEnded === 0 && !!undone && undone.Clip && !!cancelled && undone.Clip.Status === cancelled.Status, evidence.restoredJob.undo);

    await call('admin', 'DELETE', `/Bookmarks/${made.json.Id}`, { query: { deleteClip: 'true' } });
    created.splice(created.indexOf(made.json.Id), 1);
    await setConfig(context.saved);

    // 5. The Jellyfin Enhanced banner's setting (only where JE's Bookmarks is on).
    if (je.Installed && je.Overlap && je.Overlap.On) {
        await setConfig({ ...(await getConfig()), ShowJellyfinEnhancedBanner: false });
        const hidden = (await diagnostics()).JellyfinEnhanced.Overlap;
        await setConfig({ ...(await getConfig()), ShowJellyfinEnhancedBanner: true });
        const shown = (await diagnostics()).JellyfinEnhanced.Overlap;
        evidence.banner = { hidden: { banner: hidden.Banner, hiddenFlag: hidden.BannerHidden, checklist: hidden.Checklist.length }, shown: { banner: shown.Banner, hiddenFlag: shown.BannerHidden } };
        check('5. ShowJellyfinEnhancedBanner off leaves the banner out (BannerHidden), the checklist stays; on brings it back',
            hidden.Banner === null && hidden.BannerHidden === true && hidden.Checklist.length > 0 && typeof shown.Banner === 'string' && shown.BannerHidden === false, evidence.banner);
    } else {
        evidence.banner = 'Jellyfin Enhanced\'s Bookmarks is not on in this sandbox';
    }

    // 6, 7. The settings page.
    if (opts.webcheck) {
        const w1 = webcheck('settings-roundtrip');
        evidence.webcheckRoundtrip = w1;
        check('6. the settings page: the tabs, every setting shown, set, saved and shown again, the shortcut\'s check, the Clip folder help (webcheck settings-roundtrip.json)', w1.ok, w1);
        const back = await getConfig();
        check('6. the webcheck put the settings back', JSON.stringify(back) === JSON.stringify(context.saved), w1.ok ? null : { back });
        if (JSON.stringify(back) !== JSON.stringify(context.saved)) await setConfig(context.saved);
        const w2 = webcheck('settings-data');
        evidence.webcheckData = w2;
        check('7. the Data and Diagnostics tabs: snapshots, Export, the export imported back, Restore, the maintenance run, the counts (webcheck settings-data.json)', w2.ok, w2);
        const w3 = webcheck('settings-phone');
        evidence.webcheckPhone = w3;
        check('7. on a phone (375 px): each tab fits the screen, every table in a box of its own that scrolls (webcheck settings-phone.json)', w3.ok, w3);
    }

    // 8. The run's log.
    const runLog = serverLogSinceStart().slice(logStart);
    const errors = runLog.split(/\r?\n/).filter((l) => /\[ERR\]/.test(l) && /Bookmarks/.test(l));
    check('8. the log of the run has no Error from Bookmarks', errors.length === 0, errors.slice(0, 5));
}

async function main() {
    const context = { saved: null };
    let fatal = null;
    try {
        await run(context);
    } catch (error) {
        fatal = String(error && error.stack || error);
    } finally {
        for (const id of created.reverse()) {
            await call('admin', 'POST', `/Bookmarks/${id}/Clip/Cancel`).catch(() => null);
            await waitFor('the clip to stop', async () => {
                const r = await call('admin', 'GET', `/Bookmarks/${id}`).catch(() => null);
                return !r || !r.json || !r.json.Clip || !['Queued', 'Running'].includes(r.json.Clip.Status);
            }, 60).catch(() => null);
            await call('admin', 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } }).catch(() => null);
        }

        if (context.saved) {
            await setConfig(context.saved).catch((error) => check('clean-up: the settings put back', false, String(error)));
            const back = await getConfig().catch(() => null);
            check('clean-up: every setting as it was', !!back && JSON.stringify(back) === JSON.stringify(context.saved), back);
        }
    }
    const result = { pass: !fatal && checks.length > 0 && checks.every((c) => c.pass), fatal, checks, evidence };
    const text = JSON.stringify(result, null, 2);
    if (opts.out) fs.writeFileSync(opts.out, text + '\n');
    console.log(text);
    process.exit(result.pass ? 0 : 1);
}

await main();
