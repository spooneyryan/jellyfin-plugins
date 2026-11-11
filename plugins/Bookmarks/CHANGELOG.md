# Changelog

## 1.2.2 (2026-11-10)

The first public release: 1.2.1 as it was, with the plugin's image on the Dashboard's Plugins page. No other
changes, and no data changes.

## 1.2.1 (2026-11-07)

A fix release of 1.2.0. The bookmark data stays at schema 2, so the upgrade from 1.2.0 (or 1.1.4 to 1.1.6) keeps every
bookmark and clip as it is, and going back to 1.2.0 reads 1.2.1's data as it is ([DEVELOPMENT.md](DEVELOPMENT.md),
"Installing and upgrading").

**In short**
- **A deleted user's bookmark names and notes are removed.** When a user is deleted from the server, whichever
  deleted-user choice is set, their bookmarks' names and notes are removed from the bookmark data, its backup copy
  (`bookmarks.json.bak`) and the archive. Their archived bookmarks go too; before, they stayed in the archive with their
  text. A kept clip (Keep as orphan, Anonymize) keeps its file as it is, and is listed under the file's name: that name
  (the video, the bookmark's name and the range, as clip files are named since 1.2.0) and the title inside the file
  carry the bookmark's name, and the kept record keeps the file's path; **Delete** is the choice that removes those too.
  Snapshots taken before the deletion, the copy taken just before it included, still hold them until they rotate out;
  those taken after hold nothing of them. A user whose only bookmarks are archived is now found and handled too, and an
  archive that can't be read makes the deletion wait and try again rather than leave the user's text behind.
- **My Clips, All users, lists only the clips of videos the admin can open**, as each user's own My Clips does, the
  kept clips of deleted bookmarks included. Clips of videos no longer in the library are shown only to admins with
  access to every library (an API key sees every clip). Another clip's id answers 404 on the admins' routes, the same as one that
  doesn't exist. The per-user totals still count every clip.
- **Making a clip again keeps its file's name.** The new clip is written beside the old file under a temporary name and
  then replaces it, so the name stays and nothing is left to delete. If the old file is in use (a preview or download
  elsewhere), the new clip gets a name of its own (` (2)`), as in 1.2.0, and the old file is deleted once it is free.
- **A warning when the clip folder is in a Movies or TV library.** The settings page checks the Clip folder as you type
  and says so under the field, naming the library when you can open it; Diagnostics shows the same warning for the saved
  folder. Home Videos and other libraries are not warned of.
- **Jellyfin Enhanced import:** a bookmark file with text that isn't valid UTF-8 is reported for its user as "Couldn't
  read ... (Not valid text: the file has bytes that aren't UTF-8.)", and the other users' bookmarks are imported. In
  1.2.0 the import failed with a server error.
- **Snapshots kept after a data problem** (the bookmark data found missing or unreadable) rotate as usual again once none
  of the kept snapshots is left, and the date a kept-snapshots marker records is never more than a week ahead of the
  server's clock.

**Also**
- The developer library demo's export is dated by the server's clock, as the other exports are.

## 1.2.0 (2026-10-31)

The My Clips page, on top of everything 1.1.6 has (1.1.4's Set end time, 1.1.5's player fix,
1.1.6's marks on the progress bar). The bookmark data stays at schema 2, so the upgrade from 1.1.x keeps every bookmark
and clip as it is ([DEVELOPMENT.md](DEVELOPMENT.md), "Installing and upgrading").

**In short**
- **My Clips** (the menu's Library tools, after My Bookmarks) lists every clip you made, also the clips of bookmarks you
  deleted while keeping their clip, with what each one is right now: ready, being made, failed, its file missing, or its
  clip folder offline. You can search, filter and sort them, preview them (Fast MKV clips too), download them, play the
  video from the clip's start, rename them (their file too), make them again, cancel or delete them, several at once.
  Nothing is offered that could lose a clip whose folder is offline.
- **Admins** can switch My Clips to **All users**: every user's clips with their owner and file path, never a note. A
  setting turns this off.
- **Deleting a bookmark keeps its clip** now, unless you tick "Also delete its clip".
- **Clip files are named after the video, the bookmark's name and the range**, never after the note. Clips made before
  keep their file names; **Rename file** on My Clips gives them the new one.
- **Clips whose file is gone** are marked by the daily maintenance (only in a clip folder it recognises as theirs), and a
  deleted bookmark's kept clip goes into the archive after 30 days missing. No clip file is ever deleted by it.
- **A rename survives a crash**: a clip's file and its name change together, finished at the next start if the server
  stopped in between; a restore or an import from before a rename finds the renamed file.
- **The bookmark data is never started empty** when its file can't be read: the settings page offers Restore or Start
  empty instead.
- **On a TV**, a video's Bookmarks section can be reached past Jellyfin Enhanced 12.14's closed Reviews section (a
  **Bookmarks** button in the details page's buttons row).
- **Before going back to 1.1.x**, check that the settings page's Diagnostics tab says "Clip file renames not finished:
  none." 1.1 doesn't read the note a rename leaves while it waits, so such a clip would read there as having no file
  ([DEVELOPMENT.md](DEVELOPMENT.md), "Going back").

**Details** (as built)
- A new clip's file name (and a renamed file's) is the video's name, the bookmark's name and the range; a bookmark
  without a name gives the video's name and the range alone. Before, such a bookmark's clip took the start of its note,
  which admins see in the All users view and in the clip folder. The clip's title in its metadata follows the same rule.
  The editor says so under Note.
- The admins' actions in the server log say who acted: "Admin <id>", "The API key <name>" (an API key by its name,
  never its token) or "A user that no longer exists (<id>)"; a delete that left a file another clip still names says so
  rather than that the clip was deleted. A preview's first request of a clip is logged once per clip in ten minutes.
- An admin route that answers 403 while My Clips is open in All users (the setting turned off, or the admin demoted
  meanwhile) takes the page back to My clips, with a message. The names in the admin dialogs' sentences are kept apart
  from the sentence (a name in a right-to-left script doesn't reorder it); the Owner select keeps its choice and the
  focus while the page polls; no Copy beside a path on the TV layout.
- A rename's file work is done outside the bookmark data's lock, and a rename that waits is tried again after a minute,
  then less often while nothing changes. A clip folder that can't answer a name's look-up is asked once more after
  200 ms before the request ends.
- The renames' history (`renamed-clips.json`) keeps the newest 2000 renames however old, each once, is read again when it
  changed, is set aside when it can't be read (never started over in place), and forgets a bookmark's renames when the
  bookmark goes, unless its renamed file is still there or its folder can't be reached. A restore or an import follows
  the history past names reused since, and never leaves two clips naming one file: a clip that can't be put right is
  let go of its file (failed, saying why), and the file stays.

**The player** (from 1.1.4 to 1.1.6, below): Set end time, the player found after plays from My Bookmarks, and the
bookmarks marked on the progress bar.

**The My Clips page** (the admins' view of every user's clips)
- **All users**: an admin sees **My clips** and **All users** above the toolbar. All users lists every user's clips with
  their owner (a user deleted on this server as "Deleted user (id)"), each clip's name and its file's full path (with
  **Copy**), and an **Owner** filter of the users who have clips; never a bookmark's note (the search doesn't look in
  notes there either). The scope and the owner are in the page's address, like its filters.
- What an admin can do with another user's clip: **Preview** and **Download** (with Jellyfin's download permission),
  **Play** the video, **Cancel** a clip being made, **Delete** a clip whose file is there, **Forget** one whose file is
  gone, **Dismiss** a failed one, and delete several at once (the outcome counted: deleted, in use, being made, folder
  offline, refused). No rename, remake or edit of other users' clips. The dialogs say whose clip it is and start on
  Cancel; a TV remote moves between My clips and All users and through the dialogs.
- The same safety as on your own page: the file first, a file in use (being played or downloaded) left as it was and
  said, a clip being made left alone (cancel it first), nothing touched in a clip folder that is offline or not
  recognised, a rename that didn't finish finished first (or nothing touched while it can't be), a file another bookmark
  still has kept for that bookmark, and a clip file outside the clip folders refused.
- Each admin action is in the server log at Information with who did it (the admin's id, an API key by its name, or a
  user that no longer exists by id), the clip's id and its owner's, never a name, a note or a path.
- **New setting** "Admins can see and manage every user's clips" (Clips section, on by default). Off, admins see only
  their own clips on My Clips; the per-user totals stay.

**The My Clips page** (the list)
- **My Clips** under Library tools, right after My Bookmarks (`#/jfbm-clips`): every clip of yours across all videos,
  the clips of bookmarks deleted with their clip kept included, each with what it is right now: ready (with its mode and
  size), queued or being made (with its percentage and a bar), failed or cancelled (with the reason), its file missing,
  or its clip folder offline. Its video's poster (only for a video you can open), title, range and length, when it was
  made, and the badges Bookmark deleted, Range changed, Name changed, Last remake failed and In library.
- A search, the state, mode and type filters, six sorts and "Include clips of deleted bookmarks"; a line counting your
  clips and their size, how many are being made, failed, missing or in an offline or unrecognised clip folder, and how
  many match; 50 a page with Load
  more. The page's state is in the address, so a reload, a shared link and Back show the same list.
- The clips being made are followed with one status request for all of them every 2.5 s, their rows updated in place;
  nothing is asked once none is being made, while the browser tab is hidden or after you leave the page (also in Plugin
  Pages' section, which keeps the page you left hidden: it reads its list afresh when you come back). Load more pressed
  while a clip ends still lists every clip once.
- Links to it: My Bookmarks' header (and back), each clip line on My Bookmarks and the clip-ready message ("View in My
  Clips", which finds that clip on whichever page of the list it is, scrolls to it, marks it and moves the focus to it,
  or says that it isn't in the list any more), a video's bookmarks section and the player's bookmarks list.
- Empty, no-match (Clear filters), error (Retry), loading, read-only and clips-turned-off states; the TV layout (a
  larger checkbox) and phones (40 px controls, the summary line wrapping whole parts); a list for screen readers;
  Jellyfin Enhanced's Hidden Content hides the clips of a hidden video.
- Deleting a bookmark now keeps its clip unless "Also delete its clip" is ticked (the hint: "The clip stays in My
  Clips."), in the single and the bulk delete, since My Clips lists kept clips.
- The new setting "Turn on the My Clips page" (on); the settings page's "My Bookmarks page" section is now "Pages".
  Turned off, nothing links to the page, its route says so, and the delete dialogs tick the box again.

**The My Clips page** (the row actions)
- Each clip offers what fits its state: **Preview** (in a dialog; a Fast clip's MKV too, which Chrome, Edge and Firefox
  play; when a browser can't play a clip's format, such as HEVC without hardware decoding, the dialog says so and offers
  the download, and a Fast clip's preview says why it may play without sound; a clip the server doesn't serve any more,
  deleted in another tab or its folder offline, says so instead, without a Download that would fail, and its row is read
  again), **Download**, **Play the video at the
  clip's start**, **Edit bookmark**, **Make the clip again** (the range it takes and the mode chosen in a small dialog;
  marked when the bookmark's range changed or the file is gone; the row then follows it), **Cancel** (a clip being
  made), **Delete** (a file in use says so and leaves everything as it was, so you can try again), **Forget** (a clip
  whose file is gone; no file is ever deleted by it) and **Dismiss** (a failed clip without a file). On a phone they sit
  behind one More button.
- A clip whose clip folder is offline, or is there but isn't recognised as the folder its clips were made in ("Clip
  folder not recognised": a drive or share that isn't mounted, or a folder in its place), offers only Play and Edit
  bookmark, and can't be selected: nothing that could lose a clip whose file is probably fine where its folder is.
  Deleting its bookmark with "Also delete its clip" ticked is refused the same way, with nothing changed (the bookmarks'
  bulk delete keeps such a bookmark, named as one whose clip couldn't be removed); deleting it and keeping the clip still
  works. A Delete or Forget refused because the folder changed reads the row again at once.
- Several at once: a checkbox per clip, Select all, and Delete… for the ones ticked, 200 at a time with the progress;
  the confirmation says what goes and that a clip being made is kept ("1 clip being made is kept; cancel it first."); what
  the server keeps (a file in use, a clip being made, its folder unavailable) stays selected with why. On a TV the
  checkboxes show behind a Select toggle.
- The keyboard and a TV remote: each dialog takes the focus when it opens (Cancel in the ones that delete or forget,
  Close in the preview) and gives it back when it closes, so the D-pad and OK reach the dialog, not the page behind it,
  and a clip deleted, forgotten or dismissed passes the focus to the next row.
- The Preview of a video's bookmark rows and of My Bookmarks' clip lines plays MKV clips too.

**The My Clips page** (renaming)
- **Rename clip** (a clip that isn't being made and whose folder is there): the clip's name (its bookmark's, also a
  deleted bookmark's kept clip's) and, for a file that is there, **Also rename the file**, ticked: the file then gets the
  name Bookmarks gives the clip now (the video's name, the new name, the range the file holds; never the note), in the same folder, with
  " (2)" when that name is taken (by any file, a clip being written, another clip's record, a file waiting to be
  deleted), never over a file. Both change or neither does: a file in use (a preview in another tab, a download,
  Jellyfin playing it) says so in the dialog after three tries and renames nothing, the name included, so you can try
  again; a clip being made, or whose folder went offline is said there too, and a clip renamed in another tab meanwhile
  puts its name as it is now in the field and says so ("Changed elsewhere: it is named "…" now. Rename replaces that."),
  so a second Rename replaces it only on purpose. For a clip inside a Jellyfin library the box starts unticked, and the
  dialog says what renaming its file does there (Jellyfin sees a new video: what it kept for the old one, played,
  favourite, collections, playlists and anyone's bookmarks of that video, starts over, and files it wrote next to it, an
  `.nfo` or trickplay images, stay under the old name). The dialog starts with the focus in the name, its text selected.
- **Rename file** for a clip whose file still has its old name (the **Name changed** badge: the bookmark was renamed on
  My Bookmarks, which keeps a clip file's name): a confirmation naming the file and the clip's name, its focus on Cancel,
  then the file gets the clip's name; the badge goes. Marked in the accent colour, except for a clip in a library. The
  confirmation sends the clip as it showed it: renamed in another tab meanwhile, it says so with the clip's name now, and
  the next try gives the file that name. A file numbered " (2)" (its plain name was taken when it was made) gets its plain
  name back whenever its file is renamed while that name is free: Rename clip with **Also rename the file** ticked does
  that even without a new name, while Rename file is offered only for a clip whose name changed.
- A rename is safe across a crash: the server notes it in a small file in its data folder (`clip-rename-<id>.json`)
  before the clip file is moved, flushes the move to the disk before the bookmark data names the new file (also with the
  data and the clips on two drives), and drops the note once the data names it, so a stop in between, a power cut, or a
  write of the bookmark data that fails while the file can't be moved back either, is finished afterwards from what is on
  disk (at the next start, before anything else looks at the clips; after a minute while one waits, then less often; daily; before the next
  rename, delete or remake of that clip), the file told apart from another by its file id, its size and its last write
  time. Until then its row says **Rename not finished** and offers only Play and Edit bookmark, it can't be selected, and
  nothing deletes, forgets or moves that clip; its file is served from where it is. A clip folder that is there but isn't
  recognised as the clips' own never has a rename given up. A note that can't be read holds its clip the same way, until
  an admin removes it; the daily maintenance warns while any waits, and Diagnostics counts them (`ClipRenames`). A new clip
  being named and a rename never pick the same name at once.
- **A restore or an import from before a rename** names the renamed file: Restore snapshot, the restore of a snapshot at a
  start that found the bookmark data missing or unreadable, and an import (Replace or Merge) of an export from before it
  each make a clip that names a path its file left in a rename name the file again, also after several renames, from the
  data before the restore or the import, else from the renames noted in `renamed-clips.json` next to the data (the
  newest 2000); never a file another clip names or one waiting to be deleted. A clip folder away
  at the restore is put right at the next start or daily maintenance. Before, such a clip read "File missing" with its
  renamed file left behind, also after deleting it with its clip.
- **Before going back to 1.1** from a 1.2 build, check that Diagnostics shows no unfinished clip renames
  (`ClipRenames`, `Unfinished` 0): 1.1 doesn't read the rename notes, so a clip whose rename waits would read as having
  no file there.

**Clips**
- The fixes of 1.1.3 (below) hold for a rename too: a clip's new name ends before a character that doesn't fit whole,
  and is never a name another clip has, its file there or not, one waiting to be deleted, or another rename's. 1.1.3's
  safeguard holds on My Clips too: its delete of several clips never deletes a file another bookmark still has.
- Making a clip again whose file is missing gives the new file a " (2)" name (or the next number): the bookmark's own
  clip still holds the old name until the new file is in place, and since 1.1.3 a new clip never takes a name a clip
  holds. The number goes the next time the clip's file is renamed while its plain name is free.
- A clip folder that answers with an error when a clip file's name is chosen (a drive that isn't ready, a network share
  that doesn't answer) now ends the request at once with "The clip folder can't be reached right now …" (Make clip: 503;
  a clip waiting in the queue fails with it, and can be made again), where before up to a thousand numbered names were
  tried one after the other, each as slow as the folder. A rename of a clip's file there is refused the same way, with
  nothing changed.
- A clip file that two bookmarks have (after an import, a restore, or a version before 1.1.3) is no longer renamed away from one of them:
  renaming it with its file, or Rename file, is refused (409) until one of the two lets go of the file (deleted or
  forgotten); the name alone still renames.

**My Bookmarks**
- In Plugin Pages' section (page host mode Auto or Plugin Pages only), the page you left no longer reads its lists
  again in the background when a bookmark changes elsewhere (in the player, say); it reads them when you come back.
- The delete confirmations (one bookmark, several) start with the focus on Cancel and give it back when they close; a
  row or a video's group that is deleted passes the focus to the next one, so the keyboard and a TV remote carry on from
  there instead of from the top of the page. Every dialog of Bookmarks takes the focus when it opens.

**REST API** (the page's endpoints)
- `GET /Bookmarks/Clips`: your clips across all videos, the clips of bookmarks deleted with their clip kept included,
  each with what it is right now (ready, being made, failed, its file missing, or its clip folder offline: an unplugged
  drive is never taken for files that are gone), searched, filtered, sorted and paged; what Bookmarks does to a clip file
  itself (a clip made again, a file deleted) shows at once, a change made by hand within half a minute.
  `GET /Bookmarks/Clips/Summary`, `GET /Bookmarks/Clips/Status?ids=` and `POST /Bookmarks/Clips/BulkDelete` (each
  clip's file first; a clip being made, a file in use or one in an offline folder is left as it was). Only your own
  clips, ever.
- `DELETE /Bookmarks/{id}/Clip?onlyIfMissing=true`: forget a clip whose file is gone (or a failed one without a file);
  a file that is there after all is never deleted (409).
- `POST /Bookmarks/Clips/{id}/Name` `{Name, RenameFile, IfUpdatedAt}` and `POST /Bookmarks/Clips/{id}/RenameFile`: a
  clip's rename, its name, its file or both in one change, answering the clip as the list maps it (the file's name, never
  a path); 400 a name over 100 characters, 404 no clip of yours (or no file, or its file gone), 409 being made, changed
  since `IfUpdatedAt` or the file in use, 503 read-only data, the clip folder offline or not recognised, or a rename of
  that file not finished yet; nothing changed in any of them.
- A clip whose file is gone from a clip folder that is there but isn't recognised as its clips' own (no marker file of its
  own) is listed as its folder offline (`FolderUnavailable`, with `RootAvailable` true), never as missing, and the clip
  routes leave it as it is: `DELETE /Bookmarks/{id}/Clip` (with or without `onlyIfMissing`) answers 503 saying the
  folder isn't recognised, and the clips' bulk delete reports it in `FolderUnavailable`; so do the bookmarks' deletes with
  their clips (`DELETE /Bookmarks/{id}?deleteClip=true` 503, nothing changed; `POST /Bookmarks/BulkDelete` with
  `DeleteClips` in `Failed`), which had removed the record of a file that came back with the real folder.
- `GET /Bookmarks/Capabilities`: `CanDownloadClips`, the download permission the clip file route needs, also while
  making clips is turned off.
- The admins' clip routes (Jellyfin's admin policy on every request; all but `Summary` also need the setting "Admins can
  see and manage every user's clips", else 403): `GET /Bookmarks/Admin/Clips` (every user's clips, or `userId`'s, with
  the filters of `GET /Bookmarks/Clips`; each with `UserId`, `UserName`, `UserDeleted`, `FilePath` and `RootId`, and no
  `Note`), `GET /Bookmarks/Admin/Clips/Summary` (per user: count, bytes, oldest, newest), `GET …/Status?ids=`,
  `GET …/{id}/File?download=` (with the download permission), `POST …/{id}/Cancel`, `DELETE …/{id}` (204;
  `onlyIfMissing=true` for Forget; 409 for a file in use, a clip being made or a file outside the clip folders) and
  `POST …/BulkDelete`. `GET /Bookmarks/Capabilities` says `CanManageAllClips`.
- `GET /Bookmarks/Capabilities`: `Pages.Clips` is true while the My Clips page is on; `GET /Bookmarks/Pages/clips` is
  its page (a "turned off" note while it is off).
- `POST /Bookmarks/Admin/ClipFolders/{rootId}/Confirm` (admins only; a dry run unless `dryRun=false`): "This is the
  clip folder; its clips are gone" for a clip folder the daily maintenance left out.

**Clips whose file is gone**
- The daily "Bookmarks: Maintenance" marks a clip whose file is gone from its clip folder (deleted by hand, or through
  Jellyfin) as missing, and clears the mark when the file is back. It marks clips only in a clip folder it recognises as
  theirs, by a small marker file Bookmarks keeps in each clip folder (`.jfbm-clip-root`, with a token of its own that
  `bookmarks.json` records, so an older drive or a backup copy of the folder back under the same path isn't taken for it;
  the folders of 1.1 get it the first time their clips are found there with none of them gone). A clip folder that isn't
  there (an unplugged drive), one that is there but has neither the marker nor any of its clips (a disk or share that
  isn't mounted, another drive under the same letter), one there without its marker with some of its clips gone (a folder
  in its place holding a clip 1.1 wrote there while the real one was away), and one whose every clip is gone at once are
  left out: nothing in them is marked, archived or forgotten, and the Diagnostics tab says "Clip folder unavailable", why,
  and what to do.
- When a folder's clips are really gone (a folder emptied or deleted by hand), an admin presses **This is the clip folder;
  its clips are gone** on the Diagnostics tab (or under the Clip folder field): the dialog says how many clips will be
  marked missing, and the folder is made if needed, given its marker, and those clips marked (after a snapshot), so they
  are forgotten or archived after the usual days, and their owners can forget them on the My Clips page (before that,
  the page shows a folder that isn't recognised as such, with nothing to forget). The default clip folder in the server's
  data folder, deleted, is made again by itself and its clips are marked missing, so no one's new clips are refused. A
  clip being made is left alone.
- A clip is never made into a clip folder that isn't there while clips were made in it (that would make the folder again,
  empty), nor into one that isn't recognised: it fails with "The clip folder isn't available right now" (or that the
  folder isn't recognised), saying what the server admin can do, and can be made again once the drive is back or the
  folder is confirmed. The line under the Clip folder field warns of such a folder too.
- After 30 days missing (the new setting "Keep clips whose file is gone (days)"; 0 keeps them for good; at most 36500,
  and a larger value is stored as 36500) a deleted bookmark's kept clip moves into the archive, its name and note kept (in
  exports, with the reason it was archived; only an admin's Purge deletes it), and a bookmark's own clip is forgotten while
  the bookmark stays. No clip file is ever deleted by it.

**For other plugins**
- `Jellyfin.Plugin.Bookmarks.Integration.ClipRootsV1.Get()` gives the clip folders, so Library Maintenance's Duplicates
  can leave clips out of its search. An empty answer means Bookmarks isn't answering yet: ask again later, never keep it.

**The bookmark data's safety** (1.1.2's restore of a lost bookmark file, hardened)
- An unreadable `bookmarks.json` (not JSON, or not data this version can read) with no usable backup is no longer started
  empty: it is moved aside as `bookmarks.json.corrupt-<time>` and the newest snapshot that holds bookmarks is restored,
  as for a missing file. With no such snapshot, the bookmark data is read-only (`StoreReadOnlyReason` `DataUnreadable`),
  the file left exactly as it was, until an admin decides: Bookmarks never starts with no bookmarks on its own when a
  file that may hold them couldn't be read.
- The same for an unreadable `bookmarks.json.bak` beside a missing file: it is moved aside (under a name of its own,
  never overwritten) and the snapshots are tried, instead of starting empty with the backup left for the second save to
  overwrite.
- **A way out on the settings page** while the data is missing or can't be read (`DataMissing`, `DataUnreadable`): the
  Data tab's Snapshots section says so and offers **Restore** of a listed snapshot (one copied into the snapshots folder
  meanwhile too) and **Start empty**, each after a confirmation whose focus starts on Cancel. Either keeps every
  snapshot and what couldn't be read, ends the clip jobs a restored snapshot held, and makes the data writable at once,
  with no restart: the clip worker, idle until then, starts. Until now this needed the server stopped and files copied
  by hand. `POST /Bookmarks/Admin/Snapshots/Restore` does it in that state, and `POST /Bookmarks/Admin/Data/StartEmpty`
  (admins only) is new; `GET /Bookmarks/Admin/Snapshots` says `Recoverable` and `StoreReadOnlyReason`.
- With the server's clock moved back, a restore at start keeps every snapshot, also those dated after the wrong time
  (which the banner names, as possibly newer data to restore), so none of them is rotated out once the clock is right
  again; the daily rotation never deletes a snapshot dated more than a day ahead.
- A start that moved an empty `bookmarks.json` or `.bak` aside and then had to wait for a snapshot in use still says so
  on the next try, rather than "missing" (or nothing at all, when no snapshot held bookmarks).
- The snapshots kept since the data was missing or unreadable are recorded by name (`bookmarks.json.keep-snapshots`), so
  exactly those are kept and the ones taken since rotate as usual: with the clock behind, restoring a snapshot taken
  since (the `import` snapshot of "start empty, then import", say) no longer lets the kept ones rotate out, and a
  snapshot dated far ahead no longer keeps every later one. A marker written by 1.1 is read as it was meant, then
  recorded by name.
- Start empty or Restore on the Data tab never moves aside a readable `bookmarks.json` (or `.bak`) put back while the
  server runs: refused, "A readable data file is in place now (bookmarks.json); restart the server to use it."
- When moving an unreadable `bookmarks.json` or `.bak` aside fails (open in another program), or the backup's copy from
  before an upgrade can't be written, what was moved goes back, and the answer says what failed rather than "couldn't be
  read"; a restart then finds the same files.
- An unreadable `bookmarks.json` replaced by a `.bak` older than a snapshot holding other bookmarks: the banner names the
  snapshot (restore it if it has changes the backup lacks), and every snapshot is kept until one is restored.
- `kit-users.json`, the list of users deleted on this server, that can't be read: a banner, its status on the Diagnostics
  tab, and **Start the list empty** on the Data tab (`POST /Bookmarks/Admin/Users/Marks/StartEmpty`, admins only);
  meanwhile *Remap by name* and an import that matches users by name are refused, since a deleted user's bookmarks could
  go to a new user of the same name. The log has one Warning, saying what to do.
- A purge's copy of the archive (the only copy of the bookmarks purged) is never deleted by its own rotation, also with
  the clock behind the earlier copies.
- The read-only notice on My Bookmarks and My Clips says why in a plain sentence (not "DataUnreadable") and what to do:
  to an admin, the settings page; to everyone else, ask the server's administrator; so does a change refused with 503.
  The Data tab's Restore is off for a snapshot that can't be read, and a kept snapshot says why it is kept.

## 1.1.6 (2026-10-30)

1.1.5 with the video's bookmarks marked on the player's timeline. Nothing else changed: the server code, the settings
and the bookmark data are 1.1.5's (schema 2), so the upgrade keeps every bookmark as it is.

**The player**
- **Timeline marks**: the playing video's bookmarks on the progress bar, beside its chapter marks. A bookmark is an
  amber tick at its start; a range is also a band from its start to its end. Pointing at a tick shows the bookmark's
  name and times; clicking it plays from there. A bookmark made, renamed, ended or deleted while watching shows at once;
  one changed on another device or page shows the next time the video starts. Jellyfin Enhanced's own markers, which
  its switch-off checklist turns off, were the only ones until now.

## 1.1.5 (2026-10-30)

1.1.4 with one fix to the player. Nothing else changed: the server code, the settings and the bookmark data are 1.1.4's
(schema 2), so the upgrade keeps every bookmark as it is.

**The player**
- The bookmark buttons and the shortcut said "Start playing a video…" while a video played, after videos were played one
  after another from My Bookmarks (**Play from here**). Each such play adds a player view, and the web client keeps up
  to three of them, the earlier ones hidden, in page slots whose order the shown one doesn't follow. Bookmarks asked only
  the first view in the page whether it was shown, so whenever a hidden one came first, the buttons, the shortcut and
  Set end time took the player for closed. The player now counts as open while any of its views is shown. The first video
  after the page is loaded was never affected; 1.1.0 to 1.1.4 have the same fault.

## 1.1.4 (2026-10-29)

1.1.3 with one addition to the player. Nothing else changed: the server code, the settings and the bookmark data are
1.1.3's (schema 2), so the upgrade keeps every bookmark as it is.

**The player**
- **Set end time**, a fourth button between the scissors and Bookmarks: it ends one of the video's bookmarks at the
  current moment. It lists the video's bookmarks, the nearest one before this moment first and focused, each with the
  range it would get and its current end; one that starts here or later can't be chosen. The choice saves at once (a
  bookmark changed or deleted elsewhere meanwhile reads the list again and says so), and the toast offers **Make a clip**
  when the range is within the clip limit. It turns point bookmarks, such as Jellyfin Enhanced's imported ones, into
  ranges without opening the editor.

## 1.1.3 (2026-10-27)

A clip-safety release: 1.1.2 with two ways of losing a clip fixed, and a safeguard against both. Nothing else changed,
and the bookmark data is the same (schema 2). Install it instead of 1.1.2.

**Clips**
- A clip whose file name had an emoji (or another character stored in two halves, such as some rare CJK characters)
  right where the name is cut, at its 80th character, was lost. The name comes from the bookmark's name, else its note,
  and the video's name is cut the same way. 1.1.2 cut that character in half: ffmpeg wrote the clip under a slightly
  different name than the one recorded, the bookmark's clip pointed at an empty file under a name nothing else knew, and
  at the next start the clip itself was deleted as an unfinished file, so its download failed and only the empty file
  was left. The cut now ends before any character that doesn't fit whole (an emoji with its skin tone or its joined
  parts, a letter with its accents), so the clip is written and kept under the name it is recorded with. Half a
  character typed into a name or note is left out of the file name.
- A new clip could take the file name of another bookmark's clip whose file was gone ("File missing"): with two bookmarks
  of the same video with the same name and range, and the first one's clip file deleted by hand, the second one's new
  clip got the first one's file name. Both bookmarks then pointed at one file, so making the first one's clip again,
  deleting its clip, or deleting it with its clip deleted the second one's clip. A new clip's file name is now never one
  that any bookmark's clip has (any user's, its file there or not), nor one of a file waiting to be deleted.
