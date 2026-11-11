// Bookmarks' settings page, in the kit's fake page built from the real markup of Configuration/configPage.html, with
// its inline script: the tabs (Settings | Data | Diagnostics); every setting on the page loaded and saved back
// unchanged (the settings round trip), the bookmark shortcut checked as it is typed and saved as the server's
// normalised combination (the kit's ShortcutPolicy), the Clip folder help; the Diagnostics tab rendering GET
// /Bookmarks/Admin/Diagnostics as text (every dropped report counted, a full list said; the store's and the users'
// warnings as banners above the tabs; the free-space line under the Clip folder field; the per-user counts); the Data
// tab's Jellyfin Enhanced import (the dry run, the import, the report as text, the switch-off checklist and "Use B"
// only after an applied import), and the export, import, snapshots, maintenance run and the users this server doesn't
// know. Since Bookmarks 1.2 the way out of missing or unreadable data: Restore of a listed snapshot and Start empty,
// each confirmed in the kit's dialog (the real kit/web/kit.js, as the Bookmarks web client registers it beside the
// page) with the focus on Cancel.
// DiagnosticsTests runs it:
//   node --test plugins/Bookmarks/tests/Jellyfin.Plugin.Bookmarks.Tests/Web/configpage.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage, loadKit } from '../../../../../kit/web/tests/fake-dom.mjs';

const html = fs.readFileSync(new URL('../../../Jellyfin.Plugin.Bookmarks/Configuration/configPage.html', import.meta.url), 'utf8');
const script = /<script type="text\/javascript">([\s\S]*?)<\/script>/.exec(html)[1];
const formHtml = /<form[^>]*id="BookmarksConfigForm"[^>]*>([\s\S]*?)<\/form>/.exec(html)[1];
const fieldIds = Array.from(formHtml.matchAll(/<(?:input|select)[^>]* id="([A-Za-z]+)"/g), (m) => m[1]);

// ------------------------------------------------------------------ the page's markup in the fake DOM

const VOID = new Set(['input', 'br', 'img', 'meta', 'link', 'hr']);

function decode(text) {
    return text.replace(/&(amp|lt|gt|quot|#39|nbsp);/g, (m, e) => ({ amp: '&', lt: '<', gt: '>', quot: '"', '#39': "'", nbsp: '\u00a0' })[e]);
}

// Builds the page element (#BookmarksConfigPage) from the markup: elements, attributes and text, without the style and
// script elements (the script runs separately) and comments.
function build(page) {
    const start = html.indexOf('<div id="BookmarksConfigPage"');
    const markup = html.substring(start, html.lastIndexOf('</body>'));
    const root = page.element('div');
    const stack = [root];
    const tag = /<!--[\s\S]*?-->|<(style|script)\b[^>]*>[\s\S]*?<\/\1>|<\/([a-zA-Z0-9]+)\s*>|<([a-zA-Z0-9]+)((?:\s+[^\s=>/]+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+))?)*)\s*(\/?)>|([^<]+)/g;
    let m;
    while ((m = tag.exec(markup)) !== null) {
        const parent = stack[stack.length - 1];
        if (m[2]) {
            if (stack.length > 1) {
                stack.pop();
            }
        } else if (m[3]) {
            const name = m[3].toLowerCase();
            const node = page.document.createElement(name);
            for (const a of (m[4] || '').matchAll(/([^\s=>/]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/g)) {
                node.setAttribute(a[1], decode(a[2] ?? a[3] ?? a[4] ?? ''));
            }

            parent.appendChild(node);
            if (!VOID.has(name) && !m[5]) {
                stack.push(node);
            }
        } else if (m[6] && m[6].trim()) {
            parent.appendChild(page.document.createTextNode(decode(m[6].replace(/\s+/g, ' '))));
        }
    }

    return root.firstElementChild;
}

// ------------------------------------------------------------------ server answers

function entry(anchor, i) {
    return {
        Anchor: anchor, View: 'details', Required: true, WebVersion: '10.11.' + i, Client: 'Jellyfin Web', ClientVersion: '10.11.' + i,
        Layout: 'Desktop', Experimental: false, PluginVersion: '1.1.0.0', Count: 1, Reports: 1, Users: 1,
        FirstUtc: '2026-09-29T12:00:00Z', LastUtc: '2026-09-29T12:00:00Z'
    };
}

function diagnostics(entries, reports) {
    return {
        PluginVersion: '1.1.0.0', KitVersion: '1.0.0', Abi: { Target: '10.11', Server: '10.11.5', Active: true },
        Store: { File: 'bookmarks.json', Version: 1, Status: 'Ok' },
        WebAnchors: {
            Reporting: true, Declared: ['detailButtons'], Problems: [],
            Reports: Object.assign({ Accepted: 0, Invalid: 0, RateLimited: 0, TooManyWebClients: 0, Disabled: 0, TooLarge: 0, Overflow: 0 }, reports),
            MaxEntries: 200
        },
        ClientHealth: entries
    };
}

// What GET /Bookmarks/Admin/Shortcuts/Check answers by default: the kit's notation for a few spellings, jellyfin-web's M
// and Jellyfin Enhanced's B taken, anything with a space not a combination.
function defaultCheck(combo) {
    const text = String(combo || '').trim();
    if (/\s/.test(text) || /\+$/.test(text)) {
        return { Name: 'BookmarkNow', Label: 'Bookmark this moment', Combo: null, Valid: false, Reserved: false, Warning: '"' + text + '" isn\'t a key combination; use a key with optional modifiers, such as Shift+B.', Reservations: [] };
    }

    const normal = text.split('+').map((p, i, all) => (i === all.length - 1 && p.length === 1 ? p.toUpperCase() : p.charAt(0).toUpperCase() + p.slice(1).toLowerCase())).join('+');
    const taken = { M: "jellyfin-web's video player", B: "Jellyfin Enhanced's Bookmark Current Time" }[normal];
    return {
        Name: 'BookmarkNow', Label: 'Bookmark this moment', Combo: text === '' ? '' : normal, Valid: true, Reserved: !!taken,
        Warning: taken ? normal + ' is taken by ' + taken + ', so Bookmark this moment won\'t work with it. Choose another key.' : null,
        Reservations: taken ? [{ Combo: normal, Owner: normal === 'M' ? 'JellyfinWeb' : 'JellyfinEnhanced', Description: taken, UserId: null, Default: false }] : []
    };
}

function pathOf(url) {
    return String(url).replace(/^http:\/\/127\.0\.0\.1:8097\//, '').replace(/\?.*$/, '');
}

function queryOf(url) {
    const q = String(url).indexOf('?');
    return new URLSearchParams(q >= 0 ? String(url).substring(q + 1) : '');
}

async function open(body, config = {}, options = {}) {
    const page = createPage();
    const root = build(page);
    page.document.body.append(root);
    const confirms = [];
    page.context.Dashboard = {
        showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {},
        confirm(text, title, callback) {
            confirms.push({ text, title });
            callback(options.confirm !== false);
        }
    };
    const ajaxCalls = [];
    const getCalls = [];
    const handlers = {
        // The Jellyfin Enhanced import: 404 without JE bookmarks.
        'POST Bookmarks/Admin/Import/JellyfinEnhanced': () => Promise.reject({ status: 404, json: () => Promise.resolve({ title: 'Not Found', detail: 'Jellyfin Enhanced has no bookmarks on this server.' }) })
    };
    const json = {
        'Bookmarks/Admin/Snapshots': () => ({ Snapshots: [], KeepSnapshotsBeforeUtc: null, SnapshotsToKeep: 7, StoreReadOnly: false, ClipsBeingMade: 0 }),
        'Bookmarks/Admin/Shortcuts/Check': (url) => defaultCheck(queryOf(url).get('combo'))
    };
    let diagnosticsReads = 0;
    const saved = [];
    let stored = Object.assign({}, config);
    page.context.ApiClient = {
        ajax: (request) => {
            ajaxCalls.push(request);
            const handler = handlers[request.type + ' ' + pathOf(request.url)];
            return handler ? handler(request) : Promise.reject({ status: 404 });
        },
        getUrl: (path) => 'http://127.0.0.1:8097/' + path,
        getJSON: (url) => {
            getCalls.push(String(url));
            if (url.endsWith('/Bookmarks/Admin/Diagnostics')) {
                diagnosticsReads++;
                return Promise.resolve(body);
            }

            const answer = json[pathOf(url)];
            return answer ? Promise.resolve().then(() => answer(url)) : Promise.reject(new Error(url));
        },
        getPluginConfiguration: () => Promise.resolve(JSON.parse(JSON.stringify(stored))),
        updatePluginConfiguration: (id, updated) => {
            saved.push(JSON.parse(JSON.stringify(updated)));
            stored = JSON.parse(JSON.stringify(updated));
            return Promise.resolve({});
        }
    };
    if (options.kit) {
        // The Bookmarks web client's kit, as its ClientScript registers it on every page (window.__jfkit.jfbm.instance).
        const kit = loadKit(page).createKit({ plugin: 'Bookmarks', prefix: 'jfbm', route: 'Bookmarks', version: '1.2.0.0', kit: '1.0.0', mvid: '0123abcd' }, '');
        page.context.__jfkit = { jfbm: { plugin: 'Bookmarks', instance: kit } };
    }

    page.run(script, 'configPage.html');
    page.dispatch(root, 'pageshow');
    await page.flush();
    const q = (selector) => page.document.querySelector(selector);
    const view = {
        page,
        root,
        banners: q('#BookmarksBanners'),
        api: page.context.ApiClient,
        rerender: async () => {
            page.dispatch(root, 'pageshow');
            await page.flush();
        },
        get summary() {
            return q('#BookmarksDiagnosticsSummary').textContent;
        },
        summaryNode: q('#BookmarksDiagnosticsSummary'),
        list: q('#BookmarksClientHealth'),
        storage: q('#BookmarksClipStorage'),
        counts: q('#BookmarksCounts'),
        shortcutCheck: q('#BookmarksShortcutCheck'),
        field: (id) => q('#' + id),
        q,
        submit: async () => {
            page.dispatch(q('#BookmarksConfigForm'), 'submit', { cancelable: true });
            for (let i = 0; i < 4; i++) {
                await page.flush();
            }

            return saved;
        },
        // Types into a field as a user does: the value, then the input event.
        type: async (id, text) => {
            q('#' + id).value = text;
            page.dispatch(q('#' + id), 'input');
            await page.flush();
        },
        je: q('#BookmarksJellyfinEnhanced'),
        checklist: q('#BookmarksJellyfinEnhancedChecklist'),
        useBRow: q('#BookmarksUseBRow'),
        useBButton: q('#BookmarksUseB'),
        useBNote: q('#BookmarksUseBNote'),
        useB: async () => {
            q('#BookmarksUseB').click();
            await page.flush();
            await page.flush();
            return saved;
        },
        setBody: (next) => {
            body = next;
        },
        jeImport: q('#BookmarksJeImport'),
        jeStatus: q('#BookmarksJeImportStatus'),
        jeDryRun: q('#BookmarksJeDryRun'),
        jeApply: q('#BookmarksJeApply'),
        jeReport: q('#BookmarksJeImportReport'),
        jeGate: q('#BookmarksJeChecklistGate'),
        setImport: (answer) => {
            handlers['POST Bookmarks/Admin/Import/JellyfinEnhanced'] = answer;
        },
        handle: (key, answer) => {
            handlers[key] = answer;
        },
        answer: (path, answer) => {
            json[path] = answer;
        },
        click: async (node) => {
            node.click();
            for (let i = 0; i < 4; i++) {
                await page.flush();
            }
        },
        ajaxCalls,
        getCalls,
        confirms,
        diagnosticsReads: () => diagnosticsReads,
        saved,
        stored: () => stored,
        errors: page.errors
    };
    return view;
}

// ------------------------------------------------------------------ the page's layout

test('the page has the tabs Settings, Data and Diagnostics, the banners above them, and Settings shown first', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }));
    const tablist = view.q('#BookmarksTabs');
    const tabs = tablist.querySelectorAll('[role="tab"]');

    assert.equal(tablist.getAttribute('role'), 'tablist');
    assert.deepEqual(tabs.map((t) => t.textContent), ['Settings', 'Data', 'Diagnostics']);
    assert.deepEqual(tabs.map((t) => t.getAttribute('aria-controls')), ['BookmarksConfigForm', 'BookmarksData', 'BookmarksDiagnostics']);
    assert.equal(view.banners.nextElementSibling, tablist, 'the banners come before the tabs');
    assert.deepEqual(tabs.map((t) => t.getAttribute('aria-selected')), ['true', 'false', 'false']);
    assert.deepEqual(['BookmarksConfigForm', 'BookmarksData', 'BookmarksDiagnostics'].map((id) => view.q('#' + id).classList.contains('hide')), [false, true, true]);
    ['BookmarksConfigForm', 'BookmarksData', 'BookmarksDiagnostics'].forEach((id) => assert.equal(view.q('#' + id).getAttribute('role'), 'tabpanel'));

    // A click shows its panel and loads the snapshots of the Data tab.
    await view.click(view.q('#BookmarksTabData'));
    assert.deepEqual(tabs.map((t) => t.getAttribute('aria-selected')), ['false', 'true', 'false']);
    assert.deepEqual(tabs.map((t) => t.getAttribute('tabindex')), ['-1', '0', '-1']);
    assert.deepEqual(['BookmarksConfigForm', 'BookmarksData', 'BookmarksDiagnostics'].map((id) => view.q('#' + id).classList.contains('hide')), [true, false, true]);
    assert.ok(view.getCalls.some((u) => u.endsWith('/Bookmarks/Admin/Snapshots')), 'the Data tab lists the snapshots');

    // The arrow keys move between the tabs (and wrap), Home and End go to the ends, with the focus.
    view.page.key(view.q('#BookmarksTabData'), 'ArrowRight');
    await view.page.flush();
    assert.equal(view.q('#BookmarksTabDiagnostics').getAttribute('aria-selected'), 'true');
    assert.equal(view.page.document.activeElement, view.q('#BookmarksTabDiagnostics'));
    view.page.key(view.q('#BookmarksTabDiagnostics'), 'ArrowRight');
    await view.page.flush();
    assert.equal(view.q('#BookmarksTabSettings').getAttribute('aria-selected'), 'true');
    view.page.key(view.q('#BookmarksTabSettings'), 'End');
    await view.page.flush();
    assert.equal(view.q('#BookmarksTabDiagnostics').getAttribute('aria-selected'), 'true');
    view.page.key(view.q('#BookmarksTabDiagnostics'), 'Home');
    await view.page.flush();
    assert.equal(view.q('#BookmarksTabSettings').getAttribute('aria-selected'), 'true');
    view.page.key(view.q('#BookmarksTabSettings'), 'ArrowLeft');
    await view.page.flush();
    assert.equal(view.q('#BookmarksTabDiagnostics').getAttribute('aria-selected'), 'true');
    assert.deepEqual(view.errors, []);
});

// ------------------------------------------------------------------ the settings round trip

// Every setting on the page with a value that isn't its default, of each kind the page has.
const everySetting = {
    EnableWebUi: false, ReportWebAnchorProblems: false, EnableBookmarksPage: false, EnableClipsPage: false, PageHostMode: 'Auto', RegisterPluginPages: false, ShowFallbackNav: false,
    ShowPlayerButtons: false, BookmarkNowShortcut: 'Ctrl+Alt+K', AskForNameDefault: true, SuggestChapterNames: false, ShowJellyfinEnhancedBanner: false,
    DeletedUserPolicy: 'Delete', DetachedRetentionDays: 120, SnapshotsToKeep: 14,
    EnableClips: false, AdminsCanManageAllClips: false, ClipOutputPath: 'E:\\Jellyfin Clips', MinFreeSpaceMegabytes: 4096, OrganizeClipsByUser: false, DefaultClipMode: 'Fast',
    MaxClipDurationSeconds: 900, MaxConcurrentClips: 2, CleanUpPartialFilesOnStartup: false, MissingClipRetentionDays: 7,
    VideoPreset: 'slow', VideoCrf: 18, MaxVideoHeight: 1080, AudioBitrateKbps: 256, AudioChannels: 6
};

// The settings that are not on the page: the developer demos and clock, which a real server never turns on.
const developerOnly = { EnablePageDemo: false, EnableDemoPageB: true, EnableIdentityDemo: false, EnableUserDataDemo: false, EnableLibraryChangeDemo: false, EnableLibraryDemo: false, DeveloperClockOffsetDays: 0 };

