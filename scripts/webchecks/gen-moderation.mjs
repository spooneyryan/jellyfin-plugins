// Generates User Ratings' moderation webchecks over
// probes/userratings-details-probe.js (its request log and its server(), a request of the signed-in member's own):
//   userratings-moderation-desktop.json  default, admin, desktop: the settings page's Moderation tab with three members'
//                                        reviews (viewer's two, viewer2's one and a synthetic member's with a long
//                                        right-to-left name): every value as text and isolated, the search, Clear
//                                        filters, a member's "Only" and its chip; Remove text only after its dry run
//                                        (the token sent back, the reason, the stars kept), Remove rating and review,
//                                        a stale token's 409 (the member edits between the dry run and Remove: the row
//                                        read again and shown as it is now, Cancel focused, then the dry run again, then
//                                        the removal), Escape back to the opener; the
//                                        log's rows
//   userratings-moderation-tv.json       default, admin, TV layout: the Moderation tab with the D-pad: OK on Remove…
//                                        opens the dialog on Cancel, Up and Down stay inside it (no wrap, no way out),
//                                        Back closes it with the focus back on its opener, a removal by the D-pad leaves
//                                        the focus on the list, never the page's body
//   userratings-moderation-phone.json    default, admin, 375x812 with touch: the tab's rows as cards with their column's
//                                        name, no sideways scroll, every control and the dialog's at least 44 px high,
//                                        the long right-to-left name isolated and inside the screen
//   userratings-remove-desktop.json      default, the details page's "Remove…" (viewer: none on any review; admin: on
//                                        viewer's and viewer2's reviews, never on the admin's own): Remove text only,
//                                        then Remove rating and review, each after its dry run, the summary, the chip
//                                        and the Rate button as before
//   userratings-remove-experimental.json default, the same in jellyfin-web's experimental layout
//   userratings-remove-je.json           je, the same on Tagged Movie beside Jellyfin Enhanced's Reviews
// Every webcheck removes what it wrote at the end (the admin's moderation API on every rating of its items, then the
// synthetic member deleted) and checks the items have no rating left.
// Run from the repository root: node scripts/webchecks/gen-moderation.mjs [--out <folder>]
// Repo.Tests compares the files with what this writes. scripts/README.md says how to run them.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { VERSION } from './userratings-version.mjs';

const outIndex = process.argv.indexOf('--out');
const folder = outIndex > 0 ? path.resolve(process.argv[outIndex + 1]) : path.dirname(fileURLToPath(import.meta.url));
const IGNORE = '/CustomRatings/script|^Error: player cannot be null';
const P = '__jfurDetails';
const ROOT = '#UserRatingsModerationRoot';
const MOVIE = (name) => `(await ApiClient.getItems(ApiClient.getCurrentUserId(), { IncludeItemTypes: 'Movie', Recursive: true, SearchTerm: ${JSON.stringify(name)}, Limit: 1 })).Items[0].Id`;
const DRAWN = (id) => `(() => { const p = document.querySelector('#itemDetailPage:not(.hide)'); return !!p && location.hash.indexOf(__vars.${id}) > 0 && !!p.querySelector('.mainDetailButtons .btnPlay:not(.hide)'); })()`;
const MOUNTED = (id) => `(() => { const s = ${P}.section(); return !!s && s.getAttribute('data-jfur-item') === __vars.${id} && !!s.querySelector('.jfur-summary[data-jfur-summary]'); })()`;
// The kit's dialog (its panel): Jellyfin Enhanced's page has a hidden [role="dialog"][aria-modal="true"] of its own
// (#pause-screen-overlay), first in the document.
const DIALOG = `document.querySelector('.jfur-panel[role="dialog"][aria-modal="true"]')`;
const NOT_BODY = `!!document.activeElement && document.activeElement !== document.body && document.activeElement.isConnected`;

// The synthetic reviews: words to find, a markup-looking part that must stay text, and right-to-left words.
const TEXT_A = 'mod webcheck review A: the lighthouse keeper <b>never bold</b> & counts the ships.';
const TEXT_B = 'mod webcheck review B: the ferry leaves at nine.';
const TEXT_B2 = 'mod webcheck review B, edited after the dry run: the ferry leaves at ten.';
const TEXT_C = 'mod webcheck review C: מגדלור על החוף, the tide comes in.';
const TEXT_R = 'mod webcheck review R: مراجعة قصيرة من عضو تجريبي.';
// A long right-to-left member name (Arabic: "a test member with a very long name to check the isolation in the moderation
// list"); Jellyfin's user names take letters of any script and spaces.
const RTL_NAME = 'عضو تجريبي باسم طويل جدا للتحقق من العزل في قائمة الإشراف rtl9';

function write(name, title, steps) {
    const doc = { name: title, failOnException: false, timeout: 30000, steps };
    fs.writeFileSync(path.join(folder, name), JSON.stringify(doc, null, 2) + '\n', { encoding: 'utf8' });
}

const json = (value) => JSON.stringify(value);
const id = (expr) => `String(${expr}).replace(/-/g, '').toLowerCase()`;

// A member's rating and review of an item, through the API with their own session.
const rate = (label, item, stars, text) => ({
    name: `${label}: stars and a review through the API`,
    script: `const a = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.${item} + '/Mine/Rating', { Stars: ${stars} }); const b = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.${item} + '/Mine/Review', { Text: ${json(text)}, IsSpoiler: false }); if (a.status !== 200 || b.status !== 200) { throw new Error('rating ' + a.status + ', review ' + b.status); } return [a.status, b.status];`
});

// Every rating on the items, through the admin's moderation API (Remove rating and review, its dry run first): what an
// earlier run left before, everything this one wrote after.
const sweep = (label, items) => ({
    name: `clean: ${label}: every rating on ${items.join(' and ')} removed through the moderation API`,
    script: `const removed = []; for (const item of [${items.map((i) => '__vars.' + i).join(', ')}]) { const page = await ${P}.server('GET', 'UserRatings/Admin/Reviews?itemId=' + item + '&limit=200'); if (page.status !== 200) { throw new Error('list ' + page.status); } for (const row of page.json.Items) { const path = 'UserRatings/Admin/Reviews/' + row.RatingId + '?textOnly=false'; const dry = await ${P}.server('DELETE', path); const done = await ${P}.server('DELETE', path + '&dryRun=false&ifPreview=' + dry.json.PreviewToken); removed.push(done.status); } } return removed;`,
    save: 'swept'
});

