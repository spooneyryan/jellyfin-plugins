using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.LibraryMaintenance.Api;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Api;

/// <summary>
/// <c>GET /LibraryMaintenance/Duplicates/Groups</c> and <c>GET …/Groups/{groupId}</c> through the
/// registrator's services: administrators only, the effective libraries of two admins with different access (and the
/// cache never serving one's groups to the other), an API key, the options and their 400s, the filters, the orders and
/// the paging, the alternate mode's count, the 202 before the first index, and the body's shape.
/// </summary>
public sealed class GroupsApiTests
{
    private const string Groups = "/LibraryMaintenance/Duplicates/Groups";

    // Alice sees every library; Bob, also an administrator, only Movies (admins get no bypass).
    private static readonly Guid Alice = new("e0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = new("e0000000-0000-0000-0000-00000000000b");

    // Carol, an administrator who can open every library, with a parental limit and a blocked tag.
    private static readonly Guid Carol = new("e0000000-0000-0000-0000-00000000000c");

    [Fact]
    public async Task TheGroups_AnswerAdministratorsOnly()
    {
        var library = Library(out _);
        await using var host = await StartAsync(library);

        foreach (var path in new[] { Groups, Groups + "/0123456789abcdef" })
        {
            using var anonymous = await host.SendAsync(HttpMethod.Get, path);
            using var user = await host.SendAsync(HttpMethod.Get, path, As("user", Alice));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        }

        // No caller of theirs started a build.
        Assert.Empty(library.IdQueries);
    }

    [Fact]
    public async Task TwoAdmins_WithDifferentAccess_EachSeeTheirLibrariesGroups_AndNeverTheOthersResult()
    {
        var library = Library(out var heat);
        await using var host = await StartAsync(library);
        var movies = library.Libraries[0].Id;
        var more = library.Libraries[1].Id;

        // Alice: the cross-library Heat and Alien. Bob: only Alien, with the same options, after Alice's result is cached.
        var alice = await Page(host, Alice);
        var bob = await Page(host, Bob);
        Assert.Equal([movies, more], Ids(alice, "EffectiveLibraryIds"));
        Assert.Equal(["alien|1979", "heat|1995"], Keys(alice));
        Assert.Equal([movies], Ids(bob, "EffectiveLibraryIds"));
        Assert.Equal(["alien|1979"], Keys(bob));
        Assert.Equal(alice.GetProperty("OptionsHash").GetString(), bob.GetProperty("OptionsHash").GetString());

        // In the other order too, now that both are cached.
        Assert.Equal(["alien|1979"], Keys(await Page(host, Bob)));
        Assert.Equal(["alien|1979", "heat|1995"], Keys(await Page(host, Alice)));
        var cache = host.Services.GetRequiredService<DuplicateGroupsService>().Cache;
        Assert.Equal(2, cache.Count); // two library sets; each result carries the other mode's count
        Assert.Equal(2, cache.Misses);

        // Libraries asked for by id: Alice gets those, in the index's order; one Bob can't open is dropped, not shown.
        var aliceMovies = await Page(host, Alice, "?libraryIds=" + movies);
        Assert.Equal([movies], Ids(aliceMovies, "EffectiveLibraryIds"));
        Assert.Equal(["alien|1979"], Keys(aliceMovies));
        var aliceMore = await Page(host, Alice, "?libraryIds=" + more.ToString("N"));
        Assert.Equal([more], Ids(aliceMore, "EffectiveLibraryIds"));
        Assert.Equal((1, 0), (aliceMore.GetProperty("TotalEntries").GetInt32(), aliceMore.GetProperty("GroupCount").GetInt32()));
        Assert.Equal([movies, more], Ids(await Page(host, Alice, "?libraryIds=" + more + "," + movies), "EffectiveLibraryIds"));
        var asked = await Page(host, Bob, "?libraryIds=" + more);
        Assert.Empty(Ids(asked, "EffectiveLibraryIds"));
        Assert.Equal((0, 0), (asked.GetProperty("TotalEntries").GetInt32(), asked.GetProperty("GroupCount").GetInt32()));
        Assert.Equal([movies], Ids(await Page(host, Bob, "?libraryIds=" + more + "," + movies), "EffectiveLibraryIds"));

        // The group's own route: Alice gets it, Bob gets 404 (it isn't one of his groups), as for an id that never was.
        var heatId = Group(alice, "heat|1995").GetProperty("GroupId").GetString();
        using var aliceGroup = await host.SendAsync(HttpMethod.Get, Groups + "/" + heatId, As("admin", Alice));
        using var bobGroup = await host.SendAsync(HttpMethod.Get, Groups + "/" + heatId, As("admin", Bob));
        using var none = await host.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef", As("admin", Alice));
        Assert.Equal(HttpStatusCode.OK, aliceGroup.StatusCode);
        Assert.Equal(heat.Id, Id((await Json(aliceGroup)).GetProperty("SuggestedKeepId")));
        Assert.Equal(HttpStatusCode.NotFound, bobGroup.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Contains("gone", (await Json(none)).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderInTwoLibraries_ShowsOnlyTheCallersLibrary()
    {
        // A folder in two libraries: Movies' folder is also in "Secret Movies" (More Movies, renamed); Bob can open only Movies.
        var library = new DuplicatesLibrary();
        var secret = library.Libraries[1];
        secret.PhysicalFolderIds = [DuplicatesLibrary.MoreMovies, DuplicatesLibrary.Movies];
        secret.Name = "Secret Movies";
        library.Add("Alien", 1979);
        library.Add("Alien", 1979, file: "Alien (1979) - 2.mkv");
        await using var host = await StartAsync(library);

        // Alice first, so Bob's page is made from the projections her request kept (the same snapshot, every library).
        var alice = await Page(host, Alice);
        Assert.Contains("Secret Movies", alice.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, host.Services.GetRequiredService<DuplicateGroupsService>().Projections.Projections);

        foreach (var mode in new[] { "TitleAndYear", "Title" })
        {
            var bob = await Page(host, Bob, "?mode=" + mode);
            var text = bob.GetRawText();
            Assert.DoesNotContain("Secret Movies", text, StringComparison.Ordinal);
            Assert.DoesNotContain(secret.Id.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
            var group = Assert.Single(bob.GetProperty("Items").EnumerateArray());
            Assert.All(group.GetProperty("Entries").EnumerateArray(), e => Assert.Equal("Movies", Assert.Single(e.GetProperty("Libraries").EnumerateArray()).GetProperty("Name").GetString()));
            Assert.DoesNotContain("CrossLibrary", Strings(group, "Flags"));
        }

        // Asking for the shared library by id: it isn't Bob's, so nothing.
        var asked = await Page(host, Bob, "?libraryIds=" + secret.Id.ToString("N"));
        Assert.Empty(Ids(asked, "EffectiveLibraryIds"));
        Assert.Empty(Keys(asked));
        Assert.Equal(1, host.Services.GetRequiredService<DuplicateGroupsService>().Projections.Projections);
    }

    [Fact]
    public async Task AMergedSetAcrossLibraries_ShowsOnlyTheCallersVersions_AndNothingAboutTheOthers()
    {
        // A version set across libraries: Bob's Heat is a version of a set whose primary is in "Secret Movies", which he can't open; a
        // copy of his own groups with it. Nothing of the secret version shows: no path, id, size, library, source, state.
        var library = new DuplicatesLibrary();
        library.Libraries[1].Name = "Secret Movies";
        var mine = library.Add("Heat", 1995);
        mine.Size = 10;
        var secret = library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies, file: "Heat (1995) SECRET.mkv");
        secret.Size = 999;
        secret.LinkedAlternateVersions = [JellyfinLine.Link(mine.Id, mine.Path)];
        mine.SetPrimaryVersion(secret.Id);
        var copy = library.Add("Heat", 1995, file: "Heat (1995) - copy.mkv");
        copy.Size = 5000;
        await using var host = await StartAsync(library);

        // Alice sees the set whole: the secret primary represents it, with its version, two sources and both libraries.
        var alice = Group(await Page(host, Alice), "heat|1995");
        var set = alice.GetProperty("Entries").EnumerateArray().Single(e => Id(e.GetProperty("ItemId")) == secret.Id);
        Assert.Equal([mine.Id], set.GetProperty("LinkedVersionIds").EnumerateArray().Select(Id));
        Assert.Equal(2, set.GetProperty("SourceCount").GetInt32());
        Assert.Equal(["Secret Movies", "Movies"], set.GetProperty("Libraries").EnumerateArray().Select(l => l.GetProperty("Name").GetString()));

        var page = await Page(host, Bob);
        var text = page.GetRawText();
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Movies", text, StringComparison.Ordinal);
        Assert.DoesNotContain(secret.Id.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
        // The secret version's size as a value: no number in the page is it. (A text search for "999" also hit the index's
        // GeneratedUtc now and then, whose 100 ns digits are the one part of this page that changes from run to run.)
        Assert.DoesNotContain(999m, Numbers(page));
        Assert.Equal((2, 2), (page.GetProperty("TotalMovies").GetInt32(), page.GetProperty("TotalEntries").GetInt32()));
        Assert.Equal((10L, false), (page.GetProperty("ReclaimableBytes").GetInt64(), page.GetProperty("ReclaimableIsLowerBound").GetBoolean()));

        var group = Group(page, "heat|1995");
        Assert.Equal(copy.Id, Id(group.GetProperty("SuggestedKeepId")));
        Assert.Empty(Strings(group, "Flags"));
        var entry = group.GetProperty("Entries").EnumerateArray().Single(e => Id(e.GetProperty("ItemId")) == mine.Id);
        Assert.Equal(mine.Path, entry.GetProperty("Path").GetString());
        Assert.Equal(10, entry.GetProperty("SizeBytes").GetInt64());
        Assert.Equal(1, entry.GetProperty("SourceCount").GetInt32());
        Assert.Empty(entry.GetProperty("LinkedVersionIds").EnumerateArray());
        Assert.Equal("None", entry.GetProperty("VersionState").GetString());
        Assert.Empty(Strings(entry, "Flags"));

        // Every id Bob is given is one of his library's items; the group's own route agrees.
        Assert.All(group.GetProperty("Entries").EnumerateArray(), e => Assert.Contains(Id(e.GetProperty("ItemId")), new[] { mine.Id, copy.Id }));
        using var one = await host.SendAsync(HttpMethod.Get, Groups + "/" + group.GetProperty("GroupId").GetString(), As("admin", Bob));
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.DoesNotContain("SECRET", (await Json(one)).GetRawText(), StringComparison.Ordinal);

        // The group id is made of the representatives the caller sees, so it is Bob's own (it says nothing of the secret
        // primary); Alice's id isn't a group of his.
        Assert.NotEqual(alice.GetProperty("GroupId").GetString(), group.GetProperty("GroupId").GetString());
        using var hers = await host.SendAsync(HttpMethod.Get, Groups + "/" + alice.GetProperty("GroupId").GetString(), As("admin", Bob));
        Assert.Equal(HttpStatusCode.NotFound, hers.StatusCode);
    }

    [Theory]
    [InlineData("?mode=Title&mode=Fuzzy")]
    [InlineData("?mode=Title&mode=Title")]
    [InlineData("?sort=title&sort=size")]
    [InlineData("?libraryIds=c0000000000000000000000000000001&libraryIds=c0000000000000000000000000000002")]
    [InlineData("?unknownYear=MatchAny&UnknownYear=MatchAny")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?startIndex=0&startIndex=0")]
    [InlineData("?confidence=Likely&confidence=Possible")]
    [InlineData("?search=a&search=b")]
    public async Task AnOptionGivenTwice_Is400(string query)
    {
        // An option given twice is refused, not the first one taken silently. A list is one value, comma-separated.
        var library = Library(out _);
        await using var host = await StartAsync(library);

        using var response = await host.SendAsync(HttpMethod.Get, Groups + query, As("admin", Alice));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("more than once", (await Json(response)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(library.IdQueries);

        var grouping = query.Contains("mode=", StringComparison.Ordinal) || query.Contains("nknownYear", StringComparison.Ordinal) || query.Contains("libraryIds", StringComparison.Ordinal);
        using var group = await host.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef" + query, As("admin", Alice));
        Assert.Equal(grouping ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound, group.StatusCode);
    }

    [Fact]
    public async Task AFailedFirstBuild_Is503WithTheError_NotAnEndless202_AndARebuildRetries()
    {
        // A failed first build: the reads answer 503 with the reason, not a 202 that never ends.
        var library = Library(out _);
        library.BeforeItemIds = _ => throw new InvalidOperationException(@"C:\secret\path is locked.");
        await using var host = await StartAsync(library);

        using var failed = await host.SendAsync(HttpMethod.Get, Groups, As("admin", Alice));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var detail = (await Json(failed)).GetProperty("detail").GetString();
        Assert.StartsWith("The movie index couldn't be built (InvalidOperationException)", detail, StringComparison.Ordinal);
        Assert.Contains("Rebuild", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", detail, StringComparison.OrdinalIgnoreCase);
        using var failedGroup = await host.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef", As("admin", Alice));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedGroup.StatusCode);

        // The page polls: no build within the back-off.
        var queries = library.IdQueries.Count;
        using var again = await host.SendAsync(HttpMethod.Get, Groups, As("admin", Alice));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, again.StatusCode);
        Assert.Equal(queries, library.IdQueries.Count);

        // Fixed, and rebuilt: the groups, with no error.
        library.BeforeItemIds = null;
        using var rebuild = await host.SendAsync(HttpMethod.Post, "/LibraryMaintenance/Duplicates/Index/Rebuild", As("admin", Alice));
        Assert.Equal(HttpStatusCode.Accepted, rebuild.StatusCode);
        await host.Services.GetRequiredService<MovieIndexService>().Rebuild().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var page = await Page(host, Alice);
        Assert.Equal(["alien|1979", "heat|1995"], Keys(page));
        Assert.True(!page.GetProperty("Index").TryGetProperty("LastError", out var none) || none.ValueKind == JsonValueKind.Null);

        // A later failure keeps the groups, and says so.
        library.BeforeItemIds = _ => throw new InvalidOperationException("locked");
        await host.Services.GetRequiredService<MovieIndexService>().Rebuild().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var kept = await Page(host, Alice);
        Assert.Equal(["alien|1979", "heat|1995"], Keys(kept));
        Assert.StartsWith("The movie index couldn't be built", kept.GetProperty("Index").GetProperty("LastError").GetString(), StringComparison.Ordinal);
        Assert.True(kept.GetProperty("Index").GetProperty("Stale").GetBoolean());
    }

    [Fact]
    public async Task ANewSnapshot_DropsTheOldResults_AtOnce()
    {
        // The cache doesn't keep an old snapshot alive until the next read.
        var library = Library(out _);
        await using var host = await StartAsync(library);
        await Page(host, Alice);
        var groups = host.Services.GetRequiredService<DuplicateGroupsService>();
        Assert.Equal(1, groups.Cache.Count);
        Assert.True(groups.Projections.HasProjection);

        await host.Services.GetRequiredService<MovieIndexService>().Rebuild().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(0, groups.Cache.Count);
        Assert.False(groups.Projections.HasProjection);

        // New title rules drop them too (their results can't be asked for any more); a setting that isn't one doesn't.
        await Page(host, Alice);
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = library.Configuration.IncludedLibraryIds, RequestWaitSeconds = 3 };
        Assert.Equal(1, groups.Cache.Count);
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = library.Configuration.IncludedLibraryIds, ArticleLanguages = ["En", "De"] };
        Assert.Equal(0, groups.Cache.Count);
        Assert.False(groups.Projections.HasProjection);
    }

    [Fact]
    public async Task AnApiKey_SeesEveryLibraryTheIndexRead_AndAnUnknownUserIs403()
    {
        var library = Library(out _);
        await using var host = await StartAsync(library);

        using var key = await host.SendAsync(HttpMethod.Get, Groups, r => r.Headers.Add(JellyfinAuth.CallerHeader, "apikey"));
        Assert.Equal(HttpStatusCode.OK, key.StatusCode);
        var body = await Json(key);
        Assert.Equal([library.Libraries[0].Id, library.Libraries[1].Id], Ids(body, "EffectiveLibraryIds"));
        Assert.Equal(["alien|1979", "heat|1995"], Keys(body));

        using var gone = await host.SendAsync(HttpMethod.Get, Groups, As("admin", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, gone.StatusCode);
    }

    [Theory]
    [InlineData("?mode=Fuzzy", "mode must be TitleAndYear, Title")]
    [InlineData("?mode=1", "mode must be")]
    [InlineData("?unknownYear=Sometimes", "unknownYear must be MatchUnknownOnly, MatchAny")]
    [InlineData("?libraryIds=movies", "libraryIds: \"movies\" is not a library id.")]
    [InlineData("?libraryIds=00000000-0000-0000-0000-000000000000", "is not a library id")]
    [InlineData("?confidence=Likely,Sure", "confidence must be Confirmed, Likely, Possible, Conflict")]
    [InlineData("?sort=Random", "sort must be Confidence, Title, Reclaimable, Size, Newest")]
    [InlineData("?startIndex=-1", "startIndex must be 0 or more.")]
    [InlineData("?limit=0", "limit must be 1 to 200.")]
    [InlineData("?limit=201", "limit must be 1 to 200.")]
    public async Task ABadOption_Is400_AndSaysWhich(string query, string message)
    {
        var library = Library(out _);
        await using var host = await StartAsync(library);

        using var response = await host.SendAsync(HttpMethod.Get, Groups + query, As("admin", Alice));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, (await Json(response)).GetProperty("detail").GetString(), StringComparison.Ordinal);

        // An option is checked before the index is read.
        Assert.Empty(library.IdQueries);

        // The group's route takes the grouping options only (the others are the page's).
        using var group = await host.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef" + query, As("admin", Alice));
        var grouping = query.StartsWith("?mode", StringComparison.Ordinal) || query.StartsWith("?unknownYear", StringComparison.Ordinal) || query.StartsWith("?libraryIds", StringComparison.Ordinal);
        Assert.Equal(grouping ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound, group.StatusCode);
    }

    [Fact]
    public async Task TheOptions_ChangeTheGrouping_AndTheOtherModesCountComesWithIt()
    {
        var library = Library(out _);
        library.Add("Alien", null);
        library.Add("Heat", 1986);
        library.Add("Heat", 1986, file: "Heat (1986) - 2.mkv");
        await using var host = await StartAsync(library);

        // The settings' default (Title + Year, MatchUnknownOnly), then the names in any case. Title only has 2 groups.
        var byYear = await Page(host, Alice);
        Assert.Equal(("TitleAndYear", "MatchUnknownOnly"), (byYear.GetProperty("Mode").GetString(), byYear.GetProperty("UnknownYear").GetString()));
        Assert.Equal(["alien|1979", "heat|1986", "heat|1995"], Keys(byYear));
        Assert.Equal(2, byYear.GetProperty("AlternateModeGroupCount").GetInt32());

        var any = await Page(host, Alice, "?unknownyear=matchany");
        Assert.Equal("MatchAny", any.GetProperty("UnknownYear").GetString());
        Assert.Equal(3, Group(any, "alien|1979").GetProperty("Entries").GetArrayLength());
        Assert.NotEqual(byYear.GetProperty("OptionsHash").GetString(), any.GetProperty("OptionsHash").GetString());

        var title = await Page(host, Alice, "?mode=title");
        Assert.Equal("Title", title.GetProperty("Mode").GetString());
        Assert.Equal(["alien", "heat"], Keys(title));
        Assert.Equal(4, Group(title, "heat").GetProperty("Entries").GetArrayLength());
        Assert.Equal(3, title.GetProperty("AlternateModeGroupCount").GetInt32());

        // The settings' default mode applies when none is asked for.
        library.Configuration.DefaultMatchMode = "Title";
        Assert.Equal("Title", (await Page(host, Alice)).GetProperty("Mode").GetString());
    }

    [Fact]
    public async Task TheFilters_TheOrders_AndThePaging()
    {
        var library = new DuplicatesLibrary();
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Pair(library, "Amélie", 2001, size: 10, added: older, tmdb: "194");
        Pair(library, "Zodiac", 2007, size: 50, added: older);
        Pair(library, "The Matrix", 1999, size: 30, added: older.AddYears(3), tmdb: "603");
        var big = Pair(library, "Heat", 1995, size: 20, added: older);
        library.Add("Heat", 1995, file: "Heat (3).mkv").Size = 20;
        await using var host = await StartAsync(library);

        // Confidence: Confirmed (Amélie, Matrix) before Likely (Heat, Zodiac), each by title key.
        Assert.Equal(["amelie|2001", "matrix|1999", "heat|1995", "zodiac|2007"], Keys(await Page(host, Alice)));
        Assert.Equal(["amelie|2001", "heat|1995", "matrix|1999", "zodiac|2007"], Keys(await Page(host, Alice, "?sort=title")));
        Assert.Equal(["zodiac|2007", "heat|1995", "matrix|1999", "amelie|2001"], Keys(await Page(host, Alice, "?sort=Reclaimable")));
        Assert.Equal(["heat|1995", "amelie|2001", "matrix|1999", "zodiac|2007"], Keys(await Page(host, Alice, "?sort=Size")));
        Assert.Equal("matrix|1999", Keys(await Page(host, Alice, "?sort=Newest"))[0]);

        // The filters keep the totals of every group; TotalRecordCount is what they keep.
        var likely = await Page(host, Alice, "?confidence=likely");
        Assert.Equal(["heat|1995", "zodiac|2007"], Keys(likely));
        Assert.Equal((4, 2), (likely.GetProperty("GroupCount").GetInt32(), likely.GetProperty("TotalRecordCount").GetInt32()));
        Assert.Equal(10 + 50 + 30 + 40, likely.GetProperty("ReclaimableBytes").GetInt64());
        Assert.Equal(["amelie|2001", "matrix|1999"], Keys(await Page(host, Alice, "?confidence=Confirmed, Conflict")));

        // The text filter: the title rules' key (amelie finds Amélie, "the matr" The Matrix) or a name's text.
        Assert.Equal(["amelie|2001"], Keys(await Page(host, Alice, "?search=amelie")));
        Assert.Equal(["matrix|1999"], Keys(await Page(host, Alice, "?search=the%20matr")));
        Assert.Equal(["zodiac|2007"], Keys(await Page(host, Alice, "?search=ODIA")));

        // "The" alone is its own key, which no group has: only The Matrix's name holds it.
        Assert.Equal(["matrix|1999"], Keys(await Page(host, Alice, "?search=The")));
        Assert.Empty(Keys(await Page(host, Alice, "?search=nothing")));

        // The paging.
        var page = await Page(host, Alice, "?startIndex=1&limit=2");
        Assert.Equal(["matrix|1999", "heat|1995"], Keys(page));
        Assert.Equal((1, 4), (page.GetProperty("StartIndex").GetInt32(), page.GetProperty("TotalRecordCount").GetInt32()));
        Assert.Empty(Keys(await Page(host, Alice, "?startIndex=9")));
        Assert.Equal(3, Group(await Page(host, Alice, "?sort=Size&limit=1"), "heat|1995").GetProperty("Entries").GetArrayLength());
        Assert.Equal(big.Id, Id(Group(await Page(host, Alice), "heat|1995").GetProperty("SuggestedKeepId")));
    }

    [Fact]
    public async Task WithNoIndexYet_ItIs202_WithTheBuildsState_ThenTheGroups()
    {
        var library = Library(out _);
        library.Configuration.RequestWaitSeconds = 0;
        using var gate = new ManualResetEventSlim(false);
        library.BeforeItemIds = _ => gate.Wait(TimeSpan.FromSeconds(10));
        await using var host = await StartAsync(library);

        using var pending = await host.SendAsync(HttpMethod.Get, Groups, As("admin", Alice));
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        var body = await Json(pending);
        Assert.True(body.GetProperty("Rebuilding").GetBoolean());
        Assert.False(body.GetProperty("ScanInProgress").GetBoolean());
        Assert.False(body.TryGetProperty("Items", out _));
        using var pendingGroup = await host.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef", As("admin", Alice));
        Assert.Equal(HttpStatusCode.Accepted, pendingGroup.StatusCode);

        gate.Set();
        await host.Services.GetRequiredService<MovieIndexService>().Rebuild().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var page = await Page(host, Alice);
        Assert.Equal(["alien|1979", "heat|1995"], Keys(page));
        Assert.False(page.GetProperty("Index").GetProperty("Stale").GetBoolean());
        Assert.True(page.GetProperty("Index").GetProperty("Version").GetInt64() >= 1);
    }

    [Fact]
    public async Task WithDuplicatesTurnedOff_TheGroupsAre404()
    {
        var library = Library(out _);
        library.Configuration.DuplicatesEnabled = false;
        await using var host = await StartAsync(library);

        using var page = await host.SendAsync(HttpMethod.Get, Groups, As("admin", Alice));
        using var group = await host.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef?mode=Fuzzy", As("admin", Alice));
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, group.StatusCode);
        Assert.Empty(library.IdQueries);
    }

    [Fact]
    public async Task TheBody_CarriesTheGroupsAndTheirEntries()
    {
        var library = new DuplicatesLibrary();
        var a = library.Add("Blade Runner", 1982, file: "Blade Runner (1982) - 1080p.mkv");
        var b = library.Add("Blade Runner", 1982, file: "Blade Runner (1982) - 2160p.mkv");
        DuplicatesLibrary.MergeVersions(a, b);
        a.Width = 1920;
        a.Height = 800;
        a.Size = 30;
        b.Size = 40;
        a.SetProviderId(MetadataProvider.Tmdb, "78");
        a.SetProviderId(MetadataProvider.Imdb, "tt0083658");
        var copy = library.Add("Blade Runner", 1982, DuplicatesLibrary.MoreMovies);
        copy.Width = 3840;
        copy.Size = 50;
        copy.SetProviderId(MetadataProvider.Tmdb, "78");
        await using var host = await StartAsync(library);

        var body = await Page(host, Alice);
        Assert.Equal(1, body.GetProperty("Index").GetProperty("Version").GetInt64());
        Assert.False(body.GetProperty("Index").GetProperty("BuiltDuringScan").GetBoolean());
        Assert.Equal((3, 2, 1, 0), (body.GetProperty("TotalMovies").GetInt32(), body.GetProperty("TotalEntries").GetInt32(), body.GetProperty("GroupCount").GetInt32(), body.GetProperty("HiddenByIgnore").GetInt32()));
        Assert.Equal(16, body.GetProperty("OptionsHash").GetString()!.Length);

        var group = Group(body, "blade runner|1982");
        Assert.Equal(16, group.GetProperty("GroupId").GetString()!.Length);
        Assert.Equal("Blade Runner", group.GetProperty("DisplayTitle").GetString());
        Assert.Equal("Confirmed", group.GetProperty("Confidence").GetString());
        Assert.Equal(["TitleAndYear", "SameTmdbId"], Strings(group, "Reasons"));
        Assert.Equal(["CrossLibrary"], Strings(group, "Flags"));
        Assert.Equal([1982], group.GetProperty("Years").EnumerateArray().Select(y => y.GetInt32()));
        Assert.Equal("None", group.GetProperty("IgnoreState").GetString());

        // The keep: both have ids and are files; the copy is wider. The set's 70 bytes are reclaimable.
        Assert.Equal(copy.Id, Id(group.GetProperty("SuggestedKeepId")));
        Assert.Equal(70, group.GetProperty("ReclaimableBytes").GetInt64());
        var entries = group.GetProperty("Entries").EnumerateArray().ToList();
        Assert.Equal([copy.Id, a.Id], entries.Select(e => Id(e.GetProperty("ItemId"))));
        Assert.Equal("2160p", entries[0].GetProperty("ResolutionLabel").GetString());
        Assert.Equal("More Movies", Assert.Single(entries[0].GetProperty("Libraries").EnumerateArray()).GetProperty("Name").GetString());

        var set = entries[1];
        Assert.Equal("Blade Runner", set.GetProperty("Name").GetString());
        Assert.Equal(1982, set.GetProperty("Year").GetInt32());
        Assert.Equal("ProductionYear", set.GetProperty("YearSource").GetString());
        Assert.Equal("1080p", set.GetProperty("ResolutionLabel").GetString());
        Assert.Equal((1920, 800), (set.GetProperty("Width").GetInt32(), set.GetProperty("Height").GetInt32()));
        Assert.Equal(30, set.GetProperty("SizeBytes").GetInt64());
        Assert.Equal(2, set.GetProperty("SourceCount").GetInt32());
        Assert.Equal([b.Id], set.GetProperty("LinkedVersionIds").EnumerateArray().Select(Id));
        Assert.Equal("VideoFile", set.GetProperty("VideoType").GetString());
        Assert.Equal("None", set.GetProperty("VersionState").GetString());
        Assert.Equal(("78", "tt0083658"), (set.GetProperty("ProviderIds").GetProperty("Tmdb").GetString(), set.GetProperty("ProviderIds").GetProperty("Imdb").GetString()));
        Assert.Equal(a.Path, set.GetProperty("Path").GetString());
        Assert.Equal("Active", set.GetProperty("EntryIgnoreState").GetString());
        Assert.Empty(Strings(set, "Flags"));
        Assert.Empty(set.GetProperty("MarkedWith").EnumerateArray());
        Assert.True(!set.TryGetProperty("Edition", out var edition) || edition.ValueKind == JsonValueKind.Null);

        // Diagnostics: the cache, the last grouping, and the recent ones: one grouping, which counted the other mode's groups
        // on the same buckets, from one projection of the snapshot.
        var groups = await GroupsDiagnostics(host);
        Assert.Equal("Server-wide: every caller's groupings", groups.GetProperty("Scope").GetString());
        Assert.Equal(1, groups.GetProperty("CachedResults").GetInt32());
        Assert.Equal(1, groups.GetProperty("Groupings").GetInt64());
        Assert.Equal(1, groups.GetProperty("Projections").GetInt64());
        Assert.True(groups.GetProperty("ProjectionKept").GetBoolean());
        Assert.True(groups.GetProperty("LastGroupingMs").GetDouble() >= 0);
        var recent = Assert.Single(groups.GetProperty("Recent").EnumerateArray());
        Assert.Equal("TitleAndYear", recent.GetProperty("Mode").GetString());
        Assert.Equal(("MatchUnknownOnly", 1L, 2, 1, 1), (recent.GetProperty("UnknownYear").GetString(), recent.GetProperty("Version").GetInt64(), recent.GetProperty("Entries").GetInt32(), recent.GetProperty("Groups").GetInt32(), recent.GetProperty("AlternateModeGroups").GetInt32()));
        Assert.True(recent.GetProperty("Ms").GetDouble() >= recent.GetProperty("ProjectionMs").GetDouble());
        Assert.Equal(groups.GetProperty("LastGroupingMs").GetDouble(), recent.GetProperty("Ms").GetDouble());
    }

    [Fact]
    public async Task Diagnostics_ListTheLast16Groupings_TheNewestLast()
    {
        // Alice's three library sets (every one, Movies, More Movies), each in both modes with both unknown-year policies:
        // 12 groupings, each counting the other mode on its buckets; "matchany" again each time, which the cache answers.
        var library = Library(out _);
        await using var host = await StartAsync(library);
        string[] sets = [string.Empty, "&libraryIds=" + library.Libraries[0].Id.ToString("N"), "&libraryIds=" + library.Libraries[1].Id.ToString("N")];
        foreach (var set in sets)
        {
            foreach (var mode in new[] { "TitleAndYear", "Title" })
            {
                foreach (var unknownYear in new[] { "MatchUnknownOnly", "MatchAny", "matchany" })
                {
                    await Page(host, Alice, "?mode=" + mode + "&unknownYear=" + unknownYear + set);
                }
            }
        }

        var groups = await GroupsDiagnostics(host);
        Assert.Equal(12, groups.GetProperty("Groupings").GetInt64());
        Assert.Equal(12, groups.GetProperty("Recent").GetArrayLength());
        Assert.Equal(1, groups.GetProperty("Projections").GetInt64());

        // The cache keeps 8, so every library's four option sets were dropped: asked again, three group again, which drops
        // Movies' first three in turn, and two of those are asked again. 17 groupings; the last 16 show, the first one's gone.
        await Page(host, Alice, "?mode=TitleAndYear&unknownYear=MatchUnknownOnly");
        await Page(host, Alice, "?mode=TitleAndYear&unknownYear=MatchAny");
        await Page(host, Alice, "?mode=Title&unknownYear=MatchUnknownOnly");
        await Page(host, Alice, "?mode=TitleAndYear&unknownYear=MatchUnknownOnly" + sets[1]);
        await Page(host, Alice, "?mode=TitleAndYear&unknownYear=MatchAny" + sets[1]);
        groups = await GroupsDiagnostics(host);
        Assert.Equal(17, groups.GetProperty("Groupings").GetInt64());
        Assert.Equal(1, groups.GetProperty("Projections").GetInt64());
        Assert.Equal(16, DuplicateGroupsService.RecentCapacity);
        var recent = groups.GetProperty("Recent").EnumerateArray()
            .Select(r => (r.GetProperty("Mode").GetString(), r.GetProperty("UnknownYear").GetString(), r.GetProperty("Entries").GetInt32(), r.GetProperty("Groups").GetInt32(), r.GetProperty("AlternateModeGroups").GetInt32()))
            .ToList();
        Assert.Equal(16, recent.Count);
        Assert.Equal(("TitleAndYear", "MatchAny", 4, 2, 2), recent[0]); // every library: Heat and Alien
        Assert.Equal(("Title", "MatchUnknownOnly", 4, 2, 2), recent[1]);
        Assert.Equal(("TitleAndYear", "MatchUnknownOnly", 3, 1, 1), recent[3]); // Movies: Alien
        Assert.Equal(("TitleAndYear", "MatchUnknownOnly", 1, 0, 0), recent[7]); // More Movies: one Heat
        Assert.Equal(("TitleAndYear", "MatchUnknownOnly", 4, 2, 2), recent[11]);
        Assert.Equal(("TitleAndYear", "MatchAny", 3, 1, 1), recent[^1]);
    }

    // ------------------------------------------------------------------ helpers

    // Heat (1995) in Movies and in More Movies; Alien (1979) twice in Movies; Kids (not indexed) has a third Heat.
    private static DuplicatesLibrary Library(out Movie heat)
    {
        var library = new DuplicatesLibrary();
        heat = library.Add("Heat", 1995);
        heat.Size = 20;
        library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies).Size = 10;
        library.Add("Heat", 1995, DuplicatesLibrary.Kids);
        library.Add("Alien", 1979);
        library.Add("Alien", 1979, file: "Alien (1979) - 2.mkv");
        return library;
    }

    private static Movie Pair(DuplicatesLibrary library, string name, int year, long size, DateTime added, string? tmdb = null)
    {
        var first = library.Add(name, year);
        var second = library.Add(name, year, file: name + " (2).mkv");
        foreach (var movie in new[] { first, second })
        {
            movie.Size = size;
            movie.DateCreated = added;
            if (tmdb is not null)
            {
                movie.SetProviderId(MetadataProvider.Tmdb, tmdb);
            }
        }

        // The second is larger, so it is the keep, and the first's bytes are reclaimable.
        second.Size = size + 1;
        return second;
    }

    private static Action<HttpRequestMessage> As(string caller, Guid userId) => r =>
    {
        r.Headers.Add(JellyfinAuth.CallerHeader, caller);
        r.Headers.Add(JellyfinAuth.UserIdHeader, userId.ToString());
    };

    private static async Task<JsonElement> Page(WebHostHarness host, Guid admin, string query = "")
    {
        using var response = await host.SendAsync(HttpMethod.Get, Groups + query, As("admin", admin));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    private static async Task<JsonElement> GroupsDiagnostics(WebHostHarness host)
    {
        using var diagnostics = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Admin/Diagnostics", As("admin", Alice));
        Assert.Equal(HttpStatusCode.OK, diagnostics.StatusCode);
        return (await Json(diagnostics)).GetProperty("DuplicateGroups");
    }

    [Fact]
    public async Task AnAdmin_WithAParentalLimitAndABlockedTag_NeverIsShownCountedOrKeptAMovieTheyCantOpen()
    {
        var library = new DuplicatesLibrary();

        // Heat: three copies, the largest (the keep for everyone else) rated R, beyond Carol's limit.
        var rated = library.Add("Heat", 1995);
        rated.Size = 30;
        rated.OfficialRating = "R";
        var second = library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies);
        second.Size = 20;
        var third = library.Add("Heat", 1995, file: "Heat (1995) - 2.mkv");
        third.Size = 10;

        // Alien: a pair, one tagged with the tag Carol's policy blocks, so she has no Alien group.
        library.Add("Alien", 1979);
        var tagged = library.Add("Alien", 1979, file: "Alien (1979) - 2.mkv");
        tagged.Tags = ["gore"];

        // Ronin: a version set of two (its second version rated R) and a third copy: Carol sees the set's root alone.
        var root = library.Add("Ronin", 1998);
        var version = library.Add("Ronin", 1998, file: "Ronin (1998) - 4K.mkv");
        version.OfficialRating = "R";
        DuplicatesLibrary.MergeVersions(root, version);
        library.Add("Ronin", 1998, DuplicatesLibrary.MoreMovies);

        var carol = new User("carol", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Carol, MaxParentalRatingScore = 13 };
        carol.SetPreference(PreferenceKind.BlockedTags, ["gore"]);
        library.Fake.Hide(carol, rated, tagged, version);
        await using var host = await StartAsync(library, carol);

        var alice = await Page(host, Alice);
        Assert.Equal(["alien|1979", "heat|1995", "ronin|1998"], Keys(alice));
        Assert.Equal(rated.Id, Id(Group(alice, "heat|1995").GetProperty("SuggestedKeepId")));
        Assert.Contains(version.Id, Group(alice, "ronin|1998").GetProperty("Entries").EnumerateArray().SelectMany(e => Ids(e, "LinkedVersionIds")));
        var aliceQueries = library.Fake.Queries.Count(q => q.ItemIds.Length > 0 && q.User is not null);

        // Carol, after Alice's result is cached with the same options and libraries.
        var hers = await Page(host, Carol);
        Assert.Equal(Ids(alice, "EffectiveLibraryIds"), Ids(hers, "EffectiveLibraryIds"));
        Assert.Equal(["heat|1995", "ronin|1998"], Keys(hers));
        var heat = Group(hers, "heat|1995");
        Assert.Equal([second.Id, third.Id], heat.GetProperty("Entries").EnumerateArray().Select(e => Id(e.GetProperty("ItemId"))).ToList());
        Assert.Equal(second.Id, Id(heat.GetProperty("SuggestedKeepId")));
        Assert.Equal(10, heat.GetProperty("ReclaimableBytes").GetInt64());
        var ronin = Group(hers, "ronin|1998");
        Assert.All(ronin.GetProperty("Entries").EnumerateArray(), e =>
        {
            Assert.Empty(e.GetProperty("LinkedVersionIds").EnumerateArray());
            Assert.Equal(1, e.GetProperty("SourceCount").GetInt32());
        });
        Assert.Equal(alice.GetProperty("TotalMovies").GetInt32() - 3, hers.GetProperty("TotalMovies").GetInt32());

        // Nothing she can't open anywhere in her answers: not an id, not a path.
        using var one = await host.SendAsync(HttpMethod.Get, Groups + "/" + heat.GetProperty("GroupId").GetString(), As("admin", Carol));
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        foreach (var body in new[] { hers.GetRawText(), (await Json(one)).GetRawText() })
        {
            foreach (var item in new[] { rated, tagged, version })
            {
                Assert.DoesNotContain(item.Id.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(item.Path.Replace(@"\", @"\\", StringComparison.Ordinal), body, StringComparison.Ordinal);
            }
        }

        // Alice's Heat group is not Carol's (another keep, other entries); the index's counts and Diagnostics follow.
        using var aliceHeat = await host.SendAsync(HttpMethod.Get, Groups + "/" + Group(alice, "heat|1995").GetProperty("GroupId").GetString(), As("admin", Carol));
        Assert.Equal(HttpStatusCode.NotFound, aliceHeat.StatusCode);
        using var index = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Duplicates/Index", As("admin", Carol));
        using var aliceIndex = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Duplicates/Index", As("admin", Alice));
        Assert.Equal((await Json(aliceIndex)).GetProperty("Movies").GetInt32() - 3, (await Json(index)).GetProperty("Movies").GetInt32());
        using var diagnostics = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Admin/Diagnostics", As("admin", Carol));
        Assert.StartsWith("Hidden", Assert.Single((await Json(diagnostics)).GetProperty("DuplicatesIndex").EnumerateObject()).Value.GetString(), StringComparison.Ordinal);

        // The fast path: Alice (no item limits) made no item query; Carol's check made some, once for her two requests.
        Assert.Equal(0, aliceQueries);
        var carolQueries = library.Fake.Queries.Count(q => q.ItemIds.Length > 0 && q.User?.Id == Carol);
        Assert.True(carolQueries > 0);
        await Page(host, Carol);
        Assert.Equal(carolQueries, library.Fake.Queries.Count(q => q.ItemIds.Length > 0 && q.User is not null));
    }

    [Fact]
    public async Task TheIndexsLibraries_ForAnAdminWithAParentalLimit_CountOnlyTheMoviesSheCanSee()
    {
        // Before this was fixed, GET /Index's Movies left her hidden movie out, its Libraries[].Movies didn't.
        var library = new DuplicatesLibrary();
        var rated = library.Add("Heat", 1995);
        rated.OfficialRating = "R";
        library.Add("Heat", 1995, file: "Heat (1995) - 2.mkv");
        library.Add("Alien", 1979);
        library.Add("Alien", 1979, DuplicatesLibrary.MoreMovies);
        var carol = new User("carol", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Carol, MaxParentalRatingScore = 13 };
        library.Fake.Hide(carol, rated);
        await using var host = await StartAsync(library, carol);
        await Page(host, Alice);

        using var aliceResponse = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Duplicates/Index", As("admin", Alice));
        using var carolResponse = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Duplicates/Index", As("admin", Carol));
        var alice = await Json(aliceResponse);
        var hers = await Json(carolResponse);
        var counts = (JsonElement index) => index.GetProperty("Libraries").EnumerateArray().ToDictionary(l => l.GetProperty("Name").GetString()!, l => l.GetProperty("Movies").GetInt32());
        Assert.Equal(new Dictionary<string, int> { ["Movies"] = 3, ["More Movies"] = 1 }, counts(alice));
        Assert.Equal(new Dictionary<string, int> { ["Movies"] = 2, ["More Movies"] = 1 }, counts(hers));
        Assert.Equal(3, hers.GetProperty("Movies").GetInt32());
        Assert.Equal(hers.GetProperty("Movies").GetInt32(), counts(hers).Values.Sum());
    }

    [Fact]
    public async Task TheIndexsLeftOutCounts_ForAnAdminWithAParentalLimit_CountOnlyTheMoviesSheCanSee()
    {
        // The 1.0 verifier: an administrator who opens every library (no scope) but has a parental limit got the server's
        // Skipped and Excluded on Diagnostics ("Skipped: no name", "Left out by the excluded folders"), the movies she
        // can't see included. Here she sees every indexed movie, and one of each left-out pair is hidden from her.
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        library.Add("Heat", 1995, file: "Heat (1995) - 2.mkv");
        var nameless = library.Add(" ", 2001);
        nameless.OfficialRating = "R";
        library.Add(" ", 2002, DuplicatesLibrary.MoreMovies);
        var clip = library.Add("Alien", 1979, file: "Alien - clip.mkv");
        clip.Path = @"C:\media\Movies\Clips\Alien - clip.mkv";
        clip.OfficialRating = "R";
        var other = library.Add("Ronin", 1998, file: "Ronin - clip.mkv");
        other.Path = @"C:\media\Movies\Clips\Ronin - clip.mkv";
        library.Configuration = new PluginConfiguration { ExcludedPathPrefixes = [@"C:\media\Movies\Clips"] };
        var carol = new User("carol", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Carol, MaxParentalRatingScore = 13 };
        library.Fake.Hide(carol, nameless, clip);
        await using var host = await StartAsync(library, carol);
        await Page(host, Alice);

        using var aliceResponse = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Duplicates/Index", As("admin", Alice));
        using var carolResponse = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Duplicates/Index", As("admin", Carol));
        var alice = await Json(aliceResponse);
        var hers = await Json(carolResponse);
        Assert.Equal((2, 2, 2), (alice.GetProperty("Movies").GetInt32(), alice.GetProperty("Skipped").GetInt32(), alice.GetProperty("Excluded").GetInt32()));
        Assert.Equal((2, 1, 1), (hers.GetProperty("Movies").GetInt32(), hers.GetProperty("Skipped").GetInt32(), hers.GetProperty("Excluded").GetInt32()));
    }

    private static List<string?> Keys(JsonElement page) => page.GetProperty("Items").EnumerateArray().Select(g => g.GetProperty("Key").GetString()).ToList();

    private static JsonElement Group(JsonElement page, string key) => page.GetProperty("Items").EnumerateArray().Single(g => g.GetProperty("Key").GetString() == key);

    private static List<Guid> Ids(JsonElement body, string property) => body.GetProperty(property).EnumerateArray().Select(Id).ToList();

    private static List<string?> Strings(JsonElement body, string property) => body.GetProperty(property).EnumerateArray().Select(i => i.GetString()).ToList();

    // Every number anywhere in a body.
    private static IEnumerable<decimal> Numbers(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Numbers(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Numbers),
        JsonValueKind.Number => [element.GetDecimal()],
        _ => []
    };

    // Jellyfin writes ids as 32 hex digits (its JsonGuidConverter), which JsonElement.GetGuid doesn't read.
    private static Guid Id(JsonElement element) => Guid.Parse(element.GetString()!);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();

    private static Task<WebHostHarness> StartAsync(DuplicatesLibrary library, User? restricted = null)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, host.Object);

        // Alice can open every library, Bob only Movies; an API key (no user) every one.
        var users = new Mock<IUserManager>();
        var alice = new User("alice", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Alice };
        var bob = new User("bob", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Bob };
        users.Setup(u => u.GetUserById(Alice)).Returns(alice);
        users.Setup(u => u.GetUserById(Bob)).Returns(bob);
        if (restricted is not null)
        {
            users.Setup(u => u.GetUserById(restricted.Id)).Returns(restricted);
        }

        // The items a user can't see are the fake's hidden ones (its GetItemList, GetItemById and each parent's check).
        var access = new LibraryAccess(library.Fake.Object, user => user is null || user.Id != Bob ? library.Libraries : [library.Libraries[0]], library.Fake.IsVisible);

        // The libraries themselves, so that an id asked for resolves (the kit's GetItemById), as on a server.
        foreach (var folder in library.Libraries)
        {
            library.Fake.Add(folder);
        }

        Type[] replaced = [typeof(IPluginConfigurationProvider), typeof(MovieIndexBuilder), typeof(LibraryAccess)];
        return new WebHostHarness()
            .WithServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(library.Fake.Object);
                services.AddSingleton(host.Object);
                services.AddSingleton(users.Object);
                foreach (var descriptor in registered.Where(d => !replaced.Contains(d.ServiceType)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(library.ConfigurationProvider);
                services.AddSingleton(library.Builder());
                services.AddSingleton(access);
                services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o =>
                {
                    var jellyfin = JsonDefaults.PascalCaseOptions;
                    o.JsonSerializerOptions.DefaultIgnoreCondition = jellyfin.DefaultIgnoreCondition;
                    o.JsonSerializerOptions.PropertyNamingPolicy = jellyfin.PropertyNamingPolicy;
                    o.JsonSerializerOptions.Converters.Clear();
                    foreach (var converter in jellyfin.Converters)
                    {
                        o.JsonSerializerOptions.Converters.Add(converter);
                    }
                });
            })
            .WithMvc(typeof(LibraryMaintenanceDuplicatesController), typeof(LibraryMaintenanceAdminController))
            .StartAsync();
    }
}
