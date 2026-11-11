// User Ratings' details-page section on the built ClientScript, in the kit's fake page, with its chip and Rate button
// (50-chip.js). WebTests runs it:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfur node --test plugins/UserRatings/tests/Jellyfin.Plugin.UserRatings.Tests/Web/details.test.mjs
// The pure functions come from the 40-details.js part of the built script, run once more with the plugin's own kit (its
// strings included), as stars.test.mjs takes 30-stars.js's; the section itself is the shipped script's own, mounted by its
// scanner on a fake details page, with the API's answers scripted (fake-dom's fetch) and some held to test their order.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage, plain } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');
const ITEM = 'a1b2c3d4e5f60718293a4b5c6d7e8f90';
const OTHER = 'ffeeddccbbaa99887766554433221100';
const EXPORTS = ['detailsId', 'detailsOffers', 'detailsFailure', 'detailsCounts', 'detailsSummary', 'detailsHistogram', 'detailsCopies', 'detailsCopyText', 'reviewPreview', 'mergeReviews', 'reviewBody', 'reviewBase', 'reviewChangedSince', 'draftName', 'draftDiffers', 'readDraft', 'draftExpired', 'keepText', 'readOnlyNoteKey'];
const BUSY = 'The database is busy; try again in a moment.';
const NOT_FOUND = 'This item wasn\'t found, or it can\'t be rated.';
const CANCEL_NOT_KEPT = 'This browser can\'t keep your unsaved text, so the form stays open. Save it, or press Cancel again to close the form and lose that text.';
const CONFLICT = 'Your review was changed in another window, so this text wasn\'t saved. Reload shows that review and keeps your text beside it; Overwrite saves your text in its place.';

function part(name) {
    const marker = '// ---- Web/js/' + name + '\n';
    const start = source.indexOf(marker);
    assert.ok(start >= 0, 'the ClientScript holds Web/js/' + name);
    const end = source.indexOf('\n// ---- ', start + marker.length);
    return source.substring(start + marker.length, end);
}

function capabilities(over) {
    const display = Object.assign({ ShowAverageChip: true, ShowDetailButton: 'Auto', SectionExpandedByDefault: true, ShowHistogram: true, MinRatingsForAverage: 1, ReviewsPageSize: 20 }, (over && over.Display) || {});
    return Object.assign({ Enabled: true, IsAdmin: false, CanRate: true, CanReview: true, RequirePlayedToRate: false, MaxReviewLength: 5000, AllowSpoilerFlag: true, StoreReadOnly: false }, over || {}, { Display: display });
}

function summary(stars) {
    const distribution = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    (stars || []).forEach((s) => distribution[s * 2 - 1]++);
    const count = (stars || []).length;
    return { Average: count ? stars.reduce((a, b) => a + b, 0) / count : null, Count: count, ReviewCount: 0, Distribution: distribution };
}

function itemDto(over) {
    return Object.assign({
        ItemId: ITEM,
        IsPartial: false,
        Summary: summary([]),
        Reviews: { Items: [], TotalRecordCount: 0, StartIndex: 0 },
        OtherCopies: [],
        Can: { Rate: true, Review: true, Moderate: false }
    }, over || {});
}

function review(n, over) {
    return Object.assign({ Id: 'r' + n, UserId: 'u' + n, UserName: 'member ' + n, IsFormerMember: false, Stars: 3, Text: 'Review ' + n, IsSpoiler: false, CreatedUtc: '2026-10-20T10:00:00.000Z', UpdatedUtc: '2026-10-20T10:00:00.000Z', Edited: false }, over || {});
}

function mine(over) {
    return Object.assign({ Id: 'm1', Stars: 3.5, IsSpoiler: false, CreatedUtc: '2026-10-21T08:00:00.000Z', UpdatedUtc: '2026-10-21T08:00:00.123Z', Edited: false }, over || {});
}

const itemPath = (id) => '/UserRatings/Items/' + (id || ITEM);

// The page with the built script started and a signed-in user; options.layout adds html.layout-<layout>, options.items
// are the items jellyfin-web's ApiClient.getItem answers ({ id: { Id, Type } }; ShowDetailButton Auto).
async function start(options) {
    options = options || {};
    const page = createPage();
    page.document.createElementNS = (ns, tag) => {
        const e = page.document.createElement(tag);
        e.namespaceURI = ns;
        return e;
    };
    if (options.layout) {
        page.document.documentElement.classList.add('layout-' + options.layout);
    }

    page.respond('GET', '/UserRatings/Capabilities', capabilities(options.capabilities));
    page.run(source, file);
    await page.advance(50);
    page.installApiClient({ userId: 'user-1', items: options.items });
    await page.advance(50);
    return page;
}

// A details view as jellyfin-web 10.11.5 builds it (the anchors of Web/anchors.json), shown for an item, with
// the media-info row (opts.misc: 'row', a year in it, the default; 'empty', empty and hidden as on a Season page; 'none')
// and the buttons row, Play and More (opts.buttons false: no row), and opts.jeReviews: Jellyfin Enhanced 12.14's closed
// Reviews below the buttons (its summary, then "Write a Review" outside it).
function detailsView(page, id, opts) {
    opts = opts || {};
    const misc = opts.misc === 'none' ? null : page.element('div', { class: 'itemMiscInfo itemMiscInfo-primary' + (opts.misc === 'empty' ? ' hide' : '') },
        opts.misc === 'empty' ? [] : [page.element('div', { class: 'mediaInfoItem' }, ['2021'])]);
    const play = page.element('button', { class: 'btnPlay' });
    const more = page.element('button', { class: 'btnMoreCommands' });
    const buttons = opts.buttons === false ? null : page.element('div', { class: 'mainDetailButtons' }, [play, more]);
    const reviews = opts.jeReviews ? jeReviewsSection(page) : null;
    const cast = page.element('div', { id: 'castCollapsible', class: 'verticalSection detailVerticalSection hide' });
    const content = page.element('div', { class: 'detailPageContent' }, [page.element('div', { class: 'itemDetailsGroup' }), cast, page.element('div', { id: 'similarCollapsible' })]);
    const view = page.element('div', { id: 'itemDetailPage', class: 'page libraryPage itemDetailPage' }, [
        page.element('div', { class: 'detailPagePrimaryContent' }, [misc, buttons, reviews].filter(Boolean)),
        page.element('div', { class: 'detailPageSecondaryContainer' }, [content])
    ]);
    page.document.body.append(view);
    showView(page, view, id || ITEM, opts.restored);
    return { view, content, cast, misc, buttons, play, more, reviews };
}

function jeReviewsSection(page) {
    const summary = page.element('summary', { class: 'sectionTitle' }, ['Reviews']);
    const write = page.element('button', { class: 'je-review-btn je-review-write-btn' }, ['Write a Review']);
    return page.element('details', { class: 'detailSection tmdb-reviews-section' }, [summary, page.element('div', { class: 'je-review-action-bar' }, [write])]);
}

function showView(page, view, id, restored) {
    page.context.location.hash = '#/details?id=' + id + '&serverId=server-1';
    page.dispatch(view, 'viewshow', { detail: { isRestored: !!restored } });
}

async function mounted(page, id, dto, opts) {
    page.respond('GET', itemPath(id), dto || itemDto());
    const parts = detailsView(page, id, opts);
    await page.advance(400);
    return Object.assign(parts, { section: page.document.querySelector('.jfur-section') });
}

function calls(page, method, re) {
    return page.fetchLog.filter((e) => e.method === method && re.test(e.path));
}

// Requests held until the test lets each go (method, path pattern), to test what is sent while another is in flight.
function hold(page, method, re) {
    const original = page.context.fetch;
    const held = [];
    page.context.fetch = (url, init) => {
        const m = (init && init.method) || 'GET';
        const path = new URL(String(url), 'http://127.0.0.1:8097').pathname;
        if (m === method && re.test(path)) {
            return new Promise((resolve, reject) => {
                held.push({ path, body: init && init.body ? JSON.parse(init.body) : undefined, release: () => original(url, init).then(resolve, reject) });
            });
        }

        return original(url, init);
    };
    return { held, restore: () => { page.context.fetch = original; } };
}

// The member's own row on a server that keeps User Ratings' preconditions: GET Items,
// PUT Mine/Rating and PUT Mine/Review answer from one row; a review write is the 409 when its precondition doesn't hold
// (IfUpdatedUtc the row's time, IfReviewUpdatedUtc the review's, IfNoReview none yet); other() is another window's write.
// Each write takes the next second; the review's time moves with its text or its spoiler flag,
// "(edited)" with the text only. reviews lists the review writes' bodies.
function rowServer(page, start) {
    let n = 0;
    const next = () => new Date(Date.parse('2026-10-27T09:00:00.000Z') + (++n) * 1000).toISOString();
    const server = { row: start ? Object.assign({ ReviewUpdatedUtc: null }, start) : null, reviews: [] };
    const copy = () => (server.row ? Object.assign({}, server.row) : null);
    const write = (patch) => {
        const at = next();
        const row = server.row || mine({ Stars: null, Text: null, CreatedUtc: at, ReviewUpdatedUtc: null });
        const textChanged = 'Text' in patch && (patch.Text || null) !== (row.Text || null);
        const flagChanged = 'IsSpoiler' in patch && !!patch.Text && !!patch.IsSpoiler !== !!row.IsSpoiler;
        if (textChanged) {
            row.Edited = !!row.Text && !!patch.Text;
        }

        if (textChanged || flagChanged) {
            row.ReviewUpdatedUtc = patch.Text ? at : null;
        }

        Object.assign(row, patch, { UpdatedUtc: at });
        server.row = row.Stars || row.Text ? row : null;
    };
    const holds = (body) => {
        const row = server.row;
        if (body.IfUpdatedUtc) {
            return !!row && Date.parse(row.UpdatedUtc) === Date.parse(body.IfUpdatedUtc);
        }

        if (body.IfReviewUpdatedUtc) {
            return !!(row && row.Text && row.ReviewUpdatedUtc) && Date.parse(row.ReviewUpdatedUtc) === Date.parse(body.IfReviewUpdatedUtc);
        }

        return !(body.IfNoReview && row && row.Text);
    };
    server.other = write;
    page.respond('GET', itemPath(), () => itemDto({ Mine: copy() }));
    page.respond('PUT', itemPath() + '/Mine/Rating', (e) => {
        write({ Stars: e.body.Stars });
        return itemDto({ Mine: copy() });
    });
    page.respond('PUT', itemPath() + '/Mine/Review', (e) => {
        if (e.conflict) {
            return { title: 'Conflict', status: 409, detail: 'Your review was changed in another window. Reload to see it.' };
        }

        write({ Text: e.body.Text, IsSpoiler: !!e.body.IsSpoiler });
        return itemDto({ Mine: copy() });
    }, (e) => {
        server.reviews.push(e.body);
        e.conflict = !holds(e.body);
        return e.conflict ? 409 : 200;
    });
    return server;
}

const slider = (section) => section.querySelector('.jfur-stars');
const shown = (node) => !!node && node.getClientRects().length > 0;
const text = (node) => (node ? node.textContent : null);

async function pure() {
    const page = await start();
    page.run('globalThis.__details = (function (kit) {\n' + part('20-api.js') + '\n' + part('30-stars.js') + '\n' + part('40-details.js') + '\nreturn { ' + EXPORTS.join(', ') + ' };\n})(__jfkit.jfur.instance);', '40-details.js of ' + file);
    return page.context.__details;
}

// ------------------------------------------------------------------------------------------- pure functions

test('detailsOffers: what Can lets the page offer, a reason it doesn\'t know taken as no write', { skip }, async () => {
    const d = await pure();
    assert.deepEqual(plain(d.detailsOffers({ Rate: true, Review: true })), { rate: true, review: true, own: true, reason: null, reviewsOff: false });
    assert.deepEqual(plain(d.detailsOffers({ Rate: true, Review: false })), { rate: true, review: false, own: true, reason: null, reviewsOff: true }, 'only reviews being off gives Rate without Review');
    assert.deepEqual(plain(d.detailsOffers({ Rate: false, Review: false, Reason: 'NotARater' })), { rate: false, review: false, own: true, reason: 'NotARater', reviewsOff: false }, 'WhoCanRate leaves the own clear and delete');
    assert.deepEqual(plain(d.detailsOffers({ Rate: false, Review: false, Reason: 'NeedsPlay' })), { rate: false, review: false, own: true, reason: 'NeedsPlay', reviewsOff: false });
    assert.deepEqual(plain(d.detailsOffers({ Rate: false, Review: false, Reason: 'ReadOnly' })), { rate: false, review: false, own: false, reason: 'ReadOnly', reviewsOff: false }, 'read-only refuses every write');
    assert.deepEqual(plain(d.detailsOffers({ Rate: false, Review: true, Reason: 'SomethingNew' })), { rate: false, review: false, own: false, reason: 'SomethingNew', reviewsOff: false }, 'an unknown reason offers nothing');
    assert.deepEqual(plain(d.detailsOffers(null)), { rate: false, review: false, own: false, reason: 'Unknown', reviewsOff: false });
});

test('detailsFailure: every answer\'s meaning per operation', { skip }, async () => {
    const d = await pure();
    const kind = (status, op) => d.detailsFailure({ status, message: 'm' }, op).kind;
    for (const op of ['read', 'more', 'rating', 'clear', 'review', 'delete']) {
        assert.equal(kind(404, op), 'gone', '404 ' + op);
        assert.equal(kind(503, op), 'retry', '503 ' + op);
        assert.equal(kind(0, op), 'retry', 'no answer ' + op);
        assert.equal(kind(500, op), 'retry', '500 ' + op);
    }

    assert.equal(kind(403, 'read'), 'gone', 'ratings off: nothing shown');
    assert.equal(kind(403, 'more'), 'reviewsOff', 'Load more\'s 403 is "reviews turned off"');
    for (const op of ['rating', 'clear', 'review', 'delete']) {
        assert.equal(kind(403, op), 'recheck', 'a write\'s 403 reads the item again: ' + op);
    }

    assert.equal(kind(409, 'review'), 'conflict');
    assert.equal(kind(400, 'review'), 'invalid');
    assert.equal(kind(413, 'review'), 'invalid');
    assert.deepEqual(plain(d.detailsFailure({ status: 503, message: BUSY }, 'rating')), { kind: 'retry', status: 503, message: BUSY });
    assert.deepEqual(plain(d.detailsFailure(new Error('x'), 'rating')), { kind: 'retry', status: 0, message: 'x' });
    assert.deepEqual(plain(d.detailsFailure(null, 'read')), { kind: 'retry', status: 0, message: '' });
});

test('detailsSummary: none, none here, too few, the average to one decimal with its label', { skip }, async () => {
    const d = await pure();
    const display = { MinRatingsForAverage: 1 };
    assert.equal(d.detailsSummary(null, display, [], true), null, 'a partial answer has no summary');
    assert.deepEqual(plain(d.detailsSummary(summary([]), display, [], true)), { kind: 'none', text: 'No member ratings yet. Be the first.', counts: '', count: 0, reviews: 0 });
    assert.equal(d.detailsSummary(summary([]), display, [], false).text, 'No member ratings yet.', 'no "Be the first." for a member who can\'t rate');
    assert.equal(d.detailsSummary(summary([]), display, [{ ItemId: 'x', Count: 2 }], true).text, 'No member ratings on this copy yet.');
    assert.equal(d.detailsSummary(Object.assign(summary([]), { ReviewCount: 2 }), display, [], true).counts, '2 reviews', 'text-only reviews are counted');
    const four = d.detailsSummary(Object.assign(summary([3.5, 4, 4.5, 5]), { ReviewCount: 3 }), display, [], true);
    assert.deepEqual(plain(four), { kind: 'average', value: 4.3, average: '4.3', label: 'Member average 4.3 out of 5, 4 ratings', counts: '4 ratings · 3 reviews', count: 4, reviews: 3 });
    const one = d.detailsSummary(Object.assign(summary([4]), { ReviewCount: 1 }), display, [], true);
    assert.equal(one.average, '4.0', 'always one decimal');
    assert.equal(one.label, 'Member average 4.0 out of 5, 1 rating');
    assert.equal(one.counts, '1 rating · 1 review');
    assert.equal(d.detailsSummary(Object.assign(summary([4]), { ReviewCount: 1 }), display, [], true, true).counts, '1 rating', 'reviews off: no review count');
    const few = d.detailsSummary(summary([4, 5]), { MinRatingsForAverage: 3 }, [], true);
    assert.deepEqual(plain(few), { kind: 'few', text: 'Not enough ratings yet', counts: '2 ratings', count: 2, reviews: 0 });
});

test('detailsHistogram: ten buckets from 0.5 to 5, heights to the tallest, a sentence from the highest', { skip }, async () => {
    const d = await pure();
    const h = d.detailsHistogram([0, 1, 0, 0, 0, 0, 0, 2, 0, 1]);
    assert.deepEqual(plain(h.bars.map((b) => [b.stars, b.count, b.percent])), [[0.5, 0, 0], [1, 1, 50], [1.5, 0, 0], [2, 0, 0], [2.5, 0, 0], [3, 0, 0], [3.5, 0, 0], [4, 2, 100], [4.5, 0, 0], [5, 1, 50]]);
    assert.equal(h.label, 'How members rated: 1 at 5 stars, 2 at 4 stars, 1 at 1 star');
    assert.equal(d.detailsHistogram(null).max, 0);
    assert.equal(d.detailsHistogram([0, 0, -3, 'x']).bars.every((b) => b.count === 0), true, 'nonsense counts as none');
});

test('detailsCopies and detailsCopyText: copies with a rating or the member\'s own, the library left for an isolated node', { skip }, async () => {
    const d = await pure();
    const copies = d.detailsCopies([{ ItemId: 'a', Count: 3, Average: 4 }, { ItemId: 'b', Count: 0, MyStars: null }, { ItemId: 'c', Count: 0, MyStars: 2.5 }, null, { Count: 2 }]);
    assert.deepEqual(plain(copies.map((c) => c.ItemId)), ['a', 'c']);
    assert.deepEqual(plain(d.detailsCopyText({ ItemId: 'a', Count: 3, Average: 4, LibraryName: 'Movies 2' }, { MinRatingsForAverage: 1 })), { key: 'details.copy', vars: { count: 3, average: '4.0' }, mine: '' });
    assert.deepEqual(plain(d.detailsCopyText({ ItemId: 'a', Count: 1, Average: 4, LibraryName: null, MyStars: 4 }, { MinRatingsForAverage: 1 })), { key: 'details.copyUnnamed', vars: { count: 1, average: '4.0' }, mine: 'yours 4/5' });
    assert.deepEqual(plain(d.detailsCopyText({ ItemId: 'a', Count: 1, Average: 4, LibraryName: 'X' }, { MinRatingsForAverage: 2 })), { key: 'details.copyCount', vars: { count: 1, ratings: '1 rating' }, mine: '' }, 'too few for an average');
});

