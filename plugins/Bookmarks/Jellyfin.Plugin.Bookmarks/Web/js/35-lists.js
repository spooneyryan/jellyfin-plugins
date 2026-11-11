// Bookmark lists and clip jobs. A list shows one item's bookmarks, either on the detail page or in the player dialog;
// lists are refreshed whenever a bookmark of their item changes.

function registerList(list) {
    state.lists.add(list);
    return refreshList(list);
}

async function refreshList(list) {
    try {
        const results = await Promise.all([
            api('GET', 'Bookmarks', undefined, { itemId: list.itemId }),
            getCapabilities()
        ]);
        list.bookmarks = results[0] || [];
        list.capabilities = results[1];
        renderList(list);
        list.bookmarks.forEach(function (bookmark) {
            if (bookmark.Clip && (bookmark.Clip.Status === 'Queued' || bookmark.Clip.Status === 'Running')) {
                watchClip(bookmark);
            }
        });
    } catch (e) {
        report(e);
    }
}

// A bookmark of the video changed (made, saved, renamed, deleted, its clip): the lists of that video read again, and the
// change listeners (the My Bookmarks page, which lists every video's bookmarks) told, with the detail watchClip adds
// ({ clipTick, bookmark }: a progress tick of a clip being made, the bookmark as just read); without one the page reads
// that video's part of its list again.
function notifyChanged(itemId, detail) {
    state.lists.forEach(function (list) {
        if (!list.container.isConnected) {
            state.lists.delete(list);
        } else if (list.itemId === itemId) {
            refreshList(list);
        }
    });
    state.changeListeners.forEach(function (listener) {
        try {
            listener(itemId, detail || null);
        } catch (e) {
            report(e);
        }
    });
}

// The rows into the list's container, which is a list (role="list") while it has rows; the empty text alone
// isn't one.
function renderList(list) {
    const rows = list.bookmarks.map(function (bookmark) {
        return renderRow(bookmark, list);
    });
    if (rows.length === 0) {
        list.container.removeAttribute('role');
        if (list.emptyText) {
            rows.push(h('div', { class: 'jfbm-empty', text: list.emptyText }));
        }
    } else {
        list.container.setAttribute('role', 'list');
    }

    list.container.replaceChildren.apply(list.container, rows);
    if (list.onRendered) {
        list.onRendered(list.bookmarks);
    }
}

// The actions a row offers, in order, for the row's buttons and the compact menu alike:
// { icon, label, run }. "Name it"/Rename is the My Bookmarks page's inline rename (list.inlineRename); the
// scissors make a clip of a range, or (on the page, list.pointClip) open the editor on a point bookmark with the End
// field focused, so an end can be set first; a "Not in library" or "Unavailable" bookmark gets no clip (the server
// would refuse one).
function rowActions(bookmark, list, row) {
    const hasRange = typeof bookmark.EndTicks === 'number';
    const clip = bookmark.Clip;
    const clipBusy = clip && (clip.Status === 'Queued' || clip.Status === 'Running');
    const unavailable = unavailableReason(bookmark);
    const canClip = list.capabilities.CanCreateClips && !clipBusy && !unavailable;
    const actions = [];
    if (list.inlineRename && bookmark.Name) {
        actions.push({ icon: 'label', label: 'Rename', run: function () {
            startInlineRename(row, bookmark, list);
        } });
    }

    if (canClip && hasRange) {
        actions.push({ icon: 'content_cut', label: clip && clip.Status === 'Completed' ? 'Make the clip again' : 'Make a clip', run: function () {
            startClip(bookmark);
        } });
    } else if (canClip && list.pointClip) {
        actions.push({ icon: 'content_cut', label: 'Make a clip (set an end first)', run: function () {
            openEditor({ bookmark: bookmark, focus: 'end' });
        } });
    }

    actions.push({ icon: 'edit', label: 'Edit', run: function () {
        openEditor({ bookmark: bookmark });
    } });
    actions.push({ icon: 'delete', label: 'Delete', run: function () {
        confirmDelete(bookmark);
    } });
    return actions;
}

