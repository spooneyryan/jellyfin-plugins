// The Move dialog (its preview part): a kit.nativeDialog (jellyfin-web's dialogHelper: history-enabled,
// so Back closes it and never leaves the page), opened after the menu's sheet closed. It loads what moves
// (GET /Move/Source) and where it can go (GET /Move/Destinations), then shows the picker (51-move-picker.js) and the
// server's plan of the chosen place as a preview (55-move-preview.js). Every value from the server is written as text.
//
// This build only previews: Capabilities.CanMove is false, so the footer has no Move button and says so. Nothing the
// dialog does writes to disk: Source, Destinations, Folders and Plan only read (planning writes
// nothing). While CanMove is false that can't be missed: the intro starts with "Preview only:
// nothing is moved.", the footer with its note sits below the dialog's scrolling content (in the dialog itself, as
// jellyfin-web's own form footers), so it is in view however far the content scrolls, and the preview says what a move
// could do, never what it can.
//
// Keys and focus: Tab stays inside the dialog and Escape closes it on every layout (the kit's trapFocus);
// the focus starts on the library choice once the dialog has loaded, on Close while it loads or failed (autoFocus:
// false, or dialogHelper would move it to the header's back button when its entry animation ends). Since 1.0.2:
// Escape in a text field with text ("Filter folders", "New folder", its own folder's name) empties it first, and
// the next Escape closes (the kit's escapeClearsText); once closed, the focus goes back to what opened the menu (the
// card's or row's menu button, the details page's More: the kit's ctx.trigger and returnFocus).
// Close: the dialog's onClose runs after its history pop; a finished move navigates there
// with location.replace when the dialog was opened from the old item's details page.

const MOVE_DIALOG_TITLE = 'Move media';
const MOVE_PREVIEW_ONLY_TEXT = 'This version of Library Maintenance only previews moves: it shows what would happen, and nothing is moved.';
const MOVE_PREVIEW_ONLY_LEAD = 'Preview only: nothing is moved.';

function moveClass(name) {
    return kit.cls('move-' + name);
}

function moveEl(tag, props, children) {
    return kit.h(tag, props, children);
}

function moveClear(node) {
    while (node && node.firstChild) {
        node.removeChild(node.firstChild);
    }
}

// A section of the dialog with its heading (h4: the dialog's title is the h3).
function moveSection(name, title) {
    const heading = moveEl('h4', { id: kit.cls('move-' + name + '-' + (++moveDialogs.sections)), class: moveClass('heading'), text: title });
    const body = moveEl('div', { class: moveClass(name + '-body') });
    const section = moveEl('section', { class: classesOf(moveClass('section'), moveClass(name)), 'aria-labelledby': heading.id }, [heading, body]);
    return { section: section, heading: heading, body: body };
}

function classesOf() {
    return Array.prototype.slice.call(arguments).filter(Boolean).join(' ');
}

// Names are shown, not obeyed: a name or path from the server (an item's, a file's, a folder's, a library's, a collection's
// or a playlist's) goes in its own <bdi>, so a right-to-left name can't reorder the words, sizes and years beside it; and
// a bidi control in it (U+202E turns the rest of a line around) is shown as what it is, [U+202E], never obeyed. Sizes and
// years are nodes of their own, outside the name's. Where no element fits (an attribute, an option, the server's own
// sentence) the controls are shown the same way.
const MOVE_BIDI_CONTROLS = /[\u061C\u200E\u200F\u202A-\u202E\u2066-\u2069]/g;

function moveControlName(control) {
    return '[U+' + control.charCodeAt(0).toString(16).toUpperCase().padStart(4, '0') + ']';
}

// A server's string as plain text: its bidi controls shown, not obeyed.
function moveShown(value) {
    return String(value === undefined || value === null ? '' : value).replace(MOVE_BIDI_CONTROLS, moveControlName);
}

