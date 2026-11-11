#!/usr/bin/env node
// User Ratings' library lifecycle in a running sandbox (without the archive step, which the users' lifecycle checks).
// Node 22 built-ins only. Loopback only, never port 8096. The sandbox users' tokens come from
// credentials.json and are never printed or written; the probe makes no device and no API key.
//
//   node scripts/userratings-lifecycle-probe.mjs --sandbox default [--keep-files] [--timeout 300] [--out result.json]
//
// It needs the 'default' sandbox with User Ratings 0.3.8 or later and its Music library (Initialize-Sandbox.ps1). It works on
// media of its own, made from the standard set's clips (synthetic only), and on the Music library's folders, which it moves
// to a holding folder in the sandbox and back; every scan is a full library scan:
//   1. setup: "Lifecycle Probe Film (2011) [tmdbid-900811]" and "Lifecycle Moved Film (2012) [tmdbid-900812]" in Movies and
//      "Lifecycle Probe Series (2019) [tvdbid-900819]" (two episodes) in Shows; viewer rates the film (with a review), viewer2 the
//      moved film, viewer an episode, viewer2 the series, viewer Test Album A and viewer2 Test Album B; viewer2 rates the
//      standard set's Test Pattern (2020) and Test Series (2020), which stay put, so steps 2 and 5 never take every rated
//      item of Movies or Shows at once (the mass-detach guard's case, step 10);
//   2. a folder renamed on disk and a folder moved to Movies 2 (one scan): both ratings on the new items, the same rating
//      ids, stars and review; the server log's move lines inside the scan (the post-scan flush), with ids only;
//   3. the film renamed again and copied into Movies 2 (one scan): two copies match, so the rating stays detached as
//      Ambiguous; the settings page's banner (Diagnostics) and exactly one Activity Log entry; POST Detached/Seen (admin
//      only) ends the banner;
//   4. the "Also rated on another copy" hint: viewer rates the Movies 2 copy; the Movies copy's OtherCopies names it (the
//      Open target opens), and for viewer2 without Movies 2 the hint is absent (the policy is put back in a finally);
//   5. a series folder and a music artist folder deleted (moved away, Shows and Music keep other folders): the episode's,
//      the series' and the album's ratings detach; restored: reattached, the same ids;
//   6. the cache ghost, between step 5's delete and its restore: 10.11.5 still answers the deleted
//      album from its cache, and the maintenance task's resolve leaves its rating detached instead of putting it there; a
//      member's PUT on it is the one 404, and its read says Can.Reason NotInLibrary;
//   7. the Music folder made unavailable, renamed away, then an empty folder in its place (an empty mount point): a scan
//      and User Ratings' maintenance task (its verify sweep) each time detach nothing, and every rating is attached after
//      the folder is back;
//   8. the removeRoot wipe: the Music folder away and the Music library renamed with refreshLibrary, so
//      core's ValidateTopLibraryFolders(removeRoot) removes the albums; their ratings stay held, and show again on the same
//      ids once the library has its name back and the folder is back (a run that stopped half way renames it back);
//   9. a dangling junction: Test Artist B's folder a junction to the hold folder (the album keeps its
//      id and its rating), then its target offline: a scan and the verify sweep hold the rating, and it shows again once
//      the folder is back;
//  10. the mass-detach guard (another drive given the letter): both artist folders away and a file of another drive in
//      Music: the verify sweep holds both albums' ratings (Held[].MassDetach) instead of detaching every rated item of
//      the folder; Detach now (POST Admin/Held/Detach, admin only) detaches Test Album A's, with one Activity Log entry;
//      with the folders back that rating reattaches to the same id and the other shows again;
//  11. the server log's lifecycle lines at Information carry ids, never the probe's titles or paths; no token anywhere in
//      the result.
// Every rating the probe made is deleted at the end (by its id, as its owner), its media removed and scanned away, the Music
// folders put back, and Records.Attached is back where it started. The files it touches must be the sandbox's own.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { PRODUCTION_PORT } from './sandbox-policy.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PROBE = 'ur-lifecycle-probe';
const REVIEW = 'Synthetic lifecycle probe review: the heist stays put.';
const ACTIVITY_TYPE = 'UserRatingsAmbiguous';

function usage() {
    return 'usage: node scripts/userratings-lifecycle-probe.mjs --sandbox default [--keep-files] [--timeout <s>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, keepFiles: false, timeout: 300, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--keep-files') o.keepFiles = true;
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox) || !(o.timeout > 0)) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^\uFEFF/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer', 'viewer2']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}

// The sandbox's own files only: the worktree's media folder, the probe's folder (with its holding folder) and the log.
const inside = (child, parent) => {
    const rel = path.relative(path.resolve(parent), path.resolve(child));
    return rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel);
};
const sandboxes = path.join(repoRoot, '.sandbox');
const media = path.join(sandboxes, 'media');
const workDir = path.join(sandboxRoot, 'lifecycle-probe');
const hold = path.join(workDir, 'hold');
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
for (const p of [media, workDir, hold, logDir]) {
    if (!inside(p, sandboxes) || /programdata/i.test(p) || !/^c:/i.test(p)) throw new Error(`refusing to work in ${p}: not in the sandboxes' folder ${sandboxes} on C:`);
}
const createdFile = path.join(workDir, 'created.json');
const sources = path.join(media, '_sources');
const filmClip = path.join(sources, 'testsrc2-640x360-60s-440hz.mkv');
const episodeClip = path.join(sources, 'testsrc2-640x360-30s-550hz.mkv');