test('reviewPreview: lines and code points, never half a surrogate pair; mergeReviews: each row once; reviewBody', { skip }, async () => {
    const d = await pure();
    assert.deepEqual(plain(d.reviewPreview('short', 10, 3)), { text: 'short', truncated: false });
    assert.deepEqual(plain(d.reviewPreview('a\nb\nc\nd', 100, 2)), { text: 'a\nb…', truncated: true });
    const emoji = '😀'.repeat(8);
    const cut = d.reviewPreview(emoji, 5, 9);
    assert.equal(cut.text, '😀'.repeat(5) + '…', 'five code points, not five UTF-16 units');
    assert.equal(d.reviewPreview('one two     three', 8, 9).text, 'one two…', 'the white space before the ellipsis trimmed');
    assert.deepEqual(plain(d.mergeReviews([{ Id: 'A-1' }, { Id: 'b' }], [{ Id: 'a1' }, { Id: 'c' }, null]).map((r) => r.Id)), ['A-1', 'b', 'c']);
    // The review's own precondition: no review yet, the review's time, or (none on record) the row's.
    assert.deepEqual(plain(d.reviewBody('Text', false, null)), { Text: 'Text', IsSpoiler: false, IfNoReview: true }, 'no row: a first review');
    assert.deepEqual(plain(d.reviewBody('Text', true, { UpdatedUtc: '2026-10-21T08:00:00.123Z', Text: null, IsSpoiler: false })), { Text: 'Text', IsSpoiler: true, IfNoReview: true }, 'stars alone are no review');
    assert.deepEqual(plain(d.reviewBody('Text', false, { UpdatedUtc: '2026-10-21T08:00:00.123Z', Text: 'Old', IsSpoiler: false, ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' })), { Text: 'Text', IsSpoiler: false, IfReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' }, 'an edit sends the review\'s own time, never the row\'s');
    assert.deepEqual(plain(d.reviewBody('Text', false, { UpdatedUtc: '2026-10-21T08:00:00.123Z', Text: 'Old', IsSpoiler: false, ReviewUpdatedUtc: null })), { Text: 'Text', IsSpoiler: false, IfUpdatedUtc: '2026-10-21T08:00:00.123Z' }, 'a review with no time of its own: the row\'s');
    assert.equal(d.detailsId('A1B2-C3'), 'a1b2c3');
});

// ------------------------------------------------------------------------------------------------------ mounting

test('the section mounts before the cast with the item\'s id, the slider alone in its row', { skip }, async () => {
    const page = await start();
    const { content, cast, section } = await mounted(page);
    assert.ok(section, 'the section');
    assert.equal(section.parentNode, content);
    assert.equal(section.nextElementSibling, cast, 'right before #castCollapsible, hidden as it is');
    assert.equal(section.getAttribute('data-jfur-item'), ITEM);
    assert.equal(section.className, 'verticalSection detailVerticalSection jfur-section');
    assert.equal(text(section.querySelector('h2 .jfur-section-toggle')), 'Member ratings & reviews');
    assert.equal(section.querySelector('.jfur-section-toggle').getAttribute('aria-expanded'), 'true');
    assert.equal(text(section.querySelector('.jfur-summary')), 'No member ratings yet. Be the first.');
    const row = section.querySelector('.jfur-mine-row');
    assert.equal(text(row.querySelector('.jfur-mine-label')), 'Your rating');
    assert.equal(slider(section).getAttribute('aria-labelledby'), row.querySelector('.jfur-mine-label').id);
    const focusables = row.querySelectorAll('button, a, input, select, textarea, [tabindex]');
    assert.equal(focusables.filter((n) => n.classList.contains('jfur-stars')).length, 1, 'one slider');
    assert.ok(focusables.every((n) => n === slider(section) || n.classList.contains('jfur-stars-step')), 'nothing but the slider and the − / + buttons, which show only at (pointer: coarse), never on the TV layout');
    assert.equal(shown(section.querySelector('.jfur-clear')), false, 'no Clear without stars');
    assert.equal(shown(section.querySelector('.jfur-write')), true);
    assert.equal(shown(section.querySelector('.jfur-review-list')), false, 'no list without rows');
    assert.deepEqual(plain(page.fetchLog.map((e) => e.method + ' ' + e.path)), ['GET /UserRatings/Capabilities', 'GET ' + itemPath()], 'two reads, no write');
    assert.equal(page.document.querySelectorAll('.jfur-section').length, 1);
    await page.advance(1000);
    assert.equal(calls(page, 'GET', /\/Items\//).length, 1, 'later scans read nothing again');
});

test('nothing is shown for a 404 (not visible, not rateable), a 403 (ratings off) or Enabled false, and it isn\'t read again', { skip }, async () => {
    for (const [status, label] of [[404, 'not visible'], [403, 'ratings off']]) {
        const page = await start();
        page.respond('GET', itemPath(), { title: 'x', status, detail: label === 'not visible' ? NOT_FOUND : 'Ratings are turned off on this server.' }, status);
        detailsView(page);
        await page.advance(400);
        assert.equal(page.document.querySelectorAll('.jfur-section').length, 0, label);
        page.document.body.append(page.element('div'));
        await page.advance(600);
        assert.equal(calls(page, 'GET', /\/Items\//).length, 1, label + ': read once');
        assert.equal(page.errors.length, 0);
    }

    const off = await start({ capabilities: { Enabled: false } });
    await mounted(off);
    assert.equal(off.document.querySelectorAll('.jfur-section').length, 0, 'Enabled false');
});

test('the summary, the histogram and the copies\' hint: the library isolated, Open to the copy', { skip }, async () => {
    const page = await start();
    const dto = itemDto({
        Summary: Object.assign(summary([3, 4, 4, 5]), { ReviewCount: 2 }),
        OtherCopies: [{ ItemId: OTHER, LibraryName: 'Films ‮edoc', Average: 4.5, Count: 2, MyStars: 4 }, { ItemId: 'zero', Count: 0 }]
    });
    const { section } = await mounted(page, ITEM, dto);
    const stars = section.querySelector('.jfur-summary .jfur-stars-display');
    assert.equal(stars.getAttribute('role'), 'img');
    assert.equal(stars.getAttribute('aria-label'), 'Member average 4.0 out of 5, 4 ratings');
    assert.equal(text(section.querySelector('.jfur-summary-value')), '4.0');
    assert.equal(text(section.querySelector('.jfur-summary-counts')), '4 ratings · 2 reviews');
    const histogram = section.querySelector('.jfur-histogram');
    assert.equal(histogram.getAttribute('role'), 'img');
    assert.equal(histogram.getAttribute('aria-label'), 'How members rated: 1 at 5 stars, 2 at 4 stars, 1 at 3 stars');
    assert.equal(histogram.children.length, 10);
    assert.deepEqual(plain(histogram.children.map((b) => b.style.height)), ['4%', '4%', '4%', '4%', '4%', '50%', '4%', '100%', '4%', '50%']);
    const copies = section.querySelectorAll('.jfur-copy');
    assert.equal(copies.length, 1, 'the copy without a rating isn\'t named');
    assert.equal(text(copies[0]), 'Also rated on another copy (Films ‮edoc): 4.5/5 (2) · yours 4/5 · Open the copy in Films ‮edoc');
    const library = copies[0].querySelector('.jfur-copy-library');
    assert.equal(library.getAttribute('dir'), 'auto');
    assert.ok(library.classList.contains('jfur-isolate'), 'the unclosed override stays in its element');
    const open = copies[0].querySelector('.jfur-copy-open');
    assert.equal(open.getAttribute('href'), '#/details?id=' + OTHER + '&serverId=server-1');
    assert.equal(text(open), 'Open the copy in Films ‮edoc', 'the link names what it opens');
    assert.ok(open.querySelector('.jfur-copy-library.jfur-isolate'), 'its library name isolated too');

    const plain2 = await start({ capabilities: { Display: { ShowHistogram: false, MinRatingsForAverage: 5 } } });
    const s2 = (await mounted(plain2, ITEM, dto)).section;
    assert.ok(s2.querySelector('.jfur-histogram') === null, 'ShowHistogram off, and never beside too few ratings');
    assert.equal(text(s2.querySelector('.jfur-summary')), 'Not enough ratings yet4 ratings · 2 reviews');
    assert.equal(text(s2.querySelector('.jfur-copy')), 'Also rated on another copy (Films ‮edoc): 2 ratings · yours 4/5 · Open the copy in Films ‮edoc');
});

test('the other members\' rows: names and texts isolated and as text, avatars, stars, (edited), Show more, a spoiler hidden until asked', { skip }, async () => {
    const page = await start();
    const long = Array.from({ length: 9 }, (_, i) => 'Line ' + (i + 1)).join('\n');
    const dto = itemDto({
        Summary: Object.assign(summary([3, 4, 2]), { ReviewCount: 3 }),
        Reviews: {
            Items: [
                review(1, { UserName: '<b>sam</b>', Text: '<img src=x onerror=alert(1)>', UserImageTag: 'tag1', Edited: true }),
                review(2, { UserName: 'سارة ‮abc', Text: long, Stars: 4 }),
                review(3, { Text: 'The ending: everyone was a ghost.', IsSpoiler: true, Stars: null }),
                review(4, { Text: null, Stars: 2 })
            ],
            TotalRecordCount: 4,
            StartIndex: 0
        }
    });
    const { section } = await mounted(page, ITEM, dto);
    const list = section.querySelector('.jfur-review-list');
    assert.equal(list.getAttribute('role'), 'list');
    const rows = list.querySelectorAll('.jfur-review');
    assert.deepEqual(plain(rows.map((r) => r.getAttribute('data-jfur-review'))), ['r1', 'r2', 'r3', 'r4']);
    assert.equal(rows[0].getAttribute('role'), 'listitem');
    assert.equal(text(rows[0].querySelector('.jfur-review-name')), '<b>sam</b>');
    assert.equal(rows[0].querySelectorAll('b').length + rows[0].querySelectorAll('img').filter((i) => i.getAttribute('src') === 'x').length, 0, 'never HTML');
    assert.equal(text(rows[0].querySelector('.jfur-review-text')), '<img src=x onerror=alert(1)>');
    for (const node of section.querySelectorAll('.jfur-review-name, .jfur-review-text')) {
        assert.equal(node.getAttribute('dir'), 'auto', 'every name and text takes its own direction');
        assert.ok(node.classList.contains('jfur-isolate'), 'and is isolated');
    }

    assert.equal(rows[0].querySelector('img.jfur-avatar').getAttribute('src'), 'http://127.0.0.1:8097/Users/u1/Images/Primary?tag=tag1&maxHeight=96');
    assert.ok(rows[1].querySelector('.jfur-avatar-none'), 'no avatar: the icon');
    assert.equal(rows[1].querySelector('.jfur-review-stars').getAttribute('aria-label'), 'Rated 4 out of 5');
    assert.equal(text(rows[0].querySelector('.jfur-review-edited')), '(edited)');
    assert.ok(rows[1].querySelector('.jfur-review-edited') === null);
    assert.equal(rows[0].querySelector('time').getAttribute('datetime'), '2026-10-20T10:00:00.000Z');
    // Show more.
    const body = rows[1].querySelector('.jfur-review-text');
    assert.equal(body.textContent, long.split('\n').slice(0, 6).join('\n') + '…');
    const more = rows[1].querySelector('.jfur-review-more');
    assert.equal(more.textContent, 'Show more');
    assert.equal(more.getAttribute('aria-expanded'), 'false');
    assert.equal(more.getAttribute('aria-controls'), body.id, "Show more controls the text it shows");
    assert.ok(body.id);
    more.click();
    assert.equal(body.textContent, long);
    assert.equal(more.textContent, 'Show less');
    more.click();
    assert.equal(more.getAttribute('aria-expanded'), 'false');
    // The spoiler.
    assert.equal(text(rows[2].querySelector('.jfur-spoiler-flag')), 'Contains spoilers');
    assert.equal(shown(rows[2].querySelector('.jfur-review-text')), false, 'the spoiler\'s text hidden');
    const reveal = rows[2].querySelector('.jfur-spoiler-toggle');
    reveal.click();
    assert.equal(shown(rows[2].querySelector('.jfur-review-text')), true);
    assert.equal(reveal.textContent, 'Hide spoiler');
    assert.ok(rows[2].querySelector('.jfur-review-stars') === null, 'a text-only review shows no stars');
    assert.ok(rows[3].querySelector('.jfur-review-text') === null, 'a star-only row shows no text');
    assert.equal(shown(section.querySelector('.jfur-reviews-toolbar')), true, 'the order with two rows or more');
});

// ------------------------------------------------------------------------------------------------------ the list

test('Load more: pressed by the member only, the next page in the order chosen, each row once, the button gone at the end, the focus kept in the list', { skip }, async () => {
    const page = await start({ capabilities: { Display: { ReviewsPageSize: 2 } } });
    // An observer that reports everything in view at once: the section must not press Load more by itself (the kit's
    // pager does on a full page; in the middle of a details page it would push the cast and More like this away).
    const observers = [];
    page.context.IntersectionObserver = function (callback) {
        observers.push(callback);
        this.observe = () => callback([{ isIntersecting: true }]);
        this.disconnect = () => {};
    };
    const first = [review(1), review(2)];
    const { section } = await mounted(page, ITEM, itemDto({ Summary: summary([3, 3, 3, 3, 3]), Reviews: { Items: first, TotalRecordCount: 5, StartIndex: 0 } }));
    await page.advance(500);
    assert.equal(observers.length, 0, 'no observer');
    assert.equal(calls(page, 'GET', /\/Reviews$/).length, 0, 'nothing loaded until the member asks');
    page.respond('GET', itemPath() + '/Reviews', (e) => {
        const start = Number(e.query.startIndex);
        const all = [review(1), review(2), review(3), review(4), review(5)];
        return { Items: all.slice(start, start + Number(e.query.limit)), TotalRecordCount: 5, StartIndex: start };
    });
    const pager = section.querySelector('.jfur-pager');
    assert.equal(shown(pager), true);
    const more = pager.querySelector('button');
    assert.equal(more.textContent, 'Load more');
    more.focus();
    more.click();
    await page.advance(50);
    assert.deepEqual(plain(calls(page, 'GET', /\/Reviews$/).map((e) => e.query)), [{ startIndex: '2', limit: '2', sort: 'Newest' }]);
    assert.deepEqual(plain(section.querySelectorAll('.jfur-review').map((r) => r.getAttribute('data-jfur-review'))), ['r1', 'r2', 'r3', 'r4']);
    assert.ok(page.document.activeElement === more, 'the focus stays on Load more while there is more');
    more.click();
    await page.advance(50);
    assert.deepEqual(plain(section.querySelectorAll('.jfur-review').map((r) => r.getAttribute('data-jfur-review'))), ['r1', 'r2', 'r3', 'r4', 'r5']);
    assert.equal(shown(pager), false, 'no Load more at the end');
    assert.equal(page.document.activeElement.getAttribute('data-jfur-review'), 'r5', 'the focus on the first row it brought');
});

test('Load more\'s 403 is "reviews turned off": the list and the button go, no error, no Retry; an open form keeps its text, read-only, until closed', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Text: 'My words' }), Reviews: { Items: [review(1)], TotalRecordCount: 3, StartIndex: 0 } }));
    assert.equal(shown(section.querySelector('.jfur-write')), false, 'a reviewed row: Write a review hidden, Edit instead');
    section.querySelector('.jfur-own-edit').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Typed for ten minutes';
    page.dispatch(area, 'input');
    page.respond('GET', itemPath() + '/Reviews', { title: 'Forbidden', status: 403, detail: 'Reviews are turned off on this server.' }, 403);
    section.querySelector('.jfur-pager button').click();
    await page.advance(50);
    assert.equal(shown(section.querySelector('.jfur-reviews-off')), true);
    assert.equal(text(section.querySelector('.jfur-reviews-off')), 'Reviews are turned off on this server.');
    assert.equal(section.querySelectorAll('.jfur-review').length, 0);
    assert.equal(shown(section.querySelector('.jfur-pager')), false);
    const form = section.querySelector('.jfur-review-form');
    assert.ok(form, 'the form stays (before: it went, with the text)');
    assert.equal(form.querySelector('textarea').value, 'Typed for ten minutes');
    assert.equal(form.querySelector('textarea').readOnly, true, 'read-only, its text selectable');
    assert.equal(shown(form.querySelector('.jfur-review-save')), false, 'no Save');
    assert.equal(text(form.querySelector('.jfur-review-cancel')), 'Close');
    assert.equal(text(form.querySelector('.jfur-review-notice .jfur-notice-text')), 'Your review can\'t be saved now. Your text stays here so you can copy it.');
    assert.equal(shown(section.querySelector('.jfur-own')), false, 'no own review card meanwhile');
    assert.equal(shown(section.querySelector('.jfur-write')), false);
    assert.equal(section.querySelectorAll('[role="alert"]').length, 0, 'no error');
    assert.equal(shown(slider(section)), true, 'the slider and the summary stay');
    form.querySelector('.jfur-review-cancel').click();
    assert.ok(section.querySelector('.jfur-review-form') === null, 'closed by the member');
    assert.equal(shown(section.querySelector('.jfur-own')), false, 'reviews off: still no card');
    assert.equal(calls(page, 'PUT', /./).length, 0);
});

test('Load more\'s 503: the sentence and Try again, which loads the page', { skip }, async () => {
    const page = await start({ capabilities: { Display: { ReviewsPageSize: 1 } } });
    const { section } = await mounted(page, ITEM, itemDto({ Reviews: { Items: [review(1)], TotalRecordCount: 2, StartIndex: 0 } }));
    page.respond('GET', itemPath() + '/Reviews', { title: 'x', status: 503, detail: BUSY }, 503);
    section.querySelector('.jfur-pager button').click();
    await page.advance(50);
    const notice = section.querySelector('.jfur-list-message [role="alert"]');
    assert.equal(text(notice.querySelector('.jfur-notice-text')), 'More reviews couldn\'t be loaded. ' + BUSY);
    page.respond('GET', itemPath() + '/Reviews', { Items: [review(2)], TotalRecordCount: 2, StartIndex: 1 });
    notice.querySelector('button').click();
    await page.advance(50);
    assert.equal(section.querySelectorAll('.jfur-review').length, 2);
    assert.equal(shown(section.querySelector('.jfur-list-message')), false);
});

test('the order: the first page again in it, a slower earlier answer dropped', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Reviews: { Items: [review(1), review(2)], TotalRecordCount: 2, StartIndex: 0 } }));
    const sort = section.querySelector('select.jfur-sort');
    assert.deepEqual(plain(sort.children.map((o) => [o.value, o.textContent])), [['Newest', 'Newest'], ['Oldest', 'Oldest'], ['Highest', 'Highest'], ['Lowest', 'Lowest']]);
    assert.equal(text(section.querySelector('.jfur-sort-label')), 'Sort');
    const held = hold(page, 'GET', /\/Reviews$/);
    sort.value = 'Highest';
    page.dispatch(sort, 'change');
    sort.value = 'Oldest';
    page.dispatch(sort, 'change');
    assert.equal(held.held.length, 2);
    page.respond('GET', itemPath() + '/Reviews', (e) => ({ Items: e.query.sort === 'Oldest' ? [review(2), review(1)] : [review(9)], TotalRecordCount: 2, StartIndex: 0 }));
    await held.held[1].release();
    await page.advance(10);
    await held.held[0].release();
    await page.advance(10);
    assert.deepEqual(plain(section.querySelectorAll('.jfur-review').map((r) => r.getAttribute('data-jfur-review'))), ['r2', 'r1'], 'the later order wins');
    assert.deepEqual(plain(page.fetchLog.filter((e) => /\/Reviews$/.test(e.path)).map((e) => e.query)), [{ startIndex: '0', limit: '20', sort: 'Oldest' }, { startIndex: '0', limit: '20', sort: 'Highest' }]);
});

// ---------------------------------------------------------------------------------------------- the own rating

test('a rating saved: PUT Mine/Rating, the answer\'s summary and Clear, the others\' rows as they were', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Reviews: { Items: [review(1)], TotalRecordCount: 1, StartIndex: 0 } }));
    page.respond('PUT', itemPath() + '/Mine/Rating', (e) => itemDto({ Summary: summary([3, e.body.Stars]), Mine: mine({ Stars: e.body.Stars }), Reviews: { Items: [], TotalRecordCount: 0, StartIndex: 0 } }));
    const s = slider(section);
    for (let i = 0; i < 7; i++) {
        page.key(s, 'ArrowRight');
    }

    page.key(s, 'Enter');
    await page.advance(200);
    assert.deepEqual(plain(calls(page, 'PUT', /Mine\/Rating$/).map((e) => e.body)), [{ Stars: 3.5 }]);
    assert.equal(s.getAttribute('aria-valuetext'), '3.5 out of 5 stars');
    assert.equal(section.querySelector('.jfur-summary .jfur-stars-display').getAttribute('aria-label'), 'Member average 3.3 out of 5, 2 ratings');
    assert.equal(shown(section.querySelector('.jfur-clear')), true, 'Clear with stars');
    assert.equal(section.querySelectorAll('.jfur-review').length, 1, 'a write\'s answer never empties the list');
    assert.equal(text(section.querySelector('.jfur-stars-live')), 'Saved: 3.5 stars');
});

test('a rating\'s 503: the stars put back, the sentence and Try again, which saves it', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 2 }) }));
    page.respond('PUT', itemPath() + '/Mine/Rating', { title: 'x', status: 503, detail: BUSY }, 503);
    const s = slider(section);
    page.key(s, 'ArrowRight');
    page.key(s, 'Enter');
    await page.advance(100);
    assert.equal(s.getAttribute('aria-valuenow'), '2', 'back to the saved value');
    const notice = section.querySelector('.jfur-mine-message [role="alert"]');
    assert.equal(text(notice.querySelector('.jfur-notice-text')), 'Your rating wasn\'t saved. ' + BUSY);
    assert.equal(text(notice.querySelector('button')), 'Try again');
    page.respond('PUT', itemPath() + '/Mine/Rating', (e) => itemDto({ Summary: summary([e.body.Stars]), Mine: mine({ Stars: e.body.Stars }) }));
    notice.querySelector('button').click();
    await page.advance(200);
    assert.deepEqual(plain(calls(page, 'PUT', /Mine\/Rating$/).map((e) => e.body.Stars)), [2.5, 2.5]);
    assert.equal(s.getAttribute('aria-valuenow'), '2.5', 'the server\'s value on the slider');
    assert.equal(shown(section.querySelector('.jfur-mine-message')), false);
    assert.equal(text(section.querySelector('.jfur-stars-live')), 'Saved: 2.5 stars');
});

test('a rating answered 404 after the write: the section goes, no "couldn\'t save", not read again until the view is restored', { skip }, async () => {
    const page = await start();
    const { view, section } = await mounted(page);
    page.respond('PUT', itemPath() + '/Mine/Rating', { title: 'Not Found', status: 404, detail: NOT_FOUND }, 404);
    page.key(slider(section), 'End');
    page.key(slider(section), 'Enter');
    await page.advance(100);
    assert.equal(section.isConnected, false, 'the section gone');
    assert.equal(page.document.querySelectorAll('[role="alert"]').length, 0, 'no error');
    assert.equal(page.document.querySelectorAll('.jfur-toast').length, 0, 'no toast');
    await page.advance(1000);
    assert.equal(calls(page, 'GET', /\/Items\/[^/]+$/).length, 1, 'nothing read again on its own');
    page.respond('GET', itemPath(), itemDto({ Mine: mine({ Stars: 5 }) }));
    showView(page, view, ITEM, true);
    await page.advance(400);
    assert.equal(calls(page, 'GET', /\/Items\/[^/]+$/).length, 2, 'a restored view reads it again');
    assert.equal(slider(page.document.querySelector('.jfur-section')).getAttribute('aria-valuenow'), '5');
});

test('a rating answered 403: the item read again; a 403 there removes the section, a 200 shows Can\'s reason and the server\'s sentence', { skip }, async () => {
    const off = await start();
    const a = (await mounted(off)).section;
    off.respond('PUT', itemPath() + '/Mine/Rating', { title: 'Forbidden', status: 403, detail: 'Ratings are turned off on this server.', type: 'urn:jellyfin-plugin-kit:feature-disabled' }, 403);
    off.respond('GET', itemPath(), { title: 'Forbidden', status: 403, detail: 'Ratings are turned off on this server.' }, 403);
    off.key(slider(a), 'End');
    off.key(slider(a), 'Enter');
    await off.advance(200);
    assert.equal(a.isConnected, false, 'ratings off: the section goes, saved or not');
    assert.equal(off.document.querySelectorAll('[role="alert"]').length, 0);

    const page = await start();
    const { section } = await mounted(page);
    page.respond('PUT', itemPath() + '/Mine/Rating', { title: 'Forbidden', status: 403, detail: 'Rating is limited to selected members.' }, 403);
    page.respond('GET', itemPath(), itemDto({ Can: { Rate: false, Review: false, Moderate: false, Reason: 'NotARater' } }));
    page.key(slider(section), 'End');
    page.key(slider(section), 'Enter');
    await page.advance(200);
    assert.equal(section.isConnected, true);
    assert.equal(calls(page, 'GET', /\/Items\/[^/]+$/).length, 2, 'read again');
    assert.equal(slider(section).getAttribute('aria-disabled'), 'true');
    assert.equal(slider(section).getAttribute('data-jfur-reason'), 'NotARater');
    assert.equal(slider(section).getAttribute('aria-valuenow'), '0');
    assert.equal(text(section.querySelector('.jfur-mine-message .jfur-notice-text')), 'Rating is limited to selected members.');
    assert.ok(section.querySelector('.jfur-mine-message button') === null, 'no Retry for a refusal');
    assert.equal(shown(section.querySelector('.jfur-write')), false, 'no review offered');
});

