// The Settings tab: GET /AccessRoles/Admin/Settings as a form. Review sends only the settings the
// admin changed as PUT Settings?dryRun=true, whose answer lists each change (before → after), what it means (Warnings)
// and the configuration keys added that carry secrets; Save sends the same body with the dry run's PreviewToken as
// ifPreview (409 Changed when a setting changed meanwhile: nothing saved, look again), and confirmSecretKeys=true only once
// the admin ticked the box that names those keys. LibraryHardening Strict arrives in 1.1: offered only when it is stored.

const AR_SETTINGS_GROUPS = [
    {
        title: 'Enforcement',
        items: [
            { name: 'EnforcementEnabled', kind: 'bool', label: 'Enforce the roles', hint: 'Off: nothing is enforced and the library sync stops; restrictions are only stored, and the web client only shows the administrators\' banner. The native preference access the optional mirror turned off and the library access already written stay as they are. From the next server start the plugin is inert: only this page loads.' },
            { name: 'LibrarySyncEnabled', kind: 'bool', label: 'Write library roles to users\' library access', hint: 'Off: library roles are stored and previewed but never written.' },
            { name: 'ReconcileIntervalMinutes', kind: 'number', min: 1, max: 60, label: 'Check every user\'s library access every (minutes, 1 to 60)', hint: 'A backstop: library changes are synced at once.' },
            {
                name: 'DriftPolicy',
                kind: 'choice',
                label: 'When a managed user\'s library access is changed outside Access Roles',
                choices: [
                    { value: 'Reapply', text: 'Write the roles\' access again (Reapply)' },
                    { value: 'ReportOnly', text: 'Keep it and report it on the Users tab (ReportOnly)' }
                ]
            }
        ]
    },
    {
        title: 'Server-wide (every user who isn\'t an administrator)',
        items: [
            {
                name: 'LibraryHardening',
                kind: 'choice',
                label: 'Library leak hardening',
                choices: [
                    { value: 'Standard', text: 'Standard: search, people, suggestions and the other leaks show only the libraries a user can open' },
                    { value: 'Off', text: 'Off' }
                ]
            },
            {
                name: 'NonAdminConfigurationReads',
                kind: 'choice',
                label: 'Configuration reads',
                choices: [
                    { value: 'AllowList', text: 'Only the keys listed below (AllowList)' },
                    { value: 'Redact', text: 'Every key, with its known secrets stripped (Redact)' }
                ]
            },
            { name: 'ConfigurationReadAllowList', kind: 'list', label: 'Configuration keys non-admins may read (comma-separated)', hint: 'Their known secrets are stripped all the same. network, livetv, database and subtitles carry secrets: adding one asks you to confirm. encoding is the one the web player reads.' },
            { name: 'BlockNonAdminRestart', kind: 'bool', label: 'Only administrators can restart the server', hint: 'Off: anyone who can reach the server can restart it, without signing in, as on a stock 10.11.5. Shutting down is for administrators either way: Jellyfin itself allows only them.' },
            { name: 'TrimPublicUsers', kind: 'bool', label: 'Trim the public user list to what the sign-in screen needs' },
            { name: 'MirrorPreferenceLockToPolicy', kind: 'bool', label: 'Also turn native preference access off when Profile, Display (account), Home, Playback and Subtitles are all locked', hint: 'For managed non-admins only. It keeps them out of those settings even without the plugin, stays when the plugin is disabled or its folder deleted (Release all locks on the Overview, or uninstalling while the plugin is enabled, puts it back), has no Dashboard checkbox, and blocks their own watched states and favourites through UserData. Enforcement off or the SAFE-MODE file pauses it without putting the flags back. Off by default.' }
        ]
    },
    {
        title: 'Web client',
        items: [
            {
                name: 'FirstLoadMode',
                kind: 'choice',
                label: 'Until the web client knows a user\'s locks',
                choices: [
                    { value: 'HideUntilKnown', text: 'Hide the lockable sections of users who are or may be managed (HideUntilKnown)' },
                    { value: 'ShowUntilKnown', text: 'Show everything (ShowUntilKnown)' }
                ]
            },
            {
                name: 'BlockedNavigationNotice',
                kind: 'choice',
                label: 'When a navigation is blocked',
                choices: [
                    { value: 'Toast', text: 'Say so in a toast (Toast)' },
                    { value: 'Silent', text: 'Say nothing (Silent): the stock pages show nothing on a 403' }
                ]
            },
            { name: 'ShowManagedNoticeOnUserPages', kind: 'bool', label: 'Show a banner on the stock user pages of managed users', hint: 'Add user included.' },
            { name: 'EnableWebUi', kind: 'bool', label: 'Run the web client\'s script and head fragment', hint: 'Off: nothing is hidden in the web; the server still enforces.' },
            { name: 'ReportWebAnchorProblems', kind: 'bool', label: 'Report the web client\'s anchor problems to Diagnostics' }
        ]
    },
    {
        title: 'Data',
        items: [
            { name: 'SnapshotsToKeep', kind: 'number', min: 0, max: 100, label: 'Snapshots of the data file to keep (0 to 100)' },
            { name: 'AuditRetentionMonths', kind: 'number', min: 0, max: 120, label: 'Keep the audit log for (months, 0 to 120; 50 MB at most)' }
        ]
    }
];

