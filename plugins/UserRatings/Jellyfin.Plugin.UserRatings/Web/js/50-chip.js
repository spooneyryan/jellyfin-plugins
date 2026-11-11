/*
 * The member-average chip and the Rate button on the details page, both for
 * the item of the section (40-details.js) and only while its section is shown.
 *
 * The chip, at the end of jellyfin-web's media-info row (10-selectors.js, chipRow): the group icon, "Members" (hidden below
 * 600 px) and "4.2/5 (7)", so it never reads as a poor /10 score beside the community ★7.3. Its title and what a screen
 * reader hears are "Member average 4.2 out of 5, 7 ratings" (the visible parts are hidden from it), so neither touch nor
 * the TV layout needs a tooltip. It shows while ShowAverageChip is on and the section's summary has an average (at least
 * MinRatingsForAverage ratings), and follows the summary as members rate. jellyfin-web writes the row again with innerHTML
 * (another version chosen in the source list, the item edited through More), which takes the chip with it: the scanner
 * (kit.observe, the body's MutationObserver) puts the same chip back. On Season and Book pages the row is empty and
 * hidden, so the chip un-hides it while it is there, and hides it again as it goes when the row holds nothing else.
 *
 * The Rate button, before More in the buttons row (rateButtonPlace): ShowDetailButton Always shows it on every page, Never
 * on none, and Auto on Series, Season and MusicAlbum pages, whose child list comes before the section (the item's type
 * through jellyfin-web's ApiClient, read once per section), and on any page where Jellyfin Enhanced's Reviews section is
 * (kit.je.reviews), looked for on every scan since JE draws it late: JE 12.14's closed Reviews trap the TV layout's D-pad
 * in the buttons row. Pressing it expands the section, focuses the slider (for a member who can't rate, the read-only stars
 * in its place; else the section's title) and scrolls it to the middle of the view.
 *
 * The TV trap's other half is the kit's (kit.je.tvSectionJump): on the TV layout the closed Reviews'
 * controls outside its title leave jellyfin-web's focus order, so D-pad Down passes them and reaches the section, with any
 * ShowDetailButton. The jump's own button would be a second way to the section beside the Rate button, so its target is
 * never given and it draws none.
 */

// The item types ShowDetailButton Auto shows the Rate button on: their child list comes first.
const RATE_AUTO_TYPES = Object.freeze(['Series', 'Season', 'MusicAlbum']);

// ShowDetailButton's names; the server sends one of them, and anything else is Auto.
const RATE_MODES = Object.freeze(['Auto', 'Always', 'Never']);

// The row the chip un-hid carries this attribute while the chip is in it.
const CHIP_UNHID = 'data-jfur-unhid';

function rateMode(display) {
    const name = String((display && display.ShowDetailButton) || '').toLowerCase();
    for (let i = 0; i < RATE_MODES.length; i++) {
        if (RATE_MODES[i].toLowerCase() === name) {
            return RATE_MODES[i];
        }
    }

    return 'Auto';
}

// The chip's texts for a summary: { value: "4.2/5 (7)", label: "Member average 4.2 out of 5, 7 ratings" }, or null while
// there is no average to show (none, fewer than MinRatingsForAverage, a write's partial answer) or the chip is off.
function chipParts(summary, display) {
    if (display && display.ShowAverageChip === false) {
        return null;
    }

    const s = detailsSummary(summary, display, [], false, false);
    if (!s || s.kind !== 'average') {
        return null;
    }

    return { value: kit.t('chip.value', { average: s.average, count: s.count }), label: s.label };
}

// Whether the Rate button belongs on the page: Always on every page, Never on none, Auto by the
// item's type (null while unknown) or where Jellyfin Enhanced's Reviews section is. Only Auto looks at either, so Always
// and Never read nothing.
function rateWanted(mode, entry) {
    if (mode !== 'Auto') {
        return mode === 'Always';
    }

    return RATE_AUTO_TYPES.indexOf(detailsItemType(entry)) >= 0 || !!kit.je.reviews(entry.page);
}