// A name or a path as its own isolated node.
function moveName(value) {
    const text = String(value === undefined || value === null ? '' : value);
    const node = moveEl('bdi', { class: moveClass('name') });
    let last = 0;
    text.replace(MOVE_BIDI_CONTROLS, function (control, at) {
        if (at > last) {
            node.append(text.substring(last, at));
        }

        node.append(moveEl('span', { class: moveClass('bidi-control'), title: 'A text direction character', text: moveControlName(control) }));
        last = at + control.length;
        return control;
    });
    if (last < text.length) {
        node.append(text.substring(last));
    }

    return node;
}

// Names with a separator between them, each isolated.
function moveNames(values, separator) {
    const out = [];
    values.forEach(function (value, index) {
        if (index > 0) {
            out.push(separator);
        }

        out.push(moveName(value));
    });
    return out;
}

// A size or a year, in its own node after a name: "name (1.0 MB)".
function moveWithAside(name, aside) {
    return [moveName(name), ' ', moveEl('span', { class: moveClass('aside'), text: '(' + aside + ')' })];
}

// An error box: the human title, the server's sentence, and Retry when asking again may help.
function moveErrorBox(error, onRetry) {
    const info = moveErrorInfo(error);
    return moveEl('div', { class: moveClass('error'), role: 'alert' }, [
        moveEl('p', { class: moveClass('error-title'), text: info.title }),
        moveEl('p', { class: moveClass('error-detail'), text: moveShown(info.detail) }),
        info.retry && onRetry ? kit.button('Try again', { className: moveClass('retry'), onClick: onRetry }) : null
    ]);
}

const moveDialogs = { open: null, sections: 0 };

// opts: { itemId, serverId, surface, trigger }: trigger is what opened the menu, the focus's place after closing.
function openMoveDialog(opts) {
    if (moveDialogs.open && !moveDialogs.open.closed) {
        return moveDialogs.open;
    }

    const dialog = kit.nativeDialog({ title: MOVE_DIALOG_TITLE, trapFocus: true, autoFocus: false, escapeClearsText: true, returnFocus: opts.trigger || null });
    const session = {
        itemId: opts.itemId,
        serverId: opts.serverId || null,
        surface: opts.surface || null,
        openedOn: window.location.hash,
        dialog: dialog,
        caps: moveCapabilities() || MOVE_NO_CAPABILITIES,
        source: null,
        destinations: null,
        picker: null,
        preview: null,
        // Set by the run part when a move has finished: { newId, serverId }.
        moved: null,
        closed: false
    };
    moveDialogs.open = session;
    dialog.panel.classList.add(kit.cls('move-dialog'));

    const intro = moveEl('p', { class: moveClass('intro') });
    const introLead = moveEl('strong', { class: moveClass('preview-only-lead'), text: MOVE_PREVIEW_ONLY_LEAD });
    const introText = moveEl('span', { class: moveClass('intro-text') });
    kit.append(intro, introLead, introText);
    const status = moveEl('div', { class: moveClass('status'), role: 'status', 'aria-live': 'polite' });
    const content = moveEl('div', { class: moveClass('content') });
    const footer = moveEl('div', { class: moveClass('footer') });
    const note = moveEl('p', { class: moveClass('preview-only'), text: MOVE_PREVIEW_ONLY_TEXT });
    const closeButton = kit.button('Close', { className: moveClass('close'), onClick: dialog.close });
    // The run part's button: never shown while the server says this build can't move.
    const moveButton = kit.button('Move', { variant: 'primary', className: moveClass('run') });
    moveButton.disabled = true;
    const showCanMove = function () {
        kit.setHidden(moveButton, !session.caps.CanMove);
        kit.setHidden(note, !!session.caps.CanMove);
        kit.setHidden(introLead, !!session.caps.CanMove);
    };
    showCanMove();
    kit.append(footer, note, moveEl('div', { class: moveClass('footer-buttons') }, [closeButton, moveButton]));
    kit.append(dialog.body, intro, status, content);
    // Below the scrolling content, in the dialog itself (a column: the header, the content, this), so the note and Close
    // are always in view; kit.dialog's fallback panel is a column too.
    kit.append(dialog.panel, footer);
    session.moveButton = moveButton;
    session.status = status;

    dialog.onClose(function () {
        session.closed = true;
        if (moveDialogs.open === session) {
            moveDialogs.open = null;
        }

        if (session.preview) {
            session.preview.destroy();
        }

        if (session.picker) {
            session.picker.destroy();
        }

        moveAfterClose(session);
    });

    const load = function () {
        moveClear(content);
        introText.textContent = 'Loading the item…';
        status.textContent = 'Loading';
        focusLater(closeButton);
        Promise.all([moveGetSource(session.itemId), moveGetDestinations(session.itemId), moveLoadCapabilities(false)]).then(function (answers) {
            if (session.closed) {
                return;
            }

            session.source = answers[0];
            session.destinations = Array.isArray(answers[1]) ? answers[1] : [];
            session.caps = answers[2] || session.caps;
            showCanMove();
            renderLoaded();
        }, function (error) {
            if (session.closed) {
                return;
            }

            moveRememberRefusal(session.itemId, error);
            introText.textContent = '';
            status.textContent = 'Couldn\'t load the item';
            moveClear(content);
            content.append(moveErrorBox(error, load));
            focusLater(content.querySelector('button') || closeButton);
        });
    };

    const renderLoaded = function () {
        introText.textContent = 'Choose where it goes; the preview below shows what the move would do.';
        status.textContent = '';
        moveClear(content);
        const what = moveSection('source', 'What moves');
        what.body.append(moveSourceView(session.source));
        const where = moveSection('destination', 'Where to');
        const shown = moveSection('preview', 'Preview');
        session.preview = createMovePreview(session, shown.body);
        session.picker = createMovePicker(session, where.body, function (destination, immediate) {
            session.preview.request(destination, immediate);
        });
        kit.append(content, what.section, where.section, shown.section);
        focusLater(session.picker.firstControl() || closeButton);
    };

    // After the caller's microtask (as kit.dialog's focus), and only while nothing of ours has it already.
    function focusLater(target) {
        Promise.resolve().then(function () {
            if (!session.closed && target && target.isConnected && !content.contains(document.activeElement)) {
                try {
                    target.focus();
                } catch (e) {
                    // can't take the focus
                }
            }
        });
    }

    load();
    return session;
}

