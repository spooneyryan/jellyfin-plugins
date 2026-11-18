// The player's frame stepping and the Adjust bar (Bookmarks 1.4.0 / 2.2.0) on the built ClientScript, in the kit's fake
// page: the frame length from the playing version's video stream, the step from the frame on screen to the middle of the
// next, several presses and a held key, the keys' rules, the server's seek where the video can't seek, and Adjust from
// the player's Bookmarks list to the saved times. ClientScriptTests runs it:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfbm node --test plugins/Bookmarks/tests/Jellyfin.Plugin.Bookmarks.Tests/Web/frames.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage, plain } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');

const CAPABILITIES = {
    ClipsEnabled: true, CanCreateClips: true, MaxClipDurationSeconds: 600, DefaultClipMode: 'Accurate', IsAdmin: false, StoreReadOnly: false,
    MaxNameLength: 100, MaxNoteLength: 500, Shortcuts: { BookmarkNow: 'Shift+B' }, ReservedShortcuts: [], ShowPlayerButtons: true,
    AskForName: false, AskForNameIsDefault: true, PauseWhileNaming: false, SuggestChapterNames: true, Pages: { Bookmarks: false, Clips: false }
};

// A video item whose one version has a video stream with these fields (RealFrameRate, AverageFrameRate).
function movie(id, stream) {
    return {
        Id: id, Name: 'Frame Counter', Type: 'Movie', MediaType: 'Video', IsFolder: false, LocationType: 'FileSystem', Chapters: [],
        MediaSources: [{ Id: 'ms-' + id, MediaStreams: [{ Type: 'Audio', Index: 1 }, Object.assign({ Type: 'Video', Index: 0 }, stream)] }]
    };
}

function bookmarks() {
    return [
        { Id: 'b1', ItemId: 'item1', StartTicks: 150000000, EndTicks: null, Name: 'Point', Note: null, UpdatedAt: '2026-11-01T08:00:00.0000000Z', Clip: null },
        { Id: 'b2', ItemId: 'item1', StartTicks: 300000000, EndTicks: 400000000, Name: 'Range', Note: 'its note', UpdatedAt: '2026-11-01T08:00:00.0000000Z', Clip: { Status: 'Completed', FileName: 'clip.mp4', FileSize: 2048, IsOutdated: false } }
    ];
}

// A <video> as far as frame stepping reads it: currentTime, paused, seeking, duration, seekable, pause(), and the
// frames it shows. Each frame starts at a multiple of 1/fileFps after `grid` seconds; a seek presents the frame its time
// is in 20 ms later ('seeked', then the requestVideoFrameCallback callbacks with that frame's start as mediaTime).
// options: time, fileFps (25), grid (0), duration (60), seekable (true; false for none), rvfc (true), paused (false).
function fakeVideo(page, options) {
    const o = Object.assign({ time: 0, fileFps: 25, grid: 0, duration: 60, seekable: true, rvfc: true, paused: false }, options || {});
    const video = page.element('video');
    let time = o.time;
    const callbacks = [];
    video.seeks = [];
    video.paused = o.paused;
    video.seeking = false;
    video.duration = o.duration;
    video.seekable = o.seekable ? { length: 1, start: () => 0, end: () => o.duration } : { length: 0, start: () => 0, end: () => 0 };
    video.pause = () => {
        if (!video.paused) {
            video.paused = true;
            page.dispatch(video, 'pause', { bubbles: false });
        }
    };
    video.frameStart = (t) => o.grid + Math.floor((t - o.grid) * o.fileFps + 1e-9) / o.fileFps;
    video.present = () => {
        const mediaTime = video.frameStart(time);
        callbacks.splice(0).forEach((cb) => cb(page.clock.now, { mediaTime }));
    };
    video.shownFrame = () => Math.round((video.frameStart(time) - o.grid) * o.fileFps);
    Object.defineProperty(video, 'currentTime', {
        get: () => time,
        set: (value) => {
            time = value;
            video.seeks.push(value);
            video.seeking = true;
            page.context.setTimeout(() => {
                video.seeking = false;
                page.dispatch(video, 'seeked', { bubbles: false });
                video.present();
            }, 20);
        }
    });
    if (o.rvfc) {
        video.requestVideoFrameCallback = (cb) => {
            callbacks.push(cb);
            return callbacks.length;
        };
    }

    return video;
}

