// The basic search as a state, the hash parameters that hold it, the query it sends, and a saved query read back into it.
//
// The state lives in the page's hash (kit.pages.setParams), so a reload, a deep link and Back show the same search:
//   tab         search (the default), saved, or collections (the Auto-collections tab, for managers): the tab shown
//   edit        on the Auto-collections tab: the id of the auto-collection whose editor opens (the detail-page note's Edit)
//   query       the id of the saved query open on the Search tab
//   scope, find the Saved queries tab's filter (Mine or Shared; none: All) and the text it finds
//   kinds       item kinds, comma-separated (Movie,Series)
//   lib         the libraries' ids, comma-separated, or none for all of the user's
//   missing     1: include missing episodes (with Episode only)
//   q           title contains (also what the native search page's link will carry)
//   from, to    years
//   genres      genre names, each URI-encoded, comma-separated
//   sort, order the sort key (SortName by default) and desc for descending
//   mode        adv: the advanced form (its rules in where); none: the basic form
//   where       the advanced form's rules: the query's Where as JSON
//   run         1: the search ran, so it runs again when the page shows (q alone runs it too)
// A value the hash says wrongly (an unknown kind, a library the user can't open, a year that isn't a number) is left out.
// The last scope (kinds, library) is remembered per browser (localStorage jfss:lastScope) for a page opened without one.

const SS_YEAR_MIN = 1800;
const SS_YEAR_MAX = 2100;
const SS_TEXT_MAX = 200;
const SS_VALUES_MAX = 100;
const SS_DEFAULT_KINDS = Object.freeze(['Movie', 'Series']);
const SS_DEFAULT_SORT = 'SortName';
// The longest Where a link's hash may hold (16 KB): a longer one is left out before it is parsed.
const SS_WHERE_MAX = 16 * 1024;

// An id as the server writes it (N format, lower case).
function ssId(value) {
    return String(value || '').replace(/-/g, '').toLowerCase();
}

function ssSplit(value) {
    return String(value || '').split(',').map(function (part) {
        try {
            return decodeURIComponent(part).trim();
        } catch (e) {
            return '';
        }
    }).filter(Boolean);
}

function ssUnique(values) {
    const seen = {};
    return values.filter(function (value) {
        const key = value.toLowerCase();
        if (seen[key]) {
            return false;
        }

        seen[key] = true;
        return true;
    });
}

// The state from the hash. options: { kinds (allowed, in the server's order), sortKeys, libraryIds, lastScope }.
function ssReadState(params, options) {
    params = params || {};
    const allowed = options.kinds || [];
    const pick = function (kinds) {
        return allowed.filter(function (kind) {
            return kinds.indexOf(kind) >= 0;
        });
    };
    const last = options.lastScope && typeof options.lastScope === 'object' ? options.lastScope : {};
    let kinds = pick(ssSplit(params.kinds));
    if (kinds.length === 0) {
        kinds = pick(Array.isArray(last.kinds) ? last.kinds : []);
    }

    if (kinds.length === 0) {
        kinds = pick(SS_DEFAULT_KINDS);
    }

    if (kinds.length === 0 && allowed.length > 0) {
        kinds = [allowed[0]];
    }

    const libraries = (options.libraryIds || []).map(ssId);
    // A hash that holds a search says its libraries (none: all of them); the last scope fills in only a bare page.
    const fromHash = params.kinds !== undefined || params.run !== undefined || params.q !== undefined || params.mode !== undefined;
    const libParam = ssUnique(String(fromHash ? params.lib || '' : (typeof last.lib === 'string' ? last.lib : '')).split(',').map(ssId).filter(function (id) {
        return libraries.indexOf(id) >= 0;
    }));
    const year = function (value) {
        const text = String(value || '').trim();
        return /^\d{1,4}$/.test(text) ? text : '';
    };
    const sortKeys = options.sortKeys || [SS_DEFAULT_SORT];
    const q = String(params.q || '').trim().substring(0, SS_TEXT_MAX);
    let where = null;
    if (params.mode === 'adv') {
        const text = String(params.where || '');
        try {
            where = text.length <= SS_WHERE_MAX ? JSON.parse(text) : null;
        } catch (e) {
            where = null;
        }

        where = ssWhereShapeOk(where) ? where : { Match: 'All', Children: [] };
    }

    return {
        tab: params.tab === 'saved' || params.tab === 'search' || params.tab === 'collections' ? params.tab : null,
        edit: params.tab === 'collections' && /^[0-9a-f]{32}$/i.test(String(params.edit || '')) ? ssId(params.edit) : null,
        query: params.query || null,
        scope: params.scope === 'Mine' || params.scope === 'Shared' ? params.scope : '',
        find: String(params.find || '').substring(0, SS_TEXT_MAX),
        kinds: kinds,
        lib: libParam.join(','),
        missing: params.missing === '1' && kinds.indexOf('Episode') >= 0,
        q: q,
        from: year(params.from),
        to: year(params.to),
        genres: ssUnique(ssSplit(params.genres).map(function (g) {
            return g.substring(0, SS_TEXT_MAX);
        })).slice(0, SS_VALUES_MAX),
        sort: sortKeys.indexOf(params.sort) >= 0 ? params.sort : SS_DEFAULT_SORT,
        desc: params.order === 'desc',
        mode: where ? 'adv' : '',
        where: where,
        run: params.run === '1' || q.length > 0
    };
}

