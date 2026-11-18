// Frame stepping in the player (since 1.4.0 / 2.2.0): Back one frame and Forward one frame, as two buttons in the
// player's button row while the server's ShowPlayerButtons is on (PLAYER_BUTTONS, 40-player.js) and as the , and . keys
// either way, as YouTube has them; and the Adjust bar, which moves an existing bookmark's start or end frame by frame.
//
// A step pauses a playing video first, then shows the frame before or after the one on screen. The frame's length comes
// from the playing version's video stream (RealFrameRate, else AverageFrameRate; 24000/1001 only when neither is known),
// a rate within 0.002 of an NTSC one (29.97003) taken as that exact fraction (30000/1001). The step starts from the frame
// actually shown (requestVideoFrameCallback's mediaTime, kept for every frame the browser presents; without it, the frame
// the clock is in), and lands in the middle of the target frame, never on a boundary, where rounding could show the one
// next to it; so steps never drift, however many. The <video> is seeked directly while the position is in its seekable
// range: a direct play's file, and a transcode's HLS playlist, which covers the whole video (a frame outside what is
// loaded takes as long as the server needs to transcode from there). Otherwise (a progressive transcode) the seek goes
// through the Sessions API like the rest of the plugin's seeks, and the video is paused again once it plays.
//
// Positions are ticks (10,000,000 a second), as everywhere in the plugin. The video's own clock may be offset from the
// position Jellyfin reports (a progressive transcode starts its clock at 0 where it started); the offset is the last
// playback report's position less the video's time at that report, and less than a millisecond of it is rounding (the
// web client reports whole milliseconds), so a direct play's positions are the frame grid's own.

const FRAME_RATE_FALLBACK = 24000 / 1001;
const NTSC_FRAME_RATES = [24000 / 1001, 30000 / 1001, 48000 / 1001, 60000 / 1001, 120000 / 1001];
// How long a seek may take before the step gives up: a transcode that has to start again at the frame takes seconds.
const FRAME_SEEK_TIMEOUT_MS = 20000;
// How long to wait for the browser to present the frame after a seek, for its mediaTime.
const FRAME_SHOWN_TIMEOUT_MS = 400;
// After a seek through the server, a stream started again plays: it is paused when it does, for this long.
const FRAME_REPAUSE_MS = 5000;

// A frame rate as a stream reports it, or null when it isn't one.
function normalizeFrameRate(value) {
    if (typeof value !== 'number' || !isFinite(value) || value < 1 || value > 1000) {
        return null;
    }

    for (let i = 0; i < NTSC_FRAME_RATES.length; i++) {
        if (Math.abs(value - NTSC_FRAME_RATES[i]) < 0.002) {
            return NTSC_FRAME_RATES[i];
        }
    }

    const whole = Math.round(value);
    return Math.abs(value - whole) < 0.002 ? whole : value;
}

// The frame rate of an item's video stream in the given version (its MediaSources, else the item's own MediaStreams):
// RealFrameRate, else AverageFrameRate; null when neither is known.
function streamFrameRate(item, mediaSourceId) {
    if (!item) {
        return null;
    }

    const sources = Array.isArray(item.MediaSources) ? item.MediaSources : [];
    const source = sources.filter(function (s) {
        return s && mediaSourceId && s.Id === mediaSourceId;
    })[0] || sources[0];
    const streams = source && Array.isArray(source.MediaStreams) ? source.MediaStreams
        : (Array.isArray(item.MediaStreams) ? item.MediaStreams : []);
    const video = streams.filter(function (s) {
        return s && s.Type === 'Video';
    })[0];
    if (!video) {
        return null;
    }

    return normalizeFrameRate(video.RealFrameRate) || normalizeFrameRate(video.AverageFrameRate);
}

// The frame rate of the playing version, read once per video (readItem, 36-name-dialog.js).
async function playingFrameRate(itemId, mediaSourceId) {
    const item = await readItem(itemId);
    return streamFrameRate(item, mediaSourceId) || FRAME_RATE_FALLBACK;
}

