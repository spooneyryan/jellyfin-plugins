/*
 * The star widget (30-stars.js): five stars in half steps, 0.5 to 5, with 0 for "not rated"
 * (unrated is no value, never a zero-star rating).
 *
 *   - renderStars(value, { label, showValue, className }): read-only stars for a summary or another member's rating,
 *     role="img" with "Rated 3.5 out of 5" (or the caller's label); any value from 0 to 5 is drawn exactly (4.2: four
 *     stars and a fifth filled to 20 %).
 *   - createStarSlider({ value, labelledBy | label, onCommit, onChange, debounceMs }): the member's own rating, one tab stop
 *     <div role="slider" class="jfur-stars focusable"> (APG slider; jellyfin-web's TV focus manager finds it by the class),
 *     with the − / + buttons beside it shown only at (pointer: coarse), the value as text, the note of a
 *     disabled state and a polite live region. It never calls the API: a change of value is handed to onCommit(value)
 *     600 ms after the last change, or at once on Enter or Space; onCommit returns the save's promise (the details page
 *     sends PUT Mine/Rating). While it is pending the text reads "Saving…"; when it resolves the live region says
 *     "Saved: 3.5 stars"; when it rejects the stars go back to the last saved value (the page shows the error and Retry).
 *     One save is in flight at a time: a change made meanwhile goes when it settles, the latest value only.
 *     setValue(v) is the server's value, and an answer to a save sent before it changes nothing.
 *     Clearing is the page's Clear button (DELETE Mine/Rating), then setValue(0): the widget never sets 0 itself, and no
 *     key clears (Delete, Backspace and Escape are left alone: Escape is Back on the TV layout, Backspace on VIDAA).
 *     setDisabled({ reason, note }) carries the page's reason (Can.Reason NeedsPlay or ReadOnly; NotARater gets
 *     read-only stars, renderStars, instead) and the sentence it explains it with; the widget decides no policy.
 *     destroy() hands a pending change on and makes the widget inert.
 *
 * Inputs:
 *   - mouse: hovering previews the value under the pointer, a click sets it (clicking the current value does nothing),
 *     leaving shows the value again;
 *   - touch and pen: nothing on pointerdown; a tap's pointerup sets the value under the finger, a move of more than 8 px
 *     sideways scrubs (the value follows the finger, saved once it lifts, wherever it lifts: the slider captures the
 *     pointer and the document follows it), a move mostly up or down is the browser's scroll (touch-action: pan-y
 *     pinch-zoom) and its pointercancel puts back the value from before the touch, saving nothing; a change waiting for
 *     its save when the touch began is paused and goes on when it ends, however it ends;
 *   - keyboard: Right/Up +0.5, Left/Down -0.5 (never below 0.5, and never rating an unrated item), PageUp/PageDown ±1,
 *     Home 0.5, End 5, Enter/Space save now; preventDefault on every key it handles, so jellyfin-web's window handler (it
 *     skips prevented keys) never moves the focus for them; with Ctrl, Alt or Meta, or already prevented, nothing;
 *   - TV layout (kit.layout.isTv()): the same, but Up and Down are never handled, so the D-pad leaves the slider; the
 *     remotes' and gamepads' D-pad codes are read as jellyfin-web reads them (kit.shortcuts.arrowOf);
 *   - right to left (the slider's computed direction): the stars fill from the right, the pointer axis is mirrored and
 *     Left/Right follow what they point at (Left +0.5), as the APG has it.
 *
 * Pure functions, kept apart from the DOM so the node tests can run them: valueFromPointer, stepValue,
 * formatStars, clipPercent, starsValueText, starGesture. Every text the widget shows sits in its own bidi-isolated element
 * (unicode-bidi: isolate), and the note, which may carry a member's name, also takes its direction from its own
 * text (dir="auto").
 */

// Half-star units: 0.5 is the lowest rating, 5 the highest.
const STARS_MAX = 5;
const STARS_STEP = 0.5;
// Sideways or up-and-down travel (px) that makes a touch a scrub or a scroll instead of a tap ("a few pixels").
const STARS_DRAG_PX = 8;
// A change is handed to onCommit this long after the last one.
const STARS_DEBOUNCE_MS = 600;
const STARS_SVG_NS = 'http://www.w3.org/2000/svg';
// A five-pointed star in a 24-unit box, outer radius 11, inner 4.6, its centre a little low so it sits in the middle.
const STARS_POINTS = (function () {
    const points = [];
    for (let i = 0; i < 10; i++) {
        const radius = i % 2 === 0 ? 11 : 4.6;
        const angle = (Math.PI / 5) * i - Math.PI / 2;
        points.push((12 + radius * Math.cos(angle)).toFixed(2) + ',' + (12.5 + radius * Math.sin(angle)).toFixed(2));
    }

    return points.join(' ');
})();
let starsCount = 0;

