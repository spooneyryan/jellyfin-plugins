// Access Roles' web guard in the kit's fake page, with the built
// ClientScript (guard-page.mjs says how WebTests runs it). The page stands in for jellyfin-web 10.11.5: its router events
// (window.Events HISTORY_UPDATE), viewshow and pageshow, and the DOM the anchors name.
import test from 'node:test';
import assert from 'node:assert/strict';
import { plain } from '../../../../../kit/web/tests/fake-dom.mjs';
import { skip, start, html, guard, toasts, view, me, USER, OTHER, EVERY_SECTION } from './guard-page.mjs';

const PAGE_MANAGED = 'This page is managed by your server administrator.';

test('the path normaliser: #, !, the query, percent-encoding, letter case, .html and a trailing slash', { skip }, async () => {
    const g = guard(await start({ me: me([]) }));
    assert.equal(g.normalizePath('#/MyPreferences%48ome.html?userId=1'), '/mypreferenceshome');
    assert.equal(g.normalizePath('#!/userprofile/'), '/userprofile');
    assert.equal(g.normalizePath({ pathname: '/QuickConnect' }), '/quickconnect');
    assert.equal(g.normalizePath('mypreferencesmenu'), '/mypreferencesmenu');
    assert.equal(g.normalizePath('#/%E0%A4%A'), '/%e0%a4%a');
});

test('the path normaliser ends a route at a second #, as the router reads it', { skip }, async () => {
    const page = await start({ me: me(['Home', 'Profile', 'Controls']) });
    const g = guard(page);
    for (const [raw, path] of [
        ['#/mypreferenceshome#x', '/mypreferenceshome'],
        ['#/mypreferenceshome/#x', '/mypreferenceshome'],
        ['#/userprofile#x?userId=1', '/userprofile'],
        ['#/mypreferencescontrols?a=1#frag', '/mypreferencescontrols'],
        ['#!/MyPreferencesHome.html#', '/mypreferenceshome'],
        ['/#x', '/']
    ]) {
        assert.equal(g.normalizePath(raw), path, raw);
    }

    assert.equal(g.blockedSection('#/mypreferenceshome#x'), 'home');
    assert.equal(g.blockedSection('#/mypreferenceshome/#x'), 'home');
    // An encoded '#' is part of the segment: the router shows no settings page for it (its fallback page).
    assert.equal(g.normalizePath('#/mypreferenceshome%23x'), '/mypreferenceshome#x');
    assert.equal(g.blockedSection('#/mypreferenceshome%23x'), null);
});

test('a locked page behind a second # is sent Home on its viewshow, the shared page too', { skip }, async () => {
    for (const hash of ['#/mypreferenceshome#x', '#/mypreferenceshome/#x']) {
        const page = await start({ me: me(['Home']) });
        page.context.location.hash = hash;
        page.historyUpdate({ location: { pathname: '/mypreferenceshome', hash: '#x' }, navigation: { state: 'idle' }, historyAction: 'PUSH' });
        page.dispatch(view(page, 'homeScreenPreferencesPage', ['userPreferencesPage']), 'viewshow');
        assert.deepEqual(page.replaced, ['#/home'], hash);
        assert.deepEqual(plain(toasts(page)), [PAGE_MANAGED], hash);
    }
});

test('the shared page decides by the router\'s last location, not by re-reading the hash', { skip }, async () => {
    // The router showed /mypreferenceshome for a hash the guard reads as something else: Home's page id is shared with
    // Subtitles, so the page-id backstop asks the route, and the route is the router's.
    const page = await start({ me: me(['Home']) });
    page.context.location.hash = '#/home-screen-of-some-other-spelling';
    page.historyUpdate({ location: { pathname: '/mypreferenceshome' }, navigation: { state: 'idle' }, historyAction: 'PUSH' });
    page.dispatch(view(page, 'homeScreenPreferencesPage', ['userPreferencesPage']), 'viewshow');
    assert.deepEqual(page.replaced, ['#/home?jfar=1']);

    // Subtitles (open) under the same page id stays, by the router's location too.
    const open = await start({ me: me(['Home']) });
    open.context.location.hash = '#/mypreferencessubtitles';
    open.historyUpdate({ location: { pathname: '/mypreferencessubtitles' }, navigation: { state: 'idle' }, historyAction: 'PUSH' });
    open.dispatch(view(open, 'homeScreenPreferencesPage', ['userPreferencesPage']), 'viewshow');
    assert.deepEqual(open.replaced, []);
});

