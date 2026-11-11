#!/usr/bin/env node
// Bookmarks' REST API and clip extraction end to end in a running sandbox (plugins/Bookmarks/DEVELOPMENT.md "REST API").
// Node 22 built-ins only. Loopback only, never
// port 8096. The sandbox users' tokens come from credentials.json and are never printed; the API key the probe creates
// for the API-key check is deleted again and never printed either.
//
//   node scripts/bookmarks-api-probe.mjs --sandbox default [--movie "Long Test Movie"] [--keep-files] [--restart] [--out result.json]
//   node scripts/bookmarks-api-probe.mjs --sandbox default --names      (only the name checks below, and the log check)
//   node scripts/bookmarks-api-probe.mjs --sandbox default --fixture <bookmarks.json written by Bookmarks 1.0.0>
//   node scripts/bookmarks-api-probe.mjs --sandbox default --fixture <bookmarks.json of a newer schema, such as schema 3>
//   node scripts/bookmarks-api-probe.mjs --sandbox default --fixture <a 1.0 bookmarks.json the upgrade refuses> --expect-failed-upgrade
//   (--restart also goes with the two read-only fixture runs, for the .partial janitor: the newer schema's run
//   shows the janitor skipped, the failed upgrade's only the clip worker's gate; see "Read-only data" below)
//
// The normal run, as admin unless noted:
//   - Capabilities for admin (IsAdmin, CanCreateClips) and viewer (neither; viewer has no download permission);
//   - create a range bookmark on the movie, read it back, list it, update its note; a range that ends before it starts
//     is refused with 400 and ProblemDetails, an unknown id is 404 with ProblemDetails;
//   - a Fast clip (MKV, stream copy) and then an Accurate clip (H.264/AAC MP4) of the range: 202, polled to Completed,
//     downloaded with download=true (attachment, the size the bookmark reports, the container's magic bytes; with the
//     sandbox's ffprobe, the streams and the duration), streamed with download=false and a Range request (206);
//   - viewer: admin's bookmark is 404 (read, delete, clip file), its own range bookmark can't be clipped (403) and its
//     clip file is 403 (Jellyfin's download policy), GET /Bookmarks/Admin/Diagnostics is 403 (admin: 200 with the
//     plugin and kit versions); no token: 401; an API key (created for the check, then deleted): 403; ClientScript is
//     anonymous;
//   - clean-up: the bookmarks are deleted with deleteClip=true, and the clip file is gone from disk;
//   - clip-only tombstones: a bookmark with a Fast clip deleted with deleteClip=false stays in
//     bookmarks.json as a tombstone (DeletedUtc, the clip Completed in its file's state, the file on disk); it is 404 by id
//     and in no list; update, remake, cancel and both deletes are 404 and change nothing; its clip file still downloads
//     and streams (viewer2: 404); DELETE /Clip is 204 and removes record and file; a bookmark without a clip file is
//     removed instead; DELETE /Clip of a live bookmark is 200 and keeps it; and a bookmark deleted without its clip while
//     an Accurate remake runs keeps its earlier Fast file, the remake leaving no file; no Error from Bookmarks meanwhile;
//   - safe clip-file operations: with a clip file held open as a download holds it (FileShare.ReadWrite,
//     from a PowerShell process the probe starts and ends), DELETE /Clip and DELETE ?deleteClip=true are 409 with the in-use
//     message after three attempts, and neither the record in bookmarks.json nor the file changes, while the file still
//     downloads; a remake meanwhile completes under a fresh name (" (2)") and the old file waits in PendingFileDeletes;
//     released, the clip deletes (200); with --restart the probe then restarts the sandbox (Stop-Sandbox.ps1, dev-server.ps1
//     -Background -NoBuild) and checks that the startup clean-up deleted the waiting file and emptied the list;
//   - with --restart, stale .partial files: before the stop two long Accurate clips run and a short Fast
//     one waits behind them; while the sandbox is stopped, the short job's .partial under its own name (a crash's leftover)
//     and other stale files, all written an hour earlier, go into the clip folder and its per-user folder, and one written
//     "tomorrow". The start must delete exactly the ones named as clips being written in the clip folder and one level
//     down (with " (2)", with the hex fallback, and the short job's), and keep another program's name, another extension,
//     upper case, a longer extension, a file two folders down, one outside the clip folders and the one written since the
//     start, with the counts at Information and before the jobs resume; with the long jobs cancelled the short one then
//     completes under its own name, not " (2)"; then, after POST /System/Restart (Jellyfin restarts in the same process),
//     a .partial written after the process started but before that restart is deleted by the new run's clean-up;
//   - a clip folder that isn't there: with the sandbox's clip folder renamed aside (an unplugged
//     drive; renamed back in a finally, and at the next run's start after a killed run), DELETE /Clip of a live bookmark
//     and of a clip-only tombstone and DELETE ?deleteClip=true are 503 with the folder's message, bookmarks.json doesn't
//     change and the files are still there once the folder is back; then the deletes go through (200, 204, 204);
//   - names: POST /Bookmarks with a Name stores it normalised; a name over 100 characters is 400 (create, rename);
//     POST /Bookmarks/{id}/Name renames with the UpdatedAt the API sent as IfUpdatedAt, and the other tab's earlier token
//     is 409 "Changed elsewhere. Reload to see the latest." for the rename and the update, with bookmarks.json unchanged;
//     an update without Name clears it (replace semantics); a JavaScript Date's form of the token is accepted; a rename to
//     white space unnames; Bookmarks 1.0's update body (neither Name nor IfUpdatedAt) keeps the name, an explicit
//     "Name": null clears it; a body over 64 KB is 413 ProblemDetails from Kestrel's limit, with nothing
//     changed; viewer names its own bookmark and gets 404 for admin's; a named Fast clip is "<item> - <name> [range].mkv", titled
//     "<item> - <name> (h:mm:ss-h:mm:ss)" (ffprobe format_tags=title), not with the note; a rename keeps that file, and
//     the Accurate clip made next carries the new name while the earlier file goes; a tombstone's rename is 404;
//   - no server log entry of the run at Information or above names a bookmark's note or name (every name and note the
//     run gives starts with "api-probe").
//
// With --fixture it only reads, and checks the 1→2 upgrade of a store file written by Bookmarks 1.0.0,
// installed as the sandbox's bookmarks.json while the sandbox was stopped: one bookmarks.json.v1-<ts> copy and the .bak
// are the fixture byte for byte, Diagnostics says schema 2 ("Migrated" on the first start, "Ok" on later ones), the file
// holds every record with its 1.0 values, short one-line notes as names, the identity snapshot, Origin Native and
// AttachedUtc = CreatedAt, KnownUsers and ClipRoots are seeded, each user lists exactly its records through the API (no
// identity in a DTO), the store is writable, and the reads change nothing. The identity service's backfill
// completes the snapshot of every record whose video is in the library after the start (the video's name, year, runtime,
// path and provider ids; the upgrade's provisional name, the display name, stays for a video that isn't there), so the
// probe waits for its startup work (Diagnostics Identity.StartupDone) before it reads the file.
//
// Read-only data, installed the same way. A fixture of a newer schema (Version above 2): Diagnostics
// ReadOnlyNewerVersion, each user lists exactly its records as the file holds them. With --expect-failed-upgrade, a 1.0
// file the upgrade can't convert: Diagnostics ReadOnlyMigrationFailed with the one .v1-* copy listed, that copy the fixture
// byte for byte, nothing quarantined, no .bak, the server log's Error naming the step, no bookmarks listed. Both: the
// Capabilities say StoreReadOnly with the reason, every write (create; update, with and without Name and IfUpdatedAt,
// rename, clip, cancel, delete clip, delete with and without the clip, of a bookmark of admin's the file holds and of an id
// it doesn't; and viewer's update) is 503 with the store's message as ProblemDetails (after a failed upgrade too, for the
// ids the untouched file holds, which the store doesn't list), the clip worker logs once that it is idle and a
// queued or interrupted job stays as it was, a clip file still downloads (404 when it isn't on disk, never 503), no
// snapshot is written, and bookmarks.json and its folder don't change. With --restart: stale .partial files named
// as clips being written, put in the sandbox's clip folder and its per-user folder, are all still there after a restart,
// and the start logs no clean-up, only that the clip worker is idle. Only the newer schema's run shows that read-only
// data keeps the janitor out of a folder it would clean: that file records the clip folder (required). After a failed
// upgrade the store serves an empty stand-in that records no clip folder, so the janitor would find nothing there anyway;
// that run checks the clip worker's gate only (ClipExtractionService returns before the janitor). The janitor's own gate,
// for both reasons, is Clips/PartialFileJanitorTests' ReadOnlyData_KeepsTheJanitorOutOfEveryClipFolder.
//
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const TICKS = 10_000_000;

function usage() {
    return 'usage: node scripts/bookmarks-api-probe.mjs --sandbox <name> [--movie <name>] [--fixture <file> [--expect-failed-upgrade]] [--names] [--keep-files] [--restart] [--timeout <s>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, movie: 'Long Test Movie', fixture: null, expectFailedUpgrade: false, keepFiles: false, restart: false, names: false, timeout: 180, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--movie') o.movie = argv[++i];
        else if (a === '--fixture') o.fixture = argv[++i];
        else if (a === '--expect-failed-upgrade') o.expectFailedUpgrade = true;
        else if (a === '--keep-files') o.keepFiles = true;
        else if (a === '--restart') o.restart = true;
        else if (a === '--names') o.names = true;
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!Number.isFinite(o.timeout) || o.timeout <= 0) throw new Error(usage());
    if (o.expectFailedUpgrade && !o.fixture) throw new Error('--expect-failed-upgrade needs --fixture. ' + usage());
    if (o.names && (o.fixture || o.restart)) throw new Error('--names runs only the name checks of the normal run, without --fixture or --restart. ' + usage());
    if (o.restart && o.fixture && !isReadOnlyFixture(o)) throw new Error('--restart belongs to the normal run and the read-only fixtures (a newer schema, --expect-failed-upgrade). ' + usage());
    return o;
}

// A fixture checked as read-only data: a newer schema's file, or a 1.0 file run with --expect-failed-upgrade.
function isReadOnlyFixture(o) {
    if (o.expectFailedUpgrade) return true;
    try {
        return (JSON.parse(fs.readFileSync(path.resolve(o.fixture), 'utf8').replace(/^﻿/, '')).Version ?? 1) > 2;
    } catch {
        return false;
    }
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
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
const storeFile = path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'bookmarks.json');
const workDir = path.join(sandboxRoot, 'api-probe');
const ffprobe = state.JellyfinDir ? path.join(state.JellyfinDir, 'ffprobe.exe') : null;

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authorization(token, client = 'bookmarks-api-probe') {
    return `MediaBrowser Client="${client}", Device="sandbox", DeviceId="bookmarks-api-probe", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// One request as `who` ('admin', 'viewer', 'viewer2', 'anonymous' or { apiKey }). Never follows redirects.
async function call(who, method, p, { body, query, headers = {}, raw = false } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json', ...headers };
    if (who && typeof who === 'object' && who.apiKey) h.Authorization = `MediaBrowser Token="${who.apiKey}"`;
    else if (who !== 'anonymous') h.Authorization = authorization(users[who].token);
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(60000) });
    const buffer = Buffer.from(await res.arrayBuffer());
    const type = res.headers.get('content-type') || '';
    let json = null;
    if (!raw && /json/i.test(type) && buffer.length) {
        try { json = JSON.parse(buffer.toString('utf8')); } catch { json = null; }
    }
    return { status: res.status, headers: Object.fromEntries(res.headers), type, buffer, json };
}

const isProblem = (r) => /application\/problem\+json/i.test(r.type) && r.json && typeof r.json.title === 'string' && r.json.status === r.status;
const pickProblem = (r) => (r.json ? { status: r.status, type: r.type, title: r.json.title, detail: r.json.detail } : { status: r.status, type: r.type });
const sha256 = (buf) => crypto.createHash('sha256').update(buf).digest('hex');
// Jellyfin writes GUIDs without dashes ("N"); the store file has them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);

async function findMovie() {
    const r = await call('admin', 'GET', '/Items', {
        query: { userId: users.admin.id, IncludeItemTypes: 'Movie', Recursive: 'true', SearchTerm: opts.movie, Limit: 5, Fields: 'MediaSources' }
    });
    const item = r.json && r.json.Items && r.json.Items.find((i) => i.Name === opts.movie || i.Name.startsWith(`${opts.movie} (`));
    if (!item) throw new Error(`the sandbox has no movie named ${opts.movie}; run Initialize-Sandbox.ps1`);
    return { id: item.Id, name: item.Name, runTimeTicks: item.RunTimeTicks };
}

async function waitForClip(who, id, label) {
    const until = Date.now() + opts.timeout * 1000;
    const seen = { statuses: [], maxProgress: null };
    for (;;) {
        const r = await call(who, 'GET', `/Bookmarks/${id}`);
        const clip = r.json && r.json.Clip;
        const status = clip ? clip.Status : null;
        if (seen.statuses[seen.statuses.length - 1] !== status) seen.statuses.push(status);
        if (clip && typeof clip.Progress === 'number') seen.maxProgress = Math.max(seen.maxProgress ?? 0, clip.Progress);
        if (status && !['Queued', 'Running'].includes(status)) return { bookmark: r.json, seen };
        if (Date.now() > until) throw new Error(`${label}: the clip is still ${status} after ${opts.timeout} s`);
        await delay(500);
    }
}

function probeFile(file) {
    if (!ffprobe || !fs.existsSync(ffprobe)) return null;
    const run = spawnSync(ffprobe, ['-v', 'error', '-print_format', 'json', '-show_format', '-show_streams', file], { encoding: 'utf8', windowsHide: true, timeout: 60000 });
    if (run.status !== 0) return { error: (run.stderr || '').trim().slice(0, 500) };
    const info = JSON.parse(run.stdout);
    return {
        format: info.format && info.format.format_name,
        duration: info.format ? Number(info.format.duration) : null,
        streams: (info.streams || []).map((s) => ({ type: s.codec_type, codec: s.codec_name, width: s.width, height: s.height }))
    };
}

// Downloads the clip twice (as an attachment, and streamed with a Range request) and checks it.
async function checkClipFile(label, bookmark, { magic, extension, codecs, expectedSeconds, tolerance }) {
    const clip = bookmark.Clip;
    check(`${label}: Completed with a ${extension} file of the reported size`, clip.Status === 'Completed' && typeof clip.FileName === 'string'
        && clip.FileName.toLowerCase().endsWith(extension) && clip.FileSize > 0 && clip.Mode === label.split(' ')[0] && !clip.IsOutdated,
    { Status: clip.Status, Mode: clip.Mode, FileName: clip.FileName, FileSize: clip.FileSize, IsOutdated: clip.IsOutdated, Error: clip.Error });

    const download = await call('admin', 'GET', `/Bookmarks/${bookmark.Id}/Clip/File`, { query: { download: 'true' }, headers: { Accept: '*/*' }, raw: true });
    const disposition = download.headers['content-disposition'] || '';
    check(`${label}: download=true is an attachment with the file's bytes`, download.status === 200 && /^attachment/i.test(disposition)
        && download.buffer.length === clip.FileSize && magic(download.buffer),
    { status: download.status, contentType: download.type, disposition, bytes: download.buffer.length, head: download.buffer.subarray(0, 12).toString('hex') });

    const ranged = await call('admin', 'GET', `/Bookmarks/${bookmark.Id}/Clip/File`, { query: { download: 'false' }, headers: { Accept: '*/*', Range: 'bytes=0-99' }, raw: true });
    check(`${label}: download=false streams it inline with range requests (206)`, ranged.status === 206 && ranged.buffer.length === 100
        && !/^attachment/i.test(ranged.headers['content-disposition'] || '') && (ranged.headers['content-range'] || '').startsWith('bytes 0-99/'),
    { status: ranged.status, contentType: ranged.type, contentRange: ranged.headers['content-range'], bytes: ranged.buffer.length });

    fs.mkdirSync(workDir, { recursive: true });
    const file = path.join(workDir, `clip-${label.split(' ')[0].toLowerCase()}${extension}`);
    fs.writeFileSync(file, download.buffer);
    const info = probeFile(file);
    if (info && !info.error) {
        const types = info.streams.map((s) => `${s.type}:${s.codec}`);
        check(`${label}: ffprobe reads it (${codecs.join(', ')}; about ${expectedSeconds} s)`, codecs.every((c) => types.includes(c))
            && Math.abs(info.duration - expectedSeconds) <= tolerance, info);
    } else {
        check(`${label}: ffprobe reads it`, !info, info || 'no ffprobe.exe in the sandbox JellyfinDir; skipped');
    }
    if (!opts.keepFiles) fs.rmSync(file, { force: true });
    return { download: download.buffer.length, sha256: sha256(download.buffer).slice(0, 16), ffprobe: info };
}

