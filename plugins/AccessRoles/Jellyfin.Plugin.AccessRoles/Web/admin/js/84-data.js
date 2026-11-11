// The Data tab: Access Roles' data file as Diagnostics describes it (its status, schema and
// last snapshot, the copies kept next to it before an upgrade or after a corrupt load), its records counted, and what the
// settings keep; then Export, Import (the file checked with a dry run first, then Merge or Replace through the preview
// of what it does to each user, saved with that dry run's PreviewToken) and the snapshots with Restore (the same preview;
// afterwards the library sync waits for its own dry run and confirmation on the Overview).

const AR_IMPORT_USER_REASONS = {
    NotOnThisServer: 'not on this server',
    NoKnownName: 'the export has no name for them',
    NoUserWithThisName: 'no user here has that name',
    SameNameTwice: 'two users have that name',
    DeletedOnThisServer: 'deleted on this server',
    DeletedOnExportingServer: 'deleted on the server that exported it'
};

const AR_IMPORT_LIBRARY_REASONS = {
    NoSuchLibrary: 'no library here has its folders or its name',
    Ambiguous: 'more than one library here matches, or another library of the export takes it'
};

// Why a skipped record was left out, from its Reason (and its Libraries or Detail, when it has them).
function arImportSkippedReason(s) {
    if (s.Reason === 'NameTaken') {
        return 'another role has its name';
    }

    if (s.Reason === 'RoleNotImported') {
        return 'the role isn\'t imported';
    }

    if (s.Reason === 'LibrariesUnmatched') {
        return 'libraries it names aren\'t found here ('
            + (s.Libraries || []).map(arImportName).join(', ')
            + '), and without them it would grant more than the export had it';
    }

    if (s.Reason === 'UserLibrariesUnmatched') {
        return 'libraries their own Remove list leaves out aren\'t found here ('
            + (s.Libraries || []).map(arImportName).join(', ')
            + '), and without them they would see more than the export had them see';
    }

    return s.Detail || AR_IMPORT_LIBRARY_REASONS[s.Reason] || s.Reason;
}

const AR_IMPORT_SKIPPED = {
    Role: 'Role',
    RoleLibrary: 'A role\'s library',
    Assignment: 'The roles and overrides of',
    UserLibrary: 'A user\'s library override',
    AssignmentRole: 'A role a user has',
    DefaultRole: 'A default role'
};

// A user, a library or a record of the report: its name, else its id.
function arImportName(item) {
    return (item && (item.Name || item.LastKnownName)) || ('id ' + arId(item && (item.Id || item.ExportId || item.UserId || item.OldId)));
}

function arImportCounts(counts) {
    counts = counts || {};
    return [(counts.Added || 0) + ' added', (counts.Updated || 0) + ' updated', (counts.Unchanged || 0) + ' unchanged']
        .concat(counts.KeptNewer ? [counts.KeptNewer + ' kept (newer here)'] : [])
        .concat(counts.Removed ? [counts.Removed + ' removed'] : [])
        .join(', ');
}

