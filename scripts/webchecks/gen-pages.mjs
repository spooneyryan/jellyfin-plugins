// Generates the page-hosting webchecks from an earlier prototype's
// checks, with the Bookmarks prefix and its developer page demo (EnablePageDemo, #/jfbm-demo-a and
// #/jfbm-demo-b). Run from the repository root: node scripts/webchecks/gen-pages.mjs [--out <folder>]
// The step files share their page expressions; scripts/README.md says which sandbox and mode each one needs.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// --out <folder> writes the files elsewhere (Repo.Tests compares them with the committed ones).
const outIndex = process.argv.indexOf('--out');
const folder = outIndex > 0 ? path.resolve(process.argv[outIndex + 1]) : path.dirname(fileURLToPath(import.meta.url));
const PLUGIN_ID = 'd0ee1b2a-7f71-4654-8ff2-163a81441b03';
const TITLES = { 'demo-a': 'Demo page A', 'demo-b': 'Demo page B' };
const IDS = { 'demo-a': 'Jellyfin.Plugin.Bookmarks.DemoAPage', 'demo-b': 'Jellyfin.Plugin.Bookmarks.DemoBPage' };
const IGNORE = '/CustomRatings/script|^Error: player cannot be null';

// ------------------------------------------------------------------------------------------------ expressions

const notFoundVisible = "[...document.querySelectorAll('#fallbackPage > :not([data-jfbm-page]) h1')].some(h => h.getClientRects().length > 0)";
const headerTitle = "(document.querySelector('.skinHeader .pageTitle') || { textContent: '' }).textContent.trim()";

// Our demo page `key` is mounted, on, rendered and visible in `host` ('#fallbackPage >' or '.userPreferencesPage:not(.hide)'),
// "Page not found" is hidden, the header shows the page's title and the page's own authenticated calls answered.
const pageOn = (scope, key, extra = '') => `(() => { const r = [...document.querySelectorAll(${JSON.stringify(scope + ' [data-jfbm-page="' + key + '"][data-jfbm-mounted]')})].find(x => x.getClientRects().length > 0); `
    + `const t = ${headerTitle}; const title = r && r.querySelector('.jfbm-demo-title'); const user = r && r.querySelector('[data-jfbm-demo="user"]'); `
    + `if (!r || !title || title.textContent !== ${JSON.stringify(TITLES[key])} || ${notFoundVisible} || t !== ${JSON.stringify(TITLES[key])} || !user || user.textContent === '…') return false; `
    + `${extra} return { hash: location.hash, render: Number(r.getAttribute('data-jfbm-render')), reason: r.getAttribute('data-jfbm-reason'), host: r.getAttribute('data-jfbm-host'), `
    + `params: JSON.parse(r.querySelector('[data-jfbm-demo="params"]').textContent), user: user.textContent, admin: r.querySelector('[data-jfbm-demo="admin"]').textContent, `
    + `headerTitle: t, documentTitle: document.title, historyLength: history.length }; })()`;
const f1On = (key, extra) => pageOn('#fallbackPage >', key, extra);
const ppOn = (key, extra) => pageOn('.userPreferencesPage:not(.hide)', key, extra);
const anyOn = (key, extra) => pageOn('', key, extra);
// Mounted in the fallback host, without the "Page not found" condition (for the positive control, where it shows).
const f1Mounted = (key) => `(() => { const r = document.querySelector('#fallbackPage > [data-jfbm-page="${key}"][data-jfbm-mounted][data-jfbm-state="on"]'); `
    + `return !!r && !!r.querySelector('.jfbm-demo-title') && { hash: location.hash, notFoundVisible: ${notFoundVisible}, headerTitle: ${headerTitle} }; })()`;

const visible = (sel) => `(() => { const a = document.querySelector(${JSON.stringify(sel)}); if (!a) return false; const r = a.getBoundingClientRect(); `
    + `return r.width > 0 && r.height > 0 && r.left >= 0 && r.right <= innerWidth && r.top >= 0 && getComputedStyle(a).visibility !== 'hidden'; })()`;
const home = "location.hash.startsWith('#/home') && !!document.querySelector('.homePage:not(.hide)') && !document.getElementById('fallbackPage')";
const indexHtml = "fetch(new URL('index.html', location.href), { cache: 'no-store' }).then(r => r.text()).then(t => { const s = t.match(/<script type=\"application\\/json\" data-jfbm-head data-jfbm-pages>([^<]*)<\\/script>/); return { "
    + "bookmarksTags: (t.match(/data-jfbm-plugin=/g) || []).length, headStyles: (t.match(/<style data-jfbm-head>/g) || []).length, headFragmentBeforeHeadEnd: t.indexOf('data-jfbm-head') < t.indexOf('</head>'), "
    + "state: s ? JSON.parse(s[1]) : null, pluginPagesTag: /<script plugin=\"PluginPages\"/.test(t) }; })";
const kitPages = "__jfkit.jfbm.instance.pages.state()";
const libraryTools = "(() => { const vis = (a) => { if (!a) return false; const r = a.getBoundingClientRect(); "
    + "return r.width > 0 && r.height > 0 && r.left >= 0 && r.right <= innerWidth && r.top >= 0 && getComputedStyle(a).visibility !== 'hidden'; }; "
    + "const s = document.querySelector('.mainDrawer-scrollContainer > .jfkit-nav'); const a = s && s.querySelector('a[data-jfbm-nav=\"demo-a\"]'); "
    + "const b = s && s.querySelector('a[data-jfbm-nav=\"demo-b\"]'); if (!vis(a) || !vis(b)) return false; "
    + "const pp = document.querySelector('.mainDrawer .pluginMenuOptions'); "
    + "return { header: s.querySelector('.sidebarHeader').textContent, nextSibling: s.nextElementSibling && s.nextElementSibling.className, "
    + "a: { href: a.getAttribute('href'), text: a.textContent.trim(), upgraded: a.classList.contains('emby-button'), order: a.getAttribute('data-order') }, "
    + "b: { href: b.getAttribute('href'), text: b.textContent.trim(), order: b.getAttribute('data-order') }, "
    + "pluginPagesSection: pp ? { links: pp.querySelectorAll('a').length, ours: pp.querySelectorAll('a[data-itemid^=\"Jellyfin.Plugin.Bookmarks\"]').length } : null }; })()";
