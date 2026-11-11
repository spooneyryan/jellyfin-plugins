#!/usr/bin/env node
// User Ratings' Jellyfin Enhanced review import in the je sandbox: the import
// as a data job from a synthetic reviews.json in JE's own format, its rules and the switch-over checklist. Node 22
// built-ins only. Loopback only, never port 8096. The sandbox users' tokens come from credentials.json and are never
// printed or written; JE's settings are only read (their file's SHA-256 is compared before and after).
//
//   node scripts/userratings-je-import-probe.mjs --sandbox je [--out result.json] [--webchecks]
//
// It needs the je sandbox with User Ratings 0.3.21 or later and Jellyfin Enhanced 12.14 (dev-server.ps1 -Name je -Port
// <its port>; Initialize-Sandbox.ps1: admin, viewer, viewer2 and the media's [tmdbid-] films), where admin, viewer and
// viewer2 rate none of the films below. It writes reviews.json into the sandbox's
// plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced/ (the file there before is put back at the end) with:
//   e1 admin, Tagged Movie (tmdb 900001): 4 stars and a review (MA);
//   e2 viewer2, Tagged Movie: 3.5 stars, no text;
//   e3 viewer, Edition Movie (tmdb 900301, two films with that id): 5 stars and a review (MB);
//   e4 admin, Remake Story 2015 (tmdb 900102): 2.3 stars (rounded to 2.5) and a review that gives the ending away (MC);
//   e5 a user who isn't on this server (a new id each run), Remake Story 1990 (tmdb 900101): 4 stars (MD);
//   e6 viewer, an episode of a series the library lacks (tv 777001 s1 e2): 3 stars (ME);
//   e7 viewer, Same Id Remake (tmdb 900201, two films), dated two days ahead: 1 star (MF);
//   and an entry whose key isn't JE's.
// viewer2 is shown on the sign-in screen meanwhile (Jellyfin creates users hidden from it, so admin and viewer stay hidden;
// viewer2's policy is put back at the end). As admin:
//   1. who may call: nobody 401, a member 403; no reviews.json 404 JeDataNotFound; a file of another shape 400;
//   2. the dry run (Copies First): every count, the field names, the unknown user, the clamp, the notes and JE's settings,
//      no review text anywhere in the job, nothing written;
//   3. the UserMap gives e5 to viewer2; a real run without ifPlan 400, with another plan 409 Changed and nothing written;
//   4. Import with the plan: each rating as JE had it (stars, text, never a spoiler), one Edition copy, e6 detached, e7
//      dated no later than now; the import's snapshot named;
//   5. the same file again: AlreadyImported, nothing written;
//   6. delete then re-import: viewer2 deletes e5's rating; the import skips it (SkippedDeleted) and it stays deleted (a
//      rating a JE import made and its member deleted never comes back from that JE entry, so e5's user is new each run);
//   7. JE edits e2 later: Updated; 8. Copies = All: the other copy of each two-copy film rated too, once;
//   9. the restore of step 4's snapshot undoes the import (every rating it made gone, viewer's deletion kept);
//  10. the switch-over checklist: every JE 12.14 review setting, in order, as the sandbox's JE has them; the banner;
//      JE's settings file and reviews.json unchanged by the plugin;
//  11. with --webchecks, userratings-data-je-desktop.json: the Data tab's JE section in a browser (it undoes its import).

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { PRODUCTION_PORT } from './sandbox-policy.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PROBE = 'ur-je-import-probe';
const ADMIN = '/UserRatings/Admin';

function usage() {
    return 'usage: node scripts/userratings-je-import-probe.mjs --sandbox <name> [--out <file>] [--webchecks]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, webchecks: false };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--webchecks') o.webchecks = true;
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

