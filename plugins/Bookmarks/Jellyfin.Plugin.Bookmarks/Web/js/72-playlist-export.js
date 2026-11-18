// Playlist exports (since 1.5.0 / 2.3.0): on a playlist's page an "Export to clips folder" button (before the "more"
// button, as Add bookmark is on a video's) opens a dialog with what the export would make: how many videos are joined,
// the entries left out and why, the running time, the picture size and frame rate, the estimated size and time, the file
// name, and a warning for a very large one (GET /Bookmarks/Playlists/{id}/Export). Export starts it
// (POST /Bookmarks/Playlists/{id}/Export); the dialog then shows its progress and can cancel it, or be closed while it goes
// on. Its progress also shows on the My Clips page, above the clips (GET /Bookmarks/Exports), with Cancel; a toast says
// when it is done (or failed) wherever the user is then.

const EXPORT_POLL_MS = 2000;

// The exports being followed in this tab: id → { timer, listeners }.
const exportWatchers = new Map();

function isExportablePlaylist(item) {
    return !!item && item.Type === 'Playlist';
}

// The details page's button, for a user who may make clips (Capabilities.CanCreateClips).
function addPlaylistExportButton(page, playlist) {
    const buttons = findAnchor('detailButtons', page);
    if (!buttons || buttons.querySelector('.jfbm-export-playlist')) {
        return;
    }

    const button = kit.detailButton({
        className: 'jfbm-export-playlist',
        icon: 'movie',
        title: 'Export to clips folder',
        onClick: function () {
            openPlaylistExport(playlist, button);
        }
    });
    buttons.insertBefore(button, findAnchor('detailButtonsBefore', buttons));
}

// "about 25 minutes", "about 2 hours 10 minutes", "under a minute".
function roughDuration(seconds) {
    const minutes = Math.round((seconds || 0) / 60);
    if (minutes < 1) {
        return 'under a minute';
    }

    if (minutes < 60) {
        return 'about ' + minutes + (minutes === 1 ? ' minute' : ' minutes');
    }

    const hours = Math.floor(minutes / 60);
    const rest = minutes % 60;
    return 'about ' + hours + (hours === 1 ? ' hour' : ' hours') + (rest > 0 ? ' ' + rest + (rest === 1 ? ' minute' : ' minutes') : '');
}

// 23.976, 25, 29.97.
function formatFrameRate(rate) {
    return String(Math.round((rate || 0) * 1000) / 1000);
}

function exportFact(label, value, className) {
    return h('div', { class: 'jfbm-export-fact' + (className ? ' ' + className : '') }, [
        h('span', { class: 'jfbm-export-fact-label', text: label }),
        h('span', { class: 'jfbm-export-fact-value', text: value })
    ]);
}

// What the estimate says, as the dialog's lines.
function exportEstimateNodes(estimate) {
    const nodes = [];
    nodes.push(h('p', {
        class: 'jfbm-clip-dialog-text',
        text: estimate.VideoCount > 0
            ? 'The videos of "' + estimate.PlaylistName + '" joined end to end, in playlist order, into one video file in your clips folder. Each is re-encoded with the clip settings.'
            : 'Nothing in "' + estimate.PlaylistName + '" can be exported.'
    }));
    nodes.push(h('div', { class: 'jfbm-export-facts' }, [
        exportFact('Videos', String(estimate.VideoCount), 'jfbm-export-count'),
        exportFact('Running time', formatTime(estimate.DurationTicks), 'jfbm-export-duration'),
        exportFact('Picture', estimate.Width + '×' + estimate.Height + ', ' + formatFrameRate(estimate.FrameRate) + ' fps', 'jfbm-export-resolution'),
        exportFact('Estimated size', formatSize(estimate.EstimatedBytes), 'jfbm-export-size'),
        exportFact('Time to make', roughDuration(estimate.EstimatedSeconds), 'jfbm-export-time'),
        exportFact('File name', estimate.FileName, 'jfbm-export-filename')
    ]));

    const skipped = estimate.Skipped || [];
    if (skipped.length > 0) {
        nodes.push(h('p', { class: kit.cls('hint') + ' jfbm-export-skipped-title', text: skipped.length === 1 ? '1 item is left out:' : skipped.length + ' items are left out:' }));
        nodes.push(h('ul', { class: 'jfbm-export-skipped' }, skipped.map(function (skip) {
            return h('li', null, [
                h('span', { class: 'jfbm-export-skipped-name', text: skip.Name || 'An item' }),
                h('span', { class: kit.cls('hint'), text: ' – ' + skip.Reason })
            ]);
        })));
    }

    if (estimate.IsLarge && estimate.CanExport) {
        nodes.push(h('div', { class: 'jfbm-export-warning', role: 'note' }, [
            icon('warning'),
            h('span', { text: 'This is a very large export (over 3 hours or 20 GB). It is allowed, but it takes a long time and a lot of disk space.' })
        ]));
    }

    if (estimate.Problem) {
        nodes.push(kit.notice({ kind: 'error', text: estimate.Problem }));
    }

    return nodes;
}