// A value as the widget keeps it: 0 (not rated) or 0.5 to 5 in halves.
function normalizeStars(value) {
    const n = Number(value);
    if (!isFinite(n) || n <= 0) {
        return 0;
    }

    return Math.min(STARS_MAX, Math.max(STARS_STEP, Math.round(n * 2) / 2));
}

// The value under a pointer x pixels from the stars' left edge, the row being width wide: the half star the pointer is
// in, rounded up (ceil(x / starWidth × 2) / 2), 0.5 to 5. A pointer exactly on a half boundary belongs to the lower half,
// so the middle of star 3 is 2.5. Right to left, x is measured from the right edge. Null when the row
// has no width or x is no number.
function valueFromPointer(x, width, rtl) {
    const w = Number(width);
    const px = Number(x);
    if (!(w > 0) || !isFinite(px)) {
        return null;
    }

    const along = rtl ? w - px : px;
    // In half stars; the tiny margin keeps a boundary that floating point puts a hair above it in the lower half.
    const halves = Math.ceil((along * STARS_MAX * 2) / w - 1e-9);
    return Math.min(STARS_MAX, Math.max(STARS_STEP, halves / 2));
}

// The value a key gives, or null when the key isn't the widget's and is left to the page.
// key is KeyboardEvent.key, arrows as jellyfin-web reads them; layout 'tv' leaves ArrowUp and ArrowDown to jellyfin-web's
// spatial navigation. A decrease never rates an unrated item (it stays 0), and no key goes below 0.5 or above 5.
function stepValue(value, key, layout, rtl) {
    const v = normalizeStars(value);
    let delta;
    switch (key) {
        case 'ArrowRight':
            delta = rtl ? -STARS_STEP : STARS_STEP;
            break;
        case 'ArrowLeft':
            delta = rtl ? STARS_STEP : -STARS_STEP;
            break;
        case 'ArrowUp':
            if (layout === 'tv') {
                return null;
            }

            delta = STARS_STEP;
            break;
        case 'ArrowDown':
            if (layout === 'tv') {
                return null;
            }

            delta = -STARS_STEP;
            break;
        case 'PageUp':
            delta = 1;
            break;
        case 'PageDown':
            delta = -1;
            break;
        case 'Home':
            return STARS_STEP;
        case 'End':
            return STARS_MAX;
        default:
            return null;
    }

    if (delta < 0 && v === 0) {
        return 0;
    }

    return Math.min(STARS_MAX, Math.max(STARS_STEP, v + delta));
}

// A value as text: '3.5', '4', '4.2' (an average, to one decimal); with decimals 1 always one decimal ('4.0'). '' for no
// value.
function formatStars(value, decimals) {
    const n = Number(value);
    if (!isFinite(n) || n <= 0) {
        return '';
    }

    const rounded = Math.round(Math.min(STARS_MAX, n) * 10) / 10;
    return decimals === 1 ? rounded.toFixed(1) : String(rounded);
}

// The slider's aria-valuetext: "3.5 out of 5 stars", or "Not rated".
function starsValueText(value) {
    const v = normalizeStars(value);
    return v === 0 ? kit.t('stars.notRated') : kit.t('stars.valueText', { stars: formatStars(v) });
}

// How much of star starIndex (0 to 4) a value fills, in percent to a tenth: clipPercent(4.2, 4) is 20.
function clipPercent(value, starIndex) {
    const n = Number(value);
    const fill = isFinite(n) ? Math.max(0, Math.min(1, n - starIndex)) : 0;
    return Math.round(fill * 1000) / 10;
}

