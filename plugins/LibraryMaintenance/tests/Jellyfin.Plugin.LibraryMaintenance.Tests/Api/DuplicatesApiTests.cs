using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.LibraryMaintenance.Api;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Api;

/// <summary>
/// The index endpoints through the registrator's services: <c>GET /LibraryMaintenance/Duplicates/Index</c> and
/// <c>POST …/Index/Rebuild</c>, for administrators only, 404 with Duplicates turned off, and the index in Diagnostics.
/// </summary>
public sealed class DuplicatesApiTests
{
    private const string Index = "/LibraryMaintenance/Duplicates/Index";
    private const string Rebuild = "/LibraryMaintenance/Duplicates/Index/Rebuild";

    private static readonly Guid Alice = new("e0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = new("e0000000-0000-0000-0000-00000000000b");

    [Fact]
    public async Task TheIndex_AnswersAdministratorsOnly()
    {
        var library = new DuplicatesLibrary();
        await using var host = await StartAsync(library);

        foreach (var (method, path) in new[] { (HttpMethod.Get, Index), (HttpMethod.Post, Rebuild) })
        {
            using var anonymous = await host.SendAsync(method, path);
            using var user = await host.SendAsync(method, path, As("user"));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        }

        // Neither a user's GET nor their POST started a build.
        Assert.Empty(library.IdQueries);
    }

    [Fact]
    public async Task ARebuild_IsAccepted_ThenTheIndexHasTheMovies()
    {
        var library = new DuplicatesLibrary();
        var a = library.Add("Blade Runner", 1982);
        var b = library.Add("Blade Runner", 1982, file: "b.mkv");
        library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies);
        DuplicatesLibrary.MergeVersions(a, b);
        await using var host = await StartAsync(library);

        using var before = await host.SendAsync(HttpMethod.Get, Index, As("admin"));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(0, (await Json(before)).GetProperty("Version").GetInt64());

        using var rebuild = await host.SendAsync(HttpMethod.Post, Rebuild, As("admin"));
        Assert.Equal(HttpStatusCode.Accepted, rebuild.StatusCode);
        await host.Services.GetRequiredService<MovieIndexService>().Rebuild().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using var after = await host.SendAsync(HttpMethod.Get, Index, As("admin"));
        var body = await Json(after);
        Assert.True(body.GetProperty("Version").GetInt64() >= 1);
        Assert.Equal(3, body.GetProperty("Movies").GetInt32());
        Assert.Equal(2, body.GetProperty("Entries").GetInt32());
        Assert.Equal(1, body.GetProperty("MergedEntries").GetInt32());
        Assert.Equal(new[] { "Movies", "More Movies" }, body.GetProperty("Libraries").EnumerateArray().Select(l => l.GetProperty("Name").GetString()));
        Assert.Equal(0, body.GetProperty("States").GetProperty("Orphaned").GetInt32());
        Assert.False(body.GetProperty("Rebuilding").GetBoolean());

        // The Diagnostics entry (the build's counts and cost).
        using var diagnostics = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Admin/Diagnostics", As("admin"));
        var index = (await Json(diagnostics)).GetProperty("DuplicatesIndex");
        Assert.Equal(3, index.GetProperty("Movies").GetInt32());
        Assert.True(index.GetProperty("PeakWorkingSetMB").GetDouble() > 0);
    }

    [Fact]
    public async Task TheIndex_CountsOnlyTheLibrariesTheCallerCanOpen_AndDiagnosticsHideTheServerWideNumbers()
    {
        // The counts are the caller's libraries'. Movies: Blade Runner merged (2 movies, 1 entry). More Movies: Heat. Across both: Alien, one
        // version in each, merged. Bob, an administrator who can open only Movies, counts Movies' movies and one Alien.
        var library = new DuplicatesLibrary();
        DuplicatesLibrary.MergeVersions(library.Add("Blade Runner", 1982), library.Add("Blade Runner", 1982, file: "b.mkv"));
        library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies);
        DuplicatesLibrary.MergeVersions(library.Add("Alien", 1979), library.Add("Alien", 1979, DuplicatesLibrary.MoreMovies));
        await using var host = await StartAsync(library);
        await host.Services.GetRequiredService<MovieIndexService>().Rebuild().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using var alice = await host.SendAsync(HttpMethod.Get, Index, As("admin"));
        using var bob = await host.SendAsync(HttpMethod.Get, Index, As("admin", Bob));
        using var key = await host.SendAsync(HttpMethod.Get, Index, r => r.Headers.Add(JellyfinAuth.CallerHeader, "apikey"));
        var all = await Json(alice);
        var movies = await Json(bob);
        Assert.Equal((5, 3, 2), (all.GetProperty("Movies").GetInt32(), all.GetProperty("Entries").GetInt32(), all.GetProperty("MergedEntries").GetInt32()));
        Assert.Equal(new[] { "Movies", "More Movies" }, all.GetProperty("Libraries").EnumerateArray().Select(l => l.GetProperty("Name").GetString()));
        Assert.Equal(all.GetProperty("Movies").GetInt32(), (await Json(key)).GetProperty("Movies").GetInt32());
        Assert.Equal((3, 2, 1), (movies.GetProperty("Movies").GetInt32(), movies.GetProperty("Entries").GetInt32(), movies.GetProperty("MergedEntries").GetInt32()));
        Assert.Equal("Movies", Assert.Single(movies.GetProperty("Libraries").EnumerateArray()).GetProperty("Name").GetString());
        Assert.DoesNotContain("More Movies", movies.GetRawText(), StringComparison.Ordinal);

