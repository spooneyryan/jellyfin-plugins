// Unit tests of the kit's page hosting in kit/web/kit.js, in the fake page
// of fake-dom.mjs: kit.pages (fallback routes in FallbackRoute's page, the Plugin Pages host, titles, page events on swaps,
// the hand-back, the shell check) and the "Library tools" nav. Run with
//   node --test kit/web/tests
import fs from 'node:fs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { anchorDeclarations, createPage, loadKit, plain } from './fake-dom.mjs';

const BUILD = { plugin: 'Kit Test', prefix: 'jfkt', route: 'Fx', version: '1.2.3.0', kit: '0.1.0', mvid: '0123abcd' };
const SHOW_EVENTS = ['viewbeforeshow', 'pagebeforeshow', 'viewshow', 'pageshow'];
const ALPHA_SHELL = '<div class="jfkt-page" data-jfkt-page="alpha"><p class="jfkt-shell-note">This page needs the Kit Test web UI, which is turned off or failed to load.</p></div>';
const BETA_SHELL = '<div class="jfkt-page" data-jfkt-page="beta"><p class="jfkt-shell-note">This page needs the Kit Test web UI, which is turned off or failed to load.</p></div>';
// FallbackRouteHeadFragment.Build("jfkt", alpha, beta) as the server builds it (a Kit.Tests test keeps the file equal):
// its script runs while index.html is parsed, so its capture viewbeforeshow listener comes before the kit's.
const HEAD_FRAGMENT = fs.readFileSync(new URL('./head-fragment-jfkt.html', import.meta.url), 'utf8');
const HEAD_SCRIPT = /<script data-jfkt-head>([\s\S]*?)<\/script>/.exec(HEAD_FRAGMENT)[1];

// A page shaped like jellyfin-web 10.11.5 around our script: the head fragment's state (and with `headScript` its real
// script), the drawer, LibraryMenu and CustomElements. `hash` is the location before the script runs.
function setup(options) {
    options = options || {};
    const page = createPage();
    page.context.location.hash = options.hash || '#/home';
    if (options.headScript) {
        page.run(HEAD_SCRIPT, 'head-fragment.js');
    }

    if (options.head !== null) {
        const head = Object.assign({ pages: ['alpha', 'beta'], off: [], mode: 'FallbackOnly', nav: true }, options.head || {});
        const island = page.element('script', { type: 'application/json', 'data-jfkt-head': '', 'data-jfkt-pages': '' });
        island.textContent = JSON.stringify(head);
        page.document.head.append(island);
    }

    const titles = [];
    page.context.LibraryMenu = { setTitle: (t) => titles.push(t) };
    const upgraded = [];
    page.context.CustomElements = { upgradeSubtree: (root) => upgraded.push(root) };
    if (options.pluginPages) {
        page.context.PluginPages = {};
    }

    page.respond('GET', '/Fx/Pages/alpha', ALPHA_SHELL, 200, { contentType: 'text/html; charset=utf-8' });
    page.respond('GET', '/Fx/Pages/beta', BETA_SHELL, 200, { contentType: 'text/html; charset=utf-8' });
    const exports = loadKit(page);
    const kit = exports.createKit(options.build || BUILD, '', { anchors: anchorDeclarations() });
    const renders = [];
    const register = (key, title, extra) => kit.pages.register(Object.assign({
        key,
        title,
        icon: key === 'alpha' ? 'video_library' : 'collections_bookmark',
        idSuffix: key.charAt(0).toUpperCase() + key.slice(1) + 'Page',
        render: (body, params, context) => {
            renders.push({ key, reason: context.reason, host: context.host, params: plain(params), render: context.render });
            body.append(kit.h('p', { class: 'content', text: title + ' content' }));
        }
    }, extra || {}));
    return { page, kit, titles, upgraded, renders, register };
}

async function started(options) {
    const s = setup(options);
    if (!options || options.register !== false) {
        s.register('alpha', 'Alpha', { order: 20 });
        s.register('beta', 'Beta', { order: 10 });
    }

    s.kit.start();
    await s.page.advance(10);
    s.page.installApiClient({ userId: options && options.signedOut ? null : 'user-1' });
    await s.page.advance(60);
    return s;
}

// FallbackRoute's page as React renders it: "Page not found" content and the four show events.
function showFallbackPage(page, title) {
    const view = page.element('div', { id: 'fallbackPage', 'data-role': 'page', class: 'page', 'data-title': title || 'Page not found' }, [
        page.element('div', { class: 'notFound' }, [page.element('h1', {}, ['Page not found'])])
    ]);
    page.document.body.append(view);
    SHOW_EVENTS.forEach((type) => page.dispatch(view, type, { detail: { isRestored: false } }));
    return view;
}

function drawer(page, withPluginPagesLinkFor) {
    const container = page.element('div', { class: 'mainDrawer-scrollContainer' }, [page.element('div', { class: 'userMenuOptions' })]);
    if (withPluginPagesLinkFor) {
        container.append(page.element('div', { class: 'pluginMenuOptions' }, [page.element('a', { 'data-itemid': withPluginPagesLinkFor })]));
    }

    page.document.body.append(container);
    return container;
}

function root(page) {
    return page.document.querySelector('#fallbackPage > [data-jfkt-page]');
}

function recordEvents(page, target) {
    const seen = [];
    SHOW_EVENTS.forEach((type) => target.addEventListener(type, (e) => seen.push({ type, bubbles: e.bubbles, isRestored: e.detail && e.detail.isRestored })));
    return seen;
}

// ------------------------------------------------------------------------------------------------ registration

