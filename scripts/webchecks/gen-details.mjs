// Generates User Ratings' details-page webchecks over
// probes/userratings-details-probe.js, which watches the section the served ClientScript mounts and can answer one request
// in its place (a 503, a 404 after the write, IsPartial):
//   userratings-details-desktop.json       default, viewer, desktop layout: the section on Long Test Movie, Tab to the
//                                          slider, Right ×7 and Enter, the mouse, a review written and edited, Clear on a
//                                          reviewed row, the error answers (503 and Try again for the stars and the review,
//                                          IsPartial, 404 after the write), item to item with a change waiting (saved once),
//                                          Back (read again), Delete, the disclosure
//   userratings-details-experimental.json  default, viewer, the experimental layout: the same core path
//   userratings-details-tv.json            default, viewer, TV layout: the D-pad to the slider, Left/Right, OK, Up and Down,
//                                          Back with a change waiting (saved once)
//   userratings-details-three-windows.json default, viewer in three windows: Reload after Reload (it
//                                          keeps every text of the member's beside the form and in its draft; a Save
//                                          of one keeps the others)
//   userratings-details-touch.json         default, viewer, mobile layout at 390x844 with touch: a tap, a drag, − and +,
//                                          each saved; no horizontal overflow; the histogram hidden under 600 px
//   userratings-details-reviews.json       default, viewer2, after scripts/userratings-details-probe.mjs seeded more than 20
//                                          members' reviews on Long Test Movie (it runs this webcheck itself): another
//                                          member's rating in the average, the histogram, the list, and the reviews (Show
//                                          more, the four orders, paging past 20, Load more's 403), names and texts isolated
//   userratings-details-je.json            je, admin: the section beside Jellyfin Enhanced's review section, no style rule
//                                          of JE's on it, the slider's keys past JE's listener, the TV D-pad on Tagged Movie
//                                          with and without the section (recorded: JE 12.14's trap)
// Run from the repository root: node scripts/webchecks/gen-details.mjs [--out <folder>]
// Repo.Tests compares the files with what this writes. scripts/README.md says how to run them.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { VERSION } from './userratings-version.mjs';

const outIndex = process.argv.indexOf('--out');
const folder = outIndex > 0 ? path.resolve(process.argv[outIndex + 1]) : path.dirname(fileURLToPath(import.meta.url));
const IGNORE = '/CustomRatings/script|^Error: player cannot be null';
const P = '__jfurDetails';
const BUSY = 'The database is busy; try again in a moment.';
const NOT_FOUND = 'This item wasn\'t found, or it can\'t be rated.';
const MOVIE = (name) => `(await ApiClient.getItems(ApiClient.getCurrentUserId(), { IncludeItemTypes: 'Movie', Recursive: true, SearchTerm: ${JSON.stringify(name)}, Limit: 1 })).Items[0].Id`;
const DRAWN = (id) => `(() => { const p = document.querySelector('#itemDetailPage:not(.hide)'); return !!p && location.hash.indexOf(__vars.${id}) > 0 && !!p.querySelector('.mainDetailButtons .btnPlay:not(.hide)'); })()`;
const MOUNTED = (id) => `(() => { const s = ${P}.section(); return !!s && s.getAttribute('data-jfur-item') === __vars.${id} && !!s.querySelector('.jfur-summary[data-jfur-summary]'); })()`;
const STATE = `${P}.state()`;
const STYLES = `${P}.styles()`;

function write(name, title, steps) {
    const doc = { name: title, failOnException: false, timeout: 30000, steps };
    fs.writeFileSync(path.join(folder, name), JSON.stringify(doc, null, 2) + '\n', { encoding: 'utf8' });
}

// What the server holds for the signed-in member on an item (the probe's own read, past its wrapper).
const server = (label, id, save) => ({ name: `the server: ${label}`, eval: `${P}.mine(__vars.${id})`, save, full: true });
const mark = (label) => ({ name: `${label}: mark`, script: `window.__mark = ${P}.requests.length; window.__keys = ${P}.log.length; return true;` });
const since = (label, save) => ({ name: `${label}: the section's requests since`, eval: `${P}.since(window.__mark)`, save, full: true });
const state = (label, save) => ({ name: label, eval: STATE, save, full: true });
// The body of the section's last PUT Mine/Rating or Mine/Review.
const lastBody = (kind) => `(${P}.requests.filter(r => r.method === 'PUT' && /Mine\\/${kind}$/.test(r.path)).pop() || { body: null }).body`;
// User Ratings' drafts in the browser's storage, their names only.
const drafts = `Object.keys(${P}.storage()).filter(k => k.indexOf('jfur:draft:') === 0)`;
// The member's own row on an item gone, whatever an earlier run left (204, or 404 when there was none).
const clean = (label, id) => ({ name: `clean: ${label} without a rating of this member`, script: `const r = await ${P}.server('DELETE', 'UserRatings/Items/' + __vars.${id} + '/Mine'); return r.status;`, save: 'cleaned' });
const openItem = (label, id) => [
    { name: `${label}: its details page`, script: `location.hash = '#/details?id=' + __vars.${id}; return true;` },
    { name: `${label}: drawn`, waitFor: DRAWN(id), timeout: 30000 },
    { name: `${label}: the section mounted for it`, waitFor: MOUNTED(id), timeout: 15000 },
    { sleep: 400 }
];
const focusSlider = { name: 'the slider focused', script: `${P}.slider().focus(); return ${P}.onSlider();` };
const lastKey = (k) => `(${P}.log.slice(window.__keys).filter(e => e.kind === 'key-bubble' && e.key === ${JSON.stringify(k)}).pop() || { prevented: null })`;
const noAlert = `Array.prototype.filter.call(document.querySelectorAll('.jfur-section [role="alert"], .jfur-toast'), n => ${P}.displayed(n)).length === 0`;

function start(opts) {
    const steps = [{ name: 'the details probe in every document (it watches the section the served script mounts)', initScript: { file: 'probes/userratings-details-probe.js' } }];
    if (opts.emulate) {
        steps.push({ name: 'the phone\'s screen and touch, before signing in', emulate: opts.emulate });
    }

    steps.push(
        { name: 'sign in as ' + opts.user, login: opts.user },
        { name: opts.layout + ' layout', eval: opts.layout === 'desktop' ? "(localStorage.removeItem('layout'), true)" : `(localStorage.setItem('layout', '${opts.layout}'), true)` },
        { name: 'reload', reload: true },
        { name: `the layout applied, User Ratings ${VERSION} started`, waitFor: `document.documentElement.classList.contains('layout-${opts.layout === 'experimental' ? 'desktop' : opts.layout}') && !!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)`, timeout: 40000 },
        { name: 'the served script is ' + VERSION, assert: `window.__jfkit.jfur.version === '${VERSION}'`, message: 'User Ratings ' + VERSION + ' is not the script the page loaded' }
    );
    for (const [id, name] of opts.movies) {
        steps.push({ name, eval: MOVIE(name), save: id });
        if (opts.clean !== false) {
            steps.push(clean(name, id));
        }
    }

    return steps;
}

const PLAY = `document.querySelector('#itemDetailPage:not(.hide) .mainDetailButtons .btnPlay')`;
const SECTION = '#itemDetailPage:not(.hide) .jfur-section';

// A real mouse event at a page x on the slider's vertical middle.
function mouse(type, xExpr) {
    return {
        cdp: 'Input.dispatchMouseEvent',
        params: `(() => { const b = ${P}.box(${P}.slider()); return { type: '${type}', x: ${xExpr}, y: b.cy, button: '${type === 'mouseMoved' ? 'none' : 'left'}', clickCount: ${type === 'mouseMoved' ? 0 : 1} }; })()`
    };
}

// Writes a review in the open form: the focus in its text area, two lines typed, then Save.
function typeReview(label, first, second) {
    return [
        { name: `${label}: the text area has the focus`, assert: `document.activeElement && document.activeElement.matches('${SECTION} .jfur-review-form textarea')` },
        { name: `${label}: type`, type: first },
        { name: `${label}: a new line`, key: 'Enter' },
        { name: `${label}: type more`, type: second }
    ];
}

