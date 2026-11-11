#!/usr/bin/env node
// Access Roles in a running sandbox (the check that it loads in the access sandbox): the plugin is
// loaded and Active, its dashboard page is listed in the main menu and served, its data folder and schema 1 file exist,
// its Diagnostics say the store is Ok, the library sync is active (since 0.9.3) and the action manifest matches the
// running server's Jellyfin.Api actions (390, none missing, none unreviewed, none changed), the settings locks are enforced
// (since 0.9.5) with the rule table's self-check bound, the server's users come
// through the kit's KitUsers, Diagnostics is for
// admins only, the ClientScript is anonymous and index.html carries its tag once, the daily maintenance task exists, and
// the server log has no warning or error of the plugin's. Optionally (--webcheck) the settings page in a headless browser.
// Node 22 built-ins only. Loopback only, never port 8096. Tokens come from credentials.json and are never printed. It reads
// the sandbox's files and asks the sandbox's API; it changes nothing and never starts or stops a server.
//
//   node scripts/accessroles-probe.mjs --sandbox access [--webcheck] [--out result.json]
//
// Start the sandbox first: ./scripts/dev-server.ps1 -Name access -Port 8103 -Plugins AccessRoles
// -Community FileTransformation,PluginPages -Background (scripts/README.md, this probe's section). Prints one JSON
// result (pass, checks, evidence) and exits with 0 when every check passed.

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const PLUGIN = 'AccessRoles';
const MANIFEST_ACTIONS = 390;
// 99 rule rows until 0.9.7, which gave rules to the eleven actions a review had found without one.
const MANIFEST_RULES = 110;
// Access Roles' own actions: 3 in 0.9.1, 20 since the roles API (0.9.2), 23 since the library sync's Sync,
// GrantAgain and Purge (0.9.3), 24 since Import current setup (0.9.4), 25 since GET /AccessRoles/Me (0.9.5),
// 26 since the dashboard page's GET /AccessRoles/AdminScript (0.9.8), 28 since GET and PUT
// /AccessRoles/Admin/Settings (0.9.11), 30 since POST /AccessRoles/Admin/Degraded/Resolve and ReleaseLocks (0.9.13),
// 35 since GET Export, POST Import, GET Snapshots, POST Snapshots/Restore and POST Maintenance/Run (0.9.15).
const OWN_ACTIONS = 35;

function usage() {
    return 'usage: node scripts/accessroles-probe.mjs --sandbox <name> [--webcheck] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, webcheck: false };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--webcheck') o.webcheck = true;
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
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer']) {
    if (!users[name] || !users[name].token) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}

const yaml = fs.readFileSync(path.join(repoRoot, 'plugins', PLUGIN, 'build.yaml'), 'utf8');
const scalar = (key) => (new RegExp(`^${key}:\\s*"?([^"\\r\\n]+)"?\\s*$`, 'm').exec(yaml) || [])[1];
const plugin = { name: scalar('name'), guid: scalar('guid'), version: scalar('version') };

// The plugin's data folder in the sandbox (Jellyfin's DataPath is <datadir>/data), only read.
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
const dataFolder = path.join(dataDir, 'data', 'accessroles');
const storeFile = path.join(dataFolder, 'accessroles.json');
for (const p of [dataFolder, path.join(sandboxRoot, 'log')]) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), p);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(p)) throw new Error(`refusing to read ${p}`);
}

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: new Date().toISOString() }, plugin };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
}