const noneLeft = (items) => ({
    name: `clean: ${items.join(' and ')} have no rating left`,
    script: `for (const item of [${items.map((i) => '__vars.' + i).join(', ')}]) { const page = await ${P}.server('GET', 'UserRatings/Admin/Reviews?itemId=' + item); if (page.status !== 200 || page.json.TotalRecordCount !== 0) { throw new Error('left: ' + page.status + ' ' + (page.json && page.json.TotalRecordCount)); } } return true;`
});

// The synthetic member with the long right-to-left name: created by the admin with a random password that never leaves
// the page, signed in once by a request of its own (no browser session), its stars and review written, the session ended.
const rtlMember = (item) => ({
    name: 'the synthetic member with a long right-to-left name: created, its stars and review written through its own session (the password random, kept nowhere)',
    script: `const old = (await ApiClient.getUsers()).filter(u => u.Name === ${json(RTL_NAME)}); for (const u of old) { await ApiClient.deleteUser(u.Id); } const pw = Array.from(crypto.getRandomValues(new Uint8Array(18)), b => b.toString(16).padStart(2, '0')).join(''); const user = await ApiClient.createUser({ Name: ${json(RTL_NAME)}, Password: pw }); const head = 'MediaBrowser Client="mod-webcheck", Device="webcheck", DeviceId="mod-webcheck-' + Date.now() + '", Version="1.0.0"'; const auth = await fetch(ApiClient.getUrl('Users/AuthenticateByName'), { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: head }, body: JSON.stringify({ Username: ${json(RTL_NAME)}, Pw: pw }) }); if (auth.status !== 200) { throw new Error('sign-in ' + auth.status); } const token = (await auth.json()).AccessToken; const put = (p, b) => fetch(ApiClient.getUrl(p), { method: 'PUT', headers: { 'Content-Type': 'application/json', Authorization: head + ', Token="' + token + '"' }, body: JSON.stringify(b) }).then(r => r.status); const s1 = await put('UserRatings/Items/' + __vars.${item} + '/Mine/Rating', { Stars: 2.5 }); const s2 = await put('UserRatings/Items/' + __vars.${item} + '/Mine/Review', { Text: ${json(TEXT_R)}, IsSpoiler: false }); await fetch(ApiClient.getUrl('Sessions/Logout'), { method: 'POST', headers: { Authorization: head + ', Token="' + token + '"' } }); if (s1 !== 200 || s2 !== 200) { throw new Error('rating ' + s1 + ', review ' + s2); } return { id: user.Id, statuses: [s1, s2] };`,
    save: 'rtl'
});

const deleteRtlMember = {
    name: 'clean: the synthetic member deleted',
    script: `const users = (await ApiClient.getUsers()).filter(u => u.Name === ${json(RTL_NAME)}); for (const u of users) { await ApiClient.deleteUser(u.Id); } return users.length;`
};

// The rating ids of an item's rows as the admin's API lists them, by author.
const ratingIds = (item, save) => ({
    name: `the rating ids on ${item}, by author`,
    script: `const page = await ${P}.server('GET', 'UserRatings/Admin/Reviews?itemId=' + __vars.${item}); const out = {}; for (const row of page.json.Items) { out[row.UserName] = ${id('row.RatingId')}; } return out;`,
    save,
    full: true
});

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

// viewer's two reviews and viewer2's one, in their own browser contexts, and the synthetic member's.
function seed(items) {
    return [
        { context: 'viewer' },
        ...signIn('viewer'),
        { name: 'viewer: Long Test Movie', eval: MOVIE('Long Test Movie'), save: 'movieId' },
        { name: 'viewer: Tagged Movie', eval: MOVIE('Tagged Movie'), save: 'taggedId' },
        rate('viewer on Long Test Movie', 'movieId', 3.5, TEXT_A),
        rate('viewer on Tagged Movie', 'taggedId', 4, TEXT_B),
        { context: 'viewer2' },
        ...signIn('viewer2'),
        rate('viewer2 on Long Test Movie', 'movieId', 4.5, TEXT_C),
        { context: 'main' },
        rtlMember('movieId'),
        ratingIds('movieId', 'onMovie'),
        ratingIds('taggedId', 'onTagged'),
        { name: 'four rows: viewer\'s two, viewer2\'s and the synthetic member\'s', assert: `!!__vars.onMovie.viewer && !!__vars.onMovie.viewer2 && !!__vars.onMovie[${json(RTL_NAME)}] && !!__vars.onTagged.viewer && Object.keys(__vars.onMovie).length === 3 && Object.keys(__vars.onTagged).length === 1`, message: `the items had other ratings (${items.join(', ')})` },
        { name: 'viewer\'s rating of Long Test Movie', eval: '__vars.onMovie.viewer', save: 'rA' },
        { name: 'viewer\'s rating of Tagged Movie', eval: '__vars.onTagged.viewer', save: 'rB' },
        { name: 'viewer2\'s rating of Long Test Movie', eval: '__vars.onMovie.viewer2', save: 'rC' },
        { name: 'the synthetic member\'s rating of Long Test Movie', eval: `__vars.onMovie[${json(RTL_NAME)}]`, save: 'rtlId' }
    ];
}

// The admin, the items' ids, and nothing left on them from another run.
function adminStart(layout, emulate) {
    return [
        ...signIn('admin', layout, emulate),
        { name: 'Long Test Movie', eval: MOVIE('Long Test Movie'), save: 'movieId' },
        { name: 'Tagged Movie', eval: MOVIE('Tagged Movie'), save: 'taggedId' },
        sweep('before the run', ['movieId', 'taggedId']),
        deleteRtlMember
    ];
}

function openTab() {
    return [
        { name: 'open User Ratings\' settings page', goto: '/web/index.html#/configurationpage?name=UserRatings' },
        { name: 'the page and its tabs', waitFor: "!!document.querySelector('#UserRatingsConfigPage:not(.hide) #UserRatingsTabs')", timeout: 30000 },
        { name: 'mark', script: `window.__mark = ${P}.requests.length; return true;` },
        { name: 'open the Moderation tab', click: '#UserRatingsTabModeration' },
        { name: 'the tab\'s module mounted, the four rows listed', waitFor: `document.querySelectorAll('${ROOT} .jfur-mod-row').length >= 4 && !!document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rA + '"]')`, timeout: 20000 },
        { name: 'its first requests: the list and the log, nothing written', eval: `${P}.requests.slice(window.__mark).map(r => r.method + ' ' + r.path + r.query)`, save: 'firstRequests', full: true },
        { name: 'GETs only', assert: "__vars.firstRequests.length >= 2 && __vars.firstRequests.every(r => r.indexOf('GET ') === 0) && __vars.firstRequests.some(r => r.indexOf('/UserRatings/Admin/Reviews') > 0) && __vars.firstRequests.some(r => r.indexOf('/UserRatings/Admin/ModerationLog') > 0)" }
    ];
}