// The hash parameters of a state: only what isn't empty or the default.
function ssWriteParams(state) {
    const out = {};
    const set = function (name, value) {
        if (value !== undefined && value !== null && value !== '' && value !== false) {
            out[name] = value;
        }
    };
    set('tab', state.tab);
    set('edit', state.tab === 'collections' ? state.edit : null);
    set('query', state.query);
    set('scope', state.scope);
    set('find', state.find);
    set('kinds', state.kinds.join(','));
    set('lib', state.lib);
    set('missing', state.missing && state.kinds.indexOf('Episode') >= 0 ? '1' : '');
    set('q', state.q);
    set('from', state.from);
    set('to', state.to);
    set('genres', state.genres.map(encodeURIComponent).join(','));
    set('sort', state.sort !== SS_DEFAULT_SORT ? state.sort : '');
    set('order', state.desc ? 'desc' : '');
    set('mode', state.mode === 'adv' ? 'adv' : '');
    set('where', state.mode === 'adv' && state.where ? JSON.stringify(state.where) : '');
    set('run', state.run ? '1' : '');
    return out;
}

// The form's own checks before a search: { field: message } (empty when it may run).
function ssCheckState(state) {
    const errors = {};
    const valid = function (text) {
        const n = Number(text);
        return text === '' || (/^\d+$/.test(text) && n >= SS_YEAR_MIN && n <= SS_YEAR_MAX);
    };
    if (!valid(state.from) || !valid(state.to)) {
        errors.years = SS_TEXT.yearInvalid;
    } else if (state.from !== '' && state.to !== '' && Number(state.from) > Number(state.to)) {
        errors.years = SS_TEXT.yearOrder;
    }

    return errors;
}

// The query a state sends, and which form field each rule came from ({ query, fields: ['q' | 'years' | 'genres'] }):
// the basic form's one flat All group of its fields, or the advanced form's Where; the scope from the scope bar; one
// sort key. kept: what an open saved query holds that the form doesn't show (ssKeptOfQuery), sent as it was.
function ssBuildQuery(state, kept) {
    const children = [];
    const fields = [];
    const libraries = state.lib ? state.lib.split(',') : [];
    const scope = { ItemKinds: state.kinds.slice(), LibraryIds: libraries.concat(kept && kept.lib === state.lib ? kept.libraries : []), IncludeMissing: !!state.missing };
    const sort = [{ Key: state.sort, Order: state.desc ? 'Descending' : 'Ascending' }].concat(kept ? kept.sort : []);
    const finish = function (where) {
        const query = { Version: 1, Scope: scope, Where: where, Sort: sort };
        if (kept && kept.limit !== null && kept.limit !== undefined) {
            query.Limit = kept.limit;
        }

        return { query: query, fields: fields };
    };
    if (state.mode === 'adv' && state.where) {
        return finish(JSON.parse(JSON.stringify(state.where)));
    }

    if (state.q) {
        children.push({ Field: 'Title', Op: 'Contains', Value: state.q });
        fields.push('q');
    }

    const from = state.from === '' ? null : Number(state.from);
    const to = state.to === '' ? null : Number(state.to);
    if (from !== null && to !== null) {
        children.push(from === to ? { Field: 'Year', Op: 'Eq', Value: from } : { Field: 'Year', Op: 'Between', From: from, To: to });
        fields.push('years');
    } else if (from !== null) {
        children.push({ Field: 'Year', Op: 'Ge', Value: from });
        fields.push('years');
    } else if (to !== null) {
        children.push({ Field: 'Year', Op: 'Le', Value: to });
        fields.push('years');
    }

    if (state.genres.length > 0) {
        children.push({ Field: 'Genres', Op: 'AnyOf', Values: state.genres.slice() });
        fields.push('genres');
    }

    return finish({ Match: 'All', Children: children });
}

