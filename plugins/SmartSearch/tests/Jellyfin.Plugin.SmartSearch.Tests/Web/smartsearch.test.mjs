// Smart Search's web client, in the kit's fake page. ClientScriptTests runs it with the built ClientScript:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfss node --test plugins/SmartSearch/tests/Jellyfin.Plugin.SmartSearch.Tests/Web/smartsearch.test.mjs
// The script starts, registers the Advanced Search page, renders it on its fallback route (#/jfss-search) once the user
// is known, and links it from "Library tools". The page: the scope bar, the basic form, the query it sends, the hash that
// holds it, the results as cards or rows with Load more, Open and Play, the errors, the TV layout, and the saved
// queries: the tabs, the list, the save, rename, duplicate, share and delete dialogs, a saved query's link and the
// names users give, shown as text.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { createPage } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');
const SHELL = '<div class="jfss-page" data-jfss-page="search"><p class="jfss-shell-note">This page needs the Smart Search web UI, which is turned off or failed to load.</p></div>';
const SHOW_EVENTS = ['viewbeforeshow', 'pagebeforeshow', 'viewshow', 'pageshow'];
const ROOT = '#fallbackPage > [data-jfss-page] .jfss-search';

const MOVIES = 'a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1';
const SHOWS = 'b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2';
const FIELDS = {
    RegistryVersion: 2,
    Fields: [],
    SortKeys: ['SortName', 'ProductionYear', 'PremiereDate', 'DateCreated', 'CommunityRating', 'Random'],
    ItemKinds: ['Movie', 'Series', 'Episode', 'MusicVideo', 'Video', 'BoxSet']
};
const LIBRARIES = [{ Id: MOVIES, Name: 'Movies', CollectionType: 'movies' }, { Id: SHOWS, Name: 'Shows', CollectionType: 'tvshows' }];
const GENRES = [{ Value: 'Horror', Count: 12 }, { Value: 'Science Fiction', Count: 7 }, { Value: 'Comedy', Count: 3 }];

function itemId(n) {
    return String(n).padStart(32, '0');
}

function movie(n) {
    return {
        Id: itemId(n),
        Name: 'Film ' + n,
        Type: 'Movie',
        ProductionYear: 1979,
        RunTimeTicks: 117 * 6e8,
        CommunityRating: 8.5,
        DateCreated: '2024-01-02T00:00:00.0000000Z',
        ImageTags: { Primary: 'tag' + n }
    };
}

// A server for /SmartSearch/Search: `total` items, served Limit a page, all under one token.
function searchServer(total, opts) {
    opts = opts || {};
    return (entry) => {
        const body = entry.body;
        const start = body.StartIndex || 0;
        const limit = body.Limit || 50;
        const items = [];
        for (let i = start; i < Math.min(total, start + limit); i++) {
            items.push(movie(i + 1));
        }

        return {
            Items: items,
            TotalRecordCount: body.IncludeTotal ? total : null,
            StartIndex: start,
            ResultToken: 'token-1',
            HasMore: start + items.length < total,
            ScanInProgress: !!opts.scan,
            Warnings: opts.warnings || [],
            ElapsedMs: 420
        };
    };
}

// A page shaped like jellyfin-web 10.11.5 around our script: the head fragment's data block (FallbackRouteHeadFragment),
// the drawer, LibraryMenu and CustomElements, and the Smart Search endpoints; `hash` is the location before the script
// runs.
function setup(options) {
    options = options || {};
    const page = createPage();
    page.context.location.hash = options.hash || '#/home';
    if (options.tv) {
        page.document.documentElement.classList.add('layout-tv');
    }

    const island = page.element('script', { type: 'application/json', 'data-jfss-head': '', 'data-jfss-pages': '' });
    island.textContent = JSON.stringify(Object.assign({ pages: ['search'], off: [], mode: 'FallbackOnly', nav: true }, options.head || {}));
    page.document.head.append(island);
    const titles = [];
    page.context.LibraryMenu = { setTitle: (t) => titles.push(t) };
    page.context.CustomElements = { upgradeSubtree: () => {} };
    if (options.pluginPages) {
        page.context.PluginPages = {};
    }

    page.respond('GET', '/SmartSearch/Pages/search', options.shell || SHELL, 200, { contentType: 'text/html; charset=utf-8' });
    page.respond('GET', '/SmartSearch/Fields', options.fields || FIELDS);
    page.respond('GET', '/SmartSearch/Libraries', LIBRARIES);
    page.respond('GET', '/SmartSearch/Values/Genres', (entry) => {
        const prefix = (entry.query.prefix || '').toLowerCase();
        return GENRES.filter((g) => g.Value.toLowerCase().indexOf(prefix) >= 0);
    });
    page.respond('POST', '/SmartSearch/Search', options.search || searchServer(3));
    const db = options.saved ? savedServer(page, options.saved) : null;
    const drawer = page.element('div', { class: 'mainDrawer-scrollContainer' }, [page.element('div', { class: 'userMenuOptions' })]);
    page.document.body.append(drawer);
    return { page, titles, drawer, db };
}

// FallbackRoute's page as React renders it: "Page not found" content and the four show events.
function showFallbackPage(page) {
    const view = page.element('div', { id: 'fallbackPage', 'data-role': 'page', class: 'page', 'data-title': 'Page not found' }, [
        page.element('div', { class: 'notFound' }, [page.element('h1', {}, ['Page not found'])])
    ]);
    page.document.body.append(view);
    SHOW_EVENTS.forEach((type) => page.dispatch(view, type, { detail: { isRestored: false } }));
    return view;
}

function helpers(s, rootSelector) {
    s.root = s.page.document.querySelector(rootSelector || ROOT);
    s.q = (selector) => s.root.querySelector(selector);
    s.all = (selector) => s.root.querySelectorAll(selector);
    s.searches = () => s.page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/SmartSearch/Search');
    s.params = () => Object.fromEntries(new URLSearchParams(s.page.context.location.hash.split('?')[1] || '').entries());
    s.dialogs = () => s.page.document.querySelectorAll('[role="dialog"]');
    s.dialog = () => s.dialogs()[s.dialogs().length - 1] || null;
    s.button = (root, text) => root.querySelectorAll('button').find((b) => b.textContent === text) || null;
    s.toasts = () => s.page.document.querySelectorAll('.jfss-toast').map((t) => t.firstChild.textContent);
    s.change = (node, value) => {
        node.value = value;
        s.page.dispatch(node, 'change');
    };
    s.calls = (method, pattern) => s.page.fetchLog.filter((f) => f.method === method && (pattern instanceof RegExp ? pattern.test(f.path) : f.path === pattern));
    return s;
}

// The page shown on its fallback route, signed in.
async function shown(options) {
    const s = setup(Object.assign({ hash: '#/jfss-search' }, options || {}));
    s.page.run(source, file);
    await s.page.advance(5);
    s.view = showFallbackPage(s.page);
    s.page.installApiClient({ userId: 'user-1', items: {} });
    await s.page.advance(400);
    return helpers(s);
}

function type(s, input, text) {
    input.value = text;
    s.page.dispatch(input, 'input');
}

function pressed(s, selector) {
    return s.all(selector).filter((b) => b.getAttribute('aria-pressed') === 'true').map((b) => b.textContent);
}

// ------------------------------------------------------------------------------------------------ start and page host

test('the client script starts in the page without errors and registers its kit', { skip }, async () => {
    const s = setup();
    s.page.run(source, file);
    await s.page.advance(50);
    s.page.installApiClient({ userId: 'user-1', items: {} });
    await s.page.advance(100);
    assert.deepEqual(s.page.errors, []);
    assert.ok(s.page.context.__jfkit && s.page.context.__jfkit.jfss, 'window.__jfkit.jfss');
    assert.deepEqual(JSON.parse(JSON.stringify(s.page.context.__jfkit.jfss.instance.pages.state().keys)), ['search']);
});

test('#/jfss-search renders the Advanced Search page under its title: the scope bar and the basic form, after the shell check', { skip }, async () => {
    const s = await shown();
    const root = s.page.document.querySelector('#fallbackPage > [data-jfss-page]');
    assert.ok(root, 'the page root in FallbackRoute\'s page');
    assert.equal(root.getAttribute('data-jfss-page'), 'search');
    assert.equal(root.getAttribute('data-jfss-state'), 'on');
    assert.equal(root.getAttribute('data-jfss-host'), 'Fallback');
    assert.ok(s.titles.includes('Advanced Search'), 'the title: ' + JSON.stringify(s.titles));
    assert.deepEqual(s.page.fetchLog.map((f) => f.method + ' ' + f.path).slice(0, 3), ['GET /SmartSearch/Pages/search', 'GET /SmartSearch/Fields', 'GET /SmartSearch/Libraries']);
    assert.ok(s.q('.jfss-scope') && s.q('.jfss-basic') && s.q('.jfss-results-section'));
    assert.equal(s.searches().length, 0, 'no search before the user asks');
    assert.equal(s.page.document.activeElement, s.q('.jfss-title'), 'the title field has the focus on a desktop');
    for (const selector of ['.jfss-title', '.jfss-library', '.jfss-genre-input', '.jfss-sort', '.jfss-order']) {
        const input = s.q(selector);
        assert.ok(input.id && s.root.querySelector('label[for="' + input.id + '"]'), selector + ' has a label');
    }

    assert.ok(s.q('.jfss-year-from').getAttribute('aria-label') && s.q('.jfss-year-to').getAttribute('aria-label'));
    assert.deepEqual(s.page.errors, []);
});

test('"Library tools" links the page at #/jfss-search, after Bookmarks\' and Tag Cloud\'s', { skip }, async () => {
    const s = setup();
    s.page.run(source, file);
    await s.page.advance(50);
    s.page.installApiClient({ userId: 'user-1', items: {} });
    await s.page.advance(300);
    const link = s.page.document.querySelector('.jfkit-nav a[data-jfkit-plugin="jfss"]');
    assert.ok(link, 'the nav link');
    assert.equal(link.getAttribute('href'), '#/jfss-search');
    assert.equal(link.getAttribute('data-order'), '40');
    assert.match(link.textContent, /Advanced Search/);
});

test('a page the server turned off shows the shell\'s sentence and no link', { skip }, async () => {
    const s = setup({ hash: '#/jfss-search', head: { off: ['search'] }, shell: '<p>Advanced Search is turned off by your server admin.</p>' });
    s.page.run(source, file);
    await s.page.advance(5);
    showFallbackPage(s.page);
    s.page.installApiClient({ userId: 'user-1', items: {} });
    await s.page.advance(300);
    const root = s.page.document.querySelector('#fallbackPage > [data-jfss-page]');
    assert.equal(root.getAttribute('data-jfss-state'), 'off');
    assert.equal(root.querySelector('.jfss-search'), null);
    assert.match(root.textContent, /Advanced Search is turned off by your server admin\./);
    assert.equal(s.page.document.querySelector('.jfkit-nav a[data-jfkit-plugin="jfss"]'), null);
});