- **Safeguard**: a clip file is never deleted while another bookmark still has it, whatever deletes it (a bookmark
  deleted with its clip, a clip deleted, several bookmarks deleted at once, a clip made again replacing the previous
  file, a deleted user's clips): the bookmark being changed lets go of the file as if it were deleted, the other bookmark
  keeps it, and the server log has a Warning naming both bookmarks (by id).

## 1.1.2 (2026-10-19)

A data-safety release: 1.1.1 with its bookmark file restored from its snapshots when the file is gone. Nothing else
changed, and the bookmark data is the same (schema 2). 1.1.3 replaces it (two ways of losing a clip, above); install
1.1.3 instead.

**Data safety**
- `bookmarks.json` missing (or empty) with no `.bak` while its snapshots hold bookmarks (a file deleted by hand, or a
  backup of `snapshots` alone put back) is restored from the newest such snapshot at start, with a banner on the settings
  page naming it, and every earlier snapshot is kept until one is restored. 1.1.1 started empty there, without a banner,
  and the daily maintenance then deleted every snapshot holding the bookmarks within "Daily snapshots to keep" days. When
  none of the snapshots can be restored (a newer Bookmarks', unreadable, or not readable by the server's account), the
  data is read-only (`DataMissing`), no snapshot is touched, and DEVELOPMENT.md says how to go on (stop the server, copy the
  snapshot you choose over `bookmarks.json`, start it).
  - An empty `.bak` beside it counts as none (it is moved aside like an empty `bookmarks.json`), so the snapshots are
    tried.
  - A snapshot the server's account may not read is passed over and named in the banner; one open in another program
    just then is tried again at the next request, and the 503 names it.
  - An empty snapshot file counts as holding nothing; a snapshot dated in the future (a clock that was wrong) is tried
    last.
  - The earlier pre-operation snapshots are kept too, until a snapshot is restored or `bookmarks.json.keep-snapshots` is
    deleted.
  - Clips the snapshot holds queued or being made are marked failed, to be made again, as after Restore snapshot, so
    none is made a second time at start.
  - The server log has a Warning for it, with the snapshot's time in UTC.

## 1.1.1 (2026-10-16)

Jellyfin Enhanced 12.12 support: 1.1.0 with its switch-off checklist made for Jellyfin Enhanced 12.12, which rebuilt its
Keyboard tab and reads its keys more loosely. Nothing else Bookmarks reads of Jellyfin Enhanced changed, and the
bookmark data is the same (schema 2).

**Jellyfin Enhanced**
- The switch-off checklist's last step follows the Keyboard tab of the Jellyfin Enhanced the server runs: on 12.12, a grid
  of keys that takes no typed text, give Bookmark Current Time another key: click its key (`B`) at the left end of its row
  (the row's label is at the right end, so a neighbouring row's key is easily clicked instead), press for example
  `Shift+X`, then Save Settings; never Delete or the ⊘ button beside the key, which save a blank key that comes back as
  `B` (Backspace puts `B` back). On 12.11, as before, type `Disabled` into the Key field and Add.
- Jellyfin Enhanced 12.12 reads a lower-case `b` as `B`, modifiers in any order, and a symbol key with or without Shift:
  the checklist counts a `b` as `B`, and the shortcut check warns about a key either 12.11 or 12.12 would take (so `+`
  reserves `Shift++` too).
- The Plugin Pages link's note says that 12.12 already takes the link out of Plugin Pages' list when its settings are
  saved; a restart is still what makes Plugin Pages 2.4 drop it.

## 1.1.0

Named bookmarks and the My Bookmarks page, with the first clip-safety parts, built on the shared kit. Details:
[DEVELOPMENT.md](DEVELOPMENT.md).

**Names and the player**
- Every bookmark can have a **name**: one line of at most 100 characters, separate from the note (500). Clip files and
  clip titles use the name, else the note.
- `Shift+B` (or the bookmark button) bookmarks the moment at once, without pausing. A small **name dialog** follows,
  straight away with "Always ask for a name", else from the toast's **Name it**; it suggests the title of the chapter the
  bookmark is in (never "Chapter 3" or a time). Optional: **Pause while naming**.
- The shortcut is a setting (`Shift+B`, because Jellyfin Enhanced takes `B`); each user can set their own on their device.
  Keys typed in a Bookmarks dialog never reach the player's or Jellyfin Enhanced's shortcuts. Plain `B` no longer
  bookmarks (it never could next to Jellyfin Enhanced).
- The player's buttons can be turned off; the shortcut still works.

**The My Bookmarks page** (Library tools in the menu, `#/jfbm-bookmarks`)
- Every bookmark of yours: search, Movies/Episodes/Other, sorts, grouped by video or flat, the filters Has range, Has
  clip, Needs a name and Not in library, paging, and the state in the address (reload, Back and links show the same list).
- Rename in place, play from here, edit, make a clip, delete; select several and delete them (with or without their
  clips); **Player options** for your naming preferences and this device's shortcut. Works on phones, TVs and from the
  keyboard.

**On a video's page**
- The editor has a Name field first; rows show the name (else the note's first line, else the time), **Name it**, and
  the badges Not in library, Unavailable and Relinked, check position.
- Deleting a bookmark with a clip asks whether to delete the clip too (ticked, as in 1.0); a clip kept is tracked as the
  clip of a deleted bookmark (the My Clips page of 1.2.0 will list them).

**Jellyfin Enhanced**
- A one-time **import** of Jellyfin Enhanced's bookmarks (a dry run first; labels become names, positions and times kept,
  matched by item id or TMDb/TVDb id, duplicates left out; running it again adds only what is new), then a
  **switch-off checklist** (the shortcut last: set to `Disabled` or another key, never blank), and **Use B** once `B` is
  durably free. Jellyfin Enhanced's files and settings are only ever read.

**Videos that move or go**
- A bookmark follows its video when the file is moved or renamed (also through Library Maintenance's move journal), waits
  as "Not in library" while it is gone, and comes back with it; it is never moved onto another cut of the same film.
- What happens to a deleted user's bookmarks is a setting (keep their clip files, or delete everything); users the server
  doesn't know are listed and never deleted on their own.

**Clips**
- A free-space guard: a clip is refused when it would leave less than the space to keep free (2 GB) on the clip drive,
  and stopped when the drive is almost full.
- Unfinished `.partial` files a crash left are deleted at the next start; a clip file in use (being played or downloaded)
  is never deleted from under it (409, nothing changed); a clip made again gets a file name of its own; only `.mp4` and
  `.mkv` files inside the recorded clip folders are ever deleted, moved or served.
- The Clip folder's help says to keep the default folder, outside every library, or to use a Home Videos library of its
  own on a drive with room; never a Movies or TV library.

**Data**
- The bookmark file is schema 2. A 1.0 file is upgraded on first use, and the 1.0 file is kept next to it as
  `bookmarks.json.v1-<time>` (the way back to 1.0); a file a newer version wrote, or one whose upgrade failed, is
  read-only and never changed.
- The settings page has tabs: **Settings** (every 1.1 setting, the shortcut checked as it is typed), **Data** (the Jellyfin
  Enhanced import, export and import of the bookmark data, snapshots with Restore, the daily maintenance run now, the
  users the server doesn't know) and **Diagnostics** (with each user's bookmark counts, never their text).
- A daily "Bookmarks: Maintenance" task (03:10): a snapshot (7 kept), the user check, the check that every bookmark's
  video is still there.

**Web client**
- No File Transformation plugin needed: the script tag is added to `index.html` as it is served. Dialogs follow the web
  client's theme, open as bottom sheets on phones, and work with a TV remote.
- On a Jellyfin line it wasn't built for (Jellyfin 12), the plugin stays inert instead of failing.

**Privacy**
- Admins see how many bookmarks each user has, never what they are. Bookmark names and notes are never logged at
  Information or above.

## 1.0.0

- Initial release: per-user bookmarks with a position, an optional end time and a note; player buttons, `B` or
  `Shift+B`, and a bookmarks section on video pages (through the File Transformation plugin); Accurate and Fast clips with
  ffmpeg, made in the background and downloaded from the video's page.
