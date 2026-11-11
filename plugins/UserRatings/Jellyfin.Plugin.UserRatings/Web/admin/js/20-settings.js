// The settings page's Settings tab, which configPage.html mounts from the same AdminScript module as
// the Moderation tab (90-mount.js). Every setting with its help text and default, from
// GET /UserRatings/Admin/Settings; the users who can rate picked from the server's users, the kinds that can be rated
// from the kinds the server offers. A switch for each on/off setting, a group of choices for each named one, a text field
// for each number: buttons and fields only, so the TV layout's D-pad reaches each and OK changes it (no select, whose TV
// picker is jellyfin-web's own). Each value is checked as it is changed (the message under the field, aria-invalid, the
// field's aria-describedby), and again by the server: Save sends only what changed, with the settings' version, as a dry
// run first; when the server says what the change means (members who can no longer rate, ratings a kind's removal hides,
// log rows the next maintenance deletes, a restart), a dialog asks first (the kit's, scopedKeys: D-pad and Back), then
// the save. Settings saved since the tab was read are a 409 SettingsChanged, which saves nothing and offers Reload. Every
// value from the server is text, every user name isolated.

const SET_GROUPS = [
    { key: 'who', fields: ['EnableRatings', 'EnableReviews', 'WhoCanRate', 'RaterUserIds', 'RateableItemTypes', 'RequirePlayedToRate', 'MaxReviewLength', 'AllowSpoilerFlag'] },
    { key: 'reviewers', fields: ['HideReviewsFromHiddenUsers', 'HideReviewsFromDisabledUsers', 'ShowReviewerAvatars'] },
    { key: 'details', fields: ['ShowAverageChip', 'ShowHistogram', 'MinRatingsForAverage', 'SectionExpandedByDefault', 'ShowDetailButton', 'ReviewsPageSize'] },
    { key: 'web', fields: ['EnableWebUi', 'ReportWebAnchorProblems'] },
    { key: 'data', fields: ['SnapshotsToKeep', 'DetachedRetentionDays', 'ModerationLogDays', 'DeletedUserPolicy'] },
    { key: 'je', fields: ['ShowJellyfinEnhancedBanner'] },
    { key: 'later', fields: ['ShowOnPosters', 'PosterBadgeContent', 'PosterBadgePosition', 'EnableTopRatedPage', 'RegisterPluginPages', 'PageHostMode', 'ShowFallbackNav', 'MirrorToUserData'], later: true }
];

// Each field's kind, and a number's range or a name's values, as the server checks them (Configuration/RatingsSettingsFields.cs).
const SET_FIELDS = {
    EnableRatings: { type: 'bool' },
    EnableReviews: { type: 'bool' },
    WhoCanRate: { type: 'one', names: ['AllUsers', 'SelectedUsers'] },
    RaterUserIds: { type: 'users' },
    RateableItemTypes: { type: 'kinds' },
    RequirePlayedToRate: { type: 'bool' },
    MaxReviewLength: { type: 'number', min: 2000, max: 20000 },
    AllowSpoilerFlag: { type: 'bool' },
    HideReviewsFromHiddenUsers: { type: 'bool' },
    HideReviewsFromDisabledUsers: { type: 'bool' },
    ShowReviewerAvatars: { type: 'bool' },
    ShowAverageChip: { type: 'bool' },
    ShowHistogram: { type: 'bool' },
    MinRatingsForAverage: { type: 'number', min: 1, max: 10 },
    SectionExpandedByDefault: { type: 'bool' },
    ShowDetailButton: { type: 'one', names: ['Auto', 'Always', 'Never'] },
    ReviewsPageSize: { type: 'number', min: 5, max: 50 },
    EnableWebUi: { type: 'bool' },
    ReportWebAnchorProblems: { type: 'bool' },
    SnapshotsToKeep: { type: 'number', min: 0, max: 365 },
    DetachedRetentionDays: { type: 'number', min: 0, max: 36500 },
    ModerationLogDays: { type: 'number', min: 0, max: 3650 },
    DeletedUserPolicy: { type: 'one', names: ['Delete', 'Anonymize', 'KeepAsOrphan'] },
    ShowJellyfinEnhancedBanner: { type: 'bool' },
    ShowOnPosters: { type: 'bool' },
    PosterBadgeContent: { type: 'one', names: ['Average', 'Mine', 'Both'] },
    PosterBadgePosition: { type: 'one', names: ['top-start', 'top-end', 'bottom-start', 'bottom-end'] },
    EnableTopRatedPage: { type: 'bool' },
    RegisterPluginPages: { type: 'bool' },
    PageHostMode: { type: 'one', names: ['FallbackOnly', 'Auto', 'PluginPagesOnly'] },
    ShowFallbackNav: { type: 'bool' },
    // It can't be switched on yet; the server refuses it too.
    MirrorToUserData: { type: 'bool', locked: true }
};

