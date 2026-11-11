// User Ratings' settings page in the kit's fake page, built from the real
// markup of Configuration/configPage.html, with its inline script: the warning banners above the tabs, from
// GET /UserRatings/Admin/Diagnostics, read on every visit: the ratings database's sentence when it isn't Ok, and the
// kit's Notice (a recovery that didn't finish left its files in a quarantine folder while the
// database in place is used as it is), each as text, on the Settings tab as well as on Diagnostics; the
// Ambiguous banner (ratings several copies match), ended by opening the Data tab (POST /UserRatings/Admin/Detached/Seen),
// and Data → Detached & ambiguous: the counts by reason and the ratings kept on items that are gone,
// with "not checked since the server started", the mass-detach note and Detach now (POST /UserRatings/Admin/Held/Detach,
// after a confirmation). The Moderation tab and the Settings tab: the AdminScript module, imported
// once for both (the Settings tab's mount when the page is shown, the Moderation tab's when it is first opened), each
// tab read again when it is opened again. WebTests runs it:
//   node --test plugins/UserRatings/tests/Jellyfin.Plugin.UserRatings.Tests/Web/configpage.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage } from '../../../../../kit/web/tests/fake-dom.mjs';

const html = fs.readFileSync(new URL('../../../Jellyfin.Plugin.UserRatings/Configuration/configPage.html', import.meta.url), 'utf8');
const script = /<script type="text\/javascript">([\s\S]*?)<\/script>/.exec(html)[1];
// The one import of the AdminScript module, which a test given a module (open's options.module) routes to it: the fake
// page runs the inline script without a module loader, where import() fails as a loader that can't reach the module would.
const ADMIN_IMPORT = "import(ApiClient.getUrl('UserRatings/AdminScript'))";

const VOID = new Set(['input', 'br', 'img', 'meta', 'link', 'hr']);

