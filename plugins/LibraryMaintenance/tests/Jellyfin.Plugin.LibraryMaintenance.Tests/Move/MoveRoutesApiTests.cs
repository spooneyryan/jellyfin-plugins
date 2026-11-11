using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Api;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;
using static Jellyfin.Plugin.LibraryMaintenance.Tests.Move.MoveApiHost;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// The gate on every real route of <see cref="LibraryMaintenanceMoveController"/>, found by reflection so a new route
/// can't skip it: a user who isn't an administrator gets 403, Move off gets the kit's <c>feature-disabled</c> 403, and a
/// host that isn't Windows gets 503 <c>move.unsupported-os</c>, before any body is read.
/// </summary>
public sealed class MoveRoutesApiTests
{
    public static TheoryData<string, string> Routes()
    {
        var data = new TheoryData<string, string>();
        foreach (var (method, path) in Discover())
        {
            data.Add(method, path);
        }

        return data;
    }

    [Fact]
    public void TheRoutes_AreFound()
        => Assert.Contains(("GET", "/LibraryMaintenance/Move/Eligibility"), Discover());

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_IsAdminOnly_ThenFeatureDisabled_ThenTheHost(string method, string path)
    {
        var configuration = new TestConfiguration(new PluginConfiguration());
        await using var windows = await StartAsync(configuration, () => true);
        await using var other = await StartAsync(configuration, () => false);

        using (var viewer = await windows.SendAsync(new HttpMethod(method), path, Caller("user")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
            Assert.DoesNotContain("feature-disabled", await viewer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (var unsupported = await other.SendAsync(new HttpMethod(method), path, Caller("admin")))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unsupported.StatusCode);
            Assert.Equal(MoveErrorCodes.UnsupportedOs, (await Json(unsupported)).GetProperty("code").GetString());
        }

        configuration.Configuration.MoveEnabled = false;
        using (var off = await windows.SendAsync(new HttpMethod(method), path, Caller("admin")))
        using (var key = await windows.SendAsync(new HttpMethod(method), path, Caller("apikey")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
            Assert.Equal(KitProblemDetails.FeatureDisabledType, (await Json(off)).GetProperty("type").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, key.StatusCode);
        }
    }

    // Each action's method and path, route values filled with an id; a body that isn't JSON, which the gate must not read.
    private static IReadOnlyList<(string Method, string Path)> Discover()
    {
        var prefix = typeof(LibraryMaintenanceMoveController).GetCustomAttribute<RouteAttribute>()!.Template;
        var routes = new List<(string, string)>();
        foreach (var action in typeof(LibraryMaintenanceMoveController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (var verb in action.GetCustomAttributes<HttpMethodAttribute>())
            {
                var template = string.IsNullOrEmpty(verb.Template) ? prefix : prefix + "/" + verb.Template;
                var path = "/" + string.Join("/", template.Split('/').Select(s => s.StartsWith('{') ? "a0000000000000000000000000000001" : s));
                routes.AddRange(verb.HttpMethods.Select(m => (m, path)));
            }
        }

        return routes;
    }

    private static Action<HttpRequestMessage> Caller(string caller) => r =>
    {
        (caller == "apikey" ? ApiKey : As(caller))(r);
        if (r.Method != HttpMethod.Get)
        {
            r.Content = new StringContent("{ not json", Encoding.UTF8, "application/json");
        }
    };
}