const drawerLinks = "(() => ({ pluginPagesSection: !!document.querySelector('.mainDrawer .pluginMenuOptions'), "
    + "pluginPagesLinks: [...document.querySelectorAll('.mainDrawer .pluginMenuOptions a[data-itemid^=\"Jellyfin.Plugin.Bookmarks\"]')].map(a => a.getAttribute('data-itemid')), "
    + "libraryToolsLinks: [...document.querySelectorAll('.mainDrawer .jfkit-nav a[data-jfkit-plugin=\"jfbm\"]')].map(a => ({ key: a.getAttribute('data-jfbm-nav'), href: a.getAttribute('href') })) }))()";
const notFoundState = "(() => { const fp = document.getElementById('fallbackPage'); const root = document.querySelector('#fallbackPage > [data-jfbm-page]'); "
    + `return { notFoundVisible: ${notFoundVisible}, ourRootVisible: !!root && root.getClientRects().length > 0, ourRoot: !!root, headerTitle: ${headerTitle}, `
    + "documentTitle: document.title, fallbackTitle: fp && fp.getAttribute('data-title'), original: fp && fp.getAttribute('data-jfkit-orig-title'), "
    + "claim: fp && fp.getAttribute('data-jfkit-claim'), routeClass: document.documentElement.classList.contains('jfbm-route') }; })()";
// After the hand-back: jellyfin-web's own "Page not found", titled "Page not found" in the header, the page's
// data-title and the document, with our root and claim gone.
const handedBack = (v) => `__vars.${v}.notFoundVisible && !__vars.${v}.ourRoot && !__vars.${v}.claim && __vars.${v}.headerTitle === 'Page not found' `
    + `&& __vars.${v}.fallbackTitle === 'Page not found' && __vars.${v}.documentTitle === 'Page not found'`;
// Leaving our page for another unknown route, before the hand-back: our page and our title, "Page not found" still hidden.
const stillOurs = (v, title) => `__vars.${v}.ourRootVisible && !__vars.${v}.notFoundVisible && __vars.${v}.claim === 'jfbm' && __vars.${v}.headerTitle === ${JSON.stringify(title)}`;

const setConfig = (values, name) => ({
    name: name || 'Bookmarks settings through the configuration API: ' + JSON.stringify(values),
    script: `const id = '${PLUGIN_ID}'; const c = await ApiClient.getPluginConfiguration(id); Object.assign(c, ${JSON.stringify(values)}); `
        + 'await ApiClient.updatePluginConfiguration(id, c); const r = await ApiClient.getPluginConfiguration(id); '
        + `return { ${Object.keys(values).map((k) => `${k}: r.${k}`).join(', ')}, PageHostMode: r.PageHostMode };`
});
const probeInit = { name: 'frame probe in every document (probes/pages-probe.js)', initScript: { file: 'probes/pages-probe.js' } };
const reset = { name: 'reset the probe', eval: '__pagesProbe.reset(), true' };
// The kit's render count per page (all mounts) before a navigation, then: exactly one more render of `key` and of all
// pages together, and one set of page events on #fallbackPage since the probe's reset (React's or the kit's, not both).
const renderCounts = (save) => ({ name: `render counts: ${save}`, eval: "Object.assign({}, __jfkit.jfbm.instance.pages.state().renders)", save });
const renderedOnce = (key, before, after, what) => [
    renderCounts(after),
    {
        name: `${what}: one render and one set of page events`,
        assert: `__vars.${after}['${key}'] === (__vars.${before}['${key}'] || 0) + 1 && Object.values(__vars.${after}).reduce((a, b) => a + b, 0) === Object.values(__vars.${before}).reduce((a, b) => a + b, 0) + 1 `
            + "&& __pagesProbe.events.filter(e => e.kind === 'event:viewshow' && e.detail === '#fallbackPage').length === 1",
        message: `${what} rendered the page twice or fired the page events twice`
    }
];
const probe = (save) => [{ sleep: 400 }, { name: `probe: ${save}`, eval: '__pagesProbe.summary()', save }];
const events = (save) => ({ name: `events: ${save}`, eval: "__pagesProbe.events.map(e => e.t + ' ' + e.kind + ' ' + (e.detail || ''))", save });
const transitions = (save) => ({ name: `transitions on our routes: ${save}`, eval: "__pagesProbe.transitions.filter(t => t.loc).map(t => [t.t, t.frames, t.title, t.nf, t.root].join(' | '))", save });
const openDrawer = [
    { name: 'open the drawer', click: '.skinHeader .mainDrawerButton' },
    { name: 'drawer open', waitFor: visible('.mainDrawer .userMenuOptions') }
];
const noFlash = (saves) => ({
    name: 'every navigation above: 0 "Page not found" frames on our routes',
    assert: `[${saves.map((s) => `__vars.${s}`).join(', ')}].every(p => p && p.flashFrames === 0)`,
    message: 'a probe counted "Page not found" frames on a demo route'
});
const noHome = (saves) => ({
    name: 'the header never read "Home" or "Page not found" on our routes',
    assert: `[${saves.map((s) => `__vars.${s}`).join(', ')}].every(p => p && !('Home' in p.headerTitlesOnOurRoutes) && !('Page not found' in p.headerTitlesOnOurRoutes))`,
    message: 'a demo route showed the title Home or Page not found'
});
const demoOn = [
    setConfig({ EnablePageDemo: true, EnableDemoPageB: true, EnableWebUi: true }, 'turn the page demo on (the developer setting EnablePageDemo; per request)'),
    { name: 'reload, so index.html carries the demo\'s head fragment', reload: true },
    { name: 'the home page is shown', waitFor: "!!document.querySelector('.homePage:not(.hide)')" }
];
const write = (file, content) => fs.writeFileSync(path.join(folder, file), JSON.stringify(content, null, 2) + '\n');

// ----------------------------------------------------------------------------------------------------- pages-fallback.json

