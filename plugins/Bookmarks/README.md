# Bookmarks And Clips

Bookmarks And Clips lets everyone on your Jellyfin server save moments in movies and episodes, give them a name and jump back to
them later. Each person's bookmarks are their own. Give a bookmark an end time and the plugin can cut that scene into a
clip file you can watch, download or keep in a library. If your household used Jellyfin Enhanced's bookmarks, you can
bring them over in one go.

## Features

- Bookmark the current moment with **Shift+B** or a button in the player, without pausing, and give it a name. The
  chapter's title is suggested for you.
- Add a bookmark from a movie's or episode's page, and mark a whole scene with the scissors button in the player.
  Step frame by frame with the **,** and **.** keys to land on exactly the right frame.
- See your bookmarks as small marks on the progress bar, and click one to play from there.
- Find them again in a **Bookmarks** row on each video's page, and on the **My Bookmarks** page, where you can search,
  filter, sort, group them by video and delete several at once.
- Choose your own shortcut and naming habits for your device under **Player options**.
- Turn a scene into a clip, in **Accurate** or **Fast** mode, then preview, download, rename, remake or delete it on the
  **My Clips** page.
- Keep clips in a library of their own, with the movie's poster and backdrop or the clip's first frame as artwork.
- Export a whole playlist into one video file, joined in playlist order.
- Admins can see and manage every user's clips, without ever seeing anyone's notes.
- Bring over everyone's Jellyfin Enhanced bookmarks in one go, with a checklist for switching its own bookmarks
  off.
- Keep your bookmarks safe with export and import, daily snapshots you can restore, and bookmarks that stay with their
  video when you move or rename the file.

## Installing

Bookmarks And Clips needs Jellyfin 12.4 or later.

1. In Jellyfin, go to **Dashboard -> Plugins -> Repositories** and add
   `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Open the **Catalog**, find **Bookmarks And Clips** and install it.
3. Restart the server, then reload the web client in your browser (Ctrl+F5).

No other plugin is needed. Clips use the ffmpeg Jellyfin already has, so there is nothing extra to install.

## Using it

- **Bookmark a moment.** While a video plays, press **Shift+B** or click the bookmark button. Type a name, or press
  Escape to skip it. The chapter's title is suggested for you.
- **Mark a scene.** Press the scissors button at the start of the scene and again at the end. Or use **Set end time** to
  end an existing bookmark at the current moment.
- **Fine-tune it.** In the player's **Bookmarks** list, pick **Adjust**, step to the right frame and choose **Set start
  here** or **Set end here**, then **Save**.
- **Find your bookmarks.** A video's page has a **Bookmarks** row and a **Clips** row. Click a card to play it, or open
  its **⋮** menu to edit, rename or delete it. **My Bookmarks** and **My Clips** are in the menu under **Library
  tools**. There you can search, filter, sort and delete several at once. A video's page also has an **Add bookmark**
  button.
- **Pick your own keys.** Under **My Bookmarks -> Player options** you can set your own bookmark shortcut for this
  device, choose whether to be asked for a name, and pause while you type it.
- **Make a clip.** Open a bookmark that has an end time and choose **Save & make clip**, or pick **Make a clip** from its
  **⋮** menu. A message tells you when the clip is ready. A clip file is named like
  `Bank job - Heat (1995) [01.52.10-01.55.00].mp4`: the clip's name, then the movie and year, then the time range.
- **Look after your clips.** On **My Clips** you can filter by state, type and mode, and sort by date, length or size. A
  badge tells you when a clip's bookmark was renamed or its range changed, so you can rename the file or make the clip
  again. You can also cancel a clip that is still being made.
- **Export a playlist.** On a playlist's page, choose **Export to clips folder**. You see what will be joined, what is
  left out and roughly how big it will be before it starts. Its progress shows on My Clips.
- **Import from Jellyfin Enhanced.** Admins can do this once from the **Data** tab of the settings page. Run a **Dry
  run** first to see what would happen, then **Import**. A checklist then walks you through switching off Jellyfin
  Enhanced's own bookmarks, so the two don't fight over the B key.
- **Manage every user's clips.** Admins get an **All users** view on My Clips. There they can preview, download,
  cancel and delete anyone's clips.

## Settings

Find them at **Dashboard -> Plugins -> Bookmarks And Clips**.

- **Clip folder**: where clips are saved. Leave it empty to use a folder outside all your libraries.
- **Keep free on the clip drive**: clips that would leave less free space than this are refused (2 GB by default).
- **Default clip mode**: **Accurate** makes an MP4 that starts on the exact frame and plays anywhere. **Fast** copies the
  original into an MKV almost instantly, but may start a moment early.
- **Maximum clip length** and **Allow users to create clips**.
- **Admins can see and manage every user's clips**: on by default. Turn it off if admins shouldn't see other people's
  clip names.
- **Give clips their video's artwork** and **Use each clip's first frame as its thumbnail**: what a clip library shows
  for each clip.
- **Put each user's clips in a sub-folder named after the user.**
- **Accurate clip encoding**: the quality, maximum resolution and audio of Accurate clips.
- **Bookmark shortcut**: the server's default key. Each person can pick their own under **My Bookmarks -> Player
  options**. **Ask for a name** and **Suggest the chapter's name** set how naming works.
- **Pages**: turn My Bookmarks and My Clips on or off, and choose where they are linked: **Library tools** in the menu,
  or the Plugin Pages plugin's section.
- **When Jellyfin deletes a user**: delete their bookmarks and keep their clip files, or delete both.
- **Daily snapshots to keep**: how many daily copies of the bookmark data to keep.

The **Data** tab has export and import, snapshots you can restore, and a way to match bookmarks of users this server
doesn't know to users by name. The **Diagnostics** tab shows whether everything is working and how many bookmarks
each person has.

## Questions / Notes

- **Who can make clips?** Only users with Jellyfin's **Allow media downloading** permission (Dashboard -> Users).
- **Who can see my bookmarks?** Only you. Admins see how many bookmarks each person has, and in My Clips' **All users**
  view they see clip names and files, but never your notes.
- **Want clips in a library?** Make a folder just for clips, add a **Home Videos and Photos** library for exactly that
  folder, and enter it as the clip folder. Don't use a Movies or TV library: Jellyfin would show the clips as
  duplicates of the movie. Everyone who can open that library can see every user's clips.
- **Backups.** Jellyfin's own backups don't include plugin data. Use **Export** on the Data tab, and copy the clip folder
  if the clips matter to you.
- **Which apps?** The controls appear in Jellyfin's web client, on desktop, phone and TV layouts. Apps with their own
  player, such as Android TV, don't show them.
- **A drive went missing?** Bookmarks of videos that are gone wait as "Not in library" and reattach when the video comes
  back. Clips on an unplugged drive show as **Clip folder offline**, and nothing is marked as gone until the drive is back.

## License

GPL-3.0-only.
