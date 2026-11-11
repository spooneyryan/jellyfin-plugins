// Developer demo of the kit's page hosting: two user pages, "Demo page A" (#/jfbm-demo-a) and
// "Demo page B" (#/jfbm-demo-b), for the sandbox checks of scripts/webchecks/pages-*.json. They are on only while the
// server's developer setting EnablePageDemo is (off by default). The server's index.html head fragment (BookmarksKit)
// announces every known page, since 1.1.0 on every page load (the My Bookmarks page is always known), with the demo's
// two among the keys turned off while the setting is off: they are then registered here as known but off (no nav link;
// their routes show the "turned off" note), as the kit has it for any page. Without a head fragment at all this file
// does nothing.

const PAGE_DEMO = Object.freeze([
    Object.freeze({ key: 'demo-a', title: 'Demo page A', icon: 'video_library', idSuffix: 'DemoAPage', order: 90 }),
    Object.freeze({ key: 'demo-b', title: 'Demo page B', icon: 'collections_bookmark', idSuffix: 'DemoBPage', order: 91 })
]);

function pageDemoTitle(key) {
    const page = PAGE_DEMO.filter(function (p) {
        return p.key === key;
    })[0];
    return page ? page.title : key;
}

// Idempotent: the kit empties the body before every render. What it shows is what the checks read.
function renderPageDemo(body, params, context) {
    const other = context.key === 'demo-a' ? 'demo-b' : 'demo-a';
    const user = h('span', { 'data-jfbm-demo': 'user', text: '…' });
    const admin = h('span', { 'data-jfbm-demo': 'admin', text: '…' });
    body.append(
        h('h2', { class: 'jfbm-demo-title', text: context.title }),
        h('p', {}, [
            'Host: ', h('b', { 'data-jfbm-demo': 'host', text: context.host }),
            ' | render #' + context.render + ' (' + context.reason + ')'
        ]),
        h('p', {}, ['Parameters: ', h('code', { 'data-jfbm-demo': 'params', text: JSON.stringify(params) })]),
        h('p', {}, ['Signed in as: ', user, ' | Bookmarks API says admin: ', admin]),
        h('p', { class: 'jfbm-demo-links' }, [
            kit.pages.link(other, null, 'Open ' + pageDemoTitle(other)),
            ' ',
            kit.pages.link(context.key, { tab: 'two', n: String(context.render) }, 'This page with ?tab=two', { 'data-jfbm-demo': 'self-params' })
        ])
    );
    kit.user.current().then(function (u) {
        user.textContent = u && u.Name ? u.Name : '(unknown)';
    }, function (e) {
        user.textContent = 'error: ' + e.message;
    });
    api('GET', 'Bookmarks/Capabilities').then(function (capabilities) {
        admin.textContent = String(!!(capabilities && capabilities.IsAdmin));
    }, function (e) {
        admin.textContent = 'error: ' + e.message;
    });
}

if (kit.pages.announced()) {
    PAGE_DEMO.forEach(function (page) {
        kit.pages.register(Object.assign({ render: renderPageDemo }, page));
    });
}
