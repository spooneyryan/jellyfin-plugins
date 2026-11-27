using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Bookmarks;

/// <summary>
/// The Bookmarks plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin id, shared with <c>build.yaml</c> and the plugin manifest.
    /// </summary>
    public const string PluginId = "d0ee1b2a-7f71-4654-8ff2-163a81441b03";

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
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Bookmarks And Clips";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <inheritdoc />
    public override string Description => "Bookmark moments in movies and episodes, name them, and turn them into clips.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        if (KitDuplicateGuard.IsStandingDown)
        {
            // The copy that runs shows its own pages.
            return [];
        }

        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
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
