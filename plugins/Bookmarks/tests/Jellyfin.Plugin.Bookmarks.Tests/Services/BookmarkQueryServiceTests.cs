using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// The My Bookmarks page's lists through <see cref="IBookmarkQuery"/>: every
/// filter, each sort in both orders, paging at its edges, tombstones and other users' bookmarks never listed, the
/// accent-insensitive search; <c>availability</c> on the stored state with no library lookup at all, and <c>ItemAvailable</c>
/// looked up only for the page, once per video, as the caller, and again at the next request.
/// </summary>
public sealed class BookmarkQueryServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory _directory = new();
    private readonly BookmarkStore _store;
    private readonly Mock<ILibraryManager> _library = new(MockBehavior.Strict);
    private readonly HashSet<Guid> _visible = [];
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private readonly IBookmarkQuery _query;
    private int _added;

    public BookmarkQueryServiceTests()
    {
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);

        // The library answers only the per-user lookup (a strict mock: the user-less GetItemById or an ItemIds query would
        // throw), and throws for the empty GUID as 10.11.5 does (LibraryManager.cs:1357-1362).
        _library.Setup(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()))
            .Returns((Guid id, User? user) => id == Guid.Empty
                ? throw new ArgumentException("Guid can't be empty", nameof(id))
                : user is not null && _visible.Contains(id) ? new MediaBrowser.Controller.Entities.Movies.Movie { Id = id } : null);
        var clips = new Mock<IClipExtractionService>();
        _query = new BookmarkQueryService(_store, _library.Object, clips.Object, NullLogger<BookmarkQueryService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Query_ListsOnlyTheCallersLiveBookmarks_MostRecentlyChangedFirst()
    {
        var item = Guid.NewGuid();
        var older = await AddAsync(_alice, item, updated: T0);
        var newer = await AddAsync(_alice, item, updated: T0.AddHours(1));
        await AddAsync(_bob, item, updated: T0.AddHours(2));
        var tombstone = await AddAsync(_alice, item, updated: T0.AddHours(3), clipFile: true);
        await _store.UpdateAsync(tombstone.Id, b => BookmarkRecordRules.ToTombstone(b, T0.AddHours(4))!, Ct);

        var result = _query.Query(_alice, new BookmarkQueryFilter(), null);

        Assert.Equal([newer.Id, older.Id], result.Items.Select(b => b.Id));
        Assert.Equal(2, result.TotalRecordCount);
        Assert.Equal(0, result.StartIndex);
    }

    [Theory]
    [InlineData("bank", "name")]
    [InlineData("BANK JOB", "name")]
    [InlineData("reflection", "note")]
    [InlineData("heat", "item")]
    [InlineData("  heat  ", "item")]
    [InlineData("amelie", "accents")]
    [InlineData("CAFÉ", "accents")]
    [InlineData("scene", "accents")]
    [InlineData("Wire", "series")]
    public async Task Search_LooksInTheNameNoteVideoAndSeries_IgnoringCaseAndAccents(string term, string expected)
    {
        var name = await AddAsync(_alice, Guid.NewGuid(), name: "Bank job", itemName: "Other (2001)");
        var note = await AddAsync(_alice, Guid.NewGuid(), note: "Watch the reflection in the window", itemName: "Other (2002)");
        var item = await AddAsync(_alice, Guid.NewGuid(), itemName: "Heat (1995)");
        var accents = await AddAsync(_alice, Guid.NewGuid(), name: "Amélie, café scène", itemName: "Other (2003)");
        var series = await AddAsync(_alice, Guid.NewGuid(), itemName: "S01E02 - Pilot", kind: "Episode", seriesName: "The Wire");
        await AddAsync(_bob, Guid.NewGuid(), name: "Bank job heat amelie Wire reflection", itemName: "Heat (1995)");
        var ids = new Dictionary<string, Guid> { ["name"] = name.Id, ["note"] = note.Id, ["item"] = item.Id, ["accents"] = accents.Id, ["series"] = series.Id };

        var result = _query.Query(_alice, new BookmarkQueryFilter { SearchTerm = term }, null);

        Assert.Equal([ids[expected]], result.Items.Select(b => b.Id));
        Assert.Equal(1, result.TotalRecordCount);
    }

    [Fact]
    public async Task Search_OfWhiteSpace_SearchesNothing_AndOver100Characters_Is400()
    {
        await AddAsync(_alice, Guid.NewGuid());
        await AddAsync(_alice, Guid.NewGuid());

        Assert.Equal(2, _query.Query(_alice, new BookmarkQueryFilter { SearchTerm = "   " }, null).TotalRecordCount);
        Assert.Equal(0, _query.Query(_alice, new BookmarkQueryFilter { SearchTerm = new string('x', 100) }, null).TotalRecordCount);
        var error = Assert.Throws<BookmarkException>(() => _query.Query(_alice, new BookmarkQueryFilter { SearchTerm = new string('x', 101) }, null));
        Assert.Equal(PluginErrorKind.Invalid, error.Kind);
        Assert.Equal("Search terms can be at most 100 characters long.", error.Message);
        Assert.Throws<BookmarkException>(() => _query.Groups(_alice, new BookmarkQueryFilter { SearchTerm = new string('x', 101) }, null));
    }

    [Fact]
    public async Task Filters_TypeRangeClipNameItem_EachNarrowTheList()
    {
        var movieItem = Guid.NewGuid();
        var movie = await AddAsync(_alice, movieItem, kind: "Movie", end: 90, name: "Named", clipFile: true);
        var moviePoint = await AddAsync(_alice, movieItem, kind: "Movie", end: null);
        var episode = await AddAsync(_alice, Guid.NewGuid(), kind: "Episode", end: 90, clipQueued: true);
        var other = await AddAsync(_alice, Guid.NewGuid(), kind: "Video", end: null, name: "Home");
        var unknown = await AddAsync(_alice, Guid.NewGuid(), kind: string.Empty, end: null);

        Assert.Equal(Ids(movie, moviePoint), Listed(new() { ItemTypes = [BookmarkItemType.Movie] }));
        Assert.Equal(Ids(episode), Listed(new() { ItemTypes = [BookmarkItemType.Episode] }));
        Assert.Equal(Ids(other, unknown), Listed(new() { ItemTypes = [BookmarkItemType.Other] }));
        Assert.Equal(Ids(movie, moviePoint, episode), Listed(new() { ItemTypes = [BookmarkItemType.Movie, BookmarkItemType.Episode] }));
        Assert.Equal(Ids(movie, moviePoint, episode, other, unknown), Listed(new() { ItemTypes = [] }));
        Assert.Equal(Ids(movie, episode), Listed(new() { HasRange = true }));
        Assert.Equal(Ids(moviePoint, other, unknown), Listed(new() { HasRange = false }));

        // A clip file, not a clip job: the queued clip has no file yet.
        Assert.Equal(Ids(movie), Listed(new() { HasClip = true }));
        Assert.Equal(Ids(moviePoint, episode, other, unknown), Listed(new() { HasClip = false }));
        Assert.Equal(Ids(movie, other), Listed(new() { Named = true }));
        Assert.Equal(Ids(moviePoint, episode, unknown), Listed(new() { Named = false }));
        Assert.Equal(Ids(movie, moviePoint), Listed(new() { ItemId = movieItem }));
        Assert.Empty(Listed(new() { ItemId = Guid.NewGuid() }));

        // Filters combine.
        Assert.Equal(Ids(moviePoint), Listed(new() { ItemTypes = [BookmarkItemType.Movie], Named = false, HasRange = false }));
    }

    [Fact]
    public async Task Availability_IsTheStoredState_AndTheFilterAndTotalAskTheLibraryNothing()
    {
        // Missing = DetachedUtc set, Available = not; the filter and TotalRecordCount cost no lookups.
        var attached = await AddAsync(_alice, Guid.NewGuid());
        var detached = await AddAsync(_alice, Guid.NewGuid(), detached: T0);
        var alsoDetached = await AddAsync(_alice, Guid.NewGuid(), detached: T0.AddDays(1));
        _visible.Add(attached.ItemId);

        var missing = _query.Query(_alice, new BookmarkQueryFilter { Availability = BookmarkAvailability.Missing }, null);
        var missingGroups = _query.Groups(_alice, new BookmarkQueryFilter { Availability = BookmarkAvailability.Missing }, null);

        Assert.Equal(Ids(detached, alsoDetached).Order(), missing.Items.Select(b => b.Id).Order());
        Assert.Equal(2, missing.TotalRecordCount);
        Assert.All(missing.Items, b => Assert.True(b.Detached));
        Assert.All(missing.Items, b => Assert.False(b.ItemAvailable));
        Assert.Equal(2, missingGroups.TotalRecordCount);
        Assert.All(missingGroups.Items, g => Assert.False(g.ItemAvailable));
        _library.Verify(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()), Times.Never);

        // A page of nothing: the totals come from the store alone.
        var counted = _query.Query(_alice, new BookmarkQueryFilter { Availability = BookmarkAvailability.Available, StartIndex = 50 }, null);
        Assert.Empty(counted.Items);
        Assert.Equal(1, counted.TotalRecordCount);
        Assert.Equal(3, _query.Query(_alice, new BookmarkQueryFilter { Availability = BookmarkAvailability.All, StartIndex = 50 }, null).TotalRecordCount);
        _library.Verify(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()), Times.Never);

        var available = _query.Query(_alice, new BookmarkQueryFilter { Availability = BookmarkAvailability.Available }, null);
        Assert.Equal(Ids(attached), available.Items.Select(b => b.Id));
        Assert.True(available.Items[0].ItemAvailable);
        _library.Verify(m => m.GetItemById<Video>(attached.ItemId, _alice), Times.Once);
    }

    [Fact]
    public async Task ItemAvailable_IsLookedUpOnlyForThePage_OncePerVideo_AsTheCaller_AndAgainAtTheNextRequest()
    {
        // 60 bookmarks on 30 videos, two each: a page of 10 rows names at most 10 videos.
        var items = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToList();
        await AddAllAsync(Enumerable.Range(0, 60).Select(i => Make(_alice, items[i / 2], updated: T0.AddMinutes(-i))));

        _visible.UnionWith(items);

        var page = _query.Query(_alice, new BookmarkQueryFilter { Limit = 10, StartIndex = 20 }, null);

        Assert.Equal(10, page.Items.Count);
        Assert.Equal(60, page.TotalRecordCount);
        Assert.Equal(20, page.StartIndex);
        Assert.All(page.Items, b => Assert.True(b.ItemAvailable));
        var onThePage = page.Items.Select(b => b.ItemId).Distinct().ToList();
        Assert.Equal(5, onThePage.Count);
        _library.Verify(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()), Times.Exactly(5));
        foreach (var item in onThePage)
        {
            _library.Verify(m => m.GetItemById<Video>(item, _alice), Times.Once);
        }

        // No answer is kept past the request: the user lost access in between, and the next request says
        // so at once.
        _visible.ExceptWith(onThePage.Take(2));
        var next = _query.Query(_alice, new BookmarkQueryFilter { Limit = 10, StartIndex = 20 }, null);
        Assert.Equal(4, next.Items.Count(b => b.ItemAvailable == false));
        _library.Verify(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()), Times.Exactly(10));
        _library.Verify(m => m.GetItemById<Video>(It.IsAny<Guid>(), null), Times.Never);
    }

    [Fact]
    public async Task ItemAvailable_IsFalse_ForAVideoTheCallerCantOpen_ADetachedBookmark_AnAdoptedClip_AndAFailingLookup()
    {
        var visible = await AddAsync(_alice, Guid.NewGuid(), updated: T0.AddHours(5));
        var hidden = await AddAsync(_alice, Guid.NewGuid(), updated: T0.AddHours(4));
        var detached = await AddAsync(_alice, Guid.NewGuid(), updated: T0.AddHours(3), detached: T0);
        var adopted = await AddAsync(_alice, Guid.Empty, updated: T0.AddHours(2), origin: BookmarkOrigin.Adopted);
        var broken = await AddAsync(_alice, Guid.NewGuid(), updated: T0.AddHours(1));
        _visible.UnionWith([visible.ItemId, detached.ItemId]);
        _library.Setup(m => m.GetItemById<Video>(broken.ItemId, It.IsAny<User?>())).Throws(new InvalidOperationException("broken item"));

        var rows = _query.Query(_alice, new BookmarkQueryFilter(), null).Items;

        Assert.Equal([visible.Id, hidden.Id, detached.Id, adopted.Id, broken.Id], rows.Select(b => b.Id));
        Assert.Equal([true, false, false, false, false], rows.Select(b => b.ItemAvailable!.Value));

        // A detached bookmark and an adopted clip are never looked up (the empty GUID throws on 10.11.5).
        _library.Verify(m => m.GetItemById<Video>(detached.ItemId, It.IsAny<User?>()), Times.Never);
        _library.Verify(m => m.GetItemById<Video>(Guid.Empty, It.IsAny<User?>()), Times.Never);

        // The unavailable row keeps what the bookmark stored about the video, and nothing else.
        var row = rows[1];
        Assert.Equal("Heat (1995)", row.ItemName);
        Assert.Equal(1995, row.Year);
        Assert.False(row.Detached);
    }

    [Fact]
    public async Task Dto_CarriesTheSnapshotsFields_AndTheOrigin()
    {
        var episode = await AddAsync(_alice, Guid.NewGuid(), kind: "Episode", itemName: "The Wire S01E02 - The Detail", seriesName: "The Wire", season: 1, episodeNumber: 2, year: 2002, origin: BookmarkOrigin.JellyfinEnhanced);

        var row = Assert.Single(_query.Query(_alice, new BookmarkQueryFilter(), null).Items);

        Assert.Equal(episode.Id, row.Id);
        Assert.Equal("Episode", row.ItemType);
        Assert.Equal(2002, row.Year);
        Assert.Equal("The Wire", row.SeriesName);
        Assert.Equal(1, row.SeasonNumber);
        Assert.Equal(2, row.EpisodeNumber);
        Assert.Equal(BookmarkOrigin.JellyfinEnhanced, row.Origin);

        // The snapshot's path is the admin's: no DTO carries it.
        Assert.DoesNotContain(@"C:\\media", System.Text.Json.JsonSerializer.Serialize(row), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sorts_OfTheFlatList_InBothOrders()
    {
        // Created in this order; changed in another; named B, a, none, C; on two videos; at different positions.
        var heat = Guid.NewGuid();
        var alien = Guid.NewGuid();
        var one = await AddAsync(_alice, heat, created: T0, updated: T0.AddHours(2), name: "B", itemName: "Heat (1995)", start: 300, end: null);
        var two = await AddAsync(_alice, alien, created: T0.AddHours(1), updated: T0.AddHours(3), name: "a", itemName: "Alien (1979)", start: 100, end: null);
        var three = await AddAsync(_alice, heat, created: T0.AddHours(2), updated: T0.AddHours(1), itemName: "Heat (1995)", start: 200, end: null);
        var four = await AddAsync(_alice, alien, created: T0.AddHours(3), updated: T0, name: "C", itemName: "alien (1979)", start: 50, end: 60);

        Assert.Equal(Ids(two, one, three, four), Sorted(BookmarkSortBy.UpdatedAt, null));
        Assert.Equal(Ids(two, one, three, four), Sorted(null, null));
        Assert.Equal(Ids(four, three, one, two), Sorted(BookmarkSortBy.UpdatedAt, SortOrder.Ascending));
        Assert.Equal(Ids(four, three, two, one), Sorted(BookmarkSortBy.CreatedAt, null));
        Assert.Equal(Ids(one, two, three, four), Sorted(BookmarkSortBy.CreatedAt, SortOrder.Ascending));

        // Names ignoring case; the unnamed bookmark after the named ones in either order.
        Assert.Equal(Ids(two, one, four, three), Sorted(BookmarkSortBy.Name, null));
        Assert.Equal(Ids(four, one, two, three), Sorted(BookmarkSortBy.Name, SortOrder.Descending));

        // The video's name ignoring case, then the position in it.
        Assert.Equal(Ids(four, two, three, one), Sorted(BookmarkSortBy.ItemName, null));
        Assert.Equal(Ids(three, one, four, two), Sorted(BookmarkSortBy.ItemName, SortOrder.Descending));

        // Where the bookmark is in its video.
        Assert.Equal(Ids(four, two, three, one), Sorted(BookmarkSortBy.Position, null));
        Assert.Equal(Ids(one, three, two, four), Sorted(BookmarkSortBy.Position, SortOrder.Descending));
    }

    [Fact]
    public async Task Paging_AtItsEdges_AndOutOfRangeValuesAre400()
    {
        await AddAllAsync(Enumerable.Range(0, 205).Select(i => Make(_alice, Guid.NewGuid(), updated: T0.AddMinutes(-i))));

        Assert.Equal(50, _query.Query(_alice, new BookmarkQueryFilter(), null).Items.Count);
        Assert.Equal(25, _query.Groups(_alice, new BookmarkQueryFilter(), null).Items.Count);
        Assert.Equal(200, _query.Query(_alice, new BookmarkQueryFilter { Limit = 200 }, null).Items.Count);
        Assert.Single(_query.Query(_alice, new BookmarkQueryFilter { Limit = 1 }, null).Items);
        Assert.Equal(5, _query.Query(_alice, new BookmarkQueryFilter { StartIndex = 200, Limit = 200 }, null).Items.Count);
        var beyond = _query.Query(_alice, new BookmarkQueryFilter { StartIndex = 205 }, null);
        Assert.Empty(beyond.Items);
        Assert.Equal(205, beyond.TotalRecordCount);
        Assert.Equal(205, beyond.StartIndex);

        // Pages follow each other without a gap or a repeat.
        var all = _query.Query(_alice, new BookmarkQueryFilter { Limit = 200 }, null).Items.Select(b => b.Id)
            .Concat(_query.Query(_alice, new BookmarkQueryFilter { StartIndex = 200, Limit = 200 }, null).Items.Select(b => b.Id))
            .ToList();
        Assert.Equal(205, all.Distinct().Count());

        foreach (var (filter, message) in new (BookmarkQueryFilter, string)[]
        {
            (new() { Limit = 0 }, "The limit must be between 1 and 200."),
            (new() { Limit = 201 }, "The limit must be between 1 and 200."),
            (new() { StartIndex = -1 }, "The start index can't be negative.")
        })
        {
            Assert.Equal(message, Assert.Throws<BookmarkException>(() => _query.Query(_alice, filter, null)).Message);
            Assert.Equal(message, Assert.Throws<BookmarkException>(() => _query.Groups(_alice, filter, null)).Message);
        }
    }

    [Fact]
    public async Task Groups_OnePerVideo_ByPosition_WithTheCountAndTheLatestChange()
    {
        var heat = Guid.NewGuid();
        var alien = Guid.NewGuid();
        var late = await AddAsync(_alice, heat, updated: T0.AddHours(1), start: 300, end: 330, itemName: "Heat (1995)", year: 1995);
        var early = await AddAsync(_alice, heat, updated: T0.AddHours(3), start: 100, end: 150, itemName: "Heat (1995)", year: 1995);
        var point = await AddAsync(_alice, heat, updated: T0, start: 100, end: null, itemName: "Heat (1995)", year: 1995);
        var alienOnly = await AddAsync(_alice, alien, updated: T0.AddHours(2), itemName: "Alien (1979)");
        await AddAsync(_bob, heat, updated: T0.AddHours(9));
        _visible.Add(heat);

        var result = _query.Groups(_alice, new BookmarkQueryFilter(), null);

        Assert.Equal(2, result.TotalRecordCount);
        var first = result.Items[0];
        Assert.Equal(heat, first.ItemId);
        Assert.Equal("Heat (1995)", first.ItemName);
        Assert.Equal("Movie", first.ItemType);
        Assert.Equal(1995, first.Year);
        Assert.Equal(3, first.Count);
        Assert.Equal(T0.AddHours(3), first.LatestUpdatedAt);
        Assert.True(first.ItemAvailable);
        Assert.False(first.BookmarksTruncated);

        // By position: a single position before the range that starts there.
        Assert.Equal(Ids(point, early, late), first.Bookmarks.Select(b => b.Id));
        Assert.All(first.Bookmarks, b => Assert.True(b.ItemAvailable));
        Assert.Equal([alienOnly.Id], result.Items[1].Bookmarks.Select(b => b.Id));
        Assert.False(result.Items[1].ItemAvailable);

        // Filters work on the bookmarks before they are grouped; a group counts only the matching ones.
        var ranges = _query.Groups(_alice, new BookmarkQueryFilter { HasRange = true, ItemId = heat }, null);
        Assert.Equal(2, Assert.Single(ranges.Items).Count);
        Assert.Equal(Ids(early, late), ranges.Items[0].Bookmarks.Select(b => b.Id));
    }

    [Fact]
    public async Task Groups_Sorts_InBothOrders()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        await AddAsync(_alice, a, updated: T0.AddHours(1), itemName: "Heat (1995)");
        await AddAsync(_alice, b, updated: T0.AddHours(3), itemName: "alien (1979)");
        await AddAsync(_alice, b, updated: T0, itemName: "Alien (1979)");
        await AddAsync(_alice, c, updated: T0.AddHours(2), itemName: "Brazil (1985)");
        await AddAsync(_alice, c, updated: T0.AddHours(2), itemName: "Brazil (1985)");

        Assert.Equal([b, c, a], Groups(null, null));
        Assert.Equal([b, c, a], Groups(BookmarkGroupSortBy.LatestUpdated, null));
        Assert.Equal([a, c, b], Groups(BookmarkGroupSortBy.LatestUpdated, SortOrder.Ascending));
        Assert.Equal([b, c, a], Groups(BookmarkGroupSortBy.ItemName, null));
        Assert.Equal([a, c, b], Groups(BookmarkGroupSortBy.ItemName, SortOrder.Descending));

        // Most bookmarks first; a tie goes to the video changed most recently.
        Assert.Equal([b, c, a], Groups(BookmarkGroupSortBy.Count, null));
        Assert.Equal([a, b, c], Groups(BookmarkGroupSortBy.Count, SortOrder.Ascending));
    }

    [Fact]
    public async Task Groups_ListAtMost100Bookmarks_AndSaySoWhenTheyHaveMore()
    {
        var item = Guid.NewGuid();
        await AddAllAsync(Enumerable.Range(0, 101).Reverse().Select(i => Make(_alice, item, start: i, end: null)));

        var group = Assert.Single(_query.Groups(_alice, new BookmarkQueryFilter(), null).Items);

        Assert.Equal(101, group.Count);
        Assert.Equal(100, group.Bookmarks.Count);
        Assert.True(group.BookmarksTruncated);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => i * TestData.OneSecond), group.Bookmarks.Select(b => b.StartTicks));
    }

    [Fact]
    public async Task Groups_AskTheLibraryOncePerVideoOnThePage_AndNeverForADetachedOne()
    {
        var items = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToList();
        await AddAllAsync(Enumerable.Range(0, 30).SelectMany(i => new[]
        {
            Make(_alice, items[i], updated: T0.AddMinutes(-i)),
            Make(_alice, items[i], updated: T0.AddMinutes(-i - 1))
        }));

        var gone = await AddAsync(_alice, Guid.NewGuid(), updated: T0.AddDays(1), detached: T0);
        _visible.UnionWith(items);

        var page = _query.Groups(_alice, new BookmarkQueryFilter { Limit = 5 }, null);

        Assert.Equal(31, page.TotalRecordCount);
        Assert.Equal(gone.ItemId, page.Items[0].ItemId);
        Assert.False(page.Items[0].ItemAvailable);
        Assert.All(page.Items.Skip(1), g => Assert.True(g.ItemAvailable));
        _library.Verify(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()), Times.Exactly(4));
        _library.Verify(m => m.GetItemById<Video>(gone.ItemId, It.IsAny<User?>()), Times.Never);
    }

    [Fact]
    public async Task AnotherUsersVideo_OrBookmarks_NeverShow_EvenByItemId()
    {
        var item = Guid.NewGuid();
        await AddAsync(_bob, item, name: "Bob's");
        _visible.Add(item);

        Assert.Empty(_query.Query(_alice, new BookmarkQueryFilter { ItemId = item }, null).Items);
        Assert.Equal(0, _query.Groups(_alice, new BookmarkQueryFilter { ItemId = item }, null).TotalRecordCount);
        Assert.Equal(0, _query.Query(_alice, new BookmarkQueryFilter { SearchTerm = "Bob" }, null).TotalRecordCount);
        _library.Verify(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()), Times.Never);
    }

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }

    private static Guid[] Ids(params Made[] bookmarks) => bookmarks.Select(b => b.Id).ToArray();

    private Guid[] Listed(BookmarkQueryFilter filter)
        => _query.Query(_alice, filter, BookmarkSortBy.CreatedAt).Items.Select(b => b.Id).Reverse().ToArray();

    private Guid[] Sorted(BookmarkSortBy? sortBy, SortOrder? order)
        => _query.Query(_alice, new BookmarkQueryFilter { SortOrder = order }, sortBy).Items.Select(b => b.Id).ToArray();

    private Guid[] Groups(BookmarkGroupSortBy? sortBy, SortOrder? order)
        => _query.Groups(_alice, new BookmarkQueryFilter { SortOrder = order }, sortBy).Items.Select(g => g.ItemId).ToArray();

    // A bookmark of the user's, added to the store.
    private async Task<Made> AddAsync(
        User user,
        Guid itemId,
        DateTime? created = null,
        DateTime? updated = null,
        string? name = null,
        string? note = null,
        string itemName = "Heat (1995)",
        string kind = "Movie",
        string? seriesName = null,
        int? season = null,
        int? episodeNumber = null,
        int? year = 1995,
        long start = 60,
        long? end = 90,
        DateTime? detached = null,
        bool clipFile = false,
        bool clipQueued = false,
        BookmarkOrigin origin = BookmarkOrigin.Native)
    {
        var bookmark = Make(user, itemId, created, updated, name, note, itemName, kind, seriesName, season, episodeNumber, year, start, end, detached, clipFile, clipQueued, origin);
        await _store.AddAsync(bookmark, Ct);
        return new Made(bookmark.Id, bookmark.ItemId);
    }

    // Many bookmarks in one write.
    private Task AddAllAsync(IEnumerable<Bookmark> bookmarks)
    {
        var list = bookmarks.ToList();
        return _store.UpdateDocumentAsync(d => d with { Bookmarks = [.. d.Bookmarks, .. list] }, Ct);
    }

    // A bookmark of the user's as the store holds it; creation times follow the order of the calls unless given.
    private Bookmark Make(
        User user,
        Guid itemId,
        DateTime? created = null,
        DateTime? updated = null,
        string? name = null,
        string? note = null,
        string itemName = "Heat (1995)",
        string kind = "Movie",
        string? seriesName = null,
        int? season = null,
        int? episodeNumber = null,
        int? year = 1995,
        long start = 60,
        long? end = 90,
        DateTime? detached = null,
        bool clipFile = false,
        bool clipQueued = false,
        BookmarkOrigin origin = BookmarkOrigin.Native)
    {
        var createdAt = created ?? T0.AddSeconds(_added++);
        return new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ItemId = itemId,
            StartTicks = start * TestData.OneSecond,
            EndTicks = end * TestData.OneSecond,
            Name = name,
            Note = note,
            ItemName = itemName,
            Identity = new IdentitySnapshot
            {
                ItemId = itemId,
                Kind = kind,
                Name = itemName,
                Year = year,
                SeriesName = seriesName,
                Season = season,
                Episode = episodeNumber,
                Path = @"C:\media\" + itemName + ".mkv"
            },
            Origin = origin,
            AttachedUtc = createdAt,
            DetachedUtc = detached,
            CreatedAt = createdAt,
            UpdatedAt = updated ?? createdAt,
            Clip = clipFile || clipQueued
                ? new ClipInfo
                {
                    Status = clipFile ? BookmarkClipStatus.Completed : BookmarkClipStatus.Queued,
                    Mode = BookmarkClipMode.Fast,
                    StartTicks = start * TestData.OneSecond,
                    EndTicks = (end ?? start + 1) * TestData.OneSecond,
                    RequestedAt = createdAt,
                    FilePath = clipFile ? _directory.Combine("clips", Guid.NewGuid().ToString("N") + ".mkv") : null
                }
                : null
        };
    }

    /// <summary>What the tests remember of a bookmark they made.</summary>
    private sealed record Made(Guid Id, Guid ItemId);
}
