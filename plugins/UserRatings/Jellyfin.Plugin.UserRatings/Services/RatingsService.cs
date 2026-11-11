using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// A member's ratings and reviews of an item: the read of a details page
/// and every write a member makes, each write one unit of work (<see cref="IRatingsStore.WriteAsync{T}"/>).
/// </summary>
/// <remarks>
/// <para><b>The order of the checks</b> for a create or an edit: ratings on at all (403, the feature off), the
/// store writable (503, before any lookup), the item visible and rateable (one 404 for both, so existence doesn't leak),
/// <c>WhoCanRate</c> (403 "Rating is limited to selected members"), <c>RequirePlayedToRate</c> (403), and for a review
/// the reviews switch (403, the feature off); then the stars or the text themselves (400). Clearing and deleting one's own rows skips <c>WhoCanRate</c> and
/// <c>RequirePlayedToRate</c> (everyone may delete their own rows); <c>DELETE Mine/{ratingId}</c> skips the item too.</para>
/// <para><b>Inside the transaction</b>: the member's row is read, <c>IfUpdatedUtc</c> compared (409),
/// and the write keyed by member and item, so a double click or two tabs is last-write-wins; <c>EditCount</c> is counted in
/// the same statement. The unit of work returns the write's generation; the store's <c>Changed</c> then drops the item's
/// summary-cache entry.</para>
/// <para><b>Write times</b> are whole milliseconds, what a JavaScript <c>Date</c> holds, so the
/// <c>UpdatedUtc</c> a web client read comes back as <c>IfUpdatedUtc</c> unchanged ("…13.804Z" for a stored "…13.8048619Z"
/// was a 409 every time). <c>IfUpdatedUtc</c> is compared at the millisecond too (also with a row written before), in any
/// form JSON gives it: "Z", "+00:00" or another offset, or none (taken as UTC). A row's new time is never at or before its
/// last one (one millisecond later then), so of two writes within one millisecond the later still has the later time and a
/// window holding the earlier one gets its 409.</para>
/// </remarks>
internal sealed class RatingsService
{
    /// <summary>The 403 of a member <c>WhoCanRate</c> leaves out.</summary>
    public const string NotARaterMessage = "Rating is limited to selected members.";

    /// <summary>The 403 while ratings are off (<c>EnableRatings</c>).</summary>
    public const string RatingsOffMessage = "Ratings are turned off on this server.";

    /// <summary>The 403 for a review while reviews are off (<c>EnableReviews</c>).</summary>
    public const string ReviewsOffMessage = "Reviews are turned off on this server.";

    /// <summary>The 403 while <c>RequirePlayedToRate</c> is on and the member hasn't played the item.</summary>
    public const string NotPlayedMessage = "Play this first: rating it needs it to be played.";

    /// <summary>The 503 when whether the member played the item couldn't be read.</summary>
    public const string PlayedUnknownMessage = "Whether you've played this couldn't be read just now; try again in a moment.";

    /// <summary>The 409 of a review edited elsewhere.</summary>
    public const string ChangedElsewhereMessage = "Your rating or review was changed in another window. Reload to see it.";

    /// <summary>
    /// The 409 of a review write whose review precondition doesn't hold (<c>IfReviewUpdatedUtc</c>, <c>IfNoReview</c>):
    /// the review was written, changed or deleted in another window.
    /// </summary>
    public const string ReviewChangedElsewhereMessage = "Your review was changed in another window. Reload to see it.";

    /// <summary>The one 404 of <c>DELETE Mine/{ratingId}</c>, for a rating that isn't there and one that isn't the caller's.</summary>
    public const string NotYoursMessage = "No rating of yours has that id.";

    /// <summary><c>Can.Reason</c> while the ratings data is read-only.</summary>
    public const string ReadOnlyReason = "ReadOnly";

    /// <summary><c>Can.Reason</c> when <c>WhoCanRate</c> leaves the member out.</summary>
    public const string NotARaterReason = "NotARater";

