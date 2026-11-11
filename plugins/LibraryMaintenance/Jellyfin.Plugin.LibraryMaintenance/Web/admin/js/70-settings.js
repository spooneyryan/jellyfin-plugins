// The Settings tab: the 1.0 fields, and since 1.0.2 the web client's and
// Move's switches, saved for every administrator through POST …/Admin/Settings, and the settings' export and import.
// Every save is a dry run first. A change to the libraries the index reads or the
// folders it leaves out opens a dialog with what it does to the index (the movies before and after, each library, each
// folder) and is saved only from there; any other change is saved at once. The saved settings' version goes with every
// request, so a save over settings changed since (in another window, by another administrator) is refused (409) and the
// tab offers to reload them. An import is the same dry run of a file's settings, then the same dialog. Every value from the
// server or from the file is written as text.
const LM_SETTINGS_MODES = [
    { value: 'TitleAndYear', text: 'Title + Year' },
    { value: 'Title', text: 'Title only' }
];

const LM_SETTINGS_UNKNOWN_YEAR = [
    { value: 'MatchUnknownOnly', text: 'Only with other movies with no year' },
    { value: 'MatchAny', text: 'With the title\'s one year, when it has one' }
];

const LM_SETTINGS_LANGUAGES = { En: 'English', Fr: 'French', De: 'German', Es: 'Spanish', It: 'Italian', Nl: 'Dutch' };

const LM_SETTINGS_MAX_WAIT = 30;

// The code of the 409 of a save or import over settings saved since its version: the page tells it from a newer
// schema's 409 (NewerSchema) by it, never by its words (the third review).
const LM_SETTINGS_CHANGED = 'SettingsChanged';

// A field: its label, the control (which gets the id) and the hint under it, which describes the control.
function lmSettingsField(id, label, control, hint, extras) {
    control.id = id;
    const children = [
        lmEl('label', { for: id, text: label }),
        lmEl('div', { class: KIT_BUILD.prefix + '-field-row' }, [control].concat(extras || []))
    ];
    if (hint) {
        control.setAttribute('aria-describedby', id + 'Hint');
        children.push(lmEl('p', { id: id + 'Hint', class: lmClass('hint'), text: hint }));
    }

    return lmEl('div', { class: KIT_BUILD.prefix + '-field' }, children);
}

// A field of toggle buttons (lmToggles' group, named by its own label), in a slot its buttons are made again in on every
// load.
function lmSettingsGroup(id, label, slot, hint, note) {
    return lmEl('div', { class: KIT_BUILD.prefix + '-field' }, [
        lmEl('span', { id: id + 'Label', class: lmClass('field-label'), text: label }),
        slot,
        note || null,
        hint ? lmEl('p', { id: id + 'Hint', class: lmClass('hint'), text: hint }) : null
    ]);
}

// An on/off setting: a button with the switch role (a button, so the D-pad's OK and a keyboard's Enter press it, which a
// checkbox doesn't take), saying On or Off.
function lmSwitch(onChange) {
    let on = false;
    const button = kit.button('Off', {
        className: lmClass('switch'),
        onClick: function () {
            set(!on);
            onChange();
        }
    });
    button.setAttribute('role', 'switch');

    function set(value) {
        on = !!value;
        button.setAttribute('aria-checked', on ? 'true' : 'false');
        button.textContent = on ? 'On' : 'Off';
    }

    set(false);
    return { element: button, get: function () { return on; }, set: set };
}

// A select's options and its saved value, which has an option of its own when this version doesn't know it (a newer build
// saved it): a save never changes a value the administrator didn't change.
function lmSettingsOptions(select, choices, value) {
    lmClear(select);
    choices.forEach(function (choice) {
        select.append(lmEl('option', { value: choice.value, text: choice.text }));
    });
    const known = choices.some(function (choice) {
        return choice.value === value;
    });
    if (value && !known) {
        select.append(lmEl('option', { value: value, text: value + ' (set by a newer version)' }));
    }

    select.value = value || choices[0].value;
}

function lmLines(text) {
    return String(text || '').split(/\r?\n/).map(function (line) {
        return line.trim();
    }).filter(function (line) {
        return line.length > 0;
    });
}

// Ids in one form: Jellyfin writes them as 32 hex digits; a file may hold them with dashes.
function lmIdKey(id) {
    return String(id || '').replace(/-/g, '').toLowerCase();
}

