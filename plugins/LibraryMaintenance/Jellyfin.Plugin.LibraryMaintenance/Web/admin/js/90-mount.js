// mount(view, params): the page. The tabs Duplicates | Settings | Diagnostics (WAI-ARIA tabs: the arrow
// keys, Home and End move between them on a keyboard; on the TV layout the D-pad moves the focus as everywhere in
// jellyfin-web and OK opens a tab). mount renders at once and loads its data at once: the stub calls it after the first
// viewshow has fired, so it listens to viewshow only for later shows of a view jellyfin-web kept.
function mount(view, params) {
    kit.injectStyles();
    const root = (view.querySelector && view.querySelector('#LibraryMaintenanceRoot')) || view;
    if (root.querySelector && root.querySelector('.' + lmClass('app'))) {
        return;
    }

    const note = view.querySelector ? view.querySelector('#LibraryMaintenanceScriptNote') : null;
    const ctx = {
        diagnostics: null,
        diagnosticsError: null,
        index: null,
        indexError: null,
        refresh: refresh,
        onSettings: applySettings
    };
    const tabs = {
        Duplicates: lmDuplicatesTab(ctx),
        Settings: lmSettingsTab(ctx),
        Diagnostics: lmDiagnosticsTab(ctx)
    };
    const buttons = {};
    const panels = {};
    const tablist = lmEl('div', { class: lmClass('tabs'), role: 'tablist', 'aria-label': 'Library Maintenance' });

    LM_TABS.forEach(function (tab) {
        const button = lmEl('button', {
            type: 'button',
            role: 'tab',
            id: 'LibraryMaintenanceTab' + tab.name,
            class: lmClass('tab'),
            'aria-controls': 'LibraryMaintenancePanel' + tab.name,
            'aria-selected': 'false',
            'data-tab': tab.name,
            text: tab.label,
            onclick: function () {
                select(tab.name, false);
            }
        });
        button.addEventListener('keydown', function (e) {
            if (kit.layout.isTv()) {
                return;
            }

            // Among the tabs shown: Duplicates is hidden while it is turned off.
            const shown = LM_TABS.filter(function (other) {
                return !buttons[other.name].hasAttribute('hidden');
            });
            const at = shown.indexOf(tab);
            const next = e.key === 'ArrowRight' ? (at + 1) % shown.length
                : e.key === 'ArrowLeft' ? (at + shown.length - 1) % shown.length
                    : e.key === 'Home' ? 0
                        : e.key === 'End' ? shown.length - 1
                            : -1;
            if (next >= 0) {
                e.preventDefault();
                select(shown[next].name, true);
            }
        });
        buttons[tab.name] = button;
        tablist.appendChild(button);
        panels[tab.name] = lmEl('div', {
            id: 'LibraryMaintenancePanel' + tab.name,
            class: lmClass('panel'),
            role: 'tabpanel',
            'aria-labelledby': 'LibraryMaintenanceTab' + tab.name,
            hidden: true
        }, tabs[tab.name].element);
    });

    function select(name, focus) {
        const tv = kit.layout.isTv();
        LM_TABS.forEach(function (tab) {
            const selected = tab.name === name;
            buttons[tab.name].setAttribute('aria-selected', selected ? 'true' : 'false');
            // A roving tabindex on a keyboard; every tab stays reachable with the D-pad on the TV layout.
            buttons[tab.name].setAttribute('tabindex', selected || tv ? '0' : '-1');
            if (selected) {
                panels[tab.name].removeAttribute('hidden');
            } else {
                panels[tab.name].setAttribute('hidden', '');
            }
        });
        if (focus) {
            buttons[name].focus();
        }
    }

    // Duplicates turned off in the settings hides its tab, and the Settings tab shows in its place.
    function applySettings(settings) {
        const button = buttons.Duplicates;
        if (settings.DuplicatesEnabled !== false) {
            button.removeAttribute('hidden');
            return;
        }

        button.setAttribute('hidden', '');
        if (button.getAttribute('aria-selected') === 'true') {
            select('Settings', false);
        }
    }

    function render() {
        LM_TABS.forEach(function (tab) {
            tabs[tab.name].update();
        });
    }

    let loading = null;
    function refresh() {
        if (loading) {
            return loading;
        }

        // The Diagnostics and the Duplicates index, together; GET …/Index never starts a build.
        loading = Promise.all([lmSettle(lmGet('Admin/Diagnostics')), lmSettle(lmGet('Duplicates/Index'))]).then(function (results) {
            loading = null;
            ctx.diagnostics = results[0].ok ? results[0].value : null;
            ctx.diagnosticsError = results[0].ok ? null : results[0].error;
            ctx.index = results[1].ok ? results[1].value : null;
            ctx.indexError = results[1].ok ? null : results[1].error;
            render();
        });
        return loading;
    }

    // A panel with no control of its own takes the focus itself (WAI-ARIA's tabpanel tabindex), so
    // the D-pad's Down from the tabs lands in it on the TV layout.
    LM_TABS.forEach(function (tab) {
        if (!panels[tab.name].querySelector('button, a[href], input, select, textarea')) {
            panels[tab.name].setAttribute('tabindex', '0');
            // The native class "focusable" through the kit (KIT_NATIVE_CLASSES), as before.
            kit.makeFocusable(panels[tab.name]);
        }
    });

    // focuscontainer-down: jellyfin-web's D-pad looks for Down's target inside the page only, so it reaches the selected
    // panel from every tab and never the drawer or the header beside it.
    const app = lmEl('div', { class: lmClass('app') + ' focuscontainer-down' }, [tablist].concat(LM_TABS.map(function (tab) {
        return panels[tab.name];
    })));
    if (note && note.parentNode) {
        note.parentNode.removeChild(note);
    }

    root.append(app);
    select(lmTabFromParams(params), false);
    render();
    refresh();
    tabs.Settings.load();

    view.addEventListener('viewshow', function (e) {
        // A view jellyfin-web kept and shows again: load its data again (the settings unless the form has changes).
        if (e && e.detail && e.detail.isRestored) {
            refresh();
            tabs.Settings.reload();
        }
    });
}