const f1Probes = ['probeDrawerToA', 'probeAToB', 'probeLeaveToHome', 'probeBackToB', 'probeBackToA', 'probeForwardToB', 'probeParams', 'probeReload', 'probeStaleLink', 'probeHandBack', 'probeFreshA', 'probeHandBackFresh'];
write('pages-fallback.json', {
    name: 'The fallback routes of Bookmarks\' page demo (#/jfbm-demo-a, #/jfbm-demo-b) in the default FallbackOnly mode: "Library tools" above "User", no "Page not found" frame (frame probe, with its positive control at the end), titles, page events on swaps (the drawer\'s Home within 2 s, one history entry per navigation), back and forward, parameters, hard reload, a stale Plugin Pages link, an unknown key and the hand-back. Turns EnablePageDemo on (sandbox data only).',
    failOnException: false,
    timeout: 30000,
    steps: [
        probeInit,
        { name: 'sign in as admin', login: 'admin' },
        ...demoOn,
        { name: 'index.html: one Bookmarks tag, the head fragment once before </head>, and its page state', eval: indexHtml, save: 'indexHtml', full: true },
        { name: 'one tag, one head fragment, FallbackOnly, both demo pages known and on (next to My Bookmarks and My Clips)', assert: "__vars.indexHtml.bookmarksTags === 1 && __vars.indexHtml.headStyles === 1 && __vars.indexHtml.headFragmentBeforeHeadEnd && __vars.indexHtml.state.mode === 'FallbackOnly' && __vars.indexHtml.state.pages.join() === 'bookmarks,clips,demo-a,demo-b' && __vars.indexHtml.state.off.length === 0" },
        { name: 'the kit registered both pages on the fallback host', waitFor: `${kitPages}.hostsKnown && ${kitPages}.hosts['demo-a'] === 'Fallback' && ${kitPages}.hosts['demo-b'] === 'Fallback' && ${kitPages}`, save: 'pages' },
        ...openDrawer,
        { name: '"Library tools" right before "User", both links visible and upgraded', waitFor: libraryTools, save: 'drawer', full: true },
        { name: 'the section sits before .userMenuOptions and links the fallback routes', assert: "__vars.drawer.nextSibling === 'userMenuOptions' && __vars.drawer.header === 'Library tools' && __vars.drawer.a.href === '#/jfbm-demo-a' && __vars.drawer.a.upgraded" },
        { screenshot: 'pages-fallback-drawer.png' },
        { name: 'history length before the first navigation', eval: 'history.length', save: 'history0' },
        reset,
        { name: 'click Demo page A in Library tools', click: '.mainDrawer .jfkit-nav a[data-jfbm-nav="demo-a"]' },
        { name: 'Demo page A renders in #fallbackPage with its title and data', waitFor: f1On('demo-a'), save: 'a' },
        ...probe('probeDrawerToA'),
        events('eventsDrawerToA'),
        { screenshot: 'pages-fallback-a.png' },
        reset,
        { name: 'in-page link to Demo page B (appRouter.show; FallbackRoute stays mounted)', click: '#fallbackPage [data-jfbm-page="demo-a"] a[data-jfbm-link="demo-b"]' },
        { name: 'Demo page B renders, title updated', waitFor: f1On('demo-b'), save: 'b' },
        ...probe('probeAToB'),
        events('eventsAToB'),
        reset,
        ...openDrawer,
        { name: 'click Home in the drawer (appRouter.show right after an fallback-to-fallback swap)', click: '.mainDrawer .navMenuOption[href="#/home"]' },
        { name: 'the home page shows within 2 s', waitFor: home + ' && { hash: location.hash, historyLength: history.length }', timeout: 2000, save: 'home' },
        ...probe('probeLeaveToHome'),
        {
            name: 'history grew by exactly one entry per navigation (drawer to A, link to B, drawer to Home)',
            assert: '__vars.a.historyLength === __vars.history0 + 1 && __vars.b.historyLength === __vars.history0 + 2 && __vars.home.historyLength === __vars.history0 + 3',
            message: 'a navigation pushed no entry or two'
        },
        renderCounts('rendersAtHome'),
        reset,
        { name: 'browser back (home to B: FallbackRoute mounts before our popstate listener runs)', eval: 'history.back(), true' },
        { name: 'Demo page B again, rendered again', waitFor: f1On('demo-b'), save: 'backToB' },
        ...probe('probeBackToB'),
        ...renderedOnce('demo-b', 'rendersAtHome', 'rendersBackToB', 'back into the fallback host from Home'),
        reset,
        { name: 'browser back (B to A)', eval: 'history.back(), true' },
        { name: 'Demo page A again, rendered again', waitFor: f1On('demo-a', 'if (Number(r.getAttribute(\'data-jfbm-render\')) <= __vars.a.render) return false;'), save: 'backToA' },
        ...probe('probeBackToA'),
        ...renderedOnce('demo-a', 'rendersBackToB', 'rendersBackToA', 'back from B to A'),
        reset,
        { name: 'browser forward (A to B)', eval: 'history.forward(), true' },
        { name: 'Demo page B again, rendered again', waitFor: f1On('demo-b', 'if (Number(r.getAttribute(\'data-jfbm-render\')) <= __vars.backToB.render) return false;'), save: 'forwardToB' },
        ...probe('probeForwardToB'),
        ...renderedOnce('demo-b', 'rendersBackToA', 'rendersForwardToB', 'forward from A to B'),
        reset,
        { name: 'link to this page with extra hash parameters', click: '#fallbackPage [data-jfbm-page="demo-b"] a[data-jfbm-demo="self-params"]' },
        { name: 'the parameters reach the page', waitFor: f1On('demo-b', "if (JSON.parse(r.querySelector('[data-jfbm-demo=\"params\"]').textContent).tab !== 'two') return false;"), save: 'params' },
        ...probe('probeParams'),
        { name: 'hard reload of the fallback deep link', reload: true },
        { name: 'after reload: Demo page B with its parameters and title', waitFor: f1On('demo-b', "if (JSON.parse(r.querySelector('[data-jfbm-demo=\"params\"]').textContent).tab !== 'two') return false;"), save: 'reload' },
        ...probe('probeReload'),
        transitions('reloadTransitions'),
        events('reloadEvents'),
        { screenshot: 'pages-fallback-b-reload.png' },
        reset,
        { name: 'a stale Plugin Pages deep link', goto: '/web/index.html#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fdemo-a' },
        { name: 'Demo page A renders in whichever host serves the link (Plugin Pages\' when installed, else the fallback host)', waitFor: anyOn('demo-a'), save: 'staleLink' },
        ...probe('probeStaleLink'),
        { name: 'an unknown key on our prefix', goto: '/web/index.html#/jfbm-nope' },
        { sleep: 1500 },
        { name: '#/jfbm-nope shows jellyfin-web\'s Page not found', eval: notFoundState, save: 'unknownKey' },
        { name: 'not claimed', assert: '__vars.unknownKey.notFoundVisible && !__vars.unknownKey.ourRoot && !__vars.unknownKey.routeClass' },
        { name: 'back to A', goto: '/web/index.html#/jfbm-demo-a' },
        { name: 'A', waitFor: f1On('demo-a') },
        reset,
        { name: 'to an unknown route of nobody (FallbackRoute stays mounted)', goto: '/web/index.html#/nobody-route' },
        { sleep: 1800 },
        { name: 'the unknown route gets its Page not found and title back', eval: notFoundState, save: 'handBack' },
        { name: 'handed back, titled "Page not found"', assert: handedBack('handBack') },
        ...probe('probeHandBack'),
        // The same from a page React mounted for our route (FallbackRoute not mounted before): the head fragment's listener
        // titles it before the kit claims it, so the original title must come from the fragment (the hand-back).
        ...openDrawer,
        { name: 'click Home in the drawer (FallbackRoute unmounts)', click: '.mainDrawer .navMenuOption[href="#/home"]' },
        { name: 'the home page', waitFor: home },
        ...openDrawer,
        reset,
        { name: 'click Demo page A in Library tools again (a fresh FallbackRoute page)', click: '.mainDrawer .jfkit-nav a[data-jfbm-nav="demo-a"]' },
        { name: 'Demo page A', waitFor: f1On('demo-a') },
        ...probe('probeFreshA'),
        { name: 'the page\'s own title is kept for the hand-back', eval: notFoundState, save: 'freshA' },
        { name: 'original title "Page not found", ours shown', assert: "__vars.freshA.original === 'Page not found' && __vars.freshA.fallbackTitle === 'Demo page A' && __vars.freshA.claim === 'jfbm'" },
        reset,
        { name: 'to an unknown route of nobody from the fresh page', goto: '/web/index.html#/nobody-route' },
        { sleep: 300 },
        { name: 'before the hand-back: still our page under our title', eval: notFoundState, save: 'leavingFresh' },
        { name: 'our page and title until the hand-back, "Page not found" hidden', assert: stillOurs('leavingFresh', 'Demo page A') },
        { sleep: 1500 },
        { name: 'after the hand-back', eval: notFoundState, save: 'handBackFresh' },
        { name: 'handed back, titled "Page not found" (not "Demo page A")', assert: handedBack('handBackFresh') },
        ...probe('probeHandBackFresh'),
        transitions('handBackFreshTransitions'),
        { name: 'no uncaught exceptions (the stale CustomRatings tag excepted)', noExceptions: true, ignore: IGNORE },
        noFlash(f1Probes),
        noHome(f1Probes.filter((p) => p !== 'probeHandBack' && p !== 'probeHandBackFresh' && p !== 'probeLeaveToHome')),

        // Positive control: with the pre-hide rule outranked, the same probe must count flash frames.
        { name: 'positive control: pre-hide off in every later document (probes/pages-prehide-off.js)', initScript: { file: 'probes/pages-prehide-off.js' } },
        { name: 'control: load Demo page A', goto: '/web/index.html#/jfbm-demo-a' },
        { name: 'control: the hash navigation committed', waitFor: "location.hash === '#/jfbm-demo-a'" },
        { name: 'control: cold load', reload: true },
        { name: 'control: Demo page A mounted (Page not found now shows next to it)', waitFor: f1Mounted('demo-a'), save: 'control' },
        ...probe('probeControl'),
        { name: 'control: the probe counted flash frames', assert: '__vars.probeControl.flashFrames > 0', message: 'the probe saw no flash with the pre-hide rule off' },
        { screenshot: 'pages-fallback-control.png' },
        reset,
        { name: 'control: leave for an unknown route of nobody (the claim rule outranked too)', goto: '/web/index.html#/nobody-route' },
        { sleep: 1500 },
        ...probe('probeLeaveControl'),
        { name: 'control: the probe counts "Page not found" frames while our root is still there', assert: '__vars.probeLeaveControl.flashFrames > 0', message: 'the probe saw no flash while leaving with the pre-hide rules off' }
    ]
});

