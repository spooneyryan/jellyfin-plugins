// The kit's client health probe, installed with webcheck's initScript before any page script: records every
// console.warn line and every request to a plugin's /Diagnostics/ClientHealth with the status the server answered, in
// window.__healthProbe = { warnings: [text], posts: [{ url, method, body, status, t }] }. The kit calls the global fetch
// when it reports, so the wrapper sees its posts.
(function () {
    'use strict';
    if (window.__healthProbe) {
        return;
    }

    var probe = { warnings: [], posts: [] };
    window.__healthProbe = probe;

    var warn = console.warn;
    console.warn = function () {
        try {
            probe.warnings.push(Array.prototype.map.call(arguments, String).join(' '));
        } catch (e) {
            // never break the page
        }

        return warn.apply(this, arguments);
    };

    var originalFetch = window.fetch;
    window.fetch = function (input, init) {
        var result = originalFetch.apply(this, arguments);
        try {
            var url = String(input && input.url ? input.url : input);
            if (url.indexOf('/Diagnostics/ClientHealth') >= 0) {
                var entry = {
                    url: url.replace(/[?#].*$/, ''),
                    method: (init && init.method) || 'GET',
                    body: init && typeof init.body === 'string' ? init.body : null,
                    status: null,
                    t: Math.round(performance.now())
                };
                probe.posts.push(entry);
                result.then(function (response) {
                    entry.status = response.status;
                }, function () {
                    entry.status = 0;
                });
            }
        } catch (e) {
            // never break the page
        }

        return result;
    };
})();
