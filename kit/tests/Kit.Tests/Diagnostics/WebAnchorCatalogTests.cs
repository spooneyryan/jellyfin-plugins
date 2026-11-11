using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Diagnostics;

/// <summary>
/// The anchor lists: kit/web/anchors.json and a plugin's Web/anchors.json, read by the
/// kit's WebAnchorCatalog for the ClientScript and the ClientHealth endpoint.
/// </summary>
public sealed class WebAnchorCatalogTests
{
    private static readonly string KitAnchors = File.ReadAllText(Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "kit", "web", "anchors.json"));

    [Fact]
    public void TheKitsList_AndKitTestsFixture_AreEmbeddedAndValid()
    {
        var catalog = WebAnchorCatalog.Load(typeof(WebAnchorCatalogTests).Assembly, typeof(WebAnchorCatalogTests).Assembly);

        Assert.Empty(catalog.Problems);
        Assert.Equal(["kit.f1Host", "kit.pluginPagesHost", "kit.drawer", "kit.userMenu", "kit.nativeDialog", "kit.actionSheet", "kit.actionSheetRows", "kit.itemMenuButton", "kit.detailsMoreButton", "kit.detailsVersion", "kit.selectionMenuButton", "kit.selectionCheckbox", "kit.selectionClose", "fixtureList", "fixtureButton"], catalog.Anchors.Select(a => a.Name));
        var list = catalog.Find("fixtureList")!;
        Assert.Equal([".fixtureList", "#fixtureListFallback"], list.Css);
        Assert.True(list.Required);
        Assert.Equal(["fixture"], list.Views);
        Assert.StartsWith("Kit.Tests fixture", list.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_OnlyADeclaredAnchor_OnOneOfItsViews()
    {
        var catalog = WebAnchorCatalog.Load(typeof(WebAnchorCatalogTests).Assembly, typeof(WebAnchorCatalogTests).Assembly);

        Assert.True(catalog.Accepts("fixtureList", "fixture"));
        Assert.True(catalog.Accepts("fixtureButton", "other"));
        Assert.True(catalog.Accepts("kit.f1Host", "pages"));
        Assert.False(catalog.Accepts("fixtureList", "other"));
        Assert.False(catalog.Accepts("fixturelist", "fixture"));
        Assert.False(catalog.Accepts("unknown", "fixture"));
        Assert.False(catalog.Accepts("kit.f1Host", "fixture"));
    }

    [Fact]
    public void APluginWithoutAList_HasOnlyTheKitsAnchors()
    {
        var plugin = new ResourceAssembly().WithResourcesOf(typeof(WebAnchorCatalogTests).Assembly, WebAnchorCatalog.KitResource);

        var catalog = WebAnchorCatalog.Load(plugin, plugin);

        Assert.Empty(catalog.Problems);
        Assert.All(catalog.Anchors, a => Assert.StartsWith(WebAnchorCatalog.KitNamePrefix, a.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingKitList_IsAProblem_NotAnException()
    {
        var catalog = WebAnchorCatalog.Load(new ResourceAssembly(), new ResourceAssembly());

        Assert.Empty(catalog.Anchors);
        Assert.Contains(catalog.Problems, p => p.Contains(WebAnchorCatalog.KitResource, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "name": "Detail", "css": [".a"], "required": true, "views": ["details"], "note": "n" }""", "camelCase")]
    [InlineData("""{ "name": "kit.detail", "css": [".a"], "required": true, "views": ["details"], "note": "n" }""", "camelCase")]
    [InlineData("""{ "name": "detail", "css": [], "required": true, "views": ["details"], "note": "n" }""", "css must be")]
    [InlineData("""{ "name": "detail", "css": [".a", ".a"], "required": true, "views": ["details"], "note": "n" }""", "twice")]
    [InlineData("""{ "name": "detail", "css": [".a{}"], "required": true, "views": ["details"], "note": "n" }""", "the selector")]
    [InlineData("""{ "name": "detail", "css": [" .a"], "required": true, "views": ["details"], "note": "n" }""", "the selector")]
    [InlineData("""{ "name": "detail", "css": [".a"], "required": "yes", "views": ["details"], "note": "n" }""", "required must be")]
    [InlineData("""{ "name": "detail", "css": [".a"], "required": true, "views": ["Details"], "note": "n" }""", "views must be")]
    [InlineData("""{ "name": "detail", "css": [".a"], "required": true, "views": [], "note": "n" }""", "views must be")]
    [InlineData("""{ "name": "detail", "css": [".a"], "required": true, "views": ["details"], "note": " " }""", "note must")]
    [InlineData("""{ "name": "detail", "css": [".a"], "required": true, "views": ["details"], "note": "n", "extra": 1 }""", "unknown property")]
    public void ABrokenEntry_IsLeftOut_WithAProblem(string entry, string expected)
    {
        var catalog = WebAnchorCatalog.Parse(KitAnchors, """{ "anchors": [ """ + entry + """, { "name": "good", "css": [".g"], "required": false, "views": ["v"], "note": "n" } ] }""");

        Assert.Contains(catalog.Problems, p => p.Contains(expected, StringComparison.Ordinal));
        Assert.Null(catalog.Find("detail"));
        Assert.NotNull(catalog.Find("good"));
    }

    [Theory]
    [InlineData("not json", "isn't valid JSON")]
    [InlineData("[]", "must be an object")]
    [InlineData("""{ "anchors": [], "other": 1 }""", "unknown top-level property")]
    public void ABrokenFile_IsAProblem(string file, string expected)
    {
        var catalog = WebAnchorCatalog.Parse(KitAnchors, file);

        Assert.Contains(catalog.Problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void AnAnchorDeclaredTwice_KeepsTheFirst()
    {
        var catalog = WebAnchorCatalog.Parse(
            KitAnchors,
            """{ "anchors": [ { "name": "a", "css": [".one"], "required": true, "views": ["v"], "note": "n" }, { "name": "a", "css": [".two"], "required": true, "views": ["v"], "note": "n" } ] }""");

        Assert.Equal([".one"], catalog.Find("a")!.Css);
        Assert.Contains(catalog.Problems, p => p.Contains("declared twice", StringComparison.Ordinal));
    }

    [Fact]
    public void ToClientJson_HasNameCssRequiredAndViews_WithoutNotes()
    {
        var catalog = WebAnchorCatalog.Load(typeof(WebAnchorCatalogTests).Assembly, typeof(WebAnchorCatalogTests).Assembly);

        using var json = JsonDocument.Parse(catalog.ToClientJson([]));

        var list = json.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "fixtureList");
        Assert.Equal(["name", "css", "required", "views"], list.EnumerateObject().Select(p => p.Name));
        Assert.Equal(".fixtureList", list.GetProperty("css")[0].GetString());
        Assert.Equal(catalog.Anchors.Count, json.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData(".mainDetailButtons", ".mainDetailButtonsKitRenamed")]
    [InlineData("#itemDetailPage:not(.hide)", "#itemDetailPageKitRenamed:not(.hideKitRenamed)")]
    [InlineData(".videoPlayerContainer video", ".videoPlayerContainerKitRenamed video")]
    [InlineData("video.htmlvideoplayer", "video.htmlvideoplayerKitRenamed")]
    [InlineData("[data-role=\"page\"]", "[data-role=\"page\"][data-kit-renamed]")]
    public void Rename_MakesASelectorMatchNothingTheWebClientRenders(string selector, string expected)
    {
        Assert.Equal(expected, WebAnchorCatalog.Rename(selector));
    }

    [Fact]
    public void ADeveloperBuild_RenamesTheAnchorsItNames_OnlyInTheClientJson()
    {
        var plugin = new ResourceAssembly()
            .WithResourcesOf(typeof(WebAnchorCatalogTests).Assembly, WebAnchorCatalog.KitResource, WebAnchorCatalog.PluginResource)
            .WithMetadata("Jellyfin.Plugin.Kit.RenameAnchors", " fixtureList ; kit.userMenu,fixtureList");

        var renamed = WebAnchorCatalog.RenamedAnchors(plugin);
        var catalog = WebAnchorCatalog.Load(plugin, plugin);
        using var json = JsonDocument.Parse(catalog.ToClientJson(renamed));

        Assert.Equal(["fixtureList", "kit.userMenu"], renamed);
        Assert.Equal([".fixtureListKitRenamed", "#fixtureListFallbackKitRenamed"], Css(json, "fixtureList"));
        Assert.Equal([".userMenuOptionsKitRenamed"], Css(json, "kit.userMenu"));
        Assert.Equal([".fixtureButton"], Css(json, "fixtureButton"));
        Assert.Equal([".fixtureList", "#fixtureListFallback"], catalog.Find("fixtureList")!.Css);
        Assert.Empty(WebAnchorCatalog.RenamedAnchors(new ResourceAssembly()));
    }

    [Fact]
    public void TheKitsAnchorClasses_AreOnTheDenylist_SoKitJsNamesThemOnlyInKitAnchors()
    {
        var anchors = WebCouplingChecks.ReadAnchors("kit/web/anchors.json", KitAnchors, kit: true, []);

        // f1Host, pluginPagesHost, drawer, userMenu, nativeDialog (.dialogContainer, denylisted), and the action
        // sheet's two and its six trigger probes.
        Assert.Equal(13, anchors.Count);
        Assert.All(WebCouplingChecks.AnchorTokens(anchors), token => Assert.Contains(token, WebCouplingChecks.AnchorDenylist));
    }

    private static string[] Css(JsonDocument json, string name)
        => json.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == name).GetProperty("css").EnumerateArray().Select(c => c.GetString()!).ToArray();
}
