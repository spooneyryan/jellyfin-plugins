#!/usr/bin/env node
// Bookmarks' My Bookmarks page endpoints in a running sandbox: GET /Bookmarks/Query and /Groups with
// every filter and sort against real bookmarks on the sandbox's movies, episodes and home videos, the availability filter on
// the stored state after a real scan detached a bookmark, ItemAvailable following a user's library access at once (library access
// and admin rights are read at each request), POST /Bookmarks/BulkDelete with real clip files, GET and POST /Bookmarks/Preferences, the 1.1
// capabilities, and the OpenAPI document's description of all of it. Node 22 built-ins only. Loopback only, never port
// 8096. The users' tokens come from credentials.json and are never printed.
//
//   node scripts/bookmarks-query-probe.mjs --sandbox default [--timeout <seconds>] [--out result.json]
//
// It changes the shared sandbox media and puts it back as bookmarks-identity-probe.mjs does: one plain rename of a movie
// folder into its hold folder .sandbox/<name>/bookmarks-query-probe/held, recorded in moves.json there before it is made,
// undone at the end, on failure, and at the start of the next run after a run was killed (then it scans once). It
// restricts viewer2's libraries for one check (the whole policy read first, and written back in a finally), sets viewer's
// naming preferences and puts back what they were, and deletes the bookmarks and clip files it made. It never restarts or
// deploys anything. Every name and note it gives starts with "query-probe".
//
// Checks:
//   1. the 1.1 capabilities (viewer): MaxNameLength, MaxNoteLength, Shortcuts, ReservedShortcuts, ShowPlayerButtons,
//      AskForName, AskForNameIsDefault, PauseWhileNaming, SuggestChapterNames, Pages;
//   2. /api-docs/openapi.json: the four new paths with their parameters, bodies and answers, and the new schemas;
//   3. Query: each filter (search, kinds comma-separated and repeated, range, clip file, named, video), each of the five
//      sorts in both orders, paging, ItemAvailable, no path in the JSON; the 400s;
//   4. Groups: one per video with its bookmarks by position, each of the three sorts in both orders; the 400s;
//   5. availability on the stored state: a movie folder held out and scanned, its bookmark Missing (Detached, not
//      ItemAvailable) and the others Available; put back and scanned, Available again;
//   6. ItemAvailable per viewer: viewer2's bookmark on a Movies 2 video is Unavailable at the next request once viewer2 may
//      no longer open Movies 2 (listed, with its stored name, not detached), and available again once access is back;
//      viewer2 never sees admin's bookmarks;
//   7. BulkDelete: without the clips a tombstone keeps its file, another user's id and an unknown one are NotFound; with
//      them the files are gone; an empty list is 400;
//   8. Preferences: viewer's round trip, the capabilities following it, bookmarks.json's entry removed at the defaults;
//   9. the time Query and Groups take; no Error of Bookmarks or the kit, and no line of theirs at Information or above
//      naming a bookmark (every name and note starts with "query-probe").
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const TICKS = 10_000_000;
const PROBE = 'query-probe';
const HELD_MOVIE = 'Big Buck Test (2008)';
const SERIES_FOLDER = 'Test Series (2020) [tvdbid-900401]';

function usage() {
    return 'usage: node scripts/bookmarks-query-probe.mjs --sandbox <name> [--timeout <seconds>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, timeout: 300, out: null };
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
const workRoot = path.join(sandboxRoot, 'bookmarks-query-probe');
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
for (const name of ['admin', 'viewer', 'viewer2']) {
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

// Jellyfin writes GUIDs without dashes ("N"); bookmarks.json writes them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (Array.isArray(v)) for (const each of v) url.searchParams.append(k, String(each));
        else if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="bookmarks-query-probe", Device="sandbox", DeviceId="bookmarks-query-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = typeof body === 'string' ? body : JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const started = performance.now();
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(180000) });
    const text = await res.text();
    const ms = Math.round((performance.now() - started) * 10) / 10;
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text, ms, type: res.headers.get('content-type') || '' };
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

// ------------------------------------------------------------------------------------------ bookmarks

const made = [];

async function createBookmark(who, item, { start, end = null, name = null, note = null }) {
    const body = { ItemId: item.Id, StartTicks: start * TICKS, EndTicks: end === null ? null : end * TICKS, Name: name, Note: note };
    const r = await call(who, 'POST', '/Bookmarks', { body });
    if (r.status !== 201 || !r.json) throw new Error(`POST /Bookmarks on ${item.Name}: ${r.status} ${r.text.slice(0, 200)}`);
    made.push({ who, id: r.json.Id });
    await delay(20);
    return r.json;
}

async function query(who, q = {}, endpoint = 'Query') {
    const r = await call(who, 'GET', `/Bookmarks/${endpoint}`, { query: { searchTerm: PROBE, limit: 200, ...q } });
    if (r.status !== 200 || !r.json) throw new Error(`GET /Bookmarks/${endpoint} ${JSON.stringify(q)}: ${r.status} ${r.text.slice(0, 300)}`);
    return r.json;
}

const ids = (list) => list.map((b) => guid(b.Id));
const labels = (list, byId) => list.map((b) => byId.get(guid(b.Id)) || '?');

async function waitForClip(id) {
    return waitFor(`the clip of ${id} completed`, async () => {
        const r = await call('admin', 'GET', `/Bookmarks/${id}`);
        const status = r.json && r.json.Clip && r.json.Clip.Status;
        if (status && !['Queued', 'Running'].includes(status)) return r.json;
        return null;
    }, opts.timeout);
}

