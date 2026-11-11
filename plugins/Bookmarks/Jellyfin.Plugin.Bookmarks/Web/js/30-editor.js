// The bookmark editor, the delete confirmation and the clip preview.

// The sentences under "Also delete its clip" when it is unticked: until the My Clips page is there
// (Capabilities.Pages.Clips, 1.2.0), a kept clip is listed nowhere, so the dialog says where it will be.
const CLIP_KEPT_UNTIL_CLIPS_PAGE = 'The clip file stays on the server. You\'ll be able to see, download and delete it on the My Clips page, which arrives in the next update.';
const CLIP_KEPT_IN_CLIPS_PAGE = 'The clip stays in My Clips.';
const CONFLICT_STATUS = 409;

// The hint under Note (since 1.2.0): what a clip file's name is made of. An admin sees
// clip paths, so the note stays out of them.
const CLIP_FILE_NAME_HINT = 'A clip\'s file name is made from the video\'s name, this bookmark\'s name and its range; the note isn\'t part of it.';

// options: { bookmark } to edit one, or { itemId, startTicks, endTicks, mediaSourceId, audioStreamIndex } for a new one;
// focus: 'end' puts the cursor in the End field instead of Name (the My Bookmarks page's scissors on a point bookmark:
// an end is needed before a clip); onClose runs when the editor closes, however it closes (the name dialog's "More…"
// resumes playback then), except when Reload closes it to open the editor again on the latest, which takes onClose with
// it (otherwise playback stayed paused under the reloaded editor). The fields are Name, Start, End, Note and the clip
// mode; Name comes first and has the focus. Under Note, while this user can make clips or the bookmark has one, a hint
// says what a clip file's name is made of (never the note, since 1.2.0). Like every Bookmarks dialog since 1.1.0, the
// editor has the kit's scopedKeys on: a key pressed inside it never reaches Jellyfin Enhanced's or jellyfin-web's
// player shortcuts, and
// Enter in a one-line field submits the form (the browser's own implicit submission; the kit leaves every key its
// default action), Escape closes.
async function openEditor(options) {
    const capabilities = await getCapabilities();
    let current = options.bookmark || null;
    const itemId = current ? current.ItemId : options.itemId;
    const dialog = kit.dialog({ title: current ? 'Edit bookmark' : 'New bookmark', modal: true, scopedKeys: true });
    let handedToReload = false;
    if (typeof options.onClose === 'function') {
        dialog.onClose(function () {
            if (!handedToReload) {
                options.onClose();
            }
        });
    }

    const initialStart = current ? current.StartTicks : (options.startTicks || 0);
    const initialEnd = current ? current.EndTicks : options.endTicks;
    const startText = formatTime(initialStart, true);
    const endText = typeof initialEnd === 'number' ? formatTime(initialEnd, true) : '';

    const nameInput = kit.input({
        value: (current && current.Name) || '',
        maxlength: String(capabilities.MaxNameLength || 100),
        autocomplete: 'off',
        placeholder: 'Optional'
    });
    const startInput = kit.input({ value: startText, autocomplete: 'off', spellcheck: 'false', placeholder: 'h:mm:ss' });
    const endInput = kit.input({ value: endText, autocomplete: 'off', spellcheck: 'false', placeholder: 'Optional' });
    const noteInput = kit.textarea({
        value: (current && current.Note) || '',
        maxlength: String(capabilities.MaxNoteLength || 500),
        rows: '3',
        placeholder: 'Optional'
    });
    const error = h('div', { class: kit.cls('error'), role: 'alert' });
    const lengthHint = h('div', { class: kit.cls('hint') });
    const clipNameHint = capabilities.CanCreateClips === true || !!(current && current.Clip)
        ? h('div', { class: kit.cls('hint') + ' jfbm-editor-clip-name-hint', text: CLIP_FILE_NAME_HINT })
        : null;

    const nowButton = function (input) {
        const position = currentPosition();
        if (!position || position.itemId !== itemId) {
            return null;
        }

        return iconButton('schedule', 'Use the current position', function () {
            const now = currentPosition();
            if (now) {
                input.value = formatTime(now.ticks, true);
                updateHint();
            }
        });
    };

    // Keep the stored value when the text wasn't edited, so re-saving doesn't round it.
    const resolve = function (input, originalText, originalTicks) {
        return input.value.trim() === originalText ? originalTicks : parseTime(input.value);
    };

    // A "Not in library" or "Unavailable" bookmark gets no Save & make clip, as its row gets no Make clip: the server
    // would refuse a clip of a video that isn't there or that the user can't open, after the save.
    const canClip = capabilities.CanCreateClips && !(current && unavailableReason(current));
    const maxClipTicks = canClip && capabilities.MaxClipDurationSeconds > 0
        ? capabilities.MaxClipDurationSeconds * TICKS_PER_SECOND
        : 0;
    const updateHint = function () {
        const start = resolve(startInput, startText, initialStart);
        const end = resolve(endInput, endText, typeof initialEnd === 'number' ? initialEnd : null);
        const valid = typeof start === 'number' && typeof end === 'number' && !isNaN(start) && !isNaN(end) && end > start;
        const tooLong = valid && maxClipTicks > 0 && end - start > maxClipTicks;
        lengthHint.textContent = valid
            ? 'Range length ' + formatTime(end - start, true) + (tooLong ? ', longer than the ' + formatTime(maxClipTicks) + ' clip limit' : '')
            : '';
        lengthHint.classList.toggle(kit.cls('hint-warning'), tooLong);
    };
    startInput.addEventListener('input', updateHint);
    endInput.addEventListener('input', updateHint);
    updateHint();

    const modeSelect = kit.select({ title: 'Clip mode', 'aria-label': 'Clip mode' }, [
        { value: 'Accurate', text: 'Accurate (MP4)' },
        { value: 'Fast', text: 'Fast (copy, MKV)' }
    ]);
    modeSelect.value = capabilities.DefaultClipMode === 'Fast' ? 'Fast' : 'Accurate';

    const buttons = [];
    const setBusy = function (busy) {
        buttons.forEach(function (button) {
            button.disabled = busy;
        });
    };

    // "Changed elsewhere. Reload to see the latest.": Reload opens the editor again on the bookmark as the
    // server has it now, which drops this editor's changes, and hands it this editor's onClose; a bookmark deleted
    // meanwhile closes it for good (onClose runs then).
    const reload = async function () {
        let latest;
        try {
            latest = await api('GET', 'Bookmarks/' + current.Id);
        } catch (e) {
            if (e.status === 404) {
                dialog.close();
                notifyChanged(itemId);
                toast('This bookmark was deleted elsewhere.');
            } else {
                error.textContent = e.message;
            }

            return;
        }

        handedToReload = true;
        dialog.close();
        notifyChanged(latest.ItemId);
        openEditor({ bookmark: latest, onClose: options.onClose });
    };
    const reloadButton = linkButton('Reload', reload, { class: kit.cls('link') + ' jfbm-reload' });
    kit.setHidden(reloadButton, true);
    const showError = function (message, conflict) {
        error.textContent = message;
        kit.setHidden(reloadButton, !conflict);
    };

    const save = async function (makeClip) {
        showError('', false);
        const start = resolve(startInput, startText, initialStart);
        const end = resolve(endInput, endText, typeof initialEnd === 'number' ? initialEnd : null);
        if (start === null || isNaN(start)) {
            showError('Enter the start as h:mm:ss, for example 1:02:03.5.');
            startInput.focus();
            return;
        }

        if (typeof end === 'number' && isNaN(end)) {
            showError('Enter the end as h:mm:ss, or leave it empty.');
            endInput.focus();
            return;
        }

        if (end !== null && end <= start) {
            showError('The end must be after the start.');
            endInput.focus();
            return;
        }

        if (makeClip && end === null) {
            showError('Set an end time to make a clip.');
            endInput.focus();
            return;
        }

        setBusy(true);
        const name = nameInput.value.trim() || null;
        const note = noteInput.value.trim() || null;
        const created = !current;
        try {
            // An update replaces every field, the name included; with the UpdatedAt it was read with, a bookmark
            // changed elsewhere meanwhile is a 409 ("Changed elsewhere. Reload to see the latest.") instead of losing that
            // change. The server normalises the name (one line, NFC, at most 100 characters).
            current = current
                ? await api('POST', 'Bookmarks/' + current.Id, {
                    StartTicks: start,
                    EndTicks: end,
                    Name: name,
                    Note: note,
                    IfUpdatedAt: current.UpdatedAt || null
                })
                : await api('POST', 'Bookmarks', {
                    ItemId: itemId,
                    StartTicks: start,
                    EndTicks: end,
                    Name: name,
                    Note: note,
                    MediaSourceId: options.mediaSourceId || null,
                    AudioStreamIndex: typeof options.audioStreamIndex === 'number' ? options.audioStreamIndex : null
                });
        } catch (e) {
            showError(e.message, !created && e.status === CONFLICT_STATUS);
            setBusy(false);
            return;
        }

        notifyChanged(current.ItemId);
        try {
            if (makeClip) {
                current = await api('POST', 'Bookmarks/' + current.Id + '/Clip', { Mode: modeSelect.value });
                watchClip(current);
                toast('Making the clip. You can keep watching.');
            } else {
                toast(created ? 'Bookmark added at ' + formatTime(current.StartTicks) + '.' : 'Bookmark saved.');
            }

            dialog.close();
        } catch (e) {
            // The bookmark is saved (only the clip was refused); later attempts update it instead of creating another one.
            showError(e.message, false);
            setBusy(false);
        }
    };

    const deleteButton = current ? kit.button('Delete', {
        variant: 'danger',
        onClick: function () {
            dialog.close();
            confirmDelete(current);
        }
    }) : null;
    const cancelButton = kit.button('Cancel', { onClick: dialog.close });
    const saveButton = kit.button('Save', { variant: canClip ? null : 'primary', type: 'submit' });
    const clipButton = canClip ? kit.button('Save & make clip', {
        variant: 'primary',
        onClick: function () {
            save(true);
        }
    }) : null;
    [deleteButton, cancelButton, saveButton, clipButton].forEach(function (button) {
        if (button) {
            buttons.push(button);
        }
    });

    const form = h('form', {
        class: 'jfbm-form',
        onsubmit: function (event) {
            event.preventDefault();
            save(false);
        }
    }, [
        kit.field('Name', nameInput),
        kit.field('Start', startInput, nowButton(startInput)),
        kit.field('End', endInput, [nowButton(endInput), iconButton('clear', 'Clear the end', function () {
            endInput.value = '';
            updateHint();
        })]),
        lengthHint,
        kit.field('Note', noteInput),
        clipNameHint,
        h('div', { class: 'jfbm-error-row' }, [error, reloadButton]),
        h('div', { class: kit.cls('actions') }, [
            deleteButton,
            cancelButton,
            saveButton,
            canClip ? modeSelect : null,
            clipButton
        ])
    ]);
    dialog.body.append(form);
    (options.focus === 'end' ? endInput : nameInput).focus();
}

