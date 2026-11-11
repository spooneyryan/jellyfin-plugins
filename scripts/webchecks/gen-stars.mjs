// Generates User Ratings' star widget webchecks
// over probes/userratings-stars-probe.js, which mounts the real widget of the served ClientScript on a details page:
//   userratings-stars-desktop.json  default sandbox: markup and aria, Tab, every key, the mouse (the middle of star 3 is
//                                   2.5), Clear, right to left, forced colours, reduced motion, disabled, a failed save
//   userratings-stars-tv.json       default sandbox, TV layout: an earlier prototype's D-pad sequence (reach, Left/Right, OK, Up/Down out,
//                                   Back), the 3 px ring, the larger value, the disabled widget leaving the D-pad alone
//   userratings-stars-touch.json    default sandbox, mobile layout at 390x844 with touch: an earlier prototype's touches (a tap, a
//                                   sideways drag, vertical swipes that scroll and change nothing, a diagonal, − and +)
//   userratings-stars-pen.json      default sandbox: a pen (Input.dispatchMouseEvent with pointerType pen,
//                                   as DevTools sends them): a scrub that overshoots and lifts off the stars, a key's change kept through a pen
//                                   moved away, a tap
//   userratings-stars-je.json       je sandbox: Jellyfin Enhanced's version and review settings, the slider's keys next to
//                                   JE's listener, JE's keys on the slider, JE's review parts, no style rule or id of JE's
//                                   on the widget, the TV layout
// Run from the repository root: node scripts/webchecks/gen-stars.mjs [--out <folder>]
// Repo.Tests compares the files with what this writes. scripts/README.md says how to run them.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { SHORT_VERSION, VERSION } from './userratings-version.mjs';

const outIndex = process.argv.indexOf('--out');
const folder = outIndex > 0 ? path.resolve(process.argv[outIndex + 1]) : path.dirname(fileURLToPath(import.meta.url));
const IGNORE = '/CustomRatings/script|^Error: player cannot be null';
const P = '__jfurStars';
const MOVIE = (name) => `(await ApiClient.getItems(ApiClient.getCurrentUserId(), { IncludeItemTypes: 'Movie', Recursive: true, SearchTerm: ${JSON.stringify(name)}, Limit: 1 })).Items[0].Id`;
const DRAWN = (id) => `(() => { const p = document.querySelector('#itemDetailPage:not(.hide)'); return !!p && location.hash.indexOf(__vars.${id}) > 0 && !!p.querySelector('.mainDetailButtons .btnPlay:not(.hide)'); })()`;
const STATE = `${P}.state()`;
const STYLES = `${P}.styles()`;
const LAST = (kind) => `(${P}.log.filter(e => e.kind === '${kind}').pop() || null)`;
const NOTE = 'Play this first: rating it needs it to be played';

function write(name, title, steps) {
    const doc = { name: title, failOnException: false, timeout: 30000, steps };
    fs.writeFileSync(path.join(folder, name), JSON.stringify(doc, null, 2) + '\n', { encoding: 'utf8' });
}

// Signs in, sets the layout, opens a movie's page and mounts the widget there.
function start(opts) {
    const steps = [{ name: 'the star probe in every document (it mounts the served 30-stars.js)', initScript: { file: 'probes/userratings-stars-probe.js' } }];
    if (opts.je) {
        steps.push({ name: 'Jellyfin Enhanced\'s action spies', initScript: { file: 'probes/je-spy-probe.js' } });
    }

    if (opts.emulate) {
        steps.push({ name: 'the phone\'s screen and touch, before signing in', emulate: opts.emulate });
    }

    steps.push(
        { name: 'sign in as ' + opts.user, login: opts.user },
        { name: opts.layout + ' layout', eval: opts.layout === 'desktop' ? "(localStorage.removeItem('layout'), true)" : `(localStorage.setItem('layout', '${opts.layout}'), true)` },
        { name: 'reload', reload: true },
        { name: `layout-${opts.layout} applied, User Ratings' script started`, waitFor: `document.documentElement.classList.contains('layout-${opts.layout}') && !!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur)${opts.je ? ' && !!window.JellyfinEnhanced && !!window.JellyfinEnhanced.keyListener' : ''}`, timeout: 40000 },
        { name: opts.movie, eval: MOVIE(opts.movie), save: 'movieId' },
        { name: 'its details page', script: "location.hash = '#/details?id=' + __vars.movieId; return true;" },
        { name: 'drawn', waitFor: DRAWN('movieId'), timeout: 30000 },
        { sleep: opts.je ? 4000 : 1500 },
        { name: 'mount the widget (alone in its row, Clear and Write a review below, read-only stars)', eval: `${P}.mount(${JSON.stringify(opts.mount || {})})`, save: 'mounted', full: true },
        { name: 'mounted from the served ClientScript of ' + SHORT_VERSION, assert: "__vars.mounted.ok === true && __vars.mounted.version === '" + VERSION + "'", message: 'the probe could not mount the widget of User Ratings ' + SHORT_VERSION }
    );
    return steps;
}

// A key on the slider (refocused first unless asked not to), then the state, the slider's own verdict (the bubble phase
// sees it prevented when the widget handled it) and every key event of the press.
function key(label, k, opts) {
    opts = opts || {};
    const shown = k === ' ' ? 'Space' : k;
    const steps = [];
    if (opts.refocus) {
        steps.push({ name: `${label}: the slider focused`, script: `${P}.widget.slider.focus(); return ${P}.onSlider();` });
    }

    steps.push(
        { name: `${label}: mark`, script: `window.__mark = ${P}.log.length; window.__markAbs = Math.round(performance.now()) - 1; return true;` },
        { name: `${label}: ${shown}`, key: k },
        { sleep: opts.sleep || 150 },
        {
            name: `${label}: after ${shown}`,
            eval: `(() => { const since = ${P}.log.slice(window.__mark); const pick = (kind) => since.filter(e => e.kind === kind).pop() || null; const cap = pick('key-capture'); const bub = pick('key-bubble'); `
                + `return Object.assign(${STATE}, { key: ${JSON.stringify(k)}, capture: cap && { key: cap.key, prevented: cap.prevented }, bubble: bub ? { prevented: bub.prevented, target: bub.target } : 'not reached', `
                + `changes: since.filter(e => e.kind === 'change').map(e => e.value + ':' + e.how), jeCalls: window.__jeSpy ? window.__jeSpy.calls.filter(c => c.ms >= window.__markAbs).map(c => c.fn) : null }); })()`,
            save: opts.save,
            full: true
        }
    );
    if (opts.expect) {
        steps.push({ name: `${label}: ${shown} ${opts.expectName || 'as expected'}`, assert: opts.expect, message: `${shown}: ${opts.expectName || 'not as expected'}` });
    }

    return steps;
}

