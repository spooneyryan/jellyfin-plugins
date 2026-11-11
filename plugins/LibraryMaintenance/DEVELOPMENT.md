# Library Maintenance

A Jellyfin plugin with an admin-only Dashboard page that lists the movies your libraries hold more than once, and a
**preview** of moving an item's media to another place. This is its technical documentation; the user guide is
[README.md](README.md), the release history [CHANGELOG.md](CHANGELOG.md).

Version **1.0.4** (release tag `librarymaintenance/v1.0.4`), for Jellyfin **10.11.5** (`net9.0`).

- **Duplicates** (since 1.0.0): the Dashboard page's Duplicates tab groups the movies by title (and year), with a
  confidence, a suggested copy to keep and the space the others take. Read-only.
- **Move media, preview only** (since 1.0.2): an administrator's item menu has **Move media to new location**, whose
  dialog plans a move and shows what would happen. **Nothing is moved**: the release is built plan-only
  (`MovePreviewOnly`), with no route or call that moves, renames, makes or deletes a file or folder. Moving itself is
  planned for 1.1 and is not in this code ("Move media: the preview").
- Not in 1.0.3: marking a group as not a duplicate, comparing copies, CSV, merging versions, the Repair view, deleting a
  copy, and moving.

**What it changes on a server**: one script tag added to the web client's `index.html` as the server sends it (for the
item menu's entry; nothing is written to the web client's files, and the tag goes with the plugin or its switch), and its
settings file, `plugins\configurations\Jellyfin.Plugin.LibraryMaintenance.xml`. The movie index and a preview's plan
live in memory only; there is no data folder. It reads the libraries and writes nothing to them.

## The Dashboard page

Dashboard → Plugins → Library Maintenance, or the drawer's link: `#/configurationpage?name=LibraryMaintenance`, with
`&tab=Settings` or `&tab=Diagnostics` to open another tab first (read once). The page uses the kit's AdminScript
pattern: the HTML holds no data and no script; its `data-controller` module `LibraryMaintenanceJs` is a stub that imports
`GET /LibraryMaintenance/AdminScript` (the kit's `kit.js` with `Web/admin/js` and `Web/admin/css`, built by the kit's
`EmbeddedScript`) and mounts it. Tabs load their data on first open; every server value is written as text. It follows
the dashboard theme (each chip, badge and the initials 4.5:1 or more in Dark, Light, Apple TV and Purple Haze), and on
the TV layout the remote's Down from a tab reaches its panel.

### The Duplicates tab

- **The index's status line** ("Index: 19 movies in 2 libraries · built 14:02 (0.2 s) · 17 entries"), the libraries it
  read (counting every version and a movie in each library that holds its folder, so higher than Jellyfin's own counts,
  which the tab says) and the entries with broken version links. **Rebuild** starts a build; while one runs the tab
  says "Updating…" and asks again every 2 s, through passing errors, until a refusal. Rebuild stays focusable while off
  (`aria-disabled`). A build started during a scan says its results may be incomplete; a failed build keeps the index
  before it and says why; an out-of-date index is said, and during a scan the tab waits for the scan's build.
- **The groups** (`GET …/Duplicates/Groups`): the match mode (**Title + Year** or **Title only**), in Title + Year how a
  movie with no year groups, the libraries (two or more), the confidence, a title filter and the order (confidence then
  title, title, reclaimable space, group size, newest added). The choices but the filter are kept in the browser
  (`localStorage` `jflm:duplicates.view` through `kit.storage`; refused or outdated values fall back to the server's
  settings). A line counts the groups and their reclaimable space ("463 groups · 1.2 TB reclaimable").
- **A group's card**: title and years, confidence, the reasons and flags as chips, the copies and reclaimable space ("≥"
  for a lower bound), then every copy, the suggested keep first and badged: title and edition, year, libraries,
  resolution, size, bitrate, runtime, container, disc type, versions, date added, TMDb and IMDb ids, path, flags, and
  **Open** (the details page). Posters come from `Items/{id}/Images/Primary` through `ApiClient.getUrl` (so under a base
  URL too), 60 × 90 px at the screen's pixel ratio, lazy, `alt=""`, with the movie's initials until (or instead of) the
  image. 50 groups at a time, **Load more** adding 50 and moving the focus to the first new card.
- **States**: "Indexing movies… 40%" during the first build; "Index error:" with **Retry**; no groups says where it looked
  and offers **Try Title only (N groups)** when that finds some; filters that keep none offer **Clear the filters**; no
  library left that the administrator can open says so. Cards are redrawn only when what they show changes, so the 2 s
  poll never takes the focus.

### The Settings tab

The server's settings for every administrator (`GET …/Admin/Settings`).

