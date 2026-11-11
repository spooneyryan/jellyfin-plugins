#!/usr/bin/env node
// Access Roles' admin page, Roles and Users, in a running sandbox:
//   1. The page: GET /web/ConfigurationPage?name=AccessRoles is the skeleton whose data-controller is the AccessRolesJs
//      stub; the stub imports GET /AccessRoles/AdminScript, the kit's dashboard module, served anonymously
//      as static JavaScript with the build's MVID as its ETag (304 when it matches), built from the plugin's fourteen
//      Web/admin/js files (the other tabs and the inert view included) and exporting mount (its users, roles and libraries come from the admin API).
//   1b. With --webcheck, while the import is offered (no assignment stored), the kit's confirm on the TV layout
//      (webchecks/accessroles-admin-page-tv-confirm.json: the confirm reached and dismissed by D-pad alone), once the
//      probe's two users exist: the page opens on Overview although the Users tab is remembered, the import's dry run,
//      OK on Apply the import opens the confirm, the D-pad reaches its confirm button, OK on Cancel and Back dismiss it
//      with the focus back on Apply; then another administrator gives pageprobe-kid one library instead of every one
//      (the stock policy API) and OK on the confirm's Apply sends the import with its preview's PreviewToken: 409
//      Changed naming the kid, Apply off and the focus on Preview; the kid's access is put back, nothing imported.
//      Skipped, and said so, when the import isn't offered.
//   2. With --webcheck, the flows in the web client as admin at 1366 px (webchecks/accessroles-admin-page-desktop.json) on the
//      probe's two users pageprobe-kid and pageprobe-teen: a role from the Kid preset (the Dashboard row, a library
//      picked, previewed, created), the users' bulk Assign roles (previewed per user as the write's own dry run; another
//      administrator then changes the kid through the API, so Assign is a 409 Changed naming them with nothing written,
//      and "Reload and keep my changes" leads to a new preview; another administrator then adds a Subtitles lock to the
//      role, so Assign is a 409 Changed again, the preview's PreviewToken, naming both users with nothing written, and
//      after Reload the preview with Subtitles newly locked is saved), the kid's panel (the effective access with
//      its sources, a Home override Unlock previewed and saved, Unmanage user with the leave-management choice asked
//      first, Keep's dry run held 4 s while Restore's answers at once, the late answer dropped, Restore saved), the role's
//      Delete with its last member leaving management (Restore; another administrator changes the teen and changes it
//      back, so Delete is a 409 Changed naming them with the role kept, then Reload and keep my changes and Delete).
//      Then through the API: both users unmanaged, their library access written back by the library sync as it was when
//      the probe created them, the role gone, and the audit lines of each step (RoleCreated, AssignmentChanged for both
//      users, RoleDeleted) by the admin.
//   2b. The same races through the API, each with its negative control, on the probe's
//      lock-only role "Page probe Race" (it manages no library): Users/Bulk's dry run with each user's IfUpdatedAt, then
//      another write to one of them, then the bulk with the dry run's tokens is a 409 Changed naming that user only, and
//      nothing is written; the same bulk without IfUpdatedAt is saved. DELETE Roles' dry run with the role's MembersToken
//      as ifMembers, then another write to a member, then the DELETE with that ifMembers is a 409 Changed naming that
//      member only and the role is kept; the same DELETE without ifMembers deletes it. Both users are unmanaged (Keep)
//      afterwards.
//   2c. Two stale-definition races through the API, each with its negative control: no token of the request covers
//      a role's definition, the dry run's PreviewToken (ifPreview) does. Users/Bulk's dry run adding the lock-only role
//      "Page probe Effect" to the kid, then another administrator gives that role every library and two locks more, then
//      the bulk with the dry run's IfUpdatedAt and its PreviewToken is a 409 Changed naming the kid, nothing written;
//      the dry run again and the bulk with its token is saved (every library, the locks). DELETE Roles' dry run of the
//      lock-only "Page probe Source" (the teen's) reassigning its members to the lock-only "Page probe Target", with
//      ifUpdatedAt and ifMembers, then another administrator gives the target every library and a Playback lock, then the
//      DELETE with those tokens and the PreviewToken is a 409 Changed naming the teen, the role kept and nobody moved; the
//      dry run again and the DELETE with its token deletes it and moves the teen. Both users unmanaged (Keep) after.
//   3. The stored assignment in Effective (the panel's source): the probe's lock-only role "Page probe Guest"
//      given to pageprobe-teen with a Home override Unlock through the API, and Effective's Assignment says exactly that,
//      while its Locks say where each lock comes from.
//   4. With --webcheck, the page on the TV layout (webchecks/accessroles-admin-page-tv.json: the remembered Users tab, the
//      D-pad through the tabs and the teen's panel, OK on a checkbox, Back closing without saving) and on a 375 px touch
//      phone (webchecks/accessroles-admin-page-phone.json: nothing past the right edge, 44 px controls, the probe's
//      100-character role name "Page probe WWW…" (nobody has it) wrapping on the Roles tab, in its editor's title and in
//      the panel, the dialogs' Close icon and text fields 44 px, the panel as a sheet), both reading only.
//   5. Afterwards nothing of the probe's is left, the default roles are as they were (none: the probe's users would get
//      them), every other user's policy was never written, and the log has no error of Access Roles' since the probe
//      started.
// It writes only what is its own: its users (pageprobe-..., random passwords kept in memory, never printed or saved, and
// never used to sign in), their assignments and the library access the library sync writes for them, and its roles ("Page
// probe ..."); the desktop webcheck's "another administrator" writes are PUT Users/{id} of the probe's two users, through
// the page's own admin session, and the TV confirm's change of the kid's library access (put back as it was) through the
// stock policy API. Each webcheck is run up to five times when only its first sign-in fails (the login page's user cards, a known
// flake). Node 22 built-ins only. Loopback only, never port 8096; tokens are never printed.
//
//   node scripts/accessroles-admin-page-probe.mjs --sandbox access [--webcheck] [--out result.json]
//
// Start the sandbox first (scripts/README.md, this probe's section). Prints one JSON result (pass, checks,
// transcript, evidence) and exits with 0 when every check passed.

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
const PREFIX = 'Page probe ';
const PROBE_USER = 'pageprobe-';
const KID = 'pageprobe-kid';
const TEEN = 'pageprobe-teen';
const KID_ROLE = `${PREFIX}Kid`;
const GUEST_ROLE = `${PREFIX}Guest`;
const RACE_ROLE = `${PREFIX}Race`;
const EFFECT_ROLE = `${PREFIX}Effect`;
const SOURCE_ROLE = `${PREFIX}Source`;
const TARGET_ROLE = `${PREFIX}Target`;
// The phone check's long name: 100 characters (the most a role's name has), one unbroken word after the prefix.
const LONG_ROLE = `${PREFIX}${'W'.repeat(100 - PREFIX.length)}`;
const KID_LOCKS = ['Profile', 'DisplayAccount', 'Home', 'Controls', 'QuickConnect', 'EnhancedPanel', 'AdminSurfaces', 'UserDirectory'];
const WEBCHECKS = { desktop: 'accessroles-admin-page-desktop.json', tv: 'accessroles-admin-page-tv.json', tvConfirm: 'accessroles-admin-page-tv-confirm.json', phone: 'accessroles-admin-page-phone.json' };