// One step of a touch or pen on the stars, as a pure state transition:
//   state   null, or { startX, startY, before, mode: 'press' | 'scrub' | 'pan' } from the last step;
//   input   { type: 'down' | 'move' | 'up' | 'cancel', x, y, value (the value under the finger), current (the value now) }.
// Returns { state, value, commit }: value is what the stars show from now on (null: unchanged), commit whether the
// change is the member's (a tap, or the end of a scrub) and so is saved. Down changes nothing; a move past STARS_DRAG_PX
// sideways (more than up or down) starts a scrub, which follows the finger; one mostly up or down is a pan, the
// browser's scroll, ignored until the browser cancels it; up after no scrub is a tap; cancel puts back the value from
// before the touch.
function starGesture(state, input) {
    const none = { state: state, value: null, commit: false };
    switch (input.type) {
        case 'down':
            return { state: { startX: input.x, startY: input.y, before: input.current, mode: 'press' }, value: null, commit: false };
        case 'move': {
            if (!state || state.mode === 'pan') {
                return none;
            }

            if (state.mode === 'press') {
                const dx = Math.abs(input.x - state.startX);
                const dy = Math.abs(input.y - state.startY);
                if (dx > STARS_DRAG_PX && dx > dy) {
                    return { state: Object.assign({}, state, { mode: 'scrub' }), value: input.value, commit: false };
                }

                if (dy > STARS_DRAG_PX && dy >= dx) {
                    return { state: Object.assign({}, state, { mode: 'pan' }), value: null, commit: false };
                }

                return none;
            }

            return { state: state, value: input.value, commit: false };
        }
        case 'up':
            if (!state || state.mode === 'pan') {
                return { state: null, value: null, commit: false };
            }

            return { state: null, value: input.value, commit: true };
        case 'cancel':
            return { state: null, value: state ? state.before : null, commit: false };
        default:
            return none;
    }
}

// The direction the stars run in: the computed direction, else the nearest dir attribute.
function starsRtl(el) {
    try {
        if (typeof window.getComputedStyle === 'function') {
            const direction = window.getComputedStyle(el).direction;
            if (direction) {
                return direction === 'rtl';
            }
        }
    } catch (e) {
        // no computed style (a detached node in an old browser): the attribute below
    }

    for (let node = el; node && typeof node.getAttribute === 'function'; node = node.parentNode) {
        const dir = node.getAttribute('dir');
        if (dir === 'rtl' || dir === 'ltr') {
            return dir === 'rtl';
        }
    }

    return false;
}

function starsSvg(className) {
    const svg = document.createElementNS(STARS_SVG_NS, 'svg');
    svg.setAttribute('class', className);
    svg.setAttribute('viewBox', '0 0 24 24');
    // Hidden from screen readers (the slider's aria values, or the img's label, say it); an inline SVG is no tab stop in
    // any browser jellyfin-web 10.11 runs in.
    svg.setAttribute('aria-hidden', 'true');
    const shape = document.createElementNS(STARS_SVG_NS, 'polygon');
    shape.setAttribute('points', STARS_POINTS);
    svg.appendChild(shape);
    return svg;
}

// Five stars, each an outline with a filled copy over it in a box as wide as the fill: the box is
// anchored at the start edge, so right to left it fills from the right without a line of script.
function appendStars(parent) {
    const fills = [];
    for (let i = 0; i < STARS_MAX; i++) {
        const fill = kit.h('span', { class: 'jfur-star-fill' }, [starsSvg('jfur-star-on')]);
        parent.append(kit.h('span', { class: 'jfur-star' }, [starsSvg('jfur-star-off'), fill]));
        fills.push(fill);
    }

    return fills;
}

function fillStars(fills, value) {
    fills.forEach(function (fill, i) {
        fill.style.width = clipPercent(value, i) + '%';
    });
}

// Text the widget shows, in its own bidi-isolated element, with dir="auto" unless attrs says otherwise: a
// number of ours ('3.5') takes the page's direction instead, so right to left it keeps to the stars' side of its box.
function starsText(className, text, attrs) {
    return kit.h('span', Object.assign({ class: className + ' jfur-isolate', dir: 'auto', text: text }, attrs || {}));
}

