// Generates User Ratings' Data tab webchecks over
// probes/userratings-details-probe.js (its request log and its server(), a request of the signed-in user's own):
//   userratings-data-desktop.json      default, admin with viewer, desktop: the settings page's Data tab read with GETs
//                                      only (and the page's own Detached/Seen), no select; Export as a job: its file
//                                      fetched with the session's authorization (no token in a link) and handed to a
//                                      download link, which the run keeps in the page (a[download] clicks prevented, so
//                                      no file is written): the kit's export envelope with viewer's row; Download again gives the
//                                      same file; that file put in the file field: a Merge dry run first (Import off
//                                      until then), the file sent as it is; viewer changes their review: Import's
//                                      question, then the 409 Changed in the tab's words and nothing imported; the dry
//                                      run again and Import: the import's snapshot named; viewer changes their review
//                                      again; Restore… of the import's snapshot: the question, the review as it was
//                                      before the import, the restore's own snapshot listed
//   userratings-data-tv.json           default, admin, TV layout: no select; OK picks Replace and Merge again; the D-pad
//                                      walks the tab, never into the drawer; OK on Restore… opens the question with the focus
//                                      on Cancel, Down and Up stay inside it, Back (461) closes it with the focus back on
//                                      Restore…, OK on Cancel too; OK on a reason of the detached list filters it;
//                                      nothing restored
//   userratings-data-phone.json        default, admin, 375x812 with touch: a dry run's report shown; no sideways scroll;
//                                      every control of the tab and of the detached list at least 44 px high; Restore…'s
//                                      question inside the screen with 44 px controls; Cancel restores nothing
//   userratings-detached-desktop.json  default, admin with viewer, desktop: three detached ratings made by importing a
//                                      synthetic export (viewer's with a review, two of viewer2's) on titles the library
//                                      lacks, named as Long Test Movie; the detached list shows them, oldest first, each
//                                      as text, with Long Test Movie to go back to; a reason filters the list; Relink
//                                      gives viewer's back to Long Test Movie (viewer sees it); one purged meanwhile
//                                      through the API: its Relink is the 404 note, kept while the list is read again;
//                                      Purge… deletes the last for good, and a dry run of the same file then skips both
//                                      purged ratings as purged before (PreviouslyPurged; a deletion never comes back)
// The TV and phone checks change nothing (they compare the snapshots at the end); the desktop ones end with every rating
// on Long Test Movie removed through the moderation API and none of their ratings left detached.
// Run from the repository root: node scripts/webchecks/gen-data.mjs [--out <folder>]
// Repo.Tests compares the files with what this writes. scripts/README.md says how to run them.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { VERSION } from './userratings-version.mjs';

const outIndex = process.argv.indexOf('--out');
const folder = outIndex > 0 ? path.resolve(process.argv[outIndex + 1]) : path.dirname(fileURLToPath(import.meta.url));
const IGNORE = '/CustomRatings/script|^Error: player cannot be null';
const P = '__jfurDetails';
const ROOT = '#UserRatingsDataRoot';
const DET = '#UserRatingsDetachedListRoot';
const ADMIN = 'UserRatings/Admin';
const MOVIE = (name) => `(await ApiClient.getItems(ApiClient.getCurrentUserId(), { IncludeItemTypes: 'Movie', Recursive: true, SearchTerm: ${JSON.stringify(name)}, Limit: 1 })).Items[0].Id`;
// The kit's dialog (its panel).
const DIALOG = `document.querySelector('.jfur-panel[role="dialog"][aria-modal="true"]')`;
const NOT_BODY = `!!document.activeElement && document.activeElement !== document.body && document.activeElement.isConnected`;
const NORM = "const norm = (v) => String(v === null || v === undefined ? '' : v).replace(/-/g, '').toLowerCase();";
const TEXT_V = 'data webcheck review: the lighthouse <b>stays plain</b> & lit.';
const TEXT_V2 = 'data webcheck review, changed after the dry run: the tide <i>turns</i>.';
const TEXT_V3 = 'data webcheck review, changed after the import: the gulls & the gale.';
const TEXT_D = 'data webcheck detached review: <b>kept</b> as text & whole.';
const TEXT_JE = 'jeexp webcheck review: "quoted", <b>plain</b> & whole.';
const CHANGED = 'The ratings changed after the dry run, so nothing was imported. Run the dry run again to see what Import does now.';
const NEED_DRY = 'Run the dry run of this file and these options first; Import then does what it showed.';
const CHOOSE = 'Choose an export file, then run the dry run.';
const GONE = 'This rating isn\'t detached any more, or you can\'t change it. The list is read again.';

function write(name, title, steps) {
    const doc = { name: title, failOnException: false, timeout: 30000, steps };
    fs.writeFileSync(path.join(folder, name), JSON.stringify(doc, null, 2) + '\n', { encoding: 'utf8' });
}

const json = (value) => JSON.stringify(value);

function signIn(user, layout, emulate) {
    const steps = [{ name: 'the details probe in every document (its request log and server())', initScript: { file: 'probes/userratings-details-probe.js' } }];
    if (emulate) {
        steps.push({ name: 'the screen, before signing in', emulate });
    }

    steps.push(
        { name: 'sign in as ' + user, login: user },
        { name: (layout || 'desktop') + ' layout', eval: !layout || layout === 'desktop' ? "(localStorage.removeItem('layout'), true)" : `(localStorage.setItem('layout', '${layout}'), true)` },
        { name: 'reload', reload: true },
        { name: `${user}: User Ratings ${VERSION} started`, waitFor: "!!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)", timeout: 40000 },
        { name: 'the served script is ' + VERSION, assert: `window.__jfkit.jfur.version === '${VERSION}'`, message: 'User Ratings ' + VERSION + ' is not the script the page loaded' }
    );
    return steps;
}

const ids = {
    name: 'the users\' ids',
    script: `${NORM} const u = await ApiClient.getUsers(); const id = (n) => { const x = u.find(y => y.Name === n); return x ? norm(x.Id) : null; }; const r = { admin: id('admin'), viewer: id('viewer'), viewer2: id('viewer2') }; if (!r.viewer || !r.viewer2) { throw new Error('the sandbox needs viewer and viewer2'); } return r;`,
    save: 'ids',
    full: true
};

// Every rating on the movie, through the admin's moderation API (its dry run first).
function sweep(label, idVar = 'movieId', title = 'Long Test Movie') {
    return {
        name: `clean: ${label}: every rating on ${title} removed through the moderation API`,
        script: `const page = await ${P}.server('GET', '${ADMIN}/Reviews?itemId=' + __vars.${idVar} + '&limit=200'); if (page.status !== 200) { throw new Error('list ' + page.status); } const removed = []; for (const row of page.json.Items) { const p = '${ADMIN}/Reviews/' + row.RatingId + '?textOnly=false'; const dry = await ${P}.server('DELETE', p); const done = await ${P}.server('DELETE', p + '&dryRun=false&ifPreview=' + dry.json.PreviewToken); removed.push(done.status); } return removed;`
    };
}

const noneLeftOf = (idVar, title) => ({
    name: `clean: ${title} has no rating left`,
    script: `const page = await ${P}.server('GET', '${ADMIN}/Reviews?itemId=' + __vars.${idVar}); if (page.status !== 200 || page.json.TotalRecordCount !== 0) { throw new Error('left: ' + page.status + ' ' + (page.json && page.json.TotalRecordCount)); } return true;`
});
const noneLeft = noneLeftOf('movieId', 'Long Test Movie');

// The admin, the movie, its ratings swept.
function adminStart(layout, emulate) {
    return [
        ...signIn('admin', layout, emulate),
        ids,
        { name: 'Long Test Movie', eval: MOVIE('Long Test Movie'), save: 'movieId' },
        sweep('before the run')
    ];
}

const snapshotNames = (label, save) => ({
    name: `${label}: the snapshots as the server holds them`,
    script: `const r = await ${P}.server('GET', '${ADMIN}/Snapshots'); if (r.status !== 200) { throw new Error('snapshots ' + r.status); } return r.json.Snapshots.map(s => s.Name).sort();`,
    save,
    full: true
});
const sameSnapshots = {
    name: 'clean: the snapshots as before the run: nothing restored or imported',
    script: `const r = await ${P}.server('GET', '${ADMIN}/Snapshots'); const now = r.json.Snapshots.map(s => s.Name).sort(); if (JSON.stringify(now) !== JSON.stringify(__vars.snapsBefore)) { throw new Error('the snapshots changed: ' + JSON.stringify(now)); } return now.length;`
};

// The Data tab mounted: the snapshots, the users (Diagnostics) and the jobs read, and the detached list.
const LOADED = `(() => { const r = ${P}.requests.slice(window.__mark); const got = (p) => r.some(x => x.method === 'GET' && x.path === p && x.status === 200); return !document.querySelector('#UserRatingsData').classList.contains('hide') && !!document.querySelector('${ROOT} .jfur-data-export') && !!document.querySelector('${DET} .jfur-det') && got('/UserRatings/Admin/Snapshots') && got('/UserRatings/Admin/Diagnostics') && got('/UserRatings/Admin/Jobs') && got('/UserRatings/Admin/Detached'); })()`;

function openTab() {
    return [
        { name: 'mark', script: `window.__mark = ${P}.requests.length; return true;` },
        { name: 'open User Ratings\' settings page', goto: '/web/index.html#/configurationpage?name=UserRatings' },
        { name: 'the page and its tabs', waitFor: "!!document.querySelector('#UserRatingsConfigPage:not(.hide) #UserRatingsTabs')", timeout: 30000 },
        { name: 'the Data tab', click: '#UserRatingsTabData' },
        { name: 'the Data tab mounted: the snapshots, the users and the jobs read, and the detached list', waitFor: LOADED, timeout: 30000 },
        { name: 'the time the lists take to draw', sleep: 500 },
        { name: 'its requests so far', eval: `${P}.requests.slice(window.__mark).map(r => r.method + ' ' + r.path + ' ' + r.status)`, save: 'firstRequests', full: true },
        { name: 'GETs only (and the page\'s own Detached/Seen): opening the tab writes nothing', assert: "__vars.firstRequests.every(r => r.indexOf('GET ') === 0 || r.indexOf('POST /UserRatings/Admin/Detached/Seen ') === 0)" },
        { name: 'no select in the Data tab or the detached list (the TV layout\'s D-pad and OK work on every control)', assert: `document.querySelectorAll('${ROOT} select, ${DET} select').length === 0` }
    ];
}

