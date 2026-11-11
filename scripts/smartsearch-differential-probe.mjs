#!/usr/bin/env node
// Smart Search's differential check against a running sandbox: generated queries over every field and operator that
// GET /SmartSearch/Fields lists, run through POST /SmartSearch/Search for each user and compared with core's own
// /Items for the same user, evaluated here in memory with the plugin's documented semantics (folded text, ratings as
// stored floats, whole days in the server's zone, runtime as the web client rounds it, no play record as unplayed, a
// rule false on kinds its field doesn't apply to). For the administrator each query also runs with ForceResidual
// (the scope alone pushed down, every rule checked in memory), so the pushdown is compared with the database it runs
// on. Node 22 built-ins only. Loopback only, never the real server's ports. Tokens are read from credentials.json and never printed.
//
//   node scripts/smartsearch-differential-probe.mjs --sandbox <name> [--queries 200] [--seed 20261110]
//        [--no-seed-data] [--scan] [--out result.json]
//
// It writes, unless --no-seed-data: synthetic metadata on a fixed set of the sandbox's items (genres, tags and studios
// with case and accent variants and a '|', ratings next to float boundaries and a community rating of 0, parental
// ratings, premiere dates at 00:00Z and at local midnight, years outside 1800-2100, original titles and names with
// % _ [ ] ^, overviews, provider ids in both cases, locks, a series status), the same values every run, after checking
// that no library saves metadata next to the media; and played, favourite and resume states for admin and viewer2 (viewer keeps none). One studio name is new
// on every run, so its items have no Studio row until a scan (--scan scans after the seed, before the queries). For the
// run, viewer2 is restricted (no "Movies 2", parental limit 13, the tag ss-blocked blocked); its policy is saved to
// .sandbox/<name>/smartsearch-differential-probe/saved-policy.json first and put back at the end, or at the start of
// the next run when a run was killed.
//
// The queries: one for each field, operator and value form (Bool fields both ways, Title with and without the original
// title, three values for each text operator and two for each number and date operator), then --queries random ones
// (one to three rules, nested Any and negated groups, a library scope, a sort).
// Values come from the users' items (exact values, their boundaries, case and accent variants) and from values no item
// has. Each query runs as admin, viewer and viewer2; an administrator-only field must be 403 for the others. A result
// must equal what /Items shows that user (by id); the count must be no less than the results; the forced Residual run
// must find the same. It prints one JSON result (pass, counts, paths, coverage, every difference with the query) and
// exits 0 when there is no difference, no refusal and no invalid query.

import { argValue, finish, guid, openSandbox, USERS } from './smartsearch-probe-common.mjs';

const CLIENT = 'smartsearch-differential-probe';
const argv = process.argv.slice(2);
for (const a of argv) {
    if (a.startsWith('--') && !['--sandbox', '--queries', '--seed', '--no-seed-data', '--scan', '--out'].includes(a)) throw new Error(`unknown argument ${a}`);
}
const opts = {
    sandbox: argValue(argv, '--sandbox'),
    queries: Number(argValue(argv, '--queries') ?? 200),
    seed: Number(argValue(argv, '--seed') ?? 20261110),
    seedData: !argv.includes('--no-seed-data'),
    scan: argv.includes('--scan'),
    out: argValue(argv, '--out')
};
if (!Number.isInteger(opts.queries) || opts.queries < 0 || !Number.isInteger(opts.seed)) throw new Error('--queries and --seed take whole numbers');
const sb = openSandbox(opts.sandbox, CLIENT);

// ------------------------------------------------------------------------------------------------ random numbers

function prng(seed) {
    let a = seed >>> 0;
    return () => {
        a = (a + 0x6D2B79F5) >>> 0;
        let t = a;
        t = Math.imul(t ^ (t >>> 15), t | 1);
        t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}
const random = prng(opts.seed);
const int = (n) => Math.floor(random() * n);
const pick = (list) => list[int(list.length)];
const chance = (p) => random() < p;
const some = (list, max) => {
    const copy = [...list];
    const out = [];
    const n = 1 + int(Math.min(max, copy.length));
    while (out.length < n && copy.length) out.push(copy.splice(int(copy.length), 1)[0]);
    return out;
};

// ------------------------------------------------------------------------------------------------ the oracle

// Text folded as Jellyfin's CleanName folds it: accents removed (including the letters Unicode doesn't decompose) and
// lower case. Lone surrogates and U+FFFD are dropped first, as core does.
const LETTERS = { 'ł': 'l', 'Ł': 'L', 'ø': 'o', 'Ø': 'O', 'æ': 'ae', 'Æ': 'AE', 'œ': 'oe', 'Œ': 'OE', 'ß': 'ss', 'đ': 'd', 'Đ': 'D', 'ð': 'd', 'Ð': 'D', 'þ': 'th', 'Þ': 'TH', 'ı': 'i' };
const fold = (s) => s.replace(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]|�/g, '')
    .normalize('NFD').replace(/\p{Mn}/gu, '').replace(/[łŁøØæÆœŒßđĐðÐþÞı]/g, (c) => LETTERS[c]).normalize('NFC').toLowerCase();
const blank = (s) => s === null || s === undefined || /^\s*$/.test(s);
const upper = (s) => s.toUpperCase();

