// A group card's comparison (read-only, since 1.0.5): "Compare copies" opens a panel in the card that asks the server for
// the group's copies (GET …/Duplicates/Groups/{id}/Compare, one group at a time, never for the list) and shows them side
// by side: one column per file, a row per detail, the best value of each detail the copies differ in marked "Best" (the
// server's Best) and each row whose values differ marked "Differs". On a narrow screen every row stacks its copies, each
// named. Every value from the server is written as text. Its only controls are buttons and links, so a keyboard and a TV
// remote's D-pad reach them all.
const LM_COMPARE_SHOWN_TRACKS = 6;

const LM_COMPARE_ROLES = { Version: 'Version', LocalAlternate: 'Local version' };

const LM_COMPARE_FILE_STATES = {
    Missing: { text: 'File not found', kind: 'warning' },
    Unknown: { text: 'File not checked', kind: 'flag' },
    NotAFile: { text: 'Stream, not a file', kind: 'flag' }
};

const LM_COMPARE_RANGES = {
    SDR: 'SDR',
    HDR10: 'HDR10',
    HDR10Plus: 'HDR10+',
    HLG: 'HLG',
    DOVI: 'Dolby Vision',
    DOVIWithHDR10: 'Dolby Vision + HDR10',
    DOVIWithHLG: 'Dolby Vision + HLG',
    DOVIWithSDR: 'Dolby Vision + SDR',
    DOVIWithEL: 'Dolby Vision (enhancement layer)',
    DOVIWithHDR10Plus: 'Dolby Vision + HDR10+',
    DOVIWithELHDR10Plus: 'Dolby Vision (enhancement layer) + HDR10+',
    DOVIInvalid: 'Dolby Vision (out of spec)'
};

const LM_COMPARE_KINDS = {
    Trailer: ['trailer', 'trailers'],
    Featurette: ['featurette', 'featurettes'],
    DeletedScene: ['deleted scene', 'deleted scenes'],
    BehindTheScenes: ['behind the scenes', 'behind the scenes'],
    Interview: ['interview', 'interviews'],
    Scene: ['scene', 'scenes'],
    Sample: ['sample', 'samples'],
    Clip: ['clip', 'clips'],
    Short: ['short', 'shorts'],
    Commentary: ['commentary track', 'commentary tracks'],
    Unknown: ['extra', 'extras']
};

const LM_COMPARE_CODECS = { pgssub: 'PGS', subrip: 'SRT', dvdsub: 'VobSub', dvd_subtitle: 'VobSub', hdmv_pgs_subtitle: 'PGS', mov_text: 'MP4 text', webvtt: 'WebVTT' };

function lmCompareKind(name, count) {
    const words = LM_COMPARE_KINDS[name] || [String(name), String(name)];
    return count + ' ' + (count === 1 ? words[0] : words[1]);
}

function lmCompareCodec(codec) {
    const text = String(codec || '');
    return LM_COMPARE_CODECS[text.toLowerCase()] || text.toUpperCase();
}

function lmCompareChannels(track) {
    if (track.ChannelLayout) {
        return String(track.ChannelLayout);
    }

    const n = Number(track.Channels);
    return n > 0 ? ({ 1: 'mono', 2: 'stereo', 6: '5.1', 8: '7.1' }[n] || n + ' ch') : null;
}

function lmCompareLanguage(language) {
    return language ? String(language) : 'no language';
}

// One audio track's line: language, codec, channels, then what the file says of it.
function lmCompareAudioLine(track) {
    return [
        lmCompareLanguage(track.Language),
        track.Codec ? lmCompareCodec(track.Codec) : null,
        lmCompareChannels(track),
        track.Title ? '“' + String(track.Title) + '”' : null,
        track.IsCommentary ? 'commentary' : null,
        track.IsDefault ? 'default' : null,
        track.IsExternal ? 'external file' : null
    ].filter(Boolean).join(' · ');
}

function lmCompareSubtitleLine(track) {
    return [
        lmCompareLanguage(track.Language),
        track.Codec ? lmCompareCodec(track.Codec) : null,
        track.Title ? '“' + String(track.Title) + '”' : null,
        track.IsForced ? 'forced' : null,
        track.IsDefault ? 'default' : null,
        track.IsHearingImpaired ? 'SDH' : null,
        track.IsExternal ? 'external' + (track.FileName ? ' (' + String(track.FileName) + ')' : '') : 'embedded'
    ].filter(Boolean).join(' · ');
}