const mark = (label) => ({ name: `${label}: mark`, script: `window.__mark = ${P}.requests.length; return true;` });
const since = (label, save) => ({ name: `${label}: the tab's requests since the mark`, eval: `${P}.requests.slice(window.__mark).map(r => ({ method: r.method, path: r.path, query: r.query, status: r.status }))`, save, full: true });
const dialogOpen = (label) => ({ name: `${label}: the question, the focus on Cancel`, waitFor: `!!${DIALOG} && document.activeElement === ${DIALOG}.querySelector('.jfur-data-cancel')`, timeout: 15000 });
const dialogText = (label, save) => ({ name: `${label}: what the question says`, eval: `({ text: ${DIALOG}.textContent, markup: ${DIALOG}.querySelectorAll('b, i').length })`, save, full: true });
const confirm = (label) => ({ name: `${label}: the question's button`, click: '.jfur-panel[role="dialog"] .jfur-data-confirm' });
const toast = (text) => `Array.from(document.querySelectorAll('.jfur-toast')).some(t => t.textContent === ${json(text)})`;
const IMPORT_MESSAGE = `document.querySelector('${ROOT} .jfur-data-import-section .jfur-data-message')`;
const REPORT = `document.querySelector('${ROOT} .jfur-data-import-section .jfur-data-report')`;
const RESTORE_REPORT = `document.querySelector('${ROOT} .jfur-data-snapshots-section .jfur-data-report')`;
const BUTTON = (cls) => `document.querySelector('${ROOT} .${cls}')`;
const shown = (el) => `(${el}.getClientRects().length > 0)`;
// A job's final state, read from the server: the last job the tab polled since the mark.
const LAST_JOB = (save) => ({
    name: 'the job the tab followed, as the server holds it',
    script: `const polled = ${P}.requests.slice(window.__mark).map(r => r.path).filter(p => /^\\/UserRatings\\/Admin\\/Jobs\\/[0-9a-f-]+$/i.test(p)); if (polled.length === 0) { throw new Error('no job polled'); } const id = polled[polled.length - 1].split('/').pop(); const j = await ${P}.server('GET', '${ADMIN}/Jobs/' + id); return { status: j.status, kind: j.json.Kind, state: j.json.State, dryRun: j.json.DryRun, code: j.json.ErrorCode || null, errorStatus: j.json.ErrorStatus || null };`,
    save,
    full: true
});
// The file field given a file of the page's (a DataTransfer, as a chosen file is).
const PUT_FILE = (textExpr, nameExpr) => `const input = document.querySelector('${ROOT} .jfur-data-file'); const dt = new DataTransfer(); dt.items.add(new File([${textExpr}], ${nameExpr}, { type: 'application/json' })); input.files = dt.files; input.dispatchEvent(new Event('change', { bubbles: true })); return input.files.length;`;
const outcomes = `Array.from(${REPORT}.querySelectorAll('[data-jfur-outcome]')).map(li => ({ key: li.getAttribute('data-jfur-outcome'), text: li.textContent }))`;
const viewerReview = (label, save) => ({
    name: `${label}: viewer's row as the server holds it`,
    script: `const p = await ${P}.server('GET', '${ADMIN}/Reviews?itemId=' + __vars.movieId); const row = p.json.Items.find(x => x.UserName === 'viewer'); return row ? { stars: row.Stars, text: row.Text } : null;`,
    save,
    full: true
});

// ---------------------------------------------------------------------------------------------------------- desktop

// a[download] clicks kept in the page: the download prevented (no file is written), its name and the blob's text kept.
// The JE export's checks: the film's TMDb id as the library holds it, and the saved reviews.json read entry by entry.
const JE_TMDB = "const tmdb = async (id) => { const i = await ApiClient.getItem(ApiClient.getCurrentUserId(), id); const ids = i.ProviderIds || {}; const k = Object.keys(ids).find(x => x.toLowerCase() === 'tmdb'); const v = k ? String(ids[k]).trim() : ''; return /^[0-9]{1,9}$/.test(v) ? v : null; }; return { movie: await tmdb(__vars.movieId), tagged: await tmdb(__vars.taggedId) };";
const JE_FILE = `(() => { const d = window.__jfurDownloads[2]; let j = null; try { j = JSON.parse(d.text); } catch (e) { return { parsed: false }; } const reviews = j && j.Reviews && typeof j.Reviews === 'object' && !Array.isArray(j.Reviews) ? j.Reviews : null; if (!reviews) { return { parsed: true, reviews: false }; } const keys = Object.keys(reviews); const time = (v) => typeof v === 'string' && /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}[.][0-9]{7}Z$/.test(v); const bad = keys.filter(k => { const e = reviews[k]; const p = k.split(':'); return !/^[0-9a-f]{32}:(movie|tv):[0-9]{1,9}(:s[0-9]+(:e[0-9]+)?)?$/.test(k) || (p[1] === 'movie' && p.length !== 3) || Object.keys(e).join(',') !== 'UserId,TmdbId,MediaType,Content,Rating,CreatedAt,UpdatedAt' || e.UserId !== p[0] || e.MediaType !== p[1] || e.TmdbId !== p.slice(2).join(':') || typeof e.Content !== 'string' || !(e.Rating === null || (typeof e.Rating === 'number' && e.Rating >= 0.5 && e.Rating <= 5)) || !time(e.CreatedAt) || !time(e.UpdatedAt); }); const entry = (tmdb) => { const e = tmdb ? reviews[__vars.ids.viewer + ':movie:' + tmdb] : null; return e ? { rating: e.Rating, content: e.Content, tmdbId: e.TmdbId, type: e.MediaType } : null; }; return { parsed: true, reviews: true, name: d.name, scheme: d.scheme, top: Object.keys(j), count: keys.length, sorted: keys.every((k, i) => i === 0 || keys[i - 1] < k), bad: bad, tagged: entry(__vars.tmdb.tagged), movie: entry(__vars.tmdb.movie), status: ${BUTTON('jfur-data-je-export-status')}.textContent, message: ${BUTTON('jfur-data-je-export-message')}.textContent }; })()`;
const INTERCEPT = "window.__jfurDownloads = []; document.addEventListener('click', function (e) { const a = e.target && e.target.closest ? e.target.closest('a[download]') : null; if (!a) { return; } e.preventDefault(); const entry = { name: a.getAttribute('download'), scheme: a.href.split(':')[0], text: null }; window.__jfurDownloads.push(entry); fetch(a.href).then(r => r.text()).then(t => { entry.text = t; }); }, true); return true;";