// The report of an import's or a restore's dry run: where it comes from, who and what it found here, the records, what
// it leaves out, and what it does to each user.
function arImportReport(report, libraryName) {
    const users = report.Users || {};
    const libraries = report.Libraries || {};
    const records = report.Records || {};
    const mapped = users.Mapped || [];
    const unknown = users.Unknown || [];
    const unmatched = libraries.Unmatched || [];
    const byFallback = (libraries.Mapped || []).filter(function (l) {
        return l.By && l.By !== 'Id';
    });
    const rows = [
        ['From', report.Source === 'Snapshot'
            ? 'The snapshot ' + report.SourceName + ' (' + arDate(report.SourceUtc) + ')'
            : 'An export of Access Roles ' + (report.SourceName || '') + ' made ' + arDate(report.SourceUtc) + (report.SameServer ? ' on this server' : ' on another server')],
        ['Users found here', mapped.length + (report.MapUsersBy === 'Name' ? ', by name' : ', by id')],
        ['Roles', arImportCounts(records.Roles)],
        ['Users\' roles and overrides', arImportCounts(records.Assignments)]
    ];
    if (records.Ledger && records.Ledger.Added) {
        rows.push(['Original access filled in', arCount(records.Ledger.Added, 'user')]);
    }

    const parts = [arTable(rows)];
    if (unknown.length) {
        parts.push(arHeading(4, 'Users not found here (' + unknown.length + '): nothing is written for them'));
        parts.push(arList(unknown.map(function (u) {
            return arImportName(u) + ': ' + (AR_IMPORT_USER_REASONS[u.Reason] || u.Reason) + ', ' + arCount(u.Records || 0, 'record');
        })));
    }

    if (byFallback.length) {
        parts.push(arHeading(4, 'Libraries found by their folders or name (' + byFallback.length + ')'));
        parts.push(arList(byFallback.map(function (l) {
            return l.Name + ' (by ' + (l.By === 'Locations' ? 'folders' : 'name') + ')';
        })));
    }

    if (unmatched.length) {
        parts.push(arHeading(4, 'Libraries not found here (' + unmatched.length + '): left out, never guessed'));
        parts.push(arList(unmatched.map(function (l) {
            return arImportName(l) + ((l.Locations || []).length ? ' (' + l.Locations.join(', ') + ')' : '') + ': ' + (AR_IMPORT_LIBRARY_REASONS[l.Reason] || l.Reason);
        })));
    }

    const skipped = report.Skipped || [];
    if (skipped.length) {
        parts.push(arHeading(4, 'Left out (' + skipped.length + ')'));
        parts.push(arList(skipped.map(function (s) {
            return (AR_IMPORT_SKIPPED[s.Kind] || s.Kind) + ' ' + arImportName(s) + (s.Of ? ' of ' + s.Of : '') + ': ' + arImportSkippedReason(s);
        })));
    }

    const preview = report.Preview || {};
    const changed = preview.Users || [];
    parts.push(arHeading(4, report.NoChange ? 'It changes nothing here.' : changed.length ? 'What it does to each user (' + changed.length + ')' : 'It changes nobody\'s access.'));
    if (changed.length) {
        parts.push(arEl('ul', { class: arClass('preview-users') }, changed.map(function (row) {
            return arPreviewRow(row, libraryName || String);
        })));
    }

    (report.Notes || []).forEach(function (note) {
        parts.push(arHint(note));
    });
    return parts;
}

// The query of an import or a restore: dryRun always said (the server's default is a dry run), with a write's options.
function arDataQuery(options, extra) {
    const query = Object.assign(arWriteQuery(options), extra || {});
    query.dryRun = !!(options && options.dryRun);
    return query;
}

// The text of a file the admin picked (Blob.text, else a FileReader).
function arReadFile(file) {
    if (file && typeof file.text === 'function') {
        return Promise.resolve(file.text());
    }

    return new Promise(function (resolve, reject) {
        const reader = new FileReader();
        reader.onload = function () {
            resolve(String(reader.result || ''));
        };
        reader.onerror = function () {
            reject(new Error('The file couldn\'t be read.'));
        };
        reader.readAsText(file);
    });
}

