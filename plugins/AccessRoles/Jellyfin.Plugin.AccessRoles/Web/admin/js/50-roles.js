// The Roles tab: the default roles of new users at the top, every role with its
// members, locks, libraries and Dashboard row, preset cards when there is no role yet, and the role editor: the settings
// sections the user may not change (server-enforced or hidden only), the Dashboard row with "Block Dashboard data and
// server calls", "Hide other users" and the libraries (Not managed | All | All except… | Only…, at least one). Saving and
// deleting show the preview first: the write's own dry run (40-preview.js), with the role's tokens (IfUpdatedAt, and
// ifMembers: its members as the admin read them).

const AR_DASHBOARD_BLOCKED = 'The Dashboard is always blocked by Jellyfin. This role also blocks the admin data and server calls non-admins can still reach.';
const AR_DASHBOARD_OPEN = 'The Dashboard is always blocked by Jellyfin. This role doesn\'t block the admin data and server calls non-admins can still reach.';
const AR_DASHBOARD_ADMINS = 'Administrators are not restricted.';

function arRoleLibrariesText(ctx, libraries) {
    const mode = (libraries && libraries.Mode) || 'NotManaged';
    const names = ((libraries && libraries.Refs) || []).map(function (ref) {
        return (ref.Name || ctx.libraryName(ref.Id)) + (ref.Orphaned ? ' (deleted)' : '');
    }).join(', ');
    if (mode === 'Only') {
        return 'Only: ' + (names || 'no library');
    }

    if (mode === 'AllExcept') {
        return 'All except: ' + (names || 'nothing');
    }

    return arModeLabel(AR_ROLE_MODES, mode);
}

function arRoleMembers(ctx, role) {
    return ctx.users.filter(function (user) {
        return arHasId(user.RoleIds, role.Id);
    });
}

// A role as the request that would save it as it is (PUT Roles/{id}'s body).
function arRoleRequest(role) {
    const libraries = role.Libraries || {};
    return {
        Name: role.Name,
        Description: role.Description || null,
        Locks: (role.Locks || []).slice(),
        Libraries: { Mode: libraries.Mode || 'NotManaged', Refs: (libraries.Refs || []).map(function (ref) {
            return { Id: ref.Id };
        }) }
    };
}

function arRefIds(libraries) {
    return ((libraries && libraries.Refs) || []).map(function (ref) {
        return ref.Id;
    });
}

// The admin's edit of a role (from was to mine) put over the role as it is now ("Reload and keep my
// changes"): the fields they changed are theirs, the locks and libraries they added or took out too, the rest is now's.
function arRebaseRole(was, mine, now) {
    return {
        Name: mine.Name !== was.Name ? mine.Name : now.Name,
        Description: mine.Description !== was.Description ? mine.Description : now.Description,
        Locks: arRebaseList(was.Locks, mine.Locks, now.Locks),
        Libraries: mine.Libraries.Mode !== was.Libraries.Mode
            ? { Mode: mine.Libraries.Mode, Refs: mine.Libraries.Refs.slice() }
            : { Mode: now.Libraries.Mode, Refs: arRebaseList(arRefIds(was.Libraries), arRefIds(mine.Libraries), arRefIds(now.Libraries)).map(function (id) {
                return { Id: id };
            }) }
    };
}

// A role read again (after a 409 Changed): the server's list, so the tokens are the stored ones.
function arReadRole(ctx, role) {
    return arAdmin.roles().then(function (list) {
        const now = (list || []).filter(function (r) {
            return arSameId(r.Id, role.Id);
        })[0];
        if (!now) {
            throw new Error('Role ' + role.Name + ' was deleted meanwhile.');
        }

        ctx.refresh();
        return now;
    });
}

