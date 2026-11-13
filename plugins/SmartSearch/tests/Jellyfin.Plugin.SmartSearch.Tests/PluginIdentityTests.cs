using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests;

/// <summary>
/// The kit's identity checks for Smart Search: the GUID, name, version and target of
/// <c>build.yaml</c>, the project, the assembly and the plugin class agree; the settings page is the only page and in the
/// main menu. Repo.Tests runs the same checks over every plugin; these keep them in the plugin's own run.
/// </summary>
public sealed class PluginIdentityTests : IDisposable
{
    private static readonly string Root = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "plugins", "SmartSearch");
    private static readonly BuildYaml Yaml = BuildYaml.Load(Path.Combine(Root, JellyfinLine.BuildYamlName));
    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("smartsearch-identity-");

    [Fact]
    public void BuildYaml_IsThePluginsIdentity_AtTheReleaseVersion()
    {
        Assert.Empty(PluginIdentityChecks.CheckRequiredFields(Yaml));
        Assert.Empty(PluginIdentityChecks.CheckGuid(Yaml));
        Assert.Equal(("Smart Search", "d92488d5-409f-4ad0-af1d-3615db9d0513"), (Yaml.Name, Yaml.Guid));
        Assert.Equal(PluginIdentityChecks.AssignedGuids["Smart Search"], Guid.Parse(Yaml.Guid));

        // 1.1.1.0 gives auto-collections their default poster, 1.1.2.0 the owner name, 1.2.0.0 (2.1.0.0 on Jellyfin 12) the People rule.
        Assert.Equal(JellyfinLine.Is12 ? "2.1.0.0" : "1.2.0.0", Yaml.Version);
        Assert.Empty(PluginIdentityChecks.CheckVersionFormat(Yaml.Version));
        Assert.Empty(PluginIdentityChecks.CheckTargetAbi(Yaml));
        Assert.Equal(["Jellyfin.Plugin.SmartSearch.dll"], Yaml.Artifacts);
    }

    [Fact]
    public void TheProjectAssemblyAndPluginClass_AgreeWithBuildYaml()
    {
        var project = XDocument.Load(Path.Combine(Root, "Jellyfin.Plugin.SmartSearch", "Jellyfin.Plugin.SmartSearch.csproj"));
        var plugin = PluginIdentityChecks.CreatePlugin(typeof(Plugin), _scratch.FullName);

        Assert.Empty(PluginIdentityChecks.CheckProjectVersions(project, Yaml.Version));
        Assert.Empty(PluginIdentityChecks.CheckAssembly(typeof(Plugin).Assembly, Yaml));
        Assert.Empty(PluginIdentityChecks.CheckPluginInstance(plugin, Yaml));
        Assert.Empty(PluginIdentityChecks.CheckPluginBaseType(typeof(Plugin)));
        Assert.Empty(PluginIdentityChecks.CheckSourceGuids(File.ReadAllText(Path.Combine(Root, "Jellyfin.Plugin.SmartSearch", "Plugin.cs")), "Plugin.cs", Guid.Parse(Yaml.Guid)));
        Assert.Equal(Plugin.PluginId, Yaml.Guid);
    }

    [Fact]
    public void TheSettingsPage_IsFirst_AndTheOnlyPage()
    {
        var plugin = (Plugin)PluginIdentityChecks.CreatePlugin(typeof(Plugin), _scratch.FullName);
        var pages = plugin.GetPages().ToList();

        Assert.Empty(PluginIdentityChecks.CheckPages(pages, Yaml.Name, typeof(Plugin).Assembly.GetManifestResourceNames()));
        var page = Assert.Single(pages);
        Assert.Equal("SmartSearch", page.Name);
        Assert.Equal("Jellyfin.Plugin.SmartSearch.Configuration.configPage.html", page.EmbeddedResourcePath);
    }

    [Fact]
    public void TheKitIdentity_IsTheConventions()
    {
        // The conventions: route, DOM prefix, schema prefix, data folder; the page's key, Plugin Pages Id and label.
        var options = SmartSearchKit.Options;

        Assert.Equal(("Smart Search", "SmartSearch", "jfss", "SmartSearch", "smartsearch"), (options.PluginName, options.RoutePrefix, options.Prefix, options.SchemaPrefix, options.DataFolder));
        Assert.Equal(Guid.Parse(Plugin.PluginId), options.PluginId);
        var page = Assert.Single(options.Pages);
        Assert.Equal(("search", "Advanced Search", "manage_search"), (page.Key, page.Title, page.Icon));
        Assert.Equal("Jellyfin.Plugin.SmartSearch.SearchPage", page.PluginPagesId(options.RoutePrefix));
        Assert.Equal("/SmartSearch/Pages/search", page.ShellUrl(options.RoutePrefix));
    }

    // As Access Roles 1.0.1: the DLL lets only its own tests see its internals, not Moq's proxy assembly.
    [Fact]
    public void TheAssembly_LetsOnlyItsTestsSeeItsInternals()
    {
        var friends = typeof(Plugin).Assembly.GetCustomAttributes(typeof(InternalsVisibleToAttribute), inherit: false).Cast<InternalsVisibleToAttribute>().Select(a => a.AssemblyName);

        Assert.Equal(["Jellyfin.Plugin.SmartSearch.Tests"], friends);
    }

    public void Dispose() => _scratch.Delete(recursive: true);
}
