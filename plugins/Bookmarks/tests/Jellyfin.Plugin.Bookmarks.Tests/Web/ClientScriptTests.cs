using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Web;

/// <summary>
/// Bookmarks' web client on the kit: the Web/js files built into one ClientScript by the kit's
/// EmbeddedScript, checked with node (syntax, the kit's generic ClientScript checks and Bookmarks' own UI tests in
/// bookmarks.test.mjs), the coupling policy, and the anonymous ClientScript action over HTTP.
/// </summary>
public sealed class ClientScriptTests
{
    private static readonly string BookmarksScriptTests = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "Bookmarks", "tests", "Jellyfin.Plugin.Bookmarks.Tests", "Web", "bookmarks.test.mjs");

    private static readonly Lazy<EmbeddedScript> Scripts = new(() => new EmbeddedScript(BookmarksKit.Options, NullLoggerFactory.Instance));

    [Fact]
    public void TheWebFiles_AreEmbeddedByTheKit_InTheWebFolderLayout()
    {
        var assembly = typeof(Plugin).Assembly;

        Assert.Equal(
            ["Web.js.10-selectors.js", "Web.js.20-api.js", "Web.js.30-editor.js", "Web.js.35-lists.js", "Web.js.36-name-dialog.js", "Web.js.40-player.js", "Web.js.41-timeline.js", "Web.js.50-detail.js", "Web.js.60-page-bookmarks.js", "Web.js.60-pagedemo.js", "Web.js.70-page-clips.js", "Web.js.75-page-clips-admin.js", "Web.js.90-main.js"],
            EmbeddedScript.ResourceNames(assembly, EmbeddedScript.ScriptFolder, ".js"));
        Assert.Equal(["Web.css.bookmarks.css"], EmbeddedScript.ResourceNames(assembly, EmbeddedScript.CssFolder, ".css"));

        // Bookmarks 1.0's single bookmarks.js resource is gone; the only other scripts are the kit's.
        Assert.Equal(
            ["Kit.kit.js"],
            assembly.GetManifestResourceNames().Where(n => n.EndsWith(".js", StringComparison.Ordinal) && !n.StartsWith(EmbeddedScript.ScriptFolder, StringComparison.Ordinal)));
    }

    [Fact]
    public void TheClientScript_CarriesBookmarksBuild_AndItsStyles()
    {
        var script = Scripts.Value.ClientScriptText;

        Assert.Contains("var KIT_BUILD = {\"plugin\":\"Bookmarks\",\"prefix\":\"jfbm\",\"route\":\"Bookmarks\",\"version\":\"1.2.2.0\",\"kit\":\"" + KitBuild.KitVersion + "\"", script, StringComparison.Ordinal);
        Assert.Contains("// ---- Web/js/90-main.js\n", script, StringComparison.Ordinal);
        Assert.Contains(".jfbm-row{", script, StringComparison.Ordinal);
        Assert.Contains(".jfbm-toast{", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__PFX__", script, StringComparison.Ordinal);
        Assert.Null(Scripts.Value.AdminScriptText);
    }

    [Fact]
    public void TheWebFiles_FollowTheCouplingPolicy()
    {
        Assert.Empty(WebCouplingChecks.CheckPluginScripts(typeof(Plugin).Assembly));
        Assert.Empty(WebCouplingChecks.CheckPluginStyles(typeof(Plugin).Assembly));
    }

    [Fact]
    public async Task TheClientScript_PassesNodeCheck()
    {
        var run = await KitScriptChecks.CheckSyntaxAsync(Scripts.Value.ClientScriptText, module: false, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
    }

    [Fact]
    public async Task TheClientScript_PassesTheKitsClientScriptChecks()
    {
        var run = await KitScriptChecks.RunClientScriptTestsAsync(Scripts.Value.ClientScriptText, BookmarksKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# pass 3", run.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// bookmarks.test.mjs: Bookmarks 1.0's web UI, with 1.1's editor, rows, badges, "My bookmarks" section and delete
    /// confirmation, and 1.1's player: the name dialog, "Always ask", the chapter suggestion, the pause,
    /// scopedKeys on every dialog and ShowPlayerButtons.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheBookmarksUi_PassesItsTests_InAFakePage()
    {
        var run = await KitScriptChecks.RunTestFileAsync(BookmarksScriptTests, Scripts.Value.ClientScriptText, BookmarksKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("# pass 0", run.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/jf")]
    public async Task ClientScript_IsAnonymousJavaScript_WithTheMvidAsETag(string baseUrl)
    {
        await using var host = await StartAsync(baseUrl);
        var scripts = host.Services.GetRequiredService<EmbeddedScript>();

        using var response = await host.SendAsync(HttpMethod.Get, "/Bookmarks/ClientScript?v=1.0.0.0-00000000");
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var current = await host.SendAsync(HttpMethod.Get, "/Bookmarks/ClientScript", r => r.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag!.Tag));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Equal("\"" + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N") + "\"", response.Headers.ETag?.Tag);
        Assert.Equal(scripts.ClientScriptText, new UTF8Encoding(false, true).GetString(body));
        Assert.Equal(HttpStatusCode.NotModified, current.StatusCode);
    }

    private static Task<WebHostHarness> StartAsync(string baseUrl)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, host.Object);

        return new WebHostHarness()
            .WithBaseUrl(baseUrl)
            .WithServices(services =>
            {
                // The registrator's own descriptors for the kit and the script; the controller's other dependencies as mocks.
                foreach (var descriptor in registered.Where(d => d.ServiceType == typeof(KitOptions) || d.ServiceType == typeof(KitAbiStatus)
                    || d.ServiceType == typeof(EmbeddedScript) || d.ServiceType == typeof(IEmbeddedScript)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(Mock.Of<IBookmarkManager>());
                services.AddSingleton(Mock.Of<IBookmarkQuery>());
                services.AddSingleton(Mock.Of<IBookmarkPreferences>());
                services.AddSingleton(Mock.Of<IUserManager>());
            })
            .WithMvc(typeof(Bookmarks.Api.BookmarksController))
            .StartAsync();
    }
}
