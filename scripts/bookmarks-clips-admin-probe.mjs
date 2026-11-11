#!/usr/bin/env node
// Bookmarks' admin "All users" scope of My Clips in a running
// sandbox: GET /Bookmarks/Admin/Clips, /Summary, /Status, /{id}/File, POST /{id}/Cancel, DELETE /{id} and POST /BulkDelete
// over real clip files of another user, who may call them and who may not, and the page's scope in the web client (desktop
// and TV). Node 22 built-ins only. Loopback only, never port 8096. The users' tokens come from credentials.json and are
// never printed; the probe's own test user's password, its token and the probe's API key live in memory only, are never
// printed, and are revoked at the end (the user deleted with its devices, the key deleted).
//
//   node scripts/bookmarks-clips-admin-probe.mjs --sandbox default [--skip-webchecks] [--timeout <seconds>] [--out result.json]
//
// It seeds viewer2's bookmarks and Fast clips of Long Test Movie (2021) through the API (as viewer2), every name starting
// with "clip-admin-probe" and the first one with a note of words no name has, and one of admin's. What it changes and puts
// back: Bookmarks' VideoPreset (slow while one long clip is made and cancelled) and AdminsCanManageAllClips (off for one
// check), each saved to saved-settings.json first and put back right after (in a finally, and at the next start after a run
// was killed); one clip file moved aside into its work folder's held/ (a file gone; recorded in moves.json first, moved back
// at the end, and at the next start); a clip file held open from a PowerShell process of its own (read, sharing read and
// write but not delete, as a preview or a download holds it; the path through the environment) while the admin deletes it;
// an API key of its own (app "clip-admin-probe", deleted at the end and at the next start); and a test user of its own
// ("bm-admin-probe-user", its id in test-user.json, never a password or token; deleted at the end and at the next start)
// made an admin, demoted and disabled to see what its token may do. It never writes bookmarks.json (it reads paths there),
// deletes no file but its own saved-settings and test-user files and, once Bookmarks forgot the clip it stood for, its own
// copy in held/ of the file it moved aside (Bookmarks deletes the clip files the admin deletes), and never deploys anything.
//
// Checks:
//   1. the seed: viewer2's Ready (with its note), Kept (its bookmark deleted keeping the clip), Gone (its file moved aside),
//      Held, Bulk A and Bulk B, Web A to Web D (for the webchecks), and admin's own;
//   2. who may call: viewer (a member) 403 on every admin clip route, a request without a token 401, the probe's
//      API key admitted (the list and the summary 200); Capabilities' CanManageAllClips true for admin, false for viewer;
//   3. a token's admin rights are read at each request: the test user made an admin, its token lists every user's clips;
//      demoted, the same token 403 on every route and CanManageAllClips false; disabled (Jellyfin refuses to disable an
//      admin, so as a member), the same token 401; the user deleted;
//   4. the list (admin): viewer2's clips with their owner "viewer2", their names and full paths in the sandbox's clip folder,
//      admin's own with "admin"; never viewer2's note, not in the list, not in a search for its words (0 clips), not in
//      any admin answer of the run; the owner filter; the summary per user;
//   5. the download (admin, viewer2's Ready): 200, an attachment named as the file is (no folder), its bytes the file's; a
//      range 206; held open by a reader it still downloads; Gone 404 "This bookmark has no clip file." without a path;
//   6. a clip being made (viewer2's Making, Accurate with VideoPreset slow): the admin's delete 409 "Wait for the clip to
//      finish or cancel it first.", the admin's cancel ends it (Cancelled);
//   7. the deletes (admin): Ready 204, its file gone, viewer2's bookmark still there without a clip; Kept 204, its record
//      gone; Held held open 409 "in use" after the server's three attempts with bookmarks.json byte for byte, then 204 once
//      released; Forget (onlyIfMissing) of Bulk A, whose file is there, 409 with nothing deleted; Forget of Gone 204;
//   8. the bulk delete (admin): Bulk A, Bulk B and an unknown id: FilesDeleted 2, NotFound 1, both files gone;
//   9. AdminsCanManageAllClips off: every gated route 403 "Admins' access to other users' clips is turned off …", the
//      summary still 200, CanManageAllClips false; on again;
//  10. page-clips-admin.json (admin, then viewer; unless --skip-webchecks): the switch, "All users", the owner and the path
//      on viewer2's rows and no note on the page, the Owner select in the hash, the admin file route's Download, Delete of
//      Web A naming viewer2 with the focus on Cancel, the bulk delete of Web B and Web C; viewer sees no switch;
//  11. page-clips-admin-tv.json (admin, the TV layout): the switch by the D-pad and OK, the Owner select, Delete of Web D by
//      OK with the focus on Cancel, one ArrowDown to Delete, OK; the focus back in the list;
//  12. the server log: each admin action at Information with the admin's id, the clip's and its owner's (downloaded, cancelled,
//      deleted, forgot, deleted … at once), no Error of Bookmarks or the kit, no line of theirs at Information or above
//      naming a probe bookmark, viewer2's note or a clip folder's path;
//  13. the clean-up: the settings, the moved file, the API key and the test user as they were, no probe bookmark or clip
//      left, none of their files in the clip folder.
// (4a, viewer2's note in none of the run's admin clip answers, is checked after 11.)
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
const PROBE = 'clip-admin-probe';
const NOTE = 'zebra crossing at midnight, viewer2 alone knows';
const TEST_USER = 'bm-admin-probe-user';
const APP = 'clip-admin-probe';
const IN_USE = 'The clip file is in use (being played or downloaded); try again shortly.';
const IN_PROGRESS = 'Wait for the clip to finish or cancel it first.';
const NO_FILE = 'This bookmark has no clip file.';
const IS_THERE = 'The clip file is there after all (made again or put back), so nothing was deleted. Reload to see it.';
const TURNED_OFF = 'Admins\' access to other users\' clips is turned off in the Bookmarks settings ("Admins can see and manage every user\'s clips").';

