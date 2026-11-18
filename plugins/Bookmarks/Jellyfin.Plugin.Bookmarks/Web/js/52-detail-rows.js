// The details page's Bookmarks and Clips rows (since 1.6.0 / 2.4.0, in place of the "My bookmarks" list): jellyfin-web's
// own row and card markup (kit.cardRow, kit.rowCard), right after its Scenes row. Each card's image is the video's frame
// at that moment: the trickplay tile, else the last chapter image before it, else the video's own artwork. Every action
// the list had is in a card's menu; "Manage" in the heading opens My Bookmarks filtered to the video.

// The most clips the Clips row asks for (the server's largest page).
const DETAIL_CLIPS_LIMIT = 200;
// The widest trickplay width a card takes when the video has several (a card is about 18em wide).
const DETAIL_TRICKPLAY_MAX_WIDTH = 480;
// The width of the chapter images and the video's artwork the cards ask for.
const DETAIL_IMAGE_MAX_WIDTH = 400;

// ------------------------------------------------------------------------------------------ the frame images

// The trickplay of one media source of the video ({ sourceId, info }), or null: that source's (a bookmark's
// MediaSourceId; a movie's own source has the item's id), else the item's own, else the first; its width the widest up
// to DETAIL_TRICKPLAY_MAX_WIDTH, else the narrowest. item.Trickplay is { <source id>: { <width>: TrickplayInfo } }.
function trickplayInfo(item, mediaSourceId) {
    const all = item && item.Trickplay;
    if (!all || typeof all !== 'object') {
        return null;
    }

    const ids = Object.keys(all);
    const wanted = [mediaSourceId, item.Id].filter(Boolean).map(pageItemKey);
    const sourceId = ids.filter(function (id) {
        return wanted.indexOf(pageItemKey(id)) >= 0;
    }).sort(function (a, b) {
        return wanted.indexOf(pageItemKey(a)) - wanted.indexOf(pageItemKey(b));
    })[0] || ids[0];
    if (!sourceId || !all[sourceId]) {
        return null;
    }

    const infos = Object.keys(all[sourceId]).map(function (width) {
        return all[sourceId][width];
    }).filter(function (info) {
        return info && info.Width > 0 && info.Height > 0 && info.TileWidth > 0 && info.TileHeight > 0 && info.ThumbnailCount > 0 && info.Interval > 0;
    }).sort(function (a, b) {
        return a.Width - b.Width;
    });
    const fitting = infos.filter(function (info) {
        return info.Width <= DETAIL_TRICKPLAY_MAX_WIDTH;
    });
    const info = fitting.length ? fitting[fitting.length - 1] : infos[0];
    return info ? { sourceId: sourceId, info: info } : null;
}

// The trickplay tile of a moment, as jellyfin-web's scrubber finds it: thumbnail floor(ms / Interval) (the last one past
// the end), on sheet floor(thumbnail / (TileWidth × TileHeight)), at column (n % TileWidth) and row (n / TileWidth) of
// it. Jellyfin draws every sheet as a full TileWidth × TileHeight grid (the last one padded with black), so the tile is
// a background TileWidth × TileHeight times the box, positioned at column / (TileWidth − 1) and row / (TileHeight − 1);
// aspect is the tile's width over its height. null without trickplay.
function trickplayFrame(item, mediaSourceId, ticks) {
    const found = trickplayInfo(item, mediaSourceId);
    const client = kit.client();
    if (!found || !client) {
        return null;
    }

    const info = found.info;
    const ms = Math.max(0, Math.floor((ticks || 0) / 10000));
    const thumbnail = Math.min(Math.floor(ms / info.Interval), info.ThumbnailCount - 1);
    const perSheet = info.TileWidth * info.TileHeight;
    const sheet = Math.floor(thumbnail / perSheet);
    const offset = thumbnail % perSheet;
    const column = offset % info.TileWidth;
    const row = Math.floor(offset / info.TileWidth);
    const rows = info.TileHeight;
    const columns = info.TileWidth;
    const percent = function (index, count) {
        return count > 1 ? Math.round(index / (count - 1) * 100000) / 1000 + '%' : '0%';
    };
    return {
        url: client.getUrl('Videos/' + pageItemKey(item.Id) + '/Trickplay/' + info.Width + '/' + sheet + '.jpg', { ApiKey: client.accessToken(), MediaSourceId: found.sourceId }),
        size: (columns * 100) + '% ' + (rows * 100) + '%',
        position: percent(column, columns) + ' ' + percent(row, rows),
        aspect: info.Width / info.Height
    };
}

