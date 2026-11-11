// Move's words: a short human title for every code the server answers with (MoveErrorCodes), shown
// above the server's own sentence, which names the files, folders and items; the kinds, folder badges, the reasons a
// library folder can't be chosen, and the methods. The server's sentence is always shown as it is (text only); a code
// this table doesn't know still shows it, under a neutral title. Web tests check every MoveErrorCodes code has a title.

const MOVE_CODE_TITLES = Object.freeze({
    // Item checks.
    'move.not-eligible': 'This item can\'t be moved',
    'move.not-found': 'The item isn\'t there',
    'move.item-busy': 'Another operation is using this item',
    'move.unsupported-os': 'Move media works on Windows servers only',
    // Destination guards.
    'move.destination-invalid': 'This destination can\'t be used',
    'move.collision': 'Something of that name is there already',
    'move.into-movie-folder': 'That is another movie\'s own folder',
    'move.episode-needs-series': 'An episode needs a series folder',
    'move.foreign-media': 'The folder holds other videos',
    'move.reparse-point': 'A junction or symbolic link is in the way',
    'move.extras-folder-name': 'Jellyfin would read that folder as extras',
    'move.cross-volume-later': 'Moving to another drive comes later',
    'move.not-writable': 'The server can\'t write there',
    'move.insufficient-space': 'Not enough free space',
    // Checks that change.
    'move.playing': 'Someone is playing it',
    'move.in-use': 'A file is in use',
    // Plans and jobs (the run part).
    'move.plan-stale': 'The preview is out of date',
    'move.plan-expired': 'The preview expired',
    'move.queue-full': 'Too many moves are waiting',
    'move.past-commit': 'The move can\'t be cancelled now',
    'move.job-state': 'The move isn\'t in a state for that',
    'move.rollback-unavailable': 'It can\'t be rolled back',
    'move.confirm-required': 'This needs a confirmation',
    'move.moveback-unavailable': 'It can\'t be moved back',
    // Warnings.
    'move.scan-running': 'A library scan is running',
    'move.new-series': 'This makes a new series',
    'move.episode-other-series': 'The episode moves to another series',
    'move.episode-other-season': 'The episode moves to another season',
    'move.episode-season-from-name': 'The season will come from the folder\'s name',
    'move.sidecar-left-behind': 'A file that looks related stays behind',
    'move.images-refetched': 'Some images will be fetched again'
});

const MOVE_KIND_TEXT = Object.freeze({ Movie: 'Movie', Episode: 'Episode', MusicVideo: 'Music video' });

// The folder browser's badges (GET /Move/Folders Kind).
const MOVE_FOLDER_BADGES = Object.freeze({
    MovieFolder: 'Movie folder',
    Series: 'Series',
    Season: 'Season',
    Ignored: 'Ignored by Jellyfin',
    ReparsePoint: 'Link (not allowed)'
});

// Why a library folder can't be chosen (GET /Move/Destinations Locations[].Reason).
const MOVE_LOCATION_REASONS = Object.freeze({
    'cross-volume-later': 'It is on another drive. Moving between drives comes with series and season moves, in a later version.',
    'network-location': 'It is a network folder. Move works on the server\'s own drives only.',
    'device-path': 'It is a device path. Move works on drive folders only.',
    'not-a-local-path': 'It isn\'t a folder on one of the server\'s drives.',
    unreachable: 'The server can\'t reach it now.',
    ignored: 'Jellyfin ignores it (a .ignore file, or a name it skips).',
    'reparse-point': 'It is a junction or symbolic link, and Move doesn\'t follow links.'
});

const MOVE_METHOD_TEXT = Object.freeze({
    Rename: 'Instant rename (the same drive)',
    CopyVerifyDelete: 'Copy, verify, then delete the originals (another drive)'
});

function moveCodeTitle(code) {
    return Object.prototype.hasOwnProperty.call(MOVE_CODE_TITLES, code) ? MOVE_CODE_TITLES[code] : 'Move can\'t do this';
}

function moveKindText(kind) {
    return Object.prototype.hasOwnProperty.call(MOVE_KIND_TEXT, kind) ? MOVE_KIND_TEXT[kind] : String(kind || 'Item');
}

function moveLocationReason(reason) {
    return Object.prototype.hasOwnProperty.call(MOVE_LOCATION_REASONS, reason)
        ? MOVE_LOCATION_REASONS[reason]
        : 'It can\'t be chosen (' + String(reason) + ').';
}

function movePlural(count, one, many) {
    return count + ' ' + (count === 1 ? one : many);
}

// An error of one of the routes as { title, detail, retry }: the server's sentence as the detail, and whether asking
// again may help (a network failure, a busy item, a file in use; not a refusal that lasts).
function moveErrorInfo(error) {
    const status = error && typeof error.status === 'number' ? error.status : 0;
    const message = (error && error.message) || 'Something went wrong.';
    if (status === 0) {
        return { title: 'The server could not be reached', detail: message, retry: true };
    }

    if (error.isFeatureDisabled) {
        return { title: 'Move media is turned off', detail: 'An administrator turned it off in Library Maintenance\'s settings.', retry: false };
    }

    if (error.code) {
        return { title: moveCodeTitle(error.code), detail: message, retry: error.code === 'move.item-busy' || error.code === 'move.not-writable' };
    }

    if (status === 403) {
        return { title: 'Only administrators can move media', detail: message, retry: false };
    }

    if (status === 404) {
        return { title: 'Not found', detail: message, retry: false };
    }

    if (status === 400) {
        return { title: 'This destination can\'t be used', detail: message, retry: false };
    }

    if (status === 401) {
        return { title: 'Your session expired', detail: message, retry: false };
    }

    return { title: 'The server couldn\'t answer', detail: message, retry: true };
}
