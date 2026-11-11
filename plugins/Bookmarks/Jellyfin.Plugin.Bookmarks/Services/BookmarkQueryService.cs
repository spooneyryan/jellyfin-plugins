using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// Implements <see cref="IBookmarkQuery"/> over the store's in-memory records: the caller's live bookmarks
/// (<see cref="IBookmarkStore.GetLive"/>, so never a tombstone or another user's record) filtered, ordered and paged, then
/// mapped to DTOs.
/// </summary>
/// <remarks>
/// <para><b>Stored state only.</b> Every filter, the order and the totals read the records: <c>availability</c> is the
/// stored <c>DetachedUtc</c>, which the identity service keeps current, so a list of 5,000 bookmarks costs no
/// library lookup to filter or count.</para>
/// <para><b>ItemAvailable</b> ("Unavailable" when false): for the rows or groups on the returned page
/// only, each distinct attached video is asked once, as the caller: <c>GetItemById&lt;Video&gt;(id, user)</c>, which applies
/// the user's library access, parental rating and blocked tags; never the user-less lookup and never an <c>ItemIds</c>
/// query, which skip them. Detached bookmarks and adopted clips (no library item) are never looked up. The
/// answers are kept for the one request only: the kit never caches library visibility across requests, so an admin's
/// change of a user's libraries shows at the next request. The video's other fields come from what the bookmark
/// stored.</para>
/// </remarks>
internal sealed class BookmarkQueryService : IBookmarkQuery
{
    /// <summary>
    /// The page size of <see cref="Query"/> when the request gives none.
    /// </summary>
    public const int DefaultLimit = 50;

    /// <summary>
    /// The page size of <see cref="Groups"/> when the request gives none.
    /// </summary>
    public const int DefaultGroupLimit = 25;

    /// <summary>
    /// The largest page.
    /// </summary>
    public const int MaxLimit = 200;

    /// <summary>
    /// The longest search term.
    /// </summary>
    public const int MaxSearchLength = 100;

    /// <summary>
    /// The most bookmarks a group lists; <c>BookmarksTruncated</c> says when it has more.
    /// </summary>
    public const int MaxBookmarksPerGroup = 100;

    /// <summary>
    /// The 400 message of a search term over <see cref="MaxSearchLength"/> characters.
    /// </summary>
    public const string SearchTooLongMessage = "Search terms can be at most 100 characters long.";

    /// <summary>
    /// The 400 message of a negative start index.
    /// </summary>
    public const string StartIndexMessage = "The start index can't be negative.";

    /// <summary>
    /// The 400 message of a limit outside 1 to <see cref="MaxLimit"/>.
    /// </summary>
    public const string LimitMessage = "The limit must be between 1 and 200.";

    // Names sort as a person reads them: the invariant culture, ignoring case; the search also ignores accents.
    private const CompareOptions SearchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private static readonly StringComparer _textOrder = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
    private static readonly CompareInfo _search = CultureInfo.InvariantCulture.CompareInfo;

    private readonly IBookmarkStore _store;
    private readonly ILibraryManager _library;
    private readonly IClipExtractionService _clips;
    private readonly ILogger<BookmarkQueryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkQueryService"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="library">The library, asked only for <c>ItemAvailable</c>, as the caller.</param>
    /// <param name="clips">The clip service, for the progress of running clips.</param>
    /// <param name="logger">The logger.</param>
    public BookmarkQueryService(IBookmarkStore store, ILibraryManager library, IClipExtractionService clips, ILogger<BookmarkQueryService> logger)
    {
        _store = store;
        _library = library;
        _clips = clips;
        _logger = logger;
    }

