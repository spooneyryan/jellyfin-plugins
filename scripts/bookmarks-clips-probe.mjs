#!/usr/bin/env node
// Bookmarks' My Clips endpoints in a running sandbox, before the page: GET
// /Bookmarks/Clips, /Summary and /Status, POST /Bookmarks/Clips/BulkDelete and DELETE /Bookmarks/{id}/Clip?onlyIfMissing
// against real Fast clips of a seeded user (admin): a clip that is there, the clip of a bookmark deleted with its clip
// kept (a tombstone), a clip whose file is gone, a clip made again after its file had gone (Ready at once, and the page's
// Forget never deletes it), and two clips in a second clip folder that is taken away (an unplugged drive:
// FolderUnavailable, never Missing, also for a clip first asked about after the drive went); another user's clip
// (viewer2's) never listed, never answered and never deleted; the filters, the 400s, and the OpenAPI document's
// description of it all. Node 22 built-ins only. Loopback only, never port 8096. The users' tokens come from
// credentials.json and are never printed.
//
//   node scripts/bookmarks-clips-probe.mjs --sandbox default [--timeout <seconds>] [--out result.json]
//
// It changes only what it puts back: Bookmarks' ClipOutputPath, set for two clips to a folder in its work folder
// .sandbox/<name>/bookmarks-clips-probe/usb-clips (inside the sandbox, so the sandbox still starts; that folder stays
// recorded in ClipRoots), saved in saved-settings.json there before the change and put back in a finally (and at the
// start of the next run after a run was killed); and three renames, recorded in moves.json before they are made and undone
// in a finally and at the next start: that clip folder renamed aside (unplugged), and two clip files moved into the hold
// folder held/ for a while (missing). One of those two is made again by Bookmarks under its own name, so its held copy
// can't go back: that copy, in held/ only, is the one file the probe deletes itself (also at the next start after a killed
// run). Every other file and every bookmark and clip it made goes through the API; it never writes bookmarks.json, and
// never restarts or deploys anything. Every name it gives starts with "clips-probe".
//
// Checks:
//   1. /api-docs/openapi.json: the clip routes (the renames' Name and RenameFile too) with their parameters (state an array of BookmarkClipState, ids an
//      array of GUIDs, sortBy a BookmarkClipSortBy), bodies and answers; the new schemas, each with the Bookmark prefix,
//      the enums with exactly their values, BookmarkClipDto with the list's facts, the bulk delete's InProgress, and the
//      clip delete's onlyIfMissing;
//   2. a clip folder unplugged between two requests: Golf judged Ready with the folder there; the folder
//      unplugged; Charlie, in the same folder and never asked about, reads FolderUnavailable at once, and Golf goes from
//      Ready to FolderUnavailable within the 30 s, never Missing;
//   3. the remake of a missing clip: Foxtrot's file held out reads Missing; made again, it takes the first
//      numbered name (its own record still has the first one: a taken name), and the first poll that says
//      Completed says Ready, as do the list and the numbers; the page's Forget
//      (onlyIfMissing) answers 409 and leaves the file; the held copy of its first clip discarded;
//   4. GET /Bookmarks/Clips (admin): its six clips, Ready, the tombstone's (BookmarkDeleted, still Ready), Missing (the
//      file held out), the remade one Ready, and two FolderUnavailable (their folder renamed aside), the bookmark without a
//      clip and viewer2's clip not among them; the file's name and no path in the JSON; the state, deleted and search
//      filters; viewer2 sees only its own; viewer (no download permission) lists too;
//   5. Summary and Status: the numbers match the list; Status answers admin's clips in the order asked and leaves out
//      viewer2's id, the bookmark without a clip and an unknown id;
//   6. BulkDelete (admin) of the offline clip, viewer2's and the bookmark without a clip: FolderUnavailable and NotFound,
//      nothing of them changed (viewer2's file still there and listed for viewer2);
//   7. the file put back and the folder plugged in: all Ready again once the list's 30 s verdicts are up; then BulkDelete
//      of admin's six: five FilesDeleted (their bookmarks stay without a clip), the tombstone RecordsRemoved, every file
//      gone from disk;
//   8. 400 for a state, mode or sort that is none of them, a limit over 200, Status without ids or with one that isn't a
//      GUID, an onlyIfMissing that isn't a boolean; 401 without a token;
//   9. no Error of Bookmarks or the kit in the log, and no line of theirs at Information or above naming a clip-probe
//      bookmark; the clean-up: the setting, the folder and the files as they were, the probe's bookmarks and clips gone.
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const TICKS = 10_000_000;
const PROBE = 'clips-probe';
const UNPLUGGED = '.clips-probe-unplugged';

function usage() {
    return 'usage: node scripts/bookmarks-clips-probe.mjs --sandbox <name> [--timeout <seconds>] [--out <file>]';
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
const workRoot = path.join(sandboxRoot, 'bookmarks-clips-probe');
const usbRoot = path.join(workRoot, 'usb-clips');
const heldRoot = path.join(workRoot, 'held');
const movesFile = path.join(workRoot, 'moves.json');
const savedSettingsFile = path.join(workRoot, 'saved-settings.json');
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
const pluginGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8')) || [])[1];
if (!pluginGuid) throw new Error('no guid in plugins/Bookmarks/build.yaml');
const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error(`refusing to work in ${p}`);
    return full;
}
for (const p of [storeFile, clipRoot, workRoot, usbRoot, heldRoot]) assertInside(p, sandboxRoot);

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() } };
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
        h.Authorization = `MediaBrowser Client="bookmarks-clips-probe", Device="sandbox", DeviceId="bookmarks-clips-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = typeof body === 'string' ? body : JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(180000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text, type: res.headers.get('content-type') || '' };
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

// A rename inside the sandbox's clip folders and the probe's own folders only, recorded before it is made. A clip file held
// out for Bookmarks to make again is recorded as remade: once that is done (`remadeAs` notes the new file), its copy can't
// go back.
function move(from, to, { remade = false } = {}) {
    const src = path.resolve(from);
    const dst = path.resolve(to);
    for (const p of [src, dst]) {
        if (!(p.startsWith(clipRoot + path.sep) || p.startsWith(workRoot + path.sep))) throw new Error(`refusing to move ${p}: not in the sandbox's clip folders or the probe's folder`);
    }
    if (!fs.existsSync(src)) throw new Error(`${src} doesn't exist`);
    if (fs.existsSync(dst)) throw new Error(`${dst} exists already`);
    const moves = loadMoves();
    moves.push({ from: src, to: dst, remade });
    saveMoves(moves);
    fs.mkdirSync(path.dirname(dst), { recursive: true });
    fs.renameSync(src, dst);
}