test('register checks the definition, and ignores a page the head fragment does not list', () => {
    const { kit, page, register } = setup({ head: { pages: ['alpha'] } });
    assert.equal(register('alpha', 'Alpha'), true);
    assert.equal(register('alpha', 'Again'), false, 'duplicate');
    assert.equal(register('beta', 'Beta'), false, 'not announced');
    assert.equal(kit.pages.register({ key: 'Bad Key', title: 'x', icon: 'x', idSuffix: 'XPage', render() {} }), false);
    assert.equal(kit.pages.register({ key: 'gamma', title: 'x', icon: 'x', idSuffix: 'gamma', render() {} }), false);
    assert.equal(kit.pages.register({ key: 'gamma', title: '', icon: 'x', idSuffix: 'GammaPage', render() {} }), false);
    assert.equal(kit.pages.register({ key: 'gamma', title: 'x', icon: 'x', idSuffix: 'GammaPage' }), false);
    assert.deepEqual(plain(kit.pages.state().keys), ['alpha']);
    assert.deepEqual(plain(kit.pages.state().ignored), ['beta']);
    assert.equal(kit.pages.announced(), true);
    assert.equal(kit.pages.announced('beta'), false);
    assert.equal(page.warnings.filter((w) => w.includes('kit.pages.register')).length, 5);
});

test('enabled() is true for a page the head fragment lists as on, false for one turned off or not listed', () => {
    const { kit } = setup({ head: { pages: ['alpha', 'beta'], off: ['beta'] } });
    assert.equal(kit.pages.enabled('alpha'), true);
    assert.equal(kit.pages.enabled('beta'), false, 'turned off');
    assert.equal(kit.pages.enabled('gamma'), false, 'not listed');
    assert.equal(kit.pages.announced('beta'), true, 'a page turned off is still announced');
});

test('without a head fragment every registered page counts, and announced() is false', () => {
    const { kit, register } = setup({ head: null });
    assert.equal(register('alpha', 'Alpha'), true);
    assert.equal(kit.pages.announced(), false);
    assert.equal(kit.pages.enabled('alpha'), false);
    assert.equal(kit.pages.state().mode, 'FallbackOnly');
    assert.equal(kit.pages.state().nav, true);
});

test('a kit without pages adds no listener, style or request', async () => {
    const s = setup();
    s.kit.start();
    s.page.installApiClient({ userId: 'user-1' });
    await s.page.advance(500);
    assert.equal(s.page.document.getElementById('jfkt-page-styles'), null);
    s.page.context.history.pushState(null, '', '#/jfkt-alpha');
    await s.page.advance(50);
    assert.equal(s.page.document.documentElement.classList.contains('jfkt-route'), false);
    assert.equal(s.page.fetchLog.length, 0);
});

// ------------------------------------------------------------------------------------------ fallback host

test('FallbackOnly: fallback-route links with parameters, the pre-hide rule, and no Plugin Pages request', async () => {
    const s = await started({ pluginPages: true });
    assert.equal(s.kit.pages.href('alpha'), '#/jfkt-alpha');
    assert.equal(s.kit.pages.href('beta', { tab: 'two', n: 2, skipped: null }), '#/jfkt-beta?tab=two&n=2');
    assert.equal(s.kit.pages.host('alpha'), 'Fallback');
    assert.equal(s.kit.pages.state().hostsKnown, true);
    const style = s.page.document.getElementById('jfkt-page-styles');
    assert.equal(style.textContent, 'html.jfkt-route #fallbackPage > :not([data-jfkt-page]),#fallbackPage[data-jfkit-claim="jfkt"] > :not([data-jfkt-page]){display:none !important}');
    assert.equal(s.page.fetchLog.filter((f) => f.path.includes('PluginPages')).length, 0);
});

test('a cold load of a fallback route: the class at once, the title in the capture phase, the shell checked, then render', async () => {
    const s = setup({ hash: '#/jfkt-alpha?tab=running' });
    s.register('alpha', 'Alpha');
    s.kit.start();
    await s.page.advance(5);
    assert.equal(s.page.document.documentElement.classList.contains('jfkt-route'), true, 'the class before any page exists');
    // libraryMenu reads data-title in a bubble pageshow listener on the document.
    const seenTitles = [];
    s.page.document.addEventListener('pageshow', (e) => seenTitles.push(e.target.getAttribute('data-title')));
    const view = showFallbackPage(s.page);
    assert.deepEqual(seenTitles, ['Alpha']);
    assert.equal(root(s.page).getAttribute('data-jfkt-page'), 'alpha');
    assert.equal(root(s.page).hasAttribute('data-jfkt-mounted'), true);
    assert.equal(view.getAttribute('data-jfkit-claim'), 'jfkt');
    assert.equal(view.getAttribute('data-jfkit-orig-title'), 'Page not found');
    assert.equal(s.renders.length, 0, 'no render before the user');
    assert.equal(s.page.fetchLog.length, 0, 'no request before the user');
    s.page.installApiClient({ userId: 'user-1' });
    await s.page.advance(80);
    assert.deepEqual(s.renders, [{ key: 'alpha', reason: 'mount', host: 'Fallback', params: { tab: 'running' }, render: 1 }]);
    assert.deepEqual(s.page.fetchLog.map((f) => f.method + ' ' + f.path), ['GET /Fx/Pages/alpha']);
    assert.ok(s.page.fetchLog.every((f) => f.userId === 'user-1'));
    assert.equal(root(s.page).getAttribute('data-jfkt-state'), 'on');
    assert.equal(root(s.page).getAttribute('data-jfkt-host'), 'Fallback');
    assert.equal(root(s.page).querySelector('.content').textContent, 'Alpha content');
    assert.ok(s.titles.includes('Alpha'));
    assert.equal(s.page.errors.length, 0, s.page.errors.join('\n'));
});

test('a slow script: FallbackRoute already showed, so the kit claims and mounts at start', async () => {
    const s = setup({ hash: '#/jfkt-beta' });
    s.register('beta', 'Beta');
    const view = showFallbackPage(s.page);
    s.page.installApiClient({ userId: 'user-1' });
    s.kit.start();
    await s.page.advance(100);
    assert.equal(view.getAttribute('data-title'), 'Beta');
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['beta:mount']);
});

