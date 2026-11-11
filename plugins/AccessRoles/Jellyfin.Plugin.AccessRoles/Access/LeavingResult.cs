using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// What a user's library access becomes when a change takes them out of library management: the
/// admin's choice and the access it gives.
/// </summary>
internal sealed record LeavingResult
{
    /// <summary>Gets the admin's <c>onUnmanage</c> choice, or <c>null</c> when the request has none (400 on a save).</summary>
    public AccessRolesUnmanageChoice? Choice { get; init; }

    /// <summary>Gets a value indicating whether the access afterwards is every library.</summary>
    public bool EnableAllFolders { get; init; }

    /// <summary>Gets the libraries afterwards when <see cref="EnableAllFolders"/> is off.</summary>
    public IReadOnlyList<Guid> EnabledFolders { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether <c>Restore</c> was chosen for a user without a ledger entry (409 on a save,
    /// as for <c>Unmanage</c>).
    /// </summary>
    public bool NoLedgerEntry { get; init; }

    /// <summary>
    /// Gets the names of the libraries the access stays frozen at with <c>Keep</c> ("library access stays frozen at:
    /// Kids"), by name; empty for the other choices.
    /// </summary>
    public IReadOnlyList<string> FrozenAt { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether <c>Restore</c> gives no libraries because the user was created under the default
    /// roles (their original access is none, <c>LedgerEntry.DefaultRolesUtc</c>).
    /// </summary>
    public bool CreatedUnderDefaultRoles { get; init; }
}
