#!/usr/bin/env node
// User Ratings' details section with more than a page of other members' reviews. Node 22 built-ins only. Loopback only, never port 8096. The admin's and viewer's tokens come from the
// sandbox's credentials.json and are never printed; the members the probe makes get a random password held only in memory
// (a run that stopped half way resets it as the admin to sign in again) and are deleted at the end.
//
//   node scripts/userratings-details-probe.mjs --sandbox default [--out result.json]
//
// It seeds Long Test Movie with SEED synthetic members' ratings and reviews (users urdetails-01 ... and one named in
// Arabic letters), one after another so their times differ: a long review of nine lines (Show more), one whose text is
// "<b>…</b>" (shown as text), one whose text leaves a right-to-left override open (isolated), one marked as a spoiler, the
// rest short; then viewer's own 4.5 with a review, the newest row. Then it runs webchecks/userratings-details-reviews.json
// (signed in as viewer2) through webcheck.mjs, and deletes every row it made (as each row's
// owner) and its users. What it made is kept in details-probe/state.json until it is gone, so a run that stopped half way
// is cleaned up by the next. Nothing outside .sandbox/<name>/details-probe is written.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { PRODUCTION_PORT } from './sandbox-policy.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PROBE = 'ur-details-probe';
const MOVIE = 'Long Test Movie';
const SEED = 23;
const PREFIX = 'urdetails';
const ARABIC = PREFIX + ' سارة';

function usage() {
    return 'usage: node scripts/userratings-details-probe.mjs --sandbox <name> [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
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
const credentials = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const who of ['admin', 'viewer', 'viewer2']) {
    if (!credentials[who] || !credentials[who].token || !credentials[who].id) throw new Error(`no ${who} in credentials.json; run Initialize-Sandbox.ps1`);
}

const inside = (child, parent) => {
    const rel = path.relative(path.resolve(parent), path.resolve(child));
    return rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel);
};
const workDir = path.join(sandboxRoot, 'details-probe');
if (!inside(workDir, sandboxRoot) || /programdata/i.test(workDir)) throw new Error(`refusing to work in ${workDir}`);
const stateFile = path.join(workDir, 'state.json');

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, time: new Date().toISOString() } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