// The probe's media and the Music folders it moves, all under the media folder.
const FILM = 'Lifecycle Probe Film (2011) [tmdbid-900811]';
const FILM_RENAMED = 'Lifecycle Probe Film Renamed (2011) [tmdbid-900811]';
const FILM_AGAIN = 'Lifecycle Probe Film Again (2011) [tmdbid-900811]';
const MOVED = 'Lifecycle Moved Film (2012) [tmdbid-900812]';
const SERIES = 'Lifecycle Probe Series (2019) [tvdbid-900819]';
const ARTIST = 'Test Artist A';
const ARTIST_B = 'Test Artist B';
const ANCHOR_FILM = 'Test Pattern (2020)';
const ANCHOR_SERIES = 'Test Series (2020) [tvdbid-900401]';
const MUSIC_AWAY = 'Music (unmounted by the lifecycle probe)';
// Step 8's library name while the Music library is renamed; step 9's junction target, offline; step 10's other drive.
const RENAMED_LIBRARY = 'Lifecycle Probe Music';
const OFFLINE = `${ARTIST_B} (offline)`;
const OTHER_DRIVE = 'Lifecycle another drive.txt';
const at = (...parts) => {
    const p = path.join(media, ...parts);
    if (!inside(p, media)) throw new Error(`refusing ${p}: not in ${media}`);
    return p;
};
const probeFolders = () => [at('Movies', FILM), at('Movies', FILM_RENAMED), at('Movies', FILM_AGAIN), at('Movies 2', FILM_AGAIN), at('Movies', MOVED), at('Movies 2', MOVED), at('Shows', SERIES)];

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, scans: [], recovered: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authorization(token) {
    return `MediaBrowser Client="${PROBE}", Device="sandbox", DeviceId="${PROBE}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// One request as `who` ('admin', 'viewer', 'viewer2'). Never follows redirects.
async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json', Authorization: authorization(users[who].token) };
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(60000) });
    const text = await res.text();
    let json = null;
    if (/json/i.test(res.headers.get('content-type') || '') && text.length) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text };
}

const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const ratingsOf = (id) => `/UserRatings/Items/${id}`;
const samePath = (a, b) => !!a && !!b && path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();

// ------------------------------------------------------------------------------------------ the probe's ratings

function loadCreated() {
    try { return readJson(createdFile); } catch { return []; }
}
function saveCreated(list) {
    fs.mkdirSync(workDir, { recursive: true });
    fs.writeFileSync(createdFile, JSON.stringify(list, null, 2));
}
async function rate(who, id, stars, text) {
    const r = await call(who, 'PUT', `${ratingsOf(id)}/Mine/Rating`, { body: { Stars: stars } });
    if (r.status !== 200) throw new Error(`${who} could not rate item ${id}: ${r.status} ${r.text.slice(0, 200)}`);
    let dto = r.json;
    if (text) {
        const t = await call(who, 'PUT', `${ratingsOf(id)}/Mine/Review`, { body: { Text: text } });
        if (t.status !== 200) throw new Error(`${who} could not review item ${id}: ${t.status}`);
        dto = t.json;
    }
    const list = loadCreated();
    list.push({ who, ratingId: dto.Mine.Id, itemId: id });
    saveCreated(list);
    return { who, ratingId: guid(dto.Mine.Id), itemId: guid(id), stars, text: text || null };
}
async function mine(who, id) {
    const r = await call(who, 'GET', ratingsOf(id));
    return { status: r.status, mine: r.status === 200 ? r.json.Mine : null, copies: r.status === 200 ? r.json.OtherCopies || [] : null, reason: r.status === 200 ? r.json.Can?.Reason ?? null : null };
}
// The rating is on the item, as its owner left it: the same id, stars and review.
async function onItem(rating, id) {
    const m = await mine(rating.who, id);
    return m.status === 200 && !!m.mine && guid(m.mine.Id) === rating.ratingId && m.mine.Stars === rating.stars && (m.mine.Text ?? null) === rating.text;
}

// ------------------------------------------------------------------------------------------ library, tasks, diagnostics

async function diagnostics() {
    const d = await call('admin', 'GET', '/UserRatings/Admin/Diagnostics');
    if (d.status !== 200) throw new Error(`GET /UserRatings/Admin/Diagnostics: ${d.status}`);
    return d.json;
}

// No library change waiting for User Ratings and no scan running, twice in a row: the full pass and the batch after it
// (AfterFullPass) are handled.
async function settle(label) {
    const started = Date.now();
    let quiet = 0;
    while (quiet < 2) {
        const d = await diagnostics();
        const changes = d.LibraryChanges || {};
        const queue = (changes.Consumers || []).find((c) => c.Name === 'User Ratings');
        const pending = queue && queue.Pending ? ['Added', 'Updated', 'Removed', 'RemovedFolders'].reduce((n, k) => n + (queue.Pending[k] || 0), 0) : 0;
        quiet = !changes.ScanRunning && pending === 0 ? quiet + 1 : 0;
        if (Date.now() - started > 120000) throw new Error(`User Ratings still had library changes waiting 120 s after "${label}"`);
        await delay(1500);
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
            if (last.Status !== 'Completed') throw new Error(`the scan "${label}" ended ${last.Status}`);
            const end = Date.parse(last.EndTimeUtc);
            await settle(label);
            const lifecycle = (await diagnostics()).Lifecycle || {};
            evidence.scans.push({ label, seconds: Math.round((Date.now() - started) / 100) / 10, lastBatch: lifecycle.LastBatch || null });
            return { started, end };
        }
        if (Date.now() - started > opts.timeout * 1000) throw new Error(`the scan "${label}" did not finish within ${opts.timeout} s`);
        await delay(500);
    }
}

async function runMaintenance(label) {
    const find = async () => ((await call('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } })).json || []).find((t) => t.Key === 'UserRatingsMaintenance');
    const task = await find();
    if (!task) throw new Error('no UserRatingsMaintenance task');
    const started = Date.now();
    const r = await call('admin', 'POST', `/ScheduledTasks/Running/${task.Id}`);
    if (r.status !== 204) throw new Error(`POST /ScheduledTasks/Running: ${r.status}`);
    await delay(500);
    for (;;) {
        const now = await find();
        const last = now && now.LastExecutionResult;
        if (now && now.State === 'Idle' && last && Date.parse(last.EndTimeUtc) >= started - 1000) {
            evidence.scans.push({ label: `maintenance: ${label}`, seconds: Math.round((Date.now() - started) / 100) / 10, status: last.Status });
            if (last.Status !== 'Completed') throw new Error(`the maintenance "${label}" ended ${last.Status}`);
            return (await diagnostics()).Verifier || null;
        }
        if (Date.now() - started > 180000) throw new Error(`the maintenance "${label}" did not finish`);
        await delay(500);
    }
}

async function libraryId(name) {
    const folders = (await call('admin', 'GET', '/Library/VirtualFolders')).json || [];
    const library = folders.find((l) => l.Name === name);
    if (!library) throw new Error(`the sandbox has no library "${name}"; run Initialize-Sandbox.ps1 (Music is in 'default' only)`);
    return library.ItemId;
}

// The items of a kind in a library, with their paths.
async function items(library, types) {
    const r = await call('admin', 'GET', '/Items', {
        query: { userId: users.admin.id, ParentId: await libraryId(library), Recursive: 'true', IncludeItemTypes: types, Fields: 'Path,ProviderIds', EnableImages: 'false', EnableUserData: 'false', Limit: 500 }
    });
    if (r.status !== 200) throw new Error(`GET /Items: ${r.status}`);
    return r.json.Items;
}
async function itemAt(library, types, folder) {
    const found = (await items(library, types)).filter((i) => samePath(i.Path, folder) || (i.Path && inside(i.Path, folder)));
    return found.length === 1 ? { id: guid(found[0].Id), path: found[0].Path, providerIds: found[0].ProviderIds || {} } : (found.length === 0 ? null : { many: found.length });
}

async function counts() {
    const d = await diagnostics();
    return { attached: d.Records ? d.Records.Attached : null, detached: d.Records ? d.Records.Detached : null, reasons: d.Lifecycle ? d.Lifecycle.Detached : null, held: d.Lifecycle ? d.Lifecycle.Held : null, ambiguous: d.Lifecycle ? d.Lifecycle.Ambiguous : null };
}
// The held entry (Diagnostics Lifecycle.Held) for an item, or null.
const heldOn = (c, id) => (c.held || []).find((h) => guid(h.ItemId) === id) || null;
// The core's own answer for an item (its cache included): the status of GET /Items/{id}.
async function itemRead(id) {
    return (await call('admin', 'GET', `/Items/${id}`, { query: { userId: users.admin.id } })).status;
}
// Which of the items core's database still has (a query, never its cache): GET /Items?ids=.
async function listed(ids) {
    const r = await call('admin', 'GET', '/Items', { query: { userId: users.admin.id, ids: ids.join(','), Recursive: 'true', EnableImages: 'false', EnableUserData: 'false' } });
    if (r.status !== 200) throw new Error(`GET /Items?ids: ${r.status}`);
    return (r.json.Items || []).map((i) => guid(i.Id));
}
// Waits up to `ms` for `test` to hold; whether it did.
async function waitFor(test, ms) {
    const started = Date.now();
    for (;;) {
        if (await test()) return true;
        if (Date.now() - started > ms) return false;
        await delay(1000);
    }
}

// Step 8: the library renamed with refreshLibrary, so core runs ValidateTopLibraryFolders(removeRoot: true) in the background.
async function libraryNames() {
    return ((await call('admin', 'GET', '/Library/VirtualFolders')).json || []).map((l) => l.Name);
}
async function renameLibrary(from, to) {
    const r = await call('admin', 'POST', '/Library/VirtualFolders/Name', { query: { name: from, newName: to, refreshLibrary: 'true' } });
    if (r.status !== 204) throw new Error(`POST /Library/VirtualFolders/Name: ${r.status}`);
    const started = Date.now();
    for (;;) {
        const names = await libraryNames();
        if (names.includes(to) && !names.includes(from)) return;
        if (Date.now() - started > 30000) throw new Error(`the library rename to "${to}" did not show within 30 s`);
        await delay(500);
    }
}
// A run that stopped with the Music library renamed gives it its name back.
async function recoverLibrary() {
    const names = await libraryNames();
    if (names.includes(RENAMED_LIBRARY) && !names.includes('Music')) {
        await renameLibrary(RENAMED_LIBRARY, 'Music');
        evidence.recovered.push('the Music library\'s name');
    }
}

async function activity(since, type = ACTIVITY_TYPE) {
    const r = await call('admin', 'GET', '/System/ActivityLog/Entries', { query: { minDate: new Date(since).toISOString(), limit: 200 } });
    if (r.status !== 200) throw new Error(`GET /System/ActivityLog/Entries: ${r.status}`);
    return (r.json.Items || []).filter((e) => e.Type === type).map((e) => ({ name: e.Name, severity: e.Severity, date: e.Date, userId: guid(e.UserId) }));
}

// User Ratings' lifecycle lines in the sandbox's log since `since`: [{ utc, level, text }].
function lifecycleLog(since) {
    const lines = [];
    const files = fs.existsSync(logDir) ? fs.readdirSync(logDir).filter((f) => /^log_\d{8}\.log$/.test(f)).map((f) => path.join(logDir, f)) : [];
    for (const file of files.filter((f) => fs.statSync(f).mtimeMs >= since - 60000)) {
        for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
            const m = /^\[(\d{4}-\d\d-\d\d) (\d\d:\d\d:\d\d\.\d{3}) ([+-]\d\d:\d\d)\] \[(\w{3})\] \[\d+\] Jellyfin\.Plugin\.UserRatings\.Kit\.Lifecycle: (.*)$/.exec(line);
            if (!m) continue;
            const utc = Date.parse(`${m[1]}T${m[2]}${m[3]}`);
            if (utc >= since - 1000) lines.push({ utc, level: m[4], text: m[5] });
        }
    }
    return lines;
}

// Whether Jellyfin itself skipped the Music folder as inaccessible or empty in a scan since `since` (Folder.cs's warning),
// and what it removed: so the result tells core's own guard from User Ratings'.
function coreSkipped(since) {
    const files = fs.existsSync(logDir) ? fs.readdirSync(logDir).filter((f) => /^log_\d{8}\.log$/.test(f)).map((f) => path.join(logDir, f)) : [];
    const music = at('Music').toLowerCase();
    const seen = { skipped: 0, removed: 0 };
    for (const file of files.filter((f) => fs.statSync(f).mtimeMs >= since - 60000)) {
        for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
            const m = /^\[(\d{4}-\d\d-\d\d) (\d\d:\d\d:\d\d\.\d{3}) ([+-]\d\d:\d\d)\]/.exec(line);
            if (!m || Date.parse(`${m[1]}T${m[2]}${m[3]}`) < since - 1000 || !line.toLowerCase().includes(music)) continue;
            if (/is inaccessible or empty, skipping/.test(line)) seen.skipped++;
            if (/Removing item/.test(line)) seen.removed++;
        }
    }
    return seen;
}

// ------------------------------------------------------------------------------------------ files

function move(from, to) {
    for (const p of [from, to]) {
        if (!inside(p, media) && !inside(p, hold)) throw new Error(`refusing to move ${p}: not in ${media} or ${hold}`);
    }
    fs.mkdirSync(path.dirname(to), { recursive: true });
    for (let i = 0; ; i++) {
        try {
            fs.renameSync(from, to);
            return;
        } catch (e) {
            // The server may still hold a file of it for a moment after a scan.
            if (i >= 20 || !['EBUSY', 'EPERM', 'EACCES'].includes(e.code)) throw e;
        }
        spinWait(500);
    }
}
function spinWait(ms) {
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}
function remove(folder) {
    if (!inside(folder, media) || !fs.existsSync(folder)) return;
    for (let i = 0; ; i++) {
        try {
            fs.rmSync(folder, { recursive: true, force: true });
            return;
        } catch (e) {
            if (i >= 20) throw e;
        }
        spinWait(500);
    }
}
function makeFilm(library, folder) {
    const file = at(library, folder, `${folder}.mkv`);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.copyFileSync(filmClip, file);
}
function makeSeries() {
    for (const e of [1, 2]) {
        const file = at('Shows', SERIES, 'Season 01', `Lifecycle Probe Series (2019) S01E0${e}.mkv`);
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.copyFileSync(episodeClip, file);
    }
}
function copyFolder(from, to) {
    if (!inside(from, media) || !inside(to, media)) throw new Error(`refusing to copy ${from} to ${to}`);
    fs.cpSync(from, to, { recursive: true });
}

// Step 9: a junction in the media folder to a folder in the hold folder; removing it removes the link only, never its target.
function isJunction(p) {
    try {
        return fs.lstatSync(p).isSymbolicLink();
    } catch {
        return false;
    }
}
function makeJunction(target, link) {
    if (!inside(target, hold) || !inside(link, media)) throw new Error(`refusing a junction at ${link} to ${target}`);
    fs.symlinkSync(target, link, 'junction');
}
function removeJunction(link) {
    if (!inside(link, media) || !isJunction(link)) throw new Error(`refusing to unlink ${link}: not a junction in ${media}`);
    fs.unlinkSync(link);
}
// Step 10: a file of "another drive" in the Music folder, so the folder is there and not empty.
const otherDrive = () => at('Music', OTHER_DRIVE);
function removeOtherDrive() {
    if (fs.existsSync(otherDrive())) fs.unlinkSync(otherDrive());
}

// Where each folder the probe holds goes back to. The seed's artist folders are never deleted: one that can't go back stays
// in the hold folder for the next run.
const SEED_FOLDERS = [ARTIST, ARTIST_B, OFFLINE];
const backOf = (name) => (name === SERIES ? at('Shows', SERIES) : at('Music', name === OFFLINE ? ARTIST_B : name));

// What a run that stopped half way left: the Music folder back, the junction gone and its target back, the held folders back
// in place, the other drive's file gone, the probe's media gone.
function recover() {
    const musicAway = at(MUSIC_AWAY);
    const music = at('Music');
    if (fs.existsSync(musicAway)) {
        if (fs.existsSync(music) && fs.readdirSync(music).length === 0) fs.rmdirSync(music);
        if (!fs.existsSync(music)) {
            move(musicAway, music);
            evidence.recovered.push('Music');
        }
    }
    if (isJunction(at('Music', ARTIST_B))) {
        removeJunction(at('Music', ARTIST_B));
        evidence.recovered.push(`${ARTIST_B}'s junction`);
    }
    for (const name of [...SEED_FOLDERS, SERIES]) {
        const held = path.join(hold, name);
        if (fs.existsSync(held)) {
            if (SEED_FOLDERS.includes(name)) {
                if (!fs.existsSync(backOf(name))) {
                    move(held, backOf(name));
                    evidence.recovered.push(name);
                }
            } else {
                fs.rmSync(held, { recursive: true, force: true });
            }
        }
    }
    if (fs.existsSync(otherDrive())) {
        removeOtherDrive();
        evidence.recovered.push(OTHER_DRIVE);
    }
    for (const folder of probeFolders()) {
        if (fs.existsSync(folder)) {
            remove(folder);
            evidence.recovered.push(path.relative(media, folder));
        }
    }
}