const after = () => `${P}.log.slice(window.__mark).filter(e => e.kind === 'key-bubble').pop()`;
// The widget handled the last key: jellyfin-web's window bubble listener saw it prevented.
const handled = () => `(${after()} || {}).prevented === true`;
const leftAlone = () => `(${after()} || { prevented: false }).prevented === false`;
const value = (v) => `${P}.widget.value() === ${v}`;
// The saves so far, the last one, and "the last save is v, made within 100 ms of key k" (Enter and Space skip the debounce).
const COMMITS = `${P}.commits.length`;
const LASTC = `(${P}.commits.length ? ${P}.commits[${P}.commits.length - 1] : { value: null, t: -1 })`;
const savedAtOnce = (v, k) => `(() => { const c = ${LASTC}; const press = ${P}.log.slice(window.__mark).find(e => e.kind === 'key-capture' && e.key === ${JSON.stringify(k)}); return c.value === ${v} && !!press && c.t - press.t < 100; })()`;
const count = (name) => ({ name: 'the saves so far', eval: COMMITS, save: name });
// The widget never calls User Ratings' API (the page does): the only request to /UserRatings/ that reaches the server is
// the ClientScript itself. Since 0.3.5 the details section asks for the item, and the probe answers it 404 so the
// section stays out of the page under test.
const noApi = [
    { name: 'requests to User Ratings\' routes but the script', eval: "performance.getEntriesByType('resource').map(e => e.name.replace(location.origin, '')).filter(n => /\\/UserRatings\\//i.test(n) && !/\\/UserRatings\\/ClientScript/i.test(n))", save: 'apiCalls', full: true },
    { name: 'none: the widget never calls the API', assert: '__vars.apiCalls.length === 0' },
    { name: 'the details section\'s reads, answered 404 by the probe (the details section kept out)', eval: `${P}.stubbed`, save: 'stubbed', full: true },
    { name: 'the section asked for the item and showed nothing', assert: "__vars.stubbed.some(u => /\\/UserRatings\\/Items\\//.test(u)) && document.querySelectorAll('.jfur-section').length === 0" }
];

// A real mouse event at a page x on the slider's vertical middle (Input.dispatchMouseEvent, as a click step makes them).
function mouse(type, xExpr, extra) {
    return {
        cdp: 'Input.dispatchMouseEvent',
        params: `(() => { const b = ${P}.box(${P}.widget.slider); return Object.assign({ type: '${type}', x: ${xExpr}, y: b.cy, button: '${type === 'mouseMoved' ? 'none' : 'left'}', clickCount: ${type === 'mouseMoved' ? 0 : 1} }, ${JSON.stringify(extra || {})}); })()`
    };
}

function click(label, xExpr) {
    return [
        { name: `mouse: ${label}`, script: `window.__mark = ${P}.log.length; return true;` },
        mouse('mouseMoved', xExpr),
        mouse('mousePressed', xExpr),
        mouse('mouseReleased', xExpr),
        { sleep: 100 },
        { name: `mouse: after ${label}`, eval: `Object.assign(${STATE}, { pointer: ${P}.log.slice(window.__mark).filter(e => e.kind === 'pointer').map(e => e.type + '@' + e.x), left: ${P}.box(${P}.widget.slider).x, width: ${P}.box(${P}.widget.slider).w })`, full: true }
    ];
}

// ------------------------------------------------------------------------------------------------- desktop

const dk = start({ user: 'viewer', layout: 'desktop', movie: 'Long Test Movie' });
dk.push(
    {
        name: 'markup: role="slider", one tab stop, the focusable class, aria values, labelled by "Your rating", the live region; the read-only stars an img',
        eval: `(() => { const w = ${P}.widget; const s = w.slider; const l = w.element.querySelector('.jfur-stars-live'); const d = ${P}.display; return { role: s.getAttribute('role'), tabIndex: s.tabIndex, classes: s.className, min: s.getAttribute('aria-valuemin'), max: s.getAttribute('aria-valuemax'), now: s.getAttribute('aria-valuenow'), text: s.getAttribute('aria-valuetext'), labelledBy: s.getAttribute('aria-labelledby'), label: document.getElementById(s.getAttribute('aria-labelledby')).textContent, live: [l.getAttribute('role'), l.getAttribute('aria-live')], shown: w.element.querySelector('.jfur-stars-value').textContent, display: { role: d.getAttribute('role'), label: d.getAttribute('aria-label'), tabIndex: d.tabIndex, value: d.querySelector('.jfur-stars-display-value').textContent } }; })()`,
        save: 'markup',
        full: true
    },
    { name: 'markup as expected', assert: "(m => m.role === 'slider' && m.tabIndex === 0 && m.classes === 'jfur-stars focusable' && m.min === '0' && m.max === '5' && m.now === '0' && m.text === 'Not rated' && m.label === 'Your rating' && m.live[0] === 'status' && m.live[1] === 'polite' && m.shown === 'Not rated' && m.display.role === 'img' && m.display.label === 'Member average 4.2 out of 5, 7 ratings' && m.display.tabIndex === -1 && m.display.value === '4.2')(__vars.markup)" },
    { name: 'styles on the desktop', eval: STYLES, save: 'desktopStyles', full: true },
    {
        name: 'pan-y pinch-zoom, 28 px stars, the − / + buttons hidden (pointer: fine), the slider the only focusable of its row, every text isolated, outline and fill in contrast with the page, the 4.2 average filled to 20 %',
        assert: "(s => s.touchAction === 'pan-y pinch-zoom' && Math.round(s.star.w) === 28 && s.minus === 'none' && s.plus === 'none' && s.rowFocusables.length === 1 && s.rowFocusables[0].indexOf('div.jfur-stars.focusable') === 0 && s.isolate.length === 2 && s.isolate.every(i => i.unicodeBidi === 'isolate') && s.isolate[1].dir === 'auto' && s.displayIsolate.every(i => i.unicodeBidi === 'isolate') && s.contrast.stroke >= 4.5 && s.contrast.fill >= 3 && s.offFill === 'none' && s.direction === 'ltr' && s.valueInk.x - s.slider.right < 24 && s.display.fills.join() === '100%,100%,100%,100%,20%' && s.fillTransition === '0.12s')(__vars.desktopStyles)"
    },
    { name: 'desktop: focus the Play button', script: `${P}.log.length = 0; document.querySelector('#itemDetailPage:not(.hide) .mainDetailButtons .btnPlay').focus(); return ${P}.active();` }
);
for (let i = 1; i <= 15; i++) {
    dk.push({ name: `desktop: Tab ${i}`, key: 'Tab', if: `!${P}.onSlider()` });
}

dk.push(
    { name: 'desktop: Tab reached the slider', eval: `({ onSlider: ${P}.onSlider(), tabs: ${P}.log.filter(e => e.kind === 'key-capture' && e.key === 'Tab').length, path: ${P}.log.filter(e => e.kind === 'focus').map(f => f.target) })`, save: 'tabReach', full: true },
    { name: 'desktop: assert reached', assert: `${P}.onSlider()` },
    { name: 'desktop: the focus ring after Tab (:focus-visible, 2 px)', eval: STYLES, save: 'ringStyles', full: true },
    { name: 'desktop: the ring shows', assert: "(s => s.focused && s.focusVisible && s.outline.style === 'solid' && s.outline.width === '2px')(__vars.ringStyles)" },
    { name: 'desktop: the page scrolled to 200 so a key that scrolled would show', script: `window.scrollTo(0, 200); await new Promise(r => setTimeout(r, 300)); ${P}.widget.slider.focus(); return Math.round(window.scrollY);`, save: 'scroll0' }
);
for (let i = 1; i <= 7; i++) {
    dk.push(...key(`desktop Right ${i}`, 'ArrowRight', { expect: `${handled()} && ${P}.onSlider()`, expectName: 'handled, the focus kept' }));
}

