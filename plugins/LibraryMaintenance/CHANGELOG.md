# Changelog

## 1.0.4 (2026-11-10)

The first public release: 1.0.3 as it was, with the plugin's image on the Dashboard's Plugins page. No other
changes, and no data changes.

## 1.0.3 (2026-11-07)

A fix release on 1.0.2, for Jellyfin 10.11.5: still the move preview, plan-only, built so that it cannot move
anything. It upgrades 1.0.2, 1.0.1 and 1.0.0 alike, and the settings file is kept as it is. Installing, upgrading and
going back: [DEVELOPMENT.md](DEVELOPMENT.md), "Installing, upgrading and going back".

**The move preview**
- **A year once.** A movie whose name already ends with its year (Jellyfin keeps "Big Buck Test (2008)" as the name of
  a movie whose folder or file name it didn't parse) was shown as "Big Buck Test (2008) (2008)" in the dialog's texts
  (what would move, the blockers) and in the name *Put it in its own folder* proposes. The year is added only when the
  name doesn't already end with it.
- **An item the server can't read.** Jellyfin 10.11 keeps a placeholder row for watch data detached from its item
  (`00000000-0000-0000-0000-000000000001`), which the server can't open as an item; the menu's check of which items can
  move answered 500 for a request that had it, failing every other item in the same request. Such an id is now answered
  as not there ("The item isn't there, or you can't open its library."), and an item that fails to load for another
  reason as not movable, with a reason that points to the server's log (which has the details); the other items are
  answered as usual.

**The shared toolkit**
- The page and the item menu's script now carry the same shared toolkit as Access Roles 1.0.1 and User Ratings 1.0.0
  and later (Bookmarks gets it with its next release):
  - text fields and lists are held to their container's width, so none runs past a phone's screen;
  - the confirmation dialog's buttons can be reached with the D-pad on the TV layout;
  - in a dialog on a Hisense TV or on the TV layout, Backspace is Back (it closes the dialog) unless you are typing in a
    field;
  - the messages' live region is in the page from the start, so a screen reader announces the first message too.
- **Duplicates, for an administrator with a parental rating limit**: a movie in a folder rated above that limit is left
  out, as Jellyfin itself hides it from them (before, only the movie's own rating was checked).
- No visible change: a name clash between the plugin's and the toolkit's visibility helpers is resolved in the code, and
  the page's tabs are made focusable through the toolkit's helper (the same markup as before).

Duplicates is otherwise unchanged, and so are its settings. **Not in 1.0.3**: moving anything (Library Maintenance 1.1),
and the rest of 1.0.2's list.

## 1.0.2 (2026-11-02)

The move preview, plan-only, for Jellyfin 10.11.5: 1.0.1 with Move media's "⋮" entry and its preview dialog, built so
that it cannot move anything. It lets you try Move's previews on your own libraries before any version can move a file.
It upgrades 1.0.1 and 1.0.0, and the settings file is kept as it is.

**The move preview**
- **"Move media to new location"** in an administrator's item menu ("⋮" on a card or a list row, a right-click, or More
  on a details page), right after "Refresh metadata", for a movie, an episode or a music video. Never on the TV layout,
  never for a user who isn't an administrator, never in the selection's menu.
- **The dialog** says "Preview only: nothing is moved." at its top, and its foot keeps the same note in view. It shows
  what would move (the item's folder or files, its versions, parts, extras and sidecars: subtitles, `.nfo` files,
  images, trickplay), lets you pick a library and a folder (a folder browser with a filter, a new folder, "Put it in its
  own folder" for a loose movie), and previews the move: from, to, how, the files and bytes, the space on the drive, the
  folders it would create, what is kept and what would be regenerated, with every warning and everything that stands in
  the way. "It could move there" is as far as it goes: there is no Move button.
- **It cannot move anything.** The build leaves out every route and call that would move, rename, create or delete a
  file or folder (the tests read that from the DLL's code), and it tells whether a folder could be written from its
  permissions only, never with a test file.
- **What a preview touches**: it opens each file that would move exclusively for an instant, to tell whether another
  program has it open ("A file is in use", then "Check again"), so don't preview an item that is being imported or
  written; and Windows may update the folders' last-access times as they are listed. Nothing else changes on disk, and
  no library scan starts.
- **Names are shown as they are**: a right-to-left name keeps its place, and a text direction character in a name is shown
  as `[U+202E]`, never obeyed. Tab stays inside the dialog; Escape empties a text field first, then closes the dialog;
  the focus goes back to the "⋮" or More that opened it.

**The web client and the Settings tab**
- Library Maintenance now adds its script to the web client's pages as they are served, for the item menu's entry
  (nothing is written to the web client's files). A browser takes it when it next loads the web client.
- The Settings tab has a new section, **Web client and Move**: *Add Library Maintenance to the web client*, *Move media*,
  *Move in the item menu* and *Report missing web client elements*, all on. Use them to switch the entry or the script
  off, **not Disable on Jellyfin's Plugins page**, which on Jellyfin 10.11.5 deletes the plugin at the next start. The
  settings' export and import carry them; a 1.0.0 or 1.0.1 file has none of them, so an import keeps them as saved.
- Diagnostics lists the browsers' reports of a part of the web client the script can't find (none are expected).

**Also**
- An excluded folder whose second character is `?` (such as `\?\C:\x`, which Windows resolves to `C:\`) is a device
  path too: refused by a save and left out by an import, like `\\?\…`.

Duplicates is unchanged, and so are its settings. **Not in 1.0.2**: moving anything (Library Maintenance 1.1), the
Moves tab, a Move button on the details page; and more for Duplicates (marking a group as not a duplicate, comparing
copies, CSV export, merging versions, a Repair view, deleting a copy), planned for later versions.

## 1.0.1 (2026-11-01)

A fix release on 1.0.0, for Jellyfin 10.11.5, with the fixes of 1.0's final verification and review and nothing
else. The settings file is the same: 1.0.0's settings are kept as they are.

- **A device path is never opened.** An excluded folder written as a Windows device or NT path (`\\?\…`, `\\.\…` and
  the NT object prefix `\??\…`, with either slash, `\\?\GLOBALROOT\…` among them) is refused by a save and left out by
  an import, as 1.0.0 did for `\\?\` and `\\.\` only; Windows hands `\??\UNC\host\share` to the network as a share, so
  1.0.0's preview of such a folder connected to that host. One already in the saved settings (saved by 1.0.0, or
  through Jellyfin's own configuration route) is never opened by the preview either: it says "a device path, not a
  folder path: not checked", like a network folder.
- **Diagnostics' left-out counts** ("Skipped: no name", "Left out by the excluded folders", `GET /Index`'s `Skipped` and
  `Excluded`) count only the movies an administrator with a parental rating or tags limit can see; 1.0.0 gave them the
  server's.

## 1.0.0 (2026-11-01)

The first release, for Jellyfin 10.11.5: the read-only Duplicates page, which comes first. Nothing in it deletes,
merges or writes to a library; the only thing it saves is its own settings.

**In short**
- **The Dashboard page** (Dashboard, Plugins, Library Maintenance, or the drawer's Library Maintenance link), for
  administrators only, with the tabs **Duplicates**, **Settings** and **Diagnostics**.
- **Duplicates** lists the movies your libraries hold more than once, in **Title + Year** (the default) or **Title only**,
  each group with its confidence (Confirmed, Likely, Possible, Conflict), the reasons (*Title + Year*, *Same TMDb id*, ...)
  and the flags (*In 2 libraries*, *Different provider ids*, *Same id, years differ*, *Editions differ*, ...), the space
  its other copies take, and every copy with its poster, title and edition, year, libraries, resolution, size, bitrate,
  runtime, container, versions, date added, TMDb and IMDb ids and path, the suggested copy to keep first, and **Open**
  to its details page. Filter by library, confidence and title; order by confidence, title, reclaimable space, group
  size or newest; 50 groups at a time with **Load more**. Versions Jellyfin already groups count once, and its local
  alternates are never reported.
- **What you see is yours to see**: an administrator sees only the libraries their account can open, and only the movies
  their parental rating and tag limits allow; the counts follow.
- **The movie index** lives in memory, keeps itself up to date after library changes and scans, and says when it is out
  of date or was built during a scan. **Rebuild** builds it again.
- **Settings**: turn finding duplicates on or off, the default match mode, how a movie with no year matches, the
  libraries to read, the folders to leave out (the Bookmarks clip folder, when it is inside a movie library), the edition
  phrases and the leading articles' languages, the build after every library scan, and how long a request waits for a
  build. A change to the libraries or the folders shows, before it is saved, the movies the index would hold, library by
  library, and whether each folder is there (a network folder is never opened before you save). Settings saved meanwhile
  in another window are never overwritten. **Export** the settings to a file and **import** them, with what the file
  changes and what this server can't use shown first.
- **Diagnostics**: the index's numbers (movies, entries, version links and their states, movies left out), the plugin's
  and the kit's versions, the last builds and the groupings.
- **The dashboard themes** Dark, Light, Apple TV and Purple Haze, the TV layout with the remote, the keyboard, and phones
  down to 320 px.

**Not in 1.0.0**: marking a group as not a duplicate, comparing copies, CSV export (1.0.x); merging versions
and the Repair view (1.1); deleting a copy (1.2).
