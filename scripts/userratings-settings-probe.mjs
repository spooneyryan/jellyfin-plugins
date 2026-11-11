#!/usr/bin/env node
// User Ratings' Settings API in a running sandbox. Node 22 built-ins only.
// Loopback only, never port 8096. The sandbox users' tokens come from credentials.json and are never printed or written.
//
//   node scripts/userratings-settings-probe.mjs --sandbox default [--out result.json]
//
// It needs a running sandbox with User Ratings 0.3.17 or later (Initialize-Sandbox.ps1: the users admin, viewer and viewer2,
// the synthetic movie Long Test Movie with no rating of viewer's or viewer2's). As admin, viewer and viewer2:
//   1. who may call: nobody 401, a member 403 (the read and a dry run), the admin 200 with Cache-Control no-store;
//   2. the read: every setting with its default (ModerationLogDays 365), a 16-hex-digit Version, the users the
//      picker offers (by id without hyphens), the kinds RateableItemTypes offers, the settings that need a restart;
//   3. each setting alone in a dry run: that setting the one change, its label, a restart only for the Top Rated page,
//      Plugin Pages and where the pages live; nothing saved;
//   4. the refusals, each a save from the current version: a number one under or over its range, a name no setting
//      takes, only selected users with nobody (or only a user the server doesn't have), no kind, a kind not offered, the
//      Jellyfin copy turned on, a key that names no setting, a save without a version or with a malformed one, a value of
//      the wrong JSON type, a body over 64 KiB: each its status and code (InvalidSetting:<the setting>, UnknownSetting,
//      VersionNeeded; MVC's 400 for the wrong type), and nothing saved by any (the configuration byte for byte as before);
//   5. every setting but the Jellyfin copy changed in one save after its dry run; GET Settings and
//      PluginConfiguration hold each value, read again; then every one put back in one save;
//   6. two saves at once from one version: one saved, the other the 409 SettingsChanged naming the first's setting;
//   7. who may rate: only viewer chosen: viewer2's read says NotARater, their PUT 403 with its sentence, both ratings still
//      counted, viewer2 deletes their own older row; every user again: viewer2 rates; viewer's row unchanged;
//   8. Movies out of what can be rated: viewer's read 404 and Capabilities without Movie at once (no restart); back: 200;
//   9. a restart setting's dry run says a restart is needed, a setting without one says not.
// The settings and the configuration are put back in a finally (the configuration as it was read at the start), and
// every rating the probe made is deleted by its owner.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { PRODUCTION_PORT } from './sandbox-policy.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PROBE = 'ur-settings-probe';
const PLUGIN = 'f53c7c34-8cba-4846-b43b-956816b2f6ae';
const SETTINGS = '/UserRatings/Admin/Settings';
const NOT_A_RATER = 'Rating is limited to selected members.';
const RESTART = ['EnableTopRatedPage', 'PageHostMode', 'RegisterPluginPages'];
// The settings' ranges and names, as Configuration/RatingsSettingsFields.cs has them.
const NUMBERS = { MaxReviewLength: [2000, 20000], MinRatingsForAverage: [1, 10], ReviewsPageSize: [5, 50], SnapshotsToKeep: [0, 365], DetachedRetentionDays: [0, 36500], ModerationLogDays: [0, 3650] };
const NAMES = {
    WhoCanRate: ['AllUsers', 'SelectedUsers'],
    ShowDetailButton: ['Auto', 'Always', 'Never'],
    DeletedUserPolicy: ['Delete', 'Anonymize', 'KeepAsOrphan'],
    PosterBadgeContent: ['Average', 'Mine', 'Both'],
    PosterBadgePosition: ['top-start', 'top-end', 'bottom-start', 'bottom-end'],
    PageHostMode: ['FallbackOnly', 'Auto', 'PluginPagesOnly']
};