function authHeader(user) {
    const token = user ? users[user].token : null;
    return `MediaBrowser Client="accessroles-probe", Device="sandbox", DeviceId="accessroles-probe", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

function request(method, p, user = null, body = null) {
    return new Promise((resolve, reject) => {
        const headers = { Authorization: authHeader(user), Accept: 'application/json' };
        if (body !== null) headers['Content-Type'] = 'application/json';
        const req = http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let json = null;
                try { json = text && /json/.test(res.headers['content-type'] || '') ? JSON.parse(text) : null; } catch { json = null; }
                resolve({ status: res.statusCode, type: res.headers['content-type'] || '', text, json });
            });
        });
        req.on('error', reject);
        req.setTimeout(30000, () => req.destroy(new Error(`timeout ${method} ${p}`)));
        if (body !== null) req.write(typeof body === 'string' ? body : JSON.stringify(body));
        req.end();
    });
}

const count = (text, needle) => text.split(needle).length - 1;

// Log lines since the sandbox's last start (the timestamps carry their offset).
function logSinceStart() {
    const folder = path.join(sandboxRoot, 'log');
    const since = Date.parse(state.StartedUtc || '1970-01-01T00:00:00Z');
    const lines = [];
    for (const file of fs.readdirSync(folder).filter((f) => /^log_\d{8}\.log$/.test(f)).sort()) {
        for (const line of fs.readFileSync(path.join(folder, file), 'utf8').split(/\r?\n/)) {
            const m = /^\[(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2})\] \[(\w{3})\]/.exec(line);
            if (m && Date.parse(`${m[1]}T${m[2]}${m[3]}`) >= since - 1000) lines.push({ level: m[4], text: line });
        }
    }
    return lines;
}

async function main() {
    // The server, and the plugin as Jellyfin loaded it.
    const info = await request('GET', '/System/Info/Public');
    check('the sandbox answers, Jellyfin 10.11.5', info.status === 200 && info.json && info.json.Version === '10.11.5', { status: info.status, version: info.json && info.json.Version });

    const plugins = await request('GET', '/Plugins', 'admin');
    const ours = (plugins.json || []).find((p) => (p.Id || '').replace(/-/g, '').toLowerCase() === plugin.guid.replace(/-/g, '').toLowerCase());
    evidence.plugins = (plugins.json || []).map((p) => `${p.Name} ${p.Version} ${p.Status}`);
    check('Access Roles is loaded and Active, at the build.yaml version', !!ours && ours.Name === plugin.name && ours.Version === plugin.version && ours.Status === 'Active',
        ours ? { name: ours.Name, version: ours.Version, status: ours.Status } : 'not listed');

    // The dashboard page: in the main menu, and served (anonymously, as Jellyfin serves dashboard pages).
    const pages = await request('GET', '/web/ConfigurationPages?enableInMainMenu=true', 'admin');
    const page = (pages.json || []).find((p) => p.Name === 'AccessRoles');
    check('its dashboard page is listed in the main menu', pages.status === 200 && !!page && page.DisplayName === plugin.name && (page.PluginId || '').replace(/-/g, '') === plugin.guid.replace(/-/g, ''),
        page || { status: pages.status });
    // Since 0.9.8 the page is a skeleton whose data-controller is the AccessRolesJs stub, which imports the
    // anonymous AdminScript module (the kit's dashboard module).
    const html = await request('GET', '/web/ConfigurationPage?name=AccessRoles');
    const stub = await request('GET', '/web/ConfigurationPage?name=AccessRolesJs');
    const module = await request('GET', '/AccessRoles/AdminScript');
    check('its dashboard page is served: the skeleton, its AccessRolesJs stub and the AdminScript module it imports',
        html.status === 200 && /text\/html/.test(html.type) && html.text.includes('id="AccessRolesConfigPage"') && html.text.includes('data-controller="__plugin/AccessRolesJs"')
            && stub.status === 200 && stub.text.includes("import(ApiClient.getUrl('AccessRoles/AdminScript'))")
            && module.status === 200 && /javascript/.test(module.type) && module.text.trimEnd().endsWith('export { mount };'),
        { page: { status: html.status, type: html.type, bytes: html.text.length }, stub: { status: stub.status, type: stub.type }, module: { status: module.status, type: module.type, bytes: module.text.length } });

    // The data folder and its schema 1 file (written at the first start, after the startup gate).
    for (let i = 0; i < 40 && !fs.existsSync(storeFile); i++) await delay(250);
    const exists = fs.existsSync(storeFile);
    const stored = exists ? readJson(storeFile) : null;
    evidence.dataFolder = exists ? fs.readdirSync(dataFolder) : null;
    check('its data folder has accessroles.json, schema 1', exists && stored.Version === 1 && Array.isArray(stored.Roles) && Array.isArray(stored.Assignments) && typeof stored.Ledger === 'object',
        exists ? { file: path.relative(repoRoot, storeFile), version: stored.Version, keys: Object.keys(stored), roles: stored.Roles.length, assignments: stored.Assignments.length } : 'missing');

    // Diagnostics, for admins only.
    const anonymous = await request('GET', '/AccessRoles/Admin/Diagnostics');
    const viewer = await request('GET', '/AccessRoles/Admin/Diagnostics', 'viewer');
    const d = await request('GET', '/AccessRoles/Admin/Diagnostics', 'admin');
    check('Diagnostics is 401 anonymously and 403 for a non-admin', anonymous.status === 401 && viewer.status === 403, { anonymous: anonymous.status, viewer: viewer.status });
    const body = d.json || {};
    evidence.diagnostics = { PluginVersion: body.PluginVersion, KitVersion: body.KitVersion, Abi: body.Abi, Store: body.Store, Records: body.Records, Enforcement: body.Enforcement, ServerUsers: body.ServerUsers };
    check('Diagnostics answers the admin: this build, active on Jellyfin 10.11.5', d.status === 200 && body.PluginVersion === plugin.version && body.Abi && body.Abi.Active === true && body.Abi.Target === '10.11' && body.Abi.Server === '10.11.5',
        { status: d.status, abi: body.Abi });
    const store = body.Store || {};
    check('the store is Ok, schema 1, at the data folder\'s file', store.Status === 'Ok' && store.Version === 1 && store.ReadOnly === false && path.resolve(store.File || '') === path.resolve(storeFile), store);
    // Since 0.9.3 the library sync writes library roles, since 0.9.5 the settings locks are enforced, since
    // 0.9.7 the admin surfaces and the server-wide hardening, with no lock left "not enforced yet".
    check('the library sync, the settings locks and the hardening are active; no lock is "not enforced yet"',
        body.Enforcement && body.Enforcement.Active === true && body.Enforcement.LibraryAccess === 'Synced' && body.Enforcement.SettingsLocks === true && body.Enforcement.Hardening === true
            && Array.isArray(body.Enforcement.NotEnforcedLocks) && body.Enforcement.NotEnforcedLocks.length === 0
            && body.LibrarySync && body.LibrarySync.State === 'Active',
        { enforcement: body.Enforcement, librarySync: body.LibrarySync && { State: body.LibrarySync.State, DriftPolicy: body.LibrarySync.DriftPolicy } });
    const manifest = body.ActionManifest || {};
    const server = manifest.Server || {};
    evidence.actionManifest = { JellyfinVersion: manifest.JellyfinVersion, Source: manifest.Source, Actions: manifest.Actions, WithRules: manifest.WithRules, Rules: manifest.Rules,
        Server: { Matches: server.Matches, Actions: server.Actions, Missing: server.Missing, Unreviewed: server.Unreviewed, Changed: server.Changed, Duplicates: server.Duplicates, ByAssembly: server.ByAssembly } };
    check(`the action manifest reviews Jellyfin 10.11.5's ${MANIFEST_ACTIONS} actions, ${MANIFEST_RULES} with rules`, manifest.JellyfinVersion === '10.11.5' && manifest.Actions === MANIFEST_ACTIONS && manifest.WithRules === MANIFEST_RULES,
        { version: manifest.JellyfinVersion, actions: manifest.Actions, withRules: manifest.WithRules });
    check('the running server\'s Jellyfin.Api actions match the manifest: none missing, unreviewed, changed or doubled',
        server.Matches === true && server.Actions === MANIFEST_ACTIONS && [server.Missing, server.Unreviewed, server.Changed, server.Duplicates].every((l) => Array.isArray(l) && l.length === 0),
        { matches: server.Matches, actions: server.Actions, missing: (server.Missing || []).length, unreviewed: (server.Unreviewed || []).length, changed: (server.Changed || []).slice(0, 5) });
    check(`the server routes Access Roles' own ${OWN_ACTIONS} actions`, server.ByAssembly && server.ByAssembly['Jellyfin.Plugin.AccessRoles'] === OWN_ACTIONS, server.ByAssembly);

    // Since 0.9.5: the rule table's self-check after the startup gate (the one global filter registered, every rule
    // row bound to an action the server routes, nothing unreviewed).
    const ruleTable = body.RuleTable || {};
    evidence.ruleTable = { FilterRegistered: ruleTable.FilterRegistered, EnforcementActive: ruleTable.EnforcementActive, Bound: ruleTable.Bound, WithRules: ruleTable.WithRules, Missing: ruleTable.Missing, Changed: ruleTable.Changed, Unreviewed: ruleTable.Unreviewed, JellyfinEnhanced: ruleTable.JellyfinEnhanced, Flagged: ruleTable.Flagged };
    check(`the rule table's self-check: the global filter registered, all ${MANIFEST_RULES} rule rows bound, none changed or unreviewed`,
        ruleTable.FilterRegistered === true && ruleTable.EnforcementActive === true && ruleTable.Bound === MANIFEST_RULES && ruleTable.WithRules === MANIFEST_RULES
            && [ruleTable.Missing, ruleTable.Changed, ruleTable.Unreviewed].every((l) => Array.isArray(l) && l.length === 0),
        evidence.ruleTable);

    // The server's users, as the plugin lists them (KitUsers) and as Jellyfin's API does.
    const list = await request('GET', '/Users', 'admin');
    const apiUsers = list.json || [];
    const su = body.ServerUsers || {};
    check('the server\'s users come through KitUsers (10.11.5: the Users property), as many as GET /Users lists',
        su.ListedThrough === 'Users' && su.Users === apiUsers.length && su.Administrators === apiUsers.filter((u) => u.Policy && u.Policy.IsAdministrator).length
            && su.Disabled === apiUsers.filter((u) => u.Policy && u.Policy.IsDisabled).length,
        { kitUsers: su, api: { users: apiUsers.length, administrators: apiUsers.filter((u) => u.Policy && u.Policy.IsAdministrator).length } });

    // The web client: the anonymous ClientScript, the index.html tag, the ClientHealth reports.
    const script = await request('GET', '/AccessRoles/ClientScript');
    check('the ClientScript is anonymous JavaScript of this build', script.status === 200 && /text\/javascript/.test(script.type) && script.text.includes('"prefix":"jfar"') && script.text.includes(`"version":"${plugin.version}"`),
        { status: script.status, type: script.type, bytes: script.text.length });
    const index = await request('GET', '/web/index.html');
    check('index.html loads it once', index.status === 200 && count(index.text, `data-jfar-plugin="${plugin.version}"`) === 1 && index.text.includes('src="../AccessRoles/ClientScript?v='),
        { status: index.status, tags: count(index.text, 'data-jfar-plugin') });
    const health = await request('POST', '/AccessRoles/Diagnostics/ClientHealth', 'viewer', { WebVersion: '10.11.5', Client: 'Jellyfin Web', ClientVersion: '10.11.5', Layout: 'Desktop', Experimental: false, PluginVersion: plugin.version, Missing: [] });
    const healthAnonymous = await request('POST', '/AccessRoles/Diagnostics/ClientHealth', null, {});
    check('ClientHealth takes a signed-in user\'s report (204) and refuses an anonymous one (401)', health.status === 204 && healthAnonymous.status === 401, { viewer: health.status, anonymous: healthAnonymous.status });

    // The daily maintenance task (at 03:50).
    const tasks = await request('GET', '/ScheduledTasks', 'admin');
    const maintenance = (tasks.json || []).find((t) => t.Name === 'Access Roles: Maintenance');
    const trigger = maintenance && (maintenance.Triggers || [])[0];
    check('the daily maintenance task "Access Roles: Maintenance" is there, daily at 03:50', !!maintenance && maintenance.Triggers.length === 1 && trigger.Type === 'DailyTrigger' && trigger.TimeOfDayTicks === (3 * 60 + 50) * 60 * 10000000,
        maintenance ? { name: maintenance.Name, state: maintenance.State, triggers: maintenance.Triggers } : 'not listed');

    // The server log since the start: the plugin loaded, and no warning or error of its own.
    const log = logSinceStart();
    const loaded = log.filter((l) => l.text.includes(`Loaded plugin: "${plugin.name}" "${plugin.version}"`));
    const own = log.filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text) && (l.level === 'WRN' || l.level === 'ERR' || l.level === 'FTL'));
    evidence.log = { lines: log.length, loaded: loaded.map((l) => l.text.slice(0, 200)), createdFile: log.filter((l) => l.text.includes('Access Roles created its data file')).map((l) => l.text.slice(0, 220)),
        accessRolesLines: log.filter((l) => /AccessRoles|Access Roles/.test(l.text)).map((l) => l.text.slice(0, 220)).slice(0, 20) };
    check('the log since the start shows the plugin loaded, and no warning or error of its own', loaded.length === 1 && own.length === 0, { loaded: loaded.length, own: own.map((l) => l.text.slice(0, 220)) });

    if (opts.webcheck) {
        // Up to three runs: with users shown on the login screen (as here), jellyfin-web sometimes swaps the manual login
        // form for the user cards while webcheck types, so the sign-in step fails before the page is reached.
        let run = null;
        let result = null;
        const attempts = [];
        for (let attempt = 1; attempt <= 3; attempt++) {
            run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', 'accessroles-settings.json'], { encoding: 'utf8', timeout: 240000 });
            try { result = JSON.parse(run.stdout); } catch { result = null; }
            const failed = result && (result.steps || []).find((s) => !s.ok);
            attempts.push({ attempt, status: run.status, failedStep: failed ? failed.name : null });
            if (run.status === 0 && result && result.ok === true) break;
            if (!failed || failed.name !== 'sign in as admin') break;
        }

        evidence.webcheck = result
            ? { ok: result.ok, attempts, steps: (result.steps || []).map((s) => ({ name: s.name, ok: s.ok, value: s.value, error: s.error })), exceptions: result.exceptions, failedRequests: result.failedRequests, screenshots: result.screenshots }
            : { attempts, status: run.status, stderr: (run.stderr || '').slice(0, 2000) };
        check('the settings page shows the status in a browser (webcheck accessroles-settings.json)', run.status === 0 && result && result.ok === true, { attempts });
    }
}

let failure = null;
try {
    await main();
} catch (e) {
    failure = e.stack || String(e);
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