function clipFilesOnDisk(userName) {
    const folder = path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'clips', userName);
    return fs.existsSync(folder) ? fs.readdirSync(folder) : [];
}

async function normalRun() {
    plugInLeftoverClipFolder();
    const logStart = serverLogSinceStart().length;
    const movie = await findMovie();
    evidence.movie = movie;

    // Capabilities.
    const capsAdmin = await call('admin', 'GET', '/Bookmarks/Capabilities');
    check('admin Capabilities: IsAdmin, CanCreateClips, not read-only', capsAdmin.status === 200 && capsAdmin.json.IsAdmin === true
        && capsAdmin.json.CanCreateClips === true && capsAdmin.json.ClipsEnabled === true && capsAdmin.json.StoreReadOnly === false, capsAdmin.json);
    const capsViewer = await call('viewer', 'GET', '/Bookmarks/Capabilities');
    check('viewer Capabilities: not admin, no clips (no download permission)', capsViewer.status === 200 && capsViewer.json.IsAdmin === false
        && capsViewer.json.CanCreateClips === false, capsViewer.json);

    // Create, read, list, update.
    const start = 20 * TICKS;
    const end = 26 * TICKS;
    const created = await call('admin', 'POST', '/Bookmarks', { body: { ItemId: movie.id, StartTicks: start, EndTicks: end, Note: 'api-probe range' } });
    const b = created.json || {};
    check('POST /Bookmarks creates a range bookmark (201)', created.status === 201 && guid(b.ItemId) === guid(movie.id)
        && b.StartTicks === start && b.EndTicks === end && b.Note === 'api-probe range' && b.ItemType === 'Movie' && typeof b.ItemName === 'string' && !b.Clip,
    { status: created.status, location: created.headers.location, body: b });
    if (created.status !== 201) throw new Error('the bookmark could not be created');
    const id = b.Id;
    evidence.bookmarkId = id;

    const got = await call('admin', 'GET', `/Bookmarks/${id}`);
    check('GET /Bookmarks/{id} reads it back', got.status === 200 && got.json.Id === id && got.json.StartTicks === start && got.json.EndTicks === end, { status: got.status });
    const listed = await call('admin', 'GET', '/Bookmarks', { query: { itemId: movie.id } });
    check('GET /Bookmarks?itemId= lists it', listed.status === 200 && Array.isArray(listed.json) && listed.json.some((x) => x.Id === id), { status: listed.status, count: listed.json && listed.json.length });
    const all = await call('admin', 'GET', '/Bookmarks');
    check('GET /Bookmarks lists it among all of admin\'s', all.status === 200 && all.json.some((x) => x.Id === id), { status: all.status, count: all.json && all.json.length });
    await delay(20);
    const updated = await call('admin', 'POST', `/Bookmarks/${id}`, { body: { StartTicks: start, EndTicks: end, Note: 'api-probe range (edited)' } });
    check('POST /Bookmarks/{id} updates the note (UpdatedAt later than CreatedAt)', updated.status === 200 && updated.json.Note === 'api-probe range (edited)'
        && Date.parse(updated.json.UpdatedAt) > Date.parse(updated.json.CreatedAt), { status: updated.status, CreatedAt: updated.json && updated.json.CreatedAt, UpdatedAt: updated.json && updated.json.UpdatedAt });

    const invalid = await call('admin', 'POST', '/Bookmarks', { body: { ItemId: movie.id, StartTicks: end, EndTicks: start } });
    check('a range that ends before it starts: 400 with ProblemDetails', invalid.status === 400 && isProblem(invalid), pickProblem(invalid));
    const unknown = await call('admin', 'GET', `/Bookmarks/${crypto.randomUUID()}`);
    check('an unknown bookmark: 404 with ProblemDetails', unknown.status === 404 && isProblem(unknown), pickProblem(unknown));

    // Fast clip, then Accurate clip of the same range.
    const fastQueued = await call('admin', 'POST', `/Bookmarks/${id}/Clip`, { body: { Mode: 'Fast' } });
    check('POST /Bookmarks/{id}/Clip {Mode: Fast}: 202', fastQueued.status === 202 && fastQueued.json.Clip && fastQueued.json.Clip.Mode === 'Fast', { status: fastQueued.status, clip: fastQueued.json && fastQueued.json.Clip });
    const fast = await waitForClip('admin', id, 'Fast clip');
    evidence.fast = { seen: fast.seen, clip: fast.bookmark.Clip };
    evidence.fast.file = await checkClipFile('Fast clip', fast.bookmark, {
        magic: (buf) => buf.subarray(0, 4).toString('hex') === '1a45dfa3',
        extension: '.mkv',
        codecs: ['video:h264', 'audio:aac'], // the sandbox media's own streams, copied
        expectedSeconds: 6,
        tolerance: 2.5 // starts on the keyframe at or before the start
    });

    const accurateQueued = await call('admin', 'POST', `/Bookmarks/${id}/Clip`, { body: { Mode: 'Accurate' } });
    check('POST /Bookmarks/{id}/Clip {Mode: Accurate}: 202', accurateQueued.status === 202 && accurateQueued.json.Clip && accurateQueued.json.Clip.Mode === 'Accurate', { status: accurateQueued.status, clip: accurateQueued.json && accurateQueued.json.Clip });
    const accurate = await waitForClip('admin', id, 'Accurate clip');
    evidence.accurate = { seen: accurate.seen, clip: accurate.bookmark.Clip };
    evidence.accurate.file = await checkClipFile('Accurate clip', accurate.bookmark, {
        magic: (buf) => buf.subarray(4, 8).toString('latin1') === 'ftyp',
        extension: '.mp4',
        codecs: ['video:h264', 'audio:aac'],
        expectedSeconds: 6,
        tolerance: 0.35
    });
    const adminFiles = clipFilesOnDisk('admin');
    check('the Accurate clip replaced the Fast one on disk (one file for the bookmark, no .partial)', adminFiles.filter((f) => f.includes('api-probe range')).length === 1
        && adminFiles.some((f) => f === accurate.bookmark.Clip.FileName) && !adminFiles.some((f) => f.endsWith('.partial')),
    adminFiles.filter((f) => f.includes('api-probe')));

    // Viewer: ownership, the download policy and the admin endpoint.
    const viewerRead = await call('viewer', 'GET', `/Bookmarks/${id}`);
    const viewerDelete = await call('viewer', 'DELETE', `/Bookmarks/${id}`);
    const viewerFile = await call('viewer', 'GET', `/Bookmarks/${id}/Clip/File`, { headers: { Accept: '*/*' }, raw: true });
    check('viewer: admin\'s bookmark is 404 (read, delete), and its clip file is refused', viewerRead.status === 404 && viewerDelete.status === 404
        && [403, 404].includes(viewerFile.status), { read: viewerRead.status, delete: viewerDelete.status, clipFile: viewerFile.status });
    const viewerOwn = await call('viewer', 'POST', '/Bookmarks', { body: { ItemId: movie.id, StartTicks: start, EndTicks: end, Note: 'api-probe viewer range' } });
    const viewerClip = viewerOwn.json ? await call('viewer', 'POST', `/Bookmarks/${viewerOwn.json.Id}/Clip`, { body: { Mode: 'Fast' } }) : null;
    const viewerOwnFile = viewerOwn.json ? await call('viewer', 'GET', `/Bookmarks/${viewerOwn.json.Id}/Clip/File`, { headers: { Accept: '*/*' }, raw: true }) : null;
    check('viewer: its own range bookmark can\'t be clipped (403 with ProblemDetails) and its clip file is 403 (download policy)', viewerOwn.status === 201
        && viewerClip && viewerClip.status === 403 && isProblem(viewerClip) && viewerOwnFile && viewerOwnFile.status === 403,
    { create: viewerOwn.status, clip: viewerClip && pickProblem(viewerClip), clipFile: viewerOwnFile && viewerOwnFile.status });
    const viewerSeesOwnOnly = await call('viewer', 'GET', '/Bookmarks', { query: { itemId: movie.id } });
    check('viewer lists only its own bookmarks', viewerSeesOwnOnly.status === 200 && viewerSeesOwnOnly.json.every((x) => x.Id !== id)
        && viewerSeesOwnOnly.json.some((x) => viewerOwn.json && x.Id === viewerOwn.json.Id), { count: viewerSeesOwnOnly.json && viewerSeesOwnOnly.json.length });

    const diagViewer = await call('viewer', 'GET', '/Bookmarks/Admin/Diagnostics');
    const diagAdmin = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    check('GET /Bookmarks/Admin/Diagnostics: 403 for viewer, 200 for admin with the plugin and kit versions', diagViewer.status === 403 && diagAdmin.status === 200
        && typeof diagAdmin.json.PluginVersion === 'string' && typeof diagAdmin.json.KitVersion === 'string',
    { viewer: diagViewer.status, admin: diagAdmin.status, PluginVersion: diagAdmin.json && diagAdmin.json.PluginVersion, KitVersion: diagAdmin.json && diagAdmin.json.KitVersion });
    evidence.diagnostics = diagAdmin.json && Object.fromEntries(Object.entries(diagAdmin.json).filter(([k]) => k !== 'ClientHealth'));

    // No token, an API key, and the anonymous script.
    const anonymous = await call('anonymous', 'GET', '/Bookmarks');
    check('no token: 401', anonymous.status === 401, { status: anonymous.status });
    const keyApp = `bookmarks-api-probe-${crypto.randomBytes(4).toString('hex')}`;
    const keyCreated = await call('admin', 'POST', '/Auth/Keys', { query: { app: keyApp } });
    const keys = await call('admin', 'GET', '/Auth/Keys');
    const key = keys.json && keys.json.Items && keys.json.Items.find((k) => k.AppName === keyApp);
    if (key) {
        try {
            const byKey = await call({ apiKey: key.AccessToken }, 'GET', '/Bookmarks');
            const capsByKey = await call({ apiKey: key.AccessToken }, 'GET', '/Bookmarks/Capabilities');
            check('an API key: 403 with ProblemDetails (bookmarks belong to users)', byKey.status === 403 && isProblem(byKey) && capsByKey.status === 403,
                { bookmarks: pickProblem(byKey), capabilities: capsByKey.status });
        } finally {
            const removed = await call('admin', 'DELETE', `/Auth/Keys/${key.AccessToken}`);
            check('the API key made for the check is deleted', removed.status === 204, { status: removed.status });
        }
    } else {
        check('an API key could be created for the check', false, { create: keyCreated.status, list: keys.status });
    }
    const script = await call('anonymous', 'GET', '/Bookmarks/ClientScript', { headers: { Accept: '*/*' }, raw: true });
    check('GET /Bookmarks/ClientScript is anonymous JavaScript with an ETag', script.status === 200 && /javascript/i.test(script.type) && !!script.headers.etag,
        { status: script.status, type: script.type, etag: script.headers.etag, bytes: script.buffer.length });

    // Clean-up: both bookmarks, with their clips.
    const deleted = await call('admin', 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } });
    const afterDelete = await call('admin', 'GET', `/Bookmarks/${id}`);
    const fileAfterDelete = await call('admin', 'GET', `/Bookmarks/${id}/Clip/File`, { headers: { Accept: '*/*' }, raw: true });
    const viewerDeleted = viewerOwn.json ? await call('viewer', 'DELETE', `/Bookmarks/${viewerOwn.json.Id}`) : null;
    check('DELETE /Bookmarks/{id}?deleteClip=true: 204, then 404, and the clip file is gone from disk', deleted.status === 204 && afterDelete.status === 404
        && fileAfterDelete.status === 404 && !clipFilesOnDisk('admin').some((f) => f.includes('api-probe range')) && (!viewerDeleted || viewerDeleted.status === 204),
    { delete: deleted.status, read: afterDelete.status, file: fileAfterDelete.status, viewerDelete: viewerDeleted && viewerDeleted.status });

    await tombstoneChecks(movie);
    await nameChecks(movie);
    const waiting = await clipFileChecks(movie);
    await offlineFolderChecks(movie);

    logPrivacyCheck(logStart);

    if (opts.restart) await restartChecks(movie, waiting);
    else evidence.clipFiles.pendingUntilNextStart = waiting && path.basename(waiting);
}

// --names: only the name checks, and the log check over them.
async function namesRun() {
    plugInLeftoverClipFolder();
    const logStart = serverLogSinceStart().length;
    const movie = await findMovie();
    evidence.movie = movie;
    await nameChecks(movie);
    logPrivacyCheck(logStart);
}

// Names and notes are never logged at Information or above. Every note and name the run gives a bookmark
// starts with "api-probe", and a clip file's name holds one (its path is logged at Debug only), so no
// entry of the run at those levels may contain it.
function logPrivacyCheck(logStart) {
    const leaks = logEntriesAtOrAbove(serverLogSinceStart().slice(logStart), 'INF').filter((e) => e.text.toLowerCase().includes('api-probe'));
    evidence.logPrivacy = { leaks: leaks.length };
    check('privacy: no server log entry of the run at Information or above names a bookmark\'s note or name (a clip file\'s path is logged at Debug only)',
        leaks.length === 0, leaks.slice(0, 5).map((e) => e.text.slice(0, 300)));
}

// Holds a file open as a download or a preview does (read, sharing read and write but not delete: Kestrel's PhysicalFile
// opens clip files so), from a PowerShell process the probe starts, until the returned release() ends it. The path goes
// through the environment, never a command line.
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