    /// <summary><c>Can.Reason</c> while <c>RequirePlayedToRate</c> is on and the member hasn't played the item.</summary>
    public const string NeedsPlayReason = "NeedsPlay";

    /// <summary>
    /// <c>Can.Reason</c> for an item the library no longer has although <c>GetItemById</c> still answers it (a cache ghost of
    /// a deleted folder): every member write on it is the one 404.
    /// </summary>
    public const string NotInLibraryReason = "NotInLibrary";

    private readonly IRatingsStore _store;
    private readonly SummaryCache _cache;
    private readonly ItemAccess _access;
    private readonly CopySiblings _siblings;
    private readonly PlayedCheck _played;
    private readonly RatingsSettingsReader _settings;
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly ArchivedLinePurges? _archivedLines;

    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsService"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="cache">The summary cache.</param>
    /// <param name="access">Item access.</param>
    /// <param name="siblings">The "rated on another copy" hint.</param>
    /// <param name="played">The played check.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="users">The user manager (authors' states, per request).</param>
    /// <param name="library">The library manager (identity snapshots).</param>
    /// <param name="clock">The clock of the write times.</param>
    /// <param name="logger">The logger; none by default.</param>
    /// <param name="archivedLines">The archive's lines of ratings whose review goes: a member's delete, review
    /// clear or edit with other words of a rating archived once and put back since takes them out; none by default (tests of
    /// the live table).</param>
    public RatingsService(
        IRatingsStore store,
        SummaryCache cache,
        ItemAccess access,
        CopySiblings siblings,
        PlayedCheck played,
        RatingsSettingsReader settings,
        IUserManager users,
        ILibraryManager library,
        TimeProvider clock,
        ILogger<RatingsService>? logger = null,
        ArchivedLinePurges? archivedLines = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(siblings);
        ArgumentNullException.ThrowIfNull(played);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _cache = cache;
        _access = access;
        _siblings = siblings;
        _played = played;
        _settings = settings;
        _users = users;
        _library = library;
        _clock = clock;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _archivedLines = archivedLines;
    }

    /// <summary>
    /// Makes the visibility policy of a request: the settings' Hide switches, the server's users now, the former members.
    /// </summary>
    /// <param name="settings">The request's settings.</param>
    /// <returns>The policy.</returns>
    public VisibilityPolicy Policy(RatingsSettings settings)
        => VisibilityPolicy.Create(settings, KitUsers.GetAll(_users), _store.GetFormerUsers());