const desktop = [
    { context: 'viewer' },
    ...signIn('viewer'),
    { context: 'main' },
    ...adminStart(),
    { context: 'viewer' },
    { name: 'viewer: Long Test Movie', eval: MOVIE('Long Test Movie'), save: 'movieId' },
    { name: 'viewer: stars and a review through the API', script: `const a = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Rating', { Stars: 4 }); const b = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Review', { Text: ${json(TEXT_V)}, IsSpoiler: false }); if (a.status !== 200 || b.status !== 200) { throw new Error('rating ' + a.status + ', review ' + b.status); } return true;` },
    { context: 'main' },
    ...openTab(),
    { name: 'before a file: Dry run and Import off, the hint asks for a file; Download again hidden', assert: `${BUTTON('jfur-data-dryrun')}.disabled && ${BUTTON('jfur-data-import')}.disabled && ${BUTTON('jfur-data-hint')}.textContent === ${json(CHOOSE)} && !${shown(BUTTON('jfur-data-download'))}` },
    { name: 'screenshot: the Data tab', screenshot: 'userratings-data-desktop.png' },

    // Export.
    { name: 'export: the download kept in the page: a[download] clicks prevented, so no file is written', script: INTERCEPT },
    mark('export'),
    { name: 'export: Export', click: `${ROOT} .jfur-data-export` },
    { name: 'export: done: the file handed to a download link and read, the status says so', waitFor: `window.__jfurDownloads.length === 1 && window.__jfurDownloads[0].text !== null && ${BUTTON('jfur-data-export-status')}.textContent.indexOf('Exported ') === 0 && !${BUTTON('jfur-data-export')}.disabled`, timeout: 60000 },
    {
        name: 'export: the file',
        eval: `(() => { ${NORM} const d = window.__jfurDownloads[0]; let j = null; try { j = JSON.parse(d.text); } catch (e) { return { parsed: false }; } const row = ((j.Data && j.Data.Ratings) || []).find(r => norm(r.UserId) === __vars.ids.viewer && norm(r.ItemId) === norm(__vars.movieId)); return { parsed: true, name: d.name, scheme: d.scheme, format: j.Format, version: j.PluginVersion, users: (j.Users || []).map(u => ({ id: u.Id, deleted: Object.prototype.hasOwnProperty.call(u, 'Deleted') })), tables: Object.keys(j.Data || {}).sort(), viewerText: row ? row.ReviewText : null, viewerStars: row ? row.Stars : null, status: ${BUTTON('jfur-data-export-status')}.textContent, again: ${shown(BUTTON('jfur-data-download'))} }; })()`,
        save: 'exportFile',
        full: true
    },
    {
        name: 'export: the kit\'s export envelope of User Ratings ' + VERSION + ' (the users\' ids in the "N" format, Deleted only when true), with every table and viewer\'s row as they rated; a blob: link named userratings-export-<time>.json, the status naming it; Download again shown',
        assert: `(() => { const f = __vars.exportFile; return f.parsed && f.format === 'jellyfin-plugin-export/1' && f.version === '${VERSION}' && f.users.every(u => /^[0-9a-f]{32}$/.test(u.id) && !u.deleted) && f.users.some(u => u.id === __vars.ids.viewer) && ['FormerUsers', 'ImportLedger', 'ModerationLog', 'Ratings', 'Tombstones'].every(t => f.tables.indexOf(t) >= 0) && f.viewerText === ${json(TEXT_V)} && f.viewerStars === 4 && f.scheme === 'blob' && /^userratings-export-[0-9]{8}-[0-9]{6}[.]json$/.test(f.name) && f.status.endsWith(' as ' + f.name + '.') && f.again; })()`
    },
    since('export', 'exportRequests'),
    {
        name: 'export: a POST answered 202, the job polled, its file fetched with the session\'s authorization: no token in any query',
        assert: "(() => { const r = __vars.exportRequests; const post = r.find(x => x.method === 'POST' && x.path === '/UserRatings/Admin/Export'); const file = r.filter(x => /^\\/UserRatings\\/Admin\\/Jobs\\/[0-9a-f-]+\\/File$/i.test(x.path)); return !!post && post.status === 202 && r.some(x => /^\\/UserRatings\\/Admin\\/Jobs\\/[0-9a-f-]+$/i.test(x.path) && x.status === 200) && file.length === 1 && file[0].status === 200 && file[0].query === '' && r.every(x => !/api_key|token/i.test(x.query || '')); })()"
    },
    { name: 'export: Download the export again', click: `${ROOT} .jfur-data-download` },
    { name: 'export: the same file again', waitFor: 'window.__jfurDownloads.length === 2 && window.__jfurDownloads[1].text !== null', timeout: 30000 },
    { name: 'export: its name and its bytes the same', assert: 'window.__jfurDownloads[1].name === window.__jfurDownloads[0].name && window.__jfurDownloads[1].text === window.__jfurDownloads[0].text' },

    // Import: the dry run first.
    { name: 'import: the exported file put in the file field', script: PUT_FILE('window.__jfurDownloads[0].text', 'window.__jfurDownloads[0].name') },
    { name: 'import: Dry run on, Import still off, the hint asks for the dry run; Merge and By id chosen', assert: `!${BUTTON('jfur-data-dryrun')}.disabled && ${BUTTON('jfur-data-import')}.disabled && ${BUTTON('jfur-data-hint')}.textContent === ${json(NEED_DRY)} && document.querySelector('${ROOT} [role=radio][data-jfur-value="Merge"]').getAttribute('aria-checked') === 'true' && document.querySelector('${ROOT} [role=radio][data-jfur-value="Id"]').getAttribute('aria-checked') === 'true'` },
    mark('dry run'),
    { name: 'dry run: Dry run', click: `${ROOT} .jfur-data-dryrun` },
    { name: 'dry run: its report, Import on', waitFor: `!!${REPORT}.querySelector('.jfur-data-summary') && ${REPORT}.querySelector('.jfur-data-summary').textContent.indexOf('Dry run, nothing was changed.') === 0 && !${BUTTON('jfur-data-import')}.disabled`, timeout: 60000 },
    { name: 'dry run: the report', eval: `({ summary: ${REPORT}.querySelector('.jfur-data-summary').textContent, outcomes: ${outcomes}, markup: ${REPORT}.querySelectorAll('b, i').length, hint: ${BUTTON('jfur-data-hint')}.textContent })`, save: 'dry1', full: true },
    { name: 'dry run: the file named (User Ratings ' + VERSION + '), every row Unchanged, none added or updated; text only; no hint', assert: `__vars.dry1.summary.indexOf('The file is an export of User Ratings ${VERSION} from ') > 0 && __vars.dry1.outcomes.some(o => o.key === 'Unchanged') && !__vars.dry1.outcomes.some(o => o.key === 'Added' || o.key === 'Updated' || o.key === 'StoredDetached') && __vars.dry1.markup === 0 && __vars.dry1.hint === ''` },
    {
        name: 'dry run: the requests',
        eval: `await Promise.all(${P}.requests.slice(window.__mark).filter(r => r.method === 'POST').map(async r => ({ path: r.path, query: r.query, status: r.status, sent: r.body && r.body.blob, same: !!r.blob && (await r.blob.text()) === window.__jfurDownloads[0].text })))`,
        save: 'dryPosts',
        full: true
    },
    { name: 'dry run: one POST of the file to Import, sent as it is (the File, its bytes the export\'s): Merge, by id, dryRun=true, no plan; 202', assert: "__vars.dryPosts.length === 1 && __vars.dryPosts[0].path === '/UserRatings/Admin/Import' && /[?&]mode=Merge(&|$)/.test(__vars.dryPosts[0].query) && /[?&]dryRun=true(&|$)/.test(__vars.dryPosts[0].query) && /[?&]mapUsersBy=Id(&|$)/.test(__vars.dryPosts[0].query) && __vars.dryPosts[0].query.indexOf('ifPlan') < 0 && __vars.dryPosts[0].status === 202 && __vars.dryPosts[0].sent === 'File' && __vars.dryPosts[0].same" },

    // The ratings change after the dry run: Import is refused and imports nothing.
    { context: 'viewer' },
    { name: 'changed: viewer changes their review after the dry run', script: `const b = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Review', { Text: ${json(TEXT_V2)}, IsSpoiler: false }); return b.status;`, save: 'v2' },
    { context: 'main' },
    mark('changed'),
    { name: 'changed: Import', click: `${ROOT} .jfur-data-import` },
    dialogOpen('changed'),
    dialogText('changed', 'mergeQuestion'),
    { name: 'changed: the question names the snapshot taken first, as text', assert: "__vars.mergeQuestion.text.indexOf('Import the file\\'s ratings as the dry run showed? A snapshot of the ratings as they are now is taken first.') >= 0 && __vars.mergeQuestion.markup === 0" },
    { name: 'changed: screenshot of the question', screenshot: 'userratings-data-import-question.png' },
    confirm('changed'),
    { name: 'changed: the 409 in the tab\'s words; Import off again, the hint asks for the dry run', waitFor: `!${DIALOG} && !!${IMPORT_MESSAGE}.querySelector('.jfur-notice-text') && ${IMPORT_MESSAGE}.querySelector('.jfur-notice-text').textContent === ${json(CHANGED)} && ${BUTTON('jfur-data-import')}.disabled && ${BUTTON('jfur-data-hint')}.textContent === ${json(NEED_DRY)}`, timeout: 60000 },
    since('changed', 'changedRequests'),
    { name: 'changed: one POST to Import with dryRun=false and the dry run\'s 32-digit plan; 202', assert: "(() => { const p = __vars.changedRequests.filter(x => x.method === 'POST'); return __vars.v2 === 200 && p.length === 1 && p[0].path === '/UserRatings/Admin/Import' && /[?&]dryRun=false(&|$)/.test(p[0].query) && /[?&]ifPlan=[0-9a-f]{32}(&|$)/i.test(p[0].query) && p[0].status === 202; })()" },
    LAST_JOB('changedJob'),
    { name: 'changed: the job failed with 409 Changed', assert: "__vars.changedJob.kind === 'Import' && __vars.changedJob.state === 'Failed' && __vars.changedJob.code === 'Changed' && __vars.changedJob.errorStatus === 409 && __vars.changedJob.dryRun === false" },
    viewerReview('changed', 'afterChanged'),
    { name: 'changed: nothing imported: viewer\'s review as they changed it', assert: `__vars.afterChanged.text === ${json(TEXT_V2)} && __vars.afterChanged.stars === 4` },

    // The dry run again, then Import.
    mark('import'),
    { name: 'import: Dry run again', click: `${ROOT} .jfur-data-dryrun` },
    { name: 'import: its report, Import on', waitFor: `!!${REPORT}.querySelector('.jfur-data-summary') && ${REPORT}.querySelector('.jfur-data-summary').textContent.indexOf('Dry run, nothing was changed.') === 0 && !${BUTTON('jfur-data-import')}.disabled && !${IMPORT_MESSAGE}.querySelector('.jfur-notice')`, timeout: 60000 },
    { name: 'import: the report', eval: outcomes, save: 'dry2', full: true },
    { name: 'import: viewer\'s row now kept as it is here (newer here)', assert: "__vars.dry2.some(o => o.key === 'KeptNewer' && o.text === 'Kept as they are here (newer here): 1')" },
    { name: 'import: Import', click: `${ROOT} .jfur-data-import` },
    dialogOpen('import'),
    confirm('import'),
    { name: 'import: imported: the report says so and names the snapshot taken first', waitFor: `!${DIALOG} && !!${REPORT}.querySelector('.jfur-data-summary') && ${REPORT}.querySelector('.jfur-data-summary').textContent.indexOf('Imported. ') === 0 && Array.from(${REPORT}.querySelectorAll('.jfur-data-intro')).some(p => p.textContent.indexOf('The ratings from just before were kept as ') === 0)`, timeout: 60000 },
    { name: 'import: the import\'s snapshot', eval: `(() => { const p = Array.from(${REPORT}.querySelectorAll('.jfur-data-intro')).find(x => x.textContent.indexOf('The ratings from just before were kept as ') === 0).textContent; return p.slice('The ratings from just before were kept as '.length, p.indexOf(' (Snapshots, below).')); })()`, save: 'importSnap' },
    LAST_JOB('importJob'),
    { name: 'import: the job succeeded, not a dry run', assert: "__vars.importJob.kind === 'Import' && __vars.importJob.state === 'Succeeded' && __vars.importJob.dryRun === false" },
    { name: 'import: the snapshots read again: the import\'s listed as "Before an import", with Restore…', waitFor: `(() => { const b = document.querySelector('${ROOT} .jfur-data-restore[data-jfur-name="' + __vars.importSnap + '"]'); return !!b && b.closest('.jfur-data-snapshot').querySelector('.jfur-data-snapshot-meta').textContent.indexOf('Before an import · ') === 0; })()`, timeout: 15000 },
    viewerReview('import', 'afterImport'),
    { name: 'import: Merge kept viewer\'s newer review', assert: `__vars.afterImport.text === ${json(TEXT_V2)}` },
    { name: 'import: screenshot', screenshot: 'userratings-data-imported.png' },

    // Restore… of the import's snapshot undoes what came after it.
    { context: 'viewer' },
    { name: 'restore: viewer changes their review again, after the import', script: `const b = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Review', { Text: ${json(TEXT_V3)}, IsSpoiler: false }); return b.status;`, save: 'v3' },
    { context: 'main' },
    mark('restore'),
    { name: 'restore: Restore… on the import\'s snapshot', click: `${ROOT} .jfur-data-restore[data-jfur-name="\${importSnap}"]` },
    dialogOpen('restore'),
    dialogText('restore', 'restoreQuestion'),
    { name: 'restore: the question names the snapshot, says what is undone and the snapshot taken first', assert: "__vars.restoreQuestion.text.indexOf('Put ' + __vars.importSnap + ' from ') >= 0 && __vars.restoreQuestion.text.indexOf(' back? Every change made since is undone, imports with their deletions too, except deletions made on this server, which stay deleted. A snapshot of the ratings as they are now is taken first.') > 0 && __vars.restoreQuestion.markup === 0" },
    { name: 'restore: screenshot of the question', screenshot: 'userratings-data-restore-question.png' },
    confirm('restore'),
    { name: 'restore: restored: the report and the toast', waitFor: `!${DIALOG} && !!${RESTORE_REPORT}.querySelector('.jfur-data-summary') && ${RESTORE_REPORT}.querySelector('.jfur-data-summary').textContent === 'Restored ' + __vars.importSnap + '.' && Array.from(document.querySelectorAll('.jfur-toast')).some(t => t.textContent === 'Restored ' + __vars.importSnap + '.')`, timeout: 60000 },
    { name: 'restore: the report', eval: `Array.from(${RESTORE_REPORT}.children).map(p => p.textContent)`, save: 'restored', full: true },
    { name: 'restore: the restore\'s own snapshot named', assert: "__vars.restored.some(t => t.indexOf('The ratings from just before were kept as ') === 0)" },
    { name: 'restore: the restore\'s snapshot', eval: "(() => { const t = __vars.restored.find(x => x.indexOf('The ratings from just before were kept as ') === 0); return t.slice('The ratings from just before were kept as '.length, t.indexOf(' (Snapshots, below).')); })()", save: 'restoreSnap' },
    since('restore', 'restoreRequests'),
    { name: 'restore: one POST to Snapshots/Restore with the snapshot\'s name; 200', assert: "(() => { const p = __vars.restoreRequests.filter(x => x.method === 'POST'); return __vars.v3 === 200 && p.length === 1 && p[0].path === '/UserRatings/Admin/Snapshots/Restore' && decodeURIComponent(p[0].query) === '?name=' + __vars.importSnap && p[0].status === 200; })()" },
    viewerReview('restore', 'afterRestore'),
    { name: 'restore: viewer\'s review as it was before the import (the change after it undone)', assert: `__vars.afterRestore.text === ${json(TEXT_V2)} && __vars.afterRestore.stars === 4` },
    { name: 'restore: the snapshots read again: the restore\'s listed as "Before a restore"', waitFor: `(() => { const b = document.querySelector('${ROOT} .jfur-data-restore[data-jfur-name="' + __vars.restoreSnap + '"]'); return !!b && b.closest('.jfur-data-snapshot').querySelector('.jfur-data-snapshot-meta').textContent.indexOf('Before a restore · ') === 0; })()`, timeout: 15000 },
    { name: 'restore: the focus not lost to the page\'s body', assert: NOT_BODY },
    { name: 'restore: screenshot', screenshot: 'userratings-data-restored.png' },

    // The JE export: JE 12.14's reviews.json, built in memory by the server and saved through a link the same way;
    // viewer's rating of Tagged Movie (its folder's [tmdbid-900001]) in it, Long Test Movie's (no TMDb id) counted.
    { context: 'viewer' },
    { name: 'je export: viewer: Tagged Movie', eval: MOVIE('Tagged Movie'), save: 'taggedId' },
    { name: 'je export: viewer: 4.5 stars and a review of Tagged Movie through the API', script: `const a = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.taggedId + '/Mine/Rating', { Stars: 4.5 }); const b = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.taggedId + '/Mine/Review', { Text: ${json(TEXT_JE)}, IsSpoiler: false }); if (a.status !== 200 || b.status !== 200) { throw new Error('rating ' + a.status + ', review ' + b.status); } return true;` },
    { context: 'main' },
    { name: 'je export: both films\' TMDb ids as the library holds them', script: JE_TMDB, save: 'tmdb', full: true },
    mark('je export'),
    { name: 'je export: Export for Jellyfin Enhanced', click: `${ROOT} .jfur-data-je-export` },
    { name: 'je export: done: reviews.json handed to a download link and read, the status says so', waitFor: `window.__jfurDownloads.length === 3 && window.__jfurDownloads[2].text !== null && ${BUTTON('jfur-data-je-export-status')}.textContent.indexOf('Saved reviews.json: ') === 0 && !${BUTTON('jfur-data-je-export')}.disabled`, timeout: 60000 },
    { name: 'je export: the file', eval: JE_FILE, save: 'jeFile', full: true },
    {
        name: 'je export: {"Reviews":{...}} only, its keys sorted, each <userIdN>:<movie|tv>:<tmdbKey> with UserId, TmdbId (the whole TMDb key), MediaType, Content, Rating, CreatedAt, UpdatedAt in that order and as JE writes them; viewer\'s Tagged Movie in it as <viewerN>:movie:<its TMDb id> with 4.5 and the review\'s text as it is (quotes, markup and & kept); Long Test Movie in it with its stars and text when it has a TMDb id, else counted as no TMDb id; a blob: link named reviews.json, the status counting the reviews, no problem shown',
        assert: `(() => { const f = __vars.jeFile; if (!f.parsed || !f.reviews || !__vars.tmdb.tagged) { return false; } const saved = 'Saved reviews.json: ' + (f.count === 1 ? '1 review.' : f.count + ' reviews.'); return f.name === 'reviews.json' && f.scheme === 'blob' && f.top.join(',') === 'Reviews' && f.count >= 1 && f.sorted && f.bad.length === 0 && f.status.indexOf(saved) === 0 && f.message === '' && f.tagged !== null && f.tagged.rating === 4.5 && f.tagged.content === ${json(TEXT_JE)} && f.tagged.tmdbId === __vars.tmdb.tagged && f.tagged.type === 'movie' && (__vars.tmdb.movie ? f.movie !== null && f.movie.rating === 4 && f.movie.content === ${json(TEXT_V2)} : f.movie === null && f.status.indexOf('no TMDb id (') > 0); })()`
    },
    since('je export', 'jeRequests'),
    {
        name: 'je export: one GET of Admin/Export/JellyfinEnhanced answered 200, its only query leaveOutRestricted=true, the tab\'s default (0.3.24; the session\'s authorization, never a token in a link); nothing else of User Ratings asked, nothing written',
        assert: "(() => { const r = __vars.jeRequests.filter(x => x.path.indexOf('/UserRatings/') === 0); const je = r.filter(x => x.path === '/UserRatings/Admin/Export/JellyfinEnhanced'); return je.length === 1 && je[0].method === 'GET' && je[0].status === 200 && je[0].query === '?leaveOutRestricted=true' && r.length === 1 && __vars.jeRequests.every(x => x.method === 'GET'); })()"
    },
    { name: 'je export: screenshot', screenshot: 'userratings-data-je-export.png' },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    sweep('at the end'),
    noneLeft,
    sweep('at the end', 'taggedId', 'Tagged Movie'),
    noneLeftOf('taggedId', 'Tagged Movie')
];

