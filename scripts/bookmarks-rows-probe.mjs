#!/usr/bin/env node
// Bookmarks' editor, rows, details section and delete confirmation in a running sandbox, in the
// web client: the webchecks editor-rows.json (as admin) and rows-relinked.json (as viewer, on a bookmark this probe has the
// identity service relink for real), with the server's side of what they show: the capabilities, the clip folder and
// bookmarks.json (read only), and the server log. Node 22 built-ins only. Loopback only,
// never port 8096. The users' tokens come from credentials.json and are never printed.
//
//   node scripts/bookmarks-rows-probe.mjs --sandbox default [--timeout <seconds>] [--skip-relink] [--out result.json]
//
// It changes the shared sandbox media and puts it back as bookmarks-identity-probe.mjs does: one plain rename of a movie
// folder into its hold folder .sandbox/<name>/bookmarks-rows-probe/held, recorded in moves.json there before it is made,
// undone at the end, on failure, and at the start of the next run after a run was killed (then it scans once). It deletes
// the bookmarks and clip files it and its webchecks made; every name and note starts with "rows-probe". It never restarts
// or deploys anything.
//
// Checks:
//   1. the capabilities (admin): My Bookmarks (Pages.Bookmarks true) and My Clips (Pages.Clips true), so the section links See all and My Clips;
//   2. editor-rows.json (admin, Long Test Movie): "My bookmarks (n)", display titles, a name's note under it, Name it and the
//      editor with Name focused (Name, Start, End, Note textarea), Enter saving; the delete confirmation's "Also delete its
//      clip" unticked with the hint that the clip stays in My Clips (once My Clips is there), the clip
//      kept (a tombstone) when unticked and deleted when ticked; a confirmation opened without the box (no clip file yet)
//      keeping a clip that finished while it was open, as the box would have; the player's range flow
//      with Name focused and Save & make clip naming the file after the name;
//   3. the clip folder: the webcheck's named clip, kept clip and race clip gone from admin's clip folder after their deletes;
//   4. a real relink (unless --skip-relink): viewer's bookmark "rows-probe Moved" on Same Id Remake (1999); the 1999 copy
//      held out and scanned: the bookmark goes to the 2005 copy (Strong), put back and scanned: it stays there, relinked;
//   5. rows-relinked.json (viewer): the row's "Relinked, check position" badge and "Looks right", which clears the mark;
//      the API and bookmarks.json agree (no RelinkedUtc), the name and video unchanged;
//   6. no Error of Bookmarks or the kit in the server log, no line of theirs at Information or above naming a bookmark;
//      clean-up (no media change left, the bookmarks and clip files deleted).
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const TICKS = 10_000_000;
const PROBE = 'rows-probe';
const HELD_MOVIE = 'Same Id Remake (1999) [tmdbid-900201]';
const COPY_MOVIE = 'Same Id Remake (2005) [tmdbid-900201]';

function usage() {
    return 'usage: node scripts/bookmarks-rows-probe.mjs --sandbox <name> [--timeout <seconds>] [--skip-relink] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, timeout: 300, out: null, relink: true };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--skip-relink') o.relink = false;
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
const workRoot = path.join(sandboxRoot, 'bookmarks-rows-probe');
const heldRoot = path.join(workRoot, 'held');
const movesFile = path.join(workRoot, 'moves.json');
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
for (const name of ['admin', 'viewer']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error(`refusing to work in ${p}`);
    return full;
}
for (const p of [storeFile, clipRoot, workRoot]) assertInside(p, sandboxRoot);

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, scans: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// Jellyfin writes GUIDs without dashes ("N"); ASP.NET Core's JSON and bookmarks.json write them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="bookmarks-rows-probe", Device="sandbox", DeviceId="bookmarks-rows-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text };
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

// ------------------------------------------------------------------------------------------ media (undoable)

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

