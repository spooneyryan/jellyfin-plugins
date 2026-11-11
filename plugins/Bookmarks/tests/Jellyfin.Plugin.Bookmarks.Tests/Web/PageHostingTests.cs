using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Web;

/// <summary>
/// The kit's page hosting in Bookmarks: the My Bookmarks page (on by default, off with <c>EnableBookmarksPage</c>),
/// the My Clips page (on by default, off with <c>EnableClipsPage</c>) and the page-hosting demo behind the developer
/// setting <c>EnablePageDemo</c> (off by default), their shells, the head fragment of every known page on every
/// <c>index.html</c>, and the Plugin Pages registration, with exactly what the registrator registers.
/// </summary>
public sealed class PageHostingTests
{
    private const string BookmarksShell = "<div class=\"jfbm-page\" data-jfbm-page=\"bookmarks\"><p class=\"jfbm-shell-note\">This page needs the Bookmarks web UI, which is turned off or failed to load.</p></div>";
    private const string BookmarksOff = "<div class=\"jfbm-page\"><p class=\"jfbm-shell-note\">My Bookmarks is turned off by your server admin.</p></div>";
    private const string ClipsShell = "<div class=\"jfbm-page\" data-jfbm-page=\"clips\"><p class=\"jfbm-shell-note\">This page needs the Bookmarks web UI, which is turned off or failed to load.</p></div>";
    private const string ClipsOff = "<div class=\"jfbm-page\"><p class=\"jfbm-shell-note\">My Clips is turned off by your server admin.</p></div>";

