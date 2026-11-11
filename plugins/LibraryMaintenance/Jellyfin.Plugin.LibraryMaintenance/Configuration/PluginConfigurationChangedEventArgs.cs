using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Configuration;

/// <summary>
/// The settings before and after a save (<see cref="IPluginConfigurationProvider.Changed"/>).
/// </summary>
internal sealed class PluginConfigurationChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationChangedEventArgs"/> class.
    /// </summary>
    /// <param name="previous">The settings before the save.</param>
    /// <param name="current">The settings saved.</param>
    public PluginConfigurationChangedEventArgs(PluginConfiguration previous, PluginConfiguration current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        Previous = previous;
        Current = current;
    }

    /// <summary>Gets the settings before the save.</summary>
    public PluginConfiguration Previous { get; }

    /// <summary>Gets the settings saved.</summary>
    public PluginConfiguration Current { get; }
}