test('a write answered with IsPartial: Mine as written, the summary, the copies and the list as they were', { skip }, async () => {
    const page = await start();
    const dto = itemDto({ Summary: summary([4]), OtherCopies: [{ ItemId: OTHER, LibraryName: 'Movies 2', Average: 3, Count: 1 }], Reviews: { Items: [review(1)], TotalRecordCount: 1, StartIndex: 0 } });
    const { section } = await mounted(page, ITEM, dto);
    page.respond('PUT', itemPath() + '/Mine/Rating', { ItemId: ITEM, IsPartial: true, Mine: mine({ Stars: 5 }), OtherCopies: [], Can: { Rate: true, Review: true, Moderate: false } });
    page.key(slider(section), 'End');
    page.key(slider(section), 'Enter');
    await page.advance(200);
    assert.equal(slider(section).getAttribute('aria-valuenow'), '5');
    assert.equal(shown(section.querySelector('.jfur-clear')), true, 'Mine as written');
    assert.equal(section.querySelector('.jfur-summary .jfur-stars-display').getAttribute('aria-label'), 'Member average 4.0 out of 5, 1 rating', 'the summary as it was');
    assert.equal(section.querySelectorAll('.jfur-copy').length, 1, 'the copies as they were');
    assert.equal(section.querySelectorAll('.jfur-review').length, 1, 'the list as it was');
    assert.equal(page.document.querySelectorAll('[role="alert"]').length, 0);
});

test('the TV layout: the title no tab stop and the section always open, the slider alone in its row', { skip }, async () => {
    const page = await start({ layout: 'tv', capabilities: { Display: { SectionExpandedByDefault: false } } });
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Text: 'Mine' }) }));
    assert.ok(section.classList.contains('jfur-section-tv'));
    assert.equal(section.querySelector('.jfur-section-toggle').getAttribute('tabindex'), '-1', 'jellyfin-web\'s focus manager skips it');
    assert.equal(shown(section.querySelector('.jfur-section-body')), true, 'open on TV whatever the default');
    assert.ok(section.querySelector('.jfur-rating').classList.contains('jfur-rating-tv'));
    const row = section.querySelector('.jfur-mine-row');
    assert.deepEqual(plain(row.querySelectorAll('button, a, input, select, textarea, [tabindex]').map((n) => n.className)), ['jfur-stars-step jfur-stars-minus', 'jfur-stars focusable', 'jfur-stars-step jfur-stars-plus'], 'the slider, and − / + that the TV layout\'s CSS hides');
    assert.equal(section.querySelector('.jfur-mine-actions').contains(section.querySelector('.jfur-clear')), true, 'Clear on the next row');
    page.key(slider(section), 'ArrowRight');
    assert.equal(slider(section).getAttribute('aria-valuenow'), '4', 'Right is the slider\'s');

    const desk = await start({ capabilities: { Display: { SectionExpandedByDefault: false } } });
    const d = (await mounted(desk)).section;
    const toggle = d.querySelector('.jfur-section-toggle');
    assert.equal(toggle.getAttribute('tabindex'), null);
    assert.equal(toggle.getAttribute('aria-expanded'), 'false', 'SectionExpandedByDefault off');
    assert.equal(shown(d.querySelector('.jfur-section-body')), false);
    toggle.click();
    assert.equal(toggle.getAttribute('aria-expanded'), 'true');
    assert.equal(shown(d.querySelector('.jfur-section-body')), true);
    assert.equal(toggle.getAttribute('aria-controls'), d.querySelector('.jfur-section-body').id);
});

test('Can.Rate false: the slider disabled with the reason, no write the server would refuse; own clear only where allowed', { skip }, async () => {
    for (const [reason, clear] of [['NotARater', true], ['NeedsPlay', true], ['ReadOnly', false], ['FromTheFuture', false]]) {
        const page = await start();
        const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 3, Text: 'Old words' }), Can: { Rate: false, Review: false, Moderate: false, Reason: reason } }));
        const s = slider(section);
        assert.equal(s.getAttribute('aria-disabled'), 'true', reason);
        assert.equal(s.getAttribute('data-jfur-reason'), reason);
        page.key(s, 'ArrowRight');
        page.key(s, 'Enter');
        await page.advance(800);
        assert.equal(calls(page, 'PUT', /./).length, 0, reason + ': no write');
        assert.equal(shown(section.querySelector('.jfur-write')), false);
        assert.ok(section.querySelector('.jfur-own-edit') === null, reason + ': no Edit');
        assert.equal(shown(section.querySelector('.jfur-clear')), clear, reason + ': Clear');
        assert.equal(!!section.querySelector('.jfur-own-delete'), clear, reason + ': Delete');
        assert.equal(text(section.querySelector('.jfur-summary')), 'No member ratings yet.', 'no "Be the first."');
    }
});

// ------------------------------------------------------------------------------------------- Clear and Delete

test('Clear: DELETE Mine/Rating, the slider at "Not rated", "Rating cleared"; a waiting change dropped; nothing sent without saved stars', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4 }) }));
    page.respond('DELETE', itemPath() + '/Mine/Rating', itemDto({ Mine: null }));
    const s = slider(section);
    page.key(s, 'ArrowRight');
    section.querySelector('.jfur-clear').click();
    await page.advance(1000);
    assert.equal(calls(page, 'PUT', /./).length, 0, 'the waiting 4.5 dropped');
    assert.equal(calls(page, 'DELETE', /Mine\/Rating$/).length, 1);
    assert.equal(s.getAttribute('aria-valuenow'), '0');
    assert.equal(text(section.querySelector('.jfur-stars-live')), 'Rating cleared');
    assert.equal(shown(section.querySelector('.jfur-clear')), false);
    assert.ok(page.document.activeElement === s, 'the focus to the slider as Clear goes');
    page.key(s, 'End');
    section.querySelector('.jfur-clear').click();
    await page.advance(1000);
    assert.equal(calls(page, 'DELETE', /./).length, 1, 'a rating not saved yet: cleared here, nothing sent');
    assert.equal(calls(page, 'PUT', /./).length, 0);
});

test('Clear on a reviewed row asks "Keep the review without stars?", Cancel first; Delete asks and reads the item again', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4, Text: 'Words' }) }));
    page.respond('DELETE', itemPath() + '/Mine/Rating', itemDto({ Mine: mine({ Stars: null, Text: 'Words' }) }));
    section.querySelector('.jfur-clear').click();
    await page.advance(10);
    let dialog = page.document.querySelector('.jfur-panel');
    assert.equal(text(dialog.querySelector('h3')), 'Keep the review without stars?');
    assert.equal(text(dialog.querySelector('p')), 'Your stars are cleared and your review stays.');
    assert.deepEqual(plain(dialog.querySelectorAll('.jfur-actions button').map((b) => b.textContent)), ['Cancel', 'Clear the stars']);
    assert.ok(page.document.activeElement === dialog.querySelector('.jfur-dialog-cancel'), 'Cancel focused');
    dialog.querySelector('.jfur-dialog-cancel').click();
    await page.advance(10);
    assert.equal(calls(page, 'DELETE', /./).length, 0);
    section.querySelector('.jfur-clear').click();
    await page.advance(10);
    page.document.querySelector('.jfur-panel .jfur-dialog-confirm').click();
    await page.advance(100);
    assert.equal(calls(page, 'DELETE', /Mine\/Rating$/).length, 1);
    assert.equal(text(section.querySelector('.jfur-own .jfur-review-text')), 'Words', 'the review stays');

    page.respond('DELETE', itemPath() + '/Mine', null, 204);
    page.respond('GET', itemPath(), itemDto({ Summary: summary([]) }));
    section.querySelector('.jfur-own-delete').click();
    await page.advance(10);
    dialog = page.document.querySelector('.jfur-panel');
    assert.equal(text(dialog.querySelector('h3')), 'Delete your rating and review?');
    dialog.querySelector('.jfur-dialog-confirm').click();
    await page.advance(100);
    assert.equal(calls(page, 'DELETE', /Mine$/).length, 1);
    assert.equal(calls(page, 'GET', /\/Items\/[^/]+$/).length, 2, 'the item read again after the 204');
    assert.ok(section.querySelector('.jfur-own-review') === null);
    assert.equal(shown(section.querySelector('.jfur-write')), true);
    assert.equal(text(section.querySelector('.jfur-section-live')), 'Your rating and review were deleted');
});

test('Delete\'s 503: the sentence and Try again; Delete\'s 404: the section goes', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Text: 'Words' }) }));
    page.respond('DELETE', itemPath() + '/Mine', { title: 'x', status: 503, detail: BUSY }, 503);
    section.querySelector('.jfur-own-delete').click();
    await page.advance(10);
    page.document.querySelector('.jfur-panel .jfur-dialog-confirm').click();
    await page.advance(100);
    assert.equal(text(section.querySelector('.jfur-mine-message .jfur-notice-text')), 'Your rating and review weren\'t deleted. ' + BUSY);
    assert.ok(section.querySelector('.jfur-own-review'), 'nothing changed');
    page.respond('DELETE', itemPath() + '/Mine', { title: 'x', status: 404, detail: NOT_FOUND }, 404);
    section.querySelector('.jfur-mine-message button').click();
    await page.advance(10);
    page.document.querySelector('.jfur-panel .jfur-dialog-confirm').click();
    await page.advance(100);
    assert.equal(section.isConnected, false);
    assert.equal(page.document.querySelectorAll('[role="alert"]').length, 0);
});

// ------------------------------------------------------------------------------------------------- the form

test('Write a review: the text area\'s limit, the counter, Save only with words, PUT Mine/Review, the own card', { skip }, async () => {
    const page = await start({ capabilities: { MaxReviewLength: 2000 } });
    const { section } = await mounted(page);
    page.respond('PUT', itemPath() + '/Mine/Review', (e) => itemDto({ Mine: mine({ Stars: null, Text: e.body.Text, Edited: false }), Summary: Object.assign(summary([]), { ReviewCount: 1 }) }));
    section.querySelector('.jfur-write').click();
    const form = section.querySelector('.jfur-review-form');
    const area = form.querySelector('textarea');
    assert.equal(area.getAttribute('maxlength'), '2000');
    assert.equal(area.getAttribute('dir'), 'auto');
    assert.ok(page.document.activeElement === area);
    assert.equal(text(form.querySelector('label')), 'Your review');
    assert.equal(text(form.querySelector('.jfur-review-counter')), '0 / 2000');
    assert.equal(area.getAttribute('aria-describedby'), form.querySelector('.jfur-review-counter').id, "the counter describes the text area");
    assert.ok(form.querySelector('.jfur-review-counter').id);
    const save = form.querySelector('.jfur-review-save');
    assert.equal(save.disabled, true);
    area.value = '   ';
    page.dispatch(area, 'input');
    assert.equal(save.disabled, true, 'white space only');
    area.value = 'A fine film.\nWatch it.';
    page.dispatch(area, 'input');
    assert.equal(text(form.querySelector('.jfur-review-counter')), '22 / 2000');
    assert.equal(save.disabled, false);
    save.click();
    await page.advance(100);
    assert.deepEqual(plain(calls(page, 'PUT', /Mine\/Review$/).map((e) => e.body)), [{ Text: 'A fine film.\nWatch it.', IsSpoiler: false, IfNoReview: true }], 'no row yet: a first review');
    assert.ok(section.querySelector('.jfur-review-form') === null);
    assert.equal(text(section.querySelector('.jfur-own .jfur-review-text')), 'A fine film.\nWatch it.');
    assert.equal(text(section.querySelector('.jfur-own-title')), 'Your review');
    assert.ok(page.document.activeElement === section.querySelector('.jfur-own-edit'), 'the focus on Edit');
    assert.equal(text(section.querySelector('.jfur-section-live')), 'Review saved');
    assert.equal(text(section.querySelector('.jfur-summary-counts')), '1 review');
});

test('Edit: the text as saved, IfUpdatedUtc and the spoiler flag of the row, (edited) from the answer; Cancel puts the card back', { skip }, async () => {
    const page = await start();
    const row = mine({ Stars: 4, Text: 'First words', IsSpoiler: true });
    const { section } = await mounted(page, ITEM, itemDto({ Mine: row }));
    section.querySelector('.jfur-own-edit').click();
    let area = section.querySelector('.jfur-review-form textarea');
    assert.equal(area.value, 'First words');
    section.querySelector('.jfur-review-cancel').click();
    assert.ok(section.querySelector('.jfur-review-form') === null);
    assert.ok(page.document.activeElement === section.querySelector('.jfur-own-edit'), 'Cancel: back to Edit');
    page.respond('PUT', itemPath() + '/Mine/Review', (e) => itemDto({ Mine: mine({ Stars: 4, Text: e.body.Text, IsSpoiler: e.body.IsSpoiler, Edited: true, ReviewUpdatedUtc: '2026-10-27T09:00:00.000Z' }) }));
    section.querySelector('.jfur-own-edit').click();
    area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Second words';
    page.dispatch(area, 'input');
    section.querySelector('.jfur-review-save').click();
    await page.advance(100);
    assert.deepEqual(plain(calls(page, 'PUT', /Mine\/Review$/).map((e) => e.body)), [{ Text: 'Second words', IsSpoiler: true, IfUpdatedUtc: '2026-10-21T08:00:00.123Z' }]);
    assert.equal(text(section.querySelector('.jfur-own .jfur-review-edited')), '(edited)');
    assert.equal(section.querySelector('.jfur-own time').getAttribute('datetime'), '2026-10-27T09:00:00.000Z', 'the time of the review itself');
});

test('the review\'s answers: 503 keeps the text with Try again, 400 and 409 keep it with the sentence, 403 reads the item again', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4 }) }));
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Kept words';
    page.dispatch(area, 'input');
    page.respond('PUT', itemPath() + '/Mine/Review', { title: 'x', status: 503, detail: BUSY }, 503);
    section.querySelector('.jfur-review-save').click();
    await page.advance(100);
    const error = () => section.querySelector('.jfur-review-error');
    assert.equal(text(error().querySelector('.jfur-notice-text')), 'Your review wasn\'t saved. ' + BUSY);
    assert.equal(section.querySelector('.jfur-review-form textarea').value, 'Kept words', 'nothing lost');
    assert.ok(page.document.activeElement === area);
    page.respond('PUT', itemPath() + '/Mine/Review', { title: 'x', status: 409, detail: 'Your rating or review was changed in another window. Reload to see it.' }, 409);
    error().querySelector('button').click();
    await page.advance(100);
    assert.equal(calls(page, 'PUT', /Mine\/Review$/).length, 2, 'Try again sent it again');
    assert.equal(text(error().querySelector('.jfur-notice-text')), CONFLICT);
    assert.deepEqual(plain(error().querySelectorAll('button').map((b) => b.textContent)), ['Reload', 'Overwrite'], 'no Retry for a 409: Reload and Overwrite');
    assert.ok(page.document.activeElement === error().querySelector('.jfur-conflict-reload'), 'the focus on Reload');
    assert.equal(area.value, 'Kept words');
    page.respond('PUT', itemPath() + '/Mine/Review', { title: 'x', status: 400, detail: 'A review needs some text.' }, 400);
    section.querySelector('.jfur-review-save').click();
    await page.advance(100);
    assert.equal(text(error().querySelector('.jfur-notice-text')), 'Your review wasn\'t saved. A review needs some text.');
    page.respond('PUT', itemPath() + '/Mine/Review', { title: 'Forbidden', status: 403, detail: 'Reviews are turned off on this server.' }, 403);
    page.respond('GET', itemPath(), itemDto({ Mine: mine({ Stars: 4 }), Can: { Rate: true, Review: false, Moderate: false } }));
    section.querySelector('.jfur-review-save').click();
    await page.advance(200);
    const form = section.querySelector('.jfur-review-form');
    assert.ok(form, 'reviews off: the form stays, read-only (before, it went with the text)');
    assert.equal(form.querySelector('textarea').value, 'Kept words');
    assert.equal(form.querySelector('textarea').readOnly, true);
    assert.equal(text(form.querySelector('.jfur-review-error .jfur-notice-text')), 'Reviews are turned off on this server.', 'the server\'s sentence');
    assert.equal(shown(form.querySelector('.jfur-review-save')), false);
    assert.equal(shown(section.querySelector('.jfur-write')), false);
    assert.equal(section.isConnected, true);
    form.querySelector('.jfur-review-cancel').click();
    assert.ok(section.querySelector('.jfur-review-form') === null);
});

test('one write at a time: a review saved while a rating is in flight goes after it, its precondition the review\'s own, which the rating leaves alone', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 2, UpdatedUtc: '2026-10-21T08:00:00.000Z' }) }));
    page.respond('PUT', itemPath() + '/Mine/Rating', itemDto({ Mine: mine({ Stars: 2.5, UpdatedUtc: '2026-10-27T10:00:00.456Z' }) }));
    page.respond('PUT', itemPath() + '/Mine/Review', (e) => itemDto({ Mine: mine({ Stars: 2.5, Text: e.body.Text }) }));
    const held = hold(page, 'PUT', /Mine\/Rating$/);
    page.key(slider(section), 'ArrowRight');
    page.key(slider(section), 'Enter');
    await page.advance(10);
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'After the stars';
    page.dispatch(area, 'input');
    section.querySelector('.jfur-review-save').click();
    await page.advance(100);
    assert.equal(calls(page, 'PUT', /Mine\/Review$/).length, 0, 'the review waits for the rating\'s answer');
    held.restore();
    await held.held[0].release();
    await page.advance(100);
    assert.deepEqual(plain(page.fetchLog.filter((e) => e.method === 'PUT').map((e) => e.path.split('/').pop() + ' ' + (e.body.Stars || JSON.stringify([e.body.IfUpdatedUtc, e.body.IfReviewUpdatedUtc, e.body.IfNoReview])))), ['Rating 2.5', 'Review [null,null,true]'], 'stars only on the row: "no review yet", which no rating moves');
});

// ------------------------------------------------------------------------------------------ navigation and leaving

test('item to item without a reload: the old section goes, its waiting change saved once for its own item; the new item read', { skip }, async () => {
    const page = await start();
    const { view, section } = await mounted(page);
    page.respond('PUT', /Mine\/Rating$/, (e) => itemDto({ Mine: mine({ Stars: e.body.Stars }) }));
    page.respond('GET', itemPath(OTHER), itemDto({ ItemId: OTHER }));
    page.key(slider(section), 'End');
    showView(page, view, OTHER);
    await page.advance(400);
    assert.equal(section.isConnected, false);
    const sections = page.document.querySelectorAll('.jfur-section');
    assert.equal(sections.length, 1);
    assert.equal(sections[0].getAttribute('data-jfur-item'), OTHER);
    await page.advance(1000);
    assert.deepEqual(plain(calls(page, 'PUT', /./).map((e) => e.path + ' ' + e.body.Stars)), [itemPath() + '/Mine/Rating 5'], 'the hand-off, once, for the item it was made on');
    page.key(slider(section), 'ArrowLeft');
    await page.advance(1000);
    assert.equal(calls(page, 'PUT', /./).length, 1, 'the old slider is inert');
});

test('leaving the details page: the section goes with the view hidden; a cached view restored reads the item again, one section', { skip }, async () => {
    const page = await start();
    const { view, section } = await mounted(page);
    view.classList.add('hide');
    const home = page.element('div', { id: 'indexPage', class: 'page homePage' });
    page.document.body.append(home);
    page.context.location.hash = '#/home';
    page.dispatch(home, 'viewshow', { detail: { isRestored: false } });
    await page.advance(400);
    assert.equal(section.isConnected, false);
    assert.equal(page.document.querySelectorAll('.jfur-section').length, 0);
    home.classList.add('hide');
    view.classList.remove('hide');
    page.respond('GET', itemPath(), itemDto({ Summary: summary([4]) }));
    showView(page, view, ITEM, true);
    await page.advance(400);
    assert.equal(calls(page, 'GET', /\/Items\/[^/]+$/).length, 2);
    assert.equal(page.document.querySelectorAll('.jfur-section').length, 1);
    assert.equal(page.document.querySelector('.jfur-section .jfur-summary .jfur-stars-display').getAttribute('aria-label'), 'Member average 4.0 out of 5, 1 rating', 'what the server says now');
    showView(page, view, ITEM, false);
    await page.advance(400);
    assert.equal(calls(page, 'GET', /\/Items\/[^/]+$/).length, 2, 'a viewshow that isn\'t a restore reads nothing again');
});

test('a read that failed for a reason of its own: the section with the sentence and Try again', { skip }, async () => {
    const page = await start();
    page.respond('GET', itemPath(), { title: 'x', status: 503, detail: BUSY }, 503);
    detailsView(page);
    await page.advance(400);
    const section = page.document.querySelector('.jfur-section');
    assert.ok(section);
    const notice = section.querySelector('.jfur-load-error [role="alert"]');
    assert.equal(text(notice.querySelector('.jfur-notice-text')), 'Member ratings couldn\'t be loaded. ' + BUSY);
    assert.equal(shown(section.querySelector('.jfur-mine')), false);
    page.respond('GET', itemPath(), itemDto({ Summary: summary([5]) }));
    notice.querySelector('button').click();
    await page.advance(100);
    assert.equal(shown(section.querySelector('.jfur-load-error')), false);
    assert.equal(shown(section.querySelector('.jfur-mine')), true);
    assert.equal(section.querySelector('.jfur-summary .jfur-stars-display').getAttribute('aria-label'), 'Member average 5.0 out of 5, 1 rating');
});

