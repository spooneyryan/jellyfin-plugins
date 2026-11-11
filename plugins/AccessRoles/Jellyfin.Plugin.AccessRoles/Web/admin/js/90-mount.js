// mount(view, params): the page. The banners of Diagnostics above the tabs Overview | Roles |
// Users | Libraries | Audit | Settings | Data | Diagnostics (WAI-ARIA tabs: the arrow keys, Home and End move between them
// on a keyboard; on the TV layout the D-pad moves the focus as everywhere in jellyfin-web and OK opens a tab). Diagnostics,
// Roles, Users and Libraries are loaded on mount and again when the view is shown from the cache or after a write; the
// Audit, Settings and the library renames when their tab is shown (onShow). The tab an admin last opened is remembered in
// this browser (kit.storage), the route's ?tab= wins, and the page opens on the Overview while the import
// is offered or the library sync is held after a restore.
const AR_TABS = [
    { name: 'Overview', label: 'Overview' },
    { name: 'Roles', label: 'Roles' },
    { name: 'Users', label: 'Users' },
    { name: 'Libraries', label: 'Libraries' },
    { name: 'Audit', label: 'Audit' },
    { name: 'Settings', label: 'Settings' },
    { name: 'Data', label: 'Data' },
    { name: 'Diagnostics', label: 'Diagnostics' }
];

const AR_TAB_KEY = 'adminTab';

function arKnownTab(name) {
    return AR_TABS.some(function (t) {
        return t.name === name;
    }) ? name : null;
}

function arRememberedTab() {
    try {
        return arKnownTab(kit.storage.get(AR_TAB_KEY, null));
    } catch (e) {
        return null;
    }
}

function arRememberTab(name) {
    try {
        kit.storage.set(AR_TAB_KEY, name);
    } catch (e) {
        // Storage is off (a private window, a blocked site): the page opens on the Overview next time.
    }
}

// Settles a promise into { ok, value | error } (Promise.allSettled is ES2020).
function arSettle(promise) {
    return promise.then(function (value) {
        return { ok: true, value: value };
    }, function (error) {
        return { ok: false, error: error };
    });
}