| Setting (key) | Default | What it does |
| --- | --- | --- |
| *Find duplicate movies* (`DuplicatesEnabled`) | on | Off hides the Duplicates tab; every Duplicates route answers 404 "Duplicates is turned off in Library Maintenance's settings.". |
| *Default match mode* (`DefaultMatchMode`) | `TitleAndYear` | Or `Title`; the mode the tab opens in until an administrator picks one there. |
| *A movie with no year matches* (`UnknownYearPolicy`) | `MatchUnknownOnly` | Or `MatchAny` (with the title's one year). |
| *Libraries* (`IncludedLibraryIds`) | none: every movie and mixed library, also later ones | Toggles per library; a library turned off in Jellyfin says so; an included library no longer on the server stays listed until turned off. |
| *Excluded folders* (`ExcludedPathPrefixes`) | none | One full folder path on the server a line; movies under it are left out. A device or NT path (`\\?\…`, `\\.\…`, `\??\…`, a `?` as the second character) is refused, and one already saved is never opened. |
| *Cut edition suffixes* (`StripEditionSuffixes`), *Edition phrases* (`EditionPhrases`) | on; the default list | Rule N4; **Default phrases** restores the list. |
| *Leading articles dropped* (`ArticleLanguages`) | `En` | Rule N7b's six languages, with its warning. |
| *Build the index after every library scan* (`WarmIndexAfterScan`) | on | See "The movie index". |
| *Wait for a build* (`RequestWaitSeconds`) | 5 | 0 to 30. |
| *Add Library Maintenance to the web client* (`EnableWebUi`) | on | Off takes the script out of `index.html` (Duplicates keeps working). |
| *Move media* (`MoveEnabled`) | on | Off hides the entry and answers 403 on Move's routes. |
| *Move in the item menu* (`ShowInItemMenu`) | on | Off hides the entry only. |
| *Report missing web client elements* (`ReportWebAnchorProblems`) | on | Browsers' reports of web client parts the script couldn't find. |

The other Move settings in `PluginConfiguration` are not on the page yet (they wait for a Moves tab);
`AllowCrossLibraryMoves` and `LooseFileFolderMode` already shape the preview. **To turn the entry or the script off,
use the switches above, not Disable on Jellyfin's Plugins page**: on 10.11.5 a disabled plugin is deleted at the next
start (its `DiscoverPlugins`; fixed upstream only in Jellyfin's master, a272efb9a2). After a change, reload the web
client (`Ctrl+F5`).

- Switches have the `switch` role and toggles `aria-pressed`, so a remote's OK presses them. A value a newer version
  saved is shown as such and saved back unchanged.
- **Save** is a dry run first (`dryRun=true`) with the `Version` the tab read. A change to the libraries or excluded
  folders opens a dialog with the change, the movies the index holds now and would hold, each library and folder; it is
  saved only from **Save and rebuild the index**. Any other change saves at once. A limited administrator (some
  libraries, a parental rating or tags) gets the dialog without counts.
- Settings saved since the tab read them are never overwritten: 409, and the tab offers **Reload the settings**.
- Jellyfin's own `POST /Plugins/{id}/Configuration` saves the whole configuration with no dry run or version check; the
  plugin can't gate it (accepted for 1.0). Change the settings on this tab.

**Export and import** (the foot of the tab). **Export the settings** downloads
`librarymaintenance-export-<yyyyMMdd-HHmmss>.json`, the kit's envelope (`jellyfin-plugin-export/1`) with the settings in
`Settings` and an empty `Data`. It holds the excluded folders' paths: keep it where only administrators can read it.
**Import from a file…** is a dry run first; a dialog says where the file came from, what changes (with the index
preview) and what was left out or kept (unknown settings, libraries not on this server, folders that aren't full paths
here, another server's file); **Import** then saves. Unlike the kit's import, this one applies the envelope's settings.
Refused: another plugin's file, a newer schema, no settings, over 1 MB. A 1.0.0 or 1.0.1 export has no web client and
Move switches: an import keeps them as saved.

### The Diagnostics tab

First the index's numbers for the libraries the administrator can open (`GET …/Duplicates/Index`): movies, libraries,
entries, those with several versions or a broken link, each version state, the movies skipped for having no name and
those left out by the excluded folders, when the index was built and in how long. Then
`GET /LibraryMaintenance/Admin/Diagnostics`:

- plugin and kit versions, the Jellyfin line (`Abi`), and `ClientHealth` (browsers' reports of missing web client
  parts);
- `DuplicatesIndex`: the last build's counts and cost (milliseconds, queries, MB allocated, the working set), `States`,
  whether it is out of date, why the last build ran, builds by reason, builds a scan put off, when the page was last
  used;
- `LibraryChanges`: the kit's monitor and Duplicates' consumer (waiting changes, batches, full passes);
- `DuplicateGroups`: the groups' cache (results kept, hits, misses), the title keys' projections, the last 16 groupings.

`DuplicatesIndex` and `DuplicateGroups` cover every library the index read, so an administrator limited to some
libraries or by a parental rating or tags sees one line saying they are hidden; `LibraryChanges` stays for them
(counts only, no id, name or path; accepted for 1.0).

## The movie index

- **In memory only**, one per server. It reads every movie in the enabled `movies` and mixed libraries (the included
  ones when any are named; a library turned off in Jellyfin's library options is left out, as Jellyfin's
  `CollectionFolder.IsVisible` does), each library by its own folders as `TopParentIds` and never with `HasOwnerId` (on
  10.11.5 that filter drops every movie saved by 10.11), 500 items at a time with no images and no user data. Owned
  items (local alternates, extras) and movies under the excluded folders are left out. Versions Jellyfin groups become
  one entry (the kit's `VersionSets.Group`), with their broken links counted. About 15 MB at 10,000 movies with
  synthetic metadata, about 18 MB with realistic titles, paths and ids.
- **Freshness.** The kit's library-change monitor sends Duplicates the changes to movies and videos once they have been
  quiet for 60 s (10 min after the first at the latest, never during a scan), and every scan's full pass comes from the
  post-scan task (`LibraryMaintenancePostScanTask`), which runs inside the scan once it has read the libraries. Each
  marks the index out of date (`Dirty`) and rebuilds it whole in the background when the page was used in the last 24
  hours (`GET /Index`, Rebuild) or `WarmIndexAfterScan` is on; otherwise the next read builds it.
- **Builds are single-flight**: a change during a build asks for one more after it. During a scan an existing index is
  rebuilt only by the post-scan build (once, inside the scan, not marked "built during a scan"); whatever it didn't
  cover is built when the scan ends (checked every 5 s). With no index at all, the first build runs during a scan anyway
  and says its results may be incomplete.
- **Reads** wait up to `RequestWaitSeconds` for a build, then answer from the previous index marked stale, or 202 with
  none. A build that fails with no index before it makes the reads answer 503 with the reason (no paths), their own
  builds back off for 30 s, and Rebuild retries at once. Each build's log line and Diagnostics say why it ran.
- **Settings**: while Duplicates is off, changes and scans still mark the index out of date (nothing is built), so turning
  it on never serves an old index as current. A save that turns it on or changes what the index reads marks it out of
  date and builds it. A change to the title rules or the default mode builds nothing: results are keyed by both, and a
  title-rule change drops the cached title keys and results.

## How titles are compared

Two movies match when their titles give the same key (and, in Title + Year mode, the same year). The key is the title
with these rules applied, in order; they never strip a year that isn't in brackets, so *Blade Runner 2049*, *Death Race
2000* and *1917* keep theirs.

| Rule | What it does | Example |
|---|---|---|
| N1 | Invisible characters removed first (zero-width spaces and joiners, the soft hyphen, direction marks, variation selectors); lower case; accents on Latin letters and the Greek tonos dropped; ß æ œ ø ł đ, the dotless ı, the final ς and Cyrillic ё folded; full-width forms made plain. Other marks stay (Cyrillic й, Japanese が). | *Amélie* = *Amelie*; *Ёлки* = *Елки*; `Ali` + U+200B + `ens` = *Aliens* |
| N2 | Path tags `[tmdbid-…]`, `[imdbid-…]`, `[tvdbid-…]`, `{tmdb-…}`, `{imdb-…}` removed; `{edition-…}` kept as the edition | *Movie (2020) [tmdbid-603]* = *Movie (2020)* |
| N3 | One trailing year in brackets removed (and kept as a year for movies with none) | *Test Pattern (2020)* = *Test Pattern* |
| N4 | An edition after ` - `, `: ` or in trailing brackets, **only when the whole part is an edition phrase** | *Blade Runner - The Final Cut* = *Blade Runner*; *Mission: Impossible - Fallout* and *Apocalypse Now Redux* stay whole |
| N5 | Dotted acronyms joined | *L.A. Confidential* = *LA Confidential* |
| N6 | A lone `&` is "and"; apostrophes removed; other punctuation is a space | *Schindler's List* = *Schindlers List*; *Romeo + Juliet* ≠ *Romeo & Juliet* |
| N7 | One leading English article (or the trailing ", The"), never the last word | *The Matrix* = *Matrix, The* = *Matrix* |

- **N7b** (French, German, Spanish, Italian and Dutch articles) is *Leading articles dropped*, English only by default.
  It drops a first word that is an article in a chosen language even when it belongs to the title: *La La Land* becomes
  *La Land* (French or Spanish) and *Les Misérables* meets *Misérables*.
- Off in 1.0.x settings: **N8** (roman numerals II to XX: *Rocky II* = *Rocky 2*, but also *Malcolm X* = *Malcolm 10*),
  **N9** (spaces ignored: *Spider-Man* = *Spiderman*) and **N10** (release names such as
  `curse.of.chucky.2013.unrated.1080p`, for movies with no TMDb or IMDb id: cut at the first release token, then a
  trailing year with the edition phrases after it; phrases before the year are the title's, so `The.Final.Cut.2004.1080p`
  is *Final Cut*; the cut never leaves only an article, nor one word when it takes phrases).
- The rules are version 1 (`NormalizerVersion`, in the grouping options' hash). From 1.0 on, any change to the keys (a
  rule, a default, the edition phrases, N10's window) bumps it, and the release notes say so.

## How duplicates are grouped

`DuplicateGrouper` is a pure function of the index's entries (a version set is one entry) in the libraries in scope,
**each as the caller sees it**: only its versions in those libraries; an entry with none there is left out, and nothing
about versions outside shows, not even that there are some. A movie the caller's parental rating or tags hide (blocked
or allowed tags, unrated items) is in no group, count, size or suggested keep, so every id shown is one Jellyfin opens for
them (checked only for callers whose policy sets one of these: one query per 500 movies, once per index and policy). The
representative is the set's root when in scope, else its lowest item id. A group has at least two entries. A tag set on
a movie's parent folder is a change Jellyfin doesn't report for the movie, so the groups keep its old effect until the
next build (accepted for 1.0).

- **Title + Year** (default) groups entries with the same key and year (production year, else premiere year, else the
  bracketed year of the title). A movie with no year groups only with the title's other yearless movies; with
  `MatchAny` it joins the title's one year when there is only one, else it stays with the yearless ones, flagged
  *Ambiguous*.
- **Title** groups every entry with the same key, whatever its year.
- An entry whose title leaves no key is counted, not grouped.

**Confidence** is a display and sort aid only; the first rule that holds wins:

| Step | Confidence | When |
|---|---|---|
| 1 | Conflict | Two entries carry different TMDb ids, or different IMDb ids (*Different provider ids*) |
| 2 | Possible | The known years differ (every Title group with two years); the runtimes differ by more than 10 minutes **and** more than 15 % of the longer between two entries of the same edition (or both with none: different editions spare their pair, never the group; *Runtimes differ*); or a movie with no year joined by `MatchAny` |
| 3 | Confirmed | At least two entries share a TMDb or IMDb id, and no entry has another |
| 4 | Likely | The same title key and the same known year |
| 5 | Possible | Anything else, such as movies with no year |

A shared id with different years is *Same id, years differ* (TMDb gives some remakes one id), never Confirmed. Other
flags: *Years differ*, *Year unknown*, *Editions differ*, *Cross-library* (no one library holds every entry), *Broken
merge*, *Contains disc rip*, *Contains shortcut* (`.strm`), *Contains placeholder*. *Ambiguous* doesn't cap the
confidence (yearless movies sharing an id among several years are Confirmed and Ambiguous). A yearless movie joined by
`MatchAny` is Possible, while Title mode rates the same two Confirmed when they share an id.

**The suggested keep** is advisory: a real file (not `.strm`, not a placeholder), then a video file over a disc rip or
ISO, the higher width, TMDb or IMDb ids, the higher bitrate, the larger file, more sources, the earlier date added,
compared on each entry's representative; the lower item id breaks a tie. So a 3840-wide file with no ids beats a
720-wide one with them. **Reclaimable** is the size of every other entry's versions in scope, a lower bound ("≥") when a
size is unknown or a version has local alternates or stacked parts; hard links to one file count twice.

**The group id** is the first 16 hex digits of SHA-256 over the mode's name, `:`, and the representative ids the caller
sees (32 hex digits each, in ordinal order, joined with `,`). It changes when the entries change, not when a set gains or
loses a non-representative version; it doesn't depend on library order and reveals nothing outside the caller's view.

**Cost.** Title keys are worked out once per index and title rules and shared. Results are cached per index version,
options and library set (8 kept, all dropped on a newer index), so one administrator's result is never served to another
with other libraries. With 10,019 movies (the `scale` sandbox, Release) a first request took about 55 ms, a cached one
about 10 ms.

## The Bookmarks clip folder

When Bookmarks writes its clips into a folder inside a movie library, Jellyfin scans each clip as a movie named after its
film, so Title + Year lists every clip as a duplicate. Add the clip folder to **Excluded folders** (its full path as the
server sees it, for example `D:\Movies\Clips`); the dialog says how many movies it leaves out. A clip folder outside
every movie library needs nothing.

## Move media: the preview

**Released as a preview only.** 1.0.2 and 1.0.3 have the planning side, the item menu's entry and the dialog; the
**moving itself (the move job) is planned for 1.1 and is not in the code**. `Capabilities.CanMove` is false, so the dialog
has no Move button.

### The plan-only build

`plugins/LibraryMaintenance/MovePreviewOnly.props` holds the switch, `true` for the 1.0.x releases; the plugin and its
tests import it, so both are of one kind (`dotnet build -p:MovePreviewOnly=false` makes the other). With it,
`MOVE_PREVIEW_ONLY` is defined: `MoveFeature.IsPreviewOnlyBuild` is true and `CanMove` stays false, and the probe file
that checks a destination can be written (`DestinationWriteCheck.ProbeFile`, `MovePreflight.CanWriteProbeFile`) is not
compiled: the check is the ACL's (`FolderWriteAccess`). `Move/PreviewOnlyBuildTests` reads both builds' IL: from Move's
types and controller, through every method they reach, the plan-only build calls nothing that writes, makes, moves,
renames or deletes a file or folder (a `FileStream` only with `FileMode.Open` and `FileAccess.Read`, `CreateFileW` only
`OPEN_EXISTING` without `DELETE_ON_CLOSE`), no reflection or `calli` that could reach one, and no Jellyfin service that
saves; the other build's probe write is found, so the check is shown to work. `JFLM_PREVIEW_ONLY_DLL` names one more DLL
to read (a release build's).

Development builds toward 1.1 are numbered **1.1.0.N** and sort **above** the release; Jellyfin keeps only the highest
version of a plugin at startup and deletes the other folders, so never copy a release by hand next to a development
build: deploy it, and `Deploy-Plugin.ps1` removes the other version folders first.

### What to expect from the preview

- **It cannot move anything** (above). Whether a folder could be written is read from its permissions, never with a test
  file.
- **Each file that would move is opened exclusively for an instant**, up to three times 1.5 s apart, to tell whether
  another program has it open ("A file is in use", **Check again**). Meanwhile no other program can open it, so **don't
  preview an item while it is being imported or written** (a download finishing, Sonarr or Radarr importing, a copy).
  A player that already has it open isn't disturbed.
- **Folders' last-access times are the only trace**; no file or folder is made, changed, moved or deleted and no library
  scan starts (checked from the web client with a recursive watcher on the media and the server's IO log).
- A plan is kept in memory for 15 minutes, then forgotten.

### In the web client

`GET /LibraryMaintenance/ClientScript` is added to `index.html` while `EnableWebUi` is on. Browsers report web elements
they can't find to `POST …/Diagnostics/ClientHealth` while `ReportWebAnchorProblems` is on (Diagnostics, `ClientHealth`).

- **The entry**: "Move media to new location", after "Refresh metadata" in the item menu ("⋮", a right-click, More on a
  details page), through the kit's `actionSheet.addItem`. Only for an administrator while the capabilities allow it and
  `ShowMoveInItemMenu` is on; one movie, episode or music video (never a selection or a folder); never in the TV layout.
  A viewer's page never asks the plugin anything. Eligibility is asked only for an item whose menu or page was opened,
  in batches of up to 100, kept 5 minutes; an item that can't move loses the entry.
- **The dialog** (the kit's `nativeDialog`): what moves (folded), where to (the `Destinations` libraries, a folder browser
  with a breadcrumb, a filter, 200 folders a page and badges, "New folder", "New series folder", "Put it in its own
  folder"), and the preview from `POST /Move/Plan` 400 ms after the last change: its verdict ("It could move there",
  never "It can"), blockers, warnings, the files, the space on each drive, the folders it would create, what is kept and
  what is regenerated. "Preview only: nothing is moved." sits above the introduction and in the footer.
- **Names are shown, not obeyed**: each server name is in a `<bdi>`, and a text-direction character shows as `[U+202E]`
  in a box. Tab stays in the dialog and Escape closes it (the kit's `trapFocus`), returning the focus to the "⋮" or More
  that opened it; each part is a section under an `h4`; the status line is a live region; no sideways scroll at 375 px.

### The planning rules

- **The gate.** Every `/LibraryMaintenance/Move` route is for administrators (`RequiresElevation`); with `MoveEnabled`
  off it answers 403 (the kit's `feature-disabled`), on a host that isn't Windows 503 `move.unsupported-os`, both before
  a body is read. `GET /LibraryMaintenance/Capabilities` (any signed-in user; every flag false for a non-administrator):
  `IsAdmin`, `MoveEnabled`, `MoveSupported`, `CanPlanMove`, `CanMove`, `ShowMoveInItemMenu`, `ShowMoveDetailPageButton`,
  `ShowMoveInTvLayout`.
- **Library access.** Item ids resolve for the signed-in administrator, so an item or library they can't open answers
  exactly like one that isn't there (404 from `Folders`, 400 `move.destination-invalid` from `Plan`). An API key sees
  every library.
- **Paths.** A destination is a library folder plus a relative path of existing folders and at most one new folder.
  Network folders and device paths are listed but never opened; volumes are compared by volume GUID. Refused (400
  `move.destination-invalid` unless named): a rooted path, `..`, a trailing dot or space, a reserved name (`CON`, and for
  a new folder also `NUL.txt`); a name Jellyfin ignores or a `.ignore` file in the folder or above it; another entry's
  8.3 short name (`FOLDER~1`); an extras folder name (`Extras`, `Trailers`, …) below a library folder's top
  (`move.extras-folder-name`).
- **Links are never followed.** A junction or symbolic link in the destination path is 400 `move.reparse-point`; in what
  moves it blocks the plan; an item reached through one isn't eligible (409 `move.reparse-point`). Every reparse point
  counts, deduplicated files and OneDrive placeholders included.
- **Another item's media never moves.** A movie's folder holding another item's video or a library folder, or of more
  than 20,000 entries, blocks the plan (`move.foreign-media`); a library folder itself, and a version or part outside the
  movie's folder, are `move.not-eligible`.
- **What moves.** A movie in its own folder, a disc folder or a music video alone in its folder moves as the folder; a
  loose file or an episode as its file set: the files, their sidecars as 10.11.5 reads them (`<name>.*`, `<name>.nfo`,
  its images, the `<name>.trickplay` folder; each file with the longest matching video name) and the extras named after
  it, plus an episode's images in `metadata`. Blockers: a target that exists (`move.collision`; folders are never
  merged), another movie's own folder (`move.into-movie-folder`), an episode outside a series
  (`move.episode-needs-series`), another volume (`move.cross-volume-later`). A loose file goes into its own
  `Title (Year)` folder when the destination holds own-folder movies and no loose videos (`LooseFileFolderMode`
  `MatchDestination`). New item ids are predicted (`GetNewItemId`).
- **Checks that change.** A session playing it (`move.playing`); a file open in another program (`move.in-use`); an item
  held by another operation (`move.item-busy`, `Operations/`); a running scan (a warning); whether Jellyfin can write the
  destination (`move.not-writable`), from the volume's read-only flag and an access check that writes nothing.
  **Planning writes nothing**, so it starts no library refresh (a probe file once did, through Jellyfin's watcher).

## API

Every route but `AdminScript`, `ClientScript`, `Capabilities` and `ClientHealth` is for administrators
(`RequiresElevation`). Options are read by name ignoring case; one given twice is 400.

| Method | Route | Answer |
|---|---|---|
| GET | `/LibraryMaintenance/AdminScript` | Anonymous: the page's ES module, ETag the build's MVID (304 when current); static, no data. |
| GET | `/LibraryMaintenance/ClientScript` | Anonymous: the web client's script, the same way (the login page loads it too). |
| GET | `/LibraryMaintenance/Capabilities` | Any signed-in user: the Move flags above. |
| POST | `/LibraryMaintenance/Diagnostics/ClientHealth` | Any signed-in user (403 API key): the kit's anchor report; 204, also while reporting is off (then dropped); 413 over its limit. A Warning logged once per anchor and web version. |
| GET | `/LibraryMaintenance/Admin/Diagnostics` | The kit's Diagnostics body ("The Diagnostics tab"). |
| GET | `/LibraryMaintenance/Admin/Settings` | `Settings` (the ten 1.0 fields), `Version` (16 hex digits of SHA-256 over them), `Libraries[]`, `ArticlesByLanguage`, `DefaultEditionPhrases`. |
| POST | `/LibraryMaintenance/Admin/Settings` | Body `LibraryMaintenanceSettingsDto` (at most 64 KB): each field given replaces the saved one, a missing or null one keeps it. `version` required (400 without; 409 `SettingsChanged`); `dryRun` true unless false. Report: `DryRun`, `Applied`, `Version`, `Settings`, `Changes[]`, `ScopeChanged`, `RebuildsIndex`, `TitleRulesChanged`, `Preview` on a scope change (the dialog's counts, libraries and folders; a network folder is never opened before the save, a check gives up after 5 s), `Warnings[]`. 400 naming the field for a value the tab refuses (also over 100 folders or 200 phrases, a folder over 1,024 characters, a phrase over 100). A value a newer build saved is kept when sent back. |
| GET | `/LibraryMaintenance/Admin/Export` | The envelope as an attachment, `no-store`. |
| POST | `/LibraryMaintenance/Admin/Import` | An export file (at most 1 MB); `version` and `dryRun` as above. The save's report plus `Source` (`PluginVersion`, `ExportedUtc`, `SameServer`) and the `Warnings` above. 400 another plugin's file, no `Settings`, unreadable settings; 409 `NewerSchema` or `SettingsChanged`; 413 over 1 MB. |
| GET | `/LibraryMaintenance/Duplicates/Index` | The Diagnostics tab's index numbers, with `Version` (0 before the first build), `Dirty`, `Rebuilding`, `Progress`, `ScanInProgress` and `LastError`; counts only for what the caller can see (an API key: everything). Never starts a build; counts as a use of the page. |
| POST | `/LibraryMaintenance/Duplicates/Index/Rebuild` | 202 with the same body. Single-flight; during a scan with an index built, waits for the scan's build. |
| GET | `/LibraryMaintenance/Duplicates/Groups` | A page of groups. Options: `mode` (`Title`, `TitleAndYear`), `unknownYear` (`MatchUnknownOnly`, `MatchAny`), `libraryIds`, `confidence`, `search` (up to 200 characters: the title key contains the text's key, or an entry's name contains the text), `sort` (`Confidence`, `Title`, `Reclaimable`, `Size`, `Newest`), `startIndex`, `limit` (1 to 200, 50). Body: `Index` (with `Stale`), the options used, `OptionsHash` (16 hex digits of SHA-256 over the options and title rules), the totals, `AlternateModeGroupCount`, `ReclaimableBytes`, the page, and `Items` (a card's fields, `Entries` with the keep first). 400 an unreadable option; 202 (`Rebuilding`, `Progress`, `ScanInProgress`, `LastError`) with no index after the wait; 503 when its build failed and none runs. |
| GET | `/LibraryMaintenance/Duplicates/Groups/{groupId}` | One group with `mode`, `unknownYear`, `libraryIds`; 404 "This duplicate group is gone…"; 400, 202, 503 as above. |
| GET | `/LibraryMaintenance/Move/Eligibility?ids=` | 1 to 100 ids: `[{ItemId, Eligible, Code, Reason, UnitKind, OwnerItemId}]` in order; `Code` `move.not-eligible`, `move.item-busy`, `move.reparse-point` or `move.not-found`. |
| GET | `/LibraryMaintenance/Move/Source?itemId=` | `Item`, `Library`, `LocationPath`, `Unit` (`Kind`, `Root`, `Files[{Rel, Bytes}]`, `Dirs`, `TotalBytes`, `Versions`, `Parts`, `Extras`), `Retargeted` (for a version or part); 404; 409 with the code. |
| GET | `/LibraryMaintenance/Move/Destinations?itemId=` | The item type's libraries the caller can open, its own first (only its own with `AllowCrossLibraryMoves` off): `LibraryId`, `Name`, `CollectionType`, `IsSource`, `Locations[{Index, Path, Label, Reachable, FreeBytes, SameVolumeAsSource, Selectable, Reason}]` (`cross-volume-later`, `network-location`, `device-path`, `not-a-local-path`, `unreachable`, `reparse-point`, `ignored`). |
| GET | `/LibraryMaintenance/Move/Folders?libraryId=&locationIndex=&relativePath=&filter=&startIndex=&limit=&itemId=` | `Items[{Name, RelativePath, Kind, Selectable, Reason}]` (`Plain`, `MovieFolder`, `Series`, `Season`, `Ignored`, `ReparsePoint`), sorted by name, 200 a page; 400 a bad path or page; 404 a library or folder the caller can't open; 409 with `itemId` for an item that can't move; 503 one that can't be listed. |
| POST | `/LibraryMaintenance/Move/Plan` | `{ItemIds: [one id], Destination: {LibraryId, LocationIndex, RelativePath, NewFolderName, Wrap, WrapFolderName}}`. 200 even with blockers: `PlanId`, `PlanHash`, `ExpiresUtc` (15 minutes), `Method` (`Rename`, or `CopyVerifyDelete`, a blocker in this version), `Units` (from, to, files, bytes, folders made, predicted ids), `Volumes`, `Preserved`, `Regenerated`, `Warnings`, `Blockers` (`{Code, Message, Paths}`). 400 another number of items, `move.destination-invalid`, `move.reparse-point`, `move.extras-folder-name`; 404; 409; 503. |

## Installing, upgrading and going back

The user README has the short version. Nothing here runs by itself. Paths are Jellyfin's Windows defaults
(`C:\ProgramData\Jellyfin\Server`). The plugin folder is `Library Maintenance_<version>` (for example
`Library Maintenance_1.0.4.0`). 1.0.4 upgrades 1.0.0 to 1.0.3 alike and keeps their settings file (from 1.0.0 or 1.0.1,
the web client and Move switches start on).

1. **Stop Jellyfin** (it locks plugin DLLs). For an upgrade, copy the settings file first:

   ```powershell
   Copy-Item -LiteralPath 'C:\ProgramData\Jellyfin\Server\plugins\configurations\Jellyfin.Plugin.LibraryMaintenance.xml' -Destination "$HOME\JellyfinBackups\"
   ```

2. **Install**: delete the installed version's folder (keep only one Library Maintenance folder) and extract the release
   zip into `plugins\Library Maintenance_<version>`. From a clone, `scripts\Install-RealServer.ps1` does it from the
   release tag with a backup of its own, first as a dry run, then with the same `-Version`:

   ```powershell
   <repo>\scripts\Install-RealServer.ps1 -Plugin LibraryMaintenance -Version 1.0.4 -WhatIf
   <repo>\scripts\Install-RealServer.ps1 -Plugin LibraryMaintenance -Version 1.0.4
   ```

   It builds the tag (the plan-only build) in a temporary worktree, takes a cold backup to
   `$HOME\JellyfinBackups\<yyyyMMdd-HHmmss>-before-LibraryMaintenance-<version>`, replaces the installed folder, checks
   each file by SHA-256 and writes `backup.json` and `RESTORE.txt`; it leaves the settings file alone, and refuses while
   a `jellyfin.exe` runs. Its "Not there, so not backed up" line names `data\librarymaintenance`, which Library
   Maintenance never makes. More in [scripts/README.md](../../scripts/README.md#release-and-install-tooling),
   "Install-RealServer.ps1".
3. **Start Jellyfin** and reload every open web client tab (`Ctrl+F5`).
4. **Check**, as an administrator: Plugins lists Library Maintenance at the new version, Active, with no older entry; its
   page shows Duplicates as before, Settings as you had them (with the **Web client and Move** switches), and in
   Diagnostics the new `PluginVersion`, `Abi` `Active` true and no `ClientHealth` report; the log has
   `Loaded plugin: "Library Maintenance" "<version>"` and no error from `Jellyfin.Plugin.LibraryMaintenance`; the entry
   is on a movie's, episode's or music video's "⋮" and none on a series, a selection, the TV layout or for a
   non-administrator. With Bookmarks writing clips inside a movie library, exclude that folder ("The Bookmarks clip
   folder").
5. **Try the preview** on a dozen items of your own: "Preview only: nothing is moved.", **Close** and no Move button;
   check what moves, where it could go, and the verdict, warnings and what is kept.

**To go back, or to remove it.** An install by `Install-RealServer.ps1` has the backup's `RESTORE.txt`; way A puts the
previous version back (to remove the plugin instead, delete rather than copy back, as a new install's `RESTORE.txt`
says). By hand:

1. Stop Jellyfin.
2. Delete `plugins\Library Maintenance_1.0.4.0`; to go back, copy the earlier version's folder back (from the backup or
   its release zip).
3. Put the settings file you copied back into `plugins\configurations` (going back past 1.0.2, its switches go with it);
   to remove the plugin, delete `plugins\configurations\Jellyfin.Plugin.LibraryMaintenance.xml` instead.
4. Start Jellyfin and reload the web client. Back on 1.0.1 or 1.0.0 there is no Move entry (no script tag before 1.0.2);
   removed, the drawer has no Library Maintenance link. The plugin never wrote to a library, so there is nothing else to
   undo.

Way B of `RESTORE.txt` (the whole server back to the backup's moment) is only for a broken server: it loses everything
recorded since, everyone's watch history among it. *Uninstall* on the Plugins page, then a restart, also removes the
plugin and leaves its settings file. **Don't use Disable** (see "The Settings tab").

## Building and testing

From the repository root:

```powershell
dotnet build plugins/LibraryMaintenance/Jellyfin.Plugin.LibraryMaintenance
dotnet test plugins/LibraryMaintenance/tests/Jellyfin.Plugin.LibraryMaintenance.Tests
```

What the tests cover:

| Area | Tests |
| --- | --- |
| Kit conventions | The kit's API and boundary checks; the Dashboard page (no `${`, text-only rendering), node's syntax check of the stub and the module, the kit's AdminScript checks. |
| The page | `Web/admin.test.mjs` in the kit's fake page: tabs and first-open loading, the `tab` parameter, keyboard and TV layouts, the status line, Rebuild and polling, the groups, cards, posters and Load more, the Settings tab, its dialog and version check, export and import, Diagnostics. |
| Settings API | `Api/SettingsApiTests`: administrators only, dry run and version, every refusal, a newer build's values kept, the preview and a limited administrator's, export and import round trip, concurrent saves. |
| The index | `Duplicates/MovieIndexBuilderTests`, `MovieIndexServiceTests`, `Api/DuplicatesApiTests` on a fake library; `Duplicates/MovieIndexFreshnessTests` on a manual clock (the warm and scan rules, the cold build, the request wait, the monitor end to end). |
| Grouping | `Duplicates/DuplicateGrouperTests` (modes, policies, every confidence step, flags, the keep, reclaimable bytes, the group id, scope); `Api/GroupsApiTests` (access, two administrators and the cache, parental limits, API keys, options, filters, paging, 202 and 503); `Duplicates/GroupsQueryTests` (10,000 synthetic movies under a second), `GroupResultCacheTests`, `ItemVisibilityTests`. |
| Move | `Move/` (the gate on every route by reflection; path rules, volumes, real junctions, links and ACL denies; the planner on temp trees with Jellyfin's id rule; lock probes with real handles; sessions; eligibility; the plan service, hash and 15 minutes; the routes over HTTP; a whole plan watched for writes; `PreviewOnlyBuildTests`), `Operations/` (the operation gate), `Web/move.test.mjs` (the entry and dialog, run by `ClientScriptTests`). |

In a sandbox ([scripts/README.md](../../scripts/README.md#library-maintenance-checks), "Library Maintenance checks"):
`./scripts/dev-server.ps1 -Name default -Port <port> -Background` deploys, `Initialize-Sandbox.ps1` seeds, and the page
is at `http://localhost:<port>/web/#/configurationpage?name=LibraryMaintenance`. `librarymaintenance-groups-probe.mjs`
checks the groups' API (with the `scale` sandbox's timings), the `librarymaintenance-move-*` webchecks the entry and
dialog (`Set-MoveBlockedFixture.ps1` makes the blocked fixture), and `librarymaintenance-themes.json` the contrast. The
`move` sandbox, with its made-up second volume, is described there too.
