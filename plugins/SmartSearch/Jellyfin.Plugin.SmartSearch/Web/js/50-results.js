// The results: the header ("1,234 results · 0.42 s", Sort by, Order, Grid | List), the notices (no results, a scan
// running, a library not available, a refused search), the items as cards or rows, and "Load more" (the kit's pager:
// pressed by an IntersectionObserver when it scrolls into view, by the remote on the TV layout).
//
// The first page (POST /SmartSearch/Search, 50 items) asks for the total; "Load more" sends its ResultToken, so the next
// pages come from the same result. A saved query the form shows unchanged, or one it can't show, runs by its id
// (POST /SmartSearch/Queries/{id}/Search, as the user), its pages the same way. A newer search drops what an older one
// answers. Each card and row opens the item's
// details page (Open: the poster, the title, the button) and plays it (Play, the kit's playback through the Sessions
// API). The view is remembered per browser (localStorage jfss:view).

const SS_PAGE_SIZE = 50;

function ssBuildResults(page) {
    const h = kit.h;
    page.countLine = h('p', { class: 'jfss-count', role: 'status' });
    page.sortSelect = kit.select({ class: 'jfss-sort' }, page.catalog.sortKeys.map(function (key) {
        return { value: key, text: SS_SORT_LABELS[key] || key };
    }));
    page.sortSelect.value = page.state.sort;
    page.sortSelect.addEventListener('change', function () {
        ssChangeSort(page, { sort: page.sortSelect.value });
    });
    page.orderSelect = kit.select({ class: 'jfss-order' }, [{ value: 'asc', text: SS_TEXT.ascending }, { value: 'desc', text: SS_TEXT.descending }]);
    page.orderSelect.value = page.state.desc ? 'desc' : 'asc';
    page.orderSelect.addEventListener('change', function () {
        ssChangeSort(page, { desc: page.orderSelect.value === 'desc' });
    });
    page.viewButtons = [['grid', SS_TEXT.grid], ['list', SS_TEXT.list]].map(function (view) {
        return h('button', {
            type: 'button',
            class: 'jfss-segment jfss-view',
            'data-jfss-view': view[0],
            'aria-pressed': page.view === view[0] ? 'true' : 'false',
            text: view[1],
            onclick: function () {
                ssChangeView(page, view[0]);
            }
        });
    });
    const sortField = kit.field(SS_TEXT.sortLabel, page.sortSelect);
    sortField.classList.add('jfss-sort-field');
    sortField.append(ssErrorNode('sort'));
    const orderField = kit.field(SS_TEXT.orderLabel, page.orderSelect);
    orderField.classList.add('jfss-order-field');
    const viewId = ssNextId('view');
    page.resultsBar = h('div', { class: 'jfss-results-bar' }, [
        page.countLine,
        h('div', { class: 'jfss-results-tools' }, [
            sortField,
            orderField,
            h('div', { class: kit.cls('field') + ' jfss-view-field' }, [
                h('span', { class: 'jfss-label', id: viewId, text: SS_TEXT.viewLabel }),
                h('div', { class: 'jfss-segments', role: 'group', 'aria-labelledby': viewId }, page.viewButtons)
            ])
        ])
    ]);
    page.notices = h('div', { class: 'jfss-notices' });
    page.resultList = h('div', { class: 'jfss-results', 'aria-label': SS_TEXT.results });
    page.pager = kit.pager({
        text: SS_TEXT.moreResults,
        onMore: function () {
            ssLoadMore(page);
        }
    });
    page.results = { generation: 0, items: [], token: null, total: null, abort: null };
    kit.setHidden(page.resultsBar, true);
    return h('div', { class: 'jfss-results-section' }, [page.resultsBar, page.notices, page.resultList, page.pager.element]);
}

function ssChangeSort(page, changes) {
    page.state = Object.assign({}, page.state, changes);
    if (page.state.run) {
        kit.pages.setParams(SS_PAGE.key, ssWriteParams(page.state));
        ssRunSearch(page);
    }

    ssSyncActions(page);
}

function ssChangeView(page, view) {
    if (page.view === view) {
        return;
    }

    page.view = view;
    kit.storage.set('view', view);
    page.viewButtons.forEach(function (button) {
        button.setAttribute('aria-pressed', button.getAttribute('data-jfss-view') === view ? 'true' : 'false');
    });
    ssRenderItems(page, page.results.items, false);
}