// The dialog: the estimate, then Export; once started, the export's progress with Cancel export and Close.
function openPlaylistExport(playlist, returnFocus) {
    const dialog = kit.dialog({ title: 'Export to clips folder', modal: true, scopedKeys: true, returnFocus: returnFocus });
    dialog.panel.classList.add('jfbm-export-dialog');
    const status = h('p', { class: kit.cls('hint') + ' jfbm-export-loading', role: 'status', text: 'Working out the export…' });
    const error = h('div', { class: kit.cls('error') + ' jfbm-clip-dialog-error', role: 'alert' });
    const content = h('div', { class: 'jfbm-export-content' }, status);
    const cancel = kit.button('Cancel', { className: 'jfbm-export-cancel', onClick: dialog.close });
    const start = kit.button('Export', { variant: 'primary', className: 'jfbm-export-start' });
    start.disabled = true;
    const actions = h('div', { class: kit.cls('actions') }, [cancel, start]);
    kit.append(dialog.body, content, error, actions);
    let closed = false;
    dialog.onClose(function () {
        closed = true;
    });

    start.addEventListener('click', async function () {
        start.disabled = true;
        error.textContent = '';
        let job;
        try {
            job = await api('POST', 'Bookmarks/Playlists/' + playlist.Id + '/Export');
        } catch (e) {
            error.textContent = e.message;
            start.disabled = false;
            start.focus();
            return;
        }

        watchExport(job);
        if (!closed) {
            showExportProgress(dialog, content, actions, job);
        }
    });

    (async function () {
        let estimate;
        try {
            estimate = await api('GET', 'Bookmarks/Playlists/' + playlist.Id + '/Export');
        } catch (e) {
            status.textContent = '';
            error.textContent = e.message;
            cancel.focus();
            return;
        }

        if (closed) {
            return;
        }

        content.replaceChildren.apply(content, exportEstimateNodes(estimate));
        start.disabled = !estimate.CanExport;
        (estimate.CanExport ? start : cancel).focus();
    })();

    return dialog;
}

// The dialog once the export started: its progress, Cancel export, and Close (it goes on).
function showExportProgress(dialog, content, actions, job) {
    const view = exportProgressView(job);
    const close = kit.button('Close', { className: 'jfbm-export-close', onClick: dialog.close });
    const stop = kit.button('Cancel export', {
        className: 'jfbm-export-stop',
        onClick: function () {
            cancelExport(job.Id, stop);
        }
    });
    content.replaceChildren(
        h('p', { class: 'jfbm-clip-dialog-text', text: 'Exporting "' + job.PlaylistName + '". You can close this dialog: the export goes on, and its progress shows on the My Clips page.' }),
        view.element);
    actions.replaceChildren(stop, close);
    close.focus();
    const listener = function (current) {
        view.update(current);
        const active = current.State === 'Queued' || current.State === 'Running';
        stop.disabled = !active;
        kit.setHidden(stop, !active);
    };
    addExportListener(job.Id, listener);
    dialog.onClose(function () {
        removeExportListener(job.Id, listener);
    });
}

// A label and progress bar of an export, updated with each poll.
function exportProgressView(job) {
    const label = h('span', { class: 'jfbm-export-state' });
    const bar = h('div', { class: 'jfbm-progress jfbm-export-progress', role: 'progressbar', 'aria-label': 'Export progress', 'aria-valuemin': '0', 'aria-valuemax': '100' }, h('div'));
    const element = h('div', { class: 'jfbm-export-status', role: 'status' }, [label, bar]);
    const update = function (current) {
        const percent = typeof current.Progress === 'number' ? Math.max(0, Math.min(100, Math.floor(current.Progress))) : 0;
        label.textContent = exportStateText(current, percent);
        bar.setAttribute('aria-valuenow', String(current.State === 'Completed' ? 100 : percent));
        bar.firstChild.style.width = (current.State === 'Completed' ? 100 : percent) + '%';
        kit.setHidden(bar, current.State !== 'Running' && current.State !== 'Queued');
    };
    update(job);
    return { element: element, update: update };
}