// The number of the frame a position (ticks) is in, frame 0 starting at 0. A position a hair before a frame's start, as
// floating point leaves one, counts as that frame.
function frameIndexAt(ticks, fps) {
    return Math.floor(ticks * fps / TICKS_PER_SECOND + 1e-6);
}

// The middle of a frame, in ticks.
function frameMiddleTicks(index, fps) {
    return Math.round((index + 0.5) * TICKS_PER_SECOND / fps);
}

// "+3 frames", "-1 frame", "unchanged": how far a position moved from another, in whole frames.
function frameDeltaText(fromTicks, toTicks, fps) {
    const delta = frameIndexAt(toTicks, fps) - frameIndexAt(fromTicks, fps);
    if (delta === 0) {
        return toTicks === fromTicks ? 'unchanged' : 'same frame';
    }

    return (delta > 0 ? '+' : '-') + Math.abs(delta) + (Math.abs(delta) === 1 ? ' frame' : ' frames');
}

// 1:02:03.480: a position to the millisecond.
function formatFrameTime(ticks) {
    const ms = Math.max(0, Math.round(ticks / 10000));
    return formatTime(Math.floor(ms / 1000) * TICKS_PER_SECOND) + '.' + String(ms % 1000).padStart(3, '0');
}

// The video's clock against the reported position, in ticks (see above).
function videoOffsetTicks() {
    const playback = state.playback;
    if (!playback || typeof playback.videoTime !== 'number') {
        return 0;
    }

    const offset = Math.round(playback.positionTicks - playback.videoTime * TICKS_PER_SECOND);
    return Math.abs(offset) < 10000 ? 0 : offset;
}

// The frames the browser presented, per <video>: { mediaTime } of the latest, from requestVideoFrameCallback, which every
// current browser has (none: the clock decides). A scanner (90-main.js) starts it for each video element it meets.
const presentedFrames = new WeakMap();

function watchPresentedFrames() {
    const video = findVideoElement();
    if (!video || typeof video.requestVideoFrameCallback !== 'function' || presentedFrames.has(video)) {
        return;
    }

    presentedFrames.set(video, null);
    const onFrame = function (now, metadata) {
        if (metadata && typeof metadata.mediaTime === 'number') {
            presentedFrames.set(video, { mediaTime: metadata.mediaTime });
        }

        video.requestVideoFrameCallback(onFrame);
    };
    video.requestVideoFrameCallback(onFrame);
}

// The start (the video's clock, seconds) of the frame on screen: the last presented frame's while it is the clock's
// (within a frame and a half before it), else the frame the clock is in.
function shownFrameStart(video, fps) {
    const seen = presentedFrames.get(video);
    const now = video.currentTime;
    if (seen && !video.seeking && seen.mediaTime <= now + 0.002 && now - seen.mediaTime < 1.5 / fps) {
        return seen.mediaTime;
    }

    return Math.floor(now * fps + 1e-6) / fps;
}

// The position (ticks) of the frame on screen: its middle. On the frame grid (a direct play) it is the grid's own middle,
// so it reads back as the same frame anywhere.
function shownFrameTicks(video, fps) {
    const start = shownFrameStart(video, fps);
    const offset = videoOffsetTicks();
    const index = Math.round(start * fps);
    if (Math.abs(start * fps - index) < 0.01) {
        return frameMiddleTicks(index, fps) + offset;
    }

    return Math.round((start + 0.5 / fps) * TICKS_PER_SECOND) + offset;
}

// Resolves with the name of the first of the events the target fires, or null after timeoutMs.
function waitForEvent(target, names, timeoutMs) {
    return new Promise(function (resolve) {
        let timer = null;
        const done = function (value) {
            clearTimeout(timer);
            names.forEach(function (name) {
                target.removeEventListener(name, handlers[name]);
            });
            resolve(value);
        };
        const handlers = {};
        names.forEach(function (name) {
            handlers[name] = function () {
                done(name);
            };
            target.addEventListener(name, handlers[name]);
        });
        timer = setTimeout(function () {
            done(null);
        }, timeoutMs);
    });
}