kit.addStrings({
    'set.intro': 'Every setting takes effect when it is saved, without a restart, except where it says so. The server checks each one again, and says before it saves what a change means for the members and the ratings.',
    'set.loading': 'Loading the settings…',
    'set.loadFailed': 'The settings couldn\'t be loaded. {reason}',
    'set.retry': 'Try again',
    'set.problems': 'Saved values User Ratings can\'t use as they are (it uses the value named instead):',
    'set.save': 'Save',
    'set.saving': 'Saving…',
    'set.undo': 'Undo changes',
    'set.nothing': 'Nothing changed.',
    'set.invalid': 'Some settings need fixing (marked below); nothing was saved.',
    'set.saved': 'Settings saved.',
    'set.failed': 'Nothing was saved. {reason}',
    'set.refused': 'The server refused it.',
    'set.reload': 'Reload',
    'set.restart': 'Restart the server for these to take effect: {labels}.',
    'set.on': 'On',
    'set.off': 'Off',
    'set.default': 'Default: {value}.',
    'set.defaultNobody': 'nobody chosen',
    'set.restartNote': 'Takes effect after a restart.',
    'set.notUsedYet': 'Not used yet.',
    'set.unknownValue': 'Saved as "{value}", which this version doesn\'t know; it stays until you choose.',
    'set.missingUser': 'A user no longer on the server ({id})',
    'set.noUsers': 'No users.',
    'set.nobody': 'Nobody',
    'set.none': 'None',
    'set.number': 'Enter a whole number from {min} to {max}.',
    'set.pickUser': 'Choose at least one user: with only chosen users and nobody chosen, nobody could rate or review.',
    'set.pickKind': 'Choose at least one kind: with none, nothing could be rated.',
    'set.locked': 'This can\'t be turned on yet.',
    'set.confirmTitle': 'Save these settings?',
    'set.confirmChanges': 'What changes',
    'set.confirmMeans': 'What it means',
    'set.change': '{label}: {from} → {to}',
    'set.cancel': 'Cancel',
    'set.confirm': 'Save',
    'set.g.who': 'Ratings and reviews',
    'set.g.reviewers': 'Reviewers',
    'set.g.details': 'Details page',
    'set.g.web': 'Web client',
    'set.g.data': 'Data and retention',
    'set.g.je': 'Jellyfin Enhanced',
    'set.g.later': 'Coming with version 1.1',
    'set.gn.later': 'Kept for what version 1.1 brings: rating badges on posters, the Top Rated page, and the copy into Jellyfin\'s own ratings. They change nothing yet.',
    'set.f.EnableRatings': 'Ratings',
    'set.h.EnableRatings': 'Off hides every star, review and average in the web client. Nothing is deleted, and the API keeps working.',
    'set.f.EnableReviews': 'Reviews',
    'set.h.EnableReviews': 'Off hides the reviews and the review box; the stars stay. Nothing is deleted.',
    'set.f.WhoCanRate': 'Who can rate and review',
    'set.h.WhoCanRate': 'Everyone can still read every rating and review, and delete their own.',
    'set.n.WhoCanRate.AllUsers': 'Every user',
    'set.n.WhoCanRate.SelectedUsers': 'Only the users chosen below',
    'set.f.RaterUserIds': 'Users who can rate',
    'set.h.RaterUserIds': 'Used with "Only the users chosen below"; one user is enough.',
    'set.f.RateableItemTypes': 'What can be rated',
    'set.h.RateableItemTypes': 'Ratings on a kind turned off stop showing and are kept; turning it on again shows them.',
    'set.f.RequirePlayedToRate': 'Only after playing',
    'set.h.RequirePlayedToRate': 'A member must have played a title (for a series, a season or an album: one of its episodes or tracks) before rating it.',
    'set.f.MaxReviewLength': 'Longest review',
    'set.u.MaxReviewLength': 'characters',
    'set.h.MaxReviewLength': 'From 2,000 to 20,000. Lowering it never shortens a review already written; it must fit when it is edited.',
    'set.f.AllowSpoilerFlag': 'Spoiler flag',
    'set.h.AllowSpoilerFlag': 'Members can mark a review as containing spoilers.',
    'set.f.HideReviewsFromHiddenUsers': 'Leave out hidden users\' ratings',
    'set.h.HideReviewsFromHiddenUsers': 'Ratings and reviews of users hidden from the sign-in screen show only to them and to moderators, and aren\'t counted.',
    'set.f.HideReviewsFromDisabledUsers': 'Leave out disabled users\' ratings',
    'set.h.HideReviewsFromDisabledUsers': 'Ratings and reviews of disabled users show only to them and to moderators, and aren\'t counted.',
    'set.f.ShowReviewerAvatars': 'Reviewers\' pictures',
    'set.h.ShowReviewerAvatars': 'Reviews show their author\'s profile picture.',
    'set.f.ShowAverageChip': 'Member average chip',
    'set.h.ShowAverageChip': 'The details page shows the members\' average beside the title\'s other ratings.',
    'set.f.ShowHistogram': 'Ratings histogram',
    'set.h.ShowHistogram': 'The section shows how many members gave each rating.',
    'set.f.MinRatingsForAverage': 'Ratings before the average shows',
    'set.u.MinRatingsForAverage': 'ratings',
    'set.h.MinRatingsForAverage': 'From 1 to 10: a title fewer members rated shows no average yet.',
    'set.f.SectionExpandedByDefault': 'Section starts open',
    'set.h.SectionExpandedByDefault': 'The "Member ratings & reviews" section is open when a details page opens.',
    'set.f.ShowDetailButton': 'Rate button',
    'set.h.ShowDetailButton': 'A Rate button among the details page\'s buttons, which goes to the section.',
    'set.n.ShowDetailButton.Auto': 'Where the section is far down (series, seasons, albums)',
    'set.n.ShowDetailButton.Always': 'On every details page',
    'set.n.ShowDetailButton.Never': 'Never',
    'set.f.ReviewsPageSize': 'Reviews per page',
    'set.u.ReviewsPageSize': 'reviews',
    'set.h.ReviewsPageSize': 'From 5 to 50.',
    'set.f.EnableWebUi': 'Add User Ratings to the web client',
    'set.h.EnableWebUi': 'Off: the web client shows no stars, reviews or averages; the REST API keeps working. Applies the next time a page is loaded.',
    'set.f.ReportWebAnchorProblems': 'Record web client problems',
    'set.h.ReportWebAnchorProblems': 'Browsers tell the server when a part of the web client User Ratings relies on is missing, usually after a Jellyfin update; the Diagnostics tab lists them.',
    'set.f.SnapshotsToKeep': 'Daily snapshots to keep',
    'set.u.SnapshotsToKeep': 'snapshots',
    'set.h.SnapshotsToKeep': 'From 0 to 365. The daily maintenance copies the ratings database and keeps this many copies; 0 takes none.',
    'set.f.DetachedRetentionDays': 'Archive detached ratings after',
    'set.u.DetachedRetentionDays': 'days',
    'set.h.DetachedRetentionDays': 'From 0 to 36,500. A rating whose title left the library moves to the archive after this many days, never deleted, and still comes back with its title; 0 keeps them in place.',
    'set.f.ModerationLogDays': 'Keep the moderation log for',
    'set.u.ModerationLogDays': 'days',
    'set.h.ModerationLogDays': 'From 0 to 3,650. The daily maintenance deletes older rows; 0 keeps every row. The log holds who did what, never a review\'s text.',
    'set.f.DeletedUserPolicy': 'When a user is deleted',
    'set.h.DeletedUserPolicy': 'What happens to their ratings and reviews. Users deleted before a change stay as they are.',
    'set.n.DeletedUserPolicy.Delete': 'Delete their ratings and reviews',
    'set.n.DeletedUserPolicy.Anonymize': 'Keep them as a former member\'s',
    'set.n.DeletedUserPolicy.KeepAsOrphan': 'Keep them, counted nowhere, until an admin decides',
    'set.f.ShowJellyfinEnhancedBanner': 'Jellyfin Enhanced banner',
    'set.h.ShowJellyfinEnhancedBanner': 'Warns on this page while Jellyfin Enhanced\'s own reviews are on.',
    'set.f.ShowOnPosters': 'Rating badges on posters',
    'set.h.ShowOnPosters': 'Poster cards show a small rating badge.',
    'set.f.PosterBadgeContent': 'A badge shows',
    'set.h.PosterBadgeContent': 'What a poster\'s badge shows.',
    'set.n.PosterBadgeContent.Average': 'The members\' average',
    'set.n.PosterBadgeContent.Mine': 'The viewer\'s own rating',
    'set.n.PosterBadgeContent.Both': 'Both',
    'set.f.PosterBadgePosition': 'Badge corner',
    'set.h.PosterBadgePosition': 'The start is the left in a left-to-right language, the right in a right-to-left one.',
    'set.n.PosterBadgePosition.top-start': 'Top, start',
    'set.n.PosterBadgePosition.top-end': 'Top, end',
    'set.n.PosterBadgePosition.bottom-start': 'Bottom, start',
    'set.n.PosterBadgePosition.bottom-end': 'Bottom, end',
    'set.f.EnableTopRatedPage': 'Top Rated page',
    'set.h.EnableTopRatedPage': 'A page of the titles the members rated highest.',
    'set.f.RegisterPluginPages': 'Register with Plugin Pages',
    'set.h.RegisterPluginPages': 'Lists the pages in the Plugin Pages plugin, when it is installed.',
    'set.f.PageHostMode': 'Where the pages live',
    'set.h.PageHostMode': 'Plugin Pages, User Ratings\' own pages, or Plugin Pages when it is installed.',
    'set.n.PageHostMode.FallbackOnly': 'User Ratings\' own pages',
    'set.n.PageHostMode.Auto': 'Plugin Pages when installed, else User Ratings\' own',
    'set.n.PageHostMode.PluginPagesOnly': 'Only in Plugin Pages',
    'set.f.ShowFallbackNav': 'Library tools links',
    'set.h.ShowFallbackNav': 'Links to the pages in the drawer\'s Library tools section.',
    'set.f.MirrorToUserData': 'Copy ratings into Jellyfin\'s own rating',
    'set.h.MirrorToUserData': 'On Jellyfin 10.11.5 a naive rating write can reset a member\'s favourite, played and resume state; this plugin always re-reads that state from the database first. It can\'t be turned on until that is checked.',
    'set.kind.Movie': 'Movies',
    'set.kind.Series': 'Series',
    'set.kind.Season': 'Seasons',
    'set.kind.Episode': 'Episodes',
    'set.kind.Video': 'Videos',
    'set.kind.MusicVideo': 'Music videos',
    'set.kind.Trailer': 'Trailers',
    'set.kind.MusicAlbum': 'Albums',
    'set.kind.Audio': 'Tracks',
    'set.kind.Book': 'Books',
    'set.kind.AudioBook': 'Audiobooks',
    'set.kind.BoxSet': 'Collections'
});

