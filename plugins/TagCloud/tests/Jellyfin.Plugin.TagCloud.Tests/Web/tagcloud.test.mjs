// Tag Cloud's web client. ClientScriptTests runs it with the built ClientScript:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jftc node --test plugins/TagCloud/tests/Jellyfin.Plugin.TagCloud.Tests/Web/tagcloud.test.mjs
// The layout's cases pin how words are picked, sized, shaded and ordered. The packer runs over synthetic sprites, and in full through a fake
// canvas whose words are solid boxes, for the size mixing's measure. The script itself starts in
// the kit's fake page. The titles list's view and the picture card's place are pure (32-titles.js); the renderer's picture
// card runs in the fake page with the kit and the cloud's own files.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { createPage, loadKit } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');

// The layout and the packer on their own: plain functions over plain data, evaluated without the page.
function loadPure(extra) {
    const read = (name) => fs.readFileSync(new URL('../../../Jellyfin.Plugin.TagCloud/Web/js/' + name, import.meta.url), 'utf8');
    const context = Object.assign({ performance, setTimeout, Math, WeakMap, Uint8Array, Uint32Array, Promise }, extra || {});
    vm.createContext(context);
    vm.runInContext(read('30-layout.js') + '\n' + read('32-titles.js') + '\n' + read('35-pack.js') + '\nthis.tcLayout = tcLayout; this.tcPack = tcPack; this.tcTitleView = tcTitleView;', context);
    return context;
}

const { tcLayout, tcPack, tcTitleView } = loadPure();

function word(text, weight, key) {
    return { key: key === undefined ? text : key, text: text, weight: weight };
}

function arrange(items, opts) {
    opts = opts || {};
    return tcLayout.arrange(items, opts.maxItems === undefined ? 100 : opts.maxItems, opts.min === undefined ? 1 : opts.min, opts.max === undefined ? 3 : opts.max, opts.scale || 'Linear', opts.order || 'ByWeight');
}

function find(placed, text) {
    return placed.filter((p) => p.item.text === text)[0];
}

test('the heaviest gets the largest size and the lightest the smallest', () => {
    const placed = arrange([word('a', 1), word('b', 5), word('c', 9)]);
    assert.equal(find(placed, 'c').fontSize, 3);
    assert.equal(find(placed, 'a').fontSize, 1);
    assert.equal(find(placed, 'b').fontSize, 2);
});

test('equal weights all sit at the midpoint', () => {
    const placed = arrange([word('a', 4), word('b', 4)]);
    placed.forEach((p) => {
        assert.equal(p.fontSize, 2);
        assert.equal(p.fraction, 0.5);
    });
});

test('tiers span one to five', () => {
    const placed = arrange(Array.from({ length: 11 }, (_, i) => word('w' + i, i)));
    assert.equal(Math.min(...placed.map((p) => p.tier)), 1);
    assert.equal(Math.max(...placed.map((p) => p.tier)), tcLayout.TIER_COUNT);
    assert.equal(find(placed, 'w10').tier, tcLayout.TIER_COUNT);
});

test('maxItems keeps the heaviest', () => {
    const placed = arrange([word('light', 1), word('heavy', 10), word('middle', 5)], { maxItems: 2 });
    assert.deepEqual(placed.map((p) => p.item.text), ['heavy', 'middle']);
});

test('duplicate keys keep the first, and blank text is dropped', () => {
    const placed = arrange([word('Alien', 3), word('Alien', 9), word(' ', 7), word('Ripley', 2, 'p1'), word('Ripley', 1, 'p2')]);
    assert.equal(placed.length, 3);
    assert.equal(find(placed, 'Alien').item.weight, 3);
    assert.equal(placed.filter((p) => p.item.text === 'Ripley').length, 2);
});

test('negative weights count as zero', () => {
    const placed = arrange([word('neg', -5), word('zero', 0), word('one', 1)]);
    assert.equal(find(placed, 'neg').fontSize, find(placed, 'zero').fontSize);
});