write('userratings-data-desktop.json', `User Ratings ${VERSION}, the Data tab: in the default sandbox as admin, with viewer in their own window, the desktop layout: the settings page's Data tab read with GETs only (and the page's own Detached/Seen), no select; before a file Dry run and Import are off; Export runs as a job (POST 202, polled), its file fetched with the session's authorization (no token in any query) and handed to a blob: download link, which the run keeps in the page (a[download] clicks prevented, so no file is written): the kit's export envelope of User Ratings ${VERSION} with the users' ids in the "N" format, every table and viewer's row; Download again gives the same name and bytes; that file in the file field: Import stays off until a Merge dry run by id (the file sent as it is, every row Unchanged, the report as text); viewer changes their review: Import asks first, then the job's 409 Changed shows in the tab's words, nothing is imported and Import is off again; the dry run again (viewer's row kept as newer here) and Import: the report names the import's snapshot, listed as "Before an import"; viewer changes their review again; Restore… of the import's snapshot asks first (Cancel focused), then restores it: viewer's review as before the import, the restore's own snapshot named and listed. Then viewer rates Tagged Movie (its folder's TMDb id) 4.5 stars with a review, and Export for Jellyfin Enhanced fetches JE 12.14's reviews.json with one GET (its only query leaveOutRestricted=true, the default choice since 0.3.24) and saves it the same way: only Reviews, its keys sorted, each <userIdN>:<movie|tv>:<tmdbKey> with JE's seven fields in order, TmdbId the whole key; viewer's Tagged Movie in it with 4.5 and the text as it is; Long Test Movie counted as no TMDb id when the film has none; the status counting the reviews. Every rating on Long Test Movie and Tagged Movie removed at the end.`, desktop);

// ---------------------------------------------------------------------------------------------------------- TV