// The page with the player open on item1 (or options.itemId) playing at options.time, its item's stream as given.
async function player(options) {
    options = options || {};
    const itemId = options.itemId || 'item1';
    const page = createPage();
    page.respond('GET', '/Bookmarks/Capabilities', Object.assign({}, CAPABILITIES, options.capabilities || {}));
    page.respond('GET', '/Bookmarks', options.list || bookmarks());
    page.respond('GET', '/Sessions', [{ Id: 's1', DeviceId: 'device-1' }]);
    page.respond('POST', '/Sessions/s1/Playing/Seek', null, 204);
    page.run(source, file);
    await page.advance(50);
    page.installApiClient({ userId: 'user-1', items: { [itemId]: movie(itemId, options.stream === undefined ? { RealFrameRate: 25, AverageFrameRate: 25 } : options.stream) } });
    await page.advance(50);
    const buttons = page.element('div', { class: 'buttons' }, [page.element('button', { class: 'btnSubtitles' })]);
    const osd = page.element('div', { id: 'videoOsdPage', class: 'page' }, [page.element('div', { class: 'videoOsdBottom' }, [buttons])]);
    const video = fakeVideo(page, options.video);
    page.document.body.append(page.element('div', { class: 'videoPlayerContainer' }, [video]), osd);
    await page.advance(100);
    video.present();
    const time = video.currentTime;
    await page.context.ApiClient.reportPlaybackStart({
        ItemId: itemId,
        PositionTicks: options.positionTicks === undefined ? Math.round(time * 10000000) : options.positionTicks,
        MediaSourceId: 'ms-' + itemId,
        IsPaused: video.paused,
        PlayMethod: 'DirectPlay'
    });
    await page.advance(400);
    return { page, video, buttons, osd };
}

function near(actual, expected, message) {
    assert.ok(Math.abs(actual - expected) < 1e-6, (message || '') + ': ' + actual + ' is not ' + expected);
}

function toasts(page) {
    return page.document.querySelectorAll('.jfbm-toast').map((t) => t.textContent);
}

function bar(page) {
    return page.document.querySelector('.jfbm-adjust');
}

test('. and , pause a playing video and show the next or the previous frame, landing in its middle; the stream\'s RealFrameRate sets the length', { skip }, async () => {
    // 4.1 s at 25 fps is inside frame 102 (4.08-4.12).
    const { page, video } = await player({ video: { time: 4.1 } });
    assert.equal(video.shownFrame(), 102);
    assert.equal(page.key(page.document.body, '.'), false, 'handled');
    await page.advance(100);
    assert.equal(video.paused, true, 'paused first');
    assert.equal(video.seeks.length, 1);
    near(video.currentTime, 4.14, 'the middle of frame 103');
    assert.equal(video.shownFrame(), 103);

    page.key(page.document.body, ',');
    await page.advance(100);
    near(video.currentTime, 4.10, 'back to the middle of frame 102');
    assert.equal(video.shownFrame(), 102);

    // A hundred steps forward, one at a time: still exactly a hundred frames on, in the middle of the frame (no drift).
    for (let i = 0; i < 100; i++) {
        page.key(page.document.body, '.');
        await page.advance(60);
    }

    assert.equal(video.shownFrame(), 202);
    near(video.currentTime, 202.5 / 25, 'the middle of frame 202');
    assert.equal(page.fetchLog.filter((f) => f.path.indexOf('/Sessions/s1/Playing') === 0).length, 0, 'the video seeks itself, no server seek');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the frame length: AverageFrameRate without RealFrameRate, an NTSC rate as its exact fraction, 24000/1001 when the stream says nothing', { skip }, async () => {
    const cases = [
        { stream: { AverageFrameRate: 50 }, fps: 50 },
        { stream: { RealFrameRate: 29.97003, AverageFrameRate: 29.97003 }, fps: 30000 / 1001 },
        { stream: { RealFrameRate: 23.976025 }, fps: 24000 / 1001 },
        { stream: { RealFrameRate: 0, AverageFrameRate: null }, fps: 24000 / 1001 },
        { stream: {}, fps: 24000 / 1001 }
    ];
    for (const c of cases) {
        // No requestVideoFrameCallback: the frame the clock is in is the one shown.
        const { page, video } = await player({ stream: c.stream, video: { time: 10, fileFps: c.fps, rvfc: false, paused: true } });
        const frame = Math.floor(10 * c.fps + 1e-6);
        page.key(page.document.body, '.');
        await page.advance(100);
        near(video.currentTime, (frame + 1.5) / c.fps, JSON.stringify(c.stream));
        assert.equal(video.shownFrame(), frame + 1, JSON.stringify(c.stream));
    }
});