// ------------------------------------------------------------------------------------------------ pages-signed-out.json

write('pages-signed-out.json', {
    name: 'A signed-out fallback deep link (#/jfbm-demo-b?tab=deep) goes to the login page and back, with no "Page not found" frame. Needs EnablePageDemo on (pages-fallback.json turns it on).',
    failOnException: false,
    timeout: 30000,
    steps: [
        probeInit,
        { name: 'open the deep link signed out', goto: '/web/index.html#/jfbm-demo-b?tab=deep' },
        { name: 'redirected to the login page with url=', waitFor: "location.hash.startsWith('#/login') && !!document.querySelector('#txtManualName') && location.hash", save: 'loginHash' },
        { name: 'the url parameter holds the deep link', assert: "decodeURIComponent((location.hash.split('url=')[1] || '').split('&')[0]) === '/jfbm-demo-b?tab=deep'" },
        { name: 'probe before sign-in', eval: '__pagesProbe.summary()', save: 'probeBeforeLogin' },
        { screenshot: 'pages-signed-out-login.png' },
        reset,
        { name: 'sign in on that login page', login: 'admin', stay: true },
        { name: 'back on Demo page B (fallback host) with the extra parameter', waitFor: f1On('demo-b', "if (JSON.parse(r.querySelector('[data-jfbm-demo=\"params\"]').textContent).tab !== 'deep') return false;"), save: 'afterLogin' },
        ...probe('probeAfterLogin'),
        { screenshot: 'pages-signed-out-after.png' },
        noFlash(['probeBeforeLogin', 'probeAfterLogin']),
        { name: 'no uncaught exceptions', noExceptions: true, ignore: IGNORE }
    ]
});

// ------------------------------------------------------------------------------------------------ pages-slow-script.json

