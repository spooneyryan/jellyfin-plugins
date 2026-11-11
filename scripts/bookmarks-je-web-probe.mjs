#!/usr/bin/env node
// Bookmarks' Jellyfin Enhanced checks in the running je sandbox: what the API-driven walks (je-switchoff-probe.mjs, bookmarks-je-import-probe.mjs) can't show, done the way an
// admin and a user do it, in the web client, next to JE 12.11 or 12.12 with the pinned fixture.
// Node 22 built-ins only. Loopback only, never port 8096. Tokens come from credentials.json and are never printed.
//
//   node scripts/bookmarks-je-web-probe.mjs --sandbox je [--out result.json]
//
// Checks, in order:
//   0. the fixture (JE's bookmarks on with B, its Plugin Pages link and Custom Tab);
//   1. our name dialog next to JE's keys (webcheck je-name-dialog.json, viewer): "Bank job 2" typed into our dialog, Tab to
//      Save, S R O 2 there: nothing of JE fires; Enter saves; 2 outside the dialog is JE's jump (the positive control);
//   2. coexistence (je-coexist.json, admin): our "My Bookmarks" and JE's "Bookmarks" once each in the drawer, no label or
//      route twice; JE's Bookmarks page opened and what it offers recorded (its drawer link clicked once the drawer has
//      settled; clicked once more, then its href opened, when the address doesn't change: evidence.jeDrawerLink says how);
//      both OSD buttons side by side;
//   3. JE's bookmarks switched off, its settings saved (je-switchoff-save.json): JE's settings page with its four Bookmarks settings unticked and saved removes
//      the Custom Tab JE created, and only that tab;
//   4. JE's bookmarks switched off, the player (je-switchoff-osd.json): at the next page load JE's OSD bookmark button is gone; B still JE's (no modal,
//      nothing of ours), Shift+B ours;
//   5. JE's bookmarks switched off, the restarts: restarts until JE's Plugin Pages link is gone (one or two); the Custom Tab not re-created;
//   6. JE's bookmarks switched off, a tab deleted by hand: with the tab put back as JE's and JE's bookmarks off through its API, the tab deleted by hand on
//      Custom Tabs' page (je-switchoff-ct-remove.json) stays gone across a restart and another save of JE's page;
//   7. the last checklist step the way an admin does it, on JE's Keyboard tab: on 12.11 (je-switchoff-disabled.json) "Disabled" typed
//      for Bookmark Current Time and saved: Key "Disabled"; on 12.12, whose tab is a grid of keys that takes no text
//      (je-switchoff-grid-key.json), its key clicked, Shift+X pressed and saved: Key "Shift+X"; our checklist complete, Use B
//      offered; it survives a restart;
//   8. Use B works (je-use-b.json): the settings page's button makes our shortcut B; B bookmarks in the player;
//   9. the Hidden Content check: JE's Hidden Content on and a movie viewer bookmarked hidden by viewer through JE's API: our JSON
//      unchanged, and what JE's script hides on My Bookmarks (je-hidden-content.json) recorded and judged.
// In a finally it puts JE's, Custom Tabs' and Bookmarks' settings, viewer's naming preferences and viewer's JE hidden
// content back, deletes the bookmarks it made, restarts (twice when Plugin Pages needs it) and runs
// Initialize-Sandbox.ps1 -Name je, which asserts the pinned fixture. It changes nothing outside the sandbox and leaves it
// running. It prints one JSON result (pass, checks, evidence) and exits with 0 when every check passed.
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const JE_ID = 'f69e946a-4b3c-4e9a-8f0a-8d7c1b2c4d9b';
const CUSTOM_TABS_ID = 'fbacd0b6-fd46-4a05-b0a4-2045d6a135b0';
const BOOKMARKS_ID = 'd0ee1b2a-7f71-4654-8ff2-163a81441b03';
const JE_PAGE_ID = 'Jellyfin.Plugin.JellyfinEnhanced.BookmarksPage';
const JE_TAB_HTML = '<div class="sections bookmarks"></div>';
const HIDDEN = 'je-hidden';

