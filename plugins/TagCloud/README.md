# Tag Cloud

Tag Cloud turns your library's tags, genres, people and studios into a word cloud, with the most common words drawn
biggest. Click any word to see the movies and shows behind it. It's a fun way to see what your library holds at a
glance, and to find something to watch you'd forgotten about.

## Features

- A word cloud of your tags, genres, actors, directors, writers or studios, for movies, shows or both, from all your
  libraries or just the ones you pick.
- Click a word to see its titles as posters, then filter, sort or keep only what you haven't watched.
- Hold a word down to see its titles as a cloud of their own. Click one to open it.
- Right-click words to narrow the cloud to the titles they share.
- Rest the mouse on an actor, director or writer to see their photo.
- Change the layout, shape, font, sizes, colours and backdrop to your taste.
- Save the clouds you like, then rename, copy, share or delete them, or show one full screen.
- Everyone sees counts for their own titles only, so nothing hidden from them shows up.
- Works on a TV too, where a cloud starts with fewer words.
- The page can sit in the Plugin Pages plugin's menu, if you use it.

## Installing

Tag Cloud needs Jellyfin 12.4 or later.

1. In Jellyfin, go to **Dashboard -> Plugins -> Repositories** and add
   `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Open **Catalog**, find **Tag Cloud** and install it.
3. Restart the server, then reload the web client with **Ctrl+F5**.

No other plugin is needed.

## Using it

- **Open it.** Pick **Tag Cloud** under **Library tools** in the menu.
- **Choose a cloud.** Use the toolbar to pick what to count, which titles, which libraries and how many words.
- **Find titles.** Click a word and its titles show below the cloud. You can filter them by name, sort them, pick a
  minimum resolution or tick **Unwatched only**.
- **See a word's titles as a cloud.** Hold a word down. Its titles show as a cloud of their own; rest the mouse on one
  to see its poster, or click it to open it. Press **Esc** to go back.
- **Narrow it down.** Right-click a word to add it as a filter. The cloud then keeps only the words that share titles
  with it. **Show all** clears the filters.
- **Change the look.** Open **Settings** in the toolbar. Changes show as you make them, and **Reset to defaults**
  puts them back. With the **Cloud** layout, **New arrangement** shuffles the words.
- **Show it full screen.** Use **Full screen** in the toolbar.
- **Save it.** **Save cloud** keeps the cloud and its look under a name. Your saved clouds sit under **Saved clouds**,
  where you can rename, copy, share or delete one, copy its link, or open it full screen.
- **Count again.** Admins see **Recount** in the toolbar, to count the library again on the spot.

## Settings

Admins find these under **Dashboard -> Plugins -> Tag Cloud**:

- Whether the Tag Cloud page is on, and whether it's linked under **Library tools** in the menu.
- Where the page lives: its own address, or the Plugin Pages plugin's menu. This needs a restart.
- Which clouds are offered, and the most words a cloud may show.
- Whether users may share their saved clouds with everyone.
- How many words a cloud starts with on a TV, where drawing lots of words is slow.
- How many daily backups of the saved clouds to keep.
- **Count the library again**, if the cloud looks out of date. The page also shows how many titles and words were
  counted, and when.

The nightly count and backup run as the **Tag Cloud: Maintenance** task under **Dashboard -> Scheduled Tasks**.

## Questions

- **How fresh are the counts?** The library is counted when the server starts and again every night. Small changes
  show up about a minute after they happen.
- **What does a shared cloud show others?** Its name and settings, never your counts. Everyone who opens it sees their
  own titles.
- **How do I turn it off?** Use the switches on the plugin's settings page rather than **Disable** on the Plugins page,
  then reload the web client.

## License

GPL-3.0-only.
