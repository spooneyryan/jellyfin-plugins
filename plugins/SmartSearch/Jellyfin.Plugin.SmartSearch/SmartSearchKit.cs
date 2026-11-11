using System;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Jellyfin.Plugin.SmartSearch.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SmartSearch;

/// <summary>
/// Smart Search's identity for the shared kit.
/// </summary>
internal static class SmartSearchKit
{
    /// <summary>
    /// The key of the Advanced Search page (<c>#/jfss-search</c>, <c>/SmartSearch/Pages/search</c>).
    /// </summary>
    public const string SearchPage = "search";

    /// <summary>The page's label.</summary>
    public const string SearchPageTitle = "Advanced Search";

    /// <summary>The Material icon of the page's nav link.</summary>
    public const string SearchPageIcon = "manage_search";

    /// <summary>The suffix of the page's Plugin Pages Id, <c>Jellyfin.Plugin.SmartSearch.SearchPage</c>.</summary>
    public const string SearchPageIdSuffix = "SearchPage";

    /// <summary>
    /// The attribute of the JSON block that tells the web client which entry points outside the nav to add,
    /// <c>data-jfss-entrypoints</c>.
    /// </summary>
    public const string EntryPointsAttribute = "data-jfss-entrypoints";

    /// <summary>
    /// Gets the kit options. The tests check them with <see cref="KitOptions.Validate"/>.
    /// </summary>
    public static KitOptions Options { get; } = new()
    {
        PluginName = "Smart Search",
        RoutePrefix = "SmartSearch",
        Prefix = "jfss",
        SchemaPrefix = "SmartSearch",
        DataFolder = "smartsearch",
        PluginAssembly = typeof(SmartSearchKit).Assembly,
        PluginId = Guid.Parse(Plugin.PluginId),

        // The setting, Delete by default: a deleted user's private queries go; the saved-query store keeps shared and
        // referenced ones as orphans itself.
        DeletedUserPolicy = services => DeletedUserPolicies.Parse(Configuration(services).DeletedUserPolicy, DeletedUserPolicy.Delete),

        // Read on every index.html request, so the setting applies without a restart.
        WebUiEnabled = services => Configuration(services).EnableWebUi,

        // The one user page, known for good: turned off (EnableSearchPage) it keeps its key, its shell answers
        // 200 with the "turned off" note and its route shows that note, with no nav link and no Plugin Pages registration.
        Pages =
        [
            new KitPage(SearchPage, SearchPageTitle, SearchPageIcon, SearchPageIdSuffix) { IsEnabled = services => Configuration(services).EnableSearchPage },
        ],
        RegisterPluginPages = services => Configuration(services).RegisterPluginPages,
        PageHostMode = services => Configuration(services).PageHostMode,
        ShowFallbackNav = services => Configuration(services).ShowFallbackNav,

        // Whether the ClientHealth endpoint records reports of missing jellyfin-web anchors.
        ReportWebAnchorProblems = services => Configuration(services).ReportWebAnchorProblems,

        // The head fragment and page-host state of the page, then the entry points' settings, on every index.html request.
        HeadFragment = context => FallbackRouteHeadFragment.Render(Options!, context.RequestServices) + EntryPointsFragment(context.RequestServices)
    };

    /// <summary>
    /// Renders the entry points' settings for one <c>index.html</c> request, after the page-host state:
    /// <c>{"headerButton":true,"collectionNote":true}</c> while <c>ShowHeaderButton</c> and <c>ShowCollectionNote</c> are
    /// on (the web client also asks whether the page is on). A setting that can't be read takes its default.
    /// </summary>
    /// <param name="services">The request's services.</param>
    /// <returns>The block.</returns>
    public static string EntryPointsFragment(IServiceProvider services)
    {
        bool headerButton;
        bool collectionNote;
        try
        {
            var configuration = Configuration(services);
            headerButton = configuration.ShowHeaderButton;
            collectionNote = configuration.ShowCollectionNote;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            headerButton = true;
            collectionNote = true;
        }

        return "<script type=\"application/json\" " + EntryPointsAttribute + ">{\"headerButton\":" + (headerButton ? "true" : "false")
            + ",\"collectionNote\":" + (collectionNote ? "true" : "false") + "}</script>";
    }

    private static PluginConfiguration Configuration(IServiceProvider services)
        => services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration();
}
