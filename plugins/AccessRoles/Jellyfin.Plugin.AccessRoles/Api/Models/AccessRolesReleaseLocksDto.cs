using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The answer of <c>POST /AccessRoles/Admin/ReleaseLocks</c>: the users whose native locks are put
/// back, before the plugin is disabled or deleted.
/// </summary>
public sealed class AccessRolesReleaseLocksDto
{
    /// <summary>Gets or sets a value indicating whether this was a dry run.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets the dry run's token, which the confirmation sends back as <c>ifPreview</c>.</summary>
    public string? PreviewToken { get; set; }

    /// <summary>Gets or sets a value indicating whether the original library access is restored too.</summary>
    public bool RestoreLibraries { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the data file is read-only: the flags and library
    /// access are written, but nobody becomes <c>Excluded</c>, so once the file can be written again Access Roles writes
    /// their locks again unless they are released again.
    /// </summary>
    public bool StoreReadOnly { get; set; }

    /// <summary>Gets or sets the users released (or to release), by name.</summary>
    public IReadOnlyList<AccessRolesReleasedUserDto> Released { get; set; } = [];
}