test('the step starts from the frame on screen (its mediaTime), not from the clock, which may be ahead of it after a pause', { skip }, async () => {
    // A transcode's frames start 4 ms after the 25 fps grid. The clock (4.082) is in the grid's frame 102, but the frame
    // on screen is the stream's frame 101 (4.044-4.084): stepping from the clock's frame would skip one.
    const { page, video } = await player({ video: { time: 4.082, grid: 0.004 } });
    assert.equal(video.shownFrame(), 101);
    page.key(page.document.body, '.');
    await page.advance(100);
    near(video.currentTime, 4.044 + 1.5 / 25, 'the middle of the next frame on its own grid');
    assert.equal(video.shownFrame(), 102);
    page.key(page.document.body, ',');
    await page.advance(100);
    page.key(page.document.body, ',');
    await page.advance(100);
    assert.equal(video.shownFrame(), 100);
});

test('presses during a step add up into one more seek; a held key\'s repeats during a step are dropped, and step when it is done', { skip }, async () => {
    const { page, video } = await player({ video: { time: 4.1, paused: true } });
    page.key(page.document.body, '.');
    page.key(page.document.body, '.');
    page.key(page.document.body, '.');
    await page.advance(200);
    assert.equal(video.seeks.length, 2, 'the first press, then the two that came during it in one seek');
    assert.equal(video.shownFrame(), 105);

    page.key(page.document.body, ',');
    page.key(page.document.body, ',', { repeat: true });
    page.key(page.document.body, ',', { repeat: true });
    await page.advance(200);
    assert.equal(video.shownFrame(), 104, 'the repeats during the step were dropped');
    assert.equal(page.key(page.document.body, ',', { repeat: true }), false, 'a repeat when idle is handled');
    await page.advance(200);
    assert.equal(video.shownFrame(), 103);
});

test('the buttons step too; nothing to step before the first frame or after the last', { skip }, async () => {
    const { page, video, buttons } = await player({ video: { time: 0.01, paused: true, duration: 2 } });
    const back = buttons.querySelector('.btnFrameBack');
    const forward = buttons.querySelector('.btnFrameForward');
    assert.equal(back.getAttribute('aria-label'), 'Back one frame (,)');
    back.click();
    await page.advance(100);
    assert.equal(video.seeks.length, 0, 'frame 0 has no frame before it');
    forward.click();
    await page.advance(100);
    assert.equal(video.shownFrame(), 1);

    video.currentTime = 1.99;
    await page.advance(100);
    const seeks = video.seeks.length;
    page.key(page.document.body, '.');
    await page.advance(100);
    assert.equal(video.seeks.length, seeks, 'the last frame (49) has none after it');
    assert.equal(video.shownFrame(), 49);
});

