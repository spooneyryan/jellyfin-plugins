// Generates User Ratings' Settings tab webchecks over
// probes/userratings-details-probe.js (its request log and its server(), a request of the signed-in user's own):
//   userratings-settings-desktop.json  default, admin with viewer and viewer2, desktop: the settings page's Settings tab
//                                      read with GETs only; every setting in its group, in order, shown as the
//                                      server holds it, with its help and default; the inline checks (nobody chosen for
//                                      "Only the users chosen below", a number out of range, no kind) send nothing;
//                                      another tab and back keeps a change not saved, and a click made while the tab
//                                      reads again (the read held 2 s); item 2b through the tab: only viewer chosen, the dry run's dialog first, then
//                                      viewer2 gets 403 and the read-only note on the details page, still sees viewer's
//                                      review and deletes their own older row; every user again: viewer2 rates, viewer's
//                                      row unchanged; Movies taken out of what can be rated: viewer's next details view
//                                      has no section, Movies back: it has; the Top Rated page's restart notice; a save
//                                      over settings saved meanwhile is the 409 with Reload; item 15: every setting
//                                      changed through the tab at once, saved, the page reloaded, every value kept
//                                      (and in PluginConfiguration), then every one put back through the tab
//   userratings-settings-tv.json       default, admin, TV layout: no select; the D-pad moves between the tab's controls,
//                                      OK flips a switch; Save's dialog with the focus on Cancel, Up and Down stay inside
//                                      it, Back closes it with the focus back on Save and nothing saved; OK on the
//                                      dialog's Save saves
//   userratings-settings-phone.json    default, admin, 375x812 with touch: no sideways scroll; every control of the tab
//                                      and of the dialog at least 44 px high; the choices and the users inside the screen,
//                                      the names isolated; Cancel saves nothing
// Each starts from the defaults of the settings it changes (it says so and stops otherwise) and ends with every
// setting as it began (put back through the Settings API, then compared), and every rating it wrote removed.
// Run from the repository root: node scripts/webchecks/gen-settings.mjs [--out <folder>]
// Repo.Tests compares the files with what this writes. scripts/README.md says how to run them.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { VERSION } from './userratings-version.mjs';

const outIndex = process.argv.indexOf('--out');
const folder = outIndex > 0 ? path.resolve(process.argv[outIndex + 1]) : path.dirname(fileURLToPath(import.meta.url));
const IGNORE = '/CustomRatings/script|^Error: player cannot be null';
const P = '__jfurDetails';
const ROOT = '#UserRatingsSettingsRoot';
const SETTINGS = 'UserRatings/Admin/Settings';
const PLUGIN_ID = 'f53c7c34-8cba-4846-b43b-956816b2f6ae';
const MOVIE = (name) => `(await ApiClient.getItems(ApiClient.getCurrentUserId(), { IncludeItemTypes: 'Movie', Recursive: true, SearchTerm: ${JSON.stringify(name)}, Limit: 1 })).Items[0].Id`;
const DRAWN = (id) => `(() => { const p = document.querySelector('#itemDetailPage:not(.hide)'); return !!p && location.hash.indexOf(__vars.${id}) > 0 && !!p.querySelector('.mainDetailButtons .btnPlay:not(.hide)'); })()`;
const MOUNTED = (id) => `(() => { const s = ${P}.section(); return !!s && s.getAttribute('data-jfur-item') === __vars.${id} && !!s.querySelector('.jfur-summary[data-jfur-summary]'); })()`;
// The kit's dialog (its panel).
const DIALOG = `document.querySelector('.jfur-panel[role="dialog"][aria-modal="true"]')`;
const NOT_BODY = `!!document.activeElement && document.activeElement !== document.body && document.activeElement.isConnected`;
const ROW = (field) => `${ROOT} [data-jfur-field="${field}"]`;
// Every setting in the tab's order (Configuration/RatingsSettingsFields.cs).
const ORDER = ['EnableRatings', 'EnableReviews', 'WhoCanRate', 'RaterUserIds', 'RateableItemTypes', 'RequirePlayedToRate', 'MaxReviewLength', 'AllowSpoilerFlag', 'HideReviewsFromHiddenUsers', 'HideReviewsFromDisabledUsers', 'ShowReviewerAvatars', 'ShowAverageChip', 'ShowHistogram', 'MinRatingsForAverage', 'SectionExpandedByDefault', 'ShowDetailButton', 'ReviewsPageSize', 'EnableWebUi', 'ReportWebAnchorProblems', 'SnapshotsToKeep', 'DetachedRetentionDays', 'ModerationLogDays', 'DeletedUserPolicy', 'ShowJellyfinEnhancedBanner', 'ShowOnPosters', 'PosterBadgeContent', 'PosterBadgePosition', 'EnableTopRatedPage', 'RegisterPluginPages', 'PageHostMode', 'ShowFallbackNav', 'MirrorToUserData'];
const TEXT_V = 'conf webcheck review: the harbour lights <b>stay plain</b> & steady.';

function write(name, title, steps) {
    const doc = { name: title, failOnException: false, timeout: 30000, steps };
    fs.writeFileSync(path.join(folder, name), JSON.stringify(doc, null, 2) + '\n', { encoding: 'utf8' });
}

const json = (value) => JSON.stringify(value);

// In-page helpers, as one expression each (no template literals: webcheck substitutes ${name} in every step).
// A value in one form: ids without hyphens and in lower case, lists sorted.
const CANON = "const canon = (v) => Array.isArray(v) ? JSON.stringify(v.map(x => String(x).replace(/-/g, '').toLowerCase()).sort()) : JSON.stringify(v);";
// Each setting of `want` set through its control: a switch clicked when it differs, a choice clicked, a number typed (an
// input event), each listed user or kind clicked when it differs.
const SET_CONTROLS = "const root = document.querySelector('" + ROOT + "'); const rowOf = (k) => root.querySelector('[data-jfur-field=\"' + k + '\"]'); const idOf = (x) => String(x).replace(/-/g, '').toLowerCase(); for (const k of Object.keys(want)) { const v = want[k]; const r = rowOf(k); if (!r) { throw new Error('no row for ' + k); } if (typeof v === 'boolean') { const b = r.querySelector('[role=switch]'); if ((b.getAttribute('aria-checked') === 'true') !== v) { b.click(); } } else if (typeof v === 'number') { const i = r.querySelector('input'); i.value = String(v); i.dispatchEvent(new Event('input', { bubbles: true })); } else if (typeof v === 'string') { const c = r.querySelector('[role=radio][data-jfur-value=\"' + v + '\"]'); if (!c) { throw new Error(k + ' has no choice ' + v); } c.click(); } else if (Array.isArray(v)) { for (const b of r.querySelectorAll('[role=checkbox]')) { const on = v.some(x => idOf(x) === idOf(b.getAttribute('data-jfur-value'))); if ((b.getAttribute('aria-checked') === 'true') !== on) { b.click(); } } } }";

const readSettings = (label, save) => ({
    name: label,
    script: `const r = await ${P}.server('GET', '${SETTINGS}'); if (r.status !== 200) { throw new Error('settings ' + r.status); } return r.json;`,
    save,
    full: true
});