    /// <summary>
    /// Reads an item's ratings for a viewer (<c>GET Items/{itemId}</c>). The Hide settings,
    /// the authors' states and <c>RateableItemTypes</c> are applied here, after the summary cache, on every read: the cache
    /// holds raw rows, so a settings change needs no cache call.
    /// </summary>
    /// <param name="viewer">The viewer.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="isModerator">Whether the viewer may remove others' entries: Jellyfin's <c>RequiresElevation</c> policy
    /// for the request (the kit's <c>CurrentUser.IsElevatedAsync</c>), never the stored flag.</param>
    /// <returns>The summary, the viewer's own row, the other rows the viewer may see (newest change first; none while
    /// reviews are off), the other copies, what the viewer may do, and the request's settings and policy.</returns>
    /// <exception cref="PluginException">403 while ratings are off; 404 when the item isn't visible or rateable.</exception>
    public ItemRatings GetItem(User viewer, Guid itemId, bool isModerator)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var settings = _settings.Read();
        RequireRatingsOn(settings);
        var item = _access.GetRateable(itemId, viewer, settings);
        var policy = Policy(settings);
        var rows = _store.GetAttachedForItem(item.Id);
        var summary = policy.Summarize(_cache.Get(item.Id));
        var identity = ItemIdentity.Capture(item, _library);
        return new ItemRatings(
            item,
            summary,
            rows.FirstOrDefault(r => r.UserId == viewer.Id),
            settings.EnableReviews ? policy.Others(rows, viewer.Id) : [],
            _siblings.Find(identity, viewer, policy),
            Capabilities(viewer, item, settings, isModerator))
        {
            Settings = settings,
            Policy = policy
        };
    }

    /// <summary>
    /// Reads the other members' rows of an item a viewer may see (<c>GET Items/{itemId}/Reviews</c>: "excludes
    /// own"), newest change first.
    /// </summary>
    /// <param name="viewer">The viewer.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The rows, with the request's settings and policy (for the authors' names).</returns>
    /// <exception cref="PluginException">403 while ratings or reviews are off (no review list then); 404 when
    /// the item isn't visible or rateable.</exception>
    public (IReadOnlyList<RatingRecord> Rows, RatingsSettings Settings, VisibilityPolicy Policy) GetReviews(User viewer, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var settings = _settings.Read();
        RequireRatingsOn(settings);
        var item = _access.GetRateable(itemId, viewer, settings);
        if (!settings.EnableReviews)
        {
            throw PluginException.FeatureDisabled(ReviewsOffMessage);
        }

        var policy = Policy(settings);
        return (policy.Others(_store.GetAttachedForItem(item.Id), viewer.Id), settings, policy);
    }

    /// <summary>
    /// Gets the member's attached rows that the item routes can't reach under the current settings
    /// (<c>Mine/Inaccessible</c>), so a member can always find and delete their own rows: rows on
    /// items <c>GetItemById&lt;BaseItem&gt;(id, user)</c> doesn't return (a library taken away, a parental limit, a parent
    /// that hides the item; the list check of <see cref="ItemAccess.VisibleIds"/>), rows of a kind no longer in
    /// <c>RateableItemTypes</c>, and, while ratings are off (every item route answers 403), all of the member's rows. Only the
    /// member's own data: the caller maps the stars, a snippet of the text and the dates, never the item. Works whatever
    /// <c>WhoCanRate</c> and the switches say, as <see cref="DeleteOwnRowAsync"/> does.
    /// </summary>
    /// <param name="user">The member.</param>
    /// <returns>The rows, newest change first. An id the list check left out beyond its per-item limit counts as not
    /// visible, so it is listed: the list errs towards rows the member may delete, and shows nothing but their own data.</returns>
    public IReadOnlyList<RatingRecord> GetInaccessible(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var settings = _settings.Read();
        var rows = _store.GetAttachedForUser(user.Id);
        if (rows.Count == 0 || !settings.EnableRatings)
        {
            return rows;
        }

        // A row's kind is its item's: an item's id comes from its type and path, so the same id never changes kind.
        var rateable = rows.Where(r => IsRateableKind(r, settings)).ToList();
        var visible = _access.VisibleIds(user, rateable.Select(r => r.ItemId).Distinct().ToList());
        return rows.Where(r => !visible.Contains(r.ItemId) || !IsRateableKind(r, settings)).ToList();
    }

    /// <summary>
    /// Sets the member's stars on an item (<c>PUT Mine/Rating</c>).
    /// </summary>
    /// <param name="user">The member.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="stars">The stars, 0.5 to 5 in half steps; none is the same 400 as a wrong value, after the same checks.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The member's row and the write's generation.</returns>
    /// <exception cref="PluginException">400 invalid stars; 403 ratings off, not a rater, not played; 404; 503 read-only
    /// or busy.</exception>
    public async Task<RatingWrite> SetStarsAsync(User user, Guid itemId, decimal? stars, CancellationToken cancellationToken)
    {
        var (item, _) = AuthorizeEdit(user, itemId, review: false);
        var value = stars is { } given ? StarValue.FromStars(given) : throw PluginException.Invalid(StarValue.InvalidMessage);
        var identity = ItemIdentity.Capture(item, _library);

        // The time is read inside the unit of work, under the writer lock, so UpdatedUtc follows the commits' order: of two
        // clicks, the one that commits last has the later time as well as the last word, also within one millisecond.
        return await _store.WriteAsync(
            w =>
            {
                // The member's last seen name, for Remap by name after a restore onto a rebuilt server.
                RatingsUserDataOwner.RecordName(w, user.Id, user.Username);
                return new RatingWrite(w.SaveStars(user.Id, identity, value.Stars2, WriteTime(w.GetAttached(user.Id, item.Id))), w.Generation);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the member's review of an item (<c>PUT Mine/Review</c>).
    /// </summary>
    /// <param name="user">The member.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="text">The review as sent; normalised (<see cref="ReviewText"/>).</param>
    /// <param name="isSpoiler">Whether it contains spoilers; kept only while <c>AllowSpoilerFlag</c> is on.</param>
    /// <param name="ifUpdatedUtc">The member's row's <c>UpdatedUtc</c> as the client last saw it, or <c>null</c> to
    /// overwrite whatever is there.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The member's row and the write's generation.</returns>
    /// <exception cref="PluginException">400 empty or too long; 403 ratings or reviews off, not a rater, not played; 404;
    /// 409 the row changed since <paramref name="ifUpdatedUtc"/> (or was deleted); 503.</exception>
    public Task<RatingWrite> SetReviewAsync(User user, Guid itemId, string? text, bool isSpoiler, DateTime? ifUpdatedUtc, CancellationToken cancellationToken)
        => SetReviewAsync(user, itemId, text, isSpoiler, new ReviewPrecondition(ifUpdatedUtc, null, false), cancellationToken);

    /// <summary>
    /// Sets the member's review of an item (<c>PUT Mine/Review</c>) under one optional precondition:
    /// the whole row's <c>UpdatedUtc</c> (<see cref="ReviewPrecondition.IfUpdatedUtc"/>), or
    /// the review's own: its <c>ReviewUpdatedUtc</c> (<see cref="ReviewPrecondition.IfReviewUpdatedUtc"/>, which
    /// moves only when the text or the spoiler flag changes, so a change of the stars elsewhere never makes a review write a
    /// 409, and a flag changed elsewhere does) or "no review
    /// yet" (<see cref="ReviewPrecondition.IfNoReview"/>, so two windows writing a first review can't write over each other).
    /// </summary>
    /// <param name="user">The member.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="text">The review as sent; normalised (<see cref="ReviewText"/>).</param>
    /// <param name="isSpoiler">Whether it contains spoilers; kept only while <c>AllowSpoilerFlag</c> is on.</param>
    /// <param name="precondition">At most one precondition (<see cref="ReviewPrecondition.None"/> overwrites whatever is
    /// there).</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The member's row and the write's generation.</returns>
    /// <exception cref="PluginException">400 empty or too long, or two preconditions; 403 ratings or reviews off, not a rater,
    /// not played; 404; 409 the precondition doesn't hold (the row or its review changed, or went, or a review is there
    /// already); 503.</exception>
    public async Task<RatingWrite> SetReviewAsync(User user, Guid itemId, string? text, bool isSpoiler, ReviewPrecondition precondition, CancellationToken cancellationToken)
    {
        var (item, settings) = AuthorizeEdit(user, itemId, review: true);
        var normalized = ReviewText.Validate(text, settings.MaxReviewLength);
        precondition.Validate();
        var identity = ItemIdentity.Capture(item, _library);
        var spoiler = isSpoiler && settings.AllowSpoilerFlag;
        var archive = LookAtArchive();
        var recorded = 0;
        Guid? replaced = null;
        var write = await _store.WriteAsync(
            w =>
            {
                recorded = 0;
                replaced = null;
                var existing = w.GetAttached(user.Id, item.Id);
                if (precondition.IfUpdatedUtc is { } expected && (existing is null || ToMilliseconds(existing.UpdatedUtc) != ToMilliseconds(expected)))
                {
                    throw PluginException.Conflict(ChangedElsewhereMessage);
                }

                // The review's own time (ReviewUpdatedUtc moves with the text and the spoiler flag): a review deleted
                // elsewhere, written again with other words or marked otherwise is the 409; the stars changed elsewhere are not.
                if (precondition.IfReviewUpdatedUtc is { } review
                    && (existing?.ReviewText is null || existing.ReviewUpdatedUtc is not { } stored || ToMilliseconds(stored) != ToMilliseconds(review)))
                {
                    throw PluginException.Conflict(ReviewChangedElsewhereMessage);
                }

                if (precondition.IfNoReview && existing?.ReviewText is not null)
                {
                    throw PluginException.Conflict(ReviewChangedElsewhereMessage);
                }

                // Other words replace a review: the archive's earlier lines of a
                // rating archived once and put back since hold the old words, so they go as a removed review's do, and the
                // checkpoint after the commit takes the old words' page out of the -wal.
                if (existing?.ReviewText is { } old && !string.Equals(old, normalized, StringComparison.Ordinal))
                {
                    replaced = existing.Id;
                    recorded = ArchivedLinePurges.Record(w, archive, [existing.Id]);
                }

                RatingsUserDataOwner.RecordName(w, user.Id, user.Username);
                return new RatingWrite(w.SaveReview(user.Id, identity, normalized, spoiler, WriteTime(existing)), w.Generation);
            },
            cancellationToken).ConfigureAwait(false);
        if (replaced is { } ratingId)
        {
            await PurgeArchivedLinesAsync(recorded).ConfigureAwait(false);
            RequestCheckpointAfter("the member's review edit of rating", ratingId);
        }

        return write;
    }

    /// <summary>
    /// Clears the member's stars on an item (<c>DELETE Mine/Rating</c>): a row with a review keeps it, a row
    /// without one is deleted with a tombstone; no stars, no change.
    /// </summary>
    /// <param name="user">The member.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The member's row (or <c>null</c>) and the generation.</returns>
    /// <exception cref="PluginException">403 ratings off; 404; 503.</exception>
    public Task<RatingWrite> ClearStarsAsync(User user, Guid itemId, CancellationToken cancellationToken)
        => RemovePartAsync(user, itemId, stars: true, cancellationToken);

    /// <summary>
    /// Clears the member's review of an item (<c>DELETE Mine/Review</c>): a row with stars keeps them, a row
    /// without is deleted with a tombstone; no review, no change.
    /// </summary>
    /// <param name="user">The member.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The member's row (or <c>null</c>) and the generation.</returns>
    /// <exception cref="PluginException">403 ratings off; 404; 503.</exception>
    public Task<RatingWrite> ClearReviewAsync(User user, Guid itemId, CancellationToken cancellationToken)
        => RemovePartAsync(user, itemId, stars: false, cancellationToken);

    /// <summary>
    /// Deletes the member's rating and review of an item, with a tombstone (<c>DELETE Mine</c>).
    /// </summary>
    /// <param name="user">The member.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>Whether a row was deleted, as a write with no row.</returns>
    /// <exception cref="PluginException">403 ratings off; 404; 503.</exception>
    public async Task<RatingWrite> DeleteMineAsync(User user, Guid itemId, CancellationToken cancellationToken)
    {
        var item = AuthorizeOwn(user, itemId);
        var archive = LookAtArchive();
        var recorded = 0;
        Guid? deleted = null;
        var write = await _store.WriteAsync(
            w =>
            {
                recorded = 0;
                deleted = null;
                if (w.GetAttached(user.Id, item.Id) is { } row)
                {
                    w.Delete(row.Id, RatingValues.DeletedBy.User, WriteTime(row));
                    recorded = ArchivedLinePurges.Record(w, archive, [row.Id]);
                    deleted = row.Id;
                }

                return new RatingWrite(null, w.Generation);
            },
            cancellationToken).ConfigureAwait(false);
        await PurgeArchivedLinesAsync(recorded).ConfigureAwait(false);
        if (deleted is { } ratingId)
        {
            RequestCheckpointAfter("the member's delete of rating", ratingId);
        }

        return write;
    }

    /// <summary>
    /// Deletes one of the member's own rows by its id, with no item lookup, so it works after the member lost access to the
    /// item (<c>DELETE Mine/{ratingId}</c>). Any member may, whatever <c>WhoCanRate</c> and the switches say.
    /// </summary>
    /// <param name="user">The member.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The write's generation.</returns>
    /// <exception cref="PluginException">404 (<see cref="NotYoursMessage"/>) unless the row exists and is the member's,
    /// the same either way; 503.</exception>
    public async Task<long> DeleteOwnRowAsync(User user, Guid ratingId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var archive = LookAtArchive();
        var recorded = 0;
        var generation = await _store.WriteAsync(
            w =>
            {
                if (w.Get(ratingId) is not { } row || row.UserId != user.Id)
                {
                    throw PluginException.NotFound(NotYoursMessage);
                }

                w.Delete(row.Id, RatingValues.DeletedBy.User, WriteTime(row));
                recorded = ArchivedLinePurges.Record(w, archive, [row.Id]);
                return w.Generation;
            },
            cancellationToken).ConfigureAwait(false);
        await PurgeArchivedLinesAsync(recorded).ConfigureAwait(false);
        RequestCheckpointAfter("the member's delete of rating", ratingId);
        return generation;
    }

    /// <summary>
    /// Cuts a time to the whole millisecond, in UTC: a time with an offset is converted, one without is taken as UTC.
    /// </summary>
    /// <param name="value">The time.</param>
    /// <returns>The UTC time, to the millisecond.</returns>
    internal static DateTime ToMilliseconds(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    private static bool IsRateableKind(RatingRecord row, RatingsSettings settings)
        => Enum.TryParse<BaseItemKind>(row.Identity.Kind, ignoreCase: false, out var kind) && settings.IsRateable(kind);

    private static void RequireRatingsOn(RatingsSettings settings)
    {
        if (!settings.EnableRatings)
        {
            throw PluginException.FeatureDisabled(RatingsOffMessage);
        }
    }

    /// <summary>
    /// A write's time for a row, a deletion's too: now, to the millisecond, or one millisecond after the row's own last time
    /// when that is now or later (two writes within one millisecond, a clock set back, a row dated ahead). So a deletion's
    /// tombstone is never older than the row it deleted, and a restore never takes the row as newer than its deletion.
    /// </summary>
    /// <param name="nowUtc">The clock's time.</param>
    /// <param name="lastUtc">The row's own last time; null for a new row.</param>
    /// <returns>The time to write.</returns>
    internal static DateTime WriteTimeAfter(DateTime nowUtc, DateTime? lastUtc)
    {
        var now = ToMilliseconds(nowUtc);
        if (lastUtc is not { } value)
        {
            return now;
        }

        var last = ToMilliseconds(value);
        return now > last ? now : last.AddMilliseconds(1);
    }

    // A write's time for the member's row (WriteTimeAfter).
    private DateTime WriteTime(RatingRecord? row) => WriteTimeAfter(_clock.GetUtcNow().UtcDateTime, row?.UpdatedUtc);

    // What the viewer may do on the item (Can): the checks of a write in their order, with the first that
    // fails as the reason. Whether the viewer played it is read only while RequirePlayedToRate is on and the rest allows;
    // when it can't be read just now, Rate stays true and the write gives its own 503.
    private ItemCapabilities Capabilities(User viewer, BaseItem item, RatingsSettings settings, bool isModerator)
    {
        var reason = _store.IsReadOnly ? ReadOnlyReason
            : !_access.IsInLibrary(item) ? NotInLibraryReason
            : !settings.MayRate(viewer.Id) ? NotARaterReason
            : settings.RequirePlayedToRate && _played.HasPlayed(item, viewer) == false ? NeedsPlayReason
            : null;
        var rate = reason is null;
        return new ItemCapabilities(rate, rate && settings.EnableReviews, isModerator, reason);
    }

    // The order for a create or an edit: switch, writable, visible + rateable, WhoCanRate, played, reviews.
    private (BaseItem Item, RatingsSettings Settings) AuthorizeEdit(User user, Guid itemId, bool review)
    {
        ArgumentNullException.ThrowIfNull(user);
        var settings = _settings.Read();
        RequireRatingsOn(settings);
        _store.ThrowIfReadOnly();
        var item = _access.GetWritable(itemId, user, settings);
        if (!settings.MayRate(user.Id))
        {
            throw PluginException.Forbidden(NotARaterMessage);
        }

        if (settings.RequirePlayedToRate)
        {
            switch (_played.HasPlayed(item, user))
            {
                case null:
                    throw PluginException.Unavailable(PlayedUnknownMessage);
                case false:
                    throw PluginException.Forbidden(NotPlayedMessage);
            }
        }

        if (review && !settings.EnableReviews)
        {
            throw PluginException.FeatureDisabled(ReviewsOffMessage);
        }

        return (item, settings);
    }

    // Removing one's own: the switch, writable, the item visible, rateable and in the library; not WhoCanRate, not played.
    private BaseItem AuthorizeOwn(User user, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(user);
        var settings = _settings.Read();
        RequireRatingsOn(settings);
        _store.ThrowIfReadOnly();
        return _access.GetWritable(itemId, user, settings);
    }

    private async Task<RatingWrite> RemovePartAsync(User user, Guid itemId, bool stars, CancellationToken cancellationToken)
    {
        var item = AuthorizeOwn(user, itemId);
        var archive = LookAtArchive();
        var recorded = 0;
        Guid? removed = null;
        var write = await _store.WriteAsync(
            w =>
            {
                recorded = 0;
                removed = null;
                var row = w.GetAttached(user.Id, item.Id);
                if (row is null || (stars ? row.Stars2 is null : row.ReviewText is null))
                {
                    return new RatingWrite(row, w.Generation, Changed: false);
                }

                var now = WriteTime(row);

                // Clearing the last of stars and text deletes the row, with its tombstone; a row deleted or a review
                // cleared takes the archive's earlier lines of the rating with it.
                if (stars ? row.ReviewText is null : row.Stars2 is null)
                {
                    w.Delete(row.Id, RatingValues.DeletedBy.User, now);
                    recorded = ArchivedLinePurges.Record(w, archive, [row.Id]);
                    removed = row.Id;
                    return new RatingWrite(null, w.Generation);
                }

                if (!stars)
                {
                    recorded = ArchivedLinePurges.Record(w, archive, [row.Id]);
                    removed = row.Id;
                }

                return new RatingWrite(stars ? w.ClearStars(row.Id, now) : w.ClearReview(row.Id, now), w.Generation);
            },
            cancellationToken).ConfigureAwait(false);
        if (!write.Changed)
        {
            _logger.LogDebug("Nothing to clear on item {ItemId} for user {UserId}", item.Id, user.Id);
        }

        await PurgeArchivedLinesAsync(recorded).ConfigureAwait(false);
        if (removed is { } ratingId)
        {
            RequestCheckpointAfter(stars ? "the member's delete of rating" : "the member's review clear of rating", ratingId);
        }

        return write;
    }

    // After the commit of a member's delete, review clear or review edit: the checkpoint, in
    // the background, so the old words' pages leave the -wal as a moderator's removal's do. The request never waits for it.
    private void RequestCheckpointAfter(string what, Guid ratingId)
        => RatingsCheckpoint.RequestAfter(_store, _logger, what + " " + ratingId.ToString("N"));

    private ArchivedLinePurges.ArchiveView LookAtArchive() => _archivedLines?.Look() ?? ArchivedLinePurges.ArchiveView.None;

    // After the commit: the archive's earlier lines of the rating the write recorded. The member's write is done, so
    // a rewrite that fails or waits too long only stays pending for the daily maintenance; it never fails the request, and a
    // client that goes away doesn't stop it.
    private async Task PurgeArchivedLinesAsync(int recorded)
    {
        if (recorded == 0 || _archivedLines is null)
        {
            return;
        }

        try
        {
            await _archivedLines.RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A removed review's archive lines stay pending; the daily maintenance takes them out");
        }
    }
}
