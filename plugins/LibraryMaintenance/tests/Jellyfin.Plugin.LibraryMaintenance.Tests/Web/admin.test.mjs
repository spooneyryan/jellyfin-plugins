// Library Maintenance's Dashboard page in the kit's fake page, with the built AdminScript module
// (WebTests writes it to a file and runs KIT_ADMIN_SCRIPT=<file> node --test admin.test.mjs). Each test imports its own
// copy of the module (a query string on the file URL) into a fresh page, so no kit state is shared. The module runs in
// Node's realm: the page's window, document, fetch and Event become globals for it.
import test from 'node:test';
import assert from 'node:assert/strict';
import { resolveObjectURL } from 'node:buffer';
import { pathToFileURL } from 'node:url';
import { createPage } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_ADMIN_SCRIPT;
const skip = !file ? 'KIT_ADMIN_SCRIPT is not set' : false;
let imports = 0;

const DIAGNOSTICS = '/LibraryMaintenance/Admin/Diagnostics';
const TURNED_OFF = "Duplicates is turned off in Library Maintenance's settings.";
const INDEX = '/LibraryMaintenance/Duplicates/Index';
const REBUILD = '/LibraryMaintenance/Duplicates/Index/Rebuild';

function diagnostics(changes) {
    return Object.assign({
        PluginVersion: '1.0.4.0',
        KitVersion: '1.0.0',
        Abi: { Target: '10.11', Server: '10.11.5', Active: true },
        WebAnchors: { Reported: true, Reports: { Accepted: 0 } },
        ClientHealth: []
    }, changes || {});
}

// GET …/Duplicates/Index's answer: by default an index built once over two libraries.
function index(changes) {
    return Object.assign({
        Version: 1,
        GeneratedUtc: '2026-10-31T14:02:11Z',
        BuildMs: 1834,
        BuiltDuringScan: false,
        Movies: 19,
        Entries: 17,
        MergedEntries: 2,
        BrokenEntries: 0,
        Libraries: [{ Id: 'l1', Name: 'Movies', Kind: 'movies', Movies: 15 }, { Id: 'l2', Name: 'Home videos', Kind: 'mixed', Movies: 4 }],
        Dirty: false,
        Rebuilding: false,
        Progress: null,
        ScanInProgress: false,
        States: { Orphaned: 0, StaleLinks: 0, Chained: 0, Unlisted: 0, ListedNotPointing: 0, Cycle: 0 },
        Skipped: 0,
        Excluded: 0,
        LastError: null,
        LastErrorUtc: null
    }, changes || {});
}

const GROUPS = '/LibraryMaintenance/Duplicates/Groups';
const PREFS = 'jflm:duplicates.view';

function groupsIndex(changes) {
    return Object.assign({ Version: 1, GeneratedUtc: '2026-10-31T14:02:11Z', Stale: false, Rebuilding: false, ScanInProgress: false, BuiltDuringScan: false }, changes || {});
}

function entry(changes) {
    return Object.assign({
        ItemId: '0f00000000000000000000000000000a', Name: 'Heat', OriginalTitle: null, Edition: null, Year: 1995, YearSource: 'ProductionYear',
        Libraries: [{ Id: '11111111-1111-1111-1111-111111111111', Name: 'Movies' }],
        Path: 'C:\\media\\Movies\\Heat (1995)\\Heat (1995).mkv', IsInMixedFolder: false, Width: 1920, Height: 800, ResolutionLabel: '1080p',
        SizeBytes: 31400000000, TotalBitrate: 21000000, RunTimeTicks: 102000000000, Container: 'mkv', VideoType: 'VideoFile', SourceCount: 1,
        LinkedVersionIds: [], LocalAlternateCount: 0, PartCount: 1, VersionState: 'None', ProviderIds: { Tmdb: '949', Imdb: 'tt0113277' },
        DateCreated: '2021-03-02T10:00:00Z', Flags: [], EntryIgnoreState: 'Active', MarkedWith: []
    }, changes || {});
}

function group(n, changes) {
    const keep = entry({ ItemId: n.toString(16).padStart(30, '0') + 'a1', Name: 'Movie ' + n });
    const other = entry({
        ItemId: n.toString(16).padStart(30, '0') + 'b2', Name: 'Movie ' + n, ResolutionLabel: '720p', ProviderIds: { Tmdb: '949', Imdb: null },
        Libraries: [{ Id: '22222222-2222-2222-2222-222222222222', Name: 'Movies 2' }]
    });
    return Object.assign({
        GroupId: n.toString(16).padStart(16, '0'), DisplayTitle: 'Movie ' + n, Key: 'movie ' + n + '|1995', Confidence: 'Confirmed',
        Reasons: ['TitleAndYear', 'SameTmdbId'], Flags: ['CrossLibrary'], Years: [1995], SuggestedKeepId: keep.ItemId,
        ReclaimableBytes: 9876543210, ReclaimableIsLowerBound: false, IgnoreState: 'None', Entries: [keep, other]
    }, changes || {});
}

// GET …/Duplicates/Groups' answer: by default two groups on the first page, Title + Year.
function groupsPage(changes) {
    const items = (changes && changes.Items) || [group(1), group(2)];
    return Object.assign({
        Index: groupsIndex(), Mode: 'TitleAndYear', UnknownYear: 'MatchUnknownOnly', OptionsHash: '9c1e000000000000',
        EffectiveLibraryIds: ['11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222'],
        TotalMovies: 19, TotalEntries: 17, GroupCount: items.length, HiddenByIgnore: 0, AlternateModeGroupCount: 6,
        ReclaimableBytes: 19753086420, ReclaimableIsLowerBound: false, StartIndex: 0, TotalRecordCount: items.length, Items: items
    }, changes || {});
}

// The Settings tab's routes, and GET …/Admin/Settings' answer: by default three libraries (Kids turned off in
// Jellyfin and not read), the six languages and their articles.
const SETTINGS = '/LibraryMaintenance/Admin/Settings';
const EXPORT = '/LibraryMaintenance/Admin/Export';
const IMPORT = '/LibraryMaintenance/Admin/Import';
const SETTINGS_ROUTES = [SETTINGS, EXPORT, IMPORT];
const L1 = '11111111111111111111111111111111';
const L2 = '22222222222222222222222222222222';
const L3 = '33333333333333333333333333333333';
const VERSION = '0123456789abcdef';

function savedSettings(changes) {
    return Object.assign({
        DuplicatesEnabled: true, DefaultMatchMode: 'TitleAndYear', UnknownYearPolicy: 'MatchUnknownOnly', IncludedLibraryIds: [L1, L2],
        ExcludedPathPrefixes: [], StripEditionSuffixes: true, EditionPhrases: ['extended', 'director\'s cut'], ArticleLanguages: ['En'],
        WarmIndexAfterScan: true, RequestWaitSeconds: 5, EnableWebUi: true, MoveEnabled: true, ShowInItemMenu: true, ReportWebAnchorProblems: true
    }, changes || {});
}

function settingsView(settings, changes) {
    return Object.assign({
        Settings: savedSettings(settings),
        Version: VERSION,
        Libraries: [
            { Id: L1, Name: 'Movies', Kind: 'movies', Enabled: true, Included: true, Missing: false },
            { Id: L2, Name: 'Home videos', Kind: 'mixed', Enabled: true, Included: true, Missing: false },
            { Id: L3, Name: 'Kids', Kind: 'movies', Enabled: false, Included: false, Missing: false }
        ],
        ArticlesByLanguage: {
            En: ['the', 'a', 'an'], Fr: ['le', 'la', 'les', 'l\'', 'un', 'une'], De: ['der', 'die', 'das'], Es: ['el', 'la', 'los', 'las'],
            It: ['il', 'lo', 'la'], Nl: ['de', 'het', 'een']
        },
        DefaultEditionPhrases: ['extended', 'unrated', 'director\'s cut', 'imax']
    }, changes || {});
}

// POST …/Admin/Settings' and …/Admin/Import's report (Jellyfin leaves its null Preview and Source out).
function settingsReport(changes) {
    return Object.assign({
        DryRun: true, Applied: false, Version: VERSION, Settings: savedSettings(), Changes: [], ScopeChanged: false, RebuildsIndex: false,
        TitleRulesChanged: false, Warnings: []
    }, changes || {});
}

// A fresh page with the module mounted into the dashboard page's skeleton. With options.flush false, nothing has resolved
// yet when it returns: what mount did synchronously is what the first open shows.
async function start(options) {
    options = options || {};
    const page = createPage();
    globalThis.window = page.context;
    globalThis.document = page.document;
    globalThis.fetch = page.context.fetch;
    globalThis.Event = page.context.Event;
    if (options.tv) {
        page.document.documentElement.classList.add('layout-tv');
    }

    if (options.devicePixelRatio) {
        page.context.devicePixelRatio = options.devicePixelRatio;
    }

    page.installApiClient({ userId: 'admin-1', admin: true });
    if (options.baseUrl) {
        // A server under a base URL: ApiClient.getUrl puts it in every address (the posters must keep it).
        const proto = Object.getPrototypeOf(page.context.ApiClient);
        const getUrl = proto.getUrl;
        proto.getUrl = function (p, q) {
            return getUrl.call(this, p, q).replace('http://127.0.0.1:8097/', 'http://127.0.0.1:8097' + options.baseUrl + '/');
        };
    }

    if (options.failure) {
        page.respond('GET', DIAGNOSTICS, options.failure.body, options.failure.status);
    } else {
        page.respond('GET', DIAGNOSTICS, options.diagnostics || diagnostics());
    }

    if (options.indexFailure) {
        page.respond('GET', INDEX, options.indexFailure.body, options.indexFailure.status);
    } else {
        page.respond('GET', INDEX, options.index || index());
    }

    // The groups: by default a page of the same index version; their requests go to page.groupsLog, so the
    // index's tests count only their own (page.fetchLog).
    const version = (options.index || index()).Version;
    page.respond('GET', GROUPS, options.groups || groupsPage({ Index: groupsIndex({ Version: version }) }), options.groupsStatus);
    page.groupsLog = [];
    // The settings: their requests go to page.settingsLog, as the groups' do.
    if (options.settingsFailure) {
        page.respond('GET', SETTINGS, options.settingsFailure.body, options.settingsFailure.status);
    } else {
        page.respond('GET', SETTINGS, options.settings || settingsView());
    }

    page.settingsLog = [];
    const fetch = page.context.fetch;
    page.context.fetch = (url, init) => {
        // Under a base URL every request must carry it; the scripted answers are the routes without it.
        if (options.baseUrl) {
            const prefix = 'http://127.0.0.1:8097' + options.baseUrl + '/';
            assert.ok(String(url).startsWith(prefix), 'without the base URL: ' + url);
            url = 'http://127.0.0.1:8097/' + String(url).slice(prefix.length);
        }

        const answer = fetch(url, init);
        if (page.fetchLog.length && page.fetchLog[page.fetchLog.length - 1].path === GROUPS) {
            page.groupsLog.push(page.fetchLog.pop());
        } else if (page.fetchLog.length && SETTINGS_ROUTES.indexOf(page.fetchLog[page.fetchLog.length - 1].path) >= 0) {
            page.settingsLog.push(page.fetchLog.pop());
        }

        return answer;
    };
    globalThis.fetch = page.context.fetch;
    if (options.storage) {
        options.storage(page.context.localStorage);
    }

    const module = await import(pathToFileURL(file).href + '?page=' + (++imports));
    const view = page.element('div', { id: 'LibraryMaintenanceConfigPage', 'data-role': 'page' });
    const note = page.element('p', { id: 'LibraryMaintenanceScriptNote' }, ['Loading the page…']);
    const root = page.element('div', { id: 'LibraryMaintenanceRoot' });
    view.append(note, root);
    page.document.body.append(view);
    module.mount(view, options.params || {});
    if (options.flush !== false) {
        await page.flush();
    }

    return { page, view, root, note, module };
}

