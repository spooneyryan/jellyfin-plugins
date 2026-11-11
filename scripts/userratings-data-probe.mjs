#!/usr/bin/env node
// User Ratings' Data API in a running sandbox: an export and import
// round trip, and a restore, with the residue checks after each. Node 22 built-ins only. Loopback only, never port
// 8096. The sandbox users' tokens come from credentials.json and are never printed or written.
//
//   node scripts/userratings-data-probe.mjs --sandbox default [--out result.json]
//
// It needs a running sandbox with User Ratings 0.3.19 or later (Initialize-Sandbox.ps1: the users admin, viewer and
// viewer2, the synthetic movie Long Test Movie with no rating of viewer's or viewer2's). It replaces the sandbox's ratings
// with an export of themselves (step 6), so run it on a sandbox whose ratings are synthetic. As admin, viewer and viewer2:
//   1. who may call: nobody 401, a member 403 (export, import, jobs, snapshots, the detached list), the admin 200 or 202,
//      every answer with data no-store;
//   2. viewer and viewer2 rate Long Test Movie, each with a review holding a random marker (MA, MB);
//   3. Export as a job: 202, polled until it succeeds; its file an attachment, no-store, the envelope of User Ratings with
//      both rows and their markers, the tombstones, ledger and log; GET Export at once gives the same counts;
//   4. a Merge dry run of the file: 202, both rows Unchanged, a 32-hex-digit PlanToken, nothing written;
//   5. the plan's guard: a real run without ifPlan 400; with another plan the job fails with 409 Changed; nothing written;
//   6. viewer deletes their rating (a tombstone); after the checkpoint, MA in neither userratings.db nor its -wal; then
//      Replace with the export (its dry run, then its plan): viewer's row SkippedDeleted and never back, viewer2's kept;
//      an import pre-op snapshot named in SafetySnapshot, without MA; after the checkpoint MA still nowhere live;
//   7. viewer2 edits their review to MC; the admin restores the import's pre-op snapshot: 200, a restore safety
//      snapshot; viewer2's review is MB again; after the checkpoint MC is in neither userratings.db nor its -wal, MA
//      neither; the restore's own snapshot holds MC (it is the undo);
//   8. the detached list and a relink or purge of a rating that isn't detached: 404;
//   9. another export job: its file in the plugin's tmp/ holds MB and is kept an hour (FileExpiresUtc 60 minutes after
//      FinishedUtc); DELETE Jobs/{id} deletes it at once (204, the file gone, HasFile false, its File 404), and again 409
//      JobFinished;
//  10. both downloads, GET Export and the job's file, named userratings-export-yyyyMMdd-HHmmss.json.
// Every rating the probe made is deleted by its owner at the end.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { PRODUCTION_PORT } from './sandbox-policy.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PROBE = 'ur-data-probe';
const ADMIN = '/UserRatings/Admin';

