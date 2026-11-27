# Jellyfin plugins

Six plugins for [Jellyfin](https://jellyfin.org), made for people who run a home server and share it with family and
friends. Each one installs on its own, and none of them needs another plugin to work.

| Plugin | What it does |
| --- | --- |
| [Access Roles](plugins/AccessRoles/README.md) | Group your users into roles that decide which libraries they can open and which settings and dashboard pages they can reach. You see every change before it takes effect. |
| [Bookmarks And Clips](plugins/Bookmarks/README.md) | Mark favourite moments while you watch, give them names, and turn any marked stretch into a clip you can keep and share. |
| [Library Maintenance](plugins/LibraryMaintenance/README.md) | Find the movies you have more than once, and preview what moving a title to another folder would involve. It never deletes or moves anything itself. |
| [Smart Search And Dynamic Collections](plugins/SmartSearch/README.md) | Search your movies, shows and episodes by any mix of title, year, genre, rating, watched status and more. Save the searches you like, and turn any of them into a collection that keeps itself up to date. |
| [Tag Cloud](plugins/TagCloud/README.md) | See your library's tags, genres, people and studios as a word cloud, with the most common words drawn biggest. Click a word to see the titles behind it. |
| [User Ratings And Reviews](plugins/UserRatings/README.md) | Let everyone on your server rate titles from half a star to five and write short reviews, with an average shown on each title's page. |

Each plugin's own page explains what it does and how to use it.

## What you need

- Jellyfin 12.4 or later.
- Everything shows up in Jellyfin's web app. You don't need any other plugin to make it appear.
- So far the plugins have been tested on Windows servers.

## Installing

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add
   `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Open **Catalog**, find the plugin you want and install it.
3. Restart the server, then reload Jellyfin in your browser with **Ctrl+F5**.

To remove a plugin, uninstall it from **Dashboard → Plugins** rather than using **Disable**.

## If Jellyfin won't start after an update

Three plugins were renamed on 22 November 2026: Bookmarks became Bookmarks And Clips, Smart Search became Smart Search
And Dynamic Collections, and User Ratings became User Ratings And Reviews. Updating one of them from its old name leaves
Jellyfin with two copies of the plugin, one under each name.

The current versions (Bookmarks And Clips 2.5.2, Smart Search And Dynamic Collections 2.1.2 and User Ratings And
Reviews 2.0.2) sort this out on their own. After you install the update and restart, one of two things happens:

- Jellyfin has already set the older copy aside, and the new copy simply runs.
- Or the older copy is still running for this one start. The server's log then has a warning about it, the new copy
  turns the older one off, and the Dashboard asks for a restart. Restart Jellyfin once more and the new copy takes over.

The Plugins page may keep listing the older copy as disabled for a while. Jellyfin removes it when the server next
starts fresh, for example after the computer restarts, and you can also remove it yourself with **Uninstall** on the
Dashboard's Plugins page. Your bookmarks, clips, saved searches, dynamic collections, ratings and settings are all kept.

If you installed one of the first renamed versions (2.5.1, 2.1.1 or 2.0.1) next to a copy under the old name, Jellyfin
may not start at all. To fix it:

1. Open the `plugins` folder inside Jellyfin's data folder.
2. Delete the folder with the old name, for example `Bookmarks_2.5.0.0`. Keep the one with the new name, for example
   `Bookmarks And Clips_2.5.1.0`.
3. Start Jellyfin again, and update the plugin from the catalog as usual.
4. Open the settings page of each of these plugins and look the settings over. A plugin that was caught in the failed
   start may be back at its default settings; set them again if so.

Your bookmarks, clips, saved searches, dynamic collections and ratings are stored separately from the settings, so they
are kept.

## Questions and problems

Ask questions or report problems on the [Issues page](https://github.com/spooneyryan/jellyfin-plugins/issues). It
helps to include the plugin's name and version, your Jellyfin version, and what the plugin's **Diagnostics** tab shows.

## Privacy

None of the plugins sends anything to an outside service. Everything they keep stays in Jellyfin's data folder on your
own server.

## License

Copyright (C) 2026 NoRepine.
