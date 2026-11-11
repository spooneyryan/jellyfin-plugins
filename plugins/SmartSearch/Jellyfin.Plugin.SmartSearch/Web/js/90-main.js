// Start-up and the page. The Advanced Search page is registered with the kit's page host, which renders it on its route
// (#/jfss-search, or Plugin Pages' link) and links it from "Library tools" while the server's head fragment lists it as
// on. The kit waits for window.ApiClient and the signed-in user before the page renders, and renders it again from the
// hash on Back, Forward, a link and a restored view (each render reads the fields, the libraries and an open saved query
// afresh).
//
// The page: two tabs (60-saved.js), and a third for a user who manages collections (65-collections.js). Search: an open saved query's name, the scope bar and the basic or advanced form
// (40-builder.js, 42-advanced.js over the field catalogue of GET /SmartSearch/Fields, 35-tree.js), then the results
// (50-results.js); Saved queries: the user's list. A hash that holds a search (run=1, or q from a link) runs it as the
// Search tab shows; a saved query's link alone (query=<id>) fills the form from it and runs it; with a search of its
// own, that search is the form's (the saved query edited). On the TV layout the root carries jfss-tv: the fields stack,
// and "Load more" waits for the remote. On a desktop the title field (the advanced form: its first control; the Saved
// tab: its find field) takes the focus when no search runs.

function renderSsPage(body, params, context) {
    const root = kit.h('div', { class: 'jfss-search' });
    const loading = kit.h('p', { class: 'jfss-loading', role: 'status', text: SS_TEXT.loading });
    root.append(loading);
    body.append(root);
    const tv = kit.layout.isTv();
    root.classList.toggle('jfss-tv', tv);
    const queryId = params && params.query ? String(params.query) : '';
    const opened = queryId ? ssApi.getQuery(queryId).then(null, function (error) {
        return { error: error };
    }) : null;
    return Promise.all([ssApi.fields(), ssApi.libraries(), opened]).then(function (answers) {
        if (!context.isCurrent()) {
            return;
        }

        const fields = answers[0] || {};
        const libraries = (answers[1] || []).filter(function (library) {
            return library && library.Id;
        });
        const kinds = (fields.ItemKinds || []).filter(function (kind) {
            return Object.prototype.hasOwnProperty.call(SS_KIND_LABELS, kind);
        });
        const page = {
            body: root,
            context: context,
            tv: tv,
            libraries: libraries,
            allowSharing: fields.AllowQuerySharing !== false,
            // What the user may do with auto-collections ({ Enabled, CanManage, IsAdmin }); a manager's page has their tab.
            acAccess: fields.AutoCollections || null,
            catalog: Object.assign(ssCatalog(fields, kinds), {
                sortKeys: fields.SortKeys && fields.SortKeys.length ? fields.SortKeys : [SS_DEFAULT_SORT]
            }),
            view: kit.storage.get('view', 'grid') === 'list' ? 'list' : 'grid',
            // Render the page again for these hash parameters (a new history entry): a saved query read afresh.
            reload: function (next) {
                kit.pages.setParams(SS_PAGE.key, ssWriteParams(Object.assign({ kinds: [], genres: [] }, next)));
                body.replaceChildren();
                renderSsPage(body, next, context);
            }
        };
        page.state = ssReadState(params, {
            kinds: page.catalog.kinds,
            sortKeys: page.catalog.sortKeys,
            libraryIds: libraries.map(function (library) {
                return library.Id;
            }),
            lastScope: kit.storage.get('lastScope', null)
        });
        const answer = answers[2];
        const saved = answer && !answer.error ? answer : null;
        const savedForm = ssSavedForm(page, saved);
        const savedState = savedForm ? savedForm.state : null;
        if (saved && savedState && !ssHasFormParams(params)) {
            Object.assign(page.state, savedState, { run: saved.Valid !== false });
        } else if (saved && !savedState) {
            // Locked: one this version can't read never runs; one the form can't show exactly runs as it was saved.
            page.state.run = !!saved.Supported && saved.Valid !== false && !ssHasFormParams(params);
        }

        page.mode = page.state.mode === 'adv' ? 'advanced' : 'basic';
        page.tree = ssTreeFromWhere(page.state.where, page.catalog);
        const builder = kit.h('div', { class: 'jfss-builder' }, [ssBuildScopeBar(page), ssBuildBasicForm(page)]);
        ['input', 'change', 'click'].forEach(function (type) {
            builder.addEventListener(type, function () {
                ssSyncActions(page);
            });
        });
        page.searchPanel = kit.h('div', { class: 'jfss-tabpanel jfss-search-panel', role: 'tabpanel', id: ssNextId('panel-search') }, [
            ssBuildSavedHeader(page),
            builder,
            ssBuildResults(page)
        ]);
        page.collectionsPanel = ssCanManageCollections(page) ? ssBuildCollectionsTab(page) : null;
        root.replaceChildren.apply(root, [ssBuildTabs(page), page.searchPanel, ssBuildSavedTab(page), page.collectionsPanel].filter(Boolean));
        ssRenderTree(page, null);
        ssSetSaved(page, saved, savedState);
        if (answer && answer.error) {
            page.savedError = answer.error;
            ssRenderSavedHeader(page);
        }

        if (savedForm && savedForm.dropped.length > 0) {
            page.kindsNote.textContent = ssText('kindsDropped', { kinds: savedForm.dropped.map(function (kind) {
                return SS_KIND_LABELS[kind] || ssDisplayText(kind);
            }).join(', ') });
        }

        if (saved && saved.Valid === false && savedState && !ssHasFormParams(params) && Array.isArray(saved.Errors)) {
            // A query with errors (imported, or valid under other settings) opens with them under its rules.
            ssShowQueryErrors(page, saved.Errors, true);
        }

        page.pendingRun = !!page.state.run;
        ssShowTab(page, page.state.tab || 'search', false);
        if (!tv && !page.state.run && page.tab === 'search' && !page.locked && !page.treeErrors) {
            if (page.mode === 'advanced') {
                ssFocusNode(page, page.tree.children.length ? { id: page.tree.children[0].id, part: page.tree.children[0].type === 'group' ? 'match' : 'field' } : { id: page.tree.id, part: 'add-rule' });
            } else {
                page.titleInput.focus();
            }
        } else if (!tv && page.tab === 'saved') {
            page.findInput.focus();
        }
    }).catch(function (error) {
        if (!context.isCurrent()) {
            return;
        }

        const message = error && error.message ? error.message : String(error);
        root.replaceChildren(kit.notice({
            kind: 'error',
            text: error && error.isFeatureDisabled ? message : ssText('loadFailed', { message: message }),
            action: error && error.isFeatureDisabled ? null : { label: SS_TEXT.retry, onClick: function () {
                body.replaceChildren();
                renderSsPage(body, params, context);
            } }
        }));
    });
}

kit.pages.register(Object.assign({ render: renderSsPage }, SS_PAGE));
kit.start();
