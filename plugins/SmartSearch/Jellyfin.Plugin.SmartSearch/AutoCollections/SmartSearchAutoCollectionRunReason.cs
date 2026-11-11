namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>What started a run.</summary>
internal enum SmartSearchAutoCollectionRunReason
{
    /// <summary>A value this version doesn't know.</summary>
    Unknown,

    /// <summary>The end of a library scan.</summary>
    PostScan,

    /// <summary>Library changes outside a scan.</summary>
    Events,

    /// <summary>The daily task.</summary>
    Daily,

    /// <summary>A manager's Refresh now, or the scheduled task's Run.</summary>
    Manual,

    /// <summary>The definition or its query was saved.</summary>
    Saved,

    /// <summary>The pass after the server started.</summary>
    Startup,

    /// <summary>Items were added to or removed from the collection outside the plugin.</summary>
    ManualEdit,

    /// <summary>Pinned or excluded items were moved (the move journal).</summary>
    Moved,

    /// <summary>The evaluation user's watch state changed.</summary>
    WatchState
}
