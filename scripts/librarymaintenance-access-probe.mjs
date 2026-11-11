#!/usr/bin/env node
// Library Maintenance's library access (admin endpoints aren't exempt from library access) against a running sandbox:
// an administrator limited to some libraries sees, in the duplicate groups, the index's status and Diagnostics, only
// what those libraries hold. Node 22 built-ins only. Loopback only, never port 8096. The tokens come from
// credentials.json and are never printed.
//
//   node scripts/librarymaintenance-access-probe.mjs --sandbox default [--timeout <scan seconds>] [--out result.json]
//
// It changes the sandbox for the run and puts it back in a finally: it adds one library, "LM Access Probe Shared", over
// Movies' own folder (inside .sandbox/media only; any other name or path is refused), so every movie of Movies is in two
// libraries; it makes viewer2 an administrator limited to Movies (and refuses to start when viewer2 is an administrator
// already: a run that stopped before its finally, which Initialize-Sandbox.ps1 puts right); and it adds one API key of
// its own name. Then, as admin, viewer2, viewer, anonymous and the key: viewer2's groups have Movies' libraries only, no
// entry names the shared library (Title + Year and Title), every entry viewer2 is shown it can open, its totals count
// Movies only, asking for the shared library by id gives nothing; its own group by id, 404 for one only admin has; its
// /Index lists and counts Movies only; its Diagnostics hides DuplicatesIndex and DuplicateGroups; the admin sees the
// shared library on the same groups (so there was something to hide); anonymous 401, viewer 403, and the key sees every
// library the index read. The finally revokes the key, restores viewer2's policy, removes the library (refreshLibrary
// true, as the dashboard sends it), scans, rebuilds the index and checks the sandbox is as before. A library of the
// probe's name left by a stopped run is removed first. It prints one JSON result (pass, checks, evidence) and exits
// with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const PRODUCTION_PORT = 8096;
const GROUPS = '/LibraryMaintenance/Duplicates/Groups';
const INDEX = '/LibraryMaintenance/Duplicates/Index';
const REBUILD = '/LibraryMaintenance/Duplicates/Index/Rebuild';
const DIAGNOSTICS = '/LibraryMaintenance/Admin/Diagnostics';
const SHARED = 'LM Access Probe Shared';
const KEY_APP = 'librarymaintenance-access-probe';
const OPEN_CHECKS = 40;
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

function usage() {
    return 'Usage: node scripts/librarymaintenance-access-probe.mjs --sandbox <name> [--timeout <scan seconds>] [--out <file>]';
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
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const who of ['admin', 'viewer', 'viewer2']) {
    if (!users[who] || !users[who].token || !users[who].id) throw new Error(`no ${who} in credentials.json; run Initialize-Sandbox.ps1`);
}

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// Jellyfin writes GUIDs without dashes ("N"); ASP.NET Core's JSON writes them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
let keyToken = null;

async function call(who, method, p, { query, body } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: 'application/json' };
    const token = who === 'key' ? keyToken : who === 'anonymous' ? null : users[who].token;
    if (token) {
        h.Authorization = `MediaBrowser Client="${KEY_APP}", Device="sandbox", DeviceId="${KEY_APP}", Version="1.0.0", Token="${token}"`;
    }
    if (body !== undefined) h['Content-Type'] = 'application/json';
    const res = await fetch(url, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text };
}

async function admin(method, p, options) {
    const r = await call('admin', method, p, options);
    if (r.status < 200 || r.status >= 300) throw new Error(`${method} ${p}: ${r.status} ${r.text.slice(0, 300)}`);
    return r.json;
}

function inMedia(p) {
    const resolved = path.resolve(p).toLowerCase();
    return resolved.startsWith(path.resolve(mediaRoot).toLowerCase() + path.sep) && !/^z:/i.test(resolved);
}

async function scanAndWait() {
    const started = Date.now();
    await admin('POST', '/Library/Refresh');
    const deadline = started + opts.timeout * 1000;
    for (;;) {
        await delay(500);
        const task = ((await admin('GET', '/ScheduledTasks', { query: { isHidden: false } })) || []).find((t) => t.Key === 'RefreshLibrary');
        const end = task && task.LastExecutionResult && Date.parse(task.LastExecutionResult.EndTimeUtc);
        if (task && task.State === 'Idle' && end >= started - 1000) return;
        if (Date.now() > deadline) throw new Error(`the library scan didn't finish within ${opts.timeout} s`);
    }
}

