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
And Dynamic Collections, and User Ratings became User Ratings And Reviews. If you had one of them installed under its
old name and then updated it, Jellyfin can end up with two copies of the plugin and refuse to start.

To fix it:

1. Open the `plugins` folder inside Jellyfin's data folder.
2. Delete the folder with the old name, for example `Bookmarks_2.5.0.0`. Keep the one with the new name, for example
   `Bookmarks And Clips_2.5.1.0`.
3. Start Jellyfin again.

Your bookmarks, clips, searches, collections and ratings are stored elsewhere and are kept.

Those three updates have been taken out of the catalog while this is fixed, so for now the catalog lists the three
plugins under their old names. A fixed version will follow.

## Questions and problems

Ask questions or report problems on the [Issues page](https://github.com/spooneyryan/jellyfin-plugins/issues). It
helps to include the plugin's name and version, your Jellyfin version, and what the plugin's **Diagnostics** tab shows.

## Privacy

None of the plugins sends anything to an outside service. Everything they keep stays in Jellyfin's data folder on your
own server.

## License

Copyright (C) 2026 NoRepine.