dk.push({ name: 'desktop: Right ×7 reads "3.5 out of 5 stars"', assert: `${value(3.5)} && ${P}.widget.slider.getAttribute('aria-valuetext') === '3.5 out of 5 stars'` });
dk.push(...key('desktop Enter', 'Enter', { sleep: 60, expect: `${handled()} && ${savedAtOnce(3.5, 'Enter')}`, expectName: 'saves now, skipping the debounce' }));
dk.push(
    { sleep: 400 },
    { name: 'desktop: the save answered: "Saved: 3.5 stars" in the live region', assert: `${P}.widget.element.querySelector('.jfur-stars-live').textContent === 'Saved: 3.5 stars' && ${P}.widget.savedValue() === 3.5` }
);
for (const [k, v] of [['ArrowUp', 4], ['ArrowDown', 3.5], ['ArrowLeft', 3], ['PageUp', 4], ['PageDown', 3], ['End', 5], ['ArrowRight', 5], ['Home', 0.5], ['ArrowLeft', 0.5], ['PageDown', 0.5]]) {
    dk.push(...key(`desktop ${k} to ${v}`, k, { expect: `${handled()} && ${value(v)}` }));
}

dk.push(...key('desktop Space', ' ', { sleep: 60, expect: `${handled()} && (${savedAtOnce(0.5, ' ')} || (${LASTC}.value === 0.5 && ${P}.widget.isPending() === false))`, expectName: 'saves now (or the debounce had saved it already)' }));
for (const k of ['Delete', 'Backspace', '3']) {
    dk.push(...key(`desktop ${k}`, k, { expect: `${leftAlone()} && ${value(0.5)}`, expectName: 'left to the page, nothing cleared' }));
}

dk.push({ name: 'desktop: no key scrolled the page', assert: 'Math.round(window.scrollY) === __vars.scroll0', message: 'a key the slider handles scrolled the page' });
// The debounce: a change is saved 600 ms after the last one.
dk.push(count('beforeDebounce'));
dk.push(...key('desktop debounce Right', 'ArrowRight', { sleep: 300, expect: `${COMMITS} === __vars.beforeDebounce`, expectName: 'not saved within 300 ms' }));
dk.push(
    { sleep: 500 },
    { name: 'desktop: saved once, 600 ms after the change', assert: `${COMMITS} === __vars.beforeDebounce + 1 && ${LASTC}.value === 1 && ${LASTC}.t - ${LAST('change')}.t >= 590` },
    { name: 'desktop: Tab leaves the slider', key: 'Tab' },
    { name: 'desktop: where Tab went', eval: `({ active: ${P}.active(), value: ${P}.widget.value() })`, save: 'tabOut', full: true },
    { name: 'desktop: to Clear on the next row', assert: "__vars.tabOut.active.indexOf('jfur-probe-clear') >= 0 && __vars.tabOut.value === 1" },
    { name: 'mouse: the page back to the top, the stars in view', script: `window.scrollTo(0, 0); ${P}.widget.slider.scrollIntoView({ block: 'center' }); await new Promise(r => setTimeout(r, 400)); ${P}.widget.setValue(0); return ${STATE};`, full: true },
    // Hover: the preview, not the value; leaving shows the value again.
    mouse('mouseMoved', `${P}.xFor(2.5)`),
    { sleep: 100 },
    { name: 'mouse: hovering the middle of star 3', eval: STATE, save: 'hover', full: true },
    { name: 'mouse: previews 2.5, the value still "Not rated"', assert: "__vars.hover.fills.join() === '100%,100%,50%,0%,0%' && __vars.hover.valuenow === '0' && __vars.hover.shown === '2.5' && __vars.hover.classes.indexOf('jfur-rating-preview') >= 0" },
    mouse('mouseMoved', `${P}.xFor(2.5)`, { y: 5 }),
    { sleep: 100 },
    { name: 'mouse: after leaving', eval: STATE, save: 'left', full: true },
    { name: 'mouse: leaving shows the value again', assert: "__vars.left.fills.join() === '0%,0%,0%,0%,0%' && __vars.left.shown === 'Not rated'" }
);
dk.push(...click('a click on the middle of star 3', `${P}.xFor(2.5)`));
dk.push(
    { name: 'mouse: the middle of star 3 is 2.5', assert: "(() => { const s = " + STATE + "; return s.value === 2.5 && s.valuetext === '2.5 out of 5 stars'; })()", message: 'the middle of star 3 did not give 2.5' },
    { name: 'mouse: the pointer\'s x was the middle exactly', eval: `(() => { const p = ${P}.log.filter(e => e.kind === 'pointer' && e.type === 'pointerup').pop(); const b = ${P}.box(${P}.widget.slider); return { x: p.x, middle: b.x + b.w / 2, exact: Math.abs(p.x - (b.x + b.w / 2)) < 0.01 }; })()`, save: 'middle', full: true }
);
dk.push(...click('a click 1 px past the middle', `${P}.xFor(2.5) + 1`));
dk.push({ name: 'mouse: just past the middle is 3.0', assert: value(3) });
dk.push(...click('a click 1 px before the middle', `${P}.xFor(2.5) - 1`));
dk.push({ name: 'mouse: just before is 2.5', assert: value(2.5) });
dk.push(...click('a click on star 4\'s right half', `${P}.xFor(3.75)`));
dk.push(
    { name: 'mouse: star 4\'s right half is 4.0, not saved yet', assert: `${value(4)} && ${P}.widget.isPending()` },
    { sleep: 800 },
    { name: 'mouse: the last click saved, 600 ms after it', assert: `${LASTC}.value === 4 && ${P}.widget.savedValue() === 4 && ${LASTC}.t - ${LAST('change')}.t >= 590` },
    count('beforeSame')
);
dk.push(...click('a click on the current value', `${P}.xFor(3.75)`));
dk.push(
    { sleep: 800 },
    { name: 'mouse: clicking the current value does nothing', assert: `${value(4)} && ${COMMITS} === __vars.beforeSame` },
    { name: 'Clear (the page\'s button: DELETE Mine/Rating, then setValue(0) and the live region)', click: '.jfur-probe-clear' },
    { sleep: 200 },
    { name: 'after Clear', eval: STATE, save: 'cleared', full: true },
    { name: 'cleared: "Not rated", "Rating cleared" announced', assert: "__vars.cleared.valuenow === '0' && __vars.cleared.valuetext === 'Not rated' && __vars.cleared.live === 'Rating cleared'" },
    // A failed save: back to the last saved value.
    { name: 'a failed save: the next one fails', script: `${P}.widget.setValue(2); ${P}.fail = 1; ${P}.widget.slider.focus(); return true;` }
);
dk.push(...key('failed save Right', 'ArrowRight', { expect: value(2.5) }));
dk.push(...key('failed save Enter', 'Enter', { sleep: 400, expect: `${value(2)} && ${P}.widget.element.querySelector('.jfur-stars-value').textContent === '2' && ${P}.log.some(e => e.kind === 'save-failed')`, expectName: 'puts back the last saved value' }));
// Disabled with the page's reason (the details section decides it): nothing changes it, every key is the page's, the note is read.
dk.push(
    { name: 'disabled: NeedsPlay with its note (the details section gives the disabled slider to NeedsPlay and ReadOnly, read-only stars to NotARater)', script: `${P}.widget.setDisabled({ reason: 'NeedsPlay', note: ${JSON.stringify(NOTE)} }); ${P}.widget.slider.focus(); return ${STATE};`, full: true },
    { name: 'disabled: aria-disabled, the reason, the note described and isolated, still a tab stop', eval: `(() => { const s = ${P}.widget.slider; const n = document.getElementById(s.getAttribute('aria-describedby')); return { noteHidden: n && n.getAttribute('aria-hidden'), disabled: s.getAttribute('aria-disabled'), reason: s.getAttribute('data-jfur-reason'), note: n && n.textContent, shown: n && n.getClientRects().length > 0, bidi: n && getComputedStyle(n).unicodeBidi, dir: n && n.getAttribute('dir'), title: s.getAttribute('title'), tabIndex: s.tabIndex, onSlider: ${P}.onSlider() }; })()`, save: 'disabled', full: true },
    { name: 'disabled as expected (the reason and note the page gave; the note read once, as the slider\'s description: no title, hidden in the reading order)', assert: `(d => d.disabled === 'true' && d.reason === 'NeedsPlay' && d.note === ${JSON.stringify(NOTE)} && d.shown && d.bidi === 'isolate' && d.dir === 'auto' && d.title === null && d.noteHidden === 'true' && d.tabIndex === 0 && d.onSlider)(__vars.disabled)` }
);
for (const k of ['ArrowRight', 'ArrowLeft', 'Home', 'End']) {
    dk.push(...key(`disabled ${k}`, k, { refocus: true, expect: `${leftAlone()} && ${value(2)}`, expectName: 'left to the page, the value kept' }));
}

