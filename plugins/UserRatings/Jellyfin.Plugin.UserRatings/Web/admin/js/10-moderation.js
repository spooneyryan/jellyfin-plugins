// User Ratings' dashboard module (the kit's AdminScript): the settings page's Moderation tab,
// which configPage.html imports from GET /UserRatings/AdminScript and mounts into its panel (90-mount.js). Every
// member's rating and review on the items the admin can open (an admin with every library sees the detached ones too),
// filtered by member, title, review or not, since a date and attached or detached, searched over names, titles
// and reviews, 50 a page; each row's "Remove…" opens the same dialog as the details page's: "Remove text only" (the
// default, the stars stay) or "Remove rating and review", an optional reason, and what the server's dry run says it
// removes, which Remove then removes (the dry run's PreviewToken; a row changed since is a 409 that removes nothing).
// Below it the moderation log, its newest rows first. Every value from the server is text (kit.h's text, never HTML),
// every name and title isolated (dir="auto", unicode-bidi: isolate); the controls are 44 px high on a phone; the dialog
// is the kit's (scopedKeys: the TV layout's D-pad moves through it, Back closes it, the focus goes back to the button
// that opened it, or to the row in its place once the list is read again).

const MOD_PAGE_SIZE = 50;
const MOD_LOG_PAGE_SIZE = 25;
const MOD_SEARCH_MAX = 200;
const MOD_REASONS = ['Spoiler', 'Offensive', 'Spam', 'OffTopic', 'Requested', 'Other'];
// The actions an admin's call writes with its caller: with no actor, an API key's (the others with none are the server's).
const MOD_ROUTE_ACTIONS = ['RemoveText', 'RemoveEntry', 'Detach'];
// Every action the server logs (Storage/RatingValues.ModerationAction.All), in its order: the log's filter offers each.
const MOD_ACTIONS = ['RemoveText', 'RemoveEntry', 'Relink', 'Archive', 'Purge', 'ImportJe', 'ImportNative', 'ImportEnvelope', 'UserDeleted', 'UserAnonymized', 'UserOrphaned', 'UserRemapped', 'MirrorBackfill', 'RestoreSnapshot', 'Detach', 'StartEmpty'];

