// The Diagnostics tab: everything GET /AccessRoles/Admin/Diagnostics answers. The five tables
// the Overview had until 0.9.10 (Plugin, Data, Library sync, Jellyfin API actions, Server users; #AccessRolesStatus, as
// the probes read them), then the settings filter's own state, the requests it blocked in the open window and the
// server-wide rules' times, the rule table against this server, the web client's reports, and the whole document as text. A rule
// name may be several joined with + (ServerHardening.RuleNames): shown as its rules, and the search finds a rule or the
// name as it is. Since 0.9.15 also the actions for the users this server doesn't know (Remap by name, Apply deleted-user
// policy) and Run maintenance now.

// The settings locks: on or off and why, and the filter's counts since the start.
function arSettingsLocksText(enforcement) {
    const totals = (enforcement.Blocked || {}).Totals || {};
    const state = enforcement.SettingsLocks ? 'Enforced' : 'Off (' + (enforcement.SettingsLocksOffBecause || 'unknown') + ')';
    return state + '; since the start ' + (totals.Refused || 0) + ' refused, ' + (totals.Reverted || 0) + ' reverted, '
        + (totals.FailedClosed || 0) + ' failed closed';
}

// The server-wide rules (the admin surfaces and the library leak hardening) as the settings make them apply, and what
// they hid since the start.
function arServerWideText(enforcement) {
    const wide = enforcement.ServerWide || {};
    const totals = (enforcement.Blocked || {}).Totals || {};
    const reads = wide.ConfigurationReads === 'Redact' ? 'configuration reads redacted' : 'configuration reads: ' + ((wide.ConfigurationReadAllowList || []).join(', ') || 'none');
    return (enforcement.Hardening ? '' : 'Off; ') + reads + '; restart ' + (wide.BlockNonAdminRestart ? 'administrators only' : 'not blocked')
        + '; public user list ' + (wide.TrimPublicUsers ? 'trimmed' : 'not trimmed') + '; library hardening ' + (wide.LibraryHardening || 'unknown')
        + '; since the start ' + (totals.Hidden || 0) + ' hidden';
}

function arNotEnforcedText(enforcement) {
    const locks = enforcement.NotEnforcedLocks || [];
    return locks.length ? locks.join(', ') + ': stored, not enforced yet' : 'None';
}

function arManifestRows(manifest) {
    if (!manifest || manifest.Error) {
        return [['Problem', manifest ? manifest.Error : 'Not reported.']];
    }

    const rows = [
        ['Reviewed for', 'Jellyfin ' + manifest.JellyfinVersion + ' (' + manifest.Source + ')'],
        ['Actions', manifest.Actions + ', ' + manifest.WithRules + ' with rules']
    ];
    const server = manifest.Server;
    if (!server) {
        rows.push(['This server', 'Not compared.']);
        return rows;
    }

    rows.push(['This server', server.Matches
        ? 'All ' + server.Actions + ' of its Jellyfin.Api actions are in the list, unchanged.'
        : server.Actions + ' Jellyfin.Api actions; they differ from the list.']);
    [['Missing from this server', server.Missing], ['Not reviewed yet', server.Unreviewed], ['Changed', server.Changed], ['Two of one name', server.Duplicates]]
        .forEach(function (entry) {
            if (entry[1] && entry[1].length) {
                rows.push([entry[0] + ' (' + entry[1].length + ')', arList(entry[1].slice(0, 50))]);
            }
        });
    return rows;
}

function arSyncRows(sync) {
    if (!sync) {
        return [['State', 'Not reported.']];
    }

    const last = sync.LastRun;
    const totals = sync.Totals || {};
    const rows = [
        ['State', sync.State === 'Active' ? 'Active' : 'Paused: ' + sync.PausedBecause],
        ['Drift policy', sync.DriftPolicy],
        ['Backstop every', sync.ReconcileIntervalMinutes + ' min'],
        ['Last sync', last ? arDate(last.Utc) + ' (' + last.Trigger + ', ' + last.DurationMs + ' ms)' : 'Not run yet'],
        ['Last sync wrote', last ? last.Written + ' of ' + last.ManagedUsers + ' managed users, ' + last.Skipped + ' skipped' : ''],
        ['Drift', sync.DriftCount],
        ['Releases waiting', sync.PendingReleases],
        ['Renames waiting', sync.PendingRenames],
        ['Checks after writing waiting', sync.PendingVerifications],
        ['Since the server started', totals.Writes + ' writes, ' + totals.Releases + ' releases, ' + totals.RevertedAfterWrite + ' written again after a revert, ' + totals.Aborted + ' aborted, ' + totals.Errors + ' errors'
            + (totals.UserRuns ? ', ' + totals.UserRuns + ' runs for one user' : '')]
    ];
    if (sync.LastError) {
        rows.push(['Last error', arDate(sync.LastErrorUtc) + ': ' + sync.LastError]);
    }

    return rows;
}

