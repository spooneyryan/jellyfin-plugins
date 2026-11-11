using System;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.UserRatings.Configuration;

/// <summary>
/// Reads configuration from the running <see cref="Plugin"/> instance, and saves it there
/// (<see cref="MediaBrowser.Common.Plugins.BasePlugin{TConfigurationType}.UpdateConfiguration"/>).
/// </summary>
internal sealed class PluginConfigurationProvider : IPluginConfigurationProvider
{
    /// <inheritdoc />
    public PluginConfiguration GetConfiguration() => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public void Save(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var plugin = Plugin.Instance ?? throw PluginException.Unavailable("User Ratings isn't loaded, so its settings can't be saved. Restart the server and try again.");
        plugin.UpdateConfiguration(configuration);
    }
}