// The section's core path, in the desktop and the experimental layouts (both keep the legacy details view).
function corePath(layout, full) {
    const L = layout;
    const steps = start({ user: 'viewer', layout, movies: [['movieId', 'Long Test Movie'], ['taggedId', 'Tagged Movie']] });
    if (layout === 'experimental') {
        steps.push(
            { name: 'experimental: the layout jellyfin-web shows', eval: "({ setting: localStorage.getItem('layout'), appBar: !!document.querySelector('[class*=\"MuiAppBar\"]'), html: document.documentElement.className })", save: 'experimental', full: true },
            { name: 'experimental: the setting, and MUI\'s app bar on the page', assert: "__vars.experimental.setting === 'experimental' && __vars.experimental.appBar" }
        );
    }

    steps.push(
        server('Long Test Movie as the run starts', 'movieId', 'before'),
        { name: 'no member has rated Long Test Movie (another run left nothing)', assert: '__vars.before.status === 200 && __vars.before.count === 0 && __vars.before.others === 0', message: 'Long Test Movie has ratings left from another run' },
        ...openItem('Long Test Movie', 'movieId'),
        state(`${L}: the section as mounted`, 'mounted'),
        {
            name: `${L}: before the cast, for the item, open, "No member ratings yet. Be the first.", "Not rated", Write a review, no Clear, no list`,
            assert: "(s => s.mounted && s.sections === 1 && s.next === 'castCollapsible' && s.item === __vars.movieId && s.expanded === 'true' && s.summaryKind === 'none' && s.summary === 'No member ratings yet. Be the first.' && s.value === 0 && s.valuetext === 'Not rated' && s.write && !s.clear && !s.form && s.reviews.length === 0 && s.notices.length === 0)(__vars.mounted)"
        },
        { name: `${L}: styles`, eval: STYLES, save: 'styles', full: true },
        { name: `${L}: the slider the only focusable control of its row; the title a disclosure button in the title's size`, assert: "(s => s.rowFocusables.length === 1 && s.rowFocusables[0].indexOf('div.jfur-stars.focusable') === 0 && s.toggle.tabindexAttr === null && s.toggle.fontSize === s.titleFontSize && !s.tv)(__vars.styles)" },
        { name: `${L}: the section's requests: Capabilities and the item, nothing else`, eval: `${P}.since(0)`, save: 'firstRequests', full: true },
        { name: `${L}: two reads`, assert: "__vars.firstRequests.length === 2 && __vars.firstRequests[0] === 'GET /UserRatings/Capabilities 200' && __vars.firstRequests[1] === 'GET /UserRatings/Items/' + __vars.movieId + ' 200'" }
    );
    if (full) {
        steps.push({ name: `${L}: focus the Play button`, script: `${P}.log.length = 0; ${PLAY}.focus(); return ${P}.active();` });
        for (let i = 1; i <= 12; i++) {
            steps.push({ name: `${L}: Tab ${i}`, key: 'Tab', if: `!${P}.onSlider()` });
        }

        steps.push(
            { name: `${L}: Tab's path to the slider`, eval: `({ onSlider: ${P}.onSlider(), tabs: ${P}.log.filter(e => e.kind === 'key-capture' && e.key === 'Tab').length, path: ${P}.log.filter(e => e.kind === 'focus').map(f => f.target) })`, save: 'tabReach', full: true },
            { name: `${L}: Tab reached the slider, the section's title on the way`, assert: `${P}.onSlider() && __vars.tabReach.tabs <= 8 && __vars.tabReach.path.some(p => p.indexOf('jfur-section-toggle') >= 0)` }
        );
    } else {
        steps.push(focusSlider);
    }

    steps.push(mark(`${L} Right ×7, Enter`));
    for (let i = 1; i <= 7; i++) {
        steps.push({ name: `${L}: Right ${i}`, key: 'ArrowRight' });
    }

    steps.push(
        { name: `${L}: "3.5 out of 5 stars"`, assert: `${STATE}.valuetext === '3.5 out of 5 stars'` },
        { name: `${L}: Enter`, key: 'Enter' },
        { sleep: 500 },
        since(`${L} Enter`, 'enterRequests'),
        state(`${L}: after Enter`, 'afterEnter'),
        server('after Enter', 'movieId', 'afterEnterServer'),
        {
            name: `${L}: one PUT Mine/Rating of 3.5, saved; "Saved: 3.5 stars"; the summary from its answer; Clear on the next row`,
            assert: `__vars.enterRequests.length === 1 && __vars.enterRequests[0] === 'PUT /UserRatings/Items/' + __vars.movieId + '/Mine/Rating 200' && ${P}.requests[${P}.requests.length - 1].body.Stars === 3.5 && __vars.afterEnterServer.stars === 3.5 && __vars.afterEnter.live === 'Saved: 3.5 stars' && __vars.afterEnter.average === 'Member average 3.5 out of 5, 1 rating' && __vars.afterEnter.clear && __vars.afterEnter.value === 3.5`
        }
    );
    if (full) {
        steps.push(
            mark(`${L} mouse`),
            { name: `${L}: the stars in view`, script: `${P}.slider().scrollIntoView({ block: 'center' }); await new Promise(r => setTimeout(r, 400)); return true;` },
            mouse('mouseMoved', `${P}.xFor(2.5)`),
            mouse('mousePressed', `${P}.xFor(2.5)`),
            mouse('mouseReleased', `${P}.xFor(2.5)`),
            { sleep: 1000 },
            since(`${L} mouse`, 'mouseRequests'),
            server('after the click', 'movieId', 'afterClick'),
            { name: `${L}: a real click on the middle of star 3 is 2.5, saved 600 ms later`, assert: `${STATE}.value === 2.5 && __vars.mouseRequests.length === 1 && __vars.afterClick.stars === 2.5` }
        );
    }

    // A review written, then edited.
    steps.push(
        server('before the review', 'movieId', 'beforeReview'),
        mark(`${L} review`),
        { name: `${L}: Write a review`, click: `${SECTION} .jfur-write` },
        { sleep: 200 },
        { name: `${L}: the form: its limit and counter`, eval: `(() => { const f = ${P}.section().querySelector('.jfur-review-form'); return { max: f.querySelector('textarea').getAttribute('maxlength'), counter: f.querySelector('.jfur-review-counter').textContent, save: f.querySelector('.jfur-review-save').disabled, label: f.querySelector('label').textContent }; })()`, save: 'form', full: true },
        { name: `${L}: maxlength 5000, "0 / 5000", Save off while empty, labelled "Your review"`, assert: "__vars.form.max === '5000' && __vars.form.counter === '0 / 5000' && __vars.form.save === true && __vars.form.label === 'Your review'" },
        ...typeReview(L, 'A synthetic review of the test clip.', 'Second line, שלום ‮open.'),
        { name: `${L}: the counter follows`, assert: `${P}.section().querySelector('.jfur-review-counter').textContent === document.activeElement.value.length + ' / 5000'` },
        { name: `${L}: Save`, click: `${SECTION} .jfur-review-save` },
        { sleep: 700 },
        since(`${L} review`, 'reviewRequests'),
        state(`${L}: after Save`, 'afterReview'),
        server('after Save', 'movieId', 'afterReviewServer'),
        {
            name: `${L}: PUT Mine/Review with the text and IfNoReview (a first review on a row of stars); the own card; "Review saved"; the focus on Edit`,
            assert: `__vars.reviewRequests.length === 1 && / 200$/.test(__vars.reviewRequests[0]) && __vars.beforeReview.text === null && ${P}.requests[${P}.requests.length - 1].body.IfNoReview === true && !('IfUpdatedUtc' in ${P}.requests[${P}.requests.length - 1].body) && !('IfReviewUpdatedUtc' in ${P}.requests[${P}.requests.length - 1].body) && ${P}.requests[${P}.requests.length - 1].body.IsSpoiler === false && __vars.afterReviewServer.text === 'A synthetic review of the test clip.\\nSecond line, שלום ‮open.' && __vars.afterReview.own === __vars.afterReviewServer.text && !__vars.afterReview.form && __vars.afterReview.sectionLive === 'Review saved' && document.activeElement.matches('${SECTION} .jfur-own-edit')`
        },
        { name: `${L}: the own review isolated, its line breaks kept`, eval: `(() => { const n = ${P}.section().querySelector('.jfur-own .jfur-review-text'); return { dir: n.getAttribute('dir'), bidi: getComputedStyle(n).unicodeBidi, ws: getComputedStyle(n).whiteSpace }; })()`, save: 'ownStyles', full: true },
        { name: `${L}: dir="auto", unicode-bidi: isolate, pre-line`, assert: "__vars.ownStyles.dir === 'auto' && __vars.ownStyles.bidi === 'isolate' && __vars.ownStyles.ws === 'pre-line'" },
        { name: `${L}: Edit`, click: `${SECTION} .jfur-own-edit` },
        { sleep: 200 },
        { name: `${L}: the form holds the text as saved`, assert: `${STATE}.formText === __vars.afterReviewServer.text` },
        { name: `${L}: the caret at the end`, script: 'const a = document.activeElement; a.selectionStart = a.selectionEnd = a.value.length; return a.tagName;' },
        { name: `${L}: add to it`, type: ' Edited.' },
        { name: `${L}: Save the edit`, click: `${SECTION} .jfur-review-save` },
        { sleep: 700 },
        state(`${L}: after the edit`, 'afterEdit'),
        server('after the edit', 'movieId', 'afterEditServer'),
        { name: `${L}: what the edit sent`, eval: lastBody('Review'), save: 'editBody', full: true },
        { name: `${L}: the edit saved, "(edited)" shown; it sent the review's own time as the form read it (IfReviewUpdatedUtc), not the row's`, assert: "__vars.afterEdit.ownEdited && __vars.afterEditServer.edited === true && /Edited\\.$/.test(__vars.afterEditServer.text) && __vars.afterEdit.own === __vars.afterEditServer.text && __vars.editBody.IfReviewUpdatedUtc === __vars.afterReviewServer.reviewUpdated && !('IfUpdatedUtc' in __vars.editBody) && !('IfNoReview' in __vars.editBody)" }
    );
    if (full) {
        // Clear on a reviewed row, then the error answers.
        steps.push(
            mark(`${L} Clear`),
            { name: `${L}: Clear`, click: `${SECTION} .jfur-clear` },
            { sleep: 300 },
            { name: `${L}: the question`, eval: `(() => { const p = document.querySelector('.jfur-backdrop .jfur-panel'); return p && { title: p.querySelector('h3').textContent, text: p.querySelector('p').textContent, buttons: Array.prototype.map.call(p.querySelectorAll('.jfur-actions button'), b => b.textContent), focused: document.activeElement === p.querySelector('.jfur-dialog-cancel') }; })()`, save: 'clearDialog', full: true },
            { name: `${L}: "Keep the review without stars?", Cancel first and focused`, assert: "__vars.clearDialog.title === 'Keep the review without stars?' && __vars.clearDialog.buttons.join() === 'Cancel,Clear the stars' && __vars.clearDialog.focused" },
            { name: `${L}: Clear the stars`, click: '.jfur-backdrop .jfur-dialog-confirm' },
            { sleep: 600 },
            since(`${L} Clear`, 'clearRequests'),
            state(`${L}: after Clear`, 'afterClear'),
            server('after Clear', 'movieId', 'afterClearServer'),
            { name: `${L}: DELETE Mine/Rating; "Not rated", "Rating cleared"; the review kept (clear stars on a reviewed row)`, assert: "__vars.clearRequests.length === 1 && __vars.clearRequests[0] === 'DELETE /UserRatings/Items/' + __vars.movieId + '/Mine/Rating 200' && __vars.afterClear.value === 0 && __vars.afterClear.live === 'Rating cleared' && __vars.afterClear.own === __vars.afterEditServer.text && __vars.afterClearServer.stars === null && __vars.afterClearServer.text === __vars.afterEditServer.text" },
            // 503 before the write: the stars put back, the sentence, Try again.
            mark(`${L} 503`),
            { name: `${L}: the next PUT Mine/Rating answered 503 "busy" by the probe (the server not reached)`, eval: `${P}.inject({ method: 'PUT', path: 'Mine/Rating$', mode: 'stub', status: 503, body: { title: 'Service Unavailable', status: 503, detail: ${JSON.stringify(BUSY)} } })` },
            focusSlider,
            { name: `${L}: End`, key: 'End' },
            { name: `${L}: Enter`, key: 'Enter' },
            { sleep: 400 },
            state(`${L}: after the 503`, 'after503'),
            server('after the 503', 'movieId', 'after503Server'),
            { name: `${L}: the stars back at "Not rated", "Your rating wasn't saved." with the server's sentence and Try again; nothing saved`, assert: `__vars.after503.value === 0 && __vars.after503.notices.length === 1 && __vars.after503.notices[0].role === 'alert' && __vars.after503.notices[0].text === ${JSON.stringify("Your rating wasn't saved. " + BUSY)} && __vars.after503.notices[0].action === 'Try again' && __vars.after503Server.stars === null` },
            { name: `${L}: Try again`, click: `${SECTION} .jfur-mine-message button` },
            { sleep: 600 },
            state(`${L}: after Try again`, 'afterRetry'),
            server('after Try again', 'movieId', 'afterRetryServer'),
            { name: `${L}: saved, the slider at 5, the notice gone`, assert: '__vars.afterRetry.value === 5 && __vars.afterRetry.notices.length === 0 && __vars.afterRetryServer.stars === 5' },
            // 503 of a review: nothing lost from the form.
            { name: `${L}: Edit again`, click: `${SECTION} .jfur-own-edit` },
            { sleep: 200 },
            { name: `${L}: the caret at the end again`, script: 'const a = document.activeElement; a.selectionStart = a.selectionEnd = a.value.length; return a.tagName;' },
            { name: `${L}: add more`, type: ' Busy.' },
            { name: `${L}: the next PUT Mine/Review answered 503 by the probe`, eval: `${P}.inject({ method: 'PUT', path: 'Mine/Review$', mode: 'stub', status: 503, body: { title: 'Service Unavailable', status: 503, detail: ${JSON.stringify(BUSY)} } })` },
            { name: `${L}: Save (503)`, click: `${SECTION} .jfur-review-save` },
            { sleep: 500 },
            state(`${L}: after the review's 503`, 'reviewBusy'),
            server('after the review\'s 503', 'movieId', 'reviewBusyServer'),
            { name: `${L}: the form and its text kept, the sentence and Try again, nothing saved`, assert: `__vars.reviewBusy.form && /Busy\\.$/.test(__vars.reviewBusy.formText) && __vars.reviewBusy.notices.some(n => n.text === ${JSON.stringify("Your review wasn't saved. " + BUSY)} && n.action === 'Try again') && __vars.reviewBusyServer.text === __vars.afterEditServer.text` },
            { name: `${L}: Try again (the review)`, click: `${SECTION} .jfur-review-form .jfur-notice button` },
            { sleep: 700 },
            server('after the review\'s Try again', 'movieId', 'reviewRetryServer'),
            { name: `${L}: the review saved`, assert: `/Busy\\.$/.test(__vars.reviewRetryServer.text) && !${STATE}.form && ${STATE}.own === __vars.reviewRetryServer.text` },
            // IsPartial: Mine as written, the summary as it was.
            state(`${L}: before the partial answer`, 'beforePartial'),
            mark(`${L} IsPartial`),
            { name: `${L}: the next PUT Mine/Rating reaches the server and its answer comes back as IsPartial`, eval: `${P}.inject({ method: 'PUT', path: 'Mine/Rating$', mode: 'partial' })` },
            focusSlider,
            { name: `${L}: Home`, key: 'Home' },
            { name: `${L}: Enter (partial)`, key: 'Enter' },
            { sleep: 500 },
            since(`${L} IsPartial`, 'partialRequests'),
            state(`${L}: after the partial answer`, 'afterPartial'),
            server('after the partial answer', 'movieId', 'afterPartialServer'),
            { name: `${L}: saved; the own row as written (0.5), the summary as it was, no error`, assert: "__vars.partialRequests[0].indexOf('partial (injected; server 200)') > 0 && __vars.afterPartial.value === 0.5 && __vars.afterPartialServer.stars === 0.5 && __vars.afterPartial.summary === __vars.beforePartial.summary && __vars.afterPartial.notices.length === 0" },
            // 404 after the write: saved, and the section goes with no "couldn't save".
            mark(`${L} 404 after the write`),
            { name: `${L}: the next PUT Mine/Rating reaches the server and is answered 404 (the item taken away after the write)`, eval: `${P}.inject({ method: 'PUT', path: 'Mine/Rating$', mode: 'after', status: 404, body: { title: 'Not Found', status: 404, detail: ${JSON.stringify(NOT_FOUND)} } })` },
            focusSlider,
            { name: `${L}: End (404)`, key: 'End' },
            { name: `${L}: Enter (404)`, key: 'Enter' },
            { sleep: 600 },
            since(`${L} 404`, 'goneRequests'),
            state(`${L}: after the 404`, 'afterGone'),
            server('after the 404', 'movieId', 'afterGoneServer'),
            { name: `${L}: the write saved (5), the section gone, no "couldn't save", nothing read again`, assert: `__vars.goneRequests.length === 1 && __vars.goneRequests[0].indexOf('404 (injected; server 200)') > 0 && __vars.afterGoneServer.stars === 5 && !__vars.afterGone.mounted && __vars.afterGone.sections === 0 && ${noAlert}` }
        );
    }

    // Item to item with a change waiting, then Back.
    steps.push(
        ...(full ? [{ name: `${L}: to Tagged Movie and back, so Long Test Movie's section is there again`, script: "location.hash = '#/details?id=' + __vars.taggedId; return true;" }, { waitFor: MOUNTED('taggedId'), timeout: 15000 }, { name: `${L}: Back`, script: 'history.back(); return true;' }, { waitFor: MOUNTED('movieId'), timeout: 15000 }, { sleep: 500 }, state(`${L}: Long Test Movie's section again`, 'backAgain'), { name: `${L}: read again: the server's 5`, assert: '__vars.backAgain.value === 5 && __vars.backAgain.sections === 1' }] : []),
        mark(`${L} item to item`),
        focusSlider,
        { name: `${L}: Left (a change waiting 600 ms)`, key: 'ArrowLeft' },
        { name: `${L}: to Tagged Movie at once, without a reload`, script: "location.hash = '#/details?id=' + __vars.taggedId; return true;" },
        { name: `${L}: Tagged Movie's section mounted`, waitFor: MOUNTED('taggedId'), timeout: 15000 },
        { sleep: 1200 },
        since(`${L} item to item`, 'itemRequests'),
        state(`${L}: on Tagged Movie`, 'onTagged'),
        server('Long Test Movie after leaving it', 'movieId', 'leftServer'),
        server('Tagged Movie', 'taggedId', 'taggedServer'),
        {
            name: `${L}: one section, Tagged Movie's; the waiting change saved once, for Long Test Movie; Tagged Movie read`,
            assert: `__vars.onTagged.sections === 1 && __vars.onTagged.item === __vars.taggedId && __vars.itemRequests.filter(r => r.indexOf('PUT ') === 0).length === 1 && __vars.itemRequests.indexOf('PUT /UserRatings/Items/' + __vars.movieId + '/Mine/Rating 200') >= 0 && __vars.itemRequests.indexOf('GET /UserRatings/Items/' + __vars.taggedId + ' 200') >= 0 && __vars.leftServer.stars === ${full ? 4.5 : 3} && __vars.taggedServer.stars === null`
        },
        mark(`${L} Back`),
        { name: `${L}: Back`, script: 'history.back(); return true;' },
        { name: `${L}: Long Test Movie's section again`, waitFor: MOUNTED('movieId'), timeout: 15000 },
        { sleep: 600 },
        since(`${L} Back`, 'backRequests'),
        state(`${L}: back on Long Test Movie`, 'back'),
        { name: `${L}: read again (a restored view), the server's value, one section`, assert: `__vars.back.sections === 1 && __vars.back.value === ${full ? 4.5 : 3} && __vars.backRequests.indexOf('GET /UserRatings/Items/' + __vars.movieId + ' 200') >= 0` },
        // Delete.
        mark(`${L} Delete`),
        { name: `${L}: Delete`, click: `${SECTION} .jfur-own-delete` },
        { sleep: 300 },
        { name: `${L}: the question (Delete)`, eval: `(() => { const p = document.querySelector('.jfur-backdrop .jfur-panel'); return p && { title: p.querySelector('h3').textContent, focused: document.activeElement === p.querySelector('.jfur-dialog-cancel') }; })()`, save: 'deleteDialog', full: true },
        { name: `${L}: "Delete your rating and review?", Cancel focused`, assert: "__vars.deleteDialog.title === 'Delete your rating and review?' && __vars.deleteDialog.focused" },
        { name: `${L}: Delete (confirm)`, click: '.jfur-backdrop .jfur-dialog-confirm' },
        { sleep: 800 },
        since(`${L} Delete`, 'deleteRequests'),
        state(`${L}: after Delete`, 'afterDelete'),
        server('after Delete', 'movieId', 'afterDeleteServer'),
        { name: `${L}: DELETE Mine (204), the item read again; no own card, "Not rated", "Your rating and review were deleted" (delete)`, assert: "__vars.deleteRequests[0] === 'DELETE /UserRatings/Items/' + __vars.movieId + '/Mine 204' && __vars.deleteRequests.indexOf('GET /UserRatings/Items/' + __vars.movieId + ' 200') > 0 && __vars.afterDelete.own === null && __vars.afterDelete.value === 0 && __vars.afterDelete.write && __vars.afterDelete.sectionLive === 'Your rating and review were deleted' && __vars.afterDeleteServer.stars === null && __vars.afterDeleteServer.text === null && __vars.afterDeleteServer.count === 0" }
    );
    if (full) {
        steps.push(
            { name: `${L}: the title collapses the section`, click: `${SECTION} .jfur-section-toggle` },
            { name: `${L}: collapsed`, assert: `${STATE}.expanded === 'false' && !${P}.displayed(${P}.section().querySelector('.jfur-section-body'))` },
            { name: `${L}: and opens it`, click: `${SECTION} .jfur-section-toggle` },
            { name: `${L}: open`, assert: `${STATE}.expanded === 'true' && ${P}.displayed(${P}.section().querySelector('.jfur-section-body'))` },
            { name: `screenshot (${L})`, screenshot: `userratings-details-${L}.png` }
        );
    }

    steps.push(
        clean('Long Test Movie at the end', 'movieId'),
        clean('Tagged Movie at the end', 'taggedId'),
        { name: 'the default layout again', eval: "(localStorage.removeItem('layout'), true)" },
        { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
        { name: 'every request of the section', eval: `${P}.since(0)`, full: true }
    );
    return steps;
}

write('userratings-details-desktop.json', `User Ratings ${VERSION}: the details page's section on Long Test Movie as viewer in the desktop layout: mounted before the cast, the slider alone in its row; Tab reaches it; Right ×7 and Enter save 3.5; a real click on the middle of star 3 saves 2.5; a review written (its limit and counter, IfUpdatedUtc as read, isolated) and edited ("(edited)"); Clear on a reviewed row asks and keeps the review; a 503 puts the stars back with Try again, a review's 503 keeps the form; IsPartial keeps the summary; a 404 after the write removes the section with no error and the write saved; item to item with a change waiting saves it once; Back reads the item again; Delete asks and deletes; the title collapses and opens the section. The member's rows are deleted at the end.`, corePath('desktop', true));
write('userratings-details-experimental.json', `User Ratings ${VERSION}: the details page's section in jellyfin-web 10.11.5's experimental layout (the legacy details view) as viewer: mounted before the cast; Right ×7 and Enter save 3.5; a review written and edited; item to item with a change waiting saves it once; Back reads the item again; Delete. The member's rows are deleted at the end.`, corePath('experimental', false));

// -------------------------------------------------------------------------------------------------------- TV

const tv = start({ user: 'viewer', layout: 'tv', movies: [['movieId', 'Long Test Movie']] });
tv.push(
    ...openItem('Long Test Movie', 'movieId'),
    { name: 'TV: styles', eval: STYLES, save: 'tvStyles', full: true },
    { name: 'TV: the title no tab stop, the slider alone in its row (Left and Right are the slider\'s)', assert: "(s => s.tv && s.toggle.tabindexAttr === '-1' && s.rowFocusables.length === 1 && s.rowFocusables[0].indexOf('div.jfur-stars.focusable') === 0)(__vars.tvStyles)" },
    { name: 'TV: focus the Play button', script: `${P}.log.length = 0; ${PLAY}.focus(); return ${P}.active();` }
);
for (let i = 1; i <= 6; i++) {
    tv.push({ name: `TV: ArrowDown ${i} (to the slider)`, key: 'ArrowDown', if: `!${P}.onSlider()` }, { sleep: 250, if: `!${P}.onSlider()` });
}

tv.push(
    { name: 'TV: the D-pad\'s path', eval: `({ onSlider: ${P}.onSlider(), presses: ${P}.log.filter(e => e.kind === 'key-capture' && e.key === 'ArrowDown').length, path: ${P}.log.filter(e => e.kind === 'focus').map(f => f.target) })`, save: 'tvReach', full: true },
    { name: 'TV: D-pad down from Play reaches "Your rating" within 6 presses, past no title', assert: `${P}.onSlider() && __vars.tvReach.presses <= 6 && !__vars.tvReach.path.some(p => p.indexOf('jfur-section-toggle') >= 0)` },
    mark('TV Right')
);
for (let i = 1; i <= 3; i++) {
    tv.push({ name: `TV: Right ${i}`, key: 'ArrowRight' }, { sleep: 150 }, { name: `TV: Right ${i} consumed, the focus kept`, assert: `${lastKey('ArrowRight')}.prevented === true && ${P}.onSlider()` });
}

tv.push(
    { name: 'TV: Left', key: 'ArrowLeft' },
    { sleep: 150 },
    { name: 'TV: Left consumed, 1.0', assert: `${lastKey('ArrowLeft')}.prevented === true && ${STATE}.value === 1` },
    { name: 'TV: OK', key: 'Enter' },
    { sleep: 600 },
    since('TV OK', 'tvOk'),
    server('after OK', 'movieId', 'tvOkServer'),
    { name: 'TV: OK saved 1 at once', assert: "__vars.tvOk.filter(r => r.indexOf('PUT ') === 0).length === 1 && __vars.tvOkServer.stars === 1" },
    { name: 'TV: Up', key: 'ArrowUp' },
    { sleep: 400 },
    { name: 'TV: Up left to jellyfin-web: the focus went up, the value kept', assert: `!${P}.onSlider() && ${STATE}.value === 1` },
    { name: 'TV: Down back', key: 'ArrowDown' },
    { sleep: 400 },
    { name: 'TV: back on the slider', assert: `${P}.onSlider()` },
    { name: 'TV: Down', key: 'ArrowDown' },
    { sleep: 400 },
    { name: 'TV: Down to the next row: Clear or Write a review', assert: `/jfur-(clear|write)/.test(${P}.active())` },
    { name: 'TV: Up back to the slider', key: 'ArrowUp' },
    { sleep: 400 },
    { name: 'TV: on the slider again', assert: `${P}.onSlider()` },
    mark('TV Back with a change waiting'),
    { name: 'TV: Right (1.5 waiting 600 ms)', key: 'ArrowRight' },
    { name: 'TV: Back (Escape) at once', key: 'Escape' },
    { sleep: 1500 },
    since('TV Back', 'tvBack'),
    server('after Back', 'movieId', 'tvBackServer'),
    { name: 'TV: Back is jellyfin-web\'s (the page left), the section gone, the waiting 1.5 saved once', assert: `location.hash.indexOf(__vars.movieId) < 0 && document.querySelectorAll('.jfur-section').length === 0 && __vars.tvBack.filter(r => r.indexOf('PUT ') === 0).length === 1 && __vars.tvBackServer.stars === 1.5` },
    // A Delete that fails leaves the focus on Delete (before: the card was built again, and the
    // focus was on the page's body, out of the D-pad's reach).
    { name: 'TV failed delete: a review of the member\'s (written through the API)', script: `const r = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Review', { Text: 'A synthetic review, written for the TV check.', IsSpoiler: false }); return r.status;`, save: 'tvSeed' },
    ...openItem('Long Test Movie again', 'movieId'),
    { name: 'TV failed delete: the own card with Delete', waitFor: `!!${P}.section().querySelector('.jfur-own-delete')`, timeout: 10000 },
    { name: 'TV failed delete: the next DELETE Mine answered 503 "busy" by the probe', eval: `${P}.inject({ method: 'DELETE', path: 'Mine$', mode: 'stub', status: 503, body: { title: 'Service Unavailable', status: 503, detail: ${JSON.stringify(BUSY)} } })` },
    { name: 'TV failed delete: the focus on Delete', script: `${P}.section().querySelector('.jfur-own-delete').focus(); return ${P}.active();` },
    { name: 'TV failed delete: OK', key: 'Enter' },
    { sleep: 500 },
    { name: 'TV failed delete: the question, Cancel focused', assert: "document.activeElement && document.activeElement.matches('.jfur-backdrop .jfur-dialog-cancel')" },
    { name: 'TV failed delete: Right to Delete', key: 'ArrowRight' },
    { sleep: 300 },
    { name: 'TV failed delete: on the dialog\'s Delete', assert: "document.activeElement && document.activeElement.matches('.jfur-backdrop .jfur-dialog-confirm')" },
    { name: 'TV failed delete: OK (503)', key: 'Enter' },
    { sleep: 800 },
    state('TV failed delete: after the 503', 'tvDelete'),
    server('after the 503', 'movieId', 'tvDeleteServer'),
    { name: 'TV failed delete: "weren\'t deleted" with Try again, nothing deleted, and the focus still on Delete, never the page\'s body', assert: `__vars.tvDelete.notices.some(n => n.text === ${JSON.stringify("Your rating and review weren't deleted. " + BUSY)} && n.action === 'Try again') && __vars.tvDeleteServer.text === 'A synthetic review, written for the TV check.' && document.activeElement !== document.body && document.activeElement.matches('${SECTION} .jfur-own-delete') && document.activeElement.getAttribute('aria-disabled') === null` },
    { name: 'TV failed delete: Down moves on from there', key: 'ArrowDown' },
    { sleep: 400 },
    { name: "TV failed delete: the D-pad still works from there: the focus never on the page's body", assert: 'document.activeElement !== document.body' },
    { name: 'TV: screenshot', screenshot: 'userratings-details-tv.png' },
    clean('Long Test Movie at the end', 'movieId'),
    { name: 'the default layout again', eval: "(localStorage.removeItem('layout'), true)" },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    { name: 'TV: the key log', eval: `${P}.log`, full: true }
);
write('userratings-details-tv.json', `User Ratings ${VERSION}: the details page's section on Long Test Movie as viewer in jellyfin-web 10.11.5's TV layout: the title no tab stop and the slider the only focusable control of its row; the D-pad reaches it from Play; Left and Right consumed with the focus kept; OK saves; Up and Down leave and come back, Down to Clear or Write a review; Back with a change waiting leaves the page and the change is saved once; a Delete answered 503 leaves the focus on Delete, never the page's body. The member's row is deleted at the end.`, tv);

// ------------------------------------------------------------------------------------------------ two windows

// Window B: a browser context of its own (webcheck's context step), signed in as the same member, the probe in it too.
function secondWindow(name, user) {
    return [
        { name: `${name}: a browser context of its own`, context: name },
        { name: `${name}: the details probe there too`, initScript: { file: 'probes/userratings-details-probe.js' } },
        { name: `${name}: sign in as ${user}`, login: user },
        { name: `${name}: desktop layout`, eval: "(localStorage.removeItem('layout'), true)" },
        { name: `${name}: reload`, reload: true },
        { name: `${name}: User Ratings ${VERSION} started`, waitFor: "!!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)", timeout: 40000 }
    ];
}

const seedReview = (label, id, stars, textValue) => ({
    name: `${label}: the member's ${stars} stars and review, through the API`,
    script: `const a = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.${id} + '/Mine/Rating', { Stars: ${stars} }); const b = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.${id} + '/Mine/Review', { Text: ${JSON.stringify(textValue)}, IsSpoiler: false }); return [a.status, b.status];`
});

const win =start({ user: 'viewer', layout: 'desktop', movies: [['movieId', 'Long Test Movie'], ['taggedId', 'Tagged Movie']] });
win.push(
    seedReview('Long Test Movie', 'movieId', 3, 'Window A original'),
    server('Long Test Movie as seeded', 'movieId', 'seeded'),
    ...openItem('window A: Long Test Movie', 'movieId'),
    { name: 'window A: Edit', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    { name: 'window A: the caret at the end', script: 'const a = document.activeElement; a.selectionStart = a.selectionEnd = a.value.length; return a.tagName;' },
    { name: 'window A: type', type: ', edited in window A' },
    ...secondWindow('window-b', 'viewer'),
    ...openItem('window B: Long Test Movie', 'movieId'),
    { name: 'window B: Edit', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    { name: 'window B: its own text', script: "const a = document.activeElement; a.select(); return a.tagName;" },
    { name: 'window B: type over it', type: 'WINDOW B REVIEW' },
    { name: 'window B: Save', click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    server('after window B saved', 'movieId', 'afterB'),
    { name: 'window B: saved, a 200', assert: `__vars.afterB.text === 'WINDOW B REVIEW' && !${STATE}.form && ${STATE}.own === 'WINDOW B REVIEW'` },
    { name: 'back to window A', context: 'main' },
    mark('window A rating'),
    focusSlider,
    { name: 'window A: Right (3.5)', key: 'ArrowRight' },
    { name: 'window A: Enter', key: 'Enter' },
    { sleep: 900 },
    since('window A rating', 'aRating'),
    state('window A after its rating', 'aAfterRating'),
    {
        name: 'window A: the rating saved, its answer carrying window B\'s row; the form keeps window A\'s text and says the review was changed in another window',
        assert: "__vars.aRating.length === 1 && / 200$/.test(__vars.aRating[0]) && __vars.aAfterRating.form && __vars.aAfterRating.formText === 'Window A original, edited in window A' && __vars.aAfterRating.notices.some(n => /changed in another window/.test(n.text))"
    },
    mark('window A Save'),
    { name: 'window A: Save', click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    since('window A Save', 'aSave'),
    state('window A after Save', 'aAfterSave'),
    server('after window A\'s Save', 'movieId', 'afterA'),
    { name: 'window A\'s Save', eval: lastBody('Review'), save: 'aSaveBody', full: true },
    {
        name: 'window A: Save sent the review\'s time its form started from (IfReviewUpdatedUtc), the server answered 409 and still holds window B\'s review; window A\'s text kept in its form, said; the item read for the 409\'s panel (before the fix: the rating\'s answer gave B\'s time, the Save was a 200 and B\'s text was gone)',
        assert: "__vars.aSave.length === 2 && / 409$/.test(__vars.aSave[0]) && __vars.aSave[1] === 'GET /UserRatings/Items/' + __vars.movieId + ' 200' && __vars.aSaveBody.IfReviewUpdatedUtc === __vars.seeded.reviewUpdated && !('IfUpdatedUtc' in __vars.aSaveBody) && __vars.afterA.text === 'WINDOW B REVIEW' && __vars.aAfterSave.form && __vars.aAfterSave.formText === 'Window A original, edited in window A' && __vars.aAfterSave.notices.some(n => /changed in another window/.test(n.text))"
    },
    {
        name: 'window A: the 409 shows window B\'s review as it is now, read-only and isolated, and Overwrite once it is read',
        assert: "(c => !!c && c.label === 'Your review as it is now:' && c.text === 'WINDOW B REVIEW' && c.dir === 'auto' && c.editable === 0 && c.status === null)(__vars.aAfterSave.conflictPanel) && __vars.aAfterSave.overwrite"
    },
    // The control: on Tagged Movie the rating's answer leaves the review as the form found it, and Save goes through; now
    // the review's own time is the precondition, so the stars changed in another window (through the
    // API, as one does) don't make it a 409 either.
    seedReview('Tagged Movie', 'taggedId', 2, 'Control original'),
    server('Tagged Movie as seeded', 'taggedId', 'controlSeeded'),
    ...openItem('window A: Tagged Movie', 'taggedId'),
    { name: 'control: Edit', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    { name: 'control: the caret at the end', script: 'const a = document.activeElement; a.selectionStart = a.selectionEnd = a.value.length; return a.tagName;' },
    { name: 'control: type', type: ', edited' },
    focusSlider,
    { name: 'control: Right (2.5)', key: 'ArrowRight' },
    { name: 'control: Enter', key: 'Enter' },
    { sleep: 900 },
    server('after the control\'s rating', 'taggedId', 'controlRated'),
    state('control after its rating', 'controlAfterRating'),
    { name: 'control: nothing to say, the form as typed; the stars moved the row\'s time, never the review\'s', assert: "__vars.controlRated.stars === 2.5 && __vars.controlAfterRating.form && !__vars.controlAfterRating.notices.some(n => /another window/.test(n.text)) && __vars.controlRated.updated !== __vars.controlSeeded.updated && __vars.controlRated.reviewUpdated === __vars.controlSeeded.reviewUpdated" },
    { name: 'stars elsewhere: the stars changed in another window (PUT Mine/Rating through the API)', script: `const r = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.taggedId + '/Mine/Rating', { Stars: 4 }); return r.status;`, save: 'otherStars' },
    { name: 'stars elsewhere: saved there', assert: '__vars.otherStars === 200' },
    mark('control Save'),
    { name: 'control: Save', click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    since('control Save', 'controlSave'),
    server('after the control\'s Save', 'taggedId', 'controlSaved'),
    { name: 'control\'s Save', eval: lastBody('Review'), save: 'controlBody', full: true },
    { name: 'control and stars elsewhere: Save sent the review\'s own time as the form read it, a 200 although the row moved twice since (this window\'s stars, then another\'s); the text saved, the other window\'s stars kept', assert: "__vars.controlSave.length === 1 && / 200$/.test(__vars.controlSave[0]) && __vars.controlBody.IfReviewUpdatedUtc === __vars.controlSeeded.reviewUpdated && !('IfUpdatedUtc' in __vars.controlBody) && __vars.controlSaved.text === 'Control original, edited' && __vars.controlSaved.stars === 4" },
    { name: 'window A: screenshot', screenshot: 'userratings-details-windows.png' },
    clean('Long Test Movie at the end', 'movieId'),
    clean('Tagged Movie at the end', 'taggedId'),
    { name: 'window A: no uncaught exception', noExceptions: true, ignore: IGNORE },
    { name: 'window A: every request of the section', eval: `${P}.since(0)`, full: true },
    { name: 'window B again', context: 'window-b' },
    { name: 'window B: every request of the section', eval: `${P}.since(0)`, full: true }
);
write('userratings-details-windows.json', `User Ratings ${VERSION}, a 409 with two windows: as viewer in two browser contexts (two windows of the same member): window A opens Edit on its review of Long Test Movie; window B saves another review; window A then changes its stars, whose answer carries window B's row; window A's Save sends the review's time its form started from (IfReviewUpdatedUtc) and gets the 409, window B's review stays and window A's text stays in its form, with the form saying so (the reviewer's overwrite), and the 409 shows window B's review as it is now, read-only. The control, on Tagged Movie: this window's stars and then another window's (through the API) move the row's time but never the review's, and Save goes through with the review's own time. The member's rows are deleted at the end.`, win);

// ---------------------------------------------------------------------------------------------- three windows

// A reviewer's sequence in three windows of viewer: window A edits its review; window B saves
// one; A's Save is a 409 and A reloads (B's review in the text area, A's text kept beside it); A edits B's review in the
// text area; window C saves one; A's Save is a 409 again and A reloads. Before the fix that second Reload put A's edit of
// B's text in the place of A's first text, which was gone.
const editAndSave = (label, textValue) => [
    { name: `${label}: Edit`, click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    { name: `${label}: its text selected`, script: 'const a = document.activeElement; a.select(); return a.tagName;' },
    { name: `${label}: type over it`, type: textValue },
    { name: `${label}: Save`, click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    { name: `${label}: saved, the form closed`, assert: `!${STATE}.form && ${STATE}.own === ${JSON.stringify(textValue)}` }
];
const A_FIRST = 'A first: window A\'s own words';
const A_EDIT = ', and window A\'s edit of it';
// The text window A made of B's review, as a script literal (it has an apostrophe).
const B_EDITED = JSON.stringify('B saved' + A_EDIT);
const three = start({ user: 'viewer', layout: 'desktop', movies: [['movieId', 'Long Test Movie']] });
three.push(
    seedReview('Long Test Movie', 'movieId', 3, 'T0, the review as seeded'),
    ...openItem('window A: Long Test Movie', 'movieId'),
    { name: 'window A: Edit', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    { name: 'window A: its text selected', script: 'const a = document.activeElement; a.select(); return a.tagName;' },
    { name: 'window A: type over it', type: A_FIRST },
    ...secondWindow('window-b', 'viewer'),
    ...openItem('window B: Long Test Movie', 'movieId'),
    ...editAndSave('window B', 'B saved'),
    { name: 'window A', context: 'main' },
    { name: 'window A: Save (409)', click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    state('window A: the first 409', 'first409'),
    { name: 'window A: the first 409 shows "B saved"', assert: `(c => !!c && c.text === 'B saved')(__vars.first409.conflictPanel) && __vars.first409.formText === ${JSON.stringify(A_FIRST)}` },
    { name: 'window A: Reload', click: `${SECTION} .jfur-conflict-reload` },
    { sleep: 900 },
    state('window A: after the first Reload', 'firstReload'),
    { name: 'window A: "B saved" in the text area, its own text beside it', assert: `__vars.firstReload.formText === 'B saved' && JSON.stringify(__vars.firstReload.kept.map(k => [k.label, k.text])) === JSON.stringify([['Your text, not saved', ${JSON.stringify(A_FIRST)}]])` },
    { name: 'window A: the text area', script: `const a = ${P}.section().querySelector('.jfur-review-form textarea'); a.focus(); a.selectionStart = a.selectionEnd = a.value.length; return a.tagName;` },
    { name: 'window A: edits B\'s review', type: A_EDIT },
    ...secondWindow('window-c', 'viewer'),
    ...openItem('window C: Long Test Movie', 'movieId'),
    ...editAndSave('window C', 'C saved'),
    server('after window C saved', 'movieId', 'afterC'),
    { name: 'window A again', context: 'main' },
    mark('window A second Save'),
    { name: 'window A: Save (409 again)', click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    state('window A: the second 409', 'second409'),
    { name: 'window A: the second 409 shows "C saved"', assert: `(c => !!c && c.text === 'C saved')(__vars.second409.conflictPanel) && __vars.second409.formText === ${B_EDITED}` },
    { name: 'window A: Reload again', click: `${SECTION} .jfur-conflict-reload` },
    { sleep: 900 },
    state('window A: after the second Reload', 'secondReload'),
    { name: 'window A: the draft as stored', eval: `(() => { const k = ${drafts}; return k.length === 1 ? JSON.parse(localStorage.getItem(k[0])).kept.map(x => x.text) : k.length; })()`, save: 'draftKept', full: true },
    {
        name: 'window A: "C saved" in the text area, and both of the member\'s texts kept beside it, in the order written, and in the draft (before the fix: "A first" was gone)',
        assert: `__vars.secondReload.formText === 'C saved' && JSON.stringify(__vars.secondReload.kept.map(k => [k.label, k.text])) === JSON.stringify([['Your text, not saved', ${JSON.stringify(A_FIRST)}], ['Your text, not saved', ${B_EDITED}]]) && JSON.stringify(__vars.draftKept) === JSON.stringify([${JSON.stringify(A_FIRST)}, ${B_EDITED}])`
    },
    // A reload of the page brings all of it back from the draft.
    { name: 'window A: reload the page', reload: true },
    { name: 'window A: started', waitFor: '!!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)', timeout: 40000 },
    ...openItem('window A: Long Test Movie after the reload', 'movieId'),
    { name: 'window A: Continue editing', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    state('window A: the draft put back', 'putBack'),
    { name: 'window A: "C saved" and both kept texts back after the reload', assert: `__vars.putBack.formText === 'C saved' && __vars.putBack.restored && JSON.stringify(__vars.putBack.kept.map(k => k.text)) === JSON.stringify([${JSON.stringify(A_FIRST)}, ${B_EDITED}])` },
    { name: 'window A: Use my text on the first', click: `${SECTION} .jfur-aside-use` },
    state('window A: after Use my text', 'used'),
    { name: 'window A: its first text in the text area; the other kept, and "C saved" beside it as the other window\'s', assert: `__vars.used.formText === ${JSON.stringify(A_FIRST)} && JSON.stringify(__vars.used.kept.map(k => [k.label, k.text])) === JSON.stringify([['Your text, not saved', ${B_EDITED}], ['Your review as saved in the other window', 'C saved']])` },
    mark('window A last Save'),
    { name: 'window A: Save', click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    since('window A last Save', 'lastSave'),
    { name: 'window A: what Save sent', eval: lastBody('Review'), save: 'lastBody', full: true },
    server('after window A saved', 'movieId', 'afterA'),
    { name: 'window A: a 200 against window C\'s review (its time from the Reload); window A\'s first text saved', assert: `__vars.lastSave.length === 1 && / 200$/.test(__vars.lastSave[0]) && __vars.lastBody.IfReviewUpdatedUtc === __vars.afterC.reviewUpdated && __vars.afterA.text === ${JSON.stringify(A_FIRST)}` },
    // The Save took one of window A's texts; its other one stays beside the form and in the draft
    // (before: the form closed and dropped it without a word); window C's review, which the save replaced as the member
    // chose, goes. Discard lets the text go, and Cancel closes the form with nothing kept.
    state('window A: after the save', 'savedKept'),
    { name: 'window A: the form stays open on the text as saved, window A\'s other text beside it (window C\'s review gone), the sentence with Discard, and a draft keeping it', assert: `__vars.savedKept.form && __vars.savedKept.formText === ${JSON.stringify(A_FIRST)} && JSON.stringify(__vars.savedKept.kept.map(k => [k.label, k.text])) === JSON.stringify([['Your text, not saved', ${B_EDITED}]]) && __vars.savedKept.restored && document.querySelector('${SECTION} .jfur-review-restored .jfur-notice-text').textContent === 'Review saved. The other texts you kept are still beside the form.' && ${drafts}.length === 1` },
    { name: 'window A: Discard', click: `${SECTION} .jfur-review-restored .jfur-draft-discard` },
    state('window A: after Discard', 'discarded'),
    { name: 'window A: nothing kept beside the form, the draft gone', assert: `__vars.discarded.form && __vars.discarded.formText === ${JSON.stringify(A_FIRST)} && __vars.discarded.kept.length === 0 && ${drafts}.length === 0` },
    { name: 'window A: Cancel', click: `${SECTION} .jfur-review-cancel` },
    { sleep: 300 },
    state('window A: after Cancel', 'closed'),
    { name: 'window A: the form closed with no draft; the own card shows the text as saved', assert: `!__vars.closed.form && __vars.closed.own === ${JSON.stringify(A_FIRST)} && ${drafts}.length === 0` },
    { name: 'window A: screenshot', screenshot: 'userratings-details-three-windows.png' },
    clean('Long Test Movie at the end', 'movieId'),
    { name: 'window A: no draft left', script: `${drafts}.forEach(k => localStorage.removeItem(k)); return ${drafts}.length;` },
    { name: 'window A: no uncaught exception', noExceptions: true, ignore: IGNORE },
    { name: 'window A: every request of the section', eval: `${P}.since(0)`, full: true },
    { name: 'window B', context: 'window-b' },
    { name: 'window B: no draft left', script: `${drafts}.forEach(k => localStorage.removeItem(k)); return ${drafts}.length;` },
    { name: 'window C', context: 'window-c' },
    { name: 'window C: no draft left', script: `${drafts}.forEach(k => localStorage.removeItem(k)); return ${drafts}.length;` }
);
write('userratings-details-three-windows.json', `User Ratings ${VERSION}, Reload after Reload: as viewer in three browser contexts (three windows of the same member), the reviewer's sequence: window A edits its review of Long Test Movie; window B saves one; A's Save is a 409 (the panel shows B's review) and A reloads: B's review in the text area, A's text kept beside it; A edits B's review there; window C saves one; A's Save is a 409 again and A reloads: C's review in the text area and both of A's texts kept beside it, in the order written, and in the draft (before the fix the second Reload dropped A's first text); a reload of the page brings all of it back; Use my text puts A's first text in the text area with the other kept and C's review beside it; Save sends C's review time and saves A's text, and the form stays open with A's other text beside it and in the draft, C's review gone; Discard lets it go and Cancel closes the form with no draft. The member's row is deleted at the end.`, three);

// --------------------------------------------------------------------------------------------------------- part 2

const PLUGIN_ID = 'f53c7c34-8cba-4846-b43b-956816b2f6ae';
const SECRET = 'SPOILER-MARK the twist is that the narrator was the lighthouse.';
const DEFAULTS = "{ WhoCanRate: 'AllUsers', RaterUserIds: [], RequirePlayedToRate: false }";
// The admin's window: the plugin's settings changed through POST /Plugins/{id}/Configuration (ApiClient's
// updatePluginConfiguration), then read back.
const setConfig = (label, patch, save) => ({
    name: `admin: ${label} (POST /Plugins/{id}/Configuration)`,
    script: `const c = await ApiClient.getPluginConfiguration('${PLUGIN_ID}'); const before = { WhoCanRate: c.WhoCanRate, RaterUserIds: c.RaterUserIds, RequirePlayedToRate: c.RequirePlayedToRate }; await ApiClient.updatePluginConfiguration('${PLUGIN_ID}', Object.assign(c, ${patch})); const b = await ApiClient.getPluginConfiguration('${PLUGIN_ID}'); return { before, WhoCanRate: b.WhoCanRate, RaterUserIds: b.RaterUserIds, RequirePlayedToRate: b.RequirePlayedToRate };`,
    save,
    full: true
});
const caretEnd =(label) => ({ name: `${label}: the caret at the end`, script: 'const a = document.activeElement; a.selectionStart = a.selectionEnd = a.value.length; return a.tagName;' });

const p2 = start({ user: 'viewer', layout: 'desktop', movies: [['movieId', 'Long Test Movie'], ['taggedId', 'Tagged Movie']] });
p2.push(
    // The settings as the sandbox has them, whatever an earlier run left.
    ...secondWindow('admin', 'admin'),
    { name: 'admin: viewer\'s id', eval: "(await ApiClient.getUsers()).find(u => u.Name === 'viewer').Id", save: 'viewerId' },
    setConfig('who can rate: all users, no played rule (the defaults, whatever an earlier run left)', DEFAULTS, 'cfgStart'),
    { name: 'admin: the defaults in place', assert: "__vars.cfgStart.WhoCanRate === 'AllUsers' && __vars.cfgStart.RaterUserIds.length === 0 && __vars.cfgStart.RequirePlayedToRate === false" },
    // viewer2's window: their spoiler review of Long Test Movie (also the older own row for the selected raters).
    ...secondWindow('other', 'viewer2'),
    clean('Long Test Movie (viewer2)', 'movieId'),
    clean('Tagged Movie (viewer2)', 'taggedId'),
    { name: 'viewer2: 4 stars and a review marked as a spoiler, through the API', script: `const a = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Rating', { Stars: 4 }); const b = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Review', { Text: ${JSON.stringify(SECRET)}, IsSpoiler: true }); return [a.status, b.status];`, save: 'spoilerSeed' },
    { name: 'viewer2: saved', assert: '__vars.spoilerSeed[0] === 200 && __vars.spoilerSeed[1] === 200' },

    // ------------------------------------------------------------ spoilers (viewer)
    { name: 'viewer\'s window', context: 'main' },
    ...openItem('spoilers: Long Test Movie', 'movieId'),
    state('spoilers: the section', 'sp'),
    { name: 'spoilers: viewer2\'s row marked; its text nowhere in the page (window.find, the text of the page)', assert: `__vars.sp.spoilerRows.length === 1 && !${P}.findable('the narrator was the lighthouse')` },
    { name: 'spoilers: its place', eval: `(() => { const r = ${P}.section().querySelector('.jfur-spoiler-toggle').closest('.jfur-review'); const b = r.querySelector('.jfur-spoiler-blur'); return { blur: !!b, hidden: b && b.getAttribute('aria-hidden'), text: b && b.textContent, filter: b && getComputedStyle(b).filter, lines: b ? b.querySelectorAll('.jfur-spoiler-line').length : 0, reviewText: !!r.querySelector('.jfur-review-text'), toggle: r.querySelector('.jfur-spoiler-toggle').textContent }; })()`, save: 'blur', full: true },
    { name: 'spoilers: a blur with no text (aria-hidden), the real text not in the page, "Show spoiler"', assert: "__vars.blur.blur && __vars.blur.hidden === 'true' && __vars.blur.text === '' && /blur\\(/.test(__vars.blur.filter) && __vars.blur.lines >= 1 && !__vars.blur.reviewText && __vars.blur.toggle === 'Show spoiler'" },
    { name: 'spoilers: Show spoiler', click: `${SECTION} .jfur-spoiler-toggle` },
    { sleep: 200 },
    { name: 'spoilers: the text in the page now, the button "Hide spoiler" and expanded', assert: `${P}.findable('the narrator was the lighthouse') && ${P}.section().querySelector('.jfur-spoiler-toggle').getAttribute('aria-expanded') === 'true' && ${P}.section().querySelector('.jfur-spoiler-toggle').textContent === 'Hide spoiler'` },
    { name: 'spoilers: Hide spoiler', click: `${SECTION} .jfur-spoiler-toggle` },
    { sleep: 200 },
    { name: 'spoilers: out of the page again', assert: `!${P}.findable('the narrator was the lighthouse') && !!${P}.section().querySelector('.jfur-spoiler-blur')` },
    // The form's "Contains spoilers".
    { name: 'flag: Write a review', click: `${SECTION} .jfur-write` },
    { sleep: 300 },
    { name: 'flag: "Contains spoilers", not ticked', assert: `${STATE}.formSpoiler === false && ${P}.section().querySelector('.jfur-review-spoiler-field').textContent === 'Contains spoilers'` },
    { name: 'flag: type', type: 'A synthetic review that gives the ending away.' },
    { name: 'flag: tick Contains spoilers', click: `${SECTION} .jfur-review-form input.jfur-review-spoiler` },
    { name: 'flag: ticked', assert: `${STATE}.formSpoiler === true` },
    mark('flag Save'),
    { name: 'flag: Save', click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    since('flag Save', 'flagRequests'),
    { name: 'flag: what was sent', eval: lastBody('Review'), save: 'flagBody', full: true },
    state('flag: after Save', 'afterFlag'),
    { name: 'flag: IsSpoiler true sent and saved, the own card says "Contains spoilers"', assert: "__vars.flagRequests.length === 1 && / 200$/.test(__vars.flagRequests[0]) && __vars.flagBody.IsSpoiler === true && __vars.afterFlag.ownSpoiler && !__vars.afterFlag.form" },
    { name: 'flag: Edit', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    { name: 'flag: Edit keeps the row\'s flag', assert: `${STATE}.formSpoiler === true` },
    { name: 'flag: Cancel', click: `${SECTION} .jfur-review-cancel` },
    { name: 'viewer2\'s window', context: 'other' },
    ...openItem('viewer2 on Long Test Movie', 'movieId'),
    state('viewer2: the section', 'v2'),
    { name: 'viewer2: viewer\'s review marked as a spoiler, its text not in the page', assert: `__vars.v2.spoilerRows.length === 1 && !${P}.findable('gives the ending away')` },

    // ------------------------------------------------------------ the 409: Reload, then Overwrite (two windows of viewer)
    { name: 'back to viewer\'s window (A)', context: 'main' },
    { name: '409: Edit in window A', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    caretEnd('409'),
    { name: '409: window A types', type: ' Window A adds this.' },
    ...secondWindow('window-b', 'viewer'),
    ...openItem('409: window B', 'movieId'),
    { name: '409: Edit in window B', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    { name: '409: window B selects its text', script: 'const a = document.activeElement; a.select(); return a.tagName;' },
    { name: '409: window B types over it', type: 'WINDOW B TEXT' },
    { name: '409: window B saves', click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    server('after window B saved', 'movieId', 'afterB'),
    { name: '409: window B saved', assert: "__vars.afterB.text === 'WINDOW B TEXT'" },
    { name: 'window A', context: 'main' },
    mark('409 Save'),
    { name: '409: window A saves', click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    since('409 Save', 'a409'),
    state('409: after window A\'s Save', 'afterA409'),
    { name: '409: a 409, the form kept with Reload, the focus on Reload; window B\'s text on the server; the item read for the 409\'s panel', assert: `__vars.a409.length === 2 && / 409$/.test(__vars.a409[0]) && __vars.a409[1] === 'GET /UserRatings/Items/' + __vars.movieId + ' 200' && __vars.afterA409.conflict && __vars.afterA409.formText === 'A synthetic review that gives the ending away. Window A adds this.' && document.activeElement.matches('${SECTION} .jfur-conflict-reload')` },
    { name: '409: the panel shows window B\'s review as it is now, a spoiler (window B kept the row\'s flag), so hidden until asked; Overwrite shown', assert: `(c => !!c && c.label === 'Your review as it is now:' && c.spoiler && c.text === null && c.editable === 0)(__vars.afterA409.conflictPanel) && __vars.afterA409.overwrite && !${P}.findable('WINDOW B TEXT')` },
    { name: '409: Reload (Enter on it)', key: 'Enter' },
    { sleep: 900 },
    state('409: after Reload', 'reloaded'),
    { name: '409: Reload: window B\'s text in the form, window A\'s beside it ("Your text, not saved"), the draft holding both', assert: `__vars.reloaded.formText === 'WINDOW B TEXT' && __vars.reloaded.kept.length === 1 && __vars.reloaded.kept[0].label === 'Your text, not saved' && __vars.reloaded.kept[0].text === 'A synthetic review that gives the ending away. Window A adds this.' && __vars.reloaded.formSpoiler === true && ${drafts}.length === 1 && JSON.parse(localStorage.getItem(${drafts}[0])).kept.length === 1` },
    { name: '409: Use my text', click: `${SECTION} .jfur-aside-use` },
    state('409: after Use my text', 'swapped'),
    { name: '409: window A\'s text in the form, window B\'s beside it', assert: "__vars.swapped.formText === 'A synthetic review that gives the ending away. Window A adds this.' && __vars.swapped.kept.length === 1 && __vars.swapped.kept[0].text === 'WINDOW B TEXT' && __vars.swapped.kept[0].label === 'Your review as saved in the other window' && __vars.swapped.kept[0].use === 'Use that text'" },
    mark('409 Save after Reload'),
    { name: '409: Save', click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    since('409 Save after Reload', 'afterReloadSave'),
    { name: '409: what Save sent', eval: lastBody('Review'), save: 'reloadBody', full: true },
    server('after the Save after Reload', 'movieId', 'reloadSaved'),
    { name: '409: Save after Reload sent window B\'s review time and went through; window A\'s text saved; the draft gone', assert: `__vars.afterReloadSave.length === 1 && / 200$/.test(__vars.afterReloadSave[0]) && __vars.reloadBody.IfReviewUpdatedUtc === __vars.afterB.reviewUpdated && __vars.reloadSaved.text === 'A synthetic review that gives the ending away. Window A adds this.' && ${drafts}.length === 0` },
    // Overwrite.
    { name: 'Overwrite: Edit in window A', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    caretEnd('Overwrite'),
    { name: 'Overwrite: window A types', type: ' Overwrite.' },
    { name: 'window B again', context: 'window-b' },
    { name: 'Overwrite: window B saves its review again (through the API, as another window does)', script: `const m = await ${P}.mine(__vars.movieId); const r = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Review', { Text: 'WINDOW B AGAIN', IsSpoiler: false, IfUpdatedUtc: m.updated }); return r.status;`, save: 'bAgain' },
    server('after window B again', 'movieId', 'afterBAgain'),
    { name: 'window A once more', context: 'main' },
    mark('Overwrite'),
    { name: 'Overwrite: window A saves (409)', click: `${SECTION} .jfur-review-save` },
    { sleep: 900 },
    state('Overwrite: the 409', 'ow409'),
    { name: 'Overwrite: the panel shows "WINDOW B AGAIN", isolated, read-only, and Overwrite', assert: "(c => !!c && c.label === 'Your review as it is now:' && c.text === 'WINDOW B AGAIN' && c.dir === 'auto' && !c.spoiler && c.editable === 0)(__vars.ow409.conflictPanel) && __vars.ow409.overwrite" },
    // Question 4: Overwrite sends the time of the row the panel shows, with no read first; a change made after it was
    // shown is a 409 again, with the newer text shown.
    { name: 'window B, a third time', context: 'window-b' },
    { name: 'Overwrite: window B saves once more while window A shows the 409 (through the API)', script: `const m = await ${P}.mine(__vars.movieId); const r = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Review', { Text: 'WINDOW B THIRD', IsSpoiler: false, IfReviewUpdatedUtc: m.reviewUpdated }); return r.status;`, save: 'bThird' },
    server('after window B\'s third save', 'movieId', 'afterBThird'),
    { name: 'window A, with the 409 shown', context: 'main' },
    { name: 'Overwrite: Overwrite (the review shown is no longer the newest)', click: `${SECTION} .jfur-conflict-overwrite` },
    { sleep: 1200 },
    state('Overwrite: after the first Overwrite', 'ow409Again'),
    server('after the first Overwrite', 'movieId', 'afterFirstOverwrite'),
    { name: 'Overwrite: a 409 again, nothing overwritten, the panel now showing "WINDOW B THIRD"; window A\'s text still in its form', assert: "__vars.bThird === 200 && __vars.afterFirstOverwrite.text === 'WINDOW B THIRD' && (c => !!c && c.text === 'WINDOW B THIRD')(__vars.ow409Again.conflictPanel) && __vars.ow409Again.overwrite && __vars.ow409Again.formText === 'A synthetic review that gives the ending away. Window A adds this. Overwrite.'" },
    { name: 'Overwrite: Overwrite again', click: `${SECTION} .jfur-conflict-overwrite` },
    { sleep: 1200 },
    since('Overwrite', 'overwrite'),
    { name: 'Overwrite: what it sent', eval: lastBody('Review'), save: 'overwriteBody', full: true },
    server('after Overwrite', 'movieId', 'overwritten'),
    { name: 'Overwrite: Save\'s 409 and its read, Overwrite\'s 409 (window B\'s newer save) and its read, then Overwrite with the review time the panel showed, a 200, no read of its own; window A\'s text saved', assert: `__vars.bAgain === 200 && __vars.overwrite.length === 5 && / 409$/.test(__vars.overwrite[0]) && /^GET \\/UserRatings\\/Items\\/[0-9a-f]+ 200$/.test(__vars.overwrite[1]) && / 409$/.test(__vars.overwrite[2]) && /^GET \\/UserRatings\\/Items\\/[0-9a-f]+ 200$/.test(__vars.overwrite[3]) && / 200$/.test(__vars.overwrite[4]) && __vars.overwriteBody.IfReviewUpdatedUtc === __vars.afterBThird.reviewUpdated && __vars.overwritten.text === 'A synthetic review that gives the ending away. Window A adds this. Overwrite.' && !${STATE}.form` },

    // ------------------------------------------------------------ drafts across navigation, Back, a reload, sign-out
    { name: 'drafts: Edit', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    caretEnd('drafts'),
    { name: 'drafts: type', type: ' A draft.' },
    { sleep: 700 },
    { name: 'drafts: what this plugin keeps in the browser', eval: `${P}.storage()`, save: 'stored', full: true },
    { name: 'drafts: one draft, jfur:draft:<viewer>:<item>, after the pause', assert: `${drafts}.length === 1 && ${drafts}[0] === 'jfur:draft:' + __vars.viewerId.replace(/-/g, '').toLowerCase() + ':' + __vars.movieId.replace(/-/g, '').toLowerCase()` },
    ...openItem('drafts: Tagged Movie (another item)', 'taggedId'),
    { name: 'drafts: still kept', assert: `${drafts}.length === 1` },
    { name: 'drafts: Back', script: 'history.back(); return true;' },
    { name: 'drafts: Long Test Movie again', waitFor: MOUNTED('movieId'), timeout: 15000 },
    { sleep: 400 },
    state('drafts: back on the item', 'back'),
    { name: 'drafts: Edit reads "Continue editing"', assert: "__vars.back.editLabel === 'Continue editing'" },
    { name: 'drafts: Continue editing', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    state('drafts: the form', 'restored'),
    { name: 'drafts: the text put back, "Your unsaved text was put back." with Discard', assert: "/ A draft\\.$/.test(__vars.restored.formText) && __vars.restored.restored && __vars.restored.notices.some(n => n.text === 'Your unsaved text was put back.' && n.action === 'Discard')" },
    { name: 'drafts: Discard', click: `${SECTION} .jfur-draft-discard` },
    state('drafts: after Discard', 'discarded'),
    { name: 'drafts: the review as saved, the draft gone', assert: `__vars.discarded.formText === __vars.overwritten.text && ${drafts}.length === 0` },
    caretEnd('reload'),
    { name: 'reload: type', type: ' Kept over a reload.' },
    { sleep: 700 },
    { name: 'reload: reload the page', reload: true },
    { name: 'reload: started', waitFor: '!!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)', timeout: 40000 },
    ...openItem('reload: Long Test Movie', 'movieId'),
    { name: 'reload: Continue editing', click: `${SECTION} .jfur-own-edit` },
    { sleep: 300 },
    { name: 'reload: the draft put back after a reload', assert: `/ Kept over a reload\\.$/.test(${STATE}.formText) && ${STATE}.restored` },
    { name: 'reload: Cancel (the draft kept)', click: `${SECTION} .jfur-review-cancel` },
    { name: 'reload: kept', assert: `${drafts}.length === 1 && ${STATE}.editLabel === 'Continue editing'` },
    // TV Back (Escape) with the form open.
    { name: 'TV: the TV layout', eval: "(localStorage.setItem('layout', 'tv'), true)" },
    { name: 'TV: reload', reload: true },
    { name: 'TV: started in the TV layout', waitFor: "document.documentElement.classList.contains('layout-tv') && !!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)", timeout: 40000 },
    ...openItem('TV: Long Test Movie', 'movieId'),
    { name: 'TV: Continue editing (OK on it)', script: `${P}.section().querySelector('.jfur-own-edit').focus(); return ${P}.active();` },
    { name: 'TV: OK', key: 'Enter' },
    { sleep: 300 },
    caretEnd('TV'),
    { name: 'TV: type more', type: ' And on the TV.' },
    // Back pressed from inside the text area itself, where the member is typing.
    { name: 'TV: the focus still in the text area', assert: `document.activeElement && document.activeElement.matches('${SECTION} .jfur-review-form textarea')` },
    { name: 'TV: Back (Escape) at once, from the text area', key: 'Escape' },
    { sleep: 300 },
    { name: 'TV: the page left, the draft written at once (before its pause)', assert: `location.hash.indexOf(__vars.movieId) < 0 && ${drafts}.length === 1 && JSON.parse(localStorage.getItem(${drafts}[0])).text.endsWith(' And on the TV.')` },
    { name: 'TV: forward again', script: 'history.forward(); return true;' },
    { name: 'TV: the item again', waitFor: MOUNTED('movieId'), timeout: 15000 },
    { sleep: 400 },
    { name: 'TV: Continue editing', script: `${P}.section().querySelector('.jfur-own-edit').click(); return true;` },
    { sleep: 300 },
    { name: 'TV: the text as typed on the TV, put back', assert: `/ And on the TV\\.$/.test(${STATE}.formText) && ${STATE}.restored` },
    { name: 'TV: Cancel', script: `${P}.section().querySelector('.jfur-review-cancel').click(); return true;` },
    { name: 'TV: the desktop layout again', eval: "(localStorage.removeItem('layout'), true)" },
    { name: 'TV: reload', reload: true },
    { name: 'desktop again: started', waitFor: '!!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)', timeout: 40000 },
    // Sign-out removes the member's drafts.
    { name: 'sign-out: a draft there', assert: `${drafts}.length === 1` },
    { name: 'sign-out: the header drawn', waitFor: "(() => { const b = document.querySelector('.skinHeader .mainDrawerButton'); if (!b) return false; const r = b.getBoundingClientRect(); return r.width > 0 && r.height > 0; })()", timeout: 20000 },
    { name: 'sign-out: open the drawer', click: '.skinHeader .mainDrawerButton', js: true },
    { name: 'sign-out: Sign out shown', waitFor: "!!document.querySelector('.mainDrawer .btnLogout')" },
    { name: 'sign-out: sign out from the drawer', click: '.mainDrawer .btnLogout', js: true },
    { name: 'sign-out: on the login page', waitFor: "location.hash.startsWith('#/login') && !!document.querySelector('#txtManualName')", timeout: 20000 },
    { name: 'sign-out: the member\'s drafts gone', waitFor: `${drafts}.length === 0`, timeout: 5000 },
    { name: 'sign-out: sign in again', login: 'viewer', stay: true },
    { name: 'sign-out: started again', waitFor: '!!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)', timeout: 40000 },

    // ------------------------------------------------------------ the read-only states
    { name: 'admin\'s window', context: 'admin' },
    setConfig('who can rate: selected users, viewer only', "{ WhoCanRate: 'SelectedUsers', RaterUserIds: [__vars.viewerId], RequirePlayedToRate: false }", 'cfgSelected'),
    { name: 'admin: SelectedUsers with viewer only', assert: "__vars.cfgSelected.WhoCanRate === 'SelectedUsers' && __vars.cfgSelected.RaterUserIds.length === 1 && __vars.cfgSelected.RaterUserIds[0].replace(/-/g, '').toLowerCase() === __vars.viewerId.replace(/-/g, '').toLowerCase()" },
    { name: 'viewer2\'s window', context: 'other' },
    // Another item first: the next view of Long Test Movie reads it again (as a member's next visit does).
    ...openItem('2b: viewer2 on Tagged Movie first', 'taggedId'),
    ...openItem('2b: viewer2 on Long Test Movie', 'movieId'),
    state('2b: viewer2\'s section', 'ro'),
    { name: 'selected raters: read-only stars "Rated 4 out of 5" with "Rating is limited to selected members.", no tab stop among them, the slider gone; viewer2\'s own Clear and Delete there', assert: "__vars.ro.readOnly && __vars.ro.readOnly.label === 'Rated 4 out of 5' && __vars.ro.readOnly.note === 'Rating is limited to selected members.' && __vars.ro.readOnly.focusables === 0 && !__vars.ro.sliderShown && __vars.ro.reason === 'NotARater' && __vars.ro.clear && !__vars.ro.write" },
    { name: 'selected raters: viewer\'s rating and review still read by viewer2', assert: "__vars.ro.names.indexOf('viewer') >= 0 && __vars.ro.average !== null" },
    { name: 'selected raters: focus Play', script: `${P}.log.length = 0; ${PLAY}.focus(); return ${P}.active();` }
);
for (let i = 1; i <= 12; i++) {
    p2.push({ name: `selected raters: Tab ${i}`, key: 'Tab', if: `!document.activeElement.matches('${SECTION} .jfur-clear')` });
}

p2.push(
    { name: 'selected raters: the Tab path', eval: `${P}.log.filter(e => e.kind === 'focus').map(f => f.target)`, save: 'roTabs', full: true },
    { name: 'selected raters: Tab reaches Clear past the stars, never stopping on them', assert: `document.activeElement.matches('${SECTION} .jfur-clear') && !__vars.roTabs.some(t => /jfur-stars|jfur-mine-readonly/.test(t))` },
    { name: 'selected raters: PUT Mine/Rating as viewer2', script: `const r = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Rating', { Stars: 2 }); return r.status;`, save: 'roPut' },
    { name: 'selected raters: 403', assert: '__vars.roPut === 403' },
    { name: 'selected raters: Delete viewer2\'s older row', click: `${SECTION} .jfur-own-delete` },
    { sleep: 300 },
    { name: 'selected raters: Delete (confirm)', click: '.jfur-backdrop .jfur-dialog-confirm' },
    { sleep: 900 },
    server('2b: viewer2 after Delete', 'movieId', 'roDeleted'),
    { name: 'selected raters: viewer2 could delete their own older row', assert: '__vars.roDeleted.stars === null && __vars.roDeleted.text === null' },
    { name: 'admin', context: 'admin' },
    setConfig('who can rate: all users again', "{ WhoCanRate: 'AllUsers', RaterUserIds: [] }", 'cfgAll'),
    { name: 'viewer2', context: 'other' },
    ...openItem('2b: viewer2 on Tagged Movie', 'taggedId'),
    ...openItem('2b: viewer2 on Long Test Movie again', 'movieId'),
    state('2b: viewer2 may rate again', 'rateAgain'),
    { name: 'selected raters: the slider again, enabled', assert: '__vars.rateAgain.sliderShown && __vars.rateAgain.disabled === null && !__vars.rateAgain.readOnly' },
    mark('2b rate'),
    focusSlider,
    { name: 'selected raters: End', key: 'End' },
    { name: 'selected raters: Enter', key: 'Enter' },
    { sleep: 800 },
    since('2b rate', 'rateAgainRequests'),
    server('2b: viewer2 rated', 'movieId', 'v2Rated'),
    state('2b: after viewer2 rated', 'afterV2Rate'),
    { name: 'selected raters: All users again: viewer2 rates (200), and viewer\'s row is still there (no data changed)', assert: "__vars.rateAgainRequests.length === 1 && / 200$/.test(__vars.rateAgainRequests[0]) && __vars.v2Rated.stars === 5 && __vars.afterV2Rate.names.indexOf('viewer') >= 0" },
    // On the TV, the slider turned read-only under the focus (viewer2 taken out of the raters while
    // on the page) gives the focus to the read-only stars, never the page's body; the page shown again reads the item
    // again, and once viewer2 may rate, the slider takes the focus back.
    { name: 'read-only under the focus: viewer2 in the TV layout', eval: "(localStorage.setItem('layout', 'tv'), true)" },
    { name: 'read-only under the focus: reload', reload: true },
    { name: 'read-only under the focus: started in the TV layout', waitFor: "document.documentElement.classList.contains('layout-tv') && !!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)", timeout: 40000 },
    ...openItem('read-only under the focus: viewer2 on Long Test Movie in the TV layout', 'movieId'),
    { name: 'read-only under the focus: the slider, enabled', assert: `${STATE}.sliderShown && ${STATE}.disabled === null && ${STATE}.value === 5` },
    { name: 'admin', context: 'admin' },
    setConfig('who can rate: selected users, viewer only (viewer2 on the page)', "{ WhoCanRate: 'SelectedUsers', RaterUserIds: [__vars.viewerId], RequirePlayedToRate: false }", 'cfgB4'),
    { name: 'viewer2', context: 'other' },
    focusSlider,
    { name: 'read-only under the focus: Left (4.5)', key: 'ArrowLeft' },
    { name: 'read-only under the focus: OK', key: 'Enter' },
    { sleep: 1200 },
    state('read-only under the focus: after the 403', 'b4'),
    server('read-only under the focus: viewer2 after the 403', 'movieId', 'b4Server'),
    { name: 'read-only under the focus: the 403 read the item: read-only stars in the slider\'s place, the focus on them (tabindex -1: still no tab stop), never the page\'s body; nothing saved', assert: `__vars.b4.readOnly && __vars.b4.readOnly.label === 'Rated 5 out of 5' && !__vars.b4.sliderShown && __vars.b4.reason === 'NotARater' && document.activeElement !== document.body && document.activeElement.matches('${SECTION} .jfur-mine-readonly') && document.activeElement.getAttribute('tabindex') === '-1' && __vars.b4Server.stars === 5` },
    { name: 'read-only under the focus: Down', key: 'ArrowDown' },
    { sleep: 400 },
    { name: 'read-only under the focus: where Down went', eval: `${P}.active()`, save: 'b4Down' },
    { name: 'read-only under the focus: the D-pad goes on from the read-only stars: the focus never on the page\'s body', assert: 'document.activeElement !== document.body' },
    { name: 'admin', context: 'admin' },
    setConfig('who can rate: all users again', "{ WhoCanRate: 'AllUsers', RaterUserIds: [] }", 'cfgB4Back'),
    { name: 'viewer2', context: 'other' },
    { name: 'read-only under the focus: the focus on the read-only stars again', script: `${P}.section().querySelector('.jfur-mine-readonly').focus(); return ${P}.active();` },
    { name: 'read-only under the focus: the page hidden and shown again (visibilitychange dispatched by the webcheck, as a switch of tabs does it)', script: "Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' }); document.dispatchEvent(new Event('visibilitychange')); Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' }); document.dispatchEvent(new Event('visibilitychange')); delete document.visibilityState; return document.visibilityState;" },
    { sleep: 1000 },
    state('read-only under the focus: shown again', 'b4Back'),
    { name: 'read-only under the focus: read again on the way back (NotARater): the slider back, enabled, and the focus back on it', assert: '__vars.b4Back.sliderShown && __vars.b4Back.disabled === null && !__vars.b4Back.readOnly && __vars.b4Back.onSlider' },
    { name: 'read-only under the focus: the desktop layout again', eval: "(localStorage.removeItem('layout'), true)" },
    { name: 'read-only under the focus: reload', reload: true },
    { name: 'read-only under the focus: started in the desktop layout', waitFor: "!document.documentElement.classList.contains('layout-tv') && !!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)", timeout: 40000 },
    // NeedsPlay: the played rule on, an item viewer2 hasn't played.
    { name: 'admin', context: 'admin' },
    setConfig('rating needs the item played', '{ RequirePlayedToRate: true }', 'cfgPlayed'),
    { name: 'viewer2', context: 'other' },
    ...openItem('NeedsPlay: viewer2 on Tagged Movie', 'taggedId'),
    state('NeedsPlay: the section', 'np'),
    { name: 'NeedsPlay: the disabled slider, a tab stop, its note beside it as its description', assert: "__vars.np.sliderShown && __vars.np.disabled === 'true' && __vars.np.reason === 'NeedsPlay' && __vars.np.sliderNote === 'You can rate this once you have played it.' && !__vars.np.readOnly && !__vars.np.write" },
    focusSlider,
    { name: 'NeedsPlay: Tab and the D-pad reach it: it takes the focus', assert: `${P}.onSlider() && ${P}.slider().getAttribute('tabindex') === '0'` },
    mark('NeedsPlay keys'),
    { name: 'NeedsPlay: Right', key: 'ArrowRight' },
    { name: 'NeedsPlay: Enter', key: 'Enter' },
    { sleep: 900 },
    since('NeedsPlay keys', 'npKeys'),
    { name: 'NeedsPlay: nothing sent', assert: "__vars.npKeys.length === 0" },
    // ReadOnly: the item's Can as a read-only store answers it (the probe puts Reason ReadOnly over the server's answer; the
    // real read-only store is userratings-api-probe.mjs --readonly-drill's).
    { name: 'admin', context: 'admin' },
    setConfig('the defaults again', DEFAULTS, 'cfgBack'),
    { name: 'viewer\'s window', context: 'main' },
    ...openItem('ReadOnly: viewer on Tagged Movie', 'taggedId'),
    { name: 'ReadOnly: the next read of Long Test Movie answered with Can.Reason ReadOnly (simulated)', eval: `${P}.inject({ method: 'GET', path: 'Items/' + __vars.movieId + '$', mode: 'patch', patch: { Can: { Rate: false, Review: false, Moderate: false, Reason: 'ReadOnly' } } })` },
    ...openItem('ReadOnly: viewer on Long Test Movie', 'movieId'),
    state('ReadOnly: the section', 'rdo'),
    { name: 'ReadOnly: the disabled slider with "Ratings can\'t be changed right now.", no Clear, no Edit or Delete', assert: "__vars.rdo.sliderShown && __vars.rdo.disabled === 'true' && __vars.rdo.reason === 'ReadOnly' && __vars.rdo.sliderNote === \"Ratings can't be changed right now.\" && !__vars.rdo.clear && __vars.rdo.editLabel === null" },
    { name: 'ReadOnly: screenshot', screenshot: 'userratings-details-part2.png' },

    // ------------------------------------------------------------ the end: every row of the run, the settings as they were
    clean('Long Test Movie at the end', 'movieId'),
    clean('Tagged Movie at the end', 'taggedId'),
    { name: 'viewer: no draft left', script: `${drafts}.forEach(k => localStorage.removeItem(k)); return ${drafts}.length;` },
    { name: 'viewer: no uncaught exception', noExceptions: true, ignore: IGNORE },
    { name: 'viewer: every request of the section', eval: `${P}.since(0)`, full: true },
    { name: 'viewer2', context: 'other' },
    clean('Long Test Movie at the end (viewer2)', 'movieId'),
    clean('Tagged Movie at the end (viewer2)', 'taggedId'),
    { name: 'window B', context: 'window-b' },
    { name: 'window B: no draft left', script: `${drafts}.forEach(k => localStorage.removeItem(k)); return ${drafts}.length;` },
    { name: 'admin', context: 'admin' },
    setConfig('the defaults at the end', DEFAULTS, 'cfgEnd'),
    { name: 'admin: the defaults in place', assert: "__vars.cfgEnd.WhoCanRate === 'AllUsers' && __vars.cfgEnd.RaterUserIds.length === 0 && __vars.cfgEnd.RequirePlayedToRate === false" }
);
write('userratings-details-part2.json', `User Ratings ${VERSION}, spoilers, drafts and who may rate: in the default sandbox, in four browser contexts (viewer, a second window of viewer, viewer2, admin): a review marked as a spoiler is out of the page until Show spoiler (window.find doesn't find it; a blur with no text in its place) and out again after Hide spoiler; the form's "Contains spoilers" sent and kept by Edit, the own card saying it, viewer2 seeing it hidden; the 409 with two windows: its panel shows the review as it is now (a spoiler there hidden until asked), Reload puts the other window's text in the form and keeps this one's beside it, "Use my text" swaps them, Save sends the newer review's time; Overwrite sends the review time of the row its panel shows, with no read of its own, so a save made after the panel was shown is a 409 again with the newer text shown, then Overwrite saves; drafts: saved after a pause under jfur:draft:<user>:<item>, kept across another item and Back, a reload, and TV Back pressed from inside the text area (written at once), put back with Discard, removed at sign-out; selected raters after setting WhoCanRate = SelectedUsers (viewer only) through POST /Plugins/{id}/Configuration: viewer2 sees read-only stars with "Rating is limited to selected members." and no tab stop there, gets 403 from PUT Mine/Rating, still reads viewer's review, deletes an older own row, and rates again once All users is set back, nothing else changed; in the TV layout, taken out of the raters while on the page, a rating's 403 turns the focused slider into the read-only stars, which take the focus (never the page's body), and the page shown again (visibilitychange) reads the item and gives the slider the focus back once viewer2 may rate (on the TV, never the page's body); RequirePlayedToRate: the disabled slider with its note on an unplayed item, a tab stop, nothing sent; ReadOnly (the probe's answer) the disabled slider with its note. The settings are put back to the defaults and every row of the run deleted at the end.`, p2);

// ----------------------------------------------------------------------------------------------------- touch

const PHONE = { width: 390, height: 844, mobile: true, touch: true };
const tc = start({ user: 'viewer', layout: 'mobile', movies: [['movieId', 'Long Test Movie']], emulate: PHONE });

function touchEvent(kind, xy) {
    const points = kind === 'touchEnd' ? '[]' : `[{ x: Math.round(${xy[0]}), y: Math.round(${xy[1]}) }]`;
    return { cdp: 'Input.dispatchTouchEvent', params: `({ type: '${kind}', touchPoints: ${points} })` };
}

const SY = `${P}.box(${P}.slider()).cy`;
const centre = (label) => ({ name: `touch: the stars in the middle (${label})`, script: `${P}.slider().scrollIntoView({ block: 'center' }); await new Promise(r => setTimeout(r, 500)); window.__mark = ${P}.requests.length; return Math.round(window.scrollY);` });
tc.push(
    ...openItem('Long Test Movie', 'movieId'),
    { name: 'touch: styles at 390 px', eval: STYLES, save: 'touchStyles', full: true },
    { name: 'touch: no part of the section past the edges of the screen; in the slider\'s row the slider and its − / + only (− off at "Not rated")', assert: "(s => s.section.right <= s.viewport.w + 1 && s.section.x >= -1 && s.overflow.length === 0 && s.rowFocusables.some(f => f.indexOf('div.jfur-stars.focusable') === 0) && s.rowFocusables.every(f => /^(div\\.jfur-stars\\.focusable|button\\.jfur-stars-step)/.test(f)))(__vars.touchStyles)" },
    centre('the tap'),
    touchEvent('touchStart', [`${P}.xFor(3.75)`, SY]),
    touchEvent('touchEnd', null),
    { sleep: 1000 },
    server('after the tap', 'movieId', 'tapServer'),
    { name: 'touch: a tap on star 4\'s right half saved 4.0', assert: `${STATE}.value === 4 && __vars.tapServer.stars === 4 && ${P}.since(window.__mark).filter(r => r.indexOf('PUT ') === 0).length === 1` },
    { name: 'touch: the summary\'s histogram hidden under 600 px', assert: `${STATE}.summaryKind === 'average' && ${STYLES}.histogram === 'none'` },
    centre('the drag'),
    touchEvent('touchStart', [`${P}.xFor(3.75)`, SY])
);
for (let i = 1; i <= 8; i++) {
    tc.push(touchEvent('touchMove', [`${P}.xFor(3.75) - ${P}.box(${P}.slider()).w * 2 / 5 * ${i} / 8`, SY]));
}

tc.push(
    touchEvent('touchEnd', null),
    { sleep: 1000 },
    server('after the drag', 'movieId', 'dragServer'),
    { name: 'touch: a drag two stars to the left saved 2.0 once', assert: `${STATE}.value === 2 && __vars.dragServer.stars === 2 && ${P}.since(window.__mark).filter(r => r.indexOf('PUT ') === 0).length === 1` },
    { name: 'touch: + in view', script: `${P}.section().querySelector('.jfur-stars-plus').scrollIntoView({ block: 'center' }); await new Promise(r => setTimeout(r, 500)); window.__mark = ${P}.requests.length; return true;` },
    touchEvent('touchStart', [`${P}.box('${SECTION} .jfur-stars-plus').cx`, `${P}.box('${SECTION} .jfur-stars-plus').cy`]),
    touchEvent('touchEnd', null),
    { sleep: 250 },
    touchEvent('touchStart', [`${P}.box('${SECTION} .jfur-stars-plus').cx`, `${P}.box('${SECTION} .jfur-stars-plus').cy`]),
    touchEvent('touchEnd', null),
    { sleep: 250 },
    touchEvent('touchStart', [`${P}.box('${SECTION} .jfur-stars-minus').cx`, `${P}.box('${SECTION} .jfur-stars-minus').cy`]),
    touchEvent('touchEnd', null),
    { sleep: 1100 },
    server('after + + −', 'movieId', 'buttonsServer'),
    { name: 'touch: + + − saved 2.5 once', assert: `${STATE}.value === 2.5 && __vars.buttonsServer.stars === 2.5 && ${P}.since(window.__mark).filter(r => r.indexOf('PUT ') === 0).length === 1` },
    { name: 'touch: Write a review', click: `${SECTION} .jfur-write` },
    { sleep: 300 },
    { name: 'touch: the form fits the screen', eval: STYLES, save: 'formStyles', full: true },
    { name: 'touch: no part of the section past the edges of the screen with the form open', assert: '__vars.formStyles.section.right <= __vars.formStyles.viewport.w + 1 && __vars.formStyles.overflow.length === 0' },
    { name: 'touch: Cancel', click: `${SECTION} .jfur-review-cancel` },
    { name: 'touch: screenshot', screenshot: 'userratings-details-touch.png' },
    clean('Long Test Movie at the end', 'movieId'),
    { name: 'desktop again', emulate: 'desktop' },
    { name: 'the default layout again', eval: "(localStorage.removeItem('layout'), true)" },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE }
);
write('userratings-details-touch.json', `User Ratings ${VERSION}: the details page's section on Long Test Movie as viewer in the mobile layout at 390x844 with touch: no part of the section past the screen's edges; a tap on star 4's right half saves 4.0; a sideways drag saves 2.0 once; + + − saves 2.5 once; the histogram hidden under 600 px; the review form fits. Touches through Input.dispatchTouchEvent. The member's row is deleted at the end.`, tc);

// --------------------------------------------------------------------------------------------------- reviews

// Run by scripts/userratings-details-probe.mjs, which seeds the reviews first (synthetic members urdetails-NN, and viewer's
// own 4.5 with a review) and deletes them afterwards; viewer2 reads them here.
const rv = start({ user: 'viewer2', layout: 'desktop', movies: [['movieId', 'Long Test Movie']], clean: false });
const ORDER = (sort, limit) => `(async () => { const r = await ${P}.server('GET', 'UserRatings/Items/' + __vars.movieId + '/Reviews?startIndex=0&limit=${limit}&sort=${sort}'); return r.json.Items.map(i => i.Id.replace(/-/g, '')); })()`;
rv.push(
    server('Long Test Movie, seeded', 'movieId', 'seeded'),
    { name: 'the seed is there: more than 20 other members\' rows (run through userratings-details-probe.mjs)', assert: '__vars.seeded.status === 200 && __vars.seeded.others > 20 && __vars.seeded.count > 20', message: 'no seed: run node scripts/userratings-details-probe.mjs --sandbox default' },
    ...openItem('Long Test Movie', 'movieId'),
    state('viewer2: the section', 'rvState'),
    {
        name: 'viewer2 sees the members\' average with viewer\'s rating in it, the counts, the histogram, viewer\'s row in the list (without the chip)',
        assert: "(s => s.summaryKind === 'average' && s.average === 'Member average ' + (Math.round(__vars.seeded.average * 10) / 10).toFixed(1) + ' out of 5, ' + __vars.seeded.count + ' ratings' && s.histogram && /^How members rated: /.test(s.histogram.label) && s.histogram.shown && s.names.indexOf('viewer') >= 0 && s.reviews.length === 20 && s.pager && s.toolbar)(__vars.rvState)"
    },
    { name: 'the counts as the server has them', assert: `${STATE}.summary.indexOf(__vars.seeded.count + ' ratings') >= 0` },
    // Paging past 20.
    mark('Load more'),
    { name: 'Load more', click: `${SECTION} .jfur-pager button` },
    { sleep: 800 },
    since('Load more', 'moreRequests'),
    state('after Load more', 'afterMore'),
    { name: 'the next page\'s query', eval: `(() => { const q = new URLSearchParams(${P}.requests[${P}.requests.length - 1].query); return { startIndex: q.get('startIndex'), limit: q.get('limit'), sort: q.get('sort') }; })()`, save: 'moreQuery', full: true },
    { name: 'the next page (startIndex 20), every row once, Load more gone at the end (paging past 20)', assert: "__vars.moreRequests.length === 1 && __vars.moreRequests[0] === 'GET /UserRatings/Items/' + __vars.movieId + '/Reviews 200' && __vars.moreQuery.startIndex === '20' && __vars.moreQuery.limit === '20' && __vars.moreQuery.sort === 'Newest' && __vars.afterMore.reviews.length === __vars.seeded.others && new Set(__vars.afterMore.reviews).size === __vars.afterMore.reviews.length && !__vars.afterMore.pager" },
    // Every row shown now: the members' words isolated and as text.
    { name: 'names and texts isolated, texts as text (an override left open stays in its element; "<b>" is text)', eval: `(() => { const st = ${STYLES}; const s = ${P}.section(); return { isolated: st.isolated.filter(i => /jfur-review-(name|text)/.test(i.cls)), b: s.querySelectorAll('.jfur-review b').length, rlo: Array.prototype.filter.call(s.querySelectorAll('.jfur-review-text'), n => n.textContent.indexOf('\\u202E') >= 0).length, html: Array.prototype.filter.call(s.querySelectorAll('.jfur-review-text'), n => n.textContent.indexOf('<b>') >= 0).length, arabic: Array.prototype.filter.call(s.querySelectorAll('.jfur-review-name'), n => /[\\u0600-\\u06FF]/.test(n.textContent)).length }; })()`, save: 'bidi', full: true },
    { name: 'every name and text dir="auto" and unicode-bidi: isolate; the seeded override, Arabic name and "<b>" there, no HTML from a review', assert: "__vars.bidi.isolated.length > 20 && __vars.bidi.isolated.every(i => i.dir === 'auto' && i.unicodeBidi === 'isolate') && __vars.bidi.b === 0 && __vars.bidi.rlo === 1 && __vars.bidi.html === 1 && __vars.bidi.arabic === 1" },
    // Show more on the long review.
    { name: 'the long review: its Show more', eval: `(() => { const b = ${P}.section().querySelector('.jfur-review-more'); const t = b.parentNode.querySelector('.jfur-review-text'); return { label: b.textContent, expanded: b.getAttribute('aria-expanded'), lines: t.textContent.split('\\n').length, ellipsis: /…$/.test(t.textContent) }; })()`, save: 'moreBefore', full: true },
    { name: 'the start of it with "…" and Show more', assert: "__vars.moreBefore.label === 'Show more' && __vars.moreBefore.expanded === 'false' && __vars.moreBefore.ellipsis && __vars.moreBefore.lines <= 6" },
    { name: 'Show more', click: `${SECTION} .jfur-review-more` },
    { name: 'all of it, Show less', eval: `(() => { const b = ${P}.section().querySelector('.jfur-review-more'); const t = b.parentNode.querySelector('.jfur-review-text'); return { label: b.textContent, expanded: b.getAttribute('aria-expanded'), lines: t.textContent.split('\\n').length, ellipsis: /…$/.test(t.textContent) }; })()`, save: 'moreAfter', full: true },
    { name: 'the whole review', assert: "__vars.moreAfter.label === 'Show less' && __vars.moreAfter.expanded === 'true' && !__vars.moreAfter.ellipsis && __vars.moreAfter.lines > 6" },
    { name: 'the list in view', script: `${P}.section().querySelector('.jfur-review-more').scrollIntoView({ block: 'center' }); await new Promise(r => setTimeout(r, 400)); return true;` },
    { name: 'screenshot (the list)', screenshot: 'userratings-details-list.png' }
);
for (const sort of ['Oldest', 'Highest', 'Lowest', 'Newest']) {
    rv.push(
        { name: `order ${sort}: the server's own first page in it`, eval: ORDER(sort, 20), save: 'order' + sort, full: true },
        { name: `order ${sort}`, script: `const s = ${P}.section().querySelector('select.jfur-sort'); s.value = '${sort}'; s.dispatchEvent(new Event('change', { bubbles: true })); return s.value;` },
        { sleep: 800 },
        { name: `order ${sort}: the rows`, eval: `${STATE}.reviews`, save: 'shown' + sort, full: true },
        { name: `order ${sort}: the rows in the server's order (sorts)`, assert: `JSON.stringify(__vars.shown${sort}) === JSON.stringify(__vars.order${sort}) && ${STATE}.pager` }
    );
}

rv.push(
    mark('Load more 403'),
    { name: 'the next GET Reviews answered 403 by the probe (reviews turned off since the page was read)', eval: `${P}.inject({ method: 'GET', path: '/Reviews$', mode: 'stub', status: 403, body: { title: 'Forbidden', status: 403, detail: 'Reviews are turned off on this server.' } })` },
    { name: 'Load more (403)', click: `${SECTION} .jfur-pager button` },
    { sleep: 500 },
    state('after Load more\'s 403', 'reviewsOff'),
    { name: '"Reviews are turned off on this server.", the list, Load more and Write a review gone, no error, no Retry; the slider and the summary stay', assert: `(s => s.reviewsOff && s.reviews.length === 0 && !s.pager && !s.toolbar && !s.write && s.notices.length === 0 && s.summaryKind === 'average')(__vars.reviewsOff) && ${noAlert} && ${P}.displayed(${P}.slider())` },
    { name: 'screenshot (reviews)', screenshot: 'userratings-details-reviews.png' },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    { name: 'every request of the section', eval: `${P}.since(0)`, full: true }
);
write('userratings-details-reviews.json', `User Ratings ${VERSION}: run by scripts/userratings-details-probe.mjs after it seeded more than 20 synthetic members' ratings and reviews and viewer's own on Long Test Movie; as viewer2: the average with viewer's rating in it, the counts, the histogram and viewer's row (item 2a without the chip); every name and text isolated and as text; Show more on a long review; Load more past 20, each row once; the four orders as the server gives them; Load more's 403 shows "Reviews are turned off on this server." and removes the list and the form, with no error.`, rv);

// -------------------------------------------------------------------------------------------------------- je

const je = start({ user: 'admin', layout: 'desktop', movies: [['movieId', 'Long Test Movie'], ['taggedId', 'Tagged Movie']] });
const reach = (label, presses, save) => {
    const out = [{ name: `${label}: focus the Play button`, script: `${P}.log.length = 0; ${PLAY}.focus(); return ${P}.active();` }];
    for (let i = 1; i <= presses; i++) {
        out.push({ name: `${label}: ArrowDown ${i}`, key: 'ArrowDown', if: `!${P}.onSlider()` }, { sleep: 250, if: `!${P}.onSlider()` });
    }

    out.push({ name: `${label}: where the D-pad went`, eval: `({ onSlider: ${P}.onSlider(), active: ${P}.active(), presses: ${P}.log.filter(e => e.kind === 'key-capture' && e.key === 'ArrowDown').length, prevented: ${P}.log.filter(e => e.kind === 'key-bubble' && e.key === 'ArrowDown').map(e => e.prevented), path: ${P}.log.filter(e => e.kind === 'focus').map(f => f.target) })`, save, full: true });
    return out;
};
je.push(
    { name: 'Jellyfin Enhanced\'s version', eval: "(async () => { const ps = await ApiClient.getJSON(ApiClient.getUrl('Plugins')); const p = ps.find(x => x.Name === 'Jellyfin Enhanced'); return p ? { Version: p.Version, Status: p.Status } : null; })()", save: 'jeVersion', full: true },
    { name: 'Jellyfin Enhanced is there', assert: '!!__vars.jeVersion && !!window.JellyfinEnhanced' },
    ...openItem('Tagged Movie (TMDb id 900001: JE draws its review section)', 'taggedId'),
    { sleep: 5000 },
    { name: 'JE\'s review parts and the column\'s order', eval: `${P}.jeReviews()`, save: 'jeParts', full: true },
    { name: 'our section and JE\'s review section both in the page', assert: `__vars.jeParts.tmdbSection === 1 && !!${P}.section()` },
    { name: 'collisions on Tagged Movie', eval: `${P}.collisions()`, save: 'collisions', full: true },
    { name: 'no style rule of JE\'s (or anyone\'s but jellyfin-web\'s) on the section or its chip, our ids unique, no jfur- class elsewhere but the item\'s chip and its Rate button (Auto shows it beside JE\'s Reviews)', assert: '__vars.collisions.foreign.length === 0 && __vars.collisions.duplicateIds.length === 0 && __vars.collisions.foreignJfur.length === 0 && __vars.collisions.rate === true' },
    mark('JE keys'),
    focusSlider,
    { name: 'JE: Right', key: 'ArrowRight' },
    { name: 'JE: End', key: 'End' },
    { name: 'JE: Enter', key: 'Enter' },
    { sleep: 600 },
    server('Tagged Movie after the keys', 'taggedId', 'jeKeysServer'),
    { name: 'the slider\'s keys reach it past JE\'s capture listener: End and Enter saved 5', assert: `__vars.jeKeysServer.stars === 5 && ${lastKey('End')}.prevented === true` },
    { name: 'JE TV: TV layout', eval: "(localStorage.setItem('layout', 'tv'), true)" },
    { name: 'JE TV: reload', reload: true },
    { name: 'JE TV: layout-tv, JE\'s key listener up', waitFor: "document.documentElement.classList.contains('layout-tv') && !!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur) && !!window.JellyfinEnhanced && !!window.JellyfinEnhanced.keyListener", timeout: 40000 },
    ...openItem('JE TV: Long Test Movie (no TMDb id)', 'movieId'),
    { sleep: 3000 },
    ...reach('JE TV Long Test Movie', 6, 'jeTvLong'),
    { name: 'JE TV: on Long Test Movie the D-pad reaches the slider', assert: '__vars.jeTvLong.onSlider === true' },
    ...openItem('JE TV: Tagged Movie', 'taggedId'),
    { sleep: 5000 },
    ...reach('JE TV Tagged Movie with the section', 8, 'jeTvTagged'),
    // JE 12.14's closed Reviews held the D-pad in the buttons row here until Bookmarks 1.2.0, whose kit takes
    // their controls out of the TV layout's focus order on the whole page (kit.je.tvSectionJump; the je sandbox has
    // Bookmarks): since then the D-pad passes them.
    { name: 'JE TV: with the section, recorded (JE 12.14\'s closed Reviews held the D-pad in the buttons row until the kit\'s TV fix)', eval: '__vars.jeTvTagged', full: true },
    { name: 'JE TV: past JE\'s closed Reviews the D-pad reaches the slider, none of the Downs prevented by us, nothing of ours on the way', assert: "__vars.jeTvTagged.onSlider === true && __vars.jeTvTagged.prevented.every(p => p === false) && !__vars.jeTvTagged.path.slice(0, -1).some(p => /jfur-/.test(p))" },
    { name: 'JE TV: the section kept out (the probe answers the item\'s read 404, as for an item that isn\'t rateable)', eval: `${P}.inject({ method: 'GET', path: '/UserRatings/Items/[0-9a-f]+$', mode: 'stub', status: 404, times: 20, body: { title: 'Not Found', status: 404, detail: ${JSON.stringify(NOT_FOUND)} } })` },
    { name: 'JE TV: to Long Test Movie and back to Tagged Movie', script: "location.hash = '#/details?id=' + __vars.movieId; return true;" },
    { waitFor: DRAWN('movieId'), timeout: 30000 },
    { sleep: 1500 },
    { name: 'JE TV: Tagged Movie again', script: "location.hash = '#/details?id=' + __vars.taggedId; return true;" },
    { waitFor: DRAWN('taggedId'), timeout: 30000 },
    { sleep: 5000 },
    { name: 'JE TV: no section now', assert: `!${P}.section()` },
    ...reach('JE TV Tagged Movie without the section', 8, 'jeTvBare'),
    { name: 'JE TV: without the section the D-pad leaves the buttons row too, none of the Downs prevented, nothing of ours on its way', assert: '__vars.jeTvBare.path.length > 0 && !__vars.jeTvBare.onSlider && __vars.jeTvBare.prevented.every(p => p === false) && !__vars.jeTvBare.path.some(p => /jfur-/.test(p))' },
    { name: 'the probe\'s stub off', script: `${P}.rules.forEach(r => { r.times = 0; }); return true;` },
    clean('Tagged Movie at the end', 'taggedId'),
    clean('Long Test Movie at the end', 'movieId'),
    { name: 'the default layout again', eval: "(localStorage.removeItem('layout'), true)" },
    { name: 'no uncaught exception of ours', noExceptions: true, ignore: IGNORE + '|JellyfinEnhanced|/JellyfinEnhanced/' }
);
write('userratings-details-je.json', `User Ratings ${VERSION} next to Jellyfin Enhanced 12.14, in the je sandbox as admin: the details page's section on Tagged Movie beside Jellyfin Enhanced's review section, no style rule of JE's on it, our ids unique; the slider's keys reach it past JE's capture listener and save; in the TV layout the D-pad reaches the slider on Long Test Movie, and on Tagged Movie past JE 12.14's closed Reviews (out of the TV focus order since Bookmarks 1.2.0's kit) it reaches the slider too, and without the section (the probe answering the item's read 404) it leaves the buttons row, none of the Downs prevented by us. The admin's rows are deleted at the end; no setting is changed.`, je);

// -------------------------------------------------------------------------------- the chip and the Rate button

// The fixture's Test Series (two seasons), its first season and Test Album A (New-SandboxMedia.ps1).
const OF_TYPE = (type, name) => `(await ApiClient.getItems(ApiClient.getCurrentUserId(), { IncludeItemTypes: '${type}', Recursive: true, SearchTerm: ${JSON.stringify(name)}, Limit: 1 })).Items[0].Id`;
const FIRST_SEASON = "(await ApiClient.getItems(ApiClient.getCurrentUserId(), { ParentId: __vars.seriesId, IncludeItemTypes: 'Season', SortBy: 'SortName' })).Items[0].Id";
const ids = (season) => [
    { name: 'Test Series', eval: OF_TYPE('Series', 'Test Series'), save: 'seriesId' },
    ...(season ? [{ name: 'Test Series\' first season', eval: FIRST_SEASON, save: 'seasonId' }, clean('the first season', 'seasonId')] : []),
    { name: 'Test Album A', eval: OF_TYPE('MusicAlbum', 'Test Album A'), save: 'albumId' }
];
const EXTRAS = `${P}.extras()`;
const extras = (label, save) => ({ name: `${label}: the chip and the Rate button`, eval: EXTRAS, save, full: true });
const expected = (label, id, save) => ({ name: `${label}: the chip the server's summary calls for`, eval: `${P}.expectedChip(__vars.${id})`, save, full: true });
// The chip as the summary calls for it: none, or in the row, last, its value, name and title, for the item shown.
const chipAsExpected = (x, e, id) => `((x, e) => e === null ? x.chip === null && x.chips === 0 : !!x.chip && x.chips === 1 && x.chip.item === __vars.${id} && x.chip.displayed && x.row.chipLast && x.chip.value === e.value && x.chip.label === e.label && x.chip.title === e.label && x.chip.iconWidth > 0)(__vars.${x}, __vars.${e})`;
// In the je sandbox JE adds its audio-language item to the row after the chip, so there the chip is anywhere in the row, once.
const chipOnceAsExpected = (x, e, id) => `((x, e) => e !== null && !!x.chip && x.chips === 1 && x.chip.item === __vars.${id} && x.chip.displayed && x.chip.value === e.value && x.chip.label === e.label && x.chip.title === e.label && x.chip.iconWidth > 0)(__vars.${x}, __vars.${e})`;
const rateBeforeMore = (x) => `(x => !!x.rate && x.rates === 1 && x.rate.displayed && x.rate.title === 'Rate' && x.rate.label === 'Rate' && /btnMoreCommands/.test(x.rate.next) && x.jumps === 0)(__vars.${x})`;
// In the je sandbox Bookmarks' "Add bookmark" goes before More too, and which of the two comes last depends on timing:
// there the Rate button is before More, anywhere in the row.
const rateAheadOfMore = (x) => `(x => !!x.rate && x.rates === 1 && x.rate.displayed && x.rate.title === 'Rate' && x.rate.label === 'Rate' && x.rate.beforeMore === true && x.jumps === 0)(__vars.${x})`;
const noRate = (x) => `(x => x.rate === null && x.rates === 0)(__vars.${x})`;
const sliderInView = `(b => b.y >= 0 && b.y + b.h <= window.innerHeight)(${P}.box(${P}.slider()))`;
const healthClean = { name: 'the health check: no anchor of User Ratings missing on these pages', eval: 'window.__jfkit.jfur.health.missing()', save: 'missing', full: true };
const healthNone = { name: 'nothing missing', assert: '__vars.missing.length === 0' };

const ch = start({ user: 'viewer', layout: 'desktop', movies: [['movieId', 'Long Test Movie']] });
ch.push(
    ...ids(true),
    ...openItem('Long Test Movie', 'movieId'),
    expected('Long Test Movie before', 'movieId', 'chipBefore'),
    extras('Long Test Movie before', 'xBefore'),
    { name: 'the chip as the summary calls for it (none while there is no average), no Rate button on a Movie (Auto, no Jellyfin Enhanced)', assert: `${chipAsExpected('xBefore', 'chipBefore', 'movieId')} && ${noRate('xBefore')}` },
    focusSlider,
    ...[1, 2, 3, 4, 5, 6, 7].map((i) => ({ name: 'Right ' + i, key: 'ArrowRight' })),
    { name: 'Enter saves 3.5', key: 'Enter' },
    { sleep: 900 },
    server('after the rating', 'movieId', 'chipServer'),
    expected('Long Test Movie after', 'movieId', 'chipAfter'),
    extras('Long Test Movie after', 'xAfter'),
    { name: 'the rating saved; the chip follows the summary: "Members x.x/5 (n)" last in the media-info row, named and titled "Member average x.x out of 5, n ratings", the group glyph drawn, the word shown at this width', assert: `__vars.chipServer.stars === 3.5 && __vars.chipAfter !== null && ${chipAsExpected('xAfter', 'chipAfter', 'movieId')} && __vars.xAfter.chip.word === 'Members' && __vars.xAfter.chip.wordDisplayed` },
    { name: 'jellyfin-web\'s write of the row (innerHTML, as when another version is chosen): the row as it wrote it, without the chip', script: "const row = document.querySelector('#itemDetailPage:not(.hide) .itemMiscInfo-primary'); row.innerHTML = Array.prototype.filter.call(row.children, c => !c.classList.contains('jfur-chip')).map(c => c.outerHTML).join(''); return row.children.length;" },
    { sleep: 800 },
    extras('after the row\'s write', 'xRewrite'),
    { name: 'the scanner put the chip back, once, last, as it was', assert: `${chipAsExpected('xRewrite', 'chipAfter', 'movieId')}` },
    mark('Test Series'),
    ...openItem('Test Series', 'seriesId'),
    extras('Test Series', 'xSeries'),
    { name: 'Test Series (Auto: its child list comes first): the Rate button before More, the only one, no TV jump button', assert: rateBeforeMore('xSeries') },
    { name: 'Test Series: Rate (a mouse click)', click: '#itemDetailPage:not(.hide) .mainDetailButtons .jfur-rate' },
    { sleep: 700 },
    { name: 'Rate: the slider focused, in view, the section open', assert: `${P}.onSlider() && ${sliderInView} && ${STATE}.expanded === 'true'` },
    { name: 'Rate sent no request of the section', assert: `${P}.since(window.__mark).filter(r => r.indexOf('GET ') !== 0).length === 0` },
    ...openItem('Test Series\' first season', 'seasonId'),
    extras('the season before', 'xSeasonBefore'),
    { name: 'the season (Auto): the Rate button; jellyfin-web left its media-info row empty and hidden, and there is no chip', assert: `${rateBeforeMore('xSeasonBefore')} && __vars.xSeasonBefore.chip === null && !!__vars.xSeasonBefore.row && __vars.xSeasonBefore.row.hidden && __vars.xSeasonBefore.row.items === 0` },
    { name: 'the season: a rating of the member\'s through the API (4 stars)', script: `const r = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.seasonId + '/Mine/Rating', { Stars: 4 }); return r.status;`, save: 'seasonPut' },
    { name: 'to Test Series and back to the season', script: "location.hash = '#/details?id=' + __vars.seriesId; return true;" },
    { waitFor: DRAWN('seriesId'), timeout: 30000 },
    { sleep: 800 },
    ...openItem('the season again', 'seasonId'),
    { sleep: 400 },
    expected('the season after', 'seasonId', 'seasonChip'),
    extras('the season after', 'xSeasonAfter'),
    { name: 'the season\'s row un-hidden while it holds the chip, the chip as the summary calls for it', assert: `__vars.seasonPut === 200 && __vars.seasonChip !== null && ${chipAsExpected('xSeasonAfter', 'seasonChip', 'seasonId')} && !__vars.xSeasonAfter.row.hidden && __vars.xSeasonAfter.row.displayed && __vars.xSeasonAfter.row.unhid` },
    clean('the first season', 'seasonId'),
    ...openItem('Test Album A', 'albumId'),
    extras('Test Album A', 'xAlbum'),
    { name: 'Test Album A (Auto): the Rate button before More', assert: rateBeforeMore('xAlbum') },
    ...openItem('Long Test Movie again', 'movieId'),
    extras('Long Test Movie again', 'xBack'),
    { name: 'back on the Movie: its chip, no Rate button, nothing of ours left on the other pages', assert: `${chipAsExpected('xBack', 'chipAfter', 'movieId')} && ${noRate('xBack')}` },
    // Back and forward across the items, the other version of a two-version movie, favourite.
    ...[1, 2, 3].map((i) => ({ name: `history back ${i}`, script: 'history.back(); await new Promise(r => setTimeout(r, 1800)); return location.hash.slice(0, 60);' })),
    { name: 'after three Backs: the section mounted for the item shown', waitFor: `(() => { const s = ${P}.section(); return !!s && location.hash.indexOf(s.getAttribute('data-jfur-item')) > 0; })()`, timeout: 15000 },
    { sleep: 600 },
    { name: 'after three Backs: at most one chip and one section, the chip the shown item\'s', eval: `(() => { const x = ${EXTRAS}; const s = ${P}.section(); return { chips: x.chips, rates: x.rates, sections: document.querySelectorAll('.jfur-section').length, shown: s ? s.getAttribute('data-jfur-item') : null, chip: x.chip ? x.chip.item : null }; })()`, save: 'xHistory', full: true },
    { name: 'no duplicate chip, Rate button or section after Back across three items', assert: '__vars.xHistory.chips <= 1 && __vars.xHistory.rates <= 1 && __vars.xHistory.sections === 1 && (__vars.xHistory.chip === null || __vars.xHistory.chip === __vars.xHistory.shown)' },
    ...[1, 2, 3].map((i) => ({ name: `history forward ${i}`, script: 'history.forward(); await new Promise(r => setTimeout(r, 1800)); return location.hash.slice(0, 60);' })),
    { waitFor: MOUNTED('movieId'), timeout: 15000 },
    extras('forward to the Movie again', 'xForward'),
    { name: 'forward again on the Movie: its one chip', assert: chipAsExpected('xForward', 'chipAfter', 'movieId') },
    { name: 'Versions Movie (two versions, - 1080p and - 720p)', eval: MOVIE('Versions Movie'), save: 'versionsId' },
    clean('Versions Movie', 'versionsId'),
    { name: 'Versions Movie: a rating of the member\'s through the API (4.5 stars)', script: `const r = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.versionsId + '/Mine/Rating', { Stars: 4.5 }); return r.status;`, save: 'versionsPut' },
    ...openItem('Versions Movie', 'versionsId'),
    expected('Versions Movie', 'versionsId', 'versionsChip'),
    extras('Versions Movie', 'xVersions'),
    { name: 'Versions Movie: the chip as its summary calls for it', assert: `__vars.versionsPut === 200 && __vars.versionsChip !== null && ${chipAsExpected('xVersions', 'versionsChip', 'versionsId')}` },
    { name: 'the source list: the other version chosen (jellyfin-web writes the media-info row again)', script: "const row = document.querySelector('#itemDetailPage:not(.hide) .itemMiscInfo-primary'); row.firstElementChild.setAttribute('data-jfur-mark', ''); const s = document.querySelector('#itemDetailPage:not(.hide) select.selectSource'); if (!s || s.options.length < 2) { return null; } s.selectedIndex = (s.selectedIndex + 1) % s.options.length; s.dispatchEvent(new Event('change', { bubbles: true })); return s.options.length;", save: 'versionOptions' },
    { sleep: 1500 },
    extras('after the other version', 'xVersions2'),
    { name: 'the row written again by jellyfin-web (the marked item gone)', eval: "!document.querySelector('#itemDetailPage:not(.hide) .itemMiscInfo-primary [data-jfur-mark]')", save: 'versionRewrote' },
    { name: 'after the other version: the row written again, the chip back, once, last in the row', assert: `__vars.versionOptions >= 2 && __vars.versionRewrote === true && ${chipAsExpected('xVersions2', 'versionsChip', 'versionsId')}` },
    { name: 'Versions Movie: favourite as found', eval: "(async () => (await ApiClient.getItem(ApiClient.getCurrentUserId(), __vars.versionsId)).UserData.IsFavorite)()", save: 'favouriteBefore' },
    { name: 'favourite toggled (no write of the row on 10.11.5)', click: '#itemDetailPage:not(.hide) .mainDetailButtons .btnUserRating' },
    { sleep: 1000 },
    extras('after favourite', 'xFavourite'),
    { name: 'favourite toggled back, as found', click: '#itemDetailPage:not(.hide) .mainDetailButtons .btnUserRating' },
    { sleep: 1000 },
    extras('after favourite back', 'xFavouriteOff'),
    { name: 'the chip through favourite toggled and back', assert: `${chipAsExpected('xFavourite', 'versionsChip', 'versionsId')} && ${chipAsExpected('xFavouriteOff', 'versionsChip', 'versionsId')}` },
    { name: 'Versions Movie: favourite after', eval: "(async () => (await ApiClient.getItem(ApiClient.getCurrentUserId(), __vars.versionsId)).UserData.IsFavorite)()", save: 'versionsFavourite' },
    { name: 'favourite as found', assert: '__vars.versionsFavourite === __vars.favouriteBefore' },
    clean('Versions Movie at the end', 'versionsId'),
    healthClean,
    healthNone,
    clean('Long Test Movie at the end', 'movieId'),
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE }
);
write('userratings-details-chip.json', `User Ratings ${VERSION}, the chip and the Rate button (ShowDetailButton): in the default sandbox as viewer, the desktop layout: the member-average chip on Long Test Movie as the server's summary calls for it ("Members x.x/5 (n)" last in the media-info row, its name and title "Member average x.x out of 5, n ratings", the group glyph drawn) and following a rating; jellyfin-web's write of the row takes it and the scanner puts it back once; ShowDetailButton Auto: no Rate button on the Movie, one before More on Test Series, its season and Test Album A, and Rate (a mouse click) opens the section and focuses the slider in view with no request; the season's empty, hidden row is shown while it holds the chip; Back and Forward across the items leave one chip and one section, for the item shown; on Versions Movie the other version chosen and favourite toggled and back keep the chip; the health check reports nothing. The member's rows are deleted at the end.`, ch);

const ctv = start({ user: 'viewer', layout: 'tv', movies: [['movieId', 'Long Test Movie']] });
ctv.push(...ids(false), clean('Test Series', 'seriesId'), ...openItem('Test Series', 'seriesId'), extras('TV: Test Series', 'xTvSeries'),
    { name: 'TV: the Rate button before More', assert: rateBeforeMore('xTvSeries') },
    { name: 'TV: focus the Play button', script: `${P}.log.length = 0; ${PLAY}.focus(); return ${P}.active();` });
for (let i = 1; i <= 8; i++) {
    ctv.push({ name: `TV: Right ${i} (along the buttons row to Rate)`, key: 'ArrowRight', if: "!document.activeElement.classList.contains('jfur-rate')" }, { sleep: 250, if: "!document.activeElement.classList.contains('jfur-rate')" });
}

ctv.push(
    { name: 'TV: the D-pad\'s path along the row', eval: `({ onRate: document.activeElement.classList.contains('jfur-rate'), rights: ${P}.log.filter(e => e.kind === 'key-capture' && e.key === 'ArrowRight').length, path: ${P}.log.filter(e => e.kind === 'focus').map(f => f.target) })`, save: 'tvRow', full: true },
    { name: 'TV: Right along the buttons row reaches Rate (4 presses on a Season)', assert: '__vars.tvRow.onRate === true && __vars.tvRow.rights <= 8' },
    mark('TV OK'),
    { name: 'TV: OK on Rate', key: 'Enter' },
    { sleep: 800 },
    { name: 'TV: one OK press: the slider focused and in view (TV reachability), nothing written by Rate', assert: `${P}.onSlider() && ${sliderInView} && ${P}.since(window.__mark).filter(r => r.indexOf('PUT ') === 0).length === 0` },
    { name: 'TV: Left and Right are the slider\'s again', key: 'ArrowRight' },
    { sleep: 200 },
    { name: 'TV: Right consumed by the slider, the focus kept', assert: `${lastKey('ArrowRight')}.prevented === true && ${P}.onSlider()` },
    { sleep: 900 },
    { name: 'TV: what the server holds on Test Series', eval: `${P}.mine(__vars.seriesId)`, save: 'tvSeriesServer', full: true },
    { name: 'TV: the slider\'s Right saved half a star after its pause, once', assert: `__vars.tvSeriesServer.stars === 0.5 && ${P}.since(window.__mark).filter(r => r.indexOf('PUT ') === 0).length === 1` },
    clean('Test Series at the end', 'seriesId'),
    clean('Long Test Movie at the end', 'movieId'),
    { name: 'the default layout again', eval: "(localStorage.removeItem('layout'), true)" },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE }
);
write('userratings-details-chip-tv.json', `User Ratings ${VERSION}, the Rate button's TV reachability: in the default sandbox as viewer in jellyfin-web 10.11.5's TV layout: on Test Series (ShowDetailButton Auto) the Rate button before More; the D-pad's Right from Play along the buttons row reaches it, and one OK press focuses the slider, in view, writing nothing; its Left and Right are the slider's again (Right saves half a star after the slider's pause). The member's rows are deleted at the end.`, ctv);

const ctc = start({ user: 'viewer', layout: 'mobile', movies: [['movieId', 'Long Test Movie']], emulate: PHONE });
ctc.push(...ids(false),
    { name: 'touch: a rating of the member\'s on Long Test Movie through the API (3 stars)', script: `const r = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Rating', { Stars: 3 }); return r.status;`, save: 'touchPut' },
    ...openItem('Long Test Movie', 'movieId'),
    expected('touch: Long Test Movie', 'movieId', 'touchChip'),
    extras('touch: Long Test Movie', 'xTouch'),
    { name: 'touch at 390 px: the chip as the summary calls for it, its word hidden under 600 px (the icon and the value stay), inside the screen', assert: `__vars.touchPut === 200 && __vars.touchChip !== null && ${chipAsExpected('xTouch', 'touchChip', 'movieId')} && !__vars.xTouch.chip.wordDisplayed && __vars.xTouch.chip.x >= 0 && __vars.xTouch.chip.right <= __vars.xTouch.chip.viewport + 1` },
    ...openItem('Test Series', 'seriesId'),
    extras('touch: Test Series', 'xTouchSeries'),
    { name: 'touch: the Rate button before More', assert: rateBeforeMore('xTouchSeries') },
    { name: 'touch: Rate in the middle of the screen', script: "document.querySelector('#itemDetailPage:not(.hide) .jfur-rate').scrollIntoView({ block: 'center' }); await new Promise(r => setTimeout(r, 500)); return true;" },
    touchEvent('touchStart', [`${P}.box('#itemDetailPage:not(.hide) .jfur-rate').cx`, `${P}.box('#itemDetailPage:not(.hide) .jfur-rate').cy`]),
    touchEvent('touchEnd', null),
    { sleep: 900 },
    { name: 'touch: a tap on Rate focuses the slider, in view', assert: `${P}.onSlider() && ${sliderInView}` },
    clean('Long Test Movie at the end', 'movieId'),
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE }
);
write('userratings-details-chip-touch.json', `User Ratings ${VERSION}, the chip under touch: in the default sandbox as viewer in the mobile layout at 390x844 with touch: the chip on Long Test Movie as the server's summary calls for it, its word "Members" hidden under 600 px with the group glyph and the value kept, inside the screen; on Test Series a tap on the Rate button (Input.dispatchTouchEvent) focuses the slider in view. The member's row is deleted at the end.`, ctc);

const cje = start({ user: 'admin', layout: 'desktop', movies: [['movieId', 'Long Test Movie'], ['taggedId', 'Tagged Movie']] });
cje.push(
    { name: 'Jellyfin Enhanced\'s version', eval: "(async () => { const ps = await ApiClient.getJSON(ApiClient.getUrl('Plugins')); const p = ps.find(x => x.Name === 'Jellyfin Enhanced'); return p ? { Version: p.Version, Status: p.Status } : null; })()", save: 'jeVersion', full: true },
    { name: 'Jellyfin Enhanced is there', assert: '!!__vars.jeVersion && !!window.JellyfinEnhanced' },
    ...openItem('Long Test Movie (no TMDb id: no JE Reviews)', 'movieId'),
    { sleep: 3000 },
    extras('Long Test Movie', 'xJeLong'),
    { name: 'Long Test Movie: no JE Reviews, so no Rate button (Auto, a Movie)', assert: `__vars.xJeLong.jeReviews === 0 && ${noRate('xJeLong')}` },
    clean('Tagged Movie', 'taggedId'),
    { name: 'Tagged Movie: a rating of the admin\'s through the API (4 stars), so the chip shows', script: `const r = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.taggedId + '/Mine/Rating', { Stars: 4 }); return r.status;`, save: 'jeTaggedPut' },
    ...openItem('Tagged Movie (TMDb id 900001: JE draws its Reviews)', 'taggedId'),
    { name: 'JE\'s Reviews drawn', waitFor: "!!document.querySelector('#itemDetailPage:not(.hide) details.tmdb-reviews-section')", timeout: 20000 },
    { sleep: 1500 },
    extras('Tagged Movie', 'xJeTagged'),
    { name: 'Tagged Movie: JE\'s Reviews there, so the Rate button (Auto, a Movie) before More, and no TV jump button of ours on the desktop', assert: `__vars.xJeTagged.jeReviews === 1 && ${rateAheadOfMore('xJeTagged')}` },
    expected('Tagged Movie', 'taggedId', 'jeTaggedChip'),
    { name: 'Tagged Movie: exactly one chip, the item\'s, as its summary calls for it (anywhere in the row: JE adds its audio-language item after it)', assert: `__vars.jeTaggedPut === 200 && ${chipOnceAsExpected('xJeTagged', 'jeTaggedChip', 'taggedId')}` },
    { name: 'JE TV: TV layout', eval: "(localStorage.setItem('layout', 'tv'), true)" },
    { name: 'JE TV: reload', reload: true },
    { name: 'JE TV: layout-tv, JE\'s key listener up', waitFor: "document.documentElement.classList.contains('layout-tv') && !!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur) && !!window.JellyfinEnhanced && !!window.JellyfinEnhanced.keyListener", timeout: 40000 },
    ...openItem('JE TV: Tagged Movie', 'taggedId'),
    { name: 'JE TV: JE\'s Reviews drawn', waitFor: "!!document.querySelector('#itemDetailPage:not(.hide) details.tmdb-reviews-section')", timeout: 20000 },
    { sleep: 2500 },
    { name: 'JE TV: JE\'s closed Reviews and their controls', eval: "(() => { const d = document.querySelector('#itemDetailPage:not(.hide) details.tmdb-reviews-section'); const s = d.querySelector('summary'); const c = Array.prototype.filter.call(d.querySelectorAll('button, a, input, select, textarea, [tabindex]'), el => !(s && (s === el || s.contains(el)))); return { open: d.hasAttribute('open'), controls: c.length, out: c.filter(el => el.getAttribute('tabindex') === '-1').length, saved: c.filter(el => el.hasAttribute('data-jfkit-je-tabindex')).length }; })()", save: 'jeControls', full: true },
    { name: 'JE TV: closed, every control outside its title out of the focus order (the kit\'s tvSectionJump, which User Ratings registers too)', assert: '__vars.jeControls.open === false && __vars.jeControls.controls > 0 && __vars.jeControls.out === __vars.jeControls.controls && __vars.jeControls.saved > 0' },
    extras('JE TV: Tagged Movie', 'xJeTv'),
    { name: 'JE TV: the Rate button, and no jump button of ours (the Rate button is the one way in)', assert: rateAheadOfMore('xJeTv') },
    { name: 'JE TV: exactly one chip, the item\'s', assert: chipOnceAsExpected('xJeTv', 'jeTaggedChip', 'taggedId') },
    ...reach('JE TV: Tagged Movie, Down from Play', 8, 'jeTvDown'),
    { name: 'JE TV: the D-pad passes JE\'s closed Reviews and reaches the slider, none of the Downs prevented by us', assert: '__vars.jeTvDown.onSlider === true && __vars.jeTvDown.prevented.every(p => p === false)' },
    { name: 'JE TV: focus the Play button again', script: `${P}.log.length = 0; ${PLAY}.focus(); return ${P}.active();` }
);
for (let i = 1; i <= 8; i++) {
    cje.push({ name: `JE TV: Right ${i} (to Rate)`, key: 'ArrowRight', if: "!document.activeElement.classList.contains('jfur-rate')" }, { sleep: 250, if: "!document.activeElement.classList.contains('jfur-rate')" });
}

cje.push(
    { name: 'JE TV: on Rate', assert: "document.activeElement.classList.contains('jfur-rate')" },
    { name: 'JE TV: OK on Rate', key: 'Enter' },
    { sleep: 800 },
    { name: 'JE TV: one OK press: the slider focused, in view', assert: `${P}.onSlider() && ${sliderInView}` },
    { name: 'JE TV: open JE\'s Reviews (as a member would, its title)', script: "const d = document.querySelector('#itemDetailPage:not(.hide) details.tmdb-reviews-section'); d.open = true; await new Promise(r => setTimeout(r, 500)); return d.open;" },
    { name: 'JE TV: opened, its controls\' own tabindex back', eval: "(() => { const d = document.querySelector('#itemDetailPage:not(.hide) details.tmdb-reviews-section'); return { open: d.open, saved: d.querySelectorAll('[data-jfkit-je-tabindex]').length }; })()", save: 'jeOpened', full: true },
    { name: 'JE TV: nothing of the kit\'s left on them', assert: '__vars.jeOpened.open === true && __vars.jeOpened.saved === 0' },
    { name: 'JE TV: close JE\'s Reviews again, as found', script: "const d = document.querySelector('#itemDetailPage:not(.hide) details.tmdb-reviews-section'); d.open = false; return d.open;" },
    { name: 'the default layout again', eval: "(localStorage.removeItem('layout'), true)" },
    clean('Tagged Movie at the end', 'taggedId'),
    clean('Long Test Movie at the end', 'movieId'),
    { name: 'no uncaught exception of ours', noExceptions: true, ignore: IGNORE + '|JellyfinEnhanced|/JellyfinEnhanced/' }
);
write('userratings-details-chip-je.json', `User Ratings ${VERSION}, the chip and the Rate button next to Jellyfin Enhanced 12.14: in the je sandbox as admin: ShowDetailButton Auto shows no Rate button on Long Test Movie (no TMDb id, no JE Reviews) and one before More on Tagged Movie, where JE draws its Reviews; Tagged Movie, rated through the API, shows exactly one chip in the desktop and TV layouts (anywhere in the row: JE adds its audio-language item after it); in the TV layout JE's closed Reviews' controls outside its title are out of the focus order (the kit's tvSectionJump, registered by User Ratings, no jump button of ours drawn), the D-pad's Down from Play passes them and reaches the slider, Right along the buttons row reaches Rate and one OK press focuses the slider; opened, their own tabindex is back. JE's Reviews are left closed as found; the admin's rows are deleted at the end; no setting is changed.`, cje);
