// The preview of every change and the leave-management choice. The preview is the server's own
// dry run of the very request Save sends (?dryRun=true): a dialog over the editor that says, per
// user it affects, the sections newly locked or unlocked, the libraries added or removed, whether the library sync will
// write their access, and the blocking warnings: no libraries, leaving management (Restore, Keep or Grant all libraries;
// the choice goes with the request as onUnmanage), no enabled administrator left with every library (confirmNoFullAdmin).
// A dry run the save would refuse answers its refusal with the same preview (ProblemDetails' Preview). Each choice runs the
// dry run again, only the latest answer shows, and Save sends exactly the request and options of the answer on
// screen. The request carries the tokens of what the admin read (IfUpdatedAt, ifMembers), and Save the shown answer's
// PreviewToken as ifPreview (it covers what the change does, a role it adds or reassigns to included): a
// 409 Changed says so in plain words, Save stays off, and "Reload and keep my changes" reads it again and puts the
// admin's edit back over it.

// What a change does to one user, in sentences.
function arPreviewRow(row, libraryName) {
    const lines = [];
    const add = function (text, kind) {
        lines.push(arEl('p', { class: arClass('preview-line') + (kind ? ' ' + arClass('preview-' + kind) : ''), text: text }));
    };
    if (row.StateBefore !== row.StateAfter) {
        add((AR_STATES[row.StateBefore] || row.StateBefore) + ' → ' + (AR_STATES[row.StateAfter] || row.StateAfter));
    }

    if ((row.LocksAdded || []).length) {
        add('Newly locked: ' + row.LocksAdded.map(arLockLabel).join(', '));
    }

    if ((row.LocksRemoved || []).length) {
        add('Unlocked: ' + row.LocksRemoved.map(arLockLabel).join(', '));
    }

    if ((row.LibrariesAdded || []).length) {
        add('Libraries added: ' + arNames(row.LibrariesAdded));
    }

    if ((row.LibrariesRemoved || []).length) {
        add('Libraries removed: ' + arNames(row.LibrariesRemoved));
    }

    if (row.NoLibraries) {
        add(row.Name + ' would have no libraries.' + (row.NoLibrariesRefused
            ? ' This is refused: grant a library, or set the user\'s own library mode to Only… with nothing picked and tick "I really want this user to see no libraries" in their panel.'
            : ' You confirmed this.'), row.NoLibrariesRefused ? 'blocking' : 'warning');
    }

    const leaving = row.LeavesManagement;
    if (leaving) {
        let text = row.Name + ' is no longer managed for libraries.';
        if (leaving.Choice === 'Keep') {
            text += ' Library access stays frozen at: ' + ((leaving.FrozenAt || []).join(', ') || 'no libraries') + '.';
        } else if (leaving.Choice === 'GrantAll') {
            text += ' They get every library, future ones too.';
        } else if (leaving.Choice === 'Restore' && leaving.After) {
            text += leaving.After.EnableAllFolders
                ? ' Their original access is restored: every library.'
                : ' Their original access is restored: ' + ((leaving.After.EnabledFolders || []).map(libraryName).join(', ') || 'no libraries') + '.';
        }

        if (leaving.Note) {
            text += ' ' + leaving.Note;
        }

        add(text, leaving.Choice ? null : 'warning');
    }

    if (row.PolicyWrite) {
        add('The library sync will write their library access.');
    }

    (row.Warnings || []).forEach(function (warning) {
        add(warning, 'warning');
    });
    if (lines.length === 0) {
        add('No change to their access.');
    }

    return arEl('li', { class: arClass('preview-user') }, [
        arEl('p', { class: arClass('preview-name') }, [arName(row.Name), row.IsAdministrator ? ' (administrator)' : null])
    ].concat(lines));
}

