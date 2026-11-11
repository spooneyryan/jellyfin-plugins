#!/usr/bin/env node
// Library Maintenance's duplicate groups against a running sandbox: GET /LibraryMaintenance/Duplicates/Groups
// and …/Groups/{groupId}, the index's status, and Diagnostics' DuplicateGroups entry. Node 22 built-ins only. Loopback
// only, never port 8096. The tokens come from credentials.json and are never printed. It changes nothing but the index:
// its one POST is Library Maintenance's own Rebuild (in memory), which clears the groups' cache between timing rounds.
//
//   node scripts/librarymaintenance-groups-probe.mjs --sandbox default [--rounds 3] [--out result.json]
//
// Checks: anonymous 401 and a viewer 403; the default page (Title + Year): every group of two or more entries, a 16-hex id,
// the keep first, the effective libraries within the index's, the totals with no filter; Title mode's count is the
// default's AlternateModeGroupCount and the other way round; one group by its id, 404 for an id that is none, 400 for a
// bad option and for an option given twice; the paging; the cache (the same request again groups nothing; after a
// Rebuild it groups again); and the target of grouping in under 100 ms after warm-up: every grouping after the first
// request's, from Diagnostics' DuplicateGroups.Recent (the title keys' projection included when the snapshot is new),
// and each round's three requests timed from here: cold (a new snapshot: the projection and the grouping), uncached
// (the projection kept, other options) and cached (the same request again). It prints one JSON result (pass, checks,
// evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const PRODUCTION_PORT = 8096;
const GROUPS = '/LibraryMaintenance/Duplicates/Groups';
const INDEX = '/LibraryMaintenance/Duplicates/Index';
const REBUILD = '/LibraryMaintenance/Duplicates/Index/Rebuild';
const DIAGNOSTICS = '/LibraryMaintenance/Admin/Diagnostics';
const BUDGET_MS = 100;
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

function usage() {
    return 'Usage: node scripts/librarymaintenance-groups-probe.mjs --sandbox <name> [--rounds <1-10>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, rounds: 3, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--rounds') o.rounds = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!Number.isInteger(o.rounds) || o.rounds < 1 || o.rounds > 10) throw new Error(usage());
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
for (const who of ['admin', 'viewer']) {
    if (!users[who] || !users[who].token) throw new Error(`no ${who} in credentials.json; run Initialize-Sandbox.ps1`);
}

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, requests: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

