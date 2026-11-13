using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests;

/// <summary>
/// The kit's identity checks for User Ratings: the GUID, name, version and target of
/// <c>build.yaml</c>, the project, the assembly and the plugin class agree; the settings page comes first and isn't in the
/// main menu. Repo.Tests runs the same checks over every plugin; these keep them in the plugin's own run.
/// </summary>
public sealed class PluginIdentityTests : IDisposable
{
    private static readonly string Root = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "plugins", "UserRatings");
    private static readonly BuildYaml Yaml = BuildYaml.Load(Path.Combine(Root, JellyfinLine.BuildYamlName));
    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("userratings-identity-");

    [Fact]
    public void BuildYaml_IsThePluginsIdentity_AtTheReleasedVersion()
    {
        Assert.Empty(PluginIdentityChecks.CheckRequiredFields(Yaml));
        Assert.Empty(PluginIdentityChecks.CheckGuid(Yaml));
        Assert.Equal(("User Ratings", "f53c7c34-8cba-4846-b43b-956816b2f6ae"), (Yaml.Name, Yaml.Guid));
        Assert.Equal(PluginIdentityChecks.AssignedGuids["User Ratings"], Guid.Parse(Yaml.Guid));

        // Development builds were 0.3.<n>.0; 1.0.0.0 is the first release, 1.0.1.0 its fix release, 1.0.2.0 the owner name.
        Assert.Equal(JellyfinLine.Is12 ? "2.0.0.0" : "1.0.2.0", Yaml.Version);
        Assert.Empty(PluginIdentityChecks.CheckVersionFormat(Yaml.Version));
        Assert.Empty(PluginIdentityChecks.CheckTargetAbi(Yaml));
        Assert.Equal(JellyfinLine.Is12 ? "12.4.0.0" : "10.11.5.0", Yaml.TargetAbi);
        Assert.Equal(["Jellyfin.Plugin.UserRatings.dll"], Yaml.Artifacts);
    }

    [Fact]
    public void TheProjectAssemblyAndPluginClass_AgreeWithBuildYaml()
    {
        var project = XDocument.Load(Path.Combine(Root, "Jellyfin.Plugin.UserRatings", "Jellyfin.Plugin.UserRatings.csproj"));
        var plugin = PluginIdentityChecks.CreatePlugin(typeof(Plugin), _scratch.FullName);

        Assert.Empty(PluginIdentityChecks.CheckProjectVersions(project, Yaml.Version));
        Assert.Empty(PluginIdentityChecks.CheckAssembly(typeof(Plugin).Assembly, Yaml));
        Assert.Empty(PluginIdentityChecks.CheckPluginInstance(plugin, Yaml));
        Assert.Empty(PluginIdentityChecks.CheckPluginBaseType(typeof(Plugin)));
        Assert.Empty(PluginIdentityChecks.CheckSourceGuids(File.ReadAllText(Path.Combine(Root, "Jellyfin.Plugin.UserRatings", "Plugin.cs")), "Plugin.cs", Guid.Parse(Yaml.Guid)));
        Assert.Equal(Plugin.PluginId, Yaml.Guid);
    }

    [Fact]
    public void TheProjects_UseTheKitsSqliteLayer()
    {
        // KitUseSqlite in the plugin and in its tests, set before the kit's props read it.
        foreach (var path in new[]
        {
            Path.Combine(Root, "Jellyfin.Plugin.UserRatings", "Jellyfin.Plugin.UserRatings.csproj"),
            Path.Combine(Root, "tests", "Jellyfin.Plugin.UserRatings.Tests", "Jellyfin.Plugin.UserRatings.Tests.csproj")
        })
        {
            var project = XDocument.Load(path).Root!;
            var elements = project.Elements().ToList();
            var flag = Assert.Single(project.Descendants("KitUseSqlite"));
            var import = Assert.Single(project.Elements("Import"));

            Assert.Equal("true", flag.Value);
            Assert.True(elements.IndexOf(flag.Parent!) < elements.IndexOf(import), path + ": KitUseSqlite must come before the kit's import.");
        }

        // The kit's SQLite layer is compiled in: the type exists, internal, in the plugin's own assembly.
        var database = typeof(Plugin).Assembly.GetType("Jellyfin.Plugin.Kit.Sqlite.SqliteDatabase");
        Assert.NotNull(database);
        Assert.False(database!.IsVisible);

        // ... against the server's Microsoft.Data.Sqlite exactly (Deploy's reference-version check): 9.0.11 on 10.11.5,
        // 10.0.14 on 12.4.
        var reference = Assert.Single(typeof(Plugin).Assembly.GetReferencedAssemblies(), a => a.Name == "Microsoft.Data.Sqlite");
        Assert.Equal(JellyfinLine.Is12 ? new Version(10, 0, 14, 0) : new Version(9, 0, 11, 0), reference.Version);
    }

    [Fact]
    public void TheSettingsPage_IsFirst_AndNotInTheMainMenu()
    {
        var plugin = (Plugin)PluginIdentityChecks.CreatePlugin(typeof(Plugin), _scratch.FullName);
        var pages = plugin.GetPages().ToList();

        Assert.Empty(PluginIdentityChecks.CheckPages(pages, Yaml.Name, typeof(Plugin).Assembly.GetManifestResourceNames()));
        var page = Assert.Single(pages);
        Assert.Equal(("UserRatings", "User Ratings", false), (page.Name, page.DisplayName, page.EnableInMainMenu));
        Assert.Equal("Jellyfin.Plugin.UserRatings.Configuration.configPage.html", page.EmbeddedResourcePath);
    }

    public void Dispose() => _scratch.Delete(recursive: true);
}
