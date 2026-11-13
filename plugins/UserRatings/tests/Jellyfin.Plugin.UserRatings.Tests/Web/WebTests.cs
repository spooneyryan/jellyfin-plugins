using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Web;

/// <summary>
/// User Ratings' web parts: the dashboard page with its four tabs, which renders every server value with
/// <c>textContent</c>, and the ClientScript the kit builds from <c>Web/js</c> (the strings, the anchors, the API wrappers,
/// the star widget, the details page's section, the chip and the Rate button, the start) and <c>Web/css</c>, checked with
/// node, the kit's generic ClientScript checks, the star widget's and the section's own tests in the kit's fake page
/// (<c>stars.test.mjs</c>, <c>details.test.mjs</c>), the anchor list and the coupling policy; and the AdminScript module
/// the kit builds from <c>Web/admin</c> (the Moderation tab, and the Settings tab in it too), with node, the kit's
/// AdminScript checks and its own tests (<c>moderation.test.mjs</c>, <c>settings.test.mjs</c>).
/// </summary>
public sealed partial class WebTests
{
    // The number of tests in details.test.mjs, so a test dropped by mistake (or skipped) fails here.
    private const int DetailsTestCount = 85;

    // The number of tests in moderation.test.mjs.
    private const int ModerationTestCount = 13;

    // The number of tests in settings.test.mjs.
    private const int SettingsTestCount = 19;

    // The number of tests in data.test.mjs (two more since 0.3.19).
    private const int DataTestCount = 30;