const pad = (n, w = 2) => String(n).padStart(w, '0');
const localDay = (ms) => { const d = new Date(ms); return `${pad(d.getFullYear(), 4)}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`; };
const utcDay = (ms) => new Date(ms).toISOString().slice(0, 10);
// A premiere date names a calendar day: 00:00:00Z exactly is that UTC day, any other time the server's local day.
const calendarDay = (ms) => (ms % 86400000 === 0 ? utcDay(ms) : localDay(ms));
const isDay = (text) => /^\d{4}-\d{2}-\d{2}$/.test(text);
const daysInMonth = (y, m) => new Date(Date.UTC(y, m + 1, 0)).getUTCDate();
function addMonths(ms, months) {
    const d = new Date(ms);
    const total = d.getUTCFullYear() * 12 + d.getUTCMonth() + months;
    const y = Math.floor(total / 12);
    const m = total - y * 12;
    const out = new Date(ms);
    out.setUTCFullYear(y, m, Math.min(d.getUTCDate(), daysInMonth(y, m)));
    return out.getTime();
}
function spanStart(rule, now) {
    switch (rule.Unit) {
        case 'Days': return now - rule.Amount * 86400000;
        case 'Weeks': return now - 7 * rule.Amount * 86400000;
        case 'Months': return addMonths(now, -rule.Amount);
        case 'Years': return addMonths(now, -12 * rule.Amount);
        default: throw new Error(`no unit ${rule.Unit}`);
    }
}

// Parental ratings' scores, as the server's localization lists them.
let ratingScores = new Map();
function scoreOf(rating) {
    if (blank(rating)) return null;
    const text = rating.trim();
    if (/^\d+$/.test(text)) return { score: Number(text), sub: 0 };
    const bare = text.replace(/^Rated\s*:?\s*/i, '').trim();
    return ratingScores.get(bare.toUpperCase()) ?? null;
}
const compareScores = (a, b) => Math.sign(a.score - b.score) || Math.sign((a.sub ?? 0) - (b.sub ?? 0));

const FIELDS_DTO = 'Genres,Tags,Studios,OriginalTitle,Overview,ProviderIds,Path,DateCreated,Width,MediaSources,Settings';

// One /Items row as the oracle reads it.
function rowOf(dto) {
    const id = guid(dto.Id);
    const sources = dto.MediaSources || [];
    const source = sources.find((s) => guid(s.Id) === id) || sources[0] || {};
    const ud = dto.UserData || {};
    const ms = (text) => (text ? Date.parse(text) : null);
    const year = dto.ProductionYear;
    return {
        id, name: dto.Name, kind: dto.Type, originalTitle: dto.OriginalTitle ?? null, overview: dto.Overview ?? null,
        seriesName: dto.Type === 'Episode' ? dto.SeriesName ?? null : null, seriesId: dto.Type === 'Episode' && dto.SeriesId ? guid(dto.SeriesId) : null,
        path: dto.Path ?? null,
        year: Number.isInteger(year) && year >= 1800 && year <= 2100 ? year : null,
        premiere: ms(dto.PremiereDate), added: ms(dto.DateCreated),
        genres: dto.Genres || [], tags: dto.Tags || [], studios: (dto.Studios || []).map((s) => s.Name),
        officialRating: dto.OfficialRating ?? null, status: dto.Type === 'Series' ? dto.Status ?? null : null,
        community: typeof dto.CommunityRating === 'number' && Number.isFinite(dto.CommunityRating) ? Math.fround(dto.CommunityRating) : null,
        critic: typeof dto.CriticRating === 'number' && Number.isFinite(dto.CriticRating) ? Math.fround(dto.CriticRating) : null,
        runtime: dto.RunTimeTicks > 0 ? Math.max(1, Math.round(dto.RunTimeTicks / 6e8)) : null,
        width: dto.Width ?? 0, container: dto.Container ?? null, videoType: dto.VideoType ?? null, is3d: dto.Video3DFormat != null,
        sizeGb: source.Size > 0 ? source.Size / (1024 * 1024 * 1024) : null,
        bitrateMbps: source.Bitrate > 0 ? source.Bitrate / 1e6 : null,
        played: ud.Played === true, favorite: ud.IsFavorite === true, inProgress: (ud.PlaybackPositionTicks || 0) > 0,
        playCount: ud.PlayCount || 0, lastPlayed: ms(ud.LastPlayedDate),
        providerIds: dto.ProviderIds || {}, locked: dto.LockData === true
    };
}

const resolution = (w) => (w >= 3800 ? '2160p' : w >= 2500 ? '1440p' : w >= 1900 ? '1080p' : w >= 1200 ? '720p' : w > 0 ? 'SD' : 'Unknown');
const providerOf = (row, provider) => {
    for (const [k, v] of Object.entries(row.providerIds)) if (k.toLowerCase() === String(provider).trim().toLowerCase()) return v;
    return null;
};

// The value a field reads, by its id: text, number, date (ms), list, choice, bool or id.
const READ = {
    Title: (r) => r.name, OriginalTitle: (r) => r.originalTitle, Overview: (r) => r.overview, SeriesName: (r) => r.seriesName, Path: (r) => r.path,
    Year: (r) => r.year, PremiereDate: (r) => r.premiere, DateAdded: (r) => r.added,
    Genres: (r) => r.genres, Tags: (r) => r.tags, Studios: (r) => r.studios,
    OfficialRating: (r) => r.officialRating, SeriesStatus: (r) => r.status,
    CommunityRating: (r) => r.community, CriticRating: (r) => r.critic, Runtime: (r) => r.runtime,
    Resolution: (r) => resolution(r.width), Container: (r) => r.container, VideoType: (r) => r.videoType, Is3D: (r) => r.is3d,
    FileSize: (r) => r.sizeGb, Bitrate: (r) => r.bitrateMbps,
    Played: (r) => r.played, Favorite: (r) => r.favorite, InProgress: (r) => r.inProgress, PlayCount: (r) => r.playCount, LastPlayed: (r) => r.lastPlayed,
    Locked: (r) => r.locked
};
const FLOAT_FIELDS = new Set(['CommunityRating', 'CriticRating']);