// "Also delete its clip": ticked while no page lists kept clips (Capabilities.Pages.Clips false, as in
// 1.1.0), unticked once My Clips is there; unticked, a hint says where the clip stays. { node, checked() }.
function clipDeleteChoice(capabilities, label) {
    const clipsPage = offeredPages(capabilities).clips;
    const box = h('input', { type: 'checkbox', checked: !clipsPage });
    const hint = h('div', { class: 'jfbm-delete-hint', role: 'status', text: clipsPage ? CLIP_KEPT_IN_CLIPS_PAGE : CLIP_KEPT_UNTIL_CLIPS_PAGE });
    const update = function () {
        kit.setHidden(hint, box.checked);
    };
    box.addEventListener('change', update);
    update();
    return {
        node: h('div', { class: 'jfbm-delete-clip' }, [h('label', { class: kit.cls('checkbox') }, [box, h('span', { text: label })]), hint]),
        checked: function () {
            return box.checked;
        }
    };
}

async function confirmDelete(bookmark) {
    const capabilities = await getCapabilities();
    // The focus starts on Cancel: a delete can't be undone, so the least destructive control.
    let cancel = null;
    const dialog = kit.dialog({
        title: 'Delete bookmark?',
        modal: true,
        scopedKeys: true,
        initialFocus: function () {
            return cancel;
        }
    });
    const hasClipFile = !!(bookmark.Clip && bookmark.Clip.FileName);
    const choice = hasClipFile ? clipDeleteChoice(capabilities, 'Also delete its clip (' + bookmark.Clip.FileName + ')') : null;
    const error = h('div', { class: kit.cls('error'), role: 'alert' });
    const title = displayTitle(bookmark);
    const description = hasOwnTitle(bookmark)
        ? '"' + title + '" at ' + formatTime(bookmark.StartTicks) + ' will be deleted.'
        : title + ' will be deleted.';
    // Without the box (no clip file when the dialog opened) the delete still sends the box's default, so a clip that
    // finishes while the dialog is open goes with the bookmark in 1.1.0 instead of staying in a
    // tombstone that nothing lists before My Clips; from 1.2.0 it stays, listed there.
    const alsoDeleteClip = function () {
        return choice ? choice.checked() : !offeredPages(capabilities).clips;
    };
    // kit.append skips the missing checkbox; 1.0's append(null) showed the text "null" here.
    cancel = kit.button('Cancel', { onClick: dialog.close });
    kit.append(dialog.body,
        h('p', { text: description }),
        choice ? choice.node : null,
        error,
        h('div', { class: kit.cls('actions') }, [
            cancel,
            kit.button('Delete', {
                variant: 'primary',
                onClick: async function () {
                    try {
                        await api('DELETE', 'Bookmarks/' + bookmark.Id, undefined, { deleteClip: alsoDeleteClip() ? 'true' : 'false' });
                        dialog.close();
                        toast('Bookmark deleted.');
                        notifyChanged(bookmark.ItemId);
                    } catch (e) {
                        error.textContent = e.message;
                    }
                }
            })
        ]));
}

