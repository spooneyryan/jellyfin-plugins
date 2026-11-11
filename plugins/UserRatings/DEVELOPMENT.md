# User Ratings

A Jellyfin plugin for five-star ratings with half stars and optional text reviews on any rateable item, server-wide:
every member sees every member's ratings and reviews on the items they can open. An admin setting, `WhoCanRate`, can
limit who may rate, down to a single user; everyone still reads. This is its technical documentation; the user guide is
[README.md](README.md).

**Version 1.0.2.0** for Jellyfin 10.11.5 (`net9.0`). Members rate and review on an item's details page ("Member ratings
& reviews"), see the members' average beside the item's other scores, and the ratings follow their items through the
library. A deleted member's ratings follow `DeletedUserPolicy`. Admins moderate; the settings page has every setting
(Settings tab), and its Data tab exports, imports and restores, imports Jellyfin Enhanced's user reviews and exports the
ratings back in JE's format. The release notes are in [build.yaml](build.yaml)'s `changelog`.

Contents: [Installing, upgrading and going back](#installing-upgrading-and-going-back) ·
[Architecture](#architecture) · [The database](#the-database) · [The library lifecycle](#the-library-lifecycle) ·
[Deleted and unknown users](#deleted-and-unknown-users) · [Privacy: what goes when](#privacy-what-goes-when) ·
[Settings](#settings) · [The member API](#the-member-api) · [The admin API](#the-admin-api) ·
[Moderation](#moderation) · [The Data tab](#the-data-tab) · [The web client](#the-web-client) ·
[Moving from Jellyfin Enhanced's reviews](#moving-from-jellyfin-enhanceds-reviews) ·
[Known limitations](#known-limitations) · [Tests](#tests) · [In a sandbox](#in-a-sandbox)

## Installing, upgrading and going back

The user README has the short version. This is the careful way for a server that already has members and, often,
Jellyfin Enhanced (JE) with its user reviews. Nothing here runs by itself. Paths are Jellyfin's Windows defaults
(`C:\ProgramData\Jellyfin\Server`); use your server's own.

### Installing

1. **JE is 12.14**, if you use it: User Ratings reads JE 12.14's `reviews.json` and settings and exports in its format;
   with another version, read the import's dry run closely.
2. **Back up**: Dashboard → **Backups**, create one. Stop Jellyfin (a running server's database copy isn't reliable)
   and copy `plugins\configurations` (JE's settings among them) aside.
3. **Install**, Jellyfin stopped: extract the release zip into `plugins\User Ratings_<version>`, or from a clone run
   `scripts\Install-RealServer.ps1 -Plugin UserRatings -Version 1.0.2` (first with `-WhatIf`). It builds the tag
   `userratings/v<x.y.z>` in Release, takes a cold backup under
   `$HOME\JellyfinBackups\<yyyyMMdd-HHmmss>-before-UserRatings-<version>` (JE's settings and `reviews.json` included,
   with `RESTORE.txt`, the two ways back) and installs the folder checked by SHA-256; it writes no plugin setting and
   nothing of JE's ([scripts/README.md, "Install-RealServer.ps1"](../../scripts/README.md#install-realserverps1)).
4. **Mount every library drive before starting Jellyfin.** A start with one missing lets Jellyfin's own clean-up remove
   what it takes to be gone (collections, for example); User Ratings keeps such ratings, Jellyfin doesn't keep its data.
5. **Start Jellyfin**, reload every web client tab with **Ctrl+F5**, and check as an administrator: *User Ratings* at the
   new version, *Active*; Diagnostics' *Ratings database* `...\data\userratings\userratings.db (schema 4, Ok)`; the log's
   `Loaded plugin: "User Ratings" "<version>"` and no `[ERR]` from `Jellyfin.Plugin.UserRatings`; the Data tab's tools
   (only a note about every library means your account's libraries are ticked one by one: tick **Enable access to all
   libraries** on its **Access** tab); **Member ratings & reviews** before the cast on a film's page. Rate nothing before
   the JE import: a rating made and deleted now makes the import skip that member's JE review ("Skipped: deleted here").
6. **The switch-over from JE's reviews**: [Moving from Jellyfin Enhanced's reviews](#moving-from-jellyfin-enhanceds-reviews)
   (a dry run and its report, Import, then the checklist until "Every required step is done: Jellyfin Enhanced's user
   reviews are off."). JE's `reviews.json` stays as it was.
7. **Rate something**, then sign in as another member: your rating and review, the chip and the histogram.
8. **Back up after the switch**: Data → **Export** (`userratings-export-<time>.json`) and **Export for Jellyfin
   Enhanced** (`reviews.json`); keep both with the backup folder.

Two points matter before anyone is deleted or anything disabled: **deleting a Jellyfin user deletes their ratings and
reviews for good** under the default policy (choose *Keep them as a former member's* first if you'd rather keep them),
and **don't use *Disable*** on the Plugins page (on 10.11.5 the next start deletes the folder; untick *Add User Ratings to
the web client* instead). The switch from JE makes nothing more visible (JE showed every review to any signed-in user);
going back is the other way round ([Going back to Jellyfin Enhanced](#going-back-to-jellyfin-enhanced)).

### Upgrading 1.0.0 to 1.0.1

No data change: schema 4, the snapshots, the archive and the settings stay 1.0.0's; nothing of JE's is read or written.
Note the Diagnostics *Ratings* line and the *Snapshots*, take a Jellyfin backup and stop Jellyfin. Replace
`User Ratings_1.0.0.0` with `User Ratings_1.0.1.0` (one User Ratings folder only), or run `Install-RealServer.ps1` with
`-Version 1.0.1` (it prints `Removed User Ratings_1.0.0.0`). Mount every drive, start, Ctrl+F5, and check the version,
the same *Ratings* line and snapshots (none taken: no schema change), the settings, a rated film and the log.

### Going back

- **To 1.0.0**: nothing to undo. Stop Jellyfin, delete `plugins\User Ratings_1.0.1.0`, put `User Ratings_1.0.0.0` back
  (the release zip or the script's backup), start. `RESTORE.txt`'s way A also restores the data and settings as before the
  upgrade (ratings made since are lost); way B puts the whole server back. Neither is needed for a plain downgrade.
- **To a build older than schema 4** (0.3.19 and older open it read-only): stop the server, put the upgrade's
  `userratings-migrate-*.db` (in `data/userratings/snapshots/pre-op`, kept 7 days) back as
  `data/userratings/userratings.db`, delete `userratings.db-wal` and `-shm`, install the older build, start. Ratings
  made since are lost (export first).
- **Undo only the JE import**: Data → *Snapshots*, *Restore…* the newest *Before an import*, then turn JE's reviews on.
- **Back to JE's reviews, keeping what was rated here**: [Going back to Jellyfin
  Enhanced](#going-back-to-jellyfin-enhanced).
- **Remove User Ratings**: export first (step 8). With `RESTORE.txt`, way A, Jellyfin stopped: delete the plugin folder;
  move `data\userratings` aside (renamed `userratings.removed-<yyyyMMdd-HHmmss>`, never deleted); delete
  `plugins\configurations\Jellyfin.Plugin.UserRatings.xml`; start and turn JE's user reviews back on. Way B (the whole
  server, losing everything since) is only for a broken server. *Uninstall* on the Plugins page plus a restart leaves
  `data\userratings` and the settings for a later install.

## Architecture

- **Identity**: GUID `f53c7c34-8cba-4846-b43b-956816b2f6ae`, assembly `Jellyfin.Plugin.UserRatings`, routes
  `/UserRatings`, web prefix `jfur`, schema prefix `UserRatings`, data folder `data/userratings`, `targetAbi` 10.11.5.0.
  On another Jellyfin (12.x) the kit's ABI guard leaves it inert: no service, no web part, endpoints 503 with the reason.
- **SQLite through the server's own copy**: `KitUseSqlite` compiles the kit's `SqliteDatabase` in against
  `Microsoft.Data.Sqlite.Core` 9.0.11 with `ExcludeAssets=runtime` and `PrivateAssets=all`, so the plugin uses the
  server's Microsoft.Data.Sqlite and SQLitePCLRaw and ships no SQLite file. Deploy's allowlist refuses any other DLL and a
  Microsoft.Data.Sqlite newer than the server's. The tests get the full package, in their output only. See
  [kit/README.md, "SQLite"](../../kit/README.md#sqlite-sqlite-kitusesqlite).
- **No SQLite type at the boundary** (an action that named one would fail before the kit's catch could answer 503):
  the controllers take only `Services/IUserRatingsApi` (public, DTOs only), and `PublicBoundaryTests` checks the IL: only
  `Storage/RatingsSql`, the store's `NoInlining` core methods and `Storage/SqliteRatingsWriter` name one.

| Component | What it does |
|---|---|
| `Storage/SqliteRatingsStore` | Units of work, lookups, the moderation log, the import ledger, meta, counts ([The database](#the-database)). |
| `Domain/StarValue`, `Domain/ReviewText` | 0.5 to 5 in halves (400 otherwise; native `UserData.Rating` both ways). Text normalised: line endings, control characters and NUL out, lone surrogates replaced, over 3 blank lines collapsed to 2; the limit in UTF-16 units, as browsers count. |
| `Services/VisibilityPolicy` | Hidden or disabled authors (under the Hide settings) and orphaned ids count nowhere and show only to their author and moderation; former members count; summaries are the same for every viewer. |
| `Services/SummaryCache` | Per-item rows; a fill inserts only while the generation it read is unchanged; a write drops the items it touched. |
| `Services/ItemAccess` | `GetItemById<BaseItem>(id, user)`, lists through the kit's `EligibleIds`; one 404 for "not visible" and "not rateable"; cache ghosts 404 on writes. |
| `Services/PlayedCheck`, `CopySiblings` | `RequirePlayedToRate` through the kit's `FreshUserData` (played children for containers); the other copies the viewer can open. |
| `Services/RatingsService` | Reads, and member writes with checks in this order: the switch, writable, visible and rateable, `WhoCanRate`, played, reviews; preconditions inside the transaction; clearing the last of stars and text deletes the row with a tombstone. |
| `Services/RatingsData`, `RatingsModeration`, `Import/*` | [The Data tab](#the-data-tab), [Moderation](#moderation), the JE import and export. |
| `Lifecycle/*` | The library lifecycle, archive, deleted users, maintenance steps. |
| `Integration/ItemMovedHookV1` | Library Maintenance's call (public static, BCL types only) asking for a move-journal catch-up. |
| `Configuration/RatingsSettings` | The one settings reader ([Settings](#settings)). |

**The daily task** *User Ratings: Maintenance* (03:20): the kit's `quick_check` (on its own read-only connection, so
damage written from outside isn't hidden by a pooled page cache) and the daily snapshot first; then the kit's
`VerifySweep`, `ResolveDetached`, `Archive`, `Users` and `Reattach`, and its own `MergedVersions`, `ArchivePurges`
(failed archive rewrites retried), `ModerationLogRetention` and `DataTemp` (the `tmp/` sweep); `Checkpoint` last. Its gate
`DataJobs` (`AddKitMaintenanceGate`) waits up to 10 minutes for a running data job, restore or Start empty, then holds
the job runner throughout (a job meanwhile is `409 JobRunning`, "A daily maintenance is running."); still busy, the run is
skipped (`LastRun.Skipped` `"DataJobs"`) until the next day.

**Diagnostics** (`GET /UserRatings/Admin/Diagnostics`): `Store` (file, schema, status, snapshots, quarantines,
`Checkpoint`), `Records` (counts, `DetachedJeWithKeySuffix`), `Maintenance`, `Lifecycle`, `Users`, `JellyfinEnhanced`
and the web client problems browsers reported.

## The database

`data/userratings/userratings.db` through the kit's `SqliteDatabase` (`AddKitSqliteDatabase`), opened by its hosted
service after the startup gate and released at shutdown: WAL, `foreign_keys=ON`, one writer, and `secure_delete` on every
connection (deleted content is overwritten with zeros).

**Schema** (`Storage/Migrations/`). A database upgrades at the first start, after the kit's `migrate` snapshot; an older
build opens a newer schema read-only.

| Version | Migration | What it does |
|---|---|---|
| 1 | `M001_Initial` | `Ratings` (identity snapshot, container ids `SeriesId`, `SeasonId`, `AlbumId`; a partial unique index: one attached rating per member and item), `RatingKeys` (`ON DELETE CASCADE`), `FormerUsers`, `ModerationLog`, `ImportLedger`, `ImportLedgerRows`, `Tombstones`, `Meta`. |
| 2 | `M002_AttachedUtc` | A nullable `Ratings.AttachedUtc` (when a relink last attached the row), in place; the move journal and resolver follow only lines after it. |
| 3 | `M003_Vacuum` | No schema change: a `VACUUM` after the upgrade (the kit's `VacuumAfter`), so free space holds nothing deleted before `secure_delete`; a failed one is a Warning and retried at the next start. |
| 4 | `M004_DeletionOrigin` | `Tombstones.Origin` and `FormerUsers.Origin`: `Local` (the default) or `Import:<job id>`, so a restore keeps only this server's deletions. Not in an export. |

CHECK constraints guard only fixed rules (1 to 10 half stars, 1 to 20,000 characters, booleans, stars or text, the
detached pair). Growable enums (`Origin`, `DetachReason`, moderation actions, `DeletedBy`) are validated by the store
(`Storage/RatingValues`), so a new value never needs a table rebuild.

**The store.** Every write is one unit of work (`WriteAsync(w => ...)`): one transaction of the single writer, so a read,
its checks and the writes commit together; it returns the generation (raised by every commit, restore and recovery), and
`Changed` names the items touched.
- Stars and review are a keyed upsert (`ON CONFLICT(UserId, ItemId) WHERE DetachedUtc IS NULL DO UPDATE`): a double
  click or two tabs is the last write, never a 409. `EditCount` and "(edited)" move with the text only;
  `ReviewUpdatedUtc` with the text or the spoiler flag.
- Provider keys (the kit's `ItemIdentity.StrongKeys`: `Movie|Tmdb|603`, `Episode|Tvdb|81189|S01E02` …) are written with
  the rating. Only the unique index's violation (SQLite 2067) is a 409; other constraints are a logged 500. Deletes write
  their tombstone (ids and times only). Times are whole milliseconds, each at least a millisecond after the row's last.
- Read-only (newer schema, failed upgrade, damage): writes 503, reads go on. Held by another program: a 503 "The
  database is busy; try again in a moment." within about 5 s.

**Snapshots** (the kit's rules: [kit/README.md, "SQLite"](../../kit/README.md#sqlite-sqlite-kitusesqlite)):
- Daily, in `snapshots/`: `SnapshotsToKeep` (7, one a day). Pre-operation, in `snapshots/pre-op/` (`migrate`, `import`,
  `moderate`, `purge`, `user-deleted`, Restore's safety one) with the archive's copies
  (`userratings-archived-<operation>-<time>.jsonl`): the last 7 days, at most 50, none older; the newest reused while
  nothing was written; none for an operation that changes nothing; none of a database that fails `integrity_check`.
- Never deleted: a snapshot dated over a day ahead, and those present at a restore or Start empty (listed in
  `userratings.db.keep-snapshots`) until an admin restores one of them.
- A failed restore at open puts the database back as found and stays read-only with a banner. The admin's Restore
  refuses while a readable file was put back by hand ("… restart the server to use it.") and undoes itself if a step
  fails. A damaged database stays read-only after a restart (an index alone is rebuilt).
- Quarantine folders (`quarantine-*`: an unopenable database with its `-wal` and `-shm`) stay until an admin deletes them.

## The library lifecycle

`Lifecycle/RatingsLibraryConsumer` takes the kit's library-change batches: Removed, then the verify sweep when folders
were removed, then Added and Updated. `Lifecycle/RatingsPostScanTask` flushes the monitor inside every scan, so a folder
renamed on disk detaches and relinks in one batch. The kit's side: [kit/README.md, "Record lifecycle and library
reads"](../../kit/README.md#record-lifecycle-and-library-reads-library-hosting).

- **Removed**: ratings on a removed item (or an episode, season or track of a removed container) detach as `Removed`,
  never deleted, with their identity snapshot; what a removed folder took silently the sweep detaches as `Missing`.
  10.11.5's item cache still answers for an album whose artist folder is gone (a **cache ghost**), so every look-up walks
  the parents (`Kit/Library/LibraryPresence`): nothing relinks onto a ghost, a write on one is the one 404, and its read
  has `Can.Reason` `NotInLibrary`.
- **Added and Updated**: an update refreshes snapshots (nothing new, nothing written); a version merged into another
  moves its ratings to the primary (`Relink`). New or updated items take back detached ratings through the kit's
  resolver: the same path, or exactly one Strong candidate (a shared TMDb, IMDb, TVDb or MusicBrainz key); several are
  `Ambiguous`. An item at the path whose strong ids contradict never takes it (`RelinkPolicy.RefuseConflictingExact`):
  `Ambiguous` with it as candidate; another kind (a Movie back as a Video) only when a strong id agrees.
- **The conflict rule**: onto an item its member already rated, the newer stays and the older is `Ambiguous`. A relink
  never changes stars, review, times or edit count. Detached ratings are resolved at every start (after the journal's
  catch-up) and daily.
- **The move journal**: Library Maintenance's Committed lines move ratings still on an old id (and moved containers'
  ids), at every start and on each `ItemMovedHookV1` call (a catch-up from the stored position, so a lost or repeated
  call changes nothing).
- **Never detached because a library is briefly unavailable** (`Lifecycle/LibraryAvailability`): before a rating
  detaches, its stored path is checked, and it stays attached and listed in `Lifecycle.Held` while its library folder is
  missing, unreadable or empty (only Hidden+System entries such as `$RECYCLE.BIN` count as empty) or a folder on the way
  can't be read (`LibraryFolderUnavailable`), while its file is still on disk (`StillOnDisk`), or while no library folder
  is known (`NoLibraryFolders`). The **mass-detach guard** holds a pass that would detach every rated item of a library
  folder at once (at least 2, none left) as `MassDetach`, with a warning: the folder may hold another drive's contents. A
  removal split across two passes isn't guarded, by design. This also covers 10.11.5's
  `ValidateTopLibraryFolders(removeRoot: true)` and an offline junction. Each sweep looks again. A held rating follows a
  rename only within the library folder it was held in, never through the conflict rule (if its member rated the target,
  nothing changes). A hold never expires.
- **Detach now** (`POST Admin/Held/Detach`, `{"ItemIds": [...]}`, 1 to 1000): held items' ratings detach as `Missing`,
  each with a `Detach` log row (admin, reason, `HeldSinceUtc`, `MassDetach`), and one Activity Log entry
  (`UserRatingsHeldDetached`); an item back, or not held, is left alone (`NotHeld`, asked again under the write). 503
  while read-only. The Data tab's **Detach all listed** sends parts of 1000.
- **The archive**: ratings detached longer than `DetachedRetentionDays` move to `archive/userratings-archived.jsonl`,
  whole, without a tombstone; `Reattach` puts one back on its item found again, under the conflict rule (one that would
  only lose it stays archived: `Lifecycle.ArchiveSuperseded`). Held ratings are attached, never archived. Any archive
  rewrite drops every history line.
- **Versions merged while the server was off**: `MergedVersions` moves ratings to the primary under the conflict rule,
  only for members who can open it (`MergedVersion`; `Checked`, `MergedItems`, `Moved`, `Stayed`, `Ambiguous`). Chains are
  followed to the end, 16 links at most (a cycle or longer chain stays, logged once), here and in every relink.
- **Ambiguous**: a pass that makes new ones writes one Activity Log entry (Warning, `UserRatingsAmbiguous`) and a banner
  until the Data tab is opened (`POST Admin/Detached/Seen`).
- Every automatic change is logged at Information with ids only (never names, text or paths); nothing happens while
  read-only. `Lifecycle` in Diagnostics: the catch-up, the last batch, the held ratings, `HeldCheckedUtc` (null until the
  startup sweep: holds are kept in memory), counts by reason, the journal position.

## Deleted and unknown users

`Lifecycle/RatingsUserDataOwner` is the kit's `IUserDataOwner` ([kit/README.md, "Users and
data"](../../kit/README.md#users-and-data-users-export-and-import-in-storage)). When Jellyfin deletes a user,
`DeletedUserPolicy` applies to all their ratings, attached, detached or archived, after a `user-deleted` snapshot:

| Policy | What happens |
|---|---|
| `Delete` (default) | Deleted with `UserDeleted` tombstones; a log row with counts, no text. The archive is rewritten without the user's lines (by their rating ids, and unreadable lines), after a copy into `snapshots/pre-op/`. |
| `Anonymize` | A former member ("Former member", "Former member 2" …) takes the ratings, which count and show under that name (`IsFormerMember`); archived ones are appended again under it and the old id's lines go. |
| `KeepAsOrphan` | The ratings stay under the old id, counted nowhere, shown only to moderation; logged once. |

- The archive is read inside the transaction, so an unreadable archive refuses the policy with nothing written; the kit
  then records it as **pending** (`Users.PolicyPending`) and each daily reconciliation retries it with the setting as it
  is then. A pending policy never reaches a user who is back (a restored Jellyfin database): it is dropped with a warning;
  while the user list can't be read nothing pending is applied.
- The archive rewrite runs **after** the commit (`Meta.ArchivePurgesPending`); one that fails is retried by
  `ArchivePurges`, and meanwhile its ratings count for nobody (tombstoned; a reattach skips them).
- **Unknown users**: the `Users` step marks ratings whose user the server doesn't know (a restore onto a rebuilt server,
  a deletion while the plugin was off) as orphans, changing nothing; former members never are. Archived ratings count;
  while the archive is unreadable the counts are a 503 and the reconciler skips User Ratings (`SkippedOwners`).
  Diagnostics `Users` lists them with their last-seen names (`Meta.KnownUsers`).
  - **Remap by name** (`POST Admin/Users/Remap?by=Name`, a dry run) gives each one's ratings, archived too, to the user
    with its last-seen name (on an item both rated, the newer stays, the other is `Ambiguous`); users deleted here are
    never remapped. The dry run's `PlanToken` covers the pairs, what moves and the Ambiguous rows; the real run is
    `?by=Name&dryRun=false&ifPlan=<token>` (400 without; 409 `Changed` when the plan changed). `UserRemapped` rows.
  - **Apply deleted-user policy** (`POST Admin/Users/ApplyPolicy`, `{"UserIds": [...]}`, 1 to 1000, none current); 503
    when the user list is empty or unreadable.
  - Both snapshot first (unless nothing changes), need every library (`403 AllLibrariesNeeded`; Diagnostics then hides
    the list with `OrphanUsersHidden`) and answer 409 while read-only.

## Privacy: what goes when

Every write that takes text away (a member's delete, clear or edit to other words; a moderator's removal; `Delete` or
`Anonymize`):
- **`userratings.db` and its `-wal`** lose it at the **checkpoint**, not the commit: in WAL mode the commit writes the
  pages (freed cells zeroed by `secure_delete`) to the `-wal` only. The write asks for a background
  `wal_checkpoint(TRUNCATE)` about a quarter of a second later (a burst is one), which empties the `-wal`; blocked by a
  long reader it retries for about two minutes, then stays pending (`Store.Checkpoint.Pending`, Warning) until the next
  write or the daily `Checkpoint`. A start that finds frames in the `-wal` checkpoints at once. Once `Pending` is false and
  `Completed` equals `Requested`, neither file holds the text.
- **The archive's** earlier lines of a rating archived once and put back go right after the commit, without a copy
  (pending in `Meta.ArchiveIdPurgesPending`, retried daily; never failing the member's request).
- **Copies taken before** keep it until they expire: the `moderate` and `user-deleted` snapshots (7 days) and the daily
  ones (`SnapshotsToKeep`). A member's own delete, clear or edit takes no snapshot.
- **An export's file on the server** (`tmp/userratings-export-<job>.json`) goes an hour after the job (checked every
  minute), at once on `DELETE Jobs/{id}`, and when the job fails. Downloads open it with delete sharing; a file held
  without it is withdrawn (`202 FileDeletePending`) and deleted once closed. A kill's leftovers go at the next start and
  in `DataTemp`.
- **Quarantine folders** stay until an admin deletes them. A Restore never brings back this server's deletions
  ([The Data tab](#the-data-tab)).

## Settings

The settings live in `plugins\configurations\Jellyfin.Plugin.UserRatings.xml`. Names are strings, not enums: Jellyfin
10.11.5 replaces the whole file with defaults when it can't read one value, so a value a newer build wrote never stops
the file from loading. `Configuration/RatingsSettings` reads each name in any case, with a safe fallback for an unknown
or blank one (logged once), and holds numbers to their ranges. Changes apply on the next request, except the three
marked "restart".

| Setting | Default | What it does |
|---|---|---|
| `EnableRatings` | on | Off: every item read and write is 403 (Capabilities says so). |
| `EnableReviews` | on | Off: no review list; `GET …/Reviews` and a review's PUT are 403; stars stay. |
| `WhoCanRate`, `RaterUserIds` | `AllUsers`, none | `SelectedUsers` limits rating to `RaterUserIds`. Unknown name: `SelectedUsers` (nobody gains the right). |
| `RateableItemTypes` | Movie, Series, Season, Episode, Video, MusicVideo, MusicAlbum, Book, AudioBook | Of twelve kinds offered; unknown names left out, the default list when none is known. |
| `RequirePlayedToRate` | off | Rating needs the item played. |
| `MaxReviewLength` | 5000 | 2000 to 20000 characters. |
| `AllowSpoilerFlag` | on | Members may mark a review as a spoiler. |
| `HideReviewsFromHiddenUsers`, `HideReviewsFromDisabledUsers` | off, on | Leave out hidden / disabled users' ratings. |
| `ShowReviewerAvatars`, `ShowAverageChip`, `ShowHistogram`, `SectionExpandedByDefault` | on | The details page's parts. |
| `MinRatingsForAverage` | 1 | 1 to 10 ratings before the average shows. |
| `ShowDetailButton` | `Auto` | The Rate button: `Auto`, `Always`, `Never` (unknown: `Auto`). |
| `ReviewsPageSize` | 20 | 5 to 50. |
| `EnableWebUi` | on | Add User Ratings to the web client (the REST API keeps working). |
| `ReportWebAnchorProblems` | on | Record web client problems in Diagnostics. |
| `SnapshotsToKeep` | 7 | 0 to 365 daily snapshots. |
| `DetachedRetentionDays` | 90 | 0 to 36500 days before a detached rating is archived; 0 never. |
| `ModerationLogDays` | 365 | 0 to 3650; 0 keeps all. The daily step deletes older rows (but every row a restore of a kept snapshot reads, and all of those when the snapshots can't be listed), then checkpoints. |
| `DeletedUserPolicy` | `Delete` | `Delete`, `Anonymize`, `KeepAsOrphan` ([Deleted and unknown users](#deleted-and-unknown-users)); unknown or blank: `KeepAsOrphan`, which deletes nothing. |
| `ShowJellyfinEnhancedBanner` | on | JE's banner while JE's user reviews are on. |
| `ShowOnPosters`, `PosterBadgeContent`, `PosterBadgePosition`, `EnableTopRatedPage` (restart), `RegisterPluginPages` (restart), `PageHostMode` (restart), `ShowFallbackNav`, `MirrorToUserData` | off, `Average`, `top-start`, on, on, `FallbackOnly`, on, off | *Coming with version 1.1*: kept, changing nothing yet. `MirrorToUserData` can't be turned on. |

An unused key an earlier build wrote (`ModerationSnapshotDays`) loads, is ignored and is dropped at the next save.

**The Settings tab** (`Web/admin/js/20-settings.js`, `Web/admin/css/settings.css`) has every setting in its group with
label, help, "Default: …" and, where needed, "Takes effect after a restart.": switches, choice buttons (no `<select>`, so
the TV D-pad works), number fields, kind check buttons and a users picker (a gone user shows as "A user no longer on the
server"). Values are checked as they change; **Undo changes** resets. **Save** sends what changed as a dry run with the
settings' version, asks first when the server names consequences (Cancel focused), then saves; a 409 offers **Reload**.

**The Settings API** (admins and API keys; `no-store`):
- `GET /UserRatings/Admin/Settings`: `Settings`, `Defaults`, `Version` (16 hex digits, a hash), `Users` (`Id` without
  hyphens, `Name`, `Missing`), `ItemKinds`, `RestartFields`, `Problems` (saved values not usable as they are, and what
  is used instead).
- `POST /UserRatings/Admin/Settings?version&dryRun` with the settings to change: a dry run by default; `dryRun=false`
  needs `version`. It answers `Changes` (`Field`, `Label`, `From`, `To`, `RestartNeeded`) and `Consequences`: counts
  only, and **the whole server's**, whoever asks ("… on the whole server"), since settings are server-wide.
- Refusals, nothing saved, with a stable `code`: 400 `InvalidSetting:<setting>`, `UnknownSetting`, `VersionNeeded`; 409
  `SettingsChanged` (naming what changed; the dry run checks too); 413 over 64 KiB; 503 while not loaded. One save at a
  time.

## The member API

Every route needs a signed-in user's token (`Authorization: MediaBrowser Token="<token>"`): an API key gets 403 "This
endpoint needs a signed-in user; API keys can't be used.", and the user always comes from the token. Errors are
ProblemDetails; null fields are left out.

| Route | What it does |
|---|---|
| `GET /UserRatings/Capabilities` | `Enabled`, `IsAdmin`, `CanRate`, `CanReview`, `RequirePlayedToRate`, `MaxReviewLength`, `AllowSpoilerFlag`, `RateableItemTypes`, `Display`, `TopRatedPageEnabled` (false before 1.1), `StoreReadOnly`. 200 with `Enabled` false while ratings are off. |
| `GET /UserRatings/Items/{itemId}?startIndex&limit&sort` | `Summary` (`Average`, `Count`, `ReviewCount`, ten-bucket `Distribution`; the same for everyone), `Mine`, `Reviews` (a `QueryResult` of the others' rows), `OtherCopies` (only copies the user can open) and `Can` (`Rate`, `Review`, `Moderate`, and `Reason` while `Rate` is false: `ReadOnly`, `NotARater`, `NeedsPlay`, `NotInLibrary`). |
| `GET /UserRatings/Items/{itemId}/Reviews?startIndex&limit&sort` | The others' rows; `sort` `Newest` (default), `Oldest`, `Highest`, `Lowest`, by name only (`sort=1` is 400); `limit` 1 to 200. 403 while reviews are off. |
| `PUT /UserRatings/Items/{itemId}/Mine/Rating` | `{"Stars": 3.5}`; answers the item. Two at once: the last write wins. |
| `DELETE /UserRatings/Items/{itemId}/Mine/Rating` | Clears the stars; a row without a review is deleted. |
| `PUT /UserRatings/Items/{itemId}/Mine/Review` | `{"Text": "…", "IsSpoiler": false}` and at most one precondition: `IfReviewUpdatedUtc` (409 when the text or spoiler flag changed since), `IfNoReview: true` (409 when there is one) or `IfUpdatedUtc` (409 when anything changed); two is a 400, none replaces. Only white space and invisible format characters: 400 "A review needs some text." |
| `DELETE /UserRatings/Items/{itemId}/Mine/Review` | Clears the review; a row without stars is deleted. |
| `DELETE /UserRatings/Items/{itemId}/Mine` | Deletes the rating and review (204). |
| `GET /UserRatings/Mine/Inaccessible?startIndex&limit` | The user's rows the item routes can't reach now (an item they can't open, a kind no longer rateable, all while ratings are off): `RatingId`, `Stars`, `TextSnippet` (120 characters), times; nothing about the item. |
| `DELETE /UserRatings/Mine/{ratingId}` | Deletes an own row by id, no item lookup, whatever the settings (404 "No rating of yours has that id." otherwise). |

- **One 404** ("This item wasn't found, or it can't be rated.") for an item the user can't open, one that doesn't exist
  and an unrateable kind, so existence doesn't leak.
- `WhoCanRate` leaving a member out: they read everything, get 403 "Rating is limited to selected members." on a create
  or edit, and may still clear and delete their own rows. `RequirePlayedToRate`: 403 until played, 503 when that can't
  be read. A member's own rows are always theirs (`Mine/Inaccessible`, `DELETE Mine/{ratingId}`).
- Read-only: writes 503 before the item lookup, reads go on. Busy over 5 s: 503 within about 5 s, queued writes too.
- **A write answers the item read again** after the commit; a 403 or 404 there doesn't mean it was refused. If that read
  is a 503, the answer is 200 from the write's own row with `IsPartial` true (`Mine` only).
- **Times** are whole milliseconds, so `new Date(mine.UpdatedUtc).toISOString()` matches; any offset or none (UTC) is
  taken.
- **The 64 KB body limit counts bytes**: a 20,000-character CJK review is about 120 KB `\u`-escaped, 60 KB as UTF-8.
- Answers holding a review or rating are `Cache-Control: no-store`, errors too.

**Deleting your own ratings after losing access** (a library taken away, a kind made unrateable, ratings off): they still
count for members who can open the item. List and delete them with your own session's token (1.1's Top Rated page will
list them):

```sh
curl -s -H 'Authorization: MediaBrowser Token="<your token>"' http://<server>/UserRatings/Mine/Inaccessible
curl -s -X DELETE -H 'Authorization: MediaBrowser Token="<your token>"' http://<server>/UserRatings/Mine/<RatingId>
```

## The admin API

`/UserRatings/Admin/*` needs an administrator or an API key. `GET /UserRatings/ClientScript` and `GET
/UserRatings/AdminScript` are anonymous; `POST /UserRatings/Diagnostics/ClientHealth` takes any signed-in user.

| Routes (under `Admin/`) | Who | Where |
|---|---|---|
| `GET Diagnostics`, `POST Detached/Seen`, `POST Held/Detach`, `GET`/`POST Settings` | admins | [Architecture](#architecture), [The library lifecycle](#the-library-lifecycle), [Settings](#settings) |
| `GET Reviews`, `DELETE Reviews/{ratingId}`, `GET ModerationLog` | admins, scoped | [Moderation](#moderation) |
| `POST Users/Remap`, `POST Users/ApplyPolicy` | every library | [Deleted and unknown users](#deleted-and-unknown-users) |
| `GET`/`POST Export`, `GET Export/JellyfinEnhanced`, `POST Import`, `POST Import/JellyfinEnhanced`, `Jobs…`, `Snapshots…`, `POST Data/StartEmpty` | every library | [The Data tab](#the-data-tab) |
| `GET Detached`, `POST Detached/{id}/Relink`, `DELETE Detached/{id}` | admins (empty, 404 without every library) | [The Data tab](#the-data-tab) |

"Every library" means an admin with `EnableAllFolders`, or an API key; another admin gets `403 AllLibrariesNeeded`, whose
detail says how to get it (the account's Access tab, or an Access Roles role giving all libraries). Parental limits don't
apply to admins' lists. The OpenAPI schemas are the 36 `UserRatings*` ids `ApiConventionTests` pins (5 of them
Jellyfin's `QueryResult` of a DTO).

## Moderation

Admins moderate rows on items they can open (detached rows only with every library); API keys every row.

- `GET Admin/Reviews?userId&itemId&since&hasText&state&searchTerm&startIndex&limit`: every rating and review, last
  changed first, hidden authors', former members' and unknown ids' included; `state` `All`, `Attached`, `Detached`;
  `searchTerm` (≤ 200 characters) matches author, item or series name and review; 50 a page (1 to 200). An API key's
  unsearched list is paged in SQL; an admin's is filtered, their item checks cached a minute (by account version,
  libraries and `EnableAllFolders`); a removal checks afresh.
- `DELETE Admin/Reviews/{ratingId}?textOnly&reason&dryRun&ifPreview`: **Remove text only** (default; a rating without
  stars goes; 409 without a review) or **Remove rating and review** (`textOnly=false`, a `Moderator` tombstone). The dry
  run (default) answers `RemovesStars`, `RemovesReview`, `DeletesRating`, `ArchiveLines` and a `PreviewToken` (`m1-` and
  20 hex digits); the real run needs it as `ifPreview` (400 without; 409 `Changed` if the row changed, checked again in
  the write). `reason`: `Spoiler`, `Offensive`, `Spam`, `OffTopic`, `Requested`, `Other`. It answers 200 with `LogId` and
  `SafetySnapshot`, takes the `moderate` snapshot, writes one log row (`RemoveText` or `RemoveEntry`: ids, reason,
  counts), takes the archive lines and checkpoints ([Privacy](#privacy-what-goes-when)). 404 for none or not yours; 503
  while read-only or busy (the dry run still answers read-only).
- `GET Admin/ModerationLog?startIndex&limit&action`: newest first, names read now (a deleted admin is "Former member").
  An API key sees all; an admin only rows whose items (`ItemId`, a Relink's `FromItemId`) they can open, rows about a
  title no longer in the library only with every library, and rows about no item (users) always.
- **The Moderation tab** (in the `AdminScript`): the list with search, filters, **Only** chips and paging, and the log (25
  a page, by action). **Remove…** runs the dry run, takes an optional reason and sends the token; a 409 reads the row
  again and shows it before a new dry run. **Remove…** is also on the details page, on others' rows for an admin
  (`Can.Moderate`), in every layout.

## The Data tab

`Services/RatingsData.cs`, `Import/AdminJobRunner.cs`, `Import/RatingsEnvelope.cs`, `Import/ImportRowRules.cs`,
`Web/admin/js/30-data.js`. Export, import, jobs, snapshots, Restore, Start empty and the unknown users' tools need every
library ([The admin API](#the-admin-api)); for another admin the tab hides them and says why. Every answer that holds
data is `no-store`.

- **Jobs** (`AdminJobRunner`): one at a time (`409 JobRunning`); Restore, Start empty and the daily maintenance hold the
  runner too. The last ten are kept with phase, counts and report (`GET Admin/Jobs`, `GET Admin/Jobs/{id}`). `DELETE
  Admin/Jobs/{id}` stops one before it writes (`409 JobWriting` after, so an import is never half applied) or deletes a
  finished export's file. `GET Admin/Jobs/{id}/File` is an export's file, kept an hour.
- **Export** (`POST Admin/Export`, a job; `GET Admin/Export` at once): the suite's envelope (`jellyfin-plugin-export/1`;
  [kit/README.md, "Users and data"](../../kit/README.md#users-and-data-users-export-and-import-in-storage)) with every
  rating and review, the tombstones, former members, import ledger, moderation log and archive, named
  `userratings-export-yyyyMMdd-HHmmss.json`. The tab fetches it with the session's authorization (no token in a link).
- **Import** (`POST Admin/Import?mode=Merge|Replace&mapUsersBy=Id|Name&dryRun=…&ifPlan=…`, the file as the body):
  - At most **100 MiB of the body's own bytes**, declared or chunked; more is `413 TooLarge`. The body is read as bytes
    whatever its `Content-Type`.
  - Always a dry run first; its `PlanToken` covers the file and every row's decision. The real run needs it (400
    without) and is `409 Changed`, nothing imported, when decisions changed. An `import` snapshot with a copy of the
    archive comes first.
  - **A tombstoned rating never comes back**, nor one the import ledger maps to a rating deleted here
    (`SkippedDeleted`, `PreviouslyPurged`). **A deleted member's ratings are never made again**, by id or by name.
  - **Merge** keeps the newer copy and leaves the log alone. **Replace** keeps the tombstones and former members recorded
    here and takes the file's ratings, log, ledger and archive.
  - A row whose member isn't here is skipped; a missing title is stored detached. **A bad row is left out with its
    reason** (`Unusable`, the first 100 in `UnusableRows`). Names lose control characters (a former member's name also the
    bidi controls); reviews are normalised as a member's are.
  - **This server's own export** is logged `"OwnServer"`. A copied `ServerId` only makes restores keep more deletions.
- **Snapshots and Restore** (`GET Admin/Snapshots`, `POST Admin/Snapshots/Restore?name=`, the kit's recovery). Restore
  takes a safety snapshot and holds the runner.
  - **Everything since the snapshot is undone, except the deletions made on this server since**, which stay deleted (a
    member's, moderator's, policy's or Purge's deletion, a review removal or clear, a member deleted since:
    `DeletionsKept`, `ReviewRemovalsKept`, `FormerMembersKept`). So a `moderate`, `purge` or `user-deleted` snapshot can't
    undo its own deletion.
  - **An import is undone whole**: its tombstones and former members carry its origin, so restoring its snapshot or an
    earlier one brings back what it deleted.
  - A damaged database's restore (no safety snapshot) and a restore from before a Start empty can't tell, and say so
    (`DeletionsError`). A damaged database is never snapshotted: Restore answers `409 Damaged` and the tab offers the
    damaged database's restore and Start empty.
  - A pre-operation snapshot puts its archive copy back too.
- **Start empty** (`POST Admin/Data/StartEmpty`), beside Restore while damaged: quarantines the database, starts an empty
  one, keeps every snapshot and the archive; `409` on a sound database or during a job. The banner after it says when and
  why (`RecoveredEmpty`).
- **Unknown users**: **Remap by name…** and **Apply the deleted-user policy…** for one or all.
- **The detached list** (`GET Admin/Detached`, up to 50 a page; `POST Admin/Detached/{id}/Relink` with `ItemId`;
  `DELETE Admin/Detached/{id}`): member, stars, when and why, and up to five candidates. Relink (`409 AlreadyRated`);
  Purge for good (tombstone, archive lines, a `purge` snapshot). No bulk purge. Also the held ratings with **Detach now**.
- **The tab** asks before every change (the kit's dialog), keeps the focus when a list is read again, wraps at 375 px
  with 44 px controls, and is reachable with jellyfin-web's D-pad.

## The web client

The kit's `index.html` middleware adds one `<script data-jfur-plugin="1.0.2.0"
src="../UserRatings/ClientScript?v=1.0.2.0-<mvid8>" defer>` per page ([kit/README.md, "Web
client"](../../kit/README.md#web-client-web-webembeddedscript)). The script is `kit.js` plus `Web/js/` `00-strings.js`
(every word, the kit's `t()` table), `10-selectors.js`, `20-api.js`, `30-stars.js`, `40-details.js`, `50-chip.js`,
`90-main.js` and `Web/css/userratings.css`, and starts `window.__jfkit.jfur`. Every review, user and library name is set
as text in its own element with `dir="auto"` and `unicode-bidi: isolate`. Classes are `jfur-`; no JE 12.14 style rule
or id touches ours.

**Anchors** (`Web/anchors.json`, checked on each `viewshow`): `detailPage` and `detailContent` (without them, no
section), `sectionBefore` (the cast or similar section; else appended), and the optional `miscInfo` (the chip),
`detailButtons` and `detailButtonsBefore` (the Rate button). JE's Reviews section is the kit's presence probe
`kit.je.reviews`.

**The details section** (`40-details.js`) goes before the cast (below the child list on Series, Season and MusicAlbum
pages) on an item the member can open and rate. It reads Capabilities (kept 30 s per user) and `Items/{id}`; a 404, a
403 or `Enabled` false shows nothing until the next view.
- A disclosure title (`SectionExpandedByDefault`; on TV always open); the summary (the average to one decimal, counts, a
  ten-bucket histogram, "Not enough ratings yet" under `MinRatingsForAverage`, other copies' averages).
- "Your rating" (the star widget), **Clear**, **Write a review**, **Edit** and **Delete** (destructive ones ask); the form
  (`MaxReviewLength`, a live counter, "Contains spoilers"). Others' rows with "Show more"; **Sort**; **Load more** only
  when pressed, never on scroll.
- **Spoilers**: "Contains spoilers · Show spoiler" over blurred bars with no text, since a CSS blur over real text would
  still be found, read, copied and translated; the text is made only on Show spoiler.
- **Writes** go through one queue in order. The review's precondition is its own time when the form opened
  (`IfReviewUpdatedUtc`, or `IfNoReview`), so a rating saved elsewhere never makes it a 409 and another window's review
  is never written over. The 409 offers **Reload** (the text written kept beside the form) and **Overwrite**.
- **Answers**: 503 or none keeps everything, with **Try again**; a write's 404 or a read's 403 removes the section; 409,
  400 and 413 keep the form. **`Can.Reason`**: `NotARater` read-only stars, "Rating is limited to selected members.";
  `NeedsPlay` a disabled slider, "You can rate this once you have played it."; `ReadOnly` "Ratings can't be changed right
  now.". Clear and Delete stay for `NotARater` and `NeedsPlay`.
- **Drafts** are saved under `jfur:draft:<userId>:<itemId>` (the kit's `storage`) and put back by the next form (with
  **Discard**); cleared by a save, Discard, sign-out, session expiry or after 30 days; never sent.

**The chip and the Rate button** (`50-chip.js`): the chip ends the media-info row ("Members" and "4.2/5 (7)", named
"Member average 4.2 out of 5, 7 ratings") with `ShowAverageChip` and an average, and comes back when jellyfin-web
rewrites the row. The Rate button (`kit.detailButton`) goes before More: `Always`, `Never`, or `Auto` (default) on
Series, Season and MusicAlbum pages and where JE draws its Reviews section; it expands the section and focuses the
slider. On TV, JE 12.14's closed Reviews section kept the D-pad in the buttons row: the kit's `kit.je.tvSectionJump` is
registered for its focus half only, as in Bookmarks 1.2.

**The star widget** (`30-stars.js`, `createStarSlider`): one tab stop, `role="slider"`, `aria-valuetext` "3.5 out of 5
stars" or "Not rated"; 0.5 to 5 in halves, 0 only through Clear. A click sets the half star under the pointer; Right/Up
+0.5, Left/Down −0.5, PageUp/PageDown ±1, Home, End, Enter and Space save at once (Delete, Backspace, Escape and digits
are left alone: Back on TV, digits are JE's); touch taps, scrubs sideways and scrolls vertically (`touch-action: pan-y
pinch-zoom`); on TV Left/Right are always consumed, Up/Down never, OK saves. It saves 600 ms after the last change, one
save in flight ("Saving…", "Saved: 3.5 stars"). The accent fill is darker in the Light and Apple TV themes, for 3:1;
forced colours and `prefers-reduced-motion` are honoured.

**The settings page** (Dashboard → Plugins → *User Ratings*; not in the menu): **Settings | Moderation | Data |
Diagnostics**, the kit's warning banners above (read-only, restored or started-empty database, Ambiguous ratings, JE's
reviews on). It reads Diagnostics each time it is shown; the Settings and Moderation tabs come from the `AdminScript`.

## Moving from Jellyfin Enhanced's reviews

Jellyfin Enhanced (JE) 12.x has its own **User Reviews**: 1 to 5 stars and a text per member and title, in
`plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced/reviews.json`, keyed `<user id>:<movie|tv>:<TMDb key>` (the
film's or series' id, `<id>:s<n>` for a season, `<id>:s<n>:e<m>` for an episode; JE 12.14 writes the whole key into each
review's `TmdbId` too). A season or episode is found under its series by its numbers; kept detached, it attaches once the
series gets its TMDb id. User Ratings **only reads** JE's files and settings (the kit's [Jellyfin Enhanced
interop](../../kit/README.md#jellyfin-enhanced-interop-interop-kitshortcuts)); the admin turns JE's reviews off in JE.

**The import** (Data → *Import from Jellyfin Enhanced*; `POST /UserRatings/Admin/Import/JellyfinEnhanced`, every
library) is a data job: a dry run with a `PlanToken`, then the real run with `ifPlan` (409 `Changed` otherwise, no
snapshot: the plan is compared first under the writer lock); an `import` snapshot, whose restore undoes it. No
`reviews.json`: 404 `JeDataNotFound`; another shape or invalid text (not UTF-8, a lone escaped surrogate): 400
`UnsupportedFormat` with the reason. Options:
- **Copies**: *First* (default) attaches to one copy (one its member can see, then the oldest); *All* rates every copy,
  and a later *All* adds the copies a first run left (nothing beside a rating still detached: relink it first).
- **StoreUnmatched**: a title this server lacks is kept detached (`Unmatched`) or left out.
- **RecreatePurged**: a review whose rating was purged here is left out (`PreviouslyPurged`) or made again.
- **UserMap**: JE members who aren't users here (`UnknownUserIds`) are skipped (`UnknownUser`) unless mapped.

The rules:
- Stars round to the nearest half (`Adjusted`); 0 is JE's "no rating" (a review without stars, or `Empty`). Text up to
  20,000 characters (`MaxReviewLimit`; longer is `Invalid`), never a spoiler (`SpoilerNote`). JE's times are kept unless
  ahead of this server's clock (`Clamped`).
- Each key goes into the import ledger (source `JE`): the same file again is `AlreadyImported`, a later JE edit `Updated`
  unless changed here since (`SkippedNewer`). **A rating a JE import made and someone deleted never comes back from that
  entry** (`SkippedDeleted`, `PreviouslyPurged`); without a ledger row, a deletion at or after JE's time keeps it out. A
  Replace keeps those ledger rows and joins the file's to them.
- Against a member's own rating the newer wins (`ConflictKeptLocal`, `ConflictTookJe`); two keys of one member and title
  keep the newer (`DuplicateInSource`); a body that disagrees with its key loses (`KeyMismatch`).

The report never holds a review's text: the counts and outcomes, JE's field names (`SourceFields`, at most 50, cut to 64
characters), what was left out and why, the first 20 entries, and JE's review settings (with **Mirror review ratings to
Jellyfin** on, the import still reads only `reviews.json`, so nothing counts twice: `MirrorNote`; the translation
service's URL and key are reported as set or not, never their values).

**The switch-over checklist** (Diagnostics `JellyfinEnhanced.Overlap`) reads JE 12.14's settings each time. Required
off: **Enable User Written Reviews** (`ShowUserReviews`), **review translation** (`ReviewTranslationEnabled`, URL and key
cleared), **Mirror review ratings to Jellyfin**, **Show recently reviewed**. Optional: the TMDB reviews section
(`ShowReviews`, which traps the TV D-pad) and the poster rating. **Use the same hiding rules** copies JE's two hiding
rules into *Leave out hidden users' ratings* and *Leave out disabled users' ratings*. While JE's settings can't be read
every item is *Unknown*, never done, and the tab names the unreadable files with a plain reason, never a path. Steps:
dry run, map members, Import, check a few titles, turn off each *To do* in JE; the banner goes.

### Going back to Jellyfin Enhanced

**Export for Jellyfin Enhanced** (`GET /UserRatings/Admin/Export/JellyfinEnhanced?leaveOutRestricted=true|false`,
`Import/JellyfinEnhancedExportWriter.cs`, every library) sends the ratings as JE 12.14's `reviews.json`, built in memory
(`no-store`); nothing is written on the server or in JE's folder.
- **Format**: `{"Reviews":{...}}`, keys as JE's, sorted; each entry `UserId`, `TmdbId` (the whole key, `"1396:s1:e2"`),
  `MediaType`, `Content`, `Rating` (half stars kept, `null` without), `CreatedAt`, `UpdatedAt`. Seasons and episodes take
  the series' id and numbers. Archived ratings are included.
- **Left out**, counted by reason: `NoTmdbId`; `NotInJe` (music, books, home videos…); `FormerMember`; `Detached` and
  `Archived` ones not from JE (they'd come back as a second copy; JE's own stay and come back `AlreadyImported` /
  `AlreadyImportedArchived`); `RestrictedLibrary` while **Leave out reviews of titles that not every member can see** is
  chosen (default): a title some user, disabled ones too, can't open; detached and archived ones by stored path (in such
  a library or none: out; no path, a JE review of a title never here: in). `leaveOutRestricted=false` writes them all.
- One member's rating in two libraries is one key: the newer is written, the others counted as merged. A review over
  JE's 2,000 characters is written whole and counted (JE shows it but won't save it).
- The `X-UserRatings-*` headers count what was exported, skipped, merged and over JE's limit; the tab says the same in
  words.

> **Privacy: library restrictions don't carry over.** JE 12.14 serves a title's reviews by TMDb id (`GET
> /JellyfinEnhanced/reviews/{mediaType}/{tmdbId}`) to any signed-in user, checking no library access and not even its
> own **Enable User Written Reviews**, so every member can read every review in the file **from the moment it is in
> place**. Only JE's **Hide reviews from hidden users** and **Hide reviews from disabled users** apply. Check both
> **before step 3, while the server still runs**, and keep the default *Leave out* choice unless every member may read
> every review.

**To go back** (Windows default paths):
1. Data → *Import from Jellyfin Enhanced*: dry run and Import, to bring in what was written in JE since the switch.
2. **Export for Jellyfin Enhanced**, read what it left out, keep `reviews.json`. While the server runs, set and save
   JE's two hiding rules.
3. Stop the server.
4. In `C:\ProgramData\Jellyfin\Server\plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced\`, keep JE's
   `reviews.json` (say as `reviews.before-user-ratings.json`) and put the exported file in its place.
5. Start the server; turn **Enable User Written Reviews** back on, with whatever else the checklist turned off.
6. Uninstall User Ratings (and restart), or untick *Add User Ratings to the web client*. Kept as it is, both review
   interfaces show and new ratings here never reach JE; the file imported back later changes nothing (`AlreadyImported`,
   `ConflictKeptLocal`).

A review only in JE's copy (written after step 1, or deleted here) isn't in the file, hence step 4's copy. JE has no
spoiler flag. Checked in the `je` sandbox: JE showed every exported review, a film, a season and an episode by their
whole keys and a rating made only here.

## Known limitations

Decisions a review accepted as they are:
- **Moderation**: the reasons are six fixed codes (no free text, so the log holds nobody's words); a removal writes no
  Activity Log entry (the Activity Log is readable by every admin whatever their libraries); a 409 `Changed` found inside
  the write leaves its `moderate` snapshot (it expires in 7 days); `searchTerm` is in the query string, so a reverse
  proxy's access log may keep it; a signed-in admin's item checks are cached a minute, so a library taken away can still
  show its rows for up to a minute; archived ratings aren't in the list.
- **Settings**: a value of the wrong JSON type is MVC's 400 naming `$`, not the setting; `POST
  /Plugins/{id}/Configuration` still takes the whole file unchecked (the reader holds values to their ranges); a
  restricted admin may still set every setting, `ModerationLogDays` among them, since Jellyfin gives every admin its
  plugins' settings.
- **Data**: a tombstone or former member an import made before schema 4 reads `Local`, so restoring that import's
  snapshot keeps it; a member's own clear that an import then matched is undone with the import; the deleted-user policy
  and a moderator's removal write the archive outside the job runner, so the maintenance doesn't wait for them; chunks
  under about 1 KiB could take a body just under 100 MiB over the route's 1 MiB room for framing; stored names aren't
  cleaned again.
- **Snapshots**: the pre-operation snapshots' 7-day expiry runs at the daily snapshot, which doesn't run while the
  database is read-only.
- **JE import**: the dry run plans under the database's writer lock, as the real run does, so members' writes wait (at
  most 5 s, then a 503). Measured: 5,000 entries about 0.7 s, 20,000 about 3 s; the real run about twice as long. With a
  very large `reviews.json`, run both when few members are rating.
- **10.11.5**: *Disable* on the Plugins page deletes the plugin's folder at the next start.

## Tests

`dotnet test plugins/UserRatings/tests/Jellyfin.Plugin.UserRatings.Tests` (and Repo.Tests over every plugin). It also runs
the node tests (`Web/*.test.mjs`) in the kit's fake page against the built scripts; `WebTests` pins each suite's test
count.

| Area | Tests | Covers |
|---|---|---|
| Identity, API surface | `PluginIdentityTests`, `Api/ApiConventionTests`, `Api/PublicBoundaryTests` | build.yaml, project and assembly agree; `KitUseSqlite` and Microsoft.Data.Sqlite 9.0.11.0; the pinned schema ids and routes; only the scripts anonymous; no SQLite type in the plugin's own code. |
| Member API | `Api/UserRatingsApiTests` | The controller behind Jellyfin's pipeline (the kit's `WebHostHarness`) on the real services and database: API keys, the one 404, preconditions and time forms, `IsPartial`, read-only and busy 503s, the switches. |
| Registration | `PluginServiceRegistratorTests` | The ABI guard first; services, hosted database, maintenance steps and gate; only the ABI status on 12.x. |
| Store, schema | `Storage/*Tests` | Round trips, the 409, tombstones and ledger; every migration to version 4; read-only on a newer schema; snapshots, damage and recovery with a hot WAL; units of work under concurrency; 100 items' summaries on 50,000 ratings under 50 ms. |
| Domain, services | `Domain/*`, `Services/*` (not Data) | Star and text rules; the visibility matrix; cache races through deterministic hooks; the order of checks; cache ghosts. |
| Settings | `ConfigurationTests`, `RatingsSettingsTests`, `Api/UserRatingsSettingsTests`, `ModerationLogRetentionTests` | Defaults, the XML round trip, fallbacks, the Settings API, retention. |
| Lifecycle | `Lifecycle/*Tests` | Real store and kit monitor, verifier, resolver and journal over a fake library and real folders: renames, moves, ghosts, holds and the guard, the archive, merged versions, deleted users, remap, residue. |
| Moderation, Data | `Api/UserRatingsModerationTests`, `Services/RatingsData*Tests`, `Api/ImportBodyTests` | Scoping, tokens and races, residue after the checkpoint; jobs, import rules and bad rows, 100 MiB, restore rules, Start empty, the JE import and export. |
| Web | `Web/WebTests`, `stars`, `details`, `configpage`, `moderation`, `settings`, `data` `.test.mjs` | `textContent` only, `node --check`, anchors and load order, the Settings tab against `RatingsSettingsFields.cs`; the widget, section, chip, tabs and dialogs. |

## In a sandbox

Every probe and webcheck, its options and what it needs, and the full pass are in [scripts/README.md, "User Ratings
checks"](../../scripts/README.md#user-ratings-checks). The usual run:

```powershell
./scripts/dev-server.ps1 -Name default -Background          # builds and deploys every plugin
./scripts/Initialize-Sandbox.ps1 -Name default
node scripts/index-probe.mjs --sandbox default              # one data-jfur-plugin tag, the ClientScript
node scripts/webcheck.mjs --sandbox default --steps kit-registry.json             # window.__jfkit.jfur, versions
node scripts/webcheck.mjs --sandbox default --steps userratings-settings-page.json    # the four tabs
# the screens: userratings-settings-*, -details-*, -details-chip-*, -stars-* (desktop, tv, touch or phone)
# the probes: userratings-api (--readonly-drill), -busy, -visibility (--details), -details, -settings, -data, -lifecycle
./scripts/Stop-Sandbox.ps1 -Name default
./scripts/dev-server.ps1 -Name je -Port 8108 -Background     # Jellyfin Enhanced copied from the installed server
node scripts/userratings-je-import-probe.mjs --sandbox je --webchecks   # the JE import and checklist
# beside JE: userratings-stars-je, -details-je, -details-chip-je
./scripts/Stop-Sandbox.ps1 -Name je
```

The `userratings-stars-*.json` webchecks mount the served widget through
`scripts/webchecks/probes/userratings-stars-probe.js` with a stand-in for the save (the section's reads answered 404).
The `userratings-details-*.json` webchecks drive the real section through `userratings-details-probe.js`, which logs
requests and answers one in its place only when a step asks (a 503, a 404 after the write, `IsPartial`).

### How to verify a change

1. Build in Release; run the test project and `tests/Repo.Tests`, one at a time.
2. In the `default` sandbox: `index-probe.mjs` and `kit-registry.json` always, the webchecks of the screens touched
   (desktop, TV, touch, phone), and the probes of the routes touched (member API, database or lifecycle: the API, busy,
   visibility and lifecycle probes; Settings or Data tabs: their webchecks and the settings or data probe).
3. Near Jellyfin Enhanced: the `je` sandbox's webchecks and the JE import probe.
4. Every step passes; the server log has no `[ERR]` or `[FTL]` of our plugins; results go through `scripts/redact.mjs`
   before they are kept or shared; stop each sandbox.