function textTest(op, value, comparison) {
    const f = comparison === 'folded' ? fold : upper;
    const v = f(value.trim());
    switch (op) {
        case 'Contains': case 'NotContains': return (t) => f(t).includes(v);
        case 'Eq': case 'Ne': return (t) => f(t) === v;
        case 'StartsWith': return (t) => f(t).startsWith(v);
        case 'EndsWith': return (t) => f(t).endsWith(v);
        case 'IsEmpty': case 'IsNotEmpty': return () => true;
        default: return null;
    }
}

// A rule's test over a row, or null when the oracle doesn't model the field or operator (reported, and a failure).
function compileRule(rule, field, env) {
    const op = rule.Op;
    const kinds = new Set(field.Kinds);
    const scoped = (test) => (r) => kinds.has(r.kind) && test(r);
    const id = field.Id;
    let test = null;
    if (field.Type === 'Text' && id !== 'ProviderId') {
        const comparison = id === 'Path' ? 'ordinal' : 'folded';
        const read = READ[id];
        const positive = textTest(op, op === 'IsEmpty' || op === 'IsNotEmpty' ? '' : rule.Value, comparison);
        if (!positive || !read) return null;
        const second = id === 'Title' && rule.IncludeOriginalTitle === true ? (r) => r.originalTitle : null;
        const check = (t) => !blank(t) && positive(t);
        const pos = (r) => check(read(r)) || (second !== null && check(second(r)));
        test = ['NotContains', 'Ne', 'IsEmpty'].includes(op) ? (r) => !pos(r) : pos;
    } else if (id === 'ProviderId') {
        const value = upper(rule.Value.trim());
        test = (r) => { const v = providerOf(r, rule.Provider); return !blank(v) && upper(v) === value; };
    } else if (id === 'HasProviderId') {
        const has = (r) => !blank(providerOf(r, rule.Provider));
        test = rule.Value ? has : (r) => !has(r);
    } else if (field.Type === 'Bool') {
        const read = READ[id];
        if (!read) return null;
        test = rule.Value ? (r) => read(r) : (r) => !read(r);
    } else if (field.Type === 'Number') {
        const read = READ[id];
        if (!read) return null;
        const n = FLOAT_FIELDS.has(id) ? Math.fround : (x) => x;
        const v = (r) => read(r);
        switch (op) {
            case 'IsEmpty': test = (r) => v(r) == null; break;
            case 'IsNotEmpty': test = (r) => v(r) != null; break;
            case 'Between': { const a = n(rule.From); const b = n(rule.To); test = (r) => v(r) != null && v(r) >= a && v(r) <= b; break; }
            case 'AnyOf': { const set = new Set(rule.Values.map(n)); test = (r) => v(r) != null && set.has(v(r)); break; }
            default: {
                const x = n(rule.Value);
                const cmp = { Eq: (a) => a === x, Lt: (a) => a < x, Le: (a) => a <= x, Gt: (a) => a > x, Ge: (a) => a >= x }[op === 'Ne' ? 'Eq' : op];
                if (!cmp) return null;
                const pos = (r) => v(r) != null && cmp(v(r));
                test = op === 'Ne' ? (r) => !pos(r) : pos;
            }
        }
    } else if (field.Type === 'Date') {
        const read = READ[id];
        if (!read) return null;
        const dayOf = id === 'PremiereDate' ? calendarDay : localDay;
        const compare = (ms, bound) => (isDay(bound) ? (dayOf(ms) < bound ? -1 : dayOf(ms) > bound ? 1 : 0) : Math.sign(ms - Date.parse(bound)));
        switch (op) {
            case 'IsEmpty': test = (r) => read(r) == null; break;
            case 'IsNotEmpty': test = (r) => read(r) != null; break;
            case 'Before': test = (r) => read(r) != null && compare(read(r), rule.Value) < 0; break;
            case 'After': test = (r) => read(r) != null && compare(read(r), rule.Value) > 0; break;
            case 'Between': test = (r) => read(r) != null && compare(read(r), rule.From) >= 0 && compare(read(r), rule.To) <= 0; break;
            case 'InLast': case 'NotInLast': {
                const start = spanStart(rule, env.now);
                const inLast = (r) => read(r) != null && read(r) >= start && read(r) <= env.now;
                test = op === 'InLast' ? inLast : (r) => !inLast(r);
                break;
            }
            default: return null;
        }
    } else if (field.Type === 'List') {
        const read = READ[id];
        if (!read) return null;
        const values = (r) => read(r).filter((x) => !blank(x));
        if (op === 'IsEmpty') test = (r) => values(r).length === 0;
        else if (op === 'IsNotEmpty') test = (r) => values(r).length > 0;
        else {
            const wanted = new Set(rule.Values.map((x) => fold(x.trim())));
            const anyOf = (r) => values(r).some((x) => wanted.has(fold(x)));
            if (op === 'AnyOf') test = anyOf;
            else if (op === 'NoneOf') test = (r) => !anyOf(r);
            else if (op === 'AllOf') test = (r) => { const have = new Set(values(r).map(fold)); return [...wanted].every((w) => have.has(w)); };
            else return null;
        }
    } else if (field.Type === 'Choice') {
        let anyOf;
        if (id === 'Library') {
            const members = new Set(rule.Values.flatMap((v) => [...(env.libraries.get(guid(v)) || [])]));
            anyOf = (r) => members.has(r.id);
        } else if (id === 'ItemType') {
            const set = new Set(rule.Values);
            anyOf = (r) => set.has(r.kind);
        } else if (id === 'Series') {
            const set = new Set(rule.Values.map((v) => guid(v.Id)));
            anyOf = (r) => r.seriesId !== null && set.has(r.seriesId);
        } else if (id === 'OfficialRating' && (op === 'Le' || op === 'Ge')) {
            const limit = scoreOf(rule.Value);
            const cmp = (r) => { const s = scoreOf(r.officialRating); return s && limit ? compareScores(s, limit) : null; };
            return scoped(op === 'Le' ? (r) => { const c = cmp(r); return c !== null && c <= 0; } : (r) => { const c = cmp(r); return c !== null && c >= 0; });
        } else {
            const read = READ[id];
            if (!read) return null;
            const ordinal = id === 'OfficialRating';
            const norm = ordinal ? (x) => x : upper;
            const set = new Set(rule.Values.map((v) => norm(v.trim())));
            anyOf = id === 'Container'
                ? (r) => { const v = read(r); return v != null && v.split(',').map((t) => t.trim()).filter((t) => t).some((t) => set.has(norm(t))); }
                : (r) => { const v = read(r); return v != null && !blank(v) && set.has(norm(v)); };
        }
        if (op === 'AnyOf') test = anyOf;
        else if (op === 'NoneOf') test = (r) => !anyOf(r);
        else return null;
    }
    return test ? scoped(test) : null;
}