// JE's folder and settings in the sandbox's data: never anywhere else.
const configurations = path.join(sandboxRoot, 'data', 'plugins', 'configurations');
const inside = (p) => path.resolve(p).toLowerCase().startsWith(path.resolve(repoRoot, '.sandbox').toLowerCase() + path.sep) && !/programdata/i.test(path.resolve(p));
const JE_DIR = path.join(configurations, 'Jellyfin.Plugin.JellyfinEnhanced');
const JE_XML = path.join(configurations, 'Jellyfin.Plugin.JellyfinEnhanced.xml');
const REVIEWS = path.join(JE_DIR, 'reviews.json');
if (![JE_DIR, JE_XML, REVIEWS].every(inside)) throw new Error('refusing JE\'s folder: it is not inside .sandbox');
if (!fs.existsSync(JE_XML)) throw new Error('Jellyfin Enhanced has no settings in this sandbox: start it with -Community JellyfinEnhanced (the je sandbox)');

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, steps: {}, webchecks: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authorization(token) {
    return `MediaBrowser Client="${PROBE}", Device="sandbox", DeviceId="${PROBE}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') h.Authorization = authorization(users[who].token);
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    let json = null;
    if (/json/i.test(res.headers.get('content-type') || '') && text.length) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text };
}

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const n = (value) => String(value).replace(/-/g, '').toLowerCase();
const sha = (file) => (fs.existsSync(file) ? crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex') : null);
const iso = (ms) => new Date(ms).toISOString();
const marker = (label) => `jeimp-${label}-${crypto.randomBytes(8).toString('hex')}`;

// ------------------------------------------------------------------------------------------------- jobs

async function start(body, query, who = 'admin') {
    return call(who, 'POST', `${ADMIN}/Import/JellyfinEnhanced`, { body, query });
}

async function finish(started) {
    if (started.status !== 202 || !started.json || !started.json.Id) return { started, job: null };
    const until = Date.now() + 180000;
    for (;;) {
        const r = await call('admin', 'GET', `${ADMIN}/Jobs/${started.json.Id}`);
        if (r.status === 200 && r.json && r.json.State !== 'Queued' && r.json.State !== 'Running') return { started, job: r.json, text: r.text };
        if (Date.now() > until) throw new Error('the JE import job did not finish');
        await delay(200);
    }
}

async function dry(body) {
    const r = await finish(await start(body, { dryRun: 'true' }));
    if (!r.job || r.job.State !== 'Succeeded') throw new Error(`the dry run: ${r.started.status} ${r.job ? r.job.State + ' ' + r.job.Error : r.started.text}`);
    return { report: r.job.Report, text: r.text };
}

async function real(body, plan) {
    const r = await finish(await start(body, { dryRun: 'false', ifPlan: plan }));
    if (!r.job || r.job.State !== 'Succeeded') throw new Error(`the import: ${r.started.status} ${r.job ? r.job.State + ' ' + r.job.Error : r.started.text}`);
    return { report: r.job.Report, text: r.text };
}

async function importNow(body) {
    const d = await dry(body);
    return { dry: d, real: await real(body, d.report.PlanToken) };
}

async function mine(who, itemId) {
    const r = await call(who, 'GET', `/UserRatings/Items/${itemId}`);
    if (r.status !== 200) throw new Error(`GET Items/${itemId} as ${who}: ${r.status}`);
    return r.json.Mine || null;
}

async function records() {
    const d = await call('admin', 'GET', `${ADMIN}/Diagnostics`);
    return { records: d.json.Records, je: d.json.JellyfinEnhanced };
}

// ------------------------------------------------------------------------------------------------- the library

async function films() {
    const r = await call('admin', 'GET', `/Items?userId=${users.admin.id}&Recursive=true&IncludeItemTypes=Movie&Fields=ProviderIds&EnableImages=false&Limit=2000`);
    if (r.status !== 200) throw new Error(`GET /Items: ${r.status}`);
    const byTmdb = {};
    for (const i of r.json.Items) {
        const t = i.ProviderIds && i.ProviderIds.Tmdb;
        if (t) (byTmdb[t] = byTmdb[t] || []).push(n(i.Id));
    }
    for (const [t, count] of [['900001', 1], ['900301', 2], ['900102', 1], ['900101', 1], ['900201', 2]]) {
        if (!byTmdb[t] || byTmdb[t].length !== count) throw new Error(`the sandbox needs ${count} film(s) tagged [tmdbid-${t}] (New-SandboxMedia.ps1); it has ${byTmdb[t] ? byTmdb[t].length : 0}`);
    }
    return byTmdb;
}

// ------------------------------------------------------------------------------------------------- the fixture

const unknownUser = crypto.randomBytes(16).toString('hex');
const marks = { MA: marker('a'), MB: marker('b'), MC: marker('c'), MD: marker('d'), ME: marker('e'), MF: marker('f'), MG: marker('g') };
const now0 = Date.now();
// JE's times a second before the run: a rating deleted by an earlier run (step 6) is older, so it doesn't keep e1 out.
const T0 = now0 - 1000;

// An entry as JE 12.14 writes it (seen by posting reviews through JE's own API in this sandbox):
// the key "<userIdN>:<movie|tv>:<tmdbKey>" and the same whole TMDb key, as text, in TmdbId ("777001:s1:e2" for an
// episode, never the series' id alone).
function entry(user, kind, tmdb, rating, content, updated, extra = '') {
    const key = `${n(user)}:${kind}:${tmdb}${extra}`;
    return [key, { UserId: n(user), TmdbId: `${tmdb}${extra}`, MediaType: kind, Content: content, Rating: rating, CreatedAt: iso(updated - 3600000), UpdatedAt: iso(updated) }];
}

function fixture(over = {}) {
    const e = [
        entry(users.admin.id, 'movie', '900001', 4, `${marks.MA} a quiet film that earns its ending.`, T0),
        entry(users.viewer2.id, 'movie', '900001', 3.5, '', T0),
        entry(users.viewer.id, 'movie', '900301', 5, `${marks.MB} the longer cut is the better one.`, T0),
        entry(users.admin.id, 'movie', '900102', 2.3, `${marks.MC} spoiler: the twist is that the narrator was the remake.`, T0),
        entry(unknownUser, 'movie', '900101', 4, `${marks.MD} an old favourite.`, T0),
        entry(users.viewer.id, 'tv', '777001', 3, `${marks.ME} an episode this library lacks.`, T0, ':s1:e2'),
        entry(users.viewer.id, 'movie', '900201', 1, `${marks.MF} dated ahead.`, now0 + 2 * 86400000)
    ];
    const reviews = Object.fromEntries(e);
    reviews['not-a-je-key'] = { UserId: 'x', Rating: 9 };
    Object.assign(reviews, over);
    return { Reviews: reviews };
}

let lastWritten = null;
function writeReviews(doc) {
    fs.mkdirSync(JE_DIR, { recursive: true });
    fs.writeFileSync(REVIEWS, JSON.stringify(doc, null, 2), 'utf8');
    lastWritten = sha(REVIEWS);
    return lastWritten;
}

const anyMarker = (text) => Object.values(marks).some((m) => String(text).includes(m));

// ------------------------------------------------------------------------------------------------- the run

const CHECKLIST = ['ShowUserReviews', 'ShowReviews', 'ShowAnilistReviews', 'ReviewTranslationEnabled', 'ReviewsExpandedByDefault', 'MirrorReviewRatingsToJellyfin', 'HideReviewsFromHiddenUsers', 'HideReviewsFromDisabledUsers', 'ActivityFeedShowReviewed', 'SpoilerStripReviews', 'ShowUserRatingOnPosters'];
const FIELDS = ['UserId', 'TmdbId', 'MediaType', 'Content', 'Rating', 'CreatedAt', 'UpdatedAt'];

const xmlBefore = sha(JE_XML);
const reviewsBefore = fs.existsSync(REVIEWS) ? fs.readFileSync(REVIEWS) : null;
const policyBefore = (await call('admin', 'GET', `/Users/${users.viewer2.id}`)).json.Policy;
let restoredPolicy = false;

async function setHidden(hidden) {
    const r = await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: Object.assign({}, policyBefore, { IsHidden: hidden }) });
    if (r.status !== 204) throw new Error(`viewer2's policy: ${r.status}`);
}