// Safe clip-file operations on a running server. A clip file held open (as a download holds it) can't be
// deleted: both delete routes answer 409 after three attempts, with the record in bookmarks.json and the file unchanged,
// while the file still downloads. A remake meanwhile gets a fresh name, and the old file waits in PendingFileDeletes; once
// released, the clip deletes. Returns the waiting file's path, which the next start deletes.
async function clipFileChecks(movie) {
    const logBefore = serverLogSinceStart().length;
    const e = {};
    evidence.clipFiles = e;
    const made = await bookmarkWithClip(movie, 50, 54, 'api-probe held', 'Fast');
    const id = made.Id;
    const record = storedRecord(id);
    const file = record && record.Clip && record.Clip.FilePath;
    if (!file || !fs.existsSync(file)) throw new Error('the held clip has no file on disk');
    const roots = readJson(storeFile).ClipRoots || [];
    e.clipRoots = roots.length;
    check('safe clip-file operations: the clip\'s folder is a recorded clip folder in bookmarks.json (ClipRoots)',
        roots.some((r) => file.toLowerCase().startsWith(r.toLowerCase().replace(/[\\/]+$/, '') + path.sep)), { roots: roots.length });

    let release = await holdOpen(file);
    let waiting = null;
    try {
        const before = JSON.stringify(storedRecord(id));
        const started = Date.now();
        const deleteClip = await call('admin', 'DELETE', `/Bookmarks/${id}/Clip`);
        e.deleteClipMs = Date.now() - started;
        const deleteWithClip = await call('admin', 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } });
        const download = await call('admin', 'GET', `/Bookmarks/${id}/Clip/File`, { headers: { Accept: '*/*' }, raw: true });
        e.inUse = { deleteClip: pickProblem(deleteClip), deleteWithClip: pickProblem(deleteWithClip), download: download.status };
        const message = 'The clip file is in use (being played or downloaded); try again shortly.';
        check('safe clip-file operations: with the clip file held open (FileShare.ReadWrite, as a download holds it), DELETE /Bookmarks/{id}/Clip is 409 with ProblemDetails and the in-use message, after three attempts 200 ms apart, and neither the record in bookmarks.json nor the file changed',
            deleteClip.status === 409 && isProblem(deleteClip) && deleteClip.json.detail === message && e.deleteClipMs >= 380
            && JSON.stringify(storedRecord(id)) === before && fs.existsSync(file),
        { ...e.inUse, ms: e.deleteClipMs });
        check('safe clip-file operations: DELETE /Bookmarks/{id}?deleteClip=true is 409 too with nothing changed, and the held file still downloads (200)',
            deleteWithClip.status === 409 && isProblem(deleteWithClip) && deleteWithClip.json.detail === message
            && JSON.stringify(storedRecord(id)) === before && fs.existsSync(file) && download.status === 200 && download.buffer.length === made.Clip.FileSize,
        e.inUse);

        // A remake of the same range and mode while the old file is held: a fresh name, and the old file waits.
        const remade = await call('admin', 'POST', `/Bookmarks/${id}/Clip`, { body: { Mode: 'Fast' } });
        const done = remade.status === 202 ? (await waitForClip('admin', id, 'remake while held')).bookmark : null;
        await delay(500);
        const after = storedRecord(id);
        const pending = (readJson(storeFile).PendingFileDeletes || []).filter((p) => p.Path.toLowerCase() === file.toLowerCase());
        const oldName = path.basename(file);
        const freshName = oldName.replace(/\.mkv$/i, ' (2).mkv');
        const log = serverLogSinceStart().slice(logBefore);
        const queuedLine = log.split(/\r?\n/).find((l) => l.includes("couldn't be deleted now (InUse); it is deleted later") && l.replace(/-/g, '').toLowerCase().includes(guid(id)));
        e.remake = { status: remade.status, clip: done && done.Clip && { Status: done.Clip.Status, FileName: done.Clip.FileName, FileSize: done.Clip.FileSize }, pending, oldOnDisk: fs.existsSync(file), queuedLine: queuedLine && queuedLine.slice(0, 200) };
        check('safe clip-file operations: a remake while the old clip is held open completes under a fresh name (" (2)"), the record points at it, and the old file waits in PendingFileDeletes (its path, 1 attempt), with an Information line naming the bookmark and not the file',
            !!done && done.Clip.Status === 'Completed' && done.Clip.FileName === freshName && path.basename(after.Clip.FilePath) === freshName
            && pending.length === 1 && pending[0].Attempts === 1 && fs.existsSync(file) && !!queuedLine && !queuedLine.includes('api-probe'),
        e.remake);
        if (pending.length === 1) waiting = file;
    } finally {
        e.holderExit = await release();
        release = null;
    }

    // Released: the clip (now the fresh file) deletes; the old file stays listed until the next start.
    const current = storedRecord(id);
    const fresh = current && current.Clip && current.Clip.FilePath;
    const deleted = await call('admin', 'DELETE', `/Bookmarks/${id}/Clip`);
    check('safe clip-file operations: released, DELETE /Bookmarks/{id}/Clip is 200 with the bookmark left, and its file is gone; the old file is still listed for the next start',
        deleted.status === 200 && deleted.json && deleted.json.Id === id && !deleted.json.Clip && !!fresh && !fs.existsSync(fresh)
        && (!waiting || (fs.existsSync(waiting) && (readJson(storeFile).PendingFileDeletes || []).some((p) => p.Path.toLowerCase() === waiting.toLowerCase()))),
    { status: deleted.status, freshOnDisk: !!fresh && fs.existsSync(fresh) });
    const removed = await call('admin', 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } });
    check('safe clip-file operations, clean-up: the bookmark deletes (204)', removed.status === 204, { status: removed.status });
    const errors = serverLogSinceStart().slice(logBefore).split(/\r?\n/).filter((l) => /\[ERR\]/.test(l) && /Bookmarks/.test(l));
    check('safe clip-file operations: the server log has no Error from Bookmarks during the clip-file checks', errors.length === 0, errors.slice(0, 5));
    return waiting;
}

// A clip folder that isn't there (an unplugged drive whose letter is gone, a folder moved or
// removed) is never taken for a folder whose files are gone. The sandbox's clip folder is renamed aside while the server
// runs (no clip job runs and no clip file is open at this point), and renamed back in a finally; a run killed meanwhile
// leaves it aside, and the next run renames it back first (plugInLeftoverClipFolder).
async function offlineFolderChecks(movie) {
    const logBefore = serverLogSinceStart().length;
    const e = {};
    evidence.clipFiles.offlineFolder = e;
    const live = await bookmarkWithClip(movie, 56, 58, 'api-probe offline', 'Fast');
    const kept = await bookmarkWithClip(movie, 60, 62, 'api-probe offline kept', 'Fast');
    const tombstoned = await call('admin', 'DELETE', `/Bookmarks/${kept.Id}`, { query: { deleteClip: 'false' } });
    const liveFile = storedRecord(live.Id).Clip.FilePath;
    const keptFile = storedRecord(kept.Id).Clip.FilePath;
    const root = recordedRootOf(liveFile);
    if (tombstoned.status !== 204 || !root || recordedRootOf(keptFile) !== root) {
        check("missing clip folder: a clip folder that isn't there (set-up: a live clip and a tombstone's in one recorded clip folder inside the sandbox)", false,
            { tombstone: tombstoned.status, root: !!root });
        return;
    }
    const aside = unpluggedName(root);
    const before = fs.readFileSync(storeFile);
    const message = "The clip's folder isn't available (an unplugged drive, or a folder that was moved or removed); nothing was changed. Try again once it is back.";
    let unplugged = false;
    try {
        fs.renameSync(root, aside);
        unplugged = true;
        const deleteClip = await call('admin', 'DELETE', `/Bookmarks/${live.Id}/Clip`);
        const deleteWithClip = await call('admin', 'DELETE', `/Bookmarks/${live.Id}`, { query: { deleteClip: 'true' } });
        const deleteKept = await call('admin', 'DELETE', `/Bookmarks/${kept.Id}/Clip`);
        const download = await call('admin', 'GET', `/Bookmarks/${live.Id}/Clip/File`, { headers: { Accept: '*/*' }, raw: true });
        e.unplugged = { deleteClip: pickProblem(deleteClip), deleteWithClip: pickProblem(deleteWithClip), deleteKept: pickProblem(deleteKept), download: download.status };
        const unchanged = Buffer.compare(before, fs.readFileSync(storeFile)) === 0;
        e.storeUnchanged = unchanged;
        check("missing clip folder: with the clip folder gone (an unplugged drive), DELETE /Clip of a live bookmark and of a tombstone and DELETE ?deleteClip=true are 503 with the folder's message, bookmarks.json doesn't change, and the clip file is not served (404)",
            [deleteClip, deleteWithClip, deleteKept].every((r) => r.status === 503 && isProblem(r) && r.json.detail === message) && unchanged && download.status === 404,
        e.unplugged);
    } catch (err) {
        check('missing clip folder: the clip folder could be renamed aside and the deletes asked', false, String(err && err.message).slice(0, 300));
    } finally {
        if (unplugged) fs.renameSync(aside, root);
    }
    e.filesBack = fs.existsSync(liveFile) && fs.existsSync(keptFile);
    const deleteClip = await call('admin', 'DELETE', `/Bookmarks/${live.Id}/Clip`);
    const deleteKept = await call('admin', 'DELETE', `/Bookmarks/${kept.Id}/Clip`);
    const removed = await call('admin', 'DELETE', `/Bookmarks/${live.Id}`, { query: { deleteClip: 'true' } });
    e.pluggedIn = { deleteClip: deleteClip.status, deleteKept: deleteKept.status, removed: removed.status };
    check('missing clip folder: the folder back, both files are still there, and the deletes go through (200, tombstone 204, bookmark 204) with the files gone',
        e.filesBack && deleteClip.status === 200 && deleteKept.status === 204 && removed.status === 204
        && !fs.existsSync(liveFile) && !fs.existsSync(keptFile) && storedRecord(kept.Id) === null && storedRecord(live.Id) === null,
    { ...e.pluggedIn, filesBack: e.filesBack });
    const log = serverLogSinceStart().slice(logBefore);
    e.warned = log.includes("The clip folder isn't there (an unplugged drive, or a folder that was moved or removed).");
    const errors = log.split(/\r?\n/).filter((l) => /\[ERR\]/.test(l) && /Bookmarks/.test(l));
    check('missing clip folder: the server log names the missing clip folder at Warning, and has no Error from Bookmarks', e.warned && errors.length === 0, { warned: e.warned, errors: errors.slice(0, 5) });
}

// The recorded clip folder (ClipRoots) a clip file lies under, when it is inside this sandbox; else null.
function recordedRootOf(file) {
    const roots = (readJson(storeFile).ClipRoots || []).map((r) => r.replace(/[\\/]+$/, ''));
    const under = roots.filter((r) => file.toLowerCase().startsWith(r.toLowerCase() + path.sep)).sort((a, b) => b.length - a.length)[0];
    const inside = under && path.resolve(under).toLowerCase().startsWith(path.resolve(sandboxRoot).toLowerCase() + path.sep);
    return inside ? under : null;
}

// Where offlineFolderChecks puts a clip folder while it is "unplugged".
function unpluggedName(root) {
    return `${root}.api-probe-unplugged`;
}

// A run killed while the clip folder was renamed aside: put it back before anything else.
function plugInLeftoverClipFolder() {
    if (!fs.existsSync(storeFile)) return;
    for (const root of readJson(storeFile).ClipRoots || []) {
        const trimmed = root.replace(/[\\/]+$/, '');
        const aside = unpluggedName(trimmed);
        if (fs.existsSync(aside) && !fs.existsSync(trimmed) && path.resolve(trimmed).toLowerCase().startsWith(path.resolve(sandboxRoot).toLowerCase() + path.sep)) {
            fs.renameSync(aside, trimmed);
            evidence.pluggedInLeftover = path.basename(trimmed);
        }
    }
}

// --restart: the sandbox is stopped and started again (Stop-Sandbox.ps1, dev-server.ps1 -Background -NoBuild). The start's
// clean-up (the janitor, after the startup gate and before the clip worker resumes jobs) deletes the stale .partial files of
// clip jobs an earlier run didn't finish, then the file a remake left waiting, and empties
// PendingFileDeletes.
async function restartChecks(movie, waiting) {
    evidence.restarts = [];
    const janitor = await janitorJobsBeforeStop(movie);
    runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox], 'stop');
    try {
        janitorFilesWhileStopped(janitor);
        runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'], 'start');
        await waitUntilServing();
        await janitorChecksAfterStart(janitor);

        const retried = await waitForLog(/Retried the clip files waiting to be deleted: \d+ deleted/, 60);
        const pending = readJson(storeFile).PendingFileDeletes || [];
        evidence.clipFiles.afterRestart = { retried: retried && retried.slice(0, 300), pending: pending.length, waitingOnDisk: !!waiting && fs.existsSync(waiting) };
        check('safe clip-file operations after a restart: the startup clean-up deleted the file the remake left waiting and emptied PendingFileDeletes',
            !!waiting && !fs.existsSync(waiting) && pending.length === 0 && !!retried && /: [1-9]\d* deleted/.test(retried),
        evidence.clipFiles.afterRestart);
        const leaks = logEntriesAtOrAbove(serverLogSinceStart(), 'INF').filter((l) => l.text.toLowerCase().includes('api-probe'));
        check('clip-file operations and .partial clean-up after the restart: no server log entry at Information or above names a note or a clip file', leaks.length === 0, leaks.slice(0, 5).map((l) => l.text.slice(0, 300)));
        await inProcessRestartCheck();
    } finally {
        removeProbeFiles(janitor);
    }
}

// Jellyfin 10.11 restarts in the same process (POST /System/Restart, the dashboard's Restart: Program.cs runs the server
// again while _restartOnShutdown, with new services). A .partial the run before left, written after the process started,
// is stale for the new run, whose janitor starts with it (the run's start, not the process start).
async function inProcessRestartCheck() {
    const e = {};
    evidence.janitor.inProcessRestart = e;
    const pidFile = path.join(sandboxRoot, 'server.pid');
    const pid = () => (fs.existsSync(pidFile) ? fs.readFileSync(pidFile, 'utf8').trim() : null);
    const file = path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'clips', 'admin', 'api-probe janitor in-process [00.00.01-00.00.02].mkv.partial');
    const pidBefore = pid();
    const logBefore = serverLogSinceStart().length;
    fs.writeFileSync(file, 'api-probe janitor: half a clip');
    const written = new Date(Date.now() - 1000);
    fs.utimesSync(file, written, written);
    try {
        const restart = await call('admin', 'POST', '/System/Restart');
        e.status = restart.status;
        let line = null;
        for (let i = 0; i < 240 && !line; i++) {
            await delay(500);
            line = serverLogSinceStart().slice(logBefore).split(/\r?\n/).find((l) => l.includes('Cleaned up the unfinished clip files (.partial)')) || null;
        }
        await waitUntilServing();
        const after = serverLogSinceStart().slice(logBefore);
        e.line = line && line.slice(line.indexOf('Cleaned up'), line.indexOf('Cleaned up') + 220);
        e.sameProcess = pid() === pidBefore && !after.includes('Main: Jellyfin version:');
        e.startupComplete = /Startup complete/.test(after);
        e.fileGone = !fs.existsSync(file);
        check('stale .partial files (Jellyfin 10.11 restarts in the same process): after POST /System/Restart the new run\'s clean-up deleted the .partial the run before left, although it was written after the process started',
            (restart.status === 204 || restart.status === 200) && e.sameProcess && e.startupComplete && !!line && /: [1-9]\d* deleted, 0 could not be deleted/.test(line) && e.fileGone, e);
        const leaks = logEntriesAtOrAbove(after, 'INF').filter((l) => l.text.toLowerCase().includes('api-probe'));
        const errors = after.split(/\r?\n/).filter((l) => /\[ERR\]/.test(l) && /Bookmarks/.test(l));
        check('stale .partial files after the restart in the same process: no Error from Bookmarks, and no entry at Information or above naming a note or a clip file',
            leaks.length === 0 && errors.length === 0, { leaks: leaks.slice(0, 3).map((l) => l.text.slice(0, 200)), errors: errors.slice(0, 3) });
    } finally {
        fs.rmSync(file, { force: true });
    }
}