const AR_ALLOW_LIST_KEY = /^[a-z0-9][a-z0-9_.-]{0,63}$/;

// The allow-list as typed: lower case, trimmed, once each, in order (the server reads it the same way).
function arAllowList(text) {
    const keys = [];
    String(text || '').split(/[\s,;]+/).forEach(function (key) {
        const k = key.trim().toLowerCase();
        if (k && keys.indexOf(k) < 0) {
            keys.push(k);
        }
    });
    return keys;
}

function arSettingLabel(name) {
    for (let g = 0; g < AR_SETTINGS_GROUPS.length; g++) {
        const item = AR_SETTINGS_GROUPS[g].items.filter(function (i) {
            return i.name === name;
        })[0];
        if (item) {
            return item.label + ' (' + name + ')';
        }
    }

    return name;
}

function arSameSetting(a, b) {
    return Array.isArray(a) || Array.isArray(b) ? (a || []).join(',') === (b || []).join(',') : a === b;
}

// The review of a settings change: PUT Settings' dry run of body, then the save with its PreviewToken. Answers a promise of
// the saved change, or null when cancelled.
function arSettingsReview(body) {
    return new Promise(function (resolve) {
        let seq = 0;
        let shown = null;
        let saving = false;
        let done = false;
        // The keys the box was ticked for: another preview naming other keys needs it ticked again.
        let confirmedFor = null;
        const dialog = arDialog('Review the settings', { modal: true });
        const result = arEl('div', { class: arClass('preview'), id: 'AccessRolesSettingsPreview', 'aria-live': 'polite' });
        const secretBox = arEl('div', { hidden: true });
        const secretText = arEl('span');
        const secret = arCheckbox('', {
            onChange: function () {
                update();
            }
        });
        secret.input.id = 'AccessRolesSettingsConfirmSecrets';
        secret.element.querySelector('span[dir]').appendChild(secretText);
        secretBox.appendChild(secret.element);
        const error = arErrorLine();
        const staleText = arEl('p', { class: arClass('stale-text') });
        const again = arButton('Look again', function () {
            arBusy(again, load);
        });
        again.id = 'AccessRolesSettingsLookAgain';
        const staleBox = arEl('div', { class: arClass('stale'), role: 'alert', hidden: true }, [staleText, arActions([again])]);
        const cancel = arButton('Cancel', function () {
            dialog.close();
        });
        const save = arButton('Save', function () {
            arBusy(save, submit, canSave);
        }, { variant: 'primary', disabled: true });
        save.id = 'AccessRolesSettingsSave';
        kit.append(dialog.body, result, secretBox, error, staleBox, arActions([cancel, save]));
        dialog.onClose(function () {
            seq++;
            if (!done) {
                done = true;
                resolve(null);
            }
        });

        function canSave() {
            return !!shown && !saving && (shown.Changes || []).length > 0 && (!(shown.SecretKeysAdded || []).length || secret.input.checked);
        }

        function update() {
            save.disabled = !canSave();
        }

        function render(dry) {
            shown = dry;
            arClear(result);
            const changes = dry.Changes || [];
            result.appendChild(arEl('p', { class: arClass('preview-summary'), text: changes.length ? 'Saving changes ' + arCount(changes.length, 'setting') + ':' : 'Nothing would change: the settings are already so.' }));
            if (changes.length) {
                result.appendChild(arEl('ul', { class: arClass('list'), id: 'AccessRolesSettingsChanges' }, changes.map(function (c) {
                    return arEl('li', { 'data-setting': c.Name, text: arSettingLabel(c.Name) + ': ' + (c.Before || '(none)') + ' → ' + (c.After || '(none)') });
                })));
            }

            if ((dry.Warnings || []).length) {
                result.appendChild(arHeading(4, 'What this means'));
                result.appendChild(arEl('ul', { class: arClass('list') + ' ' + arClass('preview-warning'), id: 'AccessRolesSettingsWarnings' }, dry.Warnings.map(function (w) {
                    return arEl('li', { text: w });
                })));
            }

            const secrets = dry.SecretKeysAdded || [];
            if (confirmedFor !== secrets.join(',')) {
                secret.input.checked = false;
                confirmedFor = secrets.join(',');
            }

            secretText.textContent = 'Add anyway: ' + secrets.join(', ') + (secrets.length === 1 ? ' carries' : ' carry') + ' secrets. Non-admins get ' + (secrets.length === 1 ? 'it' : 'them') + ' with the known secrets stripped; a native client may need ' + (secrets.length === 1 ? 'it' : 'them') + '.';
            arShow(secretBox, secrets.length > 0);
            update();
        }

        function load() {
            const mine = ++seq;
            shown = null;
            save.disabled = true;
            error.textContent = '';
            arShow(staleBox, false);
            return arAdmin.updateSettings(body, { dryRun: true }).then(function (dry) {
                if (mine === seq) {
                    render(dry || {});
                }
            }, function (e) {
                if (mine === seq) {
                    arClear(result);
                    error.textContent = arErrorText(e, 'The change could not be previewed.');
                }
            });
        }

        // The save of the answer on screen: the same body, its PreviewToken, and the secrets' confirmation if ticked.
        function submit() {
            if (!canSave()) {
                return null;
            }

            const sent = shown;
            const query = { ifPreview: sent.PreviewToken };
            if ((sent.SecretKeysAdded || []).length && secret.input.checked) {
                query.confirmSecretKeys = true;
            }

            saving = true;
            error.textContent = '';
            return arAdmin.updateSettings(body, query).then(function (saved) {
                saving = false;
                done = true;
                dialog.close();
                resolve(saved || {});
            }, function (e) {
                saving = false;
                if (arChanged(e)) {
                    shown = null;
                    arClear(result);
                    staleText.textContent = arErrorText(e, 'The settings changed since this preview; nothing was saved.') + ' Look again before you save.';
                    arShow(staleBox, true);
                    update();
                    again.focus();
                    return;
                }

                error.textContent = arErrorText(e, 'The settings could not be saved.');
                update();
            });
        }

        load();
    });
}

