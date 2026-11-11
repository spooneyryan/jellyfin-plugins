#!/usr/bin/env node
// User Ratings' busy-database check in a sandbox (a database locked or busy longer than 5 s is a 503). Node 22 built-ins only (node:sqlite for the holder). Loopback only, never port 8096. The admin token
// from the sandbox's credentials.json is used and is never printed.
//
//   node scripts/userratings-busy-probe.mjs --sandbox default [--hold 60] [--out result.json]
//
// It needs a running sandbox with User Ratings 0.3.3 or later, whose member writes carry the check: the admin rates a
// synthetic movie of the Movies library that the admin hasn't rated.
// Steps:
//   1. PUT /UserRatings/Items/{id}/Mine/Rating {"Stars":3} as admin: 200 at once, the database Ok (Diagnostics' Store).
//   2. Starts a separate Node process that opens the sandbox's own data/userratings/userratings.db and holds
//      BEGIN IMMEDIATE (the write lock of a WAL database), as a sqlite3 shell or another program would.
//   3. The same PUT with {"Stars":4}: 503 ProblemDetails with the kit's Busy message ("The database is busy; try again in
//      a moment.") in 3 to 7 s, no path in it; then three such PUTs at once: every one 503 Busy within 7 s of the start
//      (an earlier build let the writes queued behind the first wait for the writer lock without a limit, at 5.6,
//      11.1 and 16.7 s); GET /UserRatings/Items/{id} still 200 with the 3 (a WAL reader isn't blocked); the database still
//      Ok and writable (nothing marked); the sandbox log holds the kit's Warning ("locked for more than 5 s").
//   4. Ends the holder (its transaction rolls back) and sends the PUT again: 200 at once with the 4, so the writer lock was
//      released. Then DELETE /UserRatings/Items/{id}/Mine (204) takes the probe's rating away again.
// The file the holder opens must be the sandbox's own (under .sandbox/<name>/), never anything under C:\ProgramData.
// Until 0.3.3 the write was the Debug build's maintenance step DebugWriteProbe, which the member writes replaced.

import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { PRODUCTION_PORT } from './sandbox-policy.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const BUSY = 'The database is busy; try again in a moment.';
const LIMIT_MS = 7000;

function usage() {
    return 'usage: node scripts/userratings-busy-probe.mjs --sandbox <name> [--hold <s>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, hold: 60, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--hold') o.hold = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!Number.isFinite(o.hold) || o.hold < 20 || o.hold > 600) throw new Error(`--hold is 20 to 600 seconds. ${usage()}`);
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^\uFEFF/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const admin = readJson(path.join(sandboxRoot, 'credentials.json')).users?.admin;
const token = admin?.token;
if (!token || !admin.id) throw new Error('no admin in credentials.json; run Initialize-Sandbox.ps1');

// The sandbox's own database: Jellyfin's DataPath is <DataDir>/data.
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
const database = path.join(dataDir, 'data', 'userratings', 'userratings.db');
const inside = (child, parent) => {
    const rel = path.relative(path.resolve(parent), path.resolve(child));
    return rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel);
};
if (!inside(database, sandboxRoot) || /programdata/i.test(database)) throw new Error(`refusing ${database}: not inside the sandbox ${sandboxRoot}`);
if (!fs.existsSync(database)) throw new Error(`no ${database}; start the sandbox with User Ratings first`);

const authHeader = () => `MediaBrowser Client="userratings-busy-probe", Device="sandbox", DeviceId="userratings-busy-probe", Version="1.0.0", Token="${token}"`;

async function call(method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, `http://127.0.0.1:${port}`);
    for (const [k, v] of Object.entries(query || {})) url.searchParams.set(k, String(v));
    const headers = { Authorization: authHeader(), Accept: 'application/json' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const started = Date.now();
    const res = await fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual', signal: AbortSignal.timeout(30000) });
    const text = await res.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch { json = null; }
    return { status: res.status, type: res.headers.get('content-type') || '', json, ms: Date.now() - started };
}

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, time: new Date().toISOString() }, writes: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
}

