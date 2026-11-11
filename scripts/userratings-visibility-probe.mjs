#!/usr/bin/env node
// User Ratings' lists (Mine/Inaccessible among them) against core's per-item check, for items a parent hides. Node 22 built-ins only. Loopback only, never port 8096. The admin token comes from the
// sandbox's credentials.json and is never printed; the two users the probe makes get a random password held only in
// memory (a run that stopped half way resets it as the admin to sign in again), and are deleted at the end.
//
//   node scripts/userratings-visibility-probe.mjs --sandbox default [--remove-media] [--details] [--out result.json]
//
// On 10.11.5 GetItemList checks only an item's own IsVisible(user), while GetItemById(id, user) also refuses an item one of
// whose parents fails IsVisible(user, true) (BaseItem.IsVisibleStandaloneInternal). The probe makes three copies the
// sandbox's Shows and Movies already have, in libraries of its own under .sandbox/<name>/visibility-probe/media:
//   UR Visibility A (tvshows): Test Series [tvdbid-900401], the series rated TV-MA, its episodes locked TV-PG after that
//                              (editing a series' rating overwrites its episodes' locked rating on 10.11.5, so they go second);
//   UR Visibility B (tvshows): the same series unrated, its episodes locked TV-PG;
//   UR Visibility Movies (movies): Rated Folder/Tagged Movie (2012) [tmdbid-900001], the folder rated R, the movie PG.
// and two users: urvis-kid (MaxParentalRating 14, TV-14) and urvis-tagger (BlockUnratedItems Series and Movie). Before the
// limits, kid rates A's S01E02 and the folder's movie, tagger B's S01E02; the admin rates A's and B's S01E01 and the movie.
// Then, each answer compared with core's own GET /Users/{id}/Items/{itemId}:
//   (a) kid: A's episodes and the folder's movie are 404 in core and ours; the Shows S01E01's OtherCopies names B's copy
//       (a positive control) and never A's, also in the answers of a PUT and a DELETE Mine/Rating;
//   (b) tagger: B's episodes 404 in core and ours; A's S01E01 (rated series, TV-PG episode) opens, and its OtherCopies
//       never names B's copy, also in the PUT's and the DELETE's answers;
//   (c) kid: the Movies copy's OtherCopies never names the movie in the R folder;
//   the reverse: kid's Mine/Inaccessible lists the rows on A's S01E02 and the folder's movie, tagger's the row on B's
//   S01E02, with the member's own data only; DELETE Mine/{ratingId} deletes them.
// The admin's reads are the positive control that the copies are linked at all. At the end the probe deletes every rating
// it made (as its owner), its users and its libraries; what it made is kept in visibility-probe/state.json until then, so
// a run that stopped half way is cleaned up by the next. The media copies (about 6 MB) stay for the next run unless
// --remove-media; nothing outside .sandbox/<name>/visibility-probe is written.
//
// With --details (the details page's section shows nothing for an item the member can't see) the same items are
// opened in the web client by viewer2 of credentials.json (webcheck.mjs signs in only as the sandbox's own users), first
// with kid's limit (MaxParentalRating 14), then with tagger's (BlockUnratedItems Series and Movie); viewer2's policy is put
// back after each run, in a finally, and by the next run when one stopped half way (state.json keeps it). Each run's steps
// are written into the probe's folder with the items' ids, and run through webcheck.mjs with the details probe
// (webchecks/probes/userratings-details-probe.js): on an item a parent hides the section is never mounted, and when the
// page asked for the item the answer was the 404; on the copies the member can open the section is there and names no
// library of a copy they can't.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';
import { PRODUCTION_PORT } from './sandbox-policy.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const NOT_FOUND = "This item wasn't found, or it can't be rated.";
const PROBE = 'ur-visibility-probe';
const LIB_A = 'UR Visibility A';
const LIB_B = 'UR Visibility B';
const LIB_M = 'UR Visibility Movies';
const SERIES_FOLDER = 'Test Series (2020) [tvdbid-900401]';
const MOVIE_FOLDER = 'Tagged Movie (2012) [tmdbid-900001]';
const RATED_FOLDER = 'Rated Folder';
const USERS = ['urvis-kid', 'urvis-tagger'];

function usage() {
    return 'usage: node scripts/userratings-visibility-probe.mjs --sandbox <name> [--remove-media] [--details] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, removeMedia: false, details: false, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--remove-media') o.removeMedia = true;
        else if (a === '--details') o.details = true;
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const sandboxUsers = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
const admin = sandboxUsers.admin;
if (!admin || !admin.token || !admin.id) throw new Error('no admin in credentials.json; run Initialize-Sandbox.ps1');
const viewer2 = sandboxUsers.viewer2;
if (opts.details && (!viewer2 || !viewer2.id)) throw new Error('no viewer2 in credentials.json; run Initialize-Sandbox.ps1');