// The settings this run changes start at their defaults, so what each step expects holds.
const fromDefaults = {
    name: 'the settings this run changes are at their defaults',
    assert: "(() => { const s = __vars.before.Settings; return s.EnableRatings === true && s.EnableReviews === true && s.WhoCanRate === 'AllUsers' && s.RaterUserIds.length === 0 && s.RateableItemTypes.indexOf('Movie') >= 0 && s.RateableItemTypes.indexOf('Trailer') < 0 && s.EnableTopRatedPage === true && s.MirrorToUserData === false && s.ReviewsPageSize < 50 && s.MaxReviewLength < 20000 && s.MinRatingsForAverage < 10 && s.SnapshotsToKeep < 365 && s.DetachedRetentionDays < 36500 && s.ModerationLogDays < 3650; })()",
    message: 'start with the defaults for EnableRatings, EnableReviews, WhoCanRate (AllUsers, nobody chosen), RateableItemTypes (Movie in, Trailer out), EnableTopRatedPage and MirrorToUserData, and every number under its largest'
};

// Every setting as it was before the run: what differs sent back through the Settings API, then compared.
const restore = {
    name: 'clean: every setting as it was before the run (what differs sent back through the Settings API)',
    script: `${CANON} const now = await ${P}.server('GET', '${SETTINGS}'); const change = {}; for (const k of Object.keys(__vars.before.Settings)) { if (canon(now.json.Settings[k]) !== canon(__vars.before.Settings[k])) { change[k] = __vars.before.Settings[k]; } } if (Object.keys(change).length > 0) { const r = await ${P}.server('POST', '${SETTINGS}?version=' + now.json.Version + '&dryRun=false', change); if (r.status !== 200) { throw new Error('restore ' + r.status + ' ' + JSON.stringify(r.json)); } } const after = await ${P}.server('GET', '${SETTINGS}'); return { changed: Object.keys(change), same: Object.keys(__vars.before.Settings).every(k => canon(after.json.Settings[k]) === canon(__vars.before.Settings[k])) };`,
    save: 'restored',
    full: true
};
const restored = { name: 'clean: every setting as it began', assert: '__vars.restored.same === true' };

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

// The admin, the settings as they are, Long Test Movie with nothing on it from another run.
function adminStart(layout, emulate) {
    return [
        ...signIn('admin', layout, emulate),
        readSettings('the settings before the run', 'before'),
        fromDefaults,
        { name: 'viewer\'s and viewer2\'s ids', eval: "(() => { const u = __vars.before.Users; const v = u.find(x => x.Name === 'viewer'), w = u.find(x => x.Name === 'viewer2'); return v && w ? { viewer: v.Id, viewer2: w.Id } : null; })()", save: 'ids', full: true },
        { name: 'viewer\'s id', eval: '__vars.ids.viewer', save: 'viewerId' },
        { name: 'Long Test Movie', eval: MOVIE('Long Test Movie'), save: 'movieId' },
        sweep('before the run')
    ];
}

// Every rating on the movie, through the admin's moderation API (its dry run first).
function sweep(label) {
    return {
        name: `clean: ${label}: every rating on Long Test Movie removed through the moderation API`,
        script: `const page = await ${P}.server('GET', 'UserRatings/Admin/Reviews?itemId=' + __vars.movieId + '&limit=200'); if (page.status !== 200) { throw new Error('list ' + page.status); } const removed = []; for (const row of page.json.Items) { const p = 'UserRatings/Admin/Reviews/' + row.RatingId + '?textOnly=false'; const dry = await ${P}.server('DELETE', p); const done = await ${P}.server('DELETE', p + '&dryRun=false&ifPreview=' + dry.json.PreviewToken); removed.push(done.status); } return removed;`
    };
}

const noneLeft = {
    name: 'clean: Long Test Movie has no rating left',
    script: `const page = await ${P}.server('GET', 'UserRatings/Admin/Reviews?itemId=' + __vars.movieId); if (page.status !== 200 || page.json.TotalRecordCount !== 0) { throw new Error('left: ' + page.status + ' ' + (page.json && page.json.TotalRecordCount)); } return true;`
};

function openTab() {
    return [
        { name: 'mark', script: `window.__mark = ${P}.requests.length; return true;` },
        { name: 'open User Ratings\' settings page', goto: '/web/index.html#/configurationpage?name=UserRatings' },
        { name: 'the page, its tabs, and the Settings tab mounted with every setting', waitFor: `!!document.querySelector('#UserRatingsConfigPage:not(.hide) #UserRatingsTabs') && document.querySelectorAll('${ROOT} .jfur-set-row').length === ${ORDER.length} && !!document.querySelector('${ROOT} .jfur-set-save:not(.hide)')`, timeout: 30000 },
        { name: 'its requests so far', eval: `${P}.requests.slice(window.__mark).map(r => r.method + ' ' + r.path)`, save: 'firstRequests', full: true },
        { name: 'GETs only, the settings read', assert: "__vars.firstRequests.every(r => r.indexOf('GET ') === 0) && __vars.firstRequests.indexOf('GET /UserRatings/Admin/Settings') >= 0" }
    ];
}

const mark = (label) => ({ name: `${label}: mark`, script: `window.__mark = ${P}.requests.length; return true;` });
const posts = (save) => ({ name: 'the tab\'s POSTs since the mark', eval: `${P}.requests.slice(window.__mark).filter(r => r.method === 'POST' && r.path === '/UserRatings/Admin/Settings').map(r => ({ query: r.query, body: r.body, status: r.status }))`, save, full: true });
const savedNow = (label, save) => readSettings(`${label}: the settings as the server holds them now`, save);

// A save done: its POST with dryRun=false answered 200, the read after it answered, and the tab shows what it read (Undo
// off, Save no longer waiting) and the toast. (A toast of an earlier save may still be there, so it alone says nothing.)
const SAVED = `(() => { const r = ${P}.requests.slice(window.__saveMark); const i = r.findIndex(x => x.method === 'POST' && x.path === '/UserRatings/Admin/Settings' && x.query.indexOf('dryRun=false') >= 0 && x.status === 200); return i >= 0 && r.slice(i + 1).some(x => x.method === 'GET' && x.path === '/UserRatings/Admin/Settings' && x.status === 200) && document.querySelector('${ROOT} .jfur-set-undo').disabled && document.querySelector('${ROOT} .jfur-set-save').getAttribute('aria-disabled') === 'false' && document.querySelectorAll('${ROOT} .jfur-set-row').length === ${ORDER.length} && Array.from(document.querySelectorAll('.jfur-toast')).some(t => t.textContent === 'Settings saved.'); })()`;
const saveMark = (label) => ({ name: `${label}: the requests so far`, script: `window.__saveMark = ${P}.requests.length; return true;` });