test('jellyfin-web writing the column again: the same section goes back in; a stray one goes; a sign-out unmounts it', { skip }, async () => {
    const page = await start();
    const { content, section } = await mounted(page);
    section.remove();
    await page.advance(400);
    assert.equal(section.parentNode, content, 'put back');
    const stray = page.element('div', { class: 'jfur-section', 'data-jfur-item': 'old' });
    page.document.body.append(stray);
    await page.advance(400);
    assert.equal(stray.isConnected, false);
    assert.equal(calls(page, 'GET', /\/Items\//).length, 1, 'nothing read again');
    page.clientState.userId = null;
    page.dispatch(page.document.querySelector('#itemDetailPage'), 'viewshow');
    await page.advance(400);
    assert.equal(section.isConnected, false, 'signed out: unmounted');
});

test('no API call before the details page, and only the member\'s own routes after', { skip }, async () => {
    const page = await start();
    await page.advance(1000);
    assert.equal(page.fetchLog.length, 0, 'nothing on another view');
    await mounted(page);
    assert.ok(page.fetchLog.every((e) => /^\/UserRatings\/(Capabilities|Items\/[0-9a-f]+)$/.test(e.path)), 'only Capabilities and the item');
    assert.ok(page.fetchLog.every((e) => /Token="test-token"/.test(e.headers.Authorization)), 'as the signed-in member');
});

// --------------------------------------------------------------------------------------- edge cases of the writes

test('reviewBase and reviewChangedSince: the form\'s starting point and whether another window changed the review', { skip }, async () => {
    const d = await pure();
    assert.equal(d.reviewBase(null), null);
    const base = plain(d.reviewBase(mine({ Text: 'Words', IsSpoiler: true, ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' })));
    assert.deepEqual(base, { UpdatedUtc: '2026-10-21T08:00:00.123Z', Text: 'Words', IsSpoiler: true, ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' });
    const same = mine({ Stars: 5, Text: 'Words', IsSpoiler: true, UpdatedUtc: '2026-10-27T10:00:00.000Z', ReviewUpdatedUtc: '2026-10-21T08:00:00Z' });
    assert.equal(d.reviewChangedSince(base, same), false, 'the stars and UpdatedUtc changed, the review didn\'t (a time written another way is the same time)');
    assert.equal(d.reviewChangedSince(base, Object.assign({}, same, { Text: 'Other words' })), true);
    assert.equal(d.reviewChangedSince(base, Object.assign({}, same, { IsSpoiler: false })), true);
    assert.equal(d.reviewChangedSince(base, Object.assign({}, same, { ReviewUpdatedUtc: '2026-10-27T09:00:00.000Z' })), true, 'the same words saved again elsewhere');
    assert.equal(d.reviewChangedSince(base, null), true, 'deleted elsewhere');
    assert.equal(d.reviewChangedSince(null, mine({ Stars: 2 })), false, 'no row, then a rating alone');
    assert.equal(d.reviewChangedSince(null, mine({ Text: 'Written elsewhere' })), true);
    assert.deepEqual(plain(d.reviewBody('Mine', true, d.reviewBase(same))), { Text: 'Mine', IsSpoiler: true, IfReviewUpdatedUtc: '2026-10-21T08:00:00Z' }, 'the review\'s own time');
});

test('a rating saved while the form is open never takes another window\'s review as the form\'s starting point: Save sends the review\'s time the form started from (409), and the form says so', { skip }, async () => {
    // The case: tab A opens Edit, tab B saves a review, tab A changes its stars; the rating's answer carries B's
    // row and its new UpdatedUtc, which became A's IfUpdatedUtc, and A's Save was a 200 over B's text. Now the Save
    // carries the review's own time as the form found it, which neither the rating nor anything but a review moves.
    const page = await start();
    const row = mine({ Stars: 3, Text: 'Tab A original', UpdatedUtc: '2026-10-21T08:00:00.000Z', ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' });
    const { section } = await mounted(page, ITEM, itemDto({ Mine: row }));
    const server = rowServer(page, row);
    section.querySelector('.jfur-own-edit').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Tab A edit';
    page.dispatch(area, 'input');
    server.other({ Text: 'TAB B REVIEW' });
    page.key(slider(section), 'ArrowRight');
    page.key(slider(section), 'Enter');
    await page.advance(200);
    const form = section.querySelector('.jfur-review-form');
    assert.equal(form.querySelector('textarea').value, 'Tab A edit', 'the member\'s text');
    assert.equal(text(form.querySelector('.jfur-review-notice .jfur-notice-text')), 'Your review was changed in another window after you opened it here. Your text here is kept.');
    section.querySelector('.jfur-review-save').click();
    await page.advance(200);
    assert.deepEqual(plain(server.reviews.map((b) => [b.IfReviewUpdatedUtc, 'IfUpdatedUtc' in b])), [['2026-10-21T08:00:00.000Z', false]], 'the review\'s time the form started from, never the other window\'s');
    assert.equal(server.row.Text, 'TAB B REVIEW', 'the other window\'s review stays');
    assert.ok(section.querySelector('.jfur-review-form'), 'not saved: the form and its text stay');
    assert.equal(section.querySelector('.jfur-review-form textarea').value, 'Tab A edit');
    assert.equal(text(section.querySelector('.jfur-review-error .jfur-notice-text')), CONFLICT);

    // The control: a rating whose answer leaves the review as the form found it; the Save is a 200 (the server's check).
    const control = await start();
    const c = (await mounted(control, ITEM, itemDto({ Mine: row }))).section;
    const cs = rowServer(control, row);
    c.querySelector('.jfur-own-edit').click();
    const area2 = c.querySelector('.jfur-review-form textarea');
    area2.value = 'Tab A edit';
    control.dispatch(area2, 'input');
    control.key(slider(c), 'ArrowRight');
    control.key(slider(c), 'Enter');
    await control.advance(200);
    assert.equal(shown(c.querySelector('.jfur-review-notice')), false, 'nothing to say');
    c.querySelector('.jfur-review-save').click();
    await control.advance(200);
    assert.deepEqual(plain(cs.reviews.map((b) => b.IfReviewUpdatedUtc)), ['2026-10-21T08:00:00.000Z'], 'the page\'s own rating moved nothing it sends');
    assert.ok(c.querySelector('.jfur-review-form') === null, 'saved');
    assert.equal(cs.row.Text, 'Tab A edit');
});

test('a rating changed in another window never makes the review\'s Save a 409 (the review\'s own precondition, not the row\'s)', { skip }, async () => {
    const page = await start();
    const row = mine({ Stars: 3, Text: 'Words', UpdatedUtc: '2026-10-21T08:00:00.000Z', ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' });
    const { section } = await mounted(page, ITEM, itemDto({ Mine: row }));
    const server = rowServer(page, row);
    section.querySelector('.jfur-own-edit').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Better words';
    page.dispatch(area, 'input');
    server.other({ Stars: 5 });
    server.other({ Stars: 1 });
    section.querySelector('.jfur-review-save').click();
    await page.advance(200);
    assert.ok(section.querySelector('.jfur-review-form') === null, 'saved: the stars changed elsewhere, the review didn\'t');
    assert.equal(server.row.Text, 'Better words');
    assert.equal(server.row.Stars, 1, 'and the stars the other window gave');
    assert.deepEqual(plain(server.reviews.map((b) => 'IfUpdatedUtc' in b)), [false], 'never the row\'s time');
});

test('a first review written in two windows: the second\'s Save sends IfNoReview and gets the 409, never an overwrite, and the 409 shows the first one', { skip }, async () => {
    // Before, a form opened on no row had nothing to send, so the second window wrote over the first unless a rating's
    // answer happened to show the other review first.
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: null }));
    const server = rowServer(page, null);
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'My first review';
    page.dispatch(area, 'input');
    server.other({ Text: 'Written in the other window' });
    section.querySelector('.jfur-review-save').click();
    await page.advance(200);
    assert.deepEqual(plain(server.reviews), [{ Text: 'My first review', IsSpoiler: false, IfNoReview: true }]);
    assert.equal(server.row.Text, 'Written in the other window', 'never written over');
    assert.equal(section.querySelector('.jfur-review-form textarea').value, 'My first review');
    assert.equal(text(section.querySelector('.jfur-review-error .jfur-notice-text')), CONFLICT);
    assert.equal(text(section.querySelector('.jfur-conflict-newer .jfur-review-text')), 'Written in the other window', 'the other window\'s review shown before Overwrite');
});

test('the slider takes no input while a clear or a delete is queued or under way, so its answer never leaves "Not rated" over a later change', { skip }, async () => {
    for (const op of ['clear', 'delete']) {
        const page = await start();
        const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 3, Text: op === 'delete' ? 'Words' : null }) }));
        page.respond('DELETE', itemPath() + '/Mine/Rating', itemDto({ Mine: null }));
        page.respond('DELETE', itemPath() + '/Mine', null, 204);
        page.respond('GET', itemPath(), itemDto({ Mine: null }));
        page.respond('PUT', itemPath() + '/Mine/Rating', (e) => itemDto({ Mine: mine({ Stars: e.body.Stars }) }));
        const held = hold(page, 'DELETE', /Mine(\/Rating)?$/);
        section.querySelector(op === 'clear' ? '.jfur-clear' : '.jfur-own-delete').click();
        await page.advance(10);
        if (op === 'delete') {
            page.document.querySelector('.jfur-panel .jfur-dialog-confirm').click();
            await page.advance(10);
        }

        const s = slider(section);
        assert.equal(s.getAttribute('aria-disabled'), 'true', op + ': no input meanwhile');
        assert.equal(s.getAttribute('data-jfur-reason'), 'Busy');
        page.key(s, 'ArrowRight');
        page.key(s, 'Enter');
        await page.advance(1000);
        assert.equal(calls(page, 'PUT', /./).length, 0, op + ': nothing to save after it');
        held.restore();
        await held.held[0].release();
        await page.advance(300);
        assert.equal(calls(page, 'PUT', /./).length, 0, op);
        assert.equal(s.getAttribute('aria-valuenow'), '0', op + ': Not rated, as the server has it');
        assert.equal(s.getAttribute('aria-disabled'), null, op + ': the slider again');
        page.key(s, 'ArrowRight');
        page.key(s, 'Enter');
        await page.advance(100);
        assert.deepEqual(plain(calls(page, 'PUT', /Mine\/Rating$/).map((e) => e.body.Stars)), [0.5], op + ': and it saves again');
    }
});

test('a review saved, then the page left, then not saved: a toast says so, as for a rating', { skip }, async () => {
    const page = await start();
    const { section, view } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 3 }) }));
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Long typed review';
    page.dispatch(area, 'input');
    page.respond('PUT', itemPath() + '/Mine/Review', { title: 'x', status: 503, detail: BUSY }, 503);
    const held = hold(page, 'PUT', /Mine\/Review$/);
    section.querySelector('.jfur-review-save').click();
    await page.advance(10);
    page.respond('GET', itemPath(OTHER), itemDto({ ItemId: OTHER }));
    showView(page, view, OTHER);
    await page.advance(400);
    held.restore();
    await held.held[0].release();
    await page.advance(300);
    assert.deepEqual(plain(page.document.querySelectorAll('.jfur-toast').map((t) => t.textContent)), ['Your review wasn\'t saved. ' + BUSY + ' Your text is kept on this device.'], 'the draft kept');
});

test('a rating\'s 403 whose read says the member may no longer write keeps the open form and its text, read-only, until closed', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 3 }) }));
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Typed for ten minutes';
    page.dispatch(area, 'input');
    page.respond('PUT', itemPath() + '/Mine/Rating', { title: 'Forbidden', status: 403, detail: 'Rating is limited to selected members.' }, 403);
    page.respond('GET', itemPath(), itemDto({ Mine: mine({ Stars: 3 }), Can: { Rate: false, Review: false, Moderate: false, Reason: 'NotARater' } }));
    page.key(slider(section), 'ArrowRight');
    page.key(slider(section), 'Enter');
    await page.advance(300);
    const form = section.querySelector('.jfur-review-form');
    assert.ok(form, 'kept (before: closed, the text gone)');
    assert.equal(form.querySelector('textarea').value, 'Typed for ten minutes');
    assert.equal(form.querySelector('textarea').readOnly, true);
    assert.equal(shown(form.querySelector('.jfur-review-save')), false);
    assert.equal(text(form.querySelector('.jfur-review-cancel')), 'Close');
    form.querySelector('.jfur-review-cancel').click();
    assert.ok(section.querySelector('.jfur-review-form') === null);
    assert.equal(calls(page, 'PUT', /Mine\/Review$/).length, 0);
});

test('writes queued, and the slider\'s hand-off, when the signed-in member changes are dropped with a toast, never sent with the next member\'s session', { skip }, async () => {
    const page = await start();
    const { section, view } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 2 }) }));
    page.respond('PUT', itemPath() + '/Mine/Rating', (e) => itemDto({ Mine: mine({ Stars: e.body.Stars }) }));
    page.respond('PUT', itemPath() + '/Mine/Review', (e) => itemDto({ Mine: mine({ Stars: 2.5, Text: e.body.Text }) }));
    const held = hold(page, 'PUT', /Mine\/Rating$/);
    const s = slider(section);
    page.key(s, 'ArrowRight');
    page.key(s, 'Enter');
    await page.advance(10);
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'User A private words';
    page.dispatch(area, 'input');
    section.querySelector('.jfur-review-save').click();
    page.key(s, 'ArrowRight');
    await page.advance(10);
    // User A signs out and user B signs in, the same page, no reload.
    page.clientState.userId = 'user-B';
    page.clientState.token = 'token-B';
    showView(page, view, ITEM);
    await page.advance(400);
    held.restore();
    await held.held[0].release();
    await page.advance(1500);
    const puts = page.fetchLog.filter((e) => e.method === 'PUT');
    // The held request's log entry is written when the test lets it go (after the change); its header is from when it was
    // sent, user A's.
    assert.deepEqual(plain(puts.map((e) => e.path.split('/').pop())), ['Rating'], 'only the save in flight before the change');
    assert.ok(puts.every((e) => /Token="test-token"/.test(e.headers.Authorization)), 'as user A');
    assert.deepEqual(plain(page.document.querySelectorAll('.jfur-toast').map((t) => t.textContent)), ['A rating or review wasn\'t saved: the member who made it is no longer signed in.'], 'said once, without the words');
    assert.equal(page.document.querySelectorAll('.jfur-section').length, 1, 'user B\'s own section');
    assert.ok(page.document.querySelector('.jfur-review-form') === null, 'none of user A\'s text on the page');
});

test('a failed Delete or Clear leaves the focus on its button, which is never rebuilt or disabled meanwhile', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4, Text: 'Words' }) }));
    page.respond('DELETE', itemPath() + '/Mine', { title: 'x', status: 503, detail: BUSY }, 503);
    page.respond('DELETE', itemPath() + '/Mine/Rating', { title: 'x', status: 503, detail: BUSY }, 503);
    const remove = section.querySelector('.jfur-own-delete');
    remove.focus();
    remove.click();
    await page.advance(10);
    const held = hold(page, 'DELETE', /Mine$/);
    page.document.querySelector('.jfur-panel .jfur-dialog-confirm').click();
    await page.advance(10);
    assert.ok(page.document.activeElement === remove, 'back on Delete as the question goes');
    assert.equal(remove.getAttribute('aria-disabled'), 'true', 'marked while it is under way');
    assert.equal(remove.disabled, false, 'never disabled: a disabled button loses the focus');
    held.restore();
    await held.held[0].release();
    await page.advance(100);
    assert.ok(section.querySelector('.jfur-own-delete') === remove, 'the same button (before: the card was built again)');
    assert.ok(page.document.activeElement === remove, 'the focus where it was');
    assert.equal(remove.getAttribute('aria-disabled'), null);
    assert.equal(text(section.querySelector('.jfur-mine-message .jfur-notice-text')), 'Your rating and review weren\'t deleted. ' + BUSY);

    const clear = section.querySelector('.jfur-clear');
    clear.focus();
    clear.click();
    await page.advance(10);
    page.document.querySelector('.jfur-panel .jfur-dialog-confirm').click();
    await page.advance(100);
    assert.ok(page.document.activeElement === clear, 'Clear keeps it too');
    assert.equal(clear.disabled, false);
    assert.equal(clear.getAttribute('aria-disabled'), null);
});

test('a write\'s answer never replaces the other members\' rows, as paged and ordered', { skip }, async () => {
    const page = await start({ capabilities: { Display: { ReviewsPageSize: 2 } } });
    const { section } = await mounted(page, ITEM, itemDto({ Summary: summary([3, 3, 3, 3]), Reviews: { Items: [review(1), review(2)], TotalRecordCount: 4, StartIndex: 0 } }));
    page.respond('GET', itemPath() + '/Reviews', (e) => {
        const all = e.query.sort === 'Oldest' ? [review(4), review(3), review(2), review(1)] : [review(1), review(2), review(3), review(4)];
        const at = Number(e.query.startIndex);
        return { Items: all.slice(at, at + Number(e.query.limit)), TotalRecordCount: 4, StartIndex: at };
    });
    const sort = section.querySelector('select.jfur-sort');
    sort.value = 'Oldest';
    page.dispatch(sort, 'change');
    await page.advance(50);
    section.querySelector('.jfur-pager button').click();
    await page.advance(50);
    const ids = () => plain(section.querySelectorAll('.jfur-review').map((r) => r.getAttribute('data-jfur-review')));
    assert.deepEqual(ids(), ['r4', 'r3', 'r2', 'r1']);
    // The rating's answer carries the server's first page in its default order, and another row besides.
    page.respond('PUT', itemPath() + '/Mine/Rating', itemDto({ Summary: summary([3, 3, 3, 3, 5]), Mine: mine({ Stars: 5 }), Reviews: { Items: [review(9), review(1)], TotalRecordCount: 5, StartIndex: 0 } }));
    page.key(slider(section), 'End');
    page.key(slider(section), 'Enter');
    await page.advance(200);
    assert.deepEqual(ids(), ['r4', 'r3', 'r2', 'r1'], 'as the member paged and ordered them');
    assert.equal(shown(section.querySelector('.jfur-pager')), false, 'the paging as it was');
    assert.equal(sort.value, 'Oldest');
});

test('forty item-to-item moves leave one section and one slider, each item read once per move', { skip }, async () => {
    const page = await start();
    const { view } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 3 }) }));
    page.respond('GET', itemPath(OTHER), itemDto({ ItemId: OTHER }));
    for (let i = 0; i < 40; i++) {
        showView(page, view, i % 2 ? ITEM : OTHER);
        await page.advance(400);
    }

    assert.equal(page.document.querySelectorAll('.jfur-section').length, 1);
    assert.equal(page.document.querySelectorAll('[data-jfur-stars-id]').length, 1);
    assert.equal(calls(page, 'GET', /UserRatings\/Items\/[^/]+$/).length, 41);
    assert.equal(page.errors.length, 0);
});

// -------------------------------------------------------------- spoilers, the 409, drafts, read-only states

const DRAFT = 'jfur:draft:user1:' + ITEM;
const storedDraft = (page, key) => {
    const raw = page.context.localStorage.getItem(key || DRAFT);
    return raw === null ? null : JSON.parse(raw);
};

test('draftName, draftDiffers, readDraft and readOnlyNoteKey', { skip }, async () => {
    const d = await pure();
    assert.equal(d.draftName('User-1', 'A1B2-C3'), 'draft:user1:a1b2c3');
    assert.equal(d.draftName('user-1'), 'draft:user1:', 'the start of every draft of a member');
    const base = { UpdatedUtc: 'x', Text: 'Words', IsSpoiler: false, ReviewUpdatedUtc: null };
    assert.equal(d.draftDiffers('Words', false, null, base), false);
    assert.equal(d.draftDiffers('Words!', false, null, base), true);
    assert.equal(d.draftDiffers('Words', true, null, base), true, 'the flag alone');
    assert.equal(d.draftDiffers('Words', false, [{ text: 'Mine', mine: true }], base), true, 'other texts kept beside it');
    assert.equal(d.draftDiffers('Words', false, [], base), false, 'an empty list keeps nothing');
    assert.equal(d.draftDiffers('', false, null, null), false, 'nothing typed on no row');
    assert.equal(d.readDraft(null), null);
    assert.equal(d.readDraft({ text: 7 }), null, 'anything else in the storage is no draft');
    const saved = '2026-10-27T08:00:00.000Z';
    assert.deepEqual(plain(d.readDraft({ text: 'T', spoiler: true, base: { UpdatedUtc: '2026-10-21T08:00:00.000Z', Text: 'Old', IsSpoiler: 'yes' }, kept: [{ text: 'A', mine: true }, { text: 'B' }, { text: '' }, 'x', null], savedUtc: saved })),
        { text: 'T', spoiler: true, base: { UpdatedUtc: '2026-10-21T08:00:00.000Z', Text: 'Old', IsSpoiler: true, ReviewUpdatedUtc: null }, kept: [{ text: 'A', mine: true }, { text: 'B', mine: false }], savedUtc: saved });
    assert.deepEqual(plain(d.readDraft({ text: 'T', aside: { text: 'A', mine: true }, savedUtc: saved }).kept), [{ text: 'A', mine: true }], 'an older draft\'s one text beside the form is its list');
    assert.deepEqual(plain(d.readDraft({ text: 'T', base: { Text: 'no time' } })), { text: 'T', spoiler: false, base: null, kept: [], savedUtc: null }, 'a starting point without a time is none');
    // keepText: empty texts and repeats aren't kept twice; the member's own words make an entry theirs no longer.
    const kept = [];
    d.keepText(kept, 'Mine', true);
    d.keepText(kept, '   ', true);
    d.keepText(kept, 'Theirs', false);
    d.keepText(kept, 'Theirs', true);
    d.keepText(kept, 'Mine', false);
    assert.deepEqual(plain(kept), [{ text: 'Mine', mine: true }, { text: 'Theirs', mine: true }]);
    // draftExpired: 30 days, and a draft with no time of its own.
    const now = Date.parse('2026-10-27T08:00:00.000Z');
    assert.equal(d.draftExpired({ savedUtc: '2026-09-28T08:00:00.000Z' }, now), false, '29 days');
    assert.equal(d.draftExpired({ savedUtc: '2026-09-27T07:59:59.000Z' }, now), true, 'over 30 days');
    assert.equal(d.draftExpired({ savedUtc: null }, now), false, 'no time of its own: kept, the start dates it');
    assert.equal(d.draftExpired(null, now), true);
    assert.deepEqual(['NotARater', 'NeedsPlay', 'ReadOnly', 'Other'].map(d.readOnlyNoteKey), ['details.noteNotARater', 'details.noteNeedsPlay', 'details.noteReadOnly', 'details.noteUnknown']);
});

test('spoilers: a row\'s text is out of the page until Show spoiler (a blur, no text, in its place), and out again after Hide spoiler', { skip }, async () => {
    const page = await start();
    const secret = 'The ending: everyone was a ghost all along, and the dog knew.';
    const { section } = await mounted(page, ITEM, itemDto({ Reviews: { Items: [review(1, { Text: secret, IsSpoiler: true }), review(2)], TotalRecordCount: 2, StartIndex: 0 } }));
    const row = section.querySelector('[data-jfur-review="r1"]');
    assert.equal(section.textContent.includes('everyone was a ghost'), false, 'not in the page: no Ctrl+F, no screen reader, no copy, no translation');
    assert.ok(row.querySelector('.jfur-review-text') === null);
    const blur = row.querySelector('.jfur-spoiler-blur');
    assert.equal(blur.getAttribute('aria-hidden'), 'true', 'the blur is decoration');
    assert.equal(blur.textContent, '', 'and holds no text');
    assert.equal(blur.querySelectorAll('.jfur-spoiler-line').length, 3, 'three bars, whatever the length');
    const toggle = row.querySelector('.jfur-spoiler-toggle');
    assert.equal(toggle.getAttribute('aria-controls'), row.querySelector('.jfur-spoiler-text').id);
    toggle.focus();
    toggle.click();
    assert.equal(text(row.querySelector('.jfur-review-text')), secret);
    assert.ok(row.querySelector('.jfur-spoiler-blur') === null);
    assert.equal(toggle.getAttribute('aria-expanded'), 'true');
    assert.ok(page.document.activeElement === toggle, 'the focus stays on the toggle');
    assert.equal(row.querySelector('.jfur-review-text').getAttribute('dir'), 'auto');
    toggle.click();
    assert.equal(row.textContent.includes('everyone was a ghost'), false, 'out again');
    assert.equal(text(toggle), 'Show spoiler');
    assert.equal(text(section.querySelector('[data-jfur-review="r2"] .jfur-review-text')), 'Review 2', 'a row without the flag as before');
    const long = await start();
    const s2 = (await mounted(long, ITEM, itemDto({ Reviews: { Items: [review(1, { Text: 'x'.repeat(400), IsSpoiler: true })], TotalRecordCount: 1, StartIndex: 0 } }))).section;
    assert.equal(s2.querySelectorAll('.jfur-spoiler-line').length, 3, 'a long spoiler the same three bars: its length isn\'t told');
    const short = await start();
    const s3 = (await mounted(short, ITEM, itemDto({ Reviews: { Items: [review(1, { Text: 'Dies.', IsSpoiler: true })], TotalRecordCount: 1, StartIndex: 0 } }))).section;
    assert.deepEqual(plain(s3.querySelectorAll('.jfur-spoiler-line').map((b) => b.style.width)), plain(s2.querySelectorAll('.jfur-spoiler-line').map((b) => b.style.width)), 'nor a short one\'s');
});

