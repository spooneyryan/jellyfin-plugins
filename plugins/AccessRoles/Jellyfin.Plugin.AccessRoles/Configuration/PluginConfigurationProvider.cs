using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AccessRoles.Configuration;

/// <summary>
/// Reads configuration from the running <see cref="Plugin"/> instance.
/// </summary>
internal sealed class PluginConfigurationProvider : IPluginConfigurationProvider
{
    private readonly object _lock = new();
    private readonly Func<Plugin?> _instance;
    private EventHandler? _changed;
    private Plugin? _plugin;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationProvider"/> class, for the running plugin.
    /// </summary>
    public PluginConfigurationProvider()
        : this(() => Plugin.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationProvider"/> class.
    /// </summary>
    /// <param name="instance">Gives the plugin instance (the tests' own).</param>
    internal PluginConfigurationProvider(Func<Plugin?> instance)
    {
        _instance = instance;
    }

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add
        {
            lock (_lock)
            {
                // The plugin instance's ConfigurationChanged (a delegate property, as Jellyfin's MusicBrainz plugin uses it),
                // joined at the first subscription: the instance exists by then (the sync subscribes after the startup gate).
                if (_plugin is null && _instance() is { } plugin)
                {
                    _plugin = plugin;
                    plugin.ConfigurationChanged += OnPluginConfigurationChanged;
                }

                _changed += value;
            }
        }

        remove
        {
            lock (_lock)
            {
                _changed -= value;
            }
        }
    }

    /// <inheritdoc />
    public PluginConfiguration GetConfiguration() => _instance()?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public void Save(PluginConfiguration configuration)
    {
        System.ArgumentNullException.ThrowIfNull(configuration);
        var plugin = _instance() ?? throw new System.InvalidOperationException("Access Roles isn't loaded, so its settings can't be saved.");
        plugin.UpdateConfiguration(configuration);
    }

    private void OnPluginConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        EventHandler? changed;
        lock (_lock)
        {
            changed = _changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }
}