// One bookmark: the time button plays or jumps; the title line is the display title, with "Name it"
// when the bookmark has no name, and its badges; a name's note shows as a muted second line; then the clip's status and
// the actions. "Not in library" (greyed) and "Unavailable" rows can't be played or clipped; editing, renaming and
// deleting still work. extra.itemLine (the My Bookmarks page's flat view) is a line naming the video, under the title.
// The My Bookmarks page's list adds: a checkbox to select the row for a bulk delete
// (list.select: { isSelected(id), toggle(bookmark, checked) }), the inline rename (list.inlineRename: Name it and
// Rename turn the title into a field), the point bookmark's scissors (list.pointClip) and, on a narrow screen, one
// "More" button in place of the action buttons, which opens our own menu of them (list.compactMenu; the stylesheet
// decides which shows).
function renderRow(bookmark, list, extra) {
    const hasRange = typeof bookmark.EndTicks === 'number';
    const label = hasRange
        ? formatTime(bookmark.StartTicks) + ' – ' + formatTime(bookmark.EndTicks)
        : formatTime(bookmark.StartTicks);
    const title = displayTitle(bookmark);
    const subtitle = bookmark.Name ? noteFirstLine(bookmark.Note) : '';
    const unavailable = unavailableReason(bookmark);
    const row = h('div', { class: 'jfbm-row' + (bookmark.Detached ? ' jfbm-row-detached' : ''), 'data-id': bookmark.Id, role: 'listitem' });
    const actions = rowActions(bookmark, list, row);
    const select = list.select ? h('input', {
        type: 'checkbox',
        class: 'jfbm-row-select-box',
        'aria-label': 'Select ' + title,
        checked: list.select.isSelected(bookmark.Id)
    }) : null;
    if (select) {
        select.addEventListener('change', function () {
            list.select.toggle(bookmark, select.checked);
        });
    }

    kit.append(row,
        select ? h('label', { class: 'jfbm-row-select', title: 'Select' }, select) : null,
        h('button', {
            type: 'button',
            class: 'jfbm-time-button',
            title: unavailable ? unavailable.badge : (list.mode === 'player' ? 'Jump here' : 'Play from here'),
            disabled: !!unavailable,
            onclick: function () {
                list.onJump(bookmark);
            }
        }, [icon('play_arrow'), h('span', { text: label })]),
        h('div', { class: 'jfbm-row-info' }, [
            h('div', { class: 'jfbm-row-title-line' }, [
                h('span', { class: 'jfbm-row-title', text: title, title: title }),
                bookmark.Name ? null : linkButton('Name it', function () {
                    if (list.inlineRename) {
                        startInlineRename(row, bookmark, list);
                    } else {
                        openEditor({ bookmark: bookmark });
                    }
                }, { class: kit.cls('link') + ' jfbm-name-it' }),
                unavailable ? h('span', { class: 'jfbm-badge ' + unavailable.className, text: unavailable.badge, title: unavailable.title }) : null,
                bookmark.Relinked && !bookmark.Detached ? h('span', {
                    class: 'jfbm-badge jfbm-badge-relinked',
                    text: 'Relinked, check position',
                    title: 'This bookmark moved to another copy of the video. Check that its position is still right.'
                }) : null,
                bookmark.Relinked && !bookmark.Detached ? linkButton('Looks right', function () {
                    confirmRelinked(bookmark);
                }, { class: kit.cls('link') + ' jfbm-looks-right' }) : null
            ]),
            extra && extra.itemLine ? extra.itemLine : null,
            subtitle ? h('div', { class: 'jfbm-row-subtitle', text: subtitle, title: bookmark.Note }) : null,
            renderClipStatus(bookmark, list)
        ]),
        h('div', { class: 'jfbm-row-actions' }, actions.map(function (action) {
            return iconButton(action.icon, action.label, action.run);
        }).concat(list.compactMenu ? [iconButton('more_vert', 'More actions', function () {
            openRowMenu(bookmark, list, actions, title);
        })] : [])));
    // The "More" button carries its own class (the stylesheet shows it alone on a narrow screen).
    if (list.compactMenu) {
        row.querySelector('.jfbm-row-actions').lastChild.classList.add('jfbm-row-more');
    }

    return row;
}

