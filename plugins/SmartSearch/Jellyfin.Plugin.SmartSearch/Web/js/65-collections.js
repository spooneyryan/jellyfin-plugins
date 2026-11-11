// Auto-collections: the page's third tab (tab=collections), for users who manage collections (administrators, and users
// allowed to manage collections; GET /SmartSearch/Fields' AutoCollections says so, and the server checks every call).
//
// The tab: New auto-collection, Refresh all, a filter (All | Needs review | Errors) and the list: each row the collection
// (a link to its page once made), what it follows, its status, its members and last change, whose watch state it reads,
// Mixed audience, and Refresh now, Edit, Turn on or off, History and Delete. While a row is Queued or Running the list is
// read again every 4 s, otherwise every 30 s while the browser tab is visible, and again when the browser tab or window
// comes back, so changes made elsewhere (another tab, the API, a scan) show up; a background read that finds nothing new
// leaves the list (and the focus) alone. A row that finishes says how it ended in a toast ("Horror 80s: +3 −1"). A row whose removals
// are held shows them with Apply removals, Keep these items and Wait; a row whose collection was replaced, deleted or
// never made offers Recreate (or Create), and one that lost its query Choose another query.
//
// The editor (a kit dialog; the whole screen on a phone): the name, checked as it is typed (what is in the way, and free
// names to pick); the saved query (the user's own, and others' shared ones, which are copied on save); whose watch
// state, when the query reads watch state (an administrator picks any user); the look (order, lock, overview); manual
// changes (keep them, or always match the query; an existing one's pinned and excluded items, each removable); the
// safeguards; and the preview, with up to 20 posters a side. Save stays off until a preview of the settings as they are
// has succeeded, and sends that preview's hash. Only the creator or an administrator changes the name, the query, the
// evaluation user and whether it is on; other managers change the rest.
//
// Names, queries and users are users' text: always text nodes in <bdi>, or ssIsolate in a sentence.

const SS_AC_POLL_MS = 4000;
const SS_AC_IDLE_POLL_MS = 30000;
const SS_AC_WAKE_GAP_MS = 2000;
const SS_AC_CHECK_DELAY_MS = 400;
const SS_AC_REVIEW = Object.freeze(['Guarded', 'TetherMissing']);
const SS_AC_ERRORS = Object.freeze(['Error', 'QueryMissing', 'QueryInvalid', 'EvaluationUserMissing', 'CreatorMissing', 'NeedsNewerVersion', 'Orphaned']);
const SS_AC_BUSY = Object.freeze(['Queued', 'Running']);
const SS_AC_REASONS = Object.freeze(['PostScan', 'Events', 'Daily', 'Manual', 'Saved', 'Startup', 'ManualEdit', 'Moved', 'WatchState']);
const SS_AC_OUTCOMES = Object.freeze(['Created', 'Written', 'NoChange', 'Stopped', 'Failed']);

function ssCanManageCollections(page) {
    return !!(page && page.acAccess && page.acAccess.CanManage);
}

function ssAcIsAdmin(page) {
    return !!(page && page.acAccess && page.acAccess.IsAdmin);
}

function ssAcStatusOf(item) {
    return String(item && item.State && item.State.Status || 'NotCreated');
}

// The status chip: its text and its kind (ok, busy, review, error, off), from the state and Enabled.
function ssAcChip(item) {
    const status = ssAcStatusOf(item);
    if (SS_AC_BUSY.indexOf(status) >= 0) {
        return { kind: 'busy', text: item.State.Reason === 'WaitingForScan' ? SS_TEXT.acStatusWaitingForScan : SS_TEXT.acStatusUpdating };
    }

    if (!item.Enabled) {
        return { kind: 'off', text: SS_TEXT.acStatusDisabled };
    }

    if (status === 'Ok') {
        return { kind: 'ok', text: SS_TEXT.acStatusOk };
    }

    if (SS_AC_REVIEW.indexOf(status) >= 0) {
        return { kind: 'review', text: SS_TEXT.acStatusReview };
    }

    const key = 'acStatus' + status;
    return { kind: status === 'NotCreated' || status === 'Disabled' ? 'off' : 'error', text: Object.prototype.hasOwnProperty.call(SS_TEXT, key) ? SS_TEXT[key] : SS_TEXT.acStatusError };
}

function ssAcIsMine(item) {
    return ssId(item && item.CreatedByUserId) === ssId(kit.user.id());
}

// ------------------------------------------------------------------------------------------------ the tab

function ssBuildCollectionsTab(page) {
    const h = kit.h;
    page.acFilter = '';
    page.acFilterButtons = [['', SS_TEXT.acFilterAll], ['review', SS_TEXT.acFilterReview], ['errors', SS_TEXT.acFilterErrors]].map(function (filter) {
        return h('button', {
            type: 'button',
            class: 'jfss-segment jfss-ac-filter',
            'data-jfss-ac-filter': filter[0] || 'all',
            'aria-pressed': filter[0] === '' ? 'true' : 'false',
            text: filter[1],
            onclick: function () {
                page.acFilter = filter[0];
                page.acFilterButtons.forEach(function (b) {
                    b.setAttribute('aria-pressed', b.getAttribute('data-jfss-ac-filter') === (filter[0] || 'all') ? 'true' : 'false');
                });
                ssRenderCollections(page);
            }
        });
    });
    const filterId = ssNextId('ac-filter');
    page.acNewButton = kit.button(SS_TEXT.acNew, { variant: 'primary', className: 'jfss-ac-new', onClick: function () {
        ssOpenCollectionEditor(page, { returnFocus: page.acNewButton });
    } });
    page.acRefreshAllButton = kit.button(SS_TEXT.acRefreshAll, { className: 'jfss-ac-refresh-all', onClick: function () {
        ssApi.acRefreshAll().then(function () {
            kit.toast(SS_TEXT.acRefreshAllDone);
            ssLoadCollections(page);
        }, function (error) {
            kit.toast(ssErrorMessage(error));
        });
    } });
    page.acCount = h('p', { class: 'jfss-count jfss-ac-count', role: 'status' });
    page.acNotices = h('div', { class: 'jfss-notices jfss-ac-notices' });
    page.acList = h('div', { class: 'jfss-ac-list', role: 'list', 'aria-label': SS_TEXT.acList });
    page.acItems = null;
    page.acGeneration = 0;
    page.acPoll = null;
    page.acJson = null;
    page.acReadAt = 0;
    ssWatchCollectionsWake(page);
    page.acSeen = {};
    page.acWaiting = {};
    page.acWaited = {};
    const off = page.acAccess && page.acAccess.Enabled === false;
    return h('div', { class: 'jfss-tabpanel jfss-collections-panel', role: 'tabpanel', id: ssNextId('panel-collections') }, [
        off ? kit.notice({ kind: 'info', text: SS_TEXT.acOff }) : null,
        h('div', { class: 'jfss-ac-tools' }, [
            h('div', { class: 'jfss-actions' }, [page.acNewButton, page.acRefreshAllButton]),
            h('div', { class: kit.cls('field') + ' jfss-filter-field' }, [
                h('span', { class: 'jfss-label', id: filterId, text: SS_TEXT.acFilterLabel }),
                h('div', { class: 'jfss-segments', role: 'group', 'aria-labelledby': filterId }, page.acFilterButtons)
            ])
        ]),
        page.acCount,
        page.acNotices,
        page.acList
    ].filter(Boolean));
}

