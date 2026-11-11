// Unit tests of kit.anchors and kit.health in kit/web/kit.js,
// in the fake page of fake-dom.mjs. Run with
//   node --test kit/web/tests
import fs from 'node:fs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { anchorDeclarations, createPage, loadKit, plain, KIT_ANCHORS_JSON } from './fake-dom.mjs';

const BUILD = { plugin: 'Kit Test', prefix: 'jfkt', route: 'Fx', version: '1.2.3.0', kit: '0.1.0', mvid: '0123abcd' };
// Kit.Tests' fixture anchors (kit/tests/Kit.Tests/Web/anchors.json).
const PLUGIN_ANCHORS = JSON.parse(fs.readFileSync(new URL('../../tests/Kit.Tests/Web/anchors.json', import.meta.url), 'utf8')).anchors;
const HEALTH = '/Fx/Diagnostics/ClientHealth';
const TEN_MINUTES = 10 * 60 * 1000;

function setup(options) {
    options = options || {};
    const page = createPage();
    if (options.nativeShell) {
        page.context.NativeShell = { AppHost: { appName: () => 'Jellyfin Media Player', appVersion: () => '1.12.0' } };
    }

    page.respond('POST', HEALTH, null, 204);
    const exports = loadKit(page);
    const kit = exports.createKit(BUILD, '', { anchors: anchorDeclarations(PLUGIN_ANCHORS) });
    kit.start();
    return { page, kit, exports };
}

async function signedIn(options) {
    const s = setup(options);
    s.page.installApiClient({ userId: 'user-1' });
    if (options && options.nativeShell) {
        s.page.context.ApiClient.appName = () => 'Jellyfin Media Player';
        s.page.context.ApiClient.appVersion = () => '1.12.0';
    }

    await s.page.advance(60);
    return s;
}

function posts(page) {
    return page.fetchLog.filter((f) => f.method === 'POST' && f.path === HEALTH);
}

function fixtureView(page, withList) {
    const view = page.element('div', { id: 'fixtureView' }, withList ? [page.element('div', { class: 'fixtureList' })] : []);
    page.document.body.append(view);
    return view;
}

// ---------------------------------------------------------------------------------------------------- anchors

test('find tries the primary selector, then the fallbacks; all and match follow the same order', async () => {
    const { kit, page } = await signedIn();
    assert.equal(kit.anchors.find('fixtureList'), null);
    assert.equal(kit.anchors.all('fixtureList').length, 0);
    assert.equal(kit.anchors.match('fixtureList'), -1);

    const fallback = page.element('div', { id: 'fixtureListFallback' });
    page.document.body.append(fallback);
    assert.equal(kit.anchors.find('fixtureList'), fallback);
    assert.equal(kit.anchors.match('fixtureList'), 1);

    const a = page.element('div', { class: 'fixtureList' });
    const b = page.element('div', { class: 'fixtureList' });
    const scope = page.element('section', {}, [b]);
    page.document.body.append(a, scope);
    assert.equal(kit.anchors.find('fixtureList'), a, 'the primary wins over the fallback');
    assert.equal(kit.anchors.match('fixtureList'), 0);
    assert.deepEqual([...kit.anchors.all('fixtureList')], [a, b]);
    assert.equal(kit.anchors.find('fixtureList', scope), b, 'inside a scope');
    assert.equal(page.warnings.length, 0);
});

test('the declarations: the kit\'s own and the plugin\'s, frozen; an undeclared name is reported once and finds nothing', async () => {
    const { kit, page } = await signedIn();
    assert.deepEqual(plain(kit.anchors.names()), ['kit.f1Host', 'kit.pluginPagesHost', 'kit.drawer', 'kit.userMenu', 'kit.nativeDialog', 'kit.actionSheet', 'kit.actionSheetRows', 'kit.itemMenuButton', 'kit.detailsMoreButton', 'kit.detailsVersion', 'kit.selectionMenuButton', 'kit.selectionCheckbox', 'kit.selectionClose', 'fixtureList', 'fixtureButton']);
    const list = kit.anchors.get('fixtureList');
    assert.deepEqual(plain(list), { name: 'fixtureList', css: ['.fixtureList', '#fixtureListFallback'], required: true, views: ['fixture'] });
    assert.ok(Object.isFrozen(list) && Object.isFrozen(list.css));
    assert.equal(kit.anchors.get('nope'), null);
    assert.equal(kit.anchors.find('nope'), null);
    assert.equal(kit.anchors.find('nope'), null);
    assert.equal(kit.anchors.all('nope').length, 0);
    assert.equal(page.warnings.filter((w) => w.includes('"nope" is not declared')).length, 1);
});