// Whether the hash holds a search of its own (beyond the tab, the saved query and the Saved tab's filter).
function ssHasFormParams(params) {
    return ['kinds', 'lib', 'missing', 'q', 'from', 'to', 'genres', 'sort', 'order', 'mode', 'where', 'run'].some(function (name) {
        return params && params[name] !== undefined;
    });
}

// What a state searches for, as one string: two states with the same key send the same query.
function ssSearchKey(state) {
    const adv = state.mode === 'adv';
    return JSON.stringify([state.kinds, state.lib, !!state.missing, adv ? '' : state.q, adv ? '' : state.from, adv ? '' : state.to, adv ? [] : state.genres,
        state.sort, !!state.desc, adv ? state.where : null]);
}

// Whether the form holds a rule (a title, a year or a genre; an advanced rule or group).
function ssHasRules(state) {
    if (state.mode === 'adv') {
        return !!(state.where && state.where.Children && state.where.Children.length);
    }

    return !!(state.q || state.from || state.to || state.genres.length);
}

// What a saved query holds that the form doesn't show, kept and sent as it was: the libraries the user can't open
// (never shown, only counted; sent while the visible libraries are those saved), the sort keys after the first (the
// form shows one), and its Limit. lib: the form's visible libraries as saved.
function ssKeptOfQuery(query, libraryIds) {
    const scope = query && query.Scope;
    const known = (libraryIds || []).map(ssId);
    const named = scope && Array.isArray(scope.LibraryIds) ? ssUnique(scope.LibraryIds.map(ssId).filter(Boolean)) : [];
    const sort = query && Array.isArray(query.Sort) ? query.Sort : [];
    return {
        lib: named.filter(function (id) {
            return known.indexOf(id) >= 0;
        }).join(','),
        libraries: named.filter(function (id) {
            return known.indexOf(id) < 0;
        }),
        sort: JSON.parse(JSON.stringify(sort.slice(1))),
        limit: query && typeof query.Limit === 'number' ? query.Limit : null
    };
}

// The name in names that name is, ignoring case (the server reads enum names and sort keys so), or null.
function ssNameIn(names, name) {
    const lower = String(name === undefined || name === null ? '' : name).toLowerCase();
    for (let i = 0; i < names.length; i++) {
        if (String(names[i]).toLowerCase() === lower) {
            return names[i];
        }
    }

    return null;
}

