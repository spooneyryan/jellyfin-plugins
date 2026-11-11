// The Duplicates tab: the index's status line, its Rebuild button and banners, then the duplicate groups
// (35-groups.js). It shows what GET …/Duplicates/Index says and polls it every 2 s while a rebuild runs,
// or while a library scan has put one off (the scan rule: the server builds it when the scan has read the
// libraries).
const LM_INDEX_POLL_MS = 2000;

// The broken version links an index counts (VersionState), in the order the Repair view will list them.
const LM_INDEX_STATES = [
    { key: 'Orphaned', label: 'Orphaned versions' },
    { key: 'StaleLinks', label: 'Links to missing items' },
    { key: 'Chained', label: 'Chained versions' },
    { key: 'Unlisted', label: 'Versions their primary doesn\'t list' },
    { key: 'ListedNotPointing', label: 'Listed versions that point elsewhere' },
    { key: 'Cycle', label: 'Version cycles' }
];

function lmCount(value, one, many) {
    const n = Number(value) || 0;
    return n.toLocaleString() + ' ' + (n === 1 ? one : many);
}

// "Index: 9,812 movies in 4 libraries · built 14:02 (1.8 s) · 9,700 entries" (the groups and the reclaimable
// space join it), or what stands in for it before the first build.
function lmIndexSummary(index) {
    if (!index.Version) {
        return index.Rebuilding
            ? 'Indexing movies…' + (typeof index.Progress === 'number' ? ' ' + Math.round(index.Progress * 100) + '%' : '')
            : 'The index hasn\'t been built since the server started.';
    }

    const libraries = index.Libraries || [];
    const parts = ['Index: ' + lmCount(index.Movies, 'movie', 'movies') + ' in ' + lmCount(libraries.length, 'library', 'libraries')];
    const built = kit.format.dateTime(index.GeneratedUtc, { hour: '2-digit', minute: '2-digit' });
    if (built) {
        const ms = index.BuildMs;
        parts.push('built ' + built + (typeof ms === 'number' ? ' (' + (ms < 1000 ? ms + ' ms' : (ms / 1000).toFixed(1) + ' s') + ')' : ''));
    }

    parts.push(lmCount(index.Entries, 'entry', 'entries'));
    return parts.join(' · ');
}

// Whether the tab waits for a build: one runs, or the index is out of date and a scan put its build off.
function lmIsWaiting(index) {
    return !!index && (!!index.Rebuilding || (!!index.Dirty && !!index.ScanInProgress));
}

// Whether a failed request is worth asking again: no answer, a timeout or a server error, not a refusal (403, 404).
function lmIsTransient(error) {
    const status = error && typeof error.status === 'number' ? error.status : 0;
    return status === 0 || status === 408 || status === 429 || status >= 500;
}

