using System;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LibraryMaintenance;

/// <summary>
/// Library Maintenance's identity for the shared kit.
/// </summary>
internal static class LibraryMaintenanceKit
{
    /// <summary>
    /// Gets the kit options. The tests check them with <see cref="KitOptions.Validate"/>.
    /// </summary>
    /// <remarks>
    /// Since 1.0.2 the plugin has a ClientScript (Move's item-menu entry and dialog), added to <c>index.html</c> while
    /// <c>EnableWebUi</c> is on, and takes the browsers' anchor reports while <c>ReportWebAnchorProblems</c> is.
    /// It has no user pages and no store yet, so the options the kit reads for those (<c>Pages</c>,
    /// <c>DeletedUserPolicy</c>) keep the kit's defaults.
    /// </remarks>
    public static KitOptions Options { get; } = new()
    {
        PluginName = "Library Maintenance",
        RoutePrefix = "LibraryMaintenance",
        Prefix = "jflm",
        SchemaPrefix = "LibraryMaintenance",
        DataFolder = "librarymaintenance",
        PluginAssembly = typeof(LibraryMaintenanceKit).Assembly,
        PluginId = Guid.Parse(Plugin.PluginId),

        // Read on every index.html request, so the setting applies without a restart.
        WebUiEnabled = services => Configuration(services).EnableWebUi,

        // Whether the ClientHealth endpoint records reports of missing jellyfin-web anchors.
        ReportWebAnchorProblems = services => Configuration(services).ReportWebAnchorProblems,
    };

    private static PluginConfiguration Configuration(IServiceProvider services)
        => services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration();
}
