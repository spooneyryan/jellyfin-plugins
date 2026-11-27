using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.TagCloud.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TagCloud;

/// <summary>
/// The Tag Cloud plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin id, shared with <c>build.yaml</c> and the plugin manifest.
    /// </summary>
    public const string PluginId = "e36415cf-46f1-4de6-bd61-d1fe7175c7a9";

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
    public override string Name => "Tag Cloud";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <inheritdoc />
    public override string Description => "Shows your library's tags, genres, people and studios as a word cloud you can click through.";

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
                // Dashboard page names are global across plugins, and this one's starts with the plugin's name without
                // its space (configurationpage?name=TagCloud).
                Name = "TagCloud",
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
