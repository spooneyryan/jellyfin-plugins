// The cloud's renderer: Cloud (packed, an SVG laid out by
// tcPack) or Rows (flowing lines of plain markup), each word sized and shaded by tcLayout. Words can be clicked (select),
// held (onHold) and right-clicked (onNarrow), and reached with the keyboard; their tooltip is a <title>. A new layout
// keeps the old one up, dimmed, until it arrives, and the same words in the same look are never laid out again. With
// peek(item), a mouse resting on a word (TC_PEEK_MS) shows a small card by the pointer with a picture (a person's photo, a
// title's poster), the name and a detail, while the word's own tooltip is left out; leaving the word, scrolling, a press
// or a new layout puts it away. Touch, pen and the TV's remote never show it.

const TC_SVG_NS = 'http://www.w3.org/2000/svg';

// Hold a word down this long for its titles' cloud; a shorter press is a click.
const TC_HOLD_MS = 450;

// Rest the mouse on a word this long for its picture card.
const TC_PEEK_MS = 300;

// The picture card: one for the page, on document.body (above the cloud's own stacking), never taking the pointer.
const tcPeek = (function () {
    let card = null;
    let timer = null;
    let shown = null;

    function onScroll() {
        hide();
    }

    // The word's own tooltip (an SVG <title>, or a title attribute in Rows) is set aside while the card shows.
    function muteTooltip(node) {
        const title = node.querySelector && node.querySelector('title');
        if (title) {
            const text = title.textContent;
            title.textContent = '';
            return function () {
                title.textContent = text;
            };
        }

        const attribute = node.getAttribute('title');
        if (attribute !== null) {
            node.removeAttribute('title');
            return function () {
                node.setAttribute('title', attribute);
            };
        }

        return function () {};
    }

    function build(peek) {
        const picture = h('div', { class: 'jftc-peek-picture' });
        const icon = function () {
            picture.textContent = '';
            picture.append(kit.icon(peek.icon || 'image', 'jftc-peek-icon'));
        };
        if (peek.imageUrl) {
            const img = h('img', { src: peek.imageUrl, alt: '' });
            img.addEventListener('error', icon);
            picture.append(img);
        } else {
            icon();
        }

        return h('div', { class: 'jftc-peek', 'aria-hidden': 'true' }, [
            picture,
            h('div', { class: 'jftc-peek-name', text: peek.name || '' }),
            peek.detail ? h('div', { class: 'jftc-peek-detail', text: peek.detail }) : null
        ]);
    }

    function show(node, peek, x, y) {
        hide();
        if (!peek || !node.isConnected) {
            return;
        }

        card = build(peek);
        card.style.visibility = 'hidden';
        document.body.append(card);
        const box = card.getBoundingClientRect();
        const at = tcTitleView.peekPosition(x, y, box.width || 170, box.height || 290, window.innerWidth, window.innerHeight);
        card.style.left = at.left + 'px';
        card.style.top = at.top + 'px';
        card.style.visibility = '';
        shown = { node: node, restore: muteTooltip(node) };
        window.addEventListener('scroll', onScroll, true);
    }

    function cancel() {
        if (timer) {
            clearTimeout(timer);
            timer = null;
        }
    }

    function hide() {
        cancel();
        if (card) {
            card.remove();
            card = null;
        }

        if (shown) {
            shown.restore();
            shown = null;
            window.removeEventListener('scroll', onScroll, true);
        }
    }

    return {
        // After TC_PEEK_MS without leaving, show peek() (an object, or null for none) where the pointer is then.
        schedule: function (node, peek, event) {
            hide();
            let x = event.clientX;
            let y = event.clientY;
            const follow = function (move) {
                x = move.clientX;
                y = move.clientY;
            };
            node.addEventListener('pointermove', follow);
            timer = setTimeout(function () {
                timer = null;
                node.removeEventListener('pointermove', follow);
                show(node, peek(), x, y);
            }, TC_PEEK_MS);
            const stop = function () {
                node.removeEventListener('pointermove', follow);
                node.removeEventListener('pointerleave', stop);
            };
            node.addEventListener('pointerleave', stop);
        },
        hide: hide,
        isShown: function () {
            return !!card;
        }
    };
})();