test('every setting on the page loads into its field and is saved back unchanged, and nothing else is touched (the settings round trip)', async () => {
    assert.deepEqual(fieldIds.slice().sort(), Object.keys(everySetting).sort(), 'the form holds exactly these settings');
    const config = Object.assign({}, everySetting, developerOnly);
    const view = await open(diagnostics([], { Accepted: 0 }), config);

    for (const [name, value] of Object.entries(everySetting)) {
        const node = view.field(name);
        if (typeof value === 'boolean') {
            assert.equal(node.checked, value, name);
        } else {
            assert.equal(String(node.value), String(value), name);
        }
    }

    const [saved] = await view.submit();
    assert.deepEqual(saved, config, 'saving without a change writes every setting as it was');
    assert.equal(view.ajaxCalls.length, 0);
    assert.ok(view.getCalls.some((u) => /Shortcuts\/Check\?combo=Ctrl%2BAlt%2BK$/.test(u)), 'the shortcut is checked before saving');
    assert.deepEqual(view.errors, []);
});

test('each setting changed on the page is saved with its new value and type', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }), Object.assign({}, everySetting, developerOnly));
    const changed = {
        EnableWebUi: true, ReportWebAnchorProblems: true, EnableBookmarksPage: true, EnableClipsPage: true, PageHostMode: 'PluginPagesOnly', RegisterPluginPages: true, ShowFallbackNav: true,
        ShowPlayerButtons: true, BookmarkNowShortcut: 'F7', AskForNameDefault: false, SuggestChapterNames: true, ShowJellyfinEnhancedBanner: true,
        DeletedUserPolicy: 'KeepAsOrphan', DetachedRetentionDays: 0, SnapshotsToKeep: 0,
        EnableClips: true, AdminsCanManageAllClips: true, ClipOutputPath: '', MinFreeSpaceMegabytes: 0, OrganizeClipsByUser: true, DefaultClipMode: 'Accurate',
        MaxClipDurationSeconds: 0, MaxConcurrentClips: 1, CleanUpPartialFilesOnStartup: true, MissingClipRetentionDays: 0,
        VideoPreset: 'veryfast', VideoCrf: 20, MaxVideoHeight: 0, AudioBitrateKbps: 192, AudioChannels: 2
    };
    for (const [name, value] of Object.entries(changed)) {
        if (typeof value === 'boolean') {
            view.field(name).checked = value;
        } else {
            view.field(name).value = String(value);
        }
    }

    const [saved] = await view.submit();
    assert.deepEqual(saved, Object.assign({}, changed, developerOnly));
    assert.deepEqual(view.errors, []);
});

test('a select keeps a setting it has no option for, so saving never changes it (an older or hand-edited value)', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }), { PageHostMode: 'auto', DeletedUserPolicy: 'Anonymize', DefaultClipMode: 'Fast', VideoPreset: 'medium', MaxVideoHeight: 900, AudioChannels: 2, BookmarkNowShortcut: 'Shift+B' });
    assert.equal(view.field('PageHostMode').value, 'auto');
    assert.equal(view.field('MaxVideoHeight').value, '900');
    const [saved] = await view.submit();
    assert.equal(saved.PageHostMode, 'auto');
    assert.equal(saved.DeletedUserPolicy, 'Anonymize');
    assert.equal(saved.MaxVideoHeight, 900);
    assert.deepEqual(view.errors, []);
});

test('the page sections and their order: Web client, Pages, Player, Jellyfin Enhanced, Bookmark data, Clips, Accurate clip encoding', () => {
    const titles = Array.from(formHtml.matchAll(/<h2 class="sectionTitle">([^<]+)<\/h2>/g), (m) => m[1]);
    assert.deepEqual(titles, ['Web client', 'Pages', 'Player', 'Jellyfin Enhanced', 'Bookmark data', 'Clips', 'Accurate clip encoding']);
    for (const name of ['EnableBookmarksPage', 'EnableClipsPage', 'RegisterPluginPages', 'ShowFallbackNav', 'ShowPlayerButtons', 'AskForNameDefault', 'SuggestChapterNames', 'ShowJellyfinEnhancedBanner', 'EnableClips', 'AdminsCanManageAllClips', 'OrganizeClipsByUser', 'CleanUpPartialFilesOnStartup']) {
        assert.match(html, new RegExp('<input id="' + name + '" name="' + name + '" type="checkbox" is="emby-checkbox" />'));
    }

    assert.match(html, /<input id="MinFreeSpaceMegabytes" name="MinFreeSpaceMegabytes" type="number" is="emby-input" min="0" step="1" \/>/);
    assert.match(html, /<input id="DetachedRetentionDays" name="DetachedRetentionDays" type="number" is="emby-input" min="0" step="1" \/>/);

    // The missing-clip sweep's retention, in the Clips section after the unfinished files' clean-up; at most 36500 days
    // (the server counts a larger value as that), and the help says which folders are judged.
    assert.match(html, /<input id="MissingClipRetentionDays" name="MissingClipRetentionDays" type="number" is="emby-input" min="0" max="36500" step="1" \/>/);
    const clips = /<h2 class="sectionTitle">Clips<\/h2>([\s\S]*?)<h2 class="sectionTitle">/.exec(html)[1];
    assert.ok(clips.indexOf('id="CleanUpPartialFilesOnStartup"') < clips.indexOf('id="MissingClipRetentionDays"'));
    const help = /for="MissingClipRetentionDays"[\s\S]*?<div class="fieldDescription">([\s\S]*?)<\/div>/.exec(html)[1];
    assert.match(help, /After this many days \(at most 36500\) a deleted bookmark's kept clip moves into the archive \(data\/bookmarks\/archive\), and a bookmark's own clip is forgotten while the bookmark stays; no file is ever deleted\. 0 keeps them for good\./);
    assert.match(help, /Clips are marked only in a clip folder Bookmarks recognises as theirs, by the marker file it keeps in it \(\.jfbm-clip-root\): a folder that isn't there \(an unplugged drive\), that is there without its marker and without all of its clips \(a disk or share that isn't mounted, a folder in its place\), or in which every clip is gone at once is left out, and the Diagnostics tab names it, so nothing in it is marked or archived\. If a folder's clips are gone for good, confirm it there: its clips are then marked missing\./);
    assert.doesNotMatch(help, /never checked/);
    assert.match(html, /<select is="emby-select" id="PageHostMode"[\s\S]*?<option value="FallbackOnly">[\s\S]*?<option value="Auto">[\s\S]*?<option value="PluginPagesOnly">/);
    assert.match(html, /<select is="emby-select" id="DeletedUserPolicy"[\s\S]*?<option value="KeepAsOrphan">[\s\S]*?<option value="Delete">[\s\S]*?<option value="Anonymize">/);
});

test('the Pages section turns My Bookmarks and My Clips on and off, My Clips with what it does to the delete dialogs', () => {
    const pages = /<h2 class="sectionTitle">Pages<\/h2>([\s\S]*?)<h2 class="sectionTitle">/.exec(html)[1];
    const order = ['EnableBookmarksPage', 'EnableClipsPage', 'PageHostMode', 'RegisterPluginPages', 'ShowFallbackNav'].map((id) => pages.indexOf('id="' + id + '"'));
    assert.ok(order.every((at, i) => at > 0 && (i === 0 || at > order[i - 1])), 'the two pages first, then the settings both share');
    const text = pages.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');
    assert.match(text, /Turn on the My Clips page Each user's page of all their clips, those of deleted bookmarks included,/);
    assert.match(text, /While it is on, deleting a bookmark keeps its clip unless "Also delete its clip" is ticked, since My Clips lists kept clips\./);
    assert.match(text, /When off, no link leads to it, it says the server admin turned it off, and deleting a bookmark deletes its clip unless that box is unticked\./);
    assert.match(text, /Where the pages are linked/);
    assert.match(text, /The section above User in the menu with the links to My Bookmarks and My Clips\./);
});

test('the Clip folder help says to keep the default, and never suggests a library folder without its warnings', () => {
    const help = /<label[^>]*for="ClipOutputPath"[\s\S]*?<div id="BookmarksClipStorage"/.exec(html)[0].replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');
    assert.match(help, /Leave it empty for data\/bookmarks\/clips in the server's data folder, outside every library: the right choice unless clips should be browsable in Jellyfin\./);
    assert.match(help, /a library of type Home Videos and Photos for exactly that folder/);
    assert.match(help, /on a drive with plenty of free space/);
    assert.match(help, /Never use a Movies or TV library, or a folder inside any library/);
    assert.match(help, /Everyone who can open the clip library sees every user's clips/);
    assert.doesNotMatch(html, /Point it at a folder inside a library/);
    assert.doesNotMatch(html, /to have clips show up in Jellyfin/);
});

// ------------------------------------------------------------------ the bookmark shortcut

function jeWithShortcut(check) {
    return jellyfinEnhanced({ OurShortcuts: [Object.assign({ Name: 'BookmarkNow', Label: 'Bookmark this moment' }, check)] });
}

test('the saved shortcut\'s check from Diagnostics is under the field: a key that is taken in amber, as the server\'s text, and no banner for it', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.JeImport = jeImport();
    body.JellyfinEnhanced = jeWithShortcut({ Combo: 'B', Valid: true, Reserved: true, Warning: "B is taken by Jellyfin Enhanced's Bookmark Current Time, so Bookmark this moment won't work with it. <i>Choose</i> another key.", Reservations: [] });
    const view = await open(body, { BookmarkNowShortcut: 'B' });

    const warning = view.shortcutCheck.querySelector('.bookmarksShortcutWarning');
    assert.ok(warning, 'the warning is under the field');
    assert.ok(warning.classList.contains('bookmarksFieldWarning'));
    assert.equal(warning.textContent, "B is taken by Jellyfin Enhanced's Bookmark Current Time, so Bookmark this moment won't work with it. <i>Choose</i> another key.");
    assert.equal(warning.children.length, 0, 'server text is text');
    assert.equal(warning.getAttribute('data-shortcut'), 'BookmarkNow');
    assert.equal(view.shortcutCheck.parentNode, view.field('BookmarkNowShortcut').parentNode, 'in the field\'s container');
    assert.equal(view.banners.querySelectorAll('.bookmarksShortcutWarning').length, 0, 'no banner at the top for it any more');

    // A free key says so (and that saving normalises one written otherwise); an empty one says the shortcut is off.
    const free = diagnostics([], { Accepted: 0 });
    free.JellyfinEnhanced = jeWithShortcut({ Combo: 'Shift+B', Valid: true, Reserved: false, Warning: null, Reservations: [] });
    const freeView = await open(free, { BookmarkNowShortcut: 'Shift+B' });
    assert.equal(freeView.shortcutCheck.textContent, 'Shift+B is free: neither the video player nor Jellyfin Enhanced takes it.');
    assert.equal(freeView.shortcutCheck.querySelectorAll('.bookmarksFieldWarning').length, 0);
    const lower = await open(free, { BookmarkNowShortcut: 'shift+b' });
    assert.equal(lower.shortcutCheck.textContent, 'Shift+B is free: neither the video player nor Jellyfin Enhanced takes it. Saving writes it as Shift+B.');
    const off = diagnostics([], { Accepted: 0 });
    off.JellyfinEnhanced = jeWithShortcut({ Combo: '', Valid: true, Reserved: false, Warning: null, Reservations: [] });
    const offView = await open(off, { BookmarkNowShortcut: '' });
    assert.match(offView.shortcutCheck.textContent, /^Off: no key bookmarks the current moment/);
    assert.deepEqual(view.errors.concat(freeView.errors, lower.errors, offView.errors), []);
});

test('typing a shortcut asks the server 300 ms after the last key and shows only the latest answer', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.JeImport = jeImport();
    body.JellyfinEnhanced = jeWithShortcut({ Combo: 'Shift+B', Valid: true, Reserved: false, Warning: null, Reservations: [] });
    const view = await open(body, { BookmarkNowShortcut: 'Shift+B' });
    const before = view.getCalls.filter((u) => /Shortcuts\/Check/.test(u)).length;

    await view.type('BookmarkNowShortcut', 'm');
    await view.page.advance(200);
    await view.type('BookmarkNowShortcut', 'shift+k');
    await view.page.advance(299);
    assert.equal(view.getCalls.filter((u) => /Shortcuts\/Check/.test(u)).length, before, 'nothing asked while typing');
    await view.page.advance(1);
    const asked = view.getCalls.filter((u) => /Shortcuts\/Check/.test(u)).slice(before);
    assert.deepEqual(asked, ['http://127.0.0.1:8097/Bookmarks/Admin/Shortcuts/Check?combo=shift%2Bk']);
    assert.equal(view.shortcutCheck.textContent, 'Shift+K is free: neither the video player nor Jellyfin Enhanced takes it. Saving writes it as Shift+K.');

    // A key jellyfin-web takes, and text that isn't a combination: amber, the server's sentence.
    await view.type('BookmarkNowShortcut', 'm');
    await view.page.advance(300);
    assert.equal(view.shortcutCheck.querySelector('.bookmarksShortcutWarning').textContent, "M is taken by jellyfin-web's video player, so Bookmark this moment won't work with it. Choose another key.");
    await view.type('BookmarkNowShortcut', 'Shift+');
    await view.page.advance(300);
    const invalid = view.shortcutCheck.querySelector('.bookmarksShortcutWarning');
    assert.equal(invalid.getAttribute('data-valid'), 'false');
    assert.equal(invalid.textContent, '"Shift+" isn\'t a key combination; use a key with optional modifiers, such as Shift+B.');

    // An answer that comes after a newer one is dropped.
    let release;
    view.answer('Bookmarks/Admin/Shortcuts/Check', (url) => (queryOf(url).get('combo') === 'slow'
        ? new Promise((resolve) => { release = () => resolve(defaultCheck('Ctrl+S')); })
        : defaultCheck(queryOf(url).get('combo'))));
    await view.type('BookmarkNowShortcut', 'slow');
    await view.page.advance(300);
    await view.type('BookmarkNowShortcut', 'F7');
    await view.page.advance(300);
    release();
    await view.page.flush();
    assert.equal(view.shortcutCheck.textContent, 'F7 is free: neither the video player nor Jellyfin Enhanced takes it.');
    assert.deepEqual(view.errors, []);
});

test('saving writes the shortcut as the server normalised it; a changed shortcut that is not a combination saves nothing', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }), { BookmarkNowShortcut: 'Shift+B', EnableClips: true });

    await view.type('BookmarkNowShortcut', ' shift+j ');
    let saved = await view.submit();
    assert.equal(saved.length, 1);
    assert.equal(saved[0].BookmarkNowShortcut, 'Shift+J');
    assert.equal(view.field('BookmarkNowShortcut').value, 'Shift+J', 'the field shows what was saved');

    // Not a combination: nothing is saved, the reason is under the field and the field has the focus.
    await view.type('BookmarkNowShortcut', 'Shift+');
    view.field('EnableClips').checked = false;
    saved = await view.submit();
    assert.equal(saved.length, 1, 'nothing saved');
    assert.equal(view.shortcutCheck.querySelector('.bookmarksShortcutWarning').getAttribute('data-valid'), 'false');
    assert.equal(view.page.document.activeElement, view.field('BookmarkNowShortcut'));

    // A reserved key is saved, with its warning.
    await view.type('BookmarkNowShortcut', 'b');
    saved = await view.submit();
    assert.equal(saved.length, 2);
    assert.equal(saved[1].BookmarkNowShortcut, 'B');
    assert.equal(saved[1].EnableClips, false);
    assert.ok(view.shortcutCheck.querySelector('.bookmarksShortcutWarning'));

    // Empty turns it off.
    await view.type('BookmarkNowShortcut', '');
    saved = await view.submit();
    assert.equal(saved[2].BookmarkNowShortcut, '');
    assert.deepEqual(view.errors, []);
});

test('an unchanged shortcut that is not a combination never blocks saving the other settings, and without the check the text is saved as typed', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }), { BookmarkNowShortcut: 'Shift + ', EnableClips: true });
    view.field('EnableClips').checked = false;
    let saved = await view.submit();
    assert.equal(saved.length, 1);
    assert.equal(saved[0].BookmarkNowShortcut, 'Shift +', 'kept (trimmed) as it was');
    assert.equal(saved[0].EnableClips, false);

    // An older server without the check endpoint: saved as typed.
    view.answer('Bookmarks/Admin/Shortcuts/Check', () => Promise.reject(new Error('404')));
    await view.type('BookmarkNowShortcut', 'shift+q');
    saved = await view.submit();
    assert.equal(saved[1].BookmarkNowShortcut, 'shift+q');
    assert.equal(view.shortcutCheck.childNodes.length, 0);
    assert.deepEqual(view.errors, []);
});

