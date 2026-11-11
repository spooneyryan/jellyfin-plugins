/*
 * The details page's "Member ratings & reviews" section (40-details.js): the
 * summary (the average to one decimal with exact star fill, the counts, the ten-bucket histogram), the "Also
 * rated on another copy" hint, the member's own rating (30-stars.js's slider, alone in its row: on the TV layout Left and
 * Right are always the slider's, so nothing focusable may sit beside it), Clear and "Write a review" on the row
 * below, the own review card with Edit and Delete, the review form, and the other members' rows with their order and
 * "Load more"; spoilers (a row's text out of the page until asked for, a blur in its place; the form's
 * "Contains spoilers"), the 409 (the newer review shown read-only; Reload: it in the form, every text of the member's kept
 * beside it; Overwrite: the member's text over the review shown), drafts (the member's unsaved text, kept in the browser's
 * storage per member and item until saved or discarded, removed at sign-out and when the session expires, gone after 30
 * days), and the read-only states Can.Reason names (NotARater read-only stars; NeedsPlay, ReadOnly and an unknown reason
 * the disabled slider, each with its note). A review write sends the review's own precondition
 * (IfReviewUpdatedUtc, or IfNoReview for a first review), so the page's rating saves never make it a 409.
 *
 * One section at a time, with its chip and Rate button (50-chip.js: they follow its renders and its going):
 * the scanner (kit.observe) mounts it on the shown details page for the item in the hash, before the cast
 * (10-selectors.js, sectionPlace); another item, another view, a sign-out or a restored view (viewshow with isRestored)
 * unmounts it: the slider's destroy() hands a change still waiting for its save on, so it is saved
 * once, and the section is removed. Nothing is shown for an item the server answers 404 (not
 * visible, not rateable) or 403 (ratings off) for.
 *
 * Every write of a section goes through one queue, so the server gets them in the order they were made and a review's
 * IfUpdatedUtc is the member's row as the last write left it. What each answer means is detailsFailure's:
 * a write answered 403 or 404 was saved or refused in a way the next read decides, and the section goes when that read is a
 * 403 or 404, with no "couldn't save"; IsPartial shows Mine as written and leaves the summary, the copies and the list as
 * they were until the next view; 503 and no answer keep everything and offer Retry.
 *
 * Every member's words (review texts, user names) and every library name sits in its own element with dir="auto" and
 * unicode-bidi: isolate, set as text only.
 */

// A review longer than this shows its start and "Show more": characters (code points) and lines.
const DETAILS_PREVIEW_CHARS = 420;
const DETAILS_PREVIEW_LINES = 6;
// The orders of the other members' rows, the first the server's default.
const DETAILS_SORTS = Object.freeze(['Newest', 'Oldest', 'Highest', 'Lowest']);
// The reasons (Can.Reason) that leave a member's own clear and delete allowed: WhoCanRate and the played rule
// limit creating and editing only. ReadOnly refuses every write, and a reason this page doesn't know is
// taken to refuse them too, so the page never offers a write the server refuses.
const DETAILS_OWN_WRITE_REASONS = Object.freeze(['NotARater', 'NeedsPlay']);

// keptChip (50-chip.js): the chip a restored view's left section had, kept while its item is read
// again.
const detailsState = { entry: null, count: 0, ids: 0, keptChip: null };
// A draft is saved this long after the last input, and at once when the form closes or the page is left.
const DETAILS_DRAFT_MS = 400;
// How long a draft is kept: one saved longer ago than this goes at the next start.
const DETAILS_DRAFT_MAX_AGE_MS = 30 * 24 * 60 * 60 * 1000;
// The reasons a member may stop having once they come back to the page: they played the item
// elsewhere, or an admin let them rate; the item is read again when the page is shown again.
const DETAILS_RECHECK_REASONS = Object.freeze(['NeedsPlay', 'NotARater']);
// A spoiler's blur shows this many bars whatever the text's length (its length is part of the spoiler).
const DETAILS_SPOILER_BARS = 3;

// ------------------------------------------------------------------------------------------- pure functions

// An item or row id as the page compares it: N format, lower case.
function detailsId(id) {
    return String(id === undefined || id === null ? '' : id).replace(/-/g, '').toLowerCase();
}

// What the page offers the member, from the item's Can: rate (the slider), review (the form, Edit),
// own (Clear and Delete of their own row), the reason the slider is disabled (null when it isn't; 'Unknown' when the
// server gave none), and reviewsOff (Can.Review is false while Can.Rate is true: only reviews being off does that).
function detailsOffers(can) {
    const c = can || {};
    const rate = c.Rate === true;
    const reason = rate ? null : (c.Reason ? String(c.Reason) : 'Unknown');
    return {
        rate: rate,
        review: rate && c.Review === true,
        own: rate || DETAILS_OWN_WRITE_REASONS.indexOf(reason) >= 0,
        reason: reason,
        reviewsOff: rate && c.Review !== true
    };
}

// What an answer that isn't a success means for an operation: 'read' (GET Items), 'more' (GET Reviews:
// Load more, an order), 'rating', 'clear', 'review' or 'delete' (the writes). { kind, status, message }:
//   gone        a 404, or a read's 403 (ratings off): nothing is shown; after a write the change may have been saved
//               and either way the section goes, with no "couldn't save"
//   reviewsOff  a 403 of GET Reviews: "Reviews are turned off on this server.", no error and no Retry
//   recheck     a write's 403: refused (who may rate, the played rule, reviews or ratings turned off since the page read the
//               item) or saved and refused on the read after it; the page reads the item again, which decides
//   conflict    a 409: the review changed in another window; the form keeps the text and offers Reload and Overwrite
//   invalid     a 400 or 413: the server's sentence, the form kept
//   retry       a 503 (busy, read-only), no answer (status 0), anything else: the sentence and Retry, nothing lost
function detailsFailure(error, op) {
    const status = error && typeof error.status === 'number' ? error.status : 0;
    const message = error && error.message ? String(error.message) : '';
    let kind = 'retry';
    if (status === 404) {
        kind = 'gone';
    } else if (status === 403) {
        kind = op === 'read' ? 'gone' : (op === 'more' ? 'reviewsOff' : 'recheck');
    } else if (status === 409) {
        kind = 'conflict';
    } else if (status === 400 || status === 413) {
        kind = 'invalid';
    }

    return { kind: kind, status: status, message: message };
}

// "7 ratings · 3 reviews" (the reviews left out while there are none, or while reviews are off).
function detailsCounts(count, reviews, reviewsOff) {
    const parts = [count === 1 ? kit.t('details.ratingsOne') : kit.t('details.ratings', { count: count })];
    if (reviews > 0 && !reviewsOff) {
        parts.push(reviews === 1 ? kit.t('details.reviewsOne') : kit.t('details.reviews', { count: reviews }));
    }

    return parts.join(kit.t('details.separator'));
}

// The summary line, or null without a summary (a write's partial answer: the one shown stays):
//   kind 'average'   value (to one decimal, which the stars fill exactly), average ('4.2'), label ("Member average 4.2 out
//                    of 5, 7 ratings"), counts;
//   kind 'few'       fewer than MinRatingsForAverage: "Not enough ratings yet", counts;
//   kind 'none'      no ratings: "No member ratings yet. Be the first." (or without "Be the first." when the member can't
//                    rate), counts only for text-only reviews;
//   kind 'noneHere'  none here, but another copy the member can open has some: "No member ratings on this copy yet."
function detailsSummary(summary, display, copies, canRate, reviewsOff) {
    if (!summary) {
        return null;
    }

    const count = Math.max(0, Math.floor(Number(summary.Count) || 0));
    const reviews = Math.max(0, Math.floor(Number(summary.ReviewCount) || 0));
    const min = Math.max(1, Math.floor(Number(display && display.MinRatingsForAverage) || 1));
    const average = Number(summary.Average);
    if (count === 0) {
        const elsewhere = Array.isArray(copies) && copies.length > 0;
        return {
            kind: elsewhere ? 'noneHere' : 'none',
            text: elsewhere ? kit.t('details.noneHere') : (canRate ? kit.t('details.noneFirst') : kit.t('details.none')),
            counts: reviews > 0 && !reviewsOff ? (reviews === 1 ? kit.t('details.reviewsOne') : kit.t('details.reviews', { count: reviews })) : '',
            count: 0,
            reviews: reviews
        };
    }

    const counts = detailsCounts(count, reviews, reviewsOff);
    if (count < min || !isFinite(average) || average <= 0) {
        return { kind: 'few', text: kit.t('details.few'), counts: counts, count: count, reviews: reviews };
    }

    const shown = formatStars(average, 1);
    return {
        kind: 'average',
        value: Number(shown),
        average: shown,
        label: count === 1 ? kit.t('details.averageLabelOne', { average: shown }) : kit.t('details.averageLabel', { average: shown, count: count }),
        counts: counts,
        count: count,
        reviews: reviews
    };
}

// The ten-bucket histogram (Distribution[0] is 0.5 stars, [9] 5 stars): each bar's stars, count and height in
// percent of the tallest, and the sentence a screen reader hears ("How members rated: 2 at 4 stars, 1 at 5 stars").
function detailsHistogram(distribution) {
    const counts = [];
    for (let i = 0; i < 10; i++) {
        const n = Array.isArray(distribution) ? Number(distribution[i]) : 0;
        counts.push(isFinite(n) && n > 0 ? Math.floor(n) : 0);
    }

    const max = Math.max.apply(null, counts);
    const bars = counts.map(function (count, i) {
        return { stars: (i + 1) / 2, count: count, percent: max > 0 ? Math.round((count / max) * 100) : 0 };
    });
    const parts = bars.filter(function (b) {
        return b.count > 0;
    }).reverse().map(function (b) {
        return b.stars === 1 ? kit.t('details.histogramOne', { count: b.count }) : kit.t('details.histogramItem', { count: b.count, stars: formatStars(b.stars) });
    });
    return { bars: bars, max: max, label: kit.t('details.histogramLabel', { list: parts.join(', ') }) };
}

// The other copies worth a hint: ones with a counted rating or the member's own. The server lists only
// copies the member can open, parents included; nothing here widens that.
function detailsCopies(otherCopies) {
    return (Array.isArray(otherCopies) ? otherCopies : []).filter(function (c) {
        return !!c && !!c.ItemId && ((Number(c.Count) || 0) > 0 || (Number(c.MyStars) || 0) > 0);
    });
}

// A copy's hint as a strings-table key and its values; {library} is left for the page to fill with the isolated name.
function detailsCopyText(copy, display) {
    const count = Math.max(0, Math.floor(Number(copy.Count) || 0));
    const min = Math.max(1, Math.floor(Number(display && display.MinRatingsForAverage) || 1));
    const average = Number(copy.Average);
    const named = !!copy.LibraryName;
    const vars = { count: count };
    let key;
    if (count >= min && isFinite(average) && average > 0) {
        key = named ? 'details.copy' : 'details.copyUnnamed';
        vars.average = formatStars(average, 1);
    } else {
        key = named ? 'details.copyCount' : 'details.copyCountUnnamed';
        vars.ratings = count === 1 ? kit.t('details.ratingsOne') : kit.t('details.ratings', { count: count });
    }

    const mine = Number(copy.MyStars);
    return { key: key, vars: vars, mine: isFinite(mine) && mine > 0 ? kit.t('details.copyYours', { stars: formatStars(mine) }) : '' };
}

// The start of a long review ("Show more"): at most maxLines lines and maxChars characters (code points, so a
// pair of surrogates is never cut), with "…"; truncated says whether anything was left out.
function reviewPreview(text, maxChars, maxLines) {
    const full = String(text === undefined || text === null ? '' : text);
    let cut = full;
    let truncated = false;
    const lines = full.split('\n');
    if (lines.length > maxLines) {
        cut = lines.slice(0, maxLines).join('\n');
        truncated = true;
    }

    const chars = Array.from(cut);
    if (chars.length > maxChars) {
        cut = chars.slice(0, maxChars).join('');
        truncated = true;
    }

    return { text: truncated ? cut.replace(/\s+$/, '') + '…' : full, truncated: truncated };
}

// The rows of a page added to those shown, each row once (the list can shift while a member pages: a new review
// moves the rest down by one).
function mergeReviews(rows, more) {
    const seen = {};
    const out = [];
    (rows || []).concat(more || []).forEach(function (row) {
        const id = row ? detailsId(row.Id) : '';
        if (id && !seen[id]) {
            seen[id] = true;
            out.push(row);
        }
    });
    return out;
}

// The starting point of the review form: the member's row as the form found it, its UpdatedUtc (sent
// as IfUpdatedUtc) and its review part (the text, the flag, the review's own time); null without a row.
function reviewBase(mine) {
    if (!mine) {
        return null;
    }

    return {
        UpdatedUtc: mine.UpdatedUtc ? String(mine.UpdatedUtc) : null,
        Text: mine.Text ? String(mine.Text) : null,
        IsSpoiler: !!(mine.Text && mine.IsSpoiler),
        ReviewUpdatedUtc: mine.ReviewUpdatedUtc ? String(mine.ReviewUpdatedUtc) : null
    };
}

