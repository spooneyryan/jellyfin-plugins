using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// Admin moderation: the Moderation list, a moderator's <i>Remove text only</i>
/// and <i>Remove rating and review</i>, and the moderation log.
/// </summary>
/// <remarks>
/// <para><b>Who sees what</b>: an API key, which has no user, works over the whole server; a signed-in admin has no
/// library bypass, so the list and the removals cover the attached rows on items <c>GetItemById&lt;BaseItem&gt;(id,
/// admin)</c> returns (the kit's <c>EligibleIds</c>, parents included), and the detached rows only for an admin with every
/// library (<c>EnableAllFolders</c>), since a detached row has no item left to check. A rating the admin can't moderate is
/// the same 404 as one that doesn't exist. Rows the Hide settings keep from members are listed. The moderation log
/// follows the same rule: a signed-in admin gets no row about an item they can't
/// open, neither its ids nor its <c>Details</c>' item ids, and the total counts only the rows they get.</para>
/// <para><b>A removal is two calls</b>: a dry run (the default) answers what it takes and a <c>PreviewToken</c> over the row
/// as it is (its times, stars, text, spoiler flag, item, author and state) and the kind of removal; the real run must send
/// it back as <c>ifPreview</c>, and is a 409 <c>Changed</c>, with nothing written and no snapshot, when the row changed in
/// between (an edit, a move, another moderator), so it never removes what the dry run didn't show. The real run takes a
/// pre-operation snapshot (<c>moderate</c>, kept 7 days) first, then in one unit of work checks the token again, removes
/// (Remove text only clears the review and keeps the stars, a new <c>UpdatedUtc</c>, no tombstone; on a review without
/// stars, and for Remove rating and review, the row is deleted with a <c>Moderator</c> tombstone so no import brings it
/// back) and writes one moderation-log row with ids, a reason code and counts only (<c>{"Stars","Reviews","Deleted"}</c>;
/// never the text, never a name: <c>TextSnapshot</c> stays empty). After the commit the archive's earlier lines of the
/// rating go (<see cref="ArchivedLinePurges"/>, with no copy) and a checkpoint of the database's write-ahead log is asked
/// for (<see cref="RatingsCheckpoint"/>; it runs in the background), so once it is done the removed text is in neither the
/// database, its <c>-wal</c> nor the archive (the kit's <c>secure_delete</c> zeroes the
/// freed pages). Only the snapshot taken before keeps it, for its 7 days.</para>
/// <para>Logs name ratings, items and users by id; names and text never reach them.</para>
/// </remarks>
internal sealed partial class RatingsModeration
{
    /// <summary>The longest search term.</summary>
    public const int MaxSearchLength = 200;

    /// <summary>The 404 for a rating that doesn't exist or that the caller can't moderate (the same either way).</summary>
    public const string NotFoundMessage = "This rating wasn't found, or you can't moderate it.";

    /// <summary>The 400 for a real run without the dry run's token.</summary>
    public const string PreviewRequiredMessage = "A removal needs its dry run first: send the dry run's PreviewToken back as ifPreview with dryRun=false.";

    /// <summary>The 400 for a token that isn't one.</summary>
    public const string PreviewMalformedMessage = "ifPreview isn't a PreviewToken (m1- and 20 hex digits).";

    /// <summary>The 409 for a rating that changed since its dry run.</summary>
    public const string PreviewChangedMessage = "Changed: this rating changed since the dry run, so nothing was removed. Look at it again before removing it.";

    /// <summary>The 409 for Remove text only on a rating without a review.</summary>
    public const string NoReviewMessage = "This rating has no review to remove; remove the rating instead (textOnly=false).";

    /// <summary>The <c>code</c> of the 409 <see cref="PreviewChangedMessage"/>: the rating changed since the dry run (the
    /// Remove dialogs read the row again on it; since 0.3.18, by this code, not the words).</summary>
    public const string ChangedCode = "Changed";

    /// <summary>The <c>code</c> of the 409 <see cref="NoReviewMessage"/>.</summary>
    public const string NoReviewCode = "NoReview";

    /// <summary>The 400 for a moderation-log action this version doesn't know.</summary>
    public const string ActionMessage = "action isn't a moderation-log action this version knows.";

