# Fixtures

Files written by Bookmarks, kept byte for byte (`.gitattributes` marks them `-text`, so git never changes their line
endings): by earlier releases and the 1.0 development build, one that pins the current store schema, and two made from
them for the read-only store cases of 1.1 (a hand edit 1.0 still reads, and a newer schema). The tests embed them
(`Fixtures.<name>`). None is ever regenerated.

The data in them is synthetic: sandbox users, made-up titles and made-up clip and video paths (a checkout at
`C:\src\jellyfin-plugins` and drive `Q:`). The SHA-256 values below are the files' as committed.

## bookmarks-1.0.0.json

A store file. Written by **Bookmarks 1.0.0** itself: the 1.0.0 release (tag `bookmarks/v1.0.0`), built from a
`git archive` of the tag and deployed with `Deploy-Plugin.ps1 -NoBuild` into the `default` sandbox (Jellyfin 10.11.5,
File Transformation and Plugin Pages) on 2026-09-29. The records were made through its REST API as the sandbox users:

- `admin`: a range with media source, audio track and a note with quotes and a Fast clip (Completed); a point on a
  `[tmdbid-900001]` movie with a two-line note (provider ids captured); a range with a non-ASCII note (`Amélie, café
  scène ✂ 日本語`) and an Accurate clip (Completed); a range whose Accurate clip was cancelled at once (Cancelled); an
  episode; a home video edited after creation (`UpdatedAt` later than `CreatedAt`); a bookmark whose clip was made and
  then deleted (no `Clip`); and the older `webcheck` bookmark from an earlier check, rewritten by 1.0.0 with the rest;
- `viewer` (no download permission): a range with an apostrophe in its note;
- `viewer2`: a point.

Ten records in all, file SHA-256 `e302243777854ffbf28e629d89227ebaa81c05ca329d62dac315176140c2bd02` (CRLF line endings,
as the plugin wrote it on Windows), schema version 1. Clip paths point into that sandbox; the 1→2 upgrade reads only
their folders (`ClipRoots`). Since 1.1 the store conformance suite upgrades it to schema 2 and compares the
`.v1-*` copy and the `.bak` with its bytes, and `Storage/BookmarkStoreMigrationTests` checks every upgraded record
against it. `scripts/bookmarks-api-probe.mjs --fixture` runs the same upgrade in a sandbox.

## bookmarks-1.0-dev-sandbox.json

A store file written by the **Bookmarks 1.0 development build** in its own sandbox before the release,
`jellyfin-plugin-bookmarks/.sandbox/jellyfin/data/bookmarks/bookmarks.json` as it was on 2026-09-24 (copied byte for
byte on 2026-09-30; the other repository is only read). Fourteen records of two
users of that sandbox (13 and 1), schema version 1, CRLF line endings, SHA-256
`476201b563cbd2154922c5429040dfdc8f61917295d735dbe92f6637c6d4e03c`. It holds shapes 1.0.0's file doesn't: finished clips
without `FileStartTicks`/`FileEndTicks` (written before 1.0 recorded a file's range), a cancelled remake that kept its
earlier file (`Status` `Cancelled` with `FilePath` and the range), a job cancelled before it wrote anything, points with
media source and audio track, and seven short notes. Its clip files lie in a folder outside any default clip folder,
so the upgrade records that folder in `ClipRoots`. The conformance suite upgrades it, and
`Storage/BookmarkStoreMigrationTests` checks every record against it.

## bookmarks-1.0-duplicate-key.json

`bookmarks-1.0.0.json` with one line added by hand: `viewer`'s record has `"Note": "viewer's first note"`
right above its own `Note`, as an edit that pasted a line twice would leave it. SHA-256
`a39703f84aa1bc3172fe88389e02fa19d80feda8f87316e4381e1a16b7b0f90c`, CRLF line endings like the original. Bookmarks 1.0
still reads it (System.Text.Json keeps the last value of a repeated property; `Storage/BookmarkStoreReadOnlyTests`
shows it), but the 1→2 upgrade parses the file as a tree and refuses it: the store is read-only
(`ReadOnlyMigrationFailed`) and the file stays as it is. The failed-upgrade fixture of the sandbox check
(`bookmarks-api-probe.mjs --fixture … --expect-failed-upgrade`) and of `Api/ReadOnlyStoreApiTests`.

## bookmarks-schema3.json

A store file of a **schema 3** that doesn't exist yet: `bookmarks-1.0.0.json` as a 1.1 development build upgraded it
in the `default` sandbox (`.sandbox/default/results-b2a/fixture-state/bookmarks.json`, so the sandbox's users, clip
folder and names), with `Version` 3, a `FutureDocumentField` on the document, a `FutureRecordField`
and a `FutureClipField` on `admin`'s Fast clip, which is being made again (`Queued`, as Accurate, the old file still
recorded), and `admin`'s Amélie clip `Running` (interrupted by a shutdown). Written once by a Node script with
`JSON.stringify(…, null, 2)` and CRLF line endings, SHA-256
`97d1335f0b2cb983d1486ee215f14c74d96d9a34bf16084c5df3d78ed1cbabb3`. This version shows its ten records read-only and
never writes it; a clip worker that ran would have resumed both jobs. `Storage/BookmarkStoreReadOnlyTests`,
`Api/ReadOnlyStoreApiTests` and the sandbox check (`bookmarks-api-probe.mjs --fixture`) use it.

