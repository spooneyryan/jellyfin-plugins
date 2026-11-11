// The scope bar and the search form (basic here, advanced in 42-advanced.js).
//
// Scope bar: "Find [Movies][Shows][…]" as toggle buttons (aria-pressed; at least one stays on) over the kinds the server
// allows (GET /SmartSearch/Fields' ItemKinds), "In [All my libraries ▾]" over GET /SmartSearch/Libraries (a search of
// several libraries, from a saved query or a link, shows them as one more choice), and "Include missing episodes" while
// Episodes is on. Then Basic | Advanced. Basic form: Title contains, Year from and to, Genres (any of) as chips with
// suggestions from GET /SmartSearch/Values/Genres for the chosen library (typed after 250 ms; Enter adds the typed name,
// ArrowDown goes to the suggestions), then Search (Enter in any field runs it), Reset, and the saved query's actions
// (Save, Save as new…, Share; 60-saved.js). Every control has a label; an error shows under its field (role="alert").

const SS_SUGGEST_DELAY_MS = 250;
const SS_SUGGEST_LIMIT = 8;

let ssIdCounter = 0;

// An id for a label of our own, unique on the page (a page shown twice keeps both).
function ssNextId(name) {
    ssIdCounter += 1;
    return 'jfss-' + name + '-' + ssIdCounter;
}

function ssErrorNode(field) {
    return kit.h('div', { class: kit.cls('error') + ' jfss-field-error', 'data-jfss-error': field, role: 'alert' });
}

// Enter in a field runs the search (outside an IME composition).
function ssOnEnter(input, run) {
    input.addEventListener('keydown', function (event) {
        if (event.key === 'Enter' && !event.isComposing) {
            event.preventDefault();
            run();
        }
    });
}

function ssBuildScopeBar(page) {
    const h = kit.h;
    page.kindButtons = page.catalog.kinds.map(function (kind) {
        return h('button', {
            type: 'button',
            class: 'jfss-segment jfss-kind',
            'data-jfss-kind': kind,
            'aria-pressed': page.state.kinds.indexOf(kind) >= 0 ? 'true' : 'false',
            text: SS_KIND_LABELS[kind] || kind,
            onclick: function () {
                ssToggleKind(page, kind);
            }
        });
    });
    page.kindsNote = h('span', { class: 'jfss-kinds-note', role: 'status' });
    page.librarySelect = kit.select({ class: 'jfss-library' }, [{ value: '', text: SS_TEXT.allLibraries }].concat(page.libraries.map(function (library) {
        return { value: ssId(library.Id), text: library.Name };
    })));
    ssSetLibraryValue(page, page.state.lib);
    page.librarySelect.addEventListener('change', function () {
        ssRefreshSuggestions(page);
    });
    page.missingBox = h('input', { type: 'checkbox', class: 'jfss-missing-box' });
    page.missingBox.checked = !!page.state.missing;
    page.missingRow = h('label', { class: kit.cls('checkbox') + ' jfss-missing' }, [page.missingBox, h('span', { text: SS_TEXT.includeMissing })]);
    const kindsId = ssNextId('kinds');
    const libraryField = kit.field(SS_TEXT.libraryLabel, page.librarySelect);
    libraryField.classList.add('jfss-library-field');
    page.scopeBar = h('div', { class: 'jfss-scope' }, [
        h('div', { class: kit.cls('field') + ' jfss-kinds-field' }, [
            h('span', { class: 'jfss-label', id: kindsId, text: SS_TEXT.kindsLabel }),
            h('div', { class: 'jfss-segments jfss-kinds', role: 'group', 'aria-labelledby': kindsId }, page.kindButtons),
            ssErrorNode('kinds')
        ]),
        libraryField,
        page.missingRow,
        page.kindsNote
    ]);
    libraryField.append(ssErrorNode('lib'));
    ssSyncKinds(page);
    return page.scopeBar;
}

// The library select on a value: one library, none (all of them), or several (an extra choice naming them, isolated
// from each other).
function ssSetLibraryValue(page, lib) {
    page.librarySelect.querySelectorAll('.jfss-library-several').forEach(function (option) {
        option.remove();
    });
    const ids = lib ? lib.split(',') : [];
    if (ids.length > 1) {
        const names = ids.map(function (id) {
            const library = page.libraries.filter(function (l) {
                return ssId(l.Id) === id;
            })[0];
            return ssIsolate(library ? library.Name : id);
        });
        page.librarySelect.append(kit.h('option', { class: 'jfss-library-several', value: lib, text: ssText('librariesSeveral', { count: ids.length, names: names.join(', ') }) }));
    }

    page.librarySelect.value = lib || '';
}

