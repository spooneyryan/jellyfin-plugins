// The Duplicates tab's groups (read-only): the match mode, the unknown-year
// option, the toolbar (libraries, confidence, a text filter, the order), the group cards with Open on each entry, 50 at a
// time with "Load more", and the states. The choices are kept in localStorage through kit.storage (a failure keeps the
// defaults). Every value from the server is written as text. The list and the notices are made again only when what they
// show changes, so the index's 2 s poll never takes the focus from a card, a notice's button or Load more.
const LM_GROUPS_PAGE = 50;
const LM_GROUPS_POLL_MS = 2000;
const LM_GROUPS_PREFS = 'duplicates.view';

const LM_MODES = [
    { value: 'TitleAndYear', label: 'Title + Year', hint: 'Same normalised title and same year.' },
    { value: 'Title', label: 'Title only', hint: 'Same normalised title, any year. Remakes will appear; check the years.' }
];

const LM_UNKNOWN_YEAR = [
    { value: 'MatchUnknownOnly', text: 'only with other movies with no year' },
    { value: 'MatchAny', text: 'with the title\'s one year, when it has one' }
];

const LM_SORTS = [
    { value: 'Confidence', text: 'Confidence, then title' },
    { value: 'Title', text: 'Title' },
    { value: 'Reclaimable', text: 'Reclaimable space' },
    { value: 'Size', text: 'Group size' },
    { value: 'Newest', text: 'Newest added' }
];

const LM_CONFIDENCE = ['Confirmed', 'Likely', 'Possible', 'Conflict'];

const LM_REASONS = {
    TitleAndYear: 'Title + Year',
    Title: 'Title',
    SameTmdbId: 'Same TMDb id',
    SameImdbId: 'Same IMDb id',
    UnknownYearMatch: 'No year, matched'
};

// The group's flag chips, in the server's order; CrossLibrary is "In N libraries".
const LM_GROUP_FLAGS = {
    DifferentProviderIds: 'Different provider ids',
    SameIdYearsDiffer: 'Same id, years differ',
    YearsDiffer: 'Years differ',
    RuntimesDiffer: 'Runtimes differ',
    EditionsDiffer: 'Editions differ',
    YearUnknown: 'Year unknown',
    Ambiguous: 'Ambiguous',
    ToleranceChain: 'Year ±1 chain',
    BrokenMerge: 'Broken merge',
    ContainsDiscRip: 'Disc rip',
    ContainsShortcut: 'Shortcut (.strm)',
    ContainsPlaceholder: 'Placeholder',
    NewSinceIgnored: 'New since ignored'
};

const LM_WARNING_FLAGS = ['DifferentProviderIds', 'SameIdYearsDiffer'];

const LM_ENTRY_FLAGS = {
    YearUnknown: 'Year unknown',
    BrokenMerge: 'Broken merge',
    ContainsDiscRip: 'Disc rip',
    ContainsShortcut: 'Shortcut (.strm)',
    ContainsPlaceholder: 'Placeholder'
};

function lmKnown(value, list) {
    return list.some(function (item) {
        return item.value === value;
    }) ? value : null;
}

// The kept choices, each checked against what the page offers (a value an older build stored is dropped).
function lmGroupsPrefs() {
    const stored = kit.storage.get(LM_GROUPS_PREFS, null);
    const prefs = stored && typeof stored === 'object' ? stored : {};
    return {
        mode: lmKnown(prefs.mode, LM_MODES),
        unknownYear: lmKnown(prefs.unknownYear, LM_UNKNOWN_YEAR),
        sort: lmKnown(prefs.sort, LM_SORTS),
        confidence: Array.isArray(prefs.confidence) ? prefs.confidence.filter(function (c) {
            return LM_CONFIDENCE.indexOf(c) >= 0;
        }) : [],
        libraryIds: Array.isArray(prefs.libraryIds) ? prefs.libraryIds.filter(function (id) {
            return typeof id === 'string' && /^[0-9a-f]{32}$/.test(id);
        }) : null
    };
}

function lmSaveGroupsPrefs(prefs) {
    kit.storage.set(LM_GROUPS_PREFS, {
        mode: prefs.mode,
        unknownYear: prefs.unknownYear,
        sort: prefs.sort,
        confidence: prefs.confidence,
        libraryIds: prefs.libraryIds
    });
}

