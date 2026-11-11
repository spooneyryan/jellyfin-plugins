using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The answer of every write of the roles API, and of its dry run: what was (or would be) saved, and the preview
/// of what it does per user. The roles API writes no user's policy itself: <c>PolicyWrite</c> in the preview says what the
/// library sync, which the saved change starts, will write.
/// </summary>
public sealed class AccessRolesChangeDto
{
    /// <summary>Gets or sets a value indicating whether this was a dry run: nothing was saved and no audit line written.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets the role as saved (role writes; <c>null</c> after a deletion).</summary>
    public AccessRolesRoleDto? Role { get; set; }

    /// <summary>Gets or sets the user's effective rules as saved (<c>PUT Users/{userId}</c> and <c>Unmanage</c>).</summary>
    public AccessRolesEffectiveDto? Effective { get; set; }

    /// <summary>Gets or sets the users whose assignment the write changed.</summary>
    public IReadOnlyList<Guid> Changed { get; set; } = [];

    /// <summary>Gets or sets the default roles as saved (<c>PUT DefaultRoles</c>).</summary>
    public IReadOnlyList<AccessRolesRoleRefDto>? DefaultRoles { get; set; }

    /// <summary>Gets or sets what the change does, per user it affects.</summary>
    public AccessRolesPreviewDto Preview { get; set; } = new();

    /// <summary>Gets or sets the numbers of the audit lines the write added.</summary>
    public IReadOnlyList<long> AuditSeq { get; set; } = [];

    /// <summary>
    /// Gets or sets what the admin should know about a saved write: that its audit lines couldn't be written (the change is
    /// saved all the same). Empty otherwise.
    /// </summary>
    public IReadOnlyList<string> Notes { get; set; } = [];
}