test('our route to our route: the root is swapped and the four page events fire on FallbackRoute\'s page, once', async () => {
    const s = await started({ hash: '#/jfkt-alpha' });
    const view = showFallbackPage(s.page);
    await s.page.advance(50);
    const events = recordEvents(s.page, view);
    s.page.context.history.pushState(null, '', '#/jfkt-beta?n=6');
    assert.equal(root(s.page).getAttribute('data-jfkt-page'), 'beta', 'swapped synchronously');
    assert.equal(view.getAttribute('data-title'), 'Beta');
    await s.page.advance(50);
    assert.deepEqual(events.map((e) => e.type), SHOW_EVENTS);
    assert.ok(events.every((e) => e.bubbles && e.isRestored === false));
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount', 'beta:mount']);
    assert.deepEqual(s.renders[1].params, { n: '6' });
    assert.equal(s.page.document.querySelectorAll('#fallbackPage > [data-jfkt-page]').length, 1);
    // A popstate back re-renders the other page the same way.
    s.page.context.location.hash = '#/jfkt-alpha';
    s.page.window.dispatchEvent(new s.page.Event('popstate'));
    await s.page.advance(50);
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount', 'beta:mount', 'alpha:mount']);
    assert.equal(events.length, 8);
    // The same page with other parameters keeps its root and renders again.
    s.page.context.history.pushState(null, '', '#/jfkt-alpha?tab=two');
    await s.page.advance(50);
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount', 'beta:mount', 'alpha:mount', 'alpha:swap']);
    assert.deepEqual(s.renders[3].params, { tab: 'two' });
    assert.equal(events.length, 12);
});

test('an unknown route to ours while FallbackRoute stays mounted: claimed, with the page events', async () => {
    const s = await started({ hash: '#/nobody' });
    const view = showFallbackPage(s.page);
    await s.page.advance(20);
    assert.equal(root(s.page), null, 'not claimed on an unknown route');
    const events = recordEvents(s.page, view);
    s.page.context.history.pushState(null, '', '#/jfkt-alpha');
    await s.page.advance(50);
    assert.equal(events.length, 4);
    assert.deepEqual(s.renders.map((r) => r.key), ['alpha']);
});

test('ours to an unknown route with FallbackRoute still mounted: our page and title for about 1 s, then handed back', async () => {
    const s = await started({ hash: '#/jfkt-alpha' });
    const view = showFallbackPage(s.page);
    await s.page.advance(50);
    s.titles.length = 0;
    s.page.context.history.pushState(null, '', '#/nobody');
    assert.equal(s.page.document.documentElement.classList.contains('jfkt-route'), false);
    // The claim rule keeps "Page not found" hidden, so our page and its title stay together until the hand-back.
    assert.equal(root(s.page).classList.contains('hide'), false);
    assert.equal(view.getAttribute('data-jfkit-claim'), 'jfkt');
    await s.page.advance(900);
    assert.ok(root(s.page), 'kept for about 1 s');
    assert.equal(view.getAttribute('data-title'), 'Alpha');
    assert.deepEqual(s.titles, []);
    await s.page.advance(200);
    assert.equal(root(s.page), null);
    assert.equal(view.getAttribute('data-title'), 'Page not found');
    assert.equal(view.hasAttribute('data-jfkit-claim'), false);
    assert.equal(view.hasAttribute('data-jfkit-orig-title'), false);
    assert.deepEqual(s.titles, ['Page not found']);
});

test('back to ours within the second: our page renders again and nothing is handed back', async () => {
    const s = await started({ hash: '#/jfkt-alpha' });
    showFallbackPage(s.page);
    await s.page.advance(50);
    s.page.context.history.pushState(null, '', '#/nobody');
    await s.page.advance(300);
    s.page.context.history.pushState(null, '', '#/jfkt-alpha');
    await s.page.advance(1200);
    assert.ok(root(s.page));
    assert.equal(root(s.page).classList.contains('hide'), false);
    assert.equal(s.page.document.getElementById('fallbackPage').getAttribute('data-title'), 'Alpha');
    assert.equal(s.page.document.getElementById('fallbackPage').getAttribute('data-jfkit-claim'), 'jfkt');
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount', 'alpha:swap']);
});

test('with the head fragment\'s real script first: a page React mounts on our route keeps "Page not found" for the hand-back', async () => {
    // A click from a native page: the router mounts FallbackRoute after our pushState hook ran, and React's Page fires
    // the page events at once, so the fragment's listener sets our title before the kit's listener claims the page.
    const s = setup({ headScript: true });
    s.register('alpha', 'Alpha');
    s.kit.start();
    s.page.installApiClient({ userId: 'user-1' });
    await s.page.advance(80);
    s.page.context.history.pushState(null, '', '#/jfkt-alpha');
    const seenTitles = [];
    s.page.document.addEventListener('pageshow', (e) => seenTitles.push(e.target.getAttribute('data-title')));
    const view = showFallbackPage(s.page);
    assert.deepEqual(seenTitles, ['Alpha']);
    assert.equal(view.getAttribute('data-jfkit-orig-title'), 'Page not found');
    await s.page.advance(80);
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount']);
    s.titles.length = 0;
    s.page.context.history.pushState(null, '', '#/nobody');
    await s.page.advance(1100);
    assert.equal(root(s.page), null);
    assert.equal(view.getAttribute('data-title'), 'Page not found');
    assert.deepEqual(s.titles, ['Page not found']);
    assert.equal(s.page.errors.length, 0, s.page.errors.join('\n'));
});

test('with the head fragment\'s real script first: a slow script claims a page the fragment titled, and hands back "Page not found"', async () => {
    const s = setup({ hash: '#/jfkt-alpha', headScript: true });
    s.register('alpha', 'Alpha');
    assert.equal(s.page.document.documentElement.classList.contains('jfkt-route'), true, 'the fragment set the class');
    const view = showFallbackPage(s.page);
    assert.equal(view.getAttribute('data-title'), 'Alpha', 'titled by the fragment before the kit started');
    s.page.installApiClient({ userId: 'user-1' });
    s.kit.start();
    await s.page.advance(100);
    assert.equal(view.getAttribute('data-jfkit-orig-title'), 'Page not found');
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount']);
    s.page.context.history.pushState(null, '', '#/nobody');
    await s.page.advance(1100);
    assert.equal(view.getAttribute('data-title'), 'Page not found');
    assert.ok(s.titles.includes('Page not found'));
});

