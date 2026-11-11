#!/usr/bin/env node
// Access Roles' settings enforcement (the settings rows of the bypass matrix, and /Me) in a running sandbox, as a locked non-admin, an unlocked non-admin and a locked administrator (and an API key):
//   1. Every settings section, one at a time (a lock-only role of the probe's, its Locks set to that section): Profile's
//      password (both routes), user image (all six routes) and name change (a case-only one too) refused 403 Locked for
//      the locked user and allowed for the others; the whole UserConfiguration and the whole usersettings document posted
//      with every value changed: the locked user's section's fields and keys keep their stored values (both configuration
//      routes, UpdateUser's configuration, the usersettings document under a GUID id and with client EMBY, HOMESECTION0
//      and a home section past the stored layout) and nothing else does; the other two keep every change; Quick
//      Connect's authorization refused only for the locked user; Jellyfin Enhanced's settings saves refused when JE is
//      installed; the two hidden-only sections change nothing on the server.
//   2. The variants: an invalid body answers the core's 400 (not 500, not a refusal) for a merge, a reject refuses first;
//      an API key changes a locked user's settings; an unknown action passes.
//   3. GET /AccessRoles/Me as each of them (403 for the API key, 401 anonymously); Diagnostics' counters and the rule
//      table's self-check; the refusals and reverts audited as RequestsBlocked, one line per user, rule, action and outcome
//      per 10-minute window (a flood of --refusals password attempts included), waited for unless --no-audit-wait.
//   4. The server-settings rows of the hardening, since 0.9.7 enforced (a separate hardening probe, not in this repository, checks them in full): a non-admin's configuration
//      reads refused but for encoding, the anonymous public list trimmed, GET /System/Info without the server's paths and
//      GET /Users with only the caller under AdminSurfaces and UserDirectory. The restart is never sent (the
//      throwaway access-scratch sandbox only).
//   5. With --webcheck, the web client as the credentials.json user `viewer` given the probe's role (every section): Home
//      and the Settings menu load (every link hidden, the note) and each settings page ends on Home (the web guard) with no
//      failed request of Access Roles', saves of the configuration and the usersettings document through jellyfin-web's
//      ApiClient leave the server's values as they were, with the guard's toast, and, with the guard switched off for the
//      page (an init script), the Home screen and Display pages' own form saves are kept too.
//   6. Every user the probe didn't create: their policy never written; viewer's settings as before.
// It writes only what is its own: its users (lockprobe-..., random passwords and their sign-in tokens kept in memory, never
// printed or saved), their passwords, images, names and settings, its role ("Lock probe ..."), its API key
// ("lockprobe-key", revoked in a finally), and viewer's assignment (removed in a finally; viewer's settings are written
// back only if a merge failed to keep them). Node 22 built-ins only. Loopback only, never port 8096.
//
//   node scripts/accessroles-settings-locks-probe.mjs --sandbox access [--webcheck] [--refusals 20] [--no-audit-wait] [--out result.json]
//
// Start the sandbox first (scripts/README.md, this probe's section). Prints one JSON result (pass, checks, transcript,
// evidence) and exits with 0 when every check passed.

import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PLUGIN = 'AccessRoles';
const PREFIX = 'Lock probe ';
const PROBE_USER = 'lockprobe-';
const PROBE_KEY = 'lockprobe-key';
const WEBCHECK_USER = 'viewer';
const LOCKED_TYPE = 'urn:jellyfin-plugin-kit:locked';
const USERSETTINGS_ID = '3ce5b65de116d73165d1efc4a30ec35c';
const WINDOW_MS = 10 * 60 * 1000;
// A 1x1 PNG, the user image the probe's users upload (base64, as the image endpoints read it).
const PNG = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==';

function usage() {
    return 'usage: node scripts/accessroles-settings-locks-probe.mjs --sandbox <name> [--webcheck] [--refusals <n>] [--no-audit-wait] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, webcheck: false, refusals: 20, auditWait: true };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--webcheck') o.webcheck = true;
        else if (a === '--refusals') o.refusals = Number(argv[++i]);
        else if (a === '--no-audit-wait') o.auditWait = false;
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!Number.isInteger(o.refusals) || o.refusals < 1 || o.refusals > 200) throw new Error(`--refusals 1..200. ${usage()}`);
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const credentials = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
if (!credentials.admin || !credentials.admin.token) throw new Error('no admin in credentials.json; run Initialize-Sandbox.ps1');

const yaml = fs.readFileSync(path.join(repoRoot, 'plugins', PLUGIN, 'build.yaml'), 'utf8');
const scalar = (key) => (new RegExp(`^${key}:\\s*"?([^"\\r\\n]+)"?\\s*$`, 'm').exec(yaml) || [])[1];
const plugin = { name: scalar('name'), guid: scalar('guid'), version: scalar('version') };

// The sandbox's log, read only. Inside .sandbox, never under ProgramData\Jellyfin.
function assertInSandbox(p) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), path.resolve(p));
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(path.resolve(p))) throw new Error(`refusing to touch ${p}`);
    return path.resolve(p);
}
const logFolder = assertInSandbox(path.join(sandboxRoot, 'log'));

const checks = [];
const transcript = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

