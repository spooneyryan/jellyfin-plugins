using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The answer of <c>PUT /AccessRoles/Admin/Settings</c>: what the change does (or, with <c>dryRun=true</c>,
/// would do), what it warns of, and the dry run's token for the save.
/// </summary>
public sealed class AccessRolesSettingsChangeDto
{
    /// <summary>Gets or sets a value indicating whether nothing was saved (a dry run).</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets the settings as they are after the change (or would be).</summary>
    public AccessRolesSettingsDto Settings { get; set; } = new();

    /// <summary>Gets or sets the settings the change changes, in the order of the Settings tab; empty when none.</summary>
    public IReadOnlyList<AccessRolesSettingChangeDto> Changes { get; set; } = [];

    /// <summary>
    /// Gets or sets the configuration keys whose type carries secrets the change adds to the allow-list (<c>network</c>,
    /// <c>livetv</c>, <c>database</c>, <c>subtitles</c>): their secrets are still stripped, but the save needs
    /// <c>confirmSecretKeys=true</c>.
    /// </summary>
    public IReadOnlyList<string> SecretKeysAdded { get; set; } = [];

    /// <summary>Gets or sets what the change means for non-admins and the library sync, in sentences.</summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];

    /// <summary>
    /// Gets or sets the token of what the change does (each changed setting, before and after): the save sends the dry
    /// run's as <c>ifPreview</c>, and one that would change anything else (a setting changed meanwhile) saves nothing (409
    /// <c>Changed</c>).
    /// </summary>
    public string PreviewToken { get; set; } = string.Empty;
}