function usage() {
    return 'usage: node scripts/bookmarks-clips-admin-probe.mjs --sandbox <name> [--skip-webchecks] [--timeout <seconds>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { timeout: 180, webchecks: true };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--timeout') o.timeout = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--skip-webchecks') o.webchecks = false;
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`unknown argument ${a}\n${usage()}`);
    }
    if (!o.sandbox || !/^[A-Za-z0-9_-]+$/.test(o.sandbox)) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const workRoot = path.join(sandboxRoot, 'bookmarks-clips-admin-probe');
const heldRoot = path.join(workRoot, 'held');
const movesFile = path.join(workRoot, 'moves.json');
const savedSettingsFile = path.join(workRoot, 'saved-settings.json');
const testUserFile = path.join(workRoot, 'test-user.json');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port}: not a sandbox's`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
const dataFolder = path.join(sandboxRoot, 'data', 'data', 'bookmarks');
const storeFile = path.join(dataFolder, 'bookmarks.json');
const clipRoot = path.join(dataFolder, 'clips');
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const who of ['admin', 'viewer', 'viewer2']) if (!users[who] || !users[who].token) throw new Error(`no ${who} in credentials.json: run Initialize-Sandbox.ps1`);
const pluginGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8')) || [])[1];
if (!pluginGuid) throw new Error('no guid in plugins/Bookmarks/build.yaml');
const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error(`refusing to work in ${p}`);
    return full;
}
for (const p of [dataFolder, storeFile, clipRoot, workRoot, logDir]) assertInside(p, sandboxRoot);
for (const p of [heldRoot, movesFile, savedSettingsFile, testUserFile]) assertInside(p, workRoot);

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const N = (name) => `${PROBE} ${name}`;

// Secrets the run makes, in memory only (never printed, never written): the test user's token and the API key.
const secrets = { testUserToken: null, apiKey: null };

// Every admin answer of the run, to look for viewer2's note in (never a note in an admin answer).
const adminAnswers = [];

// A request as a sandbox user (credentials.json), as the probe's test user or API key, or without a token.
async function call(who, method, p, { body, query, headers } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: 'application/json', ...(headers || {}) };
    const token = who === 'anonymous' ? null : who === 'testuser' ? secrets.testUserToken : who === 'apikey' ? secrets.apiKey : users[who].token;
    if (who !== 'anonymous') {
        if (!token) throw new Error(`no token for ${who}`);
        h.Authorization = `MediaBrowser Client="bookmarks-clips-admin-probe", Device="sandbox", DeviceId="${who === 'testuser' ? 'clip-admin-probe-user-device' : 'bookmarks-clips-admin-probe'}", Version="1.0.0", Token="${token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(180000) });
    const buffer = Buffer.from(await res.arrayBuffer());
    const text = /json|text|problem/i.test(res.headers.get('content-type') || '') ? buffer.toString('utf8') : '';
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    if (p.startsWith('/Bookmarks/Admin/Clips')) adminAnswers.push(text);
    return { status: res.status, json, text, bytes: buffer, headers: res.headers, detail: json && json.detail };
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

// ------------------------------------------------------------------------------------------ undoable changes

function loadMoves() {
    return fs.existsSync(movesFile) ? readJson(movesFile) : [];
}

function saveMoves(moves) {
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(movesFile, JSON.stringify(moves, null, 2));
}

// A move from the sandbox's clip folder into the probe's held folder only, recorded before it is made.
function moveAside(file) {
    const src = path.resolve(file);
    if (!src.startsWith(clipRoot + path.sep)) throw new Error(`refusing to move ${src}: not in the sandbox's clip folder`);
    fs.mkdirSync(heldRoot, { recursive: true });
    const dst = path.join(heldRoot, path.basename(src));
    if (fs.existsSync(dst)) throw new Error(`${dst} exists already`);
    const moves = loadMoves();
    moves.push({ from: src, to: dst });
    saveMoves(moves);
    fs.renameSync(src, dst);
}

// Moved back while a record still names the file; a file whose record let go of it meanwhile (the admin's Forget of check 7)
// is no clip's any more, so the probe's own copy in held/ goes instead of coming back as a file nobody names.
// A sandbox without bookmarks.json yet (a fresh one: no bookmark made so far) has no record naming any file; the store's
// own replace frees the name only for a moment, which storeBytes waits out before the file counts as missing.
function undoMoves() {
    let moves = loadMoves();
    if (moves.length === 0) return;
    const named = new Set(storeRecordsOrNone().map((b) => b.Clip && b.Clip.FilePath).filter(Boolean).map((f) => path.resolve(f).toLowerCase()));
    while (moves.length > 0) {
        const { from, to } = moves[moves.length - 1];
        const held = assertInside(to, heldRoot);
        if (fs.existsSync(held) && !fs.existsSync(from) && named.has(path.resolve(from).toLowerCase())) {
            fs.renameSync(held, from);
        } else if (fs.existsSync(held) && !named.has(path.resolve(from).toLowerCase())) {
            fs.rmSync(held);
        } else if (fs.existsSync(held)) {
            throw new Error(`cannot undo ${held} → ${from}: both exist; fix by hand`);
        }
        moves = moves.slice(0, -1);
        saveMoves(moves);
    }
}

async function getConfiguration() {
    const r = await call('admin', 'GET', CONFIGURATION_PATH);
    if (r.status !== 200 || !r.json) throw new Error(`GET ${CONFIGURATION_PATH}: ${r.status}`);
    return r.json;
}

async function setConfiguration(patch) {
    const current = await getConfiguration();
    const r = await call('admin', 'POST', CONFIGURATION_PATH, { body: { ...current, ...patch } });
    if (r.status !== 204) throw new Error(`POST ${CONFIGURATION_PATH}: ${r.status}`);
}

async function restoreSavedSettings() {
    if (!fs.existsSync(savedSettingsFile)) return false;
    await setConfiguration(readJson(savedSettingsFile));
    fs.rmSync(savedSettingsFile);
    return true;
}