function lmCompareFeatureLine(item) {
    const kind = (LM_COMPARE_KINDS[item.Kind] || [String(item.Kind || 'extra')])[0];
    const name = String(item.Name || '');
    const file = item.FileName && item.FileName !== name ? ' (' + String(item.FileName) + ')' : '';
    const runtime = lmRuntime(item.RunTimeTicks);
    return kind.charAt(0).toUpperCase() + kind.slice(1) + ': ' + name + file + (runtime ? ' · ' + runtime : '');
}

function lmComparePlace(place) {
    const count = Number(place.Count) || 0;
    switch (place.Name) {
        case 'BesideFile':
            return count + ' beside the file';
        case 'ExtrasFolder':
            return count + ' in the folder ' + String(place.Folder || '');
        case 'AudioTrack':
            return count + ' as an audio track' + (count === 1 ? '' : 's');
        default:
            return count + ' elsewhere';
    }
}

// A cell's list: the first LM_COMPARE_SHOWN_TRACKS lines, then "Show N more" (a button, aria-expanded) for the rest.
function lmCompareList(lines, label) {
    const shown = lmEl('ul', { class: lmClass('compare-lines') }, lines.slice(0, LM_COMPARE_SHOWN_TRACKS).map(function (line) {
        return lmEl('li', { text: line });
    }));
    if (lines.length <= LM_COMPARE_SHOWN_TRACKS) {
        return [shown];
    }

    const rest = lmEl('ul', { class: lmClass('compare-lines') }, lines.slice(LM_COMPARE_SHOWN_TRACKS).map(function (line) {
        return lmEl('li', { text: line });
    }));
    const more = lines.length - LM_COMPARE_SHOWN_TRACKS;
    kit.setHidden(rest, true);
    const toggle = kit.button('Show ' + more + ' more', {
        className: lmClass('compare-more'),
        onClick: function () {
            const open = kit.isHidden(rest);
            kit.setHidden(rest, !open);
            toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
            toggle.textContent = open ? 'Show fewer' : 'Show ' + more + ' more';
        }
    });
    toggle.setAttribute('aria-expanded', 'false');
    toggle.setAttribute('aria-label', 'Show ' + more + ' more ' + label);
    return [shown, rest, toggle];
}

