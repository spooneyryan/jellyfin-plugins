# Smart Search And Dynamic Collections

Smart Search And Dynamic Collections gives Jellyfin an Advanced Search page. You can look for movies, shows, episodes, music videos, home
videos and collections by title, year, genre, rating, watch state, the people in them and many other details, and
mix those rules any way you like. Save the searches you use often, share them with others on your server, or turn one
into a dynamic collection that keeps itself up to date. Every search only finds what the person running it is allowed to see.

## Features

- A quick basic search by title, years and genres, in one library or all of them.
- An advanced search with rules over about thirty details, such as studios, tags, runtime, resolution, file size,
  parental rating, play count and last played.
- People rules: find what an actor, director, writer or composer is in, or what two people made together.
- Rules can sit in groups that match all, any or none of them, so you can ask for things like "comedies or dramas
  from the 90s that I haven't watched".
- Results as posters or as a list, sorted the way you want, with Open and Play right there. Every search has its own
  link, so you can bookmark it or send it on.
- Saved searches you can keep private or share. A shared search always runs as the person who opens it.
- Dynamic collections: a normal Jellyfin collection that follows a saved search, adding and removing titles on its
  own as your library changes. You choose its name, order, overview and whose watch state it follows.
- Your own changes stick: items you add to a dynamic collection by hand stay in, and items you take out stay out.
- A safety check holds back large removals when a library looks unhealthy, such as when a drive is missing, until
  you review them. Each dynamic collection keeps a history of its updates.
- A note on a dynamic collection's own page says it updates itself, with **Edit** and **Refresh now** for managers.
- Export, import and daily snapshots, so you can back up and restore everyone's saved searches and dynamic
  collections.
- Works on desktop, phone and TV, from the keyboard and with a screen reader.

## Installing

Smart Search And Dynamic Collections needs Jellyfin 12.4 or later.

1. In the Dashboard, go to **Plugins**, then **Repositories**, and add
   `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Open **Catalog**, find **Smart Search And Dynamic Collections** and install it.
3. Restart the server, then reload the web client with **Ctrl+F5** in any open browser tab.

## Using it

**Finding the page.** Open **Advanced Search** from **Library tools** in the menu, or use the **Advanced search**
button in the header, next to the normal search button.

**Searching.** Pick the kinds of items and a library, fill in the basic form and press **Search**. For more, switch to
**Advanced**. Each rule picks a detail, a condition (contains, is between, in the last, is empty and so on) and a
value. Genres, tags, studios, shows and people offer suggestions as you type. **Add group** puts rules inside rules.
The search is part of the page's address, so you can bookmark it or send it as a link.

**Saving and sharing.** **Save** keeps a search under a name, private or shared. The **Saved queries** tab lists your
searches and the ones shared with you. From there you can run, rename, duplicate, share, copy a link to or delete
your own. Someone else's shared search can be run or copied, but only its owner or an admin can change it.

**Dynamic collections.** These live on the **Auto-collections** tab. Press **New auto-collection**, pick one of
your saved searches, give the collection a name and check the preview before saving. You can also start one with
**Create auto-collection…** on the Search tab, or from a saved search in the **Saved queries** tab. The editor lets
you pick the order, set an overview, lock the collection's details, and choose whose watch state counts for rules
like "not watched".

The collection then updates a few minutes after files are added or removed, after a library scan, every night, and
whenever you press **Refresh** or **Refresh all**. If its search reads watch state, it also updates shortly after
that person watches something. Add or remove items in the collection the usual Jellyfin way and they stay pinned or
kept out, or choose **Always match the query exactly** to undo such changes.

If an update would remove a lot of items while the library looks unhealthy, for example when a drive is missing, the
removals are held. The collection shows **Needs review**, and you can apply the removals, keep the items or wait. A
collection is never emptied on its own unless you allow it. **History** shows each update and what it added and
removed, and lets you put back items the last change took out. You can turn a dynamic collection off, and when you
delete it you choose whether the collection stays as a normal one.

The collection's own page says that it updates automatically. People who manage collections also get **Edit** and
**Refresh now** there.

## Settings

Find them under Dashboard, **Plugins**, **Smart Search And Dynamic Collections**. The ones you're most likely to change:

- Whether the page, the menu link and the header button are on, and whether the page is listed through
  **Plugin Pages**.
- Which kinds of items people can search, and the limits a search runs under (how many items and how long).
- Whether users can share saved searches, how many each one can keep, and what happens to a deleted user's searches.
- Whether dynamic collections stay up to date, how many there can be, and whether they update when watch state
  changes.
- Whether a dynamic collection's page shows its note, and whether a collection without a poster gets a default one.
- The guard against large removals: when it steps in and whether a collection may ever be emptied.
- **Export**, **Import** and **Snapshots** to back up and restore everyone's saved searches and dynamic collections.
  An import or restore always shows what it would change before you apply it. How many daily snapshots to keep is a
  setting too.

Two scheduled tasks do the nightly work: **Smart Search: Refresh auto-collections** updates every dynamic collection,
and **Smart Search: Maintenance** takes the daily snapshot. You can run either from the Dashboard's
**Scheduled Tasks**.

## Questions and notes

- **Who can make dynamic collections?** Admins and users with **Allow collection management** in their Jellyfin user
  settings. Everyone else just sees the collections. Only a collection's creator or an admin can change its name,
  search, watch-state user or whether it is on.
- **Who sees a dynamic collection?** It is shared by everyone on the server, but each person only sees the items in
  it they could open anyway. Its name and description are the same for everyone.
- **Why was my search refused?** A search that would check too many items, run too long or arrive while the server
  is busy is turned down rather than cut short. The page tells you what to narrow, such as a library or a type.
- **The header button is missing.** It sits right before Jellyfin's own search button, so it only shows where that
  button does. The menu link still works.
- **Plugin Pages** isn't needed. If you have it, a setting can list the page in its part of the menu instead.
- **Export files** hold every user's saved searches, private ones too, so keep them somewhere safe.
- **Uninstalling** leaves your saved searches and settings behind, so a reinstall finds them again. Dynamic
  collections stay as ordinary collections.
- Bugs and questions go to <https://github.com/spooneyryan/jellyfin-plugins/issues>.

## License

GPL-3.0, see the LICENSE file at the root of the repository.