function tabs(root) {
    return root.querySelectorAll('[role="tab"]');
}

function selected(root) {
    return tabs(root).filter((t) => t.getAttribute('aria-selected') === 'true').map((t) => t.textContent);
}

function visiblePanels(root) {
    return root.querySelectorAll('[role="tabpanel"]').filter((p) => !p.hasAttribute('hidden')).map((p) => p.id);
}

// Rebuild is never disabled (that would drop the focus to the body); aria-disabled says whether it acts.
function off(button) {
    assert.equal(button.disabled, false, 'never disabled');
    return button.getAttribute('aria-disabled') === 'true';
}

test('the first open renders the tabs and asks for the data at once, with no viewshow', { skip }, async () => {
    const { page, root, note } = await start({ flush: false });

    // Synchronously: the tabs, the first one open, the loading note gone, and the request on its way (the first
    // viewshow has already fired when the stub's import() resolves).
    assert.deepEqual(tabs(root).map((t) => t.textContent), ['Duplicates', 'Settings', 'Diagnostics']);
    assert.deepEqual(selected(root), ['Duplicates']);
    assert.deepEqual(visiblePanels(root), ['LibraryMaintenancePanelDuplicates']);
    assert.equal(note.parentNode, null);
    assert.deepEqual(page.fetchLog.map((e) => e.method + ' ' + e.path), ['GET ' + DIAGNOSTICS, 'GET ' + INDEX]);
    assert.equal(page.fetchLog[0].userId, 'admin-1');
    assert.match(page.fetchLog[0].headers.Authorization, /Token="test-token"/);

    await page.flush();
    assert.match(
        page.document.getElementById('LibraryMaintenanceDuplicatesStatus').textContent,
        /^Index: 19 movies in 2 libraries · built .+ \(1\.8 s\) · 17 entries$/);
    const body = page.document.getElementById('LibraryMaintenanceDiagnosticsBody');
    assert.ok(body.textContent.includes('PluginVersion1.0.4.0'), body.textContent);
    assert.ok(body.textContent.includes('Server10.11.5'), body.textContent);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the tab a URL names opens first; an unknown one opens Duplicates', { skip }, async () => {
    const settings = await start({ params: { name: 'LibraryMaintenance', tab: 'settings' } });
    assert.deepEqual(selected(settings.root), ['Settings']);
    assert.deepEqual(visiblePanels(settings.root), ['LibraryMaintenancePanelSettings']);

    const diagnosticsTab = await start({ params: { tab: 'Diagnostics' } });
    assert.deepEqual(selected(diagnosticsTab.root), ['Diagnostics']);

    const unknown = await start({ params: { tab: 'moves' } });
    assert.deepEqual(selected(unknown.root), ['Duplicates']);

    // The page never writes the URL.
    assert.equal(settings.page.context.history.length, 1);
    assert.equal(settings.page.context.location.hash, '#/home');
});

test('the tabs follow the WAI-ARIA keys on a keyboard, and stay reachable on the TV layout', { skip }, async () => {
    const { page, root } = await start();
    const [duplicates, settings, diagnosticsButton] = tabs(root);
    assert.deepEqual(tabs(root).map((t) => t.getAttribute('tabindex')), ['0', '-1', '-1']);

    page.key(duplicates, 'ArrowRight');
    assert.deepEqual(selected(root), ['Settings']);
    assert.equal(page.document.activeElement, settings);
    page.key(settings, 'End');
    assert.deepEqual(selected(root), ['Diagnostics']);
    page.key(diagnosticsButton, 'ArrowRight');
    assert.deepEqual(selected(root), ['Duplicates']);
    page.key(duplicates, 'ArrowLeft');
    assert.deepEqual(selected(root), ['Diagnostics']);
    page.key(diagnosticsButton, 'Home');
    assert.deepEqual(selected(root), ['Duplicates']);
    page.dispatch(settings, 'click');
    assert.deepEqual(selected(root), ['Settings']);
    assert.deepEqual(visiblePanels(root), ['LibraryMaintenancePanelSettings']);

    const tv = await start({ tv: true });
    const tvTabs = tabs(tv.root);
    assert.deepEqual(tvTabs.map((t) => t.getAttribute('tabindex')), ['0', '0', '0']);
    tv.page.key(tvTabs[0], 'ArrowRight');
    assert.deepEqual(selected(tv.root), ['Duplicates'], 'the D-pad moves the focus, not the tab');
});

test('on the TV layout the D-pad finds every panel from the tabs: one focus container, Rebuild first, every panel with controls', { skip }, async () => {
    const { root } = await start({ tv: true });

    // jellyfin-web looks for Down's target inside the nearest focuscontainer-down: the tabs and the panels, never the drawer.
    const app = root.querySelectorAll('.focuscontainer-down');
    assert.equal(app.length, 1);
    assert.ok(app[0].contains(root.querySelector('[role="tablist"]')));
    assert.equal(app[0].querySelectorAll('[role="tabpanel"]').length, 3);

    // Every panel has controls of its own (Settings too), so none takes the focus itself; Settings' first control is
    // Find duplicate movies' switch.
    const panel = (name) => root.querySelector('#LibraryMaintenancePanel' + name);
    for (const name of ['Duplicates', 'Settings', 'Diagnostics']) {
        assert.equal(panel(name).getAttribute('tabindex'), null, name);
        assert.equal(panel(name).classList.contains('focusable'), false, name);
    }

    // (The panel itself is hidden: Duplicates is open.)
    const controls = panel('Settings').querySelectorAll('button, select, textarea, input').filter((c) => [null, panel('Settings')].includes(c.closest('[hidden]')));
    assert.equal(controls[0].id, 'LibraryMaintenanceSettingsDuplicatesEnabled');
    assert.equal(controls[0].getAttribute('role'), 'switch');

    // Rebuild is the first thing in its row, ahead of the status line.
    const row = root.querySelector('#LibraryMaintenanceDuplicatesRebuild').parentNode;
    assert.equal(row.childNodes[0].id, 'LibraryMaintenanceDuplicatesRebuild');
    assert.equal(row.childNodes[1].id, 'LibraryMaintenanceDuplicatesStatus');
});

test('server values are written as text', { skip }, async () => {
    const hostile = '<img src=x onerror=alert(1)>';
    const { page, root } = await start({
        diagnostics: diagnostics({ PluginVersion: hostile, Store: { File: hostile, Nested: { Deeper: { Deepest: [1, 2] } } } }),
        index: index({ LastError: hostile, Libraries: [{ Id: 'l1', Name: hostile, Kind: 'movies', Movies: 3 }] })
    });

    const details = page.document.getElementById('LibraryMaintenanceDuplicatesDetails');
    assert.ok(details.textContent.includes(hostile + '3 movies'), details.textContent);
    assert.ok(page.document.getElementById('LibraryMaintenanceDuplicatesBanners').textContent.includes('The last rebuild failed: ' + hostile));
    // No image from a value: the only ones are the groups' posters, which the page addresses itself.
    root.querySelectorAll('img').forEach((img) => {
        assert.equal(img.getAttribute('class'), 'jflm-page-poster-image');
        assert.match(img.getAttribute('src'), /^http:\/\/127\.0\.0\.1:8097\/Items\/[0-9a-f]{32}\/Images\/Primary\?fillWidth=60&quality=90$/);
    });
    const body = page.document.getElementById('LibraryMaintenanceDiagnosticsBody');
    assert.ok(body.textContent.includes('File' + hostile));
    // Three levels deep, a list is counted, not expanded.
    assert.ok(body.textContent.includes('DeeperDeepest2 entries'), body.textContent);
});

test('a failed load says what the server said, on its own tab', { skip }, async () => {
    const { page, root } = await start({
        failure: { status: 500, body: { title: 'Error', detail: 'The diagnostics could not be read.' } },
        indexFailure: { status: 404, body: { title: 'Not Found', detail: TURNED_OFF } }
    });

    assert.equal(page.document.getElementById('LibraryMaintenanceDuplicatesStatus').textContent, TURNED_OFF);
    assert.equal(off(page.document.getElementById('LibraryMaintenanceDuplicatesRebuild')), true, 'nothing to rebuild while Duplicates is off');
    const alert = root.querySelectorAll('[role="alert"]');
    assert.equal(alert.length, 1);
    assert.equal(alert[0].textContent, 'The diagnostics could not be read.');
    assert.equal(page.document.getElementById('LibraryMaintenanceDiagnosticsBody').contains(alert[0]), true);
});