test('the square root and logarithmic scales lift the middle', () => {
    const items = [word('a', 0), word('b', 25), word('c', 100)];
    const middle = (scale) => find(arrange(items, { scale: scale }), 'b').fraction;
    assert.ok(Math.abs(middle('Linear') - 0.25) < 1e-3);
    assert.ok(Math.abs(middle('SquareRoot') - 0.5) < 1e-3);
    assert.ok(middle('Logarithmic') > middle('SquareRoot'));
});

test('centered puts the heaviest in the middle', () => {
    const placed = arrange(Array.from({ length: 9 }, (_, i) => word('w' + (i + 1), (i + 1) * (i + 1))), { order: 'Centered' });
    const heaviest = placed.findIndex((p) => p.item.text === 'w9');
    assert.ok(heaviest >= 3 && heaviest <= 5, String(heaviest));
    assert.equal(placed[0].tier, 1);
    assert.equal(placed[placed.length - 1].tier, 1);
});

test('the shuffle is stable across calls', () => {
    const items = Array.from({ length: 30 }, (_, i) => word('word' + i, i));
    const first = arrange(items, { order: 'Shuffled' }).map((p) => p.item.text);
    const second = arrange(items.slice().reverse(), { order: 'Shuffled' }).map((p) => p.item.text);
    assert.deepEqual(first, second);
    assert.notDeepEqual(first, items.map((i) => i.text));
});

test('alphabetical ignores case', () => {
    const placed = arrange([word('banana', 1), word('Apple', 2), word('cherry', 3)], { order: 'Alphabetical' });
    assert.deepEqual(placed.map((p) => p.item.text), ['Apple', 'banana', 'cherry']);
});

test('reversed font size bounds are put right', () => {
    const placed = tcLayout.arrange([word('a', 1), word('b', 2)], 10, 3, 1, 'Linear', 'ByWeight');
    assert.equal(placed[0].fontSize, 3);
    assert.equal(placed[1].fontSize, 1);
});

test('no input gives nothing', () => {
    assert.equal(tcLayout.arrange(null, 10, 1, 3, 'Linear', 'ByWeight').length, 0);
    assert.equal(arrange([]).length, 0);
    assert.equal(arrange([word('a', 1)], { maxItems: 0 }).length, 0);
});

test('the stable hash is the C# FNV-1a over UTF-16 code units', () => {
    // FNV-1a over UTF-16 code units, as C# computes it: 2166136261 (the offset basis) for "".
    assert.equal(tcLayout.stableHash(''), 2166136261);
    let hash = 2166136261n;
    for (const c of 'Horror') {
        hash = ((hash ^ BigInt(c.charCodeAt(0))) * 16777619n) % 4294967296n;
    }

    assert.equal(tcLayout.stableHash('Horror'), Number(hash));
});

test('colours: by word a stable pick, by weight the heaviest first, none keeps the tiers', () => {
    const placed = arrange([word('a', 1), word('b', 9)]);
    const colors = ['red', 'green', 'blue'];
    assert.equal(tcLayout.colorFor(find(placed, 'b'), colors, 'Weight'), 'red');
    assert.equal(tcLayout.colorFor(find(placed, 'a'), colors, 'Weight'), 'blue');
    assert.equal(tcLayout.colorFor(find(placed, 'a'), colors, 'Word'), colors[tcLayout.stableHash('a') % 3]);
    assert.equal(tcLayout.colorFor(find(placed, 'a'), [], 'Word'), null);
});

test('letter case', () => {
    assert.equal(tcLayout.displayText('Zombie', 'Upper'), 'ZOMBIE');
    assert.equal(tcLayout.displayText('Zombie', 'Lower'), 'zombie');
    assert.equal(tcLayout.displayText('Zombie', 'AsIs'), 'Zombie');
});

// ---- The packer -------------------------------------------------------------------------------------------------------

