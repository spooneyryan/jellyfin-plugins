// User Ratings' dashboard module, the settings page's Data tab, which
// configPage.html mounts twice: { tab: 'Data' } at the top of the tab, { tab: 'Detached' } in its "Detached & ambiguous"
// section (90-mount.js).
// - Data: the database's integrity (damaged, read-only, the last recovery, quarantine folders) as a banner; Export, a job
//   whose file is fetched with the session's authorization and saved; Import, a file read as it is, Merge or Replace,
//   users by id or by name: a dry run first, then Import with the dry run's PlanToken after a question (a plan changed
//   since is a 409 that imports nothing); Stop while the job reads, never once it writes; the snapshots with Restore…;
//   the unknown users of Diagnostics with Remap by name… (its dry run, then its PlanToken) and Apply the deleted-user
//   policy….
// - Jellyfin Enhanced: its reviews.json imported as a data job (Copies, unmatched titles, purged ratings,
//   the UserMap of its members who aren't users here), a dry run first and Import with its PlanToken, as the export's
//   import; the report's counts, notes, field names and first reviews (never their text); then the switch-over checklist
//   of every JE review setting from Diagnostics (read only: the admin changes them in JE) and "Use the same hiding rules",
//   which saves User Ratings' own two settings through the Settings route's dry run and save.
// - Detached: the detached ratings, oldest first, 50 a page, by reason, each with up to five titles it could go back to
//   (Relink) and Purge…, which deletes it for good as a deletion does (a tombstone, its archive lines, a snapshot first).
// Every value from the server is text (kit.h's text, never HTML), every name and title isolated (dir="auto"). The
// questions are the kit's dialog (scopedKeys: the TV layout's D-pad moves through it, Back closes it), Cancel focused, the
// focus back on the button that asked; there is no <select>, so the TV layout's D-pad and OK work everywhere; the controls
// are 44 px high on a phone and wrap at 375 px. A refusal without one of the codes below (a 409 of read-only data, a
// 503) shows the server's words and Try again (a 409 ReadOnly and a 503 alike).

const DATA_POLL_MS = 1000;
const DATA_MAX_IMPORT_BYTES = 100 * 1024 * 1024;
const DATA_DETACHED_PAGE = 50;
const DATA_REASONS = ['Removed', 'Missing', 'Unmatched', 'Ambiguous'];
// The import's outcomes (Import/RatingsEnvelope), in the report's order.
const DATA_OUTCOMES = ['Added', 'StoredDetached', 'Updated', 'Unchanged', 'KeptNewer', 'SkippedDeleted', 'PreviouslyPurged', 'SkippedUnknownUser', 'SkippedDeletedUser', 'TombstoneAdded', 'TombstoneDeleted', 'TombstoneKeptNewer', 'TombstoneKnown'];
// The Jellyfin Enhanced import's counts (Import/JellyfinEnhancedImport), in the report's order; a dry run has Created and
// Updated as WouldCreate and WouldUpdate.
const DATA_JE_COUNTS = ['Created', 'Updated', 'Matched', 'MatchedMultiple', 'Unmatched', 'StoredDetached', 'UnknownUser', 'Empty', 'Invalid', 'Adjusted', 'AlreadyImported', 'AlreadyImportedArchived', 'SkippedNewer', 'SkippedDeleted', 'PreviouslyPurged', 'ConflictKeptLocal', 'ConflictTookJe', 'DuplicateInSource', 'KeyMismatch', 'Clamped', 'HiddenUserReviews', 'DisabledUserReviews'];
const DATA_JE_JOB = 'ImportJellyfinEnhanced';
const DATA_OPERATIONS = ['import', 'restore', 'purge', 'moderate', 'user-deleted', 'apply-policy', 'remap', 'migrate', 'other'];