    /// <summary>The 400 for a search term that is too long.</summary>
    public static readonly string SearchMessage = string.Create(CultureInfo.InvariantCulture, $"searchTerm is longer than {MaxSearchLength} characters.");

    private const string TokenPrefix = "m1-";

    /// <summary>How long the list reuses what a signed-in admin's checks of their items found (<see cref="VisibleFor"/>).</summary>
    internal static readonly TimeSpan VisibleCacheTtl = TimeSpan.FromMinutes(1);

    private readonly IRatingsStore _store;
    private readonly ItemAccess _access;
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly ArchivedLinePurges _archivedLines;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, VisibleCache> _visible = [];
    private (long Generation, string Path)? _lastSnapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsModeration"/> class.
    /// </summary>
    /// <param name="store">The ratings store.</param>
    /// <param name="access">Item access, for a signed-in admin's items.</param>
    /// <param name="users">The user manager: the admin, the authors' names and states.</param>
    /// <param name="library">The library manager, for an item's name in the log of an API key's request.</param>
    /// <param name="archivedLines">The archive's lines of a rating whose review is removed.</param>
    /// <param name="clock">The clock of the write times.</param>
    /// <param name="logger">The logger.</param>
    public RatingsModeration(IRatingsStore store, ItemAccess access, IUserManager users, ILibraryManager library, ArchivedLinePurges archivedLines, TimeProvider clock, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(archivedLines);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _access = access;
        _users = users;
        _library = library;
        _archivedLines = archivedLines;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// The Moderation list: the rows the filters match that the caller may moderate, the row changed last first.
    /// </summary>
    /// <param name="adminId">The signed-in admin, or null for an API key.</param>
    /// <param name="query">The filters and the page.</param>
    /// <returns>The page and the total.</returns>
    /// <exception cref="PluginException">400 a search term over <see cref="MaxSearchLength"/> characters; 503.</exception>
    public QueryResult<UserRatingsAdminEntryDto> List(Guid? adminId, ModerationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var search = query.SearchTerm?.Trim();
        if (search is { Length: > MaxSearchLength })
        {
            throw PluginException.Invalid(SearchMessage);
        }

        var start = Math.Max(0, query.StartIndex);
        var take = Math.Clamp(query.Limit ?? UserRatingsApi.DefaultLimit, 1, UserRatingsApi.MaxLimit);
        var admin = Caller(adminId);
        if (adminId is not null && admin is null)
        {
            return new QueryResult<UserRatingsAdminEntryDto>(start, 0, []);
        }

        // A signed-in admin's item id resolves as theirs: an item they can't open lists nothing.
        if (admin is not null && query.ItemId is { } itemId && _access.GetVisible(itemId, admin) is null)
        {
            return new QueryResult<UserRatingsAdminEntryDto>(start, 0, []);
        }

        bool? detached = query.State switch
        {
            UserRatingsModerationState.Attached => false,
            UserRatingsModerationState.Detached => true,
            _ => null
        };
        DateTime? from = query.SinceUtc is { } since ? RatingsService.ToMilliseconds(since) : null;
        var authors = Authors();

        // An API key's list without a search needs nothing the database can't do: it reads the page and the count only.
        // Otherwise every row the filters match is read, then narrowed and paged here.
        if (admin is null && string.IsNullOrEmpty(search))
        {
            var (paged, count) = _store.GetForModeration(query.UserId, query.ItemId, query.HasText, detached, from, start, take);
            return new QueryResult<UserRatingsAdminEntryDto>(start, count, [.. paged.Select(r => Entry(r, authors))]);
        }

        var listed = _store.GetForModeration(query.UserId, query.ItemId, query.HasText, detached, from, 0, null).Rows.ToList();
        if (admin is not null)
        {
            listed = Moderatable(admin, listed, cached: true);
        }

        if (!string.IsNullOrEmpty(search))
        {
            listed = listed.Where(r => Matches(r, authors.NameOf(r.UserId), search)).ToList();
        }

        var page = listed.Skip(start).Take(take).Select(r => Entry(r, authors)).ToList();
        return new QueryResult<UserRatingsAdminEntryDto>(start, listed.Count, page);
    }

    /// <summary>
    /// A moderator's removal of a rating's review (<paramref name="textOnly"/>, the default) or of the whole rating: a dry run
    /// by default, which answers the <c>PreviewToken</c> the real run needs.
    /// </summary>
    /// <param name="adminId">The signed-in admin, or null for an API key.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="textOnly">Whether only the review goes (true when null).</param>
    /// <param name="reason">The reason code, or null.</param>
    /// <param name="dryRun">Whether to only report (true when null).</param>
    /// <param name="ifPreview">The dry run's <c>PreviewToken</c>; required for a real run.</param>
    /// <param name="cancellationToken">Cancels waiting for the database.</param>
    /// <returns>What the removal takes or took.</returns>
    /// <exception cref="PluginException">400 a real run without a well-formed <paramref name="ifPreview"/>; 404 no such rating
    /// or one the caller can't moderate; 409 <see cref="NoReviewMessage"/>, or <see cref="PreviewChangedMessage"/> (nothing
    /// written); 503 the data is read-only or busy (not in a dry run).</exception>
    public async Task<UserRatingsModerationDto> RemoveAsync(Guid? adminId, Guid ratingId, bool? textOnly, UserRatingsModerationReason? reason, bool? dryRun, string? ifPreview, CancellationToken cancellationToken)
    {
        var dry = dryRun ?? true;
        var textOnlyRun = textOnly ?? true;
        var token = ifPreview?.Trim();
        if (!dry)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw PluginException.Invalid(PreviewRequiredMessage);
            }

            if (!TokenFormat().IsMatch(token))
            {
                throw PluginException.Invalid(PreviewMalformedMessage);
            }
        }

