// The Users tab: every user with their roles, state and access summary, and the badges a
// glance needs (administrator, disabled, shown on the login screen, changed outside Access Roles, no libraries, library
// access waiting for the sync). A name opens the user's panel (70-panel.js); ticked users get roles added or taken away in
// bulk, previewed first as POST Users/Bulk's own dry run with each user's IfUpdatedAt: a user changed
// since the list was read makes it a 409 Changed naming them, and nothing is written.

// A user's whole assignment as a request (PUT Users/{id}, Preview's Assignments): the stored one (the Effective view's
// Assignment, never masked) with the changes over it. A field a newer version wrote stays: its value is sent as null, which
// the server reads as "keep it".
function arAssignmentRequest(effective, changes) {
    const stored = effective.Assignment || null;
    const libraries = stored ? stored.Libraries : null;
    const request = {
        UserId: effective.UserId,
        RoleIds: stored ? stored.RoleIds.slice() : [],
        LockOverrides: Object.assign({}, stored ? stored.LockOverrides : {}),
        Libraries: {
            Mode: libraries ? libraries.Mode : 'Inherit',
            Add: libraries ? libraries.Add.slice() : [],
            Remove: libraries ? libraries.Remove.slice() : [],
            ConfirmNoLibraries: libraries ? !!libraries.ConfirmNoLibraries : false
        },
        State: stored ? stored.State : 'Managed',
        IfUpdatedAt: stored ? effective.UpdatedUtc : null
    };
    if (request.State === 'CannotManage') {
        // The server's to set: a request names Managed or Excluded, or nothing.
        request.State = null;
    }

    changes = changes || {};
    Object.keys(changes).forEach(function (key) {
        if (key === 'Libraries') {
            Object.assign(request.Libraries, changes.Libraries);
        } else {
            request[key] = changes[key];
        }
    });
    return request;
}

// The admin's edit of an assignment (mine, a request made over the effective view before) put over the assignment as it is
// now (the effective view now; "Reload and keep my changes"): the roles, overrides and libraries they
// added or took out, and the fields they changed, over what the assignment is now, with now's IfUpdatedAt.
function arRebaseAssignment(before, mine, now) {
    const was = arAssignmentRequest(before, {});
    const out = arAssignmentRequest(now, {});
    out.RoleIds = arRebaseList(was.RoleIds, mine.RoleIds, out.RoleIds);
    Object.keys(Object.assign({}, was.LockOverrides, mine.LockOverrides)).forEach(function (lock) {
        if (mine.LockOverrides[lock] !== was.LockOverrides[lock]) {
            if (mine.LockOverrides[lock]) {
                out.LockOverrides[lock] = mine.LockOverrides[lock];
            } else {
                delete out.LockOverrides[lock];
            }
        }
    });
    if (mine.Libraries.Mode !== was.Libraries.Mode) {
        out.Libraries.Mode = mine.Libraries.Mode;
    }

    out.Libraries.Add = arRebaseList(was.Libraries.Add, mine.Libraries.Add, out.Libraries.Add);
    out.Libraries.Remove = arRebaseList(was.Libraries.Remove, mine.Libraries.Remove, out.Libraries.Remove);
    if (mine.Libraries.ConfirmNoLibraries !== was.Libraries.ConfirmNoLibraries) {
        out.Libraries.ConfirmNoLibraries = mine.Libraries.ConfirmNoLibraries;
    }

    if (mine.State !== was.State) {
        out.State = mine.State;
    }

    return out;
}

function arRoleNames(ctx, roleIds) {
    return (roleIds || []).map(function (id) {
        const role = ctx.roles.filter(function (r) {
            return arSameId(r.Id, id);
        })[0];
        return role ? role.Name : arId(id);
    }).join(', ');
}