dk.push(count('beforeDisabledClick'));
dk.push(...click('a click while disabled', `${P}.xFor(4.75)`));
dk.push(
    { sleep: 700 },
    { name: 'disabled: the click changed nothing, saved nothing', assert: `${value(2)} && ${COMMITS} === __vars.beforeDisabledClick` },
    { name: 'a name with an unclosed override in the note stays in its element', script: `${P}.widget.setDisabled({ reason: 'NeedsPlay', note: 'Only \\u0633\\u0627\\u0631\\u0629 \\u202Eabc can rate' }); const n = ${P}.widget.element.querySelector('.jfur-stars-note'); return { bidi: getComputedStyle(n).unicodeBidi, dir: n.getAttribute('dir'), text: n.textContent };`, save: 'bidiNote', full: true },
    { name: 'the note isolated', assert: "__vars.bidiNote.bidi === 'isolate' && __vars.bidiNote.dir === 'auto'" },
    { name: 'enabled again', script: `${P}.widget.setDisabled(null); return ${STATE};`, full: true },
    // Forced colours (Windows high contrast) and reduced motion, emulated.
    { name: 'forced colours', emulate: { media: { 'forced-colors': 'active' } } },
    { sleep: 200 },
    { name: 'styles in forced colours', eval: STYLES, save: 'forcedStyles', full: true },
    { name: 'forced colours: the theme\'s colours, the fill solid, the outline drawn', assert: "(s => s.forcedColorAdjust === 'none' && s.stroke === s.onFill && s.offFill === 'none' && s.contrast.stroke >= 4.5)(__vars.forcedStyles)" },
    { name: 'reduced motion', emulate: { media: { 'prefers-reduced-motion': 'reduce' } } },
    { sleep: 200 },
    { name: 'styles with reduced motion', eval: STYLES, save: 'motionStyles', full: true },
    { name: 'reduced motion: no transition', assert: "__vars.motionStyles.fillTransition === '0s'" },
    { name: 'media features back', emulate: 'desktop' },
    { name: 'screenshot (desktop)', screenshot: 'userratings-stars-desktop.png' },
    // Right to left: jellyfin-web sets dir="rtl" on <html> and <body> for an RTL language.
    { name: 'right to left: mount again under dir="rtl"', eval: `${P}.mount({ rtl: true, value: 0.5 })`, save: 'rtlMount', full: true },
    { sleep: 300 },
    { name: 'right to left: styles', eval: STYLES, save: 'rtlStyles', full: true },
    { name: 'right to left: the half star is the rightmost star\'s right half', assert: "(s => s.direction === 'rtl' && s.slider.right - s.fills[0].star.right < 1 && Math.abs(s.fills[0].fill.right - s.fills[0].star.right) < 0.5 && Math.abs(s.fills[0].fill.w - s.fills[0].star.w / 2) < 0.5 && Math.abs(s.fills[0].svg.right - s.fills[0].star.right) < 0.5 && s.slider.x - s.valueInk.right > 0 && s.slider.x - s.valueInk.right < 24)(__vars.rtlStyles)", message: 'right to left the stars did not fill from the right, or the value is not beside them' },
    { name: 'right to left: the stars in view', script: `${P}.widget.slider.scrollIntoView({ block: 'center' }); await new Promise(r => setTimeout(r, 300)); return true;` }
);
dk.push(...click('right to left, 2 px from the left edge', `${P}.box(${P}.widget.slider).x + 2`));
dk.push({ name: 'right to left: the left edge is 5', assert: value(5) });
dk.push(...click('right to left, the middle of star 3', `${P}.xFor(2.5, true)`));
dk.push({ name: 'right to left: the middle of star 3 is 2.5', assert: value(2.5) });
dk.push(...click('right to left, star 1\'s left half (from the right edge 30 px)', `${P}.box(${P}.widget.slider).right - 21`));
dk.push({ name: 'right to left: 1.0', assert: value(1) });
dk.push(...key('right to left Left', 'ArrowLeft', { refocus: true, expect: `${handled()} && ${value(1.5)}`, expectName: 'Left adds half a star' }));
dk.push(...key('right to left Right', 'ArrowRight', { expect: `${handled()} && ${value(1)}`, expectName: 'Right takes it away' }));
dk.push(
    { name: 'screenshot (right to left)', screenshot: 'userratings-stars-rtl.png' },
    { name: 'left to right again', eval: `${P}.direction('ltr')` },
    ...noApi,
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    { name: 'the key log', eval: `${P}.log.filter(e => e.kind !== 'pointer')`, full: true }
);
write('userratings-stars-desktop.json', 'User Ratings ' + SHORT_VERSION + ': the star widget of the served ClientScript on Long Test Movie\'s details page as viewer, in the desktop layout: its markup and aria values; Tab reaches it and the ring shows; Right ×7 reads 3.5 and Enter saves at once; every key the widget handles, prevented, none scrolling the page, Delete, Backspace and digits left alone; the 600 ms debounce; the mouse: hover previews, the middle of star 3 is 2.5, just past it 3.0, star 4\'s right half 4.0, the current value does nothing; Clear; a failed save put back; disabled with the page\'s reason and note (keys left to the page, the note read once); forced colours; reduced motion; right to left (filled from the right, the left edge 5, Left adds). No request to User Ratings\' API.', dk);

// --------------------------------------------------------------------------------------------------- TV

const tv = start({ user: 'viewer', layout: 'tv', movie: 'Long Test Movie' });
tv.push(
    { name: 'TV: styles', eval: STYLES, save: 'tvStyles', full: true },
    { name: 'TV: the − / + buttons hidden, the slider alone in its row, the value text larger', assert: "(s => s.minus === 'none' && s.plus === 'none' && s.rowFocusables.length === 1 && parseFloat(s.valueFontSize) > parseFloat(s.rowFontSize) * 1.2)(__vars.tvStyles) && " + `${P}.widget.element.classList.contains('jfur-rating-tv')` },
    { name: 'TV: focus the Play button', script: `${P}.log.length = 0; document.querySelector('#itemDetailPage:not(.hide) .mainDetailButtons .btnPlay').focus(); return ${P}.active();` }
);
for (let i = 1; i <= 6; i++) {
    tv.push({ name: `TV: ArrowDown ${i} (to the slider)`, key: 'ArrowDown', if: `!${P}.onSlider()` }, { sleep: 200, if: `!${P}.onSlider()` });
}

