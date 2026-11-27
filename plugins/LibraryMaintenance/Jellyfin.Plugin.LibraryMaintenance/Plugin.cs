using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LibraryMaintenance;

/// <summary>
/// The Library Maintenance plugin: an admin-only Dashboard page that lists duplicate movies (1.0, read-only).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin id, shared with <c>build.yaml</c> and the plugin manifest.
    /// </summary>
    public const string PluginId = "fd47a1d3-dd8c-4e34-aeea-4f2edd973800";

    /// <summary>
    /// The name of the Dashboard page (<c>#/configurationpage?name=LibraryMaintenance</c>).
    /// </summary>
    public const string PageName = "LibraryMaintenance";

    /// <summary>
    /// The name of the page's <c>data-controller</c> module, <c>__plugin/LibraryMaintenanceJs</c>: the stub that imports the
    /// AdminScript module and mounts it.
    /// </summary>
    public const string ScriptPageName = "LibraryMaintenanceJs";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        if (KitDuplicateGuard.IsStandingDown)
        {
            // Another copy of the plugin was loaded too and runs instead: Jellyfin's settings serializer is shared by both
            // copies' settings types, so this copy never reads or writes the settings file (KitDuplicateGuard).
            Configuration = new PluginConfiguration();
        }
    }

    /// <summary>
    /// Raised after <see cref="UpdateConfiguration"/> saved the settings, with the settings before and after
    /// (<see cref="PluginConfigurationProvider"/> relays it to the services).
    /// </summary>
    internal static event EventHandler<PluginConfigurationChangedEventArgs>? ConfigurationSaved;

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Library Maintenance";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <inheritdoc />
    public override string Description => "Find the movies you have more than once, and preview moving media.";

    /// <inheritdoc />
    /// <remarks>Jellyfin's <c>ConfigurationChanged</c> gives only the new settings; the index needs the old ones too, to
    /// see whether a setting it reads changed.</remarks>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var previous = Configuration;
        base.UpdateConfiguration(configuration);
        ConfigurationSaved?.Invoke(this, new PluginConfigurationChangedEventArgs(previous, Configuration));
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        if (KitDuplicateGuard.IsStandingDown)
        {
            // The copy that runs shows its own pages.
            return [];
        }

        // The HTML page first (the kit's ordering rule): jellyfin-web opens the first page when none is in the main menu,
        // and the Settings button would otherwise open the script.
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
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.LibraryMaintenanceJs.js", GetType().Namespace)
            }
        ];
    }

    /// <summary>
    /// Saves the settings. A copy that stands down beside another copy of the plugin (<see cref="KitDuplicateGuard"/>)
    /// refuses: the shared settings serializer belongs to the copy that runs.
    /// </summary>
    /// <param name="config">The settings.</param>
    public override void SaveConfiguration(PluginConfiguration config)
    {
        KitDuplicateGuard.ThrowIfStandingDown(Name);
        base.SaveConfiguration(config);
    }
}
