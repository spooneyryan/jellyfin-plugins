// Shared state, the kit helpers under their Bookmarks 1.0 names, and the Bookmarks API calls.

const TICKS_PER_SECOND = kit.format.ticksPerSecond;
const CLIP_POLL_MS = 2000;
const CAPABILITIES_TTL_MS = 5 * 60 * 1000;
// A failed capabilities read (offline, a 401 at session expiry) keeps its fallback this long only, then asks again;
// never the full five minutes.
const CAPABILITIES_RETRY_MS = 10 * 1000;
const NON_BOOKMARKABLE_TYPES = ['TvChannel', 'Program', 'LiveTvChannel', 'LiveTvProgram'];

const h = kit.h;
const icon = kit.icon;
const iconButton = kit.iconButton;
const toast = kit.toast;
const formatTime = kit.format.time;
const parseTime = kit.format.parseTime;
const formatSize = kit.format.size;
const report = kit.report;

const state = {
    playback: null,
    rangeStart: null,
    capabilities: null,
    capabilitiesKey: null,
    capabilitiesAt: 0,
    capabilitiesFailed: false,
    capabilitiesPending: null,
    detailPages: new WeakMap(),
    lists: new Set(),
    // Listeners of notifyChanged (35-lists.js) that aren't one video's list: the My Bookmarks page (60-page-bookmarks.js).
    changeListeners: new Set(),
    clipWatchers: new Map(),
    // The videos ApiClient.getItem read, as promises (36-name-dialog.js readItem).
    items: new Map()
};

// kit.api with Bookmarks 1.0's argument order. Errors are KitApiError; their message is ProblemDetails' detail.
function api(method, path, body, query) {
    return kit.api(method, path, { body: body, query: query });
}

// The longest note line a title shows, with the ellipsis.
const NOTE_TITLE_LENGTH = 80;

// The first line of a note that isn't blank, trimmed, cut to 80 characters with "…"; '' without one. It
// counts code points, so an emoji is never cut in half.
function noteFirstLine(note) {
    const lines = String(note || '').split(/\r\n|\r|\n/);
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i].trim();
        if (line) {
            const chars = Array.from(line);
            return chars.length > NOTE_TITLE_LENGTH ? chars.slice(0, NOTE_TITLE_LENGTH - 1).join('').trimEnd() + '…' : line;
        }
    }

    return '';
}

// Whether a bookmark has a title of its own: a name, or a note with a line that isn't blank.
function hasOwnTitle(bookmark) {
    return !!(bookmark.Name || noteFirstLine(bookmark.Note));
}

// What rows, dialogs and the My Bookmarks page call a bookmark: its name, else its note's first line, else
// "Bookmark at 1:02:03" or "Range 1:02:03–1:05:00". Since schema 2 the server moves a short one-line 1.0 note into the
// name, so the name comes first.
function displayTitle(bookmark) {
    if (hasOwnTitle(bookmark)) {
        return bookmark.Name || noteFirstLine(bookmark.Note);
    }

    return typeof bookmark.EndTicks === 'number'
        ? 'Range ' + formatTime(bookmark.StartTicks) + '–' + formatTime(bookmark.EndTicks)
        : 'Bookmark at ' + formatTime(bookmark.StartTicks);
}

// Why a bookmark can't be played from here, or null (the row badges): "Not in library" is the stored detached state;
// "Unavailable" is ItemAvailable false, which only the page's Query and Groups compute, per viewer.
function unavailableReason(bookmark) {
    if (bookmark.Detached) {
        return { badge: 'Not in library', className: 'jfbm-badge-missing', title: 'This video isn\'t in the library any more. The bookmark keeps its name and times.' };
    }

    if (bookmark.ItemAvailable === false) {
        return { badge: 'Unavailable', className: 'jfbm-badge-unavailable', title: 'You can\'t open this video any more.' };
    }

    return null;
}