function sameUtc(a, b) {
    if (!a || !b) {
        return !a && !b;
    }

    const x = Date.parse(a);
    const y = Date.parse(b);
    return isFinite(x) && isFinite(y) ? x === y : String(a) === String(b);
}

// Whether the review part of the member's row (its text, its flag, its own time) is no longer what the form started from:
// another window saved or deleted the review meanwhile. The stars and UpdatedUtc may differ: the page's own rating
// saves change them.
function reviewChangedSince(base, mine) {
    const now = reviewBase(mine);
    const text = function (b) {
        return b && b.Text ? b.Text : null;
    };
    return text(base) !== text(now)
        || !!(base && base.IsSpoiler) !== !!(now && now.IsSpoiler)
        || !sameUtc(base && base.ReviewUpdatedUtc, now && now.ReviewUpdatedUtc);
}

// The review's PUT body: the text, the form's "Contains spoilers" (an edit starts from the row's
// flag; false where the server has the flag off, AllowSpoilerFlag) and the review's own precondition from the form's
// starting point: IfReviewUpdatedUtc, the review's time as the form found it, or IfNoReview for a
// form opened on no review (no row, or stars alone), so a rating saved meanwhile (here or elsewhere) never makes it a 409,
// and a first review written in two windows is a 409 in the second instead of an overwrite. A review without its own time
// (none on record) falls back to the row's IfUpdatedUtc. Overwrite passes the row the 409 showed.
function reviewBody(text, spoiler, base) {
    const body = { Text: String(text), IsSpoiler: !!spoiler };
    if (base && base.Text) {
        if (base.ReviewUpdatedUtc) {
            body.IfReviewUpdatedUtc = base.ReviewUpdatedUtc;
        } else if (base.UpdatedUtc) {
            body.IfUpdatedUtc = base.UpdatedUtc;
        }
    } else {
        body.IfNoReview = true;
    }

    return body;
}

// The name of a member's draft of a review of an item in the kit's storage (localStorage
// "jfur:draft:<userId>:<itemId>"), and the start of every draft of a member.
function draftName(userId, itemId) {
    return 'draft:' + detailsId(userId) + ':' + (itemId === undefined ? '' : detailsId(itemId));
}

// Whether what the form holds is a change to keep as a draft: text or a flag other than the starting point's, or other
// texts kept beside it after a Reload.
function draftDiffers(text, spoiler, kept, base) {
    return (Array.isArray(kept) ? kept.length > 0 : !!kept) || String(text || '') !== String((base && base.Text) || '') || !!spoiler !== !!(base && base.IsSpoiler);
}

// A stored draft as the page reads it back, or null when it isn't one (the storage is the browser's: anything may be in
// it): { text, spoiler, base, kept: [{ text, mine }], savedUtc }. An older draft's one "aside" text is its kept list.
function readDraft(value) {
    if (!value || typeof value !== 'object' || typeof value.text !== 'string') {
        return null;
    }

    const base = value.base && typeof value.base === 'object' ? reviewBase({ UpdatedUtc: value.base.UpdatedUtc, Text: value.base.Text, IsSpoiler: value.base.IsSpoiler, ReviewUpdatedUtc: value.base.ReviewUpdatedUtc }) : null;
    const list = Array.isArray(value.kept) ? value.kept : (value.aside ? [value.aside] : []);
    const kept = list.filter(function (k) {
        return !!k && typeof k === 'object' && typeof k.text === 'string' && k.text !== '';
    }).map(function (k) {
        return { text: k.text, mine: k.mine === true };
    });
    const saved = typeof value.savedUtc === 'string' && isFinite(Date.parse(value.savedUtc)) ? value.savedUtc : null;
    return { text: value.text, spoiler: value.spoiler === true, base: base && base.UpdatedUtc ? base : null, kept: kept, savedUtc: saved };
}

// Whether a draft as read back is too old to keep: saved more than DETAILS_DRAFT_MAX_AGE_MS before now. One
// with no time of its own (written before drafts had one) is kept, never thrown away for want of a date;
// the start dates it (purgeExpiredDrafts), so it goes 30 days later. What isn't a draft at all is "expired".
function draftExpired(draft, now) {
    if (!draft) {
        return true;
    }

    if (!draft.savedUtc) {
        return false;
    }

    return (now === undefined ? Date.now() : now) - Date.parse(draft.savedUtc) > DETAILS_DRAFT_MAX_AGE_MS;
}

// A text kept beside the form: added to the list unless it is empty or already in it (the
// member's own words make an entry that was "theirs" theirs no longer: it is the member's now too).
function keepText(kept, text, mine) {
    const value = String(text === undefined || text === null ? '' : text);
    if (value.trim() === '') {
        return kept;
    }

    for (let i = 0; i < kept.length; i++) {
        if (kept[i].text === value) {
            kept[i].mine = kept[i].mine || !!mine;
            return kept;
        }
    }

    kept.push({ text: value, mine: !!mine });
    return kept;
}

// The note a disabled or read-only rating shows for Can.Reason, as a strings-table key.
function readOnlyNoteKey(reason) {
    switch (reason) {
        case 'NotARater':
            return 'details.noteNotARater';
        case 'NeedsPlay':
            return 'details.noteNeedsPlay';
        case 'ReadOnly':
            return 'details.noteReadOnly';
        default:
            return 'details.noteUnknown';
    }
}

// ---------------------------------------------------------------------------------------------------- the DOM

// A member's words, or a library's name, in its own element: dir="auto", unicode-bidi: isolate, as text.
function isolatedText(tag, className, text) {
    return kit.h(tag, { class: className + ' jfur-isolate', dir: 'auto', text: String(text === undefined || text === null ? '' : text) });
}

// A sentence of the strings table with one {name} given as a node (an isolated name), the rest as text.
function sentenceWith(key, vars, name, node) {
    const text = kit.t(key, vars);
    const marker = '{' + name + '}';
    const at = text.indexOf(marker);
    if (at < 0) {
        return [document.createTextNode(text)];
    }

    return [document.createTextNode(text.substring(0, at)), node, document.createTextNode(text.substring(at + marker.length))];
}

function detailsPageSize(entry) {
    const size = Number(entry.caps && entry.caps.Display && entry.caps.Display.ReviewsPageSize);
    return isFinite(size) && size >= 1 ? Math.min(200, Math.floor(size)) : 20;
}

function detailsDisplay(entry) {
    return (entry.caps && entry.caps.Display) || RATINGS_DEFAULT_CAPABILITIES.Display;
}

function announceDetails(entry, text) {
    const live = entry.els && entry.els.live;
    if (!live) {
        return;
    }

    live.textContent = '';
    setTimeout(function () {
        live.textContent = String(text || '');
    }, 50);
}

function focusIfShown(node) {
    if (node && node.isConnected && !kit.isHidden(node) && !node.disabled) {
        try {
            node.focus();
        } catch (e) {
            // can't take the focus
        }

        return document.activeElement === node;
    }

    return false;
}

// Our own confirmation: Cancel first and focused, as for every destructive dialog; scopedKeys, so the
// TV layout's D-pad moves between the buttons and Jellyfin Enhanced's keys stay out (the kit's README, "dialog").
function detailsConfirm(opts) {
    return new Promise(function (resolve) {
        let answered = false;
        let cancel = null;
        const dialog = kit.dialog({ title: opts.title, modal: true, scopedKeys: true, initialFocus: function () { return cancel; } });
        const answer = function (value) {
            answered = true;
            resolve(value);
            dialog.close();
        };
        dialog.onClose(function () {
            if (!answered) {
                resolve(false);
            }
        });
        cancel = kit.button(kit.t('details.cancel'), { className: 'jfur-dialog-cancel', onClick: function () { answer(false); } });
        const confirm = kit.button(opts.confirm, { variant: 'primary', className: 'jfur-dialog-confirm', onClick: function () { answer(true); } });
        kit.append(dialog.body, opts.message ? kit.h('p', { text: opts.message }) : null, kit.h('div', { class: kit.cls('actions') }, [cancel, confirm]));
    });
}

// A notice with an optional Retry (kit.notice: role="alert" for an error), put in its place; null empties the place.
function showNotice(place, kind, text, retry) {
    if (!place) {
        return;
    }

    if (!text) {
        place.replaceChildren();
        kit.setHidden(place, true);
        return;
    }

    place.replaceChildren(kit.notice({ kind: kind, text: text, action: retry ? { label: kit.t('details.retry'), onClick: retry } : null }));
    kit.setHidden(place, false);
}

function avatarUrl(review) {
    const client = kit.client();
    if (!client || !review.UserId || !review.UserImageTag) {
        return null;
    }

    const options = { type: 'Primary', tag: review.UserImageTag, maxHeight: 96 };
    return typeof client.getUserImageUrl === 'function'
        ? client.getUserImageUrl(detailsId(review.UserId), options)
        : client.getUrl('Users/' + detailsId(review.UserId) + '/Images/Primary', { tag: review.UserImageTag, maxHeight: 96 });
}

function reviewTime(utc, edited) {
    return kit.h('span', { class: 'jfur-review-meta' }, [
        utc ? kit.h('time', { datetime: String(utc), title: kit.format.dateTime(utc), text: kit.format.ago(utc) }) : null,
        edited ? kit.h('span', { class: 'jfur-review-edited', text: kit.t('details.edited') }) : null
    ]);
}

function detailsNextId(name) {
    return 'jfur-' + name + '-' + (++detailsState.ids);
}

// A review's text: isolated, its line breaks kept, the start of a long one with "Show more". Its nodes are made
// when it is shown, so a spoiler's text is made only when the member asks.
function reviewTextNodes(text) {
    const full = String(text || '');
    const preview = reviewPreview(full, DETAILS_PREVIEW_CHARS, DETAILS_PREVIEW_LINES);
    const body = isolatedText('div', 'jfur-review-text', '');
    body.id = detailsNextId('review-text');
    const more = preview.truncated ? kit.h('button', { type: 'button', class: kit.cls('link') + ' jfur-review-more', 'aria-expanded': 'false', 'aria-controls': body.id }) : null;
    let expanded = false;
    const show = function () {
        body.textContent = expanded || !preview.truncated ? full : preview.text;
        if (more) {
            more.textContent = expanded ? kit.t('details.showLess') : kit.t('details.showMore');
            more.setAttribute('aria-expanded', expanded ? 'true' : 'false');
        }
    };
    if (more) {
        more.addEventListener('click', function () {
            expanded = !expanded;
            show();
        });
    }

    show();
    return more ? [body, more] : [body];
}

// A spoiler's place before it is shown: blurred bars and no text at all, so nothing of the spoiler is in the page: a
// blur over the real text would still be found by Ctrl+F, read by a screen reader, selected, copied and translated. Always
// DETAILS_SPOILER_BARS bars, whatever the text's length (one bar for a line and three for a page told how
// long it was). Decorative (aria-hidden).
function spoilerPlaceholder() {
    const bars = [];
    for (let i = 0; i < DETAILS_SPOILER_BARS; i++) {
        const bar = kit.h('span', { class: 'jfur-spoiler-line' });
        bar.style.width = (i === DETAILS_SPOILER_BARS - 1 ? 60 : 100) + '%';
        bars.push(bar);
    }

    return kit.h('div', { class: 'jfur-spoiler-blur', 'aria-hidden': 'true' }, bars);
}

// A review's text as a row shows it: a spoiler (the author's flag) shows "Contains spoilers · Show spoiler" and
// the blurred placeholder; Show spoiler puts the text in (the focus stays on the button, now "Hide spoiler", which takes
// the text out again).
function reviewText(text, spoiler) {
    const wrap = kit.h('div', { class: 'jfur-review-content' });
    if (!spoiler) {
        wrap.append.apply(wrap, reviewTextNodes(text));
        return wrap;
    }

    let revealed = false;
    const holder = kit.h('div', { class: 'jfur-spoiler-text', id: detailsNextId('spoiler') }, [spoilerPlaceholder()]);
    const reveal = kit.h('button', { type: 'button', class: kit.cls('link') + ' jfur-spoiler-toggle', 'aria-expanded': 'false', 'aria-controls': holder.id, text: kit.t('details.showSpoiler') });
    reveal.addEventListener('click', function () {
        revealed = !revealed;
        holder.replaceChildren.apply(holder, revealed ? reviewTextNodes(text) : [spoilerPlaceholder()]);
        reveal.textContent = revealed ? kit.t('details.hideSpoiler') : kit.t('details.showSpoiler');
        reveal.setAttribute('aria-expanded', revealed ? 'true' : 'false');
    });
    wrap.append(kit.h('div', { class: 'jfur-spoiler' }, [kit.h('span', { class: 'jfur-spoiler-flag', text: kit.t('details.spoiler') }), reveal]), holder);
    return wrap;
}