kit.addStrings({
    'data.allLibraries': 'Export, import, restore and the unknown users\' tools cover every library\'s ratings, so they are for administrators with access to every library. To use them: Dashboard → Users → your account → Access, tick "Enable access to all libraries", then Save, then reload this tab. If an Access Roles role manages this account\'s libraries, the role must give all libraries.',
    'data.integrity': 'The ratings database',
    'data.damaged': 'The ratings database is damaged, so ratings can\'t be changed. Restore a snapshot below to repair it. {message}',
    'data.readOnly': 'The ratings database is read-only. {message}',
    'data.recovered': 'The ratings database was restored from {snapshot} on {when} after a problem ({reason}); changes from the {lost} before it were lost.',
    'data.recoveredEmpty': 'The ratings database was started empty on {when} after a problem ({reason}).',
    'data.quarantines': 'Damaged copies are kept in the plugin\'s data folder until you delete them: {names}.',
    'data.export': 'Export',
    'data.exportTitle': 'Export',
    'data.exportIntro': 'Jellyfin\'s own backups leave plugin data out. An export saves every rating, review, deletion, former member, the import record, the moderation log and the archived ratings in one file, to import here or on another server. It holds every member\'s reviews: keep it safe. The server deletes its own copy within an hour.',
    'data.exporting': 'Exporting…',
    'data.exported': 'Exported {ratings} ratings ({reviews} with a review, {detached} detached) and {archived} archived ones as {file}.',
    'data.download': 'Download the export again',
    'data.importTitle': 'Import an export',
    'data.importIntro': 'Read an export back. Merge adds the file\'s ratings and updates older copies here; Replace makes the ratings the file\'s. A rating deleted here or in the file never comes back, and a deleted member\'s ratings are never made again. A snapshot is taken first.',
    'data.file': 'Export file',
    'data.mode': 'How',
    'data.mode.Merge': 'Merge',
    'data.mode.Replace': 'Replace',
    'data.match': 'Match members',
    'data.match.Id': 'By id (this server)',
    'data.match.Name': 'By name (another server)',
    'data.dryRun': 'Dry run',
    'data.import': 'Import',
    'data.stop': 'Stop',
    'data.stopping': 'Stopping…',
    'data.chooseFile': 'Choose an export file, then run the dry run.',
    'data.needDryRun': 'Run the dry run of this file and these options first; Import then does what it showed.',
    'data.tooBig': 'The file is larger than 100 MB, so it can\'t be imported.',
    'data.unreadableFile': 'The file couldn\'t be read. {reason}',
    'data.phase.Queued': 'Waiting to start…',
    'data.phase.Read': 'Reading…',
    'data.phase.Resolve': 'Matching titles: {processed} of {total}',
    'data.phase.Write': 'Writing {total} ratings…',
    'data.phase.Archive': 'Importing archived ratings…',
    'data.phase.Other': 'Working…',
    'data.cancelled': 'Stopped. Nothing was imported.',
    'data.writing': 'It is writing now, so it can\'t be stopped; it finishes by itself.',
    'data.busy': 'Another data job is running. Wait for it to finish, or stop it.',
    'data.changed': 'The ratings changed after the dry run, so nothing was imported. Run the dry run again to see what Import does now.',
    'data.failed': 'It didn\'t work. {reason}',
    'data.refused': 'The server refused it.',
    'data.retry': 'Try again',
    'data.dry': 'Dry run, nothing was changed.',
    'data.done': 'Imported.',
    'data.fileIs': 'The file is an export of User Ratings {version} from {when}, with {count} ratings.',
    'data.replaceNote': 'Replace removes every rating and archived rating here first; the deletions and former members recorded here stay.',
    'data.outcome.Added': 'New ratings',
    'data.outcome.StoredDetached': 'New, kept detached (no title here matches)',
    'data.outcome.Updated': 'Updated (newer in the file)',
    'data.outcome.Unchanged': 'Unchanged',
    'data.outcome.KeptNewer': 'Kept as they are here (newer here)',
    'data.outcome.SkippedDeleted': 'Skipped: deleted here or in the file',
    'data.outcome.PreviouslyPurged': 'Skipped: purged here by an administrator',
    'data.outcome.SkippedUnknownUser': 'Skipped: their member isn\'t on this server',
    'data.outcome.SkippedDeletedUser': 'Skipped: their member was deleted on this server',
    'data.outcome.TombstoneAdded': 'Deletions recorded',
    'data.outcome.TombstoneDeleted': 'Ratings here deleted, as the file deleted them',
    'data.outcome.TombstoneKeptNewer': 'Ratings here kept, changed after the file deleted them',
    'data.outcome.TombstoneKnown': 'Deletions already recorded here',
    'data.unusable': 'Rows of the file that can\'t be imported, left out: {count}',
    'data.unusableRow': '{table}, row {row}: {reason}',
    'data.unusableRowId': '{table}, row {row} ({id}): {reason}',
    'data.unusableMore': 'And {count} more.',
    'data.usersTitle': 'Members in the file',
    'data.userFound': '{name}: found here ({count} ratings)',
    'data.userUnknown': '{name}: not found here ({count} ratings)',
    'data.archive': 'Archived ratings in the file: {records}; imported: {imported}.',
    'data.safety': 'The ratings from just before were kept as {name} (Snapshots, below).',
    'data.confirmMerge': 'Import the file\'s ratings as the dry run showed? A snapshot of the ratings as they are now is taken first.',
    'data.confirmReplace': 'Replace every rating on this server with the file\'s? Ratings that aren\'t in the file are removed. A snapshot of the ratings as they are now is taken first.',
    'data.confirmImportTitle': 'Import ratings',
    'data.snapshotsTitle': 'Snapshots',
    'data.snapshotsIntro': 'Copies of the ratings database: one a day, and one before each import, purge, restore, removal or deleted-user change. Restore puts a copy back after a snapshot of the ratings as they are now; a copy from before an operation brings back that operation\'s archive too. A copy from before a removal, a purge or a deleted-user change doesn\'t undo it: deletions made on this server stay deleted when a snapshot is restored. A copy from before an import undoes the import, its deletions too.',
    'data.snapshotsEmpty': 'No snapshots yet.',
    'data.daily': 'Daily',
    'data.before': 'Before {operation}',
    'data.op.import': 'an import',
    'data.op.restore': 'a restore',
    'data.op.purge': 'a purge',
    'data.op.moderate': 'a removal',
    'data.op.user-deleted': 'a deleted member\'s ratings were changed',
    'data.op.apply-policy': 'the deleted-user policy',
    'data.op.remap': 'Remap by name',
    'data.op.migrate': 'an upgrade',
    'data.op.other': 'an operation',
    'data.kept': 'kept',
    'data.size': '{size} KB',
    'data.restore': 'Restore…',
    'data.restoreLabel': 'Restore {name}…',
    'data.restoreTitle': 'Restore a snapshot',
    'data.restoreQuestion': 'Put {name} from {when} back? Every change made since is undone, imports with their deletions too, except deletions made on this server, which stay deleted. A snapshot of the ratings as they are now is taken first.',
    'data.restoreAfterEmpty': 'Put {name} from {when} back? Every change made since is undone. The ratings were started empty on {emptied}, after it: deletions made between the two can\'t be told and may come back; deletions made on this server since the start empty stay deleted. A snapshot of the ratings as they are now is taken first.',
    'data.restoreDamaged': 'Put {name} from {when} back to repair the database? Changes made after it are lost, deletions too: the damaged database can\'t tell them.',
    'data.restoreConfirm': 'Restore',
    'data.restoring': 'Restoring…',
    'data.startEmpty': 'Start empty…',
    'data.startEmptyIntro': 'If no snapshot repairs it, start empty, then import an export if you have one. The damaged database and every snapshot are kept.',
    'data.startEmptyTitle': 'Start empty',
    'data.startEmptyQuestion': 'Start the ratings empty? Every rating and review is gone until a snapshot is restored or an export imported. The damaged database is moved to a quarantine folder and kept, and so is every snapshot. If a snapshot can repair it, restore that instead.',
    'data.startEmptyConfirm': 'Start empty',
    'data.startingEmpty': 'Starting empty…',
    'data.startedEmpty': 'The ratings started empty.',
    'data.startedEmptyKept': 'The damaged database is kept in {folder}.',
    'data.restored': 'Restored {name}.',
    'data.restoredArchive': 'The archive\'s copy from that operation was put back too.',
    'data.restoredArchiveFailed': 'The archive\'s copy from that operation couldn\'t be put back, so the archive is as it was: {reason}',
    'data.restoredKept': 'Deletions made on this server since stayed: {ratings} deleted ratings, {reviews} removed reviews, {former} former members.',
    'data.restoredImports': 'Imports made since were undone, their deletions too: {ratings} deleted ratings and {former} former members came back.',
    'data.restoredKeptFailed': '{reason}',
    'data.usersSectionTitle': 'Unknown users',
    'data.usersIntro': 'Ratings whose member isn\'t a user of this server, as a restore or an import from another server can leave them. Remap by name gives them to the user with the same name; Apply the deleted-user policy treats them as a deleted member\'s ({policy}).',
    'data.usersNone': 'Every rating belongs to a user of this server.',
    'data.userRow': '{name}: {count} ratings',
    'data.unknownName': 'An unknown user ({id})',
    'data.policyPending': 'the policy is pending',
    'data.remap': 'Remap by name…',
    'data.remapTitle': 'Remap by name',
    'data.remapChecking': 'Checking which users match by name…',
    'data.remapNone': 'No unknown user has the name of a user of this server, so there is nothing to remap.',
    'data.remapPair': '{name}: {count} ratings to the user of this server with that name',
    'data.remapUnmatched': '{name}: no match ({reason})',
    'data.remapAmbiguous': 'Ratings left detached as ambiguous, because both ids rated the title: {count}',
    'data.remapArchived': 'Archived ratings moved too: {count}',
    'data.remapConfirm': 'Remap',
    'data.remapped': 'Remapped: {count} ratings moved.',
    'data.policy': 'Apply the policy…',
    'data.policyAll': 'Apply the deleted-user policy to every unknown user…',
    'data.policyTitle': 'Apply the deleted-user policy',
    'data.policyQuestion': 'Apply {policy} to the ratings of {count} unknown users? A snapshot is taken first.',
    'data.policyConfirm': 'Apply',
    'data.policyDone': 'The deleted-user policy was applied: {count} ratings affected.',
    'data.policy.Delete': 'Delete (the ratings are deleted for good)',
    'data.policy.Anonymize': 'Anonymize (kept as a former member\'s)',
    'data.policy.KeepAsOrphan': 'Keep as they are',
    'data.cancel': 'Cancel',
    'data.loading': 'Loading…',
    'data.loadFailed': 'This couldn\'t be loaded. {reason}',
    'je.title': 'Import from Jellyfin Enhanced',
    'je.intro': 'Jellyfin Enhanced\'s user reviews become ratings and reviews here: its stars (halves too) and its text, each under its member and title. Jellyfin Enhanced\'s files and settings are only read, never changed. A dry run shows what Import does; a snapshot is taken first, and restoring it (Snapshots, below) undoes the import. Import first, then switch Jellyfin Enhanced\'s reviews off with the checklist below.',
    'je.copies': 'A title in more than one library',
    'je.copies.First': 'Rate one copy (the one its member can see)',
    'je.copies.All': 'Rate every copy',
    'je.unmatched': 'Reviews of titles that aren\'t here',
    'je.unmatched.Keep': 'Keep them detached',
    'je.unmatched.Skip': 'Leave them out',
    'je.purged': 'Reviews whose rating was purged here',
    'je.purged.Skip': 'Leave them out',
    'je.purged.Make': 'Make them again',
    'je.needDryRun': 'Run the dry run with these options first; Import then does what it showed.',
    'je.noData': 'Jellyfin Enhanced has no reviews on this server: its reviews.json isn\'t there.',
    'je.format': 'Jellyfin Enhanced\'s reviews.json isn\'t in a shape this version reads, so nothing was imported. {reason}',
    'je.dry': 'Dry run, nothing was changed.',
    'je.done': 'Imported.',
    'je.total': 'Jellyfin Enhanced\'s file has {count} reviews.',
    'je.total.one': 'Jellyfin Enhanced\'s file has 1 review.',
    'je.count.Created': 'New ratings',
    'je.count.Updated': 'Updated (newer in Jellyfin Enhanced)',
    'je.count.Matched': 'Matched to a title here',
    'je.count.MatchedMultiple': 'Matched to more than one copy',
    'je.count.Unmatched': 'No title here matches',
    'je.count.StoredDetached': 'Kept detached (no title here matches)',
    'je.count.UnknownUser': 'Skipped: their member isn\'t a user here',
    'je.count.Empty': 'Skipped: no stars and no text',
    'je.count.Invalid': 'Left out: can\'t be read',
    'je.count.Adjusted': 'Stars rounded to a half star',
    'je.count.AlreadyImported': 'Already imported',
    'je.count.AlreadyImportedArchived': 'Already imported, archived here',
    'je.count.SkippedNewer': 'Skipped: changed here since it was imported',
    'je.count.SkippedDeleted': 'Skipped: deleted here',
    'je.count.PreviouslyPurged': 'Skipped: purged here by an administrator',
    'je.count.ConflictKeptLocal': 'The member\'s own rating here kept (it is newer)',
    'je.count.ConflictTookJe': 'The member\'s own rating here replaced (it is older)',
    'je.count.DuplicateInSource': 'Skipped: the same member\'s review of the same title is newer',
    'je.count.KeyMismatch': 'Entries whose key and content disagree (the content was used)',
    'je.count.Clamped': 'Dated ahead of this server\'s clock',
    'je.count.HiddenUserReviews': 'By users hidden from the sign-in screen',
    'je.count.DisabledUserReviews': 'By disabled users',
    'je.fields': 'Fields in Jellyfin Enhanced\'s file: {fields}',
    'je.leftOut': 'Left out',
    'je.leftOutRow': '{key}: {reason}',
    'je.samples': 'The first reviews, as Import treats them',
    'je.sample': '{user} · {item} · {stars} · {review} · {outcome}',
    'je.sample.noUser': '{item} · {stars} · {review} · {outcome}',
    'je.sample.bare': '{user} · {item} · {outcome}',
    'je.sample.bare.noUser': '{item} · {outcome}',
    'je.sampleEntry': 'Jellyfin Enhanced\'s entry {key}',
    'je.withReview': 'with a review',
    'je.starsOnly': 'stars only',
    'je.alsoMatched': '{count} more copies match',
    'je.alsoMatched.one': '1 more copy matches',
    'je.unknownTitle': 'Members of Jellyfin Enhanced who aren\'t users here',
    'je.unknownIntro': 'Give each one\'s reviews to a user of this server, or leave them out; then run the dry run again.',
    'je.unknownUser': 'Jellyfin Enhanced user {id}: {count} reviews',
    'je.unknownUser.one': 'Jellyfin Enhanced user {id}: 1 review',
    'je.leaveOut': 'Leave out',
    'je.usersFailed': 'The server\'s users couldn\'t be read, so these reviews can only be left out. {reason}',
    'je.confirmTitle': 'Import Jellyfin Enhanced\'s reviews',
    'je.confirm': 'Import Jellyfin Enhanced\'s reviews as the dry run showed? A snapshot of the ratings as they are now is taken first; restoring it undoes the import.',
    'je.switchTitle': 'Switch Jellyfin Enhanced\'s reviews off',
    'je.switchIntro': 'After the import, change these in Jellyfin Enhanced\'s own settings (Dashboard, Plugins, Jellyfin Enhanced). User Ratings never changes them; the list is read again each time the tab is shown.',
    'je.notInstalled': 'Jellyfin Enhanced isn\'t installed, so there is nothing to switch off.',
    'je.complete': 'Every required step is done: Jellyfin Enhanced\'s user reviews are off.',
    'je.incomplete': 'Required steps still to do: {count}.',
    'je.state.done': 'Done',
    'je.state.todo': 'To do',
    'je.state.optional': 'Optional',
    'je.state.unknown': 'Unknown',
    'je.unreadable': 'Couldn\'t read Jellyfin Enhanced\'s settings, so whether its user reviews are off can\'t be told: every step shows Unknown. Try again in a moment; if it stays, open Jellyfin Enhanced\'s settings page and save them again.',
    'je.problems': 'Jellyfin Enhanced\'s files that couldn\'t be read (the server log has the details):',
    'je.hidingUnknownIntro': 'Jellyfin Enhanced\'s hiding rules can\'t be read while its settings can\'t be.',
    'je.hidingUnreadable': 'Jellyfin Enhanced\'s settings couldn\'t be read, so its hiding rules aren\'t known: nothing was changed. Try again in a moment.',
    'je.hiding': 'Use the same hiding rules',
    'je.backTitle': 'Going back to Jellyfin Enhanced',
    'je.backIntro': 'Export for Jellyfin Enhanced saves the ratings and reviews of films, series, seasons and episodes with a TMDb id as Jellyfin Enhanced\'s own reviews.json. Nothing is written on the server. To go back: run the import above once more first (a dry run, then Import), so reviews written in Jellyfin Enhanced since come here too; export; stop the server, keep a copy of Jellyfin Enhanced\'s reviews.json (in plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced), put this file in its place, start the server and turn Jellyfin Enhanced\'s user reviews on again.',
    'je.backPrivacy': 'Library access doesn\'t carry over: Jellyfin Enhanced gives every review of a title to any signed-in user who asks for it, whatever libraries they can open, from the moment the file is in place. Check its two hiding rules (above) while the server still runs, before you stop it to put the file in place.',
    'je.restricted': 'Titles some members can\'t see',
    'je.restricted.Leave': 'Leave out reviews of titles that not every member can see',
    'je.restricted.Include': 'Include them (every signed-in user can read them in Jellyfin Enhanced)',
    'je.export': 'Export for Jellyfin Enhanced',
    'je.exporting': 'Exporting…',
    'je.exported': 'Saved {file}: {count} reviews.',
    'je.exported.one': 'Saved {file}: 1 review.',
    'je.exportSkipped': 'Left out, {count} ratings: {reasons}.',
    'je.exportSkipped.one': 'Left out, 1 rating: {reasons}.',
    'je.exportMerged': '{count} ratings of another copy of a title went into the same review (the newer one is saved).',
    'je.exportMerged.one': '1 rating of another copy of a title went into the same review (the newer one is saved).',
    'je.skip.NoTmdbId': 'no TMDb id ({count})',
    'je.skip.NotInJe': 'a kind Jellyfin Enhanced doesn\'t review, such as music or books ({count})',
    'je.skip.FormerMember': 'a former member\'s ({count})',
    'je.skip.Detached': 'detached and not from Jellyfin Enhanced ({count})',
    'je.skip.Archived': 'archived and not from Jellyfin Enhanced ({count})',
    'je.skip.RestrictedLibrary': 'a title not every member can see ({count})',
    'je.exportOverLimit': '{count} reviews are longer than the 2,000 characters Jellyfin Enhanced saves: it shows them whole, but one edited there must be shortened before it saves.',
    'je.exportOverLimit.one': '1 review is longer than the 2,000 characters Jellyfin Enhanced saves: it shows it whole, but edited there it must be shortened before it saves.',
    'je.hidingIntro': 'Jellyfin Enhanced leaves out the reviews of hidden users: {hidden}; of disabled users: {disabled}. User Ratings has its own two settings for this (the Settings tab); this sets them the same, after showing the change.',
    'je.yes': 'yes',
    'je.no': 'no',
    'je.hidingSame': 'User Ratings already leaves out the same reviews as Jellyfin Enhanced.',
    'je.hidingQuestion': 'Change User Ratings\' settings so it leaves out the same reviews as Jellyfin Enhanced?',
    'je.hidingSave': 'Save',
    'je.hidingSaved': 'Saved: User Ratings leaves out the same reviews as Jellyfin Enhanced.',
    'det.intro': 'The detached ratings, the oldest first. Relink gives a rating back to a title its member can open; Purge deletes it for good, as a deletion does (a snapshot is taken first).',
    'det.reasonAll': 'Every reason',
    'det.reason': 'Why',
    'det.empty': 'No detached ratings.',
    'det.emptyReason': 'No detached ratings for this reason.',
    'det.by': 'By {name}',
    'det.unknownMember': 'Deleted user',
    'det.when': 'Detached {when}: {reason}',
    'det.review': 'with a review',
    'det.noReview': 'stars only',
    'det.candidates': 'Titles it could go back to',
    'det.noCandidates': 'No title in the library matches.',
    'det.strength.Exact': 'the same title',
    'det.strength.Strong': 'the same provider id',
    'det.strength.Weak': 'the same name and year',
    'det.strength.None': 'no match',
    'det.ratesIt': 'This member already rates it.',
    'det.relink': 'Relink',
    'det.relinkLabel': 'Relink the rating of {name} to {item}',
    'det.relinkTitle': 'Relink a rating',
    'det.relinkQuestion': 'Give the rating of {name} on {from} to {to}?',
    'det.relinked': 'The rating was relinked.',
    'det.purge': 'Purge…',
    'det.purgeLabel': 'Purge the rating of {name} on {item}…',
    'det.purgeTitle': 'Purge a rating',
    'det.purgeQuestion': 'Delete the rating of {name} on {item} for good? It can\'t come back from an import; a snapshot is taken first.',
    'det.purgeConfirm': 'Purge',
    'det.purged': 'The rating was purged.',
    'det.gone': 'This rating isn\'t detached any more, or you can\'t change it. The list is read again.',
    'det.alreadyRated': 'The member already rates that title, so nothing was changed. Purge this rating, or remove theirs first.',
    'det.notRelinkable': 'That title can\'t take the rating. {reason}'
});

// ------------------------------------------------------------------------------------------------- helpers

// The key of a text with a count: its ".one" form for 1 ("1 review"), else the key itself ("{count} reviews").
function dataPlural(key, count) {
    return count === 1 ? key + '.one' : key;
}

// Whether an element is laid out right to left (its computed direction).
function dataRtl(el) {
    try {
        return typeof window.getComputedStyle === 'function' && window.getComputedStyle(el).direction === 'rtl';
    } catch (e) {
        return false;
    }
}