// The image of the video's last chapter that starts at or before the moment, or null.
function chapterFrame(item, ticks) {
    const chapters = (item && item.Chapters) || [];
    const client = kit.client();
    let found = -1;
    for (let i = 0; i < chapters.length; i++) {
        if (chapters[i].ImageTag && (chapters[i].StartPositionTicks || 0) <= (ticks || 0)) {
            found = i;
        }
    }

    return found < 0 || !client ? null : {
        url: client.getUrl('Items/' + pageItemKey(item.Id) + '/Images/Chapter/' + found, { maxWidth: DETAIL_IMAGE_MAX_WIDTH, tag: chapters[found].ImageTag })
    };
}

// The video's own landscape artwork (its backdrop, else its thumb, else its primary image), or null.
function itemArtwork(item) {
    const client = kit.client();
    if (!item || !client) {
        return null;
    }

    const tags = item.ImageTags || {};
    const kinds = [
        { type: 'Backdrop/0', tag: (item.BackdropImageTags || [])[0] },
        { type: 'Thumb', tag: tags.Thumb },
        { type: 'Primary', tag: tags.Primary }
    ].filter(function (kind) {
        return !!kind.tag;
    });
    return kinds.length === 0 ? null : {
        url: client.getUrl('Items/' + pageItemKey(item.Id) + '/Images/' + kinds[0].type, { maxWidth: DETAIL_IMAGE_MAX_WIDTH, tag: kinds[0].tag })
    };
}

// A card's image of a moment of the video: its trickplay tile, else its chapter image, else its artwork; null for none
// (the card's own icon then).
function frameImage(item, mediaSourceId, ticks) {
    return trickplayFrame(item, mediaSourceId, ticks) || chapterFrame(item, ticks) || itemArtwork(item);
}

// ------------------------------------------------------------------------------------------ the rows

// Both rows, right after the page's Scenes row (else at the end of its content), registered as lists of the video so
// every change to its bookmarks or clips draws them again: { bookmarks, clips }.
function addDetailRows(page, item) {
    const content = findAnchor('detailContent', page);
    const scenes = findAnchor('detailScenes', page);
    const bookmarks = createBookmarksRow(item);
    const clips = createClipsRow(item);
    const parent = scenes && scenes.parentNode ? scenes.parentNode : content;
    const before = scenes && scenes.parentNode ? scenes.nextSibling : null;
    parent.insertBefore(bookmarks.container, before);
    parent.insertBefore(clips.container, before);
    registerList(bookmarks);
    registerList(clips);
    return { bookmarks: bookmarks, clips: clips };
}

// The cards drawn again only when what they show changed: a clip's progress ticks (every two seconds) would otherwise
// take the focus and the scroll position away. With the focus in the row, the card of the same key takes it back.
function setRowCards(list, cards, signature) {
    if (list.signature === signature && list.row.slider) {
        return;
    }

    list.signature = signature;
    const active = document.activeElement;
    const focusedCard = active && list.row.section.contains(active) && active.closest ? active.closest('[data-jfbm-card]') : null;
    const focusedKey = focusedCard ? focusedCard.getAttribute('data-jfbm-card') : null;
    list.row.setCards(cards);
    if (focusedKey) {
        const again = list.row.section.querySelector('[data-jfbm-card="' + cssKey(focusedKey) + '"]');
        const target = again && (again.getAttribute('role') === 'button' ? again : again.querySelector('[role="button"]'));
        if (target) {
            target.focus({ preventScroll: true });
        }
    }
}