test('Back or Forward from a native page into ours: React\'s page events only, one render', async () => {
    const s = await started({ hash: '#/jfkt-alpha' });
    const first = showFallbackPage(s.page);
    await s.page.advance(50);
    // Leave for a native page: React unmounts FallbackRoute.
    s.page.context.history.pushState(null, '', '#/home');
    first.remove();
    await s.page.advance(1100);
    const shows = [];
    SHOW_EVENTS.forEach((type) => s.page.document.addEventListener(type, (e) => {
        if (e.target.id === 'fallbackPage') {
            shows.push(type);
        }
    }, true));
    // history.back(): jellyfin-web's popstate listener runs before ours and React mounts FallbackRoute at once, with the
    // page events; then ours runs.
    s.page.context.location.hash = '#/jfkt-alpha';
    const view = showFallbackPage(s.page);
    s.page.window.dispatchEvent(new s.page.Event('popstate'));
    s.page.window.dispatchEvent(new s.page.Event('hashchange'));
    await s.page.advance(50);
    assert.deepEqual(shows, SHOW_EVENTS, 'the page events once, from React');
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount', 'alpha:mount']);
    assert.equal(view.getAttribute('data-jfkit-claim'), 'jfkt');
    // The next swap between our routes still gets its page events.
    s.page.context.history.pushState(null, '', '#/jfkt-beta');
    await s.page.advance(50);
    assert.deepEqual(shows, SHOW_EVENTS.concat(SHOW_EVENTS));
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount', 'alpha:mount', 'beta:mount']);
    // And a Back between two of our routes too (FallbackRoute stays mounted: React fires nothing).
    s.page.context.location.hash = '#/jfkt-alpha';
    s.page.window.dispatchEvent(new s.page.Event('popstate'));
    await s.page.advance(50);
    assert.equal(shows.length, 12);
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount', 'alpha:mount', 'beta:mount', 'alpha:mount']);
});

test('the hand-back leaves the title alone when another plugin claimed the page meanwhile', async () => {
    const s = await started({ hash: '#/jfkt-alpha' });
    const view = showFallbackPage(s.page);
    await s.page.advance(50);
    s.page.context.history.pushState(null, '', '#/jfzz-other');
    view.setAttribute('data-jfkit-claim', 'jfzz');
    view.setAttribute('data-title', 'Other plugin');
    await s.page.advance(1100);
    assert.equal(root(s.page), null, 'our root is removed');
    assert.equal(view.getAttribute('data-title'), 'Other plugin');
    assert.equal(view.getAttribute('data-jfkit-claim'), 'jfzz');
    assert.equal(view.getAttribute('data-jfkit-orig-title'), 'Page not found');
});

test('a page the server turned off shows the shell\'s sentence instead of rendering', async () => {
    const s = setup({ hash: '#/jfkt-beta', head: { off: ['beta'] } });
    s.page.respond('GET', '/Fx/Pages/beta', '<div class="jfkt-page"><p class="jfkt-shell-note">Beta is turned off by your server admin.</p></div>', 200);
    s.register('beta', 'Beta');
    s.kit.start();
    s.page.installApiClient({ userId: 'user-1' });
    await s.page.advance(40);
    const view = showFallbackPage(s.page);
    await s.page.advance(80);
    assert.equal(s.renders.length, 0);
    assert.equal(view.getAttribute('data-title'), 'Beta');
    assert.equal(root(s.page).getAttribute('data-jfkt-state'), 'off');
    assert.equal(root(s.page).querySelector('.jfkt-page-note').textContent, 'Beta is turned off by your server admin.');
});

test('a page the server does not know (404) says so; a failing shell request still renders', async () => {
    const s = setup({ hash: '#/jfkt-alpha' });
    s.page.respond('GET', '/Fx/Pages/alpha', { title: 'Not Found', status: 404, detail: 'There is no such page.' }, 404);
    s.page.respond('GET', '/Fx/Pages/beta', { title: 'Service Unavailable', status: 503 }, 503);
    s.register('alpha', 'Alpha');
    s.register('beta', 'Beta');
    s.kit.start();
    s.page.installApiClient({ userId: 'user-1' });
    await s.page.advance(40);
    showFallbackPage(s.page);
    await s.page.advance(80);
    assert.equal(root(s.page).getAttribute('data-jfkt-state'), 'unknown');
    assert.equal(root(s.page).querySelector('.jfkt-page-note').textContent, 'This page is not available on this server.');
    s.page.context.history.pushState(null, '', '#/jfkt-beta');
    await s.page.advance(80);
    assert.deepEqual(s.renders.map((r) => r.key), ['beta']);
});

test('a render that throws shows a note and is reported', async () => {
    const s = setup({ hash: '#/jfkt-alpha' });
    s.kit.pages.register({ key: 'alpha', title: 'Alpha', icon: 'video_library', idSuffix: 'AlphaPage', render: () => Promise.reject(new Error('boom')) });
    s.kit.start();
    s.page.installApiClient({ userId: 'user-1' });
    await s.page.advance(40);
    showFallbackPage(s.page);
    await s.page.advance(80);
    assert.equal(root(s.page).querySelector('.jfkt-page-note').textContent, 'This page could not be shown. Reload to try again.');
    assert.ok(s.page.warnings.some((w) => w.includes('boom')));
});

