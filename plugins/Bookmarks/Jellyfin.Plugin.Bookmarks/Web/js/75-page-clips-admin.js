// The My Clips page's "All users" scope, active only while the server says
// Capabilities.CanManageAllClips (an admin on this request, with the setting "Admins can see and manage every user's clips"
// on; the server checks both again on every request, the page only hides controls). A line above the toolbar (which stays
// the same for everyone) holds "My clips" and "All users" (two buttons, aria-pressed; one Tab stop each, as My Bookmarks'
// segments), and in "All users" an Owner select of the users the server counts clips for (GET
// /Bookmarks/Admin/Clips/Summary: "alice (3)", a deleted user's "Deleted user (id)"). The list, the summary, the poll and a row read again then ask the admin routes (GET /Bookmarks/Admin/Clips with
// the page's filters and userId, /Summary, /Status), and the hash carries scope=all and user=<id> as it carries the filters
// (the page's deep link state); a hash naming the scope for someone who may not use it shows their own clips and is left as
// it came.
//
// A row of the scope names its owner (bidi-isolated, dir="auto": a name in another script never reorders the line) and the
// clip file's full path (monospace, with Copy), and never a note: the server sends none (its title is the bookmark's name,
// else the file's). Its actions (no rename or remake of other
// users' clips): Preview and Download through GET /Bookmarks/Admin/Clips/{id}/File (with the viewer's download permission),
// Play the video (while the admin can open it), Cancel a clip being made, Delete a file that is there, Forget one that is
// gone, Dismiss a failed clip without a file (DELETE /Bookmarks/Admin/Clips/{id}, Forget and Dismiss with onlyIfMissing=true,
// which never deletes a file); a clip whose folder is offline or not recognised, or whose last rename isn't finished, offers
// only Play and no checkbox, as on the owner's page. The dialogs that delete or forget start on Cancel and say whose clip it
// is; the server's refusals (in use, being made, offline, outside the clip folders) are said in them, as the owner's page
// says them. The bulk delete is the page's own, through POST /Bookmarks/Admin/Clips/BulkDelete, its outcome counted per
// reason. Every text is a text node (the kit's h); a name or a file name inside a sentence is isolated (U+2068 and U+2069),
// so a name in another script never reorders the sentence around it. An admin route answering 403 (the
// setting turned off, or the admin demoted, while the page is open) ends the scope: the capabilities are
// read afresh and the page shows the admin's own clips (clipsAdminTurnedOff).

// The scope's choices: the default first.
const CLIPS_SCOPES = Object.freeze([
    Object.freeze({ value: '', label: 'My clips' }),
    Object.freeze({ value: 'all', label: 'All users' })
]);

// Whether the page shows every user's clips now: the hash asks for it and the server allows it to this admin.
function clipsAdminScope(page) {
    return !!page && page.state.scope === 'all' && !!page.capabilities && page.capabilities.CanManageAllClips === true;
}

// The clip list's routes for the page's scope: the list ('' ), Summary, Status and BulkDelete.
function clipsRoute(page, part) {
    return (clipsAdminScope(page) ? 'Bookmarks/Admin/Clips' : 'Bookmarks/Clips') + (part ? '/' + part : '');
}

// The scope switch and the Owner select, in a line of their own above the toolbar (so the toolbar is the same for everyone);
// nothing for a viewer who may not use the scope.
function buildClipsScope(page) {
    if (!page.capabilities || page.capabilities.CanManageAllClips !== true) {
        page.scopeButtons = [];
        page.ownerSelect = null;
        return null;
    }

    page.scopeButtons = CLIPS_SCOPES.map(function (scope) {
        return h('button', {
            type: 'button',
            class: 'jfbm-page-segment jfbm-clips-scope-button',
            'data-jfbm-scope': scope.value || 'mine',
            'aria-pressed': 'false',
            text: scope.label,
            onclick: function () {
                if (page.state.scope !== scope.value) {
                    changeClipsState(page, { scope: scope.value, user: '' });
                }
            }
        });
    });
    page.ownerSelect = kit.select({ class: 'jfbm-clips-filter jfbm-clips-owner-select', 'aria-label': 'Owner' }, [{ value: '', text: 'All owners' }]);
    page.ownerSelect.addEventListener('change', function () {
        changeClipsState(page, { user: pageIdParam(page.ownerSelect.value) });
    });
    return h('div', { class: 'jfbm-clips-scope-bar' }, [h('div', { class: 'jfbm-page-segments jfbm-clips-scope', role: 'group', 'aria-label': 'Whose clips' }, page.scopeButtons), page.ownerSelect]);
}