// A solid w x h sprite, as makeSprite builds one from a word whose ink fills its box.
function sprite(key, w, h) {
    const ww = (w + 31) >> 5;
    const bits = new Uint32Array(ww * h);
    for (let y = 0; y < h; y++) {
        for (let x = 0; x < w; x++) {
            bits[y * ww + (x >> 5)] |= 0x80000000 >>> (x & 31);
        }
    }

    return { key, w, h, ww, bits, ox: 1, oy: h - 2, gx: 0, gy: 0, gw: w, gh: h, area: w * h };
}

function overlaps(a, b) {
    return a.x < b.x + b.s.w && b.x < a.x + a.s.w && a.y < b.y + b.s.h && b.y < a.y + a.s.h;
}

test('the packer places every word without overlap, the same way for the same seed', async () => {
    const sprites = Array.from({ length: 60 }, (_, i) => sprite('w' + i, 20 + ((i * 37) % 90), 8 + ((i * 13) % 30)));
    const area = sprites.reduce((s, x) => s + x.area, 0);
    const h = Math.ceil(Math.sqrt(area * 3.2 / 2));
    const w = (Math.ceil(h * 2) + 31) & ~31;
    const ideal = { w: Math.sqrt(area / 2) * 2, h: Math.sqrt(area / 2) };
    const run = () => tcPack.place(sprites, w, h, ideal, tcPack.mulberry32(5), () => false);
    const first = await run();
    const second = await run();

    assert.equal(first.dropped, 0);
    assert.equal(first.placed.length, sprites.length);
    for (let i = 0; i < first.placed.length; i++) {
        for (let j = i + 1; j < first.placed.length; j++) {
            assert.ok(!overlaps(first.placed[i], first.placed[j]), first.placed[i].s.key + ' overlaps ' + first.placed[j].s.key);
        }
    }

    assert.deepEqual(second.placed.map((p) => [p.s.key, p.x, p.y]), first.placed.map((p) => [p.s.key, p.x, p.y]));
});

// A fake page in which a word's ink fills a box as wide as its text and as tall as its size, so the whole layout runs.
function fakeCanvasContext() {
    let font = '16px';
    let lastText = '';
    const sizeOf = () => parseFloat(/(\d+(\.\d+)?)px/.exec(font)[1]);
    return {
        set font(v) {
            font = v;
        },
        get font() {
            return font;
        },
        measureText(text) {
            lastText = text;
            const size = sizeOf();
            return { width: text.length * size * 0.6, actualBoundingBoxLeft: 0, actualBoundingBoxRight: Math.ceil(text.length * size * 0.6), actualBoundingBoxAscent: Math.ceil(size * 0.75), actualBoundingBoxDescent: Math.ceil(size * 0.2) };
        },
        clearRect() {},
        fillText() {},
        getImageData(x, y, w, h) {
            const data = new Uint8ClampedArray(w * h * 4);
            const size = sizeOf();
            const inkW = Math.ceil(lastText.length * size * 0.6);
            const inkH = Math.ceil(size * 0.95);
            for (let row = 1; row < Math.min(h - 1, inkH + 1); row++) {
                for (let col = 1; col < Math.min(w - 1, inkW + 1); col++) {
                    data[(row * w + col) * 4 + 3] = 255;
                }
            }

            return { data };
        }
    };
}

function fakeSvg(words) {
    const texts = words.map((w) => ({ size: w.size, textContent: w.text, getAttribute: (name) => (name === 'data-key' ? w.key : null) }));
    return { isConnected: true, querySelectorAll: () => texts };
}

function rankCorrelation(a, b) {
    const rank = (v) => {
        const order = v.map((x, i) => [x, i]).sort((p, q) => p[0] - q[0]);
        const r = new Array(v.length);
        order.forEach(([, i], k) => {
            r[i] = k;
        });
        return r;
    };
    const ra = rank(a);
    const rb = rank(b);
    const m = (a.length - 1) / 2;
    let num = 0;
    let da = 0;
    let db = 0;
    for (let i = 0; i < a.length; i++) {
        num += (ra[i] - m) * (rb[i] - m);
        da += (ra[i] - m) ** 2;
        db += (rb[i] - m) ** 2;
    }

    return num / Math.sqrt(da * db);
}

