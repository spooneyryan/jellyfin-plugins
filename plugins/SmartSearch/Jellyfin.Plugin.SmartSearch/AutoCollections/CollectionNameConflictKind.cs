namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>Why a collection name can't be used.</summary>
internal enum CollectionNameConflictKind
{
    /// <summary>A collection of this name, or on this folder, exists (names compared ignoring case).</summary>
    ExistingCollection,

    /// <summary>The collection's folder exists on disk, with no collection on it.</summary>
    FolderExists,

    /// <summary>The id the new collection would get is taken by another item.</summary>
    ItemIdExists,

    /// <summary>Another auto-collection, not created yet, has this name.</summary>
    AutoCollection
}
