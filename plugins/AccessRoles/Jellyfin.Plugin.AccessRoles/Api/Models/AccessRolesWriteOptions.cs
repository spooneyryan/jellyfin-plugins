using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The query options every write of the roles API takes: a dry run, the <c>onUnmanage</c> choice, the lockout
/// confirmation and the dry run's <c>PreviewToken</c>.
/// </summary>
public sealed class AccessRolesWriteOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to check and preview the change without saving it: the same answer, the
    /// same refusals, nothing written and no audit line.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets what the library access of users the change takes out of library management becomes:
    /// <c>Restore</c>, <c>Keep</c> or <c>GrantAll</c>. Without it such a change is a 400 <c>UnmanageChoiceRequired</c>.
    /// </summary>
    public AccessRolesUnmanageChoice? OnUnmanage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the admin confirms a change that leaves no enabled administrator with every
    /// library (<c>LockoutGuard</c>; 409 <c>NoFullAdministrator</c> without it).
    /// </summary>
    public bool ConfirmNoFullAdmin { get; set; }

    /// <summary>
    /// Gets or sets the <c>PreviewToken</c> of the dry run the admin saw: a save whose own change does
    /// anything else for anyone (a role edited since, a library gone, a user's access changed) writes nothing and answers
    /// 409 <c>Changed</c>, naming the users whose result differs when the dry run was this process's. Not checked for a dry
    /// run; a save without it (an older client) isn't checked either.
    /// </summary>
    public string? IfPreview { get; set; }
}
