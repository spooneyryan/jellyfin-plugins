// Positive control of the pages frame probe ("with a positive control (pre-hide off, so frames > 0)").
// Loaded with webcheck's initScript step after probes/pages-probe.js: as soon as <head> exists it adds rules
// that outrank the plugin's pre-hide rules by extra type selectors: the route rule of the head fragment and the kit
// (html.<prefix>-route #fallbackPage > :not([data-<prefix>-page]) { display:none !important }) and the kit's claim rule
// (#fallbackPage[data-jfkit-claim="<prefix>"] > :not([data-<prefix>-page]), which keeps "Page not found" hidden after
// leaving for another unknown route until the hand-back). FallbackRoute's "Page not found" then shows next to our page,
// on a cold load and while leaving, and the probe must count flash frames. Everything else stays as it is.
(function () {
    'use strict';
    var config = window.__pagesProbeConfig || {};
    var prefix = config.prefix || 'jfbm';
    var others = ' > :not([data-' + prefix + '-page])';
    var css = 'html.' + prefix + '-route body #fallbackPage' + others + ', html body #fallbackPage[data-jfkit-claim="' + prefix + '"]' + others
        + ' { display: block !important; }';

    function add() {
        if (!document.head || document.getElementById('pages-prehide-off')) {
            return !!document.head;
        }

        var style = document.createElement('style');
        style.id = 'pages-prehide-off';
        style.textContent = css;
        document.head.appendChild(style);
        return true;
    }

    if (!add()) {
        var observer = new MutationObserver(function () {
            if (add()) {
                observer.disconnect();
            }
        });
        observer.observe(document, { childList: true, subtree: true });
    }

    window.__pagesPrehideOff = css;
})();