function authHeader(token, deviceId = 'accessroles-settings-locks-probe') {
    return `MediaBrowser Client="accessroles-settings-locks-probe", Device="sandbox", DeviceId="${deviceId}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

// A request as the admin (credentials.json), the probe's API key, one of the probe's users (a token from its sign-in), or
// anonymous (null).
const tokens = new Map();
let apiKey = null;
function request(method, p, user = 'admin', body = null, contentType = 'application/json') {
    return new Promise((resolve, reject) => {
        const token = user === null ? null : user === 'admin' ? credentials.admin.token : user === 'key' ? apiKey : tokens.get(user);
        if (user !== null && !token) throw new Error(`no token for ${user}`);
        const headers = { Authorization: authHeader(token, user && user !== 'admin' && user !== 'key' ? `lockprobe-${user.slice(0, 8)}` : undefined), Accept: 'application/json' };
        if (body !== null) headers['Content-Type'] = contentType;
        const started = Date.now();
        const req = http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let json = null;
                try { json = text && /json/.test(res.headers['content-type'] || '') ? JSON.parse(text) : null; } catch { json = null; }
                resolve({ status: res.statusCode, type: res.headers['content-type'] || '', headers: res.headers, text, json, ms: Date.now() - started });
            });
        });
        req.on('error', reject);
        req.setTimeout(60000, () => req.destroy(new Error(`timeout ${method} ${p}`)));
        if (body !== null) req.write(typeof body === 'string' ? body : JSON.stringify(body));
        req.end();
    });
}

// One admin request of the walkthrough, as curl would send it (the token as <admin>, never its value).
async function step(label, method, p, body = null) {
    const r = await request(method, p, 'admin', body);
    transcript.push({ step: label, curl: `curl -X ${method} "http://127.0.0.1:${port}${baseUrl}${p}" -H "Authorization: MediaBrowser Token=<admin>"${body !== null ? ` -d '${JSON.stringify(body)}'` : ''}`, status: r.status, code: r.json && r.json.Code ? r.json.Code : undefined });
    return r;
}

const idN = (x) => (x || '').replace(/-/g, '').toLowerCase();
const locked = (r) => r.status === 403 && !!r.json && r.json.type === LOCKED_TYPE;
const stable = (value) => JSON.stringify(value, (k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.keys(v).sort().map((key) => [key, v[key]])) : v));

// ---- the probe's own users: the only users whose passwords, images, names and settings it writes ----------------------
const own = new Map();
const secrets = new Map();
async function createOwnUser(name) {
    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);
    const password = crypto.randomBytes(24).toString('base64url');
    const r = await request('POST', '/Users/New', 'admin', { Name: name, Password: password });
    transcript.push({ step: `create the probe's user ${name}`, curl: `curl -X POST "http://127.0.0.1:${port}${baseUrl}/Users/New" -H "Authorization: MediaBrowser Token=<admin>" -d '{"Name":"${name}","Password":"<random>"}'`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.Id) throw new Error(`POST /Users/New ${name}: ${r.status}`);
    own.set(idN(r.json.Id), name);
    secrets.set(idN(r.json.Id), password);
    return r.json;
}

async function deleteOwnUser(id) {
    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);
    const r = await request('DELETE', `/Users/${id}`, 'admin');
    transcript.push({ step: `delete the probe's user ${own.get(idN(id))}`, status: r.status });
    if (r.status !== 204) throw new Error(`delete the probe's user ${id}: ${r.status}`);
    own.delete(idN(id));
    secrets.delete(idN(id));
    tokens.delete(idN(id));
}

// A policy save of one of the probe's users (only to make the probe's administrator one).
async function postOwnPolicy(id, policy) {
    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);
    const r = await request('POST', `/Users/${id}/Policy`, 'admin', policy);
    transcript.push({ step: `set the native policy of the probe's user ${own.get(idN(id))}`, status: r.status });
    if (r.status !== 204) throw new Error(`policy of ${id}: ${r.status}`);
}

// The probe's user signs in with its password (only ever in this request's body); the token stays in memory.
async function signIn(user) {
    const password = secrets.get(idN(user.Id));
    if (!password) throw new Error(`no password for ${user.Name}`);
    const r = await request('POST', '/Users/AuthenticateByName', null, { Username: user.Name, Pw: password });
    transcript.push({ step: `${user.Name} signs in`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.AccessToken) throw new Error(`sign-in of ${user.Name}: ${r.status}`);
    tokens.set(idN(user.Id), r.json.AccessToken);
}

// The settings writes of a probe's user, as that user (or the API key), and the admin's write back of its settings; viewer's
// write back only when a merge failed to keep them (restoreWebcheckUser).
function assertOwnOrWebcheck(id) {
    if (!own.has(idN(id)) && idN(id) !== idN(credentials[WEBCHECK_USER] && credentials[WEBCHECK_USER].id)) throw new Error(`refusing to write the settings of ${id}`);
}

const dtoOf = async (id) => (await request('GET', `/Users/${id}`, 'admin')).json;
const configOf = async (id) => (await dtoOf(id)).Configuration;
async function writeConfigAsAdmin(id, configuration) {
    assertOwnOrWebcheck(id);
    const r = await request('POST', `/Users/Configuration?userId=${id}`, 'admin', configuration);
    if (r.status !== 204) throw new Error(`write back the configuration of ${id}: ${r.status}`);
}

const prefsPath = (id, document = 'usersettings', client = 'emby') => `/DisplayPreferences/${document}?userId=${id}&client=${client}`;
const prefsOf = async (id, document, client) => (await request('GET', prefsPath(id, document, client), 'admin')).json;
async function writePrefsAsAdmin(id, prefs, document, client) {
    assertOwnOrWebcheck(id);
    const r = await request('POST', prefsPath(id, document, client), 'admin', prefs);
    if (r.status !== 204) throw new Error(`write back the display preferences of ${id}: ${r.status}`);
}

// ---- what each section covers on the server (the plugin's SettingsSections) --------------------------------------------
const CONFIG_FIELDS = {
    Profile: ['EnableLocalPassword'],
    DisplayAccount: ['DisplayMissingEpisodes', 'DisplayCollectionsView'],
    Home: ['OrderedViews', 'LatestItemsExcludes', 'MyMediaExcludes', 'GroupedFolders', 'HidePlayedInLatest'],
    Playback: ['AudioLanguagePreference', 'PlayDefaultAudioTrack', 'EnableNextEpisodeAutoPlay', 'RememberAudioSelections', 'RememberSubtitleSelections', 'CastReceiverId'],
    Subtitles: ['SubtitleLanguagePreference', 'SubtitleMode']
};
const ALL_FIELDS = Object.values(CONFIG_FIELDS).flat();
const sectionOfKey = (key) => {
    const k = key.toLowerCase();
    if (k.startsWith('homesection') || k === 'tvhome' || k.startsWith('landing-')) return 'Home';
    if (k === 'skipbacklength' || k === 'skipforwardlength') return 'Playback';
    if (k === 'dashboardtheme' || k === 'useepisodeimagesinnextupandresume') return 'DisplayAccount';
    return null;
};
const SECTIONS = ['Profile', 'DisplayAccount', 'Home', 'Playback', 'Subtitles', 'QuickConnect', 'EnhancedPanel', 'DisplayDevice', 'Controls'];
const SERVER_ENFORCED = ['Profile', 'DisplayAccount', 'Home', 'Playback', 'Subtitles', 'QuickConnect', 'EnhancedPanel'];

// A configuration with every field changed from the one given.
function flipConfig(c, library) {
    const other = (v, a, b) => (v === a ? b : a);
    return {
        ...c,
        EnableLocalPassword: !c.EnableLocalPassword,
        DisplayMissingEpisodes: !c.DisplayMissingEpisodes,
        DisplayCollectionsView: !c.DisplayCollectionsView,
        OrderedViews: (c.OrderedViews || []).length ? [] : [library],
        LatestItemsExcludes: (c.LatestItemsExcludes || []).length ? [] : [library],
        MyMediaExcludes: (c.MyMediaExcludes || []).length ? [] : [library],
        GroupedFolders: (c.GroupedFolders || []).length ? [] : [library],
        HidePlayedInLatest: !c.HidePlayedInLatest,
        AudioLanguagePreference: other(c.AudioLanguagePreference, 'fra', 'deu'),
        PlayDefaultAudioTrack: !c.PlayDefaultAudioTrack,
        EnableNextEpisodeAutoPlay: !c.EnableNextEpisodeAutoPlay,
        RememberAudioSelections: !c.RememberAudioSelections,
        RememberSubtitleSelections: !c.RememberSubtitleSelections,
        // CastReceiverId stays: the core stores only a receiver the server configures (UserManager.cs:629-633).
        SubtitleLanguagePreference: other(c.SubtitleLanguagePreference, 'ita', 'spa'),
        SubtitleMode: other(c.SubtitleMode, 'Always', 'OnlyForced')
    };
}

// A usersettings document with every section's keys changed, the core's home-section quirks included.
function flipPrefs(p, library) {
    const prefs = { ...(p.CustomPrefs || {}) };
    const other = (v, a, b) => (v === a ? b : a);
    prefs.homesection0 = other(prefs.homesection0, 'nextup', 'resume');
    prefs.homesection1 = other(prefs.homesection1, 'latestmedia', 'none');
    prefs.tvhome = other(prefs.tvhome, 'lock-a', 'lock-b');
    prefs[`landing-${idN(library)}`] = other(prefs[`landing-${idN(library)}`], 'favorites', 'movies');
    prefs.skipBackLength = other(prefs.skipBackLength, '5000', '20000');
    prefs.skipForwardLength = other(prefs.skipForwardLength, '15000', '45000');
    prefs.dashboardTheme = other(prefs.dashboardTheme, 'dark', 'light');
    prefs.useEpisodeImagesInNextUpAndResume = other(prefs.useEpisodeImagesInNextUpAndResume, 'true', 'false');
    prefs.enableNextVideoInfoOverlay = other(prefs.enableNextVideoInfoOverlay, 'True', 'False');
    return { ...p, CustomPrefs: prefs };
}

const sameValue = (a, b) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null) || ((a ?? '') === '' && (b ?? '') === '');
// Which fields the server kept at their stored values (of the ones the post changed).
const keptFields = (before, after, posted) => ALL_FIELDS.filter((f) => !sameValue(before[f], posted[f]) && sameValue(after[f], before[f]));
const changedKeys = (before, posted) => Object.keys({ ...(before.CustomPrefs || {}), ...(posted.CustomPrefs || {}) }).filter((k) => !sameValue((before.CustomPrefs || {})[k], (posted.CustomPrefs || {})[k]));
const keptKeys = (before, after, posted) => changedKeys(before, posted).filter((k) => sameValue((after.CustomPrefs || {})[k], (before.CustomPrefs || {})[k]));
const sectionsOf = (list, of) => [...new Set(list.map(of))].sort();