function usage() {
    return 'usage: node scripts/bookmarks-je-web-probe.mjs --sandbox <name> [--out <file>]';
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
const workRoot = path.join(sandboxRoot, 'bookmarks-je-web-probe');
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
let state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
if (!(state.Community || []).includes('JellyfinEnhanced')) throw new Error('this sandbox has no Jellyfin Enhanced; use the je sandbox');
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const origin = `http://127.0.0.1:${port}`;
const users = readJson(path.join(sandboxRoot, 'credentials.json')).users || {};
for (const name of ['admin', 'viewer']) {
    if (!users[name] || !users[name].token || !users[name].id) throw new Error(`no ${name} in credentials.json; run Initialize-Sandbox.ps1`);
}
const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
const jeDataFolder = path.join(dataDir, 'plugins', 'configurations', 'Jellyfin.Plugin.JellyfinEnhanced');
const viewerJeFolder = path.join(jeDataFolder, guid(users.viewer.id));
const viewerHidden = path.join(viewerJeFolder, 'hidden-content.json');
for (const p of [jeDataFolder, workRoot]) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), p);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(p)) throw new Error(`refusing to work in ${p}`);
}

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, restarts: [], webchecks: [], surfaces: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

async function call(who, method, p, { body, query } = {}) {
    const url = new URL(baseUrl + p, origin);
    for (const [k, v] of Object.entries(query || {})) if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="bookmarks-je-web-probe", Device="sandbox", DeviceId="bookmarks-je-web-probe-${who}", Version="1.0.0", Token="${users[who].token}"`;
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

async function getPluginConfig(id) {
    const r = await call('admin', 'GET', `/Plugins/${id}/Configuration`);
    if (r.status !== 200 || !r.json) throw new Error(`GET /Plugins/${id}/Configuration: ${r.status}`);
    return r.json;
}

async function setPluginConfig(id, config) {
    const r = await call('admin', 'POST', `/Plugins/${id}/Configuration`, { body: config });
    if (r.status !== 204) throw new Error(`POST /Plugins/${id}/Configuration: ${r.status}`);
}

async function patchPluginConfig(id, patch) {
    await setPluginConfig(id, { ...(await getPluginConfig(id)), ...patch });
}

const isJeTab = (t) => String(t.ContentHtml || '').trim() === JE_TAB_HTML;

// Whether JE's settings page has 12.12's Keyboard tab, a grid of keys each changed by a click and a key press, rather than
// 12.11's list with a Key field: 12.12 and later, and a version that can't be read (the kit's JeSwitchOff.HasShortcutGrid).
function hasShortcutGrid(version) {
    const v = String(version || '').split('.').map(Number);
    if (v.length < 2 || v.some((n) => !Number.isInteger(n))) return true;
    return v[0] > 12 || (v[0] === 12 && v[1] >= 12);
}

// What remains of JE's bookmarks (its settings, Plugin Pages' list, Custom Tabs' tabs) and what our checklist says.
async function surfaces(label) {
    await delay(700); // the kit's JE reader memoises for 500 ms
    const diagnostics = (await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics')).json || {};
    const ours = diagnostics.JellyfinEnhanced || {};
    const pages = (await call('admin', 'GET', '/PluginPages/User')).json || {};
    const pageItems = Array.isArray(pages.Items) ? pages.Items : Array.isArray(pages) ? pages : [];
    const tabs = (await getPluginConfig(CUSTOM_TABS_ID)).Tabs || [];
    const je = await getPluginConfig(JE_ID);
    const record = {
        label,
        je: {
            bookmarksEnabled: je.BookmarksEnabled, usePluginPages: je.BookmarksUsePluginPages, useCustomTabs: je.BookmarksUseCustomTabs,
            autoCreateCustomTab: je.BookmarksAutoCreateCustomTab, customTabJeOwned: je.BookmarksCustomTabJeOwned,
            key: ((je.Shortcuts || []).find((s) => s.Name === 'BookmarkCurrentTime') || {}).Key
        },
        pluginPagesLink: pageItems.some((p) => p.Id === JE_PAGE_ID),
        tabs: tabs.map((t) => t.Title),
        customTab: tabs.some(isJeTab),
        checklist: ((ours.Overlap && ours.Overlap.Checklist) || []).map((i) => [i.Key, i.Done, i.NeedsRestart]),
        complete: ours.Overlap && ours.Overlap.Complete,
        canUseB: ours.Overlap && ours.Overlap.CanUseB,
        useBNote: ours.Overlap && ours.Overlap.UseBNote
    };
    evidence.surfaces.push(record);
    return record;
}

const done = (record, key) => (record.checklist.find((i) => i[0] === key) || [])[1] === true;

// ------------------------------------------------------------------------------------------ sandbox control

let scriptRuns = 0;
function runScript(script, args) {
    const started = Date.now();
    fs.mkdirSync(workRoot, { recursive: true });
    const logFile = path.join(workRoot, `script-output-${++scriptRuns}.log`);
    const log = fs.openSync(logFile, 'w');
    let r;
    try {
        r = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repoRoot, 'scripts', script), ...args], {
            cwd: repoRoot, stdio: ['ignore', log, log], timeout: 900000, windowsHide: true
        });
    } finally {
        fs.closeSync(log);
    }
    if (r.status !== 0) {
        const tail = fs.readFileSync(logFile, 'utf8').trim().split(/\r?\n/).slice(-12).join('\n');
        throw new Error(`${script} ${args.join(' ')} exited ${r.status}${r.error ? ` (${r.error.message})` : ''}:\n${tail}`);
    }
    return Math.round((Date.now() - started) / 100) / 10;
}

// The sandbox comes back on the port it ran on (sandbox.json), not its name's standard one: a je sandbox made on another
// port stays there (the Bookmarks 1.1.2 follow-up).
async function restart(label) {
    const stop = runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox]);
    const start = runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild']);
    state = readJson(path.join(sandboxRoot, 'sandbox.json'));
    if (Number(state.Port) !== port) throw new Error(`the sandbox came back on port ${state.Port}`);
    evidence.restarts.push({ label, stopSeconds: stop, startSeconds: start });
    for (let i = 0; i < 60; i++) {
        const r = await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics').catch(() => null);
        const je = await call('admin', 'GET', '/JellyfinEnhanced/public-config').catch(() => null);
        if (r && r.status === 200 && je && je.status === 200) return;
        await delay(1000);
    }
    throw new Error('the sandbox did not answer after the restart');
}

function webcheck(steps, label) {
    fs.mkdirSync(workRoot, { recursive: true });
    const out = path.join(workRoot, `${label}.json`);
    const w = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', steps, '--out', out], {
        cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'], timeout: 900000, windowsHide: true, encoding: 'utf8'
    });
    let result = null;
    try { result = readJson(out); } catch { result = null; }
    const summary = {
        steps, label, exit: w.status, ok: !!(result && result.ok === true), stepsRun: result && Array.isArray(result.steps) ? result.steps.length : null,
        failed: result && Array.isArray(result.steps) ? result.steps.filter((s) => s.ok === false).map((s) => ({ name: s.name, error: String(s.error || '').slice(0, 400) })) : null,
        leftovers: result && result.leftovers
    };
    evidence.webchecks.push(summary);
    return { summary, result, value: (name) => { const s = result && (result.steps || []).find((x) => x.name === name); return s ? s.value : undefined; } };
}

// ------------------------------------------------------------------------------------------ the run

async function run(original) {
    // 0. The fixture.
    const fixture = await surfaces('0 fixture');
    check('0. the fixture: JE\'s bookmarks on (its four settings) with B, its Plugin Pages link and its Custom Tab (JE-owned)',
        fixture.je.bookmarksEnabled && fixture.je.usePluginPages && fixture.je.useCustomTabs && fixture.je.autoCreateCustomTab && fixture.je.customTabJeOwned
        && fixture.je.key === 'B' && fixture.pluginPagesLink && fixture.customTab, fixture);

    // 1. Our name dialog next to JE's keys.
    const sb1 = webcheck('je-name-dialog.json', '1-name-dialog');
    check('1. our name dialog next to JE\'s keys (viewer, JE on): "Bank job 2" typed key by key lands in the field and fires nothing of JE; Tab to Save, then S, R, O and 2 still nothing; Enter saves the name; 2 outside the dialog is JE\'s jump to 20 % (the positive control)',
        sb1.summary.ok, { ...sb1.summary, typed: sb1.value('what the typing did'), onSave: sb1.value('what they did'), control: sb1.value('JE\'s jump to 20 % fired (so the spies see JE\'s actions)') });
    await call('viewer', 'POST', '/Bookmarks/Preferences', { body: original.viewerPrefs });

    // 2. Coexistence, and what JE's own Bookmarks page offers. The webcheck clicks JE's drawer link once the drawer has
    //    settled (1.1.1: JE's own drawer entry, added a moment later, moved the link onto Sign Out's place), clicks once more
    //    when the address didn't change, and opens the link's href as a last resort, which is recorded here.
    const co = webcheck('je-coexist.json', '2-coexist');
    const jePage = co.value('what JE\'s Bookmarks page offers (recorded: headings, buttons, inputs)');
    const opened = co.value('how JE\'s page was opened') || null;
    evidence.jeBookmarksPage = jePage;
    evidence.jeDrawerLink = opened;
    if (opened && opened.fallback) {
        evidence.notes = [...(evidence.notes || []), `je-coexist.json: two clicks on JE's drawer link left the address as it was, so the link's href (${opened.fallbackHref}) was opened instead (the fallback)`];
    }
    check('2. coexistence (admin): our "My Bookmarks" and JE\'s "Bookmarks" once each in the drawer, no label or route twice; JE\'s Bookmarks page opens in Plugin Pages\' host (what it offers recorded); our three OSD buttons next to JE\'s',
        co.summary.ok && !!jePage, { ...co.summary, links: co.value('every link of the drawer'), opened, osd: co.value('their tooltips') });

    // 3. JE's own settings page saved with its Bookmarks off.
    const saved = webcheck('je-switchoff-save.json', '3-je-page-saved');
    await delay(1500);
    const afterSave = await surfaces('3 JE\'s settings page saved with Bookmarks off, no restart');
    const otherTabs = (original.tabs.Tabs || []).filter((t) => !isJeTab(t)).map((t) => t.Title);
    check('3. JE\'s bookmarks switched off: saving JE\'s settings page with its four Bookmarks settings off removes the Custom Tab JE created (and only it), and records the tab as no longer JE\'s; the link and B stay until the restart and the key step',
        saved.summary.ok && !afterSave.je.bookmarksEnabled && !afterSave.je.usePluginPages && !afterSave.je.useCustomTabs && !afterSave.je.autoCreateCustomTab
        && afterSave.customTab === false && JSON.stringify(afterSave.tabs) === JSON.stringify(otherTabs) && afterSave.je.customTabJeOwned === false
        && afterSave.pluginPagesLink === true && afterSave.je.key === 'B',
        { webcheck: saved.summary, save: saved.value('what the save left: JE\'s settings and Custom Tabs\' tabs'), afterSave });
    check('3. our checklist at once: JE\'s four settings and the Custom Tab ticked (no hand deletion needed), the Plugin Pages link and the shortcut pending',
        ['BookmarksEnabled', 'BookmarksUsePluginPages', 'BookmarksUseCustomTabs', 'BookmarksAutoCreateCustomTab', 'CustomTab'].every((k) => done(afterSave, k))
        && !done(afterSave, 'PluginPagesLink') && !done(afterSave, 'ShortcutAdmin'), afterSave.checklist);

    // 4. JE's OSD button at the next page load.
    const osd = webcheck('je-switchoff-osd.json', '4-osd');
    check('4. JE\'s bookmarks switched off, the player: at the next page load the player has no JE bookmark button (ours there); JE\'s B, still set, opens no modal and makes nothing of ours; Shift+B makes ours',
        osd.summary.ok, { ...osd.summary, osd: osd.value('the OSD'), afterB: osd.value('after B') });

    // 5. The Plugin Pages link after one restart or two; the tab not re-created.
    await restart('5 after JE\'s page was saved');
    let afterRestart = await surfaces('5 restart 1');
    let restarts = 1;
    if (afterRestart.pluginPagesLink) {
        await restart('5 second restart (the link survived one)');
        afterRestart = await surfaces('5 restart 2');
        restarts = 2;
    }
    evidence.linkGoneAfterRestarts = afterRestart.pluginPagesLink ? null : restarts;
    check(`5. JE's bookmarks switched off, the restarts: JE's Plugin Pages link is gone after ${restarts} restart${restarts === 1 ? '' : 's'} and the item ticks; the Custom Tab isn't re-created at the start; JE's settings stayed off`,
        afterRestart.pluginPagesLink === false && done(afterRestart, 'PluginPagesLink') && afterRestart.customTab === false
        && !afterRestart.je.bookmarksEnabled && !afterRestart.je.useCustomTabs, { restarts, afterRestart });

    // 6. A tab deleted by hand while JE's bookmarks are off is never re-created.
    await setPluginConfig(CUSTOM_TABS_ID, original.tabs);
    await patchPluginConfig(JE_ID, { BookmarksCustomTabJeOwned: true });
    const tabBack = await surfaces('6 the tab put back as JE\'s, JE\'s bookmarks still off');
    const removed = webcheck('je-switchoff-ct-remove.json', '6-tab-removed-by-hand');
    const afterHand = await surfaces('6 the tab deleted on Custom Tabs\' page');
    await restart('6 after the tab was deleted by hand');
    const afterHandRestart = await surfaces('6 restart');
    const resaved = webcheck('je-switchoff-save.json', '6-je-page-saved-again');
    await delay(1500);
    const afterResave = await surfaces('6 JE\'s settings page saved again');
    check('6. JE\'s bookmarks switched off: the tab deleted by hand on Custom Tabs\' page (JE\'s bookmarks off) stays gone across a restart and another save of JE\'s settings page',
        tabBack.customTab === true && removed.summary.ok && afterHand.customTab === false && afterHandRestart.customTab === false && resaved.summary.ok && afterResave.customTab === false
        && JSON.stringify(afterResave.tabs) === JSON.stringify(otherTabs) && done(afterResave, 'CustomTab'),
        { removed: removed.summary, resaved: resaved.summary, tabs: [tabBack.tabs, afterHand.tabs, afterHandRestart.tabs, afterResave.tabs] });

    // 7. The last step as an admin does it, on JE's own Keyboard tab: 12.11's Key field takes the word Disabled; 12.12's grid of
    //    keys takes no text, so there the key is given another combination (the kit's checklist words it for the version).
    const lastStep = original.grid
        ? { steps: 'je-switchoff-grid-key.json', label: '7-grid-key-recorded', key: 'Shift+X', editor: 'the grid: each row\'s key and label, Bookmark Current Time\'s among them',
            how: 'Shift+X recorded for Bookmark Current Time on JE 12.12\'s Keyboard tab (its key clicked, the combination pressed; not Delete or the block button)' }
        : { steps: 'je-switchoff-disabled.json', label: '7-disabled-typed', key: 'Disabled', editor: 'where Bookmark Current Time is: an override row, or the list of shortcuts left at their default',
            how: '"Disabled" typed for Bookmark Current Time on JE 12.11\'s Keyboard tab (chosen in its list, Add, not JE\'s Disable button)' };
    const keyed = webcheck(lastStep.steps, lastStep.label);
    const afterKey = await surfaces(`7 Bookmark Current Time = ${lastStep.key} on JE's page`);
    check(`7. ${lastStep.how} and saved: JE's key is "${lastStep.key}", every other shortcut as before; our checklist complete, Use B offered without a note`,
        keyed.summary.ok && afterKey.je.key === lastStep.key && done(afterKey, 'ShortcutAdmin') && afterKey.complete === true && afterKey.canUseB === true && afterKey.useBNote === null,
        { webcheck: keyed.summary, editor: keyed.value(lastStep.editor), afterKey });
    await restart(`7 after ${lastStep.key}`);
    const keyKept = await surfaces('7 restart');
    check(`7. "${lastStep.key}" survives the restart: the checklist stays complete`, keyKept.je.key === lastStep.key && keyKept.complete === true && keyKept.canUseB === true, keyKept);

    // 8. Use B works.
    const useB = webcheck('je-use-b.json', '8-use-b');
    const bookmarksAfter = await getPluginConfig(BOOKMARKS_ID);
    check('8. Use B for bookmarks (the settings page\'s button) sets our shortcut to B; in the player B makes our bookmark (no JE modal) and Shift+B no longer does',
        useB.summary.ok && bookmarksAfter.BookmarkNowShortcut === 'B', { ...useB.summary, shortcut: bookmarksAfter.BookmarkNowShortcut });
    await patchPluginConfig(BOOKMARKS_ID, { BookmarkNowShortcut: original.bookmarks.BookmarkNowShortcut });

    // 9. The Hidden Content check: JE's Hidden Content and our page.
    const movies = (await call('viewer', 'GET', '/Items', { query: { userId: users.viewer.id, Recursive: 'true', IncludeItemTypes: 'Movie', Fields: 'Path', EnableImages: 'false' } })).json.Items;
    const pick = (folder) => movies.find((m) => typeof m.Path === 'string' && m.Path.split(/[\\/]/).includes(folder));
    const hiddenMovie = pick('Tagged Movie (2012) [tmdbid-900001]');
    const shownMovie = pick('Long Test Movie (2021)');
    if (!hiddenMovie || !shownMovie) throw new Error('the Hidden Content check needs Tagged Movie (2012) and Long Test Movie (2021) in viewer\'s libraries');
    const made = [];
    for (const [movie, name] of [[hiddenMovie, `${HIDDEN} on the hidden movie`], [shownMovie, `${HIDDEN} on a visible movie`]]) {
        const r = await call('viewer', 'POST', '/Bookmarks', { body: { ItemId: movie.Id, StartTicks: 30_000_000, Name: name } });
        if (r.status !== 201) throw new Error(`POST /Bookmarks as viewer: ${r.status}`);
        made.push(r.json.Id);
        original.viewerBookmarks.push(r.json.Id);
    }
    const queryOf = async () => (await call('viewer', 'GET', '/Bookmarks/Query', { query: { searchTerm: HIDDEN, limit: 50 } })).text;
    const groupsOf = async () => (await call('viewer', 'GET', '/Bookmarks/Groups', { query: { searchTerm: HIDDEN, limit: 50 } })).text;
    const strip = (text) => text.replace(/"ItemAvailable":(true|false)/g, '');
    const before = { query: await queryOf(), groups: await groupsOf() };
    await patchPluginConfig(JE_ID, { HiddenContentEnabled: true });
    const hiddenItem = {
        itemId: guid(hiddenMovie.Id), name: hiddenMovie.Name, type: 'Movie', tmdbId: '', hiddenAt: new Date().toISOString(), posterPath: '',
        seriesId: '', seriesName: '', seasonNumber: null, episodeNumber: null, hideScope: 'global'
    };
    const hcSettings = {
        enabled: true, filterLibrary: true, filterDiscovery: true, filterUpcoming: true, filterCalendar: true, filterSearch: true,
        filterRecommendations: true, filterRequests: true, filterNextUp: true, filterContinueWatching: true,
        showHideButtons: true, showHideConfirmation: true, showButtonJellyseerr: true, showButtonLibrary: false,
        showButtonDetails: true, showButtonCast: false, experimentalHideCollections: false
    };
    const hc = await call('viewer', 'POST', `/JellyfinEnhanced/user-settings/${guid(users.viewer.id)}/hidden-content.json`, { body: { items: { [guid(hiddenMovie.Id)]: hiddenItem }, settings: hcSettings } });
    const readBack = await call('viewer', 'GET', `/JellyfinEnhanced/user-settings/${guid(users.viewer.id)}/hidden-content.json`);
    const coreItems = (await call('viewer', 'GET', '/Items', { query: { userId: users.viewer.id, Recursive: 'true', IncludeItemTypes: 'Movie', EnableImages: 'false' } })).json.Items;
    const after = { query: await queryOf(), groups: await groupsOf() };
    check('9. Hidden Content: with Hidden Content on and the movie hidden by viewer (JE\'s own API), Jellyfin\'s own /Items hides it from viewer, while our Query and Groups JSON for viewer is unchanged',
        hc.status >= 200 && hc.status < 300 && readBack.status === 200 && JSON.stringify(readBack.json || {}).includes(guid(hiddenMovie.Id))
        && !coreItems.some((m) => guid(m.Id) === guid(hiddenMovie.Id)) && strip(before.query) === strip(after.query) && strip(before.groups) === strip(after.groups)
        && JSON.parse(after.groups).TotalRecordCount === 2,
        { save: hc.status, readBack: readBack.status, coreHidesIt: !coreItems.some((m) => guid(m.Id) === guid(hiddenMovie.Id)), queryUnchanged: strip(before.query) === strip(after.query), groupsUnchanged: strip(before.groups) === strip(after.groups) });
    const hiddenPage = webcheck('je-hidden-content.json', '9-hidden-content');
    const grouped = hiddenPage.value('what is shown: each group and its rows') || { groups: [] };
    const flat = hiddenPage.value('what is shown: each row') || [];
    const hiddenGroup = grouped.groups.find((g) => g.header && guid(g.header.dataId) === guid(hiddenMovie.Id));
    const shownGroup = grouped.groups.find((g) => g.header && guid(g.header.dataId) === guid(shownMovie.Id));
    evidence.s10 = { hiddenMovie: hiddenMovie.Name, grouped, flat };
    check('9. Hidden Content on My Bookmarks (viewer, grouped): JE marks the hidden movie\'s group header (it carries the movie id, as JE\'s list-item rule expects) and the whole group goes with it, no rows left under no title; the visible movie\'s group is shown with its row; the flat view\'s rows (each by its bookmark\'s id) are all shown',
        hiddenPage.summary.ok && !!hiddenGroup && !!shownGroup && hiddenGroup.header.jeHidden === true && hiddenGroup.shown === false
        && shownGroup.shown && shownGroup.header.shown && shownGroup.rows.every((r) => r.shown)
        && flat.length === 2 && flat.every((r) => r.shown),
        { webcheck: hiddenPage.summary, hiddenGroup, shownGroup, flat });
    evidence.s10.hiddenGroupOutcome = hiddenGroup ? (hiddenGroup.shown === false ? 'the whole group hidden' : hiddenGroup.header.jeHidden || !hiddenGroup.header.shown ? (hiddenGroup.rows.some((r) => r.shown) ? 'header hidden by JE, rows still shown' : 'header and rows hidden') : 'shown') : 'missing';
}