function decode(text) {
    return text.replace(/&(amp|lt|gt|quot|#39|nbsp);/g, (m, e) => ({ amp: '&', lt: '<', gt: '>', quot: '"', '#39': "'", nbsp: '\u00a0' })[e]);
}

// Builds the page element (#UserRatingsConfigPage) from the markup: elements, attributes and text, without the style and
// script elements (the script runs separately) and comments.
function build(page) {
    const start = html.indexOf('<div id="UserRatingsConfigPage"');
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

function diagnostics(store, lifecycle) {
    return {
        PluginVersion: '1.0.2.0', KitVersion: '1.0.0', Abi: { Target: '10.11', Server: '10.11.5', Active: true },
        Store: Object.assign({ File: 'userratings.db', Version: 2, Status: 'Ok', ReadOnly: false, DailySnapshots: 1, PreOpSnapshots: 0 }, store),
        Records: { Attached: 3, Detached: 0, Reviews: 1, Users: 2 },
        WebAnchors: { Reporting: true, Declared: ['detailPage'] },
        ClientHealth: [],
        Lifecycle: Object.assign({
            ReadOnly: false, StartupDone: true, Held: [], HeldCheckedUtc: '2026-10-31T08:00:00Z', Failures: 0,
            Detached: { Ambiguous: 0, Missing: 0, Removed: 0, Unmatched: 0 },
            Ambiguous: { Count: 0, SinceUtc: null, SeenUtc: null, Banner: false, Message: null }
        }, lifecycle)
    };
}

async function open(body, seen, options = {}) {
    const page = createPage();
    const root = build(page);
    page.document.body.append(root);
    const confirms = [];
    page.context.Dashboard = {
        showLoadingMsg() {},
        hideLoadingMsg() {},
        confirm(text, title, callback) {
            confirms.push({ text, title });
            callback(options.confirm !== false);
        }
    };
    let reads = 0;
    const posts = [];
    page.context.ApiClient = {
        ajax: (request) => {
            posts.push(request);
            return seen ? seen(request) : Promise.resolve();
        },
        getUrl: (path) => 'http://127.0.0.1:8108/' + path,
        getJSON: (url) => {
            if (String(url).endsWith('/UserRatings/Admin/Diagnostics')) {
                reads++;
                return Promise.resolve(JSON.parse(JSON.stringify(body)));
            }

            return Promise.reject(new Error(url));
        }
    };
    let source = script;
    const imports = [];
    if (options.module) {
        assert.equal(script.split(ADMIN_IMPORT).length, 2, 'the page imports the AdminScript module in one place');
        source = script.replace(ADMIN_IMPORT, "__importAdmin(ApiClient.getUrl('UserRatings/AdminScript'))");
        page.context.__importAdmin = (url) => {
            imports.push(url);
            return options.module(imports.length);
        };
    }

    page.run(source, 'configPage.html');
    page.dispatch(root, 'pageshow');
    await page.flush();
    await page.flush();
    const q = (selector) => page.document.querySelector(selector);
    return {
        page,
        root,
        q,
        get reads() {
            return reads;
        },
        posts,
        confirms,
        imports,
        banners: () => Array.from(q('#UserRatingsBanners').children),
        setBody: (next) => {
            body = next;
        },
        rerender: async () => {
            page.dispatch(root, 'pageshow');
            await page.flush();
            await page.flush();
        }
    };
}

const NOTICE = 'A recovery of the data didn\'t finish, and its files were left in the folder C:\\data\\quarantine-20261027-101500 next to the database. The database in place is used as it is; the folder\'s copy wasn\'t put back. Look at the folder, then delete or move the whole folder, never one file of it on its own. The server log has the details.';

test('the kit\'s Notice is a warning banner above the tabs, as text, on the Settings tab', async () => {
    // A recovery that didn't finish while the database in place is used as it is: Status is Ok, so only the Notice says
    // so; it is read on every visit, before the Diagnostics tab is opened.
    const view = await open(diagnostics({ Notice: NOTICE }));

    assert.equal(view.reads, 1);
    assert.equal(view.q('#UserRatingsTabSettings').getAttribute('aria-selected'), 'true');
    assert.ok(view.q('#UserRatingsDiagnostics').classList.contains('hide'));
    const banners = view.banners();
    assert.equal(banners.length, 1);
    assert.equal(banners[0].tagName, 'P');
    assert.equal(banners[0].className, 'userRatingsBanner');
    assert.equal(banners[0].getAttribute('role'), 'alert');
    assert.equal(banners[0].textContent, NOTICE);
    assert.equal(banners[0].children.length, 0);
    // The banners sit above the tab buttons, so they show on every tab.
    const content = view.q('.content-primary');
    const order = Array.from(content.children).map((n) => n.id);
    assert.ok(order.indexOf('UserRatingsBanners') < order.indexOf('UserRatingsTabs'), order.join(','));
    // The TV layout's Up and Down stay among the banners, the tabs and the tab shown, never the dashboard's drawer.
    assert.ok(content.classList.contains('focuscontainer-y'));
});

test('the Notice beside the database\'s own sentence: both banners, the state first, each as text; markup stays text', async () => {
    const view = await open(diagnostics({
        Status: 'ReadOnlyCorrupt', ReadOnly: true, Message: 'The ratings database is damaged, so User Ratings is read-only. <b>Restore</b> a snapshot.',
        Notice: 'The folder <img src=x onerror=alert(1)> holds the log files (-wal, -shm) without the database itself. Delete or move the whole folder.'
    }));

    const banners = view.banners();
    assert.deepEqual(banners.map((b) => b.textContent), [
        'The ratings database is damaged, so User Ratings is read-only. <b>Restore</b> a snapshot.',
        'The folder <img src=x onerror=alert(1)> holds the log files (-wal, -shm) without the database itself. Delete or move the whole folder.'
    ]);
    assert.ok(banners.every((b) => b.children.length === 0 && b.getAttribute('role') === 'alert'));
    assert.ok(view.q('#UserRatingsBanners img') === null);
});

test('no Notice, an Ok or Migrated database: no banner; a Notice gone on the next visit takes its banner with it', async () => {
    const view = await open(diagnostics({ Notice: NOTICE }));
    assert.equal(view.banners().length, 1);

    // The admin deleted the folder: Diagnostics no longer has a Notice, and the next visit shows no banner.
    view.setBody(diagnostics({ Notice: null }));
    await view.rerender();
    assert.equal(view.reads, 2);
    assert.equal(view.banners().length, 0);

    view.setBody(diagnostics({ Status: 'Migrated', Message: 'Upgraded to schema 1.' }));
    await view.rerender();
    assert.equal(view.banners().length, 0);

    // The Refresh button on the Diagnostics tab reads it again, with the banners.
    view.setBody(diagnostics({ Notice: NOTICE }));
    view.q('#UserRatingsTabDiagnostics').click();
    await view.page.flush();
    await view.page.flush();
    assert.deepEqual(view.banners().map((b) => b.textContent), [NOTICE]);
    view.setBody(diagnostics({}));
    view.q('#UserRatingsRefresh').click();
    await view.page.flush();
    await view.page.flush();
    assert.equal(view.banners().length, 0);
});

test('Jellyfin Enhanced\'s user reviews on: the kit\'s banner above the tabs, as text, and Diagnostics says they are visible; off: neither', async () => {
    const jeBanner = 'Jellyfin Enhanced\'s User Reviews is on. Import its data, then turn it off to avoid two review panels. <b>now</b>';
    const view = await open(Object.assign(diagnostics({}), { JellyfinEnhanced: { Installed: true, Version: '12.14.0.0', Overlap: { Feature: 'Reviews', On: true, Banner: jeBanner, Complete: false, Checklist: [] } } }));
    assert.deepEqual(view.banners().map((b) => b.textContent), [jeBanner]);
    assert.ok(view.q('#UserRatingsBanners b') === null, 'markup is text');
    view.q('#UserRatingsTabDiagnostics').click();
    await view.page.flush();
    await view.page.flush();
    assert.match(view.q('#UserRatingsDiagnosticsSummary').textContent, /Jellyfin Enhanced12\.14\.0\.0: its user reviews are on and visible beside those of User Ratings \(Data → Import from Jellyfin Enhanced has the switch-over checklist\)/);

    // Turned off in JE (or the banner hidden in Settings: the kit sends no Banner then).
    view.setBody(Object.assign(diagnostics({}), { JellyfinEnhanced: { Installed: true, Version: '12.14.0.0', Overlap: { Feature: 'Reviews', On: false, Banner: null, Complete: true, Checklist: [] } } }));
    view.q('#UserRatingsRefresh').click();
    await view.page.flush();
    await view.page.flush();
    assert.equal(view.banners().length, 0);
    assert.match(view.q('#UserRatingsDiagnosticsSummary').textContent, /Jellyfin Enhanced12\.14\.0\.0: its user reviews are off/);
    view.setBody(diagnostics({}));
    view.q('#UserRatingsRefresh').click();
    await view.page.flush();
    await view.page.flush();
    assert.match(view.q('#UserRatingsDiagnosticsSummary').textContent, /Jellyfin EnhancedNot installed/);
});

test('Diagnostics counts the detached Jellyfin Enhanced reviews still under a 0.3.21 season or episode key, 0 expected', async () => {
    const view = await open(diagnostics({}));
    assert.ok(view.q('#UserRatingsBuildNote') === null, '1.0 is no development build');
    assert.equal(view.root.textContent.indexOf('Development build'), -1);
    view.q('#UserRatingsTabDiagnostics').click();
    await view.page.flush();
    await view.page.flush();
    assert.match(view.q('#UserRatingsDiagnosticsSummary').textContent, /Jellyfin Enhanced keys from 0\.3\.210 detached season or episode reviews still under the key 0\.3\.21 kept \(0 expected\)/);
    view.setBody(Object.assign(diagnostics({}), { Records: { Attached: 3, Detached: 2, Reviews: 1, Users: 2, DetachedJeWithKeySuffix: 2 } }));
    view.q('#UserRatingsRefresh').click();
    await view.page.flush();
    await view.page.flush();
    assert.match(view.q('#UserRatingsDiagnosticsSummary').textContent, /Jellyfin Enhanced keys from 0\.3\.212 detached season or episode reviews/);
});

const BANNER = '2 ratings could not be relinked automatically because several copies match or another title is at their place. Review them in Data → Detached & ambiguous.';

function ambiguousBody(banner) {
    return diagnostics({}, {
        Detached: { Ambiguous: 2, Missing: 1, Removed: 3, Unmatched: 0 },
        Ambiguous: { Count: 2, SinceUtc: '2026-10-31T10:00:00Z', SeenUtc: banner ? null : '2026-10-31T10:05:00Z', Banner: banner, Message: banner ? BANNER : null }
    });
}

async function settle(view) {
    for (let i = 0; i < 4; i++) {
        await view.page.flush();
    }
}

test('the Ambiguous banner above the tabs, as text; opening the Data tab marks it seen once and reads again', async () => {
    const view = await open(ambiguousBody(true), () => {
        view.setBody(ambiguousBody(false));
        return Promise.resolve();
    });
    assert.deepEqual(view.banners().map((b) => b.textContent), [BANNER]);
    assert.equal(view.posts.length, 0);

    // Another tab ends nothing.
    view.q('#UserRatingsTabDiagnostics').click();
    await settle(view);
    assert.equal(view.posts.length, 0);
    assert.deepEqual(view.banners().map((b) => b.textContent), [BANNER]);

    view.q('#UserRatingsTabData').click();
    await settle(view);
    assert.equal(view.posts.length, 1);
    assert.equal(view.posts[0].type, 'POST');
    assert.equal(view.posts[0].url, 'http://127.0.0.1:8108/UserRatings/Admin/Detached/Seen');
    assert.equal(view.banners().length, 0);
    assert.ok(!view.q('#UserRatingsData').classList.contains('hide'));

    // Opened again with nothing new: no POST.
    view.q('#UserRatingsTabSettings').click();
    view.q('#UserRatingsTabData').click();
    await settle(view);
    assert.equal(view.posts.length, 1);
});

test('a failed "seen" keeps the banner; leaving the Data tab before Diagnostics answers posts nothing', async () => {
    const view = await open(ambiguousBody(true), () => Promise.reject(new Error('503')));
    view.q('#UserRatingsTabData').click();
    await settle(view);
    assert.equal(view.posts.length, 1);
    assert.deepEqual(view.banners().map((b) => b.textContent), [BANNER]);

    const other = await open(ambiguousBody(true));
    other.q('#UserRatingsTabData').click();
    other.q('#UserRatingsTabSettings').click();
    await settle(other);
    assert.equal(other.posts.length, 0);
});

test('Data → Detached & ambiguous counts the detached ratings by reason and lists the ratings kept on items that are gone, ids only, as text', async () => {
    const view = await open(diagnostics({}, {
        Detached: { Ambiguous: 2, Missing: 1, Removed: 3, Unmatched: 0 },
        Held: [{ ItemId: '4b0c6a52-0000-4000-8000-00000000000a', Reason: 'LibraryFolderUnavailable', SinceUtc: '2026-10-31T09:00:00Z', RatingIds: ['<b>r1</b>', 'r2'] },
            { ItemId: '4b0c6a52-0000-4000-8000-00000000000b', Reason: 'StillOnDisk', SinceUtc: '2026-10-31T09:30:00Z', RatingIds: ['r3'] }]
    }));
    const box = view.q('#UserRatingsDetached');
    const rows = Array.from(box.querySelectorAll('tr')).map((tr) => Array.from(tr.children).map((c) => c.textContent));
    assert.deepEqual(rows.slice(0, 5), [
        ['Removed from the library', '3'],
        ['Missing from the library', '1'],
        ['Not matched', '0'],
        ['Ambiguous: several copies match, the member rated the copy, or another title is at their place', '2'],
        ['Detached in all', '6']
    ]);
    assert.equal(rows[5][0], 'Item 4b0c6a52-0000-4000-8000-00000000000a');
    assert.match(rows[5][1], /^its library folder is missing, empty or can't be read; since .+; 2 ratings: <b>r1<\/b>, r2$/);
    assert.match(rows[6][1], /^its file is still on disk; since .+; 1 rating: r3$/);
    assert.ok(box.querySelector('b') === null);
    assert.equal(view.banners().length, 0);

    // Nothing kept: the sentence on unmounted drives. Read-only: no counts.
    view.setBody(diagnostics({}, {}));
    await view.rerender();
    assert.match(view.q('#UserRatingsDetached').textContent, /None \(checked .+\)\. Ratings stay on an item that is gone while its library folder is missing/);
    view.setBody(diagnostics({}, { ReadOnly: true, Detached: undefined, Ambiguous: undefined }));
    await view.rerender();
    assert.equal(view.q('#UserRatingsDetached').textContent, 'The ratings database is read-only, so nothing is detached or relinked now.');
});

// ------------------------------------------------------------------------------------------------------ Detach now

const HELD = [
    { ItemId: '4b0c6a52-0000-4000-8000-00000000000a', Reason: 'LibraryFolderUnavailable', SinceUtc: '2026-10-31T09:00:00Z', RatingIds: ['r1', 'r2'], MassDetach: true },
    { ItemId: '4b0c6a52-0000-4000-8000-00000000000b', Reason: 'StillOnDisk', SinceUtc: '2026-10-31T09:30:00Z', RatingIds: ['r3'], MassDetach: false }
];

function heldButtons(view) {
    return Array.from(view.q('#UserRatingsDetached').querySelectorAll('button'));
}

test('before a verify sweep has looked since the start, the list says it is not checked, never "None"', async () => {
    const view = await open(diagnostics({}, { HeldCheckedUtc: null }));
    const text = view.q('#UserRatingsDetached').textContent;
    assert.match(text, /Not checked since the server started: the verify sweep that runs after the start fills this list\./);
    assert.doesNotMatch(text, /None/);
    assert.equal(heldButtons(view).length, 0);

    // Held items listed while unchecked (a sweep that skipped this owner since) keep their buttons.
    view.setBody(diagnostics({}, { HeldCheckedUtc: null, Held: HELD }));
    await view.rerender();
    assert.match(view.q('#UserRatingsDetached').textContent, /Not checked since the server started/);
    assert.deepEqual(heldButtons(view).map((b) => b.textContent), ['Detach now', 'Detach now', 'Detach all listed']);
});

test('each kept item has Detach now, the list Detach all listed, and the mass-detach note; one item has no bulk button', async () => {
    const view = await open(diagnostics({}, { Held: HELD }));
    const rows = Array.from(view.q('#UserRatingsDetached').querySelectorAll('tr')).slice(5).map((tr) => Array.from(tr.children).map((c) => c.textContent));
    assert.equal(rows.length, 2);
    assert.match(rows[0][1], /^its library folder is missing, empty or can't be read \(every rated item of its library folder went at once\); since .+; 2 ratings: r1, r2$/);
    assert.match(rows[1][1], /^its file is still on disk; since .+; 1 rating: r3$/);
    assert.deepEqual(rows.map((r) => r[2]), ['Detach now', 'Detach now']);
    assert.deepEqual(heldButtons(view).map((b) => b.textContent), ['Detach now', 'Detach now', 'Detach all listed']);
    assert.match(view.q('#UserRatingsDetached').textContent, /They stay on their item until it is back, for as long as it takes \(checked .+\)\. If an item is gone for good, Detach now/);

    view.setBody(diagnostics({}, { Held: [HELD[1]] }));
    await view.rerender();
    assert.deepEqual(heldButtons(view).map((b) => b.textContent), ['Detach now']);
});

test('Detach now asks first, posts the item\'s id, reads Diagnostics again and says what it did; Cancel sends nothing', async () => {
    const view = await open(diagnostics({}, { Held: HELD }), (request) => {
        if (String(request.url).endsWith('/UserRatings/Admin/Held/Detach')) {
            view.setBody(diagnostics({}, { Held: [HELD[1]] }));
            return Promise.resolve({ Items: 1, Ratings: 2, NotHeld: 0 });
        }

        return Promise.resolve();
    });
    const reads = view.reads;
    heldButtons(view)[0].click();
    await settle(view);

    assert.equal(view.confirms.length, 1);
    assert.equal(view.confirms[0].title, 'Detach now');
    assert.equal(view.confirms[0].text, '2 ratings kept on 1 item that is gone will be detached as missing. Do this only if the item is gone for good; a rating whose item returns goes back to it by itself.');
    assert.equal(view.posts.length, 1);
    const post = view.posts[0];
    assert.deepEqual([post.type, post.url, post.contentType, post.dataType], ['POST', 'http://127.0.0.1:8108/UserRatings/Admin/Held/Detach', 'application/json', 'json']);
    assert.deepEqual(JSON.parse(post.data), { ItemIds: ['4b0c6a52-0000-4000-8000-00000000000a'] });
    assert.equal(view.reads, reads + 1);
    assert.match(view.q('#UserRatingsDetached').textContent, /2 ratings detached\.$/);
    assert.deepEqual(heldButtons(view).map((b) => b.textContent), ['Detach now']);

    // Cancelled: nothing sent.
    const cancelled = await open(diagnostics({}, { Held: HELD }), undefined, { confirm: false });
    heldButtons(cancelled)[2].click();
    await settle(cancelled);
    assert.equal(cancelled.confirms.length, 1);
    assert.equal(cancelled.confirms[0].text, '3 ratings kept on 2 items that are gone will be detached as missing. Do this only if the item is gone for good; a rating whose item returns goes back to it by itself.');
    assert.equal(cancelled.posts.length, 0);
});

test('Detach all listed posts every listed id; an item back since is counted as no longer kept', async () => {
    const view = await open(diagnostics({}, { Held: HELD }), () => {
        view.setBody(diagnostics({}, {}));
        return Promise.resolve({ Items: 1, Ratings: 2, NotHeld: 1 });
    });
    heldButtons(view)[2].click();
    await settle(view);
    assert.deepEqual(JSON.parse(view.posts[0].data), { ItemIds: HELD.map((h) => h.ItemId) });
    assert.match(view.q('#UserRatingsDetached').textContent, /2 ratings detached; 1 item was no longer kept \(back in the library, or already detached\)\.$/);
});

test('a refused Detach now says why, as text, in a banner under the list, and its button works again', async () => {
    const view = await open(diagnostics({}, { Held: HELD }), () => Promise.reject({ status: 503, json: () => Promise.resolve({ status: 503, detail: 'The ratings database is <b>read-only</b>.' }) }));
    const button = heldButtons(view)[1];
    button.click();
    await settle(view);
    const notes = Array.from(view.q('#UserRatingsHeldStatus').children);
    assert.equal(notes.length, 1);
    assert.equal(notes[0].textContent, 'Nothing was detached: The ratings database is <b>read-only</b>.');
    assert.equal(notes[0].className, 'userRatingsBanner');
    assert.ok(view.q('#UserRatingsDetached').querySelector('b') === null);
    assert.equal(button.disabled, false);

    // Without a ProblemDetails: the fallback sentence.
    const other = await open(diagnostics({}, { Held: HELD }), () => Promise.reject(new Error('network')));
    heldButtons(other)[0].click();
    await settle(other);
    assert.equal(other.q('#UserRatingsHeldStatus').textContent, 'Nothing was detached: the server refused it. The server log has the details.');
});

// ------------------------------------------------------------------------------------------------- parts of 1,000

function manyHeld(count) {
    return Array.from({ length: count }, (_, n) => ({
        ItemId: '4b0c6a52-0000-4000-8000-' + n.toString(16).padStart(12, '0'),
        Reason: 'LibraryFolderUnavailable',
        SinceUtc: '2026-10-31T09:00:00Z',
        RatingIds: ['r' + n],
        MassDetach: true
    }));
}

test('Detach all listed sends 2,500 kept items in parts of 1,000, one after another, and says what they did in all', async () => {
    const kept = manyHeld(2500);
    let release;
    const first = new Promise((resolve) => {
        release = resolve;
    });
    const view = await open(diagnostics({}, { Held: kept }), (request) => {
        const ids = JSON.parse(request.data).ItemIds;
        if (view.posts.length === 1) {
            return first.then(() => ({ Items: ids.length, Ratings: ids.length, NotHeld: 0 }));
        }

        if (view.posts.length === 3) {
            view.setBody(diagnostics({}, {}));
            return Promise.resolve({ Items: ids.length - 1, Ratings: ids.length - 1, NotHeld: 1 });
        }

        return Promise.resolve({ Items: ids.length, Ratings: ids.length, NotHeld: 0 });
    });
    const reads = view.reads;
    const bulk = heldButtons(view).at(-1);
    assert.equal(bulk.textContent, 'Detach all listed');
    bulk.click();
    await settle(view);

    assert.equal(view.confirms[0].text, '2500 ratings kept on 2500 items that are gone will be detached as missing. Do this only if the item is gone for good; a rating whose item returns goes back to it by itself.');
    assert.equal(view.posts.length, 1, 'the next part waits for the answer to the one before');
    release();
    await settle(view);

    const parts = view.posts.map((p) => JSON.parse(p.data).ItemIds);
    assert.deepEqual(parts.map((p) => p.length), [1000, 1000, 500]);
    assert.deepEqual(parts.flat(), kept.map((h) => h.ItemId));
    assert.equal(view.reads, reads + 1);
    assert.match(view.q('#UserRatingsDetached').textContent, /2499 ratings detached; 1 item was no longer kept \(back in the library, or already detached\)\.$/);
});

test('a part refused after others were detached says both, as text, in a banner, and sends nothing more', async () => {
    const view = await open(diagnostics({}, { Held: manyHeld(2500) }), () => {
        if (view.posts.length === 2) {
            return Promise.reject({ status: 503, json: () => Promise.resolve({ status: 503, detail: 'The ratings database is <b>read-only</b>.' }) });
        }

        return Promise.resolve({ Items: 1000, Ratings: 1000, NotHeld: 0 });
    });
    const reads = view.reads;
    heldButtons(view).at(-1).click();
    await settle(view);

    assert.equal(view.posts.length, 2);
    assert.equal(view.reads, reads + 1);
    const notes = Array.from(view.q('#UserRatingsHeldStatus').children);
    assert.equal(notes.length, 1);
    assert.equal(notes[0].textContent, '1000 ratings detached. The rest was not detached: The ratings database is <b>read-only</b>.');
    assert.equal(notes[0].className, 'userRatingsBanner');
    assert.ok(view.q('#UserRatingsDetached').querySelector('b') === null);
});

test('the Settings and Moderation tabs import the AdminScript module (in a page that cannot import, each says so as text, and the next opening tries again)', async () => {
    const view = await open(diagnostics({}));
    const root = view.q('#UserRatingsModerationRoot');
    assert.equal(root.textContent.trim(), 'Loading the ratings…');
    // The Settings tab, shown with the page, tried at once.
    await settle(view);
    const settings = view.q('#UserRatingsSettingsRoot');
    assert.ok(!view.q('#UserRatingsSettings').classList.contains('hide'));
    assert.equal(settings.textContent, 'The Settings tab couldn\'t be loaded. The server log has the details.');
    assert.equal(settings.children.length, 1);
    view.q('#UserRatingsTabModeration').click();
    await settle(view);
    assert.ok(!view.q('#UserRatingsModeration').classList.contains('hide'));
    assert.equal(view.q('#UserRatingsTabModeration').getAttribute('aria-selected'), 'true');
    // The fake page runs the inline script without a module loader, so import() fails as a loader that can't reach the
    // module would.
    assert.equal(root.textContent, 'The Moderation tab couldn\'t be loaded. The server log has the details.');
    assert.equal(root.children.length, 1);
    assert.ok(view.q('#UserRatingsSettings').classList.contains('hide'));
    view.q('#UserRatingsTabSettings').click();
    view.q('#UserRatingsTabModeration').click();
    await settle(view);
    assert.equal(root.children.length, 1, 'tried again, said once');
    assert.equal(settings.children.length, 1, 'tried again, said once');
    assert.equal(view.page.errors.length, 0);
});

test('one import for both tabs; the Settings tab mounted when the page is shown, the Moderation tab when first opened; each read again when opened again', async () => {
    const mounts = [];
    const refreshes = [];
    const module = {
        mount(view, params) {
            mounts.push({ view: view.id, tab: params.tab, page: params.page && params.page.id });
            view.replaceChildren();
            return { refresh: () => refreshes.push(params.tab) };
        }
    };
    const view = await open(diagnostics({}), null, { module: () => Promise.resolve(module) });
    await settle(view);

    assert.deepEqual(view.imports, ['http://127.0.0.1:8108/UserRatings/AdminScript']);
    assert.deepEqual(mounts, [{ view: 'UserRatingsSettingsRoot', tab: 'Settings', page: 'UserRatingsConfigPage' }]);
    assert.equal(view.q('#UserRatingsTabSettings').getAttribute('aria-controls'), 'UserRatingsSettings');

    view.q('#UserRatingsTabModeration').click();
    await settle(view);
    assert.equal(view.imports.length, 1, 'the module is imported once');
    assert.deepEqual(mounts.map((m) => m.tab + ' ' + m.view), ['Settings UserRatingsSettingsRoot', 'Moderation UserRatingsModerationRoot']);
    assert.deepEqual(refreshes, []);

    view.q('#UserRatingsTabSettings').click();
    view.q('#UserRatingsTabModeration').click();
    await settle(view);
    assert.deepEqual(refreshes, ['Settings', 'Moderation']);
    // Shown again (jellyfin-web keeps the page): the Settings tab again, read again, not mounted again.
    await view.rerender();
    assert.deepEqual(refreshes, ['Settings', 'Moderation', 'Settings']);
    assert.equal(mounts.length, 2);
    assert.ok(!view.q('#UserRatingsSettings').classList.contains('hide'));
    assert.equal(view.page.errors.length, 0);
});

test('an import that fails is tried again by the next opening, and its module then mounts the tab', async () => {
    const mounts = [];
    const view = await open(diagnostics({}), null, {
        module: (n) => n === 1 ? Promise.reject(new Error('unreachable')) : Promise.resolve({
            mount(root, params) {
                mounts.push(params.tab);
                root.replaceChildren();
                return { refresh() {} };
            }
        })
    });
    await settle(view);
    assert.equal(view.q('#UserRatingsSettingsRoot').textContent, 'The Settings tab couldn\'t be loaded. The server log has the details.');

    view.q('#UserRatingsTabModeration').click();
    await settle(view);
    assert.equal(view.imports.length, 2);
    assert.deepEqual(mounts, ['Moderation']);
    view.q('#UserRatingsTabSettings').click();
    await settle(view);
    assert.equal(view.imports.length, 2, 'the module once loaded is kept');
    assert.deepEqual(mounts, ['Moderation', 'Settings']);
    assert.equal(view.q('#UserRatingsSettingsRoot').textContent, '');
    assert.equal(view.page.errors.length, 0);
});

test('the Data tab mounts its tools and its detached list (under the counts) from the one import, each read again when opened again; a change on it reads Diagnostics again', async () => {
    const mounts = [];
    const refreshes = [];
    const module = {
        mount(view, params) {
            mounts.push(params.tab + ' ' + view.id);
            view.replaceChildren();
            return { refresh: () => refreshes.push(params.tab) };
        }
    };
    const view = await open(diagnostics({}), null, { module: () => Promise.resolve(module) });
    await settle(view);
    view.q('#UserRatingsTabData').click();
    await settle(view);
    assert.equal(view.imports.length, 1, 'the module is imported once');
    assert.deepEqual(mounts, ['Settings UserRatingsSettingsRoot', 'Data UserRatingsDataRoot', 'Detached UserRatingsDetachedListRoot']);
    assert.ok(view.q('#UserRatingsDetached').nextElementSibling === view.q('#UserRatingsDetachedListRoot'), 'the list under the counts');
    assert.equal(view.q('#UserRatingsData').textContent.indexOf('later development build'), -1, 'the placeholder is gone');

    view.q('#UserRatingsTabSettings').click();
    view.q('#UserRatingsTabData').click();
    await settle(view);
    assert.deepEqual(refreshes, ['Settings', 'Data', 'Detached']);
    assert.equal(mounts.length, 3);

    const reads = view.reads;
    view.page.dispatch(view.q('#UserRatingsDataRoot'), 'jfur-data-changed');
    await settle(view);
    assert.equal(view.reads, reads + 1, 'Diagnostics read again');
    assert.equal(view.page.errors.length, 0);
});

test('a Data tab whose module can\'t be loaded says so in both places, as text', async () => {
    const view = await open(diagnostics({}));
    await settle(view);
    view.q('#UserRatingsTabData').click();
    await settle(view);
    assert.equal(view.q('#UserRatingsDataRoot').textContent, 'The Data tab couldn\'t be loaded. The server log has the details.');
    assert.equal(view.q('#UserRatingsDetachedListRoot').textContent, 'The detached list couldn\'t be loaded. The server log has the details.');
    assert.equal(view.page.errors.length, 0);
});
