// The Move dialog's preview: POST /Move/Plan for the chosen place, 400 ms after the admin's
// last change (at once for a click), the older request cancelled. It shows the blockers (red) and warnings (amber) with
// a title for each code and the server's own sentence, then From and To, how (an instant rename on the same drive, or a
// copy to another drive, which v1 blocks with move.cross-volume-later), the files and bytes, the space needed on each
// drive, the folders it would create, what is kept (counted now, a preview: the move reads them again) and what is
// regenerated. "Check again" plans again (a file in use, someone playing it). A plan is kept 15 minutes on the
// server; once its time is up the preview says it is out of date and the next change, or "Preview again", plans anew.

const MOVE_PREVIEW_DELAY_MS = 400;
const MOVE_ISSUE_PATHS_SHOWN = 20;

const MOVE_CARRY_OVER_TEXT = Object.freeze({
    AllEditableFields: 'every field edited in Jellyfin',
    LockedFieldsOnly: 'the locked fields only',
    None: 'none (the new item reads its metadata again)'
});

function createMovePreview(session, host) {
    const state = { seq: 0, timer: null, abort: null, plan: null, expiry: null, stale: false, destroyed: false };
    const statusLine = moveEl('p', { class: moveClass('preview-status'), role: 'status' });
    const output = moveEl('div', { class: moveClass('preview-output') });
    const again = kit.button('Preview this folder', { className: moveClass('preview-again') });
    kit.append(host, statusLine, output, moveEl('div', { class: moveClass('preview-actions') }, again));
    statusLine.textContent = 'Choose where it goes, then the preview shows what the move would do.';
    again.addEventListener('click', function () {
        run(session.picker ? session.picker.destination() : null);
    });

    function clearTimers() {
        clearTimeout(state.timer);
        state.timer = null;
        clearTimeout(state.expiry);
        state.expiry = null;
    }

    function cancel() {
        clearTimers();
        state.seq++;
        if (state.abort) {
            state.abort.abort();
            state.abort = null;
        }
    }

    function setMoveButton(plan) {
        const button = session.moveButton;
        if (!button) {
            return;
        }

        const blocked = !plan || state.stale || (plan.Blockers || []).length > 0;
        button.disabled = blocked;
        const files = plan && plan.Units && plan.Units[0] ? plan.Units[0].Files : 0;
        button.textContent = files > 0 ? 'Move ' + movePlural(files, 'file', 'files') : 'Move';
    }

    function request(destination, immediate) {
        cancel();
        state.stale = false;
        if (!destination) {
            state.plan = null;
            moveClear(output);
            statusLine.textContent = 'This place can\'t be previewed; choose another.';
            setMoveButton(null);
            return;
        }

        statusLine.textContent = 'Previewing…';
        output.setAttribute('aria-busy', 'true');
        setMoveButton(null);
        state.timer = setTimeout(function () {
            run(destination);
        }, immediate ? 0 : MOVE_PREVIEW_DELAY_MS);
    }

    function run(destination) {
        cancel();
        state.stale = false;
        if (!destination) {
            request(null);
            return;
        }

        const seq = state.seq;
        const controller = typeof AbortController === 'function' ? new AbortController() : null;
        state.abort = controller;
        statusLine.textContent = 'Previewing…';
        output.setAttribute('aria-busy', 'true');
        setMoveButton(null);
        movePostPlan({ ItemIds: [session.itemId], Destination: destination }, controller ? controller.signal : undefined).then(function (plan) {
            if (seq !== state.seq || state.destroyed) {
                return;
            }

            state.abort = null;
            state.plan = plan;
            output.removeAttribute('aria-busy');
            render(plan);
            if (session.picker) {
                session.picker.planned(plan);
            }

            watchExpiry(plan);
        }, function (error) {
            if (seq !== state.seq || state.destroyed || (controller && controller.signal.aborted)) {
                return;
            }

            state.abort = null;
            state.plan = null;
            output.removeAttribute('aria-busy');
            moveClear(output);
            output.append(moveErrorBox(error, function () {
                run(destination);
            }));
            statusLine.textContent = 'No preview: ' + moveErrorInfo(error).title + '.';
            again.textContent = 'Check again';
            setMoveButton(null);
            if (error && error.status === 409) {
                moveRememberRefusal(session.itemId, error);
            }
        });
    }

    function watchExpiry(plan) {
        const expires = Date.parse(plan && plan.ExpiresUtc);
        if (!isFinite(expires)) {
            return;
        }

        state.expiry = setTimeout(function () {
            state.expiry = null;
            if (state.destroyed || state.plan !== plan) {
                return;
            }

            state.stale = true;
            statusLine.textContent = 'This preview is out of date. Preview again to see what a move would do now.';
            output.classList.add(moveClass('stale'));
            again.textContent = 'Preview again';
            setMoveButton(plan);
        }, Math.max(0, expires - Date.now()));
    }

    function render(plan) {
        moveClear(output);
        output.classList.remove(moveClass('stale'));
        const blockers = Array.isArray(plan.Blockers) ? plan.Blockers : [];
        const warnings = Array.isArray(plan.Warnings) ? plan.Warnings : [];
        const unit = Array.isArray(plan.Units) && plan.Units[0] ? plan.Units[0] : {};
        // "could" while this build can't move: the preview never says a move can happen.
        const can = session.caps && session.caps.CanMove ? 'It can move there' : 'It could move there';
        statusLine.textContent = blockers.length > 0
            ? movePlural(blockers.length, 'blocker', 'blockers') + ': it can\'t move there now.'
            : (warnings.length > 0 ? can + ', with ' + movePlural(warnings.length, 'warning', 'warnings') + '.' : can + ': nothing stands in the way.');
        again.textContent = blockers.length > 0 ? 'Check again' : 'Preview again';
        kit.append(output,
            moveIssues('Blockers', 'blocker', blockers),
            moveIssues('Warnings', 'warning', warnings),
            moveFacts(plan, unit),
            moveKept(plan.Preserved),
            moveList('Regenerated or lost', Array.isArray(plan.Regenerated) ? plan.Regenerated.map(moveShown) : []));
        setMoveButton(plan);
    }

    return {
        request: request,
        // The plan shown now (null while there is none, or it failed).
        plan: function () {
            return state.plan;
        },
        destroy: function () {
            state.destroyed = true;
            cancel();
        }
    };
}