// The rows as shown: each cell's text, the isolated parts and their direction, and any element a review's markup made.
const rowsShown = {
    name: 'the rows as shown',
    eval: `Array.from(document.querySelectorAll('${ROOT} .jfur-mod-row')).map(r => ({ id: r.getAttribute('data-jfur-rating'), name: (r.querySelector('.jfur-mod-name') || {}).textContent, title: (r.querySelector('.jfur-mod-title') || {}).textContent, stars: (r.querySelector('.jfur-mod-stars') || {}).textContent, text: (r.querySelector('.jfur-mod-text') || {}).textContent || null, none: !!r.querySelector('.jfur-mod-none'), isolated: Array.from(r.querySelectorAll('.jfur-mod-name, .jfur-mod-title, .jfur-mod-text')).every(e => e.getAttribute('dir') === 'auto' && getComputedStyle(e).unicodeBidi === 'isolate'), markup: r.querySelectorAll('.jfur-mod-text *').length }))`,
    save: 'rows',
    full: true
};

// ---------------------------------------------------------------------------------------------------------- desktop

const desktop = [
    ...adminStart(),
    ...seed(['Long Test Movie', 'Tagged Movie']),
    ...openTab(),
    rowsShown,
    {
        name: 'every value as text: the names, the titles (the year once), the stars and the reviews; the markup-looking part a text, no element; names, titles and reviews isolated (dir="auto", unicode-bidi: isolate)',
        assert: `(() => { const by = (rid) => __vars.rows.find(x => x.id === rid); const a = by(__vars.rA), b = by(__vars.rB), c = by(__vars.rC), r = by(__vars.rtlId); return !!a && !!b && !!c && !!r && a.text === ${json(TEXT_A)} && a.markup === 0 && a.name === 'viewer' && a.title.indexOf('Long Test Movie') === 0 && a.title.split('(2021)').length === 2 && a.stars === '3.5 out of 5' && b.text === ${json(TEXT_B)} && b.title.indexOf('Tagged Movie') === 0 && b.title.split('(2012)').length === 2 && c.text === ${json(TEXT_C)} && c.name === 'viewer2' && r.name === ${json(RTL_NAME)} && r.text === ${json(TEXT_R)} && __vars.rows.every(x => x.isolated); })()`
    },
    { name: 'screenshot: the Moderation tab', screenshot: 'userratings-moderation-desktop.png' },
    { name: 'the search box', click: `${ROOT} .jfur-searchbox` },
    { name: 'search for "lighthouse"', type: 'lighthouse' },
    { name: 'only viewer\'s review of Long Test Movie matches', waitFor: `(() => { const rows = document.querySelectorAll('${ROOT} .jfur-mod-row'); return rows.length === 1 && rows[0].getAttribute('data-jfur-rating') === __vars.rA; })()`, timeout: 10000 },
    { name: 'the search went to the server', assert: `${P}.requests.slice(window.__mark).some(r => r.method === 'GET' && r.path === '/UserRatings/Admin/Reviews' && r.query.indexOf('searchTerm=lighthouse') >= 0)` },
    { name: 'Clear filters', click: `${ROOT} .jfur-mod-clear` },
    { name: 'the four rows again, the search box empty', waitFor: `document.querySelectorAll('${ROOT} .jfur-mod-row').length >= 4 && document.querySelector('${ROOT} .jfur-searchbox').value === ''`, timeout: 10000 },
    { name: 'viewer\'s "Only viewer"', click: `${ROOT} .jfur-mod-row[data-jfur-rating="\${rA}"] .jfur-mod-member .jfur-mod-only` },
    { name: 'viewer\'s two rows only, the chip "Member: viewer"', waitFor: `(() => { const rows = Array.from(document.querySelectorAll('${ROOT} .jfur-mod-row')); const chip = document.querySelector('${ROOT} .jfur-mod-chip'); return rows.length === 2 && rows.every(r => r.querySelector('.jfur-mod-name').textContent === 'viewer') && !!chip && chip.textContent.indexOf('Member: viewer') >= 0; })()`, timeout: 10000 },
    { name: 'the chip removes the filter', click: `${ROOT} .jfur-mod-chip` },
    { name: 'the four rows again, no chip', waitFor: `document.querySelectorAll('${ROOT} .jfur-mod-row').length >= 4 && !document.querySelector('${ROOT} .jfur-mod-chip')`, timeout: 10000 },

    // Remove text only on viewer's review of Long Test Movie.
    { name: 'text only: mark', script: `window.__mark = ${P}.requests.length; return true;` },
    { name: 'text only: Remove… on viewer\'s Long Test Movie row', click: `${ROOT} .jfur-mod-row[data-jfur-rating="\${rA}"] .jfur-mod-remove` },
    { name: 'text only: the dialog, Remove text only chosen, its dry run\'s sentence, Remove enabled', waitFor: `(() => { const d = ${DIALOG}; return !!d && !!d.querySelector('.jfur-mod-radio[value="text"]:checked') && d.querySelector('.jfur-mod-preview').textContent.indexOf('The review goes; the stars stay (3.5 out of 5).') === 0 && !d.querySelector('.jfur-mod-confirm').disabled; })()`, timeout: 10000 },
    { name: 'text only: the focus on Cancel (nothing removed by an early Enter)', assert: `document.activeElement === ${DIALOG}.querySelector('.jfur-mod-cancel')` },
    { name: 'text only: the dialog names the member and the title, isolated', assert: `(() => { const d = ${DIALOG}; const n = d.querySelector('.jfur-mod-by .jfur-mod-name'), t = d.querySelector('.jfur-mod-by .jfur-mod-title'); return n.textContent === 'viewer' && t.textContent.indexOf('Long Test Movie') === 0 && getComputedStyle(n).unicodeBidi === 'isolate'; })()` },
    { name: 'text only: one dry run so far, nothing removed', eval: `${P}.requests.slice(window.__mark).map(r => r.method + ' ' + r.path + r.query)`, save: 'dry', full: true },
    { name: 'text only: a DELETE without dryRun=false', assert: "__vars.dry.length === 1 && /^DELETE \\/UserRatings\\/Admin\\/Reviews\\/[0-9a-f]{32}\\?textOnly=true$/.test(__vars.dry[0])" },
    { name: 'text only: the reason "Spoiler"', script: `const s = ${DIALOG}.querySelector('.jfur-mod-reason'); s.value = 'Spoiler'; s.dispatchEvent(new Event('change', { bubbles: true })); return s.value;` },
    { name: 'text only: screenshot of the dialog', screenshot: 'userratings-moderation-dialog.png' },
    { name: 'text only: Remove', click: '[role="dialog"] .jfur-mod-confirm' },
    { name: 'text only: the dialog closed, the row with its stars and "No review"', waitFor: `!${DIALOG} && (() => { const r = document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rA + '"]'); return !!r && !!r.querySelector('.jfur-mod-none') && r.querySelector('.jfur-mod-stars').textContent === '3.5 out of 5'; })()`, timeout: 15000 },
    { name: 'text only: the focus on the list, never the page\'s body', assert: NOT_BODY },
    { name: 'text only: the real run sent the dry run\'s token and the reason', eval: `${P}.requests.slice(window.__mark).map(r => r.method + ' ' + r.path + r.query + ' ' + r.status)`, save: 'real', full: true },
    { name: 'text only: dryRun=false with ifPreview=m1-…, 200', assert: "__vars.real.some(r => /^DELETE \\/UserRatings\\/Admin\\/Reviews\\/[0-9a-f]{32}\\?textOnly=true&dryRun=false&ifPreview=m1-[0-9a-f]{20}&reason=Spoiler 200$/.test(r))" },
    { name: 'text only: the server keeps viewer\'s stars and no review', script: `const p = await ${P}.server('GET', 'UserRatings/Admin/Reviews?itemId=' + __vars.movieId); const row = p.json.Items.find(x => ${id('x.RatingId')} === __vars.rA); return { stars: row.Stars, text: row.Text === undefined ? null : row.Text };`, save: 'afterText', full: true },
    { name: 'text only: 3.5 stars, no text', assert: '__vars.afterText.stars === 3.5 && __vars.afterText.text === null' },
    { name: 'text only: the log\'s newest row', waitFor: `(() => { const r = document.querySelector('${ROOT} .jfur-mod-log-row'); return !!r && r.textContent.indexOf('Removed a review') >= 0 && r.textContent.indexOf('Spoiler') >= 0 && r.textContent.indexOf('viewer') >= 0; })()`, timeout: 10000 },
    { name: 'text only: the log has no word of the review', assert: `document.querySelector('${ROOT} .jfur-mod-log-row').textContent.indexOf('lighthouse') < 0` },

    // Remove rating and review on viewer2's.
    { name: 'entry: mark', script: `window.__mark = ${P}.requests.length; return true;` },
    { name: 'entry: Remove… on viewer2\'s row', click: `${ROOT} .jfur-mod-row[data-jfur-rating="\${rC}"] .jfur-mod-remove` },
    { name: 'entry: the dialog and its first dry run', waitFor: `(() => { const d = ${DIALOG}; return !!d && !d.querySelector('.jfur-mod-confirm').disabled; })()`, timeout: 10000 },
    { name: 'entry: choose Remove rating and review', click: '[role="dialog"] .jfur-mod-radio[value="entry"]' },
    { name: 'entry: its own dry run\'s sentence, Remove enabled', waitFor: `(() => { const d = ${DIALOG}; return !!d && d.querySelector('.jfur-mod-preview').textContent.indexOf('The rating and the review go.') === 0 && !d.querySelector('.jfur-mod-confirm').disabled; })()`, timeout: 10000 },
    { name: 'entry: Remove', click: '[role="dialog"] .jfur-mod-confirm' },
    { name: 'entry: the dialog closed, viewer2\'s row gone', waitFor: `!${DIALOG} && !document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rC + '"]')`, timeout: 15000 },
    { name: 'entry: the focus on the list, never the page\'s body', assert: NOT_BODY },
    { name: 'entry: the requests: two dry runs, then the real run with the second one\'s kind', eval: `${P}.requests.slice(window.__mark).filter(r => r.method === 'DELETE').map(r => r.query + ' ' + r.status)`, save: 'entry', full: true },
    { name: 'entry: textOnly=true, textOnly=false, then textOnly=false&dryRun=false&ifPreview=…', assert: "__vars.entry.length === 3 && __vars.entry[0] === '?textOnly=true 200' && __vars.entry[1] === '?textOnly=false 200' && /^\\?textOnly=false&dryRun=false&ifPreview=m1-[0-9a-f]{20} 200$/.test(__vars.entry[2])" },
    { name: 'entry: the server has no rating of viewer2 there', script: `const p = await ${P}.server('GET', 'UserRatings/Admin/Reviews?itemId=' + __vars.movieId); return p.json.Items.filter(x => ${id('x.RatingId')} === __vars.rC).length;`, save: 'left' },
    { name: 'entry: none', assert: '__vars.left === 0' },

    // A stale token: viewer edits the review between the dry run and Remove.
    { name: 'stale: mark', script: `window.__mark = ${P}.requests.length; return true;` },
    { name: 'stale: Remove… on viewer\'s Tagged Movie row', click: `${ROOT} .jfur-mod-row[data-jfur-rating="\${rB}"] .jfur-mod-remove` },
    { name: 'stale: the dry run, Remove enabled', waitFor: `(() => { const d = ${DIALOG}; return !!d && !d.querySelector('.jfur-mod-confirm').disabled; })()`, timeout: 10000 },
    { context: 'viewer' },
    { name: 'stale: viewer edits the review meanwhile', script: `const r = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.taggedId + '/Mine/Review', { Text: ${json(TEXT_B2)}, IsSpoiler: false }); return r.status;`, save: 'edit' },
    { context: 'main' },
    { name: 'stale: Remove', click: '[role="dialog"] .jfur-mod-confirm' },
    { name: 'stale: the 409 says "changed", the row read again and shown as it is now, then the dry run again; the dialog open, Remove enabled again', waitFor: `(() => { const d = ${DIALOG}; return !!d && d.querySelector('.jfur-mod-problem').textContent.indexOf('This rating changed after it was checked, so nothing was removed. It is shown below as it is now') >= 0 && !!d.querySelector('.jfur-mod-now-text') && d.querySelector('.jfur-mod-now').getClientRects().length > 0 && !d.querySelector('.jfur-mod-confirm').disabled; })()`, timeout: 15000 },
    { name: 'stale: the words shown are the edited ones, as text, isolated', assert: `(() => { const t = ${DIALOG}.querySelector('.jfur-mod-now-text'); return t.textContent === ${json(TEXT_B2)} && t.getAttribute('dir') === 'auto'; })()` },
    { name: 'stale: Cancel focused, a live control while Remove waited', assert: `document.activeElement === ${DIALOG}.querySelector('.jfur-mod-cancel')` },
    { name: 'stale: the order: the row read again before the new dry run', eval: `${P}.requests.slice(window.__mark).filter(r => r.method === 'DELETE' || (r.method === 'GET' && /[?&]userId=/.test(r.query) && r.path === '/UserRatings/Admin/Reviews')).map(r => r.method + ' ' + r.status)`, save: 'order', full: true },
    { name: 'stale: dry run, the 409, the read, the dry run', assert: "JSON.stringify(__vars.order) === JSON.stringify(['DELETE 200', 'DELETE 409', 'GET 200', 'DELETE 200'])" },
    { name: 'stale: the server still has the edited review', script: `const p = await ${P}.server('GET', 'UserRatings/Admin/Reviews?itemId=' + __vars.taggedId); return p.json.Items[0].Text;`, save: 'stillThere' },
    { name: 'stale: the edited text, nothing removed', assert: `__vars.stillThere === ${json(TEXT_B2)}` },
    { name: 'stale: Remove again, with the new dry run\'s token', click: '[role="dialog"] .jfur-mod-confirm' },
    { name: 'stale: the dialog closed, the row with "No review"', waitFor: `!${DIALOG} && !!document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rB + '"] .jfur-mod-none')`, timeout: 15000 },
    { name: 'stale: the requests', eval: `${P}.requests.slice(window.__mark).filter(r => r.method === 'DELETE').map(r => r.query.replace(/m1-[0-9a-f]{20}/, 'm1-…') + ' ' + r.status)`, save: 'stale', full: true },
    { name: 'stale: a dry run, the 409, the dry run again, the 200', assert: "JSON.stringify(__vars.stale) === JSON.stringify(['?textOnly=true 200', '?textOnly=true&dryRun=false&ifPreview=m1-… 409', '?textOnly=true 200', '?textOnly=true&dryRun=false&ifPreview=m1-… 200'])" },

    // Cancel and Escape: the focus back on the opener.
    { name: 'escape: the synthetic member\'s Remove… focused', script: `const b = document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rtlId + '"] .jfur-mod-remove'); b.focus(); return document.activeElement === b;` },
    { name: 'escape: Enter opens the dialog', key: 'Enter' },
    { name: 'escape: the dialog with its dry run', waitFor: `(() => { const d = ${DIALOG}; return !!d && !d.querySelector('.jfur-mod-confirm').disabled; })()`, timeout: 10000 },
    { name: 'escape: Escape', key: 'Escape' },
    { name: 'escape: closed, the focus back on that Remove…, the review still there', waitFor: `!${DIALOG} && document.activeElement === document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rtlId + '"] .jfur-mod-remove') && document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rtlId + '"] .jfur-mod-text').textContent === ${json(TEXT_R)}`, timeout: 5000 },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    sweep('at the end', ['movieId', 'taggedId']),
    deleteRtlMember,
    noneLeft(['movieId', 'taggedId'])
];