// The heading's page links, each only while the server offers its page (Capabilities.Pages).
function setRowLinks(list, links) {
    list.row.links.replaceChildren.apply(list.row.links, links);
    kit.setHidden(list.row.links, links.length === 0);
    kit.upgrade(list.row.links);
}

// ------------------------------------------------------------------------------------------ Bookmarks

// The Bookmarks row: a list of the video (35-lists.js) drawn as cards, in time order. The class jfbm-section is the
// TV layout's jump target (50-detail.js) as the list's was.
function createBookmarksRow(item) {
    const row = kit.cardRow({ title: 'Bookmarks', className: 'jfbm-section jfbm-detail-bookmarks', attrs: { 'data-item-id': item.Id } });
    const list = {
        mode: 'details',
        itemId: item.Id,
        item: item,
        row: row,
        container: row.section,
        signature: null,
        onJump: function (bookmark) {
            playFrom(bookmark).catch(function (e) {
                toast(e.message);
            });
        },
        render: renderBookmarksRow
    };
    return list;
}

function sortedByTime(bookmarks) {
    return bookmarks.slice().sort(function (a, b) {
        return (a.StartTicks - b.StartTicks) || String(a.CreatedAt || '').localeCompare(String(b.CreatedAt || ''));
    });
}

function renderBookmarksRow(list) {
    const bookmarks = sortedByTime(list.bookmarks || []);
    const signature = JSON.stringify([offeredPages(list.capabilities), list.capabilities && list.capabilities.CanCreateClips, bookmarks.map(function (b) {
        const c = b.Clip || {};
        return [b.Id, b.UpdatedAt, b.Name, b.Note, b.StartTicks, b.EndTicks, b.Relinked, b.Detached, c.Status, c.FileName, c.IsOutdated];
    })]);
    setRowCards(list, bookmarks.map(function (bookmark) {
        return bookmarkCard(bookmark, list);
    }), signature);
    const pages = offeredPages(list.capabilities);
    setRowLinks(list, pages.bookmarks ? [kit.pages.link('bookmarks', { item: list.itemId }, 'Manage', { title: 'All of this video\'s bookmarks on My Bookmarks' })] : []);
}

// "1:02:03" for a point, "1:02:03–1:05:00" for a range.
function bookmarkTimes(bookmark) {
    return typeof bookmark.EndTicks === 'number'
        ? formatTime(bookmark.StartTicks) + '–' + formatTime(bookmark.EndTicks)
        : formatTime(bookmark.StartTicks);
}

// What a bookmark's clip is doing, as a card's line, or null: its job (queued or being made, failed) or its file being
// older than the range. A progress percentage would draw the row again every tick.
function bookmarkClipLine(bookmark) {
    const c = bookmark.Clip;
    if (!c) {
        return null;
    }

    if (c.Status === 'Queued' || c.Status === 'Running') {
        return 'Making the clip…';
    }

    if (c.Status === 'Failed') {
        return 'Clip failed';
    }

    return c.FileName && c.IsOutdated ? 'Clip older than the range' : null;
}

