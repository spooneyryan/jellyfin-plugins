#!/usr/bin/env node
// User Ratings' member API in a running sandbox, as a curl-style walkthrough. Node 22 built-ins only. Loopback only, never port 8096. The sandbox users' tokens come from credentials.json and are never
// printed; the API key the probe makes for its check is deleted again at once, and its token is never printed either.
//
//   node scripts/userratings-api-probe.mjs --sandbox default [--readonly-drill] [--keep-files] [--out result.json]
//
// It needs a running sandbox with User Ratings 0.3.3 or later (Initialize-Sandbox.ps1: the users admin, viewer and viewer2,
// the libraries Movies and Movies 2). It rates synthetic sandbox movies only, as admin, viewer and viewer2:
//   1. who may call: nobody 401; an API key (made for the check, then deleted) 403 with ProblemDetails on every member route;
//      the script anonymous;
//   2. Capabilities for an admin and a member (IsAdmin from the request's role, CanRate, the limits, the kinds);
//   3. an item's read and the writes, as the member API shapes them: stars, a review, the summary the same for everyone, the other
//      members' list (excludes one's own) with its orders and pages, clearing the stars of a reviewed row (a text-only
//      review), clearing the review (the row deleted), DELETE Mine;
//   4. the errors: 400 bad stars, an empty or too long review, paging and sort values (a sort's number too); 413 a body
//      over 64 KB (Kestrel's, as ProblemDetails); one 404 for an item the caller can't open and one that doesn't exist;
//      IfUpdatedUtc sent back as a JavaScript Date writes it (milliseconds) saves, the same again 409; the review's own
//      preconditions: IfReviewUpdatedUtc saves after the stars changed elsewhere and is 409 after a text saved since,
//      IfNoReview saves a first review and is 409 for a second, two preconditions at once 400;
//   5. a double click: four PUTs at once, every one 200, one row, its value the answer with the latest UpdatedUtc;
//   6. WhoCanRate = SelectedUsers (admin and viewer): viewer2 reads (Can.Reason NotARater), gets 403 on a write, still
//      deletes an older own row; RequirePlayedToRate: Can.Reason NeedsPlay and the PUT 403 until viewer has played the
//      movie (marked played, then unplayed again); ratings off: Capabilities 200 with Enabled false, an item's read 403,
//      Mine/Inaccessible holds viewer's rows (the configuration and the played state are put back in a finally);
//   7. lost library access: viewer2's rating in Movies 2, then viewer2's libraries without Movies 2 (the policy is put
//      back in a finally): the item 404, Mine/Inaccessible lists the row with nothing about the item, DELETE
//      Mine/{ratingId} 204 (another member's id and an unknown one are the same 404);
//   8. with --readonly-drill, the kit's newer-version drill on the sandbox's own database: the sandbox stopped, a copy of
//      userratings.db kept in the probe's folder, PRAGMA user_version = 99 set, started: Store ReadOnlyNewerVersion,
//      StoreReadOnly true, reads 200, every write 503 before the item lookup (an unknown item too), DELETE Mine/{id} 503;
//      stopped, the copy put back, started: Ok again and writable.
// Every rating it made is deleted at the end (by its id, as its owner), and the Records count is back where it started
// (each delete leaves a tombstone: ids and times only). The files it touches must be the sandbox's own.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { PRODUCTION_PORT } from './sandbox-policy.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const NO_USER = "This endpoint needs a signed-in user; API keys can't be used.";
const NOT_FOUND = "This item wasn't found, or it can't be rated.";
const NOT_A_RATER = 'Rating is limited to selected members.';
const RATINGS_OFF = 'Ratings are turned off on this server.';
const CHANGED = 'Your rating or review was changed in another window. Reload to see it.';
const REVIEW_CHANGED = 'Your review was changed in another window. Reload to see it.';
const NOT_YOURS = 'No rating of yours has that id.';
const BAD_STARS = 'Stars must be between 0.5 and 5 in steps of 0.5.';
const EMPTY_REVIEW = 'A review needs some text.';
const TOO_LARGE = 'The request is too large.';
const NEWER = 'Data was written by a newer plugin version; install that version or restore an export.';
const PROBE = 'ur-api-probe';

function usage() {
    return 'usage: node scripts/userratings-api-probe.mjs --sandbox <name> [--readonly-drill] [--keep-files] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, readonlyDrill: false, keepFiles: false, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--readonly-drill') o.readonlyDrill = true;
        else if (a === '--keep-files') o.keepFiles = true;
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
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer', 'viewer2']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}

