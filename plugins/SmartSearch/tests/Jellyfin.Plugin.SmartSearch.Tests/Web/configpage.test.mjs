// Smart Search's settings page in the kit's fake page, built from the real markup of Configuration/configPage.html, with
// its inline script: Export (GET /SmartSearch/Admin/Export, saved under the server's file name), Import (a file, a dry run
// with its report: the counts, the owners, the notes and the auto-collections it would break; then Import with the same
// options, forced only when the admin says so; a refusal's reason) and the snapshots (listed, a restore checked first,
// then restored). Every value the server sends is text. SettingsTests runs it:
//   node --test plugins/SmartSearch/tests/Jellyfin.Plugin.SmartSearch.Tests/Web/configpage.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage } from '../../../../../kit/web/tests/fake-dom.mjs';

const html = fs.readFileSync(new URL('../../../Jellyfin.Plugin.SmartSearch/Configuration/configPage.html', import.meta.url), 'utf8');
const script = /<script type="text\/javascript">([\s\S]*?)<\/script>/.exec(html)[1];
const VOID = new Set(['input', 'br', 'img', 'meta', 'link', 'hr']);

function decode(text) {
    return text.replace(/&(amp|lt|gt|quot|#39|nbsp);/g, (m, e) => ({ amp: '&', lt: '<', gt: '>', quot: '"', '#39': "'", nbsp: ' ' })[e]);
}

// The page element (#SmartSearchConfigPage) from the markup, without its style and script elements and comments.
function build(page) {
    const start = html.indexOf('<div id="SmartSearchConfigPage"');
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

const EVIL = '<img src=x onerror="window.__pwned=1">‮owner';

function snapshots(list) {
    return { Store: { Status: 'Ok', ReadOnly: false }, Snapshots: list };
}

// A response as ApiClient.ajax rejects with: status and a JSON body.
function refusal(status, body) {
    return { status, json: () => Promise.resolve(body) };
}

async function open(answer, options = {}) {
    const page = createPage();
    const root = build(page);
    page.document.body.append(root);
    page.context.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {} };
    const requests = [];
    let snapshotList = options.snapshots || snapshots([]);
    page.context.ApiClient = {
        getUrl: (path) => 'http://127.0.0.1:8112/' + path,
        // The settings form isn't under test here (the fake page has no select options): its read never ends.
        getPluginConfiguration: () => new Promise(() => {}),
        getJSON: (url) => {
            requests.push({ type: 'GET', url });
            if (String(url).endsWith('/SmartSearch/Admin/Snapshots')) {
                return Promise.resolve(JSON.parse(JSON.stringify(snapshotList)));
            }

            return Promise.resolve({});
        },
        ajax: (request) => {
            requests.push(request);
            return answer(request);
        }
    };
    const urls = [];
    page.context.URL = { createObjectURL: (blob) => {
        urls.push(blob);
        return 'blob:1';
    }, revokeObjectURL() {} };
    page.run(script, 'configPage.html');
    page.dispatch(root, 'pageshow');
    await page.flush();
    await page.flush();
    const q = (selector) => root.querySelector(selector);
    return {
        page,
        root,
        q,
        requests,
        urls,
        posts: () => requests.filter((r) => r.type === 'POST'),
        setSnapshots: (next) => {
            snapshotList = next;
        },
        settle: async () => {
            for (let i = 0; i < 6; i++) {
                await page.flush();
            }
        }
    };
}

function chooseFile(view, text) {
    const input = view.q('#SmartSearchImportFile');
    input.files = [{ name: 'export.json', text: () => Promise.resolve(text) }];
    view.page.dispatch(input, 'change');
}

const REPORT = {
    DryRun: true, Mode: 'Merge', MapUsersBy: 'Id', SourcePluginVersion: '0.6.12.0', ExportedUtc: '2026-11-10T11:43:27Z', SchemaVersion: 1,
    Users: { Mapped: [{ Id: 'a1', NewId: 'a1', Name: EVIL, Records: 2 }], Unknown: [{ Id: 'b2', Name: 'gone', Records: 1, Reason: 'No user of this id.' }] },
    Records: { Incoming: 3, Added: 1, Updated: 1, Unchanged: 0, KeptNewer: 0, Removed: 0, SkippedUnknownUser: 1, Skipped: 0 },
    Notes: ['A query of <b>gone</b> was skipped.'],
    SafetySnapshot: null,
    BrokenAutoCollections: [{ Id: 'c1', Name: '<i>Horror</i> 80s', Messages: ['Its query would be removed.'] }]
};

test('Export saves the server\'s file under its name, with the session\'s authorization; a refusal says why', async () => {
    let fail = false;
    const view = await open((request) => {
        if (fail) {
            return Promise.reject(refusal(503, { title: 'Unavailable', detail: 'The saved queries <can\'t> be read.' }));
        }

        return Promise.resolve({
            headers: { get: (name) => name === 'Content-Disposition' ? 'attachment; filename=smartsearch-saved-queries-export-20261110.json; filename*=UTF-8\'\'smartsearch-saved-queries-export-20261110.json' : null },
            blob: () => Promise.resolve({ size: 10 })
        });
    });
    view.q('#SmartSearchExport').click();
    await view.settle();
    const get = view.requests.find((r) => r.type === 'GET' && String(r.url).endsWith('/SmartSearch/Admin/Export'));
    assert.ok(get, 'the export read through ApiClient');
    assert.equal(view.urls.length, 1);
    assert.match(view.q('#SmartSearchExportStatus').textContent, /^Exported as smartsearch-saved-queries-export-20261110\.json\./);
    fail = true;
    view.q('#SmartSearchExport').click();
    await view.settle();
    assert.equal(view.q('#SmartSearchExportStatus').textContent, 'The saved queries <can\'t> be read.');
    assert.equal(view.q('#SmartSearchExportStatus [role="alert"]').children.length, 0, 'text, no elements');
});

test('Import: a dry run of the chosen file shows what would change, the owners, the notes and the auto-collections it would break, as text; Import then runs it, forced only when asked', async () => {
    const view = await open((request) => {
        const dry = /dryRun=true/.test(request.url);
        return Promise.resolve(Object.assign({}, REPORT, { DryRun: dry, SafetySnapshot: dry ? null : 'saved-queries-import-1.json' }));
    });
    assert.equal(view.q('#SmartSearchImportDryRun').disabled, true, 'no file yet');
    chooseFile(view, '{"Format":"jellyfin-plugin-export/1"}');
    assert.equal(view.q('#SmartSearchImportDryRun').disabled, false);
    assert.equal(view.q('#SmartSearchImportApply').disabled, true, 'a dry run first');
    view.q('#SmartSearchImportMode').value = 'Replace';
    view.q('#SmartSearchImportUsers').value = 'Name';
    view.q('#SmartSearchImportDryRun').click();
    await view.settle();
    const dry = view.posts()[0];
    assert.equal(dry.url, 'http://127.0.0.1:8112/SmartSearch/Admin/Import?mode=Replace&mapUsersBy=Name&dryRun=true');
    assert.equal(dry.data, '{"Format":"jellyfin-plugin-export/1"}');
    const report = view.q('#SmartSearchImportReport');
    assert.equal(report.querySelector('p').textContent, 'Dry run, nothing was changed. The file is an export of Smart Search 0.6.12.0 from '
        + new Date('2026-11-10T11:43:27Z').toLocaleString() + ', with 3 queries. The import would add 1, update 1, leave 0 unchanged, keep 0 newer here, remove 0, and skip 1 (1 of users not found here).');
    assert.deepEqual(report.querySelectorAll('td').map((td) => td.textContent), [EVIL, 'Found', '2', 'gone', 'Not found: No user of this id.', '1']);
    assert.equal(report.querySelectorAll('img, b, i').length, 0, 'markup stays text');
    assert.match(report.textContent, /A query of <b>gone<\/b> was skipped\./);
    assert.match(report.textContent, /1 auto-collection would lose the saved query it uses:/);
    assert.equal(report.querySelector('li bdi').textContent, '<i>Horror</i> 80s');
    assert.equal(report.querySelector('li .fieldDescription').textContent, 'Its query would be removed.');
    assert.equal(view.q('#SmartSearchImportApply').disabled, false);

    // Not forced: the server decides (it refuses while references would break).
    view.q('#SmartSearchImportApply').click();
    await view.settle();
    assert.equal(view.posts()[1].url, 'http://127.0.0.1:8112/SmartSearch/Admin/Import?mode=Replace&mapUsersBy=Name&dryRun=false');
    assert.match(view.q('#SmartSearchImportReport').textContent, /^Imported\./);
    assert.match(view.q('#SmartSearchImportReport').textContent, /saved-queries-import-1\.json/);
    assert.equal(view.q('#SmartSearchImportApply').disabled, true, 'done: a new dry run before another import');
    // Forced, once the admin ticks the box under the list.
    view.q('#SmartSearchImportDryRun').click();
    await view.settle();
    view.q('#SmartSearchImportReport .smartSearchForce').checked = true;
    view.q('#SmartSearchImportApply').click();
    await view.settle();
    assert.equal(view.posts()[3].url, 'http://127.0.0.1:8112/SmartSearch/Admin/Import?mode=Replace&mapUsersBy=Name&dryRun=false&force=true');
    // The forced run's report lists what lost its query, as the dry run listed it, without the box.
    const done = view.q('#SmartSearchImportReport');
    assert.match(done.textContent, /^Imported\./);
    assert.match(done.textContent, /1 auto-collection lost the saved query it uses:/);
    assert.equal(done.querySelector('li bdi').textContent, '<i>Horror</i> 80s');
    assert.equal(done.querySelector('.smartSearchForce'), null);
    // Another option needs another dry run.
    view.q('#SmartSearchImportDryRun').click();
    await view.settle();
    view.page.dispatch(view.q('#SmartSearchImportMode'), 'change');
    assert.equal(view.q('#SmartSearchImportApply').disabled, true);
    assert.equal(view.page.context.__pwned, undefined);
});

test('Import: an export with auto-collections reports them apart, with their notes and snapshot, as text', async () => {
    const view = await open((request) => Promise.resolve(Object.assign({}, REPORT, {
        DryRun: /dryRun=true/.test(request.url),
        AutoCollections: {
            Records: { Incoming: 2, Added: 1, Updated: 0, Unchanged: 1, KeptNewer: 0, Skipped: 0, SkippedUnknownUser: 0 },
            NotCreated: 1,
            Notes: ['1 auto-collections are new here and have no collection yet: <b>Create</b>.'],
            SafetySnapshot: /dryRun=true/.test(request.url) ? null : 'auto-collections-import-1.json'
        }
    })));
    chooseFile(view, '{"Format":"jellyfin-plugin-export/1"}');
    view.q('#SmartSearchImportDryRun').click();
    await view.settle();
    const report = view.q('#SmartSearchImportReport');
    assert.match(report.textContent, /Auto-collections in the file: 2\. The import would add 1 \(without collections\), update 0, leave 1 unchanged, keep 0 newer here, and skip 0\./);
    assert.match(report.textContent, /no collection yet: <b>Create<\/b>\./);
    assert.equal(report.querySelectorAll('b').length, 0, 'markup stays text');
    view.q('#SmartSearchImportApply').click();
    await view.settle();
    assert.match(view.q('#SmartSearchImportReport').textContent, /Added 1 \(without collections\)/);
    assert.match(view.q('#SmartSearchImportReport').textContent, /auto-collections-import-1\.json/);
});

test('an import the server refuses: its reason, and the auto-collections it names, as text', async () => {
    const view = await open(() => Promise.reject(refusal(409, { title: 'Conflict', detail: 'The import would break auto-collections.', code: 'BreaksAutoCollections', autoCollections: [{ id: 'c1', name: '<b>Kids</b>', messages: ['It would lose its query.'] }] })));
    chooseFile(view, '{}');
    view.q('#SmartSearchImportDryRun').click();
    await view.settle();
    const report = view.q('#SmartSearchImportReport');
    assert.equal(report.querySelector('[role="alert"]').textContent, 'The import would break auto-collections.');
    assert.equal(report.querySelector('li bdi').textContent, '<b>Kids</b>');
    assert.equal(report.querySelectorAll('b').length, 0);
});

test('the snapshots: listed newest first, a restore checked first (nothing changed), then restored from its report; the list read again', async () => {
    const list = snapshots([
        { Name: 'saved-queries-import-20261110-114327.json', Kind: 'PreOp', Operation: 'import', Utc: '2026-11-10T11:43:27Z', Size: 1584 },
        { Name: 'saved-queries-20261109.json', Kind: 'Daily', Operation: null, Utc: '2026-11-09T03:00:00Z', Size: 900 }
    ]);
    const view = await open((request) => Promise.resolve(/dryRun=true/.test(request.url)
        ? { DryRun: true, RestoredFrom: 'saved-queries-20261109.json', Queries: 4, BrokenAutoCollections: [] }
        : { DryRun: false, RestoredFrom: 'saved-queries-20261109.json', Queries: 4, BrokenAutoCollections: [] }), { snapshots: list });
    const rows = view.q('#SmartSearchSnapshots').querySelectorAll('tr');
    assert.equal(rows.length, 3);
    assert.deepEqual(rows[1].querySelectorAll('td').map((td) => td.textContent).slice(1), ['Before import', '2 KB', 'Check restore']);
    assert.deepEqual(rows[2].querySelectorAll('td').map((td) => td.textContent).slice(1, 2), ['Daily']);
    rows[2].querySelector('.smartSearchRestoreCheck').click();
    await view.settle();
    assert.equal(view.posts()[0].url, 'http://127.0.0.1:8112/SmartSearch/Admin/Snapshots/Restore?name=saved-queries-20261109.json&dryRun=true');
    const report = view.q('#SmartSearchRestoreReport');
    assert.equal(report.querySelector('p').textContent, 'Checked, nothing was changed. Restoring saved-queries-20261109.json would make the saved queries its 4 queries, after a snapshot of them as they are now.');
    const reads = view.requests.filter((r) => r.type === 'GET').length;
    report.querySelector('.smartSearchRestoreApply').click();
    await view.settle();
    assert.equal(view.posts()[1].url, 'http://127.0.0.1:8112/SmartSearch/Admin/Snapshots/Restore?name=saved-queries-20261109.json&dryRun=false');
    assert.equal(view.q('#SmartSearchRestoreReport').textContent, 'Restored saved-queries-20261109.json: 4 queries.');
    assert.equal(view.requests.filter((r) => r.type === 'GET').length, reads + 1, 'the list read again');

    const empty = await open(() => Promise.resolve({}));
    assert.equal(empty.q('#SmartSearchSnapshots').textContent, 'No snapshots yet.');
});

test('a restore refused for the auto-collections it breaks offers Restore beside the box; forced, its report lists them', async () => {
    const list = snapshots([{ Name: 'saved-queries-20261109.json', Kind: 'Daily', Operation: null, Utc: '2026-11-09T03:00:00Z', Size: 900 }]);
    const broken = [{ Id: 'c1', Name: '<b>Kids</b>', Messages: ['Its query would be removed.'] }];
    const view = await open((request) => {
        if (/dryRun=true/.test(request.url)) {
            return Promise.resolve({ DryRun: true, RestoredFrom: 'saved-queries-20261109.json', Queries: 2, BrokenAutoCollections: [] });
        }

        return /force=true/.test(request.url)
            ? Promise.resolve({ DryRun: false, RestoredFrom: 'saved-queries-20261109.json', Queries: 2, BrokenAutoCollections: broken })
            : Promise.reject(refusal(409, { title: 'Conflict', detail: 'This would remove saved queries that auto-collections use.', code: 'BreaksAutoCollections', autoCollections: [{ id: 'c1', name: '<b>Kids</b>', messages: ['Its query would be removed.'] }] }));
    }, { snapshots: list });
    view.q('#SmartSearchSnapshots .smartSearchRestoreCheck').click();
    await view.settle();
    // A reference came between the check and the restore: refused, with the box and Restore.
    view.q('#SmartSearchRestoreReport .smartSearchRestoreApply').click();
    await view.settle();
    const report = view.q('#SmartSearchRestoreReport');
    assert.equal(report.querySelector('[role="alert"]').textContent, 'This would remove saved queries that auto-collections use.');
    assert.equal(report.querySelector('li bdi').textContent, '<b>Kids</b>');
    assert.ok(report.querySelector('.smartSearchForce'), 'the box to go on anyway');
    report.querySelector('.smartSearchForce').checked = true;
    report.querySelector('.smartSearchRestoreApply').click();
    await view.settle();
    assert.equal(view.posts()[2].url, 'http://127.0.0.1:8112/SmartSearch/Admin/Snapshots/Restore?name=saved-queries-20261109.json&dryRun=false&force=true');
    const done = view.q('#SmartSearchRestoreReport');
    assert.match(done.textContent, /^Restored saved-queries-20261109\.json: 2 queries\./);
    assert.match(done.textContent, /1 auto-collection lost the saved query it uses:/);
    assert.equal(done.querySelector('.smartSearchForce'), null);
    assert.equal(done.querySelector('.smartSearchRestoreApply'), null);
    assert.equal(done.querySelectorAll('b').length, 0);
});