test('two locked navigations within a redirect\'s window: the second is replaced too, and the window\'s end checks again', { skip }, async () => {
    const page = await start({ me: me(['Home', 'Controls']) });
    page.context.location.hash = '#/mypreferenceshome';
    page.historyUpdate({ location: { pathname: '/mypreferenceshome' }, navigation: { state: 'idle' }, historyAction: 'PUSH' });
    page.dispatch(view(page, 'homeScreenPreferencesPage', ['userPreferencesPage']), 'viewshow');
    assert.deepEqual(page.replaced, ['#/home']);

    // 400 ms later, the second: its own viewshow, the first redirect's window still open.
    await page.advance(400);
    page.context.location.hash = '#/mypreferencescontrols';
    page.historyUpdate({ location: { pathname: '/mypreferencescontrols' }, navigation: { state: 'idle' }, historyAction: 'PUSH' });
    page.dispatch(view(page, 'controlsPreferencesPage', ['userPreferencesPage']), 'viewshow');
    assert.deepEqual(page.replaced, ['#/home', '#/home'], 'never a blank page left under a blocked hash');
    assert.ok(guard(page).log().some((e) => e.type === 'redirect-again'));
    assert.deepEqual(plain(toasts(page)), [PAGE_MANAGED], 'one toast for what happened within 1.5 s');

    // A blocked hash left with no page event at all is replaced when the window ends.
    page.context.location.hash = '#/mypreferenceshome';
    await page.advance(1200);
    assert.deepEqual(page.replaced, ['#/home', '#/home', '#/home']);
});

test('/Me\'s locks become classes, the answer is cached (Managed false too), and jfar-pending goes', { skip }, async () => {
    const page = await start({ me: me(['Profile', 'Home']) });
    assert.ok(html(page).contains('jfar-lock-profile') && html(page).contains('jfar-lock-home'));
    assert.equal(html(page).contains('jfar-pending'), false);
    assert.deepEqual(JSON.parse(page.context.localStorage.getItem('jfar:rules:' + USER)).Locked, ['Profile', 'Home']);
    assert.equal(guard(page).rules().source, 'me');

    const open = await start({ me: { UserId: USER, Managed: false, Locked: [], AllSettingsLocked: false, NoLibraries: false } });
    assert.equal(JSON.parse(open.context.localStorage.getItem('jfar:rules:' + USER)).Managed, false);
    assert.equal([...html(open).values ? html(open).values() : []].filter((c) => String(c).startsWith('jfar-')).length, 0);
    assert.equal(open.errors.length, 0, open.errors.join('\n'));
});

