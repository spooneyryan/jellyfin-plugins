// User Ratings' star widget on the built ClientScript, in the kit's fake page. WebTests runs it:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfur node --test plugins/UserRatings/tests/Jellyfin.Plugin.UserRatings.Tests/Web/stars.test.mjs
// The shipped script mounts the widget only on a details page, so the tests take the 30-stars.js part of the built
// script, as EmbeddedScript labels it, and run it once more in the same page with the plugin's own kit (its strings from
// 00-strings.js included): the code under test is the code that ships, and the shipped script exposes nothing for it.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage, plain } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');
const SECTION = '// ---- Web/js/30-stars.js\n';
const EXPORTS = ['STARS_MAX', 'STARS_DEBOUNCE_MS', 'normalizeStars', 'valueFromPointer', 'stepValue', 'formatStars', 'starsValueText', 'clipPercent', 'starGesture', 'renderStars', 'createStarSlider'];
const KEYS = ['ArrowRight', 'ArrowLeft', 'ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End'];
const UNHANDLED = ['Delete', 'Backspace', 'Escape', 'Tab', '0', '3', '9', 'a', 'r', '/', '+', '-', 'F5'];

function starsSection() {
    const start = source.indexOf(SECTION);
    assert.ok(start >= 0, 'the ClientScript holds Web/js/30-stars.js');
    const end = source.indexOf('\n// ---- ', start + SECTION.length);
    assert.ok(end > start, 'the file after it');
    return source.substring(start + SECTION.length, end);
}

// The page with the built script started, and the 30-stars.js functions of the plugin's kit as page.stars.
async function load(options) {
    options = options || {};
    const page = createPage();
    // SVG elements: the fake page has only createElement, which is all the widget's markup needs for these checks.
    page.document.createElementNS = (ns, tag) => {
        const e = page.document.createElement(tag);
        e.namespaceURI = ns;
        return e;
    };
    if (options.layout) {
        page.document.documentElement.classList.add('layout-' + options.layout);
    }

    page.run(source, file);
    await page.advance(50);
    page.installApiClient({ userId: 'user-1' });
    await page.advance(50);
    page.run('globalThis.__stars = (function (kit) {\n' + starsSection() + '\nreturn { ' + EXPORTS.join(', ') + ' };\n})(__jfkit.jfur.instance);', '30-stars.js of ' + file);
    page.stars = page.context.__stars;
    return page;
}

// A slider in the page: a 200 px row of five 40 px stars at x 100 (the pointer's x on star n's middle is 100 + 40n - 20).
function mount(page, opts, where) {
    const commits = [];
    const changes = [];
    const answers = [];
    const widget = page.stars.createStarSlider(Object.assign({
        onCommit: (value) => {
            commits.push(value);
            return new Promise((resolve, reject) => answers.push({ value, resolve, reject }));
        },
        onChange: (value, how) => changes.push([value, how])
    }, opts || {}));
    const host = page.element('div', where && where.rtl ? { dir: 'rtl' } : {});
    host.append(widget.element);
    page.document.body.append(host);
    widget.slider.getBoundingClientRect = () => ({ left: 100, top: 200, width: 200, height: 40, right: 300, bottom: 240 });
    return { widget, slider: widget.slider, commits, changes, answers, host };
}

function pointer(page, el, type, init) {
    const event = new page.Event(type, { bubbles: true, cancelable: true });
    Object.assign(event, { pointerId: 1, pointerType: 'mouse', isPrimary: true, button: 0, clientX: 0, clientY: 220 }, init || {});
    el.dispatchEvent(event);
    return event;
}

function touch(page, el, type, x, y, init) {
    return pointer(page, el, type, Object.assign({ pointerType: 'touch', clientX: x, clientY: y === undefined ? 220 : y }, init || {}));
}

function fills(slider) {
    return slider.querySelectorAll('.jfur-star-fill').map((f) => f.style.width);
}

function aria(slider) {
    return [slider.getAttribute('aria-valuenow'), slider.getAttribute('aria-valuetext')];
}

// What jellyfin-web's window bubble listener gets to see.
function bubbleLog(page) {
    const seen = [];
    page.context.addEventListener('keydown', (e) => seen.push([e.key, e.defaultPrevented]));
    return seen;
}

// ------------------------------------------------------------------------------------------- pure functions

test('valueFromPointer: the half star under the pointer, rounded up; the middle of star 3 is 2.5', { skip }, async () => {
    const { stars } = await load();
    const W = 200;
    assert.equal(stars.valueFromPointer(0, W, false), 0.5, 'x = 0');
    assert.equal(stars.valueFromPointer(W, W, false), 5, 'x = width');
    assert.equal(stars.valueFromPointer(100, W, false), 2.5, 'the middle of star 3: a boundary belongs to the lower half');
    assert.equal(stars.valueFromPointer(100.01, W, false), 3, 'just past the middle');
    assert.equal(stars.valueFromPointer(99.99, W, false), 2.5, 'just before the middle');
    for (let k = 1; k <= 10; k++) {
        const boundary = (k * W) / 10;
        assert.equal(stars.valueFromPointer(boundary, W, false), k / 2, 'on boundary ' + k);
        assert.equal(stars.valueFromPointer(boundary - 0.01, W, false), k / 2, 'just before boundary ' + k);
        if (k < 10) {
            assert.equal(stars.valueFromPointer(boundary + 0.01, W, false), (k + 1) / 2, 'just past boundary ' + k);
        }
    }

    assert.equal(stars.valueFromPointer(-30, W, false), 0.5, 'left of the stars');
    assert.equal(stars.valueFromPointer(260, W, false), 5, 'right of the stars');
    // A width floating point can't split evenly: the middle of star 3 is still 2.5, a hair past it 3.
    assert.equal(stars.valueFromPointer(70.5, 141, false), 2.5);
    assert.equal(stars.valueFromPointer(70.51, 141, false), 3);
    assert.equal(stars.valueFromPointer(3.5 * 28.2, 141, false), 3.5, 'the boundary 3.5 at a width of 141');
    assert.equal(stars.valueFromPointer(3.5 * 28.2 + 0.01, 141, false), 4);
    // The 1e-9 margin: at a width of 172.8 px the boundary 1.5 (x 51.84) computes as
    // 3.0000000000000004 halves, which ceil would put in the upper half; a micro-pixel past it is the upper half.
    assert.equal(Math.ceil((51.84 * 10) / 172.8), 4, 'floating point puts this boundary a hair above 3 halves');
    assert.equal(stars.valueFromPointer(51.84, 172.8, false), 1.5, 'the boundary 1.5 at 172.8 px belongs to the lower half');
    assert.equal(stars.valueFromPointer(139.98000000000002, 233.3, false), 3, 'and the boundary 3 at 233.3 px');
    assert.equal(stars.valueFromPointer(51.84 + 1e-6, 172.8, false), 2, 'a millionth of a pixel past it is the upper half');
    assert.equal(stars.valueFromPointer(10, 0, false), null, 'a row with no width');
    assert.equal(stars.valueFromPointer(NaN, W, false), null);
    assert.equal(stars.valueFromPointer(undefined, W, false), null);
});