// Save: Save clicked, the dry run's dialog, its texts kept, then the dialog's Save.
function saveThroughDialog(label, save) {
    return [
        saveMark(label),
        { name: `${label}: Save`, click: `${ROOT} .jfur-set-save` },
        { name: `${label}: the dry run's dialog, the focus on Cancel`, waitFor: `!!${DIALOG} && document.activeElement === ${DIALOG}.querySelector('.jfur-set-cancel')`, timeout: 15000 },
        { name: `${label}: what the dialog says`, eval: `({ changes: Array.from(${DIALOG}.querySelectorAll('.jfur-set-changes li')).map(li => li.textContent), means: Array.from(${DIALOG}.querySelectorAll('.jfur-set-consequences li')).map(li => li.textContent), markup: ${DIALOG}.querySelectorAll('.jfur-set-changes b, .jfur-set-consequences b').length })`, save, full: true },
        { name: `${label}: the dialog's Save`, click: '.jfur-panel[role="dialog"] .jfur-set-confirm' },
        { name: `${label}: saved: the dialog closed, the settings read again, the toast`, waitFor: `!${DIALOG} && ${SAVED}`, timeout: 15000 }
    ];
}

// A save the server finds nothing to say about: no dialog, the toast.
function saveDirect(label) {
    return [
        saveMark(label),
        { name: `${label}: Save`, click: `${ROOT} .jfur-set-save` },
        { name: `${label}: saved without a question (nothing to say): no dialog, the settings read again, the toast`, waitFor: `!${DIALOG} && ${SAVED}`, timeout: 15000 }
    ];
}

const openItem = (label, item) => [
    { name: `${label}: away from any details page first (the same hash again draws nothing new)`, script: "location.hash = '#/home'; return true;" },
    { name: `${label}: no details page shown`, waitFor: "!document.querySelector('#itemDetailPage:not(.hide)')", timeout: 15000 },
    { name: `${label}: its details page`, script: `location.hash = '#/details?id=' + __vars.${item}; return true;` },
    { name: `${label}: drawn`, waitFor: DRAWN(item), timeout: 30000 },
    { name: `${label}: the section mounted for it`, waitFor: MOUNTED(item), timeout: 15000 },
    { sleep: 600 }
];

// The settings page read again: the page reloaded (the tab then reads the settings as they are).
const reloadTab = (label) => [
    { name: `${label}: reload the page`, reload: true },
    { name: `${label}: the Settings tab mounted again`, waitFor: `!!window.ApiClient && document.querySelectorAll('${ROOT} .jfur-set-row').length === ${ORDER.length} && !!document.querySelector('${ROOT} .jfur-set-save:not(.hide)')`, timeout: 40000 }
];

// ---------------------------------------------------------------------------------------------------------- desktop

const shownAsHeld = {
    name: 'every setting shown as the server holds it: a switch\'s aria-checked, a number\'s value, the choice checked, the users and kinds ticked; each with its help and default',
    assert: `(() => { ${CANON} const s = __vars.before.Settings; const root = document.querySelector('${ROOT}'); const fields = Array.from(root.querySelectorAll('.jfur-set-row')).map(r => r.getAttribute('data-jfur-field')); if (JSON.stringify(fields) !== ${json(JSON.stringify(ORDER))}) { return false; } return fields.every(k => { const r = root.querySelector('[data-jfur-field="' + k + '"]'); const v = s[k]; const help = r.querySelector('.jfur-set-help').textContent; if (help.indexOf('Default: ') < 0) { return false; } if (typeof v === 'boolean') { return r.querySelector('[role=switch]').getAttribute('aria-checked') === String(v); } if (typeof v === 'number') { return r.querySelector('input').value === String(v); } if (typeof v === 'string') { const c = r.querySelector('[role=radio][aria-checked="true"]'); return !!c && c.getAttribute('data-jfur-value') === v; } const on = Array.from(r.querySelectorAll('[role=checkbox][aria-checked="true"]')).map(b => b.getAttribute('data-jfur-value')); return canon(on) === canon(v); }); })()`
};

