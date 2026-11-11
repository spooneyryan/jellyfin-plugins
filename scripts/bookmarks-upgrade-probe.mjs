#!/usr/bin/env node
// The 1.0.0 -> 1.1.0 upgrade of Bookmarks in a sandbox that ran Bookmarks 1.0.0 first, and the way back
// (plugins/Bookmarks/DEVELOPMENT.md, "The upgrade from 1.0" and "Going back to 1.0"). Node 22 built-ins only.
// Loopback only, never port 8096. The sandbox users' tokens come from credentials.json and are never printed.
//
//   node scripts/bookmarks-upgrade-probe.mjs --sandbox <name> --phase seed        [--out result.json]   # Bookmarks 1.0.0 running
//   node scripts/bookmarks-upgrade-probe.mjs --sandbox <name> --phase upgraded    --v1 <file> [--expect-copy <name>] [--out result.json]
//   node scripts/bookmarks-upgrade-probe.mjs --sandbox <name> --phase downgraded  [--out result.json]   # 1.0.0 again, the .v1-* copy put back
//
// seed (as 1.0.0, through its REST API): admin's bookmarks on Long Test Movie with notes of each kind the upgrade treats
// differently (one short line, a multi-line note, a note over 100 characters, none, accents and other scripts), a Fast and
// an Accurate clip, viewer's on Chaptered Movie and viewer2's range with a Fast clip on Tagged Movie; it waits for the clips
// and writes the manifest (every user's list as 1.0 returns it, each clip file's size and SHA-256 as downloaded) to
// .sandbox/<name>/bookmarks-upgrade-probe/manifest.json.
// upgraded (1.1.x, after the first start on that data): Bookmarks 1.2.2.0 active; Diagnostics' store at schema 2, writable,
// Migrated from 1 (or Ok on a later start) with exactly one .v1-* copy kept, byte for byte the 1.0 file (--v1, a copy taken
// with the server stopped; --expect-copy: the copy's name an earlier upgrade made, which must be reused); every user's
// bookmarks the manifest's (ids, video, times), a short one-line note as the name and the others kept as the note; every
// clip Completed with its mode, file name and size, and its file downloading byte for byte the same.
// downgraded (1.0.0 again, with bookmarks.json replaced by the .v1-* copy): Bookmarks 1.0.0.0 active and every user's
// list exactly the manifest's, the clips downloading byte for byte the same.
// It only reads (and, in seed, makes the bookmarks and clips); it never restarts or deploys anything. It prints one JSON
// result (pass, checks, evidence) and exits with 0 when every check passed.

import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const TICKS = 10_000_000;
const LONG_NOTE = 'upgrade-probe long note: ' + 'a scene worth keeping, '.repeat(6).trim();

function usage() {
    return 'usage: node scripts/bookmarks-upgrade-probe.mjs --sandbox <name> --phase seed|upgraded|downgraded [--v1 <file>] [--expect-copy <name>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, phase: null, v1: null, expectCopy: null, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--phase') o.phase = argv[++i];
        else if (a === '--v1') o.v1 = argv[++i];
        else if (a === '--expect-copy') o.expectCopy = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox) || !['seed', 'upgraded', 'downgraded'].includes(o.phase)) throw new Error(usage());
    if (o.phase === 'upgraded' && !o.v1) throw new Error('--phase upgraded needs --v1. ' + usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const workRoot = path.join(sandboxRoot, 'bookmarks-upgrade-probe');
const manifestFile = path.join(workRoot, 'manifest.json');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const dataFolder = path.join(path.resolve(state.DataDir || path.join(sandboxRoot, 'data')), 'data', 'bookmarks');
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer', 'viewer2']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}
const BOOKMARKS_GUID = 'd0ee1b2a7f7146548ff2163a81441b03';
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const sha = (bytes) => crypto.createHash('sha256').update(bytes).digest('hex');

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, phase: opts.phase };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json', Authorization: `MediaBrowser Client="bookmarks-upgrade-probe", Device="sandbox", DeviceId="bookmarks-upgrade-probe-${who}", Version="1.0.0", Token="${users[who].token}"` };
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const bytes = Buffer.from(await res.arrayBuffer());
    let json = null;
    if (bytes.length && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(bytes.toString('utf8')); } catch { json = null; }
    }
    return { status: res.status, json, bytes, headers: res.headers };
}