write('pages-slow-script.json', {
    name: 'A cold load of #/jfbm-demo-a with Bookmarks\' ClientScript held back 1.5 s (webcheck delayRequests) shows no "Page not found" frame and the right title from the head fragment, then the same with the pre-hide rule outranked (positive control, flash frames > 0). Needs EnablePageDemo on (it turns it on).',
    failOnException: false,
    timeout: 30000,
    steps: [
        probeInit,
        { name: 'sign in as admin', login: 'admin' },
        ...demoOn,
        { name: 'load Demo page A', goto: '/web/index.html#/jfbm-demo-a' },
        { name: 'the hash navigation committed', waitFor: "location.hash === '#/jfbm-demo-a'" },
        { name: 'baseline cold load', reload: true },
        { name: 'baseline: Demo page A', waitFor: f1On('demo-a'), save: 'baseline' },
        ...probe('probeBaseline'),
        { name: 'hold every /Bookmarks/ClientScript request 1.5 s', delayRequests: { url: '/Bookmarks/ClientScript', ms: 1500 } },
        { name: 'slow cold load', reload: true },
        { name: 'slow: Demo page A eventually', waitFor: f1On('demo-a'), timeout: 20000, save: 'slow' },
        ...probe('probeSlow'),
        transitions('slowTransitions'),
        events('slowEvents'),
        {
            name: 'slow: the router showed #fallbackPage over a second before our script ran',
            eval: "(() => { const shown = __pagesProbe.events.find(e => e.kind === 'event:viewbeforeshow' && e.detail === '#fallbackPage'); return { fallbackPageShownAt: shown ? shown.t : null, scriptStartedAt: Math.round(__jfkit.jfbm.timing.script), marginMs: shown ? Math.round(__jfkit.jfbm.timing.script) - shown.t : null }; })()",
            save: 'race'
        },
        { name: 'the race really happened (the check is not vacuous)', assert: '__vars.race.fallbackPageShownAt !== null && __vars.race.marginMs > 1000' },
        { name: 'slow: 0 flash frames, and only our title on our route', assert: "__vars.probeSlow.flashFrames === 0 && Object.keys(__vars.probeSlow.headerTitlesOnOurRoutes).every(t => t === 'Demo page A' || t === '' || t === 'null')", message: 'the slow cold load flashed Page not found or another title (before the header exists the probe records null)' },
        { screenshot: 'pages-slow-script.png' },
        { name: 'slow: the page\'s own title was kept although the head fragment titled the page before our script ran', eval: notFoundState, save: 'slowState' },
        { name: 'slow: original title "Page not found"', assert: "__vars.slowState.original === 'Page not found' && __vars.slowState.fallbackTitle === 'Demo page A'" },
        reset,
        { name: 'slow: to an unknown route of nobody', goto: '/web/index.html#/nobody-route' },
        { sleep: 1800 },
        { name: 'slow: after the hand-back', eval: notFoundState, save: 'slowHandBack' },
        { name: 'slow: handed back, titled "Page not found"', assert: handedBack('slowHandBack') },
        ...probe('probeSlowHandBack'),
        { name: 'slow: no flash while leaving', assert: '__vars.probeSlowHandBack.flashFrames === 0' },
        { name: 'back to Demo page A for the control', goto: '/web/index.html#/jfbm-demo-a' },
        { name: 'the hash navigation committed', waitFor: "location.hash === '#/jfbm-demo-a'" },
        { name: 'positive control: pre-hide off in every later document', initScript: { file: 'probes/pages-prehide-off.js' } },
        { name: 'slow cold load with the pre-hide rule outranked', reload: true },
        { name: 'control: Demo page A mounted', waitFor: f1Mounted('demo-a'), timeout: 20000, save: 'control' },
        ...probe('probeSlowControl'),
        { name: 'control: the probe counted flash frames', assert: '__vars.probeSlowControl.flashFrames > 0', message: 'the probe saw no flash with the pre-hide rule off' },
        { name: 'stop holding requests', delayRequests: null },
        { name: 'baseline: 0 flash frames too', assert: '__vars.probeBaseline.flashFrames === 0' },
        { name: 'no uncaught exceptions', noExceptions: true, ignore: IGNORE }
    ]
});

// ----------------------------------------------------------------------------------------------------- pages-pluginpages.json

