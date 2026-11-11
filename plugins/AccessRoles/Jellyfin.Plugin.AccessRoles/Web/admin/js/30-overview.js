// The Overview tab: the status chips and the server-wide behaviours box, from GET
// /AccessRoles/Admin/Diagnostics and the users list, Import current setup while nobody is managed and the
// library sync's hold after a restore, element ids as the status page of 0.9.1 to 0.9.7 had
// them. The status tables of 0.9.1 to 0.9.10 are the Diagnostics tab's since 0.9.11 (86-diagnostics.js).

function arStatusSection(title, rows) {
    return arEl('div', { class: arClass('section') }, [arHeading(4, title), arTable(rows)]);
}

// The most names a chip lists; the rest are counted.
const AR_CHIP_NAMES = 8;

function arChipNames(users) {
    const nodes = arNameNodes(users.slice(0, AR_CHIP_NAMES).map(function (u) {
        return u.Name;
    }));
    if (users.length > AR_CHIP_NAMES) {
        nodes.push(' and ' + (users.length - AR_CHIP_NAMES) + ' more');
    }

    return nodes;
}

// Plugins that write users' policies too ("Other policy managers detected"), by the assemblies of the
// actions the rule table found beside Jellyfin's: sign-in providers (SSO, LDAP, OpenID, OAuth, Keycloak, Authelia) and user
// managers. By the name's words (split at . _ - and at a capital: SSO_Auth, LdapAuth), not its letters, so Lessons or
// Avoidcache isn't one; a word of one written as one with a sign-in word after it is one too (ldapauth,
// ssoauth, oauthprovider), but not any word that starts with one (Oauthor). Only a hint. No
// lookbehind, which older TV browsers can't parse.
const AR_POLICY_MANAGER_WORDS = ['sso', 'ldap', 'oidc', 'openid', 'saml', 'oauth', 'oauth2', 'keycloak', 'authelia'];
const AR_POLICY_MANAGER_SUFFIXES = ['auth', 'authentication', 'login', 'signin', 'connect', 'provider', 'client', 'sync', 'plugin'];

function arPolicyManagerWord(word) {
    return AR_POLICY_MANAGER_WORDS.indexOf(word) >= 0 || AR_POLICY_MANAGER_WORDS.some(function (manager) {
        return word.indexOf(manager) === 0 && AR_POLICY_MANAGER_SUFFIXES.indexOf(word.slice(manager.length)) >= 0;
    });
}

function arPolicyManagerName(assembly) {
    const words = String(assembly || '')
        .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
        .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
        .toLowerCase()
        .split(/[^a-z0-9]+/)
        .filter(Boolean);
    // A word, or two read as one (OAuth, OpenId and UserManager split at their capitals).
    return words.some(function (word, i) {
        const joined = word + (words[i + 1] || '');
        return arPolicyManagerWord(word)
            || AR_POLICY_MANAGER_WORDS.indexOf(joined) >= 0
            || /^usermanag/.test(word)
            || /^usermanag/.test(joined);
    });
}

function arOtherPolicyManagers(ruleTable) {
    return Object.keys((ruleTable && ruleTable.PluginActions) || {}).filter(arPolicyManagerName);
}

// The web client's script in this browser (the kit registers it as window.__jfkit[prefix]; the dashboard module doesn't).
function arWebClient() {
    try {
        return (typeof window !== 'undefined' && window.__jfkit && window.__jfkit[KIT_BUILD.prefix]) || null;
    } catch (e) {
        return null;
    }
}