// The libraries to pick from: every library the server lists (disabled ones and the Collections library too), then the
// ones picked earlier that it no longer lists (kept until unticked or purged). Answers { element, selected() }.
function arLibraryPicker(ctx, legend, selectedIds) {
    const boxes = [];
    const listed = ctx.libraries.filter(function (library) {
        return library.State !== 'Orphaned';
    });
    const gone = (selectedIds || []).filter(function (id) {
        return !listed.some(function (library) {
            return arSameId(library.Id, id);
        });
    });
    const entries = listed.map(function (library) {
        const hints = [];
        if (!library.Enabled) {
            hints.push('disabled');
        }

        if (library.CollectionType === 'boxsets') {
            hints.push('needed to browse any collection');
        }

        if (library.Recreated) {
            hints.push('re-created: grant it again on the Libraries tab');
        }

        return { id: library.Id, name: library.Name, hint: hints.join('; ') };
    }).concat(gone.map(function (id) {
        return { id: id, name: ctx.libraryName(id), hint: 'deleted; untick to drop it' };
    }));
    const element = arEl('fieldset', { class: arClass('picker') }, [arEl('legend', { text: legend })].concat(entries.length ? entries.map(function (entry) {
        const box = arCheckbox(entry.name, { checked: arHasId(selectedIds, entry.id), hint: entry.hint || null });
        box.input.setAttribute('data-library-id', arId(entry.id));
        boxes.push({ id: entry.id, input: box.input });
        return box.element;
    }) : [arHint('The server has no library.')]));
    return {
        element: element,
        selected: function () {
            return boxes.filter(function (b) {
                return b.input.checked;
            }).map(function (b) {
                return b.id;
            });
        },
        onChange: function (fn) {
            element.addEventListener('change', fn);
        }
    };
}

function arPresetCards(onPick) {
    return arEl('div', { class: arClass('presets'), role: 'list' }, ['Kid', 'Guest', 'Standard'].map(function (name) {
        const preset = AR_PRESETS[name];
        const card = arEl('button', { type: 'button', class: arClass('preset'), 'data-preset': name, onclick: function () {
            onPick(name);
        } }, [arEl('span', { class: arClass('preset-name'), text: name }), arEl('span', { class: arClass('preset-text'), text: preset.text })]);
        return arEl('div', { role: 'listitem' }, card);
    }).concat([arEl('div', { role: 'listitem' }, arEl('button', { type: 'button', class: arClass('preset'), 'data-preset': 'Blank', onclick: function () {
        onPick(null);
    } }, [arEl('span', { class: arClass('preset-name'), text: 'Blank role' }), arEl('span', { class: arClass('preset-text'), text: 'No locks, libraries not managed; set everything yourself.' })]))]));
}

