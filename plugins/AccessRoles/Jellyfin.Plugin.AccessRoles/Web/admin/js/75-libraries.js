// The Libraries tab: every library with its id, its native Enabled state, its created time,
// the roles that grant it and who can open it and why (GET Libraries' Users: the native access, what the server enforces
// now, each user marked where it isn't what the rules give and why, and NotOpenedBy, whom the rules give it who can't open
// it yet), the users' own overrides that refer to it, and its warnings: in no role; renamed (the audit's LibraryRenamed lines, read when
// the tab is shown); deleted, with what referred to it, until Remove references (POST Libraries/{id}/Purge: the grants go,
// what leaves it out stays, since 1.0.1); created again
// under the same id, until Grant again (POST Libraries/{id}/GrantAgain). Both writes are previewed as their own dry run
// and saved with its PreviewToken (40-preview.js). Then what each role and each user gets, the other way round.

// On the TV layout a card of text takes the focus too, so the D-pad can reach (and scroll to) every one of them, and Down
// and Up on it go to the next or previous control of its tab in the page's order. jellyfin-web's focus manager picks the
// nearest element in that direction, and from a library card, taller than the gap to the dashboard's menu, that was the
// menu (the admin page probe's TV webcheck).
const AR_TV_FOCUSABLE = 'button, input, select, textarea, a[href], [tabindex="0"]';

function arTvFocusable(node) {
    if (!kit.layout.isTv()) {
        return node;
    }

    node.setAttribute('tabindex', '0');
    node.addEventListener('keydown', function (e) {
        if (e.target !== node || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey || (e.key !== 'ArrowDown' && e.key !== 'ArrowUp')) {
            return;
        }

        const scope = node.closest('[role="tabpanel"]') || node.parentNode;
        const order = Array.prototype.filter.call(scope.querySelectorAll(AR_TV_FOCUSABLE), function (el) {
            return !el.disabled && !el.closest('[hidden]') && el.getClientRects().length > 0;
        });
        const next = order[order.indexOf(node) + (e.key === 'ArrowDown' ? 1 : -1)];
        if (next) {
            e.preventDefault();
            e.stopPropagation();
            next.focus();
        }
    });
    return node;
}

// Why a user's native access to a library isn't what the rules give (GET Libraries' Mismatch).
function arMismatchText(why) {
    const words = {
        Drift: 'changed outside Access Roles; DriftPolicy ReportOnly keeps it until it is reverted',
        NoLibraries: 'the roles would leave this user no library, so the library sync leaves their access as it is',
        LibrarySyncDisabled: 'the library sync is switched off',
        EnforcementDisabled: 'enforcement is switched off',
        StoreRestored: 'the library sync is held after a restore until it is confirmed',
        SafeMode: 'SAFE-MODE stops the library sync',
        StoreReadOnly: 'Access Roles\' data is read-only, so the library sync is paused',
        StoreDegraded: 'Access Roles\' data was recovered empty and Degraded mode locks users, so the library sync is paused',
        Pending: 'the library sync writes it at its next run'
    };
    return words[why] || String(why || '');
}

function arRoleName(ctx, roleId) {
    const found = ctx.roles.filter(function (r) {
        return arSameId(r.Id, roleId);
    })[0];
    return found ? found.Name : 'Role ' + arId(roleId);
}

// The roles a deletion leaves with no library: an Only… role whose every library is deleted.
function arEmptiedRoles(ctx, roleIds) {
    return ctx.roles.filter(function (r) {
        const refs = (r.Libraries && r.Libraries.Refs) || [];
        return arHasId(roleIds, r.Id) && r.Libraries.Mode === 'Only' && refs.length > 0 && refs.every(function (ref) {
            return ref.Orphaned;
        });
    });
}