// Adding (or taking away) roles of the ticked users: the preview is POST Users/Bulk's dry run, the save the same request.
// IfUpdatedAt names each ticked user with the UpdatedUtc of their assignment as the list has it (null without one).
function arBulkRoles(ctx, userIds, removing) {
    const verb = removing ? 'Remove' : 'Assign';
    const dialog = arDialog(verb + ' roles for ' + userIds.length + (userIds.length === 1 ? ' user' : ' users'), { modal: true });
    const checks = ctx.roles.map(function (role) {
        const check = arCheckbox(role.Name, {
            onChange: function () {
                go.disabled = ctx.readOnly || chosen().length === 0;
            }
        });
        check.input.setAttribute('data-role-id', arId(role.Id));
        return { role: role, check: check };
    });
    function chosen() {
        return checks.filter(function (c) {
            return c.check.input.checked;
        }).map(function (c) {
            return c.role.Id;
        });
    }

    const error = arErrorLine();
    const cancel = arButton('Cancel', function () {
        dialog.close();
    });
    const go = arButton('Continue', function () {
        arBusy(go, submit);
    }, { variant: 'primary', disabled: true });
    const users = ctx.users.filter(function (u) {
        return arHasId(userIds, u.UserId);
    });
    kit.append(dialog.body,
        arEl('p', null, [removing ? 'Take these roles from ' : 'Give these roles to '].concat(arNameNodes(users), [':'])),
        ctx.roles.length ? arEl('div', { class: arClass('checks') }, checks.map(function (c) {
            return c.check.element;
        })) : arHint('There are no roles yet.'),
        error,
        arActions([cancel, go]));

    function submit() {
        error.textContent = '';
        const roleIds = chosen();
        return arReview({
            title: verb + ' roles',
            intro: (removing ? 'Taking ' : 'Giving ') + arRoleNames(ctx, roleIds) + (removing ? ' from the ' : ' to the ') + userIds.length + (userIds.length === 1 ? ' selected user.' : ' selected users.'),
            libraryName: ctx.libraryName,
            request: function () {
                const body = {
                    UserIds: userIds.slice(),
                    IfUpdatedAt: userIds.map(function (id) {
                        const listed = ctx.users.filter(function (u) {
                            return arSameId(u.UserId, id);
                        })[0];
                        return { UserId: id, UpdatedUtc: (listed && listed.UpdatedUtc) || null };
                    })
                };
                body[removing ? 'RemoveRoleIds' : 'AddRoleIds'] = roleIds;
                return body;
            },
            send: function (body, options) {
                return arAdmin.bulk(options.onUnmanage ? Object.assign({ OnUnmanage: options.onUnmanage }, body) : body, arWriteQuery(options));
            },
            // The server says whose assignment the request changes: nobody's is "already" (no client-side guess).
            empty: function (change) {
                return (change && change.Changed || []).length ? null : (removing ? 'None of them has these roles.' : 'They all have these roles already.');
            },
            // The list again: its UpdatedUtc are the new tokens; the roles to add or take away stay the admin's.
            reload: function () {
                return ctx.refresh();
            },
            saveLabel: verb
        }).then(function (change) {
            if (change) {
                const n = (change.Changed || []).length;
                dialog.close();
                ctx.selected = [];
                kit.toast((removing ? 'Roles removed from ' : 'Roles given to ') + n + (n === 1 ? ' user.' : ' users.'));
                return ctx.refresh();
            }

            return null;
        });
    }

    return dialog;
}

function arUserBadges(user) {
    const badges = [];
    if (user.IsAdministrator) {
        badges.push(arBadge('Administrator'));
    }

    if (user.IsDisabled) {
        badges.push(arBadge('Disabled'));
    }

    if (!user.IsHidden) {
        badges.push(arBadge('Shown on the login screen'));
    }

    // A result of no library nobody chose is its own state, never drift: the library sync leaves
    // that user's access alone. A chosen one is managed like any other, so a library given natively since is drift.
    const unchosenNone = !!user.NoLibraries && !user.NoLibrariesConfirmed;
    if (user.Drift && !unchosenNone) {
        badges.push(arBadge('Changed outside Access Roles', 'warning'));
    }

    if (user.NoLibraries) {
        badges.push(unchosenNone ? arBadge('No libraries', 'warning') : arBadge('No libraries (as chosen)'));
    }

    if (user.PendingWrite) {
        badges.push(arBadge('Library access to be written'));
    }

    return badges;
}

