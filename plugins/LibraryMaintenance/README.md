# Library Maintenance

Library Maintenance helps a Jellyfin administrator tidy up a media collection. Version 1.0.4 does two things: an
admin-only Dashboard page that lists the movies your libraries hold more than once, and a **preview** of moving a
movie, an episode or a music video to another folder. It is read-only: it deletes, merges, moves and writes nothing in
your libraries. It is for home-server admins who want to find duplicate copies and see what a move would involve
before any tool touches their files.

## Features

**Duplicates** (administrators only)
- Lists the movies held more than once, matched on **Title + Year** (the default) or **Title only**.
- Versions Jellyfin already groups together count as one movie, and its local alternates are never reported.
- Each group shows its confidence (**Confirmed**, **Likely**, **Possible** or **Conflict**), the reasons and flags
  (such as *Same TMDb id*, *Different provider ids* or *In 2 libraries*), and the space the other copies take.
- Every copy is listed with its poster, edition, year, libraries, resolution, size, bitrate, runtime and path, the
  suggested copy to keep first, and **Open** to go to its page. The suggestion is advice only.
- Filter by library, confidence and title; five sort orders; 50 groups at a time with **Load more**.

**Move preview** (administrators only, preview only)
- **Move media to new location** in the item menu ("⋮") of a movie, an episode or a music video.
- A dialog shows what would move (the item's folder or files, its versions, parts, extras, subtitles, `.nfo` files,
  images and trickplay), lets you pick a library and a folder, and previews the move: from, to, the files and bytes,
  the free space, the folders it would create, what is kept (watch states, favourites, resume points, collections,
  playlists, metadata edits) and everything that would stand in the way.
- **There is no Move button.** This build cannot move, rename, create or delete any file or folder; this is by design.
  Moving itself is planned for a later version.

**Members** (users who are not administrators) see nothing new: the page, the menu entry and the preview are for
administrators only.

## Requirements

- **Jellyfin 10.11.5 or later within 10.11.** Jellyfin 12 and later are not supported yet.
- **No other plugin is needed.** Library Maintenance adds its own script to the web client; it does not use File
  Transformation or JavaScript Injector.
- **Works alongside Bookmarks**: if Bookmarks saves its clips inside a movie library, see "The Bookmarks clip folder"
  below.
- **Web client layouts**: the Dashboard page works on the desktop, mobile and TV layouts. The **Move media to new
  location** entry appears on the desktop and mobile layouts only, never on the TV layout.
- **Operating system**: the move preview needs a Windows server; on other systems the menu entry does not appear.
- **Tested on**: Windows only, with Jellyfin 10.11.5. Linux and Docker servers have not been tested yet.

## Installation