// ------------------------------------------------------------------ Diagnostics

test('the summary counts every dropped report, those from too many web clients too', async () => {
    const view = await open(diagnostics([entry('detailButtons', 5)], { Accepted: 2, Invalid: 1, RateLimited: 2, TooManyWebClients: 3, TooLarge: 4, Disabled: 5 }));

    assert.match(view.summary, /Browser reports: recorded; 2 accepted, 15 dropped \(invalid, too frequent, too many web clients for one user, too large or not recorded\)\./);
    assert.equal(view.list.querySelectorAll('tr').length, 2);
    assert.equal(view.list.querySelectorAll('p').length, 0, 'a list with room has no note');
    assert.ok(view.list.parentNode.parentNode === view.q('#BookmarksDiagnostics'), 'on the Diagnostics tab');
    assert.deepEqual(view.errors, []);
});

test('a full list says so, with how many older reports made room', async () => {
    const entries = Array.from({ length: 200 }, (_, i) => entry('detailButtons', i));
    const view = await open(diagnostics(entries, { Accepted: 237, Overflow: 37 }));

    const note = view.list.childNodes[0];
    assert.equal(note.tagName, 'P');
    assert.equal(note.textContent, 'The list is full: it keeps the 200 problems reported most recently, and 37 older ones made room for newer ones since the server started.');
    assert.equal(view.list.childNodes[1].className, 'bookmarksTableScroll');
    assert.equal(view.list.childNodes[1].childNodes[0].tagName, 'TABLE');
    assert.equal(view.list.querySelectorAll('tr').length, 201);
});

test('server values are written as text', async () => {
    const forged = Object.assign(entry('detailButtons', 5), { Anchor: '<img src=x onerror=alert(1)>', Client: '<b>x</b>', WebVersion: null });
    const view = await open(diagnostics([forged], { Accepted: 1 }));

    const cells = view.list.querySelectorAll('td');
    assert.equal(cells[0].textContent, '<img src=x onerror=alert(1)>');
    assert.equal(cells[2].textContent, 'web unknown (<b>x</b> 10.11.5)');
    assert.equal(view.list.querySelectorAll('img').length + view.list.querySelectorAll('b').length, 0);
});

test('the users line, and the banner when stored users do not match this server, are text', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.Users = {
        DeletedUserPolicy: 'KeepAsOrphan', StoredUsersMismatch: true, Message: '12 records belong to 3 unknown users.',
        Banner: "Stored users don't match this server; <b>was data restored</b>?", OrphanUsers: []
    };
    const view = await open(body);

    // The guard's banner is a warning at the top of the page; the unknown users' line stays in Diagnostics.
    const banner = assertBanners(view, ['bookmarksUsersBanner'])[0];
    assert.equal(banner.textContent, "Stored users don't match this server; <b>was data restored</b>?");
    assert.doesNotMatch(view.summary, /don't match/);
    assert.match(view.summary, /12 records belong to 3 unknown users\. Deleted-user policy: KeepAsOrphan\./);
    assert.deepEqual(view.errors, []);

    const quiet = diagnostics([], { Accepted: 0 });
    quiet.Users = { DeletedUserPolicy: 'Delete', Message: null, Banner: null, OrphanUsers: [] };
    const plain = await open(quiet);
    assert.match(plain.summary, /No data belongs to users this server doesn't know\. Deleted-user policy: Delete\./);
    assert.equal(plain.banners.childNodes.length, 0);
});

// The banners at the top of the page, above the tabs: warnings (class bookmarksBanner, role alert) in this order, each the
// server's sentence as plain text.
function assertBanners(view, classes) {
    const nodes = view.banners.childNodes;
    assert.deepEqual(nodes.map((n) => n.className), classes.map((c) => 'bookmarksBanner ' + c));
    assert.equal(view.banners.nextElementSibling.id, 'BookmarksTabs', 'the banners come before the tabs');
    nodes.forEach((n) => {
        assert.equal(n.tagName, 'P');
        assert.equal(n.getAttribute('role'), 'alert');
        assert.equal(n.children.length, 0, 'a banner is plain text');
    });
    return nodes;
}

test('a read-only bookmark file shows the store\'s sentence as a warning banner at the top, and the kept copies', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.Store = {
        File: 'C:/data/bookmarks/bookmarks.json', Version: 1, CurrentVersion: 2, Status: 'ReadOnlyMigrationFailed', ReadOnly: true,
        Message: 'The data couldn\'t be upgraded from schema 1 to 2 (the migration from version 1 to 2 failed); nothing was changed. <b>Install</b> the previous plugin version or restore an export.',
        KeptFiles: [
            { Name: 'bookmarks.json.v1-20260930010308', Kind: 'PreUpgradeCopy', SchemaVersion: 1, Utc: '2026-09-30T01:03:08Z', Size: 8971 },
            { Name: 'bookmarks.json.corrupt-20260929000000', Kind: 'Corrupt', SchemaVersion: null, Utc: '2026-09-29T00:00:00Z', Size: 12 }
        ],
        KeptFileCount: 5
    };
    body.Users = {
        DeletedUserPolicy: 'KeepAsOrphan', StoredUsersMismatch: true, Message: '12 records belong to 3 unknown users.',
        Banner: "Stored users don't match this server; was data restored from another server? Nothing was changed.", OrphanUsers: []
    };
    const view = await open(body);

    const banners = assertBanners(view, ['bookmarksStoreBanner', 'bookmarksUsersBanner']);
    assert.match(banners[0].textContent, /^The data couldn't be upgraded from schema 1 to 2 .*nothing was changed\. <b>Install<\/b> the previous plugin version/);
    assert.equal(view.summaryNode.querySelectorAll('.bookmarksBanner').length, 0);
    assert.doesNotMatch(view.summary, /couldn't be upgraded/);
    assert.match(view.summary, /Bookmark file: C:\/data\/bookmarks\/bookmarks\.json \(schema 1, ReadOnlyMigrationFailed\)\./);
    assert.match(view.summary, /Kept next to it until you delete them: bookmarks\.json\.v1-20260930010308 \(schema 1, from before an upgrade, [^)]+\), bookmarks\.json\.corrupt-20260929000000 \(unreadable, moved aside, [^)]+\), and 3 older\./);
    assert.deepEqual(view.errors, []);

    // Shown again (pageshow) once the data is fine and users match: the banners go.
    body.Store = { File: 'C:/data/bookmarks/bookmarks.json', Version: 2, CurrentVersion: 2, Status: 'Ok', ReadOnly: false, Message: null, KeptFiles: [], KeptFileCount: 0 };
    body.Users = { DeletedUserPolicy: 'KeepAsOrphan', StoredUsersMismatch: false, Message: null, Banner: null, OrphanUsers: [] };
    await view.rerender();
    assert.equal(view.banners.childNodes.length, 0);

    // Diagnostics that can't be loaded leave no banner from an earlier showing either.
    body.Store.Status = 'ReadOnlyNewerVersion';
    body.Store.Message = 'The data was written by a newer plugin version (schema 3; this version reads up to 2), so it is read-only. Install that version or restore an export.';
    await view.rerender();
    assert.equal(view.banners.childNodes.length, 1);
    view.api.getJSON = () => Promise.reject(new Error('503'));
    await view.rerender();
    assert.equal(view.banners.childNodes.length, 0);
    assert.equal(view.summaryNode.textContent, 'Diagnostics could not be loaded.');
    assert.deepEqual(view.errors, []);
});

for (const [status, message] of [
    ['ReadOnlyNewerVersion', 'The data was written by a newer plugin version (schema 3; this version reads up to 2), so it is read-only. Install that version or restore an export.'],
    ['RecoveredFromBackup', 'The data file couldn\'t be read and was moved to bookmarks.json.corrupt-20260930010308; the backup copy was used, so the most recent change may be missing.'],
    ['RecoveredEmpty', 'The data file couldn\'t be read and was moved to bookmarks.json.corrupt-20260930010308; there was no backup, so the data starts empty.']
]) {
    test(`a store that is ${status} shows its sentence as a warning banner`, async () => {
        const body = diagnostics([], { Accepted: 0 });
        body.Store = { File: 'bookmarks.json', Version: 2, CurrentVersion: 2, Status: status, ReadOnly: status.startsWith('ReadOnly'), Message: message, KeptFiles: [], KeptFileCount: 0 };
        const view = await open(body);

        assert.equal(assertBanners(view, ['bookmarksStoreBanner'])[0].textContent, message);
        assert.equal(view.summary.includes(message), false);
        assert.deepEqual(view.errors, []);
    });
}

test('an upgrade is a plain line, and a file without kept copies or a message adds nothing', async () => {
    const upgraded = diagnostics([], { Accepted: 0 });
    upgraded.Store = {
        File: 'bookmarks.json', Version: 1, Status: 'Migrated', ReadOnly: false,
        Message: 'The data was upgraded from schema 1 to 2; the previous file was kept as bookmarks.json.v1-20260930010308.',
        KeptFiles: [{ Name: 'bookmarks.json.v1-20260930010308', Kind: 'PreUpgradeCopy', SchemaVersion: 1, Utc: '2026-09-30T01:03:08Z', Size: 8971 }],
        KeptFileCount: 1
    };
    const view = await open(upgraded);
    assert.equal(view.banners.childNodes.length, 0, 'a plain upgrade is no warning');
    assert.equal(view.summaryNode.querySelectorAll('.bookmarksStoreBanner').length, 0);
    assert.match(view.summary, /The data was upgraded from schema 1 to 2; the previous file was kept as bookmarks\.json\.v1-20260930010308\./);
    assert.match(view.summary, /Kept next to it until you delete them: bookmarks\.json\.v1-20260930010308 \(schema 1, from before an upgrade, [^)]+\)\./);
    assert.doesNotMatch(view.summary, /older/);

    const plain = await open(diagnostics([], { Accepted: 0 }));
    assert.equal(plain.summaryNode.querySelectorAll('p').length, 3);
    assert.doesNotMatch(plain.summary, /Kept next to it/);
});

test('each user\'s bookmarks are counted on the Diagnostics tab, as text, with the file\'s size and its warning', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.BookmarkCounts = {
        Users: [
            { UserId: 'a1', UserName: 'alex <b>', Known: true, Bookmarks: 72, Named: 70, Ranges: 3, Detached: 2, Relinked: 1, Imported: 72, Clips: 2, ClipBytes: 3221225472, ClipsBeingMade: 1, Tombstones: 1 },
            { UserId: 'b2', UserName: null, Known: false, Bookmarks: 9, Named: 9, Ranges: 0, Detached: 0, Relinked: 0, Imported: 9, Clips: 0, ClipBytes: 0, ClipsBeingMade: 0, Tombstones: 0 }
        ],
        Total: { Users: 2, Bookmarks: 81, Named: 79, Ranges: 3, Detached: 2, Relinked: 1, Imported: 81, Clips: 2, ClipBytes: 3221225472, ClipsBeingMade: 1, Tombstones: 1 },
        FileBytes: 3355443,
        Warning: 'bookmarks.json is 3.2 MB. Every change writes the whole file, which stays quick up to about 5 MB.'
    };
    const view = await open(body);

    assert.ok(view.counts.parentNode.parentNode === view.q('#BookmarksDiagnostics'), 'on the Diagnostics tab');
    assert.equal(view.counts.querySelector('.bookmarksCountsSummary').textContent, '81 bookmark(s) of 2 user(s); 2 clip file(s), 3 GB. The bookmark file is 3.2 MB.');
    assert.equal(view.counts.querySelector('.bookmarksCountsWarning').textContent, 'bookmarks.json is 3.2 MB. Every change writes the whole file, which stays quick up to about 5 MB.');
    const rows = view.counts.querySelectorAll('table.bookmarksCountsTable tr').map((tr) => tr.children.map((c) => c.textContent));
    assert.deepEqual(rows[0], ['User', 'Bookmarks', 'Named', 'Ranges', 'Not in library', 'Relinked', 'From Jellyfin Enhanced', 'Clips', 'Clip size', 'Being made', 'Kept clips of deleted bookmarks']);
    assert.deepEqual(rows[1], ['alex <b>', '72', '70', '3', '2', '1', '72', '2', '3 GB', '1', '1']);
    assert.deepEqual(rows[2], ['b2 (not on this server)', '9', '9', '0', '0', '0', '9', '0', '0 MB', '0', '0']);
    assert.equal(view.counts.querySelectorAll('b').length, 0);

    // The entry's error, or an older server without it.
    body.BookmarkCounts = { Error: 'The store can\'t be read.' };
    await view.rerender();
    assert.equal(view.counts.textContent, 'The counts couldn\'t be read: The store can\'t be read.');
    delete body.BookmarkCounts;
    await view.rerender();
    assert.equal(view.counts.textContent, 'No counts from this server.');
    assert.deepEqual(view.errors, []);
});

