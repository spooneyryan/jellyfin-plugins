#!/usr/bin/env node
// The kit's Jellyfin Enhanced switch-off in the running je sandbox: walks the Jellyfin Enhanced
// switch-off checklist step by step through the sandbox's APIs and records, after each step, which JE surfaces remain
// (its settings, Plugin Pages' list, the Custom Tab, its active key, and what Bookmarks' Diagnostics entry
// JellyfinEnhanced ticks), with and without a restart, for bookmarks and for reviews; also the
// key moved off B, a user's own X and an unreadable user file, and Disable Keyboard Shortcuts, with "Use B" offered only
// while B is durably free. Along the way it runs the two kit webchecks: je-shortcuts.json (the kit's shortcuts handler
// on the video page) and je-settings.json (the banner, the reserved-key warning with BookmarkNowShortcut set to B, the
// checklist, "Use B for bookmarks" offered or withheld with its reason). It also checks
// that the checklist's items say what an admin does on JE's own settings page (its Pages tab boxes in the order the page
// lets them be unticked, its Keyboard tab for the key: 12.11's Key field, or 12.12's grid of
// keys), and runs je-pages-order.json (read-only) on JE's Pages tab.
// Node 22 built-ins only. Loopback only, never port 8096. Tokens come from credentials.json and are never printed.
//
//   node scripts/je-switchoff-probe.mjs --sandbox je [--out result.json] [--skip-webchecks]
//
// It changes Jellyfin Enhanced's and Custom Tabs' settings in the sandbox through their configuration APIs, writes one
// synthetic per-user JE shortcuts file for the sandbox's viewer (deleted again), restarts the sandbox through
// Stop-Sandbox.ps1 and dev-server.ps1 (-Background -NoBuild, so deploy a current build first; -Port as sandbox.json has it,
// so a sandbox on another port than its name's standard one comes back on its own), and puts the pinned
// fixture back at the end (Initialize-Sandbox.ps1 -Name je asserts it). It prints one JSON result (pass, checks,
// evidence) and exits with 0 when every check passed; the sandbox is left running.
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const JE_ID = 'f69e946a-4b3c-4e9a-8f0a-8d7c1b2c4d9b';
const CUSTOM_TABS_ID = 'fbacd0b6-fd46-4a05-b0a4-2045d6a135b0';
const JE_PAGE_ID = 'Jellyfin.Plugin.JellyfinEnhanced.BookmarksPage';
const JE_TAB_HTML = '<div class="sections bookmarks"></div>';
const BOOKMARKS_ID = 'd0ee1b2a-7f71-4654-8ff2-163a81441b03';

function usage() {
    return 'usage: node scripts/je-switchoff-probe.mjs --sandbox <name> [--out <file>] [--skip-webchecks]';
}

function parseArgs(argv) {
    const o = { sandbox: null, out: null, webchecks: true };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--skip-webchecks') o.webchecks = false;
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const workRoot = path.join(sandboxRoot, 'je-switchoff-probe');
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
// Jellyfin's plugin configurations live under <datadir>/plugins/configurations; JE's user folders inside its own.
const dataDir = path.resolve(state.DataDir || path.join(sandboxRoot, 'data'));
const jeDataFolder = path.join(dataDir, 'plugins', 'configurations', 'Jellyfin.Plugin.JellyfinEnhanced');
const viewerFolder = path.join(jeDataFolder, guid(users.viewer.id));
const viewerShortcuts = path.join(viewerFolder, 'shortcuts.json');
for (const p of [jeDataFolder, workRoot]) {
    const rel = path.relative(path.join(repoRoot, '.sandbox'), p);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(p)) throw new Error(`refusing to work in ${p}`);
}

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, time: new Date().toISOString() }, steps: [], restarts: [], webchecks: [] };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
    return !!pass;
}