test('valueFromPointer right to left: the axis mirrored, star 1 at the right edge', { skip }, async () => {
    const { stars } = await load();
    const W = 200;
    assert.equal(stars.valueFromPointer(W, W, true), 0.5, 'the right edge is the lowest');
    assert.equal(stars.valueFromPointer(0, W, true), 5, 'the left edge is the highest');
    assert.equal(stars.valueFromPointer(100, W, true), 2.5, 'the middle of star 3 is 2.5 either way');
    assert.equal(stars.valueFromPointer(99.99, W, true), 3, 'just left of the middle is past it, right to left');
    assert.equal(stars.valueFromPointer(195, W, true), 0.5, 'the right half of the rightmost star: its first half');
    assert.equal(stars.valueFromPointer(170, W, true), 1, 'its left half');
    for (let x = 0; x <= W; x += 3.7) {
        assert.equal(stars.valueFromPointer(x, W, true), stars.valueFromPointer(W - x, W, false), 'mirrored at ' + x);
    }
});

test('stepValue: every key in every layout, left to right and right to left', { skip }, async () => {
    const { stars } = await load();
    const expected = (key, layout, rtl, v) => {
        const up = { ArrowRight: rtl ? -0.5 : 0.5, ArrowLeft: rtl ? 0.5 : -0.5, ArrowUp: 0.5, ArrowDown: -0.5, PageUp: 1, PageDown: -1 }[key];
        if ((key === 'ArrowUp' || key === 'ArrowDown') && layout === 'tv') {
            return null;
        }

        if (key === 'Home') {
            return 0.5;
        }

        if (key === 'End') {
            return 5;
        }

        if (up < 0 && v === 0) {
            return 0;
        }

        return Math.min(5, Math.max(0.5, v + up));
    };
    let cases = 0;
    for (const layout of ['desktop', 'mobile', 'tv']) {
        for (const rtl of [false, true]) {
            for (const key of KEYS) {
                for (let v = 0; v <= 5; v += 0.5) {
                    assert.equal(stars.stepValue(v, key, layout, rtl), expected(key, layout, rtl, v), [key, layout, rtl ? 'rtl' : 'ltr', v].join(' '));
                    cases++;
                }
            }

            for (const key of UNHANDLED.concat(['Enter', ' '])) {
                assert.equal(stars.stepValue(3, key, layout, rtl), null, key + ' is left to the page (Enter and Space are the DOM part\'s)');
            }
        }
    }

    assert.equal(cases, 3 * 2 * KEYS.length * 11);
    // The documented examples.
    assert.equal(stars.stepValue(0, 'ArrowRight', 'desktop', false), 0.5, 'Right from not rated');
    assert.equal(stars.stepValue(0.5, 'ArrowLeft', 'desktop', false), 0.5, 'never below 0.5');
    assert.equal(stars.stepValue(0, 'ArrowLeft', 'tv', false), 0, 'a decrease never rates an unrated item');
    assert.equal(stars.stepValue(0, 'PageDown', 'desktop', false), 0);
    assert.equal(stars.stepValue(1, 'PageDown', 'desktop', false), 0.5);
    assert.equal(stars.stepValue(5, 'ArrowRight', 'tv', false), 5, 'never above 5');
    assert.equal(stars.stepValue(4.5, 'PageUp', 'desktop', false), 5);
    assert.equal(stars.stepValue(0, 'Home', 'tv', false), 0.5);
    assert.equal(stars.stepValue(3, 'ArrowUp', 'tv', false), null, 'Up is the D-pad\'s on TV');
    assert.equal(stars.stepValue(3, 'ArrowLeft', 'tv', true), 3.5, 'right to left Left points at more stars');
    assert.equal(stars.stepValue(3, 'ArrowUp', 'desktop', true), 3.5, 'Up is up in either direction');
});

test('formatStars, starsValueText and clipPercent', { skip }, async () => {
    const { stars } = await load();
    assert.deepEqual([3.5, 4, 0.5, 5, 4.2, 4.25, 4.04, 5.5].map((v) => stars.formatStars(v)), ['3.5', '4', '0.5', '5', '4.2', '4.3', '4', '5']);
    assert.deepEqual([4, 4.2, 3.5].map((v) => stars.formatStars(v, 1)), ['4.0', '4.2', '3.5']);
    assert.deepEqual([0, null, undefined, NaN, '', -1].map((v) => stars.formatStars(v)), ['', '', '', '', '', '']);
    assert.equal(stars.starsValueText(3.5), '3.5 out of 5 stars');
    assert.equal(stars.starsValueText(1), '1 out of 5 stars');
    assert.equal(stars.starsValueText(5), '5 out of 5 stars');
    assert.equal(stars.starsValueText(0), 'Not rated');
    assert.equal(stars.starsValueText(null), 'Not rated');
    const row = (v) => [0, 1, 2, 3, 4].map((i) => stars.clipPercent(v, i));
    assert.deepEqual(row(4.2), [100, 100, 100, 100, 20], '4.2 draws four stars and a fifth to 20 %');
    assert.deepEqual(row(3.5), [100, 100, 100, 50, 0]);
    assert.deepEqual(row(0.5), [50, 0, 0, 0, 0]);
    assert.deepEqual(row(0), [0, 0, 0, 0, 0]);
    assert.deepEqual(row(5), [100, 100, 100, 100, 100]);
    assert.deepEqual(row(2.25), [100, 100, 25, 0, 0]);
    assert.deepEqual(row(NaN), [0, 0, 0, 0, 0]);
    assert.deepEqual([0, 0.2, 0.25, 0.3, 3.24, 3.26, 7, -1, 'x', null].map((v) => stars.normalizeStars(v)), [0, 0.5, 0.5, 0.5, 3, 3.5, 5, 0, 0, 0]);
});

test('starGesture: the recorded touch sequences as pure state transitions', { skip }, async () => {
    const { stars } = await load();
    const run = (current, steps) => {
        let state = null;
        let value = current;
        const commits = [];
        const shown = [];
        for (const [type, x, y, v] of steps) {
            const out = stars.starGesture(state, { type, x, y, value: v, current: value });
            state = out.state;
            if (out.value !== null) {
                value = out.value;
                shown.push(value);
            }

            if (out.commit) {
                commits.push(value);
            }
        }

        return { value, commits, shown, state };
    };
    // down then cancel: the value from before, nothing saved.
    assert.deepEqual(plain(run(2, [['down', 250, 220, 4], ['cancel']])), { value: 2, commits: [], shown: [2], state: null });
    // nothing on pointerdown.
    assert.equal(stars.starGesture(null, { type: 'down', x: 250, y: 220, value: 4, current: 2 }).value, null);
    // down, up without moving: the tapped value.
    assert.deepEqual(plain(run(0, [['down', 250, 220, 4], ['up', 250, 220, 4]])), { value: 4, commits: [4], shown: [4], state: null });
    // a jitter under 8 px is still a tap.
    assert.deepEqual(run(0, [['down', 250, 220, 4], ['move', 255, 224, 4], ['up', 256, 225, 4]]).commits, [4]);
    // down, a sideways move past the threshold, more moves, up: the scrubbed value.
    const scrub = run(4, [['down', 250, 220, 4], ['move', 241, 221, 3.5], ['move', 220, 221, 3], ['move', 200, 222, 2.5], ['move', 180, 221, 2], ['up', 180, 221, 2]]);
    assert.deepEqual(plain(scrub), { value: 2, commits: [2], shown: [3.5, 3, 2.5, 2, 2], state: null });
    // down, a vertical move, cancel (the browser took the pan): unchanged.
    assert.deepEqual(plain(run(2, [['down', 250, 220, 4], ['move', 251, 200, 4], ['move', 251, 120, 4], ['cancel']])), { value: 2, commits: [], shown: [2], state: null });
    // a pan the browser didn't take (the page can't scroll) ends without a rating.
    assert.deepEqual(plain(run(2, [['down', 250, 220, 4], ['move', 250, 300, 4], ['up', 250, 300, 4]])), { value: 2, commits: [], shown: [], state: null });
    // a mostly sideways diagonal (60 px right, 20 px down) scrubs.
    assert.deepEqual(run(1, [['down', 150, 220, 1.5], ['move', 160, 223, 1.5], ['move', 180, 230, 2], ['move', 210, 240, 3], ['up', 210, 240, 3]]).commits, [3]);
    // a scrub cancelled half way puts back the value from before the touch.
    assert.deepEqual(run(4, [['down', 250, 220, 4], ['move', 230, 220, 3], ['move', 200, 220, 2.5], ['cancel']]).value, 4);
    // a move or an up with no touch under way changes nothing.
    assert.deepEqual(plain(stars.starGesture(null, { type: 'move', x: 1, y: 1, value: 3, current: 1 })), { state: null, value: null, commit: false });
    assert.deepEqual(plain(stars.starGesture(null, { type: 'up', x: 1, y: 1, value: 3, current: 1 })), { state: null, value: null, commit: false });
    assert.deepEqual(plain(stars.starGesture(null, { type: 'cancel', current: 1 })), { state: null, value: null, commit: false });
});

