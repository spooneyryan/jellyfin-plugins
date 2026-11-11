using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web;

/// <summary>
/// kit.js and kit.css follow the coupling policy and the theming rules, and the kit's JavaScript unit tests
/// (kit/web/tests) pass under Node.
/// </summary>
public sealed class KitScriptConventionTests
{
    private static readonly string Web = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "kit", "web");

    private static string KitJs => File.ReadAllText(Path.Combine(Web, "kit.js"));

    private static string KitCss => File.ReadAllText(Path.Combine(Web, "kit.css"));

    [Fact]
    public void KitJs_NamesJellyfinWebOnlyInItsTwoTables()
    {
        Assert.Empty(WebCouplingChecks.CheckKitScript(KitJs));
    }

    [Fact]
    public void KitJs_NativeClasses_AreTheOnesTheHelpersNeed()
    {
        var classes = WebCouplingChecks.NativeClasses(KitJs);

        foreach (var name in new[] { "card", "cardBox", "cardScalable", "cardImageContainer", "listItem", "paperList", "button-submit", "raised", "emby-input", "dialog", "material-icons" })
        {
            Assert.Contains(name, classes);
        }
    }

    [Theory]
    [InlineData("const x = 'cardBox';", "cardBox")]
    [InlineData("const x = '.itemDetailPage .foo';", "itemDetailPage")]
    [InlineData("const x = a?.b;", "optional chaining")]
    [InlineData("const x = a ?? b;", "??")]
    [InlineData("node.innerHTML = t;", "innerHTML")]
    [InlineData("insertAdjacentHTML('beforeend', t);", "insertAdjacentHTML")]
    [InlineData("start();", "top-level statement")]
    public void CheckKitScript_FindsViolationsOutsideTheTables(string code, string expected)
    {
        var problems = WebCouplingChecks.CheckKitScript(KitJs + "\n" + code + "\n");

        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void CheckKitScript_FindsAnExtraTopLevelName_AndMissingTables()
    {
        Assert.Contains(WebCouplingChecks.CheckKitScript(KitJs + "\nfunction helper() {\n}\n"), p => p.Contains("'helper'", StringComparison.Ordinal));
        Assert.NotEmpty(WebCouplingChecks.CheckKitScript("function createKit() {\n}\n"));
    }

    [Fact]
    public void KitJs_CommentsAndStringsMayMentionThingsFreely()
    {
        // Comments are skipped, and a class name inside a table stays allowed.
        Assert.Empty(WebCouplingChecks.CheckKitScript(KitJs + "\n// cardBox, innerHTML and a?.b in a comment\n"));
    }

    [Fact]
    public void KitCss_StylesOnlyKitClasses_AndNeverTheMuiSurfaceOrTextVariables()
    {
        Assert.Empty(WebCouplingChecks.CheckKitCss(KitCss));
        Assert.Contains("var(--jf-palette-primary-main,#00a4dc)", KitCss, StringComparison.Ordinal);
        Assert.Contains(WebCouplingChecks.CheckKitCss(KitCss + ".cardBox{margin:0}"), p => p.Contains(".cardBox", StringComparison.Ordinal));
        Assert.Contains(WebCouplingChecks.CheckKitCss(KitCss + ".__PFX__-x{background:var(--jf-palette-background-paper)}"), p => p.Contains("--jf-palette-background-", StringComparison.Ordinal));
        Assert.Contains(WebCouplingChecks.CheckCss("x.css", "a{color:var(--jf-palette-text-primary)}"), p => p.Contains("x.css", StringComparison.Ordinal));
        Assert.Contains(WebCouplingChecks.CheckCss("x.css", "a{color:var(--jf-palette-primary-contrastText)}"), p => p.Contains("contrastText", StringComparison.Ordinal));
    }

    [Fact]
    public void KitCss_HoldsTheKitsFieldsToTheirContainersWidth()
    {
        // A <select> is as wide as its longest option unless held: on a 375 px phone Access Roles' Settings tab's selects
        // were 411 to 731 px wide, past the screen (moved into the kit for Access Roles, so every plugin's fields fit).
        var rule = Regex.Match(KitCss, @"\.__PFX__-input,\.__PFX__-textarea,\.__PFX__-select\{(?<body>[^}]*)\}", RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.True(rule.Success, "the fields' rule is in kit.css");
        Assert.Contains("max-width:100%", rule.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("box-sizing:border-box", rule.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckPluginScripts_AcceptsKitTestsOwnFixtures()
    {
        Assert.Empty(WebCouplingChecks.CheckPluginScripts(typeof(KitScriptConventionTests).Assembly));
        Assert.Empty(WebCouplingChecks.CheckPluginStyles(typeof(KitScriptConventionTests).Assembly));
    }

    [Theory]
    [InlineData("Web.js.20-x.js", "document.querySelector('.videoOsdBottom .buttons');", "anchor 'videoOsdBottom' belongs in Web/anchors.json")]
    [InlineData("Web.js.20-x.js", "node.className = 'cardBox';", "comes from the kit's markup helpers")]
    [InlineData("Web.js.20-x.js", "node.classList.add('hide');", "'hide'")]
    [InlineData("Web.js.20-x.js", "node.innerHTML = html;", "innerHTML")]
    [InlineData("Web.js.20-x.js", "var kit = null;", "declared by the ClientScript itself")]
    [InlineData("Web.js.20-x.js", "const y = a?.b;", "optional chaining")]
    [InlineData("Web.js.main.js", "kit.start();", "named NN-name.js")]
    [InlineData("Web.admin.js.20-x.js", "const n = '#itemDetailPage';", "anchor 'itemDetailPage' belongs in Web/anchors.json")]
    // 10-selectors.js is no longer exempt: it names anchors, never selectors, denylisted names or native classes.
    [InlineData("Web.js.10-selectors.js", "const X = '.newNativeRow';", "'.newNativeRow' (in \".newNativeRow\") is a CSS selector")]
    [InlineData("Web.js.10-selectors.js", "const X = 'div.newNativeRow > button';", "'.newNativeRow' (in \"div.newNativeRow > button\") is a CSS selector")]
    [InlineData("Web.js.10-selectors.js", "const X = '#newNativePage';", "'#newNativePage' (in \"#newNativePage\") is a CSS selector")]
    [InlineData("Web.js.10-selectors.js", "const X = 'button[data-native-action=\"x\"]';", "'[data-native-action=' (in \"button[data-native-action=\"x\"]\") is a CSS selector")]
    [InlineData("Web.js.10-selectors.js", "const X = '[hidden]';", "'[hidden]' (in \"[hidden]\") is a CSS selector")]
    [InlineData("Web.js.10-selectors.js", "const PAGE = 'itemDetailPage';", "anchor 'itemDetailPage' belongs in Web/anchors.json")]
    [InlineData("Web.js.10-selectors.js", "const C = 'cardBox';", "comes from the kit's markup helpers")]
    public void CheckPluginScripts_FindsViolations(string resource, string code, string expected)
    {
        var plugin = Fixture().With(resource, code);

        Assert.Contains(WebCouplingChecks.CheckPluginScripts(plugin), p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void CheckPluginScripts_ARawSelectorInTheSelectorsFile_UsedElsewhereThroughAConstant_IsFound()
    {
        // Neither the selector nor its class is on a denylist, so only the selectors file's own rule finds it; through
        // kit.anchors it would be declared, checked and reported.
        var plugin = Fixture()
            .With("Web.js.10-selectors.js", "const NEW_ROW = '.newNativeRow';")
            .With("Web.js.50-detail.js", "function row() { return document.querySelector(NEW_ROW); }");

        var problem = Assert.Single(WebCouplingChecks.CheckPluginScripts(plugin));

        Assert.StartsWith("Web/js/10-selectors.js line 1: '.newNativeRow'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckPluginScripts_AllowsAnchorNamesRoutesAndKitAnchorsInTheSelectorsFile_AndPlainWordsInsideText()
    {
        var plugin = Fixture()
            .With("Web.js.10-selectors.js", "const VIEWS = { details: { route: '/details', hash: '#/details', anchors: ['detailPage', { name: 'detailButtons', within: 'detailPage' }] } };\nconst DRAWER = kit.anchors.find('kit.drawer');\nconst WEB = '10.11.5';")
            .With("Web.js.20-text.js", "const message = 'Hide the card of this dialog';");

        Assert.Empty(WebCouplingChecks.CheckPluginScripts(plugin));
    }

    [Fact]
    public void CheckPluginScripts_NeedsWebJsFiles_AndCheckPluginStyles_FindsForbiddenVariables()
    {
        Assert.NotEmpty(WebCouplingChecks.CheckPluginScripts(new ResourceAssembly()));
        var styled = Fixture().With("Web.css.10-x.css", ".jfkt-x{background:var(--jf-palette-background-default)}");

        Assert.Single(WebCouplingChecks.CheckPluginStyles(styled));
    }

    [Fact]
    public void CheckAnchors_AcceptsKitTestsOwnList_WhichTheKitsParserAcceptsToo()
    {
        Assert.Empty(WebCouplingChecks.CheckAnchors(typeof(KitScriptConventionTests).Assembly));
        Assert.Empty(WebAnchorCatalog.Load(typeof(KitScriptConventionTests).Assembly, typeof(KitScriptConventionTests).Assembly).Problems);
    }

    [Fact]
    public void CheckAnchors_FindsAMissingList_AnUnusedAnchor_AndABrokenEntry()
    {
        const string List = """{ "anchors": [ { "name": "used", "css": [".usedWidget"], "required": true, "views": ["v"], "note": "n" }, { "name": "unused", "css": [".x"], "required": false, "views": ["v"], "note": "n" }, { "name": "kit.mine", "css": [".y"], "required": false, "views": ["v"], "note": "n" } ] }""";
        var withList = Fixture().With("Web.anchors.json", List).With("Web.js.10-selectors.js", "const A = kit.anchors.find('used');");

        var problems = WebCouplingChecks.CheckAnchors(withList);

        Assert.Contains(WebCouplingChecks.CheckAnchors(Fixture().With("Web.js.10-selectors.js", "var a = 1;")), p => p.Contains("no Web/anchors.json", StringComparison.Ordinal));
        Assert.Empty(WebCouplingChecks.CheckAnchors(new ResourceAssembly()));
        Assert.Contains(problems, p => p.Contains("'unused' isn't used by name", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("the entry 'kit.mine'", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.Contains("'used'", StringComparison.Ordinal));
        Assert.Contains(WebCouplingChecks.CheckAnchors(Fixture().With("Web.anchors.json", "{ nope").With("Web.js.10-a.js", "var a;")), p => p.Contains("isn't valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckPluginScripts_TreatsTheClassesOfTheAnchorList_AsAnchors()
    {
        const string List = """{ "anchors": [ { "name": "widget", "css": [".nativeWidget .buttons", "#nativeWidgetFallback"], "required": true, "views": ["v"], "note": "n" } ] }""";
        var plugin = Fixture().With("Web.anchors.json", List)
            .With("Web.js.10-selectors.js", "const W = kit.anchors.find('widget');")
            .With("Web.js.20-x.js", "document.querySelector('.nativeWidget .buttons'); document.getElementById('nativeWidgetFallback'); const t = 'buttons';");
        var raw = Fixture().With("Web.anchors.json", List).With("Web.js.10-selectors.js", "const W = 'nativeWidget';");

        var problems = WebCouplingChecks.CheckPluginScripts(plugin);

        Assert.Equal(2, problems.Count(p => p.Contains("Web/js/20-x.js", StringComparison.Ordinal) && p.Contains("belongs in Web/anchors.json", StringComparison.Ordinal)));
        Assert.DoesNotContain(problems, p => p.Contains("10-selectors.js line", StringComparison.Ordinal));
        Assert.Contains(WebCouplingChecks.CheckPluginScripts(raw), p => p.Contains("10-selectors.js line 1: the jellyfin-web anchor 'nativeWidget'", StringComparison.Ordinal));
        Assert.Equal(["nativeWidget", "nativeWidgetFallback"], WebCouplingChecks.AnchorTokens(WebCouplingChecks.ReadAnchors(plugin, [])));
    }

    [Theory]
    [InlineData("kit.test.mjs")]
    [InlineData("pages.test.mjs")]
    [InlineData("health.test.mjs")]
    [InlineData("shortcuts.test.mjs")]
    [InlineData("actionsheet.test.mjs")]
    public async Task KitJavaScriptUnitTests_Pass(string file)
    {
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        var run = await NodeRunner.RunAsync(node!, ["--test", Path.Combine(Web, "tests", file)], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("# pass 0", run.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KitJavaScriptTestHelpers_Parse()
    {
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        foreach (var file in Directory.EnumerateFiles(Path.Combine(Web, "tests"), "*.mjs").Append(Path.Combine(Web, "kit.js")))
        {
            var run = await NodeRunner.RunAsync(node!, ["--check", file], cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(run.ExitCode == 0, Path.GetFileName(file) + ": " + run.Output);
        }
    }

    [Fact]
    public void KitJavaScriptUnitTests_AreAllRun()
    {
        // Every kit/web/tests/*.test.mjs except the two that check a plugin's built script (run by each plugin's tests).
        var files = Directory.EnumerateFiles(Path.Combine(Web, "tests"), "*.test.mjs").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["actionsheet.test.mjs", "admin-script.test.mjs", "client-script.test.mjs", "health.test.mjs", "kit.test.mjs", "pages.test.mjs", "shortcuts.test.mjs"], files);
    }

    [Fact]
    public void KitJs_ShowsTheShellsSentences()
    {
        // The fallback route's host shows the sentence the server's shell holds for a page the admin turned off
        // (KitPageMarkup).
        Assert.Contains("turnedOff: '" + Jellyfin.Plugin.Kit.Web.Pages.KitPageMarkup.TurnedOffText(string.Empty) + "'", KitJs, StringComparison.Ordinal);
    }

    [Fact]
    public void KitJs_RestoresTheOriginalTitle_FromWhereTheHeadFragmentKeepsIt()
    {
        // The head fragment's viewbeforeshow listener keeps FallbackRoute's own title before any kit claims the page; the
        // kit's hand-back restores it from the same shared attribute.
        Assert.Contains("originalTitle: '" + FallbackRouteHeadFragment.OriginalTitleAttribute + "'", KitJs, StringComparison.Ordinal);
    }

    private static ResourceAssembly Fixture()
        => new ResourceAssembly().WithResourcesOf(typeof(KitScriptConventionTests).Assembly, EmbeddedScript.KitScriptResource, EmbeddedScript.KitCssResource, WebAnchorCatalog.KitResource);
}