function setText(key, fallback) {
    const text = kit.t(key);
    return text === key ? fallback : text;
}

function setKindText(kind) {
    return setText('set.kind.' + kind, String(kind));
}

function setNameText(field, value) {
    return setText('set.n.' + field + '.' + value, String(value));
}

// Lists compare as sets (the server's canonical form sorts them); everything else as it is.
function setSame(a, b) {
    if (Array.isArray(a) || Array.isArray(b)) {
        const x = (Array.isArray(a) ? a : []).map(String).sort();
        const y = (Array.isArray(b) ? b : []).map(String).sort();
        return x.length === y.length && x.every(function (v, i) { return v === y[i]; });
    }

    return a === b;
}

// A saved name as the server reads it: in any case.
function setIs(value, name) {
    return typeof value === 'string' && value.toLowerCase() === String(name).toLowerCase();
}

// A Jellyfin id in any form, as one form: the server writes them without hyphens.
function setId(id) {
    return String(id || '').replace(/-/g, '').toLowerCase();
}

function setFocus(node) {
    if (!node || !node.isConnected || node.disabled) {
        return false;
    }

    try {
        node.focus();
    } catch (e) {
        return false;
    }

    return document.activeElement === node;
}

// ------------------------------------------------------------------------------------------------- the tab

function mountSettings(view) {
    kit.injectStyles();
    const n = 'jfur-set-' + Math.floor(Math.random() * 1e9).toString(36);
    const state = { view: null, saved: {}, values: {}, rows: {}, generation: 0, busy: false, restart: null };

    const intro = kit.h('p', { class: 'jfur-set-intro', text: kit.t('set.intro') });
    const problems = kit.h('div', { class: 'jfur-set-problems' });
    const message = kit.h('div', { class: 'jfur-set-message' });
    const form = kit.h('div', { class: 'jfur-set-form' });
    const status = kit.h('p', { class: 'jfur-set-status', role: 'status' });
    const save = kit.button(kit.t('set.save'), { variant: 'primary', className: 'jfur-set-save', onClick: function () { startSave(); } });
    const undo = kit.button(kit.t('set.undo'), { className: 'jfur-set-undo', onClick: function () { revert(); } });
    const root = kit.h('div', { class: 'jfur-set' }, [intro, problems, message, form, status, kit.h('div', { class: 'jfur-set-actions' }, [save, undo])]);
    view.replaceChildren(root);
    kit.setHidden(save, true);
    kit.setHidden(undo, true);

    function say(text, action) {
        message.replaceChildren();
        if (text) {
            message.append(kit.notice({ kind: 'error', text: text, action: action }));
        }
    }

    function sayInfo(text) {
        message.replaceChildren();
        if (text) {
            message.append(kit.notice({ kind: 'info', text: text }));
        }
    }

    // keepEdits (coming back to the tab): a value changed while it reads stays, and the form isn't built again; the save
    // then goes with the version the edit started from, so settings saved since are the server's 409 with Reload.
    function load(keepEdits) {
        const generation = ++state.generation;
        status.textContent = kit.t('set.loading');
        return kit.api('GET', 'UserRatings/Admin/Settings').then(function (answer) {
            if (generation !== state.generation || !answer) {
                return;
            }

            status.textContent = '';
            if (keepEdits === true && state.view && Object.keys(changed()).length > 0) {
                return;
            }

            render(answer);
        }, function (e) {
            if (generation !== state.generation) {
                return;
            }

            status.textContent = '';
            form.replaceChildren();
            kit.setHidden(save, true);
            kit.setHidden(undo, true);
            say(kit.t('set.loadFailed', { reason: (e && e.message) || kit.t('set.refused') }), { label: kit.t('set.retry'), onClick: function () { say(null); load(); } });
        });
    }

    function render(answer) {
        state.view = answer;
        state.saved = Object.assign({}, answer.Settings || {});
        state.values = Object.assign({}, state.saved);
        state.rows = {};
        say(null);
        if (state.restart) {
            sayInfo(state.restart);
            state.restart = null;
        }

        problems.replaceChildren();
        const listed = answer.Problems || [];
        if (listed.length > 0) {
            problems.append(kit.h('div', { class: kit.cls('notice') + ' ' + kit.cls('notice-error') + ' jfur-set-problem-list', role: 'alert' }, [
                kit.h('p', { text: kit.t('set.problems') }),
                kit.h('ul', null, listed.map(function (p) { return kit.h('li', { text: String(p) }); }))
            ]));
        }

        form.replaceChildren();
        SET_GROUPS.forEach(function (group) {
            const section = kit.h('section', { class: 'jfur-set-group', 'data-jfur-group': group.key, 'aria-labelledby': n + '-g-' + group.key }, [
                kit.h('h3', { id: n + '-g-' + group.key, class: 'jfur-set-group-title', text: kit.t('set.g.' + group.key) })
            ]);
            if (group.later) {
                section.append(kit.h('p', { class: 'jfur-set-group-note', text: kit.t('set.gn.' + group.key) }));
            }

            group.fields.forEach(function (field) {
                if (Object.prototype.hasOwnProperty.call(state.saved, field)) {
                    section.append(fieldRow(field));
                }
            });
            form.append(section);
        });

        kit.setHidden(save, false);
        kit.setHidden(undo, false);
        Object.keys(state.rows).forEach(function (field) {
            refreshField(field);
        });
        // A saved state the server would refuse (only chosen users, nobody chosen) is marked at once.
        Object.keys(state.rows).forEach(function (field) {
            showError(field, check(field));
        });
        dirty();
    }

    function helpText(field) {
        const parts = [kit.t('set.h.' + field)];
        if (SET_FIELDS[field] && SET_FIELDS[field].locked) {
            parts.push(kit.t('set.locked'));
        }

        const defaults = (state.view && state.view.Defaults) || {};
        if (Object.prototype.hasOwnProperty.call(defaults, field)) {
            parts.push(kit.t('set.default', { value: valueText(field, defaults[field]) }));
        }

        if ((state.view.RestartFields || []).indexOf(field) >= 0) {
            parts.push(kit.t('set.restartNote'));
        }

        return parts.join(' ');
    }

    function valueText(field, value) {
        const spec = SET_FIELDS[field] || {};
        if (spec.type === 'bool') {
            return value ? kit.t('set.on') : kit.t('set.off');
        }

        if (spec.type === 'one') {
            return setNameText(field, value);
        }

        if (spec.type === 'users') {
            return Array.isArray(value) && value.length > 0 ? String(value.length) : kit.t('set.defaultNobody');
        }

        if (spec.type === 'kinds') {
            return (Array.isArray(value) ? value : []).map(setKindText).join(', ');
        }

        return String(value);
    }

    // One field: its label (the switch's own for on/off), its control, its help with the default, and its message.
    function fieldRow(field) {
        const spec = SET_FIELDS[field] || { type: 'unknown' };
        const id = n + '-' + field;
        const help = kit.h('p', { id: id + '-help', class: 'jfur-set-help', text: helpText(field) });
        const error = kit.h('p', { id: id + '-error', class: 'jfur-set-error' });
        kit.setHidden(error, true);
        const note = kit.h('p', { id: id + '-note', class: 'jfur-set-note' });
        kit.setHidden(note, true);
        const described = id + '-help ' + id + '-note ' + id + '-error';
        // The row's kind as jfur-set-row-<type>: jfur-set-number is the number field's own class (its 8em width).
        const row = kit.h('div', { class: 'jfur-set-row jfur-set-row-' + spec.type, 'data-jfur-field': field });
        const entry = { row: row, error: error, note: note, control: null, focus: null, refresh: function () {} };
        state.rows[field] = entry;

        if (spec.type === 'bool') {
            const stateText = kit.h('span', { class: 'jfur-set-switch-state', 'aria-hidden': 'true' });
            const button = kit.h('button', { type: 'button', role: 'switch', id: id, class: 'jfur-set-switch', 'aria-describedby': described }, [
                kit.h('span', { class: 'jfur-set-switch-track', 'aria-hidden': 'true' }, kit.h('span', { class: 'jfur-set-switch-thumb' })),
                kit.h('span', { class: 'jfur-set-switch-label', text: kit.t('set.f.' + field) }),
                stateText
            ]);
            button.addEventListener('click', function () {
                if (spec.locked && state.values[field] !== true) {
                    return;
                }

                change(field, state.values[field] !== true);
            });
            entry.control = button;
            entry.focus = button;
            entry.refresh = function () {
                const on = state.values[field] === true;
                button.setAttribute('aria-checked', on ? 'true' : 'false');
                stateText.textContent = on ? kit.t('set.on') : kit.t('set.off');
                const locked = !!spec.locked && !on;
                button.disabled = locked;
                button.classList.toggle('jfur-set-locked', locked);
            };
            row.append(button, help, note, error);
            return row;
        }

        const label = kit.h('div', { id: id + '-label', class: 'jfur-set-label', text: kit.t('set.f.' + field) });
        if (spec.type === 'number') {
            const input = kit.input({ id: id, class: 'jfur-set-number', inputmode: 'numeric', autocomplete: 'off', 'aria-describedby': described });
            input.setAttribute('aria-labelledby', id + '-label');
            input.addEventListener('input', function () {
                const text = String(input.value).trim();
                change(field, /^\d{1,6}$/.test(text) ? Number(text) : text, true);
            });
            entry.control = input;
            entry.focus = input;
            entry.refresh = function () {
                const value = state.values[field];
                if (String(input.value).trim() !== String(value)) {
                    input.value = value === undefined || value === null ? '' : String(value);
                }
            };
            row.append(label, kit.h('div', { class: 'jfur-set-number-row' }, [input, kit.h('span', { class: 'jfur-set-unit', text: kit.t('set.u.' + field) })]), help, note, error);
            return row;
        }

        if (spec.type === 'one') {
            const buttons = spec.names.map(function (name) {
                const button = kit.h('button', { type: 'button', role: 'radio', class: 'jfur-set-choice', 'data-jfur-value': name, text: setNameText(field, name) });
                button.addEventListener('click', function () {
                    change(field, name);
                });
                return button;
            });
            const group = kit.h('div', { role: 'radiogroup', id: id, class: 'jfur-set-choices', 'aria-labelledby': id + '-label', 'aria-describedby': described }, buttons);
            entry.control = group;
            entry.focus = buttons[0];
            entry.refresh = function () {
                const value = state.values[field];
                let known = false;
                buttons.forEach(function (button) {
                    const on = setIs(value, button.getAttribute('data-jfur-value'));
                    known = known || on;
                    button.setAttribute('aria-checked', on ? 'true' : 'false');
                });
                entry.focus = buttons.find(function (b) { return b.getAttribute('aria-checked') === 'true'; }) || buttons[0];
                note.textContent = known ? '' : kit.t('set.unknownValue', { value: String(value === undefined || value === null ? '' : value) });
                kit.setHidden(note, known);
            };
            row.append(label, group, help, note, error);
            return row;
        }

        if (spec.type === 'users' || spec.type === 'kinds') {
            const options = spec.type === 'users' ? userOptions() : kindOptions();
            const buttons = options.map(function (option) {
                const button = kit.h('button', { type: 'button', role: 'checkbox', class: 'jfur-set-check', 'data-jfur-value': option.value }, [
                    kit.h('span', { class: 'jfur-set-check-box', 'aria-hidden': 'true' }),
                    option.node
                ]);
                button.addEventListener('click', function () {
                    const list = (state.values[field] || []).slice();
                    const at = list.findIndex(function (v) { return option.same(v); });
                    if (at >= 0) {
                        list.splice(at, 1);
                    } else {
                        list.push(option.value);
                    }

                    change(field, list);
                });
                button._jfurOption = option;
                return button;
            });
            const group = kit.h('div', { role: 'group', id: id, class: 'jfur-set-checks', 'aria-labelledby': id + '-label', 'aria-describedby': described }, buttons.length > 0 ? buttons : kit.h('p', { text: kit.t('set.noUsers') }));
            entry.control = group;
            entry.focus = buttons[0] || null;
            entry.refresh = function () {
                const list = state.values[field] || [];
                buttons.forEach(function (button) {
                    const on = list.some(function (v) { return button._jfurOption.same(v); });
                    button.setAttribute('aria-checked', on ? 'true' : 'false');
                });
                if (field === 'RaterUserIds') {
                    kit.setHidden(row, !setIs(state.values.WhoCanRate, 'SelectedUsers'));
                }
            };
            row.append(label, group, help, note, error);
            return row;
        }

        row.append(label, help, note, error);
        return row;
    }

    // The server's users by name, then the ids the settings hold that are no longer users (each name isolated).
    function userOptions() {
        return (state.view.Users || []).map(function (user) {
            const key = setId(user.Id);
            return {
                value: user.Id,
                missing: user.Missing === true,
                same: function (v) { return setId(v) === key; },
                node: user.Missing === true
                    ? kit.h('span', { class: 'jfur-set-check-text jfur-set-missing', text: kit.t('set.missingUser', { id: key }) })
                    : kit.h('span', { class: 'jfur-isolate jfur-set-check-text', dir: 'auto', text: String(user.Name || '') })
            };
        });
    }

    function kindOptions() {
        return (state.view.ItemKinds || []).map(function (kind) {
            const key = String(kind).toLowerCase();
            return {
                value: kind,
                same: function (v) { return String(v).toLowerCase() === key; },
                node: kit.h('span', { class: 'jfur-set-check-text', text: setKindText(kind) })
            };
        });
    }

    // The message a field's value gets, or null.
    function check(field) {
        const spec = SET_FIELDS[field] || {};
        const value = state.values[field];
        if (spec.type === 'number') {
            return typeof value === 'number' && Math.floor(value) === value && value >= spec.min && value <= spec.max
                ? null
                : kit.t('set.number', { min: spec.min, max: spec.max });
        }

        if (field === 'RaterUserIds') {
            if (!setIs(state.values.WhoCanRate, 'SelectedUsers')) {
                return null;
            }

            const current = (state.view.Users || []).filter(function (u) { return u.Missing !== true; }).map(function (u) { return setId(u.Id); });
            return (value || []).some(function (v) { return current.indexOf(setId(v)) >= 0; }) ? null : kit.t('set.pickUser');
        }

        if (field === 'RateableItemTypes') {
            return (value || []).length > 0 ? null : kit.t('set.pickKind');
        }

        return null;
    }

    function showError(field, text) {
        const entry = state.rows[field];
        if (!entry) {
            return;
        }

        entry.error.textContent = text || '';
        kit.setHidden(entry.error, !text);
        entry.row.classList.toggle('jfur-set-invalid', !!text);
        if (entry.control) {
            if (text) {
                entry.control.setAttribute('aria-invalid', 'true');
            } else {
                entry.control.removeAttribute('aria-invalid');
            }
        }
    }

    function refreshField(field) {
        const entry = state.rows[field];
        if (entry) {
            entry.refresh();
        }
    }

    // A value changed: the field shows it (a number field keeps what was typed), and it and the fields that depend on it
    // are checked again.
    function change(field, value, typed) {
        state.values[field] = value;
        if (!typed) {
            refreshField(field);
        }

        showError(field, check(field));
        if (field === 'WhoCanRate') {
            refreshField('RaterUserIds');
            showError('RaterUserIds', check('RaterUserIds'));
        }

        status.textContent = '';
        dirty();
    }

    function changed() {
        const body = {};
        Object.keys(state.rows).forEach(function (field) {
            if (!setSame(state.values[field], state.saved[field])) {
                body[field] = state.values[field];
            }
        });
        return body;
    }

    function dirty() {
        // Save stays enabled while it waits (a click then does nothing), so the focus on it isn't lost.
        undo.disabled = Object.keys(changed()).length === 0 || state.busy;
        save.setAttribute('aria-disabled', state.busy ? 'true' : 'false');
    }

    function revert() {
        state.values = Object.assign({}, state.saved);
        Object.keys(state.rows).forEach(function (field) {
            refreshField(field);
            showError(field, check(field));
        });
        status.textContent = '';
        say(null);
        dirty();
        setFocus(save);
    }

    function busy(on) {
        state.busy = on;
        save.textContent = on ? kit.t('set.saving') : kit.t('set.save');
        dirty();
    }

    // A refusal: a value the server can't take goes under its field; settings saved since offer Reload; anything else says
    // why above the form. Nothing was saved in any of them.
    function refused(e) {
        const code = e && typeof e.code === 'string' ? e.code : '';
        if (code.indexOf('InvalidSetting:') === 0 && state.rows[code.substring('InvalidSetting:'.length)]) {
            const field = code.substring('InvalidSetting:'.length);
            showError(field, e.message);
            status.textContent = kit.t('set.invalid');
            setFocus(state.rows[field].focus);
            return;
        }

        // The 409's words name what changed and already say nothing was saved.
        if (code === 'SettingsChanged') {
            say(String(e.message || kit.t('set.failed', { reason: kit.t('set.refused') })), { label: kit.t('set.reload'), onClick: function () { say(null); load(); } });
            return;
        }

        say(kit.t('set.failed', { reason: (e && e.message) || kit.t('set.refused') }));
    }

    function startSave() {
        if (state.busy || !state.view) {
            return;
        }

        say(null);
        let first = null;
        Object.keys(state.rows).forEach(function (field) {
            const text = check(field);
            showError(field, text);
            if (text && !first) {
                first = field;
            }
        });
        if (first) {
            status.textContent = kit.t('set.invalid');
            setFocus(state.rows[first].focus);
            return;
        }

        const body = changed();
        if (Object.keys(body).length === 0) {
            status.textContent = kit.t('set.nothing');
            return;
        }

        const version = state.view.Version;
        busy(true);
        status.textContent = '';
        kit.api('POST', 'UserRatings/Admin/Settings', { query: { version: version }, body: body }).then(function (report) {
            busy(false);
            const consequences = (report && report.Consequences) || [];
            if (consequences.length === 0) {
                commit(body, version);
                return;
            }

            ask(report, body).then(function (ok) {
                if (ok) {
                    commit(body, version);
                }
            });
        }, function (e) {
            busy(false);
            refused(e);
        });
    }

    // A value as the dialog shows it, in the tab's words: a user's name isolated each (a gone user by id), a kind by its
    // name; the server's text for a setting the tab doesn't show.
    function changeNode(field, value, fallback) {
        const spec = SET_FIELDS[field];
        if (!spec) {
            return kit.h('span', { class: 'jfur-isolate', dir: 'auto', text: String(fallback || '') });
        }

        if (spec.type === 'users') {
            const chosen = (state.view.Users || []).filter(function (user) {
                return (value || []).some(function (v) { return setId(v) === setId(user.Id); });
            });
            if (chosen.length === 0) {
                return kit.h('span', { text: kit.t('set.nobody') });
            }

            const parts = [];
            chosen.forEach(function (user, i) {
                if (i > 0) {
                    parts.push(document.createTextNode(', '));
                }

                parts.push(user.Missing === true
                    ? kit.h('span', { text: kit.t('set.missingUser', { id: setId(user.Id) }) })
                    : kit.h('span', { class: 'jfur-isolate', dir: 'auto', text: String(user.Name || '') }));
            });
            return kit.h('span', { class: 'jfur-set-change-users' }, parts);
        }

        if (spec.type === 'kinds') {
            const kinds = (value || []).map(setKindText);
            return kit.h('span', { class: 'jfur-isolate', dir: 'auto', text: kinds.length > 0 ? kinds.join(', ') : kit.t('set.none') });
        }

        return kit.h('span', { class: 'jfur-isolate', dir: 'auto', text: spec.type === 'bool' ? (value ? kit.t('set.on') : kit.t('set.off')) : spec.type === 'one' ? setNameText(field, value) : String(value) });
    }

    // The dry run's report: what changes (in the tab's words, from the values saved to the ones sent) and what it means,
    // Cancel focused; the focus back on Save when it closes.
    function ask(report, body) {
        return new Promise(function (resolve) {
            let answered = false;
            let cancel = null;
            const dialog = kit.dialog({
                title: kit.t('set.confirmTitle'),
                modal: true,
                scopedKeys: true,
                initialFocus: function () { return cancel; },
                returnFocus: function () { return save; }
            });
            const answer = function (value) {
                answered = true;
                resolve(value);
                dialog.close();
            };
            dialog.onClose(function () {
                if (!answered) {
                    resolve(false);
                }
            });
            cancel = kit.button(kit.t('set.cancel'), { className: 'jfur-set-cancel', onClick: function () { answer(false); } });
            const confirm = kit.button(kit.t('set.confirm'), { variant: 'primary', className: 'jfur-set-confirm', onClick: function () { answer(true); } });
            kit.append(dialog.body,
                kit.h('h4', { class: 'jfur-set-confirm-heading', text: kit.t('set.confirmChanges') }),
                kit.h('ul', { class: 'jfur-set-changes' }, (report.Changes || []).map(function (c) {
                    const field = String(c.Field || '');
                    const known = Object.prototype.hasOwnProperty.call(body, field);
                    return kit.h('li', null, [
                        kit.h('span', { class: 'jfur-set-change-label', text: setText('set.f.' + field, String(c.Label || field)) + ': ' }),
                        known ? changeNode(field, state.saved[field], c.From) : kit.h('span', { class: 'jfur-isolate', dir: 'auto', text: String(c.From || '') }),
                        document.createTextNode(' → '),
                        known ? changeNode(field, body[field], c.To) : kit.h('span', { class: 'jfur-isolate', dir: 'auto', text: String(c.To || '') })
                    ]);
                })),
                kit.h('h4', { class: 'jfur-set-confirm-heading', text: kit.t('set.confirmMeans') }),
                kit.h('ul', { class: 'jfur-set-consequences' }, (report.Consequences || []).map(function (c) {
                    return kit.h('li', { text: String(c) });
                })),
                kit.h('div', { class: kit.cls('actions') }, [cancel, confirm]));
        });
    }

    function commit(body, version) {
        busy(true);
        kit.api('POST', 'UserRatings/Admin/Settings', { query: { version: version, dryRun: 'false' }, body: body }).then(function (report) {
            busy(false);
            kit.toast(kit.t('set.saved'));
            const restart = ((report && report.Changes) || []).filter(function (c) { return c.RestartNeeded === true; }).map(function (c) { return setText('set.f.' + c.Field, String(c.Label || c.Field)); });
            state.restart = restart.length > 0 ? kit.t('set.restart', { labels: restart.join(', ') }) : null;
            return load();
        }, function (e) {
            busy(false);
            refused(e);
        });
    }

    // The first read after mount returns, through the page's own timer (so importing and mounting send nothing at once).
    window.setTimeout(load, 0);

    // The page calls refresh when the tab is selected again: the settings are read again unless a value is changed and
    // not saved (switching tabs keeps the edits) or a save is going.
    return {
        refresh: function () {
            if (state.busy || (state.view && Object.keys(changed()).length > 0)) {
                return;
            }

            load(true);
        }
    };
}