// The role editor: a new role (from a preset or blank) or an existing one. Save previews, then saves. latest is
// the role as last read: its tokens go with the request, and the admin's edit is put over it.
function arEditRole(ctx, role, presetName) {
    let latest = role;
    const preset = presetName ? AR_PRESETS[presetName] : null;
    const locks = role ? role.Locks.slice() : (preset ? preset.locks.slice() : []);
    const mode = role ? (role.Libraries.Mode || 'NotManaged') : (preset ? preset.mode : 'NotManaged');
    const refs = role ? role.Libraries.Refs.map(function (r) {
        return r.Id;
    }) : [];
    const dialog = arDialog(role ? 'Edit role ' + role.Name : (presetName ? 'New role: ' + presetName : 'New role'), {
        modal: true,
        returnFocus: function () {
            return ctx.root.querySelector('[data-ar-focus="' + (role ? 'edit-' + arId(role.Id) : 'new-role') + '"]');
        }
    });
    dialog.panel.classList.add(arClass('wide'));

    const name = kit.input({ maxlength: 100, value: role ? role.Name : (presetName || '') });
    const description = kit.input({ maxlength: 500, value: role ? (role.Description || '') : '' });
    const sections = AR_SETTINGS_SECTIONS.map(function (section) {
        const box = arCheckbox(arLockLabel(section), { checked: locks.indexOf(section) >= 0, hint: AR_HIDDEN_ONLY.indexOf(section) >= 0 ? 'hidden only' : 'server-enforced' });
        box.input.setAttribute('data-lock', section);
        return { lock: section, box: box };
    });
    const dashboardText = arEl('p', { class: arClass('dashboard-text') });
    const adminSurfaces = arCheckbox('Block Dashboard data and server calls', {
        checked: locks.indexOf('AdminSurfaces') >= 0,
        onChange: function () {
            dashboardText.textContent = adminSurfaces.input.checked ? AR_DASHBOARD_BLOCKED : AR_DASHBOARD_OPEN;
        }
    });
    adminSurfaces.input.setAttribute('data-lock', 'AdminSurfaces');
    dashboardText.textContent = adminSurfaces.input.checked ? AR_DASHBOARD_BLOCKED : AR_DASHBOARD_OPEN;
    const administrators = role && (role.Dashboard.AdministratorMembers || []).length
        ? AR_DASHBOARD_ADMINS + ' This role\'s administrators: ' + role.Dashboard.AdministratorMembers.join(', ') + '.'
        : AR_DASHBOARD_ADMINS;
    const userDirectory = arCheckbox('Hide other users', { checked: locks.indexOf('UserDirectory') >= 0, hint: 'the user lists only themselves' });
    userDirectory.input.setAttribute('data-lock', 'UserDirectory');

    const pickerBox = arEl('div', { class: arClass('picker-box') });
    let picker = null;
    const modeSelect = arSelect('Libraries', AR_ROLE_MODES, mode, function () {
        renderPicker();
    });
    function renderPicker() {
        const value = modeSelect.select.value;
        const before = picker ? picker.selected() : refs;
        arClear(pickerBox);
        picker = null;
        if (value === 'Only' || value === 'AllExcept') {
            picker = arLibraryPicker(ctx, value === 'Only' ? 'Libraries this role grants' : 'Libraries this role leaves out', before);
            pickerBox.appendChild(picker.element);
            if (value === 'Only') {
                pickerBox.appendChild(arHint('Future libraries are not included. Pick at least one.'));
            }
        } else if (value === 'All') {
            pickerBox.appendChild(arHint('Every library, and every library added later.'));
        } else if (value === 'NotManaged') {
            pickerBox.appendChild(arHint('The role leaves its members\' library access alone.'));
        }
    }

    renderPicker();
    const error = arErrorLine();
    const cancel = arButton('Cancel', function () {
        dialog.close();
    });
    const save = arButton(role ? 'Save' : 'Create', function () {
        arBusy(save, submit);
    }, { variant: 'primary', disabled: ctx.readOnly, title: ctx.readOnly ? 'Access Roles\' data is read-only.' : null });

    kit.append(dialog.body,
        kit.field('Name', name),
        kit.field('Description', description),
        arHeading(4, 'Settings the user may not change'),
        arEl('div', { class: arClass('checks') }, sections.map(function (s) {
            return s.box.element;
        })),
        arHint('Server-enforced sections are refused or kept as they were on the server; hidden-only ones live in the browser, so the web client only hides them. Settings a native app keeps on the device can\'t be locked, and other plugins\' preferences (such as Bookmarks\' player options) aren\'t covered.'),
        arHeading(4, 'Dashboard'),
        dashboardText,
        adminSurfaces.element,
        arHint(administrators),
        arHeading(4, 'Other users'),
        userDirectory.element,
        arHeading(4, 'Libraries'),
        modeSelect.element,
        pickerBox,
        error,
        arActions([cancel, save]));

    // The form as a request.
    function edited() {
        const chosen = sections.filter(function (s) {
            return s.box.input.checked;
        }).map(function (s) {
            return s.lock;
        });
        if (adminSurfaces.input.checked) {
            chosen.push('AdminSurfaces');
        }

        if (userDirectory.input.checked) {
            chosen.push('UserDirectory');
        }

        const libraryMode = modeSelect.select.value;
        const body = {
            Name: name.value.trim(),
            Description: description.value.trim() || null,
            Locks: chosen,
            Libraries: { Mode: libraryMode, Refs: picker ? picker.selected().map(function (id) {
                return { Id: id };
            }) : [] }
        };
        if (!role && presetName) {
            body.Preset = presetName;
        }

        return body;
    }

    // The request: the admin's edit over the role as last read, with its tokens.
    function request() {
        const body = edited();
        if (!role) {
            return body;
        }

        const out = arRebaseRole(arRoleRequest(role), body, arRoleRequest(latest));
        out.IfUpdatedAt = latest.UpdatedUtc;
        if (latest.MembersToken) {
            out.IfMembers = latest.MembersToken;
        }

        return out;
    }

    function submit() {
        error.textContent = '';
        const body = edited();
        if (!body.Name) {
            error.textContent = 'Give the role a name.';
            return null;
        }

        if (body.Libraries.Mode === 'Only' && body.Libraries.Refs.length === 0) {
            error.textContent = 'Pick at least one library.';
            return null;
        }

        return arReview({
            title: (role ? 'Save role ' : 'Create role ') + body.Name,
            libraryName: ctx.libraryName,
            request: request,
            send: function (sent, options) {
                return role
                    ? arAdmin.updateRole(role.Id, sent, arWriteQuery(options))
                    : arAdmin.createRole(sent, arWriteQuery(options));
            },
            reload: role ? function () {
                return arReadRole(ctx, role).then(function (now) {
                    latest = now;
                });
            } : null,
            saveLabel: role ? 'Save' : 'Create'
        }).then(function (change) {
            if (change) {
                dialog.close();
                kit.toast('Role ' + ((change.Role && change.Role.Name) || body.Name) + ' saved.');
                return ctx.refresh();
            }

            return null;
        });
    }

    return dialog;
}