test('a Plugin Pages link rewritten without .html (Plugin Pages removed or broken) opens in the fallback host', async () => {
    const s = await started({ hash: '#/userpluginsettings?pageUrl=%2FFx%2FPages%2Falpha&tab=deep' });
    const view = showFallbackPage(s.page);
    await s.page.advance(80);
    assert.equal(view.getAttribute('data-title'), 'Alpha');
    assert.deepEqual(s.renders, [{ key: 'alpha', reason: 'mount', host: 'Fallback', params: { tab: 'deep' }, render: 1 }]);
    // Another plugin's shell path, or an unknown key, is not ours.
    s.page.context.history.pushState(null, '', '#/userpluginsettings?pageUrl=%2FOther%2FPages%2Falpha');
    assert.equal(s.page.document.documentElement.classList.contains('jfkt-route'), false);
});

test('off our pages the page observer only reads the hash: no DOM query, and roots outside the hosts are left alone', async () => {
    const s = await started({ hash: '#/home' });
    const queries = [];
    const querySelectorAll = s.page.document.querySelectorAll.bind(s.page.document);
    const getElementById = s.page.document.getElementById.bind(s.page.document);
    s.page.document.querySelectorAll = (selector) => {
        queries.push(selector);
        return querySelectorAll(selector);
    };
    s.page.document.getElementById = (id) => {
        queries.push('#' + id);
        return getElementById(id);
    };
    const stray = s.page.element('div', { class: 'jfkt-page', 'data-jfkt-page': 'alpha' });
    for (let i = 0; i < 20; i++) {
        s.page.document.body.append(s.page.element('div', { class: 'card' }, i === 10 ? [stray] : []));
        await s.page.flush();
    }

    await s.page.advance(400);
    assert.deepEqual(queries.filter((q) => q.includes('jfkt-page') || q === '#fallbackPage' || q.includes('userPreferencesPage')), []);
    assert.equal(stray.hasAttribute('data-jfkt-mounted'), false);
    assert.equal(s.renders.length, 0);
});

test('unknown keys and other prefixes are never claimed', async () => {
    const s = await started({ hash: '#/jfkt-nope' });
    showFallbackPage(s.page);
    await s.page.advance(50);
    assert.equal(root(s.page), null);
    for (const hash of ['#/jfktx-alpha', '#/jfkt-alpha.html', '#/jfkt-alpha/x', '#/home']) {
        s.page.context.history.pushState(null, '', hash);
        assert.equal(s.page.document.documentElement.classList.contains('jfkt-route'), false, hash);
    }
});

// ------------------------------------------------------------------------------------------------ Plugin Pages host

test('Plugin Pages host: the title in viewbeforeshow, the shell mounted when inserted, restored views render again', async () => {
    const s = await started({ hash: '#/userpluginsettings.html?pageUrl=/Fx/Pages/beta&tab=two' });
    const host = s.page.element('div', { id: 'userPluginPreferencesPage', 'data-role': 'page', class: 'page libraryPage userPreferencesPage', 'data-title': 'Home' }, [
        s.page.element('div', { class: 'userPluginSettingsContainer' })
    ]);
    s.page.document.body.append(host);
    const seenTitles = [];
    s.page.document.addEventListener('pageshow', (e) => seenTitles.push(e.target.getAttribute('data-title')));
    SHOW_EVENTS.forEach((type) => s.page.dispatch(host, type, { detail: { isRestored: false } }));
    assert.deepEqual(seenTitles, ['Beta']);
    // The host view fetches the shell about 100 ms later and appends it.
    await s.page.advance(100);
    const shell = s.page.element('div', { class: 'jfkt-page', 'data-jfkt-page': 'beta' }, [s.page.element('p', { class: 'jfkt-shell-note' }, ['This page needs the Kit Test web UI'])]);
    host.firstElementChild.append(shell);
    await s.page.advance(50);
    assert.equal(shell.hasAttribute('data-jfkt-mounted'), true);
    assert.deepEqual(s.renders, [{ key: 'beta', reason: 'mount', host: 'PluginPages', params: { tab: 'two' }, render: 1 }]);
    assert.equal(s.page.fetchLog.filter((f) => f.path.startsWith('/Fx/Pages')).length, 0, 'the host fetched the shell itself');
    SHOW_EVENTS.forEach((type) => s.page.dispatch(host, type, { detail: { isRestored: true } }));
    await s.page.advance(50);
    assert.deepEqual(s.renders.map((r) => r.reason), ['mount', 'restored']);
    assert.ok(s.titles.includes('Beta'));
});

test('Plugin Pages host: a turned-off page keeps the shell\'s note, titled from the key', async () => {
    const s = await started({ hash: '#/userpluginsettings.html?pageUrl=%2FFx%2FPages%2Falpha', head: { off: ['alpha'] } });
    const host = s.page.element('div', { 'data-role': 'page', class: 'page userPreferencesPage', 'data-title': 'Home' });
    s.page.document.body.append(host);
    SHOW_EVENTS.forEach((type) => s.page.dispatch(host, type));
    host.append(s.page.element('div', { class: 'jfkt-page' }, [s.page.element('p', { class: 'jfkt-shell-note' }, ['Alpha is turned off by your server admin.'])]));
    await s.page.advance(50);
    assert.equal(host.getAttribute('data-title'), 'Alpha');
    assert.equal(s.renders.length, 0);
});