async function rebuild() {
    const version = (await admin('GET', INDEX)).Version;
    await admin('POST', REBUILD);
    for (let i = 0; i < 2400; i++) {
        const status = await admin('GET', INDEX);
        if (status.Version > version && !status.Rebuilding) return status;
        await delay(250);
    }
    throw new Error('the index wasn\'t rebuilt within 10 min');
}

async function removeShared() {
    if (SHARED !== 'LM Access Probe Shared') throw new Error('refusing to remove another library');
    const r = await call('admin', 'DELETE', '/Library/VirtualFolders', { query: { name: SHARED, refreshLibrary: true } });
    await scanAndWait();
    return r.status;
}

// A page of groups, after the build if the index has none yet (202).
async function groups(who, query) {
    for (let i = 0; i < 120; i++) {
        const r = await call(who, 'GET', GROUPS, { query: { limit: 200, ...query } });
        if (r.status !== 202) return r;
        await delay(500);
    }
    throw new Error('no index after 60 s');
}

const entriesOf = (body) => (body?.Items || []).flatMap((g) => g.Entries.map((e) => ({ key: g.Key, entry: e })));
const librariesOf = (body) => [...new Set(entriesOf(body).flatMap(({ entry }) => (entry.Libraries || []).map((l) => guid(l.Id))))];