function arUsersTab(ctx) {
    const element = arEl('div', { class: arClass('users') });
    ctx.selected = ctx.selected || [];

    function isSelected(id) {
        return arHasId(ctx.selected, id);
    }

    function render() {
        arClear(element);
        const users = ctx.users;
        const known = ctx.selected.filter(function (id) {
            return users.some(function (u) {
                return arSameId(u.UserId, id);
            });
        });
        ctx.selected = known;
        const count = arEl('span', { class: arClass('count'), 'aria-live': 'polite' });
        const assign = arButton('Assign roles…', function () {
            arBulkRoles(ctx, ctx.selected.slice(), false);
        });
        assign.id = 'AccessRolesBulkAssign';
        assign.setAttribute('data-ar-focus', 'bulk-assign');
        const remove = arButton('Remove roles…', function () {
            arBulkRoles(ctx, ctx.selected.slice(), true);
        });
        remove.id = 'AccessRolesBulkRemove';
        remove.setAttribute('data-ar-focus', 'bulk-remove');
        const all = arCheckbox('Select every user', {
            checked: users.length > 0 && known.length === users.length,
            onChange: function (checked) {
                ctx.selected = checked ? users.map(function (u) {
                    return u.UserId;
                }) : [];
                rows.forEach(function (row) {
                    row.input.checked = checked;
                });
                update();
            }
        });
        all.input.id = 'AccessRolesSelectAll';
        all.input.setAttribute('data-ar-focus', 'select-all');
        function update() {
            const n = ctx.selected.length;
            count.textContent = n === 0 ? 'No user selected' : (n === 1 ? '1 user selected' : n + ' users selected');
            assign.disabled = ctx.readOnly || n === 0 || ctx.roles.length === 0;
            remove.disabled = ctx.readOnly || n === 0 || ctx.roles.length === 0;
            all.input.checked = users.length > 0 && n === users.length;
        }

        const rows = users.map(function (user) {
            const id = arId(user.UserId);
            const select = arCheckbox('Select ' + user.Name, {
                checked: isSelected(user.UserId),
                onChange: function (checked) {
                    ctx.selected = ctx.selected.filter(function (s) {
                        return !arSameId(s, user.UserId);
                    });
                    if (checked) {
                        ctx.selected.push(user.UserId);
                    }

                    update();
                }
            });
            select.element.classList.add(arClass('select'));
            select.input.setAttribute('data-ar-focus', 'select-' + id);
            const open = arEl('button', { type: 'button', class: arClass('user-name'), dir: 'auto', 'data-ar-focus': 'user-' + id, text: user.Name, onclick: function () {
                arUserPanel(ctx, user.UserId);
            } });
            const roleText = user.RoleIds.length ? 'Roles: ' + arRoleNames(ctx, user.RoleIds) : 'No role';
            const item = arEl('div', { class: arClass('user'), role: 'listitem', 'data-user-id': id }, [
                select.element,
                arEl('div', { class: arClass('user-main') }, [
                    open,
                    arEl('p', { class: arClass('user-line'), text: roleText + ' · ' + (AR_STATES[user.State] || user.State) }),
                    user.Summary ? arEl('p', { class: arClass('user-line'), text: user.Summary }) : null,
                    arEl('div', { class: arClass('badges') }, arUserBadges(user))
                ])
            ]);
            return { element: item, input: select.input };
        });

        kit.append(element,
            arEl('div', { class: arClass('section') }, [
                arHeading(3, 'Users'),
                arHint('Pick a name to see and change that user\'s roles, overrides and libraries. Tick users to give them roles, or take roles away, together.'),
                arHint('No libraries: the roles would leave the user no library. Unless that was chosen (Only… with nothing, and the box ticked), the library sync leaves their access as it is, and it isn\'t drift. A user who was chosen to see no library is managed like any other, so a library given to them outside Access Roles is drift.'),
                arEl('div', { class: arClass('bulk'), id: 'AccessRolesBulk' }, [all.element, count, arActions([assign, remove])]),
                users.length ? arEl('div', { class: arClass('user-list'), role: 'list' }, rows.map(function (r) {
                    return r.element;
                })) : arHint('The server has no user.')
            ]));
        update();
    }

    return { element: element, render: render };
}