test('the form\'s "Contains spoilers": sent as ticked, an edit starts from the row\'s flag, none where the server has it off; the own card says it', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4 }) }));
    page.respond('PUT', itemPath() + '/Mine/Review', (e) => itemDto({ Mine: mine({ Stars: 4, Text: e.body.Text, IsSpoiler: e.body.IsSpoiler }) }));
    section.querySelector('.jfur-write').click();
    const form = section.querySelector('.jfur-review-form');
    const box = form.querySelector('input.jfur-review-spoiler');
    assert.equal(box.getAttribute('type'), 'checkbox');
    assert.equal(box.checked, false);
    assert.equal(text(form.querySelector('label.jfur-review-spoiler-field')), 'Contains spoilers');
    assert.equal(form.querySelector('label.jfur-review-spoiler-field').getAttribute('for'), box.id);
    const area = form.querySelector('textarea');
    area.value = 'Big twist';
    page.dispatch(area, 'input');
    box.checked = true;
    page.dispatch(box, 'change');
    section.querySelector('.jfur-review-save').click();
    await page.advance(100);
    assert.deepEqual(plain(calls(page, 'PUT', /Mine\/Review$/).map((e) => e.body.IsSpoiler)), [true]);
    assert.equal(text(section.querySelector('.jfur-own .jfur-spoiler-flag')), 'Contains spoilers', 'the own card says it');
    assert.equal(text(section.querySelector('.jfur-own .jfur-review-text')), 'Big twist', 'and shows the member their own text');
    section.querySelector('.jfur-own-edit').click();
    assert.equal(section.querySelector('.jfur-review-form input.jfur-review-spoiler').checked, true, 'Edit keeps the row\'s flag');

    const off = await start({ capabilities: { AllowSpoilerFlag: false } });
    const s2 = (await mounted(off, ITEM, itemDto({ Mine: mine({ Stars: 4, Text: 'Old', IsSpoiler: true }) }))).section;
    off.respond('PUT', itemPath() + '/Mine/Review', (e) => itemDto({ Mine: mine({ Stars: 4, Text: e.body.Text }) }));
    s2.querySelector('.jfur-own-edit').click();
    assert.ok(s2.querySelector('.jfur-review-spoiler') === null, 'no flag where the server keeps none');
    const a2 = s2.querySelector('.jfur-review-form textarea');
    a2.value = 'New';
    off.dispatch(a2, 'input');
    s2.querySelector('.jfur-review-save').click();
    await off.advance(100);
    assert.deepEqual(plain(calls(off, 'PUT', /Mine\/Review$/).map((e) => e.body.IsSpoiler)), [false]);
});

test('the 409\'s Reload: the newer review in the form, the member\'s text beside it, both kept; "Use my text" swaps them; Save sends the newer review\'s time', { skip }, async () => {
    const page = await start();
    const row = mine({ Stars: 3, Text: 'Original', UpdatedUtc: '2026-10-21T08:00:00.000Z', ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' });
    const { section } = await mounted(page, ITEM, itemDto({ Mine: row }));
    const server = rowServer(page, row);
    section.querySelector('.jfur-own-edit').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'My new words';
    page.dispatch(area, 'input');
    server.other({ Text: 'Other window\'s words', IsSpoiler: true });
    const other = server.row.ReviewUpdatedUtc;
    section.querySelector('.jfur-review-save').click();
    await page.advance(100);
    const reload = section.querySelector('.jfur-conflict-reload');
    assert.ok(page.document.activeElement === reload);
    reload.click();
    await page.advance(100);
    const form = section.querySelector('.jfur-review-form');
    assert.equal(form.querySelector('textarea').value, 'Other window\'s words', 'the newer review in the form');
    assert.equal(form.querySelector('input.jfur-review-spoiler').checked, true, 'its flag too');
    const aside = form.querySelector('.jfur-review-aside');
    assert.equal(shown(aside), true);
    assert.equal(aside.getAttribute('role'), 'group');
    assert.equal(text(aside.querySelector('.jfur-aside-label')), 'Your text, not saved');
    assert.equal(aside.getAttribute('aria-labelledby'), aside.querySelector('.jfur-aside-label').id);
    assert.equal(text(aside.querySelector('.jfur-aside-text')), 'My new words', 'the member\'s text kept beside it, never dropped');
    assert.equal(aside.querySelector('.jfur-aside-text').getAttribute('dir'), 'auto');
    assert.equal(text(form.querySelector('.jfur-review-notice .jfur-notice-text')), 'This is your review as it was saved in the other window. Your text is kept below it.');
    assert.ok(page.document.activeElement === form.querySelector('textarea'));
    assert.deepEqual(plain(storedDraft(page).kept), [{ text: 'My new words', mine: true }], 'the draft keeps both');
    aside.querySelector('.jfur-aside-use').click();
    const swapped = form.querySelector('.jfur-review-aside');
    assert.equal(form.querySelector('textarea').value, 'My new words', 'Use my text');
    assert.equal(text(swapped.querySelector('.jfur-aside-label')), 'Your review as saved in the other window');
    assert.equal(text(swapped.querySelector('.jfur-aside-text')), 'Other window\'s words', 'the other one now beside it');
    assert.equal(text(swapped.querySelector('.jfur-aside-use')), 'Use that text');
    assert.ok(page.document.activeElement === swapped.querySelector('.jfur-aside-use'));
    section.querySelector('.jfur-review-save').click();
    await page.advance(100);
    assert.deepEqual(plain(server.reviews.map((b) => [b.Text, b.IfReviewUpdatedUtc])), [['My new words', '2026-10-21T08:00:00.000Z'], ['My new words', other]]);
    assert.ok(section.querySelector('.jfur-review-form') === null, 'saved');
    assert.equal(server.row.Text, 'My new words');
    assert.equal(storedDraft(page), null, 'the draft gone with the save');
});

test('the 409 shows the review as it is now, read-only, isolated and spoiler-aware; Overwrite sends that row\'s review time (no read first), so a later change is a 409 again with the newer text shown', { skip }, async () => {
    const page = await start();
    const row = mine({ Stars: 3, Text: 'Original', UpdatedUtc: '2026-10-21T08:00:00.000Z', ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' });
    const { section } = await mounted(page, ITEM, itemDto({ Mine: row }));
    const server = rowServer(page, row);
    section.querySelector('.jfur-own-edit').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Mine wins';
    page.dispatch(area, 'input');
    const secret = 'Theirs: the butler did it.';
    server.other({ Text: secret, IsSpoiler: true });
    const theirs = server.row.ReviewUpdatedUtc;
    section.querySelector('.jfur-review-save').click();
    await page.advance(100);
    const error = section.querySelector('.jfur-review-error');
    const panel = error.querySelector('.jfur-conflict-newer');
    assert.ok(panel, 'the newer review under the 409\'s sentence');
    assert.equal(panel.getAttribute('role'), 'group');
    assert.equal(panel.getAttribute('aria-labelledby'), panel.querySelector('.jfur-conflict-label').id);
    assert.equal(text(panel.querySelector('.jfur-conflict-label')), 'Your review as it is now:');
    assert.ok(panel.querySelector('textarea, input') === null, 'read-only: text, no field');
    assert.equal(section.textContent.includes('the butler did it'), false, 'a spoiler stays out of the page until asked');
    assert.ok(panel.querySelector('.jfur-spoiler-blur'));
    panel.querySelector('.jfur-spoiler-toggle').click();
    assert.equal(text(panel.querySelector('.jfur-review-text')), secret);
    assert.equal(panel.querySelector('.jfur-review-text').getAttribute('dir'), 'auto', 'isolated');
    assert.equal(shown(error.querySelector('.jfur-conflict-overwrite')), true);

    // A third window writes after the panel was shown: Overwrite sends the shown row's review time, so it is a 409 again.
    server.other({ Text: 'Third window', IsSpoiler: false });
    const third = server.row.ReviewUpdatedUtc;
    const before = page.fetchLog.length;
    error.querySelector('.jfur-conflict-overwrite').click();
    await page.advance(100);
    assert.deepEqual(plain(page.fetchLog.slice(before, before + 1).map((e) => e.method + ' ' + e.path.split('/').pop())), ['PUT Review'], 'no read before the write: the row as shown');
    assert.equal(server.reviews[1].IfReviewUpdatedUtc, theirs, 'the shown row\'s review time');
    assert.equal(server.row.Text, 'Third window', 'nothing written over the third window\'s');
    assert.equal(section.querySelector('.jfur-review-form textarea').value, 'Mine wins', 'nothing lost');
    assert.equal(text(section.querySelector('.jfur-conflict-newer .jfur-review-text')), 'Third window', 'the newer text shown now');
    section.querySelector('.jfur-conflict-overwrite').click();
    await page.advance(100);
    assert.equal(server.reviews[2].IfReviewUpdatedUtc, third);
    assert.ok(section.querySelector('.jfur-review-form') === null, 'overwritten, as the member chose after seeing it');
    assert.equal(server.row.Text, 'Mine wins');
    assert.equal(text(section.querySelector('.jfur-own .jfur-review-text')), 'Mine wins');
    assert.equal(storedDraft(page), null);

    // A review deleted in the other window: the panel says so, and Overwrite sends "no review yet".
    const p2 = await start();
    const s2 = (await mounted(p2, ITEM, itemDto({ Mine: row }))).section;
    const v2 = rowServer(p2, row);
    s2.querySelector('.jfur-own-edit').click();
    const a2 = s2.querySelector('.jfur-review-form textarea');
    a2.value = 'Back again';
    p2.dispatch(a2, 'input');
    v2.other({ Text: null });
    s2.querySelector('.jfur-review-save').click();
    await p2.advance(100);
    assert.equal(text(s2.querySelector('.jfur-conflict-review .jfur-conflict-label')), 'Your review was deleted in the other window.');
    s2.querySelector('.jfur-conflict-overwrite').click();
    await p2.advance(100);
    assert.equal(v2.reviews[1].IfNoReview, true);
    assert.equal(v2.row.Text, 'Back again');
});

test('Reload after Reload never drops the member\'s text: three windows, every text of the member\'s kept beside the form, and in its draft', { skip }, async () => {
    // The sequence: window A edits; window B saves; A's Save is a 409 and A reloads (B's review in the text area, A's
    // text kept); A edits B's review in the text area; window C saves; A's Save is a 409 again and A reloads. Before, the second
    // Reload put A's edit of B's text in the place of A's first text, which was gone.
    const page = await start();
    const row = mine({ Stars: 3, Text: 'T0', UpdatedUtc: '2026-10-21T08:00:00.000Z', ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' });
    const { section } = await mounted(page, ITEM, itemDto({ Mine: row }));
    const server = rowServer(page, row);
    section.querySelector('.jfur-own-edit').click();
    const form = section.querySelector('.jfur-review-form');
    const area = form.querySelector('textarea');
    const keptTexts = () => form.querySelectorAll('.jfur-review-aside').map((g) => [text(g.querySelector('.jfur-aside-label')), text(g.querySelector('.jfur-aside-text'))]);
    area.value = 'A first';
    page.dispatch(area, 'input');
    server.other({ Text: 'B saved' });
    form.querySelector('.jfur-review-save').click();
    await page.advance(100);
    form.querySelector('.jfur-conflict-reload').click();
    await page.advance(100);
    assert.equal(area.value, 'B saved');
    assert.deepEqual(plain(keptTexts()), [['Your text, not saved', 'A first']]);
    area.value = 'B saved, and A\'s edit of it';
    page.dispatch(area, 'input');
    server.other({ Text: 'C saved' });
    form.querySelector('.jfur-review-save').click();
    await page.advance(100);
    assert.equal(text(form.querySelector('.jfur-conflict-newer .jfur-review-text')), 'C saved');
    form.querySelector('.jfur-conflict-reload').click();
    await page.advance(100);
    assert.equal(area.value, 'C saved', 'the newest review in the text area');
    assert.deepEqual(plain(keptTexts()), [['Your text, not saved', 'A first'], ['Your text, not saved', 'B saved, and A\'s edit of it']], 'both of the member\'s texts kept');
    assert.deepEqual(plain(storedDraft(page).kept.map((k) => k.text)), ['A first', 'B saved, and A\'s edit of it'], 'and in the draft');

    // A third 409 with the text area untouched keeps the same two (the other windows' reviews come and go).
    server.other({ Text: 'D saved' });
    form.querySelector('.jfur-review-save').click();
    await page.advance(100);
    form.querySelector('.jfur-conflict-reload').click();
    await page.advance(100);
    assert.equal(area.value, 'D saved');
    assert.deepEqual(plain(keptTexts().map((k) => k[1])), ['A first', 'B saved, and A\'s edit of it']);

    // Using one puts the newest review in the list; the draft, closed and opened again, brings all of it back.
    form.querySelectorAll('.jfur-aside-use')[0].click();
    assert.equal(area.value, 'A first');
    assert.deepEqual(plain(keptTexts()), [['Your text, not saved', 'B saved, and A\'s edit of it'], ['Your review as saved in the other window', 'D saved']]);
    form.querySelector('.jfur-review-cancel').click();
    section.querySelector('.jfur-own-edit').click();
    const again = section.querySelector('.jfur-review-form');
    assert.equal(again.querySelector('textarea').value, 'A first');
    assert.deepEqual(plain(again.querySelectorAll('.jfur-aside-text').map(text)), ['B saved, and A\'s edit of it', 'D saved']);
    again.querySelector('.jfur-review-save').click();
    await page.advance(100);
    assert.equal(server.row.Text, 'A first', 'saved against the newest review (its time from the Reload)');

    // The member's other unsaved text stays beside the form, which stays open on the review as saved,
    // and in the draft (before: the save dropped it without a word); the other window's review, which the save replaced as
    // the member chose, goes. Discard lets the text go; then Cancel closes the form with nothing kept.
    assert.ok(section.querySelector('.jfur-review-form') === again, 'the form stays open');
    assert.equal(again.querySelector('textarea').value, 'A first');
    assert.deepEqual(plain(again.querySelectorAll('.jfur-review-aside').map((g) => [text(g.querySelector('.jfur-aside-label')), text(g.querySelector('.jfur-aside-text'))])), [['Your text, not saved', 'B saved, and A\'s edit of it']]);
    assert.equal(text(again.querySelector('.jfur-review-restored .jfur-notice-text')), 'Review saved. The other texts you kept are still beside the form.');
    assert.equal(shown(again.querySelector('.jfur-review-error')), false, 'no 409 left');
    assert.deepEqual(plain(storedDraft(page).kept), [{ text: 'B saved, and A\'s edit of it', mine: true }]);
    assert.equal(storedDraft(page).base.ReviewUpdatedUtc, server.row.ReviewUpdatedUtc, 'its starting point is the review as saved');
    assert.ok(page.document.activeElement === again.querySelector('textarea'));
    again.querySelector('.jfur-draft-discard').click();
    assert.equal(again.querySelectorAll('.jfur-review-aside').length, 0);
    assert.equal(again.querySelector('textarea').value, 'A first');
    again.querySelector('.jfur-review-cancel').click();
    assert.ok(section.querySelector('.jfur-review-form') === null);
    assert.equal(storedDraft(page), null);
    assert.equal(text(section.querySelector('.jfur-own-edit')), 'Edit', 'nothing waiting');
});

test('drafts: saved on every input after a pause, per member and item; Cancel keeps it ("Continue your review"); put back with Discard; a save clears it', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4 }) }));
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Half a thought';
    page.dispatch(area, 'input');
    await page.advance(100);
    assert.equal(storedDraft(page), null, 'not on every key at once');
    await page.advance(400);
    assert.equal(storedDraft(page).text, 'Half a thought', 'under jfur:draft:<user>:<item>');
    assert.equal(storedDraft(page).base.UpdatedUtc, '2026-10-21T08:00:00.123Z', 'with the form\'s starting point');
    section.querySelector('.jfur-review-cancel').click();
    assert.equal(text(section.querySelector('.jfur-write')), 'Continue your review');
    assert.ok(storedDraft(page), 'Cancel keeps it');
    section.querySelector('.jfur-write').click();
    const form = section.querySelector('.jfur-review-form');
    assert.equal(form.querySelector('textarea').value, 'Half a thought', 'put back');
    assert.equal(text(form.querySelector('.jfur-review-restored .jfur-notice-text')), 'Your unsaved text was put back.');
    form.querySelector('.jfur-draft-discard').click();
    assert.equal(form.querySelector('textarea').value, '', 'Discard: the review as the server has it');
    assert.equal(storedDraft(page), null);
    assert.ok(page.document.activeElement === form.querySelector('textarea'));
    const a2 = form.querySelector('textarea');
    a2.value = 'Final words';
    page.dispatch(a2, 'input');
    page.respond('PUT', itemPath() + '/Mine/Review', (e) => itemDto({ Mine: mine({ Stars: 4, Text: e.body.Text }) }));
    section.querySelector('.jfur-review-save').click();
    await page.advance(100);
    assert.equal(storedDraft(page), null, 'a save clears it');
    assert.equal(text(section.querySelector('.jfur-own-edit')), 'Edit');
    assert.equal(calls(page, 'PUT', /./).every((e) => !/draft/.test(JSON.stringify(e.body))), true);
    assert.ok(page.fetchLog.every((e) => !/Half a thought/.test(JSON.stringify(e.body || ''))), 'a draft is never sent anywhere');
});

test('drafts across leaving: another item, another view and TV Back write it at once; it comes back on the item; a draft against a review changed since keeps its own starting point', { skip }, async () => {
    for (const how of ['item', 'view', 'tv']) {
        const page = await start(how === 'tv' ? { layout: 'tv' } : undefined);
        const { view, section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4, Text: 'Saved text', UpdatedUtc: '2026-10-21T08:00:00.000Z' }) }));
        section.querySelector('.jfur-own-edit').click();
        const area = section.querySelector('.jfur-review-form textarea');
        area.value = 'Saved text, and more';
        page.dispatch(area, 'input');
        if (how === 'item') {
            page.respond('GET', itemPath(OTHER), itemDto({ ItemId: OTHER }));
            showView(page, view, OTHER);
        } else {
            // Another view, as Back (the TV layout's Escape) shows it: the details view hidden, the previous one shown.
            view.classList.add('hide');
            const home = page.element('div', { id: 'indexPage', class: 'page homePage' });
            page.document.body.append(home);
            page.context.location.hash = '#/home';
            page.dispatch(home, 'viewshow', { detail: { isRestored: true } });
        }

        // The scanner runs 150 ms after the change, before the draft's own 400 ms.
        await page.advance(250);
        assert.equal(section.isConnected, false, how);
        assert.equal(storedDraft(page).text, 'Saved text, and more', how + ': written at once, before its pause');
        // Back on the item: the draft comes back; meanwhile another window changed the review.
        page.document.querySelectorAll('.page').forEach((p) => p.classList.add('hide'));
        view.classList.remove('hide');
        page.respond('GET', itemPath(), itemDto({ Mine: mine({ Stars: 4, Text: 'Changed elsewhere', UpdatedUtc: '2026-10-27T09:00:00.000Z', ReviewUpdatedUtc: '2026-10-27T09:00:00.000Z' }) }));
        showView(page, view, ITEM, true);
        await page.advance(400);
        const again = page.document.querySelector('.jfur-section');
        assert.equal(text(again.querySelector('.jfur-own-edit')), 'Continue editing', how);
        again.querySelector('.jfur-own-edit').click();
        const form = again.querySelector('.jfur-review-form');
        assert.equal(form.querySelector('textarea').value, 'Saved text, and more', how + ': put back');
        assert.equal(text(form.querySelector('.jfur-review-notice .jfur-notice-text')), 'Your review was changed in another window after this text was written.');
        page.respond('PUT', itemPath() + '/Mine/Review', { title: 'Conflict', status: 409, detail: 'changed' }, 409);
        again.querySelector('.jfur-review-save').click();
        await page.advance(100);
        assert.deepEqual(plain(calls(page, 'PUT', /Mine\/Review$/).map((e) => e.body.IfUpdatedUtc)), ['2026-10-21T08:00:00.000Z'], how + ': the draft\'s own starting point, so the 409');
        assert.equal(text(again.querySelector('.jfur-review-error .jfur-notice-text')), CONFLICT);
    }
});

test('drafts go when their member signs out, and any other member\'s when one signs in; without storage the page works and keeps none', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4 }) }));
    page.context.localStorage.setItem('jfur:draft:user1:' + OTHER, JSON.stringify({ text: 'On another item', base: null }));
    page.context.localStorage.setItem('jfbm:draft:user1:x', 'another plugin\'s');
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Private words';
    page.dispatch(area, 'input');
    await page.advance(500);
    assert.ok(storedDraft(page));
    page.clientState.userId = null;
    page.dispatch(page.document.querySelector('#itemDetailPage'), 'viewshow');
    await page.advance(400);
    assert.equal(storedDraft(page), null, 'signed out: the member\'s drafts go, the open form\'s too');
    assert.equal(page.context.localStorage.getItem('jfur:draft:user1:' + OTHER), null, 'every item\'s');
    assert.equal(page.context.localStorage.getItem('jfbm:draft:user1:x'), 'another plugin\'s', 'never another plugin\'s');
    // A draft left by a sign-out this page didn't see goes when another member signs in.
    page.context.localStorage.setItem('jfur:draft:user1:' + OTHER, JSON.stringify({ text: 'Left behind', base: null }));
    page.clientState.userId = 'user-2';
    page.dispatch(page.document.querySelector('#itemDetailPage'), 'viewshow');
    await page.advance(400);
    assert.equal(page.context.localStorage.getItem('jfur:draft:user1:' + OTHER), null, 'another member signed in');

    const broken = createPage();
    broken.document.createElementNS = (ns, tag) => broken.document.createElement(tag);
    Object.defineProperty(broken.context, 'localStorage', { configurable: true, get: () => { throw new Error('SecurityError'); } });
    broken.respond('GET', '/UserRatings/Capabilities', capabilities());
    broken.run(source, file);
    await broken.advance(50);
    broken.installApiClient({ userId: 'user-1' });
    await broken.advance(50);
    const s2 = (await mounted(broken, ITEM, itemDto({ Mine: mine({ Stars: 4 }) }))).section;
    broken.respond('PUT', itemPath() + '/Mine/Review', (e) => itemDto({ Mine: mine({ Stars: 4, Text: e.body.Text }) }));
    s2.querySelector('.jfur-write').click();
    const a2 = s2.querySelector('.jfur-review-form textarea');
    a2.value = 'Works anyway';
    broken.dispatch(a2, 'input');
    await broken.advance(500);
    s2.querySelector('.jfur-review-cancel').click();
    assert.equal(text(s2.querySelector('.jfur-write')), 'Write a review', 'no draft kept');
    s2.querySelector('.jfur-write').click();
    const a3 = s2.querySelector('.jfur-review-form textarea');
    a3.value = 'Works anyway';
    broken.dispatch(a3, 'input');
    s2.querySelector('.jfur-review-save').click();
    await broken.advance(100);
    assert.equal(text(s2.querySelector('.jfur-own .jfur-review-text')), 'Works anyway', 'saved as ever');
    assert.equal(broken.errors.length, 0);
});