## bookmarks-schema2.json

A store file at **schema version 2**, written by a 1.1 development build (`JsonStoreFormat.Options`, on Windows,
so CRLF line endings) from a document holding every schema 2 field: two users (`alice`, `bob`) in `KnownUsers` and
`UserPreferences` (one with `AskForName`, one with only `PauseWhileNaming`), an import ledger entry, a move-journal
position, a pending file delete, two clip folders, and six records: a named range with its identity, an Accurate clip
and `EstimatedBytes`; a detached Jellyfin Enhanced import; a Strong relink of an episode with the series fields; a
clip-only tombstone whose file is missing (`MissingSinceUtc`); an adopted clip (`ItemId` empty, `Origin` `Adopted`);
and a bookmark with a two-line note and no name. Fields of a later version (`FutureDocumentField`, `FutureRecordField`,
`FutureClipField`, `FutureIdentityField`) sit on the document, a record, its clip and its identity.

SHA-256 `929929622f49d3f236182acbf9163c95f2e572db2af5cf57d95b2f992e03f685`. The conformance suite loads it unchanged
at the current version; `Storage/BookmarkStoreTests` checks every field and that reading and writing it again gives the
same bytes, so a change to schema 2's shape or format fails there. Once a later schema exists, it becomes that
schema's "older" fixture and must be upgraded like `bookmarks-1.0.0.json`.

## api-wire-1.0.json

The REST API's JSON just before the 1.1 work, for `Api/WireFormatTests`. Written by that test just before the renames, from
the types 1.1 then renamed (`ClipDto`, `ClipStatus`, `ClipMode`, `CreateBookmarkRequest`, `UpdateBookmarkRequest`,
`CreateClipRequest`) and the others, and checked in unchanged apart from its `comment` (SHA-256
`97c5138be5efc44badf223845fbcc8ad4b127a5a90428f572e7f9962c500a74e`, LF line endings):

- `pascalCase`, Jellyfin's default answer (`JsonDefaults.PascalCaseOptions`), and `camelCase`, its answer for
  `profile="CamelCase"`: a bookmark with a running clip and an outdated older file (non-ASCII note with quotes and a line
  break, a time with milliseconds and one without), one with a failed clip, a point with nothing optional, the
  capabilities, the three request bodies as the types bind them and write them back, and every clip status and mode.

That is Bookmarks 1.0.0's JSON (the 1.0.0 release, tag `bookmarks/v1.0.0`) with one exception: `capabilities` also holds
`IsAdmin` and `StoreReadOnly`, two of 1.1's fields that were added before the snapshot. 1.0.0 sent only
`ClipsEnabled`, `CanCreateClips`, `MaxClipDurationSeconds` and `DefaultClipMode` there; every other DTO, both enums and
`BookmarkDtoMapper` were the same at the tag and when the snapshot was written. The test pins that: the snapshot's capabilities without
the two (`InSnapshotSince10`) must be exactly what 1.0.0's own capabilities type writes.

The test compares each sample as text (with the properties added after the snapshot, the test's `AddedSinceSnapshot`,
left out of the actual JSON) and writes `api-wire-1.0.actual.json` next to the test assembly when one differs. Never
regenerate this file: it is the contract for every property 1.0 sent, and for `IsAdmin` and `StoreReadOnly`, which are
therefore never listed in `AddedSinceSnapshot`.
