using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.UserRatings.Api;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Services;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Data.Sqlite;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Api;

/// <summary>
/// The member endpoints end to end in process: API keys 403 on every member route; the wire format (PascalCase, Guids as
/// Jellyfin writes them,
/// null fields left out); Capabilities for an admin, a member, a member <c>WhoCanRate</c> leaves out and with ratings off;
/// an item's read and every write with its errors (400, 403, one 404 for "can't open" and "can't rate", 409, 413, 503
/// read-only before the item lookup, 503 busy within about 5 s); the review list's orders and pages; the double click as
/// the last write; <c>Mine/Inaccessible</c> and <c>DELETE Mine/{ratingId}</c> after the member lost access; and a settings
/// change that needs no cache call because every read path applies visibility after the cache.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class UserRatingsApiTests : IAsyncLifetime
{
    private readonly ApiFixture _fx = new();
    private readonly Movie _matrix;
    private readonly Movie _other;

    public UserRatingsApiTests()
    {
        _matrix = _fx.Services.AddMovie(_fx.Services.Movies, "The Matrix", "603");
        _other = _fx.Services.AddMovie(_fx.Services.Movies, "Heat", "949");
    }

    private ServicesFixture S => _fx.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _fx.DisposeAsync();

    // --------------------------------------------------------------------------------------------- who may call

    [Fact]
    public async Task AnApiKey_Gets403_OnEveryMemberRoute_Nobody401_AndOnlyTheScriptIsAnonymous()
    {
        await _fx.StartAsync();
        var id = _matrix.Id.ToString("N");
        var routes = new (HttpMethod Method, string Path, string? Body)[]
        {
            (HttpMethod.Get, "/UserRatings/Capabilities", null),
            (HttpMethod.Get, $"/UserRatings/Items/{id}", null),
            (HttpMethod.Get, $"/UserRatings/Items/{id}/Reviews", null),
            (HttpMethod.Put, $"/UserRatings/Items/{id}/Mine/Rating", """{"Stars":3.5}"""),
            (HttpMethod.Delete, $"/UserRatings/Items/{id}/Mine/Rating", null),
            (HttpMethod.Put, $"/UserRatings/Items/{id}/Mine/Review", """{"Text":"x"}"""),
            (HttpMethod.Delete, $"/UserRatings/Items/{id}/Mine/Review", null),
            (HttpMethod.Delete, $"/UserRatings/Items/{id}/Mine", null),
            (HttpMethod.Get, "/UserRatings/Mine/Inaccessible", null),
            (HttpMethod.Delete, $"/UserRatings/Mine/{Guid.NewGuid():N}", null),
        };

        foreach (var (method, path, body) in routes)
        {
            Assert.Equal(UserRatingsController.NoUserMessage, await _fx.ProblemAsync(ApiFixture.ApiKey, method, path, 403, body));
            using var anonymous = await _fx.SendAsync(null, method, path, body);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        // A user id the server doesn't know (deleted since the token was issued) is a 403 too, never another user's data.
        Assert.NotNull(await _fx.ProblemAsync(new ApiFixture.AsCaller("user", Guid.NewGuid()), HttpMethod.Get, "/UserRatings/Capabilities", 403));

        using var script = await _fx.SendAsync(null, HttpMethod.Get, "/UserRatings/ClientScript");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal(0, S.Store.Store.Count().Attached);
    }

    // --------------------------------------------------------------------------------------------- Capabilities

    [Fact]
    public async Task Capabilities_ForAnAdmin_AMember_AndAMemberWhoCanRateLeavesOut_AndWhileRatingsAreOff()
    {
        await _fx.StartAsync();

        var admin = await _fx.JsonAsync(S.Admin, HttpMethod.Get, "/UserRatings/Capabilities");
        Assert.True(admin.GetProperty("Enabled").GetBoolean());
        Assert.True(admin.GetProperty("IsAdmin").GetBoolean());
        Assert.True(admin.GetProperty("CanRate").GetBoolean());
        Assert.True(admin.GetProperty("CanReview").GetBoolean());
        Assert.Equal(5000, admin.GetProperty("MaxReviewLength").GetInt32());
        Assert.True(admin.GetProperty("AllowSpoilerFlag").GetBoolean());
        Assert.Equal(
            ["Movie", "Series", "Season", "Episode", "Video", "MusicVideo", "MusicAlbum", "Book", "AudioBook"],
            admin.GetProperty("RateableItemTypes").EnumerateArray().Select(e => e.GetString()));
        var display = admin.GetProperty("Display");
        Assert.Equal(
            (true, "Auto", true, true, 1, 20),
            (display.GetProperty("ShowAverageChip").GetBoolean(), display.GetProperty("ShowDetailButton").GetString(), display.GetProperty("SectionExpandedByDefault").GetBoolean(),
                display.GetProperty("ShowHistogram").GetBoolean(), display.GetProperty("MinRatingsForAverage").GetInt32(), display.GetProperty("ReviewsPageSize").GetInt32()));
        var posters = display.GetProperty("Posters");
        Assert.Equal((false, "Average", "top-start"), (posters.GetProperty("Enabled").GetBoolean(), posters.GetProperty("Content").GetString(), posters.GetProperty("Position").GetString()));

        // No Top Rated page is registered before 1.1, so there is nothing to link to whatever the setting says.
        Assert.False(admin.GetProperty("TopRatedPageEnabled").GetBoolean());
        Assert.False(admin.GetProperty("StoreReadOnly").GetBoolean());

        // A member: no admin extras; the admin's own account without the Administrator role on the request is no admin
        // either (Jellyfin's RequiresElevation, never the stored flag).
        var viewer = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Capabilities");
        Assert.False(viewer.GetProperty("IsAdmin").GetBoolean());
        Assert.True(viewer.GetProperty("CanRate").GetBoolean());
        Assert.False((await _fx.JsonAsync(new ApiFixture.AsCaller("user", S.Admin.Id), HttpMethod.Get, "/UserRatings/Capabilities")).GetProperty("IsAdmin").GetBoolean());

        // WhoCanRate: viewer2 isn't listed; the display settings and the kinds follow the configuration on the next request.
        S.Configuration = new PluginConfiguration
        {
            WhoCanRate = "SelectedUsers", RaterUserIds = [S.Viewer.Id], EnableReviews = false, RateableItemTypes = ["Episode", "Movie", "BoxSet"],
            ShowDetailButton = "always", PosterBadgePosition = "bottom-end", PosterBadgeContent = "Both", ShowOnPosters = true, ReviewsPageSize = 99
        };
        var notARater = await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, "/UserRatings/Capabilities");
        Assert.True(notARater.GetProperty("Enabled").GetBoolean());
        Assert.False(notARater.GetProperty("CanRate").GetBoolean());
        Assert.False(notARater.GetProperty("CanReview").GetBoolean());
        Assert.Equal(["Movie", "Episode", "BoxSet"], notARater.GetProperty("RateableItemTypes").EnumerateArray().Select(e => e.GetString()));
        var rater = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Capabilities");
        Assert.Equal((true, false), (rater.GetProperty("CanRate").GetBoolean(), rater.GetProperty("CanReview").GetBoolean()));
        var changed = rater.GetProperty("Display");
        Assert.Equal(("Always", 50), (changed.GetProperty("ShowDetailButton").GetString(), changed.GetProperty("ReviewsPageSize").GetInt32()));
        Assert.Equal((true, "Both", "bottom-end"), (changed.GetProperty("Posters").GetProperty("Enabled").GetBoolean(), changed.GetProperty("Posters").GetProperty("Content").GetString(), changed.GetProperty("Posters").GetProperty("Position").GetString()));

        // An unknown WhoCanRate with an empty list: nobody may rate (the safe fallback), everyone still reads.
        S.Configuration = new PluginConfiguration { WhoCanRate = "Everyone" };
        Assert.False((await _fx.JsonAsync(S.Admin, HttpMethod.Get, "/UserRatings/Capabilities")).GetProperty("CanRate").GetBoolean());
        await _fx.JsonAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}");
        Assert.Equal(RatingsService.NotARaterMessage, await _fx.ProblemAsync(S.Admin, HttpMethod.Put, $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating", 403, """{"Stars":4}"""));

        // Ratings off: still 200, saying so, with nothing to do and no kind to rate, so the web client can hide itself.
        S.Configuration = new PluginConfiguration { EnableRatings = false };
        var off = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Capabilities");
        Assert.Equal((false, false, false), (off.GetProperty("Enabled").GetBoolean(), off.GetProperty("CanRate").GetBoolean(), off.GetProperty("CanReview").GetBoolean()));
        Assert.Empty(off.GetProperty("RateableItemTypes").EnumerateArray());
    }

    // --------------------------------------------------------------------------------------------- the item and its writes

    [Fact]
    public async Task AnItem_ReadAndRated_OnTheWire_InTheDocumentedShape()
    {
        S.Viewer.ProfileImage = new ImageInfo("/config/users/viewer.png");
        await _fx.StartAsync();
        var path = $"/UserRatings/Items/{_matrix.Id:N}";

        // Not rated yet: an empty summary, no Mine (left out, as Jellyfin leaves out nulls), no reviews, no copies.
        var empty = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, path);
        Assert.Equal(_matrix.Id.ToString("N"), empty.GetProperty("ItemId").GetString());
        Assert.False(empty.TryGetProperty("Mine", out _));
        Assert.False(empty.GetProperty("Summary").TryGetProperty("Average", out _));
        Assert.Equal((0, 0), (empty.GetProperty("Summary").GetProperty("Count").GetInt32(), empty.GetProperty("Summary").GetProperty("ReviewCount").GetInt32()));
        Assert.Equal(new int[10], empty.GetProperty("Summary").GetProperty("Distribution").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal((0, 0), (empty.GetProperty("Reviews").GetProperty("TotalRecordCount").GetInt32(), empty.GetProperty("Reviews").GetProperty("Items").GetArrayLength()));
        Assert.Empty(empty.GetProperty("OtherCopies").EnumerateArray());
        Assert.Equal((true, true, false), Can(empty));

        // PUT {"Stars":3.5} answers the item after the write.
        var rated = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path + "/Mine/Rating", """{"Stars":3.5}""");
        var mine = rated.GetProperty("Mine");
        Assert.Equal(3.5, mine.GetProperty("Stars").GetDouble());
        Assert.False(mine.TryGetProperty("Text", out _));
        Assert.False(mine.GetProperty("Edited").GetBoolean());
        Assert.EndsWith("Z", mine.GetProperty("UpdatedUtc").GetString(), StringComparison.Ordinal);
        var row = S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!;
        Assert.Equal(row.Id.ToString("N"), mine.GetProperty("Id").GetString());
        Assert.Equal(row.UpdatedUtc, mine.GetProperty("UpdatedUtc").GetDateTime().ToUniversalTime());
        Assert.Equal((1, 3.5), (rated.GetProperty("Summary").GetProperty("Count").GetInt32(), rated.GetProperty("Summary").GetProperty("Average").GetDouble()));
        Assert.Equal(1, rated.GetProperty("Summary").GetProperty("Distribution")[6].GetInt32());

        // Others see it in the summary and the list, not as theirs; the strings a string-tolerant client sends work too.
        var asViewer2 = await _fx.JsonAsync(S.Viewer2, HttpMethod.Put, path + "/Mine/Rating", """{"Stars":"4.5"}""");
        Assert.Equal((2, 4.0), (asViewer2.GetProperty("Summary").GetProperty("Count").GetInt32(), asViewer2.GetProperty("Summary").GetProperty("Average").GetDouble()));
        var review = Assert.Single(asViewer2.GetProperty("Reviews").GetProperty("Items").EnumerateArray());
        Assert.Equal((S.Viewer.Id.ToString("N"), "viewer", 3.5, false), (review.GetProperty("UserId").GetString(), review.GetProperty("UserName").GetString(), review.GetProperty("Stars").GetDouble(), review.GetProperty("IsFormerMember").GetBoolean()));
        Assert.Equal("tag-viewer", review.GetProperty("UserImageTag").GetString());

        // The admin reads the same, plus Can.Moderate (from the request's Administrator role).
        var asAdmin = await _fx.JsonAsync(S.Admin, HttpMethod.Get, path);
        Assert.Equal((true, true, true), Can(asAdmin));
        Assert.Equal(2, asAdmin.GetProperty("Reviews").GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal((true, true, false), Can(await _fx.JsonAsync(new ApiFixture.AsCaller("user", S.Admin.Id), HttpMethod.Get, path)));

        // A review with a spoiler, then clearing the stars keeps a text-only review; clearing the review then deletes the row.
        var reviewed = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path + "/Mine/Review", """{"Text":"  Whoa.\r\nI know kung fu.  ","IsSpoiler":true}""");
        Assert.Equal(("Whoa.\nI know kung fu.", true), (reviewed.GetProperty("Mine").GetProperty("Text").GetString(), reviewed.GetProperty("Mine").GetProperty("IsSpoiler").GetBoolean()));
        Assert.True(reviewed.GetProperty("Mine").TryGetProperty("ReviewUpdatedUtc", out _));
        Assert.Equal(1, reviewed.GetProperty("Summary").GetProperty("ReviewCount").GetInt32());
        var edited = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path + "/Mine/Review", """{"Text":"Whoa. I know kung fu."}""");
        Assert.True(edited.GetProperty("Mine").GetProperty("Edited").GetBoolean());
        var textOnly = await _fx.JsonAsync(S.Viewer, HttpMethod.Delete, path + "/Mine/Rating");
        Assert.False(textOnly.GetProperty("Mine").TryGetProperty("Stars", out _));
        Assert.Equal("Whoa. I know kung fu.", textOnly.GetProperty("Mine").GetProperty("Text").GetString());
        var unchanged = await _fx.JsonAsync(S.Viewer, HttpMethod.Delete, path + "/Mine/Rating");
        Assert.Equal(textOnly.GetProperty("Mine").GetProperty("UpdatedUtc").GetString(), unchanged.GetProperty("Mine").GetProperty("UpdatedUtc").GetString());
        var gone = await _fx.JsonAsync(S.Viewer, HttpMethod.Delete, path + "/Mine/Review");
        Assert.False(gone.TryGetProperty("Mine", out _));
        Assert.NotNull(S.Store.Store.GetTombstone(row.Id));

        // DELETE Mine: 204, also with nothing left to delete.
        await _fx.JsonAsync(S.Viewer2, HttpMethod.Delete, path + "/Mine", status: 204);
        await _fx.JsonAsync(S.Viewer2, HttpMethod.Delete, path + "/Mine", status: 204);
        Assert.Equal(0, S.Store.Store.Count().Attached);
    }

    [Theory]
    [InlineData("""{"Stars":0}""")]
    [InlineData("""{"Stars":0.25}""")]
    [InlineData("""{"Stars":3.3}""")]
    [InlineData("""{"Stars":5.5}""")]
    [InlineData("""{"Stars":-1}""")]
    [InlineData("""{"Stars":null}""")]
    [InlineData("""{}""")]
    public async Task InvalidStars_Are400_WithTheStarsMessage(string body)
    {
        await _fx.StartAsync();

        Assert.Equal(StarValue.InvalidMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating", 400, body));
        Assert.Null(S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id));
    }

    [Theory]
    [InlineData("""{"Stars":"abc"}""")]
    [InlineData("""{"Stars":"NaN"}""")]
    [InlineData("""{"Stars":1e400}""")]
    [InlineData("""{"Stars":[3]}""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public async Task StarsThatArentANumber_Are400_FromModelBinding(string body)
    {
        await _fx.StartAsync();

        // MVC's validation ProblemDetails (with "errors"), as for every bad body of the server's own API.
        await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating", body.Length == 0 ? 415 : 400, body.Length == 0 ? null : body);
        Assert.Null(S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id));
    }

    [Fact]
    public async Task Reviews_Empty_TooLong_Off_TheIfUpdatedUtc409_AndABodyOver64KB()
    {
        await _fx.StartAsync();
        var path = $"/UserRatings/Items/{_matrix.Id:N}/Mine/Review";

        Assert.Equal(ReviewText.EmptyMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 400, """{"Text":"  \r\n  "}"""));
        Assert.Equal(ReviewText.EmptyMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 400, """{"IsSpoiler":true}"""));
        await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 400, JsonSerializer.Serialize(new { Text = new string('x', 5001) }));
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, JsonSerializer.Serialize(new { Text = new string('x', 5000) }));

        // Two windows: the first saves with the UpdatedUtc it read, the second with the same one is a 409 and changes nothing.
        var read = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}");
        var seen = read.GetProperty("Mine").GetProperty("UpdatedUtc").GetString();
        S.Advance(TimeSpan.FromSeconds(5));
        var first = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, JsonSerializer.Serialize(new { Text = "From tab one", IfUpdatedUtc = seen }));
        Assert.Equal("From tab one", first.GetProperty("Mine").GetProperty("Text").GetString());
        Assert.Equal(RatingsService.ChangedElsewhereMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 409, JsonSerializer.Serialize(new { Text = "From tab two", IfUpdatedUtc = seen })));
        Assert.Equal("From tab one", S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!.ReviewText);

        // With the new value it saves; without IfUpdatedUtc it overwrites (flow E's Overwrite).
        var latest = first.GetProperty("Mine").GetProperty("UpdatedUtc").GetString();
        S.Advance(TimeSpan.FromSeconds(5));
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, JsonSerializer.Serialize(new { Text = "From tab two", IfUpdatedUtc = latest }));
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, """{"Text":"Overwritten"}""");
        Assert.Equal("Overwritten", S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!.ReviewText);

        // IfUpdatedUtc for a row deleted elsewhere is a 409 too, never a new row.
        await _fx.JsonAsync(S.Viewer, HttpMethod.Delete, $"/UserRatings/Items/{_matrix.Id:N}/Mine", status: 204);
        await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 409, JsonSerializer.Serialize(new { Text = "Back", IfUpdatedUtc = latest }));
        Assert.Null(S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id));

        // Reviews off: 403 on the write and on the list; the summary and the stars go on.
        S.Configuration = new PluginConfiguration { EnableReviews = false };
        Assert.Equal(RatingsService.ReviewsOffMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 403, """{"Text":"Off"}"""));
        Assert.Equal(RatingsService.ReviewsOffMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}/Reviews", 403));
        await _fx.JsonAsync(S.Viewer2, HttpMethod.Put, $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating", """{"Stars":2}""");
        var item = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}");
        Assert.Equal(0, item.GetProperty("Reviews").GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal((true, false, false), Can(item));
        Assert.Equal(1, item.GetProperty("Summary").GetProperty("Count").GetInt32());
    }

    [Fact]
    public async Task Reviews_ASpoilerFlagChangedElsewhere_IsA409_ForAStaleWindow_AndNeverShowsTheReviewAsEdited()
    {
        // Over HTTP: the flag moves Mine.ReviewUpdatedUtc, never Edited (its own card's or the
        // other members' "(edited)").
        await _fx.StartAsync();
        var path = $"/UserRatings/Items/{_matrix.Id:N}/Mine/Review";
        var first = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, """{"Text":"Words.","IsSpoiler":false,"IfNoReview":true}""");
        var read = first.GetProperty("Mine").GetProperty("ReviewUpdatedUtc").GetString()!;

        S.Advance(TimeSpan.FromSeconds(1));
        var marked = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, JsonSerializer.Serialize(new { Text = "Words.", IsSpoiler = true, IfReviewUpdatedUtc = read }));
        var mine = marked.GetProperty("Mine");
        Assert.Equal((true, false), (mine.GetProperty("IsSpoiler").GetBoolean(), mine.GetProperty("Edited").GetBoolean()));
        Assert.NotEqual(read, mine.GetProperty("ReviewUpdatedUtc").GetString());

        S.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RatingsService.ReviewChangedElsewhereMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 409, JsonSerializer.Serialize(new { Text = "Words.", IsSpoiler = false, IfReviewUpdatedUtc = read })));
        Assert.True(S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!.IsSpoiler);

        var asViewer2 = await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}");
        var row = Assert.Single(asViewer2.GetProperty("Reviews").GetProperty("Items").EnumerateArray());
        Assert.Equal((true, false), (row.GetProperty("IsSpoiler").GetBoolean(), row.GetProperty("Edited").GetBoolean()));
    }

    [Fact]
    public async Task Reviews_TheReviewPreconditions_IfReviewUpdatedUtcAndIfNoReview_AdditiveAndOneAtATime()
    {
        // Over HTTP: a stars-only change elsewhere is no 409 for a write carrying the
        // review's own time; IfNoReview makes a first review in a second window a 409; two preconditions are a 400; a body
        // without any (an older caller) is as before, and IfNoReview false is no precondition.
        await _fx.StartAsync();
        var path = $"/UserRatings/Items/{_matrix.Id:N}/Mine/Review";
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating", """{"Stars":3}""");

        S.Advance(TimeSpan.FromSeconds(1));
        var first = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, """{"Text":"Window A","IfNoReview":true}""");
        Assert.Equal(RatingsService.ReviewChangedElsewhereMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 409, """{"Text":"Window B","IfNoReview":true}"""));
        Assert.Equal("Window A", S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!.ReviewText);

        // The stars changed elsewhere: the review's time, as a JavaScript Date writes it, still holds.
        var reviewTime = first.GetProperty("Mine").GetProperty("ReviewUpdatedUtc").GetString()!;
        var rowTime = first.GetProperty("Mine").GetProperty("UpdatedUtc").GetString()!;
        S.Advance(TimeSpan.FromSeconds(1));
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating", """{"Stars":4.5}""");
        var asDate = DateTimeOffset.Parse(reviewTime, CultureInfo.InvariantCulture).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        Assert.Equal(RatingsService.ChangedElsewhereMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 409, JsonSerializer.Serialize(new { Text = "Row time", IfUpdatedUtc = rowTime })));
        S.Advance(TimeSpan.FromSeconds(1));
        var edited = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, JsonSerializer.Serialize(new { Text = "Window A, edited", IfReviewUpdatedUtc = asDate }));
        Assert.Equal("Window A, edited", edited.GetProperty("Mine").GetProperty("Text").GetString());
        Assert.Equal(4.5, edited.GetProperty("Mine").GetProperty("Stars").GetDouble());
        Assert.Equal(RatingsService.ReviewChangedElsewhereMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 409, JsonSerializer.Serialize(new { Text = "Stale", IfReviewUpdatedUtc = asDate })));

        // At most one precondition.
        var newer = edited.GetProperty("Mine").GetProperty("ReviewUpdatedUtc").GetString();
        Assert.Equal(ReviewPrecondition.RowAndReviewMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 400, JsonSerializer.Serialize(new { Text = "Two", IfUpdatedUtc = rowTime, IfReviewUpdatedUtc = newer })));
        Assert.Equal(ReviewPrecondition.RowAndReviewMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 400, JsonSerializer.Serialize(new { Text = "Two", IfUpdatedUtc = rowTime, IfNoReview = true })));
        Assert.Equal(ReviewPrecondition.BothReviewMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 400, JsonSerializer.Serialize(new { Text = "Two", IfReviewUpdatedUtc = newer, IfNoReview = true })));
        Assert.Equal("Window A, edited", S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!.ReviewText);

        // Older callers: no precondition replaces whatever is there; IfNoReview false is none.
        S.Advance(TimeSpan.FromSeconds(1));
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, """{"Text":"No precondition"}""");
        S.Advance(TimeSpan.FromSeconds(1));
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, """{"Text":"False is none","IfNoReview":false}""");
        Assert.Equal("False is none", S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!.ReviewText);
    }

    [Fact]
    public async Task AnItemTheCallerCantOpen_OneThatIsntRateable_AndOneThatDoesntExist_AreOneAndTheSame404()
    {
        var hidden = S.AddMovie(S.Movies2, "Hidden");
        var boxSet = S.Add(S.Movies, new BoxSet { Name = "Box" });
        await _fx.StartAsync();

        var answers = new List<string>();
        foreach (var id in new[] { hidden.Id, boxSet.Id, Guid.NewGuid() })
        {
            foreach (var (method, suffix, body) in new (HttpMethod, string, string?)[]
            {
                (HttpMethod.Get, string.Empty, null), (HttpMethod.Get, "/Reviews", null), (HttpMethod.Put, "/Mine/Rating", """{"Stars":3}"""),
                (HttpMethod.Delete, "/Mine/Rating", null), (HttpMethod.Put, "/Mine/Review", """{"Text":"x"}"""), (HttpMethod.Delete, "/Mine/Review", null),
                (HttpMethod.Delete, "/Mine", null)
            })
            {
                using var response = await _fx.SendAsync(S.Viewer2, method, $"/UserRatings/Items/{id:N}" + suffix, body);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                answers.Add(await response.Content.ReadAsStringAsync(Ct));
            }
        }

        Assert.Single(answers.Distinct());
        Assert.Contains(ItemAccess.NotFoundMessage, answers[0], StringComparison.Ordinal);

        // The viewer who can open Movies 2 can rate there; a box set is no one's.
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, $"/UserRatings/Items/{hidden.Id:N}/Mine/Rating", """{"Stars":3}""");
        await _fx.ProblemAsync(S.Admin, HttpMethod.Get, $"/UserRatings/Items/{boxSet.Id:N}", 404);
    }

    [Fact]
    public async Task WhoCanRate_SelectedUsers_LeavesOthersReading_DeletingTheirOwn_AndChangesNoData()
    {
        await _fx.StartAsync();
        var path = $"/UserRatings/Items/{_matrix.Id:N}";
        await _fx.JsonAsync(S.Viewer2, HttpMethod.Put, path + "/Mine/Review", """{"Text":"Older review"}""");
        await _fx.JsonAsync(S.Viewer2, HttpMethod.Put, $"/UserRatings/Items/{_other.Id:N}/Mine/Rating", """{"Stars":2.5}""");
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path + "/Mine/Rating", """{"Stars":5}""");

        // One rater: viewer. viewer2 reads everything, can't create or edit, can still clear and delete their own rows.
        S.Configuration = new PluginConfiguration { WhoCanRate = "SelectedUsers", RaterUserIds = [S.Viewer.Id] };
        var asViewer2 = await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, path);
        Assert.Equal((false, false, false), Can(asViewer2));
        Assert.Equal("Older review", asViewer2.GetProperty("Mine").GetProperty("Text").GetString());
        Assert.Equal((1, 1), (asViewer2.GetProperty("Summary").GetProperty("Count").GetInt32(), asViewer2.GetProperty("Summary").GetProperty("ReviewCount").GetInt32()));
        Assert.Equal(1, asViewer2.GetProperty("Reviews").GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(RatingsService.NotARaterMessage, await _fx.ProblemAsync(S.Viewer2, HttpMethod.Put, path + "/Mine/Rating", 403, """{"Stars":1}"""));
        Assert.Equal(RatingsService.NotARaterMessage, await _fx.ProblemAsync(S.Viewer2, HttpMethod.Put, path + "/Mine/Review", 403, """{"Text":"Edited"}"""));
        Assert.Equal(RatingsService.NotARaterMessage, await _fx.ProblemAsync(S.Admin, HttpMethod.Put, path + "/Mine/Rating", 403, """{"Stars":1}"""));
        Assert.Equal((true, true, false), Can(await _fx.JsonAsync(S.Viewer, HttpMethod.Get, path)));
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path + "/Mine/Rating", """{"Stars":4.5}""");

        var cleared = await _fx.JsonAsync(S.Viewer2, HttpMethod.Delete, path + "/Mine/Review");
        Assert.False(cleared.TryGetProperty("Mine", out _));
        var ownRow = S.Store.Store.GetAttached(S.Viewer2.Id, _other.Id)!;
        await _fx.JsonAsync(S.Viewer2, HttpMethod.Delete, $"/UserRatings/Mine/{ownRow.Id:N}", status: 204);
        Assert.Equal(1, S.Store.Store.Count().Attached);

        // All users again: nothing was hidden or changed by the setting; viewer2 rates again.
        S.Configuration = new PluginConfiguration();
        Assert.Equal(4.5, (await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, path)).GetProperty("Summary").GetProperty("Average").GetDouble());
        await _fx.JsonAsync(S.Viewer2, HttpMethod.Put, path + "/Mine/Rating", """{"Stars":1}""");
    }

    [Fact]
    public async Task RatingsOff_ItemReadsAndWrites403_TheOwnRowsWayOutStays()
    {
        await _fx.StartAsync();
        var path = $"/UserRatings/Items/{_matrix.Id:N}";
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path + "/Mine/Rating", """{"Stars":3}""");
        var row = S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!;

        S.Configuration = new PluginConfiguration { EnableRatings = false };
        foreach (var (method, suffix, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Get, string.Empty, null), (HttpMethod.Get, "/Reviews", null), (HttpMethod.Put, "/Mine/Rating", """{"Stars":3}"""),
            (HttpMethod.Delete, "/Mine/Rating", null), (HttpMethod.Put, "/Mine/Review", """{"Text":"x"}"""), (HttpMethod.Delete, "/Mine/Review", null),
            (HttpMethod.Delete, "/Mine", null)
        })
        {
            Assert.Equal(RatingsService.RatingsOffMessage, await _fx.ProblemAsync(S.Viewer, method, path + suffix, 403, body));
        }

        // A member's own rows are theirs: while every item route is 403, the list holds
        // all of them, the item still openable included, and the delete by id works.
        var listed = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Mine/Inaccessible");
        Assert.Equal(1, listed.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(row.Id.ToString("N"), listed.GetProperty("Items")[0].GetProperty("RatingId").GetString());
        await _fx.JsonAsync(S.Viewer, HttpMethod.Delete, $"/UserRatings/Mine/{row.Id:N}", status: 204);
        Assert.Null(S.Store.Store.Get(row.Id));

        // Back on: an openable item's row is no longer in the list.
        S.Configuration = new PluginConfiguration();
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path + "/Mine/Rating", """{"Stars":4}""");
        Assert.Equal(0, (await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Mine/Inaccessible")).GetProperty("TotalRecordCount").GetInt32());
    }

    // --------------------------------------------------------------------------------------------- the review list

    [Fact]
    public async Task TheReviewList_OrdersPagesAndNames_AndNeverHoldsTheCallersOwn()
    {
        var authors = Enumerable.Range(1, 6).Select(n => ServicesFixture.NewUser("member" + n.ToString(CultureInfo.InvariantCulture))).ToList();
        S.Users.AddRange(authors);
        await _fx.StartAsync();
        var path = $"/UserRatings/Items/{_matrix.Id:N}";

        // member1..member6 rate 1, 5, 3, none (text only), 5, 2 stars, a second apart; the viewer rates too.
        int?[] stars = [2, 10, 6, null, 10, 4];
        for (var i = 0; i < authors.Count; i++)
        {
            S.Advance(TimeSpan.FromSeconds(1));
            if (stars[i] is { } s2)
            {
                await _fx.JsonAsync(authors[i], HttpMethod.Put, path + "/Mine/Rating", JsonSerializer.Serialize(new { Stars = s2 / 2m }));
            }
            else
            {
                await _fx.JsonAsync(authors[i], HttpMethod.Put, path + "/Mine/Review", """{"Text":"No stars from me"}""");
            }
        }

        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path + "/Mine/Rating", """{"Stars":4}""");

        string[] Names(JsonElement page) => page.GetProperty("Items").EnumerateArray().Select(e => e.GetProperty("UserName").GetString()!).ToArray();
        Task<JsonElement> List(string query) => _fx.JsonAsync(S.Viewer, HttpMethod.Get, path + "/Reviews" + query);

        Assert.Equal(["member6", "member5", "member4", "member3", "member2", "member1"], Names(await List(string.Empty)));
        Assert.Equal(["member1", "member2", "member3", "member4", "member5", "member6"], Names(await List("?sort=Oldest")));
        Assert.Equal(["member5", "member2", "member3", "member6", "member1", "member4"], Names(await List("?sort=Highest")));
        Assert.Equal(["member1", "member6", "member3", "member5", "member2", "member4"], Names(await List("?sort=lowest")));
        var page = await List("?sort=Oldest&startIndex=2&limit=3");
        Assert.Equal(["member3", "member4", "member5"], Names(page));
        Assert.Equal((6, 2), (page.GetProperty("TotalRecordCount").GetInt32(), page.GetProperty("StartIndex").GetInt32()));
        Assert.Empty((await List("?startIndex=50")).GetProperty("Items").EnumerateArray());

        // GET Items embeds the same first page, by its own paging; the viewer's row is Mine, never in the list.
        var item = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, path + "?limit=2&sort=Highest");
        Assert.Equal(["member5", "member2"], Names(item.GetProperty("Reviews")));
        Assert.Equal(6, item.GetProperty("Reviews").GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(4, item.GetProperty("Mine").GetProperty("Stars").GetDouble());
        Assert.Equal(7, item.GetProperty("Summary").GetProperty("Count").GetInt32() + 1);

        // ReviewsPageSize is the default page.
        S.Configuration = new PluginConfiguration { ReviewsPageSize = 5 };
        Assert.Equal(5, (await List(string.Empty)).GetProperty("Items").GetArrayLength());

        // Out of range or unknown: 400, as the kit's API rules say (limit 1 to 200; a sort of another endpoint).
        // A number is no sort either, also one a sort has (sort=1 once sorted Oldest).
        foreach (var query in new[] { "?limit=0", "?limit=201", "?startIndex=-1", "?sort=Best", "?sort=7", "?sort=1", "?sort=0" })
        {
            await _fx.ProblemAsync(S.Viewer, HttpMethod.Get, path + "/Reviews" + query, 400);
            await _fx.ProblemAsync(S.Viewer, HttpMethod.Get, path + query, 400);
        }

        await _fx.ProblemAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Mine/Inaccessible?limit=201", 400);

        // Names are read per request: a rename shows at once; avatars follow ShowReviewerAvatars.
        authors[4].Username = "renamed";
        authors[4].ProfileImage = new ImageInfo("/config/users/member5.png");
        S.Configuration = new PluginConfiguration { ShowReviewerAvatars = false };
        var renamed = (await List("?sort=Highest&limit=1")).GetProperty("Items")[0];
        Assert.Equal("renamed", renamed.GetProperty("UserName").GetString());
        Assert.False(renamed.TryGetProperty("UserImageTag", out _));
        S.Configuration = new PluginConfiguration();
        Assert.Equal("tag-renamed", (await List("?sort=Highest&limit=1")).GetProperty("Items")[0].GetProperty("UserImageTag").GetString());
    }

    [Fact]
    public async Task ASettingsChange_NeedsNoCacheCall_BecauseEveryReadPathAppliesVisibilityAfterTheCache()
    {
        // Visibility after the cache, proven on every member read path: Items (summary, list, Mine), Reviews and OtherCopies.
        // The cache holds raw rows; the Hide settings and the authors' states apply at read time.
        var copy = S.AddMovie(S.Movies2, "The Matrix", "603");
        var disabled = ServicesFixture.NewUser("disabled");
        S.Users.Add(disabled);
        await _fx.StartAsync();
        var path = $"/UserRatings/Items/{_matrix.Id:N}";
        foreach (var (user, stars) in new[] { (S.Viewer, 2m), (S.Viewer2, 4m), (disabled, 5m) })
        {
            await _fx.JsonAsync(user, HttpMethod.Put, path + "/Mine/Rating", JsonSerializer.Serialize(new { Stars = stars }));
            if (user != S.Viewer2)
            {
                await _fx.JsonAsync(user, HttpMethod.Put, $"/UserRatings/Items/{copy.Id:N}/Mine/Rating", JsonSerializer.Serialize(new { Stars = stars }));
            }
        }

        // Warm: every entry filled once.
        var before = await _fx.JsonAsync(S.Admin, HttpMethod.Get, path);
        Assert.Equal((3, 11.0 / 3), (before.GetProperty("Summary").GetProperty("Count").GetInt32(), before.GetProperty("Summary").GetProperty("Average").GetDouble()));
        Assert.Equal(2, Assert.Single(before.GetProperty("OtherCopies").EnumerateArray()).GetProperty("Count").GetInt32());
        var fills = S.Cache.Fills;
        var cached = S.Cache.Count;

        // The author is disabled (HideReviewsFromDisabledUsers is on by default): no cache call, no refill.
        disabled.SetPermission(PermissionKind.IsDisabled, true);
        var hiding = await _fx.JsonAsync(S.Admin, HttpMethod.Get, path);
        Assert.Equal((2, 3.0), (hiding.GetProperty("Summary").GetProperty("Count").GetInt32(), hiding.GetProperty("Summary").GetProperty("Average").GetDouble()));
        Assert.Equal(2, hiding.GetProperty("Reviews").GetProperty("TotalRecordCount").GetInt32());
        Assert.DoesNotContain("disabled", hiding.GetProperty("Reviews").GetProperty("Items").EnumerateArray().Select(e => e.GetProperty("UserName").GetString()));
        Assert.Equal(1, Assert.Single(hiding.GetProperty("OtherCopies").EnumerateArray()).GetProperty("Count").GetInt32());
        Assert.Equal(2, (await _fx.JsonAsync(S.Viewer, HttpMethod.Get, path + "/Reviews")).GetProperty("TotalRecordCount").GetInt32() + 1);

        // The disabled author still sees their own row as Mine, and the same summary as everyone.
        var own = await _fx.JsonAsync(disabled, HttpMethod.Get, path);
        Assert.Equal(5, own.GetProperty("Mine").GetProperty("Stars").GetDouble());
        Assert.Equal(2, own.GetProperty("Summary").GetProperty("Count").GetInt32());

        // A settings change (the Hide switch off): the row counts again at once, still without any cache call.
        S.Configuration = new PluginConfiguration { HideReviewsFromDisabledUsers = false };
        var shown = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, path);
        Assert.Equal(3, shown.GetProperty("Summary").GetProperty("Count").GetInt32());
        Assert.Equal(2, shown.GetProperty("Reviews").GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(2, Assert.Single(shown.GetProperty("OtherCopies").EnumerateArray()).GetProperty("Count").GetInt32());
        Assert.Equal(3, (await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, path + "/Reviews")).GetProperty("TotalRecordCount").GetInt32() + 1);

        // RateableItemTypes applies at read time too: Movie off is the item's 404, at once.
        S.Configuration = new PluginConfiguration { RateableItemTypes = ["Episode"] };
        await _fx.ProblemAsync(S.Viewer, HttpMethod.Get, path, 404);

        Assert.Equal((fills, cached), (S.Cache.Fills, S.Cache.Count));
    }

    // --------------------------------------------------------------------------------------------- concurrency, read-only, busy

    [Fact]
    public async Task ADoubleClick_IsTheLastWrite_OneRow_NeverAConflict()
    {
        // A clock that moves on at every reading, so each write has its own time (the fixture's manual clock stands still).
        await _fx.StartAsync(new RatingsService(S.Store.Store, S.Cache, S.ItemAccess, S.Siblings, S.Played, S.Settings, S.UserManager.Object, S.Library.Object, new TickingClock()));
        var path = $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating";

        for (var round = 0; round < 5; round++)
        {
            var values = new[] { 1.5m, 4.5m, 3m, 5m };
            var answers = await Task.WhenAll(values.Select(async v =>
            {
                using var response = await _fx.SendAsync(S.Viewer, HttpMethod.Put, path, JsonSerializer.Serialize(new { Stars = v }));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                return document.RootElement.GetProperty("Mine").Clone();
            }));

            // One row; the value is the write that committed last, which also has the latest UpdatedUtc (the time is read
            // under the writer lock), so it is the answer with the latest time.
            var row = Assert.Single(S.Store.Store.GetAttachedForItem(_matrix.Id));
            var last = answers.OrderBy(a => a.GetProperty("UpdatedUtc").GetDateTime()).Last();
            Assert.Equal(last.GetProperty("Stars").GetDouble(), row.Stars2!.Value / 2.0);
            Assert.All(answers, a => Assert.Equal(row.Id.ToString("N"), a.GetProperty("Id").GetString()));
        }

        Assert.Equal(1, S.Store.Store.Count().Attached);
    }

    [Fact]
    public void ThePuts_ReadAt64KBAtMost()
    {
        // The input limits: [RequestSizeLimit(65536)] on the review's PUT (and the rating's). The in-process server
        // doesn't enforce it, so the attribute is checked here and the 413 itself in the sandbox (userratings-api-probe.mjs);
        // the kit's filter turns Kestrel's 413 into ProblemDetails (Kit.Tests).
        foreach (var name in new[] { nameof(UserRatingsController.SetUserRatingsMyReview), nameof(UserRatingsController.SetUserRatingsMyRating) })
        {
            Assert.Single(typeof(UserRatingsController).GetMethod(name)!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), false));
        }

        Assert.Equal(65536, UserRatingsController.MaxBodyBytes);
    }

    [Fact]
    public async Task ANewerVersionsDatabase_IsReadOnly_Writes503BeforeTheItemLookup_ReadsGoOn()
    {
        var row = StoreFixture.Movie(S.Viewer.Id, _matrix.Id, 7);
        await S.Store.Store.UpsertAsync(row, Ct);
        await S.Store.Database.WriteAsync((c, t) => { StoreFixture.Execute(c, t, "PRAGMA user_version = 99;"); return 0; }, Ct);
        S.Store.Reopen();
        await _fx.StartAsync(S.NewService());

        Assert.True((await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Capabilities")).GetProperty("StoreReadOnly").GetBoolean());
        Assert.False((await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Capabilities")).GetProperty("CanRate").GetBoolean());
        var item = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}");
        Assert.Equal(3.5, item.GetProperty("Mine").GetProperty("Stars").GetDouble());
        Assert.Equal((false, false, false), Can(item));

        // 503 with the kit's sentence for every write, before the item is looked up: an id that doesn't exist is a 503 too,
        // not a 404, so a read-only server never answers "not found" for a write.
        foreach (var id in new[] { _matrix.Id, Guid.NewGuid() })
        {
            foreach (var (method, suffix, body) in new (HttpMethod, string, string?)[]
            {
                (HttpMethod.Put, "/Mine/Rating", """{"Stars":3}"""), (HttpMethod.Delete, "/Mine/Rating", null), (HttpMethod.Put, "/Mine/Review", """{"Text":"x"}"""),
                (HttpMethod.Delete, "/Mine/Review", null), (HttpMethod.Delete, "/Mine", null)
            })
            {
                Assert.Equal(SqliteMessages.NewerVersion, await _fx.ProblemAsync(S.Viewer, method, $"/UserRatings/Items/{id:N}" + suffix, 503, body));
            }
        }

        Assert.Equal(SqliteMessages.NewerVersion, await _fx.ProblemAsync(S.Viewer, HttpMethod.Delete, $"/UserRatings/Mine/{row.Id:N}", 503));
        await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Mine/Inaccessible");
        Assert.Equal(1, S.Store.Store.Count().Attached);
    }

    [Fact]
    public async Task ADatabaseAnotherConnectionHolds_IsA503Busy_InAbout5Seconds_ReadsGoOn_AndTheNextWriteIsImmediate()
    {
        await _fx.StartAsync();
        var path = $"/UserRatings/Items/{_matrix.Id:N}";
        await _fx.JsonAsync(S.Viewer2, HttpMethod.Put, path + "/Mine/Rating", """{"Stars":2}""");

        await using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = S.Store.File, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await holder.OpenAsync(Ct);
            StoreFixture.Execute(holder, null, "PRAGMA busy_timeout=0;");
            StoreFixture.Execute(holder, null, "BEGIN IMMEDIATE;");

            var clock = Stopwatch.StartNew();
            Assert.Equal(SqliteMessages.Busy, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path + "/Mine/Rating", 503, """{"Stars":4}"""));
            clock.Stop();
            Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8));

            // A WAL reader isn't blocked by the write lock: the item still reads.
            Assert.Equal(1, (await _fx.JsonAsync(S.Viewer, HttpMethod.Get, path)).GetProperty("Summary").GetProperty("Count").GetInt32());
        }

        var quick = Stopwatch.StartNew();
        Assert.Equal(4, (await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path + "/Mine/Rating", """{"Stars":4}""")).GetProperty("Mine").GetProperty("Stars").GetDouble());
        Assert.True(quick.Elapsed < TimeSpan.FromSeconds(2), "The write after the holder let go took " + quick.Elapsed);
        Assert.False(S.Store.Store.IsReadOnly);
    }

    // --------------------------------------------------------------------------------------------- lost access

    [Fact]
    public async Task AMemberWhoLostAccess_FindsTheirOwnRows_WithNothingAboutTheItems_AndDeletesThemById()
    {
        var other = S.AddMovie(S.Movies, "Seven Samurai");
        await _fx.StartAsync();
        await _fx.JsonAsync(S.Viewer2, HttpMethod.Put, $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating", """{"Stars":4.5}""");
        var longText = new string('a', 119) + "\U0001F600" + "tail";
        await _fx.JsonAsync(S.Viewer2, HttpMethod.Put, $"/UserRatings/Items/{_matrix.Id:N}/Mine/Review", JsonSerializer.Serialize(new { Text = longText }));
        await _fx.JsonAsync(S.Viewer2, HttpMethod.Put, $"/UserRatings/Items/{other.Id:N}/Mine/Rating", """{"Stars":1}""");
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating", """{"Stars":2}""");
        Assert.Equal(0, (await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, "/UserRatings/Mine/Inaccessible")).GetProperty("TotalRecordCount").GetInt32());

        // The admin takes The Matrix away from viewer2 (as a library or parental rule would).
        S.Library.Hide(S.Viewer2, _matrix);
        await _fx.ProblemAsync(S.Viewer2, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}", 404);
        await _fx.ProblemAsync(S.Viewer2, HttpMethod.Delete, $"/UserRatings/Items/{_matrix.Id:N}/Mine", 404);

        // The row stays and counts for those who can open the item.
        Assert.Equal(2, (await _fx.JsonAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}")).GetProperty("Summary").GetProperty("Count").GetInt32());

        var list = await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, "/UserRatings/Mine/Inaccessible");
        Assert.Equal(1, list.GetProperty("TotalRecordCount").GetInt32());
        var entry = Assert.Single(list.GetProperty("Items").EnumerateArray());
        var row = S.Store.Store.GetAttached(S.Viewer2.Id, _matrix.Id)!;
        Assert.Equal(["CreatedUtc", "RatingId", "Stars", "TextSnippet", "UpdatedUtc"], entry.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal((row.Id.ToString("N"), 4.5), (entry.GetProperty("RatingId").GetString(), entry.GetProperty("Stars").GetDouble()));
        Assert.Equal(new string('a', 119), entry.GetProperty("TextSnippet").GetString());
        Assert.DoesNotContain("Matrix", list.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_matrix.Id.ToString("N"), list.ToString(), StringComparison.Ordinal);

        // Someone else's id and an id that doesn't exist are one 404; the owner's delete is 204 with a tombstone.
        var viewersRow = S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!;
        var notYours = await _fx.ProblemAsync(S.Viewer2, HttpMethod.Delete, $"/UserRatings/Mine/{viewersRow.Id:N}", 404);
        var missing = await _fx.ProblemAsync(S.Viewer2, HttpMethod.Delete, $"/UserRatings/Mine/{Guid.NewGuid():N}", 404);
        Assert.Equal((RatingsService.NotYoursMessage, RatingsService.NotYoursMessage), (notYours, missing));
        Assert.NotNull(S.Store.Store.Get(viewersRow.Id));

        await _fx.JsonAsync(S.Viewer2, HttpMethod.Delete, $"/UserRatings/Mine/{row.Id:N}", status: 204);
        Assert.Equal(RatingValues.DeletedBy.User, S.Store.Store.GetTombstone(row.Id)!.DeletedBy);
        Assert.Equal(RatingsService.NotYoursMessage, await _fx.ProblemAsync(S.Viewer2, HttpMethod.Delete, $"/UserRatings/Mine/{row.Id:N}", 404));
        Assert.Equal(0, (await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, "/UserRatings/Mine/Inaccessible")).GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(1, (await _fx.JsonAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}")).GetProperty("Summary").GetProperty("Count").GetInt32());

        // Paging of the list.
        for (var i = 0; i < 3; i++)
        {
            var movie = S.AddMovie(S.Movies, "Paged " + i.ToString(CultureInfo.InvariantCulture));
            await _fx.JsonAsync(S.Viewer2, HttpMethod.Put, $"/UserRatings/Items/{movie.Id:N}/Mine/Rating", """{"Stars":3}""");
            S.Library.Hide(S.Viewer2, movie);
        }

        var paged = await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, "/UserRatings/Mine/Inaccessible?startIndex=1&limit=1");
        Assert.Equal((3, 1, 1), (paged.GetProperty("TotalRecordCount").GetInt32(), paged.GetProperty("StartIndex").GetInt32(), paged.GetProperty("Items").GetArrayLength()));
    }

    [Fact]
    public async Task ThePlayedRule_403WhenNotPlayed_200WhenPlayed_And503WhenItCantBeRead()
    {
        await _fx.StartAsync();
        S.Configuration = new PluginConfiguration { RequirePlayedToRate = true };
        var path = $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating";

        Played(false);
        Assert.Equal(RatingsService.NotPlayedMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 403, """{"Stars":3}"""));
        Played(true);
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, """{"Stars":3}""");

        // The fresh read finds nothing (the item can't be loaded with its user data): never a guess, a 503 (fail closed).
        S.Library.Mock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.DtoOptions.EnableUserData && q.User == null))).Returns([]);
        Assert.Equal(RatingsService.PlayedUnknownMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 503, """{"Stars":4}"""));
        Assert.Equal(6, S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!.Stars2);
    }

    // ------------------------------------------------------------------------------------------------ findings, fixed

    [Fact]
    public async Task ThePlayedRule_IsInCanAndCapabilities_SoTheUiNeverOffersA403()
    {
        // Can used to say Rate and Review while the PUT was the 403 "Play this first".
        await _fx.StartAsync();
        S.Configuration = new PluginConfiguration { RequirePlayedToRate = true };
        var path = $"/UserRatings/Items/{_matrix.Id:N}";

        var caps = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Capabilities");
        Assert.True(caps.GetProperty("RequirePlayedToRate").GetBoolean());
        Assert.True(caps.GetProperty("CanRate").GetBoolean());

        Played(false);
        var unplayed = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, path);
        Assert.Equal((false, false, false), Can(unplayed));
        Assert.Equal("NeedsPlay", unplayed.GetProperty("Can").GetProperty("Reason").GetString());
        Assert.Equal(RatingsService.NotPlayedMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path + "/Mine/Rating", 403, """{"Stars":3}"""));

        Played(true);
        var played = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, path);
        Assert.Equal((true, true, false), Can(played));
        Assert.False(played.GetProperty("Can").TryGetProperty("Reason", out _));

        // The other reasons, and the setting off.
        S.Configuration = new PluginConfiguration { WhoCanRate = "SelectedUsers", RaterUserIds = [S.Viewer.Id] };
        Assert.Equal("NotARater", (await _fx.JsonAsync(S.Viewer2, HttpMethod.Get, path)).GetProperty("Can").GetProperty("Reason").GetString());
        Assert.False((await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Capabilities")).GetProperty("RequirePlayedToRate").GetBoolean());
    }

    [Theory]
    [InlineData("Z")]
    [InlineData("+00:00")]
    [InlineData("+02:00")]
    [InlineData("-05:30")]
    [InlineData("")]
    public async Task IfUpdatedUtc_AsAJavaScriptDateSendsItBack_InAnyZoneForm_Matches(string zone)
    {
        // The stored time is whole milliseconds, which Date.toISOString() keeps, and the server compares at the
        // millisecond; JSON's "+00:00" or another offset is read as local time, no zone as UTC.
        S.Store.Clock.Set(new DateTimeOffset(StoreFixture.T0.AddTicks(138_048_619)));
        await _fx.StartAsync();
        var path = $"/UserRatings/Items/{_matrix.Id:N}/Mine/Review";
        var saved = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, """{"Text":"First."}""");
        var instant = DateTimeOffset.Parse(saved.GetProperty("Mine").GetProperty("UpdatedUtc").GetString()!, CultureInfo.InvariantCulture);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 3, 20, 13, 804, TimeSpan.Zero), instant);
        var sent = zone switch
        {
            "Z" => instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            "" => instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture),
            _ => instant.ToOffset(TimeSpan.Parse(zone.TrimStart('+'), CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)
        };
        Assert.EndsWith(zone, sent, StringComparison.Ordinal);

        S.Advance(TimeSpan.FromSeconds(1));
        var edited = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, JsonSerializer.Serialize(new { Text = "Second.", IfUpdatedUtc = sent }));
        Assert.Equal("Second.", edited.GetProperty("Mine").GetProperty("Text").GetString());

        // The same time again is now stale: the 409, nothing changed.
        Assert.Equal(RatingsService.ChangedElsewhereMessage, await _fx.ProblemAsync(S.Viewer, HttpMethod.Put, path, 409, JsonSerializer.Serialize(new { Text = "Third.", IfUpdatedUtc = sent })));
        Assert.Equal("Second.", S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!.ReviewText);
    }

    [Fact]
    public async Task AWritesAnswer_WhenTheReadAfterItIs503_IsTheWritesOwnRow_MarkedPartial()
    {
        // The write committed, then reading the item again failed (the database busy or damaged just then): the answer is
        // 200 from the transaction's own row, never a 503 a client would show as a refused write.
        var real = S.Store.Store;
        var failReads = false;
        Func<PluginException> busy = () => new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Busy);
        var store = new Mock<IRatingsStore>();
        store.SetupGet(s => s.IsReadOnly).Returns(() => real.IsReadOnly);
        store.Setup(s => s.ThrowIfReadOnly()).Callback(real.ThrowIfReadOnly);
        store.Setup(s => s.WriteAsync(It.IsAny<Func<IRatingsWriter, RatingWrite>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<IRatingsWriter, RatingWrite> work, CancellationToken ct) => real.WriteAsync(work, ct));
        store.Setup(s => s.GetFormerUsers()).Returns(() => failReads ? throw busy() : real.GetFormerUsers());
        store.Setup(s => s.GetAttachedForItem(It.IsAny<Guid>())).Returns((Guid id) => failReads ? throw busy() : real.GetAttachedForItem(id));
        await _fx.StartAsync(new RatingsService(store.Object, S.Cache, S.ItemAccess, S.Siblings, S.Played, S.Settings, S.UserManager.Object, S.Library.Object, S.Store.Clock));
        var path = $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating";
        var whole = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, """{"Stars":2}""");
        Assert.False(whole.GetProperty("IsPartial").GetBoolean());

        failReads = true;
        var partial = await _fx.JsonAsync(S.Viewer, HttpMethod.Put, path, """{"Stars":4.5}""");
        Assert.True(partial.GetProperty("IsPartial").GetBoolean());
        Assert.Equal(4.5, partial.GetProperty("Mine").GetProperty("Stars").GetDouble());
        Assert.False(partial.TryGetProperty("Summary", out _));
        Assert.False(partial.TryGetProperty("Reviews", out _));
        Assert.Equal((true, true, false), Can(partial));
        Assert.Equal(9, real.GetAttached(S.Viewer.Id, _matrix.Id)!.Stars2);

        // Clearing the last half deletes the row: a partial answer with no Mine. A plain read is the 503 as before.
        var cleared = await _fx.JsonAsync(S.Viewer, HttpMethod.Delete, path);
        Assert.True(cleared.GetProperty("IsPartial").GetBoolean());
        Assert.False(cleared.TryGetProperty("Mine", out _));
        Assert.Null(real.GetAttached(S.Viewer.Id, _matrix.Id));
        Assert.Equal(SqliteMessages.Busy, await _fx.ProblemAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}", 503));
    }

    [Fact]
    public async Task MineInaccessible_ListsTheOwnRowsOfAKindNoLongerRateable_AndOfAnItemAParentHides()
    {
        // On the wire: the item routes are the one 404 for both, so the list holds both rows, own data only.
        var folder = S.Library.Add(new Folder { Name = "Rated Folder" });
        var inFolder = S.AddMovie(S.Movies, "Heat (in a folder)", "949");
        inFolder.ParentId = folder.Id;
        await _fx.StartAsync();
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating", """{"Stars":4}""");
        await _fx.JsonAsync(S.Viewer, HttpMethod.Put, $"/UserRatings/Items/{inFolder.Id:N}/Mine/Rating", """{"Stars":1}""");
        await _fx.JsonAsync(S.Admin, HttpMethod.Put, $"/UserRatings/Items/{inFolder.Id:N}/Mine/Rating", """{"Stars":5}""");
        Assert.Single((await _fx.JsonAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_other.Id:N}")).GetProperty("OtherCopies").EnumerateArray());

        // Movie taken out of RateableItemTypes (and the folder hidden from the viewer): both rows in the list.
        S.Library.Hide(S.Viewer, folder);
        S.Configuration = new PluginConfiguration { RateableItemTypes = ["Episode", "Series"] };
        await _fx.ProblemAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}", 404);
        await _fx.ProblemAsync(S.Viewer, HttpMethod.Delete, $"/UserRatings/Items/{_matrix.Id:N}/Mine", 404);
        var list = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Mine/Inaccessible");
        Assert.Equal(2, list.GetProperty("TotalRecordCount").GetInt32());
        Assert.DoesNotContain("Heat", list.ToString(), StringComparison.Ordinal);
        var matrixRow = S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!;
        await _fx.JsonAsync(S.Viewer, HttpMethod.Delete, $"/UserRatings/Mine/{matrixRow.Id:N}", status: 204);

        // Movie rateable again: the copy in the folder is still a 404 for the viewer and no copy on Heat's page, since its
        // parent hides it, and the viewer's own row on it is the list's one row; the admin sees the copy.
        S.Configuration = new PluginConfiguration();
        await _fx.JsonAsync(S.Admin, HttpMethod.Put, $"/UserRatings/Items/{_other.Id:N}/Mine/Rating", """{"Stars":3}""");
        await _fx.ProblemAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{inFolder.Id:N}", 404);
        Assert.Empty((await _fx.JsonAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_other.Id:N}")).GetProperty("OtherCopies").EnumerateArray());
        Assert.Single((await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"/UserRatings/Items/{_other.Id:N}")).GetProperty("OtherCopies").EnumerateArray());
        var left = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Mine/Inaccessible");
        var folderRow = S.Store.Store.GetAttached(S.Viewer.Id, inFolder.Id)!;
        Assert.Equal(folderRow.Id.ToString("N"), Assert.Single(left.GetProperty("Items").EnumerateArray()).GetProperty("RatingId").GetString());
        await _fx.JsonAsync(S.Viewer, HttpMethod.Delete, $"/UserRatings/Mine/{folderRow.Id:N}", status: 204);
        Assert.Equal(0, (await _fx.JsonAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Mine/Inaccessible")).GetProperty("TotalRecordCount").GetInt32());
    }

    // The fresh read's answer for the viewer and The Matrix: a copy of the item with the viewer's row (not core's cached one).
    private void Played(bool played)
    {
        var fresh = new Movie
        {
            Id = _matrix.Id,
            Name = _matrix.Name,
            UserData =
            [
                new UserData { ItemId = _matrix.Id, Item = null!, UserId = S.Viewer.Id, User = null!, CustomDataKey = _matrix.Id.ToString("N"), Played = played }
            ]
        };
        S.Library.Mock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.DtoOptions.EnableUserData && q.User == null))).Returns([fresh]);
    }

    private static (bool Rate, bool Review, bool Moderate) Can(JsonElement item)
    {
        var can = item.GetProperty("Can");
        return (can.GetProperty("Rate").GetBoolean(), can.GetProperty("Review").GetBoolean(), can.GetProperty("Moderate").GetBoolean());
    }

    // A clock that moves on 1 ms at every reading, from the fixture's start.
    private sealed class TickingClock : TimeProvider
    {
        private long _ticks = StoreFixture.T0.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }
}