// The sandbox's own folders only: the probe's work folder (its media copies and its state), and the shared media it copies from.
const inside = (child, parent) => {
    const rel = path.relative(path.resolve(parent), path.resolve(child));
    return rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel);
};
const workDir = path.join(sandboxRoot, 'visibility-probe');
const mediaDir = path.join(workDir, 'media');
const sharedMedia = path.join(repoRoot, '.sandbox', 'media');
for (const p of [workDir, mediaDir]) {
    if (!inside(p, sandboxRoot) || /programdata/i.test(p)) throw new Error(`refusing to work in ${p}: not in the sandbox ${sandboxRoot}`);
}
if (/programdata/i.test(sharedMedia)) throw new Error(`refusing ${sharedMedia}`);
const stateFile = path.join(workDir, 'state.json');

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, time: new Date().toISOString() } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

const tokens = { admin: admin.token };
const ids = { admin: admin.id };
function authorization(who) {
    const device = `${PROBE}-${who}`;
    const token = tokens[who];
    return `MediaBrowser Client="${PROBE}", Device="sandbox", DeviceId="${device}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// One request as `who` ('admin', a probe user's name, or 'anonymous'). Never follows redirects.
async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') h.Authorization = authorization(who);
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
const item = (id) => `/UserRatings/Items/${id}`;
const pick = (r) => (r.json && typeof r.json.title === 'string' ? { status: r.status, detail: r.json.detail ?? null } : { status: r.status });
const copies = (dto) => ((dto && dto.OtherCopies) || []).map((c) => ({ id: guid(c.ItemId), library: c.LibraryName, count: c.Count }));

// What the probe made, kept on disk until it is gone again.
function loadState() {
    try { return readJson(stateFile); } catch { return { users: [], libraries: [], ratings: [] }; }
}

// viewer2's parental limits as they were, kept on disk while the --details runs change them.
async function viewer2Limits() {
    const u = await call('admin', 'GET', `/Users/${viewer2.id}`);
    if (u.status !== 200) throw new Error(`GET viewer2: ${u.status}`);
    return { MaxParentalRating: u.json.Policy.MaxParentalRating ?? null, BlockUnratedItems: u.json.Policy.BlockUnratedItems || [] };
}

async function setViewer2Limits(limits) {
    const u = await call('admin', 'GET', `/Users/${viewer2.id}`);
    const policy = u.json.Policy;
    policy.MaxParentalRating = limits.MaxParentalRating;
    policy.BlockUnratedItems = limits.BlockUnratedItems;
    const r = await call('admin', 'POST', `/Users/${viewer2.id}/Policy`, { body: policy });
    if (r.status !== 204) throw new Error(`POST viewer2's policy: ${r.status}`);
}

// viewer2's limits put back from state.json (after a run, or what a stopped run left).
async function restoreViewer2() {
    const s = loadState();
    if (!s.viewer2) return null;
    await setViewer2Limits(s.viewer2);
    const back = await viewer2Limits();
    if (JSON.stringify(back) === JSON.stringify(s.viewer2)) {
        delete s.viewer2;
        saveState(s);
    }
    return back;
}

