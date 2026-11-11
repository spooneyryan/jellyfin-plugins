using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;
using Xunit;
using static Jellyfin.Plugin.LibraryMaintenance.Tests.Move.MoveApiHost;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// The planning routes over HTTP: <c>Source</c>, <c>Destinations</c>, <c>Folders</c> and <c>Plan</c> in the
/// server's wire format (PascalCase, nulls left out, GUIDs in N format), the plan preview's fields as the API lists them, and
/// the errors' status codes and <c>code</c>s. Nothing here moves a file.
/// </summary>
public sealed class MovePlanApiTests : IDisposable
{
    private const string Move = "/LibraryMaintenance/Move/";

    private readonly PlannerWorld _w = new();
    private readonly PlanServices _s;

    public MovePlanApiTests()
    {
        _s = new PlanServices(_w);
        _s.AllUsers.Add(UserOf(Alice));
    }

    public void Dispose()
    {
        _s.Dispose();
        _w.Dispose();
    }

    [Fact]
    public async Task Plan_HasEveryPreviewField_OfTheApi()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        await using var host = await StartAsync();

        using var response = await PostPlan(host, movie.Id, _w.CultLibrary.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = await Json(response);
        Assert.Equal(
            ["Blockers", "ExpiresUtc", "Method", "PlanHash", "PlanId", "Preserved", "Regenerated", "Units", "Volumes", "Warnings"],
            Names(plan));
        var unit = plan.GetProperty("Units")[0];
        Assert.Equal(["Bytes", "CreatedFolders", "Files", "From", "ItemId", "Kind", "PredictedIds", "Title", "To"], Names(unit));
        Assert.Equal(movie.Id.ToString("N"), unit.GetProperty("ItemId").GetString());
        Assert.Equal(["From", "NewId", "OldId", "Role", "To"], Names(unit.GetProperty("PredictedIds")[0]));
        Assert.Equal(["FreeBytes", "RequiredBytes", "Root"], Names(plan.GetProperty("Volumes")[0]));
        Assert.Equal(
            ["CarryOverMode", "Collections", "DateCreated", "Favorites", "HookConsumers", "InternalImages", "LockedFields", "Playlists", "ResumePoints", "UserDataUsers"],
            Names(plan.GetProperty("Preserved")));
        Assert.Equal("Rename", plan.GetProperty("Method").GetString());
        Assert.Equal(0, plan.GetProperty("Blockers").GetArrayLength());
    }

    [Fact]
    public async Task Plan_WithBlockers_Is200_AndEachBlockerHasItsCode()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        _w.File(Path.Join(_w.Cult, "Heat.1995.mkv"));
        await using var host = await StartAsync();

        using var response = await PostPlan(host, movie.Id, _w.CultLibrary.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var blocker = (await Json(response)).GetProperty("Blockers")[0];
        Assert.Equal("move.collision", blocker.GetProperty("Code").GetString());
        Assert.Equal(Path.Join(_w.Cult, "Heat.1995.mkv"), blocker.GetProperty("Paths")[0].GetString());
    }

    [Fact]
    public async Task Plan_Errors_AreTheirStatusCodes()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Alien (1979)", "Alien (1979).mkv"), "Alien", 1979, false);
        var trailer = _w.Trailer(movie, Path.Join(_w.Movies, "Alien (1979)", "trailers", "Teaser.mkv"));
        await using var host = await StartAsync();

