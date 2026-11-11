// The player: the playback position, the four on-screen buttons (while the server's ShowPlayerButtons is on), the
// bookmark shortcut, the quick bookmark with its name dialog or "Name it" toast, the range flow, Set end time
// and the player's bookmarks list.

function findVideoElement() {
    return findAnchor('videoElement');
}

// Open while any player view is shown. Played again and again from a page of ours (My Bookmarks' Play from here), the web
// client keeps up to three player views in its page slots, the earlier ones hidden, and the shown one is often not the
// first in the document; asking only the first said "Start playing a video" while one played.
function isPlayerOpen() {
    return findAllAnchors('playerPage').some(function (page) {
        return !kit.isHidden(page);
    });
}

// The web client reports progress on every time update (and on pause, seek, audio track change), so the latest report
// plus the video element's clock gives an accurate current position, even while transcoding where the video element's
// own time is offset.
function recordPlayback(info) {
    if (!info || !info.ItemId) {
        return;
    }

    const video = findVideoElement();
    state.playback = {
        itemId: info.ItemId,
        mediaSourceId: info.MediaSourceId || null,
        audioStreamIndex: typeof info.AudioStreamIndex === 'number' ? info.AudioStreamIndex : null,
        positionTicks: info.PositionTicks || 0,
        isPaused: !!info.IsPaused,
        rate: info.PlaybackRate || 1,
        reportedAt: Date.now(),
        videoTime: video ? video.currentTime : null
    };
}

function onPlaybackReport(kind, info) {
    if (kind !== 'stopped') {
        recordPlayback(info);
        return;
    }

    if (state.playback && info && info.ItemId === state.playback.itemId) {
        state.playback = null;
        state.rangeStart = null;
        updateRangeButtons();
    }
}

function currentPosition() {
    const playback = state.playback;
    if (!playback || !isPlayerOpen()) {
        return null;
    }

    let ticks = playback.positionTicks;
    const video = findVideoElement();
    if (video && playback.videoTime !== null) {
        const delta = video.currentTime - playback.videoTime;
        if (delta >= 0 && delta < 5) {
            ticks += delta * TICKS_PER_SECOND;
        }
    } else if (!playback.isPaused) {
        const elapsed = (Date.now() - playback.reportedAt) / 1000 * playback.rate;
        if (elapsed < 5) {
            ticks += elapsed * TICKS_PER_SECOND;
        }
    }

    return {
        itemId: playback.itemId,
        mediaSourceId: playback.mediaSourceId,
        audioStreamIndex: playback.audioStreamIndex,
        ticks: Math.max(0, Math.round(ticks))
    };
}

async function playFrom(bookmark) {
    const position = currentPosition();
    if (position && position.itemId === bookmark.ItemId) {
        await kit.playback.seekTo(bookmark.StartTicks);
        return;
    }

    await kit.playback.playItem(bookmark.ItemId, {
        startTicks: bookmark.StartTicks,
        mediaSourceId: bookmark.MediaSourceId,
        audioStreamIndex: bookmark.AudioStreamIndex
    });
}

const PLAYER_BUTTONS = [
    { className: 'btnBookmarkAdd', icon: 'bookmark_add', title: 'Bookmark this moment (Shift+B)', onClick: addBookmarkNow },
    { className: 'btnBookmarkRange', icon: 'content_cut', title: 'Mark the start of a range', onClick: toggleRange },
    { className: 'btnBookmarkEnd', icon: 'last_page', title: 'Set end time', onClick: setBookmarkEnd },
    { className: 'btnBookmarkList', icon: 'bookmarks', title: 'Bookmarks', onClick: openPlayerBookmarks }
];