// Read-only stars: role="img", "Rated 3.5 out of 5" or opts.label (a summary's own
// sentence, "Member average 4.2 out of 5, 7 ratings"); opts.showValue adds the number as text ('4.2', one decimal for a
// value that isn't a half).
function renderStars(value, opts) {
    opts = opts || {};
    const n = Number(value);
    const v = isFinite(n) ? Math.max(0, Math.min(STARS_MAX, n)) : 0;
    const shown = formatStars(v);
    const label = opts.label ? String(opts.label) : (v > 0 ? kit.t('stars.rated', { stars: shown }) : kit.t('stars.notRated'));
    const root = kit.h('span', {
        class: 'jfur-stars-display' + (opts.className ? ' ' + opts.className : ''),
        role: 'img',
        'aria-label': label,
        'data-jfur-stars': shown || '0'
    });
    fillStars(appendStars(root), v);
    if (opts.showValue && shown) {
        root.append(starsText('jfur-stars-display-value', shown, { 'aria-hidden': 'true', dir: null }));
    }

    return root;
}

// The member's own rating. Returns { element, slider, value(), savedValue(), isPending(), isSaving(),
// setValue(v), setDisabled(state), flush(), announce(text), destroy() }:
//   element      the row to insert: [−] [the slider] [+] [the value as text] [the note] [the live region];
//   setValue(v)  the saved value as the server has it (0 for none), dropping any change not handed on yet; the answer to a
//                save sent before it changes nothing when it comes (without this, the page's Clear, or a refresh,
//                then the PUT's late answer, left "Not rated" shown and 3.5 as the saved value);
//   setDisabled({ reason, note }) or setDisabled(null): no input changes the value, the slider says aria-disabled, carries
//                data-jfur-reason and shows the note, which it is described by (aria-describedby; the note itself is
//                hidden from screen readers, so it is read once); it stays a tab stop, so the note can be read, and leaves
//                every key to the page, so the D-pad moves on. During a save the stars show the value being saved, and
//                its answer settles both: saved, or put back;
//   flush()      hands a pending change to onCommit now; destroy() does the same, then makes the widget inert: its
//                listeners removed, no timer, nothing an answer, setValue or setDisabled does any more;
//   announce(t)  says t in the live region ("Rating cleared", an error).
// One save at a time: a change made while one is in flight waits, and when that one settles the latest value goes, if
// it isn't the saved one by then; Enter twice sends once.
function createStarSlider(opts) {
    opts = opts || {};
    const id = 'jfur-stars-' + (++starsCount);
    const tv = kit.layout.isTv();
    const debounceMs = typeof opts.debounceMs === 'number' ? opts.debounceMs : STARS_DEBOUNCE_MS;
    const state = {
        saved: normalizeStars(opts.value),
        value: normalizeStars(opts.value),
        preview: null,
        gesture: null,
        pointerId: null,
        mouseDown: false,
        timer: null,
        // The save onCommit has and hasn't answered for, { value, generation }, or null: one at a time.
        inflight: null,
        // Raised by setValue and destroy: an answer to a save sent before it is about a value the page has since replaced.
        generation: 0,
        // The change pending at destroy, handed on once the save in flight then settles (null: none).
        handOff: null,
        disabled: null,
        destroyed: false
    };

    const slider = kit.makeFocusable(kit.h('div', {
        class: 'jfur-stars',
        role: 'slider',
        'aria-valuemin': '0',
        'aria-valuemax': String(STARS_MAX),
        'aria-labelledby': opts.labelledBy || null,
        'aria-label': opts.labelledBy ? null : (opts.label || kit.t('stars.yourRating'))
    }));
    const fills = appendStars(slider);
    const minus = kit.h('button', { type: 'button', class: 'jfur-stars-step jfur-stars-minus', title: kit.t('stars.less'), 'aria-label': kit.t('stars.less') }, kit.icon('remove'));
    const plus = kit.h('button', { type: 'button', class: 'jfur-stars-step jfur-stars-plus', title: kit.t('stars.more'), 'aria-label': kit.t('stars.more') }, kit.icon('add'));
    // The value is the slider's aria-valuetext already, so its text is for the eyes only. The note can carry a member's
    // words (a name), so it takes its direction from its own text (dir="auto"); screen readers hear it once, as the
    // slider's description (it was once also its title and in the reading order, so it was read twice).
    const valueText = starsText('jfur-stars-value', '', { 'aria-hidden': 'true', dir: null });
    const note = starsText('jfur-stars-note', '', { id: id + '-note', 'aria-hidden': 'true' });
    const live = kit.h('span', { class: 'jfur-sr-only jfur-stars-live', role: 'status', 'aria-live': 'polite', 'aria-atomic': 'true' });
    kit.setHidden(note, true);
    const element = kit.h('div', {
        class: 'jfur-rating' + (tv ? ' jfur-rating-tv' : '') + (kit.layout.isMobile() ? ' jfur-rating-touch' : ''),
        'data-jfur-stars-id': id
    }, [minus, slider, plus, valueText, note, live]);

    // The save in flight for the value as the page has it now (not one sent before a setValue).
    function current() {
        return state.inflight !== null && state.inflight.generation === state.generation ? state.inflight : null;
    }

    function render() {
        if (state.destroyed) {
            return;
        }

        const shown = state.preview !== null ? state.preview : state.value;
        fillStars(fills, shown);
        slider.setAttribute('aria-valuenow', String(state.value));
        slider.setAttribute('aria-valuetext', starsValueText(state.value));
        // "Saving…" while the value shown is the one onCommit has and hasn't answered for; a hover elsewhere shows its own.
        const saving = current() !== null && !state.timer && state.value === state.inflight.value;
        const previewing = state.preview !== null && state.preview !== state.value;
        valueText.textContent = previewing ? formatStars(state.preview)
            : (saving ? kit.t('stars.saving') : (shown > 0 ? formatStars(shown) : kit.t('stars.notRated')));
        minus.disabled = !!state.disabled || state.value <= STARS_STEP;
        plus.disabled = !!state.disabled || state.value >= STARS_MAX;
        element.classList.toggle('jfur-rating-preview', previewing);
        element.classList.toggle('jfur-rating-saving', saving);
    }

    function clearTimer() {
        if (state.timer) {
            clearTimeout(state.timer);
            state.timer = null;
        }
    }

    // Arms the debounce for a value that differs from the saved one (and drops it for one that doesn't).
    function schedule() {
        clearTimer();
        if (!state.destroyed && state.value !== state.saved && state.value > 0 && !state.disabled && state.gesture === null) {
            state.timer = setTimeout(commit, debounceMs);
        }
    }

    function commit() {
        clearTimer();
        if (state.destroyed || state.value === state.saved || state.value === 0 || state.disabled || typeof opts.onCommit !== 'function') {
            render();
            return;
        }

        if (state.inflight !== null) {
            // One save at a time: the latest value goes once this one settles (settled).
            render();
            return;
        }

        send(state.value);
    }

    function send(value) {
        const sent = { value: value, generation: state.generation };
        state.inflight = sent;
        render();
        let result;
        try {
            result = opts.onCommit(value);
        } catch (e) {
            result = Promise.reject(e);
        }

        Promise.resolve(result).then(function () {
            settled(sent, true);
        }, function () {
            settled(sent, false);
        });
    }

    function settled(sent, ok) {
        if (state.inflight === sent) {
            state.inflight = null;
        }

        if (state.destroyed) {
            // Inert: only the change pending at destroy still goes, now that the save before it settled.
            const handOff = state.handOff;
            state.handOff = null;
            if (handOff !== null && (!ok || handOff !== sent.value)) {
                send(handOff);
            }

            return;
        }

        if (sent.generation === state.generation) {
            if (ok) {
                state.saved = sent.value;
                if (state.disabled) {
                    // The stars showed the value being saved: now it is the saved one too.
                    state.value = sent.value;
                }

                if (state.value === sent.value) {
                    announce(sent.value === 1 ? kit.t('stars.savedOne') : kit.t('stars.saved', { stars: formatStars(sent.value) }));
                }
            } else if (state.disabled || (state.value === sent.value && !state.timer && state.gesture === null)) {
                // Back to the last saved value, unless the member has changed it again since (that change saves itself).
                state.value = state.saved;
            }
        }

        // The latest value, if it changed meanwhile and nothing else hands it on (no debounce running, no touch under way).
        if (!state.timer && state.gesture === null && !state.disabled && state.value > 0 && state.value !== state.saved && typeof opts.onCommit === 'function') {
            send(state.value);
            return;
        }

        render();
    }

    // The value shown and announced as the slider's, without arming the debounce (a scrub under way, a touch put back);
    // onChange(value, how) hears every change.
    function show(next, how) {
        const v = normalizeStars(next);
        if (v !== state.value) {
            state.value = v;
            if (typeof opts.onChange === 'function') {
                try {
                    opts.onChange(v, how);
                } catch (e) {
                    kit.report(e);
                }
            }
        }

        render();
    }

    // A change the member made: shown at once, handed on after the debounce. The widget never sets 0 itself.
    function change(next, how) {
        if (normalizeStars(next) === 0 || state.disabled || state.destroyed) {
            return;
        }

        show(next, how);
        schedule();
    }

    function announce(text) {
        if (state.destroyed) {
            return;
        }

        // Emptied first, so the same sentence twice is announced twice.
        live.textContent = '';
        setTimeout(function () {
            live.textContent = String(text || '');
        }, 50);
    }

    function valueAt(event) {
        const rect = slider.getBoundingClientRect();
        return valueFromPointer(event.clientX - rect.left, rect.width, starsRtl(slider));
    }

    function onKeyDown(event) {
        if (state.disabled || state.destroyed || event.defaultPrevented || event.ctrlKey || event.altKey || event.metaKey || event.isComposing) {
            return;
        }

        const key = kit.shortcuts.arrowOf(event) || event.key;
        if (key === 'Enter' || key === ' ' || key === 'Spacebar') {
            event.preventDefault();
            commit();
            return;
        }

        const next = stepValue(state.value, key, tv ? 'tv' : 'desktop', starsRtl(slider));
        if (next === null) {
            return;
        }

        event.preventDefault();
        change(next, 'key');
    }

    // Touch and pen: one finger at a time, through starGesture.
    function gesture(type, event) {
        const scrubbing = !!state.gesture && state.gesture.mode === 'scrub';
        const out = starGesture(state.gesture, {
            type: type,
            x: event.clientX,
            y: event.clientY,
            value: type === 'cancel' ? null : valueAt(event),
            current: state.value
        });
        state.gesture = out.state;
        if (out.commit && out.value !== null) {
            change(out.value, scrubbing ? 'drag' : 'tap');
            return;
        }

        if (out.value !== null) {
            // A scrub follows the finger; a cancel puts back the value from before the touch (0 included).
            show(out.value, type === 'cancel' ? 'cancel' : 'drag');
        }

        if (state.gesture === null) {
            // The touch is over without a change of its own: a change from before it is handed on as it was.
            schedule();
        }
    }

    // The touch or pen under way is followed wherever its events go: the slider captures the pointer
    // (setPointerCapture), and the document's listeners, in the capture phase, take its moves, its lift and its cancel even
    // when the capture failed or the browser has none, so a pen lifted off the stars still ends the gesture. A lost capture
    // without a lift (the slider taken out of the page, another element taking the pointer) is a cancel: the value from
    // before the touch comes back and a change waiting from before it goes on, since nothing the member let go of decided it.
    function follow(event) {
        state.pointerId = event.pointerId;
        try {
            if (typeof slider.setPointerCapture === 'function') {
                slider.setPointerCapture(event.pointerId);
            }
        } catch (e) {
            // Not an active pointer any more, or no capture here: the document's listeners below follow it.
        }

        ['pointermove', 'pointerup', 'pointercancel', 'lostpointercapture'].forEach(function (type) {
            document.addEventListener(type, onActivePointer, true);
        });
    }

    function stopFollowing() {
        const pointerId = state.pointerId;
        state.pointerId = null;
        ['pointermove', 'pointerup', 'pointercancel', 'lostpointercapture'].forEach(function (type) {
            document.removeEventListener(type, onActivePointer, true);
        });
        try {
            if (pointerId !== null && typeof slider.hasPointerCapture === 'function' && slider.hasPointerCapture(pointerId)) {
                slider.releasePointerCapture(pointerId);
            }
        } catch (e) {
            // Released already.
        }
    }

    function onActivePointer(event) {
        if (state.pointerId === null || event.pointerId !== state.pointerId) {
            return;
        }

        if (event.type === 'pointermove') {
            gesture('move', event);
            return;
        }

        stopFollowing();
        gesture(event.type === 'pointerup' ? 'up' : 'cancel', event);
    }

    // A touch or pen ended by the page (setValue, setDisabled, destroy): nothing of it is kept.
    function dropGesture() {
        if (state.pointerId !== null) {
            stopFollowing();
        }

        state.gesture = null;
    }

    function onPointerDown(event) {
        if (state.disabled || state.destroyed) {
            return;
        }

        if (event.pointerType === 'mouse') {
            state.mouseDown = event.button === 0;
            return;
        }

        if (state.pointerId !== null) {
            return;
        }

        // A change waiting for its save (a key, a click) is paused, and handed on as it was when the gesture ends, however
        // it ends (its timer starts again there, or the gesture's own change replaces it).
        clearTimer();
        follow(event);
        gesture('down', event);
    }

    function onPointerMove(event) {
        if (!state.disabled && !state.destroyed && event.pointerType === 'mouse') {
            const v = valueAt(event);
            if (v !== null && v !== state.preview) {
                state.preview = v;
                render();
            }
        }
    }

    function onPointerUp(event) {
        if (event.pointerType === 'mouse') {
            const click = state.mouseDown && event.button === 0 && !state.disabled;
            state.mouseDown = false;
            if (click) {
                change(valueAt(event), 'click');
            }
        }
    }

    function onPointerCancel(event) {
        if (event.pointerType === 'mouse') {
            state.mouseDown = false;
            state.preview = null;
            render();
        }
    }

    function onPointerLeave(event) {
        if (event.pointerType === 'mouse' && state.preview !== null) {
            state.preview = null;
            state.mouseDown = false;
            render();
        }
    }

    function onMinus() {
        if (state.value > STARS_STEP) {
            change(state.value - STARS_STEP, 'minus');
        }
    }

    function onPlus() {
        change(state.value + STARS_STEP, 'plus');
    }

    const listeners = [
        [slider, 'keydown', onKeyDown],
        [slider, 'pointerdown', onPointerDown],
        [slider, 'pointermove', onPointerMove],
        [slider, 'pointerup', onPointerUp],
        [slider, 'pointercancel', onPointerCancel],
        [slider, 'pointerleave', onPointerLeave],
        [minus, 'click', onMinus],
        [plus, 'click', onPlus]
    ];
    listeners.forEach(function (l) {
        l[0].addEventListener(l[1], l[2]);
    });

    render();
    return {
        element: element,
        slider: slider,
        value: function () {
            return state.value;
        },
        savedValue: function () {
            return state.saved;
        },
        isPending: function () {
            return state.timer !== null;
        },
        isSaving: function () {
            return state.inflight !== null;
        },
        setValue: function (value) {
            if (state.destroyed) {
                return;
            }

            clearTimer();
            dropGesture();
            state.preview = null;
            state.generation++;
            state.saved = normalizeStars(value);
            state.value = state.saved;
            render();
        },
        setDisabled: function (disabled) {
            if (state.destroyed) {
                return;
            }

            if (disabled) {
                clearTimer();
                dropGesture();
                state.preview = null;
                state.mouseDown = false;
                // A change not handed on yet is dropped; one being saved is shown until its answer settles it.
                state.value = current() !== null ? state.inflight.value : state.saved;
                state.disabled = { reason: String(disabled.reason || ''), note: disabled.note ? String(disabled.note) : '' };
                slider.setAttribute('aria-disabled', 'true');
                slider.setAttribute('data-jfur-reason', state.disabled.reason);
                note.textContent = state.disabled.note;
                kit.setHidden(note, !state.disabled.note);
                if (state.disabled.note) {
                    slider.setAttribute('aria-describedby', note.id);
                } else {
                    slider.removeAttribute('aria-describedby');
                }
            } else {
                state.disabled = null;
                slider.removeAttribute('aria-disabled');
                slider.removeAttribute('data-jfur-reason');
                slider.removeAttribute('aria-describedby');
                note.textContent = '';
                kit.setHidden(note, true);
            }

            element.classList.toggle('jfur-rating-disabled', !!state.disabled);
            render();
        },
        flush: function () {
            if (state.timer) {
                commit();
            }
        },
        announce: announce,
        destroy: function () {
            if (state.destroyed) {
                return;
            }

            // The change not handed on yet (a debounce running, one paused by a touch, or one waiting for the save in
            // flight): it still goes, at once, or once that save settles, so the server gets them in order. Then nothing.
            const pending = state.gesture !== null ? state.gesture.before : state.value;
            const unsaved = pending > 0 && pending !== state.saved && !(current() !== null && state.inflight.value === pending)
                && !state.disabled && typeof opts.onCommit === 'function';
            clearTimer();
            dropGesture();
            listeners.forEach(function (l) {
                l[0].removeEventListener(l[1], l[2]);
            });
            state.destroyed = true;
            state.generation++;
            if (unsaved) {
                if (state.inflight !== null) {
                    state.handOff = pending;
                } else {
                    send(pending);
                }
            }
        }
    };
}
