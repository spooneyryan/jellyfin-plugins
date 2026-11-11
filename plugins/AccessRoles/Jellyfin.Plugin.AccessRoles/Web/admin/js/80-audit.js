// The Audit tab: GET /AccessRoles/Admin/Audit, newest first, 50 lines a page with Load more,
// whose cursor is the last line shown (before=its Seq: lines written meanwhile shift no page),
// filtered by user, action, date range and rule. The filter's blocked requests and errors arrive as one line per rule,
// endpoint and user per window (RequestsBlocked, FilterErrors: aggregated counts), and the page adds them up by rule. A
// rule name may be several joined with + (ServerHardening.RuleNames): it is shown as its rules, and the rule filter finds a
// line by one of its rules or by the name as it is (the server's rule=). The export downloads the lines of the date range
// as JSON Lines (GET Audit/Export).

const AR_AUDIT_PAGE = 50;

// The actions in the log (Audit/AuditActions.cs), with what the filter calls them.
const AR_AUDIT_ACTIONS = [
    ['RequestsBlocked', 'Requests blocked'],
    ['FilterErrors', 'Filter errors'],
    ['ManualEditOverridden', 'Manual edit replaced by roles'],
    ['DriftDetected', 'Changed outside Access Roles'],
    ['PolicySynced', 'Library access written'],
    ['WriteAborted', 'Write aborted'],
    ['SyncSkipped', 'User skipped by the sync'],
    ['LibrarySyncPaused', 'Library sync paused'],
    ['LibrarySyncResumed', 'Library sync resumed'],
    ['LibraryAccessRestored', 'Original access restored'],
    ['LibraryAccessFrozen', 'Library access kept as it was'],
    ['ReleaseDropped', 'Release dropped'],
    ['UsersWithoutLibraries', 'Users left with no libraries'],
    ['LibraryAdded', 'Library added'],
    ['LibraryRenamed', 'Library renamed'],
    ['LibraryRemoved', 'Library deleted'],
    ['LibraryRecreated', 'Library created again'],
    ['LibraryGrantedAgain', 'Library granted again'],
    ['LibraryPurged', 'Library references removed'],
    ['RoleCreated', 'Role created'],
    ['RoleUpdated', 'Role changed'],
    ['RoleDeleted', 'Role deleted'],
    ['AssignmentChanged', 'Assignment changed'],
    ['DefaultRolesChanged', 'Default roles changed'],
    ['ImportApplied', 'Imported or restored'],
    ['UserCreatedDefaulted', 'New user given the default roles'],
    ['UserDeleted', 'User deleted'],
    ['UsersRemapped', 'Users remapped'],
    ['SignInRefused', 'Sign-in refused'],
    ['SettingsChanged', 'Settings changed'],
    ['DegradedModeEntered', 'Degraded mode entered'],
    ['DegradedModeEnded', 'Degraded mode ended'],
    ['PreferenceAccessMirrored', 'Native preference access turned off'],
    ['PreferenceAccessRestored', 'Native preference access back on'],
    ['LocksReleased', 'Locks released']
];

const AR_OUTCOMES = { Refused: 'refused', Reverted: 'reverted', FailedClosed: 'failed closed', Hidden: 'hidden' };

function arAuditLabel(action) {
    const found = AR_AUDIT_ACTIONS.filter(function (a) {
        return a[0] === action;
    })[0];
    return found ? found[1] : String(action || '');
}

// A line's blocked requests (RequestsBlocked, FilterErrors): its count, its rule as its rules, endpoint and outcome.
function arBlockedDetails(details) {
    const count = Number(details.Count) || 0;
    return [
        arCount(count, 'request') + ' ' + (AR_OUTCOMES[details.Outcome] || String(details.Outcome || 'blocked')) + ' by ',
        arRuleNode(details.Rule),
        details.Endpoint ? ' on ' + details.Endpoint : '',
        details.WindowStartUtc ? ' (window from ' + arDate(details.WindowStartUtc) + ')' : ''
    ];
}