// The open window's counts (BlockCounter: "<rule> <outcome>" → count) as rows of { rule, outcome, count }; the rule is
// everything before the last space, so a rule name of several (A+B) stays whole.
function arOpenWindowRows(openWindows) {
    return Object.keys(openWindows || {}).map(function (key) {
        const space = key.lastIndexOf(' ');
        return { rule: space > 0 ? key.substring(0, space) : key, outcome: space > 0 ? key.substring(space + 1) : '', count: openWindows[key] };
    });
}

// A table whose first column is a rule name shown as its rules: headings, then rows of [rule name, cells…].
function arRuleTable(id, headings, rows) {
    return arEl('table', { class: arClass('table') + ' ' + arClass('rule-table'), id: id }, [
        arEl('thead', null, arEl('tr', null, headings.map(function (heading) {
            return arEl('th', { scope: 'col', text: heading });
        })))
    ].concat([arEl('tbody', null, rows.map(function (row) {
        return arEl('tr', { 'data-rule-name': String(row[0]) }, [arEl('td', null, arRuleNode(row[0]))].concat(row.slice(1).map(function (cell) {
            return arEl('td', { text: cell === undefined || cell === null ? '' : String(cell) });
        })));
    }))]));
}

function arMs(value) {
    return value === undefined || value === null ? '' : value + ' ms';
}