function ssStopCollectionsPoll(page) {
    if (page && page.acPoll) {
        clearTimeout(page.acPoll);
        page.acPoll = null;
    }
}

// Reads the list again when the browser tab becomes visible or the window gets the focus while the Auto-collections tab
// shows (the two together, or right after another read, count once). The page has no teardown, so a listener of a page
// that is no longer current removes itself.
function ssWatchCollectionsWake(page) {
    const wake = function () {
        if (!page.context.isCurrent()) {
            document.removeEventListener('visibilitychange', wake);
            window.removeEventListener('focus', wake);
            return;
        }

        if (document.visibilityState === 'visible' && page.tab === 'collections' && page.context.isShown()
            && Date.now() - page.acReadAt >= SS_AC_WAKE_GAP_MS) {
            ssLoadCollections(page, { quiet: true });
        }
    };
    document.addEventListener('visibilitychange', wake);
    window.addEventListener('focus', wake);
}

// The next read: every 4 s while a row updates, else every 30 s; only while the tab shows and the browser tab is visible
// (a hidden one is read again when it comes back).
function ssScheduleCollections(page, busy) {
    ssStopCollectionsPoll(page);
    if (page.tab !== 'collections') {
        return;
    }

    page.acPoll = setTimeout(function () {
        page.acPoll = null;
        if (page.tab === 'collections' && page.context.isShown() && document.visibilityState === 'visible') {
            ssLoadCollections(page, { quiet: !busy });
        }
    }, busy ? SS_AC_POLL_MS : SS_AC_IDLE_POLL_MS);
}

// The list read again (each time the tab shows, after a change, and on the schedule above); a newer read wins. quiet: a
// background read, which leaves the list as it is when nothing changed and keeps it when the read fails.
function ssLoadCollections(page, options) {
    if (!page.collectionsPanel) {
        return Promise.resolve();
    }

    const quiet = !!(options && options.quiet);
    ssStopCollectionsPoll(page);
    const generation = ++page.acGeneration;
    page.acReadAt = Date.now();
    if (!page.acItems) {
        page.acCount.textContent = SS_TEXT.loading;
    }

    return ssApi.acList().then(function (items) {
        if (generation !== page.acGeneration || !page.context.isShown()) {
            return;
        }

        const list = Array.isArray(items) ? items : [];
        const json = JSON.stringify(list);
        const unchanged = quiet && page.acItems !== null && json === page.acJson;
        page.acJson = json;
        if (!unchanged) {
            page.acItems = list;
            ssAnnounceFinished(page);
            ssRenderCollections(page);
        }

        if (page.state.edit) {
            const wanted = page.state.edit;
            page.state.edit = null;
            kit.pages.setParams(SS_PAGE.key, ssPageParams(page), { replace: true });
            if (page.acItems.some(function (item) {
                return ssId(item.Id) === wanted;
            })) {
                ssOpenCollectionEditor(page, { id: wanted });
            }
        }

        ssScheduleCollections(page, page.acItems.some(function (item) {
            return SS_AC_BUSY.indexOf(ssAcStatusOf(item)) >= 0;
        }));
    }, function (error) {
        if (generation !== page.acGeneration || !page.context.isShown()) {
            return;
        }

        if (quiet && page.acItems) {
            ssScheduleCollections(page, false);
            return;
        }

        page.acItems = null;
        page.acJson = null;
        page.acCount.textContent = '';
        page.acList.replaceChildren();
        page.acNotices.replaceChildren(kit.notice({
            kind: 'error',
            text: error && error.isFeatureDisabled ? error.message : ssText('acLoadFailed', { message: ssErrorMessage(error) }),
            action: error && error.isFeatureDisabled ? null : { label: SS_TEXT.retry, onClick: function () {
                ssLoadCollections(page);
            } }
        }));
    });
}

// A row that was updating (as this page saw it, or asked for it) and isn't any more: a toast says how it ended.
function ssAnnounceFinished(page) {
    page.acItems.forEach(function (item) {
        const id = ssId(item.Id);
        const status = ssAcStatusOf(item);
        const busy = SS_AC_BUSY.indexOf(status) >= 0;
        if (!busy && (SS_AC_BUSY.indexOf(page.acSeen[id]) >= 0 || page.acWaiting[id])) {
            delete page.acWaiting[id];
            const state = item.State || {};
            kit.toast(status === 'Ok'
                ? ssText('acToastChange', { name: ssIsolate(item.Name), added: state.LastAdded || 0, removed: state.LastRemoved || 0 })
                : ssText('acToastStatus', { name: ssIsolate(item.Name), status: ssAcChip(item).text }));
        }

        page.acSeen[id] = status;
    });
}

// Asks for a row's update and follows it: the list polls until it ends, and its toast comes then.
function ssAcFollow(page, id) {
    page.acWaiting[ssId(id)] = true;
    ssLoadCollections(page);
}

function ssRenderCollections(page) {
    if (!page.acItems) {
        return;
    }

    const filter = page.acFilter;
    const items = page.acItems.filter(function (item) {
        const status = ssAcStatusOf(item);
        return !filter || (filter === 'review' ? SS_AC_REVIEW.indexOf(status) >= 0 : SS_AC_ERRORS.indexOf(status) >= 0);
    });
    page.acNotices.replaceChildren();
    page.acCount.textContent = items.length === 1 ? SS_TEXT.acCountOne : ssText('acCount', { count: items.length.toLocaleString() });
    page.acList.replaceChildren();
    if (items.length === 0) {
        page.acNotices.append(kit.notice({ kind: 'empty', text: filter && page.acItems.length > 0 ? SS_TEXT.acEmptyFilter : SS_TEXT.acEmpty }));
        return;
    }

    items.forEach(function (item) {
        page.acList.append(ssCollectionRow(page, item));
    });
    kit.upgrade(page.acList);
}