// The preview's sentences: as measured, Chrome, Edge and Firefox play Matroska, so what fails is
// a codec (HEVC without hardware decoding, here headless Firefox), which the <video> reports with its error event; a sound
// format the browser lacks (AC-3 in Chrome and Firefox) plays silently, without one. Since 1.2.0 a failure that isn't
// the browser's own (the clip deleted in another tab, its folder offline, the session expired: the file route's 404, 503 or
// 401) says that instead, without a Download that would fail too.
const CLIP_PREVIEW_FAST_FAILED = 'Your browser can\'t play this clip: a Fast clip keeps the video\'s own format, and this browser doesn\'t play it. Download it instead.';
const CLIP_PREVIEW_FAILED = 'This clip couldn\'t be played here. Download it instead.';
const CLIP_PREVIEW_NO_SOUND = 'No sound? A Fast clip keeps the video\'s own sound format, which some browsers don\'t play: download it to hear it.';
const CLIP_PREVIEW_GONE = 'This clip isn\'t available any more.';
const CLIP_PREVIEW_UNREACHABLE = 'The server could not be reached.';
const CLIP_PREVIEW_SIGNED_OUT = 'Your session expired. Sign in again.';
// HTMLMediaElement's MediaError.code for a stream the browser got but couldn't decode (a codec it lacks).
const MEDIA_ERR_DECODE = 3;