// The status chips: what enforces, the web client in this browser, the library sync and its drift,
// who has no library, the administrators the locks never apply to, the managed users the sign-in screen shows, other
// plugins that write policies, Jellyfin Enhanced, the filter's errors and a degraded enforcement. Red is to put right.
// settings (optional): GET Settings, once a tab has read it.
function arChips(d, users, settings) {
    const chips = [];
    const enforcement = d.Enforcement || {};
    const table = d.RuleTable;
    const server = d.ActionManifest && d.ActionManifest.Server;
    const sync = d.LibrarySync;
    const filter = enforcement.Filter || {};
    const snapshot = enforcement.Snapshot || {};

    // Enforcement active: the filter registered and its rules bound to this server, and the action manifest check OK.
    if (enforcement.SettingsLocks === false) {
        chips.push(arChip('enforcement', 'error', 'Enforcement off (' + (enforcement.SettingsLocksOffBecause || 'switched off') + ')'));
    } else if (!table || table.Pending) {
        chips.push(arChip('enforcement', 'info', table ? 'Enforcement: the rule table is being checked' : 'Enforcement: the rule table isn\'t reported'));
    } else {
        const problems = [];
        if (table.Error) {
            problems.push(table.Error);
        } else {
            if (!table.FilterRegistered) {
                problems.push('the filter isn\'t registered');
            }

            if (!table.EnforcementActive) {
                problems.push('the rule table doesn\'t match this server');
            }
        }

        if (!server || !server.Matches) {
            problems.push('this server\'s API differs from the reviewed list');
        }

        chips.push(problems.length
            ? arChip('enforcement', 'error', 'Enforcement not fully active: ' + problems.join('; '))
            : arChip('enforcement', 'ok', 'Enforcement active'));
    }

    const web = arWebClient();
    if (!web) {
        chips.push(arChip('webui', 'warning', 'Web UI not injected in this browser' + (settings && settings.EnableWebUi === false ? ' (switched off on the Settings tab)' : '')));
    } else if (web.version && KIT_BUILD.version && web.version !== KIT_BUILD.version) {
        chips.push(arChip('webui', 'warning', 'Web UI injected, version ' + web.version + ' (this page is ' + KIT_BUILD.version + ': reload the browser)'));
    } else {
        chips.push(arChip('webui', 'ok', 'Web UI injected'));
    }

    if (!sync) {
        chips.push(arChip('sync', 'info', 'Library sync: not reported'));
    } else if (sync.State !== 'Active') {
        chips.push(arChip('sync', 'warning', 'Library sync paused (' + sync.PausedBecause + ')' + (sync.LastRun ? '; last sync ' + arDate(sync.LastRun.Utc) : '')));
    } else if (sync.LastRun) {
        chips.push(arChip('sync', sync.LastError ? 'warning' : 'ok', 'Last sync: ' + arDate(sync.LastRun.Utc) + ' (' + sync.LastRun.Trigger + ')' + (sync.LastError ? '; last error ' + sync.LastError : '')));
    } else {
        chips.push(arChip('sync', 'info', 'Last sync: not run yet'));
    }

    const drift = (sync && sync.DriftCount) || 0;
    chips.push(arChip('drift', drift ? 'warning' : 'ok', 'Drift: ' + arCount(drift, 'user') + (drift ? ' changed outside Access Roles (Users tab)' : '')));

    const none = users.filter(function (u) {
        return u.NoLibraries && !u.IsAdministrator;
    });
    chips.push(arChip('nolibraries', none.length ? 'error' : 'ok', none.length ? ['Users with no libraries: '].concat(arChipNames(none)) : 'No user is left with no libraries'));

    const admins = users.filter(function (u) {
        return u.IsAdministrator;
    });
    chips.push(arChip('administrators', 'info', ['Administrators (locks never apply): '].concat(admins.length ? arChipNames(admins) : ['none'])));

    const shown = users.filter(function (u) {
        return !u.IsAdministrator && !u.IsDisabled && !u.IsHidden && u.State === 'Managed';
    });
    chips.push(arChip('loginscreen', shown.length ? 'warning' : 'ok', shown.length
        ? ['Managed users shown on the login screen: '].concat(arChipNames(shown))
        : 'No managed user is shown on the login screen'));

    const managers = arOtherPolicyManagers(table);
    chips.push(arChip('policymanagers', managers.length ? 'warning' : 'ok', managers.length
        ? ['Other policy managers detected: '].concat(arNameNodes(managers))
        : 'No other policy manager detected'));

    const je = (table && table.JellyfinEnhanced) || {};
    chips.push(arChip('je', 'info', je.Installed ? 'Jellyfin Enhanced detected' : 'Jellyfin Enhanced not detected'));

    const errors = filter.FilterErrors || 0;
    chips.push(arChip('filtererrors', errors ? 'error' : 'ok', errors ? 'Filter errors: ' + errors + ' since the start (they fail closed; Diagnostics tab)' : 'No filter errors'));

    const degraded = enforcement.Degraded || null;
    const because = (degraded && degraded.Because) || filter.DegradedBecause || snapshot.DegradedBecause;
    chips.push(arChip('degraded', because ? 'error' : 'ok', because
        ? ['Enforcement degraded (' + because + '): '].concat(arDegradedWho(degraded, users))
        : 'Enforcement not degraded'));
    return chips;
}

// Whom Degraded mode locks: every non-admin, or the users the newest readable copy lists, by name (an id
// the server no longer has as itself).
function arDegradedWho(degraded, users) {
    if (!degraded) {
        return ['settings locked for the users it lists'];
    }

    if (degraded.EveryNonAdmin) {
        return ['settings locked for every non-admin'];
    }

    const ids = degraded.Users || [];
    const names = ids.map(function (id) {
        const user = (users || []).filter(function (u) {
            return arSameId(u.UserId, id);
        })[0];
        return user ? user.Name : 'user ' + arId(id);
    });
    return ['settings locked for '].concat(ids.length ? arNameNodes(names) : ['nobody']);
}