// A file's text: Blob.text, or a FileReader where a TV's browser has no Blob.text.
function lmReadText(file) {
    if (typeof file.text === 'function') {
        return file.text();
    }

    return new Promise(function (resolve, reject) {
        const reader = new FileReader();
        reader.onload = function () {
            resolve(String(reader.result || ''));
        };
        reader.onerror = function () {
            reject(new Error('The file couldn\'t be read.'));
        };
        reader.readAsText(file);
    });
}

function lmSettingsLibraryLabel(library) {
    if (library.Missing) {
        return 'A library no longer on this server';
    }

    return String(library.Name || '') + (library.Kind === 'mixed' ? ' (mixed content)' : '') + (library.Enabled === false ? ' (turned off in Jellyfin)' : '');
}

// The preview of a change to the index's scope: the movies before and after, each library that joins, leaves
// or stays, and each folder added or removed, with whether the server finds it.
function lmSettingsPreview(preview) {
    const parts = [lmEl('h4', { class: lmClass('heading'), text: 'The index' })];
    const before = preview.MoviesBefore;
    const after = preview.MoviesAfter;
    if (preview.Counted && typeof after === 'number') {
        parts.push(lmEl('p', {
            id: 'LibraryMaintenanceSettingsPreviewMovies',
            text: (typeof before === 'number' ? 'It holds ' + lmCount(before, 'movie', 'movies') + ' now; with these settings it would hold ' : 'With these settings it would hold ')
                + lmCount(after, 'movie', 'movies') + '.'
        }));
        if (preview.ExcludedAfter || preview.ExcludedBefore) {
            parts.push(lmEl('p', {
                id: 'LibraryMaintenanceSettingsPreviewExcluded',
                text: 'The excluded folders would leave out ' + lmCount(preview.ExcludedAfter, 'movie', 'movies')
                    + (typeof preview.ExcludedBefore === 'number' ? ' (' + Number(preview.ExcludedBefore).toLocaleString() + ' now)' : '') + '.'
            }));
        }
    }

    if (preview.Note) {
        parts.push(lmEl('p', { id: 'LibraryMaintenanceSettingsPreviewNote', class: lmClass('hint'), text: preview.Note }));
    }

    const libraries = preview.Libraries || [];
    if (libraries.length > 0) {
        parts.push(lmEl('table', { id: 'LibraryMaintenanceSettingsPreviewLibraries', class: lmClass('table') }, lmEl('tbody', null, libraries.map(function (library) {
            const was = library.MoviesBefore;
            const will = library.MoviesAfter;
            const text = library.Change === 'Joins' ? 'Joins' + (typeof will === 'number' ? ': ' + lmCount(will, 'movie', 'movies') : '')
                : library.Change === 'Leaves' ? 'Leaves' + (typeof was === 'number' ? ' (' + lmCount(was, 'movie', 'movies') + ' now)' : '')
                    : 'Stays' + (typeof will === 'number' ? ': ' + (typeof was === 'number' ? Number(was).toLocaleString() + ' → ' : '') + lmCount(will, 'movie', 'movies') : '');
            return lmEl('tr', null, [lmEl('th', { scope: 'row', text: String(library.Name || '') }), lmEl('td', { text: text })]);
        }))));
    }

    const folders = preview.Folders || [];
    if (folders.length > 0) {
        parts.push(lmEl('table', { id: 'LibraryMaintenanceSettingsPreviewFolders', class: lmClass('table') }, lmEl('tbody', null, folders.map(function (folder) {
            const found = folder.Exists === true ? 'found on the server'
                : folder.Exists === false ? 'not found on the server: check the path'
                    : folder.NotChecked === 'Network' ? 'a network folder: not checked, so nothing connects to it before you save'
                        : folder.NotChecked === 'Device' ? 'a device path, not a folder path: not checked'
                            : 'not checked: no answer within 5 seconds';
            return lmEl('tr', null, [
                lmEl('th', { scope: 'row', text: folder.Change === 'Removed' ? 'Excluded no more' : 'Excluded' }),
                lmEl('td', { text: String(folder.Path || '') + ' (' + found + ')' })
            ]);
        }))));
    }

    parts.push(lmEl('p', { class: lmClass('hint'), text: 'The index is built again when these settings are saved; until then the Duplicates tab shows the one it has.' }));
    return parts;
}

