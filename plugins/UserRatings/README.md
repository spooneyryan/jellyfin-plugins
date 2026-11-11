# User Ratings

User Ratings is a Jellyfin plugin that lets the members of your server rate movies, shows, seasons, episodes,
albums, books and videos from half a star to five stars, and write a review. You rate and review on the item's details
page. Ratings are shared across the server: every member sees every other member's ratings and reviews on the titles
they can open, along with the members' average. Admins choose who may rate, can remove reviews, and can export, import
and restore all ratings. If you used Jellyfin Enhanced's user reviews, you can import them, and you can also export them
back to Jellyfin Enhanced.

## Features

For members:
- A **Member ratings & reviews** section on the details page, just before the cast. It has the members' average
  (stars out of 5, to one decimal), how many ratings there are, a histogram, your own rating and review, and the
  other members' reviews (sort them newest, oldest, highest or lowest; 20 at a time, with Load more).
- Your stars are saved as you change them. You can write, edit, clear or delete your review.
- You can mark a review as containing spoilers. It then shows blurred until a reader chooses **Show spoiler**.
- If you leave the page before saving, your unsaved review text stays on that device until you save or discard it.
- A **Members 4.2/5 (7)** chip next to the item's other scores, and a **Rate** button that jumps to your rating.

For admins:
- Choose who may rate (everyone, or only some users) and which kinds of items can be rated. You can also require that
  a title be played before it can be rated.
- **Moderation**: browse and search every rating, remove a review's text or the whole rating, with a log of each
  removal. Admins also get **Remove...** on other members' reviews on the details page.
- **Data**: export, import, daily snapshots with Restore, and a list of detached ratings that you can relink. These
  tools are for administrators with **Enable access to all libraries** (see *Important notes* below).
- Ratings follow their items when they are renamed or moved, and they are kept while a library drive is away.
- Import from Jellyfin Enhanced (with a dry run first), and **Export for Jellyfin Enhanced**.

## Requirements

- **Jellyfin 10.11.5 or later within 10.11.** Jellyfin 12 and later are not supported yet. On a Jellyfin version the plugin
  wasn't built for, it stays inactive: it adds nothing to the web client, and its API answers that it can't run.
- **No other plugin is required.** The plugin adds its script to the web client by itself, so it does **not** need File
  Transformation or JavaScript Injector.
- **Jellyfin Enhanced is optional.** You only need it if you want to import its user reviews, or to go back to it with
  **Export for Jellyfin Enhanced**. Both work with the `reviews.json` format of Jellyfin Enhanced **12.14**, the
  version they were tested with. The import also reads an older layout of that file, where a season's or episode's
  entry holds only the series' TMDb id. User Ratings only reads Jellyfin Enhanced's files and settings: it never
  changes them, and it never turns Jellyfin Enhanced's reviews off for you.
- **Library Maintenance (optional):** User Ratings follows the moves recorded in Library Maintenance's move journal,
  so ratings would go with the items it moves. Library Maintenance 1.0.4 only *previews* moves, so for now there is
  nothing to follow.
- **Web client layouts:** desktop, mobile and TV (and the experimental layout). On a phone, the average chip hides the
  word "Members" and keeps the icon and the number. On touch screens the stars are larger and have − / + buttons. In
  the TV layout, the D-pad works on the stars (Left and Right), the Rate button and every settings control. It also
  moves past Jellyfin Enhanced's closed Reviews section instead of getting stuck there.
- **Tested on**: Windows only, with Jellyfin 10.11.5. Linux and Docker servers have not been tested yet.

## Installation