function tcSvg(tag, attrs, children) {
    const node = document.createElementNS(TC_SVG_NS, tag);
    Object.keys(attrs || {}).forEach(function (name) {
        const value = attrs[name];
        if (value !== undefined && value !== null && value !== false) {
            node.setAttribute(name, String(value));
        }
    });
    [].concat(children || []).forEach(function (child) {
        if (child) {
            node.append(child);
        }
    });
    return node;
}

function tcNum(value) {
    return String(Math.round(value * 100) / 100);
}

// options: { label, emptyText, arrangingText }. update(model) draws; model: { items: [{ key, text, weight }], look,
// title(item), selectedKey, spotlitKey, onSelect(key), onHold(item), onNarrow(item), peek(item) } (handlers optional:
// without onSelect the words aren't buttons; peek(item) answers { name, detail, imageUrl, icon } or null for no card).
function createTcCloud(options) {
    options = options || {};
    const root = h('div', { class: 'jftc-cloud', role: 'list', 'aria-label': options.label || 'Word cloud' });
    let model = null;
    let drawn = null;
    let drawnSignature = null;
    let pendingSignature = null;
    let measure = null;
    let failed = false;
    let holdTimer = null;
    let held = false;

    function signatureOf(placements, display, look) {
        const words = placements.map(function (p) {
            return p.item.key + '\u001e' + display[p.item.key] + '\u001e' + p.fontSize + '\u001e' + p.tier;
        }).join('\u001f');
        return [words, look.Arrangement, look.Font, look.WordSpacing, look.LineSpacing, look.Shape, look.Seed, look.SizeMixing].join('\u001d');
    }

    function fontFamily(look) {
        return tcChoice(TC_FONTS, look.Font).family;
    }

    function wordStyle(p, look, color, svg) {
        const family = fontFamily(look);
        return 'font-size:' + p.fontSize + 'rem' + (family ? ';font-family:' + family : '') + (color ? (svg ? ';fill:' : ';color:') + color : '');
    }

    function wordClass(p, color) {
        return 'jftc-word' + (color ? ' jftc-colored' : ' jftc-tier-' + p.tier);
    }

    function clearHold() {
        if (holdTimer) {
            clearTimeout(holdTimer);
            holdTimer = null;
        }
    }

    function wire(node, item) {
        node.addEventListener('pointerenter', function (event) {
            if (event.pointerType === 'mouse' && model && model.peek) {
                const peekOf = model.peek;
                tcPeek.schedule(node, function () {
                    return peekOf(item);
                }, event);
            }
        });
        node.addEventListener('pointerleave', tcPeek.hide);
        node.addEventListener('click', function (event) {
            tcPeek.hide();
            if (held) {
                held = false;
                event.preventDefault();
                return;
            }

            if (model && model.onSelect) {
                model.onSelect(model.selectedKey === item.key ? null : item.key);
            }
        });
        node.addEventListener('keydown', function (event) {
            if ((event.key === 'Enter' || event.key === ' ') && model && model.onSelect) {
                event.preventDefault();
                model.onSelect(model.selectedKey === item.key ? null : item.key);
            } else if ((event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) && model && model.onNarrow) {
                event.preventDefault();
                model.onNarrow(item);
            }
        });
        node.addEventListener('contextmenu', function (event) {
            if (model && model.onNarrow) {
                event.preventDefault();
                clearHold();
                model.onNarrow(item);
            }
        });
        node.addEventListener('pointerdown', function (event) {
            tcPeek.hide();
            held = false;
            clearHold();
            if (!model || !model.onHold || (event.pointerType === 'mouse' && event.button !== 0)) {
                return;
            }

            holdTimer = setTimeout(function () {
                holdTimer = null;
                held = true;
                model.onHold(item);
            }, TC_HOLD_MS);
        });
        ['pointerup', 'pointerleave', 'pointercancel'].forEach(function (type) {
            node.addEventListener(type, clearHold);
        });
    }

    function title(item) {
        return model && model.title ? model.title(item) : item.text + ': ' + item.weight;
    }

    function interactive() {
        return !!(model && model.onSelect);
    }

    function prepare() {
        const look = model.look;
        const placements = tcLayout.arrange(model.items || [], look.MaxWords, look.MinFontSize, look.MaxFontSize, look.Scale, look.Arrangement === 'Flow' ? look.Order : 'ByWeight');
        const display = Object.create(null);
        placements.forEach(function (p) {
            display[p.item.key] = tcLayout.displayText(p.item.text, look.TextCase);
        });
        return { placements: placements, display: display, colors: tcChoice(TC_PALETTES, look.Palette).colors };
    }

    function drawFlow(prepared) {
        const look = model.look;
        const flow = h('div', { class: 'jftc-flow', style: 'gap:' + look.LineSpacing + 'em ' + look.WordSpacing + 'em' });
        prepared.placements.forEach(function (p) {
            const color = tcLayout.colorFor(p, prepared.colors, look.ColorBy);
            const word = h(interactive() ? 'button' : 'span', {
                type: interactive() ? 'button' : null,
                class: wordClass(p, color) + ' jftc-flow-word',
                style: wordStyle(p, look, color, false),
                title: title(p.item),
                'data-key': p.item.key,
                'aria-pressed': interactive() ? String(model.selectedKey === p.item.key) : null,
                text: prepared.display[p.item.key]
            });
            wire(word, p.item);
            flow.append(h('span', { class: 'jftc-cell', role: 'listitem' }, word));
        });
        return flow;
    }

    function drawPacked(prepared, laid) {
        const look = model.look;
        const svg = tcSvg('svg', {
            class: 'jftc-svg',
            viewBox: [tcNum(laid.x), tcNum(laid.y), tcNum(Math.max(1, laid.width)), tcNum(Math.max(1, laid.height))].join(' '),
            role: 'list',
            'aria-label': options.label || 'Word cloud'
        });
        const at = Object.create(null);
        laid.words.forEach(function (w) {
            at[w.key] = w;
        });
        prepared.placements.forEach(function (p) {
            const w = at[p.item.key];
            if (!w) {
                return;
            }

            const color = tcLayout.colorFor(p, prepared.colors, look.ColorBy);
            const text = tcSvg('text', {
                class: wordClass(p, color),
                x: tcNum(w.x),
                y: tcNum(w.y),
                style: wordStyle(p, look, color, true),
                'data-key': p.item.key,
                role: interactive() ? 'button' : null,
                tabindex: interactive() ? '0' : null,
                'aria-label': title(p.item),
                'aria-pressed': interactive() ? String(model.selectedKey === p.item.key) : null
            }, [tcSvg('title'), document.createTextNode(prepared.display[p.item.key])]);
            text.firstChild.textContent = title(p.item);
            wire(text, p.item);
            const pad = p.fontSize * 2.4;
            svg.append(tcSvg('g', { role: 'listitem', 'data-key': p.item.key, 'data-box': [w.boxX - pad, w.boxY - pad, w.boxWidth + 2 * pad, w.boxHeight + 2 * pad, pad * 1.5].map(tcNum).join(',') }, text));
        });
        return svg;
    }

    // The selection's highlight and the spotlight's dimming, without a new layout.
    function applyState() {
        if (!drawn || !model) {
            return;
        }

        root.classList.toggle('jftc-spotlit', !!model.spotlitKey);
        Array.prototype.forEach.call(drawn.querySelectorAll('[data-key]'), function (node) {
            const key = node.getAttribute('data-key');
            if (node.tagName.toLowerCase() === 'g') {
                const old = node.querySelector('.jftc-highlight');
                if (old) {
                    old.remove();
                }

                if (key === model.selectedKey) {
                    const box = node.getAttribute('data-box').split(',');
                    node.insertBefore(tcSvg('rect', { class: 'jftc-highlight', x: box[0], y: box[1], width: box[2], height: box[3], rx: box[4] }), node.firstChild);
                }

                return;
            }

            node.classList.toggle('jftc-selected', key === model.selectedKey);
            node.classList.toggle('jftc-lit', key === model.spotlitKey);
            if (node.hasAttribute('aria-pressed')) {
                node.setAttribute('aria-pressed', String(key === model.selectedKey));
            }
        });
    }

    function show(node, signature) {
        if (drawn) {
            tcPeek.hide();
            drawn.remove();
        }

        drawn = node;
        drawnSignature = signature;
        root.querySelectorAll('.jftc-cloud-note').forEach(function (note) {
            note.remove();
        });
        root.append(node);
        applyState();
    }

    function note(text) {
        root.querySelectorAll('.jftc-cloud-note').forEach(function (old) {
            old.remove();
        });
        if (text) {
            root.append(h('div', { class: 'jftc-cloud-note', role: 'status', text: text }));
        }
    }

    async function draw() {
        const prepared = prepare();
        const look = model.look;
        root.style.setProperty('--jftc-cloud-ink', tcChoice(TC_BACKGROUNDS, look.Background).ink || 'currentColor');
        if (prepared.placements.length === 0) {
            if (drawn) {
                drawn.remove();
            }

            drawn = null;
            drawnSignature = null;
            pendingSignature = null;
            note(options.emptyText || 'Nothing to show.');
            return;
        }

        const signature = signatureOf(prepared.placements, prepared.display, look);
        if (signature === drawnSignature) {
            pendingSignature = null;
            applyState();
            return;
        }

        if (look.Arrangement === 'Flow' || failed) {
            pendingSignature = null;
            show(drawFlow(prepared), signature);
            return;
        }

        if (signature === pendingSignature) {
            return;
        }

        pendingSignature = signature;
        if (drawn) {
            drawn.classList.add('jftc-stale');
        } else {
            note(options.arrangingText === undefined ? 'Arranging…' : options.arrangingText);
        }

        // Every word as it will be drawn, hidden, for the packer to measure.
        if (measure) {
            measure.remove();
        }

        measure = tcSvg('svg', { class: 'jftc-measure', 'aria-hidden': 'true', focusable: 'false' });
        prepared.placements.forEach(function (p) {
            const color = tcLayout.colorFor(p, prepared.colors, look.ColorBy);
            measure.append(tcSvg('g', null, tcSvg('text', { 'data-key': p.item.key, class: wordClass(p, color), style: wordStyle(p, look, null, true) }, document.createTextNode(prepared.display[p.item.key]))));
        });
        root.append(measure);

        let laid;
        try {
            laid = await tcPack.layout(measure, {
                padX: Math.round(look.WordSpacing * 4),
                padY: Math.round(look.LineSpacing * 4),
                aspectRatio: tcChoice(TC_SHAPES, look.Shape).ratio,
                seed: look.Seed,
                mix: look.SizeMixing
            });
        } catch (error) {
            // Without a canvas (or a failure in it) the words still show, flowing.
            kit.report(error);
            failed = true;
            laid = undefined;
        }

        if (signature !== pendingSignature) {
            return;
        }

        if (measure) {
            measure.remove();
            measure = null;
        }

        pendingSignature = null;
        if (laid === null) {
            return;
        }

        show(laid === undefined ? drawFlow(prepared) : drawPacked(prepared, laid), signature);
    }

    return {
        element: root,
        update: function (next) {
            const relayout = !model || next.items !== model.items || next.look !== model.look;
            model = next;
            if (relayout) {
                draw().catch(kit.report);
            } else {
                applyState();
            }
        },
        destroy: function () {
            clearHold();
            tcPeek.hide();
            pendingSignature = null;
            root.remove();
        }
    };
}