    /// <inheritdoc />
    public QueryResult<BookmarkDto> Query(User user, BookmarkQueryFilter filter, BookmarkSortBy? sortBy)
    {
        ArgumentNullException.ThrowIfNull(user);

        var criteria = Criteria.From(filter, DefaultLimit);
        var matches = Match(user, criteria);
        var by = sortBy ?? BookmarkSortBy.UpdatedAt;
        var descending = criteria.Order is SortOrder order ? order == SortOrder.Descending : by is BookmarkSortBy.UpdatedAt or BookmarkSortBy.CreatedAt;
        var page = Sort(matches, by, descending).Skip(criteria.StartIndex).Take(criteria.Limit).ToList();

        var access = new PageAccess(this, user);
        var rows = page.Select(b => ToDto(b, access.CanOpen(b))).ToList();
        return new QueryResult<BookmarkDto>(criteria.StartIndex, matches.Count, rows);
    }

    /// <inheritdoc />
    public QueryResult<BookmarkGroupDto> Groups(User user, BookmarkQueryFilter filter, BookmarkGroupSortBy? sortBy)
    {
        ArgumentNullException.ThrowIfNull(user);

        var criteria = Criteria.From(filter, DefaultGroupLimit);
        var groups = Match(user, criteria)
            .GroupBy(b => b.ItemId)
            .Select(g => new Group(g.Key, ByPosition(g).ToList()))
            .ToList();
        var by = sortBy ?? BookmarkGroupSortBy.LatestUpdated;
        var descending = criteria.Order is SortOrder order ? order == SortOrder.Descending : by is BookmarkGroupSortBy.LatestUpdated or BookmarkGroupSortBy.Count;
        var page = Sort(groups, by, descending).Skip(criteria.StartIndex).Take(criteria.Limit).ToList();

        var access = new PageAccess(this, user);
        var rows = page.Select(g => ToDto(g, access)).ToList();
        return new QueryResult<BookmarkGroupDto>(criteria.StartIndex, groups.Count, rows);
    }

    /// <summary>
    /// Gets the kind of video a record is on, as the <c>itemTypes</c> filters see it: the identity snapshot's kind (the
    /// clip list's filter too).
    /// </summary>
    /// <param name="bookmark">The record.</param>
    /// <returns>Its kind.</returns>
    internal static BookmarkItemType TypeOf(Bookmark bookmark) => bookmark.Identity.Kind switch
    {
        var kind when string.Equals(kind, "Movie", StringComparison.OrdinalIgnoreCase) => BookmarkItemType.Movie,
        var kind when string.Equals(kind, "Episode", StringComparison.OrdinalIgnoreCase) => BookmarkItemType.Episode,
        _ => BookmarkItemType.Other
    };

    private static bool IsNamed(Bookmark bookmark) => !string.IsNullOrWhiteSpace(bookmark.Name);

    private static bool HasClipFile(Bookmark bookmark) => bookmark.Clip?.FilePath is { Length: > 0 };

    private static bool Contains(string? text, string term) => text is not null && _search.IndexOf(text, term, SearchOptions) >= 0;

    // The availability filter is the stored DetachedUtc, never a library lookup.
    private static bool IsAvailableAsStored(Bookmark b, BookmarkAvailability availability) => availability switch
    {
        BookmarkAvailability.Missing => b.DetachedUtc is not null,
        BookmarkAvailability.Available => b.DetachedUtc is null,
        _ => true
    };

    private static bool Matches(Bookmark b, Criteria c)
        => (c.Types is null || c.Types.Contains(TypeOf(b)))
            && (c.HasRange is not bool range || (b.EndTicks is not null) == range)
            && (c.HasClip is not bool clip || HasClipFile(b) == clip)
            && (c.Named is not bool named || IsNamed(b) == named)
            && IsAvailableAsStored(b, c.Availability)
            && (c.Term is not { } term
                || Contains(b.Name, term) || Contains(b.Note, term) || Contains(b.ItemName, term) || Contains(b.Identity.SeriesName, term));

    // Inside a video: by where the bookmark starts, then ends; a single position before a range that starts there.
    private static IOrderedEnumerable<Bookmark> ByPosition(IEnumerable<Bookmark> bookmarks)
        => bookmarks.OrderBy(b => b.StartTicks).ThenBy(b => b.EndTicks ?? b.StartTicks).ThenBy(b => b.Id);