function exportStateText(job, percent) {
    switch (job.State) {
        case 'Queued':
            return 'Waiting to start…';
        case 'Running':
            return 'Exporting ' + percent + '%';
        case 'Completed':
            return 'Done: ' + job.FileName + (typeof job.FileSize === 'number' ? ' (' + formatSize(job.FileSize) + ')' : '');
        case 'Cancelled':
            return 'Cancelled' + (job.Error ? ': ' + job.Error : '');
        default:
            return 'Failed: ' + (job.Error || 'unknown error');
    }
}

async function cancelExport(exportId, button) {
    if (button) {
        button.disabled = true;
    }

    try {
        notifyExport(await api('POST', 'Bookmarks/Exports/' + exportId + '/Cancel'));
    } catch (e) {
        toast(e.message);
        if (button) {
            button.disabled = false;
        }
    }
}

function addExportListener(exportId, listener) {
    const watcher = exportWatchers.get(exportId);
    if (watcher) {
        watcher.listeners.add(listener);
    }
}

function removeExportListener(exportId, listener) {
    const watcher = exportWatchers.get(exportId);
    if (watcher) {
        watcher.listeners.delete(listener);
    }
}

function notifyExport(job) {
    const watcher = exportWatchers.get(job.Id);
    if (watcher) {
        watcher.listeners.forEach(function (listener) {
            listener(job);
        });
    }
}

// Follows an export until it ends, then says so with a toast wherever the user is.
function watchExport(job) {
    if (exportWatchers.has(job.Id)) {
        return;
    }

    const watcher = { timer: null, listeners: new Set() };
    exportWatchers.set(job.Id, watcher);
    const poll = async function () {
        let current;
        try {
            current = await api('GET', 'Bookmarks/Exports/' + job.Id);
        } catch (e) {
            exportWatchers.delete(job.Id);
            return;
        }

        notifyExport(current);
        if (current.State === 'Queued' || current.State === 'Running') {
            watcher.timer = setTimeout(poll, EXPORT_POLL_MS);
            return;
        }

        exportWatchers.delete(job.Id);
        if (current.State === 'Completed') {
            const actions = [];
            if (offeredPages(freshCapabilities() || state.capabilities).clips) {
                actions.push({ label: 'View in My Clips', href: kit.pages.href(CLIPS_PAGE.key, null) });
            }

            toast('Export ready: ' + current.FileName, { timeout: 12000, actions: actions });
        } else if (current.State === 'Failed') {
            toast('Export failed: ' + (current.Error || 'unknown error'), { timeout: 12000 });
        } else {
            toast('Export cancelled.');
        }
    };
    watcher.timer = setTimeout(poll, EXPORT_POLL_MS);
}

// The My Clips page's exports: the caller's exports since the server started, those under way with their progress and
// Cancel, the finished ones with their file; hidden while there is none. Read once with the page, then followed while
// one is under way and the panel is on the page.
function buildClipsExportsPanel() {
    const list = h('div', { class: 'jfbm-exports-list' });
    const panel = h('section', { class: 'jfbm-exports', 'aria-label': 'Playlist exports' }, [
        h('h3', { class: 'jfbm-exports-title', text: 'Playlist exports' }),
        list
    ]);
    kit.setHidden(panel, true);
    let timer = null;
    const render = function (jobs) {
        kit.setHidden(panel, jobs.length === 0);
        list.replaceChildren.apply(list, jobs.map(function (job) {
            const view = exportProgressView(job);
            const active = job.State === 'Queued' || job.State === 'Running';
            const row = h('div', { class: 'jfbm-export-row', 'data-jfbm-export': job.Id }, [
                h('div', { class: 'jfbm-export-row-text' }, [
                    h('span', { class: 'jfbm-export-row-name', text: job.PlaylistName }),
                    h('span', { class: kit.cls('hint'), text: ' · ' + job.VideoCount + (job.VideoCount === 1 ? ' video' : ' videos') + ' · ' + formatTime(job.DurationTicks) })
                ]),
                view.element
            ]);
            if (active) {
                const stop = kit.button('Cancel', {
                    className: 'jfbm-export-row-cancel',
                    onClick: function () {
                        cancelExport(job.Id, stop).then(refresh);
                    }
                });
                row.append(stop);
            }

            return row;
        }));
        kit.upgrade(list);
        return jobs.some(function (job) {
            return job.State === 'Queued' || job.State === 'Running';
        });
    };
    const refresh = async function () {
        clearTimeout(timer);
        timer = null;
        let jobs;
        try {
            jobs = await api('GET', 'Bookmarks/Exports');
        } catch (e) {
            report(e);
            return;
        }

        if (render(jobs || []) && panel.isConnected) {
            timer = setTimeout(refresh, EXPORT_POLL_MS);
        }
    };
    refresh();
    return panel;
}