// A scanner (90-main.js): the four buttons in the player's button row while the server's ShowPlayerButtons is on (the
// shortcut works either way). The capabilities decide; the first scan of a player without them asks and
// scans again once they are there (they are then cached, so the buttons are back at once on the next video).
function injectPlayerButtons() {
    const containers = findAllAnchors('osdButtons');
    if (containers.length === 0) {
        return;
    }

    const capabilities = freshCapabilities();
    if (!capabilities) {
        getCapabilities().then(kit.rescan, kit.rescan);
        return;
    }

    const show = capabilities.ShowPlayerButtons !== false;
    containers.forEach(function (container) {
        if (!show) {
            PLAYER_BUTTONS.forEach(function (definition) {
                container.querySelectorAll('.' + definition.className).forEach(function (button) {
                    button.remove();
                });
            });
            return;
        }

        if (container.querySelector('.btnBookmarkAdd')) {
            return;
        }

        const anchor = findAnchor('osdInsertBefore', container);
        PLAYER_BUTTONS.forEach(function (definition) {
            container.insertBefore(kit.osdButton(definition), anchor || null);
        });
    });
    updateRangeButtons();
}

function updateRangeButtons() {
    document.querySelectorAll('.btnBookmarkRange').forEach(function (button) {
        const active = !!state.rangeStart;
        button.classList.toggle('jfbm-active', active);
        button.title = active
            ? 'Mark the end of the range (started at ' + formatTime(state.rangeStart.ticks) + ')'
            : 'Mark the start of a range';
    });
}

// One quick bookmark at a time: a second activation while one is being made (a double click of the
// button, two quick presses of the shortcut; the kit's binding skips only a held key's repeats) is ignored, where it
// would make a second bookmark a tenth of a second after the first, with a second name dialog on top of the first. The
// flag is set as soon as the position is known and cleared once the request has settled and, with AskForName, the
// dialog is open; from then on an open dialog of ours stops the button, as it stops the shortcut.
let creatingBookmark = false;

// The quick bookmark: the shortcut or the bookmark button creates it at once, without a name, and
// playback goes on. With the user's AskForName (their own choice, else the server's default; Capabilities.AskForName)
// the name dialog opens straight away with the chapter's suggestion, which is looked up next to the create; without it
// a toast says "Bookmarked at 1:02:03" with "Name it" for 8 s, which opens the same dialog.
async function addBookmarkNow() {
    if (creatingBookmark || kit.dialog.isOpen()) {
        return;
    }

    const position = currentPosition();
    if (!position) {
        toast('Start playing a video to bookmark it.');
        return;
    }

    creatingBookmark = true;
    try {
        const capabilities = await getCapabilities();
        const ask = capabilities.AskForName === true;
        const suggestion = ask ? chapterSuggestion(position.itemId, position.ticks, capabilities) : null;
        let bookmark;
        try {
            bookmark = await api('POST', 'Bookmarks', {
                ItemId: position.itemId,
                StartTicks: position.ticks,
                MediaSourceId: position.mediaSourceId,
                AudioStreamIndex: position.audioStreamIndex
            });
        } catch (e) {
            toast(e.message);
            return;
        }

        notifyChanged(bookmark.ItemId);
        if (ask) {
            openNameDialog({ bookmark: bookmark, suggestion: await suggestion, capabilities: capabilities });
            return;
        }

        toast('Bookmarked at ' + formatTime(bookmark.StartTicks) + '.', {
            timeout: NAME_IT_TOAST_MS,
            action: {
                label: 'Name it',
                onClick: function () {
                    nameBookmark(bookmark);
                }
            }
        });
    } finally {
        creatingBookmark = false;
    }
}

function cancelRange() {
    state.rangeStart = null;
    updateRangeButtons();
}

function toggleRange() {
    const position = currentPosition();
    if (!position) {
        toast('Start playing a video to mark a range.');
        return;
    }

    const start = state.rangeStart;
    if (!start || start.itemId !== position.itemId) {
        state.rangeStart = position;
        updateRangeButtons();
        toast('Range starts at ' + formatTime(position.ticks) + '. Press the scissors again at the end.', {
            timeout: 6000,
            action: { label: 'Cancel', onClick: cancelRange }
        });
        return;
    }

    cancelRange();
    let startTicks = start.ticks;
    let endTicks = position.ticks;
    if (endTicks < startTicks) {
        const swap = startTicks;
        startTicks = endTicks;
        endTicks = swap;
    }

    if (endTicks - startTicks < TICKS_PER_SECOND / 2) {
        toast('That range is too short.');
        return;
    }

    openEditor({
        itemId: position.itemId,
        startTicks: startTicks,
        endTicks: endTicks,
        mediaSourceId: start.mediaSourceId,
        audioStreamIndex: start.audioStreamIndex
    });
}