// The blockers or the warnings: each its title, the server's sentence and the paths it names.
function moveIssues(label, kind, issues) {
    if (issues.length === 0) {
        return null;
    }

    const id = kit.cls('move-' + kind + 's-' + (++moveDialogs.sections));
    return moveEl('div', { class: classesOf(moveClass('issues'), moveClass(kind + 's')) }, [
        moveEl('p', { id: id, class: moveClass('issues-label'), text: label }),
        moveEl('ul', { class: moveClass('issue-list'), 'aria-labelledby': id }, issues.map(function (issue) {
            const paths = Array.isArray(issue.Paths) ? issue.Paths : [];
            return moveEl('li', { class: moveClass(kind), 'data-code': issue.Code || '' }, [
                kit.icon(kind === 'blocker' ? 'block' : 'warning', moveClass('issue-icon')),
                moveEl('div', { class: moveClass('issue-text') }, [
                    moveEl('p', { class: moveClass('issue-title'), text: moveCodeTitle(issue.Code) }),
                    moveEl('p', { class: moveClass('issue-message'), text: moveShown(issue.Message || '') }),
                    paths.length > 0 ? moveEl('ul', { class: moveClass('names') }, paths.slice(0, MOVE_ISSUE_PATHS_SHOWN).map(function (path) {
                        return moveEl('li', {}, moveName(path));
                    }).concat(paths.length > MOVE_ISSUE_PATHS_SHOWN ? [moveEl('li', { text: 'and ' + (paths.length - MOVE_ISSUE_PATHS_SHOWN) + ' more' })] : [])) : null
                ])
            ]);
        }))
    ]);
}

// A value is plain text (a string) or nodes (names and paths, isolated).
function moveFact(label, value) {
    if (value === null || value === undefined || value === '') {
        return null;
    }

    return [moveEl('dt', { text: label }), typeof value === 'string' ? moveEl('dd', { text: value }) : moveEl('dd', {}, value)];
}