function ssCollectionRow(page, item) {
    const h = kit.h;
    const id = ssId(item.Id);
    const label = ssDisplayText(item.Name);
    const state = item.State || {};
    const status = ssAcStatusOf(item);
    const chip = ssAcChip(item);
    const title = item.CollectionId && ['Orphaned', 'NotCreated', 'TetherMissing'].indexOf(status) < 0
        ? h('a', { class: 'jfss-ac-open ' + kit.cls('link'), href: kit.itemHref(item.CollectionId) }, ssBdi(item.Name))
        : h('span', { class: 'jfss-ac-open' }, ssBdi(item.Name));
    const meta = [h('span', { class: 'jfss-ac-status jfss-ac-status-' + chip.kind, 'data-jfss-status': status, text: chip.text })];
    const source = item.Source || {};
    if (source.Mode === 'Snapshot') {
        meta.push(h('span', { class: 'jfss-ac-source', text: SS_TEXT.acSourceSnapshot }));
    } else if (source.QueryName) {
        meta.push(h('span', { class: 'jfss-ac-source' }, ssSentence('acSource', 'query', ssBdi(source.QueryName))));
    } else if (status !== 'QueryMissing') {
        meta.push(h('span', { class: 'jfss-ac-source' }, ssSentence('acSourceHidden', 'owner', ssBdi(item.CreatedByName || SS_TEXT.acDeletedUser))));
    }

    if (state.LastSuccessUtc) {
        meta.push(h('span', { class: 'jfss-ac-members', text: (state.LastMemberCount === 1 ? SS_TEXT.acMembersOne : ssText('acMembers', { count: (state.LastMemberCount || 0).toLocaleString() }))
            + ' · ' + ssText('acChange', { added: state.LastAdded || 0, removed: state.LastRemoved || 0 }) }));
        meta.push(h('span', { class: 'jfss-ac-updated', text: ssText('acUpdated', { date: kit.format.date(state.LastSuccessUtc) }) }));
    } else {
        meta.push(h('span', { class: 'jfss-ac-updated', text: SS_TEXT.acNeverUpdated }));
    }

    if (item.EvaluationUserId) {
        meta.push(h('span', { class: 'jfss-ac-evaluation' }, ssSentence('acEvaluationUser', 'user', ssBdi(item.EvaluationUserName || SS_TEXT.acDeletedUser))));
    }

    if (state.MixedAudience) {
        meta.push(h('span', { class: 'jfss-badge jfss-badge-errors jfss-ac-mixed', title: SS_TEXT.acMixedAudienceNote, text: SS_TEXT.acMixedAudience }));
    }

    const action = function (icon, key, cls, run) {
        const button = kit.iconButton(icon, ssText(key, { name: label }), function () {
            run(button);
        });
        button.classList.add('jfss-ac-action', cls);
        return button;
    };
    const actions = [
        action('refresh', 'acRefreshName', 'jfss-ac-refresh', function () {
            ssApi.acRefresh(id).then(function () {
                kit.toast(ssText('acQueued', { name: ssIsolate(item.Name) }));
                ssAcFollow(page, id);
            }, function (error) {
                kit.toast(ssErrorMessage(error));
            });
        }),
        action('edit', 'acEditName', 'jfss-ac-edit', function (button) {
            ssOpenCollectionEditor(page, { id: id, returnFocus: button });
        })
    ];
    if (ssAcIsAdmin(page) || ssAcIsMine(item)) {
        actions.push(action(item.Enabled ? 'pause_circle' : 'play_circle', item.Enabled ? 'acDisableName' : 'acEnableName', 'jfss-ac-toggle', function () {
            ssApi.acUpdate(id, { Definition: { Enabled: !item.Enabled }, IfUpdatedAt: item.UpdatedUtc }).then(function (dto) {
                kit.toast(ssText(dto.Enabled ? 'acTurnedOn' : 'acTurnedOff', { name: ssIsolate(dto.Name) }));
                ssLoadCollections(page);
            }, function (error) {
                kit.toast(ssErrorMessage(error));
                ssLoadCollections(page);
            });
        }));
    }

    actions.push(action('history', 'acHistoryName', 'jfss-ac-history', function (button) {
        ssCollectionHistory(page, item, button);
    }));
    actions.push(action('delete', 'acDeleteName', 'jfss-ac-delete', function (button) {
        ssDeleteCollection(page, item, button);
    }));

    const row = h('div', { class: 'jfss-ac-row', role: 'listitem', 'data-jfss-ac': id });
    row.append(
        h('div', { class: 'jfss-saved-info jfss-ac-info' }, [
            h('div', { class: 'jfss-saved-title jfss-ac-title' }, title),
            h('div', { class: 'jfss-saved-meta jfss-ac-meta' }, meta),
            ssCollectionPanel(page, item)
        ].filter(Boolean)),
        h('div', { class: 'jfss-saved-actions jfss-ac-actions' }, actions));
    return row;
}

// What a row needs a manager for: held removals, a replaced, deleted or unmade collection, a lost query, an error.
function ssCollectionPanel(page, item) {
    const h = kit.h;
    const id = ssId(item.Id);
    const state = item.State || {};
    const status = ssAcStatusOf(item);
    const parts = [];
    if (status === 'Guarded' && state.PendingGuard && page.acWaited[id] === String(state.PendingGuard.DetectedUtc)) {
        parts.push(h('div', { class: 'jfss-actions' }, [kit.button(SS_TEXT.acGuardReview, { className: 'jfss-ac-guard-review', onClick: function () {
            delete page.acWaited[id];
            ssRenderCollections(page);
            const again = page.acList.querySelector('[data-jfss-ac="' + id + '"] .jfss-ac-guard-keep');
            if (again) {
                again.focus();
            }
        } })]));
    } else if (status === 'Guarded' && state.PendingGuard) {
        parts.push.apply(parts, ssGuardPanel(page, item, state.PendingGuard));
    } else if (status === 'TetherMissing' || status === 'Orphaned' || status === 'NotCreated') {
        const text = status === 'TetherMissing' ? SS_TEXT.acTetherMessage : (status === 'Orphaned' ? SS_TEXT.acOrphanedMessage : SS_TEXT.acNotCreatedMessage);
        const recreate = kit.button(status === 'NotCreated' ? SS_TEXT.acCreate : SS_TEXT.acRecreate, { variant: 'primary', className: 'jfss-ac-recreate', onClick: function () {
            recreate.disabled = true;
            ssApi.acRecreate(id).then(function (dto) {
                const busy = SS_AC_BUSY.indexOf(ssAcStatusOf(dto)) >= 0;
                kit.toast(ssText(busy ? 'acRecreateQueued' : 'acRecreated', { name: ssIsolate(dto.Name) }));
                if (busy) {
                    ssAcFollow(page, id);
                } else {
                    ssLoadCollections(page);
                }
            }, function (error) {
                recreate.disabled = false;
                if (error && error.code === 'NameTaken') {
                    ssOpenCollectionEditor(page, { id: id });
                }

                kit.toast(ssErrorMessage(error));
            });
        } });
        const stop = status === 'TetherMissing' ? kit.button(SS_TEXT.acStopManaging, { className: 'jfss-ac-stop', onClick: function (event) {
            ssDeleteCollection(page, item, event && event.currentTarget, true);
        } }) : null;
        parts.push(h('p', { class: 'jfss-ac-panel-text', text: text }), h('div', { class: 'jfss-actions' }, [recreate, stop].filter(Boolean)));
    } else if (status === 'CreatorMissing' && ssAcIsAdmin(page)) {
        parts.push.apply(parts, ssReassignPanel(page, item));
    } else if (status === 'QueryMissing' || status === 'QueryInvalid' || status === 'EvaluationUserMissing') {
        parts.push(h('div', { class: 'jfss-actions' }, [kit.button(SS_TEXT.acChooseQuery, { className: 'jfss-ac-choose', onClick: function () {
            ssOpenCollectionEditor(page, { id: id });
        } })]));
    }

    if (state.LastError && (status === 'Error' || SS_AC_ERRORS.indexOf(status) >= 0)) {
        parts.unshift(h('p', { class: 'jfss-ac-error' }, ssBdi(ssText('acLastError', { message: state.LastError }))));
    }

    return parts.length ? h('div', { class: 'jfss-ac-panel jfss-ac-panel-' + status.toLowerCase() }, parts) : null;
}