// One webcheck run as viewer2 over the items: hidden ones (no section, the item's read 404 when the page made it) and
// shown ones (the section mounted, no library named it must not name). Its steps are written into the probe's folder.
function detailsRun(label, hidden, shown) {
    const probeFile = path.join(repoRoot, 'scripts', 'webchecks', 'probes', 'userratings-details-probe.js');
    const steps = [
        { name: 'the details probe in every document', initScript: { file: probeFile } },
        { name: 'sign in as viewer2', login: 'viewer2' },
        { name: 'desktop layout', eval: "(localStorage.removeItem('layout'), true)" },
        { name: 'reload', reload: true },
        { name: 'User Ratings started', waitFor: '!!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)', timeout: 40000 }
    ];
    for (const h of hidden) {
        steps.push(
            { name: `home before ${h.label}`, script: "location.hash = '#/home'; return true;" },
            { sleep: 1500 },
            { name: `${h.label}: its details page (the member can't see it)`, script: `location.hash = '#/details?id=${h.id}'; return true;` },
            { sleep: 7000 },
            { name: `${h.label}: what the page shows`, eval: `({ sections: document.querySelectorAll('.jfur-section').length, reads: __jfurDetails.requests.filter(r => r.path === '/UserRatings/Items/${h.id}').map(r => r.method + ' ' + r.status), hash: location.hash.slice(0, 60), page: !!document.querySelector('#itemDetailPage:not(.hide)') })`, save: h.key, full: true },
            { name: `${h.label}: no section, and no read of the item answered but 404`, assert: `__vars.${h.key}.sections === 0 && __vars.${h.key}.reads.every(r => r === 'GET 404')` }
        );
    }
    for (const v of shown) {
        steps.push(
            { name: `${v.label}: its details page`, script: `location.hash = '#/details?id=${v.id}'; return true;` },
            { name: `${v.label}: the section mounted`, waitFor: `(() => { const s = __jfurDetails.section(); return !!s && s.getAttribute('data-jfur-item') === '${v.id}' && !!s.querySelector('.jfur-summary[data-jfur-summary]'); })()`, timeout: 30000 },
            { sleep: 500 },
            { name: `${v.label}: the section's copies and text`, eval: '(() => { const s = __jfurDetails.state(); return { copies: s.copies, text: __jfurDetails.section().textContent }; })()', save: v.key, full: true },
            { name: `${v.label}: ${v.names ? 'names ' + JSON.stringify(v.names) + ', ' : ''}never ${JSON.stringify(v.never)}`, assert: `${JSON.stringify(v.names || [])}.every(n => __vars.${v.key}.copies.some(c => c.indexOf(n) >= 0)) && __vars.${v.key}.text.indexOf(${JSON.stringify(v.never)}) < 0` }
        );
    }
    steps.push({ name: 'no uncaught exception', noExceptions: true, ignore: '/CustomRatings/script|^Error: player cannot be null' });
    const file = path.join(workDir, `details-${label}.json`);
    const out = path.join(workDir, `details-${label}-result.json`);
    if (!inside(file, workDir) || !inside(out, workDir)) throw new Error('refusing to write outside the probe\'s folder');
    fs.writeFileSync(file, JSON.stringify({ name: `User Ratings' details section as viewer2 with ${label}'s limits (userratings-visibility-probe.mjs --details)`, failOnException: false, timeout: 30000, steps }, null, 2));
    const run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', file, '--out', out], { encoding: 'utf8', timeout: 600000, maxBuffer: 64 * 1024 * 1024 });
    let result = null;
    try { result = readJson(out); } catch { result = null; }
    const done = result && result.steps ? result.steps : [];
    const failed = done.find((st) => !st.ok && !st.optional);
    const values = Object.fromEntries(done.filter((st) => st.save || /what the page shows|copies and text/.test(st.name || '')).map((st) => [st.name, st.value]));
    return { pass: run.status === 0 && !!result && result.ok === true && !failed, steps: done.length, ok: done.filter((st) => st.ok).length, failed: failed ? { name: failed.name, error: failed.error } : null, values };
}
function saveState(s) {
    fs.mkdirSync(workDir, { recursive: true });
    fs.writeFileSync(stateFile, JSON.stringify(s, null, 2));
}
function remember(kind, entry) {
    const s = loadState();
    if (!s[kind].some((e) => JSON.stringify(e) === JSON.stringify(entry))) s[kind].push(entry);
    saveState(s);
}

async function rate(who, id, value) {
    const r = await call(who, 'PUT', `${item(id)}/Mine/Rating`, { body: { Stars: value } });
    if (r.status === 200 && r.json && r.json.Mine) remember('ratings', { who, ratingId: r.json.Mine.Id });
    return r;
}

function copyTree(src, dst) {
    if (!inside(dst, mediaDir)) throw new Error(`refusing to copy into ${dst}`);
    fs.mkdirSync(dst, { recursive: true });
    for (const e of fs.readdirSync(src, { withFileTypes: true })) {
        const s = path.join(src, e.name);
        const d = path.join(dst, e.name);
        if (e.isDirectory()) copyTree(s, d);
        else if (!fs.existsSync(d)) fs.copyFileSync(s, d);
    }
}

function libraryOptions(types) {
    return {
        LibraryOptions: {
            Enabled: true, EnableRealtimeMonitor: false, EnableInternetProviders: false, SaveLocalMetadata: false,
            EnableChapterImageExtraction: false, ExtractChapterImagesDuringLibraryScan: false, EnableTrickplayImageExtraction: false,
            ExtractTrickplayImagesDuringLibraryScan: false, EnableEmbeddedTitles: false, AutomaticRefreshIntervalDays: 0,
            MetadataSavers: [], DisabledSubtitleFetchers: [],
            TypeOptions: types.map((t) => ({ Type: t, MetadataFetchers: [], MetadataFetcherOrder: [], ImageFetchers: [], ImageFetcherOrder: [] }))
        }
    };
}

async function libraries() {
    return (await call('admin', 'GET', '/Library/VirtualFolders')).json || [];
}

