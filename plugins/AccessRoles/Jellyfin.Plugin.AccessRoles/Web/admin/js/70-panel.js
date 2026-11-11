// The per-user panel (with the effective view, the drift and leaving management): the user's
// roles, their state, their own lock overrides (Inherit | Lock | Unlock) and library mode (Inherit | All | Only…, with
// Add and Remove lists), drift (Keep as override, Revert this user, Revert all drifted users), and the effective access with where each part
// comes from. Saving previews the whole assignment first, as PUT Users/{id}'s own dry run with the assignment's
// IfUpdatedAt; Unmanage user always asks what their library access becomes, then previews POST Unmanage's dry run.

// Where a lock comes from (AccessSources), in words.
function arLockSourceText(state) {
    const source = String(state.Source || '');
    let text;
    if (source.indexOf('Role:') === 0) {
        text = (state.Locked ? 'Locked by role ' : 'Not locked: role ') + source.substring(5);
    } else if (source === 'UserOverride') {
        text = state.Locked ? 'Locked for this user' : 'Unlocked for this user';
    } else if (source === 'Administrator') {
        text = 'Not locked: administrators are not restricted';
    } else if (source === 'NotManaged') {
        text = 'Not locked: not managed';
    } else if (source === 'Excluded') {
        text = 'Not locked: excluded';
    } else if (source === 'UnknownValue') {
        text = 'Locked: their assignment has a value a newer version wrote';
    } else if (source === 'Default') {
        text = state.Locked ? 'Locked' : 'Not locked';
    } else {
        text = (state.Locked ? 'Locked' : 'Not locked') + (source ? ' (' + source + ')' : '');
    }

    return state.Locked && state.Enforced === false ? text + ' (hidden only)' : text;
}

function arLibrarySourceText(source) {
    source = String(source || '');
    if (source.indexOf('Role:') === 0) {
        return 'granted by role ' + source.substring(5);
    }

    const words = {
        AllLibraries: 'every library (their own mode is All)',
        UserAdd: 'added for this user',
        Original: 'their original access',
        Native: 'their own Jellyfin access (not managed)',
        Administrator: 'administrator'
    };
    return words[source] || source;
}

function arLibraryReasonText(reason) {
    const words = {
        RemovedForUser: 'removed for this user',
        NotGranted: 'not granted by any role',
        Recreated: 'deleted and created again since it was granted: grant it again'
    };
    return words[reason] || String(reason || 'not granted');
}

function arStartingSetText(start) {
    const words = {
        AllLibraries: 'every library (their own mode is All)',
        Empty: 'nothing (their own mode is Only…; the libraries they get are the ones added for them)',
        Roles: 'the libraries of their roles',
        Original: 'their original access (none of their roles manages libraries)'
    };
    return words[start] || String(start || '');
}

function arNativeText(ctx, native) {
    if (!native) {
        return 'nothing recorded';
    }

    return native.EnableAllFolders ? 'every library' : ((native.EnabledFolders || []).map(ctx.libraryName).join(', ') || 'no library');
}