// A detail's value as text: a list of names or values joined, an object as JSON (text, never markup).
function arDetailText(value) {
    if (value === null || value === undefined) {
        return '';
    }

    if (Array.isArray(value)) {
        return value.map(function (item) {
            return item && typeof item === 'object' ? (item.Name !== undefined ? String(item.Name) : JSON.stringify(item)) : String(item);
        }).join(', ');
    }

    return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

function arAuditDetails(entry) {
    const details = entry.Details || {};
    if (entry.Action === 'RequestsBlocked' || entry.Action === 'FilterErrors') {
        return arEl('p', { class: arClass('audit-details') }, arBlockedDetails(details));
    }

    if (entry.Action === 'SettingsChanged' && Array.isArray(details.Changes)) {
        return arEl('ul', { class: arClass('list') + ' ' + arClass('audit-details') }, details.Changes.map(function (c) {
            return arEl('li', { text: (c.Name || '') + ': ' + (c.Before || '') + ' → ' + (c.After || '') });
        }).concat((details.SecretKeysAdded || []).length ? [arEl('li', { text: 'Keys that carry secrets added: ' + details.SecretKeysAdded.join(', ') })] : []));
    }

    const keys = Object.keys(details);
    if (!keys.length) {
        return null;
    }

    return arEl('ul', { class: arClass('list') + ' ' + arClass('audit-details') }, keys.map(function (key) {
        return arEl('li', { text: key + ': ' + arDetailText(details[key]) });
    }));
}

function arAuditActor(actor) {
    actor = actor || {};
    if (actor.Kind === 'User' || actor.Name) {
        return arName(actor.Name || 'User ' + arId(actor.UserId));
    }

    return actor.Kind === 'System' || !actor.Kind ? 'Access Roles' : String(actor.Kind);
}

function arAuditEntry(entry) {
    return arTvFocusable(arEl('li', { class: arClass('audit-entry'), 'data-seq': String(entry.Seq), 'data-action': entry.Action, 'data-ar-focus': 'audit-' + entry.Seq }, [
        arEl('p', { class: arClass('audit-head') }, [
            arEl('time', { datetime: String(entry.Utc || ''), text: arDate(entry.Utc) }),
            ' · ',
            arEl('strong', { text: arAuditLabel(entry.Action) }),
            ' · by ',
            arAuditActor(entry.Actor),
            entry.TargetName ? ' · ' : null,
            entry.TargetName ? arName(entry.TargetName) : null
        ]),
        arAuditDetails(entry)
    ]));
}

// A date field's day (YYYY-MM-DD) as the UTC instant its local midnight is; next: the midnight after it.
function arDayUtc(value, next) {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(String(value || ''))) {
        return null;
    }

    const date = new Date(value + 'T00:00:00');
    if (isNaN(date.getTime())) {
        return null;
    }

    if (next) {
        date.setDate(date.getDate() + 1);
    }

    return date.toISOString();
}

// The file name a download's Content-Disposition gives, else the fallback.
function arFileName(response, fallback) {
    const header = response && response.headers && typeof response.headers.get === 'function' ? response.headers.get('Content-Disposition') : null;
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(String(header || ''));
    return match ? match[1].replace(/[\\/]/g, '_') : fallback;
}