const ppProbes = ['probeA', 'probeB', 'probeBack', 'probeForward', 'probeReload'];
write('pages-pluginpages.json', {
    name: 'Bookmarks\' page demo through Plugin Pages 2.4.7 in PageHostMode=Auto (registered at startup through its DI manager, no config.json): the links in Plugin Pages\' drawer section and none of ours, the text/html shells rendered in its host with our title from the first frame, a second page, back and forward, parameters and a hard reload. Needs Plugin Pages (default or baseurl sandbox) and EnablePageDemo=true, PageHostMode=Auto at startup.',
    failOnException: false,
    timeout: 30000,
    steps: [
        probeInit,
        // Plugin Pages' startup task registers its File Transformation a few seconds after "Startup complete", and the kit
        // chooses the host once per page load, so a check started right after the restart waits for the tag.
        { name: 'open the login page', goto: '/web/index.html#/login' },
        { name: 'index.html carries Plugin Pages\' tag', waitFor: "fetch(new URL('index.html', location.href), { cache: 'no-store' }).then(r => r.text()).then(t => /<script plugin=\"PluginPages\"/.test(t))", timeout: 90000 },
        { name: 'reload, so the page has the tag', reload: true },
        { name: 'sign in as admin', login: 'admin' },
        { name: 'Home shown and the Bookmarks script ran', waitFor: "!!window.__jfkit && !!window.__jfkit.jfbm && !!document.querySelector('.homePage:not(.hide)')", timeout: 30000 },
        // The session's warm-up: the first run after a restart into Auto sometimes found the
        // drawer empty, the first page after the sign-in having asked for Users/null/...; a second run passed. A reload
        // starts the web client with the signed-in user from its first request, and the check waits until the drawer's
        // user menu is built before it goes on.
        { name: 'warm-up: reload once after the sign-in, so the page starts with the signed-in user', reload: true },
        { name: 'Home shown again with the user known, the drawer\'s user menu built and the Bookmarks script ran', waitFor: "!!window.ApiClient && !!ApiClient.getCurrentUserId() && !!window.__jfkit && !!window.__jfkit.jfbm && !!document.querySelector('.homePage:not(.hide)') && !!document.querySelector('.mainDrawer .userMenuOptions')", timeout: 30000 },
        { sleep: 1500 },
        { name: 'index.html: one Bookmarks tag, Plugin Pages\' tag, the head fragment in Auto mode', eval: indexHtml, save: 'indexHtml', full: true },
        { name: 'Auto mode, both pages known and on', assert: "__vars.indexHtml.bookmarksTags === 1 && __vars.indexHtml.pluginPagesTag && __vars.indexHtml.state.mode === 'Auto' && __vars.indexHtml.state.off.length === 0" },
        { name: 'GET /PluginPages/User lists both demo pages (registered at startup)', eval: "ApiClient.getJSON(ApiClient.getUrl('PluginPages/User'))", save: 'pluginPagesUser', full: true },
        { name: 'both Ids and shell URLs listed', assert: `['${IDS['demo-a']}', '${IDS['demo-b']}'].every(id => __vars.pluginPagesUser.Items.some(i => i.Id === id && i.Url === '/Bookmarks/Pages/' + id.replace('Jellyfin.Plugin.Bookmarks.Demo', 'demo-').replace('Page', '').toLowerCase()))` },
        { name: 'the kit chose the Plugin Pages host for both', waitFor: `${kitPages}.hostsKnown && ${kitPages}.hosts['demo-a'] === 'PluginPages' && ${kitPages}.hosts['demo-b'] === 'PluginPages' && ${kitPages}`, save: 'pages' },
        ...openDrawer,
        { name: 'both links visible in Plugin Pages\' drawer section', waitFor: `${visible(`.mainDrawer .pluginMenuOptions a[data-itemid="${IDS['demo-a']}"]`)} && ${visible(`.mainDrawer .pluginMenuOptions a[data-itemid="${IDS['demo-b']}"]`)}` },
        { sleep: 1500 },
        { name: 'the drawer after Plugin Pages had its chance', eval: drawerLinks, save: 'drawer' },
        { name: 'no Library tools link of ours while Plugin Pages shows them (the two demo pages, My Bookmarks and My Clips)', assert: '__vars.drawer.pluginPagesLinks.length === 4 && __vars.drawer.libraryToolsLinks.length === 0' },
        { screenshot: 'pages-pp-drawer.png' },
        reset,
        { name: 'click Demo page A in Plugin Pages\' section', click: `.mainDrawer a[data-itemid="${IDS['demo-a']}"]` },
        { name: 'Demo page A renders in the Plugin Pages host with its title and data', waitFor: ppOn('demo-a'), save: 'a' },
        ...probe('probeA'),
        { screenshot: 'pages-pp-a.png' },
        reset,
        { name: 'in-page link to Demo page B', click: '.userPreferencesPage:not(.hide) [data-jfbm-page="demo-a"] a[data-jfbm-link="demo-b"]' },
        { name: 'Demo page B renders with its title', waitFor: ppOn('demo-b'), save: 'b' },
        ...probe('probeB'),
        reset,
        { name: 'browser back', eval: 'history.back(), true' },
        { name: 'back on Demo page A: the restored view rendered again', waitFor: ppOn('demo-a', 'if (Number(r.getAttribute(\'data-jfbm-render\')) <= __vars.a.render) return false;'), save: 'back' },
        ...probe('probeBack'),
        reset,
        { name: 'browser forward', eval: 'history.forward(), true' },
        { name: 'forward on Demo page B, rendered again', waitFor: ppOn('demo-b', 'if (Number(r.getAttribute(\'data-jfbm-render\')) <= __vars.b.render) return false;'), save: 'forward' },
        ...probe('probeForward'),
        { name: 'link to this page with extra hash parameters', click: '.userPreferencesPage:not(.hide) [data-jfbm-page="demo-b"] a[data-jfbm-demo="self-params"]' },
        { name: 'the parameters reach the page', waitFor: ppOn('demo-b', "if (JSON.parse(r.querySelector('[data-jfbm-demo=\"params\"]').textContent).tab !== 'two') return false;"), save: 'params' },
        reset,
        { name: 'hard reload of the deep link', reload: true },
        { name: 'after reload: Demo page B with its parameters and title', waitFor: ppOn('demo-b', "if (JSON.parse(r.querySelector('[data-jfbm-demo=\"params\"]').textContent).tab !== 'two') return false;"), save: 'reload' },
        ...probe('probeReload'),
        transitions('reloadTransitions'),
        { screenshot: 'pages-pp-b-reload.png' },
        noFlash(ppProbes),
        noHome(ppProbes),
        { name: 'no uncaught exceptions', noExceptions: true, ignore: IGNORE }
    ]
});

// ------------------------------------------------------------------------------------------------ pages-relogin.json

write('pages-relogin.json', {
    name: 'The drawer after signing out and back in without a reload. Plugin Pages 2.4.7 doesn\'t add its section again; in Auto mode our "Library tools" links then appear (the drawer-based decision), and in FallbackOnly mode they stay. Needs EnablePageDemo on.',
    failOnException: false,
    timeout: 30000,
    steps: [
        { name: 'sign in as admin', login: 'admin' },
        { name: 'the kit chose its hosts', waitFor: `window.__jfkit && __jfkit.jfbm && ${kitPages}.hostsKnown && ${kitPages}`, save: 'pages' },
        ...openDrawer,
        { sleep: 1500 },
        { name: 'our links before signing out', eval: drawerLinks, save: 'before' },
        { name: 'Sign out visible in the drawer', waitFor: visible('.mainDrawer .btnLogout') },
        { name: 'sign out from the drawer', click: '.mainDrawer .btnLogout', js: true },
        { name: 'on the login page', waitFor: "location.hash.startsWith('#/login') && !!document.querySelector('#txtManualName')" },
        { name: 'sign in again, in the same page load', login: 'admin', stay: true },
        { sleep: 1500 },
        ...openDrawer,
        { sleep: 1500 },
        { name: 'our links after signing in again without a reload', eval: drawerLinks, save: 'after' },
        { name: 'both demo pages still have a link: Plugin Pages\' or ours', assert: "['demo-a', 'demo-b'].every(k => __vars.after.libraryToolsLinks.some(l => l.key === k) || __vars.after.pluginPagesLinks.some(id => id.toLowerCase().endsWith(k.replace('-', '') + 'page')))" },
        { name: 'in Auto mode with Plugin Pages\' section gone, the links are ours (the two demo pages, My Bookmarks and My Clips)', assert: `${kitPages}.mode !== 'Auto' || __vars.after.pluginPagesSection || __vars.after.libraryToolsLinks.length === 4` },
        { screenshot: 'pages-relogin-drawer.png' },
        { name: 'click our Demo page A link', click: '.mainDrawer .jfkit-nav a[data-jfbm-nav="demo-a"]', optional: true },
        { name: 'Demo page A renders', waitFor: anyOn('demo-a'), save: 'a', optional: true },
        { name: 'no uncaught exceptions', noExceptions: true, ignore: IGNORE }
    ]
});