function compileNode(node, fields, env, unmodelled) {
    if (node.Children) {
        const children = node.Children.map((c) => compileNode(c, fields, env, unmodelled));
        const group = node.Match === 'Any' ? (r) => children.some((c) => c(r)) : (r) => children.every((c) => c(r));
        return node.Negate ? (r) => !group(r) : group;
    }
    const field = fields.get(node.Field);
    const test = field ? compileRule(node, field, env) : null;
    if (!test) {
        unmodelled.add(`${node.Field} ${node.Op}`);
        return () => false;
    }
    return test;
}

// What /Items shows the user that the query asks for: the scope's kinds and libraries, then the rules.
function expected(query, world, fields, unmodelled) {
    const kinds = new Set(query.Scope.ItemKinds);
    const scopeLibraries = query.Scope.LibraryIds || [];
    const inScope = scopeLibraries.length === 0 ? null : new Set(scopeLibraries.flatMap((l) => [...(world.libraries.get(guid(l)) || [])]));
    const test = compileNode(query.Where, fields, world, unmodelled);
    return world.rows.filter((r) => kinds.has(r.kind) && (inScope === null || inScope.has(r.id)) && test(r)).map((r) => r.id);
}

// ------------------------------------------------------------------------------------------------ the seed

const BLOCKED_TAG = 'ss-blocked';
const runStudio = `SS Fresh Studio ${opts.seed}-${Date.now().toString(36)}`;
const localMidnight = (y, m, d) => new Date(y, m - 1, d).toISOString();

// The synthetic values, given to the items in name order (movies, then the series, episodes and home videos). A
// community rating of 0 is among them: /Items leaves a 0 out of its rows, so the oracle learns which items store one
// from core's own filter (MinCommunityRating 0), which counts it as a rating, as Smart Search does.
const MOVIE_EDITS = [
    { Genres: ['Drama'], Tags: ['ss-keep'], Studios: ['SS Studio'], OfficialRating: 'PG', CommunityRating: 7.1, CriticRating: 89, PremiereDate: '2001-04-25T00:00:00.0000000Z', OriginalTitle: 'Crème Brûlée', Overview: 'A shy waitress changes the lives around her.', ProviderIds: { Imdb: 'tt0211915' } },
    { Genres: ['drama', 'Comedy'], Tags: ['SS-KEEP', BLOCKED_TAG], Studios: ['ss studio'], OfficialRating: 'R', CommunityRating: 7.0999994, PremiereDate: localMidnight(2026, 10, 2), OriginalTitle: 'ÉCOLE', ProviderIds: { Imdb: 'TT0133093' }, LockData: true },
    { Genres: ['Drâma'], Tags: ['Ærø'], Studios: ['Łódź Films'], OfficialRating: 'PG-13', CommunityRating: 7.1000004, CriticRating: 70, PremiereDate: '2026-11-01T23:30:00.0000000Z', OriginalTitle: '100% Pure', Overview: ' ' },
    { Genres: ['Sci|Fi'], Tags: ['a|b'], Studios: ['SS Studio', 'Other Studio'], OfficialRating: 'TV-MA', CommunityRating: 0.1, CriticRating: 0, OriginalTitle: 'Snake_Case', ProviderIds: { Tvdb: '81189' } },
    { Genres: ['Science Fiction', 'Thriller'], Studios: [runStudio], OfficialRating: 'G', CommunityRating: 10, CriticRating: 100, PremiereDate: localMidnight(2025, 12, 31), OriginalTitle: '[Rec]', ProductionYear: 1799 },
    { Genres: ['Comedy'], Tags: ['ss-keep', 'Gore'], OfficialRating: 'NC-17', CommunityRating: 5.5, OriginalTitle: 'Up^Down', ProductionYear: 2101, Overview: 'Keep this one.' },
    { Tags: [BLOCKED_TAG], OfficialRating: 'XYZ', CommunityRating: 8.25, ProviderIds: { Tmdb: '603' }, LockData: true },
    { Genres: ['Horror'], Studios: [runStudio, 'SS Studio'], OfficialRating: 'TV-14', CommunityRating: 0, PremiereDate: '1969-03-26T00:00:00.0000000Z' }
];
const SERIES_EDITS = [{ Genres: ['Animation'], Tags: ['ss-keep'], OfficialRating: 'TV-PG', CommunityRating: 8.1, Status: 'Ended', ProviderIds: { Tvdb: '900401' } }];
const EPISODE_EDITS = [
    { Tags: ['ss-keep', BLOCKED_TAG], OfficialRating: 'TV-MA', CommunityRating: 0, PremiereDate: '2020-01-01T00:00:00.0000000Z' },
    { Studios: ['SS Studio'], CommunityRating: 6.9, PremiereDate: localMidnight(2021, 5, 5) }
];
const VIDEO_EDITS = [
    { Name: 'SS 100% [Mix]_Up^ Ünïcode', OriginalTitle: 'Ünïcode Original', Tags: ['ss-keep'], OfficialRating: 'PG' },
    { Name: 'SS Plain Home Video', Genres: ['Documentary'], LockData: true }
];