// The effective access: the locks and libraries with their sources. Answers an element.
function arEffectiveView(ctx, effective) {
    const libraries = effective.Libraries || {};
    const lockRows = AR_ALL_LOCKS.filter(function (lock) {
        return effective.Locks && effective.Locks[lock];
    }).map(function (lock) {
        return [arLockLabel(lock), arLockSourceText(effective.Locks[lock])];
    });
    const visible = (libraries.Visible || []).map(function (library) {
        return library.Name + (library.Enabled ? '' : ' (disabled)') + ': ' + ((library.Sources || []).map(arLibrarySourceText).join(', ') || 'visible');
    });
    const hidden = (libraries.Hidden || []).map(function (library) {
        return library.Name + ': ' + arLibraryReasonText(library.Reason);
    });
    const rows = [
        ['Libraries managed', libraries.Managed ? 'Yes' : 'No: Access Roles leaves their library access alone'],
        ['Starting set', arStartingSetText(libraries.StartingSet)],
        ['Their Jellyfin access now', arNativeText(ctx, libraries.Native)]
    ];
    // A result of no library nobody chose isn't written: their access stays.
    const unchosenNone = !!libraries.NoLibraries && !libraries.NoLibrariesConfirmed;
    if (libraries.PendingWrite && !unchosenNone) {
        const pending = libraries.PendingWrite;
        rows.push(['To be written by the library sync', pending.EnableAllFolders ? 'every library' : [
            pending.Add.length ? 'adds ' + pending.Add.map(ctx.libraryName).join(', ') : '',
            pending.Remove.length ? 'removes ' + pending.Remove.map(ctx.libraryName).join(', ') : ''
        ].filter(Boolean).join('; ') || 'their library access']);
    }

    if (libraries.PendingRelease) {
        rows.push(['Leaving management', (arModeLabel(AR_CHOICES, libraries.PendingRelease.Choice) || 'chosen') + ', written by the next library sync']);
    }

    if ((libraries.MissingReferences || []).length) {
        rows.push(['Libraries that are gone', libraries.MissingReferences.map(ctx.libraryName).join(', ')]);
    }

    return arEl('div', { class: arClass('effective'), id: 'AccessRolesEffective' }, [
        arHeading(4, 'Effective access'),
        arEl('p', { class: arClass('effective-summary'), text: libraries.NoLibraries
            ? (unchosenNone ? 'The roles would leave them no library, so the library sync leaves their access as it is.' : 'They see no library (as chosen).')
            : 'They see ' + (libraries.EnableAllFolders ? 'every library' : visible.length + (visible.length === 1 ? ' library' : ' libraries')) + '.' }),
        arTable(rows),
        arHeading(5, 'Settings'),
        lockRows.length ? arTable(lockRows) : arHint('No setting is locked.'),
        arHeading(5, 'Libraries they see'),
        visible.length ? arList(visible) : arHint('None.'),
        hidden.length ? arHeading(5, 'Libraries hidden from them') : null,
        hidden.length ? arList(hidden) : null
    ]);
}

// Why the library sync's dry run leaves a user out, in words (SyncReasons).
const AR_SYNC_SKIPS = {
    NoLibraries: 'they would have no libraries, and nobody confirmed that',
    NoFullAdministrator: 'no enabled administrator would see every library afterwards',
    WriteAborted: 'their access changed while it was being written; the next sync tries again',
    LockTimeout: 'their access was busy; the next sync tries again',
    UserNotFound: 'the user is gone',
    WriteFailed: 'writing their access failed; the server log has the details',
    ReportOnly: 'reported only (drift policy ReportOnly)',
    NativeChanged: 'their access changed meanwhile; the next sync decides'
};

// Whether a row of the sync's report writes the user's access (SyncPlanToken's writes).
function arSyncWrites(row) {
    return row.Action === 'Write' || row.Action === 'Release' || (row.Action === 'Drift' && !!row.Target);
}