// Another member's row: avatar, name, stars, when, "(edited)", the text; for an admin (Can.Moderate, which the
// server sets for admins only) "Remove…" at its end, never on the admin's own row.
function reviewRow(review, entry) {
    const src = avatarUrl(review);
    const avatar = src
        ? kit.h('img', { class: 'jfur-avatar', src: src, alt: '', loading: 'lazy', 'aria-hidden': 'true' })
        : kit.icon('person', 'jfur-avatar jfur-avatar-none');
    const stars = Number(review.Stars);
    const name = review.UserName || kit.t('details.formerMember');
    const head = kit.h('div', { class: 'jfur-review-head' }, [
        isolatedText('span', 'jfur-review-name', name),
        isFinite(stars) && stars > 0 ? renderStars(stars, { className: 'jfur-review-stars' }) : null,
        reviewTime(review.UpdatedUtc, review.Edited === true)
    ]);
    const remove = canModerate(entry, review)
        ? kit.h('button', { type: 'button', class: kit.cls('link') + ' jfur-review-remove', 'aria-label': kit.t('moderate.removeLabel', { name: name }), text: kit.t('moderate.remove') })
        : null;
    if (remove) {
        remove.addEventListener('click', function () {
            openModeration(entry, review, remove);
        });
    }

    return kit.h('div', { class: 'jfur-review', role: 'listitem', tabindex: '-1', 'data-jfur-review': detailsId(review.Id) }, [
        avatar,
        kit.h('div', { class: 'jfur-review-body' }, [head, review.Text ? reviewText(review.Text, review.IsSpoiler === true) : null, remove])
    ]);
}

// ---------------------------------------------------------------------------------------------------------- moderation

const MODERATION_REASONS = ['Spoiler', 'Offensive', 'Spam', 'OffTopic', 'Requested', 'Other'];

function canModerate(entry, review) {
    return !!entry && !!entry.can && entry.can.Moderate === true && !!review && !!review.Id
        && detailsId(review.UserId) !== detailsId(entry.userId);
}

// The dialog of "Remove…": the two removals as a choice ("Remove text only" first and chosen), an optional reason, and what
// the server's dry run says the chosen one removes, which Remove then removes (its PreviewToken: a row changed since is a
// 409 that removes nothing, and the dialog checks again). Cancel is first and focused; scopedKeys, so the TV layout's
// D-pad moves through it and Jellyfin Enhanced's keys stay out. The focus goes back to the button that opened it, or once
// the list is read again to the row in its place.
function openModeration(entry, review, opener) {
    if (entry.dead || !canModerate(entry, review)) {
        return;
    }

    const index = entry.reviews.indexOf(review);
    const state = { textOnly: true, token: null, seq: 0, busy: false, closed: false };
    // The row's stars as the dialog knows them: the list's, then the ones read again after a 409 Changed.
    let currentStars = review.Stars;
    const inputs = {};
    const hints = {};
    let cancel = null;
    const dialog = kit.dialog({
        title: kit.t('moderate.title'),
        modal: true,
        scopedKeys: true,
        initialFocus: function () { return cancel; },
        returnFocus: function () { return moderationFocusTarget(entry, review.Id, index); }
    });
    dialog.onClose(function () {
        state.closed = true;
        state.seq++;
    });
    const name = review.UserName || kit.t('details.formerMember');
    const group = 'jfur-moderate-' + (++detailsState.ids);
    const choice = function (textOnly, label, hint) {
        const input = kit.h('input', { type: 'radio', name: group, value: textOnly ? 'text' : 'entry', class: 'jfur-moderate-radio' });
        input.checked = textOnly === state.textOnly;
        inputs[textOnly ? 'text' : 'entry'] = input;
        input.addEventListener('change', function () {
            if (input.checked && state.textOnly !== textOnly) {
                state.textOnly = textOnly;
                check();
            }
        });
        const hintNode = kit.h('span', { class: 'jfur-moderate-choice-hint', text: hint });
        hints[textOnly ? 'text' : 'entry'] = hintNode;
        return kit.h('label', { class: 'jfur-moderate-choice' }, [input, kit.h('span', { class: 'jfur-moderate-choice-text' }, [
            kit.h('span', { class: 'jfur-moderate-choice-label', text: label }),
            hintNode
        ])]);
    };
    const reasonId = detailsNextId('moderate-reason');
    const reason = kit.select({ id: reasonId, class: 'jfur-moderate-reason' }, [{ value: '', text: kit.t('moderate.reasonNone') }].concat(MODERATION_REASONS.map(function (r) {
        return { value: r, text: kit.t('moderate.reason' + r) };
    })));
    const preview = kit.h('p', { class: 'jfur-moderate-preview', role: 'status' });
    // The review as it is now, shown once it was read again after a 409 Changed: its stars and its words, isolated.
    const now = kit.h('div', { class: 'jfur-moderate-now' });
    kit.setHidden(now, true);
    const problem = kit.h('div', { class: 'jfur-moderate-problem' });
    kit.setHidden(problem, true);
    cancel = kit.button(kit.t('details.cancel'), { className: 'jfur-dialog-cancel', onClick: function () { dialog.close(); } });
    const confirm = kit.button(kit.t('moderate.confirm'), { variant: 'primary', className: 'jfur-dialog-confirm jfur-moderate-confirm', onClick: function () { remove(); } });
    confirm.disabled = true;
    kit.append(dialog.body,
        kit.h('p', { class: 'jfur-moderate-by' }, sentenceWith('moderate.by', null, 'name', isolatedText('span', 'jfur-moderate-name', name))),
        now,
        kit.h('fieldset', { class: 'jfur-moderate-what' }, [
            kit.h('legend', { text: kit.t('moderate.what') }),
            choice(true, kit.t('moderate.textOnly'), kit.t('moderate.textOnlyHint')),
            choice(false, kit.t('moderate.entry'), kit.t('moderate.entryHint'))
        ]),
        kit.h('div', { class: 'jfur-moderate-reason-field' }, [kit.h('label', { for: reasonId, text: kit.t('moderate.reason') }), reason]),
        preview,
        problem,
        kit.h('div', { class: kit.cls('actions') }, [cancel, confirm]));

    const say = function (text) {
        problem.replaceChildren();
        kit.setHidden(problem, !text);
        if (text) {
            problem.append(kit.notice({ kind: 'error', text: text }));
        }
    };
    // The dry run of the chosen removal: its sentence and token; Remove waits for it.
    function check(note) {
        const seq = ++state.seq;
        state.token = null;
        confirm.disabled = true;
        say(note || null);
        preview.textContent = kit.t('moderate.checking');
        detailsRead(entry, function () {
            return ratingsApi.moderationPreview(review.Id, state.textOnly);
        }).then(function (answer) {
            if (seq !== state.seq || state.closed || !answer) {
                return;
            }

            state.token = answer.PreviewToken || null;
            const stars = Number(currentStars);
            preview.textContent = (answer.DeletesRating === true
                ? (state.textOnly ? kit.t('moderate.previewTextDeletes') : kit.t('moderate.previewEntry'))
                : kit.t('moderate.previewText', { stars: starsValueText(stars) }))
                + ' ' + kit.t('moderate.previewLog');
            confirm.disabled = !state.token || state.busy;
        }, function (e) {
            if (seq !== state.seq || state.closed) {
                return;
            }

            preview.textContent = '';
            if (e && e.userChanged) {
                dialog.close();
                return;
            }

            say(moderationProblem(e, true));
        });
    }

    function remove() {
        if (!state.token || state.busy || state.closed) {
            return;
        }

        const token = state.token;
        const textOnly = state.textOnly;
        state.busy = true;
        confirm.disabled = true;
        confirm.textContent = kit.t('moderate.removing');
        say(null);
        enqueue(entry, function () {
            return ratingsApi.moderate(review.Id, textOnly, reason.value || null, token);
        }).then(function () {
            state.busy = false;
            if (state.closed) {
                return;
            }

            dialog.close();
            const done = textOnly ? kit.t('moderate.doneText') : kit.t('moderate.doneEntry');
            announceDetails(entry, done);
            kit.toast(done);
            afterModeration(entry, review.Id, index);
        }, function (e) {
            state.busy = false;
            confirm.textContent = kit.t('moderate.confirm');
            if (state.closed) {
                return;
            }

            if (e && e.userChanged) {
                dialog.close();
                return;
            }

            if (e && e.status === 409 && e.code === 'Changed') {
                // The row changed since the dry run: nothing was removed; it is read again and shown as it is now, then
                // what it removes now is checked.
                reload();
                return;
            }

            say(moderationProblem(e, false));
            confirm.disabled = !state.token;
            if (!focusIfShown(confirm)) {
                focusIfShown(cancel);
            }
        });
    }

    // After a 409 Changed: nothing armed while the row is read again; then the review as it is
    // now (its stars and words), the choices it allows, and only then the dry run that gives the new token. The focus goes to
    // Cancel, a live control, since Remove is disabled meanwhile (the TV layout's D-pad has somewhere to be).
    function reload() {
        const seq = ++state.seq;
        state.token = null;
        confirm.disabled = true;
        say(null);
        preview.textContent = kit.t('moderate.reloading');
        focusIfShown(cancel);
        detailsRead(entry, function () {
            return ratingsApi.moderationRows(review.UserId, entry.itemId);
        }).then(function (page) {
            if (seq !== state.seq || state.closed) {
                return;
            }

            const id = String(review.Id).replace(/-/g, '').toLowerCase();
            const fresh = ((page && page.Items) || []).filter(function (r) { return String(r.RatingId).replace(/-/g, '').toLowerCase() === id; })[0];
            if (!fresh) {
                preview.textContent = '';
                kit.setHidden(now, true);
                say(kit.t('moderate.changedGone'));
                focusIfShown(cancel);
                return;
            }

            currentStars = fresh.Stars;
            const stars = Number(fresh.Stars);
            now.replaceChildren(
                kit.h('p', { class: 'jfur-moderate-now-stars', text: isFinite(stars) && stars > 0 ? kit.t('moderate.now', { stars: starsValueText(stars) }) : kit.t('moderate.nowNoStars') }),
                fresh.Text ? isolatedText('p', 'jfur-moderate-now-text', fresh.Text) : kit.h('p', { class: 'jfur-moderate-now-text jfur-moderate-now-none', text: kit.t('moderate.nowNoReview') }));
            kit.setHidden(now, false);
            const textNow = !!fresh.Text;
            inputs.text.disabled = !textNow;
            hints.text.textContent = textNow ? kit.t('moderate.textOnlyHint') : kit.t('moderate.noReview');
            if (!textNow && state.textOnly) {
                state.textOnly = false;
                inputs.text.checked = false;
                inputs.entry.checked = true;
            }

            check(kit.t('moderate.changed'));
            focusIfShown(cancel);
        }, function (e) {
            if (seq !== state.seq || state.closed) {
                return;
            }

            preview.textContent = '';
            if (e && e.userChanged) {
                dialog.close();
                return;
            }

            say(moderationProblem(e, true));
            focusIfShown(cancel);
        });
    }

    check();
}

// What a refused dry run or removal says: a row gone or out of reach (404), a review gone meanwhile (409, Remove text
// only), the server's sentence otherwise.
function moderationProblem(error, preview) {
    const status = error && error.status;
    if (status === 404) {
        return kit.t('moderate.gone');
    }

    if (status === 409 && error.code !== 'Changed') {
        return kit.t('moderate.noReview');
    }

    const reason = (error && error.message) || kit.t('details.refused');
    return preview ? kit.t('moderate.previewFailed', { reason: reason }) : kit.t('moderate.failed', { reason: reason });
}

// After a removal: the item read again (its summary) and the list from its first page in its order; the focus, which went
// back to the Remove… button, goes to the row in its place once that button is gone with its row.
function afterModeration(entry, ratingId, index) {
    refreshDetails(entry);
    const reloaded = changeSort(entry, entry.sort);
    if (reloaded && typeof reloaded.then === 'function') {
        reloaded.then(function () {
            const active = document.activeElement;
            if (!entry.dead && (!active || active === document.body || !active.isConnected)) {
                const target = moderationFocusTarget(entry, ratingId, index);
                if (target) {
                    target.focus();
                }
            }
        });
    }
}

// The row of the rating (its Remove… when it has one), else the row now in its place, else the last row, else the slider.
function moderationFocusTarget(entry, ratingId, index) {
    if (entry.dead || !entry.els) {
        return null;
    }

    const rows = Array.prototype.slice.call(entry.els.list.children);
    const same = rows.filter(function (row) {
        return row.getAttribute('data-jfur-review') === detailsId(ratingId);
    })[0];
    const row = same || rows[Math.min(Math.max(index, 0), rows.length - 1)] || null;
    if (row) {
        return row.querySelector('.jfur-review-remove') || row;
    }

    return entry.slider ? entry.slider.slider : null;
}