function mount(view, params) {
    kit.injectStyles();
    const page = view;
    const root = (page.querySelector && page.querySelector('#AccessRolesRoot')) || page;
    if (root.querySelector && root.querySelector('.' + arClass('app'))) {
        return;
    }

    const note = page.querySelector ? page.querySelector('#AccessRolesScriptNote') : null;
    const ctx = {
        root: root,
        roles: [],
        users: [],
        libraries: [],
        diagnostics: null,
        settings: null,
        readOnly: false,
        driftPolicy: '',
        selected: [],
        refresh: refresh,
        libraryName: libraryName,
        loadSettings: loadSettings
    };

    // GET Settings, kept for the Overview's chips and the Data tab; the Settings tab reads it again when shown.
    let settingsSeq = 0;
    function loadSettings() {
        const mine = ++settingsSeq;
        return arAdmin.settings().then(function (settings) {
            if (mine === settingsSeq) {
                ctx.settings = settings || {};
                if (ctx.diagnostics) {
                    overview.render(ctx.diagnostics);
                    dataTab.render();
                }
            }

            return settings || {};
        });
    }

    // A library's name: the server's list, then a role's reference (its name when it was picked), then the id.
    function libraryName(id) {
        const library = ctx.libraries.filter(function (l) {
            return arSameId(l.Id, id);
        })[0];
        if (library) {
            return library.Name + (library.State === 'Orphaned' ? ' (deleted)' : '');
        }

        for (let i = 0; i < ctx.roles.length; i++) {
            const ref = ((ctx.roles[i].Libraries && ctx.roles[i].Libraries.Refs) || []).filter(function (r) {
                return arSameId(r.Id, id);
            })[0];
            if (ref && (ref.Name || ref.NameAtSelection)) {
                return (ref.Name || ref.NameAtSelection) + (ref.Orphaned ? ' (deleted)' : '');
            }
        }

        return 'Library ' + arId(id);
    }

    const banners = arEl('div', { id: 'AccessRolesBanners', class: arClass('banners') });
    const overview = arOverview(ctx);
    const rolesTab = arRolesTab(ctx);
    const usersTab = arUsersTab(ctx);
    const librariesTab = arLibrariesTab(ctx);
    const auditTab = arAuditTab(ctx);
    const settingsTab = arSettingsTab(ctx);
    const dataTab = arDataTab(ctx);
    const diagnosticsTab = arDiagnosticsTab(ctx);
    const modules = { Overview: overview, Roles: rolesTab, Users: usersTab, Libraries: librariesTab, Audit: auditTab, Settings: settingsTab, Data: dataTab, Diagnostics: diagnosticsTab };
    const contents = {};
    AR_TABS.forEach(function (tab) {
        contents[tab.name] = modules[tab.name].element;
    });
    const loadErrors = { Roles: arEl('div'), Users: arEl('div'), Libraries: arEl('div') };
    const buttons = {};
    const panels = {};
    const tablist = arEl('div', { class: arClass('tabs'), role: 'tablist', 'aria-label': 'Access Roles' });
    let current = null;

    AR_TABS.forEach(function (tab, index) {
        const button = arEl('button', {
            type: 'button',
            role: 'tab',
            id: 'AccessRolesTab' + tab.name,
            class: arClass('tab'),
            'aria-controls': 'AccessRolesPanel' + tab.name,
            'aria-selected': 'false',
            'data-tab': tab.name,
            'data-ar-focus': 'tab-' + tab.name,
            text: tab.label,
            onclick: function () {
                select(tab.name, false, true);
            }
        });
        button.addEventListener('keydown', function (e) {
            if (kit.layout.isTv()) {
                return;
            }

            const next = e.key === 'ArrowRight' ? (index + 1) % AR_TABS.length
                : e.key === 'ArrowLeft' ? (index + AR_TABS.length - 1) % AR_TABS.length
                    : e.key === 'Home' ? 0
                        : e.key === 'End' ? AR_TABS.length - 1
                            : -1;
            if (next >= 0) {
                e.preventDefault();
                select(AR_TABS[next].name, true, true);
            }
        });
        buttons[tab.name] = button;
        tablist.appendChild(button);
        panels[tab.name] = arEl('div', {
            id: 'AccessRolesPanel' + tab.name,
            class: arClass('panel'),
            role: 'tabpanel',
            'aria-labelledby': 'AccessRolesTab' + tab.name,
            hidden: true
        }, [loadErrors[tab.name] || null, contents[tab.name]]);
    });

    // byAdmin: a tab the admin chose, remembered for the next visit.
    let chosen = false;
    // A tab another tab sends the admin to (the Data tab's restore, to the Overview's hold), the focus on its button.
    ctx.showTab = function (name) {
        select(name, true, true);
    };
    function select(name, focus, byAdmin) {
        current = name;
        if (byAdmin) {
            chosen = true;
            arRememberTab(name);
        }

        const tv = kit.layout.isTv();
        AR_TABS.forEach(function (tab) {
            const selected = tab.name === name;
            buttons[tab.name].setAttribute('aria-selected', selected ? 'true' : 'false');
            // On the TV layout every tab stays in the D-pad's reach (jellyfin-web skips tabindex="-1").
            buttons[tab.name].setAttribute('tabindex', selected || tv ? '0' : '-1');
            arShow(panels[tab.name], selected);
        });
        if (focus) {
            buttons[name].focus();
        }

        // A tab that reads its own data when shown (the Audit, the Settings, the library renames).
        const shown = modules[name];
        if (shown && typeof shown.onShow === 'function') {
            Promise.resolve().then(shown.onShow).then(null, function () {
                // The tab shows its own error.
            });
        }
    }

    const app = arEl('div', { class: arClass('app') }, [banners, tablist].concat(AR_TABS.map(function (tab) {
        return panels[tab.name];
    })));
    // The TV remote's OK on a checkbox or radio of the page (dialogs handle it themselves, 10-common.js).
    app.addEventListener('keydown', function (e) {
        arEnterKey(e);
    });
    root.appendChild(app);
    if (note) {
        arShow(note, false);
    }

    // The tab the route names (#/configurationpage?name=AccessRoles&tab=Users), else the one this browser remembers, else
    // the Overview; until the first status says the Overview needs the admin (the import, the hold after a restore).
    const routed = arKnownTab(params && params.tab);
    select(routed || arRememberedTab() || 'Overview', false);
    let firstStatus = true;
    let inertView = null;

    function failure(tab, what, result) {
        arClear(loadErrors[tab]);
        if (!result.ok) {
            loadErrors[tab].appendChild(arBanner('The ' + what + ' couldn\'t be loaded: ' + arErrorText(result.error) + ' Refresh on the Overview tab to try again.', 'error'));
        }
    }

    function apply(results) {
        const d = results[0];
        const roles = results[1];
        const users = results[2];
        const libraries = results[3];
        // Keep the focus where it was across the re-render: the element with the same data-ar-focus, else the tab.
        const active = document.activeElement;
        const keep = active && app.contains(active) && active.getAttribute ? active.getAttribute('data-ar-focus') : null;
        const wasInside = !!active && app.contains(active);

        // The emergency stop at the server's start (88-inert.js): the page shows only what is in effect and how to end it.
        const inert = arInertOf(d);
        if (inert) {
            if (!inertView) {
                inertView = arInertView(inert);
                Array.prototype.forEach.call(app.children, function (child) {
                    arShow(child, false);
                });
                app.appendChild(inertView);
            }

            return;
        }

        arClear(banners);
        if (d.ok) {
            ctx.diagnostics = d.value || {};
            ctx.readOnly = !!(ctx.diagnostics.Store && ctx.diagnostics.Store.ReadOnly);
            ctx.driftPolicy = (ctx.diagnostics.LibrarySync && ctx.diagnostics.LibrarySync.DriftPolicy) || '';
            arBanners(ctx.diagnostics).forEach(function (banner) {
                banners.appendChild(banner);
            });
            if (firstStatus && !routed && !chosen && current !== 'Overview' && (arImportOffered(ctx.diagnostics) || arRestoreHeld(ctx.diagnostics))) {
                select('Overview', false);
            }

            firstStatus = false;
        } else {
            overview.failed();
            diagnosticsTab.failed();
            dataTab.failed();
            banners.appendChild(arBanner('The status couldn\'t be loaded: ' + arErrorText(d.error), 'error'));
        }

        if (roles.ok) {
            ctx.roles = roles.value || [];
        }

        if (users.ok) {
            ctx.users = users.value || [];
        }

        if (libraries.ok) {
            ctx.libraries = libraries.value || [];
        }

        failure('Roles', roles.ok ? 'libraries' : 'roles', roles.ok ? libraries : roles);
        failure('Users', users.ok ? 'roles' : 'users', users.ok ? roles : users);
        const librariesFailed = !libraries.ok ? ['libraries', libraries] : (!roles.ok ? ['roles', roles] : ['users', users]);
        failure('Libraries', librariesFailed[0], librariesFailed[1]);
        rolesTab.render();
        usersTab.render();
        librariesTab.render();
        auditTab.render();
        if (d.ok) {
            // After the lists: the chips name users.
            overview.render(ctx.diagnostics);
            diagnosticsTab.render(ctx.diagnostics);
            dataTab.render();
        }

        if (wasInside && !app.contains(document.activeElement)) {
            const again = keep ? app.querySelector('[data-ar-focus="' + keep + '"]') : null;
            const target = again && !again.disabled && !again.closest('[hidden]') ? again : buttons[current];
            target.focus();
        }
    }

    let inFlight = null;
    let queued = null;
    let lastStart = 0;
    function load() {
        lastStart = Date.now();
        return Promise.all([
            arSettle(arAdmin.diagnostics()),
            arSettle(arAdmin.roles()),
            arSettle(arAdmin.users()),
            arSettle(arAdmin.libraries())
        ]).then(apply);
    }

    // Loads everything again. A refresh asked for while one runs waits for it and runs once more after it (a write's
    // result must show), however many were asked for meanwhile.
    function refresh() {
        if (inFlight) {
            if (!queued) {
                queued = inFlight.then(function () {
                    queued = null;
                    return refresh();
                });
            }

            return queued;
        }

        inFlight = load().then(function () {
            inFlight = null;
        }, function () {
            inFlight = null;
        });
        return inFlight;
    }

    // A view jellyfin-web restores from its cache gets only viewshow; the first viewshow may come before or after mount.
    if (page.addEventListener) {
        page.addEventListener('viewshow', function () {
            if (!inFlight && Date.now() - lastStart > 2000) {
                refresh();
            }
        });
    }

    refresh();
}