// Settings changed for one step, saved to a file first and put back right after it, in the finally and at the next start.
async function withSettings(patch, step) {
    const current = await getConfiguration();
    const saved = Object.fromEntries(Object.keys(patch).map((k) => [k, current[k] ?? '']));
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(savedSettingsFile, JSON.stringify(saved));
    await setConfiguration(patch);
    try {
        return await step();
    } finally {
        await restoreSavedSettings();
    }
}

// Holds a file open as a download or a preview does (read, sharing read and write but not delete), from a PowerShell process
// the probe starts, until the returned release() ends it; the path goes through the environment, never a command line.
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
        cwd: repoRoot, encoding: 'utf8', timeout: 1200000, windowsHide: true
    });
    let result = null;
    try { result = readJson(out); } catch { result = null; }
    for (const shot of (result && result.screenshots) || []) {
        if (fs.existsSync(shot)) fs.copyFileSync(shot, path.join(workRoot, path.basename(shot)));
    }
    const failed = result && result.steps ? result.steps.filter((s) => !s.ok).map((s) => ({ index: s.index, name: s.name, error: s.error })) : null;
    return {
        ok: w.status === 0 && !!result && result.ok === true,
        exit: w.status,
        seconds: Math.round((Date.now() - started) / 1000),
        steps: result && result.steps ? result.steps.length : 0,
        failed,
        failedRequests: result && result.failedRequests ? result.failedRequests.map((r) => `${r.status || r.failure || ''} ${r.url || ''}`.slice(0, 160)).slice(0, 12) : null,
        exceptions: result && result.exceptions ? result.exceptions.length : null
    };
}

// ------------------------------------------------------------------------------------------ the test user and the API key

// The probe's own user, made by the admin with a random password (in memory only), signed in once for its token.
async function createTestUser() {
    const password = crypto.randomBytes(18).toString('base64url');
    const made = await call('admin', 'POST', '/Users/New', { body: { Name: TEST_USER, Password: password } });
    if (made.status !== 200 || !made.json) throw new Error(`POST /Users/New: ${made.status}`);
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(testUserFile, JSON.stringify({ id: made.json.Id, name: TEST_USER }));
    const url = new URL(baseUrl + '/Users/AuthenticateByName', origin);
    const signedIn = await fetch(url, {
        method: 'POST',
        headers: {
            Accept: 'application/json',
            'Content-Type': 'application/json',
            Authorization: 'MediaBrowser Client="bookmarks-clips-admin-probe", Device="sandbox", DeviceId="clip-admin-probe-user-device", Version="1.0.0"'
        },
        body: JSON.stringify({ Username: TEST_USER, Pw: password }),
        signal: AbortSignal.timeout(60000)
    });
    const answer = signedIn.status === 200 ? await signedIn.json() : null;
    if (!answer || !answer.AccessToken) throw new Error(`POST /Users/AuthenticateByName: ${signedIn.status}`);
    secrets.testUserToken = answer.AccessToken;
    return made.json.Id;
}

async function setTestUserPolicy(id, patch) {
    const user = await call('admin', 'GET', `/Users/${id}`);
    if (user.status !== 200 || !user.json) throw new Error(`GET /Users/${id}: ${user.status}`);
    const r = await call('admin', 'POST', `/Users/${id}/Policy`, { body: { ...user.json.Policy, ...patch } });
    if (r.status !== 204) throw new Error(`POST /Users/${id}/Policy: ${r.status} ${r.text.slice(0, 200)}`);
}

// The test user (of this run, or one a killed run left, by its name and recorded id) deleted, its sessions and devices with it.
async function deleteTestUser() {
    secrets.testUserToken = null;
    const list = await call('admin', 'GET', '/Users');
    const recorded = fs.existsSync(testUserFile) ? readJson(testUserFile).id : null;
    const found = ((list.json) || []).filter((u) => u.Name === TEST_USER || (recorded && guid(u.Id) === guid(recorded)));
    const statuses = [];
    for (const u of found) {
        statuses.push((await call('admin', 'DELETE', `/Users/${u.Id}`)).status);
    }
    if (fs.existsSync(testUserFile)) fs.rmSync(testUserFile);
    return statuses;
}

// The probe's API key: made, read back once (in memory only), and deleted (also one a killed run left).
async function createApiKey() {
    const made = await call('admin', 'POST', '/Auth/Keys', { query: { app: APP } });
    if (made.status !== 204) throw new Error(`POST /Auth/Keys: ${made.status}`);
    const keys = await call('admin', 'GET', '/Auth/Keys');
    const mine = ((keys.json && keys.json.Items) || []).filter((k) => k.AppName === APP);
    if (mine.length !== 1) throw new Error(`expected one API key of ${APP}, found ${mine.length}`);
    secrets.apiKey = mine[0].AccessToken;
}

async function deleteApiKeys() {
    secrets.apiKey = null;
    const keys = await call('admin', 'GET', '/Auth/Keys');
    const statuses = [];
    for (const k of ((keys.json && keys.json.Items) || []).filter((x) => x.AppName === APP)) {
        statuses.push((await call('admin', 'DELETE', `/Auth/Keys/${k.AccessToken}`)).status);
    }
    return statuses;
}

// ------------------------------------------------------------------------------------------ bookmarks and clips

const made = [];

async function findItem(type, test) {
    const r = await call('admin', 'GET', '/Items', {
        query: { userId: users.admin.id, Recursive: 'true', IncludeItemTypes: type, Fields: 'Path', EnableImages: 'false', EnableUserData: 'false' }
    });
    if (r.status !== 200 || !r.json) throw new Error(`GET /Items: ${r.status}`);
    const found = r.json.Items.filter((i) => typeof i.Path === 'string' && test(i.Path));
    if (found.length !== 1) throw new Error(`expected one ${type} for the seed, found ${found.length}; run Initialize-Sandbox.ps1 on fresh media`);
    return found[0];
}

async function createBookmark(who, item, { start, end, name, note }) {
    const body = { ItemId: item.Id, StartTicks: start * TICKS, EndTicks: end * TICKS, Name: name, Note: note || null };
    const r = await call(who, 'POST', '/Bookmarks', { body });
    if (r.status !== 201 || !r.json) throw new Error(`POST /Bookmarks on ${item.Name}: ${r.status} ${r.text.slice(0, 200)}`);
    made.push({ who, id: r.json.Id });
    return r.json;
}