// The actions of a row as our own menu (on a phone the actions collapse into a ⋯ button that opens it): a small dialog
// (a bottom sheet on a phone, kit.css) titled with the bookmark, one button per action of at least 40 px, play from here
// first while the video can be played. Each closes the menu, then acts. Up and Down move through the items on every
// layout (a menu's keys, not only the TV layout's D-pad: a keyboard user otherwise only had Tab), wrapping at the ends;
// Home and End go to the first and the last.
function openRowMenu(bookmark, list, actions, title) {
    const unavailable = unavailableReason(bookmark);
    return openActionMenu(title, [{ icon: 'play_arrow', label: list.mode === 'player' ? 'Jump here' : 'Play from here', run: function () {
        list.onJump(bookmark);
    }, disabled: !!unavailable }].concat(actions));
}

// The menu itself, for any row's actions ({ icon, label, run, disabled }): the My Bookmarks rows above, and since 1.2.0 the
// My Clips page's rows (70-page-clips.js), which put their own actions in the same order as their buttons.
function openActionMenu(title, items) {
    const dialog = kit.dialog({
        title: title,
        scopedKeys: true,
        onKey: function (event) {
            return moveMenuFocus(dialog.body, event);
        }
    });
    dialog.body.append(h('div', { class: 'jfbm-row-menu', role: 'menu' }, items.map(function (action) {
        return h('button', {
            type: 'button',
            class: 'jfbm-row-menu-item',
            role: 'menuitem',
            disabled: !!action.disabled,
            onclick: function () {
                dialog.close();
                action.run();
            }
        }, [icon(action.icon), h('span', { text: action.label })]);
    })));
    const first = dialog.body.querySelector('button:not([disabled])');
    if (first) {
        first.focus();
    }

    return dialog;
}

// The row menu's keys: ArrowDown and ArrowUp move the focus to the next or previous item that can be used, wrapping
// at the ends, Home and End to the first and the last. Answers true when the key was the menu's (the dialog's
// scopedKeys then stops it there), false for any other key.
function moveMenuFocus(root, event) {
    const key = event.key;
    if (key !== 'ArrowDown' && key !== 'ArrowUp' && key !== 'Home' && key !== 'End') {
        return false;
    }

    const items = Array.prototype.slice.call(root.querySelectorAll('.jfbm-row-menu-item')).filter(function (item) {
        return !item.disabled;
    });
    if (items.length === 0) {
        return false;
    }

    const index = items.indexOf(document.activeElement);
    const next = key === 'Home' ? 0
        : key === 'End' ? items.length - 1
            : key === 'ArrowDown' ? (index < 0 || index === items.length - 1 ? 0 : index + 1)
                : (index <= 0 ? items.length - 1 : index - 1);
    event.preventDefault();
    items[next].focus();
    return true;
}

// Whether a row holds an inline rename that is still the user's (typed, not saved): the page keeps such a row when
// it draws the rest of the video again (60-page-bookmarks.js); a rename that has saved waits for that redraw and is
// replaced by it (jfbm-row-rename-saved).
function isRenameOpen(row) {
    return !!row && row.classList.contains('jfbm-row-renaming') && !row.classList.contains('jfbm-row-rename-saved');
}

