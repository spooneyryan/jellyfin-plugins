using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Web;

/// <summary>
/// Library Maintenance's Dashboard page: the page HTML (no data, no script, no
/// <c>${</c>), the <c>LibraryMaintenanceJs</c> stub, and the AdminScript module the kit builds from <c>Web/admin</c>
/// (node's syntax check, the kit's AdminScript checks, the coupling policy, and the page's own tests in the kit's fake
/// page, <c>admin.test.mjs</c>), served anonymously with the MVID as its ETag.
/// </summary>
public sealed class WebTests
{
    // The fake page's fetch for a module run in Node's realm (TheAdminScript_PassesTheKitsAdminScriptChecks): the kit's check
    // makes the fake page's window and document globals, but not its fetch, so the module's first load would go to Node's
    // own fetch, at the fake ApiClient's 127.0.0.1:8097.
    private const string PageFetch = "const fetch = function (url, init) { return window.fetch(url, init); };\n";

    private static readonly Lazy<EmbeddedScript> Scripts = new(() => new EmbeddedScript(LibraryMaintenanceKit.Options, NullLoggerFactory.Instance));

    private static readonly string WebTestsFolder = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "LibraryMaintenance", "tests", "Jellyfin.Plugin.LibraryMaintenance.Tests", "Web");

    // The version the build says (build.yaml), so a development build's bump doesn't touch these tests.
    private static readonly string BuildVersion = BuildYaml.Load(Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "plugins", "LibraryMaintenance", "build.yaml")).Version;

    [Fact]
    public void TheDashboardPages_FollowTheDashboardRules()
    {
        var scratch = Directory.CreateTempSubdirectory("librarymaintenance-pages-");
        try
        {
            var plugin = (Plugin)PluginIdentityChecks.CreatePlugin(typeof(Plugin), scratch.FullName);

            Assert.Empty(DashboardPageChecks.Check(typeof(Plugin).Assembly, plugin.GetPages()));
            Assert.Equal([Plugin.PageName, Plugin.ScriptPageName], plugin.GetPages().Select(p => p.Name));
            Assert.Equal([true, false], plugin.GetPages().Select(p => p.EnableInMainMenu));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheDashboardPage_IsTheAdminScriptsSkeleton_WithNoScriptOfItsOwn()
    {
        // The page's controller is the LibraryMaintenanceJs stub; the page is the loading note and the root the
        // module mounts into, with no ${ anywhere (jellyfin-web's translateHtml would rewrite it).
        var html = Resource("Jellyfin.Plugin.LibraryMaintenance.Configuration.configPage.html");

        Assert.Contains("id=\"LibraryMaintenanceConfigPage\" data-role=\"page\" class=\"page type-interior pluginConfigurationPage\" data-title=\"Library Maintenance\" data-controller=\"__plugin/LibraryMaintenanceJs\"", html, StringComparison.Ordinal);
        Assert.Contains("<p id=\"LibraryMaintenanceScriptNote\">Loading the page…</p>", html, StringComparison.Ordinal);
        Assert.Contains("<div id=\"LibraryMaintenanceRoot\"></div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("${", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain(" is=\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);

        var stub = Stub();
        Assert.Contains("export default function LibraryMaintenancePage(view, params) {", stub, StringComparison.Ordinal);
        Assert.Contains("import(ApiClient.getUrl('LibraryMaintenance/AdminScript')).then(function (module) {", stub, StringComparison.Ordinal);
        Assert.Contains("module.mount(view, params || {});", stub, StringComparison.Ordinal);
        Assert.Contains("note.textContent = 'The Library Maintenance page could not load its script.", stub, StringComparison.Ordinal);
        Assert.DoesNotContain("viewshow", stub, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", stub, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminScript_IsBuiltFromWebAdmin_InNameOrder_AndExportsMount()
    {
        var assembly = typeof(Plugin).Assembly;
        var script = Scripts.Value.AdminScriptText;

        Assert.NotNull(script);
        Assert.Equal(
            ["Web.admin.js.10-common.js", "Web.admin.js.20-api.js", "Web.admin.js.30-duplicates.js", "Web.admin.js.35-groups.js", "Web.admin.js.70-settings.js", "Web.admin.js.80-diagnostics.js", "Web.admin.js.90-mount.js"],
            EmbeddedScript.ResourceNames(assembly, EmbeddedScript.AdminScriptFolder, ".js"));
        Assert.Equal(["Web.admin.css.librarymaintenance-admin.css"], EmbeddedScript.ResourceNames(assembly, EmbeddedScript.AdminCssFolder, ".css"));
        Assert.Contains("const KIT_BUILD = {\"plugin\":\"Library Maintenance\",\"prefix\":\"jflm\",\"route\":\"LibraryMaintenance\",\"version\":\"" + BuildVersion + "\"", script, StringComparison.Ordinal);
        Assert.Contains("// ---- Web/admin/js/90-mount.js\n", script, StringComparison.Ordinal);
        Assert.EndsWith("export { mount };\n", script, StringComparison.Ordinal);
        Assert.Contains(".jflm-page-tab{", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__PFX__", script, StringComparison.Ordinal);

        // The only other scripts are the kit's, the stub and, since Move's item-menu entry, the ClientScript's (ClientScriptTests).
        Assert.Equal(
            ["Jellyfin.Plugin.LibraryMaintenance.Configuration.LibraryMaintenanceJs.js", "Kit.kit.js"],
            assembly.GetManifestResourceNames().Where(n => n.EndsWith(".js", StringComparison.Ordinal)
                && !n.StartsWith(EmbeddedScript.AdminScriptFolder, StringComparison.Ordinal) && !n.StartsWith(EmbeddedScript.ScriptFolder, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheWebFiles_FollowTheCouplingPolicy_AndDeclareTheirAnchors()
    {
        Assert.Empty(WebCouplingChecks.CheckPluginScripts(typeof(Plugin).Assembly));
        Assert.Empty(WebCouplingChecks.CheckPluginStyles(typeof(Plugin).Assembly));
        Assert.Empty(WebCouplingChecks.CheckAnchors(typeof(Plugin).Assembly));
    }

    [Fact]
    public async Task TheDashboardStub_AndTheAdminScript_PassNodeCheck_AsModules()
    {
        var stub = await KitScriptChecks.CheckSyntaxAsync(Stub(), module: true, TestContext.Current.CancellationToken);
        Assert.SkipWhen(stub is null, "Node.js is not on the PATH.");
        Assert.True(stub!.ExitCode == 0, stub.Output);

        var admin = await KitScriptChecks.CheckSyntaxAsync(Scripts.Value.AdminScriptText!, module: true, TestContext.Current.CancellationToken);
        Assert.True(admin!.ExitCode == 0, admin.Output);
    }

    [Fact]
    public async Task TheAdminScript_PassesTheKitsAdminScriptChecks()
    {
        var run = await KitScriptChecks.RunAdminScriptTestsAsync(PageFetch + Scripts.Value.AdminScriptText, LibraryMaintenanceKit.Options.Prefix, cancellationToken: TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# pass 1", run.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// admin.test.mjs: the page in the kit's fake page: the tabs and their data on the first mount,
    /// with no viewshow; the tab a URL names; the keyboard; text-only rendering; a failed load; a restored view.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheAdminPage_PassesItsTests_InAFakePage()
    {
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        using var temp = new TempDirectory();
        var environment = new Dictionary<string, string>
        {
            ["KIT_ADMIN_SCRIPT"] = temp.CreateFile("admin-script.mjs", Scripts.Value.AdminScriptText!),
            ["KIT_PREFIX"] = LibraryMaintenanceKit.Options.Prefix
        };
        var run = await NodeRunner.RunAsync(node!, ["--test", Path.Combine(WebTestsFolder, "admin.test.mjs")], environment, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("# skipped 0", run.StandardOutput, StringComparison.Ordinal);

        // Every test of the file ran (a skipped file reports "# pass 0").
        var source = await File.ReadAllTextAsync(Path.Combine(WebTestsFolder, "admin.test.mjs"), TestContext.Current.CancellationToken);
        var declared = System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^test\(").Count;
        Assert.Contains("# pass " + declared.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", run.StandardOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/jf")]
    public async Task AdminScript_IsAnonymousJavaScript_WithTheMvidAsETag(string baseUrl)
    {
        await using var host = await StartAsync(baseUrl);
        var scripts = host.Services.GetRequiredService<EmbeddedScript>();

        using var response = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/AdminScript");
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var current = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/AdminScript", r => r.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag!.Tag));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("\"" + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N") + "\"", response.Headers.ETag?.Tag);
        Assert.Equal(scripts.AdminScriptText, new UTF8Encoding(false, true).GetString(body));
        Assert.Equal(HttpStatusCode.NotModified, current.StatusCode);
    }

    [Fact]
    public async Task TheAdminDiagnostics_AnswerAdministratorsOnly()
    {
        await using var host = await StartAsync(string.Empty);

        using var anonymous = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Admin/Diagnostics");
        using var user = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Admin/Diagnostics", r => r.Headers.Add(JellyfinAuth.CallerHeader, "user"));
        using var admin = await host.SendAsync(HttpMethod.Get, "/LibraryMaintenance/Admin/Diagnostics", r => r.Headers.Add(JellyfinAuth.CallerHeader, "admin"));
        using var body = JsonDocument.Parse(await admin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(BuildVersion, body.RootElement.GetProperty("PluginVersion").GetString());
        Assert.True(body.RootElement.GetProperty("Abi").GetProperty("Active").GetBoolean());
    }

    private static string Stub() => Resource("Jellyfin.Plugin.LibraryMaintenance.Configuration.LibraryMaintenanceJs.js");

    private static string Resource(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " isn't embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
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
                // The registrator's own descriptors for the kit, the script and the diagnostics.
                foreach (var descriptor in registered.Where(d => d.ServiceType == typeof(KitOptions) || d.ServiceType == typeof(KitAbiStatus)
                    || d.ServiceType == typeof(EmbeddedScript) || d.ServiceType == typeof(IEmbeddedScript)
                    || d.ServiceType.Namespace == "Jellyfin.Plugin.Kit.Diagnostics"))
                {
                    services.Add(descriptor);
                }

                // Diagnostics asks the index whether the caller sees every library: here they do.
                var index = new Mock<LibraryMaintenance.Duplicates.Index.IDuplicatesIndex>();
                index.Setup(i => i.SeesEveryLibrary(It.IsAny<Jellyfin.Database.Implementations.Entities.User?>())).Returns(true);
                var users = new Mock<MediaBrowser.Controller.Library.IUserManager>();
                users.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => new Jellyfin.Database.Implementations.Entities.User("admin", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = id });
                // The settings and Move's capabilities aren't asked for here.
                services.AddSingleton(Mock.Of<LibraryMaintenance.Move.IMoveMedia>());
                services.AddSingleton(Mock.Of<LibraryMaintenance.Settings.ILibraryMaintenanceSettings>());
                services.AddSingleton(index.Object);
                services.AddSingleton(users.Object);
            })
            .WithMvc(typeof(LibraryMaintenance.Api.LibraryMaintenanceController), typeof(LibraryMaintenance.Api.LibraryMaintenanceAdminController))
            .StartAsync();
    }
}