test('the keys keep the kit\'s rules (fields, dialogs, off the player, modifiers) and, when ours, never reach jellyfin-web\'s own , and .', { skip }, async () => {
    const { page, video, osd } = await player({ video: { time: 4.1, paused: true } });
    const seenByWeb = [];
    // jellyfin-web's player keys: a document bubble listener added when the player shows, after ours.
    page.document.addEventListener('keydown', (event) => seenByWeb.push(event.key));
    page.key(page.document.body, '.');
    await page.advance(100);
    assert.deepEqual(seenByWeb, [], 'ours stopped it');
    assert.equal(video.seeks.length, 1);

    const input = page.element('input');
    page.document.body.append(input);
    assert.equal(page.key(input, '.'), true, 'typed in a field');
    assert.equal(page.key(page.document.body, '.', { ctrlKey: true }), true, 'with a modifier');
    page.document.querySelector('.btnBookmarkList').click();
    await page.advance(50);
    assert.ok(page.document.querySelector('.jfbm-backdrop'), 'the list is open');
    page.key(page.document.querySelector('.jfbm-panel'), '.');
    page.key(page.document.querySelector('.jfbm-panel'), 'Escape');
    osd.classList.add('hide');
    assert.equal(page.key(page.document.body, '.'), true, 'off the player');
    await page.advance(100);
    assert.equal(video.seeks.length, 1, 'none of those stepped');
    assert.deepEqual(seenByWeb, ['.', '.', '.'], 'jellyfin-web had them');
});

test('a video that can\'t seek there itself (a progressive transcode) goes through the Sessions API, and is paused again when it plays', { skip }, async () => {
    const { page, video } = await player({ video: { time: 4.1, seekable: false } });
    page.key(page.document.body, '.');
    await page.advance(10);
    const seek = page.fetchLog.find((f) => f.path === '/Sessions/s1/Playing/Seek');
    assert.deepEqual(seek.query, { SeekPositionTicks: '41400000' }, 'the middle of frame 103 in ticks');
    video.paused = false;
    page.dispatch(video, 'playing', { bubbles: false });
    assert.equal(video.paused, true, 'paused when the new stream played');
    await page.advance(100);
    assert.equal(toasts(page).length, 0);
});

test('without playback a step only says so', { skip }, async () => {
    const page = createPage();
    page.respond('GET', '/Bookmarks/Capabilities', CAPABILITIES);
    page.run(source, file);
    await page.advance(50);
    page.installApiClient({ userId: 'user-1', items: {} });
    const osd = page.element('div', { id: 'videoOsdPage', class: 'page' }, [page.element('div', { class: 'videoOsdBottom' }, [page.element('div', { class: 'buttons' })])]);
    page.document.body.append(osd);
    await page.advance(400);
    page.document.querySelector('.btnFrameForward').click();
    await page.advance(50);
    assert.deepEqual(toasts(page), ['Start playing a video to step through its frames.']);
});

async function openAdjust(page, id) {
    page.document.querySelector('.btnBookmarkList').click();
    await page.advance(50);
    const row = page.document.querySelectorAll('.jfbm-panel .jfbm-row').find((r) => r.getAttribute('data-id') === id);
    const adjust = row.querySelectorAll('.jfbm-row-actions button').find((b) => b.getAttribute('title') === 'Adjust frame by frame');
    assert.ok(adjust, 'Adjust on the row');
    adjust.click();
    await page.advance(200);
}

function barText(page) {
    const b = bar(page);
    return {
        title: b.querySelector('.jfbm-adjust-title').textContent,
        start: b.querySelector('.jfbm-adjust-start').textContent,
        end: b.querySelector('.jfbm-adjust-end') ? b.querySelector('.jfbm-adjust-end').textContent : null,
        now: b.querySelector('.jfbm-adjust-now').textContent,
        hint: b.querySelector('.jfbm-adjust-hint').classList.contains('hide') ? null : b.querySelector('.jfbm-adjust-hint').textContent,
        error: b.querySelector('.jfbm-adjust-error').textContent,
        buttons: b.querySelectorAll('button').map((x) => x.getAttribute('title') + (x.disabled ? ' (off)' : ''))
    };
}