// A saved query's definition as the advanced form's state: its kinds the server allows, its libraries the user can
// open, its first sort key when the server lists it, and its Where as the tree writes it again (so an unchanged form
// compares equal). Kinds, the sort key and its order are read ignoring case, as the server reads them.
// options: { kinds, sortKeys, libraryIds, catalog }. Returns { state, kept, dropped, exact }: dropped the kinds it named
// that the server no longer allows (left out); exact whether the form sends the query back as it was saved
// (ssSameQuery). A query that isn't exact opens locked.
function ssAdvancedStateFromQuery(query, options) {
    const scope = query && ssPlain(query.Scope) ? query.Scope : {};
    const allowed = options.kinds || [];
    const named = Array.isArray(scope.ItemKinds) ? scope.ItemKinds.map(String) : [];
    let kinds = allowed.filter(function (kind) {
        return ssNameIn(named, kind) !== null;
    });
    const dropped = named.filter(function (kind) {
        return ssNameIn(allowed, kind) === null;
    });
    if (kinds.length === 0) {
        kinds = allowed.slice(0, 1);
    }

    const kept = ssKeptOfQuery(query, options.libraryIds);
    const first = query && Array.isArray(query.Sort) && query.Sort.length > 0 && ssPlain(query.Sort[0]) ? query.Sort[0] : null;
    const key = first ? ssNameIn(options.sortKeys || [], first.Key) : null;
    const state = {
        kinds: kinds,
        lib: kept.lib,
        missing: scope.IncludeMissing === true && kinds.indexOf('Episode') >= 0,
        q: '',
        from: '',
        to: '',
        genres: [],
        sort: key || SS_DEFAULT_SORT,
        desc: !!key && String(first.Order).toLowerCase() === 'descending',
        mode: 'adv',
        where: ssWhereFromTree(ssTreeFromWhere(query && query.Where, options.catalog), options.catalog)
    };
    return {
        state: state,
        kept: kept,
        dropped: dropped,
        exact: ssSameQuery(query, ssBuildQuery(state, kept).query, dropped)
    };
}

// The members whose values the server reads ignoring case (enum names, sort keys).
const SS_ENUM_MEMBERS = Object.freeze(['match', 'op', 'unit', 'order', 'key', 'itemkinds']);

// A query's JSON as compared with another (ssSameQuery): member names in lower case, as the server reads them, and the
// values of SS_ENUM_MEMBERS too; the defaults left out (false, null, an empty Role or Provider, no libraries, a missing
// Match as All, IncludeMissing without episodes, no sort or the sort by title ascending); library ids as the server
// writes them; kinds and libraries sorted, once each. Other values (text, numbers, field ids) stay as they are.
function ssNormalQuery(query) {
    const walk = function (value, member) {
        if (Array.isArray(value)) {
            return value.map(function (item) {
                return walk(item, member);
            });
        }

        if (ssPlain(value)) {
            const out = {};
            Object.keys(value).sort().forEach(function (name) {
                const lower = name.toLowerCase();
                const item = value[name];
                if (item === undefined || item === null || item === false || ((lower === 'role' || lower === 'provider') && item === '')) {
                    return;
                }

                out[lower] = walk(item, lower);
            });
            return out;
        }

        return typeof value === 'string' && SS_ENUM_MEMBERS.indexOf(member) >= 0 ? value.toLowerCase() : value;
    };
    const group = function (node) {
        if (ssPlain(node) && Array.isArray(node.children) && node.field === undefined) {
            node.match = node.match === undefined ? 'all' : node.match;
            node.children.forEach(group);
        }

        return node;
    };
    const out = walk(ssPlain(query) ? query : {}, '');
    const scope = ssPlain(out.scope) ? out.scope : {};
    scope.itemkinds = ssUnique(Array.isArray(scope.itemkinds) ? scope.itemkinds.map(String) : []).sort();
    scope.libraryids = ssUnique(Array.isArray(scope.libraryids) ? scope.libraryids.map(ssId) : []).sort();
    if (scope.libraryids.length === 0) {
        delete scope.libraryids;
    }

    if (scope.includemissing === true && scope.itemkinds.indexOf('episode') < 0) {
        delete scope.includemissing;
    }

    out.scope = scope;
    out.where = group(out.where === undefined ? { children: [] } : out.where);
    if (Array.isArray(out.sort)) {
        out.sort = out.sort.map(function (s) {
            return ssPlain(s) && s.order === undefined ? Object.assign({ order: 'ascending' }, s) : s;
        });
        const only = out.sort.length === 1 && ssPlain(out.sort[0]) ? out.sort[0] : null;
        if (out.sort.length === 0 || (only && Object.keys(only).length === 2 && only.key === 'sortname' && only.order === 'ascending')) {
            delete out.sort;
        }
    }

    return JSON.stringify(out, function (name, value) {
        // Members in a fixed order, so the text compares.
        return ssPlain(value) ? Object.keys(value).sort().reduce(function (o, k) {
            o[k] = value[k];
            return o;
        }, {}) : value;
    });
}

