// User Ratings' member API, thin wrappers over kit.api: every call is the signed-in member's
// own (the server takes the user from the token, never from a parameter). Errors are the kit's KitApiError, whose
// message is the ProblemDetails' detail; what each answer means for the page is 40-details.js's (detailsFailure).
// Capabilities are read once per user and kept for RATINGS_CAPABILITIES_TTL_MS, so the display settings an admin changes
// show on the next details view a little later; a failed read keeps the defaults for RATINGS_CAPABILITIES_RETRY_MS only.

const RATINGS_CAPABILITIES_TTL_MS = 30 * 1000;
const RATINGS_CAPABILITIES_RETRY_MS = 10 * 1000;
// What the page assumes when Capabilities can't be read (the settings' defaults); the item's own answer still decides what
// the member may do (Can).
const RATINGS_DEFAULT_CAPABILITIES = Object.freeze({
    Enabled: true,
    MaxReviewLength: 5000,
    AllowSpoilerFlag: true,
    Display: Object.freeze({ SectionExpandedByDefault: true, ShowHistogram: true, MinRatingsForAverage: 1, ReviewsPageSize: 20 })
});

const ratingsCache = { capabilities: null, key: null, at: 0, failed: false, pending: null };

function ratingsItemPath(itemId, rest) {
    return 'UserRatings/Items/' + encodeURIComponent(String(itemId)) + (rest || '');
}

function ratingsUserKey() {
    const client = kit.client();
    return client && typeof client.getCurrentUserId === 'function' ? String(client.getCurrentUserId() || '') : '';
}

const ratingsApi = {
    // GET /UserRatings/Capabilities: what the member may do and the display settings; the defaults above when it fails.
    capabilities: function () {
        const key = ratingsUserKey();
        const ttl = ratingsCache.failed ? RATINGS_CAPABILITIES_RETRY_MS : RATINGS_CAPABILITIES_TTL_MS;
        if (ratingsCache.capabilities && ratingsCache.key === key && Date.now() - ratingsCache.at < ttl) {
            return Promise.resolve(ratingsCache.capabilities);
        }

        if (ratingsCache.pending && ratingsCache.pending.key === key) {
            return ratingsCache.pending.promise;
        }

        const pending = {
            key: key,
            promise: kit.api('GET', 'UserRatings/Capabilities').then(function (caps) {
                return { caps: caps || RATINGS_DEFAULT_CAPABILITIES, failed: false };
            }, function (e) {
                kit.report(e);
                return { caps: RATINGS_DEFAULT_CAPABILITIES, failed: true };
            }).then(function (result) {
                ratingsCache.capabilities = result.caps;
                ratingsCache.failed = result.failed;
                ratingsCache.key = key;
                ratingsCache.at = Date.now();
                if (ratingsCache.pending === pending) {
                    ratingsCache.pending = null;
                }

                return result.caps;
            })
        };
        ratingsCache.pending = pending;
        return pending.promise;
    },
    // GET /UserRatings/Items/{id}: the summary, the member's own row, the first page of the others' rows, the other
    // copies and Can; 404 for an item the member can't open or rate, 403 while ratings are off.
    item: function (itemId, query) {
        return kit.api('GET', ratingsItemPath(itemId), { query: query });
    },
    // GET /UserRatings/Items/{id}/Reviews: a page of the other members' rows; 403 while reviews (or ratings) are off.
    reviews: function (itemId, query) {
        return kit.api('GET', ratingsItemPath(itemId, '/Reviews'), { query: query });
    },
    // PUT …/Mine/Rating: the member's stars (0.5 to 5 in halves); the item read again after the write.
    setRating: function (itemId, stars) {
        return kit.api('PUT', ratingsItemPath(itemId, '/Mine/Rating'), { body: { Stars: stars } });
    },
    // DELETE …/Mine/Rating: clears the stars, a review stays (a row without one is deleted).
    clearRating: function (itemId) {
        return kit.api('DELETE', ratingsItemPath(itemId, '/Mine/Rating'));
    },
    // PUT …/Mine/Review: { Text, IsSpoiler, IfUpdatedUtc }; 409 when the row changed since IfUpdatedUtc.
    setReview: function (itemId, body) {
        return kit.api('PUT', ratingsItemPath(itemId, '/Mine/Review'), { body: body });
    },
    // DELETE …/Mine: the member's rating and review (204, no body).
    deleteMine: function (itemId) {
        return kit.api('DELETE', ratingsItemPath(itemId, '/Mine'));
    },
    // DELETE /UserRatings/Admin/Reviews/{id}, an admin's only: the dry run (what "Remove text
    // only" or "Remove rating and review" would remove, and its PreviewToken), then the removal, which takes that token
    // (a 409 when the row changed since) and an optional reason code.
    moderationPreview: function (ratingId, textOnly) {
        return kit.api('DELETE', 'UserRatings/Admin/Reviews/' + encodeURIComponent(String(ratingId)), { query: { textOnly: textOnly ? 'true' : 'false' } });
    },
    // GET /UserRatings/Admin/Reviews?userId&itemId: the member's rows on the item as moderation sees them, to read a row
    // again after a removal's 409 Changed.
    moderationRows: function (userId, itemId) {
        return kit.api('GET', 'UserRatings/Admin/Reviews', { query: { userId: String(userId), itemId: String(itemId), limit: 200 } });
    },
    moderate: function (ratingId, textOnly, reason, token) {
        return kit.api('DELETE', 'UserRatings/Admin/Reviews/' + encodeURIComponent(String(ratingId)), {
            query: { textOnly: textOnly ? 'true' : 'false', reason: reason || null, dryRun: 'false', ifPreview: token }
        });
    },
    // Forgets the capabilities (a sign-out, a session that expired), so the next read asks the server.
    forget: function () {
        ratingsCache.capabilities = null;
        ratingsCache.key = null;
        ratingsCache.at = 0;
        ratingsCache.failed = false;
        ratingsCache.pending = null;
    }
};

document.addEventListener(kit.prefix + ':session-expired', function () {
    ratingsApi.forget();
});
