// Saved queries: the page's two tabs, a saved query open on the Search tab, the Saved queries tab, and their dialogs.
//
// Tabs: Search and Saved queries (tab=saved in the hash), a tablist of two buttons; Left and Right move between them
// outside the TV layout (there the D-pad is jellyfin-web's).
//
// A saved query opens from its link (query=<id>; the Saved tab's names are such links): the page reads it
// (GET /SmartSearch/Queries/{id}), shows its name, owner and description above the form, fills the form from its
// definition and runs it: the basic form when it can show it as it is, else the advanced form (other fields, groups,
// several libraries). What the form doesn't show is kept and sent as saved: a library the user can't open (counted,
// never named), sort keys after the first, a limit; once the user changes the libraries, the badge says the ones they
// can't open are left out. A query written by a newer version is "locked": the form is hidden, it doesn't run, and Save
// as new duplicates it. So is one the advanced form wouldn't send back exactly as saved (ssSameQuery: member names in
// another case, a member the form doesn't know), except that it runs as saved. One with errors opens with its errors
// under its rules, not run. While
// the form shows the saved query unchanged, a search runs it by id (POST /Queries/{id}/Search, as the user); once
// changed, as any search.
// Actions: Save (a new query: the save dialog; an open one the user may change: PUT with the IfUpdatedAt it read, enabled
// once the form differs from it), Save as new…, and the owner's Share switch. A save refused because the query changed
// elsewhere offers Reload or Save as new; auto-collections' refusals are shown with their names and reasons.
//
// The Saved queries tab: Mine | Shared with me | All (scope=Mine|Shared), a find field (find=, by name and
// description), and the list: each name a link that opens it, its owner when it isn't the user's ("Deleted user" as the
// server says; never an id), Shared, Updated, the auto-collections using it, Needs a newer Smart Search, Has errors.
// The user's own rows offer Rename, Duplicate, Share or Stop sharing, Copy link and Delete; others' Duplicate and Copy
// link. Copy link isn't offered on the TV layout (no clipboard to paste into there). Every dialog is the kit's, with
// scopedKeys: the focus starts inside, stays inside, Escape and a remote's Back close it, and the focus goes back.
//
// Names and descriptions are users' text: always text nodes, isolated (<bdi>, or ssIsolate in a sentence), with control
// characters shown as U+FFFD.

const SS_NAME_MAX = 100;
const SS_DESCRIPTION_MAX = 500;

// ------------------------------------------------------------------------------------------------ helpers

function ssBdi(text, cls) {
    return kit.h('bdi', { class: cls, dir: 'auto', text: ssDisplayText(text) });
}

// A sentence of SS_TEXT with one {name} given as a node (a <bdi>): [text, node, text].
function ssSentence(key, name, node) {
    const parts = String(SS_TEXT[key]).split('{' + name + '}');
    return [parts[0] ? document.createTextNode(parts[0]) : null, node, parts[1] ? document.createTextNode(parts[1]) : null].filter(Boolean);
}

function ssErrorMessage(error) {
    return error && error.message ? error.message : String(error);
}

// The auto-collections a refusal names ({ id, name, messages }), as one line each: name, then its messages.
function ssAutoCollectionLines(error) {
    const list = error && error.problem && Array.isArray(error.problem.autoCollections) ? error.problem.autoCollections : [];
    return list.map(function (c) {
        const messages = Array.isArray(c && c.messages) ? c.messages : [];
        return ssDisplayText(c && c.name) + (messages.length ? ': ' + messages.map(ssDisplayText).join(' ') : '');
    });
}

// The link to a saved query, absolute (for the clipboard): this server's web client, then the page's link for the host
// the page uses.
function ssQueryLink(id) {
    return String(window.location.href).split('#')[0] + kit.pages.href(SS_PAGE.key, { query: ssId(id) });
}

// The hash parameters the page writes: a locked saved query's are only the tab, the query and the Saved tab's.
function ssPageParams(page) {
    if (page.locked) {
        return ssWriteParams({ tab: page.state.tab, edit: page.state.edit, query: page.state.query, scope: page.state.scope, find: page.state.find, kinds: [], genres: [], run: page.state.run });
    }

    return ssWriteParams(page.state);
}

// ------------------------------------------------------------------------------------------------ tabs