async function waitUntilServing() {
    for (let i = 0; i < 60; i++) {
        const r = await call('admin', 'GET', '/Bookmarks/Capabilities').catch(() => null);
        if (r && r.status === 200) return;
        await delay(1000);
    }
}

// ClipFileNaming.Sanitize and BuildFileName (plugins/Bookmarks/.../Clips/ClipFileNaming.cs), for the names the probe
// gives: a clip file's name is "<item> - <label> [hh.mm.ss-hh.mm.ss].<mkv|mp4>".
function sanitizeName(value, max = 80) {
    if (!value || !value.trim()) return '';
    let out = '';
    let lastWasSpace = false;
    for (const ch of value) {
        const replacement = ch === ':' ? ' -' : (/[\u0000-\u001f\u007f-\u009f]/.test(ch) || /\s/.test(ch)) ? ' ' : '<>"/\\|?*'.includes(ch) ? '_' : null;
        if (replacement === null) {
            out += ch;
            lastWasSpace = false;
            continue;
        }
        for (const r of replacement) {
            if (r === ' ') {
                if (lastWasSpace) continue;
                lastWasSpace = true;
            } else {
                lastWasSpace = false;
            }
            out += r;
        }
    }
    out = out.trim();
    if (out.length > max) out = out.slice(0, max);
    return out.replace(/[. ]+$/, '');
}

function clipFileName(itemName, label, startTicks, endTicks, mode) {
    const stamp = (ticks) => {
        const seconds = Math.floor(Math.max(0, ticks) / TICKS);
        const two = (n) => String(n).padStart(2, '0');
        return `${two(Math.floor(seconds / 3600))}.${two(Math.floor(seconds / 60) % 60)}.${two(seconds % 60)}`;
    };
    let name = sanitizeName(itemName) || 'Clip';
    const labelText = sanitizeName(label);
    if (labelText) name += ' - ' + labelText;
    return `${name} [${stamp(startTicks)}-${stamp(endTicks)}]${mode === 'Fast' ? '.mkv' : '.mp4'}`;
}

// The pattern of the name of a clip being written (the plugin's ClipNamePattern), for the probe's own listing.
const PARTIAL_NAME = /^.+ \[[0-9]{2,}\.[0-9]{2}\.[0-9]{2}-[0-9]{2,}\.[0-9]{2}\.[0-9]{2}\](?: \((?:[0-9]+|[0-9a-f]{32})\))?\.(?:mp4|mkv)\.partial$/;

// The .partial files named as clips being written in the recorded clip folders inside the sandbox and the folders
// directly inside them (what the janitor looks at).
function partialsInClipFolders() {
    const found = [];
    for (const root of (readJson(storeFile).ClipRoots || []).map((r) => r.replace(/[\\/]+$/, ''))) {
        if (!path.resolve(root).toLowerCase().startsWith(path.resolve(sandboxRoot).toLowerCase() + path.sep) || !fs.existsSync(root)) continue;
        for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
            const full = path.join(root, entry.name);
            if (entry.isFile() && PARTIAL_NAME.test(entry.name)) found.push(full);
            else if (entry.isDirectory()) {
                for (const inner of fs.readdirSync(full, { withFileTypes: true })) {
                    if (inner.isFile() && PARTIAL_NAME.test(inner.name)) found.push(path.join(full, inner.name));
                }
            }
        }
    }
    return [...new Set(found)];
}

// The stale .partial files' set-up while the server runs: two long Accurate clips, and a short Fast clip queued behind them (one clip at a
// time, MaxConcurrentClips 1), so the short one is still queued, never started, when the sandbox stops.
async function janitorJobsBeforeStop(movie) {
    const e = {};
    evidence.janitor = e;
    const configFile = path.join(sandboxRoot, 'data', 'plugins', 'configurations', 'Jellyfin.Plugin.Bookmarks.xml');
    const concurrent = fs.existsSync(configFile) ? /<MaxConcurrentClips>(\d+)<\/MaxConcurrentClips>/.exec(fs.readFileSync(configFile, 'utf8')) : null;
    e.maxConcurrentClips = concurrent ? Number(concurrent[1]) : 1;
    const make = async (start, end, note, mode) => {
        const created = await call('admin', 'POST', '/Bookmarks', { body: { ItemId: movie.id, StartTicks: start * TICKS, EndTicks: end * TICKS, Note: note } });
        if (created.status !== 201) throw new Error(`the bookmark "${note}" could not be created: ${created.status}`);
        const queued = await call('admin', 'POST', `/Bookmarks/${created.json.Id}/Clip`, { body: { Mode: mode } });
        if (queued.status !== 202) throw new Error(`the clip of "${note}" could not be queued: ${queued.status}`);
        return created.json;
    };
    const long = [await make(5, 175, 'api-probe janitor long 1', 'Accurate'), await make(6, 176, 'api-probe janitor long 2', 'Accurate')];
    const resumed = await make(10, 12, 'api-probe janitor resumed', 'Fast');
    const name = clipFileName(resumed.ItemName, 'api-probe janitor resumed', 10 * TICKS, 12 * TICKS, 'Fast');
    let running = null;
    for (let i = 0; i < 100 && !running; i++) {
        const r = await call('admin', 'GET', `/Bookmarks/${long[0].Id}`);
        if (r.json && r.json.Clip && r.json.Clip.Status === 'Running') running = r.json.Clip;
        else await delay(200);
    }
    const before = await call('admin', 'GET', `/Bookmarks/${resumed.Id}`);
    e.beforeStop = { long: running && running.Status, resumed: before.json && before.json.Clip && before.json.Clip.Status };
    return { long, resumed, name, e };
}

// While the sandbox is stopped (a crash's leftovers): the short job's .partial under its own name, as the server would
// have left it had it died while making that clip, and files that must stay. The janitor looks only at the recorded clip
// folders and the folders directly inside them.
function janitorFilesWhileStopped(janitor) {
    const { e } = janitor;
    const root = path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'clips');
    const roots = (readJson(storeFile).ClipRoots || []).map((r) => r.replace(/[\\/]+$/, '').toLowerCase());
    e.rootRecorded = roots.includes(root.toLowerCase());
    const admin = path.join(root, 'admin');
    const afterStop = { long: janitor.long.map((b) => storedRecord(b.Id)?.Clip?.Status ?? null), resumed: storedRecord(janitor.resumed.Id)?.Clip?.Status ?? null };
    e.afterStop = afterStop;
    check('stale .partial files, set-up: after the stop the short clip job is still queued behind the long ones (it never ran; one clip at a time), and the long ones were stopped as interrupted',
        e.maxConcurrentClips === 1 && e.beforeStop.long === 'Running' && afterStop.resumed === 'Queued' && afterStop.long.every((s) => s === 'Queued' || s === 'Running'),
    { maxConcurrentClips: e.maxConcurrentClips, beforeStop: e.beforeStop, afterStop });

    // Stale files of an earlier run, the sandbox's own included (a .partial a killed run left), are the janitor's too.
    const earlier = partialsInClipFolders();
    const hourAgo = new Date(Date.now() - 3600 * 1000);
    const tomorrow = new Date(Date.now() + 24 * 3600 * 1000);
    const write = (file, time) => {
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.writeFileSync(file, 'api-probe janitor: half a clip');
        fs.utimesSync(file, time, time);
        return file;
    };
    janitor.stale = [
        path.join(root, 'api-probe janitor root [00.00.01-00.00.02].mkv.partial'),
        path.join(admin, 'api-probe janitor [00.00.01-00.00.02] (2).mp4.partial'),
        path.join(admin, 'api-probe janitor [00.00.01-00.00.02] (0123456789abcdef0123456789abcdef).mkv.partial'),
        path.join(admin, janitor.name + '.partial')
    ].map((f) => write(f, hourAgo));
    janitor.kept = [
        path.join(admin, 'api-probe janitor movie.mkv.partial'),
        path.join(admin, 'api-probe janitor [00.00.01-00.00.02].avi.partial'),
        path.join(admin, 'api-probe janitor [00.00.01-00.00.02].mkv.partial.old'),
        path.join(admin, 'api-probe janitor [00.00.01-00.00.02].MKV.partial'),
        path.join(admin, 'api-probe janitor deeper', 'api-probe janitor [00.00.01-00.00.02].mkv.partial'),
        path.join(workDir, 'janitor-outside', 'api-probe janitor [00.00.01-00.00.02].mkv.partial')
    ].map((f) => write(f, hourAgo));
    janitor.newer = write(path.join(admin, 'api-probe janitor newer [00.00.01-00.00.02].mkv.partial'), tomorrow);
    janitor.earlier = earlier.filter((f) => !janitor.stale.some((s) => s.toLowerCase() === f.toLowerCase()));
    e.files = { stale: janitor.stale.map((f) => path.basename(f)), kept: janitor.kept.map((f) => path.relative(sandboxRoot, f)), newer: path.basename(janitor.newer), earlier: janitor.earlier.map((f) => path.basename(f)) };
}

async function janitorChecksAfterStart(janitor) {
    const { e } = janitor;
    const line = await waitForLog(/Cleaned up the unfinished clip files \(\.partial\) of clip jobs from before the last shutdown: \d+ deleted/, 60);
    const counts = line && /: (\d+) deleted, (\d+) could not be deleted, (\d+) written since the server started kept, in (\d+) clip folders \((\d+) not there\)/.exec(line);
    e.log = line && line.slice(line.indexOf('Cleaned up'), line.indexOf('Cleaned up') + 300);
    e.counts = counts && { deleted: Number(counts[1]), notDeleted: Number(counts[2]), newer: Number(counts[3]), folders: Number(counts[4]), notThere: Number(counts[5]) };
    e.after = {
        staleLeft: janitor.stale.filter((f) => fs.existsSync(f)).map((f) => path.basename(f)),
        earlierLeft: janitor.earlier.filter((f) => fs.existsSync(f)).map((f) => path.basename(f)),
        keptGone: janitor.kept.filter((f) => !fs.existsSync(f)).map((f) => path.basename(f)),
        newerKept: fs.existsSync(janitor.newer)
    };
    check('stale .partial files after a restart: the start deleted the stale .partial files named as clips being written in the clip folder and the per-user folder (with " (2)", with the hex fallback, and the queued job\'s) and any an earlier run left, and nothing else: another program\'s name, another extension, upper case, a longer extension, two folders down, outside the clip folders and a file written since the start all stay; the counts at Information',
        e.rootRecorded && !!counts && e.after.staleLeft.length === 0 && e.after.earlierLeft.length === 0 && e.after.keptGone.length === 0 && e.after.newerKept
        && e.counts.deleted === janitor.stale.length + janitor.earlier.length && e.counts.notDeleted === 0 && e.counts.newer === 1 && e.counts.folders >= 1,
    { counts: e.counts, after: e.after, rootRecorded: e.rootRecorded });

    // The worker resumes the jobs right after the clean-up and the pending-delete retry: nothing is cancelled before that.
    const resumedLine = await waitForLog(/Resuming \d+ clip jobs from before the last shutdown/, 60);
    const log = serverLogSinceStart().split(/\r?\n/);
    const cleaned = log.findIndex((l) => l.includes('Cleaned up the unfinished clip files (.partial)'));
    const resumed = log.findIndex((l) => /Resuming \d+ clip jobs from before the last shutdown/.test(l));
    const resumedJobs = resumedLine ? Number(/Resuming (\d+) clip jobs/.exec(resumedLine)[1]) : 0;
    e.order = { cleaned, resumed, resumedJobs };
    check('stale .partial files: the clean-up ran before the interrupted clip jobs resumed (the probe\'s three among them)', cleaned >= 0 && resumed > cleaned && resumedJobs >= 3, e.order);

    // The long jobs are cancelled, so the short one runs next: under its own name, since its stale .partial is gone.
    for (const b of janitor.long) await call('admin', 'POST', `/Bookmarks/${b.Id}/Clip/Cancel`);
    const done = (await waitForClip('admin', janitor.resumed.Id, 'the resumed clip')).bookmark;
    const folder = path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'clips', 'admin');
    const stem = janitor.name.replace(/\.mkv$/, '');
    const twins = fs.readdirSync(folder).filter((f) => f.startsWith(stem + ' (') && f.endsWith('.mkv'));
    e.resumed = { status: done.Clip.Status, fileName: done.Clip.FileName, expected: janitor.name, onDisk: fs.existsSync(path.join(folder, janitor.name)), twins, partialLeft: fs.existsSync(path.join(folder, janitor.name + '.partial')) };
    check('stale .partial files (a server crash mid-encode): the job queued before the stop resumed and completed under its own name, not " (2)", with no .partial left',
        done.Clip.Status === 'Completed' && done.Clip.FileName === janitor.name && e.resumed.onDisk && twins.length === 0 && !e.resumed.partialLeft, e.resumed);
    const removed = [];
    for (const b of [...janitor.long, janitor.resumed]) removed.push((await call('admin', 'DELETE', `/Bookmarks/${b.Id}`, { query: { deleteClip: 'true' } })).status);
    check('stale .partial files, clean-up: the three bookmarks delete with their clips (204)', removed.every((s) => s === 204), removed);
    const errors = serverLogSinceStart().split(/\r?\n/).filter((l) => /\[ERR\]/.test(l) && /Bookmarks/.test(l));
    check('stale .partial files: the server log of the new start has no Error from Bookmarks', errors.length === 0, errors.slice(0, 5));
}

// The janitor checks' files that must stay (and any stale one a failed check left), removed by the probe itself.
function removeProbeFiles(janitor) {
    for (const f of [...(janitor.kept || []), ...(janitor.stale || [])].concat(janitor.newer ? [janitor.newer] : [])) fs.rmSync(f, { force: true });
    const admin = path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'clips', 'admin');
    for (const folder of [path.join(admin, 'api-probe janitor deeper'), path.join(workDir, 'janitor-outside')]) {
        if (fs.existsSync(folder) && fs.readdirSync(folder).length === 0) fs.rmdirSync(folder);
    }
}

// Runs one of the repository's sandbox scripts. Its output goes to a file, not a pipe: the server dev-server.ps1 starts in
// the background inherits the script's handles, and a pipe would stay open until that server exits.
function runScript(script, args, label) {
    const started = Date.now();
    fs.mkdirSync(workDir, { recursive: true });
    const logFile = path.join(workDir, `script-${label}.log`);
    const log = fs.openSync(logFile, 'w');
    let r;
    try {
        r = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repoRoot, 'scripts', script), ...args], {
            cwd: repoRoot, stdio: ['ignore', log, log], timeout: 900000, windowsHide: true
        });
    } finally {
        fs.closeSync(log);
    }
    const tail = fs.readFileSync(logFile, 'utf8').trim().split(/\r?\n/).slice(-12).join('\n');
    if (!opts.keepFiles) fs.rmSync(logFile, { force: true });
    if (r.status !== 0) throw new Error(`${script} ${args.join(' ')} exited ${r.status}${r.error ? ` (${r.error.message})` : ''}:\n${tail}`);
    evidence.restarts.push({ label, seconds: Math.round((Date.now() - started) / 100) / 10 });
}