const DOWN = { key: 'ArrowDown' };
const UP = { key: 'ArrowUp' };
const inDialog = `!!${DIALOG} && ${DIALOG}.contains(document.activeElement)`;
const needSnapshot = { name: 'the sandbox has a snapshot to offer Restore… on', assert: `document.querySelectorAll('${ROOT} .jfur-data-restore').length > 0`, message: 'no snapshot in the sandbox yet: run userratings-data-desktop.json (or any import) first' };
const back = [
    { name: 'tv: Back (461, webOS)', cdp: 'Input.dispatchKeyEvent', params: { type: 'rawKeyDown', windowsVirtualKeyCode: 461, nativeVirtualKeyCode: 461, key: 'Unidentified', code: '' } },
    { cdp: 'Input.dispatchKeyEvent', params: { type: 'keyUp', windowsVirtualKeyCode: 461, nativeVirtualKeyCode: 461, key: 'Unidentified', code: '' } }
];
const ACTIVE = "(() => { const a = document.activeElement; return a ? { tag: a.tagName, cls: String(a.className), value: a.getAttribute('data-jfur-value'), id: a.id || null, inPanel: document.querySelector('#UserRatingsData').contains(a) } : null; })()";
const FIRST_RESTORE = `document.querySelector('${ROOT} .jfur-data-restore')`;
const tv = [
    ...signIn('admin', 'tv'),
    { name: 'the TV layout applied', assert: "document.documentElement.classList.contains('layout-tv')" },
    snapshotNames('before the run', 'snapsBefore'),
    ...openTab(),
    needSnapshot,
    { name: 'tv: the Replace choice focused', script: `const b = document.querySelector('${ROOT} [role=radio][data-jfur-value="Replace"]'); b.focus(); return document.activeElement === b;` },
    { name: 'tv: OK', key: 'Enter' },
    { name: 'tv: OK chose Replace', waitFor: `document.querySelector('${ROOT} [role=radio][data-jfur-value="Replace"]').getAttribute('aria-checked') === 'true' && document.querySelector('${ROOT} [role=radio][data-jfur-value="Merge"]').getAttribute('aria-checked') === 'false'`, timeout: 5000 },
    { name: 'tv: the Merge choice focused', script: `const b = document.querySelector('${ROOT} [role=radio][data-jfur-value="Merge"]'); b.focus(); return document.activeElement === b;` },
    { name: 'tv: OK again', key: 'Enter' },
    { name: 'tv: Merge again', waitFor: `document.querySelector('${ROOT} [role=radio][data-jfur-value="Merge"]').getAttribute('aria-checked') === 'true'`, timeout: 5000 },
    // The D-pad's Up and Down through the tab: the page's focuscontainer-y keeps them off the dashboard's drawer, which is
    // nearer than a section's next control under its heading and intro; the file field is a stop.
    { name: 'tv: Export focused', script: `const b = ${BUTTON('jfur-data-export')}; b.focus(); return document.activeElement === b;` },
    { name: 'tv: Down', ...DOWN },
    { name: 'tv: what has the focus after Down', eval: ACTIVE, save: 'afterDown', full: true },
    { name: 'tv: on the file field (the next control below, past the import\'s heading and intro), not the drawer', assert: `document.activeElement === ${BUTTON('jfur-data-file')}` },
    { name: 'tv: Down again', ...DOWN },
    { name: 'tv: on Merge', assert: `document.activeElement === document.querySelector('${ROOT} [role=radio][data-jfur-value="Merge"]')` },
    { name: 'tv: Up', ...UP },
    { name: 'tv: on the file field again', assert: `document.activeElement === ${BUTTON('jfur-data-file')}` },
    { name: 'tv: Up again', ...UP },
    { name: 'tv: on Export again', assert: `document.activeElement === ${BUTTON('jfur-data-export')}` },
    { name: 'tv: Up once more', ...UP },
    { name: 'tv: on the Data tab, above the tab', assert: "document.activeElement === document.querySelector('#UserRatingsTabData')" },
    { name: 'tv: the last Restore… focused', script: `const all = document.querySelectorAll('${ROOT} .jfur-data-restore'); const b = all[all.length - 1]; b.focus(); return document.activeElement === b;` },
    { name: 'tv: Down ×3', ...DOWN },
    { ...DOWN }, { ...DOWN },
    { name: 'tv: what has the focus after them', eval: ACTIVE, save: 'afterDowns', full: true },
    { name: 'tv: still in the Data tab (the users\' controls or the detached list), never the drawer', assert: "document.querySelector('#UserRatingsData').contains(document.activeElement)" },
    { name: 'tv: the first Restore… focused', script: `const b = ${FIRST_RESTORE}; b.focus(); return document.activeElement === b ? b.getAttribute('data-jfur-name') : null;`, save: 'snap' },
    { name: 'tv: the hash before', eval: 'location.hash', save: 'hash' },
    mark('tv'),
    { name: 'tv: OK on Restore…', key: 'Enter' },
    { name: 'tv: the question, the focus on Cancel', waitFor: `!!${DIALOG} && document.activeElement === ${DIALOG}.querySelector('.jfur-data-cancel')`, timeout: 15000 },
    { name: 'tv: Down ×3 (past the last control)', ...DOWN },
    { ...DOWN }, { ...DOWN },
    { name: 'tv: the focus still in the question, on Restore (no wrap)', assert: `${inDialog} && document.activeElement === ${DIALOG}.querySelector('.jfur-data-confirm')` },
    { name: 'tv: screenshot of the question', screenshot: 'userratings-data-tv-question.png' },
    { name: 'tv: Up ×4 (past the first control)', ...UP },
    { ...UP }, { ...UP }, { ...UP },
    { name: 'tv: the focus still in the question (no way out by the D-pad)', assert: inDialog },
    ...back,
    { name: 'tv: the question closed, the focus back on that Restore…, still on the settings page', waitFor: `!${DIALOG} && !!document.activeElement && document.activeElement.getAttribute('data-jfur-name') === __vars.snap && location.hash === __vars.hash`, timeout: 5000 },
    { name: 'tv: OK on Restore… again', key: 'Enter' },
    { name: 'tv: the question again, the focus on Cancel', waitFor: `!!${DIALOG} && document.activeElement === ${DIALOG}.querySelector('.jfur-data-cancel')`, timeout: 15000 },
    { name: 'tv: OK on Cancel', key: 'Enter' },
    { name: 'tv: closed, the focus back on Restore…, never the page\'s body', waitFor: `!${DIALOG} && ${NOT_BODY} && document.activeElement.getAttribute('data-jfur-name') === __vars.snap`, timeout: 5000 },
    { name: 'tv: the detached list\'s "removed from the library" focused', script: `const b = document.querySelector('${DET} [role=radio][data-jfur-value="Removed"]'); b.focus(); return document.activeElement === b;` },
    { name: 'tv: OK on it', key: 'Enter' },
    { name: 'tv: chosen, the list read for that reason', waitFor: `document.querySelector('${DET} [role=radio][data-jfur-value="Removed"]').getAttribute('aria-checked') === 'true' && ${P}.requests.slice(window.__mark).some(r => r.method === 'GET' && r.path === '/UserRatings/Admin/Detached' && /[?&]reason=Removed(&|$)/.test(r.query) && r.status === 200)`, timeout: 10000 },
    { name: 'tv: "Every reason" focused', script: `const b = document.querySelector('${DET} [role=radio][data-jfur-value=""]'); b.focus(); return document.activeElement === b;` },
    { name: 'tv: OK on it too', key: 'Enter' },
    { name: 'tv: every reason again', waitFor: `document.querySelector('${DET} [role=radio][data-jfur-value=""]').getAttribute('aria-checked') === 'true'`, timeout: 10000 },
    since('tv', 'tvRequests'),
    { name: 'tv: nothing written: no POST, PUT or DELETE', assert: "__vars.tvRequests.every(r => r.method === 'GET')" },
    { name: 'tv: screenshot', screenshot: 'userratings-data-tv.png' },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    sameSnapshots
];

write('userratings-data-tv.json', `User Ratings ${VERSION}, the Data tab in the TV layout: in the default sandbox as admin in jellyfin-web 10.11.5's TV layout: the Data tab and the detached list have no select; OK on a choice picks it (Replace, then Merge again); Down and Up walk the tab (Export, the file field, Merge and back, then the Data tab above it), and Down from the last Restore… stays in the tab, never the dashboard's drawer; OK on Restore… opens its question with the focus on Cancel; Down past the last control and Up past the first keep the focus inside it (no wrap, no way out); Back (keyCode 461) closes it with the focus back on that Restore… and the page where it was; OK on Cancel closes it the same way; OK on a reason of the detached list reads the list for it, and on "Every reason" again; nothing is written and the snapshots are as before the run.`, tv);

// ---------------------------------------------------------------------------------------------------------- phone