test('the render context\'s isShown(): false while Plugin Pages\' host view is kept hidden (isCurrent() still true), and on the fallback route once the root is gone', async () => {
    const s = setup();
    const contexts = [];
    const render = (body, params, context) => {
        contexts.push(context);
    };
    s.register('alpha', 'Alpha', { order: 20, render });
    s.register('beta', 'Beta', { order: 10, render });
    s.kit.start();
    s.page.context.location.hash = '#/userpluginsettings.html?pageUrl=/Fx/Pages/beta';
    await s.page.advance(10);
    s.page.installApiClient({ userId: 'user-1' });
    await s.page.advance(60);
    const host = s.page.element('div', { id: 'userPluginPreferencesPage', 'data-role': 'page', class: 'page libraryPage userPreferencesPage', 'data-title': 'Home' }, [
        s.page.element('div', { class: 'userPluginSettingsContainer' }, [s.page.element('div', { class: 'jfkt-page', 'data-jfkt-page': 'beta' })])
    ]);
    s.page.document.body.append(host);
    SHOW_EVENTS.forEach((type) => s.page.dispatch(host, type, { detail: { isRestored: false } }));
    await s.page.advance(50);
    assert.equal(contexts.length, 1);
    const first = contexts[0];
    assert.deepEqual([first.isCurrent(), first.isShown()], [true, true]);
    // Another page shows: jellyfin-web keeps the host view, hidden.
    host.classList.add('hide');
    assert.deepEqual([first.isCurrent(), first.isShown()], [true, false], 'current, but not on screen');
    // Shown again: the kit renders it again, and the earlier render is neither.
    host.classList.remove('hide');
    SHOW_EVENTS.forEach((type) => s.page.dispatch(host, type, { detail: { isRestored: true } }));
    await s.page.advance(50);
    assert.equal(contexts.length, 2);
    assert.deepEqual([first.isCurrent(), first.isShown(), contexts[1].isShown()], [false, false, true]);
    // The root hidden itself.
    host.querySelector('[data-jfkt-page]').classList.add('hide');
    assert.equal(contexts[1].isShown(), false);

    // Fallback route: shown on its route; gone with the root (another route).
    s.page.context.location.hash = '#/jfkt-alpha';
    s.page.context.dispatchEvent(new s.page.Event('popstate'));
    const view = showFallbackPage(s.page);
    await s.page.advance(50);
    const f1 = contexts[contexts.length - 1];
    assert.equal(f1.key, 'alpha');
    assert.deepEqual([f1.isCurrent(), f1.isShown()], [true, true]);
    view.remove();
    assert.deepEqual([f1.isCurrent(), f1.isShown()], [false, false]);
});

// ------------------------------------------------------------------------------------------------ Library tools

test('Library tools: one section before .userMenuOptions, links in order, no click handler, upgraded once', async () => {
    const s = await started({ head: { off: [] } });
    const container = drawer(s.page);
    await s.page.advance(400);
    const section = container.querySelector('.jfkit-nav');
    assert.ok(section);
    assert.equal(section.getAttribute('data-jfkit-nav'), '1');
    assert.equal(section.nextElementSibling.className, 'userMenuOptions');
    assert.equal(section.querySelector('h3').className, 'sidebarHeader');
    assert.equal(section.querySelector('h3').textContent, 'Library tools');
    const links = section.querySelectorAll('a');
    assert.deepEqual(links.map((a) => a.getAttribute('data-jfkt-nav')), ['beta', 'alpha'], 'ordered by data-order');
    const alpha = links[1];
    assert.equal(alpha.getAttribute('is'), 'emby-linkbutton');
    assert.equal(alpha.className, 'navMenuOption lnkMediaFolder');
    assert.equal(alpha.getAttribute('href'), '#/jfkt-alpha');
    assert.equal(alpha.getAttribute('data-jfkit-plugin'), 'jfkt');
    assert.equal(alpha.getAttribute('data-order'), '20');
    assert.equal(alpha.querySelector('.navMenuOptionIcon').className, 'material-icons navMenuOptionIcon video_library');
    assert.equal(alpha.querySelector('.navMenuOptionText').textContent, 'Alpha');
    assert.equal(alpha._listeners.length, 0, 'emby-linkbutton shows the route itself');
    assert.deepEqual(s.upgraded, [section]);
    s.kit.nav.refresh();
    await s.page.advance(400);
    assert.equal(container.querySelectorAll('.jfkit-nav a').length, 2, 'idempotent');
    assert.equal(s.upgraded.length, 1);
    assert.equal(s.kit.nav.section(), section);
});

test('Library tools: rebuilt after the drawer is, never shown signed out, turned-off pages left out', async () => {
    const s = await started({ head: { off: ['beta'] } });
    let container = drawer(s.page);
    await s.page.advance(400);
    assert.deepEqual(container.querySelectorAll('.jfkit-nav a').map((a) => a.getAttribute('data-jfkt-nav')), ['alpha']);
    container.remove();
    container = drawer(s.page);
    await s.page.advance(400);
    assert.equal(container.querySelectorAll('.jfkit-nav a').length, 1);
    const signedOut = await started({ signedOut: true });
    const other = drawer(signedOut.page);
    await signedOut.page.advance(400);
    assert.equal(other.querySelector('.jfkit-nav'), null);
});

test('Library tools: ShowFallbackNav off adds nothing and removes our links; two plugins share one section', async () => {
    const off = await started({ head: { nav: false } });
    const container = drawer(off.page);
    await off.page.advance(400);
    assert.equal(container.querySelector('.jfkit-nav'), null);
    assert.equal(off.kit.pages.href('alpha'), '#/jfkt-alpha', 'the routes still work');

    const s = await started();
    const shared = drawer(s.page);
    const other = s.page.element('div', { class: 'jfkit-nav', 'data-jfkit-nav': '1' }, [
        s.page.element('h3', { class: 'sidebarHeader' }, ['Library tools']),
        s.page.element('a', { 'data-jfkit-plugin': 'jfzz', 'data-order': '15' })
    ]);
    shared.insertBefore(other, shared.firstElementChild);
    await s.page.advance(400);
    assert.equal(shared.querySelectorAll('.jfkit-nav').length, 1);
    assert.deepEqual(other.querySelectorAll('a').map((a) => a.getAttribute('data-jfkit-plugin') + ':' + a.getAttribute('data-order')), ['jfkt:10', 'jfzz:15', 'jfkt:20']);
});

// ------------------------------------------------------------------------------------------------ Auto mode