function usage() {
    return 'usage: node scripts/accessroles-admin-page-probe.mjs --sandbox <name> [--webcheck] [--out <file>]';
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
const credentials = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
if (!credentials.admin || !credentials.admin.token) throw new Error('no admin in credentials.json; run Initialize-Sandbox.ps1');

const yaml = fs.readFileSync(path.join(repoRoot, 'plugins', PLUGIN, 'build.yaml'), 'utf8');
const scalar = (key) => (new RegExp(`^${key}:\\s*"?([^"\\r\\n]+)"?\\s*$`, 'm').exec(yaml) || [])[1];
const plugin = { name: scalar('name'), guid: scalar('guid'), version: scalar('version') };

// The sandbox's files: inside .sandbox, never under ProgramData\Jellyfin.
function assertInSandbox(p) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), path.resolve(p));
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(path.resolve(p))) throw new Error(`refusing to touch ${p}`);
    return path.resolve(p);
}
const logFolder = assertInSandbox(path.join(sandboxRoot, 'log'));

const checks = [];
const transcript = [];
const probeStarted = new Date();
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, startedUtc: state.StartedUtc, time: probeStarted.toISOString() }, plugin };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

// A request as the admin (credentials.json's token, never printed) or anonymous (null).
function request(method, p, user = 'admin', body = null, headersExtra = {}) {
    return new Promise((resolve, reject) => {
        const token = user === 'admin' ? credentials.admin.token : null;
        const headers = { Authorization: `MediaBrowser Client="accessroles-admin-page-probe", Device="sandbox", DeviceId="accessroles-admin-page-probe", Version="1.0.0"${token ? `, Token="${token}"` : ''}`, Accept: '*/*', ...headersExtra };
        if (body !== null) headers['Content-Type'] = 'application/json';
        const req = http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let json = null;
                try { json = text && /json/.test(res.headers['content-type'] || '') ? JSON.parse(text) : null; } catch { json = null; }
                resolve({ status: res.statusCode, type: res.headers['content-type'] || '', headers: res.headers, text, json });
            });
        });
        req.on('error', reject);
        req.setTimeout(60000, () => req.destroy(new Error(`timeout ${method} ${p}`)));
        if (body !== null) req.write(JSON.stringify(body));
        req.end();
    });
}

// One admin request of the walkthrough, as curl would send it (the token as <admin>, never its value).
async function step(label, method, p, body = null) {
    const r = await request(method, p, 'admin', body);
    transcript.push({ step: label, curl: `curl -X ${method} "http://127.0.0.1:${port}${baseUrl}${p}" -H "Authorization: MediaBrowser Token=<admin>"${body !== null ? ` -d '${JSON.stringify(body).slice(0, 300)}'` : ''}`, status: r.status, code: r.json && r.json.Code ? r.json.Code : undefined });
    return r;
}

const idN = (x) => (x || '').replace(/-/g, '').toLowerCase();
const stable = (value) => JSON.stringify(value, (k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.keys(v).sort().map((key) => [key, v[key]])) : v));
const hasRoleNow = (row, roleId) => (row.RoleIds || []).some((id) => idN(id) === idN(roleId));
const access = (policy) => ({ EnableAllFolders: policy.EnableAllFolders, EnabledFolders: [...(policy.EnabledFolders || [])].map(idN).sort() });
const usersRows = async () => (await request('GET', '/AccessRoles/Admin/Users')).json || [];
const rowOfUser = async (user) => (await usersRows()).find((r) => idN(r.UserId) === idN(user.Id)) || {};
const names = (users) => (users || []).map((u) => u.Name).sort();
const rolesList = async () => (await request('GET', '/AccessRoles/Admin/Roles')).json || [];

async function waitFor(label, fn, timeoutMs = 30000) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
        const value = await fn();
        if (value) return value;
        if (Date.now() > deadline) throw new Error(`timed out waiting for ${label}`);
        await delay(500);
    }
}

// ---- the probe's own users ----------------------------------------------------------------------------------------------
const own = new Map();
async function createOwnUser(name) {
    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);
    // A random password nobody needs: the webchecks sign in as credentials.json's admin; it is never printed or saved.
    const password = crypto.randomBytes(24).toString('base64url');
    const r = await request('POST', '/Users/New', 'admin', { Name: name, Password: password });
    transcript.push({ step: `create the probe's user ${name}`, curl: `curl -X POST "http://127.0.0.1:${port}${baseUrl}/Users/New" -H "Authorization: MediaBrowser Token=<admin>" -d '{"Name":"${name}","Password":"<random>"}'`, status: r.status });
    if (r.status !== 200 || !r.json || !r.json.Id) throw new Error(`POST /Users/New ${name}: ${r.status}`);
    own.set(idN(r.json.Id), name);
    return r.json;
}

async function deleteOwnUser(id) {
    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);
    const r = await request('DELETE', `/Users/${id}`, 'admin');
    transcript.push({ step: `delete the probe's user ${own.get(idN(id))}`, status: r.status });
    if (r.status !== 204) throw new Error(`delete the probe's user ${id}: ${r.status}`);
    own.delete(idN(id));
}

// ---- the webchecks ------------------------------------------------------------------------------------------------------
// Whether a webcheck's run failed in its first sign-in only (the login page's user cards, a known flake).
function signInFlake(result) {
    const steps = (result && result.steps) || [];
    const failed = steps.find((s) => !s.ok);
    return !!failed && failed.action === 'login' && steps.find((s) => s.action === 'login') === failed;
}