// The section's frame: its title (a disclosure button, but no tab stop on the TV layout, where the section is always
// open, so the D-pad goes from the details links straight to "Your rating"), the summary, the copies'
// hint, the member's own part and the list. Filled by renderDetails.
function buildDetailsSection(entry) {
    const n = ++detailsState.count;
    const tv = kit.layout.isTv();
    const parts = kit.section({ title: '', className: 'jfur-section' + (tv ? ' jfur-section-tv' : ''), bodyClass: 'jfur-section-body', attrs: { 'data-jfur-item': entry.itemId } });
    const bodyId = 'jfur-section-' + n + '-body';
    parts.body.id = bodyId;
    const icon = kit.icon('expand_less', 'jfur-section-toggle-icon');
    const toggle = kit.h('button', { type: 'button', class: 'jfur-section-toggle', 'aria-expanded': 'true', 'aria-controls': bodyId, tabindex: tv ? '-1' : null }, [
        kit.h('span', { class: 'jfur-section-heading', text: kit.t('details.title') }),
        icon
    ]);
    parts.title.textContent = '';
    parts.title.append(toggle);
    toggle.addEventListener('click', function () {
        setExpanded(entry, !entry.expanded);
    });

    const labelId = 'jfur-section-' + n + '-rating';
    const els = {
        section: parts.section,
        body: parts.body,
        toggle: toggle,
        toggleIcon: icon,
        loadError: kit.h('div', { class: 'jfur-load-error' }),
        summary: kit.h('div', { class: 'jfur-summary' }),
        copies: kit.h('div', { class: 'jfur-copies' }),
        mine: kit.h('div', { class: 'jfur-mine' }),
        mineMessage: kit.h('div', { class: 'jfur-mine-message' }),
        // NotARater's read-only stars and their note, in the slider's place: nothing to do with them, so no
        // tab stop; while they are shown, tabindex -1 so that they can take the focus when the slider turns read-only under
        // it (on the TV layout the focus fell to the page's body, out of the D-pad's reach).
        readOnly: kit.h('span', { class: 'jfur-mine-readonly' }),
        clear: kit.button(kit.t('details.clear'), { className: 'jfur-clear', onClick: function () { onClear(entry); } }),
        write: kit.button(kit.t('details.write'), { className: 'jfur-write', onClick: function () { openForm(entry); } }),
        actions: null,
        own: kit.h('div', { class: 'jfur-own' }),
        formHost: kit.h('div', { class: 'jfur-form-host' }),
        reviews: kit.h('div', { class: 'jfur-reviews' }),
        toolbar: null,
        sort: null,
        list: kit.h('div', { class: 'jfur-review-list', role: 'list', 'aria-label': kit.t('details.listLabel') }),
        listMessage: kit.h('div', { class: 'jfur-list-message' }),
        reviewsOff: kit.h('p', { class: 'jfur-reviews-off', role: 'status', text: kit.t('details.reviewsOff') }),
        live: kit.h('span', { class: 'jfur-sr-only jfur-section-live', role: 'status', 'aria-live': 'polite', 'aria-atomic': 'true' }),
        labelId: labelId
    };
    els.actions = kit.h('div', { class: 'jfur-mine-actions' }, [els.clear, els.write]);
    const sortId = 'jfur-section-' + n + '-sort';
    els.sort = kit.select({ id: sortId, class: 'jfur-sort' }, DETAILS_SORTS.map(function (s) {
        return { value: s, text: kit.t('details.sort' + s) };
    }));
    els.sort.value = entry.sort;
    els.sort.addEventListener('change', function () {
        changeSort(entry, els.sort.value);
    });
    els.toolbar = kit.h('div', { class: 'jfur-reviews-toolbar' }, [kit.h('label', { for: sortId, class: 'jfur-sort-label', text: kit.t('details.sort') }), els.sort]);
    els.more = kit.button(kit.t('details.loadMore'), { className: kit.cls('pager-more'), onClick: function () { loadMore(entry); } });
    els.moreStatus = kit.h('span', { class: kit.cls('pager-status'), role: 'status' });
    els.pager = kit.h('div', { class: kit.cls('pager') }, [els.more, els.moreStatus]);

    entry.slider = createStarSlider({
        value: entry.mine && entry.mine.Stars ? entry.mine.Stars : 0,
        labelledBy: labelId,
        onCommit: function (value) {
            return saveRating(entry, value);
        },
        onChange: function () {
            renderMineActions(entry);
        }
    });
    els.mine.append(
        kit.h('div', { class: 'jfur-mine-row' }, [kit.h('span', { id: labelId, class: 'jfur-mine-label', text: kit.t('stars.yourRating') }), entry.slider.element, els.readOnly]),
        els.mineMessage,
        els.actions,
        els.own,
        els.formHost
    );
    els.reviews.append(els.toolbar, els.list, els.listMessage, els.pager, els.reviewsOff);
    parts.body.append(els.loadError, els.summary, els.copies, els.mine, els.reviews, els.live);
    [els.loadError, els.mineMessage, els.listMessage, els.reviewsOff, els.readOnly].forEach(function (node) {
        kit.setHidden(node, true);
    });
    entry.els = els;
    setExpanded(entry, entry.expanded);
}

function setExpanded(entry, expanded) {
    entry.expanded = kit.layout.isTv() ? true : !!expanded;
    const els = entry.els;
    kit.setHidden(els.body, !entry.expanded);
    els.toggle.setAttribute('aria-expanded', entry.expanded ? 'true' : 'false');
    els.toggleIcon.classList.toggle('expand_less', entry.expanded);
    els.toggleIcon.classList.toggle('expand_more', !entry.expanded);
}

function renderSummary(entry, offers) {
    const s = detailsSummary(entry.summary, detailsDisplay(entry), detailsCopies(entry.copies), offers.rate, entry.reviewsOff || offers.reviewsOff);
    if (!s) {
        return;
    }

    const els = entry.els;
    const children = [];
    if (s.kind === 'average') {
        children.push(kit.h('span', { class: 'jfur-summary-average' }, [
            renderStars(s.value, { label: s.label, className: 'jfur-summary-stars' }),
            kit.h('span', { class: 'jfur-summary-value', 'aria-hidden': 'true', text: s.average })
        ]));
    } else {
        children.push(kit.h('span', { class: 'jfur-summary-empty', text: s.text }));
    }

    if (s.counts) {
        children.push(kit.h('span', { class: 'jfur-summary-counts', text: s.counts }));
    }

    // The histogram only beside an average: under MinRatingsForAverage it would show what the average hides.
    if (s.kind === 'average' && detailsDisplay(entry).ShowHistogram !== false) {
        const histogram = detailsHistogram(entry.summary.Distribution);
        children.push(kit.h('span', { class: 'jfur-histogram', role: 'img', 'aria-label': histogram.label }, histogram.bars.map(function (bar) {
            const node = kit.h('span', { class: 'jfur-histogram-bar' + (bar.count === 0 ? ' jfur-histogram-zero' : ''), 'data-stars': String(bar.stars), 'data-count': String(bar.count) });
            node.style.height = Math.max(bar.count > 0 ? 8 : 4, bar.percent) + '%';
            return node;
        })));
    }

    els.summary.setAttribute('data-jfur-summary', s.kind);
    els.summary.replaceChildren.apply(els.summary, children);
}

function renderCopies(entry) {
    if (entry.partial) {
        return;
    }

    const els = entry.els;
    const copies = detailsCopies(entry.copies);
    els.copies.replaceChildren.apply(els.copies, copies.map(function (copy) {
        const text = detailsCopyText(copy, detailsDisplay(entry));
        const line = kit.h('p', { class: 'jfur-copy', 'data-jfur-copy': detailsId(copy.ItemId) });
        line.append.apply(line, sentenceWith(text.key, text.vars, 'library', isolatedText('span', 'jfur-copy-library', copy.LibraryName || '')));
        if (text.mine) {
            line.append(document.createTextNode(kit.t('details.separator') + text.mine));
        }

        // The link names what it opens ("Open the copy in Movies 2"), also read alone in a screen reader's list of links.
        const open = kit.h('a', { class: kit.cls('link') + ' jfur-copy-open', href: kit.itemHref(copy.ItemId) });
        open.append.apply(open, copy.LibraryName
            ? sentenceWith('details.openCopy', {}, 'library', isolatedText('span', 'jfur-copy-library', copy.LibraryName))
            : [document.createTextNode(kit.t('details.openCopyUnnamed'))]);
        line.append(document.createTextNode(kit.t('details.separator')), open);
        return line;
    }));
    kit.setHidden(els.copies, copies.length === 0);
}

function hasStars(entry) {
    return !!(entry.mine && Number(entry.mine.Stars) > 0) || (!!entry.slider && entry.slider.value() > 0);
}

// A control of the section while a clear or a delete is under way: aria-disabled, never the
// disabled property, so a control that has the focus keeps it (a disabled one loses it, and a 503 then left the focus on
// the page's body); its own handler does nothing meanwhile (entry.busy).
function setBusyControl(button, busy) {
    if (!button) {
        return;
    }

    if (busy) {
        button.setAttribute('aria-disabled', 'true');
    } else {
        button.removeAttribute('aria-disabled');
    }
}

function renderMineActions(entry) {
    if (!entry.els || entry.dead) {
        return;
    }

    const offers = detailsOffers(entry.can);
    const els = entry.els;
    const reviewsOff = entry.reviewsOff || offers.reviewsOff;
    kit.setHidden(els.clear, !(offers.own && hasStars(entry)));
    setBusyControl(els.clear, entry.busy);
    kit.setHidden(els.write, !(offers.review && !reviewsOff && !entry.formOpen && !(entry.mine && entry.mine.Text)));
    // A draft waiting: "Continue your review".
    els.write.textContent = hasDraft(entry) ? kit.t('details.writeDraft') : kit.t('details.write');
    kit.setHidden(els.actions, kit.isHidden(els.clear) && kit.isHidden(els.write));
}

// The own review card, built again only when what it shows changed, so a control of it
// keeps the focus across the answers of other writes; a clear or a delete under way only marks its buttons (before, the card
// was built again when one began and when it failed, so the focused Delete was gone).
function renderOwn(entry, offers) {
    const els = entry.els;
    const mine = entry.mine;
    const reviewsOff = entry.reviewsOff || offers.reviewsOff;
    const show = !!(mine && mine.Text) && !entry.formOpen && !reviewsOff;
    const key = show ? JSON.stringify([mine.Text, mine.IsSpoiler === true, mine.Edited, mine.ReviewUpdatedUtc, mine.UpdatedUtc, offers.review, offers.own]) : '';
    if (key !== entry.ownKey) {
        entry.ownKey = key;
        entry.ownButtons = null;
        if (!show) {
            els.own.replaceChildren();
            kit.setHidden(els.own, true);
            return;
        }

        const edit = offers.review ? kit.button(kit.t('details.edit'), { className: 'jfur-own-edit', onClick: function () { openForm(entry); } }) : null;
        const remove = offers.own ? kit.button(kit.t('details.delete'), { className: 'jfur-own-delete', onClick: function () { onDelete(entry); } }) : null;
        entry.ownButtons = { edit: edit, remove: remove };
        // The member's own text is shown to them, a spoiler too, with its flag.
        els.own.replaceChildren(kit.h('div', { class: 'jfur-own-review' }, [
            kit.h('div', { class: 'jfur-own-head' }, [
                kit.h('span', { class: 'jfur-own-title', text: kit.t('details.yourReview') }),
                reviewTime(mine.ReviewUpdatedUtc || mine.UpdatedUtc, mine.Edited === true),
                mine.IsSpoiler === true ? kit.h('span', { class: 'jfur-spoiler-flag', text: kit.t('details.spoiler') }) : null
            ]),
            reviewText(mine.Text, false),
            edit || remove ? kit.h('div', { class: 'jfur-own-actions' }, [edit, remove]) : null
        ]));
        kit.setHidden(els.own, false);
    }

    if (entry.ownButtons) {
        setBusyControl(entry.ownButtons.edit, entry.busy);
        setBusyControl(entry.ownButtons.remove, entry.busy);
        if (entry.ownButtons.edit) {
            // A draft waiting: Edit reads "Continue editing".
            entry.ownButtons.edit.textContent = hasDraft(entry) ? kit.t('details.editDraft') : kit.t('details.edit');
        }
    }
}

// What the member's own rating shows for what they may do: the slider; while a clear or a delete is
// queued or under way the slider taking no input; NotARater read-only stars (renderStars, no tab
// stop: there is nothing to do with them) with "Rating is limited to selected members." beside them, in the reading
// order; NeedsPlay, ReadOnly and a reason the page doesn't know the disabled slider (aria-disabled, still a tab stop, so
// Tab and the D-pad reach it and hear its note, which is its description).
function renderRating(entry, offers) {
    const els = entry.els;
    const reason = offers.rate ? null : offers.reason;
    const readOnly = reason === 'NotARater';
    const stars = entry.mine && Number(entry.mine.Stars) > 0 ? Number(entry.mine.Stars) : 0;
    const state = reason ? 'reason:' + reason + (readOnly ? ':' + stars : '') : (entry.busy ? 'busy' : '');
    if (state === entry.sliderState) {
        return;
    }

    entry.sliderState = state;
    if (readOnly) {
        const active = document.activeElement;
        const hadFocus = !!active && (entry.slider.element.contains(active) || els.readOnly.contains(active));
        entry.slider.setDisabled({ reason: reason });
        els.readOnly.replaceChildren(
            renderStars(stars, { className: 'jfur-mine-stars' }),
            kit.h('span', { class: 'jfur-mine-note jfur-isolate', dir: 'auto', text: kit.t(readOnlyNoteKey(reason)) })
        );
        kit.setHidden(entry.slider.element, true);
        els.readOnly.setAttribute('tabindex', '-1');
        kit.setHidden(els.readOnly, false);
        if (hadFocus) {
            // The slider had the focus as it went: the read-only stars, in its place, take it.
            focusIfShown(els.readOnly);
        }

        return;
    }

    if (document.activeElement && els.readOnly.contains(document.activeElement)) {
        // Allowed again while the read-only stars have the focus: the slider, in their place, takes it.
        kit.setHidden(entry.slider.element, false);
        focusIfShown(entry.slider.slider);
    }

    kit.setHidden(els.readOnly, true);
    els.readOnly.removeAttribute('tabindex');
    els.readOnly.replaceChildren();
    kit.setHidden(entry.slider.element, false);
    entry.slider.setDisabled(reason ? { reason: reason, note: kit.t(readOnlyNoteKey(reason)) } : (entry.busy ? { reason: 'Busy' } : null));
}