const tall = (selector) => `Array.from(document.querySelectorAll(${json(selector)})).filter(e => e.getClientRects().length > 0).map(e => ({ cls: e.className, h: Math.round(e.getBoundingClientRect().height) })).filter(x => x.h < 44)`;
const phone = [
    ...signIn('admin', null, { width: 375, height: 812, mobile: true, touch: true }),
    snapshotNames('before the run', 'snapsBefore'),
    ...openTab(),
    needSnapshot,
    { name: 'phone: an export of the sandbox at once, through the API', script: `const r = await ${P}.server('GET', '${ADMIN}/Export'); if (r.status !== 200) { throw new Error('export ' + r.status); } window.__jfurPhoneFile = JSON.stringify(r.json); return window.__jfurPhoneFile.length > 0;` },
    { name: 'phone: it put in the file field', script: PUT_FILE('window.__jfurPhoneFile', "'userratings-export-phone.json'") },
    { name: 'phone: Dry run', click: `${ROOT} .jfur-data-dryrun` },
    { name: 'phone: its report shown', waitFor: `!!${REPORT}.querySelector('.jfur-data-summary') && ${REPORT}.querySelector('.jfur-data-summary').textContent.indexOf('Dry run, nothing was changed.') === 0`, timeout: 60000 },
    { name: 'phone: the layout as shown', eval: `(() => { const out = Array.from(document.querySelectorAll('${ROOT} *, ${DET} *')).filter(e => e.getClientRects().length > 0 && e.children.length === 0).filter(e => { const b = e.getBoundingClientRect(); return b.left < -1 || b.right > innerWidth + 1; }).map(e => e.className || e.tagName); return { width: innerWidth, scroll: document.documentElement.scrollWidth, body: document.body.scrollWidth, out, snapshots: document.querySelectorAll('${ROOT} .jfur-data-snapshot').length }; })()`, save: 'phone', full: true },
    { name: 'phone: no sideways scroll; every control, name and line of the report inside the screen', assert: '__vars.phone.scroll <= __vars.phone.width && __vars.phone.body <= __vars.phone.width && __vars.phone.out.length === 0 && __vars.phone.snapshots > 0' },
    { name: 'phone: the controls under 44 px high (none)', eval: tall(`${ROOT} button, ${ROOT} input, ${DET} button`), save: 'short', full: true },
    { name: 'phone: every control of the tab and of the detached list at least 44 px', assert: '__vars.short.length === 0' },
    { name: 'phone: screenshot of the tab', screenshot: 'userratings-data-phone.png' },
    { name: 'phone: the first Restore… scrolled to', script: `const b = ${FIRST_RESTORE}; b.scrollIntoView({ block: 'center' }); return b.getAttribute('data-jfur-name');`, save: 'snap' },
    { name: 'phone: Restore…', click: `${ROOT} .jfur-data-restore[data-jfur-name="\${snap}"]` },
    dialogOpen('phone'),
    { name: 'phone: the question\'s controls under 44 px high (none), the question inside the screen', eval: `({ short: ${tall('[role="dialog"] button')}, left: Math.round(${DIALOG}.getBoundingClientRect().left), right: Math.round(${DIALOG}.getBoundingClientRect().right), scroll: document.documentElement.scrollWidth, width: innerWidth })`, save: 'dialog', full: true },
    { name: 'phone: every control of the question at least 44 px, inside the screen', assert: '__vars.dialog.short.length === 0 && __vars.dialog.left >= 0 && __vars.dialog.right <= __vars.dialog.width && __vars.dialog.scroll <= __vars.dialog.width' },
    { name: 'phone: screenshot of the question', screenshot: 'userratings-data-phone-question.png' },
    { name: 'phone: Cancel', click: '.jfur-panel[role="dialog"] .jfur-data-cancel' },
    { name: 'phone: closed, the focus back on Restore…', waitFor: `!${DIALOG} && !!document.activeElement && document.activeElement.getAttribute('data-jfur-name') === __vars.snap`, timeout: 5000 },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    sameSnapshots
];

write('userratings-data-phone.json', `User Ratings ${VERSION}, the Data tab under touch: in the default sandbox as admin on a 375x812 phone with touch: the Data tab with a dry run's report shown and the detached list: no sideways scroll, every control, snapshot name and line of the report inside the screen; every button and field of the tab and of the list at least 44 px high; Restore…'s question inside the screen with every control at least 44 px; Cancel restores nothing and puts the focus back on Restore…. The snapshots are as before the run.`, phone);

// ---------------------------------------------------------------------------------------------------------- the detached list

// A synthetic export: the sandbox's own export at once with its ratings replaced by three detached ones on titles the
// library lacks (random ids, written in the "D" form, the only one System.Text.Json reads a Guid in), named as Long Test
// Movie. Merged by id (dryOnly: its dry run only), polled to the end.
const IMPORT_ROWS = (dryOnly) => `const base = await ${P}.server('GET', '${ADMIN}/Export'); if (base.status !== 200) { throw new Error('export ' + base.status); } const env = base.json; const m = __vars.movie; const d = (n) => n.slice(0, 8) + '-' + n.slice(8, 12) + '-' + n.slice(12, 16) + '-' + n.slice(16, 20) + '-' + n.slice(20); const at = (h) => new Date(Date.now() - h * 3600000).toISOString(); const row = (id, user, name, ghost, stars, text, reason, h) => ({ Id: d(id), UserId: d(user), UserName: name, ItemId: d(ghost), Identity: { ItemId: d(ghost), Kind: 'Movie', Name: m.name, Year: m.year, ProviderIds: m.providerIds }, Stars: stars, ReviewText: text, IsSpoiler: false, CreatedUtc: at(h + 24), UpdatedUtc: at(h + 24), ReviewUpdatedUtc: text ? at(h + 24) : null, EditCount: 0, DetachedUtc: at(h), DetachReason: reason }); const r = __vars.rows; env.Data.Ratings = [row(r.a, __vars.ids.viewer, 'viewer', r.ghostA, 4.5, ${json(TEXT_D)}, 'Removed', 30), row(r.b, __vars.ids.viewer2, 'viewer2', r.ghostB, 3, null, 'Missing', 29), row(r.c, __vars.ids.viewer2, 'viewer2', r.ghostC, 2, null, 'Unmatched', 28)]; const go = async (dry, plan) => { const s = await ${P}.server('POST', '${ADMIN}/Import?mode=Merge&mapUsersBy=Id&dryRun=' + dry + (plan ? '&ifPlan=' + plan : ''), env); if (s.status !== 202) { throw new Error('import ' + s.status + ' ' + JSON.stringify(s.json)); } let j = s.json; for (let i = 0; i < 240 && (j.State === 'Queued' || j.State === 'Running'); i++) { await new Promise(res => setTimeout(res, 500)); j = (await ${P}.server('GET', '${ADMIN}/Jobs/' + s.json.Id)).json; } if (j.State !== 'Succeeded') { throw new Error('the import job ' + j.State + ' ' + (j.ErrorCode || '') + ' ' + (j.Error || '')); } return j.Report; }; const dry = await go(true, null); ${dryOnly ? 'return { dry: dry.Outcomes, unusable: dry.Unusable || 0 };' : 'const real = await go(false, dry.PlanToken); return { dry: dry.Outcomes, real: real.Outcomes, unusable: (dry.Unusable || 0) + (real.Unusable || 0), safety: real.SafetySnapshot || null };'}`;
const ROW_OF = `const rowOf = (id) => Array.from(document.querySelectorAll('${DET} .jfur-det-row')).find(li => String(li.getAttribute('data-jfur-rating')).replace(/-/g, '').toLowerCase() === id) || null;`;
const SHOWN_ROW = (key, save) => ({
    name: `the row of ${key} as shown`,
    eval: `(() => { ${ROW_OF} ${NORM} const li = rowOf(__vars.rows.${key}); if (!li) { return null; } const t = li.querySelector('.jfur-det-title'); return { attr: li.getAttribute('data-jfur-rating'), title: t.textContent, dir: t.getAttribute('dir'), bidi: getComputedStyle(t).unicodeBidi, meta: Array.from(li.querySelectorAll('.jfur-det-meta')).map(p => p.textContent), candidates: Array.from(li.querySelectorAll('.jfur-det-candidate')).map(c => ({ item: c.querySelector('.jfur-det-relink').getAttribute('data-jfur-item'), movie: norm(c.querySelector('.jfur-det-relink').getAttribute('data-jfur-item')) === norm(__vars.movieId), name: c.querySelector('.jfur-det-candidate-name').textContent, nameDir: c.querySelector('.jfur-det-candidate-name').getAttribute('dir'), meta: c.querySelector('.jfur-data-snapshot-meta').textContent, disabled: c.querySelector('.jfur-det-relink').disabled })), purge: li.querySelector('.jfur-det-purge').getAttribute('aria-label'), markup: li.querySelectorAll('b, i').length }; })()`,
    save,
    full: true
});
const STRENGTHS = json(['the same title', 'the same provider id', 'the same name and year']);
const detachedNow = (label, save) => ({
    name: `${label}: the detached ratings as the server holds them`,
    script: `${NORM} const r = await ${P}.server('GET', '${ADMIN}/Detached?limit=50'); if (r.status !== 200) { throw new Error('detached ' + r.status); } return { total: r.json.TotalRecordCount, ours: r.json.Items.map(x => norm(x.Id)).filter(id => id === __vars.rows.a || id === __vars.rows.b || id === __vars.rows.c) };`,
    save,
    full: true
});