async function store() {
    const d = await call('GET', '/UserRatings/Admin/Diagnostics');
    return d.json && d.json.Store ? { Status: d.json.Store.Status, ReadOnly: d.json.Store.ReadOnly, Message: d.json.Store.Message ?? null, version: d.json.PluginVersion } : { status: d.status };
}

// A synthetic movie of the Movies library the admin hasn't rated.
async function findMovie() {
    const folders = (await call('GET', '/Library/VirtualFolders')).json || [];
    const library = folders.find((l) => l.Name === 'Movies');
    if (!library) throw new Error('the sandbox has no Movies library; run Initialize-Sandbox.ps1');
    const list = (await call('GET', '/Items', { query: { userId: admin.id, ParentId: library.ItemId, IncludeItemTypes: 'Movie', Recursive: 'true', SortBy: 'SortName', Limit: 20 } })).json?.Items || [];
    for (const movie of list) {
        const ratings = await call('GET', `/UserRatings/Items/${movie.Id}`);
        if (ratings.status === 200 && !ratings.json.Mine) return { id: movie.Id, name: movie.Name };
    }
    throw new Error('no movie in Movies that the admin hasn\'t rated');
}

async function rate(value, label) {
    const r = await call('PUT', `/UserRatings/Items/${movie.id}/Mine/Rating`, { body: { Stars: value } });
    evidence.writes.push({ label, status: r.status, ms: r.ms, detail: r.json && r.json.detail ? r.json.detail : null });
    return r;
}

