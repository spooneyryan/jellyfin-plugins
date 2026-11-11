using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The outcome of <see cref="CollectionNameChecker.CheckAsync"/>.</summary>
/// <param name="Name">The name, trimmed.</param>
/// <param name="Error">Why the name isn't one at all (empty, too long, nothing left as a folder name), or <c>null</c>.</param>
/// <param name="Conflicts">What is in the way; empty when the name is free.</param>
/// <param name="Warnings">What would go wrong with a free name, in plain English.</param>
/// <param name="Suggestions">Free names close to this one, when it isn't free.</param>
/// <param name="FolderPath">The folder a new collection of this name gets.</param>
/// <param name="Rebind">The collection that already carries this definition's mark and has this name or folder: not a
/// conflict, the definition binds to it again.</param>
internal sealed record CollectionNameCheck(
    string Name,
    string? Error,
    IReadOnlyList<CollectionNameConflict> Conflicts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Suggestions,
    string? FolderPath,
    Guid? Rebind)
{
    /// <summary>Gets a value indicating whether a collection of this name can be created (or bound again).</summary>
    public bool Available => Error is null && Conflicts.Count == 0;

    /// <summary>
    /// The check as a caller may see it: a collection (or item) in the way keeps its id and name only when the caller can
    /// open it; otherwise the conflict says only that the name is taken. The folder's name (made from the name asked
    /// for), another auto-collection (every manager lists them) and the suggestions stay.
    /// </summary>
    /// <param name="canSee">Whether the caller can open an item.</param>
    /// <returns>The check for the caller.</returns>
    public CollectionNameCheck ForViewer(Func<Guid, bool> canSee)
    {
        ArgumentNullException.ThrowIfNull(canSee);
        return this with
        {
            Conflicts = Conflicts.Select(c => c.Kind is CollectionNameConflictKind.ExistingCollection or CollectionNameConflictKind.ItemIdExists
                && (c.Id is not { } id || !canSee(id))
                    ? c with { Id = null, Name = null }
                    : c).ToList()
        };
    }
}
