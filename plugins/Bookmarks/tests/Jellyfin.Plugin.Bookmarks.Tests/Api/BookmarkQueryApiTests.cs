using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// The My Bookmarks page's endpoints on the wire: <c>GET /Bookmarks/Query</c> and <c>/Groups</c> as
/// Jellyfin's <c>QueryResult</c> with Jellyfin's JSON options, the query string's values (a list comma-separated or repeated,
/// each endpoint's own sorts, a 400 for anything else), <c>ItemAvailable</c> following the caller's access at every
/// request, <c>POST /Bookmarks/BulkDelete</c>, <c>GET</c> and <c>POST /Bookmarks/Preferences</c>, API keys refused, and what
/// ASP.NET Core's API explorer, which Jellyfin's Swashbuckle reads, says about the new actions. The plugin's own
/// registrations behind Jellyfin's web pipeline; Jellyfin's services are mocks.
/// </summary>
public sealed class BookmarkQueryApiTests : IAsyncLifetime
{
    private static readonly Guid AliceId = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid BobId = new("ff71b896-f205-45ff-936a-c2393f6091f6");
    private static readonly Guid HeatId = new("ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5");
    private static readonly Guid AlienId = new("4d7a2c9e-5b31-4f6e-8a2d-9c0b1e3f5a77");

    private readonly TempDirectory _data = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");

    // Which user may open which video: the library mock answers GetItemById<Video>(id, user) from it, on every call.
    private readonly ConcurrentDictionary<(Guid Item, Guid User), bool> _access = new();

    public BookmarkQueryApiTests()
    {
        _alice.Id = AliceId;
        _bob.Id = BobId;
        foreach (var item in new[] { HeatId, AlienId })
        {
            _access[(item, AliceId)] = true;
            _access[(item, BobId)] = true;
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QueryAndGroups_AreJellyfinsQueryResult_WithItemAvailable_AndNoPath()
    {
        await using var host = await StartAsync();
        var first = await CreateAsync(host, AliceId, HeatId, 60, "Bank job");
        await CreateAsync(host, AliceId, HeatId, 120, null);
        await CreateAsync(host, AliceId, AlienId, 30, null);
        await CreateAsync(host, BobId, HeatId, 10, "Bob's");

        var (status, query) = await SendAsync(host, "GET", "/Bookmarks/Query", AliceId);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Items", "TotalRecordCount", "StartIndex"], query.EnumerateObject().Select(p => p.Name));
        Assert.Equal(3, query.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(0, query.GetProperty("StartIndex").GetInt32());
        var rows = query.GetProperty("Items").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.True(row.GetProperty("ItemAvailable").GetBoolean());
            Assert.Equal("Native", row.GetProperty("Origin").GetString());
            Assert.Equal(1995, row.GetProperty("Year").GetInt32());
            Assert.False(row.TryGetProperty("Identity", out _));
            Assert.DoesNotContain(@"C:\\media", row.GetRawText(), StringComparison.OrdinalIgnoreCase);
        });

        (status, var groups) = await SendAsync(host, "GET", "/Bookmarks/Groups", AliceId);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, groups.GetProperty("TotalRecordCount").GetInt32());
        var heat = groups.GetProperty("Items").EnumerateArray().Single(g => Id(g.GetProperty("ItemId")) == HeatId);
        Assert.Equal(2, heat.GetProperty("Count").GetInt32());
        Assert.True(heat.GetProperty("ItemAvailable").GetBoolean());
        Assert.False(heat.GetProperty("BookmarksTruncated").GetBoolean());
        Assert.Equal("Heat (1995)", heat.GetProperty("ItemName").GetString());
        Assert.Equal([60 * TestData.OneSecond, 120 * TestData.OneSecond], heat.GetProperty("Bookmarks").EnumerateArray().Select(b => b.GetProperty("StartTicks").GetInt64()));

        // Every other route leaves ItemAvailable out (null), as 1.0 did.
        (_, var one) = await SendAsync(host, "GET", $"/Bookmarks/{first}", AliceId);
        Assert.False(one.TryGetProperty("ItemAvailable", out _));
        Assert.Equal("Native", one.GetProperty("Origin").GetString());
    }