// A GUID as one spelling ("N", lower case): the server writes "D", item routes take either.
function lmGuid(value) {
    return String(value || '').replace(/-/g, '').toLowerCase();
}

function lmChip(text, kind) {
    return lmEl('span', { class: lmClass('chip') + (kind ? ' ' + lmClass('chip-' + kind) : ''), text: text });
}

function lmBitrate(bps) {
    const n = Number(bps);
    return n > 0 ? (n >= 1e6 ? (n / 1e6).toFixed(1) + ' Mbps' : Math.round(n / 1e3) + ' kbps') : null;
}

function lmRuntime(ticks) {
    const n = Number(ticks);
    if (!(n > 0)) {
        return null;
    }

    const minutes = Math.round(n / 600000000);
    return minutes >= 60 ? Math.floor(minutes / 60) + ' h ' + (minutes % 60) + ' min' : minutes + ' min';
}

function lmBytes(bytes, lowerBound) {
    return (lowerBound ? '≥ ' : '') + kit.format.size(Number(bytes) || 0);
}

function lmHasIds(entry) {
    const ids = entry.ProviderIds || {};
    return !!(ids.Tmdb || ids.Imdb);
}

// An entry's poster (60 px, lazy): the item's Primary image without a tag (the index keeps none),
// asked at the box's size through ApiClient.getUrl, so a base URL is kept. It lies over the title's initials, which stay
// when the movie has no image (a 404) or it fails to load; the box has its size before the image arrives, so nothing
// moves. Every id shown is one the caller can open (the server's item visibility), and the image is decorative (alt="";
// the title is beside it).
const LM_POSTER_WIDTH = 60;

