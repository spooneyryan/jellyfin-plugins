# Jellyfin plugins

Six server plugins for [Jellyfin](https://jellyfin.org), written for home-server admins who share their server with
family and friends. Each plugin is installed on its own; none of them needs another plugin to work.

| Plugin | Version | What it does |
| --- | --- | --- |
| [Access Roles](plugins/AccessRoles/README.md) | 1.0.3 | Give users roles that lock parts of their Settings menu, keep them away from the Dashboard's data, and decide which libraries they can open. The server enforces the roles for every client, and every change is previewed first. |
| [Bookmarks](plugins/Bookmarks/README.md) | 1.2.2 | Bookmark moments in movies and episodes from the web player (Shift+B), give them names and end times, and cut a bookmarked range into a clip with Jellyfin's own ffmpeg. Adds My Bookmarks and My Clips pages, and can import Jellyfin Enhanced's bookmarks. |
| [Library Maintenance](plugins/LibraryMaintenance/README.md) | 1.0.4 | An admin-only page that lists the movies your libraries hold more than once, plus a *preview* of moving a movie, episode or music video to another folder. It is read-only: nothing is deleted, merged or moved. |
| [Smart Search](plugins/SmartSearch/README.md) | 1.1.2 | An Advanced Search page: find movies, shows and episodes by rules over their title, year, library, genres, ratings, watch state and more, and save searches, private or shared. Every search runs as the signed-in user, over the libraries they can open. Auto-collections keep a Jellyfin collection equal to a saved search. |
| [Tag Cloud](plugins/TagCloud/README.md) | 1.0.1 | A Tag Cloud page: the tags, genres, actors, directors, writers or studios of your movies and shows as a word cloud, each word sized by how many titles carry it. Click a word for its titles, narrow the cloud, change its look, save clouds (private or shared) and show them full screen. Counts only ever cover what the viewer may see. |
| [User Ratings](plugins/UserRatings/README.md) | 1.0.2 | Members rate anything from half a star to five stars and write reviews on its details page, shared with everyone on the server, with a member average. Admins choose who may rate, moderate reviews, and can import Jellyfin Enhanced's reviews. |

Each plugin's own README explains its features, settings, data and limitations.

## Requirements

- **Jellyfin 10.11.5 or later within 10.11.** Jellyfin 12 and later are not supported yet; on another Jellyfin version the
  plugins stay inactive.
- The plugins add their features to Jellyfin's **web client**. No other plugin (such as File Transformation or
  JavaScript Injector) is needed for that.
- **Tested on Windows only**, with Jellyfin 10.11.5. Linux and Docker servers have not been tested yet.

## Installing

**From the plugin repository (recommended)**

1. In Jellyfin, go to **Dashboard -> Plugins -> Repositories** and add `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Open **Catalog**, find the plugin you want and install it.
3. Restart the server.

**Manually**

1. Download the plugin's zip from the GitHub Releases page: <https://github.com/spooneyryan/jellyfin-plugins/releases>.
2. Extract it into a folder named `<Name>_<version>` under `plugins/` in Jellyfin's data folder, for example
   `plugins/User Ratings_1.0.2.0`. Jellyfin's data folder is usually `%ProgramData%\Jellyfin\Server` on Windows,
   `/var/lib/jellyfin` on Linux, or `/config` in Docker.
3. Restart the server.

**After installing**, reload the web client with **Ctrl+F5** in every open browser tab, so it picks up the plugin's
script.

**Don't use *Disable* on the Plugins page.** On Jellyfin 10.11.5, a disabled plugin's folder is deleted at the next
server start. Each plugin's README says what to use instead, and how to uninstall it cleanly.

## Reporting issues

Please report bugs and ask questions on the GitHub Issues page: <https://github.com/spooneyryan/jellyfin-plugins/issues>. Include the plugin's name and
version, your Jellyfin version, and what the plugin's **Diagnostics** tab shows.

## Privacy

None of the plugins sends anything to an outside service. They store their data in Jellyfin's data folder on your own
server.

## More detail

How to build the plugins, run their tests and sandboxes, and how the repository is laid out: [DEVELOPMENT.md](DEVELOPMENT.md).

## Licence

Copyright (C) 2026 NoRepine.

GPL-3.0-only (the GNU General Public License, version 3), the licence of the Jellyfin packages the plugins build against. See [LICENSE](LICENSE).
