#!/usr/bin/env node
// Tag Cloud end to end in a sandbox, never the real server:
//   node scripts/tagcloud-probe.mjs --sandbox <name> [--keep-policy] [--out <file>]
// 1. Seeds the sandbox's movies and series with tags, genres, studios and credits through its own API (admin), the same
//    every run, and restricts viewer2 to one library with a blocked tag (put back at the end unless --keep-policy).
// 2. Rebuilds the index and checks which people path it took (the grouped database read should run on 10.11.5)
//    and how long the build took.
// 3. For admin, viewer and viewer2, compares every word count of every source with core's own /Items as that user
//    (the counts cover exactly what the user may see), then the library picker, narrowing and a word's titles.
//    Then blocks one more tag for viewer2 and checks its next cloud follows with no rebuild.
// 4. Patches the index through the library-change monitor (one title's tags changed, waiting out the 60 s batch).
// 5. Saved clouds: private, shared, who sees and may change which, conflicts, duplicates, deletes.
// Writes .sandbox/<name>/tagcloud-probe/result.json and exits 1 on any failed check.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PROBE_TAG = 'tc-probe';
const BLOCKED_TAG = 'tc-blocked';
const SOURCES = ['Tags', 'Genres', 'Studios', 'Actors', 'Directors', 'Writers'];
const TAG_POOL = ['space', 'Horror', 'heist', 'noir', 'robot', 'zombie', 'road trip', 'time travel', 'Los Angeles, California', 'creature'];
const GENRE_POOL = ['Drama', 'Comedy', 'Science Fiction', 'Crime', 'Thriller', 'Animation'];
const STUDIO_POOL = ['Probe Pictures', 'Sandbox Studios', 'Loopback Films', 'Port 8111 Productions'];
const ACTOR_POOL = ['Ada Actor', 'Ben Bit', 'Cleo Cache', 'Dan Delta', 'Eve Echo', 'Finn Frame', 'Gia Gamma', 'Hal Hash'];
const DIRECTOR_POOL = ['Dora Director', 'Del Direct', 'Dee Dolly'];
const WRITER_POOL = ['Wren Writer', 'Walt Words'];

function usage() {
    return 'usage: node scripts/tagcloud-probe.mjs --sandbox <name> [--keep-policy] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, keepPolicy: false, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--keep-policy') o.keepPolicy = true;
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const workRoot = path.join(sandboxRoot, 'tagcloud-probe');
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
const pluginGuid = (/^guid:\s*"?([0-9a-fA-F-]{36})"?/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'TagCloud', 'build.yaml'), 'utf8')) || [])[1];
if (!pluginGuid) throw new Error('no guid in plugins/TagCloud/build.yaml');

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    console.log((pass ? 'ok   ' : 'FAIL ') + name + (detail !== undefined && !pass ? ' :: ' + JSON.stringify(detail).slice(0, 600) : ''));
    return !!pass;
}

const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);

async function call(who, method, p, { body, query } = {}) {
    let url = baseUrl + p;
    if (query) {
        const parts = [];
        for (const [k, v] of Object.entries(query)) {
            for (const one of [].concat(v)) if (one !== undefined && one !== null && one !== '') parts.push(encodeURIComponent(k) + '=' + encodeURIComponent(String(one)));
        }
        if (parts.length) url += '?' + parts.join('&');
    }
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="tagcloud-probe", Device="sandbox", DeviceId="tagcloud-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(new URL(url, origin), { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text };
}

async function ok(who, method, p, options) {
    const r = await call(who, method, p, options);
    if (r.status < 200 || r.status > 299) throw new Error(`${method} ${p} as ${who}: ${r.status} ${r.text.slice(0, 300)}`);
    return r.json;
}

// Every movie and series a user may see, with the fields the clouds count (core's own answer, as that user).
async function coreTitles(who, kinds) {
    const r = await ok(who, 'GET', `/Users/${users[who].id}/Items`, {
        query: { Recursive: 'true', IncludeItemTypes: kinds, Fields: 'Tags,Genres,Studios,People', EnableImages: 'false', EnableUserData: 'false' }
    });
    return r.Items || [];
}