// Solid boxes leave gaps between big words that small ones fill anywhere, so the shapes here aren't letters: how evenly
// full mixing spreads the sizes was tuned on real glyphs (FULL_MIX; −0.82 unmixed, −0.03 mixed). What
// holds whatever the shapes: unmixed, the biggest tenth sits clearly nearer the middle than the smallest half, and
// mixing moves the biggest out.
test('size mixing: 0 keeps the heaviest in the middle, mixing moves them out', async () => {
    const pack = loadPure({
        document: { createElement: () => ({ width: 0, height: 0, getContext: () => fakeCanvasContext() }) },
        getComputedStyle: (el) => ({ fontStyle: 'normal', fontWeight: '400', fontSize: el.size + 'px', fontFamily: 'sans-serif' })
    }).tcPack;
    const words = Array.from({ length: 200 }, (_, i) => ({ key: 'k' + i, text: 'word' + (i % 9) + 'x'.repeat(i % 5), size: 8 + Math.round(60 * Math.pow((200 - i) / 200, 2)) }));
    const score = async (mix, seed) => {
        const result = await pack.layout(fakeSvg(words), { padX: 2, padY: 1, aspectRatio: 1.6, seed: seed, mix: mix });
        const cx = result.x + result.width / 2;
        const cy = result.y + result.height / 2;
        const size = {};
        words.forEach((w) => {
            size[w.key] = w.size;
        });
        const placed = result.words.map((w) => ({
            size: size[w.key],
            distance: Math.hypot((w.boxX + w.boxWidth / 2 - cx) / (result.width / 2), (w.boxY + w.boxHeight / 2 - cy) / (result.height / 2))
        })).sort((a, b) => b.size - a.size);
        const mean = (list) => list.reduce((s, p) => s + p.distance, 0) / list.length;
        return {
            placed: placed.length,
            big: mean(placed.slice(0, placed.length / 10)),
            small: mean(placed.slice(placed.length / 2)),
            rho: rankCorrelation(placed.map((p) => p.size), placed.map((p) => p.distance))
        };
    };

    for (const seed of [5, 6, 7]) {
        const none = await score(0, seed);
        const full = await score(1, seed);
        assert.equal(none.placed, words.length);
        assert.equal(full.placed, words.length);
        assert.ok(none.big < none.small - 0.15, 'seed ' + seed + ' unmixed: big ' + none.big + ', small ' + none.small);
        assert.ok(none.rho < 0, 'seed ' + seed + ' unmixed rho ' + none.rho);
        assert.ok(full.big > none.big + 0.1, 'seed ' + seed + ' mixed: big ' + full.big + ' (unmixed ' + none.big + ')');
    }
});

// Fully mixed, long names come late and find the middle full. Taking the first free spot put many out to the side on
// their own, and the cloud came out a good deal wider than its shape; they now go where they stick out of the oval least.
test('size mixing: long late words keep to the cloud\'s shape, not out to the side', async () => {
    const pack = loadPure({
        document: { createElement: () => ({ width: 0, height: 0, getContext: () => fakeCanvasContext() }) },
        getComputedStyle: (el) => ({ fontStyle: 'normal', fontWeight: '700', fontSize: el.size + 'px', fontFamily: 'sans-serif' })
    }).tcPack;
    const first = ['JOHN', 'MARY', 'GEORGE', 'LINNEA', 'CAMERON', 'DICK', 'WILLIAM', 'MICHELLE', 'ROBERT', 'KAREN'];
    const last = ['SMITH', 'MILLER', 'CARRADINE', 'QUIGLEY', 'MITCHELL', 'WILLIAMSON', 'HAUSER', 'BAUER', 'ROUNDTREE', 'KENNEDY'];
    const words = Array.from({ length: 200 }, (_, i) => ({ key: 'k' + i, text: first[(i * 7) % 10] + ' ' + last[(i * 3 + 1) % 10], size: 7 + Math.round(48 * Math.pow((200 - i) / 200, 2.2)) }));
    const ratio = 2.4;
    const shapes = [];
    for (const seed of [1, 2, 3, 4]) {
        const result = await pack.layout(fakeSvg(words), { padX: 2, padY: 1, aspectRatio: ratio, seed: seed, mix: 1 });
        assert.equal(result.words.length, words.length);
        shapes.push(result.width / result.height);
    }

    const mean = shapes.reduce((s, x) => s + x, 0) / shapes.length;
    assert.ok(mean < ratio * 1.04, 'fully mixed clouds came out ' + shapes.map((x) => x.toFixed(2)).join(', ') + ' wide for a shape of ' + ratio);
});