async function runWebcheck(file) {
    const attempts = [];
    let result = null;
    let run = null;
    for (let attempt = 1; attempt <= 5; attempt++) {
        if (attempt > 1) await delay(5000);
        run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', file], { encoding: 'utf8', timeout: 300000 });
        try { result = JSON.parse(run.stdout); } catch { result = null; }
        const failed = result && (result.steps || []).find((s) => !s.ok);
        attempts.push({ attempt, status: run.status, failedStep: failed ? failed.name : null, error: failed ? failed.error : (result ? result.error : (run.stderr || '').slice(0, 300)) });
        if (run.status === 0 && result && result.ok === true) break;
        if (!signInFlake(result)) break;
    }
    const failedRequests = ((result && result.failedRequests) || []).filter((f) => /\/AccessRoles\//i.test(f.url || ''));
    return {
        ok: !!run && run.status === 0 && !!result && result.ok === true,
        attempts,
        failedRequests,
        exceptions: ((result && result.exceptions) || []).filter((e) => !e.known).map((e) => (e.text || '').split('\n')[0]),
        screenshots: ((result && result.screenshots) || []).map((f) => path.relative(repoRoot, f)),
        // The requests a delayRequests step held (the desktop's late dry run), so the race is shown to have happened.
        delayed: ((result && result.delayedRequests) || []).map((d) => ({ url: (d.url || '').replace(/^https?:\/\/[^/]+/, ''), ms: d.ms })),
        steps: result ? (result.steps || []).map((s) => ({ name: s.name, ok: s.ok, value: s.value, error: s.error })) : (run && run.stderr || '').slice(0, 2000)
    };
}

// ---- cleanup: everything of the probe's, through the API ----------------------------------------------------------------
const cleanupErrors = [];
async function cleanup(label) {
    for (const u of (await usersRows()).filter((x) => x.State !== 'NotManaged' && x.Name.toLowerCase().startsWith(PROBE_USER))) {
        // The probe's own users only, deleted next: Keep needs no original access recorded.
        await step(`${label}: unmanage ${u.Name} (Keep)`, 'POST', `/AccessRoles/Admin/Users/${u.UserId}/Unmanage`, { Choice: 'Keep' });
    }
    for (const r of (await rolesList()).filter((x) => x.Name.startsWith(PREFIX))) {
        await step(`${label}: delete ${r.Name}`, 'DELETE', `/AccessRoles/Admin/Roles/${r.Id}?onUnmanage=Keep`);
    }
    for (const u of ((await request('GET', '/Users')).json || []).filter((x) => x.Name.toLowerCase().startsWith(PROBE_USER))) {
        own.set(idN(u.Id), u.Name);
        try {
            await deleteOwnUser(u.Id);
        } catch (e) {
            cleanupErrors.push(`${label}: ${e.message}`);
        }
    }
}

function logSince(since) {
    const lines = [];
    for (const file of fs.readdirSync(logFolder).filter((f) => /^log_\d{8}\.log$/.test(f)).sort()) {
        for (const line of fs.readFileSync(path.join(logFolder, file), 'utf8').split(/\r?\n/)) {
            const m = /^\[(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2})\] \[(\w{3})\]/.exec(line);
            if (m && Date.parse(`${m[1]}T${m[2]}${m[3]}`) >= since - 1000) lines.push({ level: m[4], text: line });
        }
    }
    return lines;
}

let adminId = null;
let others = [];
const othersBefore = {};
let defaultRolesBefore = null;

