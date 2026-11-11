using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests;

/// <summary>
/// The kit's identity checks for Access Roles: the GUID, name, version and target of
/// <c>build.yaml</c>, the project, the assembly and the plugin class agree; the dashboard page comes first and is the only
/// one in the main menu. Repo.Tests runs the same checks over every plugin; these keep them in the plugin's own run.
/// </summary>
public sealed class PluginIdentityTests : IDisposable
{
    private static readonly string Root = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "plugins", "AccessRoles");
    private static readonly BuildYaml Yaml = BuildYaml.Load(Path.Combine(Root, "build.yaml"));
    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("accessroles-identity-");

    [Fact]
    public void BuildYaml_IsThePluginsIdentity_AtTheReleasedVersion()
    {
        Assert.Empty(PluginIdentityChecks.CheckRequiredFields(Yaml));
        Assert.Empty(PluginIdentityChecks.CheckGuid(Yaml));
        Assert.Equal(("Access Roles", "7cbc1019-49a5-4b97-8aa2-279de946ef88"), (Yaml.Name, Yaml.Guid));
        Assert.Equal(PluginIdentityChecks.AssignedGuids["Access Roles"], Guid.Parse(Yaml.Guid));

        // Development builds were 0.9.<n>.0 (0.9.1.0 to 0.9.16.0); 1.0.0.0 was the first release, 1.0.1.0 its fixes, 1.0.2.0 corrected texts, and 1.0.3.0 the owner name.
        Assert.Equal("1.0.3.0", Yaml.Version);
        Assert.Empty(PluginIdentityChecks.CheckVersionFormat(Yaml.Version));
        Assert.Empty(PluginIdentityChecks.CheckTargetAbi(Yaml));
        Assert.Equal(["Jellyfin.Plugin.AccessRoles.dll"], Yaml.Artifacts);
    }

    [Fact]
    public void TheProjectAssemblyAndPluginClass_AgreeWithBuildYaml()
    {
        var project = XDocument.Load(Path.Combine(Root, "Jellyfin.Plugin.AccessRoles", "Jellyfin.Plugin.AccessRoles.csproj"));
        var plugin = PluginIdentityChecks.CreatePlugin(typeof(Plugin), _scratch.FullName);

        Assert.Empty(PluginIdentityChecks.CheckProjectVersions(project, Yaml.Version));
        Assert.Empty(PluginIdentityChecks.CheckAssembly(typeof(Plugin).Assembly, Yaml));
        Assert.Empty(PluginIdentityChecks.CheckPluginInstance(plugin, Yaml));
        Assert.Empty(PluginIdentityChecks.CheckPluginBaseType(typeof(Plugin)));
        Assert.Empty(PluginIdentityChecks.CheckSourceGuids(File.ReadAllText(Path.Combine(Root, "Jellyfin.Plugin.AccessRoles", "Plugin.cs")), "Plugin.cs", Guid.Parse(Yaml.Guid)));
        Assert.Equal(Plugin.PluginId, Yaml.Guid);
    }

    [Fact]
    public void TheSettingsPage_IsFirst_AndTheOnlyOneInTheMainMenu()
    {
        var plugin = (Plugin)PluginIdentityChecks.CreatePlugin(typeof(Plugin), _scratch.FullName);
        var pages = plugin.GetPages().ToList();

        Assert.Empty(PluginIdentityChecks.CheckPages(pages, Yaml.Name, typeof(Plugin).Assembly.GetManifestResourceNames()));
        Assert.Equal(2, pages.Count);
        Assert.Equal(("AccessRoles", "Access Roles", true), (pages[0].Name, pages[0].DisplayName, pages[0].EnableInMainMenu));
        Assert.Equal("Jellyfin.Plugin.AccessRoles.Configuration.configPage.html", pages[0].EmbeddedResourcePath);

        // Since 0.9.8 the page's data-controller module, never in a menu.
        Assert.Equal(("AccessRolesJs", false), (pages[1].Name, pages[1].EnableInMainMenu));
        Assert.Equal("Jellyfin.Plugin.AccessRoles.Configuration.AccessRolesJs.js", pages[1].EmbeddedResourcePath);
    }

    // 1.0.1: the DLL lets only its own tests see its internals, not Moq's proxy assembly
    // (DynamicProxyGenAssembly2, which the root Directory.Build.props adds for every plugin); the tests use written-out
    // doubles of the internal services (TestSupport/InternalFakes.cs).
    [Fact]
    public void TheAssembly_LetsOnlyItsTestsSeeItsInternals()
    {
        var friends = typeof(Plugin).Assembly.GetCustomAttributes(typeof(InternalsVisibleToAttribute), inherit: false).Cast<InternalsVisibleToAttribute>().Select(a => a.AssemblyName);

        Assert.Equal(["Jellyfin.Plugin.AccessRoles.Tests"], friends);
    }

    public void Dispose() => _scratch.Delete(recursive: true);
}