// Resolves once the browser presents a frame (the next one: ask before the seek), or after timeoutMs; at once without
// the callback.
function waitForPresentedFrame(video, timeoutMs) {
    if (typeof video.requestVideoFrameCallback !== 'function') {
        return Promise.resolve();
    }

    return new Promise(function (resolve) {
        const timer = setTimeout(resolve, timeoutMs);
        video.requestVideoFrameCallback(function (now, metadata) {
            if (metadata && typeof metadata.mediaTime === 'number') {
                presentedFrames.set(video, { mediaTime: metadata.mediaTime });
            }

            clearTimeout(timer);
            resolve();
        });
    });
}

// Whether the video can seek to a time (seconds) itself: inside one of its seekable ranges.
function canSeekVideoTo(video, seconds) {
    const ranges = video.seekable;
    if (!ranges || typeof ranges.length !== 'number') {
        return true;
    }

    for (let i = 0; i < ranges.length; i++) {
        if (seconds >= ranges.start(i) - 0.001 && seconds <= ranges.end(i) + 0.001) {
            return true;
        }
    }

    return false;
}

// Pauses the video when it plays during the next FRAME_REPAUSE_MS (a stream the server started again).
function keepPaused(video) {
    const pause = function () {
        video.pause();
    };
    video.addEventListener('playing', pause);
    setTimeout(function () {
        video.removeEventListener('playing', pause);
    }, FRAME_REPAUSE_MS);
}

// Shows the position (ticks), paused: the <video> seeked when it can, else a seek through the Sessions API. Answers false
// when the seek didn't finish in time.
async function showPosition(video, ticks) {
    const seconds = (ticks - videoOffsetTicks()) / TICKS_PER_SECOND;
    if (video && canSeekVideoTo(video, seconds)) {
        // The frame callback is asked for before the seek: a browser may present the frame before 'seeked' reaches us.
        const presented = waitForPresentedFrame(video, FRAME_SEEK_TIMEOUT_MS);
        const seeked = waitForEvent(video, ['seeked'], FRAME_SEEK_TIMEOUT_MS);
        video.currentTime = seconds;
        if (await seeked === null) {
            return false;
        }

        await Promise.race([presented, new Promise(function (resolve) {
            setTimeout(resolve, FRAME_SHOWN_TIMEOUT_MS);
        })]);
        return true;
    }

    const settled = video ? waitForEvent(video, ['seeked', 'playing', 'canplay'], FRAME_SEEK_TIMEOUT_MS) : Promise.resolve('none');
    if (video) {
        keepPaused(video);
    }

    await kit.playback.seekTo(ticks);
    const event = await settled;
    const now = findVideoElement();
    if (now && !now.paused) {
        now.pause();
    }

    if (now && now !== video) {
        keepPaused(now);
    }

    return event !== null;
}

// The steps asked for while one is under way: a press adds its step (several presses become one seek), a held key's
// repeats are dropped (holding a key steps as fast as the frames can be shown, and stops when it is let go).
const frameStepping = { busy: false, queued: 0 };

function stepFrames(direction, repeat) {
    if (frameStepping.busy) {
        if (!repeat) {
            frameStepping.queued += direction;
        }

        return Promise.resolve();
    }

    frameStepping.busy = true;
    return (async function () {
        try {
            let steps = direction;
            while (steps !== 0) {
                if (!await stepOnce(steps)) {
                    frameStepping.queued = 0;
                    break;
                }

                steps = frameStepping.queued;
                frameStepping.queued = 0;
            }
        } catch (e) {
            toast(e.message);
        } finally {
            frameStepping.busy = false;
            frameStepping.queued = 0;
            refreshAdjustBar();
        }
    })();
}