function arDiagnosticsTab(ctx) {
    const loading = arEl('p', { id: 'AccessRolesDiagnosticsLoading', text: 'Loading the diagnostics…' });
    const status = arEl('div', { id: 'AccessRolesStatus' });
    const more = arEl('div', { id: 'AccessRolesDiagnosticsMore' });
    const search = kit.input({ type: 'search', 'data-ar-focus': 'rule-search', autocomplete: 'off', spellcheck: 'false' });
    search.setAttribute('aria-describedby', 'AccessRolesRuleSearchHint');
    search.addEventListener('input', function () {
        renderRules();
    });
    const searchField = kit.field('Find a rule', search);
    searchField.id = 'AccessRolesRuleSearch';
    const rules = arEl('div', { id: 'AccessRolesRules', 'aria-live': 'polite' });
    const raw = arEl('pre', { class: arClass('raw'), id: 'AccessRolesDiagnosticsRaw', hidden: true });
    const rawButton = arButton('Show the whole document', function () {
        const shown = raw.hasAttribute('hidden');
        arShow(raw, shown);
        rawButton.textContent = shown ? 'Hide the whole document' : 'Show the whole document';
        rawButton.setAttribute('aria-expanded', shown ? 'true' : 'false');
    });
    rawButton.id = 'AccessRolesDiagnosticsRawToggle';
    rawButton.setAttribute('aria-expanded', 'false');
    rawButton.setAttribute('aria-controls', 'AccessRolesDiagnosticsRaw');
    rawButton.setAttribute('data-ar-focus', 'diagnostics-raw');
    const refresh = arButton('Refresh', function () {
        arBusy(refresh, function () {
            return ctx.refresh();
        });
    });
    refresh.id = 'AccessRolesDiagnosticsRefresh';
    refresh.setAttribute('data-ar-focus', 'diagnostics-refresh');
    let last = null;
    const userActions = arUserActions(ctx);

    const element = arEl('div', { class: arClass('diagnostics') }, [
        arEl('div', { class: arClass('section') }, [arHeading(3, 'Diagnostics'), arHint('What GET /AccessRoles/Admin/Diagnostics answers, for this server and this build.'), loading, status, arActions([refresh])]),
        userActions.element,
        more,
        arEl('div', { class: arClass('section') }, [
            arHeading(3, 'Rules'),
            arEl('p', { class: arClass('hint'), id: 'AccessRolesRuleSearchHint', text: 'A request the filter checks with more than one rule is counted and timed under their names joined with +, shown here as each rule. Search for a rule, or for a name as it is (A+B).' }),
            searchField,
            rules
        ]),
        arEl('div', { class: arClass('section') }, [arActions([rawButton]), raw])
    ]);

    function renderRules() {
        arClear(rules);
        const d = last || {};
        const enforcement = d.Enforcement || {};
        const wanted = search.value;
        const windows = arOpenWindowRows((enforcement.Blocked || {}).OpenWindows).filter(function (row) {
            return arRuleMatches(row.rule, wanted);
        });
        const cost = enforcement.Cost || {};
        const timed = Object.keys(cost).filter(function (name) {
            return arRuleMatches(name, wanted);
        });
        rules.appendChild(arHeading(4, 'Requests blocked in the open window (' + ((enforcement.Blocked || {}).WindowMinutes || 0) + ' min, before the audit log has them)'));
        rules.appendChild(windows.length
            ? arRuleTable('AccessRolesOpenWindows', ['Rule', 'Outcome', 'Requests'], windows.map(function (row) {
                return [row.rule, row.outcome, row.count];
            }))
            : arHint(wanted ? 'No rule of the open window matches.' : 'None in the open window.'));
        rules.appendChild(arHeading(4, 'Rule times since the start'));
        rules.appendChild(timed.length
            ? arRuleTable('AccessRolesRuleCost', ['Rule', 'Checks', 'p50', 'p95', 'Max'], timed.map(function (name) {
                const entry = cost[name] || {};
                return [name, entry.Count, arMs(entry.P50Ms), arMs(entry.P95Ms), arMs(entry.MaxMs)];
            }))
            : arHint(wanted ? 'No timed rule matches.' : 'No rule has been timed yet.'));
    }

    function ruleTableRows(table) {
        if (!table) {
            return [['State', 'Not reported.']];
        }

        if (table.Error) {
            return [['Problem', table.Error]];
        }

        if (table.Pending) {
            return [['State', 'Not checked yet: the check runs once the server has started.']];
        }

        const je = table.JellyfinEnhanced || {};
        const plugins = table.PluginActions || {};
        const rows = [
            ['Checked', arDate(table.CheckedUtc)],
            ['Filter registered', table.FilterRegistered ? 'Yes' : 'No'],
            ['Enforcement active', table.EnforcementActive ? 'Yes' : 'No'],
            ['Rule rows bound', table.Bound + ' of ' + table.WithRules]
        ];
        [['Missing from this server', table.Missing], ['Changed', table.Changed], ['Not reviewed yet', table.Unreviewed], ['Plugin controllers flagged', table.Flagged]].forEach(function (entry) {
            if (entry[1] && entry[1].length) {
                rows.push([entry[0] + ' (' + entry[1].length + ')', arList(entry[1].slice(0, 50).map(String))]);
            }
        });
        rows.push(['Jellyfin Enhanced', je.Installed ? 'Installed: ' + (je.Locked || []).length + ' saves follow its lock, ' + (je.Uncovered || []).length + ' actions not covered (' + (je.UncoveredUnreviewed || []).length + ' not reviewed)' : 'Not installed']);
        const assemblies = Object.keys(plugins);
        if (assemblies.length) {
            rows.push(['Other plugins\' actions', arList(assemblies.map(function (name) {
                return name + ': ' + plugins[name];
            }))]);
        }

        return rows;
    }

    function filterRows(enforcement) {
        const filter = enforcement.Filter || {};
        const snapshot = enforcement.Snapshot || {};
        return [
            ['Filter errors since the start', filter.FilterErrors || 0],
            ['Last filter error', filter.LastError ? arDate(filter.LastErrorUtc) + ': ' + filter.LastError : 'None'],
            ['Degraded', arDegradedRow(enforcement.Degraded) || filter.DegradedBecause || snapshot.DegradedBecause || 'No'],
            ['Lock snapshot', snapshot.Error ? snapshot.Error : (snapshot.ManagedUsers || 0) + ' managed users, ' + (snapshot.LockedUsers || 0) + ' with locks']
        ];
    }

    function webRows(d) {
        const anchors = d.WebAnchors || {};
        const reports = anchors.Reports || {};
        const rows = [
            ['Problem reports', anchors.Reporting ? 'On' : 'Off'],
            ['Anchors declared', (anchors.Declared || []).length],
            ['Reports', Object.keys(reports).map(function (key) {
                return key + ' ' + reports[key];
            }).join(', ') || 'None'],
            ['Clients reporting problems', (d.ClientHealth || []).length]
        ];
        (d.Pages || []).forEach(function (page) {
            rows.push(['Page ' + page.Key, page.Host + (page.Detail ? ': ' + page.Detail : '')]);
        });
        return rows;
    }

    function render(d) {
        last = d || {};
        arShow(loading, false);
        arClear(status);
        arClear(more);
        const abi = d.Abi || {};
        const store = d.Store || {};
        const records = d.Records || {};
        const users = d.ServerUsers || {};
        const enforcement = d.Enforcement || {};
        status.appendChild(arStatusSection('Plugin', [
            ['Version', d.PluginVersion],
            ['Kit', d.KitVersion],
            ['Jellyfin', 'Built for ' + abi.Target + ', running on ' + abi.Server + (abi.Active ? '' : ' (inactive)')],
            ['Enforcement', enforcement.Message || (enforcement.Active ? 'Active' : 'None')],
            ['Settings locks', arSettingsLocksText(enforcement)],
            ['Server-wide rules', arServerWideText(enforcement)],
            ['Locks not enforced yet', arNotEnforcedText(enforcement)]
        ]));
        status.appendChild(arStatusSection('Data', [
            ['File', store.File],
            ['Schema', store.Version + (store.CurrentVersion && store.Version !== store.CurrentVersion ? ' (this build writes ' + store.CurrentVersion + ')' : '')],
            ['Status', store.Status + (store.ReadOnly ? ', read-only' : '')],
            ['Last snapshot', arDate(store.LastSnapshotUtc) || 'None yet'],
            ['Roles', records.Roles],
            ['Users with roles or overrides', records.Assignments],
            ['Known libraries', records.KnownLibraries],
            ['Ledger entries', records.LedgerEntries],
            ['Library releases waiting for the sync', records.PendingReleases]
        ]));
        status.appendChild(arStatusSection('Library sync', arSyncRows(d.LibrarySync)));
        status.appendChild(arStatusSection('Jellyfin API actions', arManifestRows(d.ActionManifest)));
        status.appendChild(arStatusSection('Server users', [
            ['Users', users.Users],
            ['Administrators (never restricted)', users.Administrators],
            ['Disabled', users.Disabled],
            ['Listed through', users.ListedThrough]
        ]));
        more.appendChild(arStatusSection('Settings filter', filterRows(enforcement)));
        more.appendChild(arStatusSection('Rule table', ruleTableRows(d.RuleTable)));
        more.appendChild(arStatusSection('Web client', webRows(d)));
        userActions.render(d);
        renderRules();
        raw.textContent = JSON.stringify(d, null, 2);
    }

    function failed() {
        arShow(loading, true);
        loading.textContent = 'The diagnostics couldn\'t be loaded. The server log has the details.';
    }

    return { element: element, render: render, failed: failed };
}