        using var missing = await PostPlan(host, Guid.NewGuid(), _w.CultLibrary.Id);
        using var extra = await PostPlan(host, trailer.Id, _w.CultLibrary.Id);
        using var tv = await PostPlan(host, movie.Id, _w.TvLibrary.Id);
        using var bob = await PostPlan(host, movie.Id, _w.PrivateLibrary.Id, Bob);
        using var empty = await Post(host, "Plan", "{}");
        using var broken = await Post(host, "Plan", "{ not json");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, extra.StatusCode);
        Assert.Equal("move.not-eligible", (await Json(extra)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, tv.StatusCode);
        Assert.Equal("move.destination-invalid", (await Json(tv)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, bob.StatusCode);
        Assert.Equal("move.destination-invalid", (await Json(bob)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
    }

    [Fact]
    public async Task Source_Destinations_AndFolders_Answer()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Alien (1979)", "Alien (1979).mkv"), "Alien", 1979, false, "poster.jpg");
        _w.Folder("Cult", "Crime");
        await using var host = await StartAsync();

        using var source = await host.SendAsync(HttpMethod.Get, Move + "Source?itemId=" + movie.Id, As("admin"));
        using var destinations = await host.SendAsync(HttpMethod.Get, Move + "Destinations?itemId=" + movie.Id.ToString("N"), As("admin"));
        using var folders = await host.SendAsync(HttpMethod.Get, Move + "Folders?libraryId=" + _w.CultLibrary.Id + "&locationIndex=0&itemId=" + movie.Id, As("admin"));

        Assert.Equal(HttpStatusCode.OK, source.StatusCode);
        var unit = (await Json(source)).GetProperty("Unit");
        Assert.Equal("Folder", unit.GetProperty("Kind").GetString());
        Assert.Equal(2, unit.GetProperty("Files").GetArrayLength());
        Assert.Equal(["Bytes", "Rel"], Names(unit.GetProperty("Files")[0]));

        var libraries = (await Json(destinations)).EnumerateArray().ToList();
        Assert.Equal(_w.MoviesLibrary.Id.ToString("N"), libraries[0].GetProperty("LibraryId").GetString());
        Assert.True(libraries[0].GetProperty("IsSource").GetBoolean());

        var page = await Json(folders);
        Assert.Equal(1, page.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal("Crime", page.GetProperty("Items")[0].GetProperty("Name").GetString());
        Assert.True(page.GetProperty("Items")[0].GetProperty("Selectable").GetBoolean());
    }

    [Fact]
    public async Task Source_AndFolders_RefuseWhatTheCallerCantOpen_TheSameWayAsNothing()
    {
        var secret = _w.Movie(Path.Join(_w.Private, "Secret.2020.mkv"), "Secret", 2020, true);
        _w.Library.Hide(_w.Bob, secret);
        await using var host = await StartAsync();

        using var hidden = await host.SendAsync(HttpMethod.Get, Move + "Source?itemId=" + secret.Id, As("admin", Bob));
        using var missing = await host.SendAsync(HttpMethod.Get, Move + "Source?itemId=" + Guid.NewGuid(), As("admin", Bob));
        using var library = await host.SendAsync(HttpMethod.Get, Move + "Folders?libraryId=" + _w.PrivateLibrary.Id + "&locationIndex=0", As("admin", Bob));
        using var none = await host.SendAsync(HttpMethod.Get, Move + "Folders?libraryId=" + Guid.NewGuid() + "&locationIndex=0", As("admin", Bob));
        using var twice = await host.SendAsync(HttpMethod.Get, Move + "Folders?libraryId=" + _w.MoviesLibrary.Id + "&locationIndex=0&limit=5&limit=6", As("admin"));

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(await Detail(missing), await Detail(hidden));
        Assert.Equal(HttpStatusCode.NotFound, library.StatusCode);
        Assert.Equal(await Detail(none), await Detail(library));
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
    }

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    private static async Task<string?> Detail(HttpResponseMessage response) => (await Json(response)).GetProperty("detail").GetString();

    private static Task<HttpResponseMessage> Post(WebHostHarness host, string route, string body, Guid? user = null) => host.SendAsync(HttpMethod.Post, Move + route, r =>
    {
        As("admin", user)(r);
        r.Content = new StringContent(body, Encoding.UTF8, "application/json");
    });

    private static Task<HttpResponseMessage> PostPlan(WebHostHarness host, Guid itemId, Guid libraryId, Guid? user = null)
        => Post(host, "Plan", "{\"ItemIds\":[\"" + itemId + "\"],\"Destination\":{\"LibraryId\":\"" + libraryId.ToString("N") + "\",\"LocationIndex\":0}}", user);

    private Task<WebHostHarness> StartAsync() => MoveApiHost.StartAsync(new TestConfiguration(new PluginConfiguration()), () => true, _s.Register);
}
