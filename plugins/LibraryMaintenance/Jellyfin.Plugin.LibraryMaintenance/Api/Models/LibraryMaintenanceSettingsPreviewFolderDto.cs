namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// An excluded folder a settings change adds or removes.
/// </summary>
public sealed class LibraryMaintenanceSettingsPreviewFolderDto
{
    /// <summary>
    /// Gets or sets the folder, as written in the settings.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the change: <c>Added</c> or <c>Removed</c>.
    /// </summary>
    public string Change { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the folder exists on this server; null when it wasn't checked
    /// (<see cref="NotChecked"/> says why).
    /// </summary>
    public bool? Exists { get; set; }

    /// <summary>
    /// Gets or sets why the folder wasn't checked: <c>Network</c> (a UNC path or a drive mapped to a share, never opened
    /// before the settings are saved), <c>Device</c> (a device path such as <c>\\?\UNC\host\share</c> or
    /// <c>\??\UNC\host\share</c> already in the saved settings, never opened) or <c>NoAnswer</c> (no answer within 5 s);
    /// null when it was.
    /// </summary>
    public string? NotChecked { get; set; }
}
