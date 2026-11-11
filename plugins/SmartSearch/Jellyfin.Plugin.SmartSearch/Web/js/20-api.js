// The Smart Search endpoints the page uses, over kit.api (the user's token, JSON, ProblemDetails as KitApiError
// with its code and errors). Every one answers as the signed-in user, over the libraries they can open.

// A text of SS_TEXT with its {name} placeholders filled.
function ssText(key, vars) {
    const template = Object.prototype.hasOwnProperty.call(SS_TEXT, key) ? SS_TEXT[key] : key;
    return String(template).replace(/\{(\w+)\}/g, function (match, name) {
        return vars && Object.prototype.hasOwnProperty.call(vars, name) ? String(vars[name]) : match;
    });
}

const ssApi = {
    // GET /SmartSearch/Fields: { RegistryVersion, Fields, SortKeys, ItemKinds } (the kinds the server allows).
    fields: function () {
        return kit.api('GET', 'SmartSearch/Fields');
    },
    // GET /SmartSearch/Libraries: [{ Id, Name, CollectionType }], the libraries the user can open.
    libraries: function () {
        return kit.api('GET', 'SmartSearch/Libraries');
    },
    // GET /SmartSearch/Values/{field}: [{ Value, Count }], values starting with the prefix first, then by count.
    values: function (field, prefix, libraryIds, limit, signal) {
        // libraryIds is repeated, which kit.api's query (one value a name) can't send, so the path carries the whole query.
        const query = new URLSearchParams();
        if (prefix) {
            query.append('prefix', prefix);
        }

        (libraryIds || []).forEach(function (id) {
            query.append('libraryIds', id);
        });
        query.append('limit', String(limit || 10));
        return kit.api('GET', 'SmartSearch/Values/' + encodeURIComponent(field) + '?' + query.toString(), { signal: signal });
    },
    // GET /SmartSearch/Series: [{ Id, Name, ProductionYear, LibraryId }], the user's shows whose name holds the text
    // (2 characters or more), those starting with it first.
    series: function (prefix, limit, signal) {
        return kit.api('GET', 'SmartSearch/Series', { query: { prefix: prefix, limit: String(limit || 10) }, signal: signal });
    },
    // POST /SmartSearch/Validate: { Valid, Supported, Errors [{ Path, Code, Message }], UsesPerUserFields,
    // UsesAdminOnlyFields, UnavailableFields }, the query checked as the user would send it, nothing run.
    validate: function (query) {
        return kit.api('POST', 'SmartSearch/Validate', { body: { Query: query } });
    },
    // POST /SmartSearch/Search: a page of results. The first page asks for the total; later pages send its token.
    search: function (query, startIndex, limit, resultToken, signal) {
        const body = { Query: query, StartIndex: startIndex, Limit: limit, IncludeTotal: startIndex === 0 };
        if (resultToken) {
            body.ResultToken = resultToken;
        }

        return kit.api('POST', 'SmartSearch/Search', { body: body, signal: signal });
    },
    // GET /SmartSearch/Queries: { Items, TotalRecordCount, StartIndex }, the user's own queries and those others share
    // (scope Mine, Shared or All), ordered by name; every page of 500 read, so the tab can find in all of them.
    listQueries: function (scope) {
        const items = [];
        const next = function () {
            return kit.api('GET', 'SmartSearch/Queries', { query: { scope: scope || 'All', startIndex: items.length, limit: 500 } }).then(function (page) {
                const got = (page && page.Items) || [];
                Array.prototype.push.apply(items, got);
                return got.length > 0 && items.length < ((page && page.TotalRecordCount) || 0) ? next() : items;
            });
        };
        return next();
    },
    // GET /SmartSearch/Queries/{id}: a query with its definition (Query), Supported, Valid, Errors, CanEdit, UpdatedUtc.
    getQuery: function (id) {
        return kit.api('GET', 'SmartSearch/Queries/' + encodeURIComponent(id));
    },
    // POST /SmartSearch/Queries: { Name, Description, Shared, Query } → the new query.
    createQuery: function (body) {
        return kit.api('POST', 'SmartSearch/Queries', { body: body });
    },
    // PUT /SmartSearch/Queries/{id}: the same body plus IfUpdatedAt (the UpdatedUtc read) → the changed query.
    updateQuery: function (id, body) {
        return kit.api('PUT', 'SmartSearch/Queries/' + encodeURIComponent(id), { body: body });
    },
    // POST /SmartSearch/Queries/{id}/Duplicate: a private copy for the user, named as asked or numbered by the server.
    duplicateQuery: function (id, name) {
        return kit.api('POST', 'SmartSearch/Queries/' + encodeURIComponent(id) + '/Duplicate', { body: name ? { Name: name } : {} });
    },
    // DELETE /SmartSearch/Queries/{id}; detach: the auto-collections that use it keep a copy.
    deleteQuery: function (id, detach) {
        return kit.api('DELETE', 'SmartSearch/Queries/' + encodeURIComponent(id), detach ? { query: { detachAutoCollections: 'true' } } : undefined);
    },
    // POST /SmartSearch/Queries/{id}/Search: a page of a saved query's results, run as the user, as search() pages.
    runQuery: function (id, startIndex, limit, resultToken, signal) {
        const body = { StartIndex: startIndex, Limit: limit, IncludeTotal: startIndex === 0 };
        if (resultToken) {
            body.ResultToken = resultToken;
        }

        return kit.api('POST', 'SmartSearch/Queries/' + encodeURIComponent(id) + '/Search', { body: body, signal: signal });
    },

    // The auto-collections (65-collections.js): managers only; the server checks every call.
    // GET /SmartSearch/AutoCollections: every auto-collection with its state, by name (cheap: polled while some update).
    acList: function () {
        return kit.api('GET', 'SmartSearch/AutoCollections');
    },
    // GET /SmartSearch/AutoCollections/{id}: one with its pinned and excluded items.
    acGet: function (id) {
        return kit.api('GET', 'SmartSearch/AutoCollections/' + encodeURIComponent(id));
    },
    // GET /SmartSearch/AutoCollections/{id}/History: its last runs, newest first.
    acHistory: function (id) {
        return kit.api('GET', 'SmartSearch/AutoCollections/' + encodeURIComponent(id) + '/History');
    },
    // POST /SmartSearch/AutoCollections/CheckName: { Name, Available, Conflicts, Warnings, Suggestions }.
    acCheckName: function (name, definitionId, signal) {
        return kit.api('POST', 'SmartSearch/AutoCollections/CheckName', { body: definitionId ? { Name: name, DefinitionId: definitionId } : { Name: name }, signal: signal });
    },
    // POST /SmartSearch/AutoCollections/Preview: { Definition } or { DefinitionId, Definition } (changes).
    acPreview: function (body, signal) {
        return kit.api('POST', 'SmartSearch/AutoCollections/Preview', { body: body, signal: signal });
    },
    // POST /SmartSearch/AutoCollections: { Definition, AcceptedPreviewHash, DuplicateSourceQuery } (waits up to a minute).
    acCreate: function (body) {
        return kit.api('POST', 'SmartSearch/AutoCollections', { body: body, timeout: 90000 });
    },
    // PUT /SmartSearch/AutoCollections/{id}: { Definition, IfUpdatedAt, AcceptedPreviewHash, DuplicateSourceQuery }.
    acUpdate: function (id, body) {
        return kit.api('PUT', 'SmartSearch/AutoCollections/' + encodeURIComponent(id), { body: body });
    },
    // GET /SmartSearch/AutoCollections/Ids: the collections auto-collections fill that the user can open (any user).
    acIds: function () {
        return kit.api('GET', 'SmartSearch/AutoCollections/Ids');
    },
    // GET /SmartSearch/AutoCollections/ByCollection/{id}: { Name, LastSuccessUtc, CanManage, DefinitionId } (any user).
    acByCollection: function (boxSetId) {
        return kit.api('GET', 'SmartSearch/AutoCollections/ByCollection/' + encodeURIComponent(boxSetId));
    },
    acRefresh: function (id) {
        return kit.api('POST', 'SmartSearch/AutoCollections/' + encodeURIComponent(id) + '/Refresh');
    },
    acRefreshAll: function () {
        return kit.api('POST', 'SmartSearch/AutoCollections/Refresh');
    },
    // POST /{id}/Recreate: { Name } or {} (the name kept); waits up to a minute.
    acRecreate: function (id, name) {
        return kit.api('POST', 'SmartSearch/AutoCollections/' + encodeURIComponent(id) + '/Recreate', { body: name ? { Name: name } : {}, timeout: 90000 });
    },
    // POST /SmartSearch/AutoCollections/{id}/Reassign (administrators): a deleted creator's auto-collection given to a user.
    acReassign: function (id, userId) {
        return kit.api('POST', 'SmartSearch/AutoCollections/' + encodeURIComponent(id) + '/Reassign', { body: { UserId: userId } });
    },
    acGuardApply: function (id, detectedUtc) {
        return kit.api('POST', 'SmartSearch/AutoCollections/' + encodeURIComponent(id) + '/Guard/Apply', { body: { DetectedUtc: detectedUtc } });
    },
    acGuardKeep: function (id, detectedUtc) {
        return kit.api('POST', 'SmartSearch/AutoCollections/' + encodeURIComponent(id) + '/Guard/Keep', { body: { DetectedUtc: detectedUtc } });
    },
    // DELETE /{id}?deleteCollection: the collection kept as an ordinary one, or deleted too.
    acDelete: function (id, deleteCollection) {
        return kit.api('DELETE', 'SmartSearch/AutoCollections/' + encodeURIComponent(id), deleteCollection ? { query: { deleteCollection: 'true' } } : undefined);
    }
};
