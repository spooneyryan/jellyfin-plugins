using System;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.UserRatings.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.UserRatings;

/// <summary>
/// User Ratings' identity for the shared kit.
/// </summary>
internal static class UserRatingsKit
{
    /// <summary>
    /// Gets the kit options. The tests check them with <see cref="KitOptions.Validate"/>.
    /// </summary>
    public static KitOptions Options { get; } = new()
    {
        PluginName = "User Ratings",
        RoutePrefix = "UserRatings",
        Prefix = "jfur",
        SchemaPrefix = "UserRatings",
        DataFolder = "userratings",
        PluginAssembly = typeof(UserRatingsKit).Assembly,
        PluginId = Guid.Parse(Plugin.PluginId),

        // What happens to a deleted user's ratings and reviews: Delete is the configured
        // default, but a value this version can't read is KeepAsOrphan, which deletes nothing (see
        // RatingsSettings). Read each time a user is deleted or an admin applies it.
        DeletedUserPolicy = services => RatingsSettings.From(Configuration(services)).DeletedUserPolicy,

        // Read on every index.html request, so the setting applies without a restart.
        WebUiEnabled = services => Configuration(services).EnableWebUi,

        // Whether the ClientHealth endpoint records reports of missing jellyfin-web anchors.
        ReportWebAnchorProblems = services => Configuration(services).ReportWebAnchorProblems,

        // The Jellyfin Enhanced interop: User Ratings replaces JE's User Reviews, so the Diagnostics entry
        // JellyfinEnhanced carries the overlap banner while JE's user reviews are on (ShowJellyfinEnhancedBanner, read at
        // each request) and the switch-over checklist of every JE review setting. The plugin only reads JE's files.
        JellyfinEnhanced = new JeInteropOptions
        {
            Overlap = JeFeature.Reviews,
            ShowBanner = services => Configuration(services).ShowJellyfinEnhancedBanner
        }

        // The Top Rated page (KitPage "toprated"), its head fragment and page settings come with 1.1.
    };

    private static PluginConfiguration Configuration(IServiceProvider services)
        => services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration();
}