function renderMine(entry) {
    const offers = detailsOffers(entry.can);
    renderRating(entry, offers);
    const reviewsOff = entry.reviewsOff || offers.reviewsOff;
    if (entry.formOpen) {
        // Reviews turned off, or the member may no longer write: the form stays, read-only, with its text, until the member
        // closes it (it once went, and the text with it); allowed again, it can be saved again.
        lockForm(entry, offers.review && !reviewsOff ? null : kit.t('details.formLocked'));
    }

    renderMineActions(entry);
    renderOwn(entry, offers);
}

// Load more: shown while the server has more rows, disabled with "Loading…" while a page loads. A
// button the member presses, never pressed for them when it scrolls into view (the kit's pager does that on a full page):
// in the middle of a details page that would push the cast and More like this away as the member scrolls.
function updatePager(entry) {
    const els = entry.els;
    const offers = detailsOffers(entry.can);
    const hasMore = !entry.reviewsOff && !offers.reviewsOff && entry.offset < entry.total;
    kit.setHidden(els.pager, !hasMore && !entry.loadingMore);
    kit.setHidden(els.more, !hasMore);
    els.more.disabled = !!entry.loadingMore;
    els.moreStatus.textContent = entry.loadingMore ? kit.t('details.loading') : '';
}

// The other members' rows: 'replace' builds them again (a new order), 'append' adds the rows not shown yet (Load more).
function renderList(entry, mode) {
    const els = entry.els;
    const offers = detailsOffers(entry.can);
    if (entry.reviewsOff || offers.reviewsOff) {
        els.list.replaceChildren();
        [els.toolbar, els.list, els.listMessage].forEach(function (node) {
            kit.setHidden(node, true);
        });
        entry.loadingMore = false;
        updatePager(entry);
        kit.setHidden(els.reviewsOff, !entry.reviewsOffSaid);
        return;
    }

    kit.setHidden(els.reviewsOff, true);
    let added = null;
    if (mode === 'replace' || mode === 'all') {
        els.list.replaceChildren.apply(els.list, entry.reviews.map(function (review) {
            return reviewRow(review, entry);
        }));
    } else if (mode === 'append') {
        const shown = {};
        Array.prototype.forEach.call(els.list.children, function (row) {
            shown[row.getAttribute('data-jfur-review')] = true;
        });
        entry.reviews.forEach(function (review) {
            if (!shown[detailsId(review.Id)]) {
                const row = reviewRow(review, entry);
                els.list.append(row);
                added = added || row;
            }
        });
    }

    kit.setHidden(els.list, entry.reviews.length === 0);
    kit.setHidden(els.toolbar, entry.total < 2 && entry.sort === DETAILS_SORTS[0]);
    updatePager(entry);
    return added;
}

function renderDetails(entry, listMode) {
    if (!entry.els || entry.dead) {
        return;
    }

    const offers = detailsOffers(entry.can);
    renderSummary(entry, offers);
    renderCopies(entry);
    renderMine(entry);
    if (listMode) {
        renderList(entry, listMode);
    }

    // The chip and the Rate button follow the summary and the section (50-chip.js).
    syncDetailsExtras();
}

// --------------------------------------------------------------------------------------------- the answers

// An item answer (GET Items, or a write's answer): Mine and Can always; the summary and the copies unless
// it is partial (IsPartial: they stay as shown, and the next view reads them again); the first page of the others'
// rows only for a read of the page's own (opts.reviews), since a member's own write never changes them. opts.stars puts
// the slider at the server's stars unless a change of the member's is waiting or being saved.
function applyItem(entry, dto, opts) {
    if (!dto || entry.dead) {
        return;
    }

    opts = opts || {};
    entry.mine = dto.Mine || null;
    if (dto.Can) {
        entry.can = dto.Can;
    }

    noticeReviewChanged(entry);
    if (dto.IsPartial === true) {
        entry.partial = true;
    } else {
        entry.partial = false;
        if (dto.Summary) {
            entry.summary = dto.Summary;
        }

        entry.copies = Array.isArray(dto.OtherCopies) ? dto.OtherCopies : [];
        if (opts.reviews && dto.Reviews) {
            entry.reviews = mergeReviews([], dto.Reviews.Items);
            entry.offset = (dto.Reviews.Items || []).length;
            entry.total = Math.max(entry.offset, Number(dto.Reviews.TotalRecordCount) || 0);
        }
    }

    if (opts.stars && entry.slider && !entry.slider.isPending() && !entry.slider.isSaving()) {
        const stars = entry.mine && Number(entry.mine.Stars) > 0 ? Number(entry.mine.Stars) : 0;
        if (entry.slider.savedValue() !== stars || entry.slider.value() !== stars) {
            entry.slider.setValue(stars);
        }
    }

    renderDetails(entry, opts.reviews ? 'all' : null);
}

// An open form after an answer: its starting point never moves (the review's own precondition, IfReviewUpdatedUtc or
// IfNoReview, is untouched by the page's own rating saves); once the answer shows another window changed the review
// (its text, its flag or its own time), the form says so, and Save gets the 409.
function noticeReviewChanged(entry) {
    const form = entry.form;
    if (!form || form.changedElsewhere) {
        return;
    }

    if (reviewChangedSince(form.base, entry.mine)) {
        form.changedElsewhere = true;
        if (!form.locked) {
            showNotice(form.notice, 'info', kit.t('details.changedNotice'));
        }
    }
}

// The signed-in member's id now (the API reads the token per call).
function detailsCurrentUser() {
    return ratingsUserKey();
}

// A queued write (or a read, quiet) of a member who is no longer signed in: never sent with the next member's session,
// and its answer never shown in their section. A write says once that it wasn't saved; a read says nothing.
function detailsUserChanged(entry, quiet) {
    const error = new Error('The signed-in member changed');
    error.userChanged = true;
    if (!quiet && !entry.userToastShown) {
        entry.userToastShown = true;
        kit.toast(kit.t('details.notSavedUserChanged'));
    }

    if (!entry.dead) {
        // What the section shows is the previous member's: it goes, and the scanner mounts the new member's.
        leaveDetails(entry);
    }

    return error;
}

// A read of the section (the item again, Reload, the 409's newer review, a page of rows): sent only while the member the
// section is for is the one signed in, and its answer used only while they still are (once, Reload and
// refreshDetails read under the next member's session and showed their row in the previous member's section).
function detailsRead(entry, call) {
    if (detailsCurrentUser() !== entry.userId) {
        return Promise.reject(detailsUserChanged(entry, true));
    }

    return call().then(function (answer) {
        if (detailsCurrentUser() !== entry.userId) {
            throw detailsUserChanged(entry, true);
        }

        return answer;
    });
}

// Every write of the section, in the order made (one at a time), each only while the member who made it is the one signed
// in (once, the queued writes and the slider's hand-off went out under the next member's session after a user change).
function enqueue(entry, task) {
    const run = entry.queue.then(function () {
        if (detailsCurrentUser() !== entry.userId) {
            throw detailsUserChanged(entry);
        }

        return task();
    });
    entry.queue = run.then(function () {}, function () {});
    return run;
}

// Reads the item again (after a write's 403, a delete's 204): the server decides what is shown; 403 or 404 removes the
// section.
function refreshDetails(entry) {
    return detailsRead(entry, function () {
        return ratingsApi.item(entry.itemId);
    }).then(function (dto) {
        applyItem(entry, dto, { stars: true });
        return true;
    }, function (e) {
        if (e && e.userChanged) {
            return false;
        }

        if (detailsFailure(e, 'read').kind === 'gone') {
            goneDetails(entry);
        } else {
            kit.report(e);
        }

        return false;
    });
}

// A write's answer of 403: read the item again; the section stays only when that read is a 200, and then the server's
// sentence says why the write didn't go.
function recheckDetails(entry, failure, place) {
    return refreshDetails(entry).then(function (ok) {
        if (ok && !entry.dead) {
            showNotice(place(), 'error', failure.message || kit.t('details.refused'), null);
        }
    });
}

function ratingFailed(entry, error, value) {
    if (error && error.userChanged) {
        // Never sent; its toast is said once.
        throw error;
    }

    const failure = detailsFailure(error, 'rating');
    if (entry.dead) {
        // The page was left with this change waiting (destroy's hand-off): say so once it fails for a reason of its own.
        if (failure.kind === 'retry' || failure.kind === 'invalid') {
            kit.toast(kit.t('details.ratingNotSaved', { reason: failure.message }));
        }

        throw error;
    }

    if (failure.kind === 'gone') {
        // Saved, or not, the section goes either way; no "couldn't save".
        goneDetails(entry);
        return;
    }

    if (failure.kind === 'recheck') {
        recheckDetails(entry, failure, function () {
            return entry.els && entry.els.mineMessage;
        });
        throw error;
    }

    showNotice(entry.els.mineMessage, 'error', kit.t('details.ratingNotSaved', { reason: failure.message }), failure.kind === 'retry' ? function () {
        retryRating(entry, value);
    } : null);
    throw error;
}

// The slider's onCommit: PUT Mine/Rating; the widget shows "Saving…", says "Saved" on success and puts
// the last saved value back on a failure (the promise rejects), which is when the page shows why, with Retry.
function saveRating(entry, value) {
    return enqueue(entry, function () {
        return ratingsApi.setRating(entry.itemId, value).then(function (dto) {
            if (!entry.dead) {
                showNotice(entry.els.mineMessage, null);
                applyItem(entry, dto);
            }
        }, function (e) {
            return ratingFailed(entry, e, value);
        });
    });
}

// Retry of a rating the server didn't take: the same value again; its answer is the server's value for the slider.
function retryRating(entry, value) {
    showNotice(entry.els.mineMessage, null);
    enqueue(entry, function () {
        return ratingsApi.setRating(entry.itemId, value);
    }).then(function (dto) {
        if (entry.dead) {
            return;
        }

        applyItem(entry, dto, { stars: true });
        entry.slider.announce(value === 1 ? kit.t('stars.savedOne') : kit.t('stars.saved', { stars: formatStars(value) }));
    }, function (e) {
        try {
            ratingFailed(entry, e, value);
        } catch (ignored) {
            // shown by ratingFailed
        }
    });
}

// Clear: only the stars (DELETE Mine/Rating); with a review it asks first. A change not handed on yet
// is dropped; one being saved goes first (the queue), then the clear.
function onClear(entry) {
    if (entry.busy || entry.dead) {
        return;
    }

    const text = !!(entry.mine && entry.mine.Text);
    const asked = text
        ? detailsConfirm({ title: kit.t('details.clearTitle'), message: kit.t('details.clearMessage'), confirm: kit.t('details.clearConfirm') })
        : Promise.resolve(true);
    asked.then(function (yes) {
        if (!yes || entry.dead) {
            return;
        }

        const slider = entry.slider;
        const onServer = !!(entry.mine && Number(entry.mine.Stars) > 0) || slider.isSaving();
        slider.setValue(slider.savedValue());
        if (!onServer) {
            // Nothing saved yet: the waiting change is all there was.
            slider.setValue(0);
            slider.announce(kit.t('details.cleared'));
            renderMineActions(entry);
            focusIfShown(slider.slider);
            return;
        }

        runWrite(entry, 'clear', function () {
            return ratingsApi.clearRating(entry.itemId);
        }, function (dto) {
            slider.setValue(0);
            applyItem(entry, dto);
            slider.announce(kit.t('details.cleared'));
            focusIfShown(slider.slider);
        }, function () {
            return entry.els.mineMessage;
        }, function () {
            onClear(entry);
        });
    });
}

// Delete on the own review card: the rating and the review (DELETE Mine, 204), after asking.
function onDelete(entry) {
    if (entry.busy || entry.dead) {
        return;
    }

    detailsConfirm({ title: kit.t('details.deleteTitle'), message: kit.t('details.deleteMessage'), confirm: kit.t('details.deleteConfirm') }).then(function (yes) {
        if (!yes || entry.dead) {
            return;
        }

        entry.slider.setValue(entry.slider.savedValue());
        runWrite(entry, 'delete', function () {
            return ratingsApi.deleteMine(entry.itemId);
        }, function () {
            entry.mine = null;
            entry.slider.setValue(0);
            renderDetails(entry, null);
            announceDetails(entry, kit.t('details.deleted'));
            focusIfShown(entry.slider.slider);
            return refreshDetails(entry);
        }, function () {
            return entry.els.mineMessage;
        }, function () {
            onDelete(entry);
        });
    });
}