function arAuditTab(ctx) {
    const userSelect = kit.select({ 'data-audit-filter': 'user' }, []);
    const actionSelect = kit.select({ 'data-audit-filter': 'action' }, [{ value: '', text: 'Any action' }].concat(AR_AUDIT_ACTIONS.map(function (a) {
        return { value: a[0], text: a[1] };
    })));
    const from = kit.input({ type: 'date', 'data-audit-filter': 'from' });
    const to = kit.input({ type: 'date', 'data-audit-filter': 'to' });
    const rule = kit.input({ type: 'search', 'data-audit-filter': 'rule', autocomplete: 'off', spellcheck: 'false', placeholder: 'LibraryVisibility' });
    const show = arButton('Show', function () {
        arBusy(show, function () {
            return search(true);
        });
    }, { variant: 'primary' });
    show.id = 'AccessRolesAuditShow';
    show.setAttribute('data-ar-focus', 'audit-show');
    const download = arButton('Download (JSON Lines)', function () {
        arBusy(download, exportLog);
    });
    download.id = 'AccessRolesAuditExport';
    download.setAttribute('data-ar-focus', 'audit-export');
    const error = arErrorLine();
    error.id = 'AccessRolesAuditError';
    const summary = arEl('p', { id: 'AccessRolesAuditSummary', 'aria-live': 'polite' });
    const blocked = arEl('div', { id: 'AccessRolesAuditBlocked' });
    const list = arEl('ol', { id: 'AccessRolesAuditList', class: arClass('audit-list') });
    const pager = kit.pager({
        text: 'Load more',
        onMore: function () {
            search(false);
        }
    });
    pager.element.id = 'AccessRolesAuditMore';
    // The lines shown, the query they answer and the server's total; seq drops a late answer of an older query.
    let shown = [];
    let query = null;
    let total = 0;
    let seq = 0;
    let loaded = false;

    const element = arEl('div', { class: arClass('audit') }, [
        arEl('div', { class: arClass('section') }, [
            arHeading(3, 'Audit log'),
            arHint('Newest first. Blocked requests are counted per rule, endpoint and user in windows of a few minutes; a rule name of several (A+B) is a request the filter checked with each of them.'),
            arEl('div', { class: arClass('filters') }, [
                kit.field('User', userSelect),
                kit.field('Action', actionSelect),
                kit.field('From', from),
                kit.field('To (including)', to),
                kit.field('Rule', rule)
            ]),
            arActions([show, download]),
            error,
            summary,
            blocked,
            list,
            pager.element
        ])
    ]);

    function fillUsers() {
        const keep = userSelect.value;
        arClear(userSelect);
        userSelect.appendChild(arEl('option', { value: '', text: 'Anyone' }));
        ctx.users.forEach(function (u) {
            userSelect.appendChild(arEl('option', { value: arId(u.UserId), text: u.Name }));
        });
        userSelect.value = ctx.users.some(function (u) {
            return arId(u.UserId) === keep;
        }) ? keep : '';
    }

    // The filters as the query; null (and the reason shown) when the range is wrong.
    function filters() {
        const q = { limit: AR_AUDIT_PAGE };
        if (userSelect.value) {
            q.userId = userSelect.value;
        }

        if (actionSelect.value) {
            q.action = actionSelect.value;
        }

        const fromUtc = from.value ? arDayUtc(from.value, false) : null;
        const toUtc = to.value ? arDayUtc(to.value, true) : null;
        if ((from.value && !fromUtc) || (to.value && !toUtc)) {
            error.textContent = 'A date is not a day the page understands: use the date picker.';
            return null;
        }

        if (fromUtc && toUtc && fromUtc >= toUtc) {
            error.textContent = 'The range ends before it starts.';
            return null;
        }

        if (fromUtc) {
            q.fromUtc = fromUtc;
        }

        if (toUtc) {
            q.toUtc = toUtc;
        }

        const wanted = arRuleParts(rule.value).join('+');
        if (wanted) {
            q.rule = wanted;
        }

        return q;
    }

    // The blocked requests of the lines shown, added up by rule name (as the server names it) and outcome.
    function renderBlocked() {
        arClear(blocked);
        const sums = {};
        const order = [];
        shown.forEach(function (entry) {
            if (entry.Action !== 'RequestsBlocked' && entry.Action !== 'FilterErrors') {
                return;
            }

            const d = entry.Details || {};
            const key = String(d.Rule || '') + '\u0000' + String(d.Outcome || '');
            if (!sums[key]) {
                sums[key] = { rule: d.Rule, outcome: d.Outcome, count: 0, lines: 0 };
                order.push(key);
            }

            sums[key].count += Number(d.Count) || 0;
            sums[key].lines++;
        });
        if (!order.length) {
            return;
        }

        blocked.appendChild(arHeading(4, 'Blocked requests in the lines shown'));
        blocked.appendChild(arEl('ul', { class: arClass('list') }, order.map(function (key) {
            const s = sums[key];
            return arEl('li', { 'data-rule-name': String(s.rule || '') }, [
                arRuleNode(s.rule),
                ': ' + arCount(s.count, 'request') + ' ' + (AR_OUTCOMES[s.outcome] || String(s.outcome || 'blocked')) + ' in ' + arCount(s.lines, 'window')
            ]);
        })));
    }

    function renderSummary() {
        summary.textContent = shown.length
            ? 'Showing ' + shown.length + ' of ' + arCount(total, 'line') + '.'
            : 'No line matches.';
    }

    // fresh: the first page of the filters as they are; else the next page of the shown query.
    function search(fresh) {
        if (fresh) {
            error.textContent = '';
            const q = filters();
            if (!q) {
                return Promise.resolve(null);
            }

            query = q;
            shown = [];
            total = 0;
            arClear(list);
            arClear(blocked);
            summary.textContent = 'Loading…';
        }

        const mine = fresh ? ++seq : seq;
        // The next page is the lines older than the last one shown, not the lines after an index, which the lines written
        // since the first page would shift (lines shown twice).
        const last = shown.length ? shown[shown.length - 1] : null;
        const q = Object.assign({}, query, last ? { before: last.Seq } : {});
        const before = shown.length;
        pager.update({ busy: true });
        return arAdmin.audit(q).then(function (page) {
            if (mine !== seq) {
                return;
            }

            const items = (page && page.Items) || [];
            // With the cursor, the server's total counts the lines older than it.
            total = before + ((page && page.TotalRecordCount) || 0);
            items.forEach(function (entry) {
                shown.push(entry);
                list.appendChild(arAuditEntry(entry));
            });
            renderSummary();
            renderBlocked();
            pager.update({ busy: false, hasMore: shown.length < total && items.length > 0 });
        }, function (e) {
            if (mine !== seq) {
                return;
            }

            pager.update({ busy: false, hasMore: false });
            summary.textContent = '';
            error.textContent = arErrorText(e, 'The audit log could not be read.');
        });
    }

    // The export of the date range: the server's file, saved by the browser.
    function exportLog() {
        error.textContent = '';
        const q = filters();
        if (!q) {
            return null;
        }

        const range = {};
        if (q.fromUtc) {
            range.fromUtc = q.fromUtc;
        }

        if (q.toUtc) {
            range.toUtc = q.toUtc;
        }

        return arAdmin.auditExport(range).then(function (response) {
            const name = arFileName(response, 'accessroles-audit.jsonl');
            return Promise.resolve(typeof response.blob === 'function' ? response.blob() : response.text().then(function (text) {
                return new Blob([text], { type: 'application/x-ndjson' });
            })).then(function (blob) {
                arDownload(blob, name);
                kit.toast('The audit log is saved as ' + name + '.');
            });
        }).then(null, function (e) {
            error.textContent = arErrorText(e, 'The audit log could not be exported.');
        });
    }

    function render() {
        fillUsers();
    }

    // The first page when the tab is first shown; Show reads it again.
    function onShow() {
        fillUsers();
        if (!loaded) {
            loaded = true;
            return search(true);
        }

        return null;
    }

    return { element: element, render: render, onShow: onShow };
}