test('the kit\'s anchors.json names the same DOM anchors as KIT_ANCHORS', () => {
    const page = createPage();
    const { KIT_ANCHORS } = loadKit(page);
    const S = KIT_ANCHORS.actionSheet;
    const T = KIT_ANCHORS.menuTriggers;
    const declared = Object.fromEntries(JSON.parse(fs.readFileSync(KIT_ANCHORS_JSON, 'utf8')).anchors.map((a) => [a.name, a.css]));
    assert.deepEqual(declared, {
        'kit.f1Host': ['#' + KIT_ANCHORS.fallbackPage.id],
        'kit.pluginPagesHost': ['.' + KIT_ANCHORS.pluginPagesHost.pageClass],
        'kit.drawer': ['.' + KIT_ANCHORS.drawer.container],
        'kit.userMenu': ['.' + KIT_ANCHORS.drawer.userMenu],
        'kit.nativeDialog': ['.' + KIT_ANCHORS.nativeDialog.container],
        [S.health]: ['.' + S.container + ' > .' + S.sheet],
        [S.healthRows]: ['.' + S.sheet + ' .' + S.scroller + ' .' + S.item + '[' + S.commandAttr + ']'],
        [T.health.itemMenu]: ['.' + T.itemAction + '[' + T.actionAttr + '="' + T.menuAction + '"]'],
        [T.health.detailsMore]: ['.' + T.detailsPage + ' .' + T.detailsMore],
        [T.health.detailsVersion]: ['.' + T.detailsPage + ' .' + T.detailsVersion],
        [T.health.selectionMenu]: ['.' + T.selectionPanel + ' .' + T.selectionMenu],
        [T.health.selectionCheckbox]: ['.' + T.selectionItem + ' .' + T.selectionCheckbox],
        [T.health.selectionClose]: ['.' + T.selectionPanel + ' .' + T.selectionClose]
    });
    assert.equal(KIT_ANCHORS.actionSheet.container, KIT_ANCHORS.nativeDialog.container);
});

// ---------------------------------------------------------------------------------------------------- expect

test('an anchor that is there: no warning and no report', async () => {
    const { kit, page } = await signedIn();
    fixtureView(page, true);
    assert.equal(await kit.health.expect(['fixtureList'], 'fixture'), true);
    await page.advance(TEN_MINUTES);
    assert.deepEqual(plain(kit.health.missing()), []);
    assert.equal(posts(page).length, 0);
    assert.equal(page.warnings.length, 0);
});

test('a missing anchor: one console warning in the kit\'s words, then one report with the health report\'s fields', async () => {
    const { kit, page } = await signedIn();
    fixtureView(page, false);
    const result = kit.health.expect(['fixtureList', 'fixtureButton'], 'fixture');
    await page.advance(3999);
    assert.equal(page.warnings.length, 0, 'not before the grace period ends');
    await page.advance(1);
    assert.equal(await result, false);
    assert.deepEqual(page.warnings, [
        '[jfkt] web anchor "fixtureList" missing on fixture (web 10.11.5)',
        '[jfkt] web anchor "fixtureButton" missing on fixture (web 10.11.5)'
    ]);
    assert.equal(posts(page).length, 0, 'misses of one view go out together');
    await page.advance(2000);
    const sent = posts(page);
    assert.equal(sent.length, 1);
    assert.equal(sent[0].userId, 'user-1');
    assert.match(sent[0].headers.Authorization, /Token="test-token"/);
    assert.deepEqual(sent[0].body, {
        WebVersion: '10.11.5',
        Client: 'Jellyfin Web',
        ClientVersion: '10.11.5',
        Layout: 'Desktop',
        Experimental: false,
        PluginVersion: '1.2.3.0',
        Missing: [{ Anchor: 'fixtureList', View: 'fixture', Count: 1 }, { Anchor: 'fixtureButton', View: 'fixture', Count: 1 }]
    });
    assert.deepEqual(plain(kit.health.missing()), [{ anchor: 'fixtureList', view: 'fixture', count: 1 }, { anchor: 'fixtureButton', view: 'fixture', count: 1 }]);
    assert.equal(kit.health.reports(), 1);
    assert.equal(page.errors.length, 0);
});