// After the dialog's history pop: a finished move opened from the old item's details page replaces that
// page, so Back never returns to it; from anywhere else the moved item is pushed. Nothing to do while nothing moved.
function moveAfterClose(session) {
    if (!session.moved || !session.moved.newId) {
        return;
    }

    const href = '#/details?id=' + encodeURIComponent(session.moved.newId) + (session.moved.serverId ? '&serverId=' + encodeURIComponent(session.moved.serverId) : '');
    const params = kit.hashParams();
    if (moveOnDetailsPage() && moveIdKey(params.get('id')) === moveIdKey(session.itemId)) {
        window.location.replace(href);
    } else {
        window.location.hash = href.substring(1);
    }
}

// "What moves": the item, its library and folder, the unit (the whole folder, or the files that move
// together), its versions, parts and extras, and the sentence for a version or part opened from its own menu.
function moveSourceView(source) {
    const item = source.Item || {};
    const unit = source.Unit || {};
    const files = Array.isArray(unit.Files) ? unit.Files : [];
    const dirs = Array.isArray(unit.Dirs) ? unit.Dirs : [];
    const versions = Array.isArray(unit.Versions) ? unit.Versions : [];
    const parts = Array.isArray(unit.Parts) ? unit.Parts : [];
    const extras = Array.isArray(unit.Extras) ? unit.Extras : [];
    const root = String(unit.Root || '');
    const full = function (rel) {
        return moveJoin(root, rel);
    };
    const members = [unit.Primary].concat(versions, parts).filter(Boolean).map(movePathKey);
    const extraKeys = extras.map(movePathKey);
    const sidecars = files.filter(function (file) {
        const key = movePathKey(full(file.Rel));
        return members.indexOf(key) < 0 && extraKeys.indexOf(key) < 0;
    });
    const size = kit.format.size(unit.TotalBytes || 0);
    let summary;
    if (unit.Kind === 'Folder') {
        summary = ['The whole folder ', moveName(moveLeaf(root)), ' moves: ' + movePlural(files.length, 'file', 'files') + ', '
            + movePlural(dirs.length, 'subfolder', 'subfolders') + ', ' + size + '.'];
    } else {
        const what = parts.length > 0 ? 'These ' + (parts.length + 1) + ' parts'
            : (versions.length > 0 ? 'These ' + (versions.length + 1) + ' versions' : 'This file');
        const alone = what === 'This file' && sidecars.length === 0;
        summary = alone ? 'This file moves: ' + size + '.'
            : what + (sidecars.length > 0 ? ' and ' + movePlural(sidecars.length, 'sidecar file', 'sidecar files') : '') + ' move together: ' + size + '.';
    }

    // The year once: Jellyfin keeps it in the name of a folder it didn't parse ("Folder Movie (2008)", Year 2008).
    const name = String(item.Name || 'Untitled');
    const title = item.Year && !name.trim().endsWith('(' + item.Year + ')') ? moveWithAside(name, item.Year) : moveName(name);
    const library = source.Library || {};
    return moveEl('div', { class: moveClass('source') }, [
        moveEl('p', { class: moveClass('item') }, [
            moveEl('span', { class: moveClass('item-title') }, title),
            moveEl('span', { class: moveClass('chip'), text: moveKindText(item.Kind) })
        ]),
        moveEl('p', { class: moveClass('where') }, ['In ', library.Name ? moveName(library.Name) : 'its library', ': ',
            moveName(unit.Kind === 'Folder' ? root : moveJoin(root, moveLeaf(unit.Primary || '')))]),
        source.Retargeted ? moveEl('p', { class: moveClass('retargeted'), text: moveShown(source.Retargeted) }) : null,
        moveEl('p', { class: moveClass('summary') }, summary),
        moveList('Versions', versions.map(function (path) {
            return moveName(moveLeaf(path));
        })),
        moveList('Parts', parts.map(function (path) {
            return moveName(moveLeaf(path));
        })),
        moveList('Extras', extras.map(function (path) {
            return moveName(moveRelative(root, path));
        })),
        unit.Kind === 'Folder' ? null : moveList('Sidecar files', sidecars.map(function (file) {
            return moveWithAside(file.Rel, kit.format.size(file.Bytes || 0));
        })),
        moveFilesDetails(files)
    ]);
}

