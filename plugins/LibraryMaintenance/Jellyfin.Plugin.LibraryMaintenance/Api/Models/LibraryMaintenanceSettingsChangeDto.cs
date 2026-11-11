namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// One setting a save or an import changes, its values as text.
/// </summary>
public sealed class LibraryMaintenanceSettingsChangeDto
{
    /// <summary>
    /// Gets or sets the setting's name, as in <see cref="LibraryMaintenanceSettingsDto"/>.
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the setting's label on the Settings tab.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the saved value, as text.
    /// </summary>
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the new value, as text.
    /// </summary>
    public string To { get; set; } = string.Empty;
}