// One seek of `steps` frames from the frame on screen (pausing a playing video first). Answers false when there is
// nothing to step or the seek didn't finish.
async function stepOnce(steps) {
    watchPresentedFrames();
    const position = currentPosition();
    const video = findVideoElement();
    if (!position || !video) {
        toast('Start playing a video to step through its frames.');
        return false;
    }

    const fps = await playingFrameRate(position.itemId, position.mediaSourceId);
    if (!video.paused) {
        video.pause();
    }

    const start = shownFrameStart(video, fps);
    let target = start + (steps + 0.5) / fps;
    const duration = video.duration;
    if (typeof duration === 'number' && isFinite(duration) && duration > 0) {
        target = Math.min(target, duration - 0.5 / fps);
    }

    target = Math.max(target, Math.min(start, 0) + 0.5 / fps);
    if (Math.floor((target - start) * fps) === 0 && target >= start) {
        return false;
    }

    const shown = await showPosition(video, Math.round(target * TICKS_PER_SECOND) + videoOffsetTicks());
    if (!shown) {
        toast('That frame is taking too long to load. Try again in a moment.');
    }

    return shown;
}

function stepBack(event) {
    return stepFrames(-1, !!(event && event.repeat));
}

function stepForward(event) {
    return stepFrames(1, !!(event && event.repeat));
}

// The keys: , and . on the player, held or pressed, unless the focus is in a field or one of our dialogs or
// jellyfin-web's is open (the kit's rules). jellyfin-web's own player keys (a document bubble listener added when the
// player shows, so after ours) step by its stream's ReferenceFrameRate from wherever the clock is, and only while paused;
// ours stop the key there (stopImmediatePropagation), so a press is one step.
const FRAME_BACK_KEY = ',';
const FRAME_FORWARD_KEY = '.';

function frameKeyHandler(step) {
    return function (event) {
        event.stopImmediatePropagation();
        step(event);
    };
}

kit.shortcuts.bind(FRAME_BACK_KEY, frameKeyHandler(stepBack), { when: isPlayerOpen, repeat: true });
kit.shortcuts.bind(FRAME_FORWARD_KEY, frameKeyHandler(stepForward), { when: isPlayerOpen, repeat: true });

// ----------------------------------------------------------------------------------------------------- the Adjust bar
// The player's Bookmarks list offers Adjust on each bookmark of the playing video (35-lists.js). It closes the list,
// shows the bookmark's start, paused, and puts a bar on the player naming the bookmark, with its start (and a range's
// end) as they will be saved and how many frames each moved, the frame on screen, Back and Forward one frame (and the ,
// and . keys), Set start here, Set end here (a range only), Save and Cancel. Save goes through the same update the
// editor sends when times change (POST /Bookmarks/{id}, every field as read, with its UpdatedAt), with the editor's rule
// that the end is after the start and its clip-limit warning; the server checks the rest. A bookmark with a clip keeps
// it, as in the editor: the clip is then listed as made before the range was changed, and can be made again. Cancel,
// Escape or a remote's Back in the bar, stopping playback or another video discard the changes.
//
// The bar's keys: while the focus is in it, a window capture listener (as the kit's scopedKeys dialogs have) handles
// them and nothing after it sees them (not Jellyfin Enhanced, not jellyfin-web's player keys): Left and Right (Up and
// Down too, and a remote's D-pad) move the focus along its buttons, , and . step, Escape and Back cancel, Enter and Space
// press the button as usual.
const adjusting = { current: null };

function isAdjusting() {
    return !!adjusting.current;
}

