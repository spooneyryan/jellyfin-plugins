using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web;

/// <summary>
/// The kit's EmbeddedScript: the ClientScript IIFE and the AdminScript module, built once
/// from kit.js, kit.css and the plugin's embedded web files; the node checks run when Node is on the PATH.
/// </summary>
public sealed class EmbeddedScriptTests
{
    private static readonly Assembly KitTests = typeof(EmbeddedScriptTests).Assembly;

    [Fact]
    public void KitProps_EmbedsKitJsKitCssAndThePluginWebFiles()
    {
        var root = PluginIdentityChecks.FindRepositoryRoot();

        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "kit", "web", "kit.js")), Read(KitTests, EmbeddedScript.KitScriptResource));
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "kit", "web", "kit.css")), Read(KitTests, EmbeddedScript.KitCssResource));
        Assert.Equal(["Web.js.10-fixture.js", "Web.js.90-main.js"], EmbeddedScript.ResourceNames(KitTests, EmbeddedScript.ScriptFolder, ".js"));
        Assert.Equal(["Web.css.50-fixture.css"], EmbeddedScript.ResourceNames(KitTests, EmbeddedScript.CssFolder, ".css"));
        Assert.Equal(["Web.admin.js.50-mount.js"], EmbeddedScript.ResourceNames(KitTests, EmbeddedScript.AdminScriptFolder, ".js"));
        Assert.Equal(["Web.admin.css.50-admin.css"], EmbeddedScript.ResourceNames(KitTests, EmbeddedScript.AdminCssFolder, ".css"));
    }

    [Fact]
    public void ClientScript_IsOneIife_InTheDocumentedOrder()
    {
        var plugin = Plugin()
            .With("Web.js.20-b.js", "var b = 2;")
            .With("Web.js.10-a.js", "var a = 1;\n")
            .With("Web.js.notes.txt", "not a script")
            .With("Web.admin.js.10-admin.js", "function mount() {}");

        var script = EmbeddedScript.BuildClientScript(Options(plugin), plugin);

        Assert.StartsWith("/* Kit Test 1.2.3.0 web client, kit ", script, StringComparison.Ordinal);
        AssertInOrder(
            script,
            "(function () {\n'use strict';\n",
            "var KIT_BUILD = {",
            "window.__jfkit = window.__jfkit || {};\nif (window.__jfkit[KIT_BUILD.prefix]) {\n    return;\n}\n",
            "var KIT_CSS = \"",
            "var KIT_ANCHOR_DECLARATIONS = [{\"name\":\"kit.f1Host\"",
            "// ---- kit/web/kit.js\n",
            "const KIT_NATIVE_CLASSES = ",
            "function createKit(build, css, options) {",
            "var kit = createKit(KIT_BUILD, KIT_CSS, { anchors: KIT_ANCHOR_DECLARATIONS });\n",
            "window.__jfkit[KIT_BUILD.prefix] = { plugin: KIT_BUILD.plugin, version: KIT_BUILD.version, kit: KIT_BUILD.kit, health: kit.health, timing: kit.timing, instance: kit };",
            "// ---- Web/js/10-a.js\nvar a = 1;\n",
            "// ---- Web/js/20-b.js\nvar b = 2;\n",
            "// ---- end\n})();\n");
        Assert.EndsWith("})();\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("not a script", script, StringComparison.Ordinal);
        Assert.DoesNotContain("function mount", script, StringComparison.Ordinal);
    }

    [Fact]
    public void KitBuild_NamesThePlugin_ItsVersionAndMvid_AndTheKitVersion()
    {
        var plugin = Plugin();

        var script = EmbeddedScript.BuildClientScript(Options(plugin), plugin);
        var json = script[(script.IndexOf("var KIT_BUILD = ", StringComparison.Ordinal) + "var KIT_BUILD = ".Length)..];
        using var build = JsonDocument.Parse(json[..json.IndexOf(";\n", StringComparison.Ordinal)]);

        Assert.Equal("Kit Test", build.RootElement.GetProperty("plugin").GetString());
        Assert.Equal("jfkt", build.RootElement.GetProperty("prefix").GetString());
        Assert.Equal("Fx", build.RootElement.GetProperty("route").GetString());
        Assert.Equal("1.2.3.0", build.RootElement.GetProperty("version").GetString());
        Assert.Equal(KitBuild.KitVersion, build.RootElement.GetProperty("kit").GetString());
        Assert.Equal(File.ReadAllText(Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "kit", "VERSION")).Trim(), KitBuild.KitVersion);
        Assert.Equal("0123abcd", build.RootElement.GetProperty("mvid").GetString());
    }

    [Fact]
    public void KitCss_IsKitCssForThePrefix_FollowedByThePluginCss_InNameOrder()
    {
        var plugin = Plugin()
            .With("Web.css.20-z.css", ".jfkt-z{color:inherit}")
            .With("Web.css.10-x.css", ".jfkt-x{display:block}")
            .With("Web.admin.css.10-y.css", ".jfkt-y{display:none}");

        var css = EmbeddedScript.BuildCss(Options(plugin), plugin, EmbeddedScript.CssFolder);
        var script = EmbeddedScript.BuildClientScript(Options(plugin), plugin);

        Assert.DoesNotContain("__PFX__", css, StringComparison.Ordinal);
        AssertInOrder(css, ":root{--jfkt-accent:", ".jfkt-backdrop{", ".jfkt-toast{", "/* Web/css/10-x.css */\n.jfkt-x{display:block}", "/* Web/css/20-z.css */\n.jfkt-z{color:inherit}");
        Assert.DoesNotContain(".jfkt-y", css, StringComparison.Ordinal);
        Assert.Contains("var KIT_CSS = " + JsonSerializer.Serialize(css) + ";\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminScript_IsAnEsModule_OfKitJsAndTheAdminFiles_ThatExportsMount()
    {
        var plugin = Plugin()
            .With("Web.js.10-a.js", "var clientOnly = 1;")
            .With("Web.admin.js.20-view.js", "function render(view) { view.append(kit.h('p', { text: 'x' })); }")
            .With("Web.admin.js.10-mount.js", "function mount(view) { render(view); }")
            .With("Web.admin.css.10-y.css", ".jfkt-y{display:none}");

        var module = EmbeddedScript.BuildAdminScript(Options(plugin), plugin)!;

        Assert.StartsWith("/* Kit Test 1.2.3.0 dashboard module, kit ", module, StringComparison.Ordinal);
        AssertInOrder(
            module,
            "const KIT_BUILD = {",
            "const KIT_CSS = \"",
            "const KIT_ANCHOR_DECLARATIONS = [{\"name\":\"kit.f1Host\"",
            "// ---- kit/web/kit.js\n",
            "const kit = createKit(KIT_BUILD, KIT_CSS, { styleId: KIT_BUILD.prefix + '-admin-styles', anchors: KIT_ANCHOR_DECLARATIONS });\n",
            "// ---- Web/admin/js/10-mount.js\n",
            "// ---- Web/admin/js/20-view.js\n",
            "// ---- end\nexport { mount };\n");
        Assert.DoesNotContain("window.__jfkit", module, StringComparison.Ordinal);
        Assert.DoesNotContain("clientOnly", module, StringComparison.Ordinal);
        Assert.Contains(".jfkt-y{display:none}", module, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminScript_IsNull_WithoutAdminFiles_AndItsActionAnswers404()
    {
        var plugin = Plugin().With("Web.js.10-a.js", "var a = 1;");
        var scripts = new EmbeddedScript(Options(plugin), plugin, WebKit.LoggerFactory(new ListLogger()));

        Assert.Null(EmbeddedScript.BuildAdminScript(Options(plugin), plugin));
        Assert.Null(scripts.AdminScriptText);
        var result = Assert.IsType<KitProblemResult>(scripts.AdminScript());
        Assert.Equal(404, result.StatusCode);
        await Task.CompletedTask;
    }

    [Fact]
    public void AMissingKitResource_Answers503_AndLogsOneError()
    {
        var plugin = new ResourceAssembly().With("Web.js.10-a.js", "var a = 1;").With("Web.admin.js.10-m.js", "function mount() {}");
        var logger = new ListLogger();
        var scripts = new EmbeddedScript(Options(plugin), plugin, WebKit.LoggerFactory(logger));

        Assert.Throws<InvalidOperationException>(() => EmbeddedScript.BuildClientScript(Options(plugin), plugin));
        var first = Assert.IsType<KitProblemResult>(scripts.ClientScript());
        var second = Assert.IsType<KitProblemResult>(scripts.ClientScript());
        var admin = Assert.IsType<KitProblemResult>(scripts.AdminScript());

        Assert.Equal(503, first.StatusCode);
        Assert.Equal(503, second.StatusCode);
        Assert.Equal(503, admin.StatusCode);
        Assert.Equal("The web client script couldn't be built. The server log has the details.", first.Value.Detail);
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Error));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException && e.Message.Contains("Kit Test", StringComparison.Ordinal));
    }

    [Fact]
    public void APluginFileThatIsNotUtf8_Answers503()
    {
        var plugin = Plugin().With("Web.js.10-a.js", [0x76, 0x61, 0x72, 0x20, 0xC3, 0x28]);
        var scripts = new EmbeddedScript(Options(plugin), plugin, WebKit.LoggerFactory(new ListLogger()));

        var result = Assert.IsType<KitProblemResult>(scripts.ClientScript());

        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public void BothScripts_DeclareTheKitsAnchorsThenThePlugins_ForCreateKit()
    {
        var scripts = RealScripts();

        using var client = Anchors(scripts.ClientScriptText, "var KIT_ANCHOR_DECLARATIONS = ");
        using var admin = Anchors(scripts.AdminScriptText!, "const KIT_ANCHOR_DECLARATIONS = ");

        Assert.Equal(["kit.f1Host", "kit.pluginPagesHost", "kit.drawer", "kit.userMenu", "kit.nativeDialog", "kit.actionSheet", "kit.actionSheetRows", "kit.itemMenuButton", "kit.detailsMoreButton", "kit.detailsVersion", "kit.selectionMenuButton", "kit.selectionCheckbox", "kit.selectionClose", "fixtureList", "fixtureButton"], client.RootElement.EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(client.RootElement.GetRawText(), admin.RootElement.GetRawText());
        Assert.Equal(WebAnchorCatalog.Load(KitTests, KitTests).ToClientJson([]), client.RootElement.GetRawText());
    }

    [Fact]
    public void APluginWithoutAnAnchorList_GetsTheKitsAnchorsOnly_AndOneWithoutTheKitsList_Answers503()
    {
        var plugin = Plugin().With("Web.js.10-a.js", "var a = 1;");
        var bare = new ResourceAssembly().WithResourcesOf(KitTests, EmbeddedScript.KitScriptResource, EmbeddedScript.KitCssResource).With("Web.js.10-a.js", "var a = 1;");

        using var anchors = Anchors(EmbeddedScript.BuildClientScript(Options(plugin), plugin), "var KIT_ANCHOR_DECLARATIONS = ");

        Assert.All(anchors.RootElement.EnumerateArray(), a => Assert.StartsWith("kit.", a.GetProperty("name").GetString(), StringComparison.Ordinal));
        Assert.Contains(WebAnchorCatalog.KitResource, Assert.Throws<InvalidOperationException>(() => EmbeddedScript.BuildClientScript(Options(bare), bare)).Message, StringComparison.Ordinal);
        Assert.Equal(503, Assert.IsType<KitProblemResult>(new EmbeddedScript(Options(bare), bare, WebKit.LoggerFactory(new ListLogger())).ClientScript()).StatusCode);
    }

    [Fact]
    public void ADeveloperBuild_RenamesTheAnchorsItNames_AndWarnsOnceWhileItServesThem()
    {
        // -p:KitRenameAnchors=fixtureList (Kit.props): the developer build's simulated missing anchor.
        var plugin = Plugin()
            .WithResourcesOf(KitTests, WebAnchorCatalog.PluginResource)
            .With("Web.js.10-a.js", "var a = 1;")
            .WithMetadata("Jellyfin.Plugin.Kit.RenameAnchors", "fixtureList");
        var logger = new ListLogger();
        var scripts = new EmbeddedScript(Options(plugin), plugin, WebKit.LoggerFactory(logger));

        Assert.IsType<KitScriptResult>(scripts.ClientScript());
        Assert.IsType<KitScriptResult>(scripts.ClientScript());

        Assert.Contains("\"css\":[\".fixtureListKitRenamed\",\"#fixtureListFallbackKitRenamed\"]", scripts.ClientScriptText, StringComparison.Ordinal);
        Assert.Contains("\"css\":[\".fixtureButton\"]", scripts.ClientScriptText, StringComparison.Ordinal);
        var warning = Assert.Single(logger.At(LogLevel.Warning));
        Assert.Contains("renames the web anchors fixtureList", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Never install it on a real server", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANormalBuild_RenamesNothing_AndDoesNotWarn()
    {
        var logger = new ListLogger();
        var scripts = new EmbeddedScript(WebKit.Options(), WebKit.LoggerFactory(logger));

        Assert.IsType<KitScriptResult>(scripts.ClientScript());

        Assert.DoesNotContain(WebAnchorCatalog.RenameSuffix, scripts.ClientScriptText, StringComparison.Ordinal);
        Assert.Empty(logger.At(LogLevel.Warning));
    }

    [Fact]
    public void AByteOrderMark_IsDropped()
    {
        var plugin = Plugin().With("Web.js.10-a.js", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("var a = 1;")]);

        var script = EmbeddedScript.BuildClientScript(Options(plugin), plugin);

        Assert.Contains("// ---- Web/js/10-a.js\nvar a = 1;\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain('﻿', script);
    }

    [Fact]
    public void TheScript_IsBuiltOnce_AndItsETagIsTheQuotedMvid()
    {
        var plugin = Plugin().With("Web.js.10-a.js", "var a = 1;");
        var scripts = new EmbeddedScript(Options(plugin), plugin, WebKit.LoggerFactory(new ListLogger()));

        var first = Assert.IsType<KitScriptResult>(scripts.ClientScript());
        // A marker no comment of kit.js uses (its text is in the script too).
        plugin.With("Web.js.10-a.js", "var rewrittenAfterTheFirstBuild = 2;");
        var second = Assert.IsType<KitScriptResult>(scripts.ClientScript());

        Assert.Equal("\"0123abcd456789ef0123456789abcdef\"", scripts.ETag.Tag.ToString());
        Assert.Equal(Encoding.UTF8.GetByteCount(scripts.ClientScriptText), first.Length);
        Assert.Equal(first.Length, second.Length);
        Assert.DoesNotContain("rewrittenAfterTheFirstBuild", scripts.ClientScriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void AddEmbeddedScript_RegistersOneInstance_ForBothServiceTypes()
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(WebKit.Options());
        services.AddEmbeddedScript();
        services.AddEmbeddedScript();

        using var provider = services.BuildServiceProvider();

        Assert.Single(services, d => d.ServiceType == typeof(IEmbeddedScript));
        Assert.Same(provider.GetRequiredService<EmbeddedScript>(), provider.GetRequiredService<IEmbeddedScript>());
        Assert.Contains("createKit(", provider.GetRequiredService<EmbeddedScript>().ClientScriptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientScript_PassesNodeCheck()
    {
        var run = await KitScriptChecks.CheckSyntaxAsync(RealScripts().ClientScriptText, module: false, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
    }

    [Fact]
    public async Task AdminScript_PassesNodeCheck_AsAModule()
    {
        var run = await KitScriptChecks.CheckSyntaxAsync(RealScripts().AdminScriptText!, module: true, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
    }

    [Theory]
    [InlineData("const kit = 1;", false)]
    [InlineData("function broken( {", false)]
    [InlineData("function notMount() {}", true)]
    public async Task NodeCheck_CatchesABrokenPluginFile(string file, bool admin)
    {
        var plugin = Plugin().With(admin ? "Web.admin.js.10-x.js" : "Web.js.10-x.js", file);
        var script = admin ? EmbeddedScript.BuildAdminScript(Options(plugin), plugin)! : EmbeddedScript.BuildClientScript(Options(plugin), plugin);

        var run = await KitScriptChecks.CheckSyntaxAsync(script, module: admin, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.NotEqual(0, run!.ExitCode);
    }

    [Fact]
    public async Task ClientScript_PassesTheKitsClientScriptChecks()
    {
        var run = await KitScriptChecks.RunClientScriptTestsAsync(RealScripts().ClientScriptText, "jfkt", TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# pass 3", run.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminScript_PassesTheKitsAdminScriptChecks()
    {
        var run = await KitScriptChecks.RunAdminScriptTestsAsync(RealScripts().AdminScriptText!, "jfkt", "Mounted fixture", TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# pass 1", run.StandardOutput, StringComparison.Ordinal);
    }

    internal static KitOptions Options(Assembly assembly) => new()
    {
        PluginName = "Kit Test",
        RoutePrefix = "Fx",
        Prefix = "jfkt",
        SchemaPrefix = "Fx",
        DataFolder = "kittest",
        PluginAssembly = assembly
    };

    // Kit.Tests' own resources: the repository's kit.js and kit.css and the fixtures under kit/tests/Kit.Tests/Web.
    private static EmbeddedScript RealScripts() => new(WebKit.Options(), WebKit.LoggerFactory(new ListLogger()));

    private static ResourceAssembly Plugin() => new ResourceAssembly().WithResourcesOf(KitTests, EmbeddedScript.KitScriptResource, EmbeddedScript.KitCssResource, WebAnchorCatalog.KitResource);

    private static JsonDocument Anchors(string script, string prefix)
    {
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        return JsonDocument.Parse(script[start..script.IndexOf(";\n", start, StringComparison.Ordinal)]);
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void AssertInOrder(string text, params string[] parts)
    {
        var at = 0;
        foreach (var part in parts)
        {
            var found = text.IndexOf(part, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"'{part}' is missing, or not after the previous part (from index {at}).");
            at = found + part.Length;
        }
    }
}