async function edit(dto, changes) {
    const item = await sb.ok('admin', 'GET', `/Items/${dto.Id}`, { query: { userId: sb.users.admin.id, Fields: FIELDS_DTO } });
    const next = { ...item };
    for (const [k, v] of Object.entries(changes)) {
        if (k === 'Studios') next.Studios = v.map((name) => ({ Name: name }));
        else if (k === 'Genres') { next.Genres = v; next.GenreItems = v.map((name) => ({ Name: name })); }
        else next[k] = v;
    }
    if (changes.Name) next.LockedFields = [...new Set([...(item.LockedFields || []), 'Name'])];
    const r = await sb.call('admin', 'POST', `/Items/${dto.Id}`, { body: next });
    if (r.status >= 300) throw new Error(`editing ${dto.Name}: HTTP ${r.status}`);
}

async function seedData() {
    await sb.assertNoSavers();
    const all = await sb.items('admin', { Recursive: true, IncludeItemTypes: 'Movie,Series,Episode,Video', IsMissing: false, Fields: 'Path' });
    const byKind = (kind) => all.filter((i) => i.Type === kind).sort((a, b) => (a.Path || a.Name).localeCompare(b.Path || b.Name));
    const plan = [[byKind('Movie'), MOVIE_EDITS], [byKind('Series'), SERIES_EDITS], [byKind('Episode'), EPISODE_EDITS], [byKind('Video'), VIDEO_EDITS]];
    const edited = [];
    for (const [items, edits] of plan) {
        for (let i = 0; i < Math.min(items.length, edits.length); i++) {
            await edit(items[i], edits[i]);
            edited.push(guid(items[i].Id));
        }
    }

    // Watch state: admin and viewer2 each play, favour and half-watch some movies and an episode; viewer has none.
    const movies = byKind('Movie');
    const episodes = byKind('Episode');
    const states = {
        admin: { played: [movies[0], movies[2], episodes[0]], favorite: [movies[1], movies[2]], resume: [movies[3]] },
        viewer2: { played: [movies[3]], favorite: [movies[0], episodes[1]], resume: [movies[1]] }
    };
    for (const [who, s] of Object.entries(states)) {
        const uid = sb.users[who].id;
        for (const item of s.played.filter(Boolean)) await sb.ok(who, 'POST', `/UserPlayedItems/${item.Id}`, { query: { userId: uid } });
        for (const item of s.favorite.filter(Boolean)) await sb.ok(who, 'POST', `/UserFavoriteItems/${item.Id}`, { query: { userId: uid } });
        for (const item of s.resume.filter(Boolean)) await sb.ok(who, 'POST', `/UserItems/${item.Id}/UserData`, { query: { userId: uid }, body: { PlaybackPositionTicks: 600000000, Played: false } });
    }
    return { edited: edited.length, newStudio: runStudio };
}

// ------------------------------------------------------------------------------------------------ the world

async function world(who, libraries, allowedKinds) {
    const rows = (await sb.items(who, { Recursive: true, IncludeItemTypes: allowedKinds.join(','), IsMissing: false, Fields: FIELDS_DTO, EnableImages: false })).map(rowOf);
    // A stored community rating of 0: left out of the rows, but returned by core's filter MinCommunityRating 0.
    const rated = new Set((await sb.items(who, { Recursive: true, IncludeItemTypes: allowedKinds.join(','), IsMissing: false, MinCommunityRating: 0, EnableImages: false, EnableUserData: false })).map((i) => guid(i.Id)));
    for (const row of rows) if (row.community === null && rated.has(row.id)) row.community = 0;
    const members = new Map();
    for (const lib of libraries) {
        const r = await sb.call(who, 'GET', '/Items', { query: { userId: sb.users[who].id, ParentId: lib.id, Recursive: true, IncludeItemTypes: allowedKinds.join(','), IsMissing: false, EnableImages: false, EnableUserData: false } });
        members.set(lib.id, new Set(r.status === 200 ? r.json.Items.map((i) => guid(i.Id)) : []));
    }
    return { rows, libraries: members };
}

// ------------------------------------------------------------------------------------------------ the queries