    private static IOrderedEnumerable<Bookmark> Sort(IEnumerable<Bookmark> bookmarks, BookmarkSortBy by, bool descending) => by switch
    {
        BookmarkSortBy.CreatedAt => First(bookmarks, b => b.CreatedAt, Comparer<DateTime>.Default, descending).ThenBy(b => b.Id),

        // Unnamed bookmarks after the named ones in either order; they have nothing to be put in order by.
        BookmarkSortBy.Name => Then(bookmarks.OrderBy(b => IsNamed(b) ? 0 : 1), b => b.Name ?? string.Empty, _textOrder, descending)
            .ThenBy(b => b.ItemName, _textOrder).ThenBy(b => b.StartTicks).ThenBy(b => b.Id),
        BookmarkSortBy.ItemName => First(bookmarks, b => b.ItemName, _textOrder, descending)
            .ThenBy(b => b.ItemId).ThenBy(b => b.StartTicks).ThenBy(b => b.EndTicks ?? b.StartTicks).ThenBy(b => b.Id),
        BookmarkSortBy.Position => Then(First(bookmarks, b => b.StartTicks, Comparer<long>.Default, descending), b => b.EndTicks ?? b.StartTicks, Comparer<long>.Default, descending)
            .ThenBy(b => b.ItemName, _textOrder).ThenBy(b => b.Id),
        _ => First(bookmarks, b => b.UpdatedAt, Comparer<DateTime>.Default, descending).ThenBy(b => b.Id),
    };

    private static IOrderedEnumerable<Group> Sort(IEnumerable<Group> groups, BookmarkGroupSortBy by, bool descending) => by switch
    {
        BookmarkGroupSortBy.ItemName => First(groups, g => g.Latest.ItemName, _textOrder, descending).ThenBy(g => g.ItemId),

        // Ties of "Most bookmarks" go to the video changed most recently.
        BookmarkGroupSortBy.Count => First(groups, g => g.Bookmarks.Count, Comparer<int>.Default, descending)
            .ThenByDescending(g => g.LatestUpdatedAt).ThenBy(g => g.ItemId),
        _ => First(groups, g => g.LatestUpdatedAt, Comparer<DateTime>.Default, descending).ThenBy(g => g.Latest.ItemName, _textOrder).ThenBy(g => g.ItemId),
    };

