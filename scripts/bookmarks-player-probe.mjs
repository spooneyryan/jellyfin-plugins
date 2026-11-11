#!/usr/bin/env node
// Bookmarks' player naming flow in a running sandbox, in the web client, as viewer (a
// non-admin): the webchecks player-naming.json (the quick bookmark without pausing, "Name it", the name dialog with the
// chapter's suggestion, keys inside it kept from the player, Save, "Always ask for a name", the dialog at once, a generic
// chapter name never suggested, Escape, "Pause while naming") and player-buttons-off.json (ShowPlayerButtons off: no
// buttons, Shift+B still works), with the server's side of what they show: the capabilities, the preferences,
// bookmarks.json (read only) and the server log. Node 22 built-ins only. Loopback only, never port 8096. The users'
// tokens come from credentials.json and are never printed.
//
//   node scripts/bookmarks-player-probe.mjs --sandbox default [--out result.json] [--skip-buttons-off]
//
// It changes Bookmarks' settings in the sandbox through the plugin configuration API (ShowPlayerButtons,
// AskForNameDefault and SuggestChapterNames, as the webchecks need them) and viewer's own preferences, and puts both back
// as they were in a finally; the bookmarks it and its webchecks make on "Chaptered Movie (2022)" are deleted at the
// end (every bookmark of viewer on that movie; names start with "player-probe"). It needs that movie in the library
// (scripts/New-SandboxMedia.ps1 writes it with embedded chapters): when the file is there but the library
// doesn't know it yet, the probe scans once. It never restarts or deploys anything.
//
// Checks:
//   1. the capabilities for viewer with the probe's settings and the default preferences;
//   2. player-naming.json (viewer, Chaptered Movie);
//   3. the server's side: the named bookmark in bookmarks.json (its Name, no note), viewer's preferences as the webcheck
//      left them (AskForName true, PauseWhileNaming true);
//   4. player-buttons-off.json (ShowPlayerButtons off), unless --skip-buttons-off;
//   5. no Error of Bookmarks or the kit in the server log, no line of theirs at Information or above naming a bookmark;
//      clean-up (settings and preferences put back, the bookmarks deleted).
// It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PROBE = 'player-probe';
const MOVIE = 'Chaptered Movie (2022)';
const MOVIE_FILE = path.join('Movies', MOVIE, MOVIE + '.mkv');

function usage() {
    return 'usage: node scripts/bookmarks-player-probe.mjs --sandbox <name> [--out <file>] [--skip-buttons-off]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, buttonsOff: true };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--skip-buttons-off') o.buttonsOff = false;
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const mediaRoot = path.join(repoRoot, '.sandbox', 'media');
const workRoot = path.join(sandboxRoot, 'bookmarks-player-probe');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const logDir = path.resolve(state.LogDir || path.join(sandboxRoot, 'log'));
if (!logDir.startsWith(sandboxRoot + path.sep)) throw new Error(`refusing to read the log folder ${logDir}: not in the sandbox`);
const storeFile = path.join(sandboxRoot, 'data', 'data', 'bookmarks', 'bookmarks.json');
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}
const pluginGuid = /^guid:\s*"([0-9a-f-]{36})"/m.exec(fs.readFileSync(path.join(repoRoot, 'plugins', 'Bookmarks', 'build.yaml'), 'utf8'))[1];

function assertInside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error(`refusing to work in ${p}`);
    return full;
}
for (const p of [storeFile, workRoot]) assertInside(p, sandboxRoot);

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() } };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// Jellyfin writes GUIDs without dashes ("N"); ASP.NET Core's JSON and bookmarks.json write them with dashes ("D").
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) {
        if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    }
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="bookmarks-player-probe", Device="sandbox", DeviceId="bookmarks-player-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const text = await res.text();
    let json = null;
    if (text && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, json, text };
}

// ------------------------------------------------------------------------------------------ settings (restored)

const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;

async function readConfiguration() {
    const r = await call('admin', 'GET', CONFIGURATION_PATH);
    if (r.status !== 200 || !r.json) throw new Error(`GET ${CONFIGURATION_PATH}: ${r.status}`);
    return r.json;
}

