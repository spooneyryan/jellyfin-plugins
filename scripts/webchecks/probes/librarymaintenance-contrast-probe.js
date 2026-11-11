// Duplicates tab themes: the WCAG contrast of Library Maintenance's chips, badge and fallback poster against
// what is behind them, and the kit's danger red. Installed with webcheck's initScript step; it only reads the page, but for
// the one error notice it adds for the danger red and removes at once.
//
//   window.__lmContrast() -> { theme, background, items: [{ kind, text, color, behind, ratio, border, borderRatio }] }
//
// The text's colour is its own, times its alpha and the opacity of it and its ancestors; what is behind it is every
// background colour painted under the element's centre down to the first opaque one, composited (a gradient or an image
// is not read, so each theme's screenshot is the check of those). One entry per kind: the first element of each.
(function () {
    'use strict';
    if (window.__lmContrast) {
        return;
    }

    function parse(value) {
        var m = /rgba?\(([^)]+)\)/.exec(String(value || ''));
        if (!m) {
            return null;
        }

        var p = m[1].split(/[\s,/]+/).filter(Boolean).map(Number);
        return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
    }

    function over(top, bottom) {
        var a = top.a + bottom.a * (1 - top.a);
        if (a === 0) {
            return { r: 0, g: 0, b: 0, a: 0 };
        }

        var mix = function (k) {
            return (top[k] * top.a + bottom[k] * bottom.a * (1 - top.a)) / a;
        };
        return { r: mix('r'), g: mix('g'), b: mix('b'), a: a };
    }

    // What is painted under the element at its centre: the elements there (document.elementsFromPoint, topmost first)
    // from the element down, composited until the first opaque one. The theme's own background is a fixed sibling
    // (.backgroundContainer), not an ancestor, so the ancestors alone would miss it.
    function behind(element) {
        element.scrollIntoView({ block: 'center' });
        var r = element.getBoundingClientRect();
        var stack = document.elementsFromPoint(r.left + r.width / 2, r.top + r.height / 2);
        var at = stack.indexOf(element);
        var layers = [];
        for (var i = at < 0 ? 0 : at; i < stack.length; i++) {
            var c = parse(getComputedStyle(stack[i]).backgroundColor);
            if (c && c.a > 0) {
                layers.push(c);
                if (c.a >= 1) {
                    break;
                }
            }
        }

        // Nothing opaque: the browser's canvas, white.
        var colour = { r: 255, g: 255, b: 255, a: 1 };
        for (var j = layers.length - 1; j >= 0; j--) {
            colour = over(layers[j], colour);
        }

        return colour;
    }

    function opacity(element) {
        var value = 1;
        for (var e = element; e; e = e.parentElement) {
            value *= Number(getComputedStyle(e).opacity);
        }

        return value;
    }

    function luminance(c) {
        var f = function (v) {
            v /= 255;
            return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
        };
        return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
    }

    function ratio(a, b) {
        var x = luminance(a);
        var y = luminance(b);
        return Math.round(((Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05)) * 100) / 100;
    }

    function css(c) {
        return 'rgb(' + Math.round(c.r) + ', ' + Math.round(c.g) + ', ' + Math.round(c.b) + ')';
    }

    var KINDS = [
        ['confirmed', '.jflm-page-chip-confidence-confirmed'],
        ['likely', '.jflm-page-chip-confidence-likely'],
        ['possible', '.jflm-page-chip-confidence-possible'],
        ['conflict', '.jflm-page-chip-confidence-conflict'],
        ['warning', '.jflm-page-chip-warning'],
        ['reason', '.jflm-page-chip-reason'],
        ['flag', '.jflm-page-chip-flag'],
        ['edition', '.jflm-page-chip-edition'],
        ['keep', '.jflm-page-chip-keep'],
        ['initials', '.jflm-page-poster-none .jflm-page-poster-initials'],
        ['summary', '#LibraryMaintenanceGroupsSummary'],
        ['facts', '.jflm-page-entry-facts']
    ];

    function measure(kind, el) {
        // From the element down: a chip's own background (none today) is part of what is behind its text.
        var back = behind(el);

        var text = parse(getComputedStyle(el).color);
        var shown = over({ r: text.r, g: text.g, b: text.b, a: text.a * opacity(el) }, back);
        var border = parse(getComputedStyle(el).borderTopColor);
        var bw = parseFloat(getComputedStyle(el).borderTopWidth) || 0;
        return {
            kind: kind,
            text: (el.textContent || '').trim().slice(0, 30),
            color: css(shown),
            behind: css(back),
            ratio: ratio(shown, back),
            border: bw > 0 && border ? css(over(border, back)) : null,
            borderRatio: bw > 0 && border ? ratio(over(border, back), back) : null,
            fontSize: getComputedStyle(el).fontSize,
            fontWeight: getComputedStyle(el).fontWeight
        };
    }

    window.__lmContrast = function () {
        var page = document.querySelector('#LibraryMaintenanceConfigPage:not(.hide)') || document;
        var items = [];
        KINDS.forEach(function (kind) {
            var el = page.querySelector(kind[1]);
            items.push(el ? measure(kind[0], el) : { kind: kind[0], missing: true });
        });

        // The kit's danger red (an error notice's text, a failed Load more's), on the page's background: no error shows
        // on a working page, so one notice's text is added for the measure and removed at once.
        var host = page === document ? document.body : page.querySelector('.jflm-page-app') || page;
        var danger = document.createElement('span');
        danger.className = 'jflm-notice-error';
        danger.textContent = 'Couldn\'t load more groups';
        host.appendChild(danger);
        try {
            items.push(measure('danger', danger));
        } finally {
            danger.remove();
        }

        var poster = page.querySelector('.jflm-page-poster-none');
        return {
            theme: document.documentElement.getAttribute('data-theme'),
            background: css(behind(page === document ? document.body : page.querySelector('h3') || page)),
            posterBox: poster ? css(behind(poster)) : null,
            items: items
        };
    };
}());