function usage() {
    return 'usage: node scripts/userratings-settings-probe.mjs --sandbox <name> [--out <file>]';
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
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(60000) });
    const text = await res.text();
    const type = res.headers.get('content-type') || '';
    let json = null;
    if (/json/i.test(type) && text.length) {
        try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, type, json, text, cache: res.headers.get('cache-control') };
}

const guid = (value) => String(value).replace(/-/g, '').toLowerCase();
// A value in one form: ids without hyphens and in lower case, lists sorted.
const canon = (v) => (Array.isArray(v) ? JSON.stringify(v.map((x) => guid(x)).sort()) : JSON.stringify(v));
const problem = (r) => ({ status: r.status, code: r.json && r.json.code, detail: r.json && r.json.detail });
const read = async () => {
    const r = await call('admin', 'GET', SETTINGS);
    if (r.status !== 200) throw new Error(`GET Settings ${r.status}`);
    return r.json;
};
const configuration = async () => {
    const r = await call('admin', 'GET', `/Plugins/${PLUGIN}/Configuration`);
    if (r.status !== 200) throw new Error(`GET the configuration ${r.status}`);
    return r.text;
};
const dryRun = (body, version) => call('admin', 'POST', SETTINGS, { body, query: version ? { version } : {} });
const save = (body, version) => call('admin', 'POST', SETTINGS, { body, query: { version, dryRun: 'false' } });
// What differs between two sets of settings, as the body that changes the first into the second.
function diff(from, to) {
    const change = {};
    for (const k of Object.keys(to)) if (canon(from[k]) !== canon(to[k])) change[k] = to[k];
    return change;
}

// Another valid value for each setting but the Jellyfin copy (the ranges and names above).
function another(settings, viewerId) {
    const want = {};
    for (const [k, v] of Object.entries(settings)) {
        if (k === 'MirrorToUserData') continue;
        if (typeof v === 'boolean') want[k] = !v;
        else if (NUMBERS[k]) want[k] = v < NUMBERS[k][1] ? v + 1 : v - 1;
        else if (NAMES[k]) want[k] = NAMES[k][(NAMES[k].findIndex((n) => n.toLowerCase() === String(v).toLowerCase()) + 1) % NAMES[k].length];
        else if (k === 'RaterUserIds') want[k] = [viewerId];
        else if (k === 'RateableItemTypes') want[k] = v.includes('Trailer') ? v.filter((x) => x !== 'Trailer') : v.concat(['Trailer']);
        else throw new Error(`no other value for ${k}`);
    }
    return want;
}

const started = Date.now();
const context = { configuration: null, made: [] };
const item = (id) => `/UserRatings/Items/${id}`;
async function rate(who, id, stars) {
    const r = await call(who, 'PUT', `${item(id)}/Mine/Rating`, { body: { Stars: stars } });
    if (r.status === 200 && r.json && r.json.Mine && !context.made.some((m) => m.ratingId === r.json.Mine.Id)) context.made.push({ who, ratingId: r.json.Mine.Id });
    return r;
}