// A clear or a delete through the queue, with the section's controls disabled meanwhile; a failure as detailsFailure says.
function runWrite(entry, op, call, done, place, retry) {
    entry.busy = true;
    showNotice(place(), null);
    renderDetails(entry, null);
    return enqueue(entry, call).then(function (answer) {
        entry.busy = false;
        if (!entry.dead) {
            return done(answer);
        }
    }, function (e) {
        entry.busy = false;
        if (entry.dead) {
            return;
        }

        const failure = detailsFailure(e, op);
        renderDetails(entry, null);
        if (failure.kind === 'gone') {
            goneDetails(entry);
        } else if (failure.kind === 'recheck') {
            recheckDetails(entry, failure, place);
        } else {
            showNotice(place(), 'error', kit.t(op === 'delete' ? 'details.deleteFailed' : 'details.clearFailed', { reason: failure.message }), failure.kind === 'retry' ? retry : null);
            keepFocusInSection(entry, place());
        }
    });
}

// After a failed write the focus stays on the control that had it; should it have gone to the page's body all the
// same, it goes to the notice's Try again, so the TV layout's D-pad and Enter stay in the section.
function keepFocusInSection(entry, place) {
    const active = document.activeElement;
    if (entry.dead || (active && active !== document.body && active.isConnected)) {
        return;
    }

    focusIfShown(place && place.querySelector('button'));
}

// ----------------------------------------------------------------------------------------------- the review form

// "Write a review" or Edit: an inline form, its text area as long as Capabilities.MaxReviewLength
// allows, a live counter (the text area's description), "Contains spoilers" where the server keeps the flag
// (AllowSpoilerFlag; an edit starts from the row's flag), Save and Cancel. Its starting point (base) is the member's row as
// it opened on. A draft of the member's for the item is put back into it, with "Your unsaved text was
// restored." and Discard; when the review changed in another window since the draft was written, the draft's own
// starting point stays, so Save gets the 409 and its Reload and Overwrite. Nothing while a clear or a delete is under way
// (once, Edit and Write a review, marked aria-disabled, still opened the form during a Delete).
function openForm(entry) {
    const offers = detailsOffers(entry.can);
    if (entry.dead || entry.formOpen || entry.busy || !offers.review || entry.reviewsOff) {
        return;
    }

    const max = Math.max(1, Math.floor(Number(entry.caps && entry.caps.MaxReviewLength) || RATINGS_DEFAULT_CAPABILITIES.MaxReviewLength));
    entry.formOpen = true;
    const counterId = detailsNextId('review-counter');
    // The counter is the text area's description: heard with it, never announced on every key.
    const textarea = kit.textarea({ class: 'jfur-review-input', maxlength: String(max), rows: '5', dir: 'auto', 'aria-describedby': counterId });
    const counter = kit.h('div', { id: counterId, class: 'jfur-review-counter', 'aria-live': 'off' });
    const allowFlag = !(entry.caps && entry.caps.AllowSpoilerFlag === false);
    const spoiler = allowFlag ? kit.h('input', { type: 'checkbox', class: 'jfur-review-spoiler', id: detailsNextId('review-spoiler') }) : null;
    const restored = kit.h('div', { class: 'jfur-review-restored' });
    const notice = kit.h('div', { class: 'jfur-review-notice' });
    const error = kit.h('div', { class: 'jfur-review-error' });
    const aside = kit.h('div', { class: 'jfur-review-kept' });
    [restored, notice, error, aside].forEach(function (node) {
        kit.setHidden(node, true);
    });
    const save = kit.button(kit.t('details.save'), { variant: 'primary', className: 'jfur-review-save', onClick: function () { sendReview(entry, false); } });
    const cancel = kit.button(kit.t('details.cancel'), { className: 'jfur-review-cancel', onClick: function () { cancelForm(entry); } });
    const form = kit.h('div', { class: 'jfur-review-form' }, [
        kit.field(kit.t('details.yourReview'), textarea),
        counter,
        spoiler ? kit.h('label', { class: 'jfur-review-spoiler-field', for: spoiler.id }, [spoiler, kit.h('span', { text: kit.t('details.spoilerCheck') })]) : null,
        restored,
        notice,
        error,
        aside,
        kit.h('div', { class: kit.cls('actions') + ' jfur-review-actions' }, [cancel, save])
    ]);
    const base = reviewBase(entry.mine);
    entry.form = {
        element: form,
        textarea: textarea,
        counter: counter,
        spoiler: spoiler,
        restored: restored,
        notice: notice,
        error: error,
        asidePlace: aside,
        save: save,
        cancel: cancel,
        max: max,
        busy: false,
        // The starting point: the review's precondition comes from it; only Reload and Discard move it.
        base: base,
        changedElsewhere: false,
        locked: null,
        // The texts kept beside the form after a Reload: [{ text, mine }] (mine: the member's own words), never
        // dropped through any number of 409s until the form is saved or the member discards them.
        kept: [],
        // The member's row as the 409 showed it: Overwrite sends its review's time; null until it is read.
        conflict: null,
        draft: draftName(entry.userId, entry.itemId),
        draftTimer: null,
        // A Cancel refused once because this browser can't keep the draft: the next one closes.
        cancelWarned: false
    };
    textarea.value = (base && base.Text) || '';
    if (spoiler) {
        spoiler.checked = !!(base && base.IsSpoiler);
        spoiler.addEventListener('change', function () {
            scheduleDraft(entry);
        });
    }

    textarea.addEventListener('input', function () {
        updateForm(entry);
        scheduleDraft(entry);
    });
    restoreDraft(entry);
    updateForm(entry);
    renderKept(entry);
    entry.els.formHost.replaceChildren(form);
    renderMine(entry);
    focusIfShown(textarea);
}

function updateForm(entry) {
    const form = entry.form;
    if (!form) {
        return;
    }

    const text = String(form.textarea.value || '');
    const locked = !!form.locked;
    form.counter.textContent = kit.t('details.counter', { count: text.length, max: form.max });
    form.save.disabled = form.busy || locked || text.trim() === '';
    kit.setHidden(form.save, locked);
    form.cancel.disabled = form.busy;
    form.cancel.textContent = locked ? kit.t('details.close') : kit.t('details.cancel');
    // Read-only, never disabled, while locked: the text can still be selected and copied.
    form.textarea.readOnly = locked;
    form.textarea.disabled = form.busy;
    if (form.spoiler) {
        form.spoiler.disabled = form.busy || locked;
    }

    Array.prototype.forEach.call(form.element.querySelectorAll('.jfur-review-kept button, .jfur-review-error button, .jfur-review-restored button'), function (button) {
        button.disabled = form.busy;
    });
    form.save.textContent = form.busy ? kit.t('details.saving') : kit.t('details.save');
}

// The open form while the member may not write a review: read-only, its text kept, Save gone and Cancel "Close", with
// why (message); null makes it a form again once writing is allowed. Its focus stays where it was, and its draft is kept.
function lockForm(entry, message) {
    const form = entry.form;
    if (!form || (form.locked || null) === (message || null)) {
        return;
    }

    form.locked = message || null;
    showNotice(form.notice, 'info', message || (form.changedElsewhere ? kit.t('details.changedNotice') : null));
    if (message) {
        showNotice(form.error, null);
    }

    updateForm(entry);
}

// Cancel: the form goes and what it holds stays as the draft (closeForm). Where this browser refuses to keep it (no
// storage, full or blocked), the first Cancel leaves the form open and says so, since closing it would lose the member's
// words and every text kept beside the form; a second Cancel closes it. A member whose session
// expired keeps no drafts, and their Cancel closes the form as before.
function cancelForm(entry) {
    const form = entry.form;
    if (!form) {
        return;
    }

    const text = String(form.textarea.value || '');
    const spoiler = !!(form.spoiler && form.spoiler.checked);
    if (!form.cancelWarned && !entry.noDrafts && detailsCurrentUser() === entry.userId && draftDiffers(text, spoiler, form.kept, form.base) && !saveDraft(entry, form)) {
        form.cancelWarned = true;
        showNotice(form.error, 'error', kit.t('details.cancelNotKept'));
        focusIfShown(form.textarea);
        return;
    }

    closeForm(entry, true);
}

// The form goes; with refocus (Cancel, a save) the focus returns to Write a review or Edit, else the slider. What it holds
// stays as the member's draft (Cancel never throws text away: Discard, in the form, does).
function closeForm(entry, refocus) {
    if (!entry.formOpen) {
        return;
    }

    saveDraft(entry, entry.form);
    entry.formOpen = false;
    entry.form = null;
    entry.els.formHost.replaceChildren();
    renderMine(entry);
    if (refocus) {
        const edit = entry.els.own.querySelector('.jfur-own-edit');
        if (!focusIfShown(edit) && !focusIfShown(entry.els.write)) {
            focusIfShown(entry.slider.slider);
        }
    }
}

// -------------------------------------------------------------------------------------------------------- drafts
// The member's unsaved review of an item: saved on every input (DETAILS_DRAFT_MS after the
// last, and at once when the form closes, the section goes, the page is hidden or left), in the kit's storage under
// "jfur:draft:<userId>:<itemId>" (localStorage, each access in try/catch: without storage the page works and keeps no
// draft), with the form's flag, its starting point, the texts kept beside it and when it was saved; put back when the form
// opens again; cleared by a save that went through or by Discard; every draft of a member is removed when they sign out
// (and those of any other member when one signs in) and when their session expires, and any draft saved more than 30 days
// ago goes at the next start. Only ever written for the member the section is for, while they
// are signed in, and a write the browser refuses leaves no older draft behind. Never sent anywhere.

function readStoredDraft(name) {
    const draft = readDraft(kit.storage.get(name, null));
    if (draft && draftExpired(draft)) {
        kit.storage.remove(name);
        return null;
    }

    return draft;
}

function hasDraft(entry) {
    const draft = readStoredDraft(draftName(entry.userId, entry.itemId));
    return !!draft && draftDiffers(draft.text, draft.spoiler, draft.kept, reviewBase(entry.mine));
}

function scheduleDraft(entry) {
    const form = entry.form;
    if (!form) {
        return;
    }

    clearTimeout(form.draftTimer);
    form.draftTimer = setTimeout(function () {
        saveDraft(entry, form);
    }, DETAILS_DRAFT_MS);
}

// Writes what the form holds now as the draft, or removes the draft when it holds nothing to keep. Nothing for a member
// who is no longer the one signed in (once, a save that failed after a sign-out wrote the draft back), or once their session
// expired; a write the browser refuses removes the draft there was (once, an older draft was later put back as
// "your unsaved text"). Returns whether the draft is stored now.
function saveDraft(entry, form) {
    if (!form) {
        return false;
    }

    clearTimeout(form.draftTimer);
    form.draftTimer = null;
    if (entry.noDrafts || detailsCurrentUser() !== entry.userId) {
        return false;
    }

    const text = String(form.textarea.value || '');
    const spoiler = !!(form.spoiler && form.spoiler.checked);
    if (!draftDiffers(text, spoiler, form.kept, form.base)) {
        kit.storage.remove(form.draft);
        return false;
    }

    if (!kit.storage.set(form.draft, { v: 2, text: text, spoiler: spoiler, base: form.base, kept: form.kept, savedUtc: new Date().toISOString() })) {
        kit.storage.remove(form.draft);
        return false;
    }

    return true;
}

// The draft put back into a form just opened. One that holds what the row has now (saved since, here or elsewhere) goes.
function restoreDraft(entry) {
    const form = entry.form;
    const draft = readStoredDraft(form.draft);
    if (!draft) {
        return;
    }

    if (!draftDiffers(draft.text, draft.spoiler, draft.kept, reviewBase(entry.mine))) {
        kit.storage.remove(form.draft);
        return;
    }

    form.textarea.value = draft.text;
    if (form.spoiler) {
        form.spoiler.checked = draft.spoiler;
    }

    form.kept = draft.kept.slice();
    if (reviewChangedSince(draft.base, entry.mine)) {
        // Written against a review that has changed since: the draft's starting point stays, so Save gets the 409.
        form.base = draft.base;
        form.changedElsewhere = true;
        showNotice(form.notice, 'info', kit.t('details.draftChanged'));
    }

    form.restored.replaceChildren(kit.notice({ kind: 'info', text: kit.t('details.draftRestored'), action: { label: kit.t('details.discard'), onClick: function () { discardDraft(entry); } } }));
    form.restored.querySelector('button').classList.add('jfur-draft-discard');
    kit.setHidden(form.restored, false);
}

// Discard: the draft goes, and the form holds the review as the server has it now.
function discardDraft(entry) {
    const form = entry.form;
    if (!form || form.busy) {
        return;
    }

    kit.storage.remove(form.draft);
    clearTimeout(form.draftTimer);
    form.base = reviewBase(entry.mine);
    form.changedElsewhere = false;
    form.kept = [];
    form.conflict = null;
    form.textarea.value = (form.base && form.base.Text) || '';
    if (form.spoiler) {
        form.spoiler.checked = !!(form.base && form.base.IsSpoiler);
    }

    showNotice(form.restored, null);
    showNotice(form.notice, form.locked ? 'info' : null, form.locked);
    showNotice(form.error, null);
    updateForm(entry);
    renderKept(entry);
    focusIfShown(form.textarea);
}