// The inline rename: the row's title line becomes a field holding the name, selected,
// with Save and Cancel; Enter saves through POST /Bookmarks/{id}/Name with the UpdatedAt the row was read with, Escape
// (or a TV remote's Back key) cancels and the title line comes back. A 409 (changed elsewhere) reads the bookmark again
// and shows the name it has now, selected, under "Changed elsewhere: it is named "…" now. Save replaces that.", as the
// player's name dialog does, so the next Save carries the latest; one deleted meanwhile ends the rename
// with the editor's toast. An empty field on a named bookmark unnames it; on an unnamed one it is a Cancel.
function startInlineRename(row, bookmark, list) {
    const line = row.querySelector('.jfbm-row-title-line');
    if (!line || line.querySelector('.jfbm-rename')) {
        return;
    }

    let current = bookmark;
    let saving = false;
    const kept = Array.prototype.slice.call(line.childNodes);
    const capabilities = list.capabilities || {};
    const input = kit.input({
        class: 'jfbm-rename-input',
        value: current.Name || '',
        maxlength: String(capabilities.MaxNameLength || 100),
        autocomplete: 'off',
        placeholder: 'Name',
        'aria-label': 'Name'
    });
    const error = h('div', { class: kit.cls('error') + ' jfbm-rename-error', role: 'alert' });
    const buttons = [];
    // Read-only, not disabled, while it saves: a browser blurs a disabled field, and the focus must still be in the
    // row when the page draws it again (keepFocus in 60-page-bookmarks.js).
    const setBusy = function (busy) {
        input.readOnly = busy;
        input.setAttribute('aria-busy', busy ? 'true' : 'false');
        buttons.forEach(function (button) {
            button.disabled = busy;
        });
    };
    const restore = function () {
        line.replaceChildren.apply(line, kept);
        row.classList.remove('jfbm-row-renaming', 'jfbm-row-rename-saved', 'jfbm-row-rename-kept');
    };
    const cancel = function () {
        // The page kept this row through a redraw of its video while the name was typed (jfbm-row-rename-kept), so what
        // it shows may be older than the page's: the video is read again, which draws the row afresh (the focus kept).
        const outdated = row.classList.contains('jfbm-row-rename-kept');
        restore();
        const focus = row.querySelector('.jfbm-name-it') || row.querySelector('button[title="Rename"]') || row.querySelector('.jfbm-time-button');
        if (focus) {
            focus.focus();
        }

        if (outdated) {
            notifyChanged(current.ItemId);
        }
    };
    const save = async function () {
        if (saving) {
            return;
        }

        const name = input.value.trim();
        if (!name && !current.Name) {
            cancel();
            return;
        }

        saving = true;
        setBusy(true);
        error.textContent = '';
        try {
            const named = await api('POST', 'Bookmarks/' + current.Id + '/Name', { Name: name || null, IfUpdatedAt: current.UpdatedAt || null });
            // The row is drawn again by the page (the video's part of the list read again) or the video's list; saved,
            // the rename is no longer the user's to keep across that redraw (isRenameOpen).
            row.classList.add('jfbm-row-rename-saved');
            notifyChanged(named.ItemId || current.ItemId);
            toast(name ? 'Named "' + (named.Name || name) + '".' : 'Name removed.');
        } catch (e) {
            saving = false;
            setBusy(false);
            if (e.status === CONFLICT_STATUS) {
                await reloadRenameAfterConflict(e);
            } else {
                error.textContent = e.message;
                input.focus();
            }
        }
    };
    const reloadRenameAfterConflict = async function (conflict) {
        let latest;
        try {
            latest = await api('GET', 'Bookmarks/' + current.Id);
        } catch (e) {
            if (e.status === 404) {
                restore();
                notifyChanged(current.ItemId);
                toast('This bookmark was deleted elsewhere.');
            } else {
                error.textContent = conflict.message;
                input.focus();
            }

            return;
        }

        current = latest;
        input.value = latest.Name || '';
        error.textContent = latest.Name
            ? 'Changed elsewhere: it is named "' + latest.Name + '" now. Save replaces that.'
            : 'Changed elsewhere: it has no name now. Save names it.';
        input.focus();
        if (typeof input.select === 'function') {
            input.select();
        }
    };
    input.addEventListener('keydown', function (event) {
        if (event.isComposing || event.keyCode === 229) {
            return;
        }

        if (event.key === 'Enter') {
            event.preventDefault();
            event.stopPropagation();
            save();
        } else if (event.key === 'Escape' || kit.shortcuts.isBackKey(event)) {
            event.preventDefault();
            event.stopPropagation();
            cancel();
        }
    });
    const saveButton = iconButton('check', 'Save the name', save);
    const cancelButton = iconButton('close', 'Cancel renaming', cancel);
    buttons.push(saveButton, cancelButton);
    row.classList.add('jfbm-row-renaming');
    line.replaceChildren(h('div', { class: 'jfbm-rename' }, [input, saveButton, cancelButton]), error);
    kit.upgrade(line);
    input.focus();
    if (typeof input.select === 'function') {
        input.select();
    }
}