function lmDuplicatesTab(ctx) {
    const status = lmEl('p', { id: 'LibraryMaintenanceDuplicatesStatus', class: lmClass('status'), role: 'status', text: 'Loading…' });
    const banners = lmEl('div', { id: 'LibraryMaintenanceDuplicatesBanners' });
    const details = lmEl('div', { id: 'LibraryMaintenanceDuplicatesDetails' });
    // aria-disabled, never disabled: a disabled button drops the keyboard's and the D-pad's focus to the page's body when a
    // build starts, and leaves the D-pad's spatial navigation (which skips :disabled).
    const rebuild = kit.button('Rebuild', {
        title: 'Read every movie again and rebuild the index',
        onClick: function () {
            if (rebuild.getAttribute('aria-disabled') !== 'true') {
                requestRebuild();
            }
        }
    });
    rebuild.id = 'LibraryMaintenanceDuplicatesRebuild';
    rebuild.setAttribute('aria-disabled', 'true');
    const groups = lmGroupsView(ctx, { rebuild: requestRebuild, reloadIndex: reloadIndex });
    // Rebuild comes first in its row, so the D-pad's Down from the tabs reaches it on the TV layout. The groups
    // follow the status line and its banners; the libraries and the broken version links come last.
    const element = lmEl('div', { class: lmClass('section') }, [
        lmEl('h3', { class: lmClass('heading'), text: 'Duplicate movies' }),
        lmEl('div', { class: lmClass('actions') }, [rebuild, status]),
        banners,
        groups.element,
        details
    ]);

    // One request of the tab's own at a time: the POST, or a poll between its timer and its answer.
    let posting = false;
    let timer = null;
    let polling = false;

    function settle(result) {
        if (result.ok) {
            ctx.index = result.value;
            ctx.indexError = null;
        } else {
            ctx.indexError = result.error;
        }

        update();
    }

    function setRebuild(enabled) {
        rebuild.setAttribute('aria-disabled', enabled ? 'false' : 'true');
    }

    function requestRebuild() {
        if (posting) {
            return;
        }

        posting = true;
        setRebuild(false);
        lmSettle(lmPost('Duplicates/Index/Rebuild')).then(function (result) {
            posting = false;
            settle(result);
        });
    }

    // The index again at once (a page of groups came from a newer one), unless a request of the tab's is on its way.
    function reloadIndex() {
        if (polling || posting) {
            return;
        }

        if (timer) {
            window.clearTimeout(timer);
            timer = null;
        }

        poll();
    }

    function poll() {
        timer = null;
        if (!element.isConnected) {
            // jellyfin-web dropped the view; a later open mounts the page again.
            return;
        }

        polling = true;
        lmSettle(lmGet('Duplicates/Index')).then(function (result) {
            polling = false;
            settle(result);
        });
    }

    function banner(kind, text) {
        banners.append(kit.notice({ kind: kind, text: text }));
    }

    function schedule() {
        if (!timer && !polling) {
            timer = window.setTimeout(poll, LM_INDEX_POLL_MS);
        }
    }

    function update() {
        groups.update();
        lmClear(banners);
        lmClear(details);
        const index = ctx.index;
        const error = ctx.indexError;
        if (error) {
            // 404: Duplicates is turned off (the server's sentence says so); nothing to rebuild until it is on again.
            status.textContent = lmErrorText(error);
            setRebuild(!posting && error.status !== 404);
            if (lmIsWaiting(index) && lmIsTransient(error)) {
                // A build the tab saw running is asked about again after a passing failure (a dropped connection, a
                // restart); a refusal stops the polling.
                schedule();
            }

            return;
        }

        setRebuild(!posting && !!index && !index.Rebuilding);
        if (!index) {
            status.textContent = 'Loading…';
            return;
        }

        status.textContent = lmIndexSummary(index);
        if (index.Rebuilding && index.Version) {
            banner('info', 'Updating…');
        } else if (index.Dirty && !index.Rebuilding) {
            // Out of date, with no build running: a scan put it off, or nobody used the page lately and the
            // warm-up after scans is off.
            banner('info', index.ScanInProgress
                ? 'The index is out of date; it is rebuilt when the library scan ends.'
                : 'The index is out of date: the library changed since this build. Rebuild reads it again.');
        }

        if (index.ScanInProgress || index.BuiltDuringScan) {
            banner('info', 'Library scan in progress; results may be incomplete.');
        }

        if (index.LastError) {
            banner('error', 'The last rebuild failed: ' + index.LastError);
        }

        const libraries = index.Libraries || [];
        if (index.Version && libraries.length === 0) {
            banner('empty', 'No movie libraries found. Duplicates only checks Movies and mixed-content libraries.');
        }

        const states = index.States || {};
        const broken = LM_INDEX_STATES.filter(function (state) {
            return Number(states[state.key]) > 0;
        });
        if (index.Version && broken.length > 0) {
            details.append(
                lmEl('h4', { class: lmClass('heading'), text: 'Version links to repair' }),
                lmEl('p', { class: lmClass('hint'), text: lmCount(index.BrokenEntries, 'entry has', 'entries have') + ' a broken version link. Repairing them arrives in Library Maintenance 1.1.' }),
                lmEl('table', { class: lmClass('table') }, lmEl('tbody', null, broken.map(function (state) {
                    return lmEl('tr', null, [lmEl('th', { scope: 'row', text: state.label }), lmEl('td', { text: String(states[state.key]) })]);
                }))));
        }

        if (index.Version && libraries.length > 0) {
            details.append(
                lmEl('h4', { class: lmClass('heading'), text: 'Libraries' }),
                lmEl('table', { class: lmClass('table') }, lmEl('tbody', null, libraries.map(function (library) {
                    return lmEl('tr', null, [
                        lmEl('th', { scope: 'row', text: String(library.Name || '') }),
                        lmEl('td', { text: lmCount(library.Movies, 'movie', 'movies') + (library.Kind === 'mixed' ? ' (mixed content)' : '') })
                    ]);
                }))),
                lmEl('p', { class: lmClass('hint'), text: 'Every version of a movie counts here, and a movie counts in each library that holds its folder, so these numbers can be higher than Jellyfin\'s own, which shows merged versions once.' }));
        }

        if (lmIsWaiting(index)) {
            schedule();
        }
    }

    return { element: element, update: update };
}
