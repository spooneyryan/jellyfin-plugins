/*
 * The server side of the guard: GET /AccessRoles/Me (the signed-in user's own locks), its cache in the browser
 * (jfar:rules:<user id>, read by the head script on the next load), and the header a merged settings save answers with.
 */

// The head fragment's server-wide flags (Web/AccessRolesHead): anyManaged, enforced, firstLoad, notice, userPages. With no
// head (a fragment that failed), the safe side: enforced, anyone possibly managed.
const HEAD = (function () {
    const head = window.__jfarHead || {};
    return Object.freeze({
        present: !!window.__jfarHead,
        anyManaged: head.anyManaged !== false,
        enforced: head.enforced !== false,
        firstLoad: head.firstLoad === 'ShowUntilKnown' ? 'ShowUntilKnown' : 'HideUntilKnown',
        notice: head.notice === 'Silent' ? 'Silent' : 'Toast',
        userPages: head.userPages !== false,
        userId: head.userId || null,
        classes: Array.isArray(head.classes) ? head.classes.slice() : []
    });
}());

// How long the first-load rule waits for /Me before it shows everything (the server enforces anyway). The request
// itself goes on (the kit's 30 s): a later answer is applied and cached.
const ME_TIMEOUT_MS = 3000;

// How long after a /Me that failed a view may ask again (so a failure doesn't turn the guard off for the
// page's life).
const ME_RETRY_MS = 10000;

// The response header of a settings save whose locked values the server kept (AccessRulesActionFilter.KeptHeader).
const KEPT_HEADER = 'X-AccessRoles-Kept';

// A user id as the cache keys it: lower case, no dashes (jellyfin-apiclient and the server differ).
function normalizeUserId(id) {
    return id ? String(id).replace(/-/g, '').toLowerCase() : null;
}

function cacheKey(userId) {
    return 'rules:' + normalizeUserId(userId);
}

// The cached /Me answer of a user, or null.
function cachedRules(userId) {
    if (!userId) {
        return null;
    }

    const cached = kit.storage.get(cacheKey(userId), null);
    return cached && typeof cached === 'object' ? cached : null;
}

// What the cache keeps of an answer: what the head script needs, nothing else.
function cacheRules(me) {
    if (!me || !me.UserId) {
        return;
    }

    kit.storage.set(cacheKey(me.UserId), {
        Managed: !!me.Managed,
        Locked: Array.isArray(me.Locked) ? me.Locked.map(String) : [],
        AllSettingsLocked: !!me.AllSettingsLocked,
        NoLibraries: !!me.NoLibraries,
        Message: me.Message || null,
        RulesVersion: me.RulesVersion || null
    });
}

// GET /AccessRoles/Me, with the kit's own timeout (30 s): the first-load rule stops waiting after ME_TIMEOUT_MS, the
// request doesn't.
function fetchMe() {
    return kit.api('GET', 'AccessRoles/Me');
}

// A merged save tells the user: jellyfin-web's pages show their own "saved" for a 204, so the server names
// the locked sections it kept in KEPT_HEADER, and a toast says so. window.fetch is wrapped once and always called through
// (Jellyfin Enhanced wraps it too); a response without the header, or one that isn't a success, is left alone (the server
// sets the header only on a save that succeeded).
function watchKeptSaves(onKept) {
    if (typeof window.fetch !== 'function' || window.fetch.__jfarKept) {
        return;
    }

    const original = window.fetch;
    const wrapped = function () {
        const result = original.apply(this, arguments);
        if (result && typeof result.then === 'function') {
            result.then(function (response) {
                try {
                    const kept = response && response.ok && response.headers && typeof response.headers.get === 'function' ? response.headers.get(KEPT_HEADER) : null;
                    if (kept) {
                        onKept(String(kept).split(',').map(function (s) {
                            return s.trim();
                        }).filter(Boolean));
                    }
                } catch (e) {
                    kit.report(e);
                }
            }, function () {
                // The caller sees the failure.
            });
        }

        return result;
    };
    wrapped.__jfarKept = true;
    window.fetch = wrapped;
}