// The item's type for ShowDetailButton Auto (decided by the type, not by the child list), read through
// jellyfin-web's ApiClient as Bookmarks' details button reads it (getItem, one GET of the item, once per section): null
// until it is known, or when it can't be read; the scan runs again with the answer.
function detailsItemType(entry) {
    if (!entry.typeAsked) {
        entry.typeAsked = true;
        const client = kit.client();
        const userId = kit.user.id();
        if (client && userId && typeof client.getItem === 'function') {
            Promise.resolve().then(function () {
                return client.getItem(userId, entry.itemId);
            }).then(function (item) {
                if (!entry.dead && item && typeof item.Type === 'string') {
                    entry.itemType = item.Type;
                    syncDetailsExtras();
                }
            }, function () {
                // Unknown: the Rate button then shows only where Jellyfin Enhanced's Reviews are.
            });
        }
    }

    return entry.itemType || null;
}

// The chip of an item, marked with its id (data-jfur-chip).
function buildChip(itemId) {
    return kit.h('div', { class: 'jfur-chip', 'data-jfur-chip': itemId }, [
        kit.icon('group', 'jfur-chip-icon'),
        kit.h('span', { class: 'jfur-chip-word', 'aria-hidden': 'true', text: kit.t('chip.word') }),
        kit.h('span', { class: 'jfur-chip-value', 'aria-hidden': 'true' }),
        kit.h('span', { class: 'jfur-sr-only jfur-chip-label' })
    ]);
}

function setText(node, text) {
    if (node.textContent !== text) {
        node.textContent = text;
    }
}

// A chip out of its row: the row hidden again when the chip un-hid it and it holds nothing else.
function removeChip(chip) {
    const row = chip.parentNode;
    chip.remove();
    if (row && row.hasAttribute && row.hasAttribute(CHIP_UNHID)) {
        row.removeAttribute(CHIP_UNHID);
        if (row.children.length === 0) {
            kit.setHidden(row, true);
        }
    }
}

function syncChip(entry) {
    const parts = chipParts(entry.summary, detailsDisplay(entry));
    const row = parts ? chipRow(entry.page) : null;
    if (!row) {
        if (entry.chip && entry.chip.parentNode) {
            removeChip(entry.chip);
        }

        return;
    }

    if (!entry.chip) {
        entry.chip = buildChip(entry.itemId);
    }

    const chip = entry.chip;
    setText(chip.querySelector('.jfur-chip-value'), parts.value);
    setText(chip.querySelector('.jfur-chip-label'), parts.label);
    if (chip.getAttribute('title') !== parts.label) {
        chip.setAttribute('title', parts.label);
    }

    placeChip(chip, row);
}

// Once in the row the chip stays where it is (another plugin may add to the row after it); a row jellyfin-web left empty
// and hidden is shown while it holds the chip.
function placeChip(chip, row) {
    if (chip.parentNode !== row) {
        if (chip.parentNode) {
            removeChip(chip);
        }

        row.append(chip);
    }

    if (kit.isHidden(row)) {
        kit.setHidden(row, false);
        row.setAttribute(CHIP_UNHID, '');
    }
}

// A restored view reads its item again (onDetailsViewShow, 40-details.js): the chip its shown section had stays, saying
// what it said, while the new section of the same item loads or failed for a reason of its own (busy, no answer, with
// Retry), so it doesn't blink. The new section's answer takes it over, to update or remove it; a 403
// or 404, another item, another view or a sign-out removes it.
function keepChip(entry) {
    const kept = detailsState.keptChip;
    let chip = null;
    if (!entry.dead && entry.state === 'shown' && entry.chip && entry.chip.isConnected) {
        chip = entry.chip;
    } else if (kept && kept.owner === entry) {
        chip = kept.chip;
    }

    detailsState.keptChip = chip ? { leaving: entry, owner: null, page: entry.page, itemId: entry.itemId, chip: chip } : null;
}

