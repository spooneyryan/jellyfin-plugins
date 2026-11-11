// The Diagnostics tab: GET /LibraryMaintenance/Admin/Diagnostics, each entry as a table of its values.
const LM_DIAGNOSTICS_DEPTH = 3;

function lmValueNode(value, depth) {
    if (value === null || value === undefined) {
        return lmEl('span', { class: lmClass('none'), text: '–' });
    }

    if (Array.isArray(value)) {
        if (value.length === 0) {
            return lmEl('span', { class: lmClass('none'), text: 'none' });
        }

        if (depth >= LM_DIAGNOSTICS_DEPTH) {
            return lmEl('span', { text: value.length + ' entries' });
        }

        return lmEl('ul', { class: lmClass('list') }, value.map(function (entry) {
            return lmEl('li', null, lmValueNode(entry, depth + 1));
        }));
    }

    if (typeof value === 'object') {
        const keys = Object.keys(value);
        if (keys.length === 0) {
            return lmEl('span', { class: lmClass('none'), text: 'none' });
        }

        if (depth >= LM_DIAGNOSTICS_DEPTH) {
            return lmEl('span', { text: keys.join(', ') });
        }

        return lmEl('table', { class: lmClass('table') }, lmEl('tbody', null, keys.map(function (key) {
            return lmEl('tr', null, [lmEl('th', { scope: 'row', text: key }), lmEl('td', null, lmValueNode(value[key], depth + 1))]);
        })));
    }

    return lmEl('span', { text: String(value) });
}

// The movie index's numbers (the index's stats, each version state, the movies skipped), from GET
// …/Duplicates/Index: the libraries the administrator can open, as the Duplicates tab counts them.
function lmIndexDiagnostics(index, error) {
    const parts = [lmEl('h4', { class: lmClass('heading'), text: 'Movie index' })];
    if (error) {
        parts.push(lmEl('p', { text: lmErrorText(error) }));
        return parts;
    }

    if (!index) {
        parts.push(lmEl('p', { text: 'Loading…' }));
        return parts;
    }

    if (!index.Version) {
        parts.push(lmEl('p', { text: lmIndexSummary(index) }));
        return parts;
    }

    const count = function (value) {
        return (Number(value) || 0).toLocaleString();
    };
    const states = index.States || {};
    const built = kit.format.dateTime(index.GeneratedUtc) || String(index.GeneratedUtc || '');
    const ms = index.BuildMs;
    const rows = [
        ['Movies', count(index.Movies) + ' in ' + lmCount((index.Libraries || []).length, 'library', 'libraries')],
        ['Entries', count(index.Entries)],
        ['Entries with more than one version', count(index.MergedEntries)],
        ['Entries with a broken version link', count(index.BrokenEntries)]
    ].concat(LM_INDEX_STATES.map(function (state) {
        return [state.label, count(states[state.key])];
    })).concat([
        ['Skipped: no name', count(index.Skipped)],
        ['Left out by the excluded folders', count(index.Excluded)],
        ['Built', built + (typeof ms === 'number' ? ' in ' + (ms < 1000 ? ms + ' ms' : (ms / 1000).toFixed(1) + ' s') : '')]
    ]);
    parts.push(
        lmEl('table', { id: 'LibraryMaintenanceDiagnosticsIndex', class: lmClass('table') }, lmEl('tbody', null, rows.map(function (row) {
            return lmEl('tr', null, [lmEl('th', { scope: 'row', text: row[0] }), lmEl('td', { text: row[1] })]);
        }))),
        lmEl('p', { class: lmClass('hint'), text: 'These numbers cover the libraries you can open. The tables under them are the plugin\'s own.' }));
    return parts;
}

function lmDiagnosticsTab(ctx) {
    const summary = lmEl('div', { id: 'LibraryMaintenanceDiagnosticsSummary' });
    const body = lmEl('div', { id: 'LibraryMaintenanceDiagnosticsBody' });
    const refresh = kit.button('Refresh', {
        onClick: function () {
            ctx.refresh();
        }
    });
    const element = lmEl('div', { class: lmClass('section') }, [
        lmEl('h3', { class: lmClass('heading'), text: 'Diagnostics' }),
        lmEl('div', { class: lmClass('actions') }, refresh),
        summary,
        body
    ]);

    function update() {
        lmClear(summary);
        lmIndexDiagnostics(ctx.index, ctx.indexError).forEach(function (part) {
            summary.append(part);
        });
        lmClear(body);
        if (ctx.diagnosticsError) {
            body.append(kit.notice({ kind: 'error', text: lmErrorText(ctx.diagnosticsError) }));
            return;
        }

        if (!ctx.diagnostics) {
            body.append(kit.notice({ kind: 'info', text: 'Loading…' }));
            return;
        }

        Object.keys(ctx.diagnostics).forEach(function (key) {
            body.append(lmEl('h4', { class: lmClass('heading'), text: key }), lmValueNode(ctx.diagnostics[key], 0));
        });
    }

    return { element: element, update: update };
}