const desktop = [
    { context: 'viewer' },
    ...signIn('viewer'),
    { context: 'viewer2' },
    ...signIn('viewer2'),
    { context: 'main' },
    ...adminStart(),
    { context: 'viewer' },
    { name: 'viewer: Long Test Movie', eval: MOVIE('Long Test Movie'), save: 'movieId' },
    { name: 'viewer: stars and a review through the API', script: `const a = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Rating', { Stars: 3.5 }); const b = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Review', { Text: ${json(TEXT_V)}, IsSpoiler: false }); if (a.status !== 200 || b.status !== 200) { throw new Error('rating ' + a.status + ', review ' + b.status); } return { updated: b.json.Mine.UpdatedUtc, stars: b.json.Mine.Stars };`, save: 'viewerRow', full: true },
    { context: 'viewer2' },
    { name: 'viewer2: Long Test Movie', eval: MOVIE('Long Test Movie'), save: 'movieId' },
    { name: 'viewer2: stars through the API (the older own row)', script: `const a = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Rating', { Stars: 4 }); if (a.status !== 200) { throw new Error('rating ' + a.status); } return a.status;` },
    { context: 'main' },
    ...openTab(),
    shownAsHeld,
    { name: 'the number fields as drawn', eval: `Array.from(document.querySelectorAll('${ROOT} .jfur-set-row-number')).map(r => { const i = r.querySelector('input'); return { field: r.getAttribute('data-jfur-field'), width: Math.round(i.getBoundingClientRect().width), cut: i.scrollWidth > i.clientWidth + 1, help: Math.round(r.querySelector('.jfur-set-help').getBoundingClientRect().width), row: Math.round(r.getBoundingClientRect().width) }; })`, save: 'numbers', full: true },
    { name: 'six number fields, each 80 to 200 px wide (8em, not grown to the row by the kit\'s .jfur-input) with its value whole; their help as wide as the row', assert: '__vars.numbers.length === 6 && __vars.numbers.every(n => n.width >= 80 && n.width <= 200 && !n.cut && n.help >= n.row - 2 && n.row >= 400)' },
    { name: 'the groups', eval: `Array.from(document.querySelectorAll('${ROOT} .jfur-set-group-title')).map(h => h.textContent)`, save: 'groups', full: true },
    { name: 'the settings\' groups, in order', assert: `JSON.stringify(__vars.groups) === ${json(JSON.stringify(['Ratings and reviews', 'Reviewers', 'Details page', 'Web client', 'Data and retention', 'Jellyfin Enhanced', 'Coming with version 1.1']))}` },
    { name: 'the restart settings say so; Mirror can\'t be turned on; no select', assert: `['EnableTopRatedPage', 'RegisterPluginPages', 'PageHostMode'].every(k => document.querySelector('${ROOT} [data-jfur-field="' + k + '"] .jfur-set-help').textContent.endsWith(' Takes effect after a restart.')) && document.querySelector('${ROW('MirrorToUserData')} [role=switch]').disabled === true && document.querySelectorAll('${ROOT} select').length === 0` },
    { name: 'screenshot: the Settings tab', screenshot: 'userratings-settings-desktop.png' },

    // The inline checks: nothing sent.
    mark('inline'),
    { name: 'inline: "Only the users chosen below"', click: `${ROW('WhoCanRate')} [data-jfur-value="SelectedUsers"]` },
    { name: 'inline: the users picker shown, with admin, viewer and viewer2, nobody chosen marked', waitFor: `(() => { const r = document.querySelector('${ROW('RaterUserIds')}'); const names = Array.from(r.querySelectorAll('[role=checkbox] .jfur-set-check-text')).map(n => n.textContent); return r.getClientRects().length > 0 && ['admin', 'viewer', 'viewer2'].every(n => names.indexOf(n) >= 0) && r.querySelector('.jfur-set-error').textContent.indexOf('Choose at least one user') === 0 && r.querySelector('[role=group]').getAttribute('aria-invalid') === 'true'; })()`, timeout: 5000 },
    { name: 'inline: the users\' names isolated', assert: `Array.from(document.querySelectorAll('${ROW('RaterUserIds')} [role=checkbox] .jfur-set-check-text')).every(n => n.getAttribute('dir') === 'auto' && getComputedStyle(n).unicodeBidi === 'isolate')` },
    { name: 'inline: Save', click: `${ROOT} .jfur-set-save` },
    { name: 'inline: nothing sent, the status says so, the focus on the first user', waitFor: `document.querySelector('${ROOT} .jfur-set-status').textContent.indexOf('Some settings need fixing') === 0 && document.activeElement === document.querySelector('${ROW('RaterUserIds')} [role=checkbox]')`, timeout: 5000 },
    { name: 'inline: "Every user" again', click: `${ROW('WhoCanRate')} [data-jfur-value="AllUsers"]` },
    { name: 'inline: a review length under 2000 typed', script: "const i = document.querySelector('" + ROW('MaxReviewLength') + " input'); i.value = '1999'; i.dispatchEvent(new Event('input', { bubbles: true })); return true;" },
    { name: 'inline: its message under it, aria-invalid', assert: `document.querySelector('${ROW('MaxReviewLength')} .jfur-set-error').textContent === 'Enter a whole number from 2000 to 20000.' && document.querySelector('${ROW('MaxReviewLength')} input').getAttribute('aria-invalid') === 'true'` },
    { name: 'inline: every kind taken out', script: "for (const b of document.querySelectorAll('" + ROW('RateableItemTypes') + " [role=checkbox][aria-checked=\"true\"]')) { b.click(); } return true;" },
    { name: 'inline: its message', assert: `document.querySelector('${ROW('RateableItemTypes')} .jfur-set-error').textContent === 'Choose at least one kind: with none, nothing could be rated.'` },
    { name: 'inline: screenshot', screenshot: 'userratings-settings-inline.png' },
    { name: 'inline: Save again', click: `${ROOT} .jfur-set-save` },
    { name: 'inline: the focus on the first setting to fix in the tab\'s order (what can be rated, before the review length)', waitFor: `document.activeElement === document.querySelector('${ROW('RateableItemTypes')} [role=checkbox]')`, timeout: 5000 },
    { name: 'inline: Undo changes', click: `${ROOT} .jfur-set-undo` },
    { name: 'inline: every value back, no message', assert: `!document.querySelector('${ROOT} .jfur-set-invalid') && document.querySelector('${ROW('MaxReviewLength')} input').value === String(__vars.before.Settings.MaxReviewLength) && document.querySelector('${ROOT} .jfur-set-undo').disabled` },
    posts('inlinePosts'),
    { name: 'inline: not one POST', assert: '__vars.inlinePosts.length === 0' },

    // Another tab and back (the page's refresh): a change not saved stays and nothing is read; with nothing changed the
    // tab reads again, and a click made while that read is out (held 2 s) stays when it answers.
    mark('tabs'),
    { name: 'tabs: reviews per page changed', script: "const i = document.querySelector('" + ROW('ReviewsPageSize') + " input'); i.value = String(__vars.before.Settings.ReviewsPageSize + 1); i.dispatchEvent(new Event('input', { bubbles: true })); return i.value;" },
    { name: 'tabs: the Moderation tab', click: '#UserRatingsTabModeration' },
    { name: 'tabs: its panel shown', waitFor: "!document.querySelector('#UserRatingsModeration').classList.contains('hide') && document.querySelector('#UserRatingsSettings').classList.contains('hide')", timeout: 10000 },
    { name: 'tabs: back to Settings', click: '#UserRatingsTabSettings' },
    { name: 'tabs: the Settings panel shown', waitFor: "!document.querySelector('#UserRatingsSettings').classList.contains('hide')", timeout: 10000 },
    { name: 'tabs: the time a read would take', sleep: 1500 },
    { name: 'tabs: the change kept and Undo on; the settings not read', assert: `document.querySelector('${ROW('ReviewsPageSize')} input').value === String(__vars.before.Settings.ReviewsPageSize + 1) && !document.querySelector('${ROOT} .jfur-set-undo').disabled && ${P}.requests.slice(window.__mark).filter(r => r.method === 'GET' && r.path === '/UserRatings/Admin/Settings').length === 0` },
    { name: 'tabs: Undo changes', click: `${ROOT} .jfur-set-undo` },
    { name: 'tabs: hold the Settings reads 2 s', delayRequests: { url: '/UserRatings/Admin/Settings', ms: 2000 } },
    { name: 'tabs: the Moderation tab again', click: '#UserRatingsTabModeration' },
    { name: 'tabs: its panel shown again', waitFor: "!document.querySelector('#UserRatingsModeration').classList.contains('hide')", timeout: 10000 },
    { name: 'tabs: back to Settings, nothing changed', click: '#UserRatingsTabSettings' },
    { name: 'tabs: the ratings histogram switch clicked while the tab reads', click: `${ROW('ShowHistogram')} [role=switch]` },
    { name: 'tabs: the read answered', waitFor: `${P}.requests.slice(window.__mark).filter(r => r.method === 'GET' && r.path === '/UserRatings/Admin/Settings' && r.status === 200).length === 1`, timeout: 15000 },
    { name: 'tabs: stop holding', delayRequests: null },
    { name: 'tabs: the time the tab takes after it', sleep: 500 },
    { name: 'tabs: the click kept, Undo on', assert: `document.querySelector('${ROW('ShowHistogram')} [role=switch]').getAttribute('aria-checked') === String(!__vars.before.Settings.ShowHistogram) && !document.querySelector('${ROOT} .jfur-set-undo').disabled` },
    { name: 'tabs: Undo changes again', click: `${ROOT} .jfur-set-undo` },
    { name: 'tabs: as read, Undo off', assert: `document.querySelector('${ROW('ShowHistogram')} [role=switch]').getAttribute('aria-checked') === String(__vars.before.Settings.ShowHistogram === true) && document.querySelector('${ROOT} .jfur-set-undo').disabled` },
    posts('tabsPosts'),
    { name: 'tabs: not one POST', assert: '__vars.tabsPosts.length === 0' },

    // Item 2b through the tab.
    mark('2b'),
    { name: '2b: "Only the users chosen below"', click: `${ROW('WhoCanRate')} [data-jfur-value="SelectedUsers"]` },
    { name: '2b: viewer chosen', click: `${ROW('RaterUserIds')} [data-jfur-value="\${viewerId}"]` },
    ...saveThroughDialog('2b', 'only'),
    { name: '2b: the dialog in the tab\'s words: the change, the users who can no longer rate', assert: "__vars.only.changes.indexOf('Who can rate and review: Every user → Only the users chosen below') >= 0 && __vars.only.changes.indexOf('Users who can rate: Nobody → viewer') >= 0 && __vars.only.means.some(m => m.indexOf('can no longer rate or review. Their ratings and reviews stay and still show, and they can still delete them.') > 0) && __vars.only.markup === 0" },
    posts('onlyPosts'),
    { name: '2b: a dry run with the version, then the save with the same version, both 200, only the two settings sent', assert: "__vars.onlyPosts.length === 2 && __vars.onlyPosts[0].query.indexOf('version=' + __vars.before.Version) >= 0 && __vars.onlyPosts[0].query.indexOf('dryRun') < 0 && __vars.onlyPosts[1].query.indexOf('dryRun=false') >= 0 && __vars.onlyPosts.every(p => p.status === 200 && JSON.stringify(Object.keys(JSON.parse(typeof p.body === 'string' ? p.body : JSON.stringify(p.body))).sort()) === JSON.stringify(['RaterUserIds', 'WhoCanRate']))" },
    savedNow('2b', 'only2b'),
    { name: '2b: saved: only viewer can rate', assert: "__vars.only2b.Settings.WhoCanRate === 'SelectedUsers' && JSON.stringify(__vars.only2b.Settings.RaterUserIds.map(x => String(x).replace(/-/g, '').toLowerCase())) === JSON.stringify([String(__vars.viewerId).replace(/-/g, '').toLowerCase()])" },
    { context: 'viewer2' },
    { name: '2b: viewer2\'s stars refused, the row kept', script: `const put = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Rating', { Stars: 2 }); const item = await ${P}.server('GET', 'UserRatings/Items/' + __vars.movieId); return { put: put.status, detail: put.json && put.json.detail, rate: item.json.Can.Rate, reason: item.json.Can.Reason, mine: item.json.Mine && item.json.Mine.Stars, count: item.json.Summary.Count };`, save: 'v2', full: true },
    { name: '2b: 403 "Rating is limited to selected members.", Can NotARater, the own stars as they were, both ratings counted', assert: "__vars.v2.put === 403 && __vars.v2.detail === 'Rating is limited to selected members.' && __vars.v2.rate === false && __vars.v2.reason === 'NotARater' && __vars.v2.mine === 4 && __vars.v2.count === 2" },
    ...openItem('2b: viewer2', 'movieId'),
    { name: '2b: viewer2\'s details page: the stars read-only with the note, viewer\'s review shown (as text)', waitFor: `(() => { const s = ${P}.section(); const ro = s && s.querySelector('.jfur-mine-readonly'); return !!ro && ro.getClientRects().length > 0 && ro.textContent.indexOf('Rating is limited to selected members.') >= 0 && Array.from(s.querySelectorAll('.jfur-review-text')).some(n => n.textContent === ${json(TEXT_V)}) && !s.querySelector('.jfur-review b'); })()`, timeout: 15000 },
    { name: '2b: screenshot of viewer2\'s read-only stars', screenshot: 'userratings-settings-2b-readonly.png' },
    { name: '2b: viewer2 deletes their own older row', script: `const r = await ${P}.server('DELETE', 'UserRatings/Items/' + __vars.movieId + '/Mine'); return r.status;`, save: 'v2delete' },
    { name: '2b: 204', assert: '__vars.v2delete === 204' },
    { context: 'viewer' },
    { name: '2b: viewer still can rate', script: `const item = await ${P}.server('GET', 'UserRatings/Items/' + __vars.movieId); return item.json.Can.Rate;`, save: 'vCan' },
    { name: '2b: Can.Rate', assert: '__vars.vCan === true' },
    { context: 'main' },
    { name: '2b: viewer no longer chosen (the picker still shown)', click: `${ROW('RaterUserIds')} [data-jfur-value="\${viewerId}"]` },
    { name: '2b: nobody chosen is marked at once', assert: `document.querySelector('${ROW('RaterUserIds')} .jfur-set-error').textContent.indexOf('Choose at least one user') === 0` },
    { name: '2b: "Every user" again', click: `${ROW('WhoCanRate')} [data-jfur-value="AllUsers"]` },
    { name: '2b: the mark gone with the picker', assert: `!document.querySelector('${ROW('RaterUserIds')}.jfur-set-invalid') && document.querySelector('${ROW('RaterUserIds')}').getClientRects().length === 0` },
    ...saveThroughDialog('2b back', 'all'),
    { name: '2b back: every user again, nobody chosen, the users who can rate again', assert: "__vars.all.changes.indexOf('Who can rate and review: Only the users chosen below → Every user') >= 0 && __vars.all.changes.indexOf('Users who can rate: viewer → Nobody') >= 0 && __vars.all.means.some(m => m.indexOf('can now rate and review.') > 0)" },
    { context: 'viewer2' },
    { name: '2b back: viewer2 rates again', script: `const put = await ${P}.server('PUT', 'UserRatings/Items/' + __vars.movieId + '/Mine/Rating', { Stars: 2.5 }); return put.status;`, save: 'v2again' },
    { name: '2b back: 200', assert: '__vars.v2again === 200' },
    ...openItem('2b back: viewer2', 'movieId'),
    { name: '2b back: viewer2\'s stars no longer read-only', waitFor: `(() => { const s = ${P}.section(); const ro = s && s.querySelector('.jfur-mine-readonly'); return !!s && (!ro || ro.getClientRects().length === 0); })()`, timeout: 15000 },
    { context: 'main' },
    { name: '2b back: viewer\'s row unchanged by the settings', script: `const p = await ${P}.server('GET', 'UserRatings/Admin/Reviews?itemId=' + __vars.movieId); const row = p.json.Items.find(x => x.UserName === 'viewer'); return { stars: row.Stars, text: row.Text, updated: row.UpdatedUtc };`, save: 'vAfter', full: true },
    { name: '2b back: the same stars, words and time', assert: `__vars.vAfter.stars === 3.5 && __vars.vAfter.text === ${json(TEXT_V)} && Date.parse(__vars.vAfter.updated) === Date.parse(__vars.viewerRow.updated)` },

    // RateableItemTypes takes effect on the next details view.
    { name: 'kinds: Movies taken out', click: `${ROW('RateableItemTypes')} [data-jfur-value="Movie"]` },
    ...saveThroughDialog('kinds', 'noMovies'),
    { name: 'kinds: the dialog names Movies, its ratings and reviews, and that nothing is deleted', assert: "__vars.noMovies.changes.some(c => c.indexOf('What can be rated: ') === 0 && c.split(' → ')[0].indexOf('Movies') >= 0 && c.split(' → ')[1].indexOf('Movies') < 0) && __vars.noMovies.means.some(m => m.indexOf('The ratings on Movies (') === 0 && m.indexOf(') stop showing and can') > 0 && m.indexOf('be changed. Nothing is deleted: ') > 0)" },
    { context: 'viewer' },
    { name: 'kinds: viewer\'s read of the movie', script: `const r = await ${P}.server('GET', 'UserRatings/Items/' + __vars.movieId); return r.status;`, save: 'noMovieRead' },
    { name: 'kinds: 404 (a kind that can\'t be rated)', assert: '__vars.noMovieRead === 404' },
    { name: 'kinds: viewer\'s details page', script: `location.hash = '#/details?id=' + __vars.movieId; return true;` },
    { name: 'kinds: drawn', waitFor: DRAWN('movieId'), timeout: 30000 },
    { sleep: 3000 },
    { name: 'kinds: no section on the next details view (no restart)', assert: `!${P}.section()` },
    { context: 'main' },
    { name: 'kinds: Movies back', click: `${ROW('RateableItemTypes')} [data-jfur-value="Movie"]` },
    ...saveDirect('kinds back'),
    { context: 'viewer' },
    ...openItem('kinds back: viewer', 'movieId'),
    { name: 'kinds back: the item read again, both ratings counted', script: `const r = await ${P}.server('GET', 'UserRatings/Items/' + __vars.movieId); return { status: r.status, count: r.json && r.json.Summary && r.json.Summary.Count };`, save: 'moviesBack', full: true },
    { name: 'kinds back: 200, two ratings', assert: '__vars.moviesBack.status === 200 && __vars.moviesBack.count === 2' },
    { context: 'main' },

    // A restart setting.
    { name: 'restart: the Top Rated page off', click: `${ROW('EnableTopRatedPage')} [role=switch]` },
    ...saveThroughDialog('restart', 'topRated'),
    { name: 'restart: the dialog says it takes effect after a restart', assert: "__vars.topRated.means.indexOf('Takes effect after the server restarts: Top Rated page.') >= 0" },
    { name: 'restart: the notice names it', waitFor: `(() => { const n = document.querySelector('${ROOT} .jfur-set-message .jfur-notice-text'); return !!n && n.textContent === 'Restart the server for these to take effect: Top Rated page.'; })()`, timeout: 10000 },
    { name: 'restart: the Top Rated page on again', click: `${ROW('EnableTopRatedPage')} [role=switch]` },
    ...saveThroughDialog('restart back', 'topRatedBack'),

    // Settings saved meanwhile: the 409 with Reload.
    mark('409'),
    { name: '409: reviews per page changed in the tab', script: "const i = document.querySelector('" + ROW('ReviewsPageSize') + " input'); i.value = String(__vars.before.Settings.ReviewsPageSize + 1); i.dispatchEvent(new Event('input', { bubbles: true })); return i.value;" },
    { name: '409: meanwhile the histogram switched through the API (another window)', script: `const now = await ${P}.server('GET', '${SETTINGS}'); const r = await ${P}.server('POST', '${SETTINGS}?version=' + now.json.Version + '&dryRun=false', { ShowHistogram: !now.json.Settings.ShowHistogram }); return r.status;`, save: 'meanwhile' },
    { name: '409: Save', click: `${ROOT} .jfur-set-save` },
    { name: '409: nothing saved, the notice names what changed, with Reload', waitFor: `(() => { const n = document.querySelector('${ROOT} .jfur-set-message .jfur-notice-text'); const b = document.querySelector('${ROOT} .jfur-set-message button'); return !!n && n.textContent === 'The settings were saved since you opened them (by someone else, or in another window): Ratings histogram. Reload them, then make your change again. Nothing was saved.' && !!b && b.textContent === 'Reload'; })()`, timeout: 10000 },
    posts('conflict'),
    { name: '409: the dry run\'s 409, no save', assert: '__vars.meanwhile === 200 && __vars.conflict.length === 1 && __vars.conflict[0].status === 409' },
    { name: '409: Reload', click: `${ROOT} .jfur-set-message button` },
    { name: '409: the settings as they are now: the histogram as switched, reviews per page as saved', waitFor: `document.querySelector('${ROW('ShowHistogram')} [role=switch]').getAttribute('aria-checked') === String(!__vars.before.Settings.ShowHistogram) && document.querySelector('${ROW('ReviewsPageSize')} input').value === String(__vars.before.Settings.ReviewsPageSize) && !document.querySelector('${ROOT} .jfur-set-message .jfur-notice')`, timeout: 10000 },
    { name: '409: the histogram back through the API', script: `const now = await ${P}.server('GET', '${SETTINGS}'); const r = await ${P}.server('POST', '${SETTINGS}?version=' + now.json.Version + '&dryRun=false', { ShowHistogram: __vars.before.Settings.ShowHistogram }); return r.status;` },
    ...reloadTab('409'),

    // Item 15: every setting through the tab, saved, the page reloaded, every value kept; then every one back.
    savedNow('all', 'preAll'),
    { name: 'all: every step before put back what it changed: the settings as before the run', assert: `(() => { ${CANON} return Object.keys(__vars.before.Settings).every(k => canon(__vars.preAll.Settings[k]) === canon(__vars.before.Settings[k])); })()` },
    {
        name: 'all: every setting but Mirror changed through its control (each switch flipped, each number one more, each choice the next, only viewer chosen, Trailers added)',
        script: `const s = __vars.before.Settings; const want = {}; for (const k of Object.keys(s)) { const v = s[k]; if (k === 'MirrorToUserData') { continue; } if (typeof v === 'boolean') { want[k] = !v; } else if (typeof v === 'number') { want[k] = v + 1; } else if (typeof v === 'string') { const names = Array.from(document.querySelectorAll('${ROOT} [data-jfur-field="' + k + '"] [role=radio]')).map(b => b.getAttribute('data-jfur-value')); const at = names.findIndex(n => n.toLowerCase() === v.toLowerCase()); want[k] = names[(at + 1) % names.length]; } } want.WhoCanRate = 'SelectedUsers'; want.RaterUserIds = [__vars.viewerId]; want.RateableItemTypes = s.RateableItemTypes.concat(['Trailer']); ${SET_CONTROLS} return want;`,
        save: 'want',
        full: true
    },
    { name: 'all: 31 settings changed', assert: 'Object.keys(__vars.want).length === 31' },
    { name: 'all: no message', assert: `!document.querySelector('${ROOT} .jfur-set-invalid')` },
    { name: 'all: screenshot', screenshot: 'userratings-settings-all.png' },
    ...saveThroughDialog('all', 'allDialog'),
    { name: 'all: the dialog lists 31 changes; the restart settings named', assert: "__vars.allDialog.changes.length === 31 && __vars.allDialog.means.some(m => m.indexOf('Takes effect after the server restarts: ') === 0)" },
    savedNow('all', 'allSaved'),
    { name: 'all: the server holds each as sent, Mirror as it was', assert: `(() => { ${CANON} return Object.keys(__vars.want).every(k => canon(__vars.allSaved.Settings[k]) === canon(__vars.want[k])) && __vars.allSaved.Settings.MirrorToUserData === __vars.before.Settings.MirrorToUserData && __vars.allSaved.Version !== __vars.before.Version; })()` },
    { name: 'all: PluginConfiguration holds them too', script: `${CANON} const c = await ApiClient.getPluginConfiguration('${PLUGIN_ID}'); return Object.keys(__vars.want).filter(k => canon(c[k]) !== canon(__vars.want[k]));`, save: 'configDiffers', full: true },
    { name: 'all: none differs', assert: '__vars.configDiffers.length === 0' },
    ...reloadTab('all'),
    {
        name: 'all: after the reload every control shows the value saved',
        assert: `(() => { ${CANON} const root = document.querySelector('${ROOT}'); return Object.keys(__vars.want).every(k => { const r = root.querySelector('[data-jfur-field="' + k + '"]'); const v = __vars.want[k]; if (typeof v === 'boolean') { return r.querySelector('[role=switch]').getAttribute('aria-checked') === String(v); } if (typeof v === 'number') { return r.querySelector('input').value === String(v); } if (typeof v === 'string') { const c = r.querySelector('[role=radio][aria-checked="true"]'); return !!c && c.getAttribute('data-jfur-value') === v; } return canon(Array.from(r.querySelectorAll('[role=checkbox][aria-checked="true"]')).map(b => b.getAttribute('data-jfur-value'))) === canon(v); }); })()`
    },
    {
        name: 'all: every setting put back through its control',
        script: `const want = {}; for (const k of Object.keys(__vars.want)) { want[k] = __vars.before.Settings[k]; } ${SET_CONTROLS} return Object.keys(want).length;`
    },
    ...saveThroughDialog('all back', 'backDialog'),
    savedNow('all back', 'backSaved'),
    { name: 'all back: the server holds every setting as before the run', assert: `(() => { ${CANON} return Object.keys(__vars.before.Settings).every(k => canon(__vars.backSaved.Settings[k]) === canon(__vars.before.Settings[k])); })()` },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    restore,
    restored,
    sweep('at the end'),
    noneLeft
];