// The kept chip (keepChip), or null once it is let go: kept while the section it came from is being left, while the
// scanner hasn't mounted the next one on the same page and item yet, and while that one loads, shows its load error or
// isn't in the page yet. The next one shown takes it over as its own chip (live: the shown section's entry, or null).
function keptChipFor(entry, live) {
    const kept = detailsState.keptChip;
    if (!kept) {
        return null;
    }

    if (entry && entry === kept.leaving) {
        return kept;
    }

    if (!entry) {
        const page = shownDetailPage();
        if (page && page === kept.page && detailsId(kit.hashParams().get('id')) === kept.itemId) {
            return kept;
        }
    } else if (!entry.dead && entry.page === kept.page && entry.itemId === kept.itemId) {
        if (!live) {
            // Loading, its load error, or its answer before the section is in the page.
            kept.leaving = null;
            kept.owner = entry;
            return kept;
        }

        if (!live.chip) {
            live.chip = kept.chip;
        }
    }

    detailsState.keptChip = null;
    return null;
}

// The control Rate takes the focus to: the slider; the read-only stars in its place (NotARater); else the section's title.
function rateNow(entry) {
    if (entry.dead || !entry.els || !entry.els.section.isConnected) {
        return;
    }

    if (!entry.expanded) {
        setExpanded(entry, true);
    }

    const els = entry.els;
    const slider = entry.slider && !kit.isHidden(entry.slider.element) ? entry.slider.slider : null;
    const focused = focusIfShown(slider) || focusIfShown(els.readOnly) || focusIfShown(els.toggle);
    const target = focused ? document.activeElement : els.section;
    if (typeof target.scrollIntoView === 'function') {
        target.scrollIntoView({ block: 'center' });
    }
}

function syncRateButton(entry) {
    const place = rateButtonPlace(entry.page);
    const wanted = !!place && rateWanted(rateMode(detailsDisplay(entry)), entry);
    if (!wanted) {
        if (entry.rateButton && entry.rateButton.parentNode) {
            entry.rateButton.remove();
        }

        return;
    }

    if (!entry.rateButton) {
        entry.rateButton = kit.detailButton({
            className: 'jfur-rate',
            icon: 'star',
            title: kit.t('rate.button'),
            onClick: function (event) {
                if (event && typeof event.preventDefault === 'function') {
                    event.preventDefault();
                }

                rateNow(entry);
            }
        });
    }

    // Placed once per row: a button already in it stays where it is (moving it would drop the focus it may have).
    if (entry.rateButton.parentNode !== place.parent) {
        place.parent.insertBefore(entry.rateButton, place.before);
        kit.upgrade(entry.rateButton);
    }
}

// The scanner (kit.observe, after syncDetailsSection's), and after every render of the section and its unmounting: the
// chip and the Rate button of the shown section, the chip kept while a restored view reads its item again (keepChip), and
// nothing of ours anywhere else.
function syncDetailsExtras() {
    const entry = detailsState.entry;
    const live = entry && !entry.dead && entry.state === 'shown' && entry.els && entry.els.section.isConnected ? entry : null;
    const kept = keptChipFor(entry, live);
    document.querySelectorAll('.jfur-chip').forEach(function (node) {
        if (!(live && node === live.chip) && !(kept && node === kept.chip)) {
            removeChip(node);
        }
    });
    document.querySelectorAll('.jfur-rate').forEach(function (node) {
        if (!live || node !== live.rateButton) {
            node.remove();
        }
    });
    if (live) {
        syncChip(live);
        syncRateButton(live);
    } else if (kept && kept.owner) {
        // jellyfin-web may have written the row again meanwhile (a restored view renders it).
        const row = chipRow(kept.page);
        if (row) {
            placeChip(kept.chip, row);
        }
    }
}

// The kit's TV mitigation of Jellyfin Enhanced's closed Reviews (above): its focus-order half only.
function registerReviewsTvFix() {
    kit.je.tvSectionJump({
        label: kit.t('rate.button'),
        page: shownDetailPage,
        buttons: function (page) {
            return findAnchor('detailButtons', page);
        },
        target: function () {
            return null;
        }
    });
}