// ---- The script in the page -------------------------------------------------------------------------------------------

test('the client script starts in the page without errors', { skip }, async () => {
    const page = createPage();
    page.run(source, file);
    await page.advance(50);
    page.installApiClient({ userId: 'user-1', items: {} });
    await page.advance(100);
    assert.deepEqual(page.errors, []);
    assert.ok(page.context.__jfkit && page.context.__jfkit.jftc, 'window.__jfkit.jftc');
});

// ---- A word's titles: the list's view --------------------------------------------------------------------------------

const MOVIES = { one: 'movie', many: 'movies' };

test('the titles view: a stored one is checked, unknown values go back to their defaults', () => {
    assert.deepEqual({ ...tcTitleView.normalize(null) }, { Sort: 'Name', Descending: false, MinResolution: 'Any', Unwatched: false });
    assert.deepEqual({ ...tcTitleView.normalize({ Sort: 'Rating', Descending: true, MinResolution: 'P4K', Unwatched: true }) }, { Sort: 'Rating', Descending: true, MinResolution: 'P4K', Unwatched: true });
    assert.deepEqual({ ...tcTitleView.normalize({ Sort: 'Popularity', Descending: 'yes', MinResolution: '8K', Unwatched: 1 }) }, { Sort: 'Name', Descending: false, MinResolution: 'Any', Unwatched: false });
    assert.deepEqual({ ...tcTitleView.normalize('garbage') }, { ...tcTitleView.normalize(null) });
});

test('the titles view: the request leaves the defaults out, and the resolution out but for movies', () => {
    const defaults = tcTitleView.normalize(null);
    assert.deepEqual({ ...tcTitleView.query(defaults, '  ', 'Movies') }, { sort: null, descending: null, q: null, minResolution: null, unwatched: null });
    const view = { Sort: 'DateAdded', Descending: true, MinResolution: 'P1080', Unwatched: true };
    assert.deepEqual({ ...tcTitleView.query(view, ' Amélie ', 'Movies') }, { sort: 'DateAdded', descending: 'true', q: 'Amélie', minResolution: 'P1080', unwatched: 'true' });
    assert.equal(tcTitleView.query(view, '', 'Shows').minResolution, null);
    assert.equal(tcTitleView.query(view, '', 'Both').minResolution, null);
    assert.equal(tcTitleView.query(defaults, 'x'.repeat(150), 'Movies').q.length, tcTitleView.MAX_QUERY);
});

test('the titles view: Reset shows only when something is changed', () => {
    const defaults = tcTitleView.normalize(null);
    assert.equal(tcTitleView.isDefault(defaults, '', 'Movies'), true);
    assert.equal(tcTitleView.isDefault(defaults, 'heat', 'Movies'), false);
    assert.equal(tcTitleView.isDefault({ ...defaults, Descending: true }, '', 'Movies'), false);
    assert.equal(tcTitleView.isDefault({ ...defaults, MinResolution: 'P720' }, '', 'Movies'), false);
    // The resolution doesn't apply to shows: kept for movies, it changes nothing there.
    assert.equal(tcTitleView.isDefault({ ...defaults, MinResolution: 'P720' }, '', 'Shows'), true);
});