test('read-only states: NotARater read-only stars and their note, no tab stop; NeedsPlay, ReadOnly and an unknown reason the disabled slider with its note as its description', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 3, Text: 'Old words' }), Can: { Rate: false, Review: false, Moderate: false, Reason: 'NotARater' } }));
    const row = section.querySelector('.jfur-mine-row');
    const ro = row.querySelector('.jfur-mine-readonly');
    assert.equal(shown(ro), true);
    assert.equal(shown(slider(section)), false, 'the slider out of view, and of the tab order');
    const stars = ro.querySelector('.jfur-stars-display');
    assert.equal(stars.getAttribute('role'), 'img');
    assert.equal(stars.getAttribute('aria-label'), 'Rated 3 out of 5');
    assert.equal(text(ro.querySelector('.jfur-mine-note')), 'Rating is limited to selected members.');
    assert.equal(ro.querySelector('.jfur-mine-note').getAttribute('aria-hidden'), null, 'in the reading order');
    assert.equal(ro.querySelectorAll('button, a, input, select, textarea, [tabindex]').length, 0, 'no tab stop: nothing to do with them');
    assert.equal(shown(section.querySelector('.jfur-clear')), true, 'the member\'s own clear stays');
    assert.ok(section.querySelector('.jfur-own-delete'), 'and their own delete');
    page.respond('DELETE', itemPath() + '/Mine/Rating', itemDto({ Mine: mine({ Stars: null, Text: 'Old words' }), Can: { Rate: false, Review: false, Moderate: false, Reason: 'NotARater' } }));
    section.querySelector('.jfur-clear').click();
    await page.advance(10);
    page.document.querySelector('.jfur-panel .jfur-dialog-confirm').click();
    await page.advance(100);
    assert.equal(ro.querySelector('.jfur-stars-display').getAttribute('aria-label'), 'Not rated', 'the read-only stars follow the server');

    for (const [reason, note, own] of [['NeedsPlay', 'You can rate this once you have played it.', true], ['ReadOnly', 'Ratings can\'t be changed right now.', false], ['FromTheFuture', 'Rating isn\'t available right now.', false]]) {
        const p = await start();
        const s = (await mounted(p, ITEM, itemDto({ Mine: mine({ Stars: 2 }), Can: { Rate: false, Review: false, Moderate: false, Reason: reason } }))).section;
        const sl = slider(s);
        assert.equal(shown(sl), true, reason + ': the slider, a tab stop');
        assert.equal(sl.getAttribute('tabindex'), '0');
        assert.equal(sl.getAttribute('aria-disabled'), 'true');
        assert.equal(sl.getAttribute('data-jfur-reason'), reason);
        const noteId = sl.getAttribute('aria-describedby');
        assert.ok(noteId, reason + ': its note is its description');
        assert.equal(text(s.querySelector('#' + noteId)), note);
        assert.equal(shown(s.querySelector('#' + noteId)), true, reason + ': and beside it');
        assert.equal(shown(s.querySelector('.jfur-mine-readonly')), false);
        p.key(sl, 'ArrowRight');
        p.key(sl, 'Enter');
        await p.advance(800);
        assert.equal(calls(p, 'PUT', /./).length, 0, reason + ': nothing sent');
        assert.equal(shown(s.querySelector('.jfur-clear')), own, reason + ': Clear only where the server allows it');
    }
});

test('ReadOnly (the store read-only) while the form is open: the form disabled with its text kept, and its draft', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 3 }) }));
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Written while the store went read-only';
    page.dispatch(area, 'input');
    page.respond('PUT', itemPath() + '/Mine/Review', { title: 'x', status: 403, detail: 'The ratings database is read-only.' }, 403);
    page.respond('GET', itemPath(), itemDto({ Mine: mine({ Stars: 3 }), Can: { Rate: false, Review: false, Moderate: false, Reason: 'ReadOnly' } }));
    section.querySelector('.jfur-review-save').click();
    await page.advance(200);
    const form = section.querySelector('.jfur-review-form');
    assert.equal(form.querySelector('textarea').readOnly, true);
    assert.equal(form.querySelector('textarea').value, 'Written while the store went read-only');
    assert.equal(shown(form.querySelector('.jfur-review-save')), false);
    assert.equal(form.querySelector('input.jfur-review-spoiler').disabled, true);
    assert.equal(slider(section).getAttribute('data-jfur-reason'), 'ReadOnly');
    form.querySelector('.jfur-review-cancel').click();
    assert.equal(storedDraft(page).text, 'Written while the store went read-only', 'kept for when it can be saved');
});

test('the section gone (a 403 or 404) while the form holds unsaved text: kept as a draft, and a toast says so', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 3 }) }));
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Not lost';
    page.dispatch(area, 'input');
    page.respond('PUT', itemPath() + '/Mine/Review', { title: 'Not Found', status: 404, detail: NOT_FOUND }, 404);
    section.querySelector('.jfur-review-save').click();
    await page.advance(200);
    assert.equal(section.isConnected, false);
    assert.equal(storedDraft(page).text, 'Not lost');
    assert.deepEqual(plain(page.document.querySelectorAll('.jfur-toast').map((t) => t.textContent)), ['Your unsaved review is kept on this device for the next time you open this item.']);
});

// ------------------------------------------------------------------------------ drafts, sign-outs and the focus

test('Edit and Write a review open no form while a Delete or a Clear is under way', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4, Text: 'Words', ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' }) }));
    page.respond('DELETE', itemPath() + '/Mine', null, 204);
    page.respond('GET', itemPath(), itemDto({ Mine: null }));
    const edit = section.querySelector('.jfur-own-edit');
    section.querySelector('.jfur-own-delete').click();
    await page.advance(10);
    const held = hold(page, 'DELETE', /Mine$/);
    page.document.querySelector('.jfur-panel .jfur-dialog-confirm').click();
    await page.advance(10);
    assert.equal(edit.getAttribute('aria-disabled'), 'true');
    edit.click();
    assert.ok(section.querySelector('.jfur-review-form') === null, 'Edit during a Delete: no form (before: the form opened on the row being deleted)');
    held.restore();
    await held.held[0].release();
    await page.advance(200);
    assert.ok(section.querySelector('.jfur-own-edit') === null, 'deleted');

    const p2 = await start();
    const s2 = (await mounted(p2, ITEM, itemDto({ Mine: mine({ Stars: 4 }) }))).section;
    p2.respond('DELETE', itemPath() + '/Mine/Rating', itemDto({ Mine: null }));
    const h2 = hold(p2, 'DELETE', /Mine\/Rating$/);
    s2.querySelector('.jfur-clear').click();
    await p2.advance(10);
    s2.querySelector('.jfur-write').click();
    assert.ok(s2.querySelector('.jfur-review-form') === null, 'Write a review during a Clear: no form');
    h2.restore();
    await h2.held[0].release();
    await p2.advance(200);
    s2.querySelector('.jfur-write').click();
    assert.ok(s2.querySelector('.jfur-review-form'), 'once it is done, the form opens');
});

test('a Save in flight that fails after its member signed out writes no draft back and says nothing to the next one', { skip }, async () => {
    const page = await start();
    const { section, view } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4 }) }));
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Private words';
    page.dispatch(area, 'input');
    page.respond('PUT', itemPath() + '/Mine/Review', { title: 'x', status: 503, detail: BUSY }, 503);
    const held = hold(page, 'PUT', /Mine\/Review$/);
    section.querySelector('.jfur-review-save').click();
    await page.advance(10);
    assert.equal(held.held.length, 1, 'sent before the sign-out');
    page.clientState.userId = null;
    showView(page, view, ITEM);
    await page.advance(400);
    assert.equal(storedDraft(page), null, 'the sign-out removed the draft');
    held.restore();
    await held.held[0].release();
    await page.advance(200);
    assert.equal(storedDraft(page), null, 'and the failed save didn\'t write it back');
    assert.equal(page.document.querySelectorAll('.jfur-toast').length, 0, 'nor say anything');
    const store = page.context.localStorage;
    assert.deepEqual(plain([...Array(store.length).keys()].map((i) => store.key(i)).filter((k) => /draft/.test(k))), [], 'no draft of anyone\'s');
});

test('a draft the browser refuses to store removes the older one, which never comes back as "your unsaved text"', { skip }, async () => {
    const page = await start();
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4 }) }));
    section.querySelector('.jfur-write').click();
    let area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Old words';
    page.dispatch(area, 'input');
    await page.advance(500);
    assert.equal(storedDraft(page).text, 'Old words');
    const store = page.context.localStorage;
    const setItem = store.setItem;
    store.setItem = () => {
        throw new Error('QuotaExceededError');
    };
    area.value = 'Old words, and newer ones';
    page.dispatch(area, 'input');
    await page.advance(500);
    assert.equal(storedDraft(page), null, 'the older draft gone: it isn\'t what the form holds');

    // Closing the form here would lose the text, so the first Cancel leaves it open and says why;
    // the second closes it.
    section.querySelector('.jfur-review-cancel').click();
    assert.ok(section.querySelector('.jfur-review-form'), 'still open');
    assert.equal(text(section.querySelector('.jfur-review-error .jfur-notice-text')), CANCEL_NOT_KEPT);
    assert.equal(section.querySelector('.jfur-review-form textarea').value, 'Old words, and newer ones');
    section.querySelector('.jfur-review-cancel').click();
    assert.ok(section.querySelector('.jfur-review-form') === null, 'the second Cancel closes it');
    store.setItem = setItem;
    assert.equal(text(section.querySelector('.jfur-write')), 'Write a review', 'nothing waiting');
    section.querySelector('.jfur-write').click();
    area = section.querySelector('.jfur-review-form textarea');
    assert.equal(area.value, '', 'no older text put back');
    assert.equal(shown(section.querySelector('.jfur-review-restored')), false);
});

test('the slider turned read-only under the focus (NotARater) gives the focus to the read-only stars, never the page\'s body; allowed again, back to the slider', { skip }, async () => {
    const page = await start({ layout: 'tv' });
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 3 }) }));
    page.respond('PUT', itemPath() + '/Mine/Rating', { title: 'Forbidden', status: 403, detail: 'Rating is limited to selected members.' }, 403);
    page.respond('GET', itemPath(), itemDto({ Mine: mine({ Stars: 3 }), Can: { Rate: false, Review: false, Moderate: false, Reason: 'NotARater' } }));
    const s = slider(section);
    s.focus();
    page.key(s, 'ArrowRight');
    page.key(s, 'Enter');
    await page.advance(300);
    const ro = section.querySelector('.jfur-mine-readonly');
    assert.equal(shown(ro), true);
    // Compared with ===, so a failure names the element (assert.equal would print both fake-DOM trees, which takes minutes).
    const focused = () => { const a = page.document.activeElement; return a ? a.tagName + '.' + a.className : String(a); };
    assert.ok(page.document.activeElement !== page.document.body, 'not the body: the D-pad would have nowhere to go (' + focused() + ')');
    assert.ok(page.document.activeElement === ro, 'the read-only stars in the slider\'s place (' + focused() + ')');
    assert.equal(ro.getAttribute('tabindex'), '-1', 'focusable, still no tab stop');
    page.respond('GET', itemPath(), itemDto({ Mine: mine({ Stars: 3 }) }));
    page.document.visibilityState = 'hidden';
    page.dispatch(page.document, 'visibilitychange');
    page.document.visibilityState = 'visible';
    page.dispatch(page.document, 'visibilitychange');
    await page.advance(100);
    assert.equal(shown(ro), false);
    assert.equal(ro.getAttribute('tabindex'), null);
    assert.ok(page.document.activeElement === s, 'allowed again: the slider takes it back (' + focused() + ')');
});

test('drafts saved more than 30 days ago go at start; a member\'s drafts go when their session expires, and no more are written', { skip }, async () => {
    const page = createPage();
    page.document.createElementNS = (ns, tag) => page.document.createElement(tag);
    const day = 24 * 60 * 60 * 1000;
    const store = page.context.localStorage;
    store.setItem('jfur:draft:user1:' + OTHER, JSON.stringify({ v: 2, text: 'Old', base: null, kept: [], savedUtc: new Date(Date.now() - 31 * day).toISOString() }));
    store.setItem('jfur:draft:user1:' + ITEM, JSON.stringify({ v: 2, text: 'Recent', base: null, kept: [], savedUtc: new Date(Date.now() - 29 * day).toISOString() }));
    store.setItem('jfur:draft:user1:x', JSON.stringify({ text: 'No time' }));
    store.setItem('jfur:draft:user1:y', JSON.stringify({ nothing: 'a draft' }));
    store.setItem('jfbm:draft:user1:x', 'another plugin\'s');
    page.respond('GET', '/UserRatings/Capabilities', capabilities());
    page.run(source, file);
    assert.equal(store.getItem('jfur:draft:user1:' + OTHER), null, 'over 30 days: gone at start');
    // A draft with no time of its own is kept, dated now, so it
    // goes 30 days from here; what isn't a draft goes.
    const undated = JSON.parse(store.getItem('jfur:draft:user1:x'));
    assert.equal(undated.text, 'No time', 'no time of its own: kept');
    assert.ok(isFinite(Date.parse(undated.savedUtc)), 'and dated');
    assert.equal(store.getItem('jfur:draft:user1:y'), null, 'not a draft: gone');
    assert.ok(store.getItem('jfur:draft:user1:' + ITEM), '29 days: kept');
    assert.equal(store.getItem('jfbm:draft:user1:x'), 'another plugin\'s');
    await page.advance(50);
    page.installApiClient({ userId: 'user-1' });
    await page.advance(50);
    const { section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 4 }) }));
    section.querySelector('.jfur-write').click();
    const area = section.querySelector('.jfur-review-form textarea');
    assert.equal(area.value, 'Recent', 'the kept one comes back');
    store.setItem('jfur:draft:user2:' + ITEM, JSON.stringify({ v: 2, text: 'Another member\'s', base: null, kept: [], savedUtc: new Date().toISOString() }));
    page.dispatch(page.document, 'jfur:session-expired');
    assert.equal(storedDraft(page), null, 'the session expired: the member\'s drafts go');
    area.value = 'Typed after';
    page.dispatch(area, 'input');
    await page.advance(500);
    section.querySelector('.jfur-review-cancel').click();
    assert.equal(storedDraft(page), null, 'and none is written again');
    assert.ok(store.getItem('jfur:draft:user2:' + ITEM), 'another member\'s stays');
});