let failure = null;
let original = null;
let added = false;
const before = {};
try {
    // The sandbox as it is: its libraries, the index's, and viewer2 a plain user.
    let folders = await admin('GET', '/Library/VirtualFolders');
    if (folders.some((f) => f.Name === SHARED)) {
        evidence.leftover = await removeShared();
        await rebuild();
        folders = await admin('GET', '/Library/VirtualFolders');
    }
    before.libraries = folders.map((f) => f.Name).sort();
    before.index = (await admin('GET', INDEX)).Libraries.map((l) => `${l.Name}:${l.Movies}`).sort();
    original = (await admin('GET', `/Users/${users.viewer2.id}`)).Policy;
    if (original.IsAdministrator) {
        const policy = original;
        original = null;
        throw new Error(`viewer2 is an administrator (EnableAllFolders ${policy.EnableAllFolders}): a run stopped before its finally; run Initialize-Sandbox.ps1`);
    }
    for (const key of ((await admin('GET', '/Auth/Keys')).Items || []).filter((k) => k.AppName === KEY_APP)) {
        await call('admin', 'DELETE', `/Auth/Keys/${key.AccessToken}`);
    }

    // The setup: the shared library over Movies' folder, then viewer2 an administrator limited to Movies.
    const movies = folders.find((f) => f.Name === 'Movies');
    if (!movies || !(movies.Locations || []).length) throw new Error('the sandbox has no Movies library; run Initialize-Sandbox.ps1');
    const location = movies.Locations[0];
    if (!inMedia(location)) throw new Error(`refusing ${location}: Movies' folder is not in the sandbox's media`);
    const options = {
        LibraryOptions: {
            Enabled: true, EnableRealtimeMonitor: false, EnableInternetProviders: false, SaveLocalMetadata: false,
            EnableChapterImageExtraction: false, ExtractChapterImagesDuringLibraryScan: false, EnableTrickplayImageExtraction: false,
            ExtractTrickplayImagesDuringLibraryScan: false, EnableEmbeddedTitles: false, AutomaticRefreshIntervalDays: 0,
            MetadataSavers: [], DisabledSubtitleFetchers: [],
            TypeOptions: [{ Type: 'Movie', MetadataFetchers: [], MetadataFetcherOrder: [], ImageFetchers: [], ImageFetcherOrder: [] }],
            PathInfos: [{ Path: location }]
        }
    };
    await admin('POST', '/Library/VirtualFolders', { query: { name: SHARED, collectionType: 'movies', paths: location, refreshLibrary: false }, body: options });
    added = true;
    await scanAndWait();
    const index = await rebuild();
    const shared = (await admin('GET', '/Library/VirtualFolders')).find((f) => f.Name === SHARED);
    const moviesId = guid(movies.ItemId);
    const sharedId = guid(shared.ItemId);
    const indexed = (id) => (index.Libraries || []).find((l) => guid(l.Id) === id);
    evidence.index = (index.Libraries || []).map((l) => `${l.Name}:${l.Movies}`);
    check('the shared library is indexed, with Movies\' movies', indexed(sharedId) && indexed(moviesId) && indexed(sharedId).Movies === indexed(moviesId).Movies,
        evidence.index.join(', '));

    await admin('POST', `/Users/${users.viewer2.id}/Policy`, { body: { ...original, IsAdministrator: true, EnableAllFolders: false, EnabledFolders: [movies.ItemId] } });
    evidence.viewer2 = 'an administrator limited to Movies';

    // The groups, as admin and as viewer2, in both modes.
    const a = (await groups('admin')).json;
    const aTitle = (await groups('admin', { mode: 'Title' })).json;
    const b = await groups('viewer2');
    const bTitle = (await groups('viewer2', { mode: 'Title' })).json;
    evidence.admin = { GroupCount: a.GroupCount, TitleGroups: aTitle.GroupCount, TotalMovies: a.TotalMovies, Libraries: a.EffectiveLibraryIds.length };
    evidence.viewer2Groups = { status: b.status, GroupCount: b.json?.GroupCount, TitleGroups: bTitle?.GroupCount, TotalMovies: b.json?.TotalMovies, EffectiveLibraryIds: b.json?.EffectiveLibraryIds };
    check('viewer2\'s groups: Movies only', b.status === 200 && b.json.EffectiveLibraryIds.length === 1 && guid(b.json.EffectiveLibraryIds[0]) === moviesId,
        `${b.status} ${JSON.stringify(b.json?.EffectiveLibraryIds)}`);
    const seenAdmin = librariesOf(a).concat(librariesOf(aTitle));
    check('the admin sees the shared library on the groups (so there is something to hide)', seenAdmin.includes(sharedId) && seenAdmin.includes(moviesId),
        `${new Set(seenAdmin).size} libraries in the admin's entries`);
    const outside = [...new Set(librariesOf(b.json).concat(librariesOf(bTitle)))].filter((id) => id !== moviesId);
    const named = entriesOf(b.json).concat(entriesOf(bTitle)).filter(({ entry }) => (entry.Libraries || []).some((l) => l.Name === SHARED));
    check('no entry viewer2 is shown names another library than Movies (Title + Year and Title)', outside.length === 0 && named.length === 0,
        `${outside.length} other libraries, ${named.length} entries naming the shared one`);

    const shown = [...new Set(entriesOf(b.json).concat(entriesOf(bTitle)).flatMap(({ entry }) => [entry.ItemId, ...(entry.LinkedVersionIds || [])]))];
    const unopened = [];
    for (const id of shown.slice(0, OPEN_CHECKS)) {
        const r = await call('viewer2', 'GET', `/Items/${id}`, { query: { userId: users.viewer2.id } });
        if (r.status !== 200) unopened.push(`${id.slice(0, 8)} ${r.status}`);
    }
    check('viewer2 can open every item it is shown', shown.length > 0 && unopened.length === 0,
        `${Math.min(shown.length, OPEN_CHECKS)} of ${shown.length} opened; ${unopened.slice(0, 3).join(', ') || 'none refused'}`);
    check('viewer2\'s totals count Movies only', b.json.TotalMovies === indexed(moviesId).Movies && b.json.TotalMovies < a.TotalMovies,
        `${b.json.TotalMovies} (Movies ${indexed(moviesId).Movies}, admin ${a.TotalMovies})`);

    const asked = await groups('viewer2', { libraryIds: sharedId });
    check('viewer2 asking for the shared library by id: 200, no libraries, no groups', asked.status === 200 && asked.json.EffectiveLibraryIds.length === 0 && asked.json.GroupCount === 0,
        `${asked.status} ${JSON.stringify(asked.json?.EffectiveLibraryIds)} ${asked.json?.GroupCount}`);

    // One group by its id in the mode that has one for viewer2 (Title + Year first), and one only the admin has.
    const [mode, mine, theirs] = b.json.Items.length ? ['TitleAndYear', b.json, a] : ['Title', bTitle, aTitle];
    const own = mine.Items[0];
    const ownRead = own ? await call('viewer2', 'GET', `${GROUPS}/${own.GroupId}`, { query: { mode } }) : null;
    const adminOnly = theirs.Items.find((g) => !mine.Items.some((x) => x.GroupId === g.GroupId));
    const adminOnlyRead = adminOnly ? await call('viewer2', 'GET', `${GROUPS}/${adminOnly.GroupId}`, { query: { mode } }) : null;
    check('viewer2 reads its own group by id, and gets 404 for one only the admin has', ownRead?.status === 200 && ownRead.json.GroupId === own.GroupId && (!adminOnly || adminOnlyRead.status === 404),
        `${mode}: ${ownRead?.status}, ${adminOnly ? adminOnlyRead.status : 'no admin-only group'}`);

    // The index's status: viewer2's lists and counts Movies only.
    const aIndex = await admin('GET', INDEX);
    const bIndex = await call('viewer2', 'GET', INDEX);
    evidence.indexStatus = { admin: { Movies: aIndex.Movies, Entries: aIndex.Entries, Libraries: aIndex.Libraries.map((l) => l.Name) }, viewer2: { status: bIndex.status, Movies: bIndex.json?.Movies, Entries: bIndex.json?.Entries, Libraries: (bIndex.json?.Libraries || []).map((l) => l.Name) } };
    check('viewer2\'s index status lists and counts Movies only', bIndex.status === 200 && bIndex.json.Libraries.length === 1 && guid(bIndex.json.Libraries[0].Id) === moviesId
        && bIndex.json.Movies === indexed(moviesId).Movies && bIndex.json.Movies < aIndex.Movies && aIndex.Libraries.some((l) => guid(l.Id) === sharedId),
        `${bIndex.status}: ${evidence.indexStatus.viewer2.Libraries.join(', ')} (${bIndex.json?.Movies} movies), admin ${aIndex.Libraries.length} libraries (${aIndex.Movies})`);

    // Diagnostics: the server-wide numbers hidden from viewer2, shown to the admin.
    const aDiag = await admin('GET', DIAGNOSTICS);
    const bDiag = await call('viewer2', 'GET', DIAGNOSTICS);
    const hidden = (source) => source && Object.keys(source).length === 1 && /^Hidden/.test(source.Scope || '');
    evidence.diagnostics = { admin: Object.keys(aDiag.DuplicatesIndex || {}).length, viewer2: bDiag.json && { DuplicatesIndex: bDiag.json.DuplicatesIndex, DuplicateGroups: bDiag.json.DuplicateGroups } };
    check('Diagnostics hides DuplicatesIndex and DuplicateGroups from viewer2, not from the admin',
        bDiag.status === 200 && hidden(bDiag.json.DuplicatesIndex) && hidden(bDiag.json.DuplicateGroups) && !hidden(aDiag.DuplicatesIndex) && 'Groupings' in (aDiag.DuplicateGroups || {}),
        `${bDiag.status}`);

    check('anonymous 401, viewer 403', (await call('anonymous', 'GET', GROUPS)).status === 401 && (await call('viewer', 'GET', GROUPS)).status === 403);

    // An API key: every library the index read.
    await admin('POST', '/Auth/Keys', { query: { app: KEY_APP } });
    keyToken = ((await admin('GET', '/Auth/Keys')).Items || []).find((k) => k.AppName === KEY_APP)?.AccessToken || null;
    const k = keyToken ? await groups('key') : null;
    check('an API key sees every library the index read', k?.status === 200 && k.json.EffectiveLibraryIds.length === index.Libraries.length,
        `${k?.status} ${k?.json?.EffectiveLibraryIds?.length}/${index.Libraries.length}`);
} catch (e) {
    failure = String(e && e.stack || e);
} finally {
    const cleanup = {};
    try {
        if (keyToken) cleanup.keyRevoked = (await call('admin', 'DELETE', `/Auth/Keys/${keyToken}`)).status;
        keyToken = null;
        if (original) cleanup.policyRestored = (await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: original })).status;
        if (added) {
            cleanup.removed = await removeShared();
            await rebuild();
        }
        if (added || original) {
            const libraries = (await admin('GET', '/Library/VirtualFolders')).map((f) => f.Name).sort();
            const indexAfter = (await admin('GET', INDEX)).Libraries.map((l) => `${l.Name}:${l.Movies}`).sort();
            const viewer2 = (await admin('GET', `/Users/${users.viewer2.id}`)).Policy;
            cleanup.indexAfter = indexAfter;
            check('the sandbox is as before: its libraries, the index\'s, viewer2 a plain user, no key',
                JSON.stringify(libraries) === JSON.stringify(before.libraries) && JSON.stringify(indexAfter) === JSON.stringify(before.index)
                    && !viewer2.IsAdministrator && viewer2.EnableAllFolders === original.EnableAllFolders
                    && !((await admin('GET', '/Auth/Keys')).Items || []).some((key) => key.AppName === KEY_APP),
                `${libraries.join(', ')}; ${indexAfter.join(', ')}`);
        }
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + String(e && e.stack || e);
    }
    evidence.cleanup = cleanup;
}

const result = { pass: !failure && checks.every((c) => c.pass), failure, checks, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