async function main() {
    let failure = null;
    const original = { je: null, tabs: null, bookmarks: null, viewerPrefs: null, viewerBookmarks: [], hiddenFile: undefined, grid: true };
    try {
        original.je = await getPluginConfig(JE_ID);
        original.tabs = await getPluginConfig(CUSTOM_TABS_ID);
        original.bookmarks = await getPluginConfig(BOOKMARKS_ID);
        original.viewerPrefs = (await call('viewer', 'GET', '/Bookmarks/Preferences')).json;
        original.hiddenFile = fs.existsSync(viewerHidden) ? fs.readFileSync(viewerHidden) : null;
        const jePlugins = ((await call('admin', 'GET', '/Plugins')).json || []).filter((p) => guid(p.Id) === guid(JE_ID));
        evidence.jeVersion = jePlugins.map((p) => `${p.Version} ${p.Status}`);
        original.grid = hasShortcutGrid((jePlugins.find((p) => p.Status === 'Active') || jePlugins[0] || {}).Version);
        evidence.jeKeyboardTab = original.grid ? '12.12 grid of keys' : '12.11 list with a Key field';
        await run(original);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        try {
            for (const id of original.viewerBookmarks) await call('viewer', 'DELETE', `/Bookmarks/${id}`);
            if (original.viewerPrefs) await call('viewer', 'POST', '/Bookmarks/Preferences', { body: original.viewerPrefs });
            if (original.hiddenFile === null && fs.existsSync(viewerHidden)) fs.rmSync(viewerHidden);
            else if (original.hiddenFile) fs.writeFileSync(viewerHidden, original.hiddenFile);
            if (original.je) await setPluginConfig(JE_ID, original.je);
            if (original.tabs) await setPluginConfig(CUSTOM_TABS_ID, original.tabs);
            if (original.bookmarks) await setPluginConfig(BOOKMARKS_ID, original.bookmarks);
            await restart('clean-up 1');
            let back = await surfaces('clean-up restart 1');
            if (!back.pluginPagesLink) {
                await restart('clean-up 2 (the link needs Plugin Pages to read the entry JE re-added)');
                back = await surfaces('clean-up restart 2');
            }
            runScript('Initialize-Sandbox.ps1', ['-Name', opts.sandbox]);
            check('clean-up: the fixture is back (Initialize-Sandbox.ps1 asserts it); viewer\'s preferences and JE hidden content as they were',
                back.pluginPagesLink && back.customTab && back.je.key === 'B' && back.je.bookmarksEnabled === true, back);
        } catch (error) {
            check('clean-up: the fixture restored', false, String(error && error.stack || error));
        }
        for (const f of fs.existsSync(workRoot) ? fs.readdirSync(workRoot).filter((name) => name.startsWith('script-output-')) : []) {
            try { fs.rmSync(path.join(workRoot, f)); } catch { /* still open in the server it started; left for the next run */ }
        }
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
