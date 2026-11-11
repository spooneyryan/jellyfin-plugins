using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>Where a definition's collection is, and the collection when it may be written.</summary>
/// <param name="Kind">What was found.</param>
/// <param name="Box">The collection: for <see cref="CollectionLocationKind.Found"/> and <see cref="CollectionLocationKind.Rebound"/>,
/// and the unmarked one for <see cref="CollectionLocationKind.TetherMissing"/> (shown, never written).</param>
/// <param name="Duplicates">Other collections carrying the same mark (listed, never deleted).</param>
internal sealed record CollectionLocation(CollectionLocationKind Kind, BoxSet? Box, IReadOnlyList<Guid> Duplicates)
{
    /// <summary>Gets a value indicating whether the collection may be written.</summary>
    public bool Writable => Kind is CollectionLocationKind.Found or CollectionLocationKind.Rebound;
}
