using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// <see cref="IUserRatingsApi"/> over <see cref="RatingsService"/>: the service decides
/// (the checks, the units of work, visibility), this maps its records to the DTOs, orders and pages the review lists, and
/// reads the authors' names and avatars when the request is made. Every write answers the item as it is after the write,
/// read again through the same checks; when that read is a 503, the answer is the write's own row, marked
/// <see cref="UserRatingsItemDto.IsPartial"/>. A 403 or 404 of that read is sent as it is: the write
/// was saved all the same.
/// </summary>
internal sealed class UserRatingsApi : IUserRatingsApi
{
    /// <summary>The page size of <c>Mine/Inaccessible</c> without a limit.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The largest page of any list.</summary>
    public const int MaxLimit = 200;

    /// <summary>How much of the member's own text <c>Mine/Inaccessible</c> shows (<c>TextSnippet</c>).</summary>
    public const int SnippetLength = 120;

    /// <summary>The key of the Top Rated page.</summary>
    public const string TopRatedPageKey = "toprated";

    // The usual kinds first, in this order, then any other kind an admin added, by name.
    private static readonly BaseItemKind[] _kindOrder =
    [
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Video,
        BaseItemKind.MusicVideo, BaseItemKind.MusicAlbum, BaseItemKind.Book, BaseItemKind.AudioBook
    ];