// ------------------------------------------------------------------------------------------------------------- the run

const context = { viewer2Policy: null, musicAway: false, held: [] };
const started = Date.now();
try {
    // ---- 0. the sandbox, the plugin, the Music library; what a run that stopped half way left goes first
    const d0 = await diagnostics();
    // Schema 3 (M003_Vacuum, no schema change) since 0.3.14; 4 (M004_DeletionOrigin: where a tombstone and a former member
    // came from) since 0.3.20.
    const schema = /^(0\.3\.([2-9]\d)|[1-9]\d*\.\d+\.\d+)\./.test(d0.PluginVersion) ? 4 : /^0\.3\.1[4-9]\./.test(d0.PluginVersion) ? 3 : 2;
    check(`User Ratings 0.3.8 or later is loaded, its database Ok (or just Migrated) at schema ${schema}, with Diagnostics "Lifecycle"`, /^(0\.3\.([8-9]|\d\d)|[1-9]\d*\.\d+\.\d+)\./.test(d0.PluginVersion) && d0.Store
        && ['Ok', 'Migrated'].includes(d0.Store.Status)
        && d0.Store.Version === schema && !!d0.Lifecycle && d0.Lifecycle.StartupDone === true, { version: d0.PluginVersion, store: d0.Store && d0.Store.Status, schema: d0.Store && d0.Store.Version });
    for (const left of loadCreated()) await call(left.who, 'DELETE', `/UserRatings/Mine/${left.ratingId}`);
    saveCreated([]);
    await recoverLibrary();
    recover();
    for (const p of [filmClip, episodeClip, at('Music', ARTIST), at('Music', ARTIST_B), at('Movies', ANCHOR_FILM), at('Shows', ANCHOR_SERIES)]) {
        if (!fs.existsSync(p)) throw new Error(`${p} is missing; run ./scripts/New-SandboxMedia.ps1 and Initialize-Sandbox.ps1 -Name default`);
    }
    await libraryId('Music');
    makeFilm('Movies', FILM);
    makeFilm('Movies', MOVED);
    makeSeries();
    await scan('setup');
    const before = await counts();
    evidence.before = before;
    const activityBefore = await activity(started - 60000);

    const film = await itemAt('Movies', 'Movie', at('Movies', FILM));
    const moved = await itemAt('Movies', 'Movie', at('Movies', MOVED));
    const series = await itemAt('Shows', 'Series', at('Shows', SERIES));
    const episode = (await items('Shows', 'Episode')).find((i) => i.Path && /S01E01\.mkv$/i.test(i.Path) && inside(i.Path, at('Shows', SERIES)));
    const albumA = await itemAt('Music', 'MusicAlbum', at('Music', ARTIST, 'Test Album A (2020)'));
    const albumB = await itemAt('Music', 'MusicAlbum', at('Music', 'Test Artist B', 'Test Album B (2021)'));
    const anchorFilm = await itemAt('Movies', 'Movie', at('Movies', ANCHOR_FILM));
    const anchorSeries = await itemAt('Shows', 'Series', at('Shows', ANCHOR_SERIES));
    evidence.items = { film, moved, series, episode: episode && guid(episode.Id), albumA, albumB, anchorFilm, anchorSeries };
    if (!film || !film.id || !moved || !moved.id || !series || !series.id || !episode || !albumA || !albumA.id || !albumB || !albumB.id || !anchorFilm || !anchorFilm.id || !anchorSeries || !anchorSeries.id) {
        throw new Error(`the setup scan did not find the probe's items: ${JSON.stringify(evidence.items)}`);
    }
    check('setup: the films carry their [tmdbid-] tags as TMDb ids, the series its TVDb id', film.providerIds.Tmdb === '900811' && moved.providerIds.Tmdb === '900812' && series.providerIds.Tvdb === '900819',
        { film: film.providerIds, moved: moved.providerIds, series: series.providerIds });
    const onFilm = await rate('viewer', film.id, 4, REVIEW);
    const onMoved = await rate('viewer2', moved.id, 2.5);
    const onEpisode = await rate('viewer', guid(episode.Id), 3);
    const onSeries = await rate('viewer2', series.id, 4.5);
    const onAlbumA = await rate('viewer', albumA.id, 5);
    const onAlbumB = await rate('viewer2', albumB.id, 3.5);
    // The anchors: a rating viewer2 already has there is left as it is (and not deleted at the end).
    for (const anchor of [anchorFilm, anchorSeries]) {
        if (!(await mine('viewer2', anchor.id)).mine) await rate('viewer2', anchor.id, 3);
    }
    const rated = await counts();

    // ---- 2. renamed on disk, and moved to another library: relinked through TMDb in the scan's post-scan flush
    move(at('Movies', FILM), at('Movies', FILM_RENAMED));
    move(at('Movies', MOVED), at('Movies 2', MOVED));
    const renameScan = await scan('rename and move');
    const renamed = await itemAt('Movies', 'Movie', at('Movies', FILM_RENAMED));
    const movedTo = await itemAt('Movies 2', 'Movie', at('Movies 2', MOVED));
    const oldFilm = await mine('viewer', film.id);
    const afterRename = await counts();
    check('renamed folder: a new item, the old one gone, the rating on the new one with the same id, stars and review', renamed && renamed.id && renamed.id !== film.id && oldFilm.status === 404
        && await onItem(onFilm, renamed.id), { renamed, old: oldFilm.status });
    check('moved to Movies 2: the rating on the Movies 2 item, the same id and stars', movedTo && movedTo.id && movedTo.id !== moved.id && await onItem(onMoved, movedTo.id), { movedTo });
    check('nothing left detached by the rename and the move; Records.Attached unchanged', afterRename.detached === rated.detached && afterRename.attached === rated.attached,
        { before: rated, after: afterRename });
    const renameLog = lifecycleLog(renameScan.started);
    const moveLine = (r, from, to) => renameLog.find((l) => l.text.toLowerCase().includes(`rating ${dashed(r.ratingId)} moved from item ${dashed(from)} to item ${dashed(to)}`));
    const filmMove = moveLine(onFilm, film.id, renamed && renamed.id);
    const movedMove = moveLine(onMoved, moved.id, movedTo && movedTo.id);
    check('the server log: "Rating <id> moved from item <old> to item <new>" for both, inside the scan (the post-scan flush)', !!filmMove && !!movedMove
        && filmMove.utc <= renameScan.end + 500 && movedMove.utc <= renameScan.end + 500, { film: filmMove && filmMove.text, moved: movedMove && movedMove.text, scanEnd: new Date(renameScan.end).toISOString() });

    // ---- 3. renamed again and copied into Movies 2: two copies match, so the rating is left detached as Ambiguous
    const ambiguousStart = Date.now();
    move(at('Movies', FILM_RENAMED), at('Movies', FILM_AGAIN));
    copyFolder(at('Movies', FILM_AGAIN), at('Movies 2', FILM_AGAIN));
    await scan('two copies');
    const again = await itemAt('Movies', 'Movie', at('Movies', FILM_AGAIN));
    const copy2 = await itemAt('Movies 2', 'Movie', at('Movies 2', FILM_AGAIN));
    evidence.items.again = again;
    evidence.items.copy2 = copy2;
    const onAgain = await mine('viewer', again && again.id);
    const onCopy2 = await mine('viewer', copy2 && copy2.id);
    const ambiguous = await counts();
    check('two copies: the rating on neither, detached as Ambiguous (Data counts it)', again && copy2 && onAgain.status === 200 && !onAgain.mine && onCopy2.status === 200 && !onCopy2.mine
        && ambiguous.reasons && ambiguous.reasons.Ambiguous === (rated.reasons.Ambiguous || 0) + 1 && ambiguous.detached === rated.detached + 1, { again: onAgain.status, copy2: onCopy2.status, counts: ambiguous });
    // The documented sentence, with the other cause (another title at the rating's place).
    const expected = '1 rating could not be relinked automatically because several copies match or another title is at their place. Review them in Data → Detached & ambiguous.';
    check('the settings page\'s banner: Lifecycle.Ambiguous.Banner with the documented sentence (and the other cause)', ambiguous.ambiguous && ambiguous.ambiguous.Banner === true && ambiguous.ambiguous.Message === expected,
        ambiguous.ambiguous);
    const entries = await activity(ambiguousStart - 1000);
    check('exactly one Activity Log entry: "User Ratings: 1 rating needs a manual relink" (Warning)', entries.length === 1 && entries[0].name === 'User Ratings: 1 rating needs a manual relink'
        && entries[0].severity === 'Warning' && activityBefore.length === (await activity(started - 60000)).length - 1, { entries, before: activityBefore.length });
    const ambiguousLog = lifecycleLog(ambiguousStart).find((l) => l.text.toLowerCase().includes(`rating ${dashed(onFilm.ratingId)} (item ${dashed(renamed.id)}) needs a manual relink`));
    check('the server log names the rating, its item and both candidates by id', !!ambiguousLog && ambiguousLog.text.toLowerCase().includes(dashed(again.id)) && ambiguousLog.text.toLowerCase().includes(dashed(copy2.id)),
        ambiguousLog && ambiguousLog.text);
    const seenAsViewer = await call('viewer', 'POST', '/UserRatings/Admin/Detached/Seen');
    const seen = await call('admin', 'POST', '/UserRatings/Admin/Detached/Seen');
    const afterSeen = await counts();
    check('POST Detached/Seen: 403 for viewer, 204 for admin; the banner ends, the rating stays Ambiguous', seenAsViewer.status === 403 && seen.status === 204 && afterSeen.ambiguous.Banner === false
        && afterSeen.ambiguous.Count === ambiguous.ambiguous.Count, { viewer: seenAsViewer.status, admin: seen.status, ambiguous: afterSeen.ambiguous });

    // ---- 4. the "Also rated on another copy" hint, and not for viewer2 without Movies 2
    await rate('viewer', copy2.id, 3.5);
    const hint = await mine('admin', again.id);
    const named = hint.copies.find((c) => guid(c.ItemId) === copy2.id);
    const open = await call('admin', 'GET', `/Items/${copy2.id}`, { query: { userId: users.admin.id } });
    check('the Movies copy\'s OtherCopies names the Movies 2 copy (library, count), and its Open target opens', !!named && named.LibraryName === 'Movies 2' && named.Count === 1 && open.status === 200,
        { copies: hint.copies, open: open.status });
    const viewer2 = await call('admin', 'GET', `/Users/${users.viewer2.id}`);
    context.viewer2Policy = viewer2.json.Policy;
    const movies2 = await libraryId('Movies 2');
    const enabled = ((await call('admin', 'GET', '/Library/VirtualFolders')).json || []).filter((l) => l.ItemId !== movies2).map((l) => l.ItemId);
    const restricted = await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: { ...context.viewer2Policy, EnableAllFolders: false, EnabledFolders: enabled } });
    if (restricted.status !== 204) throw new Error(`POST /Users/viewer2/Policy: ${restricted.status}`);
    const hidden = await mine('viewer2', again.id);
    const closed = await mine('viewer2', copy2.id);
    check('viewer2 without Movies 2: the Movies copy opens with no OtherCopies; the Movies 2 copy is 404', hidden.status === 200 && hidden.copies.length === 0 && closed.status === 404,
        { copies: hidden.copies, copy2: closed.status });
    const restoredPolicy = await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: context.viewer2Policy });
    if (restoredPolicy.status === 204) context.viewer2Policy = null;
    check('viewer2\'s policy is put back; the Movies 2 copy opens again', restoredPolicy.status === 204 && (await mine('viewer2', copy2.id)).status === 200, { status: restoredPolicy.status });

    // ---- 5. a series folder and a music artist folder deleted, then restored
    const deleteStart = Date.now();
    move(at('Shows', SERIES), path.join(hold, SERIES));
    context.held.push(SERIES);
    move(at('Music', ARTIST), path.join(hold, ARTIST));
    context.held.push(ARTIST);
    await scan('series and artist folders deleted');
    const gone = await counts();
    const goneEpisode = await mine('viewer', guid(episode.Id));
    // 10.11.5 keeps the album in its item cache (an item read still answers), but no query lists it any more.
    const albumListed = (await items('Music', 'MusicAlbum')).some((i) => guid(i.Id) === albumA.id);
    check('deleted: the episode 404, the album no longer in the library; the episode\'s, the series\' and the album\'s ratings detached', goneEpisode.status === 404 && !albumListed
        && gone.detached === afterSeen.detached + 3 && gone.attached === afterSeen.attached + 1 - 3 && gone.reasons.Removed === 2 && gone.reasons.Missing === 1,
    { counts: gone, before: afterSeen, albumListed, albumRead: (await mine('viewer', albumA.id)).status });
    const detachLog = lifecycleLog(deleteStart).filter((l) => /detach/i.test(l.text)).map((l) => l.text);
    check('the server log names the detached ratings and their items by id', [onEpisode, onSeries, onAlbumA].every((r) => detachLog.some((t) => t.toLowerCase().includes(dashed(r.ratingId)))),
        detachLog);

    // ---- 6. the cache ghost: 10.11.5 still answers the deleted album from its cache; the
    // maintenance task's resolve (the same id first) used to put the album's rating back there every night.
    const ghostStart = Date.now();
    await runMaintenance('artist folder deleted');
    const ghostRead = await itemRead(albumA.id);
    const ghost = await counts();
    const ghostRoute = await mine('viewer', albumA.id);
    const ghostLines = lifecycleLog(ghostStart).filter((l) => l.text.toLowerCase().includes(dashed(onAlbumA.ratingId))).map((l) => l.text);
    check('the cache ghost: GET /Items/{album} still answers 200 from 10.11.5\'s cache, yet the maintenance task leaves the album\'s rating detached (not on the ghost, nothing logged for it)',
        ghostRead === 200 && ghost.detached === gone.detached && ghost.attached === gone.attached && ghost.reasons.Missing === gone.reasons.Missing && !(ghostRoute.mine) && ghostLines.length === 0,
        { itemRead: ghostRead, route: ghostRoute.status, counts: ghost, lines: ghostLines });

    // A member's write on the ghost is the one 404 (the next maintenance would detach it), while the
    // read answers as core does and says why the stars are off. A 200 here (the old answer) is still cleaned up at the end.
    const ghostPut = await call('viewer2', 'PUT', `${ratingsOf(albumA.id)}/Mine/Rating`, { body: { Stars: 3 } });
    if (ghostPut.status === 200 && ghostPut.json?.Mine?.Id) {
        saveCreated([...loadCreated(), { who: 'viewer2', ratingId: ghostPut.json.Mine.Id, itemId: albumA.id }]);
    }
    const ghostAfter = await counts();
    check('the cache ghost: a member\'s PUT on the album is the one 404 and writes nothing; its read answers 200 with Can.Reason NotInLibrary',
        ghostPut.status === 404 && ghostRoute.status === 200 && ghostRoute.reason === 'NotInLibrary' && ghostAfter.attached === ghost.attached && ghostAfter.detached === ghost.detached,
        { put: ghostPut.status, route: ghostRoute.status, reason: ghostRoute.reason, counts: ghostAfter });
    move(path.join(hold, SERIES), at('Shows', SERIES));
    context.held = context.held.filter((h) => h !== SERIES);
    move(path.join(hold, ARTIST), at('Music', ARTIST));
    context.held = context.held.filter((h) => h !== ARTIST);
    await scan('series and artist folders restored');
    const restored = await counts();
    check('restored: the episode\'s, the series\' and the album\'s ratings reattached to the same items, as they were', await onItem(onEpisode, guid(episode.Id)) && await onItem(onSeries, series.id)
        && await onItem(onAlbumA, albumA.id) && restored.detached === afterSeen.detached && restored.attached === afterSeen.attached + 1, { counts: restored });

    // ---- 7. the Music folder unavailable: renamed away, then an empty mount point; nothing detaches
    const steady = restored;
    move(at('Music'), at(MUSIC_AWAY));
    context.musicAway = true;
    const awayScan = await scan('Music folder unavailable');
    const verifierAway = await runMaintenance('Music folder unavailable');
    const away = await counts();
    const awayListed = await listed([albumA.id, albumB.id]);
    check('the Music folder missing: a scan and the maintenance task\'s verify sweep detach nothing; both albums keep their ratings', away.detached === steady.detached && away.attached === steady.attached
        && await onItem(onAlbumA, albumA.id) && await onItem(onAlbumB, albumB.id), { counts: away, held: away.held, verifier: verifierAway, core: coreSkipped(awayScan.started), listed: awayListed });
    fs.mkdirSync(at('Music'));
    const emptyScan = await scan('Music an empty mount point');
    const verifierEmpty = await runMaintenance('Music an empty mount point');
    const empty = await counts();
    check('an empty mount point in its place: a scan and the verify sweep detach nothing', empty.detached === steady.detached && empty.attached === steady.attached
        && await onItem(onAlbumA, albumA.id) && await onItem(onAlbumB, albumB.id), { counts: empty, held: empty.held, verifier: verifierEmpty, core: coreSkipped(emptyScan.started) });
    evidence.unavailable = { away: { held: away.held, core: coreSkipped(awayScan.started), listed: awayListed }, empty: { held: empty.held, core: coreSkipped(emptyScan.started) } };
    fs.rmdirSync(at('Music'));
    move(at(MUSIC_AWAY), at('Music'));
    context.musicAway = false;
    await scan('Music folder back');
    const back = await counts();
    check('the Music folder back: every rating attached, nothing held, the same counts', back.detached === steady.detached && back.attached === steady.attached && (back.held || []).length === 0
        && await onItem(onAlbumA, albumA.id) && await onItem(onAlbumB, albumB.id), { counts: back });

    // ---- 8. the removeRoot wipe: with the Music folder away, the library renamed with refreshLibrary has
    // core validate the top folders with removeRoot, which removes the unavailable folder's items where a scan skips it
    move(at('Music'), at(MUSIC_AWAY));
    context.musicAway = true;
    const wipeStart = Date.now();
    await renameLibrary('Music', RENAMED_LIBRARY);
    const wiped = await waitFor(async () => (await listed([albumA.id, albumB.id])).length === 0, 60000);
    await settle('Music library renamed');
    const wipeScan = await scan('Music library renamed, its folder away');
    const verifierWipe = await runMaintenance('Music library renamed, its folder away');
    const wipe = await counts();
    const heldWipe = [albumA, albumB].map((a) => heldOn(wipe, a.id));
    check('the removeRoot wipe: core removed both albums from its database (GET /Items?ids= lists neither); a scan and the verify sweep detach nothing, both albums\' ratings held as LibraryFolderUnavailable',
        wiped && wipe.detached === steady.detached && wipe.attached === steady.attached && heldWipe.every((h) => h && h.Reason === 'LibraryFolderUnavailable' && h.MassDetach === false),
        { wiped, counts: wipe, held: heldWipe, verifier: verifierWipe, reads: [await itemRead(albumA.id), await itemRead(albumB.id)], core: coreSkipped(wipeStart), seconds: Math.round((Date.now() - wipeStart) / 1000), scan: wipeScan.end - wipeScan.started });
    await renameLibrary(RENAMED_LIBRARY, 'Music');
    move(at(MUSIC_AWAY), at('Music'));
    context.musicAway = false;
    await scan('Music library and folder back');
    const unwiped = await counts();
    check('the library\'s name and its folder back: both albums in the database again under the same ids, their ratings on them, nothing held, the same counts',
        (await listed([albumA.id, albumB.id])).length === 2 && await onItem(onAlbumA, albumA.id) && await onItem(onAlbumB, albumB.id)
        && unwiped.detached === steady.detached && unwiped.attached === steady.attached && (unwiped.held || []).length === 0, { counts: unwiped });

    // ---- 9. a dangling junction: Test Artist B's folder a junction to the hold folder, then its target
    // offline (a share or a drive behind the junction)
    move(at('Music', ARTIST_B), path.join(hold, ARTIST_B));
    context.held.push(ARTIST_B);
    makeJunction(path.join(hold, ARTIST_B), at('Music', ARTIST_B));
    await scan('Test Artist B behind a junction');
    const linked = await counts();
    check('Test Artist B\'s folder a junction: its album keeps its id and its rating, nothing held', await onItem(onAlbumB, albumB.id) && linked.detached === steady.detached
        && linked.attached === steady.attached && (linked.held || []).length === 0, { counts: linked });
    move(path.join(hold, ARTIST_B), path.join(hold, OFFLINE));
    context.held.push(OFFLINE);
    const danglingScan = await scan('the junction\'s target offline');
    const verifierDangling = await runMaintenance('the junction\'s target offline');
    const dangling = await counts();
    const heldB = heldOn(dangling, albumB.id);
    check('the junction\'s target offline: a scan and the verify sweep detach nothing; Test Album B\'s rating held as LibraryFolderUnavailable (an unreadable folder on its way), Test Album A\'s on its album',
        dangling.detached === steady.detached && dangling.attached === steady.attached && !!heldB && heldB.Reason === 'LibraryFolderUnavailable' && heldB.MassDetach === false
        && !heldOn(dangling, albumA.id) && await onItem(onAlbumA, albumA.id),
        { counts: dangling, listed: await listed([albumB.id]), read: await itemRead(albumB.id), verifier: verifierDangling, core: coreSkipped(danglingScan.started) });
    removeJunction(at('Music', ARTIST_B));
    move(path.join(hold, OFFLINE), at('Music', ARTIST_B));
    context.held = context.held.filter((h) => h !== ARTIST_B && h !== OFFLINE);
    await scan('Test Artist B\'s folder back');
    const unlinked = await counts();
    check('Test Artist B\'s folder back: its album\'s rating on the same id, nothing held, the same counts', await onItem(onAlbumB, albumB.id) && unlinked.detached === steady.detached
        && unlinked.attached === steady.attached && (unlinked.held || []).length === 0, { counts: unlinked });

    // ---- 10. the mass-detach guard: both artist folders gone at once, the Music folder there with a
    // file of another drive in it (the drive letter given to another volume)
    const guardStart = Date.now();
    move(at('Music', ARTIST), path.join(hold, ARTIST));
    context.held.push(ARTIST);
    move(at('Music', ARTIST_B), path.join(hold, ARTIST_B));
    context.held.push(ARTIST_B);
    fs.writeFileSync(otherDrive(), 'Synthetic: the lifecycle probe\'s stand-in for another drive\'s files.\n');
    await scan('Music holds another drive');
    const verifierGuard = await runMaintenance('Music holds another drive');
    const guarded = await counts();
    const heldGuard = [albumA, albumB].map((a) => heldOn(guarded, a.id));
    const guardLog = lifecycleLog(guardStart).filter((l) => l.level === 'WRN' && /would detach at once/.test(l.text)).map((l) => l.text);
    check('every rated item of Music gone at once: nothing detaches; both albums\' ratings held as LibraryFolderUnavailable with MassDetach, a warning in the server log',
        guarded.detached === steady.detached && guarded.attached === steady.attached && heldGuard.every((h) => h && h.Reason === 'LibraryFolderUnavailable' && h.MassDetach === true) && guardLog.length > 0,
        { counts: guarded, held: heldGuard, log: guardLog, verifier: verifierGuard, listed: await listed([albumA.id, albumB.id]) });
    const detachBody = { ItemIds: [dashed(albumA.id)] };
    const detachAsViewer = await call('viewer', 'POST', '/UserRatings/Admin/Held/Detach', { body: detachBody });
    const detachNow = await call('admin', 'POST', '/UserRatings/Admin/Held/Detach', { body: detachBody });
    const afterDetach = await counts();
    const detachEntries = await activity(guardStart - 1000, 'UserRatingsHeldDetached');
    check('Detach now: 403 for viewer; for admin 200 with Items 1, Ratings 1, NotHeld 0; Test Album A\'s rating detached as Missing, Test Album B\'s still held; one Activity Log entry by the admin',
        detachAsViewer.status === 403 && detachNow.status === 200 && detachNow.json && detachNow.json.Items === 1 && detachNow.json.Ratings === 1 && detachNow.json.NotHeld === 0
        && afterDetach.detached === steady.detached + 1 && afterDetach.attached === steady.attached - 1 && afterDetach.reasons.Missing === (steady.reasons.Missing || 0) + 1
        && !heldOn(afterDetach, albumA.id) && !!heldOn(afterDetach, albumB.id)
        && detachEntries.length === 1 && detachEntries[0].name === 'User Ratings: an admin detached 1 kept rating' && detachEntries[0].userId === guid(users.admin.id),
        { viewer: detachAsViewer.status, admin: detachNow.status, body: detachNow.json, counts: afterDetach, entries: detachEntries });
    removeOtherDrive();
    move(path.join(hold, ARTIST), at('Music', ARTIST));
    move(path.join(hold, ARTIST_B), at('Music', ARTIST_B));
    context.held = context.held.filter((h) => h !== ARTIST && h !== ARTIST_B);
    const guardBackScan = await scan('Music\'s artists back');
    const guardBack = await counts();
    const reattachLine = lifecycleLog(guardBackScan.started).find((l) => l.text.toLowerCase().includes(`rating ${dashed(onAlbumA.ratingId)} reattached to item ${dashed(albumA.id)}`));
    check('the artists back: Test Album A\'s rating reattached to the same id ("Rating <id> reattached to item <id>"), Test Album B\'s shows again, nothing held, the same counts',
        await onItem(onAlbumA, albumA.id) && await onItem(onAlbumB, albumB.id) && !!reattachLine
        && guardBack.detached === steady.detached && guardBack.attached === steady.attached && (guardBack.held || []).length === 0, { counts: guardBack, line: reattachLine && reattachLine.text });

    // ---- 11. ids in the log, never the probe's titles or paths
    const all = lifecycleLog(started);
    const leaks = all.filter((l) => l.level === 'INF' && (/Lifecycle Moved|Probe|Test Album|Test Artist/i.test(l.text) || l.text.toLowerCase().includes(media.toLowerCase())));
    check('the lifecycle log lines at Information carry ids, never the probe\'s titles or paths', all.length > 0 && leaks.length === 0, { lines: all.length, leaks: leaks.map((l) => l.text) });

    // ---- the end: every rating the probe made deleted by its owner, its media gone, the count where it started
    const deletes = [];
    for (const made of loadCreated()) deletes.push((await call(made.who, 'DELETE', `/UserRatings/Mine/${made.ratingId}`)).status);
    saveCreated([]);
    for (const folder of probeFolders()) remove(folder);
    await scan('cleanup');
    const after = await counts();
    evidence.after = after;
    check('every rating the probe made is deleted (204); its media scanned away; Records.Attached and Detached as before; no banner', deletes.every((s) => s === 204)
        && after.attached === before.attached && after.detached === before.detached && after.ambiguous.Banner === false, { deletes, before, after });
} catch (e) {
    check('probe ran', false, String(e && e.stack ? e.stack.split('\n').slice(0, 4).join(' | ') : e));
} finally {
    if (context.viewer2Policy) await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: context.viewer2Policy }).catch(() => null);
    const left = loadCreated();
    if (left.length > 0) {
        // A run that stopped half way: its ratings go now (the next run tries again for any that remain).
        const kept = [];
        for (const made of left) {
            const r = await call(made.who, 'DELETE', `/UserRatings/Mine/${made.ratingId}`).catch(() => null);
            if (!r || (r.status !== 204 && r.status !== 404)) kept.push(made);
        }
        saveCreated(kept);
    }
    const recoveredBefore = evidence.recovered.length;
    try {
        await recoverLibrary();
    } catch (e) {
        check('the Music library has its name back', false, String(e.message));
    }
    try {
        recover();
        for (const name of context.held) {
            if (fs.existsSync(path.join(hold, name)) && !fs.existsSync(backOf(name))) move(path.join(hold, name), backOf(name));
        }
    } catch (e) {
        check('the Music folders and the probe\'s media were put back', false, String(e.message));
    }
    // What was put back is scanned in again (a removeRoot wipe left the albums out of the database).
    if (evidence.recovered.length > recoveredBefore) await scan('recovery').catch(() => null);
    // The seed's artist folders are never deleted: when one couldn't go back, the hold folder stays for the next run's recover().
    if (!opts.keepFiles && !SEED_FOLDERS.some((name) => fs.existsSync(path.join(hold, name)))) fs.rmSync(hold, { recursive: true, force: true });
}

function dashed(id) {
    const n = guid(id || '');
    return n.length === 32 ? `${n.slice(0, 8)}-${n.slice(8, 12)}-${n.slice(12, 16)}-${n.slice(16, 20)}-${n.slice(20)}` : n;
}

evidence.seconds = Math.round((Date.now() - started) / 1000);
const failed = checks.filter((x) => !x.pass);
const result = { pass: failed.length === 0, checks, evidence };
const text = JSON.stringify(result, null, 2);
for (const u of Object.values(users)) {
    if (u && u.token && text.includes(u.token)) throw new Error('a token reached the result; nothing written');
}
if (opts.out) {
    fs.mkdirSync(path.dirname(path.resolve(opts.out)), { recursive: true });
    fs.writeFileSync(opts.out, text);
}
for (const x of checks) console.log(`${x.pass ? 'PASS' : 'FAIL'} ${x.name}${x.pass ? '' : ` (${JSON.stringify(x.detail)})`}`);
console.log(`${checks.length - failed.length} of ${checks.length} passed`);
process.exit(failed.length === 0 ? 0 : 1);