kit.addStrings({
    'mod.intro': 'Every member\'s rating and review on the titles you can open, including those the Hide settings keep from the members. Remove text only takes a review away and keeps the stars; Remove rating and review takes both, for good. The moderation log below keeps who removed what and why, never the text.',
    'mod.search': 'Search names, titles and reviews',
    'mod.state': 'Shown',
    'mod.stateAll': 'Attached and detached',
    'mod.stateAttached': 'Attached to a title',
    'mod.stateDetached': 'Detached',
    'mod.hasText': 'Review',
    'mod.hasTextAny': 'With or without',
    'mod.hasTextYes': 'With a review',
    'mod.hasTextNo': 'Stars only',
    'mod.since': 'Changed since',
    'mod.clear': 'Clear filters',
    'mod.filterMember': 'Member: {name}',
    'mod.filterItem': 'Title: {name}',
    'mod.filterRemove': 'Show every member, not only {name}',
    'mod.filterRemoveItem': 'Show every title, not only {name}',
    'mod.onlyMember': 'Only {name}',
    'mod.onlyItem': 'Only this title',
    'mod.colMember': 'Member',
    'mod.colItem': 'Title',
    'mod.colRating': 'Rating',
    'mod.colChanged': 'Changed',
    'mod.colActions': 'Actions',
    'mod.formerMember': 'Former member',
    'mod.orphan': 'Unknown user',
    'mod.hidden': 'hidden',
    'mod.disabled': 'disabled',
    'mod.noStars': 'No stars',
    'mod.stars': '{stars} out of 5',
    'mod.noReview': 'No review',
    'mod.spoiler': 'Marked as a spoiler',
    'mod.edited': '(edited)',
    'mod.detached': 'Detached ({reason})',
    'mod.reasonRemoved': 'removed from the library',
    'mod.reasonMissing': 'missing from the library',
    'mod.reasonUnmatched': 'not matched',
    'mod.reasonAmbiguous': 'ambiguous',
    'mod.episode': 'S{season}E{episode}',
    'mod.loading': 'Loading…',
    'mod.empty': 'No ratings match.',
    'mod.emptyAll': 'Nobody has rated anything yet.',
    'mod.loadFailed': 'The ratings couldn\'t be loaded. {reason}',
    'mod.retry': 'Try again',
    'mod.count': '{from}–{to} of {total}',
    'mod.previous': 'Previous',
    'mod.next': 'Next',
    'mod.remove': 'Remove…',
    'mod.removeLabel': 'Remove the rating of {name} on {item}…',
    'mod.title': 'Remove a review',
    'mod.by': 'By {name}, on {item}',
    'mod.what': 'What to remove',
    'mod.textOnly': 'Remove text only',
    'mod.textOnlyHint': 'The review goes and the stars stay.',
    'mod.textOnlyNone': 'This rating has no review.',
    'mod.entry': 'Remove rating and review',
    'mod.entryHint': 'The stars and the review go, and the rating can\'t come back from an import.',
    'mod.reason': 'Reason (optional)',
    'mod.reasonNone': 'No reason given',
    'mod.reasonSpoiler': 'Spoiler',
    'mod.reasonOffensive': 'Offensive',
    'mod.reasonSpam': 'Spam',
    'mod.reasonOffTopic': 'Off topic',
    'mod.reasonRequested': 'The member asked for it',
    'mod.reasonOther': 'Other',
    'mod.checking': 'Checking what will be removed…',
    'mod.previewText': 'The review goes; the stars stay ({stars} out of 5).',
    'mod.previewTextDeletes': 'The review goes, and with it the rating, which has no stars.',
    'mod.previewEntry': 'The rating and the review go.',
    'mod.previewArchive': 'Its earlier copies in the archive go too.',
    'mod.previewSnapshot': 'A snapshot of the ratings database is taken first, kept for 7 days.',
    'mod.previewFailed': 'What would be removed couldn\'t be checked. {reason}',
    'mod.gone': 'This rating was already removed, or you can\'t remove it.',
    'mod.noReviewLeft': 'This rating has no review any more; Remove rating and review still removes its stars.',
    'mod.changed': 'This rating changed after it was checked, so nothing was removed. It is shown below as it is now; Remove removes what is checked for it now.',
    'mod.changedGone': 'This rating was moved or removed after it was checked, so nothing was removed.',
    'mod.reloading': 'Reading this rating again…',
    'mod.now': 'Now: {stars} out of 5',
    'mod.nowNoStars': 'Now: no stars',
    'mod.nowNoReview': 'No review now.',
    'mod.cancel': 'Cancel',
    'mod.confirm': 'Remove',
    'mod.removing': 'Removing…',
    'mod.failed': 'Nothing was removed. {reason}',
    'mod.refused': 'The server refused it.',
    'mod.doneText': 'The review was removed. The stars stay.',
    'mod.doneEntry': 'The rating and the review were removed.',
    'mod.log': 'Moderation log',
    'mod.logIntro': 'What moderators, imports and the deleted-user policies did, newest first: who, what and the counts, never the text of a review.',
    'mod.logAction': 'Action',
    'mod.logActionAll': 'Every action',
    'mod.logWhen': 'When',
    'mod.logWho': 'By',
    'mod.logWhat': 'What',
    'mod.logTarget': 'Member',
    'mod.logItem': 'Title',
    'mod.logReason': 'Reason',
    'mod.logDetails': 'Counts',
    'mod.logApiKey': 'An API key',
    'mod.logSystem': 'The server',
    'mod.logItemHidden': 'A title no longer in the library',
    'mod.logEmpty': 'Nothing logged yet.',
    'mod.logFailed': 'The moderation log couldn\'t be loaded. {reason}',
    'mod.action.RemoveText': 'Removed a review',
    'mod.action.RemoveEntry': 'Removed a rating and review',
    'mod.action.Relink': 'Relinked',
    'mod.action.Archive': 'Archived',
    'mod.action.Purge': 'Purged',
    'mod.action.ImportJe': 'Imported from Jellyfin Enhanced',
    'mod.action.ImportNative': 'Imported',
    'mod.action.ImportEnvelope': 'Imported a backup',
    'mod.action.UserDeleted': 'A deleted member\'s ratings deleted',
    'mod.action.UserAnonymized': 'A deleted member\'s ratings anonymized',
    'mod.action.UserOrphaned': 'A deleted member\'s ratings kept',
    'mod.action.UserRemapped': 'A member\'s ratings moved to a new account',
    'mod.action.MirrorBackfill': 'Ratings copied to Jellyfin',
    'mod.action.RestoreSnapshot': 'Restored a snapshot',
    'mod.action.Detach': 'Detached a rating kept on a title that is gone',
    'mod.action.StartEmpty': 'Started the ratings empty (the database was damaged)',
    'mod.reasonCode.Spoiler': 'Spoiler',
    'mod.reasonCode.Offensive': 'Offensive',
    'mod.reasonCode.Spam': 'Spam',
    'mod.reasonCode.OffTopic': 'Off topic',
    'mod.reasonCode.Requested': 'The member asked for it',
    'mod.reasonCode.Other': 'Other',
    'mod.reasonCode.MergedVersion': 'A merged version',
    'mod.reasonCode.LibraryFolderUnavailable': 'Its library folder was missing, empty or unreadable',
    'mod.reasonCode.StillOnDisk': 'Its file was still on disk',
    'mod.reasonCode.NoLibraryFolders': 'No library folder could be read'
});

// ------------------------------------------------------------------------------------------------- helpers

function modId(value) {
    return String(value || '').replace(/-/g, '').toLowerCase();
}

// A member's or a title's words in their own element: dir="auto" and unicode-bidi: isolate, as text.
function modIsolated(tag, className, value) {
    return kit.h(tag, { class: 'jfur-isolate' + (className ? ' ' + className : ''), dir: 'auto', text: String(value === undefined || value === null ? '' : value) });
}

// A sentence of the strings table with {name} parts given as nodes (isolated names), the rest as text.
function modSentence(key, nodes) {
    const text = kit.t(key);
    const parts = [];
    let rest = text;
    for (;;) {
        const match = /\{(\w+)\}/.exec(rest);
        if (!match) {
            break;
        }

        parts.push(document.createTextNode(rest.substring(0, match.index)));
        parts.push(nodes && nodes[match[1]] ? nodes[match[1]] : document.createTextNode(match[0]));
        rest = rest.substring(match.index + match[0].length);
    }

    parts.push(document.createTextNode(rest));
    return parts;
}