// The switch and the select after a state change, and the select's users from the last summary.
function syncClipsScope(page) {
    if (!page.scopeButtons || page.scopeButtons.length === 0) {
        return;
    }

    const admin = clipsAdminScope(page);
    page.scopeButtons.forEach(function (button) {
        const value = button.getAttribute('data-jfbm-scope') === 'all' ? 'all' : '';
        button.setAttribute('aria-pressed', String(value === (admin ? 'all' : '')));
    });
    kit.setHidden(page.ownerSelect, !admin);
    fillOwnerSelect(page);
}

// The Owner select's choices: every user the summary counts clips for, by name, with how many; the user the hash names
// even when the summary doesn't count them (no clips any more), so the select always says what the list shows.
function fillOwnerSelect(page) {
    if (!page.ownerSelect) {
        return;
    }

    const wanted = page.state.user || '';
    const users = clipsAdminScope(page) && page.summary && Array.isArray(page.summary.Users) ? page.summary.Users : [];
    const choices = [{ value: '', text: 'All owners' }].concat(users.map(function (user) {
        return { value: pageItemKey(user.UserId), text: (user.UserName || '') + ' (' + (user.Count || 0) + ')' };
    }));
    if (wanted && !choices.some(function (choice) {
        return choice.value === wanted;
    })) {
        choices.push({ value: wanted, text: 'A user without clips' });
    }

    // Its options drawn again only when they change: every summary read again (each poll's end, each action)
    // would otherwise replace them, which resets a native select a remote or a screen reader is on.
    const signature = choices.map(function (choice) {
        return choice.value + '\u0000' + choice.text;
    }).join('\u0001');
    if (page.ownerChoices !== signature) {
        const focused = document.activeElement === page.ownerSelect;
        page.ownerSelect.replaceChildren.apply(page.ownerSelect, choices.map(function (choice) {
            return h('option', { value: choice.value, text: choice.text });
        }));
        page.ownerChoices = signature;
        page.ownerSelect.value = wanted;
        if (focused) {
            page.ownerSelect.focus();
        }
    } else if (page.ownerSelect.value !== wanted) {
        page.ownerSelect.value = wanted;
    }
}

// The summary line in the scope: "All users · 14 clips · 1.6 GB · 3 users", or one owner's "alice · 3 clips · 45 MB".
function clipsAdminSummaryText(summary, userKey) {
    if (!summary || !Array.isArray(summary.Users)) {
        return null;
    }

    if (userKey) {
        const user = summary.Users.filter(function (u) {
            return pageItemKey(u.UserId) === userKey;
        })[0];
        return user
            ? [user.UserName || '', plural(user.Count || 0, 'clip'), formatSize(user.TotalBytes || 0)].join(' · ')
            : 'No clips of this user';
    }

    return ['All users', plural(summary.Count || 0, 'clip'), formatSize(summary.TotalBytes || 0), plural(summary.Users.length, 'user')].join(' · ');
}

// A row's owner: the name as the server has it now (a deleted user's "Deleted user (id)"), isolated for bidi text.
function clipOwnerLine(clip) {
    return h('div', { class: 'jfbm-clip-owner' + (clip.UserDeleted === true ? ' jfbm-clip-owner-deleted' : '') }, [
        icon('person'),
        h('bdi', { dir: 'auto', class: 'jfbm-clip-owner-name', text: clip.UserName || '' })
    ]);
}

// A row's clip file path (the full path, monospace, with a Copy button), left to right whatever the page's direction.
// On the TV layout the path alone: a TV has no clipboard to paste into, and a button more per row is one
// more D-pad stop.
function clipPathLine(clip, tv) {
    if (!clip.FilePath) {
        return null;
    }

    const path = h('code', { class: 'jfbm-clip-path-text', dir: 'ltr', text: clip.FilePath });
    if (tv) {
        return h('div', { class: 'jfbm-clip-path' }, [path]);
    }

    const copy = iconButton('content_copy', 'Copy the path', function () {
        copyClipPath(clip.FilePath, path);
    });
    copy.classList.add('jfbm-clip-path-copy');
    return h('div', { class: 'jfbm-clip-path' }, [path, copy]);
}

// The path to the clipboard where the browser allows it (a secure page); elsewhere the path is selected for Ctrl+C.
function copyClipPath(text, node) {
    const select = function () {
        const selection = typeof window.getSelection === 'function' ? window.getSelection() : null;
        if (selection && typeof document.createRange === 'function') {
            const range = document.createRange();
            range.selectNodeContents(node);
            selection.removeAllRanges();
            selection.addRange(range);
        }

        toast('The path is selected: press Ctrl+C to copy it.');
    };
    if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        navigator.clipboard.writeText(text).then(function () {
            toast('Path copied.');
        }, select);
    } else {
        select();
    }
}