test('a lookup the page needs failing shows an error with Retry, which renders the page again', { skip }, async () => {
    const s = setup({ hash: '#/jfss-search' });
    s.page.respond('GET', '/SmartSearch/Libraries', { title: 'Boom', status: 500 }, 500);
    s.page.run(source, file);
    await s.page.advance(5);
    showFallbackPage(s.page);
    s.page.installApiClient({ userId: 'user-1', items: {} });
    await s.page.advance(300);
    const root = s.page.document.querySelector(ROOT);
    assert.match(root.textContent, /couldn't load what it needs from the server: Boom/);
    s.page.respond('GET', '/SmartSearch/Libraries', LIBRARIES);
    root.querySelector('.jfss-notice-action').click();
    await s.page.advance(300);
    assert.ok(s.page.document.querySelector(ROOT + ' .jfss-scope'), 'the page after Retry');
});

// ------------------------------------------------------------------------------------------------ the scope bar

test('the scope bar: the server\'s kinds as toggles (Movies and Shows by default), the libraries, missing episodes with Episodes only, one kind always kept', { skip }, async () => {
    const s = await shown({ fields: Object.assign({}, FIELDS, { ItemKinds: ['Movie', 'Series', 'Episode'] }) });
    assert.deepEqual(s.all('.jfss-kind').map((b) => b.textContent), ['Movies', 'Shows', 'Episodes']);
    assert.deepEqual(pressed(s, '.jfss-kind'), ['Movies', 'Shows']);
    assert.equal(s.q('.jfss-kinds').getAttribute('role'), 'group');
    assert.deepEqual(s.all('.jfss-library option').map((o) => [o.value, o.textContent]), [['', 'All my libraries'], [MOVIES, 'Movies'], [SHOWS, 'Shows']]);
    assert.equal(s.q('.jfss-missing').classList.contains('hide'), true, 'no missing episodes without Episodes');
    const episodes = s.all('.jfss-kind')[2];
    episodes.click();
    assert.equal(s.q('.jfss-missing').classList.contains('hide'), false);
    episodes.click();
    s.all('.jfss-kind')[1].click();
    assert.deepEqual(pressed(s, '.jfss-kind'), ['Movies']);
    s.all('.jfss-kind')[0].click();
    assert.deepEqual(pressed(s, '.jfss-kind'), ['Movies'], 'the last kind stays');
    assert.equal(s.q('.jfss-kinds-note').textContent, 'Keep at least one item type.');
});

// ------------------------------------------------------------------------------------------------ the basic search

test('Search sends the basic form as one All group with the scope and the sort, writes the hash, and remembers the scope', { skip }, async () => {
    const s = await shown();
    s.all('.jfss-kind')[1].click(); // Shows off
    s.q('.jfss-library').value = MOVIES;
    s.page.dispatch(s.q('.jfss-library'), 'change');
    type(s, s.q('.jfss-title'), '  Alien ');
    s.q('.jfss-year-from').value = '1979';
    s.q('.jfss-year-to').value = '1986';
    // A genre from the suggestions (the chosen library's), one typed.
    s.page.dispatch(s.q('.jfss-genre-input'), 'focus');
    type(s, s.q('.jfss-genre-input'), 'hor');
    await s.page.advance(300);
    const values = s.page.fetchLog.filter((f) => f.path === '/SmartSearch/Values/Genres').pop();
    assert.deepEqual(values.query, { prefix: 'hor', libraryIds: MOVIES, limit: '8' });
    assert.deepEqual(s.all('.jfss-suggestion').map((b) => b.textContent), ['Horror (12)']);
    s.q('.jfss-suggestion').click();
    type(s, s.q('.jfss-genre-input'), 'Thriller');
    s.page.key(s.q('.jfss-genre-input'), 'Enter');
    await s.page.advance(300);
    assert.deepEqual(s.all('.jfss-chip').map((c) => c.textContent), ['Horror', 'Thriller']);
    assert.equal(s.q('.jfss-chip').getAttribute('aria-label'), 'Remove Horror');
    assert.equal(s.searches().length, 0, 'Enter with a typed genre adds it, and doesn\'t search');

    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    const sent = s.searches();
    assert.equal(sent.length, 1);
    assert.deepEqual(sent[0].body, {
        Query: {
            Version: 1,
            Scope: { ItemKinds: ['Movie'], LibraryIds: [MOVIES], IncludeMissing: false },
            Where: {
                Match: 'All',
                Children: [
                    { Field: 'Title', Op: 'Contains', Value: 'Alien' },
                    { Field: 'Year', Op: 'Between', From: 1979, To: 1986 },
                    { Field: 'Genres', Op: 'AnyOf', Values: ['Horror', 'Thriller'] }
                ]
            },
            Sort: [{ Key: 'SortName', Order: 'Ascending' }]
        },
        StartIndex: 0,
        Limit: 50,
        IncludeTotal: true
    });
    assert.deepEqual(s.params(), { kinds: 'Movie', lib: MOVIES, q: 'Alien', from: '1979', to: '1986', genres: 'Horror,Thriller', run: '1' });
    assert.deepEqual(JSON.parse(s.page.context.localStorage.getItem('jfss:lastScope')), { kinds: ['Movie'], lib: MOVIES });
    // A chip removes its genre.
    s.q('.jfss-chip').click();
    assert.deepEqual(s.all('.jfss-chip').map((c) => c.textContent), ['Thriller']);
    assert.deepEqual(s.page.errors, []);
});

test('the years: one year is Eq, an open end Ge or Le; a year that isn\'t 1800 to 2100, or years the wrong way round, stop the search under the field', { skip }, async () => {
    const s = await shown();
    const run = async (from, to) => {
        s.q('.jfss-year-from').value = from;
        s.q('.jfss-year-to').value = to;
        s.page.key(s.q('.jfss-year-to'), 'Enter');
        await s.page.advance(100);
        const last = s.searches().pop();
        return last ? last.body.Query.Where.Children : null;
    };
    assert.deepEqual(await run('2001', '2001'), [{ Field: 'Year', Op: 'Eq', Value: 2001 }]);
    assert.deepEqual(await run('2001', ''), [{ Field: 'Year', Op: 'Ge', Value: 2001 }]);
    assert.deepEqual(await run('', '1990'), [{ Field: 'Year', Op: 'Le', Value: 1990 }]);
    const before = s.searches().length;
    await run('1700', '');
    assert.equal(s.searches().length, before, 'not sent');
    assert.equal(s.q('[data-jfss-error="years"]').textContent, 'A year is a whole number from 1800 to 2100.');
    assert.equal(s.q('[data-jfss-error="years"]').getAttribute('role'), 'alert');
    await run('2000', '1990');
    assert.equal(s.searches().length, before);
    assert.equal(s.q('[data-jfss-error="years"]').textContent, 'The first year is after the second.');
    await run('', '');
    assert.equal(s.searches().length, before + 1, 'no year rule: sent');
    assert.equal(s.q('[data-jfss-error="years"]').textContent, '');
});

test('Enter in the title searches; Reset empties the fields, keeps the scope and drops the results and run from the hash', { skip }, async () => {
    const s = await shown();
    type(s, s.q('.jfss-title'), 'heat');
    s.page.key(s.q('.jfss-title'), 'Enter');
    await s.page.advance(100);
    assert.equal(s.searches().length, 1);
    assert.equal(s.all('.jfss-card').length, 3);
    s.q('.jfss-reset').click();
    await s.page.advance(50);
    assert.equal(s.q('.jfss-title').value, '');
    assert.equal(s.all('.jfss-card').length, 0);
    assert.equal(s.q('.jfss-results-bar').classList.contains('hide'), true);
    assert.deepEqual(s.params(), { kinds: 'Movie,Series' });
    assert.deepEqual(pressed(s, '.jfss-kind'), ['Movies', 'Shows']);
});

// ------------------------------------------------------------------------------------------------ the hash

test('a deep link runs its search as the page shows, with the form filled from the hash; a wrong value is left out', { skip }, async () => {
    const s = await shown({ hash: '#/jfss-search?kinds=Series,Nope&lib=' + SHOWS + '&q=Lost&from=2004&to=abc&genres=Drama,Science%2520Fiction&sort=ProductionYear&order=desc&run=1' });
    assert.equal(s.searches().length, 1);
    const query = s.searches()[0].body.Query;
    assert.deepEqual(query.Scope, { ItemKinds: ['Series'], LibraryIds: [SHOWS], IncludeMissing: false });
    assert.deepEqual(query.Where.Children, [
        { Field: 'Title', Op: 'Contains', Value: 'Lost' },
        { Field: 'Year', Op: 'Ge', Value: 2004 },
        { Field: 'Genres', Op: 'AnyOf', Values: ['Drama', 'Science Fiction'] }
    ]);
    assert.deepEqual(query.Sort, [{ Key: 'ProductionYear', Order: 'Descending' }]);
    assert.equal(s.q('.jfss-title').value, 'Lost');
    assert.equal(s.q('.jfss-library').value, SHOWS);
    assert.equal(s.q('.jfss-sort').value, 'ProductionYear');
    assert.equal(s.q('.jfss-order').value, 'desc');
    assert.deepEqual(pressed(s, '.jfss-kind'), ['Shows']);
    assert.notEqual(s.page.document.activeElement, s.q('.jfss-title'), 'no focus taken from the results');
});

test('q alone (the native search page\'s link) fills the title and runs; a library the user can\'t open is all of them', { skip }, async () => {
    const s = await shown({ hash: '#/jfss-search?q=matrix&lib=ffffffffffffffffffffffffffffffff' });
    assert.equal(s.searches().length, 1);
    const query = s.searches()[0].body.Query;
    assert.deepEqual(query.Where.Children, [{ Field: 'Title', Op: 'Contains', Value: 'matrix' }]);
    assert.deepEqual(query.Scope.LibraryIds, []);
    assert.deepEqual(query.Scope.ItemKinds, ['Movie', 'Series']);
});

test('a page opened bare takes the last scope; tab and query stay in the hash through a search; Back renders the earlier search again', { skip }, async () => {
    const s = setup({ hash: '#/jfss-search?tab=search&query=0123456789abcdef0123456789abcdef' });
    s.page.context.localStorage.setItem('jfss:lastScope', JSON.stringify({ kinds: ['Episode'], lib: SHOWS }));
    s.page.run(source, file);
    await s.page.advance(5);
    showFallbackPage(s.page);
    s.page.installApiClient({ userId: 'user-1', items: {} });
    await s.page.advance(400);
    helpers(s);
    assert.deepEqual(pressed(s, '.jfss-kind'), ['Episodes']);
    assert.equal(s.q('.jfss-library').value, SHOWS);
    s.q('.jfss-title').value = 'one';
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    const first = s.page.context.location.hash;
    assert.match(first, /^#\/jfss-search\?tab=search&query=0123456789abcdef0123456789abcdef&kinds=Episode&lib=b2b2/);
    s.q('.jfss-title').value = 'two';
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.match(s.page.context.location.hash, /q=two/);
    // Back: the location is the first search's again, and the kit renders the page from it.
    s.page.context.location.hash = first;
    s.page.window.dispatchEvent(new s.page.Event('popstate'));
    await s.page.advance(400);
    helpers(s);
    assert.equal(s.q('.jfss-title').value, 'one');
    assert.deepEqual(s.searches().map((f) => f.body.Query.Where.Children[0].Value), ['one', 'two', 'one']);
});

test('in Plugin Pages\' host the search parameters after pageUrl reach the page, and a second link with other ones renders it again', { skip }, async () => {
    const s = setup({ hash: '#/home', pluginPages: true });
    s.page.run(source, file);
    await s.page.advance(5);
    s.page.installApiClient({ userId: 'user-1', items: {} });
    await s.page.advance(60);
    const show = async (hash, restored) => {
        s.page.context.location.hash = hash;
        s.page.window.dispatchEvent(new s.page.Event('popstate'));
        await s.page.advance(10);
        let host = s.page.document.getElementById('userPluginPreferencesPage');
        if (!host) {
            host = s.page.element('div', { id: 'userPluginPreferencesPage', 'data-role': 'page', class: 'page libraryPage userPreferencesPage', 'data-title': 'Home' }, [
                s.page.element('div', { class: 'userPluginSettingsContainer' }, [s.page.element('div', { class: 'jfss-page', 'data-jfss-page': 'search' })])
            ]);
            s.page.document.body.append(host);
        }

        SHOW_EVENTS.forEach((t) => s.page.dispatch(host, t, { detail: { isRestored: !!restored } }));
        await s.page.advance(400);
        return host;
    };
    const base = '#/userpluginsettings.html?pageUrl=%2FSmartSearch%2FPages%2Fsearch';
    const host = await show(base + '&tab=search&query=aaaa&q=alien&run=1');
    assert.equal(host.querySelector('[data-jfss-page]').getAttribute('data-jfss-host'), 'PluginPages');
    await show(base + '&tab=search&query=bbbb&q=heat&run=1', true);
    helpers(s, '#userPluginPreferencesPage .jfss-search');
    assert.deepEqual(s.searches().map((f) => f.body.Query.Where.Children[0].Value), ['alien', 'heat']);
    assert.equal(s.q('.jfss-title').value, 'heat');
    // A search from the page keeps Plugin Pages' link, its pageUrl first, and the saved query's parameters.
    s.q('.jfss-title').value = 'ronin';
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.match(s.page.context.location.hash, /^#\/userpluginsettings\.html\?pageUrl=%2FSmartSearch%2FPages%2Fsearch&tab=search&query=bbbb&kinds=Movie%2CSeries&q=ronin&run=1$/);
    assert.deepEqual(s.page.errors, []);
});

// ------------------------------------------------------------------------------------------------ the results

test('the results: the count and time, cards linking to the details page with Open and Play, Load more with the token, the list view with its facts', { skip }, async () => {
    const s = await shown({ search: searchServer(60) });
    s.page.respond('GET', '/Sessions', [{ Id: 'session-1', DeviceId: 'device-1' }]);
    s.page.respond('POST', '/Sessions/session-1/Playing', '');
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.equal(s.q('.jfss-count').textContent, '60 results · 0.42 s');
    assert.equal(s.q('.jfss-count').getAttribute('role'), 'status');
    const cards = s.all('.jfss-card');
    assert.equal(cards.length, 50);
    assert.equal(cards[0].getAttribute('data-id'), itemId(1));
    assert.match(cards[0].querySelector('a').getAttribute('href'), /^#\/details\?id=0{31}1&serverId=server-1$/);
    assert.match(cards[0].textContent, /Film 1/);
    assert.equal(cards[0].querySelector('.jfss-open').getAttribute('aria-label'), 'Open Film 1');
    assert.equal(cards[0].querySelector('.jfss-open').getAttribute('href'), cards[0].querySelector('a').getAttribute('href'));
    assert.equal(cards[0].querySelector('.jfss-play').getAttribute('aria-label'), 'Play Film 1');

    // Load more: the next 10 through the first page's token.
    s.q('.jfss-pager-more').click();
    await s.page.advance(100);
    const second = s.searches()[1].body;
    assert.deepEqual([second.StartIndex, second.Limit, second.ResultToken, second.IncludeTotal], [50, 50, 'token-1', false]);
    assert.equal(s.all('.jfss-card').length, 60);

    // Play: the kit's playback through this browser's session.
    s.all('.jfss-card')[59].querySelector('.jfss-play').click();
    await s.page.advance(50);
    const play = s.page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Sessions/session-1/Playing')[0];
    assert.ok(play, 'the play request');
    assert.equal(play.query.itemIds, itemId(60));

    // The list view: rows with the kind (the year is the kit's second line), runtime, rating and date added; remembered for the next page.
    s.all('.jfss-view')[1].click();
    const rows = s.all('.jfss-row');
    assert.equal(rows.length, 60);
    assert.equal(s.all('.jfss-card').length, 0);
    assert.match(rows[0].querySelector('.jfss-facts').textContent, /^Movie · 1 h 57 min · ★ 8\.5 · Added /);
    assert.equal(rows[0].querySelector('.jfss-open').getAttribute('aria-label'), 'Open Film 1');
    assert.deepEqual(pressed(s, '.jfss-view'), ['List']);
    assert.match(s.page.context.localStorage.getItem('jfss:view'), /list/);
    assert.deepEqual(s.page.errors, []);
});

test('a sort or order change searches again with it and keeps it in the hash', { skip }, async () => {
    const s = await shown();
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.deepEqual(s.all('.jfss-sort option').map((o) => o.textContent), ['Title', 'Year', 'Release date', 'Date added', 'Community rating', 'Random']);
    s.q('.jfss-sort').value = 'CommunityRating';
    s.page.dispatch(s.q('.jfss-sort'), 'change');
    await s.page.advance(100);
    s.q('.jfss-order').value = 'desc';
    s.page.dispatch(s.q('.jfss-order'), 'change');
    await s.page.advance(100);
    assert.deepEqual(s.searches().map((f) => f.body.Query.Sort[0]), [
        { Key: 'SortName', Order: 'Ascending' },
        { Key: 'CommunityRating', Order: 'Ascending' },
        { Key: 'CommunityRating', Order: 'Descending' }
    ]);
    assert.equal(s.params().sort, 'CommunityRating');
    assert.equal(s.params().order, 'desc');
});

test('no results, a scan running and a library not available each say so', { skip }, async () => {
    const s = await shown({ search: searchServer(0, { scan: true, warnings: ['LibrariesUnavailable'] }) });
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.deepEqual(s.all('.jfss-notice').map((n) => n.textContent), [
        'A library scan is running, so new items may be missing.',
        'A library in this search isn\'t available to you; the search ran over the others.',
        'No items match these rules.'
    ]);
    assert.equal(s.q('.jfss-count').textContent, '0 results · 0.42 s');
});

test('the server\'s refusals: a rule\'s error under its field, too broad with the count and an episodes hint, Timeout and Busy with Retry', { skip }, async () => {
    const s = await shown();
    const refuse = (status, problem) => s.page.respond('POST', '/SmartSearch/Search', problem, status);
    type(s, s.q('.jfss-title'), 'x');
    refuse(400, { title: 'Invalid query', status: 400, code: 'InvalidQuery', errors: [{ path: 'Where.Children[0].Value', code: 'TooLong', message: 'The text is too long.' }] });
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.equal(s.q('[data-jfss-error="q"]').textContent, 'The text is too long.');

    refuse(400, { title: 'Too broad', status: 400, code: 'QueryTooBroad', candidateCount: 148210, kinds: ['Movie', 'Series'] });
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.equal(s.q('[data-jfss-error="q"]').textContent, '', 'the earlier error cleared');
    assert.match(s.q('.jfss-notice').textContent, /^This search would check 148,210 items\. Add a library, type, year or genre rule\.$/);

    refuse(400, { title: 'Too broad', status: 400, code: 'QueryTooBroad', candidateCount: 120000, kinds: ['Episode'] });
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.match(s.q('.jfss-notice').textContent, /^This search would check 120,000 episodes\./);

    refuse(503, { title: 'Busy', status: 503, code: 'Busy' });
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.match(s.q('.jfss-notice').textContent, /^The server is busy with other searches\./);
    refuse(503, { title: 'Timeout', status: 503, code: 'Timeout' });
    s.q('.jfss-notice-action').click();
    await s.page.advance(100);
    assert.match(s.q('.jfss-notice').textContent, /^The search took too long\./);
    s.page.respond('POST', '/SmartSearch/Search', searchServer(2));
    s.q('.jfss-notice-action').click();
    await s.page.advance(100);
    assert.equal(s.all('.jfss-card').length, 2, 'Retry searched again');
    assert.equal(s.all('.jfss-notice').length, 0);
});

test('a newer search wins over an older one still on its way', { skip }, async () => {
    const s = await shown({ search: searchServer(5) });
    s.page.respond('POST', '/SmartSearch/Search', null, 200, { hang: true });
    s.q('.jfss-search-button').click();
    await s.page.advance(10);
    s.page.responses.shift();
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.equal(s.searches().length, 2);
    assert.equal(s.all('.jfss-card').length, 5);
    assert.equal(s.all('.jfss-notice').length, 0, 'the aborted search says nothing');
    assert.deepEqual(s.page.errors, []);
});

// ------------------------------------------------------------------------------------------------ the TV layout

test('on the TV layout the root carries jfss-tv, no field takes the focus, and Load more waits for the remote', { skip }, async () => {
    const s = await shown({ tv: true, search: searchServer(60) });
    assert.equal(s.root.classList.contains('jfss-tv'), true);
    assert.notEqual(s.page.document.activeElement, s.q('.jfss-title'));
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.equal(s.all('.jfss-card').length, 50);
    assert.equal(s.searches().length, 1);
    s.q('.jfss-pager-more').click();
    await s.page.advance(100);
    assert.equal(s.all('.jfss-card').length, 60);
    assert.deepEqual(s.page.errors, []);
});

test('ArrowDown in the genre field goes to the first suggestion', { skip }, async () => {
    const s = await shown();
    s.page.dispatch(s.q('.jfss-genre-input'), 'focus');
    await s.page.advance(300);
    assert.deepEqual(s.all('.jfss-suggestion').map((b) => b.getAttribute('data-jfss-genre')), ['Horror', 'Science Fiction', 'Comedy']);
    const notPrevented = s.page.key(s.q('.jfss-genre-input'), 'ArrowDown');
    assert.equal(s.page.document.activeElement, s.q('.jfss-suggestion'));
    assert.equal(notPrevented, false, 'default prevented');
});

// ------------------------------------------------------------------------------------------------ saved queries

const QA = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01';
const QB = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02';
const QC = 'cccccccccccccccccccccccccccccc03';
const BOB = 'dddddddddddddddddddddddddddddd04';
const HIDDEN_LIB = 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeee05';

// A definition as the server stores it: one All group (Negate written), a title rule, Movies, by year descending.
function basicQuery(title, extra) {
    return Object.assign({
        Version: 1,
        Scope: { ItemKinds: ['Movie'], LibraryIds: [MOVIES], IncludeMissing: false },
        Where: { Match: 'All', Negate: false, Children: [{ Field: 'Title', Op: 'Contains', Value: title || 'alien', Negate: false }] },
        Sort: [{ Key: 'ProductionYear', Order: 'Descending' }]
    }, extra || {});
}

function savedQuery(id, name, extra) {
    return Object.assign({
        Id: id, Name: name, Description: '', OwnerUserId: null, OwnerName: 'me', OwnerDeleted: false, IsOwner: true, CanEdit: true,
        Shared: false, Query: basicQuery(), QueryHash: 'sha256:00', Supported: true, Valid: true, Errors: [], UsesPerUserFields: false,
        UsesAdminOnlyFields: false, CreatedUtc: '2026-11-01T10:00:00.0000000Z', UpdatedUtc: '2026-11-02T10:00:00.0000000Z', LastRunUtc: null,
        AutoCollectionCount: 0, AffectedAutoCollections: []
    }, extra || {});
}

function bobsQuery(extra) {
    return savedQuery(QC, 'Bob\'s picks', Object.assign({ OwnerName: 'bob', IsOwner: false, CanEdit: false, Shared: true, Description: 'Old films' }, extra || {}));
}

// The saved-query endpoints over an in-memory list (db.queries; db.referenced: ids auto-collections use; db.refusing: id
// to the code a change of it is refused with, BreaksAutoCollections or ManagerRequired, unless DetachAutoCollections),
// answering as SmartSearchQueriesController does: 404 QueryNotFound, 409 DuplicateName, IfUpdatedAt and Referenced, 201
// and 204.
function savedServer(page, queries) {
    const db = { queries: queries.map((q) => JSON.parse(JSON.stringify(q))), n: 0, referenced: new Set(), refusing: new Map() };
    const fail = (status, code, title, extra) => ({ status, body: Object.assign({ status, code, title }, extra || {}) });
    const idOf = (entry) => entry.path.split('/')[3];
    const find = (entry) => db.queries.find((q) => q.Id === idOf(entry));
    const stamp = () => '2026-11-10T00:00:' + String(++db.n).padStart(2, '0') + '.0000000Z';
    const taken = (name, except) => db.queries.some((q) => q.IsOwner && q.Id !== except && q.Name.toLowerCase() === name.toLowerCase());
    const summary = (q) => {
        const s = Object.assign({}, q);
        ['Query', 'QueryHash', 'Errors', 'UsesPerUserFields', 'UsesAdminOnlyFields', 'CreatedUtc', 'LastRunUtc', 'CanEdit', 'AffectedAutoCollections'].forEach((k) => delete s[k]);
        return s;
    };
    // One handler gives the status and the body: the status runs first (fake-dom), the body reads what it decided.
    const route = (method, path, handler) => {
        let last = null;
        page.respond(method, path, () => last.body, (entry) => {
            last = handler(entry);
            return last.status;
        });
    };
    route('GET', '/SmartSearch/Queries', (entry) => {
        const scope = entry.query.scope || 'All';
        const items = db.queries.filter((q) => scope === 'All' || (scope === 'Mine') === q.IsOwner).map(summary)
            .sort((a, b) => a.Name.toLowerCase() < b.Name.toLowerCase() ? -1 : 1);
        return { status: 200, body: { Items: items, TotalRecordCount: items.length, StartIndex: 0 } };
    });
    route('GET', /^\/SmartSearch\/Queries\/[^/]+$/, (entry) => {
        const q = find(entry);
        return q ? { status: 200, body: q } : fail(404, 'QueryNotFound', 'No saved query has this id.');
    });
    route('POST', '/SmartSearch/Queries', (entry) => {
        if (taken(entry.body.Name)) {
            return fail(409, 'DuplicateName', 'There is already a saved query of this name.');
        }

        const q = savedQuery('f'.repeat(30) + String(++db.n).padStart(2, '0'), entry.body.Name, {
            Description: entry.body.Description, Shared: entry.body.Shared, Query: entry.body.Query, UpdatedUtc: stamp()
        });
        db.queries.push(q);
        return { status: 201, body: q };
    });
    route('PUT', /^\/SmartSearch\/Queries\/[^/]+$/, (entry) => {
        const q = find(entry);
        if (!q) {
            return fail(404, 'QueryNotFound', 'No saved query has this id.');
        }

        if (entry.body.IfUpdatedAt !== q.UpdatedUtc) {
            return fail(409, 'IfUpdatedAt', 'The query changed since you read it.');
        }

        if (taken(entry.body.Name, q.Id)) {
            return fail(409, 'DuplicateName', 'There is already a saved query of this name.');
        }

        const refusal = db.refusing.get(q.Id);
        if (refusal && entry.body.DetachAutoCollections !== true) {
            return fail(refusal === 'ManagerRequired' ? 403 : 409, refusal, refusal === 'ManagerRequired' ? 'Only a collection manager can change what these auto-collections contain.' : 'This change would stop auto-collections that use this query.', {
                autoCollections: [{ id: 'ac1', name: 'Horror 80s', messages: ["A collection can't hold collections."] }, { id: 'ac2', name: 'Sci-fi', messages: [] }]
            });
        }

        db.refusing.delete(q.Id);
        Object.assign(q, { Name: entry.body.Name, Description: entry.body.Description, Shared: entry.body.Shared, Query: entry.body.Query, UpdatedUtc: stamp() });
        return { status: 200, body: q };
    });
    route('POST', /^\/SmartSearch\/Queries\/[^/]+\/Duplicate$/, (entry) => {
        const source = find(entry);
        let name = entry.body && entry.body.Name;
        if (!name) {
            name = source.Name;
            for (let n = 2; taken(name); n++) {
                name = source.Name + ' (' + n + ')';
            }
        } else if (taken(name)) {
            return fail(409, 'DuplicateName', 'There is already a saved query of this name.');
        }

        const copy = Object.assign(JSON.parse(JSON.stringify(source)), { Id: 'c'.repeat(30) + String(++db.n).padStart(2, '0'), Name: name, OwnerUserId: null, OwnerName: 'me', IsOwner: true, CanEdit: true, Shared: false, UpdatedUtc: stamp() });
        db.queries.push(copy);
        return { status: 201, body: copy };
    });
    route('DELETE', /^\/SmartSearch\/Queries\/[^/]+$/, (entry) => {
        const q = find(entry);
        if (db.referenced.has(q.Id) && entry.query.detachAutoCollections !== 'true') {
            return fail(409, 'Referenced', 'Auto-collections use this query.', { autoCollections: [{ id: 'ac1', name: 'Horror 80s', messages: [] }] });
        }

        db.queries = db.queries.filter((x) => x !== q);
        return { status: 204, body: '' };
    });
    route('POST', /^\/SmartSearch\/Queries\/[^/]+\/Search$/, (entry) => ({ status: 200, body: searchServer(2)(entry) }));
    return db;
}

// Fills a dialog's form and submits it.
function submitDialog(s, fields) {
    const dialog = s.dialog();
    Object.keys(fields || {}).forEach((selector) => {
        const node = dialog.querySelector(selector);
        if (typeof fields[selector] === 'boolean') {
            node.checked = fields[selector];
        } else {
            node.value = fields[selector];
        }
    });
    s.page.dispatch(dialog.querySelector('form'), 'submit');
}

test('the tabs: Search and Saved queries; the Saved tab lists the user\'s queries and those shared with them, filtered by Mine, Shared and a text, all kept in the hash', { skip }, async () => {
    const s = await shown({ saved: [
        savedQuery(QA, 'Alien nights', { Shared: true, AutoCollectionCount: 2 }),
        bobsQuery(),
        savedQuery(QB, 'Deleted one', { OwnerUserId: null, OwnerName: 'Deleted user', OwnerDeleted: true, IsOwner: false, CanEdit: false, Shared: true })
    ] });
    const tabs = s.all('[role="tab"]');
    assert.equal(s.q('[role="tablist"]').getAttribute('aria-label'), 'Advanced Search sections');
    assert.deepEqual(tabs.map((t) => [t.textContent, t.getAttribute('aria-selected')]), [['Search', 'true'], ['Saved queries', 'false']]);
    assert.equal(s.calls('GET', '/SmartSearch/Queries').length, 0, 'the list is read when its tab shows');
    tabs[1].click();
    await s.page.advance(50);
    assert.deepEqual(tabs.map((t) => t.getAttribute('aria-selected')), ['false', 'true']);
    assert.equal(s.q('.jfss-search-panel').classList.contains('hide'), true);
    assert.equal(s.q('.jfss-saved-panel').getAttribute('role'), 'tabpanel');
    assert.equal(s.q('.jfss-saved-panel').getAttribute('aria-labelledby'), tabs[1].id);
    assert.deepEqual(s.params(), { tab: 'saved', kinds: 'Movie,Series' });
    assert.deepEqual(s.calls('GET', '/SmartSearch/Queries')[0].query, { scope: 'All', startIndex: '0', limit: '500' });
    assert.equal(s.q('.jfss-saved-count').textContent, '3 saved queries');
    const rows = s.all('.jfss-saved-row');
    assert.deepEqual(rows.map((r) => r.querySelector('.jfss-saved-open').textContent), ['Alien nights', 'Bob\'s picks', 'Deleted one']);
    assert.equal(rows[0].querySelector('.jfss-saved-open').getAttribute('href'), '#/jfss-search?tab=search&query=' + QA);
    assert.deepEqual(rows[0].querySelectorAll('.jfss-saved-meta > span').map((m) => m.textContent).filter((t) => !/^Updated /.test(t)), ['Shared', 'Used by 2 auto-collections']);
    assert.ok(rows[0].querySelector('.jfss-saved-updated'), 'the date it was changed');
    assert.deepEqual(rows[0].querySelectorAll('.jfss-saved-action').map((b) => b.getAttribute('aria-label')),
        ['Rename Alien nights', 'Duplicate Alien nights', 'Stop sharing Alien nights', 'Copy the link to Alien nights', 'Delete Alien nights']);
    // Someone else's shared query: its owner by name, Duplicate and Copy link only.
    assert.equal(rows[1].querySelector('.jfss-saved-owner').textContent, 'Shared by bob');
    assert.equal(rows[1].querySelector('.jfss-saved-description').textContent, 'Old films');
    assert.deepEqual(rows[1].querySelectorAll('.jfss-saved-action').map((b) => b.getAttribute('aria-label')), ['Duplicate Bob\'s picks', 'Copy the link to Bob\'s picks']);
    // A deleted owner is "Deleted user", as the server says; no owner's id is shown anywhere.
    assert.equal(rows[2].querySelector('.jfss-saved-owner').textContent, 'Shared by Deleted user');
    const everything = s.root.querySelectorAll('*').map((e) => e.textContent + ' ' + ['href', 'aria-label', 'title', 'data-jfss-query'].map((a) => e.getAttribute(a)).join(' ')).join(' ');
    assert.equal(everything.includes(BOB), false, 'no owner id');

    // Mine: the server's scope, the hash.
    s.all('.jfss-filter')[0].click();
    await s.page.advance(50);
    assert.equal(s.calls('GET', '/SmartSearch/Queries').pop().query.scope, 'Mine');
    assert.deepEqual(s.all('.jfss-filter').map((b) => b.getAttribute('aria-pressed')), ['true', 'false', 'false']);
    assert.equal(s.params().scope, 'Mine');
    assert.deepEqual(s.all('.jfss-saved-open').map((a) => a.textContent), ['Alien nights']);
    s.all('.jfss-filter')[1].click();
    await s.page.advance(50);
    assert.deepEqual(s.all('.jfss-saved-open').map((a) => a.textContent), ['Bob\'s picks', 'Deleted one']);
    // All, then a text: names and descriptions, without case or accents.
    s.all('.jfss-filter')[2].click();
    await s.page.advance(50);
    assert.equal(s.params().scope, undefined);
    type(s, s.q('.jfss-find'), 'ÓLD');
    await s.page.advance(400);
    assert.deepEqual(s.all('.jfss-saved-open').map((a) => a.textContent), ['Bob\'s picks']);
    assert.equal(s.params().find, 'ÓLD');
    type(s, s.q('.jfss-find'), 'nothing like it');
    await s.page.advance(400);
    assert.deepEqual(s.all('.jfss-notice').map((n) => n.textContent), ['No saved query matches this text.']);
    // Left goes back to the Search tab.
    tabs[1].focus();
    s.page.key(tabs[1], 'ArrowLeft');
    await s.page.advance(50);
    assert.equal(s.params().tab, 'search');
    assert.equal(s.page.document.activeElement, tabs[0]);
    assert.equal(s.q('.jfss-saved-panel').classList.contains('hide'), true);
    assert.deepEqual(s.page.errors, []);
});

test('a link to the Saved tab shows it, its find field focused, and its filter from the hash; an empty list offers to save the current search', { skip }, async () => {
    const s = await shown({ hash: '#/jfss-search?tab=saved&scope=Shared', saved: [] });
    assert.equal(s.q('.jfss-search-panel').classList.contains('hide'), true);
    assert.equal(s.calls('GET', '/SmartSearch/Queries')[0].query.scope, 'Shared');
    assert.deepEqual(s.all('.jfss-notice').map((n) => n.textContent), ['No one shares a query with you yet.']);
    assert.equal(s.page.document.activeElement, s.q('.jfss-find'));
    s.all('.jfss-filter')[0].click();
    await s.page.advance(50);
    assert.deepEqual(s.all('.jfss-notice').map((n) => n.textContent), ['Save a search to run it again later.']);
    assert.equal(s.q('.jfss-save-current').disabled, true, 'nothing to save: the form has no rule');
    // A rule in the form: Save the current search opens the save dialog.
    s.q('.jfss-title').value = 'heat';
    s.all('.jfss-filter')[2].click();
    await s.page.advance(50);
    assert.equal(s.q('.jfss-save-current').disabled, false);
    s.q('.jfss-save-current').click();
    await s.page.advance(10);
    assert.equal(s.dialog().querySelector('h3').textContent, 'Save this search');
    submitDialog(s, { '.jfss-name-input': 'Heat' });
    await s.page.advance(50);
    assert.equal(s.calls('POST', '/SmartSearch/Queries')[0].body.Query.Where.Children[0].Value, 'heat');
    assert.deepEqual(s.all('.jfss-saved-open').map((a) => a.textContent), ['Heat'], 'the list read again');
    assert.deepEqual(s.page.errors, []);
});

test('F3: Save on a new search asks for a name, a description and sharing, saves the form\'s query, and the page then holds it as the open saved query; Save after an edit changes it with IfUpdatedAt', { skip }, async () => {
    const s = await shown({ saved: [] });
    assert.equal(s.q('.jfss-save').disabled, false);
    assert.equal(s.q('.jfss-save-as-new').classList.contains('hide'), true);
    assert.equal(s.q('.jfss-share-toggle').classList.contains('hide'), true);
    assert.equal(s.q('.jfss-saved-header').classList.contains('hide'), true);
    type(s, s.q('.jfss-title'), 'alien');
    s.q('.jfss-save').click();
    await s.page.advance(10);
    const dialog = s.dialog();
    assert.equal(dialog.getAttribute('aria-modal'), 'true');
    assert.equal(dialog.querySelector('h3').textContent, 'Save this search');
    assert.equal(s.page.document.activeElement, dialog.querySelector('.jfss-name-input'), 'the focus starts on the name');
    assert.equal(dialog.querySelector('.jfss-name-input').getAttribute('maxlength'), '100');
    assert.equal(dialog.querySelector('.jfss-description-input').getAttribute('maxlength'), '500');
    assert.ok(dialog.querySelector('.jfss-share-box'), 'sharing is offered: the server allows it');
    assert.equal(dialog.querySelector('.jfss-share-warning').textContent, 'Others will see the query as you wrote it: the people, series, studios and values it names, and its libraries.', 'with what sharing shows');
    submitDialog(s, { '.jfss-name-input': '   ' });
    await s.page.advance(10);
    assert.equal(dialog.querySelector('[data-jfss-dialog-error="name"]').textContent, 'Enter a name.');
    assert.equal(s.calls('POST', '/SmartSearch/Queries').length, 0);
    submitDialog(s, { '.jfss-name-input': '  Alien nights ', '.jfss-description-input': 'Late films', '.jfss-share-box': true });
    await s.page.advance(50);
    const created = s.calls('POST', '/SmartSearch/Queries')[0].body;
    assert.deepEqual(created, {
        Name: 'Alien nights',
        Description: 'Late films',
        Shared: true,
        Query: {
            Version: 1,
            Scope: { ItemKinds: ['Movie', 'Series'], LibraryIds: [], IncludeMissing: false },
            Where: { Match: 'All', Children: [{ Field: 'Title', Op: 'Contains', Value: 'alien' }] },
            Sort: [{ Key: 'SortName', Order: 'Ascending' }]
        }
    });
    assert.equal(s.dialogs().length, 0, 'closed');
    const id = s.db.queries[0].Id;
    assert.equal(s.q('.jfss-saved-name').textContent, 'Alien nights');
    assert.equal(s.q('.jfss-saved-description').textContent, 'Late films');
    assert.equal(s.q('.jfss-badge-shared').textContent, 'Shared');
    assert.equal(s.params().query, id, 'the hash names it (replaced: no new history entry)');
    assert.deepEqual(s.toasts(), ['Saved ⁨Alien nights⁩.']);
    assert.equal(s.q('.jfss-save').disabled, true, 'unchanged');
    assert.equal(s.q('.jfss-save-as-new').classList.contains('hide'), false);
    assert.equal(s.q('.jfss-share-toggle').classList.contains('hide'), false);
    assert.equal(s.q('.jfss-share-toggle').getAttribute('aria-pressed'), 'true');

    // An edit enables Save, which changes the query in place.
    type(s, s.q('.jfss-title'), 'aliens');
    assert.equal(s.q('.jfss-save').disabled, false);
    s.q('.jfss-save').click();
    await s.page.advance(50);
    const put = s.calls('PUT', '/SmartSearch/Queries/' + id)[0].body;
    assert.equal(put.IfUpdatedAt, '2026-11-10T00:00:02.0000000Z');
    assert.deepEqual([put.Name, put.Description, put.Shared, put.Query.Where.Children[0].Value], ['Alien nights', 'Late films', true, 'aliens']);
    assert.equal(s.q('.jfss-save').disabled, true);

    // Reset closes it; a new search saved under a name already used says so under the name.
    s.q('.jfss-reset').click();
    await s.page.advance(10);
    assert.equal(s.q('.jfss-saved-header').classList.contains('hide'), true);
    assert.equal(s.params().query, undefined);
    type(s, s.q('.jfss-title'), 'x');
    s.q('.jfss-save').focus();
    s.q('.jfss-save').click();
    await s.page.advance(10);
    submitDialog(s, { '.jfss-name-input': 'alien NIGHTS' });
    await s.page.advance(50);
    assert.equal(s.dialog().querySelector('[data-jfss-dialog-error="name"]').textContent, 'There is already a saved query of this name.');
    assert.equal(s.page.document.activeElement, s.dialog().querySelector('.jfss-name-input'));
    s.page.key(s.dialog().querySelector('.jfss-name-input'), 'Escape');
    await s.page.advance(10);
    assert.equal(s.dialogs().length, 0, 'Escape closes it');
    assert.equal(s.page.document.activeElement, s.q('.jfss-save'), 'the focus back on Save');
    assert.deepEqual(s.page.errors, []);
});

test('with sharing turned off on the server, nothing offers to share: not the save dialog, not the list; a shared query can still stop sharing', { skip }, async () => {
    const s = await shown({
        fields: Object.assign({}, FIELDS, { AllowQuerySharing: false }),
        saved: [savedQuery(QA, 'Private one'), savedQuery(QB, 'Was shared', { Shared: true })]
    });
    s.q('.jfss-save').click();
    await s.page.advance(10);
    assert.equal(s.dialog().querySelector('.jfss-share-box'), null);
    s.page.key(s.dialog().querySelector('.jfss-name-input'), 'Escape');
    s.all('[role="tab"]')[1].click();
    await s.page.advance(50);
    const labels = s.all('.jfss-saved-action').map((b) => b.getAttribute('aria-label'));
    assert.equal(labels.includes('Share Private one'), false);
    assert.equal(labels.includes('Stop sharing Was shared'), true);
});

test('F4: a saved query\'s link fills the form from it and runs it by id; an edit searches as any search; a save after a change made elsewhere offers Reload', { skip }, async () => {
    const s = await shown({ hash: '#/jfss-search?query=' + QA, saved: [savedQuery(QA, 'Alien nights', { Description: 'Late films' })] });
    assert.equal(s.calls('GET', '/SmartSearch/Queries/' + QA).length, 1);
    assert.equal(s.q('.jfss-saved-name').textContent, 'Alien nights');
    assert.equal(s.q('.jfss-title').value, 'alien');
    assert.deepEqual(pressed(s, '.jfss-kind'), ['Movies']);
    assert.equal(s.q('.jfss-library').value, MOVIES);
    assert.equal(s.q('.jfss-sort').value, 'ProductionYear');
    assert.equal(s.q('.jfss-order').value, 'desc');
    const runs = s.calls('POST', '/SmartSearch/Queries/' + QA + '/Search');
    assert.equal(runs.length, 1, 'run by its id');
    assert.deepEqual(runs[0].body, { StartIndex: 0, Limit: 50, IncludeTotal: true });
    assert.equal(s.searches().length, 0);
    assert.equal(s.all('.jfss-card').length, 2);
    assert.equal(s.page.context.location.hash, '#/jfss-search?query=' + QA, 'the link as it came');
    assert.equal(s.q('.jfss-save').disabled, true);
    assert.equal(s.q('.jfss-share-toggle').getAttribute('aria-pressed'), 'false');

    // The Share switch asks first, with the warning; cancelled, nothing is sent.
    s.q('.jfss-share-toggle').click();
    await s.page.advance(10);
    assert.equal(s.dialog().querySelector('.jfss-confirm-note').textContent, 'Others will see the query as you wrote it: the people, series, studios and values it names, and its libraries.');
    s.button(s.dialog(), 'Cancel').click();
    await s.page.advance(10);
    assert.equal(s.calls('PUT', '/SmartSearch/Queries/' + QA).length, 0);

    // An edit, searched: a search of its own, in the hash after the query's id; Save changes the saved query.
    type(s, s.q('.jfss-title'), 'alien 2');
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.equal(s.searches().length, 1);
    assert.match(s.page.context.location.hash, new RegExp('^#/jfss-search\\?query=' + QA + '&kinds=Movie&lib=' + MOVIES + '&q=alien\\+2&sort=ProductionYear&order=desc&run=1$'));
    s.q('.jfss-save').click();
    await s.page.advance(50);
    assert.equal(s.db.queries[0].Query.Where.Children[0].Value, 'alien 2');
    // The form back to what is saved: the search by id again.
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.equal(s.calls('POST', '/SmartSearch/Queries/' + QA + '/Search').length, 2);

    // Changed elsewhere: the save is refused, and Reload renders the query as it is now.
    s.db.queries[0].UpdatedUtc = '2026-11-10T09:00:00.0000000Z';
    s.db.queries[0].Query = basicQuery('changed elsewhere');
    type(s, s.q('.jfss-title'), 'alien 3');
    s.q('.jfss-save').click();
    await s.page.advance(50);
    const dialog = s.dialog();
    assert.equal(dialog.querySelector('h3').textContent, 'Changed elsewhere');
    assert.match(dialog.querySelector('p').textContent, /^⁨Alien nights⁩ was changed elsewhere since you opened it\./);
    assert.equal(s.page.document.activeElement, s.button(dialog, 'Reload'));
    s.button(dialog, 'Reload').click();
    await s.page.advance(400);
    helpers(s);
    assert.equal(s.q('.jfss-title').value, 'changed elsewhere');
    assert.equal(s.page.context.location.hash, '#/jfss-search?query=' + QA);
    assert.equal(s.calls('GET', '/SmartSearch/Queries/' + QA).length, 2);
    assert.deepEqual(s.page.errors, []);
});

test('Back and Forward between two saved queries render each again from its link', { skip }, async () => {
    const s = await shown({ hash: '#/jfss-search?query=' + QA, saved: [savedQuery(QA, 'First'), savedQuery(QB, 'Second', { Query: basicQuery('heat') })] });
    const go = async (hash) => {
        s.page.context.location.hash = hash;
        s.page.window.dispatchEvent(new s.page.Event('popstate'));
        await s.page.advance(400);
        helpers(s);
    };
    await go('#/jfss-search?query=' + QB);
    assert.equal(s.q('.jfss-saved-name').textContent, 'Second');
    assert.equal(s.q('.jfss-title').value, 'heat');
    await go('#/jfss-search?query=' + QA);
    assert.equal(s.q('.jfss-saved-name').textContent, 'First');
    assert.equal(s.q('.jfss-title').value, 'alien');
    assert.deepEqual(s.calls('POST', /\/Search$/).map((f) => f.path.split('/')[3]), [QA, QB, QA]);
});

test('a saved query the basic form can\'t show opens editable in the advanced form; a library the user can\'t open is counted, never named, and kept', { skip }, async () => {
    const odd = savedQuery(QB, 'Odd one', {
        OwnerName: 'bob', IsOwner: false, CanEdit: false, Shared: true,
        Query: {
            Version: 1,
            Scope: { ItemKinds: ['Movie'], LibraryIds: [MOVIES, HIDDEN_LIB], IncludeMissing: false },
            Where: { Match: 'Any', Negate: false, Children: [{ Field: 'Title', Op: 'Contains', Value: 'a', Negate: false }, { Field: 'Year', Op: 'Ge', Value: 2000, Negate: false }] },
            Sort: [{ Key: 'SortName', Order: 'Ascending' }, { Key: 'ProductionYear', Order: 'Descending' }],
            Limit: 40
        }
    });
    const s = await shown({ hash: '#/jfss-search?query=' + QB, saved: [odd], fields: ALL_FIELDS });
    respondAdvanced(s);
    assert.equal(s.q('.jfss-scope').classList.contains('hide'), false);
    assert.equal(s.q('.jfss-basic-fields').classList.contains('hide'), true);
    assert.equal(s.q('.jfss-advanced').classList.contains('hide'), false);
    assert.deepEqual(pressed(s, '.jfss-mode'), ['Advanced']);
    assert.equal(s.q('[data-jfss-mode="basic"]').disabled, true, 'an Any group isn\'t the basic form\'s');
    assert.equal(s.q('.jfss-mode-note').textContent, 'This search uses advanced rules, which the basic form can\'t show.');
    assert.equal(s.q('.jfss-match').value, 'Any');
    assert.deepEqual(s.all('.jfss-rule-field').map((f) => f.value), ['Title', 'Year']);
    assert.equal(s.q('.jfss-saved-header .jfss-notice'), null, 'no note: it is editable');
    assert.equal(s.q('.jfss-badge-unavailable').textContent, '1 library not available to you');
    assert.equal(s.q('.jfss-library').value, MOVIES);
    // Another choice of libraries leaves the one the user can't open out, and the badge says so; back, it is kept.
    s.q('.jfss-library').value = '';
    s.page.dispatch(s.q('.jfss-library'), 'change');
    assert.equal(s.q('.jfss-badge-unavailable').textContent, '1 library not available to you is left out, as the libraries changed');
    s.q('.jfss-library').value = MOVIES;
    s.page.dispatch(s.q('.jfss-library'), 'change');
    assert.equal(s.q('.jfss-badge-unavailable').textContent, '1 library not available to you');
    assert.equal(s.calls('POST', '/SmartSearch/Queries/' + QB + '/Search').length, 1);
    assert.equal(s.searches().length, 0);
    assert.equal(s.q('.jfss-save').disabled, true, 'someone else\'s');
    // Unchanged, Search runs it again by id.
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.equal(s.calls('POST', '/SmartSearch/Queries/' + QB + '/Search').length, 2);
    // Edited, it searches as any search, with what the form doesn't show sent as saved.
    type(s, s.q('.jfss-rule-input.jfss-rule-value'), 'alien');
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    const query = s.searches()[0].body.Query;
    assert.deepEqual(query.Scope.LibraryIds, [MOVIES, HIDDEN_LIB]);
    assert.deepEqual(query.Sort, [{ Key: 'SortName', Order: 'Ascending' }, { Key: 'ProductionYear', Order: 'Descending' }]);
    assert.equal(query.Limit, 40);
    assert.deepEqual(query.Where, { Match: 'Any', Children: [{ Field: 'Title', Op: 'Contains', Value: 'alien' }, { Field: 'Year', Op: 'Ge', Value: 2000 }] });
    const everything = s.page.document.body.querySelectorAll('*').map((e) => e.textContent + ' ' + ['href', 'aria-label', 'title', 'value', 'data-id', 'data-jfss-value'].map((a) => e.getAttribute(a)).join(' ')).join(' ');
    assert.equal(everything.includes(HIDDEN_LIB), false, 'the library the user can\'t open is never shown');
    assert.equal(s.page.context.location.hash.includes(HIDDEN_LIB), false);
    // Save as new: checked by the server first, then the form's query saved as the user's own.
    s.q('.jfss-save-as-new').click();
    await s.page.advance(20);
    assert.equal(s.calls('POST', '/SmartSearch/Validate').length, 1);
    assert.deepEqual(s.calls('POST', '/SmartSearch/Validate')[0].body, { Query: query });
    assert.equal(s.dialog().querySelector('h3').textContent, 'Save as a new query');
    submitDialog(s, { '.jfss-name-input': 'Mine now' });
    await s.page.advance(50);
    const copy = s.db.queries[1];
    assert.deepEqual(copy.Query, query);
    assert.equal(s.params().query, copy.Id);
    assert.equal(s.q('.jfss-saved-owner'), null, 'the user\'s own now');
    assert.deepEqual(s.page.errors, []);
});

test('a saved query the advanced form can\'t send back exactly (member names in another case) opens locked: it runs as saved, by id, and Save as new duplicates it', { skip }, async () => {
    const odd = savedQuery(QB, 'Imported long ago', {
        Query: { version: 1, scope: { itemKinds: ['Movie'] }, where: { match: 'Any', children: [{ Field: 'Title', Op: 'Contains', Value: 'a' }, { Field: 'Year', Op: 'Ge', Value: 2000 }] } }
    });
    const s = await shown({ hash: '#/jfss-search?query=' + QB, saved: [odd], fields: ALL_FIELDS });
    assert.equal(s.q('.jfss-saved-header .jfss-notice').textContent, 'The form can\'t show this query exactly as it was saved, so it runs as saved and can\'t be changed here. Save as new makes a copy.');
    assert.equal(s.q('.jfss-saved-header').textContent.includes('null'), false, 'a private query without a description: no empty parts');
    assert.equal(s.q('.jfss-scope').classList.contains('hide'), true, 'the form is hidden');
    assert.equal(s.q('.jfss-advanced').classList.contains('hide'), true);
    assert.equal(s.q('.jfss-save').disabled, true);
    assert.equal(s.calls('POST', '/SmartSearch/Queries/' + QB + '/Search').length, 1, 'it runs as saved');
    assert.equal(s.searches().length, 0, 'never as the form would send it');
    s.q('.jfss-save-as-new').click();
    await s.page.advance(20);
    assert.equal(s.calls('POST', '/SmartSearch/Validate').length, 0, 'a copy by the server, not the form\'s query');
    assert.deepEqual(s.page.errors, []);
});

test('a saved query written by a newer version doesn\'t run; a link to one that isn\'t there says so and runs the search the link holds', { skip }, async () => {
    const newer = savedQuery(QA, 'From the future', { Supported: false, Valid: false, Query: { Version: 9, Anything: true } });
    const s = await shown({ hash: '#/jfss-search?query=' + QA, saved: [newer] });
    assert.match(s.q('.jfss-saved-header .jfss-notice').textContent, /^This query was saved by a newer version of Smart Search/);
    assert.equal(s.calls('POST', /\/Search$/).length, 0);
    assert.equal(s.q('.jfss-search-button').disabled, true);
    assert.equal(s.q('.jfss-save-as-new').classList.contains('hide'), false, 'it can be duplicated');

    const gone = await shown({ hash: '#/jfss-search?query=' + QB + '&kinds=Movie&q=heat&run=1', saved: [] });
    assert.equal(gone.q('.jfss-saved-header .jfss-notice').textContent, 'The saved query in this link wasn\'t found, or isn\'t shared with you.');
    assert.equal(gone.searches().length, 1);
    assert.equal(gone.q('.jfss-saved-name'), null);
});

test('F5: the owner shares and stops sharing, confirmed; anyone duplicates a shared query into a private copy of their own', { skip }, async () => {
    const s = await shown({ hash: '#/jfss-search?tab=saved', saved: [savedQuery(QA, 'Alien nights'), bobsQuery()] });
    const action = (name, label) => s.all('.jfss-saved-row').find((r) => r.querySelector('.jfss-saved-open').textContent === name)
        .querySelectorAll('.jfss-saved-action').find((b) => b.getAttribute('aria-label') === label + ' ' + name);
    action('Alien nights', 'Share').click();
    await s.page.advance(10);
    assert.equal(s.dialog().querySelector('h3').textContent, 'Share this query');
    assert.match(s.dialog().querySelector('p').textContent, /^Share ⁨Alien nights⁩ with every user of this server\?/);
    assert.equal(s.dialog().querySelector('.jfss-confirm-note').textContent, 'Others will see the query as you wrote it: the people, series, studios and values it names, and its libraries.', 'the warning before sharing');
    s.button(s.dialog(), 'Share').click();
    await s.page.advance(50);
    const put = s.calls('PUT', '/SmartSearch/Queries/' + QA)[0].body;
    assert.deepEqual([put.Shared, put.Name, put.IfUpdatedAt], [true, 'Alien nights', '2026-11-02T10:00:00.0000000Z']);
    assert.deepEqual(put.Query, basicQuery(), 'the definition as stored');
    assert.deepEqual(s.toasts().pop(), '⁨Alien nights⁩ is shared.');
    assert.ok(action('Alien nights', 'Stop sharing'), 'the list read again');
    action('Alien nights', 'Stop sharing').click();
    await s.page.advance(10);
    assert.equal(s.dialog().querySelector('.jfss-confirm-note'), null, 'no warning to stop sharing');
    s.button(s.dialog(), 'Cancel').click();
    await s.page.advance(50);
    assert.equal(s.calls('PUT', '/SmartSearch/Queries/' + QA).length, 1, 'cancelled: nothing sent');

    // Duplicate bob's: the name as it is is numbered by the server; another name is sent.
    action('Bob\'s picks', 'Duplicate').click();
    await s.page.advance(10);
    assert.equal(s.dialog().querySelector('.jfss-duplicate-note').textContent, 'The copy is yours and private.');
    submitDialog(s);
    await s.page.advance(50);
    assert.deepEqual(s.calls('POST', '/SmartSearch/Queries/' + QC + '/Duplicate')[0].body, {});
    assert.match(s.toasts().pop(), /^Saved a copy: ⁨Bob's picks⁩\.$/);
    action('Bob\'s picks', 'Duplicate').click();
    await s.page.advance(10);
    submitDialog(s, { '.jfss-name-input': 'My picks' });
    await s.page.advance(50);
    assert.deepEqual(s.calls('POST', '/SmartSearch/Queries/' + QC + '/Duplicate')[1].body, { Name: 'My picks' });
    assert.deepEqual(s.all('.jfss-saved-open').map((a) => a.textContent), ['Alien nights', 'Bob\'s picks', 'Bob\'s picks', 'My picks']);
    // Rename: the name and description changed with the definition as stored.
    action('My picks', 'Rename').click();
    await s.page.advance(50);
    assert.equal(s.dialog().querySelector('h3').textContent, 'Rename');
    assert.equal(s.dialog().querySelector('.jfss-name-input').value, 'My picks');
    submitDialog(s, { '.jfss-name-input': 'Picks', '.jfss-description-input': 'Mine now' });
    await s.page.advance(50);
    const renamed = s.page.fetchLog.filter((f) => f.method === 'PUT').pop().body;
    assert.deepEqual([renamed.Name, renamed.Description, renamed.Shared], ['Picks', 'Mine now', false]);
    assert.deepEqual(s.toasts().pop(), 'Renamed to ⁨Picks⁩.');
    assert.deepEqual(s.page.errors, []);
});

test('F6: Delete asks first; when auto-collections use the query, a second question detaches them and deletes it', { skip }, async () => {
    const s = await shown({ hash: '#/jfss-search?tab=saved', saved: [savedQuery(QA, 'Alien nights'), savedQuery(QB, 'Used one')] });
    s.db.referenced.add(QB);
    const del = (name) => s.all('.jfss-saved-row').find((r) => r.querySelector('.jfss-saved-open').textContent === name).querySelector('.jfss-delete');
    del('Alien nights').click();
    await s.page.advance(10);
    assert.equal(s.dialog().querySelector('h3').textContent, 'Delete saved query');
    assert.equal(s.dialog().querySelector('p').textContent, 'Delete ⁨Alien nights⁩? This can\'t be undone.');
    s.button(s.dialog(), 'Cancel').click();
    await s.page.advance(10);
    assert.equal(s.calls('DELETE', /Queries/).length, 0);
    del('Alien nights').click();
    await s.page.advance(10);
    s.button(s.dialog(), 'Delete').click();
    await s.page.advance(50);
    assert.equal(s.calls('DELETE', '/SmartSearch/Queries/' + QA).length, 1);
    assert.deepEqual(s.toasts().pop(), 'Deleted ⁨Alien nights⁩.');
    assert.deepEqual(s.all('.jfss-saved-open').map((a) => a.textContent), ['Used one']);

    del('Used one').click();
    await s.page.advance(10);
    s.button(s.dialog(), 'Delete').click();
    await s.page.advance(50);
    assert.equal(s.dialog().querySelector('h3').textContent, 'Auto-collections use this query');
    assert.equal(s.dialog().querySelector('p').textContent, 'These auto-collections use ⁨Used one⁩: ⁨Horror 80s⁩. Detach them (they keep a copy of this query) and delete it?');
    s.button(s.dialog(), 'Detach them and delete').click();
    await s.page.advance(50);
    assert.deepEqual(s.calls('DELETE', '/SmartSearch/Queries/' + QB).map((f) => f.query), [{}, { detachAutoCollections: 'true' }]);
    assert.equal(s.all('.jfss-saved-row').length, 0);
    assert.deepEqual(s.page.errors, []);
});

test('a change auto-collections refuse offers "Detach these collections, then save", which saves it with them detached', { skip }, async () => {
    for (const code of ['BreaksAutoCollections', 'ManagerRequired']) {
        const s = await shown({ hash: '#/jfss-search?query=' + QA, saved: [savedQuery(QA, 'Alien nights', { AutoCollectionCount: 2 })] });
        s.db.refusing.set(QA, code);
        type(s, s.q('.jfss-title'), 'aliens');
        s.q('.jfss-save').click();
        await s.page.advance(50);
        const dialog = s.dialog();
        assert.equal(dialog.querySelector('h3').textContent, 'Auto-collections use this query');
        assert.deepEqual(Array.from(dialog.querySelectorAll('.jfss-auto-collections li')).map((li) => li.textContent), ['Horror 80s: A collection can\'t hold collections.', 'Sci-fi']);
        assert.equal(dialog.querySelector('.jfss-detach-note').textContent, 'Detached, each auto-collection keeps a copy of this query as it is saved now and no longer follows your changes.');
        assert.equal(s.db.queries[0].Query.Where.Children[0].Value, 'alien', code + ': nothing saved yet');
        s.button(dialog, 'Detach these collections, then save').click();
        await s.page.advance(50);
        const puts = s.calls('PUT', '/SmartSearch/Queries/' + QA).map((f) => f.body);
        assert.deepEqual(puts.map((b) => b.DetachAutoCollections), [undefined, true]);
        assert.deepEqual(puts.map((b) => b.Query.Where.Children[0].Value), ['aliens', 'aliens']);
        assert.equal(puts[1].IfUpdatedAt, '2026-11-02T10:00:00.0000000Z');
        assert.equal(s.db.queries[0].Query.Where.Children[0].Value, 'aliens');
        assert.deepEqual(s.toasts().slice(-2), ['2 auto-collections were detached and keep the query as it was.', 'Saved ⁨Alien nights⁩.']);
        assert.equal(s.q('.jfss-save').disabled, true, code + ': saved');
        assert.deepEqual(s.page.errors, []);
    }

    // Cancel saves nothing, and Save as new stays offered.
    const s = await shown({ hash: '#/jfss-search?query=' + QA, saved: [savedQuery(QA, 'Alien nights')] });
    s.db.refusing.set(QA, 'BreaksAutoCollections');
    type(s, s.q('.jfss-title'), 'aliens');
    s.q('.jfss-save').click();
    await s.page.advance(50);
    assert.ok(s.button(s.dialog(), 'Save as new…'));
    s.button(s.dialog(), 'Cancel').click();
    await s.page.advance(50);
    assert.equal(s.calls('PUT', '/SmartSearch/Queries/' + QA).length, 1);
    assert.equal(s.q('.jfss-save').disabled, false, 'the change is still there to save');
});

test('deleting the saved query open on the Search tab closes it there', { skip }, async () => {
    const s = await shown({ hash: '#/jfss-search?query=' + QA, saved: [savedQuery(QA, 'Alien nights')] });
    s.all('[role="tab"]')[1].click();
    await s.page.advance(50);
    s.q('.jfss-delete').click();
    await s.page.advance(10);
    s.button(s.dialog(), 'Delete').click();
    await s.page.advance(50);
    assert.equal(s.params().query, undefined);
    assert.equal(s.q('.jfss-saved-header').classList.contains('hide'), true);
});

test('names and descriptions are text: markup stays text, control characters show as U+FFFD, and every name is isolated from the text around it', { skip }, async () => {
    const evil = '<img src=x onerror="window.__pwned=1">‮evil\u0007 שלום';
    const shown1 = '<img src=x onerror="window.__pwned=1">‮evil� שלום';
    const s = await shown({ hash: '#/jfss-search?tab=saved', saved: [savedQuery(QA, evil, { Description: '<b>bold</b>\u0000' }), bobsQuery({ OwnerName: '<i>bob</i>‏' })] });
    const panel = s.q('.jfss-saved-panel');
    assert.equal(panel.querySelectorAll('img, b, i').length, 0, 'no element from a name');
    const name = panel.querySelector('.jfss-saved-open bdi');
    assert.equal(name.textContent, shown1);
    assert.equal(name.getAttribute('dir'), 'auto');
    assert.equal(panel.querySelector('.jfss-saved-description bdi').textContent, '<b>bold</b>�');
    assert.equal(panel.querySelector('.jfss-saved-owner bdi').textContent, '<i>bob</i>‏');
    assert.equal(panel.querySelector('.jfss-delete').getAttribute('aria-label'), 'Delete ' + shown1);
    panel.querySelector('.jfss-delete').click();
    await s.page.advance(10);
    assert.equal(s.dialog().querySelector('p').textContent, 'Delete ⁨' + shown1 + '⁩? This can\'t be undone.');
    s.button(s.dialog(), 'Cancel').click();
    await s.page.advance(10);
    // Opened: the heading.
    const opened = await shown({ hash: '#/jfss-search?query=' + QA, saved: [savedQuery(QA, evil)] });
    assert.equal(opened.q('.jfss-saved-name').querySelectorAll('img').length, 0);
    assert.equal(opened.q('.jfss-saved-name bdi').textContent, shown1);
    assert.equal(s.page.context.__pwned, undefined);
    assert.deepEqual(s.page.errors, []);
});

test('Copy link: the query\'s absolute link to the clipboard, or shown selected where there is none', { skip }, async () => {
    const s = await shown({ hash: '#/jfss-search?tab=saved', saved: [savedQuery(QA, 'Alien nights')] });
    const written = [];
    s.page.context.navigator = { clipboard: { writeText: (text) => {
        written.push(text);
        return Promise.resolve();
    } } };
    s.q('.jfss-copy-link').click();
    await s.page.advance(10);
    assert.deepEqual(written, ['http://127.0.0.1:8097/web/index.html#/jfss-search?query=' + QA]);
    assert.deepEqual(s.toasts().pop(), 'Link copied.');
    s.page.context.navigator = {};
    s.q('.jfss-copy-link').click();
    await s.page.advance(10);
    const input = s.dialog().querySelector('.jfss-link-input');
    assert.equal(input.value, written[0]);
    assert.equal(input.selectionEnd, written[0].length);
    assert.equal(s.page.document.activeElement, input);
});

test('on the TV layout: the tabs and the list without Copy link, and a dialog takes the focus and gives it back', { skip }, async () => {
    const s = await shown({ tv: true, hash: '#/jfss-search?tab=saved', saved: [savedQuery(QA, 'Alien nights')] });
    assert.notEqual(s.page.document.activeElement, s.q('.jfss-find'), 'no field takes the focus');
    assert.equal(s.q('.jfss-copy-link'), null);
    const rename = s.q('.jfss-rename');
    rename.focus();
    rename.click();
    await s.page.advance(50);
    assert.equal(s.page.document.activeElement, s.dialog().querySelector('.jfss-name-input'));
    s.button(s.dialog(), 'Cancel').click();
    await s.page.advance(10);
    assert.equal(s.page.document.activeElement, rename);
    assert.deepEqual(s.page.errors, []);
});

// ------------------------------------------------------------------------------------------------ the rule tree as data

// 35-tree.js with what it reads (the texts, ssText, the model's limits), outside the page: the tree's own logic.
function treeModule() {
    const web = new URL('../../../Jellyfin.Plugin.SmartSearch/Web/js/', import.meta.url);
    const names = ['SS_TEXT', 'ssCatalog', 'ssOpShape', 'ssNewGroup', 'ssNewRule', 'ssFindNode', 'ssCountRules', 'ssCanAddRule', 'ssCanAddGroup',
        'ssCanDuplicate', 'ssCanUnwrap', 'ssAddRule', 'ssAddGroup', 'ssDuplicateNode', 'ssUnwrapGroup', 'ssRemoveNode', 'ssSetRuleField', 'ssSetRuleOp',
        'ssWhereFromTree', 'ssTreeFromWhere', 'ssWhereShapeOk', 'ssCheckTree', 'ssCheckRule', 'ssNodeOfPath', 'ssText', 'ssAdvancedStateFromQuery', 'ssKeptOfQuery',
        'ssBuildQuery', 'ssSameQuery', 'ssReadState'];
    const code = ['00-strings.js', '20-api.js', '30-model.js', '35-tree.js'].map((f) => fs.readFileSync(new URL(f, web), 'utf8')).join('\n')
        + '\n({ ' + names.join(', ') + ' })';
    return vm.runInNewContext(code, { URLSearchParams });
}

const ALL_FIELDS = JSON.parse(fs.readFileSync(new URL('./fields.json', import.meta.url), 'utf8'));
const ALL_KINDS = ['Movie', 'Series', 'Episode', 'MusicVideo', 'Video', 'BoxSet'];
const plain = (value) => JSON.parse(JSON.stringify(value));

test('the tree: built from the catalogue, edited, nested to the server\'s limits, duplicated, ungrouped and removed', () => {
    const t = treeModule();
    const catalog = t.ssCatalog(ALL_FIELDS, ALL_KINDS);
    assert.deepEqual(plain(catalog.groups), [...new Set(ALL_FIELDS.Fields.map((f) => f.Group))]);
    assert.equal(catalog.fields.length, 33);
    assert.deepEqual(['IsEmpty', 'Between', 'InLast', 'NoneOf', 'Ge'].map(t.ssOpShape), ['none', 'range', 'relative', 'values', 'value']);

    const root = t.ssNewGroup('All', false, []);
    const title = t.ssAddRule(root, root.id, catalog, ['Movie']);
    assert.deepEqual([title.field, title.op], ['Title', 'Contains']);
    assert.deepEqual(plain(title.options), { IncludeOriginalTitle: false });
    const episodeOnly = t.ssNewRule(catalog, ['Episode'], 'Series');
    assert.deepEqual([episodeOnly.field, episodeOnly.op], ['Series', 'AnyOf']);

    // Another field: the operator kept when it takes it, the text when the type stays; values never.
    title.input.value = 'alien';
    t.ssSetRuleField(title, catalog, 'OriginalTitle');
    assert.deepEqual([title.field, title.op, title.input.value], ['OriginalTitle', 'Contains', 'alien']);
    t.ssSetRuleField(title, catalog, 'Year');
    assert.deepEqual([title.field, title.op, title.input.value], ['Year', 'Eq', '']);
    title.input.value = '1979';
    t.ssSetRuleOp(title, catalog, 'Between');
    assert.deepEqual([title.op, title.input.from], ['Between', '1979']);
    t.ssSetRuleOp(title, catalog, 'Contains');
    assert.equal(title.op, 'Between', 'an operator the field doesn\'t take is refused');
    t.ssSetRuleField(title, catalog, 'HasProviderId');
    assert.deepEqual(plain(title.options), { Provider: 'Tmdb' }, 'a required option starts on its first choice');

    // Groups nest 4 deep: the root is the first level.
    const g2 = t.ssAddGroup(root, root.id, catalog, ['Movie']);
    assert.deepEqual([g2.match, g2.negate, g2.children.length], ['Any', false, 1]);
    const g3 = t.ssAddGroup(root, g2.id, catalog, ['Movie']);
    const g4 = t.ssAddGroup(root, g3.id, catalog, ['Movie']);
    assert.equal(t.ssFindNode(root, g4.id).depth, 4);
    assert.equal(t.ssCanAddGroup(root, g4.id), false);
    assert.equal(t.ssAddGroup(root, g4.id, catalog, ['Movie']), null);
    assert.equal(t.ssCanAddRule(root, g4.id), true);

    // Duplicate copies a group with new ids; Ungroup moves its children up; Remove takes a node out.
    const copy = t.ssDuplicateNode(root, g3.id);
    assert.equal(g2.children[2], copy);
    assert.notEqual(copy.id, g3.id);
    assert.notEqual(copy.children[1].id, g3.children[1].id);
    assert.deepEqual(plain(t.ssWhereFromTree(copy, catalog)), plain(t.ssWhereFromTree(g3, catalog)));
    assert.equal(t.ssCountRules(root), 6);
    const moved = t.ssUnwrapGroup(root, copy.id);
    assert.equal(moved.length, 2);
    assert.equal(g2.children.length, 4);
    assert.equal(t.ssRemoveNode(root, g2.id), true);
    assert.equal(t.ssRemoveNode(root, root.id), false, 'the root stays');
    assert.equal(t.ssCountRules(root), 1);

    // At 50 rules nothing more is added or duplicated.
    for (let i = 1; i < 50; i++) {
        assert.ok(t.ssAddRule(root, root.id, catalog, ['Movie']));
    }

    assert.equal(t.ssCanAddRule(root, root.id), false);
    assert.equal(t.ssAddRule(root, root.id, catalog, ['Movie']), null);
    assert.equal(t.ssCanDuplicate(root, title.id), false);
    assert.equal(t.ssCanAddGroup(root, root.id), false);
});

test('the tree and the query JSON: a Where read and written again says the same; a rule the editor can\'t show stays as it came', () => {
    const t = treeModule();
    const catalog = t.ssCatalog(ALL_FIELDS, ALL_KINDS);
    const where = {
        Match: 'All',
        Children: [
            { Field: 'Title', Op: 'Contains', Value: 'alien', IncludeOriginalTitle: true },
            { Field: 'Year', Op: 'Between', From: 1979, To: 1986 },
            { Field: 'DateAdded', Op: 'InLast', Amount: 30, Unit: 'Days' },
            { Field: 'Series', Op: 'AnyOf', Values: [{ Id: 'c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3', Name: 'Test Series' }] },
            { Field: 'Library', Op: 'NoneOf', Values: [MOVIES] },
            { Field: 'Played', Op: 'Is', Value: false },
            { Field: 'ProviderId', Op: 'Eq', Value: 'tt0078748', Provider: 'Imdb' },
            { Field: 'CommunityRating', Op: 'Ge', Value: 7.5 },
            { Field: 'PremiereDate', Op: 'Before', Value: '2000-01-01' },
            { Field: 'Tags', Op: 'IsEmpty' },
            { Match: 'Any', Negate: true, Children: [{ Field: 'Genres', Op: 'AllOf', Values: ['Horror', 'Comedy'] }, { Match: 'All', Children: [{ Field: 'Locked', Op: 'Is', Value: true }] }] }
        ]
    };
    const tree = t.ssTreeFromWhere(where, catalog);
    assert.deepEqual(plain(t.ssWhereFromTree(tree, catalog)), where);
    assert.equal(tree.children.some((c) => c.locked), false);

    // As the server stores it: Negate false and IncludeOriginalTitle false written on every rule.
    const stored = plain(where);
    stored.Negate = false;
    stored.Children.forEach((c) => {
        c.Negate = c.Negate === true;
    });
    stored.Children[0].IncludeOriginalTitle = false;
    const expected = plain(where);
    delete expected.Children[0].IncludeOriginalTitle;
    assert.deepEqual(plain(t.ssWhereFromTree(t.ssTreeFromWhere(stored, catalog), catalog)), expected);

    // Locked: a field this page doesn't know, an operator its field doesn't list, a negated rule, a value of another shape.
    const odd = [
        { Field: 'Mood', Op: 'Eq', Value: 'calm' },
        { Field: 'Title', Op: 'Matches', Value: '^a' },
        { Field: 'Title', Op: 'Contains', Value: 'a', Negate: true },
        { Field: 'Title', Op: 'Contains', Values: ['a'] }
    ];
    const lockedTree = t.ssTreeFromWhere({ Match: 'Any', Children: odd }, catalog);
    assert.deepEqual(lockedTree.children.map((c) => !!c.locked), [true, true, true, true]);
    assert.deepEqual(plain(t.ssWhereFromTree(lockedTree, catalog)), { Match: 'Any', Children: odd });
    assert.deepEqual(plain(t.ssCheckTree(lockedTree, catalog, ALL_KINDS).errors), {}, 'the server judges them');

    // A Where from the hash must look like one.
    assert.equal(t.ssWhereShapeOk({ Match: 'All', Children: [] }), true);
    assert.equal(t.ssWhereShapeOk({ Field: 'Title' }), false);
    assert.equal(t.ssWhereShapeOk('x'), false);
    let deep = { Match: 'All', Children: [] };
    for (let i = 0; i < 6; i++) {
        deep = { Match: 'All', Children: [deep] };
    }

    assert.equal(t.ssWhereShapeOk(deep), false);
    assert.deepEqual(plain(t.ssWhereFromTree(t.ssTreeFromWhere(null, catalog), catalog)), { Match: 'All', Children: [] });

    // A saved query as the advanced form's state: what the form doesn't show is kept, kinds no longer allowed dropped.
    const query = { Version: 1, Scope: { ItemKinds: ['Movie', 'Trailer'], LibraryIds: [MOVIES, HIDDEN_LIB], IncludeMissing: false }, Where: stored, Sort: [{ Key: 'SortName', Order: 'Descending' }, { Key: 'Random', Order: 'Ascending' }], Limit: 7 };
    const read = t.ssAdvancedStateFromQuery(query, { kinds: ALL_KINDS, sortKeys: ['SortName', 'Random'], libraryIds: [MOVIES, SHOWS], catalog });
    assert.deepEqual(plain(read.state.kinds), ['Movie']);
    assert.deepEqual(plain(read.dropped), ['Trailer']);
    assert.deepEqual([read.state.lib, read.state.sort, read.state.desc, read.state.mode], [MOVIES, 'SortName', true, 'adv']);
    assert.deepEqual(plain(read.state.where), expected);
    assert.deepEqual(plain(read.kept), { lib: MOVIES, libraries: [HIDDEN_LIB], sort: [{ Key: 'Random', Order: 'Ascending' }], limit: 7 });
});

const LIB = 'a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1';
const READ_OPTIONS = (t) => ({ kinds: ALL_KINDS, sortKeys: ALL_FIELDS.SortKeys, libraryIds: [LIB], catalog: t.ssCatalog(ALL_FIELDS, ALL_KINDS) });

test('a saved query with lower-case member names opens locked, never as an empty All group', () => {
    const t = treeModule();
    const query = { Version: 1, Scope: { ItemKinds: ['Movie'] }, Where: { match: 'Any', children: [{ Field: 'Title', Op: 'Contains', Value: 'alien' }, { Field: 'Year', Op: 'Eq', Value: 1979 }] } };
    const read = t.ssAdvancedStateFromQuery(query, READ_OPTIONS(t));
    assert.equal(read.exact, false);
    // The canonical form of the same query opens editable, and the form sends it back.
    const canonical = { Version: 1, Scope: { ItemKinds: ['Movie'] }, Where: { Match: 'Any', Children: query.Where.children } };
    const again = t.ssAdvancedStateFromQuery(canonical, READ_OPTIONS(t));
    assert.equal(again.exact, true);
    assert.deepEqual(plain(t.ssBuildQuery(again.state, again.kept).query.Where), canonical.Where);
});

test('a group\'s Match is read ignoring case, as the server reads it: "any" stays an Any group', () => {
    const t = treeModule();
    const catalog = t.ssCatalog(ALL_FIELDS, ALL_KINDS);
    const where = { Match: 'All', Children: [{ Match: 'any', Children: [{ Field: 'Title', Op: 'Contains', Value: 'a' }, { Field: 'Title', Op: 'Contains', Value: 'b' }] }] };
    assert.equal(plain(t.ssWhereFromTree(t.ssTreeFromWhere(where, catalog), catalog)).Children[0].Match, 'Any');
    const read = t.ssAdvancedStateFromQuery({ Version: 1, Scope: { ItemKinds: ['Movie'] }, Where: where }, READ_OPTIONS(t));
    assert.equal(read.exact, true, 'the same query, in the server\'s casing');
});

test('a saved query whose group has a member the editor doesn\'t know opens locked, so the member isn\'t lost on a save', () => {
    const t = treeModule();
    const query = { Version: 1, Scope: { ItemKinds: ['Movie'] }, Where: { Match: 'All', Future: { x: 1 }, Children: [{ Field: 'Title', Op: 'Contains', Value: 'a' }] } };
    assert.equal(t.ssAdvancedStateFromQuery(query, READ_OPTIONS(t)).exact, false);
    // A Limit or a second sort key is the form's to keep: still exact.
    const kept = { Version: 1, Scope: { ItemKinds: ['Movie'] }, Where: { Match: 'All', Children: [] }, Sort: [{ Key: 'SortName', Order: 'Ascending' }, { Key: 'ProductionYear', Order: 'Descending' }], Limit: 40 };
    assert.equal(t.ssAdvancedStateFromQuery(kept, READ_OPTIONS(t)).exact, true);
    // No sort is the sort by title ascending; Negate false and IncludeMissing false are the defaults.
    const defaults = { Version: 1, Scope: { ItemKinds: ['Series', 'Movie'], LibraryIds: [], IncludeMissing: false }, Where: { Match: 'All', Negate: false, Children: [] }, Sort: [] };
    assert.equal(t.ssAdvancedStateFromQuery(defaults, READ_OPTIONS(t)).exact, true);
});

test('lower-case kinds, sort key and order are read as the server reads them, and an unchanged form sends them back', () => {
    const t = treeModule();
    const query = {
        Version: 1,
        Scope: { ItemKinds: ['series'], LibraryIds: [LIB], IncludeMissing: false },
        Where: { Match: 'Any', Children: [{ Field: 'Title', Op: 'Contains', Value: 'a' }, { Field: 'Title', Op: 'Contains', Value: 'b' }] },
        Sort: [{ Key: 'productionyear', Order: 'descending' }]
    };
    const read = t.ssAdvancedStateFromQuery(query, READ_OPTIONS(t));
    assert.deepEqual(plain(read.dropped), [], 'no kind is reported as no longer allowed');
    assert.deepEqual(plain(read.state.kinds), ['Series']);
    assert.equal(read.state.sort, 'ProductionYear');
    assert.equal(read.state.desc, true);
    assert.equal(read.exact, true);
    const sent = plain(t.ssBuildQuery(read.state, read.kept).query);
    assert.deepEqual(sent.Scope.ItemKinds, ['Series']);
    assert.deepEqual(sent.Sort, [{ Key: 'ProductionYear', Order: 'Descending' }]);
    // A kind the server no longer allows is left out and named, not a reason to lock.
    const fewer = t.ssAdvancedStateFromQuery(query, Object.assign(READ_OPTIONS(t), { kinds: ['Movie', 'Episode'] }));
    assert.deepEqual(plain(fewer.dropped), ['series']);
    assert.equal(fewer.exact, true);
});

test('a link\'s Where longer than 16 KB is left out before it is parsed', () => {
    const t = treeModule();
    const options = { kinds: ALL_KINDS, sortKeys: ALL_FIELDS.SortKeys, libraryIds: [LIB], lastScope: null };
    const small = JSON.stringify({ Match: 'All', Children: [{ Field: 'Title', Op: 'Contains', Value: 'x' }] });
    assert.deepEqual(plain(t.ssReadState({ mode: 'adv', where: small }, options).where), JSON.parse(small));
    const big = JSON.stringify({ Match: 'All', Children: [{ Field: 'Title', Op: 'Contains', Value: 'x'.repeat(16 * 1024) }] });
    assert.deepEqual(plain(t.ssReadState({ mode: 'adv', where: big }, options).where), { Match: 'All', Children: [] });
});

test('the tree\'s checks: the messages a search or a save stops at, and a server error\'s path to its rule', () => {
    const t = treeModule();
    const catalog = t.ssCatalog(ALL_FIELDS, ALL_KINDS);
    const T = t.SS_TEXT;
    const rule = (json) => t.ssTreeFromWhere({ Match: 'All', Children: [json] }, catalog).children[0];
    const check = (json, kinds) => plain(t.ssCheckRule(rule(json), catalog, kinds || ALL_KINDS));
    assert.deepEqual(check({ Field: 'Title', Op: 'Contains', Value: '' }), { value: T.ruleValueRequired });
    assert.deepEqual(check({ Field: 'Title', Op: 'Contains', Value: 'x'.repeat(201) }), { value: T.ruleTextTooLong });
    assert.deepEqual(check({ Field: 'Year', Op: 'Eq', Value: 1700 }), { value: 'Enter a number from 1800 to 2100.' });
    assert.deepEqual(check({ Field: 'Year', Op: 'Eq', Value: 1999.5 }), { value: T.ruleWholeNumber });
    assert.deepEqual(check({ Field: 'Year', Op: 'Between', From: 1990, To: 1980 }), { node: T.ruleRangeOrder });
    assert.deepEqual(check({ Field: 'Year', Op: 'Between', From: 1800, To: 2100 }), { node: 'The range is at most 200 wide.' });
    assert.deepEqual(check({ Field: 'CommunityRating', Op: 'Ge', Value: 'abc' }), { value: T.ruleNumberInvalid });
    assert.deepEqual(check({ Field: 'Runtime', Op: 'Ge', Value: -1 }), { value: 'Enter a number of at least 0.' });
    assert.deepEqual(check({ Field: 'PremiereDate', Op: 'Before', Value: '2001-02-30' }), { value: T.ruleDateInvalid });
    assert.deepEqual(check({ Field: 'PremiereDate', Op: 'After', Value: '1700-01-01' }), { value: T.ruleDateRange });
    assert.deepEqual(check({ Field: 'DateAdded', Op: 'After', Value: '2020-01-01T10:00:00Z' }), {});
    assert.deepEqual(check({ Field: 'DateAdded', Op: 'InLast', Amount: 11, Unit: 'Years' }), { amount: 'Enter a whole number from 1 to 10.' });
    assert.deepEqual(check({ Field: 'Genres', Op: 'AnyOf', Values: [] }), { values: T.ruleValuesRequired });
    assert.deepEqual(check({ Field: 'SeriesStatus', Op: 'AnyOf', Values: ['Ended'] }, ['Movie']), { field: 'Show status doesn\'t apply to the item types searched.'.replace('Show status', catalog.byId.SeriesStatus.Label) });
    assert.deepEqual(check({ Field: 'Played', Op: 'Is', Value: true }), {});

    const tree = t.ssTreeFromWhere({ Match: 'All', Children: [{ Match: 'Any', Children: [] }, { Field: 'Title', Op: 'Contains', Value: 'a' }] }, catalog);
    const found = t.ssCheckTree(tree, catalog, ALL_KINDS);
    assert.equal(found.count, 1);
    assert.deepEqual(plain(found.errors[tree.children[0].id]), { node: T.groupEmpty });
    assert.deepEqual(plain(t.ssCheckTree(t.ssNewGroup('Any', false, []), catalog, ALL_KINDS)).count, 1, 'an empty root only as All');
    assert.equal(t.ssCheckTree(t.ssNewGroup('All', false, []), catalog, ALL_KINDS).count, 0);

    // Paths: Children by index, the property to the control; "Query." for a saved query's; outside the Where, null.
    const nested = t.ssTreeFromWhere({ Match: 'All', Children: [{ Field: 'Title', Op: 'Contains', Value: 'a' }, { Match: 'Any', Children: [{ Field: 'Year', Op: 'Between', From: 1, To: 2 }] }] }, catalog);
    const inner = nested.children[1].children[0];
    assert.deepEqual(plain(t.ssNodeOfPath(nested, 'Where.Children[1].Children[0].From')), { id: inner.id, part: 'from' });
    assert.deepEqual(plain(t.ssNodeOfPath(nested, 'Query.Where.Children[1].Children[0]')), { id: inner.id, part: 'node' });
    assert.deepEqual(plain(t.ssNodeOfPath(nested, 'Where.Children[0].Values[3]')), { id: nested.children[0].id, part: 'values' });
    assert.deepEqual(plain(t.ssNodeOfPath(nested, 'Where.Children[7].Value')), { id: nested.id, part: 'node' });
    assert.equal(t.ssNodeOfPath(nested, 'Scope.LibraryIds[0]'), null);
});

// ------------------------------------------------------------------------------------------------ the advanced form

const SERIES_ID = 'c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3';

// The lookups the advanced form's editors use: shows by name, any field's values, and the server's check.
function respondAdvanced(s, validate) {
    s.page.respond('GET', '/SmartSearch/Series', [
        { Id: SERIES_ID, Name: 'Test Series', ProductionYear: 2020, LibraryId: SHOWS },
        { Id: 'c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4', Name: '<b>Tést</b>‮ שלום\u0007', ProductionYear: null, LibraryId: SHOWS }
    ]);
    s.page.respond('GET', /^\/SmartSearch\/Values\/(Tags|Studios|OfficialRating|Container)$/, (entry) => {
        const field = entry.path.split('/')[3];
        const values = { Tags: ['classic', 'cult'], Studios: ['A24'], OfficialRating: ['PG-13', 'R'], Container: ['mkv', 'mp4'] }[field];
        return values.filter((v) => v.startsWith(entry.query.prefix || '')).map((v, i) => ({ Value: v, Count: 9 - i }));
    });
    s.page.respond('POST', '/SmartSearch/Validate', validate || { Valid: true, Supported: true, Errors: [], UsesPerUserFields: false, UsesAdminOnlyFields: false, UnavailableFields: [] });
}

// The page in the advanced form, every item type searched.
async function advanced(options) {
    const s = await shown(Object.assign({ fields: ALL_FIELDS }, options || {}));
    respondAdvanced(s);
    s.q('[data-jfss-mode="advanced"]').click();
    s.all('.jfss-kind').forEach((b) => {
        if (b.getAttribute('aria-pressed') !== 'true') {
            b.click();
        }
    });
    s.rule = () => s.all('.jfss-rule').at(-1);
    // A new rule in the root group on this field and operator.
    s.addRule = (field, op) => {
        s.q('.jfss-group-root > .jfss-group-add .jfss-add-rule').click();
        s.change(s.rule().querySelector('.jfss-rule-field'), field);
        if (op) {
            s.change(s.rule().querySelector('.jfss-rule-op'), op);
        }

        return s.rule();
    };
    s.where = () => s.searches().at(-1).body.Query.Where;
    return s;
}

test('Basic and Advanced: the basic fields become rules; Basic comes back while the rules are the basic form\'s; the tree is kept in the hash', { skip }, async () => {
    const s = await shown({ fields: ALL_FIELDS });
    respondAdvanced(s);
    assert.deepEqual(pressed(s, '.jfss-mode'), ['Basic']);
    assert.equal(s.q('.jfss-advanced').classList.contains('hide'), true);
    type(s, s.q('.jfss-title'), 'alien');
    s.q('.jfss-year-from').value = '1979';
    s.q('[data-jfss-mode="advanced"]').click();
    assert.deepEqual(pressed(s, '.jfss-mode'), ['Advanced']);
    assert.equal(s.q('.jfss-basic-fields').classList.contains('hide'), true);
    assert.deepEqual(s.all('.jfss-rule-field').map((n) => n.value), ['Title', 'Year']);
    assert.deepEqual(s.all('.jfss-rule-op').map((n) => n.value), ['Contains', 'Ge']);
    assert.equal(s.page.document.activeElement, s.q('.jfss-rule-field'), 'the first rule\'s field takes the focus');
    assert.equal(s.q('[data-jfss-mode="basic"]').disabled, false);

    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    const where = { Match: 'All', Children: [{ Field: 'Title', Op: 'Contains', Value: 'alien' }, { Field: 'Year', Op: 'Ge', Value: 1979 }] };
    assert.deepEqual(s.searches()[0].body.Query.Where, where);
    assert.equal(s.params().mode, 'adv');
    assert.deepEqual(JSON.parse(s.params().where), where);

    // A group isn't the basic form's: Basic is off, and says why.
    s.q('.jfss-group-root > .jfss-group-add .jfss-add-group').click();
    assert.equal(s.q('[data-jfss-mode="basic"]').disabled, true);
    assert.equal(s.q('.jfss-mode-note').textContent, 'This search uses advanced rules, which the basic form can\'t show.');
    assert.equal(s.page.document.activeElement, s.all('.jfss-match')[1], 'the new group\'s match takes the focus');
    s.all('.jfss-group:not(.jfss-group-root) .jfss-remove-node')[0].click();
    assert.equal(s.q('[data-jfss-mode="basic"]').disabled, false);
    s.q('[data-jfss-mode="basic"]').click();
    assert.equal(s.q('.jfss-title').value, 'alien');
    assert.equal(s.q('.jfss-year-from').value, '1979');
    assert.equal(s.page.document.activeElement, s.q('.jfss-title'));

    // The hash's tree comes back in the advanced form, and runs.
    const link = await shown({ fields: ALL_FIELDS, hash: '#/jfss-search?kinds=Movie&mode=adv&where=' + encodeURIComponent(JSON.stringify({ Match: 'Any', Children: [{ Field: 'Played', Op: 'Is', Value: false }] })) + '&run=1' });
    assert.deepEqual(pressed(link, '.jfss-mode'), ['Advanced']);
    assert.equal(link.q('.jfss-match').value, 'Any');
    assert.equal(link.q('.jfss-rule-bool').value, 'false');
    assert.deepEqual(link.searches()[0].body.Query.Where, { Match: 'Any', Children: [{ Field: 'Played', Op: 'Is', Value: false }] });
    const broken = await shown({ fields: ALL_FIELDS, hash: '#/jfss-search?mode=adv&where=%7Bnot%20json' });
    assert.deepEqual(pressed(broken, '.jfss-mode'), ['Advanced']);
    assert.equal(broken.all('.jfss-rule').length, 0);
    assert.deepEqual(s.page.errors, []);
});

test('every field can be built with its editor, and the search sends each rule as the server reads it', { skip }, async () => {
    const s = await advanced();
    const value = (box, text, part) => type(s, box.querySelector('.jfss-rule-input.jfss-rule-' + (part || 'value')), text);
    const choose = (box, text) => box.querySelectorAll('.jfss-choice').find((b) => b.textContent === text).click();
    const pick = async (box, text, how) => {
        const input = box.querySelector('.jfss-picker-input');
        type(s, input, text);
        if (how === 'suggestion') {
            await s.page.advance(300);
            box = s.rule();
            box.querySelector('.jfss-suggestion').click();
        } else if (how === 'button') {
            box.querySelector('.jfss-picker-add').click();
        } else {
            s.page.key(input, 'Enter');
        }
    };
    const steps = [
        ['Title', 'Contains', (b) => {
            value(b, 'alien');
            b.querySelector('.jfss-rule-original').checked = true;
            s.page.dispatch(b.querySelector('.jfss-rule-original'), 'change');
        }, { Value: 'alien', IncludeOriginalTitle: true }],
        ['OriginalTitle', 'StartsWith', (b) => value(b, 'Ali'), { Value: 'Ali' }],
        ['Overview', 'NotContains', (b) => value(b, 'boring'), { Value: 'boring' }],
        ['SeriesName', 'Eq', (b) => value(b, 'Test Series'), { Value: 'Test Series' }],
        ['Series', 'AnyOf', (b) => pick(b, 'te', 'suggestion'), { Values: [{ Id: SERIES_ID, Name: 'Test Series' }] }],
        ['Path', 'Contains', (b) => value(b, 'extras'), { Value: 'extras' }],
        ['Year', 'Between', (b) => {
            value(b, '1979', 'from');
            value(s.rule(), '1986', 'to');
        }, { From: 1979, To: 1986 }],
        ['PremiereDate', 'Before', (b) => value(b, '2000-01-01'), { Value: '2000-01-01' }],
        ['DateAdded', 'InLast', (b) => {
            type(s, b.querySelector('.jfss-rule-amount'), '30');
            s.change(b.querySelector('.jfss-rule-unit'), 'Weeks');
        }, { Amount: 30, Unit: 'Weeks' }],
        ['Library', 'AnyOf', (b) => choose(b, 'Movies'), { Values: [MOVIES] }],
        ['ItemType', 'NoneOf', (b) => choose(b, 'Episode'), { Values: ['Episode'] }],
        ['Genres', 'AllOf', async (b) => {
            await pick(b, 'Horror');
            await pick(s.rule(), 'Comedy', 'button');
        }, { Values: ['Horror', 'Comedy'] }],
        ['Tags', 'IsEmpty', () => {}, {}],
        ['Studios', 'NoneOf', (b) => pick(b, 'A2', 'suggestion'), { Values: ['A24'] }],
        ['OfficialRating', 'Le', (b) => pick(b, 'PG', 'suggestion'), { Value: 'PG-13' }],
        ['SeriesStatus', 'AnyOf', (b) => choose(b, 'Ended'), { Values: ['Ended'] }],
        ['CommunityRating', 'Ge', (b) => value(b, '7.5'), { Value: 7.5 }],
        ['CriticRating', 'Lt', (b) => value(b, '50'), { Value: 50 }],
        ['Runtime', 'Gt', (b) => value(b, '120'), { Value: 120 }],
        ['Resolution', 'AnyOf', (b) => choose(b, '2160p'), { Values: ['2160p'] }],
        ['Container', 'AnyOf', (b) => pick(b, 'mkv'), { Values: ['mkv'] }],
        ['VideoType', 'AnyOf', (b) => choose(b, 'Blu-ray'), { Values: ['BluRay'] }],
        ['Is3D', 'Is', (b) => s.change(b.querySelector('.jfss-rule-bool'), 'false'), { Value: false }],
        ['FileSize', 'Ge', (b) => value(b, '1000000000'), { Value: 1000000000 }],
        ['Bitrate', 'Le', (b) => value(b, '8000000'), { Value: 8000000 }],
        ['Played', 'Is', () => {}, { Value: true }],
        ['Favorite', 'Is', (b) => s.change(b.querySelector('.jfss-rule-bool'), 'false'), { Value: false }],
        ['InProgress', 'Is', () => {}, { Value: true }],
        ['PlayCount', 'Eq', (b) => value(b, '0'), { Value: 0 }],
        ['LastPlayed', 'NotInLast', (b) => {
            type(s, b.querySelector('.jfss-rule-amount'), '2');
            s.change(b.querySelector('.jfss-rule-unit'), 'Years');
        }, { Amount: 2, Unit: 'Years' }],
        ['HasProviderId', 'Is', (b) => s.change(b.querySelector('.jfss-rule-provider'), 'Imdb'), { Value: true, Provider: 'Imdb' }],
        ['ProviderId', 'Eq', (b) => value(b, 'tt0078748'), { Value: 'tt0078748', Provider: 'Tmdb' }],
        ['Locked', 'Is', (b) => s.change(b.querySelector('.jfss-rule-bool'), 'false'), { Value: false }]
    ];
    assert.deepEqual(steps.map((x) => x[0]).sort(), ALL_FIELDS.Fields.map((f) => f.Id).sort(), 'every field of the catalogue');
    for (const [field, op, fill] of steps) {
        const box = s.addRule(field, op);
        assert.equal(box.querySelector('.jfss-rule-field').value, field);
        assert.equal(box.querySelector('.jfss-rule-op').value, op);
        await fill(box);
    }

    // A rule without a note draws nothing in its place (append(null) would show "null").
    for (const box of s.all('.jfss-rule')) {
        assert.ok(!/null|undefined/.test(box.textContent), box.textContent);
    }

    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.equal(s.q('.jfss-form-error').textContent, '', JSON.stringify(s.all('.jfss-node-error').map((n) => n.textContent).filter(Boolean)));
    assert.deepEqual(s.where(), { Match: 'All', Children: steps.map(([field, op, , rest]) => Object.assign({ Field: field, Op: op }, rest)) });
    assert.deepEqual(s.searches()[0].body.Query.Scope.ItemKinds, ALL_KINDS);
    // Every control of the tree has a name.
    for (const control of s.q('.jfss-advanced').querySelectorAll('select, input, button')) {
        const named = control.getAttribute('aria-label') || control.textContent || (control.closest('label') && control.closest('label').textContent);
        assert.ok(named, control.className + ' has a name');
    }

    assert.deepEqual(s.page.errors, []);
});

test('the tree from the keyboard: each action leaves the focus on the control it made, or the nearest one left; the limits turn actions off with a reason', { skip }, async () => {
    const s = await advanced();
    const active = () => s.page.document.activeElement;
    assert.equal(s.all('.jfss-rule').length, 0);
    assert.equal(s.q('.jfss-group-empty').textContent, 'No rules: the search finds everything in its scope.');
    s.q('.jfss-add-rule').click();
    assert.equal(active(), s.q('.jfss-rule-field'));
    assert.equal(s.q('.jfss-rule').getAttribute('aria-label'), 'Rule 1');
    assert.equal(s.q('.jfss-rule').getAttribute('role'), 'group');
    s.change(s.q('.jfss-rule-field'), 'Year');
    assert.equal(active(), s.q('.jfss-rule-field'), 'a new field keeps the focus on the field');
    s.change(s.q('.jfss-rule-op'), 'Between');
    assert.equal(active(), s.q('.jfss-rule-op'));
    assert.ok(s.q('.jfss-rule-from') && s.q('.jfss-rule-to'));
    s.q('.jfss-rule .jfss-duplicate-node').click();
    assert.equal(s.all('.jfss-rule').length, 2);
    assert.equal(active(), s.all('.jfss-rule-field')[1]);
    s.all('.jfss-rule .jfss-remove-node')[1].click();
    assert.equal(active(), s.all('.jfss-rule-field')[0], 'the one before');
    s.q('.jfss-rule .jfss-remove-node').click();
    assert.equal(active(), s.q('.jfss-add-rule'), 'the group\'s Add rule');

    // Four levels: the deepest group can't take another.
    for (let depth = 1; depth < 4; depth++) {
        s.all('.jfss-group').at(-1).querySelector('.jfss-add-group').click();
    }

    const groups = s.all('.jfss-group');
    assert.equal(groups.length, 4);
    assert.equal(groups[1].getAttribute('aria-label'), 'Group, Any of the following');
    const deepest = groups[3].querySelector('.jfss-add-group');
    assert.equal(deepest.disabled, true);
    assert.equal(deepest.title, 'Groups can be nested at most 4 deep.');
    // Negation with the match: None of.
    s.change(s.all('.jfss-match')[3], 'None');
    assert.equal(s.all('.jfss-group')[3].getAttribute('aria-label'), 'Group, None of the following');
    // Ungroup moves the group's children up and focuses the first.
    s.all('.jfss-group')[3].querySelector('.jfss-unwrap').click();
    assert.equal(s.all('.jfss-group').length, 3);
    assert.equal(active(), s.all('.jfss-group')[2].querySelectorAll('.jfss-rule-field')[1]);
    // Removing a group focuses its neighbour, or its group's Add rule.
    s.all('.jfss-group')[1].querySelector('.jfss-remove-node').click();
    assert.equal(s.all('.jfss-group').length, 1);
    assert.equal(active(), s.q('.jfss-add-rule'));
    // Fifty rules: Add rule and Duplicate are off.
    for (let i = 0; i < 50; i++) {
        s.q('.jfss-group-root > .jfss-group-add .jfss-add-rule').click();
    }

    assert.equal(s.all('.jfss-rule').length, 50);
    assert.equal(s.q('.jfss-add-rule').disabled, true);
    assert.equal(s.q('.jfss-add-rule').title, 'A search has at most 50 rules.');
    assert.equal(s.q('.jfss-rule .jfss-duplicate-node').disabled, true);
    assert.deepEqual(s.page.errors, []);
});

test('inline checks: a rule\'s problem under it, its control marked and focused, nothing sent; the server\'s errors under the rule their path names', { skip }, async () => {
    const s = await advanced();
    s.addRule('Title', 'Contains');
    s.addRule('Year', 'Between');
    type(s, s.rule().querySelector('.jfss-rule-from'), '1990');
    type(s, s.rule().querySelector('.jfss-rule-to'), '1980');
    s.q('.jfss-group-root > .jfss-group-add .jfss-add-group').click();
    s.change(s.all('.jfss-rule')[2].querySelector('.jfss-rule-field'), 'Genres');
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.equal(s.searches().length, 0);
    const errors = s.all('.jfss-node-error').map((n) => n.textContent);
    assert.deepEqual(errors.filter(Boolean), ['Enter a value.', 'The first value is after the second.', 'Add at least one value.']);
    assert.equal(s.q('.jfss-form-error').textContent, 'Check the rules marked above.');
    const title = s.all('.jfss-rule')[0].querySelector('.jfss-rule-value');
    assert.equal(title.getAttribute('aria-invalid'), 'true');
    assert.equal(s.page.document.activeElement, title, 'the first marked control');
    assert.equal(s.q('.jfss-node-error').getAttribute('role'), 'alert');
    // A field that applies to none of the kinds searched.
    s.all('.jfss-kind').filter((b) => b.getAttribute('data-jfss-kind') !== 'Movie').forEach((b) => b.click());
    s.change(s.all('.jfss-rule')[0].querySelector('.jfss-rule-field'), 'SeriesName');
    assert.equal(s.all('.jfss-rule')[0].querySelectorAll('option').find((o) => o.value === 'SeriesName').textContent, 'Series name (not for these item types)');
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.equal(s.all('.jfss-node-error')[1].textContent.startsWith('Series name doesn\'t apply'), true, s.all('.jfss-node-error')[1].textContent);

    // Fixed here, refused by the server: its error under the rule its path names.
    const fresh = await advanced();
    fresh.addRule('Title', 'Contains');
    type(fresh, fresh.rule().querySelector('.jfss-rule-value'), 'a');
    fresh.q('.jfss-group-root > .jfss-group-add .jfss-add-group').click();
    fresh.change(fresh.all('.jfss-rule')[1].querySelector('.jfss-rule-field'), 'Year');
    type(fresh, fresh.all('.jfss-rule')[1].querySelector('.jfss-rule-value'), '1999');
    fresh.page.respond('POST', '/SmartSearch/Search', { title: 'Invalid query', status: 400, code: 'InvalidQuery', errors: [{ path: 'Where.Children[1].Children[0].Value', code: 'OutOfRange', message: 'Not on this server.' }, { path: 'Scope.ItemKinds', code: 'X', message: 'Kinds wrong.' }] }, 400);
    fresh.q('.jfss-search-button').click();
    await fresh.page.advance(100);
    const year = fresh.all('.jfss-rule')[1];
    assert.equal(year.querySelector('.jfss-node-error').textContent, 'Not on this server.');
    assert.equal(year.querySelector('.jfss-rule-value').getAttribute('aria-invalid'), 'true');
    assert.equal(fresh.page.document.activeElement, year.querySelector('.jfss-rule-value'));
    assert.match(fresh.root.textContent, /Kinds wrong\./);
    // Changing the tree's shape clears them.
    fresh.q('.jfss-group-root > .jfss-group-add .jfss-add-rule').click();
    assert.equal(fresh.all('.jfss-node-error').map((n) => n.textContent).join(''), '');
    assert.deepEqual(s.page.errors, []);
});

test('the show picker: suggestions after two letters only, keyboard through them, a chip per show; names and values are text', { skip }, async () => {
    const s = await advanced();
    s.addRule('Series', 'AnyOf');
    const input = () => s.rule().querySelector('.jfss-picker-input');
    assert.equal(input().getAttribute('aria-label'), 'Find a show (2 letters or more)');
    assert.equal(s.rule().querySelector('.jfss-picker-add'), null, 'a show can\'t be typed');
    type(s, input(), 't');
    await s.page.advance(300);
    assert.equal(s.calls('GET', '/SmartSearch/Series').length, 0);
    type(s, input(), 'té');
    await s.page.advance(300);
    assert.equal(s.calls('GET', '/SmartSearch/Series').length, 1);
    assert.equal(s.calls('GET', '/SmartSearch/Series')[0].query.prefix, 'té');
    const list = s.rule().querySelectorAll('.jfss-suggestion');
    assert.deepEqual(list.map((b) => b.textContent), ['Test Series (2020)', '<b>Tést</b>‮ שלום�']);
    assert.equal(s.page.key(input(), 'ArrowDown'), false);
    assert.equal(s.page.document.activeElement, list[0]);
    s.page.key(list[0], 'ArrowDown');
    assert.equal(s.page.document.activeElement, list[1]);
    s.page.key(list[1], 'Escape');
    assert.equal(s.page.document.activeElement, input());
    list[1].click();
    type(s, input(), 'te');
    await s.page.advance(300);
    assert.deepEqual(s.rule().querySelectorAll('.jfss-suggestion').map((b) => b.textContent), ['Test Series (2020)'], 'a chosen show isn\'t suggested again');
    s.rule().querySelector('.jfss-suggestion').click();
    const chips = s.rule().querySelectorAll('.jfss-chip');
    assert.equal(chips.length, 2);
    assert.equal(s.rule().querySelectorAll('.jfss-chips b').length, 0, 'markup stays text');
    assert.equal(chips[0].querySelector('bdi').textContent, '<b>Tést</b>‮ שלום�');
    assert.equal(chips[0].getAttribute('aria-label'), 'Remove <b>Tést</b>‮ שלום�');
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.deepEqual(s.where().Children[0].Values.map((v) => v.Id), ['c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4', SERIES_ID]);
    // A chip removed from the keyboard gives the focus back to the field.
    s.rule().querySelector('.jfss-chip').click();
    assert.equal(s.rule().querySelectorAll('.jfss-chip').length, 1);
    assert.equal(s.page.document.activeElement, input());
    // A typed value of another field: markup and controls stay text, in the hash too.
    s.addRule('Tags', 'AnyOf');
    type(s, s.rule().querySelector('.jfss-picker-input'), '<img src=x onerror="window.__pwned=1">\u0000');
    s.page.key(s.rule().querySelector('.jfss-picker-input'), 'Enter');
    assert.equal(s.rule().querySelectorAll('img').length, 0);
    assert.equal(s.rule().querySelector('.jfss-chip bdi').textContent, '<img src=x onerror="window.__pwned=1">�');
    s.q('.jfss-search-button').click();
    await s.page.advance(100);
    assert.deepEqual(s.where().Children[1].Values, ['<img src=x onerror="window.__pwned=1">\u0000']);
    const back = await shown({ fields: ALL_FIELDS, hash: s.page.context.location.hash });
    assert.equal(back.all('.jfss-rule')[1].querySelectorAll('img').length, 0);
    assert.equal(back.all('.jfss-rule')[1].querySelector('.jfss-chip bdi').textContent, '<img src=x onerror="window.__pwned=1">�');
    assert.equal(s.page.context.__pwned, undefined);
    assert.deepEqual(s.page.errors, []);
});

test('a saved advanced query: the owner edits it and Save sends it with IfUpdatedAt; a rule the page can\'t edit stays, and a query with errors opens with them under its rules', { skip }, async () => {
    const where = { Match: 'All', Negate: false, Children: [
        { Field: 'Genres', Op: 'AnyOf', Values: ['Horror'], Negate: false },
        { Field: 'Mood', Op: 'Eq', Value: 'calm', Negate: false },
        { Match: 'Any', Negate: true, Children: [{ Field: 'Played', Op: 'Is', Value: true, Negate: false }] }
    ] };
    const mine = savedQuery(QA, 'Spooky', { Query: basicQuery('x', { Where: where }) });
    const s = await shown({ hash: '#/jfss-search?query=' + QA, saved: [mine], fields: ALL_FIELDS });
    respondAdvanced(s);
    assert.deepEqual(pressed(s, '.jfss-mode'), ['Advanced']);
    assert.equal(s.calls('POST', '/SmartSearch/Queries/' + QA + '/Search').length, 1);
    const locked = s.all('.jfss-rule')[1];
    assert.equal(locked.classList.contains('jfss-rule-locked'), true);
    assert.equal(locked.querySelector('.jfss-rule-field').disabled, true);
    assert.equal(locked.querySelectorAll('option').find((o) => o.value === 'Mood').textContent, 'Mood (can\'t be edited here)');
    assert.equal(locked.querySelector('.jfss-rule-note').textContent, 'This rule can\'t be edited here; it stays as it is. You can duplicate or remove it.');
    assert.equal(s.all('.jfss-match')[1].value, 'None');
    assert.equal(s.q('.jfss-save').disabled, true, 'unchanged');
    s.change(s.all('.jfss-match')[1], 'Any');
    assert.equal(s.q('.jfss-save').disabled, false);
    s.q('.jfss-save').click();
    await s.page.advance(50);
    const put = s.calls('PUT', '/SmartSearch/Queries/' + QA);
    assert.equal(put.length, 1);
    assert.equal(put[0].body.IfUpdatedAt, mine.UpdatedUtc);
    assert.deepEqual(put[0].body.Query.Where, { Match: 'All', Children: [
        { Field: 'Genres', Op: 'AnyOf', Values: ['Horror'] },
        { Field: 'Mood', Op: 'Eq', Value: 'calm', Negate: false },
        { Match: 'Any', Children: [{ Field: 'Played', Op: 'Is', Value: true }] }
    ] });
    assert.deepEqual(put[0].body.Query.Sort, [{ Key: 'ProductionYear', Order: 'Descending' }]);
    assert.equal(s.q('.jfss-save').disabled, true, 'saved: unchanged again');

    // Imported with an error: opened, not run, the error under its rule, the focus left alone.
    const broken = savedQuery(QB, 'Broken', { Valid: false, Errors: [{ Path: 'Query.Where.Children[0].Values', Code: 'Empty', Message: 'Add at least one genre.' }], Query: basicQuery('x', { Where: { Match: 'All', Children: [{ Field: 'Genres', Op: 'AnyOf', Values: [] }] } }) });
    const b = await shown({ hash: '#/jfss-search?query=' + QB, saved: [broken], fields: ALL_FIELDS });
    assert.equal(b.calls('POST', /\/Search$/).length, 0);
    assert.deepEqual(pressed(b, '.jfss-mode'), ['Advanced']);
    assert.equal(b.q('.jfss-rule .jfss-node-error').textContent, 'Add at least one genre.');
    assert.equal(b.q('.jfss-rule .jfss-picker-input').getAttribute('aria-invalid'), 'true');
    assert.deepEqual(s.page.errors, []);
});

test('Save as new in the advanced form: the server\'s check first, its errors under the rules before a name is asked; an administrator-only field isn\'t offered for sharing', { skip }, async () => {
    const s = await advanced();
    s.addRule('Path', 'Contains');
    type(s, s.rule().querySelector('.jfss-rule-value'), 'extras');
    s.page.respond('POST', '/SmartSearch/Validate', { Valid: false, Supported: true, Errors: [{ Path: 'Where.Children[0].Value', Code: 'X', Message: 'No such folder.' }], UsesAdminOnlyFields: true });
    s.q('.jfss-save').click();
    await s.page.advance(50);
    assert.equal(s.dialogs().length, 0);
    assert.equal(s.q('.jfss-rule .jfss-node-error').textContent, 'No such folder.');
    s.page.respond('POST', '/SmartSearch/Validate', { Valid: true, Supported: true, Errors: [], UsesAdminOnlyFields: true });
    s.q('.jfss-save').click();
    await s.page.advance(50);
    assert.equal(s.dialog().querySelector('.jfss-share-box'), null, 'no sharing for a query with an administrator-only field');
    assert.deepEqual(s.page.errors, []);
});

test('on the TV layout the advanced form takes no focus by itself, and its controls stack for the remote', { skip }, async () => {
    const s = await shown({ tv: true, fields: ALL_FIELDS, hash: '#/jfss-search?kinds=Movie&mode=adv&where=' + encodeURIComponent(JSON.stringify({ Match: 'All', Children: [{ Field: 'Title', Op: 'Contains', Value: 'a' }] })) });
    assert.equal(s.root.classList.contains('jfss-tv'), true);
    assert.equal(s.q('.jfss-advanced').classList.contains('hide'), false);
    assert.notEqual(s.page.document.activeElement, s.q('.jfss-rule-field'));
    s.q('.jfss-add-rule').click();
    assert.equal(s.page.document.activeElement, s.all('.jfss-rule-field')[1], 'an action from the remote moves the focus as on a desktop');
    assert.deepEqual(s.page.errors, []);
});

// ------------------------------------------------------------------------------------------------ the header button

// jellyfin-web 10.11.5's legacy header: its right side with the cast, search and user buttons, the search button hidden
// until a local user is signed in.
function legacyHeader(page, opts) {
    opts = opts || {};
    const search = page.element('button', { type: 'button', is: 'paper-icon-button-light', class: 'headerButton headerButtonRight headerSearchButton' + (opts.signedIn === false ? ' hide' : '') });
    const right = page.element('div', { class: 'headerRight' }, [
        page.element('button', { is: 'paper-icon-button-light', class: 'headerCastButton castButton headerButton headerButtonRight hide' }),
        search,
        page.element('button', { is: 'paper-icon-button-light', class: 'headerButton headerButtonRight headerUserButton' })
    ]);
    const header = page.element('div', { class: 'skinHeader' }, [page.element('div', { class: 'headerTop' }, [right])]);
    page.document.body.append(header);
    return { header, right, search };
}

function showView(s, restored) {
    SHOW_EVENTS.forEach((type) => s.page.dispatch(s.view, type, { detail: { isRestored: !!restored } }));
}

function navigate(s, hash) {
    s.page.context.location.hash = hash;
    s.page.dispatch(s.page.context, 'hashchange');
}

// The script on #/home, signed in, with the header (unless opts.header is false) and a view shown.
async function home(opts) {
    opts = opts || {};
    const s = setup({ hash: opts.hash || '#/home', head: opts.head, tv: opts.tv });
    if (opts.entryPoints !== undefined) {
        const block = s.page.element('script', { type: 'application/json', 'data-jfss-entrypoints': '' });
        block.textContent = opts.entryPoints;
        s.page.document.head.append(block);
    }

    s.page.respond('POST', '/SmartSearch/Diagnostics/ClientHealth', '', 204);
    s.dom = opts.header === false ? null : legacyHeader(s.page, opts);
    s.page.run(source, file);
    await s.page.advance(5);
    s.view = s.page.element('div', { id: 'indexPage', 'data-role': 'page', class: 'page homePage' });
    if (opts.viewFirst) {
        // As on a page load: the first view shows before the kit knows the user.
        s.page.document.body.append(s.view);
        showView(s);
        await s.page.advance(5);
    }

    s.page.installApiClient({ userId: 'user-1', items: {} });
    await s.page.advance(300);
    if (!opts.viewFirst) {
        s.page.document.body.append(s.view);
        showView(s);
    }

    await s.page.advance(300);
    s.ours = () => s.page.document.querySelectorAll('button[data-jfss-header]');
    return s;
}

test('the header button: right before jellyfin-web\'s search button, shaped like it, and it opens the page', { skip }, async () => {
    const s = await home();
    const ours = s.ours();
    assert.equal(ours.length, 1);
    const button = ours[0];
    assert.equal(button.nextSibling, s.dom.search, 'right before the native search button');
    assert.equal(button.parentNode, s.dom.right);
    assert.equal(button.getAttribute('is'), 'paper-icon-button-light');
    assert.equal(button.getAttribute('type'), 'button');
    assert.equal(button.className, 'headerButton headerButtonRight paper-icon-button-light');
    assert.equal(button.getAttribute('aria-label'), 'Advanced search');
    assert.equal(button.getAttribute('title'), 'Advanced search');
    assert.equal(button.querySelector('.material-icons').className, 'material-icons manage_search');
    assert.equal(button.classList.contains('hide'), false);
    button.click();
    assert.equal(s.page.context.location.hash, '#/jfss-search');
    assert.deepEqual(s.page.warnings.filter((w) => w.includes('web anchor')), []);
    assert.deepEqual(s.page.errors, []);
});

test('the header button shows exactly when jellyfin-web\'s search button does: hidden before sign-in and after sign-out', { skip }, async () => {
    const s = await home({ signedIn: false });
    const button = s.ours()[0];
    assert.ok(button, 'added, hidden, while the native button is hidden');
    assert.equal(button.classList.contains('hide'), true);
    s.dom.search.classList.remove('hide');
    await s.page.advance(1);
    assert.equal(button.classList.contains('hide'), false, 'shown with the native button, through the attribute observer');
    s.dom.search.classList.add('hide');
    await s.page.advance(1);
    assert.equal(button.classList.contains('hide'), true, 'hidden with it again (signed out)');
    assert.equal(s.ours().length, 1);
    assert.deepEqual(s.page.errors, []);
});

test('the header button survives navigation and a header jellyfin-web builds again: one button, in the shown header', { skip }, async () => {
    const s = await home();
    const first = s.ours()[0];
    navigate(s, '#/movies');
    showView(s, true);
    await s.page.advance(300);
    assert.equal(s.ours().length, 1);
    assert.equal(s.ours()[0], first, 'the same button after a navigation');

    s.dom.header.remove();
    const again = legacyHeader(s.page);
    await s.page.advance(400);
    assert.equal(s.ours().length, 1);
    assert.equal(s.ours()[0].nextSibling, again.search, 'in the new header, before its search button');
    again.search.classList.add('hide');
    await s.page.advance(1);
    assert.equal(s.ours()[0].classList.contains('hide'), true, 'following the new header\'s button');
    assert.deepEqual(s.page.errors, []);
});

test('no header button while the page is off or ShowHeaderButton is off', { skip }, async () => {
    const off = await home({ head: { off: ['search'] } });
    assert.equal(off.ours().length, 0, 'the page turned off');
    const hidden = await home({ entryPoints: '{"headerButton":false}' });
    assert.equal(hidden.ours().length, 0, 'the setting off');
    const on = await home({ entryPoints: '{"headerButton":true}' });
    assert.equal(on.ours().length, 1, 'the setting on');
    const broken = await home({ entryPoints: 'not json' });
    assert.equal(broken.ours().length, 1, 'an unreadable block takes the default');
    for (const s of [off, hidden, on]) {
        assert.deepEqual(s.page.errors, []);
        assert.deepEqual(s.page.warnings.filter((w) => w.includes('web anchor')), [], 'nothing to check while off');
    }
});

test('without jellyfin-web\'s search button (the experimental layout): no button, one console warning and one health report, never a throw', { skip }, async () => {
    const s = await home({ header: false, viewFirst: true });
    await s.page.advance(5000);
    assert.equal(s.ours().length, 0);
    assert.equal(s.page.warnings.filter((w) => w.includes('web anchor')).length, 1, 'checked once the user is known, though the first view showed before');
    for (const hash of ['#/movies', '#/tv', '#/home']) {
        navigate(s, hash);
        showView(s);
        await s.page.advance(5000);
    }

    assert.deepEqual(s.page.warnings.filter((w) => w.includes('web anchor')), ['[jfss] web anchor "headerSearch" missing on header (web 10.11.5)']);
    await s.page.advance(3000);
    const reports = s.page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/SmartSearch/Diagnostics/ClientHealth');
    assert.equal(reports.length, 1, 'one report (at most one every 10 minutes)');
    assert.equal(reports[0].body.Missing[0].Anchor, 'headerSearch');
    assert.equal(reports[0].body.Missing[0].View, 'header');
    assert.deepEqual(s.page.errors, []);
});

test('on the TV layout the header button is the same button, a stop for the remote like the native ones', { skip }, async () => {
    const s = await home({ tv: true });
    const button = s.ours()[0];
    assert.ok(button);
    assert.equal(button.tagName, 'BUTTON', 'a button: jellyfin-web\'s focus manager takes it');
    assert.equal(button.nextSibling, s.dom.search);
    assert.equal(button.classList.contains('hide'), false);
    assert.deepEqual(s.page.errors, []);
});

// ------------------------------------------------------------------------------------------------ auto-collections

const DEF1 = '1111111111111111111111111111aa01';
const DEF2 = '2222222222222222222222222222aa02';
const DEF3 = '3333333333333333333333333333aa03';
const BOXSET = '9999999999999999999999999999bb01';
const MANAGER = Object.assign({}, FIELDS, { AutoCollections: { Enabled: true, CanManage: true, IsAdmin: false } });

function autoCollection(id, name, extra, state) {
    return Object.assign({
        Id: id, Name: name, CollectionId: BOXSET, Enabled: true,
        Source: { Mode: 'Reference', SavedQueryId: QA, QueryName: 'Alien nights', QueryVisibleToCaller: true },
        EvaluationUserId: null, EvaluationUserName: null,
        Presentation: { SortMode: 'PremiereDate', Lock: true, OverviewMode: 'LeaveAsIs' },
        ManualEdits: { Mode: 'Respect', Pinned: [], Excluded: [] },
        Safety: { AllowEmpty: false, MaxRemovalPercent: null },
        CreatedByUserId: 'user-1', CreatedByName: 'me', CreatedUtc: '2026-11-01T10:00:00.0000000Z',
        UpdatedByUserId: 'user-1', UpdatedUtc: '2026-11-02T10:00:00.0000000Z',
        State: Object.assign({ Status: 'Ok', LastSuccessUtc: '2026-11-02T10:00:00.0000000Z', LastMemberCount: 12, LastAdded: 3, LastRemoved: 1, MixedAudience: false }, state || {})
    }, extra || {});
}

// The auto-collection endpoints over an in-memory list (db.items; db.after(item): what the next read finds, as the
// server's runner would leave it), answering as SmartSearchAutoCollectionsController does.
function autoCollectionServer(page, items) {
    const db = { items: items.map((i) => JSON.parse(JSON.stringify(i))), n: 0, history: [], after: null, usesPerUser: false };
    const fail = (status, code, title, extra) => ({ status, body: Object.assign({ status, code, title }, extra || {}) });
    const idOf = (entry) => entry.path.split('/')[3];
    const find = (entry) => db.items.find((i) => i.Id === idOf(entry));
    const route = (method, path, handler) => {
        let last = null;
        page.respond(method, path, () => last.body, (entry) => {
            last = handler(entry);
            return last.status;
        });
    };
    route('GET', '/SmartSearch/AutoCollections', () => {
        if (db.after) {
            db.items.forEach(db.after);
        }

        return { status: 200, body: JSON.parse(JSON.stringify(db.items)) };
    });
    route('GET', /^\/SmartSearch\/AutoCollections\/[0-9a-f]{32}$/, (entry) => {
        const item = find(entry);
        return item ? { status: 200, body: item } : fail(404, 'AutoCollectionNotFound', 'No auto-collection has this id.');
    });
    route('GET', /^\/SmartSearch\/AutoCollections\/[0-9a-f]{32}\/History$/, () => ({ status: 200, body: db.history }));
    route('POST', '/SmartSearch/AutoCollections/CheckName', (entry) => {
        const name = entry.body.Name;
        const taken = name === 'Horror 80s';
        return { status: 200, body: { Name: name, Available: !taken, Conflicts: [], Warnings: [], Suggestions: taken ? ['Horror 80s (2)', 'Horror 80s (3)'] : [] } };
    });
    route('POST', '/SmartSearch/AutoCollections/Preview', () => ({ status: 200, body: {
        PreviewHash: 'hash-' + (++db.n), Total: 5, WouldAdd: 5, WouldRemove: 0, AddSamples: [movie(1), movie(2)], RemoveSamples: [],
        HiddenFromCaller: 0, PinsSkipped: 0, MixedAudience: false, Guard: { WouldTrip: false, Reasons: [] }, Warnings: [], ElapsedMs: 40
    } }));
    route('POST', '/SmartSearch/AutoCollections', (entry) => {
        const def = entry.body.Definition;
        const item = autoCollection('4444444444444444444444444444aa0' + (++db.n % 10), def.Name, { CollectionId: null }, { Status: 'Queued', LastSuccessUtc: null });
        db.items.push(item);
        return { status: 202, body: item };
    });
    route('PUT', /^\/SmartSearch\/AutoCollections\/[0-9a-f]{32}$/, (entry) => {
        const item = find(entry);
        if (entry.body.IfUpdatedAt !== item.UpdatedUtc) {
            return fail(409, 'AutoCollectionChanged', 'It changed.');
        }

        if (entry.body.Definition.Enabled !== undefined) {
            item.Enabled = entry.body.Definition.Enabled;
        }

        item.UpdatedUtc = '2026-11-10T00:00:0' + (++db.n % 10) + '.0000000Z';
        item.State.Status = 'Queued';
        return { status: 200, body: item };
    });
    route('POST', /^\/SmartSearch\/AutoCollections\/[0-9a-f]{32}\/Refresh$/, (entry) => {
        find(entry).State.Status = 'Queued';
        return { status: 202, body: '' };
    });
    route('POST', /^\/SmartSearch\/AutoCollections\/[0-9a-f]{32}\/Guard\/(Keep|Apply)$/, (entry) => {
        find(entry).State.Status = 'Queued';
        return { status: 202, body: '' };
    });
    route('POST', /^\/SmartSearch\/AutoCollections\/[0-9a-f]{32}\/Reassign$/, (entry) => {
        const item = find(entry);
        Object.assign(item, { CreatedByUserId: entry.body.UserId, CreatedByName: 'alice' });
        item.State.Status = 'Queued';
        return { status: 200, body: item };
    });
    route('DELETE', /^\/SmartSearch\/AutoCollections\/[0-9a-f]{32}$/, (entry) => {
        db.items = db.items.filter((i) => i.Id !== idOf(entry));
        return { status: 204, body: '' };
    });
    page.respond('POST', '/SmartSearch/Validate', () => ({ Valid: true, Errors: [], UsesPerUserFields: db.usesPerUser, UsesAdminOnlyFields: false }));
    return db;
}

// Texts without the isolation marks around users' names.
function unmarked(texts) {
    return texts.map((t) => t.replace(/[⁦-⁩]/g, ''));
}

// shown() for a manager, on the Auto-collections tab, with its endpoints; options.users: the server's users the
// ApiClient lists.
async function shownWithCollections(items, options) {
    const s = setup(Object.assign({ hash: '#/jfss-search?tab=collections', fields: MANAGER, saved: [savedQuery(QA, 'Alien nights'), bobsQuery()] }, options || {}));
    s.ac = autoCollectionServer(s.page, items);
    s.page.run(source, file);
    await s.page.advance(5);
    s.view = showFallbackPage(s.page);
    const client = s.page.installApiClient({ userId: 'user-1', items: {} });
    if (options && options.users) {
        Object.getPrototypeOf(client).getUsers = () => Promise.resolve(options.users);
    }

    await s.page.advance(400);
    return helpers(s);
}

test('a user who doesn\'t manage collections gets no Auto-collections tab and no create buttons; a link to the tab shows Search', { skip }, async () => {
    const s = await shown({ hash: '#/jfss-search?tab=collections', saved: [savedQuery(QA, 'Alien nights')] });
    assert.deepEqual(s.all('[role="tab"]').map((t) => t.textContent), ['Search', 'Saved queries']);
    assert.equal(s.q('.jfss-collections-panel'), null);
    assert.equal(s.q('.jfss-create-collection'), null);
    assert.equal(s.calls('GET', '/SmartSearch/AutoCollections').length, 0);
    assert.deepEqual(s.page.errors, []);
});

test('the Auto-collections tab lists each with its link, query, status, members, last change and watch-state user, names as text; the filter narrows it', { skip }, async () => {
    const s = await shownWithCollections([
        autoCollection(DEF1, 'Horror <img src=x onerror=alert(1)>', { EvaluationUserId: BOB, EvaluationUserName: 'bob' }, { MixedAudience: true }),
        autoCollection(DEF2, 'Held', {}, { Status: 'Guarded', PendingGuard: { DetectedUtc: '2026-11-09T10:00:00.0000000Z', WouldRemoveCount: 6, WouldRemove: [], HiddenCount: 2, Reasons: ['Would remove 6 of 12 items.'], Approved: false } }),
        autoCollection(DEF3, 'Broken', { CreatedByUserId: BOB, CreatedByName: 'bob', Source: { Mode: 'Reference', SavedQueryId: QC, QueryName: null, QueryVisibleToCaller: false } }, { Status: 'QueryMissing', LastError: 'The saved query is gone.', LastSuccessUtc: null })
    ]);
    assert.deepEqual(s.all('[role="tab"]').map((t) => [t.textContent, t.getAttribute('aria-selected')]), [['Search', 'false'], ['Saved queries', 'false'], ['Auto-collections', 'true']]);
    assert.equal(s.calls('GET', '/SmartSearch/AutoCollections').length, 1);
    assert.equal(s.q('.jfss-ac-count').textContent, '3 auto-collections');
    const rows = s.all('.jfss-ac-row');
    assert.equal(rows.length, 3);
    const link = rows[0].querySelector('a.jfss-ac-open');
    assert.equal(link.textContent, 'Horror <img src=x onerror=alert(1)>');
    assert.equal(link.querySelector('img'), null, 'a name is text');
    assert.ok(link.getAttribute('href').includes(BOXSET));
    const meta = rows[0].querySelectorAll('.jfss-ac-meta > span').map((m) => m.textContent);
    assert.deepEqual(meta.filter((t) => !/^Updated /.test(t)), ['Up to date', 'From Alien nights', '12 items · +3 −1', 'Watch state of bob', 'Mixed audience']);
    assert.deepEqual(rows[0].querySelectorAll('.jfss-ac-action').map((b) => b.getAttribute('aria-label').replace('Horror <img src=x onerror=alert(1)>', 'N')),
        ['Refresh N now', 'Edit N', 'Turn N off', 'History of N', 'Delete N']);
    // Held removals: the count, the reason, what the caller can't see, and Keep, Apply, Wait.
    assert.equal(rows[1].querySelector('.jfss-ac-status').textContent, 'Needs review');
    assert.equal(rows[1].querySelector('.jfss-ac-guard-text').textContent, 'Would remove 6 of its items.');
    assert.equal(rows[1].querySelector('.jfss-ac-guard-hidden').textContent, '2 of them are in libraries you can\'t open.');
    assert.deepEqual(rows[1].querySelectorAll('.jfss-ac-panel button').map((b) => b.textContent), ['Keep these items', 'Apply removals', 'Wait']);
    // Someone else's, whose query is gone: no source, no Turn off, and Choose another query.
    assert.equal(rows[2].querySelector('.jfss-ac-status').textContent, 'Query missing');
    assert.equal(rows[2].querySelector('.jfss-ac-source'), null, 'no source for a query that is gone');
    assert.equal(rows[2].querySelector('.jfss-ac-error').textContent, 'Last error: The saved query is gone.');
    assert.equal(rows[2].querySelector('.jfss-ac-toggle'), null);
    assert.ok(rows[2].querySelector('.jfss-ac-choose'));
    s.q('[data-jfss-ac-filter="review"]').click();
    assert.deepEqual(s.all('.jfss-ac-open').map((a) => a.textContent), ['Held']);
    s.q('[data-jfss-ac-filter="errors"]').click();
    assert.deepEqual(s.all('.jfss-ac-open').map((a) => a.textContent), ['Broken']);
    s.q('[data-jfss-ac-filter="all"]').click();
    assert.equal(s.all('.jfss-ac-row').length, 3);
    assert.deepEqual(s.page.errors, []);
});

test('an administrator gives a deleted creator\'s auto-collection to a manager picked from the server\'s users; a manager who isn\'t one gets no such panel', { skip }, async () => {
    const ALICE = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaa0a11';
    const CAROL = 'cccccccccccccccccccccccccccc0c11';
    const DAVE = 'dddddddddddddddddddddddddddd0d11';
    const ERIN = 'eeeeeeeeeeeeeeeeeeeeeeeeeeee0e11';
    const orphan = () => autoCollection(DEF1, 'Orphan', { CreatedByUserId: BOB, CreatedByName: null }, { Status: 'CreatorMissing', LastError: 'The user who made this auto-collection was deleted.', LastSuccessUtc: null });
    const admin = Object.assign({}, FIELDS, { AutoCollections: { Enabled: true, CanManage: true, IsAdmin: true } });
    const users = [
        { Id: ALICE, Name: 'alice', Policy: { IsAdministrator: true } },
        { Id: CAROL, Name: '<b>carol</b>', Policy: { EnableCollectionManagement: true } },
        { Id: DAVE, Name: 'dave', Policy: { IsAdministrator: false, EnableCollectionManagement: false } },
        { Id: ERIN, Name: 'erin' }
    ];
    const s = await shownWithCollections([orphan()], { fields: admin, users });
    const row = s.q('.jfss-ac-row');
    assert.match(row.querySelector('.jfss-ac-panel-text').textContent, /^The user who made it was deleted\./);
    const select = row.querySelector('.jfss-ac-reassign-select');
    // An empty choice first; a user who can't manage collections isn't offered (one without a policy in the answer is).
    assert.deepEqual(select.querySelectorAll('option').map((o) => [o.value, o.textContent]), [['', 'Choose a user'], [ALICE, 'alice'], [CAROL, '<b>carol</b>'], [ERIN, 'erin']]);
    assert.equal(select.querySelector('b'), null, 'a name is text');
    const give = row.querySelector('.jfss-ac-reassign');
    assert.equal(give.textContent, 'Give to this user');
    assert.equal(give.disabled, true, 'off until someone is picked');
    give.click();
    await s.page.advance(50);
    assert.deepEqual(s.calls('POST', '/SmartSearch/AutoCollections/' + DEF1 + '/Reassign'), []);
    s.change(select, CAROL);
    assert.equal(give.disabled, false);
    give.click();
    await s.page.advance(50);
    assert.deepEqual(s.calls('POST', '/SmartSearch/AutoCollections/' + DEF1 + '/Reassign').map((f) => f.body), [{ UserId: CAROL }]);
    assert.ok(unmarked(s.toasts()).includes('Orphan now belongs to <b>carol</b>.'), s.toasts().join(' | '));
    assert.ok(s.calls('GET', '/SmartSearch/AutoCollections').length >= 2, 'the list is read again');
    assert.deepEqual(s.page.errors, []);

    const m = await shownWithCollections([orphan()], { users: [{ Id: ALICE, Name: 'alice' }] });
    assert.equal(m.q('.jfss-ac-reassign'), null);
    assert.deepEqual(m.page.errors, []);
});

test('Refresh now queues the update, the list is read every 4 s while it runs, and a toast says how it ended; nothing is polled once all are done', { skip }, async () => {
    const s = await shownWithCollections([autoCollection(DEF1, 'Horror 80s')]);
    s.all('.jfss-ac-refresh')[0].click();
    await s.page.advance(50);
    assert.equal(s.calls('POST', '/SmartSearch/AutoCollections/' + DEF1 + '/Refresh').length, 1);
    assert.equal(s.q('.jfss-ac-status').textContent, 'Updating');
    const reads = s.calls('GET', '/SmartSearch/AutoCollections').length;
    s.ac.after = (item) => {
        Object.assign(item.State, { Status: 'Ok', LastAdded: 4, LastRemoved: 2 });
    };
    await s.page.advance(4100);
    assert.equal(s.calls('GET', '/SmartSearch/AutoCollections').length, reads + 1, 'read again after 4 s');
    assert.equal(s.q('.jfss-ac-status').textContent, 'Up to date');
    assert.ok(unmarked(s.toasts()).includes('Horror 80s: +4 −2'), s.toasts().join(' | '));
    const settled = s.calls('GET', '/SmartSearch/AutoCollections').length;
    await s.page.advance(12000);
    assert.equal(s.calls('GET', '/SmartSearch/AutoCollections').length, settled, 'no polling once nothing updates');
    assert.deepEqual(s.page.errors, []);
});

test('held removals: Wait folds the panel away until Review removals; Apply asks first; Keep these items sends the guard\'s time', { skip }, async () => {
    const guard = { DetectedUtc: '2026-11-09T10:00:00.0000000Z', WouldRemoveCount: 6, WouldRemove: [], HiddenCount: 0, Reasons: [], Approved: false };
    const s = await shownWithCollections([autoCollection(DEF2, 'Held', {}, { Status: 'Guarded', PendingGuard: guard })]);
    s.q('.jfss-ac-guard-wait').click();
    assert.equal(s.q('.jfss-ac-guard-keep'), null);
    assert.equal(s.page.document.activeElement, s.q('.jfss-ac-guard-review'));
    s.q('.jfss-ac-guard-review').click();
    assert.equal(s.page.document.activeElement, s.q('.jfss-ac-guard-keep'));
    s.q('.jfss-ac-guard-apply').click();
    await s.page.advance(20);
    assert.ok(s.dialog());
    s.button(s.dialog(), 'Cancel').click();
    await s.page.advance(20);
    assert.equal(s.calls('POST', /Guard/).length, 0, 'Wait and Cancel send nothing');
    s.q('.jfss-ac-guard-keep').click();
    await s.page.advance(50);
    const keep = s.calls('POST', '/SmartSearch/AutoCollections/' + DEF2 + '/Guard/Keep');
    assert.equal(keep.length, 1);
    assert.deepEqual(keep[0].body, { DetectedUtc: guard.DetectedUtc });
    assert.deepEqual(s.page.errors, []);
});

test('New auto-collection: the name is checked as it is typed with free names to pick, Save waits for a preview of the form as it is, and sends its hash', { skip }, async () => {
    const s = await shownWithCollections([]);
    assert.equal(s.q('.jfss-ac-notices').textContent, 'Turn a saved search into a collection that keeps itself up to date.');
    s.q('.jfss-ac-new').click();
    await s.page.advance(50);
    const dialog = s.dialog();
    const select = dialog.querySelector('.jfss-ac-source-select');
    assert.deepEqual(select.querySelectorAll('optgroup').map((g) => g.getAttribute('label')), ['My queries', 'Shared by others (a copy will be made)']);
    const save = dialog.querySelector('.jfss-ac-save');
    assert.equal(save.disabled, true);
    s.change(select, 'q:' + QA);
    await s.page.advance(50);
    assert.equal(dialog.querySelector('.jfss-ac-name').value, 'Alien nights', 'the query\'s name');
    type(s, dialog.querySelector('.jfss-ac-name'), 'Horror 80s');
    await s.page.advance(1000);
    assert.equal(dialog.querySelector('.jfss-ac-name-taken').textContent, 'A collection or folder already uses this name.');
    dialog.querySelectorAll('.jfss-ac-suggestion')[0].click();
    assert.equal(dialog.querySelector('.jfss-ac-name').value, 'Horror 80s (2)');
    await s.page.advance(1000);
    assert.equal(dialog.querySelector('.jfss-ac-name-free').textContent, 'The name is free.');
    assert.equal(dialog.querySelector('.jfss-ac-preview-state').textContent, 'Will create a collection with 5 items.');
    assert.equal(dialog.querySelector('.jfss-ac-sample-row').children.length, 2);
    assert.equal(save.disabled, false);
    // A change after the preview: Save waits for the next one.
    dialog.querySelector('.jfss-ac-allow-empty').checked = true;
    s.page.dispatch(dialog.querySelector('.jfss-ac-allow-empty'), 'change');
    assert.equal(save.disabled, true);
    await s.page.advance(1000);
    assert.equal(save.disabled, false);
    const lastHash = 'hash-' + s.ac.n;
    save.click();
    await s.page.advance(50);
    const post = s.calls('POST', '/SmartSearch/AutoCollections');
    assert.equal(post.length, 1);
    assert.equal(post[0].body.AcceptedPreviewHash, lastHash);
    assert.equal(post[0].body.DuplicateSourceQuery, undefined, 'the user\'s own query');
    assert.deepEqual(post[0].body.Definition.Source, { Mode: 'Reference', SavedQueryId: QA });
    assert.equal(post[0].body.Definition.Name, 'Horror 80s (2)');
    assert.equal(post[0].body.Definition.Safety.AllowEmpty, true);
    assert.equal(post[0].body.Definition.EvaluationUserId, null, 'the query reads no watch state');
    assert.equal(s.dialog(), null);
    assert.ok(unmarked(s.toasts()).includes('Horror 80s (2) is saved; its collection is on its way.'), s.toasts().join(' | '));
    assert.equal(s.all('.jfss-ac-row').length, 1);
    assert.deepEqual(s.page.errors, []);
});

test('someone else\'s shared query is copied on save; a query reading watch state shows whose, which only an administrator changes', { skip }, async () => {
    const s = await shownWithCollections([]);
    s.ac.usesPerUser = true;
    s.q('.jfss-ac-new').click();
    await s.page.advance(50);
    const dialog = s.dialog();
    const evaluation = dialog.querySelector('.jfss-ac-evaluation-select');
    const evaluationField = evaluation.parentNode.parentNode;
    assert.equal(evaluationField.classList.contains('hide'), true, 'no query yet');
    s.change(dialog.querySelector('.jfss-ac-source-select'), 'q:' + QC);
    await s.page.advance(1000);
    assert.equal(dialog.querySelector('.jfss-ac-copy-note').textContent, 'A copy of this query will be saved as yours and used for the collection.');
    assert.equal(evaluationField.classList.contains('hide'), false);
    assert.equal(evaluation.disabled, true);
    assert.match(dialog.querySelector('.jfss-ac-evaluation-warning').textContent, /which items .*me.* has or hasn't watched/);
    dialog.querySelector('.jfss-ac-save').click();
    await s.page.advance(50);
    const post = s.calls('POST', '/SmartSearch/AutoCollections')[0];
    assert.equal(post.body.DuplicateSourceQuery, true);
    assert.equal(post.body.Definition.EvaluationUserId, 'user1');
    assert.deepEqual(s.page.errors, []);
});

test('Delete keeps the collection unless asked; History lists the runs and offers to keep what the last change removed', { skip }, async () => {
    const s = await shownWithCollections([autoCollection(DEF1, 'Horror 80s'), autoCollection(DEF2, 'Sci-fi')]);
    s.ac.history = [{ Utc: '2026-11-09T10:00:00.0000000Z', Reason: 'PostScan', Added: 3, Removed: 1, Dead: 0, Pinned: 0, Excluded: 0, Reverted: 0, Outcome: 'Written', DurationMs: 1200, RemovedIds: [itemId(7)], HiddenCount: 0 }];
    s.all('.jfss-ac-history')[0].click();
    await s.page.advance(50);
    assert.match(s.dialog().querySelector('.jfss-ac-history-line').textContent, /after a scan · \+3 −1 · changed · 1,200 ms$/);
    s.dialog().querySelector('.jfss-ac-history-keep').click();
    await s.page.advance(50);
    const put = s.calls('PUT', '/SmartSearch/AutoCollections/' + DEF1);
    assert.equal(put.length, 1);
    assert.deepEqual(put[0].body.Definition, { ManualEdits: { PinnedItemIds: [itemId(7)] } });
    assert.equal(s.dialog(), null);
    s.all('.jfss-ac-delete')[0].click();
    await s.page.advance(20);
    assert.equal(s.dialog().querySelector('.jfss-ac-delete-keep').checked, true);
    s.dialog().querySelector('.jfss-ac-delete-confirm').click();
    await s.page.advance(50);
    const del = s.calls('DELETE', '/SmartSearch/AutoCollections/' + DEF1);
    assert.equal(del.length, 1);
    assert.equal(del[0].query.deleteCollection, undefined);
    s.all('.jfss-ac-delete')[0].click();
    await s.page.advance(20);
    s.dialog().querySelector('.jfss-ac-delete-too').checked = true;
    s.dialog().querySelector('.jfss-ac-delete-confirm').click();
    await s.page.advance(50);
    assert.equal(s.calls('DELETE', '/SmartSearch/AutoCollections/' + DEF2)[0].query.deleteCollection, 'true');
    assert.equal(s.all('.jfss-ac-row').length, 0);
    assert.deepEqual(s.page.errors, []);
});

test('a link with edit opens that auto-collection\'s editor once the list is read, and the hash drops it', { skip }, async () => {
    const s = await shownWithCollections([autoCollection(DEF1, 'Horror 80s')], { hash: '#/jfss-search?tab=collections&edit=' + DEF1 });
    await s.page.advance(100);
    assert.ok(s.dialog());
    assert.equal(s.dialog().querySelector('.jfss-ac-name').value, 'Horror 80s');
    assert.equal(s.params().edit, undefined);
    assert.deepEqual(s.page.errors, []);
});

test('Create auto-collection on the Search tab and on a saved query\'s row open the editor with that query', { skip }, async () => {
    const s = await shownWithCollections([], { hash: '#/jfss-search?tab=saved' });
    const row = s.all('.jfss-saved-row').find((r) => r.querySelector('.jfss-saved-open').textContent === 'Alien nights');
    row.querySelector('.jfss-create-collection').click();
    await s.page.advance(1000);
    assert.equal(s.dialog().querySelector('.jfss-ac-source-select').value, 'q:' + QA);
    assert.equal(s.dialog().querySelector('.jfss-ac-name').value, 'Alien nights');
    assert.deepEqual(s.page.errors, []);
});

test('Create auto-collection on an unsaved search saves it first through the save dialog, then opens the editor on the new query', { skip }, async () => {
    const s = await shownWithCollections([], { hash: '#/jfss-search?title=alien&run=1' });
    await s.page.advance(400);
    const button = s.q('.jfss-create-collection');
    assert.equal(button.textContent, 'Create auto-collection…');
    button.click();
    await s.page.advance(50);
    submitDialog(s, { '.jfss-name-input': 'Alien films' });
    await s.page.advance(1000);
    const created = s.calls('POST', '/SmartSearch/Queries');
    assert.equal(created.length, 1);
    const dialog = s.dialog();
    assert.ok(dialog.querySelector('.jfss-ac-form'), 'the editor follows the save');
    assert.equal(dialog.querySelector('.jfss-ac-name').value, 'Alien films');
    assert.equal(dialog.querySelector('.jfss-ac-source-select').value.indexOf('q:'), 0);
    assert.deepEqual(s.page.errors, []);
});

test('a replaced collection offers Recreate and Stop managing (the definition deleted, the collection found left alone); a deleted one Recreate, with no link', { skip }, async () => {
    const s = await shownWithCollections([
        autoCollection(DEF1, 'Replaced', {}, { Status: 'TetherMissing' }),
        autoCollection(DEF2, 'Gone', {}, { Status: 'Orphaned' })
    ]);
    s.page.respond('POST', /^\/SmartSearch\/AutoCollections\/[0-9a-f]{32}\/Recreate$/, () => autoCollection(DEF2, 'Gone', {}, { Status: 'Queued' }), 202);
    const replaced = s.all('.jfss-ac-row')[0];
    assert.equal(replaced.querySelector('a.jfss-ac-open'), null, 'no link to a collection that isn\'t ours');
    assert.match(replaced.querySelector('.jfss-ac-panel-text').textContent, /no longer carries the Smart Search mark/);
    assert.deepEqual(replaced.querySelectorAll('.jfss-ac-panel button').map((b) => b.textContent), ['Recreate', 'Stop managing']);
    replaced.querySelector('.jfss-ac-stop').click();
    await s.page.advance(20);
    assert.equal(s.dialog().querySelector('.jfss-ac-delete-too'), null, 'Stop managing never deletes the collection found');
    s.dialog().querySelector('.jfss-ac-delete-confirm').click();
    await s.page.advance(50);
    assert.equal(s.calls('DELETE', '/SmartSearch/AutoCollections/' + DEF1)[0].query.deleteCollection, undefined);
    const gone = s.all('.jfss-ac-row')[0];
    assert.equal(gone.querySelector('.jfss-ac-status').textContent, 'Collection deleted');
    assert.equal(gone.querySelector('a.jfss-ac-open'), null);
    gone.querySelector('.jfss-ac-recreate').click();
    await s.page.advance(50);
    assert.deepEqual(s.calls('POST', '/SmartSearch/AutoCollections/' + DEF2 + '/Recreate')[0].body, {});
    assert.ok(unmarked(s.toasts()).includes('The collection of Gone is on its way.'), s.toasts().join(' | '));
    assert.deepEqual(s.page.errors, []);
});

// --------------------------------------------------------------------- the note on an auto-collection's details page

const PLAIN_ITEM = '8888888888888888888888888888cc01';

// jellyfin-web's details view of a collection as 10.11.5 builds it: the overview, then the item list, in the primary
// content; the secondary column below.
function detailsView(page) {
    const overview = page.element('div', { class: 'overview-container' });
    const items = page.element('div', { class: 'collectionItems' }, [page.element('div', { class: 'verticalSection' })]);
    const section = page.element('div', { class: 'detailSection' }, [overview, items]);
    const view = page.element('div', { id: 'itemDetailPage', 'data-role': 'page', class: 'page libraryPage itemDetailPage' }, [
        page.element('div', { class: 'detailPageWrapperContainer' }, [
            page.element('div', { class: 'detailPagePrimaryContainer' }, [page.element('div', { class: 'detailPagePrimaryContent' }, [section])]),
            page.element('div', { class: 'detailPageSecondaryContainer' }, [page.element('div', { class: 'detailPageContent' })])
        ])
    ]);
    return { view, section, items };
}

// A details page of opts.id (the auto-collection's by default), signed in, with Ids and ByCollection answered.
async function detailsPage(opts) {
    opts = opts || {};
    const s = setup({ hash: '#/details?id=' + (opts.id || BOXSET) + '&serverId=s1', head: opts.head });
    if (opts.entryPoints !== undefined) {
        const block = s.page.element('script', { type: 'application/json', 'data-jfss-entrypoints': '' });
        block.textContent = opts.entryPoints;
        s.page.document.head.append(block);
    }

    s.note = Object.assign({ Name: 'Alien <b>nights</b>', LastSuccessUtc: new Date(Date.now() - 2 * 3600 * 1000).toISOString(), CanManage: false }, opts.note || {});
    s.page.respond('POST', '/SmartSearch/Diagnostics/ClientHealth', '', 204);
    legacyHeader(s.page);
    s.page.respond('GET', '/SmartSearch/AutoCollections/Ids', () => opts.ids || [BOXSET.replace(/^(.{8})(.{4})(.{4})(.{4})/, '$1-$2-$3-$4-')]);
    s.page.respond('GET', /^\/SmartSearch\/AutoCollections\/ByCollection\//, () => s.note);
    s.page.run(source, file);
    await s.page.advance(5);
    s.page.installApiClient({ userId: 'user-1', items: {} });
    await s.page.advance(5);
    s.dom = opts.anchor === false ? { view: s.page.element('div', { id: 'itemDetailPage', 'data-role': 'page', class: 'page' }) } : detailsView(s.page);
    s.view = s.dom.view;
    s.page.document.body.append(s.view);
    showView(s);
    await s.page.advance(400);
    s.notes = () => s.page.document.querySelectorAll('[data-jfss-collection-note]');
    s.calls = (method, pattern) => s.page.fetchLog.filter((f) => f.method === method && (pattern instanceof RegExp ? pattern.test(f.path) : f.path === pattern));
    s.toasts = () => s.page.document.querySelectorAll('.jfss-toast').map((t) => t.firstChild.textContent);
    return s;
}

test('an auto-collection\'s page says it updates automatically and when it last did, right above its items; text only for a user who doesn\'t manage collections', { skip }, async () => {
    const s = await detailsPage();
    const notes = s.notes();
    assert.equal(notes.length, 1);
    assert.equal(notes[0].parentNode, s.dom.section);
    assert.equal(notes[0].nextSibling, s.dom.items, 'right before the item list');
    assert.equal(notes[0].querySelector('.jfss-ac-note-text').textContent, 'Updates automatically from a saved search · updated 2 hours ago');
    assert.equal(notes[0].querySelectorAll('button').length, 0, 'no Refresh now');
    assert.equal(notes[0].querySelectorAll('a').length, 0, 'no Edit');
    assert.equal(s.calls('GET', /ByCollection\/9999999999999999999999999999bb01$/).length, 1);
    assert.deepEqual(s.page.warnings.filter((w) => w.includes('web anchor')), []);
    assert.deepEqual(s.page.errors, []);
});

test('a manager also gets Edit (the tab\'s editor) and Refresh now, which reads the note every 4 s until the update is done', { skip }, async () => {
    const s = await detailsPage({ note: { CanManage: true, DefinitionId: DEF1, LastSuccessUtc: null } });
    s.page.respond('POST', '/SmartSearch/AutoCollections/' + DEF1 + '/Refresh', '', 202);
    const note = s.notes()[0];
    assert.equal(note.querySelector('.jfss-ac-note-text').textContent, 'Updates automatically from a saved search · not updated yet');
    const edit = note.querySelector('a.jfss-ac-note-edit');
    assert.equal(edit.textContent, 'Edit');
    assert.equal(edit.getAttribute('href'), '#/jfss-search?tab=collections&edit=' + DEF1);
    assert.equal(edit.getAttribute('title'), 'Edit Alien <b>nights</b>', 'the name is text');
    const refresh = note.querySelector('button.jfss-ac-note-refresh');
    assert.equal(refresh.textContent, 'Refresh now');
    refresh.click();
    await s.page.advance(10);
    assert.equal(s.calls('POST', '/SmartSearch/AutoCollections/' + DEF1 + '/Refresh').length, 1);
    assert.equal(refresh.disabled, true);
    assert.equal(note.querySelector('.jfss-ac-note-text').textContent, 'Updates automatically from a saved search · updating…');
    const reads = () => s.calls('GET', /ByCollection\//).length;
    const before = reads();
    await s.page.advance(4000);
    assert.equal(reads(), before + 1, 'read again after 4 s');
    await s.page.advance(4000);
    assert.equal(reads(), before + 2);
    s.note = Object.assign({}, s.note, { LastSuccessUtc: new Date(Date.now() + 9000).toISOString() });
    await s.page.advance(4000);
    assert.equal(refresh.disabled, false);
    assert.equal(note.querySelector('.jfss-ac-note-text').textContent, 'Updates automatically from a saved search · updated just now');
    assert.ok(unmarked(s.toasts()).includes('Alien <b>nights</b> is up to date.'), s.toasts().join(' | '));
    await s.page.advance(12000);
    assert.equal(reads(), before + 3, 'no more reads once done');
    assert.deepEqual(s.page.errors, []);
});

test('a manager on a server whose page is off gets Refresh now without Edit', { skip }, async () => {
    const s = await detailsPage({ head: { off: ['search'] }, note: { CanManage: true, DefinitionId: DEF1 } });
    const note = s.notes()[0];
    assert.equal(note.querySelector('a.jfss-ac-note-edit'), null);
    assert.ok(note.querySelector('button.jfss-ac-note-refresh'));
    assert.deepEqual(s.page.errors, []);
});

test('any other item\'s page has no note and asks nothing but the list, read at most every 10 minutes', { skip }, async () => {
    const s = await detailsPage({ id: PLAIN_ITEM });
    assert.equal(s.notes().length, 0);
    assert.equal(s.calls('GET', /ByCollection\//).length, 0);
    assert.equal(s.calls('GET', '/SmartSearch/AutoCollections/Ids').length, 1);
    navigate(s, '#/details?id=' + BOXSET + '&serverId=s1');
    showView(s);
    await s.page.advance(400);
    assert.equal(s.notes().length, 1, 'the auto-collection\'s page, from the same list');
    assert.equal(s.calls('GET', '/SmartSearch/AutoCollections/Ids').length, 1);
    navigate(s, '#/details?id=' + PLAIN_ITEM + '&serverId=s1');
    showView(s);
    await s.page.advance(400);
    assert.equal(s.notes().length, 0, 'gone on the next item\'s page (jellyfin-web keeps the view)');
    await s.page.advance(10 * 60 * 1000);
    navigate(s, '#/details?id=' + BOXSET + '&serverId=s1');
    showView(s);
    await s.page.advance(400);
    assert.equal(s.calls('GET', '/SmartSearch/AutoCollections/Ids').length, 2, 'read again after 10 minutes');
    assert.deepEqual(s.page.errors, []);
});

test('no note while ShowCollectionNote is off, or once the collection isn\'t an auto-collection any more', { skip }, async () => {
    const off = await detailsPage({ entryPoints: '{"headerButton":true,"collectionNote":false}' });
    assert.equal(off.notes().length, 0);
    assert.equal(off.calls('GET', '/SmartSearch/AutoCollections/Ids').length, 0, 'nothing asked');
    const stale = await detailsPage({ note: null });
    stale.page.respond('GET', /^\/SmartSearch\/AutoCollections\/ByCollection\//, { code: 'AutoCollectionNotFound' }, 404);
    navigate(stale, '#/details?id=' + PLAIN_ITEM + '&serverId=s1');
    showView(stale);
    await stale.page.advance(400);
    navigate(stale, '#/details?id=' + BOXSET + '&serverId=s1');
    showView(stale);
    await stale.page.advance(400);
    assert.equal(stale.notes().length, 0);
    navigate(stale, '#/details?id=' + PLAIN_ITEM + '&serverId=s1');
    showView(stale);
    await stale.page.advance(400);
    assert.equal(stale.calls('GET', '/SmartSearch/AutoCollections/Ids').length, 2, 'the list is read again after a 404');
    for (const s of [off, stale]) {
        assert.deepEqual(s.page.errors, []);
    }
});

test('without the collection\'s item list: no note, one console warning and one health report, never a throw', { skip }, async () => {
    const s = await detailsPage({ anchor: false });
    await s.page.advance(5000);
    assert.equal(s.notes().length, 0);
    assert.deepEqual(s.page.warnings.filter((w) => w.includes('web anchor')), ['[jfss] web anchor "collectionNoteBefore" missing on details (web 10.11.5)']);
    await s.page.advance(3000);
    const reports = s.page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/SmartSearch/Diagnostics/ClientHealth');
    assert.equal(reports.length, 1);
    assert.equal(reports[0].body.Missing[0].Anchor, 'collectionNoteBefore');
    assert.equal(reports[0].body.Missing[0].View, 'details');
    assert.deepEqual(s.page.errors, []);
});
