#!/usr/bin/env node
// The kit's item identity against a real library in a running sandbox: ItemIdentity snapshots,
// IdentityResolver with the plans' three relink policies, and MoveJournalReader, through Bookmarks' developer demo
// (/Bookmarks/Admin/IdentityDemo, behind the setting EnableIdentityDemo, which this probe turns on and restores).
// Node 22 built-ins only. Loopback only, never port 8096. The admin token comes from credentials.json and is never
// printed.
//
//   node scripts/identity-probe.mjs --sandbox default [--timeout <scan seconds>] [--out result.json]
//
// It changes the shared sandbox media (.sandbox/media) and puts every change back: it moves folders only inside
// .sandbox/media and .sandbox/<name>/identity-probe/held (same volume, plain renames), records each move in
// .sandbox/<name>/identity-probe/moves.json before making it, and undoes the recorded moves in reverse order at the end,
// on failure, and at the start of the next run if a run was killed. The synthetic move journal it writes
// (<data>/librarymaintenance/moves.jsonl of the sandbox, where the demo reads it) is deleted at the end.
//
// Scenarios (the kit's identity checks at the resolver level; library-demo-probe.mjs adds the detaching consumer):
//   1. snapshots of movies, a series, its seasons and episodes: kind, provider ids, runtime, path, container ids, keys;
//   2. every snapshot resolves to its own item (Exact by id); a blank path is never taken for a match (10.11.5's
//      FindByPath has no path filter for one) and neither is an item of another kind at the snapshot's path;
//   3. four movie folders held out and the library scanned: a movie without a twin is Unmatched; the 60 s copy of the
//      edition pair is Ambiguous with Bookmarks' runtime check (the 90 s copy fails it), Strong with the default policy
//      and Ambiguous with Exact only; one of two equal-runtime copies relinks to the other (Strong); a duplicate without
//      provider ids is only a Weak suggestion; put back and scanned: each is found again by its own id (remove and re-add);
//   4. the two-season series folder renamed and scanned: every old series, season and episode id is gone and every old
//      snapshot relinks by the series' TVDb id plus the numbers (Strong); then Season 02 held out: its episodes are
//      Unmatched, Season 01's still relink; renamed back: the old ids return (Exact);
//   5. a move journal: a movie folder moved as Library Maintenance would, with a Committed line to the id GetNewItemId
//      predicts and a Planned line that must be ignored, written before any scan (the old item still exists, so its own
//      id wins); the movie out of the library and scanned (a pending relink, Exact through the journal); at its new place
//      and scanned (the predicted id appears), and a record attached after the line doesn't follow it; then a Move back
//      line and a torn last line: the chain leads home, and the catch-up resets on a new JournalId and on a Seq below the
//      stored one.
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const MOVIE_TYPE = 'MediaBrowser.Controller.Entities.Movies.Movie';
const MOVED_FOLDER = 'identity-probe-moved';

function usage() {
    return 'usage: node scripts/identity-probe.mjs --sandbox <name> [--timeout <scan seconds>] [--out <file>]';
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
const workRoot = path.join(sandboxRoot, 'identity-probe');
const heldRoot = path.join(workRoot, 'held');
const movesFile = path.join(workRoot, 'moves.json');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^\uFEFF/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
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

// Jellyfin writes GUIDs without dashes ("N"); the kit's store format has them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="identity-probe", Device="sandbox", DeviceId="identity-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, type: res.headers.get('content-type') || '', json };
}

async function demo(method, p, options) {
    const r = await call('admin', method, `/Bookmarks/Admin/IdentityDemo${p}`, options);
    if (r.status !== 200) throw new Error(`${method} ${p}: ${r.status} ${JSON.stringify(r.json)}`);
    return r.json;
}

const capture = async (id) => (await demo('GET', `/Items/${id}`)).Identity;
const resolve = (identity, policy, attachedUtc) => demo('POST', '/Resolve', { body: identity, query: { policy, attachedUtc } });
const journalState = (journalId, lastMoveSeq) => demo('GET', '/Journal', { query: { journalId, lastMoveSeq } });