// The log entries of a log text at a level or above ('INF': [INF], [WRN], [ERR] and [FTL]), each with its continuation
// lines (an exception's).
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

// The stored record of a bookmark as bookmarks.json holds it (read only), or null.
function storedRecord(id) {
    const document = readJson(storeFile);
    return (document.Bookmarks || []).find((r) => guid(r.Id) === guid(id)) || null;
}

// A range bookmark of admin's with a finished clip of the given mode, made through the API.
async function bookmarkWithClip(movie, startSeconds, endSeconds, note, mode) {
    const created = await call('admin', 'POST', '/Bookmarks', { body: { ItemId: movie.id, StartTicks: startSeconds * TICKS, EndTicks: endSeconds * TICKS, Note: note } });
    if (created.status !== 201) throw new Error(`the bookmark "${note}" could not be created: ${created.status}`);
    const queued = await call('admin', 'POST', `/Bookmarks/${created.json.Id}/Clip`, { body: { Mode: mode } });
    if (queued.status !== 202) throw new Error(`the clip of "${note}" could not be queued: ${queued.status}`);
    const made = await waitForClip('admin', created.json.Id, note);
    return made.bookmark;
}

// A clip file's title tag, as `ffprobe -show_entries format_tags=title` shows it, or null; undefined without
// the sandbox's ffprobe.
function probeTitle(file) {
    if (!ffprobe || !fs.existsSync(ffprobe)) return undefined;
    const run = spawnSync(ffprobe, ['-v', 'error', '-show_entries', 'format_tags=title', '-of', 'json', file], { encoding: 'utf8', windowsHide: true, timeout: 60000 });
    if (run.status !== 0) return null;
    const tags = (JSON.parse(run.stdout).format || {}).tags || {};
    const key = Object.keys(tags).find((k) => k.toLowerCase() === 'title');
    return key ? tags[key] : null;
}

// Downloads a bookmark's clip file into the probe's folder and reads its title tag.
async function downloadedTitle(id, fileName) {
    const download = await call('admin', 'GET', `/Bookmarks/${id}/Clip/File`, { query: { download: 'true' }, headers: { Accept: '*/*' }, raw: true });
    if (download.status !== 200) return { status: download.status };
    fs.mkdirSync(workDir, { recursive: true });
    const file = path.join(workDir, `named-${path.extname(fileName).slice(1)}${path.extname(fileName)}`);
    fs.writeFileSync(file, download.buffer);
    const title = probeTitle(file);
    if (!opts.keepFiles) fs.rmSync(file, { force: true });
    return { status: download.status, bytes: download.buffer.length, title };
}

