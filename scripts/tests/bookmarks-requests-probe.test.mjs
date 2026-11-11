// node --test scripts/tests/bookmarks-requests-probe.test.mjs (run by tests/Repo.Tests ScriptsTests).
// webchecks/probes/bookmarks-requests-probe.js in a VM context whose scripts carry the server's URLs, so their call stacks
// read as a browser's: the Bookmarks script's own fetch counts as direct whether or not another plugin's script (Access
// Roles' saved-sections watch) wrapped window.fetch after the probe, once per request; a request jellyfin-web makes for
// the script counts as through it; one of jellyfin-web's own is never recorded (Bookmarks 1.2.1).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const probe = fs.readFileSync(path.join(here, '..', 'webchecks', 'probes', 'bookmarks-requests-probe.js'), 'utf8');
const ORIGIN = 'http://127.0.0.1:8106';

function page() {
    const sent = [];
    const context = vm.createContext({
        location: { href: ORIGIN + '/web/index.html#/home' },
        URL,
        performance: { now: () => 1 },
        XMLHttpRequest: function () {},
        fetch: function (input) {
            sent.push(String(input));
            return Promise.resolve({ ok: true, headers: { get: () => null } });
        }
    });
    context.XMLHttpRequest.prototype.open = function () {};
    context.window = context;
    const run = (source, file) => vm.runInContext(source, context, { filename: ORIGIN + file });
    run(probe, '/probe.js');
    // jellyfin-web's ApiClient, called by the script (as ApiClient.getItem is).
    run('window.ApiClient = { getItem: function (u) { return fetch(u); } };', '/web/main.jellyfin.bundle.js');
    run(`window.bookmarks = {
        own: function (u) { return window.fetch(u); },
        bare: function (u) { return fetch(u); },
        viaWeb: function (u) { return ApiClient.getItem(u); }
    };`, '/Bookmarks/ClientScript');
    return { context, run, sent, list: () => JSON.parse(JSON.stringify(context.__bookmarksRequests.list.map((x) => ({ url: x.url.replace(ORIGIN, ''), direct: x.direct })))) };
}

// Access Roles' watchKeptSaves, as plugins/AccessRoles/.../Web/js/20-api.js wraps window.fetch.
const AR_WRAP = `(function () {
    if (typeof window.fetch !== 'function' || window.fetch.__jfarKept) { return; }
    const original = window.fetch;
    const wrapped = function () { const result = original.apply(this, arguments); result.then(function () {}, function () {}); return result; };
    wrapped.__jfarKept = true;
    window.fetch = wrapped;
})();`;

function exercise(p) {
    p.context.bookmarks.own('/Bookmarks/Clips/Status?ids=1');
    p.context.bookmarks.bare('/Bookmarks/Clips?q=x');
    p.context.bookmarks.viaWeb('/Items/abc');
    p.run('fetch("/Items/Latest");', '/web/main.jellyfin.bundle.js');
}

const EXPECTED = [
    { url: '/Bookmarks/Clips/Status?ids=1', direct: true },
    { url: '/Bookmarks/Clips?q=x', direct: true },
    { url: '/Items/abc', direct: false }
];

test('without another wrapper: the script\'s own requests are direct, jellyfin-web\'s for it through it, its own unrecorded', () => {
    const p = page();
    exercise(p);
    assert.deepEqual(p.list(), EXPECTED);
    assert.equal(p.sent.length, 4);
});

test('with Access Roles\' wrapper on top: the same, once per request, and its marker still seen', () => {
    const p = page();
    p.run(AR_WRAP, '/AccessRoles/ClientScript');
    assert.equal(p.context.fetch.__jfarKept, true);
    p.run(AR_WRAP, '/AccessRoles/ClientScript');
    exercise(p);
    assert.deepEqual(p.list(), EXPECTED);
    assert.equal(p.sent.length, 4, 'each request sent once: Access Roles wrapped once');
});

test('two wrappers on top (Access Roles and another plugin\'s): still direct', () => {
    const p = page();
    p.run(AR_WRAP, '/AccessRoles/ClientScript');
    p.run('(function () { const f = window.fetch; window.fetch = function (u, i) { return f(u, i); }; })();', '/JellyfinEnhanced/script');
    exercise(p);
    assert.deepEqual(p.list(), EXPECTED);
    p.context.__bookmarksRequests.reset();
    assert.deepEqual(p.list(), []);
});
