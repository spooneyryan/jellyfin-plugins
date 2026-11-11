namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>Where a definition's collection is.</summary>
internal enum CollectionLocationKind
{
    /// <summary>The definition has no collection yet.</summary>
    NotCreated,

    /// <summary>Found by its stored id, carrying the definition's mark.</summary>
    Found,

    /// <summary>Found by the mark under another id (the stored id is to be replaced).</summary>
    Rebound,

    /// <summary>The stored id names a collection without the definition's mark: someone else's now; never written.</summary>
    TetherMissing,

    /// <summary>The collection is gone (deleted); never created again without the manager.</summary>
    Orphaned
}