// Disables a button while its request runs, and answers the function that puts the focus back on it afterwards, when it
// had the focus and nothing else took it since (0.3.24): a disabled button loses the
// focus to the page's body, so a keyboard or D-pad user was left at the top of the page after Export. Called once the
// button is enabled again; while it stays disabled (another job running) the focus is left alone.
function dataBusy(button) {
    const had = document.activeElement === button;
    button.disabled = true;
    return function () {
        const now = document.activeElement;
        if (had && !button.disabled && (!now || now === button || now === document.body || now === document.documentElement)) {
            button.focus();
        }
    };
}

// A group of choice buttons (role="radio"), as the Settings tab has them: no <select>, so the TV layout's D-pad and OK
// work. textOf(value) gives a choice's words; onChange(value) runs when another is chosen. The keys of a radio group
// (the APG's pattern): the group is one Tab stop, its chosen button's (tabindex 0, the
// others -1); Right and Down choose the next, Left and Up the one before, round the end, Left and Right as they point in a
// right-to-left page. On the TV layout only Left and Right, so the D-pad's Up and Down leave the group (as the stars
// slider does); a key it handles is prevented, so jellyfin-web's window handler never moves the focus for it too.
function dataChoices(id, labelKey, values, textOf, value, onChange) {
    const label = kit.h('p', { id: id + '-label', class: 'jfur-data-label', text: kit.t(labelKey) });
    const choose = function (v) {
        if (group.value !== v) {
            set(v);
            onChange(v);
        }
    };
    const buttons = values.map(function (v) {
        const button = kit.h('button', { type: 'button', role: 'radio', class: 'jfur-data-choice', 'data-jfur-value': v, text: textOf(v) });
        button.addEventListener('click', function () {
            choose(v);
        });
        button.addEventListener('keydown', function (event) {
            if (event.defaultPrevented || event.ctrlKey || event.altKey || event.metaKey || event.isComposing) {
                return;
            }

            const arrow = kit.shortcuts.arrowOf(event);
            const tv = kit.layout.isTv();
            if (!arrow || (tv && (arrow === 'ArrowUp' || arrow === 'ArrowDown'))) {
                return;
            }

            const rtl = dataRtl(button);
            const forward = arrow === 'ArrowDown' || arrow === (rtl ? 'ArrowLeft' : 'ArrowRight');
            const at = buttons.indexOf(button);
            const next = buttons[(at + (forward ? 1 : buttons.length - 1)) % buttons.length];
            event.preventDefault();
            choose(next.getAttribute('data-jfur-value'));
            next.focus();
        });
        return button;
    });
    const set = function (v) {
        group.value = v;
        const known = values.indexOf(v) >= 0;
        buttons.forEach(function (b, i) {
            const on = b.getAttribute('data-jfur-value') === v;
            b.setAttribute('aria-checked', on ? 'true' : 'false');
            b.setAttribute('tabindex', on || (!known && i === 0) ? '0' : '-1');
        });
    };
    const group = { value: value, buttons: buttons, set: set, node: kit.h('div', { class: 'jfur-data-row' }, [label, kit.h('div', { role: 'radiogroup', class: 'jfur-data-choices', 'aria-labelledby': id + '-label' }, buttons)]) };
    set(value);
    return group;
}

// A question before a change: the kit's dialog, Cancel focused, the focus back on returnTo (or what had it). Resolves true
// for the action.
function dataAsk(opts) {
    return new Promise(function (resolve) {
        let answered = false;
        let cancel = null;
        const dialog = kit.dialog({
            title: opts.title,
            modal: true,
            scopedKeys: true,
            initialFocus: function () { return cancel; },
            returnFocus: opts.returnFocus
        });
        dialog.onClose(function () {
            if (!answered) {
                resolve(false);
            }
        });
        cancel = kit.button(kit.t('data.cancel'), { className: 'jfur-data-cancel', onClick: function () { dialog.close(); } });
        const go = kit.button(opts.confirm, { variant: opts.danger ? 'danger' : 'primary', className: 'jfur-data-confirm', onClick: function () {
            answered = true;
            resolve(true);
            dialog.close();
        } });
        [].concat(opts.body || []).forEach(function (node) {
            kit.append(dialog.body, node);
        });
        kit.append(dialog.body, kit.h('div', { class: 'jfur-data-actions jfur-data-dialog-actions' }, [cancel, go]));
    });
}

// A list drawn again keeps the focus that was in scope: on the button with the same attr value, else on the first of
// others() that takes it (a section's heading, tabindex -1, last), so the TV layout's D-pad never starts again from the
// page's body. Call it before drawing; call what it returns after.
function dataKeepFocus(scope, attr, others) {
    const active = document.activeElement;
    if (!active || active === document.body || !scope.contains(active)) {
        return function () {};
    }

    const key = active.getAttribute(attr);
    return function () {
        if (active.isConnected && document.activeElement === active) {
            return;
        }

        const same = key === null ? null : Array.prototype.find.call(scope.querySelectorAll('button[' + attr + ']'), function (b) {
            return b.getAttribute(attr) === key;
        });
        [same].concat(others()).some(modFocus);
    };
}

function dataSize(bytes) {
    const n = Number(bytes);
    return isFinite(n) && n > 0 ? kit.t('data.size', { size: Math.max(1, Math.round(n / 1024)).toLocaleString() }) : '';
}

function dataCount(value) {
    const n = Number(value);
    return isFinite(n) ? n : 0;
}

// A refusal in words: one of our codes, else the server's words (a 409 of read-only data, a 503, anything else).
function dataProblemText(e) {
    if (e && e.code === 'AllLibrariesNeeded') {
        return kit.t('data.allLibraries');
    }

    if (e && e.code === 'JobRunning') {
        return kit.t('data.busy');
    }

    if (e && e.code === 'Changed') {
        return kit.t('data.changed');
    }

    if (e && e.status === 413) {
        return kit.t('data.tooBig');
    }

    return kit.t('data.failed', { reason: (e && e.message) || kit.t('data.refused') });
}

// A finished job that didn't succeed, as the error it carries.
function dataJobError(job) {
    return { status: job.ErrorStatus || 0, code: job.ErrorCode || null, message: job.Error || null, cancelled: job.State === 'Cancelled' };
}

function dataPhaseText(job) {
    if (job.State === 'Queued') {
        return kit.t('data.phase.Queued');
    }

    const known = ['Read', 'Resolve', 'Write', 'Archive'].indexOf(job.Phase) >= 0;
    return kit.t('data.phase.' + (known ? job.Phase : 'Other'), { processed: dataCount(job.Processed), total: dataCount(job.Total) });
}

function dataRunning(job) {
    return !!job && (job.State === 'Queued' || job.State === 'Running');
}

// Polls GET Admin/Jobs/{id} every second until the job finishes; onUpdate(job) at each answer. A request that gets no
// answer is tried again (five times); alive() false stops polling quietly.
function dataWaitForJob(id, onUpdate, alive) {
    return new Promise(function (resolve, reject) {
        let misses = 0;
        const tick = function () {
            if (!alive()) {
                resolve(null);
                return;
            }

            kit.api('GET', 'UserRatings/Admin/Jobs/' + encodeURIComponent(id)).then(function (job) {
                misses = 0;
                onUpdate(job);
                if (dataRunning(job)) {
                    window.setTimeout(tick, DATA_POLL_MS);
                } else {
                    resolve(job);
                }
            }, function (e) {
                if (e && e.status === 0 && ++misses < 5) {
                    window.setTimeout(tick, DATA_POLL_MS * 2);
                    return;
                }

                reject(e);
            });
        };
        window.setTimeout(tick, DATA_POLL_MS);
    });
}

// The export's file, fetched with the session's authorization (never a token in a link) and saved under its name.
async function dataDownload(job) {
    const response = await kit.api('GET', 'UserRatings/Admin/Jobs/' + encodeURIComponent(job.Id) + '/File', { raw: true, timeout: 120000 });
    return dataSave(response, (job.Report && job.Report.FileName) || 'userratings-export.json');
}