async function call(who, method, p, { body } = {}) {
    const url = new URL(baseUrl + p, origin);
    const h = { Accept: 'application/json' };
    if (who !== 'anonymous') {
        h.Authorization = `MediaBrowser Client="je-switchoff-probe", Device="sandbox", DeviceId="je-switchoff-probe", Version="1.0.0", Token="${users[who].token}"`;
    }
    let payload;
    if (body !== undefined) { payload = JSON.stringify(body); h['Content-Type'] = 'application/json'; }
    const res = await fetch(url, { method, headers: h, body: payload, redirect: 'manual', signal: AbortSignal.timeout(120000) });
    const bytes = Buffer.from(await res.arrayBuffer());
    let json = null;
    if (bytes.length && /json/i.test(res.headers.get('content-type') || '')) {
        try { json = JSON.parse(bytes.toString('utf8')); } catch { json = null; }
    }
    return { status: res.status, json };
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

async function patchJe(patch) {
    const current = await getPluginConfig(JE_ID);
    await setPluginConfig(JE_ID, { ...current, ...patch });
}

async function setJeShortcut(name, key) {
    const current = await getPluginConfig(JE_ID);
    const shortcuts = (current.Shortcuts || []).map((s) => (s.Name === name ? { ...s, Key: key } : s));
    await setPluginConfig(JE_ID, { ...current, Shortcuts: shortcuts });
}

async function patchBookmarks(patch) {
    const current = await getPluginConfig(BOOKMARKS_ID);
    await setPluginConfig(BOOKMARKS_ID, { ...current, ...patch });
}

// ------------------------------------------------------------------------------------------ what remains

async function surfaces(label, { restarted = false } = {}) {
    // Bookmarks' Diagnostics entry (read-only; the kit's reader memoises one snapshot for 500 ms, so a read right after a
    // change waits that out first), JE's own public config (what its script loads at the next page load), Plugin Pages'
    // list (what the drawer shows), Custom Tabs' settings and JE's XML.
    await delay(700);
    const diagnostics = (await call('admin', 'GET', '/Bookmarks/Admin/Diagnostics')).json || {};
    const je = diagnostics.JellyfinEnhanced || {};
    const publicConfig = (await call('admin', 'GET', '/JellyfinEnhanced/public-config')).json || {};
    const pages = (await call('admin', 'GET', '/PluginPages/User')).json || {};
    const pageItems = Array.isArray(pages.Items) ? pages.Items : Array.isArray(pages) ? pages : [];
    const tabs = (await getPluginConfig(CUSTOM_TABS_ID)).Tabs || [];
    const jeConfig = await getPluginConfig(JE_ID);
    const jeShortcut = (jeConfig.Shortcuts || []).filter((s) => s.Name === 'BookmarkCurrentTime').map((s) => s.Key);
    const record = {
        label,
        restarted,
        je: {
            bookmarksEnabled: !!jeConfig.BookmarksEnabled,
            usePluginPages: !!jeConfig.BookmarksUsePluginPages,
            useCustomTabs: !!jeConfig.BookmarksUseCustomTabs,
            autoCreateCustomTab: !!jeConfig.BookmarksAutoCreateCustomTab,
            showUserReviews: !!jeConfig.ShowUserReviews,
            bookmarkCurrentTimeKeys: jeShortcut,
            publicConfig: { BookmarksEnabled: publicConfig.BookmarksEnabled, ShowUserReviews: publicConfig.ShowUserReviews, DisableAllShortcuts: publicConfig.DisableAllShortcuts, BookmarkCurrentTime: ((publicConfig.Shortcuts || []).find((s) => s.Name === 'BookmarkCurrentTime') || {}).Key }
        },
        pluginPagesLink: pageItems.some((p) => p.Id === JE_PAGE_ID),
        customTab: tabs.some((t) => String(t.ContentHtml || '').trim() === JE_TAB_HTML),
        ours: {
            installed: je.Installed,
            version: je.Version,
            effectiveKey: je.Shortcuts && je.Shortcuts.BookmarkCurrentTime,
            banner: je.Overlap && je.Overlap.Banner,
            checklist: ((je.Overlap && je.Overlap.Checklist) || []).map((i) => ({ key: i.Key, done: i.Done, needsRestart: i.NeedsRestart, text: i.Text, detail: i.Detail })),
            complete: je.Overlap && je.Overlap.Complete,
            canUseB: je.Overlap && je.Overlap.CanUseB,
            useBNote: je.Overlap && je.Overlap.UseBNote,
            shortcutWarning: ((je.OurShortcuts || [])[0] || {}).Warning || null,
            reservedHasB: (je.ReservedCombos || []).includes('B'),
            userShortcuts: (je.UserShortcuts || []).map((u) => ({ name: u.Name, key: u.Overrides && u.Overrides.BookmarkCurrentTime }))
        }
    };
    evidence.steps.push(record);
    return record;
}

const item = (record, key) => record.ours.checklist.find((i) => i.key === key) || {};

// Whether JE's settings page has 12.12's Keyboard tab, a grid of keys each changed by a click and a key press, rather than
// 12.11's list with a Key field: 12.12 and later, and a version that can't be read (the kit's JeSwitchOff.HasShortcutGrid).
function hasShortcutGrid(version) {
    const v = String(version || '').split('.').map(Number);
    if (v.length < 2 || v.some((n) => !Number.isInteger(n))) return true;
    return v[0] > 12 || (v[0] === 12 && v[1] >= 12);
}

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
    if (!opts.webchecks) return null;
    fs.mkdirSync(workRoot, { recursive: true });
    const out = path.join(workRoot, `${label}.json`);
    const w = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', steps, '--out', out], {
        cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'], timeout: 900000, windowsHide: true, encoding: 'utf8'
    });
    let result = null;
    try { result = readJson(out); } catch { result = null; }
    const summary = { steps, label, exit: w.status, ok: !!(result && result.ok === true), stepsRun: result && Array.isArray(result.steps) ? result.steps.length : null, failed: result && Array.isArray(result.steps) ? result.steps.filter((s) => s.ok === false).map((s) => s.name) : null, leftovers: result && result.leftovers };
    evidence.webchecks.push(summary);
    check(`webcheck ${steps} (${label})`, summary.ok, summary);
    return result;
}