async function ok(who, method, p, options, expected = [200, 201, 202, 204]) {
    const r = await call(who, method, p, options);
    if (!expected.includes(r.status)) throw new Error(`${method} ${p} as ${who}: ${r.status} ${r.bytes.toString('utf8').slice(0, 300)}`);
    return r.json;
}

async function plugin() {
    const list = await ok('admin', 'GET', '/Plugins');
    return (list || []).filter((p) => guid(p.Id) === BOOKMARKS_GUID).map((p) => ({ Version: p.Version, Status: p.Status }));
}

async function movie(who, folder) {
    const r = await ok(who, 'GET', '/Items', { query: { userId: users[who].id, Recursive: 'true', IncludeItemTypes: 'Movie', Fields: 'Path', EnableImages: 'false' } });
    const found = r.Items.filter((i) => typeof i.Path === 'string' && i.Path.split(/[\\/]/).includes(folder));
    if (found.length !== 1) throw new Error(`expected one ${folder} for ${who}, found ${found.length}`);
    return found[0];
}

// A user's bookmarks as the plugin lists them (1.0 and 1.1 both answer GET /Bookmarks with every bookmark of the caller).
async function list(who) {
    const all = await ok(who, 'GET', '/Bookmarks');
    return all.map((b) => ({
        Id: guid(b.Id), ItemId: guid(b.ItemId), StartTicks: b.StartTicks, EndTicks: b.EndTicks ?? null, Note: b.Note ?? null, Name: b.Name ?? null,
        CreatedAt: b.CreatedAt, Clip: b.Clip ? { Status: b.Clip.Status, Mode: b.Clip.Mode, FileName: b.Clip.FileName ?? null, FileSize: b.Clip.FileSize ?? null } : null
    })).sort((a, b) => a.Id.localeCompare(b.Id));
}

async function download(who, id) {
    const r = await call(who, 'GET', `/Bookmarks/${id}/Clip/File`, { query: { download: "true" } });
    return { status: r.status, size: r.bytes.length, sha256: r.status === 200 ? sha(r.bytes) : null };
}

async function waitClip(who, id) {
    const until = Date.now() + 240000;
    for (;;) {
        const b = await ok(who, 'GET', `/Bookmarks/${id}`);
        const status = b.Clip && b.Clip.Status;
        if (status === 'Completed') return b;
        if (status && !['Queued', 'Running'].includes(status)) throw new Error(`the clip of ${id} ended ${status}: ${b.Clip.Error}`);
        if (Date.now() > until) throw new Error(`the clip of ${id} is still ${status}`);
        await delay(500);
    }
}

// ------------------------------------------------------------------------------------------ the phases

