using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.TagCloud.Configuration;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Jellyfin.Plugin.TagCloud.Tests.Web;

/// <summary>
/// The settings and their page: every setting on the page under its own name, kept by the server's
/// XML, with their documented defaults, and the page following the dashboard rules.
/// </summary>
public sealed partial class SettingsTests
{
    private static readonly PropertyInfo[] Settings = typeof(PluginConfiguration)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.DeclaringType == typeof(PluginConfiguration) && p.CanWrite)
        .ToArray();

    [Fact]
    public void EverySetting_IsOnTheSettingsPage()
    {
        var page = ReadPage();
        var form = FormPattern().Match(page);
        Assert.True(form.Success);
        var fields = FieldPattern().Matches(form.Groups["form"].Value).Select(m => m.Groups["id"].Value).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Settings.Select(p => p.Name).Order(StringComparer.Ordinal), fields.Order(StringComparer.Ordinal));
        Assert.All(Settings, p => Assert.Matches("id=\"" + p.Name + "\" name=\"" + p.Name + "\"", page));
    }

    [Fact]
    public void EverySetting_SurvivesTheServersXml_WithValuesThatArentItsDefaults()
    {
        var changed = new PluginConfiguration
        {
            EnableWebUi = false, RegisterPluginPages = false, PageHostMode = "Auto", ShowFallbackNav = false, ReportWebAnchorProblems = false,
            EnableTagCloudPage = false, EnableTags = false, EnableGenres = false, EnableStudios = false, EnableActors = false, EnableDirectors = false,
            EnableWriters = false, MaxWordsLimit = 500, TvMaxWordsDefault = 75, AllowSharedClouds = false, FullRebuildAfterChanges = 50, SnapshotsToKeep = 3
        };
        var defaults = new PluginConfiguration();
        Assert.All(Settings, p => Assert.False(Equals(p.GetValue(changed), p.GetValue(defaults)), p.Name + " is set to a value other than its default"));

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var buffer = new MemoryStream();
        serializer.Serialize(buffer, changed);
        buffer.Position = 0;
        var read = (PluginConfiguration)serializer.Deserialize(buffer)!;

        Assert.All(Settings, p => Assert.Equal(p.GetValue(changed), p.GetValue(read)));
    }

    [Fact]
    public void TheDefaults_AreThePlans()
    {
        var defaults = new PluginConfiguration();

        // The common settings and Tag Cloud's own.
        Assert.True(defaults.EnableWebUi);
        Assert.True(defaults.RegisterPluginPages);
        Assert.Equal("FallbackOnly", defaults.PageHostMode);
        Assert.True(defaults.ShowFallbackNav);
        Assert.True(defaults.ReportWebAnchorProblems);
        Assert.True(defaults.EnableTagCloudPage);
        Assert.True(defaults.EnableTags && defaults.EnableGenres && defaults.EnableStudios && defaults.EnableActors && defaults.EnableDirectors && defaults.EnableWriters);
        Assert.Equal(1000, defaults.MaxWordsLimit);
        Assert.Equal(150, defaults.TvMaxWordsDefault);
        Assert.True(defaults.AllowSharedClouds);
        Assert.Equal(500, defaults.FullRebuildAfterChanges);
        Assert.Equal(7, defaults.SnapshotsToKeep);
    }

    [Fact]
    public void TheSettingsPage_RendersServerValuesWithTextContentOnly()
    {
        // The kit's dashboard-module test, on every page of GetPages() (the settings page and its inline script).
        using var scratch = new TempDirectory();
        var plugin = (IHasWebPages)PluginIdentityChecks.CreatePlugin(typeof(Plugin), scratch.Path);

        Assert.Empty(DashboardPageChecks.Check(typeof(Plugin).Assembly, plugin.GetPages()));
    }

    private static string ReadPage()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.TagCloud.Configuration.configPage.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"<form id=""TagCloudConfigForm""[^>]*>(?<form>[\s\S]*?)</form>", RegexOptions.CultureInvariant)]
    private static partial Regex FormPattern();

    [GeneratedRegex(@"<(?:input|select)[^>]* id=""(?<id>[A-Za-z]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();
}