// Set end time: ends one of the video's bookmarks at the current position, which makes a point bookmark (a quick one, or
// one imported from Jellyfin Enhanced) a range, or moves a range's end. The position is the one at the click. A dialog
// lists the video's bookmarks, those that start before it first, the nearest at the top, so the one just passed is a
// click or Enter away (Up, Down, Home and End move through them, as in the row menu); one that starts at the position or
// after it, or less than half a second before it (the range button's shortest range), is listed but can't be chosen.
// Choosing one saves it with that end, every other field as it was read, with its UpdatedAt (an update replaces
// every field), so a change made elsewhere meanwhile is a 409, after which the list is read again; the toast offers Make
// a clip while this user can make clips and the range is within the clip limit.
let settingEnd = false;

async function setBookmarkEnd() {
    if (settingEnd || kit.dialog.isOpen()) {
        return;
    }

    const position = currentPosition();
    if (!position) {
        toast('Start playing a video to set a bookmark\'s end time.');
        return;
    }

    settingEnd = true;
    let results;
    try {
        results = await Promise.all([
            api('GET', 'Bookmarks', undefined, { itemId: position.itemId }),
            getCapabilities()
        ]);
    } catch (e) {
        toast(e.message);
        return;
    } finally {
        settingEnd = false;
    }

    const bookmarks = results[0] || [];
    if (bookmarks.length === 0) {
        toast('This video has no bookmarks yet. Bookmark a moment first, then set its end time here.');
        return;
    }

    openEndChooser(position, bookmarks, results[1]);
}

// Whether a bookmark can end at the given position: at least the range button's shortest range after its start.
function canEndAt(bookmark, ticks) {
    return ticks - bookmark.StartTicks >= TICKS_PER_SECOND / 2;
}

// The chooser's order: the bookmarks that can end at the position, the nearest start first, then the others by start.
function endChoices(bookmarks, ticks) {
    const usable = bookmarks.filter(function (bookmark) {
        return canEndAt(bookmark, ticks);
    }).sort(function (a, b) {
        return b.StartTicks - a.StartTicks;
    });
    const later = bookmarks.filter(function (bookmark) {
        return !canEndAt(bookmark, ticks);
    }).sort(function (a, b) {
        return a.StartTicks - b.StartTicks;
    });
    return usable.concat(later);
}

// The line under a bookmark's title in the chooser: the range it would get, or why it can't end there.
function endChoiceDetail(bookmark, ticks, maxClipTicks) {
    if (!canEndAt(bookmark, ticks)) {
        return bookmark.StartTicks >= ticks
            ? 'Starts at ' + formatTime(bookmark.StartTicks) + ', after this moment'
            : 'Starts at ' + formatTime(bookmark.StartTicks) + ', less than half a second before this moment';
    }

    const length = ticks - bookmark.StartTicks;
    return 'From ' + formatTime(bookmark.StartTicks) + ', ' + formatTime(length) + ' long'
        + (maxClipTicks > 0 && length > maxClipTicks ? ', longer than the ' + formatTime(maxClipTicks) + ' clip limit' : '')
        + (typeof bookmark.EndTicks === 'number' ? ' (ends at ' + formatTime(bookmark.EndTicks) + ' now)' : '');
}