test('every table is in a box of its own that scrolls sideways, so a phone scrolls the table and never the page', async () => {
    // The style: the box scrolls and takes the table's -1em, so the table has no margin to be cut off by.
    const css = /<style>([\s\S]*?)<\/style>/.exec(html)[1];
    assert.match(css, /#BookmarksConfigPage \.bookmarksTableScroll \{\s*margin-left: -1em;\s*overflow-x: auto;\s*\}/);
    assert.doesNotMatch(/#BookmarksConfigPage \.bookmarksTable \{([^}]*)\}/.exec(css)[1], /margin/);

    // A long path (the bookmark file's, the clip folder's on Diagnostics) breaks at the edge; break-word, which leaves a
    // table's columns their words, not anywhere.
    assert.match(css, /#BookmarksConfigPage \{\s*overflow-wrap: break-word;\s*\}/);

    // The per-user counts (11 columns), the snapshots and the unknown users, each in its box.
    const body = diagnostics([], { Accepted: 0 });
    const user = { UserId: 'a1', UserName: 'alex', Known: true, Bookmarks: 1, Named: 1, Ranges: 0, Detached: 0, Relinked: 0, Imported: 0, Clips: 0, ClipBytes: 0, ClipsBeingMade: 0, Tombstones: 0 };
    body.BookmarkCounts = { Users: [user], Total: Object.assign({}, user, { Users: 1 }), FileBytes: 2048, Warning: null };
    const view = await open(body);
    view.answer('Bookmarks/Admin/Snapshots', () => snapshots());
    await view.click(view.q('#BookmarksTabData'));
    const tables = view.root.querySelectorAll('table');
    assert.deepEqual(tables.map((t) => t.className).sort(), ['bookmarksTable bookmarksCountsTable', 'bookmarksTable bookmarksSnapshotsTable']);
    for (const table of tables) {
        assert.equal(table.parentNode.className, 'bookmarksTableScroll', table.className);
        assert.equal(table.parentNode.children.length, 1, 'the box holds the table only');
    }

    assert.deepEqual(view.errors, []);
});

// ------------------------------------------------------------------ clip storage

function clipStorage(overrides) {
    return Object.assign({
        Root: 'C:\\ProgramData\\Jellyfin\\Server\\data\\bookmarks\\clips', Exists: true, FreeSpace: 'Ok', ProbedPath: 'C:\\ProgramData\\Jellyfin\\Server\\data\\bookmarks\\clips',
        Volume: 'C:\\', FreeBytes: 284 * 1073741824, TotalBytes: 476 * 1073741824, MinFreeBytes: 2147483648, LowFreeSpace: false,
        Waiting: { Jobs: 0, EstimatedBytes: 0 }, PendingFileDeletes: 0,
        Message: 'Free space for clips: C:\\ has 284 GB free of 476 GB. Clips are refused when fewer than 2 GB would remain.', Warnings: []
    }, overrides);
}

test('the clip folder\'s free space is under its field, with no warning while there is room', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.ClipStorage = clipStorage();
    const view = await open(body);

    // Under the Clip folder field, in its container, in the form.
    assert.equal(view.storage.parentNode, view.field('ClipOutputPath').parentNode);
    assert.equal(view.storage.closest('form').id, 'BookmarksConfigForm');
    const lines = view.storage.querySelectorAll('p');
    assert.equal(lines.length, 1);
    assert.equal(lines[0].textContent, 'Free space for clips: C:\\ has 284 GB free of 476 GB. Clips are refused when fewer than 2 GB would remain.');
    assert.equal(view.storage.querySelectorAll('.bookmarksFieldWarning').length, 0);
    assert.match(view.summary, /Clip folder: C:\\ProgramData\\Jellyfin\\Server\\data\\bookmarks\\clips\. Clips waiting or being made: 0\. Old clip files waiting to be deleted: 0\./);
    assert.equal(view.banners.childNodes.length, 0, 'no banner at the top for free space');
    assert.deepEqual(view.errors, []);
});

test('LowFreeSpace, FolderUnavailable and FreeSpaceUnknown are amber lines under the field, as text', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.ClipStorage = clipStorage({
        Volume: 'D:\\', FreeBytes: 837518622, LowFreeSpace: true, Waiting: { Jobs: 2, EstimatedBytes: 536870912 }, PendingFileDeletes: 3,
        Message: 'Free space for clips: D:\\ has 0.8 GB free of 2.0 TB. <b>x</b>',
        Warnings: [
            { Code: 'LowFreeSpace', Message: 'D:\\ has 0.8 GB free. Clips are refused when fewer than 2 GB would remain.' },
            { Code: 'FreeSpaceUnknown', Message: '<img src=x onerror=alert(1)>' }
        ]
    });
    const view = await open(body);

    const warnings = view.storage.querySelectorAll('.bookmarksFieldWarning');
    assert.deepEqual(warnings.map((w) => w.getAttribute('data-code')), ['LowFreeSpace', 'FreeSpaceUnknown']);
    assert.equal(warnings[0].textContent, 'D:\\ has 0.8 GB free. Clips are refused when fewer than 2 GB would remain.');
    assert.equal(warnings[1].textContent, '<img src=x onerror=alert(1)>');
    warnings.forEach((w) => {
        assert.equal(w.tagName, 'P');
        assert.equal(w.getAttribute('role'), 'status');
        assert.equal(w.children.length, 0, 'a warning is plain text');
    });
    assert.equal(view.storage.querySelectorAll('p')[0].textContent, 'Free space for clips: D:\\ has 0.8 GB free of 2.0 TB. <b>x</b>');
    assert.equal(view.storage.querySelectorAll('b').length + view.storage.querySelectorAll('img').length, 0);
    assert.match(view.summary, /Clips waiting or being made: 2 \(about 0\.5 GB\)\. Old clip files waiting to be deleted: 3\./);

    // Shown again with room, the warnings go; Diagnostics that can't be loaded leave nothing behind.
    body.ClipStorage = clipStorage();
    await view.rerender();
    assert.equal(view.storage.querySelectorAll('.bookmarksFieldWarning').length, 0);
    view.api.getJSON = () => Promise.reject(new Error('503'));
    await view.rerender();
    assert.equal(view.storage.childNodes.length, 0);
    assert.deepEqual(view.errors, []);
});

test('a Diagnostics body without ClipStorage, or with its error, shows no line', async () => {
    const without = await open(diagnostics([], { Accepted: 0 }));
    assert.equal(without.storage.childNodes.length, 0);
    assert.doesNotMatch(without.summary, /Clip folder:/);

    const failed = diagnostics([], { Accepted: 0 });
    failed.ClipStorage = { Error: 'The probe failed.' };
    const view = await open(failed);
    assert.equal(view.storage.childNodes.length, 0);
    assert.deepEqual(without.errors.concat(view.errors), []);
});

test('typing a clip folder asks the server 400 ms after the last key, and a folder in a Movies or TV library is an amber line, as text (1.2.1)', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }), { ClipOutputPath: '' });
    const asked = [];
    const inLibrary = 'This folder is in the Movies library "<Films>": Jellyfin takes clips there for the films they come from and shows them as duplicates. Choose a folder of its own outside every Movies and TV library, or leave the field empty for the default folder.';
    view.handle('POST Bookmarks/Admin/ClipFolder/Check', (request) => {
        const path = JSON.parse(request.data).Path;
        asked.push(path);
        return Promise.resolve(path.indexOf('films') >= 0
            ? { Path: path, Valid: true, Warnings: [{ Code: 'InVideoLibrary', Message: inLibrary }] }
            : { Path: path, Valid: true, Warnings: [] });
    });
    const check = view.q('#BookmarksClipFolderCheck');
    assert.equal(check.getAttribute('aria-live'), 'polite');

    await view.type('ClipOutputPath', '/media/fi');
    await view.page.advance(200);
    await view.type('ClipOutputPath', '/media/films/clips');
    await view.page.advance(399);
    assert.deepEqual(asked, [], 'nothing asked while typing');
    await view.page.advance(1);
    await view.page.flush();
    assert.deepEqual(asked, ['/media/films/clips']);
    await view.page.flush();
    await view.page.flush();
    const warning = check.querySelector('.bookmarksClipFolderWarning');
    assert.equal(warning.getAttribute('data-code'), 'InVideoLibrary');
    assert.equal(warning.textContent, inLibrary);
    assert.equal(warning.querySelector('*'), null, 'the server text is text');

    // A folder of its own: no line. The saved value again: no line (Diagnostics' ClipStorage has its warnings).
    await view.type('ClipOutputPath', '/srv/clips');
    await view.page.advance(400);
    await view.page.flush();
    assert.equal(check.textContent, '');
    await view.type('ClipOutputPath', '/media/films/clips');
    await view.page.advance(400);
    await view.page.flush();
    assert.notEqual(check.textContent, '');
    await view.type('ClipOutputPath', '');
    await view.page.advance(400);
    await view.page.flush();
    assert.equal(check.textContent, '');

    // Saved: the line goes, the saved folder's warnings come from Diagnostics.
    await view.type('ClipOutputPath', '/media/films/clips');
    await view.page.advance(400);
    await view.page.flush();
    await view.submit();
    assert.equal(check.textContent, '');
    assert.deepEqual(view.errors, []);
});

test('saving reloads the free-space line', async () => {
    assert.ok(html.indexOf('id="ClipOutputPath"') < html.indexOf('id="BookmarksClipStorage"') && html.indexOf('id="BookmarksClipStorage"') < html.indexOf('id="MinFreeSpaceMegabytes"'),
        'the line is under the Clip folder field, the reserve right after it');

    const body = diagnostics([], { Accepted: 0 });
    body.ClipStorage = clipStorage();
    const view = await open(body, { MinFreeSpaceMegabytes: 2048, ClipOutputPath: '', CleanUpPartialFilesOnStartup: true });
    assert.equal(String(view.field('MinFreeSpaceMegabytes').value), '2048');

    const reads = view.diagnosticsReads();
    body.ClipStorage = clipStorage({ LowFreeSpace: true, Warnings: [{ Code: 'LowFreeSpace', Message: 'C:\\ has 284 GB free. Clips are refused when fewer than 300 GB would remain.' }] });
    view.field('MinFreeSpaceMegabytes').value = '307200';
    const [saved] = await view.submit();

    assert.equal(saved.MinFreeSpaceMegabytes, 307200);
    assert.equal(saved.CleanUpPartialFilesOnStartup, true, 'the other settings are saved as they were');
    assert.equal(view.diagnosticsReads(), reads + 1, 'Diagnostics is loaded again after saving');
    assert.equal(view.storage.querySelectorAll('.bookmarksFieldWarning').length, 1);
    assert.deepEqual(view.errors, []);
});

// ------------------------------------------------------------------ the missing-clip sweep

function clipSweep(lastRun, overrides) {
    return Object.assign({
        RetentionDays: 30, MarkedMissing: 0, OldestMissingSinceUtc: null,
        LastRun: lastRun === null ? null : Object.assign({
            StartedUtc: '2026-11-15T03:10:00Z', Milliseconds: 12, Skipped: null, RetentionDays: 30, Checked: 14, Present: 11, Missing: 2, Marked: 1,
            Cleared: 1, InProgress: 1, FolderUnavailable: 0, UnavailableFolders: [], Unreadable: 0, Refused: 0, Changed: 0, Archived: 1, ClipsForgotten: 0,
            Error: null
        }, lastRun)
    }, overrides);
}

test('the daily maintenance\'s look at the clip files is a line on the Diagnostics tab, as text', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.ClipSweep = clipSweep({}, { MarkedMissing: 2 });
    const view = await open(body);

    const line = view.summaryNode.querySelector('.bookmarksClipSweepLine');
    assert.ok(line, 'a line of its own');
    assert.match(line.textContent, /^Clip files checked .+: 11 there, 2 gone from their clip folder \(1 newly\), 1 back again, 1 being made \(left as they are\)\. Gone for 30 days or more: 1 deleted bookmarks' clips moved to the archive, 0 bookmarks' clips forgotten \(the bookmarks stay\)\. Marked missing now: 2\.$/);
    assert.equal(view.summaryNode.querySelectorAll('.bookmarksClipSweepFolder').length, 0, 'no folder offline, no amber line');
    assert.equal(view.banners.childNodes.length, 0, 'nothing at the top of the page');
    assert.deepEqual(view.errors, []);
});

test('the clip file renames not finished are a line on the Diagnostics tab: none, or in amber how many and the notes that can\'t be read', async () => {
    const none = diagnostics([], { Accepted: 0 });
    none.ClipRenames = { Unfinished: 0, Waiting: 0, Unreadable: 0, UnreadableFiles: [], Bookmarks: [], History: 3 };
    const quiet = await open(none);
    assert.equal(quiet.summaryNode.querySelector('.bookmarksClipRenamesLine').textContent, 'Clip file renames not finished: none.');

    const body = diagnostics([], { Accepted: 0 });
    body.ClipRenames = { Unfinished: 2, Waiting: 1, Unreadable: 1, UnreadableFiles: ['clip-rename-0123456789abcdef0123456789abcdef.json'], Bookmarks: [], History: 0 };
    const view = await open(body);
    const line = view.summaryNode.querySelector('.bookmarksClipRenamesLine');
    assert.equal(line.textContent, 'Clip file renames not finished: 2. Their clips are left as they are and show "Rename not finished" on My Clips; the server finishes each as soon as it can reach the clip\'s folder and save the bookmark data, trying again after a minute, then less often while nothing changes (every 16 minutes at most). 1 note can\'t be read (clip-rename-0123456789abcdef0123456789abcdef.json, in the plugin\'s data folder): look at that clip, then delete the note; its clip is held until then. Don\'t go back to Bookmarks 1.1 while any is waiting.');
    assert.equal(line.getAttribute('role'), 'alert');
    assert.ok(line.classList.contains('bookmarksFieldWarning'), 'amber');
    assert.deepEqual(quiet.errors.concat(view.errors), []);
});

test('a clip folder that wasn\'t there is an amber "Clip folder unavailable" line naming it, as text', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.ClipSweep = clipSweep({
        FolderUnavailable: 3,
        UnavailableFolders: [{ Folder: 'E:\\Jellyfin Clips', Clips: 3 }, { Folder: '<img src=x onerror=alert(1)>', Clips: 1 }]
    });
    const view = await open(body);

    const folders = view.summaryNode.querySelectorAll('.bookmarksClipSweepFolder');
    assert.deepEqual(folders.map((f) => f.textContent), [
        'Clip folder unavailable: E:\\Jellyfin Clips (3 clip(s) not checked; nothing in it is marked missing or archived while it is away).',
        'Clip folder unavailable: <img src=x onerror=alert(1)> (1 clip(s) not checked; nothing in it is marked missing or archived while it is away).'
    ]);
    folders.forEach((f) => {
        assert.ok(f.className.includes('bookmarksFieldWarning'), 'amber');
        assert.equal(f.getAttribute('role'), 'status');
        assert.equal(f.children.length, 0, 'plain text');
    });
    assert.equal(view.summaryNode.querySelectorAll('img').length, 0);
    assert.deepEqual(view.errors, []);
});

test('a clip folder left out for another reason says why, in amber: not recognised, its marker not written, every clip gone at once', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.ClipSweep = clipSweep({
        FolderUnavailable: 10, MarkersWritten: 2,
        UnavailableFolders: [
            { Folder: 'E:\\Clips', Clips: 3, Reason: 'NotThere' },
            { Folder: 'F:\\Clips', Clips: 2, Reason: 'NoMarker' },
            { Folder: '\\\\nas\\clips', Clips: 4, Reason: 'MarkerNotWritten' },
            { Folder: 'G:\\Clips', Clips: 1, Reason: 'AllMissing' },
            { Folder: 'H:\\Clips', Clips: 5, Reason: 'SomethingNew' }
        ]
    });
    const view = await open(body);

    assert.deepEqual(view.summaryNode.querySelectorAll('.bookmarksClipSweepFolder').map((f) => f.textContent), [
        'Clip folder unavailable: E:\\Clips (3 clip(s) not checked; nothing in it is marked missing or archived while it is away).',
        'Clip folder unavailable: F:\\Clips is there, but has neither Bookmarks\' marker file (.jfbm-clip-root) nor any of its clips, so it isn\'t taken for their folder (a disk or share that isn\'t mounted, or another drive under the same letter?), and new clips into it are refused. If its clips are gone for good, confirm the folder with the button below (they are then marked missing) or choose another clip folder. 2 clip(s) not checked; nothing in it is marked missing or archived.',
        'Clip folder unavailable: \\\\nas\\clips has its clips, but its marker file (.jfbm-clip-root) couldn\'t be written; let the server write in the folder. 4 clip(s) not checked; nothing in it is marked missing or archived.',
        'Clip folder unavailable: G:\\Clips: every one of its clips is gone at once (a folder emptied, or put back from an old copy?). If they were deleted on purpose, confirm the folder with the button below (they are then marked missing), or their owners can forget them on the My Clips page. 1 clip(s) not checked; nothing in it is marked missing or archived.',
        'Clip folder unavailable: H:\\Clips (5 clip(s) not checked; nothing in it is marked missing or archived while it is away).'
    ]);
    assert.match(view.summaryNode.querySelector('.bookmarksClipSweepLine').textContent, / 2 clip folder\(s\) given their marker file\.$/);
    assert.deepEqual(view.errors, []);
});

test('a folder left out unproven says so with its remedies, and every folder an admin may confirm has the button that does', async () => {
    // Some of its clips there, some gone, no marker; the remedy named, and "This is the clip folder; its clips are
    // gone" with each folder the server says may be confirmed (its RootId; never one whose marker can't be written).
    const body = diagnostics([], { Accepted: 0 });
    body.ClipSweep = clipSweep({
        FolderUnavailable: 9, FoldersMadeAgain: 1,
        UnavailableFolders: [
            { Folder: 'E:\\Clips', Clips: 3, Reason: 'NotThere', RootId: 'aaaaaaaaaaaa', Confirmable: true },
            { Folder: 'F:\\mnt\\clips', Clips: 4, Reason: 'Unproven', RootId: 'bbbbbbbbbbbb', Confirmable: true },
            { Folder: '\\\\nas\\clips', Clips: 2, Reason: 'MarkerNotWritten', RootId: 'cccccccccccc', Confirmable: false }
        ]
    });
    const view = await open(body);

    const lines = view.summaryNode.querySelectorAll('.bookmarksClipSweepFolder').map((f) => f.textContent);
    assert.deepEqual(lines, [
        'Clip folder unavailable: E:\\Clips (3 clip(s) not checked; nothing in it is marked missing or archived while it is away). If it is gone for good, confirm it with the button below: it is made again, and its clips are marked missing.',
        'Clip folder unavailable: F:\\mnt\\clips is there without its marker file (.jfbm-clip-root), and some of its clips are gone while others are there, so it isn\'t taken for their folder (a folder in its place holding a clip written while the real one was away, or an older copy?), and new clips into it are refused. If the gone ones are gone for good, confirm the folder with the button below (they are then marked missing, and their owners can forget them on the My Clips page) or choose another clip folder. 4 clip(s) not checked; nothing in it is marked missing or archived.',
        'Clip folder unavailable: \\\\nas\\clips has its clips, but its marker file (.jfbm-clip-root) couldn\'t be written; let the server write in the folder. 2 clip(s) not checked; nothing in it is marked missing or archived.'
    ]);
    const buttons = view.summaryNode.querySelectorAll('.bookmarksConfirmClipFolder');
    assert.deepEqual(buttons.map((b) => [b.textContent, b.getAttribute('data-root-id'), b.getAttribute('type')]), [
        ['This is the clip folder; its clips are gone', 'aaaaaaaaaaaa', 'button'],
        ['This is the clip folder; its clips are gone', 'bbbbbbbbbbbb', 'button']
    ]);
    assert.equal(buttons[1].getAttribute('aria-label'), 'Confirm F:\\mnt\\clips as the clip folder whose clips are gone');
    const kids = Array.from(view.summaryNode.children);
    assert.equal(kids[kids.indexOf(buttons[0]) - 1].textContent, lines[0], 'each button right after its folder\'s line');
    assert.match(view.summaryNode.querySelector('.bookmarksClipSweepLine').textContent, / The default clip folder was gone \(deleted\?\) and was made again with its marker file; its clips are marked missing\./);
    assert.deepEqual(view.errors, []);
});

