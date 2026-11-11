// Start-up. The kit waits for window.ApiClient (polled every 25 ms) and for the signed-in user before it runs the
// scanners; each scanner runs after DOM changes, on viewshow and on hash changes. On each viewshow of
// the details page or the player, the kit checks the anchors Bookmarks needs there (10-selectors.js).
// The My Bookmarks page (60-page-bookmarks.js) and the My Clips page (70-page-clips.js) are registered with the
// kit's page host, which renders each on its route (#/jfbm-bookmarks, #/jfbm-clips, or Plugin Pages' link) and links it
// from "Library tools", My Bookmarks first, while the server's head fragment lists it as on.

kit.pages.register(Object.assign({ render: renderBookmarksPage }, BOOKMARKS_PAGE));
kit.pages.register(Object.assign({ render: renderClipsPage }, CLIPS_PAGE));
kit.configure({ toastRaised: isPlayerOpen });
kit.playback.onReport(onPlaybackReport);
kit.playback.onReport(onTimelineReport);
state.changeListeners.add(onTimelineChange);
kit.observe(injectPlayerButtons);
kit.observe(syncPlayerShortcut);
kit.observe(drawTimelineMarkers);
kit.observe(syncDetailPage);
kit.onView(checkViewAnchors);
kit.start();
