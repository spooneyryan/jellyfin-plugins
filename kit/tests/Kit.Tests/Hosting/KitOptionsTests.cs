using System.IO;
using System.Linq;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Hosting;

public sealed class KitOptionsTests
{
    [Fact]
    public void Validate_AcceptsThePlannedPluginsIdentities()
    {
        // The naming rules: route, DOM prefix, DTO prefix and data folder of every planned plugin.
        (string Name, string Route, string Prefix, string Schema, string Folder)[] plugins =
        [
            ("Bookmarks", "Bookmarks", "jfbm", "Bookmark", "bookmarks"),
            ("User Ratings", "UserRatings", "jfur", "UserRatings", "userratings"),
            ("Smart Search", "SmartSearch", "jfss", "SmartSearch", "smartsearch"),
            ("Library Maintenance", "LibraryMaintenance", "jflm", "LibraryMaintenance", "librarymaintenance"),
            ("Access Roles", "AccessRoles", "jfar", "AccessRoles", "accessroles"),
            ("Tag Cloud", "TagCloud", "jftc", "TagCloud", "tagcloud")
        ];

        Assert.All(plugins, p => Assert.Empty(TestKit.Create(p.Name, p.Route, p.Prefix, p.Schema, p.Folder).Validate()));
    }

    [Theory]
    [InlineData("", "Fx", "jfkt", "Fx", "kittest", "Jellyfin.Plugin.KitTest")]
    [InlineData("Kit", "fx", "jfkt", "Fx", "kittest", "Jellyfin.Plugin.KitTest")]
    [InlineData("Kit", "Fx/Admin", "jfkt", "Fx", "kittest", "Jellyfin.Plugin.KitTest")]
    [InlineData("Kit", "Fx", "kt", "Fx", "kittest", "Jellyfin.Plugin.KitTest")]
    [InlineData("Kit", "Fx", "jfKT", "Fx", "kittest", "Jellyfin.Plugin.KitTest")]
    [InlineData("Kit", "Fx", "jfkt", "fx", "kittest", "Jellyfin.Plugin.KitTest")]
    [InlineData("Kit", "Fx", "jfkt", "Fx", "Kittest", "Jellyfin.Plugin.KitTest")]
    [InlineData("Kit", "Fx", "jfkt", "Fx", "../kittest", "Jellyfin.Plugin.KitTest")]
    [InlineData("Kit", "Fx", "jfkt", "Fx", "kit\\test", "Jellyfin.Plugin.KitTest")]
    [InlineData("Kit", "Fx", "jfkt", "Fx", "kittest", "KitTest")]
    public void Validate_ReportsEachBrokenRule(string name, string route, string prefix, string schema, string folder, string assembly)
    {
        Assert.Single(TestKit.Create(name, route, prefix, schema, folder, assembly).Validate());
    }

    [Fact]
    public void Validate_RequiresThePluginId_AndThePolicyDelegate()
    {
        // Export envelopes carry the id, and an import refuses another plugin's.
        var source = TestKit.Options;
        var options = new KitOptions
        {
            PluginName = source.PluginName,
            RoutePrefix = source.RoutePrefix,
            Prefix = source.Prefix,
            SchemaPrefix = source.SchemaPrefix,
            DataFolder = source.DataFolder,
            PluginAssembly = source.PluginAssembly,
            DeletedUserPolicy = null!
        };

        Assert.Equal(["PluginId is empty; set it to the plugin's id from build.yaml.", "DeletedUserPolicy has no delegate."], options.Validate());
        Assert.Equal(Kit.Users.DeletedUserPolicy.KeepAsOrphan, source.DeletedUserPolicy(new ServiceCollection().BuildServiceProvider()));
    }

    [Theory]
    [InlineData("Delete", "Delete")]
    [InlineData(" anonymize ", "Anonymize")]
    [InlineData("KeepAsOrphan", "KeepAsOrphan")]
    [InlineData("", "KeepAsOrphan")]
    [InlineData(null, "KeepAsOrphan")]
    [InlineData("Purge", "KeepAsOrphan")]
    [InlineData("1", "KeepAsOrphan")]
    public void DeletedUserPolicies_ReadTheSettingString(string? value, string expected)
    {
        Assert.Equal(expected, Kit.Users.DeletedUserPolicies.Parse(value).ToString());
        Assert.Equal(value is "Purge" or "1" or "" or null ? "Delete" : expected, Kit.Users.DeletedUserPolicies.Parse(value, Kit.Users.DeletedUserPolicy.Delete).ToString());
    }

    [Fact]
    public void LoggerCategory_NamesThePluginAndComponent()
    {
        var factory = new Mock<ILoggerFactory>();
        factory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(NullLogger.Instance);

        Assert.Equal("Jellyfin.Plugin.KitTest.Kit.AbiGuard", TestKit.Options.GetLoggerCategory("AbiGuard"));
        TestKit.Options.CreateLogger(factory.Object, "Api");
        factory.Verify(f => f.CreateLogger("Jellyfin.Plugin.KitTest.Kit.Api"), Times.Once);
    }

    [Fact]
    public void AddKit_RegistersOptionsAndPathsOnce()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(Path.Combine("C:", "jf", "data"));
        var services = new ServiceCollection().AddSingleton(paths.Object);

        services.AddKit(TestKit.Options).AddKit(TestKit.Options);

        Assert.Single(services, d => d.ServiceType == typeof(KitOptions));
        Assert.Single(services, d => d.ServiceType == typeof(KitPaths));
        using var provider = services.BuildServiceProvider();
        Assert.Same(TestKit.Options, provider.GetRequiredService<KitOptions>());
        Assert.Equal(Path.Combine("C:", "jf", "data", "kittest"), provider.GetRequiredService<KitPaths>().Root);
    }

    [Fact]
    public void KitPaths_IsTheDataFolderUnderDataPath()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(@"C:\ProgramData\Jellyfin\Server\data");

        var root = new KitPaths(paths.Object, TestKit.Create(dataFolder: "bookmarks")).Root;

        Assert.Equal(@"C:\ProgramData\Jellyfin\Server\data" + Path.DirectorySeparatorChar + "bookmarks", root);
        Assert.DoesNotContain(TestKit.Create(dataFolder: "bookmarks").Validate(), p => p.Contains("DataFolder", System.StringComparison.Ordinal));
    }
}