test('confirming a clip folder asks the dry run, names the clips it will mark in the dialog, then confirms and reads Diagnostics again', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.ClipSweep = clipSweep({ FolderUnavailable: 2, UnavailableFolders: [{ Folder: 'F:\\Clips', Clips: 2, Reason: 'NoMarker', RootId: 'bbbbbbbbbbbb', Confirmable: true }] }, { RetentionDays: 45 });
    const view = await open(body);
    const posts = [];
    view.handle('POST Bookmarks/Admin/ClipFolders/bbbbbbbbbbbb/Confirm', (request) => {
        const dryRun = String(request.url).endsWith('dryRun=true');
        posts.push(dryRun ? 'dry run' : 'confirm');
        if (!dryRun) {
            view.setBody(Object.assign({}, body, { ClipSweep: clipSweep({ UnavailableFolders: [] }) }));
        }

        return Promise.resolve({ DryRun: dryRun, Folder: 'F:\\Clips', RootId: 'bbbbbbbbbbbb', Existed: false, Created: !dryRun, Marker: dryRun ? null : 'Written', Clips: 2, Present: 0, Gone: 2, MarkedBefore: 0, Marked: 2 });
    });
    const reads = view.diagnosticsReads();

    await view.click(view.summaryNode.querySelector('.bookmarksConfirmClipFolder'));
    await view.page.flush();
    await view.page.flush();

    assert.deepEqual(posts, ['dry run', 'confirm']);
    assert.deepEqual(view.confirms, [{
        title: 'Confirm the clip folder',
        text: 'Take F:\\Clips for the folder its clips were made in, and make it again (it isn\'t there)? 2 clips whose file isn\'t there are then marked missing, and 45 days on a deleted bookmark\'s clip moves to the archive and a bookmark\'s own clip is forgotten. No file is deleted, and a snapshot is taken first. Only do this when the files are really gone, not while a drive is unplugged or a disk isn\'t mounted.'
    }]);
    assert.ok(view.diagnosticsReads() > reads, 'Diagnostics is read again');
    assert.equal(view.summaryNode.querySelectorAll('.bookmarksClipSweepFolder').length, 0, 'the folder is no longer left out');
    assert.equal(view.summaryNode.querySelector('.bookmarksConfirmClipFolderDone').textContent, 'Confirmed F:\\Clips as the clip folder, made again, with its marker file: 2 clips marked missing.');
    assert.deepEqual(view.errors, []);
});

test('a confirmation cancelled sends nothing more, and one refused says why in amber', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.ClipSweep = clipSweep({ UnavailableFolders: [{ Folder: 'F:\\Clips', Clips: 1, Reason: 'AllMissing', RootId: 'bbbbbbbbbbbb', Confirmable: true }] });
    const cancelled = await open(body, {}, { confirm: false });
    const posts = [];
    cancelled.handle('POST Bookmarks/Admin/ClipFolders/bbbbbbbbbbbb/Confirm', (request) => {
        posts.push(String(request.url));
        return Promise.resolve({ DryRun: true, Folder: 'F:\\Clips', Existed: true, Created: false, Clips: 1, Present: 0, Gone: 1, MarkedBefore: 0, Marked: 1 });
    });
    await cancelled.click(cancelled.summaryNode.querySelector('.bookmarksConfirmClipFolder'));
    assert.equal(posts.length, 1);
    assert.match(posts[0], /dryRun=true$/);
    assert.match(cancelled.confirms[0].text, /^Take F:\\Clips for the folder its clips were made in\? 1 clip whose file isn't there is then marked missing, and 30 days on /);

    const refused = await open(body);
    refused.handle('POST Bookmarks/Admin/ClipFolders/bbbbbbbbbbbb/Confirm', () => Promise.reject({
        status: 503, json: () => Promise.resolve({ title: 'Service Unavailable', detail: 'The clip folder\'s marker file (.jfbm-clip-root) couldn\'t be written (let the server write in the folder), so nothing was marked.' })
    }));
    await refused.click(refused.summaryNode.querySelector('.bookmarksConfirmClipFolder'));
    await refused.page.flush();
    const error = refused.summaryNode.querySelector('.bookmarksConfirmClipFolderError');
    assert.equal(error.textContent, 'The clip folder\'s marker file (.jfbm-clip-root) couldn\'t be written (let the server write in the folder), so nothing was marked.');
    assert.equal(error.getAttribute('role'), 'alert');
    assert.deepEqual(refused.errors, []);
});

test('the Clip folder field\'s warning of a folder there but refused, with its button; a value above a field\'s max loads as the max', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.ClipStorage = {
        Root: 'F:\\Clips', Exists: true, FreeSpace: 'Ok', Message: 'Free space for clips: F:\\ has 100 GB free of 200 GB.', Waiting: { Jobs: 0, EstimatedBytes: 0 }, PendingFileDeletes: 0,
        Warnings: [
            { Code: 'FolderUnrecognised', Message: 'This folder is there without its marker file (.jfbm-clip-root), and 2 of its 3 clip(s) are gone, so Bookmarks doesn\'t take it for the folder they were made in.', RootId: 'bbbbbbbbbbbb' },
            { Code: 'LowFreeSpace', Message: 'F:\\ has 2 GB free.' }
        ]
    };
    const view = await open(body, Object.assign({}, everySetting, { MissingClipRetentionDays: 100000, MinFreeSpaceMegabytes: -3 }));

    const storage = view.storage;
    assert.deepEqual(storage.querySelectorAll('.bookmarksClipStorageWarning').map((w) => w.getAttribute('data-code')), ['FolderUnrecognised', 'LowFreeSpace']);
    const buttons = storage.querySelectorAll('.bookmarksConfirmClipFolder');
    assert.equal(buttons.length, 1);
    const kids = Array.from(storage.children);
    assert.equal(kids[kids.indexOf(buttons[0]) - 1].getAttribute('data-code'), 'FolderUnrecognised');
    assert.equal(buttons[0].getAttribute('data-root-id'), 'bbbbbbbbbbbb');

    // The server holds a retention above the field's max (an older server's): the field shows 36500, a save sends it.
    assert.equal(String(view.field('MissingClipRetentionDays').value), '36500');
    assert.equal(String(view.field('MinFreeSpaceMegabytes').value), '0');
    const saved = await view.submit();
    assert.equal(saved[saved.length - 1].MissingClipRetentionDays, 36500);
    assert.equal(saved[saved.length - 1].MinFreeSpaceMegabytes, 0);
    assert.deepEqual(view.errors, []);
});

test('the clip files\' line before a run, while read-only, with the retention off, after a failed run, and without the entry', async () => {
    const texts = [];
    for (const sweep of [
        clipSweep(null),
        clipSweep({ Skipped: 'ReadOnly' }),
        clipSweep({}, { RetentionDays: 0 }),
        clipSweep({ Error: 'The store couldn\'t be written.' }),
        { Error: 'No sweep here.' }
    ]) {
        const body = diagnostics([], { Accepted: 0 });
        body.ClipSweep = sweep;
        const view = await open(body);
        const line = view.summaryNode.querySelector('.bookmarksClipSweepLine');
        texts.push(line ? line.textContent : null);
        assert.deepEqual(view.errors, []);
    }

    assert.equal(texts[0], 'Clip files: not checked since the server started; the daily maintenance checks them.');
    assert.equal(texts[1], 'Clip files: not checked, since the bookmark data is read-only.');
    assert.match(texts[2], / Clips whose file is gone are kept for good\.$/);
    assert.match(texts[3], / The run failed: The store couldn't be written\.$/);
    assert.equal(texts[4], null);
    const without = await open(diagnostics([], { Accepted: 0 }));
    assert.equal(without.summaryNode.querySelector('.bookmarksClipSweepLine'), null);
});

// ------------------------------------------------------------------ Jellyfin Enhanced

// The server's JeImport entry: by default every JE user with bookmarks is imported (or there are none), so the
// checklist shows.
function jeImport(overrides) {
    return Object.assign({
        UsersWithBookmarks: 0, Pending: [], Problem: null, ChecklistReady: true, StoreReadOnly: false, LastAppliedUtc: null, Runs: [],
        LedgerEntries: 0, ImportedBookmarks: 0
    }, overrides || {});
}

function jellyfinEnhanced(overrides) {
    return Object.assign({
        Installed: true, Active: true, Version: '12.11.0.0', Status: 'Active', SettingsFile: true,
        Settings: { BookmarksEnabled: true }, Shortcuts: { BookmarkCurrentTime: 'B' }, UserShortcuts: [],
        CustomTabs: { Installed: true, Active: true, Tabs: [] }, PluginPages: { Installed: true, Pages: [] }, Problems: [],
        Overlap: {
            Feature: 'Bookmarks', On: true, Banner: "Jellyfin Enhanced's Bookmarks is on. Import its bookmarks, then turn it off to avoid two bookmark systems.", BannerHidden: false,
            Complete: false, CanUseB: false, UseBNote: null,
            Checklist: [
                { Key: 'BookmarksEnabled', Text: 'Jellyfin Enhanced settings, Bookmarks: Enable bookmarks off', Done: false, Detail: 'Still on. <b>Its</b> button goes at the next page load.', NeedsRestart: false, Optional: false },
                { Key: 'ShortcutAdmin', Text: 'Bookmark Current Time cleared (the server default)', Done: true, Detail: 'Off: its key is "Disabled".', NeedsRestart: false, Optional: false },
                { Key: 'PluginPagesLink', Text: 'Restart Jellyfin: the link gone', Done: false, Detail: 'Still listed.', NeedsRestart: true, Optional: false }
            ]
        },
        OurShortcuts: [{ Name: 'BookmarkNow', Label: 'Bookmark this moment', Combo: 'Shift+B', Valid: true, Reserved: false, Warning: null, Reservations: [] }],
        ReservedCombos: ['K', 'B']
    }, overrides || {});
}

test('the Jellyfin Enhanced banner and the checklist are rendered as text, with ticks and details; the checklist is on the Data tab', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.JeImport = jeImport();
    body.JellyfinEnhanced = jellyfinEnhanced();
    const view = await open(body);

    const banners = Array.from(view.banners.querySelectorAll('p'));
    assert.deepEqual(banners.map((b) => b.className), ['bookmarksBanner bookmarksJeBanner']);
    assert.equal(banners[0].textContent, "Jellyfin Enhanced's Bookmarks is on. Import its bookmarks, then turn it off to avoid two bookmark systems.");

    assert.ok(!view.je.classList.contains('hide'), 'the section shows while JE is installed');
    assert.equal(view.je.parentNode.id, 'BookmarksData', 'on the Data tab');
    const items = Array.from(view.checklist.querySelectorAll('li'));
    assert.deepEqual(items.map((i) => i.getAttribute('data-key')), ['BookmarksEnabled', 'ShortcutAdmin', 'PluginPagesLink']);
    assert.deepEqual(items.map((i) => i.getAttribute('data-done')), ['false', 'true', 'false']);
    assert.equal(items[0].querySelector('span').textContent, '☐ Jellyfin Enhanced settings, Bookmarks: Enable bookmarks off');
    assert.equal(items[1].querySelector('span').textContent, '✓ Bookmark Current Time cleared (the server default)');
    assert.equal(items[0].querySelector('.fieldDescription').textContent, 'Still on. <b>Its</b> button goes at the next page load.');
    assert.equal(view.checklist.querySelectorAll('b').length, 0);
    assert.ok(view.useBRow.classList.contains('hide'), 'no Use B while JE still takes B');
    assert.deepEqual(view.errors, []);
});

test('with the banner setting off the server leaves the banner out, and the page shows none', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.JeImport = jeImport();
    body.JellyfinEnhanced = jellyfinEnhanced();
    body.JellyfinEnhanced.Overlap.Banner = null;
    body.JellyfinEnhanced.Overlap.BannerHidden = true;
    const view = await open(body, { ShowJellyfinEnhancedBanner: false });
    assert.equal(view.banners.childNodes.length, 0);
    assert.equal(view.field('ShowJellyfinEnhancedBanner').checked, false);
    assert.equal(view.checklist.querySelectorAll('li').length, 3, 'the checklist stays');
    assert.deepEqual(view.errors, []);
});

test('without Jellyfin Enhanced the section and the banner stay hidden, and an older server answer without the entry is fine', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.JeImport = jeImport();
    body.JellyfinEnhanced = jellyfinEnhanced({ Installed: false, Overlap: { Feature: 'Bookmarks', On: false, Banner: null, Complete: true, CanUseB: true, UseBNote: null, Checklist: [] } });
    const view = await open(body);
    assert.equal(view.banners.querySelectorAll('p').length, 0);
    assert.ok(view.je.classList.contains('hide'));
    assert.ok(view.useBRow.classList.contains('hide'));

    const older = await open(diagnostics([], { Accepted: 0 }));
    assert.equal(older.banners.querySelectorAll('p').length, 0);
    assert.ok(older.je.classList.contains('hide'));
    assert.deepEqual(older.errors, []);
});

test('"Use B for bookmarks" shows once the server says B is durably free, is disabled with the server\'s reason while it is withheld, re-checks, saves B and shows it in the shortcut field', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.JeImport = jeImport();
    body.JellyfinEnhanced = jellyfinEnhanced({
        Overlap: {
            Feature: 'Bookmarks', On: false, Banner: null, Complete: false, CanUseB: true, UseBNote: null,
            Checklist: [{ Key: 'ShortcutAdmin', Text: 'Moved', Done: true, Detail: 'Moved: its key is "Shift+X".', NeedsRestart: false, Optional: false }]
        }
    });
    const view = await open(body, { BookmarkNowShortcut: 'Shift+B', EnableClips: true });
    assert.ok(!view.useBRow.classList.contains('hide'));
    assert.equal(view.useBButton.disabled, false);
    assert.equal(view.useBNote.textContent, 'Jellyfin Enhanced no longer takes B, so the bookmark shortcut can be B instead of Shift+B. Users keep their own device setting.');

    // Withheld with a reason (a cleared key comes back at the next start): the button shows disabled under the
    // server's note, as text, and a click saves nothing.
    const withheld = diagnostics([], { Accepted: 0 });
    withheld.JeImport = jeImport();
    withheld.JellyfinEnhanced = jellyfinEnhanced({
        Overlap: {
            Feature: 'Bookmarks', On: false, Banner: null, Complete: false, CanUseB: false,
            UseBNote: "Use B for bookmarks isn't offered yet: Jellyfin Enhanced puts <b>B</b> back the next time the server starts.",
            Checklist: [{ Key: 'ShortcutAdmin', Text: 'Cleared', Done: true, Detail: 'Cleared, but only until the next restart.', NeedsRestart: true, Optional: false }]
        }
    });
    const pending = await open(withheld, { BookmarkNowShortcut: 'Shift+B', EnableClips: true });
    assert.ok(!pending.useBRow.classList.contains('hide'), 'shown, so the reason is');
    assert.equal(pending.useBButton.disabled, true);
    assert.equal(pending.useBNote.textContent, "Use B for bookmarks isn't offered yet: Jellyfin Enhanced puts <b>B</b> back the next time the server starts.");
    assert.equal(pending.useBNote.querySelectorAll('b').length, 0, 'server text is text');
    assert.deepEqual(await pending.useB(), [], 'the click re-reads Diagnostics and saves nothing while CanUseB is false');
    assert.deepEqual(pending.errors, []);

    // JE took B back meanwhile (no reason beyond the unticked item): the click re-reads Diagnostics and saves nothing.
    const taken = diagnostics([], { Accepted: 0 });
    taken.JeImport = jeImport();
    taken.JellyfinEnhanced = jellyfinEnhanced();
    view.setBody(taken);
    assert.deepEqual(await view.useB(), []);
    assert.ok(view.useBRow.classList.contains('hide'), 're-rendered from the fresh answer');

    // Still free: the server-wide shortcut becomes B, the other settings as they were, Diagnostics is reloaded, and the
    // Settings tab's field says B, so a later Save keeps it.
    view.setBody(body);
    const saved = await view.useB();
    assert.equal(saved.length, 1);
    assert.equal(saved[0].BookmarkNowShortcut, 'B');
    assert.equal(saved[0].EnableClips, true);
    assert.equal(view.field('BookmarkNowShortcut').value, 'B');
    const again = await view.submit();
    assert.equal(again[1].BookmarkNowShortcut, 'B');
    assert.deepEqual(view.errors, []);

    // With our shortcut already B the button is not offered.
    const done = diagnostics([], { Accepted: 0 });
    done.JeImport = jeImport();
    done.JellyfinEnhanced = jellyfinEnhanced({
        Overlap: { Feature: 'Bookmarks', On: false, Banner: null, Complete: true, CanUseB: true, UseBNote: null, Checklist: [{ Key: 'ShortcutAdmin', Text: 'Cleared', Done: true, Detail: null, NeedsRestart: false, Optional: false }] },
        OurShortcuts: [{ Name: 'BookmarkNow', Label: 'Bookmark this moment', Combo: 'B', Valid: true, Reserved: false, Warning: null, Reservations: [] }]
    });
    const after = await open(done);
    assert.ok(after.useBRow.classList.contains('hide'));
    assert.equal(after.checklist.querySelectorAll('li .fieldDescription').length, 0, 'no detail, no line');
});

