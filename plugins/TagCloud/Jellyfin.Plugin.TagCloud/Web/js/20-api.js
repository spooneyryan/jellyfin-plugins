/*
 * Tag Cloud web client for jellyfin-web 10.11, on the shared kit. The kit's EmbeddedScript
 * concatenates kit.js and the files of this folder in name order into one IIFE, served by GET /TagCloud/ClientScript and
 * added to index.html by the kit's middleware. It provides one page, the Tag Cloud (#/jftc-cloud), with its full-screen
 * view (the same page with show=1 in the hash). It uses no jellyfin-web element but the kit's page host and nav
 * (Web/anchors.json is empty): the page and the overlay are its own markup.
 *
 * This file: shared state and the Tag Cloud API.
 */

const TC_PAGE = Object.freeze({ key: 'cloud', title: 'Tag Cloud', icon: 'cloud', idSuffix: 'CloudPage', order: 30 });

// What the toolbar shows for each source and scope, in its order.
const TC_SOURCES = Object.freeze({
    Tags: Object.freeze({ label: 'Tags', one: 'tag', many: 'tags' }),
    Genres: Object.freeze({ label: 'Genres', one: 'genre', many: 'genres' }),
    Actors: Object.freeze({ label: 'Actors', one: 'actor', many: 'actors' }),
    Directors: Object.freeze({ label: 'Directors', one: 'director', many: 'directors' }),
    Writers: Object.freeze({ label: 'Writers', one: 'writer', many: 'writers' }),
    Studios: Object.freeze({ label: 'Studios', one: 'studio', many: 'studios' })
});

const TC_SCOPES = Object.freeze({
    Movies: Object.freeze({ label: 'Movies', one: 'movie', many: 'movies' }),
    Shows: Object.freeze({ label: 'TV shows', one: 'show', many: 'shows' }),
    Both: Object.freeze({ label: 'Both', one: 'title', many: 'titles' })
});

const h = kit.h;

const tcState = {
    capabilities: null,
    capabilitiesPending: null,
    libraries: Object.create(null),
    clouds: null
};

// kit.api under /TagCloud. A query is built here, not by ApiClient.getUrl: narrow= repeats once per word, and a word may
// hold a comma, which getUrl would join and split on.
function tcApi(method, path, body, query) {
    return kit.api(method, 'TagCloud/' + path + tcQuery(query), { body: body });
}

function tcQuery(query) {
    const parts = [];
    Object.keys(query || {}).forEach(function (name) {
        const value = query[name];
        if (value === undefined || value === null || value === '') {
            return;
        }

        [].concat(value).forEach(function (one) {
            if (one !== undefined && one !== null && one !== '') {
                parts.push(encodeURIComponent(name) + '=' + encodeURIComponent(String(one)));
            }
        });
    });
    return parts.length ? '?' + parts.join('&') : '';
}

// What this server's Tag Cloud offers, read once per page load and user; a failure asks again next time.
function tcCapabilities() {
    const user = kit.user.id();
    if (tcState.capabilities && tcState.capabilities.user === user) {
        return Promise.resolve(tcState.capabilities.value);
    }

    if (!tcState.capabilitiesPending) {
        tcState.capabilitiesPending = tcApi('GET', 'Capabilities').then(function (value) {
            tcState.capabilities = { user: user, value: value };
            return value;
        }).finally(function () {
            tcState.capabilitiesPending = null;
        });
    }

    return tcState.capabilitiesPending;
}

// The libraries holding titles in a scope, kept per user and scope for the page's life (their counts change only with
// the library, and the picker shows them as a guide).
function tcLibraries(scope) {
    const key = kit.user.id() + '|' + scope;
    if (!tcState.libraries[key]) {
        tcState.libraries[key] = tcApi('GET', 'Libraries', undefined, { scope: scope }).catch(function (error) {
            delete tcState.libraries[key];
            throw error;
        });
    }

    return tcState.libraries[key];
}

function tcWords(cloud, limit) {
    return tcApi('GET', 'Words', undefined, {
        source: cloud.source,
        scope: cloud.scope,
        libraryIds: cloud.libraries.length ? cloud.libraries.join(',') : null,
        narrow: cloud.narrow,
        limit: limit
    });
}

function tcTitles(cloud, key, opts) {
    opts = opts || {};
    return tcApi('GET', 'Titles', undefined, {
        source: cloud.source,
        scope: cloud.scope,
        libraryIds: cloud.libraries.length ? cloud.libraries.join(',') : null,
        narrow: cloud.narrow,
        key: key,
        order: opts.order || 'Name',
        seed: opts.seed,
        startIndex: opts.startIndex || 0,
        limit: opts.limit || 50
    });
}

function tcListClouds() {
    return tcApi('GET', 'Clouds').then(function (clouds) {
        tcState.clouds = clouds || [];
        return tcState.clouds;
    });
}

function tcSaveCloud(id, request) {
    return id ? tcApi('PUT', 'Clouds/' + encodeURIComponent(id), request) : tcApi('POST', 'Clouds', request);
}

function tcDuplicateCloud(id) {
    return tcApi('POST', 'Clouds/' + encodeURIComponent(id) + '/Duplicate');
}

function tcDeleteCloud(id) {
    return tcApi('DELETE', 'Clouds/' + encodeURIComponent(id));
}

function tcRebuild() {
    return tcApi('POST', 'Admin/Rebuild');
}

// "1 movie", "2,345 movies".
function tcCount(n, unit) {
    return Number(n || 0).toLocaleString() + ' ' + (n === 1 ? unit.one : unit.many);
}

// "Movies", "Movies and Kids", "Movies, Kids and Docs".
function tcNameList(names) {
    if (names.length <= 1) {
        return names[0] || '';
    }

    return names.slice(0, -1).join(', ') + ' and ' + names[names.length - 1];
}