function wordsOf(item, source) {
    switch (source) {
        case 'Tags': return item.Tags || [];
        case 'Genres': return item.Genres || [];
        case 'Studios': return (item.Studios || []).map((s) => s.Name);
        case 'Actors': return (item.People || []).filter((p) => p.Type === 'Actor').map((p) => p.Name);
        case 'Directors': return (item.People || []).filter((p) => p.Type === 'Director').map((p) => p.Name);
        default: return (item.People || []).filter((p) => p.Type === 'Writer').map((p) => p.Name);
    }
}

// Counts the way the index keys words: trimmed, case ignored, once per title.
function countWords(items, source) {
    const counts = new Map();
    for (const item of items) {
        const keys = new Set(wordsOf(item, source).map((w) => String(w).trim().toLowerCase()).filter(Boolean));
        for (const key of keys) counts.set(key, (counts.get(key) || 0) + 1);
    }
    return counts;
}

function mapOf(words) {
    return new Map((words || []).map((w) => [w.Key, w.Count]));
}

function diff(expected, actual) {
    const out = [];
    for (const [k, v] of expected) if (actual.get(k) !== v) out.push({ key: k, expected: v, actual: actual.get(k) });
    for (const [k, v] of actual) if (!expected.has(k)) out.push({ key: k, expected: undefined, actual: v });
    return out;
}

async function waitForIndex(after, timeoutMs) {
    const start = Date.now();
    for (;;) {
        const status = await ok('admin', 'GET', '/TagCloud/Admin/Status');
        if (status.State === 'Ready' && status.Generation > after) return status;
        if (status.State === 'Failed') throw new Error('index build failed: ' + status.LastError);
        if (Date.now() - start > timeoutMs) throw new Error('index not rebuilt in time: ' + JSON.stringify(status));
        await delay(500);
    }
}

// ---- 1. seed ------------------------------------------------------------------------------------------------------

async function seed() {
    const items = (await coreTitles('admin', 'Movie,Series')).sort((a, b) => a.Name.localeCompare(b.Name));
    const libraries = await ok('admin', 'GET', '/Library/VirtualFolders');
    evidence.libraries = libraries.map((l) => ({ name: l.Name, id: guid(l.ItemId) }));
    let n = 0;
    for (const [i, listed] of items.entries()) {
        const item = await ok('admin', 'GET', `/Users/${users.admin.id}/Items/${listed.Id}`);
        const pick = (pool, count, offset) => Array.from({ length: count }, (_, k) => pool[(i * 3 + k * 7 + (offset || 0)) % pool.length]);
        const tags = [...new Set([PROBE_TAG, ...pick(TAG_POOL, 1 + (i % 3)), i % 2 ? 'SPACE' : null].filter(Boolean))];
        if (i === 1) tags.push(BLOCKED_TAG);
        item.Tags = tags;
        item.Genres = [...new Set(pick(GENRE_POOL, 1 + (i % 2), 1))];
        item.Studios = [...new Set(pick(STUDIO_POOL, 1, 2))].map((name) => ({ Name: name }));
        item.People = [
            ...[...new Set(pick(ACTOR_POOL, 2 + (i % 2), 3))].map((name) => ({ Name: name, Type: 'Actor' })),
            { Name: DIRECTOR_POOL[i % DIRECTOR_POOL.length], Type: 'Director' },
            { Name: WRITER_POOL[i % WRITER_POOL.length], Type: 'Writer' },
            { Name: 'Pat Producer', Type: 'Producer' }
        ];
        await ok('admin', 'POST', `/Items/${listed.Id}`, { body: item });
        n++;
    }
    evidence.seeded = n;
    check('seeded the movies and series', n >= 4, { n });
    return { items, libraries };
}

// ---- viewer2: one library and a blocked tag -----------------------------------------------------------------------

