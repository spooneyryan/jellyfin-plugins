using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// One user of <c>POST /AccessRoles/Admin/ReleaseLocks</c>'s answer: what of their native locks is
/// (or would be) put back, and whether they leave management.
/// </summary>
public sealed class AccessRolesReleasedUserDto
{
    /// <summary>Gets or sets the user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the native <c>EnableUserPreferenceAccess</c> the optional mirror turned off
    /// goes back on (it is still off now).
    /// </summary>
    public bool PreferenceAccess { get; set; }

    /// <summary>
    /// Gets or sets the original library access from the ledger that is written back (<c>restoreLibraries=true</c>), without
    /// deleted libraries and without one created again since; <c>null</c> when none is written.
    /// </summary>
    public AccessRolesNativeLibrariesDto? Libraries { get; set; }

    /// <summary>Gets or sets the libraries of the original access left out because they were created again since.</summary>
    public IReadOnlyList<Guid> RecreatedLeftOut { get; set; } = [];

    /// <summary>
    /// Gets or sets why the original library access isn't written although asked for: <c>NoLedgerEntry</c>,
    /// <c>AlreadyApplied</c>, <c>NoFullAdministrator</c> (it would leave no enabled administrator with every library; send
    /// <c>confirmNoFullAdmin=true</c>), or what stopped the write; <c>null</c> otherwise.
    /// </summary>
    public string? LibrariesSkipped { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user's assignment becomes <c>Excluded</c> (roles and ledger kept), so
    /// neither the library sync nor the mirror writes their locks again; set them <c>Managed</c> again to undo.
    /// </summary>
    public bool Excluded { get; set; }

    /// <summary>Gets or sets what the confirmation did for the preference flag, or <c>null</c> in a dry run.</summary>
    public string? PreferenceOutcome { get; set; }

    /// <summary>Gets or sets what the confirmation did for the library access, or <c>null</c> in a dry run or with none.</summary>
    public string? LibrariesOutcome { get; set; }
}