function openEndChooser(position, bookmarks, capabilities) {
    const endText = formatTime(position.ticks);
    const maxClipTicks = capabilities.CanCreateClips && capabilities.MaxClipDurationSeconds > 0
        ? capabilities.MaxClipDurationSeconds * TICKS_PER_SECOND
        : 0;
    const dialog = kit.dialog({
        title: 'Set end time to ' + endText,
        scopedKeys: true,
        onKey: function (event) {
            return moveMenuFocus(dialog.body, event);
        }
    });
    const menu = h('div', { class: 'jfbm-row-menu jfbm-end-menu', role: 'menu' });
    const error = h('div', { class: kit.cls('error'), role: 'alert' });
    let current = bookmarks;
    let saving = false;

    const render = function () {
        menu.replaceChildren.apply(menu, endChoices(current, position.ticks).map(function (bookmark) {
            return h('button', {
                type: 'button',
                class: 'jfbm-row-menu-item jfbm-end-item',
                role: 'menuitem',
                'data-id': bookmark.Id,
                disabled: !canEndAt(bookmark, position.ticks),
                onclick: function () {
                    choose(bookmark);
                }
            }, [
                icon('last_page'),
                h('span', { class: 'jfbm-end-item-text' }, [
                    h('span', { class: 'jfbm-end-item-title', text: displayTitle(bookmark) }),
                    h('span', { class: 'jfbm-end-item-detail', text: endChoiceDetail(bookmark, position.ticks, maxClipTicks) })
                ])
            ]);
        }));
        const first = menu.querySelector('button:not([disabled])');
        if (first) {
            first.focus();
        }
    };

    // A 409 or a 404: the video's bookmarks as they are now, with the reason; none left closes the chooser.
    const readAgain = async function (message) {
        let latest;
        try {
            latest = (await api('GET', 'Bookmarks', undefined, { itemId: position.itemId })) || [];
        } catch (e) {
            render();
            error.textContent = e.message;
            return;
        }

        notifyChanged(position.itemId);
        if (latest.length === 0) {
            dialog.close();
            toast(message);
            return;
        }

        current = latest;
        render();
        error.textContent = message;
    };

    const choose = async function (bookmark) {
        if (saving) {
            return;
        }

        saving = true;
        error.textContent = '';
        menu.querySelectorAll('button').forEach(function (button) {
            button.disabled = true;
        });
        let saved;
        try {
            saved = await api('POST', 'Bookmarks/' + bookmark.Id, {
                StartTicks: bookmark.StartTicks,
                EndTicks: position.ticks,
                Name: bookmark.Name || null,
                Note: bookmark.Note || null,
                IfUpdatedAt: bookmark.UpdatedAt || null
            });
        } catch (e) {
            saving = false;
            if (e.status === CONFLICT_STATUS) {
                await readAgain('That bookmark was changed elsewhere. The list shows it as it is now; choose again.');
            } else if (e.status === 404) {
                await readAgain('That bookmark was deleted elsewhere.');
            } else {
                render();
                error.textContent = e.message;
            }

            return;
        }

        dialog.close();
        notifyChanged(saved.ItemId || bookmark.ItemId);
        const length = position.ticks - bookmark.StartTicks;
        const canClip = capabilities.CanCreateClips === true && (maxClipTicks === 0 || length <= maxClipTicks);
        toast('"' + displayTitle(bookmark) + '" now ends at ' + endText + '.', canClip ? {
            timeout: 8000,
            action: {
                label: 'Make a clip',
                onClick: function () {
                    startClip(saved);
                }
            }
        } : undefined);
    };

    dialog.body.append(h('div', { class: kit.cls('hint') + ' jfbm-end-hint', text: 'Choose the bookmark that ends here.' }), menu, error);
    render();
}