function ssClearResults(page) {
    page.results.generation += 1;
    if (page.results.abort) {
        page.results.abort.abort();
    }

    page.results = { generation: page.results.generation, items: [], token: null, total: null, abort: null };
    page.resultList.replaceChildren();
    page.notices.replaceChildren();
    page.countLine.textContent = '';
    kit.setHidden(page.resultsBar, true);
    page.pager.update({ hasMore: false, busy: false });
}

function ssNotice(page, kind, text, action) {
    page.notices.append(kit.notice({ kind: kind, text: text, action: action }));
}

function ssRetryAction(page) {
    return { label: SS_TEXT.retry, onClick: function () {
        ssRunSearch(page);
    } };
}

// The first page of the page's state.
function ssRunSearch(page) {
    ssClearResults(page);
    const r = page.results;
    const generation = r.generation;
    page.savedRunId = ssSavedRunId(page);
    const built = page.savedRunId ? { fields: [], query: null } : ssBuildQuery(page.state, page.kept);
    page.fields = built.fields;
    page.query = built.query;
    kit.setHidden(page.resultsBar, false);
    page.countLine.textContent = SS_TEXT.searching;
    page.pager.update({ hasMore: false, busy: true });
    r.abort = typeof AbortController === 'function' ? new AbortController() : null;
    const signal = r.abort ? r.abort.signal : undefined;
    const request = page.savedRunId
        ? ssApi.runQuery(page.savedRunId, 0, SS_PAGE_SIZE, null, signal)
        : ssApi.search(built.query, 0, SS_PAGE_SIZE, null, signal);
    return request.then(function (result) {
        if (generation !== page.results.generation || !page.context.isShown()) {
            return;
        }

        r.token = result.ResultToken || null;
        r.total = typeof result.TotalRecordCount === 'number' ? result.TotalRecordCount : null;
        const seconds = ((result.ElapsedMs || 0) / 1000).toFixed(2);
        page.countLine.textContent = r.total === 1
            ? ssText('resultCountOne', { seconds: seconds })
            : ssText('resultCount', { count: (r.total === null ? (result.Items || []).length : r.total).toLocaleString(), seconds: seconds });
        if (result.ScanInProgress) {
            ssNotice(page, 'info', SS_TEXT.scanRunning);
        }

        if ((result.Warnings || []).indexOf('LibrariesUnavailable') >= 0) {
            ssNotice(page, 'info', SS_TEXT.librariesUnavailable);
        }

        const items = result.Items || [];
        if (items.length === 0 && !result.HasMore) {
            ssNotice(page, 'empty', SS_TEXT.noResults);
        }

        r.items = items.slice();
        ssRenderItems(page, items, false);
        page.pager.update({ hasMore: !!result.HasMore, busy: false });
    }).catch(function (error) {
        if (generation !== page.results.generation || !page.context.isShown() || (r.abort && r.abort.signal.aborted)) {
            return;
        }

        page.pager.update({ hasMore: false, busy: false });
        page.countLine.textContent = '';
        ssShowSearchError(page, error);
    });
}

function ssLoadMore(page) {
    const r = page.results;
    if (!r.token) {
        return Promise.resolve();
    }

    const generation = r.generation;
    page.pager.update({ busy: true });
    const signal = r.abort ? r.abort.signal : undefined;
    const request = page.savedRunId
        ? ssApi.runQuery(page.savedRunId, r.items.length, SS_PAGE_SIZE, r.token, signal)
        : ssApi.search(page.query, r.items.length, SS_PAGE_SIZE, r.token, signal);
    return request.then(function (result) {
        if (generation !== page.results.generation || !page.context.isShown()) {
            return;
        }

        r.token = result.ResultToken || r.token;
        const items = result.Items || [];
        r.items = r.items.concat(items);
        ssRenderItems(page, items, true);
        page.pager.update({ hasMore: !!result.HasMore, busy: false });
    }).catch(function (error) {
        if (generation !== page.results.generation || !page.context.isShown()) {
            return;
        }

        page.pager.update({ hasMore: true, busy: false });
        kit.toast(ssText('searchFailed', { message: error && error.message ? error.message : String(error) }));
    });
}