// ---- the probe's role, assignments and API key ------------------------------------------------------------------------
let role = null;
async function setLocks(locks) {
    const current = (await request('GET', `/AccessRoles/Admin/Roles/${role.Id}`, 'admin')).json;
    const r = await step(`the role locks ${locks.join(', ') || 'nothing'}`, 'PUT', `/AccessRoles/Admin/Roles/${role.Id}`, { Name: current.Name, Description: current.Description, Locks: locks, Libraries: { Mode: 'NotManaged', Refs: [] }, IfUpdatedAt: current.UpdatedUtc });
    if (r.status !== 200) throw new Error(`PUT the role's locks: ${r.status} ${r.text.slice(0, 200)}`);
}

const usersRows = async () => (await request('GET', '/AccessRoles/Admin/Users', 'admin')).json || [];
async function assignRole(user) {
    const row = (await usersRows()).find((u) => idN(u.UserId) === idN(user.Id)) || {};
    const r = await step(`${user.Name} gets the probe's role`, 'PUT', `/AccessRoles/Admin/Users/${user.Id}`, { RoleIds: [role.Id], IfUpdatedAt: row.UpdatedUtc });
    if (r.status !== 200) throw new Error(`assign the role to ${user.Name}: ${r.status} ${r.text.slice(0, 200)}`);
}

async function createKey() {
    const r = await request('POST', `/Auth/Keys?app=${PROBE_KEY}`, 'admin');
    transcript.push({ step: `create the API key ${PROBE_KEY}`, status: r.status });
    // The listing holds every key's token: only the probe's own is kept, in memory.
    const keys = (((await request('GET', '/Auth/Keys', 'admin')).json || {}).Items || []).filter((k) => k.AppName === PROBE_KEY);
    apiKey = keys.length ? keys[keys.length - 1].AccessToken : null;
    return r.status === 204 && !!apiKey;
}

async function revokeKeys() {
    let revoked = 0;
    for (const key of (((await request('GET', '/Auth/Keys', 'admin')).json || {}).Items || []).filter((k) => k.AppName === PROBE_KEY)) {
        const r = await request('DELETE', `/Auth/Keys/${key.AccessToken}`, 'admin');
        transcript.push({ step: `revoke the API key ${PROBE_KEY}`, status: r.status });
        if (r.status === 204) revoked++;
    }
    apiKey = null;
    return revoked;
}

// ---- the audit log and the server log ------------------------------------------------------------------------------
const diagnostics = async () => (await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin')).json || {};
const lastSeq = async () => (((await diagnostics()).Audit || {}).LastSeq || 0);
async function auditSince(seq, action) {
    const r = await request('GET', `/AccessRoles/Admin/Audit?limit=200&action=${action}`, 'admin');
    return ((r.json && r.json.Items) || []).filter((e) => e.Seq > seq).reverse();
}

function logSinceStart() {
    const since = Date.parse(state.StartedUtc || '1970-01-01T00:00:00Z');
    const lines = [];
    for (const file of fs.readdirSync(logFolder).filter((f) => /^log_\d{8}\.log$/.test(f)).sort()) {
        for (const line of fs.readFileSync(path.join(logFolder, file), 'utf8').split(/\r?\n/)) {
            const m = /^\[(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2})\] \[(\w{3})\]/.exec(line);
            if (m && Date.parse(`${m[1]}T${m[2]}${m[3]}`) >= since - 1000) lines.push({ level: m[4], text: line });
        }
    }
    return lines;
}