function ssBuildTabs(page) {
    const h = kit.h;
    const tabs = [['search', SS_TEXT.tabSearch], ['saved', SS_TEXT.tabSaved]];
    if (page.collectionsPanel) {
        tabs.push(['collections', SS_TEXT.tabCollections]);
    }

    page.tabButtons = tabs.map(function (tab) {
        return h('button', {
            type: 'button',
            role: 'tab',
            id: ssNextId('tab-' + tab[0]),
            class: 'jfss-tab',
            'data-jfss-tab': tab[0],
            'aria-selected': 'false',
            text: tab[1],
            onclick: function () {
                ssShowTab(page, tab[0], true);
            }
        });
    });
    const list = h('div', { class: 'jfss-tabs', role: 'tablist', 'aria-label': SS_TEXT.tabsLabel }, page.tabButtons);
    list.addEventListener('keydown', function (event) {
        if (page.tv || (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight')) {
            return;
        }

        const at = page.tabButtons.indexOf(document.activeElement);
        if (at < 0) {
            return;
        }

        event.preventDefault();
        const next = page.tabButtons[(at + (event.key === 'ArrowRight' ? 1 : page.tabButtons.length - 1)) % page.tabButtons.length];
        next.focus();
        next.click();
    });
    return list;
}

// Shows a tab; push: the user chose it (a history entry). The Search tab runs a search the hash asked for once it shows;
// the Saved tab reads the list each time it shows, and so does the Auto-collections tab (only a manager's page has it).
function ssShowTab(page, tab, push) {
    page.tab = tab === 'saved' || (tab === 'collections' && page.collectionsPanel) ? tab : 'search';
    const panels = { search: page.searchPanel, saved: page.savedPanel, collections: page.collectionsPanel };
    page.tabButtons.forEach(function (button) {
        const key = button.getAttribute('data-jfss-tab');
        const on = key === page.tab;
        button.setAttribute('aria-selected', on ? 'true' : 'false');
        button.setAttribute('aria-controls', on ? panels[key].id : '');
        panels[key].setAttribute('aria-labelledby', button.id);
        kit.setHidden(panels[key], !on);
    });
    if (push) {
        page.state.tab = page.tab;
        page.state.edit = null;
        kit.pages.setParams(SS_PAGE.key, ssPageParams(page));
    }

    if (page.tab !== 'collections') {
        ssStopCollectionsPoll(page);
    }

    if (page.tab === 'saved') {
        ssLoadSaved(page);
    } else if (page.tab === 'collections') {
        ssLoadCollections(page);
    } else if (page.pendingRun) {
        page.pendingRun = false;
        ssRunSearch(page);
    }
}

// ------------------------------------------------------------------------------------------------ a saved query on the Search tab

function ssBuildSavedHeader(page) {
    page.savedHeader = kit.h('div', { class: 'jfss-saved-header' });
    return page.savedHeader;
}

function ssRenderSavedHeader(page) {
    const h = kit.h;
    const header = page.savedHeader;
    header.replaceChildren();
    if (page.savedError) {
        const error = page.savedError;
        header.append(kit.notice({
            kind: 'error',
            text: error.status === 404 || error.status === 400 ? SS_TEXT.savedNotFound : ssText('savedOpenFailed', { message: ssErrorMessage(error) })
        }));
    }

    const saved = page.saved;
    kit.setHidden(header, !saved && !page.savedError);
    if (!saved) {
        return;
    }

    const meta = [];
    if (!saved.IsOwner) {
        meta.push(h('span', { class: 'jfss-saved-owner' }, ssSentence('sharedBy', 'owner', ssBdi(saved.OwnerName))));
    } else if (saved.Shared) {
        meta.push(h('span', { class: 'jfss-badge jfss-badge-shared', text: SS_TEXT.sharedBadge }));
    }

    page.unavailableBadge = null;
    if (page.unavailable > 0) {
        page.unavailableBadge = h('span', { class: 'jfss-badge jfss-badge-unavailable' });
        meta.push(page.unavailableBadge);
        ssSyncUnavailable(page);
    }

    // append() writes a null as the text "null": only the parts there are.
    header.append.apply(header, [
        h('span', { class: 'jfss-label', text: SS_TEXT.savedHeading }),
        h('h2', { class: 'jfss-saved-name' }, ssBdi(saved.Name)),
        meta.length ? h('div', { class: 'jfss-saved-meta' }, meta) : null,
        saved.Description ? h('p', { class: 'jfss-saved-description' }, ssBdi(saved.Description)) : null
    ].filter(Boolean));
    if (!saved.Supported) {
        header.append(kit.notice({ kind: 'info', text: SS_TEXT.lockedUnsupported }));
    } else if (page.locked) {
        header.append(kit.notice({ kind: 'info', text: SS_TEXT.lockedInexact }));
    }

    if (saved.Supported && !saved.Valid) {
        header.append(kit.notice({ kind: 'error', text: ssText('lockedInvalid', { messages: (saved.Errors || []).map(function (e) {
            return ssDisplayText(e && (e.Message || e.message));
        }).join(' ') }) }));
    }
}

// The badge of the libraries the user can't open: counted, and, once the form's libraries differ from those saved (the
// query then searches only the ones chosen), it says that they are left out.
function ssSyncUnavailable(page) {
    const badge = page.unavailableBadge;
    if (!badge) {
        return;
    }

    const dropped = !page.locked && !!page.kept && page.kept.libraries.length > 0 && ssReadForm(page).lib !== page.kept.lib;
    const n = page.unavailable;
    badge.textContent = dropped
        ? (n === 1 ? SS_TEXT.librariesDroppedOne : ssText('librariesDroppedMany', { count: n }))
        : (n === 1 ? SS_TEXT.librariesUnavailableOne : ssText('librariesUnavailableMany', { count: n }));
    badge.classList.toggle('jfss-badge-dropped', dropped);
}

// The form shown or hidden for a locked saved query; its sort can't change either.
function ssSetLocked(page, locked) {
    page.locked = !!locked;
    kit.setHidden(page.scopeBar, page.locked);
    kit.setHidden(page.modeSwitch, page.locked);
    page.sortSelect.disabled = page.locked;
    page.orderSelect.disabled = page.locked;
    ssSyncMode(page);
}

// A saved query opened (or just saved) on the Search tab: state is the form's state when the form shows it, else null
// (locked). What the form doesn't show of it is kept (ssKeptOfQuery).
function ssSetSaved(page, dto, state) {
    const libraryIds = page.libraries.map(function (l) {
        return l.Id;
    });
    page.saved = dto;
    page.savedError = null;
    page.savedState = state;
    page.unavailable = dto ? ssUnavailableLibraries(dto.Query, libraryIds) : 0;
    page.kept = dto && dto.Supported ? ssKeptOfQuery(dto.Query, libraryIds) : null;
    if (dto) {
        page.state.query = ssId(dto.Id);
    }

    ssSetLocked(page, !!dto && !state);
    ssRenderSavedHeader(page);
    ssSyncActions(page);
}

// The page's own saved query closed: a new search (Reset, or the query deleted).
function ssCloseSaved(page) {
    page.state.query = null;
    ssSetSaved(page, null, null);
}

// The form a saved query's definition shows in: { state, dropped } (dropped: kinds it names that the server no longer
// allows), the basic form's state when it can show it, else the advanced form's; null (locked) for a query this version
// can't read, or one the advanced form wouldn't send back as it was saved (ssSameQuery).
function ssSavedForm(page, dto) {
    if (!dto || !dto.Supported) {
        return null;
    }

    const options = {
        kinds: page.catalog.kinds,
        sortKeys: page.catalog.sortKeys,
        catalog: page.catalog,
        libraryIds: page.libraries.map(function (library) {
            return library.Id;
        })
    };
    const basic = ssStateFromQuery(dto.Query, options);
    if (basic) {
        return { state: Object.assign(basic, { mode: '', where: null }), dropped: [] };
    }

    const advanced = ssAdvancedStateFromQuery(dto.Query, options);
    return advanced.exact ? { state: advanced.state, dropped: advanced.dropped } : null;
}

// Save, Save as new… and the Share switch for what is open.
function ssSyncActions(page) {
    if (!page.saveButton) {
        return;
    }

    const saved = page.saved;
    const dirty = !!saved && !!page.savedState && ssSearchKey(ssReadForm(page)) !== ssSearchKey(page.savedState);
    page.saveButton.disabled = !!saved && (page.locked || !saved.CanEdit || !dirty);
    kit.setHidden(page.saveAsNewButton, !saved);
    const share = !!saved && saved.IsOwner && saved.Supported && (saved.Shared || (page.allowSharing && !saved.UsesAdminOnlyFields));
    kit.setHidden(page.shareToggle, !share);
    page.shareToggle.setAttribute('aria-pressed', saved && saved.Shared ? 'true' : 'false');
    page.searchButton.disabled = !!saved && page.locked && !saved.Supported;
    if (page.createCollectionButton) {
        page.createCollectionButton.disabled = !!saved && (!saved.Supported || saved.Valid === false);
    }

    ssSyncUnavailable(page);
    ssSyncMode(page);
}

// The saved query's id when a search runs it by id: a locked one, or one the form shows unchanged.
function ssSavedRunId(page) {
    const saved = page.saved;
    if (!saved || !saved.Supported) {
        return null;
    }

    return page.locked || (page.savedState && ssSearchKey(page.state) === ssSearchKey(page.savedState)) ? ssId(saved.Id) : null;
}

// Save on the Search tab: a new query through the save dialog; the open one changed in place (PUT).
function ssSave(page) {
    if (!page.saved) {
        ssSaveAsNew(page);
        return;
    }

    if (page.locked || !page.saved.CanEdit) {
        return;
    }

    const state = ssReadForm(page);
    if (!ssCheckForm(page, state)) {
        return;
    }

    const built = ssBuildQuery(state, page.kept);
    const saved = page.saved;
    page.fields = built.fields;
    page.saveButton.disabled = true;
    const done = function (dto) {
        if (!page.context.isShown()) {
            return;
        }

        state.run = page.state.run;
        page.state = state;
        ssSetSaved(page, dto, state);
        kit.pages.setParams(SS_PAGE.key, ssPageParams(page), { replace: true });
        kit.toast(ssText('saved', { name: ssIsolate(dto.Name) }));
    };
    const failed = function (error, detached) {
        ssSyncActions(page);
        if (!page.context.isShown()) {
            return;
        }

        if (error && error.code === 'IfUpdatedAt') {
            ssConflictDialog(page, saved);
        } else if (!detached && error && (error.code === 'BreaksAutoCollections' || error.code === 'ManagerRequired')) {
            // Save as new, or detach them (each keeps a copy of the query as saved) and then save this change.
            const count = ssAutoCollectionLines(error).length;
            ssAutoCollectionsDialog(error, function () {
                ssSaveAsNew(page);
            }, function () {
                page.saveButton.disabled = true;
                ssPutQuery(saved, { Query: built.query, DetachAutoCollections: true }).then(function (dto) {
                    kit.toast(count === 1 ? SS_TEXT.detachedOne : ssText('detachedMany', { count: count }));
                    done(dto);
                }).catch(function (again) {
                    failed(again, true);
                });
            });
        } else if (error && Array.isArray(error.errors) && error.errors.length > 0 && error.code !== 'InvalidRequest') {
            ssShowSearchError(page, error);
        } else if (error && error.code !== 'Cancelled') {
            kit.toast(ssErrorMessage(error));
        }
    };
    ssPutQuery(saved, { Query: built.query }).then(done).catch(function (error) {
        failed(error, false);
    });
}

// Save as new…: the open query when locked (a copy of it, by the server), else the form's search as a new query. The
// advanced form's query is checked by the server first (POST /Validate), so its errors show under its rules before a
// name is asked for, and a query using an administrator-only field isn't offered for sharing. then(dto), when given,
// runs after the save.
function ssSaveAsNew(page, then) {
    if (page.saved && page.locked) {
        ssOpenDuplicateDialog(page, page.saved, function (copy) {
            ssSetSaved(page, copy, null);
            kit.pages.setParams(SS_PAGE.key, ssPageParams(page), { replace: true });
            if (then) {
                then(copy);
            }
        });
        return;
    }

    const state = ssReadForm(page);
    if (!ssCheckForm(page, state)) {
        return;
    }

    const query = ssBuildQuery(state, page.kept).query;
    const open = function (adminOnly) {
        ssOpenSaveDialog(page, {
            mode: page.saved ? 'asNew' : 'new',
            query: query,
            adminOnly: adminOnly,
            returnFocus: page.saved ? page.saveAsNewButton : page.saveButton,
            onSaved: function (dto) {
                state.run = page.state.run;
                state.tab = page.state.tab;
                state.scope = page.state.scope;
                state.find = page.state.find;
                page.state = state;
                ssSetSaved(page, dto, state);
                kit.pages.setParams(SS_PAGE.key, ssPageParams(page), { replace: true });
                if (page.tab === 'saved') {
                    ssLoadSaved(page);
                }

                if (then) {
                    then(dto);
                }
            }
        });
    };
    if (page.mode !== 'advanced') {
        open(false);
        return;
    }

    ssApi.validate(query).then(function (result) {
        if (!page.context.isShown()) {
            return;
        }

        const errors = result && Array.isArray(result.Errors) ? result.Errors : [];
        if (result && result.Valid === false && errors.length > 0) {
            ssShowQueryErrors(page, errors);
            return;
        }

        open(!!(result && result.UsesAdminOnlyFields));
    }, function () {
        // The save itself says what is wrong.
        open(false);
    });
}

// The owner's Share switch on the Search tab.
function ssShareOpen(page) {
    if (!page.saved) {
        return;
    }

    ssToggleShare(page, page.saved, function (dto) {
        ssSetSaved(page, dto, page.savedState);
    }, page.shareToggle);
}

// PUT of a query the user read (dto): its name, description, sharing and definition as read, with changes, and the
// UpdatedUtc read as IfUpdatedAt. Auto-collections that need a confirmation get it (and the save is sent again with
// AcknowledgeAutoCollectionWarnings); a declined confirmation rejects with code Cancelled. After a save that affects
// auto-collections, a toast says how many.
function ssPutQuery(dto, changes, acknowledged) {
    const body = Object.assign({ Name: dto.Name, Description: dto.Description || '', Shared: !!dto.Shared, Query: dto.Query, IfUpdatedAt: dto.UpdatedUtc }, changes);
    if (acknowledged) {
        body.AcknowledgeAutoCollectionWarnings = true;
    }

    return ssApi.updateQuery(ssId(dto.Id), body).then(function (result) {
        const affected = result && Array.isArray(result.AffectedAutoCollections) ? result.AffectedAutoCollections.length : 0;
        if (affected > 0) {
            kit.toast(affected === 1 ? SS_TEXT.autoCollectionsUpdateOne : ssText('autoCollectionsUpdate', { count: affected }));
        }

        return result;
    }, function (error) {
        if (!acknowledged && error && error.code === 'AutoCollectionsNeedAttention') {
            return kit.confirm({
                title: SS_TEXT.autoCollectionsTitle,
                message: [ssErrorMessage(error)].concat(ssAutoCollectionLines(error)).join(' '),
                confirmLabel: SS_TEXT.confirmChange,
                cancelLabel: SS_TEXT.cancel
            }).then(function (yes) {
                if (!yes) {
                    const cancelled = new Error(SS_TEXT.cancel);
                    cancelled.code = 'Cancelled';
                    throw cancelled;
                }

                return ssPutQuery(dto, changes, true);
            });
        }

        throw error;
    });
}

// A save refused because the query changed elsewhere since it was read: Reload (the page renders the query as it is
// now) or Save as new (the form's search as a new query).
function ssConflictDialog(page, saved) {
    const dialog = kit.dialog({ title: SS_TEXT.conflictTitle, modal: true, scopedKeys: true, returnFocus: page.saveButton });
    const reload = kit.button(SS_TEXT.reload, { variant: 'primary', className: 'jfss-reload', onClick: function () {
        dialog.close();
        page.reload({ tab: page.state.tab, query: ssId(saved.Id) });
    } });
    dialog.body.append(
        kit.h('p', { text: ssText('conflictMessage', { name: ssIsolate(saved.Name) }) }),
        kit.h('div', { class: kit.cls('actions') }, [
            kit.button(SS_TEXT.cancel, { onClick: dialog.close }),
            kit.button(SS_TEXT.saveAsNew, { className: 'jfss-conflict-save-as-new', onClick: function () {
                dialog.close();
                page.locked = false;
                ssOpenSaveDialog(page, {
                    mode: 'asNew',
                    query: ssBuildQuery(ssReadForm(page), page.kept).query,
                    returnFocus: page.saveAsNewButton,
                    onSaved: function (dto) {
                        const state = ssReadForm(page);
                        state.run = page.state.run;
                        page.state = state;
                        ssSetSaved(page, dto, state);
                        kit.pages.setParams(SS_PAGE.key, ssPageParams(page), { replace: true });
                    }
                });
            } }),
            reload
        ]));
    reload.focus();
}

// Auto-collections refused a change (BreaksAutoCollections, ManagerRequired): the server's sentence, each collection
// with its reasons, Save as new when given, and "Detach these collections, then save" when given.
function ssAutoCollectionsDialog(error, saveAsNew, detachAndSave) {
    const dialog = kit.dialog({ title: SS_TEXT.autoCollectionsTitle, modal: true, scopedKeys: true });
    const lines = ssAutoCollectionLines(error);
    dialog.body.append(
        kit.h('p', { text: ssErrorMessage(error) }),
        lines.length ? kit.h('ul', { class: 'jfss-auto-collections' }, lines.map(function (line) {
            return kit.h('li', {}, kit.h('bdi', { dir: 'auto', text: line }));
        })) : null,
        detachAndSave ? kit.h('p', { class: 'jfss-detach-note', text: SS_TEXT.detachAndSaveNote }) : null,
        kit.h('div', { class: kit.cls('actions') }, [
            kit.button(SS_TEXT.cancel, { onClick: dialog.close }),
            detachAndSave ? kit.button(SS_TEXT.detachAndSave, { className: 'jfss-detach-save', onClick: function () {
                dialog.close();
                detachAndSave();
            } }) : null,
            saveAsNew ? kit.button(SS_TEXT.saveAsNew, { variant: 'primary', onClick: function () {
                dialog.close();
                saveAsNew();
            } }) : null
        ]));
}

// ------------------------------------------------------------------------------------------------ the dialogs

// A form dialog: the fields, an error line, Cancel and the primary button; Enter in a one-line field submits.
// onSubmit(setBusy, showError) does the work and closes the dialog itself.
function ssFormDialog(opts) {
    const h = kit.h;
    const dialog = kit.dialog({
        title: opts.title,
        modal: true,
        scopedKeys: true,
        returnFocus: opts.returnFocus,
        initialFocus: opts.initialFocus,
        onKey: function (event) {
            const target = event.target;
            if (event.key === 'Enter' && !event.isComposing && target && target.tagName === 'INPUT' && target.type !== 'checkbox') {
                event.preventDefault();
                submit();
                return true;
            }

            return false;
        }
    });
    const error = h('div', { class: kit.cls('error') + ' jfss-dialog-error', role: 'alert' });
    const cancel = kit.button(SS_TEXT.cancel, { className: 'jfss-dialog-cancel', onClick: dialog.close });
    const primary = kit.button(opts.submitLabel || SS_TEXT.save, { variant: 'primary', type: 'submit', className: 'jfss-dialog-submit' });
    let busy = false;
    const setBusy = function (value) {
        busy = value;
        primary.disabled = value;
        cancel.disabled = value;
    };
    const submit = function () {
        if (!busy) {
            opts.onSubmit(setBusy, function (field, message) {
                const node = field ? form.querySelector('[data-jfss-dialog-error="' + field + '"]') : null;
                form.querySelectorAll('[data-jfss-dialog-error]').forEach(function (n) {
                    n.textContent = '';
                });
                error.textContent = '';
                (node || error).textContent = message || '';
            }, dialog);
        }
    };
    const form = h('form', {
        class: 'jfss-form ' + (opts.className || ''),
        onsubmit: function (event) {
            event.preventDefault();
            submit();
        }
    }, (opts.content || []).concat([error, h('div', { class: kit.cls('actions') }, [cancel, primary])]));
    dialog.body.append(form);
    return dialog;
}

function ssDialogFieldError(field) {
    return kit.h('div', { class: kit.cls('error') + ' jfss-field-error', 'data-jfss-dialog-error': field, role: 'alert' });
}

// The field a save's error belongs to: Name, Description, or none (the dialog's line).
function ssDialogErrorField(error) {
    if (error && error.code === 'DuplicateName') {
        return 'name';
    }

    const first = error && Array.isArray(error.errors) && error.errors.length ? error.errors[0] : null;
    const path = String(first && first.path || '');
    return path === 'Name' ? 'name' : (path === 'Description' ? 'description' : null);
}

function ssDialogErrorText(error) {
    const list = error && Array.isArray(error.errors) ? error.errors : [];
    return list.length > 0 && error.code === 'InvalidQuery'
        ? list.map(function (e) {
            return e && e.message;
        }).filter(Boolean).join(' ')
        : ssErrorMessage(error);
}

// Save, Save as new… and Rename: Name (required, at most 100), Description (at most 500), and "Share with all users"
// when the server allows sharing and the query uses no administrator-only field (a shared one keeps the switch, to stop
// sharing). opts: { mode: 'new' | 'asNew' | 'rename', query (new, asNew), adminOnly (new, asNew: the query uses an
// administrator-only field), dto (rename), onSaved(dto), returnFocus }.
function ssOpenSaveDialog(page, opts) {
    const h = kit.h;
    let dto = opts.dto || null;
    const rename = opts.mode === 'rename';
    const name = kit.input({ class: 'jfss-name-input', maxlength: String(SS_NAME_MAX), autocomplete: 'off', spellcheck: 'false', value: rename ? dto.Name : '' });
    const description = kit.textarea({ class: 'jfss-description-input', maxlength: String(SS_DESCRIPTION_MAX), rows: '3' });
    description.value = rename ? (dto.Description || '') : '';
    const shareBox = h('input', { type: 'checkbox', class: 'jfss-share-box' });
    shareBox.checked = rename ? !!dto.Shared : false;
    const canShare = rename ? (dto.Shared || (page.allowSharing && !dto.UsesAdminOnlyFields)) : page.allowSharing && !opts.adminOnly;
    const nameField = kit.field(SS_TEXT.nameLabel, name);
    nameField.append(ssDialogFieldError('name'));
    const descriptionField = kit.field(SS_TEXT.descriptionLabel, description);
    descriptionField.append(ssDialogFieldError('description'));
    const shareRow = canShare ? h('div', { class: 'jfss-share-row' }, [
        h('label', { class: kit.cls('checkbox') + ' jfss-share' }, [shareBox, h('span', { text: SS_TEXT.shareLabel })]),
        h('p', { class: 'jfss-share-note', text: SS_TEXT.shareNote }),
        h('p', { class: 'jfss-share-note jfss-share-warning', text: SS_TEXT.shareWarning })
    ]) : null;
    name.setAttribute('aria-required', 'true');
    ssFormDialog({
        title: rename ? SS_TEXT.renameTitle : (opts.mode === 'asNew' ? SS_TEXT.saveAsNewTitle : SS_TEXT.saveTitle),
        className: 'jfss-save-dialog',
        returnFocus: opts.returnFocus,
        initialFocus: name,
        content: [nameField, descriptionField, shareRow],
        onSubmit: function (setBusy, showError, dialog) {
            const wanted = String(name.value || '').trim();
            if (!wanted) {
                showError('name', SS_TEXT.nameRequired);
                name.focus();
                return;
            }

            setBusy(true);
            const fields = { Name: wanted, Description: String(description.value || ''), Shared: canShare ? !!shareBox.checked : false };
            const request = rename
                ? ssPutQuery(dto, fields)
                : ssApi.createQuery(Object.assign(fields, { Query: opts.query }));
            request.then(function (result) {
                dialog.close();
                kit.toast(ssText(rename ? 'renamed' : 'saved', { name: ssIsolate(result.Name) }));
                if (opts.onSaved) {
                    opts.onSaved(result);
                }
            }, function (error) {
                setBusy(false);
                if (error && error.code === 'Cancelled') {
                    return;
                }

                if (rename && error && error.code === 'IfUpdatedAt') {
                    // Changed elsewhere since it was read: read it again, so the next Save sends what it is now.
                    ssApi.getQuery(ssId(dto.Id)).then(function (latest) {
                        dto = latest;
                    }, function () {
                        // the next Save says what is wrong
                    });
                    showError(null, ssText('conflictMessage', { name: ssIsolate(dto.Name) }));
                    return;
                }

                const field = ssDialogErrorField(error);
                showError(field, ssDialogErrorText(error));
                (field === 'description' ? description : name).focus();
            });
        }
    });
}

// Duplicate: the copy's name (the original's: the server numbers it when the user has that name already).
function ssOpenDuplicateDialog(page, source, onDone, returnFocus) {
    const name = kit.input({ class: 'jfss-name-input', maxlength: String(SS_NAME_MAX), autocomplete: 'off', spellcheck: 'false', value: source.Name });
    const nameField = kit.field(SS_TEXT.nameLabel, name);
    nameField.append(ssDialogFieldError('name'));
    ssFormDialog({
        title: SS_TEXT.duplicateTitle,
        className: 'jfss-duplicate-dialog',
        submitLabel: SS_TEXT.save,
        returnFocus: returnFocus,
        initialFocus: name,
        content: [kit.h('p', { class: 'jfss-duplicate-note', text: SS_TEXT.duplicateNote }), nameField],
        onSubmit: function (setBusy, showError, dialog) {
            const wanted = String(name.value || '').trim();
            if (!wanted) {
                showError('name', SS_TEXT.nameRequired);
                return;
            }

            setBusy(true);
            const same = wanted.toLowerCase() === String(source.Name).trim().toLowerCase();
            ssApi.duplicateQuery(ssId(source.Id), same ? null : wanted).then(function (copy) {
                dialog.close();
                kit.toast(ssText('duplicated', { name: ssIsolate(copy.Name) }), {
                    action: page.tab === 'saved' ? { label: SS_TEXT.open, href: kit.pages.href(SS_PAGE.key, { tab: 'search', query: ssId(copy.Id) }) } : null
                });
                if (onDone) {
                    onDone(copy);
                }
            }, function (error) {
                setBusy(false);
                showError(ssDialogErrorField(error), ssErrorMessage(error));
                name.focus();
            });
        }
    });
}

// Share or Stop sharing, confirmed: the query read again (its definition and UpdatedUtc as they are now) and changed.
// Sharing warns first that others see the query as written (the names and values in it, its libraries).
function ssToggleShare(page, item, onDone, returnFocus) {
    const share = !item.Shared;
    kit.confirm({
        title: share ? SS_TEXT.shareTitle : SS_TEXT.unshareTitle,
        message: ssText(share ? 'shareMessage' : 'unshareMessage', { name: ssIsolate(item.Name) }),
        note: share ? SS_TEXT.shareWarning : null,
        confirmLabel: share ? SS_TEXT.share : SS_TEXT.unshare,
        cancelLabel: SS_TEXT.cancel
    }).then(function (yes) {
        if (!yes) {
            if (returnFocus && returnFocus.isConnected) {
                returnFocus.focus();
            }

            return null;
        }

        return ssApi.getQuery(ssId(item.Id)).then(function (dto) {
            return ssPutQuery(dto, { Shared: share });
        }).then(function (dto) {
            kit.toast(ssText(dto.Shared ? 'nowShared' : 'nowPrivate', { name: ssIsolate(dto.Name) }));
            if (onDone) {
                onDone(dto);
            }

            return dto;
        });
    }).catch(function (error) {
        if (!error || error.code !== 'Cancelled') {
            kit.toast(ssErrorMessage(error));
        }
    });
}

// Delete, confirmed; when auto-collections use the query, a second confirmation detaches them (they keep a copy).
function ssDeleteQuery(page, item, onDone) {
    const id = ssId(item.Id);
    const name = ssIsolate(item.Name);
    kit.confirm({
        title: SS_TEXT.deleteTitle,
        message: ssText('deleteMessage', { name: name }),
        confirmLabel: SS_TEXT.delete,
        cancelLabel: SS_TEXT.cancel
    }).then(function (yes) {
        if (!yes) {
            return false;
        }

        return ssApi.deleteQuery(id, false).then(function () {
            return true;
        }, function (error) {
            if (!error || error.code !== 'Referenced') {
                throw error;
            }

            const list = (error.problem && Array.isArray(error.problem.autoCollections) ? error.problem.autoCollections : []).map(function (c) {
                return ssIsolate(c && c.name);
            }).join(', ');
            return kit.confirm({
                title: SS_TEXT.detachTitle,
                message: ssText('detachMessage', { name: name, list: list }),
                confirmLabel: SS_TEXT.detachAndDelete,
                cancelLabel: SS_TEXT.cancel
            }).then(function (detach) {
                return detach ? ssApi.deleteQuery(id, true).then(function () {
                    return true;
                }) : false;
            });
        });
    }).then(function (deleted) {
        if (!deleted) {
            return;
        }

        kit.toast(ssText('deleted', { name: name }));
        if (page.saved && ssId(page.saved.Id) === id) {
            ssCloseSaved(page);
            kit.pages.setParams(SS_PAGE.key, ssPageParams(page), { replace: true });
        }

        if (onDone) {
            onDone();
        }
    }).catch(function (error) {
        kit.toast(ssErrorMessage(error));
    });
}

// Copy link: to the clipboard where the browser allows it (a secure page); elsewhere a dialog with the link selected.
function ssCopyLink(item, returnFocus) {
    const link = ssQueryLink(item.Id);
    const show = function () {
        const dialog = kit.dialog({ title: SS_TEXT.copyLinkTitle, scopedKeys: true, returnFocus: returnFocus });
        const input = kit.input({ class: 'jfss-link-input', readonly: 'readonly', value: link, 'aria-label': SS_TEXT.copyLinkTitle });
        dialog.body.append(kit.h('p', { text: SS_TEXT.copyLinkNote }), input,
            kit.h('div', { class: kit.cls('actions') }, [kit.button(SS_TEXT.cancel, { onClick: dialog.close })]));
        input.focus();
        if (typeof input.select === 'function') {
            input.select();
        }
    };
    const clipboard = typeof navigator !== 'undefined' && navigator.clipboard && typeof navigator.clipboard.writeText === 'function' ? navigator.clipboard : null;
    if (!clipboard) {
        show();
        return;
    }

    clipboard.writeText(link).then(function () {
        kit.toast(SS_TEXT.linkCopied);
    }, show);
}

// ------------------------------------------------------------------------------------------------ the Saved queries tab

function ssBuildSavedTab(page) {
    const h = kit.h;
    page.filterButtons = [['Mine', SS_TEXT.filterMine], ['Shared', SS_TEXT.filterShared], ['', SS_TEXT.filterAll]].map(function (filter) {
        return h('button', {
            type: 'button',
            class: 'jfss-segment jfss-filter',
            'data-jfss-filter': filter[0] || 'All',
            'aria-pressed': page.state.scope === filter[0] ? 'true' : 'false',
            text: filter[1],
            onclick: function () {
                if (page.state.scope === filter[0]) {
                    return;
                }

                page.state.scope = filter[0];
                page.filterButtons.forEach(function (b) {
                    b.setAttribute('aria-pressed', b.getAttribute('data-jfss-filter') === (filter[0] || 'All') ? 'true' : 'false');
                });
                kit.pages.setParams(SS_PAGE.key, ssPageParams(page));
                ssLoadSaved(page);
            }
        });
    });
    page.findInput = kit.searchBox({
        value: page.state.find,
        placeholder: SS_TEXT.findPlaceholder,
        maxLength: SS_TEXT_MAX,
        onSearch: function (text) {
            page.state.find = text;
            kit.pages.setParams(SS_PAGE.key, ssPageParams(page), { replace: true });
            ssRenderSaved(page);
        }
    });
    page.findInput.classList.add('jfss-find');
    const filterId = ssNextId('filter');
    const findField = kit.field(SS_TEXT.findLabel, page.findInput);
    findField.classList.add('jfss-find-field');
    page.savedCount = h('p', { class: 'jfss-count jfss-saved-count', role: 'status' });
    page.savedNotices = h('div', { class: 'jfss-notices jfss-saved-notices' });
    page.savedList = h('div', { class: 'jfss-saved-list', role: 'list', 'aria-label': SS_TEXT.savedList });
    page.savedPanel = h('div', { class: 'jfss-tabpanel jfss-saved-panel', role: 'tabpanel', id: ssNextId('panel-saved') }, [
        h('div', { class: 'jfss-saved-tools' }, [
            h('div', { class: kit.cls('field') + ' jfss-filter-field' }, [
                h('span', { class: 'jfss-label', id: filterId, text: SS_TEXT.filterLabel }),
                h('div', { class: 'jfss-segments', role: 'group', 'aria-labelledby': filterId }, page.filterButtons)
            ]),
            findField
        ]),
        page.savedCount,
        page.savedNotices,
        page.savedList
    ]);
    page.savedItems = null;
    page.savedGeneration = 0;
    return page.savedPanel;
}

// The list read again (each time the tab shows, and after a change); a newer read wins over an older one.
function ssLoadSaved(page) {
    const generation = ++page.savedGeneration;
    page.savedCount.textContent = SS_TEXT.loading;
    return ssApi.listQueries(page.state.scope || 'All').then(function (items) {
        if (generation !== page.savedGeneration || !page.context.isShown()) {
            return;
        }

        page.savedItems = items;
        ssRenderSaved(page);
    }, function (error) {
        if (generation !== page.savedGeneration || !page.context.isShown()) {
            return;
        }

        page.savedItems = null;
        page.savedCount.textContent = '';
        page.savedList.replaceChildren();
        page.savedNotices.replaceChildren(kit.notice({
            kind: 'error',
            text: error && error.isFeatureDisabled ? error.message : ssText('savedLoadFailed', { message: ssErrorMessage(error) }),
            action: error && error.isFeatureDisabled ? null : { label: SS_TEXT.retry, onClick: function () {
                ssLoadSaved(page);
            } }
        }));
    });
}

// Folded for finding: no case, no accents.
function ssFold(text) {
    return String(text || '').normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
}

function ssRenderSaved(page) {
    if (!page.savedItems) {
        return;
    }

    const find = ssFold(page.state.find).trim();
    const items = page.savedItems.filter(function (item) {
        return !find || ssFold(item.Name).indexOf(find) >= 0 || ssFold(item.Description).indexOf(find) >= 0;
    });
    page.savedNotices.replaceChildren();
    page.savedCount.textContent = items.length === 1 ? SS_TEXT.savedCountOne : ssText('savedCount', { count: items.length.toLocaleString() });
    page.savedList.replaceChildren();
    if (items.length === 0) {
        if (find && page.savedItems.length > 0) {
            page.savedNotices.append(kit.notice({ kind: 'empty', text: SS_TEXT.savedNoMatch }));
        } else if (page.state.scope === 'Shared') {
            page.savedNotices.append(kit.notice({ kind: 'empty', text: SS_TEXT.savedEmptyShared }));
        } else {
            const form = page.titleInput ? ssReadForm(page) : null;
            const save = kit.button(SS_TEXT.saveCurrent, { className: 'jfss-save-current', onClick: function () {
                ssSaveAsNew(page);
            } });
            save.disabled = !form || page.locked || !ssHasRules(form);
            page.savedNotices.append(kit.notice({ kind: 'empty', text: SS_TEXT.savedEmpty }), kit.h('div', { class: 'jfss-actions' }, [save]));
        }

        return;
    }

    items.forEach(function (item) {
        page.savedList.append(ssSavedRow(page, item));
    });
    kit.upgrade(page.savedList);
}

function ssSavedRow(page, item) {
    const h = kit.h;
    const id = ssId(item.Id);
    const label = ssDisplayText(item.Name);
    const open = kit.pages.link(SS_PAGE.key, { tab: 'search', query: id }, ssBdi(item.Name));
    open.classList.add('jfss-saved-open');
    const meta = [];
    if (!item.IsOwner) {
        meta.push(h('span', { class: 'jfss-saved-owner' }, ssSentence('sharedBy', 'owner', ssBdi(item.OwnerName))));
    } else if (item.Shared) {
        meta.push(h('span', { class: 'jfss-badge jfss-badge-shared', text: SS_TEXT.sharedBadge }));
    }

    const updated = item.UpdatedUtc ? kit.format.date(item.UpdatedUtc) : '';
    if (updated) {
        meta.push(h('span', { class: 'jfss-saved-updated', text: ssText('updated', { date: updated }) }));
    }

    if (item.AutoCollectionCount > 0) {
        meta.push(h('span', { class: 'jfss-saved-used', text: item.AutoCollectionCount === 1 ? SS_TEXT.usedByOne : ssText('usedBy', { count: item.AutoCollectionCount }) }));
    }

    if (item.Supported === false) {
        meta.push(h('span', { class: 'jfss-badge jfss-badge-newer', text: SS_TEXT.needsNewer }));
    } else if (item.Valid === false) {
        meta.push(h('span', { class: 'jfss-badge jfss-badge-errors', text: SS_TEXT.hasErrors }));
    }

    const row = h('div', { class: 'jfss-saved-row', role: 'listitem', 'data-jfss-query': id });
    const action = function (icon, key, cls, run) {
        const button = kit.iconButton(icon, ssText(key, { name: label }), function () {
            run(button);
        });
        button.classList.add('jfss-saved-action', cls);
        return button;
    };
    const refresh = function () {
        ssLoadSaved(page);
    };
    const actions = [];
    if (item.IsOwner && item.Supported !== false) {
        actions.push(action('edit', 'renameName', 'jfss-rename', function (button) {
            ssApi.getQuery(id).then(function (dto) {
                ssOpenSaveDialog(page, {
                    mode: 'rename',
                    dto: dto,
                    returnFocus: button,
                    onSaved: function (changed) {
                        if (page.saved && ssId(page.saved.Id) === id) {
                            ssSetSaved(page, changed, page.savedState);
                        }

                        refresh();
                    }
                });
            }, function (error) {
                kit.toast(ssErrorMessage(error));
            });
        }));
    }

    actions.push(action('content_copy', 'duplicateName', 'jfss-duplicate', function (button) {
        ssOpenDuplicateDialog(page, item, refresh, button);
    }));
    if (ssCanManageCollections(page) && item.Supported !== false && item.Valid !== false) {
        actions.push(action('video_library', 'acCreateFromQuery', 'jfss-create-collection', function (button) {
            ssOpenCollectionEditor(page, { queryId: id, returnFocus: button });
        }));
    }

    if (item.IsOwner && item.Supported !== false && (item.Shared || page.allowSharing)) {
        actions.push(action(item.Shared ? 'lock' : 'share', item.Shared ? 'unshareName' : 'shareName', 'jfss-share', function (button) {
            ssToggleShare(page, item, function (dto) {
                if (page.saved && ssId(page.saved.Id) === id) {
                    ssSetSaved(page, dto, page.savedState);
                }

                refresh();
            }, button);
        }));
    }

    if (!page.tv) {
        actions.push(action('link', 'copyLinkName', 'jfss-copy-link', function (button) {
            ssCopyLink(item, button);
        }));
    }

    if (item.IsOwner) {
        actions.push(action('delete', 'deleteName', 'jfss-delete', function () {
            ssDeleteQuery(page, item, refresh);
        }));
    }

    row.append(
        h('div', { class: 'jfss-saved-info' }, [
            h('div', { class: 'jfss-saved-title' }, open),
            meta.length ? h('div', { class: 'jfss-saved-meta' }, meta) : null,
            item.Description ? h('div', { class: 'jfss-saved-description' }, ssBdi(item.Description)) : null
        ]),
        h('div', { class: 'jfss-saved-actions' }, actions));
    return row;
}