// The roles that leave a library out (All except…, or a mode a newer build wrote): a purge keeps their reference (1.0.1).
function arExcludingRoles(ctx, roleIds) {
    return ctx.roles.filter(function (r) {
        const mode = r.Libraries && r.Libraries.Mode;
        return arHasId(roleIds, r.Id) && mode !== 'Only' && mode !== 'All' && mode !== 'NotManaged';
    });
}

// The purge dialog's sentence (1.0.1, the final verification's purge finding): what goes (the grants) and what stays (the
// exclusions, each named), as a sentence's children.
function arPurgeIntro(ctx, library) {
    const keeping = arExcludingRoles(ctx, library.RoleIds || []);
    const removed = library.RemovedFor || [];
    const children = ['Library ', arName(library.Name), ' was deleted. Removing its references takes it out of what grants it: the Only… roles\' lists, the users\' own Add lists and the original access recorded for them. A role whose only library it was then grants nothing.'];
    if (!keeping.length && !removed.length) {
        children.push(' Nothing leaves it out, so nothing refers to it any more.');
        return children;
    }

    children.push(' What leaves it out stays: ');
    if (keeping.length) {
        children.push(keeping.length === 1 ? 'the All except… role ' : 'the All except… roles ');
        children.push.apply(children, arNameNodes(keeping));
    }

    if (removed.length) {
        children.push((keeping.length ? ' and ' : '') + 'the own Remove ' + (removed.length === 1 ? 'list of ' : 'lists of '));
        children.push.apply(children, arNameNodes(removed));
    }

    children.push(' keep leaving it out. If a library is created again under its id, they leave it out too, and Grant again doesn\'t change that: to stop one leaving it out, untick the library in that role or in that user\'s Remove list.');
    return children;
}