// A deleted creator's auto-collection (administrators): the server's administrators and collection managers to pick
// from (only they can be given one), and Give to this user, on once someone is picked.
function ssReassignPanel(page, item) {
    const h = kit.h;
    const id = ssId(item.Id);
    const select = kit.select({ class: 'jfss-ac-reassign-select', 'aria-label': SS_TEXT.acReassignLabel }, [{ value: '', text: SS_TEXT.acReassignPick }]);
    const give = kit.button(SS_TEXT.acReassign, { variant: 'primary', className: 'jfss-ac-reassign', onClick: function () {
        if (!select.value) {
            return;
        }

        const userId = select.value;
        const chosen = Array.prototype.find.call(select.querySelectorAll('option'), function (option) {
            return option.value === userId;
        });
        give.disabled = true;
        ssApi.acReassign(id, userId).then(function () {
            kit.toast(ssText('acReassigned', { name: ssIsolate(item.Name), user: ssIsolate(chosen ? chosen.textContent : '') }));
            ssLoadCollections(page);
        }, function (error) {
            give.disabled = false;
            kit.toast(ssErrorMessage(error));
        });
    } });
    give.disabled = true;
    select.addEventListener('change', function () {
        give.disabled = !select.value;
    });
    const c = kit.client();
    if (c && typeof c.getUsers === 'function') {
        Promise.resolve(c.getUsers()).then(function (users) {
            (users || []).filter(function (user) {
                const policy = user && user.Policy;
                return !policy || policy.IsAdministrator || policy.EnableCollectionManagement;
            }).forEach(function (user) {
                select.append(h('option', { value: ssId(user.Id), text: ssDisplayText(user.Name) }));
            });
            give.disabled = !select.value;
        }, function () {
            // nothing to pick from: the button stays off
        });
    }

    return [
        h('p', { class: 'jfss-ac-panel-text', text: SS_TEXT.acCreatorMissingMessage }),
        h('div', { class: 'jfss-actions' }, [select, give])
    ];
}

function ssGuardPanel(page, item, guard) {
    const h = kit.h;
    const id = ssId(item.Id);
    const parts = [h('p', { class: 'jfss-ac-panel-text jfss-ac-guard-text', text: ssText('acGuardMessage', { count: (guard.WouldRemoveCount || 0).toLocaleString() }) })];
    (guard.Reasons || []).forEach(function (reason) {
        parts.push(h('p', { class: 'jfss-ac-guard-reason' }, ssBdi(reason)));
    });
    if (guard.HiddenCount > 0) {
        parts.push(h('p', { class: 'jfss-ac-guard-hidden', text: ssText('acGuardHidden', { count: guard.HiddenCount }) }));
    }

    if (Array.isArray(guard.Locations) && guard.Locations.length) {
        parts.push(h('p', { class: 'jfss-ac-guard-locations' }, ssBdi(ssText('acGuardLocations', { list: guard.Locations.join(', ') }))));
    }

    if (guard.Approved) {
        parts.push(h('p', { class: 'jfss-ac-guard-approved', text: SS_TEXT.acGuardApproved }));
        return parts;
    }

    const apply = kit.button(SS_TEXT.acGuardApply, { variant: 'danger', className: 'jfss-ac-guard-apply', onClick: function () {
        kit.confirm({
            title: SS_TEXT.acGuardApply,
            message: ssText('acGuardApplyConfirm', { count: guard.WouldRemoveCount, name: ssIsolate(item.Name) }),
            confirmLabel: SS_TEXT.acGuardApply,
            cancelLabel: SS_TEXT.cancel
        }).then(function (yes) {
            return yes ? ssApi.acGuardApply(id, guard.DetectedUtc).then(function () {
                kit.toast(ssText('acGuardApplied', { name: ssIsolate(item.Name) }));
                ssAcFollow(page, id);
            }) : null;
        }).catch(function (error) {
            kit.toast(ssErrorMessage(error));
            ssLoadCollections(page);
        });
    } });
    const keep = kit.button(SS_TEXT.acGuardKeep, { variant: 'primary', className: 'jfss-ac-guard-keep', onClick: function () {
        keep.disabled = true;
        ssApi.acGuardKeep(id, guard.DetectedUtc).then(function () {
            kit.toast(ssText('acGuardKept', { name: ssIsolate(item.Name) }));
            ssAcFollow(page, id);
        }, function (error) {
            keep.disabled = false;
            kit.toast(ssErrorMessage(error));
            ssLoadCollections(page);
        });
    } });
    // Wait: nothing is sent; the panel folds away until the guard changes (or Review removals opens it again).
    const wait = kit.button(SS_TEXT.acGuardWait, { className: 'jfss-ac-guard-wait', title: SS_TEXT.acGuardWaitNote, onClick: function () {
        page.acWaited[id] = String(guard.DetectedUtc);
        kit.toast(SS_TEXT.acGuardWaitNote);
        ssRenderCollections(page);
        const review = page.acList.querySelector('[data-jfss-ac="' + id + '"] .jfss-ac-guard-review');
        if (review) {
            review.focus();
        }
    } });
    parts.push(h('div', { class: 'jfss-actions' }, [keep, apply, wait]));
    return parts;
}

// ------------------------------------------------------------------------------------------------ delete and history

// Delete, asking whether the collection stays (the default) or goes too; stopOnly: "Stop managing" a replaced collection
// (the collection found isn't ours, so it always stays).
function ssDeleteCollection(page, item, returnFocus, stopOnly) {
    const h = kit.h;
    const id = ssId(item.Id);
    const dialog = kit.dialog({ title: SS_TEXT.acDeleteTitle, modal: true, scopedKeys: true, returnFocus: returnFocus });
    const name = 'jfss-ac-delete-' + id;
    const keep = h('input', { type: 'radio', name: name, value: 'keep', class: 'jfss-ac-delete-keep' });
    const drop = h('input', { type: 'radio', name: name, value: 'delete', class: 'jfss-ac-delete-too' });
    keep.checked = true;
    const confirm = kit.button(SS_TEXT.delete, { variant: 'danger', className: 'jfss-ac-delete-confirm', onClick: function () {
        confirm.disabled = true;
        ssApi.acDelete(id, !stopOnly && drop.checked).then(function () {
            dialog.close();
            kit.toast(ssText('acDeleted', { name: ssIsolate(item.Name) }));
            ssLoadCollections(page);
        }, function (error) {
            confirm.disabled = false;
            kit.toast(ssErrorMessage(error));
        });
    } });
    dialog.body.append(
        h('p', { text: ssText('acDeleteMessage', { name: ssIsolate(item.Name) }) }),
        stopOnly ? null : h('div', { class: 'jfss-ac-delete-choice', role: 'radiogroup', 'aria-label': SS_TEXT.acDeleteTitle }, [
            h('label', { class: kit.cls('checkbox') }, [keep, h('span', { text: SS_TEXT.acDeleteKeep })]),
            item.CollectionId ? h('label', { class: kit.cls('checkbox') }, [drop, h('span', { text: SS_TEXT.acDeleteToo })]) : null
        ].filter(Boolean)),
        h('div', { class: kit.cls('actions') }, [kit.button(SS_TEXT.cancel, { onClick: dialog.close }), confirm]));
    confirm.focus();
}