async function makeClip(who, bookmark, mode = 'Fast') {
    const r = await call(who, 'POST', `/Bookmarks/${bookmark.Id}/Clip`, { body: { Mode: mode } });
    if (r.status !== 202) throw new Error(`POST /Bookmarks/${bookmark.Id}/Clip: ${r.status} ${r.text.slice(0, 200)}`);
    return waitFor(`the clip of ${bookmark.Id}`, async () => {
        const read = await call(who, 'GET', `/Bookmarks/${bookmark.Id}`);
        const status = read.json && read.json.Clip && read.json.Clip.Status;
        if (status === 'Completed') return read.json;
        if (status && !['Queued', 'Running'].includes(status)) throw new Error(`the clip of ${bookmark.Id} ended ${status}: ${read.json.Clip.Error}`);
        return null;
    }, opts.timeout);
}

// bookmarks.json's bytes as they are (read only), read again for a moment while the store replaces the file (it writes a
// new file and moves it into place, so the name is briefly free, or briefly locked: EBUSY, seen once in an earlier run).
function storeBytes() {
    for (let i = 0; ; i++) {
        try {
            return fs.readFileSync(storeFile);
        } catch (error) {
            if (i >= 40 || !['ENOENT', 'EBUSY', 'EPERM'].includes(error.code)) throw error;
            Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 25);
        }
    }
}

function storeRecords() {
    for (let i = 0; ; i++) {
        try {
            return JSON.parse(storeBytes().toString('utf8').replace(/^\uFEFF/, '')).Bookmarks || [];
        } catch (error) {
            if (i >= 40 || !(error instanceof SyntaxError)) throw error;
            Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 25);
        }
    }
}

function storeRecordsOrNone() {
    try {
        return storeRecords();
    } catch (error) {
        if (error.code === 'ENOENT') return [];
        throw error;
    }
}

// A record as bookmarks.json has it (read only).
function stored(id) {
    return storeRecords().find((b) => guid(b.Id) === guid(id)) || null;
}

const filesIn = (folder) => (fs.existsSync(folder) ? fs.readdirSync(folder) : []);

// Every admin clip route, for the callers' checks ({id}: a clip's id).
const ROUTES = [
    ['GET', '/Bookmarks/Admin/Clips'],
    ['GET', '/Bookmarks/Admin/Clips/Summary'],
    ['GET', '/Bookmarks/Admin/Clips/Status', (id) => ({ query: { ids: guid(id) } })],
    ['GET', '/Bookmarks/Admin/Clips/{id}/File'],
    ['POST', '/Bookmarks/Admin/Clips/{id}/Cancel'],
    ['DELETE', '/Bookmarks/Admin/Clips/{id}'],
    ['DELETE', '/Bookmarks/Admin/Clips/{id}', () => ({ query: { onlyIfMissing: 'true' } })],
    ['POST', '/Bookmarks/Admin/Clips/BulkDelete', (id) => ({ body: { Ids: [id] } })]
];

async function everyRoute(who, id) {
    const answers = [];
    for (const [method, route, extra] of ROUTES) {
        const r = await call(who, method, route.replace('{id}', guid(id)), extra ? extra(id) : {});
        answers.push({ route: `${method} ${route}${extra && method === 'DELETE' ? '?onlyIfMissing=true' : ''}`, status: r.status, detail: r.detail || null });
    }
    return answers;
}

// The probe's bookmarks and clips of a failed earlier run (or of this one): the live ones deleted with their clips, then the
// clips kept when one was deleted.
async function deleteLeftovers() {
    const left = [];
    for (const who of ['admin', 'viewer2']) {
        for (let round = 0; round < 10; round++) {
            const r = await call(who, 'GET', '/Bookmarks/Query', { query: { searchTerm: PROBE, limit: 200 } });
            const ids = ((r.json && r.json.Items) || []).filter((b) => (b.Name || '').startsWith(PROBE)).map((b) => b.Id);
            if (ids.length === 0) break;
            await call(who, 'POST', '/Bookmarks/BulkDelete', { body: { Ids: ids, DeleteClips: true } });
            left.push(...ids);
        }
        const kept = await call(who, 'GET', '/Bookmarks/Clips', { query: { searchTerm: PROBE, limit: 200 } });
        for (const c of ((kept.json && kept.json.Items) || []).filter((x) => (x.Name || '').startsWith(PROBE))) {
            const d = await call(who, 'DELETE', `/Bookmarks/${c.Id}/Clip`);
            left.push({ clip: c.Id, status: d.status });
        }
    }
    return left;
}

// ------------------------------------------------------------------------------------------ the run

async function seed() {
    const movie = await findItem('Movie', (p) => p.split(/[\\/]/).includes('Long Test Movie (2021)'));
    const s = {};
    const plain = [['Ready', 10, 14, NOTE], ['Kept', 16, 20], ['Gone', 22, 26], ['Held', 28, 32], ['Bulk A', 34, 38], ['Bulk B', 40, 44],
        ['Web A', 46, 50, NOTE], ['Web B', 52, 56], ['Web C', 58, 62], ['Web D', 64, 68]];
    for (const [name, start, end, note] of plain) {
        s[name] = await createBookmark('viewer2', movie, { start, end, name: N('V2 ' + name), note });
        await makeClip('viewer2', s[name]);
    }

    s.Own = await createBookmark('admin', movie, { start: 70, end: 74, name: N('Admin own') });
    await makeClip('admin', s.Own);
    s.Making = await createBookmark('viewer2', movie, { start: 0, end: 175, name: N('V2 Making') });

    const kept = await call('viewer2', 'DELETE', `/Bookmarks/${s.Kept.Id}`, { query: { deleteClip: 'false' } });
    if (kept.status !== 204) throw new Error(`DELETE /Bookmarks/${s.Kept.Id}?deleteClip=false: ${kept.status}`);
    const files = Object.fromEntries(Object.entries(s).map(([k, b]) => [k, stored(b.Id) && stored(b.Id).Clip ? stored(b.Id).Clip.FilePath : null]));
    moveAside(files.Gone);
    const ids = Object.fromEntries(Object.entries(s).map(([k, b]) => [k, b.Id]));
    evidence.seed = Object.fromEntries(Object.entries(files).map(([k, f]) => [k, f ? path.relative(clipRoot, f) : null]));
    check('1. the seed: viewer2\'s Ready (with its note), Kept (its bookmark deleted keeping the clip), Gone (its file moved aside), Held, Bulk A, Bulk B, Web A to Web D, Making (no clip yet), and admin\'s own; every file in the user\'s folder of the clip folder',
        Object.entries(files).filter(([k]) => k !== 'Making').every(([k, f]) => !!f && path.resolve(f).startsWith(path.join(clipRoot, k === 'Own' ? 'admin' : 'viewer2') + path.sep))
        && !fs.existsSync(files.Gone) && fs.existsSync(files.Ready) && !!stored(ids.Kept) && !!stored(ids.Kept).DeletedUtc,
        evidence.seed);
    return { ids, files };
}

