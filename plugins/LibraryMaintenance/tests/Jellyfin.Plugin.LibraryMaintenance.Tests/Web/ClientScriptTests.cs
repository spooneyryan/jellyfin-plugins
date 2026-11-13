using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.LibraryMaintenance.Api;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Web;

/// <summary>
/// Library Maintenance's ClientScript: Move's item-menu entry and its dialog, built by the kit's
/// EmbeddedScript from <c>Web/js</c> and <c>Web/css</c>; node's syntax check, the kit's generic ClientScript checks and
/// Move's own tests in the kit's fake page (<c>move.test.mjs</c>); the anonymous ClientScript route; the index.html tag
/// while <c>EnableWebUi</c> is on; the ClientHealth reports; a title for every code the Move routes answer with.
/// </summary>
public sealed class ClientScriptTests
{
    private static readonly Lazy<EmbeddedScript> Scripts = new(() => new EmbeddedScript(LibraryMaintenanceKit.Options, NullLoggerFactory.Instance));

    private static readonly string WebTestsFolder = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "LibraryMaintenance", "tests", "Jellyfin.Plugin.LibraryMaintenance.Tests", "Web");

    private static readonly string BuildVersion = BuildYaml.Load(Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "plugins", "LibraryMaintenance", JellyfinLine.BuildYamlName)).Version;

    private static readonly string Mvid8 = typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];

    private const string Report = """
        { "WebVersion": "10.11.5", "Client": "Jellyfin Web", "ClientVersion": "10.11.5", "Layout": "Desktop", "Experimental": false,
          "PluginVersion": "1.0.0.0", "Missing": [ { "Anchor": "kit.detailsMoreButton", "View": "menus", "Count": 1 } ] }
        """;

    private readonly PluginConfiguration _configuration = new();

    [Fact]
    public void TheWebFiles_AreEmbeddedByTheKit_InNameOrder()
    {
        var assembly = typeof(Plugin).Assembly;

        Assert.Equal(
            ["Web.js.10-selectors.js", "Web.js.20-move-api.js", "Web.js.40-move-menu.js", "Web.js.48-move-texts.js", "Web.js.50-move-dialog.js", "Web.js.51-move-picker.js", "Web.js.55-move-preview.js", "Web.js.90-main.js"],
            EmbeddedScript.ResourceNames(assembly, EmbeddedScript.ScriptFolder, ".js"));
        Assert.Equal(["Web.css.move.css"], EmbeddedScript.ResourceNames(assembly, EmbeddedScript.CssFolder, ".css"));
    }

    [Fact]
    public void TheClientScript_CarriesTheBuild_AndItsStyles()
    {
        var script = Scripts.Value.ClientScriptText;

        Assert.Contains("var KIT_BUILD = {\"plugin\":\"Library Maintenance\",\"prefix\":\"jflm\",\"route\":\"LibraryMaintenance\",\"version\":\"" + BuildVersion + "\"", script, StringComparison.Ordinal);
        Assert.Contains("// ---- Web/js/10-selectors.js\n", script, StringComparison.Ordinal);
        Assert.Contains("// ---- Web/js/90-main.js\n", script, StringComparison.Ordinal);
        Assert.Contains(".jflm-move-dialog .jflm-native-dialog-body{", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__PFX__", script, StringComparison.Ordinal);

        // The anchors of anchors.json are in the script, for kit.anchors and the health reports.
        Assert.Contains("{\"name\":\"mediaInfoRow\",", script, StringComparison.Ordinal);
        Assert.Contains("{\"name\":\"moveIcon\",", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnchorList_IsValid_AndEveryAnchorIsReportable()
    {
        var assembly = typeof(Plugin).Assembly;
        var catalog = WebAnchorCatalog.Load(assembly, assembly);

        Assert.Empty(catalog.Problems);
        Assert.Equal(["mediaInfoRow", "moveIcon"], catalog.Anchors.Where(a => !a.Name.StartsWith(WebAnchorCatalog.KitNamePrefix, StringComparison.Ordinal)).Select(a => a.Name));
        Assert.True(catalog.Accepts("mediaInfoRow", "menus"));
        Assert.True(catalog.Accepts("kit.detailsMoreButton", "menus"));
        Assert.All(catalog.Anchors.Where(a => !a.Name.StartsWith(WebAnchorCatalog.KitNamePrefix, StringComparison.Ordinal)), a => Assert.False(a.Required));
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
        var run = await KitScriptChecks.RunClientScriptTestsAsync(Scripts.Value.ClientScriptText, LibraryMaintenanceKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# pass 3", run.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// move.test.mjs: the item-menu entry (where it shows, the capabilities, the eligibility batches, the details
    /// page and its health checks) and the dialog (what moves, the picker, the preview, the errors, the keys).
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheMoveUi_PassesItsTests_InAFakePage()
    {
        var testFile = Path.Combine(WebTestsFolder, "move.test.mjs");
        var run = await KitScriptChecks.RunTestFileAsync(testFile, Scripts.Value.ClientScriptText, LibraryMaintenanceKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("# skipped 0", run.StandardOutput, StringComparison.Ordinal);

        // Every test of the file ran.
        var declared = Regex.Matches(await File.ReadAllTextAsync(testFile, TestContext.Current.CancellationToken), @"(?m)^test\(").Count;
        Assert.Contains("# pass " + declared.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", run.StandardOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMoveCode_HasATitle_InTheDialog()
    {
        var texts = Resource("Web.js.48-move-texts.js");
        var titled = Regex.Matches(texts, @"^\s*'(move\.[a-z-]+)': '", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var codes = MoveErrorCodes.All.Append(MoveEligibility.NotFoundCode).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(codes.Order(StringComparer.Ordinal), titled.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/jf")]
    public async Task ClientScript_IsAnonymousJavaScript_WithTheMvidAsETag(string baseUrl)
    {
        await using var host = await StartAsync(baseUrl);
        var scripts = host.Services.GetRequiredService<EmbeddedScript>();

        using var response = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/ClientScript?v=" + BuildVersion + "-" + Mvid8);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var current = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/ClientScript", r => r.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag!.Tag));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Equal("\"" + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N") + "\"", response.Headers.ETag?.Tag);
        Assert.Equal(scripts.ClientScriptText, new UTF8Encoding(false, true).GetString(body));
        Assert.Equal(HttpStatusCode.NotModified, current.StatusCode);
    }

    [Theory]
    [InlineData("", "/web/index.html")]
    [InlineData("/jf", "/web/")]
    public async Task IndexHtml_GetsTheTagOnce_WhileEnableWebUiIsOn(string baseUrl, string path)
    {
        await using var host = await StartAsync(baseUrl);
        var tag = "<script data-jflm-plugin=\"" + BuildVersion + "\" src=\"../LibraryMaintenance/ClientScript?v=" + BuildVersion + "-" + Mvid8 + "\" defer></script>";

        var on = await host.GetStringAsync(path);
        _configuration.EnableWebUi = false;
        var off = await host.GetStringAsync(path);

        Assert.Contains(tag + "</body>", on, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(on, "data-jflm-plugin"));
        Assert.Equal(WebHostHarness.SyntheticIndexHtml, off);
    }

    [Fact]
    public void TheTagLoadsTheClientScriptRoute_UnderAnyBaseUrl()
    {
        var route = typeof(LibraryMaintenanceController).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single().Template;
        var action = typeof(LibraryMaintenanceController).GetMethod(nameof(LibraryMaintenanceController.GetLibraryMaintenanceClientScript))!
            .GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Single().Template;

        foreach (var baseUrl in new[] { string.Empty, "/jf" })
        {
            var resolved = new Uri(new Uri("http://127.0.0.1:8097" + baseUrl + "/web/index.html"), "../LibraryMaintenance/ClientScript?v=1");
            Assert.Equal(baseUrl + "/" + route + "/" + action, resolved.AbsolutePath);
        }
    }

    [Fact]
    public async Task AMissingAnchorReport_Answers204_AndIsListedInDiagnostics_WhileReportingIsOn()
    {
        await using var host = await StartAsync(string.Empty);

        using var anonymous = await Post(host, null);
        using var report = await Post(host, "user");
        var listed = await ClientHealth(host);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, report.StatusCode);
        var entry = Assert.Single(listed.EnumerateArray());
        Assert.Equal("kit.detailsMoreButton", entry.GetProperty("Anchor").GetString());
        Assert.Equal("menus", entry.GetProperty("View").GetString());
        Assert.False(entry.GetProperty("Required").GetBoolean());
    }

    [Fact]
    public async Task WithReportingOff_AReportIsDropped()
    {
        await using var host = await StartAsync(string.Empty);
        _configuration.ReportWebAnchorProblems = false;

        using var report = await Post(host, "user");

        Assert.Equal(HttpStatusCode.NoContent, report.StatusCode);
        Assert.Empty((await ClientHealth(host)).EnumerateArray());
    }

    private static Task<HttpResponseMessage> Post(WebHostHarness host, string? caller)
        => host.SendAsync(HttpMethod.Post, "/LibraryMaintenance/Diagnostics/ClientHealth", request =>
        {
            request.Content = new StringContent(Report, Encoding.UTF8, "application/json");
            if (caller is not null)
            {
                request.Headers.Add(JellyfinAuth.CallerHeader, caller);
                request.Headers.Add(JellyfinAuth.UserIdHeader, "f1e2d3c4000000000000000000000001");
            }
        });

    private static async Task<JsonElement> ClientHealth(WebHostHarness host)
    {
        using var response = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Admin/Diagnostics", r => r.Headers.Add(JellyfinAuth.CallerHeader, "admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("ClientHealth").Clone();
    }

    private static string Resource(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " isn't embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private Task<WebHostHarness> StartAsync(string baseUrl)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, host.Object);
        var provider = new Mock<IPluginConfigurationProvider>();
        provider.Setup(p => p.GetConfiguration()).Returns(() => _configuration);

        return new WebHostHarness()
            .WithBaseUrl(baseUrl)
            .WithServices(services =>
            {
                // The registrator's own descriptors for the kit, the script, the index.html middleware and the diagnostics.
                foreach (var descriptor in registered.Where(d => d.ServiceType == typeof(KitOptions) || d.ServiceType == typeof(KitAbiStatus)
                    || d.ServiceType == typeof(EmbeddedScript) || d.ServiceType == typeof(IEmbeddedScript) || d.ServiceType == typeof(IStartupFilter)
                    || d.ServiceType.Namespace == "Jellyfin.Plugin.Kit.Diagnostics"))
                {
                    services.Add(descriptor);
                }

                var index = new Mock<LibraryMaintenance.Duplicates.Index.IDuplicatesIndex>();
                index.Setup(i => i.SeesEveryLibrary(It.IsAny<Jellyfin.Database.Implementations.Entities.User?>())).Returns(true);
                var users = new Mock<MediaBrowser.Controller.Library.IUserManager>();
                users.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => new Jellyfin.Database.Implementations.Entities.User("admin", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = id });
                services.AddSingleton(Mock.Of<LibraryMaintenance.Move.IMoveMedia>());
                services.AddSingleton(Mock.Of<LibraryMaintenance.Settings.ILibraryMaintenanceSettings>());
                services.AddSingleton(index.Object);
                services.AddSingleton(users.Object);
                services.AddSingleton(provider.Object);
            })
            .WithMvc(typeof(LibraryMaintenanceController), typeof(LibraryMaintenanceAdminController), typeof(LibraryMaintenanceDiagnosticsController))
            .StartAsync();
    }
}