    [Fact]
    public async Task ItemTypes_BindCommaSeparatedOrRepeated_AndAValueThatIsNoneOfThemIs400()
    {
        await using var host = await StartAsync();
        await CreateAsync(host, AliceId, HeatId, 60, null);

        foreach (var query in new[] { "itemTypes=Movie,Episode", "itemTypes=movie&itemTypes=Episode", "itemTypes=Other,%20Movie", "itemTypes=Movie" })
        {
            var (status, body) = await SendAsync(host, "GET", "/Bookmarks/Query?" + query, AliceId);
            Assert.True(status == HttpStatusCode.OK, query);
            Assert.Equal(1, body.GetProperty("TotalRecordCount").GetInt32());
        }

        Assert.Equal(0, (await SendAsync(host, "GET", "/Bookmarks/Query?itemTypes=Episode,Other", AliceId)).Body.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(0, (await SendAsync(host, "GET", "/Bookmarks/Groups?itemTypes=Episode", AliceId)).Body.GetProperty("TotalRecordCount").GetInt32());

        // MVC's own validation answer ([ApiController]): ProblemDetails with the parameter's error, sent as JSON (the
        // controller's [Produces]), as Jellyfin's own controllers send theirs.
        using var bad = await SendRawAsync(host, "GET", "/Bookmarks/Query?itemTypes=Movie,Show", AliceId);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problem = await ValidationProblemAsync(bad);
        Assert.Equal(["The value 'Show' is not one of Movie, Episode, Other."], problem.GetProperty("errors").GetProperty("ItemTypes").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task EachEndpoint_TakesOnlyItsOwnSorts_AndEveryOtherValueIs400()
    {
        // Groups refuses CreatedAt, Name and Position; Query refuses Count (and LatestUpdated).
        await using var host = await StartAsync();
        await CreateAsync(host, AliceId, HeatId, 60, null);

        foreach (var sort in new[] { "UpdatedAt", "CreatedAt", "Name", "ItemName", "Position", "position" })
        {
            Assert.True((await SendRawAsync(host, "GET", "/Bookmarks/Query?sortBy=" + sort, AliceId)).StatusCode == HttpStatusCode.OK, sort);
        }

        foreach (var sort in new[] { "LatestUpdated", "ItemName", "Count" })
        {
            Assert.True((await SendRawAsync(host, "GET", "/Bookmarks/Groups?sortBy=" + sort, AliceId)).StatusCode == HttpStatusCode.OK, sort);
        }

        foreach (var path in new[]
        {
            "/Bookmarks/Query?sortBy=Count", "/Bookmarks/Query?sortBy=LatestUpdated", "/Bookmarks/Groups?sortBy=CreatedAt",
            "/Bookmarks/Groups?sortBy=Name", "/Bookmarks/Groups?sortBy=Position", "/Bookmarks/Groups?sortBy=UpdatedAt",
            "/Bookmarks/Query?sortOrder=Sideways", "/Bookmarks/Query?availability=Gone", "/Bookmarks/Query?sortBy=99",
            "/Bookmarks/Query?hasClip=perhaps", "/Bookmarks/Groups?itemId=not-a-guid",

            // A key under the filter's parameter name binds the whole filter under that prefix.
            "/Bookmarks/Query?filter.sortOrder=Bogus", "/Bookmarks/Query?filter.availability=Bogus",
            "/Bookmarks/Groups?filter.limit=2&filter.availability=Bogus", "/Bookmarks/Query?filter.itemTypes=Movie,Bogus"
        })
        {
            using var response = await SendRawAsync(host, "GET", path, AliceId);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, path);
            Assert.NotEmpty((await ValidationProblemAsync(response)).GetProperty("errors").EnumerateObject());
        }

        Assert.Equal(HttpStatusCode.OK, (await SendRawAsync(host, "GET", "/Bookmarks/Groups?sortBy=Count&sortOrder=Ascending&availability=Missing&hasRange=false&named=false", AliceId)).StatusCode);
    }

    [Fact]
    public async Task OutOfRangeValues_Are400ProblemDetails_WithTheRule()
    {
        await using var host = await StartAsync();

        foreach (var (path, detail) in new[]
        {
            ("/Bookmarks/Query?limit=201", "The limit must be between 1 and 200."),
            ("/Bookmarks/Groups?limit=0", "The limit must be between 1 and 200."),
            ("/Bookmarks/Query?startIndex=-1", "The start index can't be negative."),
            ("/Bookmarks/Groups?searchTerm=" + new string('x', 101), "Search terms can be at most 100 characters long."),

            // Under the filter's prefix the whole filter binds from it, the rule the same.
            ("/Bookmarks/Query?filter.limit=201&limit=5", "The limit must be between 1 and 200.")
        })
        {
            using var response = await SendRawAsync(host, "GET", path, AliceId);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, path);
            Assert.Equal($$"""{"title":"Bad Request","status":400,"detail":"{{detail}}"}""", await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task ItemAvailable_FollowsTheCallersAccess_AtEveryRequest()
    {
        // Jellyfin's own check as the caller, never kept across requests.
        await using var host = await StartAsync();
        await CreateAsync(host, AliceId, HeatId, 60, null);
        await CreateAsync(host, AliceId, AlienId, 60, null);

        Assert.Equal(new[] { true, true }, await AvailableAsync(host));

        _access[(HeatId, AliceId)] = false;
        Assert.Equal(new[] { false, true }, await AvailableAsync(host));
        var groups = (await SendAsync(host, "GET", "/Bookmarks/Groups?sortBy=ItemName", AliceId)).Body.GetProperty("Items").EnumerateArray().ToList();
        Assert.Equal([true, false], groups.Select(g => g.GetProperty("ItemAvailable").GetBoolean()));

        // Bob's access doesn't change Alice's answer, and the other way round.
        _access[(HeatId, AliceId)] = true;
        _access[(AlienId, BobId)] = false;
        Assert.Equal(new[] { true, true }, await AvailableAsync(host));
    }

    [Fact]
    public async Task BulkDelete_AnswersForEachId_AndOtherUsersBookmarksAreNotFound()
    {
        await using var host = await StartAsync();
        var one = await CreateAsync(host, AliceId, HeatId, 60, null);
        var two = await CreateAsync(host, AliceId, AlienId, 60, null);
        var bobs = await CreateAsync(host, BobId, HeatId, 60, null);
        var unknown = Guid.NewGuid();

        var (status, result) = await SendAsync(host, "POST", "/Bookmarks/BulkDelete", AliceId, $$"""{"Ids":["{{one}}","{{bobs}}","{{two}}","{{unknown}}"],"DeleteClips":false}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Deleted", "Tombstoned", "NotFound", "InUse", "Failed"], result.EnumerateObject().Select(p => p.Name));
        Assert.Equal([one, two], result.GetProperty("Deleted").EnumerateArray().Select(Id));
        Assert.Equal([bobs, unknown], result.GetProperty("NotFound").EnumerateArray().Select(Id));
        Assert.Empty(result.GetProperty("Tombstoned").EnumerateArray());
        Assert.Equal(0, (await SendAsync(host, "GET", "/Bookmarks/Query", AliceId)).Body.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(1, (await SendAsync(host, "GET", "/Bookmarks/Query", BobId)).Body.GetProperty("TotalRecordCount").GetInt32());

        using var none = await SendRawAsync(host, "POST", "/Bookmarks/BulkDelete", AliceId, """{"Ids":[]}""");
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("""{"title":"Bad Request","status":400,"detail":"Choose at least one bookmark to delete."}""", await none.Content.ReadAsStringAsync(Ct));
        var tooMany = "{\"Ids\":[" + string.Join(',', Enumerable.Range(0, 201).Select(_ => $"\"{Guid.NewGuid()}\"")) + "]}";
        using var over = await SendRawAsync(host, "POST", "/Bookmarks/BulkDelete", AliceId, tooMany);
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Contains("At most 200 bookmarks can be deleted at once.", await over.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preferences_RoundTrip_PerUser_AndTheCapabilitiesFollow()
    {
        await using var host = await StartAsync();

        using (var empty = await SendRawAsync(host, "GET", "/Bookmarks/Preferences", AliceId))
        {
            Assert.Equal("""{"PauseWhileNaming":false}""", await empty.Content.ReadAsStringAsync(Ct));
        }

        using (var saved = await SendRawAsync(host, "POST", "/Bookmarks/Preferences", AliceId, """{"AskForName":true}"""))
        {
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
            Assert.Empty(await saved.Content.ReadAsByteArrayAsync(Ct));
        }

        using (var read = await SendRawAsync(host, "GET", "/Bookmarks/Preferences", AliceId))
        {
            Assert.Equal("""{"AskForName":true,"PauseWhileNaming":false}""", await read.Content.ReadAsStringAsync(Ct));
        }

        var (_, capabilities) = await SendAsync(host, "GET", "/Bookmarks/Capabilities", AliceId);
        Assert.True(capabilities.GetProperty("AskForName").GetBoolean());
        Assert.False(capabilities.GetProperty("AskForNameIsDefault").GetBoolean());
        Assert.False(capabilities.GetProperty("AskForNameDefault").GetBoolean(), "the server's default, next to Alice's own choice");
        Assert.Equal(100, capabilities.GetProperty("MaxNameLength").GetInt32());
        Assert.Equal(500, capabilities.GetProperty("MaxNoteLength").GetInt32());
        Assert.Equal("""{"BookmarkNow":"Shift+B"}""", capabilities.GetProperty("Shortcuts").GetRawText());
        Assert.Equal("[]", capabilities.GetProperty("ReservedShortcuts").GetRawText());
        // The My Bookmarks page is there since 1.1.0 and My Clips since 1.2.0, both on by default.
        Assert.Equal("""{"Bookmarks":true,"Clips":true}""", capabilities.GetProperty("Pages").GetRawText());

        // Bob's are his own.
        using (var bobs = await SendRawAsync(host, "GET", "/Bookmarks/Preferences", BobId))
        {
            Assert.Equal("""{"PauseWhileNaming":false}""", await bobs.Content.ReadAsStringAsync(Ct));
        }

        Assert.True((await SendAsync(host, "GET", "/Bookmarks/Capabilities", BobId)).Body.GetProperty("AskForNameIsDefault").GetBoolean());

        using var noBody = await SendRawAsync(host, "POST", "/Bookmarks/Preferences", AliceId, string.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/Bookmarks/Query", null)]
    [InlineData("GET", "/Bookmarks/Groups", null)]
    [InlineData("POST", "/Bookmarks/BulkDelete", """{"Ids":["9fa5c677-8cf7-4a94-bff0-c82a60581ccf"]}""")]
    [InlineData("GET", "/Bookmarks/Preferences", null)]
    [InlineData("POST", "/Bookmarks/Preferences", """{"AskForName":true}""")]
    public async Task ApiKeys_Get403_WithTheBookmarks10Message(string method, string path, string? body)
    {
        await using var host = await StartAsync();

        using var response = await host.SendAsync(new HttpMethod(method), path, request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "apikey");
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(BookmarksController.NoUserMessage, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheApiExplorer_DescribesEveryFilterAsAQueryParameter_AndTheBodies()
    {
        // What Jellyfin's Swashbuckle documents in /api-docs/openapi.json comes from ASP.NET Core's API explorer: every filter
        // a query parameter (itemTypes too, whose binder reads the comma-separated form), each endpoint's own sortBy.
        await using var host = await StartAsync();
        var descriptions = host.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .ToList();

        ApiDescription Find(string method, string route) => Assert.Single(descriptions, d => d.HttpMethod == method && d.RelativePath == route);

        var query = Find("GET", "Bookmarks/Query");
        string[] filters = ["SearchTerm", "ItemId", "ItemTypes", "HasRange", "HasClip", "Named", "Availability", "SortOrder", "StartIndex", "Limit"];
        Assert.Equal([.. filters, "sortBy"], query.ParameterDescriptions.Select(p => p.Name));
        Assert.All(query.ParameterDescriptions, p => Assert.Equal(BindingSource.Query, p.Source));
        Assert.Equal(typeof(IReadOnlyList<BookmarkItemType>), query.ParameterDescriptions.Single(p => p.Name == "ItemTypes").Type);
        // An enum's description: its model type, which Jellyfin's Swashbuckle (6.2.3) builds the schema from, a reference
        // to the enum. Its Type is the enum on ASP.NET Core 10 but string on 9, the runtime Jellyfin 10.11 runs on.
        Assert.Equal(typeof(BookmarkSortBy?), query.ParameterDescriptions.Single(p => p.Name == "sortBy").ModelMetadata?.ModelType);

        var groups = Find("GET", "Bookmarks/Groups");
        Assert.Equal([.. filters, "sortBy"], groups.ParameterDescriptions.Select(p => p.Name));
        Assert.Equal(typeof(BookmarkGroupSortBy?), groups.ParameterDescriptions.Single(p => p.Name == "sortBy").ModelMetadata?.ModelType);

        Assert.Equal(typeof(BookmarkBulkDeleteRequest), Assert.Single(Find("POST", "Bookmarks/BulkDelete").ParameterDescriptions, p => p.Source == BindingSource.Body).Type);
        Assert.Equal(typeof(BookmarkPreferencesDto), Assert.Single(Find("POST", "Bookmarks/Preferences").ParameterDescriptions, p => p.Source == BindingSource.Body).Type);
        Assert.Contains(Find("GET", "Bookmarks/Preferences").SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(BookmarkPreferencesDto));
        Assert.Contains(query.SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(MediaBrowser.Model.Querying.QueryResult<BookmarkDto>));
        Assert.Contains(groups.SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(MediaBrowser.Model.Querying.QueryResult<BookmarkGroupDto>));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<JsonElement> ValidationProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var problem = document.RootElement.Clone();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("One or more validation errors occurred.", problem.GetProperty("title").GetString());
        return problem;
    }

    // Jellyfin's JSON options write GUIDs without dashes ("N"), which JsonElement.GetGuid doesn't read.
    private static Guid Id(JsonElement element) => Guid.Parse(element.GetString()!);

    private static async Task<bool[]> AvailableAsync(WebHostHarness host)
        => (await SendAsync(host, "GET", "/Bookmarks/Query?sortBy=ItemName&sortOrder=Descending", AliceId)).Body.GetProperty("Items").EnumerateArray()
            .Select(row => row.GetProperty("ItemAvailable").GetBoolean())
            .ToArray();

    private static async Task<Guid> CreateAsync(WebHostHarness host, Guid userId, Guid itemId, int seconds, string? name)
    {
        var body = JsonSerializer.Serialize(new { ItemId = itemId, StartTicks = seconds * TestData.OneSecond, Name = name });
        var (status, created) = await SendAsync(host, "POST", "/Bookmarks", userId, body);
        Assert.Equal(HttpStatusCode.Created, status);
        return Id(created.GetProperty("Id"));
    }

    private static Task<HttpResponseMessage> SendRawAsync(WebHostHarness host, string method, string path, Guid userId, string? json = null)
        => host.SendAsync(new HttpMethod(method), path, request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "user");
            request.Headers.Add(JellyfinAuth.UserIdHeader, userId.ToString("N"));
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(WebHostHarness host, string method, string path, Guid userId, string? json = null)
    {
        using var response = await SendRawAsync(host, method, path, userId, json);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return (response.StatusCode, document.RootElement.Clone());
    }

    private Task<WebHostHarness> StartAsync()
    {
        // The plugin's own registrations for everything this touches; Jellyfin's services are mocks, ffmpeg is fake.
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        applicationHost.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        Type[] own =
        [
            typeof(KitOptions), typeof(KitAbiStatus), typeof(KitPaths), typeof(KitPageHosts), typeof(ClientHealthRegistry), typeof(KitDiagnostics),
            typeof(IKitDiagnostics), typeof(IKitDiagnosticsSource), typeof(BookmarkPaths), typeof(IBookmarkStore), typeof(BackgroundWriteGate),
            typeof(ClipExtractionService), typeof(IClipExtractionService), typeof(ClipFileOps), typeof(ClipStateCache), typeof(PartialFileJanitor), typeof(IBookmarkManager),
            typeof(IBookmarkQuery), typeof(IBookmarkPreferences), typeof(ClipStorageGuard), typeof(IDriveSpaceProbe),
            .. TestData.LibraryServiceTypes
        ];

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_data.Path);
        var loggers = new Mock<ILoggerFactory>();
        loggers.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUserById(AliceId)).Returns(_alice);
        users.Setup(m => m.GetUserById(BobId)).Returns(_bob);
        var movies = new Dictionary<Guid, Video>
        {
            [HeatId] = new MediaBrowser.Controller.Entities.Movies.Movie { Id = HeatId, Name = "Heat", ProductionYear = 1995, RunTimeTicks = 3000 * TestData.OneSecond, Path = @"C:\media\Heat (1995).mkv" },
            [AlienId] = new MediaBrowser.Controller.Entities.Movies.Movie { Id = AlienId, Name = "Alien", ProductionYear = 1995, RunTimeTicks = 3000 * TestData.OneSecond, Path = @"C:\media\Alien.mkv" },
        };
        var library = new Mock<ILibraryManager>();
        library.Setup(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()))
            .Returns((Guid id, User? user) => user is not null && _access.TryGetValue((id, user.Id), out var open) && open ? movies.GetValueOrDefault(id) : null);

        return new WebHostHarness()
            .WithServices(services =>
            {
                foreach (var descriptor in registered.Where(d => own.Contains(d.ServiceType)
                    || (d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(applicationHost.Object);
                services.AddSingleton(paths.Object);
                services.AddSingleton(loggers.Object);
                services.AddSingleton(users.Object);
                services.AddSingleton(library.Object);
                services.AddSingleton(TestData.ConfigurationProvider(new PluginConfiguration()));
                services.AddSingleton<IClipPlanner>(new FakeClipPlanner(_data));
                services.AddSingleton<IFfmpegRunner>(new FakeFfmpegRunner());
                services.AddSingleton(Mock.Of<IEmbeddedScript>());
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
            .WithMvc(typeof(BookmarksController))
            .StartAsync();
    }
}
