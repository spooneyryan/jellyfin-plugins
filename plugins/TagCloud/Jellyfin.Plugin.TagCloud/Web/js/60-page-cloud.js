// The Tag Cloud page, #/jftc-cloud: the toolbar (source, scope, libraries, words, settings), the saved
// clouds, the cloud with its summary and narrowing, the selected word's titles, the held word's cloud of titles, and the
// read-only full-screen view (show=1). What the cloud counts lives in the hash (source, scope, libs, narrow, sel, cloud),
// so a reload, a deep link and Back show the same cloud; its look lives on this device (jftc:look) and in saved clouds.

const TC_TITLES_PAGE = 50;
const TC_SPOTLIGHT_MAX = 200;
const TC_SHOW_IDLE_MS = 2500;
const TC_RETRY_MS = 3000;
const TC_WORDS_DEBOUNCE_MS = 250;

// The full-screen view open now, if any (it lives on document.body, above jellyfin-web's header and drawer).
let tcShowView = null;

function tcParamsOf(cloud, extra) {
    return Object.assign({
        source: cloud.source,
        scope: cloud.scope,
        libs: cloud.libraries.length ? cloud.libraries.join(',') : null,
        narrow: cloud.narrow.length ? JSON.stringify(cloud.narrow) : null,
        sel: cloud.selected || null,
        cloud: cloud.savedId || null
    }, extra || {});
}

function tcCloudFromParams(params, caps) {
    const sources = caps.Sources || [];
    let narrow = [];
    try {
        const parsed = params.narrow ? JSON.parse(params.narrow) : [];
        narrow = Array.isArray(parsed) ? parsed.filter(function (k) {
            return typeof k === 'string' && k;
        }).slice(0, 10) : [];
    } catch (e) {
        narrow = [];
    }

    return {
        source: sources.indexOf(params.source) >= 0 ? params.source : (sources[0] || 'Tags'),
        scope: TC_SCOPES[params.scope] ? params.scope : 'Movies',
        libraries: params.libs ? String(params.libs).split(',').filter(Boolean) : [],
        narrow: narrow,
        selected: params.sel || null,
        savedId: params.cloud || null
    };
}

function tcStoredLook(caps) {
    const stored = kit.storage.get('look', null);
    const look = tcNormalizeLook(stored || TC_LOOK_DEFAULTS, caps.MaxWordsLimit);
    if (!stored && kit.layout.isTv()) {
        look.MaxWords = caps.TvMaxWordsDefault;
    }

    return look;
}

function tcDescribe(cloud, look, libraryNames) {
    const source = TC_SOURCES[cloud.source] || TC_SOURCES.Tags;
    const scope = TC_SCOPES[cloud.scope] || TC_SCOPES.Movies;
    let text = source.label + ' across ' + (cloud.scope === 'Both' ? 'movies and TV shows' : scope.many === 'shows' ? 'TV shows' : scope.many);
    if (libraryNames.length) {
        text += ' in ' + tcNameList(libraryNames);
    } else if (cloud.libraries.length) {
        text += ' in ' + tcCount(cloud.libraries.length, { one: 'library', many: 'libraries' });
    }

    if (cloud.narrow.length) {
        text += ', narrowed to ' + cloud.narrow.join(', ');
    }

    return text + ' · ' + look.MaxWords + ' words';
}

function tcSegmented(label, options, value, onPick) {
    return h('div', { class: 'jftc-seg', role: 'group', 'aria-label': label }, options.map(function (option) {
        return h('button', {
            type: 'button',
            class: 'jftc-seg-button' + (option.value === value ? ' jftc-seg-on' : ''),
            'aria-pressed': String(option.value === value),
            text: option.text,
            onclick: function () {
                if (option.value !== value) {
                    onPick(option.value);
                }
            }
        });
    }));
}

// ---- The page ------------------------------------------------------------------------------------------------------

async function renderTcPage(body, params, context) {
    body.append(kit.notice({ kind: 'info', text: 'Loading…' }));
    let caps;
    try {
        caps = await tcCapabilities();
    } catch (error) {
        if (!context.isCurrent()) {
            return;
        }

        body.textContent = '';
        body.append(kit.notice({ kind: 'error', text: error.message, action: { label: 'Retry', onClick: function () { context.render(); } } }));
        return;
    }

    if (!context.isCurrent()) {
        return;
    }

    body.textContent = '';
    if (!caps.Sources || caps.Sources.length === 0) {
        body.append(kit.notice({ kind: 'empty', text: 'Every cloud is turned off by your server admin.' }));
        return;
    }

    const page = tcPage(body, params, context, caps);
    await page.start();
}