test('an anchor that renders within the grace period, or a route left before it ends, is no miss', async () => {
    const { kit, page } = await signedIn();
    const view = fixtureView(page, false);
    const late = kit.health.expect(['fixtureList'], 'fixture');
    await page.advance(1500);
    view.append(page.element('div', { class: 'fixtureList' }));
    assert.equal(await (page.advance(3000).then(() => late)), true);

    view.remove();
    const left = kit.health.expect(['fixtureList'], 'fixture');
    await page.advance(1000);
    page.context.history.pushState(null, '', '#/elsewhere');
    await page.advance(4000);
    assert.equal(await left, true);
    assert.deepEqual(plain(kit.health.missing()), []);
    assert.equal(page.warnings.length, 0);
});

test('one miss per visit of a route; later visits count up, warn no more, and wait ten minutes to report', async () => {
    const { kit, page } = await signedIn();
    fixtureView(page, false);
    page.context.location.hash = '#/fixture?id=1';
    kit.health.expect(['fixtureList'], 'fixture');
    await page.advance(6000);
    kit.health.expect(['fixtureList'], 'fixture');
    kit.health.expect(['fixtureList'], 'fixture');
    await page.advance(6000);
    assert.equal(kit.health.missing()[0].count, 1, 'the same visit (scanner or viewshow again) counts once');
    assert.equal(posts(page).length, 1);

    page.context.location.hash = '#/fixture?id=2';
    kit.health.expect(['fixtureList'], 'fixture');
    await page.advance(6000);
    page.context.location.hash = '#/fixture?id=3';
    kit.health.expect(['fixtureList'], 'fixture');
    await page.advance(6000);
    assert.equal(kit.health.missing()[0].count, 3);
    assert.equal(page.warnings.length, 1, 'the console warns once per page load');
    assert.equal(posts(page).length, 1, 'at most one report every ten minutes');

    await page.advance(TEN_MINUTES);
    const sent = posts(page);
    assert.equal(sent.length, 2);
    assert.deepEqual(plain(sent[1].body.Missing), [{ Anchor: 'fixtureList', View: 'fixture', Count: 2 }], 'only what the first report didn\'t carry');
    await page.advance(TEN_MINUTES);
    assert.equal(posts(page).length, 2, 'nothing new, nothing sent');
});

test('the ten minutes hold for the whole browser: a report another tab sent five minutes ago delays this one', async () => {
    const { kit, page } = await signedIn();
    page.context.localStorage.setItem('jfkt:health:lastReport', JSON.stringify(page.context.Date.now() - 5 * 60 * 1000));
    fixtureView(page, false);
    kit.health.expect(['fixtureList'], 'fixture');
    await page.advance(4 * 60 * 1000);
    assert.equal(posts(page).length, 0);
    await page.advance(60 * 1000 + 100);
    assert.equal(posts(page).length, 1);
    assert.ok(Number(page.context.localStorage.getItem('jfkt:health:lastReport')) >= page.context.Date.now() - 1000, 'the time of this report is kept');
});

test('no report before the signed-in user: it goes out once the user is known', async () => {
    const { kit, page } = setup();
    page.installApiClient({ userId: null });
    await page.advance(60);
    fixtureView(page, false);
    kit.health.expect(['fixtureList'], 'fixture');
    await page.advance(30000);
    assert.equal(page.warnings.length, 1);
    assert.equal(page.fetchLog.length, 0);
    page.clientState.userId = 'user-2';
    page.dispatch(page.document.body, 'viewshow');
    await page.advance(3000);
    assert.equal(posts(page).length, 1);
    assert.equal(posts(page)[0].userId, 'user-2');
});

