using System;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Jellyfin.Plugin.TagCloud.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TagCloud;

/// <summary>
/// Tag Cloud's identity for the shared kit.
/// </summary>
internal static class TagCloudKit
{
    /// <summary>
    /// The key of the Tag Cloud page (<c>#/jftc-cloud</c>, <c>/TagCloud/Pages/cloud</c>). Its full-screen view is
    /// the same page with <c>show=1</c> in the hash, so it has no nav link of its own.
    /// </summary>
    public const string CloudPage = "cloud";

    /// <summary>The page's label.</summary>
    public const string CloudPageTitle = "Tag Cloud";

    /// <summary>The Material icon of the page's nav link.</summary>
    public const string CloudPageIcon = "cloud";

    /// <summary>The suffix of the page's Plugin Pages Id, <c>Jellyfin.Plugin.TagCloud.CloudPage</c>.</summary>
    public const string CloudPageIdSuffix = "CloudPage";

    /// <summary>
    /// Gets the kit options. The tests check them with <see cref="KitOptions.Validate"/>.
    /// </summary>
    public static KitOptions Options { get; } = new()
    {
        PluginName = "Tag Cloud",
        RoutePrefix = "TagCloud",
        Prefix = "jftc",
        SchemaPrefix = "TagCloud",
        DataFolder = "tagcloud",
        PluginAssembly = typeof(TagCloudKit).Assembly,
        PluginId = Guid.Parse(Plugin.PluginId),

        // A deleted user's private clouds go and their shared ones stay as "Former user" (SavedCloudOwner); the
        // kit's KeepAsOrphan is that, its Delete would take the shared ones too.
        DeletedUserPolicy = _ => DeletedUserPolicy.KeepAsOrphan,

        // Read on every index.html request, so the setting applies without a restart.
        WebUiEnabled = services => Configuration(services).EnableWebUi,

        // The one user page, known for good: turned off it keeps its key, its shell answers 200 with the "turned
        // off" note and its route shows that note, with no nav link and no Plugin Pages registration.
        Pages =
        [
            new KitPage(CloudPage, CloudPageTitle, CloudPageIcon, CloudPageIdSuffix) { IsEnabled = services => Configuration(services).EnableTagCloudPage },
        ],
        RegisterPluginPages = services => Configuration(services).RegisterPluginPages,
        PageHostMode = services => Configuration(services).PageHostMode,
        ShowFallbackNav = services => Configuration(services).ShowFallbackNav,

        // Whether the ClientHealth endpoint records reports of missing jellyfin-web anchors.
        ReportWebAnchorProblems = services => Configuration(services).ReportWebAnchorProblems,

        // The F1 head fragment and page-host state of the page, on every index.html request.
        HeadFragment = context => FallbackRouteHeadFragment.Render(Options!, context.RequestServices)
    };

    private static PluginConfiguration Configuration(IServiceProvider services)
        => services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration();
}
