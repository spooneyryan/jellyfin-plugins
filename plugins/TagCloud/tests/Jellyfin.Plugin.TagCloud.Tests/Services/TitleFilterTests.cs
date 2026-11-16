using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Index;
using Jellyfin.Plugin.TagCloud.Tests.TestSupport;
using MediaBrowser.Controller.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TagCloud.Tests.Services;

/// <summary>
/// A word's titles filtered and sorted on the server (<c>GET /TagCloud/Titles</c>'s sort, descending, q, minResolution and
/// unwatched), and the people clouds' photos: over the fixture's library plus four movies and The Wire carrying "pick".
/// </summary>
public sealed class TitleFilterTests
{
    private static readonly string[] AllPicks = ["Amélie", "Brazil", "Casablanca", "Downfall", "The Wire"];

    [Theory]
    [InlineData(TagCloudTitleSort.Name, false, new[] { "Amélie", "Brazil", "Casablanca", "Downfall", "The Wire" })]
    [InlineData(TagCloudTitleSort.Name, true, new[] { "The Wire", "Downfall", "Casablanca", "Brazil", "Amélie" })]
    // Casablanca has neither a premiere date nor a year: last both ways. Downfall has only its year (2004-01-01).
    [InlineData(TagCloudTitleSort.ReleaseDate, false, new[] { "Brazil", "Amélie", "The Wire", "Downfall", "Casablanca" })]
    [InlineData(TagCloudTitleSort.ReleaseDate, true, new[] { "Downfall", "The Wire", "Amélie", "Brazil", "Casablanca" })]
    [InlineData(TagCloudTitleSort.DateAdded, false, new[] { "Brazil", "Downfall", "Casablanca", "The Wire", "Amélie" })]
    [InlineData(TagCloudTitleSort.DateAdded, true, new[] { "Amélie", "The Wire", "Casablanca", "Downfall", "Brazil" })]
    // Casablanca has no rating: last both ways.
    [InlineData(TagCloudTitleSort.Rating, false, new[] { "Downfall", "Brazil", "Amélie", "The Wire", "Casablanca" })]
    [InlineData(TagCloudTitleSort.Rating, true, new[] { "The Wire", "Amélie", "Brazil", "Downfall", "Casablanca" })]
    public async Task Sort_EachWayRound_WithTitlesWithoutTheValueLast(TagCloudTitleSort sort, bool descending, string[] expected)
    {
        var fixture = await PicksAsync();

        var page = TitlesOf(fixture, new TagCloudTitleFilter { Sort = sort, Descending = descending });

        Assert.Equal(expected, page.Items.Select(t => t.Name));
        Assert.Equal(5, page.Total);
        Assert.Equal(5, page.Unfiltered);
    }

    [Theory]
    [InlineData("amelie", new[] { "Amélie" })]
    [InlineData("AMÉLIE", new[] { "Amélie" })]
    [InlineData("  Amélie ", new[] { "Amélie" })]
    [InlineData("fabuleux DESTIN", new[] { "Amélie" })]
    [InlineData("untergang", new[] { "Downfall" })]
    [InlineData("wire", new[] { "The Wire" })]
    [InlineData("nothing like it", new string[0])]
    [InlineData("", new[] { "Amélie", "Brazil", "Casablanca", "Downfall", "The Wire" })]
    public async Task NameFilter_MatchesTheNameOrTheOriginalTitle_IgnoringCaseAndAccents(string q, string[] expected)
    {
        var fixture = await PicksAsync();

        var page = TitlesOf(fixture, new TagCloudTitleFilter { Query = q });

        Assert.Equal(expected, page.Items.Select(t => t.Name));
        Assert.Equal(expected.Length, page.Total);
        Assert.Equal(5, page.Unfiltered);
    }

    [Theory]
    [InlineData(TagCloudMinResolution.Any, new[] { "Amélie", "Brazil", "Casablanca", "Downfall", "The Wire" })]
    // Downfall's size isn't known: left out by any minimum. The Wire is a series: never left out by it.
    [InlineData(TagCloudMinResolution.P480, new[] { "Amélie", "Brazil", "Casablanca", "The Wire" })]
    [InlineData(TagCloudMinResolution.P720, new[] { "Amélie", "Brazil", "The Wire" })]
    [InlineData(TagCloudMinResolution.P1080, new[] { "Amélie", "Brazil", "The Wire" })]
    [InlineData(TagCloudMinResolution.P4K, new[] { "Brazil", "The Wire" })]
    public async Task MinResolution_LeavesOutSofterMovies_AndNeverASeries(TagCloudMinResolution least, string[] expected)
    {
        var fixture = await PicksAsync();

        Assert.Equal(expected, TitlesOf(fixture, new TagCloudTitleFilter { MinResolution = least }).Items.Select(t => t.Name));
    }

