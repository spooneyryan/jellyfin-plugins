// Jellyfin Enhanced (JE) action spies for the je checks of our name dialog's keys next to JE's shortcuts.
// Loaded by webcheck's initScript into every document before any page script. Taken from an earlier experiment's probe
// (not in this repository): once JE has registered its key listener, its key actions on window.JellyfinEnhanced
// (toast, jumpToPercentage, resetPlaybackSpeed, skipIntroOutro, ...) and its bookmark modal are wrapped (pass-through) and
// every call logged, and clicks on the jellyfin-web buttons JE's shortcuts click are logged, so "JE did nothing" is
// observed rather than inferred. It never prevents, stops or changes an event.
(function () {
    'use strict';

    if (window.__jeSpy) {
        return;
    }

    var ACTIONS = ['toast', 'showEnhancedPanel', 'cycleAspect', 'togglePlaybackInfo', 'cycleSubtitleTrack', 'cycleAudioTrack',
        'resetPlaybackSpeed', 'adjustPlaybackSpeed', 'skipIntroOutro', 'frameStep', 'jumpToLastPosition', 'jumpToPercentage'];
    var spy = { installed: false, names: [], calls: [], clicks: [], hashes: [], keys: [] };
    window.__jeSpy = spy;

    function now() {
        return Math.round(performance.now());
    }

    function wrap(owner, name, label) {
        var original = owner[name];
        if (typeof original !== 'function' || original.__jeSpy) {
            return false;
        }

        var wrapped = function () {
            var args = [];
            for (var i = 0; i < arguments.length; i++) {
                var a = arguments[i];
                args.push(typeof a === 'string' ? a.replace(/<[^>]*>/g, '').slice(0, 80) : (typeof a === 'number' || typeof a === 'boolean') ? a : typeof a);
            }

            spy.calls.push({ fn: label, args: args, ms: now(), hash: location.hash.slice(0, 40) });
            return original.apply(this, arguments);
        };
        wrapped.__jeSpy = true;
        owner[name] = wrapped;
        return true;
    }

    function install() {
        var je = window.JellyfinEnhanced;
        if (!je || !je.keyListener) {
            return false;
        }

        ACTIONS.forEach(function (name) {
            if (wrap(je, name, name)) {
                spy.names.push(name);
            }
        });
        if (je.bookmarks && wrap(je.bookmarks, 'showModal', 'bookmarks.showModal')) {
            spy.names.push('bookmarks.showModal');
        }

        spy.installed = true;
        return true;
    }

    var timer = setInterval(function () {
        try {
            if (install()) {
                clearInterval(timer);
            }
        } catch (e) {
            /* never break the page */
        }
    }, 100);

    document.addEventListener('click', function (e) {
        var t = e.target && e.target.closest ? e.target.closest('button.btnSubtitles, button.headerSearchButton, #randomItemButton, #popupPreviewButton') : null;
        if (t) {
            spy.clicks.push({ target: (t.id || t.className || t.tagName).toString().slice(0, 60), trusted: e.isTrusted, ms: now() });
        }
    }, true);

    window.addEventListener('hashchange', function () {
        spy.hashes.push({ hash: location.hash.slice(0, 60), ms: now() });
    });

    window.addEventListener('keydown', function (e) {
        var t = e.target;
        spy.keys.push({ key: e.key, target: t && t.tagName ? t.tagName : String(t), ms: now() });
    }, true);

    spy.reset = function () {
        spy.calls.length = 0;
        spy.clicks.length = 0;
        spy.hashes.length = 0;
        spy.keys.length = 0;
        return true;
    };

    spy.summary = function () {
        return { installed: spy.installed, calls: spy.calls.slice(), clicks: spy.clicks.slice(), hashes: spy.hashes.slice(), keys: spy.keys.map(function (k) { return k.key + '@' + k.target; }) };
    };
}());