function vocabulary(rows, libraries) {
    const uniq = (list) => [...new Set(list.filter((x) => !blank(x)))];
    const texts = (f) => uniq(rows.map(f));
    return {
        Title: texts((r) => r.name), OriginalTitle: texts((r) => r.originalTitle), Overview: texts((r) => r.overview), SeriesName: texts((r) => r.seriesName),
        // Only the last two parts of a path: the sandbox's own folders never go into a query or the output.
        Path: texts((r) => (r.path ? r.path.split(/[\\/]/).slice(-2).join('\\') : null)),
        Genres: uniq(rows.flatMap((r) => r.genres)), Tags: uniq(rows.flatMap((r) => r.tags)), Studios: uniq(rows.flatMap((r) => r.studios)),
        OfficialRating: uniq(rows.map((r) => r.officialRating)), Container: uniq(rows.flatMap((r) => (r.container || '').split(','))),
        Year: uniq(rows.map((r) => r.year)), CommunityRating: uniq(rows.map((r) => r.community)), CriticRating: uniq(rows.map((r) => r.critic)),
        Runtime: uniq(rows.map((r) => r.runtime)), FileSize: uniq(rows.map((r) => r.sizeGb)), Bitrate: uniq(rows.map((r) => r.bitrateMbps)),
        PlayCount: [0, 1, 2], PremiereDate: uniq(rows.map((r) => r.premiere)), DateAdded: uniq(rows.map((r) => r.added)), LastPlayed: uniq(rows.map((r) => r.lastPlayed)),
        series: rows.filter((r) => r.kind === 'Series').map((r) => ({ Id: r.id, Name: r.name })),
        providerIds: uniq(rows.flatMap((r) => Object.values(r.providerIds))),
        libraries: libraries.map((l) => l.id)
    };
}

const UNKNOWN_TEXT = ['zzq', 'no such title', 'Ünknown'];
const variant = (s) => pick([(x) => x, (x) => x.toUpperCase(), (x) => x.toLowerCase(), (x) => fold(x), (x) => ` ${x} `])(s);
function textValue(op, values) {
    if (values.length === 0 || chance(0.15)) return pick(UNKNOWN_TEXT);
    const full = pick(values).trim();
    let v;
    if (op === 'Eq' || op === 'Ne') v = full;
    // Short prefixes and suffixes most often: they are what tells StartsWith and EndsWith from Contains.
    else if (op === 'StartsWith') v = full.slice(0, chance(0.7) ? 1 + int(3) : 1 + int(Math.max(1, full.length)));
    else if (op === 'EndsWith') v = full.slice(chance(0.7) ? -(1 + int(3)) : int(Math.max(1, full.length - 1)));
    else { const a = int(Math.max(1, full.length - 1)); v = full.slice(a, a + 1 + int(Math.min(8, full.length - a))); }
    v = v.trim() || full;
    return variant(v).slice(0, 200);
}
const near = (x, step) => pick([x, x, x - step, x + step]);
const clampNum = (x, field) => Math.min(field.Max ?? Number.MAX_SAFE_INTEGER, Math.max(field.Min ?? 0, x));
function numberValue(field, values) {
    const integer = field.Integer === true;
    const step = integer ? 1 : pick([0.5, 1e-6, 0.1]);
    let x = values.length > 0 && !chance(0.1) ? near(pick(values), step) : (field.Min ?? 0) + random() * ((field.Max ?? 100) - (field.Min ?? 0));
    if (field.Id === 'Year') x = Math.min(2100, Math.max(1800, Math.round(x)));
    x = clampNum(integer ? Math.round(x) : Number(x.toFixed(7)), field);
    return x;
}
function dateValue(values) {
    if (values.length === 0 || chance(0.1)) return pick(['2000-01-01', '2026-11-02', '1969-03-26T00:00:00Z']);
    const ms = pick(values) + pick([0, 0, -86400000, 86400000, -1000, 1000]);
    if (chance(0.6)) return pick([localDay(ms), utcDay(ms)]);
    return new Date(ms).toISOString().replace(/\.\d{3}Z$/, 'Z');
}
function listValues(values) {
    const out = some(values.length ? values : ['none'], 3).map((v) => (chance(0.3) ? variant(v) : v));
    if (chance(0.2)) out.push(pick(UNKNOWN_TEXT));
    return [...new Set(out.map((v) => v.trim()).filter((v) => v))];
}