function ssCollectionHistory(page, item, returnFocus) {
    const h = kit.h;
    const id = ssId(item.Id);
    const dialog = kit.dialog({ title: SS_TEXT.acHistoryTitle, modal: true, scopedKeys: true, returnFocus: returnFocus });
    dialog.panel.classList.add('jfss-ac-history-dialog');
    const status = h('p', { class: 'jfss-count', role: 'status', text: SS_TEXT.loading });
    dialog.body.append(h('h4', { class: 'jfss-ac-history-name' }, ssBdi(item.Name)), status);
    ssApi.acHistory(id).then(function (runs) {
        runs = Array.isArray(runs) ? runs : [];
        status.textContent = runs.length ? '' : SS_TEXT.acHistoryEmpty;
        const list = h('ol', { class: 'jfss-ac-history' }, runs.map(function (run) {
            const reasonKey = 'acReason' + run.Reason;
            const lines = [h('div', { class: 'jfss-ac-history-line', text: ssText('acHistoryLine', {
                date: kit.format.date(run.Utc),
                reason: SS_AC_REASONS.indexOf(run.Reason) >= 0 ? SS_TEXT[reasonKey] : ssDisplayText(run.Reason),
                added: run.Added || 0,
                removed: run.Removed || 0,
                outcome: SS_AC_OUTCOMES.indexOf(run.Outcome) >= 0 ? SS_TEXT['acOutcome' + run.Outcome] : ssDisplayText(run.Outcome),
                ms: (run.DurationMs || 0).toLocaleString()
            }) })];
            if (run.Dead > 0) {
                lines.push(h('div', { class: 'jfss-ac-history-more', text: ssText('acHistoryDead', { count: run.Dead }) }));
            }

            if (run.Pinned > 0 || run.Excluded > 0 || run.Reverted > 0) {
                lines.push(h('div', { class: 'jfss-ac-history-more', text: ssText('acHistoryEdits', { pinned: run.Pinned || 0, excluded: run.Excluded || 0, reverted: run.Reverted || 0 }) }));
            }

            return h('li', {}, lines);
        }));
        dialog.body.append(list);
        const last = runs.find(function (run) {
            return (run.RemovedIds && run.RemovedIds.length) || run.HiddenCount > 0;
        });
        if (last) {
            const ids = (last.RemovedIds || []).map(ssId);
            const keep = ids.length ? kit.button(SS_TEXT.acHistoryKeep, { className: 'jfss-ac-history-keep', onClick: function () {
                keep.disabled = true;
                ssApi.acGet(id).then(function (dto) {
                    const pinned = ((dto.ManualEdits && dto.ManualEdits.Pinned) || []).map(function (p) {
                        return ssId(p.ItemId);
                    });
                    const merged = ssUnique(pinned.concat(ids));
                    return ssApi.acUpdate(id, { Definition: { ManualEdits: { PinnedItemIds: merged } }, IfUpdatedAt: dto.UpdatedUtc });
                }).then(function () {
                    dialog.close();
                    kit.toast(ssText('acHistoryKept', { name: ssIsolate(item.Name) }));
                    ssAcFollow(page, id);
                }, function (error) {
                    keep.disabled = false;
                    kit.toast(ssErrorMessage(error));
                });
            } }) : null;
            dialog.body.append(h('div', { class: 'jfss-ac-history-removed' }, [
                h('p', { text: ssText('acHistoryRemoved', { count: ids.length + (last.HiddenCount || 0) }) }),
                last.HiddenCount > 0 ? h('p', { class: 'jfss-ac-guard-hidden', text: ssText('acHistoryRemovedHidden', { count: last.HiddenCount }) }) : null,
                keep ? h('div', { class: 'jfss-actions' }, [keep]) : null
            ].filter(Boolean)));
        }

        dialog.body.append(h('div', { class: kit.cls('actions') }, [kit.button(SS_TEXT.cancel, { className: 'jfss-ac-history-close', onClick: dialog.close })]));
    }, function (error) {
        status.textContent = ssErrorMessage(error);
    });
}

// ------------------------------------------------------------------------------------------------ create from a search

// "Create auto-collection…" on the Search tab: the open saved query as it is saved (someone else's is copied on save);
// a search not saved yet, or changed since, is saved first through Save as new.
function ssCreateCollectionFromSearch(page) {
    const saved = page.saved;
    const dirty = !!saved && !page.locked && !!page.savedState && ssSearchKey(ssReadForm(page)) !== ssSearchKey(page.savedState);
    if (saved && !dirty) {
        ssOpenCollectionEditor(page, { queryId: ssId(saved.Id), returnFocus: page.createCollectionButton });
        return;
    }

    ssSaveAsNew(page, function (dto) {
        ssOpenCollectionEditor(page, { queryId: ssId(dto.Id), returnFocus: page.createCollectionButton });
    });
}

// ------------------------------------------------------------------------------------------------ the editor

// opts: { id (an existing one) | queryId (a new one from that saved query), returnFocus }.
function ssOpenCollectionEditor(page, opts) {
    opts = opts || {};
    const reads = [ssApi.listQueries('All'), opts.id ? ssApi.acGet(opts.id) : null];
    Promise.all(reads).then(function (answers) {
        if (!page.context.isShown()) {
            return;
        }

        ssCollectionEditor(page, answers[0] || [], answers[1], opts);
    }, function (error) {
        kit.toast(ssErrorMessage(error));
    });
}