write('userratings-settings-desktop.json', `User Ratings ${VERSION}, the Settings tab: in the default sandbox as admin, with viewer and viewer2 in their own windows, the desktop layout: the settings page's Settings tab read with GETs only; every setting in its group and order, shown as the server holds it, with its help and default, the restart settings marked, Mirror locked, no select; the inline checks (nobody chosen for "Only the users chosen below", a review length under 2000, no kind) send nothing and put the focus on the first setting to fix, Undo puts every value back; another tab and back keeps a change not saved (nothing read), and with nothing changed reads again, a click made while that read is held kept; item 2b through the tab: only viewer chosen, the dry run's dialog first (in the tab's words, the users who can no longer rate), one dry run and one save with the version and only the two settings; viewer2 gets 403 "Rating is limited to selected members." and the read-only note on the details page, still sees viewer's review, deletes their own older row; every user again: viewer2 rates, viewer's row unchanged; Movies out of what can be rated: viewer's read 404 and no section on the next details view, Movies back: the section again; the Top Rated page's restart notice; a save over settings saved meanwhile: the 409, nothing saved, Reload; item 15: every setting but Mirror changed through its control at once, saved after the dialog, held by the server and by PluginConfiguration, kept after the page is reloaded, then every one put back through the tab. Every setting as it began and every rating on Long Test Movie removed at the end.`, desktop);