// The Relinked badge's "Looks right": the rename endpoint with the unchanged name clears the mark, with the
// UpdatedAt the row was read with, so a change made elsewhere meanwhile is a 409 with Reload.
async function confirmRelinked(bookmark) {
    try {
        await api('POST', 'Bookmarks/' + bookmark.Id + '/Name', { Name: bookmark.Name || null, IfUpdatedAt: bookmark.UpdatedAt || null });
        notifyChanged(bookmark.ItemId);
    } catch (e) {
        toast(e.message, e.status === CONFLICT_STATUS ? {
            timeout: 8000,
            action: {
                label: 'Reload',
                onClick: function () {
                    notifyChanged(bookmark.ItemId);
                }
            }
        } : undefined);
    }
}

// The clip's lines of a row: its job (queued, being made with Cancel, failed, cancelled) and its file (ready or an earlier
// one, with Download, Preview for an MP4, and Delete clip). On the My Bookmarks page, while the server offers My Clips,
// the file's line also links to the clip there, marked (…&highlight=<id>).
function renderClipStatus(bookmark, list) {
    const clip = bookmark.Clip;
    if (!clip) {
        return null;
    }

    const lines = [];
    const cancel = linkButton('Cancel', function () {
        cancelClip(bookmark);
    });

    switch (clip.Status) {
        case 'Queued':
            lines.push(h('div', { class: 'jfbm-clip' }, [icon('hourglass_empty'), h('span', { text: 'Clip queued' }), cancel]));
            break;
        case 'Running': {
            const percent = typeof clip.Progress === 'number' ? Math.floor(clip.Progress) : 0;
            lines.push(h('div', { class: 'jfbm-clip' }, [
                icon('hourglass_empty'),
                h('span', { text: 'Making clip ' + percent + '%' }),
                h('div', { class: 'jfbm-progress', role: 'progressbar', 'aria-valuenow': String(percent), 'aria-valuemin': '0', 'aria-valuemax': '100' },
                    h('div', { style: 'width:' + percent + '%' })),
                cancel
            ]));
            break;
        }
        case 'Failed':
            lines.push(h('div', { class: 'jfbm-clip jfbm-clip-failed', title: clip.Error || '' }, [
                icon('error'),
                h('span', { text: 'Clip failed: ' + (clip.Error || 'unknown error') })
            ]));
            break;
        case 'Cancelled':
            lines.push(h('div', { class: 'jfbm-clip' }, [icon('cancel'), h('span', { text: 'Clip cancelled' })]));
            break;
        default:
            break;
    }

    // The clip file can be from an earlier job; it stays until a new clip replaces it. Since 1.2 an MKV (a Fast clip) has
    // Preview too: Chrome, Edge and Firefox play Matroska, and the preview says so when a browser can't.
    if (clip.FileName) {
        const canPreview = /\.(mp4|mkv)$/i.test(clip.FileName);
        const label = (clip.Status === 'Completed' ? 'Clip ready, ' : 'Previous clip, ') + formatSize(clip.FileSize)
            + (clip.IsOutdated ? ' (made before the range was changed)' : '');
        lines.push(h('div', { class: 'jfbm-clip jfbm-clip-ready' }, [
            icon('check_circle'),
            h('span', { text: label }),
            h('a', { class: kit.cls('link'), href: clipFileUrl(bookmark.Id, true), download: clip.FileName, text: 'Download' }),
            canPreview ? linkButton('Preview', function () {
                openPreview(bookmark);
            }) : null,
            linkButton('Delete clip', function () {
                deleteClip(bookmark);
            }),
            list && list.mode === 'page' && offeredPages(list.capabilities).clips
                ? kit.pages.link(CLIPS_PAGE.key, { highlight: pageItemKey(bookmark.Id) }, 'View in My Clips', { class: kit.cls('link') + ' jfbm-clip-in-clips' })
                : null
        ]));
    }

    return lines.length > 0 ? h('div', {}, lines) : null;
}

