// Move media's data: the signed-in user's capabilities, kept for the menu's synchronous when(ctx), and
// the server's eligibility of items, asked for in batches and kept for a while. Every route answers administrators only
// (RequiresElevation); a viewer's script never asks (kit.user.isAdmin() first), and the menu shows nothing to them.

const MOVE_ROUTE = KIT_BUILD.route + '/Move';
const MOVE_CAPABILITIES_TTL_MS = 60 * 1000;
const MOVE_ELIGIBILITY_BATCH_MS = 50;
const MOVE_ELIGIBILITY_MAX_IDS = 100;
const MOVE_ELIGIBILITY_TTL_MS = 5 * 60 * 1000;

// What a viewer, a signed-out page or a failed request leaves: everything off.
const MOVE_NO_CAPABILITIES = Object.freeze({
    IsAdmin: false,
    MoveEnabled: false,
    MoveSupported: false,
    CanPlanMove: false,
    CanMove: false,
    ShowMoveInItemMenu: false,
    ShowMoveDetailPageButton: false,
    ShowMoveInTvLayout: false
});

// The codes of an item that can't move for good (until the library changes), which hide the menu entry while kept; any
// other answer (busy in a Duplicates merge, a request that failed) leaves the entry to the dialog, which says why.
const MOVE_INELIGIBLE_CODES = Object.freeze(['move.not-eligible', 'move.not-found', 'move.reparse-point']);

const moveData = {
    user: null,
    caps: null,
    capsAt: 0,
    capsLoading: null,
    // id -> { at, eligible, code, reason, unitKind, ownerItemId }
    eligibility: {},
    // id -> [resolve], the ids waiting for the next batch or its answer
    waiting: {},
    queue: [],
    timer: null,
    requests: 0
};

// The capabilities known now (null until the first answer for this user).
function moveCapabilities() {
    return moveData.user && moveData.user === kit.user.id() ? moveData.caps : null;
}

// Whether the item menu gets the entry: an administrator, Move on and supported here, and its menu setting on.
function moveMenuOn() {
    const caps = moveCapabilities();
    return !!(caps && caps.IsAdmin && caps.MoveEnabled && caps.CanPlanMove && caps.ShowMoveInItemMenu);
}

// GET /LibraryMaintenance/Capabilities, at most once a minute unless forced; a viewer's page never asks.
function moveLoadCapabilities(force) {
    const user = kit.user.id();
    if (!user) {
        return Promise.resolve(null);
    }

    if (!force && moveData.user === user && moveData.caps && Date.now() - moveData.capsAt < MOVE_CAPABILITIES_TTL_MS) {
        return Promise.resolve(moveData.caps);
    }

    if (moveData.capsLoading && moveData.capsLoading.user === user) {
        return moveData.capsLoading.promise;
    }

    const promise = kit.user.isAdmin().then(function (admin) {
        return admin ? kit.api('GET', KIT_BUILD.route + '/Capabilities') : MOVE_NO_CAPABILITIES;
    }).then(function (caps) {
        return caps && typeof caps === 'object' ? caps : MOVE_NO_CAPABILITIES;
    }, function () {
        // A failed request keeps what was known (the next viewshow asks again); with nothing known, everything is off.
        return moveData.user === user && moveData.caps ? moveData.caps : MOVE_NO_CAPABILITIES;
    }).then(function (caps) {
        if (moveData.capsLoading && moveData.capsLoading.promise === promise) {
            moveData.capsLoading = null;
        }

        if (kit.user.id() === user) {
            moveData.user = user;
            moveData.caps = caps;
            moveData.capsAt = Date.now();
        }

        return caps;
    });
    moveData.capsLoading = { user: user, promise: promise };
    return promise;
}

// Another user (or none): nothing known is theirs.
function moveForgetUser() {
    moveData.user = null;
    moveData.caps = null;
    moveData.capsAt = 0;
    moveData.capsLoading = null;
    moveData.eligibility = {};
}

// An item id as a key: jellyfin-web writes ids without dashes, the server's DTOs with them.
function moveIdKey(id) {
    return String(id || '').replace(/-/g, '').toLowerCase();
}

// The server's eligibility of an item while kept, else null.
function moveEligibilityOf(id) {
    const known = id ? moveData.eligibility[moveIdKey(id)] : null;
    if (!known || Date.now() - known.at > MOVE_ELIGIBILITY_TTL_MS) {
        return null;
    }

    return known;
}