// A dry run's report as text: where an imported file came from, what changes, the preview, and what was left out.
function lmSettingsReport(report, kind) {
    const parts = [];
    const source = report.Source;
    if (kind === 'import' && source) {
        const when = kit.format.dateTime(source.ExportedUtc) || String(source.ExportedUtc || '');
        parts.push(lmEl('p', {
            id: 'LibraryMaintenanceSettingsSource',
            text: 'The file holds the settings of Library Maintenance ' + String(source.PluginVersion || '') + ', exported ' + when
                + (source.SameServer === false ? ' on another server' : source.SameServer === true ? ' on this server' : '') + '.'
        }));
    }

    parts.push(
        lmEl('h4', { class: lmClass('heading'), text: 'What changes' }),
        lmEl('table', { id: 'LibraryMaintenanceSettingsChanges', class: lmClass('table') }, lmEl('tbody', null, (report.Changes || []).map(function (change) {
            return lmEl('tr', null, [
                lmEl('th', { scope: 'row', text: String(change.Label || change.Field || '') }),
                lmEl('td', { text: String(change.From || '') + ' → ' + String(change.To || '') })
            ]);
        }))));
    if (report.Preview) {
        parts.push.apply(parts, lmSettingsPreview(report.Preview));
    }

    parts.push.apply(parts, lmSettingsWarnings(report.Warnings));
    return parts;
}

function lmSettingsWarnings(warnings) {
    if (!warnings || warnings.length === 0) {
        return [];
    }

    return [
        lmEl('h4', { class: lmClass('heading'), text: 'Left out or kept' }),
        lmEl('ul', { id: 'LibraryMaintenanceSettingsWarnings', class: lmClass('list') }, warnings.map(function (warning) {
            return lmEl('li', { text: String(warning) });
        }))
    ];
}

// The file a browser saves: the export's text under the server's name for it.
function lmDownload(name, text) {
    const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
    const link = lmEl('a', { href: url, download: name, hidden: true });
    document.body.append(link);
    link.click();
    link.remove();
    window.setTimeout(function () {
        URL.revokeObjectURL(url);
    }, 10000);
}