**From the plugin repository**
1. Dashboard → **Plugins** → **Repositories** → add `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Go to **Catalog**, install **User Ratings**, then restart the server.

**Manually**
1. Download the zip from the GitHub Releases page: <https://github.com/spooneyryan/jellyfin-plugins/releases>.
2. Extract it into the `plugins/User Ratings_1.0.2.0` folder inside Jellyfin's data folder. That folder is, for
   example, `%ProgramData%\Jellyfin\Server` on Windows, `/var/lib/jellyfin` on Linux, or `/config` in Docker.
3. Restart the server.

**After installing**, reload the web client with **Ctrl+F5** in every open browser tab. A browser only picks up the
plugin's script when it loads the web client again.

## Getting started

1. Open the settings page: Dashboard → **Plugins** → **User Ratings**. It has four tabs: **Settings**, **Moderation**,
   **Data** and **Diagnostics**.
2. On **Settings**, check the settings below. Each one shows its default, and **Save** first tells you what the change
   means before anything is saved:
   - **Who can rate and review**: *Every user* (the default) or *Only the users chosen below*. Everyone can still read
     every rating and delete their own.
   - **What can be rated**: by default Movies, Series, Seasons, Episodes, Videos, Music videos, Albums, Books and
     Audiobooks.
   - **Only after playing**: off by default.
   - **Leave out hidden users' ratings** (off by default) and **Leave out disabled users' ratings** (on by default).
   - **Rate button**: by default it appears where the section is far down the page (series, seasons, albums) and on
     pages with Jellyfin Enhanced's reviews. You can also set it to *On every details page* or *Never*.
   - **When a user is deleted**: *Delete their ratings and reviews* (the default), *Keep them as a former member's*, or
     *Keep them, counted nowhere, until an admin decides*.
   - **Daily snapshots to keep** (7), **Archive detached ratings after** (90 days) and **Keep the moderation log for**
     (365 days).
3. If you used Jellyfin Enhanced's reviews, see *Moving from Jellyfin Enhanced* below.

The settings under *Coming with version 1.1* (poster badges, a Top Rated page) are kept but do nothing yet.

## Using it

- **Rate and review:** open a title's details page and set the stars in *Member ratings & reviews*. Then choose to write
  a review if you want to. Clearing both your stars and your text deletes your rating.
- **Moderate:** on the **Moderation** tab, find a rating with the search or filters, then choose **Remove...** and pick
  *Remove text only* (the stars stay) or *Remove rating and review*. You see a preview first, and you can add an
  optional reason. Each removal appears in the moderation log below the list. The log keeps who did what, never the
  review's text.
- **Back up:** on the **Data** tab, **Export** saves every rating, review and the moderation log as
  `userratings-export-<time>.json`. You can import that file on this server or another one with **Merge** or
  **Replace**. An import always runs a dry run first.
- **Restore:** **Data** → **Snapshots** → **Restore...**. A snapshot of the current data is taken first. Everything since
  the snapshot is undone, except ratings deleted on this server since then, which stay deleted.
- **Detached ratings:** **Data** → **Detached & ambiguous** lists ratings whose title left the library, or that could
  belong to more than one copy. **Relink** gives a rating to the right title. **Purge** deletes it for good.

### Moving from Jellyfin Enhanced

1. Before members start rating, go to **Data** → **Import from Jellyfin Enhanced**. Choose your options and press **Dry
   run**. This only reads Jellyfin Enhanced's `reviews.json`. Read the report, and map any Jellyfin Enhanced members
   who aren't users on this server.
2. Press **Import**. A snapshot is taken first, and restoring it undoes the import.
3. Under **Switch Jellyfin Enhanced's reviews off**, press **Use the same hiding rules**. Then, in Jellyfin Enhanced's
   own settings, turn off each item the checklist marks *To do*. The banner on User Ratings' page goes away once
   everything is done.

**Going back to Jellyfin Enhanced:** first run the import once more, so you also get reviews written in Jellyfin
Enhanced since the switch. Then press **Export for Jellyfin Enhanced**, which downloads a `reviews.json`. Nothing is
written on the server. **Stop the server** and keep a copy of Jellyfin Enhanced's current `reviews.json`. Put the
exported file in its place in `plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced/`, inside Jellyfin's data
folder. Start the server again and turn Jellyfin Enhanced's reviews back on.

> **Privacy:** Jellyfin Enhanced shows every review to any signed-in user, whatever libraries they can open. By default
> the export leaves out reviews of titles that not every member can see, because of a library or a parental limit.
> Keep that choice unless every member may read every review. Also check Jellyfin Enhanced's own two hiding rules
> before you start the server with the file. Jellyfin Enhanced saves reviews of up to 2,000 characters. User Ratings
> allows 5,000 by default, so set **Longest review** to 2,000 if you may go back.

## Important notes and limitations

- **Don't use Disable on the Plugins page.** On Jellyfin 10.11.5, a disabled plugin's folder is deleted at the next
  start. To take User Ratings out of the web client for a while, turn off **Add User Ratings to the web client** on
  the Settings tab instead. To hide all ratings, turn off **Ratings**. Neither one deletes anything.
- **A missing library drive:** while a library folder is missing, unreadable or empty (for example an unmounted drive),
  its ratings stay on their items and wait. The Data tab lists them, and **Detach now** is there for items you know are
  gone. A rating whose title really leaves the library is *detached*, never deleted. After **Archive detached ratings
  after** days (90 by default), it moves to an archive, still not deleted, and comes back when its title returns. Mount
  your drives before you start Jellyfin: Jellyfin's own cleanup may remove its own data for folders it can't find.
- **Admins with limited library access:** the Data tab's export, import, jobs, snapshots and restore, the Jellyfin
  Enhanced import and export, and the unknown-users tools all cover every library's ratings. So they need an
  administrator with **Enable access to all libraries**. For other admins the tab hides them and says why. In
  Moderation, such an admin sees only ratings, and log entries, for titles they can open. Detached ratings show only to
  admins with every library. The settings are server-wide, so any admin can change them.
- A member who loses access to a title can't see their rating on it in the web client, but it stays and still counts
  for members who can see the title.
- The daily maintenance and a restore never run at the same time as an export or import job. Only one data job runs at
  a time.

## Your data and privacy

- **Where it is:** everything is in `data/userratings/` inside Jellyfin's data folder. That includes the SQLite
  database `userratings.db`, daily snapshots in `snapshots/` (pre-operation snapshots, kept 7 days, in
  `snapshots/pre-op/`) and the archive of detached ratings in `archive/`. The settings are in
  `plugins/configurations/Jellyfin.Plugin.UserRatings.xml`.
- **Daily task:** *User Ratings: Maintenance* runs at 03:20 (Dashboard → **Scheduled Tasks**). It checks the database,
  takes the daily snapshot, archives old detached ratings, applies the deleted-user policy and trims the moderation
  log.
- **Who sees what:** every member sees every member's ratings and reviews, with their names, on the titles they can
  open (their libraries and parental limits). Hidden or disabled users' ratings are left out when the settings above
  say so.
- **Deleted reviews:** a deleted or removed review is overwritten in the database. Older snapshots keep it until they
  expire (7 days by default).
- **Export files:** an export's file on the server is deleted within an hour. A downloaded copy is yours to keep safe.
  The export holds every review.
- **No external services:** User Ratings sends nothing to any outside service. Your ratings never leave your server
  unless you download an export.

## Uninstalling

1. While the plugin still runs, use **Data** → **Export** to keep a copy of the ratings. Also use **Export for Jellyfin
   Enhanced** if you plan to go back to it.
2. Dashboard → **Plugins** → **User Ratings** → **Uninstall**, then restart the server.
3. The ratings in `data/userratings/` and the settings file `plugins/configurations/Jellyfin.Plugin.UserRatings.xml`
   stay behind, so reinstalling finds them again. To remove them, stop the server first. It's safest to move the
   `userratings` folder aside rather than delete it, because it holds every member's ratings and reviews.
4. If you switched from Jellyfin Enhanced, turn its user reviews back on in its settings.

## More detail

- The full technical documentation is in [DEVELOPMENT.md](DEVELOPMENT.md), in this folder.
- The changelog is the `changelog` in [build.yaml](build.yaml).