async function main() {
    const info = await request('GET', '/System/Info/Public', null);
    check('the sandbox answers, Jellyfin 10.11.5', info.status === 200 && info.json && info.json.Version === '10.11.5', { status: info.status, version: info.json && info.json.Version });
    const installed = (await request('GET', '/Plugins')).json || [];
    const ours = installed.find((p) => idN(p.Id) === idN(plugin.guid));
    check('Access Roles is loaded and Active, at the build.yaml version', !!ours && ours.Version === plugin.version && ours.Status === 'Active', ours ? { version: ours.Version, status: ours.Status } : 'not listed');
    adminId = idN(((await request('GET', '/Users/Me')).json || {}).Id);
    await cleanup('earlier run');

    // The default roles would be given to the probe's new users; the probe needs none and changes none.
    defaultRolesBefore = (await rolesList()).filter((r) => r.IsDefault).map((r) => r.Name).sort();
    if (!check('no default role for new users (the probe\'s users must start unmanaged)', defaultRolesBefore.length === 0, defaultRolesBefore)) return;
    others = ((await request('GET', '/Users')).json || []).filter((u) => !u.Name.toLowerCase().startsWith(PROBE_USER));
    for (const u of others) othersBefore[u.Id] = stable(u.Policy);
    evidence.managedBefore = (await usersRows()).filter((x) => x.State !== 'NotManaged').map((x) => x.Name);

    // ---------------------------------------------------------------------------------------------------------------
    // 1. The page: the skeleton, the stub, the anonymous module.
    const html = await request('GET', '/web/ConfigurationPage?name=AccessRoles');
    const stub = await request('GET', '/web/ConfigurationPage?name=AccessRolesJs');
    const module = await request('GET', '/AccessRoles/AdminScript', null);
    const etag = module.headers.etag;
    const again = etag ? await request('GET', '/AccessRoles/AdminScript', null, null, { 'If-None-Match': etag }) : { status: 0 };
    const files = (/ (\d+) plugin file\(s\) of Web\/admin\/js\. /.exec(module.text) || [])[1];
    evidence.page = { page: { status: html.status, bytes: html.text.length }, stub: { status: stub.status, bytes: stub.text.length }, module: { status: module.status, type: module.type, bytes: module.text.length, etag, files, cacheControl: module.headers['cache-control'] }, revalidated: again.status };
    check('the page is the skeleton whose data-controller is the AccessRolesJs stub, with no script of its own; the stub imports the AdminScript module',
        html.status === 200 && html.text.includes('id="AccessRolesConfigPage"') && html.text.includes('data-controller="__plugin/AccessRolesJs"') && html.text.includes('id="AccessRolesRoot"') && !/<script\b/i.test(html.text)
            && stub.status === 200 && stub.text.includes("import(ApiClient.getUrl('AccessRoles/AdminScript'))") && stub.text.includes('export default function'),
        evidence.page);
    check('GET /AccessRoles/AdminScript answers anonymously: static JavaScript from the fourteen Web/admin/js files, exporting mount, the build\'s MVID as its ETag (304 when it matches)',
        module.status === 200 && /^text\/javascript/.test(module.type) && module.text.startsWith(`/* Access Roles ${plugin.version} dashboard module, kit `) && files === '14'
            && module.text.trimEnd().endsWith('export { mount };') && /^"[0-9a-f]{32}"$/.test(etag || '') && again.status === 304,
        evidence.page.module);

    // The probe's users, before the TV confirm (whose import race changes the kid's library access and puts it back).
    const kid = await createOwnUser(KID);
    const teen = await createOwnUser(TEEN);
    const accessAtStart = { [KID]: access(kid.Policy), [TEEN]: access(teen.Policy) };
    evidence.accessAtStart = accessAtStart;

    // ---------------------------------------------------------------------------------------------------------------
    // 1b. The kit's confirm on the TV layout, while the import is offered (nothing stored yet).
    if (opts.webcheck) {
        const diagnostics = async () => (await request('GET', '/AccessRoles/Admin/Diagnostics')).json || {};
        const before = await diagnostics();
        const offered = !!before.Records && before.Records.Assignments === 0 && !(before.Store && before.Store.ReadOnly);
        if (offered) {
            const run = await runWebcheck(WEBCHECKS.tvConfirm);
            evidence.tvConfirm = run;
            const after = await diagnostics();
            // The one refusal expected: the import's Apply (no dry run, its preview's PreviewToken) after the kid's change.
            const refusedImport = (f) => f.status === 409 && f.method === 'POST' && /\/AccessRoles\/Admin\/ImportExisting\?/.test(f.url || '') && !/[?&]dryRun=true/.test(f.url || '') && /[?&]ifPreview=[0-9a-f]{32}(&|$)/.test(f.url || '');
            const kidNow = access((await request('GET', `/Users/${kid.Id}`)).json.Policy);
            check('the kit\'s confirm on the TV layout (the import\'s Apply): the page opens on Overview while the import is offered although the Users tab is remembered; the confirm takes the focus on Cancel, the D-pad reaches its confirm button and goes back, OK on Cancel and Back dismiss it with the focus back on Apply; after another administrator changed the kid\'s library access, OK on the confirm\'s Apply sends the import with its preview\'s PreviewToken and is refused (409 Changed naming the kid, Apply off, the focus on Preview); nothing imported (still no assignment, no role more), the kid\'s access as the probe created it',
                run.ok && run.failedRequests.length === 1 && refusedImport(run.failedRequests[0]) && after.Records.Assignments === 0 && after.Records.Roles === before.Records.Roles && stable(kidNow) === stable(accessAtStart[KID]),
                { attempts: run.attempts, failedRequests: run.failedRequests.map((f) => ({ method: f.method, status: f.status, url: (f.url || '').replace(/^https?:\/\/[^/]+/, '').replace(/ifPreview=[0-9a-f]+/, 'ifPreview=<token>') })), exceptions: run.exceptions, records: { before: before.Records, after: after.Records }, kid: kidNow, failed: Array.isArray(run.steps) ? run.steps.filter((x) => !x.ok) : run.steps });
        } else {
            evidence.tvConfirm = { skipped: 'the import is not offered', records: before.Records, readOnly: !!(before.Store && before.Store.ReadOnly) };
            check('the kit\'s confirm on the TV layout: skipped, the import is not offered (assignments are stored); it runs on a sandbox with none', true, evidence.tvConfirm);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2. The flows in the web client, at 1366 px.
    if (opts.webcheck) {
        const desktop = await runWebcheck(WEBCHECKS.desktop);
        evidence.desktop = desktop;
        // The flow's expected refusals: the Delete preview's dry run before the choice is made (400 with its Preview), and
        // the races' 409 Changed: the bulk's Assign twice (a user changed, then the role; the second has the preview's
        // PreviewToken), the role's Delete once. Nothing else of Access Roles' fails.
        const failedKind = (f) => {
            const dry = /[?&]dryRun=true/.test(f.url || '');
            if (f.status === 400 && f.method === 'DELETE' && dry && /\/AccessRoles\/Admin\/Roles\/[0-9a-f-]+\?/i.test(f.url)) return 'deleteChoice';
            if (f.status === 409 && f.method === 'POST' && !dry && /\/AccessRoles\/Admin\/Users\/Bulk(\?|$)/.test(f.url)) return 'bulkChanged';
            if (f.status === 409 && f.method === 'DELETE' && !dry && /\/AccessRoles\/Admin\/Roles\/[0-9a-f-]+(\?|$)/i.test(f.url)) return 'deleteChanged';
            return null;
        };
        const desktopFailed = { deleteChoice: 0, bulkChanged: 0, deleteChanged: 0, unexpected: [] };
        for (const f of desktop.failedRequests) {
            const kind = failedKind(f);
            if (kind) desktopFailed[kind]++;
            else desktopFailed.unexpected.push(f);
        }
        check('the web client (1366 px) as admin: a role from the Kid preset (its locks, Only…, the Dashboard row\'s sentence following its box, a library picked; "Pick at least one library." first) previewed and created; Assign roles to both probe users previewed per user, a 409 Changed naming the kid after another administrator changed them (nothing written, Assign disabled), Reload and keep my changes, then a 409 Changed naming both after another administrator added a lock to the role (the preview\'s PreviewToken; nothing written), Reload and keep my changes (Subtitles newly locked), saved; the kid\'s panel with its effective access and sources, a Home override Unlock previewed and saved; Unmanage user with the choice asked first, a late dry run (the held one, Keep\'s) dropped, Restore saved; the role\'s Delete with its last member leaving management (Restore), a 409 Changed naming the teen after another administrator changed them, Reload and keep my changes, deleted; every dialog the kit\'s, the focus back where it was, no uncaught exception; of Access Roles\' requests only the Delete preview\'s refused dry run (no choice yet: 400 with its preview) and the three races\' 409 Changed failed',
            desktop.ok && desktopFailed.unexpected.length === 0 && desktopFailed.bulkChanged === 2 && desktopFailed.deleteChanged === 1 && desktop.delayed.length === 1 && /\/Unmanage\?/.test(desktop.delayed[0].url),
            { attempts: desktop.attempts, failedRequests: desktopFailed, delayed: desktop.delayed, exceptions: desktop.exceptions, failed: Array.isArray(desktop.steps) ? desktop.steps.filter((s) => !s.ok) : desktop.steps });

        if (desktop.ok) {
            // What the page wrote, through the API.
            const rows = await usersRows();
            const rowOf = (name) => rows.find((r) => r.Name === name) || {};
            const roleLeft = (await rolesList()).filter((r) => r.Name === KID_ROLE);
            check('afterwards (the API): the page\'s role is deleted, and both probe users are unmanaged',
                roleLeft.length === 0 && rowOf(KID).State === 'NotManaged' && rowOf(TEEN).State === 'NotManaged' && rowOf(KID).RoleIds.length === 0 && rowOf(TEEN).RoleIds.length === 0,
                { roles: roleLeft.map((r) => r.Name), kid: { State: rowOf(KID).State, RoleIds: rowOf(KID).RoleIds }, teen: { State: rowOf(TEEN).State, RoleIds: rowOf(TEEN).RoleIds } });

            // The library sync wrote the role's one library and, after Restore, their access as it was.
            let restored = null;
            try {
                restored = await waitFor('the library sync restores both users\' library access', async () => {
                    const now = {};
                    for (const [name, user] of [[KID, kid], [TEEN, teen]]) now[name] = access((await request('GET', `/Users/${user.Id}`)).json.Policy);
                    return stable(now) === stable(accessAtStart) ? now : null;
                }, 60000);
            } catch (e) {
                restored = null;
            }
            check('the library sync wrote both users\' library access back as it was when the probe created them (Restore, chosen in the page)', !!restored, restored || { expected: accessAtStart });

            const audit = (await request('GET', `/AccessRoles/Admin/Audit?limit=200&fromUtc=${encodeURIComponent(probeStarted.toISOString())}`)).json || {};
            const lines = (audit.Items || []).filter((l) => (l.TargetName || '').startsWith(PREFIX) || own.has(idN(l.TargetUserId)));
            const by = (action, test) => lines.filter((l) => l.Action === action && test(l));
            const adminLine = (l) => l.Actor && l.Actor.Kind === 'Admin' && idN(l.Actor.UserId) === adminId;
            const forUser = (user) => (l) => idN(l.TargetUserId) === idN(user.Id);
            evidence.audit = lines.map((l) => ({ Action: l.Action, Actor: l.Actor && l.Actor.Kind, TargetName: l.TargetName }));
            check('the audit has each write of the page, by the admin: RoleCreated and RoleDeleted of the role, AssignmentChanged for the kid (the bulk, the override, the unmanage) and the teen (the bulk, the role\'s deletion)',
                by('RoleCreated', (l) => l.TargetName === KID_ROLE && adminLine(l)).length === 1 && by('RoleDeleted', (l) => l.TargetName === KID_ROLE && adminLine(l)).length === 1
                    && by('AssignmentChanged', forUser(kid)).filter(adminLine).length >= 3 && by('AssignmentChanged', forUser(teen)).filter(adminLine).length >= 1,
                evidence.audit);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2b. The races through the API, each with its negative control: what the dry run read is what the write needs.
    const race = await step('the probe\'s lock-only race role', 'POST', '/AccessRoles/Admin/Roles', { Name: RACE_ROLE, Locks: ['Profile'], Libraries: { Mode: 'NotManaged', Refs: [] } });
    if (race.status !== 200 || !race.json || !race.json.Role) throw new Error(`create ${RACE_ROLE}: ${race.status} ${race.text.slice(0, 200)}`);
    const raceId = race.json.Role.Id;
    const hasRace = (row) => (row.RoleIds || []).some((id) => idN(id) === idN(raceId));
    const kidAtRace = await rowOfUser(kid);
    const teenAtRace = await rowOfUser(teen);
    const bulkBody = { UserIds: [kid.Id, teen.Id], IfUpdatedAt: [{ UserId: kid.Id, UpdatedUtc: kidAtRace.UpdatedUtc || null }, { UserId: teen.Id, UpdatedUtc: teenAtRace.UpdatedUtc || null }], AddRoleIds: [raceId] };
    const bulkDry = await step('Users/Bulk\'s dry run: the race role for both, with each user\'s IfUpdatedAt', 'POST', '/AccessRoles/Admin/Users/Bulk?dryRun=true', bulkBody);
    const bulkOther = await step(`another administrator: ${KID} gets a Playback lock of their own`, 'PUT', `/AccessRoles/Admin/Users/${kid.Id}`, { RoleIds: [], LockOverrides: { Playback: 'Lock' }, IfUpdatedAt: kidAtRace.UpdatedUtc || null });
    const bulkReal = await step('Users/Bulk with the dry run\'s IfUpdatedAt', 'POST', '/AccessRoles/Admin/Users/Bulk', bulkBody);
    const afterBulk = { kid: await rowOfUser(kid), teen: await rowOfUser(teen) };
    evidence.bulkRace = { dryRun: bulkDry.status, other: bulkOther.status, real: { status: bulkReal.status, code: bulkReal.json && bulkReal.json.Code, users: names(bulkReal.json && bulkReal.json.Users) }, after: { kid: { State: afterBulk.kid.State, race: hasRace(afterBulk.kid) }, teen: { State: afterBulk.teen.State, race: hasRace(afterBulk.teen) } } };
    check('Users/Bulk with its dry run\'s IfUpdatedAt, after another administrator changed one of its users: 409 Changed naming that user only, and nothing written (neither user has the role, the teen still unmanaged)',
        bulkDry.status === 200 && bulkOther.status === 200 && bulkReal.status === 409 && bulkReal.json && bulkReal.json.Code === 'Changed' && stable(names(bulkReal.json.Users)) === stable([KID])
            && !hasRace(afterBulk.kid) && !hasRace(afterBulk.teen) && afterBulk.teen.State === 'NotManaged',
        evidence.bulkRace);
    const bulkControl = await step('the negative control: the same Users/Bulk without IfUpdatedAt', 'POST', '/AccessRoles/Admin/Users/Bulk', { UserIds: bulkBody.UserIds, AddRoleIds: bulkBody.AddRoleIds });
    const afterControl = { kid: await rowOfUser(kid), teen: await rowOfUser(teen) };
    evidence.bulkRace.control = { status: bulkControl.status, kid: hasRace(afterControl.kid), teen: hasRace(afterControl.teen) };
    check('the negative control: the same bulk without IfUpdatedAt is saved, and both users have the role', bulkControl.status === 200 && hasRace(afterControl.kid) && hasRace(afterControl.teen), evidence.bulkRace.control);

    const raceRole = (await rolesList()).find((r) => idN(r.Id) === idN(raceId)) || {};
    const members = `ifMembers=${encodeURIComponent(raceRole.MembersToken || '')}&onUnmanage=Keep`;
    const deleteDry = await step('DELETE Roles\' dry run with the role\'s MembersToken as ifMembers', 'DELETE', `/AccessRoles/Admin/Roles/${raceId}?dryRun=true&${members}`);
    const deleteOther = await step(`another administrator: ${TEEN} gets a Subtitles lock of their own (keeping the role)`, 'PUT', `/AccessRoles/Admin/Users/${teen.Id}`, { RoleIds: afterControl.teen.RoleIds, LockOverrides: { Subtitles: 'Lock' }, IfUpdatedAt: afterControl.teen.UpdatedUtc });
    const deleteReal = await step('DELETE Roles with the dry run\'s ifMembers', 'DELETE', `/AccessRoles/Admin/Roles/${raceId}?${members}`);
    const kept = (await rolesList()).some((r) => idN(r.Id) === idN(raceId));
    evidence.deleteRace = { token: !!raceRole.MembersToken, dryRun: deleteDry.status, other: deleteOther.status, real: { status: deleteReal.status, code: deleteReal.json && deleteReal.json.Code, users: names(deleteReal.json && deleteReal.json.Users) }, kept };
    check('DELETE Roles with its dry run\'s ifMembers, after another administrator changed a member: 409 Changed naming that member only, and the role is kept',
        !!raceRole.MembersToken && deleteDry.status === 200 && deleteOther.status === 200 && deleteReal.status === 409 && deleteReal.json && deleteReal.json.Code === 'Changed' && stable(names(deleteReal.json.Users)) === stable([TEEN]) && kept,
        evidence.deleteRace);
    const deleteControl = await step('the negative control: the same DELETE without ifMembers', 'DELETE', `/AccessRoles/Admin/Roles/${raceId}?onUnmanage=Keep`);
    const gone = !(await rolesList()).some((r) => idN(r.Id) === idN(raceId));
    evidence.deleteRace.control = { status: deleteControl.status, gone };
    check('the negative control: the same DELETE without ifMembers deletes the role', deleteControl.status === 200 && gone, evidence.deleteRace.control);
    for (const [name, user] of [[KID, kid], [TEEN, teen]]) {
        await step(`${name} unmanaged again (Keep: the race role managed no library)`, 'POST', `/AccessRoles/Admin/Users/${user.Id}/Unmanage`, { Choice: 'Keep' });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2c. Two stale-definition races through the API, each with its negative control: what the change does is what its dry
    // run showed (PreviewToken, ifPreview), a role's definition included.
    const visible = async (user) => {
        const e = (await request('GET', `/AccessRoles/Admin/Users/${user.Id}/Effective`)).json || {};
        return { libraries: ((e.Libraries && e.Libraries.Visible) || []).length, locked: Object.entries(e.Locks || {}).filter(([, v]) => v && v.Locked).map(([k]) => k).sort() };
    };
    const created = async (body) => {
        const r = await step(`the probe's lock-only role ${body.Name}`, 'POST', '/AccessRoles/Admin/Roles', body);
        if (r.status !== 200 || !r.json || !r.json.Role) throw new Error(`create ${body.Name}: ${r.status} ${r.text.slice(0, 200)}`);
        return r.json.Role;
    };
    const roleNow = async (id) => (await rolesList()).find((r) => idN(r.Id) === idN(id)) || null;
    const allLibraries = async (role, locks) => step(`another administrator gives ${role.Name} every library and the locks ${locks.join(', ')}`, 'PUT', `/AccessRoles/Admin/Roles/${role.Id}`,
        { Name: role.Name, Locks: locks, Libraries: { Mode: 'All', Refs: [] }, IfUpdatedAt: (await roleNow(role.Id)).UpdatedUtc });

    // Bulk: a role edited between the dry run and the save.
    const effect = await created({ Name: EFFECT_ROLE, Locks: ['Profile'], Libraries: { Mode: 'NotManaged', Refs: [] } });
    const kidAtEffect = await rowOfUser(kid);
    const effectBody = { UserIds: [kid.Id], IfUpdatedAt: [{ UserId: kid.Id, UpdatedUtc: kidAtEffect.UpdatedUtc || null }], AddRoleIds: [effect.Id] };
    const effectDry = await step('Users/Bulk\'s dry run: the lock-only role for the kid', 'POST', '/AccessRoles/Admin/Users/Bulk?dryRun=true', effectBody);
    const effectToken = effectDry.json && effectDry.json.Preview && effectDry.json.Preview.PreviewToken;
    const effectPreviewed = effectDry.json && effectDry.json.Preview && (effectDry.json.Preview.Users || [])[0];
    const effectEdit = await allLibraries(effect, ['Profile', 'Playback', 'Home']);
    const effectReal = await step('Users/Bulk with the dry run\'s IfUpdatedAt and its PreviewToken as ifPreview', 'POST', `/AccessRoles/Admin/Users/Bulk?ifPreview=${encodeURIComponent(effectToken || '')}`, effectBody);
    const kidAfterEffect = await rowOfUser(kid);
    const kidSeesAfterEffect = await visible(kid);
    evidence.bulkEffect = {
        token: /^[0-9a-f]{32}$/.test(effectToken || ''), dryRun: effectDry.status, previewed: effectPreviewed && { librariesAdded: (effectPreviewed.LibrariesAdded || []).length, locksAdded: effectPreviewed.LocksAdded },
        edit: effectEdit.status, real: { status: effectReal.status, code: effectReal.json && effectReal.json.Code, users: names(effectReal.json && effectReal.json.Users) },
        after: { roles: (kidAfterEffect.RoleIds || []).length, state: kidAfterEffect.State, sees: kidSeesAfterEffect }
    };
    check('A role changed after the dry run: Users/Bulk adding a lock-only role, after another administrator gave that role every library and two locks more: with the dry run\'s IfUpdatedAt and its PreviewToken as ifPreview, a 409 Changed naming the kid, and nothing written (no role, the kid still unmanaged)',
        effectDry.status === 200 && evidence.bulkEffect.token && effectPreviewed && (effectPreviewed.LibrariesAdded || []).length === 0 && effectEdit.status === 200
            && effectReal.status === 409 && effectReal.json && effectReal.json.Code === 'Changed' && stable(names(effectReal.json.Users)) === stable([KID])
            && (kidAfterEffect.RoleIds || []).length === 0 && kidAfterEffect.State === 'NotManaged',
        evidence.bulkEffect);
    const effectAgain = await step('the negative control: Users/Bulk\'s dry run again', 'POST', '/AccessRoles/Admin/Users/Bulk?dryRun=true', effectBody);
    const effectAgainToken = effectAgain.json && effectAgain.json.Preview && effectAgain.json.Preview.PreviewToken;
    const effectControl = await step('the negative control: Users/Bulk with that dry run\'s PreviewToken', 'POST', `/AccessRoles/Admin/Users/Bulk?ifPreview=${encodeURIComponent(effectAgainToken || '')}`, effectBody);
    const kidSeesNow = await visible(kid);
    evidence.bulkEffect.control = { tokenChanged: !!effectAgainToken && effectAgainToken !== effectToken, librariesAdded: ((((effectAgain.json || {}).Preview || {}).Users || [])[0] || {}).LibrariesAdded, status: effectControl.status, kid: kidSeesNow };
    check('the negative control: the dry run seen now (another PreviewToken, the libraries added) and the bulk with its token is saved: the kid has the role, every library and its locks',
        effectAgain.status === 200 && evidence.bulkEffect.control.tokenChanged && effectControl.status === 200 && hasRoleNow(await rowOfUser(kid), effect.Id)
            && kidSeesNow.libraries > 0 && ['Home', 'Playback', 'Profile'].every((l) => kidSeesNow.locked.includes(l)),
        evidence.bulkEffect.control);

    // DELETE with reassignTo: the target edited between the dry run and the delete.
    const source = await created({ Name: SOURCE_ROLE, Locks: ['Profile'], Libraries: { Mode: 'NotManaged', Refs: [] } });
    const target = await created({ Name: TARGET_ROLE, Locks: ['Subtitles'], Libraries: { Mode: 'NotManaged', Refs: [] } });
    const teenAtSource = await rowOfUser(teen);
    const given = await step(`${TEEN} gets ${SOURCE_ROLE}`, 'PUT', `/AccessRoles/Admin/Users/${teen.Id}`, { RoleIds: [source.Id], IfUpdatedAt: teenAtSource.UpdatedUtc || null });
    const sourceNow = await roleNow(source.Id);
    const reassign = `reassignTo=${idN(target.Id)}&ifUpdatedAt=${encodeURIComponent(sourceNow.UpdatedUtc)}&ifMembers=${encodeURIComponent(sourceNow.MembersToken || '')}&onUnmanage=Keep`;
    const moveDry = await step('DELETE Roles\' dry run of the source role, its members reassigned to the target, with ifUpdatedAt and ifMembers', 'DELETE', `/AccessRoles/Admin/Roles/${source.Id}?dryRun=true&${reassign}`);
    const moveToken = moveDry.json && moveDry.json.Preview && moveDry.json.Preview.PreviewToken;
    const moveEdit = await allLibraries(target, ['Subtitles', 'Playback']);
    const moveReal = await step('DELETE Roles with those tokens and the dry run\'s PreviewToken as ifPreview', 'DELETE', `/AccessRoles/Admin/Roles/${source.Id}?${reassign}&ifPreview=${encodeURIComponent(moveToken || '')}`);
    const teenAfterMove = await rowOfUser(teen);
    const sourceKept = !!(await roleNow(source.Id));
    evidence.deleteEffect = {
        given: given.status, token: /^[0-9a-f]{32}$/.test(moveToken || ''), dryRun: moveDry.status, edit: moveEdit.status,
        real: { status: moveReal.status, code: moveReal.json && moveReal.json.Code, users: names(moveReal.json && moveReal.json.Users) },
        after: { sourceKept, teenRoles: (teenAfterMove.RoleIds || []).map((id) => (idN(id) === idN(source.Id) ? 'source' : (idN(id) === idN(target.Id) ? 'target' : 'other'))) }
    };
    check('A target role changed after the dry run: DELETE Roles reassigning its members to a role another administrator then gave every library and a Playback lock: with ifUpdatedAt, ifMembers and the dry run\'s PreviewToken, a 409 Changed naming the teen, the role kept and nobody moved',
        given.status === 200 && moveDry.status === 200 && evidence.deleteEffect.token && moveEdit.status === 200 && moveReal.status === 409 && moveReal.json && moveReal.json.Code === 'Changed'
            && stable(names(moveReal.json.Users)) === stable([TEEN]) && sourceKept && stable(evidence.deleteEffect.after.teenRoles) === stable(['source']),
        evidence.deleteEffect);
    const sourceAgain = await roleNow(source.Id);
    const reassignAgain = `reassignTo=${idN(target.Id)}&ifUpdatedAt=${encodeURIComponent(sourceAgain.UpdatedUtc)}&ifMembers=${encodeURIComponent(sourceAgain.MembersToken || '')}&onUnmanage=Keep`;
    const moveAgain = await step('the negative control: DELETE Roles\' dry run again', 'DELETE', `/AccessRoles/Admin/Roles/${source.Id}?dryRun=true&${reassignAgain}`);
    const moveAgainToken = moveAgain.json && moveAgain.json.Preview && moveAgain.json.Preview.PreviewToken;
    const moveControl = await step('the negative control: DELETE Roles with that dry run\'s PreviewToken', 'DELETE', `/AccessRoles/Admin/Roles/${source.Id}?${reassignAgain}&ifPreview=${encodeURIComponent(moveAgainToken || '')}`);
    const teenMoved = await rowOfUser(teen);
    evidence.deleteEffect.control = { tokenChanged: !!moveAgainToken && moveAgainToken !== moveToken, status: moveControl.status, sourceGone: !(await roleNow(source.Id)), teenHasTarget: hasRoleNow(teenMoved, target.Id) };
    check('the negative control: the dry run seen now (another PreviewToken) and the DELETE with its token deletes the role and moves the teen to the target',
        moveAgain.status === 200 && evidence.deleteEffect.control.tokenChanged && moveControl.status === 200 && evidence.deleteEffect.control.sourceGone && evidence.deleteEffect.control.teenHasTarget,
        evidence.deleteEffect.control);
    for (const [name, user] of [[KID, kid], [TEEN, teen]]) {
        await step(`${name} unmanaged again (Keep)`, 'POST', `/AccessRoles/Admin/Users/${user.Id}/Unmanage`, { Choice: 'Keep' });
    }
    for (const role of [effect, target]) {
        await step(`delete ${role.Name}`, 'DELETE', `/AccessRoles/Admin/Roles/${role.Id}?onUnmanage=Keep`);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 3. Effective carries the stored assignment (the panel's source).
    const guest = await step('the probe\'s lock-only role', 'POST', '/AccessRoles/Admin/Roles', { Name: GUEST_ROLE, Locks: ['Profile', 'Home'], Libraries: { Mode: 'NotManaged', Refs: [] } });
    if (guest.status !== 200 || !guest.json || !guest.json.Role) throw new Error(`create ${GUEST_ROLE}: ${guest.status} ${guest.text.slice(0, 200)}`);
    const guestId = guest.json.Role.Id;
    // Nobody has it: the phone check finds its name on the Roles tab and in the panel's role list.
    const long = await step('the probe\'s lock-only role with a 100-character name', 'POST', '/AccessRoles/Admin/Roles', { Name: LONG_ROLE, Locks: ['Profile'], Libraries: { Mode: 'NotManaged', Refs: [] } });
    if (long.status !== 200 || !long.json || !long.json.Role) throw new Error(`create the long-named role: ${long.status} ${long.text.slice(0, 200)}`);
    const teenRow = (await usersRows()).find((r) => idN(r.UserId) === idN(teen.Id)) || {};
    const put = await step(`${TEEN} gets the probe's lock-only role, with Home unlocked for them`, 'PUT', `/AccessRoles/Admin/Users/${teen.Id}`, { RoleIds: [guestId], LockOverrides: { Home: 'Unlock' }, IfUpdatedAt: teenRow.UpdatedUtc });
    const effective = (await request('GET', `/AccessRoles/Admin/Users/${teen.Id}/Effective`)).json || {};
    const stored = effective.Assignment || {};
    evidence.effective = { Assignment: stored, Locks: { Profile: effective.Locks && effective.Locks.Profile, Home: effective.Locks && effective.Locks.Home } };
    check('Effective carries the stored assignment: the role, the Home override Unlock, the library mode Inherit and the state Managed; its Locks say Profile is locked by the role and Home unlocked for the user',
        put.status === 200 && stable((stored.RoleIds || []).map(idN)) === stable([idN(guestId)]) && stable(stored.LockOverrides) === stable({ Home: 'Unlock' }) && stored.State === 'Managed'
            && stored.Libraries && stored.Libraries.Mode === 'Inherit' && (stored.Libraries.Add || []).length === 0 && (stored.Libraries.Remove || []).length === 0
            && effective.Locks.Profile.Locked === true && effective.Locks.Profile.Source === `Role:${GUEST_ROLE}` && effective.Locks.Home.Locked === false && effective.Locks.Home.Source === 'UserOverride',
        evidence.effective);

    // ---------------------------------------------------------------------------------------------------------------
    // 4. The TV layout and a phone, reading only.
    if (opts.webcheck) {
        const before = stable((await usersRows()).find((r) => idN(r.UserId) === idN(teen.Id)));
        for (const [name, file, text] of [
            ['tv', WEBCHECKS.tv, 'the TV layout: the page opens on the remembered Users tab; every tab in the D-pad\'s reach and OK opens it; OK on the teen\'s name opens the panel with the focus inside; the D-pad moves within it; OK unticks and ticks a role; Back closes it unsaved, the focus back on the name'],
            ['phone', WEBCHECKS.phone, 'a 375 px touch phone: on Overview, Roles and Users nothing past the right edge and no sideways scroll (the status tables\' long values wrap), every button, checkbox row and select 44 px tall; the 100-character role name wrapping on the Roles tab, in its editor\'s title (the editor\'s Close icon 44 px both ways, its text fields 44 px tall) and in the panel; the teen\'s panel a sheet no wider than the screen, its controls and Close icon 44 px']
        ]) {
            const run = await runWebcheck(file);
            evidence[name] = run;
            check(`the web client as admin on ${text}; no uncaught exception, no failed request of Access Roles'`, run.ok && run.failedRequests.length === 0,
                { attempts: run.attempts, failedRequests: run.failedRequests, exceptions: run.exceptions, failed: Array.isArray(run.steps) ? run.steps.filter((s) => !s.ok) : run.steps });
        }
        const after = stable((await usersRows()).find((r) => idN(r.UserId) === idN(teen.Id)));
        check('the TV and phone checks wrote nothing (the teen\'s row as before)', before === after, null);
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
        const rows = await usersRows();
        cleanupResult.probeLeft = {
            roles: (await rolesList()).filter((r) => r.Name.startsWith(PREFIX)).length,
            users: ((await request('GET', '/Users')).json || []).filter((x) => x.Name.toLowerCase().startsWith(PROBE_USER)).length
        };
        cleanupResult.managed = rows.filter((x) => x.State !== 'NotManaged').map((x) => x.Name);
        cleanupResult.defaultRoles = (await rolesList()).filter((r) => r.IsDefault).map((r) => r.Name).sort();
        const changed = [];
        for (const other of others) {
            const now = (await request('GET', `/Users/${other.Id}`)).json;
            if (!now || stable(now.Policy) !== othersBefore[other.Id]) changed.push(other.Name);
        }
        cleanupResult.othersChanged = changed;
    } catch (e) {
        failure = (failure ? failure + '\n' : '') + 'cleanup: ' + (e.stack || String(e));
    }
    evidence.cleanup = cleanupResult;
    if (cleanupErrors.length) failure = (failure ? failure + ' | ' : '') + 'cleanup: ' + cleanupErrors.join('; ');
    if (!failure && defaultRolesBefore) {
        const left = cleanupResult.probeLeft || {};
        check('afterwards nothing of the probe\'s is left (its roles and users), the managed users and the default roles are as they were, and no policy of a user the probe didn\'t create was written',
            left.roles === 0 && left.users === 0 && stable(cleanupResult.managed) === stable(evidence.managedBefore) && stable(cleanupResult.defaultRoles) === stable(defaultRolesBefore)
                && cleanupResult.othersChanged.length === 0,
            cleanupResult);
        const log = logSince(probeStarted.getTime()).filter((l) => /Jellyfin\.Plugin\.AccessRoles/.test(l.text));
        const errors = log.filter((l) => l.level === 'ERR' || l.level === 'FTL');
        evidence.log = { lines: log.length, warnings: log.filter((l) => l.level === 'WRN').map((l) => l.text.slice(0, 240)), errors: errors.length };
        check('the log has no error of Access Roles\' since the probe started', errors.length === 0, errors.map((l) => l.text.slice(0, 240)));
    }
}

const result = { pass: !failure && checks.length > 0 && checks.every((c) => c.pass), failure, checks, transcript, evidence };
const text = JSON.stringify(result, null, 2);
if (opts.out) fs.writeFileSync(opts.out, text);
console.log(text);
process.exit(result.pass ? 0 : 1);