const detached = [
    { context: 'viewer' },
    ...signIn('viewer'),
    { context: 'main' },
    ...adminStart(),
    { name: 'Long Test Movie as the library holds it', script: `const i = await ApiClient.getItem(ApiClient.getCurrentUserId(), __vars.movieId); return { name: i.Name, year: i.ProductionYear || null, providerIds: i.ProviderIds || {} };`, save: 'movie', full: true },
    { name: 'its title as the list writes it (the year after the name unless the name holds it)', eval: "(() => { const m = __vars.movie; const y = m.year ? '(' + m.year + ')' : ''; return y && m.name.indexOf(y) < 0 ? m.name + ' ' + y : m.name; })()", save: 'title' },
    { name: 'the run\'s ratings and the titles the library lacks: random ids', eval: "(() => { const id = () => Array.from(crypto.getRandomValues(new Uint8Array(16))).map(b => b.toString(16).padStart(2, '0')).join(''); return { a: id(), b: id(), c: id(), ghostA: id(), ghostB: id(), ghostC: id() }; })()", save: 'rows', full: true },
    detachedNow('before', 'before'),
    { name: 'the run\'s rows fit on the list\'s first page (oldest first, 50 a page)', assert: '__vars.before.total <= 47', message: 'more than 47 detached ratings in the sandbox: purge some first (Data → Detached & ambiguous)' },
    { name: 'three detached ratings made by importing a synthetic export (its dry run, then its plan)', script: IMPORT_ROWS(false), save: 'made', full: true },
    { name: 'the three kept detached; the import\'s snapshot named', assert: '__vars.made.unusable === 0 && __vars.made.dry.StoredDetached === 3 && __vars.made.real.StoredDetached === 3 && !__vars.made.real.Added && typeof __vars.made.safety === \'string\' && __vars.made.safety.length > 0' },
    ...openTab(),
    { name: 'the list shows the three', waitFor: `(() => { ${ROW_OF} return !!rowOf(__vars.rows.a) && !!rowOf(__vars.rows.b) && !!rowOf(__vars.rows.c); })()`, timeout: 15000 },
    { name: 'the three in the list\'s order: the oldest first', eval: `(() => { ${NORM} return Array.from(document.querySelectorAll('${DET} .jfur-det-row')).map(li => norm(li.getAttribute('data-jfur-rating'))).filter(id => id === __vars.rows.a || id === __vars.rows.b || id === __vars.rows.c); })()`, save: 'order', full: true },
    { name: 'viewer\'s (detached 30 hours ago) first, then viewer2\'s two', assert: 'JSON.stringify(__vars.order) === JSON.stringify([__vars.rows.a, __vars.rows.b, __vars.rows.c])' },
    SHOWN_ROW('a', 'shownA'),
    { name: 'viewer\'s row: the title as text, isolated; by viewer, 4.5 out of 5 with a review; removed from the library; Long Test Movie to go back to, by how it matches; no markup', assert: `(() => { const s = __vars.shownA; return !!s && s.title === __vars.title && s.dir === 'auto' && s.bidi === 'isolate' && s.meta[0] === 'By viewer · 4.5 out of 5, with a review' && s.meta[1].indexOf('Detached ') === 0 && s.meta[1].endsWith(': removed from the library') && s.candidates.some(c => c.movie && !c.disabled && c.name === __vars.title && c.nameDir === 'auto' && ${STRENGTHS}.indexOf(c.meta) >= 0) && s.purge === 'Purge the rating of viewer on ' + __vars.title + '…' && s.markup === 0; })()` },
    SHOWN_ROW('c', 'shownC'),
    { name: 'viewer2\'s last row: stars only, not matched', assert: "(() => { const s = __vars.shownC; return !!s && s.meta[0] === 'By viewer2 · 2 out of 5, stars only' && s.meta[1].endsWith(': not matched') && s.candidates.some(c => c.movie && !c.disabled); })()" },
    { name: 'the candidates\' attribute for Long Test Movie', eval: "__vars.shownA.candidates.find(c => c.movie).item", save: 'movieItem' },
    ...['a', 'b', 'c'].map((k) => ({ name: `the attribute of ${k}'s row`, eval: `(() => { ${ROW_OF} return rowOf(__vars.rows.${k}).getAttribute('data-jfur-rating'); })()`, save: 'attr' + k.toUpperCase() })),
    { name: 'screenshot: the detached list', screenshot: 'userratings-detached-desktop.png' },

    // A reason filters the list.
    mark('reason'),
    { name: 'reason: "missing from the library"', click: `${DET} [role=radio][data-jfur-value="Missing"]` },
    { name: 'reason: the list read for it: viewer2\'s missing one, not the other two', waitFor: `(() => { ${ROW_OF} return ${P}.requests.slice(window.__mark).some(r => r.method === 'GET' && r.path === '/UserRatings/Admin/Detached' && /[?&]reason=Missing(&|$)/.test(r.query) && r.status === 200) && !!rowOf(__vars.rows.b) && !rowOf(__vars.rows.a) && !rowOf(__vars.rows.c); })()`, timeout: 10000 },
    { name: 'reason: "Every reason"', click: `${DET} [role=radio][data-jfur-value=""]` },
    { name: 'reason: the three again', waitFor: `(() => { ${ROW_OF} return !!rowOf(__vars.rows.a) && !!rowOf(__vars.rows.b) && !!rowOf(__vars.rows.c); })()`, timeout: 10000 },

    // Relink.
    mark('relink'),
    { name: 'relink: Relink viewer\'s rating to Long Test Movie', click: `${DET} [data-jfur-rating="\${attrA}"] .jfur-det-relink[data-jfur-item="\${movieItem}"]` },
    dialogOpen('relink'),
    dialogText('relink', 'relinkQuestion'),
    { name: 'relink: the question names the member and both titles, as text', assert: "__vars.relinkQuestion.text.indexOf('Give the rating of viewer on ' + __vars.title + ' to ' + __vars.title + '?') >= 0 && __vars.relinkQuestion.markup === 0" },
    confirm('relink'),
    { name: 'relink: relinked: the toast, the row gone from the list read again', waitFor: `(() => { ${ROW_OF} return !${DIALOG} && ${toast('The rating was relinked.')} && !rowOf(__vars.rows.a) && !!rowOf(__vars.rows.b); })()`, timeout: 15000 },
    since('relink', 'relinkRequests'),
    { name: 'relink: one POST to Relink with Long Test Movie; 204; the list read again', assert: `(() => { const r = __vars.relinkRequests; const p = r.filter(x => x.method === 'POST' && x.path.indexOf('/UserRatings/Admin/Detached/') === 0); const i = r.findIndex(x => x.method === 'POST' && x.path === '/UserRatings/Admin/Detached/' + __vars.attrA + '/Relink'); return p.length === 1 && i >= 0 && r[i].status === 204 && r.slice(i + 1).some(x => x.method === 'GET' && x.path === '/UserRatings/Admin/Detached' && x.status === 200); })()` },
    { name: 'relink: the focus not lost to the page\'s body', assert: NOT_BODY },
    { context: 'viewer' },
    { name: 'relink: viewer\'s own rating of Long Test Movie', script: `return await ${P}.mine(__vars.movieId);`, save: 'viewerMine', full: true },
    { name: 'relink: viewer sees their stars and review on it again', assert: `__vars.viewerMine.status === 200 && __vars.viewerMine.stars === 4.5 && __vars.viewerMine.text === ${json(TEXT_D)}` },
    { context: 'main' },

    // A rating purged meanwhile (another window): its Relink is the 404, kept while the list is read again.
    { name: 'gone: viewer2\'s last rating purged through the API meanwhile', script: `const r = await ${P}.server('DELETE', '${ADMIN}/Detached/' + __vars.attrC); return r.status;`, save: 'purgedC' },
    { name: 'gone: 204, the list not read since (it still shows it)', assert: `(() => { ${ROW_OF} return __vars.purgedC === 204 && !!rowOf(__vars.rows.c); })()` },
    mark('gone'),
    { name: 'gone: Relink it', click: `${DET} [data-jfur-rating="\${attrC}"] .jfur-det-relink[data-jfur-item="\${movieItem}"]` },
    dialogOpen('gone'),
    confirm('gone'),
    { name: 'gone: the note says it is gone, kept after the list is read again without it', waitFor: `(() => { ${ROW_OF} const n = document.querySelector('${DET} .jfur-det-notice .jfur-notice-text'); return !${DIALOG} && !!n && n.textContent === ${json(GONE)} && !rowOf(__vars.rows.c) && ${P}.requests.slice(window.__mark).some(r => r.method === 'GET' && r.path === '/UserRatings/Admin/Detached' && r.status === 200); })()`, timeout: 15000 },
    since('gone', 'goneRequests'),
    { name: 'gone: the Relink answered 404', assert: "__vars.goneRequests.some(x => x.method === 'POST' && x.path === '/UserRatings/Admin/Detached/' + __vars.attrC + '/Relink' && x.status === 404)" },
    { name: 'gone: screenshot of the note', screenshot: 'userratings-detached-gone.png' },

    // Purge….
    mark('purge'),
    { name: 'purge: Purge… on viewer2\'s other rating', click: `${DET} [data-jfur-rating="\${attrB}"] .jfur-det-purge` },
    dialogOpen('purge'),
    dialogText('purge', 'purgeQuestion'),
    { name: 'purge: the question: for good, never back from an import, a snapshot first', assert: "__vars.purgeQuestion.text.indexOf('Delete the rating of viewer2 on ' + __vars.title + ' for good? It can\\'t come back from an import; a snapshot is taken first.') >= 0 && __vars.purgeQuestion.markup === 0" },
    { name: 'purge: screenshot of the question', screenshot: 'userratings-detached-purge-question.png' },
    confirm('purge'),
    { name: 'purge: purged: the toast, the row gone, the earlier note cleared', waitFor: `(() => { ${ROW_OF} return !${DIALOG} && ${toast('The rating was purged.')} && !rowOf(__vars.rows.b) && !document.querySelector('${DET} .jfur-det-notice .jfur-notice'); })()`, timeout: 15000 },
    since('purge', 'purgeRequests'),
    { name: 'purge: one DELETE of it; 204', assert: "(() => { const d = __vars.purgeRequests.filter(x => x.method === 'DELETE'); return d.length === 1 && d[0].path === '/UserRatings/Admin/Detached/' + __vars.attrB && d[0].status === 204; })()" },
    { name: 'purge: the focus not lost to the page\'s body', assert: NOT_BODY },
    detachedNow('purge', 'afterPurge'),
    { name: 'purge: none of the three detached any more', assert: '__vars.afterPurge.ours.length === 0' },
    { name: 'purge: a dry run of the same file', script: IMPORT_ROWS(true), save: 'again', full: true },
    { name: 'purge: both purged ratings skipped as purged before (PreviouslyPurged, not SkippedDeleted), nothing stored detached or added: a purge is for good', assert: '__vars.again.unusable === 0 && __vars.again.dry.PreviouslyPurged === 2 && !__vars.again.dry.SkippedDeleted && !__vars.again.dry.StoredDetached && !__vars.again.dry.Added' },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    sweep('at the end'),
    noneLeft,
    {
        name: 'clean: none of the run\'s ratings left detached',
        script: `${NORM} const r = await ${P}.server('GET', '${ADMIN}/Detached?limit=50'); const left = r.json.Items.filter(x => [__vars.rows.a, __vars.rows.b, __vars.rows.c].indexOf(norm(x.Id)) >= 0); for (const x of left) { await ${P}.server('DELETE', '${ADMIN}/Detached/' + x.Id); } return left.length;`,
        save: 'leftDetached'
    },
    { name: 'clean: none was', assert: '__vars.leftDetached === 0' }
];

