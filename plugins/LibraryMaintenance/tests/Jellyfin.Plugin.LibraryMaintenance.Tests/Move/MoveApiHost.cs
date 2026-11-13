using Jellyfin.Plugin.Kit.TestSupport;
using System;
using System.Linq;
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
using Jellyfin.Plugin.LibraryMaintenance.Move;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// A web host with the registrator's services and the Move routes, over a test's settings and host OS; later tasks add
/// their world's services. Alice (an administrator) and Bob (an administrator) are known users; an API key has none.
/// </summary>
internal static class MoveApiHost
{
    public static readonly Guid Alice = new("e0000000-0000-0000-0000-00000000000a");
    public static readonly Guid Bob = new("e0000000-0000-0000-0000-00000000000b");

    public static Action<HttpRequestMessage> ApiKey => r => r.Headers.Add(JellyfinAuth.CallerHeader, "apikey");

    // A caller, Alice unless another user is named.
    public static Action<HttpRequestMessage> As(string caller, Guid? user = null) => r =>
    {
        r.Headers.Add(JellyfinAuth.CallerHeader, caller);
        r.Headers.Add(JellyfinAuth.UserIdHeader, (user ?? Alice).ToString());
    };

    public static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();

    public static User UserOf(Guid id) => new(id == Bob ? "bob" : "alice", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = id };

    public static Task<WebHostHarness> StartAsync(TestConfiguration configuration, Func<bool> isWindows, Action<IServiceCollection>? services = null, params Type[] extraControllers)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, host.Object);

        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(Alice)).Returns(UserOf(Alice));
        users.Setup(u => u.GetUserById(Bob)).Returns(UserOf(Bob));

        return new WebHostHarness()
            .WithServices(s =>
            {
                s.AddLogging();
                s.AddSingleton(host.Object);
                s.AddSingleton(users.Object);
                s.AddSingleton(new Mock<ILibraryManager>().Object);
                s.AddSingleton(new Mock<ISessionManager>().Object);
                s.AddSingleton(new Mock<IUserDataManager>().Object);
                s.AddSingleton(new Mock<IFileSystem>().Object);
                foreach (var descriptor in registered.Where(d => d.ServiceType != typeof(IPluginConfigurationProvider) && d.ServiceType != typeof(MoveFeature)))
                {
                    s.Add(descriptor);
                }

                s.AddSingleton<IPluginConfigurationProvider>(configuration);
                s.AddSingleton(new MoveFeature(configuration, isWindows));

                // A later registration wins: a test's world replaces the registrator's services here.
                services?.Invoke(s);

                // The server's wire format: PascalCase, nulls left out (Jellyfin.Server AddJellyfinApi, JsonDefaults, 10.11.5).
                s.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o =>
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
            .WithMvc([typeof(LibraryMaintenanceController), typeof(LibraryMaintenanceMoveController), .. extraControllers])
            .StartAsync();
    }
}
