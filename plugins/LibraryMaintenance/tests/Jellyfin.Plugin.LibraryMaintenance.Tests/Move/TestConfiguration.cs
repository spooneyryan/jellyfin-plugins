using System;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// A settings provider over one <see cref="PluginConfiguration"/> that a test changes in place or replaces.
/// </summary>
internal sealed class TestConfiguration(PluginConfiguration configuration) : IPluginConfigurationProvider
{
    public event EventHandler<PluginConfigurationChangedEventArgs>? Changed;

    public PluginConfiguration Configuration { get; set; } = configuration;

    public PluginConfiguration GetConfiguration() => Configuration;

    public void Save(PluginConfiguration configuration)
    {
        var previous = Configuration;
        Configuration = configuration;
        Changed?.Invoke(this, new PluginConfigurationChangedEventArgs(previous, configuration));
    }
}
