namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>What a requested run does.</summary>
internal enum AutoCollectionRequestKind
{
    /// <summary>Bring the collection in line with the query.</summary>
    Run,

    /// <summary>Create the collection (a definition whose collection is gone, or was never made).</summary>
    Create,

    /// <summary>Create the collection of a new definition; a refused create deletes the definition again.</summary>
    CreateNew
}
