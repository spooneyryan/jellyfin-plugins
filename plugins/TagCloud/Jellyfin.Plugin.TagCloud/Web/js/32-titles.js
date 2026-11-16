// The pure part of a word's titles list and of the picture shown when the pointer rests on a word: the list's view (its
// sort, direction, minimum resolution and Unwatched only, kept per user on this device), what it asks GET /TagCloud/Titles
// for, its count, and where the picture card goes beside the pointer. tests/.../Web/tagcloud.test.mjs checks the cases.

const tcTitleView = (function () {
    // What the list sorts by, with the words for each direction.
    const SORTS = Object.freeze([
        Object.freeze({ value: 'Name', text: 'Name', up: 'A to Z', down: 'Z to A' }),
        Object.freeze({ value: 'ReleaseDate', text: 'Release date', up: 'Oldest first', down: 'Newest first' }),
        Object.freeze({ value: 'DateAdded', text: 'Date added', up: 'Oldest first', down: 'Newest first' }),
        Object.freeze({ value: 'Rating', text: 'Rating', up: 'Lowest first', down: 'Highest first' })
    ]);

    // The least sharp movie kept: the server's TagCloudMinResolution.
    const RESOLUTIONS = Object.freeze([
        Object.freeze({ value: 'Any', text: 'Any' }),
        Object.freeze({ value: 'P480', text: '480p and up' }),
        Object.freeze({ value: 'P720', text: '720p and up' }),
        Object.freeze({ value: 'P1080', text: '1080p and up' }),
        Object.freeze({ value: 'P4K', text: '4K' })
    ]);

    const DEFAULTS = Object.freeze({ Sort: 'Name', Descending: false, MinResolution: 'Any', Unwatched: false });

    // The server's limit on the name filter.
    const MAX_QUERY = 100;

    function known(list, value) {
        return list.some(function (choice) {
            return choice.value === value;
        });
    }

    // A view as stored (or anything else) as one the list can use: unknown values go back to their defaults.
    function normalize(stored) {
        const view = stored && typeof stored === 'object' ? stored : {};
        return {
            Sort: known(SORTS, view.Sort) ? view.Sort : DEFAULTS.Sort,
            Descending: view.Descending === true,
            MinResolution: known(RESOLUTIONS, view.MinResolution) ? view.MinResolution : DEFAULTS.MinResolution,
            Unwatched: view.Unwatched === true
        };
    }

    // Whether the resolution filter applies: to movies only (a show's episodes are its videos), as the list offers it.
    function resolutionApplies(scope) {
        return scope === 'Movies';
    }

    // Nothing filtered out, A to Z: what Reset goes back to. The resolution counts only where it applies.
    function isDefault(view, text, scope) {
        return view.Sort === DEFAULTS.Sort && !view.Descending && !view.Unwatched && !String(text || '').trim() &&
            (!resolutionApplies(scope) || view.MinResolution === DEFAULTS.MinResolution);
    }

    // What GET /TagCloud/Titles is asked for: defaults left out, so the request stays as short as before.
    function query(view, text, scope) {
        const q = String(text || '').trim().substring(0, MAX_QUERY);
        return {
            sort: view.Sort === DEFAULTS.Sort ? null : view.Sort,
            descending: view.Descending ? 'true' : null,
            q: q || null,
            minResolution: resolutionApplies(scope) && view.MinResolution !== DEFAULTS.MinResolution ? view.MinResolution : null,
            unwatched: view.Unwatched ? 'true' : null
        };
    }

    function sortOf(value) {
        return SORTS.filter(function (s) {
            return s.value === value;
        })[0] || SORTS[0];
    }

    // "12 movies", or "3 of 12 movies" while the filters leave some out.
    function countText(total, unfiltered, unit) {
        const all = Number(unfiltered || 0);
        const kept = Number(total || 0);
        const noun = all === 1 ? unit.one : unit.many;
        return kept < all ? kept.toLocaleString() + ' of ' + all.toLocaleString() + ' ' + noun : all.toLocaleString() + ' ' + noun;
    }

    // Where the picture card goes: to the right of the pointer, or to its left where that would run past the window,
    // centred on the pointer's height and kept inside the window (8 px from its edges).
    function peekPosition(x, y, width, height, viewWidth, viewHeight) {
        const gap = 18;
        const margin = 8;
        let left = x + gap;
        if (left + width + margin > viewWidth) {
            left = x - gap - width;
        }

        left = Math.max(margin, Math.min(left, viewWidth - width - margin));
        const top = Math.max(margin, Math.min(y - height / 2, viewHeight - height - margin));
        return { left: Math.round(left), top: Math.round(top) };
    }

    return {
        SORTS: SORTS,
        RESOLUTIONS: RESOLUTIONS,
        DEFAULTS: DEFAULTS,
        MAX_QUERY: MAX_QUERY,
        normalize: normalize,
        resolutionApplies: resolutionApplies,
        isDefault: isDefault,
        query: query,
        sortOf: sortOf,
        countText: countText,
        peekPosition: peekPosition
    };
})();
