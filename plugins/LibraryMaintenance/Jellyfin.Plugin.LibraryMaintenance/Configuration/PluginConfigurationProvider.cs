using System;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.LibraryMaintenance.Configuration;

/// <summary>
/// Reads configuration from the running <see cref="Plugin"/> instance, and relays its saves
/// (<see cref="Plugin.UpdateConfiguration"/>).
/// </summary>
internal sealed class PluginConfigurationProvider : IPluginConfigurationProvider, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationProvider"/> class.
    /// </summary>
    public PluginConfigurationProvider()
    {
        Plugin.ConfigurationSaved += OnSaved;
    }

    /// <inheritdoc />
    public event EventHandler<PluginConfigurationChangedEventArgs>? Changed;

    /// <inheritdoc />
    public PluginConfiguration GetConfiguration() => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public void Save(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var plugin = Plugin.Instance ?? throw PluginException.Unavailable("Library Maintenance isn't loaded, so its settings can't be saved. Restart the server and try again.");
        plugin.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Stops relaying the plugin's saves.
    /// </summary>
    public void Dispose() => Plugin.ConfigurationSaved -= OnSaved;

    private void OnSaved(object? sender, PluginConfigurationChangedEventArgs e) => Changed?.Invoke(this, e);
}