async function seed() {
    const p = await plugin();
    check('Bookmarks 1.0.0.0 is the one loaded, Active', p.length === 1 && p[0].Version === '1.0.0.0' && p[0].Status === 'Active', p);
    const long = await movie('admin', 'Long Test Movie (2021)');
    const chaptered = await movie('viewer', 'Chaptered Movie (2022)');
    const tagged = await movie('viewer2', 'Tagged Movie (2012) [tmdbid-900001]');
    for (const who of ['admin', 'viewer', 'viewer2']) {
        for (const b of await ok(who, 'GET', '/Bookmarks')) if (/upgrade-probe/.test(b.Note || '')) await ok(who, 'DELETE', `/Bookmarks/${b.Id}`, { query: { deleteClip: 'true' } });
    }
    const make = async (who, item, start, end, note, clip) => {
        const b = await ok(who, 'POST', '/Bookmarks', { body: { ItemId: item.Id, StartTicks: start * TICKS, EndTicks: end === null ? null : end * TICKS, Note: note } });
        if (clip) await ok(who, 'POST', `/Bookmarks/${b.Id}/Clip`, { body: { Mode: clip } });
        return b.Id;
    };
    const made = {
        short: await make('admin', long, 10, null, 'upgrade-probe Bank job', null),
        fast: await make('admin', long, 20, 26, 'upgrade-probe Heist scene, take 2', 'Fast'),
        accurate: await make('admin', long, 30, 36, 'upgrade-probe first line\nsecond line', 'Accurate'),
        long: await make('admin', long, 40, null, LONG_NOTE, null),
        none: await make('admin', long, 45, null, null, null),
        accents: await make('admin', long, 50, 54, 'upgrade-probe Amélie, café scène ✂ 日本語', 'Fast'),
        viewer: await make('viewer', chaptered, 13, null, 'upgrade-probe viewer note', null),
        viewer2: await make('viewer2', tagged, 5, 9, 'upgrade-probe viewer2 range', 'Fast')
    };
    for (const [who, id] of [['admin', made.fast], ['admin', made.accurate], ['admin', made.accents], ['viewer2', made.viewer2]]) await waitClip(who, id);
    const manifest = { made, users: {}, clips: {} };
    for (const who of ['admin', 'viewer', 'viewer2']) {
        manifest.users[who] = await list(who);
        for (const b of manifest.users[who]) if (b.Clip && b.Clip.Status === 'Completed') manifest.clips[b.Id] = { who, ...(await download(who, b.Id)) };
    }
    fs.mkdirSync(workRoot, { recursive: true });
    fs.writeFileSync(manifestFile, JSON.stringify(manifest, null, 2));
    const clips = Object.values(manifest.clips);
    check('seeded through 1.0.0\'s API: admin\'s six (a short one-line note, a multi-line one, one over 100 characters, none, accents and scripts; Fast, Accurate and Fast clips), viewer\'s one and viewer2\'s Fast clip, every clip Completed and downloading',
        manifest.users.admin.filter((b) => /upgrade-probe/.test(b.Note || '') || b.Note === null).length >= 6 && clips.length >= 4 && clips.every((c) => c.status === 200 && c.size > 0),
        { counts: Object.fromEntries(Object.entries(manifest.users).map(([k, v]) => [k, v.length])), clips: clips.map((c) => ({ who: c.who, size: c.size })) });
    const doc = readJson(path.join(dataFolder, 'bookmarks.json'));
    check('bookmarks.json is 1.0\'s schema (version 1, notes, no names)', (doc.Version === 1 || doc.Version === undefined) && !(doc.Bookmarks || []).some((b) => 'Name' in b), { Version: doc.Version, Bookmarks: (doc.Bookmarks || []).length });
}

function expectedNameAndNote(note) {
    if (note === null) return { Name: null, Note: null };
    const trimmed = note.trim();
    if (trimmed.length > 0 && trimmed.length <= 100 && !/[\r\n]/.test(trimmed)) return { Name: trimmed.normalize('NFC').replace(/\t/g, ' ').replace(/ {2,}/g, ' '), Note: null };
    return { Name: null, Note: note };
}

async function compareLists(label, transform) {
    const manifest = readJson(manifestFile);
    const differences = [];
    for (const who of ['admin', 'viewer', 'viewer2']) {
        const now = await list(who);
        const expected = manifest.users[who].map(transform);
        if (now.length !== expected.length) differences.push({ who, count: [now.length, expected.length] });
        for (const e of expected) {
            const n = now.find((x) => x.Id === e.Id);
            if (!n) { differences.push({ who, missing: e.Id }); continue; }
            for (const k of ['ItemId', 'StartTicks', 'EndTicks', 'Note', 'Name']) if (JSON.stringify(n[k]) !== JSON.stringify(e[k])) differences.push({ who, id: e.Id, field: k, now: n[k], expected: e[k] });
            if (Date.parse(n.CreatedAt) !== Date.parse(e.CreatedAt)) differences.push({ who, id: e.Id, field: 'CreatedAt', now: n.CreatedAt, expected: e.CreatedAt });
            if (JSON.stringify(n.Clip && { Status: n.Clip.Status, Mode: n.Clip.Mode, FileName: n.Clip.FileName, FileSize: n.Clip.FileSize })
                !== JSON.stringify(e.Clip && { Status: e.Clip.Status, Mode: e.Clip.Mode, FileName: e.Clip.FileName, FileSize: e.Clip.FileSize })) differences.push({ who, id: e.Id, field: 'Clip', now: n.Clip, expected: e.Clip });
        }
    }
    const downloads = [];
    for (const [id, c] of Object.entries(manifest.clips)) {
        const d = await download(c.who, id);
        downloads.push({ id, who: c.who, status: d.status, same: d.sha256 === c.sha256, size: d.size });
    }
    check(`${label}: every user's bookmarks as 1.0 had them (ids, videos, times, clips)${transform === identity ? '' : ', short one-line notes now names'}`, differences.length === 0, { differences: differences.slice(0, 20) });
    check(`${label}: every clip file downloads byte for byte as before (${downloads.length})`, downloads.length >= 4 && downloads.every((d) => d.status === 200 && d.same), downloads);
}