// Names on a running server. Create, update (replace semantics) and POST /Bookmarks/{id}/Name
// with the name rules; the UpdatedAt the API sent is the IfUpdatedAt token, a stale one (the other tab) a 409 with nothing
// changed; a name over 100 characters a 400; a body over 64 KB a 413 ProblemDetails from Kestrel's own limit; viewer can
// name its own bookmarks but not admin's; a tombstone is 404. A named range clip's file name and title carry the name, not
// the note; a rename leaves that file as it is, and the clip made next carries the new name. Every name starts with
// "api-probe", so the run's log check covers them.
async function nameChecks(movie) {
    const logBefore = serverLogSinceStart().length;
    const names = {};
    evidence.names = names;
    const start = 50 * TICKS;
    const end = 54 * TICKS;

    // Create with a name, normalised.
    const created = await call('admin', 'POST', '/Bookmarks', { body: { ItemId: movie.id, StartTicks: start, EndTicks: end, Name: '  api-probe Bank\tjob ', Note: 'api-probe the note' } });
    const b = created.json || {};
    const stored = created.status === 201 ? storedRecord(b.Id) : null;
    check('names: POST /Bookmarks with a Name stores it normalised (201; the DTO and bookmarks.json), next to its note',
        created.status === 201 && b.Name === 'api-probe Bank job' && b.Note === 'api-probe the note' && !!stored && stored.Name === 'api-probe Bank job',
    { status: created.status, Name: b.Name, Note: b.Note, stored: stored && stored.Name });
    if (created.status !== 201) throw new Error('the named bookmark could not be created');
    const id = b.Id;
    const firstToken = b.UpdatedAt;
    names.firstToken = firstToken;

    const count = (await call('admin', 'GET', '/Bookmarks')).json.length;
    const tooLong = await call('admin', 'POST', '/Bookmarks', { body: { ItemId: movie.id, StartTicks: start, Name: 'api-probe ' + 'x'.repeat(91) } });
    const tooLongRename = await call('admin', 'POST', `/Bookmarks/${id}/Name`, { body: { Name: 'x'.repeat(101) } });
    check('names: a name over 100 characters is 400 with ProblemDetails "Names can be at most 100 characters long." (create and rename), and nothing is created or changed',
        [tooLong, tooLongRename].every((r) => r.status === 400 && isProblem(r) && r.json.detail === 'Names can be at most 100 characters long.')
        && (await call('admin', 'GET', '/Bookmarks')).json.length === count && storedRecord(id).Name === 'api-probe Bank job',
    { create: pickProblem(tooLong), rename: pickProblem(tooLongRename) });

    // Rename with the token the API sent; then the other tab, still holding it, is refused.
    await delay(20);
    const renamed = await call('admin', 'POST', `/Bookmarks/${id}/Name`, { body: { Name: 'api-probe Vault', IfUpdatedAt: firstToken } });
    check('names: POST /Bookmarks/{id}/Name with the UpdatedAt the API sent as IfUpdatedAt renames it (200), only the name changes, and UpdatedAt moves on',
        renamed.status === 200 && renamed.json.Name === 'api-probe Vault' && renamed.json.Note === 'api-probe the note' && renamed.json.StartTicks === start
        && renamed.json.EndTicks === end && renamed.json.UpdatedAt !== firstToken && Date.parse(renamed.json.UpdatedAt) > Date.parse(firstToken),
    { status: renamed.status, Name: renamed.json && renamed.json.Name, UpdatedAt: renamed.json && renamed.json.UpdatedAt });
    const secondToken = renamed.json.UpdatedAt;
    const storeBefore = sha256(fs.readFileSync(storeFile));
    const staleRename = await call('admin', 'POST', `/Bookmarks/${id}/Name`, { body: { Name: 'api-probe other tab', IfUpdatedAt: firstToken } });
    const staleUpdate = await call('admin', 'POST', `/Bookmarks/${id}`, { body: { StartTicks: start, EndTicks: end, Name: 'api-probe other tab', IfUpdatedAt: firstToken } });
    const afterStale = await call('admin', 'GET', `/Bookmarks/${id}`);
    check('names (two tabs): a rename or update with the earlier token is 409 with ProblemDetails "Changed elsewhere. Reload to see the latest.", and bookmarks.json doesn\'t change',
        [staleRename, staleUpdate].every((r) => r.status === 409 && isProblem(r) && r.json.detail === 'Changed elsewhere. Reload to see the latest.')
        && sha256(fs.readFileSync(storeFile)) === storeBefore && afterStale.json.Name === 'api-probe Vault',
    { rename: pickProblem(staleRename), update: pickProblem(staleUpdate), name: afterStale.json && afterStale.json.Name });

    // Replace semantics: an update without Name clears it; a JavaScript Date's form of the token is accepted.
    const cleared = await call('admin', 'POST', `/Bookmarks/${id}`, { body: { StartTicks: start, EndTicks: end, Note: 'api-probe the note', IfUpdatedAt: secondToken } });
    check('names: POST /Bookmarks/{id} without Name clears the name (replace semantics; the JSON leaves the null out), with the current token',
        cleared.status === 200 && !('Name' in cleared.json) && cleared.json.Note === 'api-probe the note' && !('Name' in storedRecord(id)),
    { status: cleared.status, body: cleared.json && { Name: cleared.json.Name, Note: cleared.json.Note } });
    const jsToken = new Date(cleared.json.UpdatedAt).toISOString();
    const named = await call('admin', 'POST', `/Bookmarks/${id}`, { body: { StartTicks: start, EndTicks: end, Name: 'api-probe Bank job', Note: 'api-probe the note', IfUpdatedAt: jsToken } });
    check('names: the current token as a JavaScript Date writes it (milliseconds only) is accepted',
        named.status === 200 && named.json.Name === 'api-probe Bank job', { token: cleared.json.UpdatedAt, sent: jsToken, status: named.status });
    const unnamed = await call('admin', 'POST', `/Bookmarks/${id}/Name`, { body: { Name: ' \r\n ' } });
    const renamedBack = await call('admin', 'POST', `/Bookmarks/${id}/Name`, { body: { Name: 'api-probe Bank job' } });
    check('names: a rename to white space leaves it unnamed; without a token the last writer wins, as in 1.0',
        unnamed.status === 200 && !('Name' in unnamed.json) && renamedBack.status === 200 && renamedBack.json.Name === 'api-probe Bank job',
    { unnamed: unnamed.status, back: renamedBack.status });

    // Bookmarks 1.0's update body: neither Name nor IfUpdatedAt, as a browser tab still running 1.0's script
    // sends it. It keeps the name and replaces the rest; an explicit "Name": null still clears it. The note is put back.
    const from10 = await call('admin', 'POST', `/Bookmarks/${id}`, { body: { StartTicks: start, EndTicks: end, Note: null } });
    const from10Stored = from10.status === 200 ? storedRecord(id) : null;
    const nulled = await call('admin', 'POST', `/Bookmarks/${id}`, { body: { StartTicks: start, EndTicks: end, Name: null, Note: 'api-probe the note' } });
    const restored = await call('admin', 'POST', `/Bookmarks/${id}`, { body: { StartTicks: start, EndTicks: end, Name: 'api-probe Bank job', Note: 'api-probe the note' } });
    check('names: Bookmarks 1.0\'s update body (neither Name nor IfUpdatedAt, as a tab still running 1.0\'s script sends it) keeps the name and replaces the rest (its null note clears the note, in the DTO and bookmarks.json); an explicit "Name": null without a token clears the name',
        from10.status === 200 && from10.json.Name === 'api-probe Bank job' && !('Note' in from10.json)
        && !!from10Stored && from10Stored.Name === 'api-probe Bank job' && !('Note' in from10Stored)
        && nulled.status === 200 && !('Name' in nulled.json) && nulled.json.Note === 'api-probe the note'
        && restored.status === 200 && restored.json.Name === 'api-probe Bank job' && restored.json.Note === 'api-probe the note',
    { from10: from10.status, name: from10.json && from10.json.Name, note: from10.json && from10.json.Note, nulled: nulled.status, nulledName: nulled.json && nulled.json.Name, restored: restored.status });

    // A body over 64 KB: Kestrel stops reading it (the actions' [RequestSizeLimit]), the kit's filter answers 413.
    const beforeHuge = sha256(fs.readFileSync(storeFile));
    let huge;
    try {
        huge = await call('admin', 'POST', `/Bookmarks/${id}/Name`, { body: { Name: 'api-probe ' + 'x'.repeat(70 * 1024) } });
    } catch (e) {
        huge = { status: 0, type: '', json: null, error: String(e && e.message) };
    }
    const afterHuge = await call('admin', 'GET', `/Bookmarks/${id}`);
    await delay(500);
    const errors = logEntriesAtOrAbove(serverLogSinceStart().slice(logBefore), 'ERR');
    names.oversizedBody = { status: huge.status, type: huge.type, body: huge.json, error: huge.error, serverErrors: errors.length };
    check('names: a body over 64 KB is 413 with ProblemDetails "The request is too large." (not Jellyfin\'s 500 "Error processing request" with an Error in the log), and nothing changes',
        huge.status === 413 && isProblem(huge) && huge.json.detail === 'The request is too large.' && sha256(fs.readFileSync(storeFile)) === beforeHuge
        && afterHuge.status === 200 && afterHuge.json.Name === 'api-probe Bank job' && errors.length === 0,
    { ...names.oversizedBody, errors: errors.slice(0, 3).map((e) => e.text.slice(0, 300)) });

    // Viewer: its own bookmark it may name; admin's is 404.
    const viewerOwn = await call('viewer', 'POST', '/Bookmarks', { body: { ItemId: movie.id, StartTicks: 5 * TICKS, Name: 'api-probe viewer bookmark' } });
    const viewerRename = viewerOwn.json ? await call('viewer', 'POST', `/Bookmarks/${viewerOwn.json.Id}/Name`, { body: { Name: 'api-probe viewer renamed', IfUpdatedAt: viewerOwn.json.UpdatedAt } }) : null;
    const viewerOnAdmins = await call('viewer', 'POST', `/Bookmarks/${id}/Name`, { body: { Name: 'api-probe mine' } });
    check('names: viewer (no admin rights) names and renames its own bookmark; admin\'s is 404 with ProblemDetails and keeps its name',
        viewerOwn.status === 201 && viewerOwn.json.Name === 'api-probe viewer bookmark' && viewerRename && viewerRename.status === 200
        && viewerRename.json.Name === 'api-probe viewer renamed' && viewerOnAdmins.status === 404 && isProblem(viewerOnAdmins) && storedRecord(id).Name === 'api-probe Bank job',
    { create: viewerOwn.status, rename: viewerRename && viewerRename.status, onAdmins: viewerOnAdmins.status });
    if (viewerOwn.json) await call('viewer', 'DELETE', `/Bookmarks/${viewerOwn.json.Id}`);

    // The named clip: the file name and the title carry the name, not the note.
    const queued = await call('admin', 'POST', `/Bookmarks/${id}/Clip`, { body: { Mode: 'Fast' } });
    const fast = queued.status === 202 ? (await waitForClip('admin', id, 'named Fast clip')).bookmark : null;
    const itemName = b.ItemName;
    const fastName = clipFileName(itemName, 'api-probe Bank job', start, end, 'Fast');
    const fastTitle = fast ? await downloadedTitle(id, fast.Clip.FileName) : null;
    const clock = (ticks) => {
        const s = Math.floor(ticks / TICKS);
        return `${Math.floor(s / 3600)}:${String(Math.floor(s / 60) % 60).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
    };
    names.fastClip = fast && { FileName: fast.Clip.FileName, expected: fastName, title: fastTitle };
    check(`names: a named range clip is named "${fastName}" and titled "${itemName} - api-probe Bank job (${clock(start)}-${clock(end)})" (ffprobe format_tags=title), not with the note`,
        !!fast && fast.Clip.Status === 'Completed' && fast.Clip.FileName === fastName && clipFilesOnDisk('admin').includes(fastName)
        && (fastTitle.title === undefined || fastTitle.title === `${itemName} - api-probe Bank job (${clock(start)}-${clock(end)})`),
    names.fastClip);

    // A rename leaves the file as it is (a deliberate choice); the clip made next has the new name, and the previous file goes.
    const vault = await call('admin', 'POST', `/Bookmarks/${id}/Name`, { body: { Name: 'api-probe Vault', IfUpdatedAt: fast && fast.UpdatedAt } });
    check('names: renaming a bookmark with a clip keeps the clip file\'s name and the file (the clip isn\'t outdated)',
        vault.status === 200 && vault.json.Name === 'api-probe Vault' && !!vault.json.Clip && vault.json.Clip.FileName === fastName
        && vault.json.Clip.IsOutdated === false && clipFilesOnDisk('admin').includes(fastName),
    { status: vault.status, clip: vault.json && vault.json.Clip && { FileName: vault.json.Clip.FileName, IsOutdated: vault.json.Clip.IsOutdated } });
    const remade = await call('admin', 'POST', `/Bookmarks/${id}/Clip`, { body: { Mode: 'Accurate' } });
    const accurate = remade.status === 202 ? (await waitForClip('admin', id, 'renamed Accurate clip')).bookmark : null;
    const accurateName = clipFileName(itemName, 'api-probe Vault', start, end, 'Accurate');
    const accurateTitle = accurate ? await downloadedTitle(id, accurate.Clip.FileName) : null;
    names.accurateClip = accurate && { FileName: accurate.Clip.FileName, expected: accurateName, title: accurateTitle };
    check(`names: the clip made after the rename is "${accurateName}", titled with the new name, and the earlier file is gone`,
        !!accurate && accurate.Clip.Status === 'Completed' && accurate.Clip.FileName === accurateName && clipFilesOnDisk('admin').includes(accurateName)
        && !clipFilesOnDisk('admin').includes(fastName)
        && (accurateTitle.title === undefined || accurateTitle.title === `${itemName} - api-probe Vault (${clock(start)}-${clock(end)})`),
    names.accurateClip);

    // Deleted with its clip kept, the bookmark is a tombstone: the rename is 404 and changes nothing.
    const kept = await call('admin', 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'false' } });
    const tombstoneBefore = JSON.stringify(storedRecord(id));
    const tombstoneRename = await call('admin', 'POST', `/Bookmarks/${id}/Name`, { body: { Name: 'api-probe after delete' } });
    check('names: renaming a clip-only tombstone is 404 with ProblemDetails, and the tombstone keeps its name',
        kept.status === 204 && tombstoneRename.status === 404 && isProblem(tombstoneRename) && JSON.stringify(storedRecord(id)) === tombstoneBefore
        && storedRecord(id).Name === 'api-probe Vault',
    { delete: kept.status, rename: pickProblem(tombstoneRename) });
    const gone = await call('admin', 'DELETE', `/Bookmarks/${id}/Clip`);
    check('names: the tombstone\'s clip deleted (204), nothing of the named bookmark is left', gone.status === 204 && storedRecord(id) === null
        && !clipFilesOnDisk('admin').includes(accurateName), { status: gone.status });
}

// Clip-only tombstones on a running server. A bookmark deleted without its clip keeps a tombstone in
// bookmarks.json that only clip operations see: every bookmark route is 404 for it, its clip file still downloads, and
// deleting its clip removes file and record (204). A bookmark without a clip file is removed instead; deleting a live
// bookmark's clip keeps the bookmark (200). Then the race: a bookmark deleted without its clip while an Accurate remake
// runs keeps its earlier Fast file, and the remake leaves nothing behind.
async function tombstoneChecks(movie) {
    const logBefore = serverLogSinceStart().length;
    const tombstoneEvidence = {};
    evidence.tombstones = tombstoneEvidence;

    // Deleted without its clip, with a clip file.
    const kept = await bookmarkWithClip(movie, 30, 34, 'api-probe tombstone', 'Fast');
    const keptId = kept.Id;
    const keptFile = kept.Clip.FileName;
    const deleted = await call('admin', 'DELETE', `/Bookmarks/${keptId}`, { query: { deleteClip: 'false' } });
    const record = storedRecord(keptId);
    tombstoneEvidence.record = record && { DeletedUtc: record.DeletedUtc, UpdatedAt: record.UpdatedAt, Note: record.Note, Clip: record.Clip && { ...record.Clip, FilePath: path.basename(record.Clip.FilePath || '') } };
    check('tombstones: DELETE /Bookmarks/{id}?deleteClip=false of a bookmark with a clip file is 204 and keeps a clip-only tombstone in bookmarks.json (DeletedUtc = UpdatedAt, the note, the clip Completed with the file\'s range and mode and its completion time, no error or estimate), with the file on disk',
        deleted.status === 204 && !!record && !!record.DeletedUtc && record.UpdatedAt === record.DeletedUtc && record.Note === 'api-probe tombstone'
        && !!record.Clip && record.Clip.Status === 'Completed' && record.Clip.Mode === 'Fast' && !record.Clip.Error && record.Clip.EstimatedBytes === undefined
        && path.basename(record.Clip.FilePath) === keptFile && record.Clip.StartTicks === record.Clip.FileStartTicks && record.Clip.EndTicks === record.Clip.FileEndTicks
        && record.Clip.FileSize === kept.Clip.FileSize && !!kept.Clip.CompletedAt && Date.parse(record.Clip.CompletedAt) === Date.parse(kept.Clip.CompletedAt)
        && clipFilesOnDisk('admin').includes(keptFile),
    { status: deleted.status, record: tombstoneEvidence.record, completedAt: kept.Clip.CompletedAt, onDisk: clipFilesOnDisk('admin').includes(keptFile) });

    // No list shows it, and it is 404 by id.
    const byId = await call('admin', 'GET', `/Bookmarks/${keptId}`);
    const forItem = await call('admin', 'GET', '/Bookmarks', { query: { itemId: movie.id } });
    const all = await call('admin', 'GET', '/Bookmarks');
    check('tombstones: the tombstone is 404 by id (ProblemDetails) and in neither list (all, and the item\'s: the details page and the player)',
        byId.status === 404 && isProblem(byId) && forItem.status === 200 && forItem.json.every((x) => x.Id !== keptId) && all.status === 200 && all.json.every((x) => x.Id !== keptId),
    { byId: pickProblem(byId), forItem: forItem.status, all: all.status });

    // Every bookmark operation is 404 and changes nothing.
    const before = JSON.stringify(storedRecord(keptId));
    const operations = {
        update: await call('admin', 'POST', `/Bookmarks/${keptId}`, { body: { StartTicks: 30 * TICKS, EndTicks: 35 * TICKS, Note: 'changed' } }),
        remake: await call('admin', 'POST', `/Bookmarks/${keptId}/Clip`, { body: { Mode: 'Accurate' } }),
        cancel: await call('admin', 'POST', `/Bookmarks/${keptId}/Clip/Cancel`),
        deleteAgain: await call('admin', 'DELETE', `/Bookmarks/${keptId}`, { query: { deleteClip: 'false' } }),
        deleteWithClip: await call('admin', 'DELETE', `/Bookmarks/${keptId}`, { query: { deleteClip: 'true' } })
    };
    check('tombstones: update, remake, cancel and both deletes of the tombstone are 404 with ProblemDetails, and the record and its file stay as they were',
        Object.values(operations).every((r) => r.status === 404 && isProblem(r)) && JSON.stringify(storedRecord(keptId)) === before && clipFilesOnDisk('admin').includes(keptFile),
    Object.fromEntries(Object.entries(operations).map(([k, r]) => [k, r.status])));

    // Its clip file still downloads and streams, for its owner only.
    const download = await call('admin', 'GET', `/Bookmarks/${keptId}/Clip/File`, { query: { download: 'true' }, headers: { Accept: '*/*' }, raw: true });
    const ranged = await call('admin', 'GET', `/Bookmarks/${keptId}/Clip/File`, { query: { download: 'false' }, headers: { Accept: '*/*', Range: 'bytes=0-99' }, raw: true });
    check('tombstones: the tombstone\'s clip file still downloads (an attachment with the recorded size, MKV magic bytes) and streams with a Range request (206)',
        download.status === 200 && /^attachment/i.test(download.headers['content-disposition'] || '') && download.buffer.length === kept.Clip.FileSize
        && download.buffer.subarray(0, 4).toString('hex') === '1a45dfa3' && ranged.status === 206 && ranged.buffer.length === 100,
    { download: download.status, bytes: download.buffer.length, size: kept.Clip.FileSize, ranged: ranged.status });
    const otherFile = await call('viewer2', 'GET', `/Bookmarks/${keptId}/Clip/File`, { headers: { Accept: '*/*' }, raw: true });
    const otherDelete = await call('viewer2', 'DELETE', `/Bookmarks/${keptId}/Clip`);
    check('tombstones: another user (viewer2) gets 404 for the tombstone\'s clip file and for deleting it; the file stays',
        [403, 404].includes(otherFile.status) && otherDelete.status === 404 && clipFilesOnDisk('admin').includes(keptFile) && !!storedRecord(keptId),
    { clipFile: otherFile.status, deleteClip: otherDelete.status });

    // Deleting the tombstone's clip removes file and record.
    const deleteClip = await call('admin', 'DELETE', `/Bookmarks/${keptId}/Clip`);
    check('tombstones: DELETE /Bookmarks/{id}/Clip of the tombstone is 204 without a body, removes the record from bookmarks.json and the file from disk',
        deleteClip.status === 204 && deleteClip.buffer.length === 0 && storedRecord(keptId) === null && !clipFilesOnDisk('admin').includes(keptFile),
    { status: deleteClip.status, record: storedRecord(keptId) !== null, onDisk: clipFilesOnDisk('admin').includes(keptFile) });
    const fileAfter = await call('admin', 'GET', `/Bookmarks/${keptId}/Clip/File`, { headers: { Accept: '*/*' }, raw: true });
    const deleteClipAgain = await call('admin', 'DELETE', `/Bookmarks/${keptId}/Clip`);
    check('tombstones: afterwards its clip file and deleting it again are 404', fileAfter.status === 404 && deleteClipAgain.status === 404,
        { clipFile: fileAfter.status, deleteClip: deleteClipAgain.status });

    // Step 3: without a clip file, deleting without the clip removes the record.
    const point = await call('admin', 'POST', '/Bookmarks', { body: { ItemId: movie.id, StartTicks: 12 * TICKS, Note: 'api-probe no clip' } });
    const pointDeleted = point.json ? await call('admin', 'DELETE', `/Bookmarks/${point.json.Id}`, { query: { deleteClip: 'false' } }) : null;
    check('tombstones: a bookmark without a clip file, deleted without its clip, is removed (no tombstone in bookmarks.json)',
        point.status === 201 && pointDeleted && pointDeleted.status === 204 && storedRecord(point.json.Id) === null,
    { create: point.status, delete: pointDeleted && pointDeleted.status });

    // The same for a live bookmark: the bookmark stays without its clip (as in 1.0).
    const live = await bookmarkWithClip(movie, 40, 44, 'api-probe live clip', 'Fast');
    const liveClip = await call('admin', 'DELETE', `/Bookmarks/${live.Id}/Clip`);
    const liveAfter = await call('admin', 'GET', `/Bookmarks/${live.Id}`);
    check('tombstones: DELETE /Bookmarks/{id}/Clip of a live bookmark is 200 with the bookmark without its clip, which is still there; the file is gone',
        liveClip.status === 200 && liveClip.json && liveClip.json.Id === live.Id && !liveClip.json.Clip && liveAfter.status === 200 && !liveAfter.json.Clip
        && !clipFilesOnDisk('admin').includes(live.Clip.FileName),
    { status: liveClip.status, clip: liveClip.json && liveClip.json.Clip, read: liveAfter.status });
    await call('admin', 'DELETE', `/Bookmarks/${live.Id}`, { query: { deleteClip: 'true' } });

    // The race: deleted without its clip while an Accurate remake of 90 s runs.
    const race = await bookmarkWithClip(movie, 60, 150, 'api-probe race', 'Fast');
    const earlier = { name: race.Clip.FileName, size: race.Clip.FileSize };
    const remake = await call('admin', 'POST', `/Bookmarks/${race.Id}/Clip`, { body: { Mode: 'Accurate' } });
    let statusAtDelete = remake.json && remake.json.Clip && remake.json.Clip.Status;
    for (const until = Date.now() + 10000; statusAtDelete !== 'Running' && Date.now() < until;) {
        await delay(100);
        const r = await call('admin', 'GET', `/Bookmarks/${race.Id}`);
        statusAtDelete = r.json && r.json.Clip && r.json.Clip.Status;
    }
    const partialsWhileRunning = clipFilesOnDisk('admin').filter((f) => f.includes('api-probe race') && f.endsWith('.partial'));
    const raceDeleted = await call('admin', 'DELETE', `/Bookmarks/${race.Id}`, { query: { deleteClip: 'false' } });
    // The worker stops the job: wait for its .partial to go (it is deleted when ffmpeg is stopped).
    for (const until = Date.now() + 30000; clipFilesOnDisk('admin').some((f) => f.includes('api-probe race') && f.endsWith('.partial')) && Date.now() < until;) await delay(200);
    await delay(2000);
    const raceRecord = storedRecord(race.Id);
    const raceFiles = clipFilesOnDisk('admin').filter((f) => f.includes('api-probe race'));
    const raceDownload = await call('admin', 'GET', `/Bookmarks/${race.Id}/Clip/File`, { headers: { Accept: '*/*' }, raw: true });
    tombstoneEvidence.race = { statusAtDelete, partialsWhileRunning, files: raceFiles, earlier, clip: raceRecord && raceRecord.Clip && { ...raceRecord.Clip, FilePath: path.basename(raceRecord.Clip.FilePath || '') } };
    check('tombstones, race: deleted without its clip while the Accurate remake was Running, the bookmark keeps its earlier Fast file as a tombstone (Completed, Fast, the Fast file\'s size, no completion time: the remake\'s isn\'t the file\'s), the remake leaves no file or .partial, and the kept file downloads',
        remake.status === 202 && statusAtDelete === 'Running' && raceDeleted.status === 204 && !!raceRecord && !!raceRecord.DeletedUtc
        && raceRecord.Clip.Status === 'Completed' && raceRecord.Clip.Mode === 'Fast' && path.basename(raceRecord.Clip.FilePath) === earlier.name
        && raceRecord.Clip.FileSize === earlier.size && raceRecord.Clip.CompletedAt == null && JSON.stringify(raceFiles) === JSON.stringify([earlier.name])
        && raceDownload.status === 200 && raceDownload.buffer.length === earlier.size,
    tombstoneEvidence.race);
    const raceCleanup = await call('admin', 'DELETE', `/Bookmarks/${race.Id}/Clip`);
    check('tombstones, race clean-up: DELETE /Clip of that tombstone is 204 and leaves no file of it', raceCleanup.status === 204 && storedRecord(race.Id) === null
        && !clipFilesOnDisk('admin').some((f) => f.includes('api-probe race')), { status: raceCleanup.status });

    // No Error from the plugin meanwhile; the worker's lines about the stopped remake, as evidence.
    const log = serverLogSinceStart().slice(logBefore);
    const errors = log.split(/\r?\n/).filter((l) => /\[ERR\]/.test(l) && /Bookmarks/.test(l));
    tombstoneEvidence.workerLog = log.split(/\r?\n/)
        .filter((l) => /ClipExtractionService/.test(l) && l.replace(/-/g, '').toLowerCase().includes(guid(race.Id)))
        .map((l) => l.slice(0, 300));
    check('tombstones: the server log has no Error from Bookmarks during the tombstone checks', errors.length === 0, errors.slice(0, 5));
}

// The name and note Bookmarks' 1→2 upgrade makes of a 1.0 note (Storage/Migrations/
// BookmarkStoreMigration1To2.cs): a note that is non-blank, at most 100 characters once trimmed and on one line becomes the
// name, put through the name rules; any other note stays the note.
function upgradedNameAndNote(note) {
    if (typeof note !== 'string') return { Name: null, Note: null };
    const trimmed = note.trim();
    if (!trimmed || trimmed.length > 100 || /[\r\n]/.test(trimmed)) return { Name: null, Note: note };
    const name = trimmed.normalize('NFC').replace(/[\r\n\t]/g, ' ').replace(/[\u0000-\u001f\u007f-\u009f]/g, '').replace(/ {2,}/g, ' ').trim();
    return name && name.length <= 100 ? { Name: name, Note: null } : { Name: null, Note: note };
}

// The recorded clip folders: the default clip folder, the configured one if set, then the folder of every clip file outside those
// (shortest first), compared ignoring case on Windows, on whole path segments.
function expectedClipRoots(clipFiles) {
    const windows = process.platform === 'win32';
    const key = (p) => (windows ? p.toLowerCase() : p);
    const norm = (p) => path.resolve(p.trim()).replace(/[\\/]+$/, '');
    const under = (p, root) => key(p) === key(root) || key(p).startsWith(key(root) + path.sep);
    const roots = [];
    const add = (p) => {
        if (!p || !p.trim()) return;
        const r = norm(p);
        if (!roots.some((x) => key(x) === key(r))) roots.push(r);
    };
    add(path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'clips'));
    const settings = path.join(sandboxRoot, 'data', 'plugins', 'configurations', 'Jellyfin.Plugin.Bookmarks.xml');
    const configured = fs.existsSync(settings) ? /<ClipOutputPath>([^<]*)<\/ClipOutputPath>/.exec(fs.readFileSync(settings, 'utf8')) : null;
    add(configured ? configured[1] : null);
    const folders = [...new Set(clipFiles.map((f) => path.dirname(norm(f))))]
        .sort((a, b) => a.length - b.length || (a < b ? -1 : a > b ? 1 : 0));
    for (const folder of folders) {
        if (!roots.some((r) => under(folder, r))) add(folder);
    }
    return roots;
}

const byKey = ([a], [b]) => (a < b ? -1 : a > b ? 1 : 0);

// The kit store's 503 messages while it is read-only (kit/src/Storage/StoreMessages.cs).
const READ_ONLY_MESSAGES = {
    NewerVersion: 'Data was written by a newer plugin version; install that version or restore an export.',
    MigrationFailed: "Stored data couldn't be upgraded; nothing was changed. Install the previous plugin version or restore an export."
};

// The sandbox server's log since its last start (the newest log file, from its last "Jellyfin version" line).
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

async function waitForLog(pattern, seconds) {
    const deadline = Date.now() + seconds * 1000;
    for (;;) {
        const line = serverLogSinceStart().split(/\r?\n/).find((l) => pattern.test(l));
        if (line || Date.now() > deadline) return line || null;
        await delay(500);
    }
}

function storeFolderFiles() {
    return fs.readdirSync(path.dirname(storeFile)).filter((f) => /^bookmarks\.json/.test(f)).sort();
}

// Read-only data: the checks a newer schema's file and a 1.0 file whose upgrade fails share. Every write is
// 503 with the store's message as ProblemDetails, the clip worker says once that it is idle and runs no job, downloads
// aren't refused, and neither the file nor its folder changes.
async function readOnlyChecks({ reason, fixtureBytes, fixture, records, fileRecords = records }) {
    const message = READ_ONLY_MESSAGES[reason];
    const onDisk = () => fs.readFileSync(storeFile);
    const filesBefore = storeFolderFiles();

    for (const who of ['admin', 'viewer']) {
        const caps = await call(who, 'GET', '/Bookmarks/Capabilities');
        check(`${who} Capabilities: StoreReadOnly with the reason ${reason}`, caps.status === 200 && caps.json.StoreReadOnly === true
            && caps.json.StoreReadOnlyReason === reason, caps.json);
    }

    // The worker: after the startup gate it logs once that it is idle, and a queued or interrupted job stays as it was.
    const idle = await waitForLog(new RegExp(`BackgroundWriteGate: The bookmark data is read-only \\(${reason}\\), so ClipWorker is idle`), 60);
    check('the server log says once that the clip worker is idle (BackgroundWriteGate)', !!idle
        && serverLogSinceStart().split(/\r?\n/).filter((l) => l.includes('so ClipWorker is idle')).length === 1, { line: idle });
    check('the server log has no Error from the clip worker', !/\[ERR\][^\n]*Jellyfin\.Plugin\.Bookmarks\.Clips/.test(serverLogSinceStart()));
    const jobs = records.filter((r) => r.Clip && ['Queued', 'Running'].includes(r.Clip.Status));
    for (const record of jobs) {
        const owner = Object.entries(users).find(([, u]) => guid(u.id) === guid(record.UserId));
        const r = owner ? await call(owner[0], 'GET', `/Bookmarks/${record.Id}`) : null;
        check(`the ${record.Clip.Status} clip job of ${record.Id} is still ${record.Clip.Status} (no worker ran it)`,
            r && r.status === 200 && r.json.Clip && r.json.Clip.Status === record.Clip.Status, r ? { status: r.status, clip: r.json && r.json.Clip } : 'no owner');
    }

    // Writes: 503 with the store's message. The ids are the file's own (fileRecords): after a failed upgrade the store lists
    // none of them, while a page loaded before the upgrade still names them; changing one is 503 too, never 404 "Bookmark
    // not found." So is an id the file doesn't hold (the same answer for every id).
    const movie = await findMovie();
    const writes = [['admin', 'POST', '/Bookmarks', { ItemId: movie.id, StartTicks: 20 * TICKS, EndTicks: 26 * TICKS, Note: 'read-only probe' }, '']];
    const target = fileRecords.find((r) => guid(r.UserId) === guid(users.admin.id) && r.EndTicks && r.Clip);
    const viewerRecord = fileRecords.find((r) => guid(r.UserId) === guid(users.viewer.id));
    check('the file holds a bookmark of admin\'s with a range and a clip, and one of viewer\'s, for the writes below', !!target && !!viewerRecord,
        { admin: target && target.Id, viewer: viewerRecord && viewerRecord.Id });
    for (const [what, bookmark] of [['{id}', target], ['{unknown id}', { Id: crypto.randomUUID(), StartTicks: 0, EndTicks: null, UpdatedAt: null }]]) {
        if (!bookmark) continue;
        const range = { StartTicks: bookmark.StartTicks, EndTicks: bookmark.EndTicks ?? null };
        writes.push(
            ['admin', 'POST', `/Bookmarks/${bookmark.Id}`, { ...range, Note: 'changed' }, what],
            ['admin', 'POST', `/Bookmarks/${bookmark.Id}`, { ...range, Name: 'read-only probe', IfUpdatedAt: bookmark.UpdatedAt ?? null }, what + ' (with Name and IfUpdatedAt)'],
            ['admin', 'POST', `/Bookmarks/${bookmark.Id}/Name`, { Name: 'read-only probe' }, what],
            ['admin', 'POST', `/Bookmarks/${bookmark.Id}/Clip`, { Mode: 'Fast' }, what],
            ['admin', 'POST', `/Bookmarks/${bookmark.Id}/Clip/Cancel`, undefined, what],
            ['admin', 'DELETE', `/Bookmarks/${bookmark.Id}/Clip`, undefined, what],
            ['admin', 'DELETE', `/Bookmarks/${bookmark.Id}?deleteClip=true`, undefined, what],
            ['admin', 'DELETE', `/Bookmarks/${bookmark.Id}?deleteClip=false`, undefined, what]);
    }

    if (viewerRecord) writes.push(['viewer', 'POST', `/Bookmarks/${viewerRecord.Id}`, { StartTicks: viewerRecord.StartTicks, EndTicks: viewerRecord.EndTicks ?? null, Note: 'changed' }, '{id}']);
    for (const [who, method, p, body, what] of writes) {
        const r = await call(who, method, p, { body });
        check(`${who} ${method} ${p.replace(/[0-9a-f]{8}-[0-9a-f-]{27}/, what)}: 503 with the store's message`, r.status === 503 && isProblem(r) && r.json.detail === message, pickProblem(r));
    }

    // Downloads aren't writes: 200 when the clip file is there, 404 when it isn't, never 503.
    for (const record of records.filter((r) => r.Clip && typeof r.Clip.FilePath === 'string')) {
        const owner = Object.entries(users).find(([, u]) => guid(u.id) === guid(record.UserId));
        if (!owner || owner[0] === 'viewer') continue;
        const r = await call(owner[0], 'GET', `/Bookmarks/${record.Id}/Clip/File`, { query: { download: 'true' }, raw: true });
        const expected = fs.existsSync(record.Clip.FilePath) ? 200 : 404;
        check(`the clip file of ${record.Id} can still be downloaded (${expected === 200 ? 'on disk: 200' : 'not on disk: 404'}, never 503)`, r.status === expected, { status: r.status });
    }

    // Nothing changed on disk: the file, and no .bak, .tmp, .corrupt-* or new copy next to it.
    check('bookmarks.json is still the fixture byte for byte', sha256(onDisk()) === sha256(fixtureBytes), { fixture: sha256(fixtureBytes), file: sha256(onDisk()) });
    check('no file appeared or went next to bookmarks.json (.bak, .tmp, .corrupt-*, .v<N>-*)', JSON.stringify(storeFolderFiles()) === JSON.stringify(filesBefore),
        { before: filesBefore, after: storeFolderFiles() });
    const snapshots = path.join(path.dirname(storeFile), 'snapshots');
    const started = Date.parse(state.StartedUtc || 0);
    const fresh = fs.existsSync(snapshots) ? fs.readdirSync(snapshots).filter((f) => /^bookmarks-/.test(f) && fs.statSync(path.join(snapshots, f)).mtimeMs >= started) : [];
    check('no snapshot of bookmarks.json was written since the start', fresh.length === 0, { fresh });
    evidence.readOnly = { reason, fixtureSha256: sha256(fixtureBytes), files: storeFolderFiles(), idle, fixtureVersion: fixture.Version ?? 1 };
}