    [Fact]
    public void ResolutionSteps_AreTheServersLabels()
    {
        Assert.Null(TitleText.ResolutionStep(0, 0));
        Assert.Equal(0, TitleText.ResolutionStep(480, 360));
        Assert.Equal(1, TitleText.ResolutionStep(640, 480));
        Assert.Equal(2, TitleText.ResolutionStep(1280, 720));
        Assert.Equal(3, TitleText.ResolutionStep(1920, 800));
        Assert.Equal(3, TitleText.ResolutionStep(1920, 1080));
        Assert.Equal(4, TitleText.ResolutionStep(3840, 1600));
    }

    [Fact]
    public async Task Unwatched_IsPerUser_AndASeriesIsWatchedOnlyWhenFullyPlayed()
    {
        var fixture = await PicksAsync();
        var brazil = Find(fixture, "Brazil");
        var amelie = Find(fixture, "Amélie");
        fixture.Played.Add((fixture.Admin.Id, brazil.Id));
        fixture.Played.Add((fixture.Admin.Id, fixture.Wire.Id));
        fixture.Played.Add((fixture.Kid.Id, amelie.Id));

        var admin = TitlesOf(fixture, new TagCloudTitleFilter { Unwatched = true });
        var kid = TitlesOf(fixture, new TagCloudTitleFilter { Unwatched = true }, fixture.Kid);

        Assert.Equal(["Amélie", "Casablanca", "Downfall"], admin.Items.Select(t => t.Name));
        Assert.Equal(5, admin.Unfiltered);
        // The kid sees Movies only: The Wire is neither listed nor asked about.
        Assert.Equal(["Brazil", "Casablanca", "Downfall"], kid.Items.Select(t => t.Name));
        Assert.Equal(4, kid.Unfiltered);
        fixture.UserData.Verify(u => u.GetUserDataDto(fixture.Wire, It.IsAny<MediaBrowser.Model.Dto.BaseItemDto?>(), fixture.Kid, It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>()), Times.Never);
        // A series is asked about as core's DTOs say it (fully played), never by its own row's played flag.
        fixture.UserData.Verify(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), fixture.Wire), Times.Never);

        // Played state is read at the request: watching Casablanca takes it out at once, with the same index.
        var generation = fixture.Index.Current!.Generation;
        fixture.Played.Add((fixture.Admin.Id, Find(fixture, "Casablanca").Id));
        Assert.Equal(["Amélie", "Downfall"], TitlesOf(fixture, new TagCloudTitleFilter { Unwatched = true }).Items.Select(t => t.Name));
        Assert.Equal(generation, fixture.Index.Current!.Generation);
    }

    [Fact]
    public async Task Unwatched_IsOnlyAskedFor_WhenWanted()
    {
        var fixture = await PicksAsync();

        TitlesOf(fixture, new TagCloudTitleFilter { Sort = TagCloudTitleSort.Rating, Query = "a" });

        fixture.UserData.Verify(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()), Times.Never);
    }

    [Fact]
    public async Task Paging_RunsOverTheFilteredList()
    {
        var fixture = await PicksAsync();
        var filter = new TagCloudTitleFilter { MinResolution = TagCloudMinResolution.P480, Sort = TagCloudTitleSort.Name };

        var first = fixture.Query.GetTitles(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, "pick", TagCloudTitleOrder.Name, 0, 0, 2, filter);
        var second = fixture.Query.GetTitles(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, "pick", TagCloudTitleOrder.Name, 0, 2, 2, filter);
        var past = fixture.Query.GetTitles(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, "pick", TagCloudTitleOrder.Name, 0, 10, 2, filter);

        Assert.Equal(["Amélie", "Brazil"], first.Items.Select(t => t.Name));
        Assert.Equal(["Casablanca", "The Wire"], second.Items.Select(t => t.Name));
        Assert.Empty(past.Items);
        Assert.All(new[] { first, second, past }, page =>
        {
            Assert.Equal(4, page.Total);
            Assert.Equal(5, page.Unfiltered);
        });
    }

    [Fact]
    public async Task Filters_NeverReachATitleTheCallerMayNotSee()
    {
        var fixture = await PicksAsync();

        // Scarface carries heist and is hidden from the kid: no filter finds it, and the counts don't hold it.
        var kid = fixture.Query.GetTitles(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Both, null, null, "heist", TagCloudTitleOrder.Name, 0, 0, 50, new TagCloudTitleFilter { Query = "scar" });
        Assert.Empty(kid.Items);
        Assert.Equal(0, kid.Total);
        Assert.Equal(1, kid.Unfiltered);

        var admin = fixture.Query.GetTitles(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, "heist", TagCloudTitleOrder.Name, 0, 0, 50, new TagCloudTitleFilter { Query = "scar" });
        Assert.Equal(["Scarface"], admin.Items.Select(t => t.Name));

        // A word only hidden titles carry stays an empty page, filters or not.
        Assert.Equal(0, fixture.Query.GetTitles(fixture.Kid, TagCloudSource.Tags, TagCloudScope.Both, null, null, "gangster", TagCloudTitleOrder.Name, 0, 0, 50, new TagCloudTitleFilter { Unwatched = true }).Unfiltered);
    }

    [Fact]
    public async Task InvalidValues_AreRefused()
    {
        var fixture = await PicksAsync();

        void Refused(TagCloudTitleOrder order, int startIndex, int limit, TagCloudTitleFilter filter)
        {
            var error = Assert.Throws<PluginException>(() => fixture.Query.GetTitles(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, "pick", order, 0, startIndex, limit, filter));
            Assert.Equal(PluginErrorKind.Invalid, error.Kind);
        }

        Refused(TagCloudTitleOrder.Name, -1, 50, new TagCloudTitleFilter());
        Refused(TagCloudTitleOrder.Name, 0, 0, new TagCloudTitleFilter());
        Refused(TagCloudTitleOrder.Name, 0, 201, new TagCloudTitleFilter());
        Refused(TagCloudTitleOrder.Random, 0, 50, new TagCloudTitleFilter { Sort = TagCloudTitleSort.Rating });
        Refused(TagCloudTitleOrder.Random, 0, 50, new TagCloudTitleFilter { Descending = true });
        Refused(TagCloudTitleOrder.Name, 0, 50, new TagCloudTitleFilter { Query = new string('a', TagCloudTitleFilter.MaxQueryLength + 1) });
        Refused(TagCloudTitleOrder.Name, 0, 50, new TagCloudTitleFilter { MinResolution = (TagCloudMinResolution)99 });
        Refused(TagCloudTitleOrder.Name, 0, 50, new TagCloudTitleFilter { Sort = (TagCloudTitleSort)99 });

        // The longest filter and the page's bounds are fine, and a shuffle may be filtered.
        Assert.Empty(fixture.Query.GetTitles(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, "pick", TagCloudTitleOrder.Name, 0, 0, 200, new TagCloudTitleFilter { Query = new string('a', TagCloudTitleFilter.MaxQueryLength) }).Items);
        Assert.Equal(["Amélie"], fixture.Query.GetTitles(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, "pick", TagCloudTitleOrder.Random, 3, 0, 1, new TagCloudTitleFilter { Query = "amel" }).Items.Select(t => t.Name));
    }

    [Fact]
    public async Task PeopleWords_CarryThePersonAndTheirPhoto_AndOtherWordsNone()
    {
        var fixture = await PicksAsync();

        var actors = fixture.Query.GetWords(fixture.Admin, TagCloudSource.Actors, TagCloudScope.Both, null, null, null).Words.ToDictionary(w => w.Text);
        Assert.Equal(fixture.Persons["Sigourney Weaver"].Id, actors["Sigourney Weaver"].PersonId);
        Assert.Equal("tag-Sigourney Weaver-Primary", actors["Sigourney Weaver"].ImageTag);
        Assert.Equal(fixture.Persons["Al Pacino"].Id, actors["Al Pacino"].PersonId);
        Assert.Null(actors["Al Pacino"].ImageTag);
        Assert.Null(actors["Michael Biehn"].PersonId);
        Assert.Null(actors["Michael Biehn"].ImageTag);

        // A tag never names a person, even one spelt like one.
        fixture.Alien.Tags = [.. fixture.Alien.Tags, "Al Pacino"];
        await fixture.Index.BuildAsync(CancellationToken.None);
        Assert.All(fixture.Query.GetWords(fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, null).Words, w =>
        {
            Assert.Null(w.PersonId);
            Assert.Null(w.ImageTag);
        });
    }

    [Fact]
    public async Task PeoplePhotos_AreLookedUpOncePerIndexGeneration()
    {
        var fixture = await PicksAsync();

        fixture.Query.GetWords(fixture.Admin, TagCloudSource.Actors, TagCloudScope.Both, null, null, null);
        fixture.Query.GetWords(fixture.Kid, TagCloudSource.Actors, TagCloudScope.Both, null, null, null);
        fixture.Library.Mock.Verify(l => l.GetPerson("Sigourney Weaver"), Times.Once);
        fixture.Library.Mock.Verify(l => l.GetPerson("Michael Biehn"), Times.Once);

        // A new photo comes with the next generation.
        await fixture.Index.BuildAsync(CancellationToken.None);
        fixture.Query.GetWords(fixture.Admin, TagCloudSource.Actors, TagCloudScope.Both, null, null, null);
        fixture.Library.Mock.Verify(l => l.GetPerson("Sigourney Weaver"), Times.Exactly(2));
    }

    [Fact]
    public async Task APersonLookupThatFails_LeavesTheWordWithoutAPhoto()
    {
        var fixture = await PicksAsync();
        fixture.Library.Mock.Setup(l => l.GetPerson(It.IsAny<string>())).Throws(new InvalidOperationException("the database is locked"));

        var words = fixture.Query.GetWords(fixture.Admin, TagCloudSource.Directors, TagCloudScope.Both, null, null, null).Words;

        Assert.NotEmpty(words);
        Assert.All(words, w => Assert.Null(w.PersonId));
    }

    [Fact]
    public async Task ARecordReadFromAnItem_CarriesWhatTheFiltersUse()
    {
        var fixture = await PicksAsync();

        var amelie = fixture.Index.Current!.Titles.OfType<TitleRecord>().Single(t => t.Name == "Amélie");
        var wire = fixture.Index.Current!.Titles.OfType<TitleRecord>().Single(t => t.Name == "The Wire");

        Assert.Equal("Le Fabuleux Destin d'Amélie Poulain", amelie.OriginalTitle);
        Assert.Equal("amelie\nle fabuleux destin d'amelie poulain", amelie.SearchText);
        Assert.Equal((1920, 1040), (amelie.Width, amelie.Height));
        Assert.Equal(8.3f, amelie.CommunityRating);
        Assert.Equal(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), amelie.DateCreated);
        // A series' own row has no picture size worth keeping.
        Assert.Equal((0, 0), (wire.Width, wire.Height));
    }

    private static TagCloudTitlesDto TitlesOf(TagCloudFixture fixture, TagCloudTitleFilter filter, Jellyfin.Database.Implementations.Entities.User? user = null)
        => fixture.Query.GetTitles(user ?? fixture.Admin, TagCloudSource.Tags, TagCloudScope.Both, null, null, "pick", TagCloudTitleOrder.Name, 0, 0, 50, filter);

    private static BaseItem Find(TagCloudFixture fixture, string name) => fixture.Library.Items.Single(i => i.Name == name);

    private static async Task<TagCloudFixture> PicksAsync()
    {
        var fixture = new TagCloudFixture();
        Pick(fixture, "Amélie", 2001, new DateTime(2001, 4, 25), 5, 8.3f, 1920, 1040, "Le Fabuleux Destin d'Amélie Poulain");
        Pick(fixture, "Brazil", 1985, new DateTime(1985, 2, 20), 1, 7.9f, 3840, 2076, null);
        Pick(fixture, "Casablanca", null, null, 3, null, 640, 480, null);
        Pick(fixture, "Downfall", 2004, null, 2, 7.5f, 0, 0, "Der Untergang");
        fixture.Wire.Tags = [.. fixture.Wire.Tags, "pick"];
        fixture.Wire.PremiereDate = new DateTime(2002, 6, 2, 0, 0, 0, DateTimeKind.Utc);
        fixture.Wire.DateCreated = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc);
        fixture.Wire.CommunityRating = 9.3f;
        fixture.Wire.Width = 1920;
        fixture.Wire.Height = 1080;
        await fixture.Index.BuildAsync(CancellationToken.None);
        Assert.Equal(AllPicks, TitlesOf(fixture, new TagCloudTitleFilter()).Items.Select(t => t.Name));
        return fixture;
    }

    private static void Pick(TagCloudFixture fixture, string name, int? year, DateTime? premiere, int addedDay, float? rating, int width, int height, string? original)
    {
        var movie = fixture.Movie(name, year ?? 0, ["pick"], [], []);
        movie.ProductionYear = year;
        movie.PremiereDate = premiere is { } p ? DateTime.SpecifyKind(p, DateTimeKind.Utc) : null;
        movie.DateCreated = new DateTime(2026, 1, addedDay, 0, 0, 0, DateTimeKind.Utc);
        movie.CommunityRating = rating;
        movie.Width = width;
        movie.Height = height;
        movie.OriginalTitle = original;
    }
}