test('Auto: Plugin Pages\' list decides the host after sign-in, and its drawer link decides ours', async () => {
    const s = setup({ head: { mode: 'Auto' }, pluginPages: true });
    s.page.respond('GET', '/PluginPages/User', { Items: [{ Id: 'Jellyfin.Plugin.Fx.AlphaPage', Url: '/Fx/Pages/alpha' }], TotalRecordCount: 1 });
    s.register('alpha', 'Alpha', { order: 20 });
    s.register('beta', 'Beta', { order: 10 });
    s.kit.start();
    await s.page.advance(100);
    assert.equal(s.kit.pages.state().hostsKnown, false);
    assert.equal(s.page.fetchLog.length, 0, 'no request before the user');
    s.page.installApiClient({ userId: 'user-1' });
    await s.page.advance(100);
    assert.deepEqual(plain(s.kit.pages.state().hosts), { alpha: 'PluginPages', beta: 'Fallback' });
    assert.equal(s.kit.pages.href('alpha', { tab: 'x' }), '#/userpluginsettings.html?pageUrl=%2FFx%2FPages%2Falpha&tab=x');
    assert.equal(s.kit.pages.href('beta'), '#/jfkt-beta');
    assert.deepEqual(s.page.fetchLog.map((f) => f.path), ['/PluginPages/User']);

    // Plugin Pages shows its link: ours only for beta.
    const container = drawer(s.page, 'Jellyfin.Plugin.Fx.AlphaPage');
    await s.page.advance(1500);
    assert.deepEqual(container.querySelectorAll('.jfkit-nav a').map((a) => a.getAttribute('data-jfkt-nav')), ['beta']);

    // A sign-in without a reload rebuilds the drawer, and Plugin Pages 2.4.7 doesn't add its section again: after about
    // 1 s our link to alpha (its Plugin Pages route) appears.
    container.remove();
    const rebuilt = drawer(s.page);
    await s.page.advance(400);
    assert.deepEqual(rebuilt.querySelectorAll('.jfkit-nav a').map((a) => a.getAttribute('data-jfkt-nav')), ['beta']);
    await s.page.advance(1000);
    const links = rebuilt.querySelectorAll('.jfkit-nav a');
    assert.deepEqual(links.map((a) => a.getAttribute('data-jfkt-nav')), ['beta', 'alpha']);
    assert.equal(links[1].getAttribute('href'), '#/userpluginsettings.html?pageUrl=%2FFx%2FPages%2Falpha');
});

test('PluginPagesOnly leaves listed pages to Plugin Pages; without Plugin Pages every page is on its fallback route', async () => {
    const s = setup({ head: { mode: 'PluginPagesOnly' }, pluginPages: true });
    s.page.respond('GET', '/PluginPages/User', { Items: [{ Id: 'Jellyfin.Plugin.Fx.AlphaPage' }] });
    s.register('alpha', 'Alpha');
    s.register('beta', 'Beta');
    s.kit.start();
    s.page.installApiClient({ userId: 'user-1' });
    const container = drawer(s.page);
    await s.page.advance(2500);
    assert.deepEqual(container.querySelectorAll('.jfkit-nav a').map((a) => a.getAttribute('data-jfkt-nav')), ['beta']);

    const absent = await started({ head: { mode: 'Auto' } });
    assert.deepEqual(plain(absent.kit.pages.state().hosts), { alpha: 'Fallback', beta: 'Fallback' });
    assert.equal(absent.page.fetchLog.filter((f) => f.path.includes('PluginPages')).length, 0);
});

// ------------------------------------------------------------------------------------------------ links

test('kit.pages.link is a plain emby-linkbutton to the page, with no click handler', async () => {
    const s = await started();
    const link = s.kit.pages.link('beta', { tab: 'two' }, 'Open Beta', { 'data-test': 'x' });
    assert.equal(link.tagName, 'A');
    assert.equal(link.getAttribute('is'), 'emby-linkbutton');
    assert.equal(link.className, 'button-link jfkt-page-link');
    assert.equal(link.getAttribute('href'), '#/jfkt-beta?tab=two');
    assert.equal(link.getAttribute('data-jfkt-link'), 'beta');
    assert.equal(link.getAttribute('data-test'), 'x');
    assert.equal(link.textContent, 'Open Beta');
    assert.equal(link._listeners.length, 0);
});

test('page bodies are upgraded after render, so in-page links work', async () => {
    const s = setup({ hash: '#/jfkt-alpha' });
    s.kit.pages.register({
        key: 'alpha',
        title: 'Alpha',
        icon: 'video_library',
        idSuffix: 'AlphaPage',
        render: (body) => {
            body.append(s.kit.pages.link('alpha', { n: 1 }, 'Again'));
        }
    });
    s.kit.start();
    s.page.installApiClient({ userId: 'user-1' });
    await s.page.advance(40);
    showFallbackPage(s.page);
    await s.page.advance(80);
    const body = root(s.page).querySelector('.jfkt-page-body');
    assert.ok(s.upgraded.includes(body));
});

// ------------------------------------------------------------------------------------------------ setParams

test('setParams changes the shown page\'s hash parameters without a render: pushed, or replaced; Back renders the earlier state', async () => {
    const s = await started({ hash: '#/jfkt-alpha?tab=one' });
    const view = showFallbackPage(s.page);
    await s.page.advance(50);
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount']);
    const events = recordEvents(s.page, view);
    const length = s.page.context.history.length;
    assert.equal(s.kit.pages.setParams('alpha', { tab: 'two', q: 'a b', skipped: null }), true);
    assert.equal(s.page.context.location.hash, '#/jfkt-alpha?tab=two&q=a+b');
    assert.equal(s.page.context.history.length, length + 1, 'pushed');
    await s.page.advance(50);
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount'], 'no render');
    assert.equal(events.length, 0, 'no page events');
    assert.equal(s.kit.pages.setParams('alpha', { tab: 'two', q: 'a b' }), false, 'nothing to change');
    assert.equal(s.kit.pages.setParams('alpha', { tab: 'three' }, { replace: true }), true);
    assert.equal(s.page.context.location.hash, '#/jfkt-alpha?tab=three');
    assert.equal(s.page.context.history.length, length + 1, 'replaced');
    assert.equal(s.kit.pages.setParams('beta', { tab: 'x' }), false, 'not the page shown');
    assert.equal(s.page.context.location.hash, '#/jfkt-alpha?tab=three');
    await s.page.advance(50);
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount']);
    // Back to the first state: the kit renders it from the hash, as for any location change.
    s.page.context.location.hash = '#/jfkt-alpha?tab=one';
    s.page.window.dispatchEvent(new s.page.Event('popstate'));
    await s.page.advance(50);
    assert.deepEqual(s.renders.map((r) => r.key + ':' + r.reason), ['alpha:mount', 'alpha:swap']);
    assert.deepEqual(s.renders[1].params, { tab: 'one' });
    assert.equal(events.length, 4);
    assert.equal(s.page.errors.length, 0, s.page.errors.join('\n'));
});

