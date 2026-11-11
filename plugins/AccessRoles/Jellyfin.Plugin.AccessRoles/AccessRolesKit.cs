using System;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AccessRoles;

/// <summary>
/// Access Roles' identity for the shared kit.
/// </summary>
internal static class AccessRolesKit
{
    /// <summary>
    /// Gets the kit options. The tests check them with <see cref="KitOptions.Validate"/>.
    /// </summary>
    public static KitOptions Options { get; } = new()
    {
        PluginName = "Access Roles",
        RoutePrefix = "AccessRoles",
        Prefix = "jfar",
        SchemaPrefix = "AccessRoles",
        DataFolder = "accessroles",
        PluginAssembly = typeof(AccessRolesKit).Assembly,
        PluginId = Guid.Parse(Plugin.PluginId),

        // Access Roles fixes DeletedUserPolicy to Delete and doesn't show it; a deleted user's assignment
        // and ledger entry go with the user (the plugin's user-data owner, AccessRolesUserDataOwner).
        DeletedUserPolicy = static _ => DeletedUserPolicy.Delete,

        // Read on every index.html request, so the setting applies without a restart. Access Roles
        // has no user pages.
        WebUiEnabled = services => Configuration(services).EnableWebUi,

        // The web guard's head fragment: the hide rules and the script that sets <html>'s classes from
        // the browser's cache before jellyfin-web's bundles run, with the server-wide flags (anyManaged among them). Rendered
        // per index.html request; it never throws.
        HeadFragment = context => Web.AccessRolesHead.Render(context.RequestServices),

        // Whether the ClientHealth endpoint records reports of missing jellyfin-web anchors.
        ReportWebAnchorProblems = services => Configuration(services).ReportWebAnchorProblems
    };

    private static PluginConfiguration Configuration(IServiceProvider services)
        => services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration();
}