async function startAdjust(bookmark) {
    endAdjust();
    const position = currentPosition();
    if (!position || position.itemId !== bookmark.ItemId) {
        toast('Play this bookmark\'s video to adjust it.');
        return;
    }

    const results = await Promise.all([playingFrameRate(position.itemId, position.mediaSourceId), getCapabilities()]);
    const fps = results[0];
    const capabilities = results[1];
    const session = {
        bookmark: bookmark,
        itemId: bookmark.ItemId,
        fps: fps,
        isRange: typeof bookmark.EndTicks === 'number',
        start: bookmark.StartTicks,
        end: typeof bookmark.EndTicks === 'number' ? bookmark.EndTicks : null,
        maxClipTicks: capabilities.CanCreateClips && capabilities.MaxClipDurationSeconds > 0 ? capabilities.MaxClipDurationSeconds * TICKS_PER_SECOND : 0,
        saving: false,
        video: null,
        bar: null
    };
    adjusting.current = session;
    buildAdjustBar(session);
    const video = findVideoElement();
    session.video = video;
    if (video) {
        if (!video.paused) {
            video.pause();
        }

        video.addEventListener('timeupdate', refreshAdjustBar);
        video.addEventListener('seeked', refreshAdjustBar);
    }

    const shown = await showPosition(video, frameMiddleTicks(frameIndexAt(bookmark.StartTicks, fps), fps));
    if (adjusting.current === session) {
        refreshAdjustBar();
        if (!shown) {
            session.error.textContent = 'The video is still loading the bookmark\'s start.';
        }
    }
}

function adjustButton(label, title, onClick, opts) {
    const button = kit.button(label, Object.assign({ onClick: onClick, title: title }, opts || {}));
    button.classList.add('jfbm-adjust-button');
    return button;
}

function buildAdjustBar(session) {
    const bookmark = session.bookmark;
    session.title = h('span', { class: 'jfbm-adjust-title' });
    session.startText = h('span', { class: 'jfbm-adjust-start' });
    session.endText = session.isRange ? h('span', { class: 'jfbm-adjust-end' }) : null;
    session.nowText = h('span', { class: 'jfbm-adjust-now' });
    session.hint = h('div', { class: kit.cls('hint') + ' jfbm-adjust-hint' });
    session.error = h('div', { class: kit.cls('error') + ' jfbm-adjust-error', role: 'alert' });
    session.backButton = adjustButton('', 'Back one frame (,)', function () {
        stepFrames(-1, false);
    }, { icon: 'navigate_before', className: 'jfbm-adjust-back' });
    session.forwardButton = adjustButton('', 'Forward one frame (.)', function () {
        stepFrames(1, false);
    }, { icon: 'navigate_next', className: 'jfbm-adjust-forward' });
    session.setStartButton = adjustButton('Set start here', 'Make the frame on screen the start', function () {
        setAdjustPoint(session, 'start');
    }, { className: 'jfbm-adjust-set-start' });
    session.setEndButton = session.isRange ? adjustButton('Set end here', 'Make the frame on screen the end', function () {
        setAdjustPoint(session, 'end');
    }, { className: 'jfbm-adjust-set-end' }) : null;
    session.saveButton = adjustButton('Save', 'Save the new times', function () {
        saveAdjust(session);
    }, { variant: 'primary', className: 'jfbm-adjust-save' });
    session.cancelButton = adjustButton('Cancel', 'Leave the bookmark as it was', function () {
        endAdjust();
    }, { className: 'jfbm-adjust-cancel' });
    [session.backButton, session.forwardButton].forEach(function (button) {
        button.setAttribute('aria-label', button.title);
    });

    const bar = h('div', { class: 'jfbm-adjust', role: 'group', 'aria-label': 'Adjust ' + displayTitle(bookmark), 'data-id': bookmark.Id }, [
        h('div', { class: 'jfbm-adjust-heading' }, [icon('tune'), session.title]),
        h('div', { class: 'jfbm-adjust-times' }, [session.startText, session.endText, session.nowText]),
        h('div', { class: 'jfbm-adjust-actions' }, [
            h('span', { class: 'jfbm-adjust-steps' }, [session.backButton, session.forwardButton]),
            session.setStartButton,
            session.setEndButton,
            h('span', { class: 'jfbm-adjust-spacer' }),
            session.cancelButton,
            session.saveButton
        ]),
        session.hint,
        session.error
    ]);
    bar.querySelectorAll('button').forEach(function (button) {
        kit.makeFocusable(button);
    });
    session.bar = bar;
    session.onKey = function (event) {
        onAdjustKey(session, event);
    };
    window.addEventListener('keydown', session.onKey, true);
    document.body.append(bar);
    refreshAdjustBar();
    session.forwardButton.focus();
}