// Opens the preview dialog. opts: {
//   title, intro (text, or a sentence's children: names isolated with arName), saveLabel, libraryName(id),
//   alwaysChoose: the leave-management choice is required whoever it concerns (Unmanage); no dry run until it is made,
//   request(): the request as the admin's edit and the tokens read so far make it (called for each dry run, and kept
//     with its answer: Save sends that same request),
//   send(request, { dryRun, onUnmanage, confirmNoFullAdmin, ifPreview }) → Promise<change>: the write (a dry run, or
//     the save with the shown dry run's PreviewToken as ifPreview),
//   reload() → Promise (optional): reads what changed again and puts the admin's edit back over it, after a 409 Changed,
//   empty(change) → text or null (optional): a dry run that changes nothing, with the reason Save stays off }.
// Answers a promise of the saved change, or null when cancelled.
function arReview(opts) {
    return new Promise(function (resolve) {
        let choice = null;
        // The answer on screen: { preview, request, options, refused, empty }; null while a dry run runs or after a 409.
        let shown = null;
        // The number of the latest dry run: an earlier one's late answer is dropped.
        let seq = 0;
        let saving = false;
        let done = false;
        const dialog = arDialog(opts.title, { modal: true });
        const intro = !opts.intro ? null : Array.isArray(opts.intro) ? arEl('p', {}, opts.intro) : arEl('p', { text: opts.intro });
        const result = arEl('div', { class: arClass('preview'), 'aria-live': 'polite' });
        const choiceBox = arEl('div', { class: arClass('preview-choice'), hidden: true });
        const radios = arRadios('What should their library access become?', AR_CHOICES, null, function (value) {
            choice = value;
            load(true);
        });
        choiceBox.appendChild(radios.element);
        const adminBox = arEl('div', { hidden: true });
        const confirmAdmin = arCheckbox('Save anyway: no enabled administrator would see every library afterwards', {
            onChange: function () {
                // Another request: its own dry run before Save.
                load(true);
            }
        });
        confirmAdmin.input.setAttribute('data-ar-focus', 'confirm-no-full-admin');
        adminBox.appendChild(confirmAdmin.element);
        const error = arErrorLine();
        const failure = arErrorLine();
        const staleText = arEl('p', { class: arClass('stale-text') });
        const reload = opts.reload ? arButton('Reload and keep my changes', function () {
            arBusy(reload, reloadAndKeep);
        }) : null;
        if (reload) {
            reload.setAttribute('data-ar-focus', 'reload');
        }

        const staleBox = arEl('div', { class: arClass('stale'), role: 'alert', hidden: true }, [staleText, reload ? arActions([reload]) : null]);
        const cancel = arButton('Cancel', function () {
            dialog.close();
        });
        const save = arButton(opts.saveLabel || 'Save', function () {
            arBusy(save, submit, canSave);
        }, { variant: 'primary', disabled: true });
        save.setAttribute('data-ar-focus', 'review-save');
        kit.append(dialog.body, intro, result, choiceBox, adminBox, error, staleBox, failure, arActions([cancel, save]));
        dialog.onClose(function () {
            seq++;
            if (!done) {
                done = true;
                resolve(null);
            }
        });

        function blockedReasons(p, options) {
            const reasons = [];
            if ((p.UnmanageChoiceRequired || []).length > 0) {
                reasons.push('Choose what the library access of the users who are no longer managed becomes.');
            }

            (p.Users || []).forEach(function (row) {
                if (row.NoLibrariesRefused) {
                    reasons.push(row.Name + ' would have no libraries.');
                }
            });
            if ((p.NoLedgerEntry || []).length) {
                reasons.push('No original access is recorded for ' + arNames(p.NoLedgerEntry) + ', so it can\'t be restored: choose Keep current access or Grant all libraries.');
            }

            if ((p.DefaultRole || []).length) {
                reasons.push(arNames(p.DefaultRole) + ' is a default role for new users: take it out of the default roles first.');
            }

            if (p.NoFullAdministrator && !options.confirmNoFullAdmin) {
                reasons.push('No enabled administrator would see every library afterwards: tick the box to save anyway.');
            }

            return reasons;
        }

        // Save is on only for the answer on screen, when the server would save it as it is.
        function canSave() {
            return !!shown && !saving && !shown.refused && !shown.empty && blockedReasons(shown.preview, shown.options).length === 0;
        }

        function update() {
            const reasons = shown ? blockedReasons(shown.preview, shown.options) : [];
            if (shown && shown.refused && reasons.length === 0) {
                reasons.push(arErrorText(shown.refused, 'The server would refuse this change.'));
            }

            if (shown && shown.empty) {
                reasons.push(shown.empty);
            }

            error.textContent = reasons.join(' ');
            save.disabled = !canSave();
        }

        function render(p) {
            arClear(result);
            const rows = p.Users || [];
            result.appendChild(arEl('p', {
                class: arClass('preview-summary'),
                text: rows.length === 0
                    ? 'This changes nobody\'s access.'
                    : 'This changes the access of ' + rows.length + (rows.length === 1 ? ' user:' : ' users:')
            }));
            if (rows.length) {
                result.appendChild(arEl('ul', { class: arClass('preview-users') }, rows.map(function (row) {
                    return arPreviewRow(row, opts.libraryName || String);
                })));
            }

            const leaving = (p.UnmanageChoiceRequired || []).length > 0 || rows.some(function (row) {
                return !!row.LeavesManagement;
            });
            arShow(choiceBox, leaving || !!opts.alwaysChoose);
            arShow(adminBox, !!p.NoFullAdministrator || confirmAdmin.input.checked);
        }

        // A 409 Changed (from the dry run or from Save): what the admin read is out of date. Nothing was saved, Save stays
        // off for the stale tokens, and the way on is to read it again.
        function stale(e) {
            shown = null;
            arClear(result);
            error.textContent = '';
            staleText.textContent = arChangedText(e) + (reload
                ? ' Reload to read it again with your changes put back over it, and check the new preview before you save.'
                : ' Close this and open it again to see the current state.');
            arShow(staleBox, true);
            update();
            const active = document.activeElement;
            if (reload && (!active || active === document.body || active === save || !dialog.panel.contains(active))) {
                reload.focus();
            }
        }

        // The dry run of the request as it is now. fresh: an action of the admin's, which clears a save's failure.
        function load(fresh) {
            const mine = ++seq;
            shown = null;
            save.disabled = true;
            error.textContent = '';
            if (fresh) {
                failure.textContent = '';
            }

            arShow(staleBox, false);
            if (opts.alwaysChoose && !choice) {
                arClear(result);
                arShow(choiceBox, true);
                result.appendChild(arHint('Choose what their library access becomes; the preview follows.'));
                return Promise.resolve(null);
            }

            const options = { dryRun: true, onUnmanage: choice, confirmNoFullAdmin: confirmAdmin.input.checked };
            result.setAttribute('aria-busy', 'true');
            let request;
            return Promise.resolve().then(function () {
                request = opts.request();
                return opts.send(request, options);
            }).then(function (change) {
                if (mine !== seq) {
                    return;
                }

                const p = (change && change.Preview) || {};
                shown = { preview: p, request: request, options: options, refused: null, empty: opts.empty ? opts.empty(change) : null };
                render(p);
                update();
            }, function (e) {
                if (mine !== seq) {
                    return;
                }

                if (arChanged(e)) {
                    stale(e);
                } else if (e && e.problem && e.problem.Preview) {
                    // A dry run the save would refuse: what it does, and why it can't be saved yet.
                    shown = { preview: e.problem.Preview, request: request, options: options, refused: e, empty: null };
                    render(e.problem.Preview);
                    update();
                } else {
                    arClear(result);
                    error.textContent = arErrorText(e, 'The preview could not be made.');
                }
            }).then(function () {
                if (mine === seq) {
                    result.removeAttribute('aria-busy');
                }
            });
        }

        function reloadAndKeep() {
            failure.textContent = '';
            return Promise.resolve().then(opts.reload).then(function () {
                return load(true);
            }, function (e) {
                staleText.textContent = arErrorText(e, 'It could not be read again.') + ' Close this and open it again.';
            });
        }

        // The save: the request and options of the answer on screen, nothing re-read.
        function submit() {
            if (!canSave()) {
                return null;
            }

            const sent = shown;
            saving = true;
            failure.textContent = '';
            return opts.send(sent.request, { dryRun: false, onUnmanage: sent.options.onUnmanage, confirmNoFullAdmin: sent.options.confirmNoFullAdmin, ifPreview: sent.preview.PreviewToken }).then(function (change) {
                saving = false;
                done = true;
                dialog.close();
                resolve(change);
            }, function (e) {
                saving = false;
                if (arChanged(e)) {
                    stale(e);
                    return null;
                }

                failure.textContent = arErrorText(e, 'The change could not be saved.');
                if (e && e.problem && e.problem.Code) {
                    // A refusal the dry run didn't foresee (something changed that no token covers): the dry run again
                    // says what the change does now.
                    return load(false);
                }

                update();
                return null;
            });
        }

        load(true);
    });
}