async function startClip(bookmark) {
    try {
        const capabilities = await getCapabilities();
        const updated = await api('POST', 'Bookmarks/' + bookmark.Id + '/Clip', { Mode: capabilities.DefaultClipMode || 'Accurate' });
        watchClip(updated);
        notifyChanged(bookmark.ItemId);
        toast('Making the clip.');
    } catch (e) {
        toast(e.message, { timeout: 8000 });
    }
}

async function cancelClip(bookmark) {
    try {
        await api('POST', 'Bookmarks/' + bookmark.Id + '/Clip/Cancel');
        notifyChanged(bookmark.ItemId);
    } catch (e) {
        toast(e.message);
    }
}

async function deleteClip(bookmark) {
    try {
        await api('DELETE', 'Bookmarks/' + bookmark.Id + '/Clip');
        notifyChanged(bookmark.ItemId);
        toast('Clip deleted.');
    } catch (e) {
        toast(e.message);
    }
}

// Polls a bookmark while its clip is being made, keeping lists up to date and announcing the result. A progress tick is
// told as such (the My Bookmarks page patches that row in place instead of reading every page again); the clip's end is
// a plain change. The clip-ready toast offers Download and, while the server offers My Clips, "View in My Clips", the
// clip marked there.
function watchClip(bookmark) {
    if (state.clipWatchers.has(bookmark.Id)) {
        return;
    }

    const poll = async function () {
        let current;
        try {
            current = await api('GET', 'Bookmarks/' + bookmark.Id);
        } catch (e) {
            state.clipWatchers.delete(bookmark.Id);
            return;
        }

        const status = current.Clip && current.Clip.Status;
        notifyChanged(current.ItemId, { clipTick: status === 'Queued' || status === 'Running', bookmark: current });
        if (status === 'Queued' || status === 'Running') {
            state.clipWatchers.set(bookmark.Id, setTimeout(poll, CLIP_POLL_MS));
            return;
        }

        state.clipWatchers.delete(bookmark.Id);
        if (status === 'Completed') {
            const actions = [{
                label: 'Download',
                onClick: function () {
                    downloadClip(current);
                }
            }];
            if (offeredPages(freshCapabilities() || state.capabilities).clips) {
                actions.push({ label: 'View in My Clips', href: kit.pages.href(CLIPS_PAGE.key, { highlight: pageItemKey(current.Id) }) });
            }

            toast('Clip ready: ' + current.Clip.FileName, { timeout: 12000, actions: actions });
        } else if (status === 'Failed') {
            toast('Clip failed: ' + (current.Clip.Error || 'unknown error'), { timeout: 12000 });
        }
    };

    state.clipWatchers.set(bookmark.Id, setTimeout(poll, CLIP_POLL_MS));
}