// Which of the user pages the server offers (Capabilities.Pages): a link to a page only while it is there.
function offeredPages(capabilities) {
    const pages = (capabilities && capabilities.Pages) || {};
    return { bookmarks: pages.Bookmarks === true, clips: pages.Clips === true };
}

function linkButton(text, onClick, attrs) {
    return h('button', Object.assign({ type: 'button', class: kit.cls('link'), text: text, onclick: onClick }, attrs || {}));
}

// A clip file's URL with the token, as Jellyfin's own media URLs carry it: the owner's route, or with `admin` the
// admins' route for another user's clip (GET /Bookmarks/Admin/Clips/{id}/File).
function clipFileUrl(bookmarkId, download, admin) {
    const client = kit.client();
    const path = admin ? 'Bookmarks/Admin/Clips/' + bookmarkId + '/File' : 'Bookmarks/' + bookmarkId + '/Clip/File';
    return client.getUrl(path, { ApiKey: client.accessToken(), download: download ? 'true' : 'false' });
}

function downloadClip(bookmark, admin) {
    const link = h('a', { href: clipFileUrl(bookmark.Id, true, admin === true), download: (bookmark.Clip && bookmark.Clip.FileName) || '' });
    document.body.append(link);
    link.click();
    link.remove();
}

// The capabilities as last read for this user, while they are fresh (CAPABILITIES_TTL_MS; the fallback of a failed read
// CAPABILITIES_RETRY_MS); null when a request is needed, which getCapabilities makes. For code that can't wait, such as
// the player's scanner.
function freshCapabilities() {
    const client = kit.client();
    const key = client ? client.serverId() + '/' + client.getCurrentUserId() : '';
    const ttl = state.capabilitiesFailed ? CAPABILITIES_RETRY_MS : CAPABILITIES_TTL_MS;
    return state.capabilities && state.capabilitiesKey === key && Date.now() - state.capabilitiesAt < ttl
        ? state.capabilities
        : null;
}

// The capabilities, read once per user and kept for CAPABILITIES_TTL_MS; callers that ask while a read is under way
// share it (the player's scanners ask together). A failed read is the fallback (no clips, no pages, nothing of the
// player's preferences) for CAPABILITIES_RETRY_MS, then a new request.
function getCapabilities() {
    const fresh = freshCapabilities();
    if (fresh) {
        return Promise.resolve(fresh);
    }

    const client = kit.client();
    const key = client ? client.serverId() + '/' + client.getCurrentUserId() : '';
    if (state.capabilitiesPending && state.capabilitiesPending.key === key) {
        return state.capabilitiesPending.promise;
    }

    const pending = {
        key: key,
        promise: (async function () {
            try {
                state.capabilities = await api('GET', 'Bookmarks/Capabilities');
                state.capabilitiesFailed = false;
            } catch (e) {
                report(e);
                state.capabilities = { ClipsEnabled: false, CanCreateClips: false, MaxClipDurationSeconds: 0, DefaultClipMode: 'Accurate', Pages: { Bookmarks: false, Clips: false } };
                state.capabilitiesFailed = true;
            }

            state.capabilitiesKey = key;
            state.capabilitiesAt = Date.now();
            if (state.capabilitiesPending === pending) {
                state.capabilitiesPending = null;
            }

            return state.capabilities;
        })()
    };
    state.capabilitiesPending = pending;
    return pending.promise;
}

// The capabilities forgotten, so the next getCapabilities is a fresh read: after a session expired, and when an admin route
// refused the My Clips page's "All users" scope (the setting turned off, or the admin demoted, meanwhile).
function forgetCapabilities() {
    state.capabilities = null;
    state.capabilitiesAt = 0;
    state.capabilitiesFailed = false;
}

// A session that expired (the kit's event, raised once on a 401): the capabilities are forgotten, so the next read, after
// signing in again, is a fresh one rather than the fallback or a stale copy.
document.addEventListener(kit.prefix + ':session-expired', forgetCapabilities);