function ssPressedKinds(page) {
    return page.kindButtons.filter(function (button) {
        return button.getAttribute('aria-pressed') === 'true';
    }).map(function (button) {
        return button.getAttribute('data-jfss-kind');
    });
}

function ssToggleKind(page, kind) {
    const button = page.kindButtons.filter(function (b) {
        return b.getAttribute('data-jfss-kind') === kind;
    })[0];
    const on = button.getAttribute('aria-pressed') === 'true';
    if (on && ssPressedKinds(page).length === 1) {
        page.kindsNote.textContent = SS_TEXT.kindsKeepOne;
        return;
    }

    button.setAttribute('aria-pressed', on ? 'false' : 'true');
    page.kindsNote.textContent = '';
    ssSyncKinds(page);
    if (page.mode === 'advanced') {
        // The fields that apply to the kinds now searched are marked again.
        ssRenderTree(page, null);
    }
}

function ssSyncKinds(page) {
    kit.setHidden(page.missingRow, ssPressedKinds(page).indexOf('Episode') < 0);
}

function ssBuildBasicForm(page) {
    const h = kit.h;
    const run = function () {
        ssSubmit(page);
    };
    page.titleInput = kit.input({ type: 'search', class: 'jfss-title', maxlength: String(SS_TEXT_MAX), placeholder: SS_TEXT.titlePlaceholder, autocomplete: 'off', spellcheck: 'false', value: page.state.q });
    ssOnEnter(page.titleInput, run);
    const year = function (cls, label, value) {
        const input = kit.input({ type: 'number', class: 'jfss-year ' + cls, inputmode: 'numeric', min: String(SS_YEAR_MIN), max: String(SS_YEAR_MAX), step: '1', value: value, 'aria-label': label });
        ssOnEnter(input, run);
        return input;
    };
    page.fromInput = year('jfss-year-from', SS_TEXT.yearFrom, page.state.from);
    page.toInput = year('jfss-year-to', SS_TEXT.yearTo, page.state.to);
    const yearsLabelId = ssNextId('years');
    const titleField = kit.field(SS_TEXT.titleLabel, page.titleInput);
    titleField.classList.add('jfss-title-field');
    titleField.append(ssErrorNode('q'));
    const yearsField = h('div', { class: kit.cls('field') + ' jfss-years-field', role: 'group', 'aria-labelledby': yearsLabelId }, [
        h('span', { class: 'jfss-label', id: yearsLabelId, text: SS_TEXT.yearLabel }),
        h('div', { class: kit.cls('field-row') + ' jfss-years' }, [page.fromInput, h('span', { class: 'jfss-dash', 'aria-hidden': 'true', text: '–' }), page.toInput]),
        ssErrorNode('years')
    ]);

    page.genres = page.state.genres.slice();
    page.chips = h('div', { class: 'jfss-chips', role: 'list', 'aria-label': SS_TEXT.genreChosen });
    page.genreInput = kit.input({ type: 'text', class: 'jfss-genre-input', maxlength: String(SS_TEXT_MAX), placeholder: SS_TEXT.genrePlaceholder, autocomplete: 'off', spellcheck: 'false' });
    page.genreAdd = kit.button(SS_TEXT.genreAdd, { className: 'jfss-genre-add', onClick: function () {
        ssAddGenre(page, page.genreInput.value);
    } });
    page.suggestions = h('div', { class: 'jfss-suggestions', role: 'group', 'aria-label': SS_TEXT.genreSuggestions });
    page.genreInput.addEventListener('keydown', function (event) {
        if (event.key === 'Enter' && !event.isComposing) {
            event.preventDefault();
            if (String(page.genreInput.value).trim()) {
                ssAddGenre(page, page.genreInput.value);
            } else {
                run();
            }
        } else if (event.key === 'ArrowDown') {
            const first = page.suggestions.querySelector('button');
            if (first) {
                event.preventDefault();
                first.focus();
            }
        }
    });
    page.genreInput.addEventListener('input', function () {
        ssScheduleSuggestions(page);
    });
    page.genreInput.addEventListener('focus', function () {
        if (!page.suggestionsShown) {
            ssScheduleSuggestions(page);
        }
    });
    const genreField = kit.field(SS_TEXT.genreLabel, page.genreInput, [page.genreAdd]);
    genreField.classList.add('jfss-genre-field');
    genreField.append(page.chips, page.suggestions, ssErrorNode('genres'));
    ssRenderChips(page);

    page.searchButton = kit.button(SS_TEXT.search, { variant: 'primary', className: 'jfss-search-button', onClick: run });
    page.resetButton = kit.button(SS_TEXT.reset, { className: 'jfss-reset', onClick: function () {
        ssReset(page);
    } });
    page.saveButton = kit.button(SS_TEXT.save, { className: 'jfss-save', onClick: function () {
        ssSave(page);
    } });
    page.saveAsNewButton = kit.button(SS_TEXT.saveAsNew, { className: 'jfss-save-as-new', onClick: function () {
        ssSaveAsNew(page);
    } });
    page.shareToggle = h('button', { type: 'button', class: 'jfss-segment jfss-share-toggle', 'aria-pressed': 'false', text: SS_TEXT.shareToggle, onclick: function () {
        ssShareOpen(page);
    } });
    // A manager's "Create auto-collection…" (65-collections.js): from the open saved query, or the search saved first.
    page.createCollectionButton = ssCanManageCollections(page) ? kit.button(SS_TEXT.acCreateFromSearch, { className: 'jfss-create-collection', onClick: function () {
        ssCreateCollectionFromSearch(page);
    } }) : null;
    page.formNote = h('div', { class: kit.cls('error') + ' jfss-form-error', role: 'alert' });
    page.basicFields = h('div', { class: 'jfss-basic-fields' }, [titleField, yearsField, genreField]);
    return h('div', { class: 'jfss-basic' }, [
        ssBuildModeSwitch(page),
        page.basicFields,
        ssBuildAdvanced(page),
        h('div', { class: 'jfss-actions' }, [page.searchButton, page.resetButton, page.saveButton, page.saveAsNewButton, page.shareToggle, page.createCollectionButton, page.formNote].filter(Boolean))
    ]);
}

