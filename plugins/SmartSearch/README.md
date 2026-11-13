# Smart Search

Smart Search is a Jellyfin plugin that adds an **Advanced Search** page: find movies, shows, episodes, music videos,
home videos and collections by rules over their title, year, library, type, genres, ratings, watch state and more,
combine the rules in groups, and save searches, private or shared. Every search runs as the signed-in user, over the
libraries they can open: it never shows anyone something Jellyfin's own library views wouldn't show them. A saved
search can also become an **auto-collection**: a Jellyfin collection the plugin keeps equal to the search's results.

## Features

For everyone:
- **The Advanced Search page**, under **Library tools** in the menu and behind the **Advanced search** button in the
  header, right before the search button.
- **A basic search**: a title that contains some text, a year or a range of years, and genres, over the item types
  and the library you pick. Text matches ignore case and accents ("amelie" finds *Amélie*).
- **An advanced search**: rules over about thirty fields (title, original title, overview, series, library, type,
  year, dates, genres, tags, studios, people, parental rating, show status, community and critic ratings, runtime, resolution,
  container, video type, 3D, file size, bitrate, played, favourite, in progress, play count, last played, provider
  ids, locked), in groups that match **All**, **Any**, **None** or **Not all** of their rules.
- **Results** as posters or rows, sorted fifteen ways, with **Open** and **Play**. The search is part of the page's
  address, so it can be bookmarked or sent as a link, and Back and Forward go through your searches.
- **Saved searches** on the **Saved queries** tab: save, rename, duplicate, share, copy a link to, and delete them.
  A shared search always runs as the person who opens it.
- The page works on desktop, on a phone, on the TV layout with a remote, from the keyboard alone and with a screen
  reader, and follows the dashboard's theme.

For admins and users who manage collections:
- **Auto-collections** on the page's **Auto-collections** tab: collections that update themselves from a saved
  search, with a preview before saving, pinned and excluded items, and a guard against large removals.

For admins:
- The settings page: whether the page and the header button are on, which item kinds users can search, the limits a
  search runs under, sharing, how many searches each user may keep and what happens to a deleted user's searches, and
  the auto-collections' settings.
- **Export**, **Import** and **Snapshots** of every user's saved searches and the auto-collections, each checked first
  with a dry run.
- The file path as a search field (administrators only).

## Requirements

- **Jellyfin 10.11.5 or later within 10.11.** Jellyfin 12 and later are not supported yet. On a Jellyfin version the plugin
  wasn't built for, it stays inactive: it adds nothing to the web client, and its API answers that it can't run.
- **No other plugin is needed.** Smart Search adds its own script to the web client; it does not need File
  Transformation or JavaScript Injector.
- **Plugin Pages is optional.** With it, the page can be listed in Plugin Pages' section of the menu instead (the
  setting **Where the page lives**, after a restart).
- **Web client layouts:** desktop, mobile and TV. In Jellyfin's experimental layout the header button doesn't show;
  the menu link remains.
- **Tested on**: Windows only, with Jellyfin 10.11.5. Linux and Docker servers have not been tested yet.

## Installation