// From, To, how, the files, the space, the folders it creates and the time it takes.
function moveFacts(plan, unit) {
    const volumes = Array.isArray(plan.Volumes) ? plan.Volumes : [];
    const created = Array.isArray(unit.CreatedFolders) ? unit.CreatedFolders : [];
    const space = volumes.map(function (volume) {
        const needed = volume.RequiredBytes || 0;
        const free = typeof volume.FreeBytes === 'number' ? ', ' + kit.format.size(volume.FreeBytes) + ' free' : '';
        return needed > 0
            ? moveShown(volume.Root) + ': ' + kit.format.size(needed) + ' needed' + free
            : moveShown(volume.Root) + ': no extra space needed (a rename)' + free;
    });
    const rows = [
        moveFact('From', unit.From ? moveName(unit.From) : null),
        moveFact('To', unit.To ? moveName(unit.To) : null),
        moveFact('How', Object.prototype.hasOwnProperty.call(MOVE_METHOD_TEXT, plan.Method) ? MOVE_METHOD_TEXT[plan.Method] : plan.Method),
        moveFact('Moves', typeof unit.Files === 'number' ? movePlural(unit.Files, 'file', 'files') + ', ' + kit.format.size(unit.Bytes || 0) : null),
        moveFact('Space', space.length > 0 ? space.join('; ') : null),
        moveFact('Creates', created.length > 0 ? moveNames(created, '; ') : 'No new folder'),
        moveFact('Its own folder', unit.WrapFolderName ? moveName(unit.WrapFolderName) : null),
        moveFact('Takes', typeof plan.EstimatedSeconds === 'number' ? 'About ' + movePlural(plan.EstimatedSeconds, 'second', 'seconds') : null)
    ];
    const list = moveEl('dl', { class: moveClass('facts') });
    rows.forEach(function (row) {
        if (row) {
            kit.append(list, row[0], row[1]);
        }
    });
    return list;
}

// "Kept": counted now, as a preview; the move itself snapshots them when it runs.
function moveKept(kept) {
    if (!kept) {
        return null;
    }

    const lines = [];
    lines.push('Watch state of ' + movePlural(kept.UserDataUsers || 0, 'user', 'users')
        + ' (' + movePlural(kept.Favorites || 0, 'favourite', 'favourites') + ', ' + movePlural(kept.ResumePoints || 0, 'resume point', 'resume points') + ')');
    if (kept.DateCreated) {
        lines.push('Date added: ' + kit.format.date(kept.DateCreated));
    }

    const collections = Array.isArray(kept.Collections) ? kept.Collections : [];
    const playlists = Array.isArray(kept.Playlists) ? kept.Playlists : [];
    lines.push(collections.length > 0 ? [movePlural(collections.length, 'collection', 'collections') + ': '].concat(moveNames(collections, ', ')) : 'No collections');
    lines.push(playlists.length > 0 ? [movePlural(playlists.length, 'playlist', 'playlists') + ': '].concat(moveNames(playlists, ', ')) : 'No playlists');
    const locked = Array.isArray(kept.LockedFields) ? kept.LockedFields : [];
    lines.push('Metadata edits: ' + (Object.prototype.hasOwnProperty.call(MOVE_CARRY_OVER_TEXT, kept.CarryOverMode) ? MOVE_CARRY_OVER_TEXT[kept.CarryOverMode] : String(kept.CarryOverMode || '')) + (locked.length > 0 ? '; locked: ' + locked.join(', ') : ''));
    lines.push('Images kept by Jellyfin: ' + (kept.InternalImages || 0));
    if (typeof kept.InternalSubtitles === 'number') {
        lines.push('Downloaded subtitles: ' + kept.InternalSubtitles);
    }

    if (typeof kept.TrickplayResolutions === 'number') {
        lines.push('Trickplay: ' + movePlural(kept.TrickplayResolutions, 'resolution', 'resolutions'));
    }

    const hooks = Array.isArray(kept.HookConsumers) ? kept.HookConsumers : [];
    if (hooks.length > 0) {
        lines.push(['Updated in other plugins: '].concat(moveNames(hooks, ', ')));
    }

    return moveList('Kept (counted now; the move counts again when it runs)', lines);
}
