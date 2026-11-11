using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests.KitTestSupport;

/// <summary>
/// Shows that the plugin identity checks catch the mistakes they exist for.
/// </summary>
public sealed class PluginIdentityChecksTests
{
    private const string SettingsResource = "Fake.Configuration.configPage.html";
    private const string ScriptResource = "Fake.Configuration.fake.js";

    private static readonly string[] Resources = [SettingsResource, ScriptResource];

    [Theory]
    [InlineData("eb5d7894-8eef-4b36-aa6f-5d124e828ce1")]
    [InlineData("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d")]
    [InlineData("a1b2c3d4-0000-4000-8000-000000000001")]
    [InlineData("12345678-1234-1234-1234-123456789abc")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void GetDenialReason_RejectsTemplateGuids(string guid)
    {
        Assert.NotNull(PluginIdentityChecks.GetDenialReason(Guid.Parse(guid)));
    }

    [Fact]
    public void GetDenialReason_AcceptsTheAssignedGuids()
    {
        Assert.All(PluginIdentityChecks.AssignedGuids.Values, guid => Assert.Null(PluginIdentityChecks.GetDenialReason(guid)));
    }

    [Theory]
    [InlineData("Bookmarks", "d0ee1b2a-7f71-4654-8ff2-163a81441b03", 0)]
    [InlineData("Bookmarks", "f53c7c34-8cba-4846-b43b-956816b2f6ae", 1)]
    [InlineData("Bookmarks", "D0EE1B2A-7F71-4654-8FF2-163A81441B03", 1)]
    [InlineData("Unlisted", "eb5d7894-8eef-4b36-aa6f-5d124e828ce1", 1)]
    [InlineData("Unlisted", "not-a-guid", 1)]
    [InlineData("Unlisted", "{3f2504e0-4f89-41d3-9a0c-0305e82c3301}", 1)]
    public void CheckGuid_FlagsWrongGuids(string name, string guid, int expectedProblems)
    {
        var yaml = BuildYaml.Parse($"name: \"{name}\"\nguid: \"{guid}\"\n");

        Assert.Equal(expectedProblems, PluginIdentityChecks.CheckGuid(yaml).Count);
    }

    [Theory]
    [InlineData("1.0.0.0", true)]
    [InlineData("0.2.13.0", true)]
    [InlineData("1.0.0.1", true)]
    [InlineData("1.1.0.2", true)]
    [InlineData("0.2.13.1", false)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0.0.0", false)]
    [InlineData("1.a.0.0", false)]
    [InlineData("1.0.0.-1", false)]
    [InlineData(" 1.0.0.0", false)]
    [InlineData("v1.0.0.0", false)]
    [InlineData("1.0.0.\u0661", false)]

    // A trailing newline and leading zeros once passed; 0 alone and 10 are fine.
    [InlineData("1.1.0.2\n", false)]
    [InlineData("1.1.0.2\r\n", false)]
    [InlineData("1.1.0.2\r", false)]
    [InlineData("01.1.0.2", false)]
    [InlineData("00.2.13.1", false)]
    [InlineData("00.2.13.0", false)]
    [InlineData("1.01.0.2", false)]
    [InlineData("1.1.00.2", false)]
    [InlineData("1.1.0.02", false)]
    [InlineData("1.1.0.00", false)]
    [InlineData("1.0.0.10", true)]
    [InlineData("0.10.0.0", true)]
    [InlineData("10.0.20.0", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("0.0.0.1", false)]
    [InlineData("99999999999.0.0.1", false)]
    public void CheckVersionFormat_FourNumericParts_DevelopmentBuildsOnlyAfter1_0(string version, bool valid)
    {
        Assert.Equal(valid, PluginIdentityChecks.CheckVersionFormat(version).Count == 0);
    }

    [Fact]
    public void CheckProjectVersions_RequiresAllThreeVersionsToMatch()
    {
        static XDocument Project(string properties) => XDocument.Parse($"<Project><PropertyGroup>{properties}</PropertyGroup></Project>");

        var complete = Project("<Version>1.0.0.0</Version><AssemblyVersion>1.0.0.0</AssemblyVersion><FileVersion>1.0.0.0</FileVersion>");
        var missing = Project("<Version>1.0.0.0</Version><AssemblyVersion>1.0.0.0</AssemblyVersion>");
        var different = Project("<Version>1.0.0.0</Version><AssemblyVersion>1.1.0.0</AssemblyVersion><FileVersion>1.0.0.0</FileVersion>");

        Assert.Empty(PluginIdentityChecks.CheckProjectVersions(complete, "1.0.0.0"));
        Assert.Single(PluginIdentityChecks.CheckProjectVersions(missing, "1.0.0.0"));
        Assert.Single(PluginIdentityChecks.CheckProjectVersions(different, "1.0.0.0"));
        Assert.Equal(3, PluginIdentityChecks.CheckProjectVersions(complete, "1.1.0.0").Count);
    }

    [Fact]
    public void CheckPages_AcceptsSettingsPageFirst()
    {
        var pages = new[] { Page("Fake", SettingsResource), Page("FakeJs", ScriptResource) };

        Assert.Empty(PluginIdentityChecks.CheckPages(pages, "Fake", Resources));
    }

    [Fact]
    public void CheckPages_AcceptsTheSettingsPageInTheMainMenu()
    {
        var pages = new[] { Page("Fake", SettingsResource, mainMenu: true), Page("FakeJs", ScriptResource) };

        Assert.Empty(PluginIdentityChecks.CheckPages(pages, "Fake", Resources));
    }

    [Fact]
    public void CheckPages_RejectsScriptPageFirst()
    {
        var pages = new[] { Page("FakeJs", ScriptResource), Page("Fake", SettingsResource) };

        var problem = Assert.Single(PluginIdentityChecks.CheckPages(pages, "Fake", Resources));
        Assert.Contains("first GetPages() entry must be the settings HTML page", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckPages_RejectsSeveralOrMisplacedMainMenuPages()
    {
        var several = new[] { Page("Fake", SettingsResource, mainMenu: true), Page("FakeJs", ScriptResource, mainMenu: true) };
        var misplaced = new[] { Page("Fake", SettingsResource), Page("FakeJs", ScriptResource, mainMenu: true) };

        Assert.Single(PluginIdentityChecks.CheckPages(several, "Fake", Resources));
        Assert.Single(PluginIdentityChecks.CheckPages(misplaced, "Fake", Resources));
    }

    [Fact]
    public void CheckPages_RejectsMissingResourcesUnprefixedAndDuplicateNames()
    {
        Assert.NotEmpty(PluginIdentityChecks.CheckPages([], "Fake", Resources));
        Assert.Single(PluginIdentityChecks.CheckPages([Page("Fake", "Fake.missing.html")], "Fake", Resources));
        Assert.Single(PluginIdentityChecks.CheckPages([Page("Other", SettingsResource)], "Fake", Resources));
        Assert.Single(PluginIdentityChecks.CheckPages([Page("Fake", SettingsResource), Page("fake", SettingsResource)], "Fake", Resources));
        Assert.Single(PluginIdentityChecks.CheckPages([Page("Fake", SettingsResource), Page("FakeCss", "Fake.x.css")], "Fake", [SettingsResource, "Fake.x.css"]));
    }

    [Fact]
    public void CheckPages_PrefixIsThePluginNameWithoutSpaces()
    {
        Assert.Empty(PluginIdentityChecks.CheckPages([Page("UserRatings", "UserRatings.x.html")], "User Ratings", ["UserRatings.x.html"]));
        Assert.Single(PluginIdentityChecks.CheckPages([Page("Ratings", "UserRatings.x.html")], "User Ratings", ["UserRatings.x.html"]));
    }

    [Fact]
    public void CheckSourceGuids_RequiresOnlyTheBuildYamlGuid()
    {
        var expected = PluginIdentityChecks.AssignedGuids["Bookmarks"];

        Assert.Empty(PluginIdentityChecks.CheckSourceGuids("const string PluginId = \"d0ee1b2a-7f71-4654-8ff2-163a81441b03\";", "Plugin.cs", expected));
        Assert.Single(PluginIdentityChecks.CheckSourceGuids("Guid Id => Guid.NewGuid();", "Plugin.cs", expected));
        Assert.Single(PluginIdentityChecks.CheckSourceGuids("new Guid(\"eb5d7894-8eef-4b36-aa6f-5d124e828ce1\")", "Plugin.cs", expected));
    }

    [Fact]
    public void CheckScriptManifest_ComparesGuidNameAndTargetAbi()
    {
        var yaml = BuildYaml.Parse("name: \"Bookmarks\"\nguid: \"d0ee1b2a-7f71-4654-8ff2-163a81441b03\"\ntargetAbi: \"10.11.5.0\"\n");
        const string Good = "$m = [ordered]@{\n    guid        = 'd0ee1b2a-7f71-4654-8ff2-163a81441b03'\n    name        = 'Bookmarks'\n    targetAbi   = '10.11.5.0'\n}";
        const string Bad = "$m = [ordered]@{\n    guid = 'eb5d7894-8eef-4b36-aa6f-5d124e828ce1'\n    name = 'Bookmark'\n    targetAbi = '10.11.0.0'\n}";

        Assert.Empty(PluginIdentityChecks.CheckScriptManifest(Good, "Deploy-Plugin.ps1", yaml));
        Assert.Equal(3, PluginIdentityChecks.CheckScriptManifest(Bad, "Deploy-Plugin.ps1", yaml).Count);
    }

    [Fact]
    public void FindRepositoryRoot_FindsTheSolutionFolder()
    {
        var root = PluginIdentityChecks.FindRepositoryRoot();

        Assert.True(File.Exists(Path.Combine(root, PluginIdentityChecks.SolutionFileName)));
        Assert.Contains("Bookmarks", PluginIdentityChecks.FindPluginFolders(root));
    }

    [Fact]
    public void FindRepositoryRoot_ThrowsOutsideTheRepository()
    {
        var scratch = Directory.CreateTempSubdirectory("jellyfin-plugins-root-");
        try
        {
            Assert.Throws<DirectoryNotFoundException>(() => PluginIdentityChecks.FindRepositoryRoot(scratch.FullName));
        }
        finally
        {
            scratch.Delete();
        }
    }

    [Fact]
    public void CreatePlugin_SuppliesConstructorServices()
    {
        var scratch = Directory.CreateTempSubdirectory("jellyfin-plugins-create-");
        try
        {
            var plugin = Assert.IsType<FakePlugin>(PluginIdentityChecks.CreatePlugin(typeof(FakePlugin), scratch.FullName));

            Assert.Equal(FakePlugin.PluginGuid, plugin.Id);
            Assert.Equal(scratch.FullName, plugin.PluginsPath);
            Assert.Empty(PluginIdentityChecks.CheckPages([.. plugin.GetPages()], plugin.Name, Resources));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckPluginBaseType_RequiresTheGenericBasePlugin()
    {
        Assert.Empty(PluginIdentityChecks.CheckPluginBaseType(typeof(FakePlugin)));
        Assert.Empty(PluginIdentityChecks.CheckPluginBaseType(typeof(DerivedFakePlugin)));

        var problem = Assert.Single(PluginIdentityChecks.CheckPluginBaseType(typeof(NonGenericFakePlugin)));
        Assert.Contains("BasePlugin<TConfiguration>", problem, StringComparison.Ordinal);
    }

    private static PluginPageInfo Page(string name, string resource, bool mainMenu = false)
        => new() { Name = name, EmbeddedResourcePath = resource, EnableInMainMenu = mainMenu };

    /// <summary>A plugin on the non-generic base, which Jellyfin 10.11.5 marks Malfunctioned (as an earlier run showed).</summary>
    public sealed class NonGenericFakePlugin : BasePlugin
    {
        public override string Name => "Fake";

        public override Guid Id => FakePlugin.PluginGuid;
    }

    /// <summary>A plugin two levels below BasePlugin&lt;T&gt;.</summary>
    public sealed class DerivedFakePlugin : FakeBasePlugin
    {
        public DerivedFakePlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
        }
    }

    /// <summary>An abstract plugin base of our own.</summary>
    public abstract class FakeBasePlugin : BasePlugin<BasePluginConfiguration>
    {
        protected FakeBasePlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
        }

        public override string Name => "Fake";

        public override Guid Id => FakePlugin.PluginGuid;
    }

    /// <summary>A plugin whose constructor takes more than the base class needs.</summary>
    public sealed class FakePlugin : BasePlugin<BasePluginConfiguration>, IHasWebPages
    {
        public static readonly Guid PluginGuid = new("3b1c7f0e-6a55-4d0f-9d7c-2f64c1e0a9b1");

        public FakePlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<FakePlugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            ArgumentNullException.ThrowIfNull(logger);
            PluginsPath = applicationPaths.PluginsPath;
        }

        public override string Name => "Fake";

        public override Guid Id => PluginGuid;

        public string PluginsPath { get; }

        public IEnumerable<PluginPageInfo> GetPages() => [Page("Fake", SettingsResource), Page("FakeJs", ScriptResource)];
    }
}