// A form's draft at once, wherever it is: the section going, the page hidden or left (90-main.js).
function flushDetailsDraft() {
    const entry = detailsState.entry;
    if (entry && entry.form) {
        saveDraft(entry, entry.form);
    }
}

// A member signed in (id), or none: every draft of anyone else goes, those of the member who signed out among them.
function removeOtherDrafts(userId) {
    const keep = userId ? draftName(userId) : null;
    kit.storage.names('draft:').forEach(function (name) {
        if (!keep || name.indexOf(keep) !== 0) {
            kit.storage.remove(name);
        }
    });
}

// Every draft of a member (their session expired).
function removeDraftsOf(userId) {
    if (!userId) {
        return;
    }

    const start = draftName(userId);
    kit.storage.names('draft:').forEach(function (name) {
        if (name.indexOf(start) === 0) {
            kit.storage.remove(name);
        }
    });
}

// At start: every draft saved more than 30 days ago goes, and anything under the drafts' name that isn't one;
// a draft without a time of its own is dated now, so it is kept for 30 days from here.
function purgeExpiredDrafts() {
    const now = Date.now();
    kit.storage.names('draft:').forEach(function (name) {
        const stored = kit.storage.get(name, null);
        const draft = readDraft(stored);
        if (draftExpired(draft, now)) {
            kit.storage.remove(name);
        } else if (!draft.savedUtc) {
            kit.storage.set(name, Object.assign({}, stored, { savedUtc: new Date(now).toISOString() }));
        }
    });
}

// The kit's session-expired: the drafts of the member whose session it was go, and the open section writes no
// more of them (a new sign-in mounts a new section).
function onDetailsSessionExpired() {
    const entry = detailsState.entry;
    if (entry) {
        entry.noDrafts = true;
        if (entry.form) {
            clearTimeout(entry.form.draftTimer);
        }

        removeDraftsOf(entry.userId);
    }

    removeDraftsOf(detailsCurrentUser());
}

// The page shown again: a member told they must play the item first, or that rating is limited to
// selected members, may have played it elsewhere or been let in meanwhile, so the item is read again.
function refreshDetailsOnReturn() {
    const entry = detailsState.entry;
    if (!entry || entry.dead || entry.state !== 'shown' || entry.busy) {
        return;
    }

    const offers = detailsOffers(entry.can);
    if (!offers.rate && DETAILS_RECHECK_REASONS.indexOf(offers.reason) >= 0) {
        refreshDetails(entry);
    }
}

// ---------------------------------------------------------------------------------------------------------- the 409

// The texts kept beside the form after a Reload: the member's own words, each in its own group,
// while the text area holds the review saved in another window, or that review once "Use my text" put theirs in the text
// area. A second Reload keeps the text area's words too when they are the member's (they differ from the review the form
// last took from the server), so none of the member's texts is ever dropped, through any number of 409s; only the
// other window's older review, which the newer one replaces in the text area, goes. All of them stay in view (and in the
// draft) until the form is saved or the member discards them.
function renderKept(entry) {
    const form = entry.form;
    if (!form) {
        return;
    }

    if (form.kept.length === 0) {
        form.asidePlace.replaceChildren();
        kit.setHidden(form.asidePlace, true);
        return;
    }

    form.asidePlace.replaceChildren.apply(form.asidePlace, form.kept.map(function (kept, index) {
        const labelId = detailsNextId('review-aside');
        const use = kit.button(kit.t(kept.mine ? 'details.useMine' : 'details.useTheirs'), { className: 'jfur-aside-use', onClick: function () { useKept(entry, index); } });
        use.setAttribute('data-jfur-kept', String(index));
        return kit.h('div', { class: 'jfur-review-aside', role: 'group', 'aria-labelledby': labelId }, [
            kit.h('div', { id: labelId, class: 'jfur-aside-label', text: kit.t(kept.mine ? 'details.asideMine' : 'details.asideTheirs') }),
            isolatedText('div', 'jfur-aside-text', kept.text),
            kit.h('div', { class: kit.cls('actions') }, [use])
        ]);
    }));
    kit.setHidden(form.asidePlace, false);
    updateForm(entry);
}

// "Use my text" / "Use that text": the kept text goes into the text area, and what the text area held takes its place in
// the list (the member's words when they differ from the review the form last took from the server, else that review).
function useKept(entry, index) {
    const form = entry.form;
    const chosen = form && form.kept[index];
    if (!chosen || form.busy || form.locked) {
        return;
    }

    const current = String(form.textarea.value || '');
    form.textarea.value = chosen.text;
    form.kept.splice(index, 1);
    keepText(form.kept, current, current !== String((form.base && form.base.Text) || ''));
    updateForm(entry);
    renderKept(entry);
    saveDraft(entry, form);
    const buttons = form.asidePlace.querySelectorAll('.jfur-aside-use');
    if (!focusIfShown(buttons[Math.min(index, buttons.length - 1)])) {
        focusIfShown(form.textarea);
    }
}

// A 409: the review was changed in another window, so this text wasn't saved: the form keeps it, says so, and offers
// Reload (the newer review in the text area, this text beside it) and Overwrite (this text over the newer one). The newer
// review is read at once and shown under the sentence, read-only, isolated and spoiler-aware, so
// the member sees what Overwrite replaces; Overwrite sends that row's review time, so a change made after it was shown is
// a 409 again, with that newer text shown. The focus goes to Reload.
function showConflict(entry) {
    const form = entry.form;
    if (!form) {
        return;
    }

    form.conflict = null;
    const notice = kit.notice({ kind: 'error', text: kit.t('details.conflict'), action: { label: kit.t('details.reload'), onClick: function () { reloadForm(entry); } } });
    notice.querySelector('button').classList.add('jfur-conflict-reload');
    const overwrite = kit.button(kit.t('details.overwrite'), { className: kit.cls('notice-action') + ' jfur-conflict-overwrite', onClick: function () { sendReview(entry, true); } });
    kit.setHidden(overwrite, true);
    notice.append(overwrite);
    const shown = kit.h('div', { class: 'jfur-conflict-review' });
    form.error.replaceChildren(notice, shown);
    form.conflictEls = { overwrite: overwrite, shown: shown };
    kit.setHidden(form.error, false);
    updateForm(entry);
    focusIfShown(notice.querySelector('.jfur-conflict-reload'));
    readConflict(entry, form);
}

// Reads the member's row as it is now for the 409's panel; Overwrite shows once it is there.
function readConflict(entry, form) {
    const els = form.conflictEls;
    els.shown.replaceChildren(kit.h('p', { class: 'jfur-conflict-status', role: 'status', text: kit.t('details.conflictReading') }));
    kit.setHidden(els.overwrite, true);
    detailsRead(entry, function () {
        return ratingsApi.item(entry.itemId);
    }).then(function (dto) {
        if (entry.dead || entry.form !== form || form.conflictEls !== els) {
            return;
        }

        applyItem(entry, dto, { stars: true });
        if (entry.form !== form) {
            return;
        }

        const mine = dto && dto.Mine ? dto.Mine : null;
        form.conflict = { base: reviewBase(mine) };
        if (mine && mine.Text) {
            const label = detailsNextId('conflict-review');
            const text = reviewText(mine.Text, mine.IsSpoiler === true);
            text.classList.add('jfur-conflict-text');
            els.shown.replaceChildren(kit.h('div', { class: 'jfur-conflict-newer', role: 'group', 'aria-labelledby': label }, [
                kit.h('div', { id: label, class: 'jfur-conflict-label', text: kit.t('details.conflictNewer') }),
                text
            ]));
        } else {
            els.shown.replaceChildren(kit.h('p', { class: 'jfur-conflict-label', text: kit.t('details.conflictDeleted') }));
        }

        kit.setHidden(els.overwrite, false);
        updateForm(entry);
    }, function (e) {
        if ((e && e.userChanged) || entry.dead || entry.form !== form || form.conflictEls !== els) {
            return;
        }

        const failure = detailsFailure(e, 'read');
        if (failure.kind === 'gone') {
            goneDetails(entry);
            return;
        }

        // Without the row as it is now, Overwrite has nothing to send against: Reload, or read it again.
        const retry = kit.notice({ kind: 'error', text: kit.t('details.conflictUnread', { reason: failure.message }), action: { label: kit.t('details.retry'), onClick: function () { readConflict(entry, form); } } });
        els.shown.replaceChildren(retry);
        updateForm(entry);
    });
}

// Reload: the item read again; the text area takes the review as it is now (its flag too), the member's words go beside
// it with any kept before, and the starting point is the row as it is now.
function reloadForm(entry) {
    const form = entry.form;
    if (!form || form.busy || entry.dead) {
        return;
    }

    form.busy = true;
    showNotice(form.error, null);
    form.conflict = null;
    form.conflictEls = null;
    updateForm(entry);
    detailsRead(entry, function () {
        return ratingsApi.item(entry.itemId);
    }).then(function (dto) {
        form.busy = false;
        if (entry.dead || entry.form !== form) {
            return;
        }

        applyItem(entry, dto, { stars: true });
        if (entry.form !== form) {
            return;
        }

        const mine = entry.mine;
        const current = String(form.textarea.value || '');
        const newer = mine && mine.Text ? String(mine.Text) : '';
        if (current !== String((form.base && form.base.Text) || '')) {
            // The text area holds the member's words (typed, a draft, or a kept text they chose): kept, with the others.
            keepText(form.kept, current, true);
        }

        // The other window's older review, kept beside the form, is the newer one's now; a kept text the newer review is
        // word for word is in the text area.
        form.kept = form.kept.filter(function (kept) {
            return kept.mine && kept.text !== newer;
        });
        form.textarea.value = newer;
        if (form.spoiler) {
            form.spoiler.checked = !!(mine && mine.Text && mine.IsSpoiler);
        }

        form.base = reviewBase(mine);
        form.changedElsewhere = false;
        showNotice(form.restored, null);
        showNotice(form.notice, 'info', form.locked || kit.t(newer ? 'details.reloaded' : 'details.reloadedNone'));
        updateForm(entry);
        renderKept(entry);
        saveDraft(entry, form);
        focusIfShown(form.textarea);
    }, function (e) {
        form.busy = false;
        if ((e && e.userChanged) || entry.dead || entry.form !== form) {
            return;
        }

        updateForm(entry);
        const failure = detailsFailure(e, 'read');
        if (failure.kind === 'gone') {
            goneDetails(entry);
            return;
        }

        showNotice(form.error, 'error', kit.t('details.reloadFailed', { reason: failure.message }), function () {
            reloadForm(entry);
        });
        keepFocusInSection(entry, form.error);
    });
}

function formFailed(entry, error) {
    const failure = detailsFailure(error, 'review');
    if (failure.kind === 'gone') {
        goneDetails(entry);
        return;
    }

    const place = function () {
        return entry.form ? entry.form.error : entry.els.mineMessage;
    };
    if (failure.kind === 'recheck') {
        // Reviews or ratings turned off, or the member left out of WhoCanRate since: the read decides what stays.
        recheckDetails(entry, failure, place);
        return;
    }

    if (failure.kind === 'conflict' && entry.form) {
        showConflict(entry);
        return;
    }

    showNotice(place(), 'error', kit.t('details.reviewNotSaved', { reason: failure.message }), failure.kind === 'retry' ? function () {
        sendReview(entry, false);
    } : null);
    if (entry.form) {
        focusIfShown(entry.form.textarea);
    }
}

// Save, or Overwrite (overwrite): PUT Mine/Review with the text, the form's flag and the review's own precondition
// (reviewBody): Save's from the form's starting point (the review as the form found it: its time, or "no review
// yet"), which the page's own rating saves never move and another window's review never becomes; Overwrite's from
// the row the 409 showed, so a change made after it was shown is a 409 again. A save that went through clears
// the draft and closes the form, unless other unsaved texts of the member's were kept beside it: then the form stays
// open on the review as saved, its new starting point, with those texts beside it and in the draft (a Save
// of one of them once dropped the others without a word).
function sendReview(entry, overwrite) {
    const form = entry.form;
    if (!form || form.busy || form.locked || entry.dead || (overwrite && !form.conflict)) {
        return;
    }

    const text = String(form.textarea.value || '');
    if (text.trim() === '') {
        return;
    }

    const spoiler = !!(form.spoiler && form.spoiler.checked);
    const base = overwrite ? form.conflict.base : form.base;
    form.busy = true;
    showNotice(form.error, null);
    form.conflictEls = null;
    updateForm(entry);
    saveDraft(entry, form);
    enqueue(entry, function () {
        return ratingsApi.setReview(entry.itemId, reviewBody(text, spoiler, base));
    }).then(function (dto) {
        form.busy = false;
        clearTimeout(form.draftTimer);
        kit.storage.remove(form.draft);

        // The member's other unsaved texts (never the other window's review, which this save replaced as they chose).
        const saved = dto && dto.Mine ? String(dto.Mine.Text || '') : text;
        const others = form.kept.filter(function (k) {
            return k.mine && k.text !== text && k.text !== saved;
        });
        if (entry.dead) {
            // The page was left while it was saved: those texts wait in the draft for the next Edit.
            if (others.length > 0 && !entry.noDrafts && detailsCurrentUser() === entry.userId) {
                const mineNow = reviewBase((dto && dto.Mine) || null);
                kit.storage.set(form.draft, { v: 2, text: (mineNow && mineNow.Text) || saved, spoiler: !!(mineNow && mineNow.IsSpoiler), base: mineNow, kept: others, savedUtc: new Date().toISOString() });
            }

            return;
        }

        if (entry.form === form && others.length > 0) {
            keepOthersAfterSave(entry, form, dto, others);
            return;
        }

        if (entry.form === form) {
            entry.formOpen = false;
            entry.form = null;
            entry.els.formHost.replaceChildren();
        }

        applyItem(entry, dto);
        announceDetails(entry, kit.t('details.reviewSaved'));
        const edit = entry.els.own.querySelector('.jfur-own-edit');
        if (!focusIfShown(edit)) {
            focusIfShown(entry.slider.slider);
        }
    }, function (e) {
        form.busy = false;
        if (e && e.userChanged) {
            return;
        }

        if (entry.dead) {
            // The page was left while it was saved: said, as a rating's is, whatever the reason but the item gone, with
            // the draft kept for the next time the form opens; nothing at all once the member who wrote it is no longer the
            // one signed in (once, the draft was written back after the sign-out had removed it, with a toast for them).
            const failure = detailsFailure(e, 'review');
            if (failure.kind !== 'gone' && detailsCurrentUser() === entry.userId) {
                const kept = saveDraft(entry, form);
                kit.toast(kit.t(kept ? 'details.reviewNotSavedKept' : 'details.reviewNotSaved', { reason: failure.message || kit.t('details.refused') }));
            }

            return;
        }

        updateForm(entry);
        formFailed(entry, e);
    });
}