// The whole configuration posted back with the given settings changed, as Set-SandboxPluginConfig.ps1 does; read back.
async function setConfiguration(changes) {
    const configuration = await readConfiguration();
    for (const [key, value] of Object.entries(changes)) {
        if (!(key in configuration)) throw new Error(`Bookmarks has no setting ${key}`);
        configuration[key] = value;
    }
    const r = await call('admin', 'POST', CONFIGURATION_PATH, { body: configuration });
    if (r.status !== 204) throw new Error(`POST ${CONFIGURATION_PATH}: ${r.status} ${r.text.slice(0, 200)}`);
    const back = await readConfiguration();
    for (const [key, value] of Object.entries(changes)) {
        if (back[key] !== value) throw new Error(`${key} did not read back as ${JSON.stringify(value)}`);
    }
    return back;
}

async function readPreferences(who) {
    const r = await call(who, 'GET', '/Bookmarks/Preferences');
    if (r.status !== 200 || !r.json) throw new Error(`GET /Bookmarks/Preferences as ${who}: ${r.status}`);
    return r.json;
}

async function setPreferences(who, preferences) {
    const r = await call(who, 'POST', '/Bookmarks/Preferences', { body: preferences });
    if (r.status !== 204) throw new Error(`POST /Bookmarks/Preferences as ${who}: ${r.status} ${r.text.slice(0, 200)}`);
}

// ------------------------------------------------------------------------------------------ the library

async function findMovie() {
    const r = await call('admin', 'GET', '/Items', {
        query: { userId: users.admin.id, Recursive: 'true', IncludeItemTypes: 'Movie', SearchTerm: 'Chaptered Movie', Fields: 'Path,Chapters', EnableImages: 'false', EnableUserData: 'false' }
    });
    if (r.status !== 200) throw new Error(`GET /Items: ${r.status}`);
    return (r.json.Items || []).find((m) => typeof m.Path === 'string' && m.Path.split(/[\\/]/).includes(MOVIE)) || null;
}

async function scan(label) {
    const started = Date.now();
    const r = await call('admin', 'POST', '/Library/Refresh');
    if (r.status !== 204) throw new Error(`POST /Library/Refresh: ${r.status}`);
    await delay(1500);
    for (;;) {
        const tasks = await call('admin', 'GET', '/ScheduledTasks', { query: { isHidden: 'false' } });
        const task = (tasks.json || []).find((t) => t.Key === 'RefreshLibrary');
        const last = task && task.LastExecutionResult;
        if (task && task.State === 'Idle' && last && Date.parse(last.EndTimeUtc) >= started - 1000) {
            evidence.scan = { label, seconds: Math.round((Date.now() - started) / 100) / 10, status: last.Status };
            if (last.Status !== 'Completed') throw new Error(`the scan "${label}" ended ${last.Status}`);
            await delay(2500);
            return;
        }
        if (Date.now() - started > 300000) throw new Error(`the scan "${label}" did not finish within 300 s`);
        await delay(500);
    }
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

// The record as bookmarks.json holds it (read only).
function stored(id) {
    const doc = readJson(storeFile);
    return (doc.Bookmarks || []).find((b) => guid(b.Id) === guid(id)) || null;
}

// A webcheck of scripts/webchecks (`<name>.json`); its result and screenshots are kept in the work folder.
function webcheck(name) {
    const out = path.join(workRoot, `webcheck-${name}.json`);
    const started = Date.now();
    const w = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', `${name}.json`, '--out', out], {
        cwd: repoRoot, encoding: 'utf8', timeout: 600000, windowsHide: true
    });
    let result = null;
    try { result = readJson(out); } catch { result = null; }
    for (const shot of (result && result.screenshots) || []) {
        if (fs.existsSync(shot)) fs.copyFileSync(shot, path.join(workRoot, path.basename(shot)));
    }
    const failed = result && result.steps ? result.steps.filter((s) => !s.ok).map((s) => ({ index: s.index, name: s.name, error: s.error })) : null;
    const value = (stepName) => (result && result.steps ? (result.steps.find((s) => s.name === stepName) || {}).value : undefined);
    return {
        ok: w.status === 0 && !!result && result.ok === true,
        exit: w.status,
        seconds: Math.round((Date.now() - started) / 1000),
        steps: result && result.steps ? result.steps.length : 0,
        failed,
        failedRequests: result && result.failedRequests ? result.failedRequests.length : null,
        closed: result && result.closed,
        value
    };
}