// Deleting a role (its members may leave library management): optionally another role for its members instead.
// The preview is DELETE's own dry run with the reassignment and the role's tokens: ifMembers refuses it (409 Changed) when
// a member was added or removed, or a member's assignment changed, since the admin read them.
function arDeleteRole(ctx, role) {
    let latest = role;
    const dialog = arDialog('Delete role ' + role.Name, {
        modal: true,
        returnFocus: function () {
            return ctx.root.querySelector('[data-ar-focus="new-role"]');
        }
    });
    const membersText = arEl('p');
    function renderMembers() {
        const now = arRoleMembers(ctx, latest);
        arClear(membersText);
        kit.append.apply(null, [membersText].concat(now.length
            ? ['Its ' + now.length + (now.length === 1 ? ' member loses it: ' : ' members lose it: ')].concat(arNameNodes(now), ['.'])
            : ['No one has this role yet.']));
        return now;
    }

    const members = renderMembers();
    const others = ctx.roles.filter(function (r) {
        return !arSameId(r.Id, role.Id);
    });
    const reassign = arSelect('Give its members this role instead', [{ value: '', text: 'No role' }].concat(others.map(function (r) {
        return { value: arId(r.Id), text: r.Name };
    })), '');
    const error = arErrorLine();
    const cancel = arButton('Cancel', function () {
        dialog.close();
    });
    const go = arButton('Continue', function () {
        arBusy(go, submit);
    }, { variant: 'primary', disabled: ctx.readOnly || role.IsDefault });
    kit.append(dialog.body,
        membersText,
        role.IsDefault ? arBanner('This is a default role for new users, so it can\'t be deleted: take it out of the default roles first.') : null,
        members.length && others.length ? reassign.element : null,
        error,
        arActions([cancel, go]));

    function submit() {
        error.textContent = '';
        const target = (members.length && others.length && reassign.select.value) || null;
        return arReview({
            title: 'Delete role ' + role.Name,
            libraryName: ctx.libraryName,
            saveLabel: 'Delete',
            request: function () {
                const query = {};
                if (target) {
                    query.reassignTo = target;
                }

                if (latest.UpdatedUtc) {
                    query.ifUpdatedAt = latest.UpdatedUtc;
                }

                if (latest.MembersToken) {
                    query.ifMembers = latest.MembersToken;
                }

                return query;
            },
            send: function (query, options) {
                return arAdmin.deleteRole(role.Id, Object.assign(arWriteQuery(options), query));
            },
            reload: function () {
                return arReadRole(ctx, role).then(function (now) {
                    latest = now;
                    return ctx.refresh();
                }).then(renderMembers);
            }
        }).then(function (change) {
            if (change) {
                dialog.close();
                kit.toast('Role ' + role.Name + ' deleted.');
                return ctx.refresh();
            }

            return null;
        });
    }

    return dialog;
}

