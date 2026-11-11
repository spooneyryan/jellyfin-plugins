// The Move dialog's destination picker: the library (compatible ones only, the item's own
// first, as GET /Move/Destinations lists them), its folder when it has several (one on another drive, a network folder,
// a link and the like listed but not choosable, with the reason), and a folder browser over GET /Move/Folders: a
// breadcrumb, a filter, 200 folders a page with "More folders", a badge for a movie's folder, a series, a season, a
// folder Jellyfin ignores and a link, and the reason on a row that can't be chosen. Then "New folder" (for an episode at
// the library folder, "New series folder"), and for a loose movie or music video "Put it in its own folder" with its
// name: by default as LooseFileFolderMode says for the chosen folder (Wrap left out), or yes or no once changed.
//
// The destination is always { LibraryId, LocationIndex, RelativePath, NewFolderName, Wrap, WrapFolderName }: the client
// never sends a path. onChange(destination, immediate) runs after each change the admin makes (the preview
// waits 400 ms for typing to settle); destination is null while the chosen place can't be planned.

const MOVE_FOLDER_PAGE = 200;
const MOVE_BAD_NAME_CHARS = /[<>:"/\\|?*\u0000-\u001f]/;

function moveNameProblem(name) {
    if (!name) {
        return null;
    }

    if (MOVE_BAD_NAME_CHARS.test(name)) {
        return 'A folder name can\'t hold any of < > : " / \\ | ? *.';
    }

    if (/[. ]$/.test(name)) {
        return 'A folder name can\'t end with a dot or a space.';
    }

    if (name.length > 255) {
        return 'A folder name can have 255 characters at most.';
    }

    return null;
}

// Whether a library folder can be browsed and planned: the server says so.
function moveLocationUsable(location) {
    return !!(location && location.Selectable);
}

// Plain text (an option's), so the path's and label's bidi controls are shown, not obeyed.
function moveLocationText(location) {
    const parts = [moveShown(location.Path)];
    if (location.Label && location.Label !== location.Path) {
        parts.push(moveShown(location.Label));
    }

    if (typeof location.FreeBytes === 'number') {
        parts.push(kit.format.size(location.FreeBytes) + ' free');
    }

    return parts.join(' — ') + (moveLocationUsable(location) ? '' : ' (can\'t be chosen)');
}

function createMovePicker(session, host, onChange) {
    const source = session.source;
    const item = source.Item || {};
    const unit = source.Unit || {};
    const libraries = session.destinations.filter(function (library) {
        return library && Array.isArray(library.Locations);
    });
    const canWrap = unit.Kind === 'FileSet' && item.Kind !== 'Episode';
    const state = {
        library: 0,
        location: 0,
        relativePath: '',
        filter: '',
        folders: [],
        total: 0,
        seq: 0,
        abort: null,
        loading: false,
        newFolder: '',
        wrap: null,
        wrapName: '',
        plannedWrap: null,
        destroyed: false
    };

    const librarySelect = kit.select({ class: moveClass('library') }, libraries.map(function (library, index) {
        const usable = library.Locations.some(moveLocationUsable);
        return { value: String(index), text: moveShown(library.Name) + (library.IsSource ? ' (its library now)' : '') + (usable ? '' : ' (can\'t be chosen)') };
    }));
    const locationSelect = kit.select({ class: moveClass('location') }, []);
    const locationField = kit.field('Library folder', locationSelect);
    const locationNote = moveEl('div', { class: moveClass('location-note') });
    const crumbs = moveEl('ol', { class: moveClass('crumbs') });
    const crumbNav = moveEl('nav', { class: moveClass('crumb-nav'), 'aria-label': 'Folder' }, crumbs);
    const filter = kit.searchBox({
        label: 'Filter folders',
        placeholder: 'Filter folders',
        onSearch: function (text) {
            state.filter = text;
            loadFolders(false);
        }
    });
    const count = moveEl('p', { class: moveClass('count'), role: 'status' });
    const list = moveEl('ul', { class: moveClass('folders'), 'aria-label': 'Folders' });
    const listError = moveEl('div', { class: moveClass('folders-error') });
    const pager = kit.pager({
        text: 'More folders',
        onMore: function () {
            loadFolders(true);
        }
    });
    const newFolderInput = kit.input({ class: moveClass('new-folder'), maxlength: '255', autocomplete: 'off', spellcheck: 'false' });
    const newFolderError = moveEl('p', { class: classesOf(kit.cls('error'), moveClass('field-error')), id: kit.cls('move-new-folder-error-' + (++moveDialogs.sections)) });
    newFolderInput.setAttribute('aria-describedby', newFolderError.id);
    const newFolderField = kit.field('New folder (optional)', newFolderInput);
    newFolderField.append(newFolderError);
    const newFolderLabel = newFolderField.querySelector('label');

    const wrapBox = moveEl('input', { type: 'checkbox', class: moveClass('wrap') });
    const wrapState = moveEl('span', { class: moveClass('wrap-state') });
    const wrapReset = kit.button('Use the default', { className: moveClass('wrap-reset') });
    const wrapNameInput = kit.input({ class: moveClass('wrap-name'), maxlength: '255', autocomplete: 'off', spellcheck: 'false' });
    const wrapNameError = moveEl('p', { class: classesOf(kit.cls('error'), moveClass('field-error')), id: kit.cls('move-wrap-name-error-' + (++moveDialogs.sections)) });
    wrapNameInput.setAttribute('aria-describedby', wrapNameError.id);
    const wrapNameField = kit.field('Its folder\'s name', wrapNameInput);
    wrapNameField.append(wrapNameError);
    const wrapGroup = moveEl('div', { class: moveClass('wrap-group') }, [
        moveEl('label', { class: moveClass('wrap-label') }, [wrapBox, moveEl('span', { text: 'Put it in its own folder' })]),
        wrapState,
        wrapReset,
        wrapNameField
    ]);
    kit.setHidden(wrapGroup, !canWrap);

    kit.append(host,
        libraries.length === 0 ? kit.notice({ kind: 'info', text: 'No library can take this item.' }) : null,
        kit.field('Library', librarySelect),
        locationField,
        locationNote,
        crumbNav,
        filter,
        count,
        listError,
        list,
        pager.element,
        newFolderField,
        wrapGroup);

    function library() {
        return libraries[state.library] || null;
    }

    function location() {
        const lib = library();
        return lib ? lib.Locations[state.location] || null : null;
    }

    function usable() {
        return moveLocationUsable(location());
    }

    // The place planned now, or null when it can't be (a folder that can't be chosen, a name that isn't valid).
    function destination() {
        const lib = library();
        const loc = location();
        if (!lib || !moveLocationUsable(loc) || moveNameProblem(state.newFolder) || (canWrap && moveNameProblem(state.wrapName))) {
            return null;
        }

        const wanted = {
            LibraryId: lib.LibraryId,
            LocationIndex: loc.Index,
            RelativePath: state.relativePath || null,
            NewFolderName: state.newFolder || null
        };
        if (canWrap) {
            wanted.Wrap = state.wrap;
            wanted.WrapFolderName = state.wrapName || null;
        }

        return wanted;
    }

    function changed(immediate) {
        if (!state.destroyed) {
            onChange(destination(), !!immediate);
        }
    }

    function fillLocations() {
        const lib = library();
        moveClear(locationSelect);
        (lib ? lib.Locations : []).forEach(function (loc, index) {
            const option = moveEl('option', { value: String(index), text: moveLocationText(loc) });
            option.disabled = !moveLocationUsable(loc);
            locationSelect.append(option);
        });
        const first = lib ? lib.Locations.findIndex(moveLocationUsable) : -1;
        state.location = first >= 0 ? first : 0;
        locationSelect.value = String(state.location);
        kit.setHidden(locationField, !lib || lib.Locations.length < 2);
    }

    function showLocation() {
        moveClear(locationNote);
        const loc = location();
        const lib = library();
        if (!loc) {
            return;
        }

        if (!moveLocationUsable(loc)) {
            locationNote.append(kit.notice({ kind: 'info', text: (lib.Locations.length > 1 ? 'This folder of ' : '') + moveShown(lib.Name) + ' (' + moveShown(loc.Path) + ') can\'t be chosen. ' + moveLocationReason(loc.Reason) }));
        } else if (lib.Locations.length < 2) {
            locationNote.append(moveEl('p', { class: moveClass('location-path'), text: moveLocationText(loc) }));
        }

        const browse = moveLocationUsable(loc);
        [crumbNav, filter, count, list, newFolderField].forEach(function (node) {
            kit.setHidden(node, !browse);
        });
        kit.setHidden(wrapGroup, !browse || !canWrap);
        if (!browse) {
            pager.update({ hasMore: false, busy: false });
            moveClear(listError);
        }
    }

    function fullPath(relative) {
        const loc = location();
        return moveJoin(loc ? loc.Path : '', relative);
    }

    function renderCrumbs() {
        moveClear(crumbs);
        const loc = location();
        const segments = state.relativePath ? state.relativePath.split('\\') : [];
        const names = [moveLeaf(loc ? loc.Path : '') || (library() || {}).Name].concat(segments);
        names.forEach(function (name, index) {
            const relative = segments.slice(0, index).join('\\');
            const current = index === names.length - 1;
            const crumb = kit.button('', { className: moveClass('crumb') });
            crumb.append(moveName(name));
            if (current) {
                crumb.setAttribute('aria-current', 'location');
            } else {
                crumb.addEventListener('click', function () {
                    openFolder(relative);
                });
            }

            crumbs.append(moveEl('li', {}, crumb));
        });
        const lib = library();
        const series = item.Kind === 'Episode' && !state.relativePath;
        newFolderLabel.textContent = series ? 'New series folder (optional)' : 'New folder (optional)';
        newFolderInput.placeholder = series ? moveSeriesFolder() : '';
        if (lib) {
            newFolderInput.setAttribute('aria-label', newFolderLabel.textContent + ' in ' + moveShown(names[names.length - 1] || lib.Name));
        }
    }

    // The episode's series folder, as a hint for "New series folder": the first folder under its library folder.
    function moveSeriesFolder() {
        const rel = moveRelative(source.LocationPath || '', unit.Root || '');
        return rel && rel !== unit.Root ? rel.split('\\')[0] : '';
    }

    function badgeOf(folder) {
        const own = movePathKey(fullPath(folder.RelativePath)) === movePathKey(unit.Root);
        if (own) {
            return unit.Kind === 'Folder' ? 'This item\'s folder' : 'Where it is now';
        }

        return Object.prototype.hasOwnProperty.call(MOVE_FOLDER_BADGES, folder.Kind) ? MOVE_FOLDER_BADGES[folder.Kind] : null;
    }

    function folderRow(folder) {
        const badge = badgeOf(folder);
        const button = moveEl('button', { type: 'button', class: moveClass('folder') }, [
            kit.icon(folder.Selectable ? 'folder' : 'folder_off', moveClass('folder-icon')),
            moveEl('span', { class: moveClass('folder-name') }, moveName(folder.Name)),
            badge ? moveEl('span', { class: moveClass('badge'), text: badge }) : null
        ]);
        const reason = folder.Selectable ? null : moveEl('span', { class: moveClass('folder-reason'), id: kit.cls('move-reason-' + (++moveDialogs.sections)), text: moveShown(folder.Reason || 'It can\'t be chosen.') });
        if (reason) {
            button.setAttribute('aria-disabled', 'true');
            button.setAttribute('aria-describedby', reason.id);
            button.title = moveShown(folder.Reason || '');
            button.classList.add(moveClass('folder-disabled'));
        } else {
            button.title = 'Open ' + moveShown(folder.Name);
            button.addEventListener('click', function () {
                openFolder(folder.RelativePath);
            });
        }

        return moveEl('li', { class: moveClass('folder-row') }, [button, reason]);
    }

    function openFolder(relative) {
        state.relativePath = String(relative || '').replace(/^[\\/]+|[\\/]+$/g, '').replace(/\//g, '\\');
        state.filter = '';
        filter.setSearch('');
        renderCrumbs();
        loadFolders(false);
        changed(true);
        // The row or crumb that had the focus went with the old list: it follows to the breadcrumb's current folder, so
        // a screen reader says where it is. Never the filter: on a phone, focusing it opens the on-screen keyboard.
        Promise.resolve().then(function () {
            if (!state.destroyed && host.contains(document.activeElement) === false) {
                const current = crumbs.querySelector('[aria-current]');
                if (current) {
                    current.focus();
                }
            }
        });
    }

    function loadFolders(more) {
        if (!usable()) {
            return;
        }

        if (state.abort) {
            state.abort.abort();
        }

        const seq = ++state.seq;
        const controller = typeof AbortController === 'function' ? new AbortController() : null;
        state.abort = controller;
        state.loading = true;
        const lib = library();
        const loc = location();
        const start = more ? state.folders.length : 0;
        if (!more) {
            state.folders = [];
            moveClear(list);
            count.textContent = 'Loading folders…';
        }

        moveClear(listError);
        pager.update({ busy: true });
        moveGetFolders({
            libraryId: lib.LibraryId,
            locationIndex: loc.Index,
            relativePath: state.relativePath || undefined,
            filter: state.filter || undefined,
            startIndex: start,
            limit: MOVE_FOLDER_PAGE,
            itemId: session.itemId
        }, controller ? controller.signal : undefined).then(function (page) {
            if (seq !== state.seq || state.destroyed) {
                return;
            }

            state.loading = false;
            const items = Array.isArray(page && page.Items) ? page.Items : [];
            state.total = page && typeof page.TotalRecordCount === 'number' ? page.TotalRecordCount : items.length;
            state.folders = state.folders.concat(items);
            items.forEach(function (folder) {
                list.append(folderRow(folder));
            });
            count.textContent = state.total === 0
                ? (state.filter ? 'No folder here matches "' + state.filter + '".' : 'No folders here.')
                : (state.total > state.folders.length
                    ? 'Showing ' + state.folders.length + ' of ' + movePlural(state.total, 'folder', 'folders') + '.'
                    : movePlural(state.total, 'folder', 'folders') + '.');
            pager.update({ hasMore: state.folders.length < state.total, busy: false });
        }, function (error) {
            if (seq !== state.seq || state.destroyed || (controller && controller.signal.aborted)) {
                return;
            }

            state.loading = false;
            pager.update({ hasMore: false, busy: false });
            count.textContent = '';
            listError.append(moveErrorBox(error, function () {
                loadFolders(more);
            }));
        });
    }

    function selectLibrary(index) {
        state.library = index;
        state.relativePath = '';
        state.filter = '';
        filter.setSearch('');
        fillLocations();
        showLocation();
        renderCrumbs();
        loadFolders(false);
    }

    function showWrap() {
        const effective = state.wrap === null ? state.plannedWrap : state.wrap;
        wrapBox.checked = effective === true;
        wrapState.textContent = state.wrap === null
            ? (state.plannedWrap === null ? 'As the settings say for the chosen folder.' : 'As the settings say for this folder.')
            : '';
        kit.setHidden(wrapReset, state.wrap === null);
        kit.setHidden(wrapNameField, effective !== true);
    }

    librarySelect.addEventListener('change', function () {
        selectLibrary(Number(librarySelect.value) || 0);
        changed(true);
    });
    locationSelect.addEventListener('change', function () {
        state.location = Number(locationSelect.value) || 0;
        state.relativePath = '';
        state.filter = '';
        filter.setSearch('');
        showLocation();
        renderCrumbs();
        loadFolders(false);
        changed(true);
    });
    newFolderInput.addEventListener('input', function () {
        state.newFolder = String(newFolderInput.value || '').trim();
        const problem = moveNameProblem(state.newFolder);
        newFolderError.textContent = problem || '';
        newFolderInput.setAttribute('aria-invalid', problem ? 'true' : 'false');
        changed(false);
    });
    wrapBox.addEventListener('change', function () {
        state.wrap = !!wrapBox.checked;
        showWrap();
        changed(true);
    });
    wrapReset.addEventListener('click', function () {
        state.wrap = null;
        showWrap();
        changed(true);
        wrapBox.focus();
    });
    wrapNameInput.addEventListener('input', function () {
        state.wrapName = String(wrapNameInput.value || '').trim();
        const problem = moveNameProblem(state.wrapName);
        wrapNameError.textContent = problem || '';
        wrapNameInput.setAttribute('aria-invalid', problem ? 'true' : 'false');
        changed(false);
    });

    if (libraries.length > 0) {
        librarySelect.value = '0';
        selectLibrary(0);
    } else {
        [locationField, crumbNav, filter, count, list, newFolderField, wrapGroup].forEach(function (node) {
            kit.setHidden(node, true);
        });
    }

    showWrap();
    return {
        destination: destination,
        firstControl: function () {
            return libraries.length > 0 ? librarySelect : null;
        },
        // What the server planned: the wrap it chose while the admin left it to the settings, and its folder's name.
        planned: function (plan) {
            const first = plan && Array.isArray(plan.Units) ? plan.Units[0] : null;
            state.plannedWrap = first ? !!first.WrapFolderName : null;
            wrapNameInput.placeholder = first && first.WrapFolderName ? moveShown(first.WrapFolderName) : '';
            showWrap();
        },
        destroy: function () {
            state.destroyed = true;
            if (state.abort) {
                state.abort.abort();
            }

            pager.destroy();
        }
    };
}