// ------------------------------------------------------------------ The Jellyfin Enhanced import

function importReport(overrides) {
    return Object.assign({
        DryRun: true, Format: 'je-bookmark/1', AppliedUtc: null, StoreReadOnly: false, Snapshot: null,
        Users: [
            { JeUserId: 'a1', UserId: 'u1', UserName: 'alice', Found: 5, Import: 3, MatchedById: 1, MatchedByProviderId: 1, Ambiguous: 0, Detached: 1, Updated: 0, Duplicates: 1, AlreadyImported: 0, Skipped: 1, Clamped: 1, Errors: [] },
            { JeUserId: 'b2', UserId: null, UserName: null, Found: 2, Import: 0, MatchedById: 0, MatchedByProviderId: 0, Ambiguous: 0, Detached: 0, Updated: 0, Duplicates: 0, AlreadyImported: 0, Skipped: 2, Clamped: 0, Errors: ['No user on this server has the id b2; <b>its</b> 2 Jellyfin Enhanced bookmark(s) are skipped.'] }
        ],
        Samples: [
            { JeUserId: 'a1', UserName: 'alice', JeKey: 'Bm_1', Label: '<b>Tears</b> in rain', ItemName: 'Blade Runner (1982)', StartTicks: 36650000000, Outcome: 'Detached', Reason: 'No video has <i>it</i>; kept as Not in library.' },
            { JeUserId: 'b2', UserName: null, JeKey: 'Bm_2', Label: null, ItemName: null, StartTicks: null, Outcome: 'Skipped', Reason: 'No user on this server has this id.' }
        ],
        MoreEntries: 3, ChecklistReady: false, Checklist: [], ChecklistComplete: false, CanUseB: false
    }, overrides || {});
}

test('the import section shows while JE is installed and says who waits; the checklist and Use B wait for an applied import', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.JeImport = jeImport({ UsersWithBookmarks: 2, ChecklistReady: false, Pending: [{ JeUserId: 'a1', UserName: 'alice' }, { JeUserId: 'b2', UserName: null }] });
    body.JellyfinEnhanced = jellyfinEnhanced({
        Overlap: {
            Feature: 'Bookmarks', On: true, Banner: 'banner', Complete: false, CanUseB: true, UseBNote: null,
            Checklist: [{ Key: 'BookmarksEnabled', Text: 'Enable bookmarks off', Done: false, Detail: null, NeedsRestart: false, Optional: false }]
        }
    });
    const view = await open(body, { BookmarkNowShortcut: 'Shift+B' });

    assert.ok(!view.jeImport.classList.contains('hide'));
    assert.equal(view.jeStatus.textContent, 'Not imported yet: the Jellyfin Enhanced bookmarks of alice, b2. Run the dry run, then Import.');
    assert.equal(view.jeDryRun.disabled, false);
    assert.equal(view.jeApply.disabled, true, 'no import before a dry run');

    assert.ok(!view.je.classList.contains('hide'), 'the section is there');
    assert.equal(view.checklist.querySelectorAll('li').length, 0, 'but no checklist yet');
    assert.ok(!view.jeGate.classList.contains('hide'));
    assert.equal(view.jeGate.textContent, "This checklist shows once Jellyfin Enhanced's bookmarks are imported above (a dry run doesn't count), so none are lost when its bookmarks are switched off. Not imported yet: alice, b2.");
    assert.ok(view.useBRow.classList.contains('hide'), 'no Use B before the import, even while B is free');

    // A click on a hidden Use B never saves: it re-reads Diagnostics and checks the gate too.
    assert.deepEqual(await view.useB(), []);

    // Imported: the checklist and Use B.
    view.setBody(Object.assign({}, body, { JeImport: jeImport({ UsersWithBookmarks: 2, ChecklistReady: true, LastAppliedUtc: '2026-10-07T12:00:00Z', ImportedBookmarks: 7 }) }));
    await view.rerender();
    assert.equal(view.checklist.querySelectorAll('li').length, 1);
    assert.ok(view.jeGate.classList.contains('hide'));
    assert.ok(!view.useBRow.classList.contains('hide'));
    assert.match(view.jeStatus.textContent, /^Imported on .+: 7 bookmark\(s\) here came from Jellyfin Enhanced\. Running the import again adds only what is new\.$/);
    assert.deepEqual(view.errors, []);
});

test("the dry run renders the server's report as text and enables Import, which posts dryRun=false and reloads Diagnostics", async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.JeImport = jeImport({ UsersWithBookmarks: 2, ChecklistReady: false, Pending: [{ JeUserId: 'a1', UserName: 'alice' }] });
    body.JellyfinEnhanced = jellyfinEnhanced();
    const view = await open(body);
    view.setImport((request) => Promise.resolve(importReport(/dryRun=false/.test(request.url)
        ? { DryRun: false, AppliedUtc: '2026-10-07T12:00:00Z', Snapshot: 'bookmarks-import-20261007-120000.json', ChecklistReady: true }
        : {})));

    await view.click(view.jeDryRun);

    const call = view.ajaxCalls[0];
    assert.equal(call.type, 'POST');
    assert.equal(call.url, 'http://127.0.0.1:8097/Bookmarks/Admin/Import/JellyfinEnhanced?dryRun=true');
    assert.equal(call.data, '{}');
    assert.equal(call.contentType, 'application/json');
    assert.equal(call.dataType, 'json');
    assert.equal(view.jeReport.querySelector('.bookmarksJeImportSummary').textContent,
        'Dry run, nothing was changed. The import would add 3 bookmark(s) (1 not in the library), update 0 and leave out 4 (1 duplicates, 0 already imported, 3 skipped).');
    const users = Array.from(view.jeReport.querySelectorAll('table.bookmarksJeImportUsers tr')).map((tr) => Array.from(tr.children).map((c) => c.textContent));
    assert.deepEqual(users[0], ['User', 'Found', 'Import', 'By item id', 'By TMDb id', 'Ambiguous', 'Not in library', 'Updated', 'Duplicates', 'Already imported', 'Skipped', 'Moved off the end']);
    assert.deepEqual(users.slice(1), [['alice', '5', '3', '1', '1', '0', '1', '0', '1', '0', '1', '1'], ['b2', '2', '0', '0', '0', '0', '0', '0', '0', '0', '2', '0']]);
    const errors = view.jeReport.querySelectorAll('.bookmarksJeImportError');
    assert.equal(errors.length, 1);
    assert.equal(errors[0].textContent, 'No user on this server has the id b2; <b>its</b> 2 Jellyfin Enhanced bookmark(s) are skipped.');
    const samples = Array.from(view.jeReport.querySelectorAll('table.bookmarksJeImportSamples tr')).slice(1).map((tr) => Array.from(tr.children).map((c) => c.textContent));
    assert.deepEqual(samples, [
        ['alice', '<b>Tears</b> in rain', 'Blade Runner (1982)', '1:01:05', 'Imported as Not in library', 'No video has <i>it</i>; kept as Not in library.'],
        ['b2', '', '', '', 'Skipped', 'No user on this server has this id.']
    ]);
    assert.equal(view.jeReport.querySelectorAll('b, i').length, 0, 'server text is text');
    assert.ok(view.jeReport.textContent.includes('And 3 more.'));
    assert.equal(view.jeApply.disabled, false, 'a dry run that answered enables Import');

    const reads = view.diagnosticsReads();
    await view.click(view.jeApply);
    assert.equal(view.ajaxCalls[1].url, 'http://127.0.0.1:8097/Bookmarks/Admin/Import/JellyfinEnhanced?dryRun=false');
    assert.ok(view.jeReport.querySelector('.bookmarksJeImportSummary').textContent.startsWith('Imported: added 3 bookmark(s) (1 not in the library), updated 0 and left out 4'));
    assert.ok(view.jeReport.textContent.includes('The bookmark data from just before the import was kept as bookmarks-import-20261007-120000.json.'));
    assert.equal(view.jeReport.querySelector('.bookmarksJeImportNext').textContent, "Next, switch Jellyfin Enhanced's bookmarks off with the checklist below.");
    assert.ok(view.diagnosticsReads() > reads, 'Diagnostics read again, so the gate opens');
    assert.equal(view.jeApply.disabled, true, 'the next import needs another dry run');
    assert.deepEqual(view.errors, []);
});

test('an error answer is shown as text from its ProblemDetails, and Import stays off; read-only data keeps it off', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.JeImport = jeImport({ UsersWithBookmarks: 1, ChecklistReady: false, Pending: [{ JeUserId: 'a1', UserName: 'alice' }] });
    body.JellyfinEnhanced = jellyfinEnhanced();
    const view = await open(body);
    view.setImport(() => Promise.reject({ status: 503, json: () => Promise.resolve({ title: 'Service Unavailable', detail: "Jellyfin Enhanced's data folder couldn't be listed: <denied>" }) }));

    await view.click(view.jeDryRun);
    const error = view.jeReport.querySelector('.bookmarksJeImportError');
    assert.equal(error.textContent, "Jellyfin Enhanced's data folder couldn't be listed: <denied>");
    assert.equal(error.getAttribute('role'), 'alert');
    assert.equal(view.jeApply.disabled, true);

    // An answer without a body: a 404 says there is nothing to import.
    view.setImport(() => Promise.reject({ status: 404 }));
    await view.click(view.jeDryRun);
    assert.equal(view.jeReport.textContent, 'Jellyfin Enhanced has no bookmarks on this server, so there is nothing to import.');

    // Read-only bookmark data: the dry run works, the import stays off.
    view.setBody(Object.assign({}, body, { JeImport: jeImport({ UsersWithBookmarks: 1, ChecklistReady: false, StoreReadOnly: true, Pending: [{ JeUserId: 'a1', UserName: 'alice' }] }) }));
    await view.rerender();
    assert.ok(view.jeStatus.textContent.endsWith('The bookmark data is read-only right now, so nothing can be imported.'));
    view.setImport(() => Promise.resolve(importReport({ StoreReadOnly: true })));
    await view.click(view.jeDryRun);
    assert.equal(view.jeApply.disabled, true);
    assert.deepEqual(view.errors, []);
});

test('without JE bookmarks there is nothing to import, and the checklist shows; without JE and its data the import is hidden', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.JeImport = jeImport();
    body.JellyfinEnhanced = jellyfinEnhanced();
    const view = await open(body);
    assert.ok(!view.jeImport.classList.contains('hide'));
    assert.equal(view.jeStatus.textContent, 'Jellyfin Enhanced has no bookmarks on this server, so there is nothing to import.');
    assert.equal(view.jeDryRun.disabled, true);
    assert.equal(view.checklist.querySelectorAll('li').length, 3);

    const gone = diagnostics([], { Accepted: 0 });
    gone.JeImport = jeImport();
    gone.JellyfinEnhanced = jellyfinEnhanced({ Installed: false, Overlap: { Feature: 'Bookmarks', On: false, Banner: null, Complete: true, CanUseB: false, UseBNote: null, Checklist: [] } });
    const without = await open(gone);
    assert.ok(without.jeImport.classList.contains('hide'));

    // JE uninstalled, its files still there: the import stays offered.
    gone.JeImport = jeImport({ UsersWithBookmarks: 1, ChecklistReady: false, Pending: [{ JeUserId: 'a1', UserName: 'alice' }] });
    const leftovers = await open(gone);
    assert.ok(!leftovers.jeImport.classList.contains('hide'));

    // The entry's error (or an older server without it): no import section, and the checklist stays closed.
    const failed = diagnostics([], { Accepted: 0 });
    failed.JeImport = { Error: 'No service' };
    failed.JellyfinEnhanced = jellyfinEnhanced();
    const broken = await open(failed);
    assert.ok(broken.jeImport.classList.contains('hide'));
    assert.equal(broken.checklist.querySelectorAll('li').length, 0);
    assert.ok(!broken.jeGate.classList.contains('hide'));
    assert.deepEqual(broken.errors, []);
});

// ------------------------------------------------------------------ export and import of the bookmark data

test('Export downloads the server\'s file with the session\'s authorization, under the server\'s file name, and says to keep it safe', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }));
    const created = [];
    view.page.context.URL = {
        createObjectURL: (blob) => {
            created.push(blob);
            return 'blob:export-1';
        },
        revokeObjectURL: (url) => created.push('revoked ' + url)
    };
    const clicks = [];
    view.page.document.addEventListener('click', (e) => {
        if (e.target.tagName === 'A') {
            clicks.push({ href: e.target.getAttribute('href'), download: e.target.getAttribute('download') });
        }
    });
    view.handle('GET Bookmarks/Admin/Export', () => Promise.resolve({
        status: 200,
        headers: { get: (name) => (name === 'Content-Disposition' ? 'attachment; filename=bookmarks-export-20261007-120000.json; filename*=UTF-8\'\'bookmarks-export-20261007-120000.json' : null) },
        blob: () => Promise.resolve({ size: 1234 })
    }));

    await view.click(view.q('#BookmarksExport'));
    assert.equal(view.ajaxCalls[0].type, 'GET');
    assert.equal(view.ajaxCalls[0].url, 'http://127.0.0.1:8097/Bookmarks/Admin/Export');
    assert.equal(view.ajaxCalls[0].dataType, undefined, 'the raw answer, not JSON');
    assert.deepEqual(clicks, [{ href: 'blob:export-1', download: 'bookmarks-export-20261007-120000.json' }]);
    assert.equal(view.q('#BookmarksData').querySelectorAll('a').length, 0, 'the link is gone again');
    assert.equal(view.q('#BookmarksExportStatus').textContent, 'Exported as bookmarks-export-20261007-120000.json. It holds every user\'s bookmark names and notes: keep it safe.');
    await view.page.advance(10000);
    assert.deepEqual(created.slice(1), ['revoked blob:export-1']);

    // A refusal (the data couldn't be upgraded) is the server's sentence, in amber.
    view.handle('GET Bookmarks/Admin/Export', () => Promise.reject({ status: 503, json: () => Promise.resolve({ title: 'Service Unavailable', detail: 'The data couldn\'t be upgraded, so it can\'t be exported <b>faithfully</b>.' }) }));
    await view.click(view.q('#BookmarksExport'));
    const error = view.q('#BookmarksExportStatus .bookmarksExportError');
    assert.equal(error.textContent, 'The data couldn\'t be upgraded, so it can\'t be exported <b>faithfully</b>.');
    assert.equal(error.getAttribute('role'), 'alert');
    assert.deepEqual(view.errors, []);
});

function dataImportReport(overrides) {
    return Object.assign({
        DryRun: true, Mode: 'Merge', MapUsersBy: 'Id', SourcePluginVersion: '1.1.0.0', SourceServerId: 's1', ExportedUtc: '2026-10-07T12:00:00Z', SchemaVersion: 2, MigratedFrom: null,
        Users: { Mapped: [{ Id: 'a1', NewId: 'a1', Name: 'alex', Records: 5 }], Unknown: [{ Id: 'c3', Name: '<i>gone</i>', Records: 2, Reason: 'NotOnThisServer' }] },
        Records: { Incoming: 7, Added: 3, Updated: 1, Unchanged: 1, KeptNewer: 0, Removed: 0, SkippedUnknownUser: 2, Skipped: 0 },
        Archive: { Records: 0, Imported: 0, Note: null },
        Settings: 'Included in the export for reference; an import never applies settings.',
        SafetySnapshot: null, Notes: ['1 record(s) kept without their clip file: the clip file path is outside this server\'s clip folders (x).'],
        ClipsBeingMade: 0, StoreReadOnly: false
    }, overrides || {});
}