// A save that went through while other texts were kept beside the form (sendReview): the review as saved is the form's
// text and starting point (so Save again is no 409, and the answer is no change from elsewhere), the other texts stay
// beside it and in the draft, and the member hears that they are still there, with Discard to let them go (then Cancel
// closes the form with nothing kept).
function keepOthersAfterSave(entry, form, dto, others) {
    const mineNow = (dto && dto.Mine) || null;
    form.base = reviewBase(mineNow);
    form.changedElsewhere = false;
    form.conflict = null;
    form.conflictEls = null;
    form.cancelWarned = false;
    form.kept = others;
    form.textarea.value = (form.base && form.base.Text) || '';
    if (form.spoiler) {
        form.spoiler.checked = !!(form.base && form.base.IsSpoiler);
    }

    showNotice(form.error, null);
    showNotice(form.notice, form.locked ? 'info' : null, form.locked);
    form.restored.replaceChildren(kit.notice({ kind: 'info', text: kit.t('details.reviewSavedKept'), action: { label: kit.t('details.discard'), onClick: function () { discardDraft(entry); } } }));
    form.restored.querySelector('button').classList.add('jfur-draft-discard');
    kit.setHidden(form.restored, false);
    applyItem(entry, dto);
    updateForm(entry);
    renderKept(entry);
    saveDraft(entry, form);
    announceDetails(entry, kit.t('details.reviewSavedKept'));
    focusIfShown(form.textarea);
}

// ----------------------------------------------------------------------------------------------------- the list

// Load more: the next page of the others' rows in the order chosen; its 403 means reviews were turned
// off since the page was read ("Reviews are turned off on this server.", the list and the form gone, no Retry).
function loadMore(entry) {
    if (entry.dead || entry.loadingMore || entry.reviewsOff || entry.offset >= entry.total) {
        return;
    }

    const generation = entry.listGeneration;
    const hadFocus = document.activeElement && entry.els.pager.contains(document.activeElement);
    entry.loadingMore = true;
    showNotice(entry.els.listMessage, null);
    updatePager(entry);
    detailsRead(entry, function () {
        return ratingsApi.reviews(entry.itemId, { startIndex: entry.offset, limit: detailsPageSize(entry), sort: entry.sort });
    }).then(function (page) {
        if (entry.dead || generation !== entry.listGeneration) {
            return;
        }

        const items = (page && page.Items) || [];
        entry.loadingMore = false;
        entry.reviews = mergeReviews(entry.reviews, items);
        entry.offset += items.length;
        entry.total = items.length === 0 ? entry.offset : Math.max(entry.offset, Number(page.TotalRecordCount) || 0);
        const added = renderList(entry, 'append');
        if (hadFocus && !focusIfShown(entry.els.more) && added) {
            // The last page: Load more went, so the focus goes to the first row it brought (no tab stop of its own).
            added.focus();
        }
    }, function (e) {
        if (entry.dead || generation !== entry.listGeneration) {
            return;
        }

        entry.loadingMore = false;
        listFailed(entry, e, function () {
            loadMore(entry);
        });
    });
}

function listFailed(entry, error, retry) {
    const failure = detailsFailure(error, 'more');
    if (failure.kind === 'gone') {
        goneDetails(entry);
        return;
    }

    if (failure.kind === 'reviewsOff') {
        const hadFocus = document.activeElement && entry.els.reviews.contains(document.activeElement);
        entry.reviewsOff = true;
        entry.reviewsOffSaid = true;
        renderDetails(entry, 'all');
        if (hadFocus) {
            focusIfShown(entry.slider.slider);
        }

        return;
    }

    renderList(entry, null);
    showNotice(entry.els.listMessage, 'error', kit.t('details.moreFailed', { reason: failure.message }), retry);
}

// A new order: the first page again in that order.
function changeSort(entry, sort) {
    if (entry.dead || DETAILS_SORTS.indexOf(sort) < 0) {
        return;
    }

    entry.sort = sort;
    entry.listGeneration++;
    const generation = entry.listGeneration;
    const hadFocus = document.activeElement === entry.els.sort;
    entry.loadingMore = true;
    entry.els.sort.disabled = true;
    showNotice(entry.els.listMessage, null);
    updatePager(entry);
    return detailsRead(entry, function () {
        return ratingsApi.reviews(entry.itemId, { startIndex: 0, limit: detailsPageSize(entry), sort: sort });
    }).then(function (page) {
        if (entry.dead || generation !== entry.listGeneration) {
            return;
        }

        const items = (page && page.Items) || [];
        entry.loadingMore = false;
        entry.els.sort.disabled = false;
        entry.reviews = mergeReviews([], items);
        entry.offset = items.length;
        entry.total = Math.max(entry.offset, Number(page.TotalRecordCount) || 0);
        renderList(entry, 'replace');
        if (hadFocus) {
            // The select was disabled while the page loaded, which took the focus from it.
            focusIfShown(entry.els.sort);
        }
    }, function (e) {
        if (entry.dead || generation !== entry.listGeneration) {
            return;
        }

        entry.loadingMore = false;
        entry.els.sort.disabled = false;
        listFailed(entry, e, function () {
            changeSort(entry, sort);
        });
    });
}

// --------------------------------------------------------------------------------------------- mount and unmount

function insertDetails(entry) {
    if (!entry.els || entry.els.section.isConnected) {
        return;
    }

    const place = sectionPlace(entry.page);
    if (place) {
        place.parent.insertBefore(entry.els.section, place.before);
        syncDetailsExtras();
    }
}

// Unmounts: the slider hands a waiting change on (saved once, after the save in flight) and goes inert, the section
// goes. A write still in the queue finishes, and its answer is dropped.
function teardownDetails(entry) {
    if (entry.dead) {
        return;
    }

    entry.dead = true;
    // An open form's text is the member's draft: Back on the TV layout, another item, another view.
    entry.draftKept = !!entry.form && saveDraft(entry, entry.form);
    if (entry.slider) {
        entry.slider.destroy();
    }

    if (entry.els) {
        entry.els.section.remove();
    }

    // The chip and the Rate button go with it (50-chip.js).
    syncDetailsExtras();
}

// The item answered 403 or 404 (after a write too): nothing shown for it until the page is shown again (another item,
// a restored view), so the scanner doesn't read it over and over.
function goneDetails(entry) {
    const hadForm = !!entry.form && !entry.dead;
    teardownDetails(entry);
    entry.state = 'none';
    if (hadForm && entry.draftKept) {
        // The section went with the member's unsaved text in it: it is kept, and they are told where.
        kit.toast(kit.t('details.draftKept'));
    }
}

function leaveDetails(entry) {
    teardownDetails(entry);
    if (detailsState.entry === entry) {
        detailsState.entry = null;
    }
}

function showDetails(entry, dto) {
    if (!entry.els) {
        entry.expanded = !(entry.caps && entry.caps.Display && entry.caps.Display.SectionExpandedByDefault === false);
        entry.mine = dto && dto.Mine ? dto.Mine : null;
        buildDetailsSection(entry);
    }

    showNotice(entry.els.loadError, null);
    [entry.els.summary, entry.els.mine, entry.els.reviews].forEach(function (node) {
        kit.setHidden(node, false);
    });
    entry.state = 'shown';
    applyItem(entry, dto, { reviews: true, stars: true });
    insertDetails(entry);
}

// A read that failed for a reason of its own (busy, no answer): the section with the sentence and Retry.
function showLoadError(entry, failure) {
    if (!entry.els) {
        entry.expanded = true;
        buildDetailsSection(entry);
    }

    [entry.els.summary, entry.els.copies, entry.els.mine, entry.els.reviews].forEach(function (node) {
        kit.setHidden(node, true);
    });
    showNotice(entry.els.loadError, 'error', kit.t('details.loadFailed', { reason: failure.message }), function () {
        loadDetails(entry);
    });
    insertDetails(entry);
}

function loadDetails(entry) {
    entry.state = 'loading';
    Promise.all([ratingsApi.capabilities(), ratingsApi.item(entry.itemId)]).then(function (answers) {
        if (entry.dead) {
            return;
        }

        entry.caps = answers[0] || RATINGS_DEFAULT_CAPABILITIES;
        if (entry.caps.Enabled === false) {
            goneDetails(entry);
            return;
        }

        showDetails(entry, answers[1]);
    }, function (e) {
        if (entry.dead) {
            return;
        }

        const failure = detailsFailure(e, 'read');
        if (failure.kind === 'gone') {
            goneDetails(entry);
            return;
        }

        if (!entry.caps) {
            entry.caps = RATINGS_DEFAULT_CAPABILITIES;
        }

        entry.state = 'error';
        showLoadError(entry, failure);
    });
}

function createDetailsEntry(page, itemId) {
    return {
        page: page,
        itemId: itemId,
        // The member the section is for: its writes go only while they are the one signed in.
        userId: detailsCurrentUser(),
        userToastShown: false,
        // The member's session expired: no more drafts are written for this section.
        noDrafts: false,
        state: 'loading',
        dead: false,
        queue: Promise.resolve(),
        caps: null,
        summary: null,
        mine: null,
        can: null,
        copies: [],
        partial: false,
        reviews: [],
        offset: 0,
        total: 0,
        sort: DETAILS_SORTS[0],
        listGeneration: 0,
        loadingMore: false,
        reviewsOff: false,
        reviewsOffSaid: false,
        formOpen: false,
        form: null,
        busy: false,
        expanded: true,
        sliderState: undefined,
        ownKey: null,
        ownButtons: null,
        els: null,
        slider: null,
        // The chip and the Rate button (50-chip.js), and the item's type for ShowDetailButton Auto, asked once.
        chip: null,
        rateButton: null,
        typeAsked: false,
        itemType: null
    };
}

// The scanner (kit.observe: after DOM changes, viewshow and hash changes): one section, on the shown details page, for
// the item in the hash. Another page or item unmounts the one there was; a section of ours left anywhere else goes.
function syncDetailsSection() {
    const page = shownDetailPage();
    const itemId = page ? detailsId(kit.hashParams().get('id')) : '';
    let entry = detailsState.entry;
    if (entry && (!page || entry.page !== page || entry.itemId !== itemId)) {
        leaveDetails(entry);
        entry = null;
    }

    document.querySelectorAll('.jfur-section').forEach(function (node) {
        if (!entry || !entry.els || node !== entry.els.section) {
            node.remove();
        }
    });
    if (!page || !itemId) {
        return;
    }

    if (entry) {
        if (entry.state === 'shown' || entry.state === 'error') {
            // jellyfin-web wrote the column again: the same section goes back in.
            insertDetails(entry);
        }

        return;
    }

    entry = createDetailsEntry(page, itemId);
    detailsState.entry = entry;
    loadDetails(entry);
}

// viewshow: a restored view (Back, the view cache) reads the item again, since another member may have rated
// it meanwhile; the scanner, which runs after every viewshow, mounts the section again.
function onDetailsViewShow(view, detail) {
    if (detail && detail.isRestored === true && detailsState.entry) {
        // The chip stays, saying what it said, until the new read answers (50-chip.js).
        keepChip(detailsState.entry);
        leaveDetails(detailsState.entry);
    }
}

// A sign-out or another user (kit.user.onChange): the section and the capabilities are the previous user's.
function onDetailsUserChange(userId) {
    ratingsApi.forget();
    if (detailsState.entry) {
        leaveDetails(detailsState.entry);
    }

    // The drafts are the member's who wrote them: those of a member who signed out go, and so does any other
    // member's when one signs in (a sign-out this page didn't see).
    removeOtherDrafts(userId || null);
}