tv.push(
    { name: 'TV: the slider reached with the D-pad', eval: `({ onSlider: ${P}.onSlider(), presses: ${P}.log.filter(e => e.kind === 'key-capture' && e.key === 'ArrowDown').length, path: ${P}.log.filter(e => e.kind === 'focus').map(f => f.target) })`, save: 'tvReach', full: true },
    { name: 'TV: reached within 6 presses', assert: `${P}.onSlider() && __vars.tvReach.presses <= 6` },
    { name: 'TV: the 3 px ring', eval: STYLES, save: 'tvRing', full: true },
    { name: 'TV: the ring shows', assert: "(s => s.focused && s.outline.style === 'solid' && s.outline.width === '3px')(__vars.tvRing)" }
);
for (let i = 1; i <= 7; i++) {
    tv.push(...key(`TV Right ${i}`, 'ArrowRight', { sleep: 200, expect: `${handled()} && ${P}.onSlider()`, expectName: 'consumed, the focus stays' }));
}

tv.push({ name: 'TV: seven Rights gave 3.5', assert: `${value(3.5)} && ${P}.onSlider()` });
tv.push(...key('TV Left', 'ArrowLeft', { sleep: 200, expect: `${handled()} && ${value(3)} && ${P}.onSlider()` }));
tv.push(...key('TV OK', 'Enter', { sleep: 100, expect: `${handled()} && (${savedAtOnce(3, 'Enter')} || (${LASTC}.value === 3 && !${P}.widget.isPending()))`, expectName: 'saves now' }));
for (const [k, v] of [['PageUp', 4], ['PageDown', 3], ['End', 5], ['Home', 0.5]]) {
    tv.push(...key(`TV ${k}`, k, { sleep: 200, expect: `${handled()} && ${value(v)}` }));
}

for (let i = 1; i <= 5; i++) {
    tv.push({ name: `TV: Right ${i} (to 3.0)`, key: 'ArrowRight' }, { sleep: 120 });
}

tv.push(...key('TV Up', 'ArrowUp', { sleep: 400, expect: `${leftAlone()} && !${P}.onSlider() && ${value(3)}`, expectName: 'not handled: the focus left upwards, the value kept' }));
tv.push(...key('TV Down back', 'ArrowDown', { sleep: 400, expect: `${P}.onSlider() && ${value(3)}`, expectName: 'back on the slider' }));
tv.push(...key('TV Down out', 'ArrowDown', { sleep: 400, expect: `${leftAlone()} && !${P}.onSlider() && ${value(3)} && /jfur-probe-(clear|write)/.test(${P}.active())`, expectName: 'not handled: down to the next row (Clear, Write a review), the value kept' }));
tv.push(...key('TV Up back', 'ArrowUp', { sleep: 400, expect: `${P}.onSlider()`, expectName: 'back on the slider' }));
tv.push(
    { sleep: 700 },
    { name: 'TV: the saves so far (none for a value back at the saved one)', eval: `${P}.commits.map(c => c.value)`, full: true },
    { name: 'TV: the last save is the saved value, nothing pending', assert: `${LASTC}.value === ${P}.widget.savedValue() && !${P}.widget.isPending()` },
    { name: 'TV: disabled with a reason', script: `${P}.widget.setDisabled({ reason: 'NeedsPlay', note: 'Play this first: rating it needs it to be played' }); ${P}.widget.slider.focus(); return ${STATE};`, full: true }
);
tv.push(...key('TV disabled Right', 'ArrowRight', { sleep: 300, expect: `${leftAlone()} && ${value(3)}`, expectName: 'left to jellyfin-web, the value kept' }));
tv.push(...key('TV disabled Down', 'ArrowDown', { refocus: true, sleep: 400, expect: `!${P}.onSlider()`, expectName: 'the D-pad moves on' }));
tv.push(
    { name: 'TV: enabled again, the slider focused', script: `${P}.widget.setDisabled(null); ${P}.widget.slider.focus(); return ${P}.onSlider();` },
    { name: 'TV: screenshot', screenshot: 'userratings-stars-tv.png' },
    { name: 'TV: the hash before Back', eval: 'location.hash', save: 'hashBefore' }
);
tv.push(...key('TV Back', 'Escape', { sleep: 1500 }));
tv.push(
    { name: 'TV: Back (Escape) is jellyfin-web\'s: the page left, the slider never handled it', eval: `({ hash: location.hash, left: location.hash.indexOf(__vars.movieId) < 0, sliderPrevented: ${P}.log.filter(e => e.kind === 'key-bubble' && e.key === 'Escape').map(e => e.prevented) })`, save: 'tvBack', full: true },
    { name: 'TV: Back navigated', assert: '__vars.tvBack.left === true' },
    { name: 'the default layout again', eval: "(localStorage.removeItem('layout'), true)" },
    ...noApi,
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    { name: 'TV: the key log', eval: `${P}.log.filter(e => e.kind !== 'pointer')`, full: true }
);
write('userratings-stars-tv.json', 'User Ratings ' + SHORT_VERSION + ' in the TV layout: the star widget of the served ClientScript on Long Test Movie as viewer in jellyfin-web 10.11.5\'s TV layout: the − / + buttons hidden and the slider alone in its row; the D-pad reaches it from Play; the 3 px ring; Left/Right step by half a star with the focus kept and jellyfin-web\'s window handler seeing them prevented; OK saves; PageUp, PageDown, End, Home; Up and Down leave with the value kept and come back; disabled with a reason it leaves the D-pad alone; Back (Escape) is jellyfin-web\'s.', tv);

// ------------------------------------------------------------------------------------------------- touch

const PHONE = { width: 390, height: 844, mobile: true, touch: true };
const tc = start({ user: 'viewer', layout: 'mobile', movie: 'Long Test Movie', emulate: PHONE });

function touchEvent(kind, xy) {
    const points = kind === 'touchEnd' ? '[]' : `[{ x: Math.round(${xy[0]}), y: Math.round(${xy[1]}) }]`;
    return { cdp: 'Input.dispatchTouchEvent', params: `({ type: '${kind}', touchPoints: ${points} })` };
}

const SY = `${P}.box(${P}.widget.slider).cy`;
function gesture(label, x0, y0, dx, dy, n) {
    n = n || 8;
    const steps = [{ name: `touch: ${label}`, script: `window.__g = { x0: ${x0}, y0: ${y0}, dx: ${dx}, dy: ${dy} }; window.__mark = ${P}.log.length; window.__scroll = Math.round(window.scrollY); return window.__g;`, full: true }];
    steps.push(touchEvent('touchStart', ['__g.x0', '__g.y0']));
    for (let i = 1; i <= n; i++) {
        steps.push(touchEvent('touchMove', [`__g.x0 + __g.dx * ${i} / ${n}`, `__g.y0 + __g.dy * ${i} / ${n}`]));
    }

    steps.push(touchEvent('touchEnd', null), { sleep: 900 });
    return steps;
}

