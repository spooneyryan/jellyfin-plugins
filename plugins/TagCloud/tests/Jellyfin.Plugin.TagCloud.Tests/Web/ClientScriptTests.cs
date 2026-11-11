using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TagCloud.Tests.Web;

/// <summary>
/// The web client through the kit: its files embedded by Kit.props, one script built by the
/// kit's EmbeddedScript, the coupling policy, node --check, the kit's ClientScript checks, and Tag Cloud's own tests.
/// </summary>
public sealed class ClientScriptTests
{
    private static readonly string TagCloudScriptTests = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "TagCloud", "tests", "Jellyfin.Plugin.TagCloud.Tests", "Web", "tagcloud.test.mjs");

    private static readonly Lazy<EmbeddedScript> Scripts = new(() => new EmbeddedScript(TagCloudKit.Options, NullLoggerFactory.Instance));

    [Fact]
    public void TheWebFiles_AreEmbeddedByTheKit()
    {
        var assembly = typeof(Plugin).Assembly;

        Assert.Equal(
            ["Web.js.20-api.js", "Web.js.30-layout.js", "Web.js.35-pack.js", "Web.js.38-look.js", "Web.js.40-cloud.js", "Web.js.50-settings.js", "Web.js.60-page-cloud.js", "Web.js.90-main.js"],
            EmbeddedScript.ResourceNames(assembly, EmbeddedScript.ScriptFolder, ".js"));
        Assert.Equal(["Web.css.tagcloud.css"], EmbeddedScript.ResourceNames(assembly, EmbeddedScript.CssFolder, ".css"));
        Assert.Equal(
            ["Kit.kit.js"],
            assembly.GetManifestResourceNames().Where(n => n.EndsWith(".js", StringComparison.Ordinal) && !n.StartsWith(EmbeddedScript.ScriptFolder, StringComparison.Ordinal)));
    }

    [Fact]
    public void TheClientScript_CarriesTagCloudsBuild_AndItsStyles()
    {
        var script = Scripts.Value.ClientScriptText;

        Assert.Contains("var KIT_BUILD = {\"plugin\":\"Tag Cloud\",\"prefix\":\"jftc\",\"route\":\"TagCloud\",\"version\":\"1.0.1.0\",\"kit\":\"" + KitBuild.KitVersion + "\"", script, StringComparison.Ordinal);
        Assert.Contains("// ---- Web/js/90-main.js\n", script, StringComparison.Ordinal);
        Assert.Contains(".jftc-cloud{", script, StringComparison.Ordinal);
        Assert.Contains(".jftc-show{", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__PFX__", script, StringComparison.Ordinal);
        Assert.Null(Scripts.Value.AdminScriptText);
    }

    [Fact]
    public void TheWebFiles_FollowTheCouplingPolicy()
    {
        Assert.Empty(WebCouplingChecks.CheckPluginScripts(typeof(Plugin).Assembly));
        Assert.Empty(WebCouplingChecks.CheckPluginStyles(typeof(Plugin).Assembly));
        Assert.Empty(WebCouplingChecks.CheckAnchors(typeof(Plugin).Assembly));
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
        var run = await KitScriptChecks.RunClientScriptTestsAsync(Scripts.Value.ClientScriptText, TagCloudKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTagCloudUi_PassesItsTests()
    {
        var run = await KitScriptChecks.RunTestFileAsync(TagCloudScriptTests, Scripts.Value.ClientScriptText, TagCloudKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("# pass 0", run.StandardOutput, StringComparison.Ordinal);
    }
}