function ssCollectionEditor(page, queries, dto, opts) {
    const h = kit.h;
    const me = ssId(kit.user.id());
    const isAdmin = ssAcIsAdmin(page);
    const editing = !!dto;
    const creatorFields = !editing || isAdmin || ssId(dto.CreatedByUserId) === me;
    const usable = queries.filter(function (q) {
        return q.Supported !== false && q.Valid !== false;
    });
    const mine = usable.filter(function (q) {
        return q.IsOwner;
    });
    const others = usable.filter(function (q) {
        return !q.IsOwner && q.Shared;
    });
    if (!editing && usable.length === 0) {
        kit.confirm({ title: SS_TEXT.acNewTitle, message: SS_TEXT.acNoQueries, confirmLabel: SS_TEXT.acGoSearch, cancelLabel: SS_TEXT.cancel }).then(function (yes) {
            if (yes) {
                ssShowTab(page, 'search', true);
            }
        });
        return;
    }

    const dialog = kit.dialog({ title: editing ? SS_TEXT.acEditTitle : SS_TEXT.acNewTitle, modal: true, scopedKeys: true, returnFocus: opts.returnFocus });
    dialog.panel.classList.add('jfss-ac-editor');
    let previewed = null; // { key, hash }
    let previewGeneration = 0;
    let previewTimer = null;
    let checkTimer = null;
    let usesPerUser = editing ? !!dto.EvaluationUserId : false;

    // The name, checked as it is typed.
    const presetQuery = opts.queryId ? usable.find(function (q) {
        return ssId(q.Id) === opts.queryId;
    }) : null;
    const name = kit.input({ class: 'jfss-ac-name', maxlength: '100', autocomplete: 'off', spellcheck: 'false', value: editing ? dto.Name : (presetQuery ? presetQuery.Name : '') });
    name.disabled = !creatorFields;
    const nameState = h('div', { class: 'jfss-ac-name-state', role: 'status' });
    const nameField = kit.field(SS_TEXT.acNameLabel, name);
    nameField.append(nameState, ssDialogFieldError('name'), h('p', { class: 'jfss-share-note', text: SS_TEXT.acNamePublic }));
    const checkName = function () {
        clearTimeout(checkTimer);
        const wanted = String(name.value || '').trim();
        nameState.replaceChildren();
        if (!wanted || (editing && wanted === dto.Name)) {
            return;
        }

        nameState.textContent = SS_TEXT.acNameChecking;
        checkTimer = setTimeout(function () {
            ssApi.acCheckName(wanted, editing ? ssId(dto.Id) : null).then(function (check) {
                if (String(name.value || '').trim() !== wanted) {
                    return;
                }

                ssShowNameCheck(nameState, check, function (suggestion) {
                    name.value = suggestion;
                    checkName();
                    schedulePreview();
                    name.focus();
                });
            }, function (error) {
                nameState.textContent = ssErrorMessage(error);
            });
        }, SS_AC_CHECK_DELAY_MS);
    };

    // The saved query.
    const source = kit.select({ class: 'jfss-ac-source-select' }, []);
    if (editing && (dto.Source || {}).Mode === 'Snapshot') {
        source.append(h('option', { value: 'snapshot', text: SS_TEXT.acSourceOwnCopy }));
    } else if (editing) {
        const current = dto.Source && dto.Source.SavedQueryId ? ssId(dto.Source.SavedQueryId) : '';
        if (!usable.some(function (q) {
            return ssId(q.Id) === current;
        })) {
            source.append(h('option', { value: 'current', text: SS_TEXT.acSourceCurrent }));
        }
    } else if (!presetQuery) {
        source.append(h('option', { value: '', text: SS_TEXT.acSourceNone }));
    }

    const group = function (labelText, list) {
        return list.length ? h('optgroup', { label: labelText }, list.map(function (q) {
            return h('option', { value: 'q:' + ssId(q.Id), text: ssDisplayText(q.Name) + (q.IsOwner ? '' : ' (' + ssDisplayText(q.OwnerName) + ')') });
        })) : null;
    };
    kit.append(source, group(SS_TEXT.acSourceMine, mine), group(SS_TEXT.acSourceShared, others));
    if (editing && dto.Source && dto.Source.Mode !== 'Snapshot' && dto.Source.SavedQueryId) {
        source.value = 'q:' + ssId(dto.Source.SavedQueryId);
        if (!source.value) {
            source.value = 'current';
        }
    } else if (presetQuery) {
        source.value = 'q:' + ssId(presetQuery.Id);
    }

    const initialSource = source.value;
    source.disabled = !creatorFields;
    const sourceNote = h('p', { class: 'jfss-share-note jfss-ac-copy-note' });
    const openLink = h('a', { class: 'jfss-ac-open-query ' + kit.cls('link'), text: SS_TEXT.acOpenInSearch });
    const sourceField = kit.field(SS_TEXT.acSourceLabel, source);
    sourceField.append(sourceNote, openLink);
    const chosenQuery = function () {
        const value = source.value || '';
        return value.indexOf('q:') === 0 ? usable.find(function (q) {
            return ssId(q.Id) === value.substring(2);
        }) || null : null;
    };

    // Whose watch state: only when the query reads it.
    const evaluation = kit.select({ class: 'jfss-ac-evaluation-select' }, [{ value: me, text: SS_TEXT.acEvaluationMe }]);
    if (editing && dto.EvaluationUserId && ssId(dto.EvaluationUserId) !== me) {
        evaluation.append(h('option', { value: ssId(dto.EvaluationUserId), text: ssDisplayText(dto.EvaluationUserName || SS_TEXT.acDeletedUser) }));
        evaluation.value = ssId(dto.EvaluationUserId);
    }

    evaluation.disabled = !creatorFields || !isAdmin;
    const evaluationWarning = h('p', { class: 'jfss-share-note jfss-share-warning jfss-ac-evaluation-warning' });
    const evaluationField = kit.field(SS_TEXT.acEvaluationLabel, evaluation);
    evaluationField.append(evaluationWarning);
    const syncEvaluation = function () {
        kit.setHidden(evaluationField, !usesPerUser);
        const option = Array.prototype.find.call(evaluation.querySelectorAll('option'), function (o) {
            return o.value === evaluation.value;
        });
        evaluationWarning.textContent = ssText('acEvaluationWarning', { user: ssIsolate(option && option.value !== me ? option.textContent : SS_TEXT.acEvaluationMe.toLowerCase()) });
    };
    if (isAdmin && creatorFields) {
        const c = kit.client();
        if (c && typeof c.getUsers === 'function') {
            Promise.resolve(c.getUsers()).then(function (users) {
                const kept = evaluation.value;
                (users || []).forEach(function (user) {
                    const uid = ssId(user.Id);
                    if (uid !== me && !evaluation.querySelector('option[value="' + uid + '"]')) {
                        evaluation.append(h('option', { value: uid, text: ssDisplayText(user.Name) }));
                    }
                });
                evaluation.value = kept;
            }, function () {
                // only "Me" and the kept user then
            });
        }
    }

    // The query's facts: whether it reads watch state (POST /Validate of the saved query's definition).
    const readQuery = function () {
        const q = chosenQuery();
        sourceNote.textContent = q && !q.IsOwner ? SS_TEXT.acSourceCopyNote : '';
        kit.setHidden(openLink, !q);
        if (q) {
            openLink.setAttribute('href', kit.pages.href(SS_PAGE.key, { tab: 'search', query: ssId(q.Id) }));
        }

        const definition = q ? ssApi.getQuery(ssId(q.Id)).then(function (read) {
            return read && read.Query;
        }) : Promise.resolve(editing && dto.Source ? dto.Source.Query : null);
        return definition.then(function (query) {
            return query ? ssApi.validate(query) : null;
        }).then(function (result) {
            usesPerUser = result ? !!result.UsesPerUserFields : (editing && !!dto.EvaluationUserId);
            syncEvaluation();
        }, function () {
            syncEvaluation();
        });
    };

    // The look.
    const presentation = (dto && dto.Presentation) || {};
    const lock = h('input', { type: 'checkbox', class: 'jfss-ac-lock' });
    lock.checked = presentation.Lock !== false;
    const sort = kit.select({ class: 'jfss-ac-sort' }, [
        { value: 'QueryOrder', text: SS_TEXT.acSortQueryOrder },
        { value: 'PremiereDate', text: SS_TEXT.acSortPremiereDate },
        { value: 'SortName', text: SS_TEXT.acSortName }
    ]);
    sort.value = presentation.SortMode && presentation.SortMode !== 'Unknown' ? presentation.SortMode : 'PremiereDate';
    const sortNote = h('p', { class: 'jfss-share-note jfss-ac-sort-note' });
    const lockNote = h('p', { class: 'jfss-share-note jfss-ac-lock-note' });
    const syncLock = function () {
        sort.querySelectorAll('option').forEach(function (option) {
            option.disabled = !lock.checked && option.value !== 'PremiereDate';
        });
        if (!lock.checked) {
            sort.value = 'PremiereDate';
        }

        sortNote.textContent = lock.checked ? SS_TEXT.acSortSectionsNote : SS_TEXT.acSortUnlockedNote;
        lockNote.textContent = lock.checked ? SS_TEXT.acLockNote : SS_TEXT.acUnlockedNote;
    };
    const sortField = kit.field(SS_TEXT.acSortLabel, sort);
    sortField.append(sortNote);
    const overviewMode = kit.select({ class: 'jfss-ac-overview-mode' }, [
        { value: 'LeaveAsIs', text: SS_TEXT.acOverviewLeave },
        { value: 'Managed', text: SS_TEXT.acOverviewSet }
    ]);
    overviewMode.value = presentation.OverviewMode === 'Managed' ? 'Managed' : 'LeaveAsIs';
    const overview = kit.textarea({ class: 'jfss-ac-overview', rows: '3', maxlength: '2000', 'aria-label': SS_TEXT.acOverviewText });
    overview.value = presentation.Overview || '';
    const syncOverview = function () {
        kit.setHidden(overview, overviewMode.value !== 'Managed');
    };
    const overviewField = kit.field(SS_TEXT.acOverviewLabel, overviewMode);
    overviewField.append(overview);

    // Manual changes.
    const edits = (dto && dto.ManualEdits) || {};
    const editsMode = kit.select({ class: 'jfss-ac-edits-mode' }, [
        { value: 'Respect', text: SS_TEXT.acEditsRespect },
        { value: 'Overwrite', text: SS_TEXT.acEditsOverwrite }
    ]);
    editsMode.value = edits.Mode === 'Overwrite' ? 'Overwrite' : 'Respect';
    const pinned = (edits.Pinned || []).slice();
    const excluded = (edits.Excluded || []).slice();
    const itemList = function (titleText, list, cls) {
        const box = h('div', { class: 'jfss-ac-items ' + cls });
        const render = function () {
            box.replaceChildren(h('span', { class: 'jfss-label', text: titleText }));
            if (!list.length) {
                box.append(h('p', { class: 'jfss-share-note', text: SS_TEXT.acNoneYet }));
                return;
            }

            box.append(h('ul', { class: 'jfss-ac-item-list' }, list.map(function (entry, index) {
                const text = entry.Name ? ssDisplayText(entry.Name) : SS_TEXT.acItemHidden;
                const remove = kit.iconButton('close', ssText('acRemoveItem', { name: text }), function () {
                    list.splice(index, 1);
                    render();
                    schedulePreview();
                });
                remove.classList.add('jfss-ac-item-remove');
                return h('li', {}, [entry.Name ? ssBdi(entry.Name) : h('span', { class: 'jfss-ac-item-hidden', text: text }), remove]);
            })));
        };
        render();
        return box;
    };
    const editsField = kit.field(SS_TEXT.acEditsLabel, editsMode);
    if (editing) {
        editsField.append(itemList(SS_TEXT.acPinned, pinned, 'jfss-ac-pinned'), itemList(SS_TEXT.acExcluded, excluded, 'jfss-ac-excluded'));
    }

    // Safety.
    const safety = (dto && dto.Safety) || {};
    const allowEmpty = h('input', { type: 'checkbox', class: 'jfss-ac-allow-empty' });
    allowEmpty.checked = !!safety.AllowEmpty;
    const threshold = kit.input({ type: 'number', class: 'jfss-ac-threshold', min: '1', max: '100', step: '1', value: safety.MaxRemovalPercent ? String(safety.MaxRemovalPercent) : '' });
    const thresholdField = kit.field(SS_TEXT.acThresholdLabel, threshold);

    // The preview.
    const previewState = h('div', { class: 'jfss-ac-preview-state', role: 'status' });
    const previewBody = h('div', { class: 'jfss-ac-preview-body' });
    const previewButton = kit.button(SS_TEXT.acPreviewRun, { className: 'jfss-ac-preview-run', onClick: function () {
        runPreview();
    } });

    const error = h('div', { class: kit.cls('error') + ' jfss-dialog-error', role: 'alert' });
    const save = kit.button(SS_TEXT.save, { variant: 'primary', className: 'jfss-ac-save', onClick: function () {
        submit();
    } });
    save.disabled = true;

    // The definition the form says: creator fields only for the creator or an administrator; the source only when it
    // changed (a new one always has one).
    const definition = function () {
        const def = {
            Presentation: { SortMode: sort.value, Lock: !!lock.checked, OverviewMode: overviewMode.value, Overview: overviewMode.value === 'Managed' ? String(overview.value || '') : null },
            ManualEdits: { Mode: editsMode.value },
            Safety: { AllowEmpty: !!allowEmpty.checked, MaxRemovalPercent: threshold.value ? Number(threshold.value) : null }
        };
        if (!def.Presentation.Overview) {
            delete def.Presentation.Overview;
        }

        if (editing) {
            def.ManualEdits.PinnedItemIds = pinned.map(function (e) {
                return ssId(e.ItemId);
            });
            def.ManualEdits.ExcludedItemIds = excluded.map(function (e) {
                return ssId(e.ItemId);
            });
        }

        if (creatorFields) {
            def.Name = String(name.value || '').trim();
            def.EvaluationUserId = usesPerUser ? evaluation.value : null;
            if (!editing) {
                def.Enabled = true;
            }
        }

        const q = chosenQuery();
        if (q && (!editing || source.value !== initialSource)) {
            def.Source = { Mode: 'Reference', SavedQueryId: ssId(q.Id) };
        }

        return def;
    };
    const foreign = function () {
        const q = chosenQuery();
        return !!q && !q.IsOwner && (!editing || source.value !== initialSource);
    };
    const formKey = function () {
        return JSON.stringify(definition());
    };
    const syncSave = function () {
        save.disabled = !previewed || previewed.key !== formKey() || (!editing && !chosenQuery());
        if (save.disabled && !previewState.textContent) {
            previewState.textContent = SS_TEXT.acPreviewNeeded;
        }
    };
    const schedulePreview = function () {
        clearTimeout(previewTimer);
        syncSave();
        previewTimer = setTimeout(runPreview, 600);
    };
    const runPreview = function () {
        clearTimeout(previewTimer);
        if (!editing && !chosenQuery()) {
            previewState.textContent = SS_TEXT.acPreviewNeeded;
            return;
        }

        const key = formKey();
        const generation = ++previewGeneration;
        previewState.textContent = SS_TEXT.acPreviewRunning;
        const body = editing ? { DefinitionId: ssId(dto.Id), Definition: definition() } : { Definition: definition() };
        ssApi.acPreview(body).then(function (preview) {
            if (generation !== previewGeneration) {
                return;
            }

            previewed = { key: key, hash: preview.PreviewHash };
            ssShowPreview(previewState, previewBody, preview, editing);
            syncSave();
        }, function (failure) {
            if (generation !== previewGeneration) {
                return;
            }

            previewed = null;
            previewBody.replaceChildren();
            previewState.textContent = ssText('acPreviewFailed', { message: ssErrorMessage(failure) });
            syncSave();
        });
    };

    const submit = function () {
        if (save.disabled) {
            return;
        }

        save.disabled = true;
        error.textContent = '';
        const body = { Definition: definition(), AcceptedPreviewHash: previewed.hash };
        if (foreign()) {
            body.DuplicateSourceQuery = true;
        }

        if (editing) {
            body.IfUpdatedAt = dto.UpdatedUtc;
        }

        const request = editing ? ssApi.acUpdate(ssId(dto.Id), body) : ssApi.acCreate(body);
        request.then(function (result) {
            dialog.close();
            const busy = SS_AC_BUSY.indexOf(ssAcStatusOf(result)) >= 0;
            kit.toast(ssText(editing ? 'acSaved' : (busy ? 'acCreating' : 'acCreated'), { name: ssIsolate(result.Name) }));
            if (page.tab !== 'collections') {
                ssShowTab(page, 'collections', true);
            }

            ssAcFollow(page, ssId(result.Id));
        }, function (failure) {
            const code = failure && failure.code;
            if (code === 'NameTaken') {
                const problem = failure.problem || {};
                ssShowNameCheck(nameState, { Available: false, Suggestions: problem.suggestions || [] }, function (suggestion) {
                    name.value = suggestion;
                    checkName();
                    schedulePreview();
                });
                name.focus();
            } else if (code === 'PreviewStale') {
                error.textContent = SS_TEXT.acPreviewStale;
                previewed = null;
                runPreview();
            } else if (code === 'AutoCollectionChanged') {
                error.textContent = SS_TEXT.acChanged;
            } else {
                error.textContent = ssDialogErrorText(failure);
            }

            syncSave();
        });
    };

    [name, threshold, overview].forEach(function (input) {
        input.addEventListener('input', schedulePreview);
    });
    name.addEventListener('input', checkName);
    [sort, overviewMode, editsMode, allowEmpty, evaluation].forEach(function (input) {
        input.addEventListener('change', function () {
            syncOverview();
            syncEvaluation();
            schedulePreview();
        });
    });
    lock.addEventListener('change', function () {
        syncLock();
        schedulePreview();
    });
    source.addEventListener('change', function () {
        const q = chosenQuery();
        if (!editing && q && !String(name.value || '').trim()) {
            name.value = q.Name;
            checkName();
        }

        readQuery().then(schedulePreview);
    });

    const section = function (cls, children) {
        return h('div', { class: 'jfss-ac-section ' + cls }, children.filter(Boolean));
    };
    const form = h('form', { class: 'jfss-form jfss-ac-form', onsubmit: function (event) {
        event.preventDefault();
        submit();
    } }, [
        !creatorFields ? kit.notice({ kind: 'info', text: SS_TEXT.acCreatorOnly }) : null,
        section('jfss-ac-section-name', [nameField]),
        section('jfss-ac-section-source', [sourceField, evaluationField]),
        section('jfss-ac-section-look', [
            h('label', { class: kit.cls('checkbox') + ' jfss-ac-lock-label' }, [lock, h('span', { text: SS_TEXT.acLockLabel })]),
            lockNote,
            sortField,
            overviewField
        ]),
        section('jfss-ac-section-edits', [editsField]),
        section('jfss-ac-section-safety', [
            h('label', { class: kit.cls('checkbox') + ' jfss-ac-allow-empty-label' }, [allowEmpty, h('span', { text: SS_TEXT.acAllowEmpty })]),
            thresholdField
        ]),
        section('jfss-ac-section-preview', [h('div', { class: 'jfss-actions' }, [previewButton, previewState]), previewBody]),
        error,
        h('div', { class: kit.cls('actions') }, [kit.button(SS_TEXT.cancel, { className: 'jfss-dialog-cancel', onClick: dialog.close }), save])
    ].filter(Boolean));
    dialog.body.append(form);
    kit.upgrade(form);
    dialog.onClose(function () {
        clearTimeout(previewTimer);
        clearTimeout(checkTimer);
        previewGeneration++;
    });
    syncLock();
    syncOverview();
    syncEvaluation();
    checkName();
    readQuery().then(function () {
        if (editing || chosenQuery()) {
            runPreview();
        } else {
            syncSave();
        }
    });
    if (!page.tv) {
        (creatorFields ? (editing ? name : source) : lock).focus();
    }
}

