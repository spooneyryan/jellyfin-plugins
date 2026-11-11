using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.AccessRoles.Web;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Web;

/// <summary>
/// Access Roles' web parts: the dashboard page, since 0.9.8 the AdminScript module
/// its <c>AccessRolesJs</c> stub mounts (node's syntax check, the kit's AdminScript checks and the page's flows in the
/// kit's fake page, <c>admin.test.mjs</c>), which renders every server value with <c>textContent</c>, and since 0.9.6
/// the web guard: the ClientScript built from <c>Web/js</c> (node's syntax check,
/// the kit's generic checks, the coupling policy and the guard's own tests in the kit's fake page,
/// <c>accessroles.test.mjs</c>) and the head fragment (its CSS from <c>Web/anchors.json</c>, its script, its flags; the head
/// script's tests, <c>head.test.mjs</c>).
/// </summary>
public sealed class WebTests
{
    private static readonly Lazy<EmbeddedScript> Scripts = new(() => new EmbeddedScript(AccessRolesKit.Options, NullLoggerFactory.Instance));

    private static readonly string WebTestsFolder = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "plugins", "AccessRoles", "tests", "Jellyfin.Plugin.AccessRoles.Tests", "Web");

    [Fact]
    public void TheDashboardPage_FollowsTheDashboardRules()
    {
        var scratch = Directory.CreateTempSubdirectory("accessroles-pages-");
        try
        {
            var plugin = (Plugin)PluginIdentityChecks.CreatePlugin(typeof(Plugin), scratch.FullName);

            Assert.Empty(DashboardPageChecks.Check(typeof(Plugin).Assembly, plugin.GetPages()));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheDashboardPage_IsTheAdminScriptsSkeleton_WithNoScriptOfItsOwn()
    {
        // The kit's dashboard page rules: the page's controller is the AccessRolesJs stub; the page is the build note, the loading note
        // and the root the module mounts into.
        var html = PageHtml();

        Assert.Contains("id=\"AccessRolesConfigPage\" data-role=\"page\" class=\"page type-interior pluginConfigurationPage\" data-controller=\"__plugin/AccessRolesJs\"", html, StringComparison.Ordinal);
        Assert.Contains("Settings locks, the admin surfaces and the server-wide hardening are enforced, and library roles are written to users' library access.", html, StringComparison.Ordinal);
        Assert.Contains("<p id=\"AccessRolesScriptNote\">Loading the page…</p>", html, StringComparison.Ordinal);
        Assert.Contains("<div id=\"AccessRolesRoot\"></div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-require", html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
        Assert.DoesNotContain("${", html, StringComparison.Ordinal);

        var stub = StubJs();
        Assert.Contains("export default function AccessRolesPage(view, params) {", stub, StringComparison.Ordinal);
        Assert.Contains("import(ApiClient.getUrl('AccessRoles/AdminScript')).then(function (module) {", stub, StringComparison.Ordinal);
        Assert.Contains("module.mount(view, params || {});", stub, StringComparison.Ordinal);
        Assert.Contains("failed('The Access Roles page could not load its script.", stub, StringComparison.Ordinal);
        Assert.Contains("failed('The Access Roles page could not start.", stub, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", stub, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminScript_IsBuiltFromWebAdmin_InNameOrder_AndExportsMount()
    {
        var assembly = typeof(Plugin).Assembly;
        var script = Scripts.Value.AdminScriptText;

        Assert.NotNull(script);
        Assert.Equal(
            [
                "Web.admin.js.10-common.js", "Web.admin.js.20-api.js", "Web.admin.js.30-overview.js", "Web.admin.js.40-preview.js", "Web.admin.js.50-roles.js", "Web.admin.js.60-users.js",
                "Web.admin.js.70-panel.js", "Web.admin.js.75-libraries.js", "Web.admin.js.80-audit.js", "Web.admin.js.82-settings.js", "Web.admin.js.84-data.js", "Web.admin.js.86-diagnostics.js",
                "Web.admin.js.88-inert.js",
                "Web.admin.js.90-mount.js",
            ],
            EmbeddedScript.ResourceNames(assembly, EmbeddedScript.AdminScriptFolder, ".js"));
        Assert.Equal(["Web.admin.css.accessroles-admin.css"], EmbeddedScript.ResourceNames(assembly, EmbeddedScript.AdminCssFolder, ".css"));
        Assert.Contains("// ---- Web/admin/js/90-mount.js\n", script, StringComparison.Ordinal);
        Assert.EndsWith("export { mount };\n", script, StringComparison.Ordinal);
        Assert.Contains(".jfar-page-tab{", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__PFX__", script, StringComparison.Ordinal);

        // The flows' requests: every change is previewed first as its own write's dry run, so the
        // page never calls POST Preview.
        Assert.DoesNotContain("'Preview'", script, StringComparison.Ordinal);
        Assert.Contains("return arApi('POST', 'Users/Bulk', { body: body, query: query });", script, StringComparison.Ordinal);
        Assert.Contains("return arApi('POST', 'Users/' + arId(userId) + '/Unmanage', { body: body, query: query });", script, StringComparison.Ordinal);
        Assert.Contains("return arApi('PUT', 'DefaultRoles', { body: { RoleIds: roleIds }, query: query });", script, StringComparison.Ordinal);

        // The other tabs: the library lifecycle's two writes, the audit and its export, the settings' save (each previewed as its
        // own dry run too) and the per-user sync.
        Assert.Contains("return arApi('POST', 'Libraries/' + arId(libraryId) + '/GrantAgain', { body: {}, query: query });", script, StringComparison.Ordinal);
        Assert.Contains("return arApi('POST', 'Libraries/' + arId(libraryId) + '/Purge', { body: {}, query: query });", script, StringComparison.Ordinal);
        Assert.Contains("return arApi('GET', 'Audit', { query: query });", script, StringComparison.Ordinal);
        Assert.Contains("return arApi('GET', 'Audit/Export', { query: query, raw: true });", script, StringComparison.Ordinal);
        Assert.Contains("return arApi('PUT', 'Settings', { body: body, query: query });", script, StringComparison.Ordinal);
        Assert.Contains("return arApi('POST', 'Sync', { body: {}, query: query });", script, StringComparison.Ordinal);
        Assert.Contains("// ---- Web/admin/js/86-diagnostics.js\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDashboardStub_AndTheAdminScript_PassNodeCheck_AsModules()
    {
        var stub = await KitScriptChecks.CheckSyntaxAsync(StubJs(), module: true, TestContext.Current.CancellationToken);
        Assert.SkipWhen(stub is null, "Node.js is not on the PATH.");
        Assert.True(stub!.ExitCode == 0, stub.Output);

        var admin = await KitScriptChecks.CheckSyntaxAsync(Scripts.Value.AdminScriptText!, module: true, TestContext.Current.CancellationToken);
        Assert.True(admin!.ExitCode == 0, admin.Output);
    }

    [Fact]
    public async Task TheAdminScript_PassesTheKitsAdminScriptChecks()
    {
        // The kit's check gives the module the fake page's fetch while it runs, so the
        // module is checked as it is built.
        var run = await KitScriptChecks.RunAdminScriptTestsAsync(Scripts.Value.AdminScriptText!, AccessRolesKit.Options.Prefix, cancellationToken: TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# pass 1", run.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// admin.test.mjs: the page's flows in the kit's fake page: the tabs and their keys on
    /// a keyboard and the TV layout, the banners and read-only data, the presets and the Dashboard row, the preview (each
    /// write's own dry run) with its choice and blocking warnings, deleting with reassignment, the default roles, the
    /// users' badges and bulk actions, the per-user panel with its sources, drift's Keep and Revert all drifted users,
    /// Unmanage, Enter on checkboxes, text-only rendering, a failed load and viewshow; since 0.9.9 a late dry run
    /// dropped, a 409 Changed with Reload and keep my changes, the hidden library lists not sent, the remembered tab,
    /// isolated names and the stub's errors; since 0.9.11 the Overview's chips and server-wide box, the Libraries, Audit,
    /// Settings, Data and Diagnostics tabs, the joined rule names and the per-user Revert.
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
            ["KIT_PREFIX"] = AccessRolesKit.Options.Prefix
        };
        var run = await NodeRunner.RunAsync(node!, ["--test", Path.Combine(WebTestsFolder, "admin.test.mjs")], environment, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("# pass 0", run.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWebFiles_AreEmbeddedByTheKit_InP09sLayout()
    {
        var assembly = typeof(Plugin).Assembly;
        var script = Scripts.Value.ClientScriptText;

        Assert.Equal(
            ["Web.js.00-strings.js", "Web.js.10-selectors.js", "Web.js.20-api.js", "Web.js.30-rules.js", "Web.js.40-guard.js", "Web.js.50-menu.js", "Web.js.55-nolibraries.js", "Web.js.60-admin-notices.js", "Web.js.90-main.js"],
            EmbeddedScript.ResourceNames(assembly, EmbeddedScript.ScriptFolder, ".js"));
        Assert.Equal(["Web.css.accessroles.css"], EmbeddedScript.ResourceNames(assembly, EmbeddedScript.CssFolder, ".css"));
        Assert.Contains("\"prefix\":\"jfar\"", script, StringComparison.Ordinal);
        Assert.Contains("window.__jfkit[KIT_BUILD.prefix] = ", script, StringComparison.Ordinal);
        Assert.Contains("// ---- Web/js/90-main.js\n", script, StringComparison.Ordinal);
        Assert.Contains(".jfar-note{", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__PFX__", script, StringComparison.Ordinal);
        Assert.NotNull(Scripts.Value.AdminScriptText);
    }

    [Fact]
    public void TheWebFiles_FollowTheCouplingPolicy_AndDeclareTheirAnchors()
    {
        Assert.Empty(WebCouplingChecks.CheckPluginScripts(typeof(Plugin).Assembly));
        Assert.Empty(WebCouplingChecks.CheckPluginStyles(typeof(Plugin).Assembly));
        Assert.Empty(WebCouplingChecks.CheckAnchors(typeof(Plugin).Assembly));
        Assert.Empty(WebAnchorCatalog.Load(typeof(Plugin).Assembly, typeof(WebAnchorCatalog).Assembly).Problems);
    }

    [Fact]
    public async Task TheClientScript_PassesTheKitsClientScriptChecks()
    {
        var run = await KitScriptChecks.RunClientScriptTestsAsync(Scripts.Value.ClientScriptText, AccessRolesKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# pass 3", run.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// accessroles.test.mjs: the web guard in the kit's fake page: the path normaliser, the
    /// first-load rule, the redirect rule, the click blocker, a sign-in without a reload, /Me failing or never answering, the
    /// Settings page's note, the MUI menu's keyboard fix, the Display (account) fields, the no-libraries text, a merged save's
    /// toast and the administrators' banners. head.test.mjs: the head script, and that it sets the classes the ClientScript's
    /// rule gives.
    /// </summary>
    /// <param name="file">The test file.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("accessroles.test.mjs")]
    [InlineData("head.test.mjs")]
    public async Task TheWebGuard_PassesItsTests_InAFakePage(string file)
    {
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        using var temp = new TempDirectory();
        var environment = new Dictionary<string, string>
        {
            ["KIT_CLIENT_SCRIPT"] = temp.CreateFile("client-script.js", Scripts.Value.ClientScriptText),
            ["KIT_PREFIX"] = AccessRolesKit.Options.Prefix,
            ["JFAR_HEAD_SCRIPT"] = temp.CreateFile("head-script.js", AccessRolesHead.Script)
        };
        var run = await NodeRunner.RunAsync(node!, ["--test", Path.Combine(WebTestsFolder, file)], environment, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("# pass 0", run.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHeadScript_PassesNodeCheck()
    {
        var run = await KitScriptChecks.CheckSyntaxAsync(AccessRolesHead.Script, module: false, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
    }

    [Fact]
    public void TheHeadsCss_HidesEveryLockedEntryPoint_FromTheDeclaredAnchors_OneRulePerSelector()
    {
        var catalog = WebAnchorCatalog.Load(typeof(Plugin).Assembly, typeof(WebAnchorCatalog).Assembly);
        var css = AccessRolesHead.Css;

        Assert.Equal(css, AccessRolesHead.BuildCss(catalog));
        foreach (var (classes, anchor) in AccessRolesHead.HideRules)
        {
            foreach (var selector in catalog.Find(anchor)!.Css)
            {
                Assert.Contains("html." + classes + " " + selector + "{display:none!important}\n", css, StringComparison.Ordinal);
            }
        }

        Assert.Contains("html.jfar-lock-profile #app-user-menu a[href^=\"#/userprofile\"]{display:none!important}", css, StringComparison.Ordinal);
        Assert.Contains("html.jfar-lock-allsettings .mainDrawer .btnSettings{display:none!important}", css, StringComparison.Ordinal);
        Assert.Contains("html.jfar-pending #myPreferencesMenuPage .lnkHomePreferences{visibility:hidden!important}", css, StringComparison.Ordinal);
        Assert.Contains("html.jfar-blocking .page.userPreferencesPage:not(#userPluginPreferencesPage){visibility:hidden!important}", css, StringComparison.Ordinal);
        Assert.Contains("html.jfar-lock-allsettings.jfar-lock-profile #app-user-menu ul:not(:has(> #jellyfinEnhancedUserMenuLink)) > hr:first-of-type{display:none!important}", css, StringComparison.Ordinal);

        // Display (account) hides nothing (its fields are disabled), and Sign out is never hidden.
        Assert.DoesNotContain("jfar-lock-displayaccount", css, StringComparison.Ordinal);
        Assert.DoesNotContain("btnLogout", css, StringComparison.Ordinal);
        Assert.DoesNotContain("<", css, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => AccessRolesHead.BuildCss(WebAnchorCatalog.Parse(null, "{\"anchors\":[]}")));
    }

    [Fact]
    public void TheHeadFragment_CarriesItsMarker_TheCss_TheScript_AndOnlyServerWideFlags()
    {
        using var temp = new TempDirectory();
        using var services = HeadServices(temp, new PluginConfiguration { FirstLoadMode = "ShowUntilKnown", BlockedNavigationNotice = "Silent", ShowManagedNoticeOnUserPages = false });

        var fragment = AccessRolesHead.Render(services)!;

        Assert.StartsWith("<style data-jfar-head>" + AccessRolesHead.Css + "</style><script data-jfar-head>window.__jfarHead=", fragment, StringComparison.Ordinal);
        Assert.EndsWith(AccessRolesHead.Script + "</script>", fragment, StringComparison.Ordinal);
        Assert.Contains("window.__jfarHead={\"anyManaged\":false,\"enforced\":true,\"firstLoad\":\"ShowUntilKnown\",\"notice\":\"Silent\",\"userPages\":false};", fragment, StringComparison.Ordinal);
        Assert.NotNull(AccessRolesKit.Options.HeadFragment);
    }

    [Fact]
    public async Task TheHeadFlags_AnyManaged_EnforcementOffOrSafeMode_AndAStoreThatCantBeRead()
    {
        using var temp = new TempDirectory();
        using var managed = HeadServices(temp, new PluginConfiguration());
        var store = managed.GetRequiredService<AccessRolesStore>();
        await store.UpdateAsync(d => d with { Assignments = [new RoleAssignment { UserId = Guid.NewGuid(), State = AccessRolesAssignmentState.Managed, UpdatedUtc = DateTime.UtcNow }] }, TestContext.Current.CancellationToken);
        Assert.Equal(true, AccessRolesHead.Flags(managed)["anyManaged"]);

        using var off = new TempDirectory();
        using var offServices = HeadServices(off, new PluginConfiguration { EnforcementEnabled = false });
        Assert.Equal(false, AccessRolesHead.Flags(offServices)["enforced"]);
        using var safe = new TempDirectory();
        using var safeServices = HeadServices(safe, new PluginConfiguration());
        Directory.CreateDirectory(safeServices.GetRequiredService<AccessRolesPaths>().Root);
        await File.WriteAllTextAsync(safeServices.GetRequiredService<AccessRolesPaths>().SafeModeFile, string.Empty, TestContext.Current.CancellationToken);
        Assert.Equal(false, AccessRolesHead.Flags(safeServices)["enforced"]);

        // A store that can't be trusted counts everyone as possibly managed (fail closed), and without any service nothing
        // throws: the safe side.
        using var broken = new TempDirectory();
        using var brokenServices = HeadServices(broken, new PluginConfiguration());
        Directory.CreateDirectory(brokenServices.GetRequiredService<AccessRolesPaths>().Root);
        await File.WriteAllTextAsync(brokenServices.GetRequiredService<AccessRolesPaths>().StoreFile, "{ not json", TestContext.Current.CancellationToken);
        Assert.Equal(true, AccessRolesHead.Flags(brokenServices)["anyManaged"]);
        using var none = new ServiceCollection().BuildServiceProvider();
        Assert.Contains("\"anyManaged\":true,\"enforced\":true", AccessRolesHead.Render(none), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClientScript_PassesNodeCheck()
    {
        var run = await KitScriptChecks.CheckSyntaxAsync(Scripts.Value.ClientScriptText, module: false, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
    }

    private static ServiceProvider HeadServices(TempDirectory temp, PluginConfiguration configuration)
    {
        var paths = AccessRolesPaths.ForRoot(temp.Path);
        var services = new ServiceCollection();
        services.AddSingleton<IPluginConfigurationProvider>(new FakeConfigurationProvider(() => configuration));
        services.AddSingleton(paths);
        services.AddSingleton(_ => AccessRolesStore.Create(paths.StoreFile, NullLogger.Instance));
        services.AddSingleton(s => new EffectiveRulesCache(s.GetRequiredService<AccessRolesStore>()));
        return services.BuildServiceProvider();
    }

    private static string PageHtml() => Resource("Jellyfin.Plugin.AccessRoles.Configuration.configPage.html");

    private static string StubJs() => Resource("Jellyfin.Plugin.AccessRoles.Configuration.AccessRolesJs.js");

    private static string Resource(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