function tcPage(body, params, context, caps) {
    const cloud = tcCloudFromParams(params, caps);
    let look = tcStoredLook(caps);
    let words = null;
    let items = [];
    let error = null;
    let libraries = null;
    let saved = [];
    let spotlight = null;
    let retryTimer = null;
    let wordsTimer = null;
    let loadVersion = 0;

    const toolbar = h('div', { class: 'jftc-toolbar' });
    const savedRow = h('div', { class: 'jftc-saved', role: 'group', 'aria-label': 'Saved clouds' });
    const headerActions = h('div', { class: 'jftc-card-actions' });
    const header = h('div', { class: 'jftc-card-header' }, [h('h2', { class: 'jftc-card-title', text: 'Tag Cloud' }), headerActions]);
    const summary = h('p', { class: 'jftc-summary' });
    const narrowRow = h('div', { class: 'jftc-narrowing', role: 'group', 'aria-label': 'Narrowed to' });
    const canvas = h('div', { class: 'jftc-canvas' });
    const status = h('div', { class: 'jftc-status' });
    const titlesBox = h('div', { class: 'jftc-titles' });
    const cloudView = createTcCloud({ label: 'Tag Cloud' });
    canvas.append(cloudView.element);
    body.append(h('div', { class: 'jftc-page-body' }, [toolbar, savedRow, h('div', { class: 'jftc-card' }, [header, summary, narrowRow, status, canvas]), titlesBox]));

    function source() {
        return TC_SOURCES[cloud.source] || TC_SOURCES.Tags;
    }

    function scope() {
        return TC_SCOPES[cloud.scope] || TC_SCOPES.Movies;
    }

    function pushParams(replace) {
        kit.pages.setParams(TC_PAGE.key, tcParamsOf(cloud), { replace: !!replace });
    }

    function setLook(next, persist) {
        look = next;
        if (persist !== false) {
            kit.storage.set('look', look);
        }

        applyBackdrop();
        drawCloud();
        renderToolbar();
    }

    function applyBackdrop() {
        const backdrop = tcChoice(TC_BACKGROUNDS, look.Background);
        canvas.style.backgroundColor = backdrop.color || '';
        canvas.style.color = backdrop.ink || '';
        canvas.classList.toggle('jftc-canvas-backdrop', !!backdrop.color);
    }

    function libraryNames() {
        if (!libraries) {
            return [];
        }

        return libraries.filter(function (l) {
            return cloud.libraries.indexOf(l.Id) >= 0;
        }).map(function (l) {
            return l.Name;
        });
    }

    // ---- toolbar --------------------------------------------------------------------------------------------------

    function renderToolbar() {
        toolbar.textContent = '';
        const sources = caps.Sources.map(function (s) {
            return { value: s, text: (TC_SOURCES[s] || { label: s }).label };
        });
        const scopes = Object.keys(TC_SCOPES).map(function (s) {
            return { value: s, text: TC_SCOPES[s].label };
        });
        const picked = libraryNames();
        const libraryButton = kit.button(picked.length ? 'Libraries (' + picked.length + ')' : 'All libraries', { icon: 'video_library', onClick: openLibraries });
        const chips = h('div', { class: 'jftc-chips' }, (libraries || []).filter(function (l) {
            return cloud.libraries.indexOf(l.Id) >= 0;
        }).map(function (l) {
            return h('button', {
                type: 'button',
                class: 'jftc-chip',
                title: 'Count every library again instead of this one',
                text: l.Name + ' ×',
                onclick: function () {
                    setLibraries(cloud.libraries.filter(function (id) {
                        return id !== l.Id;
                    }));
                }
            });
        }));
        const wordsValue = h('span', { class: 'jftc-range-value', text: String(look.MaxWords) });
        const wordsInput = h('input', {
            type: 'range',
            class: 'jftc-range jftc-words',
            min: String(caps.WordStep),
            max: String(caps.MaxWordsLimit),
            step: String(caps.WordStep),
            value: String(look.MaxWords),
            'aria-label': 'Words'
        });
        wordsInput.addEventListener('input', function () {
            wordsValue.textContent = wordsInput.value;
            clearTimeout(wordsTimer);
            wordsTimer = setTimeout(function () {
                look = tcNormalizeLook(Object.assign({}, look, { MaxWords: Number(wordsInput.value) }), caps.MaxWordsLimit);
                kit.storage.set('look', look);
                loadWords();
            }, TC_WORDS_DEBOUNCE_MS);
        });
        kit.append(toolbar,
            tcSegmented('What to count', sources, cloud.source, function (v) {
                cloud.source = v;
                cloud.narrow = [];
                cloud.selected = null;
                cloud.savedId = null;
                pushParams(false);
                reload();
            }),
            tcSegmented('Which titles', scopes, cloud.scope, function (v) {
                cloud.scope = v;
                cloud.narrow = [];
                cloud.selected = null;
                cloud.savedId = null;
                pushParams(false);
                loadLibraries().then(reload);
            }),
            h('div', { class: 'jftc-libraries' }, [libraryButton, chips]),
            h('label', { class: 'jftc-words-field' }, [h('span', { class: 'jftc-words-label', text: 'Words' }), wordsInput, wordsValue]),
            kit.button('Settings', { icon: 'tune', onClick: function () { openTcSettings(function () { return look; }, setLook, caps.MaxWordsLimit); } }));
    }

    function openLibraries() {
        const dialog = kit.dialog({ title: 'Libraries', scopedKeys: true });
        const list = h('div', { class: 'jftc-library-list' });
        const chosen = cloud.libraries.slice();
        (libraries || []).forEach(function (l) {
            const box = h('input', { type: 'checkbox', checked: chosen.indexOf(l.Id) >= 0 });
            box.addEventListener('change', function () {
                const at = chosen.indexOf(l.Id);
                if (box.checked && at < 0) {
                    chosen.push(l.Id);
                } else if (!box.checked && at >= 0) {
                    chosen.splice(at, 1);
                }
            });
            list.append(h('label', { class: 'jftc-library' }, [box, h('span', { class: 'jftc-library-name', text: l.Name }), h('span', { class: 'jftc-library-count', text: tcCount(l.TitleCount, scope()) })]));
        });
        kit.append(dialog.body,
            h('p', { class: 'jftc-note', text: libraries && libraries.length ? 'Count only the titles in the libraries ticked; none ticked counts them all.' : 'Reading the libraries…' }),
            list,
            h('div', { class: 'jftc-settings-actions' }, [
                kit.button('All libraries', { onClick: function () { dialog.close(); setLibraries([]); } }),
                kit.button('Done', { variant: 'primary', onClick: function () { dialog.close(); setLibraries(chosen); } })
            ]));
    }

    function setLibraries(ids) {
        cloud.libraries = ids.slice();
        cloud.selected = null;
        cloud.savedId = null;
        pushParams(false);
        renderToolbar();
        reload();
    }

    async function loadLibraries() {
        try {
            libraries = await tcLibraries(cloud.scope);
        } catch (e) {
            libraries = [];
        }

        // Libraries the scope doesn't have are let go, so a cloud never counts over a library it can't show.
        const known = libraries.map(function (l) {
            return l.Id;
        });
        cloud.libraries = cloud.libraries.filter(function (id) {
            return known.indexOf(id) >= 0;
        });
        if (context.isCurrent()) {
            renderToolbar();
        }
    }

    // ---- saved clouds ---------------------------------------------------------------------------------------------

    function renderSaved() {
        savedRow.textContent = '';
        savedRow.append(h('span', { class: 'jftc-saved-label', text: 'Saved clouds' }));
        if (saved.length === 0) {
            savedRow.append(h('span', { class: 'jftc-saved-none', text: 'None yet. Save a cloud to open it again, or show it full screen.' }));
        }

        saved.forEach(function (c) {
            const current = c.Id === cloud.savedId;
            savedRow.append(h('span', { class: 'jftc-pill' + (current ? ' jftc-pill-current' : '') + (c.IsMine ? '' : ' jftc-pill-shared') }, [
                h('button', {
                    type: 'button',
                    class: 'jftc-pill-open',
                    title: (c.IsMine ? '' : 'Shared by ' + c.OwnerName + '. ') + 'Open: ' + tcDescribe(savedCloud(c), c.Look, []),
                    text: c.Name,
                    onclick: function () { openSaved(c); }
                }),
                kit.iconButton('fullscreen', 'Show ' + c.Name + ' full screen', function () { openSaved(c, true); }),
                kit.iconButton('more_vert', 'More for ' + c.Name, function () { savedMenu(c); })
            ]));
        });
        savedRow.append(kit.button('Save cloud…', { icon: 'bookmark_add', onClick: openSave }));
    }

    function savedCloud(c) {
        return { source: c.Source, scope: c.Scope, libraries: c.LibraryIds || [], narrow: c.NarrowKeys || [], selected: null, savedId: c.Id };
    }

    async function loadSaved() {
        try {
            saved = await tcListClouds();
        } catch (e) {
            saved = [];
        }

        if (context.isCurrent()) {
            renderSaved();
        }
    }

    function openSaved(c, showIt) {
        const next = savedCloud(c);
        Object.assign(cloud, next);
        look = tcNormalizeLook(c.Look, caps.MaxWordsLimit);
        kit.storage.set('look', look);
        applyBackdrop();
        pushParams(false);
        renderSaved();
        if (showIt) {
            // In the click's own turn, so the browser lets the view go full screen.
            enterShow(true);
            loadLibraries().then(reload);
            return;
        }

        loadLibraries().then(reload);
    }

    function savedMenu(c) {
        const dialog = kit.dialog({ title: c.Name, scopedKeys: true });
        const run = function (action) {
            return function () {
                dialog.close();
                action().catch(function (e) {
                    kit.toast(e.message);
                });
            };
        };
        const buttons = [];
        if (c.IsMine) {
            buttons.push(kit.button('Rename…', { onClick: function () { dialog.close(); openSave(c, true); } }));
            // With sharing turned off, the owner of a cloud that is still shared can stop sharing it, never start.
            if (caps.AllowSharedClouds || c.Shared) {
                buttons.push(kit.button(c.Shared ? 'Stop sharing' : 'Share with everyone', {
                    onClick: run(function () {
                        return tcSaveCloud(c.Id, Object.assign(requestOf(c), { Shared: !c.Shared, IfUpdatedUtc: c.UpdatedUtc })).then(function () {
                            kit.toast(c.Shared ? 'Only you can open “' + c.Name + '” now.' : 'Everyone can open “' + c.Name + '” now.');
                            return loadSaved();
                        });
                    })
                }));
            }
        }

        buttons.push(kit.button('Duplicate', {
            onClick: run(function () {
                return tcDuplicateCloud(c.Id).then(function (copy) {
                    kit.toast('Saved a copy, “' + copy.Name + '”.');
                    return loadSaved();
                });
            })
        }));
        buttons.push(kit.button('Copy link', {
            onClick: run(function () {
                const link = window.location.href.split('#')[0] + kit.pages.href(TC_PAGE.key, { cloud: c.Id });
                if (navigator.clipboard && navigator.clipboard.writeText) {
                    return navigator.clipboard.writeText(link).then(function () {
                        kit.toast('Copied the link.');
                    });
                }

                kit.toast(link, { timeout: 15000 });
                return Promise.resolve();
            })
        }));
        if (c.IsMine || (caps.IsAdmin && c.Shared)) {
            buttons.push(kit.button('Delete', {
                variant: 'danger',
                onClick: run(function () {
                    return kit.confirm({ title: 'Delete “' + c.Name + '”?', message: c.IsMine ? 'The saved cloud goes; nothing in your library changes.' : 'It goes for everyone it is shared with.', confirmLabel: 'Delete' }).then(function (ok) {
                        if (!ok) {
                            return null;
                        }

                        return tcDeleteCloud(c.Id).then(function () {
                            if (cloud.savedId === c.Id) {
                                cloud.savedId = null;
                                pushParams(true);
                            }

                            kit.toast('Deleted the saved cloud “' + c.Name + '”.');
                            return loadSaved();
                        });
                    });
                })
            }));
        }

        dialog.body.append(h('div', { class: 'jftc-menu' }, buttons));
    }

    function requestOf(c) {
        return { Name: c.Name, Shared: c.Shared, Source: c.Source, Scope: c.Scope, LibraryIds: c.LibraryIds || [], NarrowKeys: c.NarrowKeys || [], Look: c.Look };
    }

    // The save box: a name (a name of one of yours replaces that cloud), and sharing while the admin allows it. With rename,
    // only the cloud's name changes.
    function openSave(existing, rename) {
        const current = rename ? existing : saved.filter(function (c) {
            return c.IsMine && c.Id === cloud.savedId;
        })[0];
        const dialog = kit.dialog({ title: rename ? 'Rename cloud' : 'Save cloud', scopedKeys: true });
        const name = kit.input({ maxlength: '80', value: current ? current.Name : '', 'aria-label': 'Name' });
        const share = h('input', { type: 'checkbox', checked: !!(current && current.Shared) });
        const replaceNote = h('p', { class: 'jftc-note' });
        const update = function () {
            const taken = mineNamed(name.value);
            replaceNote.textContent = taken && (!rename || taken.Id !== existing.Id) ? (rename ? 'You already have a cloud of that name.' : 'This replaces your saved cloud of that name.') : '';
        };
        name.addEventListener('input', update);
        const save = async function () {
            const text = name.value.trim();
            if (!text) {
                name.focus();
                return;
            }

            const taken = mineNamed(text);
            const target = rename ? existing : taken;
            const request = rename
                ? Object.assign(requestOf(existing), { Name: text, IfUpdatedUtc: existing.UpdatedUtc })
                : {
                    Name: text,
                    Shared: caps.AllowSharedClouds ? share.checked : !!(target && target.Shared),
                    Source: cloud.source,
                    Scope: cloud.scope,
                    LibraryIds: cloud.libraries,
                    NarrowKeys: cloud.narrow,
                    Look: look,
                    IfUpdatedUtc: target ? target.UpdatedUtc : null
                };
            try {
                const result = await tcSaveCloud(target ? target.Id : null, request);
                dialog.close();
                cloud.savedId = result.Id;
                pushParams(true);
                kit.toast((rename ? 'Renamed it “' : 'Saved the cloud “') + result.Name + '”.');
                await loadSaved();
            } catch (e) {
                replaceNote.textContent = e.message;
            }
        };
        name.addEventListener('keydown', function (event) {
            if (event.key === 'Enter') {
                event.preventDefault();
                save();
            }
        });
        kit.append(dialog.body,
            kit.field('Name', name),
            !rename && caps.AllowSharedClouds ? h('label', { class: 'jftc-check' }, [share, h('span', { text: 'Share with everyone (they see their own counts)' })]) : null,
            replaceNote,
            rename ? null : h('p', { class: 'jftc-note', text: tcDescribe(cloud, look, libraryNames()) }),
            rename ? null : h('p', { class: 'jftc-note', text: 'It keeps what the cloud counts, its libraries and filters, and every setting of its look.' }),
            h('div', { class: 'jftc-settings-actions' }, [
                kit.button('Cancel', { onClick: dialog.close }),
                kit.button('Save', { variant: 'primary', onClick: save })
            ]));
        update();
    }

    function mineNamed(text) {
        const wanted = String(text || '').trim().toLowerCase();
        return wanted ? saved.filter(function (c) {
            return c.IsMine && c.Name.toLowerCase() === wanted;
        })[0] : null;
    }

    // ---- the cloud ------------------------------------------------------------------------------------------------

    function reload() {
        cloud.selected = cloud.selected || null;
        closeSpotlight();
        return loadWords();
    }

    async function loadWords() {
        const version = ++loadVersion;
        clearTimeout(retryTimer);
        status.textContent = '';
        error = null;
        let result;
        try {
            result = await tcWords(cloud, look.MaxWords);
        } catch (e) {
            if (version !== loadVersion || !context.isCurrent()) {
                return;
            }

            error = e;
            words = null;
            items = [];
            status.append(kit.notice({ kind: e.status === 503 ? 'info' : 'error', text: e.message }));
            if (e.status === 503) {
                retryTimer = setTimeout(function () {
                    if (context.isShown()) {
                        loadWords();
                    }
                }, TC_RETRY_MS);
            }

            renderSummary();
            drawCloud();
            return;
        }

        if (version !== loadVersion || !context.isCurrent()) {
            return;
        }

        words = result;
        // The narrowing the server applied: words the titles no longer carry drop out.
        cloud.narrow = result.Narrow || [];
        items = result.Words.map(function (w) {
            return { key: w.Key, text: w.Text, weight: w.Count };
        });
        if (cloud.selected && !items.some(function (i) { return i.key === cloud.selected; })) {
            cloud.selected = null;
        }

        pushParams(true);
        renderSummary();
        drawCloud();
        renderTitles();
        if (tcShowView) {
            tcShowView.update(items, look, titleOf);
        }
    }

    function titleOf(item) {
        return item.text + ': ' + tcCount(item.weight, scope());
    }

    function drawCloud() {
        cloudView.update({
            items: items,
            look: look,
            title: titleOf,
            selectedKey: cloud.selected,
            spotlitKey: spotlight ? spotlight.key : null,
            onSelect: select,
            onHold: openSpotlight,
            onNarrow: narrow
        });
    }

    function renderSummary() {
        summary.textContent = '';
        headerActions.textContent = '';
        header.firstChild.textContent = cloud.source === 'Tags' ? 'Tag Cloud' : source().label + ' cloud';
        if (!words) {
            return;
        }

        const libs = libraryNames();
        let text = tcCount(words.WordCount, source()) + ' across your ' + (cloud.scope === 'Both' ? 'movies and TV shows' : scope().label === 'TV shows' ? 'TV shows' : scope().many);
        if (libs.length) {
            text += ' in ' + tcNameList(libs);
        }

        if (cloud.narrow.length) {
            text += ' that share ' + scope().many + ' with every one below';
        }

        text += '.';
        if (words.WordCount > look.MaxWords) {
            text += ' Showing the ' + look.MaxWords.toLocaleString() + ' most common.';
        }

        text += ' Click a word to list its ' + scope().many + ', hold it down to see them as a cloud, or right-click it to add it to the filters.';
        summary.textContent = text;

        narrowRow.textContent = '';
        kit.setHidden(narrowRow, cloud.narrow.length === 0);
        if (cloud.narrow.length) {
            narrowRow.append(h('span', { class: 'jftc-narrowing-label', text: 'Sharing ' + scope().many + ' with' }));
            cloud.narrow.forEach(function (key) {
                narrowRow.append(h('button', {
                    type: 'button',
                    class: 'jftc-chip jftc-chip-accent',
                    title: 'Stop narrowing by this',
                    text: textOf(key) + ' ×',
                    onclick: function () {
                        cloud.narrow = cloud.narrow.filter(function (k) {
                            return k !== key;
                        });
                        cloud.savedId = null;
                        pushParams(false);
                        reload();
                    }
                }));
            });
            if (cloud.narrow.length === 1 && !cloud.selected) {
                narrowRow.append(h('span', { class: 'jftc-narrowing-hint', role: 'status', text: 'Click at least one more item to display results' }));
            }

            narrowRow.append(kit.button('Show all', {
                onClick: function () {
                    cloud.narrow = [];
                    cloud.savedId = null;
                    pushParams(false);
                    reload();
                }
            }));
        }

        if (look.Arrangement === 'Packed') {
            headerActions.append(kit.button('New arrangement', {
                icon: 'shuffle',
                onClick: function () {
                    setLook(Object.assign({}, look, { Seed: look.Seed + 1 }));
                }
            }));
        }

        headerActions.append(kit.button('Full screen', { icon: 'fullscreen', onClick: function () { enterShow(true); } }));
        if (caps.IsAdmin) {
            headerActions.append(kit.button('Recount', {
                icon: 'refresh',
                title: 'Count the library again (administrators)',
                onClick: function () {
                    tcRebuild().then(function () {
                        kit.toast('Counting the library again; the cloud updates in a moment.');
                        setTimeout(function () {
                            if (context.isShown()) {
                                loadWords();
                            }
                        }, TC_RETRY_MS);
                    }, function (e) {
                        kit.toast(e.message);
                    });
                }
            }));
        }
    }

    function textOf(key) {
        const item = items.filter(function (i) {
            return i.key === key;
        })[0];
        return item ? item.text : key;
    }

    function select(key) {
        cloud.selected = key;
        pushParams(true);
        drawCloud();
        renderSummary();
        renderTitles();
    }

    function narrow(item) {
        const at = cloud.narrow.indexOf(item.key);
        if (at >= 0) {
            cloud.narrow.splice(at, 1);
        } else if (cloud.narrow.length < 10) {
            cloud.narrow.push(item.key);
        }

        cloud.savedId = null;
        pushParams(false);
        reload();
    }

    // ---- the selected word's titles --------------------------------------------------------------------------------

    // The word whose titles are listed: the one clicked; else, with two or more filters, the first of them, whose titles
    // in the narrowed cloud are those carrying every one.
    function listedKey() {
        return cloud.selected || (cloud.narrow.length >= 2 ? cloud.narrow[0] : null);
    }

    function renderTitles() {
        titlesBox.textContent = '';
        const key = listedKey();
        if (!key) {
            return;
        }

        const word = items.filter(function (i) {
            return i.key === key;
        })[0];
        const others = cloud.narrow.filter(function (k) {
            return k !== key;
        }).map(textOf);
        const heading = h('h3', { class: 'jftc-titles-title', text: (word ? word.text : textOf(key)) + (word ? ' · ' + tcCount(word.weight, scope()) : '') + (others.length ? ' with ' + others.join(', ') : '') });
        const list = kit.list([]);
        let loaded = 0;
        const pager = kit.pager({
            onMore: function () {
                more();
            }
        });
        const more = async function () {
            pager.update({ busy: true });
            try {
                const result = await tcTitles(cloud, key, { startIndex: loaded, limit: TC_TITLES_PAGE });
                result.Items.forEach(function (t) {
                    list.append(kit.listItem({ Id: t.Id, Name: t.Name, ProductionYear: t.Year, Type: t.Kind }, { icon: t.Kind === 'Series' ? 'tv' : 'movie' }));
                });
                loaded += result.Items.length;
                pager.update({ busy: false, hasMore: loaded < result.Total });
            } catch (e) {
                pager.update({ busy: false, hasMore: false });
                titlesBox.append(kit.notice({ kind: 'error', text: e.message }));
            }
        };
        kit.append(titlesBox, h('div', { class: 'jftc-card' }, [heading, list, pager.element]));
        more();
    }

    // ---- hold: a cloud of the word's titles ------------------------------------------------------------------------

    async function openSpotlight(item) {
        closeSpotlight();
        const seed = tcLayout.stableHash(item.key) | 0;
        const view = createTcCloud({ label: scope().many + ' with ' + item.text, arrangingText: '' });
        const veil = h('div', { class: 'jftc-spotlight', 'aria-live': 'polite' }, [view.element, h('div', { class: 'jftc-spotlight-caption', text: item.text + ' · ' + tcCount(item.weight, scope()) })]);
        spotlight = { key: item.key, veil: veil, view: view };
        veil.addEventListener('click', function (event) {
            if (event.target === veil || !view.element.contains(event.target)) {
                closeSpotlight();
            }
        });
        canvas.append(veil);
        drawCloud();
        try {
            const result = await tcTitles(cloud, item.key, { order: 'Random', seed: seed, limit: TC_SPOTLIGHT_MAX });
            if (!spotlight || spotlight.view !== view) {
                return;
            }

            // Random sizes (and, through the palette, colours), but the same ones each time for a word.
            const random = tcPack.mulberry32(seed);
            view.update({
                items: result.Items.map(function (t) {
                    return { key: t.Id, text: t.Name, weight: 1 + Math.floor(random() * 9) };
                }),
                look: tcNormalizeLook(Object.assign({}, TC_LOOK_DEFAULTS, { MaxWords: TC_SPOTLIGHT_MAX, Palette: 'Rainbow', MinFontSize: 0.7, MaxFontSize: 2.2, Scale: 'Linear', Seed: seed, Font: look.Font, TextCase: look.TextCase, Shape: look.Shape }), caps.MaxWordsLimit),
                title: function (t) {
                    return t.text;
                },
                onSelect: function (id) {
                    if (id) {
                        window.location.hash = kit.itemHref(id).replace(/^#/, '');
                    }
                }
            });
            if (result.Total > result.Items.length) {
                veil.lastChild.textContent += ' · ' + result.Items.length + ' of them shown';
            }
        } catch (e) {
            closeSpotlight();
            kit.toast(e.message);
        }
    }

    function closeSpotlight() {
        if (!spotlight) {
            return;
        }

        spotlight.view.destroy();
        spotlight.veil.remove();
        spotlight = null;
        drawCloud();
    }

    body.addEventListener('keydown', function (event) {
        if (event.key === 'Escape' && spotlight) {
            event.preventDefault();
            closeSpotlight();
        }
    });

    // ---- full screen -----------------------------------------------------------------------------------------------

    // Called in a click's own turn (fromClick), so the browser lets the page go full screen; from a deep link, the view
    // offers a "Go full screen" button instead.
    function enterShow(fromClick) {
        if (tcShowView) {
            tcShowView.close(false);
        }

        tcShowView = createTcShowView(look, fromClick, function () {
            tcShowView = null;
            if (context.isCurrent()) {
                kit.pages.setParams(TC_PAGE.key, tcParamsOf(cloud), { replace: true });
            }
        }, context);
        if (words) {
            tcShowView.update(items, look, titleOf);
        }

        kit.pages.setParams(TC_PAGE.key, tcParamsOf(cloud, { show: '1' }), { replace: true });
    }

    return {
        start: async function () {
            applyBackdrop();
            renderToolbar();
            renderSaved();
            await Promise.all([loadLibraries(), loadSaved()]);
            if (!context.isCurrent()) {
                return;
            }

            // A deep link to a saved cloud opens it (its own settings, its look).
            if (params.cloud && !params.source) {
                const c = saved.filter(function (s) {
                    return s.Id === params.cloud;
                })[0];
                if (c) {
                    openSaved(c, params.show === '1' ? false : undefined);
                } else {
                    status.append(kit.notice({ kind: 'info', text: 'That saved cloud isn’t there any more.' }));
                    cloud.savedId = null;
                }
            }

            if (params.show === '1') {
                enterShow(false);
            }

            await loadWords();
        }
    };
}

// ---- The full-screen view ------------------------------------------------------------------------------------------

// The page's own background for the Theme backdrop: the first opaque one of the body and the document, as the theme
// painted it (the kit never reads the theme's background variables); a dark grey without one.
function tcPageBackground() {
    const nodes = [document.body, document.documentElement];
    for (let i = 0; i < nodes.length; i++) {
        const color = nodes[i] ? getComputedStyle(nodes[i]).backgroundColor : '';
        if (color && color !== 'transparent' && !/rgba\([^)]*,\s*0\)$/.test(color)) {
            return color;
        }
    }

    return '#101010';
}