// Degraded mode's Diagnostics row: why, whom, from where, since when.
function arDegradedRow(degraded) {
    if (!degraded) {
        return null;
    }

    return degraded.Because + ': ' + (degraded.EveryNonAdmin ? 'every non-admin' : arCount((degraded.Users || []).length, 'user'))
        + (degraded.Source ? ', read from ' + degraded.Source : '') + ', since ' + arDate(degraded.SinceUtc)
        + (degraded.FromEarlierStart ? ' (an earlier start; the data file is usable now)' : '')
        + (degraded.CanResolve ? '; can be ended on the Overview' : '; ends when a readable copy is put back');
}

// Why Remap by name leaves an unknown id alone (the kit's UserRemapUnmatched).
const AR_REMAP_REASONS = {
    NoKnownName: 'no name was ever seen for it',
    NoUserWithThisName: 'no user here has that name',
    SameNameTwice: 'two unknown ids have that name',
    DeletedOnThisServer: 'that user was deleted here, so a new user of the name is someone else'
};

// A dry run, then the action with its token: the dialog of Remap by name and of the deleted-user policy (the kit's
// users service). opts: { title, intro, go: label, load() → Promise<{ rows: [text], ready, report }>,
// run(report) → Promise<answer>, done(answer) → text }. Answers a promise of the answer, or null when cancelled.
function arUsersActionDialog(opts) {
    return new Promise(function (resolve) {
        let shown = null;
        let running = false;
        let finished = false;
        const dialog = arDialog(opts.title, { modal: true });
        const result = arEl('div', { class: arClass('preview'), 'aria-live': 'polite' });
        const error = arErrorLine();
        const again = arButton('Check again', function () {
            arBusy(again, load);
        });
        again.hidden = true;
        const cancel = arButton('Cancel', function () {
            dialog.close();
        });
        const go = arButton(opts.go, function () {
            arBusy(go, submit, function () {
                return !!shown && shown.ready && !running;
            });
        }, { variant: 'primary', disabled: true });
        go.setAttribute('data-ar-focus', 'users-action-go');
        kit.append(dialog.body, arEl('p', { text: opts.intro }), result, error, arActions([cancel, again, go]));
        dialog.onClose(function () {
            if (!finished) {
                finished = true;
                resolve(null);
            }
        });

        function load() {
            shown = null;
            go.disabled = true;
            error.textContent = '';
            again.hidden = true;
            arClear(result);
            return Promise.resolve().then(opts.load).then(function (answer) {
                shown = answer;
                result.appendChild(arList(answer.rows));
                go.disabled = !answer.ready;
            }, function (e) {
                error.textContent = arErrorText(e, 'The check could not be made.');
            });
        }

        function submit() {
            if (!shown || !shown.ready) {
                return null;
            }

            running = true;
            return Promise.resolve().then(function () {
                return opts.run(shown.report);
            }).then(function (answer) {
                running = false;
                finished = true;
                dialog.close();
                resolve(answer);
            }, function (e) {
                running = false;
                shown = null;
                // A 409 is the token's (the kit's remap answers Conflict without a code) or a read-only data file's.
                error.textContent = e && e.status === 409
                    ? 'What this would do changed since the check, or the data cannot be written now; nothing was changed. Check again. (' + arErrorText(e, '') + ')'
                    : arErrorText(e, 'It could not be done.');
                again.hidden = false;
                go.disabled = true;
                again.focus();
            });
        }

        load();
    });
}