// The scope's row actions, in order, as clipActions gives them: { icon, label, run, suggested }.
function clipAdminActions(page, clip) {
    const capabilities = page.capabilities || {};
    const c = clip.Clip || {};
    const state = c.State;
    const writable = capabilities.StoreReadOnly !== true;
    const key = pageItemKey(clip.Id);
    const actions = [];
    if (state === 'Ready' && capabilities.CanDownloadClips === true) {
        actions.push({ icon: 'ondemand_video', label: 'Preview', run: function () {
            openPreview(clip, {
                title: clipTitle(clip),
                admin: true,
                returnFocus: clipRowReturnFocus(page, key, 'Preview'),
                onUnavailable: function () {
                    refreshClipKeys(page, [key]);
                }
            });
        } });
        actions.push({ icon: 'file_download', label: 'Download', run: function () {
            downloadClip(clip, true);
        } });
    }

    if (clip.ItemAvailable === true) {
        actions.push({ icon: 'play_arrow', label: 'Play the video at the clip\'s start', run: function () {
            playClipSource(clip);
        } });
    }

    if (writable && state === 'InProgress') {
        actions.push({ icon: 'cancel', label: 'Cancel the clip', run: function () {
            cancelAdminClipJob(page, clip);
        } });
    }

    if (writable && state === 'Ready') {
        actions.push({ icon: 'delete', label: 'Delete clip', run: function () {
            confirmAdminClipDelete(page, clip, false);
        } });
    } else if (writable && state === 'Missing') {
        actions.push({ icon: 'remove_circle_outline', label: 'Forget clip', run: function () {
            confirmAdminClipDelete(page, clip, true);
        } });
    } else if (writable && state === 'Failed') {
        actions.push({ icon: 'clear', label: 'Dismiss', run: function () {
            dismissAdminClip(page, clip);
        } });
    }

    return actions;
}

// A name or a file name inside a sentence, isolated (FSI U+2068 ... PDI U+2069): its own direction, so a
// name in a right-to-left script never reorders the words around it.
function isolate(text) {
    return '\u2068' + String(text) + '\u2069';
}

// Whose clip a sentence names: "alice's" (the name isolated), or for a deleted user "this deleted user's".
function clipOwnerPossessive(clip) {
    return clip.UserDeleted === true ? 'this deleted user\'s' : clip.UserName ? isolate(clip.UserName) + '\'s' : 'this user\'s';
}

// An admin route refused the scope (403): the setting "Admins can see and manage every user's clips" was
// turned off, or the admin demoted, while the page was open. The page stops where it is (its loads and its poll dropped),
// the capabilities are forgotten and read again, and the page is drawn again in "My clips" (the hash replaced, so Back
// doesn't return to the scope; the selection, the switch and the Owner select go with the old page, the new one has them
// only while the server still allows the scope), with one toast. True when the error was that (the caller then says
// nothing more); later refusals of the old page's requests say nothing.
function clipsAdminTurnedOff(page, error) {
    if (!page || !error || error.status !== 403 || page.state.scope !== 'all') {
        return false;
    }

    if (page.retired) {
        return true;
    }

    page.retired = true;
    page.generation++;
    stopClipsPolling(page);
    page.selected.clear();
    forgetCapabilities();
    toast('Admins\' access to other users\' clips is turned off now, so the page shows your own clips.', { timeout: 8000 });
    const params = writeClipsState(Object.assign({}, page.state, { scope: '', user: '', pages: 1, highlight: '' }));
    kit.pages.setParams(CLIPS_PAGE.key, params, { replace: true });
    if (page.context.isCurrent() && currentClipsPage === page) {
        const focused = page.body.contains(document.activeElement);
        page.body.replaceChildren();
        page.body.classList.remove('jfbm-clips-all-users');
        renderClipsPage(page.body, params, page.context).then(function () {
            const now = currentClipsPage;
            if (focused && now && now.body === page.body && !now.body.contains(document.activeElement)) {
                const target = pageFallbackFocus(now);
                if (target) {
                    target.focus();
                }
            }
        }, report);
    }

    return true;
}

