// The player's timeline: the playing video's bookmarks as marks on the position slider, beside jellyfin-web's chapter
// marks but in a layer of our own (the slider rewrites its chapter marks whenever it draws them). A bookmark is a tick at
// its start, a range a band to its end as well; a tick's tooltip is the bookmark's title and times, and a click on it
// plays from there, as the player's Bookmarks list does, which is also the way there from the keyboard (the layer is
// hidden from screen readers). The bookmarks are read when a video starts to show and again after every change made in
// this browser (notifyChanged); the marks are placed by the runtime of the playing version, as the slider is.

// A failed read is tried again after this long, at the next scan; marks already drawn stay meanwhile.
const TIMELINE_RETRY_MS = 30 * 1000;

// The marks of the playing video: { key, itemId, mediaSourceId, bookmarks, runtimeTicks, version, reads, reading,
// failedAt }; bookmarks is null until the first read. version changes with every read that lands, across videos too,
// so a layer drawn for another video or an earlier read never looks current.
let timeline = null;
let timelineVersion = 0;

// The runtime the slider measures: the playing version's, else the video's; 0 when neither is known (live TV), and then
// there are no marks.
function runtimeTicks(item, mediaSourceId) {
    if (!item) {
        return 0;
    }

    const source = (Array.isArray(item.MediaSources) ? item.MediaSources : []).filter(function (candidate) {
        return candidate && candidate.Id === mediaSourceId;
    })[0];
    const ticks = source && source.RunTimeTicks > 0 ? source.RunTimeTicks : item.RunTimeTicks;
    return typeof ticks === 'number' && ticks > 0 ? ticks : 0;
}

// Reads the video's bookmarks and its runtime; a read that lands after a newer one began is dropped.
function readTimeline(entry) {
    const read = ++entry.reads;
    entry.reading = true;
    Promise.all([
        api('GET', 'Bookmarks', undefined, { itemId: entry.itemId }),
        readItem(entry.itemId)
    ]).then(function (results) {
        if (read === entry.reads) {
            entry.bookmarks = results[0] || [];
            entry.runtimeTicks = runtimeTicks(results[1], entry.mediaSourceId);
            entry.failedAt = 0;
            landed(entry);
        }
    }, function (e) {
        if (read === entry.reads) {
            report(e);
            entry.failedAt = Date.now();
            landed(entry);
        }
    });
}

function landed(entry) {
    entry.reading = false;
    entry.version = ++timelineVersion;
    kit.rescan();
}

// The playing video's marks, read when they are first wanted (and again after a failed read, TIMELINE_RETRY_MS later).
function timelineFor(playback) {
    const key = playback.itemId + '/' + (playback.mediaSourceId || '');
    if (!timeline || timeline.key !== key) {
        timeline = { key: key, itemId: playback.itemId, mediaSourceId: playback.mediaSourceId, bookmarks: null, runtimeTicks: 0, version: 0, reads: 0, reading: false, failedAt: 0 };
    }

    if (timeline.bookmarks === null && !timeline.reading && Date.now() - timeline.failedAt >= TIMELINE_RETRY_MS) {
        readTimeline(timeline);
    }

    return timeline;
}

// Where a position is on the slider, in percent of its width.
function timelinePercent(ticks, runtime) {
    return Math.min(100, Math.max(0, ticks / runtime * 100)).toFixed(3) + '%';
}

// A tick's tooltip: the bookmark's title with its times; an untitled one's title already says them.
function timelineTitle(bookmark) {
    if (!hasOwnTitle(bookmark)) {
        return displayTitle(bookmark);
    }

    return displayTitle(bookmark) + ' (' + formatTime(bookmark.StartTicks)
        + (typeof bookmark.EndTicks === 'number' ? '–' + formatTime(bookmark.EndTicks) : '') + ')';
}

// The marks: the ranges' bands first, so every tick is drawn over them, then the ticks by start.
function timelineMarks(entry) {
    const runtime = entry.runtimeTicks;
    const bookmarks = (entry.bookmarks || []).filter(function (bookmark) {
        return runtime > 0 && typeof bookmark.StartTicks === 'number' && bookmark.StartTicks >= 0 && bookmark.StartTicks <= runtime;
    }).sort(function (a, b) {
        return a.StartTicks - b.StartTicks;
    });
    const bands = [];
    const ticks = [];
    bookmarks.forEach(function (bookmark) {
        const isRange = typeof bookmark.EndTicks === 'number' && bookmark.EndTicks > bookmark.StartTicks;
        if (isRange) {
            const band = h('div', { class: 'jfbm-timeline-range', 'data-id': bookmark.Id });
            band.style.left = timelinePercent(bookmark.StartTicks, runtime);
            band.style.right = timelinePercent(runtime - Math.min(bookmark.EndTicks, runtime), runtime);
            bands.push(band);
        }

        const tick = h('div', {
            class: 'jfbm-timeline-mark' + (isRange ? ' jfbm-timeline-mark-range' : ''),
            'data-id': bookmark.Id,
            title: timelineTitle(bookmark),
            onclick: function (event) {
                event.preventDefault();
                event.stopPropagation();
                playFrom(bookmark).catch(function (e) {
                    toast(e.message);
                });
            }
        });
        tick.style.left = timelinePercent(bookmark.StartTicks, runtime);
        ticks.push(tick);
    });
    return bands.concat(ticks);
}

// One slider's layer, drawn again only when the marks changed, so the scans its own drawing causes change nothing.
function drawTimeline(container, entry) {
    let layer = container.querySelector(':scope > .jfbm-timeline');
    const key = entry && entry.bookmarks ? entry.key + '#' + entry.version : '';
    if (layer ? layer.getAttribute('data-key') === key : !key) {
        return;
    }

    if (!layer) {
        layer = h('div', { class: 'jfbm-timeline', 'aria-hidden': 'true' });
        container.append(layer);
    }

    layer.setAttribute('data-key', key);
    layer.replaceChildren.apply(layer, key ? timelineMarks(entry) : []);
}

// A scanner (90-main.js): the marks on the position slider of every shown player view (the web client may keep several,
// 40-player.js). Nothing is read until a shown player has a slider.
function drawTimelineMarkers() {
    const playback = state.playback;
    findAllAnchors('playerPage').forEach(function (page) {
        if (kit.isHidden(page)) {
            return;
        }

        findAllAnchors('osdPositionSlider', page).forEach(function (slider) {
            if (slider.parentNode) {
                drawTimeline(slider.parentNode, playback ? timelineFor(playback) : null);
            }
        });
    });
}

// A playback start (kit.playback.onReport, after 40-player.js has recorded it): the video's bookmarks are read afresh,
// so changes made on another device or page since it last played are there.
function onTimelineReport(kind) {
    if (kind === 'start') {
        timeline = null;
        kit.rescan();
    }
}

// A change in this browser (35-lists.js notifyChanged): the playing video's bookmarks are read again; any other video's
// marks are forgotten. A clip's progress changes no mark.
function onTimelineChange(itemId, detail) {
    if (!timeline || timeline.itemId !== itemId || (detail && detail.clipTick)) {
        return;
    }

    if (isPlayerOpen()) {
        readTimeline(timeline);
    } else {
        timeline = null;
    }
}
