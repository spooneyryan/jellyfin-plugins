# Smart Search

A Jellyfin plugin that adds an **Advanced Search** page: find movies, shows, episodes, music videos, home videos and
collections by rules over their title, year, library, type, genres, ratings, watch state and more, combine rules in
groups, and save searches, private or shared. Every search runs as the signed-in user, over the libraries they may open.
A saved search can drive an **auto-collection**, a Jellyfin collection kept equal to its results. This is its technical documentation; the user guide is [README.md](README.md), the release notes the `changelog` in
[build.yaml](build.yaml).

Version **1.1.2** (release tag `smartsearch/v1.1.2`; earlier releases 1.1.1, `smartsearch/v1.1.1`, 1.1.0,
`smartsearch/v1.1.0`, and 1.0.0, `smartsearch/v1.0.0`), for Jellyfin **10.11.5** (`net9.0`). Development builds after
it are 1.1.2.`<n>`.

- **In 1.0.0:** the page with its basic and advanced forms, results, links and saved queries; the header button; the
  settings page with export, import and snapshots; the engine and its lookups, as described below.
- **New in 1.1.0:** the auto-collections: definitions, runs, triggers, the guard, manual edits, the reference guard over
  the saved queries, the Auto-collections tab, the note on a collection's page, export and import, Diagnostics.
- **New in 1.1.1:** the auto-collections' default poster (`AutoCollectionDefaultImage`), and the plugin's image.
- **Not in 1.1.0:** the `People` and `Collection` fields and stream facts (audio and subtitle languages, HDR), regular
  expressions (built, off and not listed), reordering rules, `Explain`, the native search page's link; for
  auto-collections, archiving detached pins and excludes, narrowing a library change to the definitions over that
  library, and aggregated genres.

**What it changes on a server**: one script tag and a JSON block added to the web client's `index.html` as the server
sends it (nothing is written to the web client's files), its settings file
`plugins\configurations\Jellyfin.Plugin.SmartSearch.xml` and its data folder `data\smartsearch` (the saved queries,
their snapshots, the auto-collection definitions and run state, and the kit's user records). Results and value indexes
live in memory. It never writes to a library; the auto-collections write only the collections they made (BoxSets with
the `SmartSearch` provider id).

## How it works

- **The plugin on the kit:** the settings page (the kit's common fields, *Enable the search page*, *Show the header
  button*, the item kinds users can search, and the engine's four limits), and the page *Advanced Search*, reachable
  under **Library tools** in the menu (`#/jfss-search`), from the header button, or through Plugin Pages when the kit's
  page host is `Auto`.
- **The header button** (`Web/js/70-entrypoints.js`): an "Advanced search" button (`kit.headerButton`, icon
  `manage_search`) right before jellyfin-web's own search button in the legacy header, one of the plugin's two
  jellyfin-web anchors (`headerSearch` in `Web/anchors.json`, found through `Web/js/10-selectors.js`, view `header`). It copies that
  button's `hide` class through an attribute observer, so it shows exactly when it does (signed in, not on the login
  page, gone after signing out), is checked again on every scan and `viewshow` (a rebuilt header gets it back, one
  button only), and opens `kit.pages.href('search')`. It is there only while the head fragment lists the page as on
  (`kit.pages.enabled`) and `ShowHeaderButton` is on, which reaches the client in a JSON block after the head fragment
  (`SmartSearchKit.EntryPointsFragment`, `data-jfss-entrypoints`). Without the anchor there is no button and no error;
  the drawer link remains, and the kit's health check (on `viewshow` and when the user becomes known) warns once in the
  console and sends one report. In the experimental layout jellyfin-web keeps the legacy header in the page, hidden,
  so the button stays hidden in it and nothing is reported.
- **The query model:** a saved or sent query is JSON version 1: a scope (item kinds, libraries), a tree of All/Any
  groups and rules, a sort and a limit. The reader refuses what `QueryLimits` limits (64 KB, depth 4, 50 rules, 100
  values, 200 characters, years 1800–2100, …) with one error per problem and its JSON path; the canonical form and its
  SHA-256 identify equal queries; a property the model doesn't have, or a name given twice, is refused; a query of a
  newer version is unsupported as a whole (version 1 is the only one so far, so the migrator has nothing to upgrade
  yet). A query is written with its text as it is, and the 64 KB limit holds for the written form too. The model is
  the body of `/Search` and `/Validate`.
- **The field catalogue:** 33 fields and 15 sort keys, each field with its in-memory semantics: text compared without
  case or accents (as Jellyfin's own `CleanName`), the path ignoring case only, numbers and dates where an empty value
  fails every positive rule and passes every negative one, ratings as the database stores them, a date without a time
  as a whole day of the server's calendar (a time with a zone as an exact instant), `InLast` counting back from now,
  lists, choices (`Container` as a list of tokens), and the watch state of the user (no play record means unplayed, not
  a favourite, not in progress). A rule is false on items its field doesn't apply to (`SeriesStatus` on a movie).
  A stored `CommunityRating` of 0 is a rating, not an empty one, as in core's own filter (`MinCommunityRating` 0
  returns it), although core's DTO leaves it out and the web client shows no rating for it (the field's note says so).
  `GET /SmartSearch/Fields` serves the catalogue to the rule builder; the administrator-only field `Path` only to
  administrators. Regular expressions (`Matches`) are built but wait for a query-level cancellation: they are off and
  not listed. The `Runtime` field counts a runtime under half a minute as 1 minute, as the web client shows it.
- **The planner**: turns a query into the user-aware query Jellyfin runs in SQL
  plus the rules left to check in memory. A filter is pushed to SQL only when it keeps every match (exactly, or a wider
  set that is checked again), never wider access than Jellyfin's own `/Items` for the same user; an `Any` group pushes
  only what all its branches share, a negated group nothing; a `Library` rule only counts the libraries the user can
  open; a `Series` rule pushes only ids that are series (an episode's ancestors also hold its season, folder and every
  library over that folder); a `SeriesName` rule's names and a `Studios` rule's studios are looked up in the user's own
  scope (the episodes and items they can see), so a name only hidden items carry plans exactly like a name nobody has;
  box sets, and searches without a user, are checked in memory, since `/Items` drops a box
  set the user can't see through its children and only a user's query groups merged versions. Names and genre, tag or
  studio values whose stored clean form is out of date (a database carried over from 10.8) are found at startup and
  after each scan by the clean-form probe, and such values are checked in memory. An explanation of a plan shows how
  many ids a step resolved, not the ids.
- **The engine**: runs a plan and returns a page of results as Jellyfin's `/Items`
  returns them, never an item or a count beyond what `/Items` gives the same user. A plan whose rules SQL checks exactly
  (the **Fast** path) runs in SQL, paged and counted there; each page then goes through Jellyfin's own `IsVisible`
  check, as `/Items` does, so a page can hold fewer items than asked for while the count stays SQL's. Any other plan
  (the **Residual** path) loads the candidates' ids in one query, loads and checks them in memory 1,000 at a time (a
  rule whose value needs another source is fetched only for the items the rest of the query leaves undecided), never
  returns an owned item (an extra or a trailer), keeps one result per set of merged versions and presentation key, as
  `/Items` groups them (the primary when it is in that group and the user may see it), sorts the way SQL sorts on the
  Fast path (the stored columns, text by code point, a missing value first, `SortName` then `Name`), so a query's order
  doesn't depend on its path and matches the library views, breaks ties by sort name then id, and applies the limit.
  Fast pages over equal sort keys keep SQL's order, which Jellyfin doesn't make stable either (documented, not
  changed). Results are cached
  per user for 120 seconds (`ResultCacheSeconds`, at least 10), so the next pages come from the same result, and hold at
  most `MaxCachedResultIds` ids together (2,000,000; the least recently used go first); the library changing,
  the user's access changing, or, for a query that reads watch state, their play data changing ends the entry. A
  search is refused rather than cut short when it would load more than `MaxCandidates` ids (100,000; 400
  `QueryTooBroad`), runs longer than `SearchTimeoutSeconds` (20; 503 `Timeout`), or finds all `MaxConcurrentSearches`
  slots (2) busy (503 `Busy`); a user runs one search at a time, so their next one waits for it within the same 10
  seconds; the auto-collections will have a slot of their own. On a library of about 20,000 items, a
  search checked in memory returns its first page in about 1.1 s and the next pages from the cache in a few
  milliseconds; it holds about 150 bytes a match (measured to 300,000 synthetic rows).