// "Cancel" of another user's clip being made: POST /Bookmarks/Admin/Clips/{id}/Cancel; the row as the server answers it.
async function cancelAdminClipJob(page, clip) {
    let updated;
    try {
        updated = await api('POST', 'Bookmarks/Admin/Clips/' + clip.Id + '/Cancel');
    } catch (e) {
        if (clipsAdminTurnedOff(page, e)) {
            return;
        }

        if (e.status === 404) {
            clipActionRefused(page, clip, e);
        } else {
            toast(e.message);
        }

        return;
    }

    toast('Clip cancelled.');
    if (updated && updated.Id) {
        replaceClipRow(page, pageItemKey(clip.Id), updated);
    }

    await refreshClipKeys(page, [pageItemKey(clip.Id)]);
}

// "Delete" (a file that is there) or "Forget" (one that is gone) of another user's clip: a confirmation naming whose clip and
// what happens to their bookmark, Cancel first (the least destructive control); DELETE /Bookmarks/Admin/Clips/{id},
// Forget with onlyIfMissing=true (a file there after all is the server's 409, said in a toast with the row read again).
// The server's refusals stay in the dialog with the row as it was (in use, being made, outside the clip folders: 409; its
// folder offline or not recognised, its last rename not finished: 503, the row read again); a clip gone (404) closes it.
function confirmAdminClipDelete(page, clip, forget) {
    const c = clip.Clip || {};
    const key = pageItemKey(clip.Id);
    let cancel = null;
    const dialog = kit.dialog({
        title: forget ? 'Forget clip?' : 'Delete clip?',
        modal: true,
        scopedKeys: true,
        initialFocus: function () {
            return cancel;
        },
        returnFocus: clipRowReturnFocus(page, key, forget ? 'Forget clip' : 'Delete clip')
    });
    const whose = clipOwnerPossessive(clip);
    const text = forget
        ? 'Remove ' + whose + ' entry for this clip? Its file is already gone.' + (clip.BookmarkDeleted === true ? ' Their bookmark was deleted, so it disappears from their list.' : ' Their bookmark stays.')
        : clip.BookmarkDeleted === true
            ? 'Delete ' + whose + ' clip "' + isolate(clipTitle(clip)) + '"? Their bookmark was already deleted, so it disappears from their list.'
            : 'Delete ' + whose + ' clip file "' + isolate(c.FileName || clipTitle(clip)) + '"' + (typeof c.FileSize === 'number' ? ' (' + formatSize(c.FileSize) + ')' : '') + '? Their bookmark stays.';
    const error = h('div', { class: kit.cls('error') + ' jfbm-clip-dialog-error', role: 'alert' });
    const confirm = kit.button(forget ? 'Forget' : 'Delete', {
        variant: 'primary',
        className: forget ? 'jfbm-clip-forget-confirm' : 'jfbm-clip-delete-confirm',
        onClick: async function () {
            confirm.disabled = true;
            error.textContent = '';
            try {
                await api('DELETE', 'Bookmarks/Admin/Clips/' + clip.Id, undefined, forget ? { onlyIfMissing: 'true' } : undefined);
            } catch (e) {
                confirm.disabled = false;
                if (clipsAdminTurnedOff(page, e)) {
                    dialog.close();
                    return;
                }

                if (e.status === 404 || (forget && e.status === 409)) {
                    dialog.close();
                    clipActionRefused(page, clip, e);
                } else {
                    error.textContent = e.message;
                    refocusDialogButton(dialog, confirm);
                    if (e.status === 503) {
                        refreshClipKeys(page, [key]);
                    }
                }

                return;
            }

            dialog.close();
            toast(forget ? 'Clip forgotten.' : 'Clip deleted.');
            clipGone(page, clip);
        }
    });
    cancel = kit.button('Cancel', { onClick: dialog.close });
    kit.append(dialog.body, h('p', { class: 'jfbm-clip-dialog-text', text: text }), error, h('div', { class: kit.cls('actions') }, [cancel, confirm]));
    return dialog;
}

// "Dismiss" of another user's failed clip without a file: nothing of value goes, so no confirmation, as on the owner's page;
// the same route as Forget.
async function dismissAdminClip(page, clip) {
    try {
        await api('DELETE', 'Bookmarks/Admin/Clips/' + clip.Id, undefined, { onlyIfMissing: 'true' });
    } catch (e) {
        if (clipsAdminTurnedOff(page, e)) {
            return;
        }

        if (e.status === 404 || e.status === 409 || e.status === 503) {
            clipActionRefused(page, clip, e);
        } else {
            toast(e.message);
        }

        return;
    }

    toast('Failed clip cleared; the bookmark stays.');
    clipGone(page, clip);
}