const identity = (b) => ({ ...b, Name: null });

async function upgraded() {
    const p = await plugin();
    check('Bookmarks 1.2.2.0 is the one loaded, Active (1.0.0 removed by the install)', p.length === 1 && p[0].Version === '1.2.2.0' && p[0].Status === 'Active', p);
    const diagnostics = await ok('admin', 'GET', '/Bookmarks/Admin/Diagnostics');
    const store = diagnostics.Store || {};
    const copies = fs.readdirSync(dataFolder).filter((f) => /^bookmarks\.json\.v1-\d{14}$/.test(f));
    const v1 = fs.readFileSync(path.resolve(opts.v1));
    const copyBytes = copies.length === 1 ? fs.readFileSync(path.join(dataFolder, copies[0])) : null;
    evidence.store = { Status: store.Status, Version: store.Version, CurrentVersion: store.CurrentVersion, ReadOnly: store.ReadOnly, Message: store.Message, KeptFiles: store.KeptFiles, copies };
    // On the start that upgraded the file Diagnostics says Migrated with the version it loaded (1); on a later one Ok at 2.
    check('Diagnostics: the store at schema 2, writable, Migrated from version 1 (the start that upgraded it) or Ok at 2 (a later one), the .v1-* copy listed as kept',
        store.CurrentVersion === 2 && store.ReadOnly === false && ((store.Status === 'Migrated' && store.Version === 1) || (store.Status === 'Ok' && store.Version === 2))
        && (store.KeptFiles || []).some((f) => copies.includes(String(f.Name || ''))), evidence.store);
    check('exactly one bookmarks.json.v1-* copy, byte for byte the 1.0 file' + (opts.expectCopy ? ', the one the first upgrade made (reused)' : ''),
        copies.length === 1 && !!copyBytes && copyBytes.equals(v1) && (!opts.expectCopy || copies[0] === opts.expectCopy), { copies, sha: copyBytes && sha(copyBytes), v1: sha(v1) });
    const doc = readJson(path.join(dataFolder, 'bookmarks.json'));
    check('bookmarks.json is schema 2', doc.Version === 2, { Version: doc.Version });
    await compareLists('after the upgrade', (b) => ({ ...b, ...expectedNameAndNote(b.Note) }));
    const caps = await ok('viewer', 'GET', '/Bookmarks/Capabilities');
    check('the 1.1 capabilities (viewer): names up to 100 characters, My Bookmarks offered, Shift+B', caps.MaxNameLength === 100 && caps.Pages && caps.Pages.Bookmarks === true && caps.Shortcuts.BookmarkNow === 'Shift+B', { MaxNameLength: caps.MaxNameLength, Pages: caps.Pages, Shortcuts: caps.Shortcuts });
}

async function downgraded() {
    const p = await plugin();
    check('Bookmarks 1.0.0.0 is the one loaded again, Active', p.length === 1 && p[0].Version === '1.0.0.0' && p[0].Status === 'Active', p);
    const doc = readJson(path.join(dataFolder, 'bookmarks.json'));
    check('bookmarks.json is the 1.0 file again (version 1, no names)', (doc.Version === 1 || doc.Version === undefined) && !(doc.Bookmarks || []).some((b) => 'Name' in b), { Version: doc.Version });
    await compareLists('back on 1.0.0 with the .v1-* copy', identity);
}

async function main() {
    let failure = null;
    try {
        if (opts.phase === 'seed') await seed();
        else if (opts.phase === 'upgraded') await upgraded();
        else await downgraded();
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    }
    const pass = !failure && checks.every((c) => c.pass);
    const result = { pass, passed: checks.filter((c) => c.pass).length, total: checks.length, checks, evidence };
    const text = JSON.stringify(result, null, 2);
    if (opts.out) {
        fs.mkdirSync(path.dirname(path.resolve(opts.out)), { recursive: true });
        fs.writeFileSync(opts.out, text);
    }
    console.log(text);
    process.exit(pass ? 0 : 1);
}

await main();
