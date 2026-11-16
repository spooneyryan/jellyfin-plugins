using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.LibraryMaintenance.Api;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Compare;
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

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Api;

/// <summary>
/// <c>GET /LibraryMaintenance/Duplicates/Groups/{groupId}/Compare</c> through the registrator's services: administrators
/// only (anonymous 401, a user 403), the group as the caller's groups find it (an administrator limited to some libraries
/// gets 404 for a group of libraries they can't open), the options' 400, Duplicates turned off, and the body.
/// </summary>
public sealed class CompareApiTests
{
    private const string Groups = "/LibraryMaintenance/Duplicates/Groups";

    // Alice sees every library; Bob, also an administrator, only Movies.
    private static readonly Guid Alice = new("e0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = new("e0000000-0000-0000-0000-00000000000b");

    [Fact]
    public async Task TheComparison_AnswersAdministratorsOnly_AndNeverReadsAnItemForOthers()
    {
        var (library, _, _) = Library();
        var streams = new Mock<IMediaSourceManager>();
        await using var host = await StartAsync(library, streams);

        using var anonymous = await host.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef/Compare");
        using var user = await host.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef/Compare", As("user", Alice));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        Assert.Empty(library.IdQueries);
        streams.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnAdministrator_GetsTheGroupsCopies_ALimitedOne404ForAGroupOutsideTheirLibraries()
    {
        var (library, heat, more) = Library();
        var streams = new Mock<IMediaSourceManager>();
        streams.Setup(s => s.GetMediaStreams(heat.Id)).Returns(
        [
            new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = 3840, Height = 2160 },
            new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "eac3", Language = "eng", Channels = 6 },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, Codec = "subrip", Language = "eng", IsExternal = true, Path = @"C:\media\Movies\Heat (1995)\Heat (1995).en.srt" },
        ]);
        streams.Setup(s => s.GetMediaStreams(more.Id)).Returns([new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Width = 1920, Height = 1080 }]);
        await using var host = await StartAsync(library, streams);
        var comparison = (DuplicateComparison)host.Services.GetRequiredService<IDuplicateComparison>();
        comparison.Exists = (path, _) => path.StartsWith(@"C:\media\Movies", StringComparison.Ordinal);

        using var page = await host.SendAsync(HttpMethod.Get, Groups, As("admin", Alice));
        var groups = (await Json(page)).GetProperty("Items").EnumerateArray().ToList();
        var heatGroup = groups.Single(g => g.GetProperty("Key").GetString() == "heat|1995").GetProperty("GroupId").GetString();

        using var response = await host.SendAsync(HttpMethod.Get, Groups + "/" + heatGroup + "/Compare", As("admin", Alice));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(heatGroup, body.GetProperty("GroupId").GetString());
        var copies = body.GetProperty("Copies").EnumerateArray().ToList();
        Assert.Equal([heat.Id, more.Id], copies.Select(c => Guid.Parse(c.GetProperty("ItemId").GetString()!)));
        Assert.Equal(["Present", "Missing"], copies.Select(c => c.GetProperty("FileState").GetString()));
        Assert.Equal("2160p", copies[0].GetProperty("Video").GetProperty("ResolutionLabel").GetString());
        Assert.Equal("Heat (1995).en.srt", copies[0].GetProperty("Subtitles")[0].GetProperty("FileName").GetString());
        Assert.Contains(body.GetProperty("Best").EnumerateArray(), b => b.GetProperty("Field").GetString() == "Resolution" && Guid.Parse(b.GetProperty("ItemIds")[0].GetString()!) == heat.Id);

        // Bob can open Movies only: the cross-library Heat isn't one of his groups (as for an id that never was).
        using var bob = await host.SendAsync(HttpMethod.Get, Groups + "/" + heatGroup + "/Compare", As("admin", Bob));
        using var none = await host.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef/Compare", As("admin", Alice));
        Assert.Equal(HttpStatusCode.NotFound, bob.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Contains("gone", (await Json(none)).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOptionTwice_Is400_AndDuplicatesTurnedOff_Is404()
    {
        var (library, _, _) = Library();
        await using (var host = await StartAsync(library, new Mock<IMediaSourceManager>()))
        {
            using var twice = await host.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef/Compare?mode=Title&mode=Title", As("admin", Alice));
            using var bad = await host.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef/Compare?mode=Fuzzy", As("admin", Alice));
            Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        var (off, _, _) = Library();
        off.Configuration = new PluginConfiguration { IncludedLibraryIds = off.Configuration.IncludedLibraryIds, DuplicatesEnabled = false };
        await using var offHost = await StartAsync(off, new Mock<IMediaSourceManager>());
        using var turnedOff = await offHost.SendAsync(HttpMethod.Get, Groups + "/0123456789abcdef/Compare", As("admin", Alice));
        Assert.Equal(HttpStatusCode.NotFound, turnedOff.StatusCode);
        Assert.Contains("turned off", (await Json(turnedOff)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(off.IdQueries);
    }

    private static (DuplicatesLibrary Library, Movie Heat, Movie More) Library()
    {
        var library = new DuplicatesLibrary();
        var heat = library.Add("Heat", 1995);
        heat.Size = 20;
        var more = library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies);
        more.Size = 10;
        library.Add("Alien", 1979);
        library.Add("Alien", 1979, file: "Alien (1979) - 2.mkv");
        return (library, heat, more);
    }

    private static Action<HttpRequestMessage> As(string caller, Guid userId) => r =>
    {
        r.Headers.Add(JellyfinAuth.CallerHeader, caller);
        r.Headers.Add(JellyfinAuth.UserIdHeader, userId.ToString());
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();

    private static Task<WebHostHarness> StartAsync(DuplicatesLibrary library, Mock<IMediaSourceManager> streams)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, host.Object);

        var users = new Mock<IUserManager>();
        var alice = new User("alice", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Alice };
        var bob = new User("bob", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Bob };
        users.Setup(u => u.GetUserById(Alice)).Returns(alice);
        users.Setup(u => u.GetUserById(Bob)).Returns(bob);
#if JELLYFIN_12
        users.Setup(u => u.GetUsers()).Returns(new List<User> { alice, bob });
#else
        users.SetupGet(u => u.Users).Returns(new List<User> { alice, bob });
#endif
        var userData = new Mock<IUserDataManager>();
        var access = new LibraryAccess(library.Fake.Object, user => user is null || user.Id != Bob ? library.Libraries : [library.Libraries[0]], library.Fake.IsVisible);
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
                services.AddSingleton(streams.Object);
                services.AddSingleton(userData.Object);
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
            .WithMvc(typeof(LibraryMaintenanceDuplicatesController))
            .StartAsync();
    }
}