// The name check's answer: free, or what is in the way and free names to pick (onPick(name)).
function ssShowNameCheck(node, check, onPick) {
    const h = kit.h;
    node.replaceChildren();
    if (check.Available) {
        node.append(h('span', { class: 'jfss-ac-name-free', text: SS_TEXT.acNameFree }));
    } else {
        node.append(h('span', { class: 'jfss-ac-name-taken', text: SS_TEXT.acNameTaken }));
        const suggestions = check.Suggestions || [];
        if (suggestions.length) {
            node.append(h('span', { class: 'jfss-ac-name-try', text: ' ' + SS_TEXT.acNameTry + ' ' }));
            suggestions.forEach(function (suggestion) {
                node.append(h('button', { type: 'button', class: 'jfss-suggestion jfss-ac-suggestion', onclick: function () {
                    onPick(suggestion);
                } }, ssBdi(suggestion)));
            });
        }
    }

    (check.Warnings || []).forEach(function (warning) {
        node.append(h('p', { class: 'jfss-share-note jfss-share-warning jfss-ac-name-warning' }, ssBdi(warning)));
    });
}

function ssShowPreview(state, body, preview, editing) {
    const h = kit.h;
    state.textContent = editing
        ? ssText('acPreviewChange', { added: preview.WouldAdd || 0, removed: preview.WouldRemove || 0, count: (preview.Total || 0).toLocaleString() })
        : ssText('acPreviewNew', { count: (preview.Total || 0).toLocaleString() });
    const parts = [];
    if (preview.HiddenFromCaller > 0) {
        parts.push(h('p', { class: 'jfss-share-note jfss-ac-preview-hidden', text: ssText('acPreviewHidden', { count: preview.HiddenFromCaller }) }));
    }

    if (preview.Guard && preview.Guard.WouldTrip) {
        parts.push(h('div', { class: 'jfss-ac-preview-guard' }, [h('p', { text: SS_TEXT.acPreviewGuard })].concat((preview.Guard.Reasons || []).map(function (reason) {
            return h('p', { class: 'jfss-ac-guard-reason' }, ssBdi(reason));
        }))));
    }

    (preview.Warnings || []).forEach(function (warning) {
        parts.push(h('p', { class: 'jfss-share-note jfss-share-warning jfss-ac-preview-warning' }, ssBdi(warning)));
    });
    const posters = function (titleText, items, cls) {
        return items && items.length ? h('div', { class: 'jfss-ac-samples ' + cls }, [
            h('span', { class: 'jfss-label', text: titleText }),
            h('div', { class: 'jfss-ac-sample-row' }, items.map(function (item) {
                return kit.card(item);
            }))
        ]) : null;
    };
    parts.push(posters(SS_TEXT.acPreviewAdds, preview.AddSamples, 'jfss-ac-adds'), posters(SS_TEXT.acPreviewRemoves, preview.RemoveSamples, 'jfss-ac-removes'));
    body.replaceChildren.apply(body, parts.filter(Boolean));
}