// What the probe made the filter refuse or revert, per user, rule, action and outcome (to compare with the audit).
const expectedBlocks = new Map();
function expectBlock(user, rule, endpoint, outcome, count = 1) {
    const key = `${idN(user.Id)}|${rule}|${endpoint}|${outcome}`;
    expectedBlocks.set(key, (expectedBlocks.get(key) || 0) + count);
}

// ---- cleanup: everything of the probe's, through the API --------------------------------------------------------------
const cleanupErrors = [];
async function cleanup(label) {
    for (const u of (await usersRows()).filter((x) => x.State !== 'NotManaged' && (x.Name.toLowerCase().startsWith(PROBE_USER) || x.Name === WEBCHECK_USER))) {
        // Lock-only assignments: Keep writes nothing.
        await step(`${label}: unmanage ${u.Name} (Keep)`, 'POST', `/AccessRoles/Admin/Users/${u.UserId}/Unmanage`, { Choice: 'Keep' });
    }
    for (const r of ((await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || []).filter((x) => x.Name.startsWith(PREFIX))) {
        await step(`${label}: delete ${r.Name}`, 'DELETE', `/AccessRoles/Admin/Roles/${r.Id}?onUnmanage=Keep`);
    }
    for (const u of ((await request('GET', '/Users', 'admin')).json || []).filter((x) => x.Name.toLowerCase().startsWith(PROBE_USER))) {
        own.set(idN(u.Id), u.Name);
        try {
            await deleteOwnUser(u.Id);
        } catch (e) {
            cleanupErrors.push(`${label}: ${e.message}`);
        }
    }
    await revokeKeys();
}

let webcheckUserBefore = null;

async function main() {
    const info = await request('GET', '/System/Info/Public', null);
    check('the sandbox answers, Jellyfin 10.11.5', info.status === 200 && info.json && info.json.Version === '10.11.5', { status: info.status, version: info.json && info.json.Version });
    const installed = (await request('GET', '/Plugins', 'admin')).json || [];
    const ours = installed.find((p) => idN(p.Id) === idN(plugin.guid));
    check('Access Roles is loaded and Active, at the build.yaml version', !!ours && ours.Version === plugin.version && ours.Status === 'Active', ours ? { version: ours.Version, status: ours.Status } : 'not listed');
    evidence.plugins = installed.map((p) => `${p.Name} ${p.Version} ${p.Status}`);
    await cleanup('earlier run');
    const startSeq = await lastSeq();

    const d0 = await diagnostics();
    const ruleTable = d0.RuleTable || {};
    const je = !!(ruleTable.JellyfinEnhanced && ruleTable.JellyfinEnhanced.Installed);
    evidence.ruleTable = { FilterRegistered: ruleTable.FilterRegistered, EnforcementActive: ruleTable.EnforcementActive, Bound: ruleTable.Bound, WithRules: ruleTable.WithRules, Unreviewed: ruleTable.Unreviewed, Classification: ruleTable.Classification, JellyfinEnhanced: ruleTable.JellyfinEnhanced };
    check('the settings locks are enforced, the rule table bound (the filter registered, 110 rule rows, nothing unreviewed)',
        d0.Enforcement && d0.Enforcement.SettingsLocks === true && ruleTable.FilterRegistered === true && ruleTable.EnforcementActive === true && ruleTable.Bound === 110 && (ruleTable.Unreviewed || []).length === 0,
        { enforcement: d0.Enforcement && { SettingsLocks: d0.Enforcement.SettingsLocks, Snapshot: d0.Enforcement.Snapshot }, ruleTable: evidence.ruleTable });
    const classification = ruleTable.Classification || {};
    check('every manifest action classified: 280 without a rule; settings 14, hooks 14, server hardening 73, Strict 11 actions',
        classification.Actions === 390 && classification.NoRule === 280 && (classification.Settings || {}).Actions === 14 && (classification.LibraryHooks || {}).Actions === 14
            && (classification.ServerHardening || {}).Actions === 73 && (classification.Strict || {}).Actions === 11,
        classification);

    // Every user the probe didn't create: their policies before (none may be written), and the webcheck user's settings.
    const others = (await request('GET', '/Users', 'admin')).json || [];
    const othersBefore = Object.fromEntries(others.map((u) => [u.Id, stable(u.Policy)]));
    const library = ((await request('GET', '/Library/VirtualFolders', 'admin')).json || []).filter((l) => l.ItemId).map((l) => l.ItemId)[0] || '11111111111111111111111111111111';

    // The probe's users and role.
    const lockedUser = await createOwnUser(`${PROBE_USER}locked`);
    const openUser = await createOwnUser(`${PROBE_USER}open`);
    const adminUser = await createOwnUser(`${PROBE_USER}admin`);
    const adminPolicy = (await request('GET', `/Users/${adminUser.Id}`, 'admin')).json.Policy;
    await postOwnPolicy(adminUser.Id, { ...adminPolicy, IsAdministrator: true });
    for (const u of [lockedUser, openUser, adminUser]) await signIn(u);
    const created = await step('the probe\'s role, locking nothing yet', 'POST', '/AccessRoles/Admin/Roles', { Name: `${PREFIX}locks`, Locks: [], Libraries: { Mode: 'NotManaged', Refs: [] } });
    role = created.json && created.json.Role;
    if (!role) throw new Error(`POST Roles: ${created.status} ${created.text.slice(0, 200)}`);
    await assignRole(lockedUser);
    await assignRole(adminUser);
    const users = { locked: lockedUser, open: openUser, admin: adminUser };
    const as = (who) => idN(users[who].Id);

    // ---------------------------------------------------------------------------------------------------------------
    // 1. Every section, one at a time.
    const sections = {};
    for (const section of SECTIONS) {
        await setLocks([section]);
        const result = { section };
        const lockedHere = (who) => who !== 'open' && section !== 'DisplayDevice' && section !== 'Controls';

        if (section === 'Profile') {
            const routes = {};
            for (const who of ['locked', 'open', 'admin']) {
                const u = users[who];
                const r = {};
                // The password, both routes: the locked user is refused before the core checks anything.
                const next1 = crypto.randomBytes(18).toString('base64url');
                const pw1 = await request('POST', '/Users/Password', as(who), { CurrentPw: secrets.get(idN(u.Id)), NewPw: next1 });
                if (pw1.status === 204) secrets.set(idN(u.Id), next1);
                const next2 = crypto.randomBytes(18).toString('base64url');
                const pw2 = await request('POST', `/Users/${u.Id}/Password`, as(who), { CurrentPw: secrets.get(idN(u.Id)), NewPw: next2 });
                if (pw2.status === 204) secrets.set(idN(u.Id), next2);
                r.pwAnswers = [pw1.status, pw2.status];
                r.pwLocked = [locked(pw1), locked(pw2)];
                r.pwDetail = pw1.json && pw1.json.detail;
                // The user image, all six routes: upload, delete, by turns.
                const image = [];
                for (const [method, p, body] of [
                    ['POST', '/UserImage', PNG], ['DELETE', `/Users/${u.Id}/Images/Primary/0`, null],
                    ['POST', `/Users/${u.Id}/Images/Primary`, PNG], ['DELETE', `/Users/${u.Id}/Images/Primary`, null],
                    ['POST', `/Users/${u.Id}/Images/Primary/0`, PNG], ['DELETE', '/UserImage', null]]) {
                    const answer = await request(method, p, as(who), body, 'image/png');
                    image.push({ route: `${method} ${p.replace(u.Id, '{userId}')}`, status: answer.status, locked: locked(answer) });
                }
                r.image = image;
                // The name: another one, one in other letter case, then the same one.
                // (The whole user as GET /Users/{id} gives it, as the web's Profile page posts it: UserDto's fields are required.)
                const renamed = await request('POST', `/Users?userId=${u.Id}`, as(who), { ...(await dtoOf(u.Id)), Name: `${u.Name}-r` });
                if (renamed.status === 204) await request('POST', `/Users?userId=${u.Id}`, as(who), { ...(await dtoOf(u.Id)), Name: u.Name });
                const cased = await request('POST', `/Users/${u.Id}`, as(who), { ...(await dtoOf(u.Id)), Name: u.Name.toUpperCase() });
                if (cased.status === 204) await request('POST', `/Users?userId=${u.Id}`, as(who), { ...(await dtoOf(u.Id)), Name: u.Name });
                r.rename = [renamed.status, cased.status];
                r.renameLocked = [locked(renamed), locked(cased)];
                r.nameAfter = ((await request('GET', `/Users/${u.Id}`, 'admin')).json || {}).Name;
                routes[who] = r;
            }

            for (let i = 0; i < 2; i++) expectBlock(lockedUser, 'Settings.Profile', i === 0 ? 'UserController.UpdateUserPassword' : 'UserController.UpdateUserPasswordLegacy', 'Refused');
            for (const e of ['PostUserImage', 'DeleteUserImageByIndexLegacy', 'PostUserImageLegacy', 'DeleteUserImageLegacy', 'PostUserImageByIndexLegacy', 'DeleteUserImage']) expectBlock(lockedUser, 'Settings.Profile', `ImageController.${e}`, 'Refused');
            expectBlock(lockedUser, 'Settings.Profile', 'UserController.UpdateUser', 'Refused');
            expectBlock(lockedUser, 'Settings.Profile', 'UserController.UpdateUserLegacy', 'Refused');
            result.routes = routes;
            const l = routes.locked;
            result.rejects = check('Profile locked: the password (both routes), the user image (all six routes) and a name change (also by letter case only) answer 403 Locked naming the section; the unlocked user and the administrator change all of them',
                l.pwLocked.every(Boolean) && /Profile settings are managed by your server administrator/.test(l.pwDetail || '') && l.image.every((x) => x.locked) && l.renameLocked.every(Boolean) && l.nameAfter === lockedUser.Name
                    && ['open', 'admin'].every((w) => routes[w].pwAnswers.every((s) => s === 204) && routes[w].image.every((x) => x.status === 204) && routes[w].rename.every((s) => s === 204) && routes[w].nameAfter === users[w].Name),
                routes);
        }

        if (section === 'QuickConnect') {
            const answers = {};
            for (const who of ['locked', 'open', 'admin']) {
                const r = await request('POST', '/QuickConnect/Authorize?code=000000', as(who));
                answers[who] = { status: r.status, locked: locked(r) };
            }
            expectBlock(lockedUser, 'Settings.QuickConnect', 'QuickConnectController.AuthorizeQuickConnect', 'Refused');
            result.quickConnect = answers;
            check('QuickConnect locked: authorizing a code answers 403 Locked for the locked user; the unlocked user and the administrator get the core\'s own answer',
                answers.locked.locked && !answers.open.locked && !answers.admin.locked, answers);
        }

        if (section === 'EnhancedPanel') {
            if (!je) {
                result.enhancedPanel = 'Jellyfin Enhanced isn\'t installed in this sandbox: its saves aren\'t routed (unit-tested; run with -Community JellyfinEnhanced)';
            } else {
                const answers = {};
                for (const who of ['locked', 'open', 'admin']) {
                    const u = users[who];
                    // An invalid body: JE refuses it (writing nothing), so an answer that isn't Locked shows the filter let it on.
                    const settings = await request('POST', `/JellyfinEnhanced/user-settings/${u.Id}/settings.json`, as(who), '{ not json');
                    const bookmark = await request('POST', `/JellyfinEnhanced/user-settings/${u.Id}/bookmark.json`, as(who), '{ not json');
                    // JE 12.14's Spoiler Guard preferences (the caller's own spoilerblur.json).
                    const prefs = await request('POST', '/JellyfinEnhanced/spoiler-blur/user-prefs', as(who), '{ not json');
                    answers[who] = { settings: settings.status, settingsLocked: locked(settings), bookmark: bookmark.status, bookmarkLocked: locked(bookmark), prefs: prefs.status, prefsLocked: locked(prefs) };
                }
                expectBlock(lockedUser, 'Settings.EnhancedPanel', 'JellyfinEnhancedController.SaveUserSettingsSettings', 'Refused');
                result.enhancedPanel = answers;
                check('EnhancedPanel locked: Jellyfin Enhanced\'s settings save and its Spoiler Guard preferences answer 403 Locked for the locked user, its bookmark save passes; the others save',
                    answers.locked.settingsLocked && answers.locked.prefsLocked && !answers.locked.bookmarkLocked && !answers.open.settingsLocked && !answers.open.prefsLocked && !answers.admin.settingsLocked && !answers.admin.prefsLocked, answers);
            }
        }

        // The whole configuration and the whole usersettings document, every value changed, through each route.
        const merges = {};
        const beyond = `homesection${20 + SECTIONS.indexOf(section)}`;
        const routesOf = ['/Users/Configuration', '/Users/{id}/Configuration', 'UpdateUser'];
        for (const who of ['locked', 'open', 'admin']) {
            const u = users[who];
            const entry = { config: [], prefs: [] };
            for (const route of routesOf) {
                const before = await configOf(u.Id);
                const posted = flipConfig(before, library);
                const r = route === 'UpdateUser'
                    ? await request('POST', `/Users?userId=${u.Id}`, as(who), { ...(await dtoOf(u.Id)), Name: u.Name, Configuration: posted })
                    : await request('POST', route.replace('{id}', u.Id), as(who), posted);
                const after = await configOf(u.Id);
                const kept = keptFields(before, after, posted);
                entry.config.push({ route, status: r.status, kept: sectionsOf(kept, (f) => Object.keys(CONFIG_FIELDS).find((s) => CONFIG_FIELDS[s].includes(f))), fields: kept.length });
                if (who === 'locked' && CONFIG_FIELDS[section]) expectBlock(lockedUser, `Settings.${section}`, route === 'UpdateUser' ? 'UserController.UpdateUser' : route.includes('{id}') ? 'UserController.UpdateUserConfigurationLegacy' : 'UserController.UpdateUserConfiguration', 'Reverted');
                await writeConfigAsAdmin(u.Id, before);
            }
            for (const [document, client] of [['usersettings', 'emby'], [USERSETTINGS_ID, 'emby'], ['usersettings', 'EMBY']]) {
                const before = await prefsOf(u.Id, 'usersettings', client);
                const posted = flipPrefs(before, library);
                if (client === 'emby' && document === USERSETTINGS_ID) {
                    posted.CustomPrefs.HOMESECTION0 = posted.CustomPrefs.homesection0;
                    delete posted.CustomPrefs.homesection0;
                    posted.CustomPrefs[beyond] = 'nextup';
                }
                const r = await request('POST', prefsPath(u.Id, document, client), as(who), posted);
                const after = await prefsOf(u.Id, 'usersettings', client);
                const kept = keptKeys(before, after, posted).filter((k) => k !== 'HOMESECTION0' && k !== beyond);
                // A home section past the stored layout, new in this section's post (10.11.5 keeps a home section once
                // written: its update clears the detached entity's list, which deletes no row).
                const extra = !Object.prototype.hasOwnProperty.call(before.CustomPrefs || {}, beyond) && Object.prototype.hasOwnProperty.call(after.CustomPrefs || {}, beyond) ? [beyond] : [];
                entry.prefs.push({ document: document === USERSETTINGS_ID ? 'GUID (N)' : document, client, status: r.status, kept: sectionsOf(kept, sectionOfKey), keys: kept.length, beyondLayout: extra.length > 0 });
                if (who === 'locked' && (section === 'Home' || section === 'Playback' || section === 'DisplayAccount')) expectBlock(lockedUser, `Settings.${section}`, 'DisplayPreferencesController.UpdateDisplayPreferences', 'Reverted');
                await writePrefsAsAdmin(u.Id, before, 'usersettings', client);
            }
            merges[who] = entry;
        }

        result.merges = merges;
        const expectKeptConfig = (who) => (lockedHere(who) && who === 'locked' && CONFIG_FIELDS[section] ? [section] : []);
        const expectKeptPrefs = (who) => (lockedHere(who) && who === 'locked' && ['Home', 'Playback', 'DisplayAccount'].includes(section) ? [section] : []);
        const mergeOk = ['locked', 'open', 'admin'].every((who) => merges[who].config.every((c) => c.status === 204 && JSON.stringify(c.kept) === JSON.stringify(expectKeptConfig(who)))
            && merges[who].prefs.every((p) => p.status === 204 && JSON.stringify(p.kept) === JSON.stringify(expectKeptPrefs(who)) && p.beyondLayout === (p.document === 'GUID (N)' && !(who === 'locked' && section === 'Home'))));
        check(`${section} locked: the whole configuration (both routes and UpdateUser) and the usersettings document (by name, by its GUID with HOMESECTION0 and a home section past the stored layout, with client EMBY) posted with every value changed: the locked user keeps ${expectKeptConfig('locked').concat(expectKeptPrefs('locked')).length ? `the ${section} fields and keys` : 'nothing (this section has none on the server)'} and nothing else; the unlocked user and the administrator keep every change`,
            mergeOk, merges);
        sections[section] = result;
    }
    evidence.sections = sections;

    // ---------------------------------------------------------------------------------------------------------------
    // 2. The variants.
    await setLocks(SERVER_ENFORCED);
    const badConfig = await request('POST', '/Users/Configuration', as('locked'), '{ not json');
    const badPrefs = await request('POST', prefsPath(lockedUser.Id), as('locked'), '{ not json');
    const noBody = await request('POST', '/Users/Configuration', as('locked'), '');
    const badPassword = await request('POST', '/Users/Password', as('locked'), '{ not json');
    expectBlock(lockedUser, 'Settings.Profile', 'UserController.UpdateUserPassword', 'Refused');
    evidence.variants = { badConfig: badConfig.status, badPrefs: badPrefs.status, noBody: noBody.status, badPassword: { status: badPassword.status, locked: locked(badPassword) } };
    check('an invalid or empty body to a merge answers the core\'s 400 (never a 500 or a refusal); a reject refuses an invalid body first (403 Locked)',
        badConfig.status === 400 && badPrefs.status === 400 && [400, 415].includes(noBody.status) && locked(badPassword), evidence.variants);

    const keyOk = await createKey();
    const keyBefore = await configOf(lockedUser.Id);
    const keyPosted = flipConfig(keyBefore, library);
    const byKey = await request('POST', `/Users/Configuration?userId=${lockedUser.Id}`, 'key', keyPosted);
    const keyAfter = await configOf(lockedUser.Id);
    const keyMe = await request('GET', '/AccessRoles/Me', 'key');
    evidence.apiKey = { created: keyOk, status: byKey.status, kept: keptFields(keyBefore, keyAfter, keyPosted), me: keyMe.status };
    check('an API key is never restricted: it changes every field of the locked user\'s configuration (204, nothing kept); /Me answers it 403',
        keyOk && byKey.status === 204 && evidence.apiKey.kept.length === 0 && keyMe.status === 403, evidence.apiKey);
    await writeConfigAsAdmin(lockedUser.Id, keyBefore);
    await revokeKeys();

    // ---------------------------------------------------------------------------------------------------------------
    // 3. /Me, the counters and the flood.
    await setLocks([...SECTIONS, 'AdminSurfaces', 'UserDirectory']);
    const me = {};
    for (const who of ['locked', 'open', 'admin']) {
        const r = await request('GET', '/AccessRoles/Me', as(who));
        me[who] = { status: r.status, cache: r.headers['cache-control'], ...(r.json || {}) };
    }
    me.anonymous = (await request('GET', '/AccessRoles/Me', null)).status;
    evidence.me = me;
    const allLocked = me.locked.Locked || [];
    check('/Me: the locked user is managed with its locks (every settings section, AdminSurfaces and UserDirectory) and AllSettingsLocked, no-store, no lock "not enforced yet" (since 0.9.7); the unlocked user isn\'t managed; the managed administrator has no lock; anonymous 401',
        me.locked.status === 200 && me.locked.Managed === true && SECTIONS.every((s) => allLocked.includes(s) || (s === 'EnhancedPanel' && !je && allLocked.includes(s))) && allLocked.includes('AdminSurfaces') && allLocked.includes('UserDirectory')
            && Array.isArray(me.locked.NotEnforced) && me.locked.NotEnforced.length === 0
            && me.locked.AllSettingsLocked === true && me.locked.cache === 'no-store' && idN(me.locked.UserId) === idN(lockedUser.Id)
            && me.open.status === 200 && me.open.Managed === false && (me.open.Locked || []).length === 0
            && me.admin.status === 200 && me.admin.Managed === true && (me.admin.Locked || []).length === 0 && me.admin.AllSettingsLocked === false
            && me.anonymous === 401,
        me);

    const flood = [];
    for (let i = 0; i < opts.refusals; i++) flood.push((await request('POST', '/Users/Password', as('locked'), { CurrentPw: 'x', NewPw: 'y' })).status);
    expectBlock(lockedUser, 'Settings.Profile', 'UserController.UpdateUserPassword', 'Refused', opts.refusals);
    const lastBlockAt = Date.now();
    const d1 = await diagnostics();
    const totals = ((d1.Enforcement || {}).Blocked || {}).Totals || {};
    evidence.counters = { totals, openWindows: ((d1.Enforcement || {}).Blocked || {}).OpenWindows, filter: (d1.Enforcement || {}).Filter };
    check(`a flood of ${opts.refusals} password attempts: all 403 Locked; Diagnostics counts the refusals and the reverts, no filter error`,
        flood.every((s) => s === 403) && totals.Refused >= opts.refusals && totals.Reverted > 0 && totals.FailedClosed === 0 && ((d1.Enforcement || {}).Filter || {}).FilterErrors === 0, evidence.counters);

    // ---------------------------------------------------------------------------------------------------------------
    // 4. The hardening's server-settings rows: enforced since 0.9.7 (a separate hardening probe, not in this repository, has them in full). The locked user has
    // AdminSurfaces and UserDirectory too here. The restart is never sent (access-scratch only).
    const config = await request('GET', '/System/Configuration', as('open'));
    const network = await request('GET', '/System/Configuration/network', as('open'));
    const networkCase = await request('GET', '/System/Configuration/NETWORK', as('open'));
    const publicUsers = await request('GET', '/Users/Public', null);
    const systemInfo = await request('GET', '/System/Info', as('locked'));
    const userList = await request('GET', '/Users', as('locked'));
    const encoding = await request('GET', '/System/Configuration/encoding', as('open'));
    const serverRows = {
        configuration: { status: config.status, locked: locked(config) },
        network: { status: network.status, hasCertificatePassword: !!network.json && Object.prototype.hasOwnProperty.call(network.json, 'CertificatePassword'), locked: locked(network) },
        networkUpperCase: { status: networkCase.status, locked: locked(networkCase) },
        encoding: { status: encoding.status },
        publicUsers: { status: publicUsers.status, users: (publicUsers.json || []).length, withPolicy: (publicUsers.json || []).filter((u) => !!u.Policy).length },
        systemInfoAsAdminSurfacesLocked: { status: systemInfo.status, hasProgramDataPath: !!systemInfo.json && !!systemInfo.json.ProgramDataPath, locked: locked(systemInfo) },
        userListAsLocked: { status: userList.status, users: (userList.json || []).length, onlyItself: (userList.json || []).every((u) => idN(u.Id) === idN(lockedUser.Id)) },
        remoteSearch: 'not sent (it would start remote provider searches)',
        restart: 'not sent (access-scratch only)'
    };
    evidence.serverRows = serverRows;
    check('the hardening\'s server-settings rows are enforced since 0.9.7: the configuration, network and NETWORK reads 403 Locked, encoding 200, the anonymous public list without policies, GET /System/Info without the server\'s paths and GET /Users with only the caller under AdminSurfaces and UserDirectory',
        serverRows.configuration.locked && serverRows.network.locked && serverRows.networkUpperCase.locked && serverRows.encoding.status === 200 && serverRows.publicUsers.status === 200 && serverRows.publicUsers.withPolicy === 0
            && serverRows.systemInfoAsAdminSurfacesLocked.status === 200 && !serverRows.systemInfoAsAdminSurfacesLocked.hasProgramDataPath && serverRows.userListAsLocked.status === 200 && serverRows.userListAsLocked.users === 1 && serverRows.userListAsLocked.onlyItself, serverRows);

    // ---------------------------------------------------------------------------------------------------------------
    // 5. The web client as viewer, with every section locked.
    if (opts.webcheck) {
        const viewer = (await request('GET', '/Users', 'admin')).json.find((u) => u.Name === WEBCHECK_USER);
        if (!viewer || !credentials[WEBCHECK_USER]) throw new Error('no viewer in the sandbox or credentials.json');
        webcheckUserBefore = { id: viewer.Id, config: await configOf(viewer.Id), prefs: await prefsOf(viewer.Id) };
        await assignRole(viewer);
        let run = null;
        let result = null;
        const attempts = [];
        // Up to five runs, a few seconds apart, when only the sign-in fails: the login page's user cards sometimes hide
        // the manual form again while webcheck waits for it (a known flake; seen three times in a row right after a busy run).
        for (let attempt = 1; attempt <= 5; attempt++) {
            if (attempt > 1) await delay(5000);
            run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', 'accessroles-settings-locks.json'], { encoding: 'utf8', timeout: 300000 });
            try { result = JSON.parse(run.stdout); } catch { result = null; }
            const failed = result && (result.steps || []).find((s) => !s.ok);
            attempts.push({ attempt, status: run.status, failedStep: failed ? failed.name : null });
            if (run.status === 0 && result && result.ok === true) break;
            if (!failed || failed.name !== 'sign in as viewer') break;
        }
        const failedRequests = (result && result.failedRequests) || [];
        // Expected failures: none of Access Roles'. jellyfin-web's own (if any) are listed.
        const ours = failedRequests.filter((f) => /\/(AccessRoles|Users|DisplayPreferences|UserImage|QuickConnect|System|Sessions)\b/i.test(f.url || ''));
        const viewerAfter = { config: await configOf(viewer.Id), prefs: await prefsOf(viewer.Id) };
        const keptAll = stable(viewerAfter.config) === stable(webcheckUserBefore.config) && stable(viewerAfter.prefs.CustomPrefs) === stable(webcheckUserBefore.prefs.CustomPrefs);
        evidence.webcheck = result
            ? { ok: result.ok, attempts, steps: (result.steps || []).map((s) => ({ name: s.name, ok: s.ok, skipped: s.skipped, value: s.value, error: s.error })), exceptions: result.exceptions, failedRequests, screenshots: result.screenshots, keptAll }
            : { attempts, status: run.status, stderr: (run.stderr || '').slice(0, 2000) };
        check('the web client as viewer with every section locked: Home and the Settings menu load (every link hidden, the note), every settings page ends on Home (the web guard), no request of the API fails (no 403 the locks didn\'t mean), saves of the configuration and the usersettings document through jellyfin-web\'s ApiClient leave the server\'s values as they were, with the guard\'s toast, and with the guard off for the page the Home screen and Display pages\' own form saves are kept too, with the header',
            run.status === 0 && result && result.ok === true && ours.length === 0 && keptAll, { attempts, failedRequests: ours, keptAll });
        if (!keptAll) {
            // A merge failed to keep viewer's settings: write them back as they were.
            await writeConfigAsAdmin(viewer.Id, webcheckUserBefore.config);
            await writePrefsAsAdmin(viewer.Id, webcheckUserBefore.prefs);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 6. Every user the probe didn't create: never written.
    const changed = [];
    for (const u of others) {
        if (stable((await request('GET', `/Users/${u.Id}`, 'admin')).json.Policy) !== othersBefore[u.Id]) changed.push(u.Name);
    }
    check('no policy of a user the probe didn\'t create was written', changed.length === 0, { users: others.length, changed });

    // The audit: one line per user, rule, action and outcome per window, with the counts the probe caused.
    if (opts.auditWait) {
        const closes = Math.floor(lastBlockAt / WINDOW_MS) * WINDOW_MS + WINDOW_MS;
        const waitMs = Math.max(0, closes - Date.now()) + 45000;
        evidence.auditWaitMs = waitMs;
        await delay(waitMs);
        const lines = (await auditSince(startSeq, 'RequestsBlocked')).filter((e) => idN(e.TargetUserId) === idN(lockedUser.Id));
        const sums = new Map();
        const perKeyWindow = new Map();
        for (const line of lines) {
            const key = `${idN(line.TargetUserId)}|${line.Details.Rule}|${line.Details.Endpoint}|${line.Details.Outcome}`;
            sums.set(key, (sums.get(key) || 0) + line.Details.Count);
            const windowKey = `${key}|${line.Details.WindowStartUtc}`;
            perKeyWindow.set(windowKey, (perKeyWindow.get(windowKey) || 0) + 1);
        }
        const expected = [...expectedBlocks.entries()].filter(([k, v]) => v > 0 && k.startsWith(idN(lockedUser.Id)));
        const mismatched = expected.filter(([k, v]) => sums.get(k) !== v).map(([k, v]) => ({ key: k.split('|').slice(1).join(' '), expected: v, audited: sums.get(k) || 0 }));
        const doubled = [...perKeyWindow.entries()].filter(([, n]) => n > 1).map(([k]) => k);
        evidence.audit = { lines: lines.length, keys: expected.length, mismatched, doubled, sample: lines.slice(0, 3).map((l) => ({ Rule: l.Details.Rule, Endpoint: l.Details.Endpoint, Outcome: l.Details.Outcome, Count: l.Details.Count, WindowStartUtc: l.Details.WindowStartUtc, TargetName: l.TargetName })) };
        check('every refusal and revert the probe caused is audited RequestsBlocked, one line per user, rule, action, outcome and 10-minute window, with the probe\'s counts (the flood included)',
            lines.length > 0 && mismatched.length === 0 && doubled.length === 0, evidence.audit);
    } else {
        evidence.audit = 'not waited for (--no-audit-wait): the counters are checked in Diagnostics only';
    }
}

let failure = null;
try {
    await main();
} catch (e) {
    failure = e.stack || String(e);
} finally {
    const cleanupResult = {};
    try {
        await cleanup('cleanup');
        cleanupResult.probeLeft = {
            roles: ((await request('GET', '/AccessRoles/Admin/Roles', 'admin')).json || []).filter((r) => r.Name.startsWith(PREFIX)).length,
            managed: (await usersRows()).filter((u) => u.State !== 'NotManaged').length,
            users: ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Name.toLowerCase().startsWith(PROBE_USER)).length,
            keys: ((((await request('GET', '/Auth/Keys', 'admin')).json || {}).Items) || []).filter((k) => k.AppName === PROBE_KEY).length
        };
        if (webcheckUserBefore) {
            cleanupResult.viewerSettingsAsBefore = stable(await configOf(webcheckUserBefore.id)) === stable(webcheckUserBefore.config)
                && stable((await prefsOf(webcheckUserBefore.id)).CustomPrefs) === stable(webcheckUserBefore.prefs.CustomPrefs);
        }
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + (e.stack || String(e));
    }
    evidence.cleanup = cleanupResult;
    if (cleanupErrors.length) failure = (failure ? failure + ' | ' : '') + 'cleanup: ' + cleanupErrors.join('; ');
    if (!failure) {
        const left = cleanupResult.probeLeft || {};
        check('afterwards nothing of the probe\'s is left (its role, users and API key; nobody managed), viewer\'s settings as before',
            left.roles === 0 && left.managed === 0 && left.users === 0 && left.keys === 0 && cleanupResult.viewerSettingsAsBefore !== false, cleanupResult);
        const log = logSinceStart().filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text) && (l.level === 'WRN' || l.level === 'ERR' || l.level === 'FTL'));
        // Expected: the other probes' (the 10.11.5 race both ways: a post-write check's finding, a failed save written again
        // from the stored row; the hold's refusal, the early confirmation); none of the settings filter's.
        const expected = log.filter((l) => l.level === 'WRN' && (/the server enforces .* \((Reverted|Changed)\)/.test(l.text) || /refused the sign-in of user .*: the library sync is held/.test(l.text) || /library sync stays held: confirmRestore needs a dry run/.test(l.text)
            || /write of the library access of user [0-9a-f-]+ failed|writes the library access of user [0-9a-f-]+ again in|wrote the stored policy of user [0-9a-f-]+ back instead/.test(l.text)));
        evidence.log = { warnings: log.length, expected: expected.length };
        check('the log has no warning or error of Access Roles\' settings filter (the other probes\' expected ones aside)', log.length === expected.length, log.filter((l) => !expected.includes(l)).map((l) => l.text.slice(0, 240)));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
