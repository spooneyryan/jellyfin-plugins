using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.UserRatings.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.UserRatings;

/// <summary>
/// The User Ratings plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin id, shared with <c>build.yaml</c> and the plugin manifest.
    /// </summary>
    public const string PluginId = "f53c7c34-8cba-4846-b43b-956816b2f6ae";

    /// <summary>
    /// The dashboard page's name: page names are global across plugins, so it carries the
    /// plugin's.
    /// </summary>
    public const string PageName = "UserRatings";

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
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "User Ratings";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <inheritdoc />
    public override string Description => "Five-star ratings with half stars and reviews, shared by every member of the server.";

    /// <summary>
    /// Gets the dashboard pages: the one User Ratings page, its settings page, first and not in the main menu, so the
    /// plugin card's Settings button opens it.
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
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