try {
    context.configuration = await configuration();
    const before = await read();
    evidence.before = { version: before.Version, settings: before.Settings };
    const viewerId = guid(users.viewer.id);
    const s0 = before.Settings;
    const ready = s0.WhoCanRate === 'AllUsers' && s0.RaterUserIds.length === 0 && s0.RateableItemTypes.includes('Movie') && s0.EnableRatings === true && s0.MirrorToUserData === false;
    if (!check('the settings this probe changes start at their defaults (every user, Movies rateable, ratings on, the Jellyfin copy off)', ready, s0)) throw new Error('not the default settings');

    // ---- 1. who may call
    const anon = [await call('anonymous', 'GET', SETTINGS), await call('anonymous', 'POST', SETTINGS, { body: {} })];
    const member = [await call('viewer', 'GET', SETTINGS), await call('viewer', 'POST', SETTINGS, { body: { ShowHistogram: !s0.ShowHistogram } })];
    const adminRead = await call('admin', 'GET', SETTINGS);
    const adminDry = await dryRun({ ShowHistogram: !s0.ShowHistogram }, before.Version);
    check('who may call: nobody 401, a member 403 (the read and a dry run), the admin 200, its read and dry run no-store',
        anon.every((r) => r.status === 401) && member.every((r) => r.status === 403) && adminRead.status === 200 && adminDry.status === 200
        && adminRead.cache === 'no-store' && adminDry.cache === 'no-store',
        { anonymous: anon.map((r) => r.status), member: member.map((r) => r.status), admin: [adminRead.status, adminDry.status], cache: [adminRead.cache, adminDry.cache] });

    // ---- 2. the read
    const keys = Object.keys(s0);
    check('the read: 32 settings, each with its default; ModerationLogDays 365 by default; MirrorToUserData off by default',
        keys.length === 32 && JSON.stringify(Object.keys(before.Defaults).sort()) === JSON.stringify([...keys].sort()) && before.Defaults.ModerationLogDays === 365 && before.Defaults.MirrorToUserData === false,
        { count: keys.length, defaults: before.Defaults });
    check('the read: a Version of 16 hex digits, the users by id without hyphens (admin, viewer, viewer2 among them), none missing',
        /^[0-9a-f]{16}$/i.test(before.Version) && [users.admin.id, users.viewer.id, users.viewer2.id].every((id) => before.Users.some((u) => u.Id === guid(id) && u.Missing === false)),
        { version: before.Version, users: before.Users.length });
    check('the read: the twelve kinds RateableItemTypes offers, the three settings that need a restart, no problem',
        before.ItemKinds.length === 12 && ['Movie', 'Series', 'Episode', 'Trailer', 'BoxSet'].every((k) => before.ItemKinds.includes(k))
        && JSON.stringify([...before.RestartFields].sort()) === JSON.stringify(RESTART) && before.Problems.length === 0,
        { kinds: before.ItemKinds, restart: before.RestartFields, problems: before.Problems });

    // ---- 3. each setting alone in a dry run
    const want = another(s0, viewerId);
    const alone = [];
    for (const [k, v] of Object.entries(want)) {
        // Only selected users needs one chosen (alone it is refused, below), so it goes with viewer chosen.
        const body = k === 'WhoCanRate' ? { WhoCanRate: v, RaterUserIds: [viewerId] } : { [k]: v };
        const r = await dryRun(body, before.Version);
        const c = (r.json && r.json.Changes) || [];
        alone.push({ field: k, status: r.status, ok: r.status === 200 && r.json.DryRun === true && r.json.Applied === false
            && JSON.stringify(c.map((x) => x.Field).sort()) === JSON.stringify(Object.keys(body).sort()) && c.every((x) => x.Label.length > 0 && x.RestartNeeded === RESTART.includes(x.Field))
            && r.json.RestartNeeded === RESTART.includes(k), changes: c, detail: r.json && r.json.detail });
    }
    const afterAlone = await read();
    check('each setting alone in a dry run: 200, that setting the one change with its label (only selected users with viewer chosen), a restart only for the three; nothing saved',
        alone.length === 31 && alone.every((a) => a.ok) && afterAlone.Version === before.Version, alone.filter((a) => !a.ok));

    // ---- 4. the refusals, each a save from the current version
    const refusals = [];
    const refuse = async (label, body, expect, { version = before.Version, raw } = {}) => {
        const r = raw !== undefined ? await call('admin', 'POST', SETTINGS, { raw, query: { version, dryRun: 'false' } }) : await call('admin', 'POST', SETTINGS, { body, query: version === null ? { dryRun: 'false' } : { version, dryRun: 'false' } });
        const ok = r.status === expect.status && (expect.code === undefined || (r.json && r.json.code === expect.code))
            && (expect.field === undefined || (r.json && typeof r.json.detail === 'string' && r.json.detail.startsWith(expect.field + ' ') && r.json.detail.endsWith(' Nothing was saved.')));
        refusals.push({ label, ok, ...problem(r), errors: r.json && r.json.errors ? Object.keys(r.json.errors) : undefined });
    };
    for (const [k, [min, max]] of Object.entries(NUMBERS)) {
        await refuse(`${k} ${min - 1}`, { [k]: min - 1 }, { status: 400, code: `InvalidSetting:${k}`, field: k });
        await refuse(`${k} ${max + 1}`, { [k]: max + 1 }, { status: 400, code: `InvalidSetting:${k}`, field: k });
    }
    for (const k of Object.keys(NAMES)) await refuse(`${k} Nope`, { [k]: 'Nope' }, { status: 400, code: `InvalidSetting:${k}`, field: k });
    await refuse('only selected users, nobody chosen', { WhoCanRate: 'SelectedUsers' }, { status: 400, code: 'InvalidSetting:RaterUserIds', field: 'RaterUserIds' });
    await refuse('only selected users, a user the server does not have', { WhoCanRate: 'SelectedUsers', RaterUserIds: [crypto.randomUUID()] }, { status: 400, code: 'InvalidSetting:RaterUserIds', field: 'RaterUserIds' });
    await refuse('no kind', { RateableItemTypes: [] }, { status: 400, code: 'InvalidSetting:RateableItemTypes', field: 'RateableItemTypes' });
    await refuse('a kind not offered', { RateableItemTypes: ['Movie', 'Person'] }, { status: 400, code: 'InvalidSetting:RateableItemTypes', field: 'RateableItemTypes' });
    await refuse('the Jellyfin copy turned on', { MirrorToUserData: true }, { status: 400, code: 'InvalidSetting:MirrorToUserData', field: 'MirrorToUserData' });
    await refuse('a key that names no setting', { ShowHistogram: !s0.ShowHistogram, NoSuchSetting: 1 }, { status: 400, code: 'UnknownSetting' });
    await refuse('a save without a version', { ShowHistogram: !s0.ShowHistogram }, { status: 400, code: 'VersionNeeded' }, { version: null });
    await refuse('a malformed version', { ShowHistogram: !s0.ShowHistogram }, { status: 400, code: 'VersionNeeded' }, { version: 'not-a-version' });
    await refuse('a value of the wrong JSON type (MVC\'s 400)', undefined, { status: 400 }, { raw: JSON.stringify({ MaxReviewLength: 'many' }) });
    await refuse('a body over 64 KiB', undefined, { status: 413 }, { raw: JSON.stringify({ RaterUserIds: Array.from({ length: 2000 }, () => crypto.randomUUID()) }) });
    const afterRefusals = await read();
    const configAfterRefusals = await configuration();
    evidence.refusals = refusals;
    check('the refusals: each its status and code, an InvalidSetting naming its setting first and ending "Nothing was saved."',
        refusals.length === 28 && refusals.every((r) => r.ok), refusals.filter((r) => !r.ok));
    check('the refusals saved nothing: the Version as before, the configuration byte for byte as before',
        afterRefusals.Version === before.Version && configAfterRefusals === context.configuration, { version: afterRefusals.Version });

    // ---- 5. every setting but the Jellyfin copy in one save, read again, then every one put back
    const preview = await dryRun(want, before.Version);
    check('all settings at once: the dry run of every setting at once lists 31 changes and says what a restart takes',
        preview.status === 200 && preview.json.Changes.length === 31 && preview.json.RestartNeeded === true
        && preview.json.Consequences.some((c) => c.startsWith('Takes effect after the server restarts: ')), { status: preview.status, consequences: preview.json && preview.json.Consequences });
    const saved = await save(want, before.Version);
    const held = await read();
    const config = JSON.parse(await configuration());
    const differs = Object.keys(want).filter((k) => canon(held.Settings[k]) !== canon(want[k]));
    const configDiffers = Object.keys(want).filter((k) => canon(config[k]) !== canon(want[k]));
    check('all settings at once: saved (200, Applied); GET Settings and PluginConfiguration hold every value; the Jellyfin copy as it was; a new Version',
        saved.status === 200 && saved.json.Applied === true && saved.json.Version === held.Version && held.Version !== before.Version
        && differs.length === 0 && configDiffers.length === 0 && held.Settings.MirrorToUserData === false,
        { status: saved.status, detail: saved.json && saved.json.detail, differs, configDiffers });
    const back = await save(diff(held.Settings, s0), held.Version);
    const afterBack = await read();
    check('all settings at once: every setting put back in one save: GET Settings as before, the Version as before (it is the settings\' hash)',
        back.status === 200 && Object.keys(diff(afterBack.Settings, s0)).length === 0 && afterBack.Version === before.Version,
        { status: back.status, detail: back.json && back.json.detail, left: diff(afterBack.Settings, s0) });

    // ---- 6. two saves at once from one version
    const both = await Promise.all([save({ ShowHistogram: !s0.ShowHistogram }, before.Version), save({ ShowReviewerAvatars: !s0.ShowReviewerAvatars }, before.Version)]);
    const raced = await read();
    const won = both.findIndex((r) => r.status === 200);
    const lost = both[1 - won];
    const label = won === 0 ? 'Ratings histogram' : 'Reviewers\' pictures';
    const changedNow = Object.keys(diff(s0, raced.Settings));
    check('two saves at once from one version: one saved, the other the 409 SettingsChanged naming the first\'s setting; only the one saved',
        won >= 0 && lost && lost.status === 409 && lost.json.code === 'SettingsChanged' && lost.json.detail.includes(': ' + label + '.') && changedNow.length === 1,
        { statuses: both.map((r) => r.status), lost: lost && problem(lost), changedNow });
    await save(diff(raced.Settings, s0), raced.Version);

    // ---- 7. who may rate, at the API
    const found = await call('admin', 'GET', '/Items', { query: { userId: users.admin.id, IncludeItemTypes: 'Movie', Recursive: 'true', SearchTerm: 'Long Test Movie', Limit: 1 } });
    const movie = found.json && found.json.Items && found.json.Items[0];
    if (!movie) throw new Error('no Long Test Movie in the sandbox');
    const mineBefore = [await call('viewer', 'GET', item(movie.Id)), await call('viewer2', 'GET', item(movie.Id))];
    if (!check('Long Test Movie has no rating of viewer\'s or viewer2\'s', mineBefore.every((r) => r.status === 200 && !r.json.Mine), mineBefore.map((r) => r.status))) throw new Error('the movie already has their ratings');
    const vRated = await rate('viewer', movie.Id, 3);
    const v2Rated = await rate('viewer2', movie.Id, 4);
    let now = await read();
    const only = await save({ WhoCanRate: 'SelectedUsers', RaterUserIds: [viewerId] }, now.Version);
    const v2Read = await call('viewer2', 'GET', item(movie.Id));
    const v2Put = await call('viewer2', 'PUT', `${item(movie.Id)}/Mine/Rating`, { body: { Stars: 2 } });
    const vRead = await call('viewer', 'GET', item(movie.Id));
    check('2b: only viewer chosen (saved at once): viewer2 reads NotARater, their PUT 403 with its sentence, both ratings still counted, viewer can rate',
        vRated.status === 200 && v2Rated.status === 200 && only.status === 200 && v2Read.status === 200 && v2Read.json.Can.Rate === false && v2Read.json.Can.Reason === 'NotARater'
        && v2Read.json.Summary.Count === 2 && v2Put.status === 403 && v2Put.json.detail === NOT_A_RATER && vRead.json.Can.Rate === true,
        { save: problem(only), read: v2Read.json && v2Read.json.Can, count: v2Read.json && v2Read.json.Summary.Count, put: problem(v2Put) });
    const v2Delete = await call('viewer2', 'DELETE', `${item(movie.Id)}/Mine`);
    now = await read();
    const all = await save({ WhoCanRate: 'AllUsers', RaterUserIds: [] }, now.Version);
    const v2Again = await rate('viewer2', movie.Id, 2.5);
    const vAfter = await call('viewer', 'GET', item(movie.Id));
    check('2b: viewer2 deletes their own older row (204); every user again: viewer2 rates (200); viewer\'s row unchanged',
        v2Delete.status === 204 && all.status === 200 && v2Again.status === 200 && vAfter.json.Mine.Stars === 3 && vAfter.json.Mine.UpdatedUtc === vRated.json.Mine.UpdatedUtc,
        { delete: v2Delete.status, save: problem(all), again: v2Again.status, viewer: vAfter.json && vAfter.json.Mine });

    // ---- 8. Movies out of what can be rated, at once
    now = await read();
    const noMovies = await save({ RateableItemTypes: s0.RateableItemTypes.filter((k) => k !== 'Movie') }, now.Version);
    const outRead = await call('viewer', 'GET', item(movie.Id));
    const outCaps = await call('viewer', 'GET', '/UserRatings/Capabilities');
    now = await read();
    const moviesBack = await save({ RateableItemTypes: s0.RateableItemTypes }, now.Version);
    const backRead = await call('viewer', 'GET', item(movie.Id));
    const backCaps = await call('viewer', 'GET', '/UserRatings/Capabilities');
    check('Movies out of what can be rated: viewer\'s read 404 and Capabilities without Movie at once; back: 200 with both ratings, Movie in Capabilities',
        noMovies.status === 200 && outRead.status === 404 && !outCaps.json.RateableItemTypes.includes('Movie') && moviesBack.status === 200 && backRead.status === 200
        && backRead.json.Summary.Count === 2 && backCaps.json.RateableItemTypes.includes('Movie'),
        { save: [noMovies.status, moviesBack.status], read: [outRead.status, backRead.status] });

    // ---- 9. restart or not
    now = await read();
    const restart = await dryRun({ EnableTopRatedPage: !now.Settings.EnableTopRatedPage }, now.Version);
    const noRestart = await dryRun({ ShowHistogram: !now.Settings.ShowHistogram }, now.Version);
    check('the Top Rated page\'s dry run says a restart is needed, in its words; the histogram\'s says not',
        restart.json.RestartNeeded === true && restart.json.Changes[0].RestartNeeded === true && restart.json.Consequences.includes('Takes effect after the server restarts: Top Rated page.')
        && noRestart.json.RestartNeeded === false && !noRestart.json.Consequences.some((c) => c.startsWith('Takes effect after the server restarts')),
        { restart: restart.json && restart.json.Consequences, noRestart: noRestart.json && noRestart.json.Consequences });

    const end = await read();
    check('the settings as they began, through the probe saves themselves (before the finally puts the configuration back)',
        Object.keys(diff(end.Settings, s0)).length === 0 && end.Version === before.Version, diff(end.Settings, s0));
} catch (e) {
    check('probe ran', false, String(e && e.stack ? e.stack.split('\n').slice(0, 4).join(' | ') : e));
} finally {
    for (const left of context.made) await call(left.who, 'DELETE', `/UserRatings/Mine/${left.ratingId}`).catch(() => null);
    if (context.configuration) await call('admin', 'POST', `/Plugins/${PLUGIN}/Configuration`, { raw: context.configuration }).catch(() => null);
}

if (context.configuration) {
    const end = await configuration().catch(() => null);
    check('the end: the configuration byte for byte as it was read at the start; every rating the probe made deleted',
        end === context.configuration, { made: context.made.length });
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