**From the plugin repository**
1. In Jellyfin, go to **Dashboard -> Plugins -> Repositories** and add `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Open **Catalog**, find **Library Maintenance** and install it.
3. Restart the server.

**Manually**
1. Download the zip from the GitHub Releases page: <https://github.com/spooneyryan/jellyfin-plugins/releases>.
2. Extract it into the `plugins/Library Maintenance_1.0.4.0` folder inside Jellyfin's data folder.
3. Restart the server.

**After installing**, reload the web client with **Ctrl+F5** so your browser picks up the plugin's script (needed for
the item-menu entry).

## Getting started

1. Open the plugin's page: **Dashboard -> Plugins -> Library Maintenance**, or the **Library Maintenance** link in the
   dashboard's menu. It has three tabs: **Duplicates**, **Settings** and **Diagnostics**.
2. On **Settings**, check:
   - **Libraries**: the movie and mixed libraries to read. With none chosen, every one is read, including libraries
     you add later.
   - **Excluded folders**: one full folder path on the server per line; the movies under them are left out. Before
     saving, a dialog shows how many movies the change leaves out and whether the server finds each folder.
   - **Title rules**: *Cut edition suffixes* with its list of **Edition phrases** (such as "Final Cut"), and *Leading
     articles dropped* per language (English by default).
   - **Build the index after every library scan** (on by default), so the Duplicates tab is ready when you open it.
3. Open **Duplicates**. The first time, it builds its list of movies ("Indexing movies… 40%"), then shows the groups.

## Using it

**Find duplicates.** On **Duplicates**, pick the match mode, the libraries, the confidence and the order, or type part
of a title. If Title + Year finds nothing, the page offers **Try Title only** when that would find some. **Rebuild**
reads your libraries again. The list keeps itself up to date after library scans and changes.

**Preview a move.** As an administrator, open "⋮" on a movie's, episode's or music video's card or row (or **More** on
its details page) and pick **Move media to new location**. Choose a library and a folder; the preview appears below.
A library folder on another drive or on the network is listed but cannot be chosen yet. **Close** (or Escape) closes
the dialog; the preview is forgotten after 15 minutes.

**Export and import the settings.** At the foot of **Settings**, **Export the settings** downloads a `.json` file;
**Import from a file…** shows what would change before anything is saved. The file contains your excluded folders'
paths, so keep it somewhere only administrators can read.

**Diagnostics** shows the index's numbers (movies, entries, broken version links, when it was built) and the plugin's
version, for troubleshooting.

## Important notes and limitations

- **Don't use Disable on the Plugins page.** On Jellyfin 10.11.5 a disabled plugin is deleted at the next server start.
  To switch things off, use **Settings -> Web client and Move** instead: *Move in the item menu* off hides the entry;
  *Move media* off hides it and turns the preview off; *Add Library Maintenance to the web client* off removes the
  script from the web client altogether (Duplicates keeps working). Save, then reload the web client with Ctrl+F5.
- **A preview briefly locks files.** To tell whether another program has a file open, the preview opens each file that
  would move exclusively for an instant. During that instant no other program can open it, so **don't preview an item
  that is being imported or written** (a download finishing, Sonarr or Radarr importing it, a copy under way). A player
  that already has the file open is not disturbed. Otherwise a preview changes nothing on disk and starts no library
  scan (Windows may update the folders' last-access times as they are listed).
- **What you see depends on your account.** An administrator sees only the libraries and movies their own account can
  see: a parental rating or tags limit applies to administrators too. Such an administrator also gets fewer numbers in
  the settings dialog and on Diagnostics.
- **Missing drives.** The plugin never deletes or changes anything, so an unmounted library drive cannot cost you data
  through it. While a drive is missing, the Duplicates list shows what Jellyfin's own library still holds: the index
  reads Jellyfin's library and does not check that the files are there.
- **Settings changed elsewhere are not overwritten.** If another administrator saved the settings after you opened
  them, your save is refused and the page offers **Reload the settings**.

### The Bookmarks clip folder

If you use Bookmarks and it saves its clips into a folder inside a movie library, Jellyfin scans each clip as a movie
named after its film, so every clip would show up as a duplicate. Add that clip folder to **Excluded folders** on the
Settings tab (its full path as the server sees it). A clip folder outside every movie library needs nothing.

## Your data and privacy

- **Settings**: the plugin keeps only its settings file, `<data>/plugins/configurations/Jellyfin.Plugin.LibraryMaintenance.xml`,
  where `<data>` is Jellyfin's data folder (for example `%ProgramData%\Jellyfin\Server` on Windows, `/var/lib/jellyfin`
  on Linux, `/config` in Docker).
- **No data folder**: the duplicates index and any move preview live in memory only and are never written to disk.
- **Your libraries**: nothing is ever written to them.
- **Backups**: back up the settings file above, or use **Export the settings**.
- **Who sees what**: only administrators can use the plugin, each within the libraries their account can see.
- **No external services**: the plugin makes no calls outside your Jellyfin server.

## Uninstalling

1. Go to **Dashboard -> Plugins**, select **Library Maintenance** and choose **Uninstall** (not Disable), then restart
   the server. Or stop the server and delete the `plugins/Library Maintenance_1.0.4.0` folder in Jellyfin's data
   folder.
2. To remove the settings too, delete `<data>/plugins/configurations/Jellyfin.Plugin.LibraryMaintenance.xml`.
3. Start the server and reload the web client with Ctrl+F5. The item menus no longer have the Move entry.

Nothing else is left behind: watch states, users, libraries and other plugins are untouched, and since the plugin
never wrote to a library there is nothing else to undo. Uninstall removes the plugin's folder; on Windows, where the
running server holds the plugin's file open, Jellyfin deletes the folder at the next start instead. It leaves the
settings file, which step 2 removes.

## More detail

- The full technical documentation (how titles are compared, how groups and confidence are worked out, the API):
  [DEVELOPMENT.md](DEVELOPMENT.md).
- What changed in each version: [CHANGELOG.md](CHANGELOG.md), and the `changelog` in `build.yaml`.