write('userratings-moderation-desktop.json', `User Ratings ${VERSION}, moderation: in the default sandbox as admin, the desktop layout: the settings page's Moderation tab with viewer's reviews of Long Test Movie and Tagged Movie, viewer2's of Long Test Movie and a synthetic member's with a long right-to-left name (created for the run, its password random and kept nowhere): its first requests only reads; every name, title, star count and review as text (a review's markup-looking part no element) and isolated; the search (server-side), Clear filters, "Only viewer" and its chip; Remove text only after its dry run (one DELETE without dryRun=false, the focus on Cancel), with the reason Spoiler, the dry run's token sent back, the stars kept on the server and the log's row with no word of the review; Remove rating and review (its own dry run on the choice); a stale token: viewer edits the review between the dry run and Remove, the 409 shows "changed" with the dry run again and nothing removed, then the second Remove removes; Enter on a Remove… and Escape: the focus back on it. Every rating on both movies removed and the synthetic member deleted at the end.`, desktop);

// ---------------------------------------------------------------------------------------------------------- TV

const DOWN = { key: 'ArrowDown' };
const UP = { key: 'ArrowUp' };
const inDialog = `!!${DIALOG} && ${DIALOG}.contains(document.activeElement)`;
const tv = [
    ...adminStart('tv'),
    ...seed(['Long Test Movie', 'Tagged Movie']),
    { name: 'the TV layout applied', assert: "document.documentElement.classList.contains('layout-tv')" },
    ...openTab(),
    { name: 'tv: viewer\'s Long Test Movie Remove… focused', script: `const b = document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rA + '"] .jfur-mod-remove'); b.focus(); return document.activeElement === b;` },
    { name: 'tv: the hash before', eval: 'location.hash', save: 'hash' },
    { name: 'tv: OK (Enter)', key: 'Enter' },
    { name: 'tv: the dialog with the focus on Cancel and its dry run', waitFor: `(() => { const d = ${DIALOG}; return !!d && document.activeElement === d.querySelector('.jfur-mod-cancel') && !d.querySelector('.jfur-mod-confirm').disabled; })()`, timeout: 10000 },
    { name: 'tv: Down ×6 (past the last control)', ...DOWN },
    { ...DOWN }, { ...DOWN }, { ...DOWN }, { ...DOWN }, { ...DOWN },
    { name: 'tv: the focus still in the dialog, on its last control (no wrap)', assert: `${inDialog} && document.activeElement === ${DIALOG}.querySelector('.jfur-mod-confirm')` },
    { name: 'tv: Up ×8 (past the first control)', ...UP },
    { ...UP }, { ...UP }, { ...UP }, { ...UP }, { ...UP }, { ...UP }, { ...UP },
    { name: 'tv: the focus still in the dialog (no way out by the D-pad)', assert: inDialog },
    { name: 'tv: the page is where it was', assert: 'location.hash === __vars.hash' },
    { name: 'tv: Back (461, webOS)', cdp: 'Input.dispatchKeyEvent', params: { type: 'rawKeyDown', windowsVirtualKeyCode: 461, nativeVirtualKeyCode: 461, key: 'Unidentified', code: '' } },
    { cdp: 'Input.dispatchKeyEvent', params: { type: 'keyUp', windowsVirtualKeyCode: 461, nativeVirtualKeyCode: 461, key: 'Unidentified', code: '' } },
    { name: 'tv: the dialog closed, the focus back on its Remove…, still on the settings page', waitFor: `!${DIALOG} && document.activeElement === document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rA + '"] .jfur-mod-remove') && location.hash === __vars.hash`, timeout: 5000 },
    { name: 'tv: OK again', key: 'Enter' },
    { name: 'tv: the dialog, its dry run', waitFor: `(() => { const d = ${DIALOG}; return !!d && !d.querySelector('.jfur-mod-confirm').disabled; })()`, timeout: 10000 },
    { name: 'tv: Down to Remove', script: `${DIALOG}.querySelector('.jfur-mod-confirm').focus(); return document.activeElement.textContent;` },
    { name: 'tv: OK on Remove', key: 'Enter' },
    { name: 'tv: removed: the dialog closed, the row with "No review"', waitFor: `!${DIALOG} && !!document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rA + '"] .jfur-mod-none')`, timeout: 15000 },
    { name: 'tv: the focus on a Remove… of the list, never the page\'s body', waitFor: `${NOT_BODY} && document.activeElement.classList.contains('jfur-mod-remove')`, timeout: 5000 },
    { name: 'tv: screenshot', screenshot: 'userratings-moderation-tv.png' },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    sweep('at the end', ['movieId', 'taggedId']),
    deleteRtlMember,
    noneLeft(['movieId', 'taggedId'])
];

