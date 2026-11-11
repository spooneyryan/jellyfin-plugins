// Uncaught exceptions jellyfin-web itself throws in a webcheck's headless browser, whatever the page under test does
// (scripts/README.md, "webcheck.mjs"). webcheck.mjs marks each one `known` in its result, and neither a
// noExceptions step nor --fail-on-exception counts it; the exception stays in the result as evidence. Node 22 built-ins
// only.
//
// An entry is only for an exception of jellyfin-web (never of ours, nor of another plugin) that the sandbox's browser
// environment causes and that can strike any page. Exceptions that belong to one page (jellyfin-web's "player cannot be
// null" on the player, its Home tab's "t.value.pause is not a function", the stale /CustomRatings/script tag) stay in the
// `ignore` pattern of the webchecks that meet them.

export const KNOWN_PAGE_NOISE = [
    {
        name: "jellyfin-web's new-item notification without notification permission",
        // jellyfin-web 10.11's notifications plugin answers a LibraryChanged event with items added by showing "New Movie"
        // through the service worker's registration.showNotification, without checking the permission first. A headless
        // browser never grants it, so the promise rejects uncaught whenever a library scan adds an item while a page is
        // open (pages-fallback.json in the bare sandbox once, in an earlier run).
        pattern: /Failed to execute 'showNotification' on 'ServiceWorkerRegistration': No notification permission has been granted for this origin/
    }
];

// The name of the known noise an exception is ({ text, url } as webcheck.mjs records it), or null.
export function knownPageNoise(exception) {
    const text = exception && typeof exception.text === 'string' ? exception.text : '';
    const entry = KNOWN_PAGE_NOISE.find((n) => n.pattern.test(text));
    return entry ? entry.name : null;
}