test('the titles view: the direction says itself for the sort, and the count says how many the filters keep', () => {
    assert.deepEqual([tcTitleView.sortOf('Name').up, tcTitleView.sortOf('Name').down], ['A to Z', 'Z to A']);
    assert.deepEqual([tcTitleView.sortOf('ReleaseDate').up, tcTitleView.sortOf('ReleaseDate').down], ['Oldest first', 'Newest first']);
    assert.deepEqual([tcTitleView.sortOf('Rating').up, tcTitleView.sortOf('Rating').down], ['Lowest first', 'Highest first']);
    assert.equal(tcTitleView.sortOf('nonsense').value, 'Name');
    assert.equal(tcTitleView.countText(12, 12, MOVIES), '12 movies');
    assert.equal(tcTitleView.countText(3, 12, MOVIES), '3 of 12 movies');
    assert.equal(tcTitleView.countText(0, 1, MOVIES), '0 of 1 movie');
    assert.equal(tcTitleView.countText(1, 1, MOVIES), '1 movie');
});

test('the picture card goes right of the pointer, left of it near the edge, and stays in the window', () => {
    assert.deepEqual({ ...tcTitleView.peekPosition(100, 300, 170, 290, 1200, 800) }, { left: 118, top: 155 });
    // Too near the right edge: to the left of the pointer.
    assert.deepEqual({ ...tcTitleView.peekPosition(1100, 300, 170, 290, 1200, 800) }, { left: 912, top: 155 });
    // Near the top and the bottom: kept 8 px inside.
    assert.equal(tcTitleView.peekPosition(100, 20, 170, 290, 1200, 800).top, 8);
    assert.equal(tcTitleView.peekPosition(100, 790, 170, 290, 1200, 800).top, 800 - 290 - 8);
    // A window narrower than the card: its left edge stays in.
    assert.equal(tcTitleView.peekPosition(50, 300, 170, 290, 150, 800).left, 8);
});

// ---- The renderer's picture card, in the kit's fake page ---------------------------------------------------------------

function cloudPage() {
    const page = createPage();
    loadKit(page);
    const read = (name) => fs.readFileSync(new URL('../../../Jellyfin.Plugin.TagCloud/Web/js/' + name, import.meta.url), 'utf8');
    page.run("const kit = createKit({ prefix: 'jftc' }, '', {});\n" + ['20-api.js', '30-layout.js', '32-titles.js', '35-pack.js', '38-look.js', '40-cloud.js'].map(read).join('\n') +
        '\nglobalThis.__tc = { createTcCloud, TC_LOOK_DEFAULTS, TC_PEEK_MS };', 'tagcloud-parts.js');
    // The fake page has no layout: every box is the card's size, and the window 1200 by 800.
    const proto = Object.getPrototypeOf(page.context.document.createElement('div'));
    proto.getBoundingClientRect = () => ({ left: 0, top: 0, width: 170, height: 290 });
    page.context.innerWidth = 1200;
    page.context.innerHeight = 800;
    return page;
}

function pointer(page, node, type, pointerType, x, y) {
    const event = new page.context.Event(type, { bubbles: type !== 'pointerenter' && type !== 'pointerleave' });
    event.pointerType = pointerType;
    event.clientX = x;
    event.clientY = y;
    event.button = 0;
    node.dispatchEvent(event);
}

async function drawnCloud(page, peek) {
    const { createTcCloud, TC_LOOK_DEFAULTS } = page.context.__tc;
    const view = createTcCloud({ label: 'Test' });
    // The fake page's style is a plain object: the cloud sets its ink colour through setProperty.
    view.element.style.setProperty = function (name, value) {
        this[name] = value;
    };
    page.context.document.body.append(view.element);
    const look = Object.assign({}, TC_LOOK_DEFAULTS, { Arrangement: 'Flow' });
    view.update({
        items: [{ key: 'weaver', text: 'Sigourney Weaver', weight: 2 }, { key: 'pacino', text: 'Al Pacino', weight: 1 }],
        look: look,
        title: (item) => item.text + ': ' + item.weight,
        onSelect: () => {},
        peek: peek
    });
    await page.advance(10);
    return { view, words: page.context.document.querySelectorAll('.jftc-flow-word') };
}