// Revert: the library sync writes the rules' access again, for every user
// whose access drifted, or with user ({ UserId, Name }) for that user only (POST Sync?userId=). Its dry run
// says whom it writes and whom it leaves out and why; "Write anyway" is confirmNoFullAdmin and runs the dry run again;
// Revert sends the shown dry run's PlanToken as ifPlan, so a sync that would write anyone or anything else is refused
// (409 Changed, naming only who changed) and writes nothing. Answers a promise of the run's report, or null.
function arRevertDrift(ctx, user) {
    return new Promise(function (resolve) {
        let seq = 0;
        let shown = null;
        let running = false;
        let finished = false;
        const dialog = arDialog(user ? 'Revert the library access of ' + user.Name : 'Revert all drifted users', { modal: true });
        const result = arEl('div', { class: arClass('preview'), id: 'AccessRolesRevertPreview', 'aria-live': 'polite' });
        const adminBox = arEl('div', { hidden: true });
        const confirmAdmin = arCheckbox('Write anyway: no enabled administrator would see every library afterwards', {
            onChange: function () {
                load();
            }
        });
        confirmAdmin.input.id = 'AccessRolesRevertConfirmNoFullAdmin';
        adminBox.appendChild(confirmAdmin.element);
        const error = arErrorLine();
        const staleText = arEl('p', { class: arClass('stale-text') });
        const again = arButton('Look again', function () {
            arBusy(again, load);
        });
        const staleBox = arEl('div', { class: arClass('stale'), role: 'alert', hidden: true }, [staleText, arActions([again])]);
        const cancel = arButton('Cancel', function () {
            dialog.close();
        });
        const go = arButton('Revert', function () {
            arBusy(go, submit, canRevert);
        }, { variant: 'primary', disabled: true });
        go.id = 'AccessRolesRevertConfirm';
        kit.append(dialog.body,
            user
                ? arEl('p', null, ['The library sync writes the library access the roles give ', arName(user.Name), ', and nobody else\'s. Nothing is written until you confirm.'])
                : arEl('p', { text: 'The library sync writes the library access the roles give, for every user whose access was changed outside Access Roles (not only this one), and anything else it has waiting. Nothing is written until you confirm.' }),
            result, adminBox, error, staleBox, arActions([cancel, go]));
        dialog.onClose(function () {
            seq++;
            if (!finished) {
                finished = true;
                resolve(null);
            }
        });

        function canRevert() {
            return !!shown && !running && !shown.dry.Paused && shown.writes.length > 0;
        }

        function render(dry, query) {
            const rows = dry.Users || [];
            const writes = rows.filter(arSyncWrites);
            const skipped = rows.filter(function (row) {
                return row.Action === 'Skip';
            });
            shown = { dry: dry, query: query, writes: writes };
            arClear(result);
            if (dry.Paused) {
                result.appendChild(arBanner('The library sync is paused (' + dry.Paused + '): nothing would be written.'));
            }

            result.appendChild(arEl('p', { class: arClass('preview-summary'), text: writes.length
                ? 'Reverting writes the library access of ' + writes.length + (writes.length === 1 ? ' user:' : ' users:')
                : 'The library sync has nothing to write.' }));
            if (writes.length) {
                result.appendChild(arList(writes.map(function (row) {
                    return arEl('span', null, [arName(row.Name), ': added ' + (arNames(row.Added) || 'nothing') + '; removed ' + (arNames(row.Removed) || 'nothing')]);
                })));
            }

            if (skipped.length) {
                result.appendChild(arHeading(4, 'Left as they are'));
                result.appendChild(arList(skipped.map(function (row) {
                    return arEl('span', null, [arName(row.Name), ': ' + (AR_SYNC_SKIPS[row.Reason] || 'not written (' + (row.Reason || 'skipped') + ')')]);
                })));
            }

            arShow(adminBox, confirmAdmin.input.checked || skipped.some(function (row) {
                return row.Reason === 'NoFullAdministrator';
            }));
            go.disabled = !canRevert();
        }

        function load() {
            const mine = ++seq;
            shown = null;
            go.disabled = true;
            error.textContent = '';
            arShow(staleBox, false);
            const query = { dryRun: true, reapplyDrift: true };
            if (user) {
                query.userId = arId(user.UserId);
            }

            if (confirmAdmin.input.checked) {
                query.confirmNoFullAdmin = true;
            }

            return arAdmin.sync(query).then(function (dry) {
                if (mine === seq) {
                    render(dry || {}, query);
                }
            }, function (e) {
                if (mine === seq) {
                    arClear(result);
                    error.textContent = arErrorText(e, 'The library sync could not be previewed.');
                }
            });
        }

        // The real run: the shown dry run's options and its PlanToken.
        function submit() {
            if (!canRevert()) {
                return null;
            }

            const sent = shown;
            const query = { reapplyDrift: true, ifPlan: sent.dry.PlanToken };
            if (sent.query.userId) {
                query.userId = sent.query.userId;
            }

            if (sent.query.confirmNoFullAdmin) {
                query.confirmNoFullAdmin = true;
            }

            running = true;
            return arAdmin.sync(query).then(function (done) {
                running = false;
                finished = true;
                dialog.close();
                kit.toast('Library access written for ' + (done.Written || 0) + ((done.Written || 0) === 1 ? ' user.' : ' users.'));
                resolve(done);
            }, function (e) {
                running = false;
                if (arChanged(e)) {
                    shown = null;
                    arClear(result);
                    const users = (e.problem && e.problem.Users) || [];
                    staleText.textContent = 'What the library sync would write changed after this preview' + (users.length ? ' (' + arNames(users) + ')' : '')
                        + ', so nothing was written. Look again before you revert.';
                    arShow(staleBox, true);
                    go.disabled = true;
                    again.focus();
                    return;
                }

                error.textContent = arErrorText(e, 'The library sync could not be run.');
            });
        }

        load();
    });
}