// A separate process holding BEGIN IMMEDIATE on the database until its stdin closes (or --hold seconds pass).
function startHolder() {
    const code = `
        import { DatabaseSync } from 'node:sqlite';
        const db = new DatabaseSync(process.argv[1]);
        db.exec('PRAGMA busy_timeout=0;');
        db.exec('BEGIN IMMEDIATE;');
        process.stdout.write('held\\n');
        const release = () => { try { db.exec('ROLLBACK;'); db.close(); } catch {} process.stdout.write('released\\n'); process.exit(0); };
        process.stdin.on('end', release);
        process.stdin.resume();
        setTimeout(release, Number(process.argv[2]) * 1000);
    `;
    const child = spawn(process.execPath, ['--no-warnings', '--input-type=module', '-e', code, database, String(opts.hold)], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    let out = '';
    let err = '';
    child.stdout.on('data', (c) => { out += c; });
    child.stderr.on('data', (c) => { err += c; });
    const held = new Promise((resolve, reject) => {
        const timer = setInterval(() => {
            if (out.includes('held')) { clearInterval(timer); resolve(); }
        }, 50);
        child.on('exit', (codeExit) => { clearInterval(timer); if (!out.includes('held')) reject(new Error(`the holder exited (${codeExit}): ${err.trim()}`)); });
    });
    const exited = new Promise((resolve) => child.on('exit', resolve));
    return { child, held, exited, release: () => child.stdin.end(), output: () => out };
}

function logWarnings(sinceMs) {
    const logDir = path.join(sandboxRoot, 'log');
    if (!fs.existsSync(logDir)) return [];
    const files = fs.readdirSync(logDir).map((f) => path.join(logDir, f)).filter((f) => fs.statSync(f).mtimeMs >= sinceMs - 1000);
    return files.flatMap((f) => fs.readFileSync(f, 'utf8').split(/\r?\n/)).filter((l) => l.includes('locked for more than 5 s'));
}

const begun = Date.now();
let holder = null;
let movie = null;
let rated = false;
try {
    const baseline = await store();
    check('User Ratings 0.3.3 or later, its database Ok and writable', /^(0\.3\.(3|[4-9]|\d\d)|[1-9]\d*\.\d+\.\d+)\./.test(baseline.version || '') && baseline.Status === 'Ok' && baseline.ReadOnly === false, baseline);
    movie = await findMovie();
    evidence.movie = movie;

    const first = await rate(3, 'before');
    rated = first.status === 200;
    check('before: PUT Mine/Rating {"Stars":3} as admin is 200 at once', first.status === 200 && first.ms < 2000 && first.json.Mine.Stars === 3, { status: first.status, ms: first.ms });

    holder = startHolder();
    await holder.held;
    check('a separate process holds BEGIN IMMEDIATE on the sandbox database', true, path.relative(repoRoot, database));

    const busy = await rate(4, 'while held');
    check('while held: the PUT is 503 ProblemDetails with the kit\'s Busy message', busy.status === 503 && /application\/problem\+json/.test(busy.type) && busy.json && busy.json.detail === BUSY,
        { status: busy.status, detail: busy.json && busy.json.detail });
    check(`while held: it answered in 3 to ${LIMIT_MS / 1000} s (busy_timeout, then the 503)`, busy.ms >= 3000 && busy.ms < LIMIT_MS, busy.ms);
    check('the 503 message names no path', !/[A-Za-z]:\\|\/data\//.test((busy.json && busy.json.detail) || ''), busy.json && busy.json.detail);
    const begunAt = Date.now();
    const queued = await Promise.all([1, 2, 3].map((n) => rate(4, `while held, at once ${n}`).then((r) => ({ status: r.status, detail: r.json && r.json.detail, at: Date.now() - begunAt }))));
    check(`while held: three PUTs at once all answer 503 Busy within ${LIMIT_MS / 1000} s of the start (none queued behind another)`,
        queued.every((q) => q.status === 503 && q.detail === BUSY && q.at < LIMIT_MS), queued);
    const read = await call('GET', `/UserRatings/Items/${movie.id}`);
    check('while held: GET Items/{id} is 200 with the 3 (a WAL reader isn\'t blocked)', read.status === 200 && read.json.Mine && read.json.Mine.Stars === 3, { status: read.status, ms: read.ms });
    const during = await store();
    check('while held: the database stays Ok and writable (nothing marked)', during.Status === 'Ok' && during.ReadOnly === false && during.Message === null, during);

    holder.release();
    await holder.exited;
    check('the holder released the database', holder.output().includes('released'), holder.output().trim());

    const after = await rate(4, 'after');
    check('after: the PUT is 200 at once with the 4 (the writer lock was released)', after.status === 200 && after.ms < 2000 && after.json.Mine.Stars === 4, { status: after.status, ms: after.ms });
    const end = await store();
    check('after: the database Ok and writable', end.Status === 'Ok' && end.ReadOnly === false, end);

    const warnings = logWarnings(begun);
    check('the sandbox log has the kit\'s Busy Warning', warnings.length >= 1, warnings.length);
    evidence.logWarnings = warnings.map((l) => l.replace(/Token="[^"]*"/g, 'Token="REDACTED"'));
} catch (e) {
    check('probe ran', false, String(e && e.message ? e.message : e));
} finally {
    if (holder && holder.child.exitCode === null) {
        holder.release();
        await Promise.race([holder.exited, delay(5000)]);
        if (holder.child.exitCode === null) holder.child.kill();
    }

    // The probe's rating goes again (DELETE Mine, as the admin).
    if (movie && rated) {
        const removed = await call('DELETE', `/UserRatings/Items/${movie.id}/Mine`).catch((e) => ({ status: String(e.message) }));
        check('the probe\'s rating is deleted again (204)', removed.status === 204, { status: removed.status });
    }
}

const failed = checks.filter((c) => !c.pass);
const result = { pass: failed.length === 0, checks, evidence };
if (opts.out) {
    fs.mkdirSync(path.dirname(path.resolve(opts.out)), { recursive: true });
    fs.writeFileSync(opts.out, JSON.stringify(result, null, 2));
}
for (const c of checks) console.log(`${c.pass ? 'PASS' : 'FAIL'} ${c.name}${c.pass ? '' : ` (${JSON.stringify(c.detail)})`}`);
console.log(`${checks.length - failed.length} of ${checks.length} passed`);
process.exit(failed.length === 0 ? 0 : 1);