// The sandbox's own files only: its database (Jellyfin's DataPath is <DataDir>/data) and the probe's folder.
const inside = (child, parent) => {
    const rel = path.relative(path.resolve(parent), path.resolve(child));
    return rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel);
};
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
const database = path.join(dataDir, 'data', 'userratings', 'userratings.db');
const workDir = path.join(sandboxRoot, 'api-probe');
for (const p of [database, workDir]) {
    if (!inside(p, sandboxRoot) || /programdata/i.test(p)) throw new Error(`refusing to work in ${p}: not in the sandbox ${sandboxRoot}`);
}
const createdFile = path.join(workDir, 'created.json');

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, restarts: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authorization(token) {
    return `MediaBrowser Client="${PROBE}", Device="sandbox", DeviceId="${PROBE}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// One request as `who` ('admin', 'viewer', 'viewer2', 'anonymous' or { apiKey }). Never follows redirects.
async function call(who, method, p, { body, raw, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json' };
    if (who && typeof who === 'object' && who.apiKey) h.Authorization = `MediaBrowser Token="${who.apiKey}"`;
    else if (who !== 'anonymous') h.Authorization = authorization(users[who].token);
    let payload;
    if (raw !== undefined) { payload = raw; h['Content-Type'] = 'application/json'; }
    else if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(60000) });
    const text = await res.text();
    const type = res.headers.get('content-type') || '';
    let json = null;
    if (/json/i.test(type) && text.length) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, type, json, text };
}

const isProblem = (r) => /application\/problem\+json/i.test(r.type) && r.json && r.json.status === r.status;
const pick = (r) => (r.json && typeof r.json.title === 'string' ? { status: r.status, title: r.json.title, detail: r.json.detail ?? null } : { status: r.status });
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const item = (id) => `/UserRatings/Items/${id}`;
const stars = (dto) => dto && dto.Mine ? dto.Mine.Stars ?? null : null;

// A time as Jellyfin writes it ("2026-10-20T08:01:02.1230000Z", the fraction trimmed or padded) in 100 ns ticks. Since the
// User Ratings writes whole milliseconds, and a row's next time is at least one millisecond after its
// last, so of four writes in one millisecond the last committed still has the latest time.
function ticks(iso) {
    const m = /^(.+T\d\d:\d\d:\d\d)(?:\.(\d{1,7}))?Z$/.exec(iso || '');
    if (!m) throw new Error(`not a UTC time: ${iso}`);
    return BigInt(Date.parse(m[1] + 'Z')) * 10000n + BigInt((m[2] || '').padEnd(7, '0'));
}

// The ratings the probe made, by owner: kept on disk so a run that stopped half way can be cleaned up by the next.
function loadCreated() {
    try { return readJson(createdFile); } catch { return []; }
}
function saveCreated(list) {
    fs.mkdirSync(workDir, { recursive: true });
    fs.writeFileSync(createdFile, JSON.stringify(list, null, 2));
}
function remember(who, dto) {
    if (!dto || !dto.Mine) return;
    const list = loadCreated();
    if (!list.some((c) => c.ratingId === dto.Mine.Id)) {
        list.push({ who, ratingId: dto.Mine.Id, itemId: dto.ItemId });
        saveCreated(list);
    }
}

async function rate(who, id, value) {
    const r = await call(who, 'PUT', `${item(id)}/Mine/Rating`, { body: { Stars: value } });
    if (r.status === 200) remember(who, r.json);
    return r;
}

async function review(who, id, body) {
    const r = await call(who, 'PUT', `${item(id)}/Mine/Review`, { body });
    if (r.status === 200) remember(who, r.json);
    return r;
}

async function records() {
    const d = await call('admin', 'GET', '/UserRatings/Admin/Diagnostics');
    return d.json ? { attached: d.json.Records?.Attached ?? null, store: d.json.Store?.Status ?? null, version: d.json.PluginVersion, schema: d.json.Store?.Version ?? null } : null;
}

async function movies(libraryName, count) {
    const folders = (await call('admin', 'GET', '/Library/VirtualFolders')).json || [];
    const library = folders.find((l) => l.Name === libraryName);
    if (!library) throw new Error(`the sandbox has no library "${libraryName}"; run Initialize-Sandbox.ps1`);
    const r = await call('admin', 'GET', '/Items', { query: { userId: users.admin.id, ParentId: library.ItemId, IncludeItemTypes: 'Movie', Recursive: 'true', SortBy: 'SortName', Limit: 20 } });
    const list = (r.json && r.json.Items) || [];
    if (list.length < count) throw new Error(`the library "${libraryName}" has ${list.length} movies; ${count} needed`);
    return { library, items: list.slice(0, count).map((i) => ({ id: i.Id, name: i.Name })) };
}

// Runs one of the repository's sandbox scripts, its output into a file (a pipe would stay open as long as the server
// dev-server.ps1 starts in the background lives).
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

async function waitUntilServing() {
    for (let i = 0; i < 90; i++) {
        const r = await call('admin', 'GET', '/UserRatings/Capabilities').catch(() => null);
        if (r && r.status === 200) return;
        await delay(1000);
    }
    throw new Error('the sandbox did not answer /UserRatings/Capabilities within 90 s');
}

// PRAGMA user_version = <n> on the sandbox's database, with the server stopped, in a separate Node process (node:sqlite).
function setUserVersion(version) {
    const code = `
        import { DatabaseSync } from 'node:sqlite';
        const db = new DatabaseSync(process.argv[1]);
        db.exec('PRAGMA user_version = ' + Number(process.argv[2]) + ';');
        const v = db.prepare('PRAGMA user_version;').get();
        db.close();
        process.stdout.write(JSON.stringify(v));
    `;
    const r = spawnSync(process.execPath, ['--no-warnings', '--input-type=module', '-e', code, database, String(version)], { encoding: 'utf8', windowsHide: true, timeout: 60000 });
    if (r.status !== 0) throw new Error(`setting user_version failed: ${r.stderr}`);
    return JSON.parse(r.stdout).user_version;
}

const sidecars = (file) => [file, file + '-wal', file + '-shm'];

// ------------------------------------------------------------------------------------------------------------- the run

const context = { key: null, configuration: null, viewer2Policy: null, played: null, drill: null };
const started = Date.now();
try {
    // ---- 0. the sandbox, the plugin, the movies; leftovers of a run that stopped half way go first
    const diagnostics = await call('admin', 'GET', '/UserRatings/Admin/Diagnostics');
    if (diagnostics.status !== 200) throw new Error(`GET /UserRatings/Admin/Diagnostics: ${diagnostics.status}; is User Ratings loaded?`);
    for (const left of loadCreated()) await call(left.who, 'DELETE', `/UserRatings/Mine/${left.ratingId}`);
    saveCreated([]);
    const before = await records();
    evidence.before = before;
    check('User Ratings 0.3.3 or later is loaded, its database Ok', before && /^(0\.3\.(3|[4-9]|\d\d)|[1-9]\d*\.\d+\.\d+)\./.test(before.version) && before.store === 'Ok', before);
    const { items: [a, b, c] } = await movies('Movies', 3);
    const { library: movies2, items: [m2] } = await movies('Movies 2', 1);
    evidence.items = { a, b, c, m2 };
    for (const who of ['admin', 'viewer', 'viewer2']) {
        for (const x of [a, b, c, m2]) {
            const r = await call(who, 'GET', item(x.id));
            if (r.status === 200 && r.json.Mine) throw new Error(`${who} already rated ${x.name}; the probe rates only what nobody of its users rated (delete it first)`);
        }
    }

    // ---- 1. who may call
    for (const route of ['/UserRatings/Capabilities', item(a.id), `${item(a.id)}/Reviews`, '/UserRatings/Mine/Inaccessible']) {
        const r = await call('anonymous', 'GET', route);
        check(`nobody: ${route.replace(a.id, '{a}')} 401`, r.status === 401, { status: r.status });
    }
    const keyApp = `${PROBE}-${crypto.randomBytes(4).toString('hex')}`;
    const keyCreated = await call('admin', 'POST', '/Auth/Keys', { query: { app: keyApp } });
    const keyList = await call('admin', 'GET', '/Auth/Keys');
    const key = keyList.json && keyList.json.Items && keyList.json.Items.find((k) => k.AppName === keyApp);
    if (!key) throw new Error(`an API key could not be made for the check: ${keyCreated.status}, ${keyList.status}`);
    context.key = key.AccessToken;
    const byKey = [];
    for (const [method, route, body] of [
        ['GET', '/UserRatings/Capabilities'], ['GET', item(a.id)], ['GET', `${item(a.id)}/Reviews`], ['PUT', `${item(a.id)}/Mine/Rating`, { Stars: 3 }],
        ['DELETE', `${item(a.id)}/Mine/Rating`], ['PUT', `${item(a.id)}/Mine/Review`, { Text: PROBE }], ['DELETE', `${item(a.id)}/Mine/Review`],
        ['DELETE', `${item(a.id)}/Mine`], ['GET', '/UserRatings/Mine/Inaccessible'], ['DELETE', `/UserRatings/Mine/${crypto.randomUUID()}`]
    ]) {
        const r = await call({ apiKey: context.key }, method, route, { body });
        byKey.push({ method, route: route.replace(a.id, '{a}'), ...pick(r), problem: isProblem(r) });
    }
    check('an API key: 403 ProblemDetails "needs a signed-in user" on all ten member routes', byKey.every((r) => r.status === 403 && r.problem && r.detail === NO_USER), byKey);
    const removedKey = await call('admin', 'DELETE', `/Auth/Keys/${context.key}`);
    check('the API key made for the check is deleted', removedKey.status === 204, { status: removedKey.status });
    if (removedKey.status === 204) context.key = null;
    const script = await call('anonymous', 'GET', '/UserRatings/ClientScript');
    check('GET /UserRatings/ClientScript is anonymous JavaScript', script.status === 200 && /javascript/i.test(script.type), { status: script.status, type: script.type });

    // ---- 2. Capabilities
    const capsAdmin = (await call('admin', 'GET', '/UserRatings/Capabilities')).json;
    const capsViewer = (await call('viewer', 'GET', '/UserRatings/Capabilities')).json;
    check('Capabilities: admin IsAdmin, viewer not; both may rate and review; ratings on, writable', capsAdmin && capsViewer && capsAdmin.IsAdmin === true && capsViewer.IsAdmin === false
        && capsAdmin.Enabled && capsViewer.CanRate && capsViewer.CanReview && capsViewer.StoreReadOnly === false,
    { admin: capsAdmin && { IsAdmin: capsAdmin.IsAdmin, CanRate: capsAdmin.CanRate }, viewer: capsViewer && { IsAdmin: capsViewer.IsAdmin, CanRate: capsViewer.CanRate, CanReview: capsViewer.CanReview } });
    check('Capabilities: the defaults (5000, the nine kinds, top-start, no Top Rated page before 1.1)', capsViewer && capsViewer.MaxReviewLength === 5000
        && capsViewer.RateableItemTypes.join(',') === 'Movie,Series,Season,Episode,Video,MusicVideo,MusicAlbum,Book,AudioBook'
        && capsViewer.Display.Posters.Position === 'top-start' && capsViewer.Display.ReviewsPageSize === 20 && capsViewer.TopRatedPageEnabled === false,
    capsViewer && { MaxReviewLength: capsViewer.MaxReviewLength, RateableItemTypes: capsViewer.RateableItemTypes, Display: capsViewer.Display, TopRatedPageEnabled: capsViewer.TopRatedPageEnabled });

    // ---- 3. an item's read and the writes
    const empty = await call('viewer', 'GET', item(a.id));
    check(`GET Items/{a} (${a.name}): 200, no Mine, an empty summary, ten buckets, Can Rate and Review, not Moderate`, empty.status === 200 && !('Mine' in empty.json)
        && empty.json.Summary.Count === 0 && empty.json.Summary.Distribution.length === 10 && empty.json.Can.Rate && empty.json.Can.Review && !empty.json.Can.Moderate,
    { status: empty.status, Summary: empty.json && empty.json.Summary, Can: empty.json && empty.json.Can });
    const byAdmin = await rate('admin', a.id, 4.5);
    const byViewer = await rate('viewer', a.id, 3.5);
    check('PUT Mine/Rating: admin 4.5 and viewer 3.5, each 200 with the item after the write (count 2, average 4.0, buckets 7 and 9)', byAdmin.status === 200 && byViewer.status === 200
        && stars(byViewer.json) === 3.5 && byViewer.json.Summary.Count === 2 && byViewer.json.Summary.Average === 4 && byViewer.json.Summary.Distribution[6] === 1 && byViewer.json.Summary.Distribution[8] === 1,
    { admin: byAdmin.status, viewer: byViewer.status, Summary: byViewer.json && byViewer.json.Summary });
    const reviewed = await review('viewer', a.id, { Text: `${PROBE}: a review\r\nwith two lines`, IsSpoiler: true });
    check('PUT Mine/Review: 200, the text normalised, the spoiler flag kept, ReviewCount 1', reviewed.status === 200 && reviewed.json.Mine.Text === `${PROBE}: a review\nwith two lines`
        && reviewed.json.Mine.IsSpoiler === true && reviewed.json.Summary.ReviewCount === 1 && !!reviewed.json.Mine.ReviewUpdatedUtc,
    { status: reviewed.status, Mine: reviewed.json && reviewed.json.Mine && { Text: reviewed.json.Mine.Text, IsSpoiler: reviewed.json.Mine.IsSpoiler } });
    const asViewer2 = await call('viewer2', 'GET', item(a.id));
    const names = (asViewer2.json && asViewer2.json.Reviews.Items.map((r) => r.UserName)) || [];
    check('viewer2 reads the same summary, both other rows in the list (newest first), no Mine', asViewer2.status === 200 && !('Mine' in asViewer2.json)
        && asViewer2.json.Summary.Count === 2 && asViewer2.json.Reviews.TotalRecordCount === 2 && names[0] === 'viewer' && names[1] === 'admin',
    { status: asViewer2.status, names, Summary: asViewer2.json && asViewer2.json.Summary });
    const asAdmin = await call('admin', 'GET', item(a.id));
    check('the admin reads it with Can.Moderate; one row of the list (viewer\'s), its own as Mine', asAdmin.status === 200 && asAdmin.json.Can.Moderate === true
        && asAdmin.json.Reviews.TotalRecordCount === 1 && stars(asAdmin.json) === 4.5, { Can: asAdmin.json && asAdmin.json.Can, total: asAdmin.json && asAdmin.json.Reviews.TotalRecordCount });
    const highest = await call('viewer2', 'GET', `${item(a.id)}/Reviews`, { query: { sort: 'Highest' } });
    const lowest = await call('viewer2', 'GET', `${item(a.id)}/Reviews`, { query: { sort: 'Lowest', limit: 1 } });
    const second = await call('viewer2', 'GET', `${item(a.id)}/Reviews`, { query: { sort: 'Lowest', startIndex: 1, limit: 1 } });
    check('GET Items/{a}/Reviews: Highest admin then viewer; Lowest pages of one: viewer, then admin; TotalRecordCount 2', highest.status === 200
        && highest.json.Items.map((r) => r.UserName).join(',') === 'admin,viewer' && lowest.json.Items.map((r) => r.UserName).join(',') === 'viewer'
        && second.json.Items.map((r) => r.UserName).join(',') === 'admin' && second.json.TotalRecordCount === 2 && second.json.StartIndex === 1,
    { highest: highest.json && highest.json.Items.map((r) => r.UserName), lowest: lowest.json && lowest.json.Items.map((r) => r.UserName), second: second.json && second.json.Items.map((r) => r.UserName) });

    // ---- 4. the errors
    const badStars = [];
    for (const raw of ['{"Stars":0}', '{"Stars":3.3}', '{"Stars":5.5}', '{}']) {
        const r = await call('viewer', 'PUT', `${item(b.id)}/Mine/Rating`, { raw });
        badStars.push({ raw, ...pick(r), problem: isProblem(r) });
    }
    const notANumber = await call('viewer', 'PUT', `${item(b.id)}/Mine/Rating`, { raw: '{"Stars":"abc"}' });
    check('bad stars (0, 3.3, 5.5, none): 400 ProblemDetails with the stars message; "abc": MVC\'s 400', badStars.every((r) => r.status === 400 && r.problem && r.detail === BAD_STARS)
        && notANumber.status === 400, { badStars, notANumber: pick(notANumber) });
    const emptyReview = await call('viewer', 'PUT', `${item(b.id)}/Mine/Review`, { body: { Text: '  \r\n ' } });
    const longReview = await call('viewer', 'PUT', `${item(b.id)}/Mine/Review`, { body: { Text: 'x'.repeat(5001) } });
    check('an empty review and one of 5001 characters: 400', emptyReview.status === 400 && emptyReview.json.detail === EMPTY_REVIEW && longReview.status === 400 && isProblem(longReview),
        { empty: pick(emptyReview), long: pick(longReview) });
    const tooLarge = await call('viewer', 'PUT', `${item(b.id)}/Mine/Review`, { raw: JSON.stringify({ Text: 'y'.repeat(70 * 1024) }) });
    check('a body over 64 KB: 413 ProblemDetails (Kestrel\'s limit, the kit\'s filter)', tooLarge.status === 413 && isProblem(tooLarge) && tooLarge.json.detail === TOO_LARGE, pick(tooLarge));
    const paging = [];
    for (const query of [{ limit: 0 }, { limit: 201 }, { startIndex: -1 }, { sort: 'Best' }, { sort: '1' }]) {
        const r = await call('viewer', 'GET', `${item(a.id)}/Reviews`, { query });
        paging.push({ query, status: r.status });
    }
    check('paging and sort out of range or unknown, a sort\'s number too (sort=1): 400', paging.every((p) => p.status === 400), paging);
    const unknown = await call('viewer', 'GET', item(crypto.randomUUID().replace(/-/g, '')));
    const unknownWrite = await call('viewer', 'PUT', `${item(crypto.randomUUID())}/Mine/Rating`, { body: { Stars: 3 } });
    check('an item that doesn\'t exist: 404 with the one message, on a read and a write', unknown.status === 404 && unknown.json.detail === NOT_FOUND && unknownWrite.status === 404
        && unknownWrite.json.detail === NOT_FOUND, { read: pick(unknown), write: pick(unknownWrite) });

    // IfUpdatedUtc: two windows, each sending the time back as a JavaScript Date holds it (whole milliseconds, "Z").
    const asDate = (iso) => new Date(iso).toISOString();
    const seen = asDate(reviewed.json.Mine.UpdatedUtc);
    const first = await review('viewer', a.id, { Text: `${PROBE}: from window one`, IfUpdatedUtc: seen });
    const conflict = await call('viewer', 'PUT', `${item(a.id)}/Mine/Review`, { body: { Text: `${PROBE}: from window two`, IfUpdatedUtc: seen } });
    const afterConflict = await call('viewer', 'GET', item(a.id));
    const third = await review('viewer', a.id, { Text: `${PROBE}: from window two, reloaded`, IfUpdatedUtc: first.json && asDate(first.json.Mine.UpdatedUtc) });
    check('IfUpdatedUtc as a JavaScript Date sends it: the first window saves, the second (the same time) is 409 and changes nothing, after a reload it saves (Edited)', first.status === 200
        && conflict.status === 409 && isProblem(conflict) && conflict.json.detail === CHANGED && afterConflict.json.Mine.Text === `${PROBE}: from window one`
        && third.status === 200 && third.json.Mine.Edited === true, { first: first.status, conflict: pick(conflict), kept: afterConflict.json && afterConflict.json.Mine.Text, third: third.status });

    // The review's own preconditions: IfReviewUpdatedUtc is the time of
    // the review itself, which only a change of its text moves, so the stars changed in another window don't make the
    // review's Save a 409; a text saved since does. IfNoReview: a first review, a 409 when there is one. IfUpdatedUtc with
    // either, or both of them, a 400 that changes nothing.
    const reviewSeen = asDate(third.json.Mine.ReviewUpdatedUtc);
    const starsElsewhere = await rate('viewer', a.id, 2);
    const ownTime = await review('viewer', a.id, { Text: `${PROBE}: against the review's own time`, IfReviewUpdatedUtc: reviewSeen });
    const ownStale = await call('viewer', 'PUT', `${item(a.id)}/Mine/Review`, { body: { Text: `${PROBE}: against a review saved since`, IfReviewUpdatedUtc: reviewSeen } });
    const firstReview = await review('viewer', b.id, { Text: `${PROBE}: a first review`, IfNoReview: true });
    const secondFirst = await call('viewer', 'PUT', `${item(b.id)}/Mine/Review`, { body: { Text: `${PROBE}: a first review from another window`, IfNoReview: true } });
    const both = [];
    for (const extra of [{ IfUpdatedUtc: seen, IfReviewUpdatedUtc: reviewSeen }, { IfUpdatedUtc: seen, IfNoReview: true }, { IfReviewUpdatedUtc: reviewSeen, IfNoReview: true }]) {
        const r = await call('viewer', 'PUT', `${item(a.id)}/Mine/Review`, { body: Object.assign({ Text: `${PROBE}: two preconditions` }, extra) });
        both.push({ sent: Object.keys(extra), status: r.status, problem: isProblem(r) });
    }
    const afterPreconditions = await call('viewer', 'GET', item(a.id));
    const afterFirst = await call('viewer', 'GET', item(b.id));
    check('IfReviewUpdatedUtc: the stars changed elsewhere (the row\'s time moved, the review\'s didn\'t) and the review saves; the same review time again 409 with the review\'s sentence and nothing changed',
        starsElsewhere.status === 200 && starsElsewhere.json.Mine.UpdatedUtc !== third.json.Mine.UpdatedUtc && asDate(starsElsewhere.json.Mine.ReviewUpdatedUtc) === reviewSeen
        && ownTime.status === 200 && ownTime.json.Mine.Stars === 2 && ownStale.status === 409 && isProblem(ownStale) && ownStale.json.detail === REVIEW_CHANGED
        && afterPreconditions.json.Mine.Text === `${PROBE}: against the review's own time`,
    { stars: starsElsewhere.status, own: ownTime.status, stale: pick(ownStale), kept: afterPreconditions.json && afterPreconditions.json.Mine.Text });
    check('IfNoReview: a first review saves; a second "first" review from another window is 409 with the review\'s sentence and changes nothing', firstReview.status === 200
        && secondFirst.status === 409 && isProblem(secondFirst) && secondFirst.json.detail === REVIEW_CHANGED && afterFirst.json.Mine.Text === `${PROBE}: a first review`,
    { first: firstReview.status, second: pick(secondFirst), kept: afterFirst.json && afterFirst.json.Mine && afterFirst.json.Mine.Text });
    check('IfUpdatedUtc with IfReviewUpdatedUtc or IfNoReview, or those two together: 400 ProblemDetails, nothing changed', both.every((r) => r.status === 400 && r.problem)
        && afterPreconditions.json.Mine.Text === `${PROBE}: against the review's own time`, both);
    // The first review's row on b goes again: section 5 counts viewer2's rows there.
    const firstGone = await call('viewer', 'DELETE', `${item(b.id)}/Mine`);
    check('the first review\'s row on b deleted again', firstGone.status === 204, { status: firstGone.status });

    // Clearing: the stars of a reviewed row leave a text-only review; then the review deletes the row.
    const textOnly = await call('viewer', 'DELETE', `${item(a.id)}/Mine/Rating`);
    check('DELETE Mine/Rating on a reviewed row: a text-only review (no Stars), count 1', textOnly.status === 200 && textOnly.json.Mine && !('Stars' in textOnly.json.Mine)
        && !!textOnly.json.Mine.Text && textOnly.json.Summary.Count === 1, { status: textOnly.status, Mine: textOnly.json && textOnly.json.Mine && { Stars: textOnly.json.Mine.Stars, hasText: !!textOnly.json.Mine.Text } });
    const noReview = await call('viewer', 'DELETE', `${item(a.id)}/Mine/Review`);
    check('DELETE Mine/Review on a row without stars: the row is deleted (no Mine)', noReview.status === 200 && !('Mine' in noReview.json), { status: noReview.status });

    // ---- 5. a double click: four PUTs at once
    const rounds = [];
    for (let round = 0; round < 3; round++) {
        const values = [1.5, 4.5, 3, 5];
        const answers = await Promise.all(values.map((v) => rate('viewer2', b.id, v)));
        const now = await call('viewer2', 'GET', item(b.id));
        const ok = answers.filter((r) => r.status === 200).map((r) => r.json.Mine);
        const latest = ok.slice().sort((x, y) => (ticks(x.UpdatedUtc) < ticks(y.UpdatedUtc) ? -1 : ticks(x.UpdatedUtc) > ticks(y.UpdatedUtc) ? 1 : 0)).pop();
        rounds.push({ statuses: answers.map((r) => r.status), final: stars(now.json), latest: latest && latest.Stars, count: now.json && now.json.Summary.Count, ids: [...new Set(ok.map((m) => m.Id))].length });
    }
    check('a double click (four PUTs at once, three rounds): all 200, one row, its value the answer with the latest UpdatedUtc', rounds.every((r) => r.statuses.every((s) => s === 200)
        && r.count === 1 && r.ids === 1 && r.final === r.latest), rounds);

    // ---- 6. WhoCanRate and the ratings switch (the configuration put back in the finally)
    const pluginId = 'f53c7c34-8cba-4846-b43b-956816b2f6ae';
    const configuration = await call('admin', 'GET', `/Plugins/${pluginId}/Configuration`);
    if (configuration.status !== 200) throw new Error(`GET the plugin's configuration: ${configuration.status}`);
    context.configuration = configuration.json;
    const older = await review('viewer2', c.id, { Text: `${PROBE}: an older review` });
    const setConfig = async (changes) => {
        const r = await call('admin', 'POST', `/Plugins/${pluginId}/Configuration`, { body: { ...context.configuration, ...changes } });
        if (r.status !== 204) throw new Error(`POST the plugin's configuration: ${r.status}`);
    };
    await setConfig({ WhoCanRate: 'SelectedUsers', RaterUserIds: [users.admin.id, users.viewer.id] });
    const v2caps = (await call('viewer2', 'GET', '/UserRatings/Capabilities')).json;
    const v2item = await call('viewer2', 'GET', item(c.id));
    const v2write = await call('viewer2', 'PUT', `${item(c.id)}/Mine/Rating`, { body: { Stars: 4 } });
    const v2edit = await call('viewer2', 'PUT', `${item(c.id)}/Mine/Review`, { body: { Text: `${PROBE}: edited` } });
    const viewerWrite = await rate('viewer', c.id, 2.5);
    check('WhoCanRate SelectedUsers (admin, viewer): viewer2 CanRate false, reads its own older row and the summary, 403 on rate and edit; viewer still rates',
        older.status === 200 && v2caps.CanRate === false && v2item.status === 200 && v2item.json.Can.Rate === false && v2item.json.Can.Reason === 'NotARater'
        && v2item.json.Mine && v2item.json.Mine.Text === `${PROBE}: an older review`
        && v2write.status === 403 && v2write.json.detail === NOT_A_RATER && v2edit.status === 403 && viewerWrite.status === 200 && viewerWrite.json.Summary.ReviewCount === 1,
    { caps: v2caps && v2caps.CanRate, item: pick(v2item), write: pick(v2write), edit: pick(v2edit), viewer: viewerWrite.status });
    const v2delete = await call('viewer2', 'DELETE', `${item(c.id)}/Mine`);
    const v2after = await call('viewer2', 'GET', item(c.id));
    check('… and viewer2 can still delete its own older row (204), which goes from everyone\'s summary', v2delete.status === 204 && !('Mine' in v2after.json) && v2after.json.Summary.ReviewCount === 0,
        { delete: v2delete.status, ReviewCount: v2after.json && v2after.json.Summary.ReviewCount });
    // RequirePlayedToRate: Can says NeedsPlay while the PUT is the 403; played, both allow it.
    await setConfig({ RequirePlayedToRate: true });
    const playedCaps = (await call('viewer', 'GET', '/UserRatings/Capabilities')).json;
    const unplayed = await call('viewer', 'GET', item(b.id));
    const unplayedWrite = await call('viewer', 'PUT', `${item(b.id)}/Mine/Rating`, { body: { Stars: 2 } });
    context.played = { who: 'viewer', id: b.id };
    const marked = await call('viewer', 'POST', `/UserPlayedItems/${b.id}`, { query: { userId: users.viewer.id } });
    const nowPlayed = await call('viewer', 'GET', item(b.id));
    const unmarked = await call('viewer', 'DELETE', `/UserPlayedItems/${b.id}`, { query: { userId: users.viewer.id } });
    if (unmarked.status === 200) context.played = null;
    check('RequirePlayedToRate: Capabilities says so; unplayed, Can {Rate:false, Reason:"NeedsPlay"} and the PUT 403; played, Can.Rate true (then unplayed again)',
        playedCaps.RequirePlayedToRate === true && playedCaps.CanRate === true && unplayed.status === 200 && unplayed.json.Can.Rate === false && unplayed.json.Can.Review === false
        && unplayed.json.Can.Reason === 'NeedsPlay' && unplayedWrite.status === 403 && marked.status === 200 && nowPlayed.json.Can.Rate === true && !('Reason' in nowPlayed.json.Can)
        && unmarked.status === 200,
    { caps: playedCaps && playedCaps.RequirePlayedToRate, unplayed: unplayed.json && unplayed.json.Can, write: pick(unplayedWrite), marked: marked.status, played: nowPlayed.json && nowPlayed.json.Can, unmarked: unmarked.status });
    await setConfig({ EnableRatings: false });
    const offCaps = await call('viewer', 'GET', '/UserRatings/Capabilities');
    const offItem = await call('viewer', 'GET', item(a.id));
    const offWrite = await call('viewer', 'PUT', `${item(a.id)}/Mine/Rating`, { body: { Stars: 2 } });
    const offList = await call('viewer', 'GET', '/UserRatings/Mine/Inaccessible');
    const viewerRowOnC = viewerWrite.json && viewerWrite.json.Mine && viewerWrite.json.Mine.Id;
    check('ratings off: Capabilities 200 with Enabled false and no kinds; an item\'s read and a write 403 "turned off"; Mine/Inaccessible holds viewer\'s rows (the item routes reach none)',
        offCaps.status === 200 && offCaps.json.Enabled === false && offCaps.json.RateableItemTypes.length === 0 && offItem.status === 403 && offItem.json.detail === RATINGS_OFF && offWrite.status === 403
        && offList.status === 200 && offList.json.Items.some((e) => guid(e.RatingId) === guid(viewerRowOnC)),
    { caps: offCaps.json && { Enabled: offCaps.json.Enabled, CanRate: offCaps.json.CanRate }, item: pick(offItem), write: pick(offWrite), listed: offList.json && offList.json.TotalRecordCount });
    const restoredConfig = await call('admin', 'POST', `/Plugins/${pluginId}/Configuration`, { body: context.configuration });
    if (restoredConfig.status === 204) context.configuration = null;
    const onList = await call('viewer', 'GET', '/UserRatings/Mine/Inaccessible');
    check('the plugin\'s configuration is put back as it was; viewer\'s row on an item it opens is out of the list again', restoredConfig.status === 204
        && (await call('viewer2', 'GET', '/UserRatings/Capabilities')).json.CanRate === true && !onList.json.Items.some((e) => guid(e.RatingId) === guid(viewerRowOnC)),
    { status: restoredConfig.status, listed: onList.json && onList.json.TotalRecordCount });

    // ---- 7. lost library access (viewer2's policy put back in the finally)
    const inMovies2 = await review('viewer2', m2.id, { Text: `${PROBE}: rated before the library was taken away` });
    await rate('viewer2', m2.id, 3);
    await rate('viewer', m2.id, 5);
    const viewer2 = await call('admin', 'GET', `/Users/${users.viewer2.id}`);
    context.viewer2Policy = viewer2.json.Policy;
    const folders = (await call('admin', 'GET', '/Library/VirtualFolders')).json;
    const enabled = folders.filter((l) => l.ItemId !== movies2.ItemId).map((l) => l.ItemId);
    const restricted = await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: { ...context.viewer2Policy, EnableAllFolders: false, EnabledFolders: enabled } });
    if (restricted.status !== 204) throw new Error(`POST /Users/viewer2/Policy: ${restricted.status}`);
    const lostRead = await call('viewer2', 'GET', item(m2.id));
    const lostDelete = await call('viewer2', 'DELETE', `${item(m2.id)}/Mine`);
    const viewerSees = await call('viewer', 'GET', item(m2.id));
    const inaccessible = await call('viewer2', 'GET', '/UserRatings/Mine/Inaccessible');
    const entry = inaccessible.json && inaccessible.json.Items.find((e) => guid(e.RatingId) === guid(inMovies2.json && inMovies2.json.Mine.Id));
    check('viewer2 without Movies 2: the item 404 (read and delete through the item); the row still counts for viewer', inMovies2.status === 200 && lostRead.status === 404
        && lostRead.json.detail === NOT_FOUND && lostDelete.status === 404 && viewerSees.json.Summary.Count === 2, { read: pick(lostRead), delete: pick(lostDelete), viewerCount: viewerSees.json && viewerSees.json.Summary.Count });
    check('Mine/Inaccessible lists the row: RatingId, Stars, TextSnippet and the dates only, nothing about the item', inaccessible.status === 200 && !!entry && entry.Stars === 3
        && Object.keys(entry).sort().join(',') === 'CreatedUtc,RatingId,Stars,TextSnippet,UpdatedUtc' && !inaccessible.text.includes(m2.name) && !inaccessible.text.toLowerCase().includes(guid(m2.id))
        && !inaccessible.text.includes('Movies 2'), { status: inaccessible.status, total: inaccessible.json && inaccessible.json.TotalRecordCount, keys: entry && Object.keys(entry) });
    const viewerMine = (await call('viewer', 'GET', item(m2.id))).json.Mine;
    const notYours = await call('viewer2', 'DELETE', `/UserRatings/Mine/${viewerMine.Id}`);
    const missing = await call('viewer2', 'DELETE', `/UserRatings/Mine/${crypto.randomUUID()}`);
    const ownDelete = await call('viewer2', 'DELETE', `/UserRatings/Mine/${entry && entry.RatingId}`);
    const again = await call('viewer2', 'DELETE', `/UserRatings/Mine/${entry && entry.RatingId}`);
    const emptied = await call('viewer2', 'GET', '/UserRatings/Mine/Inaccessible');
    const viewerAfter = await call('viewer', 'GET', item(m2.id));
    check('DELETE Mine/{ratingId}: another member\'s id and an unknown one the same 404; the own row 204, then 404; the list empty; viewer\'s count 1',
        notYours.status === 404 && missing.status === 404 && notYours.text === missing.text && notYours.json.detail === NOT_YOURS && ownDelete.status === 204 && again.status === 404
        && !emptied.json.Items.some((e) => guid(e.RatingId) === guid(entry && entry.RatingId)) && viewerAfter.json.Summary.Count === 1,
    { notYours: pick(notYours), missing: pick(missing), own: ownDelete.status, again: again.status, left: emptied.json && emptied.json.TotalRecordCount, viewerCount: viewerAfter.json && viewerAfter.json.Summary.Count });
    const restoredPolicy = await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: context.viewer2Policy });
    if (restoredPolicy.status === 204) context.viewer2Policy = null;
    check('viewer2\'s policy is put back; Movies 2 opens again', restoredPolicy.status === 204 && (await call('viewer2', 'GET', item(m2.id))).status === 200, { status: restoredPolicy.status });

    // ---- 8. the newer-version drill on the sandbox's own database
    if (opts.readonlyDrill) {
        const backup = path.join(workDir, 'drill-backup');
        context.drill = { backup, restored: false };
        runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox], 'drill-stop');
        fs.rmSync(backup, { recursive: true, force: true });
        fs.mkdirSync(backup, { recursive: true });
        const copied = [];
        for (const f of sidecars(database)) {
            if (fs.existsSync(f)) { fs.copyFileSync(f, path.join(backup, path.basename(f))); copied.push(path.basename(f)); }
        }
        const hashBefore = crypto.createHash('sha256').update(fs.readFileSync(database)).digest('hex');
        evidence.drill = { copied, setTo: setUserVersion(99) };
        runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'], 'drill-start');
        await waitUntilServing();
        const store = await records();
        const caps = (await call('viewer', 'GET', '/UserRatings/Capabilities')).json;
        const read = await call('viewer2', 'GET', item(b.id));
        if (!read.json || !read.json.Mine) throw new Error('viewer2 has no rating of {b} to delete in the drill');
        const writes = [];
        for (const [method, route, body] of [
            ['PUT', `${item(b.id)}/Mine/Rating`, { Stars: 2 }], ['PUT', `${item(crypto.randomUUID())}/Mine/Rating`, { Stars: 2 }],
            ['PUT', `${item(b.id)}/Mine/Review`, { Text: PROBE }], ['DELETE', `${item(b.id)}/Mine`], ['DELETE', `/UserRatings/Mine/${read.json && read.json.Mine && read.json.Mine.Id}`]
        ]) {
            const r = await call('viewer2', method, route, { body });
            writes.push({ method, route: route.replace(b.id, '{b}'), ...pick(r), problem: isProblem(r) });
        }
        check('the drill: user_version 99 opens ReadOnlyNewerVersion; StoreReadOnly true, CanRate false; reads 200', evidence.drill.setTo === 99 && store.store === 'ReadOnlyNewerVersion'
            && caps.StoreReadOnly === true && caps.CanRate === false && read.status === 200 && read.json.Can.Rate === false && read.json.Can.Reason === 'ReadOnly',
        { store, StoreReadOnly: caps.StoreReadOnly, read: read.status, Can: read.json && read.json.Can });
        check('the drill: every write 503 with the newer-version sentence, before the item lookup (an unknown item too), DELETE Mine/{id} too',
            writes.every((w) => w.status === 503 && w.problem && w.detail === NEWER), writes);
        runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox], 'drill-stop-2');
        for (const f of sidecars(database)) fs.rmSync(f, { force: true });
        for (const name of copied) fs.copyFileSync(path.join(backup, name), path.join(path.dirname(database), name));
        context.drill.restored = true;
        const hashAfter = crypto.createHash('sha256').update(fs.readFileSync(database)).digest('hex');
        runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'], 'drill-restart');
        await waitUntilServing();
        const back = await records();
        const writable = await rate('viewer2', b.id, 2);
        check('the drill: the copy put back (the same bytes as before the drill), Ok again, writable', hashBefore === hashAfter && back.store === 'Ok' && writable.status === 200,
            { store: back, write: writable.status, sameBytes: hashBefore === hashAfter });
        if (!opts.keepFiles) fs.rmSync(backup, { recursive: true, force: true });
    }

    // ---- the end: every rating the probe made deleted by its owner, the count where it started
    const left = loadCreated();
    const deletes = [];
    for (const made of left) {
        const r = await call(made.who, 'DELETE', `/UserRatings/Mine/${made.ratingId}`);
        deletes.push(r.status);
    }
    saveCreated([]);
    const after = await records();
    evidence.after = after;
    check('every rating the probe made is deleted (204, or 404 for those it deleted itself); Records.Attached as before', deletes.every((s) => s === 204 || s === 404)
        && after.attached === before.attached, { deletes, before: before.attached, after: after.attached });
} catch (e) {
    check('probe ran', false, String(e && e.stack ? e.stack.split('\n').slice(0, 4).join(' | ') : e));
} finally {
    if (context.key) await call('admin', 'DELETE', `/Auth/Keys/${context.key}`).catch(() => null);
    if (context.configuration) await call('admin', 'POST', `/Plugins/f53c7c34-8cba-4846-b43b-956816b2f6ae/Configuration`, { body: context.configuration }).catch(() => null);
    if (context.viewer2Policy) await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: context.viewer2Policy }).catch(() => null);
    if (context.played) await call(context.played.who, 'DELETE', `/UserPlayedItems/${context.played.id}`, { query: { userId: users[context.played.who].id } }).catch(() => null);
    if (context.drill && !context.drill.restored) {
        // A drill that failed half way: put the copy back with the server stopped, then start it again.
        try {
            runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox], 'drill-recover-stop');
            const names = fs.existsSync(context.drill.backup) ? fs.readdirSync(context.drill.backup) : [];
            if (names.includes(path.basename(database))) {
                for (const f of sidecars(database)) fs.rmSync(f, { force: true });
                for (const name of names) fs.copyFileSync(path.join(context.drill.backup, name), path.join(path.dirname(database), name));
            }
            runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'], 'drill-recover-start');
            check('the failed drill\'s copy was put back', true, names);
        } catch (e) {
            check('the failed drill\'s copy was put back', false, String(e.message));
        }
    }
}

evidence.seconds = Math.round((Date.now() - started) / 1000);
const failed = checks.filter((x) => !x.pass);
const result = { pass: failed.length === 0, checks, evidence };
if (opts.out) {
    fs.mkdirSync(path.dirname(path.resolve(opts.out)), { recursive: true });
    fs.writeFileSync(opts.out, JSON.stringify(result, null, 2));
}
for (const x of checks) console.log(`${x.pass ? 'PASS' : 'FAIL'} ${x.name}${x.pass ? '' : ` (${JSON.stringify(x.detail)})`}`);
console.log(`${checks.length - failed.length} of ${checks.length} passed`);
process.exit(failed.length === 0 ? 0 : 1);
