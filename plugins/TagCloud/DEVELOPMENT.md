# Tag Cloud

A Jellyfin plugin that draws the tags, genres, actors, directors, writers or studios of a server's movies and shows as
a word cloud, counted for each viewer over the titles they can see. This is its technical documentation; the user guide
is [README.md](README.md), the release notes are the `changelog` in [build.yaml](build.yaml).

Version **1.0.1**, for Jellyfin **10.11.5** (`net9.0`). Not in 1.0.1: export and import of the saved clouds.

**What it changes on a server**: one script tag added to the web client's `index.html` as the server sends it (nothing
is written to the web client's files, and the tag goes with the plugin or its switch), its settings file
`plugins/configurations/Jellyfin.Plugin.TagCloud.xml`, and its data folder `data/tagcloud` (the saved clouds and their
snapshots). The index lives in memory only. It reads the libraries and the server's database and writes to neither.

## Layout

| Folder | What it holds |
| --- | --- |
| `Index/` | The index: `TitleReader` (titles with their tags, genres and studios), `PeopleReader` (credits), `IndexState` and `TagCloudIndexSnapshot` (built, then immutable), `TagCloudIndex` (the current snapshot, patches and rebuild requests), `TagCloudIndexWorker` (the builds) and `TagCloudLibraryConsumer` (library changes). |
| `Services/` | `TagCloudQueryService` (counts for one user), `TagCloudCloudService` (saved clouds), `TagCloudAdminService` (status, rebuild). |
| `Clouds/` | The saved clouds' store: `SavedCloudDocument` (schema 1), `SavedCloudRecord`, `CloudLook` (the look, normalised) and `SavedCloudOwner` (the kit's store and users-service owner). |
| `Api/` | The controllers and their DTOs. |
| `Library/`, `Tasks/` | The post-scan task that flushes library changes, and the daily maintenance. |
| `Web/` | The web client: `js/` (API, layout, packer, look, renderer, settings panel, page), `css/`, and `anchors.json` (empty: the page uses no jellyfin-web element but the kit's page host and nav link). |
| `Configuration/` | The settings and the dashboard page. |

## How it counts

**The index.** Built in full once the server has started (the kit's startup gate), every day by "Tag Cloud:
Maintenance" (03:40 by default; its trigger is changed under Scheduled Tasks), after `FullRebuildAfterChanges` changed
titles (500 by default), on an admin's **Count the library again**, and when a source is turned on. A full build reads:

- the titles: one `GetItemList` of every movie and series without a user (server-wide work), whose tags, genres and
  studios are columns of their rows;
- the credits: one grouped, read-only read of `PeopleBaseItemMap` joined to `Peoples` through the server's
  `IDbContextFactory` (the 10.11 schema), or, when that can't run (no factory, a schema change, a lock), the library's
  `GetPeople(item)` for each title, slower but on the public API only. Status and Diagnostics say which path ran
  (`PeoplePath` `Database` or `PerItem`) and how long it took.

Between full builds the kit's library-change monitor batches Jellyfin's item events (a 60 s quiet period) and the
post-scan task flushes it inside every scan; each changed title is read again with its credits and patched in. A
removed title keeps its ordinal as a gap until the next full build, so ordinals never move under a cached mask.

**Words.** Keyed by their trimmed text in lower case ("Horror" and "horror" are one word), shown in the casing most of
their titles use; studios by name. TV counts series, never episodes. Actors, directors and writers are the people types
`Actor`, `Director` and `Writer`.

**Each request.** The caller's titles come from the kit's `LibraryAccess.CreateQuery` (`new InternalItemsQuery(user)`
with the libraries asked for, intersected with the user's) and core's `GetItemList`, which applies library access,
parental ratings and blocked tags in SQL and each item's own `IsVisible(user)` after. They are kept as a mask over the
snapshot's ordinals, per user, scope, libraries and index generation, so the list is read once per change. A cloud is
the index's words counted over that mask, heaviest first; narrowing intersects the titles of each narrowing word first.
A narrowing word the caller's titles don't carry is ignored, so it can't tell a word of hidden titles from an unknown
one.

## The web client

`Web/js` is built by the kit into one script (`GET /TagCloud/ClientScript`) that the `index.html` tag loads. The page
`#/jftc-cloud` is registered with the kit's page host (its own route, or Plugin Pages by `PageHostMode`) and linked
from **Library tools**. What the cloud counts is in the hash (`source`, `scope`, `libs`, `narrow`, `sel`, `cloud`);
`show=1` opens the full-screen view, an overlay on `document.body` built by the same page, so a link
(`#/jftc-cloud?cloud=<id>&show=1`) opens a saved cloud full screen and a click in the page's own turn lets the browser go
full screen.

- `30-layout.js`: which words to show, their sizes, shades and order (pure; a stable FNV-1a hash orders equal weights).
- `35-pack.js`: the packed layout, which fits each word against the shapes of the letters already placed, with size
  mixing.
- `38-look.js`, `50-settings.js`: the look's choices and defaults (the server holds a saved look to the same ranges in
  `CloudLook`) and the settings panel. The look is kept per device in `localStorage`.
- `40-cloud.js`: the renderer (Cloud as SVG, Rows as markup), click, hold (450 ms), right-click and keyboard.
- `60-page-cloud.js`: the toolbar, saved clouds, the summary and narrowing chips, the titles card, the held word's cloud
  and the full-screen view.

## Settings

Edited on the dashboard page (`configurationpage?name=TagCloud`): `EnableWebUi`, `EnableTagCloudPage`,
`ShowFallbackNav`, `PageHostMode` (`FallbackOnly`, `Auto`, `PluginPagesOnly`), `RegisterPluginPages`,
`ReportWebAnchorProblems`; the six sources (`EnableTags` … `EnableWriters`; one turned off is neither offered nor
indexed); `MaxWordsLimit` (100 to 2,000, steps of 25), `TvMaxWordsDefault` (150), `AllowSharedClouds`;
`FullRebuildAfterChanges` (500) and `SnapshotsToKeep` (7).

## Saved clouds

`data/tagcloud/clouds.json` (schema 1) through the kit's JSON store: a backup copy, a newer schema refused rather than
overwritten, daily snapshots in `snapshots/` and one before each of the kit's operations. A cloud keeps its name (at
most 80 characters, unique per owner), sharing, what it counts and its look, never counts. A user keeps at most 200.
Saves carry `IfUpdatedUtc`, so a save over a newer one is 409. When a user is deleted (the kit's users service,
`DeletedUserPolicy.KeepAsOrphan`), their private clouds go and their shared ones stay as "Former user" until an admin
deletes them.

## API

Every route but `ClientScript` needs a signed-in user; API keys get no clouds (there is no user to count for). While
`EnableTagCloudPage` is off, `Libraries`, `Words`, `Titles` and every `Clouds` route answer 403 `feature-disabled`
("The Tag Cloud is turned off by your server admin."); `Capabilities`, `Pages`, `ClientHealth` and the admin routes
still answer.

| Method | Route | Answer |
| --- | --- | --- |
| GET | `/TagCloud/Capabilities` | The sources turned on, the words slider's range, the TV layout's starting count, whether sharing is allowed, `IsAdmin`. |
| GET | `/TagCloud/Libraries?scope=` | The caller's libraries holding titles in the scope, by name, with their title counts. 503 during the first build. |
| GET | `/TagCloud/Words?source=&scope=&libraryIds=&narrow=&limit=` | The words, heaviest first, with the totals. `narrow` repeated, at most 10; `limit` 25 to `MaxWordsLimit`. 403 `feature-disabled` for a source turned off; 503 while building. |
| GET | `/TagCloud/Titles?source=&key=&scope=&libraryIds=&narrow=&order=&seed=&startIndex=&limit=` | A page of one word's titles: `Name` (A–Z) or `Random` (fixed by `seed`); `limit` 1 to 200. A word the caller's titles don't carry gives an empty page. |
| GET, POST | `/TagCloud/Clouds` | The caller's clouds, then the shared ones by name; a new cloud (400 name or limits, 403 a shared one while sharing is off, 409 name taken). |
| GET, PUT, DELETE | `/TagCloud/Clouds/{id}` | One cloud (404 if the caller may not open it); a save (403 another's, or turning sharing on while it is off: a cloud already shared may be saved as it is or stop being shared; 409 newer or name taken); a delete (the owner's, or an admin's of a shared one). |
| POST | `/TagCloud/Clouds/{id}/Duplicate` | A private copy, "… (copy)". |
| GET | `/TagCloud/Pages/{key}` | The page's shell for Plugin Pages' host. |
| POST | `/TagCloud/Diagnostics/ClientHealth` | The kit's anchor report; 204, 403 for an API key, 413 over 16 KB. |
| GET | `/TagCloud/ClientScript` | Anonymous: the web client's script, ETag the build's (304 when current). |
| GET | `/TagCloud/Admin/Diagnostics`, `/TagCloud/Admin/Status` | Administrators: the kit's Diagnostics with the store and the index; the index's titles, words per source, generation, `PeoplePath` and build times. |
| POST | `/TagCloud/Admin/Rebuild` | Administrators: asks for a full build in the background; poll Status. |

The admin Status and Diagnostics totals are server-wide, also for an administrator whose account can't open every
library (the README says so).

## Installing, upgrading and going back

The user README has the short version. The plugin folder is `Tag Cloud_<version>`; 1.0.1 upgrades 1.0.0 in place and
keeps its data and settings. From a clone, `scripts/Install-RealServer.ps1` installs a release tag with a cold backup
of its own, first as a dry run:

```powershell
<repo>\scripts\Install-RealServer.ps1 -Plugin TagCloud -Version 1.0.1 -WhatIf
<repo>\scripts\Install-RealServer.ps1 -Plugin TagCloud -Version 1.0.1
```

It refuses while a `jellyfin.exe` runs, and its `RESTORE.txt` gives the ways back: way A removes Tag Cloud alone (its
plugin folder, `data\tagcloud` and its settings file), way B puts the whole server back. More in
[scripts/README.md](../../scripts/README.md#release-and-install-tooling). By hand: stop Jellyfin, replace or delete `plugins\Tag Cloud_<version>`,
and start it again.

## Building and testing

From the repository root:

```powershell
dotnet build plugins/TagCloud/Jellyfin.Plugin.TagCloud
dotnet test plugins/TagCloud/tests/Jellyfin.Plugin.TagCloud.Tests
```

| Area | Tests |
| --- | --- |
| Kit conventions | `Api/ApiConventionTests` (schema prefix, controller names and routes, the one anonymous action), `Api/PublicBoundaryTests`, `Storage/SavedCloudStoreConformanceTests` (the kit's store suite). |
| The index | `Index/IndexTests`: what it holds, keys and casing, a full build and a patch, on the kit's fake library. |
| Counts | `Services/QueryServiceTests`: only the user's titles and libraries, narrowing, a word's titles. |
| Saved clouds | `Services/CloudServiceTests`: owners, sharing, names, conflicts, deletes, a deleted user's clouds. |
| The web client | `Web/ClientScriptTests` (the built script, embedded files) and `Web/tagcloud.test.mjs` (layout, packer, look and the page in the kit's fake page), which it runs with node; `Web/SettingsTests` (every setting on the page, the dashboard module). |

In a sandbox ([scripts/README.md](../../scripts/README.md#tag-cloud-checks), "Tag Cloud checks"):

```powershell
./scripts/dev-server.ps1 -Name tc -Port <port> -Plugins TagCloud -Community None -Background
./scripts/Initialize-Sandbox.ps1 -Name tc
node scripts/tagcloud-probe.mjs --sandbox tc
node scripts/webcheck.mjs --sandbox tc --steps tagcloud-page.json --fail-on-exception
./scripts/Stop-Sandbox.ps1 -Name tc
```

`tagcloud-probe.mjs` seeds the sandbox and checks every count against core's own `/Users/{id}/Items` for three users,
the people path, a patch, and the saved clouds' rules; `tagcloud-page.json` checks the page in a browser. Tested on
Windows with Jellyfin 10.11.5 only so far. Not measured yet: the first build's time on a library of 10,000 movies, and
the cloud and full-screen view on a real TV.