function arUserPanel(ctx, userId) {
    const listed = ctx.users.filter(function (u) {
        return arSameId(u.UserId, userId);
    })[0];
    const focusKey = 'user-' + arId(userId);
    const dialog = arDialog(listed ? listed.Name : 'User', {
        modal: true,
        returnFocus: function () {
            return ctx.root.querySelector('[data-ar-focus="' + focusKey + '"]');
        }
    });
    dialog.panel.classList.add(arClass('wide'));
    dialog.panel.setAttribute('data-user-id', arId(userId));
    const status = arEl('p', { text: 'Loading…' });
    dialog.body.appendChild(status);

    function load() {
        return arAdmin.effective(userId).then(render, function (e) {
            arClear(dialog.body);
            dialog.body.appendChild(arBanner(arErrorText(e, 'The user\'s access couldn\'t be loaded.'), 'error'));
            dialog.body.appendChild(arActions([arButton('Close', function () {
                dialog.close();
            })]));
        });
    }

    function render(effective) {
        // The user's assignment as last read: its IfUpdatedAt goes with the request, and the admin's edit is put over it
        // (after a 409 Changed, "Reload and keep my changes").
        let latest = effective;
        const active = document.activeElement;
        const keep = active && dialog.panel.contains(active) && active.getAttribute ? active.getAttribute('data-ar-focus') : null;
        arClear(dialog.body);
        const stored = effective.Assignment || null;
        const storedLibraries = stored ? stored.Libraries : null;
        const flags = effective.NativeFlags || {};
        const readOnly = ctx.readOnly;

        // Notes.
        const notes = [];
        if (effective.IsAdministrator) {
            notes.push(arHint('An administrator: the locks of their roles don\'t apply, the libraries do.'));
        }

        if (flags.IsDisabled) {
            notes.push(arHint('This user is disabled.'));
        }

        if (!flags.IsHidden) {
            notes.push(arHint('Jellyfin shows this user on the login screen ("Hide this user from login screens" on their Jellyfin user page).'));
        }

        if ((effective.UnknownValues || []).length) {
            notes.push(arBanner('Values a newer version wrote are kept as they are: ' + effective.UnknownValues.join(', ')));
        }

        (effective.Warnings || []).forEach(function (warning) {
            notes.push(arBanner(warning));
        });

        // Roles.
        const roleChecks = ctx.roles.map(function (role) {
            const check = arCheckbox(role.Name, { checked: arHasId(stored ? stored.RoleIds : [], role.Id), disabled: readOnly });
            check.input.setAttribute('data-role-id', arId(role.Id));
            check.input.setAttribute('data-ar-focus', 'role-' + arId(role.Id));
            return { role: role, check: check };
        });

        // State.
        const storedState = stored ? stored.State : 'Managed';
        const state = arRadios('State', [
            { value: 'Managed', text: 'Managed: their roles and overrides apply' },
            { value: 'Excluded', text: 'Excluded: Access Roles applies nothing to them for now, and keeps what is set here' }
        ], storedState === 'Managed' || storedState === 'Excluded' ? storedState : null, null);
        const stateNote = effective.State === 'CannotManage'
            ? arHint('Access Roles can\'t manage this user right now; the warnings above say why.')
            : (stored && stored.State === null ? arHint('Their state was set by a newer version; it stays unless you pick one.') : null);

        // Lock overrides.
        const overrides = AR_ALL_LOCKS.map(function (lock) {
            const current = effective.Locks && effective.Locks[lock];
            const value = stored && stored.LockOverrides[lock] ? stored.LockOverrides[lock] : 'Inherit';
            const select = arSelect(arLockLabel(lock), [
                { value: 'Inherit', text: 'As their roles say' },
                { value: 'Lock', text: 'Lock for this user' },
                { value: 'Unlock', text: 'Unlock for this user' }
            ], value, null);
            select.select.disabled = readOnly;
            select.select.setAttribute('data-lock', lock);
            select.select.setAttribute('data-ar-focus', 'lock-' + lock);
            if (current) {
                select.element.appendChild(arEl('span', { class: arClass('check-hint'), text: 'Now: ' + arLockSourceText(current) }));
            }

            return { lock: lock, select: select };
        });

        // Libraries.
        const unknownMode = !!stored && (storedLibraries.Mode === null || storedLibraries.Mode === undefined);
        const modes = unknownMode ? AR_USER_MODES.concat([{ value: '', text: 'Set by a newer version (kept)' }]) : AR_USER_MODES;
        const mode = arSelect('Their own library mode', modes, unknownMode ? '' : (storedLibraries ? storedLibraries.Mode : 'Inherit'), function () {
            updateLibraries();
        });
        mode.select.disabled = readOnly;
        mode.select.id = 'AccessRolesUserMode';
        const addPicker = arLibraryPicker(ctx, 'Also grant these libraries (Add)', storedLibraries ? storedLibraries.Add : []);
        const removePicker = arLibraryPicker(ctx, 'Always hide these libraries (Remove, beats every grant)', storedLibraries ? storedLibraries.Remove : []);
        addPicker.element.setAttribute('data-list', 'Add');
        removePicker.element.setAttribute('data-list', 'Remove');
        const none = arCheckbox('I really want this user to see no libraries', { checked: !!(storedLibraries && storedLibraries.ConfirmNoLibraries), disabled: readOnly });
        none.input.id = 'AccessRolesConfirmNoLibraries';
        const noneBox = arEl('div', null, none.element);
        const modeHint = arHint('');
        addPicker.onChange(updateLibraries);
        function updateLibraries() {
            const value = mode.select.value;
            arShow(addPicker.element, value !== 'All');
            arShow(removePicker.element, value !== 'Only');
            arShow(noneBox, value === 'Only' && addPicker.selected().length === 0);
            modeHint.textContent = value === 'All'
                ? 'Every library, and every library added later, apart from the ones hidden below.'
                : (value === 'Only' ? 'Only the libraries ticked below, whatever their roles give.' : (value === 'Inherit' ? 'The libraries of their roles, with the changes below.' : ''));
        }

        // No library by accident: its own state, with no Keep or Revert, as the library
        // sync leaves the access alone and it is never drift.
        const unchosenNone = !!effective.Libraries && !!effective.Libraries.NoLibraries && !effective.Libraries.NoLibrariesConfirmed;
        const noLibraries = unchosenNone
            ? arEl('div', { class: arClass('drift'), id: 'AccessRolesNoLibraries' }, [
                arHeading(4, 'No libraries'),
                arEl('p', { text: 'Their roles and overrides would leave them no library, and nobody chose that. The library sync doesn\'t write that by accident: their access stays as it is (' + arNativeText(ctx, effective.Libraries.Native) + ') until a role or an Add gives them a library, or you choose no library for them (Only… with nothing, and the box ticked). It does write it once none of the libraries they open is left.' })
            ])
            : null;

        // Drift.
        let drift = null;
        if (effective.Libraries && effective.Libraries.Drift && !unchosenNone) {
            const native = effective.Libraries.Native;
            drift = arEl('div', { class: arClass('drift'), id: 'AccessRolesDrift' }, [
                arHeading(4, 'Changed outside Access Roles'),
                arEl('p', { text: 'Their library access was changed outside Access Roles (on their Jellyfin user page, or by another tool). Now: ' + arNativeText(ctx, native) + '. Access Roles last wrote: ' + arNativeText(ctx, effective.Libraries.LastWrite) + '.' }),
                effective.Libraries.NoLibrariesConfirmed
                    ? arEl('p', { text: 'They were chosen to see no library (Only… with nothing, and the box ticked), so the library sync manages that like any other access: a library given to them outside Access Roles is drift.' })
                    : null
            ]);
            if (ctx.driftPolicy === 'ReportOnly') {
                const keepButton = arButton('Keep as override', function () {
                    arBusy(keepButton, function () {
                        return save(native.EnableAllFolders
                            ? { Libraries: { Mode: 'All', Add: [], Remove: [], ConfirmNoLibraries: false } }
                            : { Libraries: { Mode: 'Only', Add: (native.EnabledFolders || []).slice(), Remove: [], ConfirmNoLibraries: (native.EnabledFolders || []).length === 0 } },
                        'Keep their current library access as their own');
                    });
                }, { disabled: readOnly });
                keepButton.id = 'AccessRolesDriftKeep';
                keepButton.setAttribute('data-ar-focus', 'drift-keep');
                const revert = function (button, who) {
                    arBusy(button, function () {
                        return arRevertDrift(ctx, who).then(function (done) {
                            if (done) {
                                ctx.refresh();
                                return load();
                            }

                            return null;
                        }, function (e) {
                            error.textContent = arErrorText(e);
                        });
                    });
                };
                const revertUser = arButton('Revert this user…', function () {
                    revert(revertUser, { UserId: userId, Name: effective.UserName || (listed && listed.Name) || '' });
                }, { disabled: readOnly });
                revertUser.id = 'AccessRolesDriftRevertUser';
                revertUser.setAttribute('data-ar-focus', 'drift-revert-user');
                const revertButton = arButton('Revert all drifted users…', function () {
                    revert(revertButton, null);
                }, { disabled: readOnly });
                revertButton.id = 'AccessRolesDriftRevert';
                revertButton.setAttribute('data-ar-focus', 'drift-revert');
                kit.append(drift,
                    arHint('Keep as override makes their current access their own library mode. Revert this user has the library sync write what the roles give them, and nobody else; Revert all drifted users does it for every user whose access was changed outside Access Roles.'),
                    arActions([keepButton, revertUser, revertButton]));
            } else {
                drift.appendChild(arHint('The library sync writes the roles\' access again (drift policy ' + (ctx.driftPolicy || 'Reapply') + ').'));
            }
        }

        const error = arErrorLine();
        const close = arButton('Close', function () {
            dialog.close();
        });
        close.setAttribute('data-ar-focus', 'close');
        const unmanage = arButton('Unmanage user…', function () {
            arBusy(unmanage, unmanageUser);
        }, { variant: 'danger', disabled: readOnly });
        unmanage.id = 'AccessRolesUnmanage';
        unmanage.setAttribute('data-ar-focus', 'unmanage');
        const saveButton = arButton('Save', function () {
            arBusy(saveButton, function () {
                return save(null, 'Save ' + effective.UserName);
            });
        }, { variant: 'primary', disabled: readOnly, title: readOnly ? 'Access Roles\' data is read-only.' : null });
        saveButton.id = 'AccessRolesUserSave';
        saveButton.setAttribute('data-ar-focus', 'save');

        kit.append(dialog.body,
            notes.length ? arEl('div', { class: arClass('notes') }, notes) : null,
            arHeading(4, 'Roles'),
            ctx.roles.length ? arEl('div', { class: arClass('checks'), id: 'AccessRolesUserRoles' }, roleChecks.map(function (c) {
                return c.check.element;
            })) : arHint('There are no roles yet.'),
            arHeading(4, 'State'),
            state.element,
            stateNote,
            arHeading(4, 'Settings for this user'),
            arEl('div', { class: arClass('overrides'), id: 'AccessRolesUserOverrides' }, overrides.map(function (o) {
                return o.select.element;
            })),
            arHeading(4, 'Libraries for this user'),
            mode.element,
            modeHint,
            addPicker.element,
            removePicker.element,
            noneBox,
            arHint('For a managed user, Access Roles writes the Library access of their Jellyfin user page; a change made there shows here as changed outside Access Roles. For a user Access Roles doesn\'t otherwise manage, per-user library restrictions can also stay on the Library access tab of their Jellyfin user page.'),
            noLibraries,
            drift,
            arEffectiveView(ctx, effective),
            error,
            arActions(stored ? [close, unmanage, saveButton] : [close, saveButton]));
        updateLibraries();
        if (keep) {
            const again = dialog.panel.querySelector('[data-ar-focus="' + keep + '"]');
            if (again && !again.disabled) {
                again.focus();
            }
        }

        function request(changes) {
            const lockOverrides = {};
            overrides.forEach(function (o) {
                if (o.select.select.value === 'Lock' || o.select.select.value === 'Unlock') {
                    lockOverrides[o.lock] = o.select.select.value;
                }
            });
            // A list the mode hides isn't sent: All shows no Add, Only no Remove.
            const libraryMode = mode.select.value || null;
            const add = libraryMode === 'All' ? [] : addPicker.selected();
            const form = {
                RoleIds: roleChecks.filter(function (c) {
                    return c.check.input.checked;
                }).map(function (c) {
                    return c.role.Id;
                }),
                LockOverrides: lockOverrides,
                Libraries: {
                    Mode: libraryMode,
                    Add: add,
                    Remove: libraryMode === 'Only' ? [] : removePicker.selected(),
                    ConfirmNoLibraries: libraryMode === 'Only' && add.length === 0 && none.input.checked
                },
                State: state.value()
            };
            if (changes) {
                Object.assign(form.Libraries, changes.Libraries);
            }

            return arRebaseAssignment(effective, arAssignmentRequest(effective, form), latest);
        }

        // The assignment read again, after a 409 Changed.
        function readAgain(managed) {
            return arAdmin.effective(userId).then(function (now) {
                if (managed && !now.Assignment) {
                    throw new Error(effective.UserName + ' isn\'t managed any more.');
                }

                latest = now;
                ctx.refresh();
            });
        }

        function saved(change, verb) {
            kit.toast(verb + ' ' + effective.UserName + '.');
            ctx.refresh();
            if (change && change.Effective) {
                render(change.Effective);
                return null;
            }

            return load();
        }

        function save(changes, title) {
            error.textContent = '';
            const body = request(changes);
            const both = body.Libraries.Add.filter(function (id) {
                return arHasId(body.Libraries.Remove, id);
            });
            if (both.length) {
                error.textContent = 'A library can\'t be both added and hidden: ' + both.map(ctx.libraryName).join(', ') + '.';
                return null;
            }

            return arReview({
                title: title,
                libraryName: ctx.libraryName,
                request: function () {
                    return request(changes);
                },
                send: function (sent, options) {
                    return arAdmin.updateUser(userId, sent, arWriteQuery(options));
                },
                reload: function () {
                    return readAgain(false);
                }
            }).then(function (change) {
                return change ? saved(change, 'Saved') : null;
            });
        }

        // Unmanage user: the assignment goes, and their library access becomes what the admin chooses.
        function unmanageUser() {
            error.textContent = '';
            return arReview({
                title: 'Unmanage ' + effective.UserName,
                intro: 'Their roles, overrides and library mode go. Choose what their library access becomes.',
                alwaysChoose: true,
                libraryName: ctx.libraryName,
                saveLabel: 'Unmanage',
                request: function () {
                    return { IfUpdatedAt: latest.UpdatedUtc };
                },
                send: function (sent, options) {
                    return arAdmin.unmanage(userId, { Choice: options.onUnmanage, IfUpdatedAt: sent.IfUpdatedAt }, arWriteQuery({ confirmNoFullAdmin: options.confirmNoFullAdmin, dryRun: options.dryRun, ifPreview: options.ifPreview }));
                },
                reload: function () {
                    return readAgain(true);
                }
            }).then(function (change) {
                return change ? saved(change, 'Unmanaged') : null;
            });
        }
    }

    load();
    return dialog;
}
