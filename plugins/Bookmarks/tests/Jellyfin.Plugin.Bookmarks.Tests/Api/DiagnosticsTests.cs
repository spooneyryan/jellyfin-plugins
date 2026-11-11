using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// Bookmarks on the kit's coupling policy and health reports: its anchor list, the
/// ClientHealth endpoint (valid reports logged once and listed, forged ones dropped, one per user every ten minutes, a
/// report with <c>WebVersion: null</c> and a valid <c>Client</c> accepted), the admin Diagnostics, and the settings page's
/// Diagnostics section, which renders server values with textContent only.
/// </summary>
public sealed class DiagnosticsTests : IDisposable
{
    private static readonly Guid Viewer = Guid.Parse("f1e2d3c4-0000-0000-0000-000000000001");
    private static readonly Guid Viewer2 = Guid.Parse("f1e2d3c4-0000-0000-0000-000000000002");

    private static readonly string SettingsPageTests = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "Bookmarks", "tests", "Jellyfin.Plugin.Bookmarks.Tests", "Web", "configpage.test.mjs");

    private const string Report = """
        { "WebVersion": "10.11.5", "Client": "Jellyfin Web", "ClientVersion": "10.11.5", "Layout": "Desktop", "Experimental": false,
          "PluginVersion": "1.0.0.0", "Missing": [ { "Anchor": "detailButtons", "View": "details", "Count": 1 } ] }
        """;

    private readonly PluginConfiguration _configuration = new();
    private readonly ListLogger _logger = new();
    private readonly TempDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void TheAnchorList_IsThePinnedSet_Valid_AndUsedByTheScripts()
    {
        var assembly = typeof(Plugin).Assembly;
        var catalog = WebAnchorCatalog.Load(assembly, assembly);

        Assert.Empty(catalog.Problems);
        Assert.Empty(WebCouplingChecks.CheckAnchors(assembly));
        Assert.Equal(
            ["detailPage", "detailContent", "detailButtons", "detailButtonsBefore", "playerPage", "osdButtons", "osdInsertBefore", "osdPositionSlider", "videoElement"],
            catalog.Anchors.Where(a => !a.Name.StartsWith(WebAnchorCatalog.KitNamePrefix, StringComparison.Ordinal)).Select(a => a.Name));
        Assert.Equal([".videoOsdBottom .buttons"], catalog.Find("osdButtons")!.Css);
        Assert.Equal([".btnSubtitles", ".btnAudio", ".btnVideoOsdSettings"], catalog.Find("osdInsertBefore")!.Css);
        Assert.Equal([".videoPlayerContainer video", "video.htmlvideoplayer"], catalog.Find("videoElement")!.Css);
        Assert.All(catalog.Anchors.Where(a => a.Views.Contains("details")), a => Assert.Equal(["details"], a.Views));
    }

    [Fact]
    public void ReportWebAnchorProblems_IsOnByDefault()
    {
        Assert.True(new PluginConfiguration().ReportWebAnchorProblems);
        Assert.True(BookmarksKit.Options.ReportWebAnchorProblems(Services(new PluginConfiguration())));
        Assert.False(BookmarksKit.Options.ReportWebAnchorProblems(Services(new PluginConfiguration { ReportWebAnchorProblems = false })));
    }

    [Fact]
    public async Task AMissingAnchorReport_Answers204_IsLoggedOnce_AndIsListedInDiagnostics()
    {
        await using var host = await StartAsync();

        using var first = await Post(host, Report, "user", Viewer);
        using var second = await Post(host, Report, "user", Viewer2);
        var diagnostics = await Diagnostics(host);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var warning = Assert.Single(_logger.At(LogLevel.Warning), e => e.Message.Contains("web anchor", StringComparison.Ordinal));
        Assert.StartsWith("Bookmarks web anchor detailButtons is missing on the details view (web 10.11.5)", warning.Message, StringComparison.Ordinal);
        var entry = Assert.Single(diagnostics.GetProperty("ClientHealth").EnumerateArray());
        Assert.Equal("detailButtons", entry.GetProperty("Anchor").GetString());
        Assert.True(entry.GetProperty("Required").GetBoolean());
        Assert.Equal(2, entry.GetProperty("Count").GetInt32());
        Assert.Equal(2, entry.GetProperty("Users").GetInt32());
        Assert.Equal("1.2.2.0", diagnostics.GetProperty("PluginVersion").GetString());
        Assert.Equal(Path.Combine(_data.Path, "bookmarks", "bookmarks.json"), diagnostics.GetProperty("Store").GetProperty("File").GetString());
        Assert.Equal("Ok", diagnostics.GetProperty("Store").GetProperty("Status").GetString());
        Assert.Equal(2, diagnostics.GetProperty("Store").GetProperty("Version").GetInt32());
        Assert.True(diagnostics.GetProperty("Abi").GetProperty("Active").GetBoolean());

        // The current clip folder with the real probe (not created yet: measured by the folder above it).
        var clipStorage = diagnostics.GetProperty("ClipStorage");
        Assert.Equal(Path.Combine(_data.Path, "bookmarks", "clips"), clipStorage.GetProperty("Root").GetString());
        Assert.False(clipStorage.GetProperty("Exists").GetBoolean());
        Assert.Equal("Ok", clipStorage.GetProperty("FreeSpace").GetString());
        Assert.True(clipStorage.GetProperty("FreeBytes").GetInt64() > 0);
        Assert.Equal(2048L * 1024 * 1024, clipStorage.GetProperty("MinFreeBytes").GetInt64());
        // The My Bookmarks page first, My Clips, then the developer demo's two.
        Assert.Equal(["bookmarks", "clips", "demo-a", "demo-b"], diagnostics.GetProperty("Pages").EnumerateArray().Select(p => p.GetProperty("Key").GetString()));
        Assert.Contains("detailButtons", diagnostics.GetProperty("WebAnchors").GetProperty("Declared").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task ForgedReports_UnknownAnchor_BadVersion_BadClient_AndAFlood_AreDropped()
    {
        await using var host = await StartAsync();
        string[] forged =
        [
            Report.Replace("\"Anchor\": \"detailButtons\"", "\"Anchor\": \"forgedAnchor\"", StringComparison.Ordinal),
            Report.Replace("\"Anchor\": \"detailButtons\"", "\"Anchor\": \"kit.nothing\"", StringComparison.Ordinal),
            Report.Replace("\"View\": \"details\"", "\"View\": \"player\"", StringComparison.Ordinal),
            Report.Replace("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": \"10.11.5 <script>\"", StringComparison.Ordinal),
            Report.Replace("\"ClientVersion\": \"10.11.5\"", "\"ClientVersion\": \"10\"", StringComparison.Ordinal),
            Report.Replace("\"Client\": \"Jellyfin Web\"", "\"Client\": \"Jellyfin Web\\r\\n[ERR] forged log line\"", StringComparison.Ordinal),
            Report.Replace("\"Layout\": \"Desktop\"", "\"Layout\": \"Fridge\"", StringComparison.Ordinal),
        ];

        foreach (var body in forged)
        {
            using var response = await Post(host, body, "user", Viewer);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        for (var i = 0; i < 10; i++)
        {
            using var response = await Post(host, Report, "user", Viewer2);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var diagnostics = await Diagnostics(host);
        var reports = diagnostics.GetProperty("WebAnchors").GetProperty("Reports");
        Assert.Equal(forged.Length, reports.GetProperty("Invalid").GetInt32());
        Assert.Equal(9, reports.GetProperty("RateLimited").GetInt32());
        Assert.Equal(1, reports.GetProperty("Accepted").GetInt32());
        Assert.Equal(1, Assert.Single(diagnostics.GetProperty("ClientHealth").EnumerateArray()).GetProperty("Count").GetInt32());
        Assert.Single(_logger.At(LogLevel.Warning), e => e.Message.Contains("web anchor", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("forged", StringComparison.Ordinal) && e.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task AReportWithoutAWebVersion_FromANativeAppWrapper_IsAccepted()
    {
        await using var host = await StartAsync();
        var native = Report
            .Replace("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": null", StringComparison.Ordinal)
            .Replace("\"Client\": \"Jellyfin Web\"", "\"Client\": \"Jellyfin Media Player\"", StringComparison.Ordinal)
            .Replace("\"ClientVersion\": \"10.11.5\"", "\"ClientVersion\": \"1.12.0\"", StringComparison.Ordinal);

        using var response = await Post(host, native, "user", Viewer);
        var entry = Assert.Single((await Diagnostics(host)).GetProperty("ClientHealth").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("WebVersion").ValueKind);
        Assert.Equal("Jellyfin Media Player", entry.GetProperty("Client").GetString());
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("(web unknown (Jellyfin Media Player 1.12.0))", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithReportingTurnedOff_ReportsAreDropped_WithoutALogLine()
    {
        _configuration.ReportWebAnchorProblems = false;
        await using var host = await StartAsync();

        using var response = await Post(host, Report, "user", Viewer);
        var diagnostics = await Diagnostics(host);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(diagnostics.GetProperty("ClientHealth").EnumerateArray());
        Assert.False(diagnostics.GetProperty("WebAnchors").GetProperty("Reporting").GetBoolean());
        Assert.DoesNotContain(_logger.At(LogLevel.Warning), e => e.Message.Contains("web anchor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheEndpoints_RefuseWhoTheyMust()
    {
        await using var host = await StartAsync();

        using var apiKey = await Post(host, Report, "apikey", null);
        using var anonymous = await Post(host, Report, null, null);
        using var tooLarge = await Post(host, Report.Replace("\"Layout\"", "\"Padding\": \"" + new string('x', 17000) + "\", \"Layout\"", StringComparison.Ordinal), "user", Viewer);
        using var viewerDiagnostics = await host.SendAsync(HttpMethod.Get, "/Bookmarks/Admin/Diagnostics", r => r.Headers.Add(JellyfinAuth.CallerHeader, "user"));
        using var anonymousDiagnostics = await host.SendAsync(HttpMethod.Get, "/Bookmarks/Admin/Diagnostics");

        Assert.Equal(HttpStatusCode.Forbidden, apiKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, viewerDiagnostics.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousDiagnostics.StatusCode);
        Assert.Empty((await Diagnostics(host)).GetProperty("ClientHealth").EnumerateArray());
    }

    [Fact]
    public void TheSettingsPage_RendersServerValuesWithTextContentOnly()
    {
        // The kit's dashboard-module test, on every page of GetPages() (the settings page and its inline script).
        using var scratch = new TempDirectory();
        var plugin = (IHasWebPages)PluginIdentityChecks.CreatePlugin(typeof(Plugin), scratch.Path);

        Assert.Empty(DashboardPageChecks.Check(typeof(Plugin).Assembly, plugin.GetPages()));
    }

    [Fact]
    public void TheSettingsPage_HasTheReportingSetting_AndTheDiagnosticsSection()
    {
        var page = ReadResource("Jellyfin.Plugin.Bookmarks.Configuration.configPage.html");

        Assert.Contains("id=\"ReportWebAnchorProblems\"", page, StringComparison.Ordinal);
        Assert.Contains("'EnableWebUi', 'ReportWebAnchorProblems'", page, StringComparison.Ordinal);
        Assert.Contains("id=\"BookmarksClientHealth\"", page, StringComparison.Ordinal);
        Assert.Contains("ApiClient.getUrl('Bookmarks/Admin/Diagnostics')", page, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsPage_PutsItsWarningBannersAboveTheSettings_StyledInsideThePageElement()
    {
        // The banner area is the first thing in the page's content, before the settings; its
        // style is inside the data-role="page" element, the only part of the file jellyfin-web keeps. Since 1.1.0 the tabs
        // (Settings | Data | Diagnostics) come between the banners and the settings, so the banners show on
        // every tab.
        var page = ReadResource("Jellyfin.Plugin.Bookmarks.Configuration.configPage.html");

        var pageElement = page.IndexOf("data-role=\"page\"", StringComparison.Ordinal);
        var style = page.IndexOf("#BookmarksConfigPage .bookmarksBanner {", StringComparison.Ordinal);
        var content = page.IndexOf("<div class=\"content-primary\">", StringComparison.Ordinal);
        var banners = page.IndexOf("<div id=\"BookmarksBanners\"></div>", StringComparison.Ordinal);
        var tabs = page.IndexOf("<div id=\"BookmarksTabs\"", StringComparison.Ordinal);
        var form = page.IndexOf("<form id=\"BookmarksConfigForm\"", StringComparison.Ordinal);
        Assert.True(pageElement > 0 && pageElement < style && style < content && content < banners && banners < tabs && tabs < form, $"{pageElement} {style} {content} {banners} {tabs} {form}");
        Assert.Matches(@"<div class=""content-primary"">\s*<!--[^>]*-->\s*<div id=""BookmarksBanners""></div>\s*<div id=""BookmarksTabs""[^>]*role=""tablist""[\s\S]*?</div>\s*<form id=""BookmarksConfigForm""", page);
    }

    [Fact]
    public async Task TheSettingsPage_ShowsEveryDroppedReport_AndWhenTheListIsFull_InAFakePage()
    {
        // configpage.test.mjs runs the page's inline script against Diagnostics bodies: the dropped count includes
        // TooManyWebClients, a list at MaxEntries (or with Overflow) says that older reports made room, the Users line
        // and the bookmark file's kept copies are text, and the guard's and the store's sentences are warning banners
        // above the settings, as text; the unfinished clip files setting round-trips; the clip folder's free-space line
        // and warnings are under its field, as text, and the reserve setting round-trips; the player settings round-trip;
        // and the Jellyfin Enhanced import (dry run, import, the report as text) with the checklist and Use B only after an
        // applied import; since 1.1.0 on the real markup: the tabs, every setting's round trip, the shortcut's check under
        // its field, the Clip folder help, the per-user counts, export, import, snapshots, the maintenance run, the users
        // this server doesn't know, every table in a box of its own that scrolls sideways, and the restore's line for the
        // clip jobs it ended; since 1.2.0 the missing-clip sweep's retention setting and its line on the Diagnostics tab,
        // with "Clip folder unavailable" in amber, the Pages section with the My Clips page's setting, the setting's
        // maximum and why each folder was left out, an unproven folder, the remedies, the button
        // that confirms a folder (its dry run, dialog and outcome; cancelled; refused), the field's warning of a folder there
        // but refused, with the button, and a number above its field's max loaded as the max; and since Bookmarks 1.2 the way
        // out of missing or unreadable data (Restore of a listed snapshot, Start empty) in the kit's dialog, the focus on
        // Cancel, and jellyfin-web's confirmation without the web client; and since the JSON store hardening the
        // kept snapshots' label by how the data was found, Restore off for a snapshot that can't be read, and the list of
        // deleted users (kit-users.json) that can't be read: its banner, its line, and Start the list empty in the kit's dialog;
        // and the clip file renames not finished (none, or in amber with the notes that can't be read).
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        var run = await NodeRunner.RunAsync(node!, ["--test", SettingsPageTests], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("# pass 56", run.StandardOutput, StringComparison.Ordinal);
    }

    private static IServiceProvider Services(PluginConfiguration configuration)
    {
        var provider = new Mock<IPluginConfigurationProvider>();
        provider.Setup(p => p.GetConfiguration()).Returns(configuration);
        return new ServiceCollection().AddSingleton(provider.Object).BuildServiceProvider();
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Task<HttpResponseMessage> Post(WebHostHarness host, string body, string? caller, Guid? userId)
        => host.SendAsync(HttpMethod.Post, "/Bookmarks/Diagnostics/ClientHealth", request =>
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (caller is not null)
            {
                request.Headers.Add(JellyfinAuth.CallerHeader, caller);
            }

            if (userId is not null)
            {
                request.Headers.Add(JellyfinAuth.UserIdHeader, userId.Value.ToString("N"));
            }
        });

    private static async Task<JsonElement> Diagnostics(WebHostHarness host)
    {
        using var response = await host.SendAsync(HttpMethod.Get, "/Bookmarks/Admin/Diagnostics", r => r.Headers.Add(JellyfinAuth.CallerHeader, "admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    private Task<WebHostHarness> StartAsync()
    {
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_data.Path);
        var loggers = new Mock<ILoggerFactory>();
        loggers.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_logger);
        var configuration = new Mock<IPluginConfigurationProvider>();
        configuration.Setup(p => p.GetConfiguration()).Returns(() => _configuration);

        return new WebHostHarness()
            .WithServices(services =>
            {
                // The registrator's own descriptors for the kit, its diagnostics and the store; the rest isn't needed here.
                foreach (var descriptor in registered.Where(d => d.ServiceType == typeof(KitOptions) || d.ServiceType == typeof(KitAbiStatus)
                    || d.ServiceType == typeof(KitPaths) || d.ServiceType == typeof(KitPageHosts) || d.ServiceType == typeof(ClientHealthRegistry)
                    || d.ServiceType == typeof(KitDiagnostics) || d.ServiceType == typeof(IKitDiagnostics) || d.ServiceType == typeof(IKitDiagnosticsSource)
                    || d.ServiceType == typeof(BookmarkPaths) || d.ServiceType == typeof(IBookmarkStore)
                    || d.ServiceType == typeof(ClipStorageGuard) || d.ServiceType == typeof(IDriveSpaceProbe)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(paths.Object);
                services.AddSingleton(loggers.Object);
                services.AddSingleton(configuration.Object);

                // The admin controller's record actions aren't what these tests call.
                services.AddSingleton(Mock.Of<Jellyfin.Plugin.Bookmarks.Services.IBookmarkRecordsAdmin>());
                services.AddSingleton(Mock.Of<Jellyfin.Plugin.Bookmarks.Import.IBookmarkJeImport>());
                services.AddSingleton(Mock.Of<Jellyfin.Plugin.Bookmarks.Services.IBookmarkAdminTools>());
            })
            .WithMvc(typeof(BookmarksDiagnosticsController), typeof(BookmarksAdminController))
            .StartAsync();
    }
}
