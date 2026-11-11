namespace Jellyfin.Plugin.UserRatings.Configuration;

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

    /// <summary>
    /// Saves a configuration in place of the current one (the Settings tab's save), as Jellyfin's own
    /// <c>BasePlugin&lt;T&gt;.UpdateConfiguration</c> does: the XML file written, the next <see cref="GetConfiguration"/> this
    /// one.
    /// </summary>
    /// <param name="configuration">The new configuration.</param>
    /// <exception cref="Kit.Api.PluginException">503 when the plugin instance isn't available.</exception>
    void Save(PluginConfiguration configuration);
}