// --restart with read-only data. Stale .partial files named as clips being written, in the sandbox's clip
// folder and its per-user folder, are all still there after a restart, and the start logs no clean-up, only that the clip
// worker is idle; bookmarks.json doesn't change. A newer schema's file must record that clip folder, so the files are
// ones the janitor would delete if it ran: the run shows the janitor skipped. After a failed upgrade (MigrationFailed) the
// store serves an empty stand-in with no clip folder, where the janitor would find nothing to delete anyway, and the clip
// worker returns before calling it: that run checks the worker's gate only, and says so. The janitor's own gate is
// Clips/PartialFileJanitorTests' ReadOnlyData_KeepsTheJanitorOutOfEveryClipFolder, for both reasons.
async function readOnlyJanitorAfterRestart(fixtureBytes, reason) {
    evidence.restarts = [];
    const e = {};
    evidence.janitor = e;
    const root = path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'clips');
    e.rootRecorded = (readJson(storeFile).ClipRoots || []).some((r) => r.replace(/[\\/]+$/, '').toLowerCase() === root.toLowerCase());
    const hourAgo = new Date(Date.now() - 3600 * 1000);
    const stale = [
        path.join(root, 'api-probe janitor read-only [00.00.01-00.00.02].mkv.partial'),
        path.join(root, 'admin', 'api-probe janitor read-only [00.00.01-00.00.02] (2).mp4.partial')
    ];
    for (const file of stale) {
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.writeFileSync(file, 'api-probe janitor: half a clip');
        fs.utimesSync(file, hourAgo, hourAgo);
    }
    try {
        runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox], 'stop');
        runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'], 'start');
        await waitUntilServing();
        const idle = await waitForLog(new RegExp(`BackgroundWriteGate: The bookmark data is read-only \\(${reason}\\), so ClipWorker is idle`), 60);
        await delay(3000);
        const log = serverLogSinceStart();
        e.after = { left: stale.filter((f) => fs.existsSync(f)).map((f) => path.basename(f)), cleanedUp: log.includes('Cleaned up the unfinished clip files'), idle: idle && idle.slice(idle.indexOf('The bookmark'), idle.indexOf('The bookmark') + 120) };
        const kept = e.after.left.length === stale.length && !e.after.cleanedUp && !!idle;
        if (reason === 'MigrationFailed') {
            check('stale .partial files, read-only data (MigrationFailed), the clip worker\'s gate only: after a restart the clip worker is idle, the stale .partial files in the clip folder and its per-user folder are all still there and the start logged no clean-up (the store\'s empty stand-in records no clip folder, so this doesn\'t show the janitor\'s own gate; its unit tests do)',
                kept, { ...e.after, rootRecorded: e.rootRecorded });
        } else {
            check(`stale .partial files, read-only data (${reason}): the file records the sandbox's clip folder, and after a restart the stale .partial files in it and its per-user folder are all still there; the start logged no clean-up, only that the clip worker is idle`,
                e.rootRecorded && kept, { ...e.after, rootRecorded: e.rootRecorded });
        }
        check('stale .partial files, read-only data after the restart: bookmarks.json is still the fixture byte for byte', sha256(fs.readFileSync(storeFile)) === sha256(fixtureBytes));
    } finally {
        for (const file of stale) fs.rmSync(file, { force: true });
    }
}

// A bookmarks.json of a newer schema (a Version=3 fixture: read-only, the clip worker idle): shown
// read-only as far as this version reads it, never written.
async function newerFixtureRun(fixtureBytes, fixture) {
    check('the fixture is a newer schema than this build reads (above 2)', fixture.Version > 2 && Array.isArray(fixture.Bookmarks),
        { version: fixture.Version, records: fixture.Bookmarks && fixture.Bookmarks.length, sha256: sha256(fixtureBytes) });

    const diag = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    const store = diag.json && diag.json.Store;
    evidence.store = store;
    check(`Diagnostics: ReadOnlyNewerVersion at schema ${fixture.Version}, this build reading up to 2, nothing kept`, !!store
        && store.Status === 'ReadOnlyNewerVersion' && store.ReadOnly === true && store.Version === fixture.Version && store.CurrentVersion === 2
        && typeof store.Message === 'string' && store.Message.includes('newer plugin version') && store.KeptFileCount === 0, store || diag.status);

    // Reads: each user lists exactly its records, with the names, notes and clip states the file holds.
    const names = Object.fromEntries(Object.entries(users).map(([name, u]) => [guid(u.id), name]));
    for (const name of ['admin', 'viewer', 'viewer2']) {
        const expected = fixture.Bookmarks.filter((r) => names[guid(r.UserId)] === name && !r.DeletedUtc);
        const r = await call(name, 'GET', '/Bookmarks');
        const listed = r.json || [];
        const mismatches = expected.filter((e) => {
            const dto = listed.find((x) => guid(x.Id) === guid(e.Id));
            return !dto || (dto.Name ?? null) !== (e.Name ?? null) || (dto.Note ?? null) !== (e.Note ?? null)
                || (dto.Clip ? dto.Clip.Status : null) !== (e.Clip ? e.Clip.Status : null);
        }).map((e) => e.Id);
        check(`${name} lists exactly its ${expected.length} record(s) of the newer file, as the file holds them`,
            r.status === 200 && listed.length === expected.length && mismatches.length === 0, { status: r.status, listed: listed.length, mismatches });
    }

    await readOnlyChecks({ reason: 'NewerVersion', fixtureBytes, fixture, records: fixture.Bookmarks });
    if (opts.restart) await readOnlyJanitorAfterRestart(fixtureBytes, 'NewerVersion');
}