test('an export file is dry-run first with the chosen options, the report is text, and Import posts the same options after a confirmation', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }));
    const posted = [];
    view.handle('POST Bookmarks/Admin/Import', (request) => {
        posted.push(request);
        const q = queryOf(request.url);
        return Promise.resolve(dataImportReport(q.get('dryRun') === 'false' ? { DryRun: false, SafetySnapshot: 'bookmarks-import-20261007-120500.json' } : {}));
    });
    assert.equal(view.q('#BookmarksImportDryRun').disabled, true, 'no file, no dry run');

    const input = view.q('#BookmarksImportFile');
    input.files = [{ name: 'bookmarks-export.json', text: () => Promise.resolve('{"Format":"jellyfin-plugin-export/1"}') }];
    view.page.dispatch(input, 'change');
    await view.page.flush();
    assert.equal(view.q('#BookmarksImportDryRun').disabled, false);
    assert.equal(view.q('#BookmarksImportApply').disabled, true);

    view.q('#BookmarksImportUsers').value = 'Name';
    view.page.dispatch(view.q('#BookmarksImportUsers'), 'change');
    await view.click(view.q('#BookmarksImportDryRun'));
    assert.equal(posted.length, 1);
    assert.equal(posted[0].url, 'http://127.0.0.1:8097/Bookmarks/Admin/Import?mode=Merge&mapUsersBy=Name&dryRun=true');
    assert.equal(posted[0].data, '{"Format":"jellyfin-plugin-export/1"}');
    assert.equal(posted[0].contentType, 'application/json');
    const report = view.q('#BookmarksImportReport');
    assert.match(report.querySelector('.bookmarksImportSummary').textContent, /^Dry run, nothing was changed\. The file is an export of Bookmarks 1\.1\.0\.0 \(schema 2\) from .+, with 7 bookmarks\. The import would add 3, update 1, leave 1 unchanged, keep 0 newer here, remove 0, and skip 2 \(2 of users not found here\)\.$/);
    const users = report.querySelectorAll('table.bookmarksImportUsers tr').slice(1).map((tr) => tr.children.map((c) => c.textContent));
    assert.deepEqual(users, [['alex', 'Found', '5'], ['<i>gone</i>', 'Not found: NotOnThisServer', '2']]);
    assert.equal(report.querySelectorAll('.bookmarksImportNote').length, 1);
    assert.equal(report.querySelectorAll('i').length, 0, 'server text is text');
    assert.equal(view.q('#BookmarksImportApply').disabled, false);

    // Another option needs another dry run.
    view.q('#BookmarksImportMode').value = 'Replace';
    view.page.dispatch(view.q('#BookmarksImportMode'), 'change');
    assert.equal(view.q('#BookmarksImportApply').disabled, true);
    await view.click(view.q('#BookmarksImportDryRun'));
    assert.equal(posted[1].url, 'http://127.0.0.1:8097/Bookmarks/Admin/Import?mode=Replace&mapUsersBy=Name&dryRun=true');

    const reads = view.diagnosticsReads();
    await view.click(view.q('#BookmarksImportApply'));
    assert.equal(view.confirms.length, 1);
    assert.match(view.confirms[0].text, /^Replace every bookmark on this server with the file's\?/);
    assert.equal(posted[2].url, 'http://127.0.0.1:8097/Bookmarks/Admin/Import?mode=Replace&mapUsersBy=Name&dryRun=false');
    assert.match(report.querySelector('.bookmarksImportSummary').textContent, /^Imported\. /);
    assert.ok(report.textContent.includes('The bookmark data from just before the import was kept as bookmarks-import-20261007-120500.json'));
    assert.ok(view.diagnosticsReads() > reads, 'Diagnostics read again');
    assert.equal(view.q('#BookmarksImportApply').disabled, true, 'the next import needs another dry run');
    assert.deepEqual(view.errors, []);
});

test('the import says when it would be refused now (clips being made, read-only data), keeps Import off, and shows a refusal as text; Cancel on the confirmation imports nothing', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }), {}, { confirm: false });
    let answer = dataImportReport({ ClipsBeingMade: 2 });
    view.handle('POST Bookmarks/Admin/Import', () => Promise.resolve(answer));
    const input = view.q('#BookmarksImportFile');
    input.files = [{ name: 'x.json', text: () => Promise.resolve('{}') }];
    view.page.dispatch(input, 'change');
    await view.click(view.q('#BookmarksImportDryRun'));
    assert.equal(view.q('#BookmarksImportReport .bookmarksImportBlocked').textContent, 'Clips are being made right now (2). Import once they are done, after another dry run.');
    assert.equal(view.q('#BookmarksImportApply').disabled, true);

    answer = dataImportReport({ StoreReadOnly: true });
    await view.click(view.q('#BookmarksImportDryRun'));
    assert.equal(view.q('#BookmarksImportReport .bookmarksImportBlocked').textContent, 'The bookmark data is read-only right now, so nothing can be imported.');
    assert.equal(view.q('#BookmarksImportApply').disabled, true);

    answer = dataImportReport();
    await view.click(view.q('#BookmarksImportDryRun'));
    const calls = view.ajaxCalls.length;
    await view.click(view.q('#BookmarksImportApply'));
    assert.equal(view.ajaxCalls.length, calls, 'Cancel imports nothing');

    view.handle('POST Bookmarks/Admin/Import', () => Promise.reject({ status: 409, json: () => Promise.resolve({ title: 'Conflict', detail: 'The export is from a newer plugin version (schema 3) <b>!</b>' }) }));
    await view.click(view.q('#BookmarksImportDryRun'));
    const error = view.q('#BookmarksImportReport .bookmarksImportError');
    assert.equal(error.textContent, 'The export is from a newer plugin version (schema 3) <b>!</b>');
    assert.equal(view.q('#BookmarksImportApply').disabled, true);
    assert.deepEqual(view.errors, []);
});

// ------------------------------------------------------------------ snapshots and the maintenance

function snapshots(overrides) {
    return Object.assign({
        Snapshots: [
            { Name: 'bookmarks-import-20261007-120500.json', Kind: 'PreOp', Operation: 'import', Utc: '2026-10-07T12:05:00Z', Size: 120000, SchemaVersion: 2, Kept: false },
            { Name: 'bookmarks-20261007-031000.json', Kind: 'Daily', Operation: null, Utc: '2026-10-07T03:10:00Z', Size: 2048, SchemaVersion: 2, Kept: false },
            { Name: 'bookmarks-20261001-031000.json', Kind: 'Daily', Operation: null, Utc: '2026-10-01T03:10:00Z', Size: 4096, SchemaVersion: 3, Kept: true }
        ],
        KeepSnapshotsBeforeUtc: null, SnapshotsToKeep: 7, StoreReadOnly: false, ClipsBeingMade: 0
    }, overrides || {});
}

test('the Data tab lists the snapshots, and Restore asks first, restores the one chosen and reads the list and Diagnostics again', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }));
    view.answer('Bookmarks/Admin/Snapshots', () => snapshots());
    const restores = [];
    view.handle('POST Bookmarks/Admin/Snapshots/Restore', (request) => {
        restores.push(request);
        return Promise.resolve({ RestoredFrom: 'bookmarks-20261007-031000.json', SafetySnapshot: 'bookmarks-restore-20261007-130000.json', SchemaVersion: 2, MigratedFrom: null, Bookmarks: 79 });
    });
    await view.click(view.q('#BookmarksTabData'));

    const node = view.q('#BookmarksSnapshots');
    assert.equal(node.querySelector('.bookmarksSnapshotsSummary').textContent, '3 snapshots, newest first. Daily snapshots kept: 7.');
    const rows = node.querySelectorAll('table.bookmarksSnapshotsTable tr').slice(1).map((tr) => tr.children.map((c) => c.textContent));
    assert.deepEqual(rows.map((r) => r.slice(1, 4)), [['Before an import', '117 KB', '2'], ['Daily', '2 KB', '2'], ['Daily (kept: there when the data was found missing or unreadable)', '4 KB', '3']]);
    const buttons = node.querySelectorAll('button.bookmarksRestore');
    assert.deepEqual(buttons.map((b) => b.disabled), [false, false, true], 'a newer schema\'s snapshot can\'t be restored');

    const reads = view.diagnosticsReads();
    await view.click(buttons[1]);
    assert.equal(view.confirms.length, 1);
    assert.match(view.confirms[0].text, /^Restore the snapshot of .+\? Every bookmark becomes the snapshot's/);
    assert.equal(restores.length, 1);
    assert.equal(restores[0].url, 'http://127.0.0.1:8097/Bookmarks/Admin/Snapshots/Restore?name=bookmarks-20261007-031000.json');
    assert.equal(view.q('#BookmarksRestoreReport').textContent, 'Restored bookmarks-20261007-031000.json (79 bookmarks). The data from just before was kept as bookmarks-restore-20261007-130000.json.');
    assert.ok(view.diagnosticsReads() > reads);
    assert.ok(view.getCalls.filter((u) => u.endsWith('/Bookmarks/Admin/Snapshots')).length >= 2, 'the list read again');

    // A snapshot taken while clips were being made: their jobs are ended, and the page says so.
    for (const [ended, sentence] of [[1, '1 clip was being made when the snapshot was taken: it is marked failed, and can be made again.'], [2, '2 clips were being made when the snapshot was taken: they are marked failed, and can be made again.']]) {
        view.handle('POST Bookmarks/Admin/Snapshots/Restore', () => Promise.resolve({ RestoredFrom: 'bookmarks-20261007-031000.json', SafetySnapshot: null, SchemaVersion: 2, MigratedFrom: null, Bookmarks: 1, ClipJobsEnded: ended }));
        await view.click(view.q('#BookmarksSnapshots button.bookmarksRestore'));
        assert.equal(view.q('#BookmarksRestoreReport').textContent, 'Restored bookmarks-20261007-031000.json (1 bookmark). ' + sentence);
    }

    // Refused: the server's sentence, in amber.
    view.handle('POST Bookmarks/Admin/Snapshots/Restore', () => Promise.reject({ status: 409, json: () => Promise.resolve({ title: 'Conflict', detail: 'Clips are being made right now. <b>Wait</b>.' }) }));
    await view.click(view.q('#BookmarksSnapshots button.bookmarksRestore'));
    assert.equal(view.q('#BookmarksRestoreReport .bookmarksRestoreError').textContent, 'Clips are being made right now. <b>Wait</b>.');
    assert.deepEqual(view.errors, []);
});

test('Restore is off while clips are being made or the data is read-only, with the reason, and Cancel restores nothing; no snapshots says so', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }), {}, { confirm: false });
    view.answer('Bookmarks/Admin/Snapshots', () => snapshots({ ClipsBeingMade: 1 }));
    await view.click(view.q('#BookmarksTabData'));
    assert.equal(view.q('#BookmarksSnapshots .bookmarksSnapshotsBlocked').textContent, 'Clips are being made right now (1); a snapshot can be restored once they are done.');
    assert.ok(view.q('#BookmarksSnapshots').querySelectorAll('button.bookmarksRestore').every((b) => b.disabled));

    view.answer('Bookmarks/Admin/Snapshots', () => snapshots({ StoreReadOnly: true }));
    await view.click(view.q('#BookmarksTabSettings'));
    await view.click(view.q('#BookmarksTabData'));
    assert.equal(view.q('#BookmarksSnapshots .bookmarksSnapshotsBlocked').textContent, 'The bookmark data is read-only right now, so no snapshot can be restored.');

    view.answer('Bookmarks/Admin/Snapshots', () => snapshots());
    await view.click(view.q('#BookmarksTabSettings'));
    await view.click(view.q('#BookmarksTabData'));
    const calls = view.ajaxCalls.length;
    await view.click(view.q('#BookmarksSnapshots button.bookmarksRestore'));
    assert.equal(view.ajaxCalls.length, calls, 'Cancel restores nothing');

    view.answer('Bookmarks/Admin/Snapshots', () => snapshots({ Snapshots: [], SnapshotsToKeep: 0 }));
    await view.click(view.q('#BookmarksTabSettings'));
    await view.click(view.q('#BookmarksTabData'));
    assert.equal(view.q('#BookmarksSnapshots').textContent, 'No snapshots yet: the daily maintenance takes the first one once Daily snapshots to keep is more than 0.');
    assert.deepEqual(view.errors, []);
});

test('Bookmarks 1.2: missing data offers the way out, Restore of a listed snapshot in the kit\'s dialog with the focus on Cancel; Cancel and Escape restore nothing', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }), {}, { kit: true });
    view.answer('Bookmarks/Admin/Snapshots', () => snapshots({ StoreReadOnly: true, StoreReadOnlyReason: 'DataMissing', Recoverable: true }));
    const posts = [];
    view.handle('POST Bookmarks/Admin/Snapshots/Restore', (request) => {
        posts.push(request);
        return Promise.resolve({ RestoredFrom: 'bookmarks-20261007-031000.json', SnapshotUtc: '2026-10-07T03:10:00Z', StartedEmpty: false, MovedAside: [], SafetySnapshot: null, Bookmarks: 79, ClipJobsEnded: 1 });
    });
    await view.click(view.q('#BookmarksTabData'));

    assert.equal(view.q('#BookmarksRecovery').classList.contains('hide'), false);
    assert.equal(view.q('#BookmarksRecoveryText').textContent, 'The bookmark data is missing, and no snapshot could be restored on its own (the banner at the top of the page says why), so nothing can be changed. Restore one of the snapshots below, or start empty. Every snapshot is kept either way.');
    assert.equal(view.q('#BookmarksSnapshots .bookmarksSnapshotsBlocked'), null, 'no "read-only" line: Restore is the way out');
    const buttons = view.q('#BookmarksSnapshots').querySelectorAll('button.bookmarksRestore');
    assert.deepEqual(buttons.map((b) => b.disabled), [false, false, true], 'a newer schema\'s snapshot still can\'t be restored');

    // The kit's dialog, not jellyfin-web's, and the focus on Cancel; Cancel and Escape send nothing.
    await view.click(buttons[1]);
    const dialog = view.q('[role="dialog"]');
    assert.ok(dialog, 'the kit\'s dialog');
    assert.equal(view.confirms.length, 0);
    assert.match(dialog.querySelector('.bookmarksDialogText').textContent, /^Restore the snapshot of .+\? The bookmark data becomes the snapshot's, and can be changed again at once; what was added or changed after it is missing\. Every snapshot is kept, this one too\.$/);
    const cancel = dialog.querySelector('.bookmarksDialogCancel');
    assert.ok(view.page.document.activeElement === cancel, 'the focus starts on Cancel');
    await view.click(cancel);
    assert.equal(view.q('[role="dialog"]'), null);
    await view.click(buttons[1]);
    view.page.key(view.page.document.activeElement, 'Escape');
    await view.page.flush();
    assert.equal(view.q('[role="dialog"]'), null);
    assert.equal(posts.length, 0);

    const reads = view.diagnosticsReads();
    await view.click(buttons[1]);
    await view.click(view.q('[role="dialog"] .bookmarksRecoverConfirm'));
    assert.equal(view.q('[role="dialog"]'), null);
    assert.equal(posts.length, 1);
    assert.equal(posts[0].url, 'http://127.0.0.1:8097/Bookmarks/Admin/Snapshots/Restore?name=bookmarks-20261007-031000.json');
    assert.equal(view.q('#BookmarksRestoreReport').textContent, 'Restored bookmarks-20261007-031000.json (79 bookmarks); the bookmark data can be changed again. Every snapshot is kept until you restore one of them. 1 clip was being made when the snapshot was taken: it is marked failed, and can be made again.');
    assert.ok(view.diagnosticsReads() > reads, 'the banner read again');
    assert.ok(view.getCalls.filter((u) => u.endsWith('/Bookmarks/Admin/Snapshots')).length >= 2, 'the list read again');
    assert.deepEqual(view.errors, []);
});

