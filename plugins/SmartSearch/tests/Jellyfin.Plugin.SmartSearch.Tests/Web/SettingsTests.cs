using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.SmartSearch.Configuration;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Web;

/// <summary>
/// The settings and their page: every setting on the page under its own name, kept by the server's
/// XML, with the plan's defaults, and the page following the dashboard rules.
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
            EnableWebUi = false, RegisterPluginPages = false, PageHostMode = "Auto", ShowFallbackNav = false, ShowHeaderButton = false, ReportWebAnchorProblems = false,
            EnableSearchPage = false, MaxCandidates = 5_000, SearchTimeoutSeconds = 7, MaxConcurrentSearches = 3, ResultCacheSeconds = 30,
            MaxCachedResultIds = 3_000_000, AllowedItemKinds = [BaseItemKind.Episode, BaseItemKind.Movie], AllowQuerySharing = false,
            MaxSavedQueriesPerUser = 25, DeletedUserPolicy = "KeepAsOrphan", SnapshotsToKeep = 3,
            EnableAutoCollections = false, ShowCollectionNote = false, AutoCollectionDefaultImage = false, MaxMembersPerCollection = 300, MaxAutoCollections = 7, EventDebounceSeconds = 9, EventMaxWaitMinutes = 2,
            DefinitionCooldownSeconds = 0, StartupPassDelayMinutes = 0, GuardEnabled = false, GuardMaxRemovalPercent = 20, GuardMinRemovals = 1,
            GuardBlockEmpty = false, PassCacheMaxItems = 0, DailyPassScanWaitHours = 1, UseUserDataTriggers = false, CollectionWriter = "ManagerApi"
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

        // The settings every plugin has, then Smart Search's own.
        Assert.True(defaults.EnableWebUi);
        Assert.True(defaults.RegisterPluginPages);
        Assert.Equal("FallbackOnly", defaults.PageHostMode);
        Assert.True(defaults.ShowFallbackNav);
        Assert.True(defaults.ReportWebAnchorProblems);
        Assert.True(defaults.EnableSearchPage);
        Assert.True(defaults.ShowHeaderButton);
        Assert.True(defaults.ShowCollectionNote);
        Assert.True(defaults.AutoCollectionDefaultImage);
        Assert.Equal(100_000, defaults.MaxCandidates);
        Assert.Equal(20, defaults.SearchTimeoutSeconds);
        Assert.Equal(2, defaults.MaxConcurrentSearches);
        Assert.Equal(120, defaults.ResultCacheSeconds);
        Assert.Equal(2_000_000, defaults.MaxCachedResultIds);
        Assert.Equal(QueryLimits.ItemKindOrder, defaults.AllowedItemKinds);
        Assert.True(defaults.AllowQuerySharing);
        Assert.Equal(200, defaults.MaxSavedQueriesPerUser);
        Assert.Equal("Delete", defaults.DeletedUserPolicy);
        Assert.Equal(7, defaults.SnapshotsToKeep);
        Assert.Equal("Auto", defaults.CollectionWriter);
    }

    [Fact]
    public void ShowHeaderButton_ReachesTheWebClient_InTheHeadFragment()
    {
        Assert.Equal(
            "<script type=\"application/json\" data-jfss-entrypoints>{\"headerButton\":true,\"collectionNote\":true}</script>",
            SmartSearchKit.EntryPointsFragment(Services(new PluginConfiguration())));
        Assert.Equal(
            "<script type=\"application/json\" data-jfss-entrypoints>{\"headerButton\":false,\"collectionNote\":false}</script>",
            SmartSearchKit.EntryPointsFragment(Services(new PluginConfiguration { ShowHeaderButton = false, ShowCollectionNote = false })));

        // Without the configuration (a broken install), the defaults.
        Assert.Contains("{\"headerButton\":true,\"collectionNote\":true}", SmartSearchKit.EntryPointsFragment(new ServiceCollection().BuildServiceProvider()), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSavedQuerySettings_StayInTheirRanges_AndThePageOffersThePolicies()
    {
        Assert.Equal(1, SmartSearch.Services.SavedQuerySettings.From(new PluginConfiguration { MaxSavedQueriesPerUser = 0 }).MaxPerUser);
        Assert.Equal(10_000, SmartSearch.Services.SavedQuerySettings.From(new PluginConfiguration { MaxSavedQueriesPerUser = int.MaxValue }).MaxPerUser);
        Assert.Equal(Kit.Users.DeletedUserPolicy.Delete, SmartSearchKit.Options.DeletedUserPolicy(Services(new PluginConfiguration { DeletedUserPolicy = "Bogus" })));
        Assert.Equal(Kit.Users.DeletedUserPolicy.Anonymize, SmartSearchKit.Options.DeletedUserPolicy(Services(new PluginConfiguration { DeletedUserPolicy = "Anonymize" })));

        var page = ReadPage();
        Assert.Contains("id=\"MaxSavedQueriesPerUser\" name=\"MaxSavedQueriesPerUser\" type=\"number\" is=\"emby-input\" min=\"1\" max=\"10000\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"SnapshotsToKeep\" name=\"SnapshotsToKeep\" type=\"number\" is=\"emby-input\" min=\"0\" max=\"100\"", page, StringComparison.Ordinal);
        Assert.All(new[] { "Delete", "KeepAsOrphan", "Anonymize" }, policy => Assert.Contains("<option value=\"" + policy + "\">", page, StringComparison.Ordinal));
    }

    [Fact]
    public void TheAllowedItemKinds_AreTheSearchableOnesNamed_InTheirOrder_OrAllOfThem()
    {
        Assert.Equal([BaseItemKind.Movie, BaseItemKind.Episode], EngineSettings.AllowedItemKinds(new PluginConfiguration { AllowedItemKinds = [BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Movie] }));
        Assert.Equal(QueryLimits.ItemKindOrder, EngineSettings.AllowedItemKinds(new PluginConfiguration { AllowedItemKinds = [] }));
        Assert.Equal(QueryLimits.ItemKindOrder, EngineSettings.AllowedItemKinds(new PluginConfiguration { AllowedItemKinds = [BaseItemKind.Audio] }));
        Assert.Equal(QueryLimits.ItemKindOrder, EngineSettings.AllowedItemKinds(new PluginConfiguration { AllowedItemKinds = null! }));

        // The page offers each of them, several at once.
        var page = ReadPage();
        Assert.Contains("id=\"AllowedItemKinds\" name=\"AllowedItemKinds\"", page, StringComparison.Ordinal);
        Assert.All(QueryLimits.ItemKindOrder, kind => Assert.Contains("<option value=\"" + kind + "\">", page, StringComparison.Ordinal));
    }

    [Fact]
    public void TheEngineSettings_StayInTheirRanges()
    {
        var wild = new PluginConfiguration { MaxCandidates = -5, SearchTimeoutSeconds = 0, MaxConcurrentSearches = 99, ResultCacheSeconds = 1_000_000, MaxCachedResultIds = int.MaxValue };
        var settings = EngineSettings.From(wild);
        Assert.Equal(new EngineSettings(1_000, 1, 3_600, 20_000_000), settings);

        // A lifetime of 0 would end every result before its next page; a budget below the largest result couldn't keep it.
        var low = EngineSettings.From(new PluginConfiguration { ResultCacheSeconds = 0, MaxCachedResultIds = 0 });
        Assert.Equal(10, low.ResultCacheSeconds);
        Assert.Equal(1_000_000, low.MaxCachedResultIds);
        Assert.Equal(16, EngineSettings.SearchSlots(wild));
        Assert.Equal(new EngineSettings(), EngineSettings.From(new PluginConfiguration()));

        // The page's ranges are the server's.
        var page = ReadPage();
        Assert.Contains("id=\"MaxCandidates\" name=\"MaxCandidates\" type=\"number\" is=\"emby-input\" min=\"1000\" max=\"1000000\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"SearchTimeoutSeconds\" name=\"SearchTimeoutSeconds\" type=\"number\" is=\"emby-input\" min=\"1\" max=\"300\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"MaxConcurrentSearches\" name=\"MaxConcurrentSearches\" type=\"number\" is=\"emby-input\" min=\"1\" max=\"16\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"ResultCacheSeconds\" name=\"ResultCacheSeconds\" type=\"number\" is=\"emby-input\" min=\"10\" max=\"3600\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"MaxCachedResultIds\" name=\"MaxCachedResultIds\" type=\"number\" is=\"emby-input\" min=\"1000000\" max=\"20000000\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsPage_RendersServerValuesWithTextContentOnly()
    {
        // The kit's dashboard-module test, on every page of GetPages() (the settings page and its inline script).
        using var scratch = new TempDirectory();
        var plugin = (IHasWebPages)PluginIdentityChecks.CreatePlugin(typeof(Plugin), scratch.Path);

        Assert.Empty(DashboardPageChecks.Check(typeof(Plugin).Assembly, plugin.GetPages()));
    }

    // A PageHostMode the select has no option for read back as '' and was saved empty. The page keeps
    // the loaded value instead (a text check: the page's script is pinned, the server falls back on an unknown mode).
    [Fact]
    public void TheSettingsPage_KeepsASelectValueItHasNoOptionFor()
    {
        var page = ReadPage();
        Assert.Contains("selects.forEach(function (id) { if (field(id).value) { config[id] = field(id).value; } });", page, StringComparison.Ordinal);
        Assert.DoesNotContain("selects.forEach(function (id) { config[id] = field(id).value; });", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// configpage.test.mjs: the settings page built from its real markup in the kit's fake page, with its inline script:
    /// Export saved under the server's file name; Import's dry run with its counts, owners, notes and the auto-collections
    /// it would break, then Import with the same options, forced only when asked; the snapshots listed, a restore checked
    /// first and then run. Every server value as text.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TheSettingsPage_ExportsImportsAndRestores_InAFakePage()
    {
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");
        var tests = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "plugins", "SmartSearch", "tests", "Jellyfin.Plugin.SmartSearch.Tests", "Web", "configpage.test.mjs");

        var run = await NodeRunner.RunAsync(node!, ["--test", tests], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("# pass 6\n", run.StandardOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    private static ServiceProvider Services(PluginConfiguration configuration)
        => new ServiceCollection().AddSingleton<IPluginConfigurationProvider>(new FixedConfiguration(configuration)).BuildServiceProvider();

    private static string ReadPage()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.SmartSearch.Configuration.configPage.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"<form id=""SmartSearchConfigForm""[^>]*>(?<form>[\s\S]*?)</form>", RegexOptions.CultureInvariant)]
    private static partial Regex FormPattern();

    private sealed class FixedConfiguration(PluginConfiguration configuration) : IPluginConfigurationProvider
    {
        public PluginConfiguration GetConfiguration() => configuration;
    }

    [GeneratedRegex(@"<(?:input|select)[^>]* id=""(?<id>[A-Za-z]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();
}