function ssRenderChips(page) {
    page.chips.replaceChildren();
    page.genres.forEach(function (genre) {
        const chip = kit.h('button', {
            type: 'button',
            class: 'jfss-chip',
            'data-jfss-genre': genre,
            'aria-label': ssText('genreRemove', { name: genre }),
            onclick: function () {
                const at = page.genres.indexOf(genre);
                if (at >= 0) {
                    page.genres.splice(at, 1);
                }

                ssRenderChips(page);
                page.genreInput.focus();
            }
        }, [kit.h('bdi', { text: genre }), kit.icon('close', 'jfss-chip-icon')]);
        page.chips.append(kit.h('div', { role: 'listitem', class: 'jfss-chip-item' }, chip));
    });
    kit.setHidden(page.chips, page.genres.length === 0);
}

function ssAddGenre(page, value) {
    const genre = String(value || '').trim().substring(0, SS_TEXT_MAX);
    if (genre && page.genres.length < SS_VALUES_MAX && !page.genres.some(function (g) {
        return g.toLowerCase() === genre.toLowerCase();
    })) {
        page.genres.push(genre);
        ssRenderChips(page);
    }

    page.genreInput.value = '';
    ssScheduleSuggestions(page);
    page.genreInput.focus();
}

function ssScheduleSuggestions(page) {
    clearTimeout(page.suggestTimer);
    page.suggestTimer = setTimeout(function () {
        ssRefreshSuggestions(page);
    }, SS_SUGGEST_DELAY_MS);
}

// The genres of the chosen library (all the user's without one) that contain the typed text, those not chosen yet.
function ssRefreshSuggestions(page) {
    if (!page.genreInput || !page.context.isShown()) {
        return;
    }

    if (page.suggestAbort) {
        page.suggestAbort.abort();
    }

    const abort = typeof AbortController === 'function' ? new AbortController() : null;
    page.suggestAbort = abort;
    const lib = page.librarySelect.value;
    ssApi.values('Genres', String(page.genreInput.value || '').trim(), lib ? lib.split(',') : [], SS_SUGGEST_LIMIT + page.genres.length, abort ? abort.signal : undefined).then(function (values) {
        if (page.suggestAbort !== abort || !page.context.isShown()) {
            return;
        }

        const chosen = page.genres.map(function (g) {
            return g.toLowerCase();
        });
        const fresh = (values || []).filter(function (v) {
            return chosen.indexOf(String(v.Value).toLowerCase()) < 0;
        }).slice(0, SS_SUGGEST_LIMIT);
        page.suggestions.replaceChildren();
        fresh.forEach(function (v) {
            page.suggestions.append(kit.h('button', {
                type: 'button',
                class: 'jfss-suggestion',
                'data-jfss-genre': v.Value,
                text: ssText('genreCount', { name: v.Value, count: v.Count }),
                onclick: function () {
                    ssAddGenre(page, v.Value);
                }
            }));
        });
        page.suggestionsShown = fresh.length > 0;
        kit.setHidden(page.suggestions, fresh.length === 0);
    }).catch(function () {
        // Suggestions are a convenience: a failed or aborted request leaves the list as it was.
    });
}