async function maintenanceTask() {
    const tasks = await call('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } });
    return (tasks.json || []).find((t) => t.Key === 'BookmarksMaintenance') || null;
}

// The daily maintenance's re-resolution, when the scan's own library change hasn't relinked the bookmark yet.
async function runMaintenance(label) {
    const task = await maintenanceTask();
    if (!task) throw new Error('no BookmarksMaintenance task');
    const started = Date.now();
    const r = await call('admin', 'POST', `/ScheduledTasks/Running/${task.Id}`);
    if (r.status !== 204) throw new Error(`POST /ScheduledTasks/Running: ${r.status}`);
    await delay(500);
    for (;;) {
        const now = await maintenanceTask();
        const last = now && now.LastExecutionResult;
        if (now && now.State === 'Idle' && last && Date.parse(last.EndTimeUtc) >= started - 1000) {
            evidence.scans.push({ label: `maintenance: ${label}`, seconds: Math.round((Date.now() - started) / 100) / 10, status: last.Status });
            if (last.Status !== 'Completed') throw new Error(`the maintenance "${label}" ended ${last.Status}`);
            return;
        }
        if (Date.now() - started > 120000) throw new Error(`the maintenance "${label}" did not finish`);
        await delay(500);
    }
}

async function items(types) {
    const r = await call('admin', 'GET', '/Items', {
        query: { userId: users.admin.id, Recursive: 'true', IncludeItemTypes: types, Fields: 'Path', EnableImages: 'false', EnableUserData: 'false' }
    });
    if (r.status !== 200) throw new Error(`GET /Items: ${r.status}`);
    return r.json.Items;
}

function inFolder(item, folder) {
    return typeof item.Path === 'string' && item.Path.split(/[\\/]/).includes(folder);
}

function one(list, predicate, what) {
    const found = list.filter(predicate);
    if (found.length !== 1) throw new Error(`expected one ${what}, found ${found.length}; run Initialize-Sandbox.ps1 on fresh media`);
    return found[0];
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

// The record as bookmarks.json holds it (read only).
function stored(id) {
    const doc = readJson(storeFile);
    return (doc.Bookmarks || []).find((b) => guid(b.Id) === guid(id)) || null;
}

function clipOnDisk(user, fileName) {
    return !!fileName && fs.existsSync(path.join(clipRoot, user, fileName));
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
        closed: result && result.closed,
        value
    };
}

// ------------------------------------------------------------------------------------------ the run

const made = [];

async function run() {
    // ---- 1. the capabilities of this build
    const capabilities = await call('admin', 'GET', '/Bookmarks/Capabilities');
    evidence.capabilities = capabilities.json && { Pages: capabilities.json.Pages, MaxNameLength: capabilities.json.MaxNameLength, MaxNoteLength: capabilities.json.MaxNoteLength };
    check('1. the capabilities offer My Bookmarks (Pages.Bookmarks true) and My Clips (Pages.Clips true), so the section links See all and My Clips',
        capabilities.status === 200 && capabilities.json.Pages && capabilities.json.Pages.Bookmarks === true && capabilities.json.Pages.Clips === true,
        evidence.capabilities);

    // ---- 2. and 3. the editor, rows, section and delete confirmation as admin
    const rows = webcheck('editor-rows');
    const namedClip = rows.value('the named clip completes, its file named after the name');
    const keptClip = rows.value('a Fast clip of the range, through the API');
    const raceClip = rows.value('while the confirmation is open, an Accurate clip of the range is made through the API and completes');
    evidence.editorRows = { ok: rows.ok, exit: rows.exit, seconds: rows.seconds, steps: rows.steps, failed: rows.failed, failedRequests: rows.failedRequests, closed: rows.closed,
        rows: rows.value('the section is \'My bookmarks (n)\' with the rows\' display titles'), namedClip, keptClip, raceClip,
        keptClipEvidence: rows.value('unticked, the clip was kept: the bookmark is 404, its clip file still downloads, then DELETE /Bookmarks/{id}/Clip removes it (204)'),
        raceEvidence: rows.value('without the box the box\'s default applies: the clip that finished meanwhile is kept (since My Clips; 1.1.0 deleted it), the bookmark is 404, its clip file still downloads, then DELETE /Bookmarks/{id}/Clip removes it (204)') };
    check('2. editor-rows.json (admin): "My bookmarks (n)" with See all and My Clips, display titles with a name\'s note under it and Name it, the editor with Name focused (Name, Start, End, Note textarea) saving on Enter, "Also delete its clip" unticked with the hint that the clip stays in My Clips, the clip kept when unticked and deleted when ticked, a clip that finished while a confirmation without the box was open kept as the box would have, the range flow with Name focused and the clip named after the name',
        rows.ok, { exit: rows.exit, failed: rows.failed });
    const namedFile = namedClip && typeof namedClip === 'object' ? namedClip.fileName : null;
    check('3. the clip folder: the named clip (Long Test Movie (<year>) - rows-probe Bank job [<range>]), the kept clip and the race\'s clip (rows-probe race, kept, then deleted by the webcheck) are gone from admin\'s clip folder after their deletes',
        !!namedFile && /^Long Test Movie \(\d{4}\) - rows-probe Bank job \[[\d.]+-[\d.]+\]\.(mp4|mkv)$/.test(namedFile) && !clipOnDisk('admin', namedFile)
        && typeof keptClip === 'string' && !clipOnDisk('admin', keptClip)
        && typeof raceClip === 'string' && /^Long Test Movie \(\d{4}\) - rows-probe race \[[\d.]+-[\d.]+\]\.mp4$/.test(raceClip) && !clipOnDisk('admin', raceClip),
        { namedFile, keptClip, raceClip });

    if (!opts.relink) return;

    // ---- 4. a real Strong relink
    const movies = await items('Movie');
    const moviesRoot = path.join(mediaRoot, 'Movies') + path.sep;
    const remake1999 = one(movies, (m) => inFolder(m, HELD_MOVIE) && m.Path.startsWith(moviesRoot), HELD_MOVIE);
    const remake2005 = one(movies, (m) => inFolder(m, COPY_MOVIE) && m.Path.startsWith(moviesRoot), COPY_MOVIE);
    const created = await call('viewer', 'POST', '/Bookmarks', {
        body: { ItemId: remake1999.Id, StartTicks: 5 * TICKS, EndTicks: 15 * TICKS, Name: `${PROBE} Moved`, Note: `${PROBE} relinked`, MediaSourceId: guid(remake1999.Id), AudioStreamIndex: 2 }
    });
    if (created.status !== 201) throw new Error(`POST /Bookmarks: ${created.status} ${created.text.slice(0, 200)}`);
    made.push({ who: 'viewer', id: created.json.Id });
    const bookmarkId = created.json.Id;
    const moves = loadMoves().length;
    move(path.join(mediaRoot, 'Movies', HELD_MOVIE), path.join(heldRoot, 'Movies', HELD_MOVIE));
    await scan('the 1999 copy held out');
    let moved = (await call('viewer', 'GET', `/Bookmarks/${bookmarkId}`)).json;
    if (moved.Detached) {
        await runMaintenance('re-resolution with the 1999 copy gone');
        moved = (await call('viewer', 'GET', `/Bookmarks/${bookmarkId}`)).json;
    }
    undoMoves(moves);
    await scan('the 1999 copy put back');
    const relinked = (await call('viewer', 'GET', `/Bookmarks/${bookmarkId}`)).json;
    const relinkedStored = stored(bookmarkId);
    evidence.relink = {
        heldOut: { ItemId: guid(moved.ItemId), Detached: moved.Detached, Relinked: moved.Relinked },
        putBack: { ItemId: guid(relinked.ItemId), Detached: relinked.Detached, Relinked: relinked.Relinked, MediaSourceId: relinked.MediaSourceId ?? null },
        stored: relinkedStored && { RelinkStrength: relinkedStored.RelinkStrength, RelinkedUtc: relinkedStored.RelinkedUtc || null }
    };
    check('4. the 1999 copy held out: viewer\'s bookmark goes to the 2005 copy (Strong, media source cleared); put back: it stays there, relinked',
        guid(moved.ItemId) === guid(remake2005.Id) && moved.Relinked === true && guid(relinked.ItemId) === guid(remake2005.Id) && relinked.Relinked === true
        && !relinked.Detached && relinked.MediaSourceId == null && relinkedStored && relinkedStored.RelinkStrength === 'Strong' && !!relinkedStored.RelinkedUtc,
        evidence.relink);

    // ---- 5. the badge and Looks right in the web client
    const badge = webcheck('rows-relinked');
    const after = (await call('viewer', 'GET', `/Bookmarks/${bookmarkId}`)).json;
    const afterStored = stored(bookmarkId);
    evidence.rowsRelinked = { ok: badge.ok, exit: badge.exit, seconds: badge.seconds, steps: badge.steps, failed: badge.failed, failedRequests: badge.failedRequests, closed: badge.closed,
        badge: badge.value('the badge\'s colour (the kit\'s warning colour) and tooltip'),
        after: after && { ItemId: guid(after.ItemId), Relinked: after.Relinked, Name: after.Name }, storedRelinkedUtc: afterStored ? afterStored.RelinkedUtc || null : 'gone' };
    check('5. rows-relinked.json (viewer): the row shows "Relinked, check position" with Looks right, which clears the mark (API and bookmarks.json), the name and video unchanged',
        badge.ok && after && after.Relinked === false && after.Name === `${PROBE} Moved` && guid(after.ItemId) === guid(remake2005.Id) && afterStored && !afterStored.RelinkedUtc,
        { exit: badge.exit, failed: badge.failed, after: evidence.rowsRelinked.after, storedRelinkedUtc: evidence.rowsRelinked.storedRelinkedUtc });
}

// The admin's bookmarks a webcheck left behind (a failed run), with their clips.
async function deleteLeftovers() {
    const left = [];
    for (const who of ['admin', 'viewer']) {
        const r = await call(who, 'GET', '/Bookmarks/Query', { query: { searchTerm: PROBE, limit: 200 } });
        for (const b of (r.json && r.json.Items) || []) {
            if ((b.Name || '').startsWith(PROBE) || (b.Note || '').startsWith(PROBE)) {
                await call(who, 'DELETE', `/Bookmarks/${b.Id}`, { query: { deleteClip: 'true' } });
                left.push(b.Id);
            }
        }
    }
    return left;
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const startLog = logPosition();
    if (loadMoves().length > 0) {
        undoMoves(0);
        await scan('left-over changes of an earlier run undone');
        evidence.undidLeftovers = true;
    }
    let failure = null;
    try {
        await run();
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        try {
            const changed = loadMoves().length;
            undoMoves(0);
            if (changed > 0) await scan('clean-up: media put back');
            for (const { who, id } of made) {
                await call(who, 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } });
                await call(who, 'DELETE', `/Bookmarks/${id}/Clip`);
            }
            evidence.leftoversDeleted = await deleteLeftovers();
        } catch (error) {
            check('clean-up: media and the bookmarks put back', false, String(error));
        }
        const text = logSince(startLog);
        const lines = text.split('\n');
        const errors = lines.filter((l) => /\[ERR\]|\[FTL\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        const naming = lines.filter((l) => /\[(INF|WRN|ERR|FTL)\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l) && l.includes(PROBE));
        check('6. no Error of Bookmarks or the kit in the server log during the run, and no line of theirs at Information or above naming a bookmark',
            errors.length === 0 && naming.length === 0, { errors: errors.slice(0, 5), naming: naming.slice(0, 5) });
        const left = [];
        for (const { who, id } of made) if ((await call(who, 'GET', `/Bookmarks/${id}`)).status !== 404) left.push(id);
        check('6. clean-up: no media change left, the probe\'s bookmarks deleted',
            loadMoves().length === 0 && left.length === 0, { moves: loadMoves().length, left });
        if (fs.existsSync(heldRoot) && fs.readdirSync(heldRoot, { recursive: true }).every((file) => fs.statSync(path.join(heldRoot, file)).isDirectory())) fs.rmSync(heldRoot, { recursive: true });
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