    private static readonly string StarsScriptTests = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "UserRatings", "tests", "Jellyfin.Plugin.UserRatings.Tests", "Web", "stars.test.mjs");

    private static readonly string DetailsScriptTests = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "UserRatings", "tests", "Jellyfin.Plugin.UserRatings.Tests", "Web", "details.test.mjs");

    private static readonly string ModerationScriptTests = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "UserRatings", "tests", "Jellyfin.Plugin.UserRatings.Tests", "Web", "moderation.test.mjs");

    private static readonly string SettingsTabTests = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "UserRatings", "tests", "Jellyfin.Plugin.UserRatings.Tests", "Web", "settings.test.mjs");

    private static readonly string DataTabTests = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "UserRatings", "tests", "Jellyfin.Plugin.UserRatings.Tests", "Web", "data.test.mjs");

    private static readonly string SettingsPageTests = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "UserRatings", "tests", "Jellyfin.Plugin.UserRatings.Tests", "Web", "configpage.test.mjs");

    private static readonly Lazy<EmbeddedScript> Scripts = new(() => new EmbeddedScript(UserRatingsKit.Options, NullLoggerFactory.Instance));

    [Fact]
    public void TheDashboardPage_FollowsTheDashboardRules()
    {
        var scratch = Directory.CreateTempSubdirectory("userratings-pages-");
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
    public void TheDashboardPage_HasItsFourTabs_InOrder_EachWithItsPanel()
    {
        var html = PageHtml();

        Assert.Contains("id=\"UserRatingsConfigPage\" data-role=\"page\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"tablist\" aria-label=\"User Ratings\"", html, StringComparison.Ordinal);
        var tabs = Tab().Matches(html).Select(m => (m.Groups["name"].Value, m.Groups["panel"].Value)).ToList();
        Assert.Equal(
            [("Settings", "UserRatingsSettings"), ("Moderation", "UserRatingsModeration"), ("Data", "UserRatingsData"), ("Diagnostics", "UserRatingsDiagnostics")],
            tabs);
        foreach (var (name, panel) in tabs)
        {
            Assert.Contains($"id=\"{panel}\" class=\"userRatingsTabPanel", html, StringComparison.Ordinal);
            Assert.Contains($"aria-labelledby=\"UserRatingsTab{name}\"", html, StringComparison.Ordinal);
        }

        Assert.Contains("var tabs = ['Settings', 'Moderation', 'Data', 'Diagnostics'];", html, StringComparison.Ordinal);
        // The settings are read and saved through GET and POST UserRatings/Admin/Settings (the Settings tab, the
        // AdminScript's), never as the whole configuration, which the server's checks and its version would not see.
        Assert.DoesNotContain("PluginConfiguration", html, StringComparison.Ordinal);
        Assert.Contains("ApiClient.getUrl('UserRatings/Admin/Diagnostics')", html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDashboardPagesScript_PassesNodeCheck()
    {
        var script = InlineScript().Match(PageHtml()).Groups["body"].Value;
        Assert.False(string.IsNullOrWhiteSpace(script));

        var run = await KitScriptChecks.CheckSyntaxAsync(script, module: false, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
    }

    [Fact]
    public void TheWebFiles_AreEmbeddedByTheKit()
    {
        var assembly = typeof(Plugin).Assembly;

        Assert.Equal(
            ["Web.js.00-strings.js", "Web.js.10-selectors.js", "Web.js.20-api.js", "Web.js.30-stars.js", "Web.js.40-details.js", "Web.js.50-chip.js", "Web.js.90-main.js"],
            EmbeddedScript.ResourceNames(assembly, EmbeddedScript.ScriptFolder, ".js"));
        Assert.Equal(["Web.css.userratings.css"], EmbeddedScript.ResourceNames(assembly, EmbeddedScript.CssFolder, ".css"));
        Assert.Equal(
            ["Web.admin.js.10-moderation.js", "Web.admin.js.20-settings.js", "Web.admin.js.30-data.js", "Web.admin.js.90-mount.js"],
            EmbeddedScript.ResourceNames(assembly, EmbeddedScript.AdminScriptFolder, ".js"));
        Assert.Equal(["Web.admin.css.data.css", "Web.admin.css.moderation.css", "Web.admin.css.settings.css"], EmbeddedScript.ResourceNames(assembly, EmbeddedScript.AdminCssFolder, ".css"));
        Assert.Contains(WebCouplingChecks.AnchorsResource, assembly.GetManifestResourceNames());
        Assert.Equal(
            ["Kit.kit.js"],
            assembly.GetManifestResourceNames().Where(n => n.EndsWith(".js", StringComparison.Ordinal)
                && !n.StartsWith(EmbeddedScript.ScriptFolder, StringComparison.Ordinal)
                && !n.StartsWith(EmbeddedScript.AdminScriptFolder, StringComparison.Ordinal)));
    }

    [Fact]
    public void TheClientScript_CarriesUserRatingsBuild()
    {
        var script = Scripts.Value.ClientScriptText;

        Assert.Contains("var KIT_BUILD = {\"plugin\":\"User Ratings\",\"prefix\":\"jfur\",\"route\":\"UserRatings\",\"version\":\"" + JellyfinLine.VersionOf(typeof(Plugin)) + "\",\"kit\":\"" + KitBuild.KitVersion + "\"", script, StringComparison.Ordinal);
        Assert.Contains("// ---- Web/js/00-strings.js\n", script, StringComparison.Ordinal);
        Assert.Contains("// ---- Web/js/30-stars.js\n", script, StringComparison.Ordinal);
        Assert.Contains("// ---- Web/js/90-main.js\n", script, StringComparison.Ordinal);
        // The name order is the load order: the strings and the anchors first, the start last.
        var order = new[] { "00-strings.js", "10-selectors.js", "20-api.js", "30-stars.js", "40-details.js", "50-chip.js", "90-main.js" }
            .Select(f => script.IndexOf("// ---- Web/js/" + f + "\n", StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Contains(".jfur-stars{touch-action:pan-y pinch-zoom;", script, StringComparison.Ordinal);
        Assert.Contains(".jfur-isolate{unicode-bidi:isolate}", script, StringComparison.Ordinal);
        Assert.Contains("@media (max-width:599px){.jfur-histogram{display:none}}", script, StringComparison.Ordinal);
        // The chip's and the Rate button's scanner after the section's, then the kit's TV fix (50-chip.js).
        Assert.Contains("kit.observe(syncDetailsSection);\nkit.observe(syncDetailsExtras);\nregisterReviewsTvFix();\n", script, StringComparison.Ordinal);
        Assert.Contains("@media (max-width:599px){.jfur-chip-word{display:none}}", script, StringComparison.Ordinal);
        Assert.Contains("window.__jfkit[KIT_BUILD.prefix] = ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__PFX__", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminScript_IsTheSettingsModerationAndDataTabsModule()
    {
        var script = Scripts.Value.AdminScriptText;

        Assert.NotNull(script);
        // One export, which mounts the Settings tab for { tab: 'Settings' }, the Data tab and its detached list for
        // { tab: 'Data' } and { tab: 'Detached' }, and the Moderation tab otherwise (90-mount.js, after them all).
        Assert.Contains("function mountModeration(view, params) {", script, StringComparison.Ordinal);
        Assert.Contains("function mountSettings(view) {", script, StringComparison.Ordinal);
        Assert.Contains("function mountData(view) {", script, StringComparison.Ordinal);
        Assert.Contains("function mountDetached(view) {", script, StringComparison.Ordinal);
        Assert.Contains("    return tab === 'Settings' ? mountSettings(view, params)\n        : tab === 'Data' ? mountData(view, params)\n        : tab === 'Detached' ? mountDetached(view, params)\n        : mountModeration(view, params);", script.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains(".jfur-data-snapshot,.jfur-data-user,.jfur-det-candidate{display:flex;flex-wrap:wrap", script, StringComparison.Ordinal);
        var order = new[] { "10-moderation.js", "20-settings.js", "30-data.js", "90-mount.js" }
            .Select(f => script!.IndexOf("// ---- Web/admin/js/" + f + "\n", StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Single(Regex.Matches(script!, @"^function mount\(", RegexOptions.Multiline));
        Assert.Contains(".jfur-set-choices,.jfur-set-checks{display:flex;flex-wrap:wrap;gap:.5em}", script, StringComparison.Ordinal);
        Assert.Contains("const KIT_BUILD = {\"plugin\":\"User Ratings\",\"prefix\":\"jfur\",\"route\":\"UserRatings\",\"version\":\"" + JellyfinLine.VersionOf(typeof(Plugin)) + "\"", script, StringComparison.Ordinal);
        Assert.Contains("const kit = createKit(KIT_BUILD, KIT_CSS, { styleId: KIT_BUILD.prefix + '-admin-styles'", script, StringComparison.Ordinal);
        Assert.Contains("function mount(view, params) {", script, StringComparison.Ordinal);
        Assert.EndsWith("export { mount };\n", script.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains(".jfur-mod-table td::before{content:attr(data-label)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__PFX__", script, StringComparison.Ordinal);
        Assert.DoesNotContain("window.__jfkit[KIT_BUILD.prefix] = ", script, StringComparison.Ordinal);

        // The settings page imports it once, for its Settings tab when the page is shown and its Moderation tab.
        Assert.Contains("import(ApiClient.getUrl('UserRatings/AdminScript'))", PageHtml(), StringComparison.Ordinal);
        Assert.Contains("var mounted = module.mount(root, { page: page, tab: name });", PageHtml(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheModerationLog_OffersAndNamesEveryActionTheServerLogs()
    {
        // The log's filter lists MOD_ACTIONS, and each row's action is shown in words: a server action missing from either
        // would be unfilterable, or shown as a string key.
        var script = Scripts.Value.AdminScriptText!.ReplaceLineEndings("\n");
        var list = Regex.Match(script, @"^const MOD_ACTIONS = \[(?<items>[^\]]*)\];$", RegexOptions.Multiline);
        Assert.True(list.Success, "MOD_ACTIONS not found");
        var offered = Regex.Matches(list.Groups["items"].Value, "'(?<a>[A-Za-z]+)'").Select(m => m.Groups["a"].Value).ToList();
        Assert.Equal(RatingValues.ModerationAction.All.Order(StringComparer.Ordinal), offered.Order(StringComparer.Ordinal));
        Assert.Equal(offered.Count, offered.Distinct(StringComparer.Ordinal).Count());
        Assert.All(offered, a => Assert.Contains("    'mod.action." + a + "': '", script, StringComparison.Ordinal));
    }

    [Fact]
    public void TheSettingsTab_HasEverySettingAsTheServerChecksIt()
    {
        // The tab's SET_FIELDS and its labels against Configuration/RatingsSettingsFields.cs: every setting the server
        // takes, its kind, a number's range and a name's values, and one label for each, which the server's 409 and the
        // tab's dialog both show; and every kind the server offers has a name in the tab.
        var script = Scripts.Value.AdminScriptText!.ReplaceLineEndings("\n");
        var block = Regex.Match(script, @"^const SET_FIELDS = \{\n(?<body>.*?)\n\};$", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(block.Success, "SET_FIELDS not found");
        var entries = Regex.Matches(block.Groups["body"].Value, @"^    (?<name>[A-Za-z]+): \{ type: '(?<type>[a-z]+)'(?<rest>[^}]*)\},?$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups["name"].Value, m => (Kind: m.Groups["type"].Value, Tail: m.Groups["rest"].Value), StringComparer.Ordinal);
        Assert.Equal(RatingsSettingsFields.Fields.Select(f => f.Name), entries.Keys);
        var groups = Regex.Matches(script, @"^    \{ key: '[a-z]+', fields: \[(?<fields>[^\]]*)\]", RegexOptions.Multiline)
            .SelectMany(m => Regex.Matches(m.Groups["fields"].Value, "'(?<f>[A-Za-z]+)'").Select(f => f.Groups["f"].Value))
            .ToList();
        Assert.Equal(RatingsSettingsFields.Fields.Select(f => f.Name), groups);
        foreach (var field in RatingsSettingsFields.Fields)
        {
            var (type, rest) = entries[field.Name];
            var expected = RatingsSettingsFields.TypeOf(field.Name) switch
            {
                var t when t == typeof(bool) => "bool",
                var t when t == typeof(int) => "number",
                var t when t == typeof(Guid[]) => "users",
                var t when t == typeof(string[]) => "kinds",
                _ => "one"
            };
            Assert.True(expected == type, field.Name + ": " + type);
            if (type == "number")
            {
                Assert.Equal($", min: {field.Min}, max: {field.Max} ", rest);
            }
            else if (type == "one")
            {
                Assert.Equal(", names: [" + string.Join(", ", field.Names!.Select(n => "'" + n + "'")) + "] ", rest);
            }

            Assert.Contains("    'set.f." + field.Name + "': '" + field.Label.Replace("'", "\\'", StringComparison.Ordinal) + "',\n", script, StringComparison.Ordinal);
            Assert.Contains("    'set.h." + field.Name + "': '", script, StringComparison.Ordinal);
        }

        Assert.Equal(RatingsSettingsFields.OfferedItemTypes, RatingsSettingsFields.KindNames.Keys);
        Assert.All(RatingsSettingsFields.OfferedItemTypes, kind => Assert.Matches("\n    'set\\.kind\\." + kind + "': '" + Regex.Escape(RatingsSettingsFields.KindNames[kind]) + "',?\n", script));
        Assert.Contains("    MirrorToUserData: { type: 'bool', locked: true }", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAdminScript_PassesNodeCheck_AndTheKitsAdminScriptChecks()
    {
        var syntax = await KitScriptChecks.CheckSyntaxAsync(Scripts.Value.AdminScriptText!, module: true, TestContext.Current.CancellationToken);
        Assert.SkipWhen(syntax is null, "Node.js is not on the PATH.");
        Assert.True(syntax!.ExitCode == 0, syntax.Output);

        var run = await KitScriptChecks.RunAdminScriptTestsAsync(Scripts.Value.AdminScriptText!, UserRatingsKit.Options.Prefix, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# pass 1\n", run.StandardOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// moderation.test.mjs: the Moderation tab as the AdminScript ships it, imported into
    /// the kit's fake page and mounted into a panel: nothing sent on import and mount, the first reads after it; every value
    /// as text, names and titles isolated, the authors' kinds and flags, detached rows; the filters, the search, the
    /// member's and the title's "Only" with their chips, Clear filters; paging; a list that can't be read; Remove… with its
    /// dry run first, its token, its reason, a stale token's 409, a 404, Cancel; the TV layout's D-pad and Back; the focus
    /// after a removal; the log with its action filter and paging; the 375 px styles.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheModerationTab_PassesItsTests_InAFakePage()
    {
        var run = await KitScriptChecks.RunTestFileAsync(ModerationScriptTests, Scripts.Value.AdminScriptText!, UserRatingsKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("# pass " + ModerationTestCount + "\n", run.StandardOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// data.test.mjs: the Data tab and its detached list as the AdminScript
    /// ships them, imported into the kit's fake page and mounted with <c>{ tab: 'Data' }</c> and <c>{ tab: 'Detached' }</c>:
    /// nothing sent on import and mount; the snapshots, the integrity banner (damaged, the last recovery, quarantines) and
    /// the unknown users as text; a restricted admin's 403; Export as a job polled every second, its file fetched with the
    /// session's header and saved; Import's dry run with the file as it is, its report, Import with the plan after a
    /// question, another option or file needing another dry run, Replace's words, the plan's 409 Changed, a file over
    /// 100 MB; Stop, and Stop refused while writing; a job running when the tab opens; Restore with its question, the TV
    /// layout's D-pad and Back, its report and a refusal; Remap by name and the deleted-user policy, a 503 and a 409 alike;
    /// the detached list as text with its candidates, paging and reasons; Relink, AlreadyRated, 404, Purge; the 375 px
    /// styles.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheDataTab_PassesItsTests_InAFakePage()
    {
        var run = await KitScriptChecks.RunTestFileAsync(DataTabTests, Scripts.Value.AdminScriptText!, UserRatingsKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("# pass " + DataTestCount + "\n", run.StandardOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// settings.test.mjs: the Settings tab as the AdminScript ships it, imported into the kit's fake
    /// page and mounted with <c>{ tab: 'Settings' }</c> against a scripted GET and POST <c>Admin/Settings</c> that checks the
    /// version as the server does: nothing sent on import and mount; all 32 settings in their groups and order, each with
    /// its label, help and default, the restart settings marked, 1.1's note, Mirror locked; every value as text, user names
    /// isolated, a gone user named by id, the server's problems; a switch, a choice and a number changed, only what changed
    /// sent, a dry run with the version then the save, read again; Undo; nothing changed; a number's message as it is typed,
    /// aria-invalid, the focus on the first setting to fix; the users picker for "Only the users chosen below" and nobody
    /// chosen; the kinds and none chosen; the consequences dialog (Cancel focused, the focus back on Save); a restart's
    /// notice; the 409 with Reload; a server's InvalidSetting under its setting; an unknown saved name; a read that fails
    /// with Try again; the TV layout's D-pad and Back, no select; the 375 px styles.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheSettingsTab_PassesItsTests_InAFakePage()
    {
        var run = await KitScriptChecks.RunTestFileAsync(SettingsTabTests, Scripts.Value.AdminScriptText!, UserRatingsKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("# pass " + SettingsTestCount + "\n", run.StandardOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWebFiles_FollowTheCouplingPolicy()
    {
        Assert.Empty(WebCouplingChecks.CheckPluginScripts(typeof(Plugin).Assembly));
        Assert.Empty(WebCouplingChecks.CheckPluginStyles(typeof(Plugin).Assembly));
    }

    [Fact]
    public void TheAnchorList_IsTheDetailsSectionsSet()
    {
        // The verified list for the details page: the section uses three of its anchors, the chip and the Rate button
        // miscInfo, detailButtons and detailButtonsBefore (an anchor listed but unused fails CheckAnchors). Jellyfin
        // Enhanced's Reviews are the kit's (kit.je.reviews), not in this list.
        var assembly = typeof(Plugin).Assembly;
        var catalog = WebAnchorCatalog.Load(assembly, assembly);

        Assert.Empty(catalog.Problems);
        Assert.Empty(WebCouplingChecks.CheckAnchors(assembly));
        var own = catalog.Anchors.Where(a => !a.Name.StartsWith(WebAnchorCatalog.KitNamePrefix, StringComparison.Ordinal)).ToList();
        Assert.Equal(["detailPage", "detailContent", "sectionBefore", "miscInfo", "detailButtons", "detailButtonsBefore"], own.Select(a => a.Name));
        Assert.Equal(["#itemDetailPage:not(.hide)", ".itemDetailPage:not(.hide)"], own[0].Css);
        Assert.Equal([".detailPageSecondaryContainer .detailPageContent", ".detailPageSecondaryContainer", ".detailPageContent", ".detailPagePrimaryContent .detailSection"], own[1].Css);
        Assert.Equal(["#castCollapsible", "#guestCastCollapsible", "#similarCollapsible"], own[2].Css);
        Assert.Equal([".itemMiscInfo-primary", ".itemMiscInfo"], own[3].Css);
        Assert.Equal([".mainDetailButtons"], own[4].Css);
        Assert.Equal([".btnMoreCommands"], own[5].Css);
        Assert.Equal([true, true, false, false, false, false], own.Select(a => a.Required));
        Assert.All(own, a => Assert.Equal(["details"], a.Views));
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
        var run = await KitScriptChecks.RunClientScriptTestsAsync(Scripts.Value.ClientScriptText, UserRatingsKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# pass 3", run.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// stars.test.mjs: the star widget of <c>30-stars.js</c> as the ClientScript
    /// ships it: the pointer mapping (the middle of star 3 is 2.5, every boundary, right to left), every key in every
    /// layout, the touch sequences, the slider's markup and aria values, mouse, keyboard, TV, touch and the − / +
    /// buttons in the DOM, a failed save, the disabled state with the page's reason, the read-only stars, bidi isolation
    /// and the styles; a setValue during a save, a pen lifted off the stars and a lost capture, one save at a time,
    /// setDisabled during a save, the 1e-9 margin, the note read once and an inert widget after destroy.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheStarWidget_PassesItsTests_InAFakePage()
    {
        var run = await KitScriptChecks.RunTestFileAsync(StarsScriptTests, Scripts.Value.ClientScriptText, UserRatingsKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("# pass 24", run.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// details.test.mjs (its pure functions and the section in the DOM): the section of
    /// <c>40-details.js</c> as the ClientScript ships it, in the kit's fake page: its state from the API's answers, the
    /// summary, the histogram and the copies' hint, paging and the orders, every answer's meaning (403, 404,
    /// 409, 400, 503, no answer, <c>IsPartial</c>, Load more's 403), the writes in order through one queue, Clear and
    /// Delete with their questions, the review form, item to item, a restored view, the slider's hand-off on leave, the TV
    /// layout's row, bidi isolation and text only. Then the chip (its texts and name,
    /// put back after jellyfin-web writes the row, an empty row un-hidden and hidden again, off or without an average) and
    /// the Rate button (ShowDetailButton Auto by the item's type and Jellyfin Enhanced's Reviews, Always, Never; before
    /// More; Rate's expand, focus and scroll; a missing buttons row reported once; one of each item to item; the TV
    /// layout's focus fix of JE's closed Reviews). An admin's Remove… on the other members'
    /// rows (never on their own, never for a member; the chip and the Rate button as they were, in the default, experimental,
    /// TV and Jellyfin Enhanced layouts), its dialog's dry run, token and reason, a stale token's 409, a 404, Cancel, the
    /// TV layout's D-pad and Back, and the focus after a removal.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheDetailsSection_PassesItsTests_InAFakePage()
    {
        var run = await KitScriptChecks.RunTestFileAsync(DetailsScriptTests, Scripts.Value.ClientScriptText, UserRatingsKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("# pass " + DetailsTestCount + "\n", run.StandardOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// configpage.test.mjs: the settings page built from
    /// the real markup of <c>Configuration/configPage.html</c> in the kit's fake page, with its inline script and
    /// Diagnostics answers: the kit's Notice (a recovery that didn't finish, its files left in a quarantine folder while the
    /// database in place is used as it is) is a warning banner above the tabs, as text, on the Settings tab too; beside the
    /// database's own sentence when it isn't Ok; gone when the next read has none. The Ambiguous banner, ended
    /// by opening the Data tab (one POST to <c>Detached/Seen</c>, none when the admin left the tab first, the banner kept
    /// when it fails), and Data → Detached &amp; ambiguous with the counts by reason and the ratings kept on items that are
    /// gone, ids only, as text, with "not checked since the server started", the mass-detach note and
    /// Detach now (one POST to <c>Held/Detach</c> after a confirmation, none when cancelled, a refusal's reason as text);
    /// a list of more than 1,000 kept items in parts of 1,000, one after another, their answers added up.
    /// The Moderation tab imports the AdminScript module the first time it is opened, and says so when it can't;
    /// the Settings tab too, when the page is shown, one import for both, each tab read again when opened again.
    /// The Diagnostics tab counts the detached Jellyfin Enhanced reviews still under a 0.3.21 season or episode key.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheSettingsPage_ShowsTheKitsNotice_AsABanner_InAFakePage()
    {
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        var run = await NodeRunner.RunAsync(node!, ["--test", SettingsPageTests], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("# pass 20\n", run.StandardOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    private static string PageHtml()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.UserRatings.Configuration.configPage.html")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"<script type=""text/javascript"">(?<body>.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"role=""tab"" id=""UserRatingsTab(?<name>\w+)"" class=""userRatingsTab"" data-tab=""\k<name>"" aria-controls=""(?<panel>\w+)""")]
    private static partial Regex Tab();
}
