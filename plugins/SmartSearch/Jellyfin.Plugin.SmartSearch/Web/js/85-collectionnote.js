// The note on an auto-collection's details page: "Updates automatically from a saved search · updated 5 minutes ago",
// right above the collection's item list (Web/anchors.json collectionNoteBefore, 10-selectors.js). Every user who can open the
// collection sees it; a user who manages collections also gets Edit (the Auto-collections tab's editor, while the page
// is on) and Refresh now, which queues an update and reads the note again every 4 s until the update is done. It shows
// only while ShowCollectionNote is on (the entry points' JSON block, 70-entrypoints.js).
//
// Which collections are auto-collections comes from GET /SmartSearch/AutoCollections/Ids, read at most every 10 minutes
// (and again after a note wasn't found); the note itself from GET /ByCollection/{id}, once per visit of the page. Both
// answer only about collections the user can open. A page of any other item asks nothing more. Without the list there
// is no note, and the kit's health check warns once and reports it.

const SS_NOTE_ATTR = 'data-jfss-collection-note';
const SS_NOTE_IDS_TTL_MS = 10 * 60 * 1000;
const SS_NOTE_POLL_MS = 4000;
// The polls after Refresh now: 3 minutes, beyond the queue's usual wait.
const SS_NOTE_POLL_LIMIT = 45;
const SS_DETAILS_ROUTE = '/details';

const ssNote = { ids: null, idsAt: 0, idsUser: null, idsLoading: null, current: null };

function ssNoteKey(id) {
    return String(id === undefined || id === null ? '' : id).replace(/-/g, '').toLowerCase();
}

