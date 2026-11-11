// Every user-visible string of User Ratings' web client (00-strings.js): the kit's t() table, read with
// kit.t(key, { name: value }), which fills each {name} (a {name} it has no value for stays, so the page can put an
// isolated node there: a library's name). It holds the star widget's (30-stars.js), the details page's section's
// (40-details.js, with the read-only states' notes, spoilers, the 409's Reload and Overwrite, drafts), and the chip's and
// the Rate button's (50-chip.js). The texts that explain why a member can't rate (Can.Reason) are the details page's:
// the widget shows the note it is given and decides nothing.

kit.addStrings({
    // The slider's accessible name when the page gives it no label of its own (aria-labelledby "Your rating"),
    // and the details page's label beside it.
    'stars.yourRating': 'Your rating',
    // The value with no rating (aria-valuetext, and the text beside the stars).
    'stars.notRated': 'Not rated',
    // aria-valuetext of a value ("3.5 out of 5 stars").
    'stars.valueText': '{stars} out of 5 stars',
    // The read-only stars' accessible name (role="img").
    'stars.rated': 'Rated {stars} out of 5',
    // Beside the stars while the page saves the value ("Saving.").
    'stars.saving': 'Saving…',
    // The live region once a save went through.
    'stars.saved': 'Saved: {stars} stars',
    'stars.savedOne': 'Saved: 1 star',
    // The − and + buttons beside the stars at (pointer: coarse).
    'stars.less': 'Half a star less',
    'stars.more': 'Half a star more',

    // The details page's section. Its title, a disclosure button.
    'details.title': 'Member ratings & reviews',
    // The summary: no ratings, "Be the first." only for a member who may rate; none on this copy while
    // another copy has some; fewer than MinRatingsForAverage.
    'details.noneFirst': 'No member ratings yet. Be the first.',
    'details.none': 'No member ratings yet.',
    'details.noneHere': 'No member ratings on this copy yet.',
    'details.few': 'Not enough ratings yet',
    // The average's accessible name ("Member average 4.2 out of 5, 7 ratings").
    'details.averageLabel': 'Member average {average} out of 5, {count} ratings',
    'details.averageLabelOne': 'Member average {average} out of 5, 1 rating',
    'details.ratings': '{count} ratings',
    'details.ratingsOne': '1 rating',
    'details.reviews': '{count} reviews',
    'details.reviewsOne': '1 review',
    // Between the parts of a line ("7 ratings · 3 reviews").
    'details.separator': ' · ',
    // The histogram's accessible name, its non-empty buckets from the highest.
    'details.histogramLabel': 'How members rated: {list}',
    'details.histogramItem': '{count} at {stars} stars',
    'details.histogramOne': '{count} at 1 star',
    // The "rated on another copy" hint: {library} is the copy's library, set as an isolated name.
    'details.copy': 'Also rated on another copy ({library}): {average}/5 ({count})',
    'details.copyUnnamed': 'Also rated on another copy: {average}/5 ({count})',
    'details.copyCount': 'Also rated on another copy ({library}): {ratings}',
    'details.copyCountUnnamed': 'Also rated on another copy: {ratings}',
    'details.copyYours': 'yours {stars}/5',
    // The hint's link, which names what it opens ({library} as an isolated name).
    'details.openCopy': 'Open the copy in {library}',
    'details.openCopyUnnamed': 'Open the other copy',
    // The member's own rating and review.
    'details.clear': 'Clear',
    'details.write': 'Write a review',
    'details.yourReview': 'Your review',
    'details.edit': 'Edit',
    'details.delete': 'Delete',
    'details.counter': '{count} / {max}',
    'details.save': 'Save',
    'details.saving': 'Saving…',
    'details.cancel': 'Cancel',
    'details.retry': 'Try again',
    'details.reviewSaved': 'Review saved',
    'details.cleared': 'Rating cleared',
    'details.deleted': 'Your rating and review were deleted',
    // Clear on a reviewed row, and Delete.
    'details.clearTitle': 'Keep the review without stars?',
    'details.clearMessage': 'Your stars are cleared and your review stays.',
    'details.clearConfirm': 'Clear the stars',
    'details.deleteTitle': 'Delete your rating and review?',
    'details.deleteMessage': 'This can\'t be undone.',
    'details.deleteConfirm': 'Delete',
    // A write the server didn't take, with its sentence.
    'details.ratingNotSaved': 'Your rating wasn\'t saved. {reason}',
    'details.reviewNotSaved': 'Your review wasn\'t saved. {reason}',
    'details.clearFailed': 'Your rating wasn\'t cleared. {reason}',
    'details.deleteFailed': 'Your rating and review weren\'t deleted. {reason}',
    'details.refused': 'This can\'t be changed now.',
    // The review form: another window changed the review since the form opened; the member
    // may no longer write (the text stays, read-only, until the member closes the form).
    'details.changedNotice': 'Your review was changed in another window after you opened it here. Your text here is kept.',
    'details.formLocked': 'Your review can\'t be saved now. Your text stays here so you can copy it.',
    'details.close': 'Close',
    // A change waiting to be saved when the signed-in member changed: dropped, never sent as the next member.
    'details.notSavedUserChanged': 'A rating or review wasn\'t saved: the member who made it is no longer signed in.',
    'details.loadFailed': 'Member ratings couldn\'t be loaded. {reason}',
    'details.moreFailed': 'More reviews couldn\'t be loaded. {reason}',
    // The other members' rows: their list, its order, Load more, and Load more's 403.
    'details.listLabel': 'Member reviews',
    'details.sort': 'Sort',
    'details.sortNewest': 'Newest',
    'details.sortOldest': 'Oldest',
    'details.sortHighest': 'Highest',
    'details.sortLowest': 'Lowest',
    'details.loadMore': 'Load more',
    'details.loading': 'Loading…',
    'details.reviewsOff': 'Reviews are turned off on this server.',
    'details.showMore': 'Show more',
    'details.showLess': 'Show less',
    'details.edited': '(edited)',
    'details.formerMember': 'Former member',
    // A review its author marked as a spoiler: its text out of the page until asked for, a blur in its place;
    // the form's flag.
    'details.spoiler': 'Contains spoilers',
    'details.showSpoiler': 'Show spoiler',
    'details.hideSpoiler': 'Hide spoiler',
    'details.spoilerCheck': 'Contains spoilers',
    // Why the member's own rating can't be changed (Can.Reason): beside NotARater's read-only stars, and
    // the disabled slider's description for the others.
    'details.noteNotARater': 'Rating is limited to selected members.',
    'details.noteNeedsPlay': 'You can rate this once you have played it.',
    'details.noteReadOnly': 'Ratings can\'t be changed right now.',
    'details.noteUnknown': 'Rating isn\'t available right now.',
    // The 409: the review was changed in another window.
    'details.conflict': 'Your review was changed in another window, so this text wasn\'t saved. Reload shows that review and keeps your text beside it; Overwrite saves your text in its place.',
    'details.reload': 'Reload',
    'details.overwrite': 'Overwrite',
    'details.reloaded': 'This is your review as it was saved in the other window. Your text is kept below it.',
    'details.reloadedNone': 'Your review was deleted in the other window. Your text is kept below.',
    'details.reloadFailed': 'Your review couldn\'t be read again. {reason}',
    // The 409's panel: the review as it is now, read-only, before Overwrite replaces it.
    'details.conflictReading': 'Reading your review as it is now…',
    'details.conflictNewer': 'Your review as it is now:',
    'details.conflictDeleted': 'Your review was deleted in the other window.',
    'details.conflictUnread': 'Your review as it is now couldn\'t be read. {reason}',
    'details.asideMine': 'Your text, not saved',
    'details.asideTheirs': 'Your review as saved in the other window',
    'details.useMine': 'Use my text',
    'details.useTheirs': 'Use that text',
    // Drafts: the member's unsaved text, kept on this device.
    'details.draftRestored': 'Your unsaved text was put back.',
    'details.discard': 'Discard',
    'details.draftChanged': 'Your review was changed in another window after this text was written.',
    'details.writeDraft': 'Continue your review',
    'details.editDraft': 'Continue editing',
    'details.draftKept': 'Your unsaved review is kept on this device for the next time you open this item.',
    'details.reviewNotSavedKept': 'Your review wasn\'t saved. {reason} Your text is kept on this device.',
    // The other kept texts after a Save, and a Cancel this browser can't keep.
    'details.reviewSavedKept': 'Review saved. The other texts you kept are still beside the form.',
    'details.cancelNotKept': 'This browser can\'t keep your unsaved text, so the form stays open. Save it, or press Cancel again to close the form and lose that text.',

    // An admin's "Remove…" on another member's row, never shown to anyone else: a dialog with
    // "Remove text only" (the default, the stars stay) or "Remove rating and review", an optional reason, and what the
    // server's dry run says will be removed before Remove is pressed.
    'moderate.remove': 'Remove…',
    'moderate.removeLabel': 'Remove the review of {name}…',
    'moderate.title': 'Remove a review',
    'moderate.by': 'By {name}',
    'moderate.what': 'What to remove',
    'moderate.textOnly': 'Remove text only',
    'moderate.textOnlyHint': 'The review goes and the stars stay.',
    'moderate.entry': 'Remove rating and review',
    'moderate.entryHint': 'The stars and the review go, and the rating can\'t come back from an import.',
    'moderate.reason': 'Reason (optional)',
    'moderate.reasonNone': 'No reason given',
    'moderate.reasonSpoiler': 'Spoiler',
    'moderate.reasonOffensive': 'Offensive',
    'moderate.reasonSpam': 'Spam',
    'moderate.reasonOffTopic': 'Off topic',
    'moderate.reasonRequested': 'The member asked for it',
    'moderate.reasonOther': 'Other',
    'moderate.checking': 'Checking what will be removed…',
    'moderate.previewText': 'The review goes; the stars stay ({stars}).',
    'moderate.previewTextDeletes': 'The review goes, and with it the rating, which has no stars.',
    'moderate.previewEntry': 'The rating and the review go.',
    'moderate.previewLog': 'The moderation log keeps who removed it and why, never the text.',
    'moderate.previewFailed': 'What would be removed couldn\'t be checked. {reason}',
    'moderate.noReview': 'This rating has no review any more; Remove rating and review still removes its stars.',
    'moderate.gone': 'This review was already removed, or you can\'t remove it.',
    'moderate.changed': 'This review changed after it was checked, so nothing was removed. It is shown below as it is now; Remove removes what is checked for it now.',
    'moderate.changedGone': 'This review was moved or removed after it was checked, so nothing was removed.',
    'moderate.reloading': 'Reading this review again…',
    'moderate.now': 'Now: {stars}',
    'moderate.nowNoStars': 'Now: no stars',
    'moderate.nowNoReview': 'No review now.',
    'moderate.confirm': 'Remove',
    'moderate.removing': 'Removing…',
    'moderate.failed': 'Nothing was removed. {reason}',
    'moderate.doneText': 'The review was removed. The stars stay.',
    'moderate.doneEntry': 'The rating and the review were removed.',

    // The member-average chip at the end of the media-info row: the word, hidden below 600 px, and the
    // value, after the group icon; its accessible name and its title are details.averageLabel's.
    'chip.word': 'Members',
    'chip.value': '{average}/5 ({count})',
    // The Rate button in the details buttons row (ShowDetailButton): its title and accessible name.
    'rate.button': 'Rate'
});
