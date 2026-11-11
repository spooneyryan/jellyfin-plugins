// Start-up (90-main.js). The kit waits for window.ApiClient (polled every 25 ms) and for the signed-in user
// before it runs any scanner; each scanner runs after DOM changes, on viewshow and on hash changes. It
// starts the details page's section (40-details.js): its scanner, the restored view that reads the item again, the user
// change that unmounts it (and removes the drafts of a member who signed out), and the health check of the
// anchors it needs on each viewshow of the details page (10-selectors.js). An open review
// form's draft is written at once when the page is hidden or left. Drafts older than 30 days go
// at start, a member's drafts go when the kit says their session expired, and the page shown again reads the item again
// while the member was told to play it first or that rating is limited to selected members. The chip and the
// Rate button (50-chip.js): their scanner after the section's, so they see the section as it is after the scan, and the
// kit's TV mitigation of Jellyfin Enhanced's closed Reviews (kit.je.tvSectionJump), registered after both, so its own
// scan runs last.

purgeExpiredDrafts();
kit.observe(syncDetailsSection);
kit.observe(syncDetailsExtras);
registerReviewsTvFix();
kit.onView(onDetailsViewShow);
kit.onView(checkRatingsAnchors);
kit.user.onChange(onDetailsUserChange);
window.addEventListener('pagehide', flushDetailsDraft);
document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'hidden') {
        flushDetailsDraft();
    } else if (document.visibilityState === 'visible') {
        refreshDetailsOnReturn();
    }
});
document.addEventListener(kit.prefix + ':session-expired', onDetailsSessionExpired);
kit.start();