// ---------------------------------------------------------------------------------------------------------- TV

const DOWN = { key: 'ArrowDown' };
const UP = { key: 'ArrowUp' };
const inDialog = `!!${DIALOG} && ${DIALOG}.contains(document.activeElement)`;
const tv = [
    ...adminStart('tv'),
    { name: 'the TV layout applied', assert: "document.documentElement.classList.contains('layout-tv')" },
    ...openTab(),
    { name: 'tv: no select in the tab (the TV picker is jellyfin-web\'s own)', assert: `document.querySelectorAll('${ROOT} select').length === 0` },
    { name: 'tv: the Ratings switch focused', script: "const b = document.querySelector('" + ROW('EnableRatings') + " [role=switch]'); b.focus(); return document.activeElement === b;" },
    { name: 'tv: Down ×2', ...DOWN },
    { ...DOWN },
    { name: 'tv: the focus moved to another control of the tab', assert: `document.querySelector('${ROOT}').contains(document.activeElement) && document.activeElement !== document.querySelector('${ROW('EnableRatings')} [role=switch]')` },
    { name: 'tv: the histogram\'s switch focused', script: "const b = document.querySelector('" + ROW('ShowHistogram') + " [role=switch]'); b.focus(); return b.getAttribute('aria-checked');", save: 'histogramBefore' },
    { name: 'tv: OK', key: 'Enter' },
    { name: 'tv: OK flipped it', waitFor: `document.querySelector('${ROW('ShowHistogram')} [role=switch]').getAttribute('aria-checked') !== __vars.histogramBefore`, timeout: 5000 },
    { name: 'tv: OK again', key: 'Enter' },
    { name: 'tv: back as it was', waitFor: `document.querySelector('${ROW('ShowHistogram')} [role=switch]').getAttribute('aria-checked') === __vars.histogramBefore`, timeout: 5000 },
    { name: 'tv: the Reviews switch focused', script: "const b = document.querySelector('" + ROW('EnableReviews') + " [role=switch]'); b.focus(); return document.activeElement === b;" },
    { name: 'tv: OK turns reviews off', key: 'Enter' },
    { name: 'tv: Save focused', script: "const b = document.querySelector('" + ROOT + " .jfur-set-save'); b.focus(); return document.activeElement === b;" },
    { name: 'tv: the hash before', eval: 'location.hash', save: 'hash' },
    { name: 'tv: OK on Save', key: 'Enter' },
    { name: 'tv: the dialog, the focus on Cancel', waitFor: `!!${DIALOG} && document.activeElement === ${DIALOG}.querySelector('.jfur-set-cancel')`, timeout: 15000 },
    { name: 'tv: Down ×4 (past the last control)', ...DOWN },
    { ...DOWN }, { ...DOWN }, { ...DOWN },
    { name: 'tv: the focus still in the dialog, on its Save (no wrap)', assert: `${inDialog} && document.activeElement === ${DIALOG}.querySelector('.jfur-set-confirm')` },
    { name: 'tv: Up ×5 (past the first control)', ...UP },
    { ...UP }, { ...UP }, { ...UP }, { ...UP },
    { name: 'tv: the focus still in the dialog (no way out by the D-pad)', assert: inDialog },
    { name: 'tv: Back (461, webOS)', cdp: 'Input.dispatchKeyEvent', params: { type: 'rawKeyDown', windowsVirtualKeyCode: 461, nativeVirtualKeyCode: 461, key: 'Unidentified', code: '' } },
    { cdp: 'Input.dispatchKeyEvent', params: { type: 'keyUp', windowsVirtualKeyCode: 461, nativeVirtualKeyCode: 461, key: 'Unidentified', code: '' } },
    { name: 'tv: the dialog closed, the focus back on Save, still on the settings page', waitFor: `!${DIALOG} && document.activeElement === document.querySelector('${ROOT} .jfur-set-save') && location.hash === __vars.hash`, timeout: 5000 },
    savedNow('tv cancel', 'tvCancel'),
    { name: 'tv: nothing saved', assert: '__vars.tvCancel.Settings.EnableReviews === true && __vars.tvCancel.Version === __vars.before.Version' },
    { name: 'tv: OK on Save again', key: 'Enter' },
    { name: 'tv: the dialog again', waitFor: `!!${DIALOG} && document.activeElement === ${DIALOG}.querySelector('.jfur-set-cancel')`, timeout: 15000 },
    { name: 'tv: Down to its Save', ...DOWN },
    { name: 'tv: on its Save', assert: `document.activeElement === ${DIALOG}.querySelector('.jfur-set-confirm')` },
    { name: 'tv: screenshot of the dialog', screenshot: 'userratings-settings-tv-dialog.png' },
    { name: 'tv: OK', key: 'Enter' },
    { name: 'tv: saved: the dialog closed, the toast, the focus on Save, never the page\'s body', waitFor: `!${DIALOG} && Array.from(document.querySelectorAll('.jfur-toast')).some(t => t.textContent === 'Settings saved.') && ${NOT_BODY} && document.activeElement === document.querySelector('${ROOT} .jfur-set-save')`, timeout: 15000 },
    savedNow('tv saved', 'tvSaved'),
    { name: 'tv: reviews off on the server', assert: '__vars.tvSaved.Settings.EnableReviews === false' },
    { name: 'tv: screenshot', screenshot: 'userratings-settings-tv.png' },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    restore,
    restored
];