// One bookmark: its frame, its title and times, a name's note's first line (the whole note on hover), and what is
// to know (Relinked, its clip being made, failed or older than the range, Not in library), with a scissors in the corner
// when it has a clip file; a click plays from it, the menu has every action the list had.
function bookmarkCard(bookmark, list) {
    const title = displayTitle(bookmark);
    const times = bookmarkTimes(bookmark);
    const unavailable = unavailableReason(bookmark);
    const hasClip = !!(bookmark.Clip && bookmark.Clip.FileName);
    const relinked = bookmark.Relinked && !bookmark.Detached;
    const card = kit.rowCard({
        title: title,
        lines: [times, bookmark.Name ? noteFirstLine(bookmark.Note) : null, relinked ? h('bdi', { class: 'jfbm-card-warning', text: 'Relinked, check position', title: 'This bookmark moved to another copy of the video. Check that its position is still right.' }) : null, bookmarkClipLine(bookmark), unavailable ? h('bdi', { text: unavailable.badge, title: unavailable.title }) : null],
        image: frameImage(list.item, bookmark.MediaSourceId, bookmark.StartTicks),
        icon: 'bookmark',
        indicator: hasClip ? 'content_cut' : null,
        label: title + ', ' + times + (hasClip ? ', has a clip' : ''),
        playTitle: 'Play from here',
        moreTitle: 'More actions',
        className: 'jfbm-bookmark-card' + (relinked ? ' jfbm-bookmark-card-relinked' : ''),
        attrs: { 'data-jfbm-card': pageItemKey(bookmark.Id), title: bookmark.Note ? bookmark.Note : null },
        onPlay: function () {
            if (unavailable) {
                toast(unavailable.title);
                return;
            }

            list.onJump(bookmark);
        },
        onMenu: function () {
            openActionMenu(title, bookmarkCardActions(bookmark, list));
        }
    });
    return card;
}

// A bookmark card's menu, in the list's order: play from here; Name it (a bookmark without a name); Looks right (one
// relinked); the list's actions (make the clip, Edit, Delete: rowActions); then its clip's (Cancel while it is being
// made; Preview, Download and Delete clip for its file; View in My Clips while the server offers that page).
function bookmarkCardActions(bookmark, list) {
    const unavailable = unavailableReason(bookmark);
    const clip = bookmark.Clip;
    const items = [{ icon: 'play_arrow', label: 'Play from here', disabled: !!unavailable, run: function () {
        list.onJump(bookmark);
    } }];
    if (!bookmark.Name) {
        items.push({ icon: 'label', label: 'Name it', run: function () {
            openEditor({ bookmark: bookmark });
        } });
    }

    if (bookmark.Relinked && !bookmark.Detached) {
        items.push({ icon: 'check', label: 'Looks right', run: function () {
            confirmRelinked(bookmark);
        } });
    }

    Array.prototype.push.apply(items, rowActions(bookmark, list, null));
    if (clip && (clip.Status === 'Queued' || clip.Status === 'Running')) {
        items.push({ icon: 'cancel', label: 'Cancel the clip', run: function () {
            cancelClip(bookmark);
        } });
    }

    if (clip && clip.FileName) {
        if (/\.(mp4|mkv)$/i.test(clip.FileName)) {
            items.push({ icon: 'ondemand_video', label: 'Preview the clip', run: function () {
                openPreview(bookmark);
            } });
        }

        items.push({ icon: 'file_download', label: 'Download the clip', run: function () {
            downloadClip(bookmark);
        } });
        items.push({ icon: 'delete_sweep', label: 'Delete clip', run: function () {
            deleteClip(bookmark);
        } });
        if (offeredPages(list.capabilities).clips) {
            items.push({ icon: 'video_library', label: 'View in My Clips', run: function () {
                window.location.hash = kit.pages.href(CLIPS_PAGE.key, { highlight: pageItemKey(bookmark.Id) });
            } });
        }
    }

    return items;
}

// ------------------------------------------------------------------------------------------ Clips

