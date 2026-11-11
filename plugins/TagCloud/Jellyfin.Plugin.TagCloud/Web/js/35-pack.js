// The packed arrangement: a Wordle-style layout that fits each word against the actual shapes of the letters
// already placed, so small words tuck into the gaps between big ones. With size
// mixing, which spreads big words away from the middle; the kit builds one IIFE, so the module's
// export is tcPack.layout here.
//
// The renderer draws every word once, hidden, as an SVG <text> carrying its real classes and font size. This reads each
// one's computed font, draws it to a canvas to get a pixel mask of its ink (grown by the requested spacing), and places
// the masks heaviest-first along an Archimedean spiral out from the center, testing each candidate spot against a
// bitboard of everything placed so far. It hands back where every word landed; the renderer draws them.
//
// Long layouts give the browser a turn every few milliseconds, and a newer layout of the same cloud cancels an older one
// still running, so dragging a setting never queues up stale work.

const tcPack = (function () {
    const runs = new WeakMap();
    const SLICE_MS = 12;

    // How shuffled a fully mixed cloud's placing order is. Past this, the heaviest words are mostly left for last and
    // crowd the edge instead, the opposite of the usual cloud rather than a mix.
    const FULL_MIX = 0.8;

    // Lays out the words of a measuring <svg>, which holds one <text data-key="..."> per word, styled as it will be drawn.
    // options: { padX, padY, aspectRatio, seed, mix } (mix: 0 settles the heaviest words in the middle, 1 spreads every
    // size across the whole cloud). Resolves { x, y, width, height, words: [{ key, x, y, boxX, boxY, boxWidth,
    // boxHeight }] }, or null when superseded by a newer layout of the same cloud, or when the cloud was removed.
    async function layout(svg, options) {
        options = options || {};
        const run = (runs.get(svg) || 0) + 1;
        runs.set(svg, run);
        const superseded = function () {
            return runs.get(svg) !== run || !svg.isConnected;
        };

        // Measuring with a fallback font while a web font is still loading would mis-size everything.
        if (document.fonts && document.fonts.ready) {
            try {
                await document.fonts.ready;
            } catch (e) {
                // Measure with what there is.
            }
        }

        if (superseded()) {
            return null;
        }

        const padX = clampInt(options.padX, 0, 32, 2);
        const padY = clampInt(options.padY, 0, 32, 1);
        const ratio = clamp(Number(options.aspectRatio) || 2, 0.25, 8);
        const seed = (Number(options.seed) || 0) | 0;
        const mix = clamp(Number(options.mix) || 0, 0, 1);

        const words = Array.prototype.map.call(svg.querySelectorAll('text[data-key]'), function (el, index) {
            const style = getComputedStyle(el);
            return {
                key: el.getAttribute('data-key'),
                text: el.textContent || '',
                index: index,
                font: style.fontStyle + ' ' + style.fontWeight + ' ' + style.fontSize + ' ' + style.fontFamily,
                size: parseFloat(style.fontSize) || 16
            };
        }).filter(function (w) {
            return w.text.trim().length > 0;
        });

        // Heaviest first: the big words claim the middle and the small ones fill in around them.
        words.sort(function (a, b) {
            return b.size - a.size || a.index - b.index;
        });

        // Mixing shuffles that order, so some heavy words come after light ones have taken the middle and settle wherever
        // there's still room, out to the edge. Every word still spirals out from near the middle to the first spot free,
        // so it lands against the others, never off alone. The square root evens out the steps.
        if (mix > 0) {
            const amount = FULL_MIX * Math.sqrt(mix);
            const shuffle = mulberry32(seed ^ 0x2545f491);
            const last = Math.max(1, words.length - 1);
            words.forEach(function (w, i) {
                w.order = (i / last) * (1 - amount) + shuffle() * amount;
            });
            words.sort(function (a, b) {
                return a.order - b.order;
            });
        }

        const canvas = document.createElement('canvas');
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        if (!ctx) {
            throw new Error('No 2D canvas to measure the words with.');
        }

        const sprites = [];
        let area = 0;
        let widest = 0;
        let tallest = 0;
        let slice = performance.now();

        for (let i = 0; i < words.length; i++) {
            const sprite = makeSprite(ctx, canvas, words[i], padX, padY);
            if (!sprite) {
                continue;
            }

            sprites.push(sprite);
            area += sprite.area;
            widest = Math.max(widest, sprite.w);
            tallest = Math.max(tallest, sprite.h);

            if (performance.now() - slice > SLICE_MS) {
                await yieldToBrowser();
                if (superseded()) {
                    return null;
                }

                slice = performance.now();
            }
        }

        if (sprites.length === 0) {
            return { x: 0, y: 0, width: 0, height: 0, words: [] };
        }

        // The size the packed cloud should roughly come out at, which sets how widely words start.
        const idealH = Math.sqrt(area / ratio);
        const ideal = { w: idealH * ratio, h: idealH };

        // Start with a board a good deal bigger than that, growing it if words don't all fit. Its size only bounds how far
        // a word may wander; words still settle as near the middle as fits.
        let result = null;
        for (let attempt = 0; attempt < 4; attempt++) {
            const boardArea = area * (3.2 + attempt * 1.6);
            let height = Math.ceil(Math.sqrt(boardArea / ratio));
            let width = Math.ceil(height * ratio);
            width = Math.max(width, widest + 64);
            height = Math.max(height, tallest + 16);
            width = (width + 31) & ~31;

            result = await place(sprites, width, height, ideal, mulberry32(seed + attempt * 7919), superseded);
            if (result === null) {
                return null;
            }

            if (result.dropped === 0) {
                break;
            }
        }

        return finish(result.placed);
    }

    // ---- sprites ----------------------------------------------------------------------------------------------------

    // Draws one word and turns its ink into a bitmask, grown by the padding.
    function makeSprite(ctx, canvas, word, padX, padY) {
        ctx.font = word.font;
        const m = ctx.measureText(word.text);
        const left = Math.ceil(m.actualBoundingBoxLeft || 0);
        const right = Math.ceil(m.actualBoundingBoxRight || m.width);
        const ascent = Math.ceil(m.actualBoundingBoxAscent || word.size * 0.8);
        const descent = Math.ceil(m.actualBoundingBoxDescent || word.size * 0.2);

        // The inked box, with a pixel of margin for anti-aliasing on every side.
        const gw = left + right + 2;
        const gh = ascent + descent + 2;
        if (gw <= 2 || gh <= 2) {
            return null;
        }

        const w = gw + 2 * padX;
        const h2 = gh + 2 * padY;

        // Growing the canvas resets its state, so the font is set again after.
        if (canvas.width < w) {
            canvas.width = w;
        }

        if (canvas.height < h2) {
            canvas.height = h2;
        }

        ctx.font = word.font;
        ctx.textBaseline = 'alphabetic';
        ctx.textAlign = 'left';
        ctx.fillStyle = '#000';
        ctx.clearRect(0, 0, w, h2);

        const ox = padX + 1 + left;
        const oy = padY + 1 + ascent;
        ctx.fillText(word.text, ox, oy);

        const alpha = ctx.getImageData(0, 0, w, h2).data;
        let ink = new Uint8Array(w * h2);
        let inked = 0;
        for (let i = 0, p = 3; i < ink.length; i++, p += 4) {
            if (alpha[p] > 48) {
                ink[i] = 1;
                inked++;
            }
        }

        if (inked === 0) {
            return null;
        }

        if (padX) {
            ink = dilateRows(ink, w, h2, padX);
        }

        if (padY) {
            ink = dilateColumns(ink, w, h2, padY);
        }

        const ww = (w + 31) >> 5;
        const bits = new Uint32Array(ww * h2);
        for (let y = 0; y < h2; y++) {
            const row = y * w;
            for (let x = 0; x < w; x++) {
                if (ink[row + x]) {
                    bits[y * ww + (x >> 5)] |= 0x80000000 >>> (x & 31);
                }
            }
        }

        // area: the padded box's, which sizes the board.
        return { key: word.key, w: w, h: h2, ww: ww, bits: bits, ox: ox, oy: oy, gx: padX, gy: padY, gw: gw, gh: gh, area: w * h2 };
    }

    function dilateRows(ink, w, h2, r) {
        const out = new Uint8Array(ink.length);
        for (let y = 0; y < h2; y++) {
            const row = y * w;
            for (let x = 0; x < w; x++) {
                if (!ink[row + x]) {
                    continue;
                }

                const from = Math.max(0, x - r);
                const to = Math.min(w - 1, x + r);
                for (let k = from; k <= to; k++) {
                    out[row + k] = 1;
                }
            }
        }

        return out;
    }

    function dilateColumns(ink, w, h2, r) {
        const out = new Uint8Array(ink.length);
        for (let y = 0; y < h2; y++) {
            for (let x = 0; x < w; x++) {
                if (!ink[y * w + x]) {
                    continue;
                }

                const from = Math.max(0, y - r);
                const to = Math.min(h2 - 1, y + r);
                for (let k = from; k <= to; k++) {
                    out[k * w + x] = 1;
                }
            }
        }

        return out;
    }

    // ---- placement --------------------------------------------------------------------------------------------------

    async function place(sprites, w, h2, ideal, random, superseded) {
        // Two words of slack per row: a sprite shifted off a word boundary spills into one more.
        const rowWords = (w >> 5) + 2;
        const board = new Uint32Array(rowWords * h2);
        const cx = w / 2;
        const cy = h2 / 2;
        const stretch = w / h2;
        const maxRadius = Math.max(w, h2);
        const placed = [];
        let dropped = 0;
        let slice = performance.now();

        for (let i = 0; i < sprites.length; i++) {
            const s = sprites[i];

            // The first word sits dead center; the rest start near it, scattered a little, and spiral outward
            // (alternating direction) until they find room.
            const startX = i === 0 ? cx : cx + (random() - 0.5) * ideal.w * 0.5;
            const startY = i === 0 ? cy : cy + (random() - 0.5) * ideal.h * 0.5;
            const turn = random() < 0.5 ? 1 : -1;

            let spot = null;
            for (let t = 0; ; t++) {
                const r = t * 0.1;
                if (r > maxRadius) {
                    break;
                }

                const a = r * turn;
                const x = Math.round(startX + stretch * r * Math.cos(a) - s.w / 2);
                const y = Math.round(startY + r * Math.sin(a) - s.h / 2);
                if (x >= 0 && y >= 0 && x + s.w <= w && y + s.h <= h2 && !collides(board, rowWords, s, x, y)) {
                    spot = [x, y];
                    break;
                }

                if ((t & 1023) === 0 && performance.now() - slice > SLICE_MS) {
                    await yieldToBrowser();
                    if (superseded()) {
                        return null;
                    }

                    slice = performance.now();
                }
            }

            if (spot) {
                stamp(board, rowWords, s, spot[0], spot[1]);
                placed.push({ s: s, x: spot[0], y: spot[1] });
            } else {
                dropped++;
            }

            if (performance.now() - slice > SLICE_MS) {
                await yieldToBrowser();
                if (superseded()) {
                    return null;
                }

                slice = performance.now();
            }
        }

        return { placed: placed, dropped: dropped };
    }

    // Whether a sprite at (x, y) overlaps anything on the board. Bits run most-significant first.
    function collides(board, rowWords, s, x, y) {
        const shift = x & 31;
        const base = x >> 5;
        for (let r = 0; r < s.h; r++) {
            const brow = (y + r) * rowWords + base;
            const srow = r * s.ww;
            let prev = 0;
            for (let j = 0; j <= s.ww; j++) {
                const cur = j < s.ww ? s.bits[srow + j] : 0;
                const v = shift ? ((cur >>> shift) | (prev << (32 - shift))) : cur;
                if (v && (board[brow + j] & v)) {
                    return true;
                }

                prev = cur;
            }
        }

        return false;
    }

    function stamp(board, rowWords, s, x, y) {
        const shift = x & 31;
        const base = x >> 5;
        for (let r = 0; r < s.h; r++) {
            const brow = (y + r) * rowWords + base;
            const srow = r * s.ww;
            let prev = 0;
            for (let j = 0; j <= s.ww; j++) {
                const cur = j < s.ww ? s.bits[srow + j] : 0;
                const v = shift ? ((cur >>> shift) | (prev << (32 - shift))) : cur;
                if (v) {
                    board[brow + j] |= v;
                }

                prev = cur;
            }
        }
    }

    // Crops to the inked words and reports where each one's baseline and ink landed.
    function finish(placed) {
        let minX = Infinity;
        let minY = Infinity;
        let maxX = -Infinity;
        let maxY = -Infinity;
        const words = placed.map(function (p) {
            const s = p.s;
            const boxX = p.x + s.gx;
            const boxY = p.y + s.gy;
            minX = Math.min(minX, boxX);
            minY = Math.min(minY, boxY);
            maxX = Math.max(maxX, boxX + s.gw);
            maxY = Math.max(maxY, boxY + s.gh);
            return { key: s.key, x: p.x + s.ox, y: p.y + s.oy, boxX: boxX, boxY: boxY, boxWidth: s.gw, boxHeight: s.gh };
        });

        if (words.length === 0) {
            return { x: 0, y: 0, width: 0, height: 0, words: words };
        }

        const margin = 4;
        return { x: minX - margin, y: minY - margin, width: maxX - minX + 2 * margin, height: maxY - minY + 2 * margin, words: words };
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    function yieldToBrowser() {
        return new Promise(function (resolve) {
            setTimeout(resolve, 0);
        });
    }

    // A small seeded random number generator, so the same seed always gives the same cloud.
    function mulberry32(seed) {
        let a = seed >>> 0;
        return function () {
            a = (a + 0x6D2B79F5) >>> 0;
            let t = a;
            t = Math.imul(t ^ (t >>> 15), t | 1);
            t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
            return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
        };
    }

    function clamp(value, min, max) {
        return Math.min(max, Math.max(min, value));
    }

    function clampInt(value, min, max, fallback) {
        const n = Number(value);
        return Number.isFinite(n) ? Math.round(clamp(n, min, max)) : fallback;
    }

    return Object.freeze({
        layout: layout,
        // For the tests: the placement over sprites made without a canvas, and the seeded generator.
        place: place,
        mulberry32: mulberry32
    });
})();