- **Loading items:** on 10.11.5 an item loaded without `EnableUserData` reads as never played and not a favourite, and
  one loaded without `EnableImages` has no images; so the engine loads candidates with user data only when a rule or
  the sort reads it, and builds every page from items loaded again with both.

- **The endpoints** (table below): `/Search` and `/Validate` read their body strictly (the query as a saved one is
  read; a property the request doesn't have, or a name given twice, is a 400 `InvalidRequest`), and answer a query's
  errors with each one's path inside the query (400 `InvalidQuery`, 403 `AdminOnlyField`, 413 `TooLarge`, each with
  `errors`); a too broad search adds `candidateCount` and `kinds` to its 400. A library the query names that the user
  can't open, or that doesn't exist, is the warning `LibrariesUnavailable`, the same for both. The setting
  `AllowedItemKinds` narrows the kinds a query may name. While *Enable the search page* is off, every one of them answers
  403; the user, API key and page checks come before the body or parameters are read. `/Validate` returns `Paths`
  (the plan's path) to administrators only: a plan depends on what the server holds beyond what the user can see.
  Problem `errors` items are camelCase (`path`, `code`, `message`), like the problem's own members; lookup parameters
  that don't bind are a 400 `InvalidRequest` in the same shape. The lookups answer from what the user can see:
  `/Libraries` the libraries they can open; `/Values/{fieldId}` (Genres, Tags, Studios, OfficialRating, Container)
  each value with the number of the user's visible items of the allowed kinds that carry it; `/Series` the series whose
  name contains the text (2 characters at least). Text matches anywhere, ignoring case and accents; those that start
  with it come first.
- **The value index** (`Search/ValueIndexService`) behind `/Values`, `/Series` and the planner's lookups: per library
  folder and parental profile (limit, unrated kinds, blocked and allowed tags), so users of one profile share it; box
  set libraries are left out. One build per key at a time (a second caller waits for it with its own token), at most 2
  at once, each waiting 10 s for a slot and running 30 s at most, else 503 `Busy`; a key whose build ran out of time
  isn't built again for 60 s. Items of the indexed kinds added,
  updated (not images alone, which is also how chapter images are saved) or removed make it stale once no such event
  came for 2 s (30 s at most into a burst); people, seasons and folders never do. **During a library scan the last
  index is served**, so suggestions can lag the scan until its first use after the scan rebuilds it, once; a stale
  index is likewise served while its one rebuild runs in the background. **The planner never waits for a build:** it
  takes an index only when it is built and no event touched it since, outside a scan; otherwise it starts a build in
  the background at most and looks a studio or a series name up itself.

- **The page** (`Web/js`, `Web/css/smartsearch.css`; the kit's page host, cards, rows, pager, notices and playback):
  the scope bar (the kinds `/Fields` lists as toggles, at least one kept; a library from `/Libraries`; missing episodes
  with Episodes on), the basic form (title `Contains`, a year as `Eq`, `Between`, `Ge` or `Le`, genres `AnyOf` with
  suggestions from `/Values/Genres` for the chosen library), and the results (`/Search` 50 at a time, the total on the
  first page only, the next pages through its `ResultToken`; sort and order from `/Fields`' sort keys; Grid or List,
  remembered per browser; Open is a link to the details page, Play the kit's playback). The form becomes one version 1
  query: the scope, one `All` group, one sort key. The search lives in the hash (`kinds`, `lib`, `missing`, `q`, `from`,
  `to`, `genres`, `sort`, `order`, `run=1`; `q` alone runs a title search), after the parameters the host owns (Plugin
  Pages' `pageUrl`) and the page's own (`tab`, `query`, `scope`, `find`), so a link, Back, Forward and a restored view render it again; a
  value the hash says wrongly is left out, and a `where` over 16 KB before it is parsed. A bare page takes the types and library last searched in this browser. The
  query's errors go under their fields by their JSON paths; `QueryTooBroad`, `Timeout` and `Busy` are notices with a
  hint (and Retry). A newer search aborts the one still running. On the TV layout the fields stack, no field takes the
  focus and Load more waits for the remote; under 40em the fields stack and touch targets are 40 px.
- **The advanced form** (`Web/js/35-tree.js`, the tree's logic without the DOM; `Web/js/42-advanced.js`, its editor):
  Basic | Advanced (`mode=adv`, the tree as JSON in `where`, several libraries as a comma list in `lib`). The tree
  holds groups (`All`, `Any`, `None` = `Any` negated, `Not all` = `All` negated; four levels below the top, 50 rules,
  50 children a group) and rules; each rule's field select lists `/Fields` by its groups, a field that doesn't suit
  the chosen kinds marked, not disabled. A field's type and `ElementKind` choose the editor: text, number, date, an
  amount and unit, yes or no, choice buttons, a library list, or chips with suggestions (`/Values/{id}` for the
  chosen libraries; shows from `/Series` after two letters, kept with their id and name); a change of field keeps the
  condition when the new field has it. The checks (`ssCheckTree`) mirror the server's messages by JSON path and run
  before a search and a save; the server's own errors land on the same nodes. Every control has a label and a
  `data-jfss-focus` key, so after each change the tree is drawn again and the focus returns to the same control, or the
  nearest one left. Switching to Basic is offered only for a tree the basic form can hold. A saved query opens in the
  form that can show it: the basic one, else the advanced one, editable, with a library the user can't open counted
  and kept, extra sort keys and `Limit` sent back unchanged; one from a newer version stays read-only (a rule with an
  unknown field is shown locked), an invalid one opens with its errors on the rules. The advanced form reads `Match`,
  the kinds, the sort key and its order ignoring case, as the server does, and compares the query it would send back
  (`ssBuildQuery`) with the stored one, both normalised alike (`ssNormalQuery`: member names and enum values in lower
  case, defaults left out); when they differ (member names in another case, a member it doesn't know) the query opens
  locked, runs as saved, and Save as new duplicates it. Save as new in the advanced form
  asks `/Validate` first. Values are text only (`<bdi>`, control characters as U+FFFD).
- **The settings page's data section** (`Configuration/configPage.html`, administrators): Export (`Admin/Export`, saved
  as the file the server names), Import (a file, `Merge` or `Replace`, owners by id or name: Dry run first, which
  lists added, updated, unchanged and skipped records and `BrokenAutoCollections`; Import only after it, with a box to go on
  despite broken auto-collections, sent as `force=true`) and Snapshots (`Admin/Snapshots`, each with Check restore,
  its dry run, then Restore; a restore refused for broken auto-collections offers the box and Restore again). A forced
  import or restore lists the auto-collections that lost their query. Text only, as the dashboard page checks require.
- **The Saved queries tab** (`Web/js/60-saved.js`): two tabs, Search and Saved queries (`tab=saved`). The list (`/Queries`,
  filter Mine, Shared with me or All in `scope`, a find over name and description in `find`) shows each query's name as
  a link (`#/jfss-search?query=<id>`), its owner ("Shared by …"), Shared, the update time, its use by
  auto-collections, and Needs a newer version or Has errors; the owner's rows have Rename, Duplicate, Share or Stop
  sharing, Copy link (not on TV) and Delete, others' Duplicate and Copy link. A query's link fills the form from it and
  runs it by id (`/{id}/Search`); the header names it, and Save (`PUT` with `IfUpdatedAt`; a 409 offers Reload), Save as
  new… and the owner's Share switch act on it; an edit runs through `/Search` until saved. A query the basic form can't
  show opens in the advanced form (above); one this version can't read opens read-only: it runs by id and Save as new
  duplicates it. A library the user can't open is counted ("1 library not available to you"), never named and its id
  never shown; it is kept while the form's libraries are those saved, and the badge says it is left out once they
  change. The list's use by auto-collections (`AutoCollectionCount`) is for the owner and administrators only. Names and descriptions are text only, isolated (`<bdi>`, or
  U+2068/U+2069 in a sentence), control characters shown as U+FFFD. Delete, Share and Stop sharing ask first; the
  reference guard's answers (`Referenced`, `BreaksAutoCollections`, `AutoCollectionsNeedAttention`, `ManagerRequired`)
  become a question or a notice naming the auto-collections; a refused Save also offers *Detach these collections,
  then save* (the same PUT with `DetachAutoCollections`). `/Fields` says `AllowQuerySharing`, so Share is hidden
  while sharing is off.
- **The Auto-collections tab** (`Web/js/65-collections.js`, `tab=collections`; `edit=<id>` opens one's editor): only
  when `/Fields`' `AutoCollections.CanManage` (administrators and users who manage collections; the server checks every
  call). The list (`/AutoCollections`, read again every 4 s while a row is `Queued` or `Running`, and a toast once it
  ends; otherwise every 30 s while the browser tab is visible and on `visibilitychange` or the window's `focus`, a
  background read leaving the rows, and the focus, alone when nothing changed) filters All, Needs review (`Guarded`,
  `TetherMissing`) or Errors; a row has its status chip, query, members, last change, evaluation user and Mixed audience, Refresh, Edit, Turn on or off (creator or administrator), History
  (`/{id}/History`; Keep these pins what the last change removed) and Delete (the collection kept unless asked). Held
  removals offer Keep these items, Apply removals (confirmed) and Wait (folds the panel until the guard changes); a
  replaced, deleted or unmade collection Recreate (or Create), and a replaced one also Stop managing; a deleted
  creator's, for administrators, Give to this user (an empty choice first, then the administrators and collection
  managers from the server's user list; off until one is picked). The editor
  checks the name as it is typed (`/CheckName`, suggestions to pick), offers the user's queries and others' shared
  ones (copied on save, `DuplicateSourceQuery`), shows the evaluation user only when the query reads watch state
  (`/Validate`), and enables Save only after a preview (`/Preview`) of the form as it is, whose hash it sends.
  **Create auto-collection…** on the Search tab (an unsaved or changed search goes through Save as new first) and on a
  saved query's row opens it on that query.
- **The note on an auto-collection's page** (`Web/js/85-collectionnote.js`): on `#/details?id=<id>`, when `/Ids`
  (read at most every 10 minutes, and again after a 404) lists the item, `/ByCollection/{id}` gives the note, put right
  above the collection's item list (anchor `collectionNoteBefore`, view `details`, checked by the kit's health check
  only on such a page). Managers get Edit (`edit=<id>` on the tab, while the page is on) and Refresh now (the
  note read again every 4 s until `LastSuccessUtc` changes, 3 minutes at most). `ShowCollectionNote` reaches the
  client in the entry points' JSON block (`collectionNote`).

- **Saved queries** (`Storage/`, `Services/SavedQueryService.cs`, `Services/SavedQueryAdmin.cs`): the kit's JSON store
  `data/smartsearch/saved-queries.json` (version 1: `KnownUsers` and `Queries`, each with its owner, name, description,
  `Shared`, the query in the compact form `QueryJson.Write` gives, its canonical hash, created/updated times, the last
  editor and `LastRunUtc`). A name is unique per owner, ignoring case (409 `DuplicateName`); `MaxSavedQueriesPerUser`
  (200) is checked under the writer lock (409 `QuotaReached`); a change must send the `UpdatedUtc` it read as
  `IfUpdatedAt` (409 `IfUpdatedAt` when changed since). **Who sees what:** a user opens their own queries and the
  shared queries of others, an administrator any; another user's private query is a 404 like a missing id; a shared one
  is theirs to open, run and duplicate (as a private copy), never to change or delete (403 `NotOwner`). While
  `AllowQuerySharing` is off, shared queries are their owners' alone and nothing new is shared (403 `SharingDisabled`);
  a query using an administrator-only field can't be shared (403 `SharedAdminOnlyField`), is never another user's to
  see, and can only be saved by an administrator in their own query. **Running** (`/{id}/Search`) reads the stored JSON
  again and goes through `/Search`'s path as the caller (their libraries, parental limits, watch state and right to
  administrator-only fields), never as the owner; the owner's runs note `LastRunUtc`, at most hourly. A query this
  version can't read (a newer version, an unknown field) is listed and opened with `Supported: false` and can be
  duplicated, exported or deleted, but not changed or run (409 `NeedsNewerVersion`); even shared, it is only its
  owner's and administrators' to see, as its fields can't be checked. Owners are named by user name; only
  administrators get `OwnerUserId` (others `IsOwner`), and a deleted owner is "Deleted user". A shared query's
  definition is the owner's: the names and values it holds, and its library ids. Times are UTC, also when a file has
  them without an offset.
- **Deleted users and the kit's user tools**: the store is an `IUserDataOwner`. Under `DeletedUserPolicy` `Delete` (the
  default) a deleted user's private queries that nothing references go, and the `.bak` is written again so no copy is
  left; shared or referenced ones stay as orphans until an administrator gives them to someone (`Admin/Queries/{id}/Owner`)
  or deletes them. `KeepAsOrphan` and `Anonymize` keep every query. Remap by name, the reconciliation and
  `ApplyPolicy` are the kit's; a name the new owner already has gets a number.
- **The reference guard** (`Storage/ISavedQueryReferenceGuard.cs`): every change and delete of a saved query goes
  through it, and owner changes and the deleted-user policy ask it for references (`CountReferences`, lock-free).
  Bulk changes (import, snapshot restore, remap, owner change) run in `BulkChangeAsync`, and `FindBrokenReferences`
  is given the ids they remove and change (`SavedQueryBulkChange`) and answers the references that would break: a dry
  run lists them (`BrokenAutoCollections`), a real run is 409 `BreaksAutoCollections` unless `force=true`, after
  which the auto-collections show what is missing or changed; a real run is refused before it takes its
  pre-operation snapshot (the kit's `JsonImportAdapter.CheckBeforeWrite` for an import). It is given the saved-query
  write as a delegate, so an implementation takes its own lock first and the saved-query store's second. **The lock
  order** is the kit's user reconciler first, then the store of what references queries (the guard's lock), then the
  saved-query store; nothing calls the reconciler or a store that owns users' data while holding a later lock. So
  *Remap by name* checks its plan (the reconciler's dry run) under the guard's lock, releases it, and then runs the
  reconciler, and each store checks its own part again under its own lock. A remap refused part-way says which
  owners it already remapped (`remappedOwners`; they stay so), and running it again remaps the rest. The guard is the
  auto-collection definitions (`AutoCollections/DefinitionReferenceGuard.cs`; its lock is the definitions store's
  gate, so the definitions store is registered, and reconciled, before the saved-query store; the reads take no lock,
  and nothing under the gate waits for a run's gate). Its refusals are 403 `ManagerRequired`, 409
  `BreaksAutoCollections`, `AutoCollectionsNeedAttention` and `Referenced`, each with `autoCollections` (`id`,
  `name`, `messages`). A change of a referenced query: the same query (by canonical hash: a name, description or
  sharing change) always goes through; another needs an administrator or a collection manager (`ManagerRequired`, so
  a creator who lost the flag can't change their collection through the query); it is refused when a following
  definition would stop because of it (an earlier stop, a missing creator or a newer version don't count), asks first
  when one would pause for an evaluation user or drop it, and otherwise answers the definitions it changes
  (`AffectedAutoCollections`). `DetachAutoCollections` (a change) and `detachAutoCollections` (a delete) first switch
  every following definition to a copy of the query as stored, in one write under the gate, without needing the
  flag. Bulk checks compare each definition's check before and after (with a remap's user map), so only stops the
  change causes are listed. An owner change of a referenced query is 409 `BreaksAutoCollections` unless `force=true`
  (the normal way is the auto-collection's `Reassign`). Import and restore tell the auto-collections which queries
  they replaced or removed (`SavedQueryStore.NotifyChanged`, outside the locks), as every other write does, so the
  followers run again: a removed query stops them as `QueryMissing`, a changed one is checked again. **A reference**
  is a definition that follows the query and whose creator owns it (`AutoCollectionStore.ReferencesTo`); one whose
  creator doesn't (a forced owner change, a remap refused part-way, an import) has stopped (`SourceOwnerChanged`),
  holds nothing back, isn't named to the owner and gets no copy on a detach (the single `Detach` is 409
  `SourceOwnerChanged`). A detach-then-save runs the save's name and `IfUpdatedAt` checks under the gate before it
  detaches (`SavedQueryEdit.CheckSave`); only a failed write of the query after the detach leaves the definitions
  detached and the query as it was. **Definitions that couldn't load** (`AutoCollectionStore.DefinitionsUnknown`:
  `ReadOnlyDataUnreadable`, `ReadOnlyDataMissing`, `ReadOnlyMigrationFailed`, or a newer file that can't be read at
  all; the kit then serves an empty document) make the guard fail closed (`ReferencesUnknown`): deletes, query
  changes, owner changes, and imports, restores and remaps that remove or change queries are 409 `StoreReadOnly`,
  forced or not (a dry run says so); a name, description or sharing change passes, and no query goes with its deleted
  owner. A newer version's file that could be read still guards its queries; a query change then passes and the
  followers stay `NeedsNewerVersion`.
- **Export, import, snapshots**: the kit's envelope (`Admin/Export`, `Admin/Import`: a dry run by default, `Merge` or
  `Replace`, owners matched by id or name, queries of owners not found skipped). A query this build reads and finds
  valid is stored as a save writes it (`QueryJson.Write`: the model's member and enum names, whatever casing the
  file had); invalid and unsupported ones are imported as they are; a shared one that uses an administrator-only field comes in unshared; a name its
  owner already has gets a number. Skipped, each with a note: a record without a query or an id, an id the file
  already had, a query over 64 KB, an administrator-only one for a user who isn't an administrator, and those past
  their owner's `MaxSavedQueriesPerUser`. The dry run writes each record as the real run would, so both agree. An owner
  change, an import and a remap refuse (409 `AdminOnlyField`, `QuotaReached`) or skip the same cases. The daily task **Smart Search: Maintenance** (03:30) snapshots the store
  (`SnapshotsToKeep`, 7) and runs the kit's user reconciliation. The export's `Data` also holds `AutoCollections`, the
  definitions document as stored (`AutoCollectionTransfer`; no state); an import reads and checks it first (400, 409
  for a newer version's), imports the queries, then merges the definitions by id under the store's gate only, after
  its own snapshot: never removing one (Replace too), creator, evaluation and last-editor users mapped (an unknown
  creator skips it, an unknown evaluation user is kept), `CollectionId` kept for one already here and cleared for a new
  one (`NotCreated` until `Recreate`, which rebinds a collection already carrying its mark), an imported (added or
  updated) one numbered when its name is taken (the others keep theirs), new ones past `MaxAutoCollections` skipped,
  and those following a query missing or not their creator's (checked against the queries as imported) kept and named
  in a note. An updated definition changes its collection at its next run (a note says so; nothing is queued). The
  report adds `AutoCollections` (`Records`, `NotCreated`, `Notes`, `SafetySnapshot`), or `{Failed, Error, Notes}` when
  the definitions' part failed after the queries were imported. An export without the key imports as before.
  Diagnostics' `AutoCollectionRuns`
  (`AutoCollectionDiagnostics`): counts by status, held removals, collections carrying one mark twice, marked
  collections without a definition (`MarkedWithoutDefinition`, and the first 100 by name in
  `CollectionsWithoutDefinition`: what a definitions file restored from an older copy leaves behind), unprunable dead
  links.
- **Auto-collections** (`AutoCollections/`): a definition (`auto-collections.json`, an `IUserDataOwner` that never
  deletes a definition with its user: a missing creator stops it, `CreatorMissing`) and its rebuildable run state
  (`auto-collections-state.json`). Every part keeps fields and enum values it doesn't know, and such a definition, or a
  store a newer version wrote (a newer schema, or a document-level field this version doesn't know), is never run or
  written. Nulls in a hand-edited file read as defaults (a null definition or entry is dropped with a warning). **Members** (`MembershipPlanner`): the query through the
  engine's server context, in the creator's libraries (never boxsets), per-user rules as the evaluation user, then the
  excluded items out before the query's `Limit` cuts (the next items move up), and the pins in after the query's order
  (only pins of the member kinds, inside libraries both the creator and the evaluation user can open), at most 5,000. **Finding the collection** (`CollectionLocator`): the stored id, else the
  mark (provider id `SmartSearch` = the definition id in `N` format; the server's lookup is exact and case-sensitive),
  oldest first. A stored collection without the mark is `TetherMissing` and never written; a deleted one is
  `Orphaned` until `Recreate`. **Writing** (`DirectReplaceWriter`): the difference is always taken against the live
  links (`LinkResolver`: cached id, path, library item id; never an empty id), and a changed collection gets its links
  replaced (a member already linked keeps its link), its rating, display order, lock, name and managed overview set,
  one save (row and `collection.xml`) and one forced refresh with no metadata or image providers (so an unlocked
  collection gets no online metadata from our refreshes; the server's own refreshes still may); an unchanged one gets
  nothing. A save that throws puts the cached collection's values back, so the next run writes again. A rename in the
  server's editor is taken over by the definition when it wasn't changed since its last run and no other definition
  has the name (ignoring case), else written back (and logged). An unlocked collection is
  always sorted by release date (the server resets any other order at each refresh) and gets its name locked. The
  server makes no image for a locked collection and only remote ones for an unlocked one, so a collection gets the
  **default poster** (`AutoCollectionDefaultImage`: `AutoCollections/AutoCollection.png`, embedded, rendered from
  `branding/AutoCollection.svg`) after a create's or run's write, when the setting `AutoCollectionDefaultImage` is on
  and it has no Primary image: through `ICollectionImages` (`IProviderManager.SaveImage`, which writes `folder.png`
  into the collection's folder, then `UpdateToRepositoryAsync(ImageUpdate)`, as the server's upload does). Decided
  once per collection and kept in the state's `DefaultImage` (`CollectionId`, `Outcome` `Applied` or `HadImage`), so a
  poster removed or replaced later stays so, one the collection had is never covered, and a recreated collection (a new
  id) is decided again; the setting off gives none and removes none. It raises no collection event (the edit listener
  records nothing), writes no links and changes no hash; a failed save is logged and tried at the next run. A create checks the name first (`CollectionNameChecker`: a collection of that
  name ignoring case, its folder or id taken, or another auto-collection waiting for it) and again just before the
  server creates it, and after a failed create looks for the mark before giving up. A conflict names the collection in
  the way only to a caller who can open it. Deleting keeps the collection (only the mark goes, after the definition is removed) unless asked to delete it too.
  **The other writer** (`ManagerApiWriter`, setting `CollectionWriter`: `Auto` is `DirectReplace` before Jellyfin 12,
  `ManagerApi` from 12): `ICollectionManager.RemoveFromCollectionAsync` once and `AddToCollectionAsync` once, each only
  with items (the add only with items that exist), inside `CollectionWriteSuppression` (per item: the listener ignores
  the events of the items passed, and records any other item's); then the rating and presentation saved, when they
  differ or members went, on the collection read again (`ICollectionPersistence.Reload`) with its mark checked: each
  server call loads and saves its own copy (`GetItemById`, a small cache), so the copy located before them is stale. Its difference leaves out dead
  and duplicate links (the remove can't find them) and the order: dead links are counted in the state's
  `UnprunableDeadLinks` and cause no write; `QueryOrder` is shown as `SortName` (a warning once per definition). Creates
  go through the server's create either way.
  **The guard** (`MassRemovalGuard`): a run never empties a collection that has members or dead links (unless the
  definition allows it), and holds a large removal (at least `max(GuardMinRemovals, GuardMaxRemovalPercent` of the
  members)`, dead links included) while the library looks unhealthy: the run started during a scan (only a run by hand
  can), a folder of one of its libraries is missing, its scope (the items of the query's kinds in its libraries) under half its size at the last run that wasn't held, with the same query, or
  it is the startup pass (also when a waiting startup pass serves a later request). Adds and presentation are written;
  the members it would remove stay, and so do dead links, until a run without a signal, an Apply or a Keep. **The
  hold** (`AutoCollectionPendingGuard`) stores what is held: the member ids (up to 1,000), the dead-link count and a
  hash of both with the Empty flag; its `DetectedUtc` is its version and changes whenever the held set does, and
  Apply or Keep naming another answers 409 `PreviewStale`. An Apply lets a tripped run through only when the approved
  hold `Covers` its removals: a subset of the ids, no more dead links, Empty only when the approved hold was Empty, and
  a set too large to list only when it is the same. Any run's end clears the approval (stopped and failed runs too).
  Keep pins every held member or answers 409 `TooManyToKeep` with nothing changed (an item that is gone can't be
  pinned, so it is held again). Both writers load the members just before they write and ask the runner when some
  can't be loaded: the guard looks again at what the write would leave and holds instead. The first run after a change
  saved from its preview isn't held for its size when it removes about as many as the preview said and finds the same
  members (the state's `AcceptedResultHash`); the preview ledger finds a preview only for the definition it was of.
  A run's end merges what changed meanwhile: an approval or a Keep of the hold it leaves as it found it, a preview
  accepted, edits recorded (and their overflow); what it read (approval, accepted removals) is used up. The settings page has an *Auto-collections* part (on/off, limits, timings, the
  guard). **Running** (`AutoCollectionWorker`, a kit background service): every run and pass goes through one queue
  and runs one at a time. A definition has at most one waiting run (a second request joins it), a waiting pass takes
  it along, and creates go first. Runs for library changes wait out a cooldown; passes after a scan or at startup wait
  for the scan to end (the daily one only so long); runs by hand never wait. A pass's definitions share the items they
  read (`PassItemCache`, up to `PassCacheMaxItems` items and candidate ids together) and one time, and so do the runs one batch of library changes asks
  for while they run back to back; a definition that fails shows `Error` and the pass goes on. `Queued` and `Running`
  live only in memory; a run stores its state once, when it ends. `LastError` is a short text per reason
  (`AutoCollectionReasons.SafeText`) every manager may read; `LastErrorDetail`, the full message, goes to administrators. **Cost of an unchanged run**: within a pass, a scope
  the cache can hold is read once (`PlanRequest.RulesInMemory`: the scope's ids, then every rule in memory over the
  cached items, with the plan otherwise unchanged); the live links are checked with id-only queries
  (`LinkResolver`, one per 500 links); the mixed-audience check asks a user without parental or tag rules only about
  libraries, and keeps a ruled user's answers for the pass (`AudienceMemo`). About 20 ms for 1,000 members in a pass;
  a run on its own reads its candidates and members from the database (about 65 ms). The debug log has each run's
  steps (`ran: check …, state … (ms)`).
  **Triggers**: library changes outside scans (`AutoCollectionsChangeConsumer` on the kit's monitor: a run of every
  definition over the changed kinds after the quiet period, not narrowed by library yet, so an unchanged one only
  compares; collections, folders and views are ignored. A new file shows two to three minutes later: the server's
  monitor waits 60 s or more, then the 60 s quiet period; measured 2–3 min); the full pass
  inside every scan only queues a pass, which starts once the scan has ended; the daily task **Smart Search: Refresh
  auto-collections** (04:00) (its dashboard Run is a pass by hand and starts at once: Jellyfin doesn't say what
  started a task, so only a start within 10 minutes after a daily or weekly trigger's time counts as the scheduled pass); the startup pass after `StartupPassDelayMinutes` (`AutoCollectionTriggers`, which also
  queues the definitions that follow a saved query when it is replaced or removed, through `SavedQueryStore.QueriesChanged`).
  **Edits made outside the plugin** (`CollectionEditListener` on the collection manager's add and remove events, a
  marked collection only, not the items the plugin's own `CollectionWriteSuppression` holds): kept in the state's
  `PendingEdits` (at most 5,000: beyond, the oldest stay and `PendingEditsOverflow` stops Respect runs as `Guarded`
  `TooManyManualEdits` until a save, or a run in Overwrite mode) until the definition's next run, asked for at once except during a scan (the pass after it sorts them).
  The run sorts them (`ManualEditKeeper`) against its fresh evaluation before comparing: in Respect mode an add outside
  the result becomes a pin (an excluded one is un-excluded), a removal of a match an exclude, a removed pin is unpinned;
  the server's own add (`AutomaticallyAddToCollection` and the movie's collection name equal to the collection's) and
  every edit in Overwrite mode are reverted. Edits that would pass 1,000 pins or excludes stop the run as `Guarded`
  (`TooManyManualEdits`) and stay pending. Each run also fixes the entries: a gone item's entry is relinked by the
  kit's `IdentityResolver` (the move journal, the same path, one strong provider id) or detached, a returned one
  reattached, and an entry without an identity gets one (entries saved by a PUT or a Keep get theirs when saved). A removed item detaches its entries at once; the journal is
  followed at start and through `Integration/ItemMovedHookV1` (Library Maintenance's hook), from the document's
  `MoveJournalId`/`LastMoveSeq`.
  **Watch state** (`UserDataTrigger`): `IUserDataManager.UserDataSaved` is handled only while an enabled definition with
  a collection has an evaluation user and `UseUserDataTriggers` is on (checked on each definitions change it sees and
  every 30 s). Only that user's saves count, not `PlaybackStart`/`PlaybackProgress`; each definition waits for the
  library-change debounce (capped by its longest wait), then a `WatchState` run is asked for, none during a scan.


## How it is checked

- **Unit tests** for the reader, validator, canonicalizer, migrator, every field's evaluator, the planner's mappers and
  the engine's paths, limits and cache; the endpoints on Jellyfin's web pipeline over the real engine and lookups, as
  an administrator, a restricted user and an API key (`Api/SearchApiTests.cs`, `Api/LookupApiTests.cs`); the saved
  queries the same way, for the owner, another user, an administrator, a restricted user, an API key and nobody, with
  the kit's store conformance suite, the deleted-user policy, export and import (`SavedQueries/`).
- **The differential test** (`Planning/DifferentialTests.cs`, and through the engine
  `Execution/EngineDifferentialTests.cs`): about 2,000 random valid queries, for no user, an administrator and a
  restricted user, over synthetic libraries on servers west of, at and east of UTC. The planner's pushdown through
  `FakeCore` (a model of 10.11.5's SQL with its quirks, and of what `/Items` does after SQL) plus the re-check must
  equal the full in-memory evaluation: no false negative, no false positive that survives the re-check, nothing shown
  or counted beyond `/Items`.
- **The live check, replayed** (`Planning/LiveCheckTests.cs`): a recording of a 10.11.5 sandbox
  (`Planning/Live/planner-live.json`: its rows, the users' access and 1,590 runs of the planner's plans through the real
  library manager, each next to the real `/Items`) loaded into `FakeCore`, which must do what the real server did.
- **The planner's database reads** (`Planning/DatabasePushdownFactsTests.cs`) run on EF Core's SQLite provider at the
  version 10.11.5 ships (9.0.11, a test-only package in `Directory.Packages.props`).
- **The page's tests** (`Web/smartsearch.test.mjs`, run by `Web/ClientScriptTests.cs` in the kit's fake page): the
  scope bar, the query the form sends, the hash and its links (also in Plugin Pages' host), the results, Load more,
  Play, the errors and the TV layout; the Saved queries tab: the list, its filter and find, save, edit and conflict,
  a link and Back, read-only queries, share, duplicate, rename, delete, copy link, names with markup and control
  characters, and TV; the advanced form: the tree's logic (build, edit, nest, limits, checks, the JSON round trip),
  every field built through its editor, the keyboard and focus, the server's errors, saved advanced queries and TV;
  the Auto-collections tab: who gets it, the list, filter, polling and toasts, held removals, the editor's name check,
  preview and save, a shared query's copy, the evaluation user, history, delete, `edit` links and the create buttons;
  the collection note: its place and text, Edit and Refresh now for managers, other items, the list's cache, the
  setting off, a missing anchor;
  the header button: its place and shape, following the native button's visibility, navigation and a rebuilt header,
  the page or the setting off, a missing anchor (one warning, one report) and TV.
  `Web/fields.json`, the catalogue those tests use, must equal what `/Fields` gives an administrator (a test checks).
  The settings page's export, import and restore run in a fake page too (`Web/configpage.test.mjs`).
- **Auto-collections** (`AutoCollections/`): the stores and their downgrade rules, the checks, the name check, the
  planner and preview over the planner's world, the link resolver, locator, difference and writer against recorded
  server calls, and whole runs through the service (create, add and remove, rename, reorder, detach, a run with nothing
  to change, a lost mark, a deleted collection, a failed create, a newer definition or store); edits made outside the
  plugin, the journal relink and detached entries (`ManualEditTests`).
- **The webchecks** in a sandbox (below).
- **Against a running sandbox** (`scripts/README.md`, "Smart Search checks"; the probes and the webchecks refuse the
  real server's port 8096, `PRODUCTION_PORTS` in `scripts/sandbox-policy.mjs`): `smartsearch-differential-probe.mjs`
  runs queries over every field and operator `/Fields` lists, and random combinations, as `admin`, `viewer` and a
  restricted `viewer2`, and compares each result with core's `/Items` for that user, evaluated in memory; `admin`'s
  runs also go through `ForceResidual`, so the pushdown is checked against the database it runs on.
  `smartsearch-smoke-probe.mjs` is the quick release check: a query with no rule over each library and kind equals
  `/Items`, after a scan, after a metadata refresh and after a real-time add of a movie whose studio is new.

## API

| Method | Route | Who | What |
|---|---|---|---|
| GET | `/SmartSearch/Fields` | signed-in users (not API keys) | the fields, sort keys and allowed item kinds, and `AllowQuerySharing`; `Path` only for administrators |
| POST | `/SmartSearch/Search` | signed-in users (not API keys) | a page of results: `{Query, StartIndex, Limit (1–200), ResultToken, IncludeTotal, ForceResidual}` → `{Items, TotalRecordCount, StartIndex, ResultToken, HasMore, ScanInProgress, Warnings, ElapsedMs}`; later pages send the first page's `ResultToken`. `ForceResidual` (administrators only, else 403 `AdminOnlyOption`) pushes down the scope alone and checks every rule in memory, cached apart: the differential probe's second side |
| POST | `/SmartSearch/Validate` | signed-in users (not API keys) | `{Query}` → `{Valid, Supported, Errors, UsesPerUserFields, UsesAdminOnlyFields, UnavailableFields}`, and `Paths` for administrators; a query with errors is a 200 |
| GET | `/SmartSearch/Libraries` | signed-in users (not API keys) | the user's libraries |
| GET | `/SmartSearch/Values/{fieldId}?prefix&libraryIds&limit` | signed-in users (not API keys) | a field's values with counts; `libraryIds` repeated, `limit` 1–100 (20) |
| GET | `/SmartSearch/Series?prefix&limit` | signed-in users (not API keys) | series by name; `limit` 1–100 (20) |
| GET | `/SmartSearch/Queries?scope&startIndex&limit` | signed-in users (not API keys) | the user's queries and the shared ones (`scope` `Mine`, `Shared` or `All`; `limit` 1–500, 100) → `{Items, TotalRecordCount, StartIndex}` |
| GET | `/SmartSearch/Queries/{id}` | signed-in users (not API keys) | a query with its definition, `Supported`, `Valid`, `Errors`, `CanEdit` (`AutoCollectionCount` for the owner and administrators only, 0 for others) |
| POST | `/SmartSearch/Queries` | signed-in users (not API keys) | saves `{Name, Description, Shared, Query}` (72 KB at most) → 201 |
| PUT | `/SmartSearch/Queries/{id}` | the owner or an administrator | changes it: the same body plus `IfUpdatedAt` (and `AcknowledgeAutoCollectionWarnings`, `DetachAutoCollections`) |
| POST | `/SmartSearch/Queries/{id}/Duplicate` | who can open it | a private copy for the caller: `{Name}` or an empty body (the name numbered) → 201 |
| DELETE | `/SmartSearch/Queries/{id}?detachAutoCollections` | the owner or an administrator | 204 |
| POST | `/SmartSearch/Queries/{id}/Search` | who can open it | runs it as the caller: `{StartIndex, Limit, ResultToken, IncludeTotal}` or an empty body; the answer of `/Search` |
| GET | `/SmartSearch/Admin/Queries?userId&startIndex&limit` | administrators and API keys | every user's queries |
| POST | `/SmartSearch/Admin/Queries/{id}/Owner?force` | administrators and API keys | `{UserId}`: gives the query to another user; 409 `BreaksAutoCollections` while auto-collections follow it, unless `force=true` |
| GET, POST | `/SmartSearch/Admin/Export`, `/SmartSearch/Admin/Import?mode&dryRun&mapUsersBy&force` | administrators and API keys | the kit's export and import, with `BrokenAutoCollections`; the auto-collection definitions ride along (`Data.AutoCollections`, report `AutoCollections`) |
| GET, POST | `/SmartSearch/Admin/Users`, `Users/Remap?by=Name&dryRun&force`, `Users/ApplyPolicy` | administrators and API keys | the kit's user tools; a remap refused part-way names the owners already remapped (`remappedOwners`) |
| GET, POST | `/SmartSearch/Admin/Snapshots`, `Snapshots/Restore?name&dryRun&force` | administrators and API keys | the store's snapshots; a restore's dry run gives `Queries` and `BrokenAutoCollections` |
| POST | `/SmartSearch/AutoCollections/CheckName` | administrators and users who manage collections | `{Name, DefinitionId}` → `{Name, Available, Conflicts, Warnings, Suggestions}` |
| POST | `/SmartSearch/AutoCollections/Preview` | as above | `{Definition}` (new) or `{DefinitionId, Definition}` (changes) → `{PreviewHash, Total, WouldAdd, WouldRemove, AddSamples, RemoveSamples, HiddenFromCaller, PinsSkipped, MixedAudience, Guard, Warnings}`; samples only of items the caller may open |
| GET | `/SmartSearch/AutoCollections?status`, `/{id}` | as above; the list also API keys | the definitions and their state (the list without manual edits; a saved query only to a caller who may read it, a copy only to the creator and administrators) |
| POST | `/SmartSearch/AutoCollections` | as above, a user | `{Definition, AcceptedPreviewHash, DuplicateSourceQuery}` → 201 with the collection, or 202 while it is still on its way (one minute); 409 `PreviewStale` when the members moved more than 5% from the preview. A run the engine refuses (busy, too slow, too broad) still answers 201: the definition is kept with status `Error` and `CreatePending`, and the next run of any kind makes the collection (`Recreate` too); a definition that never asked for one (an import) stops as `NotCreated` until a `Recreate`. A client that leaves after the save leaves the definition and its queued run in place |
| PUT | `/SmartSearch/AutoCollections/{id}` | as above; name, source, evaluation user and `Enabled` only the creator or an administrator (who switches someone else's source only to a copy) | `{Definition, IfUpdatedAt, AcceptedPreviewHash}`; what it leaves out stays; the update is queued |
| POST | `/SmartSearch/AutoCollections/{id}/Refresh`, `/Refresh` | as above; also API keys | 202, queued |
| POST | `/SmartSearch/AutoCollections/{id}/Recreate`, `/{id}/Detach` | as above | `{Name}` → 201/202, a new collection for a deleted, never made or replaced one; a copy of the creator's saved query (409 `SourceOwnerChanged` when it is someone else's now) |
| POST | `/SmartSearch/AutoCollections/{id}/Reassign` | administrators | `{UserId}` (an administrator or a user who manages collections) → 200: a deleted creator's auto-collection, with its saved query and the creator's other auto-collections following it, goes to that user (the evaluation user stays); 409 `CreatorNotMissing`, `AdminOnlyField`, `QuotaReached` (checked before anything is written) |
| POST | `/SmartSearch/AutoCollections/{id}/Guard/Apply`, `/{id}/Guard/Keep` | as above | `{DetectedUtc}` as read → 202, the held removals (or fewer) go with the next run; 204, the held members become pins; 409 `GuardNotPending` when nothing is held, `PreviewStale` when the hold changed since it was read, `TooManyToKeep` when Keep can't pin them all |
| GET | `/SmartSearch/AutoCollections/{id}/History` | administrators and users who manage collections | the last 20 runs, newest first: `{Utc, Reason, Added, Removed, Dead, Pinned, Excluded, Reverted, Outcome, DurationMs, RemovedIds, HiddenCount}`; removed ids only those the caller may open |
| GET | `/SmartSearch/AutoCollections/Ids`, `/ByCollection/{boxSetId}` | signed-in users (not API keys) | the collections auto-collections fill that the caller may open (each looked up as the caller); `{Name, LastSuccessUtc, CanManage, DefinitionId}`, the id only for managers, 404 for any other collection; empty and 404 while auto-collections are off |
| DELETE | `/SmartSearch/AutoCollections/{id}?deleteCollection` | as above | 204; the collection stays, or with `true` is deleted when it still carries the mark and the caller may delete it (else 403, nothing deleted) |
| GET | `/SmartSearch/ClientScript` | anyone | the web client's script |
| GET | `/SmartSearch/Pages/search` | signed-in users | the page's shell |

The kit's diagnostics and admin endpoints (`/SmartSearch/Diagnostics/ClientHealth`, `/SmartSearch/Admin/*`) are as in
every plugin of the suite. The `/Queries` routes, like `/Search`, answer 403 while the search page is off, before the
body is read; the admin routes don't. An error is a ProblemDetails with the kit's `code`: `InvalidRequest`,
`InvalidQuery`, `AdminOnlyField`, `AdminOnlyOption`, `TooLarge`, `QueryTooBroad`, `FieldUnavailable` (400 or 403 or 413), `Timeout`,
`Busy` (503), and for saved queries `QueryNotFound` (404), `NotOwner`, `SharingDisabled`, `SharedAdminOnlyField`,
`ManagerRequired` (403), `DuplicateName`, `QuotaReached`, `IfUpdatedAt`, `NeedsNewerVersion`, `BreaksAutoCollections`,
`AutoCollectionsNeedAttention`, `Referenced` (409), `UnknownUser` (400).

## Installing, upgrading and going back

The user README has the short version. Paths are Jellyfin's Windows defaults (`C:\ProgramData\Jellyfin\Server`). The
plugin folder is `Smart Search_<version>` (`Smart Search_1.1.2.0`); keep only one.

1. **Stop Jellyfin** (it locks plugin DLLs). Jellyfin's own backups leave plugin data out: for an upgrade, copy
   `data\smartsearch` and `plugins\configurations\Jellyfin.Plugin.SmartSearch.xml` aside, or export first on the
   settings page.
2. **Install**: extract the release zip into `plugins\Smart Search_<version>`, deleting an older Smart Search folder.
   From a clone, `scripts\Install-RealServer.ps1 -Plugin SmartSearch -Version 1.1.2` (first with `-WhatIf`) builds the
   tag `smartsearch/v1.1.2` in Release in a temporary worktree, takes a cold backup (with `data\smartsearch` when it is
   there) to `$HOME\JellyfinBackups\<yyyyMMdd-HHmmss>-before-SmartSearch-<version>`, installs the folder checked by
   SHA-256 and writes `backup.json` and `RESTORE.txt` ([scripts/README.md, "Install-RealServer.ps1"](../../scripts/README.md#install-realserverps1)).
3. **Start Jellyfin**, reload every web client tab with **Ctrl+F5**, and check as an administrator: Plugins lists Smart
   Search at the new version, Active; the settings page's Diagnostics shows the version, `Abi` active and no client
   health report; the log has `Loaded plugin: "Smart Search" "<version>"` and no error from
   `Jellyfin.Plugin.SmartSearch`; *Advanced Search* is under **Library tools**, the header button before the search
   button, a search as a non-administrator finds what their library views show, and the Auto-collections tab lists
   every auto-collection *Up to date*.

**From 1.1.0** nothing is migrated either: the auto-collections' existing collections without a poster get the
default one at their next update (`DefaultImage` in their state). **From 1.0.0** nothing is migrated: the saved queries' file is read as it is, and `auto-collections.json` and
`auto-collections-state.json` appear with the first auto-collection.

**To go back or remove it**: stop Jellyfin, delete `plugins\Smart Search_<version>` (to go back, copy the earlier
folder back from the backup), and to remove it also delete or move aside `data\smartsearch` and the settings file;
start Jellyfin and reload the web client. `RESTORE.txt`'s way A does the same from an install by the script; way B (the
whole server back to the backup's moment) is only for a broken server. *Uninstall* on the Plugins page, then a restart,
leaves the data and settings for a later install. **Don't use Disable** (on 10.11.5 the next start deletes the folder;
untick *Add Smart Search to the web client* instead).

**Back to 1.0.0**: 1.0.0 starts with the saved queries and never reads the auto-collections' files; the collections stay
as ordinary locked collections with the `SmartSearch` mark, which nothing updates. 1.0.0 has no reference guard, so a
saved query deleted or changed there stops its auto-collections (`QueryMissing`, `QueryInvalid`) once 1.1.0 is back;
otherwise 1.1.0 takes them up as they were. Way A also puts the backup's `data\smartsearch` back, which was taken
before the auto-collections existed: then the definitions are gone and their collections stay unmanaged, listed in
Diagnostics' `CollectionsWithoutDefinition` when 1.1.0 is installed again.

## Building and testing

```powershell
dotnet test plugins/SmartSearch/tests/Jellyfin.Plugin.SmartSearch.Tests
./scripts/dev-server.ps1 -Name ss -Port 8112 -Plugins SmartSearch -Community None -Background
./scripts/Initialize-Sandbox.ps1 -Name ss
node scripts/webcheck.mjs --sandbox ss --steps smartsearch-page.json
node scripts/webcheck.mjs --sandbox ss --steps smartsearch-search-desktop.json   # also -tv, -phone, -themes
node scripts/webcheck.mjs --sandbox ss --steps smartsearch-saved-desktop.json    # then -tv, -phone, -themes
node scripts/webcheck.mjs --sandbox ss --steps smartsearch-advanced-desktop.json # also -tv, -phone, -themes
node scripts/webcheck.mjs --sandbox ss --steps smartsearch-header-desktop.json   # also -tv, -phone, -themes
node scripts/webcheck.mjs --sandbox ss --steps smartsearch-collections-desktop.json # also -tv, -phone, -themes
node scripts/webcheck.mjs --sandbox ss --steps smartsearch-collectionnote.json     # the note on its page
node scripts/smartsearch-smoke-probe.mjs --sandbox ss                            # the quick release check
node scripts/smartsearch-differential-probe.mjs --sandbox ss                     # every field and operator vs /Items
./scripts/Stop-Sandbox.ps1 -Name ss
```

`smartsearch-page.json` expects the default `FallbackOnly` page host; `smartsearch-pluginpages.json` expects `Auto`
with Plugin Pages installed (`scripts/README.md`, "Smart Search checks"). A sandbox with community plugins takes them
from a plugins folder of known versions with `dev-server.ps1 -CommunityFrom` (Plugin Pages 2.4.7 for these checks).

**The release checks** run the above on a Release build in three sandboxes, one per standard profile: the `default`
community plugins (File Transformation and Plugin Pages, with `smartsearch-pluginpages.json` after a restart into
`Auto`), none (`bare`), and the `je` set with its pinned settings (`Initialize-Sandbox.ps1 -PreStart` before the first
start, then `-AssertJeFixture`), where every webcheck must pass alongside Jellyfin Enhanced's script and keys. For
1.1.0 they add the auto-collections' sandbox checklist (create, real-time add, the post-scan run, removals and the
guard, the startup pass, manual edits, deletion and a native collection of the same name, renames, merged versions,
visibility, watch state, the saved-query guard, a newer definition, the order), and an upgrade from the released
1.0.0 with saved queries by `Install-RealServer.ps1 -FromWorkingTree` on a stopped sandbox, then its `RESTORE.txt` way
back.