function lmSettingsTab(ctx) {
    let view = null;
    let loading = null;
    let busy = false;
    let saved = null;
    let chosenLibraries = [];
    let chosenLanguages = [];
    let libraryToggles = null;
    let languageToggles = null;

    const message = lmEl('div', { id: 'LibraryMaintenanceSettingsMessage' });
    const enabled = lmSwitch(changed);
    const mode = kit.select({}, []);
    const unknownYear = kit.select({}, []);
    const librarySlot = lmEl('div');
    const libraryNote = lmEl('p', { id: 'LibraryMaintenanceSettingsLibrariesNote', class: lmClass('hint') });
    const folders = kit.textarea({ rows: '3', spellcheck: 'false', autocomplete: 'off', placeholder: 'D:\\Movies\\Trailers' });
    const strip = lmSwitch(changed);
    const phrases = kit.textarea({ rows: '4', spellcheck: 'false', autocomplete: 'off' });
    const defaultPhrases = kit.button('Default phrases', {
        title: 'Put the default edition phrases in the field',
        onClick: function () {
            if (view) {
                phrases.value = (view.DefaultEditionPhrases || []).join('\n');
                changed();
            }
        }
    });
    defaultPhrases.id = 'LibraryMaintenanceSettingsDefaultPhrases';
    const languageSlot = lmEl('div');
    const warm = lmSwitch(changed);
    const wait = kit.input({ type: 'number', min: '0', max: String(LM_SETTINGS_MAX_WAIT), step: '1', inputmode: 'numeric', class: lmClass('number') });
    const webUi = lmSwitch(changed);
    const move = lmSwitch(changed);
    const itemMenu = lmSwitch(changed);
    const anchorReports = lmSwitch(changed);
    const save = kit.button('Save', { variant: 'primary', onClick: onSave });
    save.id = 'LibraryMaintenanceSettingsSave';
    const discard = kit.button('Discard changes', {
        onClick: function () {
            if (!busy && view && discard.getAttribute('aria-disabled') !== 'true') {
                fill();
                lmClear(message);
            }
        }
    });
    discard.id = 'LibraryMaintenanceSettingsDiscard';
    [mode, unknownYear].forEach(function (select) {
        select.addEventListener('change', changed);
    });
    [folders, phrases, wait].forEach(function (field) {
        field.addEventListener('input', changed);
    });

    const transfer = lmEl('div', { id: 'LibraryMaintenanceSettingsTransfer' });
    const exportButton = kit.button('Export the settings', { onClick: onExport });
    exportButton.id = 'LibraryMaintenanceSettingsExport';
    // The file input is hidden; a button opens it, so the keyboard and the D-pad reach the same control as the mouse.
    const file = lmEl('input', { type: 'file', id: 'LibraryMaintenanceSettingsImportFile', accept: '.json,application/json', hidden: true, tabindex: '-1', 'aria-hidden': 'true' });
    const importButton = kit.button('Import from a file…', {
        onClick: function () {
            if (!busy && view) {
                file.value = '';
                file.click();
            }
        }
    });
    importButton.id = 'LibraryMaintenanceSettingsImport';
    file.addEventListener('change', function () {
        const chosen = file.files && file.files[0];
        if (chosen) {
            onImport(chosen);
        }
    });

    const form = lmEl('div', { id: 'LibraryMaintenanceSettingsForm', hidden: true }, [
        lmEl('h4', { class: lmClass('heading'), text: 'Duplicates' }),
        lmSettingsField('LibraryMaintenanceSettingsDuplicatesEnabled', 'Find duplicate movies', enabled.element, 'Off hides the Duplicates tab, and its API answers 404.'),
        lmSettingsField('LibraryMaintenanceSettingsMode', 'Default match mode', mode, 'The mode the Duplicates tab opens in, until an administrator picks one there.'),
        lmSettingsField('LibraryMaintenanceSettingsUnknownYear', 'A movie with no year matches', unknownYear, 'In Title + Year.'),
        lmEl('h4', { class: lmClass('heading'), text: 'What the index reads' }),
        lmSettingsGroup('LibraryMaintenanceSettingsLibraries', 'Libraries', librarySlot, 'The movie and mixed libraries the index reads. TV shows and music videos are never read.', libraryNote),
        lmSettingsField('LibraryMaintenanceSettingsFolders', 'Excluded folders', folders,
            'One full folder path on the server per line; the movies under it are left out. If the Bookmarks clip folder is inside a movie library, add it here.'),
        lmEl('h4', { class: lmClass('heading'), text: 'Titles' }),
        lmSettingsField('LibraryMaintenanceSettingsStripEditions', 'Cut edition suffixes', strip.element,
            'An edition phrase at the end of a title (Heat - Director\'s Cut) is cut before titles are compared, and shown as the edition.'),
        lmSettingsField('LibraryMaintenanceSettingsPhrases', 'Edition phrases', phrases, 'One per line, compared ignoring case.', [defaultPhrases]),
        lmSettingsGroup('LibraryMaintenanceSettingsLanguages', 'Leading articles dropped', languageSlot,
            'A title loses the article it starts with before titles are compared. A first word that only looks like one goes too: with French or Spanish, "La La Land" is compared as "La Land".'),
        lmEl('h4', { class: lmClass('heading'), text: 'Building the index' }),
        lmSettingsField('LibraryMaintenanceSettingsWarm', 'Build the index after every library scan', warm.element,
            'Off: after a scan the index is built when the Duplicates tab is next opened, unless it was used in the last 24 hours.'),
        lmSettingsField('LibraryMaintenanceSettingsWait', 'Wait for a build (seconds)', wait,
            'How long a request for the groups waits for a running build before it answers with the index it has: 0 to 30.'),
        lmEl('h4', { class: lmClass('heading'), text: 'Web client and Move' }),
        lmEl('p', { id: 'LibraryMaintenanceSettingsWebNote', class: lmClass('hint'), text: 'To turn these off, use these switches, not Disable on Jellyfin\'s Plugins page: on Jellyfin 10.11.5 a disabled plugin is deleted at the next start.' }),
        lmSettingsField('LibraryMaintenanceSettingsWebUi', 'Add Library Maintenance to the web client', webUi.element,
            'Its script in every page of the web client, which brings Move\'s entry in the item menu. Off: no script; this page and the API still work. A browser takes a change when it next loads the web client.'),
        lmSettingsField('LibraryMaintenanceSettingsMove', 'Move media', move.element,
            'This version only previews a move: nothing is moved. Off hides Move\'s entry, and its API answers 403.'),
        lmSettingsField('LibraryMaintenanceSettingsItemMenu', 'Move in the item menu', itemMenu.element,
            '"Move media to new location" in an administrator\'s item menu of a movie, an episode or a music video (never on the TV layout).'),
        lmSettingsField('LibraryMaintenanceSettingsAnchorReports', 'Report missing web client elements', anchorReports.element,
            'A browser tells the server when a part of the web client the script needs is missing; Diagnostics lists them, and the log has one warning for each. Off: browsers still warn in their console.'),
        lmEl('div', { class: lmClass('actions') }, [save, discard])
    ]);

    const element = lmEl('div', { class: lmClass('section') }, [
        lmEl('h3', { class: lmClass('heading'), text: 'Settings' }),
        lmEl('p', { class: lmClass('hint'), text: 'These settings are the server\'s, for every administrator. A change to the libraries or the excluded folders shows what it does to the index before it is saved.' }),
        message,
        form,
        lmEl('h4', { class: lmClass('heading'), text: 'Export and import' }),
        lmEl('p', { class: lmClass('hint'), text: 'The export is a file of the saved settings, with the excluded folders\' paths. An import shows what it changes before anything is saved.' }),
        lmEl('div', { class: lmClass('actions') }, [exportButton, importButton, file]),
        transfer
    ]);

    function say(area, kind, text, action) {
        lmClear(area);
        area.append(kit.notice({ kind: kind, text: text, action: action }));
    }

    function fail(area, error) {
        // The version check (409): offer to read the settings again, which drops this page's changes.
        const stale = !!error && error.status === 409 && error.code === LM_SETTINGS_CHANGED;
        say(area, 'error', lmErrorText(error), stale ? {
            label: 'Reload the settings',
            onClick: function () {
                lmClear(area);
                load();
            }
        } : undefined);
    }

    function setBusy(value) {
        busy = value;
        [save, exportButton, importButton].forEach(function (button) {
            button.setAttribute('aria-disabled', busy || (!view && button !== exportButton) ? 'true' : 'false');
        });
        discard.setAttribute('aria-disabled', busy || !isDirty() ? 'true' : 'false');
    }

    // One request of the tab's at a time; a failure is said in the area of what failed.
    function run(area, work) {
        if (busy) {
            return Promise.resolve();
        }

        setBusy(true);
        return Promise.resolve().then(work).then(null, function (error) {
            fail(area, error);
        }).then(function () {
            setBusy(false);
        });
    }

    function load() {
        if (loading) {
            return loading;
        }

        if (!view) {
            say(message, 'info', 'Loading…');
        }

        loading = lmSettle(lmGet('Admin/Settings')).then(function (result) {
            loading = null;
            if (result.ok) {
                view = result.value || {};
                fill();
                if (message.textContent === 'Loading…') {
                    lmClear(message);
                }

                ctx.onSettings(view.Settings || {});
            } else {
                say(message, 'error', lmErrorText(result.error), { label: 'Retry', onClick: load });
            }

            if (view) {
                form.removeAttribute('hidden');
            }

            setBusy(busy);
        });
        return loading;
    }

    function fill() {
        const s = view.Settings || {};
        enabled.set(s.DuplicatesEnabled !== false);
        lmSettingsOptions(mode, LM_SETTINGS_MODES, s.DefaultMatchMode);
        lmSettingsOptions(unknownYear, LM_SETTINGS_UNKNOWN_YEAR, s.UnknownYearPolicy);
        chosenLibraries = (s.IncludedLibraryIds || []).map(lmIdKey);
        buildLibraries();
        folders.value = (s.ExcludedPathPrefixes || []).join('\n');
        strip.set(s.StripEditionSuffixes !== false);
        phrases.value = (s.EditionPhrases || []).join('\n');
        chosenLanguages = (s.ArticleLanguages || []).slice();
        buildLanguages();
        warm.set(s.WarmIndexAfterScan !== false);
        wait.value = String(typeof s.RequestWaitSeconds === 'number' ? s.RequestWaitSeconds : 5);
        webUi.set(s.EnableWebUi !== false);
        move.set(s.MoveEnabled !== false);
        itemMenu.set(s.ShowInItemMenu !== false);
        anchorReports.set(s.ReportWebAnchorProblems !== false);
        saved = JSON.stringify(values());
        changed();
    }

    function toggle(list, value) {
        const at = list.indexOf(value);
        if (at >= 0) {
            list.splice(at, 1);
        } else {
            list.push(value);
        }
    }

    function buildLibraries() {
        const libraries = view.Libraries || [];
        lmClear(librarySlot);
        libraryToggles = lmToggles('LibraryMaintenanceSettingsLibrariesToggles', 'Libraries', libraries.map(function (library) {
            return { value: lmIdKey(library.Id), label: lmSettingsLibraryLabel(library) };
        }), function (value) {
            toggle(chosenLibraries, value);
            changed();
        });
        librarySlot.append(libraries.length > 0 ? libraryToggles.element : lmEl('span', { class: lmClass('none'), text: 'This server has no movie or mixed library.' }));
    }

    function buildLanguages() {
        const known = Object.keys(view.ArticlesByLanguage || {});
        const items = known.map(function (code) {
            return { value: code, label: (LM_SETTINGS_LANGUAGES[code] || code) + ' (' + (view.ArticlesByLanguage[code] || []).join(', ') + ')' };
        }).concat(chosenLanguages.filter(function (code) {
            return known.indexOf(code) < 0;
        }).map(function (code) {
            return { value: code, label: code + ' (set by a newer version)' };
        }));
        lmClear(languageSlot);
        languageToggles = lmToggles('LibraryMaintenanceSettingsLanguagesToggles', 'Leading articles dropped', items, function (value) {
            toggle(chosenLanguages, value);
            changed();
        });
        languageSlot.append(languageToggles.element);
    }

    // The fields' values as the API takes them, with the wait as typed.
    function values() {
        const libraries = (view.Libraries || []).filter(function (library) {
            return chosenLibraries.indexOf(lmIdKey(library.Id)) >= 0;
        }).map(function (library) {
            return library.Id;
        });
        const order = Object.keys(view.ArticlesByLanguage || {});
        const rank = function (code) {
            return order.indexOf(code) < 0 ? order.length : order.indexOf(code);
        };
        return {
            DuplicatesEnabled: enabled.get(),
            DefaultMatchMode: mode.value,
            UnknownYearPolicy: unknownYear.value,
            IncludedLibraryIds: libraries,
            ExcludedPathPrefixes: lmLines(folders.value),
            StripEditionSuffixes: strip.get(),
            EditionPhrases: lmLines(phrases.value),
            ArticleLanguages: chosenLanguages.slice().sort(function (a, b) {
                return rank(a) - rank(b);
            }),
            WarmIndexAfterScan: warm.get(),
            RequestWaitSeconds: String(wait.value).trim(),
            EnableWebUi: webUi.get(),
            MoveEnabled: move.get(),
            ShowInItemMenu: itemMenu.get(),
            ReportWebAnchorProblems: anchorReports.get()
        };
    }

    function isDirty() {
        return !!view && saved !== null && JSON.stringify(values()) !== saved;
    }

    function changed() {
        if (libraryToggles) {
            libraryToggles.set(function (value) {
                return chosenLibraries.indexOf(value) >= 0;
            });
        }

        if (languageToggles) {
            languageToggles.set(function (value) {
                return chosenLanguages.indexOf(value) >= 0;
            });
        }

        libraryNote.textContent = chosenLibraries.length === 0
            ? 'None chosen: the index reads every movie and mixed library, and the ones added later.'
            : 'The index reads the libraries chosen, and no others.';
        discard.setAttribute('aria-disabled', busy || !isDirty() ? 'true' : 'false');
    }

    function onSave() {
        if (busy || !view || save.getAttribute('aria-disabled') === 'true') {
            return;
        }

        const body = values();
        const text = body.RequestWaitSeconds;
        if (!/^\d{1,2}$/.test(text) || Number(text) > LM_SETTINGS_MAX_WAIT) {
            say(message, 'error', 'Wait for a build must be a whole number of seconds from 0 to 30.');
            wait.focus();
            return;
        }

        body.RequestWaitSeconds = Number(text);
        const version = view.Version;
        run(message, function () {
            return lmPost('Admin/Settings', body, { query: { version: version, dryRun: true } }).then(function (report) {
                if (!report.Changes || report.Changes.length === 0) {
                    say(message, 'info', 'Nothing to save: these are the saved settings.');
                    return null;
                }

                if (report.ScopeChanged) {
                    review('save', body, version, report);
                    return null;
                }

                return commit('save', body, version);
            });
        });
    }

    // The dialog with a dry run's report: Cancel first (the focus starts there, as in kit.confirm), then the save.
    function review(kind, body, version, report) {
        const importing = kind === 'import';
        const dialog = kit.dialog({ title: importing ? 'Import these settings?' : 'Save these settings?', modal: true, scopedKeys: true, returnFocus: importing ? importButton : save });
        dialog.panel.id = 'LibraryMaintenanceSettingsReview';
        const cancel = kit.button('Cancel', {
            onClick: function () {
                dialog.close();
            }
        });
        const label = report.RebuildsIndex ? (importing ? 'Import and rebuild the index' : 'Save and rebuild the index') : (importing ? 'Import' : 'Save');
        const confirm = kit.button(label, {
            variant: 'primary',
            onClick: function () {
                dialog.close();
                run(importing ? transfer : message, function () {
                    return commit(kind, body, version);
                });
            }
        });
        confirm.id = 'LibraryMaintenanceSettingsConfirm';
        if (importing && isDirty()) {
            dialog.body.append(lmEl('p', { text: 'The changes made on this page and not saved are dropped.' }));
        }

        lmSettingsReport(report, kind).forEach(function (part) {
            dialog.body.append(part);
        });
        dialog.body.append(lmEl('div', { class: KIT_BUILD.prefix + '-actions' }, [cancel, confirm]));
    }

    function commit(kind, body, version) {
        const importing = kind === 'import';
        return lmPost(importing ? 'Admin/Import' : 'Admin/Settings', body, { query: { version: version, dryRun: false } }).then(function (report) {
            const labels = (report.Changes || []).map(function (change) {
                return String(change.Label || change.Field || '');
            });
            let text = labels.length === 0
                ? (importing ? 'Nothing was imported: the settings were already the file\'s.' : 'Nothing was saved: these are the saved settings.')
                : (importing ? 'Imported: ' : 'Saved: ') + labels.join(', ') + '.';
            const settings = report.Settings || {};
            if (report.Applied && settings.DuplicatesEnabled === false) {
                text += ' The Duplicates tab is hidden until finding duplicate movies is on again.';
            } else if (report.Applied && report.RebuildsIndex) {
                text += ' The index is being built again; the Duplicates tab shows it when it is ready.';
            }

            say(importing ? transfer : message, 'info', text);
            return load().then(function () {
                ctx.refresh();
            });
        });
    }

    function onExport() {
        if (busy) {
            return;
        }

        run(transfer, function () {
            return kit.api('GET', LM_ROUTE + 'Admin/Export', { raw: true }).then(function (response) {
                const disposition = (response.headers && typeof response.headers.get === 'function' && response.headers.get('Content-Disposition')) || '';
                const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
                const name = match ? decodeURIComponent(match[1]) : 'librarymaintenance-export.json';
                return response.text().then(function (text) {
                    lmDownload(name, text);
                    say(transfer, 'info', 'Exported as ' + name + '. It holds the excluded folders\' paths: keep it where only administrators can read it.');
                });
            });
        });
    }

    function onImport(chosen) {
        if (busy || !view) {
            return;
        }

        const version = view.Version;
        lmClear(transfer);
        run(transfer, function () {
            return lmReadText(chosen).then(function (text) {
                let body;
                try {
                    body = JSON.parse(text);
                } catch (e) {
                    say(transfer, 'error', 'The file isn\'t JSON: choose a file exported from Library Maintenance.');
                    return null;
                }

                return lmPost('Admin/Import', body, { query: { version: version, dryRun: true } }).then(function (report) {
                    if (!report.Changes || report.Changes.length === 0) {
                        say(transfer, 'info', 'The file\'s settings are the ones saved here: there is nothing to import.');
                        lmSettingsWarnings(report.Warnings).forEach(function (part) {
                            transfer.append(part);
                        });
                        return null;
                    }

                    review('import', body, version, report);
                    return null;
                });
            });
        });
    }

    // Read again unless the form has changes of its own (a view jellyfin-web kept and shows again).
    function reload() {
        if (!busy && !isDirty()) {
            load();
        }
    }

    setBusy(false);
    return { element: element, update: function () {}, load: load, reload: reload };
}
