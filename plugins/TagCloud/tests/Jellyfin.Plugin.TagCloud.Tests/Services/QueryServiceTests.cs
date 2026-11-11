using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Services;
using Jellyfin.Plugin.TagCloud.Tests.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.TagCloud.Tests.Services;

/// <summary>
/// The counts as one user sees them: only their titles, their libraries, narrowing, the titles of a word.
/// </summary>
public sealed class QueryServiceTests
{
    [Fact]
    public async Task Counts_CoverOnlyTheUsersTitles()
    {
        var fixture = await BuiltAsync();

        var admin = fixture.Counts(fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, null));
        var kid = fixture.Query.GetWords(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Both, null, null, null);

        Assert.Equal(2, admin["heist"]);
        Assert.Equal(1, admin["police"]);
        Assert.Equal(1, fixture.Counts(kid)["heist"]);
        // Scarface's only tag and The Wire's (a library the kid can't open) never show for the kid, not even with 0.
        Assert.DoesNotContain(kid.Words, w => w.Key == "gangster" || w.Key == "police");
        Assert.Equal(3, kid.TitleCount);
        Assert.Equal(5, fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, null).TitleCount);
    }

    [Fact]
    public async Task Words_AreHeaviestFirst_TiesByText_CutToTheLimit()
    {
        var fixture = await BuiltAsync();
        for (var i = 0; i < 30; i++)
        {
            fixture.Movie("Filler " + i, 2000 + i, ["filler " + (i % 27)], [], []);
        }

        await fixture.Index.BuildAsync(CancellationToken.None);
        var words = fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Movies, null, null, 25);

        Assert.Equal(25, words.Words.Count);
        Assert.Equal(34, words.WordCount);
        Assert.Equal(["creature", "filler 0", "filler 1", "filler 2", "heist", "space"], words.Words.Take(6).Select(w => w.Key).Order(StringComparer.Ordinal));
        Assert.True(words.Words.Zip(words.Words.Skip(1)).All(p => p.First.Count >= p.Second.Count));
    }

    [Fact]
    public async Task Scope_AndLibraries_PickTheTitles_AndALibraryTheUserCantOpenIsIgnored()
    {
        var fixture = await BuiltAsync();

        Assert.Equal(["Baltimore", "police"], fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Shows, null, null, null).Words.Select(w => w.Text).Order(StringComparer.Ordinal));
        Assert.Equal(0, fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, [fixture.Shows.Id], null, null).Counts("heist"));
        Assert.Equal(1, fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, [fixture.Shows.Id], null, null).TitleCount);

        // The kid asking for Shows gets nothing of it: no library left, no titles.
        var kid = fixture.Query.GetWords(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Both, [fixture.Shows.Id], null, null);
        Assert.Empty(kid.Words);
        Assert.Equal(0, kid.TitleCount);
    }

    [Fact]
    public async Task Narrowing_KeepsTheWordsSharingTitlesWithEveryKey_AndDropsKeysTheUsersTitlesDontCarry()
    {
        var fixture = await BuiltAsync();

        var narrowed = fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Movies, null, ["Space", "creature"], null);
        Assert.Equal(["space", "creature"], narrowed.Narrow);
        Assert.Equal(2, narrowed.TitleCount);
        Assert.Equal(["creature", "horror", "sequel", "space"], narrowed.Words.Select(w => w.Key).Order(StringComparer.Ordinal));

        // gangster is on Scarface, hidden from the kid: as a narrowing key it is ignored, not an empty cloud.
        var kid = fixture.Query.GetWords(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Movies, null, ["gangster", "nonsense"], null);
        Assert.Empty(kid.Narrow);
        Assert.Equal(3, kid.TitleCount);
    }

    [Fact]
    public async Task Titles_ListTheUsersTitlesOfAWord_ByName_OrShuffledTheSameEachTime()
    {
        var fixture = await BuiltAsync();

        var heist = fixture.Query.GetTitles(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, "Heist", TagCloudTitleOrder.Name, 0, 0, 50);
        Assert.Equal(2, heist.Total);
        Assert.Equal(["Heat", "Scarface"], heist.Items.Select(t => t.Name));
        Assert.Equal("Movie", heist.Items[0].Kind);

        Assert.Equal(["Heat"], fixture.Query.GetTitles(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Both, null, null, "heist", TagCloudTitleOrder.Name, 0, 0, 50).Items.Select(t => t.Name));
        Assert.Empty(fixture.Query.GetTitles(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Both, null, null, "gangster", TagCloudTitleOrder.Name, 0, 0, 50).Items);

        var once = fixture.Query.GetTitles(fixture.Admin, TagCloudSource.Actors, TagCloudScope.Both, null, null, "sigourney weaver", TagCloudTitleOrder.Random, 7, 0, 50).Items.Select(t => t.Id).ToList();
        var twice = fixture.Query.GetTitles(fixture.Admin, TagCloudSource.Actors, TagCloudScope.Both, null, null, "sigourney weaver", TagCloudTitleOrder.Random, 7, 0, 50).Items.Select(t => t.Id).ToList();
        Assert.Equal(once, twice);
        Assert.Single(fixture.Query.GetTitles(fixture.Admin, TagCloudSource.Actors, TagCloudScope.Both, null, null, "sigourney weaver", TagCloudTitleOrder.Name, 0, 1, 1).Items);
    }

    [Fact]
    public async Task Libraries_AreTheUsersWithTitlesInTheScope_WithTheirCounts()
    {
        var fixture = await BuiltAsync();

        Assert.Equal([("Movies", 4), ("Shows", 1)], fixture.Query.GetLibraries(fixture.Admin, TagCloudScope.Both).Select(l => (l.Name, l.TitleCount)));
        Assert.Equal([("Movies", 3)], fixture.Query.GetLibraries(fixture.Kid, TagCloudScope.Both).Select(l => (l.Name, l.TitleCount)));
        Assert.Equal([("Shows", 1)], fixture.Query.GetLibraries(fixture.Admin, TagCloudScope.Shows).Select(l => (l.Name, l.TitleCount)));
    }

    [Fact]
    public async Task BeforeTheFirstBuild_ItIsUnavailable_AndASourceTurnedOffIsFeatureDisabled()
    {
        var fixture = new TagCloudFixture();
        var unavailable = Assert.Throws<PluginException>(() => fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Movies, null, null, null));
        Assert.Equal(PluginErrorKind.Unavailable, unavailable.Kind);

        await fixture.Index.BuildAsync(CancellationToken.None);
        fixture.ConfigurationProvider.Configuration.EnableStudios = false;
        var off = Assert.Throws<PluginException>(() => fixture.Query.GetWords(fixture.Admin, TagCloudSource.Studios, TagCloudScope.Movies, null, null, null));
        Assert.Equal(PluginErrorKind.FeatureDisabled, off.Kind);
        Assert.DoesNotContain(TagCloudSource.Studios, fixture.Query.GetCapabilities(isAdmin: false).Sources);
    }

    [Fact]
    public async Task Capabilities_HoldTheWordsLimitToItsRange_AndItsSteps()
    {
        var fixture = await BuiltAsync();
        fixture.ConfigurationProvider.Configuration.MaxWordsLimit = 1010;
        fixture.ConfigurationProvider.Configuration.TvMaxWordsDefault = 5000;

        var capabilities = fixture.Query.GetCapabilities(isAdmin: true);

        Assert.Equal(1000, capabilities.MaxWordsLimit);
        Assert.Equal(1000, capabilities.TvMaxWordsDefault);
        Assert.Equal(25, capabilities.WordStep);
        Assert.True(capabilities.IsAdmin);
    }

    [Fact]
    public async Task APolicyChange_AppliesAtTheNextRequest_WithTheSameIndexGeneration()
    {
        // Jellyfin 10.11.5 raises no event when an admin changes a user's policy, so the cached titles of the user must
        // not outlive it: the kid's blocked tags change (and the server hides Heat from the kid accordingly), the index
        // stays as it was, and the next count no longer has Heat.
        var fixture = await BuiltAsync();
        var generation = fixture.Index.Current!.Generation;
        var before = fixture.Query.GetWords(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Movies, null, null, null);
        Assert.Equal(1, before.Counts("heist"));
        Assert.Equal(3, before.TitleCount);

        fixture.Kid.SetPreference(PreferenceKind.BlockedTags, ["heist"]);
        fixture.Library.Hide(fixture.Kid, fixture.Heat);

        var after = fixture.Query.GetWords(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Movies, null, null, null);
        Assert.Equal(generation, fixture.Index.Current!.Generation);
        Assert.Equal(0, after.Counts("heist"));
        Assert.Equal(2, after.TitleCount);
        Assert.Equal(0, fixture.Query.GetTitles(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Movies, null, null, "heist", TagCloudTitleOrder.Name, 0, 0, 50).Total);
    }

    [Fact]
    public async Task ALibrarySwitchedOff_AppliesAtTheNextRequest_WithTheSameIndexGeneration()
    {
        // The masks are keyed by the libraries as they resolve for the user at the request, not by the ids asked for: a
        // library switched off server-wide is gone from the next count, whether every library or that one was asked for.
        var fixture = await BuiltAsync();
        var generation = fixture.Index.Current!.Generation;
        Assert.Equal(5, fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, null).TitleCount);
        Assert.Equal(1, fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, [fixture.Shows.Id], null, null).TitleCount);

        fixture.SwitchedOff.Add(fixture.Shows);

        Assert.Equal(4, fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, null).TitleCount);
        Assert.Equal(0, fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, [fixture.Shows.Id], null, null).TitleCount);
        Assert.Equal(generation, fixture.Index.Current!.Generation);

        fixture.SwitchedOff.Clear();
        Assert.Equal(5, fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, null).TitleCount);
    }

    [Fact]
    public async Task WithThePageOff_LibrariesWordsAndTitlesAreFeatureDisabled_AndTheCapabilitiesStay()
    {
        var fixture = await BuiltAsync();
        fixture.ConfigurationProvider.Configuration.EnableTagCloudPage = false;

        Assert.Equal(PluginErrorKind.FeatureDisabled, Assert.Throws<PluginException>(() => fixture.Query.GetLibraries(fixture.Kid, TagCloudScope.Both)).Kind);
        Assert.Equal(PluginErrorKind.FeatureDisabled, Assert.Throws<PluginException>(() => fixture.Query.GetWords(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Both, null, null, null)).Kind);
        Assert.Equal(PluginErrorKind.FeatureDisabled, Assert.Throws<PluginException>(() => fixture.Query.GetTitles(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Both, null, null, "heist", TagCloudTitleOrder.Name, 0, 0, 50)).Kind);
        Assert.NotEmpty(fixture.Query.GetCapabilities(isAdmin: false).Sources);
    }

    [Fact]
    public void AccessKey_ChangesWithWhatDecidesVisibility_AndNotWithTheOrderOfTags()
    {
        var user = TagCloudFixture.NewUser("u");
        var start = TagCloudQueryService.AccessKey(user);
        user.SetPreference(PreferenceKind.BlockedTags, ["b", "a"]);
        var blocked = TagCloudQueryService.AccessKey(user);
        Assert.NotEqual(start, blocked);
        user.SetPreference(PreferenceKind.BlockedTags, ["a", "b"]);
        Assert.Equal(blocked, TagCloudQueryService.AccessKey(user));
        user.MaxParentalRatingScore = 13;
        var rated = TagCloudQueryService.AccessKey(user);
        Assert.NotEqual(blocked, rated);
        user.SetPermission(PermissionKind.EnableAllFolders, !user.HasPermission(PermissionKind.EnableAllFolders));
        var folders = TagCloudQueryService.AccessKey(user);
        Assert.NotEqual(rated, folders);

        // Tags as stored, the way core compares them: a padded or a blank one is a different policy.
        user.SetPreference(PreferenceKind.BlockedTags, ["a ", "b"]);
        var padded = TagCloudQueryService.AccessKey(user);
        Assert.NotEqual(folders, padded);
        user.SetPreference(PreferenceKind.BlockedTags, ["a", "b", " "]);
        Assert.NotEqual(folders, TagCloudQueryService.AccessKey(user));
        Assert.NotEqual(padded, TagCloudQueryService.AccessKey(user));
    }

    private static async Task<TagCloudFixture> BuiltAsync()
    {
        var fixture = new TagCloudFixture();
        await fixture.Index.BuildAsync(CancellationToken.None);
        return fixture;
    }
}

internal static class WordsExtensions
{
    public static int Counts(this TagCloudWordsDto words, string key) => words.Words.FirstOrDefault(w => w.Key == key)?.Count ?? 0;
}