// Just the cloud, over the whole window (and, when the browser allows, the whole screen): no toolbar, settings, saved
// clouds or selection, only tooltips. Esc, Back, the exit button (which shows while the pointer moves) and leaving full
// screen close it. onClosed runs once it has closed.
function createTcShowView(look, fromClick, onClosed, context) {
    const backdrop = tcChoice(TC_BACKGROUNDS, look.Background);
    const view = createTcCloud({ label: 'Tag Cloud', arrangingText: 'Arranging…' });
    // Until the words are in; the cloud's first drawing replaces it.
    view.element.append(h('div', { class: 'jftc-cloud-note', role: 'status', text: 'Loading…' }));
    const exit =kit.button('Exit full screen', { icon: 'fullscreen_exit', className: 'jftc-show-exit', onClick: function () { close(true); } });
    const goFull = kit.button('Go full screen', { icon: 'fullscreen', className: 'jftc-show-full', onClick: requestFull });
    const overlay = h('div', { class: 'jftc-show', role: 'region', 'aria-label': 'Tag Cloud, full screen', tabindex: '-1' }, [view.element, exit, goFull]);
    overlay.style.backgroundColor = backdrop.color || tcPageBackground();
    overlay.style.color = backdrop.ink || '';
    let idleTimer = null;
    let closed = false;
    let wasFull = false;

    function requestFull() {
        const element = document.documentElement;
        if (element.requestFullscreen && !document.fullscreenElement) {
            element.requestFullscreen().then(function () {
                wasFull = true;
                kit.setHidden(goFull, true);
            }, function () {
                // Refused (no gesture, or not allowed): the overlay alone fills the window.
            });
        }
    }

    function wake() {
        overlay.classList.remove('jftc-show-idle');
        clearTimeout(idleTimer);
        idleTimer = setTimeout(function () {
            overlay.classList.add('jftc-show-idle');
        }, TC_SHOW_IDLE_MS);
    }

    function onKey(event) {
        if (event.key === 'Escape' || kit.shortcuts.isBackKey(event)) {
            event.preventDefault();
            event.stopPropagation();
            close(true);
        }
    }

    function onFullscreenChange() {
        if (wasFull && !document.fullscreenElement) {
            close(true);
        }
    }

    function onNavigate() {
        if (!context.isCurrent()) {
            close(false);
        }
    }

    function close(byUser) {
        if (closed) {
            return;
        }

        closed = true;
        clearTimeout(idleTimer);
        document.removeEventListener('keydown', onKey, true);
        document.removeEventListener('fullscreenchange', onFullscreenChange);
        window.removeEventListener('hashchange', onNavigate);
        window.removeEventListener('popstate', onNavigate);
        if (document.fullscreenElement && document.exitFullscreen) {
            document.exitFullscreen().catch(function () {
                // Already left.
            });
        }

        view.destroy();
        overlay.remove();
        document.documentElement.classList.remove('jftc-showing');
        if (byUser) {
            onClosed();
        }
    }

    overlay.addEventListener('pointermove', wake);
    document.addEventListener('keydown', onKey, true);
    document.addEventListener('fullscreenchange', onFullscreenChange);
    window.addEventListener('hashchange', onNavigate);
    window.addEventListener('popstate', onNavigate);
    document.documentElement.classList.add('jftc-showing');
    document.body.append(overlay);
    kit.setHidden(goFull, fromClick || !document.documentElement.requestFullscreen);
    if (fromClick) {
        requestFull();
    }

    overlay.focus();
    wake();

    return {
        update: function (items, nextLook, title) {
            view.update({ items: items, look: nextLook, title: title });
        },
        close: close
    };
}
