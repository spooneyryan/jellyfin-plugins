// The admin API the page uses, through kit.api: the MediaBrowser token header, JSON, a timeout, and a refusal's
// ProblemDetails as a KitApiError whose message is its detail and whose problem is the body (Code, Users, a refused dry
// run's Preview). Every route is RequiresElevation. Every change is previewed as its own write's dry run (?dryRun=true),
// so the page doesn't use POST Preview; every save sends that dry run's PreviewToken back as ifPreview, and the server
// refuses one that would now do anything else (409 Changed, nothing written).
function arApi(method, path, opts) {
    return kit.api(method, 'AccessRoles/Admin/' + path, opts);
}

const arAdmin = {
    diagnostics: function () {
        return arApi('GET', 'Diagnostics');
    },
    roles: function () {
        return arApi('GET', 'Roles');
    },
    libraries: function () {
        return arApi('GET', 'Libraries');
    },
    users: function () {
        return arApi('GET', 'Users');
    },
    effective: function (userId) {
        return arApi('GET', 'Users/' + arId(userId) + '/Effective');
    },
    createRole: function (body, query) {
        return arApi('POST', 'Roles', { body: body, query: query });
    },
    updateRole: function (roleId, body, query) {
        return arApi('PUT', 'Roles/' + arId(roleId), { body: body, query: query });
    },
    deleteRole: function (roleId, query) {
        return arApi('DELETE', 'Roles/' + arId(roleId), { query: query });
    },
    defaultRoles: function (roleIds, query) {
        return arApi('PUT', 'DefaultRoles', { body: { RoleIds: roleIds }, query: query });
    },
    updateUser: function (userId, body, query) {
        return arApi('PUT', 'Users/' + arId(userId), { body: body, query: query });
    },
    bulk: function (body, query) {
        return arApi('POST', 'Users/Bulk', { body: body, query: query });
    },
    unmanage: function (userId, body, query) {
        return arApi('POST', 'Users/' + arId(userId) + '/Unmanage', { body: body, query: query });
    },
    importExisting: function (query) {
        return arApi('POST', 'ImportExisting', { body: {}, query: query });
    },
    // The library sync; with userId, for that user only (the drift's per-user Revert).
    sync: function (query) {
        return arApi('POST', 'Sync', { body: {}, query: query });
    },
    // The library lifecycle's two writes (the Libraries tab), each previewed as its own dry run.
    grantAgain: function (libraryId, query) {
        return arApi('POST', 'Libraries/' + arId(libraryId) + '/GrantAgain', { body: {}, query: query });
    },
    purge: function (libraryId, query) {
        return arApi('POST', 'Libraries/' + arId(libraryId) + '/Purge', { body: {}, query: query });
    },
    // The audit log, newest first (limit, userId, action, fromUtc, toUtc, rule, and before: the Seq of the last line shown,
    // the next page's cursor), and its JSON Lines export, the response itself (raw) for the download.
    audit: function (query) {
        return arApi('GET', 'Audit', { query: query });
    },
    auditExport: function (query) {
        return arApi('GET', 'Audit/Export', { query: query, raw: true });
    },
    // The settings (the Settings tab): PUT sends only what the admin changed, a dry run first.
    settings: function () {
        return arApi('GET', 'Settings');
    },
    updateSettings: function (body, query) {
        return arApi('PUT', 'Settings', { body: body, query: query });
    },
    // Lockout and recovery: ending Degraded mode, each a dry run first.
    resolveDegraded: function (query) {
        return arApi('POST', 'Degraded/Resolve', { body: {}, query: query });
    },
    // Releasing the native locks before the plugin is disabled or deleted: everyone (no UserIds), with
    // restoreLibraries=true the original library access too.
    releaseLocks: function (query) {
        return arApi('POST', 'ReleaseLocks', { body: {}, query: query });
    },
    // The Data tab: the export (the response itself, raw, for the download), the import of one (its
    // body the parsed file; a dry run unless dryRun=false), the snapshots and the restore of one, each a dry run first.
    exportData: function () {
        return arApi('GET', 'Export', { raw: true });
    },
    importData: function (body, query) {
        return arApi('POST', 'Import', { body: body, query: query });
    },
    snapshots: function () {
        return arApi('GET', 'Snapshots');
    },
    restoreSnapshot: function (name, query) {
        return arApi('POST', 'Snapshots/Restore', { body: {}, query: Object.assign({ name: name }, query || {}) });
    },
    // The Diagnostics tab's actions: Remap by name (a dry run, then the save with its PlanToken as
    // ifPlan), the deleted-user policy for unknown user ids, and the maintenance task queued now.
    remapUsers: function (query) {
        return arApi('POST', 'Users/Remap', { body: {}, query: Object.assign({ by: 'Name' }, query || {}) });
    },
    applyPolicy: function (userIds) {
        return arApi('POST', 'Users/ApplyPolicy', { body: { UserIds: userIds } });
    },
    runMaintenance: function () {
        return arApi('POST', 'Maintenance/Run', { body: {} });
    }
};

// The query of a write: only the options that are set (ApiClient.getUrl would write false and null too).
function arWriteQuery(options) {
    const query = {};
    if (options && options.ifPreview) {
        query.ifPreview = options.ifPreview;
    }

    if (options && options.onUnmanage) {
        query.onUnmanage = options.onUnmanage;
    }

    if (options && options.confirmNoFullAdmin) {
        query.confirmNoFullAdmin = true;
    }

    if (options && options.dryRun) {
        query.dryRun = true;
    }

    return query;
}
