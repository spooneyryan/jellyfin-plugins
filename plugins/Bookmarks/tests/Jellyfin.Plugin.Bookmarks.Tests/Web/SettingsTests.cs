using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Web;

/// <summary>
/// The settings: every setting but the developer ones is on the settings page, each
/// survives the server's XML (the settings round trip on the server's side; <c>Web/configpage.test.mjs</c> has the page's),
/// and the defaults are the documented ones.
/// </summary>
public sealed partial class SettingsTests
{
    // The developer demos and clock: never on the page, never turned on on a real server.
    private static readonly string[] DeveloperOnly =
    [
        nameof(PluginConfiguration.EnablePageDemo), nameof(PluginConfiguration.EnableDemoPageB), nameof(PluginConfiguration.EnableIdentityDemo),
        nameof(PluginConfiguration.EnableUserDataDemo), nameof(PluginConfiguration.EnableLibraryChangeDemo), nameof(PluginConfiguration.EnableLibraryDemo),
        nameof(PluginConfiguration.DeveloperClockOffsetDays)
    ];

    private static readonly PropertyInfo[] Settings = typeof(PluginConfiguration)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.DeclaringType == typeof(PluginConfiguration) && p.CanWrite)
        .ToArray();

    [Fact]
    public void EverySetting_IsOnTheSettingsPage_ButTheDeveloperOnes()
    {
        var page = ReadPage();
        var form = FormPattern().Match(page);
        Assert.True(form.Success);
        var fields = FieldPattern().Matches(form.Groups["form"].Value).Select(m => m.Groups["id"].Value).ToHashSet(StringComparer.Ordinal);

        var expected = Settings.Select(p => p.Name).Except(DeveloperOnly).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, fields.Order(StringComparer.Ordinal));
        Assert.All(DeveloperOnly, name => Assert.DoesNotContain("id=\"" + name + "\"", page, StringComparison.Ordinal));

        // Each field is named after its setting (the label's for, the page script's id lookup).
        Assert.All(expected, name => Assert.Matches("id=\"" + name + "\" name=\"" + name + "\"", page));
    }

    [Fact]
    public void EverySetting_SurvivesTheServersXml_WithValuesThatArentItsDefaults()
    {
        // Jellyfin keeps plugin settings with System.Xml.Serialization (its MyXmlSerializer), so a property it can't write or
        // read back would be lost at every save.
        var changed = new PluginConfiguration
        {
            EnableWebUi = false, RegisterPluginPages = false, PageHostMode = "Auto", ShowFallbackNav = false, ReportWebAnchorProblems = false,
            EnablePageDemo = true, EnableDemoPageB = false, EnableIdentityDemo = true, EnableUserDataDemo = true, EnableLibraryChangeDemo = true,
            EnableLibraryDemo = true, DetachedRetentionDays = 120, SnapshotsToKeep = 14, DeveloperClockOffsetDays = 3, DeletedUserPolicy = "Delete",
            EnableBookmarksPage = false, EnableClipsPage = false, BookmarkNowShortcut = "Ctrl+Alt+K", ShowPlayerButtons = false, AskForNameDefault = true, SuggestChapterNames = false,
            ShowJellyfinEnhancedBanner = false, EnableClips = false, AdminsCanManageAllClips = false, ClipOutputPath = @"E:\Jellyfin Clips", OrganizeClipsByUser = false,
            DefaultClipMode = BookmarkClipMode.Fast, MaxClipDurationSeconds = 900, MaxConcurrentClips = 2, CleanUpPartialFilesOnStartup = false,
            MissingClipRetentionDays = 7, MinFreeSpaceMegabytes = 4096, VideoPreset = "slow", VideoCrf = 18, MaxVideoHeight = 1080, AudioBitrateKbps = 256,
            AudioChannels = 6
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

    [Theory]
    [InlineData(36_501, 36_500)]
    [InlineData(1_000_000, 36_500)]
    [InlineData(int.MaxValue, 36_500)]
    [InlineData(-5, 0)]
    [InlineData(36_500, 36_500)]
    [InlineData(0, 0)]
    public void MissingClipRetentionDays_IsStoredWithinTheFieldsRange_SetOrReadFromTheServersXml(int stored, int expected)
    {
        // Found in testing: the server kept a value above 36500 (only the page's field had max="36500"), and the page
        // loading it then failed the browser's own check when the admin saved anything else. The setting clamps it,
        // also when Jellyfin reads it from an older file or another client set it.
        Assert.Equal(expected, new PluginConfiguration { MissingClipRetentionDays = stored }.MissingClipRetentionDays);

        var xml = "<?xml version=\"1.0\"?><PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">"
            + "<MissingClipRetentionDays>" + stored.ToString(System.Globalization.CultureInfo.InvariantCulture) + "</MissingClipRetentionDays></PluginConfiguration>";
        using var reader = new StringReader(xml);
        var read = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;
        Assert.Equal(expected, read.MissingClipRetentionDays);
        Assert.Equal(36_500, MissingClipSweep.MaxRetentionDays);
    }

    [Fact]
    public void TheDefaults_AreTheDocumentedOnes()
    {
        var defaults = new PluginConfiguration();

        Assert.True(defaults.EnableWebUi);
        Assert.True(defaults.RegisterPluginPages);
        Assert.Equal("FallbackOnly", defaults.PageHostMode);
        Assert.True(defaults.ShowFallbackNav);
        Assert.True(defaults.ReportWebAnchorProblems);
        Assert.Equal(7, defaults.SnapshotsToKeep);
        Assert.Equal(0, defaults.DetachedRetentionDays);
        Assert.Equal("KeepAsOrphan", defaults.DeletedUserPolicy);
        Assert.True(defaults.ShowJellyfinEnhancedBanner);
        Assert.True(defaults.EnableBookmarksPage);
        Assert.True(defaults.EnableClipsPage);
        Assert.Equal("Shift+B", defaults.BookmarkNowShortcut);
        Assert.True(defaults.ShowPlayerButtons);
        Assert.False(defaults.AskForNameDefault);
        Assert.True(defaults.SuggestChapterNames);
        Assert.Equal(2048, defaults.MinFreeSpaceMegabytes);
        Assert.True(defaults.CleanUpPartialFilesOnStartup);
        Assert.Equal(30, defaults.MissingClipRetentionDays);
        Assert.True(defaults.AdminsCanManageAllClips);
        Assert.Equal(string.Empty, defaults.ClipOutputPath);
        Assert.True(defaults.OrganizeClipsByUser);
        Assert.False(defaults.EnablePageDemo || defaults.EnableIdentityDemo || defaults.EnableUserDataDemo || defaults.EnableLibraryChangeDemo || defaults.EnableLibraryDemo);
    }

    private static string ReadPage()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Bookmarks.Configuration.configPage.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"<form id=""BookmarksConfigForm""[^>]*>(?<form>[\s\S]*?)</form>", RegexOptions.CultureInvariant)]
    private static partial Regex FormPattern();

    [GeneratedRegex(@"<(?:input|select)[^>]* id=""(?<id>[A-Za-z]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();
}
