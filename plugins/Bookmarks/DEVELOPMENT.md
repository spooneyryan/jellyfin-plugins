# Jellyfin Bookmarks

A Jellyfin plugin that lets each user bookmark moments in movies and episodes, name them, find them again on a page of
their own, and cut a bookmarked range into a clip file with ffmpeg.

Version **1.2.2**, for Jellyfin **10.11.5** (`net9.0`). This is the technical reference for developers and for admins who
want more than the user [README.md](README.md) gives. Release history: [CHANGELOG.md](CHANGELOG.md).

- **Bookmarks** belong to one user: a position, an optional end time, a **name** (one line, at most 100 characters) and
  a **note** (free text, at most 500).
- **While watching**, `Shift+B` (or the bookmark button) bookmarks the moment without pausing, and a small dialog asks
  for a name. Bookmarks are marked on the progress bar.
- **A video's page** has a **My bookmarks** section; **My Bookmarks** and **My Clips** (under **Library tools**) list
  every bookmark and clip of yours. Admins can switch My Clips to **All users**.
- **Clips** are cut in the background: **Accurate** re-encodes to H.264/AAC MP4 (frame-exact, HDR tone-mapped to SDR),
  **Fast** copies the streams into MKV (from the keyframe at or before the start). A free-space guard protects the drive.
- **Jellyfin Enhanced's bookmarks** are imported once; a checklist helps switch them off.
- **Bookmarks follow their video** when it moves, and wait as "Not in library" while it is gone.
- **The data is kept safe**: one JSON file with a backup copy, daily snapshots, export and import, and a read-only mode
  when a file can't be written faithfully.

The web UI follows the web client's theme, and works on phones (bottom sheets, 40 px targets), TVs (D-pad, Back key), in
Windows High Contrast and from the keyboard.

## Privacy

- **Bookmarks are their owner's.** Names, notes, positions and clips are shown and changed only for their owner; another
  user's id answers 404, like one that doesn't exist. Admins see per-user **counts** (Diagnostics), never a name, note or
  video.
- **What an admin can still read**, because it is the server's: an **export** (every user's names and notes: keep it as
  safe as a database backup); the clip files on disk, a folder per user, named after the video, the bookmark's name and
  the range (`Heat (1995) - Bank job [01.52.10-01.55.00].mp4`); the JE import report's labels. A clip file is **never
  named after a note** since 1.2.0; an older file of an unnamed bookmark keeps the start of its note in its name until
  its owner renames it.
- **The admins' All users view** (My Clips) shows every user's clips with owner, the bookmark's name and the file's full
  path, for preview, download, cancel and delete only. It lists clips of videos the admin can open (of videos gone from
  the library only for admins with access to every library) and **never a note**: the server sends none and its search
  skips notes. Each action is logged at Information with who did it ("Admin <id>", an API key by its name, a deleted
  user by id), the clip's and owner's ids, never a name, note or path. `AdminsCanManageAllClips` turns the view off.
- **Clips in a library are everyone's** who can open that library. The default clip folder is outside every library.
- **A deleted user's text goes** from the data, its `.bak` and the archive, under every policy; older snapshots keep it
  until they rotate out, and a kept clip file carries the name in its file name and title ("Deleted users").
- **Library access is the user's own.** The pages ask the library, as the user, whether each shown video can be opened,
  load a poster only for those, and never call `/Items?Ids=` or `/Items/Latest`, which skip library access.
- **The server log** has no name or note at Information or above, only ids, counts, modes, durations and sizes; a clip
  file's path (which holds the name) only at Debug, and a failed clip's message names the file as `<clip file>`.
- **On the device** only: a user's own shortcut (`localStorage`). The naming preferences are kept on the server.
- **No outside services.**

## Requirements

- Jellyfin 10.11.5 or a later 10.11.x; no other plugin. On another Jellyfin line it logs one error
  (`Jellyfin.Plugin.Bookmarks.Kit.AbiGuard`), adds nothing to the web client, never opens its data, and every endpoint
  answers 503.
- The script reaches the web client through the kit's `index.html` middleware ("Notes and limitations"): no File
  Transformation or JavaScript Injector.