function arRolesTab(ctx) {
    const element = arEl('div', { class: arClass('roles') });

    function renderDefaults() {
        const box = arEl('div', { class: arClass('section'), id: 'AccessRolesDefaultRoles' }, [arHeading(3, 'Default roles for new users')]);
        if (ctx.roles.length === 0) {
            box.appendChild(arHint('New users get no role. Create a role first.'));
            return box;
        }

        const checks = ctx.roles.map(function (role) {
            const check = arCheckbox(role.Name, {
                checked: !!role.IsDefault,
                onChange: function () {
                    save.disabled = ctx.readOnly || !changed();
                }
            });
            check.input.setAttribute('data-role-id', arId(role.Id));
            check.input.setAttribute('data-ar-focus', 'default-' + arId(role.Id));
            return { role: role, check: check };
        });
        const chosen = function () {
            return checks.filter(function (c) {
                return c.check.input.checked;
            }).map(function (c) {
                return c.role.Id;
            });
        };
        const changed = function () {
            return checks.some(function (c) {
                return c.check.input.checked !== !!c.role.IsDefault;
            });
        };
        const save = arButton('Save default roles', function () {
            arBusy(save, function () {
                const ids = chosen();
                return arReview({
                    title: 'Default roles for new users',
                    intro: ids.length ? 'New users will get: ' + arNames(ctx.roles.filter(function (r) {
                        return arHasId(ids, r.Id);
                    })) + '.' : 'New users will get no role.',
                    libraryName: ctx.libraryName,
                    request: function () {
                        return ids;
                    },
                    send: function (sent, options) {
                        return arAdmin.defaultRoles(sent, arWriteQuery(options));
                    }
                }).then(function (change) {
                    if (change) {
                        kit.toast('Default roles saved.');
                        return ctx.refresh();
                    }

                    return null;
                });
            });
        }, { disabled: true });
        save.id = 'AccessRolesDefaultRolesSave';
        save.setAttribute('data-ar-focus', 'default-save');
        kit.append(box,
            arHint('Jellyfin gives these roles to every user it creates, users that LDAP or SSO create included.'),
            arEl('div', { class: arClass('checks') }, checks.map(function (c) {
                return c.check.element;
            })),
            arActions([save]));
        return box;
    }

    function renderRole(role) {
        const members = arRoleMembers(ctx, role);
        const locks = role.Locks.filter(function (l) {
            return l !== 'AdminSurfaces' && l !== 'UserDirectory';
        });
        const edit = arButton('Edit', function () {
            arEditRole(ctx, role, null);
        });
        edit.setAttribute('data-action', 'edit');
        edit.setAttribute('data-ar-focus', 'edit-' + arId(role.Id));
        const remove = arButton('Delete', function () {
            arDeleteRole(ctx, role);
        }, { disabled: ctx.readOnly });
        remove.setAttribute('data-action', 'delete');
        remove.setAttribute('data-ar-focus', 'delete-' + arId(role.Id));
        return arEl('div', { class: arClass('role'), role: 'listitem', 'data-role-id': arId(role.Id) }, [
            arEl('div', { class: arClass('role-head') }, [
                arEl('h4', { class: arClass('role-name'), dir: 'auto', text: role.Name }),
                role.IsDefault ? arBadge('Default for new users') : null
            ]),
            role.Description ? arEl('p', { class: arClass('role-description'), dir: 'auto', text: role.Description }) : null,
            arEl('p', { class: arClass('role-line') }, members.length ? ['Members: '].concat(arNameNodes(members)) : 'No one has this role yet'),
            arEl('p', { class: arClass('role-line'), text: locks.length ? 'Settings locked: ' + locks.map(arLockLabel).join(', ') : 'No settings locked' }),
            arEl('p', { class: arClass('role-line'), text: 'Libraries: ' + arRoleLibrariesText(ctx, role.Libraries) }),
            arEl('p', { class: arClass('role-line'), text: 'Dashboard: ' + role.Dashboard.NonAdministrators }),
            arEl('p', { class: arClass('role-line'), text: role.Locks.indexOf('UserDirectory') >= 0 ? 'Other users: hidden' : 'Other users: listed' }),
            (role.Dashboard.AdministratorMembers || []).length ? arHint(AR_DASHBOARD_ADMINS + ' Its administrators: ' + role.Dashboard.AdministratorMembers.join(', ') + '.') : null,
            (role.UnknownValues || []).length ? arHint('Values a newer version wrote, kept as they are: ' + role.UnknownValues.join(', ')) : null,
            arActions([edit, remove])
        ]);
    }

    function render() {
        arClear(element);
        element.appendChild(renderDefaults());
        const add = arButton('New role', function () {
            const chooser = arDialog('New role');
            chooser.body.appendChild(arEl('p', { text: 'Start from a preset (you can change everything before saving):' }));
            chooser.body.appendChild(arPresetCards(function (preset) {
                chooser.close();
                arEditRole(ctx, null, preset);
            }));
        }, { variant: 'primary', disabled: ctx.readOnly });
        add.id = 'AccessRolesNewRole';
        add.setAttribute('data-ar-focus', 'new-role');
        const head = arEl('div', { class: arClass('section') }, [arHeading(3, 'Roles'), arActions([add])]);
        element.appendChild(head);
        if (ctx.roles.length === 0) {
            head.appendChild(kit.notice({ kind: 'empty', text: 'There are no roles yet. Start from a preset:' }));
            head.appendChild(arPresetCards(function (preset) {
                arEditRole(ctx, null, preset);
            }));
            return;
        }

        head.appendChild(arEl('div', { class: arClass('role-list'), role: 'list' }, ctx.roles.map(renderRole)));
    }

    return { element: element, render: render };
}