test('the first load: jfar-pending until /Me answers; 3 s at most when it never does, then everything shows', { skip }, async () => {
    const page = await start({ me: me(['Profile']), meExtra: { hang: true } });
    assert.ok(html(page).contains('jfar-pending'));
    await page.advance(2800);
    assert.ok(html(page).contains('jfar-pending'));
    await page.advance(400);
    assert.equal(html(page).contains('jfar-pending'), false);
    assert.equal(html(page).contains('jfar-lock-profile'), false);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

// /Me answering after delayMs (kit.api's own timeout still aborts it), as a slow server would.
function slowMe(delayMs) {
    return (p) => {
        const base = p.context.fetch;
        p.context.fetch = (url, init) => String(url).indexOf('/AccessRoles/Me') < 0 ? base(url, init) : new Promise((resolve, reject) => {
            if (init && init.signal) {
                init.signal.addEventListener('abort', () => reject(new Error('aborted')));
            }

            p.context.setTimeout(() => base(url, init).then(resolve, reject), delayMs);
        });
    };
}

test('a /Me slower than 3 s: jfar-pending goes at 3 s, the late answer is applied and cached, and guards the page', { skip }, async () => {
    const page = await start({ me: me(['Home']), beforeScripts: slowMe(4500) });
    assert.ok(html(page).contains('jfar-pending'));
    await page.advance(3100);
    assert.equal(html(page).contains('jfar-pending'), false, 'pending lifted at 3 s');
    assert.equal(guard(page).rules().source, 'none');
    await page.advance(1500);
    assert.equal(guard(page).rules().source, 'me', 'the late answer counts');
    assert.ok(html(page).contains('jfar-lock-home'));
    assert.deepEqual(JSON.parse(page.context.localStorage.getItem('jfar:rules:' + USER)).Locked, ['Home']);

    page.context.location.hash = '#/mypreferenceshome';
    page.dispatch(view(page, 'homeScreenPreferencesPage', ['userPreferencesPage']), 'viewshow');
    assert.deepEqual(page.replaced, ['#/home'], 'a typed locked URL later in the page\'s life');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a /Me that failed is asked again on a later view, 10 s after the last try at the soonest', { skip }, async () => {
    const page = await start({ me: null, meExtra: { networkError: true } });
    assert.equal(guard(page).rules().source, 'failed');
    const asked = () => page.fetchLog.filter((f) => f.path === '/AccessRoles/Me').length;
    assert.equal(asked(), 1);

    page.responses.length = 0;
    page.respond('GET', '/AccessRoles/Me', me(['Profile']));
    await page.advance(5000);
    page.dispatch(view(page, 'indexPage', ['homePage']), 'viewshow');
    await page.advance(50);
    assert.equal(asked(), 1, 'not within 10 s');

    await page.advance(5500);
    page.dispatch(view(page, 'indexPage', ['homePage']), 'viewshow');
    await page.advance(50);
    assert.equal(asked(), 2);
    assert.equal(guard(page).rules().source, 'me');
    assert.ok(html(page).contains('jfar-lock-profile'));

    // Once /Me has answered, views ask nothing more.
    await page.advance(20000);
    page.dispatch(view(page, 'indexPage', ['homePage']), 'viewshow');
    await page.advance(50);
    assert.equal(asked(), 2);
});

test('/Me failing keeps the cached rules and shows the rest; nobody managed means no pending at all', { skip }, async () => {
    const page = await start({ cache: { [USER]: { Managed: true, Locked: ['Home'] } }, me: null, meExtra: { networkError: true } });
    assert.ok(html(page).contains('jfar-lock-home'));
    assert.equal(html(page).contains('jfar-pending'), false);
    assert.equal(guard(page).rules().source, 'cache');

    const quiet = await start({ head: { anyManaged: false }, me: { UserId: USER, Managed: false, Locked: [] }, meExtra: { hang: true } });
    assert.equal(html(quiet).contains('jfar-pending'), false);
});

test('a sign-in without a reload sets jfar-pending itself, and a user switch swaps the classes', { skip }, async () => {
    const page = await start({ credentials: false, user: null, me: me(['Playback']), meExtra: { hang: true } });
    assert.equal(html(page).contains('jfar-pending'), false);
    page.installApiClient({ userId: USER });
    await page.advance(60);
    assert.ok(html(page).contains('jfar-pending'), 'pending while /Me is asked');
    page.responses.length = 0;
    page.respond('GET', '/AccessRoles/Me', me(['Playback']));
    page.clientState.userId = null;
    page.dispatch(page.document.body, 'viewshow');
    await page.advance(30);
    page.clientState.userId = USER;
    page.dispatch(page.document.body, 'viewshow');
    await page.advance(60);
    assert.ok(html(page).contains('jfar-lock-playback'));

    page.responses.length = 0;
    page.respond('GET', '/AccessRoles/Me', me(['Subtitles'], { UserId: OTHER }));
    page.clientState.userId = OTHER;
    page.dispatch(page.document.body, 'viewshow');
    await page.advance(60);
    assert.ok(html(page).contains('jfar-lock-subtitles'));
    assert.equal(html(page).contains('jfar-lock-playback'), false);

    page.clientState.userId = null;
    page.dispatch(page.document.body, 'viewshow');
    await page.advance(30);
    assert.equal(html(page).contains('jfar-lock-subtitles'), false);
});

test('the startup check: a cold load of a locked page is replaced by Home, with the toast', { skip }, async () => {
    const page = await start({ hash: '#/mypreferenceshome?userId=' + USER, cache: { [USER]: { Managed: true, Locked: ['Home'] } }, me: me(['Home']) });
    assert.deepEqual(page.replaced, ['#/home']);
    assert.ok(html(page).contains('jfar-blocking'));
    assert.deepEqual(plain(toasts(page)), [PAGE_MANAGED]);
});

test('a lock /Me adds for the page already open redirects it then', { skip }, async () => {
    const page = await start({ hash: '#/userprofile', me: me(['Profile']) });
    assert.deepEqual(page.replaced, ['#/home']);
});

test('HISTORY_UPDATE: a pending lazy navigation to a locked page is abandoned at once; an idle update only hides', { skip }, async () => {
    const page = await start({ me: me(['QuickConnect', 'Home']) });
    page.historyUpdate({ location: { pathname: '/home' }, navigation: { state: 'loading', location: { pathname: '/quickconnect' } }, historyAction: 'PUSH' });
    assert.deepEqual(page.replaced, ['#/home']);
    assert.ok(html(page).contains('jfar-blocking'));

    await page.advance(2000);
    page.historyUpdate({ location: { pathname: '/mypreferenceshome' }, navigation: { state: 'idle' }, historyAction: 'PUSH' });
    assert.deepEqual(page.replaced, ['#/home'], 'never on idle alone');
    page.context.location.hash = '#/mypreferenceshome';
    const homeScreen = view(page, 'homeScreenPreferencesPage', ['userPreferencesPage']);
    page.dispatch(homeScreen, 'viewshow');
    assert.deepEqual(page.replaced, ['#/home', '#/home'], 'the blocked page\'s own viewshow');

    // The blocked page's pageshow after the redirect is ignored; a page that isn't blocked lifts jfar-blocking.
    page.dispatch(homeScreen, 'pageshow');
    assert.ok(html(page).contains('jfar-blocking'));
    page.dispatch(view(page, 'indexPage', ['homePage']), 'viewshow');
    assert.equal(html(page).contains('jfar-blocking'), false);
});

test('a late legacy view of a locked page under #/home is sent to Home again; Subtitles alone doesn\'t block the shared page', { skip }, async () => {
    const page = await start({ me: me(['Home']) });
    const shared = view(page, 'homeScreenPreferencesPage', ['userPreferencesPage']);
    page.context.location.hash = '#/mypreferencessubtitles';
    page.dispatch(shared, 'viewshow');
    assert.deepEqual(page.replaced, [], 'Subtitles isn\'t locked');

    page.context.location.hash = '#/home';
    const profileUnlocked = await start({ me: me(['Profile']) });
    const late = view(profileUnlocked, 'userProfilePage', ['userPreferencesPage']);
    profileUnlocked.dispatch(late, 'viewshow');
    assert.deepEqual(profileUnlocked.replaced, ['#/home?jfar=1']);
    assert.deepEqual(plain(toasts(profileUnlocked)), [PAGE_MANAGED], 'the late view\'s redirect says so too');

    // Plugin Pages' host carries the class too (My Bookmarks, My Clips): never blocked.
    const host = view(profileUnlocked, 'userPluginPreferencesPage', ['userPreferencesPage']);
    profileUnlocked.context.location.hash = '#/userpluginsettings.html?pageUrl=x';
    profileUnlocked.dispatch(host, 'viewshow');
    assert.deepEqual(profileUnlocked.replaced, ['#/home?jfar=1']);
});

test('a click on a locked link stops in the capture phase, with the toast; other links go on', { skip }, async () => {
    const page = await start({ me: me(['Profile']) });
    const locked = page.element('a', { href: '#/userprofile?userId=' + USER }, [page.element('span')]);
    const open = page.element('a', { href: '#/mypreferencesplayback' });
    page.document.body.append(locked, open);
    let reached = 0;
    page.document.body.addEventListener('click', () => reached++);

    locked.firstChild.click();
    assert.equal(reached, 0);
    assert.deepEqual(plain(toasts(page)), [PAGE_MANAGED]);
    open.click();
    assert.equal(reached, 1);
});

test('BlockedNavigationNotice Silent redirects without a toast; enforcement off guards nothing', { skip }, async () => {
    const silent = await start({ head: { notice: 'Silent' }, hash: '#/quickconnect', me: me(['QuickConnect']) });
    assert.deepEqual(silent.replaced, ['#/home']);
    assert.deepEqual(plain(toasts(silent)), []);

    const off = await start({ head: { enforced: false }, hash: '#/quickconnect', cache: { [USER]: { Managed: true, Locked: ['QuickConnect'] } }, me: me(['QuickConnect']) });
    assert.deepEqual(off.replaced, []);
    assert.equal(html(off).contains('jfar-lock-quickconnect'), false);
    assert.equal(off.fetchLog.filter((f) => f.path === '/AccessRoles/Me').length, 0);
});

function settingsPage(page) {
    const signOut = page.element('div', { class: 'btnLogout listItem-border' });
    const section = page.element('div', { class: 'userSection verticalSection' }, [page.element('div', { class: 'selectServer listItem-border' }), signOut]);
    const links = page.element('div', { class: 'verticalSection' }, [page.element('a', { class: 'lnkUserProfile listItem-border', href: '#/userprofile?userId=' + USER })]);
    return { root: view(page, 'myPreferencesMenuPage', ['libraryPage', 'userPreferencesPage'], [links, section]), section, signOut };
}

test('every section locked: the Settings page notes it above Sign out, as text; the note goes when that changes', { skip }, async () => {
    const page = await start({ me: me(EVERY_SECTION, { AllSettingsLocked: true, Message: 'Settings are managed by your administrator' }) });
    const settings = settingsPage(page);
    page.context.location.hash = '#/mypreferencesmenu';
    page.dispatch(settings.root, 'viewshow');
    await page.advance(400);
    const note = settings.section.querySelector('[data-jfar-note]');
    assert.ok(note, 'the note');
    assert.equal(note.nextSibling, settings.signOut);
    assert.equal(note.textContent, 'Settings are managed by your administrator');
    assert.equal(note.children.length, 0);
    assert.ok(html(page).contains('jfar-lock-allsettings'));
    assert.deepEqual(page.replaced, [], 'the Settings menu itself stays reachable');

    page.respond('GET', '/AccessRoles/Me', me(['Home']));
    page.clientState.userId = null;
    page.dispatch(page.document.body, 'viewshow');
    await page.advance(30);
    page.clientState.userId = USER;
    page.dispatch(page.document.body, 'viewshow');
    await page.advance(400);
    assert.equal(settings.root.querySelector('[data-jfar-note]'), null);
});

function userMenu(page) {
    const avatar = page.element('button', { 'aria-controls': 'app-user-menu' });
    const settings = page.element('a', { role: 'menuitem', href: '#/mypreferencesmenu', tabindex: '-1' });
    const quick = page.element('a', { role: 'menuitem', href: '#/quickconnect', tabindex: '-1' });
    const signOut = page.element('li', { role: 'menuitem', tabindex: '-1' });
    const paper = page.element('div', { tabindex: '-1' }, [page.element('ul', {}, [settings, quick, signOut])]);
    const menu = page.element('div', { id: 'app-user-menu' }, [paper]);
    page.document.body.append(avatar, menu);
    return { avatar, menu, paper, settings, quick, signOut };
}

test('the MUI user menu: hidden items aria-disabled, and the focus moves off a hidden first item when it opens', { skip }, async () => {
    const page = await start({ me: me(EVERY_SECTION, { AllSettingsLocked: true }) });
    const menu = userMenu(page);
    await page.advance(400);
    assert.equal(menu.settings.getAttribute('aria-disabled'), 'true');
    assert.equal(menu.quick.getAttribute('aria-disabled'), 'true');
    assert.equal(menu.signOut.getAttribute('aria-disabled'), null);

    // MUI left the focus on the paper (its first item can't take it): 60 ms after the avatar's key, Sign out has it.
    menu.paper.focus();
    page.key(menu.avatar, 'Enter');
    await page.advance(100);
    assert.equal(page.document.activeElement, menu.signOut);

    // A closed menu (the focus outside it) is left alone.
    page.document.body.focus();
    page.dispatch(menu.avatar, 'pointerdown');
    await page.advance(500);
    assert.equal(page.document.activeElement, page.document.body);
    assert.deepEqual(plain(guard(page).hiddenEntryPoints()), ['userMenuSettings', 'userMenuQuickConnect']);
});

test('Display (account) locked: its fields are disabled on the Display page, re-applied while the page renders late', { skip }, async () => {
    const page = await start({ me: me(['DisplayAccount']) });
    const display = view(page, 'displayPreferencesPage', ['userPreferencesPage']);
    page.context.location.hash = '#/mypreferencesdisplay';
    page.dispatch(display, 'viewshow');
    assert.deepEqual(page.replaced, [], 'the page stays (Display (this device) isn\'t locked)');
    await page.advance(500);
    const missing = page.element('input', { type: 'checkbox', name: 'displayMissingEpisodes' });
    const images = page.element('input', { type: 'checkbox', id: 'chkUseEpisodeImagesInNextUp' });
    const layout = page.element('select', { name: 'layout' });
    display.append(missing, images, layout);
    await page.advance(300);
    assert.equal(missing.disabled, true);
    assert.equal(images.disabled, true);
    assert.equal(layout.disabled, false);

    const open = await start({ me: me(['Home']) });
    const other = view(open, 'displayPreferencesPage', ['userPreferencesPage'], [open.element('input', { name: 'displayMissingEpisodes' })]);
    open.dispatch(other, 'viewshow');
    await open.advance(300);
    assert.equal(other.firstChild.disabled, false);
});

test('a managed user with no library: Home\'s message is /Me\'s, set as text, every time Home is built', { skip }, async () => {
    const message = 'No libraries have been shared with you yet. Ask your server administrator.';
    const page = await start({ me: me(['Profile'], { NoLibraries: true, Message: message }) });
    const build = () => {
        const p = page.element('p', {}, [page.document.createTextNode('Ask an administrator to create a library.')]);
        const home = view(page, 'indexPage', ['homePage'], [page.element('div', { class: 'sections' }, [page.element('div', { class: 'centerMessage' }, [page.element('h2'), p])])]);
        page.dispatch(home, 'viewshow');
        return p;
    };
    const first = build();
    await page.advance(300);
    assert.equal(first.textContent, message);
    first.parentNode.parentNode.parentNode.remove();
    const second = build();
    await page.advance(300);
    assert.equal(second.textContent, message);

    const unmanaged = await start({ me: { UserId: USER, Managed: false, Locked: [], NoLibraries: false } });
    const p = unmanaged.element('p', {}, [unmanaged.document.createTextNode('stock')]);
    unmanaged.dispatch(view(unmanaged, 'indexPage', ['homePage'], [unmanaged.element('div', { class: 'sections' }, [unmanaged.element('div', { class: 'centerMessage' }, [p])])]), 'viewshow');
    await unmanaged.advance(300);
    assert.equal(p.textContent, 'stock');
});

test('a save whose locked values the server kept says so, naming the sections; other responses say nothing', { skip }, async () => {
    const page = await start({
        me: me(['Home', 'Playback']),
        beforeScripts: (p) => {
            const base = p.context.fetch;
            p.context.fetch = (url, init) => String(url).indexOf('/Configuration') >= 0
                ? Promise.resolve({ ok: true, status: 204, headers: { get: (name) => (name === 'X-AccessRoles-Kept' ? 'Home,Playback' : null) }, text: () => Promise.resolve('') })
                : base(url, init);
        }
    });
    await page.context.fetch('http://127.0.0.1:8097/Users/' + USER + '/Configuration', { method: 'POST' });
    await page.advance(10);
    assert.deepEqual(plain(toasts(page)), ['Some of these settings are managed by your server administrator, so they weren\'t changed: Home screen, Playback.']);
    await page.context.fetch('http://127.0.0.1:8097/Items', { method: 'GET' });
    await page.advance(10);
    assert.equal(toasts(page).length, 1);
});

test('a save that failed names nothing as kept, whatever its headers say', { skip }, async () => {
    const page = await start({
        me: me(['Home']),
        beforeScripts: (p) => {
            const base = p.context.fetch;
            p.context.fetch = (url, init) => String(url).indexOf('/Configuration') >= 0
                ? Promise.resolve({ ok: false, status: 500, headers: { get: (name) => (name === 'X-AccessRoles-Kept' ? 'Home' : null) }, text: () => Promise.resolve('') })
                : base(url, init);
        }
    });
    await page.context.fetch('http://127.0.0.1:8097/Users/' + USER + '/Configuration', { method: 'POST' });
    await page.advance(10);
    assert.deepEqual(plain(toasts(page)), []);
});

function userPage(page, id) {
    const content = page.element('div', { class: 'content-primary' }, [page.element('div', { class: 'verticalSection' })]);
    view(page, id, ['type-interior'], [content]);
    return content;
}

test('administrators: a banner on a managed user\'s stock pages and on Add user, as text; none for others', { skip }, async () => {
    const effective = { UserId: OTHER, State: 'Managed', Roles: [{ Id: 'r1', Name: 'Kid <b>' }], Locks: { Profile: { Locked: true }, Home: { Locked: false }, AdminSurfaces: { Locked: true } } };
    const page = await start({
        admin: true,
        me: { UserId: USER, Managed: false, Locked: [] },
        hash: '#/dashboard/users/access?userId=' + OTHER,
        responses: [
            { method: 'GET', path: '/AccessRoles/Admin/Users/' + OTHER + '/Effective', body: effective },
            { method: 'GET', path: '/AccessRoles/Admin/Roles', body: [{ Name: 'Kid', IsDefault: true }, { Name: 'Other', IsDefault: false }] }
        ]
    });
    const content = userPage(page, 'userLibraryAccessPage');
    page.dispatch(content.parentNode, 'viewshow');
    await page.advance(400);
    const banner = content.firstChild;
    assert.equal(banner.getAttribute('data-jfar-banner'), 'user:' + OTHER);
    assert.equal(banner.children.length, 3);
    assert.equal(banner.children[0].textContent, 'Access Roles manages this user (roles: Kid <b>).');
    assert.equal(banner.children[0].children.length, 0, 'text only');
    assert.match(banner.children[2].textContent, /Profile, Dashboard data and server calls/);

    content.parentNode.remove();
    page.context.location.hash = '#/dashboard/users/add';
    const add = userPage(page, 'newUserPage');
    page.context.dispatchEvent(new page.Event('hashchange'));
    page.dispatch(add.parentNode, 'viewshow');
    await page.advance(400);
    assert.match(add.firstChild.textContent, /default roles: Kid\./);
    assert.equal(add.querySelectorAll('[data-jfar-banner]').length, 1);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('administrators: an unmanaged user gets no banner, a failed request none either; with enforcement off the banner says so', { skip }, async () => {
    const page = await start({
        admin: true,
        me: { UserId: USER, Managed: false, Locked: [] },
        hash: '#/dashboard/users/profile?userId=' + OTHER,
        responses: [{ method: 'GET', path: '/AccessRoles/Admin/Users/' + OTHER + '/Effective', body: { State: 'NotManaged', Roles: [], Locks: {} } }]
    });
    const content = userPage(page, 'editUserPage');
    page.dispatch(content.parentNode, 'viewshow');
    await page.advance(400);
    assert.equal(content.querySelectorAll('[data-jfar-banner]').length, 0);

    const off = await start({
        admin: true,
        head: { enforced: false },
        hash: '#/dashboard/users/profile?userId=' + OTHER,
        responses: [{ method: 'GET', path: '/AccessRoles/Admin/Users/' + OTHER + '/Effective', body: { State: 'Managed', Roles: [{ Name: 'Kid' }], Locks: {} } }]
    });
    const offContent = userPage(off, 'editUserPage');
    off.dispatch(offContent.parentNode, 'viewshow');
    await off.advance(400);
    assert.match(offContent.firstChild.textContent, /enforcement is switched off/);

    const denied = await start({ hash: '#/dashboard/users/profile?userId=' + OTHER, me: me([]), responses: [{ method: 'GET', path: '/AccessRoles/Admin/Users/' + OTHER + '/Effective', status: 403, body: { title: 'Forbidden' } }] });
    const deniedContent = userPage(denied, 'editUserPage');
    denied.dispatch(deniedContent.parentNode, 'viewshow');
    await denied.advance(400);
    assert.equal(deniedContent.querySelectorAll('[data-jfar-banner]').length, 0);
    assert.equal(denied.errors.length, 0, denied.errors.join('\n'));
});