test('a view jellyfin-web kept loads its data again when shown; Refresh does too; a second mount adds nothing', { skip }, async () => {
    const { page, view, root, module } = await start();
    assert.equal(page.fetchLog.length, 2);

    page.dispatch(view, 'viewshow', { detail: { isRestored: false } });
    await page.flush();
    assert.equal(page.fetchLog.length, 2, 'only a restored view loads again');

    page.dispatch(view, 'viewshow', { detail: { isRestored: true } });
    await page.flush();
    assert.equal(page.fetchLog.length, 4);

    const refresh = root.querySelectorAll('button').filter((b) => b.textContent === 'Refresh')[0];
    page.dispatch(refresh, 'click');
    page.dispatch(refresh, 'click');
    await page.flush();
    assert.equal(page.fetchLog.length, 6, 'one load at a time');

    module.mount(view, {});
    assert.equal(root.querySelectorAll('[role="tablist"]').length, 1);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('before the first build, Rebuild starts one and the tab polls the index every 2 s until it is built', { skip }, async () => {
    const empty = { Version: 0, GeneratedUtc: null, BuildMs: null, Movies: 0, Entries: 0, Libraries: [] };
    const { page } = await start({ index: index(empty) });
    const status = page.document.getElementById('LibraryMaintenanceDuplicatesStatus');
    const rebuild = page.document.getElementById('LibraryMaintenanceDuplicatesRebuild');
    assert.equal(status.textContent, "The index hasn't been built since the server started.");
    assert.equal(off(rebuild), false);
    // Nothing else: no build asked for, no "no libraries" notice before a build, no poll.
    assert.equal(page.document.getElementById('LibraryMaintenanceDuplicatesBanners').childNodes.length, 0);
    await page.advance(10000);
    assert.equal(page.fetchLog.length, 2);

    page.respond('POST', REBUILD, index(Object.assign({}, empty, { Rebuilding: true, Progress: 0.4 })), 202);
    page.respond('GET', INDEX, index(Object.assign({}, empty, { Rebuilding: true, Progress: 0.75 })));
    rebuild.focus();
    page.dispatch(rebuild, 'click');
    assert.equal(off(rebuild), true, 'off at once');
    page.dispatch(rebuild, 'click');
    await page.flush();
    assert.deepEqual(page.fetchLog.slice(2).map((e) => e.method + ' ' + e.path), ['POST ' + REBUILD]);
    assert.equal(page.fetchLog[2].body, undefined);
    assert.equal(status.textContent, 'Indexing movies… 40%');
    assert.equal(off(rebuild), true);

    // A click while it is off does nothing either.
    page.dispatch(rebuild, 'click');
    await page.flush();
    assert.equal(page.fetchLog.length, 3);

    await page.advance(1999);
    assert.equal(page.fetchLog.length, 3);
    await page.advance(1);
    assert.equal(page.fetchLog.length, 4);
    assert.equal(page.fetchLog[3].path, INDEX);
    assert.equal(status.textContent, 'Indexing movies… 75%');

    page.respond('GET', INDEX, index());
    await page.advance(2000);
    assert.equal(page.fetchLog.length, 5);
    assert.match(status.textContent, /^Index: 19 movies in 2 libraries/);
    assert.equal(off(rebuild), false);
    assert.equal(page.document.activeElement, rebuild, 'the focus stayed on Rebuild');

    // Built: the polling stops.
    await page.advance(10000);
    assert.equal(page.fetchLog.length, 5);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a rebuild over an index shows Updating…; a scan, a failed build and broken version links have their own notes', { skip }, async () => {
    const { page } = await start({
        index: index({
            Rebuilding: true,
            Progress: 0.1,
            ScanInProgress: true,
            LastError: 'The database is locked.',
            BrokenEntries: 3,
            States: { Orphaned: 2, StaleLinks: 0, Chained: 0, Unlisted: 1, ListedNotPointing: 0, Cycle: 0 }
        })
    });
    const banners = page.document.getElementById('LibraryMaintenanceDuplicatesBanners');
    assert.deepEqual(
        banners.childNodes.map((n) => n.getAttribute('role') + ':' + n.textContent),
        ['status:Updating…', 'status:Library scan in progress; results may be incomplete.', 'alert:The last rebuild failed: The database is locked.']);
    assert.match(page.document.getElementById('LibraryMaintenanceDuplicatesStatus').textContent, /^Index: 19 movies/);
    assert.equal(off(page.document.getElementById('LibraryMaintenanceDuplicatesRebuild')), true);

    const details = page.document.getElementById('LibraryMaintenanceDuplicatesDetails');
    assert.deepEqual(
        details.querySelectorAll('tr').map((r) => r.textContent),
        ['Orphaned versions2', "Versions their primary doesn't list1", 'Movies15 movies', 'Home videos4 movies (mixed content)']);
    assert.ok(details.textContent.includes('3 entries have a broken version link.'), details.textContent);
    assert.ok(details.textContent.includes("so these numbers can be higher than Jellyfin's own, which shows merged versions once."), details.textContent);

    // A build that finished during a scan keeps the scan note; the others go.
    page.respond('GET', INDEX, index({ BuiltDuringScan: true }));
    await page.advance(2000);
    assert.deepEqual(banners.childNodes.map((n) => n.textContent), ['Library scan in progress; results may be incomplete.']);
    assert.equal(details.querySelectorAll('tr').length, 2);
});

test('a built index with no movie libraries says so; a refused rebuild says why', { skip }, async () => {
    const { page } = await start({ index: index({ Movies: 0, Entries: 0, Libraries: [], BuildMs: 42 }) });
    assert.equal(
        page.document.getElementById('LibraryMaintenanceDuplicatesBanners').textContent,
        'No movie libraries found. Duplicates only checks Movies and mixed-content libraries.');
    assert.match(page.document.getElementById('LibraryMaintenanceDuplicatesStatus').textContent, /^Index: 0 movies in 0 libraries · built .+ \(42 ms\) · 0 entries$/);

    // A failure the server may get over: Rebuild can be tried again.
    page.respond('POST', REBUILD, { title: 'Error', detail: 'The server is busy.' }, 503);
    const rebuild = page.document.getElementById('LibraryMaintenanceDuplicatesRebuild');
    page.dispatch(rebuild, 'click');
    await page.flush();
    assert.equal(page.document.getElementById('LibraryMaintenanceDuplicatesStatus').textContent, 'The server is busy.');
    assert.equal(off(rebuild), false, 'it can be tried again');

    // Duplicates turned off meanwhile: nothing to rebuild until it is on again.
    page.respond('POST', REBUILD, { title: 'Not Found', detail: TURNED_OFF }, 404);
    page.dispatch(rebuild, 'click');
    await page.flush();
    assert.equal(page.document.getElementById('LibraryMaintenanceDuplicatesStatus').textContent, TURNED_OFF);
    assert.equal(off(rebuild), true);
    page.dispatch(rebuild, 'click');
    await page.flush();
    assert.equal(page.fetchLog.filter((e) => e.path === REBUILD).length, 2, 'no POST while off');
    await page.advance(10000);
    assert.equal(page.fetchLog.filter((e) => e.path === INDEX).length, 1, 'no polling after a refusal');
});

test('a build the tab saw running is polled on through a passing failure, and a refusal stops it', { skip }, async () => {
    const { page } = await start({ index: index({ Rebuilding: true, Progress: 0.2 }) });
    const status = page.document.getElementById('LibraryMaintenanceDuplicatesStatus');

    // The server restarts: no answer, then a 503; the tab keeps asking every 2 s.
    page.respond('GET', INDEX, { title: 'Error', detail: 'Service unavailable.' }, 503);
    await page.advance(2000);
    assert.equal(status.textContent, 'Service unavailable.');
    assert.equal(page.fetchLog.filter((e) => e.path === INDEX).length, 2);
    await page.advance(2000);
    assert.equal(page.fetchLog.filter((e) => e.path === INDEX).length, 3);

    page.respond('GET', INDEX, index());
    await page.advance(2000);
    assert.match(status.textContent, /^Index: 19 movies/);
    assert.equal(page.fetchLog.filter((e) => e.path === INDEX).length, 4);
    await page.advance(10000);
    assert.equal(page.fetchLog.filter((e) => e.path === INDEX).length, 4, 'built: no more polls');

    // A refusal (signed out, 401/403) during a build stops the polling at once.
    const second = await start({ index: index({ Rebuilding: true }) });
    second.page.respond('GET', INDEX, { title: 'Forbidden', detail: 'Forbidden.' }, 403);
    await second.page.advance(2000);
    await second.page.advance(10000);
    assert.equal(second.page.fetchLog.filter((e) => e.path === INDEX).length, 2);
});

test('an out-of-date index says so: during a scan the tab waits for the build after it, otherwise Rebuild reads it again', { skip }, async () => {
    // The scan rule: a rebuild asked for during a scan waits for the post-scan build; the tab polls until it is done.
    const { page } = await start({ index: index({ Dirty: true, ScanInProgress: true }) });
    const banners = page.document.getElementById('LibraryMaintenanceDuplicatesBanners');
    assert.deepEqual(
        banners.childNodes.map((n) => n.textContent),
        ['The index is out of date; it is rebuilt when the library scan ends.', 'Library scan in progress; results may be incomplete.']);
    assert.equal(off(page.document.getElementById('LibraryMaintenanceDuplicatesRebuild')), false);
    await page.advance(2000);
    assert.equal(page.fetchLog.filter((e) => e.path === INDEX).length, 2, 'polled while the scan put the build off');

    // The scan ended and the post-scan build runs, then is done: the notes go and the polling stops.
    page.respond('GET', INDEX, index({ Version: 2, Rebuilding: true, Progress: 0.5 }));
    await page.advance(2000);
    assert.deepEqual(banners.childNodes.map((n) => n.textContent), ['Updating…']);
    page.respond('GET', INDEX, index({ Version: 2 }));
    await page.advance(2000);
    assert.equal(banners.childNodes.length, 0);
    await page.advance(10000);
    assert.equal(page.fetchLog.filter((e) => e.path === INDEX).length, 4, 'built: no more polls');

    // Out of date with no scan and no build (the warm-up off, the page unused): a note, and no polling.
    const quiet = await start({ index: index({ Dirty: true }) });
    assert.equal(
        quiet.page.document.getElementById('LibraryMaintenanceDuplicatesBanners').textContent,
        'The index is out of date: the library changed since this build. Rebuild reads it again.');
    await quiet.page.advance(10000);
    assert.equal(quiet.page.fetchLog.filter((e) => e.path === INDEX).length, 1);

    // A passing failure while the scan holds the build off: the tab keeps asking.
    const held = await start({ index: index({ Dirty: true, ScanInProgress: true }) });
    held.page.respond('GET', INDEX, { title: 'Error', detail: 'Service unavailable.' }, 503);
    await held.page.advance(2000);
    await held.page.advance(2000);
    assert.equal(held.page.fetchLog.filter((e) => e.path === INDEX).length, 3);
});

// ----------------------------------------------------------------------------------------------- the groups

const cards = (root) => root.querySelectorAll('#LibraryMaintenanceGroupsList article');
const pressed = (page, id) => page.document.getElementById(id).querySelectorAll('button')
    .filter((b) => b.getAttribute('aria-pressed') === 'true').map((b) => b.textContent);
const groupsState = (page) => page.document.getElementById('LibraryMaintenanceGroupsState');
const summaryOf = (page) => page.document.getElementById('LibraryMaintenanceGroupsSummary').textContent;
const button = (scope, text) => scope.querySelectorAll('button').filter((b) => b.textContent === text)[0];

test('the first open asks for the groups at once and shows the cards: the keep first, chips, facts, Open', { skip }, async () => {
    const { page, root } = await start();
    assert.equal(page.groupsLog.length, 1);
    assert.deepEqual(page.groupsLog[0].query, { startIndex: '0', limit: '50' }, 'no choice made: the settings\' mode and policy');
    assert.equal(summaryOf(page), '2 groups · 18 GB reclaimable');
    assert.deepEqual(pressed(page, 'LibraryMaintenanceGroupsMode'), ['Title + Year']);
    assert.equal(page.document.getElementById('LibraryMaintenanceGroupsModeHint').textContent, 'Same normalised title and same year.');

    const list = cards(root);
    assert.equal(list.length, 2);
    const first = list[0];
    assert.equal(first.getAttribute('data-group-id'), '0000000000000001');
    assert.equal(first.querySelector('h4').textContent, 'Movie 11995');
    assert.deepEqual(first.querySelectorAll('.jflm-page-group-header .jflm-page-chip').map((c) => c.textContent),
        ['Confirmed', 'Title + Year', 'Same TMDb id', 'In 2 libraries']);
    const rows = first.querySelectorAll('li');
    assert.equal(rows.length, 2);
    assert.ok(rows[0].textContent.includes('Suggested keep'), 'the keep first, badged');
    assert.equal(rows[1].textContent.includes('Suggested keep'), false);
    assert.match(rows[0].querySelector('.jflm-page-entry-facts').textContent, /^1995 · Movies · 1080p · .+ · 21\.0 Mbps · 2 h 50 min · mkv · added /);
    assert.equal(rows[0].querySelector('.jflm-page-entry-ids').textContent, 'TMDb 949 · IMDb tt0113277');
    const path = rows[0].querySelector('.jflm-page-entry-path');
    assert.equal(path.getAttribute('title'), path.textContent, 'the whole path in its tooltip');
    assert.deepEqual(path.querySelectorAll('span').map((s) => s.textContent), ['C:\\media\\Movies\\Heat (1995)\\', 'Heat (1995).mkv'], 'the folders give way first');
    const open = rows[0].querySelector('a');
    assert.equal(open.textContent, 'Open');
    assert.match(open.getAttribute('href'), /^#\/details\?id=000000000000000000000000000001a1&serverId=server-1$/);
    assert.equal(open.getAttribute('aria-label'), 'Open Movie 1 (1995), the suggested keep');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('an edition chip names the edition in its usual capitals, not in the server\'s lower case', { skip }, async () => {
    // The third review: the server compares editions in lower case ("extended"); the chip said so too.
    const editions = ['extended', 'director\'s cut', 'imax edition', '3d', 'extended cut'];
    const entries = editions.map((edition, i) => entry({ ItemId: '0000000000000000000000000000ed' + i.toString().padStart(2, '0'), Edition: edition }));
    const { root } = await start({ groups: groupsPage({ Items: [group(1, { Entries: entries, SuggestedKeepId: entries[0].ItemId })] }) });
    assert.deepEqual(root.querySelectorAll('#LibraryMaintenanceGroupsList .jflm-page-chip-edition').map((c) => c.textContent),
        ['Extended', 'Director\'s Cut', 'IMAX Edition', '3D', 'Extended Cut']);
});

test('the mode, the policy, the order and the filters ask again and are kept; the unknown-year option is Title + Year\'s', { skip }, async () => {
    const { page, root } = await start();
    page.respond('GET', GROUPS, groupsPage({ Mode: 'Title', AlternateModeGroupCount: 2 }));
    button(page.document.getElementById('LibraryMaintenanceGroupsMode'), 'Title only').click();
    await page.flush();
    assert.equal(page.groupsLog[1].query.mode, 'Title');
    assert.deepEqual(pressed(page, 'LibraryMaintenanceGroupsMode'), ['Title only']);
    const unknownYear = page.document.getElementById('LibraryMaintenanceGroupsUnknownYear');
    assert.ok(unknownYear.parentNode.classList.contains('hide'), 'no unknown-year option in Title mode');
    assert.deepEqual(JSON.parse(page.context.localStorage.getItem(PREFS)).mode, 'Title');

    button(page.document.getElementById('LibraryMaintenanceGroupsConfidence'), 'Possible').click();
    button(page.document.getElementById('LibraryMaintenanceGroupsConfidence'), 'Conflict').click();
    const sort = page.document.getElementById('LibraryMaintenanceGroupsSort');
    sort.value = 'Reclaimable';
    page.dispatch(sort, 'change');
    await page.flush();
    const last = page.groupsLog[page.groupsLog.length - 1].query;
    assert.equal(last.confidence, 'Possible,Conflict');
    assert.equal(last.sort, 'Reclaimable');
    assert.deepEqual(pressed(page, 'LibraryMaintenanceGroupsConfidence'), ['Possible', 'Conflict']);

    // One library of the two: only it; both again: none named (every library).
    button(page.document.getElementById('LibraryMaintenanceGroupsLibraries'), 'Home videos').click();
    await page.flush();
    assert.equal(page.groupsLog[page.groupsLog.length - 1].query.libraryIds, 'l1');
    button(page.document.getElementById('LibraryMaintenanceGroupsLibraries'), 'Home videos').click();
    await page.flush();
    assert.equal(page.groupsLog[page.groupsLog.length - 1].query.libraryIds, undefined);

    // The text filter isn't kept; the rest is, and the next open starts with it (an unknown order is dropped).
    const kept = JSON.parse(page.context.localStorage.getItem(PREFS));
    assert.deepEqual(kept, { mode: 'Title', unknownYear: null, sort: 'Reclaimable', confidence: ['Possible', 'Conflict'], libraryIds: null });
    const again = await start({ storage: (s) => s.setItem(PREFS, JSON.stringify(Object.assign({}, kept, { unknownYear: 'MatchAny', sort: 'Fuzzy' }))) });
    assert.deepEqual(again.page.groupsLog[0].query, { startIndex: '0', limit: '50', mode: 'Title', unknownYear: 'MatchAny', confidence: 'Possible,Conflict' });
    assert.equal(cards(root).length, 2);
});

test('a storage that throws keeps the defaults, and saving fails quietly', { skip }, async () => {
    const { page } = await start({
        storage: (s) => {
            s.getItem = () => {
                throw new Error('denied');
            };
            s.setItem = () => {
                throw new Error('denied');
            };
        }
    });
    assert.deepEqual(page.groupsLog[0].query, { startIndex: '0', limit: '50' });
    button(page.document.getElementById('LibraryMaintenanceGroupsMode'), 'Title only').click();
    await page.flush();
    assert.equal(page.groupsLog[1].query.mode, 'Title');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Load more asks for the next page, keeps the focus with the new cards, and goes at the end', { skip }, async () => {
    const first = Array.from({ length: 50 }, (_, i) => group(i + 1));
    const { page, root } = await start({ groups: groupsPage({ Items: first, GroupCount: 60, TotalRecordCount: 60 }) });
    const more = page.document.getElementById('LibraryMaintenanceGroupsMore');
    assert.equal(more.classList.contains('hide'), false);
    assert.equal(summaryOf(page), '60 groups · 18 GB reclaimable · showing 50');
    const shownBefore = cards(root);

    page.respond('GET', GROUPS, groupsPage({ Items: Array.from({ length: 10 }, (_, i) => group(i + 51)), GroupCount: 60, TotalRecordCount: 60, StartIndex: 50 }));
    more.focus();
    more.click();
    assert.equal(more.getAttribute('aria-disabled'), 'true', 'off while it loads, never disabled');
    assert.equal(more.disabled, false);
    more.click();
    await page.flush();
    assert.equal(page.groupsLog.length, 2, 'one request');
    assert.equal(page.groupsLog[1].query.startIndex, '50');
    assert.equal(cards(root).length, 60);
    assert.ok(more.classList.contains('hide'), 'no more pages');
    assert.equal(page.document.activeElement, cards(root)[50].querySelector('a'), 'the focus on the first new card');
    // A next page only adds its cards: the 50 shown are the same nodes, so their posters aren't asked again.
    assert.ok(cards(root).slice(0, 50).every((card, i) => card === shownBefore[i] && card.isConnected), 'the first 50 cards kept');
});

test('a failed Load more keeps the cards, Load more and its focus, says why next to it, and asks for that page again', { skip }, async () => {
    // The third review: the failure wiped the 50 cards and Load more, the focus fell to the page's body, and the retry
    // asked for the first page.
    const first = Array.from({ length: 50 }, (_, i) => group(i + 1));
    const { page, root } = await start({ groups: groupsPage({ Items: first, GroupCount: 60, TotalRecordCount: 60 }) });
    const more = page.document.getElementById('LibraryMaintenanceGroupsMore');
    const notice = page.document.getElementById('LibraryMaintenanceGroupsMoreState');
    const shownBefore = cards(root);
    page.respond('GET', GROUPS, { title: 'Internal Server Error', detail: 'boom' }, 500);
    more.focus();
    more.click();
    await page.flush();
    assert.equal(cards(root).length, 50);
    assert.ok(cards(root).every((card, i) => card === shownBefore[i] && card.isConnected), 'the same 50 cards');
    assert.equal(more.classList.contains('hide'), false, 'Load more stays');
    assert.equal(more.getAttribute('aria-disabled'), 'false');
    assert.equal(more.textContent, 'Load more');
    assert.equal(page.document.activeElement, more, 'the focus stays on Load more');
    assert.equal(notice.querySelectorAll('[role="alert"]').length, 1);
    assert.equal(notice.textContent, 'Couldn\'t load more groups: boom');
    assert.equal(more.nextSibling, notice, 'next to Load more');
    assert.equal(groupsState(page).childNodes.length, 0, 'not the list\'s own error');
    assert.equal(summaryOf(page), '60 groups · 18 GB reclaimable · showing 50');

    // A passing failure: after the poll's wait the same page is asked for again, not the first.
    await page.advance(2000);
    assert.deepEqual(page.groupsLog.map((g) => g.query.startIndex), ['0', '50', '50']);
    assert.equal(cards(root).length, 50);
    assert.equal(page.document.activeElement, more);

    // Load more asks for it at once; its answer adds the 10 cards, the notice goes and the focus moves to the first new one.
    page.respond('GET', GROUPS, groupsPage({ Items: Array.from({ length: 10 }, (_, i) => group(i + 51)), GroupCount: 60, TotalRecordCount: 60, StartIndex: 50 }));
    more.click();
    await page.flush();
    assert.deepEqual(page.groupsLog.map((g) => g.query.startIndex), ['0', '50', '50', '50']);
    assert.equal(cards(root).length, 60);
    assert.equal(notice.childNodes.length, 0);
    assert.equal(page.document.activeElement, cards(root)[50].querySelector('a'));
    await page.advance(10000);
    assert.equal(page.groupsLog.length, 4, 'no retry left over');
});

test('the index\'s poll leaves the cards and the focus alone; a newer index asks for the groups again', { skip }, async () => {
    const { page, root } = await start({ index: index({ Rebuilding: true, Progress: 0.5 }) });
    const open = cards(root)[0].querySelector('a');
    open.focus();
    page.respond('GET', INDEX, index({ Rebuilding: true, Progress: 0.7 }));
    await page.advance(2000);
    assert.equal(page.document.activeElement, open, 'the same card: not made again');
    assert.equal(cards(root)[0].querySelector('a'), open, 'the same node');
    assert.ok(open.isConnected);
    assert.equal(page.groupsLog.length, 1);

    page.respond('GET', INDEX, index({ Version: 2 }));
    page.respond('GET', GROUPS, groupsPage({ Index: groupsIndex({ Version: 2 }), Items: [group(7)] }));
    await page.advance(2000);
    assert.equal(page.groupsLog.length, 2, 'the newer index\'s groups');
    assert.equal(cards(root)[0].getAttribute('data-group-id'), '0000000000000007');
});

test('while the first index is built the groups say so and ask again; a newer index than the status line\'s is read', { skip }, async () => {
    const empty = { Version: 0, GeneratedUtc: null, BuildMs: null, Movies: 0, Entries: 0, Libraries: [], Rebuilding: true, Progress: 0.2 };
    const { page, root } = await start({ index: index(empty), groups: { Rebuilding: true, Progress: 0.4, ScanInProgress: false, LastError: null }, groupsStatus: 202 });
    assert.equal(summaryOf(page), 'Indexing movies… 40%');
    assert.equal(cards(root).length, 0);
    page.respond('GET', GROUPS, groupsPage());
    await page.advance(2000);
    assert.equal(page.groupsLog.length, 2);
    assert.equal(cards(root).length, 2);
    // The groups came from version 1 while the status line shows none: the index is read again.
    page.respond('GET', INDEX, index());
    await page.advance(2000);
    assert.match(page.document.getElementById('LibraryMaintenanceDuplicatesStatus').textContent, /^Index: 19 movies/);
});

test('a failed first build is an index error with Retry, which rebuilds', { skip }, async () => {
    const detail = 'The movie index couldn\'t be built (IOException); the server\'s log has the details. Rebuild the index to try again.';
    const { page } = await start({ groups: { title: 'Service Unavailable', detail: detail }, groupsStatus: 503 });
    const state = groupsState(page);
    assert.equal(state.querySelector('[role="alert"]').textContent, 'Index error: ' + detail + 'Retry');
    await page.advance(10000);
    assert.equal(page.groupsLog.length, 1, 'no polling after the error');

    page.respond('POST', REBUILD, index({ Rebuilding: true }), 202);
    page.respond('GET', GROUPS, groupsPage());
    button(state, 'Retry').click();
    await page.flush();
    assert.equal(page.fetchLog.filter((e) => e.method === 'POST' && e.path === REBUILD).length, 1);
    await page.advance(2000);
    assert.equal(page.groupsLog.length, 2);
    assert.equal(state.childNodes.length, 0);
});

test('no groups says where it looked and offers Title only; filters that keep none, and no library, have their own words', { skip }, async () => {
    const none = groupsPage({ Items: [], GroupCount: 0, TotalRecordCount: 0, AlternateModeGroupCount: 3, ReclaimableBytes: 0 });
    const { page } = await start({ groups: none });
    const state = groupsState(page);
    assert.equal(state.querySelector('.jflm-notice-text').textContent, 'No duplicate movies found in 2 libraries (19 movies) using Title + Year.');
    page.respond('GET', GROUPS, groupsPage({ Mode: 'Title' }));
    button(state, 'Try Title only (3 groups)').click();
    await page.flush();
    assert.equal(page.groupsLog[1].query.mode, 'Title');

    const filtered = await start({ groups: groupsPage({ Items: [], GroupCount: 4, TotalRecordCount: 0 }) });
    assert.equal(groupsState(filtered.page).querySelector('.jflm-notice-text').textContent, 'No group matches the filters.');

    // The caller can open none of the libraries Duplicates reads.
    const nothing = await start({ groups: groupsPage({ Items: [], GroupCount: 0, TotalRecordCount: 0, EffectiveLibraryIds: [] }) });
    assert.equal(groupsState(nothing.page).textContent, 'There is no library you can open that Duplicates reads.');
    const chosen = await start({
        groups: groupsPage({ Items: [], GroupCount: 0, TotalRecordCount: 0, EffectiveLibraryIds: [] }),
        storage: (s) => s.setItem(PREFS, JSON.stringify({ libraryIds: ['33333333333333333333333333333333'] }))
    });
    assert.equal(chosen.page.groupsLog[0].query.libraryIds, '33333333333333333333333333333333');
    assert.match(groupsState(chosen.page).textContent, /^None of the chosen libraries can be checked/);
    chosen.page.respond('GET', GROUPS, groupsPage());
    button(groupsState(chosen.page), 'Every library').click();
    await chosen.page.flush();
    assert.equal(chosen.page.groupsLog[1].query.libraryIds, undefined);
});

test('group and entry values are written as text', { skip }, async () => {
    const hostile = '<img src=x onerror=alert(1)>';
    const bad = group(1, {
        DisplayTitle: hostile,
        Entries: [
            entry({ ItemId: '000000000000000000000000000001a1', Name: hostile, Edition: hostile, Path: hostile, Libraries: [{ Id: 'l1', Name: hostile }] }),
            entry({ ItemId: '000000000000000000000000000001b2' })
        ]
    });
    const { root } = await start({ groups: groupsPage({ Items: [bad] }) });
    // The only images are the two posters the page asks for itself, never one from a value.
    assert.deepEqual(root.querySelectorAll('img').map((img) => img.getAttribute('src')), [
        'http://127.0.0.1:8097/Items/000000000000000000000000000001a1/Images/Primary?fillWidth=60&quality=90',
        'http://127.0.0.1:8097/Items/000000000000000000000000000001b2/Images/Primary?fillWidth=60&quality=90'
    ]);
    assert.ok(cards(root)[0].textContent.includes(hostile));
});

test('each entry has a lazy poster at its size through ApiClient.getUrl, over its initials, which a missing image leaves', { skip }, async () => {
    const odd = group(3, {
        Entries: [
            entry({ ItemId: '00000000-0000-0000-0000-0000000003a1', Name: '“Amélie”' }),
            entry({ ItemId: '000000000000000000000000000003b2', Name: '¿Quién mató?' }),
            entry({ ItemId: 'not-an-item-id', Name: '(500) Days of Summer' }),
            entry({ ItemId: '000000000000000000000000000003d4', Name: '' })
        ]
    });
    const { page, root } = await start({ baseUrl: '/jf', devicePixelRatio: 2, groups: groupsPage({ Items: [group(1), odd] }) });
    const rows = root.querySelectorAll('li.jflm-page-entry');
    assert.equal(rows.length, 6);
    const posters = rows.map((row) => row.firstChild);
    posters.forEach((poster) => {
        assert.equal(poster.getAttribute('class').split(' ')[0], 'jflm-page-poster', 'the poster comes first in each entry');
        assert.equal(poster.getAttribute('aria-hidden'), 'true');
    });
    assert.deepEqual(posters.map((p) => p.querySelector('.jflm-page-poster-initials').textContent), ['M1', 'M1', 'A', 'QM', '5D', '?']);

    const images = posters.map((p) => p.querySelector('img'));
    assert.equal(images[4], null, 'no request for an id that isn\'t one');
    assert.ok(posters[4].classList.contains('jflm-page-poster-none'));
    const first = images[0];
    assert.equal(first.getAttribute('src'), 'http://127.0.0.1:8097/jf/Items/000000000000000000000000000001a1/Images/Primary?fillWidth=120&quality=90',
        'the base URL kept, no tag, twice the box\'s width on a 2x screen');
    assert.equal(images[2].getAttribute('src'), 'http://127.0.0.1:8097/jf/Items/000000000000000000000000000003a1/Images/Primary?fillWidth=120&quality=90',
        'the id in one spelling');
    assert.equal(first.getAttribute('alt'), '');
    assert.equal(first.getAttribute('loading'), 'lazy');

    // A movie with no image (a 404) or one that fails: the image goes, the box and its initials stay, nothing else moves.
    const before = rows[0].childNodes.length;
    page.dispatch(first, 'error');
    assert.equal(first.isConnected, false);
    assert.equal(posters[0].querySelector('img'), null);
    assert.ok(posters[0].classList.contains('jflm-page-poster-none'));
    assert.equal(posters[0].querySelector('.jflm-page-poster-initials').textContent, 'M1');
    assert.equal(rows[0].firstChild, posters[0], 'the same box');
    assert.equal(rows[0].childNodes.length, before);
    assert.ok(images[1].isConnected, 'the other posters stay');
    assert.equal(posters[1].classList.contains('jflm-page-poster-none'), false);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('on the TV layout the groups\' controls are buttons and links after Rebuild, none disabled', { skip }, async () => {
    const { page, root } = await start({ tv: true });
    const panel = root.querySelector('#LibraryMaintenancePanelDuplicates');
    const controls = panel.querySelectorAll('button, a[href], select, input');
    assert.equal(controls[0].id, 'LibraryMaintenanceDuplicatesRebuild', 'Rebuild first: the D-pad\'s Down from the tabs');
    assert.deepEqual(controls.slice(1, 3).map((c) => c.textContent), ['Title + Year', 'Title only']);
    assert.equal(controls.filter((c) => c.disabled).length, 0);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the index\'s poll leaves a notice and its button alone', { skip }, async () => {
    const none = groupsPage({ Items: [], GroupCount: 0, TotalRecordCount: 0, AlternateModeGroupCount: 3, ReclaimableBytes: 0 });
    const { page } = await start({ index: index({ Rebuilding: true, Progress: 0.5 }), groups: none });
    const action = button(groupsState(page), 'Try Title only (3 groups)');
    action.focus();
    page.respond('GET', INDEX, index({ Rebuilding: true, Progress: 0.8 }));
    await page.advance(2000);
    assert.equal(page.fetchLog.filter((e) => e.path === INDEX).length, 2, 'the index was polled');
    assert.equal(button(groupsState(page), 'Try Title only (3 groups)'), action, 'the same button: not made again');
    assert.ok(action.isConnected);
    assert.equal(page.document.activeElement, action);
});

// ---------------------------------------------------------------------------------------------- the Settings tab

const byId = (page, id) => page.document.getElementById(id);
const textOf = (page, id) => byId(page, id).textContent;
const posts = (page, path) => page.settingsLog.filter((e) => e.method === 'POST' && e.path === path);
const gets = (page) => page.settingsLog.filter((e) => e.method === 'GET' && e.path === SETTINGS);
const rows = (page, id) => byId(page, id).querySelectorAll('tr').map((r) => r.textContent);
const review = (page) => byId(page, 'LibraryMaintenanceSettingsReview');
const labels = (page, id) => byId(page, id).querySelectorAll('button').map((b) => b.textContent);

function type(page, id, value, event) {
    const field = byId(page, id);
    field.value = value;
    page.dispatch(field, event || 'input');
    return field;
}

// A dry run, then the save: the same report, applied when dryRun=false.
function answer(page, path, changes) {
    page.respond('POST', path, (entry) => settingsReport(Object.assign({}, changes, entry.query.dryRun === 'false' ? { DryRun: false, Applied: true } : {})));
}

const MODE_AND_WAIT = [
    { Field: 'DefaultMatchMode', Label: 'Default match mode', From: 'Title + Year', To: 'Title only' },
    { Field: 'RequestWaitSeconds', Label: 'Wait for a build', From: '5 s', To: '10 s' }
];

test('the Settings tab shows the saved settings: switches, selects that keep a newer value, toggles, a folder or phrase a line', { skip }, async () => {
    const view = settingsView({
        DefaultMatchMode: 'Fuzzy', ExcludedPathPrefixes: ['D:\\Clips', 'E:\\Trailers'], ArticleLanguages: ['En', 'Pt'], WarmIndexAfterScan: false,
        IncludedLibraryIds: [L1, '44444444-4444-4444-4444-444444444444']
    });
    view.Libraries.push({ Id: '44444444444444444444444444444444', Name: '', Kind: '', Enabled: false, Included: true, Missing: true });
    const { page } = await start({ settings: view, params: { tab: 'settings' } });

    assert.deepEqual(page.settingsLog.map((e) => e.method + ' ' + e.path), ['GET ' + SETTINGS]);
    assert.equal(byId(page, 'LibraryMaintenanceSettingsForm').hasAttribute('hidden'), false);
    const enabled = byId(page, 'LibraryMaintenanceSettingsDuplicatesEnabled');
    assert.equal(enabled.getAttribute('role'), 'switch');
    assert.deepEqual([enabled.getAttribute('aria-checked'), enabled.textContent], ['true', 'On']);
    assert.equal(page.document.querySelector('label[for="LibraryMaintenanceSettingsDuplicatesEnabled"]').textContent, 'Find duplicate movies');
    assert.deepEqual([byId(page, 'LibraryMaintenanceSettingsWarm').getAttribute('aria-checked'), textOf(page, 'LibraryMaintenanceSettingsWarm')], ['false', 'Off']);

    // A mode a newer version saved keeps an option of its own, so a save sends it back unchanged.
    const mode = byId(page, 'LibraryMaintenanceSettingsMode');
    assert.equal(mode.value, 'Fuzzy');
    assert.deepEqual(mode.querySelectorAll('option').map((o) => o.textContent), ['Title + Year', 'Title only', 'Fuzzy (set by a newer version)']);
    assert.equal(byId(page, 'LibraryMaintenanceSettingsUnknownYear').value, 'MatchUnknownOnly');

    // The libraries: the server's movie and mixed ones, then an included id no longer on it; ids in either form match.
    assert.deepEqual(labels(page, 'LibraryMaintenanceSettingsLibrariesToggles'),
        ['Movies', 'Home videos (mixed content)', 'Kids (turned off in Jellyfin)', 'A library no longer on this server']);
    assert.deepEqual(pressed(page, 'LibraryMaintenanceSettingsLibrariesToggles'), ['Movies', 'A library no longer on this server']);
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsLibrariesNote'), 'The index reads the libraries chosen, and no others.');
    assert.equal(byId(page, 'LibraryMaintenanceSettingsFolders').value, 'D:\\Clips\nE:\\Trailers');
    assert.equal(byId(page, 'LibraryMaintenanceSettingsFolders').getAttribute('aria-describedby'), 'LibraryMaintenanceSettingsFoldersHint');
    assert.ok(textOf(page, 'LibraryMaintenanceSettingsFoldersHint').includes('If the Bookmarks clip folder is inside a movie library, add it here.'));
    assert.equal(byId(page, 'LibraryMaintenanceSettingsPhrases').value, 'extended\ndirector\'s cut');
    assert.deepEqual(pressed(page, 'LibraryMaintenanceSettingsLanguagesToggles'), ['English (the, a, an)', 'Pt (set by a newer version)']);
    assert.equal(labels(page, 'LibraryMaintenanceSettingsLanguagesToggles').length, 7);
    assert.ok(textOf(page, 'LibraryMaintenanceSettingsLanguagesHint').includes('"La La Land" is compared as "La Land"'));
    assert.equal(byId(page, 'LibraryMaintenanceSettingsWait').value, '5');
    const discard = byId(page, 'LibraryMaintenanceSettingsDiscard');
    assert.equal(off(discard), true, 'nothing to discard');

    // No library chosen reads every one, which the note says; Default phrases fills the phrases; Discard puts it all back.
    button(byId(page, 'LibraryMaintenanceSettingsLibrariesToggles'), 'Movies').click();
    button(byId(page, 'LibraryMaintenanceSettingsLibrariesToggles'), 'A library no longer on this server').click();
    assert.deepEqual(pressed(page, 'LibraryMaintenanceSettingsLibrariesToggles'), []);
    assert.match(textOf(page, 'LibraryMaintenanceSettingsLibrariesNote'), /^None chosen: the index reads every movie and mixed library/);
    byId(page, 'LibraryMaintenanceSettingsDefaultPhrases').click();
    assert.equal(byId(page, 'LibraryMaintenanceSettingsPhrases').value, 'extended\nunrated\ndirector\'s cut\nimax');
    enabled.click();
    assert.deepEqual([enabled.getAttribute('aria-checked'), enabled.textContent], ['false', 'Off']);
    assert.equal(off(discard), false);
    discard.click();
    assert.deepEqual(pressed(page, 'LibraryMaintenanceSettingsLibrariesToggles'), ['Movies', 'A library no longer on this server']);
    assert.equal(byId(page, 'LibraryMaintenanceSettingsPhrases').value, 'extended\ndirector\'s cut');
    assert.equal(enabled.getAttribute('aria-checked'), 'true');
    assert.equal(off(discard), true);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

// The web client's and Move's switches are on the tab (before 1.0.2 they were only in the settings file),
// with the note that Disable on Jellyfin's Plugins page deletes the plugin on 10.11.5, and a save sends them.
test('the Settings tab has the web client\'s and Move\'s switches, says not to use Disable, and saves them', { skip }, async () => {
    const view = settingsView({ EnableWebUi: false, MoveEnabled: true, ShowInItemMenu: false, ReportWebAnchorProblems: true });
    const { page } = await start({ settings: view, params: { tab: 'settings' } });
    const ids = ['LibraryMaintenanceSettingsWebUi', 'LibraryMaintenanceSettingsMove', 'LibraryMaintenanceSettingsItemMenu', 'LibraryMaintenanceSettingsAnchorReports'];
    assert.deepEqual(ids.map((id) => page.document.querySelector('label[for="' + id + '"]').textContent),
        ['Add Library Maintenance to the web client', 'Move media', 'Move in the item menu', 'Report missing web client elements']);
    assert.deepEqual(ids.map((id) => byId(page, id).getAttribute('role')), ['switch', 'switch', 'switch', 'switch']);
    assert.deepEqual(ids.map((id) => byId(page, id).getAttribute('aria-checked')), ['false', 'true', 'false', 'true']);
    assert.deepEqual(ids.map((id) => byId(page, id).getAttribute('aria-describedby')), ids.map((id) => id + 'Hint'));
    assert.match(textOf(page, 'LibraryMaintenanceSettingsWebNote'), /not Disable on Jellyfin's Plugins page: on Jellyfin 10\.11\.5 a disabled plugin is deleted at the next start\./);
    assert.match(textOf(page, 'LibraryMaintenanceSettingsMoveHint'), /^This version only previews a move: nothing is moved\./);

    answer(page, SETTINGS, { Changes: [{ Field: 'EnableWebUi', Label: 'Add Library Maintenance to the web client', From: 'Off', To: 'On' }, { Field: 'MoveEnabled', Label: 'Move media', From: 'On', To: 'Off' }] });
    byId(page, 'LibraryMaintenanceSettingsWebUi').click();
    byId(page, 'LibraryMaintenanceSettingsMove').click();
    assert.equal(off(byId(page, 'LibraryMaintenanceSettingsDiscard')), false);
    page.respond('GET', SETTINGS, settingsView({ EnableWebUi: true, MoveEnabled: false, ShowInItemMenu: false }, { Version: 'fedcba9876543210' }));
    byId(page, 'LibraryMaintenanceSettingsSave').click();
    await page.flush();
    const sent = posts(page, SETTINGS);
    assert.deepEqual(sent.map((e) => e.query.dryRun), ['true', 'false']);
    assert.deepEqual(sent[0].body, savedSettings({ EnableWebUi: true, MoveEnabled: false, ShowInItemMenu: false }));
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsMessage'), 'Saved: Add Library Maintenance to the web client, Move media.');
    assert.deepEqual(ids.map((id) => byId(page, id).getAttribute('aria-checked')), ['true', 'false', 'false', 'true']);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a save is a dry run with the version; a change that leaves the index\'s scope alone is then saved at once', { skip }, async () => {
    const { page } = await start({ params: { tab: 'settings' } });
    answer(page, SETTINGS, { Changes: MODE_AND_WAIT });
    type(page, 'LibraryMaintenanceSettingsMode', 'Title', 'change');
    type(page, 'LibraryMaintenanceSettingsWait', ' 10 ');
    page.respond('GET', SETTINGS, settingsView({ DefaultMatchMode: 'Title', RequestWaitSeconds: 10 }, { Version: 'fedcba9876543210' }));
    const before = page.fetchLog.length;
    byId(page, 'LibraryMaintenanceSettingsSave').click();
    await page.flush();

    const sent = posts(page, SETTINGS);
    assert.deepEqual(sent.map((e) => e.query), [{ version: VERSION, dryRun: 'true' }, { version: VERSION, dryRun: 'false' }]);
    assert.deepEqual(sent[0].body, savedSettings({ DefaultMatchMode: 'Title', RequestWaitSeconds: 10 }));
    assert.deepEqual(sent[1].body, sent[0].body);
    assert.equal(review(page), null, 'no dialog: the scope is the same');
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsMessage'), 'Saved: Default match mode, Wait for a build.');

    // Read again (the new version goes with the next save), and the index and the diagnostics with them.
    assert.equal(gets(page).length, 2);
    assert.equal(page.fetchLog.length, before + 2);
    assert.equal(off(byId(page, 'LibraryMaintenanceSettingsDiscard')), true);

    // The saved settings again: the dry run changes nothing, and nothing is saved.
    page.respond('POST', SETTINGS, settingsReport({ Version: 'fedcba9876543210' }));
    byId(page, 'LibraryMaintenanceSettingsSave').click();
    await page.flush();
    assert.deepEqual(posts(page, SETTINGS).map((e) => e.query.version + ' ' + e.query.dryRun).slice(2), ['fedcba9876543210 true']);
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsMessage'), 'Nothing to save: these are the saved settings.');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a change to the libraries or the folders opens its preview; only the preview\'s button saves, and Cancel saves nothing', { skip }, async () => {
    const { page } = await start({ params: { tab: 'settings' } });
    const changes = [
        { Field: 'IncludedLibraryIds', Label: 'Libraries', From: 'Movies, Home videos', To: 'Movies, Home videos, Kids' },
        { Field: 'ExcludedPathPrefixes', Label: 'Excluded folders', From: 'None', To: 'D:\\Clips' }
    ];
    const preview = {
        Counted: true, MoviesBefore: 19, MoviesAfter: 18, ExcludedBefore: 0, ExcludedAfter: 4,
        Libraries: [
            { Id: L1, Name: 'Movies', Change: 'Stays', MoviesBefore: 15, MoviesAfter: 11 },
            { Id: L2, Name: 'Home videos', Change: 'Stays', MoviesBefore: 4, MoviesAfter: 4 },
            { Id: L3, Name: 'Kids', Change: 'Joins', MoviesAfter: 3 }
        ],
        Folders: [{ Path: 'D:\\Clips', Change: 'Added', Exists: false }]
    };
    answer(page, SETTINGS, { Changes: changes, ScopeChanged: true, RebuildsIndex: true, Preview: preview });
    button(byId(page, 'LibraryMaintenanceSettingsLibrariesToggles'), 'Kids (turned off in Jellyfin)').click();
    type(page, 'LibraryMaintenanceSettingsFolders', '  D:\\Clips  \n\n');
    const save = byId(page, 'LibraryMaintenanceSettingsSave');
    save.click();
    await page.flush();

    assert.equal(posts(page, SETTINGS).length, 1, 'only the dry run');
    assert.deepEqual(posts(page, SETTINGS)[0].body.IncludedLibraryIds, [L1, L2, L3]);
    assert.deepEqual(posts(page, SETTINGS)[0].body.ExcludedPathPrefixes, ['D:\\Clips']);
    const dialog = review(page);
    assert.equal(dialog.getAttribute('role'), 'dialog');
    assert.equal(dialog.querySelector('h3').textContent, 'Save these settings?');
    assert.deepEqual(rows(page, 'LibraryMaintenanceSettingsChanges'), ['LibrariesMovies, Home videos → Movies, Home videos, Kids', 'Excluded foldersNone → D:\\Clips']);
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsPreviewMovies'), 'It holds 19 movies now; with these settings it would hold 18 movies.');
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsPreviewExcluded'), 'The excluded folders would leave out 4 movies (0 now).');
    assert.deepEqual(rows(page, 'LibraryMaintenanceSettingsPreviewLibraries'), ['MoviesStays: 15 → 11 movies', 'Home videosStays: 4 → 4 movies', 'KidsJoins: 3 movies']);
    assert.deepEqual(rows(page, 'LibraryMaintenanceSettingsPreviewFolders'), ['ExcludedD:\\Clips (not found on the server: check the path)']);
    // The focus starts on Cancel, as in kit.confirm; Cancel saves nothing and gives the focus back to Save.
    assert.equal(page.document.activeElement, button(dialog, 'Cancel'));
    button(dialog, 'Cancel').click();
    await page.flush();
    assert.equal(review(page), null);
    assert.equal(posts(page, SETTINGS).length, 1);
    assert.equal(page.document.activeElement, save);

    // Saved from the dialog: the same body with dryRun=false, then everything is read again.
    page.respond('GET', SETTINGS, settingsView({ IncludedLibraryIds: [L1, L2, L3], ExcludedPathPrefixes: ['D:\\Clips'] }, { Version: 'aaaaaaaaaaaaaaaa' }));
    save.click();
    await page.flush();
    button(review(page), 'Save and rebuild the index').click();
    await page.flush();
    assert.equal(review(page), null);
    assert.deepEqual(posts(page, SETTINGS).map((e) => e.query.dryRun), ['true', 'true', 'false']);
    assert.deepEqual(posts(page, SETTINGS)[2].body, posts(page, SETTINGS)[1].body);
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsMessage'),
        'Saved: Libraries, Excluded folders. The index is being built again; the Duplicates tab shows it when it is ready.');
    assert.deepEqual(pressed(page, 'LibraryMaintenanceSettingsLibrariesToggles'), ['Movies', 'Home videos (mixed content)', 'Kids (turned off in Jellyfin)']);
    assert.equal(page.document.activeElement, save);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a preview without counts says why, with the libraries and folders that change', { skip }, async () => {
    const { page } = await start({ params: { tab: 'settings' } });
    const note = 'The movies aren\'t counted: these numbers cover every movie library, and your access is limited to some of them or by a parental rating or tags.';
    answer(page, SETTINGS, {
        Changes: [{ Field: 'IncludedLibraryIds', Label: 'Libraries', From: 'Movies, Home videos', To: 'Movies' }],
        ScopeChanged: true,
        RebuildsIndex: true,
        Preview: {
            Counted: false, Note: note,
            Libraries: [{ Id: L1, Name: 'Movies', Change: 'Stays' }, { Id: L2, Name: 'Home videos', Change: 'Leaves' }],
            Folders: [
                { Path: 'D:\\Old', Change: 'Removed', Exists: true },
                { Path: '\\\\nas\\gone', Change: 'Added', NotChecked: 'Network' },
                { Path: '\\\\?\\UNC\\nas\\old', Change: 'Removed', NotChecked: 'Device' },
                { Path: 'E:\\Slow', Change: 'Added', NotChecked: 'NoAnswer' }
            ]
        }
    });
    button(byId(page, 'LibraryMaintenanceSettingsLibrariesToggles'), 'Home videos (mixed content)').click();
    byId(page, 'LibraryMaintenanceSettingsSave').click();
    await page.flush();

    assert.equal(byId(page, 'LibraryMaintenanceSettingsPreviewMovies'), null);
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsPreviewNote'), note);
    assert.deepEqual(rows(page, 'LibraryMaintenanceSettingsPreviewLibraries'), ['MoviesStays', 'Home videosLeaves']);
    assert.deepEqual(rows(page, 'LibraryMaintenanceSettingsPreviewFolders'),
        ['Excluded no moreD:\\Old (found on the server)',
            'Excluded\\\\nas\\gone (a network folder: not checked, so nothing connects to it before you save)',
            'Excluded no more\\\\?\\UNC\\nas\\old (a device path, not a folder path: not checked)',
            'ExcludedE:\\Slow (not checked: no answer within 5 seconds)']);
});

test('a save over settings saved since offers to reload them; a refused value says why; a wait out of range never leaves the page', { skip }, async () => {
    const { page } = await start({ params: { tab: 'settings' } });
    // Told apart by the 409's code, never its words (the third review): reworded here on purpose.
    const stale = 'Someone else saved first.';
    page.respond('POST', SETTINGS, { title: 'Conflict', detail: stale, code: 'SettingsChanged' }, 409);
    type(page, 'LibraryMaintenanceSettingsMode', 'Title', 'change');
    byId(page, 'LibraryMaintenanceSettingsSave').click();
    await page.flush();
    const message = byId(page, 'LibraryMaintenanceSettingsMessage');
    assert.equal(message.querySelector('[role="alert"]').textContent.startsWith(stale), true);
    assert.equal(off(byId(page, 'LibraryMaintenanceSettingsSave')), false, 'Save works again');

    // Reload: the settings as saved now, the page's change dropped.
    page.respond('GET', SETTINGS, settingsView({ WarmIndexAfterScan: false }, { Version: 'bbbbbbbbbbbbbbbb' }));
    button(message, 'Reload the settings').click();
    await page.flush();
    assert.equal(gets(page).length, 2);
    assert.equal(byId(page, 'LibraryMaintenanceSettingsMode').value, 'TitleAndYear');
    assert.equal(byId(page, 'LibraryMaintenanceSettingsWarm').getAttribute('aria-checked'), 'false');
    assert.equal(message.textContent, '');

    // The server's 400 is its own sentence, with nothing to reload; the next save carries the new version.
    const refused = 'ExcludedPathPrefixes: "Clips" isn\'t a full folder path on this server (like D:\\Clips).';
    page.respond('POST', SETTINGS, { title: 'Bad Request', detail: refused }, 400);
    type(page, 'LibraryMaintenanceSettingsFolders', 'Clips');
    byId(page, 'LibraryMaintenanceSettingsSave').click();
    await page.flush();
    assert.equal(message.textContent, refused);
    assert.equal(button(message, 'Reload the settings'), undefined);
    assert.equal(posts(page, SETTINGS)[1].query.version, 'bbbbbbbbbbbbbbbb');

    // Another 409 (a newer schema's) has nothing to reload, even in the words of the version check.
    const newer = 'The settings were saved since by a newer Library Maintenance version.';
    page.respond('POST', SETTINGS, { title: 'Conflict', detail: newer, code: 'NewerSchema' }, 409);
    type(page, 'LibraryMaintenanceSettingsFolders', 'D:\\Clips');
    byId(page, 'LibraryMaintenanceSettingsSave').click();
    await page.flush();
    assert.equal(message.textContent, newer);
    assert.equal(button(message, 'Reload the settings'), undefined);

    // A wait that isn't a whole number of seconds from 0 to 30 is said before any request, and the field gets the focus.
    for (const value of ['31', '2.5', '', '-1', 'soon']) {
        const wait = type(page, 'LibraryMaintenanceSettingsWait', value);
        byId(page, 'LibraryMaintenanceSettingsSave').click();
        await page.flush();
        assert.equal(message.textContent, 'Wait for a build must be a whole number of seconds from 0 to 30.', value);
        assert.equal(page.document.activeElement, wait, value);
    }

    assert.equal(posts(page, SETTINGS).length, 3);
});

test('Duplicates turned off hides its tab and opens Settings, the arrow keys skip it, and turning it on brings it back', { skip }, async () => {
    const { page, root } = await start({ settings: settingsView({ DuplicatesEnabled: false }) });
    const [duplicates, settings, diagnosticsButton] = tabs(root);
    assert.equal(duplicates.hasAttribute('hidden'), true);
    assert.deepEqual(selected(root), ['Settings']);
    assert.deepEqual(visiblePanels(root), ['LibraryMaintenancePanelSettings']);
    page.key(settings, 'ArrowRight');
    assert.deepEqual(selected(root), ['Diagnostics']);
    page.key(diagnosticsButton, 'ArrowRight');
    assert.deepEqual(selected(root), ['Settings']);
    page.key(settings, 'ArrowLeft');
    assert.deepEqual(selected(root), ['Diagnostics']);
    page.key(diagnosticsButton, 'Home');
    assert.deepEqual(selected(root), ['Settings']);

    answer(page, SETTINGS, { Changes: [{ Field: 'DuplicatesEnabled', Label: 'Find duplicate movies', From: 'Off', To: 'On' }], RebuildsIndex: true });
    page.respond('GET', SETTINGS, settingsView());
    byId(page, 'LibraryMaintenanceSettingsDuplicatesEnabled').click();
    byId(page, 'LibraryMaintenanceSettingsSave').click();
    await page.flush();
    assert.equal(posts(page, SETTINGS)[0].body.DuplicatesEnabled, true);
    assert.equal(duplicates.hasAttribute('hidden'), false);
    assert.deepEqual(selected(root), ['Settings'], 'the tab stays where it is');
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsMessage'), 'Saved: Find duplicate movies. The index is being built again; the Duplicates tab shows it when it is ready.');

    // Turned off from the tab: it says where Duplicates went.
    answer(page, SETTINGS, { Changes: [{ Field: 'DuplicatesEnabled', Label: 'Find duplicate movies', From: 'On', To: 'Off' }], Settings: savedSettings({ DuplicatesEnabled: false }) });
    page.respond('GET', SETTINGS, settingsView({ DuplicatesEnabled: false }));
    byId(page, 'LibraryMaintenanceSettingsDuplicatesEnabled').click();
    byId(page, 'LibraryMaintenanceSettingsSave').click();
    await page.flush();
    assert.equal(duplicates.hasAttribute('hidden'), true);
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsMessage'), 'Saved: Find duplicate movies. The Duplicates tab is hidden until finding duplicate movies is on again.');
});

test('Export saves the server\'s file under its name and says what it holds; a failed export says why', { skip }, async () => {
    const { page } = await start({ params: { tab: 'settings' } });
    page.respond('GET', EXPORT, { title: 'Forbidden', detail: 'Only administrators can export the settings.' }, 403);
    const exportButton = byId(page, 'LibraryMaintenanceSettingsExport');
    exportButton.click();
    await page.flush();
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsTransfer'), 'Only administrators can export the settings.');

    const name = 'librarymaintenance-export-20261101-120000.json';
    const file = JSON.stringify({ Format: 'jellyfin-plugin-export/1', Settings: savedSettings(), Data: {} });
    const fetch = globalThis.fetch;
    page.context.fetch = globalThis.fetch = (url, init) => {
        if (new URL(String(url)).pathname !== EXPORT) {
            return fetch(url, init);
        }

        page.settingsLog.push({ method: init.method, path: EXPORT, headers: init.headers });
        return Promise.resolve({
            ok: true,
            status: 200,
            headers: { get: (h) => (h.toLowerCase() === 'content-disposition' ? 'attachment; filename=' + name + '; filename*=UTF-8\'\'' + name : 'application/json; charset=utf-8') },
            text: () => Promise.resolve(file)
        });
    };
    const downloads = [];
    page.document.addEventListener('click', (e) => {
        if (e.target.tagName === 'A') {
            downloads.push({ href: e.target.getAttribute('href'), name: e.target.getAttribute('download') });
        }
    });
    exportButton.click();
    await page.flush();

    assert.equal(page.settingsLog.filter((e) => e.path === EXPORT).length, 2);
    assert.match(page.settingsLog[page.settingsLog.length - 1].headers.Authorization, /Token="test-token"/);
    assert.deepEqual(downloads.map((d) => d.name), [name]);
    assert.equal(await resolveObjectURL(downloads[0].href).text(), file);
    assert.equal(page.document.querySelectorAll('a[download]').length, 0, 'the link is gone');
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsTransfer'), 'Exported as ' + name + '. It holds the excluded folders\' paths: keep it where only administrators can read it.');
    await page.advance(10000);
    assert.equal(resolveObjectURL(downloads[0].href), undefined, 'the file\'s address is let go');
});

test('an import reads the file and shows its dry run (where it came from, what changes, what is left out); its dialog imports', { skip }, async () => {
    const { page } = await start({ params: { tab: 'settings' } });
    const envelope = { Format: 'jellyfin-plugin-export/1', Plugin: 'Library Maintenance', PluginVersion: '1.0.4.0', SchemaVersion: 1, Settings: savedSettings({ RequestWaitSeconds: 9 }), Data: {} };
    const warning = 'The file is from another server: its libraries and folders are this server\'s only where their ids and paths are the same.';
    answer(page, IMPORT, {
        Changes: [{ Field: 'RequestWaitSeconds', Label: 'Wait for a build', From: '5 s', To: '9 s' }],
        Source: { PluginVersion: '1.0.4.0', ExportedUtc: '2026-11-01T12:00:00Z', SameServer: false },
        Warnings: [warning]
    });
    const input = byId(page, 'LibraryMaintenanceSettingsImportFile');
    let opened = 0;
    input.addEventListener('click', () => opened++);
    const importButton = byId(page, 'LibraryMaintenanceSettingsImport');
    importButton.click();
    assert.equal(opened, 1, 'the button opens the file picker');
    assert.equal(input.getAttribute('tabindex'), '-1');

    // A change on the page that isn't saved is dropped by the import, which the dialog says.
    type(page, 'LibraryMaintenanceSettingsWait', '7');
    input.files = [{ name: 'settings.json', text: () => Promise.resolve(JSON.stringify(envelope)) }];
    page.dispatch(input, 'change');
    await page.flush();

    assert.deepEqual(posts(page, IMPORT).map((e) => e.query), [{ version: VERSION, dryRun: 'true' }]);
    assert.deepEqual(posts(page, IMPORT)[0].body, envelope);
    const dialog = review(page);
    assert.equal(dialog.querySelector('h3').textContent, 'Import these settings?');
    assert.match(textOf(page, 'LibraryMaintenanceSettingsSource'), /^The file holds the settings of Library Maintenance 1\.0\.4\.0, exported .+ on another server\.$/);
    assert.ok(dialog.textContent.includes('The changes made on this page and not saved are dropped.'));
    assert.deepEqual(rows(page, 'LibraryMaintenanceSettingsChanges'), ['Wait for a build5 s → 9 s']);
    assert.deepEqual(byId(page, 'LibraryMaintenanceSettingsWarnings').querySelectorAll('li').map((li) => li.textContent), [warning]);

    page.respond('GET', SETTINGS, settingsView({ RequestWaitSeconds: 9 }, { Version: 'cccccccccccccccc' }));
    button(dialog, 'Import').click();
    await page.flush();
    assert.deepEqual(posts(page, IMPORT).map((e) => e.query.dryRun), ['true', 'false']);
    assert.deepEqual(posts(page, IMPORT)[1].body, envelope);
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsTransfer'), 'Imported: Wait for a build.');
    assert.equal(byId(page, 'LibraryMaintenanceSettingsWait').value, '9');
    assert.equal(page.document.activeElement, importButton);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('an import of a file that isn\'t JSON, that changes nothing, or that the server refuses says so, with no dialog', { skip }, async () => {
    const { page } = await start({ params: { tab: 'settings' } });
    const input = byId(page, 'LibraryMaintenanceSettingsImportFile');
    const choose = (text) => {
        input.files = [{ name: 'f.json', text: () => Promise.resolve(text) }];
        page.dispatch(input, 'change');
        return page.flush();
    };

    await choose('not json');
    assert.equal(posts(page, IMPORT).length, 0);
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsTransfer'), 'The file isn\'t JSON: choose a file exported from Library Maintenance.');

    const kept = 'DefaultMatchMode "Fuzzy" isn\'t one this version knows (TitleAndYear, Title); the saved "TitleAndYear" is kept.';
    page.respond('POST', IMPORT, settingsReport({ Warnings: [kept] }));
    await choose('{}');
    assert.equal(review(page), null);
    assert.deepEqual(byId(page, 'LibraryMaintenanceSettingsTransfer').querySelectorAll('li').map((li) => li.textContent), [kept]);
    assert.ok(textOf(page, 'LibraryMaintenanceSettingsTransfer').startsWith('The file\'s settings are the ones saved here: there is nothing to import.'));

    page.respond('POST', IMPORT, { title: 'Bad Request', detail: 'This export is from Bookmarks (d0ee1b2a-7f71-4654-8ff2-163a81441b03), not Library Maintenance.' }, 400);
    await choose('{}');
    assert.equal(review(page), null);
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsTransfer'), 'This export is from Bookmarks (d0ee1b2a-7f71-4654-8ff2-163a81441b03), not Library Maintenance.');
    assert.equal(posts(page, IMPORT).length, 2);
});

test('the settings and their reports are written as text', { skip }, async () => {
    const hostile = '<img src=x onerror=alert(1)>';
    const view = settingsView({ ExcludedPathPrefixes: [hostile], EditionPhrases: [hostile], DefaultMatchMode: hostile, ArticleLanguages: [hostile] });
    view.Libraries[0].Name = hostile;
    const { page } = await start({ settings: view, params: { tab: 'settings' } });
    answer(page, SETTINGS, {
        Changes: [{ Field: hostile, Label: hostile, From: hostile, To: hostile }],
        ScopeChanged: true,
        Preview: { Counted: true, MoviesAfter: 1, Note: hostile, Libraries: [{ Id: L1, Name: hostile, Change: 'Joins', MoviesAfter: 1 }], Folders: [{ Path: hostile, Change: 'Added' }] },
        Warnings: [hostile]
    });
    assert.equal(byId(page, 'LibraryMaintenanceSettingsFolders').value, hostile);
    assert.equal(labels(page, 'LibraryMaintenanceSettingsLibrariesToggles')[0], hostile);
    assert.ok(labels(page, 'LibraryMaintenanceSettingsLanguagesToggles').includes(hostile + ' (set by a newer version)'));
    byId(page, 'LibraryMaintenanceSettingsSave').click();
    await page.flush();

    const dialog = review(page);
    assert.ok(dialog.textContent.includes(hostile + hostile + ' → ' + hostile));
    assert.equal(textOf(page, 'LibraryMaintenanceSettingsPreviewNote'), hostile);
    assert.equal(dialog.querySelectorAll('img').length, 0);
    assert.equal(byId(page, 'LibraryMaintenanceSettingsForm').querySelectorAll('img').length, 0);
});

test('the Settings tab says a failed load with Retry, keeps Export, and reads the settings again on a restored view unless changed', { skip }, async () => {
    const { page, view } = await start({ settingsFailure: { status: 500, body: { detail: 'The settings could not be read.' } }, params: { tab: 'settings' } });
    assert.equal(byId(page, 'LibraryMaintenanceSettingsForm').hasAttribute('hidden'), true);
    const message = byId(page, 'LibraryMaintenanceSettingsMessage');
    assert.equal(message.querySelector('[role="alert"]').textContent.startsWith('The settings could not be read.'), true);
    assert.equal(off(byId(page, 'LibraryMaintenanceSettingsSave')), true);
    assert.equal(off(byId(page, 'LibraryMaintenanceSettingsImport')), true);
    assert.equal(off(byId(page, 'LibraryMaintenanceSettingsExport')), false);

    page.respond('GET', SETTINGS, settingsView());
    button(message, 'Retry').click();
    await page.flush();
    assert.equal(byId(page, 'LibraryMaintenanceSettingsForm').hasAttribute('hidden'), false);
    assert.equal(message.textContent, '');
    assert.equal(off(byId(page, 'LibraryMaintenanceSettingsImport')), false);

    page.dispatch(view, 'viewshow', { detail: { isRestored: true } });
    await page.flush();
    assert.equal(gets(page).length, 3);
    type(page, 'LibraryMaintenanceSettingsWait', '12');
    page.dispatch(view, 'viewshow', { detail: { isRestored: true } });
    await page.flush();
    assert.equal(gets(page).length, 3, 'the form has changes: not read again');
    assert.equal(byId(page, 'LibraryMaintenanceSettingsWait').value, '12');
});

test('on the TV layout every Settings control is a button, a select or a text field, none disabled, and a toggle says it is pressed', { skip }, async () => {
    const { page, root } = await start({ tv: true, params: { tab: 'settings' } });
    const panel = root.querySelector('#LibraryMaintenancePanelSettings');
    const controls = panel.querySelectorAll('button, select, textarea, input').filter((c) => !c.closest('[hidden]'));
    assert.ok(controls.length >= 15, String(controls.length));
    controls.forEach((c) => assert.equal(c.disabled, false, c.id || c.textContent));
    assert.deepEqual(panel.querySelectorAll('[role="switch"]').map((s) => s.id),
        ['LibraryMaintenanceSettingsDuplicatesEnabled', 'LibraryMaintenanceSettingsStripEditions', 'LibraryMaintenanceSettingsWarm',
            'LibraryMaintenanceSettingsWebUi', 'LibraryMaintenanceSettingsMove', 'LibraryMaintenanceSettingsItemMenu', 'LibraryMaintenanceSettingsAnchorReports']);
    byId(page, 'LibraryMaintenanceSettingsLibrariesToggles').querySelectorAll('button').forEach((b) => assert.ok(b.getAttribute('aria-pressed') !== null));
    assert.equal(byId(page, 'LibraryMaintenanceSettingsLibrariesToggles').getAttribute('role'), 'group');
    // The file input is reached through its button only.
    assert.equal(byId(page, 'LibraryMaintenanceSettingsImportFile').closest('[hidden]') !== null, true);
});

test('Diagnostics shows the index\'s numbers: its entries, each version state, the movies skipped and left out', { skip }, async () => {
    const states = { Orphaned: 1, StaleLinks: 2, Chained: 0, Unlisted: 0, ListedNotPointing: 0, Cycle: 3 };
    const { page } = await start({ index: index({ States: states, BrokenEntries: 4, Skipped: 5, Excluded: 6 }), params: { tab: 'diagnostics' } });
    const shown = rows(page, 'LibraryMaintenanceDiagnosticsIndex');
    assert.deepEqual(shown.slice(0, -1), [
        'Movies19 in 2 libraries', 'Entries17', 'Entries with more than one version2', 'Entries with a broken version link4',
        'Orphaned versions1', 'Links to missing items2', 'Chained versions0', 'Versions their primary doesn\'t list0',
        'Listed versions that point elsewhere0', 'Version cycles3', 'Skipped: no name5', 'Left out by the excluded folders6'
    ]);
    assert.match(shown[shown.length - 1], /^Built.+ in 1\.8 s$/);

    // Duplicates turned off: the index's own sentence, beside the plugin's tables.
    const offPage = await start({ indexFailure: { status: 404, body: { title: 'Not Found', detail: TURNED_OFF } }, params: { tab: 'diagnostics' } });
    const summary = offPage.page.document.getElementById('LibraryMaintenanceDiagnosticsSummary');
    assert.ok(summary.textContent.includes(TURNED_OFF));
    assert.equal(summary.querySelectorAll('[role="alert"]').length, 0);
});