try {
    const tmdb = await films();
    const [tagged] = tmdb['900001'];
    const editions = tmdb['900301'];
    const [remake15] = tmdb['900102'];
    const [remake90] = tmdb['900101'];
    const sameIds = tmdb['900201'];
    for (const who of ['admin', 'viewer', 'viewer2']) {
        for (const id of [tagged, ...editions, remake15, remake90, ...sameIds]) {
            if (await mine(who, id)) throw new Error(`${who} already rates ${id}: run the probe where they rate none of its films`);
        }
    }

    const before = await records();
    evidence.steps.before = before.records;

    // 1. Who may call; no file; another shape.
    if (fs.existsSync(REVIEWS)) fs.rmSync(REVIEWS);
    const anon = await start(undefined, { dryRun: 'true' }, 'anonymous');
    const member = await start(undefined, { dryRun: 'true' }, 'viewer');
    const none = await start(undefined, { dryRun: 'true' });
    check('1. nobody 401, a member 403', anon.status === 401 && member.status === 403, { anon: anon.status, member: member.status });
    check('1. no reviews.json: 404 JeDataNotFound, no job', none.status === 404 && none.json && none.json.code === 'JeDataNotFound', { status: none.status, code: none.json && none.json.code });
    writeReviews([1, 2, 3]);
    const shape = await start(undefined, { dryRun: 'true' });
    check('1. a file of another shape: 400 UnsupportedFormat', shape.status === 400 && shape.json && shape.json.code === 'UnsupportedFormat', { status: shape.status, code: shape.json && shape.json.code });

    // 2. The dry run.
    writeReviews(fixture());
    await setHidden(false);
    const d1 = await dry({ Copies: 'First' });
    const r1 = d1.report;
    evidence.steps.dryRun = Object.fromEntries(Object.entries(r1).filter(([k]) => !['Samples', 'LeftOut', 'PlanToken'].includes(k)));
    check('2. the counts: 5 new, 1 detached, 1 unknown user, 1 left out, 1 rounded, 1 clamped, 2 matching two copies, 5 by hidden users (admin and viewer), none by a disabled one',
        r1.DryRun === true && r1.Total === 8 && r1.WouldCreate === 5 && r1.Created === 0 && r1.StoredDetached === 1 && r1.UnknownUser === 1 && r1.Invalid === 1 && r1.Adjusted === 1 && r1.Clamped === 1 && r1.MatchedMultiple === 2 && r1.HiddenUserReviews === 5 && r1.DisabledUserReviews === 0,
        { Total: r1.Total, WouldCreate: r1.WouldCreate, StoredDetached: r1.StoredDetached, UnknownUser: r1.UnknownUser, Invalid: r1.Invalid, Adjusted: r1.Adjusted, Clamped: r1.Clamped, MatchedMultiple: r1.MatchedMultiple, HiddenUserReviews: r1.HiddenUserReviews, DisabledUserReviews: r1.DisabledUserReviews });
    check('2. the field names, the unknown user\'s id and count, the copies, mirror and spoiler notes, JE\'s review settings',
        JSON.stringify(r1.SourceFields) === JSON.stringify(FIELDS) && r1.UnknownUserIds.length === 1 && r1.UnknownUserIds[0].JeUserId === unknownUser && r1.UnknownUserIds[0].Reviews === 1
        && /more than one copy/.test(r1.CopiesNote || '') && /is off/.test(r1.MirrorNote || '') && /no spoiler flag/i.test(r1.SpoilerNote || '') && r1.JeSettings.ShowUserReviews === true && r1.JeSettings.ReviewTranslationApiKeySet === false
        && r1.Notes.some((t) => /ahead of this server's clock/.test(t)),
        { SourceFields: r1.SourceFields, UnknownUserIds: r1.UnknownUserIds, JeSettings: r1.JeSettings });
    check('2. no review text anywhere in the job, a 32-hex-digit plan, nothing written', !anyMarker(d1.text) && /^[0-9a-f]{32}$/.test(r1.PlanToken) && JSON.stringify((await records()).records) === JSON.stringify(before.records), { plan: /^[0-9a-f]{32}$/.test(r1.PlanToken) });

    // 3. The UserMap, and the plan's guard.
    const body = { Copies: 'First', UserMap: { [unknownUser]: users.viewer2.id } };
    const d2 = await dry(body);
    check('3. the UserMap: the unknown user\'s review goes to viewer2', d2.report.UnknownUser === 0 && d2.report.WouldCreate === 6, { UnknownUser: d2.report.UnknownUser, WouldCreate: d2.report.WouldCreate });
    const noPlan = await start(body, { dryRun: 'false' });
    const otherPlan = await finish(await start(body, { dryRun: 'false', ifPlan: '0'.repeat(32) }));
    check('3. a real run without ifPlan 400; with another plan the job fails 409 Changed; nothing written',
        noPlan.status === 400 && otherPlan.job && otherPlan.job.State === 'Failed' && otherPlan.job.ErrorStatus === 409 && otherPlan.job.ErrorCode === 'Changed' && JSON.stringify((await records()).records) === JSON.stringify(before.records),
        { noPlan: noPlan.status, other: otherPlan.job && [otherPlan.job.State, otherPlan.job.ErrorStatus, otherPlan.job.ErrorCode] });

    // 4. Import.
    const i1 = await real(body, d2.report.PlanToken);
    const safety = i1.report.SafetySnapshot;
    const v1 = await mine('admin', tagged);
    const w1 = await mine('viewer2', tagged);
    const vEditions = (await Promise.all(editions.map((id) => mine('viewer', id)))).filter(Boolean);
    const a15 = await mine('admin', remake15);
    const w90 = await mine('viewer2', remake90);
    const vSame = (await Promise.all(sameIds.map((id) => mine('viewer', id)))).filter(Boolean);
    const after4 = await records();
    evidence.steps.imported = { created: i1.report.Created, detached: i1.report.StoredDetached, safety, records: after4.records };
    check('4. Import: 6 new and 1 detached, the import\'s snapshot named', i1.report.DryRun === false && i1.report.Created === 6 && i1.report.StoredDetached === 1 && /^userratings-import-/.test(safety || '') && after4.records.Detached === before.records.Detached + 1, evidence.steps.imported);
    check('4. each rating as JE had it: stars, text, never a spoiler; viewer2\'s stars only; admin\'s 2.3 as 2.5; the mapped user\'s',
        v1 && v1.Stars === 4 && v1.Text.startsWith(marks.MA) && v1.IsSpoiler === false && w1 && w1.Stars === 3.5 && !w1.Text && a15 && a15.Stars === 2.5 && a15.IsSpoiler === false && a15.Text.startsWith(marks.MC) && w90 && w90.Stars === 4 && w90.Text.startsWith(marks.MD),
        { admin1: v1 && v1.Stars, viewer2: w1 && w1.Stars, admin: a15 && a15.Stars, mapped: w90 && w90.Stars });
    const detachedList = await call('admin', 'GET', `${ADMIN}/Detached`, { query: { limit: 50 } });
    const e6 = ((detachedList.json && detachedList.json.Items) || []).find((d) => d.UserId === n(users.viewer.id) && d.Kind === 'Episode' && /^TMDb 777001/.test(d.Name || ''));
    check('4. e6 (JE\'s whole key "777001:s1:e2" in TmdbId) detached as an Episode of the series 777001, not of "777001:s1:e2", and no KeyMismatch',
        detachedList.status === 200 && !!e6 && e6.Name === 'TMDb 777001' && i1.report.KeyMismatch === 0, { status: detachedList.status, name: e6 && e6.Name, kind: e6 && e6.Kind, keyMismatch: i1.report.KeyMismatch });
    check('4. Copies = First: one Edition copy and one Same Id copy; the film dated ahead no later than now',
        vEditions.length === 1 && vSame.length === 1 && Date.parse(vSame[0].UpdatedUtc) <= Date.now() + 1000, { editions: vEditions.length, same: vSame.length, updated: vSame[0] && vSame[0].UpdatedUtc });

    // 5. The same file again.
    const d5 = await dry(body);
    check('5. the same file again: every entry AlreadyImported, nothing to write', d5.report.AlreadyImported === 7 && d5.report.WouldCreate === 0 && d5.report.WouldUpdate === 0, { AlreadyImported: d5.report.AlreadyImported, WouldCreate: d5.report.WouldCreate });

    // 6. Delete then re-import.
    const del = await call('viewer2', 'DELETE', `/UserRatings/Items/${remake90}/Mine`);
    const i6 = await importNow(body);
    check('6. viewer2 deletes e5\'s rating; the import skips it (SkippedDeleted) and it stays deleted',
        [200, 204].includes(del.status) && i6.dry.report.SkippedDeleted === 1 && i6.dry.report.WouldCreate === 0 && (await mine('viewer2', remake90)) === null, { delete: del.status, skipped: i6.dry.report.SkippedDeleted });

    // 7. JE edits e2 later.
    const [k2, e2] = entry(users.viewer2.id, 'movie', '900001', 4.5, `${marks.MG} better the second time.`, Date.now() - 1000);
    writeReviews(fixture({ [k2]: e2 }));
    const i7 = await importNow(body);
    const w7 = await mine('viewer2', tagged);
    check('7. JE edits e2 later: Updated, viewer2\'s rating is JE\'s new one', i7.dry.report.WouldUpdate === 1 && i7.real.report.Updated === 1 && w7 && w7.Stars === 4.5 && w7.Text.startsWith(marks.MG), { WouldUpdate: i7.dry.report.WouldUpdate, stars: w7 && w7.Stars });

    // 8. Copies = All.
    const all = Object.assign({}, body, { Copies: 'All' });
    const i8 = await importNow(all);
    const vEditions8 = (await Promise.all(editions.map((id) => mine('viewer', id)))).filter(Boolean);
    const vSame8 = (await Promise.all(sameIds.map((id) => mine('viewer', id)))).filter(Boolean);
    const again8 = await dry(all);
    check('8. Copies = All: the other copy of each two-copy film rated too (2 new), then nothing more',
        i8.dry.report.WouldCreate === 2 && i8.real.report.Created === 2 && vEditions8.length === 2 && vSame8.length === 2 && again8.report.WouldCreate === 0,
        { WouldCreate: i8.dry.report.WouldCreate, editions: vEditions8.length, same: vSame8.length, again: again8.report.WouldCreate });

    // 9. The restore of step 4's snapshot undoes the import.
    const restore = await call('admin', 'POST', `${ADMIN}/Snapshots/Restore`, { query: { name: safety } });
    const left = [];
    for (const [who, ids] of [['viewer', [...editions, ...sameIds]], ['viewer2', [tagged, remake90]], ['admin', [tagged, remake15]]]) {
        for (const id of ids) {
            if (await mine(who, id)) left.push(`${who} ${id}`);
        }
    }
    const after9 = await records();
    check('9. the restore of the import\'s snapshot: 200, every rating the imports made gone, the detached one too',
        restore.status === 200 && left.length === 0 && after9.records.Attached === before.records.Attached && after9.records.Detached === before.records.Detached,
        { status: restore.status, left, records: after9.records });

    // 10. The switch-over checklist.
    const overlap = after9.je && after9.je.Overlap;
    const items = (overlap && overlap.Checklist) || [];
    const item = (key) => items.find((i) => i.Key === key) || {};
    evidence.steps.checklist = items.map((i) => ({ Key: i.Key, Done: i.Done, Optional: i.Optional, Detail: i.Detail }));
    check('10. the checklist: every JE 12.14 review setting in order; user reviews on (To do), not complete, the banner',
        JSON.stringify(items.map((i) => i.Key)) === JSON.stringify(CHECKLIST) && item('ShowUserReviews').Done === false && item('ShowUserReviews').Optional === false && overlap.Complete === false && overlap.On === true && /User Reviews is on/.test(overlap.Banner || ''),
        { keys: items.map((i) => i.Key), complete: overlap && overlap.Complete });
    check('10. required: user reviews, translation, the mirror, the activity feed; the others optional; translation off with no URL or key',
        ['ShowUserReviews', 'ReviewTranslationEnabled', 'MirrorReviewRatingsToJellyfin', 'ActivityFeedShowReviewed'].every((k) => item(k).Optional === false)
        && ['ShowReviews', 'ShowAnilistReviews', 'ReviewsExpandedByDefault', 'HideReviewsFromHiddenUsers', 'HideReviewsFromDisabledUsers', 'SpoilerStripReviews', 'ShowUserRatingOnPosters'].every((k) => item(k).Optional === true)
        && item('ReviewTranslationEnabled').Done === true && item('ReviewTranslationEnabled').Detail === 'Off, and no service URL or API key is set.' && /D-pad/.test(item('ShowReviews').Text || ''),
        evidence.steps.checklist);
    // Since 0.3.22: the sandbox's JE has its activity feed off (JE's default), so Show recently
    // reviewed is done whatever it is; its settings file read, so no item is Unknown.
    check('10. the activity feed off (JE\'s default), so Show recently reviewed is done; the settings readable, no item Unknown',
        item('ActivityFeedShowReviewed').Done === true && /activity feed is off/.test(item('ActivityFeedShowReviewed').Detail || '') && after9.je.SettingsReadable === true && items.every((i) => i.Unknown === false),
        { feed: item('ActivityFeedShowReviewed'), readable: after9.je.SettingsReadable });
    check('10. JE\'s settings file and reviews.json as they were: the plugin only read them', sha(JE_XML) === xmlBefore && sha(REVIEWS) === lastWritten, { xml: sha(JE_XML) === xmlBefore, reviews: sha(REVIEWS) === lastWritten });

    // 11. The Data tab in a browser.
    if (opts.webchecks) {
        writeReviews(fixture());
        const workRoot = path.join(repoRoot, '.sandbox', opts.sandbox, 'ur-je-import-probe');
        fs.mkdirSync(workRoot, { recursive: true });
        const out = path.join(workRoot, 'userratings-data-je-desktop.json');
        const w = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', 'userratings-data-je-desktop.json', '--out', out], {
            cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'], timeout: 900000, windowsHide: true, encoding: 'utf8'
        });
        let result = null;
        try { result = readJson(out); } catch { result = null; }
        const summary = { exit: w.status, ok: !!(result && result.ok === true), steps: result && Array.isArray(result.steps) ? result.steps.length : null, failed: result && Array.isArray(result.steps) ? result.steps.filter((s) => s.ok === false).map((s) => ({ name: s.name, error: s.error })) : null };
        evidence.webchecks.push(summary);
        check('11. webcheck userratings-data-je-desktop.json', summary.ok, summary);
        const after11 = await records();
        check('11. the webcheck undid its import', after11.records.Attached === before.records.Attached && after11.records.Detached === before.records.Detached, after11.records);
    }

    await setHidden(policyBefore.IsHidden === true);
    restoredPolicy = true;
} catch (e) {
    check('the run', false, String(e && e.stack || e));
} finally {
    if (!restoredPolicy) {
        try { await setHidden(policyBefore.IsHidden === true); } catch (e) { check('viewer2\'s policy put back', false, String(e)); }
    }

    if (reviewsBefore === null) {
        if (fs.existsSync(REVIEWS)) fs.rmSync(REVIEWS);
    } else {
        fs.writeFileSync(REVIEWS, reviewsBefore);
    }

    check('JE\'s settings file unchanged at the end', sha(JE_XML) === xmlBefore, null);
}

const passed = checks.filter((c) => c.pass).length;
const result = { ok: passed === checks.length, passed, total: checks.length, checks, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text + '\n', 'utf8');
for (const c of checks) console.log(`${c.pass ? 'PASS' : 'FAIL'} ${c.name}${c.pass ? '' : ' :: ' + JSON.stringify(c.detail).slice(0, 600)}`);
console.log(`${passed}/${checks.length} passed`);
process.exit(result.ok ? 0 : 1);
