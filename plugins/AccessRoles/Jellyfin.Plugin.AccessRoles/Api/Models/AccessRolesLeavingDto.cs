using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user's departure from library management (<c>LeavesManagement</c>).
/// </summary>
public sealed class AccessRolesLeavingDto
{
    /// <summary>Gets or sets the <c>onUnmanage</c> choice, or <c>null</c> when the request has none.</summary>
    public AccessRolesUnmanageChoice? Choice { get; set; }

    /// <summary>Gets or sets, for <c>Keep</c>, the libraries the access stays frozen at ("frozen at: Kids"), by name.</summary>
    public IReadOnlyList<string> FrozenAt { get; set; } = [];

    /// <summary>Gets or sets the library access the choice gives, or <c>null</c> without a choice.</summary>
    public AccessRolesNativeLibrariesDto? After { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <c>Restore</c> gives no libraries because the user was created under the
    /// default roles: their original access was the server's default for a new user, not an admin's, so it is recorded as
    /// none and <c>Restore</c> never widens it.
    /// </summary>
    public bool CreatedUnderDefaultRoles { get; set; }

    /// <summary>Gets or sets what the preview says about the choice ("restores to no libraries: created under default roles"), or <c>null</c>.</summary>
    public string? Note { get; set; }
}