write('userratings-moderation-tv.json', `User Ratings ${VERSION}, moderation in the TV layout: in the default sandbox as admin in jellyfin-web 10.11.5's TV layout: the Moderation tab with the seeded reviews; OK on a Remove… opens the dialog with the focus on Cancel and its dry run; Down past the last control and Up past the first keep the focus inside it (no wrap, no way out) and the page where it was; Back (keyCode 461) closes it with the focus back on its Remove…; OK, Remove and OK remove the review, the focus on a Remove… of the list, never the page's body. Every rating on both movies removed and the synthetic member deleted at the end.`, tv);

// ---------------------------------------------------------------------------------------------------------- phone

const tall = (selector) => `Array.from(document.querySelectorAll(${json(selector)})).filter(e => e.offsetParent !== null).map(e => ({ cls: e.className, h: Math.round(e.getBoundingClientRect().height) })).filter(x => x.h < 44)`;
const phone = [
    ...adminStart(null, { width: 375, height: 812, mobile: true, touch: true }),
    ...seed(['Long Test Movie', 'Tagged Movie']),
    ...openTab(),
    { name: 'phone: the layout as shown', eval: `(() => { const t = document.querySelector('${ROOT} .jfur-mod-table'); const td = document.querySelector('${ROOT} .jfur-mod-row td'); const name = document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rtlId + '"] .jfur-mod-name'); const nb = name.getBoundingClientRect(); return { width: innerWidth, scroll: document.documentElement.scrollWidth, body: document.body.scrollWidth, head: getComputedStyle(t.querySelector('thead')).display, td: getComputedStyle(td).display, label: getComputedStyle(td, '::before').content, nameLeft: Math.round(nb.left), nameRight: Math.round(nb.right), nameBidi: getComputedStyle(name).unicodeBidi, nameDir: name.getAttribute('dir'), nameText: name.textContent }; })()`, save: 'phone', full: true },
    { name: 'phone: no sideways scroll; the rows are cards (no header, cells as blocks with their column\'s name); the long right-to-left name isolated and inside the screen', assert: `__vars.phone.scroll <= __vars.phone.width && __vars.phone.body <= __vars.phone.width && __vars.phone.head === 'none' && __vars.phone.td === 'block' && __vars.phone.label === '"Member"' && __vars.phone.nameLeft >= 0 && __vars.phone.nameRight <= __vars.phone.width && __vars.phone.nameBidi === 'isolate' && __vars.phone.nameDir === 'auto' && __vars.phone.nameText === ${json(RTL_NAME)}` },
    { name: 'phone: the tab\'s controls under 44 px high (none)', eval: tall(`${ROOT} button, ${ROOT} select, ${ROOT} input`), save: 'short', full: true },
    { name: 'phone: every control of the tab at least 44 px', assert: '__vars.short.length === 0' },
    { name: 'phone: screenshot of the list', screenshot: 'userratings-moderation-phone.png' },
    { name: 'phone: Remove… on the synthetic member\'s row', click: `${ROOT} .jfur-mod-row[data-jfur-rating="\${rtlId}"] .jfur-mod-remove` },
    { name: 'phone: the dialog and its dry run', waitFor: `(() => { const d = ${DIALOG}; return !!d && !d.querySelector('.jfur-mod-confirm').disabled; })()`, timeout: 10000 },
    { name: 'phone: the dialog\'s controls under 44 px high (none), the dialog inside the screen', eval: `({ short: ${tall('[role="dialog"] button, [role="dialog"] select, [role="dialog"] .jfur-mod-choice')}, right: Math.round(${DIALOG}.getBoundingClientRect().right), scroll: document.documentElement.scrollWidth, width: innerWidth, name: getComputedStyle(${DIALOG}.querySelector('.jfur-mod-by .jfur-mod-name')).unicodeBidi })`, save: 'dialog', full: true },
    { name: 'phone: every control of the dialog at least 44 px, inside the screen, the name isolated', assert: "__vars.dialog.short.length === 0 && __vars.dialog.right <= __vars.dialog.width && __vars.dialog.scroll <= __vars.dialog.width && __vars.dialog.name === 'isolate'" },
    { name: 'phone: screenshot of the dialog', screenshot: 'userratings-moderation-phone-dialog.png' },
    { name: 'phone: Cancel', click: '[role="dialog"] .jfur-mod-cancel' },
    { name: 'phone: closed, the review still there', waitFor: `!${DIALOG} && document.querySelector('${ROOT} .jfur-mod-row[data-jfur-rating="' + __vars.rtlId + '"] .jfur-mod-text').textContent === ${json(TEXT_R)}`, timeout: 5000 },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    sweep('at the end', ['movieId', 'taggedId']),
    deleteRtlMember,
    noneLeft(['movieId', 'taggedId'])
];