async function restrictViewer2(libraries) {
    const user = await ok('admin', 'GET', `/Users/${users.viewer2.id}`);
    fs.mkdirSync(workRoot, { recursive: true });
    const saved = path.join(workRoot, 'viewer2-policy.json');
    if (!fs.existsSync(saved)) fs.writeFileSync(saved, JSON.stringify(user.Policy, null, 2));
    const movies = libraries.find((l) => l.Name === 'Movies');
    const policy = Object.assign({}, user.Policy, { EnableAllFolders: false, EnabledFolders: [movies.ItemId], BlockedTags: [BLOCKED_TAG] });
    await ok('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: policy });
    return movies;
}

async function putBackViewer2() {
    const saved = path.join(workRoot, 'viewer2-policy.json');
    if (!fs.existsSync(saved)) return;
    await ok('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: readJson(saved) });
    fs.rmSync(saved);
}

// ---- 3. counts against core -----------------------------------------------------------------------------------------

async function compareCounts(who) {
    for (const [scope, kinds] of [['Movies', 'Movie'], ['Shows', 'Series'], ['Both', 'Movie,Series']]) {
        const items = await coreTitles(who, kinds);
        for (const source of SOURCES) {
            const words = await ok(who, 'GET', '/TagCloud/Words', { query: { source, scope, limit: 1000 } });
            const expected = countWords(items, source);
            const problems = diff(expected, mapOf(words.Words));
            check(`${who}: ${source} over ${scope} equal core's counts (${expected.size} words, ${items.length} titles)`, problems.length === 0 && words.TitleCount === items.length, { problems: problems.slice(0, 8), titleCount: words.TitleCount, core: items.length });
        }
    }
}

async function main() {
    const { libraries } = await seed();
    const movies = await restrictViewer2(libraries);
    try {
        // ---- 2. rebuild, and the people path
        const before = await ok('admin', 'GET', '/TagCloud/Admin/Status');
        const t0 = Date.now();
        await ok('admin', 'POST', '/TagCloud/Admin/Rebuild');
        const status = await waitForIndex(before.Generation, 120000);
        evidence.status = status;
        evidence.rebuildWallMs = Date.now() - t0;
        check('the people are read in one grouped database read', status.PeoplePath === 'Database', status);
        check('the index holds every title', status.Titles >= evidence.seeded, status);

        // ---- 3. counts as each user
        for (const who of ['admin', 'viewer', 'viewer2']) await compareCounts(who);

        const libs = await ok('viewer2', 'GET', '/TagCloud/Libraries', { query: { scope: 'Both' } });
        check('viewer2 picks from Movies only', libs.length === 1 && guid(libs[0].Id) === guid(movies.ItemId), libs);
        const adminLibs = await ok('admin', 'GET', '/TagCloud/Libraries', { query: { scope: 'Both' } });
        check('admin picks from every library with titles', adminLibs.length >= 2, adminLibs.map((l) => l.Name));

        // A library filter as admin equals core's ParentId query.
        const inMovies = (await ok('admin', 'GET', `/Users/${users.admin.id}/Items`, { query: { Recursive: 'true', IncludeItemTypes: 'Movie,Series', ParentId: movies.ItemId, Fields: 'Tags', EnableImages: 'false' } })).Items;
        const filtered = await ok('admin', 'GET', '/TagCloud/Words', { query: { source: 'Tags', scope: 'Both', libraryIds: guid(movies.ItemId), limit: 1000 } });
        check('a library filter counts that library only', diff(countWords(inMovies, 'Tags'), mapOf(filtered.Words)).length === 0, diff(countWords(inMovies, 'Tags'), mapOf(filtered.Words)).slice(0, 5));

        // Narrowing: the titles carrying both words, every word recounted over them.
        const all = await coreTitles('admin', 'Movie,Series');
        const both = all.filter((t) => {
            const keys = (t.Tags || []).map((x) => x.toLowerCase());
            return keys.includes('space') && keys.includes(PROBE_TAG);
        });
        const narrowed = await ok('admin', 'GET', '/TagCloud/Words', { query: { source: 'Tags', scope: 'Both', narrow: ['Space', PROBE_TAG], limit: 1000 } });
        check('narrowing by two words counts the titles carrying both', narrowed.TitleCount === both.length && diff(countWords(both, 'Tags'), mapOf(narrowed.Words)).length === 0, { title: narrowed.TitleCount, core: both.length });
        const hidden = await ok('viewer2', 'GET', '/TagCloud/Words', { query: { source: 'Tags', scope: 'Both', narrow: [BLOCKED_TAG] } });
        check('a word of titles viewer2 may not see narrows nothing', hidden.Narrow.length === 0 && !hidden.Words.some((w) => w.Key === BLOCKED_TAG), hidden.Narrow);

        // A word's titles.
        const spaceTitles = await ok('admin', 'GET', '/TagCloud/Titles', { query: { source: 'Tags', scope: 'Both', key: 'space', limit: 200 } });
        const coreSpace = all.filter((t) => (t.Tags || []).some((x) => x.toLowerCase() === 'space')).map((t) => guid(t.Id)).sort();
        check("a word's titles are core's", JSON.stringify(spaceTitles.Items.map((t) => guid(t.Id)).sort()) === JSON.stringify(coreSpace) && spaceTitles.Total === coreSpace.length, { ours: spaceTitles.Total, core: coreSpace.length });
        const blockedTitles = await ok('viewer2', 'GET', '/TagCloud/Titles', { query: { source: 'Tags', scope: 'Both', key: BLOCKED_TAG } });
        check('a blocked word has no titles for viewer2 (an empty page, not a 404)', blockedTitles.Total === 0, blockedTitles);

        // ---- a policy change after the index is built: the next request follows it, with no rebuild
        const policyGeneration = (await ok('admin', 'GET', '/TagCloud/Admin/Status')).Generation;
        const beforeBlock = await ok('viewer2', 'GET', '/TagCloud/Words', { query: { source: 'Tags', scope: 'Both', limit: 1000 } });
        const viewer2Policy = (await ok('admin', 'GET', `/Users/${users.viewer2.id}`)).Policy;
        await ok('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: Object.assign({}, viewer2Policy, { BlockedTags: [BLOCKED_TAG, PROBE_TAG] }) });
        try {
            const afterBlock = await ok('viewer2', 'GET', '/TagCloud/Words', { query: { source: 'Tags', scope: 'Both', limit: 1000 } });
            const coreAfter = await coreTitles('viewer2', 'Movie,Series');
            const sameIndex = (await ok('admin', 'GET', '/TagCloud/Admin/Status')).Generation === policyGeneration;
            check("a policy change applies at viewer2's next request, with the same index",
                sameIndex && (mapOf(beforeBlock.Words).get(PROBE_TAG) || 0) > 0 && !mapOf(afterBlock.Words).has(PROBE_TAG)
                    && afterBlock.TitleCount === coreAfter.length && afterBlock.TitleCount < beforeBlock.TitleCount
                    && diff(countWords(coreAfter, 'Tags'), mapOf(afterBlock.Words)).length === 0,
                { sameIndex, before: beforeBlock.TitleCount, after: afterBlock.TitleCount, core: coreAfter.length });
        } finally {
            await ok('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: viewer2Policy });
        }
        const restored = await ok('viewer2', 'GET', '/TagCloud/Words', { query: { source: 'Tags', scope: 'Both', limit: 1000 } });
        check("putting viewer2's policy back brings the counts back", restored.TitleCount === beforeBlock.TitleCount, { before: beforeBlock.TitleCount, restored: restored.TitleCount });

        // ---- source turned off
        const config = await ok('admin', 'GET', `/Plugins/${pluginGuid}/Configuration`);
        await ok('admin', 'POST', `/Plugins/${pluginGuid}/Configuration`, { body: Object.assign({}, config, { EnableStudios: false }) });
        const off = await call('viewer', 'GET', '/TagCloud/Words', { query: { source: 'Studios' } });
        const caps = await ok('viewer', 'GET', '/TagCloud/Capabilities');
        await ok('admin', 'POST', `/Plugins/${pluginGuid}/Configuration`, { body: config });
        check('a cloud turned off answers 403 feature-disabled and leaves the capabilities', off.status === 403 && /feature-disabled/.test(off.json && off.json.type) && !caps.Sources.includes('Studios'), { status: off.status, type: off.json && off.json.type, sources: caps.Sources });
        const anonymous = await call('anonymous', 'GET', '/TagCloud/Words', { query: { source: 'Tags' } });
        check('the words need a signed-in user', anonymous.status === 401, anonymous.status);

        // ---- 4. a patch through the library-change monitor
        const first = all.find((t) => t.Type === 'Movie');
        const item = await ok('admin', 'GET', `/Users/${users.admin.id}/Items/${first.Id}`);
        const generation = (await ok('admin', 'GET', '/TagCloud/Admin/Status')).Generation;
        item.Tags = [...(item.Tags || []), 'patched-by-probe'];
        await ok('admin', 'POST', `/Items/${first.Id}`, { body: item });
        const t1 = Date.now();
        const patched = await waitForIndex(generation, 11 * 60 * 1000);
        const after = await ok('admin', 'GET', '/TagCloud/Words', { query: { source: 'Tags', scope: 'Both', limit: 1000 } });
        evidence.patchSeconds = Math.round((Date.now() - t1) / 1000);
        check('an edited title is patched into the index by the library-change monitor', mapOf(after.Words).get('patched-by-probe') === 1 && patched.Patched >= 1, { seconds: evidence.patchSeconds, patched: patched.Patched });
        item.Tags = item.Tags.filter((t) => t !== 'patched-by-probe');
        await ok('admin', 'POST', `/Items/${first.Id}`, { body: item });

        // ---- 5. saved clouds
        await savedClouds();
    } finally {
        if (!opts.keepPolicy) await putBackViewer2();
    }
}

async function savedClouds() {
    // Clean up the probe's own clouds of an earlier run.
    for (const who of ['viewer', 'viewer2']) {
        for (const c of await ok(who, 'GET', '/TagCloud/Clouds')) if (c.IsMine && /^Probe /.test(c.Name)) await ok(who, 'DELETE', `/TagCloud/Clouds/${c.Id}`);
    }

    const request = (name, shared) => ({ Name: name, Shared: shared, Source: 'Tags', Scope: 'Both', LibraryIds: [], NarrowKeys: ['space'], Look: { MaxWords: 250, Palette: 'Rainbow', SizeMixing: 0.5, Arrangement: 'Packed' } });
    const mine = await ok('viewer', 'POST', '/TagCloud/Clouds', { body: request('Probe private', false) });
    const shared = await ok('viewer', 'POST', '/TagCloud/Clouds', { body: request('Probe shared', true) });
    check('a saved cloud keeps its look', mine.Look.MaxWords === 250 && mine.Look.Palette === 'Rainbow' && mine.Look.SizeMixing === 0.5, mine.Look);

    const theirs = await ok('viewer2', 'GET', '/TagCloud/Clouds');
    check('another user sees the shared cloud only', theirs.some((c) => c.Id === shared.Id && !c.IsMine && c.OwnerName === 'viewer') && !theirs.some((c) => c.Id === mine.Id), theirs.map((c) => c.Name));
    check("another user can't open the private cloud", (await call('viewer2', 'GET', `/TagCloud/Clouds/${mine.Id}`)).status === 404);
    check("another user can't change the shared cloud", (await call('viewer2', 'PUT', `/TagCloud/Clouds/${shared.Id}`, { body: request('Taken', true) })).status === 403);
    check('the same name twice is a 409', (await call('viewer', 'POST', '/TagCloud/Clouds', { body: request('PROBE PRIVATE', false) })).status === 409);
    const stale = Object.assign(request('Probe private renamed', false), { IfUpdatedUtc: '2000-01-01T00:00:00Z' });
    check('a save over a newer one is a 409', (await call('viewer', 'PUT', `/TagCloud/Clouds/${mine.Id}`, { body: stale })).status === 409);
    const renamed = await ok('viewer', 'PUT', `/TagCloud/Clouds/${mine.Id}`, { body: Object.assign(request('Probe private renamed', false), { IfUpdatedUtc: mine.UpdatedUtc }) });
    check('a save with the time last read goes through', renamed.Name === 'Probe private renamed');
    const copy = await ok('viewer2', 'POST', `/TagCloud/Clouds/${shared.Id}/Duplicate`);
    check('a duplicate is a private copy of the caller', copy.IsMine && !copy.Shared && copy.Name === 'Probe shared (copy)', copy);
    check("a user can't delete another's shared cloud", (await call('viewer2', 'DELETE', `/TagCloud/Clouds/${shared.Id}`)).status === 403);
    check("an admin can delete a shared cloud", (await call('admin', 'DELETE', `/TagCloud/Clouds/${shared.Id}`)).status === 204);
    await ok('viewer', 'DELETE', `/TagCloud/Clouds/${mine.Id}`);
    await ok('viewer2', 'DELETE', `/TagCloud/Clouds/${copy.Id}`);
    check('the probe left no cloud behind', (await ok('viewer', 'GET', '/TagCloud/Clouds')).length === 0 && (await ok('viewer2', 'GET', '/TagCloud/Clouds')).length === 0);
}

let failure = null;
try {
    await main();
} catch (e) {
    failure = e;
    check('the probe ran to the end', false, e.message);
}

fs.mkdirSync(workRoot, { recursive: true });
const out = opts.out ? path.resolve(opts.out) : path.join(workRoot, 'result.json');
fs.writeFileSync(out, JSON.stringify({ checks, evidence }, null, 2));
const failed = checks.filter((c) => !c.pass).length;
console.log(`${checks.length - failed} of ${checks.length} checks passed; result: ${out}`);
process.exit(failed || failure ? 1 : 0);