// ---------------------------------------------------------------------------------------------- the slider

test('the slider\'s markup: one tab stop role="slider" with the focusable class, its aria values, the − / + buttons, no request', { skip }, async () => {
    const page = await load();
    assert.equal(page.document.querySelectorAll('.jfur-stars, .jfur-rating').length, 0, 'the shipped script mounts no widget outside a details page');
    const label = page.element('span', { id: 'your-rating' });
    page.document.body.append(label);
    const { widget, slider } = mount(page, { labelledBy: 'your-rating' });
    assert.equal(slider.tagName, 'DIV');
    assert.equal(slider.className, 'jfur-stars focusable', 'jellyfin-web\'s focus manager takes .focusable');
    assert.equal(slider.getAttribute('role'), 'slider');
    assert.equal(slider.getAttribute('tabindex'), '0');
    assert.equal(slider.getAttribute('aria-valuemin'), '0');
    assert.equal(slider.getAttribute('aria-valuemax'), '5');
    assert.deepEqual(aria(slider), ['0', 'Not rated']);
    assert.equal(slider.getAttribute('aria-labelledby'), 'your-rating');
    assert.equal(slider.hasAttribute('aria-label'), false);
    assert.equal(slider.querySelectorAll('.jfur-star').length, 5);
    assert.equal(slider.querySelectorAll('.jfur-star > svg.jfur-star-off').length, 5);
    assert.equal(slider.querySelectorAll('.jfur-star > .jfur-star-fill > svg.jfur-star-on').length, 5);
    assert.ok(slider.querySelectorAll('svg').every((s) => s.getAttribute('aria-hidden') === 'true' && !s.hasAttribute('tabindex') && s.namespaceURI === 'http://www.w3.org/2000/svg'));
    assert.deepEqual(fills(slider), ['0%', '0%', '0%', '0%', '0%']);
    const parts = widget.element.children;
    assert.deepEqual(parts.map((p) => p.className), ['jfur-stars-step jfur-stars-minus', 'jfur-stars focusable', 'jfur-stars-step jfur-stars-plus', 'jfur-stars-value jfur-isolate', 'jfur-stars-note jfur-isolate hide', 'jfur-sr-only jfur-stars-live']);
    assert.equal(widget.element.className, 'jfur-rating');
    const [minus, , plus, value, note, live] = parts;
    assert.deepEqual([minus.getAttribute('aria-label'), plus.getAttribute('aria-label')], ['Half a star less', 'Half a star more']);
    assert.deepEqual([minus.getAttribute('type'), minus.disabled, plus.disabled], ['button', true, false]);
    assert.equal(minus.firstChild.className, 'material-icons remove');
    assert.equal(value.textContent, 'Not rated');
    assert.equal(value.getAttribute('aria-hidden'), 'true', 'the value is the slider\'s aria-valuetext already');
    assert.equal(note.getAttribute('id'), widget.element.getAttribute('data-jfur-stars-id') + '-note');
    assert.equal(note.getAttribute('aria-hidden'), 'true', 'the note is read once, as the slider\'s description');
    assert.deepEqual([live.getAttribute('role'), live.getAttribute('aria-live'), live.getAttribute('aria-atomic')], ['status', 'polite', 'true']);
    // Only the slider takes the focus in its row (the buttons are hidden by CSS except at (pointer: coarse)).
    assert.deepEqual(widget.element.querySelectorAll('[tabindex]').map((e) => e.className), ['jfur-stars focusable']);
    // Without a label of the page's own it names itself.
    assert.equal(mount(page).slider.getAttribute('aria-label'), 'Your rating');
    assert.equal(mount(page, { label: 'Your rating of Long Test Movie' }).slider.getAttribute('aria-label'), 'Your rating of Long Test Movie');
    assert.equal(mount(page, { value: 3.5 }).slider.getAttribute('aria-valuetext'), '3.5 out of 5 stars');
    await page.advance(2000);
    assert.deepEqual(page.fetchLog, [], 'the widget never calls the API');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('mouse: hover previews, a click sets the value (the middle of star 3 is 2.5), leaving restores it, saved 600 ms later', { skip }, async () => {
    const page = await load();
    const { widget, slider, commits, changes, answers } = mount(page);
    const value = widget.element.querySelector('.jfur-stars-value');
    pointer(page, slider, 'pointermove', { clientX: 100 + 100 });
    assert.deepEqual(fills(slider), ['100%', '100%', '50%', '0%', '0%'], 'the middle of star 3 previews 2.5');
    assert.deepEqual(aria(slider), ['0', 'Not rated'], 'a preview isn\'t the value');
    assert.equal(value.textContent, '2.5');
    assert.ok(widget.element.classList.contains('jfur-rating-preview'));
    pointer(page, slider, 'pointermove', { clientX: 100 + 100.5 });
    assert.deepEqual(fills(slider), ['100%', '100%', '100%', '0%', '0%'], 'just past it, 3');
    pointer(page, slider, 'pointerleave', { clientX: 400 });
    assert.deepEqual(fills(slider), ['0%', '0%', '0%', '0%', '0%'], 'leaving shows the value again');
    assert.equal(value.textContent, 'Not rated');
    assert.equal(widget.element.classList.contains('jfur-rating-preview'), false);

    pointer(page, slider, 'pointermove', { clientX: 200 });
    pointer(page, slider, 'pointerdown', { clientX: 200 });
    assert.deepEqual(aria(slider), ['0', 'Not rated'], 'nothing on pointerdown');
    pointer(page, slider, 'pointerup', { clientX: 200 });
    assert.deepEqual(aria(slider), ['2.5', '2.5 out of 5 stars']);
    assert.deepEqual(changes, [[2.5, 'click']]);
    assert.equal(widget.isPending(), true);
    await page.advance(599);
    assert.deepEqual(commits, [], 'not before 600 ms');
    await page.advance(1);
    assert.deepEqual(commits, [2.5]);
    assert.equal(value.textContent, 'Saving…', 'while saving');
    assert.ok(widget.element.classList.contains('jfur-rating-saving'));
    assert.equal(widget.isSaving(), true);
    answers[0].resolve({});
    await page.advance(60);
    assert.equal(widget.savedValue(), 2.5);
    assert.equal(value.textContent, '2.5');
    assert.equal(widget.element.querySelector('.jfur-stars-live').textContent, 'Saved: 2.5 stars');

    // Clicking the current value does nothing; a right button or a click begun elsewhere neither.
    pointer(page, slider, 'pointerdown', { clientX: 200 });
    pointer(page, slider, 'pointerup', { clientX: 200 });
    pointer(page, slider, 'pointerdown', { clientX: 290, button: 2 });
    pointer(page, slider, 'pointerup', { clientX: 290, button: 2 });
    pointer(page, slider, 'pointerup', { clientX: 290 });
    await page.advance(700);
    assert.deepEqual(commits, [2.5]);
    assert.equal(widget.value(), 2.5);
    // Clicks in a row: only the last is saved, 600 ms after it.
    for (const x of [110, 150, 299]) {
        pointer(page, slider, 'pointerdown', { clientX: x });
        pointer(page, slider, 'pointerup', { clientX: x });
        await page.advance(300);
    }

    assert.deepEqual(commits, [2.5]);
    await page.advance(300);
    assert.deepEqual(commits, [2.5, 5]);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('keyboard: Right ×7 is 3.5, Enter saves at once, every handled key prevented, the page\'s keys left alone (flow B)', { skip }, async () => {
    const page = await load();
    const { widget, slider, commits, answers } = mount(page);
    const seen = bubbleLog(page);
    for (let i = 0; i < 7; i++) {
        assert.equal(page.key(slider, 'ArrowRight', { keyCode: 39 }), false, 'Right prevented');
    }

    assert.deepEqual(aria(slider), ['3.5', '3.5 out of 5 stars'], 'it reads "3.5 out of 5 stars"');
    assert.equal(page.key(slider, 'Enter', { keyCode: 13 }), false, 'Enter prevented');
    assert.deepEqual(commits, [3.5], 'Enter saves now, skipping the debounce');
    answers[0].resolve({});
    await page.advance(700);
    assert.deepEqual(commits, [3.5], 'and once');
    const steps = [['ArrowUp', 4], ['ArrowDown', 3.5], ['ArrowLeft', 3], ['PageUp', 4], ['PageDown', 3], ['End', 5], ['ArrowRight', 5], ['PageUp', 5], ['Home', 0.5], ['ArrowLeft', 0.5], ['ArrowDown', 0.5], ['PageDown', 0.5]];
    for (const [key, expected] of steps) {
        assert.equal(page.key(slider, key), false, key + ' prevented');
        assert.equal(widget.value(), expected, key);
    }

    assert.equal(page.key(slider, ' '), false, 'Space prevented: it saves, and never scrolls the page');
    assert.deepEqual(commits, [3.5, 0.5]);
    answers[1].resolve({});
    await page.advance(10);
    for (const key of UNHANDLED) {
        assert.equal(page.key(slider, key), true, key + ' is left to the page (Escape is Back on TV, Backspace on VIDAA, digits are JE\'s)');
    }

    assert.equal(widget.value(), 0.5, 'no key clears');
    for (const mod of ['ctrlKey', 'altKey', 'metaKey']) {
        assert.equal(page.key(slider, 'ArrowRight', { [mod]: true }), true, mod + ': the browser\'s (Alt+Left is Back)');
    }

    assert.equal(page.key(slider, 'ArrowRight', { isComposing: true }), true);
    const prevented = new page.Event('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true });
    prevented.preventDefault();
    slider.dispatchEvent(prevented);
    assert.equal(widget.value(), 0.5, 'a key a listener before it handled');
    assert.deepEqual(seen.filter((s) => s[0] === 'ArrowRight' || s[0] === 'Enter').slice(0, 8).map((s) => s[1]), [true, true, true, true, true, true, true, true], 'jellyfin-web\'s window handler sees them prevented');
    // Three quick steps: one save, of the last value, 600 ms after the last key.
    page.key(slider, 'ArrowRight');
    await page.advance(200);
    page.key(slider, 'ArrowRight');
    await page.advance(200);
    page.key(slider, 'ArrowRight');
    await page.advance(599);
    assert.deepEqual(commits, [3.5, 0.5]);
    await page.advance(1);
    assert.deepEqual(commits, [3.5, 0.5, 2]);
    // Stepping away and back within the debounce saves nothing.
    answers.forEach((a) => a.resolve({}));
    await page.advance(100);
    page.key(slider, 'ArrowRight');
    page.key(slider, 'ArrowLeft');
    await page.advance(700);
    assert.deepEqual(commits, [3.5, 0.5, 2]);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('TV layout: Left/Right step and are prevented, Up/Down leave it to the D-pad, OK saves, the remotes\' codes too', { skip }, async () => {
    const page = await load({ layout: 'tv' });
    const { widget, slider, commits } = mount(page);
    assert.equal(widget.element.className, 'jfur-rating jfur-rating-tv');
    const seen = bubbleLog(page);
    for (let i = 0; i < 7; i++) {
        page.key(slider, 'ArrowRight', { keyCode: 39 });
    }

    assert.equal(widget.value(), 3.5);
    assert.equal(page.key(slider, 'ArrowLeft', { keyCode: 37 }), false);
    assert.equal(widget.value(), 3);
    assert.equal(page.key(slider, 'ArrowUp', { keyCode: 38 }), true, 'Up passes through, so the focus can leave');
    assert.equal(page.key(slider, 'ArrowDown', { keyCode: 40 }), true, 'Down too');
    assert.equal(widget.value(), 3, 'and changes nothing');
    assert.equal(page.key(slider, 'Enter', { keyCode: 13 }), false, 'OK');
    assert.deepEqual(commits, [3]);
    assert.deepEqual(seen.map((s) => s.join(':')), ['ArrowRight:true', 'ArrowRight:true', 'ArrowRight:true', 'ArrowRight:true', 'ArrowRight:true', 'ArrowRight:true', 'ArrowRight:true', 'ArrowLeft:true', 'ArrowUp:false', 'ArrowDown:false', 'Enter:true']);
    for (const [key, keyCode, expected] of [['GamepadDPadRight', 206, 3.5], ['GamepadDPadLeft', 205, 3], ['NavigationRight', 141, 3.5], ['Unidentified', 39, 4], ['GamepadDPadUp', 203, 4], ['NavigationDown', 139, 4]]) {
        const handled = !page.key(slider, key, { keyCode });
        assert.equal(widget.value(), expected, key);
        assert.equal(handled, !/Up|Down/.test(key), key + (handled ? ' handled' : ' left to the D-pad'));
    }

    for (const [key, expected] of [['PageUp', 5], ['PageDown', 4], ['End', 5], ['Home', 0.5]]) {
        assert.equal(page.key(slider, key), false, key);
        assert.equal(widget.value(), expected, key);
    }

    assert.equal(page.key(slider, 'Escape', { keyCode: 27 }), true, 'Back is jellyfin-web\'s');
    assert.equal(page.key(slider, 'Backspace', { keyCode: 8 }), true);
    assert.equal(page.key(slider, 'Unidentified', { keyCode: 10009 }), true, 'Tizen\'s Back');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('right to left: the stars fill from the right, the pointer mirrored, Left adds half a star', { skip }, async () => {
    const page = await load();
    const { widget, slider } = mount(page, { value: 2 }, { rtl: true });
    // The leftmost 40 px are star 5 right to left: its left half (the end of the row) is 5, its right half 4.5.
    pointer(page, slider, 'pointerdown', { clientX: 105 });
    pointer(page, slider, 'pointerup', { clientX: 105 });
    assert.equal(widget.value(), 5);
    pointer(page, slider, 'pointerdown', { clientX: 135 });
    pointer(page, slider, 'pointerup', { clientX: 135 });
    assert.equal(widget.value(), 4.5);
    pointer(page, slider, 'pointerdown', { clientX: 200 });
    pointer(page, slider, 'pointerup', { clientX: 200 });
    assert.equal(widget.value(), 2.5, 'the middle of star 3');
    pointer(page, slider, 'pointerdown', { clientX: 290 });
    pointer(page, slider, 'pointerup', { clientX: 290 });
    assert.equal(widget.value(), 0.5, 'the right edge');
    assert.deepEqual(fills(slider), ['50%', '0%', '0%', '0%', '0%'], 'star 1 half filled: CSS anchors its fill at the start edge, the right one here');
    page.key(slider, 'ArrowLeft');
    assert.equal(widget.value(), 1, 'Left points at more stars');
    page.key(slider, 'ArrowRight');
    page.key(slider, 'ArrowRight');
    assert.equal(widget.value(), 0.5, 'Right at fewer, never below 0.5');
    page.key(slider, 'ArrowUp');
    assert.equal(widget.value(), 1, 'Up is up');
    // The same widget moved under a left-to-right parent reads the direction again.
    const ltr = page.element('div', { dir: 'ltr' });
    ltr.append(widget.element);
    page.document.body.append(ltr);
    page.key(slider, 'ArrowLeft');
    assert.equal(widget.value(), 0.5);
});

test('touch: a tap sets the value on pointerup, a sideways drag scrubs, a vertical swipe the browser cancels changes nothing', { skip }, async () => {
    const page = await load({ layout: 'mobile' });
    const { widget, slider, commits, changes, answers } = mount(page, { value: 2 });
    assert.equal(widget.element.className, 'jfur-rating jfur-rating-touch', '40 px stars in the mobile layout (CSS)');
    // A tap on star 4's right half.
    touch(page, slider, 'pointerdown', 100 + 3.75 * 40);
    assert.equal(widget.value(), 2, 'nothing on pointerdown');
    touch(page, slider, 'pointerup', 100 + 3.75 * 40);
    assert.equal(widget.value(), 4);
    assert.equal(widget.element.classList.contains('jfur-rating-preview'), false, 'no hover state from a touch');
    await page.advance(600);
    assert.deepEqual(commits, [4], 'saved 600 ms after the tap');
    answers[0].resolve({});
    await page.advance(60);

    // A drag two stars to the left from star 4: follows the finger, saved once after it lifts.
    let x = 100 + 3.75 * 40;
    touch(page, slider, 'pointerdown', x);
    for (let i = 1; i <= 8; i++) {
        touch(page, slider, 'pointermove', x - i * 10, 221);
    }

    assert.deepEqual(aria(slider), ['2', '2 out of 5 stars'], 'the value follows the finger');
    await page.advance(1000);
    assert.deepEqual(commits, [4], 'nothing saved while the finger is down');
    touch(page, slider, 'pointerup', x - 80, 221);
    await page.advance(600);
    assert.deepEqual(commits, [4, 2]);
    answers[1].resolve({});
    await page.advance(60);
    assert.deepEqual(changes.map((c) => c[0] + ':' + c[1]), ['4:tap', '3.5:drag', '3:drag', '2.5:drag', '2:drag']);

    // A vertical swipe up 300 px from star 4: the browser scrolls and cancels the pointer; nothing changes, nothing saved.
    touch(page, slider, 'pointerdown', x, 220);
    touch(page, slider, 'pointermove', x, 200);
    touch(page, slider, 'pointermove', x, 120);
    touch(page, slider, 'pointercancel', x, 120);
    // ... and one down from star 1.
    touch(page, slider, 'pointerdown', 130, 220);
    touch(page, slider, 'pointermove', 130, 260);
    touch(page, slider, 'pointercancel', 130, 260);
    await page.advance(1000);
    assert.equal(widget.value(), 2, 'a scroll\'s 2 → 4 and 4 → 1 can\'t happen');
    assert.deepEqual(commits, [4, 2]);
    // A drag the browser cancels half way (it took the pan after all): the value from before the touch.
    touch(page, slider, 'pointerdown', x);
    touch(page, slider, 'pointermove', x - 30);
    assert.equal(widget.value(), 3);
    touch(page, slider, 'pointercancel', x - 30);
    assert.equal(widget.value(), 2);
    await page.advance(1000);
    assert.deepEqual(commits, [4, 2]);
    // A second finger is ignored while the first is down.
    touch(page, slider, 'pointerdown', 110, 220, { pointerId: 7 });
    touch(page, slider, 'pointerdown', 290, 220, { pointerId: 8 });
    touch(page, slider, 'pointerup', 290, 220, { pointerId: 8 });
    assert.equal(widget.value(), 2);
    touch(page, slider, 'pointerup', 110, 220, { pointerId: 7 });
    assert.equal(widget.value(), 0.5);
    // A touch pauses a change waiting for its save; a swipe the browser takes then lets it go on.
    touch(page, slider, 'pointerdown', 250, 220);
    await page.advance(400);
    touch(page, slider, 'pointermove', 250, 150);
    touch(page, slider, 'pointercancel', 250, 150);
    await page.advance(599);
    assert.deepEqual(commits, [4, 2]);
    await page.advance(1);
    assert.deepEqual(commits, [4, 2, 0.5]);
    // A pen is a touch.
    pointer(page, slider, 'pointerdown', { pointerType: 'pen', clientX: 290, pointerId: 3 });
    assert.equal(widget.value(), 0.5);
    pointer(page, slider, 'pointerup', { pointerType: 'pen', clientX: 290, pointerId: 3 });
    assert.equal(widget.value(), 5);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the − / + buttons step by half a star and are disabled at the ends; saved after the debounce', { skip }, async () => {
    const page = await load();
    const { widget, commits } = mount(page);
    const [minus, , plus] = widget.element.children;
    assert.deepEqual([minus.disabled, plus.disabled], [true, false]);
    plus.click();
    assert.equal(widget.value(), 0.5);
    assert.deepEqual([minus.disabled, plus.disabled], [true, false], '− can\'t go below 0.5');
    for (let i = 0; i < 12; i++) {
        plus.click();
    }

    assert.equal(widget.value(), 5);
    assert.deepEqual([minus.disabled, plus.disabled], [false, true]);
    minus.click();
    assert.equal(widget.value(), 4.5);
    assert.deepEqual(commits, []);
    await page.advance(600);
    assert.deepEqual(commits, [4.5]);
});

test('a save that fails puts back the last saved value; a newer change during it is kept; a late answer never wins', { skip }, async () => {
    const page = await load();
    const { widget, slider, commits, answers } = mount(page, { value: 3 });
    page.key(slider, 'ArrowRight');
    page.key(slider, 'Enter');
    assert.deepEqual(commits, [3.5]);
    answers[0].reject(new Error('busy'));
    await page.advance(60);
    assert.deepEqual(aria(slider), ['3', '3 out of 5 stars'], 'back to the saved 3');
    assert.equal(widget.element.querySelector('.jfur-stars-live').textContent, '', 'no "Saved"; the page shows the error and Retry');
    assert.equal(widget.element.querySelector('.jfur-stars-value').textContent, '3');

    page.key(slider, 'ArrowRight');
    page.key(slider, 'Enter');
    page.key(slider, 'ArrowRight');
    answers[1].reject(new Error('busy'));
    await page.advance(60);
    assert.equal(widget.value(), 4, 'the member\'s newer change stays and saves itself');
    await page.advance(600);
    assert.deepEqual(commits, [3.5, 3.5, 4]);

    // One save at a time: changes while the 4 is in flight wait, and when it settles only the latest goes.
    page.key(slider, 'ArrowRight');
    page.key(slider, 'Enter');
    page.key(slider, 'ArrowRight');
    page.key(slider, 'Enter');
    await page.advance(700);
    assert.deepEqual(commits, [3.5, 3.5, 4], 'nothing more while the 4 is in flight');
    answers[2].resolve({});
    await page.advance(10);
    assert.deepEqual(commits, [3.5, 3.5, 4, 5], 'the latest value, once');
    answers[3].resolve({});
    await page.advance(60);
    assert.equal(widget.savedValue(), 5);
    // A commit callback that throws is a failed save.
    const thrower = mount(page, { value: 1, onCommit: () => { throw new Error('nope'); } });
    page.key(thrower.slider, 'End');
    page.key(thrower.slider, 'Enter');
    await page.advance(60);
    assert.equal(thrower.widget.value(), 1);
    // Saved: 1 star (one star).
    const one = mount(page, { value: 0.5 });
    page.key(one.slider, 'ArrowRight');
    page.key(one.slider, 'Enter');
    one.answers[0].resolve({});
    await page.advance(60);
    assert.equal(one.widget.element.querySelector('.jfur-stars-live').textContent, 'Saved: 1 star');
});

test('setValue takes the server\'s value and drops a change not handed on; announce speaks; destroy hands a pending change on', { skip }, async () => {
    const page = await load();
    const { widget, slider, commits, answers } = mount(page, { value: 4 });
    page.key(slider, 'ArrowLeft');
    widget.setValue(2.5);
    assert.deepEqual(aria(slider), ['2.5', '2.5 out of 5 stars']);
    assert.equal(widget.isPending(), false);
    await page.advance(700);
    assert.deepEqual(commits, []);
    widget.setValue(0);
    assert.deepEqual(aria(slider), ['0', 'Not rated'], 'after the page\'s Clear');
    assert.deepEqual(fills(slider), ['0%', '0%', '0%', '0%', '0%']);
    widget.announce('Rating cleared');
    await page.advance(60);
    assert.equal(widget.element.querySelector('.jfur-stars-live').textContent, 'Rating cleared');
    page.key(slider, 'End');
    widget.flush();
    assert.deepEqual(commits, [5]);
    page.key(slider, 'Home');
    widget.destroy();
    assert.deepEqual(commits, [5], 'the 5 is still in flight: the change waits for it, so the server gets them in order');
    answers[0].resolve({});
    await page.advance(10);
    assert.deepEqual(commits, [5, 0.5], 'leaving the page within the 600 ms still saves');
    widget.destroy();
    assert.deepEqual(commits, [5, 0.5]);
    // Nothing in flight: at once.
    const quick = mount(page, { value: 2 });
    page.key(quick.slider, 'ArrowRight');
    quick.widget.destroy();
    assert.deepEqual(quick.commits, [2.5]);
});

test('disabled: the page\'s reason and note, aria-disabled, no input changes it, every key left to the page, still a tab stop', { skip }, async () => {
    const page = await load({ layout: 'tv' });
    const { widget, slider, commits } = mount(page, { value: 3 });
    page.key(slider, 'ArrowRight');
    widget.setDisabled({ reason: 'NeedsPlay', note: 'Play this first: rating it needs it to be played' });
    assert.equal(widget.value(), 3, 'a change waiting for its save is dropped');
    assert.equal(slider.getAttribute('aria-disabled'), 'true');
    assert.equal(slider.getAttribute('data-jfur-reason'), 'NeedsPlay');
    const note = widget.element.querySelector('.jfur-stars-note');
    assert.equal(note.textContent, 'Play this first: rating it needs it to be played');
    assert.equal(note.classList.contains('hide'), false);
    assert.equal(slider.getAttribute('aria-describedby'), note.id, 'the note is the slider\'s description');
    assert.equal(note.getAttribute('aria-hidden'), 'true', 'and only that: not read again in the reading order');
    assert.equal(slider.hasAttribute('title'), false, 'nor as a tooltip: the note is shown beside the stars');
    assert.equal(slider.getAttribute('tabindex'), '0', 'reachable, so the note can be read');
    assert.ok(widget.element.classList.contains('jfur-rating-disabled'));
    assert.deepEqual(aria(slider), ['3', '3 out of 5 stars'], 'the saved value still reads');
    for (const key of ['ArrowRight', 'ArrowLeft', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'Enter', ' ']) {
        assert.equal(page.key(slider, key), true, key + ' is the page\'s: the D-pad moves on');
    }

    pointer(page, slider, 'pointermove', { clientX: 290 });
    pointer(page, slider, 'pointerdown', { clientX: 290 });
    pointer(page, slider, 'pointerup', { clientX: 290 });
    touch(page, slider, 'pointerdown', 110);
    touch(page, slider, 'pointerup', 110);
    const [minus, , plus] = widget.element.children;
    assert.deepEqual([minus.disabled, plus.disabled], [true, true]);
    plus.click();
    await page.advance(1000);
    assert.equal(widget.value(), 3);
    assert.deepEqual(fills(slider), ['100%', '100%', '100%', '0%', '0%'], 'no preview either');
    assert.deepEqual(commits, []);
    // The widget carries whatever reason it is given (ReadOnly, NeedsPlay, one a later server adds) and decides nothing.
    widget.setDisabled({ reason: 'ReadOnly', note: 'Ratings are read-only right now.' });
    assert.equal(slider.getAttribute('data-jfur-reason'), 'ReadOnly');
    widget.setDisabled({ reason: 'NotARater' });
    assert.equal(slider.getAttribute('data-jfur-reason'), 'NotARater');
    assert.equal(slider.hasAttribute('aria-describedby'), false, 'no note, nothing to describe it');
    assert.equal(note.classList.contains('hide'), true);
    widget.setDisabled(null);
    assert.deepEqual(['aria-disabled', 'data-jfur-reason', 'title'].map((a) => slider.hasAttribute(a)), [false, false, false]);
    assert.equal(page.key(slider, 'ArrowRight'), false);
    assert.equal(widget.value(), 3.5);
});

test('setValue while a save is in flight: its late answer changes nothing, and the same value can be saved again', { skip }, async () => {
    const page = await load();
    const { widget, slider, commits, answers } = mount(page, { value: 3 });
    const live = widget.element.querySelector('.jfur-stars-live');
    page.key(slider, 'ArrowRight');
    page.key(slider, 'Enter');
    assert.deepEqual(commits, [3.5]);
    // The page's Clear (DELETE Mine/Rating) answers first, then setValue(0); the PUT answers after it.
    widget.setValue(0);
    answers[0].resolve({});
    await page.advance(60);
    assert.deepEqual([widget.value(), widget.savedValue()], [0, 0], 'the stars and the saved value agree: not rated');
    assert.deepEqual(aria(slider), ['0', 'Not rated']);
    assert.equal(live.textContent, '', 'no "Saved: 3.5 stars" for a value the page replaced');
    // 3.5 again is a change, and it is sent.
    pointer(page, slider, 'pointerdown', { clientX: 100 + 3.25 * 40 });
    pointer(page, slider, 'pointerup', { clientX: 100 + 3.25 * 40 });
    assert.equal(widget.value(), 3.5);
    await page.advance(600);
    assert.deepEqual(commits, [3.5, 3.5]);
    answers[1].resolve({});
    await page.advance(60);
    assert.equal(widget.savedValue(), 3.5);
    assert.equal(live.textContent, 'Saved: 3.5 stars');

    // A refresh with the server's value (setValue(4)) while a save is in flight: neither its success nor its failure moves it.
    page.key(slider, 'End');
    page.key(slider, 'Enter');
    widget.setValue(4);
    answers[2].reject(new Error('late'));
    await page.advance(60);
    assert.deepEqual([widget.value(), widget.savedValue()], [4, 4]);
    page.key(slider, 'ArrowRight');
    page.key(slider, 'Enter');
    widget.setValue(1);
    answers[3].resolve({});
    await page.advance(60);
    assert.deepEqual([widget.value(), widget.savedValue()], [1, 1]);
    assert.deepEqual(commits, [3.5, 3.5, 5, 4.5]);
});

test('one save in flight at a time: Enter twice sends once, changes meanwhile wait and only the latest goes', { skip }, async () => {
    const page = await load();
    const { widget, slider, commits, answers } = mount(page, { value: 3 });
    page.key(slider, 'ArrowRight');
    page.key(slider, 'Enter');
    page.key(slider, 'Enter');
    assert.deepEqual(commits, [3.5], 'Enter twice, one save');
    assert.equal(widget.isSaving(), true);
    answers[0].resolve({});
    await page.advance(700);
    assert.deepEqual(commits, [3.5], 'and none after it settled: the value is the saved one');
    // Three changes while one save is in flight, the debounce of the last running out meanwhile: one save, of the last.
    page.key(slider, 'ArrowRight');
    page.key(slider, 'Enter');
    assert.deepEqual(commits, [3.5, 4]);
    page.key(slider, 'ArrowRight');
    page.key(slider, 'ArrowRight');
    page.key(slider, 'ArrowRight');
    await page.advance(1000);
    assert.deepEqual(commits, [3.5, 4], 'it waits for the 4');
    assert.equal(widget.isPending(), false, 'its debounce ran out');
    answers[1].resolve({});
    await page.advance(10);
    assert.deepEqual(commits, [3.5, 4, 5]);
    // A change back to the value being saved while it is in flight: nothing more once it is saved.
    page.key(slider, 'ArrowLeft');
    page.key(slider, 'Enter');
    page.key(slider, 'ArrowRight');
    await page.advance(700);
    answers[2].resolve({});
    await page.advance(10);
    assert.deepEqual(commits, [3.5, 4, 5], 'the 4.5 never went: the value was 5 again, saved');
    // A failure with a newer change waiting: the newer one goes, once.
    page.key(slider, 'Home');
    page.key(slider, 'Enter');
    page.key(slider, 'PageUp');
    page.key(slider, 'Enter');
    assert.deepEqual(commits, [3.5, 4, 5, 0.5]);
    answers[3].reject(new Error('busy'));
    await page.advance(10);
    assert.deepEqual(commits, [3.5, 4, 5, 0.5, 1.5]);
    assert.equal(widget.value(), 1.5, 'the newer change kept');
});

test('setDisabled during a save: the stars show the value being saved, and its answer settles stars and saved value together', { skip }, async () => {
    const page = await load();
    const { widget, slider, commits, answers } = mount(page, { value: 3 });
    const shown = widget.element.querySelector('.jfur-stars-value');
    page.key(slider, 'ArrowRight');
    page.key(slider, 'Enter');
    page.key(slider, 'ArrowRight');
    widget.setDisabled({ reason: 'ReadOnly', note: 'Ratings are read-only right now.' });
    assert.equal(widget.value(), 3.5, 'the change waiting for its save is dropped; the one in flight is shown');
    assert.equal(shown.textContent, 'Saving…');
    answers[0].resolve({});
    await page.advance(60);
    assert.deepEqual([widget.value(), widget.savedValue()], [3.5, 3.5]);
    assert.deepEqual(fills(slider), ['100%', '100%', '100%', '50%', '0%']);
    assert.equal(shown.textContent, '3.5');
    // Failed while disabled: back to the saved value, both.
    widget.setDisabled(null);
    page.key(slider, 'End');
    page.key(slider, 'Enter');
    widget.setDisabled({ reason: 'ReadOnly' });
    answers[1].reject(new Error('read-only'));
    await page.advance(60);
    assert.deepEqual([widget.value(), widget.savedValue()], [3.5, 3.5]);
    await page.advance(1000);
    assert.deepEqual(commits, [3.5, 5], 'nothing sent while disabled');
});

test('destroy makes the widget inert: its listeners gone, no input, setValue, setDisabled or late answer changes anything', { skip }, async () => {
    const page = await load({ layout: 'mobile' });
    const { widget, slider, commits, answers } = mount(page, { value: 2 });
    const [minus, , plus] = widget.element.children;
    const live = widget.element.querySelector('.jfur-stars-live');
    page.key(slider, 'ArrowRight');
    page.key(slider, 'Enter');
    // A touch under way when the page goes.
    touch(page, slider, 'pointerdown', 250);
    touch(page, slider, 'pointermove', 280);
    widget.destroy();
    assert.equal(slider._listeners.length, 0, 'the slider\'s listeners removed');
    assert.equal(minus._listeners.length + plus._listeners.length, 0, 'the buttons\' too');
    assert.deepEqual(page.document._listeners.map((l) => l.type).filter((t) => /pointer/.test(t)), [], 'and the document\'s for the touch');
    const before = [widget.value(), aria(slider), fills(slider)];
    page.key(slider, 'End');
    page.key(slider, 'Enter');
    pointer(page, slider, 'pointerdown', { clientX: 110 });
    pointer(page, slider, 'pointerup', { clientX: 110 });
    touch(page, slider, 'pointerup', 280);
    plus.click();
    minus.click();
    widget.setValue(4);
    widget.setDisabled({ reason: 'ReadOnly', note: 'x' });
    widget.flush();
    answers[0].resolve({});
    await page.advance(2000);
    assert.deepEqual([widget.value(), aria(slider), fills(slider)], before);
    assert.deepEqual(commits, [2.5], 'no save after destroy');
    assert.equal(live.textContent, '', 'nothing announced');
    assert.equal(widget.element.querySelector('.jfur-stars-note').textContent, '');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('pen: the slider captures it, and a scrub that overshoots and lifts off the stars still ends, saving the end', { skip }, async () => {
    const page = await load();
    const { widget, slider, commits } = mount(page, { value: 1 });
    const captured = [];
    slider.setPointerCapture = (id) => captured.push(['set', id]);
    slider.hasPointerCapture = (id) => captured.some((c) => c[1] === id);
    slider.releasePointerCapture = (id) => captured.push(['release', id]);
    const pen = (el, type, x, y) => pointer(page, el, type, { pointerType: 'pen', pointerId: 4, clientX: x, clientY: y === undefined ? 220 : y });
    pen(slider, 'pointerdown', 100 + 1.75 * 40);
    assert.deepEqual(captured, [['set', 4]], 'setPointerCapture on a pen\'s pointerdown');
    for (const x of [185, 220, 260, 300]) {
        pen(slider, 'pointermove', x);
    }

    // Past the right edge the browser gives the events to what is under the pen when nothing captured it: the body here.
    pen(page.document.body, 'pointermove', 360);
    assert.equal(widget.value(), 5, 'the scrub follows the pen past the edge (clamped)');
    pen(page.document.body, 'pointerup', 380);
    assert.equal(widget.isPending(), true, 'the lift off the stars ends the scrub');
    await page.advance(600);
    assert.deepEqual(commits, [5]);
    assert.deepEqual(page.document._listeners.map((l) => l.type).filter((t) => /pointer/.test(t)), [], 'the document stops following it');
    // A mouse is never captured.
    pointer(page, slider, 'pointerdown', { clientX: 110 });
    assert.deepEqual(captured.filter((c) => c[0] === 'set'), [['set', 4]]);
});

test('pen: a change waiting for its save is kept through a pen gesture lifted elsewhere, and a lost capture is a cancel', { skip }, async () => {
    const page = await load();
    const { widget, slider, commits } = mount(page, { value: 3 });
    const pen = (el, type, x, y) => pointer(page, el, type, { pointerType: 'pen', pointerId: 9, clientX: x, clientY: y === undefined ? 220 : y });
    // Right (3.5, its save waiting), then the pen comes down, goes mostly down, and is lifted outside the stars.
    page.key(slider, 'ArrowRight');
    await page.advance(300);
    pen(slider, 'pointerdown', 150);
    pen(slider, 'pointermove', 152, 250);
    pen(page.document.body, 'pointermove', 153, 320);
    await page.advance(1000);
    assert.deepEqual(commits, [], 'paused while the pen is down');
    pen(page.document.body, 'pointerup', 153, 330);
    assert.equal(widget.value(), 3.5, 'the vertical pen move changed nothing');
    await page.advance(599);
    assert.deepEqual(commits, []);
    await page.advance(1);
    assert.deepEqual(commits, [3.5], 'the change from the keyboard went on after the pen lifted');
    // A scrub whose capture is lost without a lift (the slider taken from it): the value from before, nothing saved.
    pen(slider, 'pointerdown', 280);
    pen(slider, 'pointermove', 240);
    pen(slider, 'pointermove', 200);
    assert.equal(widget.value(), 2.5);
    pointer(page, slider, 'lostpointercapture', { pointerType: 'pen', pointerId: 9 });
    assert.equal(widget.value(), 3.5, 'a lost capture is a cancel');
    pen(slider, 'pointerup', 200);
    await page.advance(1000);
    assert.deepEqual(commits, [3.5]);
    assert.deepEqual(page.document._listeners.map((l) => l.type).filter((t) => /pointer/.test(t)), []);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('read-only stars: role="img" with "Rated 3.5 out of 5" or the page\'s label, any value drawn exactly, not focusable', { skip }, async () => {
    const page = await load();
    const { renderStars } = page.stars;
    const rated = renderStars(3.5);
    assert.equal(rated.className, 'jfur-stars-display');
    assert.equal(rated.getAttribute('role'), 'img');
    assert.equal(rated.getAttribute('aria-label'), 'Rated 3.5 out of 5');
    assert.equal(rated.getAttribute('data-jfur-stars'), '3.5');
    assert.deepEqual(fills(rated), ['100%', '100%', '100%', '50%', '0%']);
    assert.equal(rated.hasAttribute('tabindex'), false);
    assert.equal(rated.classList.contains('focusable'), false);
    const average = renderStars(4.2, { label: 'Member average 4.2 out of 5, 7 ratings', showValue: true, className: 'jfur-summary-stars' });
    assert.equal(average.className, 'jfur-stars-display jfur-summary-stars');
    assert.equal(average.getAttribute('aria-label'), 'Member average 4.2 out of 5, 7 ratings');
    assert.deepEqual(fills(average), ['100%', '100%', '100%', '100%', '20%'], '4.2 draws 4 full stars and a 20 % fill');
    const number = average.querySelector('.jfur-stars-display-value');
    assert.equal(number.textContent, '4.2');
    assert.deepEqual([number.className, number.hasAttribute('dir'), number.getAttribute('aria-hidden')], ['jfur-stars-display-value jfur-isolate', false, 'true'], 'isolated, in the page\'s direction');
    const none = renderStars(null);
    assert.equal(none.getAttribute('aria-label'), 'Not rated');
    assert.deepEqual(fills(none), ['0%', '0%', '0%', '0%', '0%']);
    assert.equal(renderStars(9).getAttribute('aria-label'), 'Rated 5 out of 5');
    assert.equal(renderStars(0, { showValue: true }).querySelectorAll('.jfur-stars-display-value').length, 0);
});

test('every text the widget shows is bidi-isolated; a note with an unclosed override stays inside its own element', { skip }, async () => {
    const page = await load();
    const { widget } = mount(page, { value: 2 });
    // An Arabic name with a right-to-left override (U+202E) that the text never closes, as a member may type it.
    const name = 'سارة ‮abc';
    widget.setDisabled({ reason: 'NotARater', note: 'Only ' + name + ' can rate here' });
    const texts = widget.element.querySelectorAll('.jfur-stars-value, .jfur-stars-note');
    assert.equal(texts.length, 2);
    for (const t of texts) {
        assert.ok(t.classList.contains('jfur-isolate'), t.className);
        assert.equal(t.children.length, 0, 'text only, never markup');
    }

    const [shown, note] = texts;
    assert.equal(note.getAttribute('dir'), 'auto', 'the note, which can carry a member\'s words, takes its own direction');
    assert.equal(shown.hasAttribute('dir'), false, 'our number keeps the page\'s direction, beside the stars');
    assert.equal(note.textContent, 'Only ' + name + ' can rate here');
    assert.equal(widget.element.querySelector('.jfur-stars-note').nextElementSibling.className, 'jfur-sr-only jfur-stars-live', 'nothing of ours follows inside the note');
    const css = page.document.querySelector('#jfur-styles').textContent;
    assert.ok(css.includes('.jfur-isolate{unicode-bidi:isolate}'));
});

test('the styles: pan-y pinch-zoom on the stars, − / + only at (pointer: coarse) and never on TV, focus rings, reduced motion, forced colours', { skip }, async () => {
    const page = await load();
    const css = page.document.querySelector('#jfur-styles').textContent;
    for (const rule of [
        '.jfur-stars{touch-action:pan-y pinch-zoom;',
        '.jfur-stars-step{display:none;',
        '@media (pointer:coarse){.jfur-rating{--jfur-star-size:40px}.jfur-stars-step{display:inline-flex}}',
        '.jfur-rating-tv .jfur-stars-step{display:none}',
        '.jfur-rating-touch{--jfur-star-size:40px}',
        '.jfur-stars:focus-visible{outline:2px solid var(--jfur-accent);outline-offset:3px}',
        '.jfur-rating-tv .jfur-stars:focus{outline:3px solid var(--jfur-accent);outline-offset:4px}',
        '.jfur-rating-tv .jfur-stars-value{font-size:1.3em}',
        '.jfur-star polygon{stroke:currentColor;',
        '@media (prefers-reduced-motion:reduce){.jfur-star-fill{transition:none}}',
        '@media (forced-colors:active){',
        '.jfur-star-on polygon{fill:CanvasText}'
    ]) {
        assert.ok(css.includes(rule), rule);
    }

    // User Ratings' own rules, without their comments.
    const ours = css.substring(css.indexOf('/* User Ratings')).replace(/\/\*[\s\S]*?\*\//g, '');
    assert.equal(/--jf-palette-(background|text)|contrastText/.test(ours), false, 'never the MUI surface or text variables');
    assert.equal(/:hover/.test(ours), false, 'no hover style a touch could leave stuck');
    // Every class the styles name is User Ratings' own.
    const classes = [...ours.matchAll(/(?<![\w)])\.(-?[A-Za-z_][\w-]*)/g)].map((m) => m[1]);
    assert.ok(classes.length > 20);
    assert.deepEqual(classes.filter((c) => !c.startsWith('jfur-')), []);
});