function arDataTab(ctx) {
    const loading = arEl('p', { id: 'AccessRolesDataLoading', text: 'Loading…' });
    const content = arEl('div', { id: 'AccessRolesData' });

    // Export.
    const exportError = arErrorLine();
    const exportButton = arButton('Export', function () {
        arBusy(exportButton, exportData);
    });
    exportButton.id = 'AccessRolesDataExport';
    exportButton.setAttribute('data-ar-focus', 'data-export');

    // Import: the file, how users are matched, the check (Merge's dry run), then Merge… or Replace….
    // (kit.field gives each its id, for its label: the probes and tests find them by data-ar-import.)
    const fileInput = kit.input({ type: 'file', accept: '.json,application/json', 'data-ar-focus': 'import-file', 'data-ar-import': 'file' });
    const usersBy = arSelect('Match users by', [{ value: 'Id', text: 'Their id (this server, or a copy of it)' }, { value: 'Name', text: 'Their name (another server)' }], 'Id', function () {
        forget();
    });
    usersBy.select.setAttribute('data-ar-import', 'users-by');
    const checkButton = arButton('Check the file', function () {
        arBusy(checkButton, check);
    });
    checkButton.id = 'AccessRolesImportCheck';
    const mergeButton = arButton('Merge…', function () {
        arBusy(mergeButton, function () {
            return save('Merge');
        }, canSave);
    }, { disabled: true });
    mergeButton.id = 'AccessRolesImportMerge';
    const replaceButton = arButton('Replace…', function () {
        arBusy(replaceButton, function () {
            return save('Replace');
        }, canSave);
    }, { disabled: true });
    replaceButton.id = 'AccessRolesImportReplace';
    const importError = arErrorLine();
    const importReport = arEl('div', { id: 'AccessRolesImportReport', 'aria-live': 'polite' });
    // The file as checked: its parsed body and the users' match; another file or match needs its own check.
    let checked = null;
    fileInput.addEventListener('change', function () {
        forget();
    });

    // Snapshots.
    const snapshots = arEl('div', { id: 'AccessRolesSnapshots' });
    const snapshotsResult = arEl('div', { id: 'AccessRolesSnapshotsResult', 'aria-live': 'polite' });
    const snapshotsError = arErrorLine();
    let snapshotList = null;

    const element = arEl('div', { class: arClass('data') }, [
        arEl('div', { class: arClass('section') }, [
            arHeading(3, 'Data'),
            arHint('Access Roles keeps its roles, assignments, ledger of original access and known libraries in one data file, with snapshots and copies next to it. Library access and settings are Jellyfin\'s own.'),
            loading,
            content
        ]),
        arEl('div', { class: arClass('section'), id: 'AccessRolesDataExportSection' }, [
            arHeading(3, 'Export'),
            arHint('The roles, each user\'s roles and overrides, the default roles, the original access of the users it manages, the known libraries, the locks it turned on, and the settings for reference. It holds user and library names: keep it safe. The audit log is exported on the Audit tab.'),
            arActions([exportButton]),
            exportError
        ]),
        arEl('div', { class: arClass('section'), id: 'AccessRolesDataImportSection' }, [
            arHeading(3, 'Import'),
            arHint('Check an export first: the check lists the users and libraries it finds here and what it does to each user, and saves nothing. Merge adds its roles and users and takes its newer copies; Replace makes the roles, the users\' roles and the default roles the export\'s. Either takes a snapshot first. Users and libraries not found here are left out; the settings are never imported.'),
            kit.field('Export file', fileInput),
            usersBy.element,
            arActions([checkButton, mergeButton, replaceButton]),
            importError,
            importReport
        ]),
        arEl('div', { class: arClass('section'), id: 'AccessRolesDataSnapshotsSection' }, [
            arHeading(3, 'Snapshots'),
            arHint('A snapshot a day, and one before every import, restore, Remap by name or deleted user\'s policy. Restore makes the roles, the users\' roles and the default roles the snapshot\'s, after a snapshot of what it replaces; the library sync then waits until you have seen its dry run and confirmed it on the Overview.'),
            snapshots,
            snapshotsError,
            snapshotsResult
        ])
    ]);

    function readOnly() {
        return !!ctx.readOnly;
    }

    function forget() {
        checked = null;
        arClear(importReport);
        importError.textContent = '';
        mergeButton.disabled = true;
        replaceButton.disabled = true;
    }

    function canSave() {
        return !!checked && !readOnly();
    }

    function exportData() {
        exportError.textContent = '';
        return arAdmin.exportData().then(function (response) {
            const name = arFileName(response, 'accessroles-export.json');
            return Promise.resolve(typeof response.blob === 'function' ? response.blob() : response.text().then(function (text) {
                return new Blob([text], { type: 'application/json' });
            })).then(function (blob) {
                arDownload(blob, name);
                kit.toast('The data is saved as ' + name + '.');
            });
        }).then(null, function (e) {
            exportError.textContent = arErrorText(e, 'The data could not be exported.');
        });
    }

    function enableSaves() {
        mergeButton.disabled = readOnly();
        replaceButton.disabled = readOnly();
        if (readOnly()) {
            importError.textContent = 'The data file is read-only: nothing can be imported until it is put right.';
        }
    }

    // Reads the file, then Merge's dry run with the users' match chosen: the report, and Merge… and Replace… on.
    function check() {
        forget();
        const file = fileInput.files && fileInput.files[0];
        if (!file) {
            importError.textContent = 'Choose an export file first.';
            return null;
        }

        const by = usersBy.select.value;
        return arReadFile(file).then(function (text) {
            let body;
            try {
                body = JSON.parse(text);
            } catch (e) {
                throw new Error('This file isn\'t JSON: choose a file the Export button made.');
            }

            return arAdmin.importData(body, arDataQuery({ dryRun: true }, { mode: 'Merge', mapUsersBy: by })).then(function (report) {
                checked = { body: body, by: by };
                importReport.appendChild(arHeading(4, 'The check (as Merge; nothing saved)'));
                arImportReport(report || {}, ctx.libraryName).forEach(function (part) {
                    importReport.appendChild(part);
                });
                enableSaves();
            }, function (e) {
                // A dry run the save would refuse as it is (a choice to make, a box to tick): Merge… and Replace… say what.
                if (e && e.problem && e.problem.Preview) {
                    checked = { body: body, by: by };
                    importReport.appendChild(arHint(arErrorText(e, 'The import would be refused as it is.') + ' Merge… and Replace… show what to choose.'));
                    enableSaves();
                    return;
                }

                throw e;
            });
        }).then(null, function (e) {
            importError.textContent = arErrorText(e, 'The file could not be checked.');
        });
    }

    // Merge… or Replace…: the preview of the very import (its dry run), saved with its PreviewToken.
    function save(mode) {
        const sent = checked;
        if (!sent) {
            return null;
        }

        return arReview({
            title: mode === 'Replace' ? 'Import: replace' : 'Import: merge',
            intro: mode === 'Replace'
                ? 'The roles, the users\' roles and overrides and the default roles become the export\'s; a role or a user\'s roles it doesn\'t have go. A snapshot is taken first.'
                : 'The export\'s roles and users\' roles are added, and its newer copies replace older ones here. A snapshot is taken first.',
            saveLabel: mode === 'Replace' ? 'Replace' : 'Merge',
            libraryName: ctx.libraryName,
            request: function () {
                return sent.body;
            },
            send: function (body, options) {
                return arAdmin.importData(body, arDataQuery(options, { mode: mode, mapUsersBy: sent.by }));
            },
            empty: function (report) {
                return report && report.NoChange ? 'The export changes nothing here.' : null;
            }
        }).then(function (report) {
            if (!report) {
                return null;
            }

            forget();
            kit.append(importReport, arHeading(4, mode === 'Replace' ? 'Replaced' : 'Merged'), arHint('The state before is saved as the snapshot ' + (report.SafetySnapshot || '') + '.'));
            arImportReport(report, ctx.libraryName).forEach(function (part) {
                importReport.appendChild(part);
            });
            kit.toast(mode === 'Replace' ? 'The data is the export\'s now.' : 'The export is merged.');
            return Promise.all([ctx.refresh(), loadSnapshots()]);
        });
    }

    function snapshotLabel(s) {
        return (s.Kind === 'PreOp' ? 'before ' + (s.Operation || 'an operation') : 'daily') + ', ' + arDate(s.Utc)
            + (typeof s.Size === 'number' ? ', ' + kit.format.size(s.Size) : '') + (s.SchemaVersion ? ', schema ' + s.SchemaVersion : '');
    }

    function renderSnapshots() {
        arClear(snapshots);
        if (!snapshotList) {
            snapshots.appendChild(arHint('Loading the snapshots…'));
            return;
        }

        const list = snapshotList.Snapshots || [];
        if (snapshotList.SyncHeld) {
            snapshots.appendChild(arBanner('The library sync is held after a restore: see its dry run and confirm it on the Overview.', 'warning'));
        }

        if (!list.length) {
            snapshots.appendChild(arHint('None yet: the first is taken by the daily maintenance.'));
            return;
        }

        snapshots.appendChild(arEl('ul', { class: arClass('list') }, list.map(function (s) {
            const button = arButton('Restore…', function () {
                arBusy(button, function () {
                    return restore(s);
                }, function () {
                    return !readOnly();
                });
            }, { disabled: readOnly(), title: readOnly() ? 'The data file is read-only.' : null });
            button.setAttribute('data-snapshot', s.Name);
            return arEl('li', { class: arClass('snapshot') }, [arEl('span', { text: s.Name + ': ' + snapshotLabel(s) + ' ' }), button]);
        })));
    }

    function loadSnapshots() {
        return arAdmin.snapshots().then(function (list) {
            snapshotList = list || {};
            snapshotsError.textContent = '';
            renderSnapshots();
        }, function (e) {
            snapshotsError.textContent = arErrorText(e, 'The snapshots could not be listed.');
        });
    }

    function restore(snapshot) {
        arClear(snapshotsResult);
        return arReview({
            title: 'Restore ' + snapshot.Name,
            intro: 'The roles, the users\' roles and overrides and the default roles become this snapshot\'s (' + snapshotLabel(snapshot) + '). A snapshot of what it replaces is taken first. Afterwards the library sync writes nothing until you have seen its dry run and confirmed it on the Overview.',
            saveLabel: 'Restore',
            libraryName: ctx.libraryName,
            request: function () {
                return snapshot.Name;
            },
            send: function (name, options) {
                return arAdmin.restoreSnapshot(name, arDataQuery(options));
            },
            empty: function (report) {
                return report && report.NoChange ? 'The data is what this snapshot holds already.' : null;
            }
        }).then(function (report) {
            if (!report) {
                return null;
            }

            const overview = arButton('Go to the Overview', function () {
                ctx.showTab('Overview');
            });
            overview.id = 'AccessRolesSnapshotsOverview';
            kit.append(snapshotsResult,
                arBanner('Restored ' + snapshot.Name + '.' + (report.SyncHeld ? ' The library sync is held: see its dry run and confirm it on the Overview.' : ''), 'warning'),
                arHint('The state before is saved as the snapshot ' + (report.SafetySnapshot || '') + '.'),
                arActions([overview]));
            kit.toast('Restored ' + snapshot.Name + '.');
            return Promise.all([ctx.refresh(), loadSnapshots()]);
        });
    }

    function render() {
        const d = ctx.diagnostics;
        if (!d) {
            return;
        }

        arShow(loading, false);
        arClear(content);
        const store = d.Store || {};
        const records = d.Records || {};
        const settings = ctx.settings;
        content.appendChild(arStatusSection('Data file', [
            ['Name', store.Name],
            ['File', store.File],
            ['Schema', store.Version + (store.CurrentVersion && store.Version !== store.CurrentVersion ? ' (this build writes ' + store.CurrentVersion + ')' : '')],
            ['Status', store.Status + (store.ReadOnly ? ', read-only: nothing can be changed until it is put right' : '')],
            ['Message', store.Message || 'None'],
            ['Last snapshot', arDate(store.LastSnapshotUtc) || 'None yet'],
            ['Snapshots kept', settings ? (settings.SnapshotsToKeep === 0 ? 'None (switched off)' : 'The last ' + settings.SnapshotsToKeep) : 'See the Settings tab'],
            ['Audit log kept', settings ? (settings.AuditRetentionMonths === 0 ? 'Without a time limit' : arCount(settings.AuditRetentionMonths, 'month')) + ', 50 MB at most' : 'See the Settings tab']
        ]));
        const kept = store.KeptFiles || [];
        content.appendChild(arEl('div', { class: arClass('section'), id: 'AccessRolesKeptFiles' }, [
            arHeading(4, 'Copies kept next to it (' + (store.KeptFileCount || 0) + ')'),
            kept.length
                ? arEl('ul', { class: arClass('list') }, kept.map(function (f) {
                    return arEl('li', { text: f.Name + ': ' + (f.Kind || 'copy') + (f.SchemaVersion ? ', schema ' + f.SchemaVersion : '') + ', ' + arDate(f.Utc) + (typeof f.Size === 'number' ? ', ' + kit.format.size(f.Size) : '') });
                }))
                : arHint('None: no upgrade or corrupt load has kept one.')
        ]));
        content.appendChild(arStatusSection('Records', [
            ['Roles', records.Roles],
            ['Users with roles or overrides', records.Assignments],
            ['Default roles', records.DefaultRoles],
            ['Known libraries', records.KnownLibraries],
            ['Renames waiting', records.PendingRenames],
            ['Ledger entries (original access)', records.LedgerEntries],
            ['Library releases waiting for the sync', records.PendingReleases],
            ['Records with a value a newer version wrote', records.UnknownValues]
        ]));
        if (checked) {
            enableSaves();
        }

        renderSnapshots();
    }

    function failed() {
        arShow(loading, true);
        loading.textContent = 'The data file\'s status couldn\'t be loaded. The server log has the details.';
    }

    // The settings it shows are read once, with the Settings tab's; the snapshots each time the tab is shown.
    function onShow() {
        const reads = [loadSnapshots()];
        if (!ctx.settings) {
            reads.push(ctx.loadSettings().then(render, function () {
                return null;
            }));
        }

        return Promise.all(reads);
    }

    return { element: element, render: render, failed: failed, onShow: onShow };
}