// A labelled list, or nothing when it is empty. A value is plain text (a string) or nodes (a name, isolated).
function moveList(label, values) {
    if (!values || values.length === 0) {
        return null;
    }

    return moveEl('div', { class: moveClass('list') }, [
        moveEl('p', { class: moveClass('list-label'), text: label + ' (' + values.length + ')' }),
        moveEl('ul', { class: moveClass('names') }, values.map(function (value) {
            return typeof value === 'string' ? moveEl('li', { text: value }) : moveEl('li', {}, value);
        }))
    ]);
}

// Every file, folded (a folder can hold many): the first 200 and how many more.
function moveFilesDetails(files) {
    if (files.length === 0) {
        return null;
    }

    const shown = files.slice(0, 200);
    const list = moveEl('ul', { class: moveClass('names') }, shown.map(function (file) {
        return moveEl('li', {}, moveWithAside(file.Rel, kit.format.size(file.Bytes || 0)));
    }));
    if (files.length > shown.length) {
        list.append(moveEl('li', { class: moveClass('more'), text: 'and ' + (files.length - shown.length) + ' more' }));
    }

    return moveEl('details', { class: moveClass('files') }, [moveEl('summary', { text: 'Every file (' + files.length + ')' }), list]);
}

// Windows paths as the server writes them: joined with one backslash, compared ignoring case and the separator.
function moveJoin(root, rel) {
    const left = String(root || '').replace(/[\\/]+$/, '');
    const right = String(rel || '').replace(/^[\\/]+/, '');
    return right ? left + '\\' + right : left;
}

function movePathKey(path) {
    return String(path || '').replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase();
}

function moveLeaf(path) {
    const parts = String(path || '').split(/[\\/]/).filter(Boolean);
    return parts.length > 0 ? parts[parts.length - 1] : String(path || '');
}

function moveRelative(root, path) {
    const base = movePathKey(root) + '\\';
    return movePathKey(path).indexOf(base) === 0 ? String(path).substring(base.length) : String(path || '');
}