    private static readonly string WebJs = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "plugins", "Bookmarks", "Jellyfin.Plugin.Bookmarks", "Web", "js");

    private static readonly string WebTests = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "plugins", "Bookmarks", "tests", "Jellyfin.Plugin.Bookmarks.Tests", "Web");

    private readonly PluginConfiguration _configuration = new();

    [Fact]
    public void TheBookmarksAndClipsPagesAreOn_TheDemoOff_AndThePageSettingsHaveTheKitDefaults()
    {
        var configuration = new PluginConfiguration();

        Assert.True(configuration.EnableBookmarksPage);
        Assert.True(configuration.EnableClipsPage);
        Assert.False(configuration.EnablePageDemo);
        Assert.True(configuration.EnableDemoPageB);
        Assert.Equal("FallbackOnly", configuration.PageHostMode);
        Assert.True(configuration.RegisterPluginPages);
        Assert.True(configuration.ShowFallbackNav);
    }

    [Fact]
    public void ThePages_AreDeclaredOnTheServer_AsInTheirScripts()
    {
        // 60-page-bookmarks.js, 70-page-clips.js and 60-pagedemo.js register the same keys, titles, icons and Plugin Pages Id
        // suffixes as BookmarksKit, My Bookmarks first, then My Clips ("My Bookmarks", collections_bookmark,
        // BookmarksPage; "My Clips", video_library, ClipsPage).
        var declared = new[] { "60-page-bookmarks.js", "70-page-clips.js", "60-pagedemo.js" }
            .SelectMany(file => Regex.Matches(File.ReadAllText(Path.Combine(WebJs, file)), "\\{ key: '(?<key>[^']+)', title: '(?<title>[^']+)', icon: '(?<icon>[^']+)', idSuffix: '(?<id>[^']+)'"))
            .Select(m => (m.Groups["key"].Value, m.Groups["title"].Value, m.Groups["icon"].Value, m.Groups["id"].Value))
            .ToList();

        Assert.Equal(BookmarksKit.Options.Pages.Select(p => (p.Key, p.Title, p.Icon, p.IdSuffix)), declared);
        Assert.Equal(("bookmarks", "My Bookmarks", "collections_bookmark", "BookmarksPage"), declared[0]);
        Assert.Equal(("clips", "My Clips", "video_library", "ClipsPage"), declared[1]);
        Assert.Equal(
            ["Jellyfin.Plugin.Bookmarks.BookmarksPage", "Jellyfin.Plugin.Bookmarks.ClipsPage", "Jellyfin.Plugin.Bookmarks.DemoAPage", "Jellyfin.Plugin.Bookmarks.DemoBPage"],
            BookmarksKit.Options.Pages.Select(p => p.PluginPagesId("Bookmarks")));
        Assert.Equal("/Bookmarks/Pages/clips", BookmarksKit.Options.Pages[1].ShellUrl("Bookmarks"));
        Assert.Empty(BookmarksKit.Options.Validate());
    }

    [Fact]
    public async Task ByDefault_TheFragmentListsEveryKnownPage_WithTheDemoOff_AndTheBookmarksAndClipsShellsCarryTheMarker()
    {
        await using var host = await StartAsync();

        var html = await host.GetStringAsync("/web/index.html");
        using var shell = await Get(host, "/Bookmarks/Pages/bookmarks", "user");
        using var clips = await Get(host, "/Bookmarks/Pages/clips", "user");
        using var demo = await Get(host, "/Bookmarks/Pages/demo-a", "user");

        Assert.Single(Regex.Matches(html, "<style data-jfbm-head>"));
        Assert.True(html.IndexOf("data-jfbm-pages", StringComparison.Ordinal) < html.IndexOf("</head>", StringComparison.Ordinal));
        Assert.Contains("var t = {\"bookmarks\":\"My Bookmarks\",\"clips\":\"My Clips\",\"demo-a\":\"Demo page A\",\"demo-b\":\"Demo page B\"};", html, StringComparison.Ordinal);
        Assert.Contains("data-jfbm-pages>{\"mode\":\"FallbackOnly\",\"nav\":true,\"off\":[\"demo-a\",\"demo-b\"],\"pages\":[\"bookmarks\",\"clips\",\"demo-a\",\"demo-b\"]}</script>", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal(BookmarksShell, await shell.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("text/html; charset=utf-8", shell.Content.Headers.ContentType?.ToString());
        Assert.True(shell.Headers.CacheControl?.NoCache);
        Assert.Equal(HttpStatusCode.OK, clips.StatusCode);
        Assert.Equal(ClipsShell, await clips.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("text/html; charset=utf-8", clips.Content.Headers.ContentType?.ToString());
        Assert.True(clips.Headers.CacheControl?.NoCache);
        Assert.Equal(HttpStatusCode.OK, demo.StatusCode);
        Assert.Equal(
            "<div class=\"jfbm-page\"><p class=\"jfbm-shell-note\">Demo page A is turned off by your server admin.</p></div>",
            await demo.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnableBookmarksPageOff_IsA200NoteWithoutTheMarker_OnEveryRequest()
    {
        // The shell says so without script and keeps its 200 (Plugin Pages' host shows a fragment only on success), the
        // fragment lists the page as off (no nav link, the fallback route shows the note), and the next request
        // after the setting is on again serves the page: no restart.
        await using var host = await StartAsync();
        _configuration.EnableBookmarksPage = false;

        var html = await host.GetStringAsync("/web/index.html");
        using var off = await Get(host, "/Bookmarks/Pages/bookmarks", "user");
        _configuration.EnableBookmarksPage = true;
        using var on = await Get(host, "/Bookmarks/Pages/bookmarks", "user");

        Assert.Contains("data-jfbm-pages>{\"mode\":\"FallbackOnly\",\"nav\":true,\"off\":[\"bookmarks\",\"demo-a\",\"demo-b\"],\"pages\":[\"bookmarks\",\"clips\",\"demo-a\",\"demo-b\"]}</script>", html, StringComparison.Ordinal);
        Assert.Contains("var t = {\"bookmarks\":\"My Bookmarks\",", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(BookmarksOff, await off.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("data-jfbm-page=", await off.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(BookmarksShell, await on.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnableClipsPageOff_IsA200NoteWithoutTheMarker_OnEveryRequest_AndLeavesMyBookmarksOn()
    {
        // The turned-off shell (WebHostHarness): the shell says "My Clips is turned off by your server admin." with its
        // 200 and no marker, the fragment lists the page as off (no nav link; the fallback route shows the note), My
        // Bookmarks stays on, and the next request after the setting is on again serves the page: no restart.
        await using var host = await StartAsync();
        _configuration.EnableClipsPage = false;

        var html = await host.GetStringAsync("/web/index.html");
        using var off = await Get(host, "/Bookmarks/Pages/clips", "user");
        using var bookmarks = await Get(host, "/Bookmarks/Pages/bookmarks", "user");
        _configuration.EnableClipsPage = true;
        using var on = await Get(host, "/Bookmarks/Pages/clips", "user");
        var htmlOn = await host.GetStringAsync("/web/index.html");

        Assert.Contains("data-jfbm-pages>{\"mode\":\"FallbackOnly\",\"nav\":true,\"off\":[\"clips\",\"demo-a\",\"demo-b\"],\"pages\":[\"bookmarks\",\"clips\",\"demo-a\",\"demo-b\"]}</script>", html, StringComparison.Ordinal);
        Assert.Contains("\"clips\":\"My Clips\"", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(ClipsOff, await off.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("data-jfbm-page=", await off.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(BookmarksShell, await bookmarks.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ClipsShell, await on.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("\"off\":[\"demo-a\",\"demo-b\"]", htmlOn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithTheDemoOn_TheFragmentAndShellsFollowTheSettings_OnEveryRequest()
    {
        await using var host = await StartAsync();
        _configuration.EnablePageDemo = true;

        var html = await host.GetStringAsync("/web/index.html");
        using var shellA = await Get(host, "/Bookmarks/Pages/demo-a", "user");
        _configuration.EnableDemoPageB = false;
        _configuration.PageHostMode = "Auto";
        var htmlOff = await host.GetStringAsync("/web/index.html");
        using var shellB = await Get(host, "/Bookmarks/Pages/demo-b", "user");

        Assert.Single(Regex.Matches(html, "<style data-jfbm-head>"));
        Assert.Contains("data-jfbm-pages>{\"mode\":\"FallbackOnly\",\"nav\":true,\"off\":[],\"pages\":[\"bookmarks\",\"clips\",\"demo-a\",\"demo-b\"]}</script>", html, StringComparison.Ordinal);
        Assert.Contains("data-jfbm-pages>{\"mode\":\"Auto\",\"nav\":true,\"off\":[\"demo-b\"],\"pages\":[\"bookmarks\",\"clips\",\"demo-a\",\"demo-b\"]}</script>", htmlOff, StringComparison.Ordinal);
        Assert.Equal(
            "<div class=\"jfbm-page\" data-jfbm-page=\"demo-a\"><p class=\"jfbm-shell-note\">This page needs the Bookmarks web UI, which is turned off or failed to load.</p></div>",
            await shellA.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Demo page B is turned off by your server admin.", await shellB.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheShells_NeedASignedInCaller_AndOnlyKnownKeysExist()
    {
        await using var host = await StartAsync();

        using var anonymous = await Get(host, "/Bookmarks/Pages/bookmarks", null);
        using var anonymousClips = await Get(host, "/Bookmarks/Pages/clips", null);
        using var unknown = await Get(host, "/Bookmarks/Pages/nope", "user");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousClips.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task EnableWebUiOff_RemovesTheHeadFragmentToo_TheShellStillServesItsNote()
    {
        await using var host = await StartAsync();
        _configuration.EnableWebUi = false;

        var html = await host.GetStringAsync("/web/index.html");
        using var shell = await Get(host, "/Bookmarks/Pages/bookmarks", "user");

        Assert.Equal(WebHostHarness.SyntheticIndexHtml, html);
        Assert.Equal(BookmarksShell, await shell.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true, true, false, "Auto", "Fallback", "Fallback", "Disabled", "Disabled")]
    [InlineData(false, true, false, "Auto", "Disabled", "Fallback", "Disabled", "Disabled")]
    [InlineData(true, false, false, "Auto", "Fallback", "Disabled", "Disabled", "Disabled")]
    [InlineData(false, false, false, "Auto", "Disabled", "Disabled", "Disabled", "Disabled")]
    [InlineData(true, true, true, "FallbackOnly", "Fallback", "Fallback", "Fallback", "Fallback")]
    [InlineData(true, true, true, "Auto", "Fallback", "Fallback", "Fallback", "Fallback")]
    [InlineData(false, false, true, "FallbackOnly", "Disabled", "Disabled", "Fallback", "Fallback")]
    public async Task TheRegistration_RegistersThePagesThatAreOn_AndOnlyInAutoWithPluginPages(bool bookmarks, bool clips, bool demo, string mode, string page, string clipsPage, string a, string b)
    {
        // Without a Plugin Pages assembly, Auto leaves the pages on their fallback routes (NotInstalled); a page that
        // is off is never registered (Plugin Pages 2.4.x can't unregister).
        _configuration.EnableBookmarksPage = bookmarks;
        _configuration.EnableClipsPage = clips;
        _configuration.EnablePageDemo = demo;
        _configuration.PageHostMode = mode;
        using var provider = Registered().AddLogging().AddSingleton(ConfigurationProvider()).BuildServiceProvider();

        foreach (var service in provider.GetServices<IHostedService>().OfType<PluginPagesRegistrationService>())
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal([page, clipsPage, a, b], provider.GetRequiredService<KitPageHosts>().Entries.Select(e => e.Host.ToString()));
    }

    [Fact]
    public async Task TheDemoScript_DoesNothingWithoutTheFragment_AndWorksWithIt_InAFakePage()
    {
        var script = new EmbeddedScript(BookmarksKit.Options, NullLoggerFactory.Instance).ClientScriptText;
        var run = await KitScriptChecks.RunTestFileAsync(Path.Combine(WebTests, "pagedemo.test.mjs"), script, BookmarksKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# pass 4", run.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// page-bookmarks.test.mjs: the My Bookmarks page in the kit's fake page: the registration and the nav link, the
    /// toolbar with each view's sort list, the grouped and flat views on the Query and Groups endpoints, paging, the
    /// state in the hash (deep links, Back), the empty and error states, and the links to the page.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheBookmarksPage_PassesItsTests_InAFakePage()
    {
        var script = new EmbeddedScript(BookmarksKit.Options, NullLoggerFactory.Instance).ClientScriptText;
        var run = await KitScriptChecks.RunTestFileAsync(Path.Combine(WebTests, "page-bookmarks.test.mjs"), script, BookmarksKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("# pass 0", run.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// page-clips.test.mjs: the My Clips page in a fake page: the registration and the nav link after My
    /// Bookmarks, the toolbar and the state in the hash (deep links, Back, wrong values), each clip state and badge, the
    /// poster and the link only for a video the viewer can open, the summary line, paging, the poll of the clips being made
    /// (in place, paused while hidden, stopped), the highlight's one landing, the empty, error and turned-off states, and
    /// the links to the page (My Bookmarks, the player's list, the clip-ready toast); and the row actions (each
    /// state's, Preview with the MKV fallback, Download, Play, Edit, Make the clip again, Cancel, Delete with the 409, Forget,
    /// Dismiss, the More menu, the bulk delete in batches of 200, the selection through redraws, offline and unrecognised
    /// folders' rows).
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheClipsPage_PassesItsTests_InAFakePage()
    {
        var script = new EmbeddedScript(BookmarksKit.Options, NullLoggerFactory.Instance).ClientScriptText;
        var run = await KitScriptChecks.RunTestFileAsync(Path.Combine(WebTests, "page-clips.test.mjs"), script, BookmarksKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("# pass 0", run.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// page-clips-admin.test.mjs: the My Clips page's "All users" scope in a fake page: the switch and the
    /// Owner select only for an admin the server allows it, the admin routes asked with the owner, the owner (bidi-isolated)
    /// and the path on each row and never a note, the scope and the owner in the hash, the admin actions only (no rename,
    /// remake or edit) through the admin routes with their dialogs' focus on Cancel and the server's refusals said, the bulk
    /// delete through the admins' route, the poll through the admins' status, the TV layout's D-pad over the switch.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheClipsPagesAllUsersScope_PassesItsTests_InAFakePage()
    {
        var script = new EmbeddedScript(BookmarksKit.Options, NullLoggerFactory.Instance).ClientScriptText;
        var run = await KitScriptChecks.RunTestFileAsync(Path.Combine(WebTests, "page-clips-admin.test.mjs"), script, BookmarksKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("# pass 0", run.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// detail-tv.test.mjs: the details page's hook of the kit's Jellyfin Enhanced TV mitigation (kit.je.tvSectionJump,
    /// for Jellyfin Enhanced 12.14's TV trap) in a fake page: on the TV layout, with JE's closed
    /// Reviews section and "My bookmarks" shown, a Bookmarks button after Add bookmark and before More whose OK focuses the
    /// section's first control; nothing on the desktop layout or while the video has no bookmarks.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheDetailsPagesTvJump_PastJellyfinEnhancedsReviews_PassesItsTests_InAFakePage()
    {
        var script = new EmbeddedScript(BookmarksKit.Options, NullLoggerFactory.Instance).ClientScriptText;
        var run = await KitScriptChecks.RunTestFileAsync(Path.Combine(WebTests, "detail-tv.test.mjs"), script, BookmarksKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("# pass 0", run.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePagesController_FollowsTheApiConventions()
    {
        Assert.Contains(typeof(BookmarksPagesController), ControllerDiscovery.FindControllers(typeof(Plugin).Assembly));
        Assert.Empty(ControllerNameChecks.CheckNames(typeof(Plugin).Assembly, BookmarksKit.Options.RoutePrefix));
    }

    private static Task<HttpResponseMessage> Get(WebHostHarness host, string path, string? caller)
        => host.SendAsync(HttpMethod.Get, path, r =>
        {
            if (caller is not null)
            {
                r.Headers.Add(JellyfinAuth.CallerHeader, caller);
            }
        });

    private static IServiceCollection Registered()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, host.Object);

        // The kit's and the page hosting's own descriptors; the rest of the plugin isn't needed here.
        IServiceCollection kept = new ServiceCollection();
        foreach (var descriptor in registered.Where(d => d.ServiceType == typeof(KitOptions) || d.ServiceType == typeof(KitAbiStatus)
            || d.ServiceType == typeof(IStartupFilter) || d.ServiceType == typeof(KitPages) || d.ServiceType == typeof(IKitPages)
            || d.ServiceType == typeof(KitPageHosts) || (d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PluginPagesRegistrationService))))
        {
            kept.Add(descriptor);
        }

        return kept;
    }

    private IPluginConfigurationProvider ConfigurationProvider()
    {
        var provider = new Mock<IPluginConfigurationProvider>();
        provider.Setup(p => p.GetConfiguration()).Returns(() => _configuration);
        return provider.Object;
    }

    private Task<WebHostHarness> StartAsync()
    {
        var registered = Registered();
        var configuration = ConfigurationProvider();
        return new WebHostHarness()
            .WithServices(services =>
            {
                foreach (var descriptor in registered.Where(d => d.ServiceType != typeof(IHostedService)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(configuration);
            })
            .WithMvc(typeof(BookmarksPagesController))
            .StartAsync();
    }
}
