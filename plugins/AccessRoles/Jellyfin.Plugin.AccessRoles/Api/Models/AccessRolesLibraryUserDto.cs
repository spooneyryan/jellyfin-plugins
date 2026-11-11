using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user who can open a library, and why (the Libraries tab's "what each role and user gets"): who opens it is
/// the user's native access, the access the server enforces now; the reasons are the rules'
/// (<c>Role:Kid</c>, <c>UserAdd</c>, <c>AllLibraries</c>, …) for a managed user they give it to, an unmanaged user's their
/// native access (<c>Native</c>), and none for a managed user who opens it though the rules don't give it
/// (<see cref="Mismatch"/> says why). In a library's <c>NotOpenedBy</c>, a user the rules give it to who can't open it yet.
/// </summary>
public sealed class AccessRolesLibraryUserDto
{
    /// <summary>Gets or sets the user's id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user is an administrator.</summary>
    public bool IsAdministrator { get; set; }

    /// <summary>Gets or sets a value indicating whether the user's account is disabled.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Gets or sets the reasons, as Effective's <c>Libraries.Visible[].Sources</c>: empty when the rules don't give it.</summary>
    public IReadOnlyList<string> Sources { get; set; } = [];

    /// <summary>
    /// Gets or sets why the user's native access to the library isn't what the rules give, or <c>null</c> when it is:
    /// <c>NoLibraries</c> (the rules would leave the user no library, which the sync never writes by accident, so it skips
    /// them), <c>Drift</c> (changed outside Access Roles, kept by <c>DriftPolicy=ReportOnly</c> until a Revert), one of the
    /// library sync's pause reasons (<c>LibrarySyncDisabled</c>, <c>EnforcementDisabled</c>, <c>StoreRestored</c> for the
    /// hold after a restore, <c>SafeMode</c>, <c>StoreReadOnly</c>, <c>StoreDegraded</c>), or <c>Pending</c> (the next run
    /// writes it).
    /// </summary>
    public string? Mismatch { get; set; }
}
