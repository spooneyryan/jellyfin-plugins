// Bookmarks' page-hosting demo (Web/js/60-pagedemo.js) in the kit's fake page, with the built ClientScript.
// PageHostingTests runs it:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfbm node --test plugins/Bookmarks/tests/Jellyfin.Plugin.Bookmarks.Tests/Web/pagedemo.test.mjs
// Without the server's head fragment (EnablePageDemo off, the default) the demo must do nothing at all; with it, the two
// demo pages work through the kit's page host.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage, plain } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');
const SHELL = (key) => '<div class="jfbm-page" data-jfbm-page="' + key + '"><p class="jfbm-shell-note">This page needs the Bookmarks web UI, which is turned off or failed to load.</p></div>';

async function start(options) {
    options = options || {};
    const page = createPage();
    page.context.location.hash = options.hash || '#/home';
    if (options.head) {
        const island = page.element('script', { type: 'application/json', 'data-jfbm-head': '', 'data-jfbm-pages': '' });
        island.textContent = JSON.stringify(Object.assign({ pages: ['demo-a', 'demo-b'], off: [], mode: 'FallbackOnly', nav: true }, options.head));
        page.document.head.append(island);
    }

    page.context.LibraryMenu = { setTitle() {} };
    page.respond('GET', '/Bookmarks/Capabilities', { ClipsEnabled: true, CanCreateClips: true, MaxClipDurationSeconds: 600, DefaultClipMode: 'Accurate', IsAdmin: true, StoreReadOnly: false });
    page.respond('GET', '/Bookmarks/Pages/demo-a', SHELL('demo-a'), 200, { contentType: 'text/html; charset=utf-8' });
    page.respond('GET', '/Bookmarks/Pages/demo-b', options.demoBOff
        ? '<div class="jfbm-page"><p class="jfbm-shell-note">Demo page B is turned off by your server admin.</p></div>'
        : SHELL('demo-b'), 200, { contentType: 'text/html; charset=utf-8' });
    page.run(source, file);
    await page.advance(30);
    page.installApiClient({ userId: 'user-1', user: { Id: 'user-1', Name: 'admin', Policy: { IsAdministrator: true } } });
    await page.advance(60);
    return page;
}

function showFallbackPage(page) {
    const view = page.element('div', { id: 'fallbackPage', 'data-role': 'page', class: 'page', 'data-title': 'Page not found' }, [
        page.element('div', {}, [page.element('h1', {}, ['Page not found'])])
    ]);
    page.document.body.append(view);
    ['viewbeforeshow', 'pagebeforeshow', 'viewshow', 'pageshow'].forEach((type) => page.dispatch(view, type, { detail: { isRestored: false } }));
    return view;
}

function drawer(page) {
    const container = page.element('div', { class: 'mainDrawer-scrollContainer' }, [page.element('div', { class: 'userMenuOptions' })]);
    page.document.body.append(container);
    return container;
}