// The Clips row: the viewer's own clips made from this video whose file is there (any state: one being made again
// still has its earlier file), in time order, read again on every change to the video's bookmarks but not on a clip's
// progress ticks. It is a list of the video for notifyChanged (its own refresh), and the page My Clips' actions are
// given (70-page-clips.js clipActions: a page whose detail answers for its rows).
function createClipsRow(item) {
    const row = kit.cardRow({ title: 'Clips', className: 'jfbm-detail-clips', attrs: { 'data-item-id': item.Id } });
    const list = {
        mode: 'detail-clips',
        itemId: item.Id,
        item: item,
        row: row,
        container: row.section,
        signature: null,
        clips: [],
        media: new Map(),
        pending: null,
        again: false
    };
    // The page My Clips' actions take (70-page-clips.js): capabilities, the clips shown by key, and detail, which its
    // reads, removals and focus answers turn to.
    list.page = {
        capabilities: null,
        shown: new Map(),
        notifying: false,
        detail: {
            refresh: function () {
                return refreshDetailClips(list);
            },
            focusTarget: function (key) {
                const card = row.section.querySelector('[data-jfbm-card="' + cssKey(key) + '"]') || row.section.querySelector('[data-jfbm-card]');
                return card ? (card.getAttribute('role') === 'button' ? card : card.querySelector('[role="button"]')) : null;
            }
        }
    };
    list.refresh = function (detail) {
        if (detail && detail.clipTick) {
            return Promise.resolve();
        }

        return refreshDetailClips(list);
    };
    return list;
}

// The clips read again (one read at a time; a change meanwhile reads once more after it), with their media (a library
// item, the artwork beside the file), then drawn.
function refreshDetailClips(list) {
    if (list.pending) {
        list.again = true;
        return list.pending;
    }

    list.pending = (async function () {
        do {
            list.again = false;
            try {
                await readDetailClips(list);
                renderClipsRow(list);
            } catch (e) {
                report(e);
            }
        } while (list.again);
    })().finally(function () {
        list.pending = null;
    });
    return list.pending;
}

async function readDetailClips(list) {
    const capabilities = await getCapabilities();
    list.page.capabilities = capabilities;
    list.capabilities = capabilities;
    if (!capabilities || capabilities.ClipsEnabled !== true) {
        list.clips = [];
        return;
    }

    const answer = await api('GET', 'Bookmarks/Clips', undefined, { itemId: list.itemId, limit: DETAIL_CLIPS_LIMIT, includeDeleted: 'true' });
    const clips = ((answer && answer.Items) || []).filter(function (clip) {
        return clip.Clip && clip.Clip.FileExists === true && !!clip.Clip.FileName;
    }).sort(function (a, b) {
        return (a.Clip.StartTicks - b.Clip.StartTicks) || (a.Clip.EndTicks - b.Clip.EndTicks);
    });
    // Their media read each time: a clip made again or renamed can be another library item, with other artwork.
    const media = clips.length === 0 ? [] : await api('GET', 'Bookmarks/Clips/Media', undefined, { ids: clips.map(function (clip) {
        return clip.Id;
    }).join(',') });
    list.media = new Map((media || []).map(function (m) {
        return [pageItemKey(m.Id), m];
    }));
    list.clips = clips;
    list.page.shown = new Map(clips.map(function (clip) {
        return [pageItemKey(clip.Id), clip];
    }));
}

function clipMedia(list, clip) {
    return list.media.get(pageItemKey(clip.Id)) || { LibraryItemId: null, Artwork: [] };
}

function renderClipsRow(list) {
    const clips = list.clips || [];
    const signature = JSON.stringify([offeredPages(list.capabilities), list.capabilities && list.capabilities.CanDownloadClips, list.capabilities && list.capabilities.StoreReadOnly, clips.map(function (clip) {
        const c = clip.Clip;
        const media = clipMedia(list, clip);
        return [clip.Id, clip.UpdatedAt, clip.Name, c.FileName, c.State, c.Status, c.IsOutdated, c.IsNameOutdated, c.StartTicks, c.EndTicks, media.LibraryItemId, (media.Artwork || []).map(function (a) {
            return a.Kind + a.Tag;
        })];
    })]);
    setRowCards(list, clips.map(function (clip) {
        return clipCard(list, clip);
    }), signature);
    const pages = offeredPages(list.capabilities);
    setRowLinks(list, pages.clips ? [kit.pages.link(CLIPS_PAGE.key, null, 'My Clips', { title: 'All your clips on My Clips' })] : []);
}