// The rows: [label, the server's Best field or null, the copy's cell ({ key, text } or { key, nodes })]. The key is
// what "Differs" compares.
function lmCompareRows() {
    const plain = function (text) {
        return { key: text || '', text: text || '—' };
    };
    const lines = function (list, noun, none) {
        if (!list.length) {
            return { key: '', text: none };
        }

        return { key: list.join('\n'), nodes: [lmEl('span', { class: lmClass('compare-count'), text: lmCount(list.length, noun, noun + 's') })].concat(lmCompareList(list, noun + 's')) };
    };
    return [
        ['Folder', null, function (c) {
            return { key: String(c.Folder || ''), nodes: [lmEl('span', { class: lmClass('compare-path'), dir: 'ltr', text: String(c.Folder || '—') })] };
        }],
        ['Size', 'Size', function (c) {
            return plain(typeof c.SizeBytes === 'number' ? kit.format.size(c.SizeBytes) : null);
        }],
        ['Container', null, function (c) {
            return plain(c.Container ? String(c.Container).toUpperCase() + (c.VideoType && c.VideoType !== 'VideoFile' ? ' (' + c.VideoType + ')' : '') : (c.VideoType && c.VideoType !== 'VideoFile' ? String(c.VideoType) : null));
        }],
        ['Runtime', null, function (c) {
            const runtime = lmRuntime(c.RunTimeTicks);
            return plain(runtime ? runtime + (c.PartCount > 1 ? ' in ' + c.PartCount + ' parts' : '') : null);
        }],
        ['Resolution', 'Resolution', function (c) {
            const v = c.Video;
            return plain(v && v.Width && v.Height ? v.Width + ' × ' + v.Height + (v.ResolutionLabel ? ' (' + v.ResolutionLabel + ')' : '') : null);
        }],
        ['Video', null, function (c) {
            const v = c.Video;
            return plain(v ? [v.Codec ? String(v.Codec).toUpperCase() : null, v.Profile ? String(v.Profile) : null].filter(Boolean).join(' ') : null);
        }],
        ['Bit depth', 'BitDepth', function (c) {
            return plain(c.Video && c.Video.BitDepth ? c.Video.BitDepth + '-bit' : null);
        }],
        ['Dynamic range', 'DynamicRange', function (c) {
            const v = c.Video;
            return plain(v && v.VideoRangeType && v.VideoRangeType !== 'Unknown' ? (LM_COMPARE_RANGES[v.VideoRangeType] || String(v.VideoRangeType)) : (v && v.VideoRange !== 'Unknown' ? String(v.VideoRange) : null));
        }],
        ['Frame rate', null, function (c) {
            const v = c.Video;
            return plain(v && v.FrameRate ? (Math.round(v.FrameRate * 1000) / 1000) + ' fps' + (v.IsInterlaced ? ' (interlaced)' : '') : null);
        }],
        ['Video bitrate', 'VideoBitrate', function (c) {
            return plain(c.Video ? lmBitrate(c.Video.BitRate) : null);
        }],
        ['Bitrate', 'Bitrate', function (c) {
            return plain(lmBitrate(c.TotalBitrate));
        }],
        ['Audio', 'AudioTracks', function (c) {
            return lines((c.Audio || []).map(lmCompareAudioLine), 'track', 'None');
        }],
        ['Most channels', 'AudioChannels', function (c) {
            const tracks = (c.Audio || []).slice().sort(function (a, b) {
                return (Number(b.Channels) || 0) - (Number(a.Channels) || 0);
            });
            return plain(tracks.length ? lmCompareChannels(tracks[0]) : null);
        }],
        ['Subtitles', 'SubtitleTracks', function (c) {
            return lines((c.Subtitles || []).map(lmCompareSubtitleLine), 'track', 'None');
        }],
        ['Special features', 'SpecialFeatures', function (c) {
            const f = c.SpecialFeatures || {};
            if (!f.Count) {
                return { key: '', text: 'None' };
            }

            const kinds = (f.ByKind || []).map(function (k) {
                return lmCompareKind(k.Name, Number(k.Count) || 0);
            }).join(', ');
            const places = (f.ByLocation || []).map(lmComparePlace).join('; ');
            return {
                key: kinds + '|' + places,
                nodes: [
                    lmEl('span', { class: lmClass('compare-count'), text: f.Count + ': ' + kinds }),
                    lmEl('span', { class: lmClass('compare-note'), text: places })
                ].concat((f.Items || []).length ? lmCompareList(f.Items.map(lmCompareFeatureLine), 'special features') : [])
            };
        }],
        ['Added', null, function (c) {
            return plain(c.DateCreated ? kit.format.date(c.DateCreated) : null);
        }],
        ['Watched', null, function (c) {
            const p = c.Played;
            if (!p) {
                return plain(null);
            }

            const parts = [
                p.UsersPlayed ? lmCount(p.UsersPlayed, 'user', 'users') + ' played' : null,
                p.UsersInProgress ? p.UsersInProgress + ' in progress' : null,
                p.PlayCount ? lmCount(p.PlayCount, 'play', 'plays') : null
            ].filter(Boolean);
            return plain(parts.length ? parts.join(' · ') : 'Not played');
        }]
    ];
}

// A copy's column heading: its number, file name, role and file state, and Open (its details page).
function lmCompareHeading(compare, copy, n) {
    const chips = [];
    if (lmGuid(copy.ItemId) === lmGuid(compare.SuggestedKeepId)) {
        chips.push(lmChip('Suggested keep', 'keep'));
    }

    if (LM_COMPARE_ROLES[copy.Role]) {
        chips.push(lmChip(LM_COMPARE_ROLES[copy.Role], 'flag'));
    }

    if (LM_COMPARE_FILE_STATES[copy.FileState]) {
        chips.push(lmChip(LM_COMPARE_FILE_STATES[copy.FileState].text, LM_COMPARE_FILE_STATES[copy.FileState].kind));
    }

    const name = String(copy.FileName || copy.Name || '');
    return lmEl('th', { scope: 'col', class: lmClass('compare-copy'), title: String(copy.Path || '') }, [
        lmEl('span', { class: lmClass('compare-copy-number'), text: 'Copy ' + n }),
        lmEl('bdi', { class: lmClass('compare-file'), dir: 'ltr', text: name }),
        chips.length ? lmEl('span', { class: lmClass('chips') }, chips) : null,
        lmEl('a', {
            class: kit.cls('button') + ' ' + lmClass('open'),
            href: kit.itemHref(copy.ItemId),
            'aria-label': 'Open copy ' + n + ', ' + name,
            text: 'Open'
        })
    ]);
}