// ------------------------------------------------------------------------------------------ the run

const original = { configuration: null, preferences: null };
let movie = null;

async function run() {
    movie = await findMovie();
    if (!movie) {
        if (!fs.existsSync(path.join(mediaRoot, MOVIE_FILE))) throw new Error(`${MOVIE_FILE} is not in the sandbox media; run ./scripts/New-SandboxMedia.ps1`);
        await scan(`${MOVIE} not in the library yet`);
        movie = await findMovie();
        if (!movie) throw new Error(`${MOVIE} is still not in the library after a scan`);
    }
    evidence.movie = { id: guid(movie.Id), chapters: (movie.Chapters || []).map((c) => [c.StartPositionTicks / 10_000_000, c.Name]) };

    // ---- the settings the webchecks need, and viewer's preferences at the defaults
    original.configuration = await readConfiguration();
    original.preferences = await readPreferences('viewer');
    const wanted = { ShowPlayerButtons: true, AskForNameDefault: false, SuggestChapterNames: true };
    if (Object.entries(wanted).some(([k, v]) => original.configuration[k] !== v)) await setConfiguration(wanted);
    await setPreferences('viewer', { PauseWhileNaming: false });

    // ---- 1. the capabilities
    const capabilities = (await call('viewer', 'GET', '/Bookmarks/Capabilities')).json;
    evidence.capabilities = capabilities && { AskForName: capabilities.AskForName, AskForNameIsDefault: capabilities.AskForNameIsDefault, PauseWhileNaming: capabilities.PauseWhileNaming, ShowPlayerButtons: capabilities.ShowPlayerButtons, SuggestChapterNames: capabilities.SuggestChapterNames, BookmarkNow: capabilities.Shortcuts && capabilities.Shortcuts.BookmarkNow };
    check('1. the capabilities for viewer: AskForName off and the server default, no pause, the buttons on, chapter names suggested, Shift+B',
        !!capabilities && capabilities.AskForName === false && capabilities.AskForNameIsDefault === true && capabilities.PauseWhileNaming === false
        && capabilities.ShowPlayerButtons === true && capabilities.SuggestChapterNames === true && capabilities.Shortcuts.BookmarkNow === 'Shift+B',
        evidence.capabilities);

    // ---- 2. the naming flow in the web client
    const naming = webcheck('player-naming');
    evidence.playerNaming = { ok: naming.ok, exit: naming.exit, seconds: naming.seconds, steps: naming.steps, failed: naming.failed, failedRequests: naming.failedRequests, closed: naming.closed,
        chapters: naming.value('its chapters, as ApiClient.getItem returns them (SB3)'),
        first: naming.value('one bookmark at once, unnamed, and the toast with Name it; playback not paused'),
        dialog: naming.value("the name dialog: 'Bank job' suggested, focused and fully selected; 'Always ask for a name' unticked; More…, Skip, Save"),
        keysBefore: naming.value("focus the Save button, note the video's state"),
        keysAfter: naming.value("the video's state afterwards"),
        named: naming.value('named: the dialog gone, the toast, the server\'s name with the time unchanged'),
        second: naming.value('the second bookmark and its toast'),
        third: naming.value("Always ask: the dialog at once, no toast, with an empty field ('Chapter 4' is never suggested, SB3)"),
        pausedDialog: naming.value("the dialog at once with 'Opening', and the video paused by the dialog"),
        doubleDialog: naming.value("one dialog, with 'Bank job', the video paused"),
        double: naming.value('still one dialog, and one bookmark more, not two'),
        bookmarks: naming.value('five bookmarks of this run, one named') };
    check('2. player-naming.json (viewer): Shift+B creates at once without pausing, Name it opens the dialog with the chapter\'s title selected, keys inside it never reach the player, Enter saves, Always ask saves the preference and the next Shift+B opens the dialog at once (Chase; nothing for the generic Chapter 4), Escape leaves it unnamed, Pause while naming pauses and resumes, a double activation of the button makes one dialog and one bookmark',
        naming.ok, { exit: naming.exit, failed: naming.failed });

    // ---- 3. the server's side
    const named = evidence.playerNaming.first && stored(evidence.playerNaming.first.id);
    const preferences = await readPreferences('viewer');
    evidence.server = { named: named && { Name: named.Name, Note: named.Note || null, StartTicks: named.StartTicks }, preferences };
    check('3. bookmarks.json holds the named bookmark with the typed name and no note; viewer\'s preferences are as the dialog and the webcheck set them (AskForName true, PauseWhileNaming true)',
        !!named && named.Name === `${PROBE} Vault` && !named.Note && preferences.AskForName === true && preferences.PauseWhileNaming === true,
        evidence.server);

    // ---- 4. ShowPlayerButtons off
    if (!opts.buttonsOff) return;
    await setPreferences('viewer', { PauseWhileNaming: false });
    await setConfiguration({ ShowPlayerButtons: false });
    const off = webcheck('player-buttons-off');
    evidence.playerButtonsOff = { ok: off.ok, exit: off.exit, seconds: off.seconds, steps: off.steps, failed: off.failed, failedRequests: off.failedRequests, closed: off.closed,
        row: off.value('the button row'),
        double: off.value('one bookmark more and one toast more, not two of each') };
    check('4. player-buttons-off.json (viewer): with ShowPlayerButtons off the button row has none of ours while jellyfin-web\'s are there, Shift+B still bookmarks with the toast, and two Shift+B presses in one go make one bookmark',
        off.ok, { exit: off.exit, failed: off.failed });
}

