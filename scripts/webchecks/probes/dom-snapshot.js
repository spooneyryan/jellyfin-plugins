// A normalised, comparable snapshot of a DOM subtree, for "the UI is unchanged" checks: webchecks save
// __domSnap(element) before and after a change and compare the two results (scripts/compare-snapshots.mjs).
// Installed with webcheck's initScript step; it only defines a function and reads the page.
//
// Normalisation: attributes sorted by name; GUIDs (N or D format) become <guid>; generated element ids of ours (the
// values of id, for and aria-* id references that start with a jf prefix, such as Bookmarks 1.0's random field ids)
// become <id>; any api_key/ApiKey query value becomes <token>, so no session token ever reaches a result file.
// Whitespace-only text nodes are dropped, other text is trimmed.
(function () {
    'use strict';
    if (window.__domSnap) {
        return;
    }

    var GUID = /[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}/gi;
    var OWN_ID = /^jf[a-z0-9]{2,8}-/;
    var ID_REFERENCES = ['id', 'for', 'aria-labelledby', 'aria-describedby', 'aria-controls'];
    var TOKEN = /((?:api_key|apikey)=)[^&"'\s]+/gi;

    function clean(value) {
        return String(value).replace(TOKEN, '$1<token>').replace(GUID, '<guid>');
    }

    function cleanAttribute(name, value) {
        return ID_REFERENCES.indexOf(name) >= 0 && OWN_ID.test(value) ? '<id>' : clean(value);
    }

    function snap(node) {
        if (!node) {
            return null;
        }

        if (node.nodeType === 3) {
            var text = node.textContent.replace(/\s+/g, ' ').trim();
            return text ? clean(text) : null;
        }

        if (node.nodeType !== 1) {
            return null;
        }

        var attrs = {};
        Array.prototype.slice.call(node.attributes).map(function (a) { return a.name; }).sort().forEach(function (name) {
            attrs[name] = cleanAttribute(name, node.getAttribute(name));
        });
        var children = [];
        node.childNodes.forEach(function (child) {
            var s = snap(child);
            if (s !== null) {
                children.push(s);
            }
        });
        var out = { tag: node.tagName.toLowerCase() };
        if (Object.keys(attrs).length > 0) {
            out.attrs = attrs;
        }

        if (node.tagName === 'INPUT' || node.tagName === 'SELECT' || node.tagName === 'TEXTAREA') {
            out.value = clean(node.value);
        }

        if (children.length > 0) {
            out.children = children;
        }

        return out;
    }

    window.__domSnap = function (target) {
        var node = typeof target === 'string' ? document.querySelector(target) : target;
        return snap(node);
    };
})();
