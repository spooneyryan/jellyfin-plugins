using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Services;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Controller.Entities.Movies;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Api;

/// <summary>
/// The Settings tab on the wire: <c>GET</c> and <c>POST
/// /UserRatings/Admin/Settings</c> behind the admin controller. Every setting round-trips with its default; the server
/// checks every value (a 400 whose <c>code</c> and words name the setting), refuses a key that names no setting, and a save
/// over settings saved since its version (409 <c>SettingsChanged</c>, naming them); a dry run saves nothing and says what a
/// change means; a change takes effect at the next request, without a restart, except the three that need one.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class UserRatingsSettingsTests : IAsyncLifetime
{
    private const string Path = "/UserRatings/Admin/Settings";

    private readonly ApiFixture _fx = new();
    private readonly Movie _matrix;

    public UserRatingsSettingsTests()
    {
        _matrix = S.AddMovie(S.Movies, "The Matrix", "603");
    }

    private ServicesFixture S => _fx.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => S.Store.Clock.GetUtcNow().UtcDateTime;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _fx.DisposeAsync();

    [Fact]
    public void EveryPublicSetting_IsAFieldOfTheTab_AndOfItsDto_WithARangeOrNamesWhereItNeedsOne()
    {
        var settings = typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).Select(p => p.Name).Order(StringComparer.Ordinal);
        var dto = typeof(UserRatingsSettingsDto).GetProperties().Select(p => p.Name).Where(n => n != nameof(UserRatingsSettingsDto.UnknownKeys)).Order(StringComparer.Ordinal);

        Assert.Equal(settings, RatingsSettingsFields.Fields.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.Equal(settings, dto);
        Assert.All(RatingsSettingsFields.Fields, f =>
        {
            var type = RatingsSettingsFields.TypeOf(f.Name);
            Assert.True(type != typeof(int) || f.Max > f.Min, f.Name + " has a range");
            Assert.True(type != typeof(string) || f.Names is { Count: > 1 }, f.Name + " has names");
            Assert.True(type != typeof(string) || f.Names!.Contains((string)RatingsSettingsFields.Get(new PluginConfiguration(), f.Name)!), f.Name + "'s default is one of its names");
        });
        Assert.Equal(
            ["EnableTopRatedPage", "PageHostMode", "RegisterPluginPages"],
            RatingsSettingsFields.Fields.Where(f => f.RestartNeeded).Select(f => f.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheVersion_ChangesWithAnySetting_ButNotWithAListsOrder()
    {
        var saved = new PluginConfiguration();
        var version = RatingsSettingsFields.Version(saved);
        Assert.Matches("^[0-9a-f]{16}$", version);

        foreach (var field in RatingsSettingsFields.Fields)
        {
            var changed = RatingsSettingsFields.Copy(saved);
            RatingsSettingsFields.Set(changed, field.Name, RatingsSettingsFields.Get(saved, field.Name) switch
            {
                bool on => !on,
                int number => number + 1,
                string name => name + "x",
                Guid[] => new[] { Guid.NewGuid() },
                string[] => new[] { "Movie" },
                _ => throw new InvalidOperationException(field.Name)
            });
            Assert.NotEqual(version, RatingsSettingsFields.Version(changed));
        }

        var reordered = RatingsSettingsFields.Copy(saved);
        reordered.RateableItemTypes = [.. saved.RateableItemTypes.Reverse()];
        Assert.Equal(version, RatingsSettingsFields.Version(reordered));
    }

    [Fact]
    public async Task TheSettingsRoutes_AreForAnAdminOrAnApiKeyOnly_AndNoStore()
    {
        await _fx.StartAsync();
        var version = (await SettingsAsync()).GetProperty("Version").GetString();
        foreach (var (method, path, json) in new[] { (HttpMethod.Get, Path, (string?)null), (HttpMethod.Post, $"{Path}?dryRun=false&version={version}", """{"ReviewsPageSize":30}""") })
        {
            Assert.Equal(401, await StatusAsync(null, method, path, json));
            Assert.Equal(403, await StatusAsync(S.Viewer, method, path, json));
            Assert.Equal(403, await StatusAsync(new ApiFixture.AsCaller("user", S.Admin.Id), method, path, json));
        }

        Assert.Equal(20, S.Configuration.ReviewsPageSize);
        foreach (var caller in new object[] { S.Admin, ApiFixture.ApiKey })
        {
            using var get = await _fx.SendAsync(caller, HttpMethod.Get, Path);
            using var dry = await _fx.SendAsync(caller, HttpMethod.Post, Path, """{"ReviewsPageSize":30}""");
            Assert.Equal((200, 200), ((int)get.StatusCode, (int)dry.StatusCode));
            Assert.True(get.Headers.CacheControl?.NoStore);
            Assert.True(dry.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task Get_GivesEverySetting_ItsDefault_TheVersion_TheUsersByName_AndTheKinds()
    {
        await _fx.StartAsync();

        var view = await SettingsAsync();

        var defaults = new PluginConfiguration();
        foreach (var field in RatingsSettingsFields.Fields)
        {
            Assert.Equal(Json(RatingsSettingsFields.Get(defaults, field.Name)), view.GetProperty("Settings").GetProperty(field.Name).GetRawText());
            Assert.Equal(Json(RatingsSettingsFields.Get(defaults, field.Name)), view.GetProperty("Defaults").GetProperty(field.Name).GetRawText());
        }

        Assert.Equal(RatingsSettingsFields.Version(S.Configuration), view.GetProperty("Version").GetString());
        Assert.Equal(["admin", "viewer", "viewer2"], view.GetProperty("Users").EnumerateArray().Select(u => u.GetProperty("Name").GetString()));
        Assert.All(view.GetProperty("Users").EnumerateArray(), u => Assert.False(u.GetProperty("Missing").GetBoolean()));
        Assert.Equal(RatingsSettingsFields.OfferedItemTypes, view.GetProperty("ItemKinds").EnumerateArray().Select(k => k.GetString()));
        Assert.Contains("Audio", RatingsSettingsFields.OfferedItemTypes);
        Assert.Contains("BoxSet", RatingsSettingsFields.OfferedItemTypes);
        Assert.Equal(["EnableTopRatedPage", "RegisterPluginPages", "PageHostMode"], view.GetProperty("RestartFields").EnumerateArray().Select(k => k.GetString()));
        Assert.Empty(view.GetProperty("Problems").EnumerateArray());
        Assert.False(view.GetProperty("Settings").TryGetProperty("UnknownKeys", out _));
    }

    // Every field round-trips (save, read again, the value kept), through Jellyfin's XML too.
    [Fact]
    public async Task EverySetting_RoundTrips_ADryRunFirstSavingNothing()
    {
        await _fx.StartAsync();
        var before = RatingsSettingsFields.Version(S.Configuration);
        var values = NonDefaults();
        var body = JsonSerializer.Serialize(values, ApiFixture.Json);

        var dry = await _fx.JsonAsync(S.Admin, HttpMethod.Post, Path, body);

        Assert.True(dry.GetProperty("DryRun").GetBoolean());
        Assert.False(dry.GetProperty("Applied").GetBoolean());
        Assert.Equal(before, dry.GetProperty("Version").GetString());
        Assert.Equal(values.Keys.Where(k => k != nameof(PluginConfiguration.MirrorToUserData)).Order(StringComparer.Ordinal), dry.GetProperty("Changes").EnumerateArray().Select(c => c.GetProperty("Field").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(before, RatingsSettingsFields.Version(S.Configuration));
        Assert.True(dry.GetProperty("RestartNeeded").GetBoolean());

        var saved = await _fx.JsonAsync(S.Admin, HttpMethod.Post, $"{Path}?dryRun=false&version={before}", body);

        Assert.True(saved.GetProperty("Applied").GetBoolean());
        Assert.False(saved.GetProperty("DryRun").GetBoolean());
        var after = RatingsSettingsFields.Version(S.Configuration);
        Assert.NotEqual(before, after);
        Assert.Equal(after, saved.GetProperty("Version").GetString());

        var read = await SettingsAsync();
        Assert.Equal(after, read.GetProperty("Version").GetString());
        foreach (var (field, value) in values)
        {
            var got = read.GetProperty("Settings").GetProperty(field);
            if (field == nameof(PluginConfiguration.RaterUserIds))
            {
                Assert.Equal([S.Viewer.Id], got.EnumerateArray().Select(e => Guid.Parse(e.GetString()!)));
            }
            else
            {
                Assert.Equal(Json(value), got.GetRawText());
            }
        }

        // Jellyfin keeps the settings in XML (XmlSerializer): the file reads back as the same settings.
        var xml = new XmlSerializer(typeof(PluginConfiguration));
        using var file = new MemoryStream();
        xml.Serialize(file, S.Configuration);
        file.Position = 0;
        Assert.Equal(after, RatingsSettingsFields.Version((PluginConfiguration)xml.Deserialize(file)!));
        Assert.Contains(S.Store.Logger.Entries, e => e.Message.Contains("User Ratings' settings were saved: EnableRatings, EnableReviews, WhoCanRate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASave_NeedsTheVersion_AndAChangeThatChangesNothing_SavesNothing()
    {
        await _fx.StartAsync();
        var before = RatingsSettingsFields.Version(S.Configuration);

        var noVersion = await ProblemAsync(S.Admin, $"{Path}?dryRun=false", """{"ReviewsPageSize":30}""", 400);
        var badVersion = await ProblemAsync(S.Admin, $"{Path}?dryRun=false&version=not-a-version", """{"ReviewsPageSize":30}""", 400);
        var same = await _fx.JsonAsync(S.Admin, HttpMethod.Post, $"{Path}?dryRun=false&version={before}", """{"ReviewsPageSize":20}""");

        Assert.Equal((RatingsSettingsService.VersionNeededCode, RatingsSettingsService.VersionNeededCode), (noVersion.Code, badVersion.Code));
        Assert.Empty(same.GetProperty("Changes").EnumerateArray());
        Assert.False(same.GetProperty("Applied").GetBoolean());
        Assert.Equal(before, RatingsSettingsFields.Version(S.Configuration));
        S.ConfigurationProvider.Verify(c => c.Save(It.IsAny<PluginConfiguration>()), Times.Never);
    }

    [Fact]
    public async Task ASaveOverSettingsSavedSince_Is409SettingsChanged_NamingThem_AndSavesNothing()
    {
        await _fx.StartAsync();
        var opened = (await SettingsAsync()).GetProperty("Version").GetString();
        await _fx.JsonAsync(S.Admin, HttpMethod.Post, $"{Path}?dryRun=false&version={opened}", """{"ReviewsPageSize":30,"ShowHistogram":false}""");
        var now = RatingsSettingsFields.Version(S.Configuration);

        // Another admin's window, opened before that save: its save (and its dry run with the version) is refused.
        var stale = await ProblemAsync(S.Admin, $"{Path}?dryRun=false&version={opened}", """{"MaxReviewLength":3000}""", 409);
        var staleDry = await ProblemAsync(S.Admin, $"{Path}?version={opened}", """{"MaxReviewLength":3000}""", 409);

        Assert.Equal(RatingsSettingsService.SettingsChangedCode, stale.Code);
        Assert.Equal(RatingsSettingsService.SettingsChangedCode, staleDry.Code);
        Assert.Contains("Ratings histogram, Reviews per page", stale.Detail, StringComparison.Ordinal);
        Assert.Equal(5000, S.Configuration.MaxReviewLength);
        Assert.Equal(now, RatingsSettingsFields.Version(S.Configuration));

        // A version this server never answered (another server's, say) is refused too, without names; the answer's own
        // version saves.
        var unknown = await ProblemAsync(S.Admin, $"{Path}?dryRun=false&version=0123456789abcdef", """{"MaxReviewLength":3000}""", 409);
        Assert.Equal(RatingsSettingsService.SettingsChangedCode, unknown.Code);
        Assert.DoesNotContain(":", unknown.Detail.Split(')')[1], StringComparison.Ordinal);
        await _fx.JsonAsync(S.Admin, HttpMethod.Post, $"{Path}?dryRun=false&version={now}", """{"MaxReviewLength":3000}""");
        Assert.Equal(3000, S.Configuration.MaxReviewLength);
    }

    [Fact]
    public async Task TwoSavesFromTheSameVersion_OneIsSaved_TheOtherIs409()
    {
        await _fx.StartAsync();
        var opened = (await SettingsAsync()).GetProperty("Version").GetString();

        var saves = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            using var response = await _fx.SendAsync(S.Admin, HttpMethod.Post, $"{Path}?dryRun=false&version={opened}", "{\"ReviewsPageSize\":" + (30 + i) + "}");
            return (int)response.StatusCode;
        })));

        Assert.Equal(1, saves.Count(s => s == 200));
        Assert.Equal(7, saves.Count(s => s == 409));
    }

    [Theory]
    [InlineData("MaxReviewLength", "1999")]
    [InlineData("MaxReviewLength", "20001")]
    [InlineData("MinRatingsForAverage", "0")]
    [InlineData("MinRatingsForAverage", "11")]
    [InlineData("ReviewsPageSize", "4")]
    [InlineData("ReviewsPageSize", "51")]
    [InlineData("SnapshotsToKeep", "-1")]
    [InlineData("SnapshotsToKeep", "366")]
    [InlineData("DetachedRetentionDays", "-1")]
    [InlineData("DetachedRetentionDays", "36501")]
    [InlineData("ModerationLogDays", "-1")]
    [InlineData("ModerationLogDays", "3651")]
    [InlineData("WhoCanRate", "\"Everyone\"")]
    [InlineData("ShowDetailButton", "\"Sometimes\"")]
    [InlineData("PosterBadgeContent", "\"\"")]
    [InlineData("PosterBadgePosition", "\"TopStart\"")]
    [InlineData("PageHostMode", "\"Fallback\"")]
    [InlineData("DeletedUserPolicy", "\"Purge\"")]
    [InlineData("RateableItemTypes", "[]")]
    [InlineData("RateableItemTypes", "[\"Movie\",\"Person\"]")]
    [InlineData("RateableItemTypes", "[\"Movie\",\" \"]")]
    [InlineData("RaterUserIds", "[\"00000000000000000000000000000000\"]")]
    [InlineData("RaterUserIds", "[\"6b1f4bd0c8a94d0e9e1f0a2b3c4d5e6f\"]")]
    [InlineData("MirrorToUserData", "true")]
    public async Task AValueTheSettingCantTake_Is400NamingIt_InADryRunAndASave_AndSavesNothing(string field, string value)
    {
        await _fx.StartAsync();
        var version = RatingsSettingsFields.Version(S.Configuration);
        var body = "{\"" + field + "\":" + value + "}";

        foreach (var path in new[] { Path, $"{Path}?dryRun=false&version={version}" })
        {
            var problem = await ProblemAsync(S.Admin, path, body, 400);
            Assert.Equal(RatingsSettingsService.InvalidSettingCode + field, problem.Code);
            Assert.StartsWith(field + " ", problem.Detail, StringComparison.Ordinal);
            Assert.EndsWith("Nothing was saved.", problem.Detail, StringComparison.Ordinal);
        }

        Assert.Equal(version, RatingsSettingsFields.Version(S.Configuration));
    }

    // SelectedUsers with an empty list is rejected: a list with no user of the server is as empty.
    [Theory]
    [InlineData("""{"WhoCanRate":"SelectedUsers"}""")]
    [InlineData("""{"WhoCanRate":"SelectedUsers","RaterUserIds":[]}""")]
    public async Task SelectedUsers_WithNoUserOfTheServer_Is400(string body)
    {
        S.Configuration.RaterUserIds = [Guid.NewGuid()];
        await _fx.StartAsync();

        var problem = await ProblemAsync(S.Admin, Path, body, 400);

        Assert.Equal(RatingsSettingsService.InvalidSettingCode + "RaterUserIds", problem.Code);
        Assert.Contains("at least one user of this server", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeyThatNamesNoSetting_Is400UnknownSetting_AndAValueOfTheWrongType_Is400()
    {
        await _fx.StartAsync();

        var unknown = await ProblemAsync(S.Admin, Path, """{"MaxReviewLenght":3000,"ReviewsPageSize":30}""", 400);
        Assert.Equal(RatingsSettingsService.UnknownSettingCode, unknown.Code);
        Assert.StartsWith("No such setting: MaxReviewLenght (", unknown.Detail, StringComparison.Ordinal);

        // A value of the wrong JSON type is MVC's own validation problem, before the plugin sees the body: for a list it names
        // the setting's path; Jellyfin's converters for true/false and numbers report the path as "$" (the type, not the
        // setting). The page never sends one. A name of the wrong type is the plugin's 400.
        foreach (var (body, names) in new[] { ("""{"EnableRatings":"yes"}""", "System.Boolean"), ("""{"MaxReviewLength":2.5}""", "System.Int32"), ("""{"RateableItemTypes":"Movie"}""", "$.RateableItemTypes") })
        {
            using var wrongType = await _fx.SendAsync(S.Admin, HttpMethod.Post, Path, body);
            Assert.Equal(400, (int)wrongType.StatusCode);
            Assert.Contains(names, await wrongType.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Equal(RatingsSettingsService.InvalidSettingCode + "WhoCanRate", (await ProblemAsync(S.Admin, Path, """{"WhoCanRate":5}""", 400)).Code);
        Assert.Equal(20, S.Configuration.ReviewsPageSize);
    }

    [Fact]
    public async Task ANameInAnyCase_IsSavedInItsOwnSpelling_AndListsInTheTabsOrder()
    {
        await _fx.StartAsync();
        var version = RatingsSettingsFields.Version(S.Configuration);

        var report = await _fx.JsonAsync(S.Admin, HttpMethod.Post, $"{Path}?dryRun=false&version={version}", "{\"WhoCanRate\":\" selectedUSERS \",\"RaterUserIds\":[\"" + S.Viewer2.Id.ToString("N") + "\",\"" + S.Admin.Id + "\",\"" + S.Viewer2.Id + "\"],\"RateableItemTypes\":[\"boxset\",\"MOVIE\",\"Movie\"],\"PosterBadgePosition\":\"Bottom-End\"}");

        Assert.Equal("SelectedUsers", S.Configuration.WhoCanRate);
        Assert.Equal([S.Admin.Id, S.Viewer2.Id], S.Configuration.RaterUserIds);
        Assert.Equal(["Movie", "BoxSet"], S.Configuration.RateableItemTypes);
        Assert.Equal("bottom-end", S.Configuration.PosterBadgePosition);
        var raters = report.GetProperty("Changes").EnumerateArray().Single(c => c.GetProperty("Field").GetString() == "RaterUserIds");
        Assert.Equal(("Nobody", "admin, viewer2"), (raters.GetProperty("From").GetString(), raters.GetProperty("To").GetString()));
    }

    [Fact]
    public async Task NumbersSavedPastTheTab_AreNamedInProblems_SnapshotsToKeepAndDetachedRetentionDaysToo()
    {
        // A bypass probe's values, saved through POST /Plugins/{id}/Configuration.
        S.Configuration.SnapshotsToKeep = 100000;
        S.Configuration.DetachedRetentionDays = -9;
        S.Configuration.ModerationLogDays = -1;
        S.Configuration.ReviewsPageSize = 1000;
        await _fx.StartAsync();

        var problems = (await SettingsAsync()).GetProperty("Problems").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(
            [
                "ReviewsPageSize 1000 is outside 5 to 50; 50 is used",
                "ModerationLogDays -1 is outside 0 to 3650; 0 is used",
                "SnapshotsToKeep 100000 is outside 0 to 365; 365 is used",
                "DetachedRetentionDays -9 is outside 0 to 36500; 0 is used"
            ],
            problems);
    }

    [Fact]
    public async Task AValueThisVersionDoesntKnow_StaysUnlessChanged_AndAHeldUserOrKindMayStay()
    {
        var gone = Guid.NewGuid();
        S.Configuration.WhoCanRate = "Everyone";
        S.Configuration.RaterUserIds = [S.Viewer.Id, gone];
        S.Configuration.RateableItemTypes = ["Movie", "Photo"];
        await _fx.StartAsync();

        var view = await SettingsAsync();
        Assert.Equal("Everyone", view.GetProperty("Settings").GetProperty("WhoCanRate").GetString());
        Assert.Contains(view.GetProperty("Problems").EnumerateArray(), p => p.GetString()!.StartsWith("WhoCanRate 'Everyone'", StringComparison.Ordinal));
        Assert.Contains(view.GetProperty("Users").EnumerateArray(), u => Guid.Parse(u.GetProperty("Id").GetString()!) == gone && u.GetProperty("Missing").GetBoolean() && u.GetProperty("Name").GetString() == string.Empty);
        Assert.Equal("Photo", view.GetProperty("ItemKinds").EnumerateArray().Last().GetString());

        var version = view.GetProperty("Version").GetString();
        var saved = await _fx.JsonAsync(S.Admin, HttpMethod.Post, $"{Path}?dryRun=false&version={version}", "{\"ReviewsPageSize\":30,\"RateableItemTypes\":[\"photo\",\"Movie\",\"Audio\"],\"RaterUserIds\":[\"" + gone + "\",\"" + S.Viewer.Id + "\"]}");

        Assert.Equal("Everyone", S.Configuration.WhoCanRate);
        Assert.Equal(["Movie", "Audio", "Photo"], S.Configuration.RateableItemTypes);
        Assert.Equal([S.Viewer.Id, gone], S.Configuration.RaterUserIds);
        Assert.Equal(["RateableItemTypes", "ReviewsPageSize"], saved.GetProperty("Changes").EnumerateArray().Select(c => c.GetProperty("Field").GetString()));
        Assert.Equal(400, (await ProblemAsync(S.Admin, Path, """{"RateableItemTypes":["Movie","Person"]}""", 400)).Status);
    }

    // Checklist item 2b through the Settings tab's API: Selected users with only viewer; viewer2 reads but can't rate; All
    // users again, and no data changed. No restart: the services read the settings at each request.
    [Fact]
    public async Task WhoCanRate_TakesEffectAtTheNextRequest_AndTheDryRunCountsWhoLosesTheRight()
    {
        await _fx.StartAsync();
        await S.Service.SetStarsAsync(S.Viewer, _matrix.Id, 4, Ct);
        await S.Service.SetStarsAsync(S.Viewer2, _matrix.Id, 3, Ct);
        var item = $"/UserRatings/Items/{_matrix.Id:N}";
        var only = "{\"WhoCanRate\":\"SelectedUsers\",\"RaterUserIds\":[\"" + S.Viewer.Id + "\"]}";

        var dry = await _fx.JsonAsync(S.Admin, HttpMethod.Post, Path, only);
        Assert.Contains("2 users can no longer rate or review. Their ratings and reviews stay", Consequences(dry), StringComparison.Ordinal);
        await SaveAsync(only);

        Assert.Equal("NotARater", (await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, item)).GetProperty("Can").GetProperty("Reason").GetString());
        Assert.Equal(403, await StatusAsync(S.Viewer2, HttpMethod.Put, $"{item}/Mine/Rating", """{"Stars":5}"""));
        Assert.Equal(200, await StatusAsync(S.Viewer, HttpMethod.Put, $"{item}/Mine/Rating", """{"Stars":4.5}"""));
        Assert.Equal(2, (await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, item)).GetProperty("Summary").GetProperty("Count").GetInt32());

        var back = await _fx.JsonAsync(S.Admin, HttpMethod.Post, Path, """{"WhoCanRate":"AllUsers"}""");
        Assert.Contains("2 users can now rate and review.", Consequences(back), StringComparison.Ordinal);
        await SaveAsync("""{"WhoCanRate":"AllUsers"}""");
        Assert.Equal(200, await StatusAsync(S.Viewer2, HttpMethod.Put, $"{item}/Mine/Rating", """{"Stars":2.5}"""));
        Assert.Equal(2, S.Store.Store.GetAttachedForItem(_matrix.Id).Count);
    }

    [Fact]
    public async Task RateableItemTypes_TakesEffectAtTheNextRequest_AndTheDryRunCountsWhatItHides()
    {
        await _fx.StartAsync();
        await S.Service.SetStarsAsync(S.Viewer, _matrix.Id, 4, Ct);
        await S.Service.SetReviewAsync(S.Viewer, _matrix.Id, "Still the best.", false, (DateTime?)null, Ct);
        var item = $"/UserRatings/Items/{_matrix.Id:N}";
        var noMovies = """{"RateableItemTypes":["Series","Episode"]}""";

        var dry = await _fx.JsonAsync(S.Admin, HttpMethod.Post, Path, noMovies);
        Assert.Contains("The ratings on Movies (1 rating, 1 review on the whole server) stop showing and can't be changed. Nothing is deleted", Consequences(dry), StringComparison.Ordinal);
        await SaveAsync(noMovies);

        Assert.Equal(404, await StatusAsync(S.Viewer, HttpMethod.Get, item));
        Assert.Single((await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Mine/Inaccessible")).GetProperty("Items").EnumerateArray());

        await SaveAsync("""{"RateableItemTypes":["Movie","Series","Episode"]}""");
        Assert.Equal(200, await StatusAsync(S.Viewer, HttpMethod.Get, item));
    }

    [Fact]
    public async Task TheDryRun_SaysWhatAChangeMeans_ForTheMembersAndTheData()
    {
        await _fx.StartAsync();
        await S.Service.SetStarsAsync(S.Viewer, _matrix.Id, 4, Ct);
        await S.Service.SetReviewAsync(S.Viewer, _matrix.Id, new string('a', 2500), false, (DateTime?)null, Ct);
        await S.Store.Store.UpsertAsync(StoreFixture.Movie(S.Viewer2.Id, Guid.NewGuid()) with { DetachedUtc = Now.AddDays(-40), DetachReason = RatingValues.DetachReason.Missing }, Ct);
        await S.Store.Store.UpsertAsync(StoreFixture.Movie(S.Viewer2.Id, Guid.NewGuid(), tmdb: "949") with { DetachedUtc = Now.AddDays(-5), DetachReason = RatingValues.DetachReason.Missing }, Ct);
        await S.Store.Store.AppendModerationLogAsync(new ModerationLogRecord { AtUtc = Now.AddDays(-100), Action = RatingValues.ModerationAction.RemoveText, RatingId = Guid.NewGuid(), TargetUserId = Guid.NewGuid(), ItemId = Guid.NewGuid(), Reason = "Spam", Details = "{}" }, Ct);

        Assert.Contains("Ratings and reviews stop showing in the web client (1 rating, 1 review on the whole server). Nothing is deleted", Consequences(await DryAsync("""{"EnableRatings":false}""")), StringComparison.Ordinal);
        Assert.Contains("Review text stops showing (1 review on the whole server); the stars stay. Nothing is deleted.", Consequences(await DryAsync("""{"EnableReviews":false}""")), StringComparison.Ordinal);
        Assert.Contains("1 review on the whole server is longer than 2000 characters: it stays as written, and must be shortened before it can be edited.", Consequences(await DryAsync("""{"MaxReviewLength":2000}""")), StringComparison.Ordinal);
        Assert.Contains("1 detached rating on the whole server, detached more than 30 days ago, moves to the archive at the next daily maintenance", Consequences(await DryAsync("""{"DetachedRetentionDays":30}""")), StringComparison.Ordinal);
        Assert.Contains("1 moderation log row on the whole server, older than 30 days, is deleted at the next daily maintenance.", Consequences(await DryAsync("""{"ModerationLogDays":30}""")), StringComparison.Ordinal);
        Assert.Contains("users deleted from now on are kept as a former member's", Consequences(await DryAsync("""{"DeletedUserPolicy":"Anonymize"}""")), StringComparison.Ordinal);
        Assert.Contains("keeps only the newest 3 daily snapshots", Consequences(await DryAsync("""{"SnapshotsToKeep":3}""")), StringComparison.Ordinal);

        // Raising a limit, or a display setting, means nothing to warn of; the three restart settings say so.
        Assert.Empty((await DryAsync("""{"MaxReviewLength":8000,"ModerationLogDays":400,"ShowHistogram":false}""")).GetProperty("Consequences").EnumerateArray());
        var restart = await DryAsync("""{"EnableTopRatedPage":false}""");
        Assert.True(restart.GetProperty("RestartNeeded").GetBoolean());
        Assert.Equal("Takes effect after the server restarts: Top Rated page.", Consequences(restart));
        Assert.True(restart.GetProperty("Changes")[0].GetProperty("RestartNeeded").GetBoolean());
        Assert.False((await DryAsync("""{"ShowHistogram":false}""")).GetProperty("RestartNeeded").GetBoolean());

        // A server-wide setting's dry run counts the whole server's rows for a
        // restricted admin too (viewer2's libraries here), and says so; counts only, no title.
        var restricted = new ApiFixture.AsCaller("admin", S.Viewer2.Id);
        foreach (var body in new[] { """{"EnableRatings":false}""", """{"MaxReviewLength":2000}""", """{"DetachedRetentionDays":30}""", """{"ModerationLogDays":30}""" })
        {
            var mine = Consequences(await _fx.JsonAsync(restricted, HttpMethod.Post, Path, body));
            Assert.Equal(Consequences(await DryAsync(body)), mine);
            Assert.Contains(" on the whole server", mine, StringComparison.Ordinal);
            Assert.DoesNotContain("Matrix", mine, StringComparison.Ordinal);
        }
    }

    // A lower MaxReviewLength's count measures as ReviewText.Validate does (UTF-16 code units, SQLite's length() counts code
    // points); the kinds' counts are the attached rows'.
    [Fact]
    public async Task TheDryRunsCounts_MeasureAReviewAsTheLimitDoes_AndCountOnlyAttachedRows()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 1500));
        await S.Store.Store.UpsertAsync(StoreFixture.Movie(S.Viewer.Id, Guid.NewGuid(), text: emoji), Ct);
        await S.Store.Store.UpsertAsync(StoreFixture.Movie(S.Viewer2.Id, Guid.NewGuid(), text: new string('a', 1500)), Ct);
        await S.Store.Store.UpsertAsync(StoreFixture.Movie(S.Admin.Id, Guid.NewGuid(), text: emoji) with { DetachedUtc = Now, DetachReason = RatingValues.DetachReason.Missing }, Ct);

        Assert.Equal(3000, emoji.Length);
        Assert.Equal(1, S.Store.Store.CountReviewsLongerThan(2000));
        Assert.Equal(0, S.Store.Store.CountReviewsLongerThan(3000));
        Assert.Equal((2, 2), S.Store.Store.CountAttachedByKind()["Movie"]);
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value, ApiFixture.Json);

    private static string Consequences(JsonElement report) => string.Join("\n", report.GetProperty("Consequences").EnumerateArray().Select(c => c.GetString()));

    private Dictionary<string, object> NonDefaults() => new(StringComparer.Ordinal)
    {
        ["EnableRatings"] = false,
        ["EnableReviews"] = false,
        ["WhoCanRate"] = "SelectedUsers",
        ["RaterUserIds"] = new[] { S.Viewer.Id },
        ["RateableItemTypes"] = new[] { "Movie", "Audio", "BoxSet" },
        ["RequirePlayedToRate"] = true,
        ["MaxReviewLength"] = 2000,
        ["AllowSpoilerFlag"] = false,
        ["HideReviewsFromHiddenUsers"] = true,
        ["HideReviewsFromDisabledUsers"] = false,
        ["ShowReviewerAvatars"] = false,
        ["ShowAverageChip"] = false,
        ["ShowHistogram"] = false,
        ["MinRatingsForAverage"] = 3,
        ["SectionExpandedByDefault"] = false,
        ["ShowDetailButton"] = "Never",
        ["ReviewsPageSize"] = 50,
        ["EnableWebUi"] = false,
        ["ReportWebAnchorProblems"] = false,
        ["SnapshotsToKeep"] = 0,
        ["DetachedRetentionDays"] = 30,
        ["ModerationLogDays"] = 0,
        ["DeletedUserPolicy"] = "KeepAsOrphan",
        ["ShowJellyfinEnhancedBanner"] = false,
        ["ShowOnPosters"] = true,
        ["PosterBadgeContent"] = "Both",
        ["PosterBadgePosition"] = "bottom-end",
        ["EnableTopRatedPage"] = false,
        ["RegisterPluginPages"] = false,
        ["PageHostMode"] = "Auto",
        ["ShowFallbackNav"] = false,
        ["MirrorToUserData"] = false
    };

    private Task<JsonElement> SettingsAsync() => _fx.JsonAsync(S.Admin, HttpMethod.Get, Path);

    private Task<JsonElement> DryAsync(string body) => _fx.JsonAsync(S.Admin, HttpMethod.Post, Path, body);

    private async Task SaveAsync(string body)
    {
        var version = RatingsSettingsFields.Version(S.Configuration);
        var report = await _fx.JsonAsync(S.Admin, HttpMethod.Post, $"{Path}?dryRun=false&version={version}", body);
        Assert.True(report.GetProperty("Applied").GetBoolean(), body);
    }

    private async Task<int> StatusAsync(object? caller, HttpMethod method, string path, string? json = null)
    {
        using var response = await _fx.SendAsync(caller, method, path, json);
        return (int)response.StatusCode;
    }

    private async Task<(int Status, string? Code, string Detail)> ProblemAsync(object caller, string path, string body, int status)
    {
        using var response = await _fx.SendAsync(caller, HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True((int)response.StatusCode == status, $"{path}: {(int)response.StatusCode} {text}");
        Assert.True(response.Headers.CacheControl?.NoStore, path);
        using var problem = JsonDocument.Parse(text);
        var code = problem.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
        return ((int)response.StatusCode, code, problem.RootElement.GetProperty("detail").GetString()!);
    }
}