    private static IOrderedEnumerable<T> First<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, IComparer<TKey> comparer, bool descending)
        => descending ? items.OrderByDescending(key, comparer) : items.OrderBy(key, comparer);

    private static IOrderedEnumerable<T> Then<T, TKey>(IOrderedEnumerable<T> items, Func<T, TKey> key, IComparer<TKey> comparer, bool descending)
        => descending ? items.ThenByDescending(key, comparer) : items.ThenBy(key, comparer);

    // The caller's live bookmarks, on one video when asked, that match the filters: the store's memory only.
    private List<Bookmark> Match(User user, Criteria criteria)
        => _store.GetLive(user.Id, criteria.ItemId).Where(b => Matches(b, criteria)).ToList();

    private BookmarkDto ToDto(Bookmark bookmark, bool itemAvailable)
        => BookmarkDtoMapper.ToDto(bookmark, _clips.GetProgress(bookmark.Id), itemAvailable);

    private BookmarkGroupDto ToDto(Group group, PageAccess access)
    {
        // The video's fields as its most recently changed bookmark stored them: nothing is read from the library.
        var latest = group.Latest;
        var identity = latest.Identity;
        return new BookmarkGroupDto
        {
            ItemId = group.ItemId,
            ItemName = latest.ItemName,
            ItemType = string.IsNullOrEmpty(identity.Kind) ? null : identity.Kind,
            Year = identity.Year,
            SeriesName = string.IsNullOrWhiteSpace(identity.SeriesName) ? null : identity.SeriesName,
            SeasonNumber = identity.Season,
            EpisodeNumber = identity.Episode,
            ItemAvailable = group.Bookmarks.Any(PageAccess.IsInLibrary) && access.CanOpenItem(group.ItemId),
            Count = group.Bookmarks.Count,
            LatestUpdatedAt = group.LatestUpdatedAt,
            Bookmarks = group.Bookmarks.Take(MaxBookmarksPerGroup).Select(b => ToDto(b, access.CanOpen(b))).ToList(),
            BookmarksTruncated = group.Bookmarks.Count > MaxBookmarksPerGroup
        };
    }

    // Whether the user can open the video: Jellyfin's own check as that user. A lookup that fails counts
    // as "can't", so one broken item costs its rows the poster and play button, never the page.
    private bool CanOpenItem(Guid itemId, User user)
    {
        try
        {
            return _library.GetItemById<Video>(itemId, user) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not look up item {ItemId} for user {UserId}'s bookmarks", itemId, user.Id);
            return false;
        }
    }

    /// <summary>
    /// A request's filters, checked (400 otherwise).
    /// </summary>
    private sealed record Criteria(
        string? Term,
        Guid? ItemId,
        IReadOnlySet<BookmarkItemType>? Types,
        bool? HasRange,
        bool? HasClip,
        bool? Named,
        BookmarkAvailability Availability,
        SortOrder? Order,
        int StartIndex,
        int Limit)
    {
        public static Criteria From(BookmarkQueryFilter? filter, int defaultLimit)
        {
            filter ??= new BookmarkQueryFilter();
            var term = filter.SearchTerm?.Trim();
            if (term?.Length > MaxSearchLength)
            {
                throw BookmarkException.Invalid(SearchTooLongMessage);
            }

            var startIndex = filter.StartIndex ?? 0;
            if (startIndex < 0)
            {
                throw BookmarkException.Invalid(StartIndexMessage);
            }

            var limit = filter.Limit ?? defaultLimit;
            if (limit is < 1 or > MaxLimit)
            {
                throw BookmarkException.Invalid(LimitMessage);
            }

            return new Criteria(
                string.IsNullOrEmpty(term) ? null : term,
                filter.ItemId,
                filter.ItemTypes is { Count: > 0 } types ? types.ToHashSet() : null,
                filter.HasRange,
                filter.HasClip,
                filter.Named,
                filter.Availability ?? BookmarkAvailability.All,
                filter.SortOrder,
                startIndex,
                limit);
        }
    }

    /// <summary>
    /// One video's matching bookmarks, by position.
    /// </summary>
    private sealed class Group
    {
        public Group(Guid itemId, IReadOnlyList<Bookmark> bookmarks)
        {
            ItemId = itemId;
            Bookmarks = bookmarks;
            Latest = bookmarks.OrderByDescending(b => b.UpdatedAt).ThenBy(b => b.Id).First();
            LatestUpdatedAt = Latest.UpdatedAt;
        }

        public Guid ItemId { get; }

        public IReadOnlyList<Bookmark> Bookmarks { get; }

        public Bookmark Latest { get; }

        public DateTime LatestUpdatedAt { get; }
    }

    /// <summary>
    /// The answers of one request's lookups: each distinct video once, as the caller, never kept past the request.
    /// </summary>
    private sealed class PageAccess
    {
        private readonly BookmarkQueryService _service;
        private readonly User _user;
        private readonly Dictionary<Guid, bool> _items = [];

        public PageAccess(BookmarkQueryService service, User user)
        {
            _service = service;
            _user = user;
        }

        // Attached to a library item that may be looked up: not detached ("Not in library"), not an adopted clip.
        public static bool IsInLibrary(Bookmark bookmark) => bookmark.DetachedUtc is null && BookmarkRecordRules.HasLibraryItem(bookmark);

        public bool CanOpen(Bookmark bookmark) => IsInLibrary(bookmark) && CanOpenItem(bookmark.ItemId);

        public bool CanOpenItem(Guid itemId)
        {
            if (!_items.TryGetValue(itemId, out var canOpen))
            {
                canOpen = itemId != Guid.Empty && _service.CanOpenItem(itemId, _user);
                _items[itemId] = canOpen;
            }

            return canOpen;
        }
    }
}