// ------------------------------------------------------------------------------------------ media moves (undoable)

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel)) throw new Error(`refusing to touch ${full}: not inside ${root}`);
    if (/^c:\\programdata\\jellyfin/i.test(full)) throw new Error(`refusing to touch ${full}`);
    return full;
}

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
    assertInside(src, path.join(repoRoot, '.sandbox'));
    assertInside(dst, path.join(repoRoot, '.sandbox'));
    if (!fs.existsSync(src)) throw new Error(`${src} doesn't exist`);
    if (fs.existsSync(dst)) throw new Error(`${dst} exists already`);
    const moves = loadMoves();
    moves.push({ from: src, to: dst });
    saveMoves(moves);
    fs.mkdirSync(path.dirname(dst), { recursive: true });
    fs.renameSync(src, dst);
}

// Undoes recorded moves, newest first; each entry is removed once undone.
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
        // The folder the journal scenario created for its move is removed again once empty; nothing else is.
        const parent = path.dirname(to);
        if (path.basename(parent) === MOVED_FOLDER && parent.startsWith(mediaRoot + path.sep) && fs.existsSync(parent) && fs.readdirSync(parent).length === 0) {
            fs.rmdirSync(parent);
        }
        moves = moves.slice(0, -1);
        saveMoves(moves);
    }
}

// ------------------------------------------------------------------------------------------ library

async function scan(label) {
    const tasksBefore = await call('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } });
    if (!tasksBefore.json) throw new Error('cannot read the scheduled tasks');
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
            return;
        }
        if (Date.now() - started > opts.timeout * 1000) throw new Error(`the scan "${label}" did not finish within ${opts.timeout} s`);
        await delay(1000);
    }
}

async function items(types) {
    const r = await call('admin', 'GET', '/Items', {
        query: { userId: users.admin.id, Recursive: 'true', IncludeItemTypes: types, Fields: 'Path,ProviderIds', EnableImages: 'false', EnableUserData: 'false' }
    });
    if (r.status !== 200) throw new Error(`GET /Items: ${r.status}`);
    return r.json.Items;
}

async function exists(id) {
    return (await call('admin', 'GET', `/Items/${id}`, { query: { userId: users.admin.id } })).status === 200;
}

function inFolder(item, folder) {
    return typeof item.Path === 'string' && item.Path.split(/[\\/]/).includes(folder);
}

function one(list, predicate, what) {
    const found = list.filter(predicate);
    if (found.length !== 1) throw new Error(`expected one ${what}, found ${found.length}; run Initialize-Sandbox.ps1 on fresh media`);
    return found[0];
}

// GetNewItemId (LibraryManager.cs:631-657): MD5 over UTF-16LE of the type's full name plus the path (lower case unless
// EnableCaseSensitiveItemIds), read as a .NET Guid (its first three fields little-endian).
function predictId(type, key, lowerCase) {
    const hash = crypto.createHash('md5').update(Buffer.from(type + (lowerCase ? key.toLowerCase() : key), 'utf16le')).digest();
    const hex = (bytes) => Buffer.from(bytes).toString('hex');
    return hex([hash[3], hash[2], hash[1], hash[0]]) + hex([hash[5], hash[4]]) + hex([hash[7], hash[6]]) + hex(hash.subarray(8));
}

const summary = (r) => ({ Outcome: r.Outcome, Strength: r.Strength, Source: r.Source, ItemId: guid(r.ItemId), IsPending: r.IsPending, Reason: r.Reason,
    Candidates: (r.Candidates || []).map((c) => ({ ItemId: guid(c.ItemId), Name: c.Name, Strength: c.Strength, Source: c.Source, PassesValidation: c.PassesValidation, RunTimeTicks: c.RunTimeTicks })),
    Chain: r.Chain ? r.Chain.Steps.map((s) => `${s.Seq}:${guid(s.OldId).slice(0, 8)}→${guid(s.NewId).slice(0, 8)}`) : null });

function isRelink(r, strength, source, itemId) {
    return r.Outcome === 'Relink' && r.Strength === strength && r.Source === source && (itemId === undefined || guid(r.ItemId) === guid(itemId));
}

// ------------------------------------------------------------------------------------------ the run