// ------------------------------------------------------------------------------------------ the walk

async function main() {
    let failure = null;
    const original = { je: null, tabs: null, bookmarks: null };
    try {
        original.je = await getPluginConfig(JE_ID);
        original.tabs = await getPluginConfig(CUSTOM_TABS_ID);
        original.bookmarks = await getPluginConfig(BOOKMARKS_ID);
        evidence.jeVersion = ((await call('admin', 'GET', '/Plugins')).json || []).filter((p) => guid(p.Id) === guid(JE_ID)).map((p) => `${p.Version} ${p.Status}`);

        // 0. The fixture: everything on, B, the link and the tab; our Diagnostics sees it all.
        const start = await surfaces('0 fixture');
        check('0. the fixture is in place (JE Bookmarks on, B, the Plugin Pages link, the Custom Tab)',
            start.je.bookmarksEnabled && start.je.usePluginPages && start.je.useCustomTabs && start.je.autoCreateCustomTab && start.je.bookmarkCurrentTimeKeys.join() === 'B' && start.pluginPagesLink && start.customTab, start);
        check('0. our Diagnostics: JE installed, the banner, every item but the users\' pending, B reserved, Shift+B fine',
            start.ours.installed === true && !!start.ours.banner && start.ours.checklist.length === 8
                && start.ours.checklist.every((i) => i.done === (i.key === 'ShortcutUsers')) && start.ours.canUseB === false && start.ours.reservedHasB && start.ours.shortcutWarning === null, start.ours);

        // 0b. The items' words are JE's own settings page's: its Pages tab's four boxes by their
        // labels, in the order its page lets them be unticked (it hides Add the Custom Tabs entry for me once Use Custom Tabs
        // or Enable Bookmarks Feature is off, and greys out the others once Enable Bookmarks Feature is off; 12.11 and 12.12
        // alike), and the key set on its Keyboard tab (not "Jellyfin Enhanced settings, Shortcuts"): on 12.11, Bookmark Current
        // Time chosen in the list, Disabled typed into Key, Add, Save Settings, never its Disable button or a blank key; on
        // 12.12, whose tab is a grid of keys that takes no text, its key clicked and another combination pressed, Save
        // Settings, never Delete or the block button (the JE 12.12 re-check); the key named by its place, the left end of the
        // Bookmark Current Time row, whose label is at the right end, so no neighbouring row's key is clicked (1.1.1).
        const texts = Object.fromEntries(start.ours.checklist.map((i) => [i.key, i.text]));
        const grid = hasShortcutGrid(start.ours.version);
        evidence.jeKeyboardTab = grid ? '12.12 grid of keys' : '12.11 list with a Key field';
        const adminWords = grid
            ? { text: "Last, the server default: on Jellyfin Enhanced's settings page, Keyboard tab, click the key (B) at the left end of the Bookmark Current Time row, press another key combination, such as Shift+X, then Save Settings; never Delete or the ⊘ button beside the key (Disable for every user), never a blank key (Backspace puts B back)",
                detail: /^Still "B"\. .*Give it another key as above; Delete and the ⊘ button save a blank key, which Jellyfin Enhanced turns back into B at its next start\.$/,
                how: 'click the key (B) at the left end of the Bookmark Current Time row, press another combination, Save Settings, never Delete or the ⊘ button (12.12)' }
            : { text: "Last, the server default: on Jellyfin Enhanced's settings page, Keyboard tab, choose Bookmark Current Time in the list of shortcuts, type Disabled into the Key field and click Add (or type Disabled over the key in its own row under Current Overrides), then Save Settings; never its Disable button, never a blank key",
                detail: /^Still "B"\. .*Type Disabled as its key as above; its Disable button saves a blank key, which Jellyfin Enhanced turns back into B at its next start\.$/,
                how: 'choose Bookmark Current Time, type Disabled into Key, Add, Save Settings, never the Disable button or a blank key (12.11)' };
        check(`0b. the checklist names JE's Pages tab boxes in the order its page lets them be unticked, and sets the key on its Keyboard tab: ${adminWords.how}`,
            JSON.stringify(start.ours.checklist.slice(0, 4).map((i) => i.key)) === JSON.stringify(['BookmarksAutoCreateCustomTab', 'BookmarksUseCustomTabs', 'BookmarksUsePluginPages', 'BookmarksEnabled'])
                && texts.BookmarksAutoCreateCustomTab === "Jellyfin Enhanced's settings page, Pages tab, Bookmarks: Add the Custom Tabs entry for me off, first"
                && texts.BookmarksUseCustomTabs === "Jellyfin Enhanced's settings page, Pages tab, Bookmarks: Use Custom Tabs for Bookmarks off"
                && texts.BookmarksUsePluginPages === "Jellyfin Enhanced's settings page, Pages tab, Bookmarks: Use Plugin Pages for Bookmarks Library off"
                && texts.BookmarksEnabled === "Jellyfin Enhanced's settings page, Pages tab, Bookmarks: Enable Bookmarks Feature off, after the three above (it greys them out), then Save Settings"
                && texts.ShortcutAdmin === adminWords.text
                && adminWords.detail.test(item(start, 'ShortcutAdmin').detail || ''),
            { version: start.ours.version, grid, texts, adminDetail: item(start, 'ShortcutAdmin').detail });

        // 1. The kit's shortcuts handler on the video page next to JE's B (the key's own check).
        webcheck('je-shortcuts.json', 'shortcuts-fixture');

        // 1b. JE 12.11's Pages tab, read-only (never saved): Enable Bookmarks Feature unticked first greys out two boxes and
        // hides the third, all still ticked (its Save would write them on); in the checklist's order all four untick.
        webcheck('je-pages-order.json', 'pages-order');

        // 2. Our shortcut set to B: the reserved-key warning on the settings page; back to Shift+B after.
        await patchBookmarks({ BookmarkNowShortcut: 'B' });
        const reserved = await surfaces('2 BookmarkNowShortcut = B');
        check('2. with BookmarkNowShortcut = B the Diagnostics entry warns that JE takes B',
            typeof reserved.ours.shortcutWarning === 'string' && /^B is taken by Jellyfin Enhanced's Bookmark Current Time/.test(reserved.ours.shortcutWarning), reserved.ours.shortcutWarning);
        webcheck('je-settings.json', 'settings-reserved');
        await patchBookmarks({ BookmarkNowShortcut: original.bookmarks.BookmarkNowShortcut });
        check('2. back to the setting as it was', (await surfaces('2 BookmarkNowShortcut back')).ours.shortcutWarning === null);

        // 3. JE's four Bookmarks toggles off, through its configuration API (JE's own settings page also removes the tab it
        //    owns; the API path leaves it, so the checklist asks for it by hand).
        await patchJe({ BookmarksEnabled: false, BookmarksUsePluginPages: false, BookmarksUseCustomTabs: false, BookmarksAutoCreateCustomTab: false });
        const toggled = await surfaces('3 JE Bookmarks toggles off, no restart');
        check('3. the four toggles tick at once; the banner is gone; the link, the tab and B stay',
            ['BookmarksEnabled', 'BookmarksUsePluginPages', 'BookmarksUseCustomTabs', 'BookmarksAutoCreateCustomTab'].every((k) => item(toggled, k).done)
                && toggled.ours.banner === null && toggled.pluginPagesLink && toggled.customTab && item(toggled, 'ShortcutAdmin').done === false && item(toggled, 'PluginPagesLink').done === false && item(toggled, 'CustomTab').done === false, toggled);
        check('3. JE\'s public config already says Bookmarks off (its OSD button and dialog go at the next page load), while its B is still active',
            toggled.je.publicConfig.BookmarksEnabled === false && toggled.je.publicConfig.BookmarkCurrentTime === 'B' && toggled.ours.reservedHasB, toggled.je.publicConfig);

        // 4. The admin's key cleared the way JE's settings page does it (Key: ""): ticked, but fragile, so "Use B" is
        //    withheld with the note (offered only once B is durably free).
        await setJeShortcut('BookmarkCurrentTime', '');
        const cleared = await surfaces('4 BookmarkCurrentTime cleared (""), no restart');
        check('4. a cleared key ticks the admin item with the restart caveat and frees B now, but Use B is withheld with the note (B comes back at the next start)',
            item(cleared, 'ShortcutAdmin').done === true && item(cleared, 'ShortcutAdmin').needsRestart === true && cleared.ours.reservedHasB === false
                && cleared.ours.canUseB === false && typeof cleared.ours.useBNote === 'string' && /isn't offered yet/.test(cleared.ours.useBNote) && cleared.ours.effectiveKey === '', cleared.ours);
        webcheck('je-settings.json', 'settings-use-b-withheld');

        // 4a. Another key (Shift+X): JE compares whole combinations, so B is free for good and "Use B" is offered without a
        //     note; a user's own B holds it back until that user clears or moves it (named), a user's own X holds nothing,
        //     and a user's file that can't be read holds it back with the reason rather than counting as cleared.
        await setJeShortcut('BookmarkCurrentTime', 'Shift+X');
        const moved = await surfaces('4a BookmarkCurrentTime = Shift+X, no restart');
        check('4a. a key other than B ticks the admin item for good, frees B, and offers Use B without a note',
            item(moved, 'ShortcutAdmin').done === true && item(moved, 'ShortcutAdmin').needsRestart === false && moved.ours.reservedHasB === false
                && moved.ours.canUseB === true && moved.ours.useBNote === null && moved.ours.effectiveKey === 'Shift+X' && moved.ours.complete === false, moved.ours);
        webcheck('je-settings.json', 'settings-use-b-offered');

        fs.mkdirSync(viewerFolder, { recursive: true });
        fs.writeFileSync(viewerShortcuts, JSON.stringify({ Shortcuts: [{ Name: 'BookmarkCurrentTime', Key: 'B', Label: 'Bookmark Current Time', Category: 'Player' }] }));
        await delay(700);
        const userHolds = await surfaces('4a viewer\'s own shortcuts.json sets B');
        check('4a. a user\'s own JE shortcut B keeps B reserved and Use B withheld, naming the user',
            userHolds.ours.reservedHasB === true && userHolds.ours.canUseB === false && userHolds.ours.useBNote === null && item(userHolds, 'ShortcutUsers').done === false
                && /viewer \("B"\)/.test(item(userHolds, 'ShortcutUsers').detail) && userHolds.ours.userShortcuts.some((u) => u.name === 'viewer' && u.key === 'B'), userHolds.ours);
        fs.writeFileSync(viewerShortcuts, JSON.stringify({ Shortcuts: [{ Name: 'BookmarkCurrentTime', Key: 'X' }] }));
        await delay(700);
        const userMoved = await surfaces('4a viewer\'s own shortcuts.json sets X');
        check('4a. a user whose own key is X holds nothing: the users\' item is ticked and Use B offered',
            item(userMoved, 'ShortcutUsers').done === true && userMoved.ours.canUseB === true && userMoved.ours.reservedHasB === false
                && userMoved.ours.userShortcuts.some((u) => u.name === 'viewer' && u.key === 'X'), userMoved.ours);
        fs.writeFileSync(viewerShortcuts, JSON.stringify({ Shortcuts: [{ Name: 'BookmarkCurrentTime', Key: '' }] }));
        await delay(700);
        const userCleared = await surfaces('4a viewer\'s own shortcuts.json clears it');
        check('4a. the user clearing it in their own list ticks the users\' item too', item(userCleared, 'ShortcutUsers').done === true && userCleared.ours.canUseB === true, userCleared.ours);
        fs.writeFileSync(viewerShortcuts, '{ "Shortcuts": [ not json');
        await delay(700);
        const userBroken = await surfaces('4a viewer\'s own shortcuts.json unreadable');
        check('4a. a user\'s file that can\'t be read holds Use B back with the reason, rather than counting as cleared',
            item(userBroken, 'ShortcutUsers').done === false && userBroken.ours.canUseB === false && typeof userBroken.ours.useBNote === 'string'
                && /viewer \(file unreadable: /.test(item(userBroken, 'ShortcutUsers').detail), userBroken.ours);
        fs.rmSync(viewerShortcuts);
        fs.rmSync(viewerFolder, { recursive: true, force: true });

        // 4b. Disable Keyboard Shortcuts with the key back at B: JE registers no key listener while it is on, so both
        //     shortcut items tick, but only while the switch stays on: "Use B" withheld with the note; the policy reserves
        //     only JE's panel key. Then the switch off and the key cleared again, for the restart below.
        await setJeShortcut('BookmarkCurrentTime', 'B');
        await patchJe({ DisableAllShortcuts: true });
        const allOff = await surfaces('4b DisableAllShortcuts on, key B, no restart');
        check('4b. with Disable Keyboard Shortcuts on the shortcut items tick with the caveat, B is not reserved, and Use B is withheld with the note',
            item(allOff, 'ShortcutAdmin').done === true && item(allOff, 'ShortcutAdmin').needsRestart === true && item(allOff, 'ShortcutUsers').done === true
                && allOff.ours.reservedHasB === false && allOff.ours.canUseB === false && /Disable Keyboard Shortcuts/.test(allOff.ours.useBNote || '') && allOff.ours.effectiveKey === 'B', allOff.ours);
        check('4b. JE\'s public config carries the switch (its key listener isn\'t registered at the next page load)', allOff.je.publicConfig.DisableAllShortcuts === true, allOff.je.publicConfig);
        webcheck('je-settings.json', 'settings-use-b-disabled-all');
        await patchJe({ DisableAllShortcuts: false });
        await setJeShortcut('BookmarkCurrentTime', '');
        const clearedAgain = await surfaces('4b DisableAllShortcuts off, key cleared again');
        check('4b. the switch off and the key cleared again: the admin item fragile, B reserved by nobody, Use B withheld',
            item(clearedAgain, 'ShortcutAdmin').done === true && item(clearedAgain, 'ShortcutAdmin').needsRestart === true && clearedAgain.ours.reservedHasB === false
                && clearedAgain.ours.canUseB === false && clearedAgain.je.publicConfig.DisableAllShortcuts === false, clearedAgain.ours);

        // 5. The Custom Tab deleted by hand (Custom Tabs' configuration API).
        await setPluginConfig(CUSTOM_TABS_ID, { ...original.tabs, Tabs: (original.tabs.Tabs || []).filter((t) => String(t.ContentHtml || '').trim() !== JE_TAB_HTML) });
        const tabGone = await surfaces('5 Custom Tab deleted, no restart');
        check('5. the tab item ticks; only the Plugin Pages link remains before the restart',
            item(tabGone, 'CustomTab').done === true && tabGone.customTab === false && tabGone.pluginPagesLink === true && tabGone.ours.complete === false, tabGone);

        // 6. Restart: JE's constructor drops its Plugin Pages entry (a second restart if Plugin Pages was constructed first),
        //    and its start-up normalisation puts B back for the cleared key (12.8 to 12.11; JeShortcuts).
        await restart('6 after the checklist');
        let afterRestart = await surfaces('6 restart 1', { restarted: true });
        if (afterRestart.pluginPagesLink) {
            await restart('6 second restart (the link survived one)');
            afterRestart = await surfaces('6 restart 2', { restarted: true });
        }
        evidence.linkGoneAfterRestarts = evidence.restarts.length;
        check('6. after one or two restarts JE\'s Plugin Pages link is gone and the item ticks', afterRestart.pluginPagesLink === false && item(afterRestart, 'PluginPagesLink').done === true, { restarts: evidence.restarts.length, afterRestart });
        check('6. the cleared key came back as B at the start (JE drops an entry without a key and restores the default), so the admin item is pending again and Use B stays withheld, now without a note',
            afterRestart.je.bookmarkCurrentTimeKeys.join() === 'B' && afterRestart.ours.effectiveKey === 'B' && item(afterRestart, 'ShortcutAdmin').done === false && afterRestart.ours.canUseB === false && afterRestart.ours.useBNote === null && afterRestart.ours.reservedHasB === true, afterRestart);
        check('6. JE\'s Bookmarks toggles stayed off across the restart', !afterRestart.je.bookmarksEnabled && !afterRestart.je.usePluginPages && !afterRestart.je.useCustomTabs && !afterRestart.je.autoCreateCustomTab);

        // 7. The durable form: text no key press produces survives the start.
        await setJeShortcut('BookmarkCurrentTime', 'Disabled');
        const disabled = await surfaces('7 BookmarkCurrentTime = Disabled, no restart');
        check('7. "Disabled" ticks the admin item without the caveat and offers Use B without a note',
            item(disabled, 'ShortcutAdmin').done === true && item(disabled, 'ShortcutAdmin').needsRestart === false && disabled.ours.canUseB === true && disabled.ours.useBNote === null && disabled.ours.complete === true, disabled.ours);
        webcheck('je-settings.json', 'settings-complete');
        await restart('7 after Disabled');
        const disabledKept = await surfaces('7 restart', { restarted: true });
        check('7. "Disabled" survives the restart; the checklist stays complete; B is free; the kit\'s handler still fires for Shift+B and B does nothing now',
            disabledKept.je.bookmarkCurrentTimeKeys.join() === 'Disabled' && disabledKept.ours.complete === true && disabledKept.ours.reservedHasB === false, disabledKept);
        webcheck('je-shortcuts.json', 'shortcuts-je-off');

        // 8. Reviews: ShowUserReviews off takes effect in JE's public config at once, no restart; nothing of
        //    reviews is registered with Plugin Pages or Custom Tabs, so nothing else remains.
        await patchJe({ ShowUserReviews: false });
        const reviewsOff = await surfaces('8 ShowUserReviews off, no restart');
        check('8. reviews: JE\'s public config says off at once (its review panel and chip go at the next page load); no link or tab of reviews exists',
            reviewsOff.je.showUserReviews === false && reviewsOff.je.publicConfig.ShowUserReviews === false, reviewsOff.je);
        await restart('8 after reviews off');
        const reviewsKept = await surfaces('8 restart', { restarted: true });
        check('8. reviews: stays off across the restart', reviewsKept.je.showUserReviews === false && reviewsKept.je.publicConfig.ShowUserReviews === false);
    } catch (error) {
        failure = error;
        check('the run finished', false, String(error && error.stack || error));
    } finally {
        // The pinned fixture back: JE's and Custom Tabs' settings as they were (B included), our setting as it was, then a
        // restart so JE's constructor re-adds its Plugin Pages entry (and a second so Plugin Pages reads it when its
        // constructor ran first), asserted by Initialize-Sandbox.ps1.
        try {
            if (fs.existsSync(viewerShortcuts)) fs.rmSync(viewerShortcuts);
            if (fs.existsSync(viewerFolder)) fs.rmSync(viewerFolder, { recursive: true, force: true });
            if (original.je) await setPluginConfig(JE_ID, original.je);
            if (original.tabs) await setPluginConfig(CUSTOM_TABS_ID, original.tabs);
            if (original.bookmarks) await setPluginConfig(BOOKMARKS_ID, original.bookmarks);
            await restart('clean-up 1');
            let back = await surfaces('clean-up restart 1', { restarted: true });
            if (!back.pluginPagesLink) {
                await restart('clean-up 2 (the link needs Plugin Pages to read the entry JE re-added)');
                back = await surfaces('clean-up restart 2', { restarted: true });
            }
            runScript('Initialize-Sandbox.ps1', ['-Name', opts.sandbox]);
            check('clean-up: the fixture is back (Initialize-Sandbox.ps1 asserts it)', back.pluginPagesLink && back.customTab && back.je.bookmarkCurrentTimeKeys.join() === 'B', back);
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
