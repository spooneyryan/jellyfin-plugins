using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// The outcome of <see cref="LibraryLifecycle.Reconcile"/>.
/// </summary>
/// <param name="Document">The document with the lifecycle applied (the input instance when nothing changed).</param>
/// <param name="Events">What happened, for the audit log.</param>
/// <param name="Waiting">Known libraries no longer listed that wait for a rename to resolve.</param>
/// <param name="Catalog">The catalog the sync resolves against: the listed libraries plus the waiting ones, without the
/// withheld ones.</param>
internal sealed record LifecycleResult(AccessRolesDocument Document, IReadOnlyList<LifecycleEvent> Events, IReadOnlySet<Guid> Waiting, LibraryCatalog Catalog)
{
    /// <summary>
    /// Gets the listed libraries a waiting rename waits for (its new name's, or the backstop's candidates with the same
    /// folders): left out of <see cref="Catalog"/> until the rename is re-pointed, so no rule grants them meanwhile.
    /// </summary>
    public IReadOnlySet<Guid> Withheld { get; init; } = new HashSet<Guid>();
}
