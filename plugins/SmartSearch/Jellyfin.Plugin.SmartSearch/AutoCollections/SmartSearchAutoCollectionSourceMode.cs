namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>Where a definition's query comes from.</summary>
internal enum SmartSearchAutoCollectionSourceMode
{
    /// <summary>A value this version doesn't know (written by a newer Smart Search).</summary>
    Unknown,

    /// <summary>One of the creator's own saved queries, by id: editing the query changes the collection.</summary>
    Reference,

    /// <summary>A copy of a query kept in the definition.</summary>
    Snapshot
}