function usage() {
    return 'usage: node scripts/userratings-data-probe.mjs --sandbox <name> [--out <file>]';
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
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer', 'viewer2']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}

// The plugin's folder in the sandbox's data: never anywhere else.
const pluginData = path.join(sandboxRoot, 'data', 'data', 'userratings');
if (!path.resolve(pluginData).toLowerCase().startsWith(path.resolve(repoRoot, '.sandbox').toLowerCase()) || /programdata/i.test(pluginData)) throw new Error('refusing the data folder');
const DB = path.join(pluginData, 'userratings.db');
const WAL = DB + '-wal';

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authorization(token) {
    return `MediaBrowser Client="${PROBE}", Device="sandbox", DeviceId="${PROBE}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// One request as `who` ('admin', 'viewer', 'viewer2' or 'anonymous'). Never follows redirects.
async function call(who, method, p, { body, raw, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') h.Authorization = authorization(users[who].token);
    let payload;
    if (raw !== undefined) { payload = raw; h['Content-Type'] = 'application/json'; }
    else if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    const type = res.headers.get('content-type') || '';
    let json = null;
    if (/json/i.test(type) && text.length) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, type, json, text, cache: res.headers.get('cache-control'), disposition: res.headers.get('content-disposition') };
}

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const guid = (value) => String(value).replace(/-/g, '').toLowerCase();
const marker = (label) => `data-${label}-${crypto.randomBytes(9).toString('hex')}`;
const holds = (file, text) => fs.existsSync(file) && fs.readFileSync(file).includes(Buffer.from(text, 'utf8'));
const where = (text) => ({ db: holds(DB, text), wal: holds(WAL, text), walBytes: fs.existsSync(WAL) ? fs.statSync(WAL).size : null });
function findFile(dir, name) {
    if (!fs.existsSync(dir)) return null;
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, e.name);
        if (e.isDirectory()) {
            const hit = findFile(p, name);
            if (hit) return hit;
        } else if (e.name === name) {
            return p;
        }
    }
    return null;
}

async function checkpointNow() {
    const d = await call('admin', 'GET', `${ADMIN}/Diagnostics`);
    const c = d.json && d.json.Store && d.json.Store.Checkpoint;
    if (!c || typeof c.Requested !== 'number') throw new Error('Diagnostics has no Store.Checkpoint');
    return c;
}

// Waits until a checkpoint asked for after `before` is counted and none is pending (the kit's background rounds).
async function waitCheckpoint(before, label) {
    const started = Date.now();
    for (;;) {
        const c = await checkpointNow();
        if (c.Requested > before.Requested && !c.Pending) return c;
        if (Date.now() - started > 180000) throw new Error(`the checkpoint after ${label} did not complete`);
        await delay(150);
    }
}

async function waitJob(id) {
    const started = Date.now();
    for (;;) {
        const r = await call('admin', 'GET', `${ADMIN}/Jobs/${id}`);
        if (r.status !== 200) throw new Error(`job ${r.status}`);
        if (r.json.State !== 'Queued' && r.json.State !== 'Running') return r;
        if (Date.now() - started > 300000) throw new Error('the job did not finish');
        await delay(250);
    }
}

async function importFile(text, query) {
    const started = await call('admin', 'POST', `${ADMIN}/Import`, { raw: text, query });
    if (started.status !== 202) return { started, job: null };
    return { started, job: (await waitJob(started.json.Id)).json };
}

const started = Date.now();
const made = [];
const item = (id) => `/UserRatings/Items/${id}`;
async function rate(who, id, stars, text) {
    const a = await call(who, 'PUT', `${item(id)}/Mine/Rating`, { body: { Stars: stars } });
    const b = await call(who, 'PUT', `${item(id)}/Mine/Review`, { body: { Text: text, IsSpoiler: false } });
    if (a.status !== 200 || b.status !== 200) throw new Error(`rating ${a.status}, review ${b.status}`);
    made.push({ who, ratingId: a.json.Mine.Id });
    return a.json.Mine.Id;
}
const mine = async (who, id) => (await call(who, 'GET', item(id))).json?.Mine || null;

try {
    const MA = marker('a');
    const MB = marker('b');
    const MC = marker('c');

    // ---- 1. who may call
    const member = [
        await call('viewer', 'GET', `${ADMIN}/Export`), await call('viewer', 'POST', `${ADMIN}/Export`), await call('viewer', 'POST', `${ADMIN}/Import`, { raw: '{}' }),
        await call('viewer', 'GET', `${ADMIN}/Jobs`), await call('viewer', 'GET', `${ADMIN}/Snapshots`), await call('viewer', 'POST', `${ADMIN}/Snapshots/Restore`, { query: { name: 'x.db' } }),
        await call('viewer', 'GET', `${ADMIN}/Detached`)
    ];
    const anon = await call('anonymous', 'GET', `${ADMIN}/Export`);
    const adminReads = [await call('admin', 'GET', `${ADMIN}/Snapshots`), await call('admin', 'GET', `${ADMIN}/Jobs`), await call('admin', 'GET', `${ADMIN}/Detached`)];
    check('who may call: nobody 401, a member 403 on every Data route, the admin 200 with no-store',
        anon.status === 401 && member.every((r) => r.status === 403) && adminReads.every((r) => r.status === 200 && r.cache === 'no-store'),
        { anon: anon.status, member: member.map((r) => r.status), admin: adminReads.map((r) => [r.status, r.cache]) });

    // ---- 2. the ratings
    const found = await call('viewer', 'GET', '/Items', { query: { userId: users.viewer.id, Recursive: 'true', searchTerm: 'Long Test Movie', IncludeItemTypes: 'Movie', Limit: 5 } });
    const movie = (found.json.Items || []).find((i) => i.Name === 'Long Test Movie' || i.Name.startsWith('Long Test Movie ('));
    if (!check('Long Test Movie is there, with no rating of viewer\'s or viewer2\'s', movie && !(await mine('viewer', movie.Id)) && !(await mine('viewer2', movie.Id)), { found: !!movie })) throw new Error('not a fresh sandbox');
    const ra = await rate('viewer', movie.Id, 3.5, `Data probe review ${MA}.`);
    const rb = await rate('viewer2', movie.Id, 4, `Data probe review ${MB}.`);

    // ---- 3. export
    const job = await call('admin', 'POST', `${ADMIN}/Export`);
    const done = job.status === 202 ? await waitJob(job.json.Id) : null;
    const file = done ? await call('admin', 'GET', `${ADMIN}/Jobs/${job.json.Id}/File`) : null;
    const envelope = file && file.status === 200 ? JSON.parse(file.text) : null;
    const rows = (envelope && envelope.Data && envelope.Data.Ratings) || [];
    const at = await call('admin', 'GET', `${ADMIN}/Export`);
    const atRows = at.status === 200 ? (JSON.parse(at.text).Data.Ratings || []) : [];
    evidence.export = { job: job.status, state: done && done.json.State, report: done && done.json.Report, fileStatus: file && file.status, disposition: file && file.disposition, rows: rows.length };
    check('Export as a job: 202, then Succeeded; its file an attachment, no-store, User Ratings\' envelope with both rows, their words, the tombstones, ledger and log; GET Export the same rows',
        job.status === 202 && done.json.State === 'Succeeded' && done.json.HasFile === true && file.status === 200 && file.cache === 'no-store' && /attachment/i.test(file.disposition || '')
        && envelope.Plugin && rows.some((r) => guid(r.Id) === guid(ra) && r.ReviewText.includes(MA)) && rows.some((r) => guid(r.Id) === guid(rb) && r.ReviewText.includes(MB))
        && Array.isArray(envelope.Data.Tombstones) && Array.isArray(envelope.Data.ImportLedger) && Array.isArray(envelope.Data.ModerationLog) && atRows.length === rows.length && at.cache === 'no-store',
        evidence.export);

    // ---- 4. a Merge dry run
    const dry = await importFile(file.text, { mode: 'Merge', dryRun: 'true', mapUsersBy: 'Id' });
    const report = dry.job && dry.job.Report;
    check('a Merge dry run of the file: 202, every row Unchanged, a 32-hex-digit PlanToken, nothing written (both rows as before)',
        dry.started.status === 202 && dry.job.State === 'Succeeded' && report.DryRun === true && /^[0-9a-f]{32}$/.test(report.PlanToken)
        && report.Outcomes.Unchanged === rows.length && (await mine('viewer', movie.Id)).Text.includes(MA),
        { status: dry.started.status, state: dry.job && dry.job.State, outcomes: report && report.Outcomes });

    // ---- 5. the plan's guard
    const noPlan = await call('admin', 'POST', `${ADMIN}/Import`, { raw: file.text, query: { mode: 'Merge', dryRun: 'false' } });
    const otherPlan = await importFile(file.text, { mode: 'Merge', dryRun: 'false', ifPlan: '0'.repeat(32) });
    check('the plan\'s guard: a real run without ifPlan 400; with another plan the job fails with 409 Changed; nothing written',
        noPlan.status === 400 && otherPlan.job && otherPlan.job.State === 'Failed' && otherPlan.job.ErrorCode === 'Changed' && otherPlan.job.ErrorStatus === 409 && (await mine('viewer2', movie.Id)).Text.includes(MB),
        { noPlan: noPlan.status, other: otherPlan.job && [otherPlan.job.State, otherPlan.job.ErrorCode, otherPlan.job.ErrorStatus] });

    // ---- 6. a deletion, then Replace with the export
    let before = await checkpointNow();
    const deleted = await call('viewer', 'DELETE', `/UserRatings/Mine/${ra}`);
    await waitCheckpoint(before, 'viewer\'s delete');
    const afterDelete = where(MA);
    check('viewer deletes their rating: after the checkpoint MA is in neither userratings.db nor its -wal', (deleted.status === 200 || deleted.status === 204) && !afterDelete.db && !afterDelete.wal, { status: deleted.status, afterDelete });

    const replaceDry = await importFile(file.text, { mode: 'Replace', dryRun: 'true', mapUsersBy: 'Id' });
    before = await checkpointNow();
    const replaced = await importFile(file.text, { mode: 'Replace', dryRun: 'false', mapUsersBy: 'Id', ifPlan: replaceDry.job.Report.PlanToken });
    await waitCheckpoint(before, 'the Replace');
    const r6 = replaced.job && replaced.job.Report;
    const safety = r6 && r6.SafetySnapshot;
    const safetyFile = safety ? findFile(pluginData, safety) : null;
    const afterReplace = where(MA);
    evidence.replace = { dry: replaceDry.job && replaceDry.job.Report.Outcomes, outcomes: r6 && r6.Outcomes, safety, safetyFile: safetyFile && path.relative(sandboxRoot, safetyFile), afterReplace };
    check('Replace with the export (its dry run, then its plan): viewer\'s deleted row SkippedDeleted and never back, viewer2\'s kept; an import snapshot without MA; MA nowhere live after the checkpoint',
        replaced.job && replaced.job.State === 'Succeeded' && r6.Outcomes.SkippedDeleted >= 1 && !(await mine('viewer', movie.Id)) && (await mine('viewer2', movie.Id)).Text.includes(MB)
        && /import/.test(safety || '') && !!safetyFile && !holds(safetyFile, MA) && !afterReplace.db && !afterReplace.wal,
        evidence.replace);

    // ---- 7. a restore
    const edit = await call('viewer2', 'PUT', `${item(movie.Id)}/Mine/Review`, { body: { Text: `Data probe review ${MC}.`, IsSpoiler: false } });
    before = await checkpointNow();
    const restored = await call('admin', 'POST', `${ADMIN}/Snapshots/Restore`, { query: { name: safety } });
    await waitCheckpoint(before, 'the restore');
    const afterRestore = { MA: where(MA), MC: where(MC) };
    const undo = restored.json && restored.json.SafetySnapshot;
    const undoFile = undo ? findFile(pluginData, undo) : null;
    evidence.restore = { status: restored.status, cache: restored.cache, answer: restored.json, afterRestore, undoFile: undoFile && path.relative(sandboxRoot, undoFile) };
    check('viewer2 edits to MC, then the import snapshot is restored: 200 no-store, a restore safety snapshot that holds MC; viewer2\'s review MB again; after the checkpoint MC and MA in neither userratings.db nor its -wal',
        edit.status === 200 && restored.status === 200 && restored.cache === 'no-store' && guid(restored.json.RestoredFrom || '') === guid(safety) && /restore/.test(undo || '') && !!undoFile && holds(undoFile, MC)
        && (await mine('viewer2', movie.Id)).Text.includes(MB) && !afterRestore.MA.db && !afterRestore.MA.wal && !afterRestore.MC.db && !afterRestore.MC.wal,
        evidence.restore);

    // ---- 8. the detached list
    const list = await call('admin', 'GET', `${ADMIN}/Detached`, { query: { limit: 50 } });
    const relink = await call('admin', 'POST', `${ADMIN}/Detached/${rb}/Relink`, { body: { ItemId: movie.Id } });
    const purge = await call('admin', 'DELETE', `${ADMIN}/Detached/${rb}`);
    check('the detached list answers a page, no review text; Relink and Purge of a rating that isn\'t detached 404, and it stays',
        list.status === 200 && Array.isArray(list.json.Items) && !list.text.includes(MB) && relink.status === 404 && purge.status === 404 && (await mine('viewer2', movie.Id)).Text.includes(MB),
        { list: list.status, relink: relink.status, purge: purge.status });

    // ---- 9. the export's file on the server: kept an hour for its download, deleted by DELETE
    const tmp = path.join(pluginData, 'tmp');
    const kept = await call('admin', 'POST', `${ADMIN}/Export`);
    const keptJob = kept.status === 202 ? (await waitJob(kept.json.Id)).json : null;
    const keptFile = keptJob ? path.join(tmp, `userratings-export-${guid(keptJob.Id)}.json`) : null;
    const minutes = keptJob && keptJob.FileExpiresUtc && keptJob.FinishedUtc ? (Date.parse(keptJob.FileExpiresUtc) - Date.parse(keptJob.FinishedUtc)) / 60000 : null;
    const heldBefore = !!keptFile && holds(keptFile, MB);
    const dropped = keptJob ? await call('admin', 'DELETE', `${ADMIN}/Jobs/${keptJob.Id}`) : null;
    const afterDrop = keptJob ? (await call('admin', 'GET', `${ADMIN}/Jobs/${keptJob.Id}`)).json : null;
    const fileAfter = keptJob ? await call('admin', 'GET', `${ADMIN}/Jobs/${keptJob.Id}/File`) : null;
    const again = keptJob ? await call('admin', 'DELETE', `${ADMIN}/Jobs/${keptJob.Id}`) : null;
    evidence.exportFile = {
        job: kept.status, state: keptJob && keptJob.State, minutes, heldBefore, gone: !!keptFile && !fs.existsSync(keptFile),
        delete: dropped && dropped.status, hasFile: afterDrop && afterDrop.HasFile, expires: afterDrop && afterDrop.FileExpiresUtc,
        file: fileAfter && fileAfter.status, again: again && [again.status, again.json && again.json.code]
    };
    check('the export job\'s file in tmp/ holds the reviews and is kept an hour for its download (FileExpiresUtc); DELETE Jobs/{id} deletes it at once: 204, the file gone, HasFile false, its File 404, a second DELETE 409 JobFinished',
        keptJob && keptJob.State === 'Succeeded' && keptJob.HasFile === true && minutes === 60 && heldBefore && dropped.status === 204 && !fs.existsSync(keptFile)
        && afterDrop.HasFile === false && afterDrop.FileExpiresUtc == null && fileAfter.status === 404 && again.status === 409 && again.json && again.json.code === 'JobFinished',
        evidence.exportFile);

    // ---- 10. both downloads named alike: userratings-export-yyyyMMdd-HHmmss.json
    const named = (r) => ((r && r.disposition) || '').match(/filename="?(userratings-export-\d{8}-\d{6}\.json)"?/);
    check('GET Export and the job\'s file are both named userratings-export-yyyyMMdd-HHmmss.json', !!named(file) && !!named(at), { job: file && file.disposition, get: at && at.disposition });
} catch (e) {
    check('probe ran', false, String(e && e.stack ? e.stack.split('\n').slice(0, 4).join(' | ') : e));
} finally {
    for (const left of made) await call(left.who, 'DELETE', `/UserRatings/Mine/${left.ratingId}`).catch(() => null);
}

evidence.seconds = Math.round((Date.now() - started) / 1000);
const failed = checks.filter((x) => !x.pass);
const result = { pass: failed.length === 0, checks, evidence };
const text = JSON.stringify(result, null, 2);
for (const u of Object.values(users)) {
    if (u && u.token && text.includes(u.token)) throw new Error('a token reached the result; nothing written');
}
if (opts.out) {
    fs.mkdirSync(path.dirname(path.resolve(opts.out)), { recursive: true });
    fs.writeFileSync(opts.out, text);
}
for (const x of checks) console.log(`${x.pass ? 'PASS' : 'FAIL'} ${x.name}${x.pass ? '' : ` (${JSON.stringify(x.detail)})`}`);
console.log(`${checks.length - failed.length} of ${checks.length} passed`);
process.exit(failed.length === 0 ? 0 : 1);