function adjustButtons(session) {
    return Array.prototype.slice.call(session.bar.querySelectorAll('button')).filter(function (button) {
        return !button.disabled;
    });
}

function onAdjustKey(session, event) {
    const target = event.target;
    if (!target || target === window || !session.bar.contains(target) || event.isComposing || event.keyCode === 229) {
        return;
    }

    const plainKey = !event.ctrlKey && !event.altKey && !event.metaKey;
    const arrow = kit.shortcuts.arrowOf(event);
    if (plainKey && (event.key === FRAME_BACK_KEY || event.key === FRAME_FORWARD_KEY) && !event.shiftKey) {
        event.preventDefault();
        stepFrames(event.key === FRAME_BACK_KEY ? -1 : 1, !!event.repeat);
    } else if (event.key === 'Escape' || kit.shortcuts.isBackKey(event)) {
        event.preventDefault();
        endAdjust();
    } else if (arrow && plainKey && !event.shiftKey) {
        event.preventDefault();
        const buttons = adjustButtons(session);
        const index = buttons.indexOf(document.activeElement);
        const forward = arrow === 'ArrowRight' || arrow === 'ArrowDown';
        const next = index < 0 ? 0 : (index + (forward ? 1 : -1) + buttons.length) % buttons.length;
        if (buttons[next]) {
            buttons[next].focus();
        }
    }

    // Nothing after this listener sees the key: not Jellyfin Enhanced, not jellyfin-web's player keys.
    event.stopPropagation();
}

// The frame on screen as the new start or end, if it keeps the end after the start.
function setAdjustPoint(session, which) {
    const video = findVideoElement();
    if (!video) {
        return;
    }

    const ticks = shownFrameTicks(video, session.fps);
    session.error.textContent = '';
    if (which === 'start' && session.isRange && ticks >= session.end) {
        session.error.textContent = 'The start must be before the end.';
        return;
    }

    if (which === 'end' && ticks <= session.start) {
        session.error.textContent = 'The end must be after the start.';
        return;
    }

    session[which] = ticks;
    refreshAdjustBar();
}

function refreshAdjustBar() {
    const session = adjusting.current;
    if (!session || !session.bar) {
        return;
    }

    const bookmark = session.bookmark;
    const fps = session.fps;
    session.title.textContent = 'Adjusting "' + displayTitle(bookmark) + '"';
    session.startText.textContent = 'Start ' + formatFrameTime(session.start) + ' (' + frameDeltaText(bookmark.StartTicks, session.start, fps) + ')';
    if (session.endText) {
        session.endText.textContent = 'End ' + formatFrameTime(session.end) + ' (' + frameDeltaText(bookmark.EndTicks, session.end, fps) + ')';
    }

    const video = findVideoElement();
    if (video) {
        const now = shownFrameTicks(video, fps);
        const fromStart = frameIndexAt(now, fps) - frameIndexAt(session.start, fps);
        session.nowText.textContent = 'On screen ' + formatFrameTime(now) + (fromStart === 0 ? ', the start'
            : ', ' + Math.abs(fromStart) + (Math.abs(fromStart) === 1 ? ' frame ' : ' frames ') + (fromStart > 0 ? 'after' : 'before') + ' the start');
    }

    if (session.isRange) {
        const length = session.end - session.start;
        const tooLong = session.maxClipTicks > 0 && length > session.maxClipTicks;
        session.hint.textContent = 'Range length ' + formatTime(length, true) + (tooLong ? ', longer than the ' + formatTime(session.maxClipTicks) + ' clip limit' : '');
        session.hint.classList.toggle(kit.cls('hint-warning'), tooLong);
    }

    kit.setHidden(session.hint, !session.isRange);
    const changed = session.start !== bookmark.StartTicks || (session.isRange && session.end !== bookmark.EndTicks);
    session.saveButton.disabled = session.saving || !changed;
}

