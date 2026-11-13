using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Api;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// Access Roles' three controllers over HTTP in the kit's harness, registered by the plugin's own registrator:
/// the anonymous ClientScript and its tag in <c>index.html</c>, the user-only ClientHealth reports and the admin-only
/// Diagnostics.
/// </summary>
public sealed class ControllerTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    [Fact]
    public async Task TheClientScript_IsAnonymousJavaScript_WithTheMvidAsETag()
    {
        await using var harness = await StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/AccessRoles/ClientScript?v=" + JellyfinLine.VersionOf(typeof(Plugin)) + "-00000000");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var current = await harness.SendAsync(HttpMethod.Get, "/AccessRoles/ClientScript", r => r.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag!.Tag));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("\"" + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N") + "\"", response.Headers.ETag?.Tag);
        Assert.Contains("var KIT_BUILD = {\"plugin\":\"Access Roles\",\"prefix\":\"jfar\",\"route\":\"AccessRoles\",\"version\":\"" + JellyfinLine.VersionOf(typeof(Plugin)) + "\",\"kit\":\"" + KitBuild.KitVersion + "\"", text, StringComparison.Ordinal);
        Assert.Contains(" 9 plugin file(s) of Web/js. ", text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotModified, current.StatusCode);
    }

    [Fact]
    public async Task TheAdminScript_IsAnonymousJavaScript_TheDashboardModule_WithTheMvidAsETag()
    {
        // The dashboard page's module, static and anonymous (it holds no data; every value comes from the
        // RequiresElevation admin API), revalidated by the MVID like the ClientScript.
        await using var harness = await StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/AccessRoles/AdminScript");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var current = await harness.SendAsync(HttpMethod.Get, "/AccessRoles/AdminScript", r => r.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag!.Tag));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("\"" + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N") + "\"", response.Headers.ETag?.Tag);
        Assert.StartsWith("/* Access Roles " + JellyfinLine.VersionOf(typeof(Plugin)) + " dashboard module, kit ", text, StringComparison.Ordinal);
        Assert.Contains(" 14 plugin file(s) of Web/admin/js. ", text, StringComparison.Ordinal);
        Assert.EndsWith("export { mount };\n", text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotModified, current.StatusCode);
    }

    [Fact]
    public async Task IndexHtml_LoadsTheClientScript_OnceAndOnlyWhileTheWebUiIsOn()
    {
        var configuration = new PluginConfiguration();
        await using var harness = await StartAsync(configuration);

        var on = await harness.GetStringAsync("/web/index.html");
        configuration.EnableWebUi = false;
        var off = await harness.GetStringAsync("/web/index.html");

        Assert.Equal(1, Count(on, "data-jfar-plugin=\"" + JellyfinLine.VersionOf(typeof(Plugin)) + "\""));
        Assert.Contains("src=\"../AccessRoles/ClientScript?v=" + JellyfinLine.VersionOf(typeof(Plugin)) + "-", on, StringComparison.Ordinal);
        Assert.DoesNotContain("data-jfar", off, StringComparison.Ordinal);

        // The web guard's head fragment, once, right before </head>: inline, so it runs while the page is parsed, before jellyfin-web's
        // deferred bundles (the webchecks check the order in the browser).
        Assert.Equal(1, Count(on, "<style data-jfar-head>"));
        Assert.Equal(1, Count(on, "<script data-jfar-head>window.__jfarHead="));
        var head = on.IndexOf("<script data-jfar-head>", StringComparison.Ordinal);
        Assert.True(head > 0 && head < on.IndexOf("</head>", StringComparison.OrdinalIgnoreCase));
        Assert.True(on.IndexOf("</script></head>", head, StringComparison.OrdinalIgnoreCase) > head);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("user", HttpStatusCode.Forbidden)]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("apikey", HttpStatusCode.OK)]
    public async Task Diagnostics_IsForAdministratorsAndApiKeysOnly(string? caller, HttpStatusCode expected)
    {
        await using var harness = await StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/AccessRoles/Admin/Diagnostics", r => AddCaller(r, caller));

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var keys = body.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
            Assert.Superset(new HashSet<string>(["Abi", "ActionManifest", "ClientHealth", "Enforcement", "KitVersion", "PluginVersion", "Records", "ServerUsers", "Store", "WebAnchors"]), keys.ToHashSet());
            Assert.Equal(JellyfinLine.VersionOf(typeof(Plugin)), body.RootElement.GetProperty("PluginVersion").GetString());
        }
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("user", HttpStatusCode.NoContent)]
    [InlineData("apikey", HttpStatusCode.Forbidden)]
    public async Task ClientHealth_TakesReportsFromSignedInUsers(string? caller, HttpStatusCode expected)
    {
        await using var harness = await StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Post, "/AccessRoles/Diagnostics/ClientHealth", r =>
        {
            AddCaller(r, caller);
            r.Content = new StringContent("{\"WebVersion\":\"10.11.5\",\"Client\":\"Jellyfin Web\",\"ClientVersion\":\"10.11.5\",\"Layout\":\"Desktop\",\"Experimental\":false,\"PluginVersion\":\"1.0.3.0\",\"Missing\":[]}", Encoding.UTF8, "application/json");
        });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task AnInertBuild_AnswersEveryEndpointWith503()
    {
        // On a Jellyfin line the build isn't for, the registrator adds only KitAbiStatus, and the kit's gate answers 503
        // before a controller is built.
        await using var harness = await StartAsync(version: JellyfinLine.OtherLineVersion);

        using var script = await harness.SendAsync(HttpMethod.Get, "/AccessRoles/ClientScript");
        using var diagnostics = await harness.SendAsync(HttpMethod.Get, "/AccessRoles/Admin/Diagnostics", r => AddCaller(r, "admin"));
        var index = await harness.GetStringAsync("/web/index.html");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, script.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, diagnostics.StatusCode);
        Assert.Contains("\"abi\"", await diagnostics.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain("data-jfar", index, StringComparison.Ordinal);
    }

    public void Dispose() => _directory.Dispose();

    private static void AddCaller(HttpRequestMessage request, string? caller)
    {
        if (caller is not null)
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, caller);
        }
    }

    private static int Count(string text, string needle) => (text.Length - text.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;

    private Task<WebHostHarness> StartAsync(PluginConfiguration? configuration = null, string? version = null)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse(version ?? JellyfinLine.ServerVersion));
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, host.Object);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_directory.Path);
        var provider = new FakeConfigurationProvider(() => configuration ?? new PluginConfiguration());

        return new WebHostHarness()
            .WithServices(services =>
            {
                // Everything the registrator adds, except the configuration (a test's own) and the hosted services.
                foreach (var descriptor in registered.Where(d => d.ServiceType != typeof(IPluginConfigurationProvider) && d.ServiceType != typeof(Microsoft.Extensions.Hosting.IHostedService)))
                {
                    services.Add(descriptor);
                }

                services.AddLogging();
                services.AddSingleton<IPluginConfigurationProvider>(provider);
                services.AddSingleton(paths.Object);
                services.AddSingleton(host.Object);
                services.AddSingleton(Mock.Of<IUserManager>());
                services.AddSingleton(Mock.Of<ILibraryManager>());
                services.AddSingleton(Mock.Of<IServerApplicationPaths>());
            })
            .WithMvc(typeof(AccessRolesController), typeof(AccessRolesAdminController), typeof(AccessRolesDiagnosticsController))
            .StartAsync();
    }
}