function arSettingsTab(ctx) {
    const controls = {};
    let loaded = null;
    const loading = arEl('p', { id: 'AccessRolesSettingsLoading', text: 'Loading the settings…' });
    const form = arEl('div', { id: 'AccessRolesSettingsForm', hidden: true });
    const status = arEl('p', { id: 'AccessRolesSettingsStatus', 'aria-live': 'polite' });
    const error = arErrorLine();
    error.id = 'AccessRolesSettingsError';
    const review = arButton('Review and save…', function () {
        arBusy(review, reviewChanges, function () {
            return !!loaded && Object.keys(edited()).length > 0 && !problems().length;
        });
    }, { variant: 'primary', disabled: true });
    review.id = 'AccessRolesSettingsReview';
    review.setAttribute('data-ar-focus', 'settings-review');
    const undo = arButton('Undo my changes', function () {
        set(loaded);
    }, { disabled: true });
    undo.id = 'AccessRolesSettingsUndo';
    undo.setAttribute('data-ar-focus', 'settings-undo');

    function control(item) {
        let element;
        let get;
        let put;
        let input;
        if (item.kind === 'bool') {
            const box = arCheckbox(item.label, { hint: item.hint, onChange: changed });
            input = box.input;
            element = box.element;
            get = function () {
                return input.checked;
            };
            put = function (value) {
                input.checked = !!value;
            };
        } else if (item.kind === 'choice') {
            const select = arSelect(item.label, item.choices, item.choices[0].value, changed);
            input = select.select;
            element = select.element;
            get = function () {
                return input.value;
            };
            put = function (value) {
                // A value with no choice of its own (Strict, stored by hand) is offered as it is, and kept unless changed.
                const known = item.choices.some(function (c) {
                    return c.value === value;
                }) || all(input).some(function (o) {
                    return o.value === value;
                });
                if (!known && value) {
                    input.appendChild(arEl('option', { value: value, text: value === 'Strict' ? 'Strict (stored; arrives in 1.1, acts as Standard until then)' : String(value) }));
                }

                input.value = value;
            };
        } else {
            input = kit.input({ type: item.kind === 'number' ? 'number' : 'text', autocomplete: 'off', spellcheck: 'false' });
            if (item.kind === 'number') {
                input.setAttribute('min', String(item.min));
                input.setAttribute('max', String(item.max));
                input.setAttribute('step', '1');
                input.setAttribute('inputmode', 'numeric');
            }

            input.addEventListener('input', changed);
            input.addEventListener('change', changed);
            element = arEl('div', null, [kit.field(item.label, input), item.hint ? arHint(item.hint) : null]);
            get = item.kind === 'number'
                ? function () {
                    return String(input.value).trim() === '' ? NaN : Number(input.value);
                }
                : function () {
                    return arAllowList(input.value);
                };
            put = item.kind === 'number'
                ? function (value) {
                    input.value = value === undefined || value === null ? '' : String(value);
                }
                : function (value) {
                    input.value = (value || []).join(', ');
                };
        }

        input.setAttribute('data-setting', item.name);
        input.setAttribute('data-ar-focus', 'setting-' + item.name);
        controls[item.name] = { item: item, get: get, put: put, input: input };
        if (item.kind === 'choice' && item.hint) {
            return arEl('div', null, [element, arHint(item.hint)]);
        }

        return element;
    }

    function all(select) {
        return Array.prototype.slice.call(select.querySelectorAll('option'));
    }

    AR_SETTINGS_GROUPS.forEach(function (group) {
        form.appendChild(arEl('fieldset', { class: arClass('settings-group') }, [arEl('legend', { text: group.title })].concat(group.items.map(control))));
    });
    form.appendChild(arActions([undo, review]));
    form.appendChild(status);
    form.appendChild(error);

    const element = arEl('div', { class: arClass('settings') }, [
        arEl('div', { class: arClass('section') }, [
            arHeading(3, 'Settings'),
            arHint('Saved in the plugin\'s configuration; a change applies at once. Review shows what each change does before anything is saved.'),
            loading,
            form
        ])
    ]);

    // What the admin changed, as the body: only those settings.
    function edited() {
        const body = {};
        if (!loaded) {
            return body;
        }

        Object.keys(controls).forEach(function (name) {
            const value = controls[name].get();
            if (!arSameSetting(value, loaded[name])) {
                body[name] = value;
            }
        });
        return body;
    }

    function problems() {
        const found = [];
        Object.keys(controls).forEach(function (name) {
            const c = controls[name];
            const value = c.get();
            if (c.item.kind === 'number' && (!Number.isInteger(value) || value < c.item.min || value > c.item.max)) {
                found.push(c.item.label.replace(/ \(.*\)$/, '') + ': a whole number from ' + c.item.min + ' to ' + c.item.max + '.');
            }

            if (c.item.kind === 'list') {
                value.forEach(function (key) {
                    if (!AR_ALLOW_LIST_KEY.test(key)) {
                        found.push('"' + key + '" isn\'t a configuration key: letters, digits, ".", "_" and "-", at most 64.');
                    }
                });
                if (value.length > 50) {
                    found.push('At most 50 configuration keys.');
                }
            }
        });
        return found;
    }

    function changed() {
        const count = Object.keys(edited()).length;
        const found = problems();
        error.textContent = found.join(' ');
        status.textContent = !loaded ? '' : (count ? arCount(count, 'setting') + ' changed, not saved yet.' : 'No changes.');
        review.disabled = !loaded || count === 0 || found.length > 0;
        undo.disabled = count === 0;
    }

    function set(settings) {
        loaded = settings || {};
        Object.keys(controls).forEach(function (name) {
            controls[name].put(loaded[name]);
        });
        arShow(loading, false);
        arShow(form, true);
        changed();
    }

    function reviewChanges() {
        const body = edited();
        if (!Object.keys(body).length) {
            return null;
        }

        return arSettingsReview(body).then(function (saved) {
            if (!saved) {
                return null;
            }

            set(saved.Settings || loaded);
            ctx.settings = saved.Settings || ctx.settings;
            kit.toast('Settings saved.');
            return ctx.refresh();
        });
    }

    // Read when the tab is shown: what another administrator changed meanwhile shows, unless this admin has edits.
    function onShow() {
        if (loaded && Object.keys(edited()).length) {
            return null;
        }

        return ctx.loadSettings().then(set, function (e) {
            loading.textContent = 'The settings couldn\'t be loaded: ' + arErrorText(e);
            arShow(loading, true);
        });
    }

    return { element: element, onShow: onShow };
}