// One rule of a field and operator, with a value from the vocabulary.
function makeRule(field, op, voc, extra = {}) {
    const rule = { Field: field.Id, Op: op, ...extra };
    const shape = op === 'IsEmpty' || op === 'IsNotEmpty' ? 'none' : op === 'Between' ? 'range' : op === 'InLast' || op === 'NotInLast' ? 'relative' : ['AnyOf', 'AllOf', 'NoneOf'].includes(op) ? 'values' : 'value';
    if (field.Options) {
        for (const o of field.Options) {
            if (o.Name === 'Provider') rule.Provider = extra.Provider ?? pick(o.Choices || ['Imdb']);
            if (o.Name === 'IncludeOriginalTitle' && extra.IncludeOriginalTitle === undefined && chance(0.4)) rule.IncludeOriginalTitle = true;
        }
    }
    if (shape === 'none') return rule;
    const id = field.Id;
    if (field.Type === 'Bool') { rule.Value = extra.Value ?? chance(0.5); return rule; }
    if (id === 'ProviderId') {
        const v = voc.providerIds.length && !chance(0.2) ? pick(voc.providerIds) : 'tt0000000';
        rule.Value = chance(0.3) ? v.toUpperCase() : v;
        return rule;
    }
    if (field.Type === 'Text') { rule.Value = textValue(op, voc[id] || []); return rule; }
    if (field.Type === 'Number') {
        const values = voc[id] || [];
        if (shape === 'range') {
            let a = numberValue(field, values);
            let b = numberValue(field, values);
            if (a > b) [a, b] = [b, a];
            if (id === 'Year' && b - a > 200) b = a + 200;
            rule.From = a; rule.To = b;
        } else if (shape === 'values') {
            rule.Values = [...new Set([numberValue(field, values), numberValue(field, values)])];
        } else rule.Value = numberValue(field, values);
        return rule;
    }
    if (field.Type === 'Date') {
        const values = voc[id] || [];
        if (shape === 'relative') { rule.Unit = pick(['Days', 'Weeks', 'Months', 'Years']); rule.Amount = 1 + int(rule.Unit === 'Years' ? 10 : 30); }
        else if (shape === 'range') {
            let a = dateValue(values); let b = dateValue(values);
            const t = (s) => (isDay(s) ? Date.parse(`${s}T00:00:00Z`) : Date.parse(s));
            if (t(a) > t(b)) [a, b] = [b, a];
            rule.From = a; rule.To = b;
        } else rule.Value = dateValue(values);
        return rule;
    }
    if (field.Type === 'List') { rule.Values = listValues(voc[id] || []); return rule; }
    // Choice.
    if (id === 'Library') rule.Values = [...new Set([...some(voc.libraries, 2), ...(chance(0.15) ? ['0123456789abcdef0123456789abcdef'] : [])])];
    else if (id === 'ItemType') rule.Values = some(field.Choices || ['Movie'], 3);
    else if (id === 'Series') rule.Values = voc.series.length ? some(voc.series, 2) : [{ Id: '0123456789abcdef0123456789abcdef', Name: 'Nothing' }];
    else if (id === 'OfficialRating' && (op === 'Le' || op === 'Ge')) rule.Value = pick(['G', 'PG', 'PG-13', 'R', 'NC-17', 'TV-14', 'TV-MA', '13', 'XYZ']);
    else {
        const values = field.Choices && field.Choices.length ? field.Choices : voc[id] || [];
        rule.Values = some(values.length ? values : ['none'], 2);
        if (id === 'OfficialRating' && chance(0.2)) rule.Values.push('pg');
    }
    return rule;
}

function scopeFor(kinds, allowed, voc) {
    const own = kinds.filter((k) => allowed.includes(k));
    const set = new Set(own.length ? own : allowed);
    if (chance(0.3)) set.add(pick(allowed));
    const scope = { ItemKinds: allowed.filter((k) => set.has(k)) };
    if (chance(0.15)) scope.LibraryIds = some(voc.libraries, 2);
    return scope;
}
const SORTS = ['SortName', 'ProductionYear', 'DateCreated', 'CommunityRating', 'DatePlayed', 'PlayCount', 'Random'];
const sortFor = (sortKeys) => (chance(0.3) ? [{ Key: pick(SORTS.filter((s) => sortKeys.includes(s))), Order: pick(['Ascending', 'Descending']) }] : []);

function generate(catalog, voc) {
    const allowed = catalog.ItemKinds;
    const fields = catalog.Fields.filter((f) => f.Available !== false);
    const queries = [];
    // Every field, operator and value form.
    for (const field of fields) {
        for (const op of field.Operators) {
            const forms = field.Type === 'Bool' ? [{ Value: true }, { Value: false }]
                : field.Id === 'Title' ? [{ IncludeOriginalTitle: false }, { IncludeOriginalTitle: true }]
                : field.Id === 'HasProviderId' || field.Id === 'ProviderId' ? (field.Options.find((o) => o.Name === 'Provider').Choices || ['Imdb']).map((p) => ({ Provider: p }))
                : field.Type === 'Text' && !['IsEmpty', 'IsNotEmpty'].includes(op) ? [{}, {}, {}]
                : (field.Type === 'Number' || field.Type === 'Date') && !['IsEmpty', 'IsNotEmpty'].includes(op) ? [{}, {}]
                : [{}];
            for (const form of forms) {
                const extra = { ...form };
                if (field.Type === 'Bool' && field.Id === 'HasProviderId') extra.Value = chance(0.5);
                const rule = makeRule(field, op, voc, extra);
                if (form.IncludeOriginalTitle === false) delete rule.IncludeOriginalTitle;
                queries.push({ Scope: scopeFor(field.Kinds, allowed, voc), Where: { Match: 'All', Children: [rule] }, Sort: sortFor(catalog.SortKeys) });
            }
        }
    }
    // Random combinations.
    for (let i = 0; i < opts.queries; i++) {
        const kinds = some(allowed, 3);
        // Only fields that apply to a kind searched (the validator refuses the others).
        const usable = fields.filter((f) => f.Kinds.some((k) => kinds.includes(k)));
        const anyRule = () => { const field = pick(usable); return makeRule(field, pick(field.Operators), voc); };
        const children = [];
        const n = 1 + int(3);
        for (let j = 0; j < n; j++) {
            const r = random();
            if (r < 0.15) children.push({ Match: 'Any', Children: [anyRule(), anyRule()] });
            else if (r < 0.25) children.push({ Match: pick(['All', 'Any']), Negate: true, Children: [anyRule(), ...(chance(0.5) ? [anyRule()] : [])] });
            else children.push(anyRule());
        }
        const scope = { ItemKinds: allowed.filter((k) => kinds.includes(k)) };
        if (chance(0.2)) scope.LibraryIds = some(voc.libraries, 2);
        queries.push({ Scope: scope, Where: { Match: chance(0.2) ? 'Any' : 'All', Children: children }, Sort: sortFor(catalog.SortKeys) });
    }
    return queries;
}

const usesField = (node, ids) => (node.Children ? node.Children.some((c) => usesField(c, ids)) : ids.has(node.Field));
const rulesOf = (node) => (node.Children ? node.Children.flatMap(rulesOf) : [node]);

