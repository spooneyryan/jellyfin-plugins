# User Ratings And Reviews

User Ratings And Reviews lets the people on your server rate movies, shows, seasons, episodes, albums, books and more from half a
star to five stars, and write a review if they like. You rate right on the title's details page. Everyone sees each
other's ratings and reviews on the titles they can open, along with the members' average, so it's easy to see what the
household or your friends thought. If you used Jellyfin Enhanced's reviews, you can bring them over.

## Features

- Rate from half a star to five stars, and write, edit or delete a review. On a touch screen, **−** and **+** buttons
  move the stars by half a star.
- See every member's ratings and reviews on a title, with the average, a histogram and a **Members** score next to the
  title's other ratings. Sort the reviews by newest, oldest, highest or lowest.
- Mark a review as a spoiler, so it stays blurred until someone chooses to read it.
- When a title is in more than one library, see the ratings on the other copy and open it in one click.
- A review you haven't saved yet is kept on your device, so you can pick it up next time.
- Pick who can rate, which kinds of titles can be rated, and whether members must play a title first. Leave out the
  ratings of hidden or disabled users if you like.
- Remove reviews that don't belong, from the **Moderation** tab or right under the review, with a log of each removal
  and its reason.
- Back up, restore and move all the ratings with export, import and daily snapshots, to this server or another one.
- Ratings stay with their titles when files are renamed or moved, and wait safely while a drive is unplugged. Ratings
  whose title left the library are kept, and you can give them back to a title or delete them.
- Choose what happens to a deleted user's ratings, and hand ratings from a user who isn't on the server any more to
  the user with the same name.
- Bring reviews over from Jellyfin Enhanced, or send them back.
- Works in the desktop, phone and TV layouts of the web client. No other plugin is needed.

## Installing

User Ratings And Reviews needs Jellyfin 12.4 or later.

1. In the Dashboard, go to **Plugins** → **Repositories** and add
   `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Open **Catalog**, find **User Ratings And Reviews** and install it.
3. Restart the server, then reload the web client (Ctrl+F5) in any open browser tabs.

## Using it

- **Rate and review:** open a title's details page and set the stars in **Member ratings & reviews**. Your stars are
  saved as you go. Add a review if you want to. Clearing both your stars and your text removes your rating.
- **Jump to it:** on long pages, such as a series or an album, the **Rate** button takes you straight to the section.
- **Read the reviews:** use **Sort** to change the order, and **Load more** for the next page. A spoiler stays hidden
  until you choose **Show spoiler**.
- **Moderate:** in Dashboard → **Plugins** → **User Ratings And Reviews**, the **Moderation** tab lists every rating. Search for one,
  or filter by review, date or whether it's still on a title, then choose **Remove...** to take away just the review
  text or the whole rating. Admins also see **Remove...** under each review on a title's page. The **Moderation log**
  below the list shows who removed what and why.
- **Back up and restore:** the **Data** tab has **Export** and **Import**, and **Snapshots** with **Restore**. An import
  can **Merge** with the ratings here or **Replace** them, and can match members by name when the file comes from
  another server. An import always shows you a dry run first.
- **Ratings left behind:** on the **Data** tab, **Detached & ambiguous** lists ratings whose title left the library.
  **Relink** gives one back to a title, and **Purge...** deletes it for good.
- **Users who are gone:** **Unknown users** on the **Data** tab lists ratings whose member isn't on the server.
  **Remap by name...** gives them to the user with the same name, and **Apply the policy...** treats them like a deleted
  user's.
- **Coming from Jellyfin Enhanced:** on the **Data** tab, use **Import from Jellyfin Enhanced**. Run the dry run, then
  **Import**, and follow the checklist to turn Jellyfin Enhanced's own reviews off. **Export for Jellyfin Enhanced**
  takes you back if you change your mind.

## Settings

All settings are on the **Settings** tab and take effect when you save. The ones most people look at:

- **Who can rate and review:** every user, or only the users you pick. Everyone can still read the ratings.
- **What can be rated:** movies, series, seasons, episodes, videos, music videos, albums, books and audiobooks by default.
- **Only after playing:** members must play a title before rating it. Off by default.
- **Longest review:** from 2,000 to 20,000 characters, 5,000 by default.
- **Leave out hidden or disabled users' ratings:** their ratings show only to them and to admins, and don't count in
  the average. On for disabled users, off for hidden ones, by default.
- **Details page:** turn the **Members** score and the histogram on or off, set how many ratings a title needs before
  its average shows, how many reviews fit on a page, and whether the section starts open.
- **Rate button:** where the button appears on details pages.
- **Data and retention:** how many daily snapshots to keep (7), after how many days ratings left behind move to the
  archive (90), and how long to keep the moderation log (365 days).
- **When a user is deleted:** delete their ratings (the default), keep them as a former member's, or set them aside
  until you decide.

## Questions

- **Do members see ratings on titles they can't open?** No. Each person sees ratings only on the titles their libraries
  and parental limits let them open.
- **Why can't an admin see the Data tab's tools?** Export, import and restore cover every library, so they need an admin
  with **Enable access to all libraries** on their account.
- **How do I hide ratings for a while?** Turn off **Ratings** or **Add User Ratings to the web client** on the Settings
  tab. Nothing is deleted.
- **Does anything leave my server?** No. Ratings stay on your server unless you download an export.

## License

GPL-3.0-only.