// A refused search: the query's errors under their fields, a too broad, slow or busy search as a notice with its hint.
function ssShowSearchError(page, error) {
    const problem = (error && error.problem) || {};
    const code = error && error.code;
    const list = Array.isArray(error && error.errors) ? error.errors : [];
    if (list.length > 0) {
        ssShowQueryErrors(page, list);
        kit.setHidden(page.resultsBar, true);
        return;
    }

    if (code === 'QueryTooBroad') {
        const kinds = Array.isArray(problem.kinds) ? problem.kinds : [];
        const count = typeof problem.candidateCount === 'number' ? problem.candidateCount.toLocaleString() : '?';
        ssNotice(page, 'error', ssText(kinds.length === 1 && kinds[0] === 'Episode' ? 'tooBroadEpisodes' : 'tooBroad', { count: count }));
    } else if (code === 'Timeout') {
        ssNotice(page, 'error', SS_TEXT.timeout, ssRetryAction(page));
    } else if (code === 'Busy') {
        ssNotice(page, 'error', SS_TEXT.busy, ssRetryAction(page));
    } else if (error && error.isFeatureDisabled) {
        ssNotice(page, 'error', error.message);
    } else {
        ssNotice(page, 'error', ssText('searchFailed', { message: error && error.message ? error.message : String(error) }), ssRetryAction(page));
    }
}

// A query's errors (a problem's are camelCase, /Validate's and a saved query's PascalCase): the advanced form's under
// their rules, the basic form's under their fields, the scope's and the sort's under theirs, the rest in the form's note.
// quiet: shown without moving the focus (a saved query opened with errors).
function ssShowQueryErrors(page, list, quiet) {
    const adv = page.mode === 'advanced' && !page.locked;
    const focused = document.activeElement;
    const rest = adv ? ssPlaceTreeErrors(page, list) : list.map(function (e) {
        return { path: e && (e.path || e.Path), message: (e && (e.message || e.Message)) || '' };
    });
    if (quiet && adv && focused && typeof focused.focus === 'function') {
        focused.focus();
    }

    const errors = {};
    const unplaced = [];
    rest.forEach(function (e) {
        const field = ssFieldOfPath(e.path, adv ? [] : page.fields || []);
        if (field && !errors[field]) {
            errors[field] = e.message;
        } else if (!field) {
            unplaced.push(e.message);
        }
    });
    ssShowFieldErrors(page, errors);
    if (unplaced.length > 0) {
        page.formNote.textContent = unplaced.join(' ');
    } else if (adv && page.treeErrors && Object.keys(errors).length === 0) {
        page.formNote.textContent = SS_TEXT.checkRules;
    }
}

function ssPlay(item) {
    kit.playback.playItem(ssId(item.Id)).catch(function (error) {
        kit.toast(ssText('playFailed', { message: error && error.message ? error.message : String(error) }));
    });
}

function ssActions(item) {
    const name = item.Name || '';
    const open = kit.h('a', {
        class: kit.cls('icon-button') + ' jfss-open',
        href: kit.itemHref(item.Id),
        title: ssText('openName', { name: name }),
        'aria-label': ssText('openName', { name: name })
    }, kit.icon('info'));
    const play = kit.iconButton('play_arrow', ssText('playName', { name: name }), function () {
        ssPlay(item);
    });
    play.classList.add('jfss-play');
    return [open, play];
}

// The items into the grid or the list; append adds a page to what is there, otherwise the view is drawn again.
function ssRenderItems(page, items, append) {
    let container = page.resultList.firstChild;
    const wanted = page.view === 'list' ? 'list' : 'grid';
    if (!append || !container || container.getAttribute('data-jfss-view') !== wanted) {
        container = wanted === 'list' ? kit.list([], { className: 'jfss-list' }) : kit.grid([], { className: 'jfss-grid' });
        container.setAttribute('data-jfss-view', wanted);
        page.resultList.replaceChildren(container);
        items = append ? page.results.items : items;
    }

    items.forEach(function (item) {
        const node = wanted === 'list'
            ? kit.listItem(item, { className: 'jfss-row', lines: [kit.h('div', { class: 'jfss-facts', text: ssItemFacts(item) })], actions: ssActions(item) })
            : kit.card(item, { className: 'jfss-card', actions: ssActions(item) });
        container.append(node);
    });
}