// A clip's image: its own artwork beside the file (a thumb, else fanart, else a poster), else the video's frame at the
// clip's start.
function clipCardImage(list, clip) {
    const artwork = clipMedia(list, clip).Artwork || [];
    const client = kit.client();
    const kinds = ['Thumb', 'Fanart', 'Poster'];
    for (let i = 0; i < kinds.length; i++) {
        const art = artwork.filter(function (a) {
            return a.Kind === kinds[i];
        })[0];
        if (art && client) {
            return { url: client.getUrl('Bookmarks/Clips/' + pageItemKey(clip.Id) + '/Artwork/' + art.Kind, { tag: art.Tag, ApiKey: client.accessToken() }) };
        }
    }

    return frameImage(list.item, clip.MediaSourceId, clip.Clip.StartTicks);
}

// "0:12 · 0:12–0:24": the clip's length and its range.
function clipTimes(clip) {
    const c = clip.Clip;
    const length = typeof c.DurationTicks === 'number' ? c.DurationTicks : c.EndTicks - c.StartTicks;
    return formatTime(length) + ' · ' + formatTime(c.StartTicks) + '–' + formatTime(c.EndTicks);
}

// One clip: its image, name, length and range, and a line when it is outdated or being made again. A click plays it.
function clipCard(list, clip) {
    const title = clipTitle(clip);
    const c = clip.Clip;
    const outdated = c.IsOutdated === true;
    return kit.rowCard({
        title: title,
        lines: [clipTimes(clip), outdated ? h('bdi', { class: 'jfbm-card-warning', text: 'Outdated: the range changed' }) : null, c.State === 'InProgress' ? 'Being made again' : null],
        image: clipCardImage(list, clip),
        icon: 'content_cut',
        indicator: outdated ? 'update' : null,
        label: title + ', ' + clipTimes(clip) + (outdated ? ', outdated' : ''),
        playTitle: 'Play the clip',
        moreTitle: 'More actions',
        className: 'jfbm-clip-card' + (outdated ? ' jfbm-clip-card-outdated' : ''),
        attrs: { 'data-jfbm-card': pageItemKey(clip.Id) },
        onPlay: function () {
            playDetailClip(list, clip);
        },
        onMenu: function () {
            openActionMenu(title, clipCardActions(list, clip));
        }
    });
}

// Playing a clip: a clip file in a library is that library's video, played by Jellyfin's own player; any other, as My
// Clips offers it: its Preview for a viewer who may download clips, else its video from the clip's start.
function playDetailClip(list, clip) {
    const media = clipMedia(list, clip);
    const capabilities = list.capabilities || {};
    if (media.LibraryItemId) {
        kit.playback.playItem(pageItemKey(media.LibraryItemId)).catch(function (e) {
            toast(e.message);
        });
    } else if (capabilities.CanDownloadClips === true && clip.Clip.State === 'Ready') {
        openPreview(clip, {
            title: clipTitle(clip),
            returnFocus: list.page.detail.focusTarget.bind(null, pageItemKey(clip.Id)),
            onUnavailable: function () {
                refreshDetailClips(list);
            }
        });
    } else if (clip.ItemAvailable === true) {
        playClipSource(clip);
    } else {
        toast('This clip can\'t be played here.');
    }
}

// A clip card's menu: Play the clip, then My Clips' own actions for it (Preview, Download, the video at the clip's start,
// Edit bookmark, Rename, Make the clip again, Cancel, Delete: clipActions), then View in My Clips while the server
// offers that page.
function clipCardActions(list, clip) {
    const items = [{ icon: 'play_arrow', label: 'Play the clip', run: function () {
        playDetailClip(list, clip);
    } }].concat(clipActions(list.page, clip));
    if (offeredPages(list.capabilities).clips) {
        items.push({ icon: 'video_library', label: 'View in My Clips', run: function () {
            window.location.hash = kit.pages.href(CLIPS_PAGE.key, { highlight: pageItemKey(clip.Id) });
        } });
    }

    return items;
}
