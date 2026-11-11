using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// The pinned 'je' sandbox fixture (kit/testdata/je-sandbox): the files Initialize-Sandbox.ps1 writes
/// before the first start must hold exactly the values it reads back and asserts afterwards (expected.json).
/// </summary>
public sealed partial class JeSandboxFixtureTests
{
    private static readonly string FixtureRoot = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "kit", "testdata", "je-sandbox");

    private static readonly string ConfigurationsRoot = Path.Combine(FixtureRoot, "plugins", "configurations");

    [Fact]
    public void JellyfinEnhancedXml_HoldsEveryExpectedSetting()
    {
        var xml = XDocument.Load(Path.Combine(ConfigurationsRoot, "Jellyfin.Plugin.JellyfinEnhanced.xml")).Root!;
        var expected = LoadExpected().GetProperty("jellyfinEnhanced").GetProperty("settings");

        Assert.Equal("PluginConfiguration", xml.Name.LocalName);
        foreach (var setting in expected.EnumerateObject())
        {
            var element = xml.Element(setting.Name);
            Assert.True(element is not null, $"The JE fixture XML has no <{setting.Name}>.");
            Assert.Equal(setting.Value.GetBoolean(), bool.Parse(element!.Value));
        }
    }

    [Fact]
    public void JellyfinEnhancedXml_ShortcutsMatchExpected()
    {
        var xml = XDocument.Load(Path.Combine(ConfigurationsRoot, "Jellyfin.Plugin.JellyfinEnhanced.xml")).Root!;
        var shortcuts = xml.Element("Shortcuts")!.Elements("Shortcut")
            .ToDictionary(s => s.Element("Name")!.Value, s => s.Element("Key")!.Value, StringComparer.Ordinal);
        var expected = LoadExpected().GetProperty("jellyfinEnhanced").GetProperty("shortcuts").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), shortcuts.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal("B", shortcuts["BookmarkCurrentTime"]);
    }

    [Fact]
    public void PluginPagesConfig_ListsTheExpectedPages()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(ConfigurationsRoot, "Jellyfin.Plugin.PluginPages", "config.json")));
        var pages = config.RootElement.GetProperty("pages").EnumerateArray().ToList();
        foreach (var page in LoadExpected().GetProperty("pluginPages").GetProperty("pages").EnumerateArray())
        {
            var id = page.GetProperty("Id").GetString();
            var match = pages.SingleOrDefault(p => p.GetProperty("Id").GetString() == id);
            Assert.True(match.ValueKind == JsonValueKind.Object, $"config.json has no page {id}.");
            Assert.Equal(page.GetProperty("Url").GetString(), match.GetProperty("Url").GetString());
            Assert.Equal(page.GetProperty("DisplayText").GetString(), match.GetProperty("DisplayText").GetString());
        }
    }

    [Fact]
    public void CustomTabsXml_HoldsTheExpectedTabs()
    {
        var xml = XDocument.Load(Path.Combine(ConfigurationsRoot, "Jellyfin.Plugin.CustomTabs.xml")).Root!;
        var tabs = xml.Element("Tabs")!.Elements("TabConfig")
            .ToDictionary(t => t.Element("Title")!.Value, t => t.Element("ContentHtml")!.Value, StringComparer.Ordinal);
        foreach (var tab in LoadExpected().GetProperty("customTabs").GetProperty("tabs").EnumerateArray())
        {
            var title = tab.GetProperty("Title").GetString()!;
            Assert.True(tabs.ContainsKey(title), $"The Custom Tabs fixture has no '{title}' tab.");
            Assert.Equal(tab.GetProperty("ContentHtml").GetString(), tabs[title]);
        }
    }

    [Fact]
    public void Fixture_KeepsJellyfinEnhancedsOutboundFeaturesOff()
    {
        // scripts/README.md (safety rules): besides the tasks kept without triggers, a sandbox stays offline because JE's
        // usage statistics and Jellyseerr are off and it has no Seerr, MDBList, Sonarr or Radarr URL or API key, so
        // JE's other tasks that call out skip. Initialize-Sandbox reads the two settings back (expected.json).
        var settings = LoadExpected().GetProperty("jellyfinEnhanced").GetProperty("settings");
        Assert.False(settings.GetProperty("AnalyticsEnabled").GetBoolean());
        Assert.False(settings.GetProperty("JellyseerrEnabled").GetBoolean());

        var xml = XDocument.Load(Path.Combine(ConfigurationsRoot, "Jellyfin.Plugin.JellyfinEnhanced.xml")).Root!;
        Assert.Equal("false", xml.Element("AnalyticsEnabled")?.Value);
        Assert.Equal("false", xml.Element("JellyseerrEnabled")?.Value);
        Assert.DoesNotContain(xml.Descendants(), e => EndpointSetting().IsMatch(e.Name.LocalName));
    }

    [Fact]
    public void Fixture_HoldsNoUserIdsOrTokens()
    {
        // User ids and access tokens are 32 hex digits; a fixture holding one was copied from a real server.
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories))
        {
            if (HexId().IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetRelativePath(FixtureRoot, file));
            }
        }

        Assert.Empty(offenders);
    }

    private static JsonElement LoadExpected()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureRoot, "expected.json")));
        return document.RootElement.Clone();
    }

    [GeneratedRegex("(?<![0-9a-fA-F-])[0-9a-fA-F]{32}(?![0-9a-fA-F])")]
    private static partial Regex HexId();

    // JE 12.8's settings that point it at a service: JellyseerrApiKey, JellyseerrUrls, MdblistApiKey, SonarrUrl,
    // RadarrInstances and the like.
    [GeneratedRegex("(ApiKey|Urls?|UrlMappings|Instances)$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointSetting();
}
