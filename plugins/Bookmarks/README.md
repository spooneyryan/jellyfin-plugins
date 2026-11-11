# Bookmarks for Jellyfin

Bookmarks lets each user on your Jellyfin server mark moments in movies and episodes, give them a name, and find them
again later. A bookmark can also have an end time, and the plugin can cut that range into a clip file with ffmpeg. It is
meant for home-server admins who want one private bookmark system for everyone in the house, and it can bring over the
bookmarks people already made in Jellyfin Enhanced.

## Features

- **While watching:** press **Shift+B**, or the bookmark button, to bookmark the current moment without pausing. A small
  dialog asks for a name and suggests the chapter's title. A scissors button marks a range: press it at the start of a
  scene and again at the end. A **Set end time** button turns an existing bookmark into a range that ends at the current
  moment.
- **On the progress bar:** each bookmark shows as an amber tick, and a range as a band. Point at a tick to see its name.
  Click it to play from there.
- **On a movie or episode page:** an **Add bookmark** button and a **My bookmarks** section. From there you can play
  from a bookmark, edit, rename or delete it, and make, preview or download its clip.
- **My Bookmarks page** (in the menu under **Library tools**): every bookmark you have, across all videos. You can
  search, filter, sort, show them grouped by video or as one list, rename them in place and delete several at once.
- **My Clips page** (under **Library tools**, after My Bookmarks): every clip you have made, including clips you kept
  when you deleted their bookmark. Each clip shows its state (ready, being made, failed, file missing, clip folder
  offline). You can preview, download, rename, remake, cancel or delete clips, and play the video from a clip's start.
- **Clips** are made in the background in one of two modes. **Accurate** re-encodes to an H.264/AAC MP4 that is frame
  exact and plays anywhere (HDR is tone-mapped to SDR). **Fast** copies the original streams into an MKV, which is nearly
  instant but starts on the keyframe at or before your start time.
- **For admins:** a settings page with a Data tab (export, import, snapshots, restore, the Jellyfin Enhanced import) and
  a Diagnostics tab. Admins can also switch My Clips to **All users** to see and manage every user's clips of the videos
  they can open. This view never shows notes.
- **Bookmarks follow their video** when it is moved or renamed. While a video is gone they wait as "Not in library".

## Requirements

- **Jellyfin 10.11.5 or later within 10.11.** Jellyfin 12 and later are not supported yet. On any other Jellyfin version the
  plugin adds nothing to the web client and leaves its data alone.
- **No other plugin is required.** The plugin adds its own script to the web client's page when the server sends it, so
  it does not need File Transformation or JavaScript Injector. It does not use Custom Tabs.
- **Optional plugins:**
  - **Plugin Pages:** only if you want the My Bookmarks and My Clips links in Plugin Pages' menu section instead of
    Library tools. To use it, change **Where the pages are linked** on the settings page.
  - **Jellyfin Enhanced:** only as a source to import bookmarks from. The import reads Jellyfin Enhanced's bookmark files
    as versions 12.8 to 12.12 write them. The switch-off checklist gives its steps for Jellyfin Enhanced 12.11 and 12.12.
    The plugin never changes Jellyfin Enhanced's files or settings.
  - **Library Maintenance:** Bookmarks reads its move journal at every start, so bookmarks would follow a video it
    moves. Library Maintenance 1.0.4 only *previews* moves, so for now there is nothing to follow.
- **Clips need ffmpeg.** The plugin uses the ffmpeg Jellyfin is set up with (Dashboard -> Playback -> Transcoding), so
  nothing extra needs installing. Without it, bookmarks still work and only clip making fails. A user can make and
  download clips only if they have Jellyfin's **Allow media downloading** permission.
- **Web client only.** The controls appear in Jellyfin's web client (desktop, phone and TV layouts). Phones get larger
  touch targets and bottom-sheet dialogs. The TV layout works with the remote's D-pad and Back key. Apps that don't use
  Jellyfin's web client won't show the bookmark controls. In the web client's experimental layout there is no Library
  tools menu section, but links to the pages from video pages and the player still work.
- **Tested on**: Windows only, with Jellyfin 10.11.5. Linux and Docker servers have not been tested yet.

## Installation

1. **From the plugin repository:** go to Dashboard -> Plugins -> Repositories and add `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`. Then open
   the Catalog, install **Bookmarks**, and restart the server.