- Clips: ffmpeg (Jellyfin's) and the user's **Allow media downloading** permission.
- Optional: **Plugin Pages** (menu links), **Jellyfin Enhanced** 12.8 to 12.12 (import source; the checklist covers 12.11
  and 12.12), **Library Maintenance** (its move journal).

## Installing and upgrading

The user README has the short version; this is the careful one. Nothing runs by itself; try a build in a sandbox first.
Paths are Jellyfin's Windows defaults (`C:\ProgramData\Jellyfin\Server`).

1. **Stop Jellyfin** (tray icon, **Exit**, or the service): it locks plugin DLLs, and a running server's database isn't a
   reliable backup.
2. **Back up** Bookmarks' data and settings (and a clip folder of your own, if the clips matter):

   ```powershell
   $data = 'C:\ProgramData\Jellyfin\Server'
   $backup = "$HOME\JellyfinBackups\before-bookmarks-$(Get-Date -Format yyyyMMdd-HHmmss)"
   New-Item -ItemType Directory -Path $backup | Out-Null
   Copy-Item -Recurse -LiteralPath "$data\data\bookmarks" -Destination $backup -ErrorAction SilentlyContinue
   Copy-Item -LiteralPath "$data\plugins\configurations\Jellyfin.Plugin.Bookmarks.xml" -Destination $backup -ErrorAction SilentlyContinue
   ```

3. **Install**: delete `plugins\Bookmarks_<old version>` and extract the release zip into `plugins\Bookmarks_<version>`
   (for example `Bookmarks_1.2.2.0`). Leave `data\bookmarks` and the settings file alone. From a clone,
   `scripts\Install-RealServer.ps1 -Plugin Bookmarks -Version 1.2.2 -WhatIf`, then without `-WhatIf`, does steps 2 and 3
   from the release tag: a cold backup to `$HOME\JellyfinBackups\<yyyyMMdd-HHmmss>-before-Bookmarks-<version>`, a
   Release build of `bookmarks/v<x.y.z>`, the install checked by SHA-256, and a `RESTORE.txt`
   ([scripts/README.md](../../scripts/README.md#release-and-install-tooling), "Install-RealServer.ps1").
4. **Make sure every library drive and the clip folder's drive are there** before starting: Bookmarks copes with a
   missing drive, but Jellyfin's own clean-up may remove its data for a library folder it can't find.
5. **Start Jellyfin** and reload every open web client tab with **Ctrl+F5**.
6. **Check** Dashboard, Plugins (Bookmarks at the new version, Active, alone) and its Diagnostics tab ("Bookmarks
   <version> ... (server 10.11.5, active)", the file Ok at schema 2, the clip folder without warnings, "Clip file renames
   not finished: none.").

**Don't use Disable** on the Plugins page: on 10.11.5 the next start deletes a disabled plugin's folder. Untick **Add the
bookmark controls to the web client** instead.

**Upgrading.** From 1.1.x or 1.2.x the data stays at schema 2 and everything is kept; new settings start at their
defaults (**Admins can see and manage every user's clips** is on). 1.2 adds the clip folders' marker file
(`.jfbm-clip-root`) and `renamed-clips.json`. **If a clip file was ever deleted by hand**, 1.2 can't prove the clip folder
is its clips' own: new clips fail with "The clip folder isn't recognised" and Diagnostics lists the folder as Unproven;
confirm it ("Missing clip files") only if it really is the clip folder. From 1.0.0: "The upgrade from 1.0".

### Going back

Stop Jellyfin, replace `plugins\Bookmarks_<version>` with the earlier version's folder, start Jellyfin. Don't put
`data\bookmarks` back: that loses everything bookmarked since, and no version below needs it.

- **To 1.2.0**: reads 1.2.1's data as it is.
- **To 1.1.x**: first Diagnostics must say "Clip file renames not finished: none." (`ClipRenames.Unfinished` 0); 1.1
  would read a clip whose rename waits as one without its file. 1.1 keeps 1.2's fields and files; it lacks My Clips
  (kept clips of deleted bookmarks stay on disk, unlisted), the admins' view and the missing-clip marks.
- **To 1.0**: "Going back to 1.0".

`RESTORE.txt` gives two ways back: **A** (usual) removes the installed folder and puts the backup's earlier one back,
optionally also `data\bookmarks` and the settings file (leave those out to keep what was bookmarked since). If you
switched Jellyfin Enhanced's bookmarks off, turn them on again (**Bookmark Current Time** back on `B`), or copy only
`Jellyfin.Plugin.JellyfinEnhanced.xml` (and Custom Tabs' file) back; never the whole `plugins\configurations` folder,
which holds every plugin's settings and JE's per-user files. **B** puts the whole server back to the backup's moment,
losing **everything recorded since** (watched states, users, every plugin's data): only for a broken server.

## Using it

The UI's behaviour, for review and support; the user README has the tour.

### While watching

- **Buttons** (hidden by `ShowPlayerButtons`; the shortcut works either way): **bookmark now** (also `Shift+B`; the toast
  offers **Name it** for 8 s), **scissors** (press at the start and the end of a scene: the editor opens with the range),
  **Set end time** (ends one of this video's bookmarks here; the nearest earlier one first) and **this video's bookmarks**
  (with links to My Bookmarks and My Clips).
- **The progress bar** marks each bookmark (an amber tick; a range as a band); point for its name, click to play.
- **The name dialog**: **Save** (Enter), **Skip** (Escape), **More…** (the editor), **Always ask for a name**.
  - It suggests the **chapter's** title, selected (`SuggestChapterNames`), never a generated one ("Chapter 3" in any of
    Jellyfin's languages, a ripper's "Chapter 01", a time, only digits and punctuation; `Web/chapter-names.json`, derived
    from Jellyfin 10.11.5's `ChapterNameValue` formats).
  - **Always ask** (`AskForName`) and **Pause while naming** (`PauseWhileNaming`) are server-side preferences, defaulting
    to `AskForNameDefault`. A paused video resumes on close unless the user resumed it.
  - A second bookmark press while one is being made, or while a dialog is open, is ignored. Keys inside any Bookmarks
    dialog never reach the player's or JE's shortcuts (the kit's `scopedKeys`); an input method's Enter stays its own.
  - Save sends `IfUpdatedAt`: a rename made elsewhere meanwhile is refused (409) and the field shows the current name.

### On a video's page

**Add bookmark** opens the editor (Name, Start, End, Note, clip mode; times typed as `1:02:03.5` or set by a clock button
while playing). **My bookmarks (n)** lists the video's bookmarks: play, edit, delete, make, preview or download a clip,
**Name it** for an unnamed one, **See all** and **My Clips** links. A row's title is the name, else the note's first line
(80 characters), else "Bookmark at …" or "Range …–…". Badges: **Relinked, check position** (**Looks right** or a save
clears it), **Not in library** and **Unavailable** (no play, no clip). Deleting a bookmark with a clip offers **Also
delete its clip**: unticked while My Clips is on (the clip stays there), ticked while it is off.

### The My Bookmarks page

Route `#/jfbm-bookmarks` (never JE's `#/bookmarks`), linked from **Library tools**, or from Plugin Pages' section in the
host modes `Auto` and `PluginPagesOnly` (`#/userpluginsettings.html?pageUrl=/Bookmarks/Pages/bookmarks`).

- **Toolbar**: search (name, note, title; case and accents ignored; 300 ms after typing), type, sort, **Grouped** (25
  videos a page; Recently changed, Title A–Z, Most bookmarks) or **Flat** (50 rows a page; also Recently created, Name
  A–Z, Position in title), filters (Has range, Has clip, Needs a name, Not in library). **Load more** loads by itself on
  scroll, except on a TV.
- **The state is in the address**: `?view=flat&q=heat&type=Movie&sort=Name&range=1&clip=1&unnamed=1&missing=1&item=<id>&page=2&group=<id>`.
  `group` is the video Back returns to (once), `item` the one See all came from. Wrong values fall back to defaults.
- **Rows**: inline **Rename** / **Name it** (Enter, Escape, empty unnames; 409 as in the name dialog), play, edit,
  clip (a point bookmark's opens the editor in End), delete. On a narrow screen the actions are one **More** menu.
- **Deleting several**: checkboxes (behind a **Select** toggle on a TV), Select all, Clear, **Delete…**. More than 200 go
  200 per request, with progress; closing the dialog stops after the current request. Bookmarks the server kept (a
  file in use, a folder unavailable, a refusal) stay selected.
- **Player options** (header): the naming preferences and **the shortcut on this device** (`localStorage`
  `jfbm:shortcut:bookmarkNow`; empty uses the server's; can be turned off). Reserved keys (`ReservedShortcuts`) and
  text that isn't a key are refused.
- A change redraws only its video (and the count); a rename being typed survives a sibling's change. Hidden pages (in
  Plugin Pages' host) read nothing until shown. JE's **Hidden Content** hides a hidden video's group in the grouped
  view. Turned off (`EnableBookmarksPage`), the route says so.

### The My Clips page

Route `#/jfbm-clips` (Plugin Pages as above), linked from My Bookmarks, the clip-ready toast (**View in My Clips**), a
video's section and the player's list.

- **Toolbar**: search (also the file name), **State**, **Mode**, **Type**, **Sort** (Newest, Oldest, Name, Title,
  Largest, Longest), **Include clips of deleted bookmarks**; a summary line. Rows, 50 a page, clips being made first,
  with badges **Bookmark deleted**, **Range changed**, **Name changed**, **Last remake failed**, **In library "…"**.
- **States**: Ready, In progress, Failed or Cancelled, **File missing**, **Clip folder offline** (drive not there),
  **Clip folder not recognised** (there, but without its marker; "Missing clip files"), **Rename not finished**.
- **Address**: `#/jfbm-clips?q=&state=&mode=&type=&sort=&deleted=0&page=&highlight=<clip id>` (admins also
  `scope=all&user=<id>`). `highlight` scrolls to the clip (reading up to 20 pages), focuses it, then drops.
- **Progress**: while shown clips are being made, one `GET /Bookmarks/Clips/Status` every 2.5 s; none while the tab is
  hidden, the page left, or nothing is being made.
- Turned off (`EnableClipsPage`): no links, the route says so, `Pages.Clips` false, delete dialogs tick **Also delete
  its clip** again.

#### Row actions

| Action | Offered for | Effect |
| --- | --- | --- |
| Preview, Download | a file that is there, with download permission | Plays in a dialog (MKV too; "Download it instead" when the browser can't), or downloads with the token in the link. |
| Play | a video you can open | From the clip's start, with the bookmark's version and audio track. |
| Edit bookmark | a live bookmark | The editor on the current bookmark. |
| Rename clip | not being made, folder there | The name and, with **Also rename the file** (unticked in a library, with a warning that Jellyfin then sees a new video), the file: both or neither. |
| Rename file | **Name changed** | The file gets the clip's current name. |
| Make the clip again | a live bookmark with a range, folder not offline | The current range; the old file stays until the new one is there. |
| Cancel | being made | An earlier file stays. |
| Delete | a file that is there | A file in use keeps the row. |
| Forget, Dismiss | a file gone; a failed clip | Removes the entry; never deletes a file. |

A clip whose folder is offline or not recognised, or whose rename isn't finished, offers only Play and Edit bookmark;
read-only data leaves Preview, Download, Play, Edit. Dialogs take the focus (Cancel in the destructive ones) and give it
back. Deleting several works as on My Bookmarks.

#### The admins' All users view

Admins, while `AdminsCanManageAllClips` is on: **My clips** | **All users**. All users (`GET /Bookmarks/Admin/Clips`)
adds an **Owner** select, each row's owner ("Deleted user (id)") and full path (**Copy**, not on a TV), never a note.
Actions: Preview, Download (`/Bookmarks/Admin/Clips/{id}/File`, the admin's download permission), Play, Cancel, Delete,
Forget, Dismiss, bulk delete; no rename, remake or edit. The server runs the owner's delete code and logs the admin. A
403 meanwhile returns the page to **My clips**.

### Names

A name is one line of at most 100 characters: line breaks and tabs become spaces, other control characters go, runs of
spaces become one, NFC; only spaces means unnamed; names needn't be unique. The note is free text of at most 500. Rows
show the name, else the note; a clip file takes the name only. Renaming a bookmark keeps an existing clip file's name
(My Clips' **Rename file** follows). The editor refuses a stale save ("Changed elsewhere. Reload to see the latest.")
and **Reload** reopens it on the current bookmark.

### Clips

**Save & make clip** in the editor, or a row's scissors; needs **Allow media downloading** (otherwise no clip buttons and
403). A toast offers the download and **View in My Clips** when done. The files: "Where things are stored".

## Jellyfin Enhanced: moving its bookmarks over

Jellyfin Enhanced (JE) has its own bookmarks (a `B` key, an OSD button, a Plugin Pages page, a Custom Tab, a
`bookmark.json` per user). Bookmarks imports them once and helps switch them off. It **never writes, moves or deletes
JE's files or settings**; it reads them when its settings page loads and when the import runs. While JE's Bookmarks is
on, the settings page shows a banner (`ShowJellyfinEnhancedBanner`).

**1. Import** (Data tab). **Dry run** reports per JE user what would be found, imported, matched (by id, by TMDb,
ambiguous), left "Not in library", updated, duplicated, already imported or skipped, with up to 50 samples. **Import**
writes in one save after a snapshot (`snapshots/pre-op/bookmarks-import-*.json`).

- Each `bookmark.json` (JE 12.8 to 12.12) is matched to the user its folder is named after (two spellings read once).
  Unknown users, malformed entries and JE's cross-version copies (`SyncedFrom`) are skipped; an unreadable file (being
  written, not valid UTF-8, unknown format) is reported and the other users are imported.
- **Matching**: JE's item id when the owner can open it; else the one video in the owner's libraries with JE's TMDb id
  (an episode: series TMDb or TVDb id, season, episode); several: the first by library order, reported as ambiguous;
  none: kept **Not in library** with JE's title and ids, attaching later. A video the owner can't open is never matched.
- The label becomes the name (over 100 characters: the first 100, the whole label as the note); a position past a known
  runtime becomes a second before the end; JE's times are kept; `Origin` `JellyfinEnhanced`. The same name within a
  second on the same video is a duplicate.
- The **import ledger** makes a re-run change nothing; a JE bookmark changed in JE since is rewritten unless changed here;
  one deleted here never comes back. Labels never reach the log.

**2. Switch JE's bookmarks off** with the checklist under the import, shown once every JE user with bookmarks is covered
by an applied import. Each item ticks itself when the server sees the change:

1. JE's settings, **Pages** tab: **Add the Custom Tabs entry for me**, **Use Custom Tabs for Bookmarks**, **Use Plugin
   Pages for Bookmarks Library**, then **Enable Bookmarks Feature**, all off, **Save Settings** (JE then removes its
   Custom Tab, once its page has found Custom Tabs). JE greys the others out once the feature is off and saves them as
   they are; a box left on stays unticked here, with how to free it.
2. Custom Tabs: delete a "Bookmarks" tab still there.
3. **Restart**, and again if JE's link is still in Plugin Pages' menu (Plugin Pages 2.4 reads its list at start; JE
   removes its link at start, 12.12 also on save).
4. **Last**, JE's **Keyboard** tab: give **Bookmark Current Time** **another key, never a blank one**. JE 12.12 (a grid):
   click the `B` at the left of that row, press e.g. `Shift+X`, **Save Settings**; never **Delete** or **⊘**, which save a
   blank key. JE 12.11 (a list): choose the shortcut, type **`Disabled`** (the word) or a key into **Key**, **Add** (or
   type over its row under Current Overrides), **Save Settings**; never **Disable**. Users' own JE keys count too (a user
   whose key is `B` is named). Turning JE's Bookmarks off isn't enough: its handler still takes the key.

**Never clear JE's shortcut**: JE 12.8 to 12.12 replace a blank key by the default `B` at every start, and their
"Disable" controls write exactly that, so the checklist ticks a cleared key only with that warning. JE 12.12 reads `b` as
`B`. JE's **Disable Keyboard Shortcuts** frees `B` only while on. An item whose source can't be read (`shortcuts.json`,
JE's user folders, Custom Tabs' or Plugin Pages' files) stays unticked and says why.

**3. Use B** (optional). Bookmarks uses `Shift+B` because JE's capture-phase listener takes `B` first. Once `B` is
**durably** free (the default another key or `Disabled`, no user's own `B`, every file readable), the checklist offers
**Use B for bookmarks**; otherwise it says why not. The Settings tab's shortcut field runs the same check.

## Settings

Dashboard, Plugins, Bookmarks: tabs **Settings**, **Data** (JE import and checklist, export, import, snapshots,
maintenance, unknown users) and **Diagnostics**. Banners above them: read-only data, data recovered or started empty,
stored users that don't match the server, JE's bookmarks still on.

| Setting | Default and effect |
| --- | --- |
| `EnableWebUi` | on. Off: no bookmark UI from the next page load; the API keeps working. |
| `ReportWebAnchorProblems` | on: browsers' reports of missing web client parts are logged once and listed in Diagnostics. |
| `EnableBookmarksPage`, `EnableClipsPage` | on. Off (read per request): no links, the route says it is turned off, `Pages.*` false; for My Clips also the delete dialogs' default. |
| `PageHostMode` | `FallbackOnly` (Library tools); `Auto` (Plugin Pages' section when it shows a page); `PluginPagesOnly`. The Plugin Pages modes need that plugin and a restart. |
| `RegisterPluginPages` | on; in the Plugin Pages modes, after a restart. |
| `ShowFallbackNav` | on: the Library tools section. |
| `ShowPlayerButtons` | on. Off: no player buttons; the shortcut still works. |
| `BookmarkNowShortcut` | `Shift+B`; any key with modifiers, empty for none. Checked as typed against the player's and JE's current keys (a reserved key saves with a warning); saved normalised (`shift+b` → `Shift+B`). |
| `AskForNameDefault` | off. |
| `SuggestChapterNames` | on. |
| `ShowJellyfinEnhancedBanner` | on. |
| `DeletedUserPolicy` | `KeepAsOrphan`: bookmarks without a clip file deleted, those with one kept as clip-only tombstones. `Delete`: all, with their files. `Anonymize`: as `KeepAsOrphan`. |
| `DetachedRetentionDays` | 0: bookmarks of missing videos kept for good. N: archived after N days (and brought back when the video returns); never deleted on a timer; never one with a clip file. |
| `SnapshotsToKeep` | 7 daily snapshots; 0 none. |
| `EnableClips` | on. |
| `AdminsCanManageAllClips` | on: the All users view and admin clip routes. Off: those routes answer 403; the totals and Diagnostics stay. |
| `ClipOutputPath` | empty: `<data dir>/data/bookmarks/clips`. Free space shows under the field, with `LowFreeSpace` (under the reserve + 1 GB), `FolderUnavailable`, `FreeSpaceUnknown`; a folder in a Movies or TV library is warned of as typed. |
| `MinFreeSpaceMegabytes` | 2048: the reserve ("Free space"); 0 none. |
| `OrganizeClipsByUser` | on: a sub-folder per user. |
| `DefaultClipMode` | Accurate. |
| `MaxClipDurationSeconds` | 600 (0: no limit). |
| `MaxConcurrentClips` | 1 (after a restart). |
| `CleanUpPartialFilesOnStartup` | on ("Unfinished clip files"). |
| `MissingClipRetentionDays` | 30 ("Missing clip files"); 0 keeps for good; at most 36500 (larger stored as 36500, negative as 0). |
| Accurate encoding | x264 veryfast, CRF 20, the source's resolution at most, audio 192 kbit/s stereo. |

Developer settings, not on the page and never on a server in use: `EnablePageDemo`, `EnableDemoPageB`,
`EnableIdentityDemo`, `EnableUserDataDemo`, `EnableLibraryChangeDemo`, `EnableLibraryDemo`, `DeveloperClockOffsetDays`.

### The clip folder

Keep the default unless clips should be browsable in Jellyfin. Then: a folder of its own **on a drive with plenty of
free space**, **outside every library's folder**, with a **Home Videos and Photos** library for exactly that folder.
**Never a Movies or TV library** (the field and Diagnostics warn: `InVideoLibrary`): clips are matched to the real film
and show as its duplicates. **Everyone who can open the clip library sees every user's clips**: for privacy keep the
default, or give each user a library over their own sub-folder. In one library over all sub-folders, a user named like
an extras folder (`Other`, `Extras`, `Clips`, `Shorts`, `Scenes`, ...) has their clips hidden as extras. Every clip
folder ever used stays recorded, so changing it keeps older clips working.

## Backups: export, import, snapshots

Jellyfin's backups leave plugin data out, so the **Data** tab has Bookmarks' own:

- **Export**: `bookmarks-export-<yyyyMMdd-HHmmss>.json`, the kit's envelope (`jellyfin-plugin-export/1`): the whole
  bookmark file, the archive, the users and their names, the settings for reference; never the clip files. Downloaded
  with the session's authorization, not a token in a link.
- **Import**: **Merge** (adds new, updates older; removes nothing) or **Replace**; match users **by id** (this server) or
  **by name** (another server; a deleted user is never matched). Dry run, then Import after a snapshot. Clip paths are
  kept only inside this server's clip folders (an export can never make the server serve or delete other files); this
  server's clip folders, waiting deletes, preferences and ledger stay; settings are never applied. Refused while clips
  are being made. An exported clip job comes in as failed, with its earlier file.
- **Snapshots** (daily, and before an import, a restore, a user deletion, the policy, *Remap by name* or a purge) with
  **Restore**: after a snapshot of the current data, the bookmarks become the snapshot's; current clip folders and
  waiting deletes are kept; the snapshot's clip jobs are ended as failed ("…Make it again."). Refused while clips are
  being made or the data is read-only (but see below); newer or unreadable snapshots can't be restored.
- **Missing or unreadable data** (`DataMissing`, `DataUnreadable`): **Restore** a snapshot (one copied into
  `data\bookmarks\snapshots` from your own backup is listed too) or **Start empty** (Bookmarks never does that by
  itself). The unreadable file is kept as `bookmarks.json.corrupt-<yyyyMMddHHmmss>`, the snapshots until one is
  restored, a Warning names the admin, and everything works again at once. A readable file put back meanwhile wins:
  "restart the server to use it".
- **Deleted users** (only while `kit-users.json` can't be read): **Start the list empty**.
- **Run the daily maintenance now.**
- **Users this server doesn't know** (bookmarks of unknown ids, with last seen names): **Remap by name** (dry run first)
  or **Apply the deleted-user policy**. Nothing is deleted on its own.

With the server stopped, copying `data\bookmarks` is a backup. While it runs, copy only `snapshots` and `archive`
(written whole). To restore one, stop the server and copy it over `bookmarks.json`, or put back `snapshots` without
`bookmarks.json` and `.bak`: the next start restores the newest snapshot that holds bookmarks ("Snapshots"). Never with
1.1.1 or earlier, which start empty and rotate the snapshots away.

## Diagnostics

The tab shows `GET /Bookmarks/Admin/Diagnostics` as text: versions and the build's Jellyfin line; the bookmark file, its
schema and status, the copies kept next to it; the clip folder, clips waiting or being made, files waiting to be
deleted; **clip file renames not finished** (`ClipRenames`); the last **missing-clip sweep** (`ClipSweep`) with each
clip folder left out and why, and the confirm button; the deleted-user policy and unknown users ("Stored users don't
match this server…" when several turned up at once and nothing changed); **Bookmarks per user** (`BookmarkCounts`, counts
only, with a warning once the file is over 3 MB: every change writes the whole file, quick up to about 5 MB); and the
**web client problems** reported since the start (memory only, at most 200 rows, four web clients per user). The API
body also has the kit's `Verifier`, `Detached`, `Maintenance`, `LibraryChanges`, `JellyfinEnhanced`, and `Identity`,
`JeImport` and `ClipRoots`.

## Where things are stored

Under `<data dir>/data/bookmarks/` (`<data dir>` is `C:\ProgramData\Jellyfin\Server` on the Windows installer):

| Path | What |
| --- | --- |
| `bookmarks.json`, `.bak` | The data (schema 2), through the kit's JSON store, and its previous version. |
| `bookmarks.json.v1-<time>` | 1.0's file, kept by the upgrade until you delete it. |
| `bookmarks.json.corrupt-<time>` | A file moved aside as unreadable (UTC). |
| `bookmarks.json.keep-snapshots`, `.last-restore` | The snapshots kept after a recovery; the snapshot restored last. |
| `snapshots/`, `snapshots/pre-op/` | Daily snapshots and those before an operation. |
| `archive/bookmarks-archived.jsonl` | The archive. |
| `kit-users.json` | The kit's list of deleted or unknown users. |
| `clip-rename-<id>.json`, `renamed-clips.json` | A clip rename under way; the renames' history. |
| `clips/` | The default clip folder. |
| `userdata-demo.json`, `library-demo.json` | Developer demos only. |

Each clip folder holds a marker, `.jfbm-clip-root`. Settings: `plugins/configurations/Jellyfin.Plugin.Bookmarks.xml`.

### The bookmark file

- **Schema 2** (1.0.0 wrote 1) holds, per bookmark, the name; its video's identity (kind, title, year, provider ids,
  runtime, series and episode numbers, path: shown only by admins' tools); `Origin` (`Native`, `JellyfinEnhanced`;
  `Adopted` reserved, not written); attach, detach and deletion times; per clip a size estimate and "missing since". Per
  file: `ClipRoots`, `ClipRootTokens`, naming preferences, the JE ledger (`ImportRuns`), `KnownUsers`, the move-journal
  position (`MoveJournalId`, `LastMoveSeq`) and `PendingFileDeletes`.
- Saves are flushed, then atomically replace the file; a failed save changes nothing (503). Unknown fields are kept.
- An unreadable file is moved aside and the `.bak` used; else the newest snapshot holding bookmarks is restored; else the
  data is read-only. Bookmarks never starts empty on its own over a file that may hold bookmarks.

### Read-only data

| `StoreReadOnlyReason` | Cause | Listed |
| --- | --- | --- |
| `NewerVersion` | Written by a newer Bookmarks. | As far as readable; exportable. |
| `MigrationFailed` | The upgrade from 1.0 failed; the file is untouched. | Nothing; no export (the file and its `.v1-*` copy are the backup). |
| `DataMissing` | `bookmarks.json` and `.bak` missing or empty, and no snapshot holding bookmarks can be restored. | Nothing. |
| `DataUnreadable` | `bookmarks.json` (or `.bak`) unreadable, and nothing could take its place; left where it is. | Nothing. |

Every change answers 503 with the store's sentence; nothing writes on its own (the clip worker, the `.partial` clean-up,
the identity service and the maintenance stay idle, each logged once by
`Jellyfin.Plugin.Bookmarks.Storage.BackgroundWriteGate`); downloads still work. The capabilities carry
`StoreReadOnly`/`StoreReadOnlyReason`; the settings page shows the sentence as a banner; the pages tell non-admins to ask
the administrator. The first two clear when the server restarts with writable data. **The way out of `DataMissing` and
`DataUnreadable`**: on the Data tab restore a snapshot or start empty; or, with the server stopped, copy a snapshot over
`bookmarks.json` (a newer version's reads as `NewerVersion`), give the server's account read access to the snapshot the
banner names, put a readable `bookmarks.json` back, or install the version that wrote the snapshots.

### Clips

- **Writing.** Before a job writes, its clip folder (without the user sub-folder) is recorded in `ClipRoots` (at most
  50; a 51st fails the job). A clip is written as `<name>.partial` and renamed when complete, never over a file. Jobs
  survive a restart and resume after startup and the `.partial` clean-up.
- **Remakes** keep the old file until the new one is written, and keep its name when it is still right (written beside
  it, then replacing it). If the old file is in use or its name is taken, the new clip gets ` (2)` and the old file goes
  once the bookmark points at the new one. A failed remake leaves the old file.
- **Only clip files in the clip folders are touched**: `.mp4` or `.mkv` strictly inside a recorded folder, no junction,
  link or mount point on the way. A failing path can't be downloaded or deleted (404). A folder that isn't there is
  never taken for files that are gone: deletes there are 503 and change nothing.
- **Files in use**: a delete removes the file first (three tries 0.2 s apart), then the record; still in use, 409 "The
  clip file is in use (being played or downloaded); try again shortly." and nothing changes. Files nobody waits for go to
  `PendingFileDeletes` (at most 1,000), retried at start and daily.
- **Clip-only tombstones**: deleting a bookmark without its clip keeps the record (`DeletedUtc`, name, note, the clip
  reduced to its file) and stops a running job. Bookmark routes answer 404; the file still downloads, and deleting the
  clip (204) removes file and record. My Clips lists them.

#### Unfinished clip files

At each start (a dashboard restart too), before jobs resume, `.partial` files a stopped server left are deleted in every
recorded clip folder that is there and one level below (never through a junction, link or mount point): only names
Bookmarks gives a clip being written (`<name> [hh.mm.ss-hh.mm.ss].mp4.partial` or `.mkv.partial`, with ` (n)`, or the
32-hex-digit fallback) last written before this start. A file in use waits; a missing folder is left alone. One log line
of counts (`Jellyfin.Plugin.Bookmarks.Clips.PartialFileJanitor`).

#### Free space

- **Estimate**: Fast, the video's bitrate (or size over runtime) × length + 5 %; Accurate, 3, 5, 10 or 25 Mbit/s for up
  to 576, 720, 1080 lines or more (at most 1.2 × the source bitrate) plus audio, × length + 10 %. Kept with the job
  (`EstimatedBytes`).
- **Asked for, and again at start**: the folder's free space minus this and the other waiting or running estimates must
  leave `MinFreeSpaceMegabytes`, else 503 (an admin's message names drive and numbers) and nothing is queued.
- **While writing**, every 5 s: below half the reserve the job stops ("Stopped: the clip drive is almost full.") and its
  `.partial` is deleted. A full disk (ffmpeg's "No space left on device" or exit code -28, a full-disk error when
  reserving or moving) fails it with "The clip drive is full.".
- Read with `GetDiskFreeSpaceExW` on the nearest existing folder (right through junctions, mount points and shares,
  where `DriveInfo` fails). No folder or a dead junction: unavailable, clips refused. Unreadable free space (access
  denied, a share silent for 10 s): the guard is off for it, with one Warning. Logged by
  `Jellyfin.Plugin.Bookmarks.Clips.ClipStorageGuard`, never a path or name.

#### Clip file renames

The file moves first, the data second. Just before the move a note `clip-rename-<id>.json` (paths, size, times, file id,
new name) is flushed to disk; the moved file is flushed (`FlushFileBuffers`) before the data names it; the note goes
once it does. A note left by a crash is finished from what is on disk at the next start, daily (the maintenance's
`ClipFiles` step) and before the next action on that clip; until then nothing deletes, forgets or moves the clip (503),
the sweep leaves it, downloads follow the file, and the row says **Rename not finished**. The file is told apart by its
file id and volume (else size and last write time), never its creation time. An unreadable note (over 64 KB, not an
entry) holds the clip until an admin removes it. `renamed-clips.json` keeps the newest 2000 finished renames, so a
restored snapshot or an import that names a clip's old path is pointed at the renamed file again (never at a file another
record names; else the clip reads failed and the file is left alone).

#### Missing clip files

The daily maintenance's last step (`MissingClips`) marks a clip whose file is gone from its own clip folder
(`MissingSinceUtc`), and clears the mark when the file is back.

- **Its own clip folder** is proved by its **marker**, `.jfbm-clip-root` (`RootId=`, 12 hex characters of the SHA-256 of
  its path; `Token=`, 32 random hex digits recorded in `ClipRootTokens`). The worker writes it with a folder's first
  clip; a folder from 1.1 gets it from the first run that finds its clips there and **none gone** (one clip isn't proof:
  a stray folder holding a clip written while the real one was away would otherwise prove itself). A marker with another
  token, or none while one is recorded, is another folder's (an old copy). An exact copy, marker included, can't be told
  apart. Leave the file there.
- **Folders left out** whole (nothing marked, archived or forgotten; Diagnostics' `UnavailableFolders`): `NotThere`;
  `NoMarker` (none of its clips there); `Unproven` (some there, some gone); `MarkerNotWritten` (let the server write
  there); `AllMissing` (every clip gone at once, two or more newly; a single deleted clip is still marked).
- **Gone for good**: an admin presses **This is the clip folder; its clips are gone**
  (`POST /Bookmarks/Admin/ClipFolders/{rootId}/Confirm`): after a dry run and a snapshot the folder is made if absent and
  given a new marker, and its clips without a file are marked missing. Owners can then forget them. The default clip
  folder, if deleted, is made again by itself (it can't be an unplugged drive).
- **Retention**: after `MissingClipRetentionDays`, a deleted bookmark's kept clip moves into the archive (`ClipMissing`)
  and a live bookmark's clip entry is forgotten; decided again under the store's lock after one more look.
- It never deletes, moves or renames a clip file, never changes `UpdatedAt`, and logs counts
  (`Jellyfin.Plugin.Bookmarks.Clips.MissingClipSweep`; paths at Debug).
- **A clip folder away is never made again** by a clip job (it would make every clip in it look deleted): the job fails
  with "The clip folder isn't available right now…" or, for `NoMarker`/`Unproven`, a sentence naming the remedy.

### Snapshots

The kit's (see [kit/README.md](../../kit/README.md), its JSON store section): daily (`SnapshotsToKeep`) and `pre-op/`
(5 per operation); none while read-only.

- **At start**, with `bookmarks.json` missing, empty or unreadable and no usable `.bak`, the newest snapshot that holds
  bookmarks is restored (`RestoredFromSnapshot`, a banner, a Warning); empty snapshots don't count, unreadable ones are
  passed over and named, one dated over a day ahead is tried last. Its clip jobs are marked failed. When a `.bak` was
  used but a newer snapshot holds other data, the banner names it.
- **Kept snapshots**: after a recovery or an empty start, `bookmarks.json.keep-snapshots` names the snapshots there were,
  kept until one is restored (or the marker is deleted with the server stopped); newer ones rotate as usual. A marker
  whose snapshots are all gone is released at the next daily snapshot; its time is never more than a week ahead. A 1.1
  marker (a time) is converted once. Rotation never deletes a snapshot dated more than a day ahead.
- A snapshot held open by another program makes requests answer 503 until the next one can read it.

### Archive and kit-users.json

- `archive/bookmarks-archived.jsonl`: append-only, in exports. Bookmarks of videos gone longer than
  `DetachedRetentionDays` (moved back when the video returns) and kept clips gone longer than `MissingClipRetentionDays`
  (`ClipMissing`, never back). Only *Purge* deletes (after a copy). Exports carry `ArchiveReason` and
  `ArchiveReasonSinceUtc`. Bookmarks 1.1 may reattach a `ClipMissing` line; 1.2 archives it again; no file is touched.
- `kit-users.json`: users whose data is kept after they were deleted or found unknown. Never started empty on its own:
  while unreadable a banner shows (`Users.Marks`) and *Remap by name* and imports by name answer 409, since a deleted
  user's bookmarks could go to a new user of that name. **Start the list empty** keeps the old file as
  `kit-users.json.corrupt-<time>`; users deleted meanwhile may be lost from it.

### The upgrade from 1.0

Runs once, the first time a version 1 file is read:

- The file is copied to `bookmarks.json.v1-<yyyyMMddHHmmss>` (via `.tmp`, so it is complete); never deleted by the plugin.
- A one-line note of at most 100 characters becomes the name. **Reload tabs open before the upgrade**: 1.0's script
  knows no names; its updates keep the name (no `Name` and no `IfUpdatedAt`) but set the note to what its editor shows.
- 1.0's item type and provider ids become the identity, completed by the backfill. A bookmark whose video was already
  gone gets no runtime: "Not in library", never moved onto another copy, back only with its own file at its path.
- `ClipRoots` gets the default, configured and every clip file's folder; `KnownUsers` the owners' names.
- Saved at once ("Migrated"; then the `.bak` is the upgraded file, so the way back is the `.v1-*` copy). Interrupted, it
  runs again at the next start.
- **If it fails** (a file 1.0 reads but this version can't convert faithfully, such as a repeated property), nothing
  changes and the data is `MigrationFailed`; the log names the step. Install the previous version, or fix and restart.

### Going back to 1.0

1. Exit Jellyfin.
2. Install the 1.0.0 release (`bookmarks/v1.0.0`; its web UI needs File Transformation; `Install-RealServer.ps1` refuses
   that tag, so build it from a checkout) and remove `plugins\Bookmarks_<version>`.
3. Replace `data\bookmarks\bookmarks.json` with the `.v1-*` copy the upgrade named (Diagnostics, the log).
4. Start Jellyfin.

Everything since the upgrade is lost. Clip files written since stay in the clip folder untracked, and a bookmark remade
under ` (2)` no longer downloads in 1.0 (make it again). Without the copy, 1.0.0 built from this repository reads schema
2 read-only; **the first 1.0.0 release would rewrite it as version 1 and drop every name**. Checked by
`bookmarks-upgrade-probe.mjs`.

### Missing and moved videos

Jellyfin gives a moved or renamed file a new id. Each bookmark keeps its video's identity, and
`Services/BookmarkIdentityService` (on the kit's item identity and record lifecycle) keeps them current:

- **At start**: Library Maintenance's journal, then the **backfill** of what 1.0 never stored, one lookup per video,
  100 videos at a time.
- **A video removed** (a scan, a real-time change, or the kit's verify sweep at start and daily): its bookmarks, and
  those of a removed series' or season's episodes, become **"Not in library"** (`Detached`, `DetachedUtc`), keeping
  everything; still editable, renamable and deletable.
- **Found again** (scans and daily): **exactly** (same id, a journal move, or the same path) keeping media source and
  audio track; or **by provider ids** (TMDb, IMDb, TVDb; an episode by its series' ids and numbers) only when it is the
  **only** candidate and its **runtime is within 2 % and at most 60 s**: media source and audio track cleared,
  **"Relinked, check position"** (`Relinked`) until the next edit. Two candidates or a different runtime (another cut)
  leave it "Not in library" (ambiguous). Without a stored runtime only a JE import that never found its video takes the
  one candidate.
- **Library Maintenance's move journal** (`<data dir>/data/librarymaintenance/moves.jsonl`), at start and when Library
  Maintenance calls `Jellyfin.Plugin.Bookmarks.Integration.ItemMovedHookV1.OnItemsMoved`: Committed lines move bookmarks
  to the new id; the position is stored. A new journal replays from the start; nothing moves twice.
- None of this changes `UpdatedAt`, names, notes, positions or clips. Idle while read-only.

### Deleted users

- Only an explicit deletion changes data: Jellyfin deleting a user, or *Apply deleted-user policy*. A snapshot comes
  first. `KeepAsOrphan`/`Anonymize`: bookmarks without a clip file go, those with one become tombstones; `Delete`: all,
  with their files. Running clips stop; preferences go.
- **No text stays**: tombstones keep no name or note (listed by file name), archived bookmarks are removed (after a copy),
  the rename history goes, and the `.bak` is rewritten. While the archive can't be read nothing changes (503; Jellyfin's
  deletion is retried). Earlier snapshots keep the text until they rotate (7 daily, 5 per operation); a kept clip's file
  name and title keep the name, and its tombstone the path. Only `Delete` removes those.
- The daily maintenance only compares: unknown user ids are listed, never deleted. Several at once: nothing is marked and
  Diagnostics suggests *Remap by name* (via `KnownUsers`).
- Users deleted before 1.2.1 keep their text until an admin applies the policy to them
  (`POST /Bookmarks/Admin/Users/ApplyPolicy`).

### For other plugins

Two public static classes in `Jellyfin.Plugin.Bookmarks.Integration`, BCL types only, found through
`AssemblyLoadContext.All` by the assembly name `Jellyfin.Plugin.Bookmarks` and called by reflection; neither throws into
nor blocks its caller:

- `ItemMovedHookV1.OnItemsMoved(string)`: Library Maintenance's move hook.
- `ClipRootsV1.Get()`: a `string[]` of every folder Bookmarks writes clips to (recorded and current), absolute, without a
  trailing separator (compare case-insensitively on whole segments). It is meant for Library Maintenance's Duplicates,
  to leave clips in a library out of its search; Library Maintenance 1.0.4 doesn't call it yet (its Excluded folders
  setting does that job), nor `ItemMovedHookV1` (it moves nothing).
  **An empty array means "not answering"** (before startup completes, on another Jellyfin line, after shutdown), never
  "no folders": don't keep it. A non-empty answer may be kept (folders are only added). Diagnostics' `ClipRoots` shows
  calls and answers.

## REST API

- **Who:** the user routes need a signed-in user and **refuse API keys** (403 "Bookmarks require a signed-in user; API
  keys can't be used."), since bookmarks belong to users. `Admin` routes need Jellyfin's admin check
  (`RequiresElevation`, per request), which an API key passes.
- **Format:** ticks (10,000,000 per second); problem details for errors; null fields left out; a POST reads at most
  64 KB (413; the kit's filter, where Jellyfin would answer 500), the import 100 MB, the client report 16 KB.
- **Read-only data:** every change answers 503 before any lookup (only the 403 of clips off or no download permission
  comes first).
- **Concurrency:** `UpdatedAt` moves on (by at least a millisecond) with every user change, never with a clip job.
  `IfUpdatedAt` (the text sent, or a JavaScript `Date` of it) makes a stale update or rename a 409 "Changed elsewhere.
  Reload to see the latest.", checked inside the write. Without it the last change wins.
- **Schemas** all start with `Bookmark` (one clash makes Jellyfin's whole OpenAPI document a 500;
  `Api/ApiConventionTests`). Every property 1.0.0 sent is unchanged (`Api/WireFormatTests`). `BookmarkClipState`:
  `Ready`, `InProgress`, `Failed`, `Missing`, `FolderUnavailable`, `RenameUnfinished`.
- **Strict queries:** a sort the list lacks, an unknown enum name, a limit outside 1 to 200, a negative start or a search
  over 100 characters is 400 (the kit's `StrictEnumQuery`; Jellyfin would silently ignore it), also under `filter.`.
  Lists are Jellyfin's `QueryResult`; multiple values comma-separated or repeated.

### Bookmarks

| Method | Route | Notes |
| --- | --- | --- |
| GET | `/Bookmarks?itemId=` | Your bookmarks. |
| GET | `/Bookmarks/Query` | 50 a page. `searchTerm` (name, note, video, series; case and accents ignored), `itemId`, `itemTypes` (`Movie`, `Episode`, `Other`), `hasRange`, `hasClip`, `named`, `availability` (stored state, no library lookup); `sortBy` `UpdatedAt`, `CreatedAt`, `Name`, `ItemName`, `Position`; `sortOrder`. |
| GET | `/Bookmarks/Groups` | Same filters, 25 videos a page, at most 100 bookmarks each (`BookmarksTruncated`); `sortBy` `LatestUpdated`, `ItemName`, `Count`. |
| GET | `/Bookmarks/{id}` | One bookmark with clip status and progress. |
| POST | `/Bookmarks` | `{ItemId, StartTicks, EndTicks?, Name?, Note?, MediaSourceId?, AudioStreamIndex?}`. |
| POST | `/Bookmarks/{id}` | **Replaces** every field: `{StartTicks, EndTicks?, Name?, Note?, IfUpdatedAt?}`. 1.0's body (no `Name`, no `IfUpdatedAt`) keeps the name. |
| POST | `/Bookmarks/{id}/Name` | `{Name?, IfUpdatedAt?}`; white space unnames; always a change; clears "Relinked". |
| DELETE | `/Bookmarks/{id}?deleteClip=` | Keeps a clip file as a tombstone unless `true`; then the file first (409 in use, 404 outside the clip folders, 503 folder unavailable or unrecognised). |
| POST | `/Bookmarks/BulkDelete` | `{Ids (1–200), DeleteClips}` → `{Deleted, Tombstoned, NotFound, InUse, Failed}`. |
| GET, POST | `/Bookmarks/Preferences` | `{AskForName?, PauseWhileNaming}`; POST replaces both (204). |
| POST | `/Bookmarks/{id}/Clip` | `{Mode?}`, 202; 503 without the free space or the folder. |
| POST | `/Bookmarks/{id}/Clip/Cancel` | An existing file stays. |
| DELETE | `/Bookmarks/{id}/Clip?onlyIfMissing=` | File first: 200 (204 for a tombstone), 409 in use. `onlyIfMissing=true` never deletes a file (409 when there). |
| GET | `/Bookmarks/{id}/Clip/File?download=` | The file (a tombstone's too), with download permission. |
| GET | `/Bookmarks/Capabilities` | Below. |
| POST | `/Bookmarks/Diagnostics/ClientHealth` | A browser's missing-parts report, checked against `Web/anchors.json`, one per user per ten minutes; 204. |
| GET | `/Bookmarks/ClientScript` | The web script (anonymous). |
| GET | `/Bookmarks/Pages/{key}` | A page shell (`bookmarks`, `clips`, demo `demo-a`, `demo-b`); turned off: 200 with its sentence, so Plugin Pages shows it; else 404. |

`BookmarkDto` has `Name`, `Detached`, `Relinked`, `Origin`, `Year`, `SeriesName`, `SeasonNumber`, `EpisodeNumber` (stored
identity), and in the two lists `ItemAvailable` (`GetItemById<Video>(id, user)`, once per video per request).
**Capabilities**: `IsAdmin`, `StoreReadOnly`, `StoreReadOnlyReason` (only while read-only), `MaxNameLength`,
`MaxNoteLength`, `Shortcuts`, `ReservedShortcuts` (player keys and JE's as 12.11 and 12.12 read them, the caller's own JE
keys included), `ShowPlayerButtons`, `AskForName`, `AskForNameIsDefault`, `AskForNameDefault`, `PauseWhileNaming`,
`SuggestChapterNames`, `Pages {Bookmarks, Clips}`, `CanDownloadClips`, `CanManageAllClips`, and for admins
`JellyfinEnhancedImportPending`.

### The clips

The caller's own (tombstones' too).

| Method | Route | Notes |
| --- | --- | --- |
| GET | `/Bookmarks/Clips` | 50 a page. `searchTerm` (also the file name), `state`, `mode`, `itemTypes`, `includeDeleted` (default true), `itemId`; `sortBy` `Newest`, `Oldest`, `Name`, `ItemName`, `Size`, `Duration`; being made first. |
| GET | `/Bookmarks/Clips/Summary` | `{Count, TotalBytes, InProgress, Failed, Missing, FolderUnavailable}`. |
| GET | `/Bookmarks/Clips/Status?ids=` | 1–200 ids: `{Id, Status, State, Progress, Error, FileName, FileSize}`. |
| POST | `/Bookmarks/Clips/BulkDelete` | `{Ids}` → `{FilesDeleted, RecordsRemoved, NotFound, InProgress, InUse, FolderUnavailable, Failed}`. |
| POST | `/Bookmarks/Clips/{id}/Name` | `{Name, RenameFile, IfUpdatedAt}`: name, and file, both or neither. |
| POST | `/Bookmarks/Clips/{id}/RenameFile` | The file to the clip's name; optional `{IfUpdatedAt}`. |

- An item's `Clip` adds `State`, `Container`, `DurationTicks`, `FileExists`, `IsNameOutdated`, `LastJobFailed`,
  `InLibrary`, `LibraryName` (only a library the caller may open), `RootAvailable`, `MadeAt`, the file name (never its
  folder). The state is read from the file system per request (kept 30 s; library folders 60 s; Bookmarks' own file
  changes judged at once). An unplugged folder's clips are `FolderUnavailable`, never `Missing`; a gone file in a folder
  without its marker is `FolderUnavailable` with `RootAvailable` true.
- **Renames**: the new name is the clip's, " (2)" when taken by any file, folder, `.partial`, record path or waiting
  delete (case-insensitive on Windows), never over a file; the file moves inside the record's write. 400 too long; 404
  no clip, file gone, outside the folders; 409 being made, stale, in use, another record's file; 503 read-only, folder
  offline or unrecognised, a failed write (the file is put back), an unfinished rename. Nothing changes in any of them.

### Admin

| Method | Route | Notes |
| --- | --- | --- |
| GET | `/Bookmarks/Admin/Diagnostics` | See "Diagnostics"; also `ClipStorage` (`Root`, `Exists`, `FreeSpace`, `FreeBytes`, `MinFreeBytes`, `Waiting`, `PendingFileDeletes`, `Warnings`, …). |
| POST | `/Bookmarks/Admin/Import/JellyfinEnhanced?dryRun=` | Dry run unless `false`; optional `{UserIds}` (≤ 1000). `Found = Import + Updated + Duplicates + AlreadyImported + Skipped`; `Import = MatchedById + MatchedByProviderId + Ambiguous + Detached`; ≤ 50 `Samples`; `Checklist`; `CanUseB`. 404 no JE bookmarks; 503 JE's folder unreadable or (real import) read-only. |
| POST | `/Bookmarks/Admin/Users/Remap?by=Name&dryRun=` | After a snapshot; 409 read-only or `kit-users.json` unreadable. |
| POST | `/Bookmarks/Admin/Users/ApplyPolicy` | `{UserIds}` (1–1000, none current); 503 when the user list is unreadable or empty. |
| POST | `/Bookmarks/Admin/Detached/Purge?olderThanDays=&includeArchive=` | The only hard delete of missing videos' bookmarks; never one with a clip file. |
| GET | `/Bookmarks/Admin/Export` | Attachment, `no-store`; 503 after a failed upgrade or with an unreadable archive. |
| POST | `/Bookmarks/Admin/Import?mode=&dryRun=&mapUsersBy=` | `Merge`/`Replace`, `Id`/`Name`; 409 newer schema, read-only, `Name` with `kit-users.json` unreadable, clips being made. |
| GET | `/Bookmarks/Admin/Snapshots` | With `Kept`, `KeptBecause`, `Recoverable`, `ClipsBeingMade`. |
| POST | `/Bookmarks/Admin/Snapshots/Restore?name=` | `ClipJobsEnded`; while `Recoverable`, the way out (`MovedAside`). |
| POST | `/Bookmarks/Admin/Data/StartEmpty` | Only while missing or unreadable. |
| POST | `/Bookmarks/Admin/Users/Marks/StartEmpty` | Only while `kit-users.json` is unreadable. |
| POST | `/Bookmarks/Admin/Maintenance/Run` | 202. |
| GET | `/Bookmarks/Admin/Shortcuts/Check?combo=` | The kit's shortcut policy. |
| POST | `/Bookmarks/Admin/ClipFolder/Check` | `{Path}` → `{Path, Valid, Warnings}`; changes nothing. |
| POST | `/Bookmarks/Admin/ClipFolders/{rootId}/Confirm?dryRun=` | Dry run by default; no file deleted. |
| GET | `/Bookmarks/Admin/Clips` | `AdminsCanManageAllClips` on (else 403): every user's clips (`userId`) with `UserId`, `UserName`, `UserDeleted`, `FilePath`, `RootId`, **no `Note`**; videos the admin can open (gone ones with `EnableAllFolders`; an API key all). Other ids are 404 below. |
| GET | `/Bookmarks/Admin/Clips/Summary` | Per-user totals, also with the setting off. |
| GET | `/Bookmarks/Admin/Clips/Status?ids=` | For the poll. |
| GET | `/Bookmarks/Admin/Clips/{id}/File?download=` | With ranges; download permission; logged. |
| POST | `/Bookmarks/Admin/Clips/{id}/Cancel` | |
| DELETE | `/Bookmarks/Admin/Clips/{id}?onlyIfMissing=` | The owner's delete code; 204. |
| POST | `/Bookmarks/Admin/Clips/BulkDelete` | As the clips' bulk delete. |
| various | `/Bookmarks/Admin/{IdentityDemo,LibraryDemo,LibraryChangeDemo,UserDataDemo}/…` | Developer demos: 403 unless turned on. |

## Development

Built in this monorepo with its root settings and the shared kit (`kit/build/Kit.props`;
[kit/README.md](../../kit/README.md)):

```powershell
dotnet build Jellyfin.Plugin.Bookmarks
dotnet test tests/Jellyfin.Plugin.Bookmarks.Tests
```

Or `dotnet test jellyfin-plugins.slnx` from the root. With Node on the PATH, `dotnet test` also checks the built web
script (`node --check`, the kit's checks, the `Web/*.test.mjs` suites in the kit's fake page). ffmpeg tests use
`BOOKMARKS_TEST_FFMPEG`, else the installed Jellyfin's, else the PATH's, and are skipped without one.

**Server** (`Jellyfin.Plugin.Bookmarks/`): ten controllers (`Api/`) over public interfaces (`IBookmarkManager`,
`IBookmarkRecordsAdmin`, `IBookmarkQuery`, `IBookmarkPreferences`, `IBookmarkClips`, `IBookmarkAdminClips`,
`IBookmarkJeImport`, `IBookmarkAdminTools`) answering DTOs (`Api/Models`). Records, the store, the mapper and every
implementation are internal; no controller touches a record (`Api/PublicBoundaryTests`). `Storage/` (store, migration,
`BackgroundWriteGate`), `Clips/` (`ClipExtractionService`, `ClipFileOps` over `IClipFileSystem`, `ClipStorageGuard`,
`PartialFileJanitor`, `MissingClipSweep`, `ClipFileRenamer`, `ClipQueryService`, `ClipStateCache`), `Services/`,
`Import/`, `Tasks/`, `Integration/`.

**Web** (`Web/js/*.js`, in name order): `10-selectors.js` (jellyfin-web's elements by the names in `Web/anchors.json`),
API, editor, lists, name dialog, player, timeline, details, the pages (`60-page-bookmarks.js`, `70-page-clips.js`,
`75-page-clips-admin.js`), the page demo, `90-main.js`; `Web/css/bookmarks.css`; on the kit's `kit.js`/`kit.css`, built
into one script in the DLL (restart the sandbox after a change). The settings page is `Configuration/configPage.html`.

### Tests

Each area has unit tests, most also behind Jellyfin's real web pipeline (`Api/*ApiTests`), and a sandbox probe
([scripts/README.md](../../scripts/README.md#bookmarks-checks), "Bookmarks checks"):

| Area | Tests | Sandbox |
| --- | --- | --- |
| Wire format, schemas, boundary | `Api/WireFormatTests` (`Fixtures/api-wire-1.0.json`, `AddedSinceSnapshot`), `ApiConventionTests`, `PublicBoundaryTests` | `bookmarks-api-probe.mjs` |
| Store, upgrade, read-only | `Storage/BookmarkStore*Tests` (the kit's conformance suite on the 1.0.0 fixture, migration, read-only, upgrade crash), `Api/ReadOnlyStoreApiTests`, `Storage/BackgroundWriterCoverageTests` | `bookmarks-upgrade-probe.mjs` |
| Tombstones, log privacy | `Services/TombstoneTests`, `Clips/TombstoneRaceTests`, `Api/TombstoneApiTests`, `Clips/ClipLogPrivacyTests` | |
| Clip files, guard, `.partial`, free space | `Clips/ClipFileGuardTests`, `ClipFileOpsTests`, `ClipFileWorkerTests`, `FileOperationsCoverageTests`, `PartialFileJanitorTests`, `DriveSpaceProbeTests`, `ClipStorageGuardTests`, `ClipWorkerFfmpegTests` (real ffmpeg), `Services/ClipFileDeleteTests` | `clip-guard-probe.mjs` |
| Names | `Services/BookmarkNamesTests`, `BookmarkNamingTests`, `Api/BookmarkNameApiTests`, `Clips/NamedClipFfmpegTests` | `bookmarks-api-probe.mjs --names` |
| Identity, deleted users | `Services/BookmarkIdentityServiceTests`, `BookmarkUserDataOwnerTests`, `Api/RecordsAdminApiTests`, `Integration/ItemMovedHookV1Tests` | `bookmarks-identity-probe.mjs`, `bookmarks-data-probe.mjs` |
| My Bookmarks | `Services/BookmarkQueryServiceTests`, `BookmarkBulkDeleteTests`, `BookmarkPreferencesTests`, `Api/BookmarkQueryApiTests`, `Web/page-bookmarks.test.mjs` | `bookmarks-query-probe.mjs`, `bookmarks-page-probe.mjs` |
| Editor, player | `Web/bookmarks.test.mjs`, `Web/chapter-names.test.mjs`, `Web/ChapterSuggestionTests` | `bookmarks-rows-probe.mjs`, `bookmarks-player-probe.mjs` |
| JE import | `Import/JellyfinEnhancedBookmarkImporterTests` (`kit/testdata/je-data/import-81`), `Api/JeImportApiTests` | `bookmarks-je-import-probe.mjs`, `bookmarks-je-web-probe.mjs` |
| Settings and admin tools | `Web/configpage.test.mjs` (the page's real markup), `Web/SettingsTests`, `Services/BookmarkCountsTests`, `Api/AdminToolsApiTests` | `bookmarks-settings-probe.mjs` |
| My Clips | `Clips/ClipQueryServiceTests`, `ClipStateCacheTests`, `ClipServiceTests`, `Api/ClipsApiTests`, `Web/page-clips.test.mjs` | `bookmarks-clips-probe.mjs`, `bookmarks-clips-page-probe.mjs` |
| Missing clips, markers, facade | `Clips/MissingClipSweepTests`, `ClipRootMarkerTests`, `Integration/ClipRootsV1Tests` (by reflection, its own load context) | `bookmarks-clip-sweep-probe.mjs` |
| Clip renames | `Clips/ClipRenameTests*`, `ClipRenameRestoreTests` | `bookmarks-clip-rename-probe.mjs` |
| All users view | `Clips/AdminClipServiceTests*`, `Api/AdminClipsApiTests`, `Web/page-clips-admin.test.mjs` | `bookmarks-clips-admin-probe.mjs` |

### The sandbox

```powershell
./scripts/Initialize-Sandbox.ps1 -Name default     # build, deploy, start on http://127.0.0.1:8097, seed users and media
node scripts/webcheck.mjs --sandbox default --steps bookmarks-details.json
./scripts/Stop-Sandbox.ps1 -Name default
```

Your installed `jellyfin.exe` with its own folders under `.sandbox\<name>`, on 127.0.0.1 only. `dev-server.ps1 -Name
default -Background` rebuilds and redeploys (stop the sandbox first). Users `admin`, `viewer` (no download permission)
and `viewer2`, passwords in `.sandbox\<name>\credentials.json`. The `bookmarks-injection.json`, `bookmarks-ui.json` and
`bookmarks-phone.json` webchecks check the whole UI.

### Background work and developer demos

- **Library changes**: the kit's monitor batches item events after startup; `Library/BookmarksPostScanTask` hands each
  scan's changes over as one full pass (Jellyfin's own post-scan changes follow in one more batch). The clip worker runs
  on the kit's background-service base: it starts after startup and restarts 30 s after a failure.
- **"Bookmarks: Maintenance"** (`Tasks/BookmarksMaintenanceTask`, daily 03:10): the snapshot, the kit's retention, user
  reconciliation, verify sweep, re-resolution and reattach, waiting deletes and renames (`ClipFiles`), and the
  missing-clip sweep. The verify sweep also runs after each start and after folder removals. A **Debug** build moves the
  lifecycle clock on by `DeveloperClockOffsetDays`.
- **Developer demos** (off; never on a server in use; their probes turn them on and off; "Kit and web hosting checks" in
  [scripts/README.md](../../scripts/README.md#kit-and-web-hosting-checks)): `EnablePageDemo` (+ `EnableDemoPageB`; pages
  `#/jfbm-demo-a`, `#/jfbm-demo-b`), `EnableIdentityDemo` (read-only item identity routes; ids resolve as the admin, no
  library bypass), `EnableUserDataDemo` (`userdata-demo.json`, the kit's users and data services),
  `EnableLibraryChangeDemo` (the last 50 batches, never a name or path), `EnableLibraryDemo` (`library-demo.json`, the
  kit's record lifecycle with Bookmarks' relink policy; registered before the identity service).
- **A simulated missing element**: a build with `-p:KitRenameAnchors=detailButtons` loses its Add bookmark button, to
  check the report path (`health-*.json` webchecks). Sandbox only (`Deploy-Plugin.ps1 -NoBuild`).

## Notes and limitations

- **Clips**: regular video files only (no disc folders, ISOs, remote streams); the video and the playing (or default)
  audio track, no subtitles. Download links carry the token in the query (`ApiKey`), as Jellyfin's own do.
- **Backups**: the export holds clip records, never clip files; Jellyfin's backups hold neither. Back up the clip folder
  (the default one is inside `data\bookmarks`).
- **Offline or unrecognised clip folders** are never taken for gone clips: only Play and Edit are offered, the
  maintenance leaves them, new clips are refused, and an admin confirms a folder whose clips are really gone.
- **Web client coupling**: the UI depends on jellyfin-web 10.11's structure (`.videoOsdBottom .buttons`,
  `#itemDetailPage .detailPageContent`, `.mainDetailButtons`, ...), declared with fallbacks in `Web/anchors.json`, and
  reads the position from `ApiClient.reportPlayback*`. A missing part switches off only its feature, warns once per page
  load (`[jfbm] web anchor "detailButtons" missing on details (web 10.11.5)`) and is reported to the server (once per ten
  minutes per browser). `./scripts/Test-WebAnchors.ps1` checks a web client before an update.
- **The script tag**: the kit's middleware adds
  `<script data-jfbm-plugin="<version>" src="../Bookmarks/ClientScript?v=<version>-<build id>" defer></script>` before
  `</body>` of `index.html` on every request (any base URL; the build id busts caches). `index.html` is then sent
  uncompressed, `Cache-Control: no-cache`, without `ETag` or `Last-Modified`, as JE and JavaScript Injector do; nothing
  else is touched, and the file on disk never changes. It coexists with JE, JavaScript Injector and File
  Transformation-based plugins such as Plugin Pages. A failure serves the page unchanged with one warning
  (`Jellyfin.Plugin.Bookmarks.Kit.WebInjection`). It also adds the kit's head fragment for the user pages (keeps "Page
  not found" from flashing on a cold load; the page keys, host mode and nav setting: server-wide, nothing about a user).
- **The pages** live in jellyfin-web's "Page not found" view, so the **experimental layout** (no drawer) has no Library
  tools section; the routes still work from links.
- **Jellyfin Enhanced 12.14, TV layout**: JE's closed **Reviews** section above Bookmarks' section held an unfocusable
  **Write a Review** button that stopped the D-pad both ways. On the TV layout only, Bookmarks takes the closed section's
  controls out of the focus order (the kit's `kit.je.tvSectionJump`) and, while the video has bookmarks, adds a
  **Bookmarks** button before **More** that jumps to its section (`webchecks/je-tv-reviews.json`, on "Reviews (0)"; a
  section full of TMDB reviews wasn't seen, the sandbox has no TMDB access). Where it still sticks, turn on JE's **Expand
  reviews by default** (`ReviewsExpandedByDefault`; JE 12.14 saves it whenever the section is toggled) or its reviews off.
