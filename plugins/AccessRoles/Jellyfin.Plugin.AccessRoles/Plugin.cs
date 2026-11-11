using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Hosting;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AccessRoles;

/// <summary>
/// The Access Roles plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin id, shared with <c>build.yaml</c> and the plugin manifest.
    /// </summary>
    public const string PluginId = "7cbc1019-49a5-4b97-8aa2-279de946ef88";

    /// <summary>
    /// The dashboard page's name: page names are global across plugins, so it carries the plugin's.
    /// </summary>
    public const string PageName = "AccessRoles";

    /// <summary>
    /// The dashboard page's ES module page: the stub its <c>data-controller="__plugin/AccessRolesJs"</c> loads,
    /// which imports <c>/AccessRoles/AdminScript</c> and mounts it.
    /// </summary>
    public const string ScriptPageName = "AccessRolesJs";

    private readonly IServiceProvider? _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class with the server's services, as 10.11.5's
    /// <c>PluginManager.CreatePluginInstance</c> creates it (<c>ActivatorUtilities</c>), so <see cref="OnUninstalling"/> reaches
    /// the plugin's own.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="services">The server's services.</param>
    [ActivatorUtilitiesConstructor]
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IServiceProvider services)
        : this(applicationPaths, xmlSerializer)
    {
        _services = services;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Access Roles";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <inheritdoc />
    public override string Description => "Roles that keep non-admin users out of settings, the Dashboard's data and libraries they may not open.";

    /// <summary>
    /// Gets the dashboard pages: the one Access Roles page, first and the only one in the main menu, so both the plugin
    /// card's Settings button and the dashboard's menu open it, then since 0.9.8 its
    /// script stub, an ES module page that is never in a menu.
    /// </summary>
    /// <returns>The pages.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = PageName,
                DisplayName = Name,
                EnableInMainMenu = true,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            },
            new PluginPageInfo
            {
                Name = ScriptPageName,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.AccessRolesJs.js", GetType().Namespace)
            }
        ];
    }

    /// <summary>
    /// Called by 10.11.5's <c>InstallationManager.UninstallPlugin</c> before it removes the plugin: the optional
    /// mirror stops and puts back every preference access it turned off (<see cref="AccessRolesUninstall"/>). Never throws.
    /// </summary>
    public override void OnUninstalling()
    {
        base.OnUninstalling();
        AccessRolesUninstall.Run(_services, AccessRolesUninstall.Timeout);
    }
}