// The probe's own copy, in its hold folder, of a clip file Bookmarks has made again (a remade move whose clip file is there
// again, under its first name or a numbered one, `remadeAs`): the one file the probe deletes itself, and only that.
function discardHeldCopy({ from, to, remade, remadeAs }) {
    const held = assertInside(to, heldRoot);
    if (remade !== true || path.dirname(held) !== heldRoot || !fs.statSync(held).isFile()) throw new Error(`refusing to delete ${held}: not a held copy of a clip made again`);
    if (![from, remadeAs].some((p) => p && fs.existsSync(path.resolve(p)))) throw new Error(`refusing to delete ${held}: ${remadeAs || from}, made again, isn't there`);
    fs.rmSync(held);
}

function undoMoves(untilCount = 0) {
    let moves = loadMoves();
    while (moves.length > untilCount) {
        const last = moves[moves.length - 1];
        const { from, to } = last;
        // A remade move whose clip is there again (under its first name or the numbered one) keeps that clip: the held
        // copy goes. Otherwise a held file goes back where it was, and never over another.
        if (fs.existsSync(to) && last.remade === true && [from, last.remadeAs].some((p) => p && fs.existsSync(path.resolve(p)))) {
            discardHeldCopy(last);
        } else if (fs.existsSync(to) && !fs.existsSync(from)) {
            fs.mkdirSync(path.dirname(from), { recursive: true });
            fs.renameSync(to, from);
        } else if (fs.existsSync(to)) {
            throw new Error(`cannot undo ${to} → ${from}: both exist; fix by hand`);
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

// The setting is saved to a file before it changes, so a killed run's next start puts it back.
async function restoreSavedSettings() {
    if (!fs.existsSync(savedSettingsFile)) return false;
    await setConfiguration(readJson(savedSettingsFile));
    fs.rmSync(savedSettingsFile);
    return true;
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

// ------------------------------------------------------------------------------------------ bookmarks and clips

const made = [];

async function items(types) {
    const r = await call('admin', 'GET', '/Items', {
        query: { userId: users.admin.id, Recursive: 'true', IncludeItemTypes: types, Fields: 'Path', EnableImages: 'false', EnableUserData: 'false' }
    });
    if (r.status !== 200) throw new Error(`GET /Items: ${r.status}`);
    return r.json.Items;
}

async function createBookmark(who, item, { start, end = null, name }) {
    const body = { ItemId: item.Id, StartTicks: start * TICKS, EndTicks: end === null ? null : end * TICKS, Name: name };
    const r = await call(who, 'POST', '/Bookmarks', { body });
    if (r.status !== 201 || !r.json) throw new Error(`POST /Bookmarks on ${item.Name}: ${r.status} ${r.text.slice(0, 200)}`);
    made.push({ who, id: r.json.Id });
    return r.json;
}

async function makeClip(who, bookmark) {
    const r = await call(who, 'POST', `/Bookmarks/${bookmark.Id}/Clip`, { body: { Mode: 'Fast' } });
    if (r.status !== 202) throw new Error(`POST /Bookmarks/${bookmark.Id}/Clip: ${r.status} ${r.text.slice(0, 200)}`);
    return waitFor(`the clip of ${bookmark.Id}`, async () => {
        const read = await call(who, 'GET', `/Bookmarks/${bookmark.Id}`);
        const status = read.json && read.json.Clip && read.json.Clip.Status;
        if (status === 'Completed') return read.json;
        if (status && !['Queued', 'Running'].includes(status)) throw new Error(`the clip of ${bookmark.Id} ended ${status}: ${read.json.Clip.Error}`);
        return null;
    }, opts.timeout);
}

// A record's clip file as bookmarks.json has it (read only).
function storedClipPath(id) {
    const record = (readJson(storeFile).Bookmarks || []).find((b) => guid(b.Id) === guid(id));
    return record && record.Clip && record.Clip.FilePath;
}

async function clips(who, query = {}) {
    const r = await call(who, 'GET', '/Bookmarks/Clips', { query: { searchTerm: PROBE, limit: 200, ...query } });
    if (r.status !== 200 || !r.json) throw new Error(`GET /Bookmarks/Clips ${JSON.stringify(query)}: ${r.status} ${r.text.slice(0, 300)}`);
    return r.json;
}

// One clip's Status and State, as the page's poll of the clips being made reads them.
async function statusOf(who, bookmark) {
    const r = await call(who, 'GET', '/Bookmarks/Clips/Status', { query: { ids: guid(bookmark.Id) } });
    if (r.status !== 200 || !Array.isArray(r.json) || r.json.length !== 1) throw new Error(`GET /Bookmarks/Clips/Status of ${bookmark.Id}: ${r.status} ${r.text.slice(0, 200)}`);
    return { Status: r.json[0].Status, State: r.json[0].State, at: new Date().toISOString() };
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
    // ---- 1. the OpenAPI document
    const docResponse = await openApiDocument();
    const doc = docResponse.json;
    const schemas = (doc.components && doc.components.schemas) || {};
    const op = (p, m) => (doc.paths[p] || {})[m] || null;
    const params = (o) => Object.fromEntries(((o && o.parameters) || []).map((p) => [p.name.toLowerCase(), p]));
    const content = (c) => (c ? (c['application/json'] || Object.values(c)[0] || {}).schema : null);
    const okSchema = (o) => content(o && o.responses && o.responses['200'] && o.responses['200'].content);
    const bodySchema = (o) => content(o && o.requestBody && o.requestBody.content);
    const list = params(op('/Bookmarks/Clips', 'get'));
    const status = params(op('/Bookmarks/Clips/Status', 'get'));
    const statusOk = okSchema(op('/Bookmarks/Clips/Status', 'get'));
    const foreign = new Set(fs.readFileSync(path.join(repoRoot, 'kit', 'testdata', 'openapi-schema-ids.txt'), 'utf8').split(/\r?\n/).map((l) => l.trim()).filter((l) => l && !l.startsWith('#')));
    const newSchemas = ['BookmarkClipItemDto', 'BookmarkClipItemDtoQueryResult', 'BookmarkClipState', 'BookmarkClipSortBy', 'BookmarkClipSummaryDto',
        'BookmarkClipStatusDto', 'BookmarkClipBulkDeleteRequest', 'BookmarkClipBulkDeleteResultDto'];
    const openApi = {
        status: docResponse.status,
        paths: Object.keys(doc.paths).filter((p) => /^\/Bookmarks\/Clips(\/|$)/i.test(p)).sort(),
        listParameters: Object.keys(list),
        state: list.state && { in: list.state.in, type: list.state.schema && list.state.schema.type, items: refName(list.state.schema && list.state.schema.items) },
        mode: list.mode && refName(list.mode.schema),
        sortBy: list.sortby && refName(list.sortby.schema),
        ids: status.ids && { in: status.ids.in, type: status.ids.schema && status.ids.schema.type, format: status.ids.schema && status.ids.schema.items && status.ids.schema.items.format },
        answers: {
            list: refName(okSchema(op('/Bookmarks/Clips', 'get'))),
            summary: refName(okSchema(op('/Bookmarks/Clips/Summary', 'get'))),
            status: statusOk && statusOk.type === 'array' ? `array of ${refName(statusOk.items)}` : refName(statusOk),
            bulk: refName(okSchema(op('/Bookmarks/Clips/BulkDelete', 'post')))
        },
        bulkBody: refName(bodySchema(op('/Bookmarks/Clips/BulkDelete', 'post'))),
        bulkAnswers: op('/Bookmarks/Clips/BulkDelete', 'post') && Object.keys(op('/Bookmarks/Clips/BulkDelete', 'post').responses).sort(),
        enums: { BookmarkClipState: schemas.BookmarkClipState && schemas.BookmarkClipState.enum, BookmarkClipSortBy: schemas.BookmarkClipSortBy && schemas.BookmarkClipSortBy.enum },
        clipDtoFacts: Object.keys((schemas.BookmarkClipDto && schemas.BookmarkClipDto.properties) || {})
            .filter((k) => ['State', 'Container', 'DurationTicks', 'FileExists', 'IsNameOutdated', 'LastJobFailed', 'InLibrary', 'LibraryName', 'RootAvailable', 'MadeAt'].includes(k)),
        itemFields: Object.keys((schemas.BookmarkClipItemDto && schemas.BookmarkClipItemDto.properties) || {}),
        bulkResult: Object.keys((schemas.BookmarkClipBulkDeleteResultDto && schemas.BookmarkClipBulkDeleteResultDto.properties) || {}),
        onlyIfMissing: (() => {
            const p = params(op('/Bookmarks/{bookmarkId}/Clip', 'delete')).onlyifmissing;
            return p ? { in: p.in, type: p.schema && p.schema.type, required: !!p.required } : null;
        })(),
        newSchemas: newSchemas.filter((s) => schemas[s]),
        foreign: newSchemas.filter((s) => foreign.has(s)),
        bookmarkSchemas: Object.keys(schemas).filter((s) => s.startsWith('Bookmark')).length
    };
    evidence.openApi = openApi;
    check('1. OpenAPI (200): the clip routes, the renames\' Name and RenameFile too; GET /Bookmarks/Clips with every filter as a query parameter (state an array of BookmarkClipState, mode a BookmarkClipMode, sortBy a BookmarkClipSortBy) answering BookmarkClipItemDtoQueryResult; Summary, Status (ids an array of GUIDs, an array of BookmarkClipStatusDto) and BulkDelete (BookmarkClipBulkDeleteRequest → BookmarkClipBulkDeleteResultDto; 200, 400, 413, 503)',
        openApi.status === 200 && same(openApi.paths, ['/Bookmarks/Clips', '/Bookmarks/Clips/BulkDelete', '/Bookmarks/Clips/Status', '/Bookmarks/Clips/Summary',
            '/Bookmarks/Clips/{bookmarkId}/Name', '/Bookmarks/Clips/{bookmarkId}/RenameFile'])
        && ['searchterm', 'state', 'mode', 'itemtypes', 'includedeleted', 'itemid', 'startindex', 'limit', 'sortby'].every((p) => list[p] && list[p].in === 'query')
        && openApi.state.type === 'array' && openApi.state.items === 'BookmarkClipState' && openApi.mode === 'BookmarkClipMode' && openApi.sortBy === 'BookmarkClipSortBy'
        && openApi.ids && openApi.ids.in === 'query' && openApi.ids.type === 'array' && openApi.ids.format === 'uuid'
        && openApi.answers.list === 'BookmarkClipItemDtoQueryResult' && openApi.answers.summary === 'BookmarkClipSummaryDto'
        && openApi.answers.status === 'array of BookmarkClipStatusDto' && openApi.answers.bulk === 'BookmarkClipBulkDeleteResultDto'
        && openApi.bulkBody === 'BookmarkClipBulkDeleteRequest' && ['200', '400', '413', '503'].every((code) => (openApi.bulkAnswers || []).includes(code)), openApi);
    check('1. OpenAPI: the eight new schemas, each prefixed and none in the foreign list; the enums with exactly their values (RenameUnfinished too); BookmarkClipDto with the list\'s ten facts',
        openApi.newSchemas.length === newSchemas.length && openApi.foreign.length === 0
        && same(openApi.enums.BookmarkClipState, ['Ready', 'InProgress', 'Failed', 'Missing', 'FolderUnavailable', 'RenameUnfinished'])
        && same(openApi.enums.BookmarkClipSortBy, ['Newest', 'Oldest', 'Name', 'ItemName', 'Size', 'Duration'])
        && openApi.clipDtoFacts.length === 10 && ['BookmarkDeleted', 'ItemAvailable', 'Clip', 'BookmarkStartTicks'].every((f) => openApi.itemFields.includes(f)),
        { newSchemas: openApi.newSchemas, foreign: openApi.foreign, enums: openApi.enums, clipDtoFacts: openApi.clipDtoFacts });
    check('1. OpenAPI: BookmarkClipBulkDeleteResultDto with InProgress among its seven lists; DELETE /Bookmarks/{bookmarkId}/Clip takes onlyIfMissing, an optional boolean in the query',
        same(openApi.bulkResult, ['FilesDeleted', 'RecordsRemoved', 'NotFound', 'InProgress', 'InUse', 'FolderUnavailable', 'Failed'])
        && openApi.onlyIfMissing && openApi.onlyIfMissing.in === 'query' && openApi.onlyIfMissing.type === 'boolean' && !openApi.onlyIfMissing.required,
        { bulkResult: openApi.bulkResult, onlyIfMissing: openApi.onlyIfMissing });

    // ---- the clips: admin's six (and a bookmark without one), viewer2's one
    const movies = await items('Movie');
    const longTest = movies.filter((m) => typeof m.Path === 'string' && m.Path.split(/[\\/]/).includes('Long Test Movie (2021)'));
    if (longTest.length !== 1) throw new Error(`expected one Long Test Movie, found ${longTest.length}; run Initialize-Sandbox.ps1 on fresh media`);
    const movie = longTest[0];
    const a = await createBookmark('admin', movie, { start: 10, end: 14, name: `${PROBE} Alpha` });
    const b = await createBookmark('admin', movie, { start: 20, end: 24, name: `${PROBE} Bravo` });
    const c = await createBookmark('admin', movie, { start: 30, end: 34, name: `${PROBE} Charlie` });
    const d = await createBookmark('admin', movie, { start: 40, end: 44, name: `${PROBE} Delta` });
    const e = await createBookmark('admin', movie, { start: 50, name: `${PROBE} Echo` });
    const f = await createBookmark('admin', movie, { start: 60, end: 64, name: `${PROBE} Foxtrot` });
    const g = await createBookmark('admin', movie, { start: 70, end: 74, name: `${PROBE} Golf` });
    const w = await createBookmark('viewer2', movie, { start: 12, end: 16, name: `${PROBE} Whiskey` });
    const byId = new Map([[a, 'a'], [b, 'b'], [c, 'c'], [d, 'd'], [e, 'e'], [f, 'f'], [g, 'g'], [w, 'w']].map(([x, l]) => [guid(x.Id), l]));
    const L = (rows) => rows.map((r) => byId.get(guid(r.Id)) || '?');
    evidence.bookmarks = {
        a: 'admin: a clip that is there', b: 'admin: deleted with its clip kept (tombstone)', c: 'admin: a clip in a second clip folder, unplugged, first asked about after that',
        d: 'admin: its clip file held out (missing)', e: 'admin: no clip', f: 'admin: its clip file held out, then made again under its name',
        g: 'admin: a clip in the second clip folder, asked about before it was unplugged', w: 'viewer2: a clip'
    };

    for (const x of [a, b, d, f]) await makeClip('admin', x);
    await makeClip('viewer2', w);

    // Charlie's and Golf's clips in a second clip folder inside the sandbox (an admin's other drive): the setting saved first.
    const config = await getConfiguration();
    context.savedSettings = { ClipOutputPath: config.ClipOutputPath ?? '' };
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(savedSettingsFile, JSON.stringify(context.savedSettings));
    fs.mkdirSync(usbRoot, { recursive: true });
    await setConfiguration({ ClipOutputPath: usbRoot });
    await makeClip('admin', c);
    await makeClip('admin', g);
    await restoreSavedSettings();
    context.savedSettings = null;

    const files = { a: storedClipPath(a.Id), b: storedClipPath(b.Id), c: storedClipPath(c.Id), d: storedClipPath(d.Id), f: storedClipPath(f.Id), g: storedClipPath(g.Id), w: storedClipPath(w.Id) };
    context.files = files;
    const inUsb = ['c', 'g'].every((k) => files[k] && path.resolve(files[k]).toLowerCase().startsWith(usbRoot.toLowerCase() + path.sep) && fs.existsSync(files[k]));
    const inClips = ['a', 'b', 'd', 'f', 'w'].every((k) => files[k] && path.resolve(files[k]).toLowerCase().startsWith(clipRoot.toLowerCase() + path.sep) && fs.existsSync(files[k]));
    check('set-up: six Fast clips in the sandbox\'s clip folder (admin\'s five, viewer2\'s one) and Charlie\'s and Golf\'s in the second clip folder, recorded in ClipRoots; the setting put back',
        inUsb && inClips && (readJson(storeFile).ClipRoots || []).some((r) => path.resolve(r).toLowerCase() === usbRoot.toLowerCase())
        && ((await getConfiguration()).ClipOutputPath ?? '') === (config.ClipOutputPath ?? ''),
        { inUsb, inClips, clipRootsHasUsb: (readJson(storeFile).ClipRoots || []).length });

    // Bravo deleted with its clip kept; Delta's file held out. No clip has been judged yet: each verdict below is a first one,
    // except where a step says otherwise.
    const tombstoned = await call('admin', 'DELETE', `/Bookmarks/${b.Id}`, { query: { deleteClip: 'false' } });
    if (tombstoned.status !== 204) throw new Error(`DELETE /Bookmarks/${b.Id}?deleteClip=false: ${tombstoned.status}`);
    const heldFile = path.join(heldRoot, path.basename(files.d));
    move(files.d, heldFile);

    // ---- 2. a clip folder unplugged between two requests: Golf judged with the folder there, then Charlie,
    // in the same folder, first asked about once it is unplugged
    const unplug = { golfBefore: await statusOf('admin', g) };
    move(usbRoot, usbRoot + UNPLUGGED);
    unplug.charlieFirst = await statusOf('admin', c);
    unplug.golfPolls = [];
    await waitFor('Golf FolderUnavailable once its verdict of 30 s is up', async () => {
        const now = await statusOf('admin', g);
        unplug.golfPolls.push(now.State);
        return now.State === 'FolderUnavailable' || unplug.golfPolls.length > 200 ? now : null;
    }, 45);
    evidence.unplug = { golfBefore: unplug.golfBefore, charlieFirst: unplug.charlieFirst, golfStates: [...new Set(unplug.golfPolls)], golfPolls: unplug.golfPolls.length };
    check('2. a clip folder unplugged between two requests: Golf Ready with the folder there; unplugged, Charlie, in the same folder and never asked about, reads FolderUnavailable at once (not Missing)',
        unplug.golfBefore.State === 'Ready' && unplug.charlieFirst.Status === 'Completed' && unplug.charlieFirst.State === 'FolderUnavailable', evidence.unplug);
    check('2. Golf, judged before the unplug, stays Ready until its 30 s are up, then reads FolderUnavailable: never Missing',
        unplug.golfPolls.every((s) => s === 'Ready' || s === 'FolderUnavailable') && unplug.golfPolls[unplug.golfPolls.length - 1] === 'FolderUnavailable', evidence.unplug);

    // ---- 3. the remake of a missing clip: Foxtrot's file held out, then the clip made again
    const heldFoxtrot = path.join(heldRoot, path.basename(files.f));
    const firstJob = readJson(storeFile).Bookmarks.find((x) => guid(x.Id) === guid(f.Id)).Clip.RequestedAt;
    const movesBeforeFoxtrot = loadMoves().length;
    move(files.f, heldFoxtrot, { remade: true });
    const remake = { missing: await statusOf('admin', f), polls: [] };
    const remade = await call('admin', 'POST', `/Bookmarks/${f.Id}/Clip`, { body: { Mode: 'Fast' } });
    if (remade.status !== 202) throw new Error(`POST /Bookmarks/${f.Id}/Clip (the remake): ${remade.status} ${remade.text.slice(0, 200)}`);

    // As the page polls the clips being made: every 2.5 s until the job is over; its first answer after that is the one.
    const remakeUntil = Date.now() + opts.timeout * 1000;
    for (;;) {
        await delay(2500);
        const polled = await statusOf('admin', f);
        remake.polls.push(polled);
        if (!['Queued', 'Running'].includes(polled.Status)) break;
        if (Date.now() > remakeUntil) throw new Error('timed out waiting for the remake of Foxtrot');
    }

    const record = readJson(storeFile).Bookmarks.find((x) => guid(x.Id) === guid(f.Id));
    const remadePath = record && record.Clip ? record.Clip.FilePath : null;
    // A name another record has is taken, its file there or not; since Bookmarks 1.2.1 a remake
    // takes back the name its own record has (its file gone here, so nothing to replace), so no numbered name is made.
    const firstFoxtrot = files.f;
    const numbered = path.join(path.dirname(firstFoxtrot), `${path.basename(firstFoxtrot, path.extname(firstFoxtrot))} (2)${path.extname(firstFoxtrot)}`);
    if (remadePath) {
        const moves = loadMoves();
        moves[movesBeforeFoxtrot] = { ...moves[movesBeforeFoxtrot], remadeAs: remadePath };
        saveMoves(moves);
    }
    const foxtrotRow = (await clips('admin', { searchTerm: `${PROBE} Foxtrot` })).Items[0];
    const atRemake = await call('admin', 'GET', '/Bookmarks/Clips/Summary');
    const atRemakeList = await call('admin', 'GET', '/Bookmarks/Clips', { query: { limit: 200 } });
    const forget = await call('admin', 'DELETE', `/Bookmarks/${f.Id}/Clip`, { query: { onlyIfMissing: 'true' } });
    const afterForget = await statusOf('admin', f);
    Object.assign(remake, {
        first: remake.polls[remake.polls.length - 1], remadeAs: remadePath && path.basename(remadePath), ownName: remadePath === firstFoxtrot,
        noNumberedFile: !fs.existsSync(numbered), newJob: !!record && !!record.Clip && record.Clip.RequestedAt !== firstJob,
        fileThere: !!remadePath && fs.existsSync(remadePath),
        listed: foxtrotRow && foxtrotRow.Clip.State, summaryMissing: atRemake.json && atRemake.json.Missing,
        listMissing: atRemakeList.json && atRemakeList.json.Items.filter((r) => r.Clip.State === 'Missing').length,
        forget: { status: forget.status, detail: forget.json && forget.json.detail }, fileAfterForget: !!remadePath && fs.existsSync(remadePath), afterForget
    });
    evidence.remake = remake;
    check('3. the remake of a missing clip: Foxtrot\'s file held out reads Missing; made again it takes back its own first name (since Bookmarks 1.2.1; no numbered file), and the first poll after its job says Completed and Ready (not Missing), as do the list and the summary',
        remake.missing.State === 'Missing' && remake.first.Status === 'Completed' && remake.first.State === 'Ready'
        && remake.ownName && remake.noNumberedFile && remake.newJob && remake.fileThere && remake.listed === 'Ready' && remake.summaryMissing === remake.listMissing, remake);
    check('3. the page\'s Forget (DELETE /Bookmarks/{id}/Clip?onlyIfMissing=true) of the clip made again: 409 "The clip file is there after all…", the file and the clip as they were',
        forget.status === 409 && typeof remake.forget.detail === 'string' && remake.forget.detail.startsWith('The clip file is there after all')
        && remake.fileAfterForget && afterForget.State === 'Ready', remake.forget);

    // The probe's held copy of Foxtrot's first clip can't go back (put back, it would be a file no clip names): it goes, and
    // its move with it. From here on Foxtrot's file is the remade one.
    if (remadePath) files.f = remadePath;
    undoMoves(movesBeforeFoxtrot);
    check('3. the held copy of Foxtrot\'s first clip discarded (the one file the probe deletes itself), the other moves kept, the remade file in place under the first name',
        !fs.existsSync(heldFoxtrot) && loadMoves().length === movesBeforeFoxtrot && fs.existsSync(heldFile) && fs.existsSync(files.f) && files.f === firstFoxtrot,
        { held: fs.existsSync(heldFoxtrot), moves: loadMoves().length, firstName: files.f === firstFoxtrot });

    // ---- 4. the list
    const listed = await clips('admin');
    const rows = Object.fromEntries(listed.Items.map((r) => [byId.get(guid(r.Id)), r]));
    const facts = (r) => r && { State: r.Clip.State, BookmarkDeleted: r.BookmarkDeleted, FileExists: r.Clip.FileExists, RootAvailable: r.Clip.RootAvailable, FileName: r.Clip.FileName, Container: r.Clip.Container, ItemAvailable: r.ItemAvailable };
    evidence.list = { total: listed.TotalRecordCount, order: L(listed.Items), rows: Object.fromEntries(Object.entries(rows).map(([k, r]) => [k, facts(r)])) };
    check('4. GET /Bookmarks/Clips (admin): its six clips, the bookmark without a clip and viewer2\'s not among them: Alpha Ready, Bravo (deleted with its clip kept) Ready and BookmarkDeleted, Delta Missing (its folder there), Foxtrot (made again) Ready, Charlie and Golf FolderUnavailable (their folder unplugged: not Missing, no file, no root)',
        listed.TotalRecordCount === 6 && same([...L(listed.Items)].sort(), ['a', 'b', 'c', 'd', 'f', 'g'])
        && rows.a.Clip.State === 'Ready' && rows.a.BookmarkDeleted === false && rows.a.Clip.FileExists === true && rows.a.ItemAvailable === true
        && rows.b.Clip.State === 'Ready' && rows.b.BookmarkDeleted === true && rows.b.Clip.FileExists === true
        && rows.d.Clip.State === 'Missing' && rows.d.Clip.FileExists === false && rows.d.Clip.RootAvailable === true
        && rows.f.Clip.State === 'Ready' && rows.f.Clip.FileExists === true
        && ['c', 'g'].every((k) => rows[k].Clip.State === 'FolderUnavailable' && rows[k].Clip.FileExists === false && rows[k].Clip.RootAvailable === false)
        && Object.values(rows).every((r) => r.Clip.Container === 'mkv' && r.Clip.FileName && r.Clip.FileName.startsWith('Long Test Movie')), evidence.list);
    check('4. no server path in the list\'s JSON: the clip file\'s name only', !/\.sandbox|bookmarks-clips-probe|[A-Za-z]:\\\\/i.test(listed ? JSON.stringify(listed) : ''), {});

    const filtered = {
        ready: L((await clips('admin', { state: 'Ready' })).Items).sort(),
        offline: L((await clips('admin', { state: 'FolderUnavailable' })).Items).sort(),
        missing: L((await clips('admin', { state: 'Missing' })).Items),
        missingOrOffline: L((await clips('admin', { state: 'Missing,FolderUnavailable' })).Items).sort(),
        withoutDeleted: L((await clips('admin', { includeDeleted: 'false' })).Items).sort(),
        bySearch: L((await clips('admin', { searchTerm: 'CLIPS-PROBE charlie' })).Items),
        whiskeyAsAdmin: (await clips('admin', { searchTerm: `${PROBE} Whiskey` })).TotalRecordCount
    };
    evidence.filters = filtered;
    check('4. the filters: state (one and two), includeDeleted=false (no Bravo), the search ignoring case; viewer2\'s clip never, even searched for by its name',
        same(filtered.ready, ['a', 'b', 'f']) && same(filtered.offline, ['c', 'g']) && same(filtered.missing, ['d']) && same(filtered.missingOrOffline, ['c', 'd', 'g'])
        && same(filtered.withoutDeleted, ['a', 'c', 'd', 'f', 'g']) && same(filtered.bySearch, ['c']) && filtered.whiskeyAsAdmin === 0, filtered);

    const viewer2List = await clips('viewer2');
    const viewerList = await call('viewer', 'GET', '/Bookmarks/Clips', { query: { searchTerm: PROBE } });
    evidence.otherUsers = { viewer2: L(viewer2List.Items), viewer: { status: viewerList.status, total: viewerList.json && viewerList.json.TotalRecordCount } };
    check('4. viewer2 lists its own clip only (none of admin\'s); viewer, without download permission, lists its clips too (none here)',
        same(L(viewer2List.Items), ['w']) && viewer2List.Items[0].Clip.State === 'Ready' && viewerList.status === 200 && viewerList.json.TotalRecordCount === 0, evidence.otherUsers);

    // ---- 5. Summary and Status
    const summary = await call('admin', 'GET', '/Bookmarks/Clips/Summary');
    const everything = await call('admin', 'GET', '/Bookmarks/Clips', { query: { limit: 200 } });
    const unknown = crypto.randomUUID();
    const statusIds = [d.Id, w.Id, a.Id, unknown, e.Id, c.Id, b.Id].map(guid).join(',');
    const states = await call('admin', 'GET', '/Bookmarks/Clips/Status', { query: { ids: statusIds } });
    evidence.summary = summary.json;
    evidence.status = states.json && states.json.map((s) => ({ l: byId.get(guid(s.Id)), Status: s.Status, State: s.State, FileName: !!s.FileName }));
    const counted = (st) => everything.json.Items.filter((r) => r.Clip.State === st).length;
    check('5. Summary (admin) matches its whole list: Count, InProgress, Failed, Missing (Delta among them), FolderUnavailable (Charlie and Golf among them)',
        summary.status === 200 && summary.json.Count === everything.json.TotalRecordCount && everything.json.TotalRecordCount <= 200
        && summary.json.InProgress === counted('InProgress') && summary.json.Failed === counted('Failed') && summary.json.Missing === counted('Missing')
        && summary.json.FolderUnavailable === counted('FolderUnavailable') && summary.json.Missing >= 1 && summary.json.FolderUnavailable >= 2 && summary.json.TotalBytes > 0,
        { summary: summary.json, total: everything.json.TotalRecordCount });
    check('5. Status (ids comma-separated, without dashes): admin\'s four in the order asked, with their states; viewer2\'s clip, the bookmark without a clip and an unknown id left out',
        states.status === 200 && same(evidence.status.map((s) => s.l), ['d', 'a', 'c', 'b'])
        && same(evidence.status.map((s) => s.State), ['Missing', 'Ready', 'FolderUnavailable', 'Ready']), evidence.status);

    // ---- 6. BulkDelete of what isn't admin's to delete, or can't be deleted now
    const storeBefore = fs.readFileSync(storeFile);
    const refused = await call('admin', 'POST', '/Bookmarks/Clips/BulkDelete', { body: { Ids: [c.Id, w.Id, e.Id] } });
    const wStill = fs.existsSync(files.w) && same(L((await clips('viewer2')).Items), ['w']);
    const cStill = fs.existsSync(path.join(usbRoot + UNPLUGGED, path.relative(usbRoot, files.c)));
    evidence.refusedBulk = { status: refused.status, result: refused.json && Object.fromEntries(Object.entries(refused.json).map(([k, v]) => [k, L(v.map((id) => ({ Id: id })))])), viewer2ClipStill: wStill, charlieFileStill: cStill, storeUnchanged: Buffer.compare(storeBefore, fs.readFileSync(storeFile)) === 0 };
    check('6. BulkDelete (admin) of the clip in the unplugged folder, viewer2\'s clip and the bookmark without a clip: FolderUnavailable [Charlie], NotFound [Whiskey, Echo], nothing deleted or written; viewer2\'s file still there and still listed for viewer2',
        refused.status === 200 && same(Object.keys(refused.json), ['FilesDeleted', 'RecordsRemoved', 'NotFound', 'InProgress', 'InUse', 'FolderUnavailable', 'Failed'])
        && same(evidence.refusedBulk.result.FolderUnavailable, ['c']) && same(evidence.refusedBulk.result.NotFound, ['w', 'e'])
        && refused.json.FilesDeleted.length === 0 && refused.json.RecordsRemoved.length === 0 && refused.json.InProgress.length === 0
        && refused.json.InUse.length === 0 && refused.json.Failed.length === 0 && wStill && cStill && evidence.refusedBulk.storeUnchanged, evidence.refusedBulk);

    // ---- 7. back: the file put back, the folder plugged in
    undoMoves(0);
    const back = await waitFor('Delta, Charlie and Golf Ready again once the list\'s verdicts of 30 s are up', async () => {
        const now = await clips('admin', { state: 'Ready' });
        return L(now.Items).length === 6 ? now : null;
    }, 40);
    evidence.back = { ready: L(back.Items).sort() };
    check('7. the file put back and the folder plugged in: all six Ready again (within the list\'s 30 s)', same(evidence.back.ready, ['a', 'b', 'c', 'd', 'f', 'g']), evidence.back);

    const deleted = await call('admin', 'POST', '/Bookmarks/Clips/BulkDelete', { body: { Ids: [a.Id, b.Id, c.Id, d.Id, f.Id, g.Id] } });
    const afterRows = await clips('admin');
    const aBookmark = await call('admin', 'GET', `/Bookmarks/${a.Id}`);
    const bFile = await call('admin', 'GET', `/Bookmarks/${b.Id}/Clip/File`);
    evidence.bulk = {
        status: deleted.status, result: deleted.json && Object.fromEntries(Object.entries(deleted.json).map(([k, v]) => [k, L(v.map((id) => ({ Id: id })))])),
        filesLeft: ['a', 'b', 'c', 'd', 'f', 'g'].filter((k) => fs.existsSync(files[k])), listedAfter: afterRows.TotalRecordCount,
        alphaStays: aBookmark.status === 200 && !aBookmark.json.Clip, bravoClipFile: bFile.status
    };
    check('7. BulkDelete (admin) of its six: FilesDeleted [Alpha, Charlie, Delta, Foxtrot, Golf] (the bookmarks stay without a clip), RecordsRemoved [Bravo] (its clip file 404 now), every file gone from disk, none listed',
        deleted.status === 200 && same(evidence.bulk.result.FilesDeleted, ['a', 'c', 'd', 'f', 'g']) && same(evidence.bulk.result.RecordsRemoved, ['b'])
        && deleted.json.NotFound.length === 0 && evidence.bulk.filesLeft.length === 0 && afterRows.TotalRecordCount === 0
        && evidence.bulk.alphaStays && bFile.status === 404 && fs.existsSync(files.w), evidence.bulk);

    // ---- 8. refusals
    const refusals = {};
    for (const [label, p, q] of [['state=Gone', '/Bookmarks/Clips', { state: 'Gone' }], ['mode=Slow', '/Bookmarks/Clips', { mode: 'Slow' }],
        ['sortBy=UpdatedAt', '/Bookmarks/Clips', { sortBy: 'UpdatedAt' }], ['limit=201', '/Bookmarks/Clips', { limit: 201 }],
        ['Status without ids', '/Bookmarks/Clips/Status', {}], ['Status ids=nope', '/Bookmarks/Clips/Status', { ids: `${guid(a.Id)},nope` }]]) {
        const r = await call('admin', 'GET', p, { query: q });
        refusals[label] = { status: r.status, detail: r.json && (r.json.detail || (r.json.errors && Object.values(r.json.errors).flat()[0])) };
    }
    const emptyBulk = await call('admin', 'POST', '/Bookmarks/Clips/BulkDelete', { body: { Ids: [] } });
    refusals['BulkDelete of no ids'] = { status: emptyBulk.status, detail: emptyBulk.json && emptyBulk.json.detail };
    const notABoolean = await call('admin', 'DELETE', `/Bookmarks/${a.Id}/Clip`, { query: { onlyIfMissing: 'perhaps' } });
    refusals['onlyIfMissing=perhaps'] = { status: notABoolean.status, detail: notABoolean.json && notABoolean.json.errors && Object.values(notABoolean.json.errors).flat()[0] };
    const anonymous = await call('anonymous', 'GET', '/Bookmarks/Clips');
    evidence.refusals = { ...refusals, anonymous: anonymous.status };
    check('8. 400 for a state, mode or sort that is none of them, a limit over 200, Status without ids or with one that isn\'t a GUID, a BulkDelete of no ids, an onlyIfMissing that isn\'t a boolean; 401 without a token',
        Object.values(refusals).every((r) => r.status === 400) && refusals['limit=201'].detail === 'The limit must be between 1 and 200.'
        && refusals['Status without ids'].detail === 'Ask for at least one clip.' && refusals['Status ids=nope'].detail === 'The value \'nope\' is not a GUID.'
        && refusals['BulkDelete of no ids'].detail === 'Choose at least one clip to delete.' && anonymous.status === 401, evidence.refusals);
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const context = { savedSettings: null, files: null };
    const startLog = logPosition();
    evidence.leftovers = { moves: loadMoves().length, settings: fs.existsSync(savedSettingsFile) };
    undoMoves(0);
    await restoreSavedSettings();
    let failure = null;
    try {
        await run(context);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        const cleanup = { settingsRestored: null, deleted: [] };
        try {
            undoMoves(0);
            cleanup.settingsRestored = await restoreSavedSettings();
            for (const { who, id } of made) {
                const removed = await call(who, 'DELETE', `/Bookmarks/${id}`, { query: { deleteClip: 'true' } });
                const clip = await call(who, 'DELETE', `/Bookmarks/${id}/Clip`);
                cleanup.deleted.push({ id, bookmark: removed.status, clip: clip.status });
            }
        } catch (error) {
            check('clean-up: the setting, the folder, the file and the bookmarks put back', false, String(error));
        }
        evidence.cleanup = cleanup;
        const text = logSince(startLog);
        const lines = text.split('\n');
        const errors = lines.filter((l) => /\[ERR\]|\[FTL\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        const naming = lines.filter((l) => /\[(INF|WRN|ERR|FTL)\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l) && l.includes(PROBE));
        check('9. no Error of Bookmarks or the kit in the server log during the run, and no line of theirs at Information or above naming a clip-probe bookmark',
            errors.length === 0 && naming.length === 0, { errors: errors.slice(0, 5), naming: naming.slice(0, 5) });
        const left = [];
        for (const { who, id } of made) if ((await call(who, 'GET', `/Bookmarks/${id}`)).status !== 404) left.push(id);
        const filesLeft = Object.values((context.files) || {}).filter((f) => f && fs.existsSync(f));
        const heldLeft = fs.existsSync(heldRoot) ? fs.readdirSync(heldRoot) : [];
        const settingNow = (await getConfiguration()).ClipOutputPath ?? '';
        check('9. clean-up: no move left, nothing in the hold folder, the clip folder plugged in, the setting as it was (not the probe\'s folder), the probe\'s bookmarks and clip files gone',
            loadMoves().length === 0 && heldLeft.length === 0 && fs.existsSync(usbRoot) && !fs.existsSync(usbRoot + UNPLUGGED) && !fs.existsSync(savedSettingsFile)
            && path.resolve(settingNow || '.').toLowerCase() !== usbRoot.toLowerCase() && left.length === 0 && filesLeft.length === 0,
            { moves: loadMoves().length, held: heldLeft.length, left, filesLeft: filesLeft.length, setting: settingNow ? 'set' : 'default' });
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
