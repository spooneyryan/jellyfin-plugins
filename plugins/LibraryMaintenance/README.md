# Library Maintenance

Library Maintenance helps you tidy up a Jellyfin server by finding the movies you have more than once. It adds a
Dashboard page for administrators that lists the duplicates across your libraries, shows how much space the extra
copies take, and lets you compare the copies side by side before you decide which to keep. You can also preview what
moving a movie, episode or music video to another folder would involve. The plugin only looks: it never deletes,
moves or changes anything in your libraries.

## Features

- Finds movies you have more than once in your movie and mixed libraries, matched on the title and year or on the
  title alone.
- Leaves out versions Jellyfin already groups together, so they don't show up as duplicates.
- Explains each match: how sure it is, why the copies matched (such as the same TMDb or IMDb id), and what to check,
  like different years, runtimes or editions, disc rips and .strm shortcuts.
- Shows the space the extra copies take and suggests a copy to keep.
- Lets you filter the groups by library, how sure the match is and title, and sort them by how sure, title, space,
  group size or newest added. Your browser remembers your choices.
- Compares the copies side by side: resolution, codecs, HDR, audio tracks, subtitles, extras, date added, how many
  users watched each one and more, with the best value of each detail marked.
- Counts the movies in each library, points out broken version links between copies, and shows the index's numbers
  on a Diagnostics tab.
- Keeps the list up to date after every library scan, with a **Rebuild** button for when you want it now.
- Previews moving a movie, episode or music video to another folder or library: the files, the free space, what is
  kept, such as watch state, collections and playlists, and anything that would stand in the way.
- Lets you export your settings to a file and import them again, and shows what a change to the libraries or
  excluded folders does before you save it.
- Keeps your files safe: nothing is deleted, merged, moved or written.

## Installing

1. In Jellyfin, go to **Dashboard -> Plugins -> Repositories** and add
   `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Open **Catalog**, find **Library Maintenance** and install it.
3. Restart the server, then reload the web client with **Ctrl+F5**.

Library Maintenance needs Jellyfin 12.4 or later.

## Using it

**Find duplicates.** Open **Dashboard -> Plugins -> Library Maintenance**, or its link in the dashboard's menu, and go
to the **Duplicates** tab. The first time, it reads your movies, then shows the groups. You can switch between
**Title + Year** and **Title only**, filter by library, how sure the match is and title, and change the order. Each
group shows its copies with their posters, years, ids and file paths, the reasons they matched and a suggested keep.
The list keeps itself up to date after library scans, and **Rebuild** reads your libraries again whenever you like.
Below the groups you'll find how many movies each library holds, and any broken version links Jellyfin has between
copies.

**Compare copies.** Pick **Compare copies** on a group to see its files side by side. "Best" marks the best value of
each detail, and "Differs" marks the details that aren't the same in every copy. **Open** takes you to a copy's page
in Jellyfin.

**Preview a move.** On a movie, episode or music video, open the "⋮" menu (or **More** on its details page) and pick
**Move media to new location**. Choose a library and a folder, or type a new folder's name. A loose movie or music
video file can go in its own folder. The preview shows what would move, how, the free space, how long it would take,
what is kept and anything in the way. It is a preview only, so there is no Move button.

**Check the numbers.** The **Diagnostics** tab shows how many movies and entries the index holds, how many it left
out, and when it was last built. **Refresh** reads them again.

## Settings

The **Settings** tab holds the server's settings, the same for every administrator:

- **Find duplicate movies**: turn this off to hide the Duplicates tab.
- **Default match mode** and **A movie with no year matches**: how the Duplicates tab starts, until you pick
  something else there.
- **Libraries**: the movie libraries to check. Choose none to check every one, including ones you add later.
- **Excluded folders**: folders whose movies are left out, one full path per line.
- **Title rules**: edition phrases such as "Final Cut" to ignore, with a **Default phrases** button, and the leading
  words to drop, such as "The", in English, French, German, Spanish, Italian or Dutch.
- **Building the index**: whether the list is rebuilt after every library scan, and how long the page waits for a
  rebuild that is running.
- **Web client and Move**: switches to leave the plugin out of the web client, turn the move preview off, or hide the
  Move entry in the item menu.
- **Export the settings** and **Import from a file** to save your settings and bring them back.

When you change the libraries or the excluded folders, or import a file, a dialog first shows what changes and how
many movies the list would hold, and you save from there.

## Questions

- **Who sees it?** Only administrators. Other users see nothing new. Each administrator sees only the libraries and
  movies their own account can see.
- **Where is the Move entry?** It shows on the desktop and mobile layouts of a Windows server, not on the TV layout.
- **Should I use Disable on the Plugins page?** No. To turn parts of the plugin off, use the switches under
  **Settings -> Web client and Move**.
- **Is a preview completely hands-off?** Almost. To see whether another program is using a file, a preview opens it
  for an instant. Avoid previewing something that is still downloading or being imported.
- **Do you use Bookmarks?** If it saves its clips inside a movie library, add that clip folder to **Excluded folders**
  so the clips don't show up as duplicates.

## License

Released under the GNU General Public License v3.0.