// Whether the query the form sends (built) is the saved one (saved), both normalised the same way (ssNormalQuery). The
// kinds the server no longer allows (dropped), which the form names beside the kinds, are left out of the saved one;
// when none of its kinds is left, the form's first allowed kind stands in for them.
function ssSameQuery(saved, built, dropped) {
    const gone = (dropped || []).map(function (kind) {
        return String(kind).toLowerCase();
    });
    let copy = saved;
    if (gone.length > 0 && ssPlain(saved) && ssPlain(saved.Scope) && Array.isArray(saved.Scope.ItemKinds)) {
        const left = saved.Scope.ItemKinds.filter(function (kind) {
            return gone.indexOf(String(kind).toLowerCase()) < 0;
        });
        copy = Object.assign({}, saved, { Scope: Object.assign({}, saved.Scope, { ItemKinds: left.length ? left : built.Scope.ItemKinds }) });
    }

    return ssNormalQuery(copy) === ssNormalQuery(built);
}

// How many of the libraries a query names the user can't open: they are never named or shown, only counted.
function ssUnavailableLibraries(query, libraryIds) {
    const scope = query && query.Scope;
    const known = (libraryIds || []).map(ssId);
    const named = scope && Array.isArray(scope.LibraryIds) ? ssUnique(scope.LibraryIds.map(ssId).filter(Boolean)) : [];
    return named.filter(function (id) {
        return known.indexOf(id) < 0;
    }).length;
}

// A saved query's definition as the basic form's state, or null when the form can't show it as it is: anything but
// one All group of at most a title Contains, a year (Eq, Between, Ge or Le) and genres AnyOf, over kinds the server
// allows, at most one library the user can open, and one sort key the server lists. Such a query runs as it was saved.
// options: { kinds, sortKeys, libraryIds }.
function ssStateFromQuery(query, options) {
    const only = function (value, names) {
        return !!value && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).every(function (name) {
            return names.indexOf(name) >= 0;
        });
    };
    const year = function (value) {
        return typeof value === 'number' && Math.floor(value) === value && value >= SS_YEAR_MIN && value <= SS_YEAR_MAX;
    };
    const text = function (value) {
        return typeof value === 'string' && value !== '' && value === value.trim() && value.length <= SS_TEXT_MAX;
    };
    if (!only(query, ['Version', 'Scope', 'Where', 'Sort']) || query.Version !== 1) {
        return null;
    }

    const scope = query.Scope;
    const allowed = options.kinds || [];
    if (!only(scope, ['ItemKinds', 'LibraryIds', 'IncludeMissing']) || !Array.isArray(scope.ItemKinds) || scope.ItemKinds.length === 0
        || ssUnique(scope.ItemKinds.map(String)).length !== scope.ItemKinds.length || scope.ItemKinds.some(function (kind) {
            return allowed.indexOf(kind) < 0;
        })) {
        return null;
    }

    const kinds = allowed.filter(function (kind) {
        return scope.ItemKinds.indexOf(kind) >= 0;
    });
    const libraries = scope.LibraryIds === undefined ? [] : scope.LibraryIds;
    if (!Array.isArray(libraries) || libraries.length > 1
        || (libraries.length === 1 && (options.libraryIds || []).map(ssId).indexOf(ssId(libraries[0])) < 0)) {
        return null;
    }

    const missing = scope.IncludeMissing === undefined ? false : scope.IncludeMissing;
    if (typeof missing !== 'boolean' || (missing && kinds.indexOf('Episode') < 0)) {
        return null;
    }

    const where = query.Where;
    if (!only(where, ['Match', 'Negate', 'Children']) || where.Match !== 'All' || (where.Negate !== undefined && where.Negate !== false)
        || !Array.isArray(where.Children)) {
        return null;
    }

    const state = { kinds: kinds, lib: libraries.length ? ssId(libraries[0]) : '', missing: missing, q: '', from: '', to: '', genres: [] };
    const seen = {};
    const fits = where.Children.every(function (rule) {
        if (!rule || typeof rule !== 'object' || (rule.Negate !== undefined && rule.Negate !== false) || seen[rule.Field]) {
            return false;
        }

        seen[rule.Field] = true;
        if (rule.Field === 'Title') {
            state.q = rule.Value;
            return only(rule, ['Field', 'Op', 'Value', 'Negate']) && rule.Op === 'Contains' && text(rule.Value);
        }

        if (rule.Field === 'Year' && (rule.Op === 'Eq' || rule.Op === 'Ge' || rule.Op === 'Le')) {
            state.from = rule.Op === 'Le' ? '' : String(rule.Value);
            state.to = rule.Op === 'Ge' ? '' : String(rule.Value);
            return only(rule, ['Field', 'Op', 'Value', 'Negate']) && year(rule.Value);
        }

        if (rule.Field === 'Year' && rule.Op === 'Between') {
            state.from = String(rule.From);
            state.to = String(rule.To);
            return only(rule, ['Field', 'Op', 'From', 'To', 'Negate']) && year(rule.From) && year(rule.To) && rule.From <= rule.To;
        }

        if (rule.Field === 'Genres' && rule.Op === 'AnyOf') {
            state.genres = Array.isArray(rule.Values) ? rule.Values.slice() : [];
            return only(rule, ['Field', 'Op', 'Values', 'Negate']) && state.genres.length > 0 && state.genres.length <= SS_VALUES_MAX
                && state.genres.every(text) && ssUnique(state.genres).length === state.genres.length;
        }

        return false;
    });
    const sort = query.Sort;
    if (!fits || !Array.isArray(sort) || sort.length !== 1 || !only(sort[0], ['Key', 'Order'])
        || (options.sortKeys || [SS_DEFAULT_SORT]).indexOf(sort[0].Key) < 0 || (sort[0].Order !== 'Ascending' && sort[0].Order !== 'Descending')) {
        return null;
    }

    state.sort = sort[0].Key;
    state.desc = sort[0].Order === 'Descending';
    return state;
}