const TSTATE = `Object.assign(${STATE}, { scrollBefore: window.__scroll, pointer: ${P}.log.slice(window.__mark).filter(e => e.kind === 'pointer').map(e => e.type), changes: ${P}.log.slice(window.__mark).filter(e => e.kind === 'change').map(e => e.value + ':' + e.how) })`;
const centre = (label) => ({ name: `touch: the stars in the middle (${label})`, script: `${P}.widget.slider.scrollIntoView({ block: 'center' }); await new Promise(r => setTimeout(r, 500)); window.__mark = ${P}.log.length; window.__scroll = Math.round(window.scrollY); return window.__scroll;` });
tc.push(
    { name: 'touch: styles at (pointer: coarse)', eval: STYLES, save: 'touchStyles', full: true },
    { name: 'touch: 40 px stars, pan-y pinch-zoom, the − / + buttons shown at 40 px', assert: "(s => s.coarse && Math.round(s.star.w) === 40 && s.touchAction === 'pan-y pinch-zoom' && s.minus !== 'none' && s.plus !== 'none' && s.minusBox.w >= 40 && s.minusBox.h >= 40)(__vars.touchStyles)" },
    { name: 'touch: room to scroll both ways (a 1500 px spacer under the section)', script: "const s = document.createElement('div'); s.style.height = '1500px'; document.querySelector('#itemDetailPage:not(.hide) .detailPageContent').appendChild(s); return true;" },
    centre('the tap'),
    touchEvent('touchStart', [`${P}.xFor(3.75)`, SY]),
    touchEvent('touchEnd', null),
    { sleep: 900 },
    { name: 'touch: a tap on star 4\'s right half', eval: TSTATE, save: 'tap', full: true },
    { name: 'touch: the tap set 4.0 on pointerup and saved it once, no hover state left', assert: "(s => s.value === 4 && s.commits.join() === '4' && s.classes.indexOf('jfur-rating-preview') < 0 && s.fills.join() === '100%,100%,100%,100%,0%')(__vars.tap)" },
    centre('the drag')
);
tc.push(...gesture('a horizontal drag from star 4, two stars to the left', `${P}.xFor(3.75)`, SY, `-2 * ${P}.box(${P}.widget.slider).w / 5`, '0'));
tc.push(
    { name: 'touch: after the horizontal drag', eval: TSTATE, save: 'drag', full: true },
    { name: 'touch: the drag scrubbed to 2.0, saved once on lift, the page still', assert: "(s => s.value === 2 && s.commits.join() === '4,2' && s.scrollX === 0 && s.scrollY === s.scrollBefore && s.pointer.indexOf('pointerup') >= 0)(__vars.drag)" },
    centre('the swipe up')
);
tc.push(...gesture('a vertical swipe up 300 px starting on star 4 (scrolls the page down)', `${P}.xFor(3.75)`, SY, '0', '-300'));
tc.push(
    { name: 'touch: after the swipe up', eval: TSTATE, save: 'swipeUp', full: true },
    { name: 'touch: the page scrolled, the pointer was cancelled, the value kept, nothing saved (2 → 4 before the fix)', assert: "(s => s.value === 2 && s.commits.join() === '4,2' && s.scrollY > s.scrollBefore && s.pointer.indexOf('pointercancel') >= 0)(__vars.swipeUp)" },
    centre('the swipe down')
);
tc.push(...gesture('a vertical swipe down 300 px starting on star 1 (scrolls the page up)', `${P}.xFor(0.75)`, SY, '0', '300'));
tc.push(
    { name: 'touch: after the swipe down', eval: TSTATE, save: 'swipeDown', full: true },
    { name: 'touch: the page scrolled back, the value kept, nothing saved (4 → 1 before the fix)', assert: "(s => s.value === 2 && s.commits.join() === '4,2' && s.scrollY < s.scrollBefore && s.pointer.indexOf('pointercancel') >= 0)(__vars.swipeDown)" },
    centre('the diagonal')
);
tc.push(...gesture('a diagonal drag, mostly sideways (60 px right, 20 px down) from star 1¼', `${P}.xFor(1.25)`, SY, '60', '20'));
tc.push(
    { name: 'touch: after the diagonal', eval: TSTATE, save: 'diagonal', full: true },
    { name: 'touch: the diagonal scrubbed to 3.0 and saved it, the page still', assert: "(s => s.value === 3 && s.commits.join() === '4,2,3' && s.scrollY === s.scrollBefore)(__vars.diagonal)" },
    { name: 'touch: + in view', script: `document.querySelector('.jfur-stars-plus').scrollIntoView({ block: 'center' }); await new Promise(r => setTimeout(r, 500)); window.__mark = ${P}.log.length; return ${P}.widget.value();` },
    touchEvent('touchStart', [`${P}.box('.jfur-stars-plus').cx`, `${P}.box('.jfur-stars-plus').cy`]),
    touchEvent('touchEnd', null),
    { sleep: 300 },
    touchEvent('touchStart', [`${P}.box('.jfur-stars-plus').cx`, `${P}.box('.jfur-stars-plus').cy`]),
    touchEvent('touchEnd', null),
    { sleep: 300 },
    touchEvent('touchStart', [`${P}.box('.jfur-stars-minus').cx`, `${P}.box('.jfur-stars-minus').cy`]),
    touchEvent('touchEnd', null),
    { sleep: 900 },
    { name: 'touch: +, + and −', eval: TSTATE, save: 'buttons', full: true },
    { name: 'touch: 3 + 0.5 + 0.5 - 0.5 = 3.5, saved once 600 ms after the last', assert: "(s => s.value === 3.5 && s.commits.join() === '4,2,3,3.5')(__vars.buttons)" },
    { name: 'touch: screenshot', screenshot: 'userratings-stars-touch.png' },
    { name: 'desktop again', emulate: 'desktop' },
    { name: 'the default layout again', eval: "(localStorage.removeItem('layout'), true)" },
    ...noApi,
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    { name: 'touch: the log', eval: `${P}.log.filter(e => e.kind !== 'focus')`, full: true }
);
write('userratings-stars-touch.json', 'User Ratings ' + SHORT_VERSION + ' under touch: the star widget of the served ClientScript on Long Test Movie as viewer in the mobile layout at 390x844 with touch emulated ((pointer: coarse)): 40 px stars, touch-action pan-y pinch-zoom, the − / + buttons at 40 px; a tap on star 4\'s right half sets 4.0 on pointerup; a horizontal drag scrubs to 2.0 without moving the page and saves once; vertical swipes starting on the stars scroll the page, the pointer is cancelled and the value stays (an earlier build gave 2 → 4 and 4 → 1 there); a mostly sideways diagonal scrubs; + + − step by half a star. Touches through Input.dispatchTouchEvent.', tc);

// --------------------------------------------------------------------------------------------------------- pen

// A pen's pointer events (Input.dispatchMouseEvent with pointerType pen, as DevTools sends them). The
// slider captures the pen, so its moves and its lift off the stars come to it; before, a pen lifted off the stars was never
// seen: the stars kept the scrubbed value, nothing was saved, and a key's change waiting for its save was dropped.
const pn = start({ user: 'viewer', layout: 'desktop', movie: 'Long Test Movie' });

function pen(type, xExpr, yExpr) {
    const down = type !== 'mouseReleased';
    return {
        cdp: 'Input.dispatchMouseEvent',
        params: `(() => { const b = ${P}.box(${P}.widget.slider); return { type: '${type}', x: ${xExpr}, y: ${yExpr || 'b.cy'}, button: 'left', buttons: ${down ? 1 : 0}, clickCount: ${type === 'mouseMoved' ? 0 : 1}, pointerType: 'pen' }; })()`
    };
}