// The comparison's table: a heading row of copies, then a row per detail.
function lmCompareTable(compare) {
    const copies = compare.Copies || [];
    const best = {};
    (compare.Best || []).forEach(function (b) {
        best[b.Field] = (b.ItemIds || []).map(lmGuid);
    });
    const head = lmEl('tr', null, [lmEl('td', { class: lmClass('compare-corner') })].concat(copies.map(function (copy, i) {
        return lmCompareHeading(compare, copy, i + 1);
    })));
    const body = lmCompareRows().map(function (row) {
        const cells = copies.map(row[2]);
        const keys = cells.map(function (c) {
            return c.key;
        });
        const differs = keys.some(function (k) {
            return k !== keys[0];
        });
        const winners = row[1] && best[row[1]] ? best[row[1]] : null;
        const label = lmEl('th', { scope: 'row', class: lmClass('compare-label') }, [
            lmEl('span', { text: row[0] }),
            differs ? lmEl('span', { class: lmClass('compare-differs'), text: 'Differs' }) : null
        ]);
        return lmEl('tr', { class: differs ? lmClass('compare-row-differs') : null, 'data-row': row[0] }, [label].concat(cells.map(function (cell, i) {
            const isBest = !!winners && winners.indexOf(lmGuid(copies[i].ItemId)) >= 0;
            return lmEl('td', { class: lmClass('compare-cell') + (isBest ? ' ' + lmClass('compare-best') : ''), 'data-copy': 'Copy ' + (i + 1) }, [
                isBest ? lmEl('span', { class: lmClass('compare-best-mark'), text: 'Best' }) : null
            ].concat(cell.nodes || [lmEl('span', { text: cell.text })]));
        })));
    });
    return lmEl('table', { class: lmClass('compare-table') }, [
        lmEl('caption', { class: lmClass('compare-caption'), text: 'The copies of ' + String(compare.DisplayTitle || '') + ' side by side. "Best" marks the highest value of a detail; "Differs" marks the details that are not the same in every copy.' }),
        lmEl('thead', null, head),
        lmEl('tbody', null, body)
    ]);
}

// The card's toggle and panel. opts: { query() → the group's options (mode, unknownYear, libraryIds), open (a map of the
// view's open group ids, so a card made again keeps its panel open) }.
function lmCompareSection(group, opts) {
    const id = String(group.GroupId || '');
    const panelId = 'LibraryMaintenanceCompare-' + id.replace(/[^0-9a-zA-Z]/g, '');
    const panel = lmEl('div', { id: panelId, class: lmClass('compare'), role: 'region', 'aria-label': 'Copies of ' + String(group.DisplayTitle || '') + ' compared' });
    const status = lmEl('p', { class: lmClass('status'), role: 'status' });
    const content = lmEl('div', { class: lmClass('compare-content') });
    panel.append(status, content);
    kit.setHidden(panel, true);
    let generation = 0;
    let loaded = false;

    const toggle = kit.button('Compare copies', {
        className: lmClass('compare-toggle'),
        onClick: function () {
            if (toggle.getAttribute('aria-expanded') === 'true') {
                close();
            } else {
                open();
            }
        }
    });
    toggle.setAttribute('aria-expanded', 'false');
    toggle.setAttribute('aria-controls', panelId);

    function close() {
        generation++;
        toggle.setAttribute('aria-expanded', 'false');
        toggle.textContent = 'Compare copies';
        kit.setHidden(panel, true);
        if (opts && opts.open) {
            delete opts.open[id];
        }
    }

    function open() {
        toggle.setAttribute('aria-expanded', 'true');
        toggle.textContent = 'Hide the comparison';
        kit.setHidden(panel, false);
        if (opts && opts.open) {
            opts.open[id] = true;
        }

        if (!loaded) {
            load();
        }
    }

    function load() {
        const mine = ++generation;
        status.textContent = 'Comparing the copies…';
        lmClear(content);
        const query = (opts && opts.query && opts.query()) || {};
        lmSettle(lmGet('Duplicates/Groups/' + encodeURIComponent(id) + '/Compare', { query: query })).then(function (answer) {
            if (mine !== generation) {
                return;
            }

            if (answer.ok && answer.value && Array.isArray(answer.value.Copies)) {
                loaded = true;
                const compare = answer.value;
                status.textContent = lmCount(compare.Copies.length, 'copy', 'copies') + ' compared' + (compare.Truncated ? ' (the first ' + compare.Copies.length + ')' : '') + '.';
                // Focusable, so a keyboard can scroll it sideways when the copies are wider than the card.
                content.append(kit.makeFocusable(lmEl('div', { class: lmClass('compare-scroll'), 'aria-label': 'The comparison table' }, lmCompareTable(compare))));
                return;
            }

            status.textContent = '';
            const text = answer.ok
                ? 'The movie index is being built; try again in a moment.'
                : 'Couldn\'t compare the copies: ' + lmErrorText(answer.error);
            content.append(kit.notice({ kind: answer.ok ? 'empty' : 'error', text: text, action: { label: 'Try again', onClick: load } }));
        });
    }

    if (opts && opts.open && opts.open[id]) {
        open();
    }

    return { toggle: toggle, panel: panel };
}