        var admin = Caller(adminId);
        if ((adminId is not null && admin is null) || _store.Get(ratingId) is not { } row || (admin is not null && Moderatable(admin, [row]).Count == 0))
        {
            throw PluginException.NotFound(NotFoundMessage);
        }

        if (textOnlyRun && row.ReviewText is null)
        {
            throw new PluginException(PluginErrorKind.Conflict, NoReviewMessage) { Code = NoReviewCode };
        }

        var archive = _archivedLines.Look();
        var answer = Preview(row, textOnlyRun, Authors(), archive.Mentions(row.Id));
        if (dry)
        {
            return answer;
        }

        _store.ThrowIfReadOnly();
        if (!string.Equals(token, answer.PreviewToken, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginException(PluginErrorKind.Conflict, PreviewChangedMessage) { Code = ChangedCode };
        }

        var snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var action = textOnlyRun ? RatingValues.ModerationAction.RemoveText : RatingValues.ModerationAction.RemoveEntry;
        var (logId, recorded) = await _store.WriteAsync(
            w =>
            {
                // The token again, under the writer: a change since the dry run (or since the check above) writes nothing.
                if (w.Get(ratingId) is not { } current || !string.Equals(TokenOf(current, textOnlyRun), answer.PreviewToken, StringComparison.Ordinal))
                {
                    throw new PluginException(PluginErrorKind.Conflict, PreviewChangedMessage) { Code = ChangedCode };
                }

                var now = WriteTime(current);
                var deletes = !textOnlyRun || current.Stars2 is null;
                if (deletes)
                {
                    w.Delete(current.Id, RatingValues.DeletedBy.Moderator, now);
                }
                else
                {
                    w.ClearReview(current.Id, now);
                }

                var details = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{{\"Stars\":{(deletes && current.Stars2 is not null ? 1 : 0)},\"Reviews\":{(current.ReviewText is not null ? 1 : 0)},\"Deleted\":{(deletes ? 1 : 0)}}}");
                var id = w.AppendModerationLog(new ModerationLogRecord
                {
                    AtUtc = now,
                    ActorUserId = adminId,
                    Action = action,
                    RatingId = current.Id,
                    TargetUserId = current.UserId,
                    ItemId = current.ItemId,
                    Reason = reason?.ToString(),
                    Details = details
                });
                return (id, ArchivedLinePurges.Record(w, archive, [current.Id]));
            },
            cancellationToken).ConfigureAwait(false);

        // Committed: the archive's earlier lines of the rating, then the write-ahead log, so the text is in neither. A rewrite
        // that fails stays pending for the daily maintenance; the removal is done either way.
        if (recorded > 0)
        {
            try
            {
                await _archivedLines.RunAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Moderation: the archive lines of rating {RatingId} stay pending; the daily maintenance takes them out", ratingId);
            }
        }

        // The checkpoint runs in the background: the answer never waits for it.
        RatingsCheckpoint.RequestAfter(_store, _logger, "the moderator's removal of rating " + ratingId.ToString("N"));

        _logger.LogInformation(
            "Moderation: {Action} on rating {RatingId} of user {UserId} on item {ItemId} by {ActorId} (log {LogId})",
            action,
            ratingId,
            row.UserId,
            row.ItemId,
            adminId?.ToString() ?? "an API key",
            logId);
        answer.DryRun = false;
        answer.LogId = logId;
        answer.SafetySnapshot = snapshot;
        return answer;
    }

    /// <summary>
    /// A page of the moderation log, newest first, with the names read now: for an API key every row; for a signed-in admin
    /// only the rows about items they may see (<see cref="LogRowsFor"/>), the total counting
    /// only those.
    /// </summary>
    /// <param name="adminId">The signed-in admin, or null for an API key.</param>
    /// <param name="startIndex">Rows to skip.</param>
    /// <param name="limit">Rows to return (50 by default, at most 200).</param>
    /// <param name="action">Only rows of this action (in any case), or null for all.</param>
    /// <returns>The page and the total.</returns>
    /// <exception cref="PluginException">400 an action this version doesn't know; 503.</exception>
    public QueryResult<UserRatingsModerationLogDto> Log(Guid? adminId, int startIndex, int? limit, string? action)
    {
        string? known = null;
        if (!string.IsNullOrWhiteSpace(action))
        {
            known = RatingValues.ModerationAction.All.FirstOrDefault(a => string.Equals(a, action.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw PluginException.Invalid(ActionMessage);
        }

        var start = Math.Max(0, startIndex);
        var take = Math.Clamp(limit ?? UserRatingsApi.DefaultLimit, 1, UserRatingsApi.MaxLimit);
        IReadOnlyList<ModerationLogRecord> rows;
        int total;
        var admin = Caller(adminId);
        if (adminId is null)
        {
            (rows, total) = _store.GetModerationLogPage(start, take, known);
        }
        else if (admin is null)
        {
            return new QueryResult<UserRatingsModerationLogDto>(start, 0, []);
        }
        else
        {
            // One read of every row the filter matches, so the page and the total agree; then the rows the admin may see,
            // then the page.
            var (every, _) = _store.GetModerationLogPage(0, int.MaxValue, known);
            var kept = LogRowsFor(admin, every);
            total = kept.Count;
            rows = [.. kept.Skip(start).Take(take)];
        }

        var authors = Authors();
        var page = rows.Select(r => new UserRatingsModerationLogDto
        {
            Id = r.Id,
            AtUtc = r.AtUtc,
            ActorUserId = r.ActorUserId,
            ActorName = r.ActorUserId is { } actor ? authors.CurrentName(actor) : null,
            Action = r.Action,
            RatingId = r.RatingId,
            TargetUserId = r.TargetUserId,
            TargetName = r.TargetUserId is { } target ? authors.NameOf(target) : null,
            ItemId = r.ItemId,
            ItemName = r.ItemId is { } item ? ItemName(item, admin, adminId is not null) : null,
            Reason = r.Reason,
            Details = r.Details
        }).ToList();
        return new QueryResult<UserRatingsModerationLogDto>(start, total, page);
    }

    // The token over the row as the dry run saw it and the kind of removal: any change of the row since makes another.
    internal static string TokenOf(RatingRecord row, bool textOnly)
    {
        var text = row.ReviewText is null ? "-" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.ReviewText)));
        var canonical = string.Join(
            '|',
            "v1",
            row.Id.ToString("N", CultureInfo.InvariantCulture),
            row.UserId.ToString("N", CultureInfo.InvariantCulture),
            row.ItemId.ToString("N", CultureInfo.InvariantCulture),
            row.Stars2?.ToString(CultureInfo.InvariantCulture) ?? "-",
            text,
            row.IsSpoiler ? "1" : "0",
            Ticks(row.UpdatedUtc),
            row.ReviewUpdatedUtc is { } review ? Ticks(review) : "-",
            row.DetachedUtc is { } detached ? Ticks(detached) : "-",
            textOnly ? "text" : "entry");
        return TokenPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)), 0, 10).ToLowerInvariant();
    }

    [GeneratedRegex("^m1-[0-9a-fA-F]{20}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenFormat();

    private static string Ticks(DateTime utc) => RatingsService.ToMilliseconds(utc).Ticks.ToString(CultureInfo.InvariantCulture);

    private static bool Matches(RatingRecord row, string? author, string search)
        => Contains(author, search) || Contains(row.Identity.Name, search) || Contains(row.Identity.SeriesName, search) || Contains(row.ReviewText, search);

    private static bool Contains(string? text, string search) => text is not null && text.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private static UserRatingsAdminEntryDto Entry(RatingRecord row, AuthorBook authors)
    {
        var (kind, hidden, disabled) = authors.StateOf(row.UserId);
        return new UserRatingsAdminEntryDto
        {
            RatingId = row.Id,
            UserId = row.UserId,
            UserName = authors.NameOf(row.UserId),
            AuthorKind = kind.ToString(),
            IsHiddenUser = hidden,
            IsDisabledUser = disabled,
            ItemId = row.ItemId,
            ItemName = row.Identity.Name,
            ItemKind = row.Identity.Kind,
            ProductionYear = row.Identity.Year,
            SeriesName = row.Identity.SeriesName,
            SeasonNumber = row.Identity.Season,
            EpisodeNumber = row.Identity.Episode,
            Stars = row.Stars2 is { } s ? s / 2.0 : null,
            Text = row.ReviewText,
            IsSpoiler = row.IsSpoiler,
            CreatedUtc = row.CreatedUtc,
            UpdatedUtc = row.UpdatedUtc,
            Edited = row.EditCount > 0,
            State = row.IsAttached ? nameof(UserRatingsModerationState.Attached) : nameof(UserRatingsModerationState.Detached),
            DetachReason = row.DetachReason,
            DetachedUtc = row.DetachedUtc
        };
    }

    private static UserRatingsModerationDto Preview(RatingRecord row, bool textOnly, AuthorBook authors, bool archiveLines)
    {
        var deletes = !textOnly || row.Stars2 is null;
        return new UserRatingsModerationDto
        {
            DryRun = true,
            RatingId = row.Id,
            Action = textOnly ? RatingValues.ModerationAction.RemoveText : RatingValues.ModerationAction.RemoveEntry,
            UserId = row.UserId,
            UserName = authors.NameOf(row.UserId),
            ItemId = row.ItemId,
            ItemName = row.Identity.Name,
            RemovesStars = deletes && row.Stars2 is not null,
            RemovesReview = row.ReviewText is not null,
            DeletesRating = deletes,
            ArchiveLines = archiveLines,
            PreviewToken = TokenOf(row, textOnly)
        };
    }

    // The signed-in admin, or null (an API key, or an id that is no user any more: the callers tell them apart).
    private User? Caller(Guid? adminId) => adminId is { } id ? _users.GetUserById(id) : null;

    // The rows a signed-in admin may moderate: attached ones on items they can open, detached ones only with every
    // library. cached: the list's pages, which reuse what this admin's last minute of pages found (VisibleFor); a removal
    // checks its row afresh.
    private List<RatingRecord> Moderatable(User admin, IReadOnlyList<RatingRecord> rows, bool cached = false)
    {
        var attached = rows.Where(r => r.IsAttached).Select(r => r.ItemId).Distinct().ToList();
        var visible = attached.Count == 0 ? (IReadOnlySet<Guid>)new HashSet<Guid>() : cached ? VisibleFor(admin, attached) : _access.VisibleIds(admin, attached);
        var allLibraries = admin.HasPermission(PermissionKind.EnableAllFolders);
        return rows.Where(r => r.IsAttached ? visible.Contains(r.ItemId) : allLibraries).ToList();
    }

    // Which of the items the admin can open, for the list: what the
    // admin's checks found in the last VisibleCacheTtl is reused while the admin's account is unchanged (AccountOf:
    // User.RowVersion moves with every save, a library access or permission change among them), and only the items not checked yet are
    // asked about; so paging through the list asks about each item once a minute, not once a page.
    private IReadOnlySet<Guid> VisibleFor(User admin, IReadOnlyList<Guid> itemIds)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        VisibleCache cache;
        lock (_lock)
        {
            var account = AccountOf(admin);
            if (!_visible.TryGetValue(admin.Id, out var found) || !string.Equals(found.Account, account, StringComparison.Ordinal) || now - found.Since > VisibleCacheTtl || now < found.Since)
            {
                found = new VisibleCache(account, now, new Dictionary<Guid, bool>());
                _visible[admin.Id] = found;
            }

            cache = found;
        }

        List<Guid> unknown;
        lock (_lock)
        {
            unknown = [.. itemIds.Where(id => !cache.Items.ContainsKey(id))];
        }

        if (unknown.Count > 0)
        {
            var visible = _access.VisibleIds(admin, unknown);
            lock (_lock)
            {
                foreach (var id in unknown)
                {
                    cache.Items[id] = visible.Contains(id);
                }
            }
        }

        lock (_lock)
        {
            return itemIds.Where(id => cache.Items.TryGetValue(id, out var open) && open).ToHashSet();
        }
    }

    // What decides the admin's items: the account's RowVersion (it moves with every save), every library or not, and the
    // libraries chosen.
    private static string AccountOf(User admin)
        => string.Join(
            '|',
            admin.RowVersion.ToString(CultureInfo.InvariantCulture),
            admin.HasPermission(PermissionKind.EnableAllFolders) ? "all" : "some",
            string.Join(',', admin.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders)));

    /// <summary>
    /// The items a moderation-log row is about: its <c>ItemId</c> and every id in its <c>Details</c> under a name ending in
    /// <c>ItemId</c> (a Relink's <c>FromItemId</c>).
    /// </summary>
    /// <param name="row">The row.</param>
    /// <returns>The ids, and whether its <c>Details</c> couldn't be read (so it may name an item no check can see).</returns>
    internal static (IReadOnlyList<Guid> Items, bool Unreadable) ItemsOf(ModerationLogRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var ids = new List<Guid>();
        if (row.ItemId is { } item && item != Guid.Empty)
        {
            ids.Add(item);
        }

        if (string.IsNullOrWhiteSpace(row.Details))
        {
            return (ids, false);
        }

        try
        {
            using var details = JsonDocument.Parse(row.Details);
            if (details.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (ids, false);
            }

            foreach (var property in details.RootElement.EnumerateObject())
            {
                if (property.Name.EndsWith("ItemId", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(property.Value.GetString(), out var id)
                    && id != Guid.Empty)
                {
                    ids.Add(id);
                }
            }

            return (ids, false);
        }
        catch (JsonException)
        {
            return (ids, true);
        }
    }

    // The log rows a signed-in admin may see (by the Moderatable rule): a row is kept
    // only when every item it is about (ItemsOf) is one they can open, or, for an admin with every library, one the library
    // no longer has (a detached rating's item, whose rows go only to them as the list's detached rows do). A row about no
    // item (a deleted or remapped user) is kept; one whose Details can't be read goes only to an admin with every library.
    private List<ModerationLogRecord> LogRowsFor(User admin, IReadOnlyList<ModerationLogRecord> rows)
    {
        var about = rows.Select(r => (Row: r, Of: ItemsOf(r))).ToList();
        var ids = about.SelectMany(a => a.Of.Items).Distinct().ToList();
        var visible = ids.Count == 0 ? (IReadOnlySet<Guid>)new HashSet<Guid>() : _access.VisibleIds(admin, ids);
        var allLibraries = admin.HasPermission(PermissionKind.EnableAllFolders);
        var gone = new Dictionary<Guid, bool>();
        bool Shown(Guid id)
        {
            if (visible.Contains(id))
            {
                return true;
            }

            if (!allLibraries)
            {
                return false;
            }

            if (!gone.TryGetValue(id, out var isGone))
            {
                isGone = _library.GetItemById(id) is null;
                gone[id] = isGone;
            }

            return isGone;
        }

        return [.. about.Where(a => (!a.Of.Unreadable || allLibraries) && a.Of.Items.All(Shown)).Select(a => a.Row)];
    }

    private string? ItemName(Guid itemId, User? admin, bool signedIn)
    {
        if (signedIn)
        {
            return admin is null ? null : _access.GetVisible(itemId, admin)?.Name;
        }

        return _library.GetItemById(itemId)?.Name;
    }

    /// <summary>
    /// A real run's pre-operation snapshot (<c>moderate</c>), reused while nothing was written since it was taken and its
    /// file is still there (as <see cref="RatingsUserDataOwner.SnapshotBeforeAsync"/> does: a
    /// snapshot the rotation or its expiry deleted is never named as the one before).
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for another snapshot.</param>
    /// <returns>The snapshot's file name, or null when none was written (read-only).</returns>
    internal async Task<string?> SnapshotAsync(CancellationToken cancellationToken)
    {
        var generation = _store.Generation;
        lock (_lock)
        {
            if (_lastSnapshot is { } last && last.Generation == generation && System.IO.File.Exists(last.Path))
            {
                return System.IO.Path.GetFileName(last.Path);
            }
        }

        if (await _store.PreOpSnapshotAsync(SnapshotOperations.Moderate, cancellationToken).ConfigureAwait(false) is not { } path)
        {
            return null;
        }

        lock (_lock)
        {
            _lastSnapshot = (generation, path);
        }

        return System.IO.Path.GetFileName(path);
    }

    private DateTime WriteTime(RatingRecord row) => RatingsService.WriteTimeAfter(_clock.GetUtcNow().UtcDateTime, row.UpdatedUtc);

    private AuthorBook Authors()
        => new(KitUsers.GetAll(_users).Where(u => u is not null).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First()), _store.GetFormerUsers());

    /// <summary>
    /// The Moderation list's filters and page.
    /// </summary>
    internal sealed record ModerationQuery
    {
        /// <summary>Gets only this member's rows.</summary>
        public Guid? UserId { get; init; }

        /// <summary>Gets only the rows on this item.</summary>
        public Guid? ItemId { get; init; }

        /// <summary>Gets only the rows changed at or after this time.</summary>
        public DateTime? SinceUtc { get; init; }

        /// <summary>Gets only rows with (true) or without (false) a review.</summary>
        public bool? HasText { get; init; }

        /// <summary>Gets which rows by state.</summary>
        public UserRatingsModerationState? State { get; init; }

        /// <summary>Gets a text the author's name, the item's or series' name or the review holds (any case).</summary>
        public string? SearchTerm { get; init; }

        /// <summary>Gets the rows to skip.</summary>
        public int StartIndex { get; init; }

        /// <summary>Gets the rows to return (50 by default, at most 200).</summary>
        public int? Limit { get; init; }
    }

    // The server's users now and the former members, for the names and states of one request.
    private sealed class AuthorBook(IReadOnlyDictionary<Guid, User> users, IReadOnlyDictionary<Guid, string> formers)
    {
        public string? CurrentName(Guid id) => users.TryGetValue(id, out var user) ? user.Username : null;

        public string? NameOf(Guid id) => CurrentName(id) ?? (formers.TryGetValue(id, out var name) ? name : null);

        public (AuthorKind Kind, bool Hidden, bool Disabled) StateOf(Guid id)
            => users.TryGetValue(id, out var user)
                ? (AuthorKind.Member, user.HasPermission(PermissionKind.IsHidden), user.HasPermission(PermissionKind.IsDisabled))
                : (formers.ContainsKey(id) ? AuthorKind.FormerMember : AuthorKind.Orphan, false, false);
    }

    // What VisibleFor found for one admin: their account then (AccountOf), when the minute began, and each item checked.
    private sealed record VisibleCache(string Account, DateTime Since, Dictionary<Guid, bool> Items);
}
