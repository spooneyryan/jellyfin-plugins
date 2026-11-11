# Tag Cloud

Tag Cloud adds a **Tag Cloud** page to Jellyfin's web client. It shows the tags, genres, actors, directors, writers or
studios of your movies and shows as a word cloud, each word sized by how many titles carry it. It is for anyone who
wants another way to browse a library: see what it holds at a glance, then click a word to find its titles. Every
count covers only the titles the viewer may see.

## Features

- **The Tag Cloud page**, under **Library tools** in the menu. Pick what to count (**Tags**, **Genres**, **Actors**,
  **Directors**, **Writers**, **Studios**), which titles (**Movies**, **TV shows**, **Both**), any of your libraries, and
  how many words (25 to 1,000, in steps of 25).
- **Click a word** to list its titles under the cloud. **Hold it** for a cloud of up to 200 of its titles, each one
  opening its page. **Right-click it** (or press the menu key) to add it to the filters: the cloud then keeps only the
  words that share titles with every filter.
- **Settings** change the look live: **Cloud** (packed) or **Rows**, the shape, how freely sizes mix, word and line
  spacing, letter case, font, the largest and smallest sizes, the size scale, eleven palettes, colour by word or by size,
  and six backdrops. Each device keeps its own look.
- **Saved clouds** keep all of the above under a name. Open one again, rename it, duplicate it, share it with everyone
  (when the admin allows it), copy its link or delete it. A shared cloud shares its settings, never its counts: each
  viewer sees their own titles.
- **Full screen** shows only the cloud, over the whole window and, when the browser allows it, the whole screen. Start
  it from the page's **Full screen** button, from a saved cloud's ⛶ button, or from a link. **Esc**, **Back** or the
  exit button (which fades while the pointer is still) closes it.

## Requirements

- **Jellyfin 10.11.5 or later within 10.11.** Jellyfin 12 and later are not supported yet.
- **No other plugin is needed.** Tag Cloud adds its own script to the web client; it does not use File Transformation,
  JavaScript Injector or Plugin Pages. If Plugin Pages is installed, the page can also be listed there (see the
  settings).
- **Web client layouts**: the page works on the desktop, mobile and TV layouts. On a TV a cloud starts with fewer words
  (150 by default), because drawing many words is slow there.
- **Tested on**: Windows only, with Jellyfin 10.11.5. Linux and Docker servers have not been tested yet.

## Installation

**From the plugin repository**
1. In Jellyfin, go to **Dashboard -> Plugins -> Repositories** and add `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Open **Catalog**, find **Tag Cloud** and install it.
3. Restart the server.

**Manually**
1. Download the zip from the GitHub Releases page: <https://github.com/spooneyryan/jellyfin-plugins/releases>.
2. Extract it into the `plugins/Tag Cloud_1.0.1.0` folder inside Jellyfin's data folder.
3. Restart the server.

**After installing**, reload the web client with **Ctrl+F5** in every open browser tab, so it picks up the plugin's
script.

## Getting started

1. After the restart, the plugin counts your library once in the background. Its settings page (**Dashboard ->
   Plugins -> Tag Cloud**) shows how many titles and words it found, and when.
2. Open the menu and pick **Tag Cloud** under **Library tools**.
3. Choose a source and a scope in the toolbar, narrow the libraries, or move the words slider.

## Using it

**Find titles.** Click a word: its titles are listed below the cloud, by name. The selection is part of the page's
address, so Back and a copied link return to it.

**Narrow the cloud.** Right-click a word to add it as a filter; each filter shows as a chip above the cloud. **Show all**
removes them. A filter only ever matches the titles you can see.

**Change the look.** Open **Settings** in the toolbar. Changes apply as you make them; **Reset to defaults** keeps the
word count.

**Save a cloud.** **Save cloud** keeps what the cloud counts and how it looks under a name. Your saved clouds, and the
ones others shared, are listed under **Saved clouds**. A saved cloud keeps no counts: opening it counts again, for
whoever opens it.

**For administrators**, the settings page has:
- **Index**: the count's numbers, and **Count the library again**.
- **Page**: switch the page and its menu link on or off, where the page lives (its own address, or Plugin Pages), and
  whether browsers' reports of web client problems are recorded.
- **Clouds**: which clouds are offered (a cloud turned off isn't counted either), the most words a cloud may show (100
  to 2,000), the word count a TV starts with, and whether users may share saved clouds.
- **Upkeep**: after how many changed titles the library is counted again in full, and how many daily snapshots of the
  saved clouds to keep.
- **Diagnostics**, for troubleshooting.

## Important notes and limitations

- **Don't use Disable on the Plugins page.** On Jellyfin 10.11.5 a disabled plugin is deleted at the next server start.
  To switch Tag Cloud off, use the settings page instead: **Turn the Tag Cloud page on** off removes the page and its
  link and turns the clouds and saved clouds off for everyone (the settings page's index and Diagnostics stay), and **Add the Tag Cloud to the web client** off removes its script altogether. Save, then reload the web client
  with Ctrl+F5.
- **How fresh the counts are.** The library is counted when the server starts, every day at 03:40 (Scheduled Tasks,
  "Tag Cloud: Maintenance") and after a large library change. Smaller changes are applied title by title about a
  minute after they happen.
- **TV shows count as series**, never as episodes. Words are counted ignoring case and are shown in the casing most of
  their titles use.
- **Administrators see server-wide totals.** The index numbers on the settings page and in Diagnostics count every
  library, also for an administrator whose account can't open all of them. They name no title and no word.
- **Not in this version:** export and import of the saved clouds. The saved clouds have a backup copy and daily
  snapshots (see below).

## Your data and privacy

- **Where it is stored:** in `data/tagcloud/` inside Jellyfin's data folder (for example
  `%ProgramData%\Jellyfin\Server\data\tagcloud` on Windows, `/var/lib/jellyfin/data/tagcloud` on Linux,
  `/config/data/tagcloud` in Docker). It holds the saved clouds, `clouds.json`, with a backup copy (`clouds.json.bak`)
  and `snapshots/`. The settings are in `plugins/configurations/Jellyfin.Plugin.TagCloud.xml`. The counts live in
  memory only.
- **Each viewer sees only their own titles.** Their libraries, parental rating and blocked tags apply to every count, so
  a word that only hidden titles carry never shows, nor does its count.
- **Saved clouds are private** unless their owner shares them. A shared cloud shows everyone it is shared with its name,
  its owner's name, its narrowing words and the ids of the libraries it was saved with (each viewer still counts only
  their own titles, and a library they can't open is left out). When the admin turns sharing off, shared clouds are
  listed only to their owners, who can still stop sharing them. When a user is deleted, their private clouds are deleted
  too; their shared clouds stay, owned by "Former user", until an admin deletes them.
- **API keys** can't use the clouds, because there is no user to count for.
- **No outside services:** the plugin doesn't contact any outside service. It talks only to your own Jellyfin server.

## Uninstalling

1. Dashboard -> **Plugins** -> **Tag Cloud** -> **Uninstall** (not Disable), then restart the server.
2. The saved clouds in `data/tagcloud/` and the settings file `plugins/configurations/Jellyfin.Plugin.TagCloud.xml` stay
   behind, so reinstalling finds them again. To remove them, stop the server and delete both.
3. Reload the web client with Ctrl+F5. The menu no longer has the Tag Cloud link.

## More detail

- The technical documentation (how it counts, the API, building and testing) is in [DEVELOPMENT.md](DEVELOPMENT.md), in
  this folder.
- The changelog is the `changelog` in [build.yaml](build.yaml).