// The form's state: the scope bar, the fields (the basic form's, or the advanced form's rules), and what the page keeps
// (tab, query, sort, order).
function ssReadForm(page) {
    const kinds = ssPressedKinds(page);
    const adv = page.mode === 'advanced';
    return {
        tab: page.state.tab,
        query: page.state.query,
        scope: page.state.scope,
        find: page.state.find,
        kinds: kinds,
        lib: page.librarySelect.value,
        missing: page.missingBox.checked && kinds.indexOf('Episode') >= 0,
        q: String(page.titleInput.value || '').trim().substring(0, SS_TEXT_MAX),
        from: String(page.fromInput.value || '').trim(),
        to: String(page.toInput.value || '').trim(),
        genres: page.genres.slice(),
        sort: page.state.sort,
        desc: page.state.desc,
        mode: adv ? 'adv' : '',
        where: adv ? ssWhereFromTree(page.tree, page.catalog) : null,
        run: true
    };
}

// The form's own checks before a search or a save: the basic form's years, or the advanced form's rules; the errors
// shown, true when nothing stops it.
function ssCheckForm(page, state) {
    if (page.mode === 'advanced') {
        ssShowFieldErrors(page, {});
        return ssCheckAdvanced(page);
    }

    const errors = ssCheckState(state);
    ssShowFieldErrors(page, errors);
    return Object.keys(errors).length === 0;
}

// The errors under the fields: { field: message }; a field not named is cleared.
function ssShowFieldErrors(page, errors) {
    page.body.querySelectorAll('[data-jfss-error]').forEach(function (node) {
        node.textContent = errors[node.getAttribute('data-jfss-error')] || '';
    });
    page.formNote.textContent = Object.keys(errors).length > 0 ? SS_TEXT.checkFields : '';
}

// Search: the form's checks, then the state into the hash (a new history entry) and the last scope remembered, then
// the first page. A locked saved query (60-saved.js) runs as it was saved.
function ssSubmit(page) {
    if (page.locked) {
        page.state.run = true;
        kit.pages.setParams(SS_PAGE.key, ssPageParams(page));
        ssRunSearch(page);
        return;
    }

    const state = ssReadForm(page);
    if (!ssCheckForm(page, state)) {
        return;
    }

    page.state = state;
    kit.storage.set('lastScope', { kinds: state.kinds, lib: state.lib });
    kit.pages.setParams(SS_PAGE.key, ssWriteParams(state));
    ssRunSearch(page);
    ssSyncActions(page);
}

// Reset: the fields emptied (the advanced form's rules too, its form kept), the sort back to its default and no results;
// the scope bar is kept. A saved query open is closed: what follows is a new search.
function ssReset(page) {
    ssCloseSaved(page);
    page.titleInput.value = '';
    page.fromInput.value = '';
    page.toInput.value = '';
    page.genreInput.value = '';
    page.genres = [];
    ssRenderChips(page);
    ssShowFieldErrors(page, {});
    page.tree = ssNewGroup('All', false, []);
    page.treeErrors = null;
    ssRenderTree(page, null);
    const state = ssReadForm(page);
    state.sort = SS_DEFAULT_SORT;
    state.desc = false;
    state.run = false;
    page.state = state;
    kit.pages.setParams(SS_PAGE.key, ssWriteParams(state));
    ssClearResults(page);
    page.sortSelect.value = state.sort;
    page.orderSelect.value = 'asc';
    ssSyncActions(page);
    if (page.mode === 'advanced') {
        ssFocusNode(page, { id: page.tree.id, part: 'add-rule' });
    } else {
        page.titleInput.focus();
    }
}