function ssOnDetailsRoute() {
    return String(window.location.hash || '').replace(/^#!?/, '').split('?')[0] === SS_DETAILS_ROUTE;
}

// The auto-collections' ids the user can open, as a Set of keys; read again after 10 minutes or for another user.
function ssNoteIds(fresh) {
    const user = kit.user.id();
    if (!fresh && ssNote.ids && ssNote.idsUser === user && Date.now() - ssNote.idsAt < SS_NOTE_IDS_TTL_MS) {
        return Promise.resolve(ssNote.ids);
    }

    if (ssNote.idsLoading && ssNote.idsUser === user) {
        return ssNote.idsLoading;
    }

    ssNote.idsUser = user;
    const loading = ssApi.acIds().then(function (ids) {
        const set = new Set((Array.isArray(ids) ? ids : []).map(ssNoteKey));
        if (ssNote.idsLoading === loading) {
            ssNote.ids = set;
            ssNote.idsAt = Date.now();
            ssNote.idsLoading = null;
        }

        return set;
    }, function (error) {
        if (ssNote.idsLoading === loading) {
            ssNote.idsLoading = null;
        }

        throw error;
    });
    ssNote.idsLoading = loading;
    return loading;
}

function ssRemoveCollectionNotes(keep) {
    document.querySelectorAll('[' + SS_NOTE_ATTR + ']').forEach(function (node) {
        if (node !== keep) {
            node.remove();
        }
    });
}

function ssNoteText(note) {
    return note.LastSuccessUtc
        ? ssText('acNoteUpdated', { ago: kit.format.ago(note.LastSuccessUtc) })
        : SS_TEXT.acNoteNotUpdated;
}

function ssBuildCollectionNote(entry) {
    const note = entry.note;
    const text = kit.h('span', { class: 'jfss-ac-note-text', text: ssNoteText(note) });
    const children = [kit.icon('autorenew'), text];
    entry.text = text;
    entry.refreshButton = null;
    if (note.CanManage && note.DefinitionId) {
        const actions = kit.h('span', { class: 'jfss-ac-note-actions' });
        if (kit.pages.enabled(SS_PAGE.key)) {
            actions.append(kit.h('a', {
                class: kit.cls('button') + ' jfss-ac-note-edit',
                href: kit.pages.href(SS_PAGE.key, { tab: 'collections', edit: ssNoteKey(note.DefinitionId) }),
                title: ssText('acEditName', { name: note.Name }),
                text: SS_TEXT.acNoteEdit
            }));
        }

        entry.refreshButton = kit.button(SS_TEXT.acNoteRefresh, {
            className: 'jfss-ac-note-refresh',
            title: ssText('acRefreshName', { name: note.Name }),
            onClick: function () {
                ssRefreshFromNote(entry);
            }
        });
        actions.append(entry.refreshButton);
        children.push(actions);
    }

    const node = kit.h('div', { class: 'jfss-ac-note', 'data-item-id': entry.itemId }, children);
    node.setAttribute(SS_NOTE_ATTR, '');
    ssSyncNoteBusy(entry);
    return node;
}

function ssSyncNoteBusy(entry) {
    if (entry.refreshButton) {
        entry.refreshButton.disabled = !!entry.polling;
    }

    if (entry.text && entry.note) {
        entry.text.textContent = entry.polling ? SS_TEXT.acNoteUpdating : ssNoteText(entry.note);
    }
}

// Puts the note right before its place in the shown details page (once; again when jellyfin-web built the view anew).
function ssPlaceCollectionNote(entry) {
    const before = ssCollectionNoteBefore();
    if (!before || !before.parentNode) {
        ssRemoveCollectionNotes(null);
        return;
    }

    if (!entry.node) {
        entry.node = ssBuildCollectionNote(entry);
    }

    if (before !== entry.node && entry.node.nextSibling !== before) {
        before.parentNode.insertBefore(entry.node, before);
    }

    ssRemoveCollectionNotes(entry.node);
}

function ssLoadCollectionNote(entry) {
    return ssApi.acByCollection(entry.itemId).then(function (note) {
        if (ssNote.current !== entry) {
            return;
        }

        entry.note = note || null;
        if (entry.node && entry.note) {
            ssSyncNoteBusy(entry);
        }
    });
}

function ssIsNotFound(error) {
    return !!error && error.status === 404;
}

// Queues an update and reads the note again every 4 s until the update is done (its time changed), or 3 minutes.
function ssRefreshFromNote(entry) {
    if (entry.polling || !entry.note) {
        return;
    }

    const before = entry.note.LastSuccessUtc || null;
    entry.polling = true;
    ssSyncNoteBusy(entry);
    let tries = 0;
    function done(message) {
        entry.polling = false;
        ssSyncNoteBusy(entry);
        kit.toast(message);
    }

    function poll() {
        if (ssNote.current !== entry) {
            return;
        }

        tries++;
        ssApi.acByCollection(entry.itemId).then(function (note) {
            if (ssNote.current !== entry) {
                return;
            }

            entry.note = note || entry.note;
            if ((entry.note.LastSuccessUtc || null) !== before) {
                done(ssText('acNoteDone', { name: entry.note.Name }));
            } else if (tries >= SS_NOTE_POLL_LIMIT) {
                done(SS_TEXT.acNoteStillQueued);
            } else {
                setTimeout(poll, SS_NOTE_POLL_MS);
            }
        }, function (error) {
            done(ssText('acNoteRefreshFailed', { message: error && error.message ? error.message : String(error) }));
        });
    }

    ssApi.acRefresh(entry.note.DefinitionId).then(function () {
        setTimeout(poll, SS_NOTE_POLL_MS);
    }, function (error) {
        done(ssText('acNoteRefreshFailed', { message: error && error.message ? error.message : String(error) }));
    });
}

// The scanner: on a details page, find out once per visit whether the item is an auto-collection, then keep its note in
// place. Never throws (the kit reports a rejection).
function ssSyncCollectionNote() {
    if (!ssEntryPoints().collectionNote || !ssOnDetailsRoute() || !kit.user.id()) {
        ssNote.current = null;
        ssRemoveCollectionNotes(null);
        return null;
    }

    const itemId = ssNoteKey(kit.hashParams().get('id'));
    if (!itemId) {
        return null;
    }

    const user = kit.user.id();
    const known = ssNote.current;
    if (known && known.itemId === itemId && known.user === user) {
        if (known.note) {
            ssPlaceCollectionNote(known);
        }

        return null;
    }

    const entry = { itemId: itemId, user: user, note: null, node: null, text: null, refreshButton: null, polling: false };
    ssNote.current = entry;
    ssRemoveCollectionNotes(null);
    return ssNoteIds(false).then(function (ids) {
        if (ssNote.current !== entry || !ids.has(itemId)) {
            return null;
        }

        return ssLoadCollectionNote(entry).then(function () {
            if (ssNote.current !== entry || !entry.note) {
                return;
            }

            ssPlaceCollectionNote(entry);
            ssCheckCollectionNoteAnchor().then(function (present) {
                if (present && ssNote.current === entry) {
                    ssPlaceCollectionNote(entry);
                }
            });
        }, function (error) {
            if (!ssIsNotFound(error)) {
                throw error;
            }

            // No longer an auto-collection (or not one this user can open): the list is read again next time.
            ssNote.idsAt = 0;
        });
    });
}

kit.observe(ssSyncCollectionNote);
kit.onView(function () {
    const entry = ssNote.current;
    if (entry && entry.note && !entry.polling && ssOnDetailsRoute() && ssNoteKey(kit.hashParams().get('id')) === entry.itemId) {
        // Back on the page: the time shown is read again.
        ssLoadCollectionNote(entry).then(function () {
            ssPlaceCollectionNote(entry);
        }, function () {
            ssNote.current = null;
            ssRemoveCollectionNotes(null);
        });
    }
});