// The server-wide behaviours: what applies to every non-admin rather than per role,
// as Diagnostics' ServerWide says the settings make it apply.
function arServerWideItems(enforcement) {
    const wide = enforcement.ServerWide || {};
    const keys = wide.ConfigurationReadAllowList || [];
    const degraded = enforcement.Degraded;
    const items = degraded && degraded.EveryNonAdmin
        ? ['Degraded mode: every non-admin\'s server-enforced settings sections are locked, because nothing listing the managed users could be read (' + degraded.Because + ').']
        : [];
    return items.concat([
        wide.ConfigurationReads === 'Redact'
            ? 'Configuration reads: non-admins read every configuration key, with its known secrets stripped (Redact).'
            : 'Configuration reads: non-admins read only ' + (keys.length ? keys.join(', ') : 'no key') + ', with its known secrets stripped; every other key is refused.',
        wide.BlockNonAdminRestart
            ? 'Restart: administrators only, anonymous callers included. Shutdown: administrators only, as Jellyfin itself allows.'
            : 'Restart: not blocked; anyone who can reach the server can restart it, as on a stock 10.11.5. Shutdown: administrators only, as Jellyfin itself allows.',
        wide.TrimPublicUsers
            ? 'The public user list (the sign-in screen\'s) is trimmed to what the sign-in screen needs.'
            : 'The public user list is not trimmed: it shows every field, other users\' library ids included.',
        wide.LibraryHardening === 'Off'
            ? 'Library leak hardening: Off; search, people, suggestions and the other leaks show items of libraries a user can\'t open.'
            : 'Library leak hardening: ' + (wide.LibraryHardening || 'unknown') + '; search, people, suggestions and the other leaks show only the libraries a user can open.',
        wide.FirstLoadMode === 'ShowUntilKnown'
            ? 'First load: the web client shows every section until it knows a user\'s locks (ShowUntilKnown).'
            : 'First load: the web client hides the lockable sections of users who are or may be managed until it knows their locks (HideUntilKnown).'
    ]);
}

// The way out of Degraded mode (which replaced the earlier "until the server restarts"): it lasts
// across restarts until it is put right. A read-only file is put right by a readable copy put back (or the plugin version
// that wrote it); a store that started empty, a usable file after an earlier degraded start, or one an older copy replaced
// by an admin's confirmation below (Stop locking them).
function arDegradedText(degraded) {
    const who = degraded.EveryNonAdmin
        ? 'every non-admin (nothing listing the managed users could be read; this applies server-wide)'
        : 'the ' + arCount((degraded.Users || []).length, 'user') + ' it listed as managed' + (degraded.Source ? ' (read from ' + degraded.Source + ')' : '');
    const restored = degraded.Because === 'RestoredFromSnapshot' || degraded.Because === 'RecoveredFromBackup';
    const start = restored && degraded.FromEarlierStart
        ? 'Access Roles\' data file couldn\'t be read and an older copy took its place (' + degraded.Because + '), so every server-enforced settings section stays locked for '
        : degraded.FromEarlierStart
            ? 'Access Roles\' data file is usable again, but an earlier start found it couldn\'t be trusted (' + degraded.Because + '), so every server-enforced settings section stays locked for '
            : 'Access Roles\' data file can\'t be trusted (' + degraded.Because + '): every server-enforced settings section is locked for ';
    return start + who + ', on top of their roles\' locks, across restarts'
        + (degraded.CanResolve
            ? ', until you confirm below that their roles alone decide their settings locks.'
            : ', until a readable copy of the file is put back (or the plugin version that wrote it is installed) with the server stopped.');
}

// The warnings above the tabs, on every tab: the data file, the sync's pause, enforcement switched off (the page's empty
// and error states), a degraded snapshot, the rule table and the action manifest.
function arBanners(d) {
    const banners = [];
    const store = d.Store || {};
    const enforcement = d.Enforcement || {};
    if (store.Message) {
        banners.push(arBanner(store.Message));
    }

    if (store.ReadOnly) {
        banners.push(arBanner('Access Roles\' data is read-only, so roles and assignments can\'t be changed until it is put right.', 'error'));
    }

    if (enforcement.SettingsLocks === false) {
        banners.push(arBanner('Nothing is enforced; restrictions are only stored (' + (enforcement.SettingsLocksOffBecause || 'switched off') + ').', 'error'));
    }

    if (d.LibrarySync && d.LibrarySync.State !== 'Active') {
        banners.push(arBanner('The library sync is paused (' + d.LibrarySync.PausedBecause + '): no user\'s library access is written.'
            + (d.LibrarySync.PausedBecause === 'StoreRestored'
                ? ' The data file was restored from a snapshot (on the Data tab, or when it loaded), so the roles may be older than the users\' access: preview the sync on the Overview tab, then confirm.'
                : '')));
    }

    if (enforcement.Degraded) {
        banners.push(arBanner(arDegradedText(enforcement.Degraded), 'error'));
    }

    if (d.RuleTable && d.RuleTable.EnforcementActive === false) {
        banners.push(arBanner('The settings filter\'s rule table doesn\'t match this server (see the Rule table on the Diagnostics tab): some rules may not apply.'));
    }

    if (d.ActionManifest && d.ActionManifest.Server && !d.ActionManifest.Server.Matches) {
        banners.push(arBanner('This server\'s API differs from the reviewed list of actions; see the Diagnostics tab.'));
    }

    return banners;
}

