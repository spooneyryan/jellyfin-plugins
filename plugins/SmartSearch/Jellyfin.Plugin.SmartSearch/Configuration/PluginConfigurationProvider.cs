namespace Jellyfin.Plugin.SmartSearch.Configuration;

/// <summary>
/// Reads configuration from the running <see cref="Plugin"/> instance.
/// </summary>
internal sealed class PluginConfigurationProvider : IPluginConfigurationProvider
{
    /// <inheritdoc />
    public PluginConfiguration GetConfiguration() => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