function arLibrariesTab(ctx) {
    const summary = arEl('p', { id: 'AccessRolesLibrariesSummary' });
    const list = arEl('div', { id: 'AccessRolesLibraryList', class: arClass('library-list') });
    const renamesNote = arEl('p', { class: arClass('hint'), id: 'AccessRolesRenamesNote', hidden: true });
    const byRole = arEl('div', { id: 'AccessRolesLibrariesByRole' });
    const byUser = arEl('div', { id: 'AccessRolesLibrariesByUser' });
    // The audit's LibraryRenamed lines, newest first; null until the tab was shown.
    let renames = null;
    let renamesSeq = 0;

    const element = arEl('div', { class: arClass('libraries') }, [
        arEl('div', { class: arClass('section') }, [
            arHeading(3, 'Libraries'),
            arHint('What the server has and what Access Roles knows: who can open each library now and why, and where that isn\'t what the roles give. A deleted library\'s references are kept until you remove them, and what leaves it out (All except… roles, users\' own Remove lists) stays even then; a library created again under the same id is a new library until you grant it again, and what leaves it out still does.'),
            summary,
            renamesNote,
            list
        ]),
        arEl('div', { class: arClass('section') }, [arHeading(3, 'What each role grants'), byRole]),
        arEl('div', { class: arClass('section') }, [arHeading(3, 'What each user can open'), byUser])
    ]);

    // The renames, read again each time the tab is shown (they're the library sync's, any time).
    function onShow() {
        const mine = ++renamesSeq;
        return arAdmin.audit({ action: 'LibraryRenamed', limit: 50 }).then(function (page) {
            if (mine === renamesSeq) {
                renames = (page && page.Items) || [];
                arShow(renamesNote, false);
                render();
            }
        }, function (e) {
            if (mine === renamesSeq) {
                renamesNote.textContent = 'The renames couldn\'t be read from the audit log: ' + arErrorText(e);
                arShow(renamesNote, true);
            }
        });
    }

    function renameOf(library) {
        return (renames || []).filter(function (line) {
            return line.Details && arSameId(line.Details.LibraryId, library.Id);
        })[0] || null;
    }

    function sourcesText(sources) {
        return (sources || []).map(arLibrarySourceText).join(', ');
    }

    // One user of a library's lists: why they open it (or the roles give it), and where that isn't what the rules give, why.
    function userLine(u) {
        const why = (u.Sources || []).length ? sourcesText(u.Sources) : 'not given by the roles';
        const attributes = { 'data-user-id': arId(u.UserId) };
        if (u.Mismatch) {
            attributes['data-mismatch'] = u.Mismatch;
            attributes.class = arClass('library-mismatch');
        }

        return arEl('li', attributes, [arName(u.Name), ': ' + why + (u.Mismatch ? ' (' + arMismatchText(u.Mismatch) + ')' : '') + (u.IsDisabled ? ' (disabled)' : '')]);
    }

    function warning(children, kind) {
        return arEl('p', { class: arClass('library-warning') + (kind === 'error' ? ' ' + arClass('library-warning-error') : ''), role: 'note' }, children);
    }

    // Remove references (Purge) and Grant again: the write's own dry run, then the save with its PreviewToken.
    function lifecycle(library, button, purge) {
        const name = library.Name;
        return arBusy(button, function () {
            return arReview({
                title: purge ? 'Remove the references to ' + name : 'Grant ' + name + ' again',
                intro: purge
                    ? arPurgeIntro(ctx, library)
                    : 'Library "' + name + '" was deleted and created again under the same id, so Jellyfin made it a new library and nobody was given it. Granting it again gives it back to the roles and users that had it; the library sync then writes their access.',
                saveLabel: purge ? 'Remove references' : 'Grant again',
                libraryName: ctx.libraryName,
                request: function () {
                    return null;
                },
                send: function (request, options) {
                    const query = arWriteQuery(options);
                    return purge ? arAdmin.purge(library.Id, query) : arAdmin.grantAgain(library.Id, query);
                }
            }).then(function (done) {
                if (done) {
                    kit.toast(purge ? 'The references to ' + name + ' are removed.' : name + ' is granted again.');
                    return ctx.refresh();
                }

                return null;
            });
        });
    }

    function card(library) {
        const deleted = library.State === 'Orphaned';
        const roleIds = library.RoleIds || [];
        const users = library.Users || [];
        const pending = library.NotOpenedBy || [];
        const added = library.AddedFor || [];
        const removed = library.RemovedFor || [];
        const differs = pending.length + users.filter(function (u) {
            return !!u.Mismatch;
        }).length;
        const badges = arEl('div', { class: arClass('badges') }, [
            deleted ? arBadge('Deleted', 'warning') : null,
            !deleted && differs ? arBadge('Not as the roles give', 'warning') : null,
            library.Recreated ? arBadge('Created again', 'warning') : null,
            !deleted && library.Enabled === false ? arBadge('Disabled in Jellyfin') : null,
            library.InNoRole && !deleted ? arBadge('In no role') : null,
            library.CollectionType ? arBadge(library.CollectionType) : null
        ]);
        const warnings = [];
        if (library.InNoRole && !deleted) {
            warnings.push(warning(['Library ', arName(library.Name), ' is in no role: of the managed users, only those with every library or with it added for them can see it.']));
        }

        const renamed = !deleted ? renameOf(library) : null;
        if (renamed && renamed.Details.OldName) {
            warnings.push(warning(['Library renamed: ', arName(renamed.Details.OldName), ' → ', arName(renamed.Details.Name || library.Name), '; roles re-pointed (' + arDate(renamed.Utc) + ').']));
        }

        const actions = [];
        if (deleted) {
            const roleNames = roleIds.map(function (id) {
                return arRoleName(ctx, id);
            });
            const overrides = added.length + removed.length;
            const emptied = arEmptiedRoles(ctx, roleIds);
            const without = ctx.users.filter(function (u) {
                return u.NoLibraries && !u.IsAdministrator;
            }).length;
            const children = ['Library ', arName(library.Name), ' was deleted.'];
            if (roleNames.length || overrides) {
                children.push(' ');
                if (roleNames.length) {
                    children.push(roleNames.length === 1 ? 'Role ' : 'Roles ');
                    children.push.apply(children, arNameNodes(roleNames));
                }

                if (overrides) {
                    children.push((roleNames.length ? ' and ' : '') + arCount(overrides, 'user override'));
                }

                children.push(' referred to it');
                if (emptied.length) {
                    children.push('; ' + (emptied.length === 1 ? 'role ' : 'roles '));
                    children.push.apply(children, arNameNodes(emptied));
                    children.push(' now ' + (emptied.length === 1 ? 'grants' : 'grant') + ' no library');
                    if (without) {
                        children.push(', so ' + arCount(without, 'user') + ' ' + (without === 1 ? 'has' : 'have') + ' no libraries');
                    }
                }

                // Only exclusions left (1.0.1): nothing to remove, and they stay, so a library created again stays left out of
                // them, Grant again or not (1.0.2: the text said "until you grant it again").
                children.push(library.Purgeable === false
                    ? '. Only what leaves it out (All except… roles, users\' own Remove lists) refers to it now, and that stays: if a library is created again under its id, they leave it out too, and Grant again doesn\'t change that. To stop one leaving it out, untick the library in that role or in that user\'s Remove list.'
                    : '. Their references are kept until you remove them.');
            } else {
                children.push(' Nothing refers to it any more.');
            }

            warnings.push(warning(children, emptied.length ? 'error' : null));
            if ((roleNames.length || overrides || library.Known) && library.Purgeable !== false) {
                const purge = arButton('Remove references…', function () {
                    lifecycle(library, purge, true);
                }, { disabled: ctx.readOnly });
                purge.setAttribute('data-action', 'purge');
                purge.setAttribute('data-ar-focus', 'purge-' + arId(library.Id));
                actions.push(purge);
            }
        }

        if (library.Recreated) {
            warnings.push(warning(['Library ', arName(library.Name), ' was created again. It is a new library: grant it again?']));
            const again = arButton('Grant again…', function () {
                lifecycle(library, again, false);
            }, { variant: 'primary', disabled: ctx.readOnly });
            again.setAttribute('data-action', 'grant-again');
            again.setAttribute('data-ar-focus', 'grant-again-' + arId(library.Id));
            actions.push(again);
        }

        const facts = ['Id ' + arId(library.Id)];
        if (library.CreatedUtc) {
            facts.push('created ' + arDate(library.CreatedUtc));
        }

        const lines = [
            arEl('p', { class: arClass('library-line') + ' ' + arClass('hint'), text: facts.join(' · ') }),
            arEl('p', { class: arClass('library-line'), 'data-line': 'roles' }, roleIds.length
                ? [deleted ? 'Roles that referred to it: ' : 'Granted by the roles: '].concat(arNameNodes(roleIds.map(function (id) {
                    return arRoleName(ctx, id);
                })))
                : [deleted ? 'No role referred to it.' : 'No role grants it.'])
        ];
        if (!deleted) {
            const who = arEl('ul', { class: arClass('list') + ' ' + arClass('library-users'), 'data-line': 'users' }, users.map(userLine));
            lines.push(arEl('p', { class: arClass('library-line') }, users.length ? 'Who can open it now (' + users.length + '):' : 'Nobody can open it now.'));
            if (users.length) {
                lines.push(who);
            }

            if (pending.length) {
                lines.push(arEl('p', { class: arClass('library-line') }, 'Given by the roles, not open to them yet (' + pending.length + '):'));
                lines.push(arEl('ul', { class: arClass('list') + ' ' + arClass('library-users'), 'data-line': 'not-opened' }, pending.map(userLine)));
            }
        }

        if (added.length) {
            lines.push(arEl('p', { class: arClass('library-line'), 'data-line': 'added' }, ['Added for: '].concat(arNameNodes(added))));
        }

        if (removed.length) {
            lines.push(arEl('p', { class: arClass('library-line'), 'data-line': 'removed' }, ['Removed for: '].concat(arNameNodes(removed))));
        }

        if (library.Locations && library.Locations.length) {
            lines.push(arEl('p', { class: arClass('library-line') + ' ' + arClass('hint'), text: 'Folders: ' + library.Locations.join(', ') }));
        }

        return arTvFocusable(arEl('div', { class: arClass('library'), 'data-library-id': arId(library.Id), 'data-state': library.State, 'data-ar-focus': 'library-' + arId(library.Id) }, [
            arEl('div', { class: arClass('library-head') }, [arEl('h4', { class: arClass('library-name') }, arName(library.Name)), badges])
        ].concat(warnings, lines, actions.length ? [arActions(actions)] : [])));
    }

    function renderByRole(active) {
        arClear(byRole);
        if (!ctx.roles.length) {
            byRole.appendChild(arHint('There are no roles yet.'));
            return;
        }

        byRole.appendChild(arEl('ul', { class: arClass('list') }, ctx.roles.map(function (role) {
            const mode = (role.Libraries && role.Libraries.Mode) || 'NotManaged';
            const granted = active.filter(function (l) {
                return arHasId(l.RoleIds, role.Id);
            });
            let text;
            if (mode === 'NotManaged') {
                text = 'doesn\'t manage libraries';
            } else if (mode === 'All') {
                text = 'every library, future ones too (' + arCount(granted.length, 'library', 'libraries') + ' now)';
            } else {
                text = (granted.length ? arNames(granted) : 'no library') + (mode === 'AllExcept' ? ' (every library but those it excepts, future ones too)' : '');
            }

            return arEl('li', { 'data-role-id': arId(role.Id) }, [arName(role.Name), ': ' + text]);
        })));
    }

    function renderByUser(active) {
        arClear(byUser);
        byUser.appendChild(arEl('ul', { class: arClass('list') }, ctx.users.map(function (u) {
            const opens = active.filter(function (l) {
                return (l.Users || []).some(function (x) {
                    return arSameId(x.UserId, u.UserId);
                });
            });
            // Why what they open now isn't what the roles give, as the library cards say it.
            const reasons = [];
            opens.forEach(function (l) {
                (l.Users || []).forEach(function (x) {
                    if (arSameId(x.UserId, u.UserId) && x.Mismatch && reasons.indexOf(x.Mismatch) < 0) {
                        reasons.push(x.Mismatch);
                    }
                });
            });
            const text = u.IsAdministrator ? ' (administrator): ' : ': ';
            const attributes = { 'data-user-id': arId(u.UserId), class: !opens.length && !u.IsAdministrator ? arClass('library-none') : null };
            if (reasons.length) {
                attributes['data-mismatch'] = reasons.join(' ');
                attributes.class = arClass('library-mismatch');
            }

            return arEl('li', attributes, [
                arName(u.Name), text + (opens.length ? arNames(opens) : 'no library') + (reasons.length ? ' (not as the roles give: ' + reasons.map(arMismatchText).join('; ') + ')' : '')
            ]);
        })));
    }

    function render() {
        arClear(list);
        const libraries = ctx.libraries || [];
        const active = libraries.filter(function (l) {
            return l.State !== 'Orphaned';
        });
        const deleted = libraries.length - active.length;
        const inNoRole = active.filter(function (l) {
            return l.InNoRole;
        }).length;
        const recreated = libraries.filter(function (l) {
            return l.Recreated;
        }).length;
        summary.textContent = arCount(active.length, 'library', 'libraries') + (inNoRole ? '; ' + inNoRole + ' in no role' : '')
            + (deleted ? '; ' + deleted + ' deleted with references kept' : '') + (recreated ? '; ' + recreated + ' created again' : '') + '.';
        libraries.forEach(function (library) {
            list.appendChild(card(library));
        });
        renderByRole(active);
        renderByUser(active);
    }

    return { element: element, render: render, onShow: onShow };
}