// ------------------------------------------------------------------------------------------------ main

async function main() {
    const result = { pass: false, sandbox: sb.name, seed: opts.seed, users: USERS, setup: {}, queries: 0, runs: 0, forcedRuns: 0, paths: {}, expected: { none: 0, some: 0 }, coverage: {}, differences: [], refusals: [], invalid: [], unmodelled: [], notes: [] };
    let failure = null;
    try {
        if (await sb.restoreRestricted()) result.notes.push('a killed run had left viewer2 restricted: its policy was put back first');
        const fieldsAdmin = await sb.ok('admin', 'GET', '/SmartSearch/Fields');
        const catalog = fieldsAdmin;
        const fields = new Map(catalog.Fields.map((f) => [f.Id, f]));
        const adminOnly = new Set(catalog.Fields.filter((f) => f.AdminOnly).map((f) => f.Id));
        ratingScores = new Map((await sb.ok('admin', 'GET', '/Localization/ParentalRatings')).filter((r) => r.RatingScore).map((r) => [r.Name.toUpperCase(), { score: r.RatingScore.score, sub: r.RatingScore.subScore ?? 0 }]));

        if (opts.seedData) result.setup.seed = await seedData();
        result.setup.restricted = await sb.restrict('Movies 2', BLOCKED_TAG, 13);
        if (opts.scan) result.setup.scan = await sb.scan();

        const libraries = await sb.libraries();
        const worlds = {};
        for (const who of USERS) worlds[who] = await world(who, libraries, catalog.ItemKinds);
        result.setup.items = Object.fromEntries(USERS.map((u) => [u, worlds[u].rows.length]));
        const queries = generate(catalog, vocabulary(worlds.admin.rows, libraries));
        result.queries = queries.length;
        const names = new Map(worlds.admin.rows.map((r) => [r.id, r.name]));
        const named = (ids) => ids.map((id) => ({ id, name: names.get(id) ?? null }));
        const coverage = new Map();

        for (let qi = 0; qi < queries.length; qi++) {
            const query = queries[qi];
            for (const rule of rulesOf(query.Where)) {
                const key = rule.Field;
                if (!coverage.has(key)) coverage.set(key, new Set());
                coverage.get(key).add(rule.Op);
            }
            const check = await sb.call('admin', 'POST', '/SmartSearch/Validate', { body: { Query: query } });
            if (check.status !== 200 || !check.json.Valid) {
                result.invalid.push({ index: qi, query, status: check.status, errors: check.json && (check.json.Errors || check.json.errors) });
                continue;
            }
            const path = (check.json.Paths || [])[0] || 'none';
            result.paths[path] = (result.paths[path] || 0) + 1;
            for (const who of USERS) {
                if (who !== 'admin' && usesField(query.Where, adminOnly)) {
                    const r = await sb.call(who, 'POST', '/SmartSearch/Search', { body: { Query: query } });
                    result.runs++;
                    if (r.status !== 403 || (r.json && r.json.code) !== 'AdminOnlyField') result.differences.push({ index: qi, user: who, kind: 'an administrator-only field was not refused', status: r.status, query });
                    continue;
                }
                const unmodelled = new Set();
                worlds[who].now = Date.now();
                const want = new Set(expected(query, worlds[who], fields, unmodelled));
                for (const u of unmodelled) if (!result.unmodelled.includes(u)) result.unmodelled.push(u);
                result.expected[want.size === 0 ? 'none' : 'some']++;
                const runs = [['search', await sb.searchAll(who, query)]];
                if (who === 'admin') runs.push(['forced Residual', await sb.searchAll(who, query, { forceResidual: true })]);
                for (const [label, got] of runs) {
                    result.runs++;
                    if (label !== 'search') result.forcedRuns++;
                    if (got.refused) {
                        result.refusals.push({ index: qi, user: who, run: label, ...got.refused, query });
                        continue;
                    }
                    const have = new Set(got.ids);
                    const missing = [...want].filter((id) => !have.has(id));
                    const extra = [...have].filter((id) => !want.has(id));
                    const twice = got.ids.length !== have.size;
                    const countShort = got.total !== null && got.total < have.size;
                    if (missing.length || extra.length || twice || countShort) {
                        result.differences.push({ index: qi, user: who, run: label, path, missing: named(missing), extra: named(extra), twice, total: got.total, results: have.size, expected: want.size, warnings: got.warnings, query });
                    }
                }
            }
        }
        result.coverage = Object.fromEntries([...coverage].sort().map(([k, v]) => [k, [...v].sort()]));
        const notCovered = catalog.Fields.flatMap((f) => f.Operators.filter((op) => !(coverage.get(f.Id) || new Set()).has(op)).map((op) => `${f.Id} ${op}`));
        if (notCovered.length) result.notes.push({ notCovered });
    } catch (error) {
        failure = String(error && error.stack ? error.stack : error);
    } finally {
        try {
            await sb.restoreRestricted();
        } catch (error) {
            failure = (failure ? failure + '; ' : '') + 'putting viewer2 back: ' + String(error);
        }
    }
    if (failure) result.error = failure;
    result.pass = !failure && result.differences.length === 0 && result.refusals.length === 0 && result.invalid.length === 0 && result.unmodelled.length === 0;
    result.summary = { queries: result.queries, runs: result.runs, forcedRuns: result.forcedRuns, expectedEmpty: result.expected.none, expectedSome: result.expected.some, differences: result.differences.length, refusals: result.refusals.length, invalid: result.invalid.length, unmodelled: result.unmodelled.length };
    finish(result, opts.out);
}

await main();