test('setParams keeps the Plugin Pages host of a page shown there, and the pageUrl parameter', async () => {
    const s = await started({ hash: '#/userpluginsettings.html?pageUrl=%2FFx%2FPages%2Falpha&tab=one' });
    assert.equal(s.kit.pages.setParams('alpha', { tab: 'two' }), true);
    assert.equal(s.page.context.location.hash, '#/userpluginsettings.html?pageUrl=%2FFx%2FPages%2Falpha&tab=two');
    assert.equal(s.kit.pages.setParams('alpha', {}, { replace: true }), true);
    assert.equal(s.page.context.location.hash, '#/userpluginsettings.html?pageUrl=%2FFx%2FPages%2Falpha');
    s.page.context.location.hash = '#/home';
    assert.equal(s.kit.pages.setParams('alpha', { tab: 'two' }), false, 'off the page');
    assert.equal(s.page.context.location.hash, '#/home');
});

// A Plugin Pages host view with the shell of a page in it, shown and mounted.
async function pluginPagesShown(s, key) {
    const host = s.page.element('div', { id: 'userPluginPreferencesPage', 'data-role': 'page', class: 'page libraryPage userPreferencesPage', 'data-title': 'Home' }, [
        s.page.element('div', { class: 'userPluginSettingsContainer' })
    ]);
    s.page.document.body.append(host);
    SHOW_EVENTS.forEach((type) => s.page.dispatch(host, type, { detail: { isRestored: false } }));
    await s.page.advance(100);
    const shell = s.page.element('div', { class: 'jfkt-page', 'data-jfkt-page': key }, [s.page.element('p', { class: 'jfkt-shell-note' }, ['This page needs the Kit Test web UI'])]);
    host.firstElementChild.append(shell);
    await s.page.advance(50);
    return { host, shell };
}

test('in Plugin Pages\' host, Back and Forward over setParams entries render the page again from the hash (jellyfin-web fires no page events for the query alone); a Back React restores renders once', async () => {
    const s = await started({ hash: '#/userpluginsettings.html?pageUrl=%2FFx%2FPages%2Falpha&tab=one' });
    const { host } = await pluginPagesShown(s, 'alpha');
    assert.deepEqual(s.renders.map((r) => r.reason + ':' + JSON.stringify(r.params)), ['mount:{"tab":"one"}']);
    assert.equal(s.kit.pages.setParams('alpha', { tab: 'two' }), true);
    await s.page.advance(50);
    assert.equal(s.renders.length, 1, 'no render for the page\'s own change');

    // Back: the query alone changed, no view event from jellyfin-web, so the kit renders from the hash.
    s.page.context.location.hash = '#/userpluginsettings.html?pageUrl=%2FFx%2FPages%2Falpha&tab=one';
    s.page.window.dispatchEvent(new s.page.Event('popstate'));
    s.page.window.dispatchEvent(new s.page.Event('hashchange'));
    await s.page.advance(150);
    assert.deepEqual(s.renders.map((r) => r.reason + ':' + JSON.stringify(r.params)), ['mount:{"tab":"one"}', 'navigation:{"tab":"one"}']);
    assert.equal(s.renders[1].host, 'PluginPages');
    // Forward.
    s.page.context.location.hash = '#/userpluginsettings.html?pageUrl=%2FFx%2FPages%2Falpha&tab=two';
    s.page.window.dispatchEvent(new s.page.Event('popstate'));
    await s.page.advance(150);
    assert.deepEqual(s.renders.map((r) => r.reason + ':' + JSON.stringify(r.params)).slice(2), ['navigation:{"tab":"two"}']);
    assert.ok(s.titles.includes('Alpha'));

    // Back from another page: React restores the host view and fires its page events (before or after our popstate
    // listener); the restored render stands alone.
    s.page.context.location.hash = '#/home';
    s.page.window.dispatchEvent(new s.page.Event('popstate'));
    host.classList.add('hide');
    await s.page.advance(150);
    assert.equal(s.renders.length, 3, 'nothing off the page');
    s.page.context.location.hash = '#/userpluginsettings.html?pageUrl=%2FFx%2FPages%2Falpha&tab=two';
    host.classList.remove('hide');
    SHOW_EVENTS.forEach((type) => s.page.dispatch(host, type, { detail: { isRestored: true } }));
    s.page.window.dispatchEvent(new s.page.Event('popstate'));
    await s.page.advance(150);
    assert.deepEqual(s.renders.map((r) => r.reason).slice(3), ['restored'], 'React\'s events first: one render');
    s.page.context.location.hash = '#/home';
    s.page.window.dispatchEvent(new s.page.Event('popstate'));
    host.classList.add('hide');
    await s.page.advance(150);
    s.page.context.location.hash = '#/userpluginsettings.html?pageUrl=%2FFx%2FPages%2Falpha&tab=one';
    s.page.window.dispatchEvent(new s.page.Event('popstate'));
    host.classList.remove('hide');
    SHOW_EVENTS.forEach((type) => s.page.dispatch(host, type, { detail: { isRestored: true } }));
    await s.page.advance(150);
    assert.deepEqual(s.renders.map((r) => r.reason).slice(4), ['restored'], 'our listener first: still one render');
    assert.equal(s.page.errors.length, 0, s.page.errors.join('\n'));
});
