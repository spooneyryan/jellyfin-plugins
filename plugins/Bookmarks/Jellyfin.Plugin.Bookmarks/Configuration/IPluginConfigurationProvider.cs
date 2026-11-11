namespace Jellyfin.Plugin.Bookmarks.Configuration;

/// <summary>
/// Gives services the current plugin configuration without depending on the static plugin instance.
/// </summary>
internal interface IPluginConfigurationProvider
{
    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    /// <returns>The configuration; defaults when the plugin instance isn't available.</returns>
    PluginConfiguration GetConfiguration();
}