function openPlayerBookmarks() {
    const position = currentPosition();
    if (!position) {
        toast('Start playing a video to see its bookmarks.');
        return;
    }

    const dialog = kit.dialog({ title: 'Bookmarks', scopedKeys: true });
    const container = h('div', { class: 'jfbm-list' });
    const combo = bookmarkNowCombo(freshCapabilities());
    // "All my bookmarks", the My Bookmarks page's link, and "My Clips", each once the capabilities
    // say its page is there; either closes the dialog as it navigates.
    const pageLinks = h('span', { class: 'jfbm-list-toolbar-links' });
    const list = {
        mode: 'player',
        itemId: position.itemId,
        container: container,
        emptyText: 'No bookmarks yet. Press ' + (combo ? combo + ' or ' : '') + 'the bookmark button while watching.',
        onJump: function (bookmark) {
            dialog.close();
            kit.playback.seekTo(bookmark.StartTicks).catch(function (e) {
                toast(e.message);
            });
        },
        onRendered: function () {
            if (pageLinks.firstChild) {
                return;
            }

            const pages = offeredPages(list.capabilities);
            if (pages.bookmarks) {
                pageLinks.append(kit.pages.link('bookmarks', null, 'All my bookmarks', { onclick: dialog.close }));
            }

            if (pages.clips) {
                pageLinks.append(kit.pages.link(CLIPS_PAGE.key, null, 'My Clips', { onclick: dialog.close }));
            }

            kit.upgrade(pageLinks);
        }
    };
    dialog.body.append(
        h('div', { class: 'jfbm-list-toolbar' }, [
            kit.button('New bookmark', {
                icon: 'bookmark_add',
                onClick: function () {
                    dialog.close();
                    const now = currentPosition();
                    openEditor({
                        itemId: position.itemId,
                        startTicks: now ? now.ticks : position.ticks,
                        mediaSourceId: position.mediaSourceId,
                        audioStreamIndex: position.audioStreamIndex
                    });
                }
            }),
            pageLinks
        ]),
        container);
    dialog.onClose(function () {
        state.lists.delete(list);
    });
    registerList(list);
}

// The "bookmark now" key (the kit's shortcut policy): the server's BookmarkNowShortcut
// (Capabilities.Shortcuts.BookmarkNow, Shift+B by default, because Jellyfin Enhanced's capture-phase listener takes B),
// which the user may override on this device (localStorage jfbm:shortcut:bookmarkNow, a combination or '' for
// off; the My Bookmarks page's Player options set it since 1.1.0). The kit's shortcuts module matches the whole
// combination in the document's bubble phase, skips keys already handled, key repeats, text fields and open dialogs, and
// fires once; the binding follows the capabilities as they arrive and change.
const BOOKMARK_NOW_DEFAULT = 'Shift+B';
const BOOKMARK_NOW_OVERRIDE = 'shortcut:bookmarkNow';
const bookmarkNowBinding = kit.shortcuts.bind(BOOKMARK_NOW_DEFAULT, addBookmarkNow, { when: isPlayerOpen });

// The combination in effect on this device: the user's override when they made one, else the server's.
function bookmarkNowCombo(capabilities) {
    const override = kit.storage.get(BOOKMARK_NOW_OVERRIDE, null);
    const shortcuts = capabilities && capabilities.Shortcuts;
    const server = shortcuts && typeof shortcuts.BookmarkNow === 'string' ? shortcuts.BookmarkNow : BOOKMARK_NOW_DEFAULT;
    const combo = kit.shortcuts.normalize(typeof override === 'string' ? override : server);
    return combo === null ? '' : combo;
}

// Applies the combination to the binding and the button's tooltip; called whenever the capabilities are (re)read.
function syncBookmarkNowShortcut(capabilities) {
    const combo = bookmarkNowCombo(capabilities);
    bookmarkNowBinding.update(combo);
    const title = combo ? 'Bookmark this moment (' + combo + ')' : 'Bookmark this moment';
    PLAYER_BUTTONS[0].title = title;
    document.querySelectorAll('.btnBookmarkAdd').forEach(function (button) {
        button.title = title;
    });
}

// A scanner (90-main.js): while the player is open, keeps the binding in step with the capabilities (cached by
// getCapabilities, so this costs nothing between refreshes); without them the server's default stands.
function syncPlayerShortcut() {
    if (!isPlayerOpen()) {
        return;
    }

    getCapabilities().then(syncBookmarkNowShortcut, function () {
        syncBookmarkNowShortcut(null);
    });
}