// -------------------------------------------------------------------------------------------------------------- pages-off.json

const turnedOffShown = (title) => `(() => { const t = ${headerTitle}; const notes = [...document.querySelectorAll('.jfbm-page-note, .jfbm-shell-note')].filter(n => n.getClientRects().length > 0 && getComputedStyle(n).display !== 'none'); `
    + `const n = notes.find(x => x.textContent === ${JSON.stringify(title + ' is turned off by your server admin.')}); return !!n && t === ${JSON.stringify(title)} && { note: n.textContent, className: n.className, headerTitle: t, hash: location.hash, host: (n.closest('[data-role="page"]') || {}).id }; })()`;
const webUiOffShown = "(() => { const notes = [...document.querySelectorAll('.userPreferencesPage:not(.hide) .jfbm-shell-note')].filter(n => n.getClientRects().length > 0); "
    + "const n = notes.find(x => x.textContent === 'This page needs the Bookmarks web UI, which is turned off or failed to load.'); "
    + `return !!n && { note: n.textContent, headerTitle: ${headerTitle}, bookmarksTags: document.querySelectorAll('script[data-jfbm-plugin]').length, kit: !!(window.__jfkit && window.__jfkit.jfbm) }; })()`;
write('pages-off.json', {
    name: 'A user page turned off while its link is still there shows "Demo page B is turned off by your server admin." (in the fallback host after the kit asks the shell, and in Plugin Pages\' host from the 200 shell), and with EnableWebUi off a page link shows the shell\'s plain-text note. Needs Plugin Pages (default or baseurl sandbox). Changes EnablePageDemo, EnableDemoPageB and EnableWebUi through the API and restores EnableDemoPageB and EnableWebUi at the end (sandbox data only).',
    failOnException: false,
    timeout: 30000,
    steps: [
        probeInit,
        { name: 'sign in as admin', login: 'admin' },
        ...demoOn,
        { name: 'the kit chose its hosts', waitFor: `${kitPages}.hostsKnown && ${kitPages}`, save: 'pages' },
        ...openDrawer,
        { sleep: 1500 },
        { name: 'the drawer links Demo page B', eval: drawerLinks, save: 'drawerBefore' },
        { name: 'Demo page B has a link (ours, or Plugin Pages\' in Auto mode)', waitFor: `${visible('.mainDrawer .jfkit-nav a[data-jfbm-nav="demo-b"]')} || ${visible(`.mainDrawer a[data-itemid="${IDS['demo-b']}"]`)}` },
        { name: 'close the drawer by going to Demo page A', goto: '/web/index.html#/jfbm-demo-a' },
        { name: 'Demo page A', waitFor: f1On('demo-a') },
        setConfig({ EnableDemoPageB: false }, 'turn Demo page B off (per request; the drawer link stays until the next page load)'),
        ...openDrawer,
        { name: 'the still-present link to Demo page B', eval: drawerLinks, save: 'drawerAfterOff' },
        reset,
        { name: 'click the still-present Demo page B link', click: '.mainDrawer .jfkit-nav a[data-jfbm-nav="demo-b"], .mainDrawer .pluginMenuOptions a[data-itemid="' + IDS['demo-b'] + '"]' },
        { name: 'the page says it is turned off, under its own title', waitFor: turnedOffShown('Demo page B'), save: 'offByLink' },
        ...probe('probeOffByLink'),
        { screenshot: 'pages-off-link.png' },
        { name: 'the fallback route of Demo page B', goto: '/web/index.html#/jfbm-demo-b' },
        { name: 'fallback host: the kit asked the shell and shows its sentence', waitFor: turnedOffShown('Demo page B'), save: 'offF1' },
        { name: 'fallback host: the page state is off', assert: "document.querySelector('#fallbackPage > [data-jfbm-page=\"demo-b\"]').getAttribute('data-jfbm-state') === 'off'" },
        { name: 'Plugin Pages\' route of Demo page B', goto: '/web/index.html#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fdemo-b' },
        { name: 'Plugin Pages host: the 200 shell\'s note, titled by the kit', waitFor: turnedOffShown('Demo page B'), save: 'offPluginPages' },
        { screenshot: 'pages-off-pluginpages.png' },
        setConfig({ EnableDemoPageB: true }, 'turn Demo page B on again'),
        setConfig({ EnableWebUi: false }, 'turn the Bookmarks web UI off (per request)'),
        { name: 'a Plugin Pages link to Demo page A', goto: '/web/index.html#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fdemo-a' },
        { name: 'the hash navigation committed', waitFor: "location.hash.indexOf('demo-a') > 0" },
        { name: 'reload with the web UI off', reload: true },
        { name: 'the shell\'s plain-text note shows, with no Bookmarks script', waitFor: webUiOffShown, save: 'webUiOff' },
        { name: 'no Bookmarks tag or kit on the page', assert: '__vars.webUiOff.bookmarksTags === 0 && !__vars.webUiOff.kit' },
        { screenshot: 'pages-off-webui.png' },
        setConfig({ EnableWebUi: true }, 'turn the Bookmarks web UI on again'),
        { name: 'reload with the web UI on', reload: true },
        { name: 'Demo page A renders again', waitFor: anyOn('demo-a'), save: 'backOn' },
        { name: 'no Page not found frame on the turned-off page', assert: '__vars.probeOffByLink.flashFrames === 0' },
        { name: 'no uncaught exceptions', noExceptions: true, ignore: IGNORE }
    ]
});

// -------------------------------------------------------------------------------------------------------------- pages-off-bare.json

// The same bullet without Plugin Pages (the bare sandbox): the still-present "Library tools" link and the fallback
// route say the page is turned off; an old Plugin Pages link is rewritten by FallbackRoute and claimed by the fallback host (the
// "Broken Plugin Pages route"). With EnableWebUi off there is no host that fetches a shell: after the reload no link of
// ours is left, a deep link shows jellyfin-web's own "Page not found", and the shell itself still answers its note.
const shellNote = "(async () => { const r = await fetch(ApiClient.getUrl('Bookmarks/Pages/demo-a'), { headers: { Authorization: 'MediaBrowser Token=\"' + ApiClient.accessToken() + '\"' } }); const t = await r.text(); "
    + "return { status: r.status, type: r.headers.get('content-type'), note: t.includes('<p class=\"jfbm-shell-note\">This page needs the Bookmarks web UI, which is turned off or failed to load.</p>'), script: /<script/i.test(t) }; })()";