// The Overview needs the admin's eye: the import is offered (nobody is managed yet) or the library sync is held after a
// restore. The page opens on it then, whatever tab it remembers.
function arImportOffered(d) {
    const store = (d && d.Store) || {};
    return !!d && !!d.Records && d.Records.Assignments === 0 && !store.ReadOnly;
}

// Focuses an element when nothing on the page has the focus (or a disabled button, or one a closed dialog took away),
// as after a refused write whose button stays off.
function arFocusIfLost(element) {
    const active = document.activeElement;
    if (!active || active === document.body || active.disabled || !active.isConnected) {
        element.focus();
    }
}

function arRestoreHeld(d) {
    const store = (d && d.Store) || {};
    return !!d && !!d.LibrarySync && d.LibrarySync.PausedBecause === 'StoreRestored' && !store.ReadOnly;
}

function arOverview(ctx) {
    const loading = arEl('p', { id: 'AccessRolesLoading', text: 'Loading the status…' });
    const chips = arEl('ul', { id: 'AccessRolesChips', class: arClass('chips'), 'aria-label': 'Status' });
    const wideIntro = arEl('p', { text: 'These apply to every user who isn\'t an administrator, managed or not, whatever their roles. Change them on the Settings tab.' });
    const wideList = arEl('ul', { class: arClass('list') });
    const wideNote = arEl('p', { class: arClass('hint') });
    const wide = arEl('div', { id: 'AccessRolesServerWide', class: arClass('section') + ' ' + arClass('server-wide'), hidden: true }, [
        arHeading(3, 'Server-wide behaviours'), wideIntro, wideList, wideNote
    ]);
    const refresh = arButton('Refresh', function () {
        arBusy(refresh, function () {
            return ctx.refresh();
        });
    });
    refresh.id = 'AccessRolesRefresh';

    // The hold after a restore: POST Sync?dryRun=true, then confirmRestore=true, which the server
    // refuses (409 DryRunRequired) without that dry run, with its PlanToken as ifPlan: a run
    // that would write anyone or anything else is refused (409 Changed), and the hold stays.
    let holdDry = null;
    const holdResult = arEl('div', { id: 'AccessRolesHoldResult', 'aria-live': 'polite' });
    const holdPreview = arButton('Preview the sync', function () {
        arBusy(holdPreview, previewHold);
    });
    holdPreview.id = 'AccessRolesHoldPreview';
    const holdConfirm = arButton('Confirm and resume the sync', function () {
        arBusy(holdConfirm, confirmHold, function () {
            return !!holdDry;
        });
    }, { variant: 'primary', disabled: true });
    holdConfirm.id = 'AccessRolesHoldConfirm';
    const hold = arEl('div', { id: 'AccessRolesRestoreHold', class: arClass('section'), hidden: true }, [
        arHeading(3, 'Library sync held after a restore'),
        arEl('p', { text: 'Access Roles\' data file was restored from a snapshot (on the Data tab, or when it loaded), so its roles and assignments may be older than the users\' library access. Nothing is written until you confirm. Preview what the sync would write first; you can still change the roles before you confirm.' }),
        arActions([holdPreview, holdConfirm]),
        holdResult
    ]);

    // Import current setup: POST ImportExisting, a dry run first. Users who see every library are
    // imported only when the box is ticked; Apply sends what the preview showed, with its PreviewToken as
    // ifPreview, so an import that would now do anything else writes nothing (409 Changed).
    let importEveryLibrary = false;
    let importDry = null;
    const importResult = arEl('div', { id: 'AccessRolesImportResult', 'aria-live': 'polite' });
    const everyLibrary = arCheckbox('Also manage users who see every library (gives them an "All libraries" role; later edits on the stock page will be overridden)', {
        onChange: function () {
            // Another choice needs its own preview before Apply.
            arClear(importResult);
            importDry = null;
            importApply.disabled = true;
        }
    });
    everyLibrary.input.id = 'AccessRolesImportEveryLibrary';
    const importPreview = arButton('Preview the import', function () {
        arBusy(importPreview, previewImport);
    });
    importPreview.id = 'AccessRolesImportPreview';
    const importApply = arButton('Apply the import', function () {
        arBusy(importApply, applyImport, function () {
            return !!importDry && (importDry.Assignments || []).length > 0;
        });
    }, { variant: 'primary', disabled: true });
    importApply.id = 'AccessRolesImportApply';
    const importBox = arEl('div', { id: 'AccessRolesImport', class: arClass('section'), hidden: true }, [
        arHeading(3, 'Import current setup'),
        arEl('p', { text: 'Access Roles isn\'t managing anyone yet. The import proposes one role per distinct library access among the users who aren\'t administrators, named after its libraries and with no locks, so that nobody\'s access changes. Users who see every library are left unmanaged unless you tick the box below. Preview it first; nothing is saved until you apply it.' }),
        everyLibrary.element,
        arActions([importPreview, importApply]),
        importResult
    ]);

    // Degraded mode's end: POST Degraded/Resolve, a dry run first that lists whom it locks, then the
    // confirmation with its PreviewToken as ifPreview (409 Changed when it locks anyone else since).
    let degradedDry = null;
    // Ended by the admin here: the box stays with its result until Degraded mode comes back.
    let degradedEnded = false;
    const degradedResult = arEl('div', { id: 'AccessRolesDegradedResult', 'aria-live': 'polite' });
    const degradedPreview = arButton('Show whom it locks', function () {
        arBusy(degradedPreview, previewDegraded);
    });
    degradedPreview.id = 'AccessRolesDegradedPreview';
    const degradedConfirm = arButton('Stop locking them', function () {
        arBusy(degradedConfirm, confirmDegraded, function () {
            return !!degradedDry;
        });
    }, { variant: 'primary', disabled: true });
    degradedConfirm.id = 'AccessRolesDegradedConfirm';
    const degradedActions = arActions([degradedPreview, degradedConfirm]);
    const degradedBox = arEl('div', { id: 'AccessRolesDegraded', class: arClass('section'), hidden: true }, [
        arHeading(3, 'Degraded mode'),
        arEl('p', { text: 'These users keep every server-enforced settings section locked because a start couldn\'t trust Access Roles\' data file. If the data file is right as it is now (for example you started over on purpose), stop locking them: their settings locks then come from their roles alone, and the native preference access the optional mirror turned off is put back for those whose roles no longer lock all five sections. Library access stays as it was last written.' }),
        degradedActions,
        degradedResult
    ]);

    // Release all locks before disabling: POST ReleaseLocks for everyone, a dry run first that lists whom it
    // releases and how, then the confirmation with its PreviewToken as ifPreview (409 Changed naming whom, nothing written).
    let releaseDry = null;
    const releaseResult = arEl('div', { id: 'AccessRolesReleaseResult', 'aria-live': 'polite' });
    const releaseLibraries = arCheckbox('Also restore each user\'s original library access (from before Access Roles managed them)', {
        onChange: function () {
            arClear(releaseResult);
            releaseDry = null;
            releaseConfirm.disabled = true;
        }
    });
    releaseLibraries.input.id = 'AccessRolesReleaseLibraries';
    const releasePreview = arButton('Release all locks…', function () {
        arBusy(releasePreview, previewRelease);
    });
    releasePreview.id = 'AccessRolesReleasePreview';
    const releaseConfirm = arButton('Release them', function () {
        arBusy(releaseConfirm, confirmRelease, function () {
            return !!releaseDry && (releaseDry.Released || []).length > 0;
        });
    }, { variant: 'primary', disabled: true });
    releaseConfirm.id = 'AccessRolesReleaseConfirm';
    const releaseBox = arEl('div', { id: 'AccessRolesRelease', class: arClass('section') }, [
        arHeading(3, 'Before disabling or deleting Access Roles'),
        arEl('p', { text: 'Release the locks here, or uninstall, while Access Roles is still enabled: disabling it on the Dashboard takes effect only at the next server restart (its locks keep working until then), Jellyfin 10.11 deletes a disabled plugin\'s folder at that restart, and uninstalling a disabled plugin can\'t put anything back, because Jellyfin doesn\'t call it. What it wrote to users\' own policies stays when it stops: the native preference access the optional mirror turned off (which has no checkbox on the Dashboard), and the library access its roles gave. Release all locks puts the preference access back on and, if you tick the box, each user\'s original library access; the users released become Excluded (their roles are kept; set them Managed again to undo). Uninstalling while it is enabled puts the preference access back on, not the library access. Preview it first.' }),
        releaseLibraries.element,
        arActions([releasePreview, releaseConfirm]),
        releaseResult
    ]);

    // Before enforcement goes back on (the install's check before enforcing): while it is off the sync writes
    // nothing, but its dry run plans the first run as enforcement would (POST Sync?dryRun=true, nothing written), so the
    // admin sees whom that run writes before ticking Enforce the roles. After Import current setup it writes nobody.
    const offResult = arEl('div', { id: 'AccessRolesEnforcementOffResult', 'aria-live': 'polite' });
    const offPreview = arButton('Preview the sync', function () {
        arBusy(offPreview, previewOff);
    });
    offPreview.id = 'AccessRolesEnforcementOffPreview';
    const offBox = arEl('div', { id: 'AccessRolesEnforcementOff', class: arClass('section'), hidden: true }, [
        arHeading(3, 'Before turning enforcement back on'),
        arEl('p', { text: 'Enforcement is off, so the library sync writes nothing. Its first run after Enforce the roles is ticked again on the Settings tab writes what this preview shows, at once. After Import current setup it must write nobody, and the Libraries tab must mark nobody as not as the roles give: if anyone is listed, check their roles before you turn enforcement on.' }),
        arActions([offPreview]),
        offResult
    ]);

    const element = arEl('div', { class: arClass('overview') }, [
        arEl('div', { class: arClass('section') }, [arHeading(3, 'Status'), loading, chips, arActions([refresh])]),
        degradedBox,
        hold,
        offBox,
        importBox,
        wide,
        releaseBox
    ]);

    // The import's query: the dry run, or the save of the shown dry run (sent) with its PreviewToken.
    function importQuery(sent) {
        const query = {};
        if (!sent) {
            query.dryRun = true;
        } else if (sent.PreviewToken) {
            query.ifPreview = sent.PreviewToken;
        }

        if (importEveryLibrary) {
            query.manageUsersWithEveryLibrary = true;
        }

        return query;
    }

    function renderImport(result) {
        arClear(importResult);
        importDry = result.DryRun ? result : null;
        const assigned = (result.Assignments || []).length;
        const roles = result.ProposedRoles || [];
        let summary;
        if (!result.DryRun) {
            summary = 'Imported: ' + roles.length + ' roles, ' + assigned + ' users. No library access was changed; the audit log has the details.';
        } else if (assigned === 0) {
            summary = 'There is nobody to import.';
        } else {
            summary = 'No change for ' + result.NoChange + ' of the ' + assigned + ' users the import would assign (' + roles.length + ' roles).';
        }

        importResult.appendChild(arEl('p', { text: summary }));
        if (roles.length) {
            importResult.appendChild(arStatusSection(result.DryRun ? 'Proposed roles' : 'Roles created', roles.map(function (role) {
                return [role.Name, (role.Mode === 'All' ? 'Every library, future ones too' : arNames(role.Libraries)) + ' (members: ' + arNames(role.Members) + ')'];
            })));
        }

        const changed = (result.EffectiveChanges || []).filter(function (row) {
            return (row.LibrariesAdded || []).length || (row.LibrariesRemoved || []).length || row.PolicyWrite;
        });
        if (changed.length) {
            importResult.appendChild(arStatusSection('Users whose library access would be written', changed.map(function (row) {
                return [row.Name, 'added: ' + (arNames(row.LibrariesAdded) || 'nothing') + '; removed: ' + (arNames(row.LibrariesRemoved) || 'nothing')];
            })));
        }

        if ((result.Warnings || []).length) {
            importResult.appendChild(arEl('div', { class: arClass('section') }, [
                arHeading(4, 'Warnings'),
                arList(result.Warnings.map(function (warning) {
                    return warning.Message;
                }))
            ]));
        }

        importApply.disabled = !result.DryRun || assigned === 0;
    }

    function importFailed(error) {
        arClear(importResult);
        importDry = null;
        importResult.appendChild(arBanner(arErrorText(error, 'The import could not be done. The server log has the details.'), 'error'));
        importApply.disabled = true;
    }

    function previewImport() {
        importEveryLibrary = everyLibrary.input.checked;
        importDry = null;
        importApply.disabled = true;
        return arAdmin.importExisting(importQuery(null)).then(renderImport, importFailed);
    }

    function applyImport() {
        const sent = importDry;
        if (!sent) {
            return null;
        }

        return kit.confirm({ title: 'Import current setup', message: 'Save the proposed roles and assignments? Nobody\'s library access changes.', confirmLabel: 'Apply' }).then(function (ok) {
            if (!ok) {
                return null;
            }

            return arAdmin.importExisting(importQuery(sent)).then(function (result) {
                renderImport(result);
                return ctx.refresh();
            }, function (error) {
                // Apply is off until the import is previewed again: the focus goes to Preview (a D-pad has no other way).
                importFailed(error);
                arFocusIfLost(importPreview);
            });
        });
    }

    function renderDegraded(result) {
        arClear(degradedResult);
        degradedDry = result.DryRun ? result : null;
        const names = (result.Users || []).map(function (u) {
            return u.Name || 'user ' + arId(u.UserId);
        });
        degradedResult.appendChild(arEl('p', {
            text: result.Resolved
                ? 'Degraded mode ended: their settings locks come from their roles alone now. The audit log has the details.'
                : result.EveryNonAdmin
                    ? 'It locks every non-admin\'s settings.'
                    : 'It locks the settings of ' + arCount(names.length, 'user') + (names.length ? ':' : '.')
        }));
        if (!result.Resolved && names.length) {
            degradedResult.appendChild(arList(names));
        }

        degradedConfirm.disabled = !result.DryRun;
    }

    function degradedFailed(error) {
        arClear(degradedResult);
        degradedDry = null;
        degradedConfirm.disabled = true;
        degradedResult.appendChild(arBanner(arErrorText(error, 'Degraded mode could not be ended. The server log has the details.'), 'error'));
    }

    function previewDegraded() {
        degradedDry = null;
        degradedConfirm.disabled = true;
        return arAdmin.resolveDegraded({ dryRun: true }).then(renderDegraded, degradedFailed);
    }

    function releaseQuery(sent) {
        const query = sent ? { ifPreview: sent.PreviewToken } : { dryRun: true };
        if (releaseLibraries.input.checked) {
            query.restoreLibraries = true;
        }

        return query;
    }

    function renderRelease(result) {
        arClear(releaseResult);
        releaseDry = result.DryRun ? result : null;
        const rows = result.Released || [];
        releaseResult.appendChild(arEl('p', {
            text: !result.DryRun
                ? 'Released ' + arCount(rows.length, 'user') + '; the audit log has the details.'
                : rows.length === 0
                    ? 'There is nothing to release: no user has native preference access the mirror turned off' + (result.RestoreLibraries ? ', nor original library access to restore.' : '.')
                    : 'Releasing writes ' + arCount(rows.length, 'user') + ':'
        }));
        if (result.StoreReadOnly && rows.length) {
            // A read-only data file releases too, but can't record anyone as Excluded.
            releaseResult.appendChild(arEl('p', { text: 'Access Roles\' data file is read-only, so nobody becomes Excluded: once it can be written again, Access Roles writes their locks again as their roles say, unless you release them again.' }));
        }

        if (rows.length) {
            releaseResult.appendChild(arList(rows.map(function (row) {
                const parts = [];
                parts.push(row.PreferenceAccess ? 'preference access back on' : 'preference access as it is');
                if (row.Libraries) {
                    parts.push('original library access: ' + (row.Libraries.EnableAllFolders ? 'every library' : (arNames((row.Libraries.EnabledFolders || []).map(ctx.libraryName)) || 'no library')));
                } else if (row.LibrariesSkipped === 'NoFullAdministrator') {
                    parts.push('library access kept: it would leave no administrator with every library');
                }

                if (row.Excluded) {
                    parts.push('becomes Excluded');
                }

                const done = !result.DryRun && (row.PreferenceOutcome === 'Failed' || (row.LibrariesOutcome && row.LibrariesOutcome !== 'Written' && row.LibrariesOutcome !== 'Unchanged'))
                    ? ' (not all written: ' + [row.PreferenceOutcome, row.LibrariesOutcome].filter(Boolean).join(', ') + ')'
                    : '';
                return (row.Name || 'user ' + arId(row.UserId)) + ': ' + parts.join('; ') + done;
            })));
        }

        releaseConfirm.disabled = !result.DryRun || rows.length === 0;
    }

    function releaseFailed(error) {
        arClear(releaseResult);
        releaseDry = null;
        releaseConfirm.disabled = true;
        releaseResult.appendChild(arBanner(arErrorText(error, 'The locks could not be released. The server log has the details.'), 'error'));
    }

    function previewRelease() {
        releaseDry = null;
        releaseConfirm.disabled = true;
        return arAdmin.releaseLocks(releaseQuery(null)).then(renderRelease, releaseFailed);
    }

    function confirmRelease() {
        const sent = releaseDry;
        if (!sent) {
            return null;
        }

        const after = sent.StoreReadOnly
            ? ' They stay managed: the data file is read-only.'
            : ' They become Excluded, and Access Roles stops writing their policies.';
        return kit.confirm({ title: 'Release all locks', message: 'Release the locks of ' + arCount((sent.Released || []).length, 'user') + '?' + after, confirmLabel: 'Release them' }).then(function (ok) {
            if (!ok) {
                return null;
            }

            releaseConfirm.disabled = true;
            return arAdmin.releaseLocks(releaseQuery(sent)).then(function (result) {
                renderRelease(result);
                return ctx.refresh();
            }, function (error) {
                releaseFailed(error);
                arFocusIfLost(releasePreview);
            });
        });
    }

    function confirmDegraded() {
        const sent = degradedDry;
        if (!sent) {
            return null;
        }

        return kit.confirm({ title: 'Degraded mode', message: 'Stop locking these users? Their settings locks then come from their roles alone; library access stays as it was last written.', confirmLabel: 'Stop locking them' }).then(function (ok) {
            if (!ok) {
                return null;
            }

            degradedConfirm.disabled = true;
            return arAdmin.resolveDegraded({ ifPreview: sent.PreviewToken }).then(function (result) {
                // The result stays in view and the focus goes to the Status section's Refresh, which stays (otherwise
                // the refresh hid the box and its result, and the focus fell to the page).
                degradedEnded = true;
                renderDegraded(result);
                arShow(degradedActions, false);
                return Promise.resolve(ctx.refresh()).then(function () {
                    // Its actions are hidden now: a focus left in the box (the dialog gives it back to Show whom it locks)
                    // goes to Refresh too.
                    const active = document.activeElement;
                    if (!active || active === document.body || !active.isConnected || degradedBox.contains(active)) {
                        refresh.focus();
                    }
                });
            }, function (error) {
                degradedFailed(error);
                arFocusIfLost(degradedPreview);
            });
        });
    }

    function renderHoldPreview(result) {
        arClear(holdResult);
        holdDry = result;
        // Only the rows the run writes: a Drift row under ReportOnly has no Target and is reported, not written.
        const rows = (result.Users || []).filter(arSyncWrites);
        holdResult.appendChild(arEl('p', {
            text: rows.length
                ? 'Confirming writes the library access of ' + rows.length + (rows.length === 1 ? ' user:' : ' users:')
                : 'Confirming writes nobody\'s library access.'
        }));
        if (rows.length) {
            holdResult.appendChild(arList(rows.map(function (row) {
                return row.Name + ': added ' + (arNames(row.Added) || 'nothing') + '; removed ' + (arNames(row.Removed) || 'nothing');
            })));
        }

        holdConfirm.disabled = false;
    }

    function holdFailed(error) {
        arClear(holdResult);
        holdDry = null;
        holdResult.appendChild(arBanner(arErrorText(error, 'The library sync could not be run. The server log has the details.'), 'error'));
    }

    function previewHold() {
        holdConfirm.disabled = true;
        holdDry = null;
        return arAdmin.sync({ dryRun: true }).then(renderHoldPreview, holdFailed);
    }

    // Resume: the shown dry run's PlanToken, nothing read again.
    function confirmHold() {
        const sent = holdDry;
        if (!sent) {
            return null;
        }

        return kit.confirm({ title: 'Library sync held after a restore', message: 'Resume the library sync? It writes what the preview showed.', confirmLabel: 'Resume' }).then(function (ok) {
            if (!ok) {
                return null;
            }

            const query = { confirmRestore: true };
            if (sent.PlanToken) {
                query.ifPlan = sent.PlanToken;
            }

            holdConfirm.disabled = true;
            return arAdmin.sync(query).then(function () {
                arClear(holdResult);
                holdDry = null;
                return ctx.refresh();
            }, function (error) {
                // The preview is gone (a 409 Changed: the run would write otherwise than it showed): Confirm stays off
                // until the sync is previewed again, and the focus goes to Preview.
                holdFailed(error);
                arFocusIfLost(holdPreview);
            });
        });
    }

    function renderOff(result) {
        arClear(offResult);
        const users = (result && result.Users) || [];
        const rows = users.filter(arSyncWrites);
        const others = users.filter(function (row) {
            return !arSyncWrites(row);
        });
        offResult.appendChild(arEl('p', {
            'data-writes': String(rows.length),
            text: rows.length
                ? 'Its first run writes the library access of ' + arCount(rows.length, 'user') + ':'
                : 'Its first run writes nobody\'s library access' + (others.length ? '.' : ', and leaves nobody out.')
        }));
        if (rows.length) {
            offResult.appendChild(arList(rows.map(function (row) {
                return row.Name + ': added ' + (arNames(row.Added) || 'nothing') + '; removed ' + (arNames(row.Removed) || 'nothing');
            })));
        }

        if (others.length) {
            offResult.appendChild(arEl('p', { text: 'Left out or only reported (' + others.length + '):' }));
            offResult.appendChild(arList(others.map(function (row) {
                return row.Name + ': ' + row.Action + (row.Reason ? ' (' + row.Reason + ')' : '');
            })));
        }
    }

    function previewOff() {
        arClear(offResult);
        return arAdmin.sync({ dryRun: true }).then(renderOff, function (error) {
            arClear(offResult);
            offResult.appendChild(arBanner(arErrorText(error, 'The library sync could not be previewed. The server log has the details.'), 'error'));
        });
    }

    function render(d) {
        arClear(chips);
        arShow(loading, false);
        const enforcement = d.Enforcement || {};
        arChips(d, ctx.users, ctx.settings).forEach(function (chip) {
            chips.appendChild(chip);
        });
        arClear(wideList);
        arServerWideItems(enforcement).forEach(function (text) {
            wideList.appendChild(arEl('li', { text: text }));
        });
        const totals = (enforcement.Blocked || {}).Totals || {};
        wideNote.textContent = (enforcement.Hardening === false ? 'Not applied now: ' + (enforcement.SettingsLocksOffBecause || 'enforcement is off') + '. ' : '')
            + 'Since the server started they hid ' + arCount(totals.Hidden || 0, 'value') + '.';
        arShow(wide, true);

        const resolvable = !!(enforcement.Degraded && enforcement.Degraded.CanResolve);
        const degradedShown = resolvable || degradedEnded;
        if (degradedBox.hasAttribute('hidden') === degradedShown || (resolvable && degradedEnded)) {
            arShow(degradedBox, degradedShown);
            if (resolvable || !degradedEnded) {
                degradedEnded = false;
                arShow(degradedActions, true);
                arClear(degradedResult);
                degradedDry = null;
                degradedConfirm.disabled = true;
            }
        }

        const held = arRestoreHeld(d);
        if (hold.hasAttribute('hidden') === held) {
            arShow(hold, held);
            arClear(holdResult);
            holdDry = null;
            holdConfirm.disabled = true;
        }

        const off = !held && !!d.LibrarySync && d.LibrarySync.PausedBecause === 'EnforcementDisabled';
        if (offBox.hasAttribute('hidden') === off) {
            arShow(offBox, off);
            arClear(offResult);
        }

        const importable = arImportOffered(d);
        if (importBox.hasAttribute('hidden') === importable) {
            arShow(importBox, importable);
            if (importable) {
                arClear(importResult);
                importDry = null;
                importApply.disabled = true;
            }
        }
    }

    function failed() {
        arShow(loading, true);
        loading.textContent = 'The status couldn\'t be loaded. The server log has the details.';
    }

    return { element: element, render: render, failed: failed };
}