const tokens = { admin: credentials.admin.token, viewer: credentials.viewer.token, viewer2: credentials.viewer2.token };
const ids = { admin: credentials.admin.id, viewer: credentials.viewer.id, viewer2: credentials.viewer2.id };
function authorization(who) {
    const token = tokens[who];
    return `MediaBrowser Client="${PROBE}", Device="sandbox", DeviceId="${PROBE}-${encodeURIComponent(who)}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json', Authorization: authorization(who) };
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(60000) });
    const text = await res.text();
    let json = null;
    if (/json/i.test(res.headers.get('content-type') || '') && text.length) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json };
}

function loadState() {
    try { return readJson(stateFile); } catch { return { users: [], viewerRow: false }; }
}
function saveState(s) {
    fs.mkdirSync(workDir, { recursive: true });
    fs.writeFileSync(stateFile, JSON.stringify(s, null, 2));
}

async function signIn(name, secret) {
    const res = await fetch(new URL(baseUrl + '/Users/AuthenticateByName', origin), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', Authorization: `MediaBrowser Client="${PROBE}", Device="sandbox", DeviceId="${PROBE}-${encodeURIComponent(name)}", Version="1.0.0"` },
        body: JSON.stringify({ Username: name, Pw: secret }),
        signal: AbortSignal.timeout(60000)
    });
    if (res.status !== 200) throw new Error(`signing in as a probe member: ${res.status}`);
    const j = await res.json();
    tokens[name] = j.AccessToken;
    ids[name] = j.User.Id;
}

// A member of the probe's own: a random password that lives only in this process.
async function makeUser(name) {
    const secret = crypto.randomBytes(18).toString('base64url');
    const r = await call('admin', 'POST', '/Users/New', { body: { Name: name, Password: secret } });
    if (r.status !== 200) throw new Error(`POST /Users/New: ${r.status}`);
    const s = loadState();
    s.users.push({ name, id: r.json.Id });
    saveState(s);
    await signIn(name, secret);
}

// The review each synthetic member writes (the section's cases), or null for stars alone.
function reviewOf(n) {
    if (n === 1) return { Text: Array.from({ length: 9 }, (_, i) => `Line ${i + 1} of a long synthetic review, written to test Show more.`).join('\n'), IsSpoiler: false };
    if (n === 2) return { Text: '<b>Markup is only text here</b> and <i>stays</i> visible as written.', IsSpoiler: false };
    if (n === 3) return { Text: 'An override left open ‮at the end of this synthetic review', IsSpoiler: false };
    if (n === 5) return { Text: 'A synthetic spoiler: the clip ends with a test pattern.', IsSpoiler: true };
    if (n % 6 === 0) return null;
    return { Text: `Synthetic review number ${n}.`, IsSpoiler: false };
}

// Everything a run made, gone again: each row by its owner, then the users; viewer's row.
async function cleanUp(label, movieId) {
    const s = loadState();
    const outcome = { rows: [], users: [], viewer: null };
    for (const u of s.users) {
        if (!tokens[u.name]) {
            const reset = await call('admin', 'POST', '/Users/Password', { query: { userId: u.id }, body: { ResetPassword: true } });
            if (reset.status === 204) await signIn(u.name, '').catch(() => null);
        }
        if (tokens[u.name] && movieId) {
            outcome.rows.push((await call(u.name, 'DELETE', `/UserRatings/Items/${movieId}/Mine`)).status);
        }
    }
    const kept = [];
    for (const u of s.users) {
        const d = await call('admin', 'DELETE', `/Users/${u.id}`);
        outcome.users.push(d.status);
        if (d.status !== 204 && d.status !== 404) kept.push(u);
        delete tokens[u.name];
    }
    s.users = kept;
    if (s.viewerRow && movieId) {
        outcome.viewer = (await call('viewer', 'DELETE', `/UserRatings/Items/${movieId}/Mine`)).status;
        if (outcome.viewer === 204 || outcome.viewer === 404) s.viewerRow = false;
    }
    saveState(s);
    evidence[`cleanUp-${label}`] = outcome;
    return outcome;
}

let movieId = null;
const started = Date.now();
try {
    const diagnostics = await call('admin', 'GET', '/UserRatings/Admin/Diagnostics');
    check('User Ratings 0.3.5 or later is loaded, its database Ok', diagnostics.status === 200 && /^(0\.3\.([5-9]|\d\d)|[1-9]\d*\.\d+\.\d+)\./.test(diagnostics.json.PluginVersion) && diagnostics.json.Store && diagnostics.json.Store.Status === 'Ok',
        { status: diagnostics.status, version: diagnostics.json && diagnostics.json.PluginVersion });
    const found = await call('admin', 'GET', '/Items', { query: { userId: ids.admin, IncludeItemTypes: 'Movie', Recursive: 'true', SearchTerm: MOVIE, Limit: 1 } });
    movieId = found.json && found.json.Items && found.json.Items[0] ? found.json.Items[0].Id.replace(/-/g, '') : null;
    if (!movieId) throw new Error(`no "${MOVIE}" in the sandbox`);
    evidence.movie = movieId;
    const leftovers = loadState();
    if (leftovers.users.length || leftovers.viewerRow) await cleanUp('leftovers', movieId);
    const before = await call('viewer2', 'GET', `/UserRatings/Items/${movieId}`);
    check(`no member has rated ${MOVIE} as the run starts (another run left nothing)`, before.status === 200 && before.json.Summary.Count === 0 && before.json.Reviews.TotalRecordCount === 0,
        { status: before.status, count: before.json && before.json.Summary && before.json.Summary.Count });

    // ---- the seed: SEED members, one after another, then viewer (the newest row)
    const written = [];
    for (let n = 1; n <= SEED; n++) {
        const name = n === 4 ? ARABIC : `${PREFIX}-${String(n).padStart(2, '0')}`;
        await makeUser(name);
        const stars = ((n % 10) + 1) / 2;
        const r = await call(name, 'PUT', `/UserRatings/Items/${movieId}/Mine/Rating`, { body: { Stars: stars } });
        const review = reviewOf(n);
        const t = review ? await call(name, 'PUT', `/UserRatings/Items/${movieId}/Mine/Review`, { body: review }) : { status: 200 };
        written.push({ n, stars, review: !!review, rating: r.status, text: t.status });
        await delay(15);
    }
    const s = loadState();
    s.viewerRow = true;
    saveState(s);
    const viewerRating = await call('viewer', 'PUT', `/UserRatings/Items/${movieId}/Mine/Rating`, { body: { Stars: 4.5 } });
    const viewerReview = await call('viewer', 'PUT', `/UserRatings/Items/${movieId}/Mine/Review`, { body: { Text: 'Viewer\'s own synthetic review, the newest row.', IsSpoiler: false } });
    evidence.seed = { members: written.length, statuses: [...new Set(written.map((w) => w.rating + '/' + w.text))], viewer: [viewerRating.status, viewerReview.status] };
    check(`the seed: ${SEED} members' ratings (and their reviews) and viewer's 4.5 with a review, all 200`, written.every((w) => w.rating === 200 && w.text === 200) && viewerRating.status === 200 && viewerReview.status === 200, evidence.seed);
    const seeded = await call('viewer2', 'GET', `/UserRatings/Items/${movieId}`);
    evidence.seeded = { count: seeded.json.Summary.Count, average: seeded.json.Summary.Average, reviews: seeded.json.Summary.ReviewCount, others: seeded.json.Reviews.TotalRecordCount };
    check('viewer2 reads them: more than a page of other members\' rows', seeded.status === 200 && seeded.json.Reviews.TotalRecordCount === SEED + 1 && seeded.json.Summary.Count === SEED + 1, evidence.seeded);

    // ---- the webcheck, as viewer2
    const out = path.join(workDir, 'details-reviews.json');
    const run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', path.join(repoRoot, 'scripts', 'webchecks', 'userratings-details-reviews.json'), '--out', out], { encoding: 'utf8', timeout: 900000, maxBuffer: 64 * 1024 * 1024 });
    let result = null;
    try { result = readJson(out); } catch { result = null; }
    const steps = result && result.steps ? result.steps : [];
    const failed = steps.find((st) => !st.ok && !st.optional);
    evidence.webcheck = { exit: run.status, steps: steps.length, ok: steps.filter((st) => st.ok).length, failed: failed ? { name: failed.name, error: failed.error } : null, out };
    check(`webchecks/userratings-details-reviews.json passed as viewer2 (${evidence.webcheck.ok} of ${steps.length} steps)`, run.status === 0 && !!result && result.ok === true && !failed, evidence.webcheck);
} catch (e) {
    check('probe ran', false, String(e && e.stack ? e.stack.split('\n').slice(0, 4).join(' | ') : e));
} finally {
    try {
        const outcome = await cleanUp('end', movieId);
        const s = loadState();
        const after = movieId ? await call('viewer2', 'GET', `/UserRatings/Items/${movieId}`) : null;
        check('the end: the probe\'s rows and members are gone, no rating left on the movie', s.users.length === 0 && !s.viewerRow && (!after || (after.status === 200 && after.json.Summary.Count === 0 && after.json.Reviews.TotalRecordCount === 0)),
            { outcome, left: after && after.json && after.json.Summary ? after.json.Summary.Count : null });
    } catch (e) {
        check('the end: the probe\'s rows and members are gone', false, String(e.message));
    }
}

evidence.seconds = Math.round((Date.now() - started) / 1000);
const failedChecks = checks.filter((c) => !c.pass);
const result = { pass: failedChecks.length === 0, checks, evidence };
if (opts.out) {
    fs.mkdirSync(path.dirname(path.resolve(opts.out)), { recursive: true });
    fs.writeFileSync(opts.out, JSON.stringify(result, null, 2));
}
for (const c of checks) console.log(`${c.pass ? 'PASS' : 'FAIL'} ${c.name}${c.pass ? '' : ` (${JSON.stringify(c.detail)})`}`);
console.log(`${checks.length - failedChecks.length} of ${checks.length} passed`);
process.exit(failedChecks.length === 0 ? 0 : 1);
