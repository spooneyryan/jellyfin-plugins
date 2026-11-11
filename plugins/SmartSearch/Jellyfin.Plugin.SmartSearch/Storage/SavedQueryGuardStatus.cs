namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>How a guarded change ended.</summary>
internal enum SavedQueryGuardStatus
{
    /// <summary>Written.</summary>
    Done,

    /// <summary>References need a collection manager to change the query (403 <c>ManagerRequired</c>).</summary>
    ManagerRequired,

    /// <summary>The edit breaks references (409 <c>BreaksAutoCollections</c>).</summary>
    BreaksReferences,

    /// <summary>The edit changes how references are evaluated and waits for a confirmation (409 <c>AutoCollectionsNeedAttention</c>).</summary>
    NeedsAttention,

    /// <summary>The query is referenced and the deletion doesn't detach them (409 <c>Referenced</c>).</summary>
    Referenced,

    /// <summary>
    /// What references the queries couldn't be loaded, and the change could break a reference (409 <c>StoreReadOnly</c>).
    /// </summary>
    ReferencesUnknown
}