test('inside a native app wrapper WebVersion is null and Client names the app', async () => {
    const { kit, page } = await signedIn({ nativeShell: true });
    fixtureView(page, false);
    kit.health.expect(['fixtureList'], 'fixture');
    await page.advance(7000);
    assert.deepEqual(page.warnings, ['[jfkt] web anchor "fixtureList" missing on fixture (web unknown, Jellyfin Media Player 1.12.0)']);
    const body = posts(page)[0].body;
    assert.equal(body.WebVersion, null);
    assert.equal(body.Client, 'Jellyfin Media Player');
    assert.equal(body.ClientVersion, '1.12.0');
    assert.deepEqual(plain(kit.health.web()), { webVersion: null, client: 'Jellyfin Media Player', clientVersion: '1.12.0' });
});

test('Layout and Experimental follow the web client\'s layout', async () => {
    const { kit, page } = await signedIn();
    page.document.documentElement.classList.add('layout-tv');
    page.context.localStorage.setItem('layout', 'experimental');
    fixtureView(page, false);
    kit.health.expect(['fixtureList'], 'fixture');
    await page.advance(7000);
    assert.equal(posts(page)[0].body.Layout, 'Tv');
    assert.equal(posts(page)[0].body.Experimental, true);
});

test('within: an anchor inside another is looked for there, and skipped while that one is missing', async () => {
    const { kit, page } = await signedIn();
    const outside = page.element('div', { class: 'fixtureButton' });
    page.document.body.append(outside);
    kit.health.expect([{ name: 'fixtureButton', within: 'fixtureList' }], 'fixture');
    await page.advance(7000);
    assert.deepEqual(plain(kit.health.missing()), [], 'no list, so the button inside it isn\'t reported');

    page.document.body.append(page.element('div', { class: 'fixtureList' }));
    page.context.location.hash = '#/fixture?id=9';
    kit.health.expect(['fixtureList', { name: 'fixtureButton', within: 'fixtureList' }], 'fixture');
    await page.advance(7000);
    assert.deepEqual(plain(kit.health.missing()), [{ anchor: 'fixtureButton', view: 'fixture', count: 1 }], 'the button outside the list doesn\'t count');
});

test('a view the anchor isn\'t declared for is a mistake in the plugin: reported, never sent', async () => {
    const { kit, page } = await signedIn();
    assert.equal(await kit.health.expect(['fixtureList'], 'details'), false);
    assert.equal(await kit.health.expect(['nope'], 'fixture'), false);
    await page.advance(TEN_MINUTES);
    assert.equal(posts(page).length, 0);
    assert.ok(page.warnings.some((w) => w.includes('"fixtureList" is not declared for the view "details"')));
    assert.ok(page.warnings.some((w) => w.includes('"nope" is not declared')));
});

test('a server error on the report is swallowed: the console warned already, nothing is retried', async () => {
    const { kit, page } = await signedIn();
    page.respond('POST', HEALTH, { title: 'Internal Server Error', status: 500 }, 500);
    fixtureView(page, false);
    kit.health.expect(['fixtureList'], 'fixture');
    await page.advance(7000);
    await page.advance(TEN_MINUTES);
    assert.equal(posts(page).length, 1);
    assert.equal(page.errors.length, 0);
});

test('the kit expects FallbackRoute\'s page on its own fallback routes (kit.f1Host on pages)', async () => {
    const { kit, page } = await signedIn();
    kit.pages.register({ key: 'alpha', title: 'Alpha', icon: 'star', idSuffix: 'AlphaPage', render: () => {} });
    page.context.history.pushState(null, '', '#/jfkt-alpha');
    await page.advance(7000);
    assert.deepEqual(plain(kit.health.missing()), [{ anchor: 'kit.f1Host', view: 'pages', count: 1 }]);
    assert.ok(page.warnings.includes('[jfkt] web anchor "kit.f1Host" missing on pages (web 10.11.5)'));

    page.context.history.pushState(null, '', '#/home');
    await page.advance(100);
    const view = page.element('div', { id: 'fallbackPage', 'data-role': 'page' });
    page.document.body.append(view);
    page.context.history.pushState(null, '', '#/jfkt-alpha?x=1');
    await page.advance(7000);
    assert.equal(kit.health.missing()[0].count, 1, 'with #fallbackPage there, no new miss');
});