// A response's body saved as a file under a name, through a link clicked once (the JE export shares it).
async function dataSave(response, name) {
    const blob = typeof response.blob === 'function' ? await response.blob() : new Blob([await response.text()], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = kit.h('a', { href: url, download: name, class: 'jfur-data-download-link' });
    kit.setHidden(link, true);
    document.body.append(link);
    link.click();
    link.remove();
    window.setTimeout(function () {
        URL.revokeObjectURL(url);
    }, 10000);
    return name;
}

// ------------------------------------------------------------------------------------------------- the Data tab

function mountData(view) {
    kit.injectStyles();
    const n = 'jfur-data-' + Math.floor(Math.random() * 1e9).toString(36);
    const state = { file: null, mode: 'Merge', mapUsersBy: 'Id', plan: null, planOf: null, job: null, generation: 0, snapshots: [], users: null, restricted: false, damaged: false, startedEmptyUtc: null,
        je: { copies: 'First', storeUnmatched: true, recreatePurged: false, userMap: {}, unknown: {}, users: null, usersError: null, plan: null, planOptions: null, settings: null, leaveOutRestricted: true } };
    const alive = function () {
        return view.isConnected;
    };

    // The integrity banner.
    const integrity = kit.h('div', { class: 'jfur-data-integrity' });
    const restricted = kit.h('div', { class: 'jfur-data-restricted' });

    // Export.
    const exportButton = kit.button(kit.t('data.export'), { className: 'jfur-data-export', onClick: function () { runExport(); } });
    const downloadButton = kit.button(kit.t('data.download'), { className: 'jfur-data-download', onClick: function () { downloadAgain(); } });
    const exportStatus = kit.h('p', { class: 'jfur-data-status jfur-data-export-status', role: 'status' });
    const exportMessage = kit.h('div', { class: 'jfur-data-message' });
    kit.setHidden(downloadButton, true);

    // Import.
    // jellyfin-web's TV layout skips a file field for the D-pad unless it has its class "focusable".
    const fileInput = kit.makeFocusable(kit.h('input', { id: n + '-file', type: 'file', accept: '.json,application/json', class: 'jfur-data-file' }));
    fileInput.addEventListener('change', function () {
        const files = fileInput.files;
        state.file = files && files.length ? files[0] : null;
        report.replaceChildren();
        importMessage.replaceChildren();
        resetPlan();
    });
    const modes = dataChoices(n + '-mode', 'data.mode', ['Merge', 'Replace'], function (v) { return kit.t('data.mode.' + v); }, 'Merge', function (v) {
        state.mode = v;
        resetPlan();
    });
    const matches = dataChoices(n + '-match', 'data.match', ['Id', 'Name'], function (v) { return kit.t('data.match.' + v); }, 'Id', function (v) {
        state.mapUsersBy = v;
        resetPlan();
    });
    const dryRunButton = kit.button(kit.t('data.dryRun'), { className: 'jfur-data-dryrun', onClick: function () { runImport(true); } });
    const importButton = kit.button(kit.t('data.import'), { variant: 'primary', className: 'jfur-data-import', onClick: function () { askImport(); } });
    const stopButton = kit.button(kit.t('data.stop'), { className: 'jfur-data-stop', onClick: function () { stop(); } });
    const importHint = kit.h('p', { class: 'jfur-data-hint' });
    const progress = kit.h('p', { class: 'jfur-data-progress', role: 'status' });
    const importMessage = kit.h('div', { class: 'jfur-data-message' });
    const report = kit.h('div', { class: 'jfur-data-report', 'aria-live': 'polite' });
    kit.setHidden(stopButton, true);

    // Jellyfin Enhanced.
    const jeCopies = dataChoices(n + '-je-copies', 'je.copies', ['First', 'All'], function (v) { return kit.t('je.copies.' + v); }, 'First', function (v) {
        state.je.copies = v;
        jeResetPlan();
    });
    const jeUnmatched = dataChoices(n + '-je-unmatched', 'je.unmatched', ['Keep', 'Skip'], function (v) { return kit.t('je.unmatched.' + v); }, 'Keep', function (v) {
        state.je.storeUnmatched = v === 'Keep';
        jeResetPlan();
    });
    const jePurged = dataChoices(n + '-je-purged', 'je.purged', ['Skip', 'Make'], function (v) { return kit.t('je.purged.' + v); }, 'Skip', function (v) {
        state.je.recreatePurged = v === 'Make';
        jeResetPlan();
    });
    const jeUserMap = kit.h('div', { class: 'jfur-data-je-usermap' });
    const jeDryRunButton = kit.button(kit.t('data.dryRun'), { className: 'jfur-data-je-dryrun', onClick: function () { runJe(true); } });
    const jeImportButton = kit.button(kit.t('data.import'), { variant: 'primary', className: 'jfur-data-je-import', onClick: function () { askJe(); } });
    const jeStopButton = kit.button(kit.t('data.stop'), { className: 'jfur-data-je-stop', onClick: function () { stop(); } });
    const jeHint = kit.h('p', { class: 'jfur-data-hint jfur-data-je-hint' });
    const jeProgress = kit.h('p', { class: 'jfur-data-progress jfur-data-je-progress', role: 'status' });
    const jeMessage = kit.h('div', { class: 'jfur-data-message jfur-data-je-message' });
    const jeReport = kit.h('div', { class: 'jfur-data-report jfur-data-je-report', 'aria-live': 'polite' });
    const jeChecklist = kit.h('div', { class: 'jfur-data-je-checklist' });
    const jeHidingIntro = kit.h('p', { class: 'jfur-data-intro jfur-data-je-hiding-intro' });
    const jeHidingButton = kit.button(kit.t('je.hiding'), { className: 'jfur-data-je-hiding', onClick: function () { sameHiding(); } });
    const jeHidingRow = kit.h('div', { class: 'jfur-data-je-hiding-row' }, [jeHidingIntro, kit.h('div', { class: 'jfur-data-actions' }, [jeHidingButton])]);
    const jeHidingMessage = kit.h('div', { class: 'jfur-data-message jfur-data-je-hiding-message' });
    // The way back, the ratings as JE's own reviews.json.
    // JE 12.14 serves every review by TMDb id to any signed-in user, so by default the export leaves
    // out the ratings of titles not every member can open (the server's leaveOutRestricted, RestrictedLibrary).
    const jeRestricted = dataChoices(n + '-je-restricted', 'je.restricted', ['Leave', 'Include'], function (v) { return kit.t('je.restricted.' + v); }, 'Leave', function (v) {
        state.je.leaveOutRestricted = v === 'Leave';
    });
    const jeExportButton = kit.button(kit.t('je.export'), { className: 'jfur-data-je-export', onClick: function () { exportJe(); } });
    const jeExportStatus = kit.h('div', { class: 'jfur-data-report jfur-data-je-export-status', 'aria-live': 'polite' });
    const jeExportMessage = kit.h('div', { class: 'jfur-data-message jfur-data-je-export-message' });
    kit.setHidden(jeStopButton, true);
    kit.setHidden(jeHidingRow, true);

    // Snapshots.
    const snapshotsHeading = kit.h('h3', { tabindex: '-1', text: kit.t('data.snapshotsTitle') });
    const snapshotList = kit.h('ul', { class: 'jfur-data-snapshots' });
    const snapshotMessage = kit.h('div', { class: 'jfur-data-message' });
    const restoreReport = kit.h('div', { class: 'jfur-data-report', 'aria-live': 'polite' });

    // Unknown users.
    const usersHeading = kit.h('h3', { tabindex: '-1', text: kit.t('data.usersSectionTitle') });
    const usersIntro = kit.h('p', { class: 'jfur-data-intro' });
    const usersList = kit.h('ul', { class: 'jfur-data-users' });
    const remapButton = kit.button(kit.t('data.remap'), { className: 'jfur-data-remap', onClick: function () { remap(); } });
    const policyAllButton = kit.button(kit.t('data.policyAll'), { className: 'jfur-data-policy-all', onClick: function () { applyPolicy(null, policyAllButton); } });
    const usersActions = kit.h('div', { class: 'jfur-data-actions' }, [remapButton, policyAllButton]);
    const usersMessage = kit.h('div', { class: 'jfur-data-message' });
    const usersReport = kit.h('div', { class: 'jfur-data-report', 'aria-live': 'polite' });

    const fileLabel = kit.h('label', { for: fileInput.id, class: 'jfur-data-label', text: kit.t('data.file') });
    const sections = kit.h('div', { class: 'jfur-data-sections' }, [
        kit.h('section', { class: 'jfur-data-section jfur-data-export-section' }, [
            kit.h('h3', { text: kit.t('data.exportTitle') }),
            kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.exportIntro') }),
            kit.h('div', { class: 'jfur-data-actions' }, [exportButton, downloadButton]),
            exportStatus,
            exportMessage
        ]),
        kit.h('section', { class: 'jfur-data-section jfur-data-import-section' }, [
            kit.h('h3', { text: kit.t('data.importTitle') }),
            kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.importIntro') }),
            kit.h('div', { class: 'jfur-data-row' }, [fileLabel, fileInput]),
            modes.node,
            matches.node,
            kit.h('div', { class: 'jfur-data-actions' }, [dryRunButton, importButton, stopButton]),
            importHint,
            progress,
            importMessage,
            report
        ]),
        kit.h('section', { class: 'jfur-data-section jfur-data-je-section' }, [
            kit.h('h3', { text: kit.t('je.title') }),
            kit.h('p', { class: 'jfur-data-intro', text: kit.t('je.intro') }),
            jeCopies.node,
            jeUnmatched.node,
            jePurged.node,
            jeUserMap,
            kit.h('div', { class: 'jfur-data-actions' }, [jeDryRunButton, jeImportButton, jeStopButton]),
            jeHint,
            jeProgress,
            jeMessage,
            jeReport,
            kit.h('h4', { class: 'jfur-data-je-switch-title', text: kit.t('je.switchTitle') }),
            kit.h('p', { class: 'jfur-data-intro', text: kit.t('je.switchIntro') }),
            jeChecklist,
            jeHidingRow,
            jeHidingMessage,
            kit.h('h4', { class: 'jfur-data-je-back-title', text: kit.t('je.backTitle') }),
            kit.h('p', { class: 'jfur-data-intro', text: kit.t('je.backIntro') }),
            kit.h('p', { class: 'jfur-data-intro jfur-data-je-back-privacy', text: kit.t('je.backPrivacy') }),
            jeRestricted.node,
            kit.h('div', { class: 'jfur-data-actions' }, [jeExportButton]),
            jeExportStatus,
            jeExportMessage
        ]),
        kit.h('section', { class: 'jfur-data-section jfur-data-snapshots-section' }, [
            snapshotsHeading,
            kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.snapshotsIntro') }),
            snapshotMessage,
            snapshotList,
            restoreReport
        ])
    ]);
    const usersSection = kit.h('section', { class: 'jfur-data-section jfur-data-users-section' }, [
        usersHeading,
        usersIntro,
        usersMessage,
        usersList,
        usersActions,
        usersReport
    ]);
    const root = kit.h('div', { class: 'jfur-data' }, [integrity, restricted, sections, usersSection]);
    view.replaceChildren(root);
    [integrity, restricted, usersActions].forEach(function (node) {
        kit.setHidden(node, true);
    });
    resetPlan();
    jeResetPlan();

    // Tells the page the data changed (its Diagnostics and banners are read again).
    function changed() {
        view.dispatchEvent(new CustomEvent('jfur-data-changed', { bubbles: true }));
    }

    function retryNotice(text, again) {
        return kit.notice({ kind: 'error', text: text, action: again ? { label: kit.t('data.retry'), onClick: again } : null });
    }

    // ---- reading

    function load() {
        const generation = ++state.generation;
        snapshotMessage.replaceChildren(kit.notice({ kind: 'empty', text: kit.t('data.loading') }));
        kit.api('GET', 'UserRatings/Admin/Snapshots').then(function (answer) {
            if (generation !== state.generation) {
                return;
            }

            setRestricted(false);
            renderIntegrity((answer && answer.Integrity) || {});
            renderSnapshots((answer && answer.Snapshots) || []);
        }, function (e) {
            if (generation !== state.generation) {
                return;
            }

            if (e && e.code === 'AllLibrariesNeeded') {
                setRestricted(true);
                return;
            }

            snapshotMessage.replaceChildren(retryNotice(kit.t('data.loadFailed', { reason: (e && e.message) || '' }), load));
        });
        loadUsers();
        if (!state.job) {
            kit.api('GET', 'UserRatings/Admin/Jobs').then(function (jobs) {
                const running = (jobs || []).find(dataRunning);
                if (running && !state.job && generation === state.generation) {
                    follow(running).then(function (finished) {
                        if (finished) {
                            showFinished(finished);
                        }
                    }, function (e) {
                        importMessage.replaceChildren(retryNotice(dataProblemText(e), null));
                    });
                }
            }, function () {
                // The jobs are read again with the tab.
            });
        }
    }

    function setRestricted(on) {
        state.restricted = on;
        kit.setHidden(sections, on);
        kit.setHidden(restricted, !on);
        // The unknown users' tools too: Remap and the policy change every library's ratings.
        kit.setHidden(usersSection, on);
        restricted.replaceChildren();
        if (on) {
            restricted.append(kit.notice({ kind: 'info', text: kit.t('data.allLibraries') }));
        }

        if (on) {
            kit.setHidden(integrity, true);
        }
    }

    function renderIntegrity(info) {
        const notes = [];
        if (info.Damaged === true) {
            notes.push(kit.notice({ kind: 'error', text: kit.t('data.damaged', { message: info.Message || '' }).trim() }));
            // Start empty: when no snapshot repairs it, or the admin would rather not go back to one.
            const startEmptyButton = kit.button(kit.t('data.startEmpty'), { className: 'jfur-data-start-empty', onClick: function () { startEmpty(); } });
            notes.push(kit.h('div', { class: 'jfur-data-start-empty-row' }, [kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.startEmptyIntro') }), startEmptyButton]));
        } else if (info.ReadOnly === true) {
            notes.push(kit.notice({ kind: 'error', text: kit.t('data.readOnly', { message: info.Message || '' }).trim() }));
        }

        const recovery = info.LastRecovery;
        if (recovery && recovery.RestoredFrom) {
            notes.push(kit.notice({ kind: 'info', text: kit.t('data.recovered', { snapshot: recovery.RestoredFrom, when: modWhen(recovery.Utc), reason: recovery.Reason || '', lost: recovery.LostSpan || '' }) }));
        } else if (recovery && recovery.StartedEmpty === true) {
            // Start empty restores nothing: its time and why (RestoredFrom is null then).
            notes.push(kit.notice({ kind: 'info', text: kit.t('data.recoveredEmpty', { when: modWhen(recovery.Utc), reason: recovery.Reason || '' }) }));
        }

        // A snapshot from before that start empty can't keep the deletions made between the two.
        state.startedEmptyUtc = recovery && recovery.StartedEmpty === true && recovery.Utc ? recovery.Utc : null;

        const quarantines = info.Quarantines || [];
        if (quarantines.length > 0) {
            notes.push(kit.notice({ kind: 'info', text: kit.t('data.quarantines', { names: quarantines.map(function (q) { return q.Name; }).join(', ') }) }));
        }

        state.damaged = info.Damaged === true;
        const hadFocus = integrity.contains(document.activeElement);
        integrity.replaceChildren.apply(integrity, notes);
        kit.setHidden(integrity, notes.length === 0);
        if (hadFocus && !integrity.contains(document.activeElement)) {
            // Start empty's button went with the damage: the focus goes to the snapshots, never to the page's body.
            snapshotsHeading.focus();
        }
    }

    function startEmpty() {
        restoreReport.replaceChildren();
        dataAsk({
            title: kit.t('data.startEmptyTitle'),
            body: kit.h('p', { text: kit.t('data.startEmptyQuestion') }),
            confirm: kit.t('data.startEmptyConfirm'),
            danger: true,
            returnFocus: function () { return integrity.querySelector('.jfur-data-start-empty') || snapshotsHeading; }
        }).then(function (yes) {
            if (!yes) {
                return null;
            }

            restoreReport.replaceChildren(kit.notice({ kind: 'empty', text: kit.t('data.startingEmpty') }));
            return kit.api('POST', 'UserRatings/Admin/Data/StartEmpty', { timeout: 120000 }).then(function (r) {
                const parts = [kit.h('p', { class: 'jfur-data-summary', text: kit.t('data.startedEmpty') })];
                if (r && r.QuarantineFolder) {
                    parts.push(kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.startedEmptyKept', { folder: r.QuarantineFolder }) }));
                }

                restoreReport.replaceChildren.apply(restoreReport, parts);
                kit.toast(kit.t('data.startedEmpty'));
                load();
                changed();
            }, function (e) {
                restoreReport.replaceChildren(retryNotice(dataProblemText(e), startEmpty));
            });
        });
    }

    function renderSnapshots(list) {
        // After a restore the list is read again: the focus stays on that snapshot's Restore….
        const keep = dataKeepFocus(snapshotList, 'data-jfur-name', function () {
            return [snapshotList.querySelector('.jfur-data-restore'), snapshotsHeading];
        });
        state.snapshots = list;
        snapshotMessage.replaceChildren();
        if (list.length === 0) {
            snapshotList.replaceChildren();
            snapshotMessage.append(kit.notice({ kind: 'empty', text: kit.t('data.snapshotsEmpty') }));
            keep();
            return;
        }

        snapshotList.replaceChildren.apply(snapshotList, list.map(function (s) {
            const operation = DATA_OPERATIONS.indexOf(s.Operation) >= 0 ? s.Operation : 'other';
            const kind = s.Kind === 'PreOp' ? kit.t('data.before', { operation: kit.t('data.op.' + operation) }) : kit.t('data.daily');
            const button = kit.button(kit.t('data.restore'), { className: 'jfur-data-restore', onClick: function () { restore(s, button); } });
            button.setAttribute('aria-label', kit.t('data.restoreLabel', { name: s.Name }));
            button.setAttribute('data-jfur-name', s.Name);
            return kit.h('li', { class: 'jfur-data-snapshot' }, [
                kit.h('div', { class: 'jfur-data-snapshot-text' }, [
                    modIsolated('span', 'jfur-data-snapshot-name', s.Name),
                    kit.h('span', { class: 'jfur-data-snapshot-meta', text: [kind, modWhen(s.Utc), dataSize(s.Size), s.Kept ? kit.t('data.kept') : ''].filter(Boolean).join(' · ') })
                ]),
                button
            ]);
        }));
        keep();
    }

    function loadUsers() {
        kit.api('GET', 'UserRatings/Admin/Diagnostics').then(function (d) {
            renderUsers((d && d.Users) || {});
            renderJeChecklist((d && d.JellyfinEnhanced) || null);
        }, function (e) {
            usersMessage.replaceChildren(retryNotice(kit.t('data.loadFailed', { reason: (e && e.message) || '' }), loadUsers));
        });
    }

    function renderUsers(users) {
        // After Remap or the policy the users are read again: the focus stays on that user's button, else the next.
        const keep = dataKeepFocus(usersSection, 'data-jfur-user', function () {
            return [usersList.querySelector('.jfur-data-policy')].concat(kit.isHidden(usersActions) ? [] : [remapButton]).concat([usersHeading]);
        });
        state.users = users;
        const policy = users.DeletedUserPolicy || 'KeepAsOrphan';
        usersIntro.textContent = kit.t('data.usersIntro', { policy: kit.t('data.policy.' + (['Delete', 'Anonymize', 'KeepAsOrphan'].indexOf(policy) >= 0 ? policy : 'KeepAsOrphan')) });
        usersMessage.replaceChildren();
        const orphans = users.OrphanUsers || [];
        kit.setHidden(usersActions, orphans.length === 0);
        if (orphans.length === 0) {
            usersList.replaceChildren();
            usersMessage.append(kit.notice({ kind: 'empty', text: kit.t('data.usersNone') }));
            keep();
            return;
        }

        usersList.replaceChildren.apply(usersList, orphans.map(function (u) {
            const name = u.LastKnownName || kit.t('data.unknownName', { id: u.UserId });
            const button = kit.button(kit.t('data.policy'), { className: 'jfur-data-policy', onClick: function () { applyPolicy([u.UserId], button); } });
            button.setAttribute('data-jfur-user', u.UserId);
            return kit.h('li', { class: 'jfur-data-user' }, [
                kit.h('span', { class: 'jfur-data-user-text' }, modSentence('data.userRow', { name: modIsolated('span', null, name), count: document.createTextNode(String(dataCount(u.Records))) })
                    .concat(u.PolicyPending ? [document.createTextNode(' (' + kit.t('data.policyPending') + ')')] : [])),
                button
            ]);
        }));
        keep();
    }

    // ---- jobs

    // Follows a job until it finishes (null when the tab left the page): its phase as it goes, Stop while it can stop.
    function follow(job) {
        state.job = job;
        showProgress(job);
        return dataWaitForJob(job.Id, showProgress, alive).then(function (finished) {
            state.job = null;
            showProgress(null);
            return finished;
        }, function (e) {
            state.job = null;
            showProgress(null);
            throw e;
        });
    }

    function showProgress(job) {
        const running = dataRunning(job);
        // The Jellyfin Enhanced import's job shows in its own section, every other job in the import's.
        const je = running && job.Kind === DATA_JE_JOB;
        progress.textContent = running && !je ? dataPhaseText(job) : '';
        jeProgress.textContent = je ? dataPhaseText(job) : '';
        kit.setHidden(stopButton, !running || je || job.Writing === true);
        kit.setHidden(jeStopButton, !je || job.Writing === true);
        [stopButton, jeStopButton].forEach(function (b) {
            b.disabled = false;
            b.textContent = kit.t('data.stop');
        });
        exportButton.disabled = running;
        dryRunButton.disabled = running || !state.file;
        importButton.disabled = running || !planReady();
        jeDryRunButton.disabled = running;
        jeImportButton.disabled = running || !jePlanReady();
        const busy = !!state.job || running;
        snapshotList.querySelectorAll('.jfur-data-restore').forEach(function (b) {
            b.disabled = busy;
        });
    }

    // A job this tab didn't start (one running when it was opened): what it ended with.
    function showFinished(job) {
        if (job.Kind === 'Export' && job.State === 'Succeeded') {
            exported(job);
            return;
        }

        if (job.Kind === DATA_JE_JOB) {
            if (job.State === 'Succeeded' && job.Report) {
                renderJeReport(job.Report);
                if (!job.DryRun) {
                    load();
                    changed();
                }
            } else {
                jeMessage.replaceChildren(job.State === 'Cancelled' ? kit.notice({ kind: 'info', text: kit.t('data.cancelled') }) : retryNotice(jeProblemText(dataJobError(job)), null));
            }

            return;
        }

        if (job.State === 'Succeeded' && job.Report) {
            renderReport(job.Report);
            if (!job.DryRun) {
                load();
                changed();
            }

            return;
        }

        importMessage.replaceChildren(job.State === 'Cancelled' ? kit.notice({ kind: 'info', text: kit.t('data.cancelled') }) : retryNotice(dataProblemText(dataJobError(job)), null));
    }

    function stop() {
        const job = state.job;
        if (!job) {
            return;
        }

        const je = job.Kind === DATA_JE_JOB;
        const button = je ? jeStopButton : stopButton;
        const message = je ? jeMessage : importMessage;
        button.disabled = true;
        button.textContent = kit.t('data.stopping');
        kit.api('DELETE', 'UserRatings/Admin/Jobs/' + encodeURIComponent(job.Id)).then(null, function (e) {
            button.disabled = false;
            button.textContent = kit.t('data.stop');
            if (e && e.code === 'JobWriting') {
                kit.setHidden(button, true);
                message.replaceChildren(kit.notice({ kind: 'info', text: kit.t('data.writing') }));
            } else if (!(e && e.code === 'JobFinished')) {
                message.replaceChildren(retryNotice(dataProblemText(e), null));
            }
        });
    }

    // ---- export

    function runExport() {
        exportMessage.replaceChildren();
        exportStatus.textContent = kit.t('data.exporting');
        const refocus = dataBusy(exportButton);
        kit.api('POST', 'UserRatings/Admin/Export').then(follow).then(function (finished) {
            if (!finished) {
                return null;
            }

            if (finished.State !== 'Succeeded') {
                throw dataJobError(finished);
            }

            return dataDownload(finished).then(function () {
                exported(finished);
            });
        }).then(null, function (e) {
            exportStatus.textContent = '';
            exportMessage.replaceChildren(retryNotice(dataProblemText(e), runExport));
        }).then(function () {
            exportButton.disabled = !!state.job;
            refocus();
        });
    }

    function exported(job) {
        const r = job.Report || {};
        state.exportJob = job;
        // A job listed after its hour has no file left to download again.
        kit.setHidden(downloadButton, job.HasFile === false);
        exportStatus.textContent = kit.t('data.exported', { ratings: dataCount(r.Ratings), reviews: dataCount(r.Reviews), detached: dataCount(r.Detached), archived: dataCount(r.Archived), file: r.FileName || '' });
    }

    function downloadAgain() {
        if (!state.exportJob) {
            return;
        }

        exportMessage.replaceChildren();
        dataDownload(state.exportJob).then(null, function (e) {
            // The server keeps the file an hour, and for the last ten jobs only.
            kit.setHidden(downloadButton, true);
            exportMessage.replaceChildren(retryNotice(dataProblemText(e), runExport));
        });
    }

    // ---- import

    function options() {
        return state.mode + '|' + state.mapUsersBy;
    }

    function planReady() {
        return !!state.plan && state.planOf === state.file && state.planOptions === options();
    }

    function resetPlan() {
        state.plan = null;
        state.planOf = null;
        state.planOptions = null;
        dryRunButton.disabled = !state.file || !!state.job;
        importButton.disabled = true;
        importHint.textContent = state.file ? kit.t('data.needDryRun') : kit.t('data.chooseFile');
    }

    function runImport(dryRun) {
        const file = state.file;
        if (!file) {
            resetPlan();
            return Promise.resolve();
        }

        const opts = options();
        const plan = state.plan;
        importMessage.replaceChildren();
        if (Number(file.size) > DATA_MAX_IMPORT_BYTES) {
            importMessage.append(kit.notice({ kind: 'error', text: kit.t('data.tooBig') }));
            return Promise.resolve();
        }

        dryRunButton.disabled = true;
        importButton.disabled = true;
        report.replaceChildren();
        progress.textContent = kit.t('data.phase.Read');
        return Promise.resolve().then(function () {
            const query = { mode: state.mode, dryRun: dryRun ? 'true' : 'false', mapUsersBy: state.mapUsersBy };
            if (!dryRun) {
                query.ifPlan = plan;
            }

            // The file itself, byte for byte: file.text() dropped a BOM, replaced invalid UTF-8 and
            // made two string copies of up to 100 MB; fetch reads the Blob as it sends it.
            return kit.api('POST', 'UserRatings/Admin/Import', { query: query, blob: file, timeout: 300000 }).then(follow).then(function (finished) {
                if (!finished) {
                    return;
                }

                if (finished.State === 'Cancelled') {
                    importMessage.replaceChildren(kit.notice({ kind: 'info', text: kit.t('data.cancelled') }));
                    state.plan = null;
                    return;
                }

                if (finished.State !== 'Succeeded') {
                    throw dataJobError(finished);
                }

                renderReport(finished.Report || {});
                if (dryRun) {
                    // The plan is this file's with these options; another file or option needs another dry run.
                    state.plan = (finished.Report && finished.Report.PlanToken) || null;
                    state.planOf = file === state.file && opts === options() ? file : null;
                    state.planOptions = opts;
                } else {
                    state.plan = null;
                    load();
                    changed();
                }
            });
        }, function (e) {
            throw { unreadable: true, message: kit.t('data.unreadableFile', { reason: (e && e.message) || '' }) };
        }).then(null, function (e) {
            state.plan = null;
            importMessage.replaceChildren(retryNotice(e && e.unreadable ? e.message : dataProblemText(e), null));
            if (e && e.code === 'JobRunning') {
                load();
            }
        }).then(function () {
            progress.textContent = '';
            dryRunButton.disabled = !state.file || !!state.job;
            importButton.disabled = !!state.job || !planReady();
            importHint.textContent = !state.file ? kit.t('data.chooseFile') : planReady() ? '' : kit.t('data.needDryRun');
        });
    }

    function askImport() {
        if (!planReady()) {
            resetPlan();
            return;
        }

        dataAsk({
            title: kit.t('data.confirmImportTitle'),
            body: kit.h('p', { text: kit.t(state.mode === 'Replace' ? 'data.confirmReplace' : 'data.confirmMerge') }),
            confirm: kit.t('data.import'),
            danger: state.mode === 'Replace',
            returnFocus: function () { return importButton.disabled ? dryRunButton : importButton; }
        }).then(function (yes) {
            if (yes && planReady()) {
                runImport(false);
            }
        });
    }

    function renderReport(r) {
        const dry = r.DryRun !== false;
        const records = r.Records || {};
        const parts = [kit.h('p', { class: 'jfur-data-summary', text: kit.t(dry ? 'data.dry' : 'data.done') + ' ' + kit.t('data.fileIs', { version: r.SourcePluginVersion || '?', when: modWhen(r.ExportedUtc), count: dataCount(records.Incoming) }) })];
        if (r.Mode === 'Replace') {
            parts.push(kit.h('p', { class: 'jfur-data-note', text: kit.t('data.replaceNote') }));
        }

        const outcomes = r.Outcomes || {};
        const known = DATA_OUTCOMES.filter(function (key) { return dataCount(outcomes[key]) > 0; });
        const others = Object.keys(outcomes).filter(function (key) { return DATA_OUTCOMES.indexOf(key) < 0 && dataCount(outcomes[key]) > 0; });
        if (known.length + others.length > 0) {
            parts.push(kit.h('ul', { class: 'jfur-data-outcomes' }, known.map(function (key) {
                return kit.h('li', { 'data-jfur-outcome': key, text: kit.t('data.outcome.' + key) + ': ' + dataCount(outcomes[key]) });
            }).concat(others.map(function (key) {
                return kit.h('li', { 'data-jfur-outcome': key, text: key + ': ' + dataCount(outcomes[key]) });
            }))));
        }

        if (dataCount(r.Unusable) > 0) {
            // Each row left out, with its reason; the server lists the first 100.
            parts.push(kit.h('p', { class: 'jfur-data-note', text: kit.t('data.unusable', { count: dataCount(r.Unusable) }) }));
            const rows = Array.isArray(r.UnusableRows) ? r.UnusableRows : [];
            if (rows.length > 0) {
                const more = dataCount(r.Unusable) - rows.length;
                parts.push(kit.h('ul', { class: 'jfur-data-unusable' }, rows.map(function (u) {
                    const args = { table: String(u.Table || '?'), row: dataCount(u.Row), id: String(u.Id || ''), reason: String(u.Reason || '') };
                    return kit.h('li', { text: kit.t(u.Id ? 'data.unusableRowId' : 'data.unusableRow', args) });
                }).concat(more > 0 ? [kit.h('li', { text: kit.t('data.unusableMore', { count: more }) })] : [])));
            }
        }

        const users = r.Users || {};
        const mapped = users.Mapped || [];
        const unknown = users.Unknown || [];
        if (mapped.length + unknown.length > 0) {
            parts.push(kit.h('p', { class: 'jfur-data-label', text: kit.t('data.usersTitle') }));
            parts.push(kit.h('ul', { class: 'jfur-data-report-users' }, mapped.map(function (u) {
                return kit.h('li', null, modSentence('data.userFound', { name: modIsolated('span', null, u.Name || u.Id), count: document.createTextNode(String(dataCount(u.Records))) }));
            }).concat(unknown.map(function (u) {
                return kit.h('li', { class: 'jfur-data-unknown' }, modSentence('data.userUnknown', { name: modIsolated('span', null, u.Name || u.Id), count: document.createTextNode(String(dataCount(u.Records))) }));
            }))));
        }

        const archive = r.Archive || {};
        if (dataCount(archive.Records) > 0) {
            parts.push(kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.archive', { records: dataCount(archive.Records), imported: dataCount(archive.Imported) }) }));
        }

        [archive.Note].concat(r.Notes || []).filter(Boolean).forEach(function (note) {
            parts.push(kit.h('p', { class: 'jfur-data-note', text: String(note) }));
        });
        if (r.SafetySnapshot) {
            parts.push(kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.safety', { name: r.SafetySnapshot }) }));
        }

        report.replaceChildren.apply(report, parts);
    }

    // ---- Jellyfin Enhanced

    // The request's body: the options, and the UserMap of the members mapped (one left out is not sent).
    function jeBody() {
        const map = {};
        Object.keys(state.je.userMap).sort().forEach(function (id) {
            if (state.je.userMap[id]) {
                map[id] = state.je.userMap[id];
            }
        });
        return { Copies: state.je.copies, StoreUnmatched: state.je.storeUnmatched, RecreatePurged: state.je.recreatePurged, UserMap: map };
    }

    function jeOptions() {
        return JSON.stringify(jeBody());
    }

    function jePlanReady() {
        return !!state.je.plan && state.je.planOptions === jeOptions();
    }

    function jeResetPlan() {
        state.je.plan = null;
        state.je.planOptions = null;
        jeDryRunButton.disabled = !!state.job;
        jeImportButton.disabled = true;
        jeHint.textContent = kit.t('je.needDryRun');
    }

    function jeProblemText(e) {
        if (e && e.code === 'JeDataNotFound') {
            return kit.t('je.noData');
        }

        if (e && e.code === 'UnsupportedFormat') {
            return kit.t('je.format', { reason: e.message || '' }).trim();
        }

        return dataProblemText(e);
    }

    function runJe(dryRun) {
        const opts = jeOptions();
        const plan = state.je.plan;
        const query = { dryRun: dryRun ? 'true' : 'false' };
        if (!dryRun) {
            query.ifPlan = plan;
        }

        jeMessage.replaceChildren();
        jeReport.replaceChildren();
        jeDryRunButton.disabled = true;
        jeImportButton.disabled = true;
        jeProgress.textContent = kit.t('data.phase.Read');
        return kit.api('POST', 'UserRatings/Admin/Import/JellyfinEnhanced', { query: query, body: jeBody(), timeout: 120000 }).then(follow).then(function (finished) {
            if (!finished) {
                return;
            }

            if (finished.State === 'Cancelled') {
                jeMessage.replaceChildren(kit.notice({ kind: 'info', text: kit.t('data.cancelled') }));
                state.je.plan = null;
                return;
            }

            if (finished.State !== 'Succeeded') {
                throw dataJobError(finished);
            }

            renderJeReport(finished.Report || {});
            if (dryRun) {
                // The plan is these options'; another option or member mapping needs another dry run.
                state.je.plan = (finished.Report && finished.Report.PlanToken) || null;
                state.je.planOptions = opts === jeOptions() ? opts : null;
            } else {
                state.je.plan = null;
                load();
                changed();
            }
        }).then(null, function (e) {
            state.je.plan = null;
            jeMessage.replaceChildren(retryNotice(jeProblemText(e), null));
            if (e && e.code === 'JobRunning') {
                load();
            }
        }).then(function () {
            jeProgress.textContent = '';
            jeDryRunButton.disabled = !!state.job;
            jeImportButton.disabled = !!state.job || !jePlanReady();
            jeHint.textContent = jePlanReady() ? '' : kit.t('je.needDryRun');
        });
    }

    function askJe() {
        if (!jePlanReady()) {
            jeResetPlan();
            return;
        }

        dataAsk({
            title: kit.t('je.confirmTitle'),
            body: kit.h('p', { text: kit.t('je.confirm') }),
            confirm: kit.t('data.import'),
            returnFocus: function () { return jeImportButton.disabled ? jeDryRunButton : jeImportButton; }
        }).then(function (yes) {
            if (yes && jePlanReady()) {
                runJe(false);
            }
        });
    }

    function jeOutcomeText(codes) {
        return String(codes || '').split(', ').filter(Boolean).map(function (code) {
            return DATA_JE_COUNTS.indexOf(code) >= 0 ? kit.t('je.count.' + code) : code;
        }).join(', ');
    }

    // The report, as text: the counts above zero, the notes, JE's field names, the entries left out and the first reviews
    // (member, title, stars, whether it has a review, what Import does; never the review itself).
    function renderJeReport(r) {
        const dry = r.DryRun !== false;
        const parts = [kit.h('p', { class: 'jfur-data-summary', text: kit.t(dry ? 'je.dry' : 'je.done') + ' ' + kit.t(dataPlural('je.total', dataCount(r.Total)), { count: dataCount(r.Total) }) })];
        const counts = DATA_JE_COUNTS.map(function (key) {
            const field = dry && key === 'Created' ? 'WouldCreate' : dry && key === 'Updated' ? 'WouldUpdate' : key;
            return { key: key, value: dataCount(r[field]) };
        }).filter(function (c) { return c.value > 0; });
        if (counts.length > 0) {
            parts.push(kit.h('ul', { class: 'jfur-data-outcomes jfur-data-je-counts' }, counts.map(function (c) {
                return kit.h('li', { 'data-jfur-count': c.key, text: kit.t('je.count.' + c.key) + ': ' + c.value });
            })));
        }

        [r.CopiesNote].concat(r.Notes || []).filter(Boolean).forEach(function (note) {
            parts.push(kit.h('p', { class: 'jfur-data-note', text: String(note) }));
        });
        [r.MirrorNote, r.SpoilerNote].filter(Boolean).forEach(function (note) {
            parts.push(kit.h('p', { class: 'jfur-data-intro', text: String(note) }));
        });
        const fields = Array.isArray(r.SourceFields) ? r.SourceFields : [];
        if (fields.length > 0) {
            parts.push(kit.h('p', { class: 'jfur-data-intro jfur-data-je-fields', text: kit.t('je.fields', { fields: fields.map(String).join(', ') }) }));
        }

        const leftOut = Array.isArray(r.LeftOut) ? r.LeftOut : [];
        if (leftOut.length > 0) {
            parts.push(kit.h('p', { class: 'jfur-data-label', text: kit.t('je.leftOut') }));
            parts.push(kit.h('ul', { class: 'jfur-data-unusable jfur-data-je-leftout' }, leftOut.map(function (l) {
                return kit.h('li', { text: kit.t('je.leftOutRow', { key: String(l.Key || '?'), reason: String(l.Reason || '') }) });
            })));
        }

        const samples = Array.isArray(r.Samples) ? r.Samples : [];
        if (samples.length > 0) {
            parts.push(kit.h('p', { class: 'jfur-data-label', text: kit.t('je.samples') }));
            parts.push(kit.h('ul', { class: 'jfur-data-report-users jfur-data-je-samples' }, samples.map(function (s) {
                // An entry left out before its member or its title was looked up (no stars and no text, one that can't be
                // read, a member who isn't a user here) is named by its key in JE's file, without a "?" for the member; one
                // with neither stars nor text says only what Import does with it (since 1.0.1; in 1.0 a
                // sample row read "? · <key> · No stars · stars only · Skipped: no stars and no text").
                const stars = modStars(s.Stars);
                const bare = !stars && !s.HasReview;
                const line = modSentence('je.sample' + (bare ? '.bare' : '') + (s.User ? '' : '.noUser'), {
                    user: modIsolated('span', null, s.User),
                    item: s.Item ? modIsolated('span', null, s.Item) : kit.h('span', null, modSentence('je.sampleEntry', { key: modIsolated('span', null, s.Key || '?') })),
                    stars: document.createTextNode(stars ? kit.t('mod.stars', { stars: stars }) : kit.t('mod.noStars')),
                    review: document.createTextNode(kit.t(s.HasReview ? 'je.withReview' : 'je.starsOnly')),
                    outcome: document.createTextNode(jeOutcomeText(s.Outcome))
                });
                if (dataCount(s.AlsoMatched) > 0) {
                    line.push(document.createTextNode(' (' + kit.t(dataPlural('je.alsoMatched', dataCount(s.AlsoMatched)), { count: dataCount(s.AlsoMatched) }) + ')'));
                }

                return kit.h('li', null, line);
            })));
        }

        if (r.SafetySnapshot) {
            parts.push(kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.safety', { name: r.SafetySnapshot }) }));
        }

        jeReport.replaceChildren.apply(jeReport, parts);
        if (dry) {
            // The members to map: those this dry run found unknown, and those mapped before (no longer unknown now).
            const unknown = {};
            (Array.isArray(r.UnknownUserIds) ? r.UnknownUserIds : []).forEach(function (u) {
                unknown[String(u.JeUserId)] = dataCount(u.Reviews);
            });
            Object.keys(state.je.userMap).forEach(function (id) {
                if (state.je.userMap[id] && !(id in unknown)) {
                    unknown[id] = dataCount(state.je.unknown[id]);
                }
            });
            state.je.unknown = unknown;
            renderJeUserMap();
        }
    }

    // A row per JE member who isn't a user here: Leave out, or one of the server's users (GET Admin/Settings has them).
    function renderJeUserMap() {
        const ids = Object.keys(state.je.unknown).sort();
        if (ids.length === 0) {
            jeUserMap.replaceChildren();
            return;
        }

        if (!state.je.users && !state.je.usersError) {
            kit.api('GET', 'UserRatings/Admin/Settings').then(function (answer) {
                state.je.users = ((answer && answer.Users) || []).filter(function (u) { return u.Missing !== true; });
                renderJeUserMap();
            }, function (e) {
                state.je.usersError = (e && e.message) || kit.t('data.refused');
                renderJeUserMap();
            });
            return;
        }

        const users = state.je.users || [];
        const rows = ids.map(function (id) {
            const group = dataChoices(n + '-je-map-' + id.replace(/[^0-9a-z]/gi, ''), 'je.unknownTitle', [''].concat(users.map(function (u) { return String(u.Id); })), function (v) {
                const user = users.find(function (u) { return String(u.Id) === v; });
                return v === '' ? kit.t('je.leaveOut') : String((user && user.Name) || v);
            }, state.je.userMap[id] || '', function (v) {
                state.je.userMap[id] = v;
                jeResetPlan();
            });
            const label = group.node.querySelector('.jfur-data-label');
            label.replaceChildren.apply(label, modSentence(dataPlural('je.unknownUser', dataCount(state.je.unknown[id])), { id: modIsolated('span', null, id), count: document.createTextNode(String(dataCount(state.je.unknown[id]))) }));
            group.node.setAttribute('data-jfur-je-user', id);
            return group.node;
        });
        jeUserMap.replaceChildren.apply(jeUserMap, [
            kit.h('p', { class: 'jfur-data-label', text: kit.t('je.unknownTitle') }),
            kit.h('p', { class: 'jfur-data-intro', text: kit.t('je.unknownIntro') })
        ].concat(state.je.usersError ? [kit.notice({ kind: 'error', text: kit.t('je.usersFailed', { reason: state.je.usersError }) })] : []).concat(rows));
    }

    // The switch-over checklist: every JE review setting as the kit reads it, required ones Done or To
    // do, the others Optional; each item's text and detail are the server's, as text. Read only: the admin changes them in
    // Jellyfin Enhanced (User Ratings never writes its settings). While JE's settings file can't be read (half
    // written, malformed: SettingsReadable false), every item is Unknown, never Done, with the reason first (it once
    // showed JE's defaults as its values, user reviews Done), and the files that couldn't be read
    // (the diagnostics' Problems, by name and a plain reason) are listed.
    function renderJeChecklist(je) {
        const overlap = (je && je.Overlap) || null;
        state.je.settings = (je && je.Settings) || null;
        state.je.readable = !je || je.SettingsReadable !== false;
        if (!je || !je.Installed || !overlap) {
            jeChecklist.replaceChildren(kit.notice({ kind: 'empty', text: kit.t('je.notInstalled') }));
            kit.setHidden(jeHidingRow, true);
            return;
        }

        const items = overlap.Checklist || [];
        const pending = items.filter(function (item) { return !item.Done && !item.Optional; }).length;
        const problems = (je.Problems || []).filter(Boolean);
        jeChecklist.replaceChildren.apply(jeChecklist, [
            state.je.readable
                ? kit.notice({ kind: 'info', text: overlap.Complete ? kit.t('je.complete') : kit.t('je.incomplete', { count: pending }) })
                : kit.notice({ kind: 'error', text: kit.t('je.unreadable') })
        ].concat(problems.length ? [kit.h('div', { class: 'jfur-data-je-problems' }, [
            kit.h('p', { class: 'jfur-data-label', text: kit.t('je.problems') }),
            kit.h('ul', {}, problems.map(function (p) { return kit.h('li', { text: String(p) }); }))
        ])] : []).concat([
            kit.h('ul', { class: 'jfur-data-je-items' }, items.map(function (item) {
                const stateKey = item.Unknown ? 'unknown' : item.Optional ? 'optional' : item.Done ? 'done' : 'todo';
                return kit.h('li', { class: 'jfur-data-je-item' + (stateKey === 'todo' || stateKey === 'unknown' ? ' jfur-data-je-todo' : ''), 'data-jfur-key': String(item.Key || ''), 'data-jfur-done': item.Done && !item.Unknown ? 'true' : 'false', 'data-jfur-state': stateKey }, [
                    kit.h('span', { class: 'jfur-data-je-state', text: kit.t('je.state.' + stateKey) + ': ' }),
                    kit.h('span', { class: 'jfur-data-je-text', text: String(item.Text || '') }),
                    item.Detail ? kit.h('span', { class: 'jfur-data-je-detail', text: String(item.Detail) }) : null
                ].filter(Boolean));
            }))
        ]));
        const flags = state.je.settings || {};
        jeHidingIntro.textContent = state.je.readable
            ? kit.t('je.hidingIntro', { hidden: kit.t(flags.HideReviewsFromHiddenUsers ? 'je.yes' : 'je.no'), disabled: kit.t(flags.HideReviewsFromDisabledUsers ? 'je.yes' : 'je.no') })
            : kit.t('je.hidingUnknownIntro');
        kit.setHidden(jeHidingRow, false);
    }

    // "Export for Jellyfin Enhanced": the server builds JE's reviews.json in memory and
    // sends it; its headers count what it holds and left out, and the tab says so in words. leaveOutRestricted is the
    // tab's choice (0.3.24). The button is disabled while it runs, and the focus goes back to it if it had it (0.3.24:
    // a disabled button drops the focus to the page).
    function exportJe() {
        jeExportMessage.replaceChildren();
        jeExportStatus.replaceChildren(kit.h('p', { text: kit.t('je.exporting') }));
        const refocus = dataBusy(jeExportButton);
        let counts = null;
        kit.api('GET', 'UserRatings/Admin/Export/JellyfinEnhanced', { raw: true, timeout: 120000, query: { leaveOutRestricted: state.je.leaveOutRestricted === false ? 'false' : 'true' } }).then(function (response) {
            const header = function (name) {
                return response.headers && typeof response.headers.get === 'function' ? response.headers.get(name) : null;
            };
            counts = { reviews: dataCount(Number(header('X-UserRatings-Exported'))), skipped: dataCount(Number(header('X-UserRatings-Skipped'))), detail: String(header('X-UserRatings-Skipped-Detail') || ''), merged: dataCount(Number(header('X-UserRatings-Merged-Copies'))), over: dataCount(Number(header('X-UserRatings-Over-Je-Limit'))) };
            return dataSave(response, 'reviews.json');
        }).then(function (name) {
            const lines = [kit.h('p', { text: kit.t(dataPlural('je.exported', counts.reviews), { file: name, count: counts.reviews }) })];
            if (counts.skipped > 0) {
                const reasons = counts.detail.split(',').map(function (part) {
                    const pair = part.split('=');
                    const key = 'je.skip.' + pair[0];
                    return kit.t(key) === key ? null : kit.t(key, { count: dataCount(Number(pair[1])) });
                }).filter(Boolean).join('; ');
                lines.push(kit.h('p', { text: kit.t(dataPlural('je.exportSkipped', counts.skipped), { count: counts.skipped, reasons: reasons }) }));
            }

            if (counts.merged > 0) {
                lines.push(kit.h('p', { text: kit.t(dataPlural('je.exportMerged', counts.merged), { count: counts.merged }) }));
            }

            if (counts.over > 0) {
                lines.push(kit.h('p', { text: kit.t(dataPlural('je.exportOverLimit', counts.over), { count: counts.over }) }));
            }

            jeExportStatus.replaceChildren.apply(jeExportStatus, lines);
        }, function (e) {
            jeExportStatus.replaceChildren();
            jeExportMessage.replaceChildren(retryNotice(dataProblemText(e), exportJe));
        }).then(function () {
            jeExportButton.disabled = false;
            refocus();
        });
    }

    // "Use the same hiding rules": User Ratings' two settings set as JE has its own, through the
    // Settings route: its dry run shows the change, a question, then the save; nothing when they are the same already.
    // JE's settings are read again first, and while they can't be read nothing is changed (it once
    // worked from JE's defaults).
    function sameHiding() {
        jeHidingMessage.replaceChildren();
        let flags = {};
        kit.api('GET', 'UserRatings/Admin/Diagnostics').then(function (d) {
            const je = (d && d.JellyfinEnhanced) || null;
            renderJeChecklist(je);
            if (!je || !je.Installed || je.SettingsReadable === false) {
                jeHidingMessage.replaceChildren(kit.notice({ kind: 'error', text: kit.t('je.hidingUnreadable') }));
                return null;
            }

            flags = je.Settings || {};
            return kit.api('GET', 'UserRatings/Admin/Settings');
        }).then(function (answer) {
            if (answer === null) {
                return null;
            }

            const saved = (answer && answer.Settings) || {};
            const body = {};
            ['HideReviewsFromHiddenUsers', 'HideReviewsFromDisabledUsers'].forEach(function (field) {
                if (saved[field] !== (flags[field] === true)) {
                    body[field] = flags[field] === true;
                }
            });
            if (Object.keys(body).length === 0) {
                jeHidingMessage.replaceChildren(kit.notice({ kind: 'info', text: kit.t('je.hidingSame') }));
                return null;
            }

            const version = answer.Version;
            return kit.api('POST', 'UserRatings/Admin/Settings', { query: { version: version }, body: body }).then(function (report) {
                const lines = ((report && report.Changes) || []).map(function (c) {
                    return kit.h('li', { text: String(c.Label || c.Field) + ': ' + String(c.From) + ' → ' + String(c.To) });
                });
                const consequences = ((report && report.Consequences) || []).map(function (c) {
                    return kit.h('li', { text: String(c) });
                });
                return dataAsk({
                    title: kit.t('je.hiding'),
                    body: [kit.h('p', { text: kit.t('je.hidingQuestion') }), kit.h('ul', { class: 'jfur-data-je-changes' }, lines)].concat(consequences.length ? [kit.h('ul', { class: 'jfur-data-je-consequences' }, consequences)] : []),
                    confirm: kit.t('je.hidingSave'),
                    returnFocus: function () { return jeHidingButton; }
                }).then(function (yes) {
                    if (!yes) {
                        return null;
                    }

                    return kit.api('POST', 'UserRatings/Admin/Settings', { query: { version: version, dryRun: 'false' }, body: body }).then(function () {
                        jeHidingMessage.replaceChildren(kit.notice({ kind: 'info', text: kit.t('je.hidingSaved') }));
                        kit.toast(kit.t('je.hidingSaved'));
                        changed();
                    });
                });
            });
        }).then(null, function (e) {
            jeHidingMessage.replaceChildren(retryNotice(dataProblemText(e), sameHiding));
        });
    }

    // ---- restore

    function restore(snapshot, button) {
        restoreReport.replaceChildren();
        dataAsk({
            title: kit.t('data.restoreTitle'),
            body: kit.h('p', { text: restoreQuestion(snapshot) }),
            confirm: kit.t('data.restoreConfirm'),
            danger: true,
            returnFocus: function () { return snapshotList.querySelector('[data-jfur-name="' + snapshot.Name.replace(/["\\]/g, '') + '"]') || button; }
        }).then(function (yes) {
            if (!yes) {
                return null;
            }

            restoreReport.replaceChildren(kit.notice({ kind: 'empty', text: kit.t('data.restoring') }));
            return kit.api('POST', 'UserRatings/Admin/Snapshots/Restore', { query: { name: snapshot.Name }, timeout: 120000 }).then(function (r) {
                const parts = [kit.h('p', { class: 'jfur-data-summary', text: kit.t('data.restored', { name: (r && r.RestoredFrom) || snapshot.Name }) })];
                if (r && r.ArchiveRestoredFrom) {
                    parts.push(kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.restoredArchive') }));
                }

                if (r && r.ArchiveRestored === false) {
                    parts.push(kit.notice({ kind: 'error', text: kit.t('data.restoredArchiveFailed', { reason: r.ArchiveError || kit.t('data.refused') }) }));
                }

                if (r && r.DeletionsError) {
                    parts.push(kit.notice({ kind: 'error', text: kit.t('data.restoredKeptFailed', { reason: r.DeletionsError }) }));
                }

                if (r && dataCount(r.DeletionsKept) + dataCount(r.ReviewRemovalsKept) + dataCount(r.FormerMembersKept) > 0) {
                    parts.push(kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.restoredKept', { ratings: dataCount(r.DeletionsKept), reviews: dataCount(r.ReviewRemovalsKept), former: dataCount(r.FormerMembersKept) }) }));
                }

                if (r && dataCount(r.ImportDeletionsUndone) + dataCount(r.ImportFormerMembersUndone) > 0) {
                    parts.push(kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.restoredImports', { ratings: dataCount(r.ImportDeletionsUndone), former: dataCount(r.ImportFormerMembersUndone) }) }));
                }

                if (r && r.SafetySnapshot) {
                    parts.push(kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.safety', { name: r.SafetySnapshot }) }));
                }

                restoreReport.replaceChildren.apply(restoreReport, parts);
                kit.toast(kit.t('data.restored', { name: (r && r.RestoredFrom) || snapshot.Name }));
                load();
                changed();
            }, function (e) {
                restoreReport.replaceChildren(retryNotice(dataProblemText(e), function () { restore(snapshot, button); }));
                if (e && e.code === 'Damaged') {
                    // The restore's own check found the database damaged: the banner,
                    // Start empty and the damaged database's restore come with the tab read again.
                    load();
                }
            });
        });
    }

    // The question names what the restore keeps: deletions made on this server since (a damaged database, or a start empty
    // after the snapshot, can't tell them all).
    function restoreQuestion(snapshot) {
        const values = { name: snapshot.Name, when: modWhen(snapshot.Utc) };
        if (state.damaged) {
            return kit.t('data.restoreDamaged', values);
        }

        if (state.startedEmptyUtc && Date.parse(state.startedEmptyUtc) > Date.parse(snapshot.Utc)) {
            return kit.t('data.restoreAfterEmpty', Object.assign(values, { emptied: modWhen(state.startedEmptyUtc) }));
        }

        return kit.t('data.restoreQuestion', values);
    }

    // ---- unknown users

    function remap() {
        usersReport.replaceChildren(kit.notice({ kind: 'empty', text: kit.t('data.remapChecking') }));
        kit.api('POST', 'UserRatings/Admin/Users/Remap', { query: { by: 'Name', dryRun: 'true' } }).then(function (plan) {
            usersReport.replaceChildren();
            const mapped = (plan && plan.Mapped) || [];
            const unmatched = (plan && plan.Unmatched) || [];
            const lines = mapped.map(function (m) {
                return kit.h('li', null, modSentence('data.remapPair', { name: modIsolated('span', null, m.Name || m.OldId), count: document.createTextNode(String(dataCount(m.Records))) }));
            }).concat(unmatched.map(function (u) {
                return kit.h('li', { class: 'jfur-data-unknown' }, modSentence('data.remapUnmatched', { name: modIsolated('span', null, u.Name || u.OldId), reason: document.createTextNode(String(u.Reason || '')) }));
            }));
            if (mapped.length === 0) {
                usersReport.replaceChildren(kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.remapNone') }));
                if (lines.length) {
                    usersReport.append(kit.h('ul', { class: 'jfur-data-remap-list' }, lines));
                }

                return null;
            }

            const body = [kit.h('ul', { class: 'jfur-data-remap-list' }, lines)];
            if (dataCount(plan.Ambiguous) > 0) {
                body.push(kit.h('p', { class: 'jfur-data-note', text: kit.t('data.remapAmbiguous', { count: dataCount(plan.Ambiguous) }) }));
            }

            if (dataCount(plan.ArchivedRecords) > 0) {
                body.push(kit.h('p', { class: 'jfur-data-intro', text: kit.t('data.remapArchived', { count: dataCount(plan.ArchivedRecords) }) }));
            }

            return dataAsk({ title: kit.t('data.remapTitle'), body: body, confirm: kit.t('data.remapConfirm'), returnFocus: function () { return remapButton; } }).then(function (yes) {
                if (!yes) {
                    return null;
                }

                return kit.api('POST', 'UserRatings/Admin/Users/Remap', { query: { by: 'Name', dryRun: 'false', ifPlan: plan.PlanToken } }).then(function (done) {
                    usersReport.replaceChildren(kit.h('p', { class: 'jfur-data-summary', text: kit.t('data.remapped', { count: dataCount(done && done.MovedRecords) }) }));
                    loadUsers();
                    changed();
                });
            });
        }).then(null, function (e) {
            usersReport.replaceChildren(retryNotice(dataProblemText(e), remap));
            loadUsers();
        });
    }

    function applyPolicy(ids, button) {
        const orphans = (state.users && state.users.OrphanUsers) || [];
        const userIds = ids || orphans.map(function (u) { return u.UserId; });
        if (userIds.length === 0) {
            return;
        }

        const policy = (state.users && state.users.DeletedUserPolicy) || 'KeepAsOrphan';
        dataAsk({
            title: kit.t('data.policyTitle'),
            body: kit.h('p', { text: kit.t('data.policyQuestion', { policy: kit.t('data.policy.' + (['Delete', 'Anonymize', 'KeepAsOrphan'].indexOf(policy) >= 0 ? policy : 'KeepAsOrphan')), count: userIds.length }) }),
            confirm: kit.t('data.policyConfirm'),
            danger: policy === 'Delete',
            returnFocus: function () { return button && button.isConnected ? button : remapButton.isConnected && !kit.isHidden(usersActions) ? remapButton : usersReport; }
        }).then(function (yes) {
            if (!yes) {
                return null;
            }

            return kit.api('POST', 'UserRatings/Admin/Users/ApplyPolicy', { body: { UserIds: userIds } }).then(function (r) {
                usersReport.replaceChildren(kit.h('p', { class: 'jfur-data-summary', text: kit.t('data.policyDone', { count: dataCount(r && r.Affected) }) }));
                loadUsers();
                changed();
            });
        }).then(null, function (e) {
            usersReport.replaceChildren(retryNotice(dataProblemText(e), function () { applyPolicy(ids, button); }));
        });
    }

    // The first reads after mount returns, through the page's own timer (so importing and mounting send nothing at once).
    window.setTimeout(load, 0);

    return {
        refresh: function () {
            load();
        }
    };
}

// ------------------------------------------------------------------------------------------------- the detached list

function mountDetached(view) {
    kit.injectStyles();
    const state = { reason: '', startIndex: 0, total: 0, generation: 0 };
    const n = 'jfur-det-' + Math.floor(Math.random() * 1e9).toString(36);
    const reasons = dataChoices(n + '-reason', 'det.reason', [''].concat(DATA_REASONS), function (v) {
        return v ? kit.t('mod.reason' + v).replace(/^./, function (c) { return c.toUpperCase(); }) : kit.t('det.reasonAll');
    }, '', function (v) {
        state.reason = v;
        state.startIndex = 0;
        load();
    });
    const message = kit.h('div', { class: 'jfur-data-message' });
    // What a Relink or a Purge ended with, kept while the list is read again after it.
    const notice = kit.h('div', { class: 'jfur-data-message jfur-det-notice', role: 'status' });
    const list = kit.h('ul', { class: 'jfur-det-list' });
    const previous = kit.button(kit.t('mod.previous'), { className: 'jfur-det-previous', onClick: function () { page(-1); } });
    const next = kit.button(kit.t('mod.next'), { className: 'jfur-det-next', onClick: function () { page(1); } });
    const count = kit.h('span', { class: 'jfur-mod-count' });
    const pager = kit.h('div', { class: 'jfur-mod-pager jfur-det-pager' }, [previous, count, next]);
    const root = kit.h('div', { class: 'jfur-data jfur-det' }, [
        kit.h('p', { class: 'jfur-data-intro', text: kit.t('det.intro') }),
        reasons.node,
        notice,
        message,
        list,
        pager
    ]);
    view.replaceChildren(root);
    kit.setHidden(pager, true);

    function changed() {
        view.dispatchEvent(new CustomEvent('jfur-data-changed', { bubbles: true }));
    }

    function page(step) {
        const start = state.startIndex + step * DATA_DETACHED_PAGE;
        if (start < 0 || start >= state.total) {
            return;
        }

        state.startIndex = start;
        load(step > 0 ? next : previous);
    }

    // focusAfter: the control to focus once the list is drawn; true, or one the list replaced: the list's first button,
    // else the reasons (a row gone takes its buttons with it, and the focus must not fall to the page's body).
    function load(focusAfter) {
        const generation = ++state.generation;
        const query = { startIndex: state.startIndex, limit: DATA_DETACHED_PAGE };
        if (state.reason) {
            query.reason = state.reason;
        }

        kit.api('GET', 'UserRatings/Admin/Detached', { query: query }).then(function (page) {
            if (generation !== state.generation) {
                return;
            }

            const items = (page && page.Items) || [];
            state.total = dataCount(page && page.TotalRecordCount);
            if (items.length === 0 && state.startIndex > 0 && state.total > 0) {
                state.startIndex = Math.max(0, Math.floor((state.total - 1) / DATA_DETACHED_PAGE) * DATA_DETACHED_PAGE);
                load(focusAfter);
                return;
            }

            render(items);
            if (focusAfter && (focusAfter === true || !modFocus(focusAfter))) {
                modFocus(list.querySelector('button:not([disabled])')) || modFocus(reasons.buttons[0]);
            }
        }, function (e) {
            if (generation !== state.generation) {
                return;
            }

            list.replaceChildren();
            kit.setHidden(pager, true);
            message.replaceChildren(kit.notice({ kind: 'error', text: kit.t('data.loadFailed', { reason: (e && e.message) || '' }), action: { label: kit.t('data.retry'), onClick: function () { load(); } } }));
        });
    }

    function itemName(e) {
        const name = String(e.Name || '');
        if (e.SeriesName && e.Season !== null && e.Season !== undefined && e.Episode !== null && e.Episode !== undefined) {
            return e.SeriesName + ' ' + kit.t('mod.episode', { season: e.Season, episode: e.Episode }) + ' · ' + name;
        }

        const year = e.Year ? '(' + e.Year + ')' : '';
        return year && name.indexOf(year) < 0 ? name + ' ' + year : name;
    }

    function render(items) {
        message.replaceChildren();
        if (items.length === 0) {
            list.replaceChildren();
            kit.setHidden(pager, true);
            message.append(kit.notice({ kind: 'empty', text: kit.t(state.reason ? 'det.emptyReason' : 'det.empty') }));
            return;
        }

        list.replaceChildren.apply(list, items.map(row));
        kit.setHidden(pager, state.total <= DATA_DETACHED_PAGE);
        count.textContent = kit.t('mod.count', { from: state.startIndex + 1, to: state.startIndex + items.length, total: state.total });
        previous.disabled = state.startIndex === 0;
        next.disabled = state.startIndex + items.length >= state.total;
    }

    function row(e) {
        const member = e.UserName || kit.t('det.unknownMember');
        const title = itemName(e);
        const reason = DATA_REASONS.indexOf(e.DetachReason) >= 0 ? kit.t('mod.reason' + e.DetachReason) : String(e.DetachReason || '');
        const stars = modStars(e.Stars);
        const candidates = (e.Candidates || []).map(function (c) {
            const target = itemName({ Name: c.Name, Year: c.Year, SeriesName: c.SeriesName });
            const button = kit.button(kit.t('det.relink'), { className: 'jfur-det-relink', onClick: function () { relink(e, c, button); } });
            button.setAttribute('aria-label', kit.t('det.relinkLabel', { name: member, item: target }));
            button.setAttribute('data-jfur-item', c.ItemId);
            button.disabled = c.MemberRatesIt === true;
            return kit.h('li', { class: 'jfur-det-candidate' }, [
                kit.h('span', { class: 'jfur-det-candidate-text' }, [
                    modIsolated('span', 'jfur-det-candidate-name', target),
                    kit.h('span', { class: 'jfur-data-snapshot-meta', text: (kit.t('det.strength.' + (['Exact', 'Strong', 'Weak', 'None'].indexOf(c.Strength) >= 0 ? c.Strength : 'None'))) + (c.MemberRatesIt ? ' · ' + kit.t('det.ratesIt') : '') })
                ]),
                button
            ]);
        });
        const purge = kit.button(kit.t('det.purge'), { variant: 'danger', className: 'jfur-det-purge', onClick: function () { purgeRow(e, purge); } });
        purge.setAttribute('aria-label', kit.t('det.purgeLabel', { name: member, item: title }));
        return kit.h('li', { class: 'jfur-det-row', 'data-jfur-rating': e.Id }, [
            modIsolated('p', 'jfur-det-title', title),
            kit.h('p', { class: 'jfur-det-meta' }, modSentence('det.by', { name: modIsolated('span', null, member) }).concat([
                document.createTextNode(' · ' + (stars ? kit.t('mod.stars', { stars: stars }) : kit.t('mod.noStars')) + ', ' + kit.t(e.HasReview ? 'det.review' : 'det.noReview'))
            ])),
            kit.h('p', { class: 'jfur-det-meta', text: kit.t('det.when', { when: modWhen(e.DetachedUtc), reason: reason }) }),
            kit.h('p', { class: 'jfur-data-label', text: kit.t('det.candidates') }),
            candidates.length ? kit.h('ul', { class: 'jfur-det-candidates' }, candidates) : kit.h('p', { class: 'jfur-data-intro jfur-det-none', text: kit.t('det.noCandidates') }),
            kit.h('div', { class: 'jfur-data-actions' }, [purge])
        ]);
    }

    // After a change, the focus goes to the row in its place once the list is read again (or the filters).
    function focusNear(id) {
        return function () {
            return list.querySelector('[data-jfur-rating="' + String(id).replace(/["\\]/g, '') + '"] button:not([disabled])') || list.querySelector('button:not([disabled])') || reasons.buttons[0];
        };
    }

    function problem(e) {
        if (e && e.status === 404) {
            return kit.t('det.gone');
        }

        if (e && e.code === 'AlreadyRated') {
            return kit.t('det.alreadyRated');
        }

        if (e && e.status === 400) {
            return kit.t('det.notRelinkable', { reason: e.message || '' });
        }

        return dataProblemText(e);
    }

    function relink(e, c, button) {
        notice.replaceChildren();
        const member = e.UserName || kit.t('det.unknownMember');
        dataAsk({
            title: kit.t('det.relinkTitle'),
            body: kit.h('p', null, modSentence('det.relinkQuestion', {
                name: modIsolated('span', null, member),
                from: modIsolated('span', null, itemName(e)),
                to: modIsolated('span', null, itemName({ Name: c.Name, Year: c.Year, SeriesName: c.SeriesName }))
            })),
            confirm: kit.t('det.relink'),
            returnFocus: function () { return button.isConnected ? button : focusNear(e.Id)(); }
        }).then(function (yes) {
            if (!yes) {
                return null;
            }

            return kit.api('POST', 'UserRatings/Admin/Detached/' + encodeURIComponent(e.Id) + '/Relink', { body: { ItemId: c.ItemId } }).then(function () {
                kit.toast(kit.t('det.relinked'));
                changed();
                load(focusNear(e.Id)());
            });
        }).then(null, function (err) {
            notice.replaceChildren(kit.notice({ kind: 'error', text: problem(err) }));
            if (err && (err.status === 404 || err.code === 'AlreadyRated')) {
                load(true);
            }
        });
    }

    function purgeRow(e, button) {
        notice.replaceChildren();
        const member = e.UserName || kit.t('det.unknownMember');
        dataAsk({
            title: kit.t('det.purgeTitle'),
            body: kit.h('p', null, modSentence('det.purgeQuestion', { name: modIsolated('span', null, member), item: modIsolated('span', null, itemName(e)) })),
            confirm: kit.t('det.purgeConfirm'),
            danger: true,
            returnFocus: function () { return button.isConnected ? button : focusNear(e.Id)(); }
        }).then(function (yes) {
            if (!yes) {
                return null;
            }

            return kit.api('DELETE', 'UserRatings/Admin/Detached/' + encodeURIComponent(e.Id)).then(function () {
                kit.toast(kit.t('det.purged'));
                changed();
                const after = list.querySelector('[data-jfur-rating="' + String(e.Id).replace(/["\\]/g, '') + '"]');
                const neighbour = after && (after.nextElementSibling || after.previousElementSibling);
                load((neighbour && neighbour.querySelector('button:not([disabled])')) || true);
            });
        }).then(null, function (err) {
            notice.replaceChildren(kit.notice({ kind: 'error', text: problem(err) }));
            if (err && err.status === 404) {
                load(true);
            }
        });
    }

    window.setTimeout(load, 0);

    return {
        refresh: function () {
            load();
        }
    };
}