// POST /Library/Refresh, then waits until the RefreshLibrary task is idle again with a run that ended after the start.
async function scan() {
    const started = Date.now();
    const r = await call('admin', 'POST', '/Library/Refresh');
    if (r.status !== 204) throw new Error(`POST /Library/Refresh: ${r.status}`);
    await delay(2000);
    for (let i = 0; i < 150; i++) {
        const tasks = (await call('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } })).json || [];
        const task = tasks.find((t) => t.Key === 'RefreshLibrary');
        if (task && task.State === 'Idle' && task.LastExecutionResult && Date.parse(task.LastExecutionResult.EndTimeUtc) >= started - 1000) return task.LastExecutionResult.Status;
        await delay(2000);
    }
    throw new Error('the library scan did not finish within 5 minutes');
}

async function children(libraryName, types) {
    const library = (await libraries()).find((l) => l.Name === libraryName);
    if (!library) throw new Error(`no library "${libraryName}"`);
    const r = await call('admin', 'GET', '/Items', {
        query: { userId: admin.id, ParentId: library.ItemId, IncludeItemTypes: types, Recursive: 'true', Fields: 'ParentId,Path,ProviderIds,OfficialRating', Limit: 200 }
    });
    return (r.json && r.json.Items) || [];
}

const episode = (list, season, number) => list.find((e) => e.Type === 'Episode' && e.ParentIndexNumber === season && e.IndexNumber === number);

// The admin edits an item's metadata from its full DTO (POST /Items/{id}), then reads it back.
async function edit(id, change) {
    const got = await call('admin', 'GET', `/Users/${admin.id}/Items/${id}`);
    if (got.status !== 200) throw new Error(`GET item ${id}: ${got.status}`);
    const dto = got.json;
    change(dto);
    const w = await call('admin', 'POST', `/Items/${id}`, { body: dto });
    if (w.status !== 204) throw new Error(`POST /Items/${id}: ${w.status} ${w.text.slice(0, 200)}`);
    const back = (await call('admin', 'GET', `/Users/${admin.id}/Items/${id}`)).json;
    return { OfficialRating: back.OfficialRating ?? null, Locked: (back.LockedFields || []).includes('OfficialRating') };
}
const rated = (rating) => (dto) => {
    dto.OfficialRating = rating;
    dto.LockedFields = [...new Set([...(dto.LockedFields || []), 'OfficialRating'])];
};

async function signIn(name, secret) {
    const res = await fetch(new URL(baseUrl + '/Users/AuthenticateByName', origin), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', Authorization: `MediaBrowser Client="${PROBE}", Device="sandbox", DeviceId="${PROBE}-${name}", Version="1.0.0"` },
        body: JSON.stringify({ Username: name, Pw: secret }),
        signal: AbortSignal.timeout(60000)
    });
    if (res.status !== 200) throw new Error(`signing in as ${name}: ${res.status}`);
    const j = await res.json();
    tokens[name] = j.AccessToken;
    ids[name] = j.User.Id;
}

// A user of the probe's own: made with a random password that lives only in this process.
async function makeUser(name) {
    const secret = crypto.randomBytes(18).toString('base64url');
    const r = await call('admin', 'POST', '/Users/New', { body: { Name: name, Password: secret } });
    if (r.status !== 200) throw new Error(`POST /Users/New ${name}: ${r.status} ${r.text.slice(0, 200)}`);
    remember('users', { name, id: r.json.Id });
    await signIn(name, secret);
}

async function setPolicy(name, change) {
    const user = await call('admin', 'GET', `/Users/${ids[name]}`);
    const policy = user.json.Policy;
    change(policy);
    const r = await call('admin', 'POST', `/Users/${ids[name]}/Policy`, { body: policy });
    if (r.status !== 204) throw new Error(`POST /Users/${name}/Policy: ${r.status} ${r.text.slice(0, 200)}`);
}

// Everything a run made, gone again: the ratings by their owners, then the users, then the libraries.
async function cleanUp(label) {
    const outcome = { ratings: [], users: [], libraries: [], viewer2: null };
    if (loadState().viewer2) outcome.viewer2 = await restoreViewer2();
    const s = loadState();
    for (const u of s.users) {
        if (!tokens[u.name]) {
            // A run that stopped half way: the admin resets the password (Jellyfin's "reset" leaves it empty) to sign in again.
            const reset = await call('admin', 'POST', '/Users/Password', { query: { userId: u.id }, body: { ResetPassword: true } });
            if (reset.status === 204) await signIn(u.name, '').catch(() => null);
        }
    }
    for (const r of s.ratings) {
        if (!tokens[r.who]) { outcome.ratings.push({ who: r.who, status: 'no session' }); continue; }
        const d = await call(r.who, 'DELETE', `/UserRatings/Mine/${r.ratingId}`);
        outcome.ratings.push({ who: r.who, status: d.status });
    }
    s.ratings = s.ratings.filter((r, i) => ![204, 404].includes(outcome.ratings[i].status));
    for (const u of s.users) {
        // A user whose rating couldn't be deleted stays, so the next run can try again (never an orphaned row).
        if (s.ratings.some((r) => r.who === u.name)) { outcome.users.push({ name: u.name, status: 'kept' }); continue; }
        const d = await call('admin', 'DELETE', `/Users/${u.id}`);
        outcome.users.push({ name: u.name, status: d.status });
        if (d.status === 204 || d.status === 404) delete tokens[u.name];
    }
    s.users = s.users.filter((u, i) => ![204, 404].includes(outcome.users[i].status));
    const present = (await libraries()).map((l) => l.Name);
    for (const name of s.libraries) {
        if (!present.includes(name)) { outcome.libraries.push({ name, status: 'absent' }); continue; }
        const d = await call('admin', 'DELETE', '/Library/VirtualFolders', { query: { name, refreshLibrary: 'false' } });
        outcome.libraries.push({ name, status: d.status });
    }
    s.libraries = s.libraries.filter((name, i) => !['absent', 204].includes(outcome.libraries[i].status));
    saveState(s);
    if (outcome.libraries.some((l) => l.status === 204)) outcome.scan = await scan();
    evidence[`cleanUp-${label}`] = outcome;
    return outcome;
}

async function records() {
    const d = await call('admin', 'GET', '/UserRatings/Admin/Diagnostics');
    return d.json ? { attached: d.json.Records?.Attached ?? null, store: d.json.Store?.Status ?? null, version: d.json.PluginVersion } : null;
}

// ------------------------------------------------------------------------------------------------------------- the run

const started = Date.now();
let ran = false;
try {
    const diagnostics = await call('admin', 'GET', '/UserRatings/Admin/Diagnostics');
    if (diagnostics.status !== 200) throw new Error(`GET /UserRatings/Admin/Diagnostics: ${diagnostics.status}; is User Ratings loaded?`);
    const leftovers = loadState();
    if (leftovers.users.length || leftovers.libraries.length || leftovers.ratings.length || leftovers.viewer2) await cleanUp('leftovers');
    const before = await records();
    evidence.before = before;
    check('User Ratings 0.3.3 or later is loaded, its database Ok', before && /^(0\.3\.(3|[4-9]|\d\d)|[1-9]\d*\.\d+\.\d+)\./.test(before.version) && before.store === 'Ok', before);
    ran = true;

    // ---- 1. the media copies and the probe's libraries
    const showSource = path.join(sharedMedia, 'Shows', SERIES_FOLDER);
    const movieSource = path.join(sharedMedia, 'Movies', MOVIE_FOLDER);
    if (!fs.existsSync(showSource) || !fs.existsSync(movieSource)) throw new Error('the shared media has no Test Series or Tagged Movie; run New-SandboxMedia.ps1');
    copyTree(showSource, path.join(mediaDir, 'shows-a', SERIES_FOLDER));
    copyTree(showSource, path.join(mediaDir, 'shows-b', SERIES_FOLDER));
    copyTree(movieSource, path.join(mediaDir, 'movies', RATED_FOLDER, MOVIE_FOLDER));
    const existing = (await libraries()).map((l) => l.Name);
    for (const [name, folder, type, kinds] of [[LIB_A, 'shows-a', 'tvshows', ['Series', 'Season', 'Episode']], [LIB_B, 'shows-b', 'tvshows', ['Series', 'Season', 'Episode']], [LIB_M, 'movies', 'movies', ['Movie']]]) {
        if (existing.includes(name)) throw new Error(`a library "${name}" is there already and isn't the probe's (state.json doesn't list it); remove it first`);
        remember('libraries', name);
        const r = await call('admin', 'POST', '/Library/VirtualFolders', {
            query: { name, collectionType: type, paths: path.join(mediaDir, folder), refreshLibrary: 'false' }, body: libraryOptions(kinds)
        });
        if (r.status !== 204) throw new Error(`POST /Library/VirtualFolders ${name}: ${r.status} ${r.text.slice(0, 200)}`);
    }
    const scanned = await scan();
    const shows = await children('Shows', 'Episode');
    const inA = await children(LIB_A, 'Series,Episode');
    const inB = await children(LIB_B, 'Series,Episode');
    const inM = await children(LIB_M, 'Movie');
    const regularMovie = (await children('Movies', 'Movie')).find((m) => m.Name.startsWith('Tagged Movie'));
    const x = {
        s1: episode(shows, 1, 1), a1: episode(inA, 1, 1), a2: episode(inA, 1, 2), aSeries: inA.find((i) => i.Type === 'Series'),
        b1: episode(inB, 1, 1), b2: episode(inB, 1, 2), bSeries: inB.find((i) => i.Type === 'Series'), m1: regularMovie, m2: inM[0]
    };
    const missing = Object.entries(x).filter(([, v]) => !v).map(([k]) => k);
    if (missing.length) throw new Error(`items not found after the scan: ${missing.join(', ')}`);
    const folder = (await call('admin', 'GET', `/Users/${admin.id}/Items/${x.m2.ParentId}`)).json;
    evidence.items = Object.fromEntries(Object.entries(x).map(([k, v]) => [k, { id: guid(v.Id), name: v.Name }]));
    check('the scan made the copies: A\'s and B\'s series and episodes, and the movie inside the Folder "Rated Folder"', scanned === 'Completed' && folder && folder.Type === 'Folder'
        && folder.Name === RATED_FOLDER, { scan: scanned, folder: folder && { Type: folder.Type, Name: folder.Name } });
    x.folder = folder;

    // ---- 2. the users, their ratings before the limits, the admin's ratings
    for (const name of USERS) await makeUser(name);
    const [kid, tagger] = USERS;
    const kidA2 = await rate(kid, x.a2.Id, 3);
    const kidM2 = await rate(kid, x.m2.Id, 4);
    const taggerB2 = await rate(tagger, x.b2.Id, 2);
    const adminRates = [await rate('admin', x.a1.Id, 5), await rate('admin', x.b1.Id, 4.5), await rate('admin', x.m2.Id, 3.5)];
    check('before the limits: kid rates A S01E02 and the folder\'s movie, tagger B S01E02, the admin A\'s and B\'s S01E01 and the movie (all 200)',
        [kidA2, kidM2, taggerB2, ...adminRates].every((r) => r.status === 200), [kidA2, kidM2, taggerB2, ...adminRates].map(pick));

    // ---- 3. the limits: the series and the folder first, the episodes and the movie after them
    const edits = {
        aSeries: await edit(x.aSeries.Id, rated('TV-MA')),
        a1: await edit(x.a1.Id, rated('TV-PG')), a2: await edit(x.a2.Id, rated('TV-PG')),
        b1: await edit(x.b1.Id, rated('TV-PG')), b2: await edit(x.b2.Id, rated('TV-PG')),
        folder: await edit(x.folder.Id, rated('R')), m2: await edit(x.m2.Id, rated('PG'))
    };
    const bSeries = (await call('admin', 'GET', `/Users/${admin.id}/Items/${x.bSeries.Id}`)).json;
    check('the ratings as set: A\'s series TV-MA, A\'s and B\'s episodes TV-PG (locked), B\'s series unrated, the folder R, its movie PG (locked)',
        edits.aSeries.OfficialRating === 'TV-MA' && ['a1', 'a2', 'b1', 'b2'].every((k) => edits[k].OfficialRating === 'TV-PG' && edits[k].Locked)
        && !bSeries.OfficialRating && edits.folder.OfficialRating === 'R' && edits.m2.OfficialRating === 'PG' && edits.m2.Locked, { ...edits, bSeries: bSeries.OfficialRating ?? null });
    await setPolicy(kid, (p) => { p.MaxParentalRating = 14; });
    await setPolicy(tagger, (p) => { p.BlockUnratedItems = ['Series', 'Movie']; });

    // ---- 4. core's per-item check, and ours, on each item
    const core = async (who, v) => (await call(who, 'GET', `/Users/${ids[who]}/Items/${v.Id}`)).status;
    const ours = async (who, v) => call(who, 'GET', item(v.Id));
    const coreView = {
        kid: { s1: await core(kid, x.s1), a1: await core(kid, x.a1), a2: await core(kid, x.a2), b1: await core(kid, x.b1), m1: await core(kid, x.m1), m2: await core(kid, x.m2) },
        tagger: { a1: await core(tagger, x.a1), b1: await core(tagger, x.b1), b2: await core(tagger, x.b2) }
    };
    evidence.core = coreView;
    check('core (GET /Users/{id}/Items/{itemId}): kid opens Shows S01E01, B S01E01 and the Movies copy, not A\'s episodes (TV-MA series) nor the folder\'s movie (R folder)',
        coreView.kid.s1 === 200 && coreView.kid.b1 === 200 && coreView.kid.m1 === 200 && coreView.kid.a1 === 404 && coreView.kid.a2 === 404 && coreView.kid.m2 === 404, coreView.kid);
    check('core: tagger opens A S01E01 (a rated series, a TV-PG episode), not B\'s episodes (an unrated series)',
        coreView.tagger.a1 === 200 && coreView.tagger.b1 === 404 && coreView.tagger.b2 === 404, coreView.tagger);
    const oursHidden = [await ours(kid, x.a1), await ours(kid, x.a2), await ours(kid, x.m2), await ours(tagger, x.b1), await ours(tagger, x.b2)];
    check('ours: the same items are the one 404 for them', oursHidden.every((r) => r.status === 404 && r.json && r.json.detail === NOT_FOUND), oursHidden.map(pick));

    // The positive control: the admin sees every copy linked.
    const adminS1 = await ours('admin', x.s1);
    const adminM1 = await ours('admin', x.m1);
    check('the admin (no limits): Shows S01E01 lists A\'s and B\'s copies, the Movies copy lists the folder\'s movie (the copies are linked)',
        copies(adminS1.json).some((c) => c.id === guid(x.a1.Id)) && copies(adminS1.json).some((c) => c.id === guid(x.b1.Id)) && copies(adminM1.json).some((c) => c.id === guid(x.m2.Id)),
        { s1: copies(adminS1.json), m1: copies(adminM1.json) });

    // ---- (a) kid
    const kidS1 = await ours(kid, x.s1);
    check('(a) kid: Shows S01E01 lists B\'s copy (it opens) and never A\'s (its series is TV-MA), nor A\'s library', kidS1.status === 200
        && copies(kidS1.json).some((c) => c.id === guid(x.b1.Id)) && !copies(kidS1.json).some((c) => c.id === guid(x.a1.Id)) && !kidS1.text.includes(LIB_A), { status: kidS1.status, copies: copies(kidS1.json) });
    const kidPut = await rate(kid, x.s1.Id, 2);
    const kidClear = await call(kid, 'DELETE', `${item(x.s1.Id)}/Mine/Rating`);
    check('(a) kid: the answers of PUT Mine/Rating and DELETE Mine/Rating on Shows S01E01 never name A\'s copy either', kidPut.status === 200 && kidClear.status === 200
        && ![kidPut, kidClear].some((r) => copies(r.json).some((c) => c.id === guid(x.a1.Id)) || r.text.includes(LIB_A)), { put: copies(kidPut.json), clear: copies(kidClear.json) });

    // ---- (b) tagger
    const taggerA1 = await ours(tagger, x.a1);
    check('(b) tagger: A S01E01 opens, and lists neither B\'s copy (its series is unrated) nor B\'s library', taggerA1.status === 200
        && !copies(taggerA1.json).some((c) => c.id === guid(x.b1.Id)) && !taggerA1.text.includes(LIB_B), { status: taggerA1.status, copies: copies(taggerA1.json) });
    const taggerPut = await rate(tagger, x.a1.Id, 1.5);
    const taggerClear = await call(tagger, 'DELETE', `${item(x.a1.Id)}/Mine/Rating`);
    check('(b) tagger: the answers of PUT Mine/Rating and DELETE Mine/Rating on A S01E01 never name B\'s copy either', taggerPut.status === 200 && taggerClear.status === 200
        && ![taggerPut, taggerClear].some((r) => copies(r.json).some((c) => c.id === guid(x.b1.Id)) || r.text.includes(LIB_B)), { put: copies(taggerPut.json), clear: copies(taggerClear.json) });

    // ---- (c) kid and the movie in the R folder
    const kidM1 = await ours(kid, x.m1);
    check('(c) kid: the Movies copy of Tagged Movie opens and never lists the PG copy inside the R folder, nor its library', kidM1.status === 200
        && !copies(kidM1.json).some((c) => c.id === guid(x.m2.Id)) && !kidM1.text.includes(LIB_M), { status: kidM1.status, copies: copies(kidM1.json) });

    // ---- the reverse: the own rows on items a parent hides
    const kidList = await call(kid, 'GET', '/UserRatings/Mine/Inaccessible');
    const taggerList = await call(tagger, 'GET', '/UserRatings/Mine/Inaccessible');
    const listed = (r) => ((r.json && r.json.Items) || []).map((e) => guid(e.RatingId));
    const ownOnly = (r) => ((r.json && r.json.Items) || []).every((e) => Object.keys(e).every((k) => ['RatingId', 'Stars', 'TextSnippet', 'CreatedUtc', 'UpdatedUtc'].includes(k)));
    check('the reverse: kid\'s Mine/Inaccessible lists its rows on A S01E02 and the folder\'s movie, with its own data only', kidList.status === 200
        && listed(kidList).includes(guid(kidA2.json.Mine.Id)) && listed(kidList).includes(guid(kidM2.json.Mine.Id)) && ownOnly(kidList) && !kidList.text.includes(SERIES_FOLDER),
    { status: kidList.status, total: kidList.json && kidList.json.TotalRecordCount });
    check('the reverse: tagger\'s Mine/Inaccessible lists its row on B S01E02', taggerList.status === 200 && listed(taggerList).includes(guid(taggerB2.json.Mine.Id)) && ownOnly(taggerList),
        { status: taggerList.status, total: taggerList.json && taggerList.json.TotalRecordCount });
    const deletes = [];
    for (const [who, r] of [[kid, kidA2], [kid, kidM2], [tagger, taggerB2]]) deletes.push((await call(who, 'DELETE', `/UserRatings/Mine/${r.json.Mine.Id}`)).status);
    const kidAfter = await call(kid, 'GET', '/UserRatings/Mine/Inaccessible');
    check('the reverse: DELETE Mine/{ratingId} deletes them (204), and kid\'s list is empty after', deletes.every((s) => s === 204) && kidAfter.json && kidAfter.json.TotalRecordCount === 0,
        { deletes, left: kidAfter.json && kidAfter.json.TotalRecordCount });

    // ---- --details: the details page's section in the web client, as viewer2 with kid's limit, then tagger's
    if (opts.details) {
        const original = await viewer2Limits();
        const s = loadState();
        s.viewer2 = original;
        saveState(s);
        evidence.viewer2Limits = original;
        const id = (v) => guid(v.Id);
        try {
            await setViewer2Limits({ MaxParentalRating: 14, BlockUnratedItems: original.BlockUnratedItems });
            const kidRun = detailsRun('kid', [
                { label: 'A S01E01 (its series TV-MA)', id: id(x.a1), key: 'hiddenA1' },
                { label: 'the movie in the R folder', id: id(x.m2), key: 'hiddenM2' }
            ], [
                { label: 'Shows S01E01', id: id(x.s1), key: 'shownS1', names: [LIB_B], never: LIB_A },
                { label: 'the Movies copy of Tagged Movie', id: id(x.m1), key: 'shownM1', never: LIB_M }
            ]);
            evidence.detailsKid = kidRun;
            check(`--details, kid's limit: no section on A's episode and the movie in the R folder; Shows S01E01's names ${LIB_B} and never ${LIB_A}; the Movies copy's never ${LIB_M} (${kidRun.ok} of ${kidRun.steps} steps)`, kidRun.pass, kidRun);
            await setViewer2Limits({ MaxParentalRating: original.MaxParentalRating, BlockUnratedItems: ['Series', 'Movie'] });
            const taggerRun = detailsRun('tagger', [
                { label: 'B S01E01 (its series unrated)', id: id(x.b1), key: 'hiddenB1' }
            ], [
                { label: 'A S01E01 (a rated series)', id: id(x.a1), key: 'shownA1', never: LIB_B }
            ]);
            evidence.detailsTagger = taggerRun;
            check(`--details, tagger's limit: no section on B's episode; A S01E01's never names ${LIB_B} (${taggerRun.ok} of ${taggerRun.steps} steps)`, taggerRun.pass, taggerRun);
        } finally {
            const back = await restoreViewer2();
            check('--details: viewer2\'s limits put back as they were', JSON.stringify(back) === JSON.stringify(original) && !loadState().viewer2, { before: original, after: back });
        }
    }
} catch (e) {
    check('probe ran', false, String(e && e.stack ? e.stack.split('\n').slice(0, 4).join(' | ') : e));
} finally {
    try {
        const outcome = await cleanUp('end');
        const s = loadState();
        check('the end: the probe\'s ratings, users and libraries are gone', s.users.length === 0 && s.libraries.length === 0 && s.ratings.length === 0, outcome);
        if (ran) {
            const after = await records();
            check('the end: Records.Attached is back where it started', after && evidence.before && after.attached === evidence.before.attached, { before: evidence.before && evidence.before.attached, after: after && after.attached });
        }
    } catch (e) {
        check('the end: the probe\'s ratings, users and libraries are gone', false, String(e.message));
    }
    if (opts.removeMedia) fs.rmSync(mediaDir, { recursive: true, force: true });
}

evidence.seconds = Math.round((Date.now() - started) / 1000);
const failed = checks.filter((c) => !c.pass);
const result = { pass: failed.length === 0, checks, evidence };
if (opts.out) {
    fs.mkdirSync(path.dirname(path.resolve(opts.out)), { recursive: true });
    fs.writeFileSync(opts.out, JSON.stringify(result, null, 2));
}
for (const c of checks) console.log(`${c.pass ? 'PASS' : 'FAIL'} ${c.name}${c.pass ? '' : ` (${JSON.stringify(c.detail)})`}`);
console.log(`${checks.length - failed.length} of ${checks.length} passed`);
process.exit(failed.length === 0 ? 0 : 1);
