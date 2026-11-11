// Library Maintenance's Dashboard page: the AdminScript module's shared words and helpers. The module is
// the kit (above) and these files, in name order; 90-mount.js declares mount(view, params). Every value from the server is
// written as text, through kit.h's text and textContent, and no file touches the page until mount is called.

// The tabs: Moves joins them in 1.1.
const LM_TABS = [
    { name: 'Duplicates', label: 'Duplicates' },
    { name: 'Settings', label: 'Settings' },
    { name: 'Diagnostics', label: 'Diagnostics' }
];

// The page's own classes: jflm-page-<name>.
function lmClass(name) {
    return KIT_BUILD.prefix + '-page-' + name;
}

function lmEl(tag, props, children) {
    return kit.h(tag, props, children);
}

function lmClear(node) {
    while (node && node.firstChild) {
        node.removeChild(node.firstChild);
    }
}

// Settles a promise into { ok, value | error } (Promise.allSettled is ES2020).
function lmSettle(promise) {
    return promise.then(function (value) {
        return { ok: true, value: value };
    }, function (error) {
        return { ok: false, error: error };
    });
}

function lmErrorText(error) {
    return (error && error.message) || 'Something went wrong. Reload the page to try again.';
}

// The tab a URL names (#/configurationpage?name=LibraryMaintenance&tab=settings), read once when the page mounts; the
// page never writes the URL.
function lmTabFromParams(params) {
    const wanted = String((params && params.tab) || '').toLowerCase();
    const match = LM_TABS.filter(function (tab) {
        return tab.name.toLowerCase() === wanted;
    })[0];
    return match ? match.name : LM_TABS[0].name;
}