async function setDemo(enabled) {
    const p = `/Plugins/${bookmarksGuid}/Configuration`;
    const current = await call('admin', 'GET', p);
    if (current.status !== 200 || !current.json) throw new Error(`GET ${p}: ${current.status}`);
    const before = current.json.EnableIdentityDemo === true;
    if (before !== enabled) {
        const posted = await call('admin', 'POST', p, { body: { ...current.json, EnableIdentityDemo: enabled } });
        if (posted.status !== 204) throw new Error(`POST ${p}: ${posted.status}`);
    }
    return before;
}

async function run(context) {
    // The demo is off by default: 403 "turned off" for admins; viewers are refused by the admin policy either way.
    const off = await call('admin', 'GET', '/Bookmarks/Admin/IdentityDemo/Journal');
    const wasOn = off.status === 200;
    context.demoWasOn = wasOn ? true : await setDemo(true);
    if (!wasOn) {
        check('the demo is off by default: 403 with the kit\'s feature-disabled type', off.status === 403 && off.json && off.json.type === 'urn:jellyfin-plugin-kit:feature-disabled',
            { status: off.status, type: off.json && off.json.type, title: off.json && off.json.title });
    }
    const viewer = await call('viewer', 'GET', '/Bookmarks/Admin/IdentityDemo/Journal');
    check('viewer: the demo is admin-only (403)', viewer.status === 403, { status: viewer.status });

    const journal0 = await journalState();
    context.journalFile = assertInside(journal0.File, sandboxRoot);
    check('the journal is the sandbox\'s <data>/librarymaintenance/moves.jsonl, absent', /[\\/]librarymaintenance[\\/]moves\.jsonl$/.test(journal0.File) && journal0.Status === 'Missing',
        { File: path.relative(repoRoot, journal0.File), Status: journal0.Status });

    // ---- 1. snapshots
    const movies = await items('Movie');
    const movie = (folder) => one(movies, (m) => inFolder(m, folder) && m.Path.startsWith(path.join(mediaRoot, 'Movies') + path.sep), folder);
    const tagged = movie('Tagged Movie (2012) [tmdbid-900001]');
    const edition60 = movie('Edition Movie (2001) [tmdbid-900301]');
    const edition90 = movie('Edition Movie Extended (2001) [tmdbid-900301]');
    const remake1999 = movie('Same Id Remake (1999) [tmdbid-900201]');
    const remake2005 = movie('Same Id Remake (2005) [tmdbid-900201]');
    const bigBuck = movie('Big Buck Test (2008)');
    const bigBuck2 = one(movies, (m) => inFolder(m, 'Big Buck Test (2008)') && m.Path.startsWith(path.join(mediaRoot, 'Movies 2') + path.sep), 'Big Buck Test in Movies 2');
    const bladeRunner = movie('Blade Runner (1982)');
    const seriesFolder = 'Test Series (2020) [tvdbid-900401]';
    const series = one(await items('Series'), (s) => inFolder(s, seriesFolder), 'Test Series');
    const seasons = (await items('Season')).filter((s) => inFolder(s, seriesFolder)).sort((a, b) => a.IndexNumber - b.IndexNumber);
    const episodes = (await items('Episode')).filter((e) => inFolder(e, seriesFolder))
        .sort((a, b) => (a.ParentIndexNumber - b.ParentIndexNumber) || (a.IndexNumber - b.IndexNumber));
    check('the sandbox media: the series has two seasons of two episodes', seasons.length === 2 && episodes.length === 4,
        { seasons: seasons.map((s) => s.IndexNumber), episodes: episodes.map((e) => `S${e.ParentIndexNumber}E${e.IndexNumber}`) });

    const snap = {};
    for (const [key, item] of Object.entries({ tagged, edition60, edition90, remake1999, remake2005, bigBuck, bladeRunner, series })) snap[key] = await capture(item.Id);
    snap.seasons = [];
    for (const s of seasons) snap.seasons.push(await capture(s.Id));
    snap.episodes = [];
    const episodeKeys = [];
    for (const e of episodes) {
        const c = await demo('GET', `/Items/${e.Id}`);
        snap.episodes.push(c.Identity);
        episodeKeys.push(c.StrongKeys);
    }
    const e1 = snap.episodes[0];
    evidence.snapshots = { edition60: snap.edition60, episode: e1, episodeKeys: episodeKeys[0] };
    // ffprobe's durations include the encoder's padding (60.021 s for the 60 s clip).
    const seconds = (ticks) => ticks / 10_000_000;
    check('movie snapshot: kind, TMDb id from the folder tag, runtime and path', snap.edition60.Kind === 'Movie' && snap.edition60.ProviderIds.Tmdb === '900301'
        && Math.abs(seconds(snap.edition60.RunTimeTicks) - 60) < 0.5 && Math.abs(seconds(snap.edition90.RunTimeTicks) - 90) < 0.5
        && snap.edition60.Path === edition60.Path && snap.edition60.Year === 2001,
        { Kind: snap.edition60.Kind, ProviderIds: snap.edition60.ProviderIds, RunTimeTicks: [snap.edition60.RunTimeTicks, snap.edition90.RunTimeTicks] });
    check('episode snapshot: series id, season id, series TVDb id, numbers, runtime', snap.episodes.every((e, i) => guid(e.SeriesId) === guid(series.Id)
        && guid(e.SeasonId) === guid(seasons[Math.floor(i / 2)].Id) && e.SeriesProviderIds && e.SeriesProviderIds.Tvdb === '900401'
        && e.Season === episodes[i].ParentIndexNumber && e.Episode === episodes[i].IndexNumber && e.RunTimeTicks > 0 && e.Kind === 'Episode'),
        { SeriesId: e1.SeriesId, SeasonId: e1.SeasonId, SeriesProviderIds: e1.SeriesProviderIds, Season: e1.Season, Episode: e1.Episode, SeriesName: e1.SeriesName });
    check('episode keys: Episode|Tvdb|900401|S01E01', episodeKeys[0].includes('Episode|Tvdb|900401|S01E01'), episodeKeys[0]);
    check('season snapshot: series id and number', snap.seasons.every((s, i) => s.Kind === 'Season' && s.Season === seasons[i].IndexNumber && guid(s.SeriesId) === guid(series.Id)),
        snap.seasons.map((s) => ({ Season: s.Season, SeriesId: s.SeriesId })));

    // ---- 2. every snapshot resolves to its own item
    const all = [snap.tagged, snap.edition60, snap.edition90, snap.remake1999, snap.remake2005, snap.bigBuck, snap.bladeRunner, snap.series, ...snap.seasons, ...snap.episodes];
    const baseline = [];
    for (const s of all) baseline.push(await resolve(s, 'Bookmarks'));
    check('baseline: every snapshot resolves to its own item (Exact by id)', baseline.every((r, i) => isRelink(r, 'Exact', 'Id', all[i].ItemId)), baseline.map(summary).filter((r) => r.Source !== 'Id'));

    // ---- 2b. paths that are no match: blank (FindByPath(" ") has no path filter on 10.11.5 and answers with the newest
    // item of the library) and an item of another kind (a season folder) at the snapshot's path.
    const nothing = { ItemId: crypto.randomUUID(), Kind: 'Movie', Name: 'Nothing like this' };
    const blank = [];
    for (const p of [' ', '\t']) blank.push(await resolve({ ...nothing, Path: p }, 'Bookmarks'));
    check('a blank path is no match: Unmatched, not an Exact relink to the newest item', blank.every((r) => r.Outcome === 'Unmatched' && r.Strength === 'None' && !r.ItemId), blank.map(summary));
    const otherKind = await resolve({ ...nothing, Path: seasons[0].Path }, 'Bookmarks');
    const ownKind = await resolve({ ...nothing, Kind: 'Season', Path: seasons[0].Path }, 'Bookmarks');
    check('a season folder at a movie snapshot\'s path is no match; with the kind Season it is (Exact by path)', otherKind.Outcome === 'Unmatched' && !otherKind.ItemId
        && isRelink(ownKind, 'Exact', 'Path', seasons[0].Id), { otherKind: summary(otherKind), ownKind: summary(ownKind) });

    // ---- 3. movies removed and put back
    const heldMovies = ['Tagged Movie (2012) [tmdbid-900001]', 'Edition Movie (2001) [tmdbid-900301]', 'Same Id Remake (1999) [tmdbid-900201]', 'Big Buck Test (2008)'];
    const movesBefore = loadMoves().length;
    for (const folder of heldMovies) move(path.join(mediaRoot, 'Movies', folder), path.join(heldRoot, 'Movies', folder));
    await scan('four movie folders held out');
    const gone = await Promise.all([tagged, edition60, remake1999, bigBuck].map((m) => exists(m.Id)));
    check('held out: the four movies are gone from the library', gone.every((g) => !g), gone);

    const rTagged = await resolve(snap.tagged, 'Bookmarks');
    check('a removed movie without a twin: Unmatched (the record stays detached)', rTagged.Outcome === 'Unmatched' && rTagged.Strength === 'None' && rTagged.Candidates.length === 0, summary(rTagged));
    const rEditionBookmarks = await resolve(snap.edition60, 'Bookmarks');
    const cut = rEditionBookmarks.Candidates[0];
    check('edition pair, Bookmarks policy: Ambiguous, the 90 s copy fails the runtime check (not relinked)', rEditionBookmarks.Outcome === 'Ambiguous'
        && rEditionBookmarks.Reason === 'FailedValidation' && rEditionBookmarks.Candidates.length === 1 && guid(cut.ItemId) === guid(edition90.Id)
        && cut.PassesValidation === false && cut.Strength === 'Strong', summary(rEditionBookmarks));
    const rEditionDefault = await resolve(snap.edition60, 'Default');
    check('edition pair, default policy: relinks Strong to the other copy', isRelink(rEditionDefault, 'Strong', 'ProviderIds', edition90.Id), summary(rEditionDefault));
    const rEditionExact = await resolve(snap.edition60, 'ExactOnly');
    check('edition pair, Exact only: Ambiguous (Strong relinks not allowed)', rEditionExact.Outcome === 'Ambiguous' && rEditionExact.Reason === 'StrongRelinkNotAllowed', summary(rEditionExact));
    const rRemake = await resolve(snap.remake1999, 'Bookmarks');
    check('two copies of equal runtime, Bookmarks policy: relinks Strong to the other', isRelink(rRemake, 'Strong', 'ProviderIds', remake2005.Id)
        && rRemake.Candidates[0].PassesValidation === true, summary(rRemake));
    const rBigBuck = await resolve(snap.bigBuck, 'Default');
    check('a duplicate without provider ids: only a Weak suggestion, never applied', rBigBuck.Outcome === 'Unmatched' && rBigBuck.Strength === 'Weak'
        && rBigBuck.Candidates.length === 1 && guid(rBigBuck.Candidates[0].ItemId) === guid(bigBuck2.Id), summary(rBigBuck));

    undoMoves(movesBefore);
    await scan('the four movie folders put back');
    const back = [];
    for (const [s, m] of [[snap.tagged, tagged], [snap.edition60, edition60], [snap.remake1999, remake1999], [snap.bigBuck, bigBuck]]) back.push([await resolve(s, 'Bookmarks'), m]);
    check('put back: each movie is found again by its own id (remove and re-add reattaches)', back.every(([r, m]) => isRelink(r, 'Exact', 'Id', m.Id)), back.map(([r]) => summary(r)));

    // ---- 4. the series folder renamed, a season folder deleted, renamed back
    const renamedFolder = 'Test Series Renamed (2020) [tvdbid-900401]';
    const seriesMovesBefore = loadMoves().length;
    move(path.join(mediaRoot, 'Shows', seriesFolder), path.join(mediaRoot, 'Shows', renamedFolder));
    await scan('series folder renamed');
    const oldIdsGone = await Promise.all([series, ...seasons, ...episodes].map((i) => exists(i.Id)));
    const newSeries = one(await items('Series'), (s) => inFolder(s, renamedFolder), 'the renamed series');
    const newSeasons = (await items('Season')).filter((s) => inFolder(s, renamedFolder)).sort((a, b) => a.IndexNumber - b.IndexNumber);
    const newEpisodes = (await items('Episode')).filter((e) => inFolder(e, renamedFolder))
        .sort((a, b) => (a.ParentIndexNumber - b.ParentIndexNumber) || (a.IndexNumber - b.IndexNumber));
    check('renamed: every old series, season and episode id is gone, and new ones exist', oldIdsGone.every((g) => !g) && newEpisodes.length === 4
        && newEpisodes.every((e) => !episodes.some((o) => guid(o.Id) === guid(e.Id))), { oldIdsStillThere: oldIdsGone.filter(Boolean).length, newEpisodes: newEpisodes.length });
    const renamed = [];
    for (let i = 0; i < 4; i++) renamed.push(await resolve(snap.episodes[i], 'Bookmarks'));
    evidence.renamedEpisodes = renamed.map(summary);
    check('renamed: each old episode snapshot relinks by the series TVDb id plus season and episode (Strong, runtime checked)',
        renamed.every((r, i) => isRelink(r, 'Strong', 'SeriesNumbers', newEpisodes[i].Id) && r.Candidates.length === 1 && r.Candidates[0].PassesValidation === true), evidence.renamedEpisodes);
    const renamedSeasons = [];
    for (let i = 0; i < 2; i++) renamedSeasons.push(await resolve(snap.seasons[i], 'Default'));
    check('renamed: each old season snapshot relinks by the series id plus its number', renamedSeasons.every((r, i) => isRelink(r, 'Strong', 'SeriesNumbers', newSeasons[i].Id)), renamedSeasons.map(summary));
    const renamedSeries = await resolve(snap.series, 'Default');
    check('renamed: the old series snapshot relinks by its TVDb id', isRelink(renamedSeries, 'Strong', 'ProviderIds', newSeries.Id), summary(renamedSeries));
    const newEpisode = await demo('GET', `/Items/${newEpisodes[0].Id}`);
    check('renamed: the new episode\'s snapshot has the new container ids and shares a key with the old one', guid(newEpisode.Identity.SeriesId) === guid(newSeries.Id)
        && guid(newEpisode.Identity.SeasonId) === guid(newSeasons[0].Id) && newEpisode.StrongKeys.some((k) => episodeKeys[0].includes(k)),
        { SeriesId: newEpisode.Identity.SeriesId, SeasonId: newEpisode.Identity.SeasonId, shared: newEpisode.StrongKeys.filter((k) => episodeKeys[0].includes(k)) });

    move(path.join(mediaRoot, 'Shows', renamedFolder, 'Season 02'), path.join(heldRoot, 'Shows', 'Season 02'));
    await scan('Season 02 held out');
    const withoutSeason = [];
    for (let i = 0; i < 4; i++) withoutSeason.push(await resolve(snap.episodes[i], 'Bookmarks'));
    check('Season 02 deleted: its episodes are Unmatched, Season 01\'s still relink', withoutSeason.slice(2).every((r) => r.Outcome === 'Unmatched' && r.Candidates.length === 0)
        && withoutSeason.slice(0, 2).every((r, i) => isRelink(r, 'Strong', 'SeriesNumbers', newEpisodes[i].Id)), withoutSeason.map(summary));

    undoMoves(seriesMovesBefore);
    await scan('series folder and Season 02 put back');
    const restored = [];
    for (const s of [snap.series, ...snap.seasons, ...snap.episodes]) restored.push(await resolve(s, 'Bookmarks'));
    check('renamed back: the old ids return (Exact by id)', restored.every((r, i) => isRelink(r, 'Exact', 'Id', [series, ...seasons, ...episodes][i].Id)), restored.map(summary));

    // ---- 5. the move journal
    const brFolder = path.join(mediaRoot, 'Movies', 'Blade Runner (1982)');
    const brMovedFolder = path.join(mediaRoot, 'Movies', MOVED_FOLDER, 'Blade Runner (1982)');
    // The new path as the scan will spell it: the library location as the server has it, then the folders on disk.
    const brOldPath = bladeRunner.Path;
    const brNewPath = path.join(path.dirname(path.dirname(brOldPath)), MOVED_FOLDER, path.basename(path.dirname(brOldPath)), path.basename(brOldPath));
    const variant = [false, true].find((lower) => predictId(MOVIE_TYPE, brOldPath, lower) === guid(bladeRunner.Id));
    check('the id prediction (GetNewItemId) matches the movie\'s current id', variant !== undefined, { lowerCase: variant ?? null });
    const predicted = predictId(MOVIE_TYPE, brNewPath, variant === true);
    const journalId = crypto.randomUUID().replace(/-/g, '');
    const now = () => new Date().toISOString().replace(/\.\d+Z$/, 'Z');
    const line = (seq, status, oldId, newId, oldPath, newPath, extra = {}) => JSON.stringify({ V: 1, JournalId: journalId, Seq: seq, Status: status, Utc: now(),
        OldId: guid(oldId), NewId: guid(newId), Kind: 'Movie', OldPath: oldPath, NewPath: newPath, JobId: crypto.randomUUID().replace(/-/g, ''), Role: 'Primary', ...extra });
    // Library Maintenance appends the line once the files are final, before any scan (its move's last step). The movie leaves
    // the library first (the source folder's scan), and reaches its new place only later ("moved, awaiting library").
    const journalMovesBefore = loadMoves().length;
    const brHeld = path.join(heldRoot, 'Movies', 'Blade Runner (1982)');
    move(brFolder, brHeld);
    fs.mkdirSync(path.dirname(context.journalFile), { recursive: true });
    fs.writeFileSync(context.journalFile, [
        JSON.stringify({ V: 1, Header: true, JournalId: journalId, CreatedUtc: now() }),
        line(1, 'Committed', bladeRunner.Id, predicted, brOldPath, brNewPath),
        line(2, 'Planned', predicted, tagged.Id, brNewPath, tagged.Path)
    ].join('\n') + '\n');

    const unscanned = await resolve(snap.bladeRunner, 'Bookmarks');
    check('journal, before any scan: the old item is still in the library, so its own id wins (Exact by id)', isRelink(unscanned, 'Exact', 'Id', bladeRunner.Id), summary(unscanned));

    await scan('movie taken out of the library, awaiting its new place');
    const pending = await resolve(snap.bladeRunner, 'Bookmarks');
    check('journal, old id gone and new one not scanned yet: a pending relink to the predicted id (Exact through the journal)', pending.Outcome === 'Relink'
        && pending.Strength === 'Exact' && pending.Source === 'Journal' && pending.IsPending === true && guid(pending.ItemId) === predicted
        && !(await exists(bladeRunner.Id)), summary(pending));
    const j1 = await journalState();
    check('journal: Ok, one Committed line, the Planned line ignored, the predicted id pending and not in the library yet', j1.Status === 'Ok' && guid(j1.JournalId) === journalId
        && j1.Lines === 1 && j1.IgnoredLines === 1 && j1.LastSeq === 2 && j1.PendingRelinks.length === 1 && guid(j1.PendingRelinks[0].NewId) === predicted
        && j1.PendingRelinks[0].InLibrary === false, { Status: j1.Status, Lines: j1.Lines, IgnoredLines: j1.IgnoredLines, LastSeq: j1.LastSeq, PendingRelinks: j1.PendingRelinks });
    const catchUp = j1.CatchUp;
    check('journal: a consumer\'s first catch-up gives the Committed line and the position to store', catchUp.Reset === 'None' && catchUp.LastMoveSeq === 2
        && catchUp.Lines.length === 1 && guid(catchUp.Lines[0].NewId) === predicted, catchUp);

    move(brHeld, brMovedFolder);
    await scan('movie at its new place');
    const arrived = await exists(predicted);
    check('after the scan: the movie has the predicted id', arrived && !(await exists(bladeRunner.Id)), { predicted, arrived });
    const moved = await resolve(snap.bladeRunner, 'Bookmarks');
    check('journal, after the scan: Exact through the journal, no longer pending, and not onward through the Planned line', isRelink(moved, 'Exact', 'Journal', predicted)
        && moved.IsPending === false && moved.Chain.Steps.length === 1, summary(moved));
    const lineWritten = Date.parse(JSON.parse(fs.readFileSync(context.journalFile, 'utf8').split('\n')[1]).Utc);
    const attachedBefore = await resolve(snap.bladeRunner, 'Bookmarks', new Date(lineWritten - 60_000).toISOString());
    const attachedAfter = await resolve(snap.bladeRunner, 'Bookmarks', new Date(lineWritten + 1000).toISOString());
    check('journal: a record attached before the line follows it; one attached after it doesn\'t (the id may be a new file at the old path)',
        isRelink(attachedBefore, 'Exact', 'Journal', predicted) && attachedAfter.Source !== 'Journal' && attachedAfter.Chain === null,
        { attachedBefore: summary(attachedBefore), attachedAfter: summary(attachedAfter) });
    const j2 = await journalState();
    check('journal: the pending relink is in the library now', j2.PendingRelinks.length === 1 && j2.PendingRelinks[0].InLibrary === true, j2.PendingRelinks);
    const atB = await capture(predicted);

    undoMoves(journalMovesBefore);
    fs.appendFileSync(context.journalFile, line(3, 'Committed', predicted, bladeRunner.Id, brNewPath, brOldPath) + '\n' + line(4, 'Committed', bladeRunner.Id, tagged.Id, brOldPath, tagged.Path).slice(0, 60));
    await scan('movie moved back');
    const home = await resolve(atB, 'Bookmarks');
    check('Move back: a snapshot taken at the moved location follows B→A home (Exact through the journal)', isRelink(home, 'Exact', 'Journal', bladeRunner.Id), summary(home));
    const original = await resolve(snap.bladeRunner, 'Bookmarks');
    check('Move back: the original snapshot is the item again (Exact by id)', isRelink(original, 'Exact', 'Id', bladeRunner.Id), summary(original));
    const j3 = await journalState(journalId, 1);
    const reset1 = await journalState(crypto.randomUUID(), 3);
    const reset2 = await journalState(journalId, 9);
    check('journal: a torn last line is ignored; catch-up from Seq 1 gives line 3', j3.TruncatedTail === true && j3.LastSeq === 3 && j3.InvalidLines === 0
        && j3.CatchUp.Reset === 'None' && j3.CatchUp.Lines.map((l) => l.Seq).join() === '3' && j3.CatchUp.LastMoveSeq === 3,
        { TruncatedTail: j3.TruncatedTail, LastSeq: j3.LastSeq, CatchUp: j3.CatchUp });
    check('journal: a new JournalId resets and replays every Committed line', reset1.CatchUp.Reset === 'NewJournalId' && reset1.CatchUp.Lines.map((l) => l.Seq).join() === '1,3'
        && guid(reset1.CatchUp.JournalId) === journalId, reset1.CatchUp);
    check('journal: a Seq below the stored one resets and replays', reset2.CatchUp.Reset === 'SeqBelowStored' && reset2.CatchUp.Lines.length === 2 && reset2.CatchUp.LastMoveSeq === 3, reset2.CatchUp);
    evidence.journal = { journalId, predicted, lines: fs.readFileSync(context.journalFile, 'utf8').split('\n').length };
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { demoWasOn: null, journalFile: null };
    const leftover = loadMoves();
    if (leftover.length > 0) {
        undoMoves(0);
        await scan('left-over moves of an earlier run undone');
        evidence.undidLeftovers = leftover.length;
    }
    let failure = null;
    try {
        await run(context);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        const undo = loadMoves().length;
        try {
            undoMoves(0);
            if (undo > 0) await scan('clean-up: moves undone');
        } catch (error) {
            check('clean-up: every media move undone', false, String(error));
        }
        if (context.journalFile && fs.existsSync(context.journalFile)) {
            fs.rmSync(context.journalFile);
            const folder = path.dirname(context.journalFile);
            if (fs.readdirSync(folder).length === 0) fs.rmdirSync(folder);
        }
        if (context.demoWasOn === false) await setDemo(false).catch((error) => check('clean-up: the demo setting restored', false, String(error)));
        check('clean-up: no media move left, the journal removed, the demo setting as it was', loadMoves().length === 0
            && !(context.journalFile && fs.existsSync(context.journalFile)) && (context.demoWasOn !== false || (await call('admin', 'GET', '/Bookmarks/Admin/IdentityDemo/Journal')).status === 403),
            { moves: loadMoves().length, demoWasOn: context.demoWasOn });
        if (fs.existsSync(heldRoot) && fs.readdirSync(heldRoot, { recursive: true }).every((f) => fs.statSync(path.join(heldRoot, f)).isDirectory())) fs.rmSync(heldRoot, { recursive: true });
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