// Whether the entry is hidden for an item: the server said it can't move, for a reason that lasts.
function moveKnownIneligible(id) {
    const known = moveEligibilityOf(id);
    return !!(known && !known.eligible && MOVE_INELIGIBLE_CODES.indexOf(known.code) >= 0);
}

function moveRemember(dto) {
    if (!dto || !dto.ItemId) {
        return;
    }

    moveData.eligibility[moveIdKey(dto.ItemId)] = {
        at: Date.now(),
        eligible: dto.Eligible === true,
        code: dto.Code || null,
        reason: dto.Reason || null,
        unitKind: dto.UnitKind || null,
        ownerItemId: dto.OwnerItemId || null
    };
}

function moveSettle(id, value) {
    const resolvers = moveData.waiting[id] || [];
    delete moveData.waiting[id];
    resolvers.forEach(function (resolve) {
        resolve(value);
    });
}

// GET /LibraryMaintenance/Move/Eligibility?ids=… for the queued ids: one request per 100, never one per card.
function moveFlushEligibility() {
    moveData.timer = null;
    const ids = moveData.queue.splice(0, moveData.queue.length);
    const user = kit.user.id();
    for (let start = 0; start < ids.length; start += MOVE_ELIGIBILITY_MAX_IDS) {
        const batch = ids.slice(start, start + MOVE_ELIGIBILITY_MAX_IDS);
        moveData.requests++;
        kit.api('GET', MOVE_ROUTE + '/Eligibility', { query: { ids: batch.join(',') } }).then(function (answers) {
            if (kit.user.id() !== user) {
                batch.forEach(function (id) {
                    moveSettle(id, null);
                });
                return;
            }

            (Array.isArray(answers) ? answers : []).forEach(moveRemember);
            batch.forEach(function (id) {
                moveSettle(id, moveEligibilityOf(id));
            });
        }, function () {
            // Not kept: the menu falls back to its synchronous rule and the dialog to the server's answer.
            batch.forEach(function (id) {
                moveSettle(id, null);
            });
        });
    }
}

// The eligibility of these ids: kept answers at once, the others in the next batch (50 ms). Resolves { id: entry|null }.
function moveLookupEligibility(ids) {
    const unique = [];
    (ids || []).forEach(function (id) {
        if (id && unique.indexOf(String(id)) < 0) {
            unique.push(String(id));
        }
    });
    return Promise.all(unique.map(function (id) {
        const known = moveEligibilityOf(id);
        if (known) {
            return Promise.resolve(known);
        }

        return new Promise(function (resolve) {
            if (!moveData.waiting[id]) {
                moveData.waiting[id] = [];
                moveData.queue.push(id);
            }

            moveData.waiting[id].push(resolve);
            if (moveData.timer === null) {
                moveData.timer = setTimeout(moveFlushEligibility, MOVE_ELIGIBILITY_BATCH_MS);
            }
        });
    })).then(function (values) {
        const result = {};
        unique.forEach(function (id, index) {
            result[id] = values[index];
        });
        return result;
    });
}

// What the dialog learnt from Source: a 409 for an item that can't move hides the entry like an Eligibility answer.
function moveRememberRefusal(id, error) {
    if (error && error.status === 409 && error.code && MOVE_INELIGIBLE_CODES.indexOf(error.code) >= 0) {
        moveRemember({ ItemId: id, Eligible: false, Code: error.code, Reason: error.message });
    } else if (error && error.status === 404) {
        moveRemember({ ItemId: id, Eligible: false, Code: 'move.not-found', Reason: error.message });
    }
}

// The dialog's routes.
function moveGetSource(itemId) {
    return kit.api('GET', MOVE_ROUTE + '/Source', { query: { itemId: itemId } });
}

function moveGetDestinations(itemId) {
    return kit.api('GET', MOVE_ROUTE + '/Destinations', { query: { itemId: itemId } });
}

function moveGetFolders(query, signal) {
    return kit.api('GET', MOVE_ROUTE + '/Folders', { query: query, signal: signal });
}

function movePostPlan(body, signal) {
    return kit.api('POST', MOVE_ROUTE + '/Plan', { body: body, signal: signal });
}