write('userratings-moderation-phone.json', `User Ratings ${VERSION}, moderation under touch: in the default sandbox as admin on a 375x812 phone with touch: the Moderation tab with the seeded reviews: no sideways scroll; the rows as cards, no header, each cell with its column's name; every button, select and field of the tab at least 44 px high; the synthetic member's long right-to-left name isolated (dir="auto", unicode-bidi: isolate) and inside the screen; the dialog of Remove… inside the screen with every control at least 44 px and the name isolated; Cancel keeps the review. Every rating on both movies removed and the synthetic member deleted at the end.`, phone);

// ---------------------------------------------------------------------------------------------------------- the details page

const openItem = (label, item) => [
    { name: `${label}: its details page`, script: `location.hash = '#/details?id=' + __vars.${item}; return true;` },
    { name: `${label}: drawn`, waitFor: DRAWN(item), timeout: 30000 },
    { name: `${label}: the section mounted for it`, waitFor: MOUNTED(item), timeout: 15000 },
    { sleep: 600 }
];
const SECTION = '#itemDetailPage:not(.hide) .jfur-section';
const removeButtons = `Array.from(document.querySelectorAll('${SECTION} .jfur-review-remove')).map(b => b.closest('.jfur-review').getAttribute('data-jfur-review'))`;
const reviewRow = (rid) => `document.querySelector('${SECTION} .jfur-review[data-jfur-review="' + __vars.${rid} + '"]')`;
const extras = { name: 'the chip and the Rate button as shown', eval: `${P}.extras()`, full: true };