const webUiOffBare = `(() => { const nf = ${notFoundVisible}; if (!nf) return false; return { notFoundVisible: nf, headerTitle: ${headerTitle}, bookmarksTags: document.querySelectorAll('script[data-jfbm-plugin]').length, `
    + "headFragment: document.querySelectorAll('[data-jfbm-head]').length, kit: !!(window.__jfkit && window.__jfkit.jfbm), routeClass: document.documentElement.classList.contains('jfbm-route') }; })()";
write('pages-off-bare.json', {
    name: 'Without Plugin Pages (bare sandbox): Demo page B turned off while its "Library tools" link is still there shows "Demo page B is turned off by your server admin." under its own title, from the link, its fallback route and an old Plugin Pages link (rewritten by FallbackRoute and claimed by the fallback host). With EnableWebUi off no link of ours is left after a reload and a deep link shows jellyfin-web\'s own "Page not found" (no host fetches a shell without Plugin Pages), while the shell still answers its plain-text note. Changes EnablePageDemo, EnableDemoPageB and EnableWebUi through the API and restores EnableDemoPageB and EnableWebUi at the end (sandbox data only).',
    failOnException: false,
    timeout: 30000,
    steps: [
        probeInit,
        { name: 'sign in as admin', login: 'admin' },
        ...demoOn,
        { name: 'the kit chose the fallback host (no Plugin Pages)', waitFor: `${kitPages}.hostsKnown && ${kitPages}.hosts['demo-b'] === 'Fallback' && typeof PluginPages === 'undefined' && ${kitPages}`, save: 'pages' },
        ...openDrawer,
        { sleep: 1500 },
        { name: 'the drawer links Demo page B', eval: drawerLinks, save: 'drawerBefore' },
        { name: 'Demo page B has a "Library tools" link', waitFor: visible('.mainDrawer .jfkit-nav a[data-jfbm-nav="demo-b"]') },
        { name: 'close the drawer by going to Demo page A', goto: '/web/index.html#/jfbm-demo-a' },
        { name: 'Demo page A', waitFor: f1On('demo-a') },
        setConfig({ EnableDemoPageB: false }, 'turn Demo page B off (per request; the drawer link stays until the next page load)'),
        ...openDrawer,
        { name: 'the still-present link to Demo page B', eval: drawerLinks, save: 'drawerAfterOff' },
        reset,
        { name: 'click the still-present Demo page B link', click: '.mainDrawer .jfkit-nav a[data-jfbm-nav="demo-b"]' },
        { name: 'the page says it is turned off, under its own title', waitFor: turnedOffShown('Demo page B'), save: 'offByLink' },
        ...probe('probeOffByLink'),
        { screenshot: 'pages-off-bare-link.png' },
        { name: 'the fallback route of Demo page B', goto: '/web/index.html#/jfbm-demo-b' },
        { name: 'fallback host: the kit asked the shell and shows its sentence', waitFor: turnedOffShown('Demo page B'), save: 'offF1' },
        { name: 'fallback host: the page state is off', assert: "document.querySelector('#fallbackPage > [data-jfbm-page=\"demo-b\"]').getAttribute('data-jfbm-state') === 'off'" },
        reset,
        { name: 'an old Plugin Pages link to Demo page B', goto: '/web/index.html#/userpluginsettings.html?pageUrl=%2FBookmarks%2FPages%2Fdemo-b' },
        { name: 'rewritten by FallbackRoute, claimed by the fallback host: the same sentence', waitFor: turnedOffShown('Demo page B'), save: 'offOldLink' },
        ...probe('probeOffOldLink'),
        { name: 'the old link ended on #/userpluginsettings? in #fallbackPage', assert: "location.hash.startsWith('#/userpluginsettings?') && __vars.offOldLink.host === 'fallbackPage'" },
        { screenshot: 'pages-off-bare-old-link.png' },
        setConfig({ EnableDemoPageB: true }, 'turn Demo page B on again'),
        setConfig({ EnableWebUi: false }, 'turn the Bookmarks web UI off (per request)'),
        { name: 'the fallback route of Demo page A', goto: '/web/index.html#/jfbm-demo-a' },
        { name: 'the hash navigation committed', waitFor: "location.hash === '#/jfbm-demo-a'" },
        { name: 'reload with the web UI off', reload: true },
        { name: 'jellyfin-web\'s own "Page not found", with no Bookmarks tag, head fragment or kit', waitFor: webUiOffBare, save: 'webUiOff' },
        { name: 'nothing of ours on the page', assert: '__vars.webUiOff.bookmarksTags === 0 && __vars.webUiOff.headFragment === 0 && !__vars.webUiOff.kit && !__vars.webUiOff.routeClass' },
        { screenshot: 'pages-off-bare-webui.png' },
        ...openDrawer,
        { sleep: 1500 },
        { name: 'the drawer with the web UI off', eval: drawerLinks, save: 'drawerWebUiOff' },
        { name: 'no "Library tools" section or link of ours is left', assert: "__vars.drawerWebUiOff.libraryToolsLinks.length === 0 && !document.querySelector('.mainDrawer .jfkit-nav')" },
        { name: 'the shell still answers its plain-text note (text/html, no script)', eval: shellNote, save: 'shell' },
        { name: 'shell as expected', assert: "__vars.shell.status === 200 && /^text\\/html/.test(__vars.shell.type) && __vars.shell.note && !__vars.shell.script" },
        setConfig({ EnableWebUi: true }, 'turn the Bookmarks web UI on again'),
        { name: 'still on the fallback route of Demo page A', assert: "location.hash === '#/jfbm-demo-a'" },
        { name: 'reload with the web UI on', reload: true },
        { name: 'Demo page A renders again', waitFor: f1On('demo-a'), save: 'backOn' },
        { name: 'no Page not found frame on the turned-off page', assert: '__vars.probeOffByLink.flashFrames === 0 && __vars.probeOffOldLink.flashFrames === 0' },
        { name: 'no uncaught exceptions', noExceptions: true, ignore: IGNORE }
    ]
});

console.log('written pages-fallback.json, pages-signed-out.json, pages-slow-script.json, pages-pluginpages.json, pages-relogin.json, pages-off.json and pages-off-bare.json');