test('Bookmarks 1.2: unreadable data says so, and Start empty is confirmed in the kit\'s dialog, posts once, says what was kept, and shows a refusal in amber', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }), {}, { kit: true });
    view.answer('Bookmarks/Admin/Snapshots', () => snapshots({ StoreReadOnly: true, StoreReadOnlyReason: 'DataUnreadable', Recoverable: true }));
    const posts = [];
    view.handle('POST Bookmarks/Admin/Data/StartEmpty', (request) => {
        posts.push(request);
        return Promise.resolve({ RestoredFrom: null, StartedEmpty: true, MovedAside: ['bookmarks.json.corrupt-20261020120000'], KeepSnapshotsBeforeUtc: '2026-10-20T12:00:00Z', Bookmarks: 0 });
    });
    await view.click(view.q('#BookmarksTabData'));

    assert.equal(view.q('#BookmarksRecoveryText').textContent, 'The bookmark data couldn\'t be read, and no snapshot could take its place on its own (the banner at the top of the page says why), so nothing can be changed. Restore one of the snapshots below, or start empty. Every snapshot is kept either way, and so is the file that couldn\'t be read, as bookmarks.json.corrupt-<time>.');
    const startEmpty = view.q('#BookmarksStartEmpty');
    assert.equal(startEmpty.disabled, false);
    await view.click(startEmpty);
    const dialog = view.q('[role="dialog"]');
    assert.equal(dialog.querySelector('h3').textContent, 'Start empty?');
    assert.equal(dialog.querySelector('.bookmarksDialogText').textContent, 'Start the bookmark data empty? Every user\'s bookmarks are gone from Bookmarks until a snapshot that holds them is restored. Every snapshot is kept, and so is the file that couldn\'t be read, as bookmarks.json.corrupt-<time>. Only do this when none of the snapshots holds the bookmarks you want.');
    assert.ok(view.page.document.activeElement === dialog.querySelector('.bookmarksDialogCancel'), 'the focus starts on Cancel');
    await view.click(dialog.querySelector('.bookmarksStartEmptyConfirm'));
    assert.equal(posts.length, 1);
    assert.equal(posts[0].url, 'http://127.0.0.1:8097/Bookmarks/Admin/Data/StartEmpty');
    assert.equal(view.q('#BookmarksRestoreReport .bookmarksStartedEmpty').textContent, 'The bookmark data starts empty, and can be changed again. What couldn\'t be read is kept as bookmarks.json.corrupt-20261020120000. Every snapshot is kept until you restore one of them.');

    // Refused (the data recovered meanwhile, say): the server's sentence, as text, in amber.
    view.handle('POST Bookmarks/Admin/Data/StartEmpty', () => Promise.reject({ status: 409, json: () => Promise.resolve({ title: 'Conflict', detail: 'The data isn\'t missing or unreadable, so there is nothing to recover. <b>No</b>.' }) }));
    await view.click(startEmpty);
    await view.click(view.q('[role="dialog"] .bookmarksStartEmptyConfirm'));
    assert.equal(view.q('#BookmarksRestoreReport .bookmarksRestoreError').textContent, 'The data isn\'t missing or unreadable, so there is nothing to recover. <b>No</b>.');
    assert.deepEqual(view.errors, []);
});

test('Bookmarks 1.2: without the web client the way out asks jellyfin-web\'s confirmation; data that is there, or read-only for another reason, shows no way out', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }), {}, { confirm: false });
    view.answer('Bookmarks/Admin/Snapshots', () => snapshots({ StoreReadOnly: true, StoreReadOnlyReason: 'DataMissing', Recoverable: true }));
    await view.click(view.q('#BookmarksTabData'));
    const calls = view.ajaxCalls.length;
    await view.click(view.q('#BookmarksStartEmpty'));
    assert.equal(view.confirms.length, 1);
    assert.equal(view.confirms[0].title, 'Start empty?');
    assert.equal(view.ajaxCalls.length, calls, 'cancelled: nothing sent');

    for (const answer of [snapshots(), snapshots({ StoreReadOnly: true, StoreReadOnlyReason: 'NewerVersion', Recoverable: false })]) {
        view.answer('Bookmarks/Admin/Snapshots', () => answer);
        await view.click(view.q('#BookmarksTabSettings'));
        await view.click(view.q('#BookmarksTabData'));
        assert.equal(view.q('#BookmarksRecovery').classList.contains('hide'), true);
    }

    assert.equal(view.q('#BookmarksSnapshots .bookmarksSnapshotsBlocked').textContent, 'The bookmark data is read-only right now, so no snapshot can be restored.');
    assert.deepEqual(view.errors, []);
});

test('the kept snapshots say why they are kept by how the data was found, pre-operation ones too, and Restore is off for one this version can\'t restore', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }));
    const listed = [
        { Name: 'bookmarks-import-20261007-120500.json', Kind: 'PreOp', Operation: 'import', Utc: '2026-10-07T12:05:00Z', Size: 120000, SchemaVersion: 2, Kept: true },
        { Name: 'bookmarks-20261007-031000.json', Kind: 'Daily', Operation: null, Utc: '2026-10-07T03:10:00Z', Size: 2048, SchemaVersion: null, Kept: true },
        { Name: 'bookmarks-20261006-031000.json', Kind: 'Daily', Operation: null, Utc: '2026-10-06T03:10:00Z', Size: 2048, SchemaVersion: 2, Kept: false }
    ];
    const cases = {
        RestoredFromSnapshot: 'there when the data was restored from a snapshot',
        RecoveredEmpty: 'there when the data started empty',
        RecoveredFromBackup: 'there when the data was restored from its backup',
        Unknown: 'there when the data was found missing or unreadable'
    };
    for (const [because, words] of Object.entries(cases)) {
        view.answer('Bookmarks/Admin/Snapshots', () => snapshots({ Snapshots: listed, KeptBecause: because === 'Unknown' ? null : because, KeepSnapshotsBeforeUtc: '2026-10-07T12:05:01Z' }));
        await view.click(view.q('#BookmarksTabSettings'));
        await view.click(view.q('#BookmarksTabData'));
        const rows = view.q('#BookmarksSnapshots').querySelectorAll('table.bookmarksSnapshotsTable tr').slice(1).map((tr) => tr.children.map((c) => c.textContent));
        assert.deepEqual(rows.map((r) => r[1]), ['Before an import (kept: ' + words + ')', 'Daily (kept: ' + words + ')', 'Daily'], because);
        assert.ok(rows.every((r) => !r[1].includes('started empty') || because === 'RecoveredEmpty'), because);
    }

    // A snapshot whose schema can't be read (unreadable, or no bytes) can't be restored: its Restore is off, and says why.
    const buttons = view.q('#BookmarksSnapshots').querySelectorAll('button.bookmarksRestore');
    assert.deepEqual(buttons.map((b) => b.disabled), [false, true, false]);
    assert.equal(buttons[1].getAttribute('title'), 'This snapshot can\'t be read, so it can\'t be restored.');
    assert.equal(view.q('#BookmarksSnapshots').querySelectorAll('table.bookmarksSnapshotsTable tr').slice(1).map((tr) => tr.children[3].textContent)[1], '?');

    // While the data is missing or unreadable too (the recovery's list).
    view.answer('Bookmarks/Admin/Snapshots', () => snapshots({ Snapshots: listed, StoreReadOnly: true, StoreReadOnlyReason: 'DataUnreadable', Recoverable: true }));
    await view.click(view.q('#BookmarksTabSettings'));
    await view.click(view.q('#BookmarksTabData'));
    assert.deepEqual(view.q('#BookmarksSnapshots').querySelectorAll('button.bookmarksRestore').map((b) => b.disabled), [false, true, false]);
    assert.deepEqual(view.errors, []);
});

test('the list of deleted users (kit-users.json) that can\'t be read: a banner, its line on Diagnostics, and Start the list empty, confirmed in the kit\'s dialog with the focus on Cancel, posts once and reads Diagnostics again; a refusal in amber', async () => {
    const marks = {
        File: 'C:\\data\\bookmarks\\kit-users.json', Status: 'ReadOnlyDataUnreadable', ReadOnly: true, Recoverable: true, MarksInMemory: 2,
        Message: 'The data file couldn\'t be read and there was no backup. There is no snapshot of it. <b>Nothing</b> was changed.'
    };
    const body = Object.assign(diagnostics([], { Accepted: 0 }), { Users: { DeletedUserPolicy: 'KeepAsOrphan', OrphanUsers: [], Message: null, Banner: null, Marks: marks } });
    const view = await open(body, {}, { kit: true });
    const banner = view.banners.querySelector('.bookmarksMarksBanner');
    assert.equal(banner.textContent, 'The list of users deleted on this server (kit-users.json): ' + marks.Message);
    assert.equal(banner.querySelector('b'), null, 'the server\'s sentence is text');
    assert.equal(view.q('#BookmarksDiagnosticsSummary .bookmarksMarksStatus').textContent, 'Deleted users file: C:\\data\\bookmarks\\kit-users.json (ReadOnlyDataUnreadable); 2 marks kept in memory only, until the server restarts.');

    const posts = [];
    view.handle('POST Bookmarks/Admin/Users/Marks/StartEmpty', (request) => {
        posts.push(request);
        return Promise.resolve({ StartedEmpty: true, MovedAside: ['kit-users.json.corrupt-20261020120000'], MarksKept: 1, Orphans: 1 });
    });
    await view.click(view.q('#BookmarksTabData'));
    const section = view.q('#BookmarksMarksRecovery');
    assert.equal(section.classList.contains('hide'), false);
    assert.match(view.q('#BookmarksMarksRecoveryText').textContent, /^The list of users deleted on this server \(kit-users\.json\) couldn't be read, so Remap by name and an import that matches users by name are refused/);
    assert.match(view.q('#BookmarksMarksRecoveryText').textContent, /the users deleted while it couldn't be read may be lost from it/);

    const reads = view.diagnosticsReads();
    await view.click(view.q('#BookmarksMarksStartEmpty'));
    let dialog = view.q('[role="dialog"]');
    assert.equal(dialog.querySelector('h3').textContent, 'Start the list of deleted users empty?');
    assert.match(dialog.querySelector('.bookmarksDialogText').textContent, /^Start the list of users deleted on this server empty\? What couldn't be read is kept as kit-users\.json\.corrupt-<time>, and the list of users this server doesn't know is rebuilt from the server's users\. Users deleted while the list couldn't be read may be lost from it/);
    assert.ok(view.page.document.activeElement === dialog.querySelector('.bookmarksDialogCancel'), 'the focus starts on Cancel');
    await view.click(dialog.querySelector('.bookmarksDialogCancel'));
    assert.equal(posts.length, 0, 'Cancel sends nothing');

    await view.click(view.q('#BookmarksMarksStartEmpty'));
    dialog = view.q('[role="dialog"]');
    await view.click(dialog.querySelector('.bookmarksMarksStartEmptyConfirm'));
    assert.equal(posts.length, 1);
    assert.equal(posts[0].url, 'http://127.0.0.1:8097/Bookmarks/Admin/Users/Marks/StartEmpty');
    assert.equal(view.q('#BookmarksMarksReport .bookmarksMarksStartedEmpty').textContent, 'The list of deleted users starts empty, and can be changed again. What couldn\'t be read is kept as kit-users.json.corrupt-20261020120000. 1 user deleted since the server started is still on it.');
    assert.ok(view.diagnosticsReads() > reads, 'Diagnostics read again');

    // Refused (a readable copy put back meanwhile, say): the server's sentence, as text, in amber.
    view.handle('POST Bookmarks/Admin/Users/Marks/StartEmpty', () => Promise.reject({ status: 409, json: () => Promise.resolve({ title: 'Conflict', detail: 'A readable data file is in place now (kit-users.json); restart the server to use it. <b>Nothing</b> was changed.' }) }));
    await view.click(view.q('#BookmarksMarksStartEmpty'));
    await view.click(view.q('[role="dialog"] .bookmarksMarksStartEmptyConfirm'));
    assert.equal(view.q('#BookmarksMarksReport .bookmarksRestoreError').textContent, 'A readable data file is in place now (kit-users.json); restart the server to use it. <b>Nothing</b> was changed.');

    // Readable marks: no banner, no section; an older server's answer without Marks is fine.
    for (const next of [Object.assign({}, marks, { Status: 'Ok', ReadOnly: false, Recoverable: false, Message: null, MarksInMemory: null }), undefined]) {
        view.setBody(Object.assign(diagnostics([], { Accepted: 0 }), { Users: { DeletedUserPolicy: 'KeepAsOrphan', OrphanUsers: [], Marks: next } }));
        await view.rerender();
        assert.equal(view.banners.querySelector('.bookmarksMarksBanner'), null);
        assert.equal(view.q('#BookmarksMarksRecovery').classList.contains('hide'), true);
    }

    assert.deepEqual(view.errors, []);
});

test('Run the daily maintenance now queues the task and says where to see it', async () => {
    const view = await open(diagnostics([], { Accepted: 0 }));
    view.handle('POST Bookmarks/Admin/Maintenance/Run', () => Promise.resolve({ Queued: true, Task: 'Bookmarks: Maintenance' }));
    await view.click(view.q('#BookmarksRunMaintenance'));
    assert.equal(view.ajaxCalls[0].url, 'http://127.0.0.1:8097/Bookmarks/Admin/Maintenance/Run');
    assert.match(view.q('#BookmarksMaintenanceStatus').textContent, /^The daily maintenance runs now/);

    view.handle('POST Bookmarks/Admin/Maintenance/Run', () => Promise.reject({ status: 503 }));
    await view.click(view.q('#BookmarksRunMaintenance'));
    assert.equal(view.q('#BookmarksMaintenanceStatus').textContent, 'The maintenance didn\'t start (503).');
    assert.deepEqual(view.errors, []);
});

// ------------------------------------------------------------------ users this server doesn't know

test('users this server doesn\'t know are listed on the Data tab with Remap by name (a dry run first) and the deleted-user policy', async () => {
    const body = diagnostics([], { Accepted: 0 });
    body.Users = {
        DeletedUserPolicy: 'KeepAsOrphan', StoredUsersMismatch: false, Message: '7 records belong to 2 unknown users.', Banner: null,
        OrphanUsers: [{ UserId: 'c3', LastKnownName: '<b>alice</b>', Records: 5, Marked: true }, { UserId: 'd4', LastKnownName: null, Records: 2, Marked: true }]
    };
    const view = await open(body);
    const section = view.q('#BookmarksUnknownUsers');
    assert.ok(!section.classList.contains('hide'));
    const rows = section.querySelectorAll('table.bookmarksUnknownUsersTable tr').slice(1).map((tr) => tr.children.map((c) => c.textContent));
    assert.deepEqual(rows, [['<b>alice</b>', 'c3', '5'], ['(no name known)', 'd4', '2']]);
    assert.equal(section.querySelectorAll('b').length, 0);
    assert.equal(view.q('#BookmarksApplyPolicy').textContent, 'Apply the deleted-user policy (KeepAsOrphan) to them');

    const remaps = [];
    view.handle('POST Bookmarks/Admin/Users/Remap', (request) => {
        remaps.push(request.url);
        const dry = queryOf(request.url).get('dryRun') === 'true';
        return Promise.resolve({ DryRun: dry, Mapped: [{ OldId: 'c3', NewId: 'a1', Name: 'alice', Records: 5 }], Unmatched: [{ OldId: 'd4', Name: null, Records: 2, Reason: 'NoKnownName' }], MovedRecords: dry ? 0 : 5, SafetySnapshots: [] });
    });
    assert.equal(view.q('#BookmarksRemapApply').disabled, true);
    await view.click(view.q('#BookmarksRemapDryRun'));
    assert.deepEqual(remaps, ['http://127.0.0.1:8097/Bookmarks/Admin/Users/Remap?by=Name&dryRun=true']);
    assert.equal(view.q('#BookmarksUsersReport .bookmarksRemapSummary').textContent, 'Dry run, nothing was changed: 1 user would be remapped by name.');
    const report = view.q('#BookmarksUsersReport').querySelectorAll('tr').slice(1).map((tr) => tr.children.map((c) => c.textContent));
    assert.deepEqual(report, [['alice', 'c3', '5', 'Would go to alice (a1)'], ['(no name known)', 'd4', '2', 'Left alone: NoKnownName']]);
    assert.equal(view.q('#BookmarksRemapApply').disabled, false);
    await view.click(view.q('#BookmarksRemapApply'));
    assert.equal(remaps[1], 'http://127.0.0.1:8097/Bookmarks/Admin/Users/Remap?by=Name&dryRun=false');
    assert.equal(view.q('#BookmarksUsersReport .bookmarksRemapSummary').textContent, 'Remapped 1 user by name, 5 records moved.');

    const policies = [];
    view.handle('POST Bookmarks/Admin/Users/ApplyPolicy', (request) => {
        policies.push(request);
        return Promise.resolve({ Policy: 'KeepAsOrphan', Affected: 7, Users: [], SafetySnapshots: [] });
    });
    await view.click(view.q('#BookmarksApplyPolicy'));
    assert.equal(policies.length, 1);
    assert.deepEqual(JSON.parse(policies[0].data), { UserIds: ['c3', 'd4'] });
    assert.match(view.confirms[view.confirms.length - 1].text, /^Treat these 2 users as deleted users/);
    assert.equal(view.q('#BookmarksUsersReport .bookmarksPolicySummary').textContent, 'Applied KeepAsOrphan to 2 users: 7 records affected.');

    // Without unknown users the section is hidden.
    body.Users.OrphanUsers = [];
    await view.rerender();
    assert.ok(section.classList.contains('hide'));
    assert.deepEqual(view.errors, []);
});