async function run(context) {
    const { ids, files } = await seed();

    // 2. who may call.
    const viewer = await everyRoute('viewer', ids.Ready);
    const anonymous = await everyRoute('anonymous', ids.Ready);
    await createApiKey();
    const keyList = await call('apikey', 'GET', '/Bookmarks/Admin/Clips', { query: { searchTerm: PROBE, limit: 200 } });
    const keySummary = await call('apikey', 'GET', '/Bookmarks/Admin/Clips/Summary');
    evidence.apiKeys = { deleted: await deleteApiKeys() };
    const capabilities = { admin: (await call('admin', 'GET', '/Bookmarks/Capabilities')).json, viewer: (await call('viewer', 'GET', '/Bookmarks/Capabilities')).json };
    evidence.callers = { viewer, anonymous, apiKey: { list: keyList.status, listed: keyList.json ? keyList.json.TotalRecordCount : null, summary: keySummary.status }, canManage: { admin: capabilities.admin.CanManageAllClips, viewer: capabilities.viewer.CanManageAllClips } };
    check('2. who may call: viewer, a member, 403 on every admin clip route; no token 401; the probe\'s API key admitted (list and summary 200, every probe clip listed); CanManageAllClips true for admin, false for viewer; nothing changed',
        viewer.every((a) => a.status === 403) && anonymous.every((a) => a.status === 401)
        && keyList.status === 200 && keyList.json.TotalRecordCount === 11 && keySummary.status === 200
        && capabilities.admin.CanManageAllClips === true && capabilities.viewer.CanManageAllClips === false
        && fs.existsSync(files.Ready) && !!stored(ids.Ready).Clip && evidence.apiKeys.deleted.length === 1 && evidence.apiKeys.deleted[0] === 204,
        evidence.callers);

    // 3. a token's admin rights are read at each request.
    const testUser = await createTestUser();
    await setTestUserPolicy(testUser, { IsAdministrator: true });
    const asAdmin = await call('testuser', 'GET', '/Bookmarks/Admin/Clips', { query: { searchTerm: PROBE, limit: 200 } });
    const capsAdmin = (await call('testuser', 'GET', '/Bookmarks/Capabilities')).json;
    await setTestUserPolicy(testUser, { IsAdministrator: false });
    const demoted = await everyRoute('testuser', ids.Ready);
    const capsDemoted = (await call('testuser', 'GET', '/Bookmarks/Capabilities')).json;
    await setTestUserPolicy(testUser, { IsDisabled: true });
    const disabled = await everyRoute('testuser', ids.Ready);
    const disabledCaps = await call('testuser', 'GET', '/Bookmarks/Capabilities');
    const adminDisabledRefused = await (async () => {
        await setTestUserPolicy(testUser, { IsDisabled: false, IsAdministrator: true });
        const user = await call('admin', 'GET', `/Users/${testUser}`);
        const r = await call('admin', 'POST', `/Users/${testUser}/Policy`, { body: { ...user.json.Policy, IsDisabled: true } });
        return r.status;
    })();
    evidence.tokens = { asAdmin: asAdmin.status, listed: asAdmin.json ? asAdmin.json.TotalRecordCount : null, canManage: { admin: capsAdmin && capsAdmin.CanManageAllClips, demoted: capsDemoted && capsDemoted.CanManageAllClips }, demoted, disabled, disabledCapabilities: disabledCaps.status, disableAnAdmin: adminDisabledRefused, deleted: await deleteTestUser() };
    check('3. a token\'s admin rights are read at each request: the test user made an admin lists every user\'s clips with its token (CanManageAllClips true); demoted, the same token 403 on every route (CanManageAllClips false); disabled, the same token 401 (Jellyfin revokes it; it refuses to disable an admin: 403); the user deleted',
        asAdmin.status === 200 && asAdmin.json.TotalRecordCount === 11 && capsAdmin.CanManageAllClips === true
        && demoted.every((a) => a.status === 403) && capsDemoted.CanManageAllClips === false
        && disabled.every((a) => a.status === 401) && disabledCaps.status === 401 && adminDisabledRefused === 403
        && evidence.tokens.deleted.length === 1 && evidence.tokens.deleted[0] === 204,
        evidence.tokens);

    // 4. the list.
    const list = await call('admin', 'GET', '/Bookmarks/Admin/Clips', { query: { searchTerm: PROBE, limit: 200, sortBy: 'Name' } });
    const rows = (list.json && list.json.Items) || [];
    const byId = Object.fromEntries(rows.map((r) => [guid(r.Id), r]));
    const ready = byId[guid(ids.Ready)];
    const own = byId[guid(ids.Own)];
    const search = await call('admin', 'GET', '/Bookmarks/Admin/Clips', { query: { searchTerm: 'zebra crossing' } });
    const ownSearch = await call('viewer2', 'GET', '/Bookmarks/Clips', { query: { searchTerm: 'zebra crossing' } });
    const filtered = await call('admin', 'GET', '/Bookmarks/Admin/Clips', { query: { searchTerm: PROBE, userId: guid(users.viewer2.id), limit: 200 } });
    const summary = await call('admin', 'GET', '/Bookmarks/Admin/Clips/Summary');
    const names = ((summary.json && summary.json.Users) || []).map((u) => u.UserName);
    evidence.list = {
        total: list.json && list.json.TotalRecordCount,
        ready: ready && { owner: ready.UserName, name: ready.Name, file: ready.Clip.FileName, inClipFolder: !!ready.FilePath && path.resolve(ready.FilePath).startsWith(clipRoot + path.sep), rootId: ready.RootId, hasNote: Object.prototype.hasOwnProperty.call(ready, 'Note') },
        own: own && { owner: own.UserName },
        gone: byId[guid(ids.Gone)] && byId[guid(ids.Gone)].Clip.State,
        kept: byId[guid(ids.Kept)] && byId[guid(ids.Kept)].BookmarkDeleted,
        search: search.json && search.json.TotalRecordCount,
        ownSearch: ownSearch.json && ownSearch.json.TotalRecordCount,
        filtered: filtered.json && filtered.json.TotalRecordCount,
        summaryUsers: names
    };
    check('4. the list (admin): viewer2\'s clips with their owner "viewer2", names and full paths in the clip folder (RootId), Kept as a deleted bookmark\'s, Gone "Missing", admin\'s own with "admin"; never viewer2\'s note (no Note member, a search for its words 0 clips while viewer2\'s own search finds its two clips with it); the owner filter 10; the summary per user',
        list.status === 200 && rows.length === 11 && !!ready && ready.UserName === 'viewer2' && ready.Name === N('V2 Ready') && evidence.list.ready.inClipFolder
        && /^[0-9a-f]{12}$/.test(ready.RootId || '') && !evidence.list.ready.hasNote && !!own && own.UserName === 'admin'
        && evidence.list.gone === 'Missing' && evidence.list.kept === true
        && evidence.list.search === 0 && evidence.list.ownSearch === 2 && evidence.list.filtered === 10
        && names.includes('viewer2') && names.includes('admin'),
        evidence.list);

    // 5. the download.
    const download = await call('admin', 'GET', `/Bookmarks/Admin/Clips/${guid(ids.Ready)}/File`);
    const disposition = download.headers.get('content-disposition') || '';
    const onDisk = fs.readFileSync(files.Ready);
    const range = await call('admin', 'GET', `/Bookmarks/Admin/Clips/${guid(ids.Ready)}/File`, { query: { download: 'false' }, headers: { Range: 'bytes=0-99' } });
    const releaseReady = await holdOpen(files.Ready);
    let heldDownload;
    try {
        heldDownload = await call('admin', 'GET', `/Bookmarks/Admin/Clips/${guid(ids.Ready)}/File`);
    } finally {
        await releaseReady();
    }
    const gone = await call('admin', 'GET', `/Bookmarks/Admin/Clips/${guid(ids.Gone)}/File`);
    evidence.download = {
        status: download.status,
        contentType: download.headers.get('content-type'),
        disposition: disposition.replace(/filename\*=UTF-8''[^;]+/, 'filename*=…'),
        named: disposition.includes(encodeURIComponent(path.basename(files.Ready)).replace(/'/g, '%27')) || disposition.includes(`filename="${path.basename(files.Ready)}"`) || disposition.includes(`filename=${path.basename(files.Ready)}`),
        folderInHeaders: [...download.headers.entries()].some(([, v]) => v.toLowerCase().includes(clipRoot.toLowerCase()) || v.toLowerCase().includes('viewer2\\') || v.toLowerCase().includes('viewer2%5c')),
        sameBytes: Buffer.compare(download.bytes, onDisk) === 0,
        bytes: download.bytes.length,
        range: [range.status, range.bytes.length],
        held: heldDownload.status,
        gone: [gone.status, gone.detail, gone.text.toLowerCase().includes(clipRoot.toLowerCase().replace(/\\/g, '\\\\'))]
    };
    check('5. the download (admin, viewer2\'s Ready): 200, an attachment named as the file is, no folder in any header, its bytes the file\'s; a range 206 of 100 bytes; held open by a reader, 200 still; Gone 404 "This bookmark has no clip file." without a path',
        download.status === 200 && /^attachment/i.test(disposition) && evidence.download.named && !evidence.download.folderInHeaders && evidence.download.sameBytes
        && range.status === 206 && range.bytes.length === 100 && heldDownload.status === 200
        && gone.status === 404 && gone.detail === NO_FILE && !evidence.download.gone[2],
        evidence.download);

    // 6. a clip being made: its delete refused, its cancel.
    const making = await withSettings({ VideoPreset: 'slow' }, async () => {
        const asked = await call('viewer2', 'POST', `/Bookmarks/${ids.Making}/Clip`, { body: { Mode: 'Accurate' } });
        if (asked.status !== 202) throw new Error(`POST /Bookmarks/${ids.Making}/Clip: ${asked.status} ${asked.text.slice(0, 200)}`);
        await waitFor('the long clip queued or running', async () => {
            const s = stored(ids.Making);
            return s && s.Clip && ['Queued', 'Running'].includes(s.Clip.Status);
        }, 30);
        const before = storeBytes();
        const refused = await call('admin', 'DELETE', `/Bookmarks/Admin/Clips/${guid(ids.Making)}`);
        const unchanged = Buffer.compare(before, storeBytes()) === 0 || ['Queued', 'Running'].includes(stored(ids.Making).Clip.Status);
        const cancelled = await call('admin', 'POST', `/Bookmarks/Admin/Clips/${guid(ids.Making)}/Cancel`);
        const ended = await waitFor('the long clip ended', async () => {
            const s = stored(ids.Making);
            return s && s.Clip && !['Queued', 'Running'].includes(s.Clip.Status) ? s.Clip.Status : null;
        }, 60);
        return { refused: [refused.status, refused.detail], unchanged, cancel: [cancelled.status, cancelled.json && cancelled.json.UserName], ended };
    });
    evidence.making = making;
    check('6. a clip being made (viewer2\'s Making, Accurate with VideoPreset slow): the admin\'s delete 409 "Wait for the clip to finish or cancel it first." with the clip as it was; the admin\'s cancel 200 (the row viewer2\'s) and the job Cancelled',
        making.refused[0] === 409 && making.refused[1] === IN_PROGRESS && making.unchanged && making.cancel[0] === 200 && making.cancel[1] === 'viewer2' && making.ended === 'Cancelled',
        making);

    // 7. the deletes.
    const deleteReady = await call('admin', 'DELETE', `/Bookmarks/Admin/Clips/${guid(ids.Ready)}`);
    const readyAfter = await call('viewer2', 'GET', `/Bookmarks/${ids.Ready}`);
    const deleteKept = await call('admin', 'DELETE', `/Bookmarks/Admin/Clips/${guid(ids.Kept)}`);
    const beforeHeld = storeBytes();
    const releaseHeld = await holdOpen(files.Held);
    context.releaseHeld = releaseHeld;
    let heldRefused;
    let heldUnchanged;
    try {
        const started = Date.now();
        heldRefused = await call('admin', 'DELETE', `/Bookmarks/Admin/Clips/${guid(ids.Held)}`);
        heldRefused.ms = Date.now() - started;
        heldUnchanged = Buffer.compare(beforeHeld, storeBytes()) === 0 && fs.existsSync(files.Held);
    } finally {
        context.releaseHeld = null;
        await releaseHeld();
    }
    const heldDeleted = await call('admin', 'DELETE', `/Bookmarks/Admin/Clips/${guid(ids.Held)}`);
    const forgetThere = await call('admin', 'DELETE', `/Bookmarks/Admin/Clips/${guid(ids['Bulk A'])}`, { query: { onlyIfMissing: 'true' } });
    const forgetGone = await call('admin', 'DELETE', `/Bookmarks/Admin/Clips/${guid(ids.Gone)}`, { query: { onlyIfMissing: 'true' } });
    evidence.deletes = {
        ready: [deleteReady.status, fs.existsSync(files.Ready), readyAfter.status, readyAfter.json && readyAfter.json.Name, readyAfter.json && readyAfter.json.Clip],
        kept: [deleteKept.status, fs.existsSync(files.Kept), stored(ids.Kept)],
        held: [heldRefused.status, heldRefused.detail, heldRefused.ms, heldUnchanged, heldDeleted.status, fs.existsSync(files.Held)],
        forgetThere: [forgetThere.status, forgetThere.detail, fs.existsSync(files['Bulk A'])],
        forgetGone: [forgetGone.status, stored(ids.Gone) && stored(ids.Gone).Clip]
    };
    check('7. the deletes (admin): Ready 204, its file gone, viewer2\'s bookmark still there without a clip; Kept 204, its record gone with its file; Held held open 409 "in use" after the server\'s three attempts (400 ms and more) with bookmarks.json byte for byte, then 204 once released; Forget of Bulk A, whose file is there, 409 with nothing deleted; Forget of Gone 204',
        deleteReady.status === 204 && !fs.existsSync(files.Ready) && readyAfter.status === 200 && readyAfter.json.Name === N('V2 Ready') && !readyAfter.json.Clip
        && deleteKept.status === 204 && !fs.existsSync(files.Kept) && stored(ids.Kept) === null
        && heldRefused.status === 409 && heldRefused.detail === IN_USE && heldRefused.ms >= 400 && heldUnchanged && heldDeleted.status === 204 && !fs.existsSync(files.Held)
        && forgetThere.status === 409 && forgetThere.detail === IS_THERE && fs.existsSync(files['Bulk A'])
        && forgetGone.status === 204 && stored(ids.Gone) && !stored(ids.Gone).Clip,
        evidence.deletes);

    // 8. the bulk delete.
    const unknown = crypto.randomUUID();
    const bulk = await call('admin', 'POST', '/Bookmarks/Admin/Clips/BulkDelete', { body: { Ids: [ids['Bulk A'], ids['Bulk B'], unknown] } });
    evidence.bulk = { status: bulk.status, result: bulk.json && Object.fromEntries(Object.entries(bulk.json).map(([k, v]) => [k, v.length])), files: [fs.existsSync(files['Bulk A']), fs.existsSync(files['Bulk B'])] };
    check('8. the bulk delete (admin): Bulk A, Bulk B and an unknown id: 200, FilesDeleted 2, NotFound 1, both files gone, the bookmarks stay',
        bulk.status === 200 && bulk.json.FilesDeleted.length === 2 && bulk.json.NotFound.length === 1 && bulk.json.InUse.length === 0
        && !fs.existsSync(files['Bulk A']) && !fs.existsSync(files['Bulk B']) && !!stored(ids['Bulk A']) && !stored(ids['Bulk A']).Clip,
        evidence.bulk);

    // 9. the setting off.
    const off = await withSettings({ AdminsCanManageAllClips: false }, async () => {
        const answers = await everyRoute('admin', ids['Web A']);
        const caps = (await call('admin', 'GET', '/Bookmarks/Capabilities')).json;
        return { answers, canManage: caps.CanManageAllClips };
    });
    const onAgain = (await call('admin', 'GET', '/Bookmarks/Capabilities')).json.CanManageAllClips;
    evidence.off = { ...off, onAgain, webA: fs.existsSync(files['Web A']) };
    check('9. AdminsCanManageAllClips off: every gated admin clip route 403 "Admins\' access to other users\' clips is turned off …", the summary 200, CanManageAllClips false, Web A untouched; on again afterwards',
        off.answers.every((a) => a.route.endsWith('/Summary') ? a.status === 200 : a.status === 403 && a.detail === TURNED_OFF)
        && off.canManage === false && onAgain === true && fs.existsSync(files['Web A']),
        evidence.off);

    // 10 and 11. the webchecks.
    if (opts.webchecks) {
        const desktop = webcheck('page-clips-admin');
        evidence.webcheck = desktop;
        check('10. page-clips-admin.json (admin, then viewer): the switch, "All users" with viewer2\'s rows naming their owner and path and no note on the page, the Owner select in the hash, Download through the admin file route, Delete of Web A naming viewer2 with the focus on Cancel, the bulk delete of Web B and Web C; viewer sees no switch',
            desktop.ok && !fs.existsSync(files['Web A']) && !fs.existsSync(files['Web B']) && !fs.existsSync(files['Web C']), desktop);
        const tv = webcheck('page-clips-admin-tv');
        evidence.webcheckTv = tv;
        check('11. page-clips-admin-tv.json (admin, the TV layout): the switch by the D-pad and OK, the Owner select, Delete of Web D by OK with the focus on Cancel, one ArrowDown to Delete, OK, the focus back in the list',
            tv.ok && !fs.existsSync(files['Web D']), tv);
    }

    // Every admin answer of the run, for viewer2's note.
    evidence.noteInAnswers = adminAnswers.filter((t) => t && (t.includes('zebra') || t.includes(NOTE))).length;
    check('4a. viewer2\'s note in none of the run\'s ' + adminAnswers.length + ' admin clip answers', evidence.noteInAnswers === 0 && adminAnswers.length > 30, { answers: adminAnswers.length, withTheNote: evidence.noteInAnswers });
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { releaseHeld: null };
    const startLog = logPosition();
    evidence.leftovers = { moves: loadMoves().length, settings: fs.existsSync(savedSettingsFile), testUser: fs.existsSync(testUserFile) };
    undoMoves();
    await restoreSavedSettings();
    evidence.leftovers.apiKeys = await deleteApiKeys();
    evidence.leftovers.testUsers = await deleteTestUser();
    evidence.leftovers.deleted = await deleteLeftovers();
    const startSettings = await getConfiguration();
    let failure = null;
    try {
        await run(context);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        const cleanup = { deleted: [] };
        try {
            if (context.releaseHeld) {
                cleanup.heldRelease = await context.releaseHeld();
            }

            // The file moved aside first (back while its record names it), then the probe's records with their clips.
            undoMoves();
            cleanup.settingsRestored = await restoreSavedSettings();
            cleanup.apiKeys = await deleteApiKeys();
            cleanup.testUsers = await deleteTestUser();
            for (const { who, id } of made) {
                const removed = await call(who, 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } });
                const clip = await call(who, 'DELETE', `/Bookmarks/${id}/Clip`);
                cleanup.deleted.push({ id, bookmark: removed.status, clip: clip.status });
            }
            cleanup.leftovers = await deleteLeftovers();
        } catch (error) {
            check('clean-up: the settings, the moved file, the API key, the test user and the bookmarks put back', false, String(error));
        }
        evidence.cleanup = cleanup;
        const text = logSince(startLog);
        const lines = text.split('\n');
        const ours = lines.filter((l) => /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        const errors = ours.filter((l) => /\[ERR\]|\[FTL\]/.test(l));
        const naming = ours.filter((l) => /\[(INF|WRN|ERR|FTL)\]/.test(l) && (l.includes(PROBE) || l.toLowerCase().includes(clipRoot.toLowerCase()) || l.includes('zebra')));
        const audit = lines.filter((l) => /\[INF\].*(Admin [0-9a-f-]{36} |The API key |A user that no longer exists )/.test(l)).map((l) => l.replace(/^.*?\] /, '').trim().slice(0, 220));
        evidence.log = { errors: errors.slice(0, 5), naming: naming.slice(0, 5), audit: audit.slice(0, 20) };
        const adminId = guid(users.admin.id);
        check('12. the server log: each admin action at Information with the admin\'s id, the clip\'s id and its owner\'s (downloaded, cancelled, deleted, forgot, deleted … at once), no Error of Bookmarks or the kit, and no line of theirs at Information or above naming a probe bookmark, viewer2\'s note or a path in the clip folder',
            errors.length === 0 && naming.length === 0
            && ['downloaded the clip of bookmark', 'previewed the clip of bookmark', 'cancelled the clip being made of bookmark', 'deleted the clip of bookmark', 'forgot the clip of bookmark', 'clips at once'].every((what) => audit.some((l) => l.replace(/-/g, '').includes(`Admin ${adminId} `) && l.includes(what))),
            evidence.log);
        const left = [];
        for (const who of ['admin', 'viewer2']) {
            const live = await call(who, 'GET', '/Bookmarks/Query', { query: { searchTerm: PROBE, limit: 1 } });
            const clips = await call(who, 'GET', '/Bookmarks/Clips', { query: { searchTerm: PROBE, limit: 1 } });
            if ((live.json && live.json.TotalRecordCount > 0) || (clips.json && clips.json.TotalRecordCount > 0)) left.push(who);
        }
        const probeFiles = filesIn(path.join(clipRoot, 'admin')).concat(filesIn(path.join(clipRoot, 'viewer2'))).filter((f) => f.includes(PROBE));
        const settingsNow = await getConfiguration();
        const keysLeft = ((await call('admin', 'GET', '/Auth/Keys')).json || { Items: [] }).Items.filter((k) => k.AppName === APP).length;
        const usersLeft = ((await call('admin', 'GET', '/Users')).json || []).filter((u) => u.Name === TEST_USER).length;
        check('13. clean-up: no moved file left, the settings as they were (VideoPreset, AdminsCanManageAllClips), no API key of the probe\'s, no test user, no clip-admin-probe bookmark or clip left, none of their files in the clip folder',
            loadMoves().length === 0 && !fs.existsSync(savedSettingsFile) && settingsNow.VideoPreset === startSettings.VideoPreset
            && settingsNow.AdminsCanManageAllClips === startSettings.AdminsCanManageAllClips && keysLeft === 0 && usersLeft === 0
            && left.length === 0 && probeFiles.length === 0,
            { moves: loadMoves().length, left, probeFiles, keysLeft, usersLeft, videoPreset: settingsNow.VideoPreset, adminsCanManageAllClips: settingsNow.AdminsCanManageAllClips });
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