test('Adjust: the row action closes the list, shows the start paused in a bar; steps, Set start here and Save move the start by those frames', { skip }, async () => {
    const { page, video } = await player({ video: { time: 4.1 } });
    page.document.querySelector('.btnBookmarkList').click();
    await page.advance(50);
    const rows = page.document.querySelectorAll('.jfbm-panel .jfbm-row');
    assert.deepEqual(rows[1].querySelectorAll('.jfbm-row-actions button').map((b) => b.getAttribute('title')), ['Make the clip again', 'Adjust frame by frame', 'Edit', 'Delete']);
    page.key(page.document.querySelector('.jfbm-panel'), 'Escape');
    await openAdjust(page, 'b2');

    assert.equal(page.document.querySelector('.jfbm-backdrop'), null, 'the list closed');
    assert.equal(video.paused, true);
    near(video.currentTime, 30.02, 'the middle of the start\'s frame (750)');
    assert.equal(page.fetchLog.filter((f) => f.path === '/Sessions/s1/Playing/Seek').length, 0, 'the video seeked itself');
    assert.deepEqual(plain(barText(page)), {
        title: 'Adjusting "Range"',
        start: 'Start 0:30.000 (unchanged)',
        end: 'End 0:40.000 (unchanged)',
        now: 'On screen 0:30.020, the start',
        hint: 'Range length 0:10.0',
        error: '',
        buttons: ['Back one frame (,)', 'Forward one frame (.)', 'Make the frame on screen the start', 'Make the frame on screen the end', 'Leave the bookmark as it was', 'Save the new times (off)']
    });
    assert.equal(page.document.activeElement, bar(page).querySelector('.jfbm-adjust-forward'), 'the focus on Forward one frame');
    assert.equal(bar(page).getAttribute('aria-label'), 'Adjust Range');

    // In the bar: . three times (a held key's repeat too), Set start here.
    const seenByWeb = [];
    page.document.addEventListener('keydown', (event) => seenByWeb.push(event.key));
    page.key(page.document.activeElement, '.');
    await page.advance(100);
    page.key(page.document.activeElement, '.', { repeat: true });
    await page.advance(100);
    page.key(page.document.activeElement, '.');
    await page.advance(100);
    assert.equal(video.shownFrame(), 753);
    assert.deepEqual(seenByWeb, [], 'the bar\'s keys reach nothing else');
    assert.equal(barText(page).now, 'On screen 0:30.140, 3 frames after the start');
    bar(page).querySelector('.jfbm-adjust-set-start').click();
    await page.advance(10);
    assert.equal(barText(page).start, 'Start 0:30.140 (+3 frames)');
    assert.equal(barText(page).hint, 'Range length 0:09.8');
    assert.equal(barText(page).buttons[5], 'Save the new times', 'Save on');

    page.respond('POST', '/Bookmarks/b2', (entry) => Object.assign({}, bookmarks()[1], entry.body, { UpdatedAt: '2026-11-17T08:00:00.0000000Z' }));
    bar(page).querySelector('.jfbm-adjust-save').click();
    await page.advance(50);
    const saved = page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks/b2');
    assert.deepEqual(plain(saved.body), { StartTicks: 301400000, EndTicks: 400000000, Name: 'Range', Note: 'its note', IfUpdatedAt: '2026-11-01T08:00:00.0000000Z' });
    assert.equal(Math.floor(saved.body.StartTicks / 400000) - Math.floor(300000000 / 400000), 3, 'three frames later');
    assert.equal(bar(page), null, 'the bar closed');
    assert.ok(toasts(page).includes('Bookmark saved.'));
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Adjust: Set end here moves a range\'s end; the end must stay after the start, and the start before the end', { skip }, async () => {
    const { page, video } = await player({ video: { time: 4.1 } });
    await openAdjust(page, 'b2');
    bar(page).querySelector('.jfbm-adjust-back').click();
    await page.advance(100);
    assert.equal(video.shownFrame(), 749);
    bar(page).querySelector('.jfbm-adjust-set-end').click();
    await page.advance(10);
    assert.equal(barText(page).error, 'The end must be after the start.');
    assert.equal(barText(page).end, 'End 0:40.000 (unchanged)');

    // 41 s: past the end; Set start here there is refused, Set end here moves the end 25 frames on.
    video.currentTime = 41;
    await page.advance(100);
    bar(page).querySelector('.jfbm-adjust-set-start').click();
    await page.advance(10);
    assert.equal(barText(page).error, 'The start must be before the end.');
    bar(page).querySelector('.jfbm-adjust-set-end').click();
    await page.advance(10);
    assert.equal(barText(page).error, '');
    assert.equal(barText(page).end, 'End 0:41.020 (+25 frames)');
    page.respond('POST', '/Bookmarks/b2', (entry) => Object.assign({}, bookmarks()[1], entry.body));
    bar(page).querySelector('.jfbm-adjust-save').click();
    await page.advance(50);
    assert.deepEqual(page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks/b2').body.EndTicks, 410200000);
});

test('Adjust on a point bookmark has no Set end here and saves no end; the clip limit is warned of on a range', { skip }, async () => {
    const { page } = await player({ video: { time: 4.1 }, capabilities: { MaxClipDurationSeconds: 5 } });
    await openAdjust(page, 'b1');
    const text = barText(page);
    assert.equal(text.end, null);
    assert.equal(text.hint, null);
    assert.equal(bar(page).querySelector('.jfbm-adjust-set-end'), null);
    page.key(page.document.activeElement, ',');
    await page.advance(100);
    bar(page).querySelector('.jfbm-adjust-set-start').click();
    page.respond('POST', '/Bookmarks/b1', (entry) => Object.assign({}, bookmarks()[0], entry.body));
    bar(page).querySelector('.jfbm-adjust-save').click();
    await page.advance(50);
    assert.deepEqual(plain(page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks/b1').body), { StartTicks: 149800000, EndTicks: null, Name: 'Point', Note: null, IfUpdatedAt: '2026-11-01T08:00:00.0000000Z' });

    await openAdjust(page, 'b2');
    assert.equal(barText(page).hint, 'Range length 0:10.0, longer than the 0:05 clip limit');
    assert.ok(bar(page).querySelector('.jfbm-adjust-hint').classList.contains('jfbm-hint-warning'));
});

test('Adjust keys: Left and Right (and a remote\'s D-pad) move along the buttons, Escape cancels without saving; Cancel, stopping playback and closing the player discard too', { skip }, async () => {
    const { page, video, osd } = await player({ video: { time: 4.1 } });
    await openAdjust(page, 'b2');
    const titleOf = () => page.document.activeElement.getAttribute('title');
    page.key(page.document.activeElement, 'ArrowRight');
    assert.equal(titleOf(), 'Make the frame on screen the start');
    page.key(page.document.activeElement, 'ArrowRight');
    page.key(page.document.activeElement, 'ArrowRight');
    assert.equal(titleOf(), 'Leave the bookmark as it was', 'Save is off (nothing changed), so skipped');
    page.key(page.document.activeElement, 'ArrowRight');
    assert.equal(titleOf(), 'Back one frame (,)', 'wraps');
    page.key(page.document.activeElement, 'Unidentified', { keyCode: 205 });
    assert.equal(titleOf(), 'Leave the bookmark as it was', 'a D-pad left (keyCode 205) from the first wraps to the last button on');
    page.key(page.document.activeElement, 'Escape');
    assert.equal(bar(page), null, 'Escape cancelled');

    await openAdjust(page, 'b2');
    bar(page).querySelector('.jfbm-adjust-cancel').click();
    assert.equal(bar(page), null, 'Cancel');

    await openAdjust(page, 'b2');
    await page.context.ApiClient.reportPlaybackStopped({ ItemId: 'item1', PositionTicks: 300200000 });
    assert.equal(bar(page), null, 'playback stopped');

    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item1', PositionTicks: 300200000, MediaSourceId: 'ms-item1' });
    await openAdjust(page, 'b2');
    osd.classList.add('hide');
    page.document.body.append(page.element('div'));
    await page.advance(400);
    assert.equal(bar(page), null, 'the player closed');
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST' && f.path.indexOf('/Bookmarks/') === 0).length, 0, 'nothing saved');
    assert.ok(video.paused);
});

test('Adjust: a bookmark changed elsewhere (409) is read again and says so, keeping the times set here for the next Save; one deleted ends the bar', { skip }, async () => {
    const { page } = await player({ video: { time: 4.1 } });
    await openAdjust(page, 'b2');
    page.key(page.document.activeElement, '.');
    await page.advance(100);
    bar(page).querySelector('.jfbm-adjust-set-start').click();
    page.respond('POST', '/Bookmarks/b2', { type: 'conflict', title: 'Conflict', status: 409, detail: 'Changed elsewhere. Reload to see the latest.' }, 409);
    page.respond('GET', '/Bookmarks/b2', Object.assign({}, bookmarks()[1], { Name: 'Renamed', StartTicks: 310000000, UpdatedAt: '2026-11-17T09:00:00.0000000Z' }));
    bar(page).querySelector('.jfbm-adjust-save').click();
    await page.advance(50);
    assert.equal(barText(page).error, 'Changed elsewhere: it now runs from 0:31.000 to 0:40.000. Save replaces that with the times here.');
    assert.equal(barText(page).title, 'Adjusting "Renamed"');
    page.respond('POST', '/Bookmarks/b2', (entry) => Object.assign({}, bookmarks()[1], entry.body));
    bar(page).querySelector('.jfbm-adjust-save').click();
    await page.advance(50);
    const posts = page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/b2');
    assert.deepEqual(plain(posts[1].body), { StartTicks: 300600000, EndTicks: 400000000, Name: 'Renamed', Note: 'its note', IfUpdatedAt: '2026-11-17T09:00:00.0000000Z' });
    assert.equal(bar(page), null);

    await openAdjust(page, 'b1');
    page.key(page.document.activeElement, '.');
    await page.advance(100);
    bar(page).querySelector('.jfbm-adjust-set-start').click();
    page.respond('POST', '/Bookmarks/b1', { status: 404, detail: 'Bookmark not found.' }, 404);
    bar(page).querySelector('.jfbm-adjust-save').click();
    await page.advance(50);
    assert.equal(bar(page), null);
    assert.ok(toasts(page).includes('This bookmark was deleted elsewhere.'));
});

test('Adjust with the video\'s clock offset from the reported position (a transcode started part way): positions stay the reported ones', { skip }, async () => {
    // The video's clock reads 0 at 30 s of the film.
    const { page, video } = await player({ video: { time: 0, duration: 20 }, positionTicks: 300000000 });
    await openAdjust(page, 'b2');
    near(video.currentTime, 0.02, 'the start\'s frame, on the video\'s clock');
    page.key(page.document.activeElement, '.');
    await page.advance(100);
    bar(page).querySelector('.jfbm-adjust-set-start').click();
    assert.equal(barText(page).start, 'Start 0:30.060 (+1 frame)');
    page.respond('POST', '/Bookmarks/b2', (entry) => Object.assign({}, bookmarks()[1], entry.body));
    bar(page).querySelector('.jfbm-adjust-save').click();
    await page.advance(50);
    assert.equal(page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks/b2').body.StartTicks, 300600000);
});
