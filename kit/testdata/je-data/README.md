# Synthetic Jellyfin Enhanced data

Hand-written files in the shapes Jellyfin Enhanced (JE) 12.8 to 12.12 write, for the kit's reader and parser tests
and, copied into a sandbox's `plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced/` folder, for the importers'
sandbox checks (Bookmarks and User Ratings). Never a copy of a server's own files: every id below is made up
(`0000…0001` and `0000…0002` are the two synthetic users; the item ids are synthetic too). The field names and
value types were read from JE's `Configuration/UserConfiguration.cs` (`BookmarkItem`, `UserReview`, `UserShortcuts`),
the same in the 12.8 source and the 12.11 and 12.12 DLLs.

| File | Shape |
| --- | --- |
| `reviews.json` | `{"Reviews": {"<userIdN>:<movie|tv>:<tmdbKey>": {UserId, TmdbId, MediaType, Content, Rating (double, half steps, or null), CreatedAt, UpdatedAt}}}`; a `tv` key may carry `:s<N>` and `:s<N>:e<M>` |
| `<userIdN>/bookmark.json` | `{"Bookmarks": {"Bm_<epoch ms>_<random>": {ItemId, TmdbId, TvdbId, MediaType, Name, Timestamp (seconds, double), Label, CreatedAt, UpdatedAt, SyncedFrom, SeasonNumber, EpisodeNumber}}}`; the TMDb and TVDb ids may be strings or numbers (JE's reader takes both) |
| `<userIdN>/shortcuts.json` | `{"Shortcuts": [{Name, Key, Label, Category}]}`, the user's own overrides; `Key: ""` switches an action off for that user |
| `Jellyfin.Plugin.JellyfinEnhanced.xml` | A hand-written stand-in for JE's settings file, the older-version shape (standing for **12.11**), not a copy of JE's file: XmlSerializer's `<PluginConfiguration>` root and namespaces, only what the kit reads (the flags of `JellyfinEnhancedDefaults.Flags` but 12.14's two, with JE's defaults, `ShowUserReviews` and `MirrorReviewRatingsToJellyfin` flipped on so the reviews checklist has something to see, and the `Shortcuts` list with JE's default keys and labels) and a few made-up `Example*` elements, standing for the settings the kit never reads, which the reader must skip. No id in it (the reader test checks). The `je-sandbox` fixture next door stays the coexistence fixture (12.8-shaped, the settings that matter set) |
| `Jellyfin.Plugin.JellyfinEnhanced.12.12.xml` | The newer-version stand-in (standing for **12.12**): the file above element for element, in its order and with its values, plus four made-up `ExampleAdded*` elements standing for the settings a newer JE adds; nothing renamed, moved or removed, so the reader reads both alike (the reader test checks the order and the additions) |

The second user's `bookmark.json` holds the odd cases the parser must leave out with a reason (a value of the wrong
type, a missing `ItemId`, a negative timestamp, a repeated key) next to good entries; `reviews.json` likewise. The
first user's files are clean. The reader tests use the `je-sandbox` fixture's 12.8-shaped JE XML and Custom Tabs XML
for the coexistence settings, and the two stand-in settings files here for the 12.11 and 12.12 shapes. Two more reader
tests read the whole settings files JE 12.11 and 12.12 write for a fresh install; those files are not part of this
repository, and the two tests run only where a copy is in the optional, gitignored local folder `private/testdata/je-data`.

## `import-81/`: the 81-style fixture

Synthetic data in the shape of a server's Jellyfin Enhanced bookmarks in use (two users with 72 + 9 bookmarks, nearly all
named, float timestamps, `Bm_<epoch ms>_…` keys, N-format item ids, `movie` with a `TmdbId`), for Bookmarks' import tests (`Import/JellyfinEnhancedBookmarkImporterTests`) and its sandbox check
(`scripts/bookmarks-je-import-probe.mjs`). Everything in it is made up: the three user folders (`…81`, `…82`, `…83`), the
item ids (`b14…`) and the keys.

| File | What |
| --- | --- |
| `library.json` | The synthetic library the files refer to: 16 videos with their kind, name, year (or series, season and episode), runtime in seconds and provider ids, matching the sandbox media of `New-SandboxMedia.ps1` (Long Test Movie 180 s, the 60 s films, the 30 s episodes of the `[tvdbid-900401]` series, the `[tmdbid-900001]` film, the two `[tmdbid-900201]` remakes), the users (`…81` is the sandbox's admin, `…82` its viewer) and the one native bookmark the duplicate case needs (viewer's "Bank job" at 12.4 s on Chaptered Movie). The tests build a fake library from it; the probe maps each item to the sandbox video of the same kind, name and year (or series and numbers) and rewrites the ids when it copies the files in. |
| `…81/bookmark.json` | 70 entries: 57 on the videos by item id (unnamed ones among them, a label over 100 characters, one with a line break and a tab, one with accents and an emoji, one with markup, one with no label, one past the end of Long Test Movie at 250.5 s), 2 duplicates of earlier entries (the same name within a second), 2 whose item id is gone but whose TMDb id is the `[tmdbid-900001]` film's (one as a JSON number), 1 episode by the series' TMDb and TVDb ids with its season and episode, 1 on TMDb 900201, which two films share, 3 not in the library (a film, an unnamed film, an episode), 1 cross-version copy (`SyncedFrom`), 1 whose `ItemId` isn't an id, 1 with a text `Timestamp` and 1 without an `ItemId`. |
| `…82/bookmark.json` | 9 entries: 7 by item id (one unnamed), 1 duplicating viewer's own "Bank job" (12.9 s, the same name), 1 not in the library. |
| `…83/bookmark.json` | 2 entries of a user the server doesn't have. |

81 bookmarks, 72 with a label. What the import makes of them is in `JellyfinEnhancedBookmarkImporterTests` and
`scripts/README.md` (the JE import probe): 72 imported (64 + 8; 64 by item id, 3 by TMDb or TVDb, 1 of two copies, 4 not in
the library), 3 duplicates, 6 skipped, 1 moved off the end.