**From the plugin repository**
1. Dashboard → **Plugins** → **Repositories** → add `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Go to **Catalog**, install **Smart Search**, then restart the server.

**Manually**
1. Download the zip from the GitHub Releases page: <https://github.com/spooneyryan/jellyfin-plugins/releases>.
2. Extract it into the `plugins/Smart Search_1.2.0.0` folder inside Jellyfin's data folder, and delete an older
   `Smart Search_…` folder. That folder is, for example, `%ProgramData%\Jellyfin\Server` on Windows,
   `/var/lib/jellyfin` on Linux, or `/config` in Docker.
3. Restart the server.

**After installing**, reload the web client with **Ctrl+F5** in every open browser tab. A browser only picks up the
plugin's script when it loads the web client again.

## Getting started

1. Open the settings page: Dashboard → **Plugins** → **Smart Search**. Each setting shows its default.
   - **Page**: **Add Smart Search to the web client**, **Turn the Advanced Search page on**, **Link it from Library
     tools in the menu** and **Add an Advanced search button next to the header's search button** (all on), and
     **Where the page lives** (its own address by default, or Plugin Pages when it is installed; a change needs a
     restart).
   - **Searches**: the item kinds users can search (all six by default) and the limits: **Most items one search
     checks** (100,000), **Longest search** (20 seconds), **Searches at once** (2) and **Keep results for their next
     pages** (120 seconds).
   - **Saved queries**: **Let users share their saved queries** (on), **Most saved queries per user** (200), what
     happens to a deleted user's searches (deleted by default; shared ones stay, as "Deleted user"), and **Daily
     snapshots to keep** (7).
   - **Auto-collections**: **Keep auto-collections up to date**, the note on the collection's page and the default
     poster (all on),
     **Most auto-collections** (100), **Most items in one auto-collection** (5,000), the timings of updates after
     library changes, **Update when watch state changes** (on), how collections are written, and the guard against
     large removals (on: half of a collection and at least 5 items, and never empty).
2. Open **Advanced Search** from **Library tools** in the menu, or with the header button, and try a search.

## Using it

- **Search:** choose the item types (Movies and Shows to start with) and a library or all of yours, fill in the basic
  form and press **Search** or Enter. **Reset** empties the fields. The page remembers the types and library you last
  searched in this browser.
- **Advanced:** switch to **Advanced** to build rules. Each rule picks a field, a condition that suits it (contains,
  is any of, is between, in the last, is empty and more) and a value; fields with a few values offer buttons, and
  genres, tags, studios and shows offer suggestions from what you can see. **Add group** nests a group (four levels
  deep; up to 50 rules). A rule that is wrong is marked before the search runs. A search the basic form can hold can
  go back to **Basic**.
- **Results:** the count and the time the search took, sorted by title, year, release date, date added, rating, last
  played and more, either way round. **Grid** shows posters, **List** rows with the type, runtime, rating and date
  added. More load as you scroll, or with **Load more**.
- **Save:** **Save** keeps the search under a name, with a description, private or shared. Once saved, the page names
  it at the top; change the search and **Save** updates it, or **Save as new…** keeps the change as another search.
- **The Saved queries tab:** your searches and those shared with you (filter *Mine*, *Shared with me* or *All*, and
  find by name or description). Click a name to run it. Your own have **Rename**, **Duplicate**, **Share** or **Stop
  sharing**, **Copy link** and **Delete**; others' have **Duplicate** (a private copy) and **Copy link**.
- **Sharing:** a shared search is listed for every user and can be run or copied by them, but only its owner or an
  administrator can change or delete it. It runs as the person who opens it and finds only what they could find
  themselves. Others see it as its owner wrote it: the series, studios and values it names, and its libraries (only
  as a count, "1 library not available to you", when they can't open them); the page says so before you share. A
  search that uses the administrator-only file path can't be shared.
- **Back up and restore (admins):** on the settings page, **Export** saves every user's saved searches and the
  auto-collections to a file; **Import** reads one back, here or on another server, merged with the searches there or
  replacing them, with owners matched by user id or by name; **Snapshots** lists the copies of the saved searches, each
  with **Check restore** and **Restore**. An import or restore first shows what it would add, update, leave unchanged
  or skip, and why, and only then can it be applied.

## Auto-collections

An auto-collection is an ordinary Jellyfin collection that the plugin keeps equal to a saved search's results, with
items you pin or keep out by hand. Administrators and users allowed to manage collections (**Allow collection
management** in Jellyfin's user settings) get the page's **Auto-collections** tab; everyone else just sees the
collections.

- **Creating one:** **New auto-collection** on the Auto-collections tab, or **Create auto-collection…** on the Search
  tab and on a saved query. Pick one of your own saved searches (someone else's shared search is copied for you
  first, so their later edits never change your collection), the collection's name (checked as you type; a name
  another collection or folder already uses is refused, with free names to pick), the order (the search's order,
  release date or name), whether to lock the collection's metadata (on by default), and what happens to changes made
  in Jellyfin's own editor. **Preview** shows how many items it would hold and some of them; **Save** makes the
  collection.
- **When they update:** two to three minutes after files are added or removed outside a scan (Jellyfin notices a
  file after a minute or more, and the plugin waits for a quiet minute so that a burst of changes becomes one
  update); once a library scan has ended; every night at 04:00 (the task **Smart Search: Refresh auto-collections**); five minutes
  after the server starts; and at once with **Refresh** or **Refresh all**. A search that reads watch state (played,
  favourite, play count and the like) uses one user's watch state, chosen in the editor (you, or anyone when an
  administrator picks), and also updates about a minute after that user marks something played or unplayed or a
  favourite, rates it or finishes playing it; other users' changes start nothing. The tab shows each one's status,
  last change (*+N −M*) and history.
- **The guard against large removals:** the plugin never empties a collection on its own. When an update would remove
  many items (by default half of them and at least 5) and the library looks unhealthy (a library folder missing, far
  fewer items found than before, a scan running), it adds the new items but holds the removals, and the row shows
  *Needs review*. **Review removals** lists them; **Keep these items** pins them (up to 1,000 pinned items in all),
  and **Apply removals** lets exactly the removals shown, or fewer, go with the next update; if more would go by then,
  or the collection would be emptied, they are held again for a new review. An update that stops before it writes
  (the auto-collection turned off, the server busy) needs a new approval. A hold clears on its own once the items are
  back. A large removal while the library looks healthy (you deleted the files) is simply applied.
- **Pinned and excluded items, and changes in Jellyfin's own editor:** with **Keep my manual adds and removes** (the
  default), an item added to the collection in Jellyfin's editor that the search doesn't find becomes a pinned item,
  and an item removed that the search finds becomes an excluded item, however many at once, and also during a scan
  (sorted once the scan has ended). The editor lists both and can remove entries. **Always match the query exactly**
  puts such changes back instead, and also the movies Jellyfin adds on its own to a collection of the same name when a
  library is set to. Up to 1,000 pinned and 1,000 excluded items; changes beyond that, or more than 5,000 between two
  updates, wait for a manager (the collection is left as it is meanwhile; saving the auto-collection or switching it to
  *Always match the query exactly* settles it). Pinned and excluded items follow files moved with Library
  Maintenance. **Renaming** the collection in Jellyfin's editor is kept, unless the auto-collection was changed since
  its last update or another auto-collection has that name (then its own name is written back).
- **The poster:** Jellyfin makes no image for a locked collection, so a collection without a poster gets the plugin's
  default auto-collection poster, once, when it is made or at its next update. A poster you upload is never replaced,
  and one you remove or replace later stays as you left it. **Give a collection without a poster the default one** on
  the settings page turns it off; that removes no poster.
- **Who may change what:** the creator or an administrator changes the name, the search, whose watch state it uses and
  whether it is on. Any user who manages collections may change another creator's auto-collection's order, overview,
  safeguards and pinned or excluded items, keep or apply its held removals, refresh it, and see why its last update
  failed (an administrator sees the full message, others a short one without file paths or search details). An
  auto-collection with its own copy of a search shows that copy only to its creator and administrators. An API key
  can only list the auto-collections and refresh them.
- **Who sees what:** a collection is shared by everyone on the server. Each user sees only the items in it they could
  open themselves, but its name and overview are the same for everyone, and the chosen user's watch state decides the
  items everyone sees. The editor says so, and marks a collection whose items not everyone can open (*Mixed
  audience*).
- **The saved search an auto-collection follows:** renaming, describing or sharing it changes no collection. Changing
  the search itself needs an administrator or a user who manages collections, and is refused when it would stop an
  auto-collection (for example by adding Collections to the item kinds); **Detach these collections, then save** lets
  each keep a copy of the search as it was. Deleting it asks first and detaches them the same way. Administrators'
  imports, restores and owner changes list the auto-collections they would stop and go ahead only when forced; those
  then show what is missing, with **Choose another query**. Only its creator's own searches count: an auto-collection
  following a search that now belongs to someone else (after an owner change or an import) has stopped, holds none of
  that user's changes back and gets no copy of their search.
- **A deleted creator:** the auto-collection and its saved search stay; it shows *Creator missing* until an
  administrator uses **Give to this user** on the Auto-collections tab, which moves both to an administrator or a user
  who manages collections (if that user has room for one more saved search) and keeps the user whose watch state it
  reads.
- **On the collection's page:** a note says it updates automatically from a saved search and when it last did, for
  everyone who can open the collection; users who manage collections also get **Edit** and **Refresh now** there. A
  setting turns the note off.
- **Deleting:** **Delete** on the tab keeps the collection as a normal collection, unless you choose **Delete the
  collection too**. A collection deleted in Jellyfin shows *Collection deleted* on the tab; **Recreate** makes it again.
  If someone makes a collection of their own that takes the deleted one's place, the plugin leaves it alone and asks
  what to do.
- **Images:** a collection the plugin makes has no image until one is uploaded (**Edit images** on its page); the
  plugin's updates keep an uploaded image.
- **Online metadata:** the plugin's own updates never fetch any. An **unlocked** collection can still get online
  metadata and images from Jellyfin's own refreshes (when it is created, and during library scans), and is always
  ordered by release date; a locked one never fetches anything.
- **Order on the collection's page:** Jellyfin groups a collection's items by type and keeps the order within each
  type; a new order shows once the page is reloaded.
- **The collection's parental rating** is worked out from its items each time the plugin writes the collection, not
  when an item's rating changes on its own. It is the least restrictive rating among the items that have one, as
  Jellyfin does for its own collections: items without a rating don't count, so a collection whose only rated item is
  R is R, and people with a lower limit don't see it. When no item has a rating, the collection keeps the one it had.
- **Export and import:** the settings page's **Export** also saves the auto-collections (their searches, pinned and
  excluded items and creators, not the collections), and **Import** reads them back, merged by id with the ones there:
  an import never removes one, an auto-collection already there keeps its collection, and a new one has no collection
  until a manager uses **Create the collection** on the Auto-collections tab. Its creator must be a user there; an
  imported one whose name is already taken gets a number; one whose saved search is missing or isn't its creator's is
  kept and named in the report. An updated one changes its collection at its next update.
- **Restoring an older copy of the auto-collections** (`data/smartsearch/auto-collections.json` from a backup):
  auto-collections made after that copy are lost, and their collections stay as ordinary collections that nothing
  updates. The settings page's **Diagnostics** lists them (`CollectionsWithoutDefinition`), together with held
  removals and collections carrying an auto-collection's mark twice. Delete them, or make new auto-collections and
  delete the old collections.
- **When the auto-collections can't be read** (a damaged or missing file, or one from a newer version that can't be
  read at all), it isn't known which searches they follow: deleting a saved search, changing its search, giving it to
  another user, and imports, restores or remaps that remove or change searches are refused until they can be read
  again (renaming, describing and sharing still work), and no search goes with a deleted user. An auto-collection
  saved by a newer version of the plugin shows *Needs a newer Smart Search* and its collection is left alone.
- **How collections are written:** by default the plugin replaces a collection's items in one save. A setting can use
  Jellyfin's own add and remove instead; then a collection ordered by its search is ordered by name, links to deleted
  files stay (counted, without causing writes), and Jellyfin's own refresh after each change may fetch online
  metadata for an unlocked collection.

## Important notes and limitations

- **Don't use Disable on the Plugins page.** On Jellyfin 10.11.5, a disabled plugin's folder is deleted at the next
  start. To take Smart Search out of the web client for a while, untick **Add Smart Search to the web client** or
  **Turn the Advanced Search page on** instead, and **Keep auto-collections up to date** to stop the updates. None of
  them deletes anything.
- **A search is refused rather than cut short** when it would check more items than the limit, runs longer than the
  limit, or arrives while the server is busy. The page says why and what to add (a library, a type or a series, for
  example). On a library of about 20,000 items, a search checked item by item returns its first page in about a
  second; over 60,000 items it takes a few seconds.
- **During a library scan** new items may be missing from results, and suggestions come from the last list of values
  until the scan ends. Auto-collections wait for the scan to end.
- **Merged versions** show as one row, which matches if any version matches (a film merged with its 4K copy matches
  *Resolution 2160p*). Alternate versions kept in the same folder, and extras, are never results.
- **A community rating of 0** counts as a rating in searches, as in Jellyfin's own filters, although item pages show
  no rating for it.
- **Genres, tags and studios are matched as text:** after a rename in the metadata, a saved search naming the old
  value finds nothing.
- **Jellyfin Enhanced:** its card overlays also show on the results. Its Hidden Content doesn't change what the
  server finds, but its script hides the cards of hidden items in the results, so the count and the pages can hold
  more than you see. Its spoiler protection blurs the result images but doesn't hide titles or overviews.
- **Other collection plugins** (SmartLists, Auto Collections) do similar things with their own rules; Smart Search's
  auto-collections reuse its saved searches. Don't let two plugins manage the same collection.
- **People rules** find what people are credited on, as actors, directors, writers, producers, composers or guest
  stars, or in any role, matching any, all, none or not all of them ("Robert De Niro and Al Pacino as actors"). A show's
  cast is credited on the show, a guest star on the episode. People are suggested only when they're credited on
  something you can see: a user whose parental control blocks unrated items isn't suggested people credited only on
  unrated items. Suggestions match the text ignoring case and accents ("zoë" finds "Zoë Kravitz"), but a typed name
  without its accent ("zoe") doesn't find an accented one. Each suggestion request reads up to 1,000 names holding the
  text and checks up to 100 of them, those starting with the text first. On Jellyfin 12 those names are only people
  credited on what you can reach; on Jellyfin 10.11 they're people credited anywhere, so with many people you can't
  see, a name can be missed (type more of it). On Jellyfin 10.11 a People rule with a role is also checked item by
  item, so it can be slower on a large library.
- **Not in 1.2:** searching by collections, audio and subtitle languages or HDR, regular expressions, moving
  rules within a search (duplicate and remove instead), a search link on Jellyfin's own search page, and
  auto-collections' own genres (a locked collection gathers none).

## Your data and privacy

- **Where it is:** the settings in `plugins/configurations/Jellyfin.Plugin.SmartSearch.xml` and the saved searches in
  `data/smartsearch/saved-queries.json`, inside Jellyfin's data folder. Each search keeps its name, description,
  rules, owner (by user id) and when it was saved and last run.
- **Auto-collections:** `data/smartsearch/auto-collections.json` (each one's name, search, pinned and excluded items,
  creator) and `data/smartsearch/auto-collections-state.json` (when each last ran, its counts and recent history; it
  is rebuilt when lost). The collections themselves are ordinary Jellyfin collections, marked with the plugin's
  provider id, and stay when the plugin is removed.
- **Backups:** Jellyfin's own backups leave plugin data out, so keep a copy of `data/smartsearch` and the settings
  file with yours, or use **Export**. The daily task **Smart Search: Maintenance** (Dashboard → **Scheduled Tasks**)
  keeps snapshots of the saved searches in `data/smartsearch/snapshots`, and one is also taken before every import and
  restore. `data/smartsearch/kit-users.json` notes deleted users.
- **Export files** hold every user's saved searches, private ones too, and the auto-collections, with their owners'
  user names: keep them safe.
- **Search results** stay in the server's memory for a few minutes and the suggestion lists until the library
  changes; neither is written to disk.
- **Your libraries:** nothing is ever written to them. Auto-collections write only their own collections.
- **No external services:** the plugin makes no calls outside your Jellyfin server.

## Uninstalling

1. While the plugin still runs, use **Export** on its settings page to keep a copy of the saved searches and
   auto-collections.
2. Dashboard → **Plugins** → **Smart Search** → **Uninstall**, then restart the server.
3. The data in `data/smartsearch/` and the settings file `plugins/configurations/Jellyfin.Plugin.SmartSearch.xml`
   stay behind, so reinstalling finds them again. To remove them, stop the server first, then delete them. The
   auto-collections' collections stay as ordinary collections; delete them in Jellyfin if you don't want them.
4. Reload the web client with **Ctrl+F5** in every open browser tab.

## Reporting issues

Please report bugs and ask questions on the GitHub Issues page: <https://github.com/spooneyryan/jellyfin-plugins/issues>. Include the plugin's version, your
Jellyfin version, and what the settings page's **Diagnostics** section shows.

## More detail

- The technical documentation (the query model, the fields, how a search is planned and run, the auto-collections,
  the API, building and testing): [DEVELOPMENT.md](DEVELOPMENT.md), in this folder.
- The changelog is the `changelog` in [build.yaml](build.yaml).
