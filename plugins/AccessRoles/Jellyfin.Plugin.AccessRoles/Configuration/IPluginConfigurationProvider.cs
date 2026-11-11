using System;

namespace Jellyfin.Plugin.AccessRoles.Configuration;

/// <summary>
/// Gives services the current plugin configuration without depending on the static plugin instance.
/// </summary>
internal interface IPluginConfigurationProvider
{
    /// <summary>
    /// Occurs when the configuration was saved: the Settings tab's save, or Jellyfin's own plugin configuration endpoint
    /// (the plugin instance's <c>ConfigurationChanged</c>). The library sync re-reads its settings
    /// then.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    /// <returns>The configuration; defaults when the plugin instance isn't available.</returns>
    PluginConfiguration GetConfiguration();

    /// <summary>
    /// Saves a changed copy of the configuration as Jellyfin's plugin configuration endpoint does (the Settings tab):
    /// the plugin instance takes it, writes its XML file and raises <c>ConfigurationChanged</c>.
    /// </summary>
    /// <param name="configuration">The configuration to save, a copy (never the instance <see cref="GetConfiguration"/> answered, changed in place).</param>
    void Save(PluginConfiguration configuration);
}