function clipOnDisk(fileName) {
    return !!fileName && fs.existsSync(path.join(clipRoot, 'admin', fileName));
}

async function openApiDocument() {
    const until = Date.now() + opts.timeout * 1000;
    for (;;) {
        const r = await call('anonymous', 'GET', '/api-docs/openapi.json');
        if (r.status === 200 && r.json) return r;
        if (Date.now() > until) throw new Error(`/api-docs/openapi.json: ${r.status} after ${opts.timeout} s`);
        await delay(2000);
    }
}

// The schema name a $ref, a nullable allOf or an array's items point at.
function refName(schema) {
    if (!schema) return null;
    if (schema.$ref) return schema.$ref.split('/').pop();
    if (Array.isArray(schema.allOf) && schema.allOf.length === 1) return refName(schema.allOf[0]);
    if (Array.isArray(schema.oneOf) && schema.oneOf.length === 1) return refName(schema.oneOf[0]);
    return null;
}

// ------------------------------------------------------------------------------------------ the run

async function run(context) {
    // ---- 1. the 1.1 capabilities
    const caps = await call('viewer', 'GET', '/Bookmarks/Capabilities');
    const c = caps.json || {};
    // At first ReservedShortcuts was [] and Pages.Bookmarks false; since the kit's Jellyfin Enhanced interop the list holds
    // the keys jellyfin-web's player and Jellyfin Enhanced take (K among them, never our Shift+B), since the My Bookmarks
    // page the capabilities offer it, and since My Clips that page too.
    check('the capabilities carry the 1.1 fields with the defaults (viewer): MaxNameLength 100, MaxNoteLength 500, Shortcuts.BookmarkNow Shift+B, ReservedShortcuts the player\'s and JE\'s keys (K, never Shift+B), ShowPlayerButtons, AskForName from the server default, PauseWhileNaming off, SuggestChapterNames, Pages {Bookmarks: true, Clips: true}',
        caps.status === 200 && c.MaxNameLength === 100 && c.MaxNoteLength === 500 && same(c.Shortcuts, { BookmarkNow: 'Shift+B' })
        && Array.isArray(c.ReservedShortcuts) && c.ReservedShortcuts.includes('K') && !c.ReservedShortcuts.includes('Shift+B')
        && c.ShowPlayerButtons === true && typeof c.AskForName === 'boolean' && typeof c.AskForNameIsDefault === 'boolean' && typeof c.PauseWhileNaming === 'boolean'
        && c.SuggestChapterNames === true && same(c.Pages, { Bookmarks: true, Clips: true }) && c.IsAdmin === false && c.StoreReadOnly === false, c);

    // ---- 2. the OpenAPI document
    const docResponse = await openApiDocument();
    const doc = docResponse.json;
    const schemas = (doc.components && doc.components.schemas) || {};
    const op = (p, m) => (doc.paths[p] || {})[m] || null;
    const params = (o) => Object.fromEntries(((o && o.parameters) || []).map((p) => [p.name.toLowerCase(), p]));
    const qp = params(op('/Bookmarks/Query', 'get'));
    const gp = params(op('/Bookmarks/Groups', 'get'));
    const filters = ['searchterm', 'itemid', 'itemtypes', 'hasrange', 'hasclip', 'named', 'availability', 'sortorder', 'startindex', 'limit', 'sortby'];
    const okResponse = (o) => {
        const content = o && o.responses && o.responses['200'] && o.responses['200'].content;
        return content ? refName((content['application/json'] || Object.values(content)[0] || {}).schema) : null;
    };
    const body = (o) => {
        const content = o && o.requestBody && o.requestBody.content;
        return content ? refName((content['application/json'] || Object.values(content)[0] || {}).schema) : null;
    };
    const enumOf = (name) => (schemas[name] && schemas[name].enum) || null;
    const openApi = {
        status: docResponse.status,
        bookmarkPaths: Object.keys(doc.paths).filter((p) => /^\/Bookmarks(\/|$)/i.test(p)).length,
        queryParameters: Object.keys(qp),
        itemTypes: qp.itemtypes && { in: qp.itemtypes.in, type: qp.itemtypes.schema && qp.itemtypes.schema.type, items: refName(qp.itemtypes.schema && qp.itemtypes.schema.items) },
        querySortBy: qp.sortby && refName(qp.sortby.schema),
        groupSortBy: gp.sortby && refName(gp.sortby.schema),
        responses: {
            query: okResponse(op('/Bookmarks/Query', 'get')), groups: okResponse(op('/Bookmarks/Groups', 'get')),
            bulk: okResponse(op('/Bookmarks/BulkDelete', 'post')), preferences: okResponse(op('/Bookmarks/Preferences', 'get'))
        },
        bodies: { bulk: body(op('/Bookmarks/BulkDelete', 'post')), preferences: body(op('/Bookmarks/Preferences', 'post')) },
        preferencesPost: op('/Bookmarks/Preferences', 'post') && Object.keys(op('/Bookmarks/Preferences', 'post').responses),
        enums: {
            BookmarkSortBy: enumOf('BookmarkSortBy'), BookmarkGroupSortBy: enumOf('BookmarkGroupSortBy'),
            BookmarkItemType: enumOf('BookmarkItemType'), BookmarkAvailability: enumOf('BookmarkAvailability'), BookmarkOrigin: enumOf('BookmarkOrigin')
        },
        dtoFields: Object.keys((schemas.BookmarkDto && schemas.BookmarkDto.properties) || {}).filter((k) => ['Year', 'SeriesName', 'SeasonNumber', 'EpisodeNumber', 'ItemAvailable', 'Origin'].includes(k)),
        groupFields: Object.keys((schemas.BookmarkGroupDto && schemas.BookmarkGroupDto.properties) || {}),
        capabilityFields: Object.keys((schemas.BookmarkCapabilitiesDto && schemas.BookmarkCapabilitiesDto.properties) || {}).filter((k) => ['MaxNameLength', 'MaxNoteLength', 'Shortcuts', 'ReservedShortcuts', 'ShowPlayerButtons', 'AskForName', 'AskForNameIsDefault', 'PauseWhileNaming', 'SuggestChapterNames', 'Pages'].includes(k))
    };
    evidence.openApi = openApi;
    check('OpenAPI: GET /Bookmarks/Query and /Groups document every filter as a query parameter (itemTypes an array of BookmarkItemType), each its own sortBy enum, and answer BookmarkDtoQueryResult and BookmarkGroupDtoQueryResult',
        filters.every((f) => qp[f] && qp[f].in === 'query') && filters.every((f) => gp[f] && gp[f].in === 'query')
        && openApi.itemTypes.type === 'array' && openApi.itemTypes.items === 'BookmarkItemType'
        && openApi.querySortBy === 'BookmarkSortBy' && openApi.groupSortBy === 'BookmarkGroupSortBy'
        && openApi.responses.query === 'BookmarkDtoQueryResult' && openApi.responses.groups === 'BookmarkGroupDtoQueryResult', openApi);
    check('OpenAPI: POST /Bookmarks/BulkDelete (BookmarkBulkDeleteRequest → BookmarkBulkDeleteResultDto) and GET/POST /Bookmarks/Preferences (BookmarkPreferencesDto, 204) are there',
        openApi.bodies.bulk === 'BookmarkBulkDeleteRequest' && openApi.responses.bulk === 'BookmarkBulkDeleteResultDto'
        && openApi.responses.preferences === 'BookmarkPreferencesDto' && openApi.bodies.preferences === 'BookmarkPreferencesDto'
        && (openApi.preferencesPost || []).includes('204'), { bodies: openApi.bodies, responses: openApi.responses, preferencesPost: openApi.preferencesPost });
    check('OpenAPI: the enums list exactly their values, BookmarkDto has the 1.1 fields, BookmarkGroupDto and the capabilities too',
        same(openApi.enums.BookmarkSortBy, ['UpdatedAt', 'CreatedAt', 'Name', 'ItemName', 'Position'])
        && same(openApi.enums.BookmarkGroupSortBy, ['LatestUpdated', 'ItemName', 'Count'])
        && same(openApi.enums.BookmarkItemType, ['Movie', 'Episode', 'Other'])
        && same(openApi.enums.BookmarkAvailability, ['All', 'Available', 'Missing'])
        && same(openApi.enums.BookmarkOrigin, ['Native', 'JellyfinEnhanced', 'Adopted'])
        && openApi.dtoFields.length === 6 && openApi.capabilityFields.length === 10
        && ['ItemId', 'ItemName', 'ItemAvailable', 'Count', 'LatestUpdatedAt', 'Bookmarks', 'BookmarksTruncated'].every((k) => openApi.groupFields.includes(k)),
        { enums: openApi.enums, dtoFields: openApi.dtoFields, capabilityFields: openApi.capabilityFields, groupFields: openApi.groupFields });

    // ---- the bookmarks
    const movies = await items('Movie');
    const longTest = one(movies, (m) => inFolder(m, 'Long Test Movie (2021)'), 'Long Test Movie');
    const bigBuck = one(movies, (m) => inFolder(m, HELD_MOVIE) && m.Path.startsWith(path.join(mediaRoot, 'Movies') + path.sep), HELD_MOVIE);
    const blade2049 = one(movies, (m) => typeof m.Path === 'string' && path.basename(m.Path) === 'Blade Runner 2049.mkv', 'Blade Runner 2049');
    const episodes = (await items('Episode')).filter((e) => inFolder(e, SERIES_FOLDER))
        .sort((a, b) => (a.ParentIndexNumber - b.ParentIndexNumber) || (a.IndexNumber - b.IndexNumber));
    const homeVideo = one(await items('Video'), (v) => typeof v.Path === 'string' && path.basename(v.Path) === 'birthday cake.mkv', 'the birthday home video');
    if (episodes.length < 2) throw new Error('the test series needs two episodes; run Initialize-Sandbox.ps1 on fresh media');

    const a = await createBookmark('admin', longTest, { start: 10, end: 14, name: `${PROBE} Alpha` });
    const b = await createBookmark('admin', longTest, { start: 30, note: `${PROBE} note bravo` });
    const cc = await createBookmark('admin', bigBuck, { start: 5, name: `${PROBE} Charlie` });
    const d = await createBookmark('admin', episodes[0], { start: 5, end: 8, name: `${PROBE} delta` });
    const e = await createBookmark('admin', episodes[1], { start: 2, note: `${PROBE} echo` });
    const f = await createBookmark('admin', homeVideo, { start: 1, name: `${PROBE} Foxtrot` });
    const g = await createBookmark('admin', longTest, { start: 20, end: 24, name: `${PROBE} Golf` });
    const v1 = await createBookmark('viewer', longTest, { start: 40, note: `${PROBE} viewer` });
    const w1 = await createBookmark('viewer2', blade2049, { start: 3, name: `${PROBE} Whiskey` });
    const byId = new Map([[a, 'a'], [b, 'b'], [cc, 'c'], [d, 'd'], [e, 'e'], [f, 'f'], [g, 'g'], [v1, 'v1'], [w1, 'w1']].map(([x, l]) => [guid(x.Id), l]));
    const L = (list) => labels(list, byId);
    evidence.bookmarks = { a: 'Long Test Movie 0:10–0:14 "Alpha"', b: 'Long Test Movie 0:30 (note)', c: `${HELD_MOVIE} 0:05 "Charlie"`, d: 'S01E01 0:05–0:08 "delta"', e: 'S01E02 0:02 (note)', f: 'home video 0:01 "Foxtrot"', g: 'Long Test Movie 0:20–0:24 "Golf"', v1: 'viewer, Long Test Movie', w1: 'viewer2, Blade Runner 2049 (Movies 2)' };

    // ---- 3. Query
    const all = await query('admin');
    const rows = all.Items;
    check('Query: admin\'s seven probe bookmarks (viewer\'s not among them), most recently changed first, each ItemAvailable, Origin Native, the snapshot\'s year and series, and no path in the JSON',
        all.TotalRecordCount === 7 && all.StartIndex === 0 && same(L(rows), ['g', 'f', 'e', 'd', 'c', 'b', 'a'])
        && rows.every((r) => r.ItemAvailable === true && r.Origin === 'Native' && r.Detached === false)
        && rows.find((r) => guid(r.Id) === guid(d.Id)).SeriesName.startsWith('Test Series') && rows.find((r) => guid(r.Id) === guid(d.Id)).SeasonNumber === 1
        && !/\.sandbox|\\\\media\\\\|:\\\\/i.test(JSON.stringify(all)),
        { total: all.TotalRecordCount, order: L(rows), sample: rows.map((r) => ({ l: byId.get(guid(r.Id)), ItemType: r.ItemType, Year: r.Year, SeriesName: r.SeriesName, SeasonNumber: r.SeasonNumber, EpisodeNumber: r.EpisodeNumber, ItemAvailable: r.ItemAvailable, Origin: r.Origin })) });

    const filtered = {
        movies: L((await query('admin', { itemTypes: 'Movie' })).Items),
        episodes: L((await query('admin', { itemTypes: 'Episode' })).Items),
        other: L((await query('admin', { itemTypes: 'Other' })).Items),
        commaSeparated: L((await query('admin', { itemTypes: 'Movie,Episode' })).Items),
        repeated: L((await query('admin', { itemTypes: ['movie', 'Episode'] })).Items),
        ranges: L((await query('admin', { hasRange: 'true' })).Items),
        points: L((await query('admin', { hasRange: 'false' })).Items),
        named: L((await query('admin', { named: 'true' })).Items),
        unnamed: L((await query('admin', { named: 'false' })).Items),
        onLongTest: L((await query('admin', { itemId: longTest.Id })).Items),
        byNameIgnoringCase: L((await query('admin', { searchTerm: 'QUERY-PROBE CHARLIE' })).Items),
        byNote: L((await query('admin', { searchTerm: 'bravo' })).Items),
        combined: L((await query('admin', { itemTypes: 'Movie', named: 'false', hasRange: 'false' })).Items)
    };
    evidence.filters = filtered;
    check('Query filters: kinds (Movie, Episode, Other for the home video; comma-separated and repeated alike), ranges and points, named and unnamed, one video, the search in names and notes ignoring case, combined',
        same(filtered.movies, ['g', 'c', 'b', 'a']) && same(filtered.episodes, ['e', 'd']) && same(filtered.other, ['f'])
        && same(filtered.commaSeparated, ['g', 'e', 'd', 'c', 'b', 'a']) && same(filtered.repeated, filtered.commaSeparated)
        && same(filtered.ranges, ['g', 'd', 'a']) && same(filtered.points, ['f', 'e', 'c', 'b'])
        && same(filtered.named, ['g', 'f', 'd', 'c', 'a']) && same(filtered.unnamed, ['e', 'b'])
        && same(filtered.onLongTest, ['g', 'b', 'a']) && same(filtered.byNameIgnoringCase, ['c']) && same(filtered.byNote, ['b'])
        && same(filtered.combined, ['b']), filtered);

    // The series name alone (it is in the episodes' display names and their snapshots) finds them; asked without the probe's
    // term, so earlier bookmarks of admin's on the series may come too.
    const series = await call('admin', 'GET', '/Bookmarks/Query', { query: { searchTerm: 'TEST series', itemTypes: 'Episode', limit: 200 } });
    check('Query search: the series name finds the episodes\' bookmarks, ignoring case', series.status === 200
        && ['d', 'e'].every((l) => L(series.json.Items).includes(l)), L(series.json.Items));

    const sorts = {};
    for (const [sortBy, order] of [['UpdatedAt', null], ['UpdatedAt', 'Ascending'], ['CreatedAt', 'Descending'], ['CreatedAt', 'Ascending'],
        ['Name', 'Ascending'], ['Name', 'Descending'], ['ItemName', 'Ascending'], ['ItemName', 'Descending'], ['Position', 'Ascending'], ['Position', 'Descending']]) {
        sorts[`${sortBy} ${order || 'default'}`] = L((await query('admin', { sortBy, sortOrder: order })).Items);
    }
    evidence.sorts = sorts;
    // The videos' display names ignoring case, then the position: computed here from what the API sent.
    const nameOf = (x) => rows.find((r) => guid(r.Id) === guid(x.Id)).ItemName;
    const titleOrder = [[a, 'a'], [b, 'b'], [cc, 'c'], [d, 'd'], [e, 'e'], [f, 'f'], [g, 'g']]
        .map(([x, l]) => ({ l, t: nameOf(x), s: x.StartTicks }))
        .sort((x, y) => x.t.toLowerCase().localeCompare(y.t.toLowerCase()) || (x.s - y.s)).map((x) => x.l);
    const titleDescending = [...new Set(titleOrder.map((l) => rows.find((r) => byId.get(guid(r.Id)) === l).ItemName))].reverse()
        .flatMap((t) => titleOrder.filter((l) => rows.find((r) => byId.get(guid(r.Id)) === l).ItemName === t));
    check('Query sorts, each in both orders: UpdatedAt (newest first by default), CreatedAt, Name (ignoring case, the unnamed after the named either way), ItemName (then the position), Position',
        same(sorts['UpdatedAt default'], ['g', 'f', 'e', 'd', 'c', 'b', 'a']) && same(sorts['UpdatedAt Ascending'], ['a', 'b', 'c', 'd', 'e', 'f', 'g'])
        && same(sorts['CreatedAt Descending'], ['g', 'f', 'e', 'd', 'c', 'b', 'a']) && same(sorts['CreatedAt Ascending'], ['a', 'b', 'c', 'd', 'e', 'f', 'g'])
        && same(sorts['Name Ascending'], ['a', 'c', 'd', 'f', 'g', 'b', 'e']) && same(sorts['Name Descending'], ['g', 'f', 'd', 'c', 'a', 'b', 'e'])
        && same(sorts['ItemName Ascending'], titleOrder) && same(sorts['ItemName Descending'], titleDescending)
        && same(sorts['Position Ascending'], ['f', 'e', 'c', 'd', 'a', 'g', 'b']) && same(sorts['Position Descending'], ['b', 'g', 'a', 'd', 'c', 'e', 'f']),
        { sorts, titleOrder, titleDescending });

    const page1 = await query('admin', { limit: 3 });
    const page3 = await query('admin', { limit: 3, startIndex: 6 });
    const beyond = await query('admin', { startIndex: 7 });
    check('Query paging: 3 of 7, then the last one from 6, nothing from 7, the total always 7',
        page1.Items.length === 3 && page1.TotalRecordCount === 7 && page3.Items.length === 1 && page3.StartIndex === 6 && same(L(page3.Items), ['a'])
        && beyond.Items.length === 0 && beyond.TotalRecordCount === 7, { page1: L(page1.Items), page3: L(page3.Items), beyond: beyond.Items.length });

    const refusals = {};
    for (const [label, p, q] of [['Query sortBy=Count', 'Query', { sortBy: 'Count' }], ['Query limit=201', 'Query', { limit: 201 }],
        ['Query itemTypes=Movie,Show', 'Query', { itemTypes: 'Movie,Show' }], ['Groups sortBy=CreatedAt', 'Groups', { sortBy: 'CreatedAt' }],
        ['Groups sortBy=Name', 'Groups', { sortBy: 'Name' }], ['Groups sortBy=Position', 'Groups', { sortBy: 'Position' }],
        ['Groups startIndex=-1', 'Groups', { startIndex: -1 }], ['Query searchTerm of 101', 'Query', { searchTerm: 'x'.repeat(101) }],
        // A key under the filter's parameter name binds the whole filter under that prefix.
        ['Query filter.sortOrder=Bogus', 'Query', { 'filter.sortOrder': 'Bogus' }],
        ['Groups filter.availability=Bogus', 'Groups', { 'filter.limit': 2, 'filter.availability': 'Bogus' }]]) {
        const r = await call('admin', 'GET', `/Bookmarks/${p}`, { query: q });
        refusals[label] = { status: r.status, detail: r.json && (r.json.detail || (r.json.errors && Object.values(r.json.errors).flat()[0])) };
    }
    const anonymous = await call('anonymous', 'GET', '/Bookmarks/Query');
    evidence.refusals = { ...refusals, anonymous: anonymous.status };
    check('400 for another endpoint\'s sort, a limit over 200, a kind that isn\'t one, a negative start, a search over 100 characters, a bad value under the filter\'s prefix; 401 without a token',
        Object.values(refusals).every((r) => r.status === 400) && refusals['Query limit=201'].detail === 'The limit must be between 1 and 200.'
        && refusals['Query filter.sortOrder=Bogus'].detail === 'The value \'Bogus\' is not one of Ascending, Descending.'
        && anonymous.status === 401, evidence.refusals);

    // ---- 4. Groups
    const groups = await query('admin', {}, 'Groups');
    const heatGroup = groups.Items.find((gr) => guid(gr.ItemId) === guid(longTest.Id));
    check('Groups: five videos, the latest change first; Long Test Movie\'s three bookmarks by position with its count, the latest change and ItemAvailable',
        groups.TotalRecordCount === 5 && groups.Items.length === 5 && guid(groups.Items[0].ItemId) === guid(longTest.Id)
        && !!heatGroup && heatGroup.Count === 3 && same(L(heatGroup.Bookmarks), ['a', 'g', 'b']) && heatGroup.ItemAvailable === true
        && heatGroup.BookmarksTruncated === false && heatGroup.LatestUpdatedAt === rows[0].UpdatedAt,
        { total: groups.TotalRecordCount, groups: groups.Items.map((gr) => ({ ItemName: gr.ItemName, Count: gr.Count, ItemAvailable: gr.ItemAvailable, bookmarks: L(gr.Bookmarks) })) });
    const groupSorts = {};
    for (const [sortBy, order] of [['LatestUpdated', null], ['LatestUpdated', 'Ascending'], ['ItemName', null], ['ItemName', 'Descending'], ['Count', null], ['Count', 'Ascending']]) {
        groupSorts[`${sortBy} ${order || 'default'}`] = (await query('admin', { sortBy, sortOrder: order }, 'Groups')).Items.map((gr) => L(gr.Bookmarks).join(''));
    }
    evidence.groupSorts = groupSorts;
    const groupTitles = [...new Set(titleOrder.map((l) => ({ a: 'agb', g: 'agb', b: 'agb' })[l] || l))];
    check('Groups sorts, each in both orders: LatestUpdated (newest first by default), ItemName, Count (most first by default; a tie to the latest change)',
        same(groupSorts['LatestUpdated default'], ['agb', 'f', 'e', 'd', 'c']) && same(groupSorts['LatestUpdated Ascending'], ['c', 'd', 'e', 'f', 'agb'])
        && same(groupSorts['ItemName default'], groupTitles) && same(groupSorts['ItemName Descending'], [...groupTitles].reverse())
        && same(groupSorts['Count default'], ['agb', 'f', 'e', 'd', 'c']) && same(groupSorts['Count Ascending'], ['f', 'e', 'd', 'c', 'agb']),
        { groupSorts, groupTitles });

    // ---- 5. clip files, and availability on the stored state
    for (const x of [a, g]) {
        const r = await call('admin', 'POST', `/Bookmarks/${x.Id}/Clip`, { body: { Mode: 'Fast' } });
        if (r.status !== 202) throw new Error(`POST /Bookmarks/${x.Id}/Clip: ${r.status} ${r.text.slice(0, 200)}`);
    }
    const clipA = await waitForClip(a.Id);
    const clipG = await waitForClip(g.Id);
    context.clipFiles = [clipA.Clip && clipA.Clip.FileName, clipG.Clip && clipG.Clip.FileName];
    const withClip = L((await query('admin', { hasClip: 'true' })).Items);
    const withoutClip = L((await query('admin', { hasClip: 'false' })).Items);
    check('hasClip: the two bookmarks with a Fast clip file, and the five without',
        clipA.Clip.Status === 'Completed' && clipG.Clip.Status === 'Completed' && context.clipFiles.every(clipOnDisk)
        && same([...withClip].sort(), ['a', 'g']) && same([...withoutClip].sort(), ['b', 'c', 'd', 'e', 'f']),
        { withClip, withoutClip, files: context.clipFiles });

    const moves = loadMoves().length;
    move(path.join(mediaRoot, 'Movies', HELD_MOVIE), path.join(heldRoot, 'Movies', HELD_MOVIE));
    await scan(`${HELD_MOVIE} held out`);
    await waitFor('the held-out movie\'s bookmark detached', async () => ((await call('admin', 'GET', `/Bookmarks/${cc.Id}`)).json.Detached ? true : null));
    const missing = await query('admin', { availability: 'Missing' });
    const available = await query('admin', { availability: 'Available' });
    const missingGroups = await query('admin', { availability: 'Missing' }, 'Groups');
    const everything = await query('admin', { availability: 'All' });
    evidence.availability = {
        missing: missing.Items.map((r) => ({ l: byId.get(guid(r.Id)), Detached: r.Detached, ItemAvailable: r.ItemAvailable, ItemName: r.ItemName })),
        available: L(available.Items), missingGroups: missingGroups.Items.map((gr) => ({ ItemName: gr.ItemName, ItemAvailable: gr.ItemAvailable })),
        all: everything.TotalRecordCount
    };
    check('availability on the stored state: with its movie held out and scanned, Charlie is the one Missing ("Not in library": Detached, not ItemAvailable, its stored name), the other six Available, its group not ItemAvailable, All seven',
        same(L(missing.Items), ['c']) && missing.Items[0].Detached === true && missing.Items[0].ItemAvailable === false && missing.Items[0].ItemName.startsWith('Big Buck Test')
        && same([...L(available.Items)].sort(), ['a', 'b', 'd', 'e', 'f', 'g']) && available.Items.every((r) => r.ItemAvailable === true)
        && missingGroups.TotalRecordCount === 1 && missingGroups.Items[0].ItemAvailable === false && everything.TotalRecordCount === 7, evidence.availability);
    undoMoves(moves);
    await scan(`${HELD_MOVIE} put back`);
    await waitFor('the movie\'s bookmark reattached', async () => (!(await call('admin', 'GET', `/Bookmarks/${cc.Id}`)).json.Detached ? true : null));
    const back = await query('admin', { availability: 'Missing' });
    const charlie = (await query('admin', { searchTerm: `${PROBE} Charlie` })).Items[0];
    check('put back and scanned: nothing Missing, and Charlie ItemAvailable again (the same id)', back.TotalRecordCount === 0 && charlie && charlie.ItemAvailable === true
        && guid(charlie.ItemId) === guid(bigBuck.Id), { missing: back.TotalRecordCount, charlie: charlie && { ItemAvailable: charlie.ItemAvailable, Detached: charlie.Detached } });

    // ---- 6. ItemAvailable per viewer (library access read at each request)
    const before = (await query('viewer2')).Items;
    const folders = (await call('admin', 'GET', '/Library/VirtualFolders')).json;
    const viewer2 = await call('admin', 'GET', `/Users/${users.viewer2.id}`);
    context.viewer2Policy = viewer2.json.Policy;
    const enabled = folders.filter((l) => l.Name !== 'Movies 2').map((l) => l.ItemId);
    const restricted = await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: { ...context.viewer2Policy, EnableAllFolders: false, EnabledFolders: enabled } });
    if (restricted.status !== 204) throw new Error(`POST /Users/viewer2/Policy: ${restricted.status}`);
    const lost = (await query('viewer2')).Items;
    const lostGroups = (await query('viewer2', {}, 'Groups')).Items;
    const core = await call('viewer2', 'GET', `/Items/${blade2049.Id}`, { query: { userId: users.viewer2.id } });
    const restored = await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: context.viewer2Policy });
    if (restored.status !== 204) throw new Error(`POST /Users/viewer2/Policy (put back): ${restored.status}`);
    context.viewer2Policy = null;
    const regained = (await query('viewer2')).Items;
    const isolated = await call('viewer2', 'GET', '/Bookmarks/Query', { query: { itemId: longTest.Id, searchTerm: PROBE } });
    evidence.perViewer = {
        before: before.map((r) => ({ l: byId.get(guid(r.Id)), ItemAvailable: r.ItemAvailable })),
        lost: lost.map((r) => ({ l: byId.get(guid(r.Id)), ItemAvailable: r.ItemAvailable, Detached: r.Detached, ItemName: r.ItemName, Name: r.Name })),
        lostGroups: lostGroups.map((gr) => ({ ItemAvailable: gr.ItemAvailable, Count: gr.Count })), coreItem: core.status,
        regained: regained.map((r) => ({ l: byId.get(guid(r.Id)), ItemAvailable: r.ItemAvailable })),
        viewer2OnAdminsVideo: isolated.json && isolated.json.TotalRecordCount
    };
    check('ItemAvailable per viewer: viewer2\'s bookmark on a Movies 2 video is available; at the next request after viewer2 lost Movies 2 it is "Unavailable" (listed with its stored name, not detached; Jellyfin\'s own /Items says 404 too), its group too; available again at once when access is back; viewer2 sees none of admin\'s or viewer\'s bookmarks',
        same(L(before), ['w1']) && before[0].ItemAvailable === true
        && same(L(lost), ['w1']) && lost[0].ItemAvailable === false && lost[0].Detached === false && lost[0].Name === `${PROBE} Whiskey` && lost[0].ItemName.startsWith('Blade Runner')
        && lostGroups.length === 1 && lostGroups[0].ItemAvailable === false && core.status === 404
        && same(L(regained), ['w1']) && regained[0].ItemAvailable === true && isolated.status === 200 && isolated.json.TotalRecordCount === 0, evidence.perViewer);

    // ---- 7. BulkDelete
    const unknown = crypto.randomUUID();
    const keep = await call('admin', 'POST', '/Bookmarks/BulkDelete', { body: { Ids: [g.Id, v1.Id, unknown], DeleteClips: false } });
    const gGone = await call('admin', 'GET', `/Bookmarks/${g.Id}`);
    const gFileKept = clipOnDisk(clipG.Clip.FileName);
    const viewerStill = await call('viewer', 'GET', `/Bookmarks/${v1.Id}`);
    const afterKeep = await query('admin');
    const deleteKept = await call('admin', 'DELETE', `/Bookmarks/${g.Id}/Clip`);
    evidence.bulk = { keep: keep.json, getAfter: gGone.status, fileKept: gFileKept, viewerStill: viewerStill.status, listed: afterKeep.TotalRecordCount, deleteKeptClip: deleteKept.status, keptFileGone: !clipOnDisk(clipG.Clip.FileName) };
    check('BulkDelete without the clips: Golf becomes a tombstone that keeps its clip file (404 by id, no longer listed), viewer\'s id and an unknown one are NotFound and viewer\'s bookmark stays; the kept clip then deletes (204)',
        keep.status === 200 && same(keep.json.Tombstoned.map(guid), [guid(g.Id)]) && keep.json.Deleted.length === 0
        && same(keep.json.NotFound.map(guid), [guid(v1.Id), guid(unknown)]) && keep.json.InUse.length === 0 && keep.json.Failed.length === 0
        && gGone.status === 404 && gFileKept && viewerStill.status === 200 && afterKeep.TotalRecordCount === 6
        && deleteKept.status === 204 && evidence.bulk.keptFileGone, evidence.bulk);
    const withClips = await call('admin', 'POST', '/Bookmarks/BulkDelete', { body: { Ids: [a.Id, b.Id], DeleteClips: true } });
    const empty = await call('admin', 'POST', '/Bookmarks/BulkDelete', { body: { Ids: [] } });
    evidence.bulk.withClips = withClips.json;
    evidence.bulk.aFileGone = !clipOnDisk(clipA.Clip.FileName);
    evidence.bulk.empty = { status: empty.status, detail: empty.json && empty.json.detail };
    check('BulkDelete with the clips: Alpha and Bravo deleted, Alpha\'s clip file gone from the clip folder; an empty list is 400',
        withClips.status === 200 && same(withClips.json.Deleted.map(guid), [guid(a.Id), guid(b.Id)]) && withClips.json.NotFound.length === 0
        && evidence.bulk.aFileGone && (await query('admin')).TotalRecordCount === 4
        && empty.status === 400 && evidence.bulk.empty.detail === 'Choose at least one bookmark to delete.', evidence.bulk);

    // ---- 8. Preferences
    const original = await call('viewer', 'GET', '/Bookmarks/Preferences');
    context.viewerPreferences = original.json;
    const adminBefore = (await call('admin', 'GET', '/Bookmarks/Preferences')).json;
    const set = await call('viewer', 'POST', '/Bookmarks/Preferences', { body: { AskForName: true, PauseWhileNaming: true } });
    const read = (await call('viewer', 'GET', '/Bookmarks/Preferences')).json;
    const capsAfter = (await call('viewer', 'GET', '/Bookmarks/Capabilities')).json;
    const preferencesOf = (id) => Object.entries(readJson(storeFile).UserPreferences || {}).find(([key]) => guid(key) === guid(id));
    const stored = (preferencesOf(users.viewer.id) || [null, null])[1];
    const reset = await call('viewer', 'POST', '/Bookmarks/Preferences', { body: {} });
    const readReset = (await call('viewer', 'GET', '/Bookmarks/Preferences')).json;
    const storedReset = Object.keys(readJson(storeFile).UserPreferences || {}).map(guid);
    const adminAfter = (await call('admin', 'GET', '/Bookmarks/Preferences')).json;
    evidence.preferences = { original: original.json, set: set.status, read, capabilities: { AskForName: capsAfter.AskForName, AskForNameIsDefault: capsAfter.AskForNameIsDefault, PauseWhileNaming: capsAfter.PauseWhileNaming }, stored, reset: reset.status, readReset, storedUsers: storedReset.length, adminBefore, adminAfter };
    check('Preferences (viewer): POST is 204 and GET reads it back, the capabilities follow (AskForName chosen, not the default; the pause on), bookmarks.json holds them under viewer\'s id; the defaults again remove the entry; admin\'s are untouched',
        set.status === 204 && same(read, { AskForName: true, PauseWhileNaming: true })
        && capsAfter.AskForName === true && capsAfter.AskForNameIsDefault === false && capsAfter.PauseWhileNaming === true
        && !!stored && stored.AskForName === true && stored.PauseWhileNaming === true
        && reset.status === 204 && same(readReset, { PauseWhileNaming: false }) && !storedReset.includes(guid(users.viewer.id)) && same(adminBefore, adminAfter), evidence.preferences);

    // ---- 9. timing
    const timing = { query: [], groups: [] };
    for (let i = 0; i < 5; i++) {
        timing.query.push((await call('admin', 'GET', '/Bookmarks/Query')).ms);
        timing.groups.push((await call('admin', 'GET', '/Bookmarks/Groups')).ms);
    }
    const median = (list) => [...list].sort((x, y) => x - y)[Math.floor(list.length / 2)];
    const everyBookmark = await call('admin', 'GET', '/Bookmarks/Query', { query: { limit: 1 } });
    evidence.timing = { adminBookmarks: everyBookmark.json.TotalRecordCount, queryMs: timing.query, groupsMs: timing.groups, queryMedianMs: median(timing.query), groupsMedianMs: median(timing.groups) };
    check('Query and Groups answer admin\'s whole list (first page of 50 or 25) within 500 ms (median of five, on loopback)',
        evidence.timing.queryMedianMs < 500 && evidence.timing.groupsMedianMs < 500, evidence.timing);
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { viewer2Policy: null, viewerPreferences: null, clipFiles: [] };
    const startLog = logPosition();
    if (loadMoves().length > 0) {
        undoMoves(0);
        await scan('left-over changes of an earlier run undone');
        evidence.undidLeftovers = true;
    }
    let failure = null;
    try {
        await run(context);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        try {
            if (context.viewer2Policy) await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: context.viewer2Policy });
            if (context.viewerPreferences) await call('viewer', 'POST', '/Bookmarks/Preferences', { body: context.viewerPreferences });
            const changed = loadMoves().length;
            undoMoves(0);
            if (changed > 0) await scan('clean-up: media put back');
            for (const { who, id } of made) {
                await call(who, 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } });
                await call(who, 'DELETE', `/Bookmarks/${id}/Clip`);
            }
        } catch (error) {
            check('clean-up: media, viewer2\'s libraries, viewer\'s preferences and the bookmarks put back', false, String(error));
        }
        const text = logSince(startLog);
        const lines = text.split('\n');
        const errors = lines.filter((l) => /\[ERR\]|\[FTL\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        const naming = lines.filter((l) => /\[(INF|WRN|ERR|FTL)\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l) && l.includes(PROBE));
        evidence.otherLinesNamingTheProbe = lines.filter((l) => /\[(INF|WRN|ERR|FTL)\]/.test(l) && l.includes(PROBE)).length;
        check('no Error of Bookmarks or the kit in the server log during the run, and no line of theirs at Information or above naming a bookmark',
            errors.length === 0 && naming.length === 0, { errors: errors.slice(0, 5), naming: naming.slice(0, 5) });
        const left = [];
        for (const { who, id } of made) if ((await call(who, 'GET', `/Bookmarks/${id}`)).status !== 404) left.push(id);
        const filesLeft = (context.clipFiles || []).filter(clipOnDisk);
        check('clean-up: no media change left, viewer2\'s libraries as they were, the probe\'s bookmarks and clip files deleted',
            loadMoves().length === 0 && !context.viewer2Policy && left.length === 0 && filesLeft.length === 0, { moves: loadMoves().length, left, filesLeft });
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