const PSTATE = `Object.assign(${STATE}, { pointer: ${P}.log.slice(window.__mark).filter(e => e.kind === 'pointer').map(e => e.type + ':' + e.pointerType), changes: ${P}.log.slice(window.__mark).filter(e => e.kind === 'change').map(e => e.value + ':' + e.how) })`;
pn.push(
    { name: 'pen: the stars in view, the value 1 (saved)', script: `${P}.widget.setValue(1); ${P}.widget.slider.scrollIntoView({ block: 'center' }); await new Promise(r => setTimeout(r, 400)); window.__mark = ${P}.log.length; return ${STATE};`, full: true },
    pen('mousePressed', `${P}.xFor(1.75)`),
    pen('mouseMoved', `${P}.xFor(2.5)`),
    pen('mouseMoved', `${P}.xFor(3.5)`),
    pen('mouseMoved', `${P}.xFor(4.5)`),
    pen('mouseMoved', 'b.right + 40'),
    pen('mouseMoved', 'b.right + 120'),
    { name: 'pen: past the right edge, still down', eval: PSTATE, save: 'penOver', full: true },
    { name: 'pen: the scrub follows it past the edge: 5, not saved while it is down', assert: "(s => s.value === 5 && s.commits.length === 0 && s.pointer.indexOf('pointerdown:pen') >= 0)(__vars.penOver)" },
    pen('mouseReleased', 'b.right + 140'),
    { sleep: 900 },
    { name: 'pen: after the lift off the stars', eval: PSTATE, save: 'penLift', full: true },
    { name: 'pen: the lift off the stars ends the scrub, and 5 is saved once (before, 4.5 stayed shown and nothing was saved)', assert: "(s => s.value === 5 && s.saved === 5 && s.commits.join() === '5' && s.pointer.indexOf('pointerup:pen') >= 0)(__vars.penLift)" },
    { name: 'pen: the slider focused for a key', script: `${P}.widget.slider.focus(); window.__mark = ${P}.log.length; return ${P}.onSlider();` },
    { name: 'pen: Left (4.5, its save waiting 600 ms)', key: 'ArrowLeft' },
    pen('mousePressed', `${P}.xFor(1.25)`),
    pen('mouseMoved', `${P}.xFor(1.25) + 2`, 'b.cy + 40'),
    pen('mouseMoved', `${P}.xFor(1.25) + 3`, 'b.cy + 120'),
    pen('mouseReleased', `${P}.xFor(1.25) + 3`, 'b.cy + 150'),
    { sleep: 1100 },
    { name: 'pen: after a pen that went down from the stars and lifted below them', eval: PSTATE, save: 'penAway', full: true },
    { name: 'pen: the pen changed nothing, and the key\'s change was saved after it lifted (before, the pen\'s down dropped its save for good)', assert: "(s => s.value === 4.5 && s.saved === 4.5 && s.commits.join() === '5,4.5')(__vars.penAway)" },
    { name: 'pen: a tap', script: `window.__mark = ${P}.log.length; return true;` },
    pen('mousePressed', `${P}.xFor(2.75)`),
    pen('mouseReleased', `${P}.xFor(2.75)`),
    { sleep: 900 },
    { name: 'pen: after the tap', eval: PSTATE, save: 'penTap', full: true },
    { name: 'pen: a tap sets the value under it (3.0) on its lift and saves it', assert: "(s => s.value === 3 && s.saved === 3 && s.commits.join() === '5,4.5,3')(__vars.penTap)" },
    { name: 'pen: screenshot', screenshot: 'userratings-stars-pen.png' },
    ...noApi,
    { name: 'no uncaught exception', noExceptions: true, ignore: IGNORE },
    { name: 'pen: the log', eval: `${P}.log.filter(e => e.kind !== 'focus')`, full: true }
);
write('userratings-stars-pen.json', 'User Ratings ' + SHORT_VERSION + ' under a pen: the star widget of the served ClientScript on Long Test Movie as viewer in the desktop layout, under a pen (Input.dispatchMouseEvent with pointerType pen): a scrub from star 2 that overshoots the right edge follows the pen to 5 and, lifted off the stars, saves 5 once; a key\'s change (4.5) waiting for its save is kept through a pen that went down from the stars and lifted below them, and saved after; a tap sets 3.0 on its lift. No request to User Ratings\' API.', pn);

// ---------------------------------------------------------------------------------------------- the je sandbox

const je = start({ user: 'admin', layout: 'desktop', movie: 'Long Test Movie', je: true });
// The settings are read as names and kinds only (a string's length, never its text: a translation service's key would be one).
je.splice(je.findIndex((s) => s.login) + 1, 0,
    { name: 'Jellyfin Enhanced\'s version', eval: "(async () => { const ps = await ApiClient.getJSON(ApiClient.getUrl('Plugins')); const p = ps.find(x => x.Name === 'Jellyfin Enhanced'); return p ? { Version: p.Version, Status: p.Status, Id: p.Id } : null; })()", save: 'jeVersion', full: true },
    { name: 'its review settings (12.14\'s ReviewTranslation* and ShowAnilistReviews), as names and kinds', eval: "(async () => { const c = await ApiClient.getPluginConfiguration(__vars.jeVersion.Id); const out = {}; Object.keys(c).filter(k => /review|translat|anilist|tmdb.*rating/i.test(k)).sort().forEach(k => { const v = c[k]; out[k] = typeof v === 'boolean' || typeof v === 'number' ? v : (typeof v === 'string' ? (v === '' ? '(empty)' : '(text, ' + v.length + ' characters)') : (Array.isArray(v) ? '(list of ' + v.length + ')' : typeof v)); }); return { count: Object.keys(c).length, settings: out }; })()", save: 'jeSettings', full: true }
);
const NAV = [['ArrowRight', 0.5], ['ArrowRight', 1], ['ArrowLeft', 0.5], ['ArrowUp', 1], ['ArrowDown', 0.5], ['PageUp', 1.5], ['PageDown', 0.5], ['End', 5], ['Home', 0.5]];
const jeKeys = (label, list) => {
    const out = [];
    for (const [k, v] of list) {
        out.push(...key(`${label} ${k}`, k, { refocus: true, sleep: 250, expect: `${handled()} && ${value(v)} && (window.__jeSpy ? window.__jeSpy.calls.filter(c => c.ms >= window.__markAbs).length === 0 : true) && ${P}.log.slice(window.__mark).some(e => e.kind === 'key-capture' && e.key === ${JSON.stringify(k)} && e.prevented === false)`, expectName: 'reached the slider unprevented, handled, JE did nothing' }));
    }

    return out;
};
je.push(...jeKeys('JE desktop', NAV));
je.push(...key('JE desktop Enter', 'Enter', { refocus: true, sleep: 250, expect: `${handled()} && ${LASTC}.value === 0.5 && !${P}.widget.isPending()` }));
// JE's own shortcut keys on the slider: the slider takes none of them; JE's player keys do nothing here.
for (const k of ['3', 'S', 'I', 'C', '+', '-', 'B']) {
    je.push(...key(`JE key ${k}`, k, { refocus: true, sleep: 250, expect: `${value(0.5)} && ${P}.onSlider()`, expectName: 'not the slider\'s, the value kept' }));
}

