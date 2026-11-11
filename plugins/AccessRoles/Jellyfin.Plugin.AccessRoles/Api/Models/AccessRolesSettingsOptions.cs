namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The query options of <c>PUT /AccessRoles/Admin/Settings</c>: the dry run, its token and the confirmation of
/// a configuration key that carries secrets.
/// </summary>
public sealed class AccessRolesSettingsOptions
{
    /// <summary>Gets or sets a value indicating whether to check and show the change without saving it (no audit line).</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets the <c>PreviewToken</c> of the dry run the admin saw: a save that would change anything else saves
    /// nothing and answers 409 <c>Changed</c> with the settings that changed meanwhile. Left out, the save isn't checked
    /// against a dry run (an API caller's choice; the admin page always sends it).
    /// </summary>
    public string? IfPreview { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the admin confirms adding a configuration key whose type carries secrets to
    /// the allow-list; without it such a save is a 400 <c>SecretKeys</c>.
    /// </summary>
    public bool ConfirmSecretKeys { get; set; }
}