// Whether the clip file route serves a preview's URL: its first byte asked for (Range: bytes=0-0) with the
// same URL and token the <video> used, so the answer is the one the video got. Answers null when it is served (2xx: the
// browser itself can't play the file), else { status, message }: the server's sentence (a ProblemDetails' detail), or ours.
async function probeClipFile(url) {
    let response;
    try {
        response = await fetch(url, { headers: { Range: 'bytes=0-0' } });
    } catch (e) {
        return { status: 0, message: CLIP_PREVIEW_UNREACHABLE };
    }

    if (response.ok) {
        return null;
    }

    let problem = null;
    try {
        problem = JSON.parse(await response.text());
    } catch (e) {
        problem = null;
    }

    const said = problem && typeof problem === 'object' ? problem.detail || problem.title : null;
    const message = response.status === 404 ? CLIP_PREVIEW_GONE
        : response.status === 401 ? CLIP_PREVIEW_SIGNED_OUT
            : said || CLIP_PREVIEW_GONE;
    return { status: response.status, message: message };
}

// A clip file in our dialog (a bookmark's, a kept one's, or a My Clips row's: anything with Id and Clip.FileName): a plain
// <video> of GET /Bookmarks/{id}/Clip/File?download=false with the token in the URL, as Jellyfin's own media URLs carry it,
// an MP4 and since 1.2 an MKV alike, with Download and Close under it; the focus starts on Close. When the browser can't
// play it (the video's error event: a decode error, or any other while the file route still serves the file) the
// picture gives way to a sentence, and the focus goes to Download; when the route refuses it (the page open in two
// tabs: deleted elsewhere, its folder offline, the session expired), its own sentence without Download, and the clip's rows are read again (options.onUnavailable, else notifyChanged for the video). A Fast clip's
// preview also says that a silent clip is a sound format the browser lacks. Closing stops the video and lets the file go.
// options.title names the dialog (the clip's file name otherwise); options.returnFocus is the kit dialog's (where the focus
// goes on close when the button that opened it went meanwhile); options.admin serves another user's clip through the admins'
// route (My Clips' "All users" scope).
function openPreview(record, options) {
    const clip = record.Clip || {};
    const fast = /\.mkv$/i.test(clip.FileName || '');
    const admin = !!(options && options.admin === true);
    let close = null;
    const dialog = kit.dialog({
        title: (options && options.title) || clip.FileName || 'Clip',
        scopedKeys: true,
        initialFocus: function () {
            return close;
        },
        returnFocus: options && options.returnFocus
    });
    let closed = false;
    let failed = false;
    const url = clipFileUrl(record.Id, false, admin);
    const video = h('video', { controls: true, autoplay: true, preload: 'auto', src: url });
    const frame = h('div', { class: 'jfbm-preview' }, video);
    const message = h('p', { class: 'jfbm-preview-message', role: 'alert' });
    kit.setHidden(message, true);
    const hint = fast ? h('p', { class: kit.cls('hint') + ' jfbm-preview-hint', text: CLIP_PREVIEW_NO_SOUND }) : null;
    const download = h('a', { class: kit.cls('link') + ' jfbm-preview-download', href: clipFileUrl(record.Id, true, admin), download: clip.FileName || '', text: 'Download' });
    const fail = function (text, offerDownload) {
        frame.remove();
        if (hint) {
            hint.remove();
        }

        message.textContent = text;
        kit.setHidden(message, false);
        if (offerDownload) {
            download.focus();
        } else {
            download.remove();
            close.focus();
        }
    };
    video.addEventListener('error', async function () {
        // Letting the file go on close (no src) is no failure; one error is enough.
        if (closed || failed) {
            return;
        }

        failed = true;
        const code = video.error && typeof video.error.code === 'number' ? video.error.code : 0;
        const refused = code === MEDIA_ERR_DECODE ? null : await probeClipFile(url);
        if (closed) {
            return;
        }

        if (!refused) {
            fail(fast ? CLIP_PREVIEW_FAST_FAILED : CLIP_PREVIEW_FAILED, true);
            return;
        }

        fail(refused.message, false);
        if (refused.status > 0) {
            if (options && typeof options.onUnavailable === 'function') {
                options.onUnavailable(refused);
            } else if (record.ItemId) {
                notifyChanged(record.ItemId);
            }
        }
    });
    close = kit.button('Close', { onClick: dialog.close });
    kit.append(dialog.body, frame, message, hint, h('div', { class: kit.cls('actions') }, [download, close]));
    dialog.onClose(function () {
        closed = true;
        if (typeof video.pause === 'function') {
            video.pause();
        }

        video.removeAttribute('src');
        if (typeof video.load === 'function') {
            video.load();
        }
    });
    return dialog;
}