test('without a head fragment the demo registers nothing and asks nothing (only the My Bookmarks page is known, and since 1.2.0 the My Clips page)', { skip }, async () => {
    const page = await start({ hash: '#/jfbm-demo-a' });
    const kit = page.context.__jfkit.jfbm.instance;
    assert.deepEqual(plain(kit.pages.state().keys), ['bookmarks', 'clips']);
    assert.equal(kit.pages.announced(), false);
    showFallbackPage(page);
    const container = drawer(page);
    await page.advance(600);
    assert.equal(page.document.querySelector('#fallbackPage > [data-jfbm-page]'), null, 'jellyfin-web\'s Page not found stays');
    assert.deepEqual(container.querySelectorAll('.jfkit-nav a').map((a) => a.getAttribute('data-jfbm-nav')), ['bookmarks', 'clips'], 'only the pages\' links');
    assert.equal(page.fetchLog.filter((f) => f.path.startsWith('/Bookmarks/Pages') || f.path.startsWith('/PluginPages')).length, 0);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('with the head fragment of a server whose demo is off (the default since 1.1.0), the demo pages are known but off: no link, their routes show the note', { skip }, async () => {
    const page = await start({ head: { pages: ['bookmarks', 'clips', 'demo-a', 'demo-b'], off: ['demo-a', 'demo-b'] }, hash: '#/jfbm-demo-a', demoBOff: true });
    const kit = page.context.__jfkit.jfbm.instance;
    assert.deepEqual(plain(kit.pages.state().keys), ['demo-a', 'demo-b', 'bookmarks', 'clips']);
    page.respond('GET', '/Bookmarks/Pages/demo-a', '<div class="jfbm-page"><p class="jfbm-shell-note">Demo page A is turned off by your server admin.</p></div>', 200, { contentType: 'text/html; charset=utf-8' });
    showFallbackPage(page);
    const container = drawer(page);
    await page.advance(600);
    const root = page.document.querySelector('#fallbackPage > [data-jfbm-page="demo-a"]');
    assert.equal(root.getAttribute('data-jfbm-state'), 'off');
    assert.equal(root.querySelector('.jfbm-page-note').textContent, 'Demo page A is turned off by your server admin.');
    assert.deepEqual(container.querySelectorAll('.jfkit-nav a').map((a) => a.getAttribute('data-jfbm-nav')), ['bookmarks', 'clips']);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('with the head fragment the two demo pages open in the fallback host, titled, with their data and links', { skip }, async () => {
    const page = await start({ head: {}, hash: '#/jfbm-demo-a?tab=x' });
    const kit = page.context.__jfkit.jfbm.instance;
    assert.deepEqual(plain(kit.pages.state().keys), ['demo-a', 'demo-b']);
    const view = showFallbackPage(page);
    await page.advance(100);
    const root = page.document.querySelector('#fallbackPage > [data-jfbm-page="demo-a"]');
    assert.equal(view.getAttribute('data-title'), 'Demo page A');
    assert.equal(root.getAttribute('data-jfbm-state'), 'on');
    assert.equal(root.querySelector('.jfbm-demo-title').textContent, 'Demo page A');
    assert.equal(root.querySelector('[data-jfbm-demo="host"]').textContent, 'Fallback');
    assert.deepEqual(JSON.parse(root.querySelector('[data-jfbm-demo="params"]').textContent), { tab: 'x' });
    assert.equal(root.querySelector('[data-jfbm-demo="user"]').textContent, 'admin');
    assert.equal(root.querySelector('[data-jfbm-demo="admin"]').textContent, 'true');
    assert.equal(root.querySelector('a[data-jfbm-link="demo-b"]').getAttribute('href'), '#/jfbm-demo-b');
    assert.equal(root.querySelector('a[data-jfbm-demo="self-params"]').getAttribute('href'), '#/jfbm-demo-a?tab=two&n=1');
    const container = drawer(page);
    await page.advance(400);
    const links = container.querySelectorAll('.jfkit-nav a');
    assert.deepEqual(links.map((a) => a.getAttribute('href')), ['#/jfbm-demo-a', '#/jfbm-demo-b']);
    assert.deepEqual(links.map((a) => a.textContent), ['Demo page A', 'Demo page B']);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Demo page B turned off: no nav link, and its route shows the server\'s sentence', { skip }, async () => {
    const page = await start({ head: { off: ['demo-b'] }, hash: '#/jfbm-demo-b', demoBOff: true });
    showFallbackPage(page);
    const container = drawer(page);
    await page.advance(400);
    const root = page.document.querySelector('#fallbackPage > [data-jfbm-page="demo-b"]');
    assert.equal(root.getAttribute('data-jfbm-state'), 'off');
    assert.equal(root.querySelector('.jfbm-page-note').textContent, 'Demo page B is turned off by your server admin.');
    assert.equal(root.querySelector('.jfbm-demo-title'), null);
    assert.deepEqual(container.querySelectorAll('.jfkit-nav a').map((a) => a.getAttribute('data-jfbm-nav')), ['demo-a']);
});