write('userratings-settings-tv.json', `User Ratings ${VERSION}, the Settings tab in the TV layout: in the default sandbox as admin in jellyfin-web 10.11.5's TV layout: the Settings tab has no select; the D-pad moves the focus between its controls; OK flips a switch and flips it back; OK on Save opens the dry run's dialog with the focus on Cancel; Down past the last control and Up past the first keep the focus inside it (no wrap, no way out); Back (keyCode 461) closes it with the focus back on Save, the page where it was and nothing saved; OK on Save, Down and OK on the dialog's Save save it, the focus back on Save. Every setting as it began at the end.`, tv);

// ---------------------------------------------------------------------------------------------------------- phone

const tall = (selector) => `Array.from(document.querySelectorAll(${json(selector)})).filter(e => e.getClientRects().length > 0).map(e => ({ cls: e.className, h: Math.round(e.getBoundingClientRect().height) })).filter(x => x.h < 44)`;
const phone = [
    ...adminStart(null, { width: 375, height: 812, mobile: true, touch: true }),
    ...openTab(),
    { name: 'phone: "Only the users chosen below", so the users picker shows', click: `${ROW('WhoCanRate')} [data-jfur-value="SelectedUsers"]` },
    { name: 'phone: the picker shown', waitFor: `document.querySelector('${ROW('RaterUserIds')}').getClientRects().length > 0`, timeout: 5000 },
    { name: 'phone: the layout as shown', eval: `(() => { const root = document.querySelector('${ROOT}'); const out = Array.from(root.querySelectorAll('button, input, .jfur-set-help, .jfur-set-check-text')).filter(e => e.getClientRects().length > 0).filter(e => { const b = e.getBoundingClientRect(); return b.left < 0 || b.right > innerWidth + 1; }).map(e => e.className); const names = Array.from(root.querySelectorAll('[data-jfur-field="RaterUserIds"] .jfur-set-check-text')); return { width: innerWidth, scroll: document.documentElement.scrollWidth, body: document.body.scrollWidth, out, isolated: names.length >= 3 && names.every(n => getComputedStyle(n).unicodeBidi === 'isolate' && n.getAttribute('dir') === 'auto') }; })()`, save: 'phone', full: true },
    { name: 'phone: no sideways scroll; every control, help and name inside the screen; the names isolated', assert: '__vars.phone.scroll <= __vars.phone.width && __vars.phone.body <= __vars.phone.width && __vars.phone.out.length === 0 && __vars.phone.isolated' },
    { name: 'phone: the number fields as drawn', eval: `Array.from(document.querySelectorAll('${ROOT} .jfur-set-row-number input')).map(i => ({ width: Math.round(i.getBoundingClientRect().width), cut: i.scrollWidth > i.clientWidth + 1 }))`, save: 'numbers', full: true },
    { name: 'phone: six number fields, each at least 80 px wide with its value whole', assert: '__vars.numbers.length === 6 && __vars.numbers.every(n => n.width >= 80 && !n.cut)' },
    { name: 'phone: the tab\'s controls under 44 px high (none)', eval: tall(`${ROOT} button, ${ROOT} input`), save: 'short', full: true },
    { name: 'phone: every control of the tab at least 44 px', assert: '__vars.short.length === 0' },
    { name: 'phone: screenshot of the tab', screenshot: 'userratings-settings-tab-phone.png' },
    { name: 'phone: "Every user" again', click: `${ROW('WhoCanRate')} [data-jfur-value="AllUsers"]` },
    { name: 'phone: reviews off', click: `${ROW('EnableReviews')} [role=switch]` },
    { name: 'phone: Save', click: `${ROOT} .jfur-set-save` },
    { name: 'phone: the dialog', waitFor: `!!${DIALOG} && document.activeElement === ${DIALOG}.querySelector('.jfur-set-cancel')`, timeout: 15000 },
    { name: 'phone: the dialog\'s controls under 44 px high (none), the dialog inside the screen', eval: `({ short: ${tall('[role="dialog"] button')}, left: Math.round(${DIALOG}.getBoundingClientRect().left), right: Math.round(${DIALOG}.getBoundingClientRect().right), scroll: document.documentElement.scrollWidth, width: innerWidth })`, save: 'dialog', full: true },
    { name: 'phone: every control of the dialog at least 44 px, inside the screen', assert: '__vars.dialog.short.length === 0 && __vars.dialog.left >= 0 && __vars.dialog.right <= __vars.dialog.width && __vars.dialog.scroll <= __vars.dialog.width' },
    { name: 'phone: screenshot of the dialog', screenshot: 'userratings-settings-tab-phone-dialog.png' },
    { name: 'phone: Cancel', click: '.jfur-panel[role="dialog"] .jfur-set-cancel' },
    { name: 'phone: closed, the focus back on Save', waitFor: `!${DIALOG} && document.activeElement === document.querySelector('${ROOT} .jfur-set-save')`, timeout: 5000 },
    savedNow('phone', 'phoneAfter'),
    { name: 'phone: nothing saved', assert: '__vars.phoneAfter.Version === __vars.before.Version && __vars.phoneAfter.Settings.EnableReviews === true' },
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    restore,
    restored
];

write('userratings-settings-phone.json', `User Ratings ${VERSION}, the Settings tab under touch: in the default sandbox as admin on a 375x812 phone with touch: the Settings tab with the users picker shown: no sideways scroll; every control, help text and user name inside the screen, the names isolated (dir="auto", unicode-bidi: isolate); every button and field of the tab at least 44 px high; Save's dialog inside the screen with every control at least 44 px; Cancel saves nothing and puts the focus back on Save. Every setting as it began at the end.`, phone);