je.push(
    { name: 'JE desktop: collisions on Long Test Movie', eval: `${P}.collisions()`, save: 'collisions1', full: true },
    { name: 'JE desktop: no style rule from JE (or anyone but jellyfin-web) on the widget, our ids unique, no jfur- class or id of anyone else\'s', assert: '__vars.collisions1.foreign.length === 0 && __vars.collisions1.duplicateIds.length === 0 && __vars.collisions1.foreignJfur.length === 0' },
    { name: 'a movie with a TMDb id (its folder\'s [tmdbid-900001]), where JE draws its review section', eval: MOVIE('Tagged Movie'), save: 'taggedId' },
    { name: 'its page', script: "location.hash = '#/details?id=' + __vars.taggedId; return true;" },
    { name: 'drawn (Tagged Movie)', waitFor: DRAWN('taggedId'), timeout: 30000 },
    { sleep: 6000 },
    { name: 'JE\'s review parts (jeReviews; on 12.12: the section 1, the Write button, no chip without a review)', eval: `${P}.jeReviews()`, save: 'jeReviews', full: true },
    { name: 'mount the widget beside JE\'s reviews', eval: `${P}.mount({})`, save: 'taggedMount', full: true },
    { sleep: 500 },
    { name: 'JE desktop: collisions on Tagged Movie, JE\'s review section drawn', eval: `${P}.collisions()`, save: 'collisions2', full: true },
    { name: 'JE desktop: still none with JE\'s review UI on the page', assert: '__vars.collisions2.foreign.length === 0 && __vars.collisions2.duplicateIds.length === 0 && __vars.collisions2.foreignJfur.length === 0' },
    { name: 'both sections in the page, in order', eval: `${P}.jeReviews().sectionOrder`, full: true }
);
je.push(...jeKeys('JE Tagged', [['ArrowRight', 0.5], ['End', 5], ['ArrowLeft', 4.5]]));
je.push(
    { name: 'screenshot Tagged Movie with JE\'s reviews and the widget', screenshot: 'userratings-stars-je.png' },
    { name: 'JE TV: TV layout', eval: "(localStorage.setItem('layout', 'tv'), true)" },
    { name: 'JE TV: reload', reload: true },
    { name: 'JE TV: layout-tv, JE\'s key listener up', waitFor: "document.documentElement.classList.contains('layout-tv') && !!window.ApiClient && !!ApiClient.getCurrentUserId() && !!(window.__jfkit && window.__jfkit.jfur) && !!window.JellyfinEnhanced && !!window.JellyfinEnhanced.keyListener", timeout: 40000 },
    // Long Test Movie (no TMDb id, so no JE review section): the TV path with JE loaded.
    { name: 'JE TV: Long Test Movie', script: "location.hash = '#/details?id=' + __vars.movieId; return true;" },
    { name: 'JE TV: drawn', waitFor: DRAWN('movieId'), timeout: 30000 },
    { sleep: 4000 },
    { name: 'JE TV: mount', eval: `${P}.mount({})`, save: 'tvMount', full: true },
    { name: 'JE TV: focus the Play button', script: `${P}.log.length = 0; document.querySelector('#itemDetailPage:not(.hide) .mainDetailButtons .btnPlay').focus(); return ${P}.active();` }
);
for (let i = 1; i <= 6; i++) {
    je.push({ name: `JE TV: ArrowDown ${i}`, key: 'ArrowDown', if: `!${P}.onSlider()` }, { sleep: 200, if: `!${P}.onSlider()` });
}

je.push(
    { name: 'JE TV: the D-pad\'s path to the slider', eval: `({ onSlider: ${P}.onSlider(), presses: ${P}.log.filter(e => e.kind === 'key-capture' && e.key === 'ArrowDown').length, path: ${P}.log.filter(e => e.kind === 'focus').map(f => f.target) })`, save: 'jeTvReach', full: true },
    { name: 'JE TV: reached with JE loaded, as without it', assert: `${P}.onSlider() && __vars.jeTvReach.presses <= 6` }
);
je.push(...key('JE TV Right', 'ArrowRight', { sleep: 250, expect: `${handled()} && ${value(0.5)} && ${P}.onSlider()` }));
je.push(...key('JE TV Left', 'ArrowLeft', { sleep: 250, expect: `${handled()} && ${value(0.5)} && ${P}.onSlider()` }));
je.push(...key('JE TV Up', 'ArrowUp', { sleep: 400, expect: `${leftAlone()} && !${P}.onSlider()`, expectName: 'left to the D-pad: the focus moved up' }));
je.push(...key('JE TV Down back', 'ArrowDown', { sleep: 400, expect: `${P}.onSlider()`, expectName: 'back on the slider' }));
je.push(...key('JE TV Down out', 'ArrowDown', { sleep: 400, expect: `${leftAlone()} && !${P}.onSlider()`, expectName: 'left to the D-pad: the focus moved down' }));
// Tagged Movie, where JE draws its review section (a <details> whose <summary> title jellyfin-web's focus manager doesn't
// take): recorded, not asserted, since what the D-pad does there is jellyfin-web's and JE's (the widget only leaves Up and
// Down alone, checked below).
je.push(
    { name: 'JE TV: Tagged Movie', script: "location.hash = '#/details?id=' + __vars.taggedId; return true;" },
    { name: 'JE TV: drawn (Tagged Movie)', waitFor: DRAWN('taggedId'), timeout: 30000 },
    { sleep: 5000 },
    { name: 'JE TV: mount on Tagged Movie', eval: `${P}.mount({})`, full: true },
    { name: 'JE TV: focus the Play button (Tagged Movie)', script: `${P}.log.length = 0; document.querySelector('#itemDetailPage:not(.hide) .mainDetailButtons .btnPlay').focus(); return ${P}.active();` }
);
for (let i = 1; i <= 8; i++) {
    je.push({ name: `JE TV Tagged: ArrowDown ${i}`, key: 'ArrowDown', if: `!${P}.onSlider()` }, { sleep: 200, if: `!${P}.onSlider()` });
}

je.push(
    { name: 'JE TV Tagged: where the D-pad went from Play (recorded: JE\'s review section in the way)', eval: `({ onSlider: ${P}.onSlider(), active: ${P}.active(), presses: ${P}.log.filter(e => e.kind === 'key-capture' && e.key === 'ArrowDown').length, prevented: ${P}.log.filter(e => e.kind === 'key-bubble' && e.key === 'ArrowDown').map(e => e.prevented), path: ${P}.log.filter(e => e.kind === 'focus').map(f => f.target) })`, save: 'jeTvTagged', full: true },
    { name: 'JE TV Tagged: the slider focused', script: `${P}.widget.slider.focus(); return ${P}.onSlider();` }
);
je.push(...key('JE TV Tagged Right', 'ArrowRight', { sleep: 250, expect: `${handled()} && ${value(0.5)}` }));
je.push(...key('JE TV Tagged Up', 'ArrowUp', { sleep: 400, expect: leftAlone(), expectName: 'not the slider\'s (left to jellyfin-web)' }));
je.push(
    { name: 'the default layout again', eval: "(localStorage.removeItem('layout'), true)" },
    { name: 'every JE action the spies saw', eval: 'window.__jeSpy ? window.__jeSpy.calls : null', full: true },
    ...noApi,
    { name: 'no uncaught exception of ours', noExceptions: true, ignore: IGNORE + '|JellyfinEnhanced|/JellyfinEnhanced/' }
);
write('userratings-stars-je.json', 'User Ratings ' + SHORT_VERSION + ' next to Jellyfin Enhanced, in the je sandbox as admin: Jellyfin Enhanced\'s version and its review settings (names and kinds only); on Long Test Movie every key the slider handles reaches it unprevented past JE\'s capture listener, is handled, and JE does nothing; JE\'s own keys (3 S I C + - B) aren\'t the slider\'s; no style rule of JE\'s (or of anyone but jellyfin-web) selects a node of the widget, and our ids are unique, also on Tagged Movie with JE\'s review section drawn; JE\'s review parts recorded; the TV layout with JE loaded: on Long Test Movie the D-pad reaches the slider, Left/Right handled, Up and Down move on; on Tagged Movie where the D-pad goes from Play (JE\'s review section) recorded, the slider\'s keys the same. No setting is changed.', je);