function inline(opts) {
    const item = opts.item;
    const L = opts.label;
    const steps = [
        ...adminStart(opts.layout),
        { context: 'viewer' },
        ...signIn('viewer', opts.layout),
        { name: 'viewer: the movie', eval: MOVIE(opts.movie), save: item },
        rate('viewer', item, 3.5, TEXT_A),
        { context: 'viewer2' },
        ...signIn('viewer2', opts.layout),
        rate('viewer2', item, 4.5, TEXT_C),
        { context: 'main' },
        rate('admin (their own review: never a Remove… of their own)', item, 5, 'mod webcheck: the admin\'s own review.'),
        ratingIds(item, 'ids'),
        { name: 'viewer\'s rating', eval: '__vars.ids.viewer', save: 'rv' },
        { name: 'viewer2\'s rating', eval: '__vars.ids.viewer2', save: 'rv2' },
        { context: 'viewer' },
        ...openItem(`${L}: viewer`, item),
        { name: `${L}: viewer: the reviews listed`, waitFor: `!!document.querySelector('${SECTION} .jfur-review')`, timeout: 10000 },
        { name: `${L}: viewer: the Remove… buttons (none)`, eval: removeButtons, save: 'viewerButtons', full: true },
        { name: `${L}: viewer sees no Remove… (no Can.Moderate)`, assert: '__vars.viewerButtons.length === 0' },
        { context: 'main' }
    ];
    if (opts.layout === 'experimental') {
        steps.push(
            { name: 'experimental: the layout jellyfin-web shows', eval: "({ setting: localStorage.getItem('layout'), appBar: !!document.querySelector('[class*=\"MuiAppBar\"]') })", save: 'experimental', full: true },
            { name: 'experimental: the setting, and MUI\'s app bar', assert: "__vars.experimental.setting === 'experimental' && __vars.experimental.appBar" }
        );
    }

    steps.push(
        ...openItem(`${L}: admin`, item),
        { name: `${L}: viewer's and viewer2's reviews shown`, waitFor: `!!${reviewRow('rv')} && !!${reviewRow('rv2')}`, timeout: 10000 },
        { ...extras, name: `${L}: the chip and the Rate button before`, save: 'before' },
        { name: `${L}: the Remove… buttons`, eval: removeButtons, save: 'buttons', full: true },
        { name: `${L}: one on viewer's and one on viewer2's review, none on the admin's own`, assert: '__vars.buttons.length === 2 && __vars.buttons.indexOf(__vars.rv) >= 0 && __vars.buttons.indexOf(__vars.rv2) >= 0' },
        { name: `${L}: its name says whose review`, assert: `${reviewRow('rv')}.querySelector('.jfur-review-remove').getAttribute('aria-label') === 'Remove the review of viewer…'` },
        { name: `${L}: text only: mark`, script: `window.__mark = ${P}.requests.length; return true;` },
        { name: `${L}: text only: Remove… on viewer's review`, script: `${reviewRow('rv')}.querySelector('.jfur-review-remove').click(); return true;` },
        { name: `${L}: text only: the dialog, Remove text only chosen, the dry run's sentence, the focus on Cancel`, waitFor: `(() => { const d = ${DIALOG}; return !!d && !!d.querySelector('.jfur-moderate-radio[value="text"]:checked') && d.querySelector('.jfur-moderate-preview').textContent.indexOf('The review goes; the stars stay') === 0 && !d.querySelector('.jfur-moderate-confirm').disabled && document.activeElement === d.querySelector('.jfur-dialog-cancel'); })()`, timeout: 10000 },
        { name: `${L}: text only: the dialog's name isolated`, assert: `getComputedStyle(${DIALOG}.querySelector('.jfur-moderate-name')).unicodeBidi === 'isolate' && ${DIALOG}.querySelector('.jfur-moderate-name').textContent === 'viewer'` },
        { name: `${L}: text only: screenshot`, screenshot: `userratings-remove-${opts.shot}.png` },
        { name: `${L}: text only: Remove`, click: '[role="dialog"] .jfur-moderate-confirm' },
        { name: `${L}: text only: the dialog closed, viewer's row in the list with its stars and no review (its Remove… left for the stars)`, waitFor: `!${DIALOG} && !!${reviewRow('rv')} && ${reviewRow('rv')}.textContent.indexOf('lighthouse') < 0 && !!${reviewRow('rv')}.querySelector('.jfur-review-stars') && !!${reviewRow('rv')}.querySelector('.jfur-review-remove')`, timeout: 15000 },
        { name: `${L}: text only: the focus in the section, never the page's body`, waitFor: `${NOT_BODY} && !!document.activeElement.closest('${SECTION}')`, timeout: 5000 },
        { name: `${L}: text only: the requests`, eval: `${P}.requests.slice(window.__mark).filter(r => r.method === 'DELETE').map(r => r.path.replace(/[0-9a-f]{32}$/, '{id}') + r.query.replace(/m1-[0-9a-f]{20}/, 'm1-…') + ' ' + r.status)`, save: 'textReq', full: true },
        { name: `${L}: text only: the dry run, then the real run with its token`, assert: "JSON.stringify(__vars.textReq) === JSON.stringify(['/UserRatings/Admin/Reviews/{id}?textOnly=true 200', '/UserRatings/Admin/Reviews/{id}?textOnly=true&dryRun=false&ifPreview=m1-… 200'])" },
        { name: `${L}: text only: the server keeps viewer's stars`, script: `const p = await ${P}.server('GET', 'UserRatings/Admin/Reviews?itemId=' + __vars.${item}); const r = p.json.Items.find(x => ${id('x.RatingId')} === __vars.rv); return { stars: r.Stars, text: r.Text === undefined ? null : r.Text, count: p.json.TotalRecordCount };`, save: 'afterText', full: true },
        { name: `${L}: text only: 3.5 stars, no review, three ratings`, assert: '__vars.afterText.stars === 3.5 && __vars.afterText.text === null && __vars.afterText.count === 3' },
        { name: `${L}: entry: Remove… on viewer2's review`, script: `${reviewRow('rv2')}.querySelector('.jfur-review-remove').click(); return true;` },
        { name: `${L}: entry: the dialog and its dry run`, waitFor: `(() => { const d = ${DIALOG}; return !!d && !d.querySelector('.jfur-moderate-confirm').disabled; })()`, timeout: 10000 },
        { name: `${L}: entry: choose Remove rating and review`, click: '[role="dialog"] .jfur-moderate-radio[value="entry"]' },
        { name: `${L}: entry: its dry run's sentence`, waitFor: `(() => { const d = ${DIALOG}; return !!d && d.querySelector('.jfur-moderate-preview').textContent.indexOf('The rating and the review go.') === 0 && !d.querySelector('.jfur-moderate-confirm').disabled; })()`, timeout: 10000 },
        { name: `${L}: entry: Remove`, click: '[role="dialog"] .jfur-moderate-confirm' },
        { name: `${L}: entry: the dialog closed, viewer2's review gone`, waitFor: `!${DIALOG} && !${reviewRow('rv2')}`, timeout: 15000 },
        { name: `${L}: entry: the focus in the section, never the page's body`, waitFor: `${NOT_BODY} && !!document.activeElement.closest('${SECTION}')`, timeout: 5000 },
        { name: `${L}: entry: the summary counts two ratings (admin's and viewer's stars)`, waitFor: `(() => { const s = document.querySelector('${SECTION} .jfur-summary[data-jfur-summary]'); const c = s && s.querySelector('.jfur-summary-counts'); return !!c && c.textContent.indexOf('2 ratings') === 0; })()`, timeout: 10000 },
        { ...extras, name: `${L}: the chip and the Rate button after`, save: 'after' },
        { name: `${L}: the Rate button as before, and the chip shown where it was (now 2 ratings)`, assert: '__vars.after.rates === __vars.before.rates && __vars.after.chips === __vars.before.chips && JSON.stringify(__vars.after.buttons) === JSON.stringify(__vars.before.buttons)' },
        { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
        sweep('at the end', [item]),
        noneLeft([item])
    );
    return steps;
}

const desktopInline = inline({ label: 'desktop', layout: null, item: 'movieId', movie: 'Long Test Movie', shot: 'desktop' });
write('userratings-remove-desktop.json', `User Ratings ${VERSION}, a review removed from the details page: in the default sandbox, desktop layout, on Long Test Movie with viewer's, viewer2's and the admin's own reviews: viewer sees no "Remove…" on any review; the admin sees one on viewer's and on viewer2's and none on their own, named for the member; Remove text only after its dry run (the focus on Cancel, the name isolated): viewer's row stays with its stars and no review, the stars kept on the server; Remove rating and review on viewer2's after its own dry run; each real run sent its dry run's token; the focus stays in the section; the summary counts two ratings; the chip and the Rate button as before. Every rating on the movie removed at the end.`, desktopInline);
const experimentalInline = inline({ label: 'experimental', layout: 'experimental', item: 'movieId', movie: 'Long Test Movie', shot: 'experimental' });
write('userratings-remove-experimental.json', `User Ratings ${VERSION}, a review removed from the details page: in the default sandbox, jellyfin-web 10.11.5's experimental layout: the details page's "Remove…" as in userratings-remove-desktop.json: none for viewer; the admin's on the other members' reviews only; Remove text only and Remove rating and review, each after its dry run; the chip and the Rate button as before. Every rating on the movie removed at the end.`, experimentalInline);
const jeInline = inline({ label: 'je', layout: null, item: 'taggedId', movie: 'Tagged Movie', shot: 'je' });
jeInline.splice(jeInline.findIndex((s) => s.name && s.name.indexOf('je: viewer\'s and viewer2\'s reviews shown') === 0), 0,
    { name: 'je: Jellyfin Enhanced loaded and its Reviews drawn on Tagged Movie', waitFor: '!!window.JellyfinEnhanced && !!document.querySelector(\'#itemDetailPage:not(.hide) .jellyfin-enhanced-reviews, #itemDetailPage:not(.hide) [class*="je-reviews"], #itemDetailPage:not(.hide) .tmdb-reviews-section\')', timeout: 20000, optional: true },
    { name: 'je: JE\'s parts, recorded', eval: `${P}.jeReviews()`, full: true, optional: true });
write('userratings-remove-je.json', `User Ratings ${VERSION}, a review removed next to Jellyfin Enhanced 12.14: in the je sandbox on Tagged Movie, beside Jellyfin Enhanced's Reviews: the details page's "Remove…" as in userratings-remove-desktop.json: none for viewer; the admin's on the other members' reviews only; Remove text only and Remove rating and review, each after its dry run; the chip and the Rate button as before. Every rating on the movie removed at the end; no setting is changed.`, jeInline);