    private readonly RatingsService _service;
    private readonly RatingsSettingsReader _settings;
    private readonly IRatingsStore _store;
    private readonly IUserManager _users;
    private readonly IImageProcessor? _images;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserRatingsApi"/> class.
    /// </summary>
    /// <param name="service">The ratings service.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="store">The store (whether it is read-only).</param>
    /// <param name="users">The user manager (the authors' names, read per request).</param>
    /// <param name="images">Jellyfin's image processor (the authors' avatar tags); none leaves the tags out.</param>
    /// <param name="logger">The logger; none by default.</param>
    public UserRatingsApi(
        RatingsService service,
        RatingsSettingsReader settings,
        IRatingsStore store,
        IUserManager users,
        IImageProcessor? images = null,
        ILogger<UserRatingsApi>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(users);
        _service = service;
        _settings = settings;
        _store = store;
        _users = users;
        _images = images;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public UserRatingsCapabilitiesDto GetCapabilities(User user, bool isAdmin)
    {
        ArgumentNullException.ThrowIfNull(user);
        var settings = _settings.Read();
        var readOnly = _store.IsReadOnly;
        var canRate = settings.EnableRatings && settings.MayRate(user.Id) && !readOnly;
        return new UserRatingsCapabilitiesDto
        {
            Enabled = settings.EnableRatings,
            IsAdmin = isAdmin,
            CanRate = canRate,
            CanReview = canRate && settings.EnableReviews,
            RequirePlayedToRate = settings.EnableRatings && settings.RequirePlayedToRate,
            MaxReviewLength = settings.MaxReviewLength,
            AllowSpoilerFlag = settings.AllowSpoilerFlag,
            RateableItemTypes = settings.EnableRatings ? KindNames(settings.RateableItemTypes) : [],
            Display = new UserRatingsDisplayDto
            {
                ShowAverageChip = settings.ShowAverageChip,
                ShowDetailButton = settings.ShowDetailButton.ToString(),
                SectionExpandedByDefault = settings.SectionExpandedByDefault,
                ShowHistogram = settings.ShowHistogram,
                MinRatingsForAverage = settings.MinRatingsForAverage,
                ReviewsPageSize = settings.ReviewsPageSize,
                Posters = new UserRatingsPostersDto
                {
                    Enabled = settings.ShowOnPosters,
                    Content = settings.PosterBadgeContent.ToString(),
                    Position = PositionName(settings.PosterBadgePosition)
                }
            },
            TopRatedPageEnabled = settings.EnableTopRatedPage
                && UserRatingsKit.Options.Pages.Any(p => string.Equals(p.Key, TopRatedPageKey, StringComparison.Ordinal)),
            StoreReadOnly = readOnly
        };
    }

    /// <inheritdoc />
    public UserRatingsItemDto GetItem(User user, Guid itemId, bool isModerator, int startIndex, int? limit, UserRatingsReviewSort? sort)
    {
        ArgumentNullException.ThrowIfNull(user);
        return ToDto(_service.GetItem(user, itemId, isModerator), startIndex, limit, sort);
    }

    /// <inheritdoc />
    public QueryResult<UserRatingsReviewDto> GetReviews(User user, Guid itemId, int startIndex, int? limit, UserRatingsReviewSort? sort)
    {
        ArgumentNullException.ThrowIfNull(user);
        var (rows, settings, policy) = _service.GetReviews(user, itemId);
        return Page(rows, settings, policy, startIndex, limit, sort);
    }

    /// <inheritdoc />
    public async Task<UserRatingsItemDto> SetRatingAsync(User user, Guid itemId, UserRatingsRatingRequest request, bool isModerator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        // The service's checks come first (ratings on, writable, visible, WhoCanRate, played), then the value; a
        // missing value is a 400 like a wrong one, after the same checks.
        var write = await _service.SetStarsAsync(user, itemId, request.Stars, cancellationToken).ConfigureAwait(false);
        return AfterWrite(user, itemId, isModerator, write);
    }

    /// <inheritdoc />
    public async Task<UserRatingsItemDto> ClearRatingAsync(User user, Guid itemId, bool isModerator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var write = await _service.ClearStarsAsync(user, itemId, cancellationToken).ConfigureAwait(false);
        return AfterWrite(user, itemId, isModerator, write);
    }

    /// <inheritdoc />
    public async Task<UserRatingsItemDto> SetReviewAsync(User user, Guid itemId, UserRatingsReviewRequest request, bool isModerator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        var precondition = new ReviewPrecondition(request.IfUpdatedUtc, request.IfReviewUpdatedUtc, request.IfNoReview == true);
        var write = await _service.SetReviewAsync(user, itemId, request.Text, request.IsSpoiler, precondition, cancellationToken).ConfigureAwait(false);
        return AfterWrite(user, itemId, isModerator, write);
    }

    /// <inheritdoc />
    public async Task<UserRatingsItemDto> ClearReviewAsync(User user, Guid itemId, bool isModerator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var write = await _service.ClearReviewAsync(user, itemId, cancellationToken).ConfigureAwait(false);
        return AfterWrite(user, itemId, isModerator, write);
    }

    /// <inheritdoc />
    public Task DeleteMineAsync(User user, Guid itemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return _service.DeleteMineAsync(user, itemId, cancellationToken);
    }

    /// <inheritdoc />
    public QueryResult<UserRatingsOwnRowDto> GetInaccessible(User user, int startIndex, int? limit)
    {
        ArgumentNullException.ThrowIfNull(user);
        var rows = _service.GetInaccessible(user);
        var (start, take) = PageBounds(startIndex, limit, DefaultLimit);
        var page = rows.Skip(start).Take(take).Select(r => new UserRatingsOwnRowDto
        {
            RatingId = r.Id,
            Stars = Stars(r.Stars2),
            TextSnippet = Snippet(r.ReviewText),
            CreatedUtc = r.CreatedUtc,
            UpdatedUtc = r.UpdatedUtc
        }).ToList();
        return new QueryResult<UserRatingsOwnRowDto>(start, rows.Count, page);
    }

    /// <inheritdoc />
    public Task DeleteOwnRowAsync(User user, Guid ratingId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return _service.DeleteOwnRowAsync(user, ratingId, cancellationToken);
    }

    /// <summary>
    /// Orders review rows (<c>sort</c>): ties go to the row changed last, then by id.
    /// </summary>
    /// <param name="rows">The rows.</param>
    /// <param name="sort">The order.</param>
    /// <returns>The rows in that order.</returns>
    internal static IEnumerable<RatingRecord> Sort(IEnumerable<RatingRecord> rows, UserRatingsReviewSort sort)
        => sort switch
        {
            UserRatingsReviewSort.Oldest => rows.OrderBy(r => r.UpdatedUtc).ThenBy(r => r.Id),
            UserRatingsReviewSort.Highest => rows.OrderBy(r => r.Stars2 is null).ThenByDescending(r => r.Stars2).ThenByDescending(r => r.UpdatedUtc).ThenBy(r => r.Id),
            UserRatingsReviewSort.Lowest => rows.OrderBy(r => r.Stars2 is null).ThenBy(r => r.Stars2).ThenByDescending(r => r.UpdatedUtc).ThenBy(r => r.Id),
            _ => rows.OrderByDescending(r => r.UpdatedUtc).ThenBy(r => r.Id)
        };

    /// <summary>
    /// Cuts a review to <see cref="SnippetLength"/> UTF-16 units without splitting a surrogate pair.
    /// </summary>
    /// <param name="text">The review, or <c>null</c>.</param>
    /// <returns>The snippet, or <c>null</c>.</returns>
    internal static string? Snippet(string? text)
    {
        if (text is null || text.Length <= SnippetLength)
        {
            return text;
        }

        var end = char.IsHighSurrogate(text[SnippetLength - 1]) ? SnippetLength - 1 : SnippetLength;
        return text[..end];
    }

    private static (int Start, int Take) PageBounds(int startIndex, int? limit, int defaultLimit)
        => (Math.Max(0, startIndex), Math.Clamp(limit ?? defaultLimit, 1, MaxLimit));

    private static double? Stars(int? stars2) => stars2 is { } s ? s / 2.0 : null;

    private static UserRatingsMineDto? MineDto(RatingRecord? mine) => mine is null ? null : new UserRatingsMineDto
    {
        Id = mine.Id,
        Stars = Stars(mine.Stars2),
        Text = mine.ReviewText,
        IsSpoiler = mine.IsSpoiler,
        CreatedUtc = mine.CreatedUtc,
        UpdatedUtc = mine.UpdatedUtc,
        ReviewUpdatedUtc = mine.ReviewUpdatedUtc,
        Edited = mine.EditCount > 0
    };

    private static string PositionName(PosterBadgePosition position) => position switch
    {
        PosterBadgePosition.TopEnd => "top-end",
        PosterBadgePosition.BottomStart => "bottom-start",
        PosterBadgePosition.BottomEnd => "bottom-end",
        _ => "top-start"
    };

    private static List<string> KindNames(IReadOnlySet<BaseItemKind> kinds)
        => _kindOrder.Where(kinds.Contains)
            .Concat(kinds.Where(k => !_kindOrder.Contains(k)).OrderBy(k => k.ToString(), StringComparer.Ordinal))
            .Select(k => k.ToString())
            .ToList();

    // The answer of a write: the item read again; on a 503 of that read, the write's own row (IsPartial), since the write
    // committed and a client that saw an error would show its change as refused. Can is what needs no read: the switch,
    // the store's state and WhoCanRate (the member passed the played check a moment ago).
    private UserRatingsItemDto AfterWrite(User user, Guid itemId, bool isModerator, RatingWrite write)
    {
        try
        {
            return GetItem(user, itemId, isModerator, 0, null, null);
        }
        catch (PluginException ex) when (ex.Kind == PluginErrorKind.Unavailable)
        {
            _logger.LogDebug(ex, "The item {ItemId} couldn't be read again after a write of user {UserId}; answered from the write's own row", itemId, user.Id);
            var settings = _settings.Read();
            var reason = _store.IsReadOnly ? RatingsService.ReadOnlyReason : !settings.MayRate(user.Id) ? RatingsService.NotARaterReason : null;
            var rate = settings.EnableRatings && reason is null;
            return new UserRatingsItemDto
            {
                ItemId = itemId,
                IsPartial = true,
                Summary = null,
                Mine = MineDto(write.Mine),
                Reviews = null,
                Can = new UserRatingsItemCanDto { Rate = rate, Review = rate && settings.EnableReviews, Moderate = isModerator, Reason = reason }
            };
        }
    }

    private UserRatingsItemDto ToDto(ItemRatings ratings, int startIndex, int? limit, UserRatingsReviewSort? sort)
    {
        return new UserRatingsItemDto
        {
            ItemId = ratings.Item.Id,
            Summary = new UserRatingsItemSummaryDto
            {
                Average = ratings.Summary.Average,
                Count = ratings.Summary.Count,
                ReviewCount = ratings.Summary.ReviewCount,
                Distribution = ratings.Summary.Distribution.ToArray()
            },
            Mine = MineDto(ratings.Mine),
            Reviews = Page(ratings.Others, ratings.Settings, ratings.Policy, startIndex, limit, sort),
            OtherCopies = ratings.OtherCopies.Select(c => new UserRatingsOtherCopyDto
            {
                ItemId = c.ItemId,
                LibraryName = c.LibraryName,
                Average = c.Summary.Average,
                Count = c.Summary.Count,
                MyStars = c.MyStars is { } my ? (double)my : null
            }).ToList(),
            Can = new UserRatingsItemCanDto { Rate = ratings.Can.Rate, Review = ratings.Can.Review, Moderate = ratings.Can.Moderate, Reason = ratings.Can.Reason }
        };
    }

    private QueryResult<UserRatingsReviewDto> Page(IReadOnlyList<RatingRecord> rows, RatingsSettings settings, VisibilityPolicy policy, int startIndex, int? limit, UserRatingsReviewSort? sort)
    {
        var (start, take) = PageBounds(startIndex, limit, settings.ReviewsPageSize);
        var authors = new Dictionary<Guid, (string Name, string? ImageTag, bool IsFormer)>();
        var page = Sort(rows, sort ?? UserRatingsReviewSort.Newest).Skip(start).Take(take).Select(r =>
        {
            if (!authors.TryGetValue(r.UserId, out var author))
            {
                author = Author(r.UserId, policy, settings.ShowReviewerAvatars);
                authors[r.UserId] = author;
            }

            return new UserRatingsReviewDto
            {
                Id = r.Id,
                UserId = r.UserId,
                UserName = author.Name,
                UserImageTag = author.ImageTag,
                IsFormerMember = author.IsFormer,
                Stars = Stars(r.Stars2),
                Text = r.ReviewText,
                IsSpoiler = r.IsSpoiler,
                CreatedUtc = r.CreatedUtc,
                UpdatedUtc = r.UpdatedUtc,
                Edited = r.EditCount > 0
            };
        }).ToList();
        return new QueryResult<UserRatingsReviewDto>(start, rows.Count, page);
    }

    // The author's name and avatar now (renames show at once); a former member's stored name.
    private (string Name, string? ImageTag, bool IsFormer) Author(Guid userId, VisibilityPolicy policy, bool avatars)
    {
        if (policy.KindOf(userId) == AuthorKind.FormerMember)
        {
            return (policy.FormerMemberName(userId) ?? "Former member", null, true);
        }

        var user = _users.GetUserById(userId);
        if (user is null)
        {
            // Deleted since the policy read the users: shown as nobody in particular, never as another id's name.
            return ("Former member", null, true);
        }

        return (user.Username, avatars ? ImageTag(user) : null, false);
    }

    private string? ImageTag(User user)
    {
        if (_images is null || user.ProfileImage is null)
        {
            return null;
        }

        try
        {
            return _images.GetImageCacheTag(user);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Couldn't read the image tag of user {UserId}", user.Id);
            return null;
        }
    }
}