// A 1.0 file the upgrade can't convert (a failed migration), installed like any --fixture, run with
// --expect-failed-upgrade: the store is empty and read-only, the file and its one .v1-* copy stay the fixture byte for byte,
// nothing is quarantined, and the server log names the failed step.
async function failedUpgradeRun(fixtureBytes, fixture) {
    check('the fixture is a schema 1 file (Bookmarks 1.0)', (fixture.Version ?? 1) === 1, { version: fixture.Version, sha256: sha256(fixtureBytes) });

    const diag = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    const store = diag.json && diag.json.Store;
    evidence.store = store;
    const kept = (store && store.KeptFiles) || [];
    check('Diagnostics: ReadOnlyMigrationFailed at schema 1, "nothing was changed", the .v1-* copy listed', !!store
        && store.Status === 'ReadOnlyMigrationFailed' && store.ReadOnly === true && store.Version === 1 && store.CurrentVersion === 2
        && typeof store.Message === 'string' && store.Message.includes("couldn't be upgraded") && store.Message.includes('nothing was changed')
        && kept.length === 1 && kept[0].Kind === 'PreUpgradeCopy' && kept[0].SchemaVersion === 1 && /^bookmarks\.json\.v1-\d{14}$/.test(kept[0].Name),
        store || diag.status);

    const folder = path.dirname(storeFile);
    const copies = fs.readdirSync(folder).filter((f) => /^bookmarks\.json\.v1-\d{14}(-\d+)?$/.test(f));
    check('exactly one bookmarks.json.v1-<yyyyMMddHHmmss> copy, the fixture byte for byte (reused on every failing start)', copies.length === 1
        && sha256(fs.readFileSync(path.join(folder, copies[0]))) === sha256(fixtureBytes) && kept.length === 1 && kept[0].Name === copies[0], { copies });
    check('nothing was quarantined and no .bak or .tmp was written', !storeFolderFiles().some((f) => /\.corrupt-|\.bak$|\.tmp$/.test(f)), storeFolderFiles());
    // Serilog quotes the step: 'from schema 1 to 2: "the migration from version 1 to 2" failed'.
    const failed = /Could not upgrade "[^"]*bookmarks\.json" from schema 1 to 2: "?(the migration from version 1 to 2|reading the upgraded data)"? failed/;
    const line = serverLogSinceStart().split(/\r?\n/).find((l) => failed.test(l));
    check('the server log has the Error naming the failed step', !!line && line.includes('[ERR]'), { line });

    for (const name of ['admin', 'viewer', 'viewer2']) {
        const r = await call(name, 'GET', '/Bookmarks');
        check(`${name} lists no bookmarks (the store holds an empty stand-in, not a half-upgraded file)`, r.status === 200 && Array.isArray(r.json) && r.json.length === 0,
            { status: r.status, listed: r.json && r.json.length });
    }

    await readOnlyChecks({ reason: 'MigrationFailed', fixtureBytes, fixture, records: [], fileRecords: fixture.Bookmarks || [] });
    if (opts.restart) await readOnlyJanitorAfterRestart(fixtureBytes, 'MigrationFailed');
}

// A store file written by Bookmarks 1.0.0 (schema 1), installed as the sandbox's bookmarks.json while it was stopped.
// The store is schema 2, so the first start upgrades it: the probe checks the upgrade on that
// start (Diagnostics "Migrated") and on any later one ("Ok"), and that reading changes nothing. A file of a newer schema,
// or one run with --expect-failed-upgrade, is checked as read-only data instead.
async function fixtureRun() {
    const fixtureBytes = fs.readFileSync(path.resolve(opts.fixture));
    const fixture = JSON.parse(fixtureBytes.toString('utf8').replace(/^﻿/, ''));
    if ((fixture.Version ?? 1) > 2) return newerFixtureRun(fixtureBytes, fixture);
    if (opts.expectFailedUpgrade) return failedUpgradeRun(fixtureBytes, fixture);
    const folder = path.dirname(storeFile);
    const onDisk = () => fs.readFileSync(storeFile);
    check('the fixture is a schema 1 file (Bookmarks 1.0)', (fixture.Version ?? 1) === 1 && Array.isArray(fixture.Bookmarks),
        { version: fixture.Version, records: fixture.Bookmarks && fixture.Bookmarks.length, sha256: sha256(fixtureBytes) });

    // The first request loads the store, if the clip service didn't at startup.
    const caps = await call('admin', 'GET', '/Bookmarks/Capabilities');
    check('the store is writable (not read-only)', caps.status === 200 && caps.json.StoreReadOnly === false, caps.json);

    // The identity service's backfill runs after the startup gate; the file is read once it is done.
    let identity = null;
    for (const until = Date.now() + 60000; Date.now() < until; await delay(500)) {
        const d = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
        identity = d.json && d.json.Identity;
        if (identity && identity.StartupDone) break;
    }
    check('identity: the identity service finished its startup work (the move-journal catch-up and the backfill)', !!identity && identity.StartupDone === true, identity);
    evidence.backfill = identity && identity.Backfill;
    const before = sha256(onDisk());

    const copies = fs.readdirSync(folder).filter((f) => /^bookmarks\.json\.v1-\d{14}$/.test(f));
    check('exactly one pre-upgrade copy bookmarks.json.v1-<yyyyMMddHHmmss>, byte for byte the fixture', copies.length === 1
        && sha256(fs.readFileSync(path.join(folder, copies[0]))) === sha256(fixtureBytes), { copies });
    // The .bak is the file before the last save: the fixture after the upgrade's save, and with the identity service the upgraded file once
    // the backfill has written (schema 2, the fixture's records; the kept .v1-* copy above stays the fixture).
    const bak = path.join(folder, 'bookmarks.json.bak');
    const bakBytes = fs.existsSync(bak) ? fs.readFileSync(bak) : null;
    let bakDoc = null;
    try { bakDoc = bakBytes && JSON.parse(bakBytes.toString('utf8').replace(/^\uFEFF/, '')); } catch { bakDoc = null; }
    const bakIsFixture = !!bakBytes && sha256(bakBytes) === sha256(fixtureBytes);
    const bakIsUpgraded = !!bakDoc && bakDoc.Version === 2 && (bakDoc.Bookmarks || []).length === fixture.Bookmarks.length
        && fixture.Bookmarks.every((r) => (bakDoc.Bookmarks || []).some((b) => guid(b.Id) === guid(r.Id)));
    check('bookmarks.json.bak is the file before the last save: the fixture (the upgrade\'s save was the last), or the upgraded schema 2 file with the fixture\'s records (the backfill wrote after it)',
        bakIsFixture || bakIsUpgraded, { bak: bakBytes ? sha256(bakBytes) : null, bakIsFixture, bakIsUpgraded });

    const diag = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    const store = diag.json && diag.json.Store;
    const firstStart = !!store && store.Status === 'Migrated';
    evidence.store = store;
    const upgradedHere = firstStart && store.Version === 1 && typeof store.Message === 'string'
        && store.Message.includes('from schema 1 to 2') && copies.length === 1 && store.Message.includes(copies[0]);
    const upgradedBefore = !!store && store.Status === 'Ok' && store.Version === 2;
    check('Diagnostics: schema 2, upgraded on this start ("Migrated" from version 1, naming the copy) or on an earlier one ("Ok")',
        !!store && store.CurrentVersion === 2 && store.ReadOnly === false && (upgradedHere || upgradedBefore), store || diag.status);

    // The upgraded file.
    const upgraded = JSON.parse(onDisk().toString('utf8').replace(/^﻿/, ''));
    const inFile = Object.fromEntries((upgraded.Bookmarks || []).map((b) => [guid(b.Id), b]));
    const recordProblems = [];
    const backfilled = [];
    for (const record of fixture.Bookmarks) {
        const b = inFile[guid(record.Id)];
        if (!b) {
            recordProblems.push({ id: record.Id, missing: true });
            continue;
        }
        const { Name, Note } = upgradedNameAndNote(record.Note);
        const problems = [];
        if ((b.Name ?? null) !== Name || (b.Note ?? null) !== Note) problems.push({ Name: b.Name ?? null, Note: b.Note ?? null, expected: { Name, Note } });
        // The upgrade's snapshot, completed by the identity service's backfill when the video is in the library.
        const item = (await call('admin', 'GET', `/Items/${record.ItemId}`, { query: { userId: users.admin.id, Fields: 'Path,ProviderIds' } })).json;
        const ids = JSON.stringify(Object.entries((item ? item.ProviderIds : record.ItemProviderIds) || {}).filter(([, v]) => v).sort(byKey));
        const identityProblem = !b.Identity || guid(b.Identity.ItemId) !== guid(record.ItemId) || b.Identity.Kind !== ((record.ItemType || '').trim() || 'Video')
            || JSON.stringify(Object.entries(b.Identity.ProviderIds || {}).sort(byKey)) !== ids
            || (item
                ? b.Identity.Name !== item.Name || b.Identity.Path !== item.Path || (b.Identity.RunTimeTicks ?? null) !== (item.RunTimeTicks ?? null) || (b.Identity.Year ?? null) !== (item.ProductionYear ?? null)
                : b.Identity.Name !== record.ItemName || 'Path' in b.Identity);
        if (identityProblem) problems.push({ Identity: b.Identity, item: item && { Name: item.Name, Path: item.Path, RunTimeTicks: item.RunTimeTicks, ProductionYear: item.ProductionYear } });
        if (item) backfilled.push(record.Id);
        if (b.Origin !== 'Native' || b.AttachedUtc !== record.CreatedAt || 'ItemType' in b || 'ItemProviderIds' in b) {
            problems.push({ Origin: b.Origin, AttachedUtc: b.AttachedUtc, left: ['ItemType', 'ItemProviderIds'].filter((k) => k in b) });
        }
        for (const k of ['UserId', 'ItemId', 'MediaSourceId', 'AudioStreamIndex', 'StartTicks', 'EndTicks', 'ItemName', 'CreatedAt', 'UpdatedAt', 'Clip']) {
            if (JSON.stringify(record[k] ?? null) !== JSON.stringify(b[k] ?? null)) problems.push({ [k]: { fixture: record[k] ?? null, file: b[k] ?? null } });
        }
        if (problems.length) recordProblems.push({ id: record.Id, problems });
    }
    check(`the upgraded bookmarks.json: schema 2 with the fixture's ${fixture.Bookmarks.length} records, their 1.0 values, short notes as names, the identity (backfilled from the video when it is in the library), Native, attached when created`,
        upgraded.Version === 2 && (upgraded.Bookmarks || []).length === fixture.Bookmarks.length && recordProblems.length === 0,
        { version: upgraded.Version, records: (upgraded.Bookmarks || []).length, backfilled: backfilled.length, recordProblems });

    const names = Object.fromEntries(Object.entries(users).map(([name, u]) => [guid(u.id), name]));
    const owners = [...new Set(fixture.Bookmarks.map((r) => guid(r.UserId)))];
    const wantKnown = Object.fromEntries(owners.filter((id) => names[id]).sort().map((id) => [id, names[id]]));
    const wantRoots = expectedClipRoots(fixture.Bookmarks.map((r) => r.Clip && r.Clip.FilePath).filter((p) => typeof p === 'string'));
    check('the upgraded document: KnownUsers names the sandbox users who own records, ClipRoots seeded, nothing else yet',
        JSON.stringify(upgraded.KnownUsers) === JSON.stringify(wantKnown) && JSON.stringify(upgraded.ClipRoots) === JSON.stringify(wantRoots)
            && JSON.stringify(upgraded.UserPreferences) === '{}' && JSON.stringify(upgraded.ImportLedger) === '{}'
            && Array.isArray(upgraded.PendingFileDeletes) && upgraded.PendingFileDeletes.length === 0 && upgraded.LastMoveSeq === 0 && !('MoveJournalId' in upgraded),
        { KnownUsers: upgraded.KnownUsers, expectedKnownUsers: wantKnown, ClipRoots: upgraded.ClipRoots, expectedClipRoots: wantRoots, LastMoveSeq: upgraded.LastMoveSeq });

    // The API: each user lists exactly its records with the 1.0 values (the note as the name when it moved), and no
    // identity snapshot reaches a DTO.
    const listedBy = {};
    for (const record of fixture.Bookmarks) {
        const owner = names[guid(record.UserId)];
        check(`fixture record ${record.Id} belongs to a sandbox user`, !!owner, { UserId: record.UserId });
        if (owner) (listedBy[owner] ||= []).push(record);
    }
    for (const name of ['admin', 'viewer', 'viewer2']) {
        const r = await call(name, 'GET', '/Bookmarks');
        const listed = r.json || [];
        const expected = listedBy[name] || [];
        const mismatches = [];
        for (const record of expected) {
            const dto = listed.find((x) => guid(x.Id) === guid(record.Id));
            if (!dto) {
                mismatches.push({ id: record.Id, missing: true });
                continue;
            }
            const { Name, Note } = upgradedNameAndNote(record.Note);
            const want = {
                ItemId: guid(record.ItemId), ItemName: record.ItemName, ItemType: record.ItemType, MediaSourceId: record.MediaSourceId ?? null,
                AudioStreamIndex: record.AudioStreamIndex ?? null, StartTicks: record.StartTicks, EndTicks: record.EndTicks ?? null, Name, Note,
                CreatedAt: Date.parse(record.CreatedAt), UpdatedAt: Date.parse(record.UpdatedAt),
                ClipStatus: record.Clip ? record.Clip.Status : null, ClipMode: record.Clip ? record.Clip.Mode : null
            };
            const have = {
                ItemId: guid(dto.ItemId), ItemName: dto.ItemName, ItemType: dto.ItemType, MediaSourceId: dto.MediaSourceId ?? null,
                AudioStreamIndex: dto.AudioStreamIndex ?? null, StartTicks: dto.StartTicks, EndTicks: dto.EndTicks ?? null, Name: dto.Name ?? null, Note: dto.Note ?? null,
                CreatedAt: Date.parse(dto.CreatedAt), UpdatedAt: Date.parse(dto.UpdatedAt),
                ClipStatus: dto.Clip ? dto.Clip.Status : null, ClipMode: dto.Clip ? dto.Clip.Mode : null
            };
            const diff = Object.keys(want).filter((k) => JSON.stringify(want[k]) !== JSON.stringify(have[k]));
            if (diff.length) mismatches.push({ id: record.Id, fields: Object.fromEntries(diff.map((k) => [k, { fixture: want[k], api: have[k] }])) });
            if ('Identity' in dto || 'ProviderIds' in dto) mismatches.push({ id: record.Id, leaked: 'Identity' });
        }
        const foreign = listed.filter((x) => !expected.some((e) => guid(e.Id) === guid(x.Id))).map((x) => x.Id);
        check(`${name} lists exactly its ${expected.length} fixture record(s) with the fixture's values`, r.status === 200 && mismatches.length === 0 && foreign.length === 0,
            { status: r.status, listed: listed.length, mismatches, foreign });
    }
    check('reading changed nothing: bookmarks.json is the same after the reads', sha256(onDisk()) === before, { before, after: sha256(onDisk()) });
    evidence.upgrade = { firstStart, copy: copies[0] || null, storeSha256: before, clipRoots: upgraded.ClipRoots, knownUsers: Object.keys(upgraded.KnownUsers || {}).length };
}

let fatal = null;
try {
    if (opts.fixture) await fixtureRun();
    else if (opts.names) await namesRun();
    else await normalRun();
} catch (e) {
    fatal = e.stack || String(e);
}
if (!opts.keepFiles && fs.existsSync(workDir) && fs.readdirSync(workDir).length === 0) fs.rmdirSync(workDir);

const result = { pass: !fatal && checks.length > 0 && checks.every((c) => c.pass), fatal, checks, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text + '\n');
console.log(text);
process.exit(result.pass ? 0 : 1);