async function saveAdjust(session) {
    if (session.saving) {
        return;
    }

    const bookmark = session.bookmark;
    if (session.isRange && session.end <= session.start) {
        session.error.textContent = 'The end must be after the start.';
        return;
    }

    session.saving = true;
    session.error.textContent = '';
    refreshAdjustBar();
    let saved;
    try {
        saved = await api('POST', 'Bookmarks/' + bookmark.Id, {
            StartTicks: session.start,
            EndTicks: session.isRange ? session.end : null,
            Name: bookmark.Name || null,
            Note: bookmark.Note || null,
            IfUpdatedAt: bookmark.UpdatedAt || null
        });
    } catch (e) {
        session.saving = false;
        if (e.status === CONFLICT_STATUS) {
            await adjustAfterConflict(session, e);
        } else if (e.status === 404) {
            endAdjust();
            notifyChanged(bookmark.ItemId);
            toast('This bookmark was deleted elsewhere.');
        } else {
            session.error.textContent = e.message;
            refreshAdjustBar();
        }

        return;
    }

    session.saving = false;
    endAdjust();
    notifyChanged(saved.ItemId || bookmark.ItemId);
    toast('Bookmark saved.');
}

// A 409: the bookmark as it is now (its name and note kept by the next Save), the times set here kept, and the reason.
async function adjustAfterConflict(session, conflict) {
    let latest;
    try {
        latest = await api('GET', 'Bookmarks/' + session.bookmark.Id);
    } catch (e) {
        if (e.status === 404) {
            endAdjust();
            notifyChanged(session.bookmark.ItemId);
            toast('This bookmark was deleted elsewhere.');
        } else {
            session.error.textContent = conflict.message;
            refreshAdjustBar();
        }

        return;
    }

    if (adjusting.current !== session) {
        return;
    }

    session.bookmark = latest;
    notifyChanged(latest.ItemId);
    session.error.textContent = 'Changed elsewhere: it now ' + (typeof latest.EndTicks === 'number'
        ? 'runs from ' + formatFrameTime(latest.StartTicks) + ' to ' + formatFrameTime(latest.EndTicks)
        : 'starts at ' + formatFrameTime(latest.StartTicks)) + '. Save replaces that with the times here.';
    refreshAdjustBar();
    session.saveButton.disabled = false;
}

// Ends the Adjust bar, discarding what wasn't saved.
function endAdjust() {
    const session = adjusting.current;
    if (!session) {
        return;
    }

    adjusting.current = null;
    window.removeEventListener('keydown', session.onKey, true);
    if (session.video) {
        session.video.removeEventListener('timeupdate', refreshAdjustBar);
        session.video.removeEventListener('seeked', refreshAdjustBar);
    }

    const hadFocus = session.bar && session.bar.contains(document.activeElement);
    if (session.bar) {
        session.bar.remove();
    }

    if (hadFocus) {
        const list = findAnchor('osdButtons');
        const target = list && list.querySelector('.btnBookmarkList');
        if (target && typeof target.focus === 'function') {
            target.focus();
        }
    }
}

// A playback report (90-main.js): stopping, or another video, ends the bar.
function onAdjustReport(kind, info) {
    const session = adjusting.current;
    if (session && (kind === 'stopped' || (info && info.ItemId && info.ItemId !== session.itemId))) {
        endAdjust();
    }
}

// A scanner (90-main.js): the player closed ends the bar; the frames of a new video element are watched.
function syncFramePlayer() {
    if (isAdjusting() && !isPlayerOpen()) {
        endAdjust();
    }

    if (isPlayerOpen()) {
        watchPresentedFrames();
    }
}