// A name or description for the page: a control character (which shows as nothing, or moves the text around) as U+FFFD.
// It always goes in as text, and in a <bdi> or between ssIsolate's marks, so a name never changes the markup or the
// direction of the sentence around it.
function ssDisplayText(value) {
    return String(value === undefined || value === null ? '' : value).replace(/[\u0000-\u001f\u007f-\u009f]/g, '�');
}

// A name in a sentence of plain text (a toast, a confirmation): between first-strong isolate marks (U+2068, U+2069),
// the text form of <bdi>, so a right-to-left name or an override inside it never reorders the sentence.
function ssIsolate(value) {
    return '⁨' + ssDisplayText(value) + '⁩';
}

// The form field a server error's path points at ("Where.Children[1].From" → the rule's field), or null.
function ssFieldOfPath(path, fields) {
    // A saved query's errors are paths inside its body's Query.
    const text = String(path || '').replace(/^Query\./, '');
    const rule = /^Where\.Children\[(\d+)\]/.exec(text);
    if (rule) {
        return fields[Number(rule[1])] || null;
    }

    if (text.indexOf('Scope.ItemKinds') === 0) {
        return 'kinds';
    }

    if (text.indexOf('Scope.LibraryIds') === 0) {
        return 'lib';
    }

    return text.indexOf('Sort') === 0 ? 'sort' : null;
}

// What a result's list row says under the kit's title and year: kind, runtime, community rating, date added.
function ssItemFacts(item) {
    const facts = [];
    if (item.Type && SS_KIND_SINGULAR[item.Type]) {
        facts.push(SS_KIND_SINGULAR[item.Type]);
    }

    if (item.RunTimeTicks > 0) {
        // jellyfin-web's own rounding (getDisplayDuration): whole minutes, at least 1.
        const minutes = Math.round(item.RunTimeTicks / 6e8) || 1;
        facts.push(minutes >= 60
            ? ssText('runtimeHours', { h: Math.floor(minutes / 60), m: minutes % 60 })
            : ssText('runtimeMinutes', { m: minutes }));
    }

    if (typeof item.CommunityRating === 'number') {
        facts.push(ssText('rating', { value: item.CommunityRating.toFixed(1) }));
    }

    if (item.DateCreated) {
        const date = kit.format.date(item.DateCreated);
        if (date) {
            facts.push(ssText('added', { date: date }));
        }
    }

    return facts.join(' · ');
}
