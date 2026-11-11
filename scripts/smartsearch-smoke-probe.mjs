#!/usr/bin/env node
// Smart Search's smoke check against a running sandbox, with core's own /Items as the oracle: a query with no rule
// over each library and item kind (and over all libraries) must return exactly the ids
// /Items?ParentId=<library>&Recursive=true&IncludeItemTypes=<kind>&IsMissing=false returns the same user, for admin,
// viewer and viewer2, with a count no larger than /Items' count. It runs three times:
//
//   scan     right after a scan of the libraries, with a library of its own (below) added;
//   refresh  after a full metadata refresh of one seeded movie (core saves the item's row again);
//   add      after a movie whose studio is new is reported added (POST /Library/Media/Updated, as the real-time monitor
//            reports a file): the new movie is found as a movie of its library and by its studio, which has no Studio
//            item yet (no scan ran since).
//
// A seeded movie must be in admin's results each time. Node 22 built-ins only. Loopback only, never the real server's ports. Tokens
// are read from credentials.json and never printed.
//
//   node scripts/smartsearch-smoke-probe.mjs --sandbox <name> [--out result.json]
//
// It writes only under .sandbox/<name>/smartsearch-smoke-probe/: the library "SS Smoke" over its media/ folder (one
// movie at first, a copy like the added one; the same safe options as Initialize-Sandbox.ps1's libraries: no fetchers, no savers, no real-time
// monitor), and the added movie there (a copy of a sandbox movie's video and a movie.nfo naming the studio). At the end
// the library is removed (and the scan that removes its items waited for) and the folder deleted; created.json records
// both first, so the next run after a killed one removes them before it starts.

import fs from 'node:fs';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { argValue, finish, guid, openSandbox, repoRoot, USERS } from './smartsearch-probe-common.mjs';

const CLIENT = 'smartsearch-smoke-probe';
const LIBRARY = 'SS Smoke';
const argv = process.argv.slice(2);
for (const a of argv) {
    if (a.startsWith('--') && !['--sandbox', '--out'].includes(a)) throw new Error(`unknown argument ${a}`);
}
const opts = { sandbox: argValue(argv, '--sandbox'), out: argValue(argv, '--out') };
const sb = openSandbox(opts.sandbox, CLIENT);
const mediaDir = path.join(sb.work, 'media');
const createdFile = path.join(sb.work, 'created.json');

function inside(p, root) {
    const full = path.resolve(p);
    const rel = path.relative(root, full);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel) || /programdata[\\/]jellyfin/i.test(full)) throw new Error('refusing to work outside the probe folder');
    return full;
}

async function removeLibrary() {
    if ((await sb.libraries()).some((l) => l.name === LIBRARY)) {
        await sb.ok('admin', 'DELETE', '/Library/VirtualFolders', { query: { name: LIBRARY, refreshLibrary: false } });
        await sb.scan();
    }
}

// What a killed run left: the library and the folder.
async function cleanUp() {
    if (!fs.existsSync(createdFile)) return false;
    await removeLibrary();
    if (fs.existsSync(mediaDir)) fs.rmSync(inside(mediaDir, sb.work), { recursive: true });
    fs.rmSync(createdFile);
    return true;
}

// A movie folder under the library: a copy of a sandbox movie's video and a movie.nfo naming its studio.
function writeMovie(source, title, studio) {
    const folder = inside(path.join(mediaDir, `${title} (2024)`), sb.work);
    fs.mkdirSync(folder, { recursive: true });
    const file = path.join(folder, `${title} (2024)${path.extname(source)}`);
    fs.copyFileSync(source, file);
    fs.writeFileSync(path.join(folder, 'movie.nfo'), `<?xml version="1.0" encoding="utf-8" standalone="yes"?>\n<movie>\n  <title>${title}</title>\n  <year>2024</year>\n  <studio>${studio}</studio>\n</movie>\n`);
    return file;
}

// The library starts with one movie: a scan skips an empty library folder, and a file reported added under a folder
// the library never read isn't found.
async function addLibrary(source) {
    fs.mkdirSync(mediaDir, { recursive: true });
    fs.writeFileSync(createdFile, JSON.stringify({ library: LIBRARY, folder: 'media' }));
    writeMovie(source, 'SS Smoke Base', 'SS Smoke Base Studio');
    const options = {
        LibraryOptions: {
            Enabled: true, EnableRealtimeMonitor: false, EnableInternetProviders: false, SaveLocalMetadata: false,
            EnableChapterImageExtraction: false, ExtractChapterImagesDuringLibraryScan: false,
            EnableTrickplayImageExtraction: false, ExtractTrickplayImagesDuringLibraryScan: false, EnableEmbeddedTitles: false,
            AutomaticRefreshIntervalDays: 0, MetadataSavers: [], DisabledSubtitleFetchers: [],
            TypeOptions: [{ Type: 'Movie', MetadataFetchers: [], MetadataFetcherOrder: [], ImageFetchers: [], ImageFetcherOrder: [] }]
        }
    };
    await sb.ok('admin', 'POST', '/Library/VirtualFolders', { query: { name: LIBRARY, collectionType: 'movies', paths: mediaDir, refreshLibrary: false }, body: options });
}