test('resting the mouse on a word shows its picture card, without the word\'s tooltip, until it leaves', async () => {
    const page = cloudPage();
    const asked = [];
    const { words } = await drawnCloud(page, (item) => {
        asked.push(item.key);
        return { name: item.text, detail: item.weight + ' movies', imageUrl: 'http://127.0.0.1/Items/p/Images/Primary?tag=t', icon: 'person' };
    });
    const doc = page.context.document;
    const word = Array.from(words).filter((w) => w.getAttribute('data-key') === 'weaver')[0];
    assert.equal(word.getAttribute('title'), 'Sigourney Weaver: 2');

    pointer(page, word, 'pointerenter', 'mouse', 100, 300);
    await page.advance(page.context.__tc.TC_PEEK_MS - 10);
    assert.equal(doc.querySelector('.jftc-peek'), null, 'not before the pause');
    await page.advance(20);
    const card = doc.querySelector('.jftc-peek');
    assert.ok(card, 'the card shows');
    assert.deepEqual(asked, ['weaver']);
    assert.equal(card.getAttribute('aria-hidden'), 'true');
    assert.equal(card.querySelector('.jftc-peek-name').textContent, 'Sigourney Weaver');
    assert.equal(card.querySelector('.jftc-peek-detail').textContent, '2 movies');
    assert.equal(card.querySelector('img').getAttribute('src'), 'http://127.0.0.1/Items/p/Images/Primary?tag=t');
    assert.equal(card.style.left, '118px');
    assert.equal(word.getAttribute('title'), null, 'the native tooltip is set aside');

    pointer(page, word, 'pointerleave', 'mouse', 400, 300);
    assert.equal(doc.querySelector('.jftc-peek'), null, 'gone on leave');
    assert.equal(word.getAttribute('title'), 'Sigourney Weaver: 2');

    // A scroll, or a press, puts it away too.
    pointer(page, word, 'pointerenter', 'mouse', 100, 300);
    await page.advance(400);
    assert.ok(doc.querySelector('.jftc-peek'));
    page.context.dispatchEvent(new page.context.Event('scroll'));
    assert.equal(doc.querySelector('.jftc-peek'), null, 'gone on scroll');
    pointer(page, word, 'pointerleave', 'mouse', 400, 300);
    pointer(page, word, 'pointerenter', 'mouse', 100, 300);
    await page.advance(400);
    pointer(page, word, 'pointerdown', 'mouse', 100, 300);
    assert.equal(doc.querySelector('.jftc-peek'), null, 'gone on a press');
    assert.equal(word.getAttribute('title'), 'Sigourney Weaver: 2');
    assert.deepEqual(page.errors, []);
});

test('the picture card: never for touch or pen, never without peek, and an icon without a picture', async () => {
    const page = cloudPage();
    const { words } = await drawnCloud(page, (item) => ({ name: item.text, imageUrl: null, icon: 'person' }));
    const doc = page.context.document;

    for (const kind of ['touch', 'pen']) {
        pointer(page, words[1], 'pointerenter', kind, 100, 300);
        await page.advance(500);
        assert.equal(doc.querySelector('.jftc-peek'), null, kind);
        pointer(page, words[1], 'pointerleave', kind, 100, 300);
    }

    pointer(page, words[1], 'pointerenter', 'mouse', 100, 300);
    // Moving on before the pause shows nothing.
    pointer(page, words[1], 'pointerleave', 'mouse', 100, 300);
    await page.advance(500);
    assert.equal(doc.querySelector('.jftc-peek'), null);

    pointer(page, words[1], 'pointerenter', 'mouse', 100, 300);
    await page.advance(500);
    const card = doc.querySelector('.jftc-peek');
    assert.ok(card.querySelector('.jftc-peek-icon'), 'an icon in place of the picture');
    assert.equal(card.querySelector('.jftc-peek-detail'), null);

    const plain = cloudPage();
    const drawn = await drawnCloud(plain, null);
    pointer(plain, drawn.words[0], 'pointerenter', 'mouse', 100, 300);
    await plain.advance(500);
    assert.equal(plain.context.document.querySelector('.jftc-peek'), null, 'no peek, no card');
});