async function deleteBookmarks() {
    const left = [];
    if (!movie) return left;
    for (const who of ['viewer']) {
        const r = await call(who, 'GET', '/Bookmarks', { query: { itemId: movie.Id } });
        for (const b of r.json || []) {
            await call(who, 'DELETE', `/Bookmarks/${b.Id}`);
            left.push(guid(b.Id));
        }
    }
    return left;
}

async function main() {
    fs.mkdirSync(workRoot, { recursive: true });
    const startLog = logPosition();
    let failure = null;
    try {
        await run();
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        const cleanup = { deleted: [], configurationRestored: null, preferencesRestored: null };
        try {
            cleanup.deleted = await deleteBookmarks();
            if (original.configuration) {
                const back = await setConfiguration({ ShowPlayerButtons: original.configuration.ShowPlayerButtons, AskForNameDefault: original.configuration.AskForNameDefault, SuggestChapterNames: original.configuration.SuggestChapterNames });
                cleanup.configurationRestored = { ShowPlayerButtons: back.ShowPlayerButtons, AskForNameDefault: back.AskForNameDefault, SuggestChapterNames: back.SuggestChapterNames };
            }
            if (original.preferences) {
                await setPreferences('viewer', original.preferences);
                cleanup.preferencesRestored = await readPreferences('viewer');
            }
        } catch (error) {
            check('clean-up: the settings, the preferences and the bookmarks put back', false, String(error));
        }
        evidence.cleanup = cleanup;
        const text = logSince(startLog);
        const lines = text.split('\n');
        const errors = lines.filter((l) => /\[ERR\]|\[FTL\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l));
        const naming = lines.filter((l) => /\[(INF|WRN|ERR|FTL)\]/.test(l) && /Jellyfin\.Plugin\.Bookmarks|Kit\./.test(l) && l.includes(PROBE));
        check('5. no Error of Bookmarks or the kit in the server log during the run, and no line of theirs at Information or above naming a bookmark',
            errors.length === 0 && naming.length === 0, { errors: errors.slice(0, 5), naming: naming.slice(0, 5) });
        const stillThere = movie ? ((await call('viewer', 'GET', '/Bookmarks', { query: { itemId: movie.Id } })).json || []).length : 0;
        const restored = !original.configuration || (cleanup.configurationRestored && ['ShowPlayerButtons', 'AskForNameDefault', 'SuggestChapterNames'].every((k) => cleanup.configurationRestored[k] === original.configuration[k]));
        const preferencesBack = !original.preferences || (cleanup.preferencesRestored && cleanup.preferencesRestored.PauseWhileNaming === original.preferences.PauseWhileNaming
            && (cleanup.preferencesRestored.AskForName ?? null) === (original.preferences.AskForName ?? null));
        check('5. clean-up: the settings and viewer\'s preferences as they were, viewer\'s bookmarks on the movie deleted',
            restored && preferencesBack && stillThere === 0, { restored, preferencesBack, stillThere, deleted: cleanup.deleted.length });
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
