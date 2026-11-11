namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// One setting a change changes, with its value before and after as text.
/// </summary>
public sealed class UserRatingsSettingsChangeDto
{
    /// <summary>Gets or sets the setting's name.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Gets or sets the setting's label on the Settings tab.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the saved value, as text.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Gets or sets the new value, as text.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the change takes effect only after the server restarts.</summary>
    public bool RestartNeeded { get; set; }
}