// The Diagnostics tab's actions for the users this server doesn't know (the kit's users service): their ids
// listed with their last seen names, Remap by name (its dry run, then the remap with its PlanToken), Apply deleted-user
// policy (Delete, Access Roles' only one: their assignments and ledger entries go, after a snapshot), and Run maintenance
// now (the daily task queued).
function arUserActions(ctx) {
    const list = arEl('div', { id: 'AccessRolesOrphanUsers' });
    const result = arEl('p', { id: 'AccessRolesUserActionsResult', 'aria-live': 'polite' });
    const error = arErrorLine();
    let orphans = [];
    let writable = false;
    const canAct = function () {
        return writable && orphans.length > 0;
    };
    const remap = arButton('Remap by name…', function () {
        arBusy(remap, remapByName, canAct);
    });
    remap.id = 'AccessRolesRemap';
    const apply = arButton('Apply deleted-user policy…', function () {
        arBusy(apply, applyPolicy, canAct);
    });
    apply.id = 'AccessRolesApplyPolicy';
    const maintenance = arButton('Run maintenance now', function () {
        arBusy(maintenance, runMaintenance);
    });
    maintenance.id = 'AccessRolesRunMaintenance';
    const element = arEl('div', { class: arClass('section'), id: 'AccessRolesUserActions' }, [
        arHeading(3, 'Users this server doesn\'t know'),
        arHint('Records under a user id this server has no user for: a user deleted while the plugin wasn\'t running, or this data restored on another server. Remap by name gives each id whose last seen name is one user\'s name here that user; Apply deleted-user policy deletes their assignments and original access. Both take a snapshot first. Run maintenance now runs the daily task: the snapshot, the audit log\'s retention and this check of the users.'),
        list,
        arActions([remap, apply, maintenance]),
        error,
        result
    ]);

    // After a Remap or Apply: the result shown, everything loaded again. When that handled the last unknown user, both
    // buttons are off now and the focus goes on to Run maintenance now, the next step (on the TV layout a disabled
    // button would leave the D-pad nowhere to start from). A focus elsewhere stays.
    function done(text) {
        result.textContent = text;
        kit.toast(text);
        return Promise.resolve(ctx.refresh()).then(function () {
            const active = document.activeElement;
            const lost = !active || active === document.body || !active.isConnected || active === remap || active === apply;
            if (!orphans.length && lost && maintenance.isConnected && !maintenance.disabled) {
                maintenance.focus();
            }
        });
    }

    function remapByName() {
        error.textContent = '';
        result.textContent = '';
        return arUsersActionDialog({
            title: 'Remap by name',
            intro: 'Each unknown id whose last seen name is one user\'s name here gets that user\'s id, with its roles, overrides and original access, after a snapshot. Nothing changes until you confirm.',
            go: 'Remap',
            load: function () {
                return arAdmin.remapUsers({ dryRun: true }).then(function (report) {
                    const mapped = (report && report.Mapped) || [];
                    const unmatched = (report && report.Unmatched) || [];
                    const rows = mapped.map(function (m) {
                        return m.Name + ': id ' + arId(m.OldId) + ' becomes this server\'s ' + m.Name + ' (' + arCount(m.Records || 0, 'record') + ')';
                    }).concat(unmatched.map(function (u) {
                        return (u.Name || 'id ' + arId(u.OldId)) + ': left alone, ' + (AR_REMAP_REASONS[u.Reason] || u.Reason);
                    }));
                    return { rows: rows.length ? rows : ['No unknown user id: nothing to remap.'], ready: mapped.length > 0 && !!report.PlanToken, report: report };
                });
            },
            run: function (report) {
                return arAdmin.remapUsers({ dryRun: false, ifPlan: report.PlanToken });
            }
        }).then(function (answer) {
            return answer ? done('Remapped ' + arCount(((answer.Mapped) || []).length, 'user') + ' (' + arCount(answer.MovedRecords || 0, 'record') + ').') : null;
        });
    }

    function applyPolicy() {
        error.textContent = '';
        result.textContent = '';
        const ids = orphans.map(function (o) {
            return o.UserId;
        });
        return arUsersActionDialog({
            title: 'Apply deleted-user policy',
            intro: 'Access Roles\' policy for a deleted user is Delete: the assignments and original access of these ids go, after a snapshot. A user created again later starts with the default roles. Nothing changes until you confirm.',
            go: 'Delete their records',
            load: function () {
                return Promise.resolve({
                    rows: orphans.length ? orphans.map(function (o) {
                        return (o.LastKnownName || 'id ' + arId(o.UserId)) + ': ' + arCount(o.Records || 0, 'record') + (o.Marked ? ', reported deleted ' + arDate(o.MarkedUtc) : '');
                    }) : ['No unknown user id.'],
                    ready: ids.length > 0,
                    report: ids
                });
            },
            run: function (userIds) {
                return arAdmin.applyPolicy(userIds);
            }
        }).then(function (answer) {
            return answer ? done('The records of ' + arCount(((answer.Users) || []).length, 'unknown user') + ' are deleted (' + arCount(answer.Affected || 0, 'record') + ').') : null;
        });
    }

    function runMaintenance() {
        error.textContent = '';
        return arAdmin.runMaintenance().then(function (answer) {
            result.textContent = (answer && answer.Task ? answer.Task : 'The maintenance') + ' is queued: the Scheduled Tasks page shows it.';
            kit.toast(result.textContent);
        }, function (e) {
            error.textContent = arErrorText(e, 'The maintenance could not be queued.');
        });
    }

    function render(d) {
        const users = (d && d.Users) || {};
        orphans = users.OrphanUsers || [];
        arClear(list);
        list.appendChild(orphans.length
            ? arList(orphans.map(function (o) {
                return (o.LastKnownName || 'No name seen') + ' (id ' + arId(o.UserId) + '): ' + arCount(o.Records || 0, 'record') + (o.Marked ? ', reported deleted ' + arDate(o.MarkedUtc) : '');
            }))
            : arHint('None: every record belongs to a user of this server.'));
        writable = !(d && d.Store && d.Store.ReadOnly);
        remap.disabled = !canAct();
        apply.disabled = !canAct();
    }

    return { element: element, render: render };
}
