using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Web;

/// <summary>
/// The web client through the kit: its files embedded by Kit.props, one script built by the
/// kit's EmbeddedScript, the coupling policy, node --check, the kit's ClientScript checks, and Smart Search's own tests.
/// </summary>
public sealed class ClientScriptTests
{
    private static readonly string SmartSearchScriptTests = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "SmartSearch", "tests", "Jellyfin.Plugin.SmartSearch.Tests", "Web", "smartsearch.test.mjs");

    private static readonly JsonSerializerOptions WithoutNulls = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() } };

    private static readonly Lazy<EmbeddedScript> Scripts = new(() => new EmbeddedScript(SmartSearchKit.Options, NullLoggerFactory.Instance));

    [Fact]
    public void TheWebFiles_AreEmbeddedByTheKit()
    {
        var assembly = typeof(Plugin).Assembly;

        Assert.Equal(
            ["Web.js.00-strings.js", "Web.js.10-selectors.js", "Web.js.20-api.js", "Web.js.30-model.js", "Web.js.35-tree.js", "Web.js.40-builder.js", "Web.js.42-advanced.js", "Web.js.50-results.js", "Web.js.60-saved.js", "Web.js.65-collections.js", "Web.js.70-entrypoints.js", "Web.js.85-collectionnote.js", "Web.js.90-main.js"],
            EmbeddedScript.ResourceNames(assembly, EmbeddedScript.ScriptFolder, ".js"));
        Assert.Equal(["Web.css.smartsearch.css"], EmbeddedScript.ResourceNames(assembly, EmbeddedScript.CssFolder, ".css"));
        Assert.Equal(
            ["Kit.kit.js"],
            assembly.GetManifestResourceNames().Where(n => n.EndsWith(".js", StringComparison.Ordinal) && !n.StartsWith(EmbeddedScript.ScriptFolder, StringComparison.Ordinal)));
    }

    [Fact]
    public void TheClientScript_CarriesSmartSearchsBuild()
    {
        var script = Scripts.Value.ClientScriptText;

        Assert.Contains("var KIT_BUILD = {\"plugin\":\"Smart Search\",\"prefix\":\"jfss\",\"route\":\"SmartSearch\",\"version\":\"" + JellyfinLine.VersionOf(typeof(Plugin)) + "\",\"kit\":\"" + KitBuild.KitVersion + "\"", script, StringComparison.Ordinal);
        Assert.Contains("// ---- Web/js/90-main.js\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__PFX__", script, StringComparison.Ordinal);
        Assert.Null(Scripts.Value.AdminScriptText);
    }

    [Fact]
    public void ThePageTestsCatalogue_IsTheOneAnAdministratorGets()
    {
        // The page's tests build every field from fields.json; it must stay the server's own catalogue.
        var served = JsonSerializer.SerializeToNode(
            new SmartSearchFields(new FieldRegistry()).GetCatalog(isAdmin: true),
            WithoutNulls);
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(SmartSearchScriptTests)!, "fields.json")));

        Assert.True(JsonNode.DeepEquals(served, fixture), served!.ToJsonString());
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
        var run = await KitScriptChecks.RunClientScriptTestsAsync(Scripts.Value.ClientScriptText, SmartSearchKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSmartSearchUi_PassesItsTests()
    {
        var run = await KitScriptChecks.RunTestFileAsync(SmartSearchScriptTests, Scripts.Value.ClientScriptText, SmartSearchKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("# pass 0", run.StandardOutput, StringComparison.Ordinal);
    }
}
