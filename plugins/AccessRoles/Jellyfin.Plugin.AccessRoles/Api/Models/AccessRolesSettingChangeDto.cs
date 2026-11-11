namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// One setting a <c>PUT Settings</c> changes: its name and its value before and after, as text.
/// </summary>
public sealed class AccessRolesSettingChangeDto
{
    /// <summary>Gets or sets the setting's name (<c>DriftPolicy</c>, …).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the value before, as this build reads it (a list as its keys joined with ", ").</summary>
    public string Before { get; set; } = string.Empty;

    /// <summary>Gets or sets the value after.</summary>
    public string After { get; set; } = string.Empty;
}