function modStars(value) {
    const n = Number(value);
    return isFinite(n) && n > 0 ? String(Math.round(n * 10) / 10) : '';
}

function modAuthor(entry) {
    if (entry.AuthorKind === 'Orphan') {
        return kit.t('mod.orphan');
    }

    return entry.UserName || (entry.AuthorKind === 'FormerMember' ? kit.t('mod.formerMember') : kit.t('mod.orphan'));
}

// A title as the row shows it: an episode with its series and number, a year after a film's.
function modItemName(entry) {
    const name = String(entry.ItemName || '');
    if (entry.SeriesName && entry.SeasonNumber !== null && entry.SeasonNumber !== undefined && entry.EpisodeNumber !== null && entry.EpisodeNumber !== undefined) {
        return entry.SeriesName + ' ' + kit.t('mod.episode', { season: entry.SeasonNumber, episode: entry.EpisodeNumber }) + ' · ' + name;
    }

    // The year once: a name Jellyfin took from a folder ("Heat (1995)", no metadata) already has it.
    const year = entry.ProductionYear ? '(' + entry.ProductionYear + ')' : '';
    return year && name.indexOf(year) < 0 ? name + ' ' + year : name;
}

function modWhen(utc) {
    const date = new Date(utc);
    return utc && !isNaN(date.getTime()) ? date.toLocaleString() : '';
}

function modFocus(node) {
    if (!node || !node.isConnected || node.disabled) {
        return false;
    }

    try {
        node.focus();
    } catch (e) {
        return false;
    }

    return document.activeElement === node;
}

function modProblem(error, preview) {
    const status = error && error.status;
    if (status === 404) {
        return kit.t('mod.gone');
    }

    if (status === 409 && error.code !== 'Changed') {
        return kit.t('mod.noReviewLeft');
    }

    const reason = (error && error.message) || kit.t('mod.refused');
    return kit.t(preview ? 'mod.previewFailed' : 'mod.failed', { reason: reason });
}

// ------------------------------------------------------------------------------------------------- the tab