function lmInitials(name) {
    const words = String(name || '').split(/\s+/).map(function (word) {
        // Leading quotes, brackets and punctuation: "Amélie", ¿Quién?, «Léon» and (500) Days give A, Q, L and 5.
        return word.replace(/^[\s"'«»‘’“”([{¡¿.,:;!?\-–—]+/, '');
    }).filter(Boolean);
    const initials = words.slice(0, 2).map(function (word) {
        return Array.from(word)[0];
    }).join('');
    return initials ? initials.toLocaleUpperCase() : '?';
}

function lmPoster(entry) {
    const box = lmEl('div', { class: lmClass('poster'), 'aria-hidden': 'true' }, [
        lmEl('span', { class: lmClass('poster-initials'), text: lmInitials(entry.Name) })
    ]);
    const id = lmGuid(entry.ItemId);
    const client = kit.client();
    if (!client || !/^[0-9a-f]{32}$/.test(id)) {
        box.classList.add(lmClass('poster-none'));
        return box;
    }

    const ratio = Math.min(Math.max(Number(window.devicePixelRatio) || 1, 1), 3);
    const image = lmEl('img', {
        class: lmClass('poster-image'),
        alt: '',
        loading: 'lazy',
        decoding: 'async',
        src: client.getUrl('Items/' + id + '/Images/Primary', { fillWidth: Math.round(LM_POSTER_WIDTH * ratio), quality: 90 })
    });
    image.addEventListener('error', function () {
        image.remove();
        box.classList.add(lmClass('poster-none'));
    });
    box.appendChild(image);
    return box;
}

// An edition as a chip's label: the server compares editions in lower case ("extended cut"), and the chip names it in
// its usual capitals ("Extended Cut", "IMAX", "3D").
const LM_EDITION_WORDS = { imax: 'IMAX', '3d': '3D' };

function lmEditionLabel(edition) {
    return String(edition).split(' ').map(function (word) {
        return Object.prototype.hasOwnProperty.call(LM_EDITION_WORDS, word) ? LM_EDITION_WORDS[word] : word.charAt(0).toUpperCase() + word.slice(1);
    }).join(' ');
}

// One entry of a card: the poster, the stored title with its edition and badges, the facts, the
// ids, the path (one line, ellipsised, the whole in its tooltip), the entry's flags, and Open (the item's details page).
function lmEntryRow(group, entry) {
    const keep = lmGuid(entry.ItemId) === lmGuid(group.SuggestedKeepId);
    const name = String(entry.Name || '');
    const title = [lmEl('bdi', { class: lmClass('entry-name'), text: name })];
    if (entry.Edition) {
        title.push(lmChip(lmEditionLabel(entry.Edition), 'edition'));
    }

    if (keep) {
        title.push(lmChip('Suggested keep', 'keep'));
        if (!lmHasIds(entry)) {
            // The picture comes before the ids, so a keep can have none.
            title.push(lmEl('span', { class: lmClass('hint-inline'), text: 'no TMDb or IMDb id' }));
        }
    }

    const facts = [
        entry.Year ? String(entry.Year) : 'No year',
        (entry.Libraries || []).map(function (l) {
            return String(l.Name || '');
        }).join(', '),
        entry.ResolutionLabel ? String(entry.ResolutionLabel) : null,
        typeof entry.SizeBytes === 'number' ? kit.format.size(entry.SizeBytes) : null,
        lmBitrate(entry.TotalBitrate),
        lmRuntime(entry.RunTimeTicks),
        entry.Container ? String(entry.Container) : null,
        entry.VideoType && entry.VideoType !== 'VideoFile' ? String(entry.VideoType) : null,
        entry.SourceCount > 1 ? entry.SourceCount + ' versions' : null,
        entry.DateCreated ? 'added ' + kit.format.date(entry.DateCreated) : null
    ].filter(Boolean);
    const ids = entry.ProviderIds || {};
    const idText = [ids.Tmdb ? 'TMDb ' + ids.Tmdb : null, ids.Imdb ? 'IMDb ' + ids.Imdb : null].filter(Boolean).join(' · ');
    const flags = (entry.Flags || []).filter(function (f) {
        return LM_ENTRY_FLAGS[f];
    }).map(function (f) {
        return lmChip(LM_ENTRY_FLAGS[f], 'flag');
    });
    // A middle ellipsis: the folders give way first, the file name stays whole until it alone is too wide.
    const path = String(entry.Path || '');
    const cut = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/')) + 1;
    const open = lmEl('a', {
        class: kit.cls('button') + ' ' + lmClass('open'),
        href: kit.itemHref(entry.ItemId),
        'aria-label': 'Open ' + name + (entry.Year ? ' (' + entry.Year + ')' : '') + (keep ? ', the suggested keep' : ''),
        text: 'Open'
    });
    return lmEl('li', { class: lmClass('entry') + (keep ? ' ' + lmClass('entry-keep') : ''), 'data-item-id': lmGuid(entry.ItemId) }, [
        lmPoster(entry),
        lmEl('div', { class: lmClass('entry-body') }, [
            lmEl('div', { class: lmClass('entry-title') }, title),
            lmEl('div', { class: lmClass('entry-facts'), text: facts.join(' · ') }),
            idText ? lmEl('div', { class: lmClass('entry-ids'), text: idText }) : null,
            lmEl('div', { class: lmClass('entry-path'), title: path, dir: 'ltr' }, [
                lmEl('span', { class: lmClass('entry-path-folder'), text: path.slice(0, cut) }),
                lmEl('span', { class: lmClass('entry-path-file'), text: path.slice(cut) })
            ]),
            flags.length ? lmEl('div', { class: lmClass('chips') }, flags) : null
        ]),
        lmEl('div', { class: lmClass('entry-actions') }, open)
    ]);
}

// One group's card: the title, the years, the confidence, the reasons and the flags, the space the
// other copies take, and the entries with the suggested keep first (the server's order).
function lmGroupCard(group) {
    const libraries = {};
    (group.Entries || []).forEach(function (entry) {
        (entry.Libraries || []).forEach(function (l) {
            libraries[lmGuid(l.Id)] = true;
        });
    });
    const confidence = String(group.Confidence || '');
    const chips = [lmChip(confidence, 'confidence-' + confidence.toLowerCase())];
    (group.Reasons || []).forEach(function (reason) {
        if (LM_REASONS[reason]) {
            chips.push(lmChip(LM_REASONS[reason], 'reason'));
        }
    });
    (group.Flags || []).forEach(function (flag) {
        if (flag === 'CrossLibrary') {
            chips.push(lmChip('In ' + Object.keys(libraries).length + ' libraries', 'flag'));
        } else if (LM_GROUP_FLAGS[flag]) {
            chips.push(lmChip(LM_GROUP_FLAGS[flag], LM_WARNING_FLAGS.indexOf(flag) >= 0 ? 'warning' : 'flag'));
        }
    });
    const entries = group.Entries || [];
    const years = (group.Years || []).length ? group.Years.join(' · ') : 'No year';
    return lmEl('article', { class: lmClass('group'), 'data-group-id': String(group.GroupId || ''), 'aria-label': String(group.DisplayTitle || '') + ', ' + years }, [
        lmEl('div', { class: lmClass('group-header') }, [
            lmEl('h4', { class: lmClass('group-title') }, [
                lmEl('bdi', { text: String(group.DisplayTitle || '') }),
                lmEl('span', { class: lmClass('group-years'), text: years })
            ]),
            lmEl('div', { class: lmClass('chips') }, chips),
            lmEl('div', { class: lmClass('group-facts'), text: lmCount(entries.length, 'copy', 'copies') + ' · ' + lmBytes(group.ReclaimableBytes, group.ReclaimableIsLowerBound) + ' reclaimable' })
        ]),
        lmEl('ul', { class: lmClass('entries') }, entries.map(function (entry) {
            return lmEntryRow(group, entry);
        }))
    ]);
}

// A row of toggle buttons (aria-pressed; buttons, so the D-pad's OK and a keyboard's Enter and Space press them, and the
// focus stays on the one pressed).
function lmToggles(id, label, items, onToggle) {
    const row = lmEl('div', { id: id, class: lmClass('toggles'), role: 'group', 'aria-label': label });
    const buttons = items.map(function (item) {
        const button = kit.button(item.label, {
            className: lmClass('toggle'),
            onClick: function () {
                onToggle(item.value);
            }
        });
        button.setAttribute('aria-pressed', 'false');
        button.setAttribute('data-value', item.value);
        row.append(button);
        return button;
    });
    return {
        element: row,
        set: function (isOn) {
            buttons.forEach(function (button) {
                button.setAttribute('aria-pressed', isOn(button.getAttribute('data-value')) ? 'true' : 'false');
            });
        }
    };
}

// The groups view: { element, update() }; the tab calls update whenever its index changes. host.rebuild() is the
// index error's Retry (the tab's Rebuild); host.reloadIndex() asks for the status line's index again when a page of
// groups comes from a newer one (the groups' read may have built it).
function lmGroupsView(ctx, host) {
    const prefs = lmGroupsPrefs();
    let search = '';
    let result = null;
    let items = [];
    let shown = null;
    let pending = null;
    let error = null;
    // A next page's failure (the third review): the cards shown, Load more and its focus stay, with this next to it.
    let moreError = null;
    let moreKey = null;
    let moreTimer = null;
    let loading = false;
    let more = false;
    let started = false;
    let generation = 0;
    let timer = null;
    let stateKey = null;

    const summary = lmEl('p', { id: 'LibraryMaintenanceGroupsSummary', class: lmClass('status'), role: 'status' });
    const stateArea = lmEl('div', { id: 'LibraryMaintenanceGroupsState' });
    const list = lmEl('div', { id: 'LibraryMaintenanceGroupsList', class: lmClass('groups') });
    const moreState = lmEl('div', { id: 'LibraryMaintenanceGroupsMoreState' });
    const modeHint = lmEl('p', { id: 'LibraryMaintenanceGroupsModeHint', class: lmClass('hint') });

    const modes = lmToggles('LibraryMaintenanceGroupsMode', 'Match mode', LM_MODES, function (value) {
        if (currentMode() !== value) {
            prefs.mode = value;
            changed();
        }
    });
    const unknownYear = kit.select({ id: 'LibraryMaintenanceGroupsUnknownYear' }, LM_UNKNOWN_YEAR);
    unknownYear.addEventListener('change', function () {
        prefs.unknownYear = lmKnown(unknownYear.value, LM_UNKNOWN_YEAR);
        changed();
    });
    const unknownYearField = lmEl('label', { class: lmClass('control') }, [lmEl('span', { class: lmClass('control-label'), text: 'A movie with no year groups' }), unknownYear]);
    const sort = kit.select({ id: 'LibraryMaintenanceGroupsSort' }, LM_SORTS);
    sort.addEventListener('change', function () {
        prefs.sort = lmKnown(sort.value, LM_SORTS);
        changed();
    });
    const searchBox = kit.searchBox({
        placeholder: 'Filter by title',
        label: 'Filter the groups by title',
        maxLength: 200,
        onSearch: function (text) {
            search = text;
            load(false);
        }
    });
    searchBox.id = 'LibraryMaintenanceGroupsSearch';
    const confidence = lmToggles('LibraryMaintenanceGroupsConfidence', 'Confidence', LM_CONFIDENCE.map(function (c) {
        return { value: c, label: c };
    }), function (value) {
        const at = prefs.confidence.indexOf(value);
        if (at >= 0) {
            prefs.confidence.splice(at, 1);
        } else {
            prefs.confidence.push(value);
        }

        changed();
    });
    let libraries = null;
    const libraryRow = lmEl('div', { class: lmClass('control') });
    // aria-disabled while a page loads, never disabled: the focus stays on it (as for Rebuild).
    const loadMore = kit.button('Load more', {
        className: lmClass('more'),
        onClick: function () {
            if (loadMore.getAttribute('aria-disabled') !== 'true') {
                load(true);
            }
        }
    });
    loadMore.id = 'LibraryMaintenanceGroupsMore';
    loadMore.setAttribute('aria-disabled', 'false');

    const element = lmEl('div', { id: 'LibraryMaintenanceGroups', class: lmClass('groups-view') }, [
        lmEl('div', { class: lmClass('controls') }, [modes.element, modeHint]),
        lmEl('div', { class: lmClass('toolbar') }, [
            unknownYearField,
            libraryRow,
            lmEl('div', { class: lmClass('control') }, [lmEl('span', { class: lmClass('control-label'), text: 'Confidence' }), confidence.element]),
            lmEl('label', { class: lmClass('control') }, [lmEl('span', { class: lmClass('control-label'), text: 'Order' }), sort]),
            lmEl('div', { class: lmClass('control') + ' ' + lmClass('search') }, searchBox)
        ]),
        summary,
        stateArea,
        list,
        loadMore,
        moreState
    ]);

    function currentMode() {
        return prefs.mode || (result && result.Mode) || 'TitleAndYear';
    }

    // The libraries' toggles (more than one library only), made again when the index's libraries change.
    function syncLibraries() {
        const choices = ((ctx.index && ctx.index.Libraries) || []).map(function (l) {
            return { value: lmGuid(l.Id), label: String(l.Name || '') };
        });
        const key = choices.map(function (c) {
            return c.value + '=' + c.label;
        }).join('\n');
        if (libraries && libraries.key === key) {
            return;
        }

        lmClear(libraryRow);
        libraries = null;
        if (choices.length < 2) {
            return;
        }

        const all = choices.map(function (c) {
            return c.value;
        });
        libraries = lmToggles('LibraryMaintenanceGroupsLibraries', 'Libraries', choices, function (value) {
            const chosen = (prefs.libraryIds || all).filter(function (id) {
                return all.indexOf(id) >= 0;
            });
            const at = chosen.indexOf(value);
            if (at >= 0) {
                chosen.splice(at, 1);
            } else {
                chosen.push(value);
            }

            prefs.libraryIds = chosen.length === all.length ? null : chosen;
            changed();
        });
        libraries.key = key;
        libraryRow.append(lmEl('span', { class: lmClass('control-label'), text: 'Libraries' }), libraries.element);
    }

    function query(startIndex) {
        const q = { startIndex: startIndex, limit: LM_GROUPS_PAGE };
        if (prefs.mode) {
            q.mode = prefs.mode;
        }

        if (prefs.unknownYear) {
            q.unknownYear = prefs.unknownYear;
        }

        if (prefs.sort) {
            q.sort = prefs.sort;
        }

        if (prefs.confidence.length) {
            q.confidence = prefs.confidence.join(',');
        }

        if (prefs.libraryIds) {
            q.libraryIds = prefs.libraryIds.join(',');
        }

        if (search) {
            q.search = search;
        }

        return q;
    }

    function changed() {
        lmSaveGroupsPrefs(prefs);
        load(false);
    }

    function schedule() {
        if (!timer) {
            timer = window.setTimeout(function () {
                timer = null;
                if (element.isConnected) {
                    load(false);
                }
            }, LM_GROUPS_POLL_MS);
        }
    }

    // A passing failure of a next page asks for the same page again after the poll's wait, not the first; Load more does
    // at once. A newer list (a choice, a newer index) clears moreError, and with it this retry.
    function scheduleMore() {
        if (!moreTimer) {
            moreTimer = window.setTimeout(function () {
                moreTimer = null;
                if (element.isConnected && moreError) {
                    load(true);
                }
            }, LM_GROUPS_POLL_MS);
        }
    }

    // A page of groups: the first (anew, after a choice or for a newer index) or the next ("Load more"). The answer to
    // an older request is dropped.
    function load(next) {
        if (next && (!result || loading)) {
            return;
        }

        const mine = ++generation;
        moreError = null;
        if (prefs.libraryIds && prefs.libraryIds.length === 0) {
            // No library chosen: nothing to ask (an empty list would mean every library).
            loading = false;
            result = null;
            items = [];
            error = null;
            pending = null;
            render();
            return;
        }

        loading = true;
        more = next;
        render();
        lmSettle(lmGet('Duplicates/Groups', { query: query(next ? items.length : 0) })).then(function (answer) {
            if (mine !== generation) {
                return;
            }

            loading = false;
            more = false;
            if (answer.ok && answer.value && Array.isArray(answer.value.Items)) {
                const page = answer.value;
                if (next && result && page.Index && result.Index && page.Index.Version !== result.Index.Version) {
                    // The index changed between the pages: start again from the first.
                    load(false);
                    return;
                }

                const focusNew = next && document.activeElement === loadMore;
                const before = next ? items.length : 0;
                items = next ? items.concat(page.Items) : page.Items.slice();
                result = page;
                pending = null;
                error = null;
                render();
                if (focusNew) {
                    // Load more is hidden when the last page came; the D-pad and the keyboard go on from the first new card.
                    const card = list.querySelectorAll('.' + lmClass('group'))[before];
                    const target = card ? card.querySelector('a[href], button') : null;
                    if (target) {
                        target.focus();
                    }
                }

                // The status line's index is older (the groups' read built a newer one): ask for it again. With none yet,
                // the tab's own first load is on its way.
                if (page.Index && ctx.index && page.Index.Version > ctx.index.Version && host.reloadIndex) {
                    host.reloadIndex();
                }
            } else if (answer.ok) {
                // 202: no index yet after the server's wait (RequestWaitSeconds); it is being built.
                pending = answer.value || {};
                error = null;
                render();
                schedule();
            } else if (next) {
                // The next page failed: keep what is shown; the notice is next to Load more, which asks for that page again.
                moreError = answer.error;
                render();
                if (lmIsTransient(moreError) && moreError.status !== 503) {
                    scheduleMore();
                }
            } else {
                error = answer.error;
                pending = null;
                render();
                if (lmIsTransient(error) && error.status !== 503) {
                    schedule();
                }
            }
        });
    }

    // What the state area says: [{ kind, text, action }], made again only when its words change.
    function notices() {
        if (prefs.libraryIds && prefs.libraryIds.length === 0) {
            return [{ kind: 'empty', text: 'Choose at least one library to look for duplicates in.', action: { label: 'Every library', onClick: everyLibrary } }];
        }

        if (error) {
            if (error.status === 503) {
                // "Index error": the build failed and there is no index; Retry rebuilds it.
                return [{ kind: 'error', text: 'Index error: ' + lmErrorText(error), action: { label: 'Retry', onClick: retry } }];
            }

            return [error.status === 404
                ? { kind: 'empty', text: lmErrorText(error) }
                : { kind: 'error', text: lmErrorText(error), action: { label: 'Retry', onClick: function () {
                    load(false);
                } } }];
        }

        if (pending) {
            return pending.LastError ? [{ kind: 'error', text: 'The last rebuild failed: ' + pending.LastError }] : [];
        }

        if (!result) {
            return [];
        }

        const effective = result.EffectiveLibraryIds || [];
        if (effective.length === 0) {
            // Nothing left to look in; the server drops a library the caller can't open, or that
            // Duplicates doesn't read, without an error.
            return [prefs.libraryIds
                ? { kind: 'empty', text: 'None of the chosen libraries can be checked: you can\'t open them, or Duplicates doesn\'t read them.', action: { label: 'Every library', onClick: everyLibrary } }
                : { kind: 'empty', text: 'There is no library you can open that Duplicates reads.' }];
        }

        if (result.GroupCount === 0) {
            const label = (LM_MODES.filter(function (m) {
                return m.value === result.Mode;
            })[0] || { label: result.Mode }).label;
            const where = lmCount(effective.length, 'library', 'libraries') + ' (' + lmCount(result.TotalMovies, 'movie', 'movies') + ')';
            return [{
                kind: 'empty',
                text: 'No duplicate movies found in ' + where + ' using ' + label + '.',
                action: result.Mode === 'TitleAndYear' && result.AlternateModeGroupCount > 0
                    ? { label: 'Try Title only (' + lmCount(result.AlternateModeGroupCount, 'group', 'groups') + ')', onClick: titleOnly }
                    : null
            }];
        }

        if (result.TotalRecordCount === 0) {
            return [{ kind: 'empty', text: 'No group matches the filters.', action: { label: 'Clear the filters', onClick: clearFilters } }];
        }

        return [];
    }

    function summaryText() {
        if ((prefs.libraryIds && prefs.libraryIds.length === 0) || error) {
            return '';
        }

        if (pending) {
            return 'Indexing movies…' + (typeof pending.Progress === 'number' ? ' ' + Math.round(pending.Progress * 100) + '%' : '');
        }

        if (!result) {
            return loading ? 'Looking for duplicates…' : '';
        }

        return lmCount(result.GroupCount, 'group', 'groups') + ' · ' + lmBytes(result.ReclaimableBytes, result.ReclaimableIsLowerBound) + ' reclaimable'
            + (result.TotalRecordCount !== result.GroupCount ? ' · ' + Number(result.TotalRecordCount).toLocaleString() + ' match the filters' : '')
            + (items.length < result.TotalRecordCount ? ' · showing ' + items.length.toLocaleString() : '')
            + (loading && !more ? ' · updating…' : '');
    }

    function render() {
        syncLibraries();
        const mode = currentMode();
        modes.set(function (value) {
            return value === mode;
        });
        modeHint.textContent = (LM_MODES.filter(function (m) {
            return m.value === mode;
        })[0] || LM_MODES[0]).hint;
        kit.setHidden(unknownYearField, mode !== 'TitleAndYear');
        unknownYear.value = prefs.unknownYear || (result && result.UnknownYear) || 'MatchUnknownOnly';
        sort.value = prefs.sort || 'Confidence';
        confidence.set(function (value) {
            return prefs.confidence.indexOf(value) >= 0;
        });
        if (libraries) {
            libraries.set(function (value) {
                return !prefs.libraryIds || prefs.libraryIds.indexOf(value) >= 0;
            });
        }

        summary.textContent = summaryText();
        const now = notices();
        const key = now.map(function (n) {
            return n.kind + ':' + n.text + ':' + (n.action ? n.action.label : '');
        }).join('\n');
        if (key !== stateKey) {
            stateKey = key;
            lmClear(stateArea);
            now.forEach(function (n) {
                stateArea.append(kit.notice(n));
            });
        }

        // The cards: made again only for another list (a new answer), and kept while the next page loads. A next page only
        // adds its own cards (checked with 200 groups): the ones shown, their posters and the focus stay.
        const want = !error && !pending && result && !(prefs.libraryIds && prefs.libraryIds.length === 0) ? items : null;
        if (want !== shown && !(loading && more)) {
            const extended = !!shown && !!want && want.length > shown.length && shown.every(function (group, i) {
                return want[i] === group;
            });
            if (!extended) {
                lmClear(list);
            }

            (extended ? want.slice(shown.length) : want || []).forEach(function (group) {
                list.append(lmGroupCard(group));
            });
            shown = want;
        }

        kit.setHidden(loadMore, !(want && items.length < result.TotalRecordCount));
        loadMore.setAttribute('aria-disabled', loading ? 'true' : 'false');
        loadMore.textContent = loading && more ? 'Loading…' : 'Load more';
        const moreNow = moreError && want ? 'Couldn\'t load more groups: ' + lmErrorText(moreError) : '';
        if (moreNow !== moreKey) {
            moreKey = moreNow;
            lmClear(moreState);
            if (moreNow) {
                moreState.append(kit.notice({ kind: 'error', text: moreNow }));
            }
        }
    }

    function everyLibrary() {
        prefs.libraryIds = null;
        changed();
    }

    function titleOnly() {
        prefs.mode = 'Title';
        changed();
    }

    function clearFilters() {
        prefs.confidence = [];
        search = '';
        searchBox.setSearch('');
        changed();
    }

    function retry() {
        if (host.rebuild) {
            host.rebuild();
        }

        error = null;
        pending = { Rebuilding: true };
        render();
        schedule();
    }

    // The tab's update: the first one loads the groups; later, an index newer than the groups' (a rebuild, the tab's
    // poll) loads them again once it is built.
    function update() {
        if (!started) {
            started = true;
            load(false);
            return;
        }

        render();
        const index = ctx.index;
        if (index && result && result.Index && index.Version > result.Index.Version && !index.Rebuilding && !loading) {
            load(false);
        }
    }

    return { element: element, update: update };
}