test('the page shown again reads the item again while the member must play it first or isn\'t a rater, and only then', { skip }, async () => {
    const page = await start();
    await mounted(page, ITEM, itemDto({ Mine: null, Can: { Rate: false, Review: false, Moderate: false, Reason: 'NeedsPlay' } }));
    const reads = () => calls(page, 'GET', /\/Items\//).length;
    const show = async () => {
        page.document.visibilityState = 'hidden';
        page.dispatch(page.document, 'visibilitychange');
        page.document.visibilityState = 'visible';
        page.dispatch(page.document, 'visibilitychange');
        await page.advance(100);
    };
    assert.equal(reads(), 1);
    page.respond('GET', itemPath(), itemDto({ Mine: null }));
    await show();
    assert.equal(reads(), 2, 'played elsewhere meanwhile? read again');
    const s = slider(page.document.querySelector('.jfur-section'));
    assert.equal(s.getAttribute('aria-disabled'), null, 'it may be rated now');
    await show();
    assert.equal(reads(), 2, 'a member who may rate: nothing read again');
});

test('reads respect the member they are for: a read after the user changed is never sent, one answered after it is never shown', { skip }, async () => {
    const page = await start();
    const row = mine({ Stars: 3, Text: 'Original', ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' });
    const { section } = await mounted(page, ITEM, itemDto({ Mine: row }));
    page.respond('PUT', itemPath() + '/Mine/Review', { title: 'Conflict', status: 409, detail: 'changed' }, 409);
    page.respond('GET', itemPath(), itemDto({ Mine: Object.assign({}, row, { Text: 'Newer' }) }));
    section.querySelector('.jfur-own-edit').click();
    const area = section.querySelector('.jfur-review-form textarea');
    area.value = 'Mine';
    page.dispatch(area, 'input');
    section.querySelector('.jfur-review-save').click();
    await page.advance(100);
    const before = calls(page, 'GET', /\/Items\//).length;
    page.clientState.userId = 'user-2';
    section.querySelector('.jfur-conflict-reload').click();
    await page.advance(100);
    assert.equal(section.isConnected, false, 'the previous member\'s section went');
    const sections = page.document.querySelectorAll('.jfur-section');
    assert.ok(sections.every((s) => s !== section && !s.querySelector('.jfur-review-form')), 'only the next member\'s own section, if any');
    assert.equal(calls(page, 'GET', /\/Items\//).length - before, sections.length, 'Reload sent nothing: the one read since is the next member\'s own section\'s');
    assert.equal(page.document.querySelectorAll('.jfur-toast').length, 0, 'a read says nothing');

    // An answer that comes after the change is dropped (refreshDetails, as the page shown again calls it).
    const p2 = await start();
    const s2 = (await mounted(p2, ITEM, itemDto({ Mine: null, Can: { Rate: false, Review: false, Moderate: false, Reason: 'NotARater' } }))).section;
    p2.respond('GET', itemPath(), itemDto({ Mine: mine({ Stars: 5, Text: 'Another member\'s row' }) }));
    const held = hold(p2, 'GET', /\/UserRatings\/Items\//);
    p2.document.visibilityState = 'hidden';
    p2.dispatch(p2.document, 'visibilitychange');
    p2.document.visibilityState = 'visible';
    p2.dispatch(p2.document, 'visibilitychange');
    await p2.advance(10);
    assert.equal(held.held.length, 1);
    p2.clientState.userId = 'user-2';
    held.restore();
    await held.held[0].release();
    await p2.advance(100);
    assert.equal(s2.isConnected, false, 'never shown in the previous member\'s section');
    assert.equal(s2.textContent.includes('Another member'), false);
});

// ------------------------------------------------------------------------- the spoiler flag, Cancel and kept texts

test('"Contains spoilers" changed in another window makes this window\'s Save a 409, never a silent undo; no "(edited)"', { skip }, async () => {
    const page = await start();
    const row = mine({ Stars: 3, Text: 'Words', IsSpoiler: false, UpdatedUtc: '2026-10-21T08:00:00.000Z', ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' });
    const { section } = await mounted(page, ITEM, itemDto({ Mine: row }));
    const server = rowServer(page, row);
    section.querySelector('.jfur-own-edit').click();
    const form = section.querySelector('.jfur-review-form');
    server.other({ Text: 'Words', IsSpoiler: true });
    assert.equal(server.row.Edited, false, 'the flag is no edit');
    form.querySelector('.jfur-review-save').click();
    await page.advance(100);
    assert.deepEqual(plain(server.reviews.map((b) => [b.Text, !!b.IsSpoiler, b.IfReviewUpdatedUtc])), [['Words', false, '2026-10-21T08:00:00.000Z']]);
    assert.equal(server.row.IsSpoiler, true, 'the other window\'s flag stays');
    const reload = form.querySelector('.jfur-conflict-reload');
    assert.ok(reload, 'the 409, with Reload');
    reload.click();
    await page.advance(100);
    assert.equal(form.querySelector('input.jfur-review-spoiler').checked, true, 'Reload: the flag as saved there');
    assert.equal(form.querySelector('textarea').value, 'Words');
    form.querySelector('.jfur-review-save').click();
    await page.advance(100);
    assert.ok(section.querySelector('.jfur-review-form') === null, 'saved from the newer time');
    assert.equal(server.row.IsSpoiler, true);
    assert.ok(section.querySelector('.jfur-own .jfur-review-edited') === null, 'no "(edited)" on the own card');
});

test('Cancel where this browser can\'t keep a draft leaves the form open with the texts kept beside it; the second Cancel closes it', { skip }, async () => {
    const page = await start();
    const row = mine({ Stars: 3, Text: 'T0', UpdatedUtc: '2026-10-21T08:00:00.000Z', ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' });
    const { section } = await mounted(page, ITEM, itemDto({ Mine: row }));
    const server = rowServer(page, row);
    section.querySelector('.jfur-own-edit').click();
    const form = section.querySelector('.jfur-review-form');
    const area = form.querySelector('textarea');
    area.value = 'My words';
    page.dispatch(area, 'input');
    server.other({ Text: 'B saved' });
    form.querySelector('.jfur-review-save').click();
    await page.advance(100);
    form.querySelector('.jfur-conflict-reload').click();
    await page.advance(100);
    const store = page.context.localStorage;
    const setItem = store.setItem;
    store.setItem = () => {
        throw new Error('SecurityError');
    };
    form.querySelector('.jfur-review-cancel').click();
    assert.ok(section.querySelector('.jfur-review-form') === form, 'still open');
    assert.equal(area.value, 'B saved');
    assert.deepEqual(plain(form.querySelectorAll('.jfur-aside-text').map(text)), ['My words'], 'the kept text still beside it');
    assert.equal(text(form.querySelector('.jfur-review-error .jfur-notice-text')), CANCEL_NOT_KEPT);
    assert.ok(page.document.activeElement === area);
    form.querySelector('.jfur-review-cancel').click();
    assert.ok(section.querySelector('.jfur-review-form') === null, 'the member chose to close it');
    store.setItem = setItem;

    // Where the browser keeps it, one Cancel closes the form, as before.
    section.querySelector('.jfur-own-edit').click();
    const again = section.querySelector('.jfur-review-form');
    again.querySelector('textarea').value = 'Kept this time';
    page.dispatch(again.querySelector('textarea'), 'input');
    again.querySelector('.jfur-review-cancel').click();
    assert.ok(section.querySelector('.jfur-review-form') === null);
    assert.equal(storedDraft(page).text, 'Kept this time');
});

test('a Save that goes through after the page was left keeps the member\'s other unsaved texts in the draft', { skip }, async () => {
    const page = await start();
    const row = mine({ Stars: 3, Text: 'T0', UpdatedUtc: '2026-10-21T08:00:00.000Z', ReviewUpdatedUtc: '2026-10-21T08:00:00.000Z' });
    const { section, view } = await mounted(page, ITEM, itemDto({ Mine: row }));
    const server = rowServer(page, row);
    section.querySelector('.jfur-own-edit').click();
    const form = section.querySelector('.jfur-review-form');
    const area = form.querySelector('textarea');
    area.value = 'First';
    page.dispatch(area, 'input');
    server.other({ Text: 'B saved' });
    form.querySelector('.jfur-review-save').click();
    await page.advance(100);
    form.querySelector('.jfur-conflict-reload').click();
    await page.advance(100);
    area.value = 'Second';
    page.dispatch(area, 'input');
    const held = hold(page, 'PUT', /Mine\/Review$/);
    form.querySelector('.jfur-review-save').click();
    await page.advance(10);
    page.respond('GET', itemPath(OTHER), itemDto({ ItemId: OTHER }));
    showView(page, view, OTHER);
    await page.advance(400);
    held.restore();
    await held.held[0].release();
    await page.advance(300);
    assert.equal(server.row.Text, 'Second');
    const draft = storedDraft(page);
    assert.deepEqual(plain(draft.kept), [{ text: 'First', mine: true }], 'the other text waits for the next Edit');
    assert.deepEqual([draft.text, draft.base.ReviewUpdatedUtc], ['Second', server.row.ReviewUpdatedUtc], 'beside the review as saved');
});

// ------------------------------------------------------------------------------------ the chip and the Rate button

// Seven ratings averaging 4.2 (29.5 / 7), and with an eighth 5 the average 4.3 (34.5 / 8).
const SEVEN = [5, 5, 4, 4, 4, 4, 3.5];
const chipOf = (node) => (node ? node.querySelector('.jfur-chip') : null);
const rateOf = (node) => (node ? node.querySelector('.jfur-rate') : null);
const count = (page, selector) => page.document.querySelectorAll(selector).length;
const health = (page) => plain(page.context.__jfkit.jfur.instance.health.missing());

// jellyfin-web's ApiClient.getItem, counted: { calls, restore }.
function countGetItem(page) {
    const proto = Object.getPrototypeOf(page.context.ApiClient);
    const original = proto.getItem;
    const spy = { calls: [] };
    proto.getItem = function (userId, id) {
        spy.calls.push([userId, id]);
        return original.call(this, userId, id);
    };
    spy.restore = () => { proto.getItem = original; };
    return spy;
}

test('the chip: "Members 4.2/5 (7)" at the end of the media-info row, named "Member average 4.2 out of 5, 7 ratings" (its title too), the visible parts hidden from a screen reader; it follows the summary', { skip }, async () => {
    const page = await start();
    const { misc, section } = await mounted(page, ITEM, itemDto({ Summary: summary(SEVEN), Mine: mine({ Stars: 3 }) }));
    const chip = chipOf(misc);
    assert.ok(chip, 'the chip is in the row');
    assert.ok(misc.lastChild === chip, 'at the row\'s end, after jellyfin-web\'s items');
    assert.equal(chip.getAttribute('data-jfur-chip'), ITEM);
    const icon = chip.querySelector('.material-icons');
    assert.ok(icon && icon.classList.contains('group') && icon.getAttribute('aria-hidden') === 'true', 'the group glyph, hidden from a screen reader');
    assert.equal(text(chip.querySelector('.jfur-chip-word')), 'Members');
    assert.equal(text(chip.querySelector('.jfur-chip-value')), '4.2/5 (7)');
    assert.equal(chip.querySelector('.jfur-chip-word').getAttribute('aria-hidden'), 'true');
    assert.equal(chip.querySelector('.jfur-chip-value').getAttribute('aria-hidden'), 'true');
    const label = chip.querySelector('.jfur-chip-label');
    assert.ok(label.classList.contains('jfur-sr-only'));
    assert.equal(text(label), 'Member average 4.2 out of 5, 7 ratings');
    assert.equal(chip.getAttribute('title'), 'Member average 4.2 out of 5, 7 ratings', 'the title for a mouse; touch and TV read the visible scale');
    assert.ok(section.querySelector('.jfur-chip') === null, 'not in the section');

    page.respond('PUT', itemPath() + '/Mine/Rating', itemDto({ Summary: summary(SEVEN.concat([5])), Mine: mine({ Stars: 5 }) }));
    const s = slider(section);
    s.focus();
    page.key(s, 'End');
    page.key(s, 'Enter');
    await page.advance(300);
    assert.ok(chipOf(misc) === chip, 'the same chip, updated in place');
    assert.equal(text(chip.querySelector('.jfur-chip-value')), '4.3/5 (8)');
    assert.equal(chip.getAttribute('title'), 'Member average 4.3 out of 5, 8 ratings');
    assert.equal(count(page, '.jfur-chip'), 1);
    assert.equal(page.errors.length, 0);
});

test('no chip while ShowAverageChip is off, while there are no ratings, or fewer than MinRatingsForAverage; one rating says "1 rating"', { skip }, async () => {
    const off = await start({ capabilities: { Display: { ShowAverageChip: false } } });
    assert.ok(chipOf((await mounted(off, ITEM, itemDto({ Summary: summary(SEVEN) }))).misc) === null, 'ShowAverageChip off');
    const none = await start();
    assert.ok(chipOf((await mounted(none)).misc) === null, 'no ratings');
    const few = await start({ capabilities: { Display: { MinRatingsForAverage: 3 } } });
    const f = await mounted(few, ITEM, itemDto({ Summary: summary([4, 5]) }));
    assert.ok(chipOf(f.misc) === null, 'two ratings, three needed');
    assert.equal(f.section.querySelector('.jfur-summary').getAttribute('data-jfur-summary'), 'few', 'the section says "Not enough ratings yet"');
    const one = await start();
    const o = chipOf((await mounted(one, ITEM, itemDto({ Summary: summary([4]) }))).misc);
    assert.equal(text(o.querySelector('.jfur-chip-value')), '4.0/5 (1)');
    assert.equal(text(o.querySelector('.jfur-chip-label')), 'Member average 4.0 out of 5, 1 rating');
});

test('jellyfin-web writing the media-info row again (another version chosen) takes the chip, and the scanner puts the same chip back once, at the end', { skip }, async () => {
    const page = await start();
    const { misc } = await mounted(page, ITEM, itemDto({ Summary: summary(SEVEN) }));
    const chip = chipOf(misc);
    for (let i = 0; i < 3; i++) {
        misc.replaceChildren(page.element('div', { class: 'mediaInfoItem' }, ['2021']), page.element('div', { class: 'mediaInfoItem' }, ['2h 28m']));
        await page.advance(300);
        assert.ok(chipOf(misc) === chip, 'the same chip back, write ' + (i + 1));
        assert.ok(misc.lastChild === chip);
        assert.equal(misc.querySelectorAll('.jfur-chip').length, 1);
    }

    assert.equal(calls(page, 'GET', /UserRatings\/Items\/[^/]+$/).length, 1, 'no read of ours for it');
});

test('on a Season page the empty, hidden row is shown while the chip is in it and hidden again as it goes; a row jellyfin-web filled meanwhile stays shown', { skip }, async () => {
    const page = await start();
    const { view, misc } = await mounted(page, ITEM, itemDto({ Summary: summary(SEVEN) }), { misc: 'empty' });
    assert.ok(chipOf(misc));
    assert.equal(shown(chipOf(misc)), true, 'the chip shows: the row is un-hidden');
    page.respond('GET', itemPath(OTHER), itemDto({ ItemId: OTHER }));
    showView(page, view, OTHER);
    await page.advance(400);
    assert.ok(chipOf(misc) === null, 'no average on the next item: no chip');
    assert.equal(misc.classList.contains('hide'), true, 'hidden again, as jellyfin-web left it');
    assert.equal(misc.hasAttribute('data-jfur-unhid'), false);

    const page2 = await start();
    const second = await mounted(page2, ITEM, itemDto({ Summary: summary(SEVEN) }), { misc: 'empty' });
    second.misc.append(page2.element('div', { class: 'mediaInfoItem' }, ['2h 28m']));
    page2.respond('GET', itemPath(OTHER), itemDto({ ItemId: OTHER }));
    showView(page2, second.view, OTHER);
    await page2.advance(400);
    assert.ok(chipOf(second.misc) === null);
    assert.equal(second.misc.classList.contains('hide'), false, 'the row holds jellyfin-web\'s item: never hidden by us');
});

test('the Rate button, ShowDetailButton Auto: on Series, Season and MusicAlbum pages, before More, the type read once through jellyfin-web\'s getItem; not on a Movie or an Episode', { skip }, async () => {
    for (const [type, wanted] of [['Series', true], ['Season', true], ['MusicAlbum', true], ['Movie', false], ['Episode', false]]) {
        const page = await start({ items: { [ITEM]: { Id: ITEM, Type: type } } });
        const spy = countGetItem(page);
        const { buttons, more, section } = await mounted(page, ITEM, itemDto({ Summary: summary(SEVEN) }));
        for (let i = 0; i < 3; i++) {
            buttons.append(page.element('span', { class: 'spacer' }));
            await page.advance(300);
        }

        const rate = rateOf(buttons);
        assert.equal(!!rate, wanted, type);
        assert.deepEqual(spy.calls, [['user-1', ITEM]], type + ': one read of the item, by jellyfin-web\'s ApiClient');
        if (wanted) {
            assert.ok(rate.nextSibling === more, type + ': before More');
            assert.equal(rate.getAttribute('title'), 'Rate');
            assert.equal(rate.getAttribute('aria-label'), 'Rate');
            assert.equal(rate.getAttribute('type'), 'button');
            assert.ok(rate.querySelector('.material-icons.star'));
            assert.ok(section.querySelector('.jfur-rate') === null);
        }

        spy.restore();
        assert.equal(page.errors.length, 0);
    }

    const unknown = await start();
    const u = await mounted(unknown, ITEM, itemDto());
    assert.ok(rateOf(u.buttons) === null, 'a type jellyfin-web can\'t read: Auto shows none');
    const page = await start({ items: { [ITEM]: { Id: ITEM, Type: 'Series' } } });
    page.respond('GET', itemPath(), itemDto());
    const v = detailsView(page, ITEM, {});
    v.more.remove();
    await page.advance(400);
    assert.ok(v.buttons.lastChild === rateOf(v.buttons), 'no More: at the row\'s end');
});

test('Auto also where Jellyfin Enhanced\'s Reviews are, drawn late; Always on every page; Never on none, with JE\'s Reviews too', { skip }, async () => {
    const page = await start({ items: { [ITEM]: { Id: ITEM, Type: 'Movie' } } });
    const { buttons, view } = await mounted(page, ITEM, itemDto());
    assert.ok(rateOf(buttons) === null, 'a Movie without JE\'s Reviews');
    const primary = view.querySelector('.detailPagePrimaryContent');
    const reviews = jeReviewsSection(page);
    primary.append(reviews);
    await page.advance(300);
    assert.ok(rateOf(buttons), 'JE drew its Reviews: the Rate button');
    reviews.remove();
    await page.advance(300);
    assert.ok(rateOf(buttons) === null, 'gone with them');

    const always = await start({ capabilities: { Display: { ShowDetailButton: 'always' } }, items: { [ITEM]: { Id: ITEM, Type: 'Movie' } } });
    const spy = countGetItem(always);
    assert.ok(rateOf((await mounted(always)).buttons), 'Always (any case): on a Movie');
    assert.equal(spy.calls.length, 0, 'Always needs no type');

    const never = await start({ capabilities: { Display: { ShowDetailButton: 'Never' } }, items: { [ITEM]: { Id: ITEM, Type: 'Series' } } });
    assert.ok(rateOf((await mounted(never, ITEM, itemDto(), { jeReviews: true })).buttons) === null, 'Never: not on a Series with JE\'s Reviews');

    const odd = await start({ capabilities: { Display: { ShowDetailButton: 'Sometimes' } }, items: { [ITEM]: { Id: ITEM, Type: 'Season' } } });
    assert.ok(rateOf((await mounted(odd)).buttons), 'an unknown name is Auto');
});

test('Rate expands a collapsed section, focuses the slider and scrolls it to the middle; for a member who can\'t rate, the read-only stars', { skip }, async () => {
    const page = await start({ capabilities: { Display: { SectionExpandedByDefault: false, ShowDetailButton: 'Always' } } });
    const { buttons, section } = await mounted(page, ITEM, itemDto({ Mine: mine({ Stars: 3 }) }));
    const toggle = section.querySelector('.jfur-section-toggle');
    assert.equal(toggle.getAttribute('aria-expanded'), 'false');
    page.document._scrolled.length = 0;
    rateOf(buttons).click();
    assert.equal(toggle.getAttribute('aria-expanded'), 'true', 'expanded');
    assert.equal(shown(section.querySelector('.jfur-section-body')), true);
    assert.ok(page.document.activeElement === slider(section), 'the slider has the focus');
    assert.equal(page.document._scrolled.length, 1);
    assert.ok(page.document._scrolled[0].element === slider(section));
    assert.deepEqual(plain(page.document._scrolled[0].options), { block: 'center' });
    rateOf(buttons).click();
    assert.equal(toggle.getAttribute('aria-expanded'), 'true', 'pressed again: still open');

    const ro = await start({ capabilities: { Display: { ShowDetailButton: 'Always' } } });
    const r = await mounted(ro, ITEM, itemDto({ Mine: mine({ Stars: 3 }), Can: { Rate: false, Review: false, Moderate: false, Reason: 'NotARater' } }));
    rateOf(r.buttons).click();
    assert.ok(ro.document.activeElement === r.section.querySelector('.jfur-mine-readonly'), 'NotARater: the read-only stars in the slider\'s place');
    const np = await start({ capabilities: { Display: { ShowDetailButton: 'Always' } } });
    const n = await mounted(np, ITEM, itemDto({ Can: { Rate: false, Review: false, Moderate: false, Reason: 'NeedsPlay' } }));
    rateOf(n.buttons).click();
    assert.ok(np.document.activeElement === slider(n.section), 'NeedsPlay: the disabled slider, with its note');
});

test('a page without the buttons row: no Rate button, and the health check reports detailButtons missing once, no other anchor', { skip }, async () => {
    const page = await start({ capabilities: { Display: { ShowDetailButton: 'Always' } } });
    const { view, section } = await mounted(page, ITEM, itemDto(), { buttons: false });
    assert.ok(section, 'the section is there');
    await page.advance(5000);
    assert.equal(count(page, '.jfur-rate'), 0);
    assert.deepEqual(health(page).map((m) => m.anchor + '|' + m.view + '|' + m.count), ['detailButtons|details|1']);
    showView(page, view, ITEM);
    await page.advance(5000);
    assert.deepEqual(health(page).map((m) => m.anchor + '|' + m.count), ['detailButtons|1'], 'the same visit: reported once');

    const noRow = await start();
    await mounted(noRow, ITEM, itemDto({ Summary: summary(SEVEN) }), { misc: 'none' });
    await noRow.advance(5000);
    assert.equal(count(noRow, '.jfur-chip'), 0);
    assert.deepEqual(health(noRow).map((m) => m.anchor), ['miscInfo'], 'no media-info row: no chip, miscInfo reported');

    const full = await start();
    await mounted(full, ITEM, itemDto({ Summary: summary(SEVEN) }));
    await full.advance(5000);
    assert.deepEqual(health(full), [], 'every anchor there: nothing reported');
});

test('item to item: one chip and one Rate button, for the item shown; none once the section is gone (a 404) or the page left', { skip }, async () => {
    const page = await start({ capabilities: { Display: { ShowDetailButton: 'Always' } } });
    const { view, misc, buttons } = await mounted(page, ITEM, itemDto({ Summary: summary(SEVEN) }));
    page.respond('GET', itemPath(OTHER), itemDto({ ItemId: OTHER, Summary: summary([2, 3]) }));
    for (let i = 0; i < 20; i++) {
        const id = i % 2 ? ITEM : OTHER;
        showView(page, view, id);
        await page.advance(400);
        assert.equal(count(page, '.jfur-chip'), 1, 'move ' + i);
        assert.equal(count(page, '.jfur-rate'), 1, 'move ' + i);
        assert.equal(chipOf(misc).getAttribute('data-jfur-chip'), id);
        assert.equal(text(chipOf(misc).querySelector('.jfur-chip-value')), id === ITEM ? '4.2/5 (7)' : '2.5/5 (2)');
    }

    page.respond('GET', itemPath(OTHER), { title: 'Not Found', status: 404, detail: NOT_FOUND }, 404);
    showView(page, view, OTHER);
    await page.advance(400);
    assert.ok(page.document.querySelector('.jfur-section') === null, 'the 404: no section');
    assert.equal(count(page, '.jfur-chip'), 0, 'and no chip');
    assert.ok(rateOf(buttons) === null, 'and no Rate button');

    showView(page, view, ITEM);
    await page.advance(400);
    assert.equal(count(page, '.jfur-chip'), 1);
    view.classList.add('hide');
    const home = page.element('div', { id: 'indexPage', class: 'page homePage' });
    page.document.body.append(home);
    page.context.location.hash = '#/home';
    page.dispatch(home, 'viewshow', { detail: { isRestored: false } });
    await page.advance(400);
    assert.equal(count(page, '.jfur-chip') + count(page, '.jfur-rate'), 0, 'another view: none');
    assert.equal(page.errors.length, 0);
});

test('a 404 or a 403 that comes after the section was shown takes the chip and the Rate button with the section; none is left behind, nor put back by the next scans', { skip }, async () => {
    // A rating answered 404: the section goes, saved or not.
    const page = await start({ capabilities: { Display: { ShowDetailButton: 'Always' } } });
    const { misc, buttons, section } = await mounted(page, ITEM, itemDto({ Summary: summary(SEVEN) }));
    assert.ok(chipOf(misc) && rateOf(buttons), 'the chip and the Rate button beside the shown section');
    page.respond('PUT', itemPath() + '/Mine/Rating', { title: 'Not Found', status: 404, detail: NOT_FOUND }, 404);
    page.key(slider(section), 'End');
    page.key(slider(section), 'Enter');
    await page.advance(400);
    assert.equal(section.isConnected, false, 'the 404: the section gone');
    assert.equal(count(page, '.jfur-chip'), 0, 'no chip left');
    assert.equal(count(page, '.jfur-rate'), 0, 'no Rate button left');
    misc.replaceChildren(page.element('div', { class: 'mediaInfoItem' }, ['2021']));
    buttons.append(page.element('span', { class: 'spacer' }));
    await page.advance(400);
    assert.equal(count(page, '.jfur-chip') + count(page, '.jfur-rate'), 0, 'nor after the next scans');

    // A rating answered 403, and the item's read again 403 too (ratings turned off since the page read it).
    const off = await start({ capabilities: { Display: { ShowDetailButton: 'Always' } } });
    const o = await mounted(off, ITEM, itemDto({ Summary: summary(SEVEN) }));
    assert.ok(chipOf(o.misc) && rateOf(o.buttons));
    off.respond('PUT', itemPath() + '/Mine/Rating', { title: 'Forbidden', status: 403, detail: 'Ratings are turned off on this server.', type: 'urn:jellyfin-plugin-kit:feature-disabled' }, 403);
    off.respond('GET', itemPath(), { title: 'Forbidden', status: 403, detail: 'Ratings are turned off on this server.' }, 403);
    off.key(slider(o.section), 'End');
    off.key(slider(o.section), 'Enter');
    await off.advance(400);
    assert.equal(o.section.isConnected, false, 'the read\'s 403: the section gone');
    assert.equal(count(off, '.jfur-chip'), 0, 'no chip left');
    assert.equal(count(off, '.jfur-rate'), 0, 'no Rate button left');
    o.misc.replaceChildren(off.element('div', { class: 'mediaInfoItem' }, ['2021']));
    await off.advance(400);
    assert.equal(count(off, '.jfur-chip') + count(off, '.jfur-rate'), 0, 'nor after the next scans');
    assert.equal(page.errors.length + off.errors.length, 0);
});

test('a restored view reading its item again keeps the chip it showed, the same node saying the same, until the answer: a 200 updates it or takes it away, a 503 and its Retry keep it, a 404 or another item removes it', { skip }, async () => {
    const page = await start();
    const { view, misc } = await mounted(page, ITEM, itemDto({ Summary: summary(SEVEN) }));
    const chip = chipOf(misc);
    const reads = () => calls(page, 'GET', /UserRatings\/Items\/[^/]+$/).length;
    const held = hold(page, 'GET', /\/UserRatings\/Items\/[^/]+$/);
    showView(page, view, ITEM, true);
    await page.advance(400);
    assert.equal(held.held.length, 1, 'read again, the answer held');
    assert.ok(chipOf(misc) === chip, 'the same chip while it is read');
    assert.equal(text(chip.querySelector('.jfur-chip-value')), '4.2/5 (7)', 'saying what it said');
    misc.replaceChildren(page.element('div', { class: 'mediaInfoItem' }, ['2021']));
    await page.advance(300);
    assert.ok(chipOf(misc) === chip && misc.lastChild === chip, 'put back once jellyfin-web wrote the row again');
    assert.equal(count(page, '.jfur-chip'), 1);
    page.respond('GET', itemPath(), itemDto({ Summary: summary(SEVEN.concat([5])) }));
    held.restore();
    held.held[0].release();
    await page.advance(400);
    assert.ok(chipOf(misc) === chip, 'the same node, now the new section\'s');
    assert.equal(text(chip.querySelector('.jfur-chip-value')), '4.3/5 (8)', 'what the server says now');
    assert.equal(chip.getAttribute('title'), 'Member average 4.3 out of 5, 8 ratings');

    // A read that fails for a reason of its own: the section's Retry, and the chip as it was; Retry's answer updates it.
    page.respond('GET', itemPath(), { title: 'x', status: 503, detail: BUSY }, 503);
    showView(page, view, ITEM, true);
    await page.advance(400);
    const section = page.document.querySelector('.jfur-section');
    assert.ok(section && section.querySelector('.jfur-load-error [role="alert"]'), 'the load error with Retry');
    assert.ok(chipOf(misc) === chip, 'the chip kept through the load error');
    assert.equal(text(chip.querySelector('.jfur-chip-value')), '4.3/5 (8)');
    page.respond('GET', itemPath(), itemDto({ Summary: summary([1]) }));
    section.querySelector('.jfur-load-error button').click();
    await page.advance(400);
    assert.ok(chipOf(misc) === chip, 'Retry: still the same chip');
    assert.equal(text(chip.querySelector('.jfur-chip-value')), '1.0/5 (1)', 'Retry\'s answer');

    // An answer without an average takes it away; a 404 removes it.
    page.respond('GET', itemPath(), itemDto());
    showView(page, view, ITEM, true);
    await page.advance(400);
    assert.equal(count(page, '.jfur-chip'), 0, 'no average now: no chip');
    page.respond('GET', itemPath(), itemDto({ Summary: summary(SEVEN) }));
    showView(page, view, ITEM, true);
    await page.advance(400);
    assert.ok(chipOf(misc));
    page.respond('GET', itemPath(), { title: 'Not Found', status: 404, detail: NOT_FOUND }, 404);
    showView(page, view, ITEM, true);
    await page.advance(400);
    assert.equal(count(page, '.jfur-chip'), 0, 'the 404: no chip');
    assert.ok(page.document.querySelector('.jfur-section') === null);

    // Another item while the restored one is read: the kept chip goes, and the late answer brings nothing back.
    page.respond('GET', itemPath(), itemDto({ Summary: summary(SEVEN) }));
    showView(page, view, ITEM, true);
    await page.advance(400);
    assert.ok(chipOf(misc));
    const late = hold(page, 'GET', /\/UserRatings\/Items\/[^/]+$/);
    showView(page, view, ITEM, true);
    await page.advance(400);
    assert.ok(chipOf(misc), 'kept while read');
    late.restore();
    page.respond('GET', itemPath(OTHER), itemDto({ ItemId: OTHER }));
    showView(page, view, OTHER);
    await page.advance(400);
    assert.equal(count(page, '.jfur-chip'), 0, 'another item without an average: no chip');
    late.held[0].release();
    await page.advance(400);
    assert.equal(count(page, '.jfur-chip'), 0, 'the left item\'s late answer: nothing');
    assert.ok(reads() >= 7);
    assert.equal(page.errors.length, 0);
});

test('the TV layout: the controls of Jellyfin Enhanced\'s closed Reviews leave the focus order (the kit\'s tvSectionJump) and get their own back once open; no button of the jump\'s, the Rate button the one way in', { skip }, async () => {
    const page = await start({ layout: 'tv', items: { [ITEM]: { Id: ITEM, Type: 'Movie' } } });
    const { reviews, buttons } = await mounted(page, ITEM, itemDto(), { jeReviews: true });
    const write = reviews.querySelector('.je-review-write-btn');
    assert.equal(write.getAttribute('tabindex'), '-1', 'out of jellyfin-web\'s focus order while closed');
    assert.equal(reviews.querySelector('summary').getAttribute('tabindex'), null, 'its title untouched');
    assert.equal(count(page, '.jfur-je-jump'), 0, 'no jump button: the Rate button is there');
    assert.ok(rateOf(buttons), 'Auto with JE\'s Reviews: the Rate button');
    reviews.setAttribute('open', '');
    page.dispatch(reviews, 'toggle');
    await page.advance(300);
    assert.equal(write.getAttribute('tabindex'), null, 'open: its own tabindex (none) back');

    const desk = await start({ items: { [ITEM]: { Id: ITEM, Type: 'Movie' } } });
    const d = await mounted(desk, ITEM, itemDto(), { jeReviews: true });
    assert.equal(d.reviews.querySelector('.je-review-write-btn').getAttribute('tabindex'), null, 'not on the desktop layout');
});

test('the chip and the Rate button stay where they are once in their rows (another plugin may add after them), and one of ours left anywhere else is removed', { skip }, async () => {
    const page = await start({ capabilities: { Display: { ShowDetailButton: 'Always' } } });
    const { misc, buttons, more, view } = await mounted(page, ITEM, itemDto({ Summary: summary(SEVEN) }));
    const chip = chipOf(misc);
    const rate = rateOf(buttons);
    const after = page.element('div', { class: 'mediaInfoItem other-plugin' }, ['after']);
    misc.append(after);
    const extra = page.element('button', { class: 'other-plugin-button' });
    buttons.insertBefore(extra, more);
    await page.advance(300);
    assert.ok(chip.nextSibling === after, 'the chip stays before what was added after it');
    assert.ok(rate.nextSibling === extra, 'the Rate button stays where it was');
    assert.ok(chipOf(misc) === chip && rateOf(buttons) === rate);

    const strayChip = page.element('div', { class: 'jfur-chip' });
    const strayRate = page.element('button', { class: 'jfur-rate' });
    view.querySelector('.detailPageContent').append(strayChip, strayRate);
    await page.advance(300);
    assert.equal(strayChip.isConnected, false, 'a chip that isn\'t the shown item\'s');
    assert.equal(strayRate.isConnected, false, 'a Rate button that isn\'t the shown item\'s');
    assert.equal(count(page, '.jfur-chip'), 1);
    assert.equal(count(page, '.jfur-rate'), 1);
});

// ----------------------------------------------------------------------------------------------------- moderation

const ADMIN_CAN = { Rate: true, Review: true, Moderate: true };
const CHANGED = 'Changed: this rating changed since the dry run, so nothing was removed. Run the dry run again to see what it removes now.';

// The admin moderation route as the server answers it: a dry run unless dryRun=false, whose token stands for the
// row and the removal; the real run takes it. server.stale makes the next real run the 409 Changed (and moves the row on);
// server.status answers every call with that status instead.
function moderationServer(page, opts) {
    opts = opts || {};
    const server = { version: 1, stale: false, status: null, conflict: null, removed: [] };
    const token = (textOnly) => 'm1-' + (textOnly ? 'aaaa' : 'bbbb') + String(server.version).padStart(16, '0');
    page.respond('DELETE', /^\/UserRatings\/Admin\/Reviews\/[^/]+$/, (e) => {
        if (server.conflict && e.query.dryRun === 'false') {
            const conflict = server.conflict;
            server.conflict = null;
            return Object.assign({ title: 'Conflict', status: 409 }, conflict);
        }

        if (server.status) {
            return { title: 'x', status: server.status, detail: server.status === 404 ? 'This rating wasn\'t found, or you can\'t moderate it.' : 'This rating has no review to remove.' };
        }

        const textOnly = e.query.textOnly !== 'false';
        if (e.query.dryRun === 'false') {
            if (server.stale) {
                server.stale = false;
                server.version++;
                return { title: 'Conflict', status: 409, detail: CHANGED, code: 'Changed' };
            }

            server.removed.push({ id: e.path.split('/').pop(), textOnly, reason: e.query.reason || null, token: e.query.ifPreview });
            return { DryRun: false, Action: textOnly ? 'RemoveText' : 'RemoveEntry', PreviewToken: e.query.ifPreview, LogId: server.removed.length };
        }

        return { DryRun: true, Action: textOnly ? 'RemoveText' : 'RemoveEntry', RemovesStars: !textOnly, RemovesReview: true, DeletesRating: !textOnly || !!opts.noStars, ArchiveLines: false, PreviewToken: token(textOnly) };
    }, (e) => (server.conflict && e.query.dryRun === 'false' ? 409 : server.status ? server.status : (e.query.dryRun === 'false' && server.stale ? 409 : 200)));
    return server;
}

test('an admin (Can.Moderate) sees "Remove…" on the other members\' rows, never on their own; a member never; the chip and the Rate button as they were, in every layout', { skip }, async () => {
    for (const layout of [null, 'experimental', 'tv', 'je']) {
        const name = layout || 'default';
        const page = await start({ layout: layout === 'je' ? null : layout, capabilities: { Display: { ShowDetailButton: 'Always' } }, items: { [ITEM]: { Id: ITEM, Type: 'Movie' } } });
        const reviews = { Items: [review(1), review(2, { UserId: 'user-1', UserName: 'me' }), review(3, { UserName: 'שלום Name‮' })], TotalRecordCount: 3, StartIndex: 0 };
        const { section, misc, buttons } = await mounted(page, ITEM, itemDto({ Can: ADMIN_CAN, Summary: summary(SEVEN), Reviews: reviews }), { jeReviews: layout === 'je' });
        const removes = section.querySelectorAll('.jfur-review-remove');
        assert.deepEqual(plain(removes.map((b) => b.closest('.jfur-review').getAttribute('data-jfur-review'))), ['r1', 'r3'], name + ': not on the admin\'s own row');
        assert.deepEqual(plain(removes.map((b) => b.textContent)), ['Remove…', 'Remove…']);
        assert.equal(removes[0].getAttribute('aria-label'), 'Remove the review of member 1…');
        assert.equal(removes[0].tagName, 'BUTTON');
        assert.ok(chipOf(misc), name + ': the chip');
        assert.ok(rateOf(buttons), name + ': the Rate button');
        rateOf(buttons).click();
        assert.ok(page.document.activeElement === slider(section), name + ': Rate still focuses the slider');
        assert.equal(calls(page, 'DELETE', /./).length, 0, 'nothing sent by showing it');

        const member = await start({ layout: layout === 'je' ? null : layout });
        const m = await mounted(member, ITEM, itemDto({ Reviews: reviews }), { jeReviews: layout === 'je' });
        assert.equal(m.section.querySelectorAll('.jfur-review').length, 3);
        assert.equal(count(member, '.jfur-review-remove'), 0, name + ': a member never');
        assert.equal(page.errors.length + member.errors.length, 0);
    }
});

test('Remove…: the dialog checks first (the dry run), "Remove text only" chosen, Cancel focused; the other choice checks again; Remove sends the token and the reason, then the list is read again and the focus goes to the row in its place', { skip }, async () => {
    const page = await start();
    const reviews = { Items: [review(1, { Stars: 4 }), review(2)], TotalRecordCount: 2, StartIndex: 0 };
    const { section } = await mounted(page, ITEM, itemDto({ Can: ADMIN_CAN, Reviews: reviews }));
    const server = moderationServer(page);
    const remove = section.querySelector('[data-jfur-review="r1"] .jfur-review-remove');
    remove.focus();
    remove.click();
    await page.advance(50);
    const dialog = page.document.querySelector('.jfur-panel');
    assert.equal(text(dialog.querySelector('h3')), 'Remove a review');
    assert.equal(text(dialog.querySelector('.jfur-moderate-by')), 'By member 1');
    assert.equal(dialog.querySelector('.jfur-moderate-name').getAttribute('dir'), 'auto', 'the name isolated');
    const radios = dialog.querySelectorAll('.jfur-moderate-radio');
    assert.deepEqual(plain(radios.map((r) => r.checked)), [true, false], 'Remove text only is the default');
    assert.deepEqual(plain(dialog.querySelectorAll('.jfur-moderate-choice-label').map((l) => l.textContent)), ['Remove text only', 'Remove rating and review']);
    assert.deepEqual(plain(dialog.querySelectorAll('.jfur-moderate-reason option').map((o) => o.value)), ['', 'Spoiler', 'Offensive', 'Spam', 'OffTopic', 'Requested', 'Other']);
    assert.ok(page.document.activeElement === dialog.querySelector('.jfur-dialog-cancel'), 'Cancel focused');
    let sent = calls(page, 'DELETE', /Admin\/Reviews\/r1$/);
    assert.equal(sent.length, 1);
    assert.deepEqual(plain(sent[0].query), { textOnly: 'true' }, 'a dry run: no dryRun, no token');
    assert.equal(text(dialog.querySelector('.jfur-moderate-preview')), 'The review goes; the stars stay (4 out of 5 stars). The moderation log keeps who removed it and why, never the text.');
    const confirm = dialog.querySelector('.jfur-moderate-confirm');
    assert.equal(confirm.disabled, false);

    radios[1].checked = true;
    page.dispatch(radios[1], 'change');
    assert.equal(confirm.disabled, true, 'Remove waits for the new check');
    confirm.click();
    await page.advance(50);
    assert.ok(calls(page, 'DELETE', /Admin\/Reviews\/r1$/).every((c) => plain(c.query).dryRun === undefined), 'a click while it waits removes nothing');
    sent = calls(page, 'DELETE', /Admin\/Reviews\/r1$/);
    assert.deepEqual(plain(sent[1].query), { textOnly: 'false' });
    assert.equal(text(dialog.querySelector('.jfur-moderate-preview')), 'The rating and the review go. The moderation log keeps who removed it and why, never the text.');

    dialog.querySelector('.jfur-moderate-reason').value = 'Offensive';
    page.respond('GET', itemPath(), itemDto({ Can: ADMIN_CAN, Reviews: { Items: [review(2)], TotalRecordCount: 1, StartIndex: 0 } }));
    page.respond('GET', itemPath() + '/Reviews', { Items: [review(2)], TotalRecordCount: 1, StartIndex: 0 });
    confirm.click();
    await page.advance(200);
    assert.deepEqual(server.removed, [{ id: 'r1', textOnly: false, reason: 'Offensive', token: 'm1-bbbb0000000000000001' }]);
    assert.ok(page.document.querySelector('.jfur-panel') === null, 'the dialog closed');
    assert.ok(calls(page, 'GET', /\/Reviews$/).length >= 1, 'the list read again');
    assert.deepEqual(plain(section.querySelectorAll('.jfur-review').map((r) => r.getAttribute('data-jfur-review'))), ['r2']);
    assert.ok(page.document.activeElement === section.querySelector('[data-jfur-review="r2"] .jfur-review-remove'), 'the focus on the row in its place');
    assert.deepEqual(plain(page.document.querySelectorAll('.jfur-toast').map((t) => t.textContent)), ['The rating and the review were removed.']);
    await page.advance(100);
    assert.equal(text(section.querySelector('.jfur-section-live')), 'The rating and the review were removed.');
    assert.equal(page.errors.length, 0);
});

test('Remove…: a stale token\'s 409 removes nothing, says so, reads the row again and shows its stars and words as they are now, and only then checks again, Cancel focused; a 404 says the review is gone; Cancel sends nothing and puts the focus back on Remove…', { skip }, async () => {
    const page = await start();
    const reviews = { Items: [review(1, { Stars: null })], TotalRecordCount: 1, StartIndex: 0 };
    const { section } = await mounted(page, ITEM, itemDto({ Can: ADMIN_CAN, Reviews: reviews }));
    const server = moderationServer(page, { noStars: true });
    // The row as moderation reads it after the 409: stars given and other words meanwhile.
    page.respond('GET', '/UserRatings/Admin/Reviews', (e) => (e.query.userId === 'u1' && e.query.itemId === ITEM
        ? { Items: [{ RatingId: 'r1', UserId: 'u1', ItemId: ITEM, Stars: 2, Text: 'Edited: <b>new</b> words', IsSpoiler: false }], TotalRecordCount: 1, StartIndex: 0 }
        : { Items: [], TotalRecordCount: 0, StartIndex: 0 }));
    const remove = section.querySelector('.jfur-review-remove');
    remove.focus();
    remove.click();
    await page.advance(50);
    let dialog = page.document.querySelector('.jfur-panel');
    assert.equal(text(dialog.querySelector('.jfur-moderate-preview')), 'The review goes, and with it the rating, which has no stars. The moderation log keeps who removed it and why, never the text.');
    server.stale = true;
    dialog.querySelector('.jfur-moderate-confirm').click();
    await page.advance(100);
    assert.equal(server.removed.length, 0, 'nothing removed');
    assert.ok(page.document.querySelector('.jfur-panel') === dialog, 'the dialog stays');
    assert.equal(text(dialog.querySelector('.jfur-moderate-problem')), 'This review changed after it was checked, so nothing was removed. It is shown below as it is now; Remove removes what is checked for it now.');
    const dry = calls(page, 'DELETE', /Admin\/Reviews\/r1$/).filter((e) => e.query.dryRun === undefined);
    assert.equal(dry.length, 2, 'checked again');
    const read = calls(page, 'GET', /Admin\/Reviews$/);
    assert.equal(read.length, 1, 'the row read again');
    assert.ok(page.fetchLog.indexOf(read[0]) < page.fetchLog.indexOf(dry[1]), 'read before the new check');
    assert.equal(text(dialog.querySelector('.jfur-moderate-now-stars')), 'Now: 2 out of 5 stars');
    const words = dialog.querySelector('.jfur-moderate-now-text');
    assert.equal(text(words), 'Edited: <b>new</b> words');
    assert.equal(words.getAttribute('dir'), 'auto');
    assert.equal(dialog.querySelectorAll('.jfur-moderate-now b').length, 0, 'the words are text');
    assert.ok(page.document.activeElement === dialog.querySelector('.jfur-dialog-cancel'), 'Cancel focused');
    page.respond('GET', itemPath() + '/Reviews', { Items: [], TotalRecordCount: 0, StartIndex: 0 });
    dialog.querySelector('.jfur-moderate-confirm').click();
    await page.advance(100);
    assert.deepEqual(server.removed.map((r) => r.token), ['m1-aaaa0000000000000002'], 'the new check\'s token');

    // A 404, then Cancel.
    const again = await start();
    const a = await mounted(again, ITEM, itemDto({ Can: ADMIN_CAN, Reviews: reviews }));
    const gone = moderationServer(again);
    gone.status = 404;
    const button = a.section.querySelector('.jfur-review-remove');
    button.focus();
    button.click();
    await again.advance(50);
    dialog = again.document.querySelector('.jfur-panel');
    assert.equal(text(dialog.querySelector('.jfur-moderate-problem')), 'This review was already removed, or you can\'t remove it.');
    assert.equal(dialog.querySelector('.jfur-moderate-confirm').disabled, true);
    dialog.querySelector('.jfur-dialog-cancel').click();
    await again.advance(50);
    assert.ok(again.document.querySelector('.jfur-panel') === null);
    assert.ok(again.document.activeElement === button, 'back on Remove…');
    assert.equal(calls(again, 'DELETE', /./).filter((e) => e.query.dryRun === 'false').length, 0, 'no removal sent');
    assert.equal(page.errors.length + again.errors.length, 0);
});

test('Remove…: the 409 is told apart by its code, never by its words: code Changed reads the row again whatever the words, and a 409 whose words start "Changed" with another code is no reload', { skip }, async () => {
    // The dialog once matched the message against /^Changed/.
    for (const [conflict, reloads] of [[{ detail: 'Some other words.', code: 'Changed' }, true], [{ detail: CHANGED, code: 'NoReview' }, false]]) {
        const page = await start();
        const reviews = { Items: [review(1, { Stars: 4 })], TotalRecordCount: 1, StartIndex: 0 };
        const { section } = await mounted(page, ITEM, itemDto({ Can: ADMIN_CAN, Reviews: reviews }));
        const server = moderationServer(page);
        page.respond('GET', '/UserRatings/Admin/Reviews', (e) => (e.query.userId === 'u1' && e.query.itemId === ITEM
            ? { Items: [{ RatingId: 'r1', UserId: 'u1', ItemId: ITEM, Stars: 3, Text: 'Now these words', IsSpoiler: false }], TotalRecordCount: 1, StartIndex: 0 }
            : { Items: [], TotalRecordCount: 0, StartIndex: 0 }));
        section.querySelector('.jfur-review-remove').click();
        await page.advance(50);
        const dialog = page.document.querySelector('.jfur-panel');
        server.conflict = conflict;
        dialog.querySelector('.jfur-moderate-confirm').click();
        await page.advance(100);
        assert.equal(server.removed.length, 0);
        assert.equal(calls(page, 'GET', /Admin\/Reviews$/).length, reloads ? 1 : 0, conflict.code + ': the row read again or not');
        if (reloads) {
            assert.equal(text(dialog.querySelector('.jfur-moderate-now-stars')), 'Now: 3 out of 5 stars');
        } else {
            assert.notEqual(text(dialog.querySelector('.jfur-moderate-problem')), 'This review changed after it was checked, so nothing was removed. It is shown below as it is now; Remove removes what is checked for it now.');
        }

        assert.equal(page.errors.length, 0);
    }
});

test('Remove… on the TV layout: the D-pad moves through the dialog (no trap), Back closes it, and the focus is back on Remove…', { skip }, async () => {
    const page = await start({ layout: 'tv' });
    const { section } = await mounted(page, ITEM, itemDto({ Can: ADMIN_CAN, Reviews: { Items: [review(1)], TotalRecordCount: 1, StartIndex: 0 } }));
    moderationServer(page);
    const remove = section.querySelector('.jfur-review-remove');
    remove.focus();
    remove.click();
    await page.advance(50);
    const dialog = page.document.querySelector('.jfur-panel');
    const cancel = dialog.querySelector('.jfur-dialog-cancel');
    assert.ok(page.document.activeElement === cancel);
    page.key(cancel, 'ArrowDown');
    assert.ok(page.document.activeElement === dialog.querySelector('.jfur-moderate-confirm'), 'down: Remove');
    page.key(page.document.activeElement, 'ArrowUp');
    page.key(page.document.activeElement, 'ArrowUp');
    assert.ok(page.document.activeElement === dialog.querySelector('.jfur-moderate-reason'), 'up twice: the reason');
    page.key(page.document.activeElement, 'ArrowUp');
    assert.ok(page.document.activeElement === dialog.querySelectorAll('.jfur-moderate-radio')[1], 'up: the second choice');
    page.key(page.document.activeElement, 'GoBack', { keyCode: 461 });
    await page.advance(50);
    assert.ok(page.document.querySelector('.jfur-panel') === null, 'Back closed it');
    assert.ok(page.document.activeElement === remove, 'back on Remove…');
    assert.equal(page.errors.length, 0);
});

test('Remove… whose button went while the dialog was open (its row built again behind it): the dialog\'s close puts the focus on the Remove… of the row in its place, never the page\'s body', { skip }, async () => {
    const page = await start({ layout: 'tv' });
    const { section } = await mounted(page, ITEM, itemDto({ Can: ADMIN_CAN, Reviews: { Items: [review(1), review(2)], TotalRecordCount: 2, StartIndex: 0 } }));
    moderationServer(page);
    const remove = section.querySelector('[data-jfur-review="r1"] .jfur-review-remove');
    remove.focus();
    remove.click();
    await page.advance(50);
    section.querySelector('[data-jfur-review="r1"]').remove();
    page.key(page.document.activeElement, 'GoBack', { keyCode: 461 });
    await page.advance(50);
    assert.ok(page.document.querySelector('.jfur-panel') === null, 'Back closed it');
    assert.ok(page.document.activeElement === section.querySelector('[data-jfur-review="r2"] .jfur-review-remove'), 'the Remove… of the row now in its place');
    assert.equal(page.errors.length, 0);
});
