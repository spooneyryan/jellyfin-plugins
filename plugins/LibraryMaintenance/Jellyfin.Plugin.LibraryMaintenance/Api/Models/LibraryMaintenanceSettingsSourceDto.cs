using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// Where an imported settings file came from: its envelope's plugin version, time and server.
/// </summary>
public sealed class LibraryMaintenanceSettingsSourceDto
{
    /// <summary>
    /// Gets or sets the version of Library Maintenance that exported it.
    /// </summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when it was exported.
    /// </summary>
    public DateTime ExportedUtc { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this server exported it; null when the file doesn't say.
    /// </summary>
    public bool? SameServer { get; set; }
}
