using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Operations;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using static Jellyfin.Plugin.LibraryMaintenance.Tests.Move.MoveApiHost;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// <c>GET /LibraryMaintenance/Move/Eligibility</c>: one answer per id in order; an item the caller can't
/// open reads exactly like one that isn't there; an API key sees every library; 400 for no ids, too many, a bad id or a
/// repeated option.
/// </summary>
public sealed class MoveEligibilityApiTests : IDisposable
{
    private const string Route = "/LibraryMaintenance/Move/Eligibility";

    private readonly PlannerWorld _w = new();

    public void Dispose() => _w.Dispose();

    [Fact]
    public async Task Eligibility_AnswersEachIdInOrder()
    {
        var folder = _w.Movie(Path.Join(_w.Movies, "Alien (1979)", "Alien (1979).mkv"), "Alien", 1979, false);
        var loose = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var trailer = _w.Trailer(folder, Path.Join(_w.Movies, "Alien (1979)", "trailers", "Teaser.mkv"));
        var missing = Guid.NewGuid();
        await using var host = await StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, Route + "?ids=" + string.Join(", ", folder.Id, loose.Id.ToString("N"), trailer.Id, missing), As("admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answers = (await Json(response)).EnumerateArray().ToList();
        Assert.Equal([folder.Id, loose.Id, trailer.Id, missing], answers.Select(a => Guid.Parse(a.GetProperty("ItemId").GetString()!)));
        Assert.True(answers[0].GetProperty("Eligible").GetBoolean());
        Assert.Equal("Folder", answers[0].GetProperty("UnitKind").GetString());
        Assert.Equal("FileSet", answers[1].GetProperty("UnitKind").GetString());
        Assert.False(answers[2].GetProperty("Eligible").GetBoolean());
        Assert.Equal("move.not-eligible", answers[2].GetProperty("Code").GetString());
        Assert.Equal("move.not-found", answers[3].GetProperty("Code").GetString());

        // Jellyfin's wire format leaves nulls out (JsonDefaults: WhenWritingNull).
        static string[] Names(JsonElement answer) => answer.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Eligible", "ItemId", "UnitKind"], Names(answers[0]));
        Assert.Equal(["Code", "Eligible", "ItemId", "Reason"], Names(answers[3]));
    }

    [Fact]
    public async Task Eligibility_ForAnItemTheCallerCantOpen_IsTheSameAsMissing_AndAnApiKeySeesIt()
    {
        var secret = _w.Movie(Path.Join(_w.Private, "Secret (2020)", "Secret (2020).mkv"), "Secret", 2020, false);
        var version = _w.Library.Add(new MediaBrowser.Controller.Entities.Video { Path = _w.File(Path.Join(_w.Private, "Secret (2020)", "Secret (2020) - 720p.mkv")), OwnerId = secret.Id });
        _w.Library.Hide(_w.Bob, secret);
        var missing = Guid.NewGuid();
        await using var host = await StartAsync();

        using var bob = await host.SendAsync(HttpMethod.Get, Route + "?ids=" + secret.Id + "," + version.Id + "," + missing, As("admin", Bob));
        using var key = await host.SendAsync(HttpMethod.Get, Route + "?ids=" + secret.Id + "," + version.Id, ApiKey);

        var answers = (await Json(bob)).EnumerateArray().ToList();
        string Without(JsonElement answer) => string.Join("|", answer.EnumerateObject().Where(p => p.Name != "ItemId").Select(p => p.Name + "=" + p.Value.GetRawText()));
        Assert.Equal(Without(answers[2]), Without(answers[0]));
        Assert.Equal(Without(answers[2]), Without(answers[1]));
        var body = await bob.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(secret.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private", body, StringComparison.Ordinal);

        var all = (await Json(key)).EnumerateArray().ToList();
        Assert.True(all[0].GetProperty("Eligible").GetBoolean());
        Assert.Equal(secret.Id, Guid.Parse(all[1].GetProperty("OwnerItemId").GetString()!));
    }

    [Fact]
    public async Task Eligibility_ForAnItemInAnotherOperation_IsItemBusy()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        await using var host = await StartAsync();
        Assert.True(host.Services.GetRequiredService<LibraryOperationGate>().TryMarkBusy(OperationOwner.Duplicates, "merge-1", [movie.Id], out _));

        using var response = await host.SendAsync(HttpMethod.Get, Route + "?ids=" + movie.Id, As("admin"));

        var answer = Assert.Single((await Json(response)).EnumerateArray());
        Assert.Equal("move.item-busy", answer.GetProperty("Code").GetString());
        Assert.Equal("This item is being changed on the Duplicates tab.", answer.GetProperty("Reason").GetString());
    }

    [Fact]
    public async Task Eligibility_ForJellyfinsPlaceholderItem_IsNotFound_AndTheRestAreAnswered()
    {
        // 10.11's placeholder row (detached user data): GetItemById throws for it, and the whole list was 500 (the 1.0.2
        // verifier). The kit's GetItem takes it as no item since 1.0.3.
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        await using var host = await StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, Route + "?ids=" + movie.Id + "," + FakeLibrary.PlaceholderId, As("admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answers = (await Json(response)).EnumerateArray().ToList();
        Assert.Equal([movie.Id, FakeLibrary.PlaceholderId], answers.Select(a => Guid.Parse(a.GetProperty("ItemId").GetString()!)));
        Assert.True(answers[0].GetProperty("Eligible").GetBoolean());
        Assert.False(answers[1].GetProperty("Eligible").GetBoolean());
        Assert.Equal("move.not-found", answers[1].GetProperty("Code").GetString());
        Assert.False(string.IsNullOrEmpty(answers[1].GetProperty("Reason").GetString()));
    }

    [Fact]
    public async Task Eligibility_ForAnIdTheServerCantAnswerFor_IsNotEligible_AndTheRestAreAnswered()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var broken = Guid.NewGuid();
        _w.Library.Mock.Setup(l => l.GetItemById<BaseItem>(broken, It.IsAny<User?>())).Throws(new IOException(@"C:\private\path can't be read"));
        await using var host = await StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, Route + "?ids=" + broken + "," + movie.Id, As("admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answers = (await Json(response)).EnumerateArray().ToList();
        Assert.Equal([broken, movie.Id], answers.Select(a => Guid.Parse(a.GetProperty("ItemId").GetString()!)));
        Assert.False(answers[0].GetProperty("Eligible").GetBoolean());
        Assert.Equal("move.not-eligible", answers[0].GetProperty("Code").GetString());
        Assert.Equal(MoveMedia.UnreadableReason, answers[0].GetProperty("Reason").GetString());
        Assert.True(answers[1].GetProperty("Eligible").GetBoolean());
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("private", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "Give the item ids")]
    [InlineData("?ids=", "Give the item ids")]
    [InlineData("?ids=,%20,", "Give the item ids")]
    [InlineData("?ids=not-an-id", "isn't an item id")]
    [InlineData("?ids=00000000-0000-0000-0000-000000000000", "isn't an item id")]
    [InlineData("?ids=e0000000-0000-0000-0000-00000000000a&ids=e0000000-0000-0000-0000-00000000000b", "more than once")]
    public async Task Eligibility_RefusesABadList(string query, string detail)
    {
        await using var host = await StartAsync();
        using var response = await host.SendAsync(HttpMethod.Get, Route + query, As("admin"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(detail, (await Json(response)).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eligibility_TakesAHundredIds_NotMore()
    {
        var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();
        await using var host = await StartAsync();

        using var hundred = await host.SendAsync(HttpMethod.Get, Route + "?ids=" + string.Join(",", ids.Take(100)), As("admin"));
        using var more = await host.SendAsync(HttpMethod.Get, Route + "?ids=" + string.Join(",", ids), As("admin"));

        Assert.Equal(100, (await Json(hundred)).GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, more.StatusCode);
        Assert.Contains("100 items at most", (await Json(more)).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    private Task<WebHostHarness> StartAsync()
    {
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(Alice)).Returns(UserOf(Alice));
        users.Setup(u => u.GetUserById(Bob)).Returns(_w.Bob);
        return MoveApiHost.StartAsync(new TestConfiguration(new PluginConfiguration()), () => true, s =>
        {
            s.AddSingleton(_w.Library.Object);
            s.AddSingleton(users.Object);
            s.AddSingleton(_w.Access);
            s.AddSingleton(_w.Catalog);
        });
    }
}