        // A rebuild's answer is counted the same way.
        using var rebuild = await host.SendAsync(HttpMethod.Post, Rebuild, As("admin", Bob));
        Assert.Equal(HttpStatusCode.Accepted, rebuild.StatusCode);
        Assert.Single((await Json(rebuild)).GetProperty("Libraries").EnumerateArray());

        // Diagnostics: Alice sees the server-wide entries, labelled; Bob a note in their place.
        using var aliceDiagnostics = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Admin/Diagnostics", As("admin"));
        using var bobDiagnostics = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Admin/Diagnostics", As("admin", Bob));
        var shown = await Json(aliceDiagnostics);
        var hidden = await Json(bobDiagnostics);
        Assert.Equal(5, shown.GetProperty("DuplicatesIndex").GetProperty("Movies").GetInt32());
        Assert.StartsWith("Server-wide", shown.GetProperty("DuplicatesIndex").GetProperty("Scope").GetString(), StringComparison.Ordinal);
        foreach (var source in LibraryMaintenanceAdminController.ServerWideSources)
        {
            var note = hidden.GetProperty(source);
            Assert.StartsWith("Hidden", Assert.Single(note.EnumerateObject()).Value.GetString(), StringComparison.Ordinal);
        }

        Assert.Equal(shown.GetProperty("PluginVersion").GetString(), hidden.GetProperty("PluginVersion").GetString());
    }

    [Fact]
    public async Task WithDuplicatesTurnedOff_TheEndpointsAre404()
    {
        var library = new DuplicatesLibrary { Configuration = new PluginConfiguration { DuplicatesEnabled = false } };
        await using var host = await StartAsync(library);

        using var index = await host.SendAsync(HttpMethod.Get, Index, As("admin"));
        using var rebuild = await host.SendAsync(HttpMethod.Post, Rebuild, As("admin"));

        Assert.Equal(HttpStatusCode.NotFound, index.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rebuild.StatusCode);
        Assert.Contains("turned off", (await Json(index)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(library.IdQueries);
    }

    // A caller, Alice unless another user is named.
    private static Action<HttpRequestMessage> As(string caller, Guid? user = null) => r =>
    {
        r.Headers.Add(JellyfinAuth.CallerHeader, caller);
        r.Headers.Add(JellyfinAuth.UserIdHeader, (user ?? Alice).ToString());
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();

    private static Task<WebHostHarness> StartAsync(DuplicatesLibrary library)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, host.Object);

        // Alice can open every library, Bob only Movies (admins get no bypass); an API key every one.
        var users = new Mock<MediaBrowser.Controller.Library.IUserManager>();
        users.Setup(u => u.GetUserById(Alice)).Returns(new User("alice", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Alice });
        users.Setup(u => u.GetUserById(Bob)).Returns(new User("bob", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Bob });
        var access = new LibraryAccess(library.Fake.Object, user => user is null || user.Id == Alice ? library.Libraries : [library.Libraries[0]]);

        return new WebHostHarness()
            .WithServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(library.Fake.Object);

                // The kit's library-change monitor is a hosted service, and takes the server; the controller takes
                // the user manager, for the groups' library access (GroupsApiTests).
                services.AddSingleton(host.Object);
                services.AddSingleton(users.Object);

                // The registrator's descriptors, with the test library's settings and libraries, and its library access.
                foreach (var descriptor in registered.Where(d => d.ServiceType != typeof(IPluginConfigurationProvider) && d.ServiceType != typeof(MovieIndexBuilder) && d.ServiceType != typeof(LibraryAccess)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(library.ConfigurationProvider);
                services.AddSingleton(library.Builder());
                services.AddSingleton(access);

                // The server's wire format: PascalCase, nulls kept (Jellyfin.Server AddJellyfinApi, 10.11.5).
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