write('userratings-detached-desktop.json', `User Ratings ${VERSION}, detached ratings: in the default sandbox as admin, with viewer in their own window, the desktop layout: three detached ratings made by importing a synthetic export by id (the sandbox's own export with its ratings replaced: viewer's with a review, two of viewer2's, on titles the library lacks, named as Long Test Movie, detached as removed, missing and not matched); the Data tab's detached list shows them oldest first, each title and name as isolated text, the member, stars and review, why and when, Long Test Movie to go back to by how it matches, and Purge… named for it; a reason reads the list for it alone; Relink asks first (Cancel focused), then gives viewer's rating back to Long Test Movie (POST 204, the list read again, viewer sees their stars and review on it); one purged meanwhile through the API: its Relink is the 404, whose note stays while the list is read again without it; Purge… asks first, then deletes the last for good (DELETE 204, the note cleared, the focus kept); none of the three detached any more, and a dry run of the same file skips both purged ratings as purged before (PreviouslyPurged). Every rating on Long Test Movie removed at the end, and none of the run's ratings left detached.`, detached);

// ---------------------------------------------------------------------------------------------------------- Jellyfin Enhanced

// userratings-data-je-desktop.json: the je sandbox, as admin, with the synthetic
// reviews.json that userratings-je-import-probe.mjs --webchecks writes first. The banner above the tabs; the Data tab's
// "Import from Jellyfin Enhanced" section and its switch-over checklist (every JE review setting, read only); a dry run
// (POST 202, polled), its report as text without the review text; the JE member who isn't a user here given to viewer2,
// the dry run again; Import asks first (Cancel focused), then imports with the dry run's PlanToken; the report names the
// import's snapshot, which the run restores at the end through the API (the import undone).
const JE = (cls) => `document.querySelector('${ROOT} .jfur-data-je-section .${cls}')`;
const JE_REPORT = JE('jfur-data-je-report');
const JE_KEYS = 'ShowUserReviews,ShowReviews,ShowAnilistReviews,ReviewTranslationEnabled,ReviewsExpandedByDefault,MirrorReviewRatingsToJellyfin,HideReviewsFromHiddenUsers,HideReviewsFromDisabledUsers,ActivityFeedShowReviewed,SpoilerStripReviews,ShowUserRatingOnPosters';
const jeDesktop = [
    ...signIn('admin'),
    ids,
    ...openTab(),
    { name: 'the kit\'s Jellyfin Enhanced banner above the tabs, as text', assert: `Array.from(document.querySelectorAll('#UserRatingsBanners .userRatingsBanner')).some(b => b.textContent.indexOf(${json('Jellyfin Enhanced\'s User Reviews is on.')}) === 0 && b.children.length === 0)` },
    {
        name: 'the switch-over checklist: every JE 12.14 review setting, in order, as the sandbox\'s JE has them',
        eval: `Array.from(document.querySelectorAll('${ROOT} .jfur-data-je-item')).map(li => ({ key: li.getAttribute('data-jfur-key'), done: li.getAttribute('data-jfur-done'), state: li.querySelector('.jfur-data-je-state').textContent }))`,
        save: 'checklist',
        full: true
    },
    { name: 'eleven items; user reviews on, so To do; translation off with no URL or key, so Done; the TMDB section Optional; the activity feed off (JE\'s default), so Show recently reviewed Done (0.3.22); none Unknown; Use the same hiding rules shown', assert: `__vars.checklist.map(i => i.key).join(',') === ${json(JE_KEYS)} && __vars.checklist[0].state === 'To do: ' && __vars.checklist[3].state === 'Done: ' && __vars.checklist[1].state === 'Optional: ' && __vars.checklist[8].state === 'Done: ' && __vars.checklist.every(i => i.state !== 'Unknown: ') && document.querySelectorAll('${ROOT} .jfur-data-je-problems').length === 0 && ${shown(JE('jfur-data-je-hiding'))}` },
    { name: 'Import waits for a dry run', assert: `${JE('jfur-data-je-import')}.disabled && !${JE('jfur-data-je-dryrun')}.disabled` },
    { name: 'screenshot: the Jellyfin Enhanced section', screenshot: 'userratings-data-je-desktop.png' },
    mark('dry'),
    { name: 'dry: Dry run', click: `${ROOT} .jfur-data-je-dryrun` },
    { name: 'dry: the report', waitFor: `${JE_REPORT}.textContent.indexOf(${json('Dry run, nothing was changed. Jellyfin Enhanced\'s file has 8 reviews.')}) === 0`, timeout: 60000 },
    { name: 'dry: the counts, the field names, the JE member to map', eval: `({ counts: Array.from(${JE_REPORT}.querySelectorAll('[data-jfur-count]')).map(li => li.getAttribute('data-jfur-count')), fields: ${JE_REPORT}.querySelector('.jfur-data-je-fields').textContent, unknown: document.querySelectorAll('${ROOT} [data-jfur-je-user]').length, text: document.querySelector('${ROOT}').textContent.indexOf('jeimp-') >= 0, markup: ${JE_REPORT}.querySelectorAll('b, i').length })`, save: 'dry', full: true },
    { name: 'dry: new ratings, a review stored detached, one left out and one clamped; JE\'s field names; one JE member to map; no review text anywhere on the page', assert: `['Created', 'StoredDetached', 'Invalid', 'Clamped'].every(k => __vars.dry.counts.indexOf(k) >= 0) && __vars.dry.fields === ${json('Fields in Jellyfin Enhanced\'s file: UserId, TmdbId, MediaType, Content, Rating, CreatedAt, UpdatedAt')} && __vars.dry.unknown === 1 && !__vars.dry.text && __vars.dry.markup === 0` },
    since('dry', 'dryRequests'),
    { name: 'dry: one POST of the import, a dry run', assert: "(() => { const p = __vars.dryRequests.filter(x => x.method === 'POST'); return p.length === 1 && p[0].path === '/UserRatings/Admin/Import/JellyfinEnhanced' && p[0].status === 202 && /dryRun=true/.test(JSON.stringify(p[0].query || '')); })()" },
    { name: 'map: the JE member given to viewer2', script: `const b = Array.from(document.querySelectorAll('${ROOT} [data-jfur-je-user] .jfur-data-choice')).find(x => x.textContent === 'viewer2'); if (!b) { throw new Error('no viewer2 choice'); } b.click(); return true;` },
    { name: 'map: Import waits for another dry run', assert: `${JE('jfur-data-je-import')}.disabled` },
    { name: 'map: Dry run', click: `${ROOT} .jfur-data-je-dryrun` },
    { name: 'map: the report again, Import on', waitFor: `${JE_REPORT}.textContent.indexOf('Dry run, nothing was changed.') === 0 && !${JE('jfur-data-je-import')}.disabled`, timeout: 60000 },
    mark('import'),
    { name: 'import: Import', click: `${ROOT} .jfur-data-je-import` },
    dialogOpen('import'),
    dialogText('import', 'jeQuestion'),
    { name: 'import: the question: a snapshot first, its restore the undo', assert: `__vars.jeQuestion.text.indexOf(${json('Import Jellyfin Enhanced\'s reviews as the dry run showed? A snapshot of the ratings as they are now is taken first; restoring it undoes the import.')}) >= 0` },
    confirm('import'),
    { name: 'import: imported, the report names the import\'s snapshot', waitFor: `!${DIALOG} && ${JE_REPORT}.textContent.indexOf('Imported.') === 0 && ${JE_REPORT}.textContent.indexOf('The ratings from just before were kept as userratings-import-') >= 0`, timeout: 60000 },
    { name: 'import: the snapshot\'s name', eval: `/kept as (userratings-import-[^ ]+[.]db)/.exec(${JE_REPORT}.textContent)[1]`, save: 'jeSnapshot' },
    since('import', 'importRequests'),
    { name: 'import: one real run with the dry run\'s PlanToken', assert: "(() => { const p = __vars.importRequests.filter(x => x.method === 'POST'); const q = JSON.stringify(p.length ? p[0].query : ''); return p.length === 1 && /dryRun=false|\"dryRun\":\"false\"/.test(q) && /ifPlan=[0-9a-f]{32}|\"ifPlan\":\"[0-9a-f]{32}\"/.test(q) && p[0].status === 202; })()" },
    { name: 'import: screenshot of the report', screenshot: 'userratings-data-je-imported.png' },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    { name: 'clean: the import undone: its snapshot restored through the API', script: `const r = await ${P}.server('POST', '${ADMIN}/Snapshots/Restore?name=' + encodeURIComponent(__vars.jeSnapshot)); if (r.status !== 200) { throw new Error('restore ' + r.status); } return r.status;` }
];

write('userratings-data-je-desktop.json', `User Ratings ${VERSION}, the Jellyfin Enhanced import: in the je sandbox as admin, the desktop layout, with the synthetic reviews.json of userratings-je-import-probe.mjs --webchecks: the kit's Jellyfin Enhanced banner above the tabs, as text; the Data tab's "Import from Jellyfin Enhanced" section with its switch-over checklist (every JE 12.14 review setting in order: user reviews To do, translation Done, the TMDB section Optional) and Use the same hiding rules; Import waits for a dry run; the dry run (one POST 202, polled) and its report as text: new ratings, one stored detached, one left out, one clamped, JE's field names, one JE member to map, no review text anywhere on the page; that member given to viewer2 and the dry run again; Import asks first (Cancel focused), then imports with the dry run's PlanToken; the report names the import's snapshot, which the run restores at the end through the API (the import undone).`, jeDesktop);