2. **Manually:** download the zip from the GitHub Releases page (<https://github.com/spooneyryan/jellyfin-plugins/releases>). Extract it into the
   `plugins/Bookmarks_1.2.2.0` folder inside Jellyfin's data folder, then restart the server.
3. **After installing or upgrading**, reload the web client with **Ctrl+F5** in every open browser tab so it loads the
   plugin's script.

## Getting started

1. Open **Dashboard -> Plugins -> Bookmarks**. On the **Diagnostics** tab, check that the plugin shows as active and that
   the clip folder has enough free space.
2. On the **Settings** tab, review the **Clips** section:
   - **Clip folder:** leave it empty to use the default folder (inside the plugin's data folder, outside every library).
     Its free space is shown under the field.
   - **Keep free on the clip drive (MB):** 2048 by default. Clips that would go below this are refused.
   - **Default clip mode**, **Maximum clip length** (600 seconds by default), **Allow users to create clips**.
   - **Admins can see and manage every user's clips:** on by default. Turn it off if admins shouldn't see other users'
     clip names and file paths.
3. Give clip makers Jellyfin's **Allow media downloading** permission (Dashboard -> Users -> the user).
4. If you use Jellyfin Enhanced's bookmarks, move them over (see below) before people start using both.

## Using it

- **Bookmark a moment:** press **Shift+B** in the player, or click the bookmark button. Type a name or press Escape to
  skip. Each user can set their own shortcut on their device from **My Bookmarks -> Player options**. The admin can change
  the server's default shortcut or hide the player buttons on the settings page.
- **Make a clip:** give the bookmark an end time (the scissors button, **Set end time**, or the editor's End field). Then
  use **Save & make clip** in the editor, or the scissors on a bookmark row. A message appears when the clip is ready,
  with a download link and **View in My Clips**.
- **Delete a bookmark:** its clip is kept and stays on My Clips, unless you tick **Also delete its clip**.
- **Import from Jellyfin Enhanced** (one time, admins): open **Data -> Import from Jellyfin Enhanced** on the settings
  page. Run a **Dry run** first, which changes nothing and shows what would happen. Then run **Import**. Running it again
  doesn't add anything twice. Then follow the switch-off checklist beneath it, in order: turn off Jellyfin Enhanced's
  bookmarks feature and its tabs, remove its "Bookmarks" Custom Tab, restart, and **last** give Jellyfin Enhanced's
  "Bookmark Current Time" shortcut a different key. Never leave that key blank, because Jellyfin Enhanced puts `B` back at
  its next start. Once `B` is truly free, the checklist offers **Use B for bookmarks**.
- **Admins' All users view:** on My Clips, switch from **My clips** to **All users**. There you can preview, download,
  cancel or delete any user's clips. It lists the clips of the videos you can open. Clips of videos no longer in the
  library are shown only to admins with access to every library. Renaming, remaking and editing are only available on
  your own clips.

## Important notes and limitations

- **Don't use Disable on Jellyfin's Plugins page.** On Jellyfin 10.11.5, the next server start after Disable deletes the
  disabled plugin's folder. To hide Bookmarks for a while, untick **Add the bookmark controls to the web client** on its
  settings page instead.
- **A missing library drive:** while a video is gone (for example on an unmounted drive), its bookmarks stay and show as
  "Not in library". You can still edit, rename and delete them. When the video comes back, they reattach. They are kept
  for good unless you set **Keep bookmarks of missing videos (days)**, and even then they are only moved to an archive,
  never deleted.
- **A missing clip drive:** if the clip folder's drive or share isn't there, its clips show as **Clip folder offline**
  (or **Clip folder not recognised**). Only Play and Edit bookmark are offered for them, nothing is marked as gone, and
  new clips into that folder are refused until it is back. If its clips really are gone for good, confirm that on the
  Diagnostics tab with **This is the clip folder; its clips are gone**.
- **A clip file deleted by hand:** the plugin may then stop recognising the clip folder, and new clips fail with "The
  clip folder isn't recognised". Use the same Diagnostics button, but only if it really is the clip folder.
- **Clips kept inside a library are visible to everyone** who can open that library. The default clip folder is outside
  every library. If you want clips browsable in Jellyfin, use a separate **Home Videos and Photos** library for the clip
  folder, never a Movies or TV library.
- **Clip limits:** clips can only be made from regular video files, not from disc folders, ISOs or remote streams. A clip
  includes the video and the audio track that was playing, without subtitles. Clips are made one at a time by default.
- **Maintenance never deletes clip files.** The daily **Bookmarks: Maintenance** task (Dashboard -> Scheduled Tasks)
  marks clips whose file is gone. After 30 days it archives a kept clip of a deleted bookmark.
- **Going back to 1.1.x:** before you downgrade, check that the Diagnostics tab says "Clip file renames not finished:
  none." Version 1.1 would read a clip whose rename is still waiting as having no file.
- **Admins only:** the settings page, the Jellyfin Enhanced import, export, import, restore and the All users view all
  need an administrator.
- **Web client changes:** the controls depend on the 10.11 web client's layout. If a future web client moves something,
  only that feature switches off, and the Diagnostics tab lists what browsers reported.

## Your data and privacy

- **Where it is stored:** in `data/bookmarks/` inside Jellyfin's data folder (for example
  `%ProgramData%\Jellyfin\Server\data\bookmarks` on Windows, `/var/lib/jellyfin/data/bookmarks` on Linux,
  `/config/data/bookmarks` in Docker). It holds `bookmarks.json` with a backup copy (`bookmarks.json.bak`),
  `snapshots/`, `archive/`, and the default clip folder `clips/`. The settings are in
  `plugins/configurations/Jellyfin.Plugin.Bookmarks.xml`.
- **Your bookmarks are private.** Names, notes, positions and clips are shown only to their owner. Admins see only counts
  per user on the Diagnostics tab. In the All users view, admins also see clip names and file paths, but never notes.
  The server log doesn't record bookmark names or notes at its normal (Information) level.
- **When a user is deleted** from the server, their bookmarks' names and notes are removed from the bookmark data, its
  backup copy and the archive. A clip file that the **When Jellyfin deletes a user** setting keeps stays as it is, and
  its file name still carries the bookmark's name; **Delete their bookmarks and their clip files** removes those files
  too. Snapshots taken before the deletion still hold the text until they rotate out. Users deleted before 1.2.1 keep
  their text. An admin can clear it on the settings page's **Data** tab: under **Users this server doesn't know**, press
  **Apply the deleted-user policy (...) to them** (the policy set under **When Jellyfin deletes a user**).
- **What an admin can still read:** an export contains every user's names and notes, so keep it as safe as a database
  backup. Clip files are named after the video, the bookmark's name and the range. Clips made before 1.2.0 for an
  unnamed bookmark may still carry the start of its note in the file name.
- **Backups:** Jellyfin's own backups don't include plugin data, so use the settings page's **Data** tab:
  - **Export** downloads all bookmark data as a JSON file. Clip files are not included, so back up the clip folder
    yourself if the clips matter.
  - **Import an export** merges or replaces bookmarks. It has a dry run, and a snapshot is taken first.
  - **Snapshots:** one is taken daily (7 kept) and another before every import or restore. Restore any of them from the
    list.
  - If `bookmarks.json` can't be read, the plugin never starts empty on its own. It restores from the backup copy or a
    snapshot. If neither works, the data stays read-only and the settings page offers **Restore** or **Start empty**.
- **No outside services:** the plugin doesn't contact any outside service. It talks only to your own Jellyfin server.

## Uninstalling

1. Optionally, make an **Export** first (Data tab), and copy the clip folder if you want to keep the clips.
2. Uninstall Bookmarks from Dashboard -> Plugins, then restart the server. (Don't use Disable; see above.)
3. What stays behind: the data folder `data/bookmarks/` (bookmarks, snapshots, and clips in the default clip folder), the
   settings file `plugins/configurations/Jellyfin.Plugin.Bookmarks.xml`, and any clip folder you chose yourself. If you
   install the plugin again, it picks these up. To remove everything, stop the server and delete them.
4. If you switched Jellyfin Enhanced's bookmarks off, turn them back on in its settings and give **Bookmark Current
   Time** its `B` key again. Bookmarks never changed Jellyfin Enhanced's own bookmark files, so they are as they were.

## More detail

- The full technical reference (every setting, the REST API, how data is stored and recovered) is in
  [DEVELOPMENT.md](DEVELOPMENT.md) in this folder.
- What changed in each version: the `changelog` in [build.yaml](build.yaml), and in more detail in
  [CHANGELOG.md](CHANGELOG.md).