function mountModeration(view, params) {
    kit.injectStyles();
    const state = {
        startIndex: 0,
        total: 0,
        rows: [],
        generation: 0,
        filters: { searchTerm: '', state: '', hasText: '', since: '', userId: null, userName: null, itemId: null, itemName: null },
        log: { startIndex: 0, total: 0, action: '', generation: 0 }
    };
    const n = 'jfur-mod-' + Math.floor(Math.random() * 1e9).toString(36);

    // The filters.
    const search = kit.searchBox({ placeholder: kit.t('mod.search'), label: kit.t('mod.search'), maxLength: MOD_SEARCH_MAX, onSearch: function (text) {
        state.filters.searchTerm = text;
        reload();
    } });
    const stateSelect = kit.select({ id: n + '-state', class: 'jfur-mod-state' }, [
        { value: '', text: kit.t('mod.stateAll') },
        { value: 'Attached', text: kit.t('mod.stateAttached') },
        { value: 'Detached', text: kit.t('mod.stateDetached') }
    ]);
    const textSelect = kit.select({ id: n + '-text', class: 'jfur-mod-hastext' }, [
        { value: '', text: kit.t('mod.hasTextAny') },
        { value: 'true', text: kit.t('mod.hasTextYes') },
        { value: 'false', text: kit.t('mod.hasTextNo') }
    ]);
    const since = kit.input({ id: n + '-since', type: 'date', class: 'jfur-mod-since' });
    const clear = kit.button(kit.t('mod.clear'), { className: 'jfur-mod-clear', onClick: function () { clearFilters(); } });
    stateSelect.addEventListener('change', function () {
        state.filters.state = stateSelect.value;
        reload();
    });
    textSelect.addEventListener('change', function () {
        state.filters.hasText = textSelect.value;
        reload();
    });
    since.addEventListener('change', function () {
        state.filters.since = since.value;
        reload();
    });
    const field = function (label, input) {
        return kit.h('div', { class: 'jfur-mod-field' }, [kit.h('label', { for: input.id, text: label }), input]);
    };
    const chips = kit.h('div', { class: 'jfur-mod-chips' });
    const filters = kit.h('div', { class: 'jfur-mod-filters' }, [
        kit.h('div', { class: 'jfur-mod-field jfur-mod-search' }, [search]),
        field(kit.t('mod.state'), stateSelect),
        field(kit.t('mod.hasText'), textSelect),
        field(kit.t('mod.since'), since),
        kit.h('div', { class: 'jfur-mod-field jfur-mod-clear-field' }, [clear])
    ]);

    // The list.
    const status = kit.h('p', { class: 'jfur-mod-status', role: 'status' });
    const message = kit.h('div', { class: 'jfur-mod-message' });
    const body = kit.h('tbody');
    const table = kit.h('table', { class: 'jfur-mod-table' }, [
        kit.h('thead', null, kit.h('tr', null, ['colMember', 'colItem', 'colRating', 'colChanged', 'colActions'].map(function (key) {
            return kit.h('th', { scope: 'col', text: kit.t('mod.' + key) });
        }))),
        body
    ]);
    const previous = kit.button(kit.t('mod.previous'), { className: 'jfur-mod-previous', onClick: function () { page(-1); } });
    const next = kit.button(kit.t('mod.next'), { className: 'jfur-mod-next', onClick: function () { page(1); } });
    const count = kit.h('span', { class: 'jfur-mod-count' });
    const pager = kit.h('div', { class: 'jfur-mod-pager' }, [previous, count, next]);

    // The log.
    const logAction = kit.select({ id: n + '-action', class: 'jfur-mod-log-action' }, [{ value: '', text: kit.t('mod.logActionAll') }].concat(MOD_ACTIONS.map(function (a) {
        return { value: a, text: kit.t('mod.action.' + a) };
    })));
    logAction.addEventListener('change', function () {
        state.log.action = logAction.value;
        state.log.startIndex = 0;
        loadLog();
    });
    const logMessage = kit.h('div', { class: 'jfur-mod-message' });
    const logBody = kit.h('tbody');
    const logTable = kit.h('table', { class: 'jfur-mod-table jfur-mod-log-table' }, [
        kit.h('thead', null, kit.h('tr', null, ['logWhen', 'logWho', 'logWhat', 'logTarget', 'logItem', 'logReason', 'logDetails'].map(function (key) {
            return kit.h('th', { scope: 'col', text: kit.t('mod.' + key) });
        }))),
        logBody
    ]);
    const logPrevious = kit.button(kit.t('mod.previous'), { className: 'jfur-mod-log-previous', onClick: function () { logPage(-1); } });
    const logNext = kit.button(kit.t('mod.next'), { className: 'jfur-mod-log-next', onClick: function () { logPage(1); } });
    const logCount = kit.h('span', { class: 'jfur-mod-count' });
    const logPager = kit.h('div', { class: 'jfur-mod-pager' }, [logPrevious, logCount, logNext]);

    const root = kit.h('div', { class: 'jfur-mod' }, [
        kit.h('p', { class: 'jfur-mod-intro', text: kit.t('mod.intro') }),
        filters,
        chips,
        status,
        message,
        kit.h('div', { class: 'jfur-mod-scroll' }, [table]),
        pager,
        kit.h('h3', { class: 'jfur-mod-log-title', text: kit.t('mod.log') }),
        kit.h('p', { class: 'jfur-mod-intro', text: kit.t('mod.logIntro') }),
        kit.h('div', { class: 'jfur-mod-filters' }, [field(kit.t('mod.logAction'), logAction)]),
        logMessage,
        kit.h('div', { class: 'jfur-mod-scroll' }, [logTable]),
        logPager
    ]);
    view.replaceChildren(root);
    [table, pager, logTable, logPager, message, logMessage].forEach(function (node) {
        kit.setHidden(node, true);
    });

    function clearFilters() {
        state.filters = { searchTerm: '', state: '', hasText: '', since: '', userId: null, userName: null, itemId: null, itemName: null };
        search.setSearch('');
        stateSelect.value = '';
        textSelect.value = '';
        since.value = '';
        reload();
    }

    function renderChips() {
        const f = state.filters;
        const list = [];
        if (f.userId) {
            list.push(chip(modSentence('mod.filterMember', { name: modIsolated('span', null, f.userName) }), kit.t('mod.filterRemove', { name: f.userName }), function () {
                f.userId = null;
                f.userName = null;
            }));
        }

        if (f.itemId) {
            list.push(chip(modSentence('mod.filterItem', { name: modIsolated('span', null, f.itemName) }), kit.t('mod.filterRemoveItem', { name: f.itemName }), function () {
                f.itemId = null;
                f.itemName = null;
            }));
        }

        chips.replaceChildren.apply(chips, list);
        kit.setHidden(chips, list.length === 0);
    }

    function chip(nodes, label, drop) {
        const button = kit.h('button', { type: 'button', class: kit.cls('button') + ' jfur-mod-chip', 'aria-label': label }, nodes.concat([kit.icon('close', 'jfur-mod-chip-icon')]));
        button.addEventListener('click', function () {
            drop();
            reload(search);
        });
        return button;
    }

    function query() {
        const f = state.filters;
        const q = { startIndex: state.startIndex, limit: MOD_PAGE_SIZE };
        if (f.searchTerm) {
            q.searchTerm = f.searchTerm;
        }

        if (f.state) {
            q.state = f.state;
        }

        if (f.hasText) {
            q.hasText = f.hasText;
        }

        if (f.since) {
            const date = new Date(f.since + 'T00:00:00');
            if (!isNaN(date.getTime())) {
                q.since = date.toISOString();
            }
        }

        if (f.userId) {
            q.userId = f.userId;
        }

        if (f.itemId) {
            q.itemId = f.itemId;
        }

        return q;
    }

    function filtered() {
        const f = state.filters;
        return !!(f.searchTerm || f.state || f.hasText || f.since || f.userId || f.itemId);
    }

    function reload(focusAfter) {
        state.startIndex = 0;
        return load(focusAfter);
    }

    function page(step) {
        const start = state.startIndex + step * MOD_PAGE_SIZE;
        if (start < 0 || (step > 0 && start >= state.total)) {
            return;
        }

        state.startIndex = start;
        load(step > 0 ? next : previous);
    }

    // The page of rows; a later read wins (an older answer is dropped). focusAfter: the control the focus goes to when the
    // one that had it went with the rows (a removal, a filter's chip, a page).
    function load(focusAfter, removed) {
        const generation = ++state.generation;
        renderChips();
        status.textContent = kit.t('mod.loading');
        return kit.api('GET', 'UserRatings/Admin/Reviews', { query: query() }).then(function (page) {
            if (generation !== state.generation) {
                return;
            }

            const items = (page && page.Items) || [];
            state.total = Math.max(items.length, Number(page && page.TotalRecordCount) || 0);
            if (items.length === 0 && state.startIndex > 0 && state.total > 0) {
                // The last page emptied (a removal): the page before it.
                state.startIndex = Math.max(0, state.startIndex - MOD_PAGE_SIZE);
                return load(focusAfter, removed);
            }

            state.rows = items;
            status.textContent = '';
            renderRows(focusAfter, removed);
        }, function (e) {
            if (generation !== state.generation) {
                return;
            }

            status.textContent = '';
            message.replaceChildren(kit.notice({ kind: 'error', text: kit.t('mod.loadFailed', { reason: e && e.message ? e.message : '' }), action: { label: kit.t('mod.retry'), onClick: function () { load(); } } }));
            kit.setHidden(message, false);
            kit.setHidden(table, true);
            kit.setHidden(pager, true);
        });
    }

    function renderRows(focusAfter, removed) {
        const active = document.activeElement;
        const hadFocus = !!active && root.contains(active);
        body.replaceChildren.apply(body, state.rows.map(row));
        const empty = state.rows.length === 0;
        kit.setHidden(table, empty);
        message.replaceChildren();
        if (empty) {
            message.append(kit.notice({ kind: 'empty', text: filtered() ? kit.t('mod.empty') : kit.t('mod.emptyAll') }));
        }

        kit.setHidden(message, !empty);
        const from = empty ? 0 : state.startIndex + 1;
        const to = state.startIndex + state.rows.length;
        count.textContent = kit.t('mod.count', { from: from, to: to, total: state.total });
        previous.disabled = state.startIndex === 0;
        next.disabled = to >= state.total;
        kit.setHidden(pager, state.total <= MOD_PAGE_SIZE && state.startIndex === 0);
        if (hadFocus && (!document.activeElement || document.activeElement === document.body || !document.activeElement.isConnected || document.activeElement.disabled)) {
            focusRow(removed, focusAfter);
        }
    }

    // After the rows were built again: the removed row's place (its Remove… there, the next row's, the last one's), else the
    // control asked for, else the search box, so the focus is never lost to the page's body (the TV layout's D-pad).
    function focusRow(removed, focusAfter) {
        const buttons = body.querySelectorAll('.jfur-mod-remove');
        if (removed) {
            const same = body.querySelector('[data-jfur-rating="' + modId(removed.id) + '"] .jfur-mod-remove');
            if (modFocus(same) || modFocus(buttons[Math.min(removed.index, buttons.length - 1)])) {
                return;
            }
        }

        if (modFocus(focusAfter) || modFocus(buttons[0])) {
            return;
        }

        modFocus(search);
    }

    function row(entry, index) {
        const author = modAuthor(entry);
        const item = modItemName(entry);
        const flags = [];
        if (entry.IsHiddenUser) {
            flags.push(kit.t('mod.hidden'));
        }

        if (entry.IsDisabledUser) {
            flags.push(kit.t('mod.disabled'));
        }

        const memberCell = kit.h('td', { class: 'jfur-mod-member', 'data-label': kit.t('mod.colMember') }, [
            modIsolated('span', 'jfur-mod-name', author),
            flags.length ? kit.h('span', { class: 'jfur-mod-flags', text: ' (' + flags.join(', ') + ')' }) : null,
            entry.AuthorKind === 'Member' && entry.UserId && state.filters.userId !== modId(entry.UserId)
                ? filterButton(kit.t('mod.onlyMember', { name: author }), function () {
                    state.filters.userId = modId(entry.UserId);
                    state.filters.userName = author;
                })
                : null
        ]);
        const detached = entry.State === 'Detached';
        const itemCell = kit.h('td', { class: 'jfur-mod-item', 'data-label': kit.t('mod.colItem') }, [
            modIsolated('span', 'jfur-mod-title', item),
            detached ? kit.h('span', { class: 'jfur-mod-detached', text: ' ' + kit.t('mod.detached', { reason: kit.t('mod.reason' + (entry.DetachReason || 'Removed')) }) }) : null,
            !detached && entry.ItemId && state.filters.itemId !== modId(entry.ItemId)
                ? filterButton(kit.t('mod.onlyItem'), function () {
                    state.filters.itemId = modId(entry.ItemId);
                    state.filters.itemName = item;
                })
                : null
        ]);
        const stars = modStars(entry.Stars);
        const ratingCell = kit.h('td', { class: 'jfur-mod-rating', 'data-label': kit.t('mod.colRating') }, [
            kit.h('div', { class: 'jfur-mod-stars', text: stars ? kit.t('mod.stars', { stars: stars }) : kit.t('mod.noStars') }),
            entry.Text
                ? kit.h('div', { class: 'jfur-mod-review' }, [
                    entry.IsSpoiler ? kit.h('div', { class: 'jfur-mod-spoiler', text: kit.t('mod.spoiler') }) : null,
                    modIsolated('div', 'jfur-mod-text', entry.Text)
                ])
                : kit.h('div', { class: 'jfur-mod-none', text: kit.t('mod.noReview') })
        ]);
        const changedCell = kit.h('td', { class: 'jfur-mod-changed', 'data-label': kit.t('mod.colChanged') }, [
            kit.h('time', { datetime: String(entry.UpdatedUtc || ''), text: modWhen(entry.UpdatedUtc) }),
            entry.Edited ? kit.h('span', { text: ' ' + kit.t('mod.edited') }) : null
        ]);
        const remove = kit.button(kit.t('mod.remove'), { className: 'jfur-mod-remove' });
        remove.setAttribute('aria-label', kit.t('mod.removeLabel', { name: author, item: item }));
        remove.addEventListener('click', function () {
            openRemove(entry, index, remove, author, item);
        });
        return kit.h('tr', { class: 'jfur-mod-row', 'data-jfur-rating': modId(entry.RatingId) }, [memberCell, itemCell, ratingCell, changedCell, kit.h('td', { class: 'jfur-mod-actions', 'data-label': kit.t('mod.colActions') }, [remove])]);
    }

    function filterButton(label, set) {
        const button = kit.h('button', { type: 'button', class: kit.cls('link') + ' jfur-mod-only', text: label });
        button.addEventListener('click', function () {
            set();
            reload(search);
        });
        return button;
    }

    // ------------------------------------------------------------------------------------------- the dialog

    function openRemove(entry, index, opener, author, item) {
        const hasText = !!entry.Text;
        const dialogState = { textOnly: hasText, token: null, seq: 0, busy: false, closed: false };
        // The row as the dialog knows it: the list's, then the one read again after a 409 Changed,
        // whose stars the preview names.
        let current = entry;
        const inputs = {};
        const hints = {};
        const labels = {};
        let cancel = null;
        const dialog = kit.dialog({
            title: kit.t('mod.title'),
            modal: true,
            scopedKeys: true,
            initialFocus: function () { return cancel; },
            returnFocus: function () { return body.querySelector('.jfur-mod-remove') || search; }
        });
        dialog.onClose(function () {
            dialogState.closed = true;
            dialogState.seq++;
        });
        const group = n + '-what-' + index;
        const choice = function (textOnly, label, hint, disabled) {
            const input = kit.h('input', { type: 'radio', name: group, value: textOnly ? 'text' : 'entry', class: 'jfur-mod-radio' });
            input.checked = textOnly === dialogState.textOnly;
            input.disabled = !!disabled;
            inputs[textOnly ? 'text' : 'entry'] = input;
            input.addEventListener('change', function () {
                if (input.checked && dialogState.textOnly !== textOnly) {
                    dialogState.textOnly = textOnly;
                    check();
                }
            });
            const hintNode = kit.h('span', { class: 'jfur-mod-choice-hint', text: hint });
            hints[textOnly ? 'text' : 'entry'] = hintNode;
            const labelNode = kit.h('label', { class: 'jfur-mod-choice' + (disabled ? ' jfur-mod-choice-disabled' : '') }, [input, kit.h('span', { class: 'jfur-mod-choice-text' }, [
                kit.h('span', { class: 'jfur-mod-choice-label', text: label }),
                hintNode
            ])]);
            labels[textOnly ? 'text' : 'entry'] = labelNode;
            return labelNode;
        };
        const reasonId = group + '-reason';
        const reason = kit.select({ id: reasonId, class: 'jfur-mod-reason' }, [{ value: '', text: kit.t('mod.reasonNone') }].concat(MOD_REASONS.map(function (r) {
            return { value: r, text: kit.t('mod.reason' + r) };
        })));
        const preview = kit.h('p', { class: 'jfur-mod-preview', role: 'status' });
        // The row as it is now, shown once it was read again after a 409 Changed: its stars and its words, isolated.
        const now = kit.h('div', { class: 'jfur-mod-now' });
        kit.setHidden(now, true);
        const problem = kit.h('div', { class: 'jfur-mod-problem' });
        kit.setHidden(problem, true);
        cancel = kit.button(kit.t('mod.cancel'), { className: 'jfur-mod-cancel', onClick: function () { dialog.close(); } });
        const confirm = kit.button(kit.t('mod.confirm'), { variant: 'primary', className: 'jfur-mod-confirm', onClick: function () { remove(); } });
        confirm.disabled = true;
        kit.append(dialog.body,
            kit.h('p', { class: 'jfur-mod-by' }, modSentence('mod.by', { name: modIsolated('span', 'jfur-mod-name', author), item: modIsolated('span', 'jfur-mod-title', item) })),
            now,
            kit.h('fieldset', { class: 'jfur-mod-what' }, [
                kit.h('legend', { text: kit.t('mod.what') }),
                choice(true, kit.t('mod.textOnly'), hasText ? kit.t('mod.textOnlyHint') : kit.t('mod.textOnlyNone'), !hasText),
                choice(false, kit.t('mod.entry'), kit.t('mod.entryHint'), false)
            ]),
            kit.h('div', { class: 'jfur-mod-field' }, [kit.h('label', { for: reasonId, text: kit.t('mod.reason') }), reason]),
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
        const path = 'UserRatings/Admin/Reviews/' + encodeURIComponent(modId(entry.RatingId));

        function check(note) {
            const seq = ++dialogState.seq;
            dialogState.token = null;
            confirm.disabled = true;
            say(note || null);
            preview.textContent = kit.t('mod.checking');
            kit.api('DELETE', path, { query: { textOnly: dialogState.textOnly ? 'true' : 'false' } }).then(function (answer) {
                if (seq !== dialogState.seq || dialogState.closed || !answer) {
                    return;
                }

                dialogState.token = answer.PreviewToken || null;
                const parts = [answer.DeletesRating === true
                    ? (dialogState.textOnly ? kit.t('mod.previewTextDeletes') : kit.t('mod.previewEntry'))
                    : kit.t('mod.previewText', { stars: modStars(current.Stars) })];
                if (answer.ArchiveLines === true) {
                    parts.push(kit.t('mod.previewArchive'));
                }

                parts.push(kit.t('mod.previewSnapshot'));
                preview.textContent = parts.join(' ');
                confirm.disabled = !dialogState.token || dialogState.busy;
            }, function (e) {
                if (seq !== dialogState.seq || dialogState.closed) {
                    return;
                }

                preview.textContent = '';
                say(modProblem(e, true));
            });
        }

        function remove() {
            if (!dialogState.token || dialogState.busy || dialogState.closed) {
                return;
            }

            const textOnly = dialogState.textOnly;
            dialogState.busy = true;
            confirm.disabled = true;
            confirm.textContent = kit.t('mod.removing');
            say(null);
            const query = { textOnly: textOnly ? 'true' : 'false', dryRun: 'false', ifPreview: dialogState.token };
            if (reason.value) {
                query.reason = reason.value;
            }

            kit.api('DELETE', path, { query: query }).then(function () {
                dialogState.busy = false;
                if (dialogState.closed) {
                    return;
                }

                dialog.close();
                kit.toast(textOnly ? kit.t('mod.doneText') : kit.t('mod.doneEntry'));
                load(null, { id: entry.RatingId, index: index });
                state.log.startIndex = 0;
                loadLog();
            }, function (e) {
                dialogState.busy = false;
                confirm.textContent = kit.t('mod.confirm');
                if (dialogState.closed) {
                    return;
                }

                if (e && e.status === 409 && e.code === 'Changed') {
                    reload();
                    return;
                }

                say(modProblem(e, false));
                confirm.disabled = !dialogState.token;
                if (!modFocus(confirm)) {
                    modFocus(cancel);
                }
            });
        }

        // After a 409 Changed: nothing armed while the row is read again; then the row as it
        // is now (its stars and words), the choices it allows, and only then the dry run that gives the new token. The focus
        // goes to Cancel, a live control, since Remove is disabled meanwhile (the TV layout's D-pad has somewhere to be).
        function reload() {
            const seq = ++dialogState.seq;
            dialogState.token = null;
            confirm.disabled = true;
            say(null);
            preview.textContent = kit.t('mod.reloading');
            modFocus(cancel);
            kit.api('GET', 'UserRatings/Admin/Reviews', { query: { userId: modId(current.UserId), itemId: modId(current.ItemId), limit: 200 } }).then(function (page) {
                if (seq !== dialogState.seq || dialogState.closed) {
                    return;
                }

                const fresh = ((page && page.Items) || []).filter(function (r) { return modId(r.RatingId) === modId(entry.RatingId); })[0];
                if (!fresh) {
                    preview.textContent = '';
                    kit.setHidden(now, true);
                    say(kit.t('mod.changedGone'));
                    modFocus(cancel);
                    return;
                }

                current = fresh;
                const stars = modStars(fresh.Stars);
                now.replaceChildren(
                    kit.h('p', { class: 'jfur-mod-now-stars', text: stars ? kit.t('mod.now', { stars: stars }) : kit.t('mod.nowNoStars') }),
                    fresh.Text ? modIsolated('p', 'jfur-mod-now-text', fresh.Text) : kit.h('p', { class: 'jfur-mod-now-text jfur-mod-now-none', text: kit.t('mod.nowNoReview') }));
                kit.setHidden(now, false);
                const textNow = !!fresh.Text;
                inputs.text.disabled = !textNow;
                labels.text.className = 'jfur-mod-choice' + (textNow ? '' : ' jfur-mod-choice-disabled');
                hints.text.textContent = textNow ? kit.t('mod.textOnlyHint') : kit.t('mod.textOnlyNone');
                if (!textNow && dialogState.textOnly) {
                    dialogState.textOnly = false;
                    inputs.text.checked = false;
                    inputs.entry.checked = true;
                }

                check(kit.t('mod.changed'));
                modFocus(cancel);
            }, function (e) {
                if (seq !== dialogState.seq || dialogState.closed) {
                    return;
                }

                preview.textContent = '';
                say(modProblem(e, true));
                modFocus(cancel);
            });
        }

        check();
    }

    // ------------------------------------------------------------------------------------------- the log

    function logPage(step) {
        const start = state.log.startIndex + step * MOD_LOG_PAGE_SIZE;
        if (start < 0 || (step > 0 && start >= state.log.total)) {
            return;
        }

        state.log.startIndex = start;
        loadLog(step > 0 ? logNext : logPrevious);
    }

    function loadLog(focusAfter) {
        const generation = ++state.log.generation;
        const q = { startIndex: state.log.startIndex, limit: MOD_LOG_PAGE_SIZE };
        if (state.log.action) {
            q.action = state.log.action;
        }

        return kit.api('GET', 'UserRatings/Admin/ModerationLog', { query: q }).then(function (page) {
            if (generation !== state.log.generation) {
                return;
            }

            const items = (page && page.Items) || [];
            state.log.total = Math.max(items.length, Number(page && page.TotalRecordCount) || 0);
            const hadFocus = !!document.activeElement && logPager.contains(document.activeElement);
            logBody.replaceChildren.apply(logBody, items.map(logRow));
            const empty = items.length === 0;
            kit.setHidden(logTable, empty);
            logMessage.replaceChildren();
            if (empty) {
                logMessage.append(kit.notice({ kind: 'empty', text: kit.t('mod.logEmpty') }));
            }

            kit.setHidden(logMessage, !empty);
            const from = empty ? 0 : state.log.startIndex + 1;
            const to = state.log.startIndex + items.length;
            logCount.textContent = kit.t('mod.count', { from: from, to: to, total: state.log.total });
            logPrevious.disabled = state.log.startIndex === 0;
            logNext.disabled = to >= state.log.total;
            kit.setHidden(logPager, state.log.total <= MOD_LOG_PAGE_SIZE && state.log.startIndex === 0);
            if (hadFocus && focusAfter && focusAfter.disabled) {
                modFocus(focusAfter === logNext ? logPrevious : logNext) || modFocus(logAction);
            }
        }, function (e) {
            if (generation !== state.log.generation) {
                return;
            }

            logMessage.replaceChildren(kit.notice({ kind: 'error', text: kit.t('mod.logFailed', { reason: e && e.message ? e.message : '' }), action: { label: kit.t('mod.retry'), onClick: function () { loadLog(); } } }));
            kit.setHidden(logMessage, false);
            kit.setHidden(logTable, true);
            kit.setHidden(logPager, true);
        });
    }

    function logRow(entry) {
        const who = entry.ActorName ? modIsolated('span', 'jfur-mod-name', entry.ActorName)
            : kit.h('span', { text: entry.ActorUserId ? kit.t('mod.formerMember') : (MOD_ROUTE_ACTIONS.indexOf(entry.Action) >= 0 ? kit.t('mod.logApiKey') : kit.t('mod.logSystem')) });
        const target = entry.TargetName ? modIsolated('span', 'jfur-mod-name', entry.TargetName)
            : kit.h('span', { text: entry.TargetUserId ? kit.t('mod.formerMember') : '' });
        const item = entry.ItemName ? modIsolated('span', 'jfur-mod-title', entry.ItemName)
            : kit.h('span', { text: entry.ItemId ? kit.t('mod.logItemHidden') : '' });
        return kit.h('tr', { class: 'jfur-mod-log-row' }, [
            kit.h('td', { 'data-label': kit.t('mod.logWhen') }, [kit.h('time', { datetime: String(entry.AtUtc || ''), text: modWhen(entry.AtUtc) })]),
            kit.h('td', { 'data-label': kit.t('mod.logWho') }, [who]),
            kit.h('td', { 'data-label': kit.t('mod.logWhat'), text: logCode('mod.action.', entry.Action) }),
            kit.h('td', { 'data-label': kit.t('mod.logTarget') }, [target]),
            kit.h('td', { 'data-label': kit.t('mod.logItem') }, [item]),
            kit.h('td', { 'data-label': kit.t('mod.logReason'), text: logCode('mod.reasonCode.', entry.Reason) }),
            kit.h('td', { class: 'jfur-mod-details', 'data-label': kit.t('mod.logDetails'), text: logDetails(entry.Details) })
        ]);
    }

    // An action's or a reason's words; a code this page has none for (a newer server's) as the server wrote it.
    function logCode(prefix, code) {
        if (!code) {
            return '';
        }

        const text = kit.t(prefix + code);
        return text === prefix + code ? String(code) : text;
    }

    // The log row's counts ({"Stars":1,"Reviews":1,"Deleted":1}), as "Stars 1, Reviews 1, Deleted 1": numbers only.
    function logDetails(details) {
        let parsed = null;
        try {
            parsed = details ? JSON.parse(details) : null;
        } catch (e) {
            parsed = null;
        }

        if (!parsed || typeof parsed !== 'object') {
            return '';
        }

        return Object.keys(parsed).filter(function (key) {
            return typeof parsed[key] === 'number';
        }).map(function (key) {
            return key + ' ' + parsed[key];
        }).join(', ');
    }

    // The first reads after mount returns, through the page's own timer (so importing and mounting send nothing at once).
    window.setTimeout(function () {
        load();
        loadLog();
    }, 0);

    return {
        refresh: function () {
            load();
            loadLog();
        }
    };
}