async function call(who, method, p, query) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="librarymaintenance-groups-probe", Device="sandbox", DeviceId="librarymaintenance-groups-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    const started = performance.now();
    const res = await fetch(url, { method, headers: h, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    const ms = Math.round((performance.now() - started) * 10) / 10;
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    if (p.startsWith(GROUPS)) evidence.requests.push({ who, path: p.replace(/[0-9a-f]{16}$/, '{id}'), query: query || {}, status: res.status, ms });
    return { status: res.status, json, text };
}

async function admin(method, p, query) {
    const r = await call('admin', method, p, query);
    if (r.status < 200 || r.status >= 300) throw new Error(`${method} ${p}: ${r.status} ${r.text.slice(0, 300)}`);
    return r;
}

const groupsDiagnostics = async () => (await admin('GET', DIAGNOSTICS)).json.DuplicateGroups;

// A page, after the first build if the server has none yet (202).
async function page(query) {
    for (let i = 0; i < 120; i++) {
        const r = await call('admin', 'GET', GROUPS, query);
        if (r.status === 200) return r.json;
        if (r.status !== 202) throw new Error(`GET ${GROUPS}: ${r.status} ${r.text.slice(0, 300)}`);
        await delay(500);
    }
    throw new Error('no index after 60 s');
}

function pageProblems(body, indexLibraries) {
    const problems = [];
    const ids = new Set();
    for (const g of body.Items) {
        if (!/^[0-9a-f]{16}$/.test(g.GroupId)) problems.push(`${g.Key}: id ${g.GroupId}`);
        if (ids.has(g.GroupId)) problems.push(`${g.Key}: id twice`);
        ids.add(g.GroupId);
        if (!Array.isArray(g.Entries) || g.Entries.length < 2) problems.push(`${g.Key}: ${g.Entries?.length} entries`);
        else if (g.SuggestedKeepId !== g.Entries[0].ItemId) problems.push(`${g.Key}: the keep isn't first`);
        if (!['Confirmed', 'Likely', 'Possible', 'Conflict'].includes(g.Confidence)) problems.push(`${g.Key}: confidence ${g.Confidence}`);
    }
    for (const id of body.EffectiveLibraryIds) if (!indexLibraries.includes(id)) problems.push(`library ${id} isn't the index's`);
    return problems;
}

let failure = null;
try {
    check('anonymous is 401', (await call('anonymous', 'GET', GROUPS)).status === 401);
    check('a viewer is 403', (await call('viewer', 'GET', GROUPS)).status === 403);

    // The first request: the warm-up (the build, if none, and the grouper's first run). Then the index it read.
    const byYear = await page();
    const warmGroupings = (await groupsDiagnostics()).Groupings;
    const index = (await admin('GET', INDEX)).json;
    const indexLibraries = (index.Libraries || []).map((l) => l.Id);
    evidence.index = { Version: index.Version, Movies: index.Movies, Entries: index.Entries, Libraries: (index.Libraries || []).map((l) => l.Name) };
    evidence.defaultPage = { Mode: byYear.Mode, UnknownYear: byYear.UnknownYear, GroupCount: byYear.GroupCount, AlternateModeGroupCount: byYear.AlternateModeGroupCount, TotalEntries: byYear.TotalEntries, TotalMovies: byYear.TotalMovies, ReclaimableBytes: byYear.ReclaimableBytes, Items: byYear.Items.length, Index: byYear.Index };
    const problems = pageProblems(byYear, indexLibraries);
    check('the default page is Title + Year, MatchUnknownOnly', byYear.Mode === 'TitleAndYear' && byYear.UnknownYear === 'MatchUnknownOnly', `${byYear.Mode}, ${byYear.UnknownYear}`);
    check('every group: 2+ entries, a 16-hex id, the keep first; libraries within the index\'s', problems.length === 0, problems.slice(0, 5).join('; ') || `${byYear.Items.length} groups`);
    check('with no filter the totals agree', byYear.TotalRecordCount === byYear.GroupCount && byYear.Items.length === Math.min(50, byYear.GroupCount), `${byYear.TotalRecordCount}/${byYear.GroupCount}/${byYear.Items.length}`);

    const title = await page({ mode: 'Title' });
    check('Title mode: its count is the default\'s alternate count, and back', title.GroupCount === byYear.AlternateModeGroupCount && title.AlternateModeGroupCount === byYear.GroupCount, `${title.GroupCount}/${byYear.AlternateModeGroupCount}, ${title.AlternateModeGroupCount}/${byYear.GroupCount}`);
    check('Title mode: every group is valid', pageProblems(title, indexLibraries).length === 0);

    if (byYear.Items.length > 0) {
        const first = byYear.Items[0];
        const one = await call('admin', 'GET', `${GROUPS}/${first.GroupId}`);
        check('a group by its id', one.status === 200 && one.json?.GroupId === first.GroupId && one.json?.Entries?.length === first.Entries.length, `${one.status}`);
    }
    check('an id that is no group is 404', (await call('admin', 'GET', `${GROUPS}/0000000000000000`)).status === 404);
    const bad = await call('admin', 'GET', GROUPS, { mode: 'Fuzzy' });
    check('a bad option is 400 and says which', bad.status === 400 && /mode must be/.test(bad.text), `${bad.status}`);
    check('a page over 200 is 400', (await call('admin', 'GET', GROUPS, { limit: 201 })).status === 400);
    const twice = await call('admin', 'GET', `${GROUPS}?mode=Title&mode=TitleAndYear`);
    check('an option given twice is 400 and says which', twice.status === 400 && /mode.{0,8} was given more than once/.test(twice.text), `${twice.status}`);
    if (byYear.GroupCount >= 2) {
        const second = await page({ startIndex: 1, limit: 1 });
        check('the paging', second.Items.length === 1 && second.Items[0].GroupId === byYear.Items[1].GroupId && second.TotalRecordCount === byYear.GroupCount);
    }

    // One library by its id: only it, and only groups with an entry in it.
    if (indexLibraries.length > 0) {
        const one = await page({ libraryIds: indexLibraries[0], limit: 200 });
        const outside = one.Items.filter((g) => !g.Entries.some((e) => (e.Libraries || []).some((l) => l.Id === indexLibraries[0])));
        check('one library by its id', one.EffectiveLibraryIds.length === 1 && one.EffectiveLibraryIds[0] === indexLibraries[0] && outside.length === 0 && one.GroupCount <= byYear.GroupCount, `${one.GroupCount} groups, ${outside.length} outside`);
    }

    // The cache: the same request groups nothing; a Rebuild (a newer snapshot) clears it.
    const before = await groupsDiagnostics();
    await page();
    await page({ mode: 'Title' });
    const after = await groupsDiagnostics();
    check('the same requests again group nothing (the cache)', after.Groupings === before.Groupings, `${before.Groupings} → ${after.Groupings}, hits ${before.CacheHits} → ${after.CacheHits}`);

    // The timing rounds: each a Rebuild, then the default page (cold: the new snapshot's projection and the grouping),
    // MatchAny (uncached: the projection kept) and the default page again (cached). One grouping per uncached request
    // (the other mode's count comes from the same buckets).
    const timings = { cold: [], uncached: [], cached: [] };
    const lastMs = () => evidence.requests[evidence.requests.length - 1].ms;
    for (let round = 0; round < opts.rounds; round++) {
        const version = (await admin('GET', INDEX)).json.Version;
        await admin('POST', REBUILD);
        for (let i = 0; i < 240; i++) {
            const status = (await admin('GET', INDEX)).json;
            if (status.Version > version && !status.Rebuilding) break;
            await delay(250);
        }

        const start = await groupsDiagnostics();
        await page();
        timings.cold.push(lastMs());
        await page({ unknownYear: 'MatchAny' });
        timings.uncached.push(lastMs());
        await page();
        timings.cached.push(lastMs());
        const end = await groupsDiagnostics();
        if (round === 0) check('after a Rebuild the same options group again, once each, and the repeat is cached', end.Groupings - start.Groupings === 2, `${end.Groupings - start.Groupings} groupings`);
    }

    const last = await groupsDiagnostics();
    const fresh = Math.min(16, last.Groupings - warmGroupings);
    const recent = fresh > 0 ? (last.Recent || []).slice(-fresh) : [];
    const times = recent.map((r) => r.Ms).sort((a, b) => a - b);
    const entries = recent.length ? Math.max(...recent.map((r) => r.Entries)) : null;
    evidence.groupings = { count: times.length, entries, best: times[0], median: times[Math.floor(times.length / 2)], worst: times[times.length - 1], recent };
    const stats = (list) => {
        const sorted = [...list].sort((a, b) => a - b);
        return { median: sorted[Math.floor(sorted.length / 2)], slowest: sorted[sorted.length - 1], all: list };
    };
    evidence.requestMs = { cold: stats(timings.cold), uncached: stats(timings.uncached), cached: stats(timings.cached) };
    check(`every grouping after the warm-up under ${BUDGET_MS} ms`, times.length >= 2 && times[times.length - 1] < BUDGET_MS, `${times.length} groupings of up to ${entries} entries: best ${times[0]} ms, worst ${times[times.length - 1]} ms`);
} catch (e) {
    failure = String(e && e.stack || e);
}

const result = { pass: !failure && checks.every((c) => c.pass), failure, checks, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