// One stage: every library and kind, and every kind over all libraries, for each user, against /Items.
async function stage(name, kinds, extraChecks) {
    const libraries = await sb.libraries();
    const scopes = [...libraries.map((l) => ({ library: l, LibraryIds: [l.id] })), { library: null, LibraryIds: [] }];
    const row = { stage: name, queries: 0, runs: 0, differences: [], refusals: [] };
    const results = {};
    for (const who of USERS) {
        for (const scope of scopes) {
            for (const kind of kinds) {
                const query = { Scope: { ItemKinds: [kind], LibraryIds: scope.LibraryIds }, Where: { Match: 'All', Children: [] } };
                const where = { kind, library: scope.library ? scope.library.name : 'all' };
                if (who === 'admin') row.queries++;
                row.runs++;
                const params = { userId: sb.users[who].id, Recursive: true, IncludeItemTypes: kind, IsMissing: false, EnableImages: false, EnableUserData: false, Limit: 100000 };
                if (scope.library) params.ParentId = scope.library.id;
                const core = await sb.call(who, 'GET', '/Items', { query: params });
                // A library the user can't open: /Items refuses it or answers nothing, and so must the search.
                const want = core.status === 200 ? core.json.Items.map((i) => guid(i.Id)) : [];
                const wantCount = core.status === 200 ? core.json.TotalRecordCount : 0;
                const got = await sb.searchAll(who, query);
                if (got.refused) {
                    row.refusals.push({ user: who, ...where, ...got.refused });
                    continue;
                }
                results[`${who}|${where.library}|${kind}`] = got.ids;
                const have = new Set(got.ids);
                const missing = want.filter((id) => !have.has(id));
                const extra = got.ids.filter((id) => !want.includes(id));
                if (missing.length || extra.length || have.size !== got.ids.length || (got.total ?? 0) > wantCount) {
                    row.differences.push({ user: who, ...where, missing, extra, twice: have.size !== got.ids.length, total: got.total, itemsTotal: wantCount });
                }
            }
        }
    }
    Object.assign(row, await extraChecks(results));
    row.pass = row.differences.length === 0 && row.refusals.length === 0 && Object.entries(row).every(([k, v]) => !k.startsWith('has') || v === true);
    return row;
}

async function main() {
    const result = { pass: false, sandbox: sb.name, stages: [], notes: [] };
    let failure = null;
    try {
        if (await cleanUp()) result.notes.push('a killed run had left its library or folder: removed first');
        const kinds = (await sb.ok('admin', 'GET', '/SmartSearch/Fields')).ItemKinds;

        // The seeded movie: the first movie of the library Movies.
        const movies = (await sb.libraries()).find((l) => l.name === 'Movies');
        if (!movies) throw new Error('no library Movies; run Initialize-Sandbox.ps1 first');
        const seeded = (await sb.items('admin', { ParentId: movies.id, Recursive: true, IncludeItemTypes: 'Movie', Fields: 'Path', SortBy: 'SortName' }))[0];
        if (!seeded) throw new Error('the library Movies has no movie');
        const seededId = guid(seeded.Id);
        const source = seeded.Path;
        if (!source || !fs.existsSync(source) || !inside(source, path.join(repoRoot, '.sandbox', 'media'))) throw new Error('the seeded movie has no video file in the sandbox media');

        await addLibrary(source);
        result.scan = await sb.scan();
        result.seededMovie = { id: seededId, name: seeded.Name };
        const hasSeeded = (r) => (r['admin|Movies|Movie'] || []).includes(seededId) && (r['admin|all|Movie'] || []).includes(seededId);

        result.stages.push(await stage('scan', kinds, async (r) => ({ hasSeededMovie: hasSeeded(r) })));

        await sb.ok('admin', 'POST', `/Items/${seeded.Id}/Refresh`, { query: { metadataRefreshMode: 'FullRefresh', imageRefreshMode: 'None', replaceAllMetadata: false, replaceAllImages: false } });
        await delay(10000);
        result.stages.push(await stage('refresh', kinds, async (r) => ({ hasSeededMovie: hasSeeded(r) })));

        // A movie of a new studio, reported added. Its video is a copy of the seeded movie's.
        const stamp = Date.now().toString(36);
        const title = `SS Smoke ${stamp}`;
        const studio = `SS Smoke Studio ${stamp}`;
        const file = writeMovie(source, title, studio);
        await sb.ok('admin', 'POST', '/Library/Media/Updated', { body: { Updates: [{ Path: file, UpdateType: 'Created' }] } });
        let added = null;
        for (let i = 0; i < 60 && !added; i++) {
            await delay(5000);
            added = (await sb.items('admin', { Recursive: true, IncludeItemTypes: 'Movie', SearchTerm: title, Fields: 'Studios' })).find((x) => x.Name === title) || null;
        }
        if (!added) throw new Error('the added movie never appeared');
        const addedId = guid(added.Id);
        result.added = { id: addedId, name: title, studioRead: (added.Studios || []).some((s) => s.Name === studio) };
        result.stages.push(await stage('add', kinds, async (r) => {
            const byStudio = await sb.searchAll('admin', { Scope: { ItemKinds: ['Movie'] }, Where: { Match: 'All', Children: [{ Field: 'Studios', Op: 'AnyOf', Values: [studio] }] } });
            return {
                hasSeededMovie: hasSeeded(r),
                hasAddedMovie: (r[`admin|${LIBRARY}|Movie`] || []).includes(addedId) && (r['admin|all|Movie'] || []).includes(addedId),
                hasAddedMovieByItsNewStudio: !byStudio.refused && byStudio.ids.length === 1 && byStudio.ids[0] === addedId
            };
        }));
    } catch (error) {
        failure = String(error && error.stack ? error.stack : error);
    } finally {
        try {
            await removeLibrary();
            if (fs.existsSync(mediaDir)) fs.rmSync(inside(mediaDir, sb.work), { recursive: true });
            if (fs.existsSync(createdFile)) fs.rmSync(createdFile);
        } catch (error) {
            failure = (failure ? failure + '; ' : '') + 'clean-up: ' + String(error);
        }
    }
    if (failure) result.error = failure;
    result.pass = !failure && result.stages.length === 3 && result.stages.every((s) => s.pass);
    finish(result, opts.out);
}

await main();
