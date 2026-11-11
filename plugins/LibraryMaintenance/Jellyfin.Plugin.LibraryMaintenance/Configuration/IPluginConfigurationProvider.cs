using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Configuration;

/// <summary>
/// Gives services the current plugin configuration without depending on the static plugin instance.
/// </summary>
internal interface IPluginConfigurationProvider
{
    /// <summary>
    /// Raised after the settings are saved (the Dashboard's <c>POST /Plugins/{id}/Configuration</c>, or the Settings tab's
    /// <see cref="Save"/>), with the settings before and after; the movie index marks itself out of date when a setting it
    /// reads changed.
    /// </summary>
    event EventHandler<PluginConfigurationChangedEventArgs>? Changed;

    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    /// <returns>The configuration; defaults when the plugin instance isn't available.</returns>
    PluginConfiguration GetConfiguration();

    /// <summary>
    /// Saves the settings as Jellyfin does (the plugin's <c>UpdateConfiguration</c>: the XML file, then
    /// <see cref="Changed"/>), for the Settings tab and the settings import.
    /// </summary>
    /// <param name="configuration">The settings to save.</param>
    void Save(PluginConfiguration configuration);
}
